using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

[Flags]
public enum SimpleDdgiMutationClass : byte
{
    None = 0,
    Environment = 1 << 0,
    Light = 1 << 1,
    Emissive = 1 << 2,
    Material = 1 << 3,
    Transform = 1 << 4,
    Topology = 1 << 5
}

public readonly record struct SimpleDdgiLatencyDistribution(
    int SampleCount,
    int P50Frames,
    int P95Frames,
    int P99Frames,
    int MaximumFrames,
    int CensoredCount);

public readonly record struct SimpleDdgiMutationLatencySnapshot(
    SimpleDdgiMutationClass MutationClass,
    SimpleDdgiLatencyDistribution FirstVisibleResponse,
    SimpleDdgiLatencyDistribution CertifiedConvergence,
    bool EventPending,
    bool FirstResponsePending)
{
    /// <summary>
    /// Visual settling for probes influenced by this mutation. This is the
    /// bounded interactive checkpoint; it is deliberately independent of the
    /// much more expensive full-field fixed-point certificate.
    /// </summary>
    public SimpleDdgiLatencyDistribution AffectedRegionConvergence { get; init; }
    public bool AffectedRegionConvergencePending { get; init; }
    public ulong ActiveMutationGeneration { get; init; }
}

/// <summary>
/// Scene-attachment/bootstrap evidence. Bootstrap work is intentionally kept
/// out of per-class runtime distributions because field construction and a
/// settled edit have different convergence envelopes.
/// </summary>
public readonly record struct SimpleDdgiColdStartLatencySnapshot(
    SimpleDdgiLatencyDistribution FirstVisibleResponse,
    SimpleDdgiLatencyDistribution AffectedRegionConvergence,
    SimpleDdgiLatencyDistribution CertifiedConvergence,
    bool EventPending,
    bool FirstResponsePending,
    bool AffectedRegionConvergencePending,
    ulong ActiveMutationGeneration);

/// <summary>
/// Frame-aligned latency evidence for every production mutation class. Keeping
/// the six distributions in one value prevents capture and overlay consumers
/// from accidentally publishing only a subset of the qualification contract.
/// </summary>
public readonly record struct SimpleDdgiMutationLatencyTelemetry(
    SimpleDdgiMutationLatencySnapshot Environment,
    SimpleDdgiMutationLatencySnapshot Light,
    SimpleDdgiMutationLatencySnapshot Emissive,
    SimpleDdgiMutationLatencySnapshot Material,
    SimpleDdgiMutationLatencySnapshot Transform,
    SimpleDdgiMutationLatencySnapshot Topology)
{
    public SimpleDdgiColdStartLatencySnapshot ColdStart { get; init; }

    public static SimpleDdgiMutationLatencyTelemetry Empty { get; } = new(
        EmptySnapshot(SimpleDdgiMutationClass.Environment),
        EmptySnapshot(SimpleDdgiMutationClass.Light),
        EmptySnapshot(SimpleDdgiMutationClass.Emissive),
        EmptySnapshot(SimpleDdgiMutationClass.Material),
        EmptySnapshot(SimpleDdgiMutationClass.Transform),
        EmptySnapshot(SimpleDdgiMutationClass.Topology));

    public IEnumerable<SimpleDdgiMutationLatencySnapshot> Enumerate()
    {
        yield return Environment;
        yield return Light;
        yield return Emissive;
        yield return Material;
        yield return Transform;
        yield return Topology;
    }

    public SimpleDdgiMutationLatencyTelemetry NormalizeForPersistence() => new(
        Normalize(Environment, SimpleDdgiMutationClass.Environment),
        Normalize(Light, SimpleDdgiMutationClass.Light),
        Normalize(Emissive, SimpleDdgiMutationClass.Emissive),
        Normalize(Material, SimpleDdgiMutationClass.Material),
        Normalize(Transform, SimpleDdgiMutationClass.Transform),
        Normalize(Topology, SimpleDdgiMutationClass.Topology))
    {
        ColdStart = ColdStart
    };

    private static SimpleDdgiMutationLatencySnapshot EmptySnapshot(
        SimpleDdgiMutationClass mutationClass) =>
        new(mutationClass, default, default, false, false);

    private static SimpleDdgiMutationLatencySnapshot Normalize(
        SimpleDdgiMutationLatencySnapshot snapshot,
        SimpleDdgiMutationClass expectedClass) =>
        snapshot.MutationClass == expectedClass
            ? snapshot
            : EmptySnapshot(expectedClass);
}

