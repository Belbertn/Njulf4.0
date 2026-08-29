using Njulf.Assets.Scenes;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FoliageProductionContractsTests
{
    [Test]
    public void CellIdentity_IsStableAcrossUnloadAndHandlesNegativeWorldCells()
    {
        Guid patchId = Guid.Parse("91a80875-d245-4d90-aa17-a124c875931e");
        FoliagePatch first = CreatePatch(patchId);
        FoliagePatch reloaded = CreatePatch(patchId);

        FoliageCellKey firstKey = FoliageCellKey.FromWorld(
            first,
            new Vector3(-0.25f, 0f, 17f));
        FoliageCellKey reloadedKey = FoliageCellKey.FromWorld(
            reloaded,
            new Vector3(-0.25f, 0f, 17f));

        Assert.Multiple(() =>
        {
            Assert.That(firstKey, Is.EqualTo(reloadedKey));
            Assert.That(firstKey.X, Is.EqualTo(-1));
            Assert.That(firstKey.Z, Is.EqualTo(1));
            Assert.That(firstKey.CellSizeMillimeters, Is.EqualTo(16_000));
            Assert.That(firstKey.StableIdentity,
                Is.EqualTo(reloadedKey.StableIdentity));
        });
    }

    [Test]
    public void Streaming_IsBoundedAndRetiresOnlyAfterTheFrameDelay()
    {
        FoliagePatch patch = CreatePatch(
            Guid.Parse("d3c4be7f-149c-43ff-857f-4deec97a2488"));
        var manager = new FoliageStreamingManager();
        FoliageStreamingOptions options = Options(
            maximumLoads: 2,
            maximumRetirements: 2,
            retirementDelay: 2);

        FoliageStreamingSnapshot first = manager.Update(
            [patch],
            new Vector3(8f, 1f, 8f),
            1,
            options);
        FoliageStreamingSnapshot second = manager.Update(
            [patch],
            new Vector3(8f, 1f, 8f),
            2,
            options);
        FoliageStreamingSnapshot retiring = manager.Update(
            [patch],
            new Vector3(1_000f, 1f, 1_000f),
            3,
            options);
        FoliageStreamingSnapshot stillRetiring = manager.Update(
            [patch],
            new Vector3(1_000f, 1f, 1_000f),
            4,
            options);
        FoliageStreamingSnapshot partiallyRetired = manager.Update(
            [patch],
            new Vector3(1_000f, 1f, 1_000f),
            5,
            options);
        FoliageStreamingSnapshot fullyRetired = manager.Update(
            [patch],
            new Vector3(1_000f, 1f, 1_000f),
            6,
            options);

        Assert.Multiple(() =>
        {
            Assert.That(first.LoadedThisFrame, Is.EqualTo(2));
            Assert.That(first.ScheduledUploadBytes,
                Is.EqualTo(2UL * options.EstimatedCellUploadBytes));
            Assert.That(second.ResidentCellCount, Is.EqualTo(4));
            Assert.That(retiring.RetiringCellCount, Is.EqualTo(4));
            Assert.That(stillRetiring.ResidentCellCount, Is.EqualTo(4));
            Assert.That(partiallyRetired.RetiredThisFrame, Is.EqualTo(2));
            Assert.That(partiallyRetired.ResidentCellCount, Is.EqualTo(2));
            Assert.That(fullyRetired.ResidentCellCount, Is.Zero);
        });
    }

    [Test]
    public void Streaming_FailedLoadsUseAttemptBudgetAndRetryBackoff()
    {
        FoliagePatch patch = CreatePatch(
            Guid.Parse("da631bc4-7d8c-4791-8839-b4cd35382641"));
        var manager = new FoliageStreamingManager();
        FoliageStreamingOptions options = Options(
            maximumLoads: 2,
            maximumRetirements: 4,
            retirementDelay: 2) with
        {
            RetryDelayFrames = 3
        };
        int attempts = 0;

        FoliageStreamingSnapshot failed = manager.Update(
            [patch],
            new Vector3(8f, 1f, 8f),
            1,
            options,
            _ =>
            {
                attempts++;
                return false;
            });
        manager.Update(
            [patch],
            new Vector3(8f, 1f, 8f),
            2,
            options,
            _ =>
            {
                attempts++;
                return false;
            });

        Assert.Multiple(() =>
        {
            Assert.That(failed.LoadedThisFrame, Is.Zero);
            Assert.That(failed.RetryCellCount, Is.EqualTo(2));
            Assert.That(attempts, Is.EqualTo(4),
                "Two backed-off failures are skipped while two remaining cells consume the second frame's attempt budget.");
        });
    }

    [Test]
    public async Task Streaming_AsyncLoaderDoesNotBlockTheCallingThread()
    {
        FoliagePatch patch = CreatePatch(
            Guid.Parse("01454406-4094-475e-b0a9-686e0a04e71e"));
        var manager = new FoliageStreamingManager();
        FoliageStreamingOptions options = Options(
            maximumLoads: 1,
            maximumRetirements: 1,
            retirementDelay: 2);
        var gate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<FoliageStreamingSnapshot> pending = manager.UpdateAsync(
            [patch],
            new Vector3(8f, 1f, 8f),
            1,
            options,
            async (_, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return true;
            }).AsTask();

        Assert.That(pending.IsCompleted, Is.False);
        gate.SetResult(true);
        FoliageStreamingSnapshot completed = await pending;
        Assert.That(completed.LoadedThisFrame, Is.EqualTo(1));
    }

    [Test]
    public void ImpostorAsset_ValidatesExplicitAndLegacyLayouts()
    {
        FoliageImpostorAsset legacy = CreateImpostor();
        FoliageImpostorAsset explicitLayout = new()
        {
            AlbedoOpacityAtlasPath = "albedo.png",
            NormalAtlasPath = "normal.png",
            DepthAtlasPath = "depth.png",
            ViewCount = 2,
            AtlasWidth = 512,
            AtlasHeight = 256,
            ViewDirections =
            [
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, -1f)
            ],
            AtlasRectangles =
            [
                new Vector4(0f, 0f, 0.5f, 1f),
                new Vector4(0.5f, 0f, 0.5f, 1f)
            ],
            SourceBounds = new BoundingBox(
                new Vector3(-1f, 0f, -1f),
                new Vector3(1f, 3f, 1f)),
            Pivot = Vector3.Zero,
            Scale = 1f,
            ContentHash = "abc"
        };
        FoliageImpostorAsset invalidRectangle = new()
        {
            AlbedoOpacityAtlasPath = "albedo.png",
            NormalAtlasPath = "normal.png",
            DepthAtlasPath = "depth.png",
            ViewCount = 1,
            AtlasWidth = 64,
            AtlasHeight = 64,
            ViewDirections = [Vector3.UnitZ],
            AtlasRectangles = [new Vector4(0.8f, 0f, 0.4f, 1f)],
            SourceBounds = explicitLayout.SourceBounds,
            Scale = 1f
        };

        Assert.Multiple(() =>
        {
            Assert.That(legacy.IsComplete, Is.True);
            Assert.That(legacy.HasExplicitViewLayout, Is.False);
            Assert.That(explicitLayout.IsComplete, Is.True);
            Assert.That(explicitLayout.HasExplicitViewLayout, Is.True);
            Assert.That(invalidRectangle.IsComplete, Is.False);
        });
    }

    [Test]
    public void SceneJson_PreservesImpostorViewLayout()
    {
        var source = new SceneDocument
        {
            FoliagePrototypes =
            [
                new SceneFoliagePrototypeDocument
                {
                    Model = new SceneAssetReferenceDocument("tree.njmodel"),
                    Impostor = new SceneFoliageImpostorDocument
                    {
                        AlbedoOpacityAtlasPath = "tree.albedo.png",
                        NormalAtlasPath = "tree.normal.png",
                        DepthAtlasPath = "tree.depth.png",
                        ViewCount = 1,
                        AtlasWidth = 256,
                        AtlasHeight = 256,
                        Views =
                        [
                            new SceneFoliageImpostorViewDocument
                            {
                                Direction = new SceneVector3(0f, 0f, 1f),
                                AtlasRectangle = new SceneVector4(
                                    0f, 0f, 1f, 1f)
                            }
                        ],
                        SourceBounds = new SceneBoundingBox(
                            new SceneVector3(-1f, 0f, -1f),
                            new SceneVector3(1f, 3f, 1f)),
                        Scale = 1f,
                        ContentHash = "layout-hash"
                    }
                }
            ]
        };

        string json = SceneDocumentJson.Serialize(source);
        SceneDocument roundTrip = System.Text.Json.JsonSerializer
            .Deserialize<SceneDocument>(json, SceneDocumentJson.Options)!;
        SceneFoliageImpostorDocument result =
            roundTrip.FoliagePrototypes.Single().Impostor!;

        Assert.Multiple(() =>
        {
            Assert.That(result.AtlasWidth, Is.EqualTo(256));
            Assert.That(result.AtlasHeight, Is.EqualTo(256));
            Assert.That(result.Views, Has.Count.EqualTo(1));
            Assert.That(result.Views[0].Direction,
                Is.EqualTo(new SceneVector3(0f, 0f, 1f)));
            Assert.That(result.Views[0].AtlasRectangle,
                Is.EqualTo(new SceneVector4(0f, 0f, 1f, 1f)));
        });
    }

    [Test]
    public void AuthoredExpansionAndImpostorSelection_AreActiveShaderContracts()
    {
        string cull = ReadShader("foliage_cull.comp");
        string expand = ReadShader("foliage_authored_expand.comp");
        string grass = ReadShader("foliage_grass.mesh");
        string main = cull[cull.LastIndexOf("void main()", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(cull, Does.Contain("EmitAuthoredInstanceCommand"));
            Assert.That(main, Does.Contain("ProcessCluster"));
            Assert.That(main, Does.Not.Contain("ProcessAuthoredCluster"));
            Assert.That(expand, Does.Contain("layout(local_size_x = 64"));
            Assert.That(expand, Does.Contain("meshletSlot += gl_WorkGroupSize.x"));
            Assert.That(expand, Does.Contain("command.TargetMeshletCount"));
            Assert.That(grass, Does.Contain("SelectFoliageImpostorView"));
            Assert.That(grass, Does.Contain("AtlasRectangle"));
        });
    }

    private static FoliageImpostorAsset CreateImpostor() => new()
    {
        AlbedoOpacityAtlasPath = "albedo.png",
        NormalAtlasPath = "normal.png",
        DepthAtlasPath = "depth.png",
        ViewCount = 4,
        SourceBounds = new BoundingBox(
            new Vector3(-1f, 0f, -1f),
            new Vector3(1f, 3f, 1f)),
        Scale = 1f
    };

    private static FoliagePatch CreatePatch(Guid id)
    {
        var prototype = new FoliagePrototype
        {
            GeometryMode = FoliageGeometryMode.ProceduralGrass
        };
        var patch = new FoliagePatch(
            prototype,
            new BoundingBox(
                new Vector3(0f, 0f, 0f),
                new Vector3(32f, 2f, 32f)))
        {
            Id = id
        };
        patch.Placement.CellSize = 16f;
        return patch;
    }

    private static FoliageStreamingOptions Options(
        int maximumLoads,
        int maximumRetirements,
        uint retirementDelay) => new(
        NearDistance: 32f,
        MidDistance: 64f,
        FarDistance: 128f,
        HysteresisDistance: 16f,
        MaximumLoadsPerFrame: maximumLoads,
        MaximumRetirementsPerFrame: maximumRetirements,
        MaximumUploadBytesPerFrame: 4UL * 128UL * 1024UL,
        EstimatedCellUploadBytes: 128UL * 1024UL,
        RetirementDelayFrames: retirementDelay,
        RetryDelayFrames: 3,
        MaximumCandidateCells: 32);

    private static string ReadShader(string name)
    {
        DirectoryInfo? cursor = new(TestContext.CurrentContext.TestDirectory);
        while (cursor != null)
        {
            string path = Path.Combine(cursor.FullName, "Njulf.Shaders", name);
            if (File.Exists(path))
                return File.ReadAllText(path);
            cursor = cursor.Parent;
        }
        throw new FileNotFoundException($"Could not locate shader '{name}'.");
    }
}
