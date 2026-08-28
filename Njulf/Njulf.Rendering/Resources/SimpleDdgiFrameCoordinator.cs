using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Preserves the ordered core Simple-DDGI frame transaction while delegating
/// invalidation, emissive transport, feedback, evidence, and C3 execution to
/// their existing state owners.
/// </summary>
internal sealed class SimpleDdgiFrameCoordinator
{
    private readonly VulkanContext _context;
    private readonly StagingRing _stagingRing;
    private readonly AdvancedGiAdmissionCoordinator _admission;
    private readonly DdgiSceneInvalidationCoordinator _invalidation;
    private readonly DdgiEmissiveTransportCoordinator _emissiveTransport;
    private readonly SimpleDdgiReceiverFeedbackCoordinator _receiverFeedback;
    private readonly SimpleDdgiFrameEvidenceCoordinator _frameEvidence;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly FarFieldClipmapManager _farField;
    private readonly AdvancedGiTransientBufferArena _transientArena;
    private readonly SimpleDdgiGuidingSourceCacheSidecar _guidingSourceCache;
    private readonly SimpleDdgiGuidingFrameCoordinator _guidingFrames;
    private readonly SimpleDdgiRefinementFocusTracker _refinementFocus = new();

    private SimpleDdgiGuidingFrameConfiguration _guidingConfiguration =
        SimpleDdgiGuidingFrameConfiguration.Disabled;

    private SimpleDdgiGuidingFrameConfiguration _appliedArenaConfiguration =
        SimpleDdgiGuidingFrameConfiguration.Disabled;

    private string _guidingConfigurationReason =
        "directional-guiding-disabled";

    private bool _lastReflectionProbeGiReady;

    public SimpleDdgiGuidingFrameConfiguration GuidingFrameConfiguration =>
        _guidingConfiguration;

    public string GuidingConfigurationReason =>
        _guidingConfigurationReason;

