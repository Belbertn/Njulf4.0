using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PhysicsGeometryTests
{
    [Test]
    public void SelectedProcessedAndDecodedCookedSubmeshProduceSameCollisionHits()
    {
        var model = new ModelMesh { Name = "Collision extraction" };
        model.Materials.Add(new ModelMaterial { Name = "First" });
        model.Materials.Add(new ModelMaterial { Name = "Second" });
        for (int i = 0; i < 2; i++)
            model.SubMeshes.Add(new ModelSubMesh { Name = $"Part {i}", MaterialIndex = i, NodeIndex = i,
                Vertices = [new(i * 4, 0, 0), new(i * 4 + 2, 0, 0), new(i * 4, 2, 0)], Indices = [0, 1, 2] });
        var processed = new ProcessedMeshAssetBuilder().Build(model);
        var raw = CollisionGeometry.Extract(processed.SubMeshes[1]);
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "Njulf.sln"))) root = Directory.GetParent(root)!.FullName;
        string directory = Path.Combine(root, ".codex-tmp", "jitter-integration", "mesh-test");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "collision.njmesh");
        CookedPackage.WriteMesh(path, CookedMeshBuilder.Build(processed), 1, 2, 3);
        var decoded = CookedPackage.LoadMesh(path, CookedAssetReaderFlags.None, out _);
        var cooked = CollisionGeometry.Extract(decoded, 1);
        Assert.That(cooked.Positions, Is.EqualTo(raw.Positions)); Assert.That(cooked.Indices, Is.EqualTo(raw.Indices));
        Assert.That(cooked.NodeIndex, Is.EqualTo(raw.NodeIndex));
        processed.SubMeshes[1].Vertices[0] = new(100); // Extraction owns its compact copy.
        Assert.That(raw.Positions[0], Is.EqualTo(new Vector3(4, 0, 0)));
        foreach (var data in new[] { raw, cooked })
        {
            using var physics = new PhysicsScene(PhysicsMode.QueryOnly);
            physics.Register(Guid.NewGuid(), [ColliderShape.TriangleMesh(data.Positions, data.Indices)]);
            Assert.That(physics.Raycast(new(4.5f, .5f, 3), -Vector3.UnitZ, 3, out var hit), Is.True);
            Assert.That(hit.Distance, Is.EqualTo(3).Within(.002));
            Assert.That(physics.Raycast(new(.5f, .5f, 3), -Vector3.UnitZ, 4, out _), Is.False, "Unrequested submesh was not retained.");
        }
    }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void ConvexHullCapsuleAndScaledOffsetMeshQueries(PhysicsMode mode)
    {
        using var physics = new PhysicsScene(mode);
        var hull = ColliderShape.ConvexHull([new(-1,-1,-1), new(1,-1,-1), new(-1,1,-1), new(1,1,-1),
            new(-1,-1,1), new(1,-1,1), new(-1,1,1), new(1,1,1)]);
        physics.Register(Guid.NewGuid(), [hull], new PhysicsPose(new(0, 0, 5)));
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 10, out var hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(4).Within(.003));
        physics.Register(Guid.NewGuid(), [ColliderShape.Capsule(.5f, 2)], new PhysicsPose(new(5, 0, 5)));
        Assert.That(physics.Raycast(new(5, 0, 0), Vector3.UnitZ, 10, out hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(4.5).Within(.003));
        var mesh = ColliderShape.TriangleMesh([new(0,0,0), new(1,0,0), new(0,1,0)], [0,1,2]);
        var transform = Matrix4x4.CreateScale(new(2,3,1)) * Matrix4x4.CreateTranslation(new(10,0,5));
        physics.Register(Guid.NewGuid(), [mesh.WithLocalTransform(transform)]);
        Assert.That(physics.Raycast(new(10.25f,.25f,0), Vector3.UnitZ, 10, out hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(5).Within(.003));
        using var dynamics = new PhysicsScene(PhysicsMode.Simulation);
        Assert.Throws<ArgumentException>(() => dynamics.Register(Guid.NewGuid(), [mesh], body: new BodySettings { Kind = BodyKind.Dynamic }));
    }

    [Test]
    public void ModelInstanceRemovalReleasesCompoundBoundToPlacement()
    {
        using var template = new Model();
        template.Add(new RenderObject()); template.Add(new RenderObject());
        using var scene = new Scene(); using var physics = new PhysicsScene(PhysicsMode.QueryOnly, scene);
        var instance = template.CreateInstance(); scene.Add(instance);
        physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(1), ColliderShape.Sphere(1).WithLocalTransform(Matrix4x4.CreateTranslation(new(2,0,0)))],
            node: instance.PlacementRoot, entity: instance);
        Assert.That(physics.ColliderCount, Is.EqualTo(1));
        scene.Detach(instance);
        Assert.That(physics.ColliderCount, Is.Zero);
        instance.Dispose();
    }
}
