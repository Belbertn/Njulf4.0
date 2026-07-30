using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

[Flags]
public enum DdgiEmissiveSourceFlags : uint
{
    None = 0,
    Triangle = 1u << 0,
    DoubleSided = 1u << 1,
    AlphaCoverageApproximation = 1u << 2,
    DynamicTransform = 1u << 3,
    ProxyRollback = 1u << 4
}

public readonly record struct DdgiEmissiveTriangleCandidate(
    Vector3 Vertex0,
    Vector3 Vertex1,
    Vector3 Vertex2,
    Vector3 CoveredMeanRadiance,
    DdgiEmissiveSourceFlags Flags,
    ulong StableKey);

public readonly record struct DdgiEmissiveTriangleTableStats(
    int CandidateCount,
    int SelectedCount,
    double TotalImportance,
    double SelectedImportance,
    double SkippedImportance)
{
    public float SkippedEnergyFraction => TotalImportance > 0.0
        ? (float)Math.Clamp(SkippedImportance / TotalImportance, 0.0, 1.0)
        : 0.0f;
}

/// <summary>
/// Deterministic, bounded emissive-triangle selection and Vose alias-table
/// construction. Selection is proportional to emitted power (covered mean
/// luminance times world area); the GPU divides by the exact stored triangle
/// probability and area, preserving an unbiased one-sample estimator.
/// </summary>
public static class DdgiEmissiveTriangleTable
{
    public const int MaximumAliasEntryCount = ushort.MaxValue;
    public const uint AliasIndexMask = 0x0000_FFFFu;
    public const int FlagsShift = 16;

    public static DdgiEmissiveTriangleTableStats IncludeExcluded(
        DdgiEmissiveTriangleTableStats retained,
        int excludedCandidateCount,
        double excludedImportance)
    {
        if (excludedCandidateCount < 0)
            throw new ArgumentOutOfRangeException(nameof(excludedCandidateCount));
        if (!double.IsFinite(excludedImportance) || excludedImportance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(excludedImportance));

        int aggregateCandidateCount = (int)Math.Min(
            (long)retained.CandidateCount + excludedCandidateCount,
            int.MaxValue);
        double aggregateTotalImportance = retained.TotalImportance + excludedImportance;
        if (!double.IsFinite(aggregateTotalImportance))
            aggregateTotalImportance = double.MaxValue;
        return new DdgiEmissiveTriangleTableStats(
            aggregateCandidateCount,
            retained.SelectedCount,
            aggregateTotalImportance,
            retained.SelectedImportance,
            Math.Max(aggregateTotalImportance - retained.SelectedImportance, 0.0));
    }

    public static DdgiEmissiveTriangleTableStats Build(
        IEnumerable<DdgiEmissiveTriangleCandidate> candidates,
        Span<GPUDdgiEmissiveSource> destination)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (destination.Length > MaximumAliasEntryCount)
            throw new ArgumentOutOfRangeException(nameof(destination));
        if (destination.Length == 0)
            return default;

        var selected = new PriorityQueue<WeightedCandidate, (float Importance, ulong ReverseStableKey)>(
            destination.Length);
        int candidateCount = 0;
        double totalImportance = 0.0;
        foreach (DdgiEmissiveTriangleCandidate candidate in candidates)
        {
            if (!TryMeasure(candidate, out float area, out float importance))
                continue;

            candidateCount++;
            totalImportance += importance;
            var weighted = new WeightedCandidate(candidate, area, importance);
            var priority = (importance, ulong.MaxValue - candidate.StableKey);
            if (selected.Count < destination.Length)
            {
                selected.Enqueue(weighted, priority);
                continue;
            }

            selected.TryPeek(out _, out (float Importance, ulong ReverseStableKey) weakest);
            if (priority.CompareTo(weakest) <= 0)
                continue;

            selected.Dequeue();
            selected.Enqueue(weighted, priority);
        }

        if (selected.Count == 0)
            return new DdgiEmissiveTriangleTableStats(candidateCount, 0, totalImportance, 0.0, totalImportance);

        var entries = new WeightedCandidate[selected.Count];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = selected.Dequeue();
        Array.Sort(entries, static (left, right) =>
        {
            int importance = right.Importance.CompareTo(left.Importance);
            return importance != 0
                ? importance
                : left.Candidate.StableKey.CompareTo(right.Candidate.StableKey);
        });

        double selectedImportance = 0.0;
        for (int i = 0; i < entries.Length; i++)
            selectedImportance += entries[i].Importance;

