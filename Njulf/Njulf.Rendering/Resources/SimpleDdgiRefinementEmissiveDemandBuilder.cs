using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiRefinementEmissiveDemandConfiguration(
    float MinimumLuminanceNits,
    float MaximumEmitterAreaSquareMeters,
    int MaximumDemandCount = 32);

public readonly record struct SimpleDdgiRefinementEmissiveDemandDiagnostics(
    int ExaminedSourceCount,
    int EligibleSourceCount,
    int AdmittedDemandCount,
    int RejectedLargeSourceCount,
    int RejectedDimSourceCount);

/// <summary>
/// Converts the already-selected DDGI emissive-source table into a tiny,
/// allocation-free set of B3 refinement requests. Selection is bounded by a
/// fixed top-K pass, so a scene with thousands of emissive triangles cannot
/// inflate the brick allocator's CPU cost or input cardinality.
/// </summary>
public static class SimpleDdgiRefinementEmissiveDemandBuilder
{
    public const int MaximumDemandCount = 64;
    private const float MinimumArea = 1e-6f;
    private const float MinimumPriority = 176f;
    private const float MaximumPriority = 384f;

    private struct EmitterCluster
    {
        public EmitterCluster(
            Vector3 center,
            BoundingBox bounds,
            float area,
            float priority,
            ulong stableSourceId)
        {
            Center = center;
            Minimum = Min(bounds.Min, bounds.Max);
            Maximum = Max(bounds.Min, bounds.Max);
            Area = area;
            Priority = priority;
            StableSourceId = stableSourceId;
        }

        public Vector3 Center;
        public Vector3 Minimum;
        public Vector3 Maximum;
        public float Area;
        public float Priority;
        public ulong StableSourceId;

        public void Merge(
            Vector3 center,
            BoundingBox bounds,
            float area,
            float priority,
            ulong stableSourceId)
        {
            float combinedArea = Area + area;
            Center = (Center * Area + center * area) / combinedArea;
            Minimum = Min(Minimum, Min(bounds.Min, bounds.Max));
            Maximum = Max(Maximum, Max(bounds.Min, bounds.Max));
            Area = combinedArea;
            Priority = Math.Max(Priority, priority);
            StableSourceId = Math.Min(StableSourceId, stableSourceId);
        }
    }

    public static SimpleDdgiRefinementEmissiveDemandDiagnostics Build(
        ReadOnlySpan<GPUDdgiEmissiveSource> sources,
        SimpleDdgiRefinementEmissiveDemandConfiguration configuration,
        List<SimpleDdgiRefinementDemand> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Validate(configuration);
        destination.Clear();

        int capacity = Math.Min(
            configuration.MaximumDemandCount,
            MaximumDemandCount);
        Span<EmitterCluster> clusters =
            stackalloc EmitterCluster[MaximumDemandCount];
        int clusterCount = 0;
        float maximumMergeDistance = 2f * MathF.Sqrt(
            configuration.MaximumEmitterAreaSquareMeters / MathF.PI);
        float maximumMergeDistanceSquared =
            maximumMergeDistance * maximumMergeDistance;
        int eligible = 0;
        int rejectedLarge = 0;
        int rejectedDim = 0;
        for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            GPUDdgiEmissiveSource source = sources[sourceIndex];
            if (!TryMeasure(
                    source,
                    out Vector3 center,
                    out BoundingBox bounds,
                    out float area,
                    out float luminanceNits))
            {
                rejectedDim++;
                continue;
            }

            if (area > configuration.MaximumEmitterAreaSquareMeters)
            {
                rejectedLarge++;
                continue;
            }
            if (luminanceNits < configuration.MinimumLuminanceNits)
            {
                rejectedDim++;
                continue;
            }

            eligible++;
            float threshold = Math.Max(
                configuration.MinimumLuminanceNits,
                EmissivePhotometry.MinimumChromaticityLuminance);
            float brightnessStops = MathF.Log2(Math.Max(luminanceNits / threshold, 1f));
            float compactness = 1f - Math.Clamp(
                area / configuration.MaximumEmitterAreaSquareMeters,
                0f,
                1f);
            float priority = Math.Clamp(
                MinimumPriority + brightnessStops * 16f + compactness * 16f,
                MinimumPriority,
                MaximumPriority);
            ulong stableSourceId = StablePayloadKey(source);
            int mergeCluster = FindMergeCluster(
                clusters[..clusterCount],
                center,
                area,
                configuration.MaximumEmitterAreaSquareMeters,
                maximumMergeDistanceSquared);
            if (mergeCluster >= 0)
            {
                EmitterCluster cluster = clusters[mergeCluster];
                cluster.Merge(center, bounds, area, priority, stableSourceId);
                clusters[mergeCluster] = cluster;
            }
            else
            {
                AdmitCluster(
                    clusters,
                    ref clusterCount,
                    new EmitterCluster(
                        center,
                        bounds,
                        area,
                        priority,
                        stableSourceId));
            }
        }

