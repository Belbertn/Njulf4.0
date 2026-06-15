using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class TransparentForwardPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly BufferManager _bufferManager;
        private readonly GpuVisibilityBufferSet _visibilityBuffers;

        public TransparentForwardPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            BufferManager bufferManager,
            GpuVisibilityBufferSet visibilityBuffers)
            : base("TransparentForwardPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
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

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle hizDepth = ProductionRenderGraphResources.HiZDepthPyramid(resources);
            RenderGraphResourceHandle oitAccumulation = ProductionRenderGraphResources.WeightedOitAccumulation(resources);
            RenderGraphResourceHandle oitRevealage = ProductionRenderGraphResources.WeightedOitRevealage(resources);
            RenderGraphResourceHandle transparentDraws = ProductionRenderGraphResources.TransparentMeshletDrawBuffer(resources);
            RenderGraphResourceHandle lightBuffer = ProductionRenderGraphResources.LightBuffer(resources);
            RenderGraphResourceHandle tileHeaders = ProductionRenderGraphResources.TiledLightHeaderBuffer(resources);
            RenderGraphResourceHandle tileIndices = ProductionRenderGraphResources.TiledLightIndexBuffer(resources);
            RenderGraphResourceHandle skinnedVertices = ProductionRenderGraphResources.SkinnedVertexBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .After("SkyboxPass")
                .ReadWrite(
                    sceneColor,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Load,
                    AttachmentStoreOp.Store)
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentRead,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)
                .Read(
                    hizDepth,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.TaskShaderBitExt)
                .Write(
                    oitAccumulation,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)))
                .Write(
                    oitRevealage,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(1.0f, 1.0f, 1.0f, 1.0f)))
                .Read(
                    transparentDraws,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt)
                .Read(
                    skinnedVertices,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt)
                .Read(
                    lightBuffer,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.FragmentShaderBit)
                .Read(
                    tileHeaders,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.FragmentShaderBit)
                .Read(
                    tileIndices,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.FragmentShaderBit));
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int drawCount = sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.TransparentCapacity : sceneData.TransparentMeshletCount;
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            bool weightedOit = sceneData.TransparencyMode == TransparencyMode.WeightedBlendedOit &&
                _meshPipeline.WeightedOitSupported;
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                weightedOit ? _meshPipeline.WeightedOitTransparentPipeline : _meshPipeline.TransparentForwardPipeline);
            bool hasGeometryDecals = sceneData.GeometryDecalObjectCount > 0;
            _context.Api.CmdSetDepthBias(
                cmd,
                hasGeometryDecals ? sceneData.GeometryDecalDepthBias : 0.0f,
                0.0f,
                hasGeometryDecals ? sceneData.GeometryDecalSlopeScaledDepthBias : 0.0f);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

            var colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = ColorAttachment(
                weightedOit ? _renderTargets.WeightedOitAccumulation.View : _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                weightedOit ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                AttachmentStoreOp.Store,
                weightedOit
                    ? new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f))
                    : default);
            colorAttachments[1] = ColorAttachment(
                _renderTargets.WeightedOitRevealage.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(1.0f, 1.0f, 1.0f, 1.0f)));

            var sortedAlphaColorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.DontCare);

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = weightedOit ? 2u : 1u,
                PColorAttachments = weightedOit ? colorAttachments : &sortedAlphaColorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.ViewMatrix.Invert(),
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)drawCount,
                MeshletDrawBufferBaseIndex = BindlessIndex.TransparentMeshletDrawBufferBase,
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = 0u,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    ambientOcclusionEnabled: false,
                    ambientOcclusionDebugView: (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: sceneData.TransparentReceiveShadows,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    weightedOitMode: weightedOit)
            };

            uint size = (uint)Marshal.SizeOf<GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (sceneData.GpuDrivenVisibilityEnabled)
            {
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    _bufferManager.GetBuffer(_visibilityBuffers.GetCounterBuffer((int)sceneData.CurrentFrameIndex)),
                    _visibilityBuffers.GetIndirectCommandOffset(GpuVisibilityIndirectList.Transparent),
                    1,
                    (uint)Marshal.SizeOf<GPUMeshTaskIndirectCommand>());
            }
            else
            {
                _context.ExtMeshShader.CmdDrawMeshTask(
                    cmd,
                    (uint)drawCount,
                    1,
                    1);
            }

            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return FramePassRuntimePolicy.ShouldExecute(Name, sceneData);
        }

        public override void OnSwapchainRecreated()
        {
        }

        public override void Cleanup()
        {
        }
    }
}
