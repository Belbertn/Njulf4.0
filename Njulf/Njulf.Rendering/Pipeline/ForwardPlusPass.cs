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
        // The low-cost receiver accelerator evaluates one exact gather per
        // 12x12 block, then reconstructs one FP16 value per 2x2 screen block.
        // That approximation is reserved for lower quality tiers: it cannot
        // preserve the fragment normal/material signal required by High,
        // DDGI High, or Ultra. Those tiers use the exact forward gather.
        internal const uint SimpleDdgiReceiverGatherScale =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.SurfaceTileScale;
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
        private readonly GiPipelineCacheService? _giPipelineCacheService;
        private readonly SimpleDdgiReceiverFeedbackVulkanRuntime?
            _simpleDdgiReceiverFeedbackRuntime;
        private ForwardNearFieldDirectSourceAttachmentBinding?
            _nearFieldDirectSourceBinding;
        private readonly Func<bool>? _nearFieldDirectSourceRuntimeAvailable;
        private readonly ForwardGiCausticReceiverAttachmentBinding?
            _giCausticReceiverBinding;
        private readonly Func<bool>? _giCausticRuntimeAvailable;
        private readonly ForwardHybridReflectionReceiverAttachmentBinding?
            _hybridReflectionReceiverBinding;
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
        private VkPipeline _simpleDdgiReceiverFeedbackPipeline;
        private VkPipeline _simpleDdgiReceiverCacheResolvePipeline;
        private uint _simpleDdgiReceiverCacheWidth;
        private uint _simpleDdgiReceiverCacheHeight;
        private ulong _simpleDdgiReceiverCacheBufferBytes;
        private uint _simpleDdgiReceiverGatherWidth;
        private uint _simpleDdgiReceiverGatherHeight;
        private ulong _simpleDdgiReceiverGatherBufferBytes;
        private bool _simpleDdgiReceiverCacheAvailableForCurrentView;
        private bool _simpleDdgiReceiverCacheConsumedForCurrentView;
        private bool _forwardGiDisabledBenchmarkPipelineUsedForCurrentView;
        private bool _forwardGiExactGatherUsedForCurrentView;
        private bool _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView;
        private bool _simpleDdgiFoliageFeedbackRequiredForCurrentView;
        private bool _simpleDdgiReflectionFeedbackRequiredForCurrentView;
        private bool _hybridReflectionReceiverEnabledForCurrentView;
        private int _reflectionFeedbackCubemapArrayLayer;
        private int _reflectionFeedbackBatchFrameIndex = -1;
        private int _reflectionFeedbackFacesRecordedForCurrentBatch;

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
            PipelineObjects.SkyboxPipeline? skyboxPipeline = null,
            GiPipelineCacheService? giPipelineCacheService = null,
            ForwardNearFieldDirectSourceAttachmentBinding?
                nearFieldDirectSourceBinding = null,
            Func<bool>? nearFieldDirectSourceRuntimeAvailable = null,
            ForwardGiCausticReceiverAttachmentBinding?
                giCausticReceiverBinding = null,
            Func<bool>? giCausticRuntimeAvailable = null,
            ForwardHybridReflectionReceiverAttachmentBinding?
                hybridReflectionReceiverBinding = null,
            SimpleDdgiReceiverFeedbackVulkanRuntime?
                simpleDdgiReceiverFeedbackRuntime = null)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _skyboxPipeline = skyboxPipeline;
            _giPipelineCacheService = giPipelineCacheService;
            _nearFieldDirectSourceBinding = nearFieldDirectSourceBinding;
            _nearFieldDirectSourceRuntimeAvailable =
                nearFieldDirectSourceRuntimeAvailable;
            _giCausticReceiverBinding = giCausticReceiverBinding;
            _giCausticRuntimeAvailable = giCausticRuntimeAvailable;
            _hybridReflectionReceiverBinding = hybridReflectionReceiverBinding;
            _simpleDdgiReceiverFeedbackRuntime =
                simpleDdgiReceiverFeedbackRuntime;
            for (int i = 0; i < FramesInFlight; i++)
            {
                _simpleDdgiReceiverCacheBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherBuffers[i] = BufferHandle.Invalid;
            }
        }

        /// <summary>
        /// Current C5 source capability observed by this pass.  It is a
        /// fail-closed status for the eventual renderer integration, not a
        /// claim that C5 tracing/compositing is active.
        /// </summary>
        public string NearFieldDirectSourceFailureReason { get; private set; } =
            "near-field-direct-source-disabled";
        public string GiCausticReceiverFailureReason { get; private set; } =
            "caustic-forward-receiver-disabled";
        public string HybridReflectionReceiverFailureReason { get; private set; } =
            "hybrid-reflection-receiver-disabled";

        /// <summary>
        /// Publishes the source attachments and extent-bound V12 contract for
        /// a newly committed C5 generation. The renderer calls this only at a
        /// frame boundary while the old generation is no longer recordable.
        /// </summary>
        internal void PublishNearFieldDirectSourceGeneration(
            ForwardNearFieldDirectSourceAttachmentBinding? binding)
        {
            _nearFieldDirectSourceBinding = binding;
            NearFieldDirectSourceFailureReason = binding is null
                ? "near-field-direct-source-generation-unavailable"
                : "near-field-direct-source-generation-published";
        }

        public override void Initialize()
        {
            if (_bufferManager == null)
                return;

            try
            {
                _simpleDdgiReceiverCacheEntryPointName =
                    SilkMarshal.StringToPtr("main");
                if (_giPipelineCacheService != null)
                {
                    _simpleDdgiReceiverCachePipelineCache =
                        _giPipelineCacheService.Cache;
                }
                else
                {
                    CreateSimpleDdgiReceiverCachePipelineCache();
                }
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
            int frameIndex,
            SceneRenderingData sceneData,
            in ReflectionCaptureViewContext view,
            ImageView colorView,
            ImageView depthView)
        {
            if (colorView.Handle == 0 || depthView.Handle == 0)
                throw new InvalidOperationException("Reflection capture attachments are unavailable.");

            PrepareReflectionReceiverFeedbackFace(frameIndex, sceneData, view);

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

                // The skybox uses a distinct pipeline layout. Rebind both
                // bindless sets against the mesh layout before resuming mesh
                // draws; descriptor-set compatibility is tracked from set zero
                // and cannot be inherited across these layouts.
                BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

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
                if (_simpleDdgiReflectionFeedbackRequiredForCurrentView)
                {
                    _reflectionFeedbackFacesRecordedForCurrentBatch = checked(
                        _reflectionFeedbackFacesRecordedForCurrentBatch + 1);
                }
            }
            finally
            {
                _recordingReflectionCapture = false;
                _reflectionCaptureIncludesDdgi = false;
                _simpleDdgiReflectionFeedbackRequiredForCurrentView = false;
                _reflectionFeedbackCubemapArrayLayer = 0;
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

        private void PrepareReflectionReceiverFeedbackFace(
            int frameIndex,
            SceneRenderingData sceneData,
            in ReflectionCaptureViewContext view)
        {
            _simpleDdgiReflectionFeedbackRequiredForCurrentView = false;
            _reflectionFeedbackCubemapArrayLayer = 0;
            SimpleDdgiReceiverFeedbackVulkanRuntime? runtime =
                _simpleDdgiReceiverFeedbackRuntime;
            if (runtime is null ||
                !runtime.IsPendingOwnedProducerRequired(
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.ReflectionCapture))
            {
                return;
            }

            string? unavailableReason = null;
            bool hasOpaqueDraws = sceneData.SimpleOpaqueMeshletCount > 0 ||
                sceneData.SimpleNormalOpaqueMeshletCount > 0 ||
                sceneData.FullOpaqueMeshletCount > 0;
            bool hasFoliageDraws = sceneData.FoliageClusterCount > 0 &&
                sceneData.FoliageDrawBufferBytes > 0;
            if (!view.IncludesDdgi)
            {
                unavailableReason =
                    "receiver-feedback-reflection-capture-ddgi-disabled";
            }
            else if (!TryComputeReflectionFeedbackTileNamespace(
                         view.CubemapArrayLayer,
                         view.Resolution,
                         out _,
                         out unavailableReason))
            {
                // The helper supplies the stable reason.
            }
            else if (hasOpaqueDraws &&
                     !_meshPipeline.AlphaMaskReceiverFeedbackPipelinesAvailable)
            {
                unavailableReason =
                    "receiver-feedback-reflection-capture-opaque-pipelines-unavailable";
            }
            else if (hasFoliageDraws &&
                     (_foliagePipeline is null ||
                      !_foliagePipeline.ReceiverFeedbackPipelinesAvailable))
            {
                unavailableReason =
                    "receiver-feedback-reflection-capture-foliage-pipelines-unavailable";
            }
            else if (sceneData.DebugViewMode != 0u ||
                     sceneData.AmbientOcclusionDebugView !=
                         AmbientOcclusionDebugView.None ||
                     sceneData.TransparencyDebugView !=
                         TransparencyDebugView.None ||
                     sceneData.AnimationDebugView != AnimationDebugView.None ||
                     sceneData.ReflectionDebugView != ReflectionDebugView.None ||
                     sceneData.FoliageDebugView != 0u ||
                     _settings.GlobalIllumination.DebugView !=
                         GlobalIlluminationDebugView.None ||
                     _settings.Environment.DebugView != EnvironmentDebugView.None)
            {
                unavailableReason =
                    "receiver-feedback-reflection-capture-debug-view-active";
            }
            else if (_reflectionFeedbackBatchFrameIndex >= 0 &&
                     _reflectionFeedbackBatchFrameIndex != frameIndex)
            {
                unavailableReason =
                    "receiver-feedback-reflection-capture-batch-frame-mismatch";
            }

            if (unavailableReason is not null)
            {
                runtime.AbortCapture(unavailableReason);
                return;
            }

            if (_reflectionFeedbackBatchFrameIndex < 0)
            {
                _reflectionFeedbackBatchFrameIndex = frameIndex;
                _reflectionFeedbackFacesRecordedForCurrentBatch = 0;
            }
            _simpleDdgiReflectionFeedbackRequiredForCurrentView = true;
            _reflectionFeedbackCubemapArrayLayer = view.CubemapArrayLayer;
        }

        internal void CompleteReflectionReceiverFeedbackBatch(
            CommandBuffer commandBuffer,
            int frameIndex,
            int recordedFaceCount,
            bool batchSucceeded)
        {
            try
            {
                SimpleDdgiReceiverFeedbackVulkanRuntime? runtime =
                    _simpleDdgiReceiverFeedbackRuntime;
                if (runtime is null ||
                    !runtime.IsPendingOwnedProducerRequired(
                        frameIndex,
                        SimpleDdgiReceiverFeedbackProducer.ReflectionCapture))
                {
                    return;
                }

                string? failureReason = null;
                if (!batchSucceeded)
                {
                    failureReason =
                        "receiver-feedback-reflection-capture-batch-failed";
                }
                else if (recordedFaceCount <= 0)
                {
                    failureReason =
                        "receiver-feedback-reflection-capture-recorded-no-faces";
                }
                else if (_reflectionFeedbackBatchFrameIndex != frameIndex ||
                         _reflectionFeedbackFacesRecordedForCurrentBatch !=
                         recordedFaceCount)
                {
                    failureReason =
                        "receiver-feedback-reflection-capture-face-count-mismatch";
                }

                if (failureReason is not null)
                {
                    runtime.AbortCapture(failureReason);
                    return;
                }

                if (!runtime.TryRecordOwnedProducerCompletion(
                        commandBuffer,
                        frameIndex,
                        SimpleDdgiReceiverFeedbackProducer.ReflectionCapture,
                        out string completionReason))
                {
                    runtime.AbortCapture(
                        "receiver-feedback-reflection-capture-completion-failed:" +
                        completionReason);
                }
            }
            finally
            {
                _simpleDdgiReflectionFeedbackRequiredForCurrentView = false;
                _reflectionFeedbackCubemapArrayLayer = 0;
                _reflectionFeedbackBatchFrameIndex = -1;
                _reflectionFeedbackFacesRecordedForCurrentBatch = 0;
            }
        }

        internal static bool TryComputeReflectionFeedbackTileNamespace(
            int cubemapArrayLayer,
            uint resolution,
            out uint tileNamespaceBase,
            out string reason)
        {
            tileNamespaceBase = 0u;
            if ((uint)cubemapArrayLayer >
                GPUForwardPushConstants.MaximumReflectionCaptureLayer)
            {
                reason =
                    "receiver-feedback-reflection-capture-layer-out-of-range";
                return false;
            }
            if (resolution == 0u)
            {
                reason =
                    "receiver-feedback-reflection-capture-resolution-zero";
                return false;
            }

            ulong tileResolution = 1UL +
                ((ulong)resolution - 1UL) /
                SimpleDdgiReceiverGatherScale;
            ulong faceTileCount = checked(tileResolution * tileResolution);
            if (faceTileCount == 0u || faceTileCount > uint.MaxValue ||
                (ulong)cubemapArrayLayer > uint.MaxValue / faceTileCount)
            {
                reason =
                    "receiver-feedback-reflection-capture-tile-namespace-overflow";
                return false;
            }

            ulong baseValue = (ulong)cubemapArrayLayer * faceTileCount;
            if (baseValue > uint.MaxValue - (faceTileCount - 1u))
            {
                reason =
                    "receiver-feedback-reflection-capture-tile-namespace-overflow";
                return false;
            }

            tileNamespaceBase = checked((uint)baseValue);
            reason = "valid";
            return true;
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
            _hybridReflectionReceiverEnabledForCurrentView = false;
            sceneData.GiCausticReceiverPayloadCompleted = false;
            sceneData.GiCausticReceiverPayloadFrameSerial = 0UL;
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

            bool receiverFeedbackCaptureOpen = false;
            bool exactOpaqueProducerCompleted = false;
            SimpleDdgiReceiverFeedbackCaptureProducerContract
                receiverFeedbackProducer =
                    SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
            try
            {
            _simpleDdgiReceiverCacheAvailableForCurrentView = false;
            _simpleDdgiReceiverCacheConsumedForCurrentView = false;
            _forwardGiDisabledBenchmarkPipelineUsedForCurrentView = false;
            _forwardGiExactGatherUsedForCurrentView = false;
            _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView = false;
            _simpleDdgiFoliageFeedbackRequiredForCurrentView = false;
            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            bool materialTransportProvenanceEnabled =
                ShouldWriteMaterialTransportProvenance();
            bool nearFieldDirectSourceEnabled = TryGetNearFieldDirectSourceBinding(
                sceneData,
                renderExtent,
                materialTransportProvenanceEnabled,
                out ForwardNearFieldDirectSourceAttachmentBinding?
                    nearFieldDirectSourceBinding);
            ForwardGiCausticReceiverAttachmentBinding?
                giCausticReceiverBinding = null;
            bool giCausticReceiverEnabled =
                TryGetGiCausticReceiverBinding(
                    sceneData,
                    renderExtent,
                    materialTransportProvenanceEnabled,
                    out giCausticReceiverBinding);
            bool hybridReflectionReceiverEnabled =
                TryGetHybridReflectionReceiverBinding(
                    sceneData,
                    renderExtent,
                    materialTransportProvenanceEnabled,
                    out ForwardHybridReflectionReceiverAttachmentBinding?
                        hybridReflectionReceiverBinding);
            if (!hybridReflectionReceiverEnabled &&
                sceneData.EffectiveReflectionMode is
                    (ReflectionMode.StaticProbesAndSsr or
                     ReflectionMode.HybridRayQuery))
            {
                // Fail closed before selecting the opaque pipeline: the
                // ordinary forward variants retain local-probe/environment
                // specular, while the deferred chain observes the demotion
                // and does not consume an unwritten payload.
                sceneData.EffectiveReflectionMode = ReflectionMode.StaticProbes;
                sceneData.ReflectionMode = ReflectionMode.StaticProbes;
                sceneData.ReflectionFallbackReason =
                    ReflectionFallbackReason.ReceiverPayloadUnavailable;
                sceneData.ReflectionFallbackDetail =
                    HybridReflectionReceiverFailureReason;
                sceneData.HybridReflectionPassEnabled = false;
            }
            _hybridReflectionReceiverEnabledForCurrentView =
                hybridReflectionReceiverEnabled;
            if (nearFieldDirectSourceEnabled && giCausticReceiverEnabled &&
                !_meshPipeline.CombinedAdvancedGiAttachmentEnabled)
            {
                // Keep the C5 source contract live if the optional combined
                // four-target pipeline failed to materialize. C4 remains
                // independently admitted and retries on the next clean
                // renderer lifetime, but cannot consume an incomplete MRT
                // payload from this frame.
                giCausticReceiverEnabled = false;
                giCausticReceiverBinding = null;
                GiCausticReceiverFailureReason =
                    _meshPipeline.CombinedAdvancedGiFailureReason;
            }
            if (hybridReflectionReceiverEnabled)
            {
                bool meshVariantsReady =
                    _meshPipeline.TryPrepareHybridReflectionPipelines(
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled);
                bool foliageVariantsReady =
                    _foliagePipeline is null ||
                    sceneData.FoliageClusterCount <= 0 ||
                    sceneData.FoliageDrawBufferBytes == 0 ||
                    _foliagePipeline.TryPrepareHybridReflectionPipelines(
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled);
                if (!meshVariantsReady || !foliageVariantsReady)
                {
                    hybridReflectionReceiverEnabled = false;
                    hybridReflectionReceiverBinding = null;
                    _hybridReflectionReceiverEnabledForCurrentView = false;
                    sceneData.EffectiveReflectionMode = ReflectionMode.StaticProbes;
                    sceneData.ReflectionMode = ReflectionMode.StaticProbes;
                    sceneData.ReflectionFallbackReason =
                        ReflectionFallbackReason.ReceiverPayloadUnavailable;
                    HybridReflectionReceiverFailureReason = meshVariantsReady
                        ? _foliagePipeline!.HybridReflectionPipelineFailureReason
                        : _meshPipeline.HybridReflectionFailureReason;
                    sceneData.ReflectionFallbackDetail =
                        HybridReflectionReceiverFailureReason;
                    sceneData.HybridReflectionPassEnabled = false;
                }
            }
            bool receiverGatherDispatchable =
                ShouldDispatchSimpleDdgiReceiverCache(
                    frameIndex,
                    sceneData,
                    renderExtent,
                    materialTransportProvenanceEnabled);
            // C5 adds producer MRTs but does not change SceneColor ownership.
            // High-quality tiers deliberately keep exact per-fragment DDGI:
            // the coarse cache has only depth and cannot retain normal/material
            // discontinuities. B1 may still request this gather independently
            // for scheduler feedback without making it a color source.
            bool receiverCacheEligible = ShouldConsumeSimpleDdgiReceiverCache(
                    _settings.QualityPreset,
                    _settings.Diagnostics
                        .ForceForwardGiReceiverCacheForBenchmark) &&
                !giCausticReceiverEnabled &&
                receiverGatherDispatchable;
            // B1 owns an exact opaque receiver producer in this compute
            // gather. C4/C5 attachment output changes how Forward+ consumes
            // GI, but must not suppress an independently enabled B1 capture.
            bool receiverGatherRequired = receiverCacheEligible ||
                (receiverGatherDispatchable &&
                 _simpleDdgiReceiverFeedbackRuntime?.IsOwnedCaptureReady == true);
            if (sceneData.GlobalIlluminationDdgiActive != 0 ||
                sceneData.SimpleDdgiActive != 0)
            {
                PublishComputeStorageToFragment(
                    cmd,
                    includeComputeReceiver: receiverGatherRequired);
            }

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            if (receiverGatherRequired)
            {
                receiverFeedbackCaptureOpen =
                    TryBeginSimpleDdgiReceiverFeedbackCapture(
                        cmd,
                        frameIndex,
                        sceneData,
                        out receiverFeedbackProducer);
                timestamps?.BeginPass(
                    cmd,
                    frameIndex,
                    "SimpleDdgiReceiverCachePass");
                try
                {
                    bool receiverGatherRecorded =
                        DispatchSimpleDdgiReceiverCache(
                            cmd,
                            frameIndex,
                            sceneData,
                            renderExtent,
                            receiverFeedbackProducer);
                    _simpleDdgiReceiverCacheAvailableForCurrentView =
                        receiverCacheEligible && receiverGatherRecorded;
                    if (receiverFeedbackCaptureOpen &&
                        receiverGatherRecorded)
                    {
                        exactOpaqueProducerCompleted =
                            _simpleDdgiReceiverFeedbackRuntime!
                                .TryRecordOwnedProducerCompletion(
                                    cmd,
                                    frameIndex,
                                    SimpleDdgiReceiverFeedbackProducer.OpaqueForward,
                                    out string completionReason);
                        if (!exactOpaqueProducerCompleted)
                        {
                            _simpleDdgiReceiverFeedbackRuntime.AbortCapture(
                                completionReason);
                            receiverFeedbackCaptureOpen = false;
                        }
                    }
                }
                finally
                {
                    timestamps?.EndPass(cmd, frameIndex);
                }
            }

            if (receiverFeedbackCaptureOpen &&
                _simpleDdgiReceiverFeedbackRuntime!
                    .IsPendingOwnedProducerRequired(
                        frameIndex,
                        SimpleDdgiReceiverFeedbackProducer
                            .AlphaMaskOrFoliage))
            {
                bool maskedFeedbackRequired =
                    sceneData.MaskedMeshletCount > 0;
                bool foliageFeedbackRequired =
                    sceneData.FoliageClusterCount > 0;
                string? unavailableReason = null;
                if (maskedFeedbackRequired &&
                    !_meshPipeline.AlphaMaskReceiverFeedbackPipelinesAvailable)
                {
                    unavailableReason =
                        "receiver-feedback-alpha-mask-pipelines-unavailable";
                }
                else if (foliageFeedbackRequired &&
                    (_foliagePipeline is null ||
                     !_foliagePipeline.ReceiverFeedbackPipelinesAvailable ||
                     sceneData.FoliageDrawBufferBytes == 0))
                {
                    unavailableReason =
                        "receiver-feedback-foliage-pipelines-or-draws-unavailable";
                }

                if (unavailableReason is not null)
                {
                    _simpleDdgiReceiverFeedbackRuntime.AbortCapture(
                        unavailableReason);
                    receiverFeedbackCaptureOpen = false;
                }
                else
                {
                    _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView =
                        maskedFeedbackRequired;
                    _simpleDdgiFoliageFeedbackRequiredForCurrentView =
                        foliageFeedbackRequired;
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
            if (nearFieldDirectSourceEnabled)
            {
                foreach (RenderTarget target in nearFieldDirectSourceBinding!.Targets)
                    target.TransitionToColorAttachment(cmd);
            }
            if (giCausticReceiverEnabled)
            {
                giCausticReceiverBinding!.ReceiverPayload
                    .TransitionToColorAttachment(cmd);
            }
            if (hybridReflectionReceiverEnabled)
            {
                hybridReflectionReceiverBinding!.ReceiverPayload
                    .TransitionToColorAttachment(cmd);
            }
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
            var colorAttachments = stackalloc RenderingAttachmentInfo[5];
            colorAttachments[0] = colorAttachment;
            if (nearFieldDirectSourceEnabled && giCausticReceiverEnabled)
            {
                // Combined ABI: SceneColor, C4 receiver, C5 direct source,
                // C5 receiver. All auxiliary values clear to invalid/zero so
                // omitted pixels can never decode as valid transport input.
                colorAttachments[1] = ColorAttachment(
                    giCausticReceiverBinding!.ReceiverPayload.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
                colorAttachments[2] = ColorAttachment(
                    nearFieldDirectSourceBinding!.DirectSource.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
                colorAttachments[3] = ColorAttachment(
                    nearFieldDirectSourceBinding.ReceiverPayload.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            }
            else if (nearFieldDirectSourceEnabled)
            {
                // Clear both auxiliary attachments. A background or omitted
                // draw therefore decodes as invalid, never as plausible
                // receiver geometry or radiance.
                colorAttachments[1] = ColorAttachment(
                    nearFieldDirectSourceBinding!.DirectSource.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
                colorAttachments[2] = ColorAttachment(
                    nearFieldDirectSourceBinding.ReceiverPayload.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            }
            else if (giCausticReceiverEnabled)
            {
                // A cleared uvec4 payload is invalid by ABI. Omitted pixels,
                // foliage, transparency, and backgrounds therefore cannot be
                // mistaken for C4 diffuse receivers.
                colorAttachments[1] = ColorAttachment(
                    giCausticReceiverBinding!.ReceiverPayload.View,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            }
            else if (materialTransportProvenanceEnabled)
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
            if (hybridReflectionReceiverEnabled)
            {
                int hybridAttachmentIndex = nearFieldDirectSourceEnabled &&
                                            giCausticReceiverEnabled
                    ? 4
                    : nearFieldDirectSourceEnabled
                        ? 3
                        : giCausticReceiverEnabled ? 2 : 1;
                colorAttachments[hybridAttachmentIndex] = ColorAttachment(
                    hybridReflectionReceiverBinding!.ReceiverPayload.View,
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
                        materialTransportProvenanceEnabled,
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled,
                        hybridReflectionReceiverEnabled),
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
                    DrawForwardVisibilityBucketsIndirect(
                        cmd,
                        sceneData,
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled);
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
                            sceneData,
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                    }
                    else
                    {
                        sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                        sceneData.SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
                        UpdateCompactedForwardVariantDiagnostics(sceneData);
                        UpdateCompactedForwardShadowDiagnostics(sceneData, compactedDrawCapacity);
                        DrawCompactedForwardBucketsDirect(
                            cmd,
                            sceneData,
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
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
                        sceneData,
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled);
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
                    BindlessIndex.MeshletDrawBufferBase,
                    nearFieldDirectSourceEnabled,
                    giCausticReceiverEnabled);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    variantSelection.UseSimpleGlobalIblPipeline
                        ? _meshPipeline.ForwardSimpleFullInputGlobalIblPipeline
                        : _meshPipeline.ForwardFullMaterialPipeline,
                    sceneData.SimpleNormalOpaqueMeshletCount,
                    BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase,
                    nearFieldDirectSourceEnabled,
                    giCausticReceiverEnabled);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    _meshPipeline.ForwardFullMaterialPipeline,
                    sceneData.FullOpaqueMeshletCount,
                    BindlessIndex.FullOpaqueMeshletDrawBufferBase,
                    nearFieldDirectSourceEnabled,
                    giCausticReceiverEnabled);
            }

            if (nearFieldDirectSourceEnabled)
            {
                bool foliageAdvancedGiWritten = DrawFoliageForward(
                    cmd,
                    sceneData,
                    nearFieldDirectSource: true,
                    combinedAdvancedGi: giCausticReceiverEnabled);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
                if (!foliageAdvancedGiWritten)
                {
                    DrawFoliageWithoutNearFieldDirectSource(
                        cmd,
                        sceneData,
                        renderExtent);
                }
            }
            else if (giCausticReceiverEnabled)
            {
                bool foliageReceiverWritten =
                    _hybridReflectionReceiverEnabledForCurrentView &&
                    DrawFoliageForward(
                        cmd,
                        sceneData,
                        combinedAdvancedGi: true);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
                if (!foliageReceiverWritten)
                {
                    // C4 alone has no foliage transport contract. Preserve
                    // SceneColor and leave its cleared receiver payload
                    // invalid when a hybrid foliage variant is unavailable.
                    DrawFoliageWithoutNearFieldDirectSource(
                        cmd,
                        sceneData,
                        renderExtent);
                }
            }
            else
            {
                DrawFoliageForward(cmd, sceneData);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }
            if (hybridReflectionReceiverEnabled)
            {
                hybridReflectionReceiverBinding!.ReceiverPayload
                    .TransitionToShaderRead(cmd);
            }
            if (giCausticReceiverEnabled)
            {
                sceneData.GiCausticReceiverPayloadCompleted = true;
                sceneData.GiCausticReceiverPayloadFrameSerial =
                    sceneData.DdgiFrameSerial;
            }
            bool exactAlphaProducerRequired =
                _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView ||
                _simpleDdgiFoliageFeedbackRequiredForCurrentView;
            if (receiverFeedbackCaptureOpen && exactAlphaProducerRequired &&
                !_simpleDdgiReceiverFeedbackRuntime!
                    .TryRecordOwnedProducerCompletion(
                        cmd,
                        frameIndex,
                        SimpleDdgiReceiverFeedbackProducer
                            .AlphaMaskOrFoliage,
                        out string alphaCompletionReason))
            {
                _simpleDdgiReceiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-alpha-foliage-completion-failed:" +
                    alphaCompletionReason);
                receiverFeedbackCaptureOpen = false;
            }
            _simpleDdgiReceiverCacheAvailableForCurrentView = false;
            _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView = false;
            _simpleDdgiFoliageFeedbackRequiredForCurrentView = false;
            if (receiverFeedbackCaptureOpen)
            {
                if (!exactOpaqueProducerCompleted)
                {
                    _simpleDdgiReceiverFeedbackRuntime!.AbortCapture(
                        "receiver-feedback-opaque-producer-did-not-complete");
                }
                // A successful producer transaction is intentionally left
                // open. VulkanRenderer finalizes it only after the late
                // transparent/particle/fog/capture producer boundary.
                receiverFeedbackCaptureOpen = false;
            }
            }
            finally
            {
                if (receiverFeedbackCaptureOpen)
                {
                    _simpleDdgiReceiverFeedbackRuntime?.AbortCapture(
                        "receiver-feedback-forward-pass-recording-aborted");
                }
                _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView = false;
                _simpleDdgiFoliageFeedbackRequiredForCurrentView = false;
            }
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
            bool deferredReflection = sceneData.EffectiveReflectionMode is
                ReflectionMode.StaticProbesAndSsr or
                ReflectionMode.HybridRayQuery;
            bool requiresLocalProbeEvaluation = RequiresLocalReflectionProbeEvaluation(sceneData);
            bool forceFullForDebug = !deferredReflection &&
                sceneData.ReflectionDebugView != ReflectionDebugView.None;
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

            if (sceneData.EffectiveReflectionMode is
                ReflectionMode.StaticProbesAndSsr or
                ReflectionMode.HybridRayQuery)
            {
                return false;
            }

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
            int meshletDrawBufferBaseIndex,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            if (meshletCount <= 0)
                return;

            bool receiverCacheEnabled = !giCausticReceiverEnabled &&
                !_hybridReflectionReceiverEnabledForCurrentView &&
                !_simpleDdgiAlphaMaskFeedbackRequiredForCurrentView &&
                !_simpleDdgiReflectionFeedbackRequiredForCurrentView &&
                ShouldUseSimpleDdgiReceiverCacheForDraw();
            bool disabledBenchmarkPipeline =
                ShouldUseForwardGiDisabledBenchmarkPipeline();
            if (_hybridReflectionReceiverEnabledForCurrentView &&
                !_recordingReflectionCapture)
            {
                if (!_meshPipeline.TryResolveHybridReflectionPipeline(
                        pipeline,
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled,
                        out Silk.NET.Vulkan.Pipeline hybridPipeline))
                {
                    throw new InvalidOperationException(
                        "The hybrid reflection pass selected an opaque pipeline without a matching receiver MRT variant.");
                }

                pipeline = hybridPipeline;
            }
            else if (nearFieldDirectSourceEnabled && giCausticReceiverEnabled)
            {
                if (!_meshPipeline.TryResolveCombinedAdvancedGiPipeline(
                        pipeline,
                        out Silk.NET.Vulkan.Pipeline combinedPipeline))
                {
                    throw new InvalidOperationException(
                        "The combined C4/C5 pass selected an opaque pipeline without a matching four-attachment semantic variant.");
                }

                pipeline = combinedPipeline;
            }
            else if (nearFieldDirectSourceEnabled)
            {
                if (!_meshPipeline.TryResolveNearFieldDirectSourcePipeline(
                        pipeline,
                        receiverCacheEnabled,
                        out Silk.NET.Vulkan.Pipeline nearFieldPipeline))
                {
                    throw new InvalidOperationException(
                        "The C5 direct-source pass selected an opaque pipeline without a matching semantic MRT variant.");
                }

                pipeline = nearFieldPipeline;
            }
            else if (giCausticReceiverEnabled)
            {
                if (!_meshPipeline.TryResolveGiCausticReceiverPipeline(
                        pipeline,
                        out Silk.NET.Vulkan.Pipeline causticPipeline))
                {
                    throw new InvalidOperationException(
                        "The C4 receiver pass selected an opaque pipeline without a matching semantic MRT variant.");
                }

                pipeline = causticPipeline;
            }
            else
            {
                pipeline = _meshPipeline.ResolveOpaqueSpecializedPipeline(
                    pipeline,
                    receiverCacheEnabled,
                    disabledBenchmarkPipeline,
                    _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView ||
                    _simpleDdgiReflectionFeedbackRequiredForCurrentView);
            }
            _simpleDdgiReceiverCacheConsumedForCurrentView |=
                receiverCacheEnabled && !disabledBenchmarkPipeline;
            _forwardGiDisabledBenchmarkPipelineUsedForCurrentView |=
                disabledBenchmarkPipeline;
            _forwardGiExactGatherUsedForCurrentView |=
                !disabledBenchmarkPipeline &&
                !receiverCacheEnabled &&
                sceneData.SimpleDdgiActive != 0 &&
                ShouldApplyGlobalIllumination(sceneData);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                // C5 consumes the exact temporal-sample bits. Receiver-cache
                // variants derive their row stride from ScreenDimensions, so
                // both paths can remain active without overloading this word.
                Time = nearFieldDirectSourceEnabled
                    ? BitConverter.UInt32BitsToSingle(sceneData.TemporalSampleIndex)
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
                        !nearFieldDirectSourceEnabled &&
                        !giCausticReceiverEnabled &&
                        ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCacheEnabled),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    _recordingReflectionCapture,
                    _reflectionFeedbackCubemapArrayLayer)
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
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
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
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.FullOpaqueMeshletCount),
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
        }

        private void DrawForwardVisibilityBucketsIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
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
                sceneData.ForwardVisibilityIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardCompactedSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.ForwardVisibilitySimpleNormalCapacity),
                BindlessIndex.ForwardVisibleSimpleNormalOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                _meshPipeline.ForwardCompactedPipeline,
                Math.Max(0, sceneData.ForwardVisibilityFullCapacity),
                BindlessIndex.ForwardVisibleFullOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
        }

        private void DrawCompactedForwardBucketsDirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            bool useSimpleGlobalIblPipeline = ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardSimpleGlobalIblPipeline
                    : _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.SimpleOpaqueMeshletCount),
                BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? _meshPipeline.ForwardSimpleFullInputGlobalIblPipeline
                    : _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucket(
                cmd,
                sceneData,
                _meshPipeline.ForwardFullMaterialPipeline,
                Math.Max(0, sceneData.FullOpaqueMeshletCount),
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
        }

        private void DrawForwardBucketIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectOffset,
            BufferHandle indirectBufferHandle,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            if (meshletCapacity <= 0 || _bufferManager == null)
                return;

            bool receiverCacheEnabled = !giCausticReceiverEnabled &&
                !_hybridReflectionReceiverEnabledForCurrentView &&
                !_simpleDdgiAlphaMaskFeedbackRequiredForCurrentView &&
                !_simpleDdgiReflectionFeedbackRequiredForCurrentView &&
                ShouldUseSimpleDdgiReceiverCacheForDraw();
            bool disabledBenchmarkPipeline =
                ShouldUseForwardGiDisabledBenchmarkPipeline();
            if (_hybridReflectionReceiverEnabledForCurrentView &&
                !_recordingReflectionCapture)
            {
                if (!_meshPipeline.TryResolveHybridReflectionPipeline(
                        pipeline,
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled,
                        out Silk.NET.Vulkan.Pipeline hybridPipeline))
                {
                    throw new InvalidOperationException(
                        "The hybrid reflection pass selected an indirect opaque pipeline without a matching receiver MRT variant.");
                }

                pipeline = hybridPipeline;
            }
            else if (nearFieldDirectSourceEnabled && giCausticReceiverEnabled)
            {
                if (!_meshPipeline.TryResolveCombinedAdvancedGiPipeline(
                        pipeline,
                        out Silk.NET.Vulkan.Pipeline combinedPipeline))
                {
                    throw new InvalidOperationException(
                        "The combined C4/C5 pass selected an indirect opaque pipeline without a matching four-attachment semantic variant.");
                }

                pipeline = combinedPipeline;
            }
            else if (nearFieldDirectSourceEnabled)
            {
                if (!_meshPipeline.TryResolveNearFieldDirectSourcePipeline(
                        pipeline,
                        receiverCacheEnabled,
                        out Silk.NET.Vulkan.Pipeline nearFieldPipeline))
                {
                    throw new InvalidOperationException(
                        "The C5 direct-source pass selected an indirect opaque pipeline without a matching semantic MRT variant.");
                }

                pipeline = nearFieldPipeline;
            }
            else if (giCausticReceiverEnabled)
            {
                if (!_meshPipeline.TryResolveGiCausticReceiverPipeline(
                        pipeline,
                        out Silk.NET.Vulkan.Pipeline causticPipeline))
                {
                    throw new InvalidOperationException(
                        "The C4 receiver pass selected an indirect opaque pipeline without a matching semantic MRT variant.");
                }

                pipeline = causticPipeline;
            }
            else
            {
                pipeline = _meshPipeline.ResolveOpaqueSpecializedPipeline(
                    pipeline,
                    receiverCacheEnabled,
                    disabledBenchmarkPipeline,
                    _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView ||
                    _simpleDdgiReflectionFeedbackRequiredForCurrentView);
            }
            _simpleDdgiReceiverCacheConsumedForCurrentView |=
                receiverCacheEnabled && !disabledBenchmarkPipeline;
            _forwardGiDisabledBenchmarkPipelineUsedForCurrentView |=
                disabledBenchmarkPipeline;
            _forwardGiExactGatherUsedForCurrentView |=
                !disabledBenchmarkPipeline &&
                !receiverCacheEnabled &&
                sceneData.SimpleDdgiActive != 0 &&
                ShouldApplyGlobalIllumination(sceneData);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = nearFieldDirectSourceEnabled
                    ? BitConverter.UInt32BitsToSingle(sceneData.TemporalSampleIndex)
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
                        !nearFieldDirectSourceEnabled &&
                        !giCausticReceiverEnabled &&
                        ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCacheEnabled),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    _recordingReflectionCapture,
                    _reflectionFeedbackCubemapArrayLayer)
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
            bool directionalReceiverActive =
                _settings.GlobalIllumination
                    .EffectiveSimpleDdgiDirectionalRadianceMode !=
                    SimpleDdgiDirectionalRadianceMode.Off &&
                _settings.GlobalIllumination
                    .EffectiveSimpleDdgiGlossyTransportMode !=
                    SimpleDdgiGlossyTransportMode.Off;
            if (_recordingReflectionCapture || materialTransportProvenanceEnabled ||
                directionalReceiverActive ||
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
            Extent2D renderExtent,
            in SimpleDdgiReceiverFeedbackCaptureProducerContract
                receiverFeedbackProducer)
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

            // First evaluate one exact structured gather per receiver lattice
            // block. This compact lattice carries representative depth only
            // until the following compute resolve.
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                receiverFeedbackProducer.IsAvailable
                    ? _simpleDdgiReceiverFeedbackPipeline
                    : _simpleDdgiReceiverCachePipeline);
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
                ReceiverScale = SimpleDdgiReceiverGatherScale,
                FeedbackControlOffsetWords = receiverFeedbackProducer.IsAvailable
                    ? receiverFeedbackProducer.CandidateControlOffsetWords
                    : 0u,
                FeedbackSamplePeriod = receiverFeedbackProducer.IsAvailable
                    ? receiverFeedbackProducer.ScreenSamplingPeriod
                    : 0u,
                FeedbackSamplePhase = receiverFeedbackProducer.IsAvailable
                    ? receiverFeedbackProducer.ScreenSamplingPhase
                    : 0u,
                FeedbackMaximumOwnersPerTile = receiverFeedbackProducer.IsAvailable
                    ? receiverFeedbackProducer.MaximumUniqueGatherOwnersPerTile
                    : 0u
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
            // packed FP16 buffer. Invalid lattice cells are repaired only from
            // nearby occupied cells, then current receiver depth rejects
            // incompatible bilinear corners. Empty tiles and unrelated surfaces
            // therefore cannot darken or illuminate receiver silhouettes.
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
                            renderExtent),
                    DepthTextureIndex = BindlessIndex.DepthTexture
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

        private bool TryBeginSimpleDdgiReceiverFeedbackCapture(
            CommandBuffer commandBuffer,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            out SimpleDdgiReceiverFeedbackCaptureProducerContract producer)
        {
            producer = SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
            SimpleDdgiReceiverFeedbackVulkanRuntime? runtime =
                _simpleDdgiReceiverFeedbackRuntime;
            if (runtime is null || !runtime.IsOwnedCaptureReady ||
                sceneData.DdgiFrameSerial == ulong.MaxValue)
            {
                return false;
            }

            try
            {
                EnsureSimpleDdgiReceiverFeedbackPipeline();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    "Simple-DDGI exact receiver-feedback producer pipeline " +
                    $"unavailable: {exception.GetType().Name}: {exception.Message}");
                return false;
            }

            if (_simpleDdgiReceiverFeedbackPipeline.Handle == 0)
                return false;

            int resizeCount = Math.Max(0, _renderTargets.ResizeCount);
            uint viewportGeneration = checked((uint)resizeCount + 1u);
            uint requiredProducerMask = ResolveRequiredReceiverFeedbackProducerMask(
                sceneData,
                _settings.Fog.Enabled &&
                _settings.Fog.Mode != FogMode.Disabled &&
                sceneData.AnimationDebugView == AnimationDebugView.None,
                _settings.Reflections.Enabled &&
                _settings.Reflections.CaptureIncludesDdgi &&
                _settings.Reflections.MaxProbeCapturesPerFrame > 0 &&
                _settings.Reflections.MaxProbeCaptureFacesPerFrame > 0);
            if (!runtime.TryBeginOwnedCapture(
                    commandBuffer,
                    frameIndex,
                    viewportGeneration,
                    sceneData.DdgiFrameSerial,
                    sceneData.SimpleDdgiVolumeResourceGeneration,
                    requiredProducerMask,
                    out producer,
                    out _))
            {
                producer =
                    SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
                return false;
            }

            return true;
        }

        internal static uint ResolveRequiredReceiverFeedbackProducerMask(
            SceneRenderingData sceneData,
            bool? fogEnabled = null,
            bool? reflectionCaptureFeedbackEnabled = null)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            uint mask = SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                SimpleDdgiReceiverFeedbackProducer.OpaqueForward);

            if (sceneData.MaskedMeshletCount > 0 ||
                sceneData.FoliageClusterCount > 0)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage);
            }
            if (sceneData.TransparentPassEnabled &&
                sceneData.TransparentReceiveGlobalIllumination &&
                sceneData.TransparentObjectCount > 0)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit);
            }
            if (sceneData.ParticleDdgiSampleCount > 0)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.Particles);
            }
            if (fogEnabled ?? sceneData.FogEnabled)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.Fog);
            }
            if ((reflectionCaptureFeedbackEnabled ?? true) &&
                sceneData.ReflectionProbeCapturesQueued >
                    sceneData.ReflectionProbeCapturesCompleted)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.ReflectionCapture);
            }
            if (sceneData.SimpleDdgiRefinement.Requested ||
                sceneData.SimpleDdgiRefinement.BaseFallbackBrickCount > 0)
            {
                mask |= SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.RefinementOrBaseFallback);
            }
            return mask;
        }

        private void EnsureSimpleDdgiReceiverFeedbackPipeline()
        {
            if (_simpleDdgiReceiverFeedbackPipeline.Handle != 0)
                return;
            _simpleDdgiReceiverFeedbackPipeline =
                CreateSimpleDdgiReceiverCachePipeline(
                    "ddgi_simple_receiver_cache_b1.comp.spv",
                    "Simple DDGI Exact Receiver Feedback Gather Pipeline");
        }

        private bool ShouldUseSimpleDdgiReceiverCacheForDraw()
        {
            return _simpleDdgiReceiverCacheAvailableForCurrentView &&
                   ShouldConsumeSimpleDdgiReceiverCache(
                       _settings.QualityPreset,
                       _settings.Diagnostics
                           .ForceForwardGiReceiverCacheForBenchmark) &&
                   !_settings.Diagnostics.ForceExactForwardGiGatherForBenchmark &&
                   !_recordingReflectionCapture &&
                   !ShouldWriteMaterialTransportProvenance();
        }

        internal static bool ShouldConsumeSimpleDdgiReceiverCache(
            RenderQualityPreset qualityPreset,
            bool forceForBenchmark)
        {
            // The cache samples a depth-derived representative once per 12x12
            // tile and therefore cannot meet high-tier surface-detail quality.
            // Keep it available for explicitly lower-cost presets and for the
            // existing controlled cache-vs-exact benchmark pair.
            return forceForBenchmark ||
                   qualityPreset is RenderQualityPreset.Low or
                       RenderQualityPreset.Medium;
        }

        internal bool CanConsumeSimpleDdgiReceiverCacheForCurrentView =>
            ShouldUseSimpleDdgiReceiverCacheForDraw();

        internal bool ConsumedSimpleDdgiReceiverCacheForCurrentView =>
            _simpleDdgiReceiverCacheConsumedForCurrentView;

        internal bool UsedForwardGiDisabledBenchmarkPipelineForCurrentView =>
            _forwardGiDisabledBenchmarkPipelineUsedForCurrentView;

        internal bool UsedForwardGiExactGatherForCurrentView =>
            _forwardGiExactGatherUsedForCurrentView;

        internal void BindSimpleDdgiReceiverCacheBuffer(
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

        private bool TryGetNearFieldDirectSourceBinding(
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool materialTransportProvenanceEnabled,
            out ForwardNearFieldDirectSourceAttachmentBinding? binding)
        {
            binding = null;
            if (_nearFieldDirectSourceRuntimeAvailable is not null &&
                !_nearFieldDirectSourceRuntimeAvailable())
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-runtime-not-effective";
                return false;
            }
            if (_nearFieldDirectSourceBinding == null)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-attachment-binding-unavailable";
                return false;
            }

            if (_recordingReflectionCapture)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-reflection-capture-unsupported";
                return false;
            }

            if (!_meshPipeline.NearFieldDirectSourceAttachmentEnabled)
            {
                NearFieldDirectSourceFailureReason =
                    _meshPipeline.NearFieldDirectSourceFailureReason;
                return false;
            }

            if (materialTransportProvenanceEnabled)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-material-transport-provenance-conflict";
                return false;
            }

            // Any forward-owned debug path can return before direct-light
            // evaluation. C5 views are different: forward remains on its normal
            // lighting path and the final C5 compute pass owns visualization.
            bool c5DebugView =
                SimpleDdgiNearFieldResidualDebugViewContract.IsC5View(
                    sceneData.NearFieldResidualDebugView);
            if (sceneData.DebugViewMode != 0u ||
                sceneData.AmbientOcclusionDebugView != AmbientOcclusionDebugView.None ||
                sceneData.TransparencyDebugView != TransparencyDebugView.None ||
                sceneData.AnimationDebugView != AnimationDebugView.None ||
                sceneData.ReflectionDebugView != ReflectionDebugView.None ||
                _settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None && !c5DebugView ||
                _settings.Environment.DebugView != EnvironmentDebugView.None)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-debug-view-active";
                return false;
            }

            if (_settings.Diagnostics.SuppressForwardGiGatherForBenchmark)
            {
                NearFieldDirectSourceFailureReason =
                    "near-field-direct-source-forward-gi-benchmark-control-active";
                return false;
            }

            if (!ForwardNearFieldDirectSourceContract.TryValidateAttachmentBinding(
                    _nearFieldDirectSourceBinding,
                    _meshPipeline.NearFieldDirectSourceConfiguration,
                    _renderTargets.SceneColor,
                    renderExtent,
                    out string failure))
            {
                NearFieldDirectSourceFailureReason = failure;
                return false;
            }

            binding = _nearFieldDirectSourceBinding;
            NearFieldDirectSourceFailureReason = "valid";
            return true;
        }

        private bool TryGetGiCausticReceiverBinding(
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool materialTransportProvenanceEnabled,
            out ForwardGiCausticReceiverAttachmentBinding? binding)
        {
            binding = null;
            if (_giCausticRuntimeAvailable is not null &&
                !_giCausticRuntimeAvailable())
            {
                GiCausticReceiverFailureReason =
                    "caustic-runtime-not-effective";
                return false;
            }
            if (_giCausticReceiverBinding is null)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-attachment-unavailable";
                return false;
            }
            if (_recordingReflectionCapture)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-reflection-capture-unsupported";
                return false;
            }
            if (!_meshPipeline.GiCausticReceiverAttachmentEnabled)
            {
                GiCausticReceiverFailureReason =
                    _meshPipeline.GiCausticReceiverFailureReason;
                return false;
            }
            if (materialTransportProvenanceEnabled)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-material-provenance-conflict";
                return false;
            }
            if (sceneData.DebugViewMode != 0u ||
                sceneData.AmbientOcclusionDebugView != AmbientOcclusionDebugView.None ||
                sceneData.TransparencyDebugView != TransparencyDebugView.None ||
                sceneData.AnimationDebugView != AnimationDebugView.None ||
                sceneData.ReflectionDebugView != ReflectionDebugView.None ||
                _settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None ||
                _settings.Environment.DebugView != EnvironmentDebugView.None)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-debug-view-active";
                return false;
            }
            if (_settings.Diagnostics.SuppressForwardGiGatherForBenchmark)
            {
                GiCausticReceiverFailureReason =
                    "caustic-forward-receiver-forward-gi-benchmark-control-active";
                return false;
            }
            if (!ForwardGiCausticReceiverContract.TryValidateAttachmentBinding(
                    _giCausticReceiverBinding,
                    _meshPipeline.GiCausticReceiverConfiguration,
                    _renderTargets.SceneColor,
                    renderExtent,
                    out string failure))
            {
                GiCausticReceiverFailureReason = failure;
                return false;
            }

            binding = _giCausticReceiverBinding;
            GiCausticReceiverFailureReason = "valid";
            return true;
        }

        private bool TryGetHybridReflectionReceiverBinding(
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool materialTransportProvenanceEnabled,
            out ForwardHybridReflectionReceiverAttachmentBinding? binding)
        {
            binding = null;
            if (sceneData.EffectiveReflectionMode is not
                (ReflectionMode.StaticProbesAndSsr or
                 ReflectionMode.HybridRayQuery))
            {
                HybridReflectionReceiverFailureReason =
                    "hybrid-reflection-mode-not-effective";
                return false;
            }
            if (_recordingReflectionCapture || materialTransportProvenanceEnabled)
            {
                HybridReflectionReceiverFailureReason = _recordingReflectionCapture
                    ? "hybrid-reflection-probe-capture-unsupported"
                    : "hybrid-reflection-material-provenance-conflict";
                return false;
            }
            if (_hybridReflectionReceiverBinding is null ||
                !_meshPipeline.HybridReflectionAttachmentEnabled)
            {
                HybridReflectionReceiverFailureReason =
                    _meshPipeline.HybridReflectionFailureReason;
                return false;
            }
            bool supportedReflectionDebug = sceneData.ReflectionDebugView is
                ReflectionDebugView.None or ReflectionDebugView.SsrMask or
                ReflectionDebugView.Confidence or
                ReflectionDebugView.SourceSelection or
                ReflectionDebugView.DetailBudget;
            if (!supportedReflectionDebug || sceneData.DebugViewMode != 0u ||
                sceneData.AmbientOcclusionDebugView != AmbientOcclusionDebugView.None ||
                sceneData.TransparencyDebugView != TransparencyDebugView.None ||
                sceneData.AnimationDebugView != AnimationDebugView.None ||
                _settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None ||
                _settings.Environment.DebugView != EnvironmentDebugView.None)
            {
                HybridReflectionReceiverFailureReason =
                    "hybrid-reflection-incompatible-debug-view-active";
                return false;
            }
            if (!ForwardHybridReflectionReceiverContract.TryValidateAttachmentBinding(
                    _hybridReflectionReceiverBinding,
                    _renderTargets.SceneColor,
                    renderExtent,
                    out string failure))
            {
                HybridReflectionReceiverFailureReason = failure;
                return false;
            }

            binding = _hybridReflectionReceiverBinding;
            HybridReflectionReceiverFailureReason = "valid";
            return true;
        }

        internal static bool ShouldApplyDdgi(
            Data.SceneRenderingData sceneData,
            GlobalIlluminationSettings gi)
        {
            return gi.EffectiveUseDdgi &&
                   sceneData.DdgiProbeCount > 0 &&
                   sceneData.DepthPrePassEnabled;
        }

        private bool DrawFoliageForward(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSource = false,
            bool combinedAdvancedGi = false)
        {
            if (_foliagePipeline == null || sceneData.FoliageClusterCount <= 0 || sceneData.FoliageDrawBufferBytes == 0)
                return true;

            bool receiverFeedback =
                _simpleDdgiFoliageFeedbackRequiredForCurrentView ||
                _simpleDdgiReflectionFeedbackRequiredForCurrentView;
            VkPipeline foliagePipeline = default;
            VkPipeline authoredFoliagePipeline = default;
            bool pipelinesResolved =
                _hybridReflectionReceiverEnabledForCurrentView &&
                !_recordingReflectionCapture
                    ? _foliagePipeline.TryResolveHybridReflectionPipeline(
                          authored: false,
                          nearFieldDirectSource,
                          combinedAdvancedGi,
                          out foliagePipeline) &&
                      _foliagePipeline.TryResolveHybridReflectionPipeline(
                          authored: true,
                          nearFieldDirectSource,
                          combinedAdvancedGi,
                          out authoredFoliagePipeline)
                    : _foliagePipeline.TryResolveForwardPipeline(
                    authored: false,
                    receiverFeedback,
                    nearFieldDirectSource,
                    combinedAdvancedGi,
                    out foliagePipeline) &&
                      _foliagePipeline.TryResolveForwardPipeline(
                          authored: true,
                          receiverFeedback,
                          nearFieldDirectSource,
                          combinedAdvancedGi,
                          out authoredFoliagePipeline);
            if (!pipelinesResolved)
            {
                return false;
            }
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                foliagePipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)sceneData.FoliageClusterCount),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = GPUFoliageDrawPushConstants.PackFlags(
                    ShouldWriteMaterialTransportProvenance(),
                    _simpleDdgiReflectionFeedbackRequiredForCurrentView,
                    _reflectionFeedbackCubemapArrayLayer),
                DebugView = sceneData.FoliageDebugView,
                ShadowDensityScale = 1.0f,
                Padding2 = checked((uint)Math.Min(
                    sceneData.ObjectCount,
                    (int)SimpleDdgiNearFieldResidualGpuAbi
                        .MaximumSurfaceTableEntryCount))
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

            DrawAuthoredFoliageForward(
                cmd,
                sceneData,
                authoredFoliagePipeline);
            return true;
        }

        private void DrawFoliageWithoutNearFieldDirectSource(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent)
        {
            if (_foliagePipeline == null || sceneData.FoliageClusterCount <= 0 ||
                sceneData.FoliageDrawBufferBytes == 0)
            {
                return;
            }

            RenderingAttachmentInfo colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            RenderingAttachmentInfo depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store,
                new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)));
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = renderExtent
                },
                LayerCount = 1,
                ColorAttachmentCount =
                    ForwardDynamicRenderingContract.SceneColorAttachmentCount,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            bool hybridReceiverWasEnabled =
                _hybridReflectionReceiverEnabledForCurrentView;
            _hybridReflectionReceiverEnabledForCurrentView = false;
            try
            {
                DrawFoliageForward(cmd, sceneData);
            }
            finally
            {
                _hybridReflectionReceiverEnabledForCurrentView =
                    hybridReceiverWasEnabled;
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }
        }

        private void DrawAuthoredFoliageForward(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            VkPipeline authoredFoliagePipeline)
        {
            if (_foliagePipeline == null || _bufferManager == null || _foliageManager == null || sceneData.FoliageDrawBufferBytes == 0)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers((int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid || buffers.MeshletDrawCapacity <= 0)
                return;

            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                authoredFoliagePipeline);
            BindFoliageDescriptorSets(cmd);

            var pushConstants = new GPUFoliageDrawPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y, sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = GPUFoliageDrawPushConstants.PackFlags(
                    ShouldWriteMaterialTransportProvenance(),
                    _simpleDdgiReflectionFeedbackRequiredForCurrentView,
                    _reflectionFeedbackCubemapArrayLayer),
                DebugView = sceneData.FoliageDebugView,
                ShadowDensityScale = 1.0f,
                Padding2 = checked((uint)Math.Min(
                    sceneData.ObjectCount,
                    (int)SimpleDdgiNearFieldResidualGpuAbi
                        .MaximumSurfaceTableEntryCount))
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
                long pipelineStart =
                    _giPipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateComputePipelines(
                        _context.Device,
                        _simpleDdgiReceiverCachePipelineCache,
                        1,
                        &info,
                        null,
                        out pipeline);
                }
                finally
                {
                    _giPipelineCacheService?.EndPipelineCreation(
                        $"{Name}:{shaderArtifactName}",
                        pipelineStart);
                }
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

            if (_simpleDdgiReceiverFeedbackPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverFeedbackPipeline,
                    null);
                _simpleDdgiReceiverFeedbackPipeline = default;
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
            if (_giPipelineCacheService == null &&
                _simpleDdgiReceiverCachePipelineCache.Handle != 0)
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