        for (int clusterIndex = 0;
             clusterIndex < clusterCount;
             clusterIndex++)
        {
            EmitterCluster cluster = clusters[clusterIndex];
            var demand = new SimpleDdgiRefinementDemand(
                cluster.Center,
                cluster.Priority,
                SimpleDdgiRefinementDemandReason.CompactEmissive,
                cluster.StableSourceId)
            {
                SourceBounds = new BoundingBox(
                    cluster.Minimum,
                    cluster.Maximum)
            };
            AdmitTopK(destination, demand, capacity);
        }

        destination.Sort(static (left, right) =>
        {
            int priority = right.Priority.CompareTo(left.Priority);
            return priority != 0
                ? priority
                : left.StableSourceId.CompareTo(right.StableSourceId);
        });
        return new SimpleDdgiRefinementEmissiveDemandDiagnostics(
            sources.Length,
            eligible,
            destination.Count,
            rejectedLarge,
            rejectedDim);
    }

    private static int FindMergeCluster(
        ReadOnlySpan<EmitterCluster> clusters,
        Vector3 center,
        float area,
        float maximumArea,
        float maximumDistanceSquared)
    {
        int best = -1;
        float bestDistanceSquared = float.MaxValue;
        for (int index = 0; index < clusters.Length; index++)
        {
            EmitterCluster cluster = clusters[index];
            if (cluster.Area + area > maximumArea)
                continue;
            float distanceSquared = (center - cluster.Center).LengthSquared();
            if (distanceSquared > maximumDistanceSquared ||
                distanceSquared >= bestDistanceSquared)
            {
                continue;
            }
            best = index;
            bestDistanceSquared = distanceSquared;
        }
        return best;
    }

    private static void AdmitCluster(
        Span<EmitterCluster> clusters,
        ref int count,
        EmitterCluster cluster)
    {
        if (count < clusters.Length)
        {
            clusters[count++] = cluster;
            return;
        }

        int weakestIndex = 0;
        for (int index = 1; index < count; index++)
        {
            EmitterCluster candidate = clusters[index];
            EmitterCluster weakest = clusters[weakestIndex];
            if (candidate.Priority < weakest.Priority ||
                (candidate.Priority == weakest.Priority &&
                 candidate.StableSourceId > weakest.StableSourceId))
            {
                weakestIndex = index;
            }
        }

        EmitterCluster currentWeakest = clusters[weakestIndex];
        if (cluster.Priority < currentWeakest.Priority ||
            (cluster.Priority == currentWeakest.Priority &&
             cluster.StableSourceId >= currentWeakest.StableSourceId))
        {
            return;
        }
        clusters[weakestIndex] = cluster;
    }

    private static void AdmitTopK(
        List<SimpleDdgiRefinementDemand> destination,
        SimpleDdgiRefinementDemand demand,
        int capacity)
    {
        if (destination.Count < capacity)
        {
            destination.Add(demand);
            return;
        }

        int weakestIndex = 0;
        for (int index = 1; index < destination.Count; index++)
        {
            SimpleDdgiRefinementDemand candidate = destination[index];
            SimpleDdgiRefinementDemand weakest = destination[weakestIndex];
            if (candidate.Priority < weakest.Priority ||
                (candidate.Priority == weakest.Priority &&
                 candidate.StableSourceId > weakest.StableSourceId))
            {
                weakestIndex = index;
            }
        }

        SimpleDdgiRefinementDemand currentWeakest = destination[weakestIndex];
        if (demand.Priority < currentWeakest.Priority ||
            (demand.Priority == currentWeakest.Priority &&
             demand.StableSourceId >= currentWeakest.StableSourceId))
        {
            return;
        }
        destination[weakestIndex] = demand;
    }

    private static bool TryMeasure(
        GPUDdgiEmissiveSource source,
        out Vector3 center,
        out BoundingBox bounds,
        out float area,
        out float luminanceNits)
    {
        DdgiEmissiveSourceFlags flags =
            DdgiEmissiveTriangleTable.DecodeFlags(source);
        if ((flags & DdgiEmissiveSourceFlags.Triangle) != 0)
        {
            Vector3 vertex0 = Xyz(source.Vertex0Area);
            Vector3 vertex1 = vertex0 + Xyz(source.Edge1AliasProbability);
            Vector3 vertex2 = vertex0 + Xyz(source.Edge2AliasFlags);
            center = (vertex0 + vertex1 + vertex2) / 3f;
            bounds = new BoundingBox(
                Min(vertex0, Min(vertex1, vertex2)),
                Max(vertex0, Max(vertex1, vertex2)));
            area = Math.Max(source.Vertex0Area.W, 0f);
            luminanceNits = EmissivePhotometry.SceneLinearLuminanceToNits(
                Math.Max(
                    EmissivePhotometry.Luminance(
                        Xyz(source.RadianceSelectionProbability)),
                    0f));
            return IsMeasured(center, area, luminanceNits);
        }

        if ((flags & DdgiEmissiveSourceFlags.MacroEmitter) != 0)
        {
            center = Xyz(source.Vertex0Area);
            float radius = Math.Max(Math.Abs(source.Vertex0Area.W), 1e-3f);
            float secondRadius = Math.Max(
                Math.Abs(source.Edge2AliasFlags.X),
                radius);
            float thirdRadius = Math.Max(
                Math.Abs(source.Edge2AliasFlags.Y),
                radius);
            float maximumRadius = Math.Max(
                radius,
                Math.Max(secondRadius, thirdRadius));
            Vector3 extent = new(maximumRadius);
            bounds = new BoundingBox(center - extent, center + extent);
            // Conservative projected footprint: elongated beams/capsules stop
            // qualifying as compact while spherical sparks remain small.
            area = MathF.PI * secondRadius * thirdRadius;
            float powerLuminance = Math.Max(
                EmissivePhotometry.Luminance(
                    Xyz(source.RadianceSelectionProbability)),
                0f);
            float equivalentRadiance = powerLuminance /
                Math.Max(2f * MathF.PI * area, MinimumArea);
            luminanceNits = EmissivePhotometry.SceneLinearLuminanceToNits(
                equivalentRadiance);
            return IsMeasured(center, area, luminanceNits);
        }

        if ((flags & DdgiEmissiveSourceFlags.ProxyRollback) != 0)
        {
            center = Xyz(source.Vertex0Area);
            Vector3 min = Min(
                Xyz(source.Edge2AliasFlags),
                Xyz(source.RadianceSelectionProbability));
            Vector3 max = Max(
                Xyz(source.Edge2AliasFlags),
                Xyz(source.RadianceSelectionProbability));
            Vector3 extent = max - min;
            bounds = new BoundingBox(min, max);
            area = 2f * (
                extent.X * extent.Y +
                extent.X * extent.Z +
                extent.Y * extent.Z);
            luminanceNits = EmissivePhotometry.SceneLinearLuminanceToNits(
                Math.Max(
                    EmissivePhotometry.Luminance(
                        Xyz(source.Edge1AliasProbability)),
                    0f));
            return IsMeasured(center, area, luminanceNits);
        }

        center = default;
        bounds = default;
        area = 0f;
        luminanceNits = 0f;
        return false;
    }

    private static bool IsMeasured(
        Vector3 center,
        float area,
        float luminanceNits) =>
        float.IsFinite(center.X) &&
        float.IsFinite(center.Y) &&
        float.IsFinite(center.Z) &&
        float.IsFinite(area) &&
        float.IsFinite(luminanceNits) &&
        area > MinimumArea &&
        luminanceNits > 0f;

    private static ulong StablePayloadKey(GPUDdgiEmissiveSource source)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        Add(source.Vertex0Area);
        Add(source.Edge1AliasProbability, ignoreW: true);
        Add(source.Edge2AliasFlags);
        Add(source.RadianceSelectionProbability, ignoreW: true);
        return hash;

        void Add(Vector4 value, bool ignoreW = false)
        {
            Mix(BitConverter.SingleToUInt32Bits(value.X));
            Mix(BitConverter.SingleToUInt32Bits(value.Y));
            Mix(BitConverter.SingleToUInt32Bits(value.Z));
            if (!ignoreW)
                Mix(BitConverter.SingleToUInt32Bits(value.W));
        }

        void Mix(uint value)
        {
            hash ^= value;
            hash *= prime;
        }
    }

    private static Vector3 Xyz(Vector4 value) =>
        new(value.X, value.Y, value.Z);

    private static Vector3 Min(Vector3 left, Vector3 right) => new(
        Math.Min(left.X, right.X),
        Math.Min(left.Y, right.Y),
        Math.Min(left.Z, right.Z));

    private static Vector3 Max(Vector3 left, Vector3 right) => new(
        Math.Max(left.X, right.X),
        Math.Max(left.Y, right.Y),
        Math.Max(left.Z, right.Z));

    private static void Validate(
        SimpleDdgiRefinementEmissiveDemandConfiguration configuration)
    {
        if (!float.IsFinite(configuration.MinimumLuminanceNits) ||
            configuration.MinimumLuminanceNits < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
        if (!float.IsFinite(configuration.MaximumEmitterAreaSquareMeters) ||
            configuration.MaximumEmitterAreaSquareMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
        if (configuration.MaximumDemandCount is < 1 or > MaximumDemandCount)
            throw new ArgumentOutOfRangeException(nameof(configuration));
    }
}
