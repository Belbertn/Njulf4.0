using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

[Flags]
public enum SimpleDdgiRefinementDemandReason : uint
{
    None = 0,
    VisibleReceiver = 1u << 0,
    ThinSurface = 1u << 1,
    DynamicGeometry = 1u << 2,
    CompactEmissive = 1u << 3,
    AuthoredHero = 1u << 4
}

public readonly record struct SimpleDdgiRefinementDemand(
    Vector3 Position,
    float Priority,
    SimpleDdgiRefinementDemandReason Reason,
    ulong StableSourceId = 0);

public readonly record struct SimpleDdgiRefinementBrickConfiguration(
    bool Enabled,
    int Capacity,
    int CountX,
    int CountY,
    int CountZ,
    float Spacing,
    int RetentionFrames)
{
    public int ProbesPerBrick => checked(CountX * CountY * CountZ);
}

public readonly record struct SimpleDdgiRefinementBrickKey(int X, int Y, int Z);

public readonly record struct SimpleDdgiRefinementBrick(
    int Slot,
    SimpleDdgiRefinementBrickKey Key,
    Vector3 Origin,
    int CountX,
    int CountY,
    int CountZ,
    float Spacing,
    float Priority,
    SimpleDdgiRefinementDemandReason Reasons,
    uint LastDemandFrame)
{
    public int ProbeCount => checked(CountX * CountY * CountZ);
    public Vector3 LatticeSize => new(
        Math.Max(CountX - 1, 1) * Spacing,
        Math.Max(CountY - 1, 1) * Spacing,
        Math.Max(CountZ - 1, 1) * Spacing);
}

public readonly record struct SimpleDdgiRefinementBrickPoolDiagnostics(
    bool Enabled,
    int DemandCount,
    int UniqueCandidateCount,
    int ActiveBrickCount,
    int AdmittedBrickCount,
    int EvictedBrickCount,
    int RetainedUndemandedBrickCount,
    int RejectedDemandCount,
    bool TopologyChanged,
    int ProbeCount);

/// <summary>
/// Tiny world-keyed, hysteretic allocation pool for additive fine-probe
/// bricks. Slot identity is stable; changing the world key in a slot is an
/// explicit topology transaction and therefore cannot silently reuse receiver
/// authority from the previous brick.
/// </summary>
public sealed class SimpleDdgiRefinementBrickPool
{
    public const int MaximumCapacity = 4;
    public const int MaximumCandidateDemandCount = 64;
    private const float ReplacementHysteresis = 1.20f;

    private readonly SlotState[] _slots = new SlotState[MaximumCapacity];
    private readonly List<Candidate> _candidateScratch =
        new(MaximumCandidateDemandCount);
    private readonly List<SimpleDdgiRefinementBrick> _activeScratch =
        new(MaximumCapacity);

    public SimpleDdgiRefinementBrickPoolDiagnostics Diagnostics { get; private set; }
    public IReadOnlyList<SimpleDdgiRefinementBrick> ActiveBricks => _activeScratch;

