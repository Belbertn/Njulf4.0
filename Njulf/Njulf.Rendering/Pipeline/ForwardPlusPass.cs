using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Forward+ pass: renders all visible meshlets with per-tile lighting.
    /// Input: meshlet data, material data, textures, light index buffers
    /// Uses mesh shaders and bindless resource access.
    /// </summary>
    public sealed unsafe class ForwardPlusPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly BufferManager _bufferManager;
        private readonly GpuVisibilityBufferSet _visibilityBuffers;
        
        public ForwardPlusPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            BufferManager bufferManager,
            GpuVisibilityBufferSet visibilityBuffers)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
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

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle hizDepth = ProductionRenderGraphResources.HiZDepthPyramid(resources);
            RenderGraphResourceHandle aoBlurred = ProductionRenderGraphResources.AmbientOcclusionBlurred(resources, 0.5f);
            RenderGraphResourceHandle opaqueDraws = ProductionRenderGraphResources.OpaqueMeshletDrawBuffer(resources);
            RenderGraphResourceHandle lightBuffer = ProductionRenderGraphResources.LightBuffer(resources);
            RenderGraphResourceHandle tileHeaders = ProductionRenderGraphResources.TiledLightHeaderBuffer(resources);
            RenderGraphResourceHandle tileIndices = ProductionRenderGraphResources.TiledLightIndexBuffer(resources);
            RenderGraphResourceHandle skinnedVertices = ProductionRenderGraphResources.SkinnedVertexBuffer(resources);

            RenderGraphPassDesc pass = new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .After("TiledLightCullingPass")
                .After("PointShadowPass")
                .Write(
                    sceneColor,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store)
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentRead,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)
                .Read(
                    hizDepth,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.TaskShaderBitExt)
                .Read(
                    opaqueDraws,
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
                    PipelineStageFlags2.FragmentShaderBit);

            if (_settings.AmbientOcclusion.Enabled)
            {
                pass.Read(
                    aoBlurred,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.FragmentShaderBit);
            }

            resources.AddPass(pass);
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            if (!sceneData.GpuDrivenVisibilityEnabled)
                throw new InvalidOperationException("ForwardPlusPass requires GPU-driven visibility. The legacy direct mesh-task draw path is no longer supported by the production renderer.");

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ForwardPipeline);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);
            
            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(
                    sceneData.ClearColor.X,
                    sceneData.ClearColor.Y,
                    sceneData.ClearColor.Z,
                    sceneData.ClearColor.W)));
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
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };
            
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            
            // Push constants
            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.ViewMatrix.Invert(),
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)_visibilityBuffers.OpaqueCapacity,
                MeshletDrawBufferBaseIndex = BindlessIndex.MeshletDrawBufferBase,
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? 1u : 0u,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    weightedOitMode: false)
            };
            
            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                _bufferManager.GetBuffer(_visibilityBuffers.GetCounterBuffer((int)sceneData.CurrentFrameIndex)),
                _visibilityBuffers.GetIndirectCommandOffset(GpuVisibilityIndirectList.Opaque),
                1,
                (uint)Marshal.SizeOf<GPUMeshTaskIndirectCommand>());
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
        
        public override void OnSwapchainRecreated()
        {
        }
        
        public override void Cleanup()
        {
        }
    }
    
}
