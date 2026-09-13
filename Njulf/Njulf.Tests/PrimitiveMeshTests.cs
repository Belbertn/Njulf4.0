using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class PrimitiveMeshTests
{
    [TestCase("Box", false)]
    [TestCase("Box", true)]
    [TestCase("Sphere", false)]
    [TestCase("Sphere", true)]
    [TestCase("Plane", false)]
    [TestCase("Plane", true)]
    [TestCase("Capsule", false)]
    [TestCase("Capsule", true)]
    public void FactoriesProduceValidSurfaceDataMatchingColliderDimensions(string kind, bool tangents)
    {
        ModelMesh mesh = kind switch
        {
            "Box" => PrimitiveMesh.Box(new(2, 4, 6), tangents),
            "Sphere" => PrimitiveMesh.Sphere(1, tangents: tangents),
            "Plane" => PrimitiveMesh.Plane(2, 6, tangents),
            _ => PrimitiveMesh.Capsule(1, 2, tangents: tangents)
        };
        Vector3 expectedSize = kind switch { "Box" => new(2, 4, 6), "Plane" => new(2, 0, 6), "Sphere" => new(2), _ => new(2, 4, 2) };
        Assert.That(mesh.BoundingBox.Size, Is.EqualTo(expectedSize));
        Assert.That(BoundingBox.FromPoints(mesh.Vertices).Size, Is.EqualTo(expectedSize));
        Assert.That(mesh.Normals.Length, Is.EqualTo(mesh.Vertices.Length));
        Assert.That(mesh.TexCoords.Length, Is.EqualTo(mesh.Vertices.Length));
        Assert.That(mesh.Tangents.Length, Is.EqualTo(tangents ? mesh.Vertices.Length : 0));
        Assert.That(mesh.Bitangents.Length, Is.EqualTo(mesh.Tangents.Length));
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Assert.That(mesh.Normals[i].Length(), Is.EqualTo(1).Within(1e-5));
            Assert.That(mesh.TexCoords[i].X, Is.InRange(0, 1)); Assert.That(mesh.TexCoords[i].Y, Is.InRange(0, 1));
            if (!tangents) continue;
            Assert.That(mesh.Tangents[i].Length(), Is.EqualTo(1).Within(1e-5));
            Assert.That(mesh.Bitangents[i].Length(), Is.EqualTo(1).Within(1e-5));
            Assert.That(Vector3.Dot(mesh.Normals[i], mesh.Tangents[i]), Is.EqualTo(0).Within(1e-5));
            Assert.That(Vector3.Dot(mesh.Normals[i], mesh.Bitangents[i]), Is.EqualTo(0).Within(1e-5));
        }
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            Assert.That(a, Is.LessThan(mesh.Vertices.Length)); Assert.That(b, Is.LessThan(mesh.Vertices.Length)); Assert.That(c, Is.LessThan(mesh.Vertices.Length));
            Vector3 cross = Vector3.Cross(mesh.Vertices[b] - mesh.Vertices[a], mesh.Vertices[c] - mesh.Vertices[a]);
            Assert.That(Vector3.Dot(cross, mesh.Normals[a] + mesh.Normals[b] + mesh.Normals[c]), Is.GreaterThan(0));
            if (tangents)
            {
                Vector2 uv1 = mesh.TexCoords[b] - mesh.TexCoords[a], uv2 = mesh.TexCoords[c] - mesh.TexCoords[a];
                float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
                Vector3 edge1 = mesh.Vertices[b] - mesh.Vertices[a], edge2 = mesh.Vertices[c] - mesh.Vertices[a];
                Vector3 du = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
                Vector3 dv = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
                Assert.That(Vector3.Dot(du, mesh.Tangents[a] + mesh.Tangents[b] + mesh.Tangents[c]), Is.GreaterThan(0));
                Assert.That(Vector3.Dot(dv, mesh.Bitangents[a] + mesh.Bitangents[b] + mesh.Bitangents[c]), Is.GreaterThan(0));
            }
        }
        if (kind is "Sphere" or "Capsule")
            for (int row = 0; row < mesh.Vertices.Length; row += 33)
            {
                Assert.That(mesh.Vertices[row + 32], Is.EqualTo(mesh.Vertices[row]));
                Assert.That(mesh.TexCoords[row].X, Is.Zero); Assert.That(mesh.TexCoords[row + 32].X, Is.EqualTo(1));
            }
        using var physics = new PhysicsScene(PhysicsMode.QueryOnly);
        ColliderShape shape = kind switch
        {
            "Box" => ColliderShape.Box(expectedSize), "Sphere" => ColliderShape.Sphere(1),
            "Capsule" => ColliderShape.Capsule(1, 2), _ => ColliderShape.TriangleMesh(mesh.Vertices, mesh.Indices)
        };
        physics.Register(Guid.NewGuid(), [shape]);
        Assert.That(physics.Raycast(new(0, 10, 0), Vector3.Down, 20, out var hit), Is.True);
        Assert.That(hit.Point.Y, Is.EqualTo(expectedSize.Y / 2).Within(.001));
    }

    [Test]
    public void ZeroLengthCapsuleAndMinimumTessellationAreSupported()
    {
        var sphere = PrimitiveMesh.Sphere(2, 3, 2, true);
        var capsule = PrimitiveMesh.Capsule(2, 0, 3, 2, true);
        Assert.That(capsule.Vertices, Is.EqualTo(sphere.Vertices));
        Assert.That(capsule.Indices, Is.EqualTo(sphere.Indices));
        Assert.That(sphere.Indices.Length, Is.EqualTo(18));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveMesh.Box(new(1, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveMesh.Plane(1, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveMesh.Sphere(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveMesh.Sphere(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrimitiveMesh.Capsule(1, -1));
    }

    [Test]
    public void PrimitiveUsesExistingAssetProcessingPath()
    {
        ModelMesh mesh = PrimitiveMesh.Sphere(1, 8, 4, tangents: true);
        var processed = new ProcessedMeshAssetBuilder().Build(mesh);
        Assert.That(processed.SubMeshes, Has.Count.EqualTo(1));
        Assert.That(processed.VertexLayout.Has(ProcessedVertexAttribute.Normal), Is.True);
        Assert.That(processed.VertexLayout.Has(ProcessedVertexAttribute.TexCoord0), Is.True);
        Assert.That(processed.VertexLayout.Has(ProcessedVertexAttribute.Tangent), Is.True);
        Assert.That(processed.VertexLayout.Has(ProcessedVertexAttribute.Bitangent), Is.True);
        Assert.That(processed.BoundingBox, Is.EqualTo(mesh.BoundingBox));
    }
}
