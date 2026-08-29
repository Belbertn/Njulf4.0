using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Njulf.Core.Foliage;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

public enum FoliageCellTier : uint
{
    Near = 0,
    Mid = 1,
    Far = 2
}

public enum FoliageCellResidencyState : uint
{
    Pending = 0,
    Resident = 1,
    Retiring = 2,
    Retry = 3
}

public readonly record struct FoliageStreamingOptions(
    float NearDistance,
    float MidDistance,
    float FarDistance,
    float HysteresisDistance,
    int MaximumLoadsPerFrame,
    int MaximumRetirementsPerFrame,
    ulong MaximumUploadBytesPerFrame,
    ulong EstimatedCellUploadBytes,
    uint RetirementDelayFrames,
    uint RetryDelayFrames,
    int MaximumCandidateCells)
{
    public static FoliageStreamingOptions ForDrawDistance(
        float drawDistance) => new(
        NearDistance: Math.Max(16.0f, drawDistance * 0.35f),
        MidDistance: Math.Max(32.0f, drawDistance * 0.70f),
        FarDistance: Math.Max(64.0f, drawDistance),
        HysteresisDistance: 32.0f,
        MaximumLoadsPerFrame: 32,
        MaximumRetirementsPerFrame: 32,
        MaximumUploadBytesPerFrame: 8UL * 1024UL * 1024UL,
        EstimatedCellUploadBytes: 128UL * 1024UL,
        RetirementDelayFrames: 3u,
        RetryDelayFrames: 30u,
        MaximumCandidateCells: 65_536);
}

public readonly record struct FoliageStreamingSnapshot(
    ulong ResidencyGeneration,
    int CandidateCellCount,
    int ResidentCellCount,
    int PendingCellCount,
    int RetiringCellCount,
    int RetryCellCount,
    int NearCellCount,
    int MidCellCount,
    int FarCellCount,
    int LoadedThisFrame,
    int RetiredThisFrame,
    ulong ScheduledUploadBytes,
    int CandidateOverflowCount)
{
    public bool ChangedThisFrame => LoadedThisFrame != 0 ||
        RetiredThisFrame != 0;
}

/// <summary>
/// Bounded logical residency for foliage world cells. Loading can be backed by
/// an asynchronous content callback; retirement always waits long enough for
/// in-flight frame references to complete.
/// </summary>
public sealed class FoliageStreamingManager
{
    private readonly Dictionary<FoliageCellKey, CellState> _states = [];
    private readonly Dictionary<FoliageCellKey, DesiredCell> _desired = [];
    private ulong _generation;

    public FoliageStreamingSnapshot LastSnapshot { get; private set; }

    public bool IsResident(FoliageCellKey key) =>
        _states.TryGetValue(key, out CellState state) &&
        state.State is FoliageCellResidencyState.Resident or
            FoliageCellResidencyState.Retiring;

    public bool TryGetTier(FoliageCellKey key, out FoliageCellTier tier)
    {
        if (_states.TryGetValue(key, out CellState state) &&
            state.State is FoliageCellResidencyState.Resident or
                FoliageCellResidencyState.Retiring)
        {
            tier = state.Tier;
            return true;
        }
        tier = FoliageCellTier.Far;
        return false;
    }

    public FoliageStreamingSnapshot Update(
        IReadOnlyList<FoliagePatch> patches,
        Vector3 cameraPosition,
        ulong frameSerial,
        in FoliageStreamingOptions options,
        Func<FoliageCellKey, bool>? tryLoad = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        Validate(options);
        BuildDesired(patches, cameraPosition, options,
            out int candidateOverflow);
        UpdateExistingStates(frameSerial, options);

        int loadBudget = Math.Max(0, options.MaximumLoadsPerFrame);
        ulong uploadBudget = options.MaximumUploadBytesPerFrame;
        int attempts = 0;
        int loaded = 0;
        ulong scheduledBytes = 0UL;
        foreach ((FoliageCellKey key, DesiredCell desired) in _desired
                     .OrderBy(static pair => pair.Value.Distance)
                     .ThenBy(static pair => pair.Key))
        {
            if (attempts >= loadBudget ||
                options.EstimatedCellUploadBytes >
                    uploadBudget - scheduledBytes)
            {
                break;
            }
            if (_states.TryGetValue(key, out CellState existing) &&
                existing.State is FoliageCellResidencyState.Resident or
                    FoliageCellResidencyState.Retiring)
            {
                continue;
            }
            if (existing.State == FoliageCellResidencyState.Retry &&
                frameSerial < existing.RetryAfterFrame)
            {
                continue;
            }
            attempts++;
            bool admitted = tryLoad?.Invoke(key) ?? true;
            if (!admitted)
            {
                _states[key] = new CellState(
                    FoliageCellResidencyState.Retry,
                    desired.Tier,
                    frameSerial,
                    0UL,
                    checked(frameSerial + options.RetryDelayFrames));
                continue;
            }
            _states[key] = new CellState(
                FoliageCellResidencyState.Resident,
                desired.Tier,
                frameSerial,
                0UL,
                0UL);
            loaded++;
            scheduledBytes = checked(
                scheduledBytes + options.EstimatedCellUploadBytes);
            AdvanceGeneration();
        }

        int retired = RetireExpired(frameSerial, options);
        LastSnapshot = CreateSnapshot(
            loaded,
            retired,
            scheduledBytes,
            candidateOverflow);
        return LastSnapshot;
    }

