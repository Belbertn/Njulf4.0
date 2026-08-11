using System;

namespace Njulf.Rendering.Data
{
    /// <summary>First pipeline stage that can explain a DDGI progress stall.</summary>
    public enum SimpleDdgiLivenessStage
    {
        None = 0,
        DemandWithoutAdmissionCandidate = 1,
        AdmissionCandidateNotSelected = 2,
        EligibleProbeNotSelected = 3,
        SelectedRequestNotDispatched = 4,
        DispatchedRequestNotCommitted = 5,
        CommittedUpdateNotPublished = 6,
        PublicationNotConverged = 7
    }

    /// <summary>
    /// Explicit non-stall explanation.  These values are intentionally not
    /// inferred from unrelated aggregate work counters.
    /// </summary>
    public enum SimpleDdgiLivenessBlockReason
    {
        None = 0,
        Inactive = 1,
        FeedbackInvalid = 2,
        GenerationMismatch = 3,
        FrameSerialRegressed = 4,
        NoPendingWork = 5,
        NoEligibleWork = 6,
        ZeroBudget = 7,
        ReconfigurationBarrier = 8,
        PublicationBarrier = 9,
        AuditBarrier = 10,
        GenerationTransitionBarrier = 11,
        SuppressedEmptyPages = 12,
        InitializingOrUnpublishedPages = 13,
        NoFreePageCapacity = 14,
        NoAdmissionCandidate = 15,
        SchedulerDidNotSelect = 16,
        NoIndirectDispatch = 17,
        TransactionAbort = 18
    }

    /// <summary>
    /// The transaction boundary at which a CPU-owned Simple-DDGI update was
    /// abandoned.  These values are deliberately stable capture identifiers;
    /// they are not formatted renderer messages.
    /// </summary>
    public enum SimpleDdgiUpdateTransactionAbortReason : byte
    {
        None = 0,
        TraceUnavailable = 1,
        RelocatePrerequisite = 2,
        TransportPrerequisite = 3,
        BlendPrerequisite = 4,
        PublishPrerequisite = 5,
        AcceleratedSolvePrerequisite = 6,
        SchedulerModeTransition = 7,
        Disabled = 8,
        Unknown = 9,
        Count = 10
    }

    /// <summary>
    /// The source-cache invalidation domain that changed.  A solver-only
    /// calibration is intentionally absent because it must not invalidate
    /// cached source radiance.
    /// </summary>
    public enum SimpleDdgiSourceCacheInvalidationReason : byte
    {
        None = 0,
        LightingSignature = 1,
        TransportActivation = 2,
        SourceCalibration = 3,
        SourceCacheResourceRecreated = 4,
        AtlasCleared = 5,
        TailRecovery = 6,
        Unknown = 7,
        Count = 8
    }

    /// <summary>Scheduler eligibility split by the fixed work-class ABI.</summary>
    public readonly record struct SimpleDdgiSchedulerClassCounts(
        uint VisibleZeroSupport,
        uint FreshExposedVisible,
        uint VisibleDirty,
        uint VisibleRetry,
        uint NearMaintenance,
        uint MidMaintenance,
        uint FarMaintenance)
    {
        public static SimpleDdgiSchedulerClassCounts Empty { get; } = default;
    }

    /// <summary>Near/mid/far residency or demand evidence.</summary>
    public readonly record struct SimpleDdgiRingCounts(
        uint Near,
        uint Mid,
        uint Far)
    {
        public static SimpleDdgiRingCounts Empty { get; } = default;
    }

    /// <summary>Eligibility failures reported by the GPU scheduler feedback ABI.</summary>
    public readonly record struct SimpleDdgiEligibilityRejectionCounts(
        uint Rejected,
        uint InvalidGeneration,
        uint Overflow)
    {
        public static SimpleDdgiEligibilityRejectionCounts Empty { get; } = default;
    }

    /// <summary>
    /// Exact GPU classification evidence copied from the bounded scheduler
    /// feedback region after its matching header has been validated.
    /// </summary>
    public readonly record struct SimpleDdgiSchedulerEligibilityEvidence(
        SimpleDdgiSchedulerClassCounts ByClass,
        SimpleDdgiRingCounts ByRing)
    {
        public static SimpleDdgiSchedulerEligibilityEvidence Empty { get; } = new(
            SimpleDdgiSchedulerClassCounts.Empty,
            SimpleDdgiRingCounts.Empty);
    }

