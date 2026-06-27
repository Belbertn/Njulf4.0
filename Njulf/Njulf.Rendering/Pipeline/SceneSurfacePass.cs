using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Writes stable opaque scene-surface data for screen-space and probe-based GI.
    /// </summary>
    public sealed unsafe class SceneSurfacePass : RenderPassBase
    {
        private readonly MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly BufferManager? _bufferManager;
        private readonly RenderSettings _settings;

        public SceneSurfacePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            BufferManager? bufferManager = null)
            : base("SceneSurfacePass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _bufferManager = bufferManager;
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.DepthPrePassEnabled &&
                   _settings.GlobalIllumination.EffectiveUseSsgi;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            Extent2D renderExtent = _renderTargets.SceneNormal.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.SceneNormal.TransitionToColorAttachment(cmd);
            _renderTargets.SceneMaterial.TransitionToColorAttachment(cmd);

            var colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = ColorAttachment(
                _renderTargets.SceneNormal.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            colorAttachments[1] = ColorAttachment(
                _renderTargets.SceneMaterial.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(1.0f, 0.0f, 0.0f, 0.0f)));

            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.DontCare,
                new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)));

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 2,
                PColorAttachments = colorAttachments,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            DrawOpaqueSurfaceBuckets(cmd, sceneData);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            _renderTargets.SceneNormal.TransitionToShaderRead(cmd);
            _renderTargets.SceneMaterial.TransitionToShaderRead(cmd);
        }

        private void DrawOpaqueSurfaceBuckets(CommandBuffer cmd, SceneRenderingData sceneData)
        {
            if (sceneData.SceneSubmissionGpuCompactionActive &&
                sceneData.SceneSubmissionGpuOpaqueCandidateCount > 0 &&
                sceneData.SceneSubmissionGpuCompactedOpaqueCapacity > 0 &&
                sceneData.SceneSubmissionFallbackReason.Length == 0)
            {
                int compactedDrawCapacity = Math.Min(
                    sceneData.SceneSubmissionGpuOpaqueCandidateCount,
                    sceneData.SceneSubmissionGpuCompactedOpaqueCapacity);
                if (sceneData.SceneSubmissionIndirectMeshletDispatchEnabled)
                {
                    string skipReason = BuildIndirectDispatchSkipReason(sceneData);
                    if (skipReason.Length == 0)
                    {
                        DrawSurfaceBucketIndirect(
                            cmd,
                            sceneData,
                            _meshPipeline.SceneSurfaceCompactedPipeline,
                            BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase);
                        return;
                    }
                }

                DrawSurfaceBucket(
                    cmd,
                    sceneData,
                    _meshPipeline.SceneSurfaceCompactedPipeline,
                    compactedDrawCapacity,
                    BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase);
                return;
            }

            DrawSurfaceBucket(
                cmd,
                sceneData,
                _meshPipeline.SceneSurfaceSimplePipeline,
                sceneData.SimpleOpaqueMeshletCount,
                BindlessIndex.MeshletDrawBufferBase);
            DrawSurfaceBucket(
                cmd,
                sceneData,
                _meshPipeline.SceneSurfacePipeline,
                sceneData.SimpleNormalOpaqueMeshletCount,
                BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
            DrawSurfaceBucket(
                cmd,
                sceneData,
                _meshPipeline.SceneSurfacePipeline,
                sceneData.FullOpaqueMeshletCount,
                BindlessIndex.FullOpaqueMeshletDrawBufferBase);
        }

        private void DrawSurfaceBucket(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = BuildPushConstants(sceneData, checked((uint)meshletCount), checked((uint)meshletDrawBufferBaseIndex));
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUForwardPushConstants>(),
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTask(cmd, checked((uint)meshletCount), 1, 1);
        }

        private void DrawSurfaceBucketIndirect(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletDrawBufferBaseIndex)
        {
            if (_bufferManager == null)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = BuildPushConstants(
                sceneData,
                checked((uint)sceneData.SceneSubmissionGpuCompactedOpaqueCapacity),
                checked((uint)meshletDrawBufferBaseIndex));
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUForwardPushConstants>(),
                &pushConstants);

            VkBuffer indirect = _bufferManager.GetBuffer(sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                0,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private static GPUForwardPushConstants BuildPushConstants(
            SceneRenderingData sceneData,
            uint meshletCount,
            uint meshletDrawBufferBaseIndex)
        {
            return new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = meshletCount,
                MeshletDrawBufferBaseIndex = meshletDrawBufferBaseIndex,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias
            };
        }

        private string BuildIndirectDispatchSkipReason(SceneRenderingData sceneData)
        {
            if (_bufferManager == null)
                return "scene opaque indirect dispatch buffer unavailable";

            return SceneSubmissionDiagnosticsPolicy.BuildIndirectDispatchSkipReason(
                sceneData,
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }
    }
}
