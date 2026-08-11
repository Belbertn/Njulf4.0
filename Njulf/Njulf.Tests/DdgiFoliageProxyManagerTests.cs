using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiFoliageProxyManagerTests
{
    [Test]
    public void ProceduralGeneration_IsStableAndIndependentOfSceneOrder()
    {
        FoliagePatch first = CreateProceduralPatch(
            new Guid("10000000-0000-0000-0000-000000000001"),
            new BoundingBox(Vector3.Zero, new Vector3(8f, 1f, 8f)),
            density: 8f,
            seed: 11);
        FoliagePatch second = CreateProceduralPatch(
            new Guid("20000000-0000-0000-0000-000000000002"),
            new BoundingBox(new Vector3(10f, 0f, 0f), new Vector3(18f, 1f, 8f)),
            density: 8f,
            seed: 23);

        DdgiFoliageProxyBuild a = DdgiFoliageProxyManager.BuildReference(
            new[] { first, second },
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            triangleBudget: 1_024,
            cadenceGeneration: 7,
            windTimeSeconds: 2.5f);
        DdgiFoliageProxyBuild b = DdgiFoliageProxyManager.BuildReference(
            new[] { second, first },
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            triangleBudget: 1_024,
            cadenceGeneration: 7,
            windTimeSeconds: 2.5f);

        Assert.Multiple(() =>
        {
            Assert.That(b.Vertices.Length, Is.EqualTo(a.Vertices.Length));
            Assert.That(b.Indices, Is.EqualTo(a.Indices));
            Assert.That(
                b.Instances.Select(static item => item.PatchIdentity),
                Is.EqualTo(a.Instances.Select(static item => item.PatchIdentity)));
        });
        for (int index = 0; index < a.Vertices.Length; index++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(b.Vertices[index].Position,
                    Is.EqualTo(a.Vertices[index].Position));
                Assert.That(b.Vertices[index].Normal,
                    Is.EqualTo(a.Vertices[index].Normal));
                Assert.That(b.Vertices[index].TexCoord,
                    Is.EqualTo(a.Vertices[index].TexCoord));
                Assert.That(b.Vertices[index].Color,
                    Is.EqualTo(a.Vertices[index].Color));
            });
        }
    }

    [Test]
    public void ProceduralGeneration_ObeysTriangleBudgetAndUsesLocalIndices()
    {
        FoliagePatch first = CreateProceduralPatch(
            new Guid("10000000-0000-0000-0000-000000000001"),
            new BoundingBox(Vector3.Zero, new Vector3(32f, 1f, 32f)),
            density: 64f,
            seed: 1);
        FoliagePatch second = CreateProceduralPatch(
            new Guid("20000000-0000-0000-0000-000000000002"),
            new BoundingBox(new Vector3(40f, 0f, 0f), new Vector3(72f, 1f, 32f)),
            density: 64f,
            seed: 2);

        const int triangleBudget = 100;
        DdgiFoliageProxyBuild build = DdgiFoliageProxyManager.BuildReference(
            new[] { first, second },
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            triangleBudget,
            cadenceGeneration: 1,
            windTimeSeconds: 0f);

        Assert.Multiple(() =>
        {
            Assert.That(build.Indices.Length / 3,
                Is.LessThanOrEqualTo(triangleBudget));
            Assert.That(build.Indices.Length / 3 %
                DdgiFoliageProxyManager.TrianglesPerCrossedCard,
                Is.Zero);
            Assert.That(build.DroppedTriangleCount, Is.GreaterThan(0));
        });

        foreach (DdgiFoliageProxyInstance instance in build.Instances)
        {
            if (!instance.Generated)
                continue;
            ReadOnlySpan<uint> localIndices = build.Indices.AsSpan(
                checked((int)instance.IndexOffset),
                checked((int)instance.IndexCount));
            Assert.That(localIndices.ToArray(),
                Has.All.LessThan(instance.VertexCount));
        }
    }

    [Test]
    public void WindUpdate_MovesOnlyCardTopsAndPreservesStablePlacement()
    {
        FoliagePatch patch = CreateProceduralPatch(
            new Guid("30000000-0000-0000-0000-000000000003"),
            new BoundingBox(Vector3.Zero, new Vector3(8f, 1f, 8f)),
            density: 16f,
            seed: 7);
        patch.Prototype.Wind.Strength = 1f;
        patch.Prototype.Wind.Frequency = 1f;

        DdgiFoliageProxyBuild still = DdgiFoliageProxyManager.BuildReference(
            new[] { patch },
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            1_024,
            cadenceGeneration: 4,
            windTimeSeconds: 0f);
        DdgiFoliageProxyBuild moved = DdgiFoliageProxyManager.BuildReference(
            new[] { patch },
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            1_024,
            cadenceGeneration: 5,
            windTimeSeconds: 0.25f);

        Assert.That(moved.Vertices.Length, Is.EqualTo(still.Vertices.Length));
        int movedTopCount = 0;
        for (int index = 0; index < still.Vertices.Length; index++)
        {
            bool bottom = index % 4 is 0 or 1;
            if (bottom)
            {
                Assert.That(moved.Vertices[index].Position,
                    Is.EqualTo(still.Vertices[index].Position));
            }
            else if (moved.Vertices[index].Position !=
                     still.Vertices[index].Position)
            {
                movedTopCount++;
            }
        }
        Assert.That(movedTopCount, Is.GreaterThan(0));
    }

    [Test]
    public void AuthoredOnly_UsesQualifiedMeshWithoutGeneratingCardStorage()
    {
        var prototype = new FoliagePrototype
        {
            GeometryMode = FoliageGeometryMode.AuthoredMeshlets,
            Mesh = new MeshHandle(7, 3)
        };
        var patch = new FoliagePatch(
            prototype,
            new BoundingBox(new Vector3(-1f), new Vector3(1f)))
        {
            Id = new Guid("40000000-0000-0000-0000-000000000004"),
            InstancePosition = new Vector3(4f, 2f, 1f),
            InstanceScale = 2f
        };

        DdgiFoliageProxyBuild build = DdgiFoliageProxyManager.BuildReference(
            new[] { patch },
            DdgiFoliageGeometryMode.AuthoredMeshOnly,
            triangleBudget: 0,
            cadenceGeneration: 1,
            windTimeSeconds: 0f);

        Assert.Multiple(() =>
        {
            Assert.That(build.AuthoredInstanceCount, Is.EqualTo(1));
            Assert.That(build.GeneratedInstanceCount, Is.Zero);
            Assert.That(build.Vertices, Is.Empty);
            Assert.That(build.Indices, Is.Empty);
            Assert.That(build.Instances[0].SourceMesh,
                Is.EqualTo(new MeshHandle(7, 3)));
            Assert.That(build.Instances[0].Generated, Is.False);
        });
    }

    [Test]
    public void GpuGenerationPlan_MatchesCpuOracleAdmissionAndPatchRanges()
    {
        FoliagePatch first = CreateProceduralPatch(
            new Guid("10000000-0000-0000-0000-000000000001"),
            new BoundingBox(Vector3.Zero, new Vector3(12f, 1f, 9f)),
            density: 9f,
            seed: 17);
        FoliagePatch second = CreateProceduralPatch(
            new Guid("20000000-0000-0000-0000-000000000002"),
            new BoundingBox(
                new Vector3(20f, 0f, 0f),
                new Vector3(32f, 1f, 9f)),
            density: 11f,
            seed: 29);

        const int triangleBudget = 128;
        DdgiFoliageProxyGenerationPlan plan =
            DdgiFoliageProxyManager.BuildGenerationPlan(
                new[] { second, first },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget,
                cadenceGeneration: 3,
                densityScale: 1f,
                proceduralGenerationAvailable: true);
        DdgiFoliageProxyBuild oracle =
            DdgiFoliageProxyManager.BuildReference(
                new[] { first, second },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget,
                cadenceGeneration: 3,
                windTimeSeconds: 1.25f);

        Assert.Multiple(() =>
        {
            Assert.That(plan.CardCount,
                Is.EqualTo(
                    oracle.Vertices.Length /
                    DdgiFoliageProxyManager.VerticesPerCrossedCard));
            Assert.That(plan.GeneratedInstanceCount,
                Is.EqualTo(oracle.GeneratedInstanceCount));
            Assert.That(plan.DroppedTriangleCount,
                Is.EqualTo(oracle.DroppedTriangleCount));
            Assert.That(plan.EstimatedRepresentedBladeCount,
                Is.EqualTo(oracle.EstimatedRepresentedBladeCount));
            Assert.That(plan.Patches, Has.Length.EqualTo(plan.GeneratedInstanceCount));
        });

        uint expectedCardOffset = 0;
        foreach (var patch in plan.Patches)
        {
            Assert.That(patch.CardOffset, Is.EqualTo(expectedCardOffset));
            Assert.That(patch.CardCount, Is.GreaterThan(0u));
            expectedCardOffset = checked(expectedCardOffset + patch.CardCount);
        }
        Assert.That(expectedCardOffset, Is.EqualTo((uint)plan.CardCount));

        foreach (DdgiFoliageProxyInstance instance in plan.Instances)
        {
            if (!instance.Generated)
                continue;
            Assert.Multiple(() =>
            {
                Assert.That(instance.VertexOffset %
                    DdgiFoliageProxyManager.VerticesPerCrossedCard,
                    Is.Zero);
                Assert.That(instance.IndexOffset %
                    DdgiFoliageProxyManager.IndicesPerCrossedCard,
                    Is.Zero);
                Assert.That(instance.IndexCount / 3u,
                    Is.EqualTo(
                        instance.VertexCount /
                        DdgiFoliageProxyManager.VerticesPerCrossedCard *
                        DdgiFoliageProxyManager.TrianglesPerCrossedCard));
            });
        }
    }

    [Test]
    public void GpuGeneratorUnavailable_ExcludesProceduralWorkAndDeclaresFallback()
    {
        FoliagePatch patch = CreateProceduralPatch(
            new Guid("50000000-0000-0000-0000-000000000005"),
            new BoundingBox(Vector3.Zero, new Vector3(16f, 1f, 16f)),
            density: 16f,
            seed: 41);

        DdgiFoliageProxyGenerationPlan plan =
            DdgiFoliageProxyManager.BuildGenerationPlan(
                new[] { patch },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget: 1_024,
                cadenceGeneration: 1,
                densityScale: 1f,
                proceduralGenerationAvailable: false,
                proceduralGenerationFailureReason: "test pipeline failure");

        Assert.Multiple(() =>
        {
            Assert.That(plan.Patches, Is.Empty);
            Assert.That(plan.Instances, Is.Empty);
            Assert.That(plan.CardCount, Is.Zero);
            Assert.That(plan.DroppedTriangleCount, Is.GreaterThan(0));
            Assert.That(plan.FallbackReason,
                Does.Contain("test pipeline failure"));
            Assert.That(plan.FallbackReason,
                Does.Contain("excluded"));
        });
    }

    [Test]
    public void DensityScale_ChangesAdmissionWithoutChangingStablePatchIdentity()
    {
        FoliagePatch patch = CreateProceduralPatch(
            new Guid("60000000-0000-0000-0000-000000000006"),
            new BoundingBox(Vector3.Zero, new Vector3(20f, 1f, 20f)),
            density: 4f,
            seed: 53);

        DdgiFoliageProxyGenerationPlan half =
            DdgiFoliageProxyManager.BuildGenerationPlan(
                new[] { patch },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget: 4_096,
                cadenceGeneration: 9,
                densityScale: 0.5f,
                proceduralGenerationAvailable: true);
        DdgiFoliageProxyGenerationPlan full =
            DdgiFoliageProxyManager.BuildGenerationPlan(
                new[] { patch },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget: 4_096,
                cadenceGeneration: 9,
                densityScale: 1f,
                proceduralGenerationAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(half.CardCount, Is.GreaterThan(0));
            Assert.That(full.CardCount, Is.GreaterThan(half.CardCount));
            Assert.That(full.Patches[0].StablePatchKeyLow,
                Is.EqualTo(half.Patches[0].StablePatchKeyLow));
            Assert.That(full.Patches[0].StablePatchKeyHigh,
                Is.EqualTo(half.Patches[0].StablePatchKeyHigh));
        });
    }

    [Test]
    public void ProbeInfluenceLod_IsWorldStableAndPreservesRepresentedDensity()
    {
        var volume = new GlobalIlluminationProbeVolume
        {
            Id = new Guid("90000000-0000-0000-0000-000000000009"),
            Origin = Vector3.Zero,
            Size = new Vector3(10f, 10f, 10f),
            ProbeCountX = 6,
            ProbeCountY = 6,
            ProbeCountZ = 6,
            BlendDistance = 2f,
            MaxRayDistance = 10f
        };
        FoliagePatch near = CreateProceduralPatch(
            new Guid("10000000-0000-0000-0000-000000000001"),
            new BoundingBox(new Vector3(1f, 0f, 1f), new Vector3(9f, 1f, 9f)),
            density: 64f,
            seed: 1);
        FoliagePatch mid = CreateProceduralPatch(
            new Guid("20000000-0000-0000-0000-000000000002"),
            new BoundingBox(new Vector3(16f, 0f, 1f), new Vector3(24f, 1f, 9f)),
            density: 64f,
            seed: 2);
        FoliagePatch far = CreateProceduralPatch(
            new Guid("30000000-0000-0000-0000-000000000003"),
            new BoundingBox(new Vector3(28f, 0f, 1f), new Vector3(36f, 1f, 9f)),
            density: 64f,
            seed: 3);
        FoliagePatch excluded = CreateProceduralPatch(
            new Guid("40000000-0000-0000-0000-000000000004"),
            new BoundingBox(new Vector3(40f, 0f, 1f), new Vector3(48f, 1f, 9f)),
            density: 64f,
            seed: 4);

        DdgiFoliageProxyGenerationPlan plan =
            DdgiFoliageProxyManager.BuildGenerationPlan(
                new[] { excluded, far, mid, near },
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                triangleBudget: 16_384,
                cadenceGeneration: 4,
                densityScale: 1f,
                proceduralGenerationAvailable: true,
                probeVolumes: new[] { volume });

        Assert.Multiple(() =>
        {
            Assert.That(DdgiFoliageProxyManager.ClassifyLodTier(
                near.Bounds,
                new[] { volume }), Is.EqualTo(DdgiFoliageProxyLodTier.Near));
            Assert.That(DdgiFoliageProxyManager.ClassifyLodTier(
                mid.Bounds,
                new[] { volume }), Is.EqualTo(DdgiFoliageProxyLodTier.Mid));
            Assert.That(DdgiFoliageProxyManager.ClassifyLodTier(
                far.Bounds,
                new[] { volume }), Is.EqualTo(DdgiFoliageProxyLodTier.Far));
            Assert.That(DdgiFoliageProxyManager.ClassifyLodTier(
                excluded.Bounds,
                new[] { volume }), Is.EqualTo(DdgiFoliageProxyLodTier.Excluded));
            Assert.That(plan.Instances.Select(static instance => instance.LodTier),
                Is.EqualTo(new[]
                {
                    DdgiFoliageProxyLodTier.Near,
                    DdgiFoliageProxyLodTier.Mid,
                    DdgiFoliageProxyLodTier.Far
                }));
            Assert.That(plan.NearCardCount,
                Is.GreaterThan(plan.MidCardCount));
            Assert.That(plan.MidCardCount,
                Is.GreaterThan(plan.FarCardCount));
            Assert.That(plan.ExcludedPatchCount, Is.EqualTo(1));
            Assert.That(plan.EstimatedRepresentedBladeCount,
                Is.EqualTo(plan.RequestedRepresentedInstanceCount * 3 / 4));
            Assert.That(plan.DensityError, Is.EqualTo(0.25f).Within(1e-6f));
        });
    }

    [Test]
    public void ProbeInfluenceLod_NoAuthoredVolumeDefaultsToCameraIndependentNearTier()
    {
        FoliagePatch patch = CreateProceduralPatch(
            new Guid("70000000-0000-0000-0000-000000000007"),
            new BoundingBox(
                new Vector3(100_000f, 0f, 100_000f),
                new Vector3(100_008f, 1f, 100_008f)),
            density: 8f,
            seed: 71);

        Assert.That(
            DdgiFoliageProxyManager.ClassifyLodTier(patch.Bounds, null),
            Is.EqualTo(DdgiFoliageProxyLodTier.Near));
    }

    private static FoliagePatch CreateProceduralPatch(
        Guid id,
        BoundingBox bounds,
        float density,
        uint seed)
    {
        var prototype = new FoliagePrototype
        {
            GeometryMode = FoliageGeometryMode.ProceduralGrass,
            CardHeight = 1.2f,
            CardWidth = 0.08f
        };
        return new FoliagePatch(prototype, bounds)
        {
            Id = id,
            Density = density,
            Seed = seed
        };
    }
}
