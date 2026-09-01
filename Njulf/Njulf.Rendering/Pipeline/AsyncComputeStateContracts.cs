using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

public readonly record struct AsyncComputeProjectedResourceState(
    RenderGraphAllocationIdentity Allocation,
    AsyncComputeQueue LastQueue,
    uint OwnerQueueFamily,
    ImageLayout Layout,
    PipelineStageFlags2 StageMask,
    AccessFlags2 AccessMask,
    ulong ResourcePlanGeneration);

public enum AsyncComputeProjectionFailure : byte
{
    None,
    Capacity,
    MissingResource,
    StalePlan,
    InvalidStateTransition,
    DuplicateResource
}

/// <summary>
/// Two-phase projection of concrete queue/layout state.  Mutable state is changed while a plan is
/// being compiled; committed state is copied only after command recording and validation succeed.
/// The arrays grow only at immutable resource-plan generation boundaries, so a rejected plan
/// cannot partially mutate the next frame and a stable plan performs no allocation.
/// </summary>
public sealed class AsyncComputeResourceStateProjection
{
    private const int MaximumCapacity = 131_072;

    private AsyncComputeProjectedResourceState[] _committed;
    private AsyncComputeProjectedResourceState[] _mutable;
    private readonly Dictionary<RenderGraphAllocationIdentity, int>
        _mutableIndices;
    private int _count;
    private int _committedCount;
    private ulong _committedPlanGeneration;
    private ulong _mutablePlanGeneration;

