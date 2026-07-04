using System.Numerics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshSdfBakePlannerTests
{
    [Test]
    public void CreateDescriptor_ClampsResolutionAndKeepsThinAxesRepresented()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(-10.0f, -0.01f, -1.0f),
            BoundingBoxMax = new Vector3(10.0f, 0.01f, 1.0f),
            VertexCount = 8,
            IndexCount = 36
        };

        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Extent.Width, Is.InRange(MeshSdfBakePlanner.MinResolution, MeshSdfBakePlanner.MaxResolution));
            Assert.That(descriptor.Extent.Height, Is.EqualTo(MeshSdfBakePlanner.MinResolution));
            Assert.That(descriptor.Extent.Depth, Is.InRange(MeshSdfBakePlanner.MinResolution, MeshSdfBakePlanner.MaxResolution));
            Assert.That(descriptor.VoxelSize, Is.GreaterThan(0.0f));
            Assert.That(descriptor.BoundsMin.X, Is.LessThan(meshInfo.BoundingBoxMin.X));
            Assert.That(descriptor.BoundsMax.X, Is.GreaterThan(meshInfo.BoundingBoxMax.X));
        });
    }

    [Test]
    public void GetVoxelAddress_MapsCornersAndCenterIntoLocalBounds()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(0.0f, 0.0f, 0.0f),
            BoundingBoxMax = new Vector3(1.0f, 2.0f, 4.0f),
            VertexCount = 8,
            IndexCount = 36
        };
        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        MeshSdfVoxelAddress min = MeshSdfBakePlanner.GetVoxelAddress(descriptor, 0, 0, 0);
        MeshSdfVoxelAddress max = MeshSdfBakePlanner.GetVoxelAddress(
            descriptor,
            descriptor.Extent.Width - 1,
            descriptor.Extent.Height - 1,
            descriptor.Extent.Depth - 1);

        Assert.Multiple(() =>
        {
            Assert.That(min.LocalPosition.X, Is.EqualTo(descriptor.BoundsMin.X).Within(1.0e-6f));
            Assert.That(min.LocalPosition.Y, Is.EqualTo(descriptor.BoundsMin.Y).Within(1.0e-6f));
            Assert.That(min.LocalPosition.Z, Is.EqualTo(descriptor.BoundsMin.Z).Within(1.0e-6f));
            Assert.That(max.LocalPosition.X, Is.EqualTo(descriptor.BoundsMax.X).Within(1.0e-6f));
            Assert.That(max.LocalPosition.Y, Is.EqualTo(descriptor.BoundsMax.Y).Within(1.0e-6f));
            Assert.That(max.LocalPosition.Z, Is.EqualTo(descriptor.BoundsMax.Z).Within(1.0e-6f));
            Assert.That(max.NormalizedUv, Is.EqualTo(Vector3.One));
        });
    }
}
