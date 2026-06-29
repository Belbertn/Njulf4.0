using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Depth prepass: renders all visible meshlets to create a hi-Z depth buffer.
    /// Uses mesh shaders with reverse-Z (depth cleared to 0.0, greater comparison).
    /// </summary>
    public sealed unsafe class DepthPrePass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly PipelineObjects.FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly RenderTargetManager _renderTargets;
        
        public DepthPrePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            PipelineObjects.FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null)
            : base("DepthPrePass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }
        
        public override void Initialize()
        {
            // Pipeline is already created
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            if (!sceneData.DepthPrePassEnabled)
                return;

            _renderTargets.SceneDepth.TransitionToDepthAttachment(cmd);
            var renderExtent = new Extent2D { Width = sceneData.ScreenWidth, Height = sceneData.ScreenHeight };

            // Set viewport and scissor
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
            
            // Bind descriptor sets
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
            
            // Begin rendering
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = default, // No color attachment for depth prepass
                ImageLayout = ImageLayout.Undefined,
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };
            
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            
            if (CanUseSceneCompactedDepth(sceneData))
            {
                DrawSceneCompactedDepthList(
                    cmd,
                    sceneData,
                    _meshPipeline.DepthPipeline,
                    Math.Min(sceneData.SceneSubmissionGpuDepthSolidCandidateCount, sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity),
                    BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetSolidDepthIndirectDispatchOffset(),
                    sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount);

                DrawSceneCompactedDepthList(
                    cmd,
                    sceneData,
                    _meshPipeline.MaskedDepthPipeline,
                    Math.Min(sceneData.SceneSubmissionGpuDepthMaskedCandidateCount, sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity),
                    BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetMaskedDepthIndirectDispatchOffset(),
                    sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount);
            }
            else
            {
                DrawDepthList(
                    cmd,
                    sceneData,
                    _meshPipeline.DepthPipeline,
                    sceneData.SolidMeshletCount,
                    BindlessIndex.SolidDepthMeshletDrawBufferBase);

                DrawDepthList(
                    cmd,
                    sceneData,
                    _meshPipeline.MaskedDepthPipeline,
                    sceneData.MaskedMeshletCount,
                    BindlessIndex.MaskedDepthMeshletDrawBufferBase);
            }

            DrawFoliageDepth(cmd, sceneData);
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private static bool CanUseSceneCompactedDepth(SceneRenderingData sceneData)
        {
            if (!sceneData.SceneSubmissionGpuCompactionActive ||
                sceneData.SceneSubmissionFallbackReason.Length != 0)
                return false;

            bool hasSolidDepthCandidates = sceneData.SceneSubmissionGpuDepthSolidCandidateCount > 0;
            bool hasMaskedDepthCandidates = sceneData.SceneSubmissionGpuDepthMaskedCandidateCount > 0;
            if (!hasSolidDepthCandidates && !hasMaskedDepthCandidates)
                return false;

            bool solidReady = !hasSolidDepthCandidates ||
                              (sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBuffer.IsValid &&
                               sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity > 0);
            bool maskedReady = !hasMaskedDepthCandidates ||
                               (sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer.IsValid &&
                                sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity > 0);
            return solidReady && maskedReady;
        }

        private void DrawDepthList(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            sceneData.DepthTaskInvocations += meshletCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
        }

        private void DrawSceneCompactedDepthList(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            int completedEmittedCount)
        {
            if (CanUseSceneIndirectDispatch(sceneData, indirectDispatchOffset))
            {
                DrawDepthListIndirect(
                    cmd,
                    sceneData,
                    pipeline,
                    meshletCapacity,
                    meshletDrawBufferBaseIndex,
                    indirectDispatchOffset,
                    completedEmittedCount);
                return;
            }

            DrawDepthList(cmd, sceneData, pipeline, meshletCapacity, meshletDrawBufferBaseIndex);
        }

        private void DrawDepthListIndirect(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            int completedEmittedCount)
        {
            if (meshletCapacity <= 0 || _bufferManager == null)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCapacity,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            sceneData.DepthTaskInvocations += Math.Max(0, completedEmittedCount);
            VkBuffer indirect = _bufferManager.GetBuffer(sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                indirectDispatchOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private bool CanUseSceneIndirectDispatch(SceneRenderingData sceneData, ulong indirectDispatchOffset)
        {
            if (_bufferManager == null ||
                !sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ||
                !sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer.IsValid)
            {
                return false;
            }

            ulong requiredBytes = checked(indirectDispatchOffset + (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            return sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize >= requiredBytes;
        }

        private void DrawFoliageDepth(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || sceneData.FoliageClusterCount <= 0 || sceneData.FoliageDrawBufferBytes == 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.DepthPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)sceneData.FoliageClusterCount),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = 1u,
                DebugView = sceneData.FoliageDebugView,
                ShadowDensityScale = 1.0f
            };

            _context.Api.CmdPushConstants(
                cmd,
                _foliagePipeline.GraphicsLayout,
                ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                &pushConstants);

            sceneData.DepthTaskInvocations += sceneData.FoliageClusterCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)sceneData.FoliageClusterCount, 1, 1);

            DrawAuthoredFoliageDepth(cmd, sceneData);
        }

        private void DrawAuthoredFoliageDepth(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || _bufferManager == null || _foliageManager == null || sceneData.FoliageDrawBufferBytes == 0)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid || buffers.MeshletDrawCapacity <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.AuthoredDepthPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = 1u,
                DebugView = sceneData.FoliageDebugView,
                ShadowDensityScale = 1.0f
            };

            _context.Api.CmdPushConstants(
                cmd,
                _foliagePipeline.GraphicsLayout,
                ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                &pushConstants);

            if (sceneData.FoliageIndirectMeshletDispatchEnabled)
            {
                VkBuffer indirect = _bufferManager.GetBuffer(buffers.IndirectDispatchBuffer);
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    indirect,
                    0,
                    1,
                    (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
                return;
            }

            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)buffers.MeshletDrawCapacity, 1, 1);
        }

        private void BindFoliageDescriptorSets(CommandBuffer cmd)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _foliagePipeline!.GraphicsLayout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _foliagePipeline.GraphicsLayout,
                1,
                1,
                &textureSet,
                0,
                null);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void TransitionDepthForWrite(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilAttachmentOptimal)
                return;

            var depthRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageLayout oldLayout = _swapchain.DepthImageLayout;
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilAttachmentOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                oldLayout,
                ImageLayout.DepthStencilAttachmentOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                depthRange);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }
        
        public override void OnSwapchainRecreated()
        {
            // Depth pass doesn't have swapchain-dependent resources
        }
        
        public override void Cleanup()
        {
        }
    }
}
