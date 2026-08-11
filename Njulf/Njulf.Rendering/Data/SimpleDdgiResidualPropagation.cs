using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data;

/// <summary>
/// Why sparse transport propagation could not be used for a particular wave.
/// A non-<see cref="None"/> value is a hard request for the ordinary complete
/// sweep; sparse ordering is never allowed to become a correctness gate.
/// </summary>
public enum SimpleDdgiResidualFallbackReason : byte
{
    None = 0,
    DependencyGenerationMismatch = 1,
    DependencyBuildIncomplete = 2,
    DependencyCapacityOverflow = 3,
    InvalidResidual = 4
}

/// <summary>One conservative edge in the cached transport operator.</summary>
public readonly record struct SimpleDdgiResidualDependency(
    int DependentProbeIndex,
    float MaximumGain);

/// <summary>A probe selected by the residual-driven sparse ordering oracle.</summary>
public readonly record struct SimpleDdgiResidualWorkItem(
    int ProbeIndex,
    float ResidualUpperBound,
    float PredictedCost,
    uint DeadlineFrame,
    bool DeadlineForced)
{
    public float ErrorReductionPerCost =>
        ResidualUpperBound / MathF.Max(PredictedCost, 1.0e-6f);
}

/// <summary>CPU mirror of the one-word resident scheduler propagation hint.</summary>
public static class SimpleDdgiPackedResidualState
{
    public const uint ResidualMask = 0x0000_ffffu;
    public const int GenerationShift = 16;
    public const int DeadlineShift = 24;
    public const uint MaximumUnambiguousDeadlineFrames = 120;

    public static uint PackConservative(
        float residualUpperBound,
        uint transportGeneration,
        uint deadlineFrame)
    {
        if (!float.IsFinite(residualUpperBound) || residualUpperBound < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(residualUpperBound));

        float inflated = MathF.Min(
            residualUpperBound * 1.001f + 1.0e-6f,
            65_504.0f);
        ushort residualBits = BitConverter.HalfToUInt16Bits((Half)inflated);
        return residualBits |
            ((transportGeneration & 0xffu) << GenerationShift) |
            ((deadlineFrame & 0xffu) << DeadlineShift);
    }

    public static float DecodeResidual(uint packed) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(packed & ResidualMask));

    public static uint DecodeGeneration(uint packed) =>
        (packed >> GenerationShift) & 0xffu;

    public static uint DecodeDeadline(uint packed) => packed >> DeadlineShift;

    public static bool DeadlineReached(uint frame, uint packed)
    {
        uint deadline = DecodeDeadline(packed);
        return (((frame & 0xffu) - deadline) & 0xffu) < 0x80u;
    }
}

/// <summary>
/// Bounded reverse dependencies for the positive Simple-DDGI transport
/// operator. The GPU representation is allowed to be coarser, but it follows
/// the same rules: every active consumer must be witnessed, every edge stores
/// an upper bound, and overflow or staleness invalidates the whole structure.
/// </summary>
public sealed class SimpleDdgiResidualDependencyGraph
{
    private readonly int _probeCount;
    private readonly int _capacityPerSource;
    private readonly int[] _dependencyCounts;
    private readonly int[] _dependentProbeIndices;
    private readonly float[] _maximumGains;
    private readonly bool[] _consumerComplete;
    private uint _generation;
    private bool _building;
    private bool _complete;
    private bool _overflowed;

    public SimpleDdgiResidualDependencyGraph(
        int probeCount,
        int capacityPerSource = 32)
    {
        if (probeCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(probeCount));
        if (capacityPerSource <= 0 || capacityPerSource > 256)
            throw new ArgumentOutOfRangeException(nameof(capacityPerSource));

        _probeCount = probeCount;
        _capacityPerSource = capacityPerSource;
        _dependencyCounts = new int[probeCount];
        _dependentProbeIndices = new int[checked(probeCount * capacityPerSource)];
        _maximumGains = new float[_dependentProbeIndices.Length];
        _consumerComplete = new bool[probeCount];
        Array.Fill(_dependentProbeIndices, -1);
    }

    public int ProbeCount => _probeCount;
    public int CapacityPerSource => _capacityPerSource;
    public uint Generation => _generation;
    public bool IsComplete => _complete;
    public bool Overflowed => _overflowed;