    public SimpleDdgiFrameCoordinator(
        VulkanContext context,
        StagingRing stagingRing,
        AdvancedGiAdmissionCoordinator admission,
        DdgiSceneInvalidationCoordinator invalidation,
        DdgiEmissiveTransportCoordinator emissiveTransport,
        SimpleDdgiReceiverFeedbackCoordinator receiverFeedback,
        SimpleDdgiFrameEvidenceCoordinator frameEvidence,
        SimpleDdgiVolumeManager volumeManager,
        FarFieldClipmapManager farField,
        AdvancedGiTransientBufferArena transientArena,
        SimpleDdgiGuidingSourceCacheSidecar guidingSourceCache,
        SimpleDdgiGuidingFrameCoordinator guidingFrames)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stagingRing = stagingRing ??
                       throw new ArgumentNullException(nameof(stagingRing));
        _admission = admission ??
                     throw new ArgumentNullException(nameof(admission));
        _invalidation = invalidation ??
                        throw new ArgumentNullException(nameof(invalidation));
        _emissiveTransport = emissiveTransport ??
                             throw new ArgumentNullException(
                                 nameof(emissiveTransport));
        _receiverFeedback = receiverFeedback ??
                            throw new ArgumentNullException(
                                nameof(receiverFeedback));
        _frameEvidence = frameEvidence ??
                         throw new ArgumentNullException(nameof(frameEvidence));
        _volumeManager = volumeManager ??
                         throw new ArgumentNullException(nameof(volumeManager));
        _farField = farField ?? throw new ArgumentNullException(nameof(farField));
        _transientArena = transientArena ??
                          throw new ArgumentNullException(nameof(transientArena));
        _guidingSourceCache = guidingSourceCache ??
                              throw new ArgumentNullException(
                                  nameof(guidingSourceCache));
        _guidingFrames = guidingFrames ??
                         throw new ArgumentNullException(nameof(guidingFrames));
    }

    public SimpleDdgiCoreFrameResult PrepareFrame(
        in SimpleDdgiCoreFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Scene.Scene);
        if (request.CommandBuffer.Handle == 0)
        {
            throw new ArgumentException(
                "A valid command buffer is required.",
                nameof(request));
        }

        RenderSettings settings = request.Settings.Settings ??
                                  throw new ArgumentNullException(nameof(request));
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        bool simpleDdgiActive =
            gi.EffectiveUseDdgi;
        bool rayUpdateActive = simpleDdgiActive &&
                               gi.EffectiveUseRayQueryBackend &&
                               request.Capabilities.RayQuerySupported &&
                               request.Capabilities.AccelerationStructureActive;

        if (!simpleDdgiActive)
            return PrepareDisabledFrame(request);
        if (gi.EffectiveUseRayQueryBackend &&
            request.Capabilities.RayQuerySupported &&
            !request.Capabilities.AccelerationStructureActive)
        {
            return PreparePendingRaySceneFrame(request);
        }

        DdgiInvalidationFrame invalidationFrame =
            _invalidation.CollectFrame(
                new DdgiInvalidationCollectionRequest(
                    request.Scene.Scene,
                    request.Scene.Lights,
                    gi,
                    request.Scene.Foliage,
                    gi.SimpleDdgiMutationJournalEnabled));

        DdgiEmissiveTransportSnapshot emissive =
            _emissiveTransport.PrepareFrame(
                new DdgiEmissiveFrameRequest(
                    request.Scene.Scene,
                    gi,
                    request.Identity.SceneContentRevision,
                    request.Scene.GpuParticleDeltaSeconds,
                    rayUpdateActive),
                _stagingRing,
                request.CommandBuffer);

        _farField.Upload(
            request.Scene.Scene,
            request.View.CameraPosition,
            _stagingRing,
            request.CommandBuffer,
            request.Identity.SceneContentRevision);
        SimpleDdgiFarFieldFrameSnapshot farField = CaptureFarFieldSnapshot(gi);

        bool structuredGatherAvailable = rayUpdateActive &&
                                         gi.SimpleDdgiStructuredGatherEnabled;
        DdgiInvalidationIdentityFrame invalidationIdentity =
            _invalidation.ResolveFrameIdentity(
                new DdgiInvalidationIdentityRequest(
                    request.Scene.Scene,
                    request.Scene.Lights,
                    gi,
                    settings.Environment,
                    request.Capabilities.EnvironmentGiLightingSignature,
                    request.Capabilities.EnvironmentUsesAnalyticSky,
                    request.Identity.SceneContentRevision,
                    request.Identity.GiTransportMaterialRevision,
                    new DdgiEmissiveInvalidationFacts(
                        emissive.Content.SourceRevision,
                        emissive.Content.SourceSignature,
                        emissive.Content.SourceCount,
                        emissive.Diagnostics.TriangleStats.CandidateCount,
                        emissive.Diagnostics.SkippedSkinnedObjectCount,
                        emissive.Diagnostics.ExcludedCandidateCount,
                        emissive.Diagnostics.Vfx.SourceCount),
                    request.Capabilities.ShaderBundleHash),
                request.Scene.AtmosphereOwnedLights.Span);

        _volumeManager.SetSchedulerCostEstimate(_frameEvidence.CostEstimate);

        _guidingConfiguration = CompileGuidingConfiguration(
            simpleDdgiActive,
            gi);
        ReconcileAdvancedGiArena(
            simpleDdgiActive,
            request.View.ReceiverFeedbackViewport,
            request.Admission,
            settings,
            request.CommandBuffer);
        SynchronizePublishedDirectionalGuidingSourceCache();

        Vector3 receiverForward = request.View.CameraForward.Normalized();
        if (receiverForward == Vector3.Zero)
            receiverForward = new Vector3(0f, 0f, -1f);
        float nearRingSpacing = SimpleDdgiVolumeManager.ResolveRingSpacing(
            gi,
            0);
        float visibleReceiverFocusDistance = Math.Max(
            1.5f,
            nearRingSpacing * 2f);
        Vector3 visibleReceiverFallbackFocus =
            request.View.CameraPosition +
            receiverForward * visibleReceiverFocusDistance;
        Vector3? measuredReceiverFocus = null;
        uint viewportGeneration =
            request.View.ReceiverFeedbackViewport.Generation;
        if (viewportGeneration != 0u &&
            _receiverFeedback.TryGetPublishedRefinementWitness(
                viewportGeneration,
                _volumeManager.VolumeTableGeneration,
                request.Identity.FrameSerial,
                out SimpleDdgiReceiverFeedbackRefinementWitness witness) &&
            _volumeManager.TryResolveBaseVolumeVirtualProbeWorldPosition(
                witness.ResolvedVirtualProbeId,
                out Vector3 resolvedMeasuredReceiverFocus))
        {
            measuredReceiverFocus = resolvedMeasuredReceiverFocus;
        }

        Vector3 visibleReceiverFocus = _refinementFocus.Resolve(
            visibleReceiverFallbackFocus,
            request.View.CameraPosition,
            receiverForward,
            Math.Max(0.5f, nearRingSpacing * 0.75f),
            request.Identity.CameraCutSerial,
            request.Identity.SceneContentRevision,
            measuredReceiverFocus);
        SimpleDdgiReceiverFeedbackGpuSchedulingBinding feedbackBinding =
            request.View.ReceiverFeedbackViewport.Available
                ? _receiverFeedback.AcquireSchedulingBinding(
                    new SimpleDdgiReceiverFeedbackSchedulingRequest(
                        request.CommandBuffer,
                        viewportGeneration,
                        request.Identity.FrameSerial))
                : SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                    "receiver-feedback-runtime-or-render-targets-unavailable");
        bool feedbackSummaryReadRecorded = feedbackBinding.UseFeedback;
        _volumeManager.SetReceiverFeedbackSchedulingBinding(
            feedbackBinding,
            request.Identity.FrameSerial);

        SimpleDdgiDirtySignature dirty =
            invalidationIdentity.DirtySignature;
        _volumeManager.Upload(
            request.Scene.Scene,
            request.View.CameraPosition,
            _stagingRing,
            request.CommandBuffer,
            request.Identity.FrameSlotIndex,
            dirty.Signature,
            dirty.ReasonFlags,
            structuredGatherAvailable,
            farField.CoverageReady,
            invalidationFrame.DirtyRegions,
            dirty.CohortTransition,
            request.Scene.Scene.GlobalIlluminationProbeVolumes,
            request.Identity.SceneContentRevision,
            dirty.SourceRefreshMode,
            dirty.SourceRelightScale,
            invalidationIdentity.WarmStartIdentity,
            emissive.RefinementDemands,
            visibleReceiverFocus,
            request.View.CameraForward);

        _guidingConfiguration = CompileGuidingConfiguration(
            simpleDdgiActive,
            gi);
        if (SimpleDdgiReceiverFeedbackCoordinator
            .ShouldReconcileAfterUpload(feedbackSummaryReadRecorded))
        {
            ReconcileAdvancedGiArena(
                simpleDdgiActive,
                request.View.ReceiverFeedbackViewport,
                request.Admission,
                settings,
                request.CommandBuffer);
        }

        PrepareGuidingFrame(request);
        bool fullPageManagement =
            _volumeManager.PrepareProbePageManagement(
                request.View.CameraPosition,
                request.View.ViewProjection,
                request.Identity.SceneContentRevision,
                request.Identity.CameraCutSerial);
        SimpleDdgiReflectionRecaptureIntent reflectionIntent =
            ResolveReflectionRecaptureIntent(
                simpleDdgiActive,
                request.Capabilities.ReflectionConsumersAvailable);

        return new SimpleDdgiCoreFrameResult(
            Active: true,
            RayUpdateActive: rayUpdateActive,
            invalidationFrame.Telemetry,
            emissive,
            farField,
            fullPageManagement,
            _volumeManager.LastUploadMicroseconds,
            _volumeManager.LastUploadTiming,
            _guidingConfiguration,
            _guidingConfigurationReason,
            reflectionIntent);
    }

    private SimpleDdgiCoreFrameResult PrepareDisabledFrame(
        in SimpleDdgiCoreFrameRequest request)
    {
        RenderSettings settings = request.Settings.Settings ??
                                  throw new ArgumentNullException(nameof(request));
        _invalidation.ResetDynamicTracking();
        _frameEvidence.ResetDisabled();
        _volumeManager.SetReceiverFeedbackSchedulingBinding(
            SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                "receiver-feedback-simple-ddgi-disabled"),
            request.Identity.FrameSerial);
        _volumeManager.SetPublishedDirectionalGuidingSourceCache(0u, 0);
        _volumeManager.EnsureDisabled(_stagingRing, request.CommandBuffer);
        _emissiveTransport.ResetSceneTracking();
        DdgiEmissiveTransportSnapshot emissive =
            _emissiveTransport.PrepareFrame(
                new DdgiEmissiveFrameRequest(
                    request.Scene.Scene,
                    settings.GlobalIllumination,
                    request.Identity.SceneContentRevision,
                    request.Scene.GpuParticleDeltaSeconds,
                    RayUpdateActive: false),
                _stagingRing,
                request.CommandBuffer);
        _guidingConfiguration =
            SimpleDdgiGuidingFrameConfiguration.Disabled;
        _guidingConfigurationReason =
            "directional-guiding-simple-ddgi-disabled";
        ReconcileAdvancedGiArena(
            simpleDdgiActive: false,
            request.View.ReceiverFeedbackViewport,
            request.Admission,
            settings,
            request.CommandBuffer);

        return new SimpleDdgiCoreFrameResult(
            Active: false,
            RayUpdateActive: false,
            default,
            emissive,
            default,
            FullPageManagementRequired: false,
            SimpleDdgiUploadMicroseconds: 0,
            SimpleDdgiUploadTiming: default,
            _guidingConfiguration,
            _guidingConfigurationReason,
            ResolveReflectionRecaptureIntent(
                simpleDdgiActive: false,
                request.Capabilities.ReflectionConsumersAvailable));
    }

    private SimpleDdgiCoreFrameResult PreparePendingRaySceneFrame(
        in SimpleDdgiCoreFrameRequest request)
    {
        // Static BLAS construction is deliberately progressive. Until its
        // complete TLAS transaction can be published, raster rendering uses
        // IBL and the renderer keeps the previous DDGI allocation dormant.
        // Reconfiguring or clearing it here would add descriptor waits to every
        // warm-up frame and could expose a partially represented ray scene.
        _frameEvidence.ResetDisabled();
        _volumeManager.SetReceiverFeedbackSchedulingBinding(
            SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                "receiver-feedback-ray-scene-pending"),
            request.Identity.FrameSerial);
        _guidingConfiguration =
            SimpleDdgiGuidingFrameConfiguration.Disabled;
        _guidingConfigurationReason =
            "directional-guiding-ray-scene-pending";

        return new SimpleDdgiCoreFrameResult(
            Active: false,
            RayUpdateActive: false,
            default,
            default,
            default,
            FullPageManagementRequired: false,
            SimpleDdgiUploadMicroseconds: 0,
            SimpleDdgiUploadTiming: default,
            _guidingConfiguration,
            _guidingConfigurationReason,
            ResolveReflectionRecaptureIntent(
                simpleDdgiActive: false,
                request.Capabilities.ReflectionConsumersAvailable));
    }

    private SimpleDdgiGuidingFrameConfiguration
        CompileGuidingConfiguration(
            bool simpleDdgiActive,
            GlobalIlluminationSettings gi)
    {
        MemoryHeapBudgetSnapshot heapBudget =
            _context.GetMemoryHeapBudgetSnapshot();
        ulong memoryHeadroom = ResolveMemoryHeadroom(heapBudget);
        SimpleDdgiGuidingFrameConfiguration configuration =
            SimpleDdgiGuidingConfigurationPlanner.Compile(
                new SimpleDdgiGuidingConfigurationRequest(
                    simpleDdgiActive,
                    _admission.GraphModes.UsesDirectionalGuiding,
                    gi,
                    _admission.RuntimeContentState,
                    _admission.EvaluatePrerequisite(
                        AdvancedGiPrerequisiteFeature.DirectionalGuiding),
                    _volumeManager.PhysicalProbeCapacity,
                    _volumeManager.RaysPerProbe,
                    _volumeManager
                        .GuidingTraceDirectionScratchProbeCapacity,
                    _volumeManager.StoragePackingMode,
                    _appliedArenaConfiguration,
                    memoryHeadroom,
                    _context.MinimumStorageBufferOffsetAlignment,
                    _context.MaximumStorageBufferRange),
                out _guidingConfigurationReason);
        return configuration;
    }

    private void ReconcileAdvancedGiArena(
        bool simpleDdgiActive,
        in SimpleDdgiReceiverFeedbackViewport viewport,
        in SimpleDdgiFrameAdmissionInput admission,
        RenderSettings settings,
        CommandBuffer commandBuffer)
    {
        if (!viewport.Available)
            return;

        Extent2D extent = viewport.Extent;
        ulong screenTileCount = extent.Width == 0u || extent.Height == 0u
            ? 0UL
            : checked(
                (((ulong)extent.Width +
                     ForwardPlusPass.SimpleDdgiReceiverGatherScale - 1UL) /
                 ForwardPlusPass.SimpleDdgiReceiverGatherScale) *
                (((ulong)extent.Height +
                     ForwardPlusPass.SimpleDdgiReceiverGatherScale - 1UL) /
                 ForwardPlusPass.SimpleDdgiReceiverGatherScale));
        int physicalProbeCapacity = _volumeManager.PhysicalProbeCapacity;
        uint pageGeneration = Math.Max(
            1u,
            _volumeManager.ProbeResidencyResourceGeneration);
        AdvancedGiPrerequisiteGateResult gate =
            _admission.EvaluatePrerequisite(
                AdvancedGiPrerequisiteFeature.ReceiverFeedback);
        AdvancedGiQualificationGateResult qualification =
            EvaluateReceiverFeedbackQualification(
                gate,
                physicalProbeCapacity > 0,
                admission.ReceiverFeedbackQualificationContext,
                settings.GlobalIllumination
                    .SimpleDdgiReceiverFeedbackQualificationId);
        MemoryHeapBudgetSnapshot heapBudget =
            _context.GetMemoryHeapBudgetSnapshot();
        ulong memoryHeadroom = ResolveMemoryHeadroom(heapBudget);
        ulong maximumStorageBufferRange =
            _context.MaximumStorageBufferRange;
        SimpleDdgiReceiverFeedbackProductionWorkload producerWorkload =
            SimpleDdgiReceiverFeedbackCoordinator
                .CompileProductionWorkload(
                    settings,
                    extent,
                    screenTileCount);

        SimpleDdgiReceiverFeedbackDesiredState desired =
            _receiverFeedback.CompileDesiredState(
                new SimpleDdgiReceiverFeedbackRequest(
                    simpleDdgiActive,
                    settings.GlobalIllumination
                        .SimpleDdgiReceiverFeedbackMode,
                    physicalProbeCapacity,
                    producerWorkload,
                    pageGeneration,
                    maximumStorageBufferRange,
                    memoryHeadroom,
                    gate,
                    qualification,
                    settings.GlobalIllumination
                        .SimpleDdgiReceiverFeedbackQualificationId,
                    _admission.RuntimeContentState.Matched,
                    _admission.RuntimeContentState.Reason));
        if (!desired.ConfigurationChanged &&
            _appliedArenaConfiguration.Equals(_guidingConfiguration))
        {
            return;
        }

        SimpleDdgiGuidingFrameConfiguration guidingArenaConfiguration =
            _guidingConfiguration;
        if (_appliedArenaConfiguration.IsEnabled &&
            !_guidingFrames.TryConfigure(
                SimpleDdgiGuidingFrameConfiguration.Disabled,
                commandBuffer,
                out _))
        {
            return;
        }

        bool arenaReady = TryReconcileAdvancedGiScratchArena(
            desired.Plan,
            guidingArenaConfiguration,
            maximumStorageBufferRange,
            memoryHeadroom,
            out string arenaFailure);
        if (!arenaReady && guidingArenaConfiguration.IsEnabled)
        {
            _guidingConfigurationReason =
                string.IsNullOrWhiteSpace(arenaFailure)
                    ? "directional-guiding-transient-arena-rejected"
                    : arenaFailure;
            guidingArenaConfiguration =
                SimpleDdgiGuidingFrameConfiguration.Disabled;
            arenaReady = TryReconcileAdvancedGiScratchArena(
                desired.Plan,
                guidingArenaConfiguration,
                maximumStorageBufferRange,
                memoryHeadroom,
                out arenaFailure);
        }

        _ = _receiverFeedback.TryApplyConfiguration(
            desired,
            arenaReady,
            arenaFailure,
            out _);
        _appliedArenaConfiguration = guidingArenaConfiguration;
    }

    private AdvancedGiQualificationGateResult
        EvaluateReceiverFeedbackQualification(
            in AdvancedGiPrerequisiteGateResult prerequisite,
            bool supported,
            in AdvancedGiRuntimeQualificationContext baseContext,
            string? configuredQualificationId)
    {
        if (!prerequisite.Passed)
        {
            return AdvancedGiQualificationGateResult.Reject(
                prerequisite.FailureDetail);
        }

        AdvancedGiRuntimeQualificationContext context = baseContext with
        {
            FeatureSupported = supported
        };
        return _admission.EvaluateQualification(
            AdvancedGiPrerequisiteFeature.ReceiverFeedback,
            context,
            prerequisite.QualificationId,
            configuredQualificationId);
    }

    private bool TryReconcileAdvancedGiScratchArena(
        in SimpleDdgiReceiverFeedbackPlan plan,
        in SimpleDdgiGuidingFrameConfiguration guidingConfiguration,
        ulong maximumStorageBufferRange,
        ulong memoryHeadroom,
        out string failure)
    {
        ulong alignment = _context.MinimumStorageBufferOffsetAlignment;
        Span<GiExperimentScratchAllocation> requests =
            stackalloc GiExperimentScratchAllocation[2];
        int requestCount = 0;
        if (plan.UsesExactCompacted)
        {
            requests[requestCount++] = new GiExperimentScratchAllocation(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                plan.Layout.SortScratchBytes,
                new GiExperimentScratchInterval(100, 106),
                alignment);
        }

        if (guidingConfiguration.IsEnabled)
        {
            SimpleDdgiGuidingLayout layout =
                guidingConfiguration.RuntimeRequest.Layout;
            requests[requestCount++] = new GiExperimentScratchAllocation(
                SimpleDdgiAdvancedMemoryCategory
                    .DirectionalGuidingBuildScratch,
                layout.TransientWorkspace.TotalBytes,
                new GiExperimentScratchInterval(10, 40),
                layout.StorageAlignmentBytes);
        }

        if (!GiExperimentScratchAliasing.TryCompileArenaPlan(
                requests[..requestCount],
                out GiExperimentScratchArenaPlan arenaPlan,
                out failure))
        {
            return false;
        }

        return _transientArena.TryReconcile(
            arenaPlan,
            maximumStorageBufferRange,
            memoryHeadroom,
            out failure);
    }

    private void SynchronizePublishedDirectionalGuidingSourceCache()
    {
        SimpleDdgiGuidingSourceCacheSnapshot snapshot =
            _guidingSourceCache.Snapshot;
        SimpleDdgiGuidingSourceCacheLayout layout = snapshot.Layout;
        if (!snapshot.IsReady || !snapshot.PayloadDescriptorPublished ||
            !layout.IsAdmitted ||
            layout.AdmittedGuidedPhysicalProbeCapacity <= 0 ||
            layout.DirectionSlotsPerProbe <= 0)
        {
            _volumeManager.SetPublishedDirectionalGuidingSourceCache(0u, 0);
            return;
        }

        _volumeManager.SetPublishedDirectionalGuidingSourceCache(
            checked((uint)layout.AdmittedGuidedPhysicalProbeCapacity),
            layout.DirectionSlotsPerProbe);
    }

    private void PrepareGuidingFrame(
        in SimpleDdgiCoreFrameRequest request)
    {
        if (!_guidingConfiguration.IsEnabled ||
            !_guidingConfiguration.Equals(_appliedArenaConfiguration))
        {
            return;
        }

        if (!_guidingFrames.TryConfigure(
                _guidingConfiguration,
                request.CommandBuffer,
                out string configureReason))
        {
            _guidingConfigurationReason = configureReason;
            return;
        }

        if (!_guidingFrames.TryPrepareFrame(
                request.Identity.FrameSlotIndex,
                Math.Max(1UL, request.Identity.FrameSerial),
                request.CommandBuffer,
                out string prepareReason))
        {
            _guidingConfigurationReason = prepareReason;
        }
    }

    private SimpleDdgiFarFieldFrameSnapshot CaptureFarFieldSnapshot(
        GlobalIlluminationSettings gi) =>
        new(
            _farField.CoverageReady,
            _farField.PagedMode,
            _farField.PagePoolCapacity,
            _farField.ResidentPageCount,
            _farField.PendingPageCount,
            _farField.PageRequestCount,
            _farField.PageMissCount,
            _farField.PageRebuildCount,
            _farField.PageEvictionsThisFrame,
            _farField.ScheduledPageBakeCount,
            _farField.PageCacheBytes,
            gi.FarFieldMemoryBudgetBytes,
            _farField.InstanceBufferBytes,
            _farField.PageTableBufferBytes,
            _farField.LastUploadMicroseconds);

    private SimpleDdgiReflectionRecaptureIntent
        ResolveReflectionRecaptureIntent(
            bool simpleDdgiActive,
            bool reflectionConsumersAvailable)
    {
        if (!reflectionConsumersAvailable)
        {
            _lastReflectionProbeGiReady = false;
            return SimpleDdgiReflectionRecaptureIntent.None;
        }

        SimpleDdgiAtmosphereCohortFeedback simpleCohort =
            simpleDdgiActive && _volumeManager.TransportV2Active
                ? _volumeManager.CreateAtmosphereCohortFeedbackSnapshot()
                : default;
        uint livePropagationSourceGeneration =
            _volumeManager.LivePropagationSourceGeneration;
        bool simpleCohortReady = simpleDdgiActive &&
                                 _volumeManager.TransportV2Active &&
                                 simpleCohort.ParticipatingProbeCount > 0 &&
                                 livePropagationSourceGeneration != 0u &&
                                 livePropagationSourceGeneration ==
                                 simpleCohort.SourceCohortGeneration &&
                                 simpleCohort
                                     .VisiblePublicationBoundaryComplete &&
                                 simpleCohort
                                     .MinimumPropagationBoundaryComplete &&
                                 simpleCohort
                                     .PublishedPropagationGeneration != 0u &&
                                 simpleCohort.PropagationGeneration ==
                                 simpleCohort
                                     .PublishedPropagationGeneration;
        bool giReady = simpleDdgiActive &&
                       (simpleCohortReady ||
                        (!_volumeManager.TransportV2Active &&
                         _volumeManager.ProbeCount > 0 &&
                         !_volumeManager.AtlasFresh &&
                         _volumeManager.FramesSinceLastClear > 0));
        bool requestRecapture =
            giReady && !_lastReflectionProbeGiReady;
        _lastReflectionProbeGiReady = giReady;
        return new SimpleDdgiReflectionRecaptureIntent(
            UpdateTelemetry: true,
            RequestRecaptureAll: requestRecapture,
            Reason: "ddgi-ready");
    }

    private static ulong ResolveMemoryHeadroom(
        in MemoryHeapBudgetSnapshot snapshot) =>
        snapshot.IsAvailable &&
        snapshot.PrimaryBudgetBytes > snapshot.PrimaryUsageBytes
            ? snapshot.PrimaryBudgetBytes - snapshot.PrimaryUsageBytes
            : 0UL;
}

