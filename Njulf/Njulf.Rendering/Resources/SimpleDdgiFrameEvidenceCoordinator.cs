using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns submitted-frame evidence, delayed cost training, and DDGI liveness
/// state across the graphics submission/fence boundary.
/// </summary>
internal sealed class SimpleDdgiFrameEvidenceCoordinator
{
    private readonly SimpleDdgiSchedulerCostModel _costModel = new();
    private readonly SimpleDdgiSubmittedFrameRing _submittedFrames = new();
    private readonly SimpleDdgiLivenessWatchdog _livenessWatchdog;
    private readonly Action<SimpleDdgiSchedulerCostSample>? _trainingObserver;
    private SimpleDdgiSubmittedFrameEvidence _pending;
    private SimpleDdgiCompletedFrameEvidence _completed;
    private SimpleDdgiSourceCacheWorkloadObservation _sourceCacheObservation;
    private SimpleDdgiLivenessSnapshot _liveness =
        SimpleDdgiLivenessSnapshot.Empty;
    private bool _feedbackRejectionBaselineInitialized;
    private ulong _lastSchedulerFeedbackGenerationRejectionCount;
    private ulong _lastResidencyFeedbackGenerationRejectionCount;

    public SimpleDdgiSchedulerCostEstimate CostEstimate =>
        _costModel.Estimate;

    public SimpleDdgiFrameEvidenceCoordinator(int framesInFlight)
        : this(framesInFlight, trainingObserver: null)
    {
    }

    internal SimpleDdgiFrameEvidenceCoordinator(
        int framesInFlight,
        Action<SimpleDdgiSchedulerCostSample>? trainingObserver)
    {
        _livenessWatchdog = new SimpleDdgiLivenessWatchdog(
            framesInFlight,
            schedulerFeedbackLatencyFrames: framesInFlight,
            residencyFeedbackLatencyFrames: framesInFlight,
            publicationReadbackLatencyFrames: framesInFlight);
        _trainingObserver = trainingObserver;
    }

    public void CapturePending(
        int frameIndex,
        in SimpleDdgiSubmittedWorkload workload)
    {
        SimpleDdgiSubmittedFrameEvidence evidence = workload.Evidence;
        if (evidence.Valid && evidence.FrameSlot != frameIndex)
        {
            throw new ArgumentException(
                $"DDGI evidence names slot {evidence.FrameSlot}, but slot " +
                $"{frameIndex} is being captured.",
                nameof(workload));
        }

        _pending = evidence;
    }

    public void CommitSuccessfulSubmission(int frameIndex)
    {
        if (_pending.Valid)
            _submittedFrames.MarkSubmitted(frameIndex, _pending);
        _pending = default;
    }

    public void AbortPendingSubmission() => _pending = default;

