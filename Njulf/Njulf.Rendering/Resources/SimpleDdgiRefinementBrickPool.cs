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
    AuthoredHero = 1u << 4,
    HighReceiverDensity = 1u << 5,
    GeometricComplexity = 1u << 6,
    LightingVariance = 1u << 7,
    ObservedError = 1u << 8
}

public readonly record struct SimpleDdgiRefinementDemand(
    Vector3 Position,
    float Priority,
    SimpleDdgiRefinementDemandReason Reason,
    ulong StableSourceId = 0)
{
    /// <summary>
    /// Optional conservative source bounds used to place compact-emissive
    /// refinement probes on the receiver side of a Y-up scene. Keeping this
    /// outside the positional record contract preserves the original
    /// constructor and deconstructor for existing callers.
    /// </summary>
    public BoundingBox? SourceBounds { get; init; }
}

/// <summary>
/// Independent automatic-refinement signals. Receiver density is the raw B1
/// corrected contribution mass, geometric complexity and lighting variance
/// are normalized [0,1] observations, and observed error is expressed as a
/// non-negative ratio to the configured transport tolerance.
/// </summary>
public readonly record struct SimpleDdgiAutomaticRefinementMetrics(
    float ReceiverDensity,
    float GeometricComplexity,
    float LightingVariance,
    float ObservedError);

/// <summary>
/// Deterministic production scoring for automatic refinement placement. It
/// admits a strong single witness as well as combinations of moderate signals,
/// while the brick pool supplies spatial retention and replacement hysteresis.
/// </summary>
public static class SimpleDdgiAutomaticRefinementDemandBuilder
{
    public const float AdmissionThreshold = 0.45f;
    public const float ReasonThreshold = 0.35f;
    public const float ReceiverDensityReferenceMass = 8.0f;

    public static bool TryBuild(
        Vector3 position,
        in SimpleDdgiAutomaticRefinementMetrics metrics,
        ulong stableSourceId,
        out SimpleDdgiRefinementDemand demand)
    {
        demand = default;
        if (!Finite(position) ||
            !FiniteNonNegative(metrics.ReceiverDensity) ||
            !FiniteNonNegative(metrics.GeometricComplexity) ||
            !FiniteNonNegative(metrics.LightingVariance) ||
            !FiniteNonNegative(metrics.ObservedError))
        {
            return false;
        }

        float receiver = SaturatingNormalize(
            metrics.ReceiverDensity,
            ReceiverDensityReferenceMass);
        float geometry = Math.Clamp(metrics.GeometricComplexity, 0.0f, 1.0f);
        float lighting = Math.Clamp(metrics.LightingVariance, 0.0f, 1.0f);
        float error = SaturatingNormalize(metrics.ObservedError, 1.0f);
        float weighted =
            receiver * 0.35f +
            geometry * 0.25f +
            lighting * 0.20f +
            error * 0.20f;
        float score = Math.Max(
            weighted,
            Math.Max(
                Math.Max(receiver, geometry),
                Math.Max(lighting, error)) * 0.55f);
        if (score < AdmissionThreshold)
            return false;

        SimpleDdgiRefinementDemandReason reason =
            SimpleDdgiRefinementDemandReason.None;
        if (receiver >= ReasonThreshold)
            reason |= SimpleDdgiRefinementDemandReason.HighReceiverDensity;
        if (geometry >= ReasonThreshold)
            reason |= SimpleDdgiRefinementDemandReason.GeometricComplexity;
        if (lighting >= ReasonThreshold)
            reason |= SimpleDdgiRefinementDemandReason.LightingVariance;
        if (error >= ReasonThreshold)
            reason |= SimpleDdgiRefinementDemandReason.ObservedError;
        if (reason == SimpleDdgiRefinementDemandReason.None)
            return false;

        demand = new SimpleDdgiRefinementDemand(
            position,
            144.0f + score * 160.0f,
            reason,
            stableSourceId);
        return true;
    }

