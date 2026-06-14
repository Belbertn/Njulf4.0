using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

public sealed class FoliageBatchManager
{
    private readonly Dictionary<FoliageBatchKey, FoliageBatch> _batches = new();

    public IReadOnlyDictionary<FoliageBatchKey, FoliageBatch> Batches => _batches;

    public void Clear() => _batches.Clear();

    public void AddAsset(ProcessedMeshAsset asset, int lodPolicy = 0, int cellId = 0)
    {
        if (asset == null)
            throw new ArgumentNullException(nameof(asset));
        asset.Validate();
        if (asset.Foliage == null)
            throw new ArgumentException($"Processed mesh asset '{asset.AssetId}' does not contain foliage metadata.", nameof(asset));

        foreach (FoliageCluster cluster in asset.Foliage.Clusters)
        {
            foreach (int materialSlot in asset.Foliage.MaterialSlots.Count > 0 ? asset.Foliage.MaterialSlots : asset.MaterialSlots)
            {
                var key = new FoliageBatchKey(
                    asset.AssetId,
                    materialSlot,
                    asset.Foliage.WindStrength,
                    lodPolicy,
                    cellId);

                if (!_batches.TryGetValue(key, out FoliageBatch? batch))
                {
                    batch = new FoliageBatch(key);
                    _batches.Add(key, batch);
                }

                batch.AddCluster(cluster);
            }
        }
    }

    public IReadOnlyList<FoliageBatchDraw> BuildVisibleDraws(BoundingBox visibleBounds)
    {
        var draws = new List<FoliageBatchDraw>();
        foreach (FoliageBatch batch in _batches.Values)
        {
            int visibleInstances = 0;
            int visibleClusters = 0;
            foreach (FoliageCluster cluster in batch.Clusters)
            {
                if (!cluster.Bounds.Intersects(visibleBounds))
                    continue;

                visibleClusters++;
                visibleInstances += cluster.InstanceCount;
            }

            if (visibleInstances > 0)
                draws.Add(new FoliageBatchDraw(batch.Key, visibleClusters, visibleInstances));
        }

        return draws;
    }
}

public sealed class FoliageBatch
{
    private readonly List<FoliageCluster> _clusters = new();

    internal FoliageBatch(FoliageBatchKey key)
    {
        Key = key;
    }

    public FoliageBatchKey Key { get; }
    public IReadOnlyList<FoliageCluster> Clusters => _clusters;
    public int InstanceCount { get; private set; }

    internal void AddCluster(FoliageCluster cluster)
    {
        _clusters.Add(cluster);
        InstanceCount += cluster.InstanceCount;
    }
}

public readonly record struct FoliageBatchKey(
    string MeshAssetId,
    int MaterialSlot,
    float WindStrength,
    int LodPolicy,
    int CellId);

public readonly record struct FoliageBatchDraw(
    FoliageBatchKey Key,
    int VisibleClusterCount,
    int VisibleInstanceCount);

public static class ImpostorLodSelector
{
    public static ImpostorSelection Select(
        ProcessedMeshAsset asset,
        Vector3 cameraPosition,
        Vector3 objectCenter,
        float objectRadius,
        float impostorStartDistanceRatio,
        float fadeWidthRatio)
    {
        if (asset == null)
            throw new ArgumentNullException(nameof(asset));
        asset.Validate();
        if (asset.Impostor == null)
            return new ImpostorSelection(false, 0f, 0);
        if (objectRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(objectRadius), "Object radius must be positive.");

        float distanceRatio = Vector3.Distance(cameraPosition, objectCenter) / objectRadius;
        float fadeStart = MathF.Max(0f, impostorStartDistanceRatio - MathF.Max(0f, fadeWidthRatio));
        if (distanceRatio < fadeStart)
            return new ImpostorSelection(false, 0f, 0);

        float blend = fadeWidthRatio <= 0f
            ? 1f
            : Math.Clamp((distanceRatio - fadeStart) / fadeWidthRatio, 0f, 1f);

        int viewIndex = SelectViewDirection(asset.Impostor.ViewDirectionCount, cameraPosition - objectCenter);
        return new ImpostorSelection(blend >= 1f, blend, viewIndex);
    }

    private static int SelectViewDirection(int viewDirectionCount, Vector3 viewDirection)
    {
        if (viewDirectionCount <= 1)
            return 0;

        Vector3 direction = viewDirection.LengthSquared() > float.Epsilon
            ? viewDirection.Normalized()
            : Vector3.Forward;
        float angle = MathF.Atan2(direction.X, direction.Z);
        if (angle < 0f)
            angle += MathF.PI * 2f;
        int index = (int)MathF.Round(angle / (MathF.PI * 2f) * viewDirectionCount) % viewDirectionCount;
        return index;
    }
}

public readonly record struct ImpostorSelection(
    bool UseImpostor,
    float BlendFactor,
    int ViewDirectionIndex);