        var aliasThreshold = new float[entries.Length];
        var aliasIndex = new int[entries.Length];
        BuildAliasTable(entries, selectedImportance, aliasThreshold, aliasIndex);

        for (int i = 0; i < entries.Length; i++)
        {
            WeightedCandidate entry = entries[i];
            Vector3 edge1 = entry.Candidate.Vertex1 - entry.Candidate.Vertex0;
            Vector3 edge2 = entry.Candidate.Vertex2 - entry.Candidate.Vertex0;
            float selectionProbability = (float)(entry.Importance / selectedImportance);
            uint packedAliasFlags =
                ((uint)aliasIndex[i] & AliasIndexMask) |
                ((uint)entry.Candidate.Flags << FlagsShift);
            destination[i] = new GPUDdgiEmissiveSource
            {
                Vertex0Area = new Vector4(
                    entry.Candidate.Vertex0.X,
                    entry.Candidate.Vertex0.Y,
                    entry.Candidate.Vertex0.Z,
                    entry.Area),
                Edge1AliasProbability = new Vector4(
                    edge1.X,
                    edge1.Y,
                    edge1.Z,
                    aliasThreshold[i]),
                Edge2AliasFlags = new Vector4(
                    edge2.X,
                    edge2.Y,
                    edge2.Z,
                    BitConverter.UInt32BitsToSingle(packedAliasFlags)),
                RadianceSelectionProbability = new Vector4(
                    entry.Candidate.CoveredMeanRadiance.X,
                    entry.Candidate.CoveredMeanRadiance.Y,
                    entry.Candidate.CoveredMeanRadiance.Z,
                    selectionProbability)
            };
        }

        return new DdgiEmissiveTriangleTableStats(
            candidateCount,
            entries.Length,
            totalImportance,
            selectedImportance,
            Math.Max(totalImportance - selectedImportance, 0.0));
    }

    public static uint DecodeAliasIndex(GPUDdgiEmissiveSource source) =>
        BitConverter.SingleToUInt32Bits(source.Edge2AliasFlags.W) & AliasIndexMask;

    public static DdgiEmissiveSourceFlags DecodeFlags(GPUDdgiEmissiveSource source) =>
        (DdgiEmissiveSourceFlags)(
            BitConverter.SingleToUInt32Bits(source.Edge2AliasFlags.W) >> FlagsShift);

    private static bool TryMeasure(
        DdgiEmissiveTriangleCandidate candidate,
        out float area,
        out float importance)
    {
        area = 0.5f * Vector3.Cross(
            candidate.Vertex1 - candidate.Vertex0,
            candidate.Vertex2 - candidate.Vertex0).Length();
        float luminance =
            0.2126f * Math.Max(candidate.CoveredMeanRadiance.X, 0.0f) +
            0.7152f * Math.Max(candidate.CoveredMeanRadiance.Y, 0.0f) +
            0.0722f * Math.Max(candidate.CoveredMeanRadiance.Z, 0.0f);
        float sideWeight = (candidate.Flags & DdgiEmissiveSourceFlags.DoubleSided) != 0
            ? 2.0f
            : 1.0f;
        importance = area * luminance * sideWeight;
        return float.IsFinite(area) &&
               float.IsFinite(importance) &&
               area > 1e-10f &&
               importance > 1e-10f;
    }

    private static void BuildAliasTable(
        IReadOnlyList<WeightedCandidate> entries,
        double totalImportance,
        Span<float> thresholds,
        Span<int> aliases)
    {
        int count = entries.Count;
        var scaled = new double[count];
        var small = new Stack<int>(count);
        var large = new Stack<int>(count);
        for (int i = 0; i < count; i++)
        {
            scaled[i] = entries[i].Importance * count / totalImportance;
            if (scaled[i] < 1.0)
                small.Push(i);
            else
                large.Push(i);
        }

        while (small.Count > 0 && large.Count > 0)
        {
            int low = small.Pop();
            int high = large.Pop();
            thresholds[low] = (float)Math.Clamp(scaled[low], 0.0, 1.0);
            aliases[low] = high;
            scaled[high] = scaled[high] + scaled[low] - 1.0;
            if (scaled[high] < 1.0)
                small.Push(high);
            else
                large.Push(high);
        }

        while (large.Count > 0)
        {
            int index = large.Pop();
            thresholds[index] = 1.0f;
            aliases[index] = index;
        }
        while (small.Count > 0)
        {
            int index = small.Pop();
            thresholds[index] = 1.0f;
            aliases[index] = index;
        }
    }

    private readonly record struct WeightedCandidate(
        DdgiEmissiveTriangleCandidate Candidate,
        float Area,
        float Importance);
}
