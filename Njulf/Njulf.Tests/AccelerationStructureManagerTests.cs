using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed unsafe class AccelerationStructureManagerTests
{
    [Test]
    public void CreateTransform_StoresVulkanThreeByFourMatrix()
    {
        var matrix = new Matrix4x4(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f);

        TransformMatrixKHR transform = AccelerationStructureManager.CreateTransform(matrix);
        float* values = transform.Matrix;

        Assert.Multiple(() =>
        {
            Assert.That(values[0], Is.EqualTo(1f));
            Assert.That(values[1], Is.EqualTo(5f));
            Assert.That(values[2], Is.EqualTo(9f));
            Assert.That(values[3], Is.EqualTo(13f));
            Assert.That(values[4], Is.EqualTo(2f));
            Assert.That(values[5], Is.EqualTo(6f));
            Assert.That(values[6], Is.EqualTo(10f));
            Assert.That(values[7], Is.EqualTo(14f));
            Assert.That(values[8], Is.EqualTo(3f));
            Assert.That(values[9], Is.EqualTo(7f));
            Assert.That(values[10], Is.EqualTo(11f));
            Assert.That(values[11], Is.EqualTo(15f));
        });
    }

    [Test]
    public void CreateInstance_PacksStaticOpaqueMetadata()
    {
        const ulong blasAddress = 0x1234_5678_9ABC_DEF0UL;
        const uint customIndex = 0x1FF_FFFFu;

        AccelerationStructureInstanceKHR instance = AccelerationStructureManager.CreateInstance(
            Matrix4x4.Identity,
            blasAddress,
            customIndex,
            AccelerationStructureManager.StaticOpaqueInstanceMask);

        Assert.Multiple(() =>
        {
            Assert.That(instance.InstanceCustomIndex, Is.EqualTo(0x00FF_FFFFu));
            Assert.That(instance.Mask, Is.EqualTo(AccelerationStructureManager.StaticOpaqueInstanceMask));
            Assert.That(instance.InstanceShaderBindingTableRecordOffset, Is.EqualTo(0u));
            Assert.That(instance.Flags, Is.EqualTo(GeometryInstanceFlagsKHR.ForceOpaqueBitKhr));
            Assert.That(instance.AccelerationStructureReference, Is.EqualTo(blasAddress));
        });
    }

    [Test]
    public void CreateRayQueryInstanceMetadata_UsesMeshOffsetsMaterialAndNormalMatrix()
    {
        var meshInfo = new MeshInfo
        {
            VertexOffset = 12u,
            IndexOffset = 34u
        };
        var world = Matrix4x4.CreateScale(new Vector3(2f, 3f, 4f));
        var source = new AccelerationStructureManager.StaticOpaqueInstance(
            new MeshHandle(7, 1),
            meshInfo,
            56u,
            world);

        GPUDdgiRayQueryInstance metadata = AccelerationStructureManager.CreateRayQueryInstanceMetadata(source);
        Matrix4x4 expectedNormalMatrix = world.Invert().Transpose();

        Assert.Multiple(() =>
        {
            Assert.That(metadata.VertexOffset, Is.EqualTo(12u));
            Assert.That(metadata.IndexOffset, Is.EqualTo(34u));
            Assert.That(metadata.MaterialIndex, Is.EqualTo(56u));
            Assert.That(metadata.Padding0, Is.EqualTo(0u));
            Assert.That(metadata.WorldMatrixInverseTranspose, Is.EqualTo(expectedNormalMatrix));
        });
    }

    [Test]
    public void MeshGeometryBuffers_DeclareAccelerationStructureBuildInputUsage()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MeshManager.VertexPositionBufferUsage & BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                Is.EqualTo(BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr));
            Assert.That(
                MeshManager.IndexBufferUsage & BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                Is.EqualTo(BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr));
            Assert.That(
                MeshManager.IndexBufferUsage & BufferUsageFlags.IndexBufferBit,
                Is.EqualTo(BufferUsageFlags.IndexBufferBit));
        });
    }
}
