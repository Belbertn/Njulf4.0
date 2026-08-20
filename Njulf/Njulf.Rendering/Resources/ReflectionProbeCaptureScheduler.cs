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
    private ulong _queueCapacityRejections;
    private ulong _staleCompletionRejections;
    private ulong _retryExhaustedTotal;
    private ulong _deferredChangingSceneTotal;
    private int _completionCount;
    private int _retryLimit;

    public ReflectionProbeCaptureScheduler(int capacity, int retryLimit = 3)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (retryLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(retryLimit));
        _entries = new Entry[capacity];
        _queue = new int[capacity];
        _retryLimit = retryLimit;
    }

    public int Capacity => _entries.Length;
    public int QueueDepth => _queueCount;
    public int ActiveTicketCount => _activeLayer >= 0 ? 1 : 0;
    /// <summary>Exclusive count of tickets still recording work.</summary>
    public int ActiveWorkCount =>
        _activeLayer >= 0 && _entries[_activeLayer].CompletionValue == 0UL
            ? 1
            : 0;
    public ReflectionProbeCaptureState CurrentState
    {
        get
        {
            if (_activeLayer >= 0)
                return _entries[_activeLayer].State;
            if (_queueCount <= 0)
                return ReflectionProbeCaptureState.Unregistered;

            ReflectionProbeCaptureState queuedState =
                _entries[_queue[_queueHead]].State;
            return queuedState == ReflectionProbeCaptureState.Unregistered
                ? ReflectionProbeCaptureState.Queued
                : queuedState;
        }
    }
    public ulong CapturesStartedTotal => _startedTotal;
    public ulong CapturesPublishedTotal => _publishedTotal;
    public ulong CapturesFailedTotal => _failedTotal;
    public ulong QueueCapacityRejections => _queueCapacityRejections;
    public ulong StaleCompletionRejections => _staleCompletionRejections;
    public ulong RetryExhaustedTotal => _retryExhaustedTotal;
    public ulong DeferredChangingSceneTotal => _deferredChangingSceneTotal;
    public int RetainedCompletionCount => _completionCount;

    public int RetryLimit
    {
        get => _retryLimit;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _retryLimit = value;
        }
    }

    public void Register(int layer, Guid probeId, bool hasPublishedCapture, ReflectionCaptureVersion publishedVersion = default)
    {
        ValidateLayer(layer);
        if (probeId == Guid.Empty)
            throw new ArgumentException("A probe ID must be non-empty.", nameof(probeId));
        ref Entry entry = ref _entries[layer];
        if (entry.Registered && entry.ProbeId == probeId)
            return;
        if (entry.CompletionValue != 0UL)
        {
            throw new InvalidOperationException(
                $"Reflection layer {layer} is still pinned by an in-flight GPU completion.");
        }
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
        if (entry.CompletionValue != 0UL)
        {
            // The published layer remains pinned until the renderer observes the copy's
            // completion.  It is deliberately not recycled or reset here.
            entry.Registered = false;
            entry.HasPendingRequest = false;
            entry.RequestedReasons = ReflectionCaptureReason.None;
            entry.State = ReflectionProbeCaptureState.AwaitingGpuCompletion;
            if (_activeLayer == layer)
                _activeLayer = -1;
            return;
        }
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
        bool versionChanged = !entry.HasPendingRequest || entry.RequestedVersion != version;
        entry.RequestedVersion = version;
        entry.RequestedReasons |= reasons;
        entry.RequestedSnapshot = snapshot;
        entry.RequestedResourceGeneration = resourceGeneration;
        entry.RequestedSceneRevision = sceneRevision;
        entry.HasPendingRequest = true;
        if (versionChanged)
        {
            entry.RetryCount = 0;
            entry.RetryAfterFrame = 0UL;
            entry.Deferred = false;
        }

        // A copy is the commit boundary.  Until it has a completion token, a newer
        // generation can still invalidate the entire private scratch transaction,
        // including a ticket that has reached CopyReady.  Restarting from the latest
        // request prevents a stale scene/resource snapshot from being copied into the
        // stable layer.  Once the copy is in flight, retain the old ticket and queue the
        // newer version behind its completion instead.
        if (_activeLayer == layer && entry.CompletionValue == 0UL &&
            entry.State <= ReflectionProbeCaptureState.CopyReady)
            entry.SupersededBeforeCommit = true;
        if (_activeLayer == layer && entry.CompletionValue != 0UL)
        {
            // A recapture request arriving while the previous copy is in flight is retained
            // behind the completion boundary; the published layer is never overwritten early.
        }
        else if (_activeLayer != layer)
            Enqueue(layer, ref entry);
    }

    public bool TryAcquireWork(
        int mipCount,
        int maxFaces,
        int maxMips,
        out ReflectionProbeWork work,
        ReflectionProbeWorkKind requiredKind = ReflectionProbeWorkKind.None,
        ulong currentFrame = 0UL)
    {
        if (mipCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(mipCount));
        // One scheduler scratch image is shared by the capture transaction.  Do not start a new
        // transaction until the previous copy has retired, even when its owner was removed.
        if (_completionCount != 0)
        {
            work = default;
            return false;
        }

        if (_activeLayer < 0 && !TryStartNext(currentFrame, out _activeLayer))
        {
            work = default;
            return false;
        }

        ref Entry entry = ref _entries[_activeLayer];
        if (entry.SupersededBeforeCommit)
        {
            if (requiredKind is not (ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.CaptureFace))
            {
                work = default;
                return false;
            }
            RestartFromLatest(ref entry);
        }

        ReflectionProbeCaptureTicket ticket = CreateTicket(_activeLayer, entry);
        if (entry.NextFace < 6 && maxFaces > 0)
        {
            if (requiredKind is not (ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.CaptureFace))
            {
                work = default;
                return false;
            }
            entry.State = ReflectionProbeCaptureState.CapturingFaces;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.CaptureFace, ticket, entry.NextFace, 0);
            return true;
        }
        if (entry.NextMip < mipCount && maxMips > 0)
        {
            if (requiredKind is not (ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.PrefilterMip))
            {
                work = default;
                return false;
            }
            entry.State = ReflectionProbeCaptureState.PrefilteringMips;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.PrefilterMip, ticket, -1, entry.NextMip);
            return true;
        }
        if (entry.NextFace >= 6 && entry.NextMip >= mipCount)
        {
            if (requiredKind is not (ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.PublishCopy))
            {
                work = default;
                return false;
            }
            entry.State = ReflectionProbeCaptureState.CopyReady;
            work = new ReflectionProbeWork(ReflectionProbeWorkKind.PublishCopy, CreateTicket(_activeLayer, entry), -1, -1);
            return true;
        }

        work = default;
        return false;
    }

    /// <summary>
    /// Non-mutating work-kind probe used by conditional render-pass declarations. It inspects the
    /// active ticket first and otherwise only the bounded scheduler ring; it never starts a ticket,
    /// advances progress, or allocates.
    /// </summary>
    public bool HasWork(
        int mipCount,
        ReflectionProbeWorkKind requiredKind,
        ulong currentFrame = 0UL)
    {
        if (mipCount <= 0 || _completionCount != 0)
            return false;

        if (_activeLayer >= 0)
            return EntryHasWork(_entries[_activeLayer], mipCount, requiredKind, currentFrame);

        int candidates = _queueCount;
        int queueIndex = _queueHead;
        while (candidates-- > 0)
        {
            int layer = _queue[queueIndex];
            queueIndex = (queueIndex + 1) % _queue.Length;
            ref Entry entry = ref _entries[layer];
            if (entry.Registered && entry.HasPendingRequest &&
                entry.RetryAfterFrame <= currentFrame &&
                EntryHasWork(entry, mipCount, requiredKind, currentFrame))
            {
                return true;
            }
        }

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
        if (entry.State != ReflectionProbeCaptureState.CopyReady || entry.CompletionValue != 0UL)
            throw new InvalidOperationException("The reflection publish copy was already submitted or is not ready.");
        entry.State = ReflectionProbeCaptureState.AwaitingGpuCompletion;
        entry.CompletionValue = completionValue;
        entry.CopyCommitted = true;
        _completionCount++;
    }

    /// <summary>
    /// Keeps the latest ticket request but removes the active work item from the hot queue for a
    /// bounded cooling period. This is used when an authored scene is changing faster than a
    /// six-face capture can be made coherent; it prevents repeated record/fail/requeue spins.
    /// </summary>
    public void DeferActive(in ReflectionProbeWork work, ulong currentFrame, ulong deferFrames)
    {
        ref Entry entry = ref ValidateActive(work.Ticket);
        if (work.Kind == ReflectionProbeWorkKind.PublishCopy)
            throw new InvalidOperationException("A copy-committed reflection ticket cannot be deferred.");

        int layer = _activeLayer;
        _activeLayer = -1;
        PreserveActiveRequest(ref entry);
        entry.Deferred = true;
        entry.RetryAfterFrame = AddSaturating(currentFrame, deferFrames);
        entry.State = ReflectionProbeCaptureState.DeferredChangingScene;
        _deferredChangingSceneTotal++;
        Enqueue(layer, ref entry);
    }

    public bool TryPublishCompleted(ulong completedValue, out ReflectionProbeCaptureTicket ticket)
    {
        bool retired = TryRetireCompleted(completedValue, 0U, out ticket, out bool published);
        return retired && published;
    }

    /// <summary>
    /// Consumes the completion boundary for the copy.  A nonzero current resource generation
    /// rejects a completion recorded against an older cubemap allocation and releases the pin
    /// without publishing stale pixels.
    /// </summary>
    public bool TryPublishCompleted(
        ulong completedValue,
        uint currentResourceGeneration,
        out ReflectionProbeCaptureTicket ticket)
    {
        bool retired = TryRetireCompleted(
            completedValue,
            currentResourceGeneration,
            out ticket,
            out bool published);
        return retired && published;
    }

    /// <summary>
    /// Retires a completed copy even when its owner was removed or its resource generation is
    /// stale. <paramref name="published"/> distinguishes a valid publication from a discarded
    /// completion, allowing the manager to reclaim an orphaned layer without exposing old pixels.
    /// </summary>
    public bool TryRetireCompleted(
        ulong completedValue,
        uint currentResourceGeneration,
        out ReflectionProbeCaptureTicket ticket,
        out bool published) =>
        TryRetireCompleted(
            completedValue,
            currentResourceGeneration,
            default,
            out ticket,
            out published);

    public bool TryRetireCompleted(
        ulong completedValue,
        uint currentResourceGeneration,
        ReflectionCaptureVersion currentVersion,
        out ReflectionProbeCaptureTicket ticket,
        out bool published)
    {
        for (int layer = 0; layer < _entries.Length; layer++)
        {
            ref Entry entry = ref _entries[layer];
            if (entry.CompletionValue == 0UL || entry.CompletionValue > completedValue)
                continue;

            _completionCount--;
            ticket = CreateTicket(layer, entry);
            entry.CompletionValue = 0UL;
            entry.CopyCommitted = false;
            if (currentVersion != default &&
                entry.TicketVersion != currentVersion)
            {
                // The lighting generation changed after the copy was queued.
                // Never advertise that stale ticket as the current publication;
                // retain the prior logical publication and immediately recapture
                // from the latest requested generation.
                _staleCompletionRejections++;
                if (!entry.HasPendingRequest)
                {
                    entry.RequestedSnapshot = entry.TicketSnapshot;
                    entry.RequestedResourceGeneration =
                        entry.TicketResourceGeneration;
                    entry.RequestedSceneRevision =
                        entry.TicketSceneRevision;
                }
                entry.HasPendingRequest = true;
                entry.RequestedVersion = currentVersion;
                entry.RequestedReasons |= ReflectionCaptureReason.DdgiChanged;
                entry.State = ReflectionProbeCaptureState.RetryPending;
                entry.Serial = 0UL;
                if (_activeLayer == layer)
                    _activeLayer = -1;
                Enqueue(layer, ref entry);
                published = false;
                return true;
            }
            if (!entry.Registered ||
                (currentResourceGeneration != 0U && entry.TicketResourceGeneration != currentResourceGeneration))
            {
                _staleCompletionRejections++;
                bool retainRequest = entry.Registered && entry.HasPendingRequest;
                if (retainRequest)
                {
                    entry.HasPublished = false;
                    entry.State = ReflectionProbeCaptureState.RetryPending;
                    entry.CopyCommitted = false;
                    entry.Serial = 0UL;
                    Enqueue(layer, ref entry);
                }
                else
                {
                    entry = default;
                }
                if (_activeLayer == layer)
                    _activeLayer = -1;
                published = false;
                return true;
            }

            entry.HasPublished = true;
            entry.PublishedVersion = entry.TicketVersion;
            entry.State = ReflectionProbeCaptureState.Published;
            _publishedTotal++;
            if (_activeLayer == layer)
                _activeLayer = -1;
            if (entry.HasPendingRequest &&
                (entry.RequestedVersion != entry.PublishedVersion ||
                 entry.RequestedReasons != ReflectionCaptureReason.None))
                Enqueue(layer, ref entry);
            published = true;
            return true;
        }

        ticket = default;
        published = false;
        return false;
    }

    public void FailActive(in ReflectionProbeCaptureTicket ticket, bool retry)
    {
        FailActive(ticket, retry, 0UL, 0UL);
    }

    public void FailActive(
        in ReflectionProbeCaptureTicket ticket,
        bool retry,
        ulong currentFrame,
        ulong retryBackoffFrames)
    {
        ref Entry entry = ref ValidateActive(ticket);
        _failedTotal++;
        int layer = _activeLayer;
        _activeLayer = -1;
        if (retry)
        {
            if (entry.RetryCount >= _retryLimit)
            {
                if (entry.HasPendingRequest)
                {
                    // A newer request arrived while this ticket was active. The exhausted
                    // retry budget belongs to the old ticket; keep the latest request alive.
                    PreserveActiveRequest(ref entry);
                    entry.RetryCount = 0;
                    entry.RetryAfterFrame = AddSaturating(currentFrame, retryBackoffFrames);
                    entry.Deferred = false;
                    entry.State = ReflectionProbeCaptureState.RetryPending;
                    Enqueue(layer, ref entry);
                }
                else
                {
                    entry.State = ReflectionProbeCaptureState.Failed;
                    _retryExhaustedTotal++;
                }
                return;
            }

            entry.RetryCount++;
            PreserveActiveRequest(ref entry);
            entry.RetryAfterFrame = AddSaturating(currentFrame, retryBackoffFrames);
            entry.Deferred = false;
            entry.State = ReflectionProbeCaptureState.RetryPending;
            Enqueue(layer, ref entry);
        }
        else
        {
            if (entry.HasPendingRequest)
            {
                // Non-retry failures still must not discard a request that superseded the
                // failed transaction while it was recording.
                PreserveActiveRequest(ref entry);
                entry.RetryCount = 0;
                entry.RetryAfterFrame = AddSaturating(currentFrame, retryBackoffFrames);
                entry.Deferred = false;
                entry.State = ReflectionProbeCaptureState.RetryPending;
                Enqueue(layer, ref entry);
            }
            else
            {
                entry.State = ReflectionProbeCaptureState.Failed;
            }
        }
    }

    public bool HasPublishedCapture(int layer, Guid probeId)
    {
        ValidateLayer(layer);
        ref Entry entry = ref _entries[layer];
        return entry.Registered && entry.ProbeId == probeId && entry.HasPublished;
    }

    /// <summary>Returns true while a resource generation cannot safely reuse this layer.</summary>
    public bool IsLayerPinned(int layer)
    {
        ValidateLayer(layer);
        return _entries[layer].CompletionValue != 0UL;
    }

    public ReflectionProbeCaptureState GetState(int layer, Guid probeId)
    {
        ValidateLayer(layer);
        ref Entry entry = ref _entries[layer];
        return entry.Registered && entry.ProbeId == probeId
            ? entry.State
            : ReflectionProbeCaptureState.Unregistered;
    }

    private bool TryStartNext(ulong currentFrame, out int layer)
    {
        int candidates = _queueCount;
        while (candidates-- > 0)
        {
            layer = _queue[_queueHead];
            _queueHead = (_queueHead + 1) % _queue.Length;
            _queueCount--;
            ref Entry entry = ref _entries[layer];
            entry.Queued = false;
            if (!entry.Registered || !entry.HasPendingRequest)
                continue;
            if (entry.RetryAfterFrame > currentFrame)
            {
                Enqueue(layer, ref entry);
                continue;
            }
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
            entry.Deferred = false;
            _startedTotal++;
            return true;
        }
        layer = -1;
        return false;
    }

    private static bool EntryHasWork(
        in Entry entry,
        int mipCount,
        ReflectionProbeWorkKind requiredKind,
        ulong currentFrame)
    {
        if (!entry.Registered || entry.RetryAfterFrame > currentFrame)
            return false;
        if (entry.SupersededBeforeCommit)
            return requiredKind is ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.CaptureFace;
        if (entry.NextFace < 6)
            return requiredKind is ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.CaptureFace;
        if (entry.NextMip < mipCount)
            return requiredKind is ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.PrefilterMip;
        return requiredKind is ReflectionProbeWorkKind.None or ReflectionProbeWorkKind.PublishCopy;
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
        entry.Deferred = false;
        _startedTotal++;
    }

    private static void PreserveActiveRequest(ref Entry entry)
    {
        if (!entry.HasPendingRequest)
        {
            entry.HasPendingRequest = true;
            entry.RequestedVersion = entry.TicketVersion;
            entry.RequestedSnapshot = entry.TicketSnapshot;
            entry.RequestedResourceGeneration = entry.TicketResourceGeneration;
            entry.RequestedSceneRevision = entry.TicketSceneRevision;
        }
        entry.RequestedReasons |= entry.TicketReasons;
    }

    private void Enqueue(int layer, ref Entry entry)
    {
        if (entry.Queued)
            return;
        if (_queueCount == _queue.Length)
        {
            _queueCapacityRejections++;
            entry.State = ReflectionProbeCaptureState.RetryPending;
            return;
        }
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

    private static ulong AddSaturating(ulong value, ulong increment) =>
        increment > ulong.MaxValue - value ? ulong.MaxValue : value + increment;

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
        public ulong Serial, CompletionValue, RetryAfterFrame;
        public int RetryCount;
        public int NextFace, NextMip;
        public bool Deferred;
    }
}