    public SimpleDdgiCompletedFrameEvidence CompleteAfterFence(
        int frameIndex,
        in SimpleDdgiFenceCompletedEvidence completed)
    {
        _completed = default;
        _sourceCacheObservation = default;

        // Consume first. Any observer/training failure after this point cannot
        // make a retry train the same submitted slot twice.
        if (!_submittedFrames.TryConsume(
                frameIndex,
                out SimpleDdgiSubmittedFrameEvidence submitted) ||
            !submitted.Valid)
        {
            return default;
        }

        ulong farFieldSteps =
            (ulong)completed.InvestigationCounters
                .FarFieldStepBucket0Count * 2UL +
            (ulong)completed.InvestigationCounters
                .FarFieldStepBucket1Count * 6UL +
            (ulong)completed.InvestigationCounters
                .FarFieldStepBucket2Count * 12UL +
            (ulong)completed.InvestigationCounters
                .FarFieldStepBucket3Count * 24UL +
            (ulong)completed.InvestigationCounters
                .FarFieldStepBucket4Count * 48UL;
        ulong materialEvaluations =
            (ulong)completed.MaterialCounters
                .EstimatedDetailedTransportHitCount +
            completed.MaterialCounters.EstimatedCompactTransportHitCount +
            completed.MaterialCounters
                .EstimatedCorrectnessFallbackHitCount +
            completed.MaterialCounters.EstimatedFarFieldTransportHitCount;
        var sample = new SimpleDdgiSchedulerCostSample(
            submitted.ScheduledPrimaryRayCount,
            submitted.VisibilityRayCount,
            completed.MaterialCounters.EstimatedAlphaCandidateTestCount,
            materialEvaluations,
            farFieldSteps);
        _trainingObserver?.Invoke(sample);
        _costModel.Observe(sample);

        ulong completedHits = 0UL;
        ulong completedMisses = 0UL;
        ulong rejectedBackFaces = 0UL;
        if (completed.ForwardEstimateCounters.ReadbackValid != 0)
        {
            completedHits = completed.ForwardEstimateCounters
                .TraceEnergyHitCount;
            completedMisses = completed.ForwardEstimateCounters
                .TraceEnergyMissCount;
            rejectedBackFaces = completed.ForwardEstimateCounters
                .TraceOneSidedBackFaceHitCount;
        }
        if (completedHits == 0UL && completedMisses == 0UL &&
            completed.InvestigationCounters.ReadbackValid != 0)
        {
            completedHits = completed.InvestigationCounters
                .SimpleTraceHitCount;
            completedMisses = completed.InvestigationCounters
                .SimpleTraceMissCount;
            rejectedBackFaces = 0UL;
        }

        ulong shadeableHits = completedHits > rejectedBackFaces
            ? completedHits - rejectedBackFaces
            : 0UL;
        _sourceCacheObservation =
            new SimpleDdgiSourceCacheWorkloadObservation(
                Valid: true,
                ShadeableHitCount: shadeableHits,
                MissCount: completedMisses,
                RejectedBackFaceCount: rejectedBackFaces,
                SourceCacheLayoutIdentity:
                    submitted.SourceCacheLayoutIdentity,
                FrameSerial: submitted.FrameSerial);

        _completed = SimpleDdgiFrameEvidenceFactory.Complete(
            submitted,
            completed.Timings,
            completed.SchedulerFeedbackAvailable,
            completed.SchedulerFeedback,
            completed.SchedulerFeedbackTransportTopologyGeneration,
            completed.SchedulerActiveCanonicalMutationCount,
            completed.SchedulerActiveSourceMutationCount);
        return _completed;
    }

