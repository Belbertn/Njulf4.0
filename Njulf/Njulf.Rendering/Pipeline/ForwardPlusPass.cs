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
    /// Forward+ pass: renders all visible meshlets with per-tile lighting.
    /// Input: meshlet data, material data, textures, light index buffers
    /// Uses mesh shaders and bindless resource access.
    /// </summary>
    public sealed unsafe class ForwardPlusPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly PipelineObjects.FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly RenderTargetManager _renderTargets;
        
        public ForwardPlusPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            PipelineObjects.FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }
        
        public override void Initialize()
        {
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);
            
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            if (sceneData.DepthPrePassEnabled)
                _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            else
                _renderTargets.SceneDepth.TransitionToDepthAttachment(cmd);
            
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
                sceneData.DepthPrePassEnabled ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.DepthStencilAttachmentOptimal,
                sceneData.DepthPrePassEnabled ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear,
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
            
            sceneData.ForwardTaskInvocations = 0;
            sceneData.ForwardSimpleMeshletCount = 0;
            sceneData.ForwardFullMaterialMeshletCount = 0;
            sceneData.ForwardLocalProbeMeshletCount = 0;
            sceneData.ForwardShadowReceiverMeshletCount = 0;
            sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData);
            sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull;
            sceneData.SceneSubmissionIndirectDispatchSkipReason =
                sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                    ? "GPU compaction inactive"
                    : "indirect dispatch disabled";
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
                    string indirectSkipReason = BuildSceneOpaqueIndirectDispatchSkipReason(sceneData);
                    sceneData.SceneSubmissionIndirectDispatchSkipReason = indirectSkipReason;
                    if (indirectSkipReason.Length == 0)
                    {
                        sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect;
                        sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedEmit;
                        UpdateCompactedForwardVariantDiagnostics(sceneData, compactedDrawCapacity);
                        UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                        DrawForwardBucketIndirect(
                            cmd,
                            sceneData,
                            _meshPipeline.ForwardCompactedPipeline,
                            compactedDrawCapacity,
                            BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase);
                    }
                    else
                    {
                        sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                        sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
                        UpdateCompactedForwardVariantDiagnostics(sceneData, compactedDrawCapacity);
                        UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                        DrawForwardBucket(
                            cmd,
                            sceneData,
                            _meshPipeline.ForwardFullMaterialPipeline,
                            compactedDrawCapacity,
                            BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase);
                    }
                }
                else
                {
                    sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                    sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
                    UpdateCompactedForwardVariantDiagnostics(sceneData, compactedDrawCapacity);
                    UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                    DrawForwardBucket(
                        cmd,
                        sceneData,
                        _meshPipeline.ForwardFullMaterialPipeline,
                        compactedDrawCapacity,
                        BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase);
                }
            }
            else
            {
                sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData);
                ForwardOpaqueVariantSelection variantSelection = ResolveOpaqueVariantSelection(sceneData);
                sceneData.ForwardSimpleMeshletCount = variantSelection.SimpleMeshletCount;
                sceneData.ForwardFullMaterialMeshletCount = variantSelection.FullMaterialMeshletCount;
                sceneData.ForwardLocalProbeMeshletCount = variantSelection.LocalProbeMeshletCount;
                sceneData.ForwardShadowReceiverMeshletCount = ResolveForwardShadowReceiverMeshletCount(sceneData);

                DrawForwardBucket(
                    cmd,
                    sceneData,
                    variantSelection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    sceneData.SimpleOpaqueMeshletCount,
                    BindlessIndex.MeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    variantSelection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleFullInputGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    sceneData.SimpleNormalOpaqueMeshletCount,
                    BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    _meshPipeline.ForwardFullMaterialPipeline,
                    sceneData.FullOpaqueMeshletCount,
                    BindlessIndex.FullOpaqueMeshletDrawBufferBase);
            }
            DrawFoliageForward(cmd, sceneData);
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            
        }

        internal static ForwardOpaqueVariantSelection ResolveOpaqueVariantSelection(Data.SceneRenderingData sceneData)
        {
            int simpleMeshlets = Math.Max(0, sceneData.SimpleOpaqueMeshletCount);
            int simpleNormalMeshlets = Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount);
            int fullMeshlets = Math.Max(0, sceneData.FullOpaqueMeshletCount);
            bool requiresLocalProbeEvaluation = RequiresLocalReflectionProbeEvaluation(sceneData);
            bool forceFullForDebug = sceneData.ReflectionDebugView != ReflectionDebugView.None;
            bool useSimpleGlobalIblPipeline = !forceFullForDebug && !requiresLocalProbeEvaluation;
            int simpleVariantMeshlets = simpleMeshlets + simpleNormalMeshlets;

            return new ForwardOpaqueVariantSelection(
                UseSimpleGlobalIblPipeline: useSimpleGlobalIblPipeline,
                SimpleMeshletCount: useSimpleGlobalIblPipeline ? simpleVariantMeshlets : 0,
                FullMaterialMeshletCount: fullMeshlets + (useSimpleGlobalIblPipeline ? 0 : simpleVariantMeshlets),
                LocalProbeMeshletCount: requiresLocalProbeEvaluation ? simpleVariantMeshlets + fullMeshlets : 0);
        }

        private static bool RequiresLocalReflectionProbeEvaluation(Data.SceneRenderingData sceneData)
        {
            if (!sceneData.ReflectionsEnabled)
                return false;

            if (sceneData.ReflectionMode is ReflectionMode.Disabled or ReflectionMode.GlobalEnvironmentOnly)
                return false;

            return sceneData.ReflectionProbeCount > 0;
        }

        private static void UpdateCompactedForwardVariantDiagnostics(
            Data.SceneRenderingData sceneData,
            int compactedDrawCapacity)
        {
            int meshletCount = Math.Max(0, compactedDrawCapacity);
            sceneData.ForwardSimpleMeshletCount = 0;
            sceneData.ForwardFullMaterialMeshletCount = meshletCount;
            sceneData.ForwardLocalProbeMeshletCount = RequiresLocalReflectionProbeEvaluation(sceneData) ? meshletCount : 0;
        }

        private static void UpdateCompactedForwardShadowDiagnostics(
            Data.SceneRenderingData sceneData,
            int compactedDrawCapacity)
        {
            sceneData.ForwardShadowReceiverMeshletCount = HasForwardShadowReceivers(sceneData)
                ? Math.Max(0, compactedDrawCapacity)
                : 0;
        }

        private static int ResolveForwardShadowReceiverMeshletCount(Data.SceneRenderingData sceneData)
        {
            if (!HasForwardShadowReceivers(sceneData))
                return 0;

            return Math.Max(0, sceneData.SimpleOpaqueMeshletCount) +
                   Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount) +
                   Math.Max(0, sceneData.FullOpaqueMeshletCount);
        }

        private static bool HasForwardShadowReceivers(Data.SceneRenderingData sceneData)
        {
            return sceneData.DirectionalShadowPassEnabled ||
                   sceneData.SpotShadowSelectedCount > 0 ||
                   sceneData.PointShadowSelectedCount > 0;
        }

        internal readonly record struct ForwardOpaqueVariantSelection(
            bool UseSimpleGlobalIblPipeline,
            int SimpleMeshletCount,
            int FullMaterialMeshletCount,
            int LocalProbeMeshletCount);

        private string BuildSceneOpaqueIndirectDispatchSkipReason(Data.SceneRenderingData sceneData)
        {
            if (_bufferManager == null)
                return "scene opaque indirect dispatch buffer unavailable";

            return SceneSubmissionDiagnosticsPolicy.BuildIndirectDispatchSkipReason(
                sceneData,
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private void DrawForwardBucket(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode)
            };

            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            sceneData.ForwardTaskInvocations += meshletCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
        }

        private void DrawForwardBucketIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCapacity <= 0 || _bufferManager == null)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCapacity,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex,
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode)
            };

            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            VkBuffer indirect = _bufferManager.GetBuffer(sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            int diagnosticTaskCount = ResolveIndirectTaskCountForDiagnostics(sceneData);
            sceneData.SceneSubmissionGpuIndirectMeshletTaskCount = diagnosticTaskCount;
            sceneData.ForwardTaskInvocations += diagnosticTaskCount;
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                0,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private static int ResolveIndirectTaskCountForDiagnostics(Data.SceneRenderingData sceneData)
        {
            return Math.Max(
                0,
                Math.Max(
                    sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
                    sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount));
        }

        private void DrawFoliageForward(CommandBuffer cmd, Data.SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || sceneData.FoliageClusterCount <= 0 || sceneData.FoliageDrawBufferBytes == 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.ForwardPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)sceneData.FoliageClusterCount),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = 0u,
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

            sceneData.ForwardTaskInvocations += sceneData.FoliageClusterCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)sceneData.FoliageClusterCount, 1, 1);

            DrawAuthoredFoliageForward(cmd, sceneData);
        }

        private void DrawAuthoredFoliageForward(CommandBuffer cmd, Data.SceneRenderingData sceneData)
        {
            if (_foliagePipeline == null || _bufferManager == null || _foliageManager == null || sceneData.FoliageDrawBufferBytes == 0)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid || buffers.MeshletDrawCapacity <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _foliagePipeline.AuthoredForwardPipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = 0u,
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
        
        public override void OnSwapchainRecreated()
        {
        }
        
        public override void Cleanup()
        {
        }
    }
    
}
