using System;

namespace Njulf.Rendering.Resources;

public enum GiAtmosphereAdmissionAction
{
    Hold,
    ReplacePendingCandidate,
    AdmitPendingCandidate,
    HardRestartWithCandidate
}

public enum GiAtmosphereAdmissionReason
{
    None,
    CandidateUnchanged,
    Bootstrap,
    ConsumerInactive,
    NoParticipatingProbes,
    SourceCohortRefreshing,
    PublicationBoundaryPending,
    CohortReleased,
    SourceContractChanged,
    FeedbackGenerationMismatch,
    QuietPeriodPending
}

/// <summary>
/// Incremental DDGI feedback consumed by the atmosphere admission boundary.  It deliberately
/// contains only scalar state already maintained by the scheduler, so deciding whether to admit
/// a candidate is O(1) in probe count and cannot trigger a GPU readback.
/// </summary>
public readonly record struct GiAtmosphereCohortFeedback(
    bool ConsumesSteppedAtmosphere,
    int ParticipatingProbeCount,
    bool SourceCohortActive,
    int StaleParticipatingProbeCount,
    bool VisiblePublicationBoundaryComplete = true,
    bool MinimumPropagationBoundaryComplete = true,
    float AchievableSourceSweepSeconds = 0.0f,
    uint VolumeResourceGeneration = 0U,
    uint SourceCohortGeneration = 0U,
    uint AdmittedSourceCohortGeneration = 0U,
    uint PropagationGeneration = 0U,
    uint PublishedPropagationGeneration = 0U,
    int VisiblePriorityParticipatingProbeCount = 0,
    int VisiblePrioritySourceReadyProbeCount = 0,
    int VisiblePriorityPublishedProbeCount = 0,
    bool QuietPeriodComplete = true,
    bool CandidateStreamActive = false,
    uint SourceCohortStartFrame = 0U,
    uint SourceCohortCompletionFrame = 0U,
    ulong SourceCohortStartCount = 0UL,
    ulong SourceCohortCompletionCount = 0UL,
    int TargetSourceProbeCount = 0,
    int AdmittedSourceProbeCount = 0,
    int ScheduledSourceProbeCount = 0,
    ulong TargetSourceRayCount = 0UL,
    ulong AdmittedSourceRayCount = 0UL,
    ulong ScheduledSourceRayCount = 0UL,
    int SourceCapacityShortfall = 0,
    ulong SourceRayCapacityShortfall = 0UL,
    uint StaticConvergedGeneration = 0U,
    bool StaticConvergencePending = false,
    ulong StaleReadbackRejectionCount = 0UL,
    ulong ResourceGenerationRejectionCount = 0UL,
    bool ResidencyFeedbackComplete = true,
    uint ResidencyEventSourceGeneration = 0U,
    uint ResidencyEventCohortGeneration = 0U,
    int ResidencyAdmissionProbeCount = 0,
    int ResidencyEvictionProbeCount = 0,
    int ResidencyOtherGenerationEvictionProbeCount = 0);

public readonly record struct GiAtmosphereAdmissionInput(
    ulong CandidateSignature,
    in GiAtmosphereCohortFeedback Cohort,
    bool HardInvalidation = false,
    uint CurrentVolumeResourceGeneration = 0U,
    uint CurrentSourceCohortGeneration = 0U,
    uint CurrentPropagationGeneration = 0U);

public readonly record struct GiAtmosphereAdmissionDecision(
    GiAtmosphereAdmissionAction Action,
    GiAtmosphereAdmissionReason Reason,
    ulong CandidateSignature,
    ulong AdmittedSignature,
    uint AdmittedGeneration,
    bool HasPendingCandidate,
    ulong RequestedCount,
    ulong CoalescedCount,
    ulong AdmittedCount,
    float PredictedLagSeconds);

/// <summary>
/// Allocation-free latest-wins admission state for procedural-atmosphere DDGI cohorts.
/// Coefficients remain in environment-owned preallocated frames; this type owns identifiers only.
/// </summary>
public struct GiAtmosphereAdmissionController
{
    private ulong _requestedSignature;
    private ulong _admittedSignature;
    private ulong _pendingSignature;
    private uint _admittedGeneration;
    private uint _requestedGeneration;
    private ulong _requestedCount;
    private ulong _coalescedCount;
    private ulong _admittedCount;
    private bool _hasPending;

