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
    bool FirstResponsePending);

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
        Normalize(Topology, SimpleDdgiMutationClass.Topology));

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
/// counted as censored; only a response and certificate belonging to the latest
/// settled edit enter percentile evidence.
/// </summary>
public sealed class SimpleDdgiMutationLatencyTracker
{
    public const int BucketCount = 4_096;
    private const int ClassCount = 6;

    private readonly uint[] _startFrames = new uint[ClassCount];
    // 0 = idle, 1 = awaiting first visible response, 2 = awaiting certificate.
    private readonly byte[] _states = new byte[ClassCount];
    private readonly uint[] _firstBuckets = new uint[ClassCount * BucketCount];
    private readonly uint[] _certifiedBuckets = new uint[ClassCount * BucketCount];
    private readonly uint[] _firstSamples = new uint[ClassCount];
    private readonly uint[] _certifiedSamples = new uint[ClassCount];
    private readonly uint[] _firstCensored = new uint[ClassCount];
    private readonly uint[] _certifiedCensored = new uint[ClassCount];
    private readonly uint[] _firstMaximum = new uint[ClassCount];
    private readonly uint[] _certifiedMaximum = new uint[ClassCount];

    public void Begin(SimpleDdgiMutationClass classes, uint frame)
    {
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            SimpleDdgiMutationClass value = FromIndex(classIndex);
            if ((classes & value) == 0)
                continue;

            if (_states[classIndex] == 1)
                SaturatingIncrement(ref _firstCensored[classIndex]);
            if (_states[classIndex] != 0)
                SaturatingIncrement(ref _certifiedCensored[classIndex]);
            _startFrames[classIndex] = frame;
            _states[classIndex] = 1;
        }
    }

    public void RecordFirstVisibleResponse(uint frame)
    {
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            if (_states[classIndex] != 1)
                continue;
            Record(
                _firstBuckets,
                classIndex,
                unchecked(frame - _startFrames[classIndex]),
                ref _firstSamples[classIndex],
                ref _firstMaximum[classIndex]);
            _states[classIndex] = 2;
        }
    }

    public void RecordCertifiedConvergence(uint frame)
    {
        // A valid certificate necessarily implies a receiver-visible coherent
        // payload, even if delayed telemetry missed the earlier publication.
        RecordFirstVisibleResponse(frame);
        for (int classIndex = 0; classIndex < ClassCount; classIndex++)
        {
            if (_states[classIndex] != 2)
                continue;
            Record(
                _certifiedBuckets,
                classIndex,
                unchecked(frame - _startFrames[classIndex]),
                ref _certifiedSamples[classIndex],
                ref _certifiedMaximum[classIndex]);
            _states[classIndex] = 0;
            _startFrames[classIndex] = 0u;
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
            _states[classIndex] == 1);
    }

    public SimpleDdgiMutationLatencyTelemetry GetTelemetry() => new(
        GetSnapshot(SimpleDdgiMutationClass.Environment),
        GetSnapshot(SimpleDdgiMutationClass.Light),
        GetSnapshot(SimpleDdgiMutationClass.Emissive),
        GetSnapshot(SimpleDdgiMutationClass.Material),
        GetSnapshot(SimpleDdgiMutationClass.Transform),
        GetSnapshot(SimpleDdgiMutationClass.Topology));

    public void ResetActive()
    {
        Array.Clear(_startFrames);
        Array.Clear(_states);
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
        return new SimpleDdgiLatencyDistribution(
            ClampToInt(samples),
            CalculatePercentile(slice, samples, 0.50f),
            CalculatePercentile(slice, samples, 0.95f),
            CalculatePercentile(slice, samples, 0.99f),
            ClampToInt(maximum),
            ClampToInt(censored));
    }

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