    public AsyncComputeResourceStateProjection(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (capacity > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _committed = new AsyncComputeProjectedResourceState[capacity];
        _mutable = new AsyncComputeProjectedResourceState[capacity];
        _mutableIndices = new Dictionary<RenderGraphAllocationIdentity, int>(
            capacity);
    }

    public int Capacity => _committed.Length;
    public int Count => _count;
    public ulong CommittedPlanGeneration => _committedPlanGeneration;
    public ulong MutablePlanGeneration => _mutablePlanGeneration;

    public bool Begin(RenderGraphResourcePlan plan)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));
        // Resource plans are immutable and only change when allocation
        // generations change. Grow once at that boundary; stable frames reuse
        // both arrays and the open dictionary storage without allocation.
        if (!EnsureCapacity(plan.BindingCount))
            return false;

        _count = 0;
        _mutablePlanGeneration = plan.Generation;
        _mutableIndices.Clear();
        for (int index = 0; index < plan.Bindings.Count; index++)
        {
            RenderGraphConcreteResourceBinding binding = plan.Bindings[index];
            RenderGraphAllocationIdentity identity = binding.AllocationIdentity;
            if (!_mutableIndices.TryAdd(identity, _count))
            {
                // Exact aliases intentionally share physical state. The immutable plan already
                // checked their compatibility, so one state entry is sufficient.
                continue;
            }

            if (_count == Capacity)
            {
                _count = 0;
                _mutablePlanGeneration = 0UL;
                return false;
            }

            ImageLayout layout = binding.Kind == RenderGraphConcreteResourceKind.Image
                ? binding.Layout
                : ImageLayout.Undefined;
            uint owner = binding.InitialOwnerQueueFamily ?? 0U;
            _mutable[_count] = new AsyncComputeProjectedResourceState(
                identity,
                AsyncComputeQueue.Graphics,
                owner,
                layout,
                binding.InitialStageMask,
                binding.InitialAccessMask,
                plan.Generation);
            _count++;
        }

        return true;
    }

    public bool Begin(RenderGraphResourceBindings bindings)
    {
        if (bindings == null)
            throw new ArgumentNullException(nameof(bindings));
        if (!Begin(bindings.CurrentPlan))
            return false;

        for (int bindingIndex = 0; bindingIndex < bindings.CurrentPlan.Bindings.Count; bindingIndex++)
        {
            RenderGraphConcreteResourceBinding binding = bindings.CurrentPlan.Bindings[bindingIndex];
            int stateIndex = Find(binding.AllocationIdentity);
            if (stateIndex < 0)
                continue;
            uint owner = bindings.GetCurrentOwner(binding) ?? binding.InitialOwnerQueueFamily ?? 0U;
            ImageLayout layout = binding.Kind == RenderGraphConcreteResourceKind.Image
                ? bindings.GetCurrentLayout(binding)
                : ImageLayout.Undefined;
            _mutable[stateIndex] = _mutable[stateIndex] with
            {
                OwnerQueueFamily = owner,
                Layout = layout
            };
        }
        return true;
    }

    public bool TryGet(
        in RenderGraphAllocationIdentity identity,
        bool committed,
        out AsyncComputeProjectedResourceState state)
    {
        AsyncComputeProjectedResourceState[] source = committed ? _committed : _mutable;
        int count = committed ? _committedCount : _count;
        int index = committed
            ? Find(identity, source, count)
            : Find(identity);
        if (index < 0)
        {
            state = default;
            return false;
        }

        state = source[index];
        return true;
    }

    public bool TryTransition(
        in RenderGraphAllocationIdentity identity,
        ulong planGeneration,
        AsyncComputeQueue queue,
        uint ownerQueueFamily,
        ImageLayout layout,
        PipelineStageFlags2 stageMask,
        AccessFlags2 accessMask,
        out AsyncComputeProjectionFailure failure)
    {
        int index = Find(identity);
        if (index < 0)
        {
            failure = AsyncComputeProjectionFailure.MissingResource;
            return false;
        }
        if (planGeneration == 0UL || planGeneration != _mutablePlanGeneration)
        {
            failure = AsyncComputeProjectionFailure.StalePlan;
            return false;
        }
        if (queue == AsyncComputeQueue.Compute && ownerQueueFamily == uint.MaxValue)
        {
            failure = AsyncComputeProjectionFailure.InvalidStateTransition;
            return false;
        }

        _mutable[index] = _mutable[index] with
        {
            LastQueue = queue,
            OwnerQueueFamily = ownerQueueFamily,
            Layout = layout,
            StageMask = stageMask,
            AccessMask = accessMask,
            ResourcePlanGeneration = planGeneration
        };
        failure = AsyncComputeProjectionFailure.None;
        return true;
    }

    public bool Commit(ulong planGeneration)
    {
        if (planGeneration == 0UL || planGeneration != _mutablePlanGeneration)
            return false;
        Array.Copy(_mutable, _committed, _count);
        if (_count < _committedCount)
            Array.Clear(_committed, _count, _committedCount - _count);
        _committedCount = _count;
        _committedPlanGeneration = planGeneration;
        return true;
    }

    public void Discard()
    {
        Array.Copy(_committed, _mutable, _committedCount);
        if (_committedCount < _count)
            Array.Clear(_mutable, _committedCount, _count - _committedCount);
        _count = _committedCount;
        _mutablePlanGeneration = _committedPlanGeneration;
        RebuildMutableIndices();
    }

    private int Find(in RenderGraphAllocationIdentity identity) =>
        _mutableIndices.TryGetValue(identity, out int index) ? index : -1;

    private bool EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return true;
        if (required > MaximumCapacity)
            return false;

        int capacity = Capacity;
        while (capacity < required)
        {
            int next = capacity <= MaximumCapacity / 2
                ? capacity * 2
                : MaximumCapacity;
            if (next == capacity)
                return false;
            capacity = next;
        }

        var committed =
            new AsyncComputeProjectedResourceState[capacity];
        var mutable =
            new AsyncComputeProjectedResourceState[capacity];
        Array.Copy(_committed, committed, _committedCount);
        Array.Copy(_mutable, mutable, _count);
        _committed = committed;
        _mutable = mutable;
        _mutableIndices.EnsureCapacity(capacity);
        return true;
    }

    private void RebuildMutableIndices()
    {
        _mutableIndices.Clear();
        for (int index = 0; index < _count; index++)
            _mutableIndices.Add(_mutable[index].Allocation, index);
    }

    private static int Find(
        in RenderGraphAllocationIdentity identity,
        AsyncComputeProjectedResourceState[] states,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            if (states[index].Allocation == identity)
                return index;
        }
        return -1;
    }
}

