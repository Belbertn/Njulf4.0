using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class ParticlePass : RenderPassBase
    {
        private const uint GpuBlendBucketCount = 5;

        private readonly PipelineObjects.ParticlePipeline _particlePipeline;
        private readonly BufferManager _bufferManager;
        private readonly RenderTargetManager _renderTargets;
        private readonly ParticleSettings _settings;

        private readonly ISimpleDdgiReceiverFeedbackCapture?
            _receiverFeedbackRuntime;

        public ParticlePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.ParticlePipeline particlePipeline,
            BufferManager bufferManager,
            RenderTargetManager renderTargets,
            ParticleSettings settings,
            ISimpleDdgiReceiverFeedbackCapture? receiverFeedbackRuntime = null)
            : base("ParticlePass", context, swapchain, bindlessHeap)
        {
            _particlePipeline = particlePipeline ?? throw new ArgumentNullException(nameof(particlePipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _receiverFeedbackRuntime = receiverFeedbackRuntime;
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            bool receiverFeedbackRequired =
                _receiverFeedbackRuntime?.IsPendingOwnedProducerRequired(
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.Particles) == true;
            bool useReceiverFeedback = receiverFeedbackRequired;
            if (useReceiverFeedback &&
                sceneData.CurrentFrameIndex != checked((uint)frameIndex))
            {
                AbortReceiverFeedback(
                    "receiver-feedback-particle-frame-index-mismatch");
                receiverFeedbackRequired = false;
                useReceiverFeedback = false;
            }

            if (useReceiverFeedback &&
                !_particlePipeline.ReceiverFeedbackPipelinesAvailable)
            {
                AbortReceiverFeedback(
                    "receiver-feedback-particle-pipelines-unavailable");
                receiverFeedbackRequired = false;
                useReceiverFeedback = false;
            }

            if (sceneData.GpuParticlesEnabled != 0)
            {
                bool drewParticles = ExecuteGpuParticles(
                    cmd,
                    frameIndex,
                    sceneData,
                    useReceiverFeedback);
                CompleteOrAbortReceiverFeedback(
                    cmd,
                    frameIndex,
                    receiverFeedbackRequired,
                    useReceiverFeedback,
                    drewParticles);
                return;
            }

            if (!sceneData.ParticlesEnabled || sceneData.RenderedParticleCount <= 0 ||
                sceneData.ParticleBatches.Count == 0)
            {
                if (receiverFeedbackRequired)
                {
                    AbortReceiverFeedback(
                        "receiver-feedback-particle-producer-required-without-draws");
                }

                return;
            }

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = renderExtent.Width,
                Height = renderExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = renderExtent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _particlePipeline.Layout, 0, 1,
                &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _particlePipeline.Layout, 1, 1,
                &textureSet, 0, null);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneColor.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            ParticleBlendMode currentBlendMode = (ParticleBlendMode)uint.MaxValue;
            bool drewAnyParticles = false;
            for (int i = 0; i < sceneData.ParticleBatches.Count; i++)
            {
                GPUParticleBatch batch = sceneData.ParticleBatches[i];
                if (batch.Count == 0)
                    continue;

                var blendMode = (ParticleBlendMode)batch.BlendMode;
                if (blendMode != currentBlendMode)
                {
                    currentBlendMode = blendMode;
                    _context.Api.CmdBindPipeline(
                        cmd,
                        PipelineBindPoint.Graphics,
                        _particlePipeline.GetPipeline(
                            blendMode,
                            useReceiverFeedback));
                }

                var pushConstants = new GPUParticlePushConstants
                {
                    CurrentFrameIndex = (uint)frameIndex,
                    ParticleInstanceBufferBaseIndex = BindlessIndex.ParticleInstanceBufferBase,
                    ParticleFrameDataBufferBaseIndex = BindlessIndex.ParticleFrameDataBufferBase,
                    DepthTextureIndex = BindlessIndex.DepthTexture,
                    DebugView = (uint)sceneData.ParticleDebugView,
                    SoftParticlesEnabled = _settings.SoftParticlesEnabled ? 1u : 0u,
                    InstanceOffset = batch.Start
                };

                _context.Api.CmdPushConstants(
                    cmd,
                    _particlePipeline.Layout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0,
                    (uint)Marshal.SizeOf<GPUParticlePushConstants>(),
                    &pushConstants);

                _context.Api.CmdDraw(cmd, 6, batch.Count, 0, 0);
                drewAnyParticles = true;
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            CompleteOrAbortReceiverFeedback(
                cmd,
                frameIndex,
                receiverFeedbackRequired,
                useReceiverFeedback,
                drewAnyParticles);
        }

        private bool ExecuteGpuParticles(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData,
            bool useReceiverFeedback)
        {
            if (sceneData.GpuParticleEmitterCount <= 0 || !sceneData.GpuParticleIndirectDrawBuffer.IsValid)
                return false;

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = renderExtent.Width,
                Height = renderExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = renderExtent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _particlePipeline.Layout, 0, 1,
                &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _particlePipeline.Layout, 1, 1,
                &textureSet, 0, null);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneColor.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            VkBuffer indirectBuffer = _bufferManager.GetBuffer(sceneData.GpuParticleIndirectDrawBuffer);
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            for (uint bucket = 0; bucket < GpuBlendBucketCount; bucket++)
            {
                var blendMode = (ParticleBlendMode)bucket;
                _context.Api.CmdBindPipeline(
                    cmd,
                    PipelineBindPoint.Graphics,
                    _particlePipeline.GetPipeline(
                        blendMode,
                        useReceiverFeedback));

                var pushConstants = new GPUParticlePushConstants
                {
                    CurrentFrameIndex = (uint)frameIndex,
                    ParticleInstanceBufferBaseIndex = BindlessIndex.GpuParticleRenderInstanceBufferBase,
                    ParticleFrameDataBufferBaseIndex = BindlessIndex.ParticleFrameDataBufferBase,
                    DepthTextureIndex = BindlessIndex.DepthTexture,
                    DebugView = (uint)sceneData.ParticleDebugView,
                    SoftParticlesEnabled = _settings.SoftParticlesEnabled ? 1u : 0u,
                    InstanceOffset = 0
                };

                _context.Api.CmdPushConstants(
                    cmd,
                    _particlePipeline.Layout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0,
                    (uint)Marshal.SizeOf<GPUParticlePushConstants>(),
                    &pushConstants);

                ulong indirectOffset = bucket * (ulong)Marshal.SizeOf<GPUParticleDrawCommand>();
                _context.Api.CmdDrawIndirect(cmd, indirectBuffer, indirectOffset, 1,
                    (uint)Marshal.SizeOf<GPUParticleDrawCommand>());
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            return true;
        }

        private void CompleteOrAbortReceiverFeedback(
            CommandBuffer commandBuffer,
            int frameIndex,
            bool receiverFeedbackRequired,
            bool usedReceiverFeedback,
            bool drewParticles)
        {
            if (!receiverFeedbackRequired)
                return;
            if (!usedReceiverFeedback || !drewParticles)
            {
                AbortReceiverFeedback(
                    "receiver-feedback-particle-producer-did-not-record-exact-draws");
                return;
            }

            if (_receiverFeedbackRuntime is null)
                return;
            if (!_receiverFeedbackRuntime.TryRecordOwnedProducerCompletion(
                    commandBuffer,
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.Particles,
                    out string reason))
            {
                AbortReceiverFeedback(
                    "receiver-feedback-particle-completion-failed:" + reason);
            }
        }

        private void AbortReceiverFeedback(string reason)
        {
            _receiverFeedbackRuntime?.AbortCapture(reason);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            _particlePipeline.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
        }
    }
}