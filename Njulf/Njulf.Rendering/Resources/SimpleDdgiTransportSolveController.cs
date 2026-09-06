using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiBlendSweepWork(
    bool WritesIrradiance,
    bool WritesVisibility,
    bool AdvancesOneUpdateLifecycle);

public enum SimpleDdgiTailCertificationFallbackReason : byte
{
    None = 0,
    DisabledByConfiguration = 1,
    RequiresGpuResidentScheduler = 2,
    GpuSchedulerNotReady = 3,
    GpuSchedulerFrameExecutionUnavailable = 4,
    GuidedOperatorUnsupported = 5
}

public readonly record struct SimpleDdgiTailCertificationAvailability(
    bool Enabled,
    SimpleDdgiTailCertificationFallbackReason Reason)
{
    public string Message => Reason switch
    {
        SimpleDdgiTailCertificationFallbackReason.None => string.Empty,
        SimpleDdgiTailCertificationFallbackReason.DisabledByConfiguration =>
            "Tail certification is disabled by configuration.",
        SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler =>
            "Tail certification requires the GpuResident Simple-DDGI scheduler; CPU reference and GPU mirror modes use uncertified fallback convergence.",
        SimpleDdgiTailCertificationFallbackReason.GpuSchedulerNotReady =>
            "Tail certification is pending because the GpuResident scheduler resources are not ready.",
        SimpleDdgiTailCertificationFallbackReason.GpuSchedulerFrameExecutionUnavailable =>
            "Tail certification is disabled because GpuResident scheduler frame execution is unavailable.",
        SimpleDdgiTailCertificationFallbackReason.GuidedOperatorUnsupported =>
            "Tail certification is disabled because directional guiding is active but the matching guided audit operator is unavailable.",
        _ => "Tail certification is unavailable for an unknown reason."
    };
}

/// <summary>
/// The scheduler-facing state machine for error-bounded V2 transport.  It is
/// intentionally independent of Vulkan objects so generation invalidation and
/// complete-epoch rules can be exercised without a device.
/// </summary>
public sealed class SimpleDdgiTransportSolveController
{
    private uint[] _participantVisitEpoch;
    private int _expectedParticipantCount;
    private int _visitedParticipantCount;
    private uint _solveEpoch;
    private uint _auditEpoch;
    private bool _auditCancelled;
    private bool _completedAuditPending;
    private SimpleDdgiTransportTailSummary _pendingAuditSummary;
    private SimpleDdgiTransportGenerations _pendingAuditCurrentGenerations;
    private SimpleDdgiTransportAuditTuple _lastRejectedAuditTuple;
    private bool _hasLastRejectedAuditTuple;
    private ulong _sameTupleReauditAttemptCount;
    private ulong _recoveryCount;
    private uint _recoveryGeneration;
    private int _noProgressFrames;

    public SimpleDdgiTransportSolveController(int participantCapacity = 0)
    {
        _participantVisitEpoch = participantCapacity > 0
            ? new uint[participantCapacity]
            : Array.Empty<uint>();
        Phase = SimpleDdgiTransportPhase.SourceRepair;
        LastReason = SimpleDdgiTransportCertificationReason.SourceRepairRequired;
        LastSummary = SimpleDdgiTransportTailSummary.Empty;
    }

    public SimpleDdgiTransportPhase Phase { get; private set; }
    public SimpleDdgiTransportCertificationReason LastReason { get; private set; }
    public SimpleDdgiTransportTailSummary LastSummary { get; private set; }
    public SimpleDdgiTransportGenerations FrozenGenerations { get; private set; }
    public uint SolveEpoch => _solveEpoch;
    public uint AuditEpoch => _auditEpoch;
    public int ExpectedParticipantCount => _expectedParticipantCount;
    public int VisitedParticipantCount => _visitedParticipantCount;
    public int ParticipantVisitCapacity => _participantVisitEpoch.Length;
    public bool CompletedAuditPending => _completedAuditPending;
    public ulong SameTupleReauditAttemptCount => _sameTupleReauditAttemptCount;
    public ulong RecoveryCount => _recoveryCount;
    public uint RecoveryGeneration => _recoveryGeneration;
    public int NoProgressFrames => _noProgressFrames;
    public SimpleDdgiTransportRecoveryAction RecoveryAction { get; private set; }
    public bool IsSolveEpochComplete =>
        Phase == SimpleDdgiTransportPhase.AcceleratedSolve &&
        _visitedParticipantCount == _expectedParticipantCount;
    public bool IsCertified => Phase == SimpleDdgiTransportPhase.Certified && LastSummary.IsCertified;

