using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

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
            Assert.That(descriptor.BoundsExtent.Y, Is.GreaterThanOrEqualTo(descriptor.VoxelSize * MeshSdfBakePlanner.MinBakeBoundsVoxelsPerAxis).Within(1.0e-6f));
        });
    }

    [Test]
    public void CreateDescriptor_ClampsZeroThicknessBakeBoundsToTwoVoxels()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(-1.0f, 0.0f, -1.0f),
            BoundingBoxMax = new Vector3(1.0f, 0.0f, 1.0f),
            VertexCount = 4,
            IndexCount = 6
        };

        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.BoundsExtent.Y, Is.GreaterThanOrEqualTo(descriptor.VoxelSize * MeshSdfBakePlanner.MinBakeBoundsVoxelsPerAxis).Within(1.0e-6f));
            Assert.That(descriptor.BoundsMin.Y, Is.LessThan(0.0f));
            Assert.That(descriptor.BoundsMax.Y, Is.GreaterThan(0.0f));
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

    [Test]
    public void TryCreateInstanceGpuRecord_PacksInstanceBoundsInverseTransformAndDistanceScale()
    {
        GPUMeshSdf bakedRecord = new()
        {
            LocalBoundsMinAndVoxelSize = new CoreVector4(-1.0f, -2.0f, -3.0f, 0.1f),
            LocalBoundsExtentAndInvVoxelSize = new CoreVector4(2.0f, 4.0f, 6.0f, 10.0f),
            TextureIndex = 7,
            MeshIndex = 11
        };
        CoreMatrix4x4 worldMatrix =
            CoreMatrix4x4.CreateScale(new CoreVector3(2.0f, 3.0f, 4.0f)) *
            CoreMatrix4x4.CreateTranslation(new CoreVector3(10.0f, 20.0f, 30.0f));

        bool created = MeshSdfManager.TryCreateInstanceGpuRecord(bakedRecord, worldMatrix, out GPUMeshSdf instanceRecord);

        Assert.That(created, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(instanceRecord.WorldBoundsMinAndDistanceScale.X, Is.EqualTo(7.8f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndDistanceScale.Y, Is.EqualTo(13.8f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndDistanceScale.Z, Is.EqualTo(17.8f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndDistanceScale.W, Is.EqualTo(2.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndInvDistanceScale.X, Is.EqualTo(12.2f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndInvDistanceScale.Y, Is.EqualTo(26.2f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndInvDistanceScale.Z, Is.EqualTo(42.2f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndInvDistanceScale.W, Is.EqualTo(0.5f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow0.X, Is.EqualTo(0.5f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow0.W, Is.EqualTo(-5.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow1.Y, Is.EqualTo(1.0f / 3.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow1.W, Is.EqualTo(-20.0f / 3.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow2.Z, Is.EqualTo(0.25f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldToLocalRow2.W, Is.EqualTo(-7.5f).Within(1.0e-5f));
            Assert.That(instanceRecord.TextureIndex, Is.EqualTo(7u));
            Assert.That(instanceRecord.MeshIndex, Is.EqualTo(11u));
        });
    }

    [Test]
    public void TryCreateInstanceGpuRecord_RejectsSingularTransforms()
    {
        GPUMeshSdf bakedRecord = new()
        {
            LocalBoundsMinAndVoxelSize = new CoreVector4(-1.0f, -1.0f, -1.0f, 0.1f),
            LocalBoundsExtentAndInvVoxelSize = new CoreVector4(2.0f, 2.0f, 2.0f, 10.0f)
        };

        bool created = MeshSdfManager.TryCreateInstanceGpuRecord(
            bakedRecord,
            CoreMatrix4x4.CreateScale(new CoreVector3(0.0f, 1.0f, 1.0f)),
            out _);

        Assert.That(created, Is.False);
    }

    [Test]
    public void CreateBakeFlags_MarksOpenMeshesForUnsignedFallback()
    {
        Vector3[] positions =
        [
            new(0.0f, 0.0f, 0.0f),
            new(1.0f, 0.0f, 0.0f),
            new(0.0f, 1.0f, 0.0f)
        ];
        uint[] indices = [0u, 1u, 2u];

        uint flags = MeshSdfBakePlanner.CreateBakeFlags(positions, indices);

        Assert.That(flags & MeshSdfBakePlanner.MeshSdfFlagUnsignedFallback, Is.Not.Zero);
    }

    [Test]
    public void CreateBakeFlags_KeepsClosedTwoManifoldMeshesSigned()
    {
        Vector3[] positions =
        [
            new(0.0f, 0.0f, 0.0f),
            new(1.0f, 0.0f, 0.0f),
            new(0.0f, 1.0f, 0.0f),
            new(0.0f, 0.0f, 1.0f)
        ];
        uint[] indices =
        [
            0u, 2u, 1u,
            0u, 1u, 3u,
            1u, 2u, 3u,
            2u, 0u, 3u
        ];

        uint flags = MeshSdfBakePlanner.CreateBakeFlags(positions, indices);

        Assert.That(flags & MeshSdfBakePlanner.MeshSdfFlagUnsignedFallback, Is.Zero);
    }

    [Test]
    public void CreateBakeFlags_MarksDegenerateTrianglesForUnsignedFallback()
    {
        Vector3[] positions =
        [
            new(0.0f, 0.0f, 0.0f),
            new(1.0f, 0.0f, 0.0f),
            new(2.0f, 0.0f, 0.0f)
        ];
        uint[] indices = [0u, 1u, 2u];

        uint flags = MeshSdfBakePlanner.CreateBakeFlags(positions, indices);

        Assert.That(flags & MeshSdfBakePlanner.MeshSdfFlagUnsignedFallback, Is.Not.Zero);
    }
}