    public async ValueTask<FoliageStreamingSnapshot> UpdateAsync(
        IReadOnlyList<FoliagePatch> patches,
        Vector3 cameraPosition,
        ulong frameSerial,
        FoliageStreamingOptions options,
        Func<FoliageCellKey, CancellationToken, ValueTask<bool>> tryLoadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(tryLoadAsync);
        Validate(options);
        BuildDesired(patches, cameraPosition, options,
            out int candidateOverflow);
        UpdateExistingStates(frameSerial, options);

        int loadBudget = Math.Max(0, options.MaximumLoadsPerFrame);
        ulong uploadBudget = options.MaximumUploadBytesPerFrame;
        int attempts = 0;
        int loaded = 0;
        ulong scheduledBytes = 0UL;
        foreach ((FoliageCellKey key, DesiredCell desired) in _desired
                     .OrderBy(static pair => pair.Value.Distance)
                     .ThenBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempts >= loadBudget ||
                options.EstimatedCellUploadBytes >
                    uploadBudget - scheduledBytes)
            {
                break;
            }
            if (_states.TryGetValue(key, out CellState existing) &&
                existing.State is FoliageCellResidencyState.Resident or
                    FoliageCellResidencyState.Retiring)
            {
                continue;
            }
            if (existing.State == FoliageCellResidencyState.Retry &&
                frameSerial < existing.RetryAfterFrame)
            {
                continue;
            }

            attempts++;
            bool admitted = await tryLoadAsync(key, cancellationToken)
                .ConfigureAwait(false);
            if (!admitted)
            {
                _states[key] = new CellState(
                    FoliageCellResidencyState.Retry,
                    desired.Tier,
                    frameSerial,
                    0UL,
                    checked(frameSerial + options.RetryDelayFrames));
                continue;
            }

            _states[key] = new CellState(
                FoliageCellResidencyState.Resident,
                desired.Tier,
                frameSerial,
                0UL,
                0UL);
            loaded++;
            scheduledBytes = checked(
                scheduledBytes + options.EstimatedCellUploadBytes);
            AdvanceGeneration();
        }