    public IReadOnlyList<SimpleDdgiRefinementBrick> Update(
        uint frameIndex,
        SimpleDdgiRefinementBrickConfiguration configuration,
        IReadOnlyList<SimpleDdgiRefinementDemand> demands)
    {
        ArgumentNullException.ThrowIfNull(demands);
        ValidateConfiguration(configuration);
        _activeScratch.Clear();
        _candidateScratch.Clear();

        int capacity = configuration.Enabled
            ? Math.Clamp(configuration.Capacity, 0, MaximumCapacity)
            : 0;
        bool topologyChanged = false;
        int admitted = 0;
        int evicted = 0;
        int rejected = 0;

        for (int slot = 0; slot < _slots.Length; slot++)
        {
            SlotState state = _slots[slot];
            state.DemandedThisFrame = false;
            if (slot >= capacity && state.Active)
            {
                state = default;
                topologyChanged = true;
                evicted++;
            }
            _slots[slot] = state;
        }

        if (capacity == 0)
        {
            Diagnostics = new SimpleDdgiRefinementBrickPoolDiagnostics(
                configuration.Enabled,
                demands.Count,
                0,
                0,
                0,
                evicted,
                0,
                demands.Count,
                topologyChanged,
                0);
            return _activeScratch;
        }

        for (int demandIndex = 0; demandIndex < demands.Count; demandIndex++)
        {
            SimpleDdgiRefinementDemand demand = demands[demandIndex];
            if (!IsValid(demand))
            {
                rejected++;
                continue;
            }

            int retainedSlot = FindRetainingSlot(
                demand.Position,
                configuration,
                capacity);
            if (retainedSlot >= 0)
            {
                SlotState retained = _slots[retainedSlot];
                retained.DemandedThisFrame = true;
                retained.LastDemandFrame = frameIndex;
                retained.Priority = Math.Max(retained.Priority, demand.Priority);
                retained.Reasons |= demand.Reason;
                _slots[retainedSlot] = retained;
                continue;
            }

            SimpleDdgiRefinementBrickKey key = ResolveKey(
                demand.Position,
                configuration);
            int candidateIndex = FindCandidate(key);
            if (candidateIndex >= 0)
            {
                Candidate candidate = _candidateScratch[candidateIndex];
                candidate.Priority = Math.Max(candidate.Priority, demand.Priority);
                candidate.Reasons |= demand.Reason;
                candidate.StableSourceId = Math.Min(
                    candidate.StableSourceId,
                    demand.StableSourceId);
                _candidateScratch[candidateIndex] = candidate;
            }
            else if (_candidateScratch.Count < MaximumCandidateDemandCount)
            {
                _candidateScratch.Add(new Candidate(
                    key,
                    demand.Priority,
                    demand.Reason,
                    demand.StableSourceId));
            }
            else
            {
                rejected++;
            }
        }

        for (int slot = 0; slot < capacity; slot++)
        {
            SlotState state = _slots[slot];
            if (!state.Active || state.DemandedThisFrame)
                continue;
            uint age = unchecked(frameIndex - state.LastDemandFrame);
            if (age <= (uint)Math.Max(configuration.RetentionFrames, 0))
                continue;
            _slots[slot] = default;
            topologyChanged = true;
            evicted++;
        }

        _candidateScratch.Sort(static (left, right) =>
        {
            int priority = right.Priority.CompareTo(left.Priority);
            if (priority != 0)
                return priority;
            int x = left.Key.X.CompareTo(right.Key.X);
            if (x != 0)
                return x;
            int y = left.Key.Y.CompareTo(right.Key.Y);
            if (y != 0)
                return y;
            int z = left.Key.Z.CompareTo(right.Key.Z);
            return z != 0
                ? z
                : left.StableSourceId.CompareTo(right.StableSourceId);
        });

        foreach (Candidate candidate in _candidateScratch)
        {
            int slot = FindEmptySlot(capacity);
            if (slot < 0)
                slot = FindReplacementSlot(candidate.Priority, capacity);
            if (slot < 0)
            {
                rejected++;
                continue;
            }

            if (_slots[slot].Active)
                evicted++;
            _slots[slot] = CreateSlot(
                candidate,
                configuration,
                frameIndex);
            topologyChanged = true;
            admitted++;
        }

        int retainedUndemanded = 0;
        int probeCount = 0;
        for (int slot = 0; slot < capacity; slot++)
        {
            SlotState state = _slots[slot];
            if (!state.Active)
                continue;
            if (!state.DemandedThisFrame)
                retainedUndemanded++;
            var brick = new SimpleDdgiRefinementBrick(
                slot,
                state.Key,
                state.Origin,
                configuration.CountX,
                configuration.CountY,
                configuration.CountZ,
                configuration.Spacing,
                state.Priority,
                state.Reasons,
                state.LastDemandFrame);
            _activeScratch.Add(brick);
            probeCount = checked(probeCount + brick.ProbeCount);
        }

        Diagnostics = new SimpleDdgiRefinementBrickPoolDiagnostics(
            true,
            demands.Count,
            _candidateScratch.Count,
            _activeScratch.Count,
            admitted,
            evicted,
            retainedUndemanded,
            rejected,
            topologyChanged,
            probeCount);
        return _activeScratch;
    }

    public void Clear()
    {
        Array.Clear(_slots);
        _candidateScratch.Clear();
        _activeScratch.Clear();
        Diagnostics = default;
    }

    private int FindRetainingSlot(
        Vector3 position,
        SimpleDdgiRefinementBrickConfiguration configuration,
        int capacity)
    {
        int best = -1;
        float bestDistanceSquared = float.MaxValue;
        Vector3 lattice = LatticeSize(configuration);
        Vector3 margin = lattice * 0.20f +
                         new Vector3(configuration.Spacing);
        for (int slot = 0; slot < capacity; slot++)
        {
            SlotState state = _slots[slot];
            if (!state.Active)
                continue;
            Vector3 min = state.Origin - margin;
            Vector3 max = state.Origin + lattice + margin;
            if (position.X < min.X || position.X > max.X ||
                position.Y < min.Y || position.Y > max.Y ||
                position.Z < min.Z || position.Z > max.Z)
            {
                continue;
            }
            Vector3 center = state.Origin + lattice * 0.5f;
            float distanceSquared = (position - center).LengthSquared();
            if (distanceSquared < bestDistanceSquared)
            {
                best = slot;
                bestDistanceSquared = distanceSquared;
            }
        }
        return best;
    }

    private int FindCandidate(SimpleDdgiRefinementBrickKey key)
    {
        for (int index = 0; index < _candidateScratch.Count; index++)
            if (_candidateScratch[index].Key == key)
                return index;
        return -1;
    }

    private int FindEmptySlot(int capacity)
    {
        for (int slot = 0; slot < capacity; slot++)
            if (!_slots[slot].Active)
                return slot;
        return -1;
    }

