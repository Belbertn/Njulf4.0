using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;
using System.IO;
using System.Linq;

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

    [Test]
    public void AlignScratchBufferAddress_AlignsReportedValidationFailureAddress()
    {
        const ulong reportedAddress = 61_213_635_408UL;
        const ulong requiredAlignment = 128UL;

        ulong alignedAddress = AccelerationStructureManager.AlignScratchBufferAddress(
            reportedAddress,
            requiredAlignment);

        Assert.Multiple(() =>
        {
            Assert.That(alignedAddress, Is.EqualTo(61_213_635_456UL));
            Assert.That(alignedAddress % requiredAlignment, Is.Zero);
        });
    }

    [Test]
    public void ScratchBufferAllocation_ReservesWorstCaseAlignmentPadding()
    {
        const ulong requiredSize = 4_096UL;
        const ulong requiredAlignment = 128UL;

        ulong allocationSize = AccelerationStructureManager.CalculateScratchBufferAllocationSize(
            requiredSize,
            requiredAlignment);

        Assert.That(allocationSize, Is.EqualTo(4_223UL));
    }

    [Test]
    public void TopLevelReservation_OnlyReservesGrowthBeyondCurrentAllocation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.CalculateAdditionalTopLevelReservation(1_024UL, 256UL),
                Is.EqualTo(768UL));
            Assert.That(
                AccelerationStructureManager.CalculateAdditionalTopLevelReservation(1_024UL, 1_024UL),
                Is.Zero);
            Assert.That(
                AccelerationStructureManager.CalculateAdditionalTopLevelReservation(512UL, 1_024UL),
                Is.Zero);
        });
    }

    [Test]
    public void RenderObject_StaticHintIsExplicitOptIn()
    {
        var renderObject = new Njulf.Core.Scene.RenderObject();

        Assert.That(renderObject.IsStatic, Is.False);

        renderObject.IsStatic = true;
        Assert.That(renderObject.IsStatic, Is.True);
    }

    [Test]
    public void ResolveGeometryPolicy_DeclaresDdgiVisibilityPolicy()
    {
        DdgiAccelerationStructureGeometryPolicy opaque = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Opaque,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Static);
        DdgiAccelerationStructureGeometryPolicy masked = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Mask,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Dynamic);
        DdgiAccelerationStructureGeometryPolicy transparent = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Blend,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Dynamic);
        DdgiAccelerationStructureGeometryPolicy skinned = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: true,
            MaterialRenderMode.Opaque,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Skinned);
        DdgiAccelerationStructureGeometryPolicy foliage = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Mask,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Foliage);

        Assert.Multiple(() =>
        {
            Assert.That(opaque.Include, Is.True);
            Assert.That(opaque.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.OpaqueTriangles));
            Assert.That(masked.Include, Is.True);
            Assert.That(masked.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.AlphaMaskApproximateOpaque));
            Assert.That(masked.InstanceFlags, Is.EqualTo(GeometryInstanceFlagsKHR.ForceOpaqueBitKhr));
            Assert.That(transparent.Include, Is.False);
            Assert.That(transparent.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent));
            Assert.That(skinned.Include, Is.True);
            Assert.That(skinned.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.SkinnedBindPoseProxy));
            Assert.That(skinned.InstanceFlags, Is.EqualTo(GeometryInstanceFlagsKHR.ForceOpaqueBitKhr));
            Assert.That(foliage.Include, Is.False);
            Assert.That(foliage.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.FoliageProxyPending));
        });
    }

    [Test]
    public void SelectTopLevelBuildAction_SkipsStaticFramesAndUpdatesDirtyTransforms()
    {
        const ulong previousSignature = 1234UL;
        const ulong movedSignature = 5678UL;

        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.SelectTopLevelBuildAction(
                    hasTopLevelAccelerationStructure: false,
                    hasPreviousSignature: false,
                    previousInstanceCount: 0,
                    previousSignature: 0,
                    currentInstanceCount: 4,
                    currentSignature: previousSignature),
                Is.EqualTo(TopLevelAccelerationStructureBuildAction.Build));
            Assert.That(
                AccelerationStructureManager.SelectTopLevelBuildAction(
                    hasTopLevelAccelerationStructure: true,
                    hasPreviousSignature: true,
                    previousInstanceCount: 4,
                    previousSignature: previousSignature,
                    currentInstanceCount: 4,
                    currentSignature: previousSignature),
                Is.EqualTo(TopLevelAccelerationStructureBuildAction.Skip));
            Assert.That(
                AccelerationStructureManager.SelectTopLevelBuildAction(
                    hasTopLevelAccelerationStructure: true,
                    hasPreviousSignature: true,
                    previousInstanceCount: 4,
                    previousSignature: previousSignature,
                    currentInstanceCount: 4,
                    currentSignature: movedSignature),
                Is.EqualTo(TopLevelAccelerationStructureBuildAction.Update));
            Assert.That(
                AccelerationStructureManager.SelectTopLevelBuildAction(
                    hasTopLevelAccelerationStructure: true,
                    hasPreviousSignature: true,
                    previousInstanceCount: 4,
                    previousSignature: previousSignature,
                    currentInstanceCount: 5,
                    currentSignature: movedSignature),
                Is.EqualTo(TopLevelAccelerationStructureBuildAction.Build));
        });
    }

    [Test]
    public void CreateInstanceSignature_ChangesForTransformMaterialAndGeometryDomain()
    {
        var meshInfo = new MeshInfo
        {
            VertexOffset = 1u,
            IndexOffset = 2u,
            VertexCount = 24u,
            IndexCount = 36u
        };
        var baseInstance = new AccelerationStructureManager.StaticOpaqueInstance(
            new MeshHandle(3, 4),
            meshInfo,
            5u,
            Matrix4x4.Identity);
        var movedInstance = baseInstance with
        {
            WorldMatrix = Matrix4x4.CreateTranslation(new Vector3(1f, 2f, 3f))
        };
        var rematerialedInstance = baseInstance with
        {
            MaterialIndex = 9u
        };
        var dynamicInstance = baseInstance with
        {
            Domain = AccelerationStructureGeometryDomain.Dynamic
        };

        ulong baseSignature = AccelerationStructureManager.CreateInstanceSignature(new[] { baseInstance });
        ulong repeatedSignature = AccelerationStructureManager.CreateInstanceSignature(new[] { baseInstance });
        ulong movedSignature = AccelerationStructureManager.CreateInstanceSignature(new[] { movedInstance });
        ulong rematerialedSignature = AccelerationStructureManager.CreateInstanceSignature(new[] { rematerialedInstance });
        ulong dynamicSignature = AccelerationStructureManager.CreateInstanceSignature(new[] { dynamicInstance });

        Assert.Multiple(() =>
        {
            Assert.That(repeatedSignature, Is.EqualTo(baseSignature));
            Assert.That(movedSignature, Is.Not.EqualTo(baseSignature));
            Assert.That(rematerialedSignature, Is.Not.EqualTo(baseSignature));
            Assert.That(dynamicSignature, Is.Not.EqualTo(baseSignature));
        });
    }

    [Test]
    public void ResidencyPolicy_UsesExplicitBoundedSettingsAndKeepsLegacyPathOptIn()
    {
        AccelerationStructureResidencyPolicy disabled = AccelerationStructureResidencyPolicy.Disabled;
        var bounded = new AccelerationStructureResidencyPolicy(
            Enabled: true,
            CameraPosition: new Vector3(10f, 20f, 30f),
            MemoryBudgetBytes: 0,
            StaticResidentDistance: 128f,
            MaximumStaticInstances: 256,
            EvictionGraceFrames: 3);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.Enabled, Is.False);
            Assert.That(disabled.EffectiveMemoryBudgetBytes, Is.EqualTo(ulong.MaxValue));
            Assert.That(bounded.Enabled, Is.True);
            Assert.That(bounded.EffectiveMemoryBudgetBytes, Is.EqualTo(16UL));
            Assert.That(bounded.MaximumStaticInstances, Is.EqualTo(256));
            Assert.That(bounded.EvictionGraceFrames, Is.EqualTo(3));
        });
    }

    [Test]
    public void AccelerationStructureManager_DoesNotWaitIdleForSteadyGrowthOrStreamingRetirement()
    {
        string source = File.ReadAllText(FindSourceFile("Njulf.Rendering", "Resources", "AccelerationStructureManager.cs"));

        Assert.That(source, Does.Not.Contain(".WaitIdle("));
        Assert.That(source, Does.Contain("RetireAccelerationStructureResource"));
        Assert.That(source, Does.Contain("RetireBufferResource"));
        Assert.That(source, Does.Contain("RetiredAccelerationStructureBytes"));
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            directory = parent?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException("Could not locate repository source file.", Path.Combine(relativeParts));
    }
}