    public SimpleDdgiLivenessSnapshot EvaluateLiveness(
        in SimpleDdgiLivenessRequest request)
    {
        if (!request.Active)
        {
            ResetLiveness();
            return _liveness;
        }

        bool schedulerFeedbackValid =
            !request.GpuSchedulerAuthoritative ||
            request.SchedulerFeedbackValid;
        bool residencyFeedbackValid =
            !request.SparseResidencyAuthoritative ||
            request.ResidencyFeedbackValid;
        ulong schedulerFeedbackFrameSerial =
            request.GpuSchedulerAuthoritative
                ? request.SchedulerFeedbackFrameSerial
                : request.FrameSerial;
        ulong residencyFeedbackFrameSerial =
            request.SparseResidencyAuthoritative
                ? request.ResidencyFeedbackFrameSerial
                : schedulerFeedbackFrameSerial;

        bool feedbackGenerationsCompatible = schedulerFeedbackValid &&
            residencyFeedbackValid;
        bool generationTransition = request.SourceCohortTransitionActive;

        if (request.GpuSchedulerAuthoritative && schedulerFeedbackValid)
        {
            bool schedulerResourceMatches =
                request.SchedulerFeedback.SchedulerResourceGeneration ==
                request.SchedulerResourceGeneration;
            bool schedulerTopologyMatches =
                request.SchedulerFeedbackTransportTopologyGeneration ==
                request.TransportTopologyGeneration;
            bool schedulerTemporalGenerationKnown =
                schedulerTopologyMatches &&
                request.SchedulerFeedback.SourceLightingGeneration ==
                request.SourceLightingGeneration &&
                IsCurrentOrNextGeneration(
                    request.SchedulerFeedback.TransportGeneration,
                    request.TransportGeneration);
            bool schedulerGenerationCurrent = schedulerResourceMatches &&
                schedulerTopologyMatches &&
                request.SchedulerFeedback.SourceLightingGeneration ==
                request.SourceLightingGeneration &&
                request.SchedulerFeedback.TransportGeneration ==
                request.TransportGeneration;

            feedbackGenerationsCompatible &= schedulerResourceMatches;
            generationTransition |= !schedulerTemporalGenerationKnown ||
                !schedulerGenerationCurrent ||
                !request.SchedulerFeedbackCoversCurrentVolumeTable;
        }

        if (request.SparseResidencyAuthoritative && residencyFeedbackValid)
        {
            bool residencyResourceMatches =
                request.ResidencyFeedback.ResidencyResourceGeneration ==
                request.ResidencyResourceGeneration;
            feedbackGenerationsCompatible &= residencyResourceMatches;

            if (request.GpuSchedulerAuthoritative &&
                schedulerFeedbackValid)
            {
                bool feedbackFramesMatch =
                    residencyFeedbackFrameSerial ==
                    schedulerFeedbackFrameSerial;
                bool sourceGenerationsMatch =
                    request.ResidencyFeedback.EventSourceGeneration ==
                    request.SchedulerFeedback.SourceLightingGeneration &&
                    request.ResidencyFeedback.EventCohortGeneration ==
                    request.SchedulerFeedback.SourceLightingGeneration;
                feedbackGenerationsCompatible &= feedbackFramesMatch &&
                    sourceGenerationsMatch;
            }
            else
            {
                feedbackGenerationsCompatible = false;
            }

            bool residencyGenerationCurrent =
                residencyResourceMatches &&
                request.ResidencyFeedback.EventSourceGeneration ==
                request.SourceLightingGeneration &&
                request.ResidencyFeedback.EventCohortGeneration ==
                request.SourceLightingGeneration;
            generationTransition |= !residencyGenerationCurrent;
        }

        SimpleDdgiLivenessBlockReason feedbackRejectionReason =
            ObserveFeedbackGenerationRejections(
                request.GpuSchedulerAuthoritative,
                request.SparseResidencyAuthoritative,
                request.SchedulerFeedbackGenerationRejectionCount,
                request.ResidencyFeedbackGenerationRejectionCount);

        SimpleDdgiSchedulerClassCounts eligibleByClass =
            SimpleDdgiSchedulerClassCounts.Empty;
        SimpleDdgiRingCounts eligibleByRing = SimpleDdgiRingCounts.Empty;
        SimpleDdgiEligibilityRejectionCounts eligibilityRejections =
            SimpleDdgiEligibilityRejectionCounts.Empty;
        uint eligibleProbeCount;
        uint selectedRequestCount;
        uint indirectDispatchRequestCount;
        uint committedUpdateCount;
        uint blendedUpdateCount;
        uint coherentPublicationCount;
        uint effectiveRequestBudget;
        uint effectiveRayBudget;
        int globalConvergencePending;
        int localConvergencePending;

        if (request.GpuSchedulerAuthoritative && schedulerFeedbackValid)
        {
            eligibleByClass = request.SchedulerEligibility.ByClass;
            eligibleByRing = request.SchedulerEligibility.ByRing;
            eligibleProbeCount = Sum(eligibleByClass);
            uint eligibleByRingTotal = Sum(eligibleByRing);

            if (eligibleProbeCount !=
                    request.SchedulerFeedback.EligibleCount ||
                eligibleByRingTotal !=
                    request.SchedulerFeedback.EligibleCount)
            {
                schedulerFeedbackValid = false;
                feedbackGenerationsCompatible = false;
                eligibleProbeCount = 0u;
                eligibleByClass = SimpleDdgiSchedulerClassCounts.Empty;
                eligibleByRing = SimpleDdgiRingCounts.Empty;
            }

            selectedRequestCount =
                request.SchedulerFeedback.AcceptedCount;
            indirectDispatchRequestCount =
                request.SchedulerFeedback.DispatchedLaneCount != 0u
                    ? request.SchedulerFeedback.AcceptedCount
                    : 0u;
            committedUpdateCount =
                request.SchedulerFeedback.CommittedCount;
            blendedUpdateCount =
                request.SchedulerFeedback.PublishedCount;
            coherentPublicationCount =
                request.SchedulerFeedback.PublishedCount;
            effectiveRequestBudget =
                request.SchedulerFeedback.RequestBudget;
            effectiveRayBudget =
                request.SchedulerFeedback.PrimaryRayBudget;
            eligibilityRejections =
                new SimpleDdgiEligibilityRejectionCounts(
                    request.SchedulerFeedback.RejectedCount,
                    request.SchedulerFeedback.InvalidGenerationCount,
                    request.SchedulerFeedback.OverflowCount);
            globalConvergencePending =
                request.SchedulerFeedback.StaticConvergencePending != 0u ||
                request.TransportGlobalConvergencePending
                    ? 1
                    : 0;
            localConvergencePending =
                request.SchedulerFeedback.PendingFreshCount != 0u ||
                request.SchedulerFeedback.PendingExposedCount != 0u ||
                request.SchedulerFeedback.PendingRelocationCount != 0u ||
                request.SchedulerFeedback.PendingSourceCount != 0u ||
                request.SchedulerFeedback.PendingSolverCount != 0u ||
                eligibleProbeCount != 0u ||
                request.HasPendingUpdateTransaction ||
                !request.SchedulerFeedbackCoversCurrentVolumeTable
                    ? 1
                    : 0;
        }
        else
        {
            SimpleDdgiSchedulerTelemetry schedulerTelemetry =
                request.SchedulerTelemetry;
            eligibleByClass = new SimpleDdgiSchedulerClassCounts(
                ToUInt(schedulerTelemetry.PendingVisibleZeroSupport),
                ToUInt(schedulerTelemetry.PendingFreshExposedVisible),
                ToUInt(schedulerTelemetry.PendingVisibleDirty),
                ToUInt(schedulerTelemetry.PendingVisibleRetry),
                ToUInt(schedulerTelemetry.PendingNearMaintenance),
                ToUInt(schedulerTelemetry.PendingMidMaintenance),
                ToUInt(schedulerTelemetry.PendingFarMaintenance));
            eligibleProbeCount = Sum(eligibleByClass);
            selectedRequestCount = ToUInt(request.ProbesUpdated);
            indirectDispatchRequestCount = selectedRequestCount;
            coherentPublicationCount =
                ToUInt(request.ReceiverRecordsPublishedCount);
            committedUpdateCount = coherentPublicationCount;
            blendedUpdateCount = coherentPublicationCount;
            effectiveRequestBudget =
                ToUInt(schedulerTelemetry.EffectiveRequestBudget);
            effectiveRayBudget =
                ToUInt(request.ConfiguredPrimaryRayBudget);
            globalConvergencePending =
                request.TransportGlobalConvergencePending ? 1 : 0;
            localConvergencePending = eligibleProbeCount != 0u ||
                selectedRequestCount != 0u ||
                request.HasPendingUpdateTransaction
                    ? 1
                    : 0;
        }

        uint visibleDemandPageCount = 0u;
        uint visibleDemandSuppressedCount = 0u;
        uint visibleDemandInitializingOrUnpublishedCount = 0u;
        uint admissionCandidateCount = 0u;
        uint freePageCount = 0u;
        bool useAlignedSparseFeedback =
            request.GpuSchedulerAuthoritative &&
            request.SparseResidencyAuthoritative &&
            schedulerFeedbackValid &&
            residencyFeedbackValid &&
            feedbackGenerationsCompatible;
        if (useAlignedSparseFeedback)
        {
            visibleDemandPageCount =
                request.ResidencyFeedback.VisibleDemandPageCount;
            visibleDemandSuppressedCount = request.ResidencyFeedback
                .VisibleDemandSuppressedPageCount;
            visibleDemandInitializingOrUnpublishedCount =
                request.ResidencyFeedback
                    .VisibleDemandInitializingOrUnpublishedPageCount;
            admissionCandidateCount = SaturatingSubtract(
                SaturatingSubtract(
                    request.ResidencyFeedback.VisibleDemandMissingPageCount,
                    visibleDemandSuppressedCount),
                visibleDemandInitializingOrUnpublishedCount);
            freePageCount = request.ResidencyFeedback.FreePageCount;
            localConvergencePending |=
                admissionCandidateCount != 0u ? 1 : 0;
        }

        generationTransition |= request.SourceCacheInvalidationDeltas.Any;
        var generations = new SimpleDdgiGenerationTuple(
            FrameSerial: request.FrameSerial,
            SchedulerFeedbackFrameSerial: schedulerFeedbackFrameSerial,
            ResidencyFeedbackFrameSerial: residencyFeedbackFrameSerial,
            VolumeTableGeneration: request.VolumeTableGeneration,
            SchedulerArenaGeneration:
                request.GpuSchedulerAuthoritative &&
                schedulerFeedbackValid
                    ? request.SchedulerFeedback
                        .SchedulerResourceGeneration
                    : request.SchedulerResourceGeneration,
            ResidencyArenaGeneration:
                request.SparseResidencyAuthoritative &&
                residencyFeedbackValid
                    ? request.ResidencyFeedback
                        .ResidencyResourceGeneration
                    : request.ResidencyResourceGeneration,
            SourceLightingGeneration:
                request.GpuSchedulerAuthoritative &&
                schedulerFeedbackValid
                    ? request.SchedulerFeedback
                        .SourceLightingGeneration
                    : request.SourceLightingGeneration,
            TransportGeneration:
                request.GpuSchedulerAuthoritative &&
                schedulerFeedbackValid
                    ? request.SchedulerFeedback.TransportGeneration
                    : request.TransportGeneration);
        var telemetry = new SimpleDdgiLivenessTelemetry(
            Generations: generations,
            SchedulerFeedbackValid: schedulerFeedbackValid ? 1 : 0,
            ResidencyFeedbackValid: residencyFeedbackValid ? 1 : 0,
            FeedbackGenerationsCompatible:
                feedbackGenerationsCompatible ? 1 : 0,
            GlobalConvergencePending: globalConvergencePending,
            LocalConvergencePending: localConvergencePending,
            EligibleProbeCount: eligibleProbeCount,
            AdmissionCandidateCount: admissionCandidateCount,
            SelectedRequestCount: selectedRequestCount,
            IndirectDispatchRequestCount: indirectDispatchRequestCount,
            CommittedUpdateCount: committedUpdateCount,
            BlendedUpdateCount: blendedUpdateCount,
            CoherentPublicationCount: coherentPublicationCount,
            VisibleDemandPageCount: visibleDemandPageCount,
            VisibleDemandSuppressedCount: visibleDemandSuppressedCount,
            VisibleDemandInitializingOrUnpublishedCount:
                visibleDemandInitializingOrUnpublishedCount,
            FreePageCount: freePageCount,
            EffectiveRequestBudget: effectiveRequestBudget,
            EffectiveRayBudget: effectiveRayBudget,
            ReconfigurationBarrier: request.Recentered ||
                request.AtlasCleared ||
                request.ProbeResidencyBootstrapClassificationActive
                    ? 1
                    : 0,
            PublicationBarrier: 0,
            AuditBarrier: request.TransportTailAuditPending ? 1 : 0,
            GenerationTransitionBarrier:
                generationTransition ? 1 : 0,
            FeedbackRejectionReason: feedbackRejectionReason,
            EligibleBySchedulerClass: eligibleByClass,
            EligibleByRing: eligibleByRing,
            EligibilityRejections: eligibilityRejections,
            TransactionAbortDeltas: request.TransactionAbortDeltas,
            SourceCacheInvalidationDeltas:
                request.SourceCacheInvalidationDeltas);
        _liveness = new SimpleDdgiLivenessSnapshot(
            telemetry,
            _livenessWatchdog.Evaluate(telemetry));
        return _liveness;
    }

