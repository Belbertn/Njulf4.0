using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FoliageSceneModelTests
{
    [Test]
    public void Scene_AddFoliagePatchAddsPrototypeAndRemovePrototypeRemovesPatches()
    {
        var scene = new Scene();
        var prototype = new FoliagePrototype { Name = "grass", Mesh = new MeshHandle(1, 1) };
        var patch = new FoliagePatch(prototype, UnitBounds()) { Name = "patch" };

        scene.Add(patch);

        Assert.Multiple(() =>
        {
            Assert.That(scene.FoliagePrototypes, Is.EquivalentTo(new[] { prototype }));
            Assert.That(scene.FoliagePatches, Is.EquivalentTo(new[] { patch }));
        });

        scene.Remove(prototype);

        Assert.Multiple(() =>
        {
            Assert.That(scene.FoliagePrototypes, Is.Empty);
            Assert.That(scene.FoliagePatches, Is.Empty);
        });
    }

    [Test]
    public void FoliagePatch_RevisionTracksPlacementAndPrototypeRevision()
    {
        var prototype = new FoliagePrototype { Mesh = new MeshHandle(1, 1) };
        var patch = new FoliagePatch(prototype, UnitBounds());
        uint initialPatchRevision = patch.Revision;
        uint initialContentRevision = patch.ContentRevision;

        patch.Density = 2.0f;
        patch.InstancePosition = new Vector3(1f, 2f, 3f);
        patch.InstanceScale = 2f;
        uint placementRevision = patch.Revision;
        prototype.AuthoredMeshletStride = 4;
        prototype.Material = new MaterialHandle(2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(placementRevision, Is.Not.EqualTo(initialPatchRevision));
            Assert.That(patch.ContentRevision, Is.Not.EqualTo(initialContentRevision));
            Assert.That(patch.Revision, Is.EqualTo(placementRevision), "Prototype changes must not mutate the patch revision itself.");
        });
    }

    [Test]
    public void FoliageManager_RegisterSceneTracksCountsAndRevisionChanges()
    {
        var scene = new Scene();
        var prototype = new FoliagePrototype { Mesh = new MeshHandle(1, 1) };
        var patch = new FoliagePatch(prototype, UnitBounds()) { Density = 1f };
        scene.Add(patch);
        var manager = new FoliageManager();

        FoliageSceneRegistrationSnapshot first = manager.RegisterScene(scene);
        patch.Seed++;
        FoliageSceneRegistrationSnapshot second = manager.RegisterScene(scene);
        prototype.GeometryMode = FoliageGeometryMode.AuthoredMeshlets;
        FoliageSceneRegistrationSnapshot third = manager.RegisterScene(scene);

        Assert.Multiple(() =>
        {
            Assert.That(first.PrototypeCount, Is.EqualTo(1));
            Assert.That(first.PatchCount, Is.EqualTo(1));
            Assert.That(first.VisiblePatchCount, Is.EqualTo(1));
            Assert.That(second.Revision, Is.Not.EqualTo(first.Revision));
            Assert.That(third.Revision, Is.Not.EqualTo(second.Revision));
        });
    }

    [Test]
    public void FoliageManager_DebugFallbackIsCappedAndDeterministic()
    {
        var scene = new Scene();
        var prototype = new FoliagePrototype
        {
            Mesh = new MeshHandle(3, 1),
            Material = new MaterialHandle(4, 1)
        };
        var patch = new FoliagePatch(
            prototype,
            new BoundingBox(new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, 5f)))
        {
            Density = 10f,
            Seed = 1234u
        };
        scene.Add(patch);
        var manager = new FoliageManager();
        var options = new FoliageDebugFallbackOptions { MaxInstancesPerPatch = 16 };

        FoliageDebugFallbackResult first = manager.ApplyDebugFallback(scene, options);
        Matrix4x4 firstMatrix = first.Batches[0].WorldMatrices[0];
        manager.ClearDebugFallback(scene);
        FoliageDebugFallbackResult second = manager.ApplyDebugFallback(scene, options);

        Assert.Multiple(() =>
        {
            Assert.That(first.GeneratedInstanceCount, Is.EqualTo(16));
            Assert.That(first.DroppedInstanceCount, Is.EqualTo(984));
            Assert.That(first.WasCapped, Is.True);
            Assert.That(first.Batches, Has.Count.EqualTo(1));
            Assert.That(first.Batches[0].Name, Does.StartWith("FoliageDebugFallback."));
            Assert.That(first.Batches[0].Mesh, Is.EqualTo(prototype.Mesh));
            Assert.That(first.Batches[0].Material, Is.EqualTo(prototype.Material));
            Assert.That(second.Batches[0].WorldMatrices[0], Is.EqualTo(firstMatrix));
        });
    }

    [Test]
    public void FoliageManager_ClearDebugFallbackRemovesOwnedStaticBatchesOnly()
    {
        var scene = new Scene();
        var existingBatch = new StaticInstanceBatch(new[] { Matrix4x4.Identity });
        scene.Add(existingBatch);
        var prototype = new FoliagePrototype { Mesh = new MeshHandle(3, 1) };
        scene.Add(new FoliagePatch(prototype, UnitBounds()) { Density = 1f });
        var manager = new FoliageManager();

        manager.ApplyDebugFallback(scene);
        manager.ClearDebugFallback(scene);

        Assert.That(scene.StaticInstanceBatches, Is.EquivalentTo(new[] { existingBatch }));
    }

    [Test]
    public void FoliageManager_PrepareFrameGeneratesDeterministicClustersFromPatchSeed()
    {
        var firstScene = CreateClusterScene(1234u);
        var secondScene = CreateClusterScene(1234u);
        var manager = new FoliageManager();
        var settings = new FoliageSettings { DensityScale = 1f, MaxVisibleClusters = 128 };

        FoliageGpuBuildSnapshot first = manager.PrepareFrame(firstScene, settings, default, new SceneRenderingData());
        FoliageGpuBuildSnapshot second = manager.PrepareFrame(secondScene, settings, default, new SceneRenderingData());

        Assert.Multiple(() =>
        {
            Assert.That(first.PrototypeCount, Is.EqualTo(1));
            Assert.That(first.PatchCount, Is.EqualTo(1));
            Assert.That(first.ClusterCount, Is.GreaterThan(0));
            Assert.That(second.ClusterCount, Is.EqualTo(first.ClusterCount));
            Assert.That(second.GrassBladeEstimate, Is.EqualTo(first.GrassBladeEstimate));
            Assert.That(second.ClusterSignature, Is.EqualTo(first.ClusterSignature));
        });
    }

    [Test]
    public void FoliageManager_PrepareFrameChangesClusterSignatureWhenPatchSeedChanges()
    {
        var firstScene = CreateClusterScene(1234u);
        var secondScene = CreateClusterScene(5678u);
        var settings = new FoliageSettings { DensityScale = 1f, MaxVisibleClusters = 128 };

        FoliageGpuBuildSnapshot first = new FoliageManager().PrepareFrame(firstScene, settings, default, new SceneRenderingData());
        FoliageGpuBuildSnapshot second = new FoliageManager().PrepareFrame(secondScene, settings, default, new SceneRenderingData());

        Assert.That(second.ClusterSignature, Is.Not.EqualTo(first.ClusterSignature));
    }

    [Test]
    public void FoliageManager_PrepareFrameSkipsUploadWhenOnlyFrameDataChanges()
    {
        var scene = CreateClusterScene(1234u);
        var manager = new FoliageManager();
        var settings = new FoliageSettings { DensityScale = 1f, MaxVisibleClusters = 128 };
        var firstFrame = new SceneRenderingData { FrameIndex = 0 };
        var secondFrame = new SceneRenderingData { FrameIndex = 1, CameraPosition = new Vector3(10f, 2f, -5f) };

        manager.PrepareFrame(scene, settings, default, firstFrame);
        FoliageGpuBuildSnapshot first = manager.LastGpuBuildSnapshot;
        manager.PrepareFrame(scene, settings, default, secondFrame);
        FoliageGpuBuildSnapshot second = manager.LastGpuBuildSnapshot;

        Assert.Multiple(() =>
        {
            Assert.That(manager.LastUploadBytes, Is.EqualTo(0));
            Assert.That(manager.LastContentChanged, Is.False);
            Assert.That(second.ContentSignature, Is.EqualTo(first.ContentSignature));
            Assert.That(second.ClusterSignature, Is.EqualTo(first.ClusterSignature));
        });
    }

    private static BoundingBox UnitBounds()
    {
        return new BoundingBox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 1f));
    }

    private static Scene CreateClusterScene(uint seed)
    {
        var scene = new Scene();
        var prototype = new FoliagePrototype
        {
            Mesh = new MeshHandle(3, 1),
            Material = new MaterialHandle(4, 1),
            GeometryMode = FoliageGeometryMode.ProceduralGrass
        };
        var patch = new FoliagePatch(
            prototype,
            new BoundingBox(new Vector3(-8f, 0f, -8f), new Vector3(8f, 2f, 8f)))
        {
            Density = 2f,
            Seed = seed
        };

        scene.Add(patch);
        return scene;
    }
}