/// <summary>
/// Stable key for one complete async plan variant. It contains only resource/settings/capability
/// generations and an explicit pass/path mask; frame history and timing samples are not keys.
/// </summary>
public readonly record struct AsyncComputePlanVariantKey(
    ulong ResourcePlanGeneration,
    ulong SettingsSignature,
    ulong CapabilitySignature,
    ulong PassSetSignature,
    ulong PathMask,
    ulong SynchronizationStateGeneration = 0UL,
    ulong TimelineValueBase = 0UL);

/// <summary>Bounded LRU cache for validated immutable submission plans.</summary>
public sealed class AsyncComputePlanVariantCache
{
    private readonly AsyncComputePlanVariantKey[] _keys;
    private readonly AsyncComputeSubmissionPlan?[] _plans;
    private readonly ulong[] _lastUse;
    private ulong _clock;
    private int _count;
    private ulong _hitCount;
    private ulong _missCount;
    private ulong _evictionCount;

    public AsyncComputePlanVariantCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _keys = new AsyncComputePlanVariantKey[capacity];
        _plans = new AsyncComputeSubmissionPlan[capacity];
        _lastUse = new ulong[capacity];
    }

    public int Capacity => _keys.Length;
    public int Count => _count;
    public ulong HitCount => _hitCount;
    public ulong MissCount => _missCount;
    public ulong EvictionCount => _evictionCount;

    public bool TryGet(in AsyncComputePlanVariantKey key, out AsyncComputeSubmissionPlan plan)
    {
        for (int index = 0; index < _count; index++)
        {
            if (_keys[index] != key || _plans[index] == null ||
                !_plans[index]!.Accepted ||
                _plans[index]!.ResourcePlanGeneration != key.ResourcePlanGeneration)
                continue;
            _lastUse[index] = NextClock();
            _hitCount++;
            plan = _plans[index]!;
            return true;
        }

        _missCount++;
        plan = null!;
        return false;
    }

    public void Add(in AsyncComputePlanVariantKey key, AsyncComputeSubmissionPlan plan)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

        int slot = -1;
        for (int index = 0; index < _count; index++)
        {
            if (_keys[index] == key)
            {
                slot = index;
                break;
            }
        }
        if (slot < 0)
        {
            if (_count < _keys.Length)
            {
                slot = _count++;
            }
            else
            {
                slot = 0;
                ulong leastRecent = _lastUse[0];
                for (int index = 1; index < _lastUse.Length; index++)
                {
                    if (_lastUse[index] < leastRecent)
                    {
                        leastRecent = _lastUse[index];
                        slot = index;
                    }
                }
                _evictionCount++;
            }
        }

        _keys[slot] = key;
        _plans[slot] = plan;
        _lastUse[slot] = NextClock();
    }

    public void Clear()
    {
        Array.Clear(_plans);
        Array.Clear(_lastUse);
        _count = 0;
        _clock = 0UL;
    }

    private ulong NextClock()
    {
        _clock = _clock == ulong.MaxValue ? 1UL : _clock + 1UL;
        return _clock;
    }
}

public enum AsyncComputeValidationAttribution : byte
{
    None,
    Segment,
    Ambiguous,
    Unknown
}

public readonly record struct AsyncComputeValidationEvent(
    ulong FrameIndex,
    int SegmentId,
    AsyncComputePath Path,
    ulong ResourceHandle,
    int LabelId,
    AsyncComputeValidationAttribution Attribution,
    bool Quarantined);