    public void BeginBuild(uint generation)
    {
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        _generation = generation;
        _building = true;
        _complete = false;
        _overflowed = false;
        Array.Clear(_dependencyCounts);
        Array.Clear(_maximumGains);
        Array.Clear(_consumerComplete);
        Array.Fill(_dependentProbeIndices, -1);
    }

    /// <summary>
    /// Records that <paramref name="dependentProbeIndex"/> sampled
    /// <paramref name="sourceProbeIndex"/>. Duplicate edges are folded by
    /// retaining their largest proven gain.
    /// </summary>
    public bool RecordDependency(
        int sourceProbeIndex,
        int dependentProbeIndex,
        float maximumGain)
    {
        EnsureBuilding();
        ValidateProbe(sourceProbeIndex, nameof(sourceProbeIndex));
        ValidateProbe(dependentProbeIndex, nameof(dependentProbeIndex));
        if (!float.IsFinite(maximumGain) || maximumGain < 0.0f || maximumGain > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(maximumGain));
        if (maximumGain == 0.0f)
            return true;

        int baseIndex = checked(sourceProbeIndex * _capacityPerSource);
        int count = _dependencyCounts[sourceProbeIndex];
        for (int index = 0; index < count; index++)
        {
            int slot = baseIndex + index;
            if (_dependentProbeIndices[slot] != dependentProbeIndex)
                continue;

            _maximumGains[slot] = MathF.Max(_maximumGains[slot], maximumGain);
            return true;
        }

        if (count >= _capacityPerSource)
        {
            _overflowed = true;
            _complete = false;
            return false;
        }

        int destination = baseIndex + count;
        _dependentProbeIndices[destination] = dependentProbeIndex;
        _maximumGains[destination] = maximumGain;
        _dependencyCounts[sourceProbeIndex] = count + 1;
        return true;
    }

    /// <summary>
    /// Marks a consumer only after all of its cached reflected/transmitted
    /// gathers have recorded their source edges.
    /// </summary>
    public void MarkConsumerComplete(int consumerProbeIndex)
    {
        EnsureBuilding();
        ValidateProbe(consumerProbeIndex, nameof(consumerProbeIndex));
        _consumerComplete[consumerProbeIndex] = true;
    }

    /// <summary>
    /// Freezes the graph. Inactive/nonresident probes may be excluded by the
    /// participant mask, matching the complete-field tail audit denominator.
    /// </summary>
    public bool Seal(ReadOnlySpan<bool> activeParticipants)
    {
        EnsureBuilding();
        if (activeParticipants.Length != _probeCount)
            throw new ArgumentException("The participant mask must cover every probe.", nameof(activeParticipants));

        bool allConsumersComplete = true;
        for (int probeIndex = 0; probeIndex < _probeCount; probeIndex++)
        {
            if (activeParticipants[probeIndex] && !_consumerComplete[probeIndex])
            {
                allConsumersComplete = false;
                break;
            }
        }

        _building = false;
        _complete = allConsumersComplete && !_overflowed;
        return _complete;
    }

    public ReadOnlySpan<int> GetDependents(int sourceProbeIndex)
    {
        ValidateProbe(sourceProbeIndex, nameof(sourceProbeIndex));
        int count = _dependencyCounts[sourceProbeIndex];
        return _dependentProbeIndices.AsSpan(
            checked(sourceProbeIndex * _capacityPerSource),
            count);
    }

    public ReadOnlySpan<float> GetMaximumGains(int sourceProbeIndex)
    {
        ValidateProbe(sourceProbeIndex, nameof(sourceProbeIndex));
        int count = _dependencyCounts[sourceProbeIndex];
        return _maximumGains.AsSpan(
            checked(sourceProbeIndex * _capacityPerSource),
            count);
    }

    private void EnsureBuilding()
    {
        if (!_building)
            throw new InvalidOperationException("BeginBuild must be called before recording dependency data.");
    }