    /// <summary>Per-frame deltas for completed CPU transaction aborts.</summary>
    public readonly record struct SimpleDdgiTransactionAbortDeltas(
        uint TraceUnavailable,
        uint RelocatePrerequisite,
        uint TransportPrerequisite,
        uint BlendPrerequisite,
        uint PublishPrerequisite,
        uint AcceleratedSolvePrerequisite,
        uint SchedulerModeTransition,
        uint Disabled,
        uint Unknown)
    {
        public static SimpleDdgiTransactionAbortDeltas Empty { get; } = default;

        public bool Any => TraceUnavailable != 0u ||
            RelocatePrerequisite != 0u ||
            TransportPrerequisite != 0u ||
            BlendPrerequisite != 0u ||
            PublishPrerequisite != 0u ||
            AcceleratedSolvePrerequisite != 0u ||
            SchedulerModeTransition != 0u ||
            Disabled != 0u ||
            Unknown != 0u;

        public static SimpleDdgiTransactionAbortDeltas Combine(
            in SimpleDdgiTransactionAbortDeltas left,
            in SimpleDdgiTransactionAbortDeltas right) => new(
                SaturatingAdd(left.TraceUnavailable, right.TraceUnavailable),
                SaturatingAdd(left.RelocatePrerequisite, right.RelocatePrerequisite),
                SaturatingAdd(left.TransportPrerequisite, right.TransportPrerequisite),
                SaturatingAdd(left.BlendPrerequisite, right.BlendPrerequisite),
                SaturatingAdd(left.PublishPrerequisite, right.PublishPrerequisite),
                SaturatingAdd(left.AcceleratedSolvePrerequisite, right.AcceleratedSolvePrerequisite),
                SaturatingAdd(left.SchedulerModeTransition, right.SchedulerModeTransition),
                SaturatingAdd(left.Disabled, right.Disabled),
                SaturatingAdd(left.Unknown, right.Unknown));

        private static uint SaturatingAdd(uint left, uint right)
        {
            return uint.MaxValue - left < right ? uint.MaxValue : left + right;
        }
    }

    /// <summary>Per-frame deltas for source-cache invalidation domains.</summary>
    public readonly record struct SimpleDdgiSourceCacheInvalidationDeltas(
        uint LightingSignature,
        uint TransportActivation,
        uint SourceCalibration,
        uint SourceCacheResourceRecreated,
        uint AtlasCleared,
        uint TailRecovery,
        uint Unknown)
    {
        public static SimpleDdgiSourceCacheInvalidationDeltas Empty { get; } = default;

        public bool Any => LightingSignature != 0u ||
            TransportActivation != 0u ||
            SourceCalibration != 0u ||
            SourceCacheResourceRecreated != 0u ||
            AtlasCleared != 0u ||
            TailRecovery != 0u ||
            Unknown != 0u;

        public static SimpleDdgiSourceCacheInvalidationDeltas Combine(
            in SimpleDdgiSourceCacheInvalidationDeltas left,
            in SimpleDdgiSourceCacheInvalidationDeltas right) => new(
                SaturatingAdd(left.LightingSignature, right.LightingSignature),
                SaturatingAdd(left.TransportActivation, right.TransportActivation),
                SaturatingAdd(left.SourceCalibration, right.SourceCalibration),
                SaturatingAdd(left.SourceCacheResourceRecreated, right.SourceCacheResourceRecreated),
                SaturatingAdd(left.AtlasCleared, right.AtlasCleared),
                SaturatingAdd(left.TailRecovery, right.TailRecovery),
                SaturatingAdd(left.Unknown, right.Unknown));

        private static uint SaturatingAdd(uint left, uint right)
        {
            return uint.MaxValue - left < right ? uint.MaxValue : left + right;
        }
    }