internal readonly record struct SimpleDdgiCoreFrameRequest(
    SimpleDdgiFrameSceneInput Scene,
    SimpleDdgiFrameSettingsInput Settings,
    SimpleDdgiFrameViewInput View,
    SimpleDdgiFrameIdentity Identity,
    SimpleDdgiFrameCapabilities Capabilities,
    SimpleDdgiFrameAdmissionInput Admission,
    CommandBuffer CommandBuffer);

internal readonly record struct SimpleDdgiFrameSettingsInput(
    RenderSettings Settings);

internal readonly record struct SimpleDdgiFrameSceneInput(
    Scene Scene,
    LightFrameSnapshot Lights,
    DdgiFoliageProxyFrame Foliage,
    float GpuParticleDeltaSeconds,
    ReadOnlyMemory<bool> AtmosphereOwnedLights);

internal readonly record struct SimpleDdgiFrameViewInput(
    Vector3 CameraPosition,
    Vector3 CameraForward,
    Matrix4x4 ViewProjection,
    SimpleDdgiReceiverFeedbackViewport ReceiverFeedbackViewport);

internal readonly record struct SimpleDdgiReceiverFeedbackViewport(
    bool Available,
    Extent2D Extent,
    uint Generation)
{
    public static SimpleDdgiReceiverFeedbackViewport Unavailable =>
        new(false, default, 0u);
}

