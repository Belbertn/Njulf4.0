using System.Buffers.Binary;
using System.Reflection;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class CookedTextureAuthenticationTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfTests",
            nameof(CookedTextureAuthenticationTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Authenticate_ExactCookedKtxAndSiblingMetadata_ReturnsSourceStatistics()
    {
        CookedFixture fixture = Cook(
            "exact",
            CreateUniformBmp(32, 96, 224, 255));

        AuthenticatedCookedTexture authenticated =
            CookedTextureAuthentication.Authenticate(
                fixture.Ktx2Path,
                File.ReadAllBytes(fixture.Ktx2Path),
                CreateContract());

        Assert.Multiple(() =>
        {
            Assert.That(authenticated.Ktx2Path, Is.EqualTo(Path.GetFullPath(fixture.Ktx2Path)));
            Assert.That(authenticated.MetadataPath, Is.EqualTo(Path.GetFullPath(fixture.MetadataPath)));
            Assert.That(authenticated.Ktx2ContentHash, Is.EqualTo(fixture.Metadata.Ktx2ContentHash));
            Assert.That(authenticated.Metadata.TransportStatistics.IsValid, Is.True);
            Assert.That(
                authenticated.Metadata.TransportStatistics.SourceContentHash,
                Is.EqualTo(fixture.Metadata.SourceHash));
            Assert.That(
                authenticated.Metadata.TransportStatistics.SourceContentHash,
                Is.Not.EqualTo(authenticated.Ktx2ContentHash),
                "The authenticated transport hash must retain the raw authored source identity.");
            Assert.That(authenticated.PublicationContentHash, Is.Not.Zero);
        });
    }

    [Test]
    public void Authenticate_SubstitutedSameShapeKtx_FailsWholeFileIdentity()
    {
        CookedFixture original = Cook(
            "original",
            CreateUniformBmp(24, 80, 208, 255));
        CookedFixture substitute = Cook(
            "substitute",
            CreateUniformBmp(224, 48, 32, 255));
        byte[] substitutedBytes = File.ReadAllBytes(substitute.Ktx2Path);
        File.Copy(substitute.Ktx2Path, original.Ktx2Path, overwrite: true);

        CookedAssetHashException? failure =
            Assert.Throws<CookedAssetHashException>(
                () => CookedTextureAuthentication.Authenticate(
                    original.Ktx2Path,
                    substitutedBytes,
                    CreateContract()));

        Assert.That(failure!.Message, Does.Contain("whole-file hash"));
    }

    [Test]
    public void Authenticate_ContractMismatch_FailsClosedBeforeStatisticsPublication()
    {
        CookedFixture fixture = Cook(
            "contract",
            CreateUniformBmp(72, 144, 216, 255));
        byte[] ktx2 = File.ReadAllBytes(fixture.Ktx2Path);
        var differentSampler = TextureSamplerDescription.Default with
        {
            WrapU = TextureWrapMode.ClampToEdge
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                () => CookedTextureAuthentication.Authenticate(
                    fixture.Ktx2Path,
                    ktx2,
                    CreateContract() with { Semantic = TextureSemantic.Normal }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("semantic"));
            Assert.That(
                () => CookedTextureAuthentication.Authenticate(
                    fixture.Ktx2Path,
                    ktx2,
                    CreateContract() with { ColorSpace = TextureColorSpace.Linear }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("color space"));
            Assert.That(
                () => CookedTextureAuthentication.Authenticate(
                    fixture.Ktx2Path,
                    ktx2,
                    CreateContract() with { Sampler = differentSampler }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("sampler"));
            Assert.That(
                () => CookedTextureAuthentication.Authenticate(
                    fixture.Ktx2Path,
                    ktx2,
                    CreateContract() with
                    {
                        PreserveAlphaCoverage = true,
                        AlphaCoverageCutoff = 0.5f
                    }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("alpha-coverage"));
        });
    }

    [Test]
    public void Authenticate_MissingSiblingMetadata_IsRejected()
    {
        string ktx2Path = Path.Combine(_directory, "orphan.ktx2");
        File.WriteAllBytes(ktx2Path, [1, 2, 3, 4]);

        Assert.That(
            () => CookedTextureAuthentication.Authenticate(
                ktx2Path,
                File.ReadAllBytes(ktx2Path),
                CreateContract()),
            Throws.TypeOf<FileNotFoundException>().With.Message.Contains("sibling .njtex"));
    }

    [Test]
    [NonParallelizable]
    public void Authenticate_MetadataMutatedAfterSigning_RejectsExactSnapshot()
    {
        CookedFixture fixture = Cook(
            "signed-mutation",
            CreateUniformBmp(80, 160, 224, 255));
        string privateKey = Path.Combine(_directory, "private.pem");
        string publicKey = Path.Combine(_directory, "public.pem");
        CookedPackageSigner.GenerateKeyPair(privateKey, publicKey);
        _ = CookedPackageSigner.SignFile(fixture.MetadataPath, privateKey);
        _ = CookedPackageSigner.SignFile(fixture.Ktx2Path, privateKey);

        byte[] metadataBytes = File.ReadAllBytes(fixture.MetadataPath);
        metadataBytes[^1] ^= 0x01;
        File.WriteAllBytes(fixture.MetadataPath, metadataBytes);

        string? previousKey = Environment.GetEnvironmentVariable(
            CookedPackageSigner.PublicKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                CookedPackageSigner.PublicKeyEnvironmentVariable,
                publicKey);
            Assert.That(
                () => CookedTextureAuthentication.Authenticate(
                    fixture.Ktx2Path,
                    File.ReadAllBytes(fixture.Ktx2Path),
                    CreateContract(),
                    CookedAssetReaderFlags.RequireSignature),
                Throws.TypeOf<CookedAssetHashException>()
                    .With.Message.Contains("detached signature content hash"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CookedPackageSigner.PublicKeyEnvironmentVariable,
                previousKey);
        }
    }

    [Test]
    public void CookedSlot_EmptyOriginalCacheIdentity_UsesNormalizedMetadataIdentity()
    {
        var slot = new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                DebugName = "authored-empty-identity.png",
                CacheIdentity = string.Empty,
                Bytes = [1]
            }
        };
        MethodInfo cloneSlot = typeof(CookedPackage).GetMethod(
            "CloneSlot",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertionException("Cooked slot cloning seam was not found.");

        var cooked = (ModelTextureSlot?)cloneSlot.Invoke(
            null,
            [slot, "textures/authored.ktx2", "authored-empty-identity.png"]);

        Assert.Multiple(() =>
        {
            Assert.That(cooked, Is.Not.Null);
            Assert.That(
                cooked!.Source!.CacheIdentity,
                Is.EqualTo("cooked:authored-empty-identity.png"));
            Assert.That(cooked.Source.ContainerKind, Is.EqualTo(TextureContainerKind.Ktx2));
        });
    }

    private CookedFixture Cook(string stem, byte[] sourceBytes)
    {
        const string sourceIdentity = "smoke://authenticated-texture";
        string ktx2Path = Path.Combine(_directory, $"{stem}.ktx2");
        var source = new ModelTextureSource
        {
            DebugName = $"{stem}.bmp",
            SourceKind = TextureSourceKind.EmbeddedMemory,
            Bytes = sourceBytes,
            MimeType = "image/bmp",
            CacheIdentity = sourceIdentity,
            ContainerKind = TextureContainerKind.StandardImage,
            EncodedByteLength = sourceBytes.Length
        };
        CookedTextureReport report = new TextureCooker().Cook(
            source,
            ktx2Path,
            new TextureCookOptions(
                MaxDimension: 16,
                ColorSpace: TextureColorSpace.Srgb,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                Semantic: TextureSemantic.Color));
        var metadata = new CookedTextureMeta(
            CookedPackage.StableAssetId($"{sourceIdentity}|{stem}"),
            sourceIdentity,
            report.TransportStatistics.SourceContentHash,
            Path.GetFileName(ktx2Path),
            TextureColorSpace.Srgb,
            TextureSamplerDescription.Default,
            report.OriginalWidth,
            report.OriginalHeight,
            report.CookedWidth,
            report.CookedHeight,
            report.MipCount,
            report.VulkanFormat,
            report.CookedBytes)
        {
            Ktx2ContentHash = CookedHash.File(ktx2Path),
            Semantic = TextureSemantic.Color,
            TransportStatistics = report.TransportStatistics,
            AlphaCoveragePreserved = false,
            AlphaCoverageCutoff = null
        };
        string metadataPath = Path.ChangeExtension(ktx2Path, ".njtex");
        CookedPackage.WriteTextureMeta(metadataPath, metadata);
        return new CookedFixture(ktx2Path, metadataPath, metadata);
    }

    private static CookedTextureRuntimeContract CreateContract() => new(
        "smoke://authenticated-texture",
        TextureSemantic.Color,
        TextureColorSpace.Srgb,
        TextureSamplerDescription.Default,
        PreserveAlphaCoverage: false,
        AlphaCoverageCutoff: null);

    private static byte[] CreateUniformBmp(
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        const int width = 2;
        const int height = 2;
        const int headerBytes = 54;
        const int pixelBytes = width * height * 4;
        var bytes = new byte[headerBytes + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2, 4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10, 4), headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34, 4), pixelBytes);
        for (int offset = headerBytes; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = blue;
            bytes[offset + 1] = green;
            bytes[offset + 2] = red;
            bytes[offset + 3] = alpha;
        }

        return bytes;
    }

    private sealed record CookedFixture(
        string Ktx2Path,
        string MetadataPath,
        CookedTextureMeta Metadata);
}
