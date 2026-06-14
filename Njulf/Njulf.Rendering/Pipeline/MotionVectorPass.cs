using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class MotionVectorPass : RenderPassBase
    {
        private readonly MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly BufferManager _bufferManager;
        private readonly GpuVisibilityBufferSet _visibilityBuffers;
        private Matrix4x4 _previousViewProjectionMatrix = Matrix4x4.Identity;
        private bool _hasPreviousViewProjectionMatrix;

        public MotionVectorPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            BufferManager bufferManager,
            GpuVisibilityBufferSet visibilityBuffers)
            : base("MotionVectorPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _visibilityBuffers = visibilityBuffers ?? throw new ArgumentNullException(nameof(visibilityBuffers));
        }

        public override void Initialize()
        {
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle motionVectors = ProductionRenderGraphResources.MotionVectors(resources);
            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle opaqueDraws = ProductionRenderGraphResources.OpaqueMeshletDrawBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                IsEnabled = _settings.AntiAliasing.EffectiveMode == AntiAliasingMode.Taa
            }
                .After("DepthPrePass")
                .Write(
                    motionVectors,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)))
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentRead,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)
                .Read(
                    opaqueDraws,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt));
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (_settings.AntiAliasing.EffectiveMode != AntiAliasingMode.Taa)
            {
                _hasPreviousViewProjectionMatrix = false;
                sceneData.MotionVectorsEnabled = 0;
                return;
            }

            long start = Stopwatch.GetTimestamp();
            bool previousFrameValid = _hasPreviousViewProjectionMatrix;
            Matrix4x4 previousViewProjection = previousFrameValid
                ? _previousViewProjectionMatrix
                : sceneData.ViewProjectionMatrix;

            Extent2D renderExtent = _renderTargets.MotionVectors.Extent;

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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.MotionVectorPipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);

            var pushConstants = new GPUMotionVectorPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                PreviousViewProjectionMatrix = previousViewProjection,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)(sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.OpaqueCapacity : sceneData.OpaqueMeshletCount),
                MeshletDrawBufferBaseIndex = BindlessIndex.MeshletDrawBufferBase,
                PreviousFrameValid = previousFrameValid ? 1u : 0u
            };

            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                &pushConstants);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.MotionVectors.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f))
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
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
            int drawCount = sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.OpaqueCapacity : sceneData.OpaqueMeshletCount;
            if (drawCount > 0)
            {
                if (sceneData.GpuDrivenVisibilityEnabled)
                {
                    _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                        cmd,
                        _bufferManager.GetBuffer(_visibilityBuffers.GetCounterBuffer((int)sceneData.CurrentFrameIndex)),
                        _visibilityBuffers.GetIndirectCommandOffset(GpuVisibilityIndirectList.Opaque),
                        1,
                        (uint)Marshal.SizeOf<GPUMeshTaskIndirectCommand>());
                }
                else
                {
                    _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)drawCount, 1, 1);
                }
            }
            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            _previousViewProjectionMatrix = sceneData.ViewProjectionMatrix;
            _hasPreviousViewProjectionMatrix = true;
            sceneData.MotionVectorsEnabled = previousFrameValid ? 1 : 0;
            sceneData.CpuMotionVectorRecordMicroseconds = ElapsedMicroseconds(start);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            _hasPreviousViewProjectionMatrix = false;
        }

        public override void Cleanup()
        {
        }

        private void TransitionDepthForRead(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilReadOnlyOptimal)
                return;

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageLayout oldLayout = _swapchain.DepthImageLayout;
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilReadOnlyOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }
    }
}