    /// <summary>
    /// Generation and serial tuple carried with every watchdog decision.  The
    /// feedback serials may intentionally lag <see cref="FrameSerial"/> by
    /// frames in flight; compatibility is represented explicitly by telemetry
    /// rather than requiring those serials to be numerically equal.
    /// </summary>
    public readonly record struct SimpleDdgiGenerationTuple(
        ulong FrameSerial,
        ulong SchedulerFeedbackFrameSerial,
        ulong ResidencyFeedbackFrameSerial,
        uint VolumeTableGeneration,
        uint SchedulerArenaGeneration,
        uint ResidencyArenaGeneration,
        uint SourceLightingGeneration,
        uint TransportGeneration)
    {
        public SimpleDdgiGenerationKey ToGenerationKey() => new(
            VolumeTableGeneration,
            SchedulerArenaGeneration,
            ResidencyArenaGeneration,
            SourceLightingGeneration,
            TransportGeneration);
    }

    /// <summary>Comparable resource portion of a generation tuple.</summary>
    public readonly record struct SimpleDdgiGenerationKey(
        uint VolumeTableGeneration,
        uint SchedulerArenaGeneration,
        uint ResidencyArenaGeneration,
        uint SourceLightingGeneration,
        uint TransportGeneration);

    /// <summary>
    /// Generation-aligned, stage-by-stage telemetry. The fixed-size
    /// reason/class/ring records remain separate from the scalar predicate so a
    /// missing optional detail can never manufacture a stall or allocation.
    /// </summary>
    public readonly record struct SimpleDdgiLivenessTelemetry(
        SimpleDdgiGenerationTuple Generations,
        int SchedulerFeedbackValid,
        int ResidencyFeedbackValid,
        int FeedbackGenerationsCompatible,
        int GlobalConvergencePending,
        int LocalConvergencePending,
        uint EligibleProbeCount,
        uint AdmissionCandidateCount,
        uint SelectedRequestCount,
        uint IndirectDispatchRequestCount,
        uint CommittedUpdateCount,
        uint BlendedUpdateCount,
        uint CoherentPublicationCount,
        uint VisibleDemandPageCount,
        uint VisibleDemandSuppressedCount,
        uint VisibleDemandInitializingOrUnpublishedCount,
        uint FreePageCount,
        uint EffectiveRequestBudget,
        uint EffectiveRayBudget,
        int ReconfigurationBarrier,
        int PublicationBarrier,
        int AuditBarrier,
        int GenerationTransitionBarrier,
        SimpleDdgiLivenessBlockReason FeedbackRejectionReason,
        SimpleDdgiSchedulerClassCounts EligibleBySchedulerClass,
        SimpleDdgiRingCounts EligibleByRing,
        SimpleDdgiEligibilityRejectionCounts EligibilityRejections,
        SimpleDdgiTransactionAbortDeltas TransactionAbortDeltas,
        SimpleDdgiSourceCacheInvalidationDeltas SourceCacheInvalidationDeltas)
    {
        public static SimpleDdgiLivenessTelemetry Empty { get; } = new(
            Generations: default,
            SchedulerFeedbackValid: 0,
            ResidencyFeedbackValid: 0,
            FeedbackGenerationsCompatible: 0,
            GlobalConvergencePending: 0,
            LocalConvergencePending: 0,
            EligibleProbeCount: 0u,
            AdmissionCandidateCount: 0u,
            SelectedRequestCount: 0u,
            IndirectDispatchRequestCount: 0u,
            CommittedUpdateCount: 0u,
            BlendedUpdateCount: 0u,
            CoherentPublicationCount: 0u,
            VisibleDemandPageCount: 0u,
            VisibleDemandSuppressedCount: 0u,
            VisibleDemandInitializingOrUnpublishedCount: 0u,
            FreePageCount: 0u,
            EffectiveRequestBudget: 0u,
            EffectiveRayBudget: 0u,
            ReconfigurationBarrier: 0,
            PublicationBarrier: 0,
            AuditBarrier: 0,
            GenerationTransitionBarrier: 0,
            FeedbackRejectionReason: SimpleDdgiLivenessBlockReason.None,
            EligibleBySchedulerClass: SimpleDdgiSchedulerClassCounts.Empty,
            EligibleByRing: SimpleDdgiRingCounts.Empty,
            EligibilityRejections: SimpleDdgiEligibilityRejectionCounts.Empty,
            TransactionAbortDeltas: SimpleDdgiTransactionAbortDeltas.Empty,
            SourceCacheInvalidationDeltas: SimpleDdgiSourceCacheInvalidationDeltas.Empty);

        public bool HasPendingWork => GlobalConvergencePending != 0 || LocalConvergencePending != 0;
        public bool HasEligibleWork => EligibleProbeCount != 0u || AdmissionCandidateCount != 0u;
        public bool HasPositiveBudget => EffectiveRequestBudget != 0u || EffectiveRayBudget != 0u;
        public bool HasDeclaredBarrier => ReconfigurationBarrier != 0 ||
            PublicationBarrier != 0 ||
            AuditBarrier != 0 ||
            GenerationTransitionBarrier != 0;
        public bool HasFeedback => SchedulerFeedbackValid != 0 && ResidencyFeedbackValid != 0;
        public bool HasProgress => CommittedUpdateCount != 0u ||
            BlendedUpdateCount != 0u ||
            CoherentPublicationCount != 0u;
    }

