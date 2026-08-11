using System.Security.Cryptography;
using System.Buffers.Binary;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpacityMicromapContractsTests
{
    [Test]
    public void FourStateClassifier_PreservesEqualityAndUnknownCandidateSemantics()
    {
        OpacityMicromapEligibility eligibility =
            OpacityMicromapEligibilityEvaluator.Evaluate(
                OpacityMicromapEligibilityInput.ExactStaticMask);

        OpacityMicromapClassification transparent =
            OpacityMicromapFourStateClassifier.Classify(
                Range(0.0f, 0.49f, 0.25f),
                Bits(0.5f),
                eligibility);
        OpacityMicromapClassification opaqueAtEquality =
            OpacityMicromapFourStateClassifier.Classify(
                Range(0.5f, 1.0f, 0.5f),
                Bits(0.5f),
                eligibility);
        OpacityMicromapClassification unknownTransparent =
            OpacityMicromapFourStateClassifier.Classify(
                Range(0.49f, 0.5f, 0.49f),
                Bits(0.5f),
                eligibility);
        OpacityMicromapClassification unknownOpaque =
            OpacityMicromapFourStateClassifier.Classify(
                Range(0.49f, 0.5f, 0.5f),
                Bits(0.5f),
                eligibility);

        Assert.Multiple(() =>
        {
            Assert.That(eligibility.Eligible, Is.True);
            Assert.That(transparent.State, Is.EqualTo(OpacityMicromapMicrotriangleState.Transparent));
            Assert.That(transparent.RequiresShaderConfirmation, Is.False);
            Assert.That(opaqueAtEquality.State, Is.EqualTo(OpacityMicromapMicrotriangleState.Opaque));
            Assert.That(opaqueAtEquality.RequiresShaderConfirmation, Is.False);
            Assert.That(unknownTransparent.State,
                Is.EqualTo(OpacityMicromapMicrotriangleState.UnknownTransparent));
            Assert.That(unknownTransparent.RequiresShaderConfirmation, Is.True);
            Assert.That(unknownOpaque.State,
                Is.EqualTo(OpacityMicromapMicrotriangleState.UnknownOpaque));
            Assert.That(unknownOpaque.RequiresShaderConfirmation, Is.True);
        });
    }

    [Test]
    public void Eligibility_FailsClosedForDynamicAlphaAndNonIdentityUvTransform()
    {
        OpacityMicromapEligibility dynamicMask =
            OpacityMicromapEligibilityEvaluator.Evaluate(
                OpacityMicromapEligibilityInput.ExactStaticMask with
                {
                    AnimatedMaskAbsent = false
                });
        OpacityMicromapEligibility transformedUv =
            OpacityMicromapEligibilityEvaluator.Evaluate(
                OpacityMicromapEligibilityInput.ExactStaticMask with
                {
                    UvTransform = new OpacityMicromapUvTransformBits(
                        Bits(1.0f), 0, Bits(0.25f),
                        0, Bits(1.0f), 0,
                        0, 0, Bits(1.0f))
                });
        OpacityMicromapClassification fallback =
            OpacityMicromapFourStateClassifier.Classify(
                Range(1.0f, 1.0f, 1.0f),
                Bits(0.5f),
                dynamicMask);

        Assert.Multiple(() =>
        {
            Assert.That(dynamicMask.Eligible, Is.False);
            Assert.That(dynamicMask.Failure,
                Is.EqualTo(OpacityMicromapEligibilityFailure.AnimatedMaskPresent));
            Assert.That(transformedUv.Eligible, Is.False);
            Assert.That(transformedUv.Failure,
                Is.EqualTo(OpacityMicromapEligibilityFailure.UvTransformNotIdentity));
            Assert.That(fallback.RequiresShaderConfirmation, Is.True);
            Assert.That(fallback.State,
                Is.EqualTo(OpacityMicromapMicrotriangleState.UnknownOpaque));
        });
    }

    [Test]
    public void ContentKey_UsesCanonicalRawBytesAndTracksOnlyContentInputs()
    {
        OpacityMicromapMaterialContract material = MaterialContract();
        var input = new OpacityMicromapContentKeyInput(
            MeshTopologyBytes: new byte[] { 1, 2, 3 },
            IndexBytes: new byte[] { 3, 2, 1 },
            UvBytes: new byte[] { 4, 5, 6 },
            Material: material,
            TextureResidencyRevision: 13,
            CookAbi: 7,
            PayloadSchemaVersion: OpacityMicromapCookedPayloadCodec.CurrentSchemaVersion,
            PayloadKind: OpacityMicromapPayloadKind.VulkanExtFourState,
            SubdivisionPolicy: new OpacityMicromapSubdivisionPolicy(1, 4, 9));

        OpacityMicromapContentKey first = OpacityMicromapContentKeyBuilder.Compute(input);
        OpacityMicromapContentKey sameValues = OpacityMicromapContentKeyBuilder.Compute(input);
        OpacityMicromapContentKey changedMaterial = OpacityMicromapContentKeyBuilder.Compute(
            input with { Material = material with { AlphaCutoffBits = Bits(0.6f) } });
        OpacityMicromapContentKey changedResidency = OpacityMicromapContentKeyBuilder.Compute(
            input with { TextureResidencyRevision = 14 });

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(sameValues));
            Assert.That(changedMaterial, Is.Not.EqualTo(first));
            Assert.That(changedResidency, Is.Not.EqualTo(first));
            Assert.That(first.ToString(), Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void CookedPayload_RoundTripsAndRejectsSpanCorruptionAndHostileCounts()
    {
        OpacityMicromapCookedPayload payload = CreatePayload();
        byte[] bytes = OpacityMicromapCookedPayloadCodec.Write(payload);
        OpacityMicromapPayloadReadResult roundTrip =
            OpacityMicromapCookedPayloadCodec.TryRead(bytes);

        byte[] corruptedSpan = bytes.ToArray();
        // First span is the material table.  The OMM data starts after the
        // first 172-byte table, aligned to eight bytes.
        int ommOffset = (OpacityMicromapCookedPayloadCodec.HeaderBytes +
            OpacityMicromapCookedPayloadCodec.MaterialContractBytes + 7) & ~7;
        corruptedSpan[ommOffset] ^= 0x80;
        OpacityMicromapPayloadReadResult corruptResult =
            OpacityMicromapCookedPayloadCodec.TryRead(corruptedSpan);

        byte[] hostileCount = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(hostileCount.AsSpan(100, 4), uint.MaxValue);
        OpacityMicromapPayloadReadResult hostileResult =
            OpacityMicromapCookedPayloadCodec.TryRead(hostileCount);

        Assert.Multiple(() =>
        {
            Assert.That(roundTrip.Success, Is.True, roundTrip.Detail);
            Assert.That(roundTrip.Payload, Is.Not.Null);
            Assert.That(roundTrip.Payload!.SourceContentHash,
                Is.EqualTo(payload.SourceContentHash));
            Assert.That(roundTrip.Payload.DescriptorData.Span.ToArray(),
                Is.EqualTo(payload.DescriptorData.Span.ToArray()));
            Assert.That(roundTrip.Payload.ClassificationStatistics,
                Is.EqualTo(payload.ClassificationStatistics));
            Assert.That(corruptResult.Success, Is.False);
            Assert.That(corruptResult.Failure,
                Is.EqualTo(OpacityMicromapPayloadValidationFailure.SpanChecksumMismatch));
            Assert.That(hostileResult.Success, Is.False);
            Assert.That(hostileResult.Failure,
                Is.EqualTo(OpacityMicromapPayloadValidationFailure.CountInvalid));
        });
    }

    [Test]
    public async Task BridgeAndNullBackend_FailClosedWithoutAllocatingOrClaimingKhrSupport()
    {
        OpacityMicromapBakeResult bake = await FailClosedOpacityMicromapBakeBridge.Instance
            .BakeAsync(default, CancellationToken.None);
        OpacityMicromapCookedPayload payload = CreatePayload();
        var selector = new OpacityMicromapBackendSelector();
        OpacityMicromapBackendResolution khr = selector.Resolve(
            OpacityMicromapBackendKind.KhrReserved,
            payload,
            payload.SourceContentHash);
        OpacityMicromapBackendBuildResult nullBuild = await khr.Backend.BuildAsync(
            new OpacityMicromapBackendBuildRequest(
                payload.SourceContentHash,
                payload,
                AccelerationStructureBuildAbi: 1,
                PublicationGeneration: 1),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(bake.Succeeded, Is.False);
            Assert.That(bake.Failure, Is.EqualTo(OpacityMicromapBakeFailure.BridgeUnavailable));
            Assert.That(khr.Backend.Kind, Is.EqualTo(OpacityMicromapBackendKind.Null));
            Assert.That(khr.FallbackReason,
                Is.EqualTo(OpacityMicromapBackendFallbackReason.KhrBackendNotImplemented));
            Assert.That(nullBuild.Succeeded, Is.False);
            Assert.That(nullBuild.Lease, Is.Null);
            Assert.That(nullBuild.UsesOrdinaryCandidatePath, Is.True);
        });
    }

    [Test]
    public void BackendSelector_AdmitsOnlyInjectedExtBackendWithExactPayloadKey()
    {
        OpacityMicromapCookedPayload payload = CreatePayload();
        var selector = new OpacityMicromapBackendSelector(new FakeExtBackend());

        OpacityMicromapBackendResolution selected = selector.Resolve(
            OpacityMicromapBackendKind.VulkanExtFourState,
            payload,
            payload.SourceContentHash);
        OpacityMicromapBackendResolution stale = selector.Resolve(
            OpacityMicromapBackendKind.VulkanExtFourState,
            payload,
            Key(99));

        Assert.Multiple(() =>
        {
            Assert.That(selected.UsesExtBackend, Is.True);
            Assert.That(selected.FallbackReason, Is.EqualTo(OpacityMicromapBackendFallbackReason.None));
            Assert.That(stale.Backend.Kind, Is.EqualTo(OpacityMicromapBackendKind.Null));
            Assert.That(stale.FallbackReason,
                Is.EqualTo(OpacityMicromapBackendFallbackReason.ContentKeyMismatch));
        });
    }

    [Test]
    public void BlasVariantPlanner_RetainsPlainFallbackAndBoundsQualifiedVariants()
    {
        OpacityMicromapContentKey meshA = Key(20);
        OpacityMicromapContentKey meshB = Key(21);
        var policy = new OpacityMicromapBlasVariantCapPolicy(
            Enabled: true,
            MaximumOpacityVariantsPerMesh: 1,
            MaximumOpacityVariantsGlobally: 1,
            MaximumOpacityVariantResidentBytes: 400);
        StaticBlasVariantKey plainA = StaticBlasVariantKey.Plain(
            meshA,
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            1);
        StaticBlasVariantKey plainB = StaticBlasVariantKey.Plain(
            meshB,
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
            1);
        var candidates = new[]
        {
            new OpacityMicromapBlasVariantCandidate(plainA, 100, 0, 0, 0, false),
            new OpacityMicromapBlasVariantCandidate(plainB, 100, 0, 0, 0, false),
            new OpacityMicromapBlasVariantCandidate(
                plainA with { OpacityMicromapContentKeyOrNull = Key(30) },
                180, 100, 8, 10.0, true),
            new OpacityMicromapBlasVariantCandidate(
                plainA with { OpacityMicromapContentKeyOrNull = Key(31) },
                180, 100, 7, 100.0, true),
            new OpacityMicromapBlasVariantCandidate(
                plainB with { OpacityMicromapContentKeyOrNull = Key(32) },
                180, 100, 1, 100.0, true)
        };

        OpacityMicromapBlasVariantPlan plan =
            OpacityMicromapBlasVariantCapPlanner.Plan(candidates, policy);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SelectedOpacityVariantCount, Is.EqualTo(1));
            Assert.That(plan.SelectedOpacityVariantResidentBytes, Is.EqualTo(280));
            Assert.That(plan.Decisions.Count(d => d.Selected && d.Key.IsPlainFallback), Is.EqualTo(2));
            Assert.That(plan.Decisions.Any(d => d.Selected &&
                d.Key.OpacityMicromapContentKeyOrNull == Key(30)), Is.True);
            Assert.That(plan.Decisions.Any(d => !d.Selected &&
                d.Reason == OpacityMicromapBlasVariantDecisionReason.PerMeshCapReached), Is.True);
            Assert.That(plan.Decisions.Any(d => !d.Selected &&
                d.Reason == OpacityMicromapBlasVariantDecisionReason.GlobalCapReached), Is.True);
        });
    }

    private static OpacityMicromapCookedPayload CreatePayload() =>
        OpacityMicromapCookedPayload.Create(
            cookAbi: 7,
            sourceContentHash: Key(1),
            sdkProvenanceHash: Key(2),
            maximumSubdivisionLevel: 4,
            primitiveCount: 1,
            descriptorCount: 1,
            materialContracts: new[] { MaterialContract() },
            usageHistogram: new[]
            {
                new OpacityMicromapUsage(OpacityMicromapFormat.FourState, 1, 1)
            },
            ommData: new byte[] { 1, 2, 3, 4, 5 },
            indexData: new byte[] { 6, 7, 8, 9 },
            descriptorData: new byte[] { 10, 11 },
            classificationStatistics: new OpacityMicromapClassificationStatistics(2, 3, 5, 7));

    private static OpacityMicromapMaterialContract MaterialContract() => new(
        MaterialSlot: 3,
        FirstPrimitive: 0,
        PrimitiveCount: 1,
        TexCoordSet: 0,
        UvTransform: OpacityMicromapUvTransformBits.Identity,
        TextureContentHash: Key(3),
        TextureFormatAndMipHash: Key(4),
        Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
        MaterialAlphaBits: Bits(1.0f),
        UniformVertexAlphaBits: Bits(1.0f),
        AlphaCutoffBits: Bits(0.5f),
        FixedLodBits: Bits(0.0f),
        AlphaContractRevision: 8,
        ShaderAbiRevision: 9);

    private static OpacityMicromapAlphaRange Range(float minimum, float maximum, float representative) => new(
        Bits(minimum), Bits(maximum), Bits(representative));

    private static uint Bits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData(new[] { value }));

    private sealed class FakeExtBackend : IExtOpacityMicromapBackend
    {
        public OpacityMicromapBackendKind Kind => OpacityMicromapBackendKind.VulkanExtFourState;

        public OpacityMicromapRuntimeCapabilities Capabilities => new(
            ExtensionAvailable: true,
            FeatureEnabled: true,
            AccelerationStructureDependencyAvailable: true,
            CommandBufferBuildAvailable: true,
            FourStateFormatAvailable: true,
            MaximumFourStateSubdivisionLevel: 4,
            CompactionAvailable: true);

        public ValueTask<OpacityMicromapBackendBuildResult> BuildAsync(
            OpacityMicromapBackendBuildRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OpacityMicromapBackendBuildResult.Fallback(
                OpacityMicromapBackendFallbackReason.BuildUnavailable,
                "test-ext-backend-does-not-create-native-resources"));
    }
}