/// <summary>
/// Bounded, allocation-free latency evidence for the six production mutation
/// classes. A newer edit supersedes an unfinished class transaction and is
/// counted as censored; only checkpoints correlated with the mutation
/// generation submitted to the GPU may close that transaction.
/// </summary>
public sealed class SimpleDdgiMutationLatencyTracker
{
    public const int BucketCount = 4_096;
    public const int MinimumP95SampleCount = 20;
    private const int ClassCount = 6;
    private const byte AwaitingFirstVisible = 1 << 0;
    private const byte AwaitingAffectedRegion = 1 << 1;
    private const byte AwaitingCertificate = 1 << 2;
    private const byte AwaitingAll =
        AwaitingFirstVisible | AwaitingAffectedRegion | AwaitingCertificate;

    private readonly uint[] _startFrames = new uint[ClassCount];
    private readonly ulong[] _generations = new ulong[ClassCount];
    private readonly byte[] _states = new byte[ClassCount];
    private readonly uint[] _firstBuckets = new uint[ClassCount * BucketCount];
    private readonly uint[] _affectedBuckets = new uint[ClassCount * BucketCount];
    private readonly uint[] _certifiedBuckets = new uint[ClassCount * BucketCount];
    private readonly uint[] _firstSamples = new uint[ClassCount];
    private readonly uint[] _affectedSamples = new uint[ClassCount];
    private readonly uint[] _certifiedSamples = new uint[ClassCount];
    private readonly uint[] _firstCensored = new uint[ClassCount];
    private readonly uint[] _affectedCensored = new uint[ClassCount];
    private readonly uint[] _certifiedCensored = new uint[ClassCount];
    private readonly uint[] _firstMaximum = new uint[ClassCount];
    private readonly uint[] _affectedMaximum = new uint[ClassCount];
    private readonly uint[] _certifiedMaximum = new uint[ClassCount];

    private readonly uint[] _coldFirstBuckets = new uint[BucketCount];
    private readonly uint[] _coldAffectedBuckets = new uint[BucketCount];
    private readonly uint[] _coldCertifiedBuckets = new uint[BucketCount];
    private uint _coldStartFrame;
    private ulong _coldGeneration;
    private byte _coldState;
    private uint _coldFirstSamples;
    private uint _coldAffectedSamples;
    private uint _coldCertifiedSamples;
    private uint _coldFirstCensored;
    private uint _coldAffectedCensored;
    private uint _coldCertifiedCensored;
    private uint _coldFirstMaximum;
    private uint _coldAffectedMaximum;
    private uint _coldCertifiedMaximum;
    private ulong _latestGeneration;

    public ulong LatestGeneration => _latestGeneration;