    /// <summary>Diagnostic outcome of a bounded liveness observation window.</summary>
    public readonly record struct SimpleDdgiLivenessWatchdogResult(
        int Active,
        int StallDetected,
        SimpleDdgiLivenessStage FirstStalledStage,
        SimpleDdgiLivenessBlockReason BlockingReason,
        int ElapsedFrames,
        int LatencyBoundFrames,
        uint EligibleProbeCount,
        uint AdmissionCandidateCount,
        uint SelectedRequestCount,
        uint IndirectDispatchRequestCount,
        uint CommittedUpdateCount,
        uint CoherentPublicationCount,
        uint EffectiveRequestBudget,
        uint EffectiveRayBudget,
        SimpleDdgiGenerationTuple Generations)
    {
        public static SimpleDdgiLivenessWatchdogResult Empty { get; } = default;
    }

    /// <summary>
    /// Diagnostic-only watchdog.  It deliberately requires compatible
    /// generations, pending + eligible work, available budget, no declared
    /// barrier, and a full bounded latency window without commit/publication
    /// progress.  It does not use a per-frame scheduled-count heuristic.
    /// </summary>
    public sealed class SimpleDdgiLivenessWatchdog
    {
        private readonly int _latencyBoundFrames;
        private bool _tracking;
        private SimpleDdgiGenerationKey _trackedGeneration;
        private ulong _lastFrameSerial;
        private ulong _lastProgressFrameSerial;

        public SimpleDdgiLivenessWatchdog(
            int framesInFlight,
            int schedulerFeedbackLatencyFrames,
            int residencyFeedbackLatencyFrames,
            int publicationReadbackLatencyFrames)
        {
            _latencyBoundFrames = CalculateLatencyBound(
                framesInFlight,
                schedulerFeedbackLatencyFrames,
                residencyFeedbackLatencyFrames,
                publicationReadbackLatencyFrames);
        }

        public int LatencyBoundFrames => _latencyBoundFrames;

        public static int CalculateLatencyBound(
            int framesInFlight,
            int schedulerFeedbackLatencyFrames,
            int residencyFeedbackLatencyFrames,
            int publicationReadbackLatencyFrames)
        {
            long bound = Math.Max(1, framesInFlight) +
                Math.Max(0, schedulerFeedbackLatencyFrames) +
                Math.Max(0, residencyFeedbackLatencyFrames) +
                Math.Max(0, publicationReadbackLatencyFrames);
            return bound > int.MaxValue ? int.MaxValue : (int)Math.Max(1L, bound);
        }

        public void Reset()
        {
            _tracking = false;
            _trackedGeneration = default;
            _lastFrameSerial = 0UL;
            _lastProgressFrameSerial = 0UL;
        }

