using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Strict runtime evidence for the production GI optimization set. A feature
/// is not active merely because its setting was requested: support, effective
/// resource admission, GPU execution, and a downstream consumption boundary
/// must all be observed while every feature is requested simultaneously.
/// </summary>
public readonly record struct GiAllOnFeatureRuntimeEvidence(
    bool Requested,
    bool Supported,
    bool Effective,
    bool Executed,
    bool Consumed,
    string Detail)
{
    public bool Passed => Requested && Supported && Effective && Executed &&
        Consumed;

    public GiAllOnFeatureRuntimeEvidence Merge(
        in GiAllOnFeatureRuntimeEvidence observation) => new(
        Requested || observation.Requested,
        Supported || observation.Supported,
        Effective || observation.Effective,
        Executed || observation.Executed,
        Consumed || observation.Consumed,
        observation.Passed || string.IsNullOrWhiteSpace(Detail)
            ? observation.Detail
            : Detail);
}

public sealed record GiAllOnRuntimeQualificationSnapshot
{
    public const uint SchemaRevision = 1u;

    public uint Schema { get; init; } = SchemaRevision;
    public ulong FirstFrameSerial { get; init; }
    public ulong LastFrameSerial { get; init; }
    public int ObservedAllOnFrameCount { get; init; }
    public int RejectedNonAllOnFrameCount { get; init; }
    public string LastRequestMismatchDetail { get; init; } = string.Empty;
    public bool SimultaneouslyEffectiveFrameObserved { get; init; }
    public GiAllOnFeatureRuntimeEvidence ReceiverCache { get; init; }
    public GiAllOnFeatureRuntimeEvidence AcceleratedTransportSolver { get; init; }
    public GiAllOnFeatureRuntimeEvidence OpacityMicromaps { get; init; }
    public GiAllOnFeatureRuntimeEvidence DirectionalGuiding { get; init; }
    public GiAllOnFeatureRuntimeEvidence TaggedCaustics { get; init; }
    public bool CurrentTailCertificateObserved { get; init; }
    public bool FatalRuntimeFailureObserved { get; init; }
    public string FatalRuntimeFailureDetail { get; init; } = string.Empty;

    public bool Passed =>
        Schema == SchemaRevision &&
        FirstFrameSerial != 0UL &&
        LastFrameSerial >= FirstFrameSerial &&
        ObservedAllOnFrameCount >= 3 &&
        SimultaneouslyEffectiveFrameObserved &&
        ReceiverCache.Passed &&
        AcceleratedTransportSolver.Passed &&
        OpacityMicromaps.Passed &&
        DirectionalGuiding.Passed &&
        TaggedCaustics.Passed &&
        CurrentTailCertificateObserved &&
        !FatalRuntimeFailureObserved;
}

/// <summary>
/// Session accumulator for a single uninterrupted all-on scene run. Frames
/// where any feature is not requested are deliberately ignored so isolated
/// or sequential feature captures cannot manufacture an all-on pass.
/// </summary>
public sealed class GiAllOnRuntimeQualificationAccumulator
{
    private GiAllOnRuntimeQualificationSnapshot _snapshot = new();
    private GiCausticTimedStage _observedCausticStages;

    public GiAllOnRuntimeQualificationSnapshot Snapshot => _snapshot;