    /// <summary>
    /// CPU mirror of the blend shader's per-sweep side-effect policy. Cached
    /// sweeps always advance irradiance, while visibility and transaction
    /// lifecycle work are restricted to sweep zero's first color.
    /// </summary>
    public static SimpleDdgiBlendSweepWork ResolveBlendSweepWork(
        int sweepIndex,
        bool isFirstColor,
        bool transportV2Active,
        bool requiresSourceRefresh,
        bool freshUpdate)
    {
        if (sweepIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sweepIndex));

        bool firstSweepFirstColor = sweepIndex == 0 && isFirstColor;
        bool visibility = !transportV2Active ||
            (firstSweepFirstColor && (requiresSourceRefresh || freshUpdate));
        return new SimpleDdgiBlendSweepWork(
            WritesIrradiance: true,
            WritesVisibility: visibility,
            AdvancesOneUpdateLifecycle: !transportV2Active || firstSweepFirstColor);
    }

    public static SimpleDdgiTailCertificationAvailability ResolveTailCertificationAvailability(
        bool requested,
        SimpleDdgiSchedulerMode schedulerMode,
        bool gpuSchedulerReady,
        bool gpuSchedulerFrameExecutionAvailable,
        bool guidedTransportActive = false,
        bool guidedAuditAvailable = false)
    {
        if (!requested)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.DisabledByConfiguration);
        }

        if (schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler);
        }

        if (!gpuSchedulerReady)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.GpuSchedulerNotReady);
        }

        if (!gpuSchedulerFrameExecutionAvailable)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.GpuSchedulerFrameExecutionUnavailable);
        }

        if (guidedTransportActive && !guidedAuditAvailable)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.GuidedOperatorUnsupported);
        }

        return new SimpleDdgiTailCertificationAvailability(
            true,
            SimpleDdgiTailCertificationFallbackReason.None);
    }

    /// <summary>
    /// Starts a source-repair transaction and invalidates any certificate that
    /// belongs to the old source or ownership generations.
    /// </summary>
    public void BeginSourceRepair(
        SimpleDdgiTransportGenerations generations,
        SimpleDdgiTransportCertificationReason reason =
            SimpleDdgiTransportCertificationReason.SourceRepairRequired,
        SimpleDdgiTransportRecoveryAction recoveryAction =
            SimpleDdgiTransportRecoveryAction.None)
    {
        FrozenGenerations = generations;
        _expectedParticipantCount = 0;
        _visitedParticipantCount = 0;
        _auditCancelled = false;
        _completedAuditPending = false;
        RecoveryAction = recoveryAction;
        Phase = SimpleDdgiTransportPhase.SourceRepair;
        LastReason = reason;
        LastSummary = SimpleDdgiTransportTailSummary.Empty with
        {
            Generations = FrozenGenerations,
            Reason = reason
        };
    }

    /// <summary>
    /// Starts a complete solve epoch.  Every participant must be visited once
    /// before <see cref="TryBeginAudit"/> can succeed; zero participants is a
    /// valid empty field only when the caller explicitly supplies zero.
    /// </summary>
    public bool BeginSolveEpoch(SimpleDdgiTransportGenerations generations, int expectedParticipantCount)
    {
        if (expectedParticipantCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedParticipantCount));

        if (Phase == SimpleDdgiTransportPhase.AuditFrozen)
        {
            LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
            return false;
        }

        EnsureParticipantCapacity(expectedParticipantCount);
        _expectedParticipantCount = expectedParticipantCount;
        AdvanceSolveEpoch(generations);
        RecoveryAction = SimpleDdgiTransportRecoveryAction.None;
        LastReason = expectedParticipantCount == 0
            ? SimpleDdgiTransportCertificationReason.SolveEpochIncomplete
            : SimpleDdgiTransportCertificationReason.None;
        LastSummary = SimpleDdgiTransportTailSummary.Empty with
        {
            Generations = FrozenGenerations
        };
        return true;
    }

    /// <summary>
    /// Reopens the completion witness for probe-local source repair without
    /// advancing the field solve epoch. GPU visit stamps for overlapping probes
    /// remain valid; repaired probes clear and reacquire their stamp in the same
    /// epoch before <see cref="MarkGpuEpochComplete"/> can close it again.
    /// </summary>
    public bool PauseSolveForLocalSourceRepair(
        SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !IsSolveGenerationCompatible(generations, FrozenGenerations))
        {
            return false;
        }

        FrozenGenerations = generations with
        {
            Solve = _solveEpoch,
            Audit = NonZeroGeneration(_auditEpoch)
        };
        _visitedParticipantCount = 0;
        _auditCancelled = false;
        _completedAuditPending = false;
        RecoveryAction = SimpleDdgiTransportRecoveryAction.None;
        LastReason = SimpleDdgiTransportCertificationReason.SourceRepairRequired;
        LastSummary = LastSummary with
        {
            Generations = FrozenGenerations,
            Reason = LastReason,
            IsComplete = false
        };
        return true;
    }

    /// <summary>
    /// Marks one active participant as visited by the current accelerated
    /// epoch.  Duplicate visits are rejected so a malformed queue cannot make
    /// an incomplete field appear complete.
    /// </summary>
    public bool MarkParticipantVisited(int participantIndex, SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !TryRefreshSolveGenerations(generations) ||
            (uint)participantIndex >= (uint)_participantVisitEpoch.Length)
        {
            LastReason = !IsSolveGenerationCompatible(generations, FrozenGenerations)
                ? SimpleDdgiTransportCertificationReason.GenerationsChanged
                : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        uint stamp = _solveEpoch;
        if (_participantVisitEpoch[participantIndex] == stamp)
        {
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        _participantVisitEpoch[participantIndex] = stamp;
        _visitedParticipantCount++;
        if (_visitedParticipantCount != _expectedParticipantCount)
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
        return true;
    }

    /// <summary>
    /// Advances the mutable canonical/queue observations while an epoch is
    /// solving. Those generations naturally change after each publication and
    /// queue transaction; source/operator/ownership changes do not belong to
    /// this method and invalidate the epoch instead.
    /// </summary>
    public bool TryRefreshSolveGenerations(SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !IsSolveGenerationCompatible(generations, FrozenGenerations))
        {
            return false;
        }

        FrozenGenerations = generations;
        LastSummary = LastSummary with { Generations = generations };
        return true;
    }

    /// <summary>
    /// Completes the visit witness produced by the GPU-resident scheduler.
    /// The scheduler has already reduced the per-probe stamps, so the host
    /// must not synthesize individual visits from a delayed queue summary.
    /// Requiring the exact epoch, generation, and participant count prevents
    /// a stale feedback packet from authorizing an audit for a different field.
    /// </summary>
    public bool MarkGpuEpochComplete(
        uint solveEpoch,
        int participantCount,
        SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            solveEpoch == 0u ||
            solveEpoch != _solveEpoch ||
            generations != FrozenGenerations ||
            participantCount < 0 ||
            participantCount != _expectedParticipantCount)
        {
            LastReason = generations != FrozenGenerations
                ? SimpleDdgiTransportCertificationReason.GenerationsChanged
                : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        _visitedParticipantCount = participantCount;
        LastReason = participantCount > 0
            ? SimpleDdgiTransportCertificationReason.None
            : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
        return IsSolveEpochComplete;
    }

    /// <summary>
    /// Binds the participant cardinality from the first generation-matched GPU
    /// reduction for a newly uploaded solve epoch. The host necessarily starts
    /// that epoch from delayed feedback for the preceding epoch, during which
    /// source-ready membership can still be zero or incomplete. Only an
    /// unvisited epoch may be rebound; the exact-count check in
    /// <see cref="MarkGpuEpochComplete"/> remains authoritative afterwards.
    /// </summary>
    public bool TryBindGpuEpochParticipantCount(
        uint solveEpoch,
        int participantCount,
        SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            solveEpoch == 0u ||
            solveEpoch != _solveEpoch ||
            generations != FrozenGenerations ||
            participantCount < 0 ||
            participantCount > _participantVisitEpoch.Length ||
            _visitedParticipantCount != 0)
        {
            LastReason = generations != FrozenGenerations
                ? SimpleDdgiTransportCertificationReason.GenerationsChanged
                : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        _expectedParticipantCount = participantCount;
        LastReason = participantCount > 0
            ? SimpleDdgiTransportCertificationReason.None
            : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
        LastSummary = LastSummary with
        {
            Generations = generations,
            Reason = LastReason,
            IsComplete = false
        };
        return true;
    }

    /// <summary>
    /// Freezes the resource generations for the audit.  No queue publication or
    /// source refresh is allowed to mutate this snapshot while the audit is in
    /// flight.
    /// </summary>
    public bool TryBeginAudit(SimpleDdgiTransportGenerations generations)
    {
        if (_completedAuditPending)
        {
            LastReason = SimpleDdgiTransportCertificationReason.CompletedAuditUnconsumed;
            return false;
        }

        // Render-pass predicates may poll this method every frame. A current
        // accepted certificate is an idle terminal state, not an incomplete
        // solve, so an idempotent poll must not corrupt its diagnostic reason.
        if (IsCertified)
            return false;

        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !IsSolveEpochComplete)
        {
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        SimpleDdgiTransportAuditTuple candidate = CreateAuditTuple(
            generations,
            _expectedParticipantCount);
        if (_hasLastRejectedAuditTuple && candidate == _lastRejectedAuditTuple)
        {
            _sameTupleReauditAttemptCount = SaturatingIncrement(
                _sameTupleReauditAttemptCount);
            EnterRecovery(
                SimpleDdgiTransportPhase.ParticipantReconciliation,
                SimpleDdgiTransportCertificationReason.SameTupleReauditBlocked,
                SimpleDdgiTransportRecoveryAction.ReconcileParticipants,
                clearParticipantWitness: true);
            return false;
        }

        if (!TryRefreshSolveGenerations(generations))
        {
            LastReason = SimpleDdgiTransportCertificationReason.GenerationsChanged;
            return false;
        }

        _auditEpoch = NextNonZero(_auditEpoch);
        _auditCancelled = false;
        FrozenGenerations = FrozenGenerations with { Audit = _auditEpoch };
        LastSummary = LastSummary with { Generations = FrozenGenerations };
        Phase = SimpleDdgiTransportPhase.AuditFrozen;
        LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
        RecoveryAction = SimpleDdgiTransportRecoveryAction.None;
        return true;
    }

    /// <summary>
    /// Accepts a summary only when it describes the exact frozen epoch and has
    /// complete participant/texel coverage.  A failed audit returns to solving
    /// and never leaves a stale certificate active.
    /// </summary>
    public bool TryAcceptAudit(
        SimpleDdgiTransportTailSummary summary,
        SimpleDdgiTransportGenerations currentGenerations)
    {
        if (!TryStageCompletedAudit(summary, currentGenerations))
            return false;

        _ = TryConsumeCompletedAudit(out bool accepted);
        return accepted;
    }

    /// <summary>
    /// Stages one fence-complete audit summary.  Staging and consumption are
    /// separate so a completed readback can never be overwritten or followed
    /// by a new audit while manager integration is still deciding recovery.
    /// </summary>
    public bool TryStageCompletedAudit(
        SimpleDdgiTransportTailSummary summary,
        SimpleDdgiTransportGenerations currentGenerations)
    {
        if (_completedAuditPending)
        {
            LastReason = SimpleDdgiTransportCertificationReason.CompletedAuditUnconsumed;
            return false;
        }

        if (Phase != SimpleDdgiTransportPhase.AuditFrozen || _auditCancelled)
        {
            LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
            return false;
        }

        if (summary.AuditEpoch != _auditEpoch ||
            summary.Generations != FrozenGenerations ||
            currentGenerations != FrozenGenerations)
        {
            CancelAudit(SimpleDdgiTransportCertificationReason.GenerationsChanged);
            return false;
        }

        _pendingAuditSummary = summary;
        _pendingAuditCurrentGenerations = currentGenerations;
        _completedAuditPending = true;
        return true;
    }

    /// <summary>Consumes the staged summary exactly once.</summary>
    public bool TryConsumeCompletedAudit(out bool accepted)
    {
        accepted = false;
        if (!_completedAuditPending)
            return false;

        SimpleDdgiTransportTailSummary summary = _pendingAuditSummary;
        SimpleDdgiTransportGenerations currentGenerations =
            _pendingAuditCurrentGenerations;
        _completedAuditPending = false;
        _pendingAuditSummary = default;
        _pendingAuditCurrentGenerations = default;

        SimpleDdgiTransportAuditTuple rejectedTuple = CreateAuditTuple(
            summary.Generations,
            checked((int)Math.Min(int.MaxValue, summary.ExpectedParticipantCount)));

        if (!summary.HasExactParticipantCoverage || !summary.HasExactTexelCoverage)
        {
            bool cacheFailure = summary.ExcludedInvalidCacheCount != 0u ||
                summary.CacheIdentityFailureCount != 0u ||
                summary.CacheCardinalityFailureCount != 0u ||
                summary.CacheSourceGenerationFailureCount != 0u ||
                summary.CacheSourceEpochFailureCount != 0u ||
                summary.CachePhysicalGenerationFailureCount != 0u ||
                summary.ExcludedStaleSourceCount != 0u;
            SimpleDdgiTransportCertificationReason reason = cacheFailure
                ? SimpleDdgiTransportCertificationReason.InvalidCache
                : SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete;
            LastSummary = summary with { Reason = reason };
            RememberRejectedTuple(rejectedTuple);
            EnterRecovery(
                cacheFailure
                    ? SimpleDdgiTransportPhase.SourceRepair
                    : SimpleDdgiTransportPhase.ParticipantReconciliation,
                reason,
                cacheFailure
                    ? SimpleDdgiTransportRecoveryAction.RepairSourceCache
                    : SimpleDdgiTransportRecoveryAction.ReconcileParticipants,
                clearParticipantWitness: true,
                preserveSummary: true);
            return true;
        }

        if (summary.CounterOverflowCount != 0u || !summary.HasFiniteEvidence)
        {
            SimpleDdgiTransportCertificationReason reason =
                summary.CounterOverflowCount != 0u ||
                summary.Reason == SimpleDdgiTransportCertificationReason.CounterOverflow
                    ? SimpleDdgiTransportCertificationReason.CounterOverflow
                    : SimpleDdgiTransportCertificationReason.NonFiniteEvidence;
            LastSummary = summary with { Reason = reason };
            RememberRejectedTuple(rejectedTuple);
            EnterRecovery(
                SimpleDdgiTransportPhase.FailClosedRecovery,
                reason,
                SimpleDdgiTransportRecoveryAction.RebuildPrivateField,
                clearParticipantWitness: true,
                preserveSummary: true);
            return true;
        }

        if (summary.Reason != SimpleDdgiTransportCertificationReason.Certified ||
            !summary.IsCertified)
        {
            LastSummary = summary;
            LastReason = summary.Reason switch
            {
                SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete =>
                    SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete,
                SimpleDdgiTransportCertificationReason.CounterOverflow =>
                    SimpleDdgiTransportCertificationReason.CounterOverflow,
                SimpleDdgiTransportCertificationReason.NonFiniteEvidence =>
                    SimpleDdgiTransportCertificationReason.NonFiniteEvidence,
                SimpleDdgiTransportCertificationReason.QuantizationLimited =>
                    SimpleDdgiTransportCertificationReason.QuantizationLimited,
                SimpleDdgiTransportCertificationReason.TailAboveTolerance =>
                    SimpleDdgiTransportCertificationReason.TailAboveTolerance,
                _ => summary.CanonicalQuantizationFloor > summary.Tolerance
                    ? SimpleDdgiTransportCertificationReason.QuantizationLimited
                    : SimpleDdgiTransportCertificationReason.TailAboveTolerance
            };
            RememberRejectedTuple(rejectedTuple);
            // A finite, complete audit above tolerance is useful evidence, but
            // it does not authorize another audit of the byte-identical field.
            // Start a distinct epoch and clear its visit witness so every probe
            // receives another cached solve before certification is attempted.
            if (LastReason == SimpleDdgiTransportCertificationReason.TailAboveTolerance)
            {
                RecoveryAction = SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch;
                _recoveryCount = SaturatingIncrement(_recoveryCount);
                AdvanceSolveEpoch(currentGenerations);
            }
            else if (LastReason == SimpleDdgiTransportCertificationReason.QuantizationLimited)
            {
                EnterRecovery(
                    SimpleDdgiTransportPhase.UnsupportedTolerance,
                    LastReason,
                    SimpleDdgiTransportRecoveryAction.ReportUnsupportedTolerance,
                    clearParticipantWitness: true,
                    preserveSummary: true);
            }
            else if (LastReason is SimpleDdgiTransportCertificationReason.CounterOverflow or
                     SimpleDdgiTransportCertificationReason.NonFiniteEvidence or
                     SimpleDdgiTransportCertificationReason.InvalidContractionBound)
            {
                EnterRecovery(
                    SimpleDdgiTransportPhase.FailClosedRecovery,
                    LastReason,
                    SimpleDdgiTransportRecoveryAction.RebuildPrivateField,
                    clearParticipantWitness: true,
                    preserveSummary: true);
            }
            else
            {
                EnterRecovery(
                    SimpleDdgiTransportPhase.ParticipantReconciliation,
                    LastReason,
                    SimpleDdgiTransportRecoveryAction.ReconcileParticipants,
                    clearParticipantWitness: true,
                    preserveSummary: true);
            }
            return true;
        }

        LastSummary = summary;
        LastReason = SimpleDdgiTransportCertificationReason.Certified;
        Phase = SimpleDdgiTransportPhase.Certified;
        RecoveryAction = SimpleDdgiTransportRecoveryAction.None;
        _hasLastRejectedAuditTuple = false;
        accepted = true;
        return true;
    }

    /// <summary>
    /// Cancels a frozen audit.  Publication, generation changes, source-cache
    /// invalidation, and non-finite evidence all use this path.
    /// </summary>
    public void CancelAudit(SimpleDdgiTransportCertificationReason reason)
    {
        // Cancellation is scoped to an in-flight audit. A late or repeated
        // notification must not overwrite an accepted certificate or recovery
        // result; actual field changes go through generation invalidation.
        if (Phase != SimpleDdgiTransportPhase.AuditFrozen)
            return;

        _auditCancelled = true;
        Phase = SimpleDdgiTransportPhase.AcceleratedSolve;

        LastReason = reason;
        LastSummary = LastSummary with { Reason = reason };
    }

    /// <summary>
    /// Cancels a readback that exceeded its computed fence deadline and starts
    /// a distinct solve epoch.  The byte-identical completed visit witness is
    /// never left armed after the timeout.
    /// </summary>
    public bool ExpireAudit(SimpleDdgiTransportGenerations currentGenerations)
    {
        if (Phase != SimpleDdgiTransportPhase.AuditFrozen)
            return false;

        RememberRejectedTuple(CreateAuditTuple(
            FrozenGenerations,
            _expectedParticipantCount));
        LastReason = SimpleDdgiTransportCertificationReason.AuditReadbackTimeout;
        LastSummary = LastSummary with
        {
            Reason = LastReason,
            IsComplete = false
        };
        RecoveryAction = SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch;
        _recoveryCount = SaturatingIncrement(_recoveryCount);
        AdvanceSolveEpoch(currentGenerations);
        return true;
    }

    /// <summary>
    /// Enters the same bounded fail-closed path used by invalid audit evidence
    /// when fence-complete scheduler feedback proves that a non-empty source
    /// cohort is making no work progress.
    /// </summary>
    public void EnterSourceCohortRecovery(
        SimpleDdgiTransportGenerations currentGenerations)
    {
        FrozenGenerations = currentGenerations;
        LastSummary = LastSummary with
        {
            Generations = currentGenerations,
            Reason = SimpleDdgiTransportCertificationReason.SourceCohortNoProgress,
            IsComplete = false
        };
        EnterRecovery(
            SimpleDdgiTransportPhase.FailClosedRecovery,
            SimpleDdgiTransportCertificationReason.SourceCohortNoProgress,
            SimpleDdgiTransportRecoveryAction.RebuildPrivateField,
            clearParticipantWitness: true,
            preserveSummary: true);
    }

    /// <summary>
    /// Fails closed when the complete source/solve/audit wave exceeds its
    /// computed end-to-end deadline even if individual phase/epoch counters
    /// continue changing. This catches bounded but non-converging audit loops,
    /// which a consecutive-no-progress detector intentionally cannot see.
    /// </summary>
    public void EnterConvergenceDeadlineRecovery(
        SimpleDdgiTransportGenerations currentGenerations)
    {
        FrozenGenerations = currentGenerations;
        LastSummary = LastSummary with
        {
            Generations = currentGenerations,
            Reason =
                SimpleDdgiTransportCertificationReason.ConvergenceDeadlineExceeded,
            IsComplete = false
        };
        EnterRecovery(
            SimpleDdgiTransportPhase.FailClosedRecovery,
            SimpleDdgiTransportCertificationReason.ConvergenceDeadlineExceeded,
            SimpleDdgiTransportRecoveryAction.RebuildPrivateField,
            clearParticipantWitness: true,
            preserveSummary: true);
    }

    public void ObserveProgressFrame(bool madeProgress)
    {
        _noProgressFrames = madeProgress
            ? 0
            : _noProgressFrames == int.MaxValue
                ? int.MaxValue
                : _noProgressFrames + 1;
    }

    public static int ResolveAuditReadbackDeadlineFrames(
        int probeCount,
        int chunkSize,
        int framesInFlight,
        int readbackMargin)
    {
        if (probeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(probeCount));
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (framesInFlight < 0)
            throw new ArgumentOutOfRangeException(nameof(framesInFlight));
        if (readbackMargin < 0)
            throw new ArgumentOutOfRangeException(nameof(readbackMargin));

        long chunks = Math.Max(1L, (probeCount + (long)chunkSize - 1L) / chunkSize);
        return checked((int)Math.Min(
            int.MaxValue,
            chunks + framesInFlight + (long)readbackMargin));
    }

    public static int ResolveConvergenceDeadlineFrames(
        int sourceSweepFrames,
        int participantCount,
        int solveProbeBudgetPerFrame,
        int acceleratedSweepCount,
        int auditDeadlineFrames,
        int schedulingMarginFrames)
    {
        if (sourceSweepFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSweepFrames));
        if (participantCount < 0)
            throw new ArgumentOutOfRangeException(nameof(participantCount));
        if (solveProbeBudgetPerFrame <= 0)
            throw new ArgumentOutOfRangeException(nameof(solveProbeBudgetPerFrame));
        if (acceleratedSweepCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(acceleratedSweepCount));
        if (auditDeadlineFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(auditDeadlineFrames));
        if (schedulingMarginFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(schedulingMarginFrames));

        long solveEpochFrames = Math.Max(
            1L,
            (participantCount + (long)solveProbeBudgetPerFrame - 1L) /
                solveProbeBudgetPerFrame);
        solveEpochFrames = checked(solveEpochFrames * acceleratedSweepCount);
        long deadline = checked(
            (long)sourceSweepFrames + solveEpochFrames +
            auditDeadlineFrames + schedulingMarginFrames);
        return checked((int)Math.Min(int.MaxValue, Math.Max(1L, deadline)));
    }

    public void EnterTracking()
    {
        Phase = SimpleDdgiTransportPhase.Tracking;
        LastReason = SimpleDdgiTransportCertificationReason.Tracking;
        LastSummary = LastSummary with { Reason = LastReason };
    }

    /// <summary>
    /// Invalidates the field from any phase.  The caller supplies the new
    /// generation snapshot so the next source repair cannot accidentally reuse
    /// a certificate from an older physical ownership mapping.
    /// </summary>
    public void Invalidate(
        SimpleDdgiTransportGenerations generations,
        SimpleDdgiTransportCertificationReason reason,
        bool requireSourceRepair)
    {
        FrozenGenerations = generations;
        _expectedParticipantCount = 0;
        _visitedParticipantCount = 0;
        _auditCancelled = Phase == SimpleDdgiTransportPhase.AuditFrozen;
        _completedAuditPending = false;
        RecoveryAction = requireSourceRepair
            ? SimpleDdgiTransportRecoveryAction.RepairSourceCache
            : SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch;
        Phase = requireSourceRepair
            ? SimpleDdgiTransportPhase.SourceRepair
            : SimpleDdgiTransportPhase.AcceleratedSolve;
        LastReason = reason;
        LastSummary = LastSummary with
        {
            Generations = generations,
            Reason = reason,
            IsComplete = false
        };
    }

    public static int ResolveLogicalParity(
        int localProbeIndex,
        int gridCountX,
        int gridCountY,
        int gridCountZ,
        int physicalOffsetX,
        int physicalOffsetY,
        int physicalOffsetZ)
    {
        ResolveLogicalCoordinate(
            localProbeIndex,
            gridCountX,
            gridCountY,
            gridCountZ,
            physicalOffsetX,
            physicalOffsetY,
            physicalOffsetZ,
            out int logicalX,
            out int logicalY,
            out int logicalZ);
        return (logicalX + logicalY + logicalZ) & 1;
    }

    /// <summary>
    /// Mirrors the shader's toroidal ownership mapping.  Parity must be based
    /// on this logical coordinate, never on a physical array index.
    /// </summary>
    public static void ResolveLogicalCoordinate(
        int localProbeIndex,
        int gridCountX,
        int gridCountY,
        int gridCountZ,
        int physicalOffsetX,
        int physicalOffsetY,
        int physicalOffsetZ,
        out int logicalX,
        out int logicalY,
        out int logicalZ)
    {
        if (gridCountX <= 0 || gridCountY <= 0 || gridCountZ <= 0)
            throw new ArgumentOutOfRangeException("Grid counts must be positive.");
        int layerSize = checked(gridCountX * gridCountY);
        int physicalZ = localProbeIndex / layerSize;
        int remainder = localProbeIndex - physicalZ * layerSize;
        int physicalY = remainder / gridCountX;
        int physicalX = remainder - physicalY * gridCountX;
        if ((uint)physicalZ >= (uint)gridCountZ)
            throw new ArgumentOutOfRangeException(nameof(localProbeIndex));

        logicalX = PositiveModulo(physicalX - physicalOffsetX, gridCountX);
        logicalY = PositiveModulo(physicalY - physicalOffsetY, gridCountY);
        logicalZ = PositiveModulo(physicalZ - physicalOffsetZ, gridCountZ);
    }

    public static int ResolveColor(int localProbeIndex, int gridCountX, int gridCountY, int gridCountZ,
        int physicalOffsetX, int physicalOffsetY, int physicalOffsetZ, int startingColor)
    {
        int parity = ResolveLogicalParity(
            localProbeIndex, gridCountX, gridCountY, gridCountZ,
            physicalOffsetX, physicalOffsetY, physicalOffsetZ);
        return (parity ^ (startingColor & 1)) & 1;
    }

    /// <summary>
    /// Orders volumes from the coarsest/farthest operator to the finest/near
    /// operator.  Lower fallback priority wins before the stable volume index
    /// tie-breaker, making dispatch order reproducible across frames.
    /// </summary>
    public static void OrderVolumes(
        ReadOnlySpan<SimpleDdgiTransportVolumeOrderKey> keys,
        Span<int> orderedVolumeIndices)
    {
        if (orderedVolumeIndices.Length < keys.Length)
            throw new ArgumentException("The destination span is too small.", nameof(orderedVolumeIndices));

        for (int i = 0; i < keys.Length; i++)
        {
            int candidate = keys[i].VolumeIndex;
            int insertAt = i;
            while (insertAt > 0 && Compare(keys[FindKeyIndex(keys, orderedVolumeIndices[insertAt - 1])], keys[i]) > 0)
            {
                orderedVolumeIndices[insertAt] = orderedVolumeIndices[insertAt - 1];
                insertAt--;
            }
            orderedVolumeIndices[insertAt] = candidate;
        }
    }

    public static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    public void EnsureParticipantCapacity(int required)
    {
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= _participantVisitEpoch.Length)
            return;
        Array.Resize(ref _participantVisitEpoch, required);
    }

    private void AdvanceSolveEpoch(SimpleDdgiTransportGenerations generations)
    {
        uint previousSolveEpoch = _solveEpoch;
        _solveEpoch = NextNonZero(_solveEpoch);
        if (_solveEpoch <= previousSolveEpoch)
        {
            // Visit stamps are meaningful only within the current 32-bit
            // epoch namespace. Clear the bounded table on wrap so a visit
            // from an old epoch 1 cannot authorize the new epoch 1.
            Array.Clear(_participantVisitEpoch);
        }

        _visitedParticipantCount = 0;
        _auditCancelled = false;
        FrozenGenerations = generations with
        {
            Solve = _solveEpoch,
            Audit = NonZeroGeneration(_auditEpoch)
        };
        Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
    }

    private void EnterRecovery(
        SimpleDdgiTransportPhase phase,
        SimpleDdgiTransportCertificationReason reason,
        SimpleDdgiTransportRecoveryAction action,
        bool clearParticipantWitness,
        bool preserveSummary = false)
    {
        if (clearParticipantWitness)
        {
            _expectedParticipantCount = 0;
            _visitedParticipantCount = 0;
        }
        _auditCancelled = true;
        _completedAuditPending = false;
        Phase = phase;
        LastReason = reason;
        RecoveryAction = action;
        _recoveryGeneration = NextNonZero(_recoveryGeneration);
        _recoveryCount = SaturatingIncrement(_recoveryCount);
        if (!preserveSummary)
            LastSummary = LastSummary with { Reason = reason, IsComplete = false };
    }

    private void RememberRejectedTuple(SimpleDdgiTransportAuditTuple tuple)
    {
        _lastRejectedAuditTuple = tuple;
        _hasLastRejectedAuditTuple = true;
    }

    private static SimpleDdgiTransportAuditTuple CreateAuditTuple(
        SimpleDdgiTransportGenerations generations,
        int participantCount) => new(
        generations.VolumeTable,
        generations.PhysicalOwnership,
        generations.SourceLighting,
        generations.SourceEpoch,
        generations.TransportOperator,
        generations.CanonicalField,
        generations.Solve,
        generations.SchedulerResources,
        Math.Max(0, participantCount));

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? ulong.MaxValue : value + 1UL;

    private static uint NextNonZero(uint value)
    {
        value++;
        return value == 0u ? 1u : value;
    }

    private static int Compare(SimpleDdgiTransportVolumeOrderKey left, SimpleDdgiTransportVolumeOrderKey right)
    {
        int comparison = right.Spacing.CompareTo(left.Spacing);
        if (comparison != 0)
            return comparison;
        comparison = left.FallbackPriority.CompareTo(right.FallbackPriority);
        if (comparison != 0)
            return comparison;
        comparison = left.OuterPriority.CompareTo(right.OuterPriority);
        if (comparison != 0)
            return comparison;
        return left.VolumeIndex.CompareTo(right.VolumeIndex);
    }

    private static int FindKeyIndex(ReadOnlySpan<SimpleDdgiTransportVolumeOrderKey> keys, int volumeIndex)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].VolumeIndex == volumeIndex)
                return i;
        }
        throw new InvalidOperationException("Volume order contains an unknown volume index.");
    }

    private static bool IsSolveGenerationCompatible(
        SimpleDdgiTransportGenerations current,
        SimpleDdgiTransportGenerations frozen) =>
        current.VolumeTable == frozen.VolumeTable &&
        current.PhysicalOwnership == frozen.PhysicalOwnership &&
        current.SourceLighting == frozen.SourceLighting &&
        current.SourceEpoch == frozen.SourceEpoch &&
        current.TransportOperator == frozen.TransportOperator &&
        current.Solve == frozen.Solve &&
        current.SchedulerResources == frozen.SchedulerResources;

    private static uint NonZeroGeneration(uint value) => value == 0u ? 1u : value;
}

public readonly record struct SimpleDdgiTransportAuditTuple(
    uint VolumeTable,
    uint PhysicalOwnership,
    uint SourceLighting,
    uint SourceEpoch,
    uint TransportOperator,
    uint CanonicalField,
    uint SolveEpoch,
    uint SchedulerResources,
    int ParticipantCount);

public readonly record struct SimpleDdgiTransportVolumeOrderKey(
    int VolumeIndex,
    float Spacing,
    int FallbackPriority = 0,
    int OuterPriority = 0);

public readonly record struct SimpleDdgiTransportSolveParticipant(
    int ParticipantIndex,
    int VolumeIndex,
    int LocalProbeIndex,
    int GridCountX,
    int GridCountY,
    int GridCountZ,
    int PhysicalOffsetX,
    int PhysicalOffsetY,
    int PhysicalOffsetZ)
{
    public int LogicalParity => SimpleDdgiTransportSolveController.ResolveLogicalParity(
        LocalProbeIndex,
        GridCountX,
        GridCountY,
        GridCountZ,
        PhysicalOffsetX,
        PhysicalOffsetY,
        PhysicalOffsetZ);
}