        public SimpleDdgiLivenessWatchdogResult Evaluate(in SimpleDdgiLivenessTelemetry telemetry)
        {
            SimpleDdgiLivenessBlockReason blockReason = GetBlockingReason(telemetry);
            ulong frameSerial = telemetry.Generations.FrameSerial;
            if (blockReason != SimpleDdgiLivenessBlockReason.None)
            {
                Reset();
                return CreateResult(
                    telemetry,
                    active: false,
                    stalled: false,
                    stage: GetDiagnosticStage(telemetry, blockReason),
                    blockReason,
                    elapsedFrames: 0);
            }

            SimpleDdgiGenerationKey generation = telemetry.Generations.ToGenerationKey();
            if (_tracking && frameSerial <= _lastFrameSerial)
            {
                Reset();
                return CreateResult(
                    telemetry,
                    active: false,
                    stalled: false,
                    stage: SimpleDdgiLivenessStage.None,
                    SimpleDdgiLivenessBlockReason.FrameSerialRegressed,
                    elapsedFrames: 0);
            }

            if (!_tracking || _trackedGeneration != generation)
            {
                _tracking = true;
                _trackedGeneration = generation;
                _lastProgressFrameSerial = frameSerial;
            }
            else if (telemetry.HasProgress)
            {
                _lastProgressFrameSerial = frameSerial;
            }

            _lastFrameSerial = frameSerial;
            ulong elapsed = frameSerial >= _lastProgressFrameSerial
                ? frameSerial - _lastProgressFrameSerial
                : 0UL;
            int elapsedFrames = elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
            SimpleDdgiLivenessStage stage = GetProgressStage(telemetry);
            bool stalled = elapsedFrames > _latencyBoundFrames;
            return CreateResult(
                telemetry,
                active: true,
                stalled,
                stage,
                stalled ? GetStallReason(telemetry, stage) : SimpleDdgiLivenessBlockReason.None,
                elapsedFrames);
        }

        private static SimpleDdgiLivenessBlockReason GetBlockingReason(
            in SimpleDdgiLivenessTelemetry telemetry)
        {
            if (!telemetry.HasPendingWork)
                return SimpleDdgiLivenessBlockReason.NoPendingWork;
            if (telemetry.FeedbackRejectionReason != SimpleDdgiLivenessBlockReason.None)
                return telemetry.FeedbackRejectionReason;
            if (!telemetry.HasFeedback)
                return SimpleDdgiLivenessBlockReason.FeedbackInvalid;
            if (telemetry.FeedbackGenerationsCompatible == 0)
                return SimpleDdgiLivenessBlockReason.GenerationMismatch;
            if (telemetry.TransactionAbortDeltas.Any)
                return SimpleDdgiLivenessBlockReason.TransactionAbort;
            if (telemetry.ReconfigurationBarrier != 0)
                return SimpleDdgiLivenessBlockReason.ReconfigurationBarrier;
            if (telemetry.PublicationBarrier != 0)
                return SimpleDdgiLivenessBlockReason.PublicationBarrier;
            if (telemetry.AuditBarrier != 0)
                return SimpleDdgiLivenessBlockReason.AuditBarrier;
            if (telemetry.GenerationTransitionBarrier != 0)
                return SimpleDdgiLivenessBlockReason.GenerationTransitionBarrier;
            if (!telemetry.HasEligibleWork)
            {
                if (telemetry.VisibleDemandSuppressedCount != 0u)
                    return SimpleDdgiLivenessBlockReason.SuppressedEmptyPages;
                if (telemetry.VisibleDemandInitializingOrUnpublishedCount != 0u)
                    return SimpleDdgiLivenessBlockReason.InitializingOrUnpublishedPages;
                if (telemetry.VisibleDemandPageCount != 0u)
                    return SimpleDdgiLivenessBlockReason.NoAdmissionCandidate;
                return SimpleDdgiLivenessBlockReason.NoEligibleWork;
            }
            if (telemetry.AdmissionCandidateCount != 0u &&
                telemetry.EligibleProbeCount == 0u &&
                telemetry.FreePageCount == 0u)
            {
                return SimpleDdgiLivenessBlockReason.NoFreePageCapacity;
            }
            if (!telemetry.HasPositiveBudget)
                return SimpleDdgiLivenessBlockReason.ZeroBudget;
            return SimpleDdgiLivenessBlockReason.None;
        }