    private int FindReplacementSlot(float candidatePriority, int capacity)
    {
        int weakestUndemanded = -1;
        float weakestUndemandedPriority = float.MaxValue;
        int weakestDemanded = -1;
        float weakestDemandedPriority = float.MaxValue;
        for (int slot = 0; slot < capacity; slot++)
        {
            SlotState state = _slots[slot];
            if (!state.Active)
                continue;
            if (!state.DemandedThisFrame &&
                state.Priority < weakestUndemandedPriority)
            {
                weakestUndemanded = slot;
                weakestUndemandedPriority = state.Priority;
            }
            else if (state.DemandedThisFrame &&
                     state.Priority < weakestDemandedPriority)
            {
                weakestDemanded = slot;
                weakestDemandedPriority = state.Priority;
            }
        }

        // Prefer dormant slots, but do not let the two persistent camera
        // anchors monopolize a tiny pool when a materially stronger compact
        // emissive or dynamic-geometry request appears. The same hysteresis
        // margin applies, preventing equal-priority frame-order churn.
        if (weakestUndemanded >= 0 &&
            candidatePriority >
                weakestUndemandedPriority * ReplacementHysteresis)
        {
            return weakestUndemanded;
        }
        return weakestDemanded >= 0 &&
               candidatePriority >
                   weakestDemandedPriority * ReplacementHysteresis
            ? weakestDemanded
            : -1;
    }

    private static SlotState CreateSlot(
        Candidate candidate,
        SimpleDdgiRefinementBrickConfiguration configuration,
        uint frameIndex) => new()
    {
        Active = true,
        Key = candidate.Key,
        Origin = ResolveOrigin(candidate.Key, configuration),
        Priority = candidate.Priority,
        Reasons = candidate.Reasons,
        LastDemandFrame = frameIndex,
        DemandedThisFrame = true
    };

    private static SimpleDdgiRefinementBrickKey ResolveKey(
        Vector3 position,
        SimpleDdgiRefinementBrickConfiguration configuration)
    {
        Vector3 step = LatticeSize(configuration);
        return new SimpleDdgiRefinementBrickKey(
            FloorToInt(position.X / step.X + 0.5f),
            FloorToInt(position.Y / step.Y + 0.5f),
            FloorToInt(position.Z / step.Z + 0.5f));
    }

    private static Vector3 ResolveOrigin(
        SimpleDdgiRefinementBrickKey key,
        SimpleDdgiRefinementBrickConfiguration configuration)
    {
        Vector3 step = LatticeSize(configuration);
        return new Vector3(key.X * step.X, key.Y * step.Y, key.Z * step.Z) -
            step * 0.5f;
    }

    private static Vector3 LatticeSize(
        SimpleDdgiRefinementBrickConfiguration configuration) => new(
        Math.Max(configuration.CountX - 1, 1) * configuration.Spacing,
        Math.Max(configuration.CountY - 1, 1) * configuration.Spacing,
        Math.Max(configuration.CountZ - 1, 1) * configuration.Spacing);

    private static int FloorToInt(float value)
    {
        double floor = Math.Floor(value);
        return floor <= int.MinValue
            ? int.MinValue
            : floor >= int.MaxValue
                ? int.MaxValue
                : (int)floor;
    }

    private static bool IsValid(SimpleDdgiRefinementDemand demand) =>
        float.IsFinite(demand.Position.X) &&
        float.IsFinite(demand.Position.Y) &&
        float.IsFinite(demand.Position.Z) &&
        float.IsFinite(demand.Priority) &&
        demand.Priority > 0f &&
        demand.Reason != SimpleDdgiRefinementDemandReason.None;

    private static void ValidateConfiguration(
        SimpleDdgiRefinementBrickConfiguration configuration)
    {
        if (configuration.Capacity is < 0 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Refinement capacity is invalid.");
        if (configuration.CountX < 2 ||
            configuration.CountY < 2 ||
            configuration.CountZ < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "Every refinement brick axis needs at least two probes.");
        }
        if (!float.IsFinite(configuration.Spacing) || configuration.Spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Refinement spacing must be finite and positive.");
        if (configuration.RetentionFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Refinement retention cannot be negative.");
        _ = configuration.ProbesPerBrick;
    }

    private struct Candidate
    {
        public Candidate(
            SimpleDdgiRefinementBrickKey key,
            float priority,
            SimpleDdgiRefinementDemandReason reasons,
            ulong stableSourceId)
        {
            Key = key;
            Priority = priority;
            Reasons = reasons;
            StableSourceId = stableSourceId;
        }

        public SimpleDdgiRefinementBrickKey Key;
        public float Priority;
        public SimpleDdgiRefinementDemandReason Reasons;
        public ulong StableSourceId;
    }

    private struct SlotState
    {
        public bool Active;
        public SimpleDdgiRefinementBrickKey Key;
        public Vector3 Origin;
        public float Priority;
        public SimpleDdgiRefinementDemandReason Reasons;
        public uint LastDemandFrame;
        public bool DemandedThisFrame;
    }
}