    public void ResetDisabled() => ResetLiveness();

    public SimpleDdgiFrameEvidenceSnapshot CaptureSnapshot() => new(
        CostEstimate,
        _completed,
        _sourceCacheObservation,
        _pending.Valid,
        _liveness);

    private SimpleDdgiLivenessBlockReason
        ObserveFeedbackGenerationRejections(
            bool observeSchedulerFeedback,
            bool observeResidencyFeedback,
            ulong schedulerRejectionCount,
            ulong residencyRejectionCount)
    {
        if (!observeSchedulerFeedback && !observeResidencyFeedback)
        {
            ResetFeedbackBaseline();
            return SimpleDdgiLivenessBlockReason.None;
        }

        if (!_feedbackRejectionBaselineInitialized)
        {
            _feedbackRejectionBaselineInitialized = true;
            _lastSchedulerFeedbackGenerationRejectionCount =
                observeSchedulerFeedback ? schedulerRejectionCount : 0UL;
            _lastResidencyFeedbackGenerationRejectionCount =
                observeResidencyFeedback ? residencyRejectionCount : 0UL;
            return SimpleDdgiLivenessBlockReason.None;
        }

        ulong currentSchedulerCount = observeSchedulerFeedback
            ? schedulerRejectionCount
            : 0UL;
        ulong currentResidencyCount = observeResidencyFeedback
            ? residencyRejectionCount
            : 0UL;
        bool rejected = currentSchedulerCount >
                _lastSchedulerFeedbackGenerationRejectionCount ||
            currentResidencyCount >
                _lastResidencyFeedbackGenerationRejectionCount;
        _lastSchedulerFeedbackGenerationRejectionCount =
            currentSchedulerCount;
        _lastResidencyFeedbackGenerationRejectionCount =
            currentResidencyCount;
        return rejected
            ? SimpleDdgiLivenessBlockReason.GenerationMismatch
            : SimpleDdgiLivenessBlockReason.None;
    }

