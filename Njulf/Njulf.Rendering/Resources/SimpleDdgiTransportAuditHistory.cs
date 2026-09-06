using System.Diagnostics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiTransportAuditTrigger : byte
{
    InitialSolve,
    ConvergenceRequest,
    LightingChange,
    VolumeOwnershipChange,
    SourceCacheInvalidation,
    SolverCalibration,
    SourceRepairFeedback,
    VolumeScroll,
    GenerationChange,
    AuditRecovery,
    Disabled,
    PeriodicSourceRefresh
}

public enum SimpleDdgiTransportAuditEventKind : byte
{
    None,
    Started,
    FirstSubmitted,
    DispatchComplete,
    Certified,
    Rejected,
    Cancelled,
    TimedOut
}

/// <summary>CPU-only evidence. Completion is observed readback consumption, not final dispatch.</summary>
public readonly record struct SimpleDdgiTransportAuditEvent
{
    public ulong Sequence { get; init; }
    public SimpleDdgiTransportAuditEventKind Kind { get; init; }
    public SimpleDdgiTransportAuditTrigger Trigger { get; init; }
    public SimpleDdgiTransportCertificationReason TriggerReason { get; init; }
    public SimpleDdgiSourceCacheInvalidationReason SourceInvalidationReason { get; init; }
    public SimpleDdgiTransportCertificationReason Reason { get; init; }
    public SimpleDdgiTransportGenerations FrozenGenerations { get; init; }
    public SimpleDdgiTransportGenerations CurrentGenerations { get; init; }
    public uint AdmissionVolumeTableGeneration { get; init; }
    public uint VolumeTableGeneration { get; init; }
    // Admission may precede this submission's scheduler serial. Do not use it
    // as a substitute for FirstSubmissionFrameSerial in certificate checks.
    public ulong AdmissionFrameSerial { get; init; }
    public ulong FrameSerial { get; init; }
    public ulong FirstSubmissionFrameSerial { get; init; }
    public ulong FinalSubmissionFrameSerial { get; init; }
    public long ElapsedMicroseconds { get; init; }
    public uint PlannedChunkCount { get; init; }
    public uint SubmittedChunkCount { get; init; }
    public uint ExpectedParticipantCount { get; init; }
}

/// <summary>
/// Bounded transition history, owned by the render thread.
/// Diagnostic history allocates only after a
/// transition and never changes a snapshot already handed to a consumer.
/// </summary>
internal sealed class SimpleDdgiTransportAuditHistory
{
    internal const int Capacity = 64;
    private readonly SimpleDdgiTransportAuditEvent[] _events = new SimpleDdgiTransportAuditEvent[Capacity];
    private IReadOnlyList<SimpleDdgiTransportAuditEvent> _snapshot = Array.Empty<SimpleDdgiTransportAuditEvent>();
    private ulong _snapshotSequence;
    private ulong _sequence;
    private long _admissionTimestamp;
    private bool _active;
    private ulong _firstSubmissionFrameSerial;
    private ulong _finalSubmissionFrameSerial;
    private uint _submittedChunkCount;
    private SimpleDdgiTransportAuditTrigger _trigger;
    private SimpleDdgiTransportCertificationReason _triggerReason = SimpleDdgiTransportCertificationReason.SourceRepairRequired;
    private SimpleDdgiSourceCacheInvalidationReason _sourceInvalidationReason;

    public SimpleDdgiTransportAuditEvent Current { get; private set; }
    public ulong DroppedEventCount => _sequence > Capacity ? _sequence - Capacity : 0UL;

    public void SetTrigger(SimpleDdgiTransportAuditTrigger trigger, SimpleDdgiTransportCertificationReason reason,
        SimpleDdgiSourceCacheInvalidationReason sourceInvalidationReason = SimpleDdgiSourceCacheInvalidationReason.None)
    {
        _trigger = trigger;
        _triggerReason = reason;
        _sourceInvalidationReason = sourceInvalidationReason;
    }