        private static SimpleDdgiLivenessStage GetDiagnosticStage(
            in SimpleDdgiLivenessTelemetry telemetry,
            SimpleDdgiLivenessBlockReason reason)
        {
            if (reason is SimpleDdgiLivenessBlockReason.SuppressedEmptyPages or
                SimpleDdgiLivenessBlockReason.InitializingOrUnpublishedPages or
                SimpleDdgiLivenessBlockReason.NoAdmissionCandidate)
            {
                return SimpleDdgiLivenessStage.DemandWithoutAdmissionCandidate;
            }

            return reason == SimpleDdgiLivenessBlockReason.NoFreePageCapacity
                ? SimpleDdgiLivenessStage.AdmissionCandidateNotSelected
                : SimpleDdgiLivenessStage.None;
        }

        private static SimpleDdgiLivenessStage GetProgressStage(
            in SimpleDdgiLivenessTelemetry telemetry)
        {
            if (telemetry.AdmissionCandidateCount != 0u && telemetry.EligibleProbeCount == 0u)
            {
                if (telemetry.FreePageCount == 0u)
                    return SimpleDdgiLivenessStage.AdmissionCandidateNotSelected;
                if (telemetry.SelectedRequestCount == 0u)
                    return SimpleDdgiLivenessStage.AdmissionCandidateNotSelected;
            }

            if (telemetry.EligibleProbeCount != 0u && telemetry.SelectedRequestCount == 0u)
                return SimpleDdgiLivenessStage.EligibleProbeNotSelected;
            if (telemetry.SelectedRequestCount != 0u && telemetry.IndirectDispatchRequestCount == 0u)
                return SimpleDdgiLivenessStage.SelectedRequestNotDispatched;
            if (telemetry.IndirectDispatchRequestCount != 0u && telemetry.CommittedUpdateCount == 0u)
                return SimpleDdgiLivenessStage.DispatchedRequestNotCommitted;
            if (telemetry.CommittedUpdateCount != 0u &&
                telemetry.BlendedUpdateCount == 0u &&
                telemetry.CoherentPublicationCount == 0u)
            {
                return SimpleDdgiLivenessStage.CommittedUpdateNotPublished;
            }

            return SimpleDdgiLivenessStage.PublicationNotConverged;
        }

        private static SimpleDdgiLivenessBlockReason GetStallReason(
            in SimpleDdgiLivenessTelemetry telemetry,
            SimpleDdgiLivenessStage stage) => stage switch
        {
            SimpleDdgiLivenessStage.AdmissionCandidateNotSelected when telemetry.FreePageCount == 0u =>
                SimpleDdgiLivenessBlockReason.NoFreePageCapacity,
            SimpleDdgiLivenessStage.AdmissionCandidateNotSelected or
            SimpleDdgiLivenessStage.EligibleProbeNotSelected =>
                SimpleDdgiLivenessBlockReason.SchedulerDidNotSelect,
            SimpleDdgiLivenessStage.SelectedRequestNotDispatched =>
                SimpleDdgiLivenessBlockReason.NoIndirectDispatch,
            _ => SimpleDdgiLivenessBlockReason.None
        };

        private SimpleDdgiLivenessWatchdogResult CreateResult(
            in SimpleDdgiLivenessTelemetry telemetry,
            bool active,
            bool stalled,
            SimpleDdgiLivenessStage stage,
            SimpleDdgiLivenessBlockReason reason,
            int elapsedFrames) => new(
                Active: active ? 1 : 0,
                StallDetected: stalled ? 1 : 0,
                FirstStalledStage: stage,
                BlockingReason: reason,
                ElapsedFrames: elapsedFrames,
                LatencyBoundFrames: _latencyBoundFrames,
                EligibleProbeCount: telemetry.EligibleProbeCount,
                AdmissionCandidateCount: telemetry.AdmissionCandidateCount,
                SelectedRequestCount: telemetry.SelectedRequestCount,
                IndirectDispatchRequestCount: telemetry.IndirectDispatchRequestCount,
                CommittedUpdateCount: telemetry.CommittedUpdateCount,
                CoherentPublicationCount: telemetry.CoherentPublicationCount,
                EffectiveRequestBudget: telemetry.EffectiveRequestBudget,
                EffectiveRayBudget: telemetry.EffectiveRayBudget,
                Generations: telemetry.Generations);
    }
}