    private void ResetLiveness()
    {
        ResetFeedbackBaseline();
        _livenessWatchdog.Reset();
        _liveness = SimpleDdgiLivenessSnapshot.Empty;
    }

    private void ResetFeedbackBaseline()
    {
        _feedbackRejectionBaselineInitialized = false;
        _lastSchedulerFeedbackGenerationRejectionCount = 0UL;
        _lastResidencyFeedbackGenerationRejectionCount = 0UL;
    }

    private static bool IsCurrentOrNextGeneration(
        uint recordedGeneration,
        uint currentGeneration)
    {
        if (recordedGeneration == currentGeneration)
            return true;

        uint nextGeneration = recordedGeneration + 1u;
        if (nextGeneration == 0u)
            nextGeneration = 1u;
        return nextGeneration == currentGeneration;
    }

    private static uint SaturatingSubtract(uint value, uint subtrahend) =>
        value > subtrahend ? value - subtrahend : 0u;

    private static uint ToUInt(int value) =>
        value <= 0 ? 0u : (uint)value;

    private static uint Sum(in SimpleDdgiSchedulerClassCounts counts)
    {
        uint sum = SaturatingAdd(
            counts.VisibleZeroSupport,
            counts.FreshExposedVisible);
        sum = SaturatingAdd(sum, counts.VisibleDirty);
        sum = SaturatingAdd(sum, counts.VisibleRetry);
        sum = SaturatingAdd(sum, counts.NearMaintenance);
        sum = SaturatingAdd(sum, counts.MidMaintenance);
        return SaturatingAdd(sum, counts.FarMaintenance);
    }

