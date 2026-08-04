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
using Njulf.Rendering.Debug;
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
        private readonly RenderSettings _settings;
        private readonly PipelineObjects.SkyboxPipeline? _skyboxPipeline;
        private bool _recordingReflectionCapture;
        private bool _reflectionCaptureIncludesDdgi;

        public ForwardPlusPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            PipelineObjects.FoliagePipeline? foliagePipeline = null,
            BufferManager? bufferManager = null,
            FoliageManager? foliageManager = null,
            PipelineObjects.SkyboxPipeline? skyboxPipeline = null)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _skyboxPipeline = skyboxPipeline;
        }

        public override void Initialize()
        {
        }

        /// <summary>
        /// Records the same material/mesh forward path into one probe face. The caller supplies a
        /// ticket-pinned view and private attachments; no camera state, local reflection lookup,
        /// post-processing, exposure, or screen-space effect is allowed to leak into the capture.
        /// </summary>
        internal void RecordReflectionCapture(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            in ReflectionCaptureViewContext view,
            ImageView colorView,
            ImageView depthView)
        {
            if (colorView.Handle == 0 || depthView.Handle == 0)
                throw new InvalidOperationException("Reflection capture attachments are unavailable.");

            Matrix4x4 oldView = sceneData.ViewMatrix;
            Matrix4x4 oldProjection = sceneData.ProjectionMatrix;
            Matrix4x4 oldViewProjection = sceneData.ViewProjectionMatrix;
            Matrix4x4 oldInverseView = sceneData.InverseViewMatrix;
            Matrix4x4 oldInverseProjection = sceneData.InverseProjectionMatrix;
            Matrix4x4 oldInverseViewProjection = sceneData.InverseViewProjectionMatrix;
            Vector3 oldCameraPosition = sceneData.CameraPosition;
            uint oldScreenWidth = sceneData.ScreenWidth;
            uint oldScreenHeight = sceneData.ScreenHeight;
            bool oldDepthPrePassEnabled = sceneData.DepthPrePassEnabled;
            bool oldReflectionsEnabled = sceneData.ReflectionsEnabled;
            ReflectionMode oldReflectionMode = sceneData.ReflectionMode;
            int oldReflectionProbeCount = sceneData.ReflectionProbeCount;
            bool oldOcclusionEnabled = sceneData.OcclusionCullingEnabled;
            uint oldHiZMipCount = sceneData.HiZMipCount;
            int oldForwardTaskInvocations = sceneData.ForwardTaskInvocations;
            int oldDdgiProbeCount = sceneData.DdgiProbeCount;
            int oldGlobalIlluminationDdgiActive = sceneData.GlobalIlluminationDdgiActive;
            int oldSimpleDdgiActive = sceneData.SimpleDdgiActive;

            try
            {
                _recordingReflectionCapture = true;
                _reflectionCaptureIncludesDdgi = view.IncludesDdgi;
                sceneData.ViewMatrix = view.View;
                sceneData.ProjectionMatrix = view.Projection;
                sceneData.ViewProjectionMatrix = view.View * view.Projection;
                sceneData.InverseViewMatrix = view.View.Invert();
                sceneData.InverseProjectionMatrix = view.Projection.Invert();
                sceneData.InverseViewProjectionMatrix = sceneData.ViewProjectionMatrix.Invert();
                sceneData.CameraPosition = view.Position;
                sceneData.ScreenWidth = view.Resolution;
                sceneData.ScreenHeight = view.Resolution;
                sceneData.DepthPrePassEnabled = view.IncludesDdgi;
                sceneData.ReflectionsEnabled = false;
                sceneData.ReflectionMode = ReflectionMode.Disabled;
                sceneData.ReflectionProbeCount = 0;
                sceneData.DdgiProbeCount = view.IncludesDdgi ? oldDdgiProbeCount : 0;
                if (!view.IncludesDdgi)
                {
                    sceneData.GlobalIlluminationDdgiActive = 0;
                    sceneData.SimpleDdgiActive = 0;
                }
                sceneData.OcclusionCullingEnabled = false;
                sceneData.HiZMipCount = 0;

                var viewport = new Viewport
                {
                    X = 0,
                    Y = 0,
                    Width = view.Resolution,
                    Height = view.Resolution,
                    MinDepth = 0.0f,
                    MaxDepth = 1.0f
                };
                var scissor = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = view.Resolution, Height = view.Resolution }
                };
                _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
                _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
                BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

                RenderingAttachmentInfo colorAttachment = ColorAttachment(
                    colorView,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f)));
                RenderingAttachmentInfo depthAttachment = DepthAttachment(
                    depthView,
                    ImageLayout.DepthStencilAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)));
                var renderingInfo = new RenderingInfo
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D { X = 0, Y = 0 },
                        Extent = new Extent2D { Width = view.Resolution, Height = view.Resolution }
                    },
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    PColorAttachments = &colorAttachment,
                    PDepthAttachment = &depthAttachment
                };
                _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

                // A local capture is a complete scene radiance sample, not a black-clear
                // fallback. Draw the ticket-pinned global sky before opaque geometry so the
                // reverse-Z scene depth naturally occludes it.
                RecordReflectionSkybox(cmd, view);

                ForwardOpaqueVariantSelection selection = ResolveOpaqueVariantSelection(sceneData);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    selection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    Math.Max(0, sceneData.SimpleOpaqueMeshletCount),
                    BindlessIndex.MeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    selection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleFullInputGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                    BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    selection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    Math.Max(0, sceneData.FullOpaqueMeshletCount),
                    BindlessIndex.FullOpaqueMeshletDrawBufferBase);
                DrawFoliageForward(cmd, sceneData);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }
            finally
            {
                _recordingReflectionCapture = false;
                _reflectionCaptureIncludesDdgi = false;
                sceneData.ViewMatrix = oldView;
                sceneData.ProjectionMatrix = oldProjection;
                sceneData.ViewProjectionMatrix = oldViewProjection;
                sceneData.InverseViewMatrix = oldInverseView;
                sceneData.InverseProjectionMatrix = oldInverseProjection;
                sceneData.InverseViewProjectionMatrix = oldInverseViewProjection;
                sceneData.CameraPosition = oldCameraPosition;
                sceneData.ScreenWidth = oldScreenWidth;
                sceneData.ScreenHeight = oldScreenHeight;
                sceneData.DepthPrePassEnabled = oldDepthPrePassEnabled;
                sceneData.ReflectionsEnabled = oldReflectionsEnabled;
                sceneData.ReflectionMode = oldReflectionMode;
                sceneData.ReflectionProbeCount = oldReflectionProbeCount;
                sceneData.OcclusionCullingEnabled = oldOcclusionEnabled;
                sceneData.HiZMipCount = oldHiZMipCount;
                sceneData.ForwardTaskInvocations = oldForwardTaskInvocations;
                sceneData.DdgiProbeCount = oldDdgiProbeCount;
                sceneData.GlobalIlluminationDdgiActive = oldGlobalIlluminationDdgiActive;
                sceneData.SimpleDdgiActive = oldSimpleDdgiActive;
            }
        }

        private void RecordReflectionSkybox(
            CommandBuffer cmd,
            in ReflectionCaptureViewContext view)
        {
            if (_skyboxPipeline == null || !_settings.Environment.Enabled)
                return;

            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                _skyboxPipeline.Pipeline);

            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _skyboxPipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _skyboxPipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);

            GPUSkyboxPushConstants pushConstants = new()
            {
                InverseViewMatrix = view.View.Invert(),
                InverseProjectionMatrix = view.Projection.Invert(),
                EnvironmentTextureIndex = BindlessIndex.EnvironmentCubemapTexture,
                SkyIntensity = _settings.Environment.SkyIntensity,
                RotationRadians = _settings.Environment.RotationRadians,
                DebugView = (uint)EnvironmentDebugView.None
            };
            _context.Api.CmdPushConstants(
                cmd,
                _skyboxPipeline.Layout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUSkyboxPushConstants>(),
                &pushConstants);
            _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            if (!sceneData.HasCurrentDepthPrePass)
            {
                throw new InvalidOperationException(
                    "ForwardPlusPass requires depth produced by DepthPrePass in the current frame.");
            }

            if (sceneData.LocalLightCount > 0 && !sceneData.HasCurrentTiledLightCulling)
            {
                throw new InvalidOperationException(
                    "ForwardPlusPass requires tiled local-light culling produced from current-frame depth.");
            }

            if (sceneData.GlobalIlluminationDdgiActive != 0 ||
                sceneData.SimpleDdgiActive != 0)
            {
                PublishComputeStorageToFragment(cmd);
            }
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            bool materialTransportProvenanceEnabled =
                ShouldWriteMaterialTransportProvenance();
            SetFullViewportAndScissor(cmd, renderExtent);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            if (materialTransportProvenanceEnabled)
                _renderTargets.MaterialTransportProvenance.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);

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
            var colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = colorAttachment;
            if (materialTransportProvenanceEnabled)
            {
                // Zero is the stable background/no-geometry code. Rasterized
                // pixels overwrite it with a categorical source-path byte.
                colorAttachments[1] = ColorAttachment(
                    _renderTargets.MaterialTransportProvenance.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            }
            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store,
                new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)));

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount =
                    ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                        hasColorAttachment: true,
                        materialTransportProvenanceEnabled),
                PColorAttachments = colorAttachments,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            sceneData.ForwardTaskInvocations = 0;
            sceneData.ForwardSimpleMeshletCount = 0;
            sceneData.ForwardFullMaterialMeshletCount = 0;
            sceneData.ForwardLocalProbeMeshletCount = 0;
            sceneData.ForwardShadowReceiverMeshletCapacity = 0;
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
                if (sceneData.ForwardVisibilityCompactionActive)
                {
                    sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect;
                    sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly;
                    sceneData.SceneSubmissionIndirectDispatchSkipReason = string.Empty;
                    UpdateCompactedForwardVariantDiagnostics(sceneData);
                    UpdateCompactedForwardShadowDiagnostics(
                        sceneData,
                        sceneData.ForwardVisibilitySimpleCapacity +
                        sceneData.ForwardVisibilitySimpleNormalCapacity +
                        sceneData.ForwardVisibilityFullCapacity);
                    DrawForwardVisibilityBucketsIndirect(cmd, sceneData);
                }
                else if (sceneData.SceneSubmissionIndirectMeshletDispatchEnabled)
                {
                    int compactedDrawCapacity = Math.Min(
                        sceneData.SceneSubmissionGpuOpaqueCandidateCount,
                        sceneData.SceneSubmissionGpuCompactedOpaqueCapacity);
                    string indirectSkipReason = BuildSceneOpaqueIndirectDispatchSkipReason(sceneData);
                    sceneData.SceneSubmissionIndirectDispatchSkipReason = indirectSkipReason;
                    if (indirectSkipReason.Length == 0)
                    {
                        sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect;
                        sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly;
                        UpdateCompactedForwardVariantDiagnostics(sceneData);
                        UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                        DrawCompactedForwardBucketsIndirect(
                            cmd,
                            sceneData);
                    }
                    else
                    {
                        sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                        sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
                        UpdateCompactedForwardVariantDiagnostics(sceneData);
                        UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                        DrawCompactedForwardBucketsDirect(
                            cmd,
                            sceneData);
                    }
                }
                else
                {
                    int compactedDrawCapacity = Math.Min(
                        sceneData.SceneSubmissionGpuOpaqueCandidateCount,
                        sceneData.SceneSubmissionGpuCompactedOpaqueCapacity);
                    sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                    sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
                    UpdateCompactedForwardVariantDiagnostics(sceneData);
                    UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                    DrawCompactedForwardBucketsDirect(
                        cmd,
                        sceneData);
                }
            }
            else
            {
                sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData);
                ForwardOpaqueVariantSelection variantSelection = ResolveOpaqueVariantSelection(sceneData);
                sceneData.ForwardSimpleMeshletCount = variantSelection.SimpleMeshletCount;
                sceneData.ForwardFullMaterialMeshletCount = variantSelection.FullMaterialMeshletCount;
                sceneData.ForwardLocalProbeMeshletCount = variantSelection.LocalProbeMeshletCount;
                sceneData.ForwardShadowReceiverMeshletCapacity = ResolveForwardShadowReceiverMeshletCapacity(sceneData);

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

        /// <summary>
        /// GPU timestamps cannot isolate instructions inside a fragment shader, but
        /// this nested scope gives GI accounting a conservative, explicit owner for
        /// the forward pass whenever its DDGI gather code is active.  The capture
        /// records it as an inclusive forward-GI timing rather than pretending it is
        /// a pure shader-instruction measurement.
        /// </summary>
        public override void Execute(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps)
        {
            bool giGatherActive = sceneData.GlobalIlluminationDdgiActive != 0 || sceneData.SimpleDdgiActive != 0;
            if (giGatherActive)
                timestamps?.BeginPass(cmd, frameIndex, "ForwardGiGatherPass");

            try
            {
                Execute(cmd, frameIndex, sceneData);
            }
            finally
            {
                if (giGatherActive)
                    timestamps?.EndPass(cmd, frameIndex);
            }
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

        private static void UpdateCompactedForwardVariantDiagnostics(Data.SceneRenderingData sceneData)
        {
            ForwardOpaqueVariantSelection variantSelection = ResolveOpaqueVariantSelection(sceneData);
            sceneData.ForwardSimpleMeshletCount = variantSelection.SimpleMeshletCount;
            sceneData.ForwardFullMaterialMeshletCount = variantSelection.FullMaterialMeshletCount;
            sceneData.ForwardLocalProbeMeshletCount = variantSelection.LocalProbeMeshletCount;
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
            sceneData.ForwardShadowReceiverMeshletCapacity = HasForwardShadowReceivers(sceneData)
                ? Math.Max(0, compactedDrawCapacity)
                : 0;
        }

        private static int ResolveForwardShadowReceiverMeshletCapacity(Data.SceneRenderingData sceneData)
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
                SceneOpaqueCompactionPass.GetFullOpaqueIndirectDispatchOffset() +
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
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode,
                    globalIlluminationEnabled: ShouldApplyGlobalIllumination(sceneData),
                    screenSpaceGlobalIlluminationEnabled: false),
                DiagnosticFlags = Data.GPUForwardPushConstants.PackDiagnosticFlags(
                    ShouldCollectDdgiForwardEstimateCounters(sceneData),
                    ShouldCollectDdgiClipmapCoverageCounters(sceneData),
                    ShouldCollectDirectionalShadowReceiverCounters(sceneData),
                    (uint)sceneData.DirectionalShadowPreviewCascade,
                    materialTransportProvenanceEnabled:
                        ShouldWriteMaterialTransportProvenance()),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    _recordingReflectionCapture)
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

        private void DrawCompactedForwardBucketsIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData)
        {
            bool useSimpleGlobalIblPipeline = ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.SimpleOpaqueMeshletCount),
                BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetSimpleOpaqueIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.FullOpaqueMeshletCount),
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer);
        }

        private void DrawForwardVisibilityBucketsIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData)
        {
            bool useSimpleGlobalIblPipeline = ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.ForwardVisibilitySimpleCapacity),
                BindlessIndex.ForwardVisibleSimpleOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetSimpleOpaqueIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.ForwardVisibilitySimpleNormalCapacity),
                BindlessIndex.ForwardVisibleSimpleNormalOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.ForwardVisibilityFullCapacity),
                BindlessIndex.ForwardVisibleFullOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer);
        }

        private void DrawCompactedForwardBucketsDirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData)
        {
            bool useSimpleGlobalIblPipeline = ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardSimpleGlobalIblPipeline
                    : _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.SimpleOpaqueMeshletCount),
                BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase);
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase);
            DrawForwardBucket(
                cmd,
                sceneData,
                _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.FullOpaqueMeshletCount),
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase);
        }

        private void DrawForwardBucketIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectOffset,
            BufferHandle indirectBufferHandle)
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
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode,
                    globalIlluminationEnabled: ShouldApplyGlobalIllumination(sceneData),
                    screenSpaceGlobalIlluminationEnabled: false),
                DiagnosticFlags = Data.GPUForwardPushConstants.PackDiagnosticFlags(
                    ShouldCollectDdgiForwardEstimateCounters(sceneData),
                    ShouldCollectDdgiClipmapCoverageCounters(sceneData),
                    ShouldCollectDirectionalShadowReceiverCounters(sceneData),
                    (uint)sceneData.DirectionalShadowPreviewCascade,
                    materialTransportProvenanceEnabled:
                        ShouldWriteMaterialTransportProvenance()),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    _recordingReflectionCapture)
            };

            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            VkBuffer indirect = _bufferManager.GetBuffer(indirectBufferHandle);
            // meshletCapacity is an allocation bound, not executed work. Keep the
            // legacy ForwardTaskInvocations diagnostic as a submitted-workgroup
            // compatibility metric even though this indirect path is mesh-only.
            // The fence-safe readback corrects it to the exact emitted count.
            sceneData.ForwardTaskInvocations = Math.Max(
                sceneData.ForwardTaskInvocations,
                sceneData.SceneSubmissionGpuIndirectMeshletTaskCount);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                indirectOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private bool ShouldApplyGlobalIllumination(Data.SceneRenderingData sceneData)
        {
            if (_recordingReflectionCapture)
            {
                return _reflectionCaptureIncludesDdgi &&
                       (_settings.GlobalIllumination.EffectiveUseDdgi ||
                        _settings.GlobalIllumination.EffectiveUseDdgi) &&
                       sceneData.DdgiProbeCount > 0;
            }

            return ShouldApplyGlobalIllumination(sceneData, _settings.GlobalIllumination);
        }

        private bool ShouldCollectDdgiForwardEstimateCounters(Data.SceneRenderingData sceneData)
        {
            return ShouldCollectDdgiForwardEstimateCounters(
                sceneData,
                _settings.GlobalIllumination,
                _settings.Diagnostics);
        }

        internal static bool ShouldCollectDdgiForwardEstimateCounters(
            Data.SceneRenderingData sceneData,
            GlobalIlluminationSettings gi,
            RenderDiagnosticsSettings diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            return diagnostics.DdgiForwardEstimateCountersEnabled &&
                ShouldApplyDdgi(sceneData, gi);
        }

        private bool ShouldCollectDdgiClipmapCoverageCounters(Data.SceneRenderingData sceneData)
        {
            return ShouldCollectDdgiClipmapCoverageCounters(
                sceneData,
                _settings.GlobalIllumination,
                _settings.Diagnostics);
        }

        internal static bool ShouldCollectDdgiClipmapCoverageCounters(
            Data.SceneRenderingData sceneData,
            GlobalIlluminationSettings gi,
            RenderDiagnosticsSettings diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            return ShouldApplyDdgi(sceneData, gi) &&
                (diagnostics.DdgiForwardEstimateCountersEnabled ||
                 IsDdgiGatherDebugView(gi.DebugView));
        }

        private bool ShouldCollectDirectionalShadowReceiverCounters(Data.SceneRenderingData sceneData)
        {
            // Reuse the existing capture/debug gate rather than paying atomics in normal
            // gameplay. The shader additionally samples only one pixel per 16x16 tile.
            return sceneData.DirectionalShadowPassEnabled &&
                (_settings.Diagnostics.DirectionalShadowReceiverCountersEnabled ||
                 _settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                 _settings.Shadows.DebugView != ShadowDebugView.None);
        }

        private static bool IsDdgiGatherDebugView(GlobalIlluminationDebugView view)
        {
            return view is GlobalIlluminationDebugView.DdgiGatherLocalVolume
                or GlobalIlluminationDebugView.DdgiGatherClipmap
                or GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight
                or GlobalIlluminationDebugView.DdgiGatherBlendWeight
                or GlobalIlluminationDebugView.DdgiGatherFallback;
        }

        internal static bool ShouldApplyGlobalIllumination(
            Data.SceneRenderingData sceneData,
            GlobalIlluminationSettings gi)
        {
            if (sceneData.AnimationDebugView != AnimationDebugView.None)
                return false;

            if (!RenderFeatureIsolationPolicy.AllowsPostProcessing(sceneData.ActiveFeatureIsolation))
                return false;

            return ShouldApplyDdgi(sceneData, gi);
        }

        private bool ShouldWriteMaterialTransportProvenance() =>
            !_recordingReflectionCapture &&
            _settings.GlobalIllumination.DebugView ==
            GlobalIlluminationDebugView.MaterialTransportHitProvenance;

        internal static bool ShouldApplyDdgi(
            Data.SceneRenderingData sceneData,
            GlobalIlluminationSettings gi)
        {
            return gi.EffectiveUseDdgi &&
                   sceneData.DdgiProbeCount > 0 &&
                   sceneData.DepthPrePassEnabled;
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
                Flags = ShouldWriteMaterialTransportProvenance() ? 1u << 2 : 0u,
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
                Flags = ShouldWriteMaterialTransportProvenance() ? 1u << 2 : 0u,
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
