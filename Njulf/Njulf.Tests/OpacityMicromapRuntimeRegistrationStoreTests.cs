using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpacityMicromapRuntimeRegistrationStoreTests
{
    [Test]
    public void Registration_FollowsEveryMeshLifetimeReference()
    {
        var store = new OpacityMicromapRuntimeRegistrationStore();
        OpacityMicromapRuntimeMeshRegistration registration = Registration();

        bool added = store.TryRegisterInitialReference(
            registration,
            out string detail);
        store.RetainMeshReference(registration.Mesh);
        OpacityMicromapRuntimeRegistrationSnapshot retained =
            store.GetSnapshot();
        store.ReleaseMeshReference(registration.Mesh);
        store.ReleaseMeshReference(registration.Mesh);
        OpacityMicromapRuntimeRegistrationSnapshot released =
            store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.True, detail);
            Assert.That(detail, Is.EqualTo("omm-runtime-registration-added"));
            Assert.That(retained.RegistrationCount, Is.EqualTo(1));
            Assert.That(retained.TotalReferenceCount, Is.EqualTo(2));
            Assert.That(retained.RegisteredPayloadBytes, Is.GreaterThan(0UL));
            Assert.That(released.RegistrationCount, Is.Zero);
            Assert.That(released.TotalReferenceCount, Is.Zero);
            Assert.That(store.TryGet(registration.Mesh, out _), Is.False);
        });
    }

    [Test]
    public void EquivalentInitialRegistration_AcquiresAnIndependentReference()
    {
        var store = new OpacityMicromapRuntimeRegistrationStore();
        OpacityMicromapRuntimeMeshRegistration first = Registration();
        OpacityMicromapRuntimeMeshRegistration equivalent = first with
        {
            Payload = Payload()
        };

        Assert.That(store.TryRegisterInitialReference(first, out _), Is.True);
        bool accepted = store.TryRegisterInitialReference(
            equivalent,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, detail);
            Assert.That(detail,
                Is.EqualTo("omm-runtime-registration-retained-existing"));
            Assert.That(store.GetSnapshot().TotalReferenceCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void ConflictingMeshIdentity_IsRejectedWithoutReplacingLivePayload()
    {
        var store = new OpacityMicromapRuntimeRegistrationStore();
        OpacityMicromapRuntimeMeshRegistration first = Registration();
        OpacityMicromapRuntimeMeshRegistration conflict = first with
        {
            MaterialOpacityRevision = first.MaterialOpacityRevision with
            {
                AlphaCoverage =
                    first.MaterialOpacityRevision.AlphaCoverage + 1U
            }
        };

        Assert.That(store.TryRegisterInitialReference(first, out _), Is.True);
        bool accepted = store.TryRegisterInitialReference(
            conflict,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(detail,
                Is.EqualTo("omm-runtime-registration-mesh-conflict"));
            Assert.That(store.TryGet(first.Mesh, out var retained), Is.True);
            Assert.That(retained.MaterialOpacityRevision,
                Is.EqualTo(first.MaterialOpacityRevision));
            Assert.That(store.GetSnapshot().RejectedRegistrationCount,
                Is.EqualTo(1UL));
        });
    }

    [Test]
    public void GeometryKey_IsDeterministicAndIncludesExactIndexOrdering()
    {
        GPUVertexPositionStream[] positions = Positions();
        uint[] indices = [0U, 1U, 2U, 2U, 1U, 3U];
        uint[] reordered = [0U, 2U, 1U, 2U, 1U, 3U];

        OpacityMicromapContentKey first =
            OpacityMicromapRuntimeRegistrationStore.ComputeMeshGeometryKey(
                positions,
                indices);
        OpacityMicromapContentKey second =
            OpacityMicromapRuntimeRegistrationStore.ComputeMeshGeometryKey(
                (GPUVertexPositionStream[])positions.Clone(),
                (uint[])indices.Clone());
        OpacityMicromapContentKey changed =
            OpacityMicromapRuntimeRegistrationStore.ComputeMeshGeometryKey(
                positions,
                reordered);

        Assert.Multiple(() =>
        {
            Assert.That(first.IsZero, Is.False);
            Assert.That(second, Is.EqualTo(first));
            Assert.That(changed, Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void VariantKey_SharesImmutableGeometryAndPayloadAcrossMeshHandles()
    {
        OpacityMicromapRuntimeMeshRegistration first = Registration();
        OpacityMicromapRuntimeMeshRegistration shared = first with
        {
            Mesh = new MeshHandle(51, 9U),
            Material = new MaterialHandle(71, 11U),
            MaterialOpacityRevision =
                new OpacityMicromapMaterialRevision(19U, 20U)
        };
        OpacityMicromapRuntimeMeshRegistration differentPolicy = shared with
        {
            RayGeometryPolicy =
                StaticBlasRayGeometryPolicy
                    .TwoSidedCandidateConfirmationRequired
        };

        Assert.Multiple(() =>
        {
            Assert.That(shared.CreateVariantKey(),
                Is.EqualTo(first.CreateVariantKey()),
                "Instance-local handles must not fragment immutable BLAS variants.");
            Assert.That(differentPolicy.CreateVariantKey(),
                Is.Not.EqualTo(first.CreateVariantKey()),
                "Ray-geometry policy participates in the native BLAS identity.");
        });
    }

    [Test]
    public void VariantRetention_SelectsLeastReusedInactivePublishedCandidate()
    {
        OpacityMicromapContentKey geometry = Key(20);
        StaticBlasVariantKey active = VariantKey(geometry, 21);
        StaticBlasVariantKey incomplete = VariantKey(geometry, 22);
        StaticBlasVariantKey retained = VariantKey(geometry, 23);
        StaticBlasVariantKey evicted = VariantKey(geometry, 24);
        OpacityMicromapVariantRetentionCandidate[] candidates =
        [
            new(active, 0UL, 0UL, Active: true, Published: true,
                HasCandidateBlas: true),
            new(incomplete, 0UL, 0UL, Active: false, Published: false,
                HasCandidateBlas: true),
            new(retained, 8UL, 10UL, Active: false, Published: true,
                HasCandidateBlas: true),
            new(evicted, 2UL, 100UL, Active: false, Published: true,
                HasCandidateBlas: true)
        ];

        bool selected = OpacityMicromapVariantRetentionPolicy
            .TrySelectEvictionCandidate(
                candidates,
                geometry,
                restrictToGeometry: true,
                out StaticBlasVariantKey selectedKey);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.True);
            Assert.That(selectedKey, Is.EqualTo(evicted));
        });
    }

    [Test]
    public void VariantRetention_UsesOldestUseThenStableKeyAndHonorsGeometryScope()
    {
        OpacityMicromapContentKey geometry = Key(30);
        OpacityMicromapContentKey otherGeometry = Key(31);
        StaticBlasVariantKey stableFirst = VariantKey(geometry, 32);
        StaticBlasVariantKey stableSecond = VariantKey(geometry, 33);
        StaticBlasVariantKey other = VariantKey(otherGeometry, 34);
        OpacityMicromapVariantRetentionCandidate[] candidates =
        [
            new(stableSecond, 4UL, 7UL, Active: false, Published: true,
                HasCandidateBlas: true),
            new(stableFirst, 4UL, 7UL, Active: false, Published: true,
                HasCandidateBlas: true),
            new(other, 0UL, 0UL, Active: false, Published: true,
                HasCandidateBlas: true)
        ];

        bool scoped = OpacityMicromapVariantRetentionPolicy
            .TrySelectEvictionCandidate(
                candidates,
                geometry,
                restrictToGeometry: true,
                out StaticBlasVariantKey scopedKey);
        bool global = OpacityMicromapVariantRetentionPolicy
            .TrySelectEvictionCandidate(
                candidates,
                geometry,
                restrictToGeometry: false,
                out StaticBlasVariantKey globalKey);
        StaticBlasVariantKey expectedScoped =
            OpacityMicromapVariantRetentionPolicy.CompareKeys(
                stableFirst,
                stableSecond) < 0
                ? stableFirst
                : stableSecond;

        Assert.Multiple(() =>
        {
            Assert.That(scoped, Is.True);
            Assert.That(scopedKey, Is.EqualTo(expectedScoped));
            Assert.That(global, Is.True);
            Assert.That(globalKey, Is.EqualTo(other));
        });
    }

    [Test]
    public void VariantRetention_FailsClosedWhenOnlyProtectedEntriesExist()
    {
        OpacityMicromapContentKey geometry = Key(40);
        StaticBlasVariantKey plain = StaticBlasVariantKey.Plain(
            geometry,
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            AccelerationStructureManager.StaticBlasBuildAbi);
        OpacityMicromapVariantRetentionCandidate[] candidates =
        [
            new(plain, 0UL, 0UL, Active: false, Published: true,
                HasCandidateBlas: true),
            new(VariantKey(geometry, 41), 0UL, 0UL, Active: true,
                Published: true, HasCandidateBlas: true),
            new(VariantKey(geometry, 42), 0UL, 0UL, Active: false,
                Published: true, HasCandidateBlas: false)
        ];

        bool selected = OpacityMicromapVariantRetentionPolicy
            .TrySelectEvictionCandidate(
                candidates,
                geometry,
                restrictToGeometry: true,
                out StaticBlasVariantKey selectedKey);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.False);
            Assert.That(selectedKey, Is.EqualTo(default(StaticBlasVariantKey)));
        });
    }

    [Test]
    public void ContentDiagnostics_ExposeExactKnownCoverageAndFailClosedOnOverflow()
    {
        var valid = new OpacityMicromapContentDiagnostics(
            Authoritative: true,
            RegisteredMeshCount: 2,
            UniqueVariantCount: 1,
            RejectedRegistrationCount: 3UL,
            StaleMaterialRegistrationCount: 1,
            AmbiguousContentKeyCount: 0,
            PrimitiveCount: 2UL,
            MaterialContractCount: 1UL,
            OmmDataBytes: 8UL,
            IndexBytes: 8UL,
            DescriptorBytes: 16UL,
            ClassifiedPayloadCount: 1,
            UnclassifiedPayloadCount: 0,
            OpaqueMicrotriangleCount: 5UL,
            TransparentMicrotriangleCount: 3UL,
            UnknownOpaqueMicrotriangleCount: 1UL,
            UnknownTransparentMicrotriangleCount: 1UL,
            MaximumSubdivisionLevel: 1U,
            SubdivisionHistogram:
                new OpacityMicromapSubdivisionHistogram(
                    0UL, 2UL, 0UL, 0UL,
                    0UL, 0UL, 0UL, 0UL,
                    0UL, 0UL, 0UL, 0UL,
                    0UL, 0UL, 0UL, 0UL),
            Detail: " authoritative ");
        OpacityMicromapContentDiagnostics overflow = valid with
        {
            OpaqueMicrotriangleCount = ulong.MaxValue,
            TransparentMicrotriangleCount = 1UL
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.IsValid, Is.True);
            Assert.That(valid.KnownMicrotriangleCount, Is.EqualTo(8UL));
            Assert.That(valid.ClassifiedMicrotriangleCount, Is.EqualTo(10UL));
            Assert.That(valid.KnownCoverage, Is.EqualTo(0.8).Within(1e-12));
            Assert.That(valid.NormalizeForPersistence().Detail,
                Is.EqualTo("authoritative"));
            Assert.That(overflow.IsValid, Is.False);
            Assert.That(overflow.NormalizeForPersistence(),
                Is.EqualTo(OpacityMicromapContentDiagnostics.Unavailable));
        });
    }

    [Test]
    public void RuntimePartitioner_ExtractsOnlySubmeshDescriptorsAndRebasesIndices()
    {
        OpacityMicromapCookedPayload aggregate = AggregatePayload();

        bool created = OpacityMicromapRuntimePayloadPartitioner
            .TryCreateSubmeshPayload(
                aggregate,
                firstPrimitive: 2U,
                primitiveCount: 2U,
                materialSlot: 1U,
                out OpacityMicromapCookedPayload? partition,
                out string detail);

        Assert.That(created, Is.True, detail);
        Assert.That(partition, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(partition!.PrimitiveCount, Is.EqualTo(2U));
            Assert.That(partition.DescriptorCount, Is.EqualTo(2U));
            Assert.That(partition.MaterialContracts, Has.Count.EqualTo(1));
            Assert.That(partition.MaterialContracts[0].MaterialSlot,
                Is.EqualTo(1U));
            Assert.That(partition.MaterialContracts[0].FirstPrimitive,
                Is.Zero);
            Assert.That(partition.MaterialContracts[0].PrimitiveCount,
                Is.EqualTo(2U));
            Assert.That(ReadUInt32(partition.IndexData.Span, 0),
                Is.EqualTo(0U));
            Assert.That(ReadUInt32(partition.IndexData.Span, sizeof(uint)),
                Is.EqualTo(1U));
            Assert.That(partition.UsageHistogram.Single().Count,
                Is.EqualTo(2UL));
            Assert.That(partition.SourceContentHash,
                Is.Not.EqualTo(aggregate.SourceContentHash));
            Assert.That(
                OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
                    partition,
                    out string validationDetail),
                Is.True,
                validationDetail);
        });
    }

    [Test]
    public void RuntimePartitioner_FailsClosedForNonExactOrDescriptorFreeRanges()
    {
        OpacityMicromapCookedPayload aggregate = AggregatePayload();
        bool wrongMaterial = OpacityMicromapRuntimePayloadPartitioner
            .TryCreateSubmeshPayload(
                aggregate,
                firstPrimitive: 2U,
                primitiveCount: 2U,
                materialSlot: 0U,
                out _,
                out string wrongMaterialDetail);

        byte[] descriptor = new byte[8];
        WriteDescriptor(descriptor, 0, 0U);
        byte[] specialIndex = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            specialIndex,
            unchecked((uint)OpacityMicromapSpecialIndexEXT
                .FullyUnknownOpaqueExt));
        OpacityMicromapCookedPayload specialOnly =
            OpacityMicromapCookedPayload.Create(
                7U,
                Key(80),
                Key(81),
                1U,
                1U,
                1U,
                [MaterialContract(0U, 0U, 1U)],
                [new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    1U,
                    1UL)],
                new byte[1],
                specialIndex,
                descriptor);
        bool descriptorFree = OpacityMicromapRuntimePayloadPartitioner
            .TryCreateSubmeshPayload(
                specialOnly,
                firstPrimitive: 0U,
                primitiveCount: 1U,
                materialSlot: 0U,
                out _,
                out string descriptorFreeDetail);

        Assert.Multiple(() =>
        {
            Assert.That(wrongMaterial, Is.False);
            Assert.That(wrongMaterialDetail,
                Is.EqualTo(
                    "omm-runtime-partition-material-range-is-not-submesh-exact"));
            Assert.That(descriptorFree, Is.False);
            Assert.That(descriptorFreeDetail,
                Is.EqualTo(
                    "omm-runtime-partition-submesh-has-only-special-indices"));
        });
    }

    private static OpacityMicromapRuntimeMeshRegistration Registration()
    {
        GPUVertexPositionStream[] positions = Positions();
        uint[] indices = [0U, 1U, 2U, 2U, 1U, 3U];
        return new OpacityMicromapRuntimeMeshRegistration(
            new MeshHandle(5, 1U),
            new MaterialHandle(7, 2U),
            new OpacityMicromapMaterialRevision(3U, 4U),
            OpacityMicromapRuntimeRegistrationStore.ComputeMeshGeometryKey(
                positions,
                indices),
            Payload(),
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            AccelerationStructureManager.StaticBlasBuildAbi);
    }

    private static GPUVertexPositionStream[] Positions() =>
    [
        new() { Position = new Vector4(0f, 0f, 0f, 1f) },
        new() { Position = new Vector4(1f, 0f, 0f, 1f) },
        new() { Position = new Vector4(0f, 1f, 0f, 1f) },
        new() { Position = new Vector4(1f, 1f, 0f, 1f) }
    ];

    private static OpacityMicromapCookedPayload Payload()
    {
        byte[] descriptors = new byte[2 * 8];
        WriteDescriptor(descriptors, 0, 0U);
        WriteDescriptor(descriptors, 8, 4U);
        byte[] indices = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(indices, 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(sizeof(uint)),
            1U);
        return OpacityMicromapCookedPayload.Create(
            cookAbi: 7U,
            sourceContentHash: Key(1),
            sdkProvenanceHash: Key(2),
            maximumSubdivisionLevel: 1U,
            primitiveCount: 2U,
            descriptorCount: 2U,
            materialContracts:
            [
                new OpacityMicromapMaterialContract(
                    MaterialSlot: 0U,
                    FirstPrimitive: 0U,
                    PrimitiveCount: 2U,
                    TexCoordSet: 0,
                    UvTransform: OpacityMicromapUvTransformBits.Identity,
                    TextureContentHash: Key(3),
                    TextureFormatAndMipHash: Key(4),
                    Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
                    MaterialAlphaBits: Bits(1f),
                    UniformVertexAlphaBits: Bits(1f),
                    AlphaCutoffBits: Bits(0.5f),
                    FixedLodBits: Bits(0f),
                    AlphaContractRevision: 1U,
                    ShaderAbiRevision: 1U)
            ],
            usageHistogram:
            [
                new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    1U,
                    2UL)
            ],
            ommData: [1, 2, 3, 4, 5, 6, 7, 8],
            indexData: indices,
            descriptorData: descriptors);
    }

    private static OpacityMicromapCookedPayload AggregatePayload()
    {
        byte[] descriptors = new byte[3 * 8];
        WriteDescriptor(descriptors, 0, 0U);
        WriteDescriptor(descriptors, 8, 8U);
        WriteDescriptor(descriptors, 16, 16U);
        byte[] indices = new byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(indices, 2U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(sizeof(uint)),
            unchecked((uint)OpacityMicromapSpecialIndexEXT
                .FullyUnknownOpaqueExt));
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(2 * sizeof(uint)),
            0U);
        BinaryPrimitives.WriteUInt32LittleEndian(
            indices.AsSpan(3 * sizeof(uint)),
            2U);
        return OpacityMicromapCookedPayload.Create(
            cookAbi: 7U,
            sourceContentHash: Key(70),
            sdkProvenanceHash: Key(71),
            maximumSubdivisionLevel: 1U,
            primitiveCount: 4U,
            descriptorCount: 3U,
            materialContracts:
            [
                MaterialContract(0U, 0U, 2U),
                MaterialContract(1U, 2U, 2U)
            ],
            usageHistogram:
            [
                new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    1U,
                    3UL)
            ],
            ommData: new byte[17],
            indexData: indices,
            descriptorData: descriptors);
    }

    private static OpacityMicromapMaterialContract MaterialContract(
        uint materialSlot,
        uint firstPrimitive,
        uint primitiveCount) => new(
            materialSlot,
            firstPrimitive,
            primitiveCount,
            TexCoordSet: 0,
            UvTransform: OpacityMicromapUvTransformBits.Identity,
            TextureContentHash: Key(72),
            TextureFormatAndMipHash: Key(73),
            Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
            MaterialAlphaBits: Bits(1f),
            UniformVertexAlphaBits: Bits(1f),
            AlphaCutoffBits: Bits(0.5f),
            FixedLodBits: Bits(0f),
            AlphaContractRevision: 1U,
            ShaderAbiRevision: 1U);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            source.Slice(offset, sizeof(uint)));

    private static void WriteDescriptor(
        Span<byte> destination,
        int offset,
        uint dataOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[offset..],
            dataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[(offset + sizeof(uint))..],
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[(offset + sizeof(uint) + sizeof(ushort))..],
            (ushort)OpacityMicromapFormatEXT.Format4StateExt);
    }

    private static uint Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value);

    private static StaticBlasVariantKey VariantKey(
        OpacityMicromapContentKey geometry,
        byte content) => new(
            geometry,
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            Key(content),
            AccelerationStructureManager.StaticBlasBuildAbi);

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(
            SHA256.HashData([value]));
}