    private static uint Sum(in SimpleDdgiRingCounts counts) =>
        SaturatingAdd(
            SaturatingAdd(counts.Near, counts.Mid),
            counts.Far);

    private static uint SaturatingAdd(uint left, uint right)
    {
        ulong sum = (ulong)left + right;
        return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
    }
}

internal readonly record struct SimpleDdgiSubmittedWorkload(
    SimpleDdgiSubmittedFrameEvidence Evidence);

internal readonly record struct SimpleDdgiFenceCompletedEvidence(
    FrameTimingSnapshot Timings,
    DdgiForwardEstimateCounters ForwardEstimateCounters,
    DdgiInvestigationCounters InvestigationCounters,
    MaterialGiGpuCounters MaterialCounters,
    bool SchedulerFeedbackAvailable,
    GPUSimpleDdgiSchedulerFeedback SchedulerFeedback,
    uint SchedulerFeedbackTransportTopologyGeneration,
    uint SchedulerActiveCanonicalMutationCount,
    uint SchedulerActiveSourceMutationCount);

internal readonly record struct SimpleDdgiSourceCacheWorkloadObservation(
    bool Valid,
    ulong ShadeableHitCount,
    ulong MissCount,
    ulong RejectedBackFaceCount,
    ulong SourceCacheLayoutIdentity,
    ulong FrameSerial);

