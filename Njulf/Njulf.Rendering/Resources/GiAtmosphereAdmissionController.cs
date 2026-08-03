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
    SourceContractChanged
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
    float AchievableSourceSweepSeconds = 0.0f);

public readonly record struct GiAtmosphereAdmissionInput(
    ulong CandidateSignature,
    in GiAtmosphereCohortFeedback Cohort,
    bool HardInvalidation = false);

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
        if (cohort.SourceCohortActive || cohort.StaleParticipatingProbeCount > 0)
            return Decision(newRequest ? GiAtmosphereAdmissionAction.ReplacePendingCandidate : GiAtmosphereAdmissionAction.Hold,
                GiAtmosphereAdmissionReason.SourceCohortRefreshing, input);
        if (!cohort.VisiblePublicationBoundaryComplete || !cohort.MinimumPropagationBoundaryComplete)
            return Decision(GiAtmosphereAdmissionAction.Hold, GiAtmosphereAdmissionReason.PublicationBoundaryPending, input);

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

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? ulong.MaxValue : value + 1UL;
}
