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
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Forward+ pass: renders all visible meshlets with per-tile lighting.
    /// Input: meshlet data, material data, textures, light index buffers
    /// Uses mesh shaders and bindless resource access.
    /// </summary>
    public sealed unsafe class ForwardPlusPass : RenderPassBase
    {
        // DDGI irradiance is deliberately low frequency. One exact gather per
        // 12x12 block is published as a compact gather lattice. Compute performs
        // centered bilinear reconstruction into one FP16 value per 2x2 screen
        // block so a fragment quad shares one cache address. The gather
        // producer scans the whole block for covered reverse-Z depth, so this
        // is not a blind center downsample.
        internal const uint SimpleDdgiReceiverGatherScale = 12u;
        internal const uint SimpleDdgiReceiverCacheScale = 2u;
        internal const uint SimpleDdgiReceiverCacheWorkgroupSize = 8u;
        internal const ulong SimpleDdgiReceiverCacheEntryBytes = 16u;
        internal const ulong SimpleDdgiReceiverGatherEntryBytes = 16u;
        private const int FramesInFlight = 2;

        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly PipelineObjects.FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly PipelineObjects.SkyboxPipeline? _skyboxPipeline;
        private bool _recordingReflectionCapture;
        private bool _reflectionCaptureIncludesDdgi;
        private readonly BufferHandle[] _simpleDdgiReceiverCacheBuffers =
            new BufferHandle[FramesInFlight];
        private readonly BufferHandle[] _simpleDdgiReceiverGatherBuffers =
            new BufferHandle[FramesInFlight];
        private nint _simpleDdgiReceiverCacheEntryPointName;
        private DescriptorSetLayout _simpleDdgiReceiverCacheOutputSetLayout;
        private DescriptorPool _simpleDdgiReceiverCacheDescriptorPool;
        private readonly DescriptorSet[] _simpleDdgiReceiverCacheOutputSets =
            new DescriptorSet[FramesInFlight];
        private readonly DescriptorSet[] _simpleDdgiReceiverCacheConsumerSets =
            new DescriptorSet[FramesInFlight];
        private PipelineLayout _simpleDdgiReceiverCachePipelineLayout;
        private PipelineCache _simpleDdgiReceiverCachePipelineCache;
        private VkPipeline _simpleDdgiReceiverCachePipeline;
        private VkPipeline _simpleDdgiReceiverCacheResolvePipeline;
        private uint _simpleDdgiReceiverCacheWidth;
        private uint _simpleDdgiReceiverCacheHeight;
        private ulong _simpleDdgiReceiverCacheBufferBytes;
        private uint _simpleDdgiReceiverGatherWidth;
        private uint _simpleDdgiReceiverGatherHeight;
        private ulong _simpleDdgiReceiverGatherBufferBytes;
        private bool _simpleDdgiReceiverCacheAvailableForCurrentView;

        internal ulong SimpleDdgiReceiverCacheBufferBytes
        {
            get
            {
                ulong bytes = 0u;
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (_simpleDdgiReceiverCacheBuffers[i].IsValid)
                        bytes = checked(bytes + _simpleDdgiReceiverCacheBufferBytes);
                }
                return bytes;
            }
        }

        internal int SimpleDdgiReceiverCacheBufferCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (_simpleDdgiReceiverCacheBuffers[i].IsValid)
                        count++;
                }
                return count;
            }
        }

        internal ulong SimpleDdgiReceiverGatherBufferTotalBytes
        {
            get
            {
                ulong bytes = 0u;
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (_simpleDdgiReceiverGatherBuffers[i].IsValid)
                        bytes = checked(bytes + _simpleDdgiReceiverGatherBufferBytes);
                }
                return bytes;
            }
        }

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
            for (int i = 0; i < FramesInFlight; i++)
            {
                _simpleDdgiReceiverCacheBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherBuffers[i] = BufferHandle.Invalid;
            }
        }

        public override void Initialize()
        {
            if (_bufferManager == null)
                return;

            try
            {
                _simpleDdgiReceiverCacheEntryPointName =
                    SilkMarshal.StringToPtr("main");
                CreateSimpleDdgiReceiverCachePipelineCache();
                CreateSimpleDdgiReceiverCacheOutputDescriptors();
                CreateSimpleDdgiReceiverCachePipelineLayout();
                _simpleDdgiReceiverCachePipeline =
                    CreateSimpleDdgiReceiverCachePipeline(
                        "ddgi_simple_receiver_cache.comp.spv",
                        "Simple DDGI Receiver Gather Pipeline");
                _simpleDdgiReceiverCacheResolvePipeline =
                    CreateSimpleDdgiReceiverCachePipeline(
                        "ddgi_simple_receiver_cache_resolve.comp.spv",
                        "Simple DDGI Receiver Cache Resolve Pipeline");
                RecreateSimpleDdgiReceiverCacheResources();
            }
            catch (Exception ex)
            {
                // Receiver caching is an accelerator, not a correctness
                // prerequisite. Keep the exact fragment gather available when
                // resource or pipeline creation is unsupported.
                System.Diagnostics.Debug.WriteLine(
                    $"Simple-DDGI receiver cache unavailable: {ex.GetType().Name}: {ex.Message}");
                CleanupSimpleDdgiReceiverCache();
            }
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
            ExecuteInternal(cmd, frameIndex, sceneData, timestamps: null);
        }

        private void ExecuteInternal(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            GpuTimestampRecorder? timestamps)
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

            _simpleDdgiReceiverCacheAvailableForCurrentView = false;
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            bool materialTransportProvenanceEnabled =
                ShouldWriteMaterialTransportProvenance();
            bool receiverCacheEligible = ShouldDispatchSimpleDdgiReceiverCache(
                frameIndex,
                sceneData,
                renderExtent,
                materialTransportProvenanceEnabled);
            if (sceneData.GlobalIlluminationDdgiActive != 0 ||
                sceneData.SimpleDdgiActive != 0)
            {
                PublishComputeStorageToFragment(
                    cmd,
                    includeComputeReceiver: receiverCacheEligible);
            }

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            if (receiverCacheEligible)
            {
                timestamps?.BeginPass(
                    cmd,
                    frameIndex,
                    "SimpleDdgiReceiverCachePass");
                try
                {
                    _simpleDdgiReceiverCacheAvailableForCurrentView =
                        DispatchSimpleDdgiReceiverCache(
                            cmd,
                            frameIndex,
                            sceneData,
                            renderExtent);
                }
                finally
                {
                    timestamps?.EndPass(cmd, frameIndex);
                }
            }

            SetFullViewportAndScissor(cmd, renderExtent);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);
            if (_simpleDdgiReceiverCacheAvailableForCurrentView)
            {
                BindSimpleDdgiReceiverCacheBuffer(cmd, frameIndex);
            }

            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            if (materialTransportProvenanceEnabled)
                _renderTargets.MaterialTransportProvenance.TransitionToColorAttachment(cmd);
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
            _simpleDdgiReceiverCacheAvailableForCurrentView = false;
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
                ExecuteInternal(cmd, frameIndex, sceneData, timestamps);
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

            bool receiverCacheEnabled = ShouldUseSimpleDdgiReceiverCacheForDraw();
            pipeline = _meshPipeline.ResolveOpaqueSpecializedPipeline(
                pipeline,
                receiverCacheEnabled,
                ShouldUseForwardGiDisabledBenchmarkPipeline());
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                // Time is unused by opaque forward mesh/fragment stages. The
                // receiver-cache specialization consumes its exact bit pattern
                // as the cache width without changing the 256-byte ABI.
                Time = receiverCacheEnabled
                    ? BitConverter.UInt32BitsToSingle(
                        _simpleDdgiReceiverCacheWidth)
                    : sceneData.Time,
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
                        ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCacheEnabled),
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

            bool receiverCacheEnabled = ShouldUseSimpleDdgiReceiverCacheForDraw();
            pipeline = _meshPipeline.ResolveOpaqueSpecializedPipeline(
                pipeline,
                receiverCacheEnabled,
                ShouldUseForwardGiDisabledBenchmarkPipeline());
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = receiverCacheEnabled
                    ? BitConverter.UInt32BitsToSingle(
                        _simpleDdgiReceiverCacheWidth)
                    : sceneData.Time,
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
                        ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCacheEnabled),
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

        private bool ShouldDispatchSimpleDdgiReceiverCache(
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool materialTransportProvenanceEnabled)
        {
#if DEBUG || NJULF_DETAILED_INVESTIGATION
            // Detailed/provenance artifacts deliberately retain exact
            // per-fragment diagnostics and gather attribution.
            return false;
#else
            float environmentFallbackIntensity =
                _settings.GlobalIllumination.EnvironmentFallbackIntensity;
            if (_recordingReflectionCapture || materialTransportProvenanceEnabled ||
                _settings.Diagnostics.ForceExactForwardGiGatherForBenchmark ||
                !float.IsFinite(environmentFallbackIntensity) ||
                environmentFallbackIntensity > 1.0f ||
                _settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None ||
                _bufferManager == null ||
                _simpleDdgiReceiverCachePipeline.Handle == 0 ||
                _simpleDdgiReceiverCacheResolvePipeline.Handle == 0 ||
                frameIndex < 0 || frameIndex >= FramesInFlight ||
                !_simpleDdgiReceiverCacheBuffers[frameIndex].IsValid ||
                _simpleDdgiReceiverCacheOutputSets[frameIndex].Handle == 0 ||
                _simpleDdgiReceiverCacheConsumerSets[frameIndex].Handle == 0 ||
                !_simpleDdgiReceiverGatherBuffers[frameIndex].IsValid ||
                renderExtent.Width == 0 || renderExtent.Height == 0)
            {
                return false;
            }

            if ((sceneData.CurrentFrameIndex & 1u) != (uint)frameIndex ||
                sceneData.SimpleDdgiActive == 0 ||
                !ShouldApplyGlobalIllumination(sceneData))
            {
                return false;
            }

            int opaqueReceiverCapacity =
                Math.Max(0, sceneData.SimpleOpaqueMeshletCount) +
                Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount) +
                Math.Max(0, sceneData.FullOpaqueMeshletCount) +
                Math.Max(0, sceneData.ForwardVisibilitySimpleCapacity) +
                Math.Max(0, sceneData.ForwardVisibilitySimpleNormalCapacity) +
                Math.Max(0, sceneData.ForwardVisibilityFullCapacity);
            if (opaqueReceiverCapacity == 0)
                return false;

            uint expectedWidth = DivideRoundUp(
                renderExtent.Width,
                SimpleDdgiReceiverCacheScale);
            uint expectedHeight = DivideRoundUp(
                renderExtent.Height,
                SimpleDdgiReceiverCacheScale);
            uint expectedGatherWidth = DivideRoundUp(
                renderExtent.Width,
                SimpleDdgiReceiverGatherScale);
            uint expectedGatherHeight = DivideRoundUp(
                renderExtent.Height,
                SimpleDdgiReceiverGatherScale);
            return _simpleDdgiReceiverCacheWidth == expectedWidth &&
                   _simpleDdgiReceiverCacheHeight == expectedHeight &&
                   _simpleDdgiReceiverCacheBufferBytes == checked(
                       (ulong)expectedWidth * expectedHeight *
                       SimpleDdgiReceiverCacheEntryBytes) &&
                   _simpleDdgiReceiverCacheBuffers[frameIndex].IsValid &&
                   _simpleDdgiReceiverGatherWidth == expectedGatherWidth &&
                   _simpleDdgiReceiverGatherHeight == expectedGatherHeight &&
                   _simpleDdgiReceiverGatherBufferBytes == checked(
                       (ulong)expectedGatherWidth * expectedGatherHeight *
                       SimpleDdgiReceiverGatherEntryBytes);