/// <summary>
/// Fixed diagnostic ledger for validation-only queue failures. An error is attributed to a single
/// segment only when the supplied segment identity is exact; otherwise the involved path is
/// quarantined and Auto timing/certification remains disabled for it.
/// </summary>
public sealed class AsyncComputeValidationLedger
{
    private readonly Segment[] _segments;
    private readonly AsyncComputeValidationEvent[] _events;
    private int _segmentCount;
    private int _eventCount;
    private ulong _quarantinedPathMask;
    private ulong _frameIndex;
    private bool _globalQuarantine;

    public AsyncComputeValidationLedger(int segmentCapacity, int eventCapacity)
    {
        if (segmentCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(segmentCapacity));
        if (eventCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(eventCapacity));
        _segments = new Segment[segmentCapacity];
        _events = new AsyncComputeValidationEvent[eventCapacity];
    }

    public int EventCount => _eventCount;
    public ulong QuarantinedPathMask => _quarantinedPathMask;

    public void BeginFrame(ulong frameIndex)
    {
        _frameIndex = frameIndex;
        _segmentCount = 0;
        _eventCount = 0;
    }

    public bool RegisterSegment(int segmentId, AsyncComputePath path, ulong commandBufferIdentity)
    {
        if (_segmentCount == _segments.Length)
            return false;
        _segments[_segmentCount++] = new Segment(segmentId, path, commandBufferIdentity);
        return true;
    }

    public AsyncComputeValidationAttribution RecordError(
        int segmentId,
        ulong resourceHandle,
        int labelId,
        out AsyncComputePath path)
    {
        int match = -1;
        for (int index = 0; index < _segmentCount; index++)
        {
            if (_segments[index].SegmentId != segmentId)
                continue;
            if (match >= 0)
            {
                path = _segments[index].Path;
                return RecordEvent(
                    segmentId,
                    path,
                    resourceHandle,
                    labelId,
                    AsyncComputeValidationAttribution.Ambiguous);
            }
            match = index;
        }

        if (match < 0)
        {
            path = default;
            return RecordEvent(
                segmentId,
                default,
                resourceHandle,
                labelId,
                AsyncComputeValidationAttribution.Unknown);
        }

        path = _segments[match].Path;
        return RecordEvent(
            segmentId,
            path,
            resourceHandle,
            labelId,
            AsyncComputeValidationAttribution.Segment);
    }

    public bool IsQuarantined(AsyncComputePath path) =>
        (_quarantinedPathMask & PathBit(path)) != 0UL;

    public bool IsAutoTimingAllowed(AsyncComputePath path) =>
        !_globalQuarantine && !IsQuarantined(path);

    public void ClearQuarantine()
    {
        _quarantinedPathMask = 0UL;
        _globalQuarantine = false;
    }

    public ReadOnlySpan<AsyncComputeValidationEvent> Events =>
        _events.AsSpan(0, _eventCount);

    private AsyncComputeValidationAttribution RecordEvent(
        int segmentId,
        AsyncComputePath path,
        ulong resourceHandle,
        int labelId,
        AsyncComputeValidationAttribution attribution)
    {
        bool quarantine = attribution != AsyncComputeValidationAttribution.Segment;
        if (quarantine)
        {
            if (attribution == AsyncComputeValidationAttribution.Unknown)
                _globalQuarantine = true;
            else
                _quarantinedPathMask |= PathBit(path);
        }
        if (_eventCount < _events.Length)
        {
            _events[_eventCount++] = new AsyncComputeValidationEvent(
                _frameIndex,
                segmentId,
                path,
                resourceHandle,
                labelId,
                attribution,
                quarantine);
        }
        return attribution;
    }

    private static ulong PathBit(AsyncComputePath path)
    {
        int shift = (int)path;
        return (uint)shift < 64U ? 1UL << shift : 0UL;
    }

    private readonly record struct Segment(
        int SegmentId,
        AsyncComputePath Path,
        ulong CommandBufferIdentity);
}