    public bool Observe(RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        GiRoadmapExperimentDiagnostics roadmap = diagnostics.GiRoadmapExperiments;
        GiRoadmapExperimentModeDiagnostics modes = roadmap.Modes;
        SimpleDdgiReceiverCacheDiagnostics cache =
            diagnostics.SimpleDdgiReceiverCache;
        OpacityMicromapGpuRuntimeSnapshot opacity =
            roadmap.OpacityMicromapRuntime;
        SimpleDdgiDirectionalGuidingDiagnostics guiding =
            roadmap.DirectionalGuidingRuntime;
        GiCausticDiagnostics caustics = roadmap.CausticRuntime;

        bool receiverCacheRequested = cache.RequestedMode ==
            SimpleDdgiReceiverCacheMode.TemporalAdaptive;
        bool acceleratedRequested =
            diagnostics.SimpleDdgiTransportAccelerationEnabled &&
            diagnostics.SimpleDdgiTransportAcceleratedSweepCount == 2;
        bool opacityRequested = modes.OpacityMicromap.RequestedMode is
            DdgiOpacityMicromapMode.ExtFourStateExperiment or
            DdgiOpacityMicromapMode.AutoQualified;
        bool guidingRequested = modes.DirectionalGuiding.RequestedMode is
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment or
            SimpleDdgiDirectionalGuidingMode.AutoQualified;
        bool causticRequested = modes.Caustic.RequestedMode is
            GiCausticMode.WorldCacheExperiment or GiCausticMode.AutoQualified;

        if (!receiverCacheRequested || !acceleratedRequested ||
            !opacityRequested || !guidingRequested || !causticRequested)
        {
            _snapshot = _snapshot with
            {
                RejectedNonAllOnFrameCount = checked(
                    _snapshot.RejectedNonAllOnFrameCount + 1),
                LastRequestMismatchDetail = string.Join(
                    ",",
                    new[]
                    {
                        ("receiver-cache", receiverCacheRequested),
                        ("accelerated-solver", acceleratedRequested),
                        ("opacity-micromaps", opacityRequested),
                        ("directional-guiding", guidingRequested),
                        ("tagged-caustics", causticRequested)
                    }.Where(static feature => !feature.Item2)
                     .Select(static feature => feature.Item1 + "-not-requested"))
            };
            return false;
        }

        bool receiverCacheSupported =
            cache.AdaptiveAbiVersion == SimpleDdgiReceiverCacheAdaptiveAbi.Version &&
            cache.AdaptiveResourceGeneration != 0u &&
            cache.AdaptiveResourceBytes != 0UL;
        bool receiverCacheEffective = cache.EffectiveMode ==
            SimpleDdgiReceiverCacheMode.TemporalAdaptive &&
            cache.FallbackReason == SimpleDdgiReceiverCacheFallbackReason.None;
        var receiverCache = new GiAllOnFeatureRuntimeEvidence(
            true,
            receiverCacheSupported,
            receiverCacheEffective,
            diagnostics.ForwardGiReceiverCacheGenerated != 0,
            diagnostics.ForwardGiReceiverCacheConsumed != 0,
            cache.FallbackDetail);

        bool solverSupported =
            diagnostics.SimpleDdgiTransportV2Active != 0 &&
            diagnostics.SimpleDdgiTransportTailCertificationEnabled &&
            diagnostics.SimpleDdgiTransportAccelerationRuntimeAvailable &&
            diagnostics.SimpleDdgiSchedulerMode ==
                SimpleDdgiSchedulerMode.GpuResident;
        var solver = new GiAllOnFeatureRuntimeEvidence(
            true,
            solverSupported,
            solverSupported &&
                diagnostics.SimpleDdgiTransportAcceleratedSweepCount == 2,
            diagnostics.SimpleDdgiTransportAcceleratedDispatchCount > 0 &&
                diagnostics
                    .SimpleDdgiTransportAcceleratedCanonicalPublicationCount > 0,
            diagnostics.SimpleDdgiTransportAcceleratedFinalPublicationCount > 0,
            diagnostics.SimpleDdgiTransportTailCertificationFallbackReason);

        bool opacitySupported =
            modes.OpacityMicromap.SupportedMode != DdgiOpacityMicromapMode.Off &&
            opacity.Supported && opacity.Content.Authoritative &&
            opacity.RegisteredCandidateCount > 0;
        bool opacityEffective =
            modes.OpacityMicromap.EffectiveMode != DdgiOpacityMicromapMode.Off &&
            opacity.Enabled;
        bool opacityExecuted = opacity.BuildCount > 0UL &&
            opacity.PublicationCount > 0UL && opacity.PublishedVariantCount > 0;
        bool rayTraversalExecuted = diagnostics.SimpleDdgiRaysPerFrame > 0UL ||
            diagnostics.SimpleDdgiTransportSourceRayCount > 0UL;
        var opacityMicromaps = new GiAllOnFeatureRuntimeEvidence(
            true,
            opacitySupported,
            opacityEffective,
            opacityExecuted,
            opacityExecuted && rayTraversalExecuted &&
                diagnostics.AccelerationStructureTopLevelInstanceCount > 0,
            opacity.Detail);

        bool guidingSupported =
            modes.DirectionalGuiding.SupportedMode !=
                SimpleDdgiDirectionalGuidingMode.Off;
        bool guidingEffective =
            modes.DirectionalGuiding.EffectiveMode !=
                SimpleDdgiDirectionalGuidingMode.Off &&
            guiding.Runtime.Resource.IsEffectivelyEnabled;
        bool guidingExecuted = guiding.Frame.SampleRecorded &&
            guiding.Frame.TrainRecorded && guiding.Frame.BuildRecorded &&
            guiding.Frame.ValidateRecorded;
        bool guidingConsumed = guiding.HasAuthoritativeSampleReadback &&
            guiding.Frame.CompletedSampleCount > 0 &&
            guiding.Frame.DistributionPublicationSucceeded &&
            guiding.Runtime.Resource.HasReadableDistribution &&
            rayTraversalExecuted;
        var directionalGuiding = new GiAllOnFeatureRuntimeEvidence(
            true,
            guidingSupported,
            guidingEffective,
            guidingExecuted,
            guidingConsumed,
            guiding.Reason);

        bool causticSupported =
            modes.Caustic.SupportedMode != GiCausticMode.Off;
        bool causticEffective =
            modes.Caustic.EffectiveMode != GiCausticMode.Off &&
            caustics.Runtime.Resource.IsEffectivelyEnabled;
        // C4 is intentionally transactional: task/trace/cache-build execute
        // while a cache generation is being produced, and resolve/composite
        // consume that generation only after its publication fence completes.
        // Consequently no production frame is required to expose every stage
        // in one timing snapshot. Accumulate only accepted all-on frames so
        // settings-only or sequential isolated captures still cannot qualify.
        _observedCausticStages |= caustics.Timings.AvailableStages &
            GiCausticTimedStage.All;
        bool causticExecuted =
            (_observedCausticStages & GiCausticTimedStage.All) ==
                GiCausticTimedStage.All;
        bool causticConsumed = caustics.HasAuthoritativePublication &&
            caustics.Publication.RetainedPhotonCount > 0u &&
            caustics.Publication.OccupiedCellCount > 0u &&
            diagnostics.GiCausticReceiverPayloadCompleted != 0 &&
            diagnostics.GiCausticReceiverPayloadFrameSerial != 0UL &&
            (caustics.Timings.AvailableStages &
                GiCausticTimedStage.Composite) != 0;
        var taggedCaustics = new GiAllOnFeatureRuntimeEvidence(
            true,
            causticSupported,
            causticEffective,
            causticExecuted,
            causticConsumed,
            caustics.Reason);

        bool fatalFailure = cache.AdaptiveOverflowFlags != 0u ||
            diagnostics.ValidationWarningMessageCount != 0 ||
            diagnostics.ValidationErrorMessageCount != 0 ||
            opacity.QueryFailureCount != 0UL ||
            guiding.State == SimpleDdgiGuidingTelemetryState.Faulted ||
            caustics.State == GiCausticTelemetryState.Faulted ||
            caustics.Publication.OverflowCount != 0u ||
            diagnostics.SimpleDdgiTransportConvergence
                .TailAuditReadbackTimeoutCount != 0UL ||
            diagnostics.SimpleDdgiTransportConvergence
                .TailSourceNoProgressRecoveryCount != 0UL ||
            diagnostics.SimpleDdgiTransportConvergence
                .TailConvergenceDeadlineRecoveryCount != 0UL;
        string fatalDetail = fatalFailure
            ? ResolveFatalDetail(diagnostics, cache, opacity, guiding, caustics)
            : string.Empty;
        ulong frameSerial = Math.Max(
            Math.Max(
                diagnostics.DdgiLastUpdatedFrameSerial,
                diagnostics.SimpleDdgiSchedulerFeedbackFrameSerial),
            diagnostics.GiCausticReceiverPayloadFrameSerial);

        _snapshot = _snapshot with
        {
            FirstFrameSerial = _snapshot.FirstFrameSerial == 0UL
                ? frameSerial
                : Math.Min(_snapshot.FirstFrameSerial, frameSerial),
            LastFrameSerial = Math.Max(_snapshot.LastFrameSerial, frameSerial),
            ObservedAllOnFrameCount = checked(
                _snapshot.ObservedAllOnFrameCount + 1),
            SimultaneouslyEffectiveFrameObserved =
                _snapshot.SimultaneouslyEffectiveFrameObserved ||
                (receiverCacheSupported && receiverCacheEffective &&
                 solverSupported &&
                 opacitySupported && opacityEffective &&
                 guidingSupported && guidingEffective &&
                 causticSupported && causticEffective),
            ReceiverCache = _snapshot.ReceiverCache.Merge(receiverCache),
            AcceleratedTransportSolver =
                _snapshot.AcceleratedTransportSolver.Merge(solver),
            OpacityMicromaps =
                _snapshot.OpacityMicromaps.Merge(opacityMicromaps),
            DirectionalGuiding =
                _snapshot.DirectionalGuiding.Merge(directionalGuiding),
            TaggedCaustics =
                _snapshot.TaggedCaustics.Merge(taggedCaustics),
            CurrentTailCertificateObserved =
                _snapshot.CurrentTailCertificateObserved ||
                diagnostics.SimpleDdgiTransportConvergence
                    .TailCertificateCurrent,
            FatalRuntimeFailureObserved =
                _snapshot.FatalRuntimeFailureObserved || fatalFailure,
            FatalRuntimeFailureDetail = fatalFailure
                ? fatalDetail
                : _snapshot.FatalRuntimeFailureDetail
        };
        return true;
    }