    private static float SaturatingNormalize(float value, float scale) =>
        value <= 0.0f ? 0.0f : value / (value + scale);

    private static bool FiniteNonNegative(float value) =>
        float.IsFinite(value) && value >= 0.0f;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

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

public readonly record struct SimpleDdgiRefinementBrickKey(int X, int Y, int Z)
{
    /// <summary>
    /// Separates ordinary centered cells from source-bounded placement cells
    /// so the two policies can never alias or retain each other's authority.
    /// </summary>
    public int PlacementClass { get; init; }
}

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
    int ProbeCount)
{
    /// <summary>Slots whose world-key ownership changed this update.</summary>
    public uint ChangedSlotMask { get; init; }

    /// <summary>Slots that contain an active brick after this update.</summary>
    public uint ActiveSlotMask { get; init; }
}

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
    public const float CompactEmissiveReceiverClearanceSpacingScale = 0.125f;
    public const float CompactEmissivePlacementQuantumSpacingScale = 0.25f;
    private const int CenteredPlacementClass = 0;
    private const int CompactEmissivePlacementClass = 1;
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
        uint changedSlotMask = 0u;
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
                changedSlotMask |= 1u << slot;
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
                0)
            {
                ChangedSlotMask = changedSlotMask,
                ActiveSlotMask = 0u
            };
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

            int retainedSlot = FindRetainingSlot(demand, configuration, capacity);
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

            SimpleDdgiRefinementBrickKey key = ResolveKey(demand, configuration);
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
            changedSlotMask |= 1u << slot;
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
            if (z != 0)
                return z;
            int placementClass = left.Key.PlacementClass.CompareTo(
                right.Key.PlacementClass);
            return placementClass != 0
                ? placementClass
                : left.StableSourceId.CompareTo(right.StableSourceId);
        });

        foreach (Candidate candidate in _candidateScratch)
        {
            int slot = FindEmptySlot(capacity);
            if (slot < 0)
                slot = FindReplacementSlot(candidate, capacity);
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
            changedSlotMask |= 1u << slot;
            admitted++;
        }

        int retainedUndemanded = 0;
        int probeCount = 0;
        uint activeSlotMask = 0u;
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
            activeSlotMask |= 1u << slot;
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
            probeCount)
        {
            ChangedSlotMask = changedSlotMask,
            ActiveSlotMask = activeSlotMask
        };
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
        SimpleDdgiRefinementDemand demand,
        SimpleDdgiRefinementBrickConfiguration configuration,
        int capacity)
    {
        int best = -1;
        float bestDistanceSquared = float.MaxValue;
        Vector3 lattice = LatticeSize(configuration);
        int placementClass = ResolvePlacementClass(demand);
        SimpleDdgiRefinementBrickKey resolvedKey = ResolveKey(
            demand,
            configuration);
        Vector3 position = demand.Position;
        for (int slot = 0; slot < capacity; slot++)
        {
            SlotState state = _slots[slot];
            if (!state.Active || state.Key.PlacementClass != placementClass)
                continue;
            if (placementClass == CompactEmissivePlacementClass &&
                state.Key != resolvedKey)
            {
                continue;
            }
            Vector3 min = state.Origin;
            Vector3 max = state.Origin + lattice;
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

    private int FindReplacementSlot(Candidate candidate, int capacity)
    {
        int weakestUndemanded = -1;
        float weakestUndemandedPriority = float.MaxValue;
        int weakestUndemandedSameReason = -1;
        float weakestUndemandedSameReasonPriority = float.MaxValue;
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
            if (!state.DemandedThisFrame &&
                (state.Reasons & candidate.Reasons) !=
                    SimpleDdgiRefinementDemandReason.None &&
                state.Priority < weakestUndemandedSameReasonPriority)
            {
                weakestUndemandedSameReason = slot;
                weakestUndemandedSameReasonPriority = state.Priority;
            }
            if (state.DemandedThisFrame &&
                state.Priority < weakestDemandedPriority)
            {
                weakestDemanded = slot;
                weakestDemandedPriority = state.Priority;
            }
        }

        // A current demand may reclaim an undemanded slot serving the same
        // purpose without a hysteresis premium. Cross-purpose replacement
        // retains the material-priority margin to prevent frame-order churn.
        if (weakestUndemandedSameReason >= 0 &&
            candidate.Priority >= weakestUndemandedSameReasonPriority)
        {
            return weakestUndemandedSameReason;
        }
        if (weakestUndemanded >= 0 &&
            candidate.Priority >
                weakestUndemandedPriority * ReplacementHysteresis)
        {
            return weakestUndemanded;
        }
        return weakestDemanded >= 0 &&
               candidate.Priority >
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
        SimpleDdgiRefinementDemand demand,
        SimpleDdgiRefinementBrickConfiguration configuration)
    {
        if (ResolvePlacementClass(demand) == CompactEmissivePlacementClass)
        {
            BoundingBox sourceBounds = demand.SourceBounds!.Value;
            Vector3 min = Vector3.Min(sourceBounds.Min, sourceBounds.Max);
            Vector3 max = Vector3.Max(sourceBounds.Min, sourceBounds.Max);
            Vector3 center = (min + max) * 0.5f;
            Vector3 lattice = LatticeSize(configuration);
            float quantum = Math.Max(
                configuration.Spacing *
                    CompactEmissivePlacementQuantumSpacingScale,
                0.001f);
            Vector3 desiredOrigin = new(
                center.X - lattice.X * 0.5f,
                min.Y + configuration.Spacing *
                    CompactEmissiveReceiverClearanceSpacingScale,
                center.Z - lattice.Z * 0.5f);
            return new SimpleDdgiRefinementBrickKey(
                FloorToInt(desiredOrigin.X / quantum + 0.5f),
                FloorToInt(desiredOrigin.Y / quantum + 0.5f),
                FloorToInt(desiredOrigin.Z / quantum + 0.5f))
            {
                PlacementClass = CompactEmissivePlacementClass
            };
        }

        Vector3 position = demand.Position;
        Vector3 step = PlacementStep(configuration);
        return new SimpleDdgiRefinementBrickKey(
            FloorToInt(position.X / step.X + 0.5f),
            FloorToInt(position.Y / step.Y + 0.5f),
            FloorToInt(position.Z / step.Z + 0.5f));
    }

    private static Vector3 ResolveOrigin(
        SimpleDdgiRefinementBrickKey key,
        SimpleDdgiRefinementBrickConfiguration configuration)
    {
        if (key.PlacementClass == CompactEmissivePlacementClass)
        {
            float quantum = Math.Max(
                configuration.Spacing *
                    CompactEmissivePlacementQuantumSpacingScale,
                0.001f);
            return new Vector3(key.X, key.Y, key.Z) * quantum;
        }

        Vector3 step = PlacementStep(configuration);
        return new Vector3(key.X * step.X, key.Y * step.Y, key.Z * step.Z) -
            LatticeSize(configuration) * 0.5f;
    }

    private static int ResolvePlacementClass(
        SimpleDdgiRefinementDemand demand) =>
        (demand.Reason & SimpleDdgiRefinementDemandReason.CompactEmissive) != 0 &&
        demand.SourceBounds.HasValue
            ? CompactEmissivePlacementClass
            : CenteredPlacementClass;

    private static Vector3 PlacementStep(
        SimpleDdgiRefinementBrickConfiguration configuration) => new(
        Math.Max(configuration.CountX - 2, 1) * configuration.Spacing,
        Math.Max(configuration.CountY - 2, 1) * configuration.Spacing,
        Math.Max(configuration.CountZ - 2, 1) * configuration.Spacing);

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
        demand.Reason != SimpleDdgiRefinementDemandReason.None &&
        (!demand.SourceBounds.HasValue ||
         IsFinite(demand.SourceBounds.Value.Min) &&
         IsFinite(demand.SourceBounds.Value.Max));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

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