    public void Begin(SimpleDdgiTransportGenerations generations, uint volumeTableGeneration,
        ulong frameSerial, uint chunkCount, uint participantCount, long timestamp)
    {
        if (_active)
            throw new InvalidOperationException("An active audit must terminate before another starts.");
        _active = true;
        _admissionTimestamp = timestamp;
        _firstSubmissionFrameSerial = 0UL;
        _finalSubmissionFrameSerial = 0UL;
        _submittedChunkCount = 0u;
        Current = new SimpleDdgiTransportAuditEvent
        {
            Trigger = _trigger,
            TriggerReason = _triggerReason,
            SourceInvalidationReason = _sourceInvalidationReason,
            FrozenGenerations = generations,
            CurrentGenerations = generations,
            AdmissionVolumeTableGeneration = volumeTableGeneration,
            VolumeTableGeneration = volumeTableGeneration,
            AdmissionFrameSerial = frameSerial,
            PlannedChunkCount = chunkCount,
            ExpectedParticipantCount = participantCount
        };
        Record(SimpleDdgiTransportAuditEventKind.Started,
            SimpleDdgiTransportCertificationReason.AuditInProgress, frameSerial, timestamp);
    }

    public void SubmitChunk(ulong frameSerial, uint submittedChunkCount, bool final, long timestamp)
    {
        if (!_active)
            return;
        if (_firstSubmissionFrameSerial == 0UL)
            _firstSubmissionFrameSerial = frameSerial;
        _finalSubmissionFrameSerial = frameSerial;
        _submittedChunkCount = submittedChunkCount;
        if (final || submittedChunkCount == 1u)
            Record(final ? SimpleDdgiTransportAuditEventKind.DispatchComplete
                : SimpleDdgiTransportAuditEventKind.FirstSubmitted,
                SimpleDdgiTransportCertificationReason.AuditInProgress, frameSerial, timestamp);
    }

    public void Finish(SimpleDdgiTransportAuditEventKind kind, SimpleDdgiTransportCertificationReason reason,
        SimpleDdgiTransportGenerations currentGenerations, uint volumeTableGeneration,
        ulong frameSerial, long timestamp)
    {
        if (!_active)
            return;
        Current = Current with { CurrentGenerations = currentGenerations, VolumeTableGeneration = volumeTableGeneration };
        Record(kind, reason, frameSerial, timestamp);
        _active = false;
        if (kind != SimpleDdgiTransportAuditEventKind.Certified)
            SetTrigger(SimpleDdgiTransportAuditTrigger.AuditRecovery, reason);
    }

    public IReadOnlyList<SimpleDdgiTransportAuditEvent> Snapshot()
    {
        if (_snapshotSequence == _sequence)
            return _snapshot;
        int count = (int)Math.Min((ulong)Capacity, _sequence);
        var events = new SimpleDdgiTransportAuditEvent[count];
        ulong first = _sequence - (ulong)count;
        for (int i = 0; i < count; i++)
            events[i] = _events[(int)((first + (ulong)i) % Capacity)];
        _snapshot = Array.AsReadOnly(events);
        _snapshotSequence = _sequence;
        return _snapshot;
    }

    private void Record(SimpleDdgiTransportAuditEventKind kind, SimpleDdgiTransportCertificationReason reason,
        ulong frameSerial, long timestamp)
    {
        Current = Current with
        {
            Sequence = ++_sequence,
            Kind = kind,
            Reason = reason,
            FrameSerial = frameSerial,
            FirstSubmissionFrameSerial = _firstSubmissionFrameSerial,
            FinalSubmissionFrameSerial = _finalSubmissionFrameSerial,
            SubmittedChunkCount = _submittedChunkCount,
            ElapsedMicroseconds = (long)Stopwatch.GetElapsedTime(_admissionTimestamp, timestamp).TotalMicroseconds
        };
        _events[(int)((_sequence - 1UL) % Capacity)] = Current;
    }
}