    private static string ResolveFatalDetail(
        RendererDiagnostics diagnostics,
        in SimpleDdgiReceiverCacheDiagnostics cache,
        in OpacityMicromapGpuRuntimeSnapshot opacity,
        SimpleDdgiDirectionalGuidingDiagnostics guiding,
        GiCausticDiagnostics caustics)
    {
        if (diagnostics.ValidationWarningMessageCount != 0 ||
            diagnostics.ValidationErrorMessageCount != 0)
        {
            return $"vulkan-validation-warning-count=" +
                $"{diagnostics.ValidationWarningMessageCount};" +
                $"error-count={diagnostics.ValidationErrorMessageCount}";
        }
        if (cache.AdaptiveOverflowFlags != 0u)
            return "receiver-cache-adaptive-overflow";
        if (opacity.QueryFailureCount != 0UL)
            return "opacity-micromap-query-failure";
        if (guiding.State == SimpleDdgiGuidingTelemetryState.Faulted)
            return guiding.Reason;
        if (caustics.State == GiCausticTelemetryState.Faulted ||
            caustics.Publication.OverflowCount != 0u)
        {
            return caustics.Reason;
        }
        if (diagnostics.SimpleDdgiTransportConvergence
                .TailAuditReadbackTimeoutCount != 0UL)
            return "accelerated-tail-audit-readback-timeout";
        if (diagnostics.SimpleDdgiTransportConvergence
                .TailSourceNoProgressRecoveryCount != 0UL)
            return "accelerated-tail-source-no-progress";
        return "accelerated-tail-convergence-deadline";
    }
}
