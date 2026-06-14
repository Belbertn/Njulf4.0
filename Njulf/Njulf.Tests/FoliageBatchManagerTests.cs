using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class FoliageBatchManagerTests
{
    [Test]
    public void AddAsset_BatchesByMeshMaterialWindLodAndCell()
    {
        ProcessedMeshAsset asset = CreateFoliageAsset();
        var manager = new FoliageBatchManager();

        manager.AddAsset(asset, lodPolicy: 2, cellId: 9);

        Assert.That(manager.Batches, Has.Count.EqualTo(1));
        FoliageBatch batch = manager.Batches.Values.First();
        Assert.That(batch.Key.MeshAssetId, Is.EqualTo(asset.AssetId));
        Assert.That(batch.Key.MaterialSlot, Is.EqualTo(0));
        Assert.That(batch.Key.LodPolicy, Is.EqualTo(2));
        Assert.That(batch.Key.CellId, Is.EqualTo(9));
        Assert.That(batch.InstanceCount, Is.EqualTo(128));
    }

    [Test]
    public void BuildVisibleDraws_CullsWholeClustersBeforeDrawing()
    {
        ProcessedMeshAsset asset = CreateFoliageAsset(
            new FoliageCluster(new BoundingBox(new Vector3(-1f), new Vector3(1f)), 64, 0f, 1f),
            new FoliageCluster(new BoundingBox(new Vector3(50f), new Vector3(55f)), 64, 0f, 2f));
        var manager = new FoliageBatchManager();
        manager.AddAsset(asset);

        var draws = manager.BuildVisibleDraws(new BoundingBox(new Vector3(-2f), new Vector3(2f)));

        Assert.That(draws, Has.Count.EqualTo(1));
        Assert.That(draws[0].VisibleClusterCount, Is.EqualTo(1));
        Assert.That(draws[0].VisibleInstanceCount, Is.EqualTo(64));
    }

    [Test]
    public void ImpostorLodSelector_FadesThenSwitchesToImpostor()
    {
        ProcessedMeshAsset asset = CreateFoliageAsset();

        ImpostorSelection near = ImpostorLodSelector.Select(asset, new Vector3(0f, 0f, 4f), Vector3.Zero, 1f, 12f, 4f);
        ImpostorSelection fading = ImpostorLodSelector.Select(asset, new Vector3(0f, 0f, 10f), Vector3.Zero, 1f, 12f, 4f);
        ImpostorSelection far = ImpostorLodSelector.Select(asset, new Vector3(0f, 0f, 20f), Vector3.Zero, 1f, 12f, 4f);

        Assert.That(near.UseImpostor, Is.False);
        Assert.That(near.BlendFactor, Is.EqualTo(0f));
        Assert.That(fading.UseImpostor, Is.False);
        Assert.That(fading.BlendFactor, Is.GreaterThan(0f).And.LessThan(1f));
        Assert.That(far.UseImpostor, Is.True);
        Assert.That(far.ViewDirectionIndex, Is.InRange(0, asset.Impostor!.ViewDirectionCount - 1));
    }

    private static ProcessedMeshAsset CreateFoliageAsset(params FoliageCluster[] clusters)
    {
        if (clusters.Length == 0)
            clusters = new[] { new FoliageCluster(new BoundingBox(new Vector3(-1f), new Vector3(1f)), 128, 0f, 42f) };

        BoundingBox bounds = new(new Vector3(-1f), new Vector3(1f));
        var asset = new ProcessedMeshAsset(
            "foliage/grass",
            "grass.glb",
            ProcessedMeshAssetValidator.ComputeSourceHash("grass"),
            ProcessedMeshVersion.Current,
            new[] { new ProcessedSubmesh(0, 0, 3, bounds) },
            new[] { new ProcessedMeshLod(0, MeshLodProvenance.Authored, 0, 3, 0, 3, 0, 1, bounds, new ProcessedLodQualityMetrics(0f, 0f, 0f, 0f, 0f, true)) },
            new[] { 0 },
            ProcessedMeshFlags.Foliage | ProcessedMeshFlags.SupportsImpostor,
            new FoliageAssetMetadata("foliage/grass", new[] { 0 }, 0.5f, true, true, 0f, Vector3.UnitX, 1f, Vector3.Zero, clusters),
            new ImpostorAssetMetadata(
                "foliage/grass/impostor",
                512,
                512,
                16,
                ImpostorType.Octahedral,
                new[] { new ImpostorAtlasTexture(ImpostorTextureKind.Albedo, "foliage/grass/impostor/albedo", TextureColorSpace.Srgb) },
                bounds,
                Vector3.Zero,
                0.75f),
            ContentBudgetStatus.Pass);
        asset.Validate();
        return asset;
    }
}