    public readonly ulong RequestedSignature => _requestedSignature;
    public readonly ulong AdmittedSignature => _admittedSignature;
    public readonly ulong PendingSignature => _pendingSignature;
    public readonly uint AdmittedGeneration => _admittedGeneration;
    public readonly uint RequestedGeneration => _requestedGeneration;
    public readonly ulong RequestedCount => _requestedCount;
    public readonly ulong CoalescedCount => _coalescedCount;
    public readonly ulong AdmittedCount => _admittedCount;
    public readonly bool HasPendingCandidate => _hasPending;

    public GiAtmosphereAdmissionDecision Update(in GiAtmosphereAdmissionInput input)
    {
        if (input.CandidateSignature == 0UL)
            throw new ArgumentOutOfRangeException(nameof(input), "A GI atmosphere signature must be nonzero.");

        bool newRequest = input.CandidateSignature != _requestedSignature;
        if (newRequest)
        {
            _requestedSignature = input.CandidateSignature;
            _requestedGeneration = _requestedGeneration == uint.MaxValue ? 1u : _requestedGeneration + 1u;
            _requestedCount = SaturatingIncrement(_requestedCount);
            if (_hasPending && _pendingSignature != input.CandidateSignature)
                _coalescedCount = SaturatingIncrement(_coalescedCount);
            _pendingSignature = input.CandidateSignature;
            _hasPending = _pendingSignature != _admittedSignature;
        }

        if (_admittedGeneration == 0u)
            return Admit(GiAtmosphereAdmissionAction.AdmitPendingCandidate, GiAtmosphereAdmissionReason.Bootstrap, input);

        if (input.HardInvalidation)
            return Admit(GiAtmosphereAdmissionAction.HardRestartWithCandidate, GiAtmosphereAdmissionReason.SourceContractChanged, input);

        if (!_hasPending)
            return Decision(GiAtmosphereAdmissionAction.Hold, GiAtmosphereAdmissionReason.CandidateUnchanged, input);

        GiAtmosphereCohortFeedback cohort = input.Cohort;
        if (!cohort.ConsumesSteppedAtmosphere)
            return Admit(GiAtmosphereAdmissionAction.AdmitPendingCandidate, GiAtmosphereAdmissionReason.ConsumerInactive, input);
        if (cohort.ParticipatingProbeCount <= 0)
            return Admit(GiAtmosphereAdmissionAction.AdmitPendingCandidate, GiAtmosphereAdmissionReason.NoParticipatingProbes, input);
        if (HasGenerationMismatch(input))
        {
            return Decision(
                newRequest
                    ? GiAtmosphereAdmissionAction.ReplacePendingCandidate
                    : GiAtmosphereAdmissionAction.Hold,
                GiAtmosphereAdmissionReason.FeedbackGenerationMismatch,
                input);
        }
        if (cohort.SourceCohortActive || cohort.StaleParticipatingProbeCount > 0)
            return Decision(newRequest ? GiAtmosphereAdmissionAction.ReplacePendingCandidate : GiAtmosphereAdmissionAction.Hold,
                GiAtmosphereAdmissionReason.SourceCohortRefreshing, input);
        int visibleParticipants = cohort.VisiblePriorityParticipatingProbeCount;
        bool hasExplicitVisibleCounters = visibleParticipants > 0 ||
            cohort.VisiblePrioritySourceReadyProbeCount > 0 ||
            cohort.VisiblePriorityPublishedProbeCount > 0;
        if (hasExplicitVisibleCounters && visibleParticipants > 0 &&
            cohort.VisiblePrioritySourceReadyProbeCount < visibleParticipants)
        {
            return Decision(GiAtmosphereAdmissionAction.Hold,
                GiAtmosphereAdmissionReason.PublicationBoundaryPending, input);
        }
        if (hasExplicitVisibleCounters && visibleParticipants > 0 &&
            cohort.VisiblePriorityPublishedProbeCount < visibleParticipants)
        {
            return Decision(GiAtmosphereAdmissionAction.Hold,
                GiAtmosphereAdmissionReason.PublicationBoundaryPending, input);
        }
        if (!cohort.VisiblePublicationBoundaryComplete || !cohort.MinimumPropagationBoundaryComplete)
            return Decision(GiAtmosphereAdmissionAction.Hold, GiAtmosphereAdmissionReason.PublicationBoundaryPending, input);
        if (!cohort.QuietPeriodComplete || cohort.CandidateStreamActive)
            return Decision(GiAtmosphereAdmissionAction.Hold, GiAtmosphereAdmissionReason.QuietPeriodPending, input);

        return Admit(GiAtmosphereAdmissionAction.AdmitPendingCandidate, GiAtmosphereAdmissionReason.CohortReleased, input);
    }

