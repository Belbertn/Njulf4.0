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
    public sealed unsafe partial class ForwardPlusPass : RenderPassBase
    {
        // The receiver accelerator evaluates one exact gather per 12x12 block,
        // then reconstructs one FP16 value per 2x2 screen block. Its sidecar
        // carries the representative surface needed to reject depth, plane,
        // and normal discontinuities before the radiance payload is touched.
        internal const uint SimpleDdgiReceiverGatherScale =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.SurfaceTileScale;

        internal const uint SimpleDdgiReceiverCacheScale = 2u;
        internal const uint SimpleDdgiReceiverCacheWorkgroupSize = 8u;
        internal const ulong SimpleDdgiReceiverCacheEntryBytes = 16u;
        // Canonical 16-byte diffuse record plus a 64-byte, frame-stamped signed
        // L2 coefficient tail. Only the coarse 12x12 lattice owns this payload;
        // the half-resolution published cache ABI remains unchanged.
        internal const ulong SimpleDdgiReceiverGatherEntryBytes = 80u;
        internal const ulong SimpleDdgiReceiverSurfaceEntryBytes = 8u;
        private const int FramesInFlight = 2;

        private sealed class SimpleDdgiReceiverPipelineBank
        {
            internal VkPipeline CanonicalGather;
            internal VkPipeline HybridDiffuseVisibilityGather;
            internal VkPipeline CanonicalResolve;
            internal VkPipeline ExactFeedbackGather;
            internal VkPipeline LegacyGather;
            internal VkPipeline LegacyResolve;
            internal VkPipeline DiagnosticsResolve;
            internal VkPipeline AdaptiveClassify;
            internal VkPipeline AdaptiveGather;
            internal VkPipeline AdaptiveFeedbackGather;
            internal VkPipeline AdaptiveMissingFeedbackGather;
            internal VkPipeline AdaptiveResolve;
            internal VkPipeline CompactMaskedFeedback;

            internal bool IsComplete(
                bool requiresReceiverFeedback,
                bool requiresAdaptive,
                bool requiresLegacy,
                bool requiresDiagnostics,
                bool requiresCompactMaskedFeedback) =>
                CanonicalGather.Handle != 0 &&
                CanonicalResolve.Handle != 0 &&
                (!requiresReceiverFeedback ||
                 ExactFeedbackGather.Handle != 0) &&
                (!requiresAdaptive ||
                 AdaptiveClassify.Handle != 0 &&
                 AdaptiveGather.Handle != 0 &&
                 AdaptiveFeedbackGather.Handle != 0 &&
                 AdaptiveMissingFeedbackGather.Handle != 0 &&
                 AdaptiveResolve.Handle != 0) &&
                (!requiresCompactMaskedFeedback ||
                 CompactMaskedFeedback.Handle != 0) &&
                (!requiresLegacy ||
                 LegacyGather.Handle != 0 && LegacyResolve.Handle != 0) &&
                (!requiresDiagnostics || DiagnosticsResolve.Handle != 0);
        }

        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly PipelineObjects.FoliagePipeline? _foliagePipeline;
        private readonly BufferManager? _bufferManager;
        private readonly FoliageManager? _foliageManager;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly bool _opaqueComputeSupported;
        private OpaqueVisibilityCompute? _opaqueCompute;
        private readonly PipelineObjects.SkyboxPipeline? _skyboxPipeline;
        private readonly GiPipelineCacheService? _giPipelineCacheService;

        private readonly ISimpleDdgiReceiverFeedbackCapture?
            _simpleDdgiReceiverFeedbackRuntime;

        private ForwardNearFieldDirectSourceAttachmentBinding?
            _nearFieldDirectSourceBinding;

        private readonly Func<bool>? _nearFieldDirectSourceRuntimeAvailable;
        private readonly Func<SimpleDdgiNearFieldResidualExecutionExtent>?
            _nearFieldDirectSourceExecutionExtent;
        private bool _recordingTraceResolutionNearFieldSource;
        private SimpleDdgiNearFieldResidualExecutionScale
            _traceResolutionNearFieldSourceScale;

        private readonly ForwardGiCausticReceiverAttachmentBinding?
            _giCausticReceiverBinding;

        private readonly Func<bool>? _giCausticRuntimeAvailable;

        private readonly ForwardHybridReflectionReceiverAttachmentBinding?
            _hybridReflectionReceiverBinding;

        private bool _recordingReflectionCapture;
        private bool _reflectionCaptureIncludesDdgi;

        private bool RecordingAutomaticPlanarCapture => _recordingReflectionCapture &&
            ((uint)_reflectionFeedbackCubemapArrayLayer &
                AutomaticPlanarReflectionManager.AutomaticCaptureLayerFlag) != 0;

        private readonly BufferHandle[] _simpleDdgiReceiverCacheBuffers =
            new BufferHandle[FramesInFlight];

        private readonly BufferHandle[] _simpleDdgiReceiverGatherBuffers =
            new BufferHandle[FramesInFlight];

        private readonly BufferHandle[] _simpleDdgiReceiverCacheSurfaceBuffers =
            new BufferHandle[FramesInFlight];

        private readonly BufferHandle[] _simpleDdgiReceiverGatherSurfaceBuffers =
            new BufferHandle[FramesInFlight];

        private readonly BufferHandle[] _simpleDdgiReceiverPublicationBuffers =
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
        private VkPipeline
            _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline;
        private VkPipeline _simpleDdgiReceiverFeedbackPipeline;
        private VkPipeline _simpleDdgiMaskedFeedbackCompactPipeline;
        private VkPipeline _simpleDdgiReceiverCacheResolvePipeline;
        private VkPipeline _simpleDdgiReceiverCacheResolveDiagnosticsPipeline;
        private bool
            _simpleDdgiReceiverCacheResolveDiagnosticsPipelineCreationAttempted;
        private VkPipeline _simpleDdgiReceiverCacheLegacyPipeline;
        private VkPipeline _simpleDdgiReceiverCacheResolveLegacyPipeline;
        private readonly object _simpleDdgiReceiverPipelineBankGate = new();
        private SimpleDdgiReceiverPipelineBank?
            _simpleDdgiReceiverPipelineBank;
        private bool _simpleDdgiReceiverPipelineBankPreparationAttempted;
        private bool _simpleDdgiReceiverPipelineBankDisposing;
        private string _simpleDdgiReceiverPipelineBankFailure =
            "receiver-pipeline-bank-not-prepared";
        private uint _simpleDdgiReceiverCacheWidth;
        private uint _simpleDdgiReceiverCacheHeight;
        private ulong _simpleDdgiReceiverCacheBufferBytes;
        private uint _simpleDdgiReceiverGatherWidth;
        private uint _simpleDdgiReceiverGatherHeight;
        private ulong _simpleDdgiReceiverGatherBufferBytes;
        private ulong _simpleDdgiReceiverCacheSurfaceBufferBytes;
        private ulong _simpleDdgiReceiverGatherSurfaceBufferBytes;
        private bool _simpleDdgiReceiverCacheAvailableForCurrentView;
        private bool _simpleDdgiReceiverCacheConsumedForCurrentView;
        private SimpleDdgiReceiverCacheMode _simpleDdgiReceiverCacheRequestedMode =
            SimpleDdgiReceiverCacheMode.Exact;
        private SimpleDdgiReceiverCacheMode _simpleDdgiReceiverCacheEffectiveMode =
            SimpleDdgiReceiverCacheMode.Exact;
        private SimpleDdgiReceiverCacheFallbackReason
            _simpleDdgiReceiverCacheFallbackReason =
                SimpleDdgiReceiverCacheFallbackReason.ExactRequested;
        private string _simpleDdgiReceiverCacheFallbackDetail =
            "exact receiver gathering requested";
        private string _simpleDdgiReceiverCachePipelineArtifact =
            "forward-exact-ddgi";
        private SimpleDdgiReceiverCacheGpuCounters
            _completedSimpleDdgiReceiverCacheCounters =
                SimpleDdgiReceiverCacheGpuCounters.Unavailable;
        private readonly SimpleDdgiReceiverCacheLifetimeAccumulator
            _receiverCacheLifetime = new();
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

        internal ulong SimpleDdgiReceiverSurfaceSidecarTotalBytes
        {
            get
            {
                ulong bytes = 0u;
                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (_simpleDdgiReceiverCacheSurfaceBuffers[i].IsValid)
                    {
                        bytes = checked(bytes +
                            _simpleDdgiReceiverCacheSurfaceBufferBytes);
                    }
                    if (_simpleDdgiReceiverGatherSurfaceBuffers[i].IsValid)
                    {
                        bytes = checked(bytes +
                            _simpleDdgiReceiverGatherSurfaceBufferBytes);
                    }
                }

                return bytes;
            }
        }

        internal SimpleDdgiReceiverCacheDiagnostics
            SimpleDdgiReceiverCacheDiagnostics
        {
            get
            {
                SimpleDdgiReceiverCacheDiagnostics diagnostics =
                    _simpleDdgiReceiverCacheEffectiveMode ==
                        SimpleDdgiReceiverCacheMode.Exact
                        ? Data.SimpleDdgiReceiverCacheDiagnostics.Exact(
                            _simpleDdgiReceiverCacheRequestedMode,
                            _simpleDdgiReceiverCacheFallbackReason,
                            _simpleDdgiReceiverCacheFallbackDetail,
                            checked(SimpleDdgiReceiverCacheBufferBytes +
                                SimpleDdgiReceiverGatherBufferTotalBytes),
                            SimpleDdgiReceiverSurfaceSidecarTotalBytes)
                        : Data.SimpleDdgiReceiverCacheDiagnostics.Active(
                            _simpleDdgiReceiverCacheRequestedMode,
                            _simpleDdgiReceiverCacheEffectiveMode,
                            _simpleDdgiReceiverCacheFallbackReason,
                            _simpleDdgiReceiverCacheFallbackDetail,
                            checked(SimpleDdgiReceiverCacheBufferBytes +
                                SimpleDdgiReceiverGatherBufferTotalBytes),
                            SimpleDdgiReceiverSurfaceSidecarTotalBytes,
                            _simpleDdgiReceiverCachePipelineArtifact);
                SimpleDdgiReceiverCacheGpuCounters counters =
                    _completedSimpleDdgiReceiverCacheCounters;
                SimpleDdgiReceiverCacheLifetimeCounters lifetime =
                    _receiverCacheLifetime.Snapshot;
                SimpleDdgiReceiverCacheAdaptiveCounters adaptive =
                    _adaptiveReceiverCounters;
                SimpleDdgiMaskedFeedbackCompactionCounters maskedFeedback =
                    _maskedFeedbackCompactCounters;
                diagnostics = diagnostics with
                {
                    AdaptiveAbiVersion =
                        SimpleDdgiReceiverCacheAdaptiveAbi.Version,
                    AdaptiveHistoryValid =
                        _adaptiveReceiverFrameToken.IsAvailable ? 1 : 0,
                    AdaptiveResourceGeneration =
                        _adaptiveReceiverResourceGeneration,
                    AdaptiveResourceBytes =
                        SimpleDdgiReceiverCacheAdaptiveBytes,
                    AdaptiveCounterReadbackValid = adaptive.ReadbackValid,
                    AdaptiveGatherWorkCount = adaptive.GatherWorkCount,
                    AdaptiveMissingFeedbackWorkCount =
                        adaptive.MissingFeedbackWorkCount,
                    AdaptiveResolveTileCount = adaptive.ResolveTileCount,
                    AdaptiveOverflowFlags = adaptive.OverflowFlags,
                    AdaptiveAcceptedEntryCount = adaptive.AcceptedEntryCount,
                    AdaptiveRejectedEntryCount = adaptive.RejectedEntryCount,
                    AdaptiveFullTileCount = adaptive.FullTileCount,
                    AdaptiveHalfTileCount = adaptive.HalfTileCount,
                    AdaptiveQuarterTileCount = adaptive.QuarterTileCount,
                    AdaptiveReuseTileCount = adaptive.ReuseTileCount,
                    PublicationGeneration =
                        _receiverPublicationGenerationEnabled
                            ? _receiverPublicationStamp
                            : 0u,
                    PublicationStableIdentityHitCount =
                        _receiverPublicationTracker.StableIdentityHitCount,
                    PublicationDirtyIdentityCount =
                        _receiverPublicationTracker.DirtyIdentityCount,
                    PublicationWrapResetCount =
                        _receiverPublicationTracker.WrapResetCount,
                    AdaptivePublicationGenerationHitCount =
                        adaptive.PublicationGenerationHitCount,
                    AdaptivePublicationDirtyInvalidationCount =
                        adaptive.PublicationDirtyInvalidationCount,
                    AdaptivePublicationSkippedTileCount =
                        adaptive.PublicationSkippedTileCount,
                    LifetimeObservedFrameCount =
                        lifetime.ObservedFrameCount,
                    LifetimeResolveCandidateCount =
                        lifetime.ResolveCandidateCount,
                    LifetimeResolveValidCount =
                        lifetime.ResolveValidCount,
                    LifetimeForwardCandidateCount =
                        lifetime.ForwardCandidateCount,
                    LifetimeForwardAcceptedCount =
                        lifetime.ForwardAcceptedCount,
                    LifetimeExactFallbackFragmentCount =
                        lifetime.ExactFallbackFragmentCount,
                    LifetimeDirectionalCacheEvaluationCount =
                        lifetime.DirectionalCacheEvaluationCount,
                    LifetimeLegacyFragmentCount =
                        lifetime.LegacyFragmentCount,
                    MaskedFeedbackCompactionReadbackValid =
                        maskedFeedback.ReadbackValid,
                    MaskedFeedbackCompactedCount =
                        maskedFeedback.PublishedCount,
                    MaskedFeedbackOverflowFallbackCount =
                        maskedFeedback.OverflowFallbackCount,
                    MaskedFeedbackCandidateHighWater =
                        maskedFeedback.CandidateHighWater,
                    MaskedFeedbackLogicalCapacity =
                        maskedFeedback.LogicalCapacity,
                    MaskedFeedbackObservedHighWater =
                        maskedFeedback.ObservedHighWater,
                    MaskedFeedbackCompactBufferBytes =
                        SimpleDdgiMaskedFeedbackCompactTotalBytes
                };
                if (diagnostics.EffectiveMode ==
                        SimpleDdgiReceiverCacheMode.Exact ||
                    _settings.GlobalIllumination.DebugView ==
                        GlobalIlluminationDebugView
                            .DdgiReceiverCacheRejection ||
                    counters.ReadbackValid == 0)
                {
                    return diagnostics;
                }

                return diagnostics with
                {
                    CounterReadbackValid = counters.ReadbackValid,
                    ResolveCandidateCount = counters.ResolveCandidateCount,
                    ResolveValidCount = counters.ResolveValidCount,
                    ResolveInvalidOrNonFiniteRejectCount =
                        counters.ResolveInvalidOrNonFiniteRejectCount,
                    ResolveDepthOrPositionRejectCount =
                        counters.ResolveDepthOrPositionRejectCount,
                    ResolvePlaneRejectCount = counters.ResolvePlaneRejectCount,
                    ResolveNormalRejectCount = counters.ResolveNormalRejectCount,
                    ResolveInsufficientSupportRejectCount =
                        counters.ResolveInsufficientSupportRejectCount,
                    ForwardCandidateCount = counters.ForwardCandidateCount,
                    ForwardAcceptedCount = counters.ForwardAcceptedCount,
                    ForwardInvalidOrNonFiniteRejectCount =
                        counters.ForwardInvalidOrNonFiniteRejectCount,
                    ForwardDepthOrPositionRejectCount =
                        counters.ForwardDepthOrPositionRejectCount,
                    ForwardPlaneRejectCount = counters.ForwardPlaneRejectCount,
                    ForwardNormalRejectCount = counters.ForwardNormalRejectCount,
                    ForwardInsufficientSupportRejectCount =
                        counters.ForwardInsufficientSupportRejectCount,
                    ExactFallbackFragmentCount =
                        counters.ExactFallbackFragmentCount,
                    LegacyFragmentCount = counters.LegacyFragmentCount,
                    DirectionalCacheEvaluationCount =
                        counters.DirectionalCacheEvaluationCount,
                    CommonSurfaceSimpleOpaquePixelEstimate =
                        counters.CommonSurfaceSimpleOpaquePixelEstimate,
                    CommonSurfaceEligiblePixelEstimate =
                        counters.CommonSurfaceEligiblePixelEstimate
                };
            }
        }

        internal void ObserveCompletedSimpleDdgiReceiverCacheCounters(
            SimpleDdgiReceiverCacheGpuCounters counters)
        {
            _receiverCacheLifetime.Observe(counters);
            _completedSimpleDdgiReceiverCacheCounters = counters;
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
            ISimpleDdgiReceiverFeedbackCapture?
                simpleDdgiReceiverFeedbackRuntime = null,
            Func<SimpleDdgiNearFieldResidualExecutionExtent>?
                nearFieldDirectSourceExecutionExtent = null)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _foliagePipeline = foliagePipeline;
            _bufferManager = bufferManager;
            _foliageManager = foliageManager;
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _opaqueComputeSupported = OpaqueVisibilityComputePolicy.Requested && OpaqueVisibilityCompute.SupportsComputeQuads(context);
            _skyboxPipeline = skyboxPipeline;
            _giPipelineCacheService = giPipelineCacheService;
            _nearFieldDirectSourceBinding = nearFieldDirectSourceBinding;
            _nearFieldDirectSourceRuntimeAvailable =
                nearFieldDirectSourceRuntimeAvailable;
            _nearFieldDirectSourceExecutionExtent =
                nearFieldDirectSourceExecutionExtent;
            _giCausticReceiverBinding = giCausticReceiverBinding;
            _giCausticRuntimeAvailable = giCausticRuntimeAvailable;
            _hybridReflectionReceiverBinding = hybridReflectionReceiverBinding;
            _simpleDdgiReceiverFeedbackRuntime =
                simpleDdgiReceiverFeedbackRuntime;
            for (int i = 0; i < FramesInFlight; i++)
            {
                _simpleDdgiReceiverCacheBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverCacheSurfaceBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherSurfaceBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverPublicationBuffers[i] = BufferHandle.Invalid;
                _sparseHybridLobePayloadBuffers[i] = BufferHandle.Invalid;
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
        /// Publishes the source attachments and extent-bound V13 contract for
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

            RecreateSparseHybridLobePayloadResources(
                _renderTargets.SceneColor.Extent);
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
                RecreateSimpleDdgiReceiverCacheResources();
                InitializeSimpleDdgiReceiverCacheAdaptiveInfrastructure();
                if (!RendererBuildConfiguration.ProgressivePipelineStartup)
                    PrepareSimpleDdgiReceiverPipelineBank();
            }
            catch (Exception ex)
            {
                // Receiver caching is an accelerator, not a correctness
                // prerequisite. Keep the exact fragment gather available when
                // resource or pipeline creation is unsupported.
                System.Diagnostics.Debug.WriteLine(
                    $"Simple-DDGI receiver cache unavailable: {ex.GetType().Name}: {ex.Message}");
                SetSimpleDdgiReceiverCacheFallback(
                    SimpleDdgiReceiverCacheFallbackReason.PipelineUnavailable,
                    $"receiver-cache initialization failed: {ex.GetType().Name}");
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
                        ? ForwardOpaquePipelineFamily.Simple
                        : ForwardOpaquePipelineFamily.Full,
                    Math.Max(0, sceneData.SimpleOpaqueMeshletCount),
                    BindlessIndex.MeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    selection.UseSimpleGlobalIblPipeline
                        ? ForwardOpaquePipelineFamily.SimpleFullInput
                        : ForwardOpaquePipelineFamily.Full,
                    Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount),
                    BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
                DrawForwardBucket(
                    cmd,
                    sceneData,
                    selection.UseSimpleGlobalIblPipeline
                        ? ForwardOpaquePipelineFamily.Simple
                        : ForwardOpaquePipelineFamily.Full,
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

        /// <summary>
        /// Builds the receiver-cache compute programs required by the selected
        /// immutable renderer mode into local ownership. Optional exact-
        /// feedback and adaptive programs are included only when their owning
        /// features request them. Command recording observes the bank only
        /// after every required handle has been created successfully; failure
        /// permanently retains the canonical fragment-gather path.
        /// </summary>
        internal bool PrepareSimpleDdgiReceiverPipelineBank(
            bool receiverFeedbackRequired = true)
        {
            lock (_simpleDdgiReceiverPipelineBankGate)
            {
                SimpleDdgiReceiverCacheMode requestedMode =
                    SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                        _settings.GlobalIllumination
                            .SimpleDdgiReceiverCacheMode,
                        _settings.Diagnostics
                            .ForceForwardGiReceiverCacheForBenchmark,
                        _settings.Diagnostics
                            .ForceExactForwardGiGatherForBenchmark);
                bool requiresAdaptive = requestedMode ==
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive;
                bool requiresLegacy = requestedMode ==
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark;
                bool requiresDiagnostics = _settings.Diagnostics
                    .DdgiForwardEstimateCountersEnabled;
                bool requiresCompactMaskedFeedback =
                    receiverFeedbackRequired &&
                    _settings.IsPerformanceOptimizationEnabled(
                        PerformanceOptimizationFeature.CompactMaskedFeedback);
                bool hybridDiffuseVisibilityCandidate =
                    requestedMode ==
                        SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial &&
                    _settings.Reflections.Enabled &&
                    _settings.Reflections.Mode is
                        (ReflectionMode.StaticProbesAndSsr or
                         ReflectionMode.StaticProbesAndPlanar or
                         ReflectionMode.HybridRayQuery);

                if (Volatile.Read(
                        ref _simpleDdgiReceiverPipelineBank) is { } published)
                {
                    return published.IsComplete(
                        receiverFeedbackRequired,
                        requiresAdaptive,
                        requiresLegacy,
                        requiresDiagnostics,
                        requiresCompactMaskedFeedback);
                }
                if (_simpleDdgiReceiverPipelineBankPreparationAttempted ||
                    _simpleDdgiReceiverPipelineBankDisposing)
                {
                    return false;
                }

                _simpleDdgiReceiverPipelineBankPreparationAttempted = true;
                var bank = new SimpleDdgiReceiverPipelineBank();
                try
                {
                    if (requiresAdaptive && !AdaptiveReceiverResourcesValid())
                    {
                        throw new InvalidOperationException(
                            "Adaptive receiver resources were not initialized before pipeline compilation.");
                    }
                    bank.CanonicalGather = CreateSimpleDdgiReceiverCachePipeline(
                        "ddgi_simple_receiver_cache.comp.spv",
                        "Simple DDGI Receiver Gather Pipeline");
                    if (hybridDiffuseVisibilityCandidate)
                    {
                        try
                        {
                            bank.HybridDiffuseVisibilityGather =
                                CreateSimpleDdgiReceiverCachePipeline(
                                    "ddgi_simple_receiver_cache_diffuse_visibility.comp.spv",
                                    "Hybrid DDGI Diffuse Visibility Receiver Gather Pipeline");
                        }
                        catch (Exception exception) when (
                            exception is VulkanException or IOException or
                                InvalidOperationException or ArgumentException or
                                OverflowException)
                        {
                            // This producer is an optional specialization. The
                            // canonical gather has the same ABI and remains the
                            // fallback when native pipeline creation rejects it.
                            System.Diagnostics.Debug.WriteLine(
                                "Hybrid DDGI diffuse/visibility gather unavailable; " +
                                "canonical receiver gather retained. " +
                                $"{exception.GetType().Name}: {exception.Message}");
                        }
                    }
                    bank.CanonicalResolve = CreateSimpleDdgiReceiverCachePipeline(
                        "ddgi_simple_receiver_cache_resolve.comp.spv",
                        "Simple DDGI Receiver Cache Resolve Pipeline");
                    if (receiverFeedbackRequired)
                    {
                        bank.ExactFeedbackGather =
                            CreateSimpleDdgiReceiverCachePipeline(
                            "ddgi_simple_receiver_cache_b1.comp.spv",
                            "Simple DDGI Exact Receiver Feedback Gather Pipeline");
                    }

                    if (requiresLegacy)
                    {
                        bank.LegacyGather =
                            CreateSimpleDdgiReceiverCachePipeline(
                                "ddgi_simple_receiver_cache_legacy.comp.spv",
                                "Legacy DDGI Receiver Gather Benchmark Pipeline");
                        bank.LegacyResolve =
                            CreateSimpleDdgiReceiverCachePipeline(
                                "ddgi_simple_receiver_cache_resolve_legacy.comp.spv",
                                "Legacy Depth-Only DDGI Receiver Resolve Benchmark Pipeline");
                    }

                    if (requiresDiagnostics)
                    {
                        bank.DiagnosticsResolve =
                            CreateSimpleDdgiReceiverCachePipeline(
                                "ddgi_simple_receiver_cache_resolve_diagnostics.comp.spv",
                                "Simple DDGI Receiver Cache Diagnostic Resolve Pipeline");
                    }

                    if (requiresAdaptive)
                    {
                        bank.AdaptiveClassify =
                            CreateSimpleDdgiReceiverCacheAdaptivePipeline(
                                "ddgi_simple_receiver_cache_classify.comp.spv",
                                "Simple DDGI Receiver Cache Adaptive Classify Pipeline");
                        bank.AdaptiveGather =
                            CreateSimpleDdgiReceiverCacheAdaptivePipeline(
                                "ddgi_simple_receiver_cache_adaptive.comp.spv",
                                "Simple DDGI Receiver Cache Adaptive Gather Pipeline");
                        bank.AdaptiveFeedbackGather =
                            CreateSimpleDdgiReceiverCacheAdaptivePipeline(
                                "ddgi_simple_receiver_cache_adaptive_b1.comp.spv",
                                "Simple DDGI Receiver Cache Adaptive Exact Feedback Gather Pipeline");
                        bank.AdaptiveMissingFeedbackGather =
                            CreateSimpleDdgiReceiverCacheAdaptivePipeline(
                                "ddgi_simple_receiver_cache_adaptive_b1_missing.comp.spv",
                                "Simple DDGI Receiver Cache Adaptive Missing Feedback Gather Pipeline");
                        bank.AdaptiveResolve =
                            CreateSimpleDdgiReceiverCacheAdaptivePipeline(
                                "ddgi_simple_receiver_cache_resolve_adaptive.comp.spv",
                                "Simple DDGI Receiver Cache Adaptive Resolve Pipeline");
                    }
                    if (requiresCompactMaskedFeedback)
                    {
                        bank.CompactMaskedFeedback =
                            CreateSimpleDdgiReceiverCachePipeline(
                                "ddgi_masked_feedback_compact.comp.spv",
                                "Simple DDGI Masked Feedback Compact Pipeline");
                    }

                    if (!bank.IsComplete(
                            receiverFeedbackRequired,
                            requiresAdaptive,
                            requiresLegacy,
                            requiresDiagnostics,
                            requiresCompactMaskedFeedback))
                    {
                        throw new InvalidOperationException(
                            "The receiver pipeline bank is incomplete.");
                    }

                    _simpleDdgiReceiverCachePipeline = bank.CanonicalGather;
                    _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline =
                        bank.HybridDiffuseVisibilityGather;
                    _simpleDdgiReceiverCacheResolvePipeline =
                        bank.CanonicalResolve;
                    _simpleDdgiReceiverFeedbackPipeline =
                        bank.ExactFeedbackGather;
                    _simpleDdgiReceiverCacheLegacyPipeline = bank.LegacyGather;
                    _simpleDdgiReceiverCacheResolveLegacyPipeline =
                        bank.LegacyResolve;
                    _simpleDdgiReceiverCacheResolveDiagnosticsPipeline =
                        bank.DiagnosticsResolve;
                    _simpleDdgiReceiverCacheResolveDiagnosticsPipelineCreationAttempted =
                        requiresDiagnostics;
                    _adaptiveReceiverClassifyPipeline = bank.AdaptiveClassify;
                    _adaptiveReceiverGatherPipeline = bank.AdaptiveGather;
                    _adaptiveReceiverFeedbackGatherPipeline =
                        bank.AdaptiveFeedbackGather;
                    _adaptiveReceiverMissingFeedbackGatherPipeline =
                        bank.AdaptiveMissingFeedbackGather;
                    _adaptiveReceiverResolvePipeline = bank.AdaptiveResolve;
                    _simpleDdgiMaskedFeedbackCompactPipeline =
                        bank.CompactMaskedFeedback;
                    _simpleDdgiReceiverPipelineBankFailure =
                        "receiver-pipeline-bank-ready";
                    Volatile.Write(
                        ref _simpleDdgiReceiverPipelineBank,
                        bank);
                    return true;
                }
                catch (Exception exception) when (
                    exception is VulkanException or IOException or
                        InvalidOperationException or ArgumentException or
                        OverflowException)
                {
                    DestroySimpleDdgiReceiverPipelineBank(bank);
                    _simpleDdgiReceiverPipelineBankFailure =
                        "receiver-pipeline-bank-creation-failed:" +
                        exception.GetType().Name + ":" + exception.Message;
                    System.Diagnostics.Debug.WriteLine(
                        "Deferred receiver pipeline bank unavailable; exact " +
                        "canonical shading retained. " +
                        _simpleDdgiReceiverPipelineBankFailure);
                    return false;
                }
            }
        }

        internal bool SimpleDdgiReceiverPipelineBankReady =>
            Volatile.Read(ref _simpleDdgiReceiverPipelineBank) is not null;

        internal string SimpleDdgiReceiverPipelineBankStatus =>
            SimpleDdgiReceiverPipelineBankReady
                ? "receiver-pipeline-bank-ready"
                : _simpleDdgiReceiverPipelineBankFailure;

        /// <summary>
        /// Records one rectangular automatic-planar view. It deliberately
        /// reuses the shipping opaque, foliage, and sorted-transparent
        /// material programs while the capture flag disables recursive local
        /// reflection lookup and enables receiver/half-space clipping.
        /// </summary>
        internal void RecordAutomaticPlanarCapture(
            CommandBuffer cmd,
            int frameIndex,
            SceneRenderingData sceneData,
            in AutomaticPlanarCaptureView view,
            ImageView colorView,
            ImageView depthView)
        {
            if (colorView.Handle == 0 || depthView.Handle == 0)
            {
                throw new InvalidOperationException(
                    "Automatic planar capture attachments are unavailable.");
            }

            Matrix4x4 oldView = sceneData.ViewMatrix;
            Matrix4x4 oldProjection = sceneData.ProjectionMatrix;
            Matrix4x4 oldViewProjection = sceneData.ViewProjectionMatrix;
            Matrix4x4 oldInverseView = sceneData.InverseViewMatrix;
            Matrix4x4 oldInverseProjection = sceneData.InverseProjectionMatrix;
            Matrix4x4 oldInverseViewProjection =
                sceneData.InverseViewProjectionMatrix;
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
            int oldGlobalIlluminationDdgiActive =
                sceneData.GlobalIlluminationDdgiActive;
            int oldSimpleDdgiActive = sceneData.SimpleDdgiActive;
            int captureLayer = checked(
                (int)(AutomaticPlanarReflectionManager
                    .AutomaticCaptureLayerFlag | (uint)view.Slot));

            try
            {
                _recordingReflectionCapture = true;
                _reflectionCaptureIncludesDdgi = true;
                _reflectionFeedbackCubemapArrayLayer = captureLayer;
                sceneData.ViewMatrix = view.View;
                sceneData.ProjectionMatrix = view.Projection;
                sceneData.ViewProjectionMatrix = view.ViewProjection;
                sceneData.InverseViewMatrix = view.View.Invert();
                sceneData.InverseProjectionMatrix = view.Projection.Invert();
                sceneData.InverseViewProjectionMatrix =
                    view.ViewProjection.Invert();
                sceneData.CameraPosition = view.Position;
                sceneData.ScreenWidth = view.Width;
                sceneData.ScreenHeight = view.Height;
                sceneData.DepthPrePassEnabled = false;
                sceneData.ReflectionsEnabled = false;
                sceneData.ReflectionMode = ReflectionMode.Disabled;
                sceneData.ReflectionProbeCount = 0;
                sceneData.OcclusionCullingEnabled = false;
                sceneData.HiZMipCount = 0u;

                var viewport = new Viewport
                {
                    X = 0.0f,
                    Y = 0.0f,
                    Width = view.Width,
                    Height = view.Height,
                    MinDepth = 0.0f,
                    MaxDepth = 1.0f
                };
                var scissor = new Rect2D
                {
                    Offset = new Offset2D(),
                    Extent = new Extent2D
                    {
                        Width = view.Width,
                        Height = view.Height
                    }
                };
                _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
                _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
                BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

                RenderingAttachmentInfo colorAttachment = ColorAttachment(
                    colorView,
                    ImageLayout.ColorAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(new ClearColorValue(
                        0.0f, 0.0f, 0.0f, 1.0f)));
                RenderingAttachmentInfo depthAttachment = DepthAttachment(
                    depthView,
                    ImageLayout.DepthStencilAttachmentOptimal,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(
                        null,
                        new ClearDepthStencilValue(0.0f, 0)));
                var renderingInfo = new RenderingInfo
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = new Rect2D
                    {
                        Offset = new Offset2D(),
                        Extent = new Extent2D
                        {
                            Width = view.Width,
                            Height = view.Height
                        }
                    },
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    PColorAttachments = &colorAttachment,
                    PDepthAttachment = &depthAttachment
                };
                RecordAutomaticPlanarDepthPrepass(cmd, sceneData, ref renderingInfo);
                _context.KhrDynamicRendering.CmdBeginRendering(
                    cmd,
                    &renderingInfo);

                RecordReflectionSkybox(cmd, view.View, view.Projection);
                BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

                DrawAutomaticPlanarOpaque(cmd, sceneData);
                DrawFoliageForward(cmd, sceneData);
                DrawAutomaticPlanarTransparentSurfaces(
                    cmd,
                    sceneData,
                    captureLayer);
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
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
                sceneData.InverseViewProjectionMatrix =
                    oldInverseViewProjection;
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
                sceneData.GlobalIlluminationDdgiActive =
                    oldGlobalIlluminationDdgiActive;
                sceneData.SimpleDdgiActive = oldSimpleDdgiActive;
            }
        }

        private void DrawAutomaticPlanarTransparentSurfaces(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int captureLayer)
        {
            if (sceneData.TransparentMeshletCount <= 0 ||
                !_settings.Transparency.Enabled)
            {
                return;
            }

            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.TransparentForwardPipeline);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);
            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = checked(
                    (uint)sceneData.TransparentMeshletCount),
                MeshletDrawBufferBaseIndex =
                    BindlessIndex.TransparentMeshletDrawBufferBase,
                PackedLightDispatch = GPUForwardPushConstants
                    .PackLightDispatch(
                        sceneData.LightCount,
                        sceneData.LocalLightCount,
                        sceneData.DirectionalLightIndex0,
                        sceneData.DirectionalLightIndex1),
                LocalLightCount = checked((uint)sceneData.LocalLightCount),
                DebugAndAoFlags = GPUForwardPushConstants
                    .PackDebugAndAoFlags(
                        debugViewMode: 0u,
                        ambientOcclusionEnabled: false,
                        ambientOcclusionDebugView: 0u,
                        transparentReceiveShadows:
                            sceneData.TransparentReceiveShadows,
                        transparencyDebugView: 0u,
                        ambientOcclusionForwardSamplingMode: 0u,
                        globalIlluminationEnabled:
                            sceneData.TransparentReceiveGlobalIllumination),
                DiagnosticFlags = GPUForwardPushConstants
                    .PackDiagnosticFlags(
                        ddgiForwardEstimateCountersEnabled: false,
                        effectiveReflectionMode: ReflectionMode.Disabled,
                        transparentSampleReflections: false,
                        opaqueSceneColorSnapshotAvailable: false),
                CaptureFlags = GPUForwardPushConstants.PackCaptureFlags(
                    reflectionCaptureEnabled: true,
                    reflectionCaptureLayer: captureLayer)
            };
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt |
                ShaderStageFlags.FragmentBit |
                ShaderStageFlags.TaskBitExt,
                0,
                (uint)Marshal.SizeOf<GPUForwardPushConstants>(),
                &pushConstants);
            _context.ExtMeshShader.CmdDrawMeshTask(
                cmd,
                checked((uint)sceneData.TransparentMeshletCount),
                1,
                1);
        }

        private void PrepareReflectionReceiverFeedbackFace(
            int frameIndex,
            SceneRenderingData sceneData,
            in ReflectionCaptureViewContext view)
        {
            _simpleDdgiReflectionFeedbackRequiredForCurrentView = false;
            _reflectionFeedbackCubemapArrayLayer = 0;
            ISimpleDdgiReceiverFeedbackCapture? runtime =
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
                ISimpleDdgiReceiverFeedbackCapture? runtime =
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
            in ReflectionCaptureViewContext view) =>
            RecordReflectionSkybox(cmd, view.View, view.Projection);

        private void RecordReflectionSkybox(
            CommandBuffer cmd,
            Matrix4x4 view,
            Matrix4x4 projection)
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
                InverseViewMatrix = view.Invert(),
                InverseProjectionMatrix = projection.Invert(),
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
            _adaptiveReceiverExecutedForCurrentView = false;
            _adaptiveReceiverFrameToken =
                SimpleDdgiReceiverCacheFrameToken.Unavailable;
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
            SimpleDdgiReceiverPipelineBank? receiverPipelineBank =
                Volatile.Read(ref _simpleDdgiReceiverPipelineBank);
            SimpleDdgiReceiverFeedbackCaptureProducerContract
                receiverFeedbackProducer =
                    SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
            try
            {
                _simpleDdgiReceiverCacheAvailableForCurrentView = false;
                _simpleDdgiReceiverCacheConsumedForCurrentView = false;
                _simpleDdgiReceiverCacheRequestedMode =
                    SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                        _settings.GlobalIllumination
                            .SimpleDdgiReceiverCacheMode,
                        _settings.Diagnostics
                            .ForceForwardGiReceiverCacheForBenchmark,
                        _settings.Diagnostics
                            .ForceExactForwardGiGatherForBenchmark);
                _simpleDdgiReceiverCacheEffectiveMode =
                    SimpleDdgiReceiverCacheMode.Exact;
                _simpleDdgiReceiverCachePipelineArtifact =
                    "forward-exact-ddgi";
                if (_simpleDdgiReceiverCacheRequestedMode ==
                    SimpleDdgiReceiverCacheMode.Exact)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason.ExactRequested,
                        "exact receiver gathering requested");
                }
                else if (_simpleDdgiReceiverCacheRequestedMode ==
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .DispatchUnavailable,
                        "legacy depth-only benchmark cache has not completed this frame");
                }
                else
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .DispatchUnavailable,
                        "surface-aware receiver cache has not completed this frame");
                }
                _forwardGiDisabledBenchmarkPipelineUsedForCurrentView = false;
                _forwardGiExactGatherUsedForCurrentView = false;
                _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView = false;
                _simpleDdgiFoliageFeedbackRequiredForCurrentView = false;
                Extent2D renderExtent = _renderTargets.SceneColor.Extent;
                bool materialTransportProvenanceEnabled =
                    ShouldWriteMaterialTransportProvenance();
                bool nearFieldSourceEnabled = TryGetNearFieldDirectSourceBinding(
                    sceneData,
                    renderExtent,
                    materialTransportProvenanceEnabled,
                    out ForwardNearFieldDirectSourceAttachmentBinding?
                        nearFieldDirectSourceBinding);
                bool nearFieldTraceResolutionEnabled =
                    nearFieldSourceEnabled &&
                    _meshPipeline.NearFieldDirectSourceConfiguration
                        .SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
                bool nearFieldDirectSourceEnabled =
                    nearFieldSourceEnabled && !nearFieldTraceResolutionEnabled;
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
                bool sparseHybridLobePayloadEnabled =
                    hybridReflectionReceiverEnabled &&
                    UsesSparseHybridLobePayload;
                if (sparseHybridLobePayloadEnabled &&
                    !PrepareSparseHybridLobePayload(
                        cmd,
                        frameIndex,
                        renderExtent))
                {
                    hybridReflectionReceiverEnabled = false;
                    hybridReflectionReceiverBinding = null;
                    sparseHybridLobePayloadEnabled = false;
                    HybridReflectionReceiverFailureReason =
                        "hybrid-reflection-sparse-lobe-buffer-unavailable";
                }
                if (!hybridReflectionReceiverEnabled &&
                    sceneData.EffectiveReflectionMode is
                        (ReflectionMode.StaticProbesAndSsr or
                        ReflectionMode.StaticProbesAndPlanar or
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
                        _meshPipeline.AreHybridReflectionExactPipelinesReady(
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                    bool foliageVariantsReady =
                        _foliagePipeline is null ||
                        sceneData.FoliageClusterCount <= 0 ||
                        sceneData.FoliageDrawBufferBytes == 0 ||
                        _foliagePipeline.AreHybridReflectionExactPipelinesReady(
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                    if (!meshVariantsReady || !foliageVariantsReady)
                    {
                        hybridReflectionReceiverEnabled = false;
                        hybridReflectionReceiverBinding = null;
                        sparseHybridLobePayloadEnabled = false;
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
                // Qualification counter variants intentionally expose only the
                // ordinary SceneColor output. Advanced MRT producers keep their
                // exact pipelines while counters are enabled.
                bool ordinaryReceiverCacheOutput =
                    !nearFieldDirectSourceEnabled &&
                    !giCausticReceiverEnabled &&
                    !hybridReflectionReceiverEnabled;
                bool receiverCacheDiagnosticsCompatible =
                    !_settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                    ordinaryReceiverCacheOutput;
                bool receiverCacheModeOutputCompatible =
                    _simpleDdgiReceiverCacheRequestedMode !=
                        SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark ||
                    ordinaryReceiverCacheOutput;
                bool receiverCacheDebugView =
                    _settings.GlobalIllumination.DebugView ==
                    GlobalIlluminationDebugView.DdgiReceiverCacheRejection;
                bool receiverCacheEligible = ShouldConsumeSimpleDdgiReceiverCache(
                                                 _settings.QualityPreset,
                                                 _settings.GlobalIllumination
                                                     .SimpleDdgiReceiverCacheMode,
                                                 _settings.Diagnostics
                                                     .ForceForwardGiReceiverCacheForBenchmark,
                                                 _settings.Diagnostics
                                                     .ForceExactForwardGiGatherForBenchmark) &&
                                             receiverCacheDiagnosticsCompatible &&
                                             receiverCacheModeOutputCompatible &&
                                             receiverGatherDispatchable &&
                                             receiverPipelineBank is not null;
                bool hybridReceiverCacheConsumerReady =
                    !hybridReflectionReceiverEnabled ||
                    IsHybridReflectionReceiverCacheSplitEligible(sceneData) &&
                    _meshPipeline.AreHybridReflectionPerformancePipelinesReady(
                        nearFieldDirectSourceEnabled,
                        giCausticReceiverEnabled);
                if (receiverCacheEligible &&
                    !hybridReceiverCacheConsumerReady)
                {
                    receiverCacheEligible = false;
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .PipelineUnavailable,
                        "hybrid cache consumption requires a ready split performance lane; exact pipeline selected");
                }
                if (_simpleDdgiReceiverCacheRequestedMode.UsesCache() &&
                         _settings.GlobalIllumination.DebugView !=
                            GlobalIlluminationDebugView.None &&
                         !receiverCacheDebugView)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason.DebugViewActive,
                        "the active GI debug view requires the exact receiver shader");
                }
                else if (_simpleDdgiReceiverCacheRequestedMode.UsesCache() &&
                         !receiverCacheModeOutputCompatible)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .AdvancedOutputRequiresExact,
                        "legacy depth-only benchmark does not publish advanced MRT outputs");
                }
                else if (_simpleDdgiReceiverCacheRequestedMode.UsesCache() &&
                         !receiverCacheDiagnosticsCompatible)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .AdvancedOutputRequiresExact,
                        "receiver-cache qualification counters do not publish advanced MRT outputs");
                }
                else if (_simpleDdgiReceiverCacheRequestedMode.UsesCache() &&
                         !receiverGatherDispatchable &&
                         _simpleDdgiReceiverCacheRequestedMode !=
                            SimpleDdgiReceiverCacheMode
                                .LegacyDepthOnlyBenchmark)
                {
                    SetSimpleDdgiReceiverCacheFallback(
                        SimpleDdgiReceiverCacheFallbackReason
                            .DispatchUnavailable,
                        "surface-aware receiver gather prerequisites are unavailable");
                }
                // B1 owns an exact opaque receiver producer in this compute
                // gather. C4/C5 attachment output changes how Forward+ consumes
                // GI, but must not suppress an independently enabled B1 capture.
                bool receiverGatherRequired = receiverPipelineBank is not null &&
                                              (receiverCacheEligible ||
                                               receiverGatherDispatchable &&
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
                            receiverPipelineBank,
                            out receiverFeedbackProducer);
                    bool hybridDiffuseVisibilityGather =
                        receiverCacheEligible &&
                        ShouldUseHybridDiffuseVisibilityReceiverGather(
                            hybridReflectionReceiverEnabled,
                            _simpleDdgiReceiverCacheRequestedMode,
                            receiverFeedbackProducer.IsAvailable,
                            _settings.Diagnostics
                                .DdgiForwardEstimateCountersEnabled,
                            receiverCacheDebugView,
                            _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline
                                .Handle != 0);
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
                                receiverFeedbackProducer,
                                receiverPipelineBank,
                                hybridDiffuseVisibilityGather);
                        _simpleDdgiReceiverCacheAvailableForCurrentView =
                            receiverCacheEligible && receiverGatherRecorded;
                        if (_simpleDdgiReceiverCacheAvailableForCurrentView)
                        {
                            bool temporalAdaptiveRequested =
                                _simpleDdgiReceiverCacheRequestedMode ==
                                SimpleDdgiReceiverCacheMode.TemporalAdaptive;
                            _simpleDdgiReceiverCacheEffectiveMode =
                                temporalAdaptiveRequested
                                    ? _adaptiveReceiverExecutedForCurrentView
                                        ? SimpleDdgiReceiverCacheMode
                                            .TemporalAdaptive
                                        : SimpleDdgiReceiverCacheMode
                                            .SurfaceAwareSpatial
                                    : _simpleDdgiReceiverCacheRequestedMode;
                            if (!temporalAdaptiveRequested)
                            {
                                _simpleDdgiReceiverCacheFallbackReason =
                                    SimpleDdgiReceiverCacheFallbackReason.None;
                                _simpleDdgiReceiverCacheFallbackDetail =
                                    string.Empty;
                            }
                            _simpleDdgiReceiverCachePipelineArtifact =
                                _adaptiveReceiverExecutedForCurrentView
                                    ? "ddgi_simple_receiver_cache_{classify,adaptive,adaptive-b1,adaptive-b1-missing,resolve-adaptive}.comp.spv@adaptive-abi-v2"
                                : receiverCacheDebugView
                                    ? "forward_*_ddgi_cache_debug.frag.spv@surface-abi-v1"
                                    : _simpleDdgiReceiverCacheEffectiveMode ==
                                    SimpleDdgiReceiverCacheMode
                                        .LegacyDepthOnlyBenchmark
                                    ? "forward_*_ddgi_cache_legacy.frag.spv@depth-only-benchmark"
                                    : _settings.Diagnostics
                                        .DdgiForwardEstimateCountersEnabled
                                    ? "forward_*_ddgi_cache_required_diagnostics.frag.spv@surface-abi-v1"
                                    : hybridDiffuseVisibilityGather
                                    ? "ddgi_simple_receiver_cache_diffuse_visibility.comp.spv+forward_*_ddgi_cache_required.frag.spv@surface-abi-v1"
                                    : "forward_*_ddgi_cache_required.frag.spv@surface-abi-v1";
                        }
                        else if (receiverCacheEligible)
                        {
                            SetSimpleDdgiReceiverCacheFallback(
                                SimpleDdgiReceiverCacheFallbackReason
                                    .DispatchUnavailable,
                                "surface-aware receiver-cache dispatch was not recorded");
                        }
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

                if (_simpleDdgiReceiverCacheAvailableForCurrentView)
                {
                    PrepareSimpleDdgiMaskedFeedbackCompaction(
                        cmd,
                        frameIndex,
                        receiverFeedbackCaptureOpen &&
                        _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView &&
                        receiverFeedbackProducer.IsAvailable);
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
                    if (!sparseHybridLobePayloadEnabled)
                    {
                        hybridReflectionReceiverBinding.LobeExtension
                            .TransitionToColorAttachment(cmd);
                    }
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
                var colorAttachments = stackalloc RenderingAttachmentInfo[6];
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
                            : giCausticReceiverEnabled
                                ? 2
                                : 1;
                    colorAttachments[hybridAttachmentIndex] = ColorAttachment(
                        hybridReflectionReceiverBinding!.ReceiverPayload.View,
                        ImageLayout.ColorAttachmentOptimal,
                        AttachmentLoadOp.Clear,
                        AttachmentStoreOp.Store,
                        new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
                    if (!sparseHybridLobePayloadEnabled)
                    {
                        colorAttachments[hybridAttachmentIndex + 1] =
                            ColorAttachment(
                                hybridReflectionReceiverBinding.LobeExtension.View,
                                ImageLayout.ColorAttachmentOptimal,
                                AttachmentLoadOp.Clear,
                                AttachmentStoreOp.Store,
                                new ClearValue(new ClearColorValue(
                                    0.0f,
                                    0.0f,
                                    0.0f,
                                    0.0f)));
                    }
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
                            hybridReflectionReceiverEnabled,
                            sparseHybridLobePayloadEnabled),
                    PColorAttachments = colorAttachments,
                    PDepthAttachment = &depthAttachment,
                    PStencilAttachment = null
                };

                var fragmentShadingRateAttachment =
                    new RenderingFragmentShadingRateAttachmentInfoKHR
                    {
                        SType = StructureType
                            .RenderingFragmentShadingRateAttachmentInfoKhr
                    };
                if (sceneData.VariableRateShadingActive != 0 &&
                    _context.FragmentShadingRateSupported)
                {
                    Extent2D attachmentTexel = _context
                        .FragmentShadingRateAttachmentTexelSize;
                    fragmentShadingRateAttachment.ImageView =
                        _renderTargets.VariableRateShading.View;
                    fragmentShadingRateAttachment.ImageLayout =
                        ImageLayout
                            .FragmentShadingRateAttachmentOptimalKhr;
                    fragmentShadingRateAttachment
                        .ShadingRateAttachmentTexelSize = attachmentTexel;
                    renderingInfo.PNext =
                        &fragmentShadingRateAttachment;
                }

                _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

                bool useOpaqueCompute = _opaqueComputeSupported &&
                    sceneData.OpaqueVisibilityCompleted && !_recordingReflectionCapture &&
                    _bufferManager != null && !receiverFeedbackCaptureOpen &&
                    ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline &&
                    (_opaqueCompute == null || _opaqueCompute.PerformanceMask ==
                        MeshPipeline.ResolveForwardPerformanceSpecializationMask(_settings)) &&
                    _simpleDdgiReceiverCacheRequestedMode == SimpleDdgiReceiverCacheMode.Exact &&
                    !nearFieldDirectSourceEnabled && !giCausticReceiverEnabled && !materialTransportProvenanceEnabled &&
                    sceneData.VariableRateShadingActive == 0 && sceneData.DebugViewMode == 0 &&
                    sceneData.TransparencyDebugView == TransparencyDebugView.None &&
                    sceneData.AmbientOcclusionDebugView == AmbientOcclusionDebugView.None;

                sceneData.ForwardTaskInvocations = 0;
                sceneData.ForwardSimpleMeshletCount = 0;
                sceneData.ForwardFullMaterialMeshletCount = 0;
                sceneData.ForwardLocalProbeMeshletCount = 0;
                sceneData.ForwardShadowReceiverMeshletCapacity = 0;
                sceneData.SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData);
                sceneData.SceneSubmissionForwardTaskShader =
                    SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull;
                sceneData.SceneSubmissionIndirectDispatchSkipReason =
                    sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                        ? "GPU compaction inactive"
                        : "indirect dispatch disabled";
                if (!useOpaqueCompute)
                {
                    if (_meshPipeline.TasklessSubmissionEnabled &&
                        sceneData.SceneSubmissionGpuCompactionActive &&
                        sceneData.SceneSubmissionGpuOpaqueCandidateCount > 0 &&
                        sceneData.SceneSubmissionGpuCompactedOpaqueCapacity > 0 &&
                        sceneData.SceneSubmissionFallbackReason.Length == 0)
                    {
                        if (sceneData.ForwardVisibilityCompactionActive)
                        {
                            sceneData.SceneSubmissionForwardPath =
                                SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect;
                            sceneData.SceneSubmissionForwardTaskShader =
                                SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly;
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
                            int compactedDrawCapacity =
                                ResolveCompactedForwardCapacityPlan(sceneData)
                                    .AggregateCapacity;
                            string indirectSkipReason = BuildSceneOpaqueIndirectDispatchSkipReason(sceneData);
                            sceneData.SceneSubmissionIndirectDispatchSkipReason = indirectSkipReason;
                            if (indirectSkipReason.Length == 0)
                            {
                                sceneData.SceneSubmissionForwardPath =
                                    SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect;
                                sceneData.SceneSubmissionForwardTaskShader =
                                    SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly;
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
                                sceneData.SceneSubmissionForwardPath =
                                    SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                                sceneData.SceneSubmissionForwardTaskShader =
                                    SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
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
                            int compactedDrawCapacity =
                                ResolveCompactedForwardCapacityPlan(sceneData)
                                    .AggregateCapacity;
                            sceneData.SceneSubmissionForwardPath =
                                SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect;
                            sceneData.SceneSubmissionForwardTaskShader =
                                SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter;
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
                        sceneData.SceneSubmissionForwardPath =
                            SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData);
                        ForwardOpaqueVariantSelection variantSelection = ResolveOpaqueVariantSelection(sceneData);
                        sceneData.ForwardSimpleMeshletCount = variantSelection.SimpleMeshletCount;
                        sceneData.ForwardFullMaterialMeshletCount = variantSelection.FullMaterialMeshletCount;
                        sceneData.ForwardLocalProbeMeshletCount = variantSelection.LocalProbeMeshletCount;
                        sceneData.ForwardShadowReceiverMeshletCapacity =
                            ResolveForwardShadowReceiverMeshletCapacity(sceneData);

                        DrawForwardBucket(
                            cmd,
                            sceneData,
                            variantSelection.UseSimpleGlobalIblPipeline
                                ? ForwardOpaquePipelineFamily.Simple
                                : ForwardOpaquePipelineFamily.Full,
                            sceneData.SimpleOpaqueMeshletCount,
                            BindlessIndex.MeshletDrawBufferBase,
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                        DrawForwardBucket(
                            cmd,
                            sceneData,
                            variantSelection.UseSimpleGlobalIblPipeline
                                ? ForwardOpaquePipelineFamily.SimpleFullInput
                                : ForwardOpaquePipelineFamily.Full,
                            sceneData.SimpleNormalOpaqueMeshletCount,
                            BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase,
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                        DrawForwardBucket(
                            cmd,
                            sceneData,
                            ForwardOpaquePipelineFamily.Full,
                            sceneData.FullOpaqueMeshletCount,
                            BindlessIndex.FullOpaqueMeshletDrawBufferBase,
                            nearFieldDirectSourceEnabled,
                            giCausticReceiverEnabled);
                    }
                }

                if (useOpaqueCompute)
                {
                    _context.KhrDynamicRendering.CmdEndRendering(cmd);
                    if (_opaqueCompute == null)
                    {
                        _opaqueCompute = new OpaqueVisibilityCompute(_context, _bindlessHeap, _bufferManager!, _renderTargets,
                            _giPipelineCacheService, MeshPipeline.ResolveForwardPerformanceSpecializationMask(_settings));
                        Console.WriteLine("Opaque shading backend: VisibilityCompute (exact, primitive quads).");
                    }
                    _opaqueCompute.Record(cmd, frameIndex, CreateOpaqueComputePushConstants(sceneData),
                        hybridReflectionReceiverEnabled, sparseHybridLobePayloadEnabled);
                    sceneData.SceneSubmissionForwardPath = "VisibilityCompute";
                    sceneData.SceneSubmissionForwardTaskShader = "opaque_shade.comp";
                    sceneData.SceneSubmissionIndirectDispatchSkipReason = string.Empty;
                    _forwardGiExactGatherUsedForCurrentView = ShouldApplyGlobalIllumination(sceneData);
                    _simpleDdgiReceiverCachePipelineArtifact = "opaque-compute-exact-ddgi";
                }
                else if (nearFieldDirectSourceEnabled)
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

                RecordSimpleDdgiMaskedFeedbackCompaction(
                    cmd,
                    frameIndex,
                    sceneData,
                    renderExtent,
                    timestamps);

                if (nearFieldTraceResolutionEnabled)
                {
                    RecordTraceResolutionNearFieldSource(
                        cmd,
                        sceneData,
                        nearFieldDirectSourceBinding!);
                }

                if (hybridReflectionReceiverEnabled)
                {
                    hybridReflectionReceiverBinding!.ReceiverPayload
                        .TransitionToShaderRead(cmd);
                    if (sparseHybridLobePayloadEnabled)
                    {
                        PublishSparseHybridLobePayload(cmd, frameIndex);
                    }
                    else
                    {
                        hybridReflectionReceiverBinding.LobeExtension
                            .TransitionToStorageReadWrite(cmd);
                    }
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
                _maskedFeedbackCompactionActiveForCurrentView = false;
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
                ReflectionMode.StaticProbesAndPlanar or
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
                ReflectionMode.StaticProbesAndPlanar or
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
            sceneData.ForwardLocalProbeMeshletCount =
                RequiresLocalReflectionProbeEvaluation(sceneData) ? meshletCount : 0;
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

        internal static CompactedForwardCapacityPlan
            ResolveCompactedForwardCapacityPlan(
                Data.SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            return new CompactedForwardCapacityPlan(
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleOpaqueCapacity),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleOpaqueDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleOpaqueDoubleSidedCapacity),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleNormalOpaqueCapacity),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleNormalOpaqueDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedSimpleNormalOpaqueDoubleSidedCapacity),
                Math.Max(
                    0,
                    sceneData.SceneSubmissionGpuCompactedFullOpaqueCapacity),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedFullOpaqueDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData
                        .SceneSubmissionGpuCompactedFullOpaqueDoubleSidedCapacity),
                Math.Max(
                    0,
                    sceneData.SceneSubmissionGpuCompactedOpaqueCapacity));
        }

        internal readonly record struct CompactedForwardCapacityPlan(
            int SimpleCapacity,
            int SimpleDoubleSidedBase,
            int SimpleDoubleSidedCapacity,
            int SimpleNormalCapacity,
            int SimpleNormalDoubleSidedBase,
            int SimpleNormalDoubleSidedCapacity,
            int FullCapacity,
            int FullDoubleSidedBase,
            int FullDoubleSidedCapacity,
            int AggregateCapacity);

        private string BuildSceneOpaqueIndirectDispatchSkipReason(Data.SceneRenderingData sceneData)
        {
            if (_bufferManager == null)
                return "scene opaque indirect dispatch buffer unavailable";

            ulong finalDispatchOffset = sceneData
                .SceneSubmissionSidedRasterSpecializationActive
                ? SceneOpaqueCompactionPass
                    .GetFullOpaqueDoubleSidedIndirectDispatchOffset()
                : SceneOpaqueCompactionPass
                    .GetFullOpaqueIndirectDispatchOffset();
            return SceneSubmissionDiagnosticsPolicy.BuildIndirectDispatchSkipReason(
                sceneData,
                finalDispatchOffset +
                (ulong)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
        }

        private void DrawForwardBucket(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            ForwardOpaquePipelineFamily pipelineFamily,
            int meshletCount,
            int meshletDrawBufferBaseIndex,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            if (meshletCount <= 0)
                return;

            bool receiverCacheEnabled = !_recordingTraceResolutionNearFieldSource &&
                                        !_simpleDdgiReflectionFeedbackRequiredForCurrentView &&
                                        ShouldUseSimpleDdgiReceiverCacheForDraw();
            bool disabledBenchmarkPipeline =
                !_recordingTraceResolutionNearFieldSource &&
                ShouldUseForwardGiDisabledBenchmarkPipeline();
            ForwardOpaquePipelineKey pipelineKey = BuildForwardPipelineKey(
                sceneData,
                pipelineFamily,
                receiverCacheEnabled,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled,
                disabledBenchmarkPipeline);
            bool receiverCacheSplit =
                ShouldUseHybridReflectionReceiverCacheSplit(
                    sceneData,
                    pipelineKey);
            if (!TryResolveForwardOpaquePipeline(
                    pipelineKey,
                    receiverCacheEnabled,
                    receiverCacheSplit,
                    out VkPipeline pipeline,
                    out VkPipeline receiverCacheFallbackPipeline))
            {
                if (!receiverCacheEnabled)
                {
                    throw new InvalidOperationException(
                        $"No opaque forward pipeline is available for {pipelineKey}.");
                }

                pipelineKey = pipelineKey with
                {
                    Features = pipelineKey.Features &
                        ~ForwardOpaquePipelineFeatures.ReceiverCache
                };
                if (!_meshPipeline.TryResolveForwardOpaquePipeline(
                        pipelineKey,
                        out pipeline))
                {
                    throw new InvalidOperationException(
                        $"No exact opaque fallback pipeline is available for {pipelineKey}.");
                }
                _simpleDdgiReceiverCacheAvailableForCurrentView = false;
                receiverCacheSplit = false;
                receiverCacheFallbackPipeline = default;
                SetSimpleDdgiReceiverCacheFallback(
                    SimpleDdgiReceiverCacheFallbackReason.PipelineUnavailable,
                    "surface-aware opaque pipeline unavailable; exact pipeline selected");
            }

            bool giDisabledPipeline = pipelineKey.Has(
                ForwardOpaquePipelineFeatures.GlobalIlluminationDisabled);
            bool receiverCachePipeline = pipelineKey.Has(
                ForwardOpaquePipelineFeatures.ReceiverCache);
            if (!_recordingTraceResolutionNearFieldSource)
            {
                _simpleDdgiReceiverCacheConsumedForCurrentView |=
                    receiverCachePipeline;
                _forwardGiDisabledBenchmarkPipelineUsedForCurrentView |=
                    giDisabledPipeline;
                _forwardGiExactGatherUsedForCurrentView |=
                    !giDisabledPipeline &&
                    !receiverCachePipeline &&
                    sceneData.SimpleDdgiActive != 0 &&
                    ShouldApplyGlobalIllumination(sceneData);
            }
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            // The reflected prepass uses the Full mesh producer with a
            // depth-only pipeline. Its culling and reverse-Z comparison are
            // static; issuing dynamic setters after binding it is invalid.
            if (!_recordingAutomaticPlanarDepthPrepass)
            {
                _context.Api.CmdSetCullMode(cmd, CullModeFlags.None);
                _context.Api.CmdSetDepthCompareOp(cmd, CompareOp.GreaterOrEqual);
            }

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
                PackedLightDispatch = Data.GPUForwardPushConstants
                    .PackLightDispatch(
                        sceneData.LightCount,
                        sceneData.LocalLightCount,
                        sceneData.DirectionalLightIndex0,
                        sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled
                    ? (uint)sceneData.HiZTestMode
                    : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode,
                    globalIlluminationEnabled: ShouldApplyGlobalIllumination(sceneData),
                    screenSpaceGlobalIlluminationEnabled: false,
                    ambientOcclusionBentNormalMode:
                        (uint)sceneData.AmbientOcclusionBentNormalMode),
                DiagnosticFlags = _recordingTraceResolutionNearFieldSource
                    ? 0u
                    : Data.GPUForwardPushConstants.PackDiagnosticFlags(
                    ShouldCollectDdgiForwardEstimateCounters(sceneData),
                    ShouldCollectDdgiClipmapCoverageCounters(sceneData),
                    ShouldCollectDirectionalShadowReceiverCounters(sceneData),
                    (uint)sceneData.DirectionalShadowPreviewCascade,
                    materialTransportProvenanceEnabled:
                    !nearFieldDirectSourceEnabled &&
                    !giCausticReceiverEnabled &&
                    ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCachePipeline,
                    geometricSpecularAntialiasingEnabled:
                        sceneData.SpecularAntialiasingMode ==
                        SpecularAntialiasingMode.GeometricVariance),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    !_recordingTraceResolutionNearFieldSource &&
                    _recordingReflectionCapture,
                    _reflectionFeedbackCubemapArrayLayer)
            };
            if (_recordingTraceResolutionNearFieldSource)
            {
                pushConstants.DiagnosticFlags |=
                    Data.GPUForwardPushConstants.PackTraceResolutionScale(
                        _traceResolutionNearFieldSourceScale);
            }

            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (!_recordingTraceResolutionNearFieldSource)
                sceneData.ForwardTaskInvocations += meshletCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
            if (receiverCacheSplit)
            {
                _context.Api.CmdBindPipeline(
                    cmd,
                    PipelineBindPoint.Graphics,
                    receiverCacheFallbackPipeline);
                _context.ExtMeshShader.CmdDrawMeshTask(
                    cmd,
                    (uint)meshletCount,
                    1,
                    1);
            }
        }

        private void DrawCompactedForwardBucketsIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            bool useSimpleGlobalIblPipeline = ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
            CompactedForwardCapacityPlan capacities =
                ResolveCompactedForwardCapacityPlan(sceneData);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.CompactedSimple
                    : ForwardOpaquePipelineFamily.CompactedFull,
                capacities.SimpleCapacity,
                capacities.SimpleDoubleSidedBase,
                capacities.SimpleDoubleSidedCapacity,
                BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetSimpleOpaqueIndirectDispatchOffset(),
                SceneOpaqueCompactionPass.GetSimpleOpaqueDoubleSidedIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.CompactedSimpleFullInput
                    : ForwardOpaquePipelineFamily.CompactedFull,
                capacities.SimpleNormalCapacity,
                capacities.SimpleNormalDoubleSidedBase,
                capacities.SimpleNormalDoubleSidedCapacity,
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                SceneOpaqueCompactionPass.GetSimpleNormalOpaqueDoubleSidedIndirectDispatchOffset(),
                sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                ForwardOpaquePipelineFamily.CompactedFull,
                capacities.FullCapacity,
                capacities.FullDoubleSidedBase,
                capacities.FullDoubleSidedCapacity,
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                SceneOpaqueCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                SceneOpaqueCompactionPass.GetFullOpaqueDoubleSidedIndirectDispatchOffset(),
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
                    ? ForwardOpaquePipelineFamily.CompactedSimple
                    : ForwardOpaquePipelineFamily.CompactedFull,
                Math.Max(0, sceneData.ForwardVisibilitySimpleCapacity),
                Math.Max(
                    0,
                    sceneData.ForwardVisibilitySimpleDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData.ForwardVisibilitySimpleDoubleSidedCapacity),
                BindlessIndex.ForwardVisibleSimpleOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetSimpleOpaqueIndirectDispatchOffset(),
                ForwardVisibilityCompactionPass.GetSimpleOpaqueDoubleSidedIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.CompactedSimpleFullInput
                    : ForwardOpaquePipelineFamily.CompactedFull,
                Math.Max(0, sceneData.ForwardVisibilitySimpleNormalCapacity),
                Math.Max(
                    0,
                    sceneData
                        .ForwardVisibilitySimpleNormalDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData
                        .ForwardVisibilitySimpleNormalDoubleSidedCapacity),
                BindlessIndex.ForwardVisibleSimpleNormalOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetSimpleNormalOpaqueIndirectDispatchOffset(),
                ForwardVisibilityCompactionPass.GetSimpleNormalOpaqueDoubleSidedIndirectDispatchOffset(),
                sceneData.ForwardVisibilityIndirectDispatchBuffer,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucketIndirect(
                cmd,
                sceneData,
                ForwardOpaquePipelineFamily.CompactedFull,
                Math.Max(0, sceneData.ForwardVisibilityFullCapacity),
                Math.Max(
                    0,
                    sceneData.ForwardVisibilityFullDoubleSidedBase),
                Math.Max(
                    0,
                    sceneData.ForwardVisibilityFullDoubleSidedCapacity),
                BindlessIndex.ForwardVisibleFullOpaqueMeshletDrawBufferBase,
                ForwardVisibilityCompactionPass.GetFullOpaqueIndirectDispatchOffset(),
                ForwardVisibilityCompactionPass.GetFullOpaqueDoubleSidedIndirectDispatchOffset(),
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
            CompactedForwardCapacityPlan capacities =
                ResolveCompactedForwardCapacityPlan(sceneData);
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.Simple
                    : ForwardOpaquePipelineFamily.Full,
                capacities.SimpleCapacity,
                BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucket(
                cmd,
                sceneData,
                useSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.SimpleFullInput
                    : ForwardOpaquePipelineFamily.Full,
                capacities.SimpleNormalCapacity,
                BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
            DrawForwardBucket(
                cmd,
                sceneData,
                ForwardOpaquePipelineFamily.Full,
                capacities.FullCapacity,
                BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
        }

        private void DrawForwardBucketIndirect(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            ForwardOpaquePipelineFamily pipelineFamily,
            int meshletCapacity,
            int doubleSidedFirstDraw,
            int doubleSidedMeshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectOffset,
            ulong doubleSidedIndirectOffset,
            BufferHandle indirectBufferHandle,
            bool nearFieldDirectSourceEnabled = false,
            bool giCausticReceiverEnabled = false)
        {
            if ((meshletCapacity <= 0 && doubleSidedMeshletCapacity <= 0) ||
                _bufferManager == null)
                return;

            bool sidedStreams = sceneData.ForwardVisibilityCompactionActive
                ? sceneData.ForwardVisibilitySidedStreamsActive
                : sceneData.SceneSubmissionSidedRasterSpecializationActive;
            if (meshletCapacity > 0)
            {
                DrawForwardBucketIndirectCore(
                    cmd,
                    sceneData,
                    pipelineFamily,
                    meshletCapacity,
                    meshletDrawBufferBaseIndex,
                    indirectOffset,
                    indirectBufferHandle,
                    firstDraw: 0u,
                    oneSided: sidedStreams,
                    depthEqual: sidedStreams,
                    nearFieldDirectSourceEnabled,
                    giCausticReceiverEnabled);
            }
            if (!sidedStreams || doubleSidedMeshletCapacity <= 0)
                return;

            DrawForwardBucketIndirectCore(
                cmd,
                sceneData,
                pipelineFamily,
                doubleSidedMeshletCapacity,
                meshletDrawBufferBaseIndex,
                doubleSidedIndirectOffset,
                indirectBufferHandle,
                firstDraw: checked((uint)doubleSidedFirstDraw),
                oneSided: false,
                depthEqual: true,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled);
        }

        private void DrawForwardBucketIndirectCore(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            ForwardOpaquePipelineFamily pipelineFamily,
            int meshletCapacity,
            int meshletDrawBufferBaseIndex,
            ulong indirectOffset,
            BufferHandle indirectBufferHandle,
            uint firstDraw,
            bool oneSided,
            bool depthEqual,
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled)
        {
            if (meshletCapacity <= 0 || _bufferManager == null)
                return;

            bool receiverCacheEnabled = !_recordingTraceResolutionNearFieldSource &&
                                        !_simpleDdgiReflectionFeedbackRequiredForCurrentView &&
                                        ShouldUseSimpleDdgiReceiverCacheForDraw();
            bool disabledBenchmarkPipeline =
                !_recordingTraceResolutionNearFieldSource &&
                ShouldUseForwardGiDisabledBenchmarkPipeline();
            ForwardOpaquePipelineKey pipelineKey = BuildForwardPipelineKey(
                sceneData,
                pipelineFamily,
                receiverCacheEnabled,
                nearFieldDirectSourceEnabled,
                giCausticReceiverEnabled,
                disabledBenchmarkPipeline);
            bool receiverCacheSplit =
                ShouldUseHybridReflectionReceiverCacheSplit(
                    sceneData,
                    pipelineKey);
            if (!TryResolveForwardOpaquePipeline(
                    pipelineKey,
                    receiverCacheEnabled,
                    receiverCacheSplit,
                    out VkPipeline pipeline,
                    out VkPipeline receiverCacheFallbackPipeline))
            {
                if (!receiverCacheEnabled)
                {
                    throw new InvalidOperationException(
                        $"No indirect opaque forward pipeline is available for {pipelineKey}.");
                }

                pipelineKey = pipelineKey with
                {
                    Features = pipelineKey.Features &
                        ~ForwardOpaquePipelineFeatures.ReceiverCache
                };
                if (!_meshPipeline.TryResolveForwardOpaquePipeline(
                        pipelineKey,
                        out pipeline))
                {
                    throw new InvalidOperationException(
                        $"No exact indirect opaque fallback pipeline is available for {pipelineKey}.");
                }
                _simpleDdgiReceiverCacheAvailableForCurrentView = false;
                receiverCacheSplit = false;
                receiverCacheFallbackPipeline = default;
                SetSimpleDdgiReceiverCacheFallback(
                    SimpleDdgiReceiverCacheFallbackReason.PipelineUnavailable,
                    "surface-aware indirect opaque pipeline unavailable; exact pipeline selected");
            }

            bool giDisabledPipeline = pipelineKey.Has(
                ForwardOpaquePipelineFeatures.GlobalIlluminationDisabled);
            bool receiverCachePipeline = pipelineKey.Has(
                ForwardOpaquePipelineFeatures.ReceiverCache);
            if (!_recordingTraceResolutionNearFieldSource)
            {
                _simpleDdgiReceiverCacheConsumedForCurrentView |=
                    receiverCachePipeline;
                _forwardGiDisabledBenchmarkPipelineUsedForCurrentView |=
                    giDisabledPipeline;
                _forwardGiExactGatherUsedForCurrentView |=
                    !giDisabledPipeline &&
                    !receiverCachePipeline &&
                    sceneData.SimpleDdgiActive != 0 &&
                    ShouldApplyGlobalIllumination(sceneData);
            }
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            _context.Api.CmdSetCullMode(
                cmd,
                oneSided ? CullModeFlags.BackBit : CullModeFlags.None);
            _context.Api.CmdSetDepthCompareOp(
                cmd,
                !_recordingTraceResolutionNearFieldSource && depthEqual
                    ? CompareOp.Equal
                    : CompareOp.GreaterOrEqual);

            if (!Data.GPUForwardPushConstants.TryPackTransparentDrawRange(
                    checked((uint)meshletDrawBufferBaseIndex),
                    firstDraw,
                    out uint packedDrawBufferBaseIndex))
            {
                throw new InvalidOperationException(
                    "The compacted Forward+ sided range cannot be represented " +
                    "by the packed draw-buffer push constant.");
            }

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
                MeshletDrawBufferBaseIndex = packedDrawBufferBaseIndex,
                PackedLightDispatch = Data.GPUForwardPushConstants
                    .PackLightDispatch(
                        sceneData.LightCount,
                        sceneData.LocalLightCount,
                        sceneData.DirectionalLightIndex0,
                        sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled
                    ? (uint)sceneData.HiZTestMode
                    : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = Data.GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    sceneData.AmbientOcclusionEnabled,
                    (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: true,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode,
                    globalIlluminationEnabled: ShouldApplyGlobalIllumination(sceneData),
                    screenSpaceGlobalIlluminationEnabled: false,
                    ambientOcclusionBentNormalMode:
                        (uint)sceneData.AmbientOcclusionBentNormalMode),
                DiagnosticFlags = _recordingTraceResolutionNearFieldSource
                    ? 0u
                    : Data.GPUForwardPushConstants.PackDiagnosticFlags(
                    ShouldCollectDdgiForwardEstimateCounters(sceneData),
                    ShouldCollectDdgiClipmapCoverageCounters(sceneData),
                    ShouldCollectDirectionalShadowReceiverCounters(sceneData),
                    (uint)sceneData.DirectionalShadowPreviewCascade,
                    materialTransportProvenanceEnabled:
                    !nearFieldDirectSourceEnabled &&
                    !giCausticReceiverEnabled &&
                    ShouldWriteMaterialTransportProvenance(),
                    ddgiReceiverCacheEnabled: receiverCachePipeline,
                    geometricSpecularAntialiasingEnabled:
                        sceneData.SpecularAntialiasingMode ==
                        SpecularAntialiasingMode.GeometricVariance),
                CaptureFlags = Data.GPUForwardPushConstants.PackCaptureFlags(
                    !_recordingTraceResolutionNearFieldSource &&
                    _recordingReflectionCapture,
                    _reflectionFeedbackCubemapArrayLayer)
            };
            if (_recordingTraceResolutionNearFieldSource)
            {
                pushConstants.DiagnosticFlags |=
                    Data.GPUForwardPushConstants.PackTraceResolutionScale(
                        _traceResolutionNearFieldSourceScale);
            }

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
            if (!_recordingTraceResolutionNearFieldSource)
            {
                sceneData.ForwardTaskInvocations = Math.Max(
                    sceneData.ForwardTaskInvocations,
                    sceneData.SceneSubmissionGpuIndirectMeshletTaskCount);
                sceneData.ForwardMeshOnlyIndirectDrawCount++;
            }
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                indirectOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            if (receiverCacheSplit)
            {
                _context.Api.CmdBindPipeline(
                    cmd,
                    PipelineBindPoint.Graphics,
                    receiverCacheFallbackPipeline);
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    indirect,
                    indirectOffset,
                    1,
                    (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());
            }
        }

        private bool TryResolveForwardOpaquePipeline(
            in ForwardOpaquePipelineKey pipelineKey,
            bool receiverCacheEnabled,
            bool receiverCacheSplit,
            out VkPipeline pipeline,
            out VkPipeline receiverCacheFallbackPipeline)
        {
            receiverCacheFallbackPipeline = default;
            if (RecordingAutomaticPlanarCapture)
                return _meshPipeline.TryResolveAutomaticPlanarCapturePipeline(
                    pipelineKey, _recordingAutomaticPlanarDepthPrepass, out pipeline);
            ForwardOpaquePipelineFeatures nonCacheDiagnosticFeatures =
                pipelineKey.Features &
                ~(ForwardOpaquePipelineFeatures.ReceiverCache |
                  ForwardOpaquePipelineFeatures.AlphaMaskReceiverFeedback);
            bool ordinaryReceiverCacheVariant =
                pipelineKey.Has(ForwardOpaquePipelineFeatures.ReceiverCache) &&
                nonCacheDiagnosticFeatures == ForwardOpaquePipelineFeatures.None;
            if (receiverCacheEnabled &&
                _settings.GlobalIllumination.DebugView ==
                    GlobalIlluminationDebugView.DdgiReceiverCacheRejection)
            {
                if (!ordinaryReceiverCacheVariant)
                {
                    pipeline = default;
                    return false;
                }

                return _meshPipeline.TryResolveReceiverCacheDebugPipeline(
                    pipelineKey.Family,
                    out pipeline);
            }

            if (receiverCacheEnabled &&
                _simpleDdgiReceiverCacheEffectiveMode ==
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark)
            {
                if (!ordinaryReceiverCacheVariant)
                {
                    pipeline = default;
                    return false;
                }

                return _meshPipeline.TryResolveReceiverCacheLegacyPipeline(
                    pipelineKey.Family,
                    out pipeline);
            }

            if (receiverCacheEnabled &&
                _settings.Diagnostics.DdgiForwardEstimateCountersEnabled)
            {
                // The qualification artifacts intentionally have the ordinary
                // one-target output contract. An unexpected advanced feature
                // combination fails closed into the caller's exact fallback.
                if (!ordinaryReceiverCacheVariant)
                {
                    pipeline = default;
                    return false;
                }

                return _meshPipeline.TryResolveReceiverCacheDiagnosticsPipeline(
                    pipelineKey.Family,
                    out pipeline);
            }

            if (receiverCacheSplit)
            {
                bool nearField = pipelineKey.Has(
                    ForwardOpaquePipelineFeatures.NearFieldDirectSource);
                bool caustic = pipelineKey.Has(
                    ForwardOpaquePipelineFeatures.GiCausticReceiver);
                return _meshPipeline
                    .TryResolveHybridReflectionCacheSplitPipelines(
                        pipelineKey.Family,
                        nearField,
                        caustic,
                        out pipeline,
                        out receiverCacheFallbackPipeline);
            }

            return _meshPipeline.TryResolveForwardOpaquePipeline(
                pipelineKey,
                out pipeline);
        }

        private bool ShouldDispatchSimpleDdgiReceiverCache(
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool materialTransportProvenanceEnabled)
        {
            bool legacyBenchmark = _simpleDdgiReceiverCacheRequestedMode ==
                SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark;
            if (_settings.Diagnostics.DdgiForwardEstimateCountersEnabled &&
                _simpleDdgiReceiverCacheResolveDiagnosticsPipeline.Handle == 0)
            {
                return false;
            }
            bool receiverCacheDebugView =
                _settings.GlobalIllumination.DebugView ==
                GlobalIlluminationDebugView.DdgiReceiverCacheRejection;
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
                (directionalReceiverActive &&
                 !_simpleDdgiReceiverCacheRequestedMode
                     .CarriesDirectionalRadiancePayload() &&
                 !receiverCacheDebugView) ||
                _settings.Diagnostics.ForceExactForwardGiGatherForBenchmark ||
                !float.IsFinite(environmentFallbackIntensity) ||
                environmentFallbackIntensity > 1.0f ||
                (_settings.GlobalIllumination.DebugView !=
                    GlobalIlluminationDebugView.None &&
                 !receiverCacheDebugView) ||
                (legacyBenchmark && receiverCacheDebugView) ||
                _bufferManager == null ||
                (legacyBenchmark
                    ? _simpleDdgiReceiverCacheLegacyPipeline.Handle == 0 ||
                      _simpleDdgiReceiverCacheResolveLegacyPipeline.Handle == 0
                    : _simpleDdgiReceiverCachePipeline.Handle == 0 ||
                      _simpleDdgiReceiverCacheResolvePipeline.Handle == 0) ||
                (_settings.Diagnostics.DdgiForwardEstimateCountersEnabled &&
                 _simpleDdgiReceiverCacheResolveDiagnosticsPipeline.Handle == 0) ||
                frameIndex < 0 || frameIndex >= FramesInFlight ||
                !_simpleDdgiReceiverCacheBuffers[frameIndex].IsValid ||
                !_simpleDdgiReceiverCacheSurfaceBuffers[frameIndex].IsValid ||
                _simpleDdgiReceiverCacheOutputSets[frameIndex].Handle == 0 ||
                _simpleDdgiReceiverCacheConsumerSets[frameIndex].Handle == 0 ||
                !_simpleDdgiReceiverGatherBuffers[frameIndex].IsValid ||
                !_simpleDdgiReceiverGatherSurfaceBuffers[frameIndex].IsValid ||
                !_simpleDdgiReceiverPublicationBuffers[frameIndex].IsValid ||
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
                   _simpleDdgiReceiverCacheSurfaceBufferBytes == checked(
                       (ulong)expectedWidth * expectedHeight *
                       SimpleDdgiReceiverSurfaceEntryBytes) &&
                   _simpleDdgiReceiverGatherWidth == expectedGatherWidth &&
                   _simpleDdgiReceiverGatherHeight == expectedGatherHeight &&
                   _simpleDdgiReceiverGatherBufferBytes == checked(
                       (ulong)expectedGatherWidth * expectedGatherHeight *
                       SimpleDdgiReceiverGatherEntryBytes) &&
                   _simpleDdgiReceiverGatherSurfaceBufferBytes == checked(
                       (ulong)expectedGatherWidth * expectedGatherHeight *
                       SimpleDdgiReceiverSurfaceEntryBytes);
        }

        private bool DispatchSimpleDdgiReceiverCache(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            in SimpleDdgiReceiverFeedbackCaptureProducerContract
                receiverFeedbackProducer,
            SimpleDdgiReceiverPipelineBank? pipelineBank,
            bool hybridDiffuseVisibilityGather)
        {
            _adaptiveReceiverExecutedForCurrentView = false;
            _adaptiveReceiverFrameToken =
                SimpleDdgiReceiverCacheFrameToken.Unavailable;
            UpdateSimpleDdgiReceiverPublication(
                cmd,
                frameIndex,
                sceneData);
            if (_simpleDdgiReceiverCacheRequestedMode !=
                SimpleDdgiReceiverCacheMode.TemporalAdaptive)
            {
                bool recorded = DispatchCanonicalSimpleDdgiReceiverCache(
                    cmd,
                    frameIndex,
                    sceneData,
                    renderExtent,
                    receiverFeedbackProducer,
                    pipelineBank,
                    hybridDiffuseVisibilityGather);
                if (recorded)
                    PublishSimpleDdgiReceiverPublication(cmd, frameIndex);
                return recorded;
            }

            // The adaptive gather owns both cache shading and any overlapping
            // exact samples. Its stable missing-cell pass completes the exact
            // coarse lattice and directional tail without reshading cells that
            // already ran adaptively.
            if (DispatchSimpleDdgiReceiverCacheAdaptive(
                    cmd,
                    frameIndex,
                    sceneData,
                    renderExtent,
                    receiverFeedbackProducer,
                    pipelineBank))
            {
                _simpleDdgiReceiverCacheFallbackReason =
                    SimpleDdgiReceiverCacheFallbackReason.None;
                _simpleDdgiReceiverCacheFallbackDetail = string.Empty;
                PublishSimpleDdgiReceiverPublication(cmd, frameIndex);
                return true;
            }

            bool canonicalRecorded =
                DispatchCanonicalSimpleDdgiReceiverCache(
                    cmd,
                    frameIndex,
                sceneData,
                renderExtent,
                receiverFeedbackProducer,
                pipelineBank,
                hybridDiffuseVisibilityGather);
            if (!canonicalRecorded)
                return false;

            bool seeded = SeedSimpleDdgiReceiverCacheAdaptiveHistory(
                cmd,
                frameIndex,
                sceneData,
                renderExtent,
                pipelineBank);
            if (receiverFeedbackProducer.IsAvailable)
            {
                _simpleDdgiReceiverCacheFallbackReason =
                    SimpleDdgiReceiverCacheFallbackReason
                        .FeedbackVariantRequiresExact;
                _simpleDdgiReceiverCacheFallbackDetail =
                    "exact receiver-feedback attribution forced canonical cache execution";
            }
            else
            {
                _simpleDdgiReceiverCacheFallbackReason =
                    SimpleDdgiReceiverCacheFallbackReason
                        .TemporalAdaptiveUnavailable;
                _simpleDdgiReceiverCacheFallbackDetail = seeded
                    ? "canonical cache execution seeded discontinuous or invalid temporal history"
                    : "adaptive receiver-cache runtime unavailable; canonical cache retained";
            }
            PublishSimpleDdgiReceiverPublication(cmd, frameIndex);
            return true;
        }

        private void PublishSimpleDdgiReceiverPublication(
            CommandBuffer commandBuffer,
            int frameIndex)
        {
            if (_bufferManager is null || frameIndex < 0 ||
                frameIndex >= FramesInFlight ||
                !_simpleDdgiReceiverPublicationBuffers[frameIndex].IsValid)
            {
                return;
            }

            BufferHandle handle =
                _simpleDdgiReceiverPublicationBuffers[frameIndex];
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                buffer,
                SimpleDdgiReceiverPublicationAbi.GenerationByteOffset,
                sizeof(uint),
                _receiverPublicationGenerationEnabled
                    ? _receiverPublicationStamp
                    : 0u);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                buffer,
                SimpleDdgiReceiverPublicationAbi.ChangedRegionsByteOffset,
                sizeof(uint),
                _receiverPublicationChangedRegionMask);
            ExecuteAdaptiveReceiverBarrier(
                commandBuffer,
                CreateAdaptiveReceiverBufferBarrier(
                    handle,
                    SimpleDdgiReceiverPublicationAbi.ByteCount,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private bool DispatchCanonicalSimpleDdgiReceiverCache(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            in SimpleDdgiReceiverFeedbackCaptureProducerContract
                receiverFeedbackProducer,
            SimpleDdgiReceiverPipelineBank? pipelineBank,
            bool hybridDiffuseVisibilityGather)
        {
            bool legacyBenchmark = _simpleDdgiReceiverCacheRequestedMode ==
                SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark;
            if (pipelineBank is null || _bufferManager == null ||
                (legacyBenchmark
                    ? _simpleDdgiReceiverCacheLegacyPipeline.Handle == 0 ||
                      _simpleDdgiReceiverCacheResolveLegacyPipeline.Handle == 0
                    : _simpleDdgiReceiverCachePipeline.Handle == 0 ||
                      _simpleDdgiReceiverCacheResolvePipeline.Handle == 0) ||
                frameIndex < 0 || frameIndex >= FramesInFlight)
            {
                return false;
            }

            BufferHandle cacheHandle =
                _simpleDdgiReceiverCacheBuffers[frameIndex];
            BufferHandle gatherHandle =
                _simpleDdgiReceiverGatherBuffers[frameIndex];
            BufferHandle cacheSurfaceHandle =
                _simpleDdgiReceiverCacheSurfaceBuffers[frameIndex];
            BufferHandle gatherSurfaceHandle =
                _simpleDdgiReceiverGatherSurfaceBuffers[frameIndex];
            if (!cacheHandle.IsValid || !gatherHandle.IsValid ||
                !cacheSurfaceHandle.IsValid || !gatherSurfaceHandle.IsValid ||
                _simpleDdgiReceiverCacheOutputSets[frameIndex].Handle == 0)
                return false;

            if (!DispatchSimpleDdgiReceiverGather(
                    cmd,
                    frameIndex,
                    sceneData,
                    renderExtent,
                    receiverFeedbackProducer,
                    legacyBenchmark,
                    hybridDiffuseVisibilityGather))
            {
                return false;
            }

            VkBuffer gatherBuffer = _bufferManager.GetBuffer(gatherHandle);
            VkBuffer gatherSurfaceBuffer =
                _bufferManager.GetBuffer(gatherSurfaceHandle);
            BufferMemoryBarrier2* gatherBarriers =
                stackalloc BufferMemoryBarrier2[2];
            gatherBarriers[0] = new BufferMemoryBarrier2
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
            gatherBarriers[1] = gatherBarriers[0];
            gatherBarriers[1].Buffer = gatherSurfaceBuffer;
            gatherBarriers[1].Size =
                _simpleDdgiReceiverGatherSurfaceBufferBytes;
            var gatherDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = gatherBarriers
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
                legacyBenchmark
                    ? _simpleDdgiReceiverCacheResolveLegacyPipeline
                    : _settings.Diagnostics.DdgiForwardEstimateCountersEnabled
                        ? _simpleDdgiReceiverCacheResolveDiagnosticsPipeline
                        : _simpleDdgiReceiverCacheResolvePipeline);
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
            if (legacyBenchmark)
            {
                var legacyResolveConstants =
                    new GPUSimpleDdgiReceiverCacheLegacyResolvePushConstants
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
                        GPUSimpleDdgiReceiverCacheLegacyResolvePushConstants>(),
                    &legacyResolveConstants);
            }
            else
            {
                var resolveConstants =
                    new GPUSimpleDdgiReceiverCacheResolvePushConstants
                {
                    InverseViewProjectionMatrix =
                        sceneData.InverseViewProjectionMatrix,
                    CameraPositionAndPadding =
                        new Vector4(sceneData.CameraPosition, 0.0f),
                    ScreenWidth = renderExtent.Width,
                    ScreenHeight = renderExtent.Height,
                    GatherWidth = _simpleDdgiReceiverGatherWidth,
                    GatherHeight = _simpleDdgiReceiverGatherHeight,
                    CacheWidth = _simpleDdgiReceiverCacheWidth,
                    CacheHeight = _simpleDdgiReceiverCacheHeight,
                    GatherBufferIndex = checked((uint)
                        (BindlessIndex.SimpleDdgiReceiverGatherBufferBase +
                         frameIndex)),
                    GatherSurfaceBufferIndex = checked((uint)
                        (BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase +
                         frameIndex)),
                    PackedScaleAndEdgeExtents =
                        PackSimpleDdgiReceiverCacheResolveDimensions(
                            renderExtent),
                    DepthTextureIndex = BindlessIndex.DepthTexture,
                    CurrentFrameIndex = sceneData.CurrentFrameIndex
                    };
                _context.Api.CmdPushConstants(
                    cmd,
                    _simpleDdgiReceiverCachePipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<
                        GPUSimpleDdgiReceiverCacheResolvePushConstants>(),
                    &resolveConstants);
            }
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
            VkBuffer cacheSurfaceBuffer =
                _bufferManager.GetBuffer(cacheSurfaceHandle);
            BufferMemoryBarrier2* cacheBarriers =
                stackalloc BufferMemoryBarrier2[4];
            cacheBarriers[0] = new BufferMemoryBarrier2
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
            cacheBarriers[1] = cacheBarriers[0];
            cacheBarriers[1].Buffer = cacheSurfaceBuffer;
            cacheBarriers[1].Size =
                _simpleDdgiReceiverCacheSurfaceBufferBytes;
            // Forward also evaluates the compact directional tail directly
            // from the coarse lattice after surface admission. Publish those
            // producer writes to fragment reads in the same transaction as the
            // resolved cache; the preceding compute-read barrier is not a
            // substitute for this consumer visibility dependency.
            cacheBarriers[2] = cacheBarriers[0];
            cacheBarriers[2].Buffer = gatherBuffer;
            cacheBarriers[2].Size =
                _simpleDdgiReceiverGatherBufferBytes;
            cacheBarriers[3] = cacheBarriers[0];
            cacheBarriers[3].Buffer = gatherSurfaceBuffer;
            cacheBarriers[3].Size =
                _simpleDdgiReceiverGatherSurfaceBufferBytes;
            var cacheDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 4,
                PBufferMemoryBarriers = cacheBarriers
            };
            _context.Api.CmdPipelineBarrier2(cmd, &cacheDependency);
            return true;
        }

        private bool DispatchSimpleDdgiReceiverGather(
            CommandBuffer cmd,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            Extent2D renderExtent,
            in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
            bool legacyBenchmark,
            bool hybridDiffuseVisibilityGather)
        {
            VkPipeline gatherPipeline = producer.IsAvailable
                ? _simpleDdgiReceiverFeedbackPipeline
                : legacyBenchmark
                    ? _simpleDdgiReceiverCacheLegacyPipeline
                    : hybridDiffuseVisibilityGather
                        ? _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline
                        : _simpleDdgiReceiverCachePipeline;
            if (_bufferManager == null ||
                frameIndex < 0 || frameIndex >= FramesInFlight ||
                gatherPipeline.Handle == 0 ||
                !_simpleDdgiReceiverGatherBuffers[frameIndex].IsValid ||
                !_simpleDdgiReceiverGatherSurfaceBuffers[frameIndex].IsValid)
            {
                return false;
            }

            // Evaluate one exact structured gather per coarse receiver block.
            // Adaptive shading may consume only a scheduled subset, while the
            // exact feedback variant retains its independent attribution ABI.
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                gatherPipeline);
            BindBindlessStorageAndTextures(
                cmd,
                _simpleDdgiReceiverCachePipelineLayout,
                PipelineBindPoint.Compute);
            var pushConstants = new GPUSimpleDdgiReceiverCachePushConstants
            {
                InverseViewProjectionMatrix =
                    sceneData.InverseViewProjectionMatrix,
                CameraPositionAndPadding =
                    new Vector4(
                        sceneData.CameraPosition,
                        BitConverter.UInt32BitsToSingle(
                            _receiverPublicationStamp)),
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
                FeedbackControlOffsetWords = producer.IsAvailable
                    ? producer.CandidateControlOffsetWords
                    : 0u,
                FeedbackSamplePeriod = producer.IsAvailable
                    ? producer.ScreenSamplingPeriod
                    : 0u,
                FeedbackSamplePhase = producer.IsAvailable
                    ? producer.ScreenSamplingPhase
                    : 0u,
                FeedbackMaximumOwnersPerTile = producer.IsAvailable
                    ? producer.MaximumUniqueGatherOwnersPerTile
                    : 0u,
                SurfaceBufferIndex = checked((uint)
                    (BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase +
                     frameIndex))
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
            return true;
        }

        private bool TryBeginSimpleDdgiReceiverFeedbackCapture(
            CommandBuffer commandBuffer,
            int frameIndex,
            Data.SceneRenderingData sceneData,
            SimpleDdgiReceiverPipelineBank? pipelineBank,
            out SimpleDdgiReceiverFeedbackCaptureProducerContract producer)
        {
            producer = SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
            ISimpleDdgiReceiverFeedbackCapture? runtime =
                _simpleDdgiReceiverFeedbackRuntime;
            if (pipelineBank is null || runtime is null ||
                !runtime.IsOwnedCaptureReady ||
                sceneData.DdgiFrameSerial == ulong.MaxValue)
            {
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
                sceneData.TransparentObjectCount > 0 &&
                !TransparentForwardPass.RequiresCanonicalRayColorPipeline(
                    sceneData))
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

        private bool ShouldUseSimpleDdgiReceiverCacheForDraw()
        {
            return _simpleDdgiReceiverCacheAvailableForCurrentView &&
                   _simpleDdgiReceiverCacheEffectiveMode.UsesCache() &&
                   ShouldConsumeSimpleDdgiReceiverCache(
                       _settings.QualityPreset,
                       _settings.GlobalIllumination
                           .SimpleDdgiReceiverCacheMode,
                       _settings.Diagnostics
                           .ForceForwardGiReceiverCacheForBenchmark,
                       _settings.Diagnostics
                           .ForceExactForwardGiGatherForBenchmark) &&
                   !_recordingReflectionCapture &&
                   !ShouldWriteMaterialTransportProvenance();
        }

        internal static bool ShouldConsumeSimpleDdgiReceiverCache(
            RenderQualityPreset qualityPreset,
            SimpleDdgiReceiverCacheMode configuredMode,
            bool forceLegacyBenchmark,
            bool forceExact)
        {
            // Surface compatibility, not a preset/vendor allowlist, is the
            // correctness gate. All quality presets may request the spatial
            // cache; unsupported resources and features fail closed per view.
            _ = qualityPreset;
            SimpleDdgiReceiverCacheMode requested =
                SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                    configuredMode,
                    forceLegacyBenchmark,
                    forceExact);
            return requested is
                SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark or
                SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial or
                SimpleDdgiReceiverCacheMode.TemporalAdaptive;
        }

        private void SetSimpleDdgiReceiverCacheFallback(
            SimpleDdgiReceiverCacheFallbackReason reason,
            string detail)
        {
            _simpleDdgiReceiverCacheEffectiveMode =
                SimpleDdgiReceiverCacheMode.Exact;
            _simpleDdgiReceiverCacheFallbackReason = reason;
            _simpleDdgiReceiverCacheFallbackDetail = detail ?? string.Empty;
            _simpleDdgiReceiverCachePipelineArtifact = "forward-exact-ddgi";
        }

        internal bool CanConsumeSimpleDdgiReceiverCacheForCurrentView =>
            ShouldUseSimpleDdgiReceiverCacheForDraw();

        internal bool ConsumedSimpleDdgiReceiverCacheForCurrentView =>
            _simpleDdgiReceiverCacheConsumedForCurrentView;

        internal bool GeneratedSimpleDdgiReceiverCacheForCurrentView =>
            _adaptiveReceiverExecutedForCurrentView;

        internal bool UsedForwardGiDisabledBenchmarkPipelineForCurrentView =>
            _forwardGiDisabledBenchmarkPipelineUsedForCurrentView;

        internal bool UsedForwardGiDisabledPipelineForCurrentView =>
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

        private ForwardOpaquePipelineKey BuildForwardPipelineKey(
            Data.SceneRenderingData sceneData,
            ForwardOpaquePipelineFamily family,
            bool receiverCacheEnabled,
            bool nearFieldDirectSourceEnabled,
            bool giCausticReceiverEnabled,
            bool disabledBenchmarkPipeline)
        {
            bool hybridReflection =
                !_recordingTraceResolutionNearFieldSource &&
                _hybridReflectionReceiverEnabledForCurrentView &&
                !_recordingReflectionCapture;
            bool alphaMaskFeedbackRequired =
                !_recordingTraceResolutionNearFieldSource &&
                _simpleDdgiAlphaMaskFeedbackRequiredForCurrentView;
            bool reflectionFeedbackRequired =
                !_recordingTraceResolutionNearFieldSource &&
                _simpleDdgiReflectionFeedbackRequiredForCurrentView;
            bool feedbackRequired = alphaMaskFeedbackRequired ||
                                    reflectionFeedbackRequired;
            bool advancedOutput = hybridReflection ||
                                  nearFieldDirectSourceEnabled || giCausticReceiverEnabled;
            bool giDisabled = !advancedOutput && !feedbackRequired &&
                              _meshPipeline.GiDisabledPipelinesAvailable &&
                              (disabledBenchmarkPipeline ||
                               ShouldUseProductionForwardGiDisabledPipeline(sceneData));

            ForwardOpaquePipelineFeatures features =
                ForwardOpaquePipelineFeatures.None;
            if (receiverCacheEnabled && !giDisabled)
            {
                features |= ForwardOpaquePipelineFeatures.ReceiverCache;
            }

            if (giDisabled)
            {
                features |= ForwardOpaquePipelineFeatures
                    .GlobalIlluminationDisabled;
            }

            if ((feedbackRequired && !advancedOutput) ||
                (receiverCacheEnabled && alphaMaskFeedbackRequired))
            {
                features |= ForwardOpaquePipelineFeatures
                    .AlphaMaskReceiverFeedback;
            }

            if (nearFieldDirectSourceEnabled)
            {
                features |= ForwardOpaquePipelineFeatures
                    .NearFieldDirectSource;
            }

            if (giCausticReceiverEnabled)
            {
                features |= ForwardOpaquePipelineFeatures.GiCausticReceiver;
            }

            if (hybridReflection)
            {
                features |= ForwardOpaquePipelineFeatures
                    .HybridReflectionReceiver;
            }

            return new ForwardOpaquePipelineKey(family, features);
        }

        private bool ShouldUseHybridReflectionReceiverCacheSplit(
            Data.SceneRenderingData sceneData,
            in ForwardOpaquePipelineKey pipelineKey)
        {
            // Alpha-mask attribution and every bent-normal mode can require
            // exact normal-dependent work on a cache-accepted fragment, so
            // they retain the combined quality path. A fully opaque,
            // bent-normal-off hybrid receiver has complementary cache/exact
            // ownership and can use two lower-pressure native programs whose
            // bent-normal mode specializes to zero.
            return pipelineKey.Has(
                       ForwardOpaquePipelineFeatures.ReceiverCache) &&
                   pipelineKey.Has(
                       ForwardOpaquePipelineFeatures.HybridReflectionReceiver) &&
                   IsHybridReflectionReceiverCacheSplitEligible(sceneData);
        }

        private bool IsHybridReflectionReceiverCacheSplitEligible(
            Data.SceneRenderingData sceneData)
        {
            // The combined hybrid/cache program is deliberately not admitted:
            // alpha-mask or bent-normal ownership keeps both expensive paths
            // live in one fragment program. Only publish the cache when every
            // opaque bucket can use the measured low-pressure split lanes.
            return _settings.IsPerformanceOptimizationEnabled(
                       PerformanceOptimizationFeature
                           .SplitHybridForwardPrograms) &&
                   sceneData.MaskedMeshletCount <= 0 &&
                   sceneData.AmbientOcclusionBentNormalMode ==
                       AmbientOcclusionBentNormalMode.Off &&
                   !_simpleDdgiAlphaMaskFeedbackRequiredForCurrentView &&
                   !_simpleDdgiReflectionFeedbackRequiredForCurrentView &&
                   _simpleDdgiReceiverCacheEffectiveMode !=
                       SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark &&
                   !_settings.Diagnostics.DdgiForwardEstimateCountersEnabled &&
                   _settings.GlobalIllumination.DebugView ==
                       GlobalIlluminationDebugView.None;
        }

        internal static bool ShouldUseHybridDiffuseVisibilityReceiverGather(
            bool hybridReflectionReceiverEnabled,
            SimpleDdgiReceiverCacheMode requestedMode,
            bool exactFeedbackProducerAvailable,
            bool diagnosticsEnabled,
            bool receiverCacheDebugView,
            bool specializedPipelineAvailable) =>
            hybridReflectionReceiverEnabled &&
            requestedMode == SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial &&
            !exactFeedbackProducerAvailable &&
            !diagnosticsEnabled &&
            !receiverCacheDebugView &&
            specializedPipelineAvailable;

        private bool ShouldUseProductionForwardGiDisabledPipeline(
            Data.SceneRenderingData sceneData)
        {
            return _settings.GlobalIllumination.DebugView ==
                   GlobalIlluminationDebugView.None &&
                   !_recordingReflectionCapture &&
                   !ShouldWriteMaterialTransportProvenance() &&
                   !ShouldApplyGlobalIllumination(sceneData);
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
                    _meshPipeline.NearFieldDirectSourceConfiguration
                            .SourceProducerMode ==
                        SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster
                        ? _nearFieldDirectSourceBinding.DirectSource.Extent
                        : renderExtent,
                    out string failure))
            {
                NearFieldDirectSourceFailureReason = failure;
                return false;
            }

            if (_meshPipeline.NearFieldDirectSourceConfiguration
                    .SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster)
            {
                if (!TryResolveTraceResolutionExecutionExtent(
                        _nearFieldDirectSourceBinding,
                        out _,
                        out failure))
                {
                    NearFieldDirectSourceFailureReason = failure;
                    return false;
                }

                if (_foliagePipeline is not null &&
                    sceneData.FoliageClusterCount > 0 &&
                    sceneData.FoliageDrawBufferBytes > 0 &&
                    !_foliagePipeline.NearFieldDirectSourcePipelinesAvailable)
                {
                    NearFieldDirectSourceFailureReason =
                        _foliagePipeline.NearFieldDirectSourcePipelineFailureReason;
                    return false;
                }
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
                ReflectionMode.StaticProbesAndPlanar or
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

            if (UsesSparseHybridLobePayload &&
                (!_sparseHybridLobePayloadAvailable ||
                 _sparseHybridLobePayloadWidth != renderExtent.Width ||
                 _sparseHybridLobePayloadHeight != renderExtent.Height))
            {
                HybridReflectionReceiverFailureReason =
                    "hybrid-reflection-sparse-lobe-buffer-unavailable";
                return false;
            }

            bool supportedReflectionDebug = sceneData.ReflectionDebugView is
                ReflectionDebugView.None or ReflectionDebugView.SsrMask or
                ReflectionDebugView.DdgiDirectionalRadianceLobe or
                ReflectionDebugView.Confidence or
                ReflectionDebugView.SourceSelection or
                ReflectionDebugView.DetailBudget or
                ReflectionDebugView.ReceiverMaterial or
                ReflectionDebugView.RoughnessInputs;
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

        private bool TryResolveTraceResolutionExecutionExtent(
            ForwardNearFieldDirectSourceAttachmentBinding binding,
            out SimpleDdgiNearFieldResidualExecutionExtent executionExtent,
            out string failure)
        {
            executionExtent = default;
            RenderTarget? traceDepth = binding.TraceRasterDepth;
            if (traceDepth is null)
            {
                failure = "near-field-trace-raster-depth-unavailable";
                return false;
            }

            try
            {
                if (_nearFieldDirectSourceExecutionExtent is not null)
                {
                    executionExtent = _nearFieldDirectSourceExecutionExtent();
                }
                else
                {
                    SimpleDdgiNearFieldTraceSourceScaledExtent contractExtent =
                        binding.Configuration.TraceSourceContract.Extent;
                    executionExtent = new SimpleDdgiNearFieldResidualExecutionExtent(
                        contractExtent.ScaledWidth,
                        contractExtent.ScaledHeight,
                        ResolveTraceResolutionScale(
                            contractExtent.ResolutionScale),
                        1u);
                }
            }
            catch (Exception exception)
            {
                failure = "near-field-trace-execution-extent-unavailable:" +
                          exception.GetType().Name;
                return false;
            }

            if (!executionExtent.IsValid ||
                !Enum.IsDefined(executionExtent.Scale))
            {
                failure = "near-field-trace-execution-extent-invalid";
                return false;
            }

            SimpleDdgiNearFieldTraceSourceScaledExtent sourceExtent =
                binding.Configuration.TraceSourceContract.Extent;
            float scale = TraceResolutionScaleFactor(executionExtent.Scale);
            int expectedWidth = Math.Max(1, checked((int)Math.Ceiling(
                sourceExtent.FullWidth * scale)));
            int expectedHeight = Math.Max(1, checked((int)Math.Ceiling(
                sourceExtent.FullHeight * scale)));
            if (executionExtent.Width != expectedWidth ||
                executionExtent.Height != expectedHeight)
            {
                failure = "near-field-trace-execution-extent-scale-mismatch";
                return false;
            }

            if (scale > sourceExtent.ResolutionScale ||
                executionExtent.Width > binding.DirectSource.Extent.Width ||
                executionExtent.Height > binding.DirectSource.Extent.Height ||
                binding.ReceiverPayload.Extent.Width !=
                    binding.DirectSource.Extent.Width ||
                binding.ReceiverPayload.Extent.Height !=
                    binding.DirectSource.Extent.Height ||
                traceDepth.Extent.Width != binding.DirectSource.Extent.Width ||
                traceDepth.Extent.Height != binding.DirectSource.Extent.Height)
            {
                failure = "near-field-trace-execution-extent-exceeds-generation";
                return false;
            }

            failure = "valid";
            return true;
        }

        private static SimpleDdgiNearFieldResidualExecutionScale
            ResolveTraceResolutionScale(float scale) => scale >= 0.5f
                ? SimpleDdgiNearFieldResidualExecutionScale.Half
                : scale >= 0.25f
                    ? SimpleDdgiNearFieldResidualExecutionScale.Quarter
                    : SimpleDdgiNearFieldResidualExecutionScale.Eighth;

        private static float TraceResolutionScaleFactor(
            SimpleDdgiNearFieldResidualExecutionScale scale) => scale switch
            {
                SimpleDdgiNearFieldResidualExecutionScale.Half => 0.5f,
                SimpleDdgiNearFieldResidualExecutionScale.Quarter => 0.25f,
                SimpleDdgiNearFieldResidualExecutionScale.Eighth => 0.125f,
                _ => throw new ArgumentOutOfRangeException(nameof(scale))
            };

        private void RecordTraceResolutionNearFieldSource(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            ForwardNearFieldDirectSourceAttachmentBinding binding)
        {
            if (!TryResolveTraceResolutionExecutionExtent(
                    binding,
                    out SimpleDdgiNearFieldResidualExecutionExtent execution,
                    out string failure))
            {
                throw new InvalidOperationException(failure);
            }

            RenderTarget traceDepth = binding.TraceRasterDepth ??
                throw new InvalidOperationException(
                    "The admitted trace-resolution source has no depth target.");
            var traceExtent = new Extent2D(
                checked((uint)execution.Width),
                checked((uint)execution.Height));

            binding.DirectSource.TransitionToColorAttachment(cmd);
            binding.ReceiverPayload.TransitionToColorAttachment(cmd);
            traceDepth.TransitionToDepthAttachment(cmd);

            var colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = ColorAttachment(
                binding.DirectSource.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            colorAttachments[1] = ColorAttachment(
                binding.ReceiverPayload.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            RenderingAttachmentInfo depthAttachment = DepthAttachment(
                traceDepth.View,
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
                    Extent = traceExtent
                },
                LayerCount = 1,
                ColorAttachmentCount = 2u,
                PColorAttachments = colorAttachments,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            SetFullViewportAndScissor(cmd, traceExtent);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            _recordingTraceResolutionNearFieldSource = true;
            _traceResolutionNearFieldSourceScale = execution.Scale;
            try
            {
                DrawTraceResolutionOpaqueBuckets(cmd, sceneData);
                if (!DrawFoliageForward(
                        cmd,
                        sceneData,
                        nearFieldDirectSource: true))
                {
                    throw new InvalidOperationException(
                        "The trace-resolution foliage source pipelines became unavailable during recording.");
                }
            }
            finally
            {
                _recordingTraceResolutionNearFieldSource = false;
                _context.KhrDynamicRendering.CmdEndRendering(cmd);
            }

            binding.DirectSource.TransitionToShaderRead(cmd);
            binding.ReceiverPayload.TransitionToShaderRead(cmd);
            traceDepth.TransitionToDepthReadOnly(cmd);
            NearFieldDirectSourceFailureReason =
                "valid; trace-resolution raster source recorded";
        }

        private void DrawTraceResolutionOpaqueBuckets(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData)
        {
            if (sceneData.SceneSubmissionForwardPath ==
                SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedIndirect)
            {
                if (sceneData.ForwardVisibilityCompactionActive)
                {
                    DrawForwardVisibilityBucketsIndirect(
                        cmd,
                        sceneData,
                        nearFieldDirectSourceEnabled: true);
                }
                else
                {
                    DrawCompactedForwardBucketsIndirect(
                        cmd,
                        sceneData,
                        nearFieldDirectSourceEnabled: true);
                }
                return;
            }

            if (sceneData.SceneSubmissionForwardPath ==
                SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect)
            {
                DrawCompactedForwardBucketsDirect(
                    cmd,
                    sceneData,
                    nearFieldDirectSourceEnabled: true);
                return;
            }

            ForwardOpaqueVariantSelection variants =
                ResolveOpaqueVariantSelection(sceneData);
            DrawForwardBucket(
                cmd,
                sceneData,
                variants.UseSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.Simple
                    : ForwardOpaquePipelineFamily.Full,
                sceneData.SimpleOpaqueMeshletCount,
                BindlessIndex.MeshletDrawBufferBase,
                nearFieldDirectSourceEnabled: true);
            DrawForwardBucket(
                cmd,
                sceneData,
                variants.UseSimpleGlobalIblPipeline
                    ? ForwardOpaquePipelineFamily.SimpleFullInput
                    : ForwardOpaquePipelineFamily.Full,
                sceneData.SimpleNormalOpaqueMeshletCount,
                BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled: true);
            DrawForwardBucket(
                cmd,
                sceneData,
                ForwardOpaquePipelineFamily.Full,
                sceneData.FullOpaqueMeshletCount,
                BindlessIndex.FullOpaqueMeshletDrawBufferBase,
                nearFieldDirectSourceEnabled: true);
        }

        private bool DrawFoliageForward(
            CommandBuffer cmd,
            Data.SceneRenderingData sceneData,
            bool nearFieldDirectSource = false,
            bool combinedAdvancedGi = false)
        {
            if (_foliagePipeline == null || _bufferManager == null ||
                _foliageManager == null ||
                sceneData.FoliageClusterCount <= 0 ||
                sceneData.FoliageDrawBufferBytes == 0)
                return true;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers(
                (int)sceneData.CurrentFrameIndex);
            if (!buffers.IndirectDispatchBuffer.IsValid)
                return true;

            bool receiverFeedback =
                !_recordingTraceResolutionNearFieldSource &&
                (_simpleDdgiFoliageFeedbackRequiredForCurrentView ||
                 _simpleDdgiReflectionFeedbackRequiredForCurrentView);
            VkPipeline foliagePipeline = default;
            VkPipeline authoredFoliagePipeline = default;
            bool pipelinesResolved =
                RecordingAutomaticPlanarCapture
                    ? _foliagePipeline.TryResolveAutomaticPlanarCapturePipeline(
                          authored: false, receiverFeedback, out foliagePipeline) &&
                      _foliagePipeline.TryResolveAutomaticPlanarCapturePipeline(
                          authored: true, receiverFeedback, out authoredFoliagePipeline)
                    : !_recordingTraceResolutionNearFieldSource &&
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
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight,
                    1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)Math.Max(
                    0,
                    buffers.VisibleClusterCapacity)),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = _recordingTraceResolutionNearFieldSource
                    ? GPUFoliageDrawPushConstants.PackTraceResolutionScale(
                        _traceResolutionNearFieldSourceScale)
                    : GPUFoliageDrawPushConstants.PackFlags(
                        ShouldWriteMaterialTransportProvenance(),
                        _simpleDdgiReflectionFeedbackRequiredForCurrentView,
                        _reflectionFeedbackCubemapArrayLayer,
                        reflectionCaptureEnabled:
                            _recordingReflectionCapture),
                DebugView = _recordingTraceResolutionNearFieldSource
                    ? 0u
                    : sceneData.FoliageDebugView,
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

            VkBuffer indirect = _bufferManager.GetBuffer(
                buffers.IndirectDispatchBuffer);
            _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                cmd,
                indirect,
                FoliageManager.ProceduralIndirectDispatchOffset,
                1,
                (uint)Marshal.SizeOf<DrawMeshTasksIndirectCommandEXT>());

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
            if (_foliagePipeline == null || _bufferManager == null || _foliageManager == null ||
                sceneData.FoliageDrawBufferBytes == 0)
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
                CameraPositionTime = new Vector4(sceneData.CameraPosition.X, sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z, sceneData.Time),
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight,
                    1.0f / Math.Max(1u, sceneData.ScreenWidth), 1.0f / Math.Max(1u, sceneData.ScreenHeight)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                ClusterDrawCount = checked((uint)buffers.MeshletDrawCapacity),
                VisibleClusterBufferBaseIndex = (uint)BindlessIndex.FoliageVisibleClusterBufferBase,
                Flags = _recordingTraceResolutionNearFieldSource
                    ? GPUFoliageDrawPushConstants.PackTraceResolutionScale(
                        _traceResolutionNearFieldSourceScale)
                    : GPUFoliageDrawPushConstants.PackFlags(
                        ShouldWriteMaterialTransportProvenance(),
                        _simpleDdgiReflectionFeedbackRequiredForCurrentView,
                        _reflectionFeedbackCubemapArrayLayer,
                        reflectionCaptureEnabled:
                            _recordingReflectionCapture),
                DebugView = _recordingTraceResolutionNearFieldSource
                    ? 0u
                    : sceneData.FoliageDebugView,
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
            DescriptorSetLayoutBinding* bindings =
                stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[1] = bindings[0];
            bindings[1].Binding = 1;
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
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
                DescriptorCount = FramesInFlight * 5
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
                Result result = _giPipelineCacheService != null
                    ? _giPipelineCacheService.CreateComputePipeline(
                        new PipelineArtifactId(
                            $"{Name}:{shaderArtifactName}"),
                        &info,
                        out VkPipeline pipeline)
                    : _context.Api.CreateComputePipelines(
                        _context.Device,
                        _simpleDdgiReceiverCachePipelineCache,
                        1,
                        &info,
                        null,
                        out pipeline);

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
            ulong cacheSurfaceByteSize = checked(
                (ulong)cacheWidth * cacheHeight *
                SimpleDdgiReceiverSurfaceEntryBytes);
            uint gatherWidth = DivideRoundUp(
                extent.Width,
                SimpleDdgiReceiverGatherScale);
            uint gatherHeight = DivideRoundUp(
                extent.Height,
                SimpleDdgiReceiverGatherScale);
            RecreateSimpleDdgiMaskedFeedbackCompactResources(extent);
            ulong gatherByteSize = checked(
                (ulong)gatherWidth * gatherHeight *
                SimpleDdgiReceiverGatherEntryBytes);
            ulong gatherSurfaceByteSize = checked(
                (ulong)gatherWidth * gatherHeight *
                SimpleDdgiReceiverSurfaceEntryBytes);
            bool currentResourcesMatch =
                _simpleDdgiReceiverCacheWidth == cacheWidth &&
                _simpleDdgiReceiverCacheHeight == cacheHeight &&
                _simpleDdgiReceiverCacheBufferBytes == cacheByteSize &&
                _simpleDdgiReceiverCacheSurfaceBufferBytes ==
                    cacheSurfaceByteSize &&
                _simpleDdgiReceiverGatherWidth == gatherWidth &&
                _simpleDdgiReceiverGatherHeight == gatherHeight &&
                _simpleDdgiReceiverGatherBufferBytes == gatherByteSize &&
                _simpleDdgiReceiverGatherSurfaceBufferBytes ==
                    gatherSurfaceByteSize;
            for (int i = 0; i < FramesInFlight; i++)
            {
                currentResourcesMatch &=
                    _simpleDdgiReceiverCacheBuffers[i].IsValid &&
                    _simpleDdgiReceiverGatherBuffers[i].IsValid &&
                    _simpleDdgiReceiverCacheSurfaceBuffers[i].IsValid &&
                    _simpleDdgiReceiverGatherSurfaceBuffers[i].IsValid &&
                    _simpleDdgiReceiverPublicationBuffers[i].IsValid;
            }

            if (currentResourcesMatch)
                return;

            // Adaptive descriptor sets reference the resolved cache banks.
            // Retire their dependent generation before replacing those banks,
            // then republish a complete descriptor generation below.
            if (_adaptiveReceiverInitializationAttempted &&
                !_adaptiveReceiverInitializationFailed)
            {
                CleanupSimpleDdgiReceiverCacheAdaptive(
                    destroyInfrastructure: false);
            }

            var cacheReplacements = new BufferHandle[FramesInFlight];
            var gatherReplacements = new BufferHandle[FramesInFlight];
            var cacheSurfaceReplacements = new BufferHandle[FramesInFlight];
            var gatherSurfaceReplacements = new BufferHandle[FramesInFlight];
            var publicationReplacements = new BufferHandle[FramesInFlight];
            var cacheNativeBuffers = new VkBuffer[FramesInFlight];
            var gatherNativeBuffers = new VkBuffer[FramesInFlight];
            var cacheSurfaceNativeBuffers = new VkBuffer[FramesInFlight];
            var gatherSurfaceNativeBuffers = new VkBuffer[FramesInFlight];
            var publicationNativeBuffers = new VkBuffer[FramesInFlight];
            for (int i = 0; i < FramesInFlight; i++)
            {
                cacheReplacements[i] = BufferHandle.Invalid;
                gatherReplacements[i] = BufferHandle.Invalid;
                cacheSurfaceReplacements[i] = BufferHandle.Invalid;
                gatherSurfaceReplacements[i] = BufferHandle.Invalid;
                publicationReplacements[i] = BufferHandle.Invalid;
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
                        BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.TransferDstBit,
                        requireDeviceAddress: false,
                        MemoryBudgetCategory.GlobalIllumination,
                        $"Simple DDGI Receiver Gather Lattice Frame {i} " +
                        $"({gatherWidth}x{gatherHeight})");
                    cacheSurfaceReplacements[i] =
                        _bufferManager.CreateDeviceBuffer(
                            cacheSurfaceByteSize,
                            BufferUsageFlags.StorageBufferBit,
                            requireDeviceAddress: false,
                            MemoryBudgetCategory.GlobalIllumination,
                            $"Simple DDGI Resolved Receiver Surface Frame {i} " +
                            $"({cacheWidth}x{cacheHeight})");
                    gatherSurfaceReplacements[i] =
                        _bufferManager.CreateDeviceBuffer(
                            gatherSurfaceByteSize,
                            BufferUsageFlags.StorageBufferBit,
                            requireDeviceAddress: false,
                            MemoryBudgetCategory.GlobalIllumination,
                            $"Simple DDGI Receiver Gather Surface Frame {i} " +
                            $"({gatherWidth}x{gatherHeight})");
                    publicationReplacements[i] =
                        _bufferManager.CreateDeviceBuffer(
                            SimpleDdgiReceiverPublicationAbi.ByteCount,
                            BufferUsageFlags.StorageBufferBit |
                            BufferUsageFlags.TransferDstBit,
                            requireDeviceAddress: false,
                            MemoryBudgetCategory.GlobalIllumination,
                            $"Simple DDGI Receiver Publication Frame {i}");
                }

                for (int i = 0; i < FramesInFlight; i++)
                {
                    if (!cacheReplacements[i].IsValid ||
                        !gatherReplacements[i].IsValid ||
                        !cacheSurfaceReplacements[i].IsValid ||
                        !gatherSurfaceReplacements[i].IsValid ||
                        !publicationReplacements[i].IsValid ||
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
                    cacheSurfaceNativeBuffers[i] =
                        _bufferManager.GetBuffer(cacheSurfaceReplacements[i]);
                    gatherSurfaceNativeBuffers[i] =
                        _bufferManager.GetBuffer(gatherSurfaceReplacements[i]);
                    publicationNativeBuffers[i] =
                        _bufferManager.GetBuffer(publicationReplacements[i]);
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
                    if (cacheSurfaceReplacements[i].IsValid)
                        _bufferManager.DestroyBuffer(cacheSurfaceReplacements[i]);
                    if (gatherSurfaceReplacements[i].IsValid)
                        _bufferManager.DestroyBuffer(gatherSurfaceReplacements[i]);
                    if (publicationReplacements[i].IsValid)
                        _bufferManager.DestroyBuffer(publicationReplacements[i]);
                }

                throw;
            }

            // Swapchain recreation waits for the device to become idle before
            // this callback. Resolve and validate every native handle before
            // descriptor publication, then publish all replacements before
            // retiring the old pairs so no frame can observe a descriptor for
            // a destroyed resource. Vulkan descriptor updates have no
            // recoverable result after this preflight boundary.
            DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[5];
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[5];
            for (int i = 0; i < FramesInFlight; i++)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.SimpleDdgiReceiverGatherBufferBase + i,
                    gatherNativeBuffers[i],
                    0,
                    gatherByteSize);
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase + i,
                    gatherSurfaceNativeBuffers[i],
                    0,
                    gatherSurfaceByteSize);

                bufferInfos[0] = new DescriptorBufferInfo
                {
                    Buffer = cacheNativeBuffers[i],
                    Offset = 0,
                    Range = cacheByteSize
                };
                bufferInfos[1] = new DescriptorBufferInfo
                {
                    Buffer = cacheSurfaceNativeBuffers[i],
                    Offset = 0,
                    Range = cacheSurfaceByteSize
                };
                bufferInfos[2] = new DescriptorBufferInfo
                {
                    Buffer = cacheNativeBuffers[i],
                    Offset = 0,
                    Range = cacheByteSize
                };
                bufferInfos[3] = new DescriptorBufferInfo
                {
                    Buffer = cacheSurfaceNativeBuffers[i],
                    Offset = 0,
                    Range = cacheSurfaceByteSize
                };
                bufferInfos[4] = new DescriptorBufferInfo
                {
                    Buffer = publicationNativeBuffers[i],
                    Offset = 0,
                    Range = SimpleDdgiReceiverPublicationAbi.ByteCount
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
                    DstSet = _simpleDdgiReceiverCacheOutputSets[i],
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[1]
                };
                writes[2] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _simpleDdgiReceiverCacheConsumerSets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[2]
                };
                writes[3] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _simpleDdgiReceiverCacheConsumerSets[i],
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[3]
                };
                writes[4] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _simpleDdgiReceiverCacheConsumerSets[i],
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[4]
                };
                _context.Api.UpdateDescriptorSets(
                    _context.Device,
                    5,
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
                BufferHandle oldCacheSurface =
                    _simpleDdgiReceiverCacheSurfaceBuffers[i];
                BufferHandle oldGatherSurface =
                    _simpleDdgiReceiverGatherSurfaceBuffers[i];
                BufferHandle oldPublication =
                    _simpleDdgiReceiverPublicationBuffers[i];
                _simpleDdgiReceiverCacheBuffers[i] = cacheReplacements[i];
                _simpleDdgiReceiverGatherBuffers[i] = gatherReplacements[i];
                _simpleDdgiReceiverCacheSurfaceBuffers[i] =
                    cacheSurfaceReplacements[i];
                _simpleDdgiReceiverGatherSurfaceBuffers[i] =
                    gatherSurfaceReplacements[i];
                _simpleDdgiReceiverPublicationBuffers[i] =
                    publicationReplacements[i];
                if (oldCache.IsValid)
                    _bufferManager.DestroyBuffer(oldCache);
                if (oldGather.IsValid)
                    _bufferManager.DestroyBuffer(oldGather);
                if (oldCacheSurface.IsValid)
                    _bufferManager.DestroyBuffer(oldCacheSurface);
                if (oldGatherSurface.IsValid)
                    _bufferManager.DestroyBuffer(oldGatherSurface);
                if (oldPublication.IsValid)
                    _bufferManager.DestroyBuffer(oldPublication);
            }

            _simpleDdgiReceiverCacheWidth = cacheWidth;
            _simpleDdgiReceiverCacheHeight = cacheHeight;
            _simpleDdgiReceiverCacheBufferBytes = cacheByteSize;
            _simpleDdgiReceiverCacheSurfaceBufferBytes =
                cacheSurfaceByteSize;
            _simpleDdgiReceiverGatherWidth = gatherWidth;
            _simpleDdgiReceiverGatherHeight = gatherHeight;
            _simpleDdgiReceiverGatherBufferBytes = gatherByteSize;
            _simpleDdgiReceiverGatherSurfaceBufferBytes =
                gatherSurfaceByteSize;
            _receiverPublicationTracker.Reset();
            Array.Fill(_receiverPublicationDependentClearPending, true);

            if (_adaptiveReceiverInitializationAttempted &&
                !_adaptiveReceiverInitializationFailed)
            {
                RecreateSimpleDdgiReceiverCacheAdaptiveResources();
            }
        }

        private void DestroySimpleDdgiReceiverPipelineBank(
            SimpleDdgiReceiverPipelineBank bank)
        {
            ArgumentNullException.ThrowIfNull(bank);
            Span<VkPipeline> pipelines =
            [
                bank.CanonicalGather,
                bank.HybridDiffuseVisibilityGather,
                bank.CanonicalResolve,
                bank.ExactFeedbackGather,
                bank.LegacyGather,
                bank.LegacyResolve,
                bank.DiagnosticsResolve,
                bank.AdaptiveClassify,
                bank.AdaptiveGather,
                bank.AdaptiveFeedbackGather,
                bank.AdaptiveMissingFeedbackGather,
                bank.AdaptiveResolve,
                bank.CompactMaskedFeedback
            ];
            foreach (VkPipeline pipeline in pipelines)
            {
                if (pipeline.Handle != 0)
                {
                    _context.Api.DestroyPipeline(
                        _context.Device,
                        pipeline,
                        null);
                }
            }
        }

        private void CleanupSimpleDdgiReceiverCache()
        {
            lock (_simpleDdgiReceiverPipelineBankGate)
            {
                _simpleDdgiReceiverPipelineBankDisposing = true;
                Volatile.Write(
                    ref _simpleDdgiReceiverPipelineBank,
                    null);
            }
            CleanupSimpleDdgiReceiverCacheAdaptive(
                destroyInfrastructure: true);
            CleanupSimpleDdgiMaskedFeedbackCompactResources();
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

                    if (_simpleDdgiReceiverCacheSurfaceBuffers[i].IsValid)
                    {
                        _bufferManager.DestroyBuffer(
                            _simpleDdgiReceiverCacheSurfaceBuffers[i]);
                    }

                    if (_simpleDdgiReceiverGatherSurfaceBuffers[i].IsValid)
                    {
                        _bufferManager.DestroyBuffer(
                            _simpleDdgiReceiverGatherSurfaceBuffers[i]);
                    }

                    if (_simpleDdgiReceiverPublicationBuffers[i].IsValid)
                    {
                        _bufferManager.DestroyBuffer(
                            _simpleDdgiReceiverPublicationBuffers[i]);
                    }
                }

                _simpleDdgiReceiverCacheBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverCacheSurfaceBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverGatherSurfaceBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverPublicationBuffers[i] = BufferHandle.Invalid;
                _simpleDdgiReceiverCacheOutputSets[i] = default;
                _simpleDdgiReceiverCacheConsumerSets[i] = default;
            }

            _simpleDdgiReceiverCacheWidth = 0;
            _simpleDdgiReceiverCacheHeight = 0;
            _simpleDdgiReceiverCacheBufferBytes = 0;
            _simpleDdgiReceiverCacheSurfaceBufferBytes = 0;
            _simpleDdgiReceiverGatherWidth = 0;
            _simpleDdgiReceiverGatherHeight = 0;
            _simpleDdgiReceiverGatherBufferBytes = 0;
            _simpleDdgiReceiverGatherSurfaceBufferBytes = 0;
            _receiverPublicationTracker.Reset();
            _receiverPublicationStamp = 1u;
            _receiverPublicationChangedRegionMask = 0u;
            _receiverPublicationGenerationEnabled = false;
            Array.Clear(_receiverPublicationDependentClearPending);

            if (_simpleDdgiReceiverCacheResolvePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheResolvePipeline,
                    null);
                _simpleDdgiReceiverCacheResolvePipeline = default;
            }

            if (_simpleDdgiReceiverCacheResolveDiagnosticsPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheResolveDiagnosticsPipeline,
                    null);
                _simpleDdgiReceiverCacheResolveDiagnosticsPipeline = default;
            }

            if (_simpleDdgiReceiverCacheResolveLegacyPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheResolveLegacyPipeline,
                    null);
                _simpleDdgiReceiverCacheResolveLegacyPipeline = default;
            }

            if (_simpleDdgiReceiverCacheLegacyPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheLegacyPipeline,
                    null);
                _simpleDdgiReceiverCacheLegacyPipeline = default;
            }

            if (_simpleDdgiReceiverFeedbackPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverFeedbackPipeline,
                    null);
                _simpleDdgiReceiverFeedbackPipeline = default;
            }

            if (_simpleDdgiMaskedFeedbackCompactPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiMaskedFeedbackCompactPipeline,
                    null);
                _simpleDdgiMaskedFeedbackCompactPipeline = default;
            }

            if (_simpleDdgiReceiverCachePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCachePipeline,
                    null);
                _simpleDdgiReceiverCachePipeline = default;
            }

            if (_simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline.Handle !=
                0)
            {
                _context.Api.DestroyPipeline(
                    _context.Device,
                    _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline,
                    null);
                _simpleDdgiReceiverCacheHybridDiffuseVisibilityPipeline =
                    default;
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
            RecreateSparseHybridLobePayloadResources(
                _renderTargets.SceneColor.Extent);
            try
            {
                RecreateSimpleDdgiReceiverCacheResources();
            }
            catch (Exception ex)
            {
                _simpleDdgiReceiverCacheAvailableForCurrentView = false;
                SetSimpleDdgiReceiverCacheFallback(
                    SimpleDdgiReceiverCacheFallbackReason.ResourceUnavailable,
                    $"receiver-cache resize failed: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine(
                    $"Simple-DDGI receiver cache resize failed; exact gather retained: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private GPUForwardPushConstants CreateOpaqueComputePushConstants(SceneRenderingData sceneData) => new()
        {
            ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
            InverseViewMatrix = sceneData.InverseViewMatrix,
            InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
            CameraPosition = sceneData.CameraPosition,
            Time = sceneData.Time,
            ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
            CurrentFrameIndex = sceneData.CurrentFrameIndex,
            PackedLightDispatch = GPUForwardPushConstants.PackLightDispatch(sceneData.LightCount,
                sceneData.LocalLightCount, sceneData.DirectionalLightIndex0, sceneData.DirectionalLightIndex1),
            LocalLightCount = (uint)sceneData.LocalLightCount,
            HiZMipCount = sceneData.HiZMipCount,
            OcclusionBias = sceneData.OcclusionBias,
            DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(
                sceneData.DebugViewMode, sceneData.AmbientOcclusionEnabled, (uint)sceneData.AmbientOcclusionDebugView,
                transparentReceiveShadows: true, transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                ambientOcclusionForwardSamplingMode: (uint)sceneData.AmbientOcclusionForwardSamplingMode,
                globalIlluminationEnabled: ShouldApplyGlobalIllumination(sceneData),
                screenSpaceGlobalIlluminationEnabled: false,
                ambientOcclusionBentNormalMode: (uint)sceneData.AmbientOcclusionBentNormalMode),
            DiagnosticFlags = GPUForwardPushConstants.PackDiagnosticFlags(
                ShouldCollectDdgiForwardEstimateCounters(sceneData), ShouldCollectDdgiClipmapCoverageCounters(sceneData),
                ShouldCollectDirectionalShadowReceiverCounters(sceneData), (uint)sceneData.DirectionalShadowPreviewCascade,
                geometricSpecularAntialiasingEnabled: sceneData.SpecularAntialiasingMode == SpecularAntialiasingMode.GeometricVariance)
        };

        public override void Cleanup()
        {
            _opaqueCompute?.Dispose();
            _opaqueCompute = null;
            CleanupSimpleDdgiReceiverCache();
            CleanupSparseHybridLobePayloadResources();
        }
    }
}