internal readonly record struct SimpleDdgiLivenessRequest(
    bool Active,
    bool GpuSchedulerAuthoritative,
    bool SparseResidencyAuthoritative,
    ulong FrameSerial,
    int ProbesUpdated,
    bool Recentered,
    bool AtlasCleared,
    bool SourceCohortTransitionActive,
    int ConfiguredPrimaryRayBudget,
    GPUSimpleDdgiSchedulerFeedback SchedulerFeedback,
    bool SchedulerFeedbackValid,
    ulong SchedulerFeedbackFrameSerial,
    ulong SchedulerFeedbackGenerationRejectionCount,
    uint SchedulerFeedbackTransportTopologyGeneration,
    uint SchedulerResourceGeneration,
    bool SchedulerFeedbackCoversCurrentVolumeTable,
    SimpleDdgiSchedulerEligibilityEvidence SchedulerEligibility,
    GPUSimpleDdgiResidencyFeedback ResidencyFeedback,
    bool ResidencyFeedbackValid,
    ulong ResidencyFeedbackFrameSerial,
    ulong ResidencyFeedbackGenerationRejectionCount,
    uint ResidencyResourceGeneration,
    uint VolumeTableGeneration,
    uint TransportTopologyGeneration,
    uint SourceLightingGeneration,
    uint TransportGeneration,
    bool TransportGlobalConvergencePending,
    bool HasPendingUpdateTransaction,
    int ReceiverRecordsPublishedCount,
    SimpleDdgiSchedulerTelemetry SchedulerTelemetry,
    bool ProbeResidencyBootstrapClassificationActive,
    bool TransportTailAuditPending,
    SimpleDdgiTransactionAbortDeltas TransactionAbortDeltas,
    SimpleDdgiSourceCacheInvalidationDeltas SourceCacheInvalidationDeltas);

internal readonly record struct SimpleDdgiLivenessSnapshot(
    SimpleDdgiLivenessTelemetry Telemetry,
    SimpleDdgiLivenessWatchdogResult Watchdog)
{
    public static SimpleDdgiLivenessSnapshot Empty { get; } = new(
        SimpleDdgiLivenessTelemetry.Empty,
        SimpleDdgiLivenessWatchdogResult.Empty);
}

internal readonly record struct SimpleDdgiFrameEvidenceSnapshot(
    SimpleDdgiSchedulerCostEstimate CostEstimate,
    SimpleDdgiCompletedFrameEvidence Completed,
    SimpleDdgiSourceCacheWorkloadObservation SourceCacheObservation,
    bool HasPendingCapture,
    SimpleDdgiLivenessSnapshot Liveness);
