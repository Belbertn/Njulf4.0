using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class NvidiaOpacityMicromapModelPayloadProducerTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "nvidia-omm-model-producer",
            TestContext.CurrentContext.Test.ID,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Test]
    public void Producer_BakesEligibleParts_MergesExactNativeLayoutAndUnknownFallbacks()
    {
        TextureSamplerDescription sampler = ExactSampler();
        OpacityMicromapCookedTextureArtifact artifact = CookExactRgba8Texture(sampler);
        ModelMaterial material = ExactMaskMaterial(artifact, sampler);
        CookedMeshPayload mesh = CreateTwoTrianglePayload();
        var materials = new CookedMaterialTable([material])
        {
            OpacityMicromapTextureArtifacts = [artifact]
        };
        var bridge = new DeterministicBridge();
        var producer = new NvidiaOpacityMicromapModelPayloadProducer(
            bridge,
            new NvidiaOpacityMicromapCookPolicy
            {
                RequestedSubdivisionLevel = 1,
                MaximumSubdivisionLevel = 4,
                MaximumWorkloadSize = 1UL << 20,
                MaximumArrayDataBytes = 1U << 20
            });
        var context = new OpacityMicromapModelCookContext(
            Path.Combine(_directory, "fixture.gltf"),
            Guid.NewGuid(),
            11,
            12,
            13,
            1,
            new ModelMesh(),
            null!,
            mesh,
            materials);

        OpacityMicromapPayloadProductionResult result = producer.Produce(context);

        Assert.That(result.Status, Is.EqualTo(OpacityMicromapPayloadProductionStatus.Produced), result.Detail);
        OpacityMicromapCookedPayload payload = result.Payload!;
        Assert.That(
            OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
                payload,
                out string nativeDetail),
            Is.True,
            nativeDetail);
        Assert.Multiple(() =>
        {
            Assert.That(bridge.CallCount, Is.EqualTo(2));
            Assert.That(payload.PrimitiveCount, Is.EqualTo(2));
            Assert.That(payload.DescriptorCount, Is.EqualTo(2));
            Assert.That(payload.MaterialContracts.Select(static item => item.FirstPrimitive),
                Is.EqualTo(new uint[] { 0, 1 }));
            Assert.That(ReadUInt32(payload.IndexData.Span, 0), Is.EqualTo(0U));
            Assert.That(ReadUInt32(payload.IndexData.Span, 4), Is.EqualTo(1U));
            Assert.That(ReadUInt32(payload.DescriptorData.Span, 0), Is.EqualTo(0U));
            Assert.That(ReadUInt32(payload.DescriptorData.Span, 8), Is.EqualTo(8U));
            Assert.That(payload.UsageHistogram.Single().Count, Is.EqualTo(2UL));
            Assert.That(payload.SdkProvenanceHash,
                Is.EqualTo(bridge.Contract.Provenance.ComputeFingerprint()));
        });
    }

    [Test]
    public void Producer_RejectsAnisotropicOrNonUnitAlphaWithoutInvokingNativeBridge()
    {
        TextureSamplerDescription sampler = ExactSampler() with { MaxAnisotropy = 16.0f };
        OpacityMicromapCookedTextureArtifact artifact = CookExactRgba8Texture(sampler);
        ModelMaterial material = ExactMaskMaterial(artifact, sampler);
        CookedMeshPayload mesh = CreateTwoTrianglePayload();
        var bridge = new DeterministicBridge();
        var producer = new NvidiaOpacityMicromapModelPayloadProducer(bridge);
        var context = new OpacityMicromapModelCookContext(
            "fixture.gltf",
            Guid.NewGuid(),
            11,
            12,
            13,
            1,
            new ModelMesh(),
            null!,
            mesh,
            new CookedMaterialTable([material])
            {
                OpacityMicromapTextureArtifacts = [artifact]
            });

        OpacityMicromapPayloadProductionResult result = producer.Produce(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status,
                Is.EqualTo(OpacityMicromapPayloadProductionStatus.NotProduced));
            Assert.That(result.Payload, Is.Null);
            Assert.That(bridge.CallCount, Is.Zero);
        });
    }

    [Test]
    public void InclusiveCutoffTranslation_IsExactAtEveryBoundaryClass()
    {
        float[] cutoffs =
        [
            0.0f,
            float.Epsilon,
            MathF.BitIncrement(float.Epsilon),
            0.25f,
            0.5f,
            MathF.BitDecrement(1.0f),
            1.0f
        ];
        foreach (float cutoff in cutoffs)
        {
            float sdkCutoff =
                NvidiaOpacityMicromapCookPolicy.TranslateInclusiveCutoffForSdk(cutoff);
            float[] samples =
            [
                0.0f,
                cutoff == 0.0f ? 0.0f : MathF.BitDecrement(cutoff),
                cutoff,
                MathF.BitIncrement(cutoff),
                1.0f
            ];
            foreach (float sample in samples.Where(static value =>
                         float.IsFinite(value) && value is >= 0.0f and <= 1.0f))
            {
                bool njulf = sample >= cutoff;
                bool sdk = cutoff == 0.0f || sample > sdkCutoff;
                Assert.That(sdk, Is.EqualTo(njulf),
                    $"sample={sample:R}, cutoff={cutoff:R}, sdkCutoff={sdkCutoff:R}");
            }
        }
    }

    [Test]
    public void PinnedBridge_RejectsBinaryDigestMismatchBeforeNativeLoad()
    {
        string library = Path.Combine(_directory, "untrusted.dll");
        string manifest = Path.Combine(_directory, "omm-provenance.json");
        File.WriteAllBytes(library, [1, 2, 3, 4]);
        var document = new OpacityMicromapBridgeProvenanceManifest
        {
            SourceUri = "https://github.com/NVIDIA-RTX/OMM",
            CommitOrRelease = "9abacd0f187d0efca491946a29ba7df8c5345264",
            LicenseIdentifier = "NVIDIA-RTX-SDKs",
            BuildFlags = "Release",
            CompilerIdentity = "test-compiler",
            BinarySha256 = Key(99).ToString(),
            SdkVersion = "1.9.2"
        };
        File.WriteAllText(manifest, JsonSerializer.Serialize(document,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        PinnedOpacityMicromapBakeBridgeOptions options =
            OpacityMicromapBridgeProvenanceManifest.LoadOptions(manifest, library);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => _ = new PinnedOpacityMicromapBakeBridge(options))!;

        Assert.That(exception.Message, Does.Contain("SHA-256"));
    }

    private OpacityMicromapCookedTextureArtifact CookExactRgba8Texture(
        TextureSamplerDescription sampler)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL0NwAAAABJRU5ErkJggg==");
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".ktx2");
        var source = new ModelTextureSource
        {
            DebugName = "mask.png",
            SourceKind = TextureSourceKind.EmbeddedMemory,
            Bytes = png,
            ContainerKind = TextureContainerKind.StandardImage,
            CacheIdentity = "test-mask"
        };
        var cooker = new TextureCooker();
        CookedTextureReport report = cooker.Cook(
            source,
            path,
            new TextureCookOptions(
                MaxDimension: 1,
                ColorSpace: TextureColorSpace.Srgb,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                Semantic: TextureSemantic.Color,
                PreserveAlphaCoverage: true,
                AlphaCutoff: 0.5f));
        return new OpacityMicromapCookedTextureArtifact(
            0,
            Path.GetFullPath(path),
            OpacityMicromapContentKey.FromSha256(SHA256.HashData(File.ReadAllBytes(path))),
            report.VulkanFormat,
            report.CookedWidth,
            report.CookedHeight,
            report.MipCount,
            TextureColorSpace.Srgb,
            sampler,
            report.AlphaCoveragePreserved,
            report.AlphaCoveragePreserved ? report.AlphaCutoff : null);
    }

    private static ModelMaterial ExactMaskMaterial(
        OpacityMicromapCookedTextureArtifact artifact,
        TextureSamplerDescription sampler) => new()
    {
        Name = "ExactMask",
        AlphaMode = ModelAlphaMode.Mask,
        AlphaCutoff = 0.5f,
        Albedo = Vector4.One,
        BaseColorTexture = new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                DebugName = "mask.ktx2",
                FilePath = artifact.AbsoluteKtx2Path,
                SourceKind = TextureSourceKind.ExternalFile,
                ContainerKind = TextureContainerKind.Ktx2,
                CacheIdentity = "cooked:test-mask"
            },
            Sampler = sampler,
            ColorSpace = TextureColorSpace.Srgb,
            TexCoordSet = 0,
            Offset = Vector2.Zero,
            Scale = new Vector2(1.0f, 1.0f),
            RotationRadians = 0.0f
        }
    };

    private static TextureSamplerDescription ExactSampler() => new(
        TextureWrapMode.Repeat,
        TextureWrapMode.Repeat,
        TextureFilterMode.Linear,
        TextureFilterMode.Linear,
        TextureMipFilterMode.Linear,
        1.0f);

    private static CookedMeshPayload CreateTwoTrianglePayload()
    {
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        CookedSubMeshRecord Record(string name, int vertex, int index) => new(
            name,
            0,
            -1,
            -1,
            Matrix4x4.Identity,
            vertex,
            3,
            index,
            3,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            [],
            [],
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)(ProcessedVertexAttribute.Position |
                   ProcessedVertexAttribute.TexCoord0 |
                   ProcessedVertexAttribute.VertexColor));
        CookedVertexPositionStream Position(float x, float y) => new()
        {
            Position = new Vector4(x, y, 0.0f, 1.0f)
        };
        CookedVertexUvColorStream Uv(float x, float y) => new()
        {
            TexCoord = new Vector2(x, y),
            Color = Vector4.One
        };
        return new CookedMeshPayload(
            [Record("A", 0, 0), Record("B", 3, 3)],
            [
                Position(0, 0), Position(1, 0), Position(0, 1),
                Position(0, 0), Position(1, 0), Position(0, 1)
            ],
            new CookedVertexNormalTangentStream[6],
            [Uv(0, 0), Uv(1, 0), Uv(0, 1), Uv(0, 0), Uv(1, 0), Uv(0, 1)],
            [],
            [0U, 1U, 2U, 0U, 1U, 2U],
            [],
            [],
            [],
            [],
            []);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData([value]));

    private sealed class DeterministicBridge : IOpacityMicromapBakeBridge
    {
        public DeterministicBridge()
        {
            var provenance = new OpacityMicromapSdkProvenance(
                "https://github.com/NVIDIA-RTX/OMM",
                "9abacd0f187d0efca491946a29ba7df8c5345264",
                "NVIDIA-RTX-SDKs",
                "Release",
                "test-compiler",
                Key(201));
            Contract = new OpacityMicromapBakeBridgeContract(
                1,
                provenance,
                16 * 1024 * 1024,
                16 * 1024 * 1024,
                1024);
        }

        public OpacityMicromapBakeBridgeContract Contract { get; }
        public int CallCount { get; private set; }

        public ValueTask<OpacityMicromapBakeResult> BakeAsync(
            OpacityMicromapBakeRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (!request.TryValidate(Contract, out OpacityMicromapBakeFailure failure,
                    out string detail))
            {
                return ValueTask.FromResult(
                    OpacityMicromapBakeResult.Rejected(failure, detail));
            }
            byte[] descriptor = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.AsSpan(4, 2),
                checked((ushort)request.RequestedSubdivisionLevel));
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(6, 2), 2);
            byte[] indexData = new byte[checked((int)request.PrimitiveCount * 4)];
            var payload = OpacityMicromapCookedPayload.Create(
                NvidiaOpacityMicromapCookPolicy.CurrentCookAbi,
                request.ContentKey,
                Contract.Provenance.ComputeFingerprint(),
                request.RequestedSubdivisionLevel,
                request.PrimitiveCount,
                1,
                [request.MaterialContract],
                [new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    request.RequestedSubdivisionLevel,
                    1)],
                [0],
                indexData,
                descriptor,
                new OpacityMicromapClassificationStatistics(1, 1, 1, 1));
            return ValueTask.FromResult(new OpacityMicromapBakeResult(
                true,
                payload,
                OpacityMicromapBakeFailure.None,
                "deterministic-test-bake"));
        }
    }
}
