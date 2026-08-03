using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Rendering.Resources;

[Flags]
public enum ReflectionCaptureReason : uint
{
    None = 0,
    InitialLoad = 1 << 0,
    EnvironmentChanged = 1 << 1,
    DdgiChanged = 1 << 2,
    SceneChanged = 1 << 3,
    MaterialChanged = 1 << 4,
    LightChanged = 1 << 5,
    ResourceChanged = 1 << 6,
    Manual = 1 << 7
}

public enum ReflectionProbeCaptureState
{
    Unregistered,
    Published,
    Queued,
    CapturingFaces,
    PrefilteringMips,
    CopyReady,
    AwaitingGpuCompletion,
    RetryPending,
    DeferredChangingScene,
    Failed
}

public enum ReflectionProbeWorkKind { None, CaptureFace, PrefilterMip, PublishCopy }

public readonly record struct ReflectionCaptureVersion(
    uint SceneRadianceRevision,
    uint LightRevision,
    uint AdmittedEnvironmentGeneration,
    uint CompletedDdgiGeneration,
    uint MaterialRevision,
    uint AccelerationStructureGeneration,
    uint ShaderSettingsRevision);

public readonly record struct ReflectionProbeCaptureSnapshot(
    Vector3 Position,
    Quaternion Rotation,
    ReflectionProbeShape Shape,
    Vector3 BoxExtents,
    float Radius);

public readonly record struct ReflectionProbeCaptureTicket(
    ulong Serial,
    Guid ProbeId,
    int Layer,
    uint ResourceGeneration,
    uint SceneRevision,
    ReflectionCaptureVersion Version,
    ReflectionCaptureReason Reasons,
    ReflectionProbeCaptureSnapshot Snapshot,
    int NextFace,
    int NextMip,
    ReflectionProbeCaptureState State);

public readonly record struct ReflectionProbeWork(
    ReflectionProbeWorkKind Kind,
    ReflectionProbeCaptureTicket Ticket,
    int Face,
    int Mip);

/// <summary>
/// Vulkan-independent, fixed-capacity state machine. Each stable published layer is also the
/// scheduler slot, avoiding per-frame maps, sorting, and allocation.
/// </summary>
public sealed class ReflectionProbeCaptureScheduler
{
    private readonly Entry[] _entries;
    private readonly int[] _queue;
    private int _queueHead;
    private int _queueTail;
    private int _queueCount;
    private int _activeLayer = -1;
    private ulong _nextSerial = 1UL;
    private ulong _startedTotal;
    private ulong _publishedTotal;
    private ulong _failedTotal;

    public ReflectionProbeCaptureScheduler(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = new Entry[capacity];
        _queue = new int[capacity];
    }

    public int Capacity => _entries.Length;
    public int QueueDepth => _queueCount;
    public int ActiveTicketCount => _activeLayer >= 0 ? 1 : 0;
    public ulong CapturesStartedTotal => _startedTotal;
    public ulong CapturesPublishedTotal => _publishedTotal;
    public ulong CapturesFailedTotal => _failedTotal;

    public void Register(int layer, Guid probeId, bool hasPublishedCapture, ReflectionCaptureVersion publishedVersion = default)
    {
        ValidateLayer(layer);
        if (probeId == Guid.Empty)
            throw new ArgumentException("A probe ID must be non-empty.", nameof(probeId));
        ref Entry entry = ref _entries[layer];
        if (entry.Registered && entry.ProbeId == probeId)
            return;
        bool queued = entry.Queued;
        if (_activeLayer == layer)
        {
            _activeLayer = -1;
            queued = false;
        }
        entry = new Entry
        {
            Registered = true,
            ProbeId = probeId,
            HasPublished = hasPublishedCapture,
            PublishedVersion = publishedVersion,
            Queued = queued,
            State = queued
                ? ReflectionProbeCaptureState.Queued
                : hasPublishedCapture ? ReflectionProbeCaptureState.Published : ReflectionProbeCaptureState.Unregistered
        };
    }

    public void Unregister(int layer, Guid probeId)
    {
        ValidateLayer(layer);
        ref Entry entry = ref _entries[layer];
        if (!entry.Registered || entry.ProbeId != probeId)
            return;
        bool queued = entry.Queued;
        if (_activeLayer == layer)
        {
            _activeLayer = -1;
            queued = false;
        }
        entry = new Entry { Queued = queued };
        // Stale ring entries are discarded lazily by DequeueNext, keeping removal O(1).
    }

