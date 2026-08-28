using System;
using System.Collections.Generic;
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
        _volumeManager.SetDynamicGeometryEpoch(
            request.Capabilities.RaySceneContentEpoch);

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
        SimpleDdgiReceiverFeedbackRefinementWitness receiverWitness = default;
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
            receiverWitness = witness;
        }

        Vector3 visibleReceiverFocus = _refinementFocus.Resolve(
            visibleReceiverFallbackFocus,
            request.View.CameraPosition,
            receiverForward,
            Math.Max(0.5f, nearRingSpacing * 0.75f),
            request.Identity.CameraCutSerial,
            request.Identity.SceneContentRevision,
            measuredReceiverFocus);
        SimpleDdgiAutomaticRefinementMetrics automaticMetrics =
            ResolveAutomaticRefinementMetrics(
                receiverWitness.IsValid
                    ? receiverWitness.EstimatedContributionMass
                    : 0.0f,
                invalidationFrame.DirtyRegions,
                visibleReceiverFocus,
                nearRingSpacing,
                gi.SimpleDdgiArchitecturalThicknessMeters,
                invalidationIdentity.DirtySignature.ReasonFlags,
                invalidationIdentity.DirtySignature.SourceRelightScale,
                _volumeManager.TransportTailSummary);
        SimpleDdgiRefinementDemand? automaticRefinementDemand =
            SimpleDdgiAutomaticRefinementDemandBuilder.TryBuild(
                visibleReceiverFocus,
                automaticMetrics,
                receiverWitness.IsValid
                    ? receiverWitness.ResolvedVirtualProbeId + 1UL
                    : 0UL,
                out SimpleDdgiRefinementDemand resolvedAutomaticDemand)
                ? resolvedAutomaticDemand
                : null;
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
            request.View.CameraForward,
            automaticRefinementDemand);

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

    internal static SimpleDdgiAutomaticRefinementMetrics
        ResolveAutomaticRefinementMetrics(
            float receiverDensity,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            Vector3 focus,
            float nearRingSpacing,
            float architecturalThickness,
            uint dirtyReasonFlags,
            Vector3 sourceRelightScale,
            in SimpleDdgiTransportTailSummary tail)
    {
        receiverDensity = float.IsFinite(receiverDensity)
            ? Math.Max(receiverDensity, 0.0f)
            : 0.0f;
        float spacing = float.IsFinite(nearRingSpacing)
            ? Math.Max(nearRingSpacing, 0.001f)
            : 1.0f;
        float thickness = float.IsFinite(architecturalThickness)
            ? Math.Max(architecturalThickness, 0.001f)
            : 0.08f;
        float geometricComplexity = 0.0f;
        float lightingVariance =
            (dirtyReasonFlags &
                (DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonLight |
                 DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonEmissive)) !=
                0u
                ? 0.85f
                : 0.0f;
        if (dirtyRegions != null)
        {
            float reach = Math.Max(spacing * 6.0f, thickness * 8.0f);
            float reachSquared = reach * reach;
            for (int index = 0; index < dirtyRegions.Count; index++)
            {
                DdgiDirtyRegion region = dirtyRegions[index];
                BoundingBox bounds = region.InfluenceBounds;
                if (!Finite(bounds.Min) || !Finite(bounds.Max) ||
                    DistanceSquaredToBounds(focus, bounds) > reachSquared)
                {
                    continue;
                }

                Vector3 extent = new(
                    Math.Max(bounds.Max.X - bounds.Min.X, 0.0f),
                    Math.Max(bounds.Max.Y - bounds.Min.Y, 0.0f),
                    Math.Max(bounds.Max.Z - bounds.Min.Z, 0.0f));
                float minimumExtent = Math.Min(
                    extent.X,
                    Math.Min(extent.Y, extent.Z));
                float maximumExtent = Math.Max(
                    extent.X,
                    Math.Max(extent.Y, extent.Z));
                float thinness = 1.0f - Math.Clamp(
                    minimumExtent / Math.Max(thickness * 2.0f, 0.001f),
                    0.0f,
                    1.0f);
                float spatialFrequency = Math.Clamp(
                    maximumExtent / Math.Max(spacing * 4.0f, 0.001f),
                    0.0f,
                    1.0f);
                float localComplexity = Math.Max(
                    thinness * 0.90f,
                    spatialFrequency * 0.50f);
                if (region.Reason is
                    DdgiDirtyReason.GeometryAdded or
                    DdgiDirtyReason.GeometryRemoved or
                    DdgiDirtyReason.TransformChanged or
                    DdgiDirtyReason.StreamIn or
                    DdgiDirtyReason.StreamOut or
                    DdgiDirtyReason.Teleport)
                {
                    localComplexity = Math.Max(localComplexity, 0.75f);
                }
                if (region.Reason is
                    DdgiDirtyReason.MaterialChanged or
                    DdgiDirtyReason.EmissiveChanged or
                    DdgiDirtyReason.LocalLightChanged or
                    DdgiDirtyReason.DirectionalLightChanged)
                {
                    lightingVariance = Math.Max(lightingVariance, 0.85f);
                }
                geometricComplexity = Math.Max(
                    geometricComplexity,
                    localComplexity);
            }
        }

        if (Finite(sourceRelightScale))
        {
            float minimum = Math.Min(
                sourceRelightScale.X,
                Math.Min(sourceRelightScale.Y, sourceRelightScale.Z));
            float maximum = Math.Max(
                sourceRelightScale.X,
                Math.Max(sourceRelightScale.Y, sourceRelightScale.Z));
            float deviation = Math.Max(
                Math.Abs(maximum - minimum),
                Math.Max(
                    Math.Abs(maximum - 1.0f),
                    Math.Abs(minimum - 1.0f)));
            if (float.IsFinite(deviation) && deviation > 0.0f)
            {
                lightingVariance = Math.Max(
                    lightingVariance,
                    deviation / (deviation + 0.25f));
            }
        }

        float observedError = 0.0f;
        if (float.IsFinite(tail.Tolerance) && tail.Tolerance > 0.0f)
        {
            float error = float.IsFinite(tail.RelativeTailBound) &&
                    tail.RelativeTailBound >= 0.0f
                ? tail.RelativeTailBound
                : float.IsFinite(tail.FixedPointDefect) &&
                    tail.FixedPointDefect >= 0.0f
                    ? tail.FixedPointDefect
                    : 0.0f;
            observedError = Math.Max(error / tail.Tolerance, 0.0f);
        }

        return new SimpleDdgiAutomaticRefinementMetrics(
            receiverDensity,
            Math.Clamp(geometricComplexity, 0.0f, 1.0f),
            Math.Clamp(lightingVariance, 0.0f, 1.0f),
            observedError);
    }

    private static float DistanceSquaredToBounds(
        Vector3 point,
        in BoundingBox bounds)
    {
        float dx = point.X < bounds.Min.X
            ? bounds.Min.X - point.X
            : point.X > bounds.Max.X
                ? point.X - bounds.Max.X
                : 0.0f;
        float dy = point.Y < bounds.Min.Y
            ? bounds.Min.Y - point.Y
            : point.Y > bounds.Max.Y
                ? point.Y - bounds.Max.Y
                : 0.0f;
        float dz = point.Z < bounds.Min.Z
            ? bounds.Min.Z - point.Z
            : point.Z > bounds.Max.Z
                ? point.Z - bounds.Max.Z
                : 0.0f;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

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
    bool ReflectionConsumersAvailable,
    ulong RaySceneContentEpoch);

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