        int retired = RetireExpired(frameSerial, options);
        LastSnapshot = CreateSnapshot(
            loaded,
            retired,
            scheduledBytes,
            candidateOverflow);
        return LastSnapshot;
    }

    public void Clear()
    {
        if (_states.Count != 0)
            AdvanceGeneration();
        _states.Clear();
        _desired.Clear();
        LastSnapshot = default;
    }

    private void BuildDesired(
        IReadOnlyList<FoliagePatch> patches,
        Vector3 cameraPosition,
        in FoliageStreamingOptions options,
        out int overflow)
    {
        _desired.Clear();
        overflow = 0;
        foreach (FoliagePatch patch in patches)
        {
            if (!patch.Visible)
                continue;
            float size = FoliageCellKey.ResolveCellSize(patch);
            int sizeMillimeters = checked((int)MathF.Round(size * 1000.0f));
            int minimumX = checked((int)MathF.Floor(
                patch.Bounds.Min.X / size));
            int maximumX = checked((int)MathF.Floor(
                MathF.BitDecrement(patch.Bounds.Max.X) / size));
            int minimumZ = checked((int)MathF.Floor(
                patch.Bounds.Min.Z / size));
            int maximumZ = checked((int)MathF.Floor(
                MathF.BitDecrement(patch.Bounds.Max.Z) / size));
            maximumX = Math.Max(minimumX, maximumX);
            maximumZ = Math.Max(minimumZ, maximumZ);
            for (int z = minimumZ; z <= maximumZ; z++)
            for (int x = minimumX; x <= maximumX; x++)
            {
                if (_desired.Count >= options.MaximumCandidateCells)
                {
                    overflow++;
                    continue;
                }
                var key = new FoliageCellKey(
                    patch.Id,
                    x,
                    z,
                    sizeMillimeters);
                BoundingBox bounds = key.ResolveBounds(patch);
                float distance = DistanceToBounds(cameraPosition, bounds);
                if (distance > options.FarDistance +
                    options.HysteresisDistance)
                {
                    continue;
                }
                FoliageCellTier tier = distance <= options.NearDistance
                    ? FoliageCellTier.Near
                    : distance <= options.MidDistance
                        ? FoliageCellTier.Mid
                        : FoliageCellTier.Far;
                _desired[key] = new DesiredCell(distance, tier);
            }
        }
    }

    private void UpdateExistingStates(
        ulong frameSerial,
        in FoliageStreamingOptions options)
    {
        foreach (FoliageCellKey key in _states.Keys.ToArray())
        {
            CellState state = _states[key];
            if (_desired.TryGetValue(key, out DesiredCell desired))
            {
                _states[key] = state with
                {
                    State = state.State ==
                        FoliageCellResidencyState.Retiring
                            ? FoliageCellResidencyState.Resident
                            : state.State,
                    Tier = desired.Tier,
                    LastDesiredFrame = frameSerial,
                    RetireAfterFrame = 0UL
                };
                continue;
            }
            if (state.State is FoliageCellResidencyState.Pending or
                FoliageCellResidencyState.Retry)
            {
                _states.Remove(key);
                continue;
            }
            if (state.State == FoliageCellResidencyState.Resident &&
                frameSerial > state.LastDesiredFrame)
            {
                _states[key] = state with
                {
                    State = FoliageCellResidencyState.Retiring,
                    RetireAfterFrame = checked(
                        frameSerial + options.RetirementDelayFrames)
                };
            }
        }
    }

    private int RetireExpired(
        ulong frameSerial,
        in FoliageStreamingOptions options)
    {
        int budget = Math.Max(0, options.MaximumRetirementsPerFrame);
        int retired = 0;
        foreach (FoliageCellKey key in _states
                     .Where(pair => pair.Value.State ==
                         FoliageCellResidencyState.Retiring &&
                         frameSerial >= pair.Value.RetireAfterFrame)
                     .Select(static pair => pair.Key)
                     .Order()
                     .ToArray())
        {
            if (retired >= budget)
                break;
            _states.Remove(key);
            retired++;
            AdvanceGeneration();
        }
        return retired;
    }

    private FoliageStreamingSnapshot CreateSnapshot(
        int loaded,
        int retired,
        ulong uploadBytes,
        int overflow)
    {
        int resident = 0;
        int pending = 0;
        int retiring = 0;
        int retry = 0;
        int near = 0;
        int mid = 0;
        int far = 0;
        foreach ((_, CellState state) in _states)
        {
            switch (state.State)
            {
                case FoliageCellResidencyState.Resident:
                    resident++;
                    break;
                case FoliageCellResidencyState.Retiring:
                    resident++;
                    retiring++;
                    break;
                case FoliageCellResidencyState.Pending:
                    pending++;
                    break;
                case FoliageCellResidencyState.Retry:
                    retry++;
                    break;
            }
            if (state.State is not (FoliageCellResidencyState.Resident or
                FoliageCellResidencyState.Retiring))
                continue;
            if (state.Tier == FoliageCellTier.Near)
                near++;
            else if (state.Tier == FoliageCellTier.Mid)
                mid++;
            else
                far++;
        }
        pending += _desired.Keys.Count(key => !_states.ContainsKey(key));
        return new FoliageStreamingSnapshot(
            _generation,
            _desired.Count,
            resident,
            pending,
            retiring,
            retry,
            near,
            mid,
            far,
            loaded,
            retired,
            uploadBytes,
            overflow);
    }

    private void AdvanceGeneration()
    {
        _generation++;
        if (_generation == 0UL)
            _generation = 1UL;
    }

    private static float DistanceToBounds(
        Vector3 point,
        BoundingBox bounds)
    {
        float dx = Math.Max(
            Math.Max(bounds.Min.X - point.X, 0.0f),
            point.X - bounds.Max.X);
        float dy = Math.Max(
            Math.Max(bounds.Min.Y - point.Y, 0.0f),
            point.Y - bounds.Max.Y);
        float dz = Math.Max(
            Math.Max(bounds.Min.Z - point.Z, 0.0f),
            point.Z - bounds.Max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void Validate(in FoliageStreamingOptions options)
    {
        if (!float.IsFinite(options.NearDistance) ||
            !float.IsFinite(options.MidDistance) ||
            !float.IsFinite(options.FarDistance) ||
            options.NearDistance < 0.0f ||
            options.MidDistance < options.NearDistance ||
            options.FarDistance < options.MidDistance ||
            options.MaximumCandidateCells <= 0 ||
            options.EstimatedCellUploadBytes == 0UL)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Foliage streaming distances and budgets must be finite, monotonic, and positive.");
        }
    }

    private readonly record struct DesiredCell(
        float Distance,
        FoliageCellTier Tier);

    private readonly record struct CellState(
        FoliageCellResidencyState State,
        FoliageCellTier Tier,
        ulong LastDesiredFrame,
        ulong RetireAfterFrame,
        ulong RetryAfterFrame);
}
