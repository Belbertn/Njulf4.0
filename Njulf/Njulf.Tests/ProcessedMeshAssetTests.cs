using System;
using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class ProcessedMeshAssetTests
{
    [Test]
    public void Validate_AcceptsCurrentRendererReadyAsset()
    {
        ProcessedMeshAsset asset = CreateAsset();

        Assert.DoesNotThrow(asset.Validate);
        Assert.That(asset.IsStale(asset.SourceContentHash), Is.False);
    }

    [Test]
    public void Validate_RejectsStaleSchemaVersion()
    {
        ProcessedMeshAsset asset = CreateAsset(version: new ProcessedMeshVersion(0, 1, 1, 1));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(asset.Validate)!;
        Assert.That(ex.Message, Does.Contain("runtime expects"));
    }

    [Test]
    public void Validate_RejectsIncreasingTriangleCountsAcrossLods()
    {
        ProcessedMeshAsset asset = CreateAsset(lod1IndexCount: 600);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(asset.Validate)!;
        Assert.That(ex.Message, Does.Contain("more triangles"));
    }

    [Test]
    public void Validate_RejectsMixedMeshletAndGeometryLod()
    {
        ProcessedMeshAsset asset = CreateAsset(
            lod0Provenance: MeshLodProvenance.MeshletGenerated,
            lod1Provenance: MeshLodProvenance.Authored);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(asset.Validate)!;
        Assert.That(ex.Message, Does.Contain("mixes meshlet LOD"));
    }

    [Test]
    public void IsStale_DetectsSourceHashChange()
    {
        ProcessedMeshAsset asset = CreateAsset();

        Assert.That(asset.IsStale(ProcessedMeshAssetValidator.ComputeSourceHash("changed")), Is.True);
    }

    [Test]
    public void BudgetEvaluator_FailsHardRuntimeIncompatibleContent()
    {
        ProcessedMeshAsset asset = CreateAsset(materialSlots: 40);

        ContentBudgetStatus status = ProcessedMeshAssetValidator.EvaluateBudgets(asset, ContentBudgetLimits.Default);

        Assert.That(status.Severity, Is.EqualTo(ContentBudgetSeverity.Error));
        Assert.Throws<InvalidOperationException>(() => (asset with { BudgetStatus = status }).Validate());
    }

    [Test]
    public void BudgetEvaluator_WarnsForHighMeshletCount()
    {
        ProcessedMeshAsset asset = CreateAsset(lod0MeshletCount: 65_000);

        ContentBudgetStatus status = ProcessedMeshAssetValidator.EvaluateBudgets(asset, ContentBudgetLimits.Default);

        Assert.That(status.Severity, Is.EqualTo(ContentBudgetSeverity.Warning));
    }

    private static ProcessedMeshAsset CreateAsset(
        ProcessedMeshVersion? version = null,
        uint lod1IndexCount = 150,
        int materialSlots = 2,
        uint lod0MeshletCount = 20,
        MeshLodProvenance lod0Provenance = MeshLodProvenance.Authored,
        MeshLodProvenance lod1Provenance = MeshLodProvenance.GeneratedFallback)
    {
        int[] slots = new int[materialSlots];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = i;

        var asset = new ProcessedMeshAsset(
            "test/mesh",
            "test.glb",
            ProcessedMeshAssetValidator.ComputeSourceHash("source"),
            version ?? ProcessedMeshVersion.Current,
            new[] { new ProcessedSubmesh(0, 0, 300, UnitBox()) },
            new[]
            {
                new ProcessedMeshLod(0, lod0Provenance, 0, 100, 0, 300, 0, lod0MeshletCount, UnitBox(), Metrics(0f)),
                new ProcessedMeshLod(1, lod1Provenance, 100, 50, 300, lod1IndexCount, lod0MeshletCount, 8, UnitBox(), Metrics(0.5f))
            },
            slots,
            ProcessedMeshFlags.AlphaTested | ProcessedMeshFlags.SupportsImpostor,
            new FoliageAssetMetadata(0.5f, Vector3.UnitX, 1f, Vector3.Zero, new[] { new FoliageCluster(UnitBox(), 128, 0.25f, 42f) }),
            new ImpostorAssetMetadata("test/impostor", 512, 512, 16, UnitBox(), Vector3.Zero, 0.8f),
            ContentBudgetStatus.Pass);

        return asset;
    }

    private static ProcessedLodQualityMetrics Metrics(float reduction) =>
        new(reduction, reduction, 0.01f, 0.0f, 1.0f, true);

    private static BoundingBox UnitBox() => new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
}