    public ulong Begin(
        SimpleDdgiMutationClass classes,
        uint frame,
        bool coldStart = false)
    {
        if (classes == SimpleDdgiMutationClass.None)
            return _latestGeneration;

        ulong generation = NextGeneration();
        if (coldStart)
        {
            BeginColdStart(frame, generation);
            return generation;
        }

        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            SimpleDdgiMutationClass value = FromIndex(classIndex);
            if ((classes & value) == 0)
                continue;

            CensorPendingClass(classIndex);
            _startFrames[classIndex] = frame;
            _generations[classIndex] = generation;
            _states[classIndex] = AwaitingAll;
        }
        return generation;
    }

    public void RecordFirstVisibleResponse(uint frame) =>
        RecordFirstVisibleResponse(_latestGeneration, frame);

    public void RecordFirstVisibleResponse(ulong mutationGeneration, uint frame)
    {
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            if (!CanRecord(classIndex, mutationGeneration, AwaitingFirstVisible))
                continue;
            Record(
                _firstBuckets,
                classIndex,
                unchecked(frame - _startFrames[classIndex]),
                ref _firstSamples[classIndex],
                ref _firstMaximum[classIndex]);
            _states[classIndex] &= unchecked((byte)~AwaitingFirstVisible);
        }

        if (CanRecordCold(mutationGeneration, AwaitingFirstVisible))
        {
            RecordSingle(
                _coldFirstBuckets,
                unchecked(frame - _coldStartFrame),
                ref _coldFirstSamples,
                ref _coldFirstMaximum);
            _coldState &= unchecked((byte)~AwaitingFirstVisible);
        }
    }

    public void RecordAffectedRegionConvergence(uint frame) =>
        RecordAffectedRegionConvergence(_latestGeneration, frame);

    public void RecordAffectedRegionConvergence(
        ulong mutationGeneration,
        uint frame)
    {
        // Settled affected probes necessarily have a receiver-visible payload.
        // If delayed evidence missed publication, retain a conservative sample
        // rather than silently manufacturing an unavailable first checkpoint.
        RecordFirstVisibleResponse(mutationGeneration, frame);
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            if (!CanRecord(classIndex, mutationGeneration, AwaitingAffectedRegion))
                continue;
            Record(
                _affectedBuckets,
                classIndex,
                unchecked(frame - _startFrames[classIndex]),
                ref _affectedSamples[classIndex],
                ref _affectedMaximum[classIndex]);
            _states[classIndex] &= unchecked((byte)~AwaitingAffectedRegion);
        }

        if (CanRecordCold(mutationGeneration, AwaitingAffectedRegion))
        {
            RecordSingle(
                _coldAffectedBuckets,
                unchecked(frame - _coldStartFrame),
                ref _coldAffectedSamples,
                ref _coldAffectedMaximum);
            _coldState &= unchecked((byte)~AwaitingAffectedRegion);
        }
    }

    public void RecordCertifiedConvergence(uint frame) =>
        RecordCertifiedConvergence(_latestGeneration, frame);

    public void RecordCertifiedConvergence(ulong mutationGeneration, uint frame)
    {
        // A valid full-field certificate implies both earlier contracts. Missing
        // telemetry is backfilled conservatively at the certificate frame.
        RecordAffectedRegionConvergence(mutationGeneration, frame);
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            if (!CanRecord(classIndex, mutationGeneration, AwaitingCertificate))
                continue;
            Record(
                _certifiedBuckets,
                classIndex,
                unchecked(frame - _startFrames[classIndex]),
                ref _certifiedSamples[classIndex],
                ref _certifiedMaximum[classIndex]);
            _states[classIndex] &= unchecked((byte)~AwaitingCertificate);
            RetireClassIfComplete(classIndex);
        }

        if (CanRecordCold(mutationGeneration, AwaitingCertificate))
        {
            RecordSingle(
                _coldCertifiedBuckets,
                unchecked(frame - _coldStartFrame),
                ref _coldCertifiedSamples,
                ref _coldCertifiedMaximum);
            _coldState &= unchecked((byte)~AwaitingCertificate);
            if (_coldState == 0)
            {
                _coldStartFrame = 0u;
                _coldGeneration = 0UL;
            }
        }
    }

    public SimpleDdgiMutationLatencySnapshot GetSnapshot(
        SimpleDdgiMutationClass mutationClass)
    {
        int classIndex = ToIndex(mutationClass);
        return new SimpleDdgiMutationLatencySnapshot(
            mutationClass,
            Distribution(
                _firstBuckets,
                classIndex,
                _firstSamples[classIndex],
                _firstMaximum[classIndex],
                _firstCensored[classIndex]),
            Distribution(
                _certifiedBuckets,
                classIndex,
                _certifiedSamples[classIndex],
                _certifiedMaximum[classIndex],
                _certifiedCensored[classIndex]),
            _states[classIndex] != 0,
            (_states[classIndex] & AwaitingFirstVisible) != 0)
        {
            AffectedRegionConvergence = Distribution(
                _affectedBuckets,
                classIndex,
                _affectedSamples[classIndex],
                _affectedMaximum[classIndex],
                _affectedCensored[classIndex]),
            AffectedRegionConvergencePending =
                (_states[classIndex] & AwaitingAffectedRegion) != 0,
            ActiveMutationGeneration = _generations[classIndex]
        };
    }

    public SimpleDdgiColdStartLatencySnapshot GetColdStartSnapshot() => new(
        SingleDistribution(
            _coldFirstBuckets,
            _coldFirstSamples,
            _coldFirstMaximum,
            _coldFirstCensored),
        SingleDistribution(
            _coldAffectedBuckets,
            _coldAffectedSamples,
            _coldAffectedMaximum,
            _coldAffectedCensored),
        SingleDistribution(
            _coldCertifiedBuckets,
            _coldCertifiedSamples,
            _coldCertifiedMaximum,
            _coldCertifiedCensored),
        _coldState != 0,
        (_coldState & AwaitingFirstVisible) != 0,
        (_coldState & AwaitingAffectedRegion) != 0,
        _coldGeneration);

    public SimpleDdgiMutationLatencyTelemetry GetTelemetry() => new(
        GetSnapshot(SimpleDdgiMutationClass.Environment),
        GetSnapshot(SimpleDdgiMutationClass.Light),
        GetSnapshot(SimpleDdgiMutationClass.Emissive),
        GetSnapshot(SimpleDdgiMutationClass.Material),
        GetSnapshot(SimpleDdgiMutationClass.Transform),
        GetSnapshot(SimpleDdgiMutationClass.Topology))
    {
        ColdStart = GetColdStartSnapshot()
    };

    public void ResetActive()
    {
        Array.Clear(_startFrames);
        Array.Clear(_generations);
        Array.Clear(_states);
        _coldStartFrame = 0u;
        _coldGeneration = 0UL;
        _coldState = 0;
    }

    internal static int CalculatePercentile(
        ReadOnlySpan<uint> buckets,
        uint sampleCount,
        float percentile)
    {
        if (sampleCount == 0u || buckets.IsEmpty)
            return 0;
        percentile = Math.Clamp(percentile, 0.0f, 1.0f);
        ulong target = Math.Max(
            1UL,
            (ulong)Math.Ceiling(sampleCount * (double)percentile));
        ulong cumulative = 0UL;
        for (int index = 0; index < buckets.Length; index++)
        {
            cumulative += buckets[index];
            if (cumulative >= target)
                return index;
        }
        return buckets.Length - 1;
    }

    private void BeginColdStart(uint frame, ulong generation)
    {
        if ((_coldState & AwaitingFirstVisible) != 0)
            SaturatingIncrement(ref _coldFirstCensored);
        if ((_coldState & AwaitingAffectedRegion) != 0)
            SaturatingIncrement(ref _coldAffectedCensored);
        if ((_coldState & AwaitingCertificate) != 0)
            SaturatingIncrement(ref _coldCertifiedCensored);
        _coldStartFrame = frame;
        _coldGeneration = generation;
        _coldState = AwaitingAll;
    }

    private void CensorPendingClass(int classIndex)
    {
        if ((_states[classIndex] & AwaitingFirstVisible) != 0)
            SaturatingIncrement(ref _firstCensored[classIndex]);
        if ((_states[classIndex] & AwaitingAffectedRegion) != 0)
            SaturatingIncrement(ref _affectedCensored[classIndex]);
        if ((_states[classIndex] & AwaitingCertificate) != 0)
            SaturatingIncrement(ref _certifiedCensored[classIndex]);
    }

    private bool CanRecord(
        int classIndex,
        ulong mutationGeneration,
        byte checkpoint) =>
        mutationGeneration != 0UL &&
        (_states[classIndex] & checkpoint) != 0 &&
        _generations[classIndex] != 0UL &&
        _generations[classIndex] <= mutationGeneration;

    private bool CanRecordCold(ulong mutationGeneration, byte checkpoint) =>
        mutationGeneration != 0UL &&
        (_coldState & checkpoint) != 0 &&
        _coldGeneration != 0UL &&
        _coldGeneration <= mutationGeneration;

    private void RetireClassIfComplete(int classIndex)
    {
        if (_states[classIndex] != 0)
            return;
        _startFrames[classIndex] = 0u;
        _generations[classIndex] = 0UL;
    }

    private ulong NextGeneration()
    {
        _latestGeneration++;
        if (_latestGeneration == 0UL)
            _latestGeneration = 1UL;
        return _latestGeneration;
    }

    private static void Record(
        uint[] buckets,
        int classIndex,
        uint elapsedFrames,
        ref uint samples,
        ref uint maximum)
    {
        int bucket = elapsedFrames >= BucketCount - 1
            ? BucketCount - 1
            : checked((int)elapsedFrames);
        int offset = checked(classIndex * BucketCount + bucket);
        SaturatingIncrement(ref buckets[offset]);
        SaturatingIncrement(ref samples);
        maximum = Math.Max(maximum, elapsedFrames);
    }

    private static void RecordSingle(
        uint[] buckets,
        uint elapsedFrames,
        ref uint samples,
        ref uint maximum)
    {
        int bucket = elapsedFrames >= BucketCount - 1
            ? BucketCount - 1
            : checked((int)elapsedFrames);
        SaturatingIncrement(ref buckets[bucket]);
        SaturatingIncrement(ref samples);
        maximum = Math.Max(maximum, elapsedFrames);
    }

    private static SimpleDdgiLatencyDistribution Distribution(
        uint[] buckets,
        int classIndex,
        uint samples,
        uint maximum,
        uint censored)
    {
        ReadOnlySpan<uint> slice = buckets.AsSpan(
            checked(classIndex * BucketCount),
            BucketCount);
        return CreateDistribution(slice, samples, maximum, censored);
    }

    private static SimpleDdgiLatencyDistribution SingleDistribution(
        uint[] buckets,
        uint samples,
        uint maximum,
        uint censored) =>
        CreateDistribution(buckets, samples, maximum, censored);

    private static SimpleDdgiLatencyDistribution CreateDistribution(
        ReadOnlySpan<uint> buckets,
        uint samples,
        uint maximum,
        uint censored) =>
        new(
            ClampToInt(samples),
            CalculatePercentile(buckets, samples, 0.50f),
            CalculatePercentile(buckets, samples, 0.95f),
            CalculatePercentile(buckets, samples, 0.99f),
            ClampToInt(maximum),
            ClampToInt(censored));

    private static int ToIndex(SimpleDdgiMutationClass mutationClass) =>
        mutationClass switch
        {
            SimpleDdgiMutationClass.Environment => 0,
            SimpleDdgiMutationClass.Light => 1,
            SimpleDdgiMutationClass.Emissive => 2,
            SimpleDdgiMutationClass.Material => 3,
            SimpleDdgiMutationClass.Transform => 4,
            SimpleDdgiMutationClass.Topology => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(mutationClass))
        };

    private static SimpleDdgiMutationClass FromIndex(int classIndex) =>
        (SimpleDdgiMutationClass)(1 << classIndex);

    private static int ClampToInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : checked((int)value);

    private static void SaturatingIncrement(ref uint value)
    {
        if (value < uint.MaxValue)
            value++;
    }
}