internal readonly record struct SimpleDdgiFrameIdentity(
    int FrameSlotIndex,
    ulong FrameSerial,
    ulong SceneContentRevision,
    uint GiTransportMaterialRevision,
    ulong CameraCutSerial);

internal readonly record struct SimpleDdgiFrameCapabilities(
    bool RayQuerySupported,
    bool AccelerationStructureActive,
    ulong EnvironmentGiLightingSignature,
    bool EnvironmentUsesAnalyticSky,
    string ShaderBundleHash,
    bool ReflectionConsumersAvailable);

internal readonly record struct SimpleDdgiFrameAdmissionInput(
    AdvancedGiRuntimeQualificationContext
        ReceiverFeedbackQualificationContext);

internal readonly record struct SimpleDdgiCoreFrameResult(
    bool Active,
    bool RayUpdateActive,
    DdgiInvalidationTelemetry InvalidationTelemetry,
    DdgiEmissiveTransportSnapshot Emissive,
    SimpleDdgiFarFieldFrameSnapshot FarField,
    bool FullPageManagementRequired,
    long SimpleDdgiUploadMicroseconds,
    SimpleDdgiUploadTiming SimpleDdgiUploadTiming,
    SimpleDdgiGuidingFrameConfiguration GuidingConfiguration,
    string GuidingConfigurationReason,
    SimpleDdgiReflectionRecaptureIntent ReflectionIntent);

internal readonly record struct SimpleDdgiFarFieldFrameSnapshot(
    bool CoverageReady,
    bool PagedMode,
    int PagePoolCapacity,
    int ResidentPageCount,
    int PendingPageCount,
    int PageRequestCount,
    int PageMissCount,
    int PageRebuildCount,
    int PageEvictionsThisFrame,
    int ScheduledPageBakeCount,
    ulong PageCacheBytes,
    ulong MemoryBudgetBytes,
    ulong InstanceBufferBytes,
    ulong PageTableBufferBytes,
    long UploadMicroseconds);

internal readonly record struct SimpleDdgiReflectionRecaptureIntent(
    bool UpdateTelemetry,
    bool RequestRecaptureAll,
    string Reason)
{
    public static SimpleDdgiReflectionRecaptureIntent None =>
        new(false, false, string.Empty);
}
