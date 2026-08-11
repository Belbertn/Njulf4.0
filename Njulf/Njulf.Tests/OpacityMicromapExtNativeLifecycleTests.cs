using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpacityMicromapExtNativeLifecycleTests
{
    [Test]
    public void NativeInputLayout_RequiresExactVulkanTriangleAndIndexAbi()
    {
        OpacityMicromapCookedPayload payload = Payload();
        OpacityMicromapExtNativeInputLayout layout =
            OpacityMicromapExtNativeInputLayout.PackedUint32;

        bool valid = layout.TryValidate(payload, out string validDetail);
        var badStride = layout with { PerPrimitiveIndexStride = sizeof(ushort) };
        bool invalid = badStride.TryValidate(payload, out string invalidDetail);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True, validDetail);
            Assert.That(validDetail, Is.EqualTo("native-omm-layout-valid"));
            Assert.That(invalid, Is.False);
            Assert.That(invalidDetail,
                Is.EqualTo("native-omm-layout-index-stride-not-tightly-packed"));
        });
    }

    [Test]
    public void NativeInputLayout_RejectsDescriptorOrPrimitiveIndexReinterpretation()
    {
        OpacityMicromapCookedPayload good = Payload();
        OpacityMicromapCookedPayload badDescriptor = OpacityMicromapCookedPayload.Create(
            cookAbi: good.CookAbi,
            sourceContentHash: good.SourceContentHash,
            sdkProvenanceHash: good.SdkProvenanceHash,
            maximumSubdivisionLevel: good.MaximumSubdivisionLevel,
            primitiveCount: good.PrimitiveCount,
            descriptorCount: good.DescriptorCount,
            materialContracts: good.MaterialContracts.ToArray(),
            usageHistogram: good.UsageHistogram.ToArray(),
            ommData: good.OmmData.Span,
            indexData: good.IndexData.Span,
            descriptorData: new byte[good.DescriptorData.Length - 1]);

        bool valid = OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
            good,
            out _);
        bool invalid = OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
            badDescriptor,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(invalid, Is.False);
            Assert.That(detail,
                Is.EqualTo("native-omm-layout-descriptor-data-length-mismatch"));
        });
    }

    [Test]
    public unsafe void StaticBlasAttachment_PinsOnlyDuringNativeRecordingCallback()
    {
        OpacityMicromapCookedPayload payload = Payload();
        bool created = OpacityMicromapExtStaticBlasAttachment.TryCreate(
            VariantKey(),
            new MicromapEXT { Handle = 73UL },
            payload,
            OpacityMicromapExtNativeInputLayout.PackedUint32,
            new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(11, 1U),
                0x1_0000UL,
                (ulong)payload.IndexData.Length),
            out OpacityMicromapExtStaticBlasAttachment? attachment,
            out string createDetail);

        Assert.That(created, Is.True, createDetail);
        Assert.That(attachment, Is.Not.Null);

        bool callbackInvoked = false;
        attachment!.RecordWithNativeAttachment(native =>
        {
            callbackInvoked = true;
            Assert.Multiple(() =>
            {
                Assert.That(native->SType,
                    Is.EqualTo(StructureType.AccelerationStructureTrianglesOpacityMicromapExt));
                Assert.That(native->PUsageCounts != null, Is.True);
                Assert.That(native->PpUsageCounts == null, Is.True);
                Assert.That(native->UsageCountsCount, Is.EqualTo(1U));
                Assert.That(native->PUsageCounts[0].Count, Is.EqualTo(2U));
                Assert.That(native->Micromap.Handle, Is.EqualTo(73UL));
            });
        });

        Assert.That(callbackInvoked, Is.True);
    }

    [Test]
    public void NativeInputLayout_RejectsDescriptorHistogramThatDoesNotMatchDescriptorSubdivisions()
    {
        OpacityMicromapCookedPayload good = Payload();
        byte[] descriptors = good.DescriptorData.ToArray();
        WriteDescriptor(descriptors, 8, dataOffset: 4U, subdivision: 2);
        OpacityMicromapCookedPayload mismatched = OpacityMicromapCookedPayload.Create(
            cookAbi: good.CookAbi,
            sourceContentHash: good.SourceContentHash,
            sdkProvenanceHash: good.SdkProvenanceHash,
            maximumSubdivisionLevel: good.MaximumSubdivisionLevel,
            primitiveCount: good.PrimitiveCount,
            descriptorCount: good.DescriptorCount,
            materialContracts: good.MaterialContracts.ToArray(),
            usageHistogram: good.UsageHistogram.ToArray(),
            ommData: good.OmmData.Span,
            indexData: good.IndexData.Span,
            descriptorData: descriptors);

        bool valid = OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
            mismatched,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.False);
            Assert.That(detail,
                Is.EqualTo("native-omm-layout-usage-histogram-does-not-match-triangle-descriptors"));
        });
    }

    [Test]
    public void NativeInputLayout_RejectsDescriptorWhoseFourStatePayloadOverrunsOmmData()
    {
        OpacityMicromapCookedPayload good = Payload();
        OpacityMicromapCookedPayload truncatedData = OpacityMicromapCookedPayload.Create(
            cookAbi: good.CookAbi,
            sourceContentHash: good.SourceContentHash,
            sdkProvenanceHash: good.SdkProvenanceHash,
            maximumSubdivisionLevel: good.MaximumSubdivisionLevel,
            primitiveCount: good.PrimitiveCount,
            descriptorCount: good.DescriptorCount,
            materialContracts: good.MaterialContracts.ToArray(),
            usageHistogram: good.UsageHistogram.ToArray(),
            ommData: new byte[] { 1, 2, 3, 4 },
            indexData: good.IndexData.Span,
            descriptorData: good.DescriptorData.Span);

        bool valid = OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
            truncatedData,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.False);
            Assert.That(detail, Is.EqualTo("native-omm-layout-triangle-descriptor-invalid"));
        });
    }

    [Test]
    public void StaticBlasAttachment_DerivesUsageFromGeometryIndirectionInsteadOfBuildHistogram()
    {
        OpacityMicromapCookedPayload payload = ReusedDescriptorPayload();
        bool created = OpacityMicromapExtStaticBlasAttachment.TryCreate(
            VariantKey(),
            new MicromapEXT { Handle = 73UL },
            payload,
            OpacityMicromapExtNativeInputLayout.PackedUint32,
            new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(12, 1U),
                0x1_0000UL,
                (ulong)payload.IndexData.Length),
            out OpacityMicromapExtStaticBlasAttachment? attachment,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True, detail);
            Assert.That(attachment, Is.Not.Null);
            Assert.That(attachment!.UsageCounts, Has.Count.EqualTo(1));
            Assert.That(attachment.UsageCounts[0].SubdivisionLevel, Is.EqualTo(1U));
            Assert.That(attachment.UsageCounts[0].Count, Is.EqualTo(2U),
                "The two geometry triangles reuse descriptor zero; descriptor one is not referenced.");
        });
    }

    [Test]
    public async Task AsLifecycleHost_StaysFailClosedUntilStaticVariantPublicationIsIntegrated()
    {
        int fallbackRequests = 0;
        using var host = new AccelerationStructureOpacityMicromapNativeLifecycleHost(
            () => VulkanExtOpacityMicromapCapabilityInspector.Evaluate(Snapshot()),
            _ =>
            {
                fallbackRequests++;
                return new OpacityMicromapExtOrdinaryFallback(
                    new MeshHandle(4, 1U),
                    PrimitiveCount: 2U,
                    BlasHandle: 99UL,
                    ResidentBytes: 1024UL,
                    IsStaticTriangleGeometry: true,
                    CandidateConfirmationAvailable: true);
            });
        OpacityMicromapBackendBuildRequest request = Request();
        var candidate = new OpacityMicromapExtStaticBlasCandidate(
            ContentKey: request.ContentKey,
            Mesh: new MeshHandle(4, 1U),
            MeshGeometryKey: Key(44),
            RayGeometryPolicy:
                StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            AccelerationStructureBuildAbi: request.AccelerationStructureBuildAbi,
            NativeInputLayout: OpacityMicromapExtNativeInputLayout.PackedUint32);

        bool registered = host.TryRegister(candidate, out string registerDetail);
        bool planned = host.TryCreateBuildPlan(
            request,
            OpacityMicromapExtBuildPolicy.Default,
            out _,
            out string planDetail);
        OpacityMicromapExtBuildReceipt receipt =
            await host.BuildAndWaitForPublicationAsync(
                request,
                default,
                OpacityMicromapExtBuildPolicy.Default,
                CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.True, registerDetail);
            Assert.That(host.CapabilityReport.Failure,
                Is.EqualTo(OpacityMicromapExtCapabilityFailure.BlasAttachmentNotIntegrated));
            Assert.That(host.CapabilityReport.SupportsPublication, Is.False);
            Assert.That(planned, Is.False);
            Assert.That(planDetail,
                Is.EqualTo("matching-static-BLAS-EXT-attachment-submission-and-retirement-not-integrated"));
            Assert.That(fallbackRequests, Is.EqualTo(0),
                "No AS lookup or allocation may occur after the capability gate rejects the path.");
            Assert.That(receipt.Succeeded, Is.False);
            Assert.That(receipt.Detail,
                Is.EqualTo("matching-static-BLAS-EXT-attachment-submission-completion-and-retirement-contract-not-integrated"));
        });
    }

    [Test]
    public void AsLifecycleHost_AtomicallyMovesRepresentativeForSharedVariant()
    {
        using var host = new AccelerationStructureOpacityMicromapNativeLifecycleHost(
            () => VulkanExtOpacityMicromapCapabilityInspector.Evaluate(Snapshot()),
            _ => default);
        OpacityMicromapBackendBuildRequest request = Request();
        var first = new OpacityMicromapExtStaticBlasCandidate(
            request.ContentKey,
            new MeshHandle(9, 1U),
            Key(45),
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            request.AccelerationStructureBuildAbi,
            OpacityMicromapExtNativeInputLayout.PackedUint32);
        OpacityMicromapExtStaticBlasCandidate shared = first with
        {
            Mesh = new MeshHandle(3, 2U)
        };
        OpacityMicromapExtStaticBlasCandidate conflict = shared with
        {
            MeshGeometryKey = Key(46)
        };

        bool registered = host.TryRegister(first, out string firstDetail);
        bool moved = host.TryRegister(shared, out string sharedDetail);
        bool rejected = host.TryRegister(conflict, out string conflictDetail);

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.True, firstDetail);
            Assert.That(moved, Is.True, sharedDetail);
            Assert.That(sharedDetail, Is.EqualTo(
                "omm-static-blas-registration-shared-owner-updated"));
            Assert.That(rejected, Is.False);
            Assert.That(conflictDetail, Is.EqualTo(
                "omm-static-blas-registration-content-key-conflict"));
        });
    }

    [Test]
    public void NativeBuildInputs_RejectsUnalignedMicromapAddressesBeforeCommandRecording()
    {
        OpacityMicromapCookedPayload payload = Payload();
        var inputs = new OpacityMicromapExtNativeBuildInputs(
            OmmData: new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(1, 1U), 17UL, (ulong)payload.OmmData.Length),
            TriangleArray: new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(2, 1U), 0x200UL, (ulong)payload.DescriptorData.Length),
            PerPrimitiveIndex: new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(3, 1U), 0x300UL, (ulong)payload.IndexData.Length),
            Scratch: new OpacityMicromapExtDeviceBufferBinding(
                new BufferHandle(4, 1U), 0x400UL, 128UL),
            DestinationMicromap: new MicromapEXT { Handle = 9UL });
        var sizes = new OpacityMicromapExtNativeBuildSizes(
            MicromapStorageBytes: 512UL,
            BuildScratchBytes: 128UL,
            CompactionAllowed: true,
            Discardable: false);

        bool valid = inputs.TryValidateForBuild(payload, sizes, out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.False);
            Assert.That(detail,
                Is.EqualTo("native-omm-build-data-or-triangle-address-not-256-byte-aligned"));
        });
    }

    private static VulkanExtOpacityMicromapFeatureSnapshot Snapshot() => new(
        ExtensionAdvertised: true,
        ExtensionEnabled: true,
        MicromapFeatureEnabled: true,
        AccelerationStructureExtensionEnabled: true,
        BufferDeviceAddressEnabled: true,
        DeferredHostOperationsExtensionEnabled: true,
        NativeDispatchLoaded: true,
        CommandBufferBuildEnabled: true,
        CompactedSizeQueryEnabled: true,
        BlasOpacityAttachmentEnabled: true,
        MaximumFourStateSubdivisionLevel: 4U);

    private static OpacityMicromapBackendBuildRequest Request() => new(
        ContentKey: Key(1),
        Payload: Payload(),
        AccelerationStructureBuildAbi: 5U,
        PublicationGeneration: 1UL);

    private static StaticBlasVariantKey VariantKey() => new(
        MeshGeometryKey: Key(7),
        RayGeometryPolicy: StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
        OpacityMicromapContentKeyOrNull: Key(1),
        AccelerationStructureBuildAbi: 5U);

    private static OpacityMicromapCookedPayload Payload()
    {
        byte[] descriptors = new byte[2 * 8];
        WriteDescriptor(descriptors, 0, dataOffset: 0U, subdivision: 1);
        WriteDescriptor(descriptors, 8, dataOffset: 4U, subdivision: 1);
        byte[] indices = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(0, sizeof(uint)), 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(sizeof(uint), sizeof(uint)), 1U);
        return OpacityMicromapCookedPayload.Create(
            cookAbi: 7U,
            sourceContentHash: Key(1),
            sdkProvenanceHash: Key(2),
            maximumSubdivisionLevel: 4U,
            primitiveCount: 2U,
            descriptorCount: 2U,
            materialContracts: new[] { MaterialContract(primitiveCount: 2U) },
            usageHistogram: new[]
            {
                new OpacityMicromapUsage(OpacityMicromapFormat.FourState, 1U, 2UL)
            },
            ommData: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            indexData: indices,
            descriptorData: descriptors);
    }

    private static OpacityMicromapCookedPayload ReusedDescriptorPayload()
    {
        byte[] descriptors = new byte[2 * 8];
        WriteDescriptor(descriptors, 0, dataOffset: 0U, subdivision: 1);
        WriteDescriptor(descriptors, 8, dataOffset: 1U, subdivision: 2);
        byte[] indices = new byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(0, sizeof(uint)), 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(sizeof(uint), sizeof(uint)), 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(2 * sizeof(uint), sizeof(uint)), uint.MaxValue);
        return OpacityMicromapCookedPayload.Create(
            cookAbi: 7U,
            sourceContentHash: Key(1),
            sdkProvenanceHash: Key(2),
            maximumSubdivisionLevel: 4U,
            primitiveCount: 3U,
            descriptorCount: 2U,
            materialContracts: new[] { MaterialContract(primitiveCount: 3U) },
            usageHistogram: new[]
            {
                new OpacityMicromapUsage(OpacityMicromapFormat.FourState, 1U, 1UL),
                new OpacityMicromapUsage(OpacityMicromapFormat.FourState, 2U, 1UL)
            },
            ommData: new byte[] { 1, 2, 3, 4, 5 },
            indexData: indices,
            descriptorData: descriptors);
    }

    private static void WriteDescriptor(
        Span<byte> destination,
        int offset,
        uint dataOffset,
        ushort subdivision)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(offset, sizeof(uint)),
            dataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset + sizeof(uint), sizeof(ushort)), subdivision);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination.Slice(offset + sizeof(uint) + sizeof(ushort), sizeof(ushort)),
            (ushort)OpacityMicromapFormatEXT.Format4StateExt);
    }

    private static OpacityMicromapMaterialContract MaterialContract(uint primitiveCount) => new(
        MaterialSlot: 3U,
        FirstPrimitive: 0U,
        PrimitiveCount: primitiveCount,
        TexCoordSet: 0,
        UvTransform: OpacityMicromapUvTransformBits.Identity,
        TextureContentHash: Key(3),
        TextureFormatAndMipHash: Key(4),
        Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
        MaterialAlphaBits: Bits(1.0f),
        UniformVertexAlphaBits: Bits(1.0f),
        AlphaCutoffBits: Bits(0.5f),
        FixedLodBits: Bits(0.0f),
        AlphaContractRevision: 8U,
        ShaderAbiRevision: 9U);

    private static uint Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData(new[] { value }));

}