    private void ValidateProbe(int probeIndex, string parameterName)
    {
        if ((uint)probeIndex >= (uint)_probeCount)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// Allocation-free-per-wave residual queue used as the CPU oracle for GPU
/// sparse propagation. It uses separate score and deadline heaps so expensive
/// work cannot starve while ordinary work remains ordered by conservative
/// error reduction per predicted cost.
/// </summary>
public sealed class SimpleDdgiResidualPropagationQueue
{
    private readonly record struct QueueEntry(int ProbeIndex, uint Version);

    private readonly SimpleDdgiResidualDependencyGraph _graph;
    private readonly float[] _pendingResidual;
    private readonly float[] _predictedCost;
    private readonly uint[] _deadlineFrame;
    private readonly uint[] _versions;
    private readonly bool[] _pending;
    private readonly PriorityQueue<QueueEntry, float> _scoreQueue = new();
    private readonly PriorityQueue<QueueEntry, uint> _deadlineQueue = new();
    private uint _generation;
    private float _errorBudget;
    private uint _starvationFrames;
    private int _pendingCount;

    public SimpleDdgiResidualPropagationQueue(
        SimpleDdgiResidualDependencyGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _pendingResidual = new float[graph.ProbeCount];
        _predictedCost = new float[graph.ProbeCount];
        _deadlineFrame = new uint[graph.ProbeCount];
        _versions = new uint[graph.ProbeCount];
        _pending = new bool[graph.ProbeCount];
    }

    public int PendingCount => _pendingCount;
    public ulong SeededCount { get; private set; }
    public ulong EnqueuedDependentCount { get; private set; }
    public ulong ThresholdRejectedCount { get; private set; }
    public bool CompleteAuditRequired => true;
    public SimpleDdgiResidualFallbackReason FallbackReason { get; private set; }
    public bool FullSweepRequired => FallbackReason != SimpleDdgiResidualFallbackReason.None;

    public void BeginWave(
        uint dependencyGeneration,
        float finalErrorBudget,
        uint starvationFrames = 30)
    {
        if (dependencyGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(dependencyGeneration));
        if (!float.IsFinite(finalErrorBudget) || finalErrorBudget < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(finalErrorBudget));
        if (starvationFrames == 0 || starvationFrames >= 0x8000_0000u)
            throw new ArgumentOutOfRangeException(nameof(starvationFrames));

        _generation = dependencyGeneration;
        _errorBudget = finalErrorBudget;
        _starvationFrames = starvationFrames;
        _pendingCount = 0;
        SeededCount = 0;
        EnqueuedDependentCount = 0;
        ThresholdRejectedCount = 0;
        FallbackReason = ResolveFallbackReason(dependencyGeneration);
        Array.Clear(_pendingResidual);
        Array.Clear(_predictedCost);
        Array.Clear(_deadlineFrame);
        Array.Clear(_pending);
        _scoreQueue.Clear();
        _deadlineQueue.Clear();
    }

    public bool Seed(
        int probeIndex,
        float measuredIrradianceChange,
        float predictedCost,
        uint currentFrame)
    {
        SeededCount++;
        return Enqueue(
            probeIndex,
            measuredIrradianceChange,
            predictedCost,
            currentFrame + _starvationFrames,
            isDependent: false);
    }

    /// <summary>
    /// Completes one sparse evaluation and propagates its measured change
    /// through the frozen reverse graph. The graph gain is an upper bound, so
    /// rejecting an edge below the final budget cannot hide an above-budget
    /// downstream effect in this positive contraction.
    /// </summary>
    public void CompleteAndPropagate(
        in SimpleDdgiResidualWorkItem completed,
        float measuredIrradianceChange,
        Func<int, float> predictedCost,
        uint currentFrame)
    {
        if (predictedCost == null)
            throw new ArgumentNullException(nameof(predictedCost));
        ValidateProbe(completed.ProbeIndex);
        RemovePending(completed.ProbeIndex);

        if (!float.IsFinite(measuredIrradianceChange) || measuredIrradianceChange < 0.0f)
        {
            FallbackReason = SimpleDdgiResidualFallbackReason.InvalidResidual;
            return;
        }
        if (FullSweepRequired || measuredIrradianceChange == 0.0f)
            return;

        ReadOnlySpan<int> dependents = _graph.GetDependents(completed.ProbeIndex);
        ReadOnlySpan<float> gains = _graph.GetMaximumGains(completed.ProbeIndex);
        for (int index = 0; index < dependents.Length; index++)
        {
            float propagatedBound = measuredIrradianceChange * gains[index];
            if (propagatedBound <= _errorBudget)
            {
                ThresholdRejectedCount++;
                continue;
            }

            float cost = predictedCost(dependents[index]);
            if (Enqueue(
                    dependents[index],
                    propagatedBound,
                    cost,
                    currentFrame + _starvationFrames,
                    isDependent: true))
            {
                EnqueuedDependentCount++;
            }
        }
    }

    public bool TryDequeue(uint currentFrame, out SimpleDdgiResidualWorkItem work)
    {
        while (_deadlineQueue.TryPeek(out QueueEntry entry, out uint deadline))
        {
            if (!IsCurrent(entry))
            {
                _deadlineQueue.Dequeue();
                continue;
            }
            if (!FrameReached(currentFrame, deadline))
                break;

            _deadlineQueue.Dequeue();
            work = BuildWork(entry.ProbeIndex, deadlineForced: true);
            return true;
        }

        while (_scoreQueue.TryDequeue(out QueueEntry entry, out _))
        {
            if (!IsCurrent(entry))
                continue;
            work = BuildWork(entry.ProbeIndex, deadlineForced: false);
            return true;
        }

        work = default;
        return false;
    }

    private bool Enqueue(
        int probeIndex,
        float residual,
        float predictedCost,
        uint deadlineFrame,
        bool isDependent)
    {
        ValidateProbe(probeIndex);
        if (!float.IsFinite(residual) || residual < 0.0f ||
            !float.IsFinite(predictedCost) || predictedCost <= 0.0f)
        {
            FallbackReason = SimpleDdgiResidualFallbackReason.InvalidResidual;
            return false;
        }
        if (residual <= _errorBudget && isDependent)
        {
            ThresholdRejectedCount++;
            return false;
        }

        bool wasPending = _pending[probeIndex];
        _pending[probeIndex] = true;
        _pendingResidual[probeIndex] = MathF.Max(
            _pendingResidual[probeIndex],
            residual);
        _predictedCost[probeIndex] = wasPending
            ? MathF.Min(_predictedCost[probeIndex], predictedCost)
            : predictedCost;
        _deadlineFrame[probeIndex] = wasPending &&
            FrameReached(deadlineFrame, _deadlineFrame[probeIndex])
                ? _deadlineFrame[probeIndex]
                : deadlineFrame;
        if (!wasPending)
            _pendingCount++;

        uint version = ++_versions[probeIndex];
        var entry = new QueueEntry(probeIndex, version);
        float score = _pendingResidual[probeIndex] /
            MathF.Max(_predictedCost[probeIndex], 1.0e-6f);
        _scoreQueue.Enqueue(entry, -score);
        _deadlineQueue.Enqueue(entry, _deadlineFrame[probeIndex]);
        return true;
    }

    private SimpleDdgiResidualWorkItem BuildWork(
        int probeIndex,
        bool deadlineForced) =>
        new(
            probeIndex,
            _pendingResidual[probeIndex],
            _predictedCost[probeIndex],
            _deadlineFrame[probeIndex],
            deadlineForced);

    private bool IsCurrent(QueueEntry entry) =>
        _pending[entry.ProbeIndex] &&
        _versions[entry.ProbeIndex] == entry.Version;

    private void RemovePending(int probeIndex)
    {
        if (!_pending[probeIndex])
            return;
        _pending[probeIndex] = false;
        _pendingCount--;
        _versions[probeIndex]++;
    }

    private SimpleDdgiResidualFallbackReason ResolveFallbackReason(uint generation)
    {
        if (_graph.Generation != generation)
            return SimpleDdgiResidualFallbackReason.DependencyGenerationMismatch;
        if (_graph.Overflowed)
            return SimpleDdgiResidualFallbackReason.DependencyCapacityOverflow;
        return _graph.IsComplete
            ? SimpleDdgiResidualFallbackReason.None
            : SimpleDdgiResidualFallbackReason.DependencyBuildIncomplete;
    }

    private void ValidateProbe(int probeIndex)
    {
        if ((uint)probeIndex >= (uint)_pending.Length)
            throw new ArgumentOutOfRangeException(nameof(probeIndex));
    }

    private static bool FrameReached(uint frame, uint target) =>
        frame - target < 0x8000_0000u;
}