    private GiAtmosphereAdmissionDecision Admit(
        GiAtmosphereAdmissionAction action,
        GiAtmosphereAdmissionReason reason,
        in GiAtmosphereAdmissionInput input)
    {
        _admittedSignature = _pendingSignature != 0UL ? _pendingSignature : input.CandidateSignature;
        _pendingSignature = 0UL;
        _hasPending = false;
        _admittedGeneration = _admittedGeneration == uint.MaxValue ? 1u : _admittedGeneration + 1u;
        _admittedCount = SaturatingIncrement(_admittedCount);
        return Decision(action, reason, input);
    }

    private readonly GiAtmosphereAdmissionDecision Decision(
        GiAtmosphereAdmissionAction action,
        GiAtmosphereAdmissionReason reason,
        in GiAtmosphereAdmissionInput input) =>
        new(action, reason, input.CandidateSignature, _admittedSignature, _admittedGeneration,
            _hasPending, _requestedCount, _coalescedCount, _admittedCount,
            MathF.Max(input.Cohort.AchievableSourceSweepSeconds, 0.0f));

    private static bool HasGenerationMismatch(in GiAtmosphereAdmissionInput input)
    {
        GiAtmosphereCohortFeedback cohort = input.Cohort;
        if (!cohort.ResidencyFeedbackComplete)
            return true;
        bool hasResidencyEvent = cohort.ResidencyAdmissionProbeCount > 0 ||
            cohort.ResidencyEvictionProbeCount > 0 ||
            cohort.ResidencyOtherGenerationEvictionProbeCount > 0;
        if (hasResidencyEvent &&
            input.CurrentSourceCohortGeneration != 0U &&
            (cohort.ResidencyEventSourceGeneration !=
                input.CurrentSourceCohortGeneration ||
             cohort.ResidencyEventCohortGeneration !=
                input.CurrentSourceCohortGeneration))
        {
            return true;
        }
        if (cohort.ResidencyOtherGenerationEvictionProbeCount > 0)
            return true;
        if (input.CurrentVolumeResourceGeneration != 0U &&
            cohort.VolumeResourceGeneration != 0U &&
            cohort.VolumeResourceGeneration != input.CurrentVolumeResourceGeneration)
            return true;
        if (input.CurrentSourceCohortGeneration != 0U &&
            cohort.SourceCohortGeneration != 0U &&
            cohort.SourceCohortGeneration != input.CurrentSourceCohortGeneration)
            return true;
        if (cohort.SourceCohortGeneration != 0U &&
            cohort.AdmittedSourceCohortGeneration != 0U &&
            cohort.SourceCohortGeneration != cohort.AdmittedSourceCohortGeneration)
            return true;
        if (input.CurrentPropagationGeneration != 0U &&
            cohort.PropagationGeneration != 0U &&
            cohort.PropagationGeneration != input.CurrentPropagationGeneration)
            return true;
        if (cohort.PropagationGeneration != 0U &&
            cohort.PublishedPropagationGeneration != 0U &&
            cohort.PublishedPropagationGeneration != cohort.PropagationGeneration)
            return true;
        return false;
    }

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? ulong.MaxValue : value + 1UL;
}