#endif
        }

        private bool DispatchSimpleDdgiReceiverCache(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent)
        {
            if (_bufferManager == null ||
                _simpleDdgiReceiverCachePipeline.Handle == 0 ||
                _simpleDdgiReceiverCacheResolvePipeline.Handle == 0 ||
                frameIndex < 0 || frameIndex >= FramesInFlight)
            {
                return false;
            }

            BufferHandle cacheHandle =
                _simpleDdgiReceiverCacheBuffers[frameIndex];
            BufferHandle gatherHandle =
                _simpleDdgiReceiverGatherBuffers[frameIndex];
            if (!cacheHandle.IsValid || !gatherHandle.IsValid ||
                _simpleDdgiReceiverCacheOutputSets[frameIndex].Handle == 0)
                return false;

            // First evaluate one exact structured gather per 8x8 receiver
            // block. This compact lattice carries representative depth only
            // until the following compute resolve.
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _simpleDdgiReceiverCachePipeline);
            BindBindlessStorageAndTextures(
                cmd,
                _simpleDdgiReceiverCachePipelineLayout,
                PipelineBindPoint.Compute);
            var pushConstants = new GPUSimpleDdgiReceiverCachePushConstants
            {
                InverseViewProjectionMatrix =
                    sceneData.InverseViewProjectionMatrix,
                CameraPositionAndPadding =
                    new Vector4(sceneData.CameraPosition, 0.0f),
                ScreenWidth = renderExtent.Width,
                ScreenHeight = renderExtent.Height,
                CacheWidth = _simpleDdgiReceiverGatherWidth,
                CacheHeight = _simpleDdgiReceiverGatherHeight,
                ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
                DepthTextureIndex = BindlessIndex.DepthTexture,
                CacheBufferIndex = checked((uint)
                    (BindlessIndex.SimpleDdgiReceiverGatherBufferBase +
                     frameIndex)),
                ReceiverScale = SimpleDdgiReceiverGatherScale
            };
            _context.Api.CmdPushConstants(
                cmd,
                _simpleDdgiReceiverCachePipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiReceiverCachePushConstants>(),
                &pushConstants);
            _context.Api.CmdDispatch(
                cmd,
                DivideRoundUp(
                    pushConstants.CacheWidth,
                    SimpleDdgiReceiverCacheWorkgroupSize),
                DivideRoundUp(
                    pushConstants.CacheHeight,
                    SimpleDdgiReceiverCacheWorkgroupSize),
                1u);

            VkBuffer gatherBuffer = _bufferManager.GetBuffer(gatherHandle);
            var gatherBarrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = gatherBuffer,
                Offset = 0,
                Size = _simpleDdgiReceiverGatherBufferBytes
            };
            var gatherDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &gatherBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &gatherDependency);

            // Prefilter the exact-gather lattice to a frame-local half-size
            // packed FP16 buffer. Invalid lattice cells are repaired only from nearby
            // occupied cells before centered bilinear reconstruction, so
            // empty depth tiles cannot darken receiver silhouettes.
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _simpleDdgiReceiverCacheResolvePipeline);
            DescriptorSet outputSet =
                _simpleDdgiReceiverCacheOutputSets[frameIndex];
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _simpleDdgiReceiverCachePipelineLayout,
                2,
                1,
                &outputSet,
                0,
                null);
            var resolveConstants =
                new GPUSimpleDdgiReceiverCacheResolvePushConstants
                {
                    GatherWidth = _simpleDdgiReceiverGatherWidth,
                    GatherHeight = _simpleDdgiReceiverGatherHeight,
                    CacheWidth = _simpleDdgiReceiverCacheWidth,
                    CacheHeight = _simpleDdgiReceiverCacheHeight,
                    GatherBufferIndex = checked((uint)
                        (BindlessIndex.SimpleDdgiReceiverGatherBufferBase +
                         frameIndex)),
                    PackedScaleAndEdgeExtents =
                        PackSimpleDdgiReceiverCacheResolveDimensions(
                            renderExtent)
                };
            _context.Api.CmdPushConstants(
                cmd,
                _simpleDdgiReceiverCachePipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<
                    GPUSimpleDdgiReceiverCacheResolvePushConstants>(),
                &resolveConstants);
            _context.Api.CmdDispatch(
                cmd,
                DivideRoundUp(
                    _simpleDdgiReceiverCacheWidth,
                    SimpleDdgiReceiverCacheWorkgroupSize),
                DivideRoundUp(
                    _simpleDdgiReceiverCacheHeight,
                    SimpleDdgiReceiverCacheWorkgroupSize),
                1u);

            VkBuffer cacheBuffer = _bufferManager.GetBuffer(cacheHandle);
            var cacheBarrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = cacheBuffer,
                Offset = 0,
                Size = _simpleDdgiReceiverCacheBufferBytes
            };
            var cacheDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &cacheBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &cacheDependency);
            return true;
        }

        private bool ShouldUseSimpleDdgiReceiverCacheForDraw()
        {
            return _simpleDdgiReceiverCacheAvailableForCurrentView &&
                   !_settings.Diagnostics.ForceExactForwardGiGatherForBenchmark &&
                   !_recordingReflectionCapture &&
                   !ShouldWriteMaterialTransportProvenance();
        }

        private void BindSimpleDdgiReceiverCacheBuffer(
            CommandBuffer cmd,
            int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameIndex),
                    frameIndex,
                    "Receiver-cache frame index is out of range.");
            }

            DescriptorSet consumerSet =
                _simpleDdgiReceiverCacheConsumerSets[frameIndex];
            if (consumerSet.Handle == 0)
            {
                throw new InvalidOperationException(
                    "The current receiver-cache buffer descriptor is unavailable.");
            }
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                2,
                1,
                &consumerSet,
                0,
                null);
        }

        private bool ShouldUseForwardGiDisabledBenchmarkPipeline()
        {
            return _settings.Diagnostics.SuppressForwardGiGatherForBenchmark &&
                   _settings.GlobalIllumination.DebugView ==
                       GlobalIlluminationDebugView.None &&
                   !_recordingReflectionCapture &&
                   !ShouldWriteMaterialTransportProvenance();
        }

        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1u) / divisor);
        }

        internal static uint PackSimpleDdgiReceiverCacheResolveDimensions(
            Extent2D renderExtent)
        {
            if (renderExtent.Width == 0 || renderExtent.Height == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(renderExtent),
                    "Receiver-cache render extent must be non-zero.");

            uint lastBlockWidth =
                ((renderExtent.Width - 1u) % SimpleDdgiReceiverCacheScale) + 1u;
            uint lastBlockHeight =
                ((renderExtent.Height - 1u) % SimpleDdgiReceiverCacheScale) + 1u;
            return SimpleDdgiReceiverGatherScale |
                   (SimpleDdgiReceiverCacheScale << 8) |
                   (lastBlockWidth << 16) |
                   (lastBlockHeight << 24);
        }

        private bool ShouldApplyGlobalIllumination(Data.SceneRenderingData sceneData)
        {
            if (_recordingReflectionCapture)
            {
                return _reflectionCaptureIncludesDdgi &&
                       _settings.GlobalIllumination.EffectiveUseDdgi &&
                       sceneData.DdgiProbeCount > 0;
            }

            if (_settings.Diagnostics.SuppressForwardGiGatherForBenchmark)
                return false;

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

        private void CreateSimpleDdgiReceiverCachePipelineCache()
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };
            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &info,
                null,
                out _simpleDdgiReceiverCachePipelineCache);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create Simple-DDGI receiver-cache pipeline cache",
                    result);
            }
            _context.SetDebugName(
                _simpleDdgiReceiverCachePipelineCache.Handle,
                ObjectType.PipelineCache,
                "Simple DDGI Receiver Cache Pipeline Cache");
        }

        private void CreateSimpleDdgiReceiverCacheOutputDescriptors()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _simpleDdgiReceiverCacheOutputSetLayout);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create Simple-DDGI receiver-cache output descriptor layout",
                    result);
            }
            _context.SetDebugName(
                _simpleDdgiReceiverCacheOutputSetLayout.Handle,
                ObjectType.DescriptorSetLayout,
                "Simple DDGI Receiver Cache Output Descriptor Layout");

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = FramesInFlight * 2
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = FramesInFlight * 2
            };
            result = _context.Api.CreateDescriptorPool(
                _context.Device,
                &poolInfo,
                null,
                out _simpleDdgiReceiverCacheDescriptorPool);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create Simple-DDGI receiver-cache output descriptor pool",
                    result);
            }
            _context.SetDebugName(
                _simpleDdgiReceiverCacheDescriptorPool.Handle,
                ObjectType.DescriptorPool,
                "Simple DDGI Receiver Cache Descriptor Pool");

            DescriptorSetLayout* layouts =
                stackalloc DescriptorSetLayout[FramesInFlight];
            DescriptorSet* sets = stackalloc DescriptorSet[FramesInFlight];
            for (int i = 0; i < FramesInFlight; i++)
                layouts[i] = _simpleDdgiReceiverCacheOutputSetLayout;
            var allocationInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool =
                    _simpleDdgiReceiverCacheDescriptorPool,
                DescriptorSetCount = FramesInFlight,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocationInfo,
                sets);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to allocate Simple-DDGI receiver-cache output descriptor sets",
                    result);
            }
            for (int i = 0; i < FramesInFlight; i++)
            {
                _simpleDdgiReceiverCacheOutputSets[i] = sets[i];
                _context.SetDebugName(
                    sets[i].Handle,
                    ObjectType.DescriptorSet,
                    $"Simple DDGI Receiver Cache Output Descriptor Set {i}");
            }

            for (int i = 0; i < FramesInFlight; i++)
                layouts[i] = _meshPipeline.ForwardReceiverCacheBufferSetLayout;
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocationInfo,
                sets);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to allocate Simple-DDGI receiver-cache consumer descriptor sets",
                    result);
            }
            for (int i = 0; i < FramesInFlight; i++)
            {
                _simpleDdgiReceiverCacheConsumerSets[i] = sets[i];
                _context.SetDebugName(
                    sets[i].Handle,
                    ObjectType.DescriptorSet,
                    $"Simple DDGI Receiver Cache Consumer Descriptor Set {i}");
            }
        }

        private void CreateSimpleDdgiReceiverCachePipelineLayout()
        {
            DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
            {
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _simpleDdgiReceiverCacheOutputSetLayout
            };
            var range = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<
                    GPUSimpleDdgiReceiverCachePushConstants>()
            };
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &info,
                null,
                out _simpleDdgiReceiverCachePipelineLayout);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create Simple-DDGI receiver-cache pipeline layout",
                    result);
            }
            _context.SetDebugName(
                _simpleDdgiReceiverCachePipelineLayout.Handle,
                ObjectType.PipelineLayout,
                "Simple DDGI Receiver Cache Pipeline Layout");
        }

        private VkPipeline CreateSimpleDdgiReceiverCachePipeline(
            string shaderArtifactName,
            string debugName)
        {
            if (string.IsNullOrWhiteSpace(shaderArtifactName))
                throw new ArgumentException(
                    "A receiver-cache shader artifact is required.",
                    nameof(shaderArtifactName));
            if (string.IsNullOrWhiteSpace(debugName))
                throw new ArgumentException(
                    "A receiver-cache pipeline debug name is required.",
                    nameof(debugName));

            ShaderModule module = default;
            try
            {
                module = ShaderModuleLoader.Load(
                    _context,
                    shaderArtifactName);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = (byte*)_simpleDdgiReceiverCacheEntryPointName
                };
                var info = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _simpleDdgiReceiverCachePipelineLayout,
                    BasePipelineIndex = -1
                };
                Result result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _simpleDdgiReceiverCachePipelineCache,
                    1,
                    &info,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                {
                    throw new VulkanException(
                        $"Failed to create {debugName}",
                        result);
                }
                _context.SetDebugName(
                    pipeline.Handle,
                    ObjectType.Pipeline,
                    debugName);
                return pipeline;
            }
            finally
            {
                if (module.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, module, null);
            }
        }

        private void RecreateSimpleDdgiReceiverCacheResources()
        {
            if (_bufferManager == null ||
                _simpleDdgiReceiverCachePipeline.Handle == 0 ||
                _simpleDdgiReceiverCacheResolvePipeline.Handle == 0 ||
                _simpleDdgiReceiverCacheDescriptorPool.Handle == 0)
            {
                return;
            }

            Extent2D extent = _renderTargets.SceneColor.Extent;
            if (extent.Width == 0 || extent.Height == 0)
                return;
            uint cacheWidth = DivideRoundUp(
                extent.Width,
                SimpleDdgiReceiverCacheScale);
            uint cacheHeight = DivideRoundUp(
                extent.Height,
                SimpleDdgiReceiverCacheScale);
            ulong cacheByteSize = checked(
                (ulong)cacheWidth * cacheHeight *
                SimpleDdgiReceiverCacheEntryBytes);
            uint gatherWidth = DivideRoundUp(
                extent.Width,
                SimpleDdgiReceiverGatherScale);
            uint gatherHeight = DivideRoundUp(
                extent.Height,
                SimpleDdgiReceiverGatherScale);
            ulong gatherByteSize = checked(
                (ulong)gatherWidth * gatherHeight *
                SimpleDdgiReceiverGatherEntryBytes);
            bool currentResourcesMatch =
                _simpleDdgiReceiverCacheWidth == cacheWidth &&
                _simpleDdgiReceiverCacheHeight == cacheHeight &&
                _simpleDdgiReceiverCacheBufferBytes == cacheByteSize &&
                _simpleDdgiReceiverGatherWidth == gatherWidth &&
                _simpleDdgiReceiverGatherHeight == gatherHeight &&
                _simpleDdgiReceiverGatherBufferBytes == gatherByteSize;
            for (int i = 0; i < FramesInFlight; i++)
            {
                currentResourcesMatch &=
                    _simpleDdgiReceiverCacheBuffers[i].IsValid &&
                    _simpleDdgiReceiverGatherBuffers[i].IsValid;
            }
            if (currentResourcesMatch)
                return;

            var cacheReplacements = new BufferHandle[FramesInFlight];
            var gatherReplacements = new BufferHandle[FramesInFlight];
            var cacheNativeBuffers = new VkBuffer[FramesInFlight];
            var gatherNativeBuffers = new VkBuffer[FramesInFlight];
            for (int i = 0; i < FramesInFlight; i++)
            {
                cacheReplacements[i] = BufferHandle.Invalid;
                gatherReplacements[i] = BufferHandle.Invalid;
            }
            try
            {
                for (int i = 0; i < FramesInFlight; i++)
                {
                    cacheReplacements[i] = _bufferManager.CreateDeviceBuffer(
                        cacheByteSize,
                        BufferUsageFlags.StorageBufferBit,
                        requireDeviceAddress: false,
                        MemoryBudgetCategory.GlobalIllumination,
                        $"Simple DDGI Resolved Receiver Cache Frame {i} " +
                        $"({cacheWidth}x{cacheHeight})");
                    gatherReplacements[i] = _bufferManager.CreateDeviceBuffer(
                        gatherByteSize,
                        BufferUsageFlags.StorageBufferBit,
                        requireDeviceAddress: false,
                        MemoryBudgetCategory.GlobalIllumination,
                        $"Simple DDGI Receiver Gather Lattice Frame {i} " +
                        $"({gatherWidth}x{gatherHeight})");
                }
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (!cacheReplacements[i].IsValid ||
                        _simpleDdgiReceiverCacheOutputSets[i].Handle == 0 ||
                        _simpleDdgiReceiverCacheConsumerSets[i].Handle == 0)
                    {
                        throw new InvalidOperationException(
                            "Receiver-cache descriptor publication prerequisites are invalid.");
                    }
                    cacheNativeBuffers[i] =
                        _bufferManager.GetBuffer(cacheReplacements[i]);
                    gatherNativeBuffers[i] =
                        _bufferManager.GetBuffer(gatherReplacements[i]);
                }
            }
            catch
            {
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (cacheReplacements[i].IsValid)
                        _bufferManager.DestroyBuffer(cacheReplacements[i]);
                    if (gatherReplacements[i].IsValid)
                        _bufferManager.DestroyBuffer(gatherReplacements[i]);
                }
                throw;
            }

            // Swapchain recreation waits for the device to become idle before
            // this callback. Resolve and validate every native handle before
            // descriptor publication, then publish all replacements before
            // retiring the old pairs so no frame can observe a descriptor for
            // a destroyed resource. Vulkan descriptor updates have no
            // recoverable result after this preflight boundary.
            DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[2];
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
            for (int i = 0; i < FramesInFlight; i++)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.SimpleDdgiReceiverGatherBufferBase + i,
                    gatherNativeBuffers[i],
                    0,
                    gatherByteSize);

                bufferInfos[0] = new DescriptorBufferInfo
                {
                    Buffer = cacheNativeBuffers[i],
                    Offset = 0,
                    Range = cacheByteSize
                };
                bufferInfos[1] = new DescriptorBufferInfo
                {
                    Buffer = cacheNativeBuffers[i],
                    Offset = 0,
                    Range = cacheByteSize
                };
                writes[0] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _simpleDdgiReceiverCacheOutputSets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[0]
                };
                writes[1] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _simpleDdgiReceiverCacheConsumerSets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[1]
                };
                _context.Api.UpdateDescriptorSets(
                    _context.Device,
                    2,
                    writes,
                    0,
                    null);
            }
            for (int i = 0; i < FramesInFlight; i++)
            {
                BufferHandle oldCache =
                    _simpleDdgiReceiverCacheBuffers[i];
                BufferHandle oldGather =
                    _simpleDdgiReceiverGatherBuffers[i];
                _simpleDdgiReceiverCacheBuffers[i] = cacheReplacements[i];
                _simpleDdgiReceiverGatherBuffers[i] = gatherReplacements[i];
                if (oldCache.IsValid)
                    _bufferManager.DestroyBuffer(oldCache);
                if (oldGather.IsValid)
                    _bufferManager.DestroyBuffer(oldGather);
            }
            _simpleDdgiReceiverCacheWidth = cacheWidth;
            _simpleDdgiReceiverCacheHeight = cacheHeight;
            _simpleDdgiReceiverCacheBufferBytes = cacheByteSize;
            _simpleDdgiReceiverGatherWidth = gatherWidth;
            _simpleDdgiReceiverGatherHeight = gatherHeight;
            _simpleDdgiReceiverGatherBufferBytes = gatherByteSize;
        }

        private void CleanupSimpleDdgiReceiverCache()
        {
            _simpleDdgiReceiverCacheAvailableForCurrentView = false;
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (_bufferManager != null)
                {
                    if (_simpleDdgiReceiverCacheBuffers[i].IsValid)
                    {
                        _bufferManager.DestroyBuffer(
                            _simpleDdgiReceiverCacheBuffers[i]);
                    }
                    if (_simpleDdgiReceiverGatherBuffers[i].IsValid)
                    {
                        _bufferManager.DestroyBuffer(
                            _simpleDdgiReceiverGatherBuffers[i]);
                    }
                }
                _simpleDdgiReceiverCacheBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverCacheOutputSets[i] = default;
                _simpleDdgiReceiverCacheConsumerSets[i] = default;
            }
            _simpleDdgiReceiverCacheWidth = 0;
            _simpleDdgiReceiverCacheHeight = 0;
            _simpleDdgiReceiverCacheBufferBytes = 0;
            _simpleDdgiReceiverGatherWidth = 0;
            _simpleDdgiReceiverGatherHeight = 0;
            _simpleDdgiReceiverGatherBufferBytes = 0;

            if (_simpleDdgiReceiverCacheResolvePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheResolvePipeline,
                    null);
                _simpleDdgiReceiverCacheResolvePipeline = default;
            }

            if (_simpleDdgiReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCachePipeline,
                    null);
                _simpleDdgiReceiverCachePipeline = default;
            }
            if (_simpleDdgiReceiverCachePipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(
                    _context.Device,
                    _simpleDdgiReceiverCachePipelineLayout,
                    null);
                _simpleDdgiReceiverCachePipelineLayout = default;
            }
            if (_simpleDdgiReceiverCacheDescriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(
                    _context.Device,
                    _simpleDdgiReceiverCacheDescriptorPool,
                    null);
                _simpleDdgiReceiverCacheDescriptorPool = default;
            }
            if (_simpleDdgiReceiverCacheOutputSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(
                    _context.Device,
                    _simpleDdgiReceiverCacheOutputSetLayout,
                    null);
                _simpleDdgiReceiverCacheOutputSetLayout = default;
            }
            if (_simpleDdgiReceiverCachePipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(
                    _context.Device,
                    _simpleDdgiReceiverCachePipelineCache,
                    null);
                _simpleDdgiReceiverCachePipelineCache = default;
            }
            if (_simpleDdgiReceiverCacheEntryPointName != 0)
            {
                SilkMarshal.Free(_simpleDdgiReceiverCacheEntryPointName);
                _simpleDdgiReceiverCacheEntryPointName = 0;
            }
        }

        public override void OnSwapchainRecreated()
        {
            try
            {
                RecreateSimpleDdgiReceiverCacheResources();
            }
            catch (Exception ex)
            {
                _simpleDdgiReceiverCacheAvailableForCurrentView = false;
                System.Diagnostics.Debug.WriteLine(
                    $"Simple-DDGI receiver cache resize failed; exact gather retained: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void Cleanup()
        {
            CleanupSimpleDdgiReceiverCache();
        }
    }

}
