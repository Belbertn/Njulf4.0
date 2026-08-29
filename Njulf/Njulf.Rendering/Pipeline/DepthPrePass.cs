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
                throw new InvalidOperationException("DepthPrePass was scheduled with its required Forward+ depth contract disabled.");

            // A SceneRenderingData instance can be reused by tooling. Never let a prior frame's
            // completion marker satisfy a consumer if recording this prepass fails partway through.
            sceneData.DepthPrePassCompleted = false;
            sceneData.DepthPrePassFrameSerial = 0;

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
                    _meshPipeline.CompactedDepthPipeline,
                    Math.Min(sceneData.SceneSubmissionGpuDepthSolidCandidateCount, sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity),
                    BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetSolidDepthIndirectDispatchOffset(),
                    SceneOpaqueCompactionPass.GetSolidDepthDoubleSidedIndirectDispatchOffset(),
                    sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount);

                DrawSceneCompactedDepthList(
                    cmd,
                    sceneData,
                    _meshPipeline.MaskedDepthPipeline,
                    _meshPipeline.CompactedMaskedDepthPipeline,
                    Math.Min(sceneData.SceneSubmissionGpuDepthMaskedCandidateCount, sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity),
                    BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase,
                    SceneOpaqueCompactionPass.GetMaskedDepthIndirectDispatchOffset(),
                    SceneOpaqueCompactionPass.GetMaskedDepthDoubleSidedIndirectDispatchOffset(),
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
            sceneData.DepthPrePassFrameSerial = sceneData.DdgiFrameSerial;
            sceneData.DepthPrePassCompleted = true;
        }

        private bool CanUseSceneCompactedDepth(SceneRenderingData sceneData)
        {
            if (!_meshPipeline.TasklessSubmissionEnabled ||
                !sceneData.SceneSubmissionGpuCompactionActive ||
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
            if (!solidReady || !maskedReady)
                return false;

            if (!sceneData.SceneSubmissionSidedRasterSpecializationActive)
                return true;

            ulong requiredBytes = checked(
                SceneOpaqueCompactionPass
                    .GetMaskedDepthDoubleSidedIndirectDispatchOffset() +
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            return sceneData.SceneSubmissionIndirectMeshletDispatchEnabled &&
                   sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer.IsValid &&
                   sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize >=
                   requiredBytes;
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
            Silk.NET.Vulkan.Pipeline fallbackPipeline,
            Silk.NET.Vulkan.Pipeline compactedPipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            ulong doubleSidedIndirectDispatchOffset,
            int completedEmittedCount)
        {
            if (CanUseSceneIndirectDispatch(sceneData, indirectDispatchOffset))
            {
                DrawDepthListIndirect(
                    cmd,
                    sceneData,
                    compactedPipeline,
                    meshletCapacity,
                    meshletDrawBufferBaseIndex,
                    indirectDispatchOffset,
                    completedEmittedCount,
                    firstDraw: 0u,
                    oneSided: sceneData
                        .SceneSubmissionSidedRasterSpecializationActive);
                if (sceneData.SceneSubmissionSidedRasterSpecializationActive)
                {
                    DrawDepthListIndirect(
                        cmd,
                        sceneData,
                        compactedPipeline,
                        meshletCapacity,
                        meshletDrawBufferBaseIndex,
                        doubleSidedIndirectDispatchOffset,
                        completedEmittedCount,
                        firstDraw: checked((uint)meshletCapacity),
                        oneSided: false);
                }
                return;
            }

            DrawDepthList(
                cmd,
                sceneData,
                fallbackPipeline,
                meshletCapacity,
                meshletDrawBufferBaseIndex);
        }

        private void DrawDepthListIndirect(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectDispatchOffset,
            int completedEmittedCount,
            uint firstDraw,
            bool oneSided)
        {
            if (meshletCapacity <= 0 || _bufferManager == null)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            _context.Api.CmdSetCullMode(
                cmd,
                oneSided ? CullModeFlags.BackBit : CullModeFlags.None);
            _context.Api.CmdSetDepthCompareOp(cmd, CompareOp.GreaterOrEqual);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCapacity,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                FirstDraw = firstDraw
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            // The exact indirect count feeds a mesh-only compacted pipeline;
            // no pass-through task workgroups are launched on this path.
            sceneData.DepthMeshOnlyIndirectDrawCount++;
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
            if (_foliagePipeline == null || _bufferManager == null ||
                _foliageManager == null ||
                sceneData.FoliageClusterCount <= 0 ||
                sceneData.FoliageDrawBufferBytes == 0)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers(
                (int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.DepthPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)Math.Max(
                    0,
                    buffers.VisibleClusterCapacity)),
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

            VkBuffer indirect = _bufferManager.GetBuffer(
                buffers.IndirectDispatchBuffer);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                FoliageManager.ProceduralIndirectDispatchOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());

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
                    FoliageManager.AuthoredIndirectDispatchOffset,
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
