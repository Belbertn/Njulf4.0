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
    public void DirectionalReadiness_RejectsPartialResidentAndProxyCoverage()
    {
        var requirement = new RaySceneRequirement(
            RaySceneConsumer.DirectionalFull,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            80f,
            RequiresCurrentPose: true);

        string complete = AccelerationStructureManager
            .BuildRaySceneCoverageFailureDetail(
                requirement, 0, 0, 0, 0, 0);
        string incomplete = AccelerationStructureManager
            .BuildRaySceneCoverageFailureDetail(
                requirement,
                staticCulledCount: 2,
                blasBudgetRejectedCount: 1,
                dynamicProxyFallbackCount: 3,
                dynamicExcludedCount: 4,
                dynamicBudgetDeferredCount: 5);

        Assert.Multiple(() =>
        {
            Assert.That(complete, Is.Empty);
            Assert.That(incomplete, Does.Contain("not resident"));
            Assert.That(incomplete, Does.Contain("budget-rejected"));
            Assert.That(incomplete, Does.Contain("conservative proxies"));
            Assert.That(incomplete, Does.Contain("foliage"));
            Assert.That(incomplete, Does.Contain("budget-deferred"));
        });
    }

    [Test]
    public void PreparedSceneReuse_IsSuppressedWhileOpacityMicromapWorkCanAdvance()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.ShouldReusePreparedRayScene(
                    hasDynamicBuilds: false,
                    meshBuffersChanged: false,
                    pendingOpacityMicromapWork: false,
                    preparationIdentityReusable: true),
                Is.True);
            Assert.That(
                AccelerationStructureManager.ShouldReusePreparedRayScene(
                    hasDynamicBuilds: false,
                    meshBuffersChanged: false,
                    pendingOpacityMicromapWork: true,
                    preparationIdentityReusable: true),
                Is.False,
                "A stable TLAS must not starve a multi-frame OMM transaction.");
        });
    }

    [TestCase(0UL, 16UL)]
    [TestCase(1UL, 16UL)]
    [TestCase(15UL, 16UL)]
    [TestCase(16UL, 16UL)]
    [TestCase(17UL, 17UL)]
    public void OpacityMicromapAllocationSize_MatchesTrackedDeviceAllocation(
        ulong requestedBytes,
        ulong expectedBytes)
    {
        Assert.That(
            AccelerationStructureManager.GetOpacityMicromapAllocationSize(
                requestedBytes),
            Is.EqualTo(expectedBytes));
    }

    [Test]
    public void OpacityMicromapRetryDelay_IsBoundedExponentialBackoff()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager
                    .CalculateOpacityMicromapRetryDelay(0U),
                Is.Zero);
            Assert.That(
                AccelerationStructureManager
                    .CalculateOpacityMicromapRetryDelay(1U),
                Is.EqualTo(60UL));
            Assert.That(
                AccelerationStructureManager
                    .CalculateOpacityMicromapRetryDelay(4U),
                Is.EqualTo(480UL));
            Assert.That(
                AccelerationStructureManager
                    .CalculateOpacityMicromapRetryDelay(uint.MaxValue),
                Is.EqualTo(4_096UL));
        });
    }

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
    public void CreateInstance_FlipsTriangleFacingForNegativeDeterminantAndPreservesSidedness()
    {
        Matrix4x4 mirrored =
            Matrix4x4.CreateScale(new Vector3(-1f, 1f, 1f));
        const GeometryInstanceFlagsKHR authoredFlags =
            GeometryInstanceFlagsKHR.ForceOpaqueBitKhr |
            GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr;

        AccelerationStructureInstanceKHR instance =
            AccelerationStructureManager.CreateInstance(
                mirrored,
                blasAddress: 1,
                instanceCustomIndex: 0,
                AccelerationStructureManager.StaticOpaqueInstanceMask,
                authoredFlags);

        Assert.That(
            instance.Flags,
            Is.EqualTo(
                authoredFlags |
                GeometryInstanceFlagsKHR.TriangleFlipFacingBitKhr));
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
            Assert.That(metadata.AbiVersion, Is.EqualTo(DdgiRayQueryInstanceAbi.Version2));
            Assert.That(metadata.VertexFormat, Is.EqualTo((uint)DdgiRayVertexFormat.SplitStatic));
            Assert.That(metadata.VertexStride, Is.EqualTo(16u));
            Assert.That(metadata.IndexType, Is.EqualTo(DdgiRayQueryInstanceAbi.Uint32IndexType));
            Assert.That(metadata.RepresentationGeneration, Is.EqualTo(1u));
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
        DdgiAccelerationStructureGeometryPolicy opaqueThin = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Opaque,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Static,
            doubleSided: true,
            transmissionPolicy: GiTransmissionPolicy.ThinSurface);
        DdgiAccelerationStructureGeometryPolicy blendThin = AccelerationStructureManager.ResolveGeometryPolicy(
            isSkinned: false,
            MaterialRenderMode.Blend,
            isGeometryDecal: false,
            AccelerationStructureGeometryDomain.Dynamic,
            doubleSided: true,
            transmissionPolicy: GiTransmissionPolicy.ThinSurface);

        Assert.Multiple(() =>
        {
            Assert.That(opaque.Include, Is.True);
            Assert.That(opaque.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.OpaqueTriangles));
            Assert.That(masked.Include, Is.True);
            Assert.That(masked.VisibilityPolicy, Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.AlphaMaskTested));
            Assert.That(masked.InstanceFlags, Is.EqualTo(default(GeometryInstanceFlagsKHR)));
            Assert.That(transparent.Include, Is.False);
            Assert.That(transparent.VisibilityPolicy,
                Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent));
            Assert.That(skinned.Include, Is.True);
            Assert.That(skinned.VisibilityPolicy,
                Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.SkinnedBindPoseProxy));
            Assert.That(skinned.InstanceFlags, Is.EqualTo(GeometryInstanceFlagsKHR.ForceOpaqueBitKhr));
            Assert.That(foliage.Include, Is.False);
            Assert.That(foliage.VisibilityPolicy,
                Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.FoliageProxyPending));
            Assert.That(foliage.Reason, Is.EqualTo(AccelerationStructureManager.FoliageDdgiExclusionReason));
            Assert.That(opaqueThin.Include, Is.True);
            Assert.That(blendThin.Include, Is.True);
            Assert.That(opaqueThin.VisibilityPolicy,
                Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.ThinSurfaceCandidateTested));
            Assert.That(blendThin.VisibilityPolicy,
                Is.EqualTo(DdgiAccelerationStructureVisibilityPolicy.ThinSurfaceCandidateTested));
            Assert.That(opaqueThin.InstanceFlags,
                Is.EqualTo(GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr));
            Assert.That(opaqueThin.InstanceFlags.HasFlag(GeometryInstanceFlagsKHR.ForceOpaqueBitKhr), Is.False);
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
        ulong rematerialedSignature =
            AccelerationStructureManager.CreateInstanceSignature(new[] { rematerialedInstance });
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
            Assert.That(disabled.AllowStaticMemoryCulling, Is.False);
            Assert.That(
                disabled.EffectiveMaximumStaticBlasBuildsPerFrame,
                Is.EqualTo(int.MaxValue));
            Assert.That(bounded.Enabled, Is.True);
            Assert.That(bounded.EffectiveMemoryBudgetBytes, Is.EqualTo(16UL));
            Assert.That(bounded.MaximumStaticInstances, Is.EqualTo(256));
            Assert.That(bounded.EvictionGraceFrames, Is.EqualTo(3));
            Assert.That(bounded.AllowStaticMemoryCulling, Is.True);
            Assert.That(
                bounded.EffectiveMaximumStaticBlasBuildsPerFrame,
                Is.EqualTo(int.MaxValue));
        });
    }

    [Test]
    public void ResidencyPolicy_ClampsProgressiveStaticBlasBudgetToForwardProgress()
    {
        var policy = new AccelerationStructureResidencyPolicy(
            Enabled: true,
            CameraPosition: Vector3.Zero,
            MemoryBudgetBytes: 1024,
            StaticResidentDistance: float.MaxValue,
            MaximumStaticInstances: int.MaxValue,
            EvictionGraceFrames: 3,
            AllowStaticMemoryCulling: false,
            MaximumStaticBlasBuildsPerFrame: 0);

        Assert.That(
            policy.EffectiveMaximumStaticBlasBuildsPerFrame,
            Is.EqualTo(1));
    }

    [Test]
    public void SharedInstanceMask_AssignsIndependentDirectionalAndReflectionConsumers()
    {
        var opaque = new AccelerationStructureManager.StaticOpaqueInstance(
            new MeshHandle(1, 1),
            new MeshInfo(),
            0u,
            Matrix4x4.Identity);
        var alphaMask = opaque with
        {
            GeometryFlags = DdgiRayGeometryFlags.AlphaMask
        };
        var thin = opaque with
        {
            GeometryFlags = DdgiRayGeometryFlags.ThinTransmission
        };
        var blend = opaque with
        {
            GeometryFlags = DdgiRayGeometryFlags.AlphaBlend
        };
        var decal = opaque with
        {
            GeometryClass = DdgiRayGeometryClass.DecalOverlay,
            GeometryFlags = DdgiRayGeometryFlags.DecalOverlay
        };
        var volume = opaque with
        {
            GeometryClass = DdgiRayGeometryClass.VolumeTransmission,
            GeometryFlags = DdgiRayGeometryFlags.VolumeTransmission
        };
        var water = opaque with
        {
            GeometryClass = DdgiRayGeometryClass.WaterSurface,
            GeometryFlags = DdgiRayGeometryFlags.WaterSurface
        };
        var blendedThin = opaque with
        {
            GeometryClass = DdgiRayGeometryClass.ThinTransmission,
            GeometryFlags = DdgiRayGeometryFlags.ThinTransmission |
                DdgiRayGeometryFlags.AlphaBlend
        };
        var mismatchedWaterFlag = opaque with
        {
            GeometryFlags = DdgiRayGeometryFlags.WaterSurface
        };
        var mismatchedWaterClass = opaque with
        {
            GeometryClass = DdgiRayGeometryClass.WaterSurface
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(opaque),
                Is.EqualTo(
                    AccelerationStructureManager.SharedRaySceneInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(alphaMask),
                Is.EqualTo(
                    AccelerationStructureManager.SharedRaySceneInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(thin),
                Is.EqualTo(
                    AccelerationStructureManager.StaticOpaqueInstanceMask |
                    AccelerationStructureManager
                        .TransparentReflectionInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(blend),
                Is.EqualTo(
                    AccelerationStructureManager.StaticOpaqueInstanceMask |
                    AccelerationStructureManager
                        .TransparentReflectionInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(decal),
                Is.EqualTo(AccelerationStructureManager.StaticOpaqueInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(volume),
                Is.EqualTo(AccelerationStructureManager.StaticOpaqueInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(water),
                Is.EqualTo(AccelerationStructureManager.StaticOpaqueInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(
                    blendedThin),
                Is.EqualTo(
                    AccelerationStructureManager.StaticOpaqueInstanceMask |
                    AccelerationStructureManager
                        .TransparentReflectionInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(
                    mismatchedWaterFlag),
                Is.EqualTo(AccelerationStructureManager.StaticOpaqueInstanceMask));
            Assert.That(
                AccelerationStructureManager.ResolveSharedInstanceMask(
                    mismatchedWaterClass),
                Is.EqualTo(
                    AccelerationStructureManager.SharedLightingInstanceMask));
        });
    }

    [Test]
    public void SharedInstanceMaskBits_AreStableDisjointAndUnioned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.StaticOpaqueInstanceMask,
                Is.EqualTo(0x01));
            Assert.That(
                AccelerationStructureManager.DirectionalShadowInstanceMask,
                Is.EqualTo(0x02));
            Assert.That(
                AccelerationStructureManager.TransparentReflectionInstanceMask,
                Is.EqualTo(0x04));
            Assert.That(
                AccelerationStructureManager.StaticOpaqueInstanceMask &
                AccelerationStructureManager.DirectionalShadowInstanceMask,
                Is.Zero);
            Assert.That(
                AccelerationStructureManager.StaticOpaqueInstanceMask &
                AccelerationStructureManager.TransparentReflectionInstanceMask,
                Is.Zero);
            Assert.That(
                AccelerationStructureManager.DirectionalShadowInstanceMask &
                AccelerationStructureManager.TransparentReflectionInstanceMask,
                Is.Zero);
            Assert.That(
                AccelerationStructureManager.SharedLightingInstanceMask,
                Is.EqualTo(
                    AccelerationStructureManager.StaticOpaqueInstanceMask |
                    AccelerationStructureManager
                        .DirectionalShadowInstanceMask));
            Assert.That(
                AccelerationStructureManager.SharedRaySceneInstanceMask,
                Is.EqualTo(
                    AccelerationStructureManager.SharedLightingInstanceMask |
                    AccelerationStructureManager
                        .TransparentReflectionInstanceMask));
        });
    }

    [Test]
    public void RaySceneContentSignature_IgnoresFrameSlotButTracksPoseAndMaterial()
    {
        var meshInfo = new MeshInfo
        {
            VertexOffset = 4u,
            IndexOffset = 8u,
            VertexCount = 64u,
            IndexCount = 96u
        };
        var instance = new AccelerationStructureManager.StaticOpaqueInstance(
                new MeshHandle(7, 2),
                meshInfo,
                3u,
                Matrix4x4.Identity) with
            {
                ObjectIdentity =
                new Guid("a0000000-0000-0000-0000-00000000000a"),
                FrameSlot = 0,
                VertexBufferIndex = 17u,
                RepresentationGeneration = 9u,
                MaterialRevision = 11u,
                UsesDynamicBlas = true
            };
        var rotatedFrameResources = instance with
        {
            FrameSlot = 1,
            VertexBufferIndex = 23u,
            IndexBufferIndex = 29u
        };
        var advancedPose = rotatedFrameResources with
        {
            RepresentationGeneration = 10u
        };
        var editedMaterial = instance with { MaterialRevision = 12u };
        DdgiDynamicRayScenePolicy policy =
            DdgiDynamicRayScenePolicy.LegacyBaseline with
            {
                SkinnedGeometryMode = DdgiSkinnedGeometryMode.CurrentPose
            };

        ulong baseline = AccelerationStructureManager
            .CreateRaySceneContentSignature(
                enabled: true,
                sceneContentRevision: 5,
                policy,
                new[] { instance });
        ulong frameRotated = AccelerationStructureManager
            .CreateRaySceneContentSignature(
                enabled: true,
                sceneContentRevision: 5,
                policy,
                new[] { rotatedFrameResources });
        ulong baselineBuildSignature = AccelerationStructureManager
            .CreateInstanceSignature(new[] { instance });
        ulong rotatedBuildSignature = AccelerationStructureManager
            .CreateInstanceSignature(new[] { rotatedFrameResources });
        ulong poseChanged = AccelerationStructureManager
            .CreateRaySceneContentSignature(
                enabled: true,
                sceneContentRevision: 5,
                policy,
                new[] { advancedPose });
        ulong materialChanged = AccelerationStructureManager
            .CreateRaySceneContentSignature(
                enabled: true,
                sceneContentRevision: 5,
                policy,
                new[] { editedMaterial });

        Assert.Multiple(() =>
        {
            Assert.That(rotatedBuildSignature,
                Is.Not.EqualTo(baselineBuildSignature),
                "The TLAS must still rebuild when rotating physical frame resources.");
            Assert.That(frameRotated, Is.EqualTo(baseline));
            Assert.That(poseChanged, Is.Not.EqualTo(baseline));
            Assert.That(materialChanged, Is.Not.EqualTo(baseline));
        });
    }

    [Test]
    public void SkinningOutput_IsExactAccelerationStructureVertexContract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SkinningManager.OutputPositionOffset, Is.Zero);
            Assert.That(
                SkinningManager.OutputVertexStride,
                Is.EqualTo((uint)System.Runtime.InteropServices.Marshal.SizeOf<GPUVertex>()));
            Assert.That(SkinningManager.OutputVertexStride, Is.EqualTo(80u));
            Assert.That(
                SkinningManager.OutputVertexBufferUsage &
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                Is.EqualTo(BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr));
            Assert.That(
                SkinningManager.OutputVertexBufferUsage &
                BufferUsageFlags.ShaderDeviceAddressBit,
                Is.EqualTo(BufferUsageFlags.ShaderDeviceAddressBit));
        });
    }

    [Test]
    public void StableInstanceIdentity_IsObjectAndOrdinalStableAndNeverZero()
    {
        Guid identity = Guid.Parse("e96354b7-e9fe-4d42-b50c-09df08a7051f");
        uint first = AccelerationStructureManager.StableInstanceIdentity(identity);
        uint repeated = AccelerationStructureManager.StableInstanceIdentity(identity);
        uint nextOrdinal = AccelerationStructureManager.StableInstanceIdentity(identity, 1u);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Zero);
            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(nextOrdinal, Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void DynamicRayScenePolicy_SeparatesStorageScratchAndWorkCaps()
    {
        var policy = new DdgiDynamicRayScenePolicy(
            DdgiSkinnedGeometryMode.CurrentPose,
            DdgiTransparentGeometryMode.StochasticBlend,
            DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
            GeometryDecalsEnabled: true,
            AlphaMaskedTransportEnabled: true,
            DynamicStorageBudgetBytes: 64UL * 1024UL * 1024UL,
            DynamicScratchBudgetBytes: 8UL * 1024UL * 1024UL,
            MaximumBuildsPerFrame: 7,
            MaximumPrimitivesPerFrame: 90_000,
            DecalCandidateLimit: 8);

        Assert.Multiple(() =>
        {
            Assert.That(policy.EffectiveDynamicStorageBudgetBytes, Is.EqualTo(64UL * 1024UL * 1024UL));
            Assert.That(policy.EffectiveDynamicScratchBudgetBytes, Is.EqualTo(8UL * 1024UL * 1024UL));
            Assert.That(policy.EffectiveMaximumBuildsPerFrame, Is.EqualTo(7));
            Assert.That(policy.EffectiveMaximumPrimitivesPerFrame, Is.EqualTo(90_000));
            Assert.That(policy.EffectiveDecalCandidateLimit, Is.EqualTo(8));
            Assert.That(
                (policy with
                {
                    SkinnedGeometryMode = DdgiSkinnedGeometryMode.Excluded,
                    FoliageGeometryMode = DdgiFoliageGeometryMode.Excluded
                })
                .EffectiveDynamicStorageBudgetBytes,
                Is.Zero);
        });
    }

    [TestCase(true, 0, true, true, false)]
    [TestCase(true, 0, true, false, true)]
    [TestCase(true, 0, false, true, true)]
    [TestCase(true, 1, true, false, false)]
    [TestCase(false, 0, true, false, false)]
    public void CoarseRayProxyPolicy_PreservesExactOpacityMicromapTopology(
        bool proxyAvailable,
        int domainValue,
        bool ommEnabled,
        bool matchingRegistration,
        bool expected)
    {
        Assert.That(
            AccelerationStructureManager.ShouldUseCoarseRayProxyForInstance(
                proxyAvailable,
                (AccelerationStructureGeometryDomain)domainValue,
                ommEnabled,
                matchingRegistration),
            Is.EqualTo(expected));
    }

    [Test]
    public void DynamicRayScenePolicy_CapsMixedFoliageWithoutIdlingSkinnedCapacity()
    {
        DdgiDynamicRayScenePolicy policy =
            DdgiDynamicRayScenePolicy.LegacyBaseline with
            {
                SkinnedGeometryMode = DdgiSkinnedGeometryMode.CurrentPose,
                FoliageGeometryMode =
                    DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                DynamicStorageBudgetBytes = 64UL * 1024UL * 1024UL,
                MaximumBuildsPerFrame = 8,
                MaximumPrimitivesPerFrame = 80_000
            };

        DdgiDynamicBlasContentBudget skinned = policy.ResolveContentBudget(
            DdgiDynamicBlasContentClass.CurrentPoseSkinned,
            mixedContent: true);
        DdgiDynamicBlasContentBudget foliage = policy.ResolveContentBudget(
            DdgiDynamicBlasContentClass.ProceduralFoliage,
            mixedContent: true);
        DdgiDynamicBlasContentBudget foliageOnly = policy.ResolveContentBudget(
            DdgiDynamicBlasContentClass.ProceduralFoliage,
            mixedContent: false);

        Assert.Multiple(() =>
        {
            Assert.That(skinned.StorageBudgetBytes,
                Is.EqualTo(64UL * 1024UL * 1024UL));
            Assert.That(skinned.MaximumBuildsPerFrame, Is.EqualTo(8));
            Assert.That(skinned.MaximumPrimitivesPerFrame, Is.EqualTo(80_000));
            Assert.That(foliage.StorageBudgetBytes,
                Is.EqualTo(16UL * 1024UL * 1024UL));
            Assert.That(foliage.MaximumBuildsPerFrame, Is.EqualTo(2));
            Assert.That(foliage.MaximumPrimitivesPerFrame, Is.EqualTo(20_000));
            Assert.That(foliageOnly.StorageBudgetBytes,
                Is.EqualTo(skinned.StorageBudgetBytes));
            Assert.That(foliageOnly.MaximumBuildsPerFrame,
                Is.EqualTo(skinned.MaximumBuildsPerFrame));
            Assert.That(foliageOnly.MaximumPrimitivesPerFrame,
                Is.EqualTo(skinned.MaximumPrimitivesPerFrame));
        });
    }

    [Test]
    public void ResidencyPolicy_SkipsNearestSelectionWhenStaticAdmissionIsUnbounded()
    {
        var unbounded = new AccelerationStructureResidencyPolicy(
            Enabled: true,
            CameraPosition: Vector3.Zero,
            MemoryBudgetBytes: 1024,
            StaticResidentDistance: float.MaxValue,
            MaximumStaticInstances: int.MaxValue,
            EvictionGraceFrames: 3,
            AllowStaticMemoryCulling: false);
        AccelerationStructureResidencyPolicy boundedDistance = unbounded with
        {
            StaticResidentDistance = 128f
        };
        AccelerationStructureResidencyPolicy boundedCount = unbounded with
        {
            MaximumStaticInstances = 256
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.RequiresStaticResidencySelection(unbounded),
                Is.False);
            Assert.That(
                AccelerationStructureManager.RequiresStaticResidencySelection(boundedDistance),
                Is.True);
            Assert.That(
                AccelerationStructureManager.RequiresStaticResidencySelection(boundedCount),
                Is.True);
            Assert.That(
                AccelerationStructureManager.RequiresStaticResidencySelection(
                    AccelerationStructureResidencyPolicy.Disabled),
                Is.False);
        });
    }

    [Test]
    public void TransientResidencyBudget_IsExplicitTierBoundedAndNeverUnbounded()
    {
        const ulong mib = 1024UL * 1024UL;

        Assert.Multiple(() =>
        {
            Assert.That(AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(0), Is.Zero);
            Assert.That(
                AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(64UL * mib),
                Is.EqualTo(96UL * mib));
            Assert.That(
                AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(256UL * mib),
                Is.EqualTo(384UL * mib));
            Assert.That(
                AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(512UL * mib),
                Is.EqualTo(640UL * mib));
            Assert.That(
                AccelerationStructureManager.CalculateScratchMemoryBudgetBytes(256UL * mib),
                Is.EqualTo(128UL * mib));
            Assert.That(
                AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(ulong.MaxValue),
                Is.EqualTo(ulong.MaxValue));
        });
    }

    [TestCase(1_024UL, 0UL, false)]
    [TestCase(1_024UL, 1_024UL, false)]
    [TestCase(1_024UL, 1_025UL, false)]
    [TestCase(1_024UL, 512UL, true)]
    [TestCase(16UL, 1UL, false)]
    public void BlasCompaction_RequiresAValidStrictResidencyReduction(
        ulong sourceBytes,
        ulong queriedCompactedBytes,
        bool expected)
    {
        Assert.That(
            AccelerationStructureManager.ShouldCompactBottomLevelAccelerationStructure(
                sourceBytes,
                queriedCompactedBytes),
            Is.EqualTo(expected));
    }

    [Test]
    public void BlasCompactionFrameBudget_AllowsOneOversizedItemThenBoundsOverlap()
    {
        const ulong mib = 1024UL * 1024UL;

        Assert.Multiple(() =>
        {
            Assert.That(
                AccelerationStructureManager.FitsBlasCompactionFrameBudget(
                    0,
                    48UL * mib,
                    32UL * mib),
                Is.True);
            Assert.That(
                AccelerationStructureManager.FitsBlasCompactionFrameBudget(
                    24UL * mib,
                    8UL * mib,
                    32UL * mib),
                Is.True);
            Assert.That(
                AccelerationStructureManager.FitsBlasCompactionFrameBudget(
                    24UL * mib,
                    9UL * mib,
                    32UL * mib),
                Is.False);
        });
    }

    [Test]
    public void BlasAddressReplacement_InvalidatesEveryTlasFrameSlot()
    {
        ulong[] signatures = [11UL, 22UL, 33UL];
        bool[] hasSignatures = [true, true, true];
        int[] instanceCounts = [4, 5, 6];

        AccelerationStructureManager.InvalidateTopLevelFrameSignatures(
            signatures,
            hasSignatures,
            instanceCounts);

        Assert.Multiple(() =>
        {
            Assert.That(signatures, Is.All.Zero);
            Assert.That(hasSignatures, Is.All.False);
            Assert.That(instanceCounts, Is.All.Zero);
        });
    }

    [Test]
    public void BlasCompaction_UsesFenceCompletedNonWaitingQueriesAndDeferredRetirement()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "AccelerationStructureManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("AllowCompactionBitKhr"));
            Assert.That(source, Does.Contain("AccelerationStructureCompactedSizeKhr"));
            Assert.That(source, Does.Contain("CopyAccelerationStructureModeKHR.CompactKhr"));
            Assert.That(source, Does.Contain("QueryResultFlags.Result64Bit"));
            Assert.That(source, Does.Not.Contain("QueryResultFlags.WaitBit"));
            Assert.That(source, Does.Contain("RetireAccelerationStructureResource("));
        });
    }

    [Test]
    public void AccelerationStructureManager_DoesNotWaitIdleForSteadyGrowthOrStreamingRetirement()
    {
        string source =
            File.ReadAllText(FindSourceFile("Njulf.Rendering", "Resources", "AccelerationStructureManager.cs"));

        Assert.That(source, Does.Not.Contain(".WaitIdle("));
        Assert.That(source, Does.Contain("RetireAccelerationStructureResource"));
        Assert.That(source, Does.Contain("RetireBufferResource"));
        Assert.That(source, Does.Contain("RetiredAccelerationStructureBytes"));
        Assert.That(source, Does.Contain("RetiredResourceBytes"));
    }

    [Test]
    public void TlasInstanceUpload_IsVisibleAsBuildInputShaderRead()
    {
        string source = File.ReadAllText(FindSourceFile(
                "Njulf.Rendering",
                "Resources",
                "AccelerationStructureManager.cs"))
            .ReplaceLineEndings("\n");

        Assert.That(source, Does.Contain(
            "PipelineStageFlags2.AccelerationStructureBuildBitKhr,\n" +
            "                    AccessFlags2.ShaderReadBit |\n" +
            "                    AccessFlags2.AccelerationStructureReadBitKhr"));
    }

    [Test]
    public void AccelerationStructureManager_NeverPublishesAHolePunchedResidentSet()
    {
        string source =
            File.ReadAllText(FindSourceFile("Njulf.Rendering", "Resources", "AccelerationStructureManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ApplyMemoryResidencyPolicy(_instanceScratch)"));
            Assert.That(source, Does.Contain("AllowStaticMemoryCulling"));
            Assert.That(source, Does.Contain("Stop at the first non-fitting nearest-first candidate"));
            Assert.That(source, Does.Contain("no partial TLAS was published"));
            Assert.That(source, Does.Not.Contain("RemoveInstancesWithUnavailableBottomLevelAccelerationStructures"));
        });
    }

    [Test]
    public void ResidencySizing_CachesBlasBuildSizeQueriesForStableMeshBuffers()
    {
        string source =
            File.ReadAllText(FindSourceFile("Njulf.Rendering", "Resources", "AccelerationStructureManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("_blasSizeEstimateCache.TryGetValue"));
            Assert.That(source, Does.Contain("_blasSizeEstimateCache[meshHandle] = estimatedSize"));
            Assert.That(source, Does.Contain("_blasSizeEstimateCache.Clear()"));
        });
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
