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
    public void CreateDescriptor_LargeThinMeshesUseHigherLongAxisResolution()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(-80.0f, -0.03f, -0.5f),
            BoundingBoxMax = new Vector3(80.0f, 0.03f, 0.5f),
            VertexCount = 8,
            IndexCount = 36
        };

        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        Assert.Multiple(() =>
        {
            Assert.That(MeshSdfBakePlanner.MaxResolution, Is.EqualTo(128));
            Assert.That(descriptor.Extent.Width, Is.GreaterThan(64));
            Assert.That(descriptor.Extent.Width, Is.LessThanOrEqualTo(MeshSdfBakePlanner.MaxResolution));
            Assert.That(descriptor.Extent.Height, Is.EqualTo(MeshSdfBakePlanner.MinResolution));
            Assert.That(descriptor.BoundsExtent.Y, Is.GreaterThanOrEqualTo(descriptor.VoxelSize * MeshSdfBakePlanner.MinBakeBoundsVoxelsPerAxis).Within(1.0e-6f));
        });
    }

    [Test]
    public void CreateDescriptor_ThirtyMeterMeshCapsTargetVoxelSize()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(-15.0f, -0.11f, -15.0f),
            BoundingBoxMax = new Vector3(15.0f, 0.11f, 15.0f),
            VertexCount = 24,
            IndexCount = 36
        };

        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.Extent.Width, Is.GreaterThan(64));
            Assert.That(descriptor.Extent.Depth, Is.GreaterThan(64));
            Assert.That(descriptor.VoxelSize, Is.LessThanOrEqualTo(MeshSdfBakePlanner.MaxTargetVoxelSize));
        });
    }

    [Test]
    public void GetVoxelAddress_MapsTexelCentersIntoLocalBounds()
    {
        MeshInfo meshInfo = new()
        {
            BoundingBoxMin = new Vector3(0.0f, 0.0f, 0.0f),
            BoundingBoxMax = new Vector3(1.0f, 2.0f, 4.0f),
            VertexCount = 8,
            IndexCount = 36
        };
        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(meshInfo);

        MeshSdfVoxelAddress first = MeshSdfBakePlanner.GetVoxelAddress(descriptor, 0, 0, 0);
        MeshSdfVoxelAddress last = MeshSdfBakePlanner.GetVoxelAddress(
            descriptor,
            descriptor.Extent.Width - 1,
            descriptor.Extent.Height - 1,
            descriptor.Extent.Depth - 1);
        Vector3 firstUv = new(
            0.5f / descriptor.Extent.Width,
            0.5f / descriptor.Extent.Height,
            0.5f / descriptor.Extent.Depth);
        Vector3 lastUv = new(
            (descriptor.Extent.Width - 0.5f) / descriptor.Extent.Width,
            (descriptor.Extent.Height - 0.5f) / descriptor.Extent.Height,
            (descriptor.Extent.Depth - 0.5f) / descriptor.Extent.Depth);

        Assert.Multiple(() =>
        {
            Assert.That(first.NormalizedUv.X, Is.EqualTo(firstUv.X).Within(1.0e-6f));
            Assert.That(first.NormalizedUv.Y, Is.EqualTo(firstUv.Y).Within(1.0e-6f));
            Assert.That(first.NormalizedUv.Z, Is.EqualTo(firstUv.Z).Within(1.0e-6f));
            Assert.That(first.LocalPosition.X, Is.EqualTo(descriptor.BoundsMin.X + descriptor.BoundsExtent.X * firstUv.X).Within(1.0e-6f));
            Assert.That(first.LocalPosition.Y, Is.EqualTo(descriptor.BoundsMin.Y + descriptor.BoundsExtent.Y * firstUv.Y).Within(1.0e-6f));
            Assert.That(first.LocalPosition.Z, Is.EqualTo(descriptor.BoundsMin.Z + descriptor.BoundsExtent.Z * firstUv.Z).Within(1.0e-6f));
            Assert.That(last.NormalizedUv.X, Is.EqualTo(lastUv.X).Within(1.0e-6f));
            Assert.That(last.NormalizedUv.Y, Is.EqualTo(lastUv.Y).Within(1.0e-6f));
            Assert.That(last.NormalizedUv.Z, Is.EqualTo(lastUv.Z).Within(1.0e-6f));
            Assert.That(last.LocalPosition.X, Is.EqualTo(descriptor.BoundsMin.X + descriptor.BoundsExtent.X * lastUv.X).Within(1.0e-6f));
            Assert.That(last.LocalPosition.Y, Is.EqualTo(descriptor.BoundsMin.Y + descriptor.BoundsExtent.Y * lastUv.Y).Within(1.0e-6f));
            Assert.That(last.LocalPosition.Z, Is.EqualTo(descriptor.BoundsMin.Z + descriptor.BoundsExtent.Z * lastUv.Z).Within(1.0e-6f));
        });
    }

    [Test]
    public void TryCreateInstanceGpuRecord_PacksInstanceBoundsInverseTransformAndPerAxisScale()
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
            Assert.That(instanceRecord.WorldBoundsMinAndLocalScaleX.X, Is.EqualTo(7.6f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndLocalScaleX.Y, Is.EqualTo(13.6f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndLocalScaleX.Z, Is.EqualTo(17.6f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMinAndLocalScaleX.W, Is.EqualTo(2.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndLocalScaleY.X, Is.EqualTo(12.4f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndLocalScaleY.Y, Is.EqualTo(26.4f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndLocalScaleY.Z, Is.EqualTo(42.4f).Within(1.0e-5f));
            Assert.That(instanceRecord.WorldBoundsMaxAndLocalScaleY.W, Is.EqualTo(3.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.LocalToWorldAxisScale.X, Is.EqualTo(2.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.LocalToWorldAxisScale.Y, Is.EqualTo(3.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.LocalToWorldAxisScale.Z, Is.EqualTo(4.0f).Within(1.0e-5f));
            Assert.That(instanceRecord.LocalToWorldAxisScale.W, Is.EqualTo(4.0f).Within(1.0e-5f));
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
    public void CreateBakeFlags_WeldsPerFaceNormalCubeByPosition()
    {
        Vector3[] positions =
        [
            new(-1.0f, -1.0f, -1.0f), new(1.0f, -1.0f, -1.0f), new(1.0f, 1.0f, -1.0f), new(-1.0f, 1.0f, -1.0f),
            new(1.0f, -1.0f, 1.0f), new(-1.0f, -1.0f, 1.0f), new(-1.0f, 1.0f, 1.0f), new(1.0f, 1.0f, 1.0f),
            new(-1.0f, -1.0f, 1.0f), new(-1.0f, -1.0f, -1.0f), new(-1.0f, 1.0f, -1.0f), new(-1.0f, 1.0f, 1.0f),
            new(1.0f, -1.0f, -1.0f), new(1.0f, -1.0f, 1.0f), new(1.0f, 1.0f, 1.0f), new(1.0f, 1.0f, -1.0f),
            new(-1.0f, -1.0f, 1.0f), new(1.0f, -1.0f, 1.0f), new(1.0f, -1.0f, -1.0f), new(-1.0f, -1.0f, -1.0f),
            new(-1.0f, 1.0f, -1.0f), new(1.0f, 1.0f, -1.0f), new(1.0f, 1.0f, 1.0f), new(-1.0f, 1.0f, 1.0f)
        ];
        uint[] indices =
        [
            0u, 2u, 1u, 0u, 3u, 2u,
            4u, 6u, 5u, 4u, 7u, 6u,
            8u, 10u, 9u, 8u, 11u, 10u,
            12u, 14u, 13u, 12u, 15u, 14u,
            16u, 18u, 17u, 16u, 19u, 18u,
            20u, 22u, 21u, 20u, 23u, 22u
        ];

        uint flags = MeshSdfBakePlanner.CreateBakeFlags(positions, indices);

        Assert.That(flags, Is.Zero);
    }

    [Test]
    public void CreateBakeFlags_KeepsActualOpenPlaneUnsignedAfterPositionWeld()
    {
        Vector3[] positions =
        [
            new(-1.0f, 0.0f, -1.0f),
            new(1.0f, 0.0f, -1.0f),
            new(1.0f, 0.0f, 1.0f),
            new(-1.0f, 0.0f, 1.0f)
        ];
        uint[] indices =
        [
            0u, 1u, 2u,
            0u, 2u, 3u
        ];

        uint flags = MeshSdfBakePlanner.CreateBakeFlags(positions, indices);

        Assert.That(flags & MeshSdfBakePlanner.MeshSdfFlagUnsignedFallback, Is.Not.Zero);
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