    public void Request(
        int layer,
        Guid probeId,
        in ReflectionCaptureVersion version,
        ReflectionCaptureReason reasons,
        in ReflectionProbeCaptureSnapshot snapshot,
        uint resourceGeneration,
        uint sceneRevision)
    {
        ValidateLayer(layer);
        ref Entry entry = ref _entries[layer];
        if (!entry.Registered || entry.ProbeId != probeId)
            Register(layer, probeId, hasPublishedCapture: false);
        entry.RequestedVersion = version;
        entry.RequestedReasons |= reasons;
        entry.RequestedSnapshot = snapshot;
        entry.RequestedResourceGeneration = resourceGeneration;
        entry.RequestedSceneRevision = sceneRevision;
        entry.HasPendingRequest = true;

        if (_activeLayer == layer && entry.State < ReflectionProbeCaptureState.CopyReady)
            entry.SupersededBeforeCommit = true;
        if (_activeLayer != layer)
            Enqueue(layer, ref entry);
    }

    public bool TryAcquireWork(int mipCount, int maxFaces, int maxMips, out ReflectionProbeWork work)
    {
        if (mipCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(mipCount));
        if (_activeLayer < 0 && !TryStartNext(out _activeLayer))
        {
            work = default;
            return false;
        }

        ref Entry entry = ref _entries[_activeLayer];
        if (entry.SupersededBeforeCommit)
        {
            RestartFromLatest(ref entry);
        }

        ReflectionProbeCaptureTicket ticket = CreateTicket(_activeLayer, entry);
        if (entry.NextFace < 6 && maxFaces > 0)
        {
            entry.State = ReflectionProbeCaptureState.CapturingFaces;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.CaptureFace, ticket, entry.NextFace, 0);
            return true;
        }
        if (entry.NextMip < mipCount && maxMips > 0)
        {
            entry.State = ReflectionProbeCaptureState.PrefilteringMips;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.PrefilterMip, ticket, -1, entry.NextMip);
            return true;
        }
        if (entry.NextFace >= 6 && entry.NextMip >= mipCount)
        {
            entry.State = ReflectionProbeCaptureState.CopyReady;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.PublishCopy, CreateTicket(_activeLayer, entry), -1, -1);
            return true;
        }

        work = default;
        return false;
    }

    public void CompleteWork(in ReflectionProbeWork work)
    {
        ref Entry entry = ref ValidateActive(work.Ticket);
        switch (work.Kind)
        {
            case ReflectionProbeWorkKind.CaptureFace when work.Face == entry.NextFace:
                entry.NextFace++;
                if (entry.NextFace == 6)
                    entry.NextMip = 1;
                break;
            case ReflectionProbeWorkKind.PrefilterMip when entry.NextFace == 6 && work.Mip == entry.NextMip:
                entry.NextMip++;
                break;
            default:
                throw new InvalidOperationException("Reflection work completed out of order.");
        }
    }

    public void MarkCopySubmitted(in ReflectionProbeWork work, ulong completionValue)
    {
        if (work.Kind != ReflectionProbeWorkKind.PublishCopy || completionValue == 0UL)
            throw new ArgumentException("A publish copy requires a nonzero completion token.", nameof(work));
        ref Entry entry = ref ValidateActive(work.Ticket);
        entry.State = ReflectionProbeCaptureState.AwaitingGpuCompletion;
        entry.CompletionValue = completionValue;
        entry.CopyCommitted = true;
    }

    public bool TryPublishCompleted(ulong completedValue, out ReflectionProbeCaptureTicket ticket)
    {
        if (_activeLayer < 0)
        {
            ticket = default;
            return false;
        }
        ref Entry entry = ref _entries[_activeLayer];
        if (entry.State != ReflectionProbeCaptureState.AwaitingGpuCompletion || entry.CompletionValue > completedValue)
        {
            ticket = default;
            return false;
        }

        ticket = CreateTicket(_activeLayer, entry);
        entry.HasPublished = true;
        entry.PublishedVersion = entry.TicketVersion;
        entry.State = ReflectionProbeCaptureState.Published;
        entry.CopyCommitted = false;
        entry.CompletionValue = 0UL;
        _publishedTotal++;
        int completedLayer = _activeLayer;
        _activeLayer = -1;
        if (entry.HasPendingRequest && entry.RequestedVersion != entry.PublishedVersion)
            Enqueue(completedLayer, ref entry);
        return true;
    }

    public void FailActive(in ReflectionProbeCaptureTicket ticket, bool retry)
    {
        ref Entry entry = ref ValidateActive(ticket);
        _failedTotal++;
        int layer = _activeLayer;
        _activeLayer = -1;
        if (retry)
        {
            entry.HasPendingRequest = true;
            entry.RequestedVersion = entry.TicketVersion;
            entry.RequestedReasons |= entry.TicketReasons;
            entry.RequestedSnapshot = entry.TicketSnapshot;
            entry.RequestedResourceGeneration = entry.TicketResourceGeneration;
            entry.RequestedSceneRevision = entry.TicketSceneRevision;
            entry.State = ReflectionProbeCaptureState.RetryPending;
            Enqueue(layer, ref entry);
        }
        else
        {
            entry.State = ReflectionProbeCaptureState.Failed;
        }
    }

    public bool HasPublishedCapture(int layer, Guid probeId)
    {
        ValidateLayer(layer);
        ref Entry entry = ref _entries[layer];
        return entry.Registered && entry.ProbeId == probeId && entry.HasPublished;
    }

    private bool TryStartNext(out int layer)
    {
        while (_queueCount > 0)
        {
            layer = _queue[_queueHead];
            _queueHead = (_queueHead + 1) % _queue.Length;
            _queueCount--;
            ref Entry entry = ref _entries[layer];
            entry.Queued = false;
            if (!entry.Registered || !entry.HasPendingRequest)
                continue;
            entry.Serial = NextSerial();
            entry.TicketVersion = entry.RequestedVersion;
            entry.TicketReasons = entry.RequestedReasons;
            entry.TicketSnapshot = entry.RequestedSnapshot;
            entry.TicketResourceGeneration = entry.RequestedResourceGeneration;
            entry.TicketSceneRevision = entry.RequestedSceneRevision;
            entry.RequestedReasons = ReflectionCaptureReason.None;
            entry.HasPendingRequest = false;
            entry.NextFace = 0;
            entry.NextMip = 1;
            entry.State = ReflectionProbeCaptureState.CapturingFaces;
            entry.SupersededBeforeCommit = false;
            _startedTotal++;
            return true;
        }
        layer = -1;
        return false;
    }

    private void RestartFromLatest(ref Entry entry)
    {
        entry.Serial = NextSerial();
        entry.TicketVersion = entry.RequestedVersion;
        entry.TicketReasons |= entry.RequestedReasons;
        entry.TicketSnapshot = entry.RequestedSnapshot;
        entry.TicketResourceGeneration = entry.RequestedResourceGeneration;
        entry.TicketSceneRevision = entry.RequestedSceneRevision;
        entry.RequestedReasons = ReflectionCaptureReason.None;
        entry.HasPendingRequest = false;
        entry.NextFace = 0;
        entry.NextMip = 1;
        entry.State = ReflectionProbeCaptureState.CapturingFaces;
        entry.SupersededBeforeCommit = false;
        _startedTotal++;
    }

    private void Enqueue(int layer, ref Entry entry)
    {
        if (entry.Queued)
            return;
        if (_queueCount == _queue.Length)
            throw new InvalidOperationException("Reflection capture queue capacity was exceeded.");
        _queue[_queueTail] = layer;
        _queueTail = (_queueTail + 1) % _queue.Length;
        _queueCount++;
        entry.Queued = true;
        entry.State = ReflectionProbeCaptureState.Queued;
    }

    private ref Entry ValidateActive(in ReflectionProbeCaptureTicket ticket)
    {
        if (_activeLayer < 0 || ticket.Layer != _activeLayer)
            throw new InvalidOperationException("The reflection capture ticket is no longer active.");
        ref Entry entry = ref _entries[_activeLayer];
        if (entry.Serial != ticket.Serial || entry.ProbeId != ticket.ProbeId)
            throw new InvalidOperationException("The reflection capture ticket is stale.");
        return ref entry;
    }

    private static ReflectionProbeCaptureTicket CreateTicket(int layer, in Entry entry) =>
        new(entry.Serial, entry.ProbeId, layer, entry.TicketResourceGeneration,
            entry.TicketSceneRevision, entry.TicketVersion, entry.TicketReasons,
            entry.TicketSnapshot, entry.NextFace, entry.NextMip, entry.State);

    private ulong NextSerial()
    {
        ulong serial = _nextSerial++;
        if (serial == 0UL)
            serial = _nextSerial++;
        return serial;
    }

    private void ValidateLayer(int layer)
    {
        if ((uint)layer >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(layer));
    }

    private struct Entry
    {
        public bool Registered, HasPublished, HasPendingRequest, Queued, SupersededBeforeCommit, CopyCommitted;
        public Guid ProbeId;
        public ReflectionProbeCaptureState State;
        public ReflectionCaptureVersion PublishedVersion, RequestedVersion, TicketVersion;
        public ReflectionCaptureReason RequestedReasons, TicketReasons;
        public ReflectionProbeCaptureSnapshot RequestedSnapshot, TicketSnapshot;
        public uint RequestedResourceGeneration, RequestedSceneRevision, TicketResourceGeneration, TicketSceneRevision;
        public ulong Serial, CompletionValue;
        public int NextFace, NextMip;
    }
}
