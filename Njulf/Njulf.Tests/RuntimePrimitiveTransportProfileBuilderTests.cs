using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RuntimePrimitiveTransportProfileBuilderTests
{
    [Test]
    public void UncookedSparseCorrelatedTextures_ProduceSpatialProfileAndHitContentCache()
    {
        byte[] sparse = EncodeRgba(
            [
                0, 0, 0, 0,
                255, 255, 255, 255
            ],
            width: 2,
            height: 1);
        ModelTextureSource source = CreateMemorySource("shared-sparse", sparse);
        ModelTextureSlot binding = CreateNearestBinding(source);
        var material = new ModelMaterial
        {
            Albedo = Vector4.One,
            Emissive = Vector4.One,
            EmissiveStrength = 1.0f,
            AlphaMode = ModelAlphaMode.Mask,
            AlphaCutoff = 0.5f,
            BaseColorTexture = binding,
            EmissiveTexture = binding
        };
        ModelSubMesh subMesh = CreateTwoTriangleUvFixture();
        var builder = new RuntimePrimitiveTransportProfileBuilder();

        RuntimePrimitiveTransportProfileBuildResult first =
            builder.Build([subMesh], [material]);
        RuntimePrimitiveTransportProfileBuildResult second =
            builder.Build([subMesh], [material]);
        GiPrimitiveTransportProfile profile = first.Profiles.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Validate(), Is.Empty);
            Assert.That(profile.IsComplete, Is.True);
            Assert.That(
                profile.Quality,
                Is.EqualTo(GiPrimitiveTransportProfileQuality.SurfaceQuadrature7));
            Assert.That(profile.AlphaCoverage, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(profile.MeanEmission.X, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(profile.MeanEmission.Y, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(profile.MeanEmission.Z, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(profile.EmissiveCandidateTriangleCount, Is.EqualTo(1));
            Assert.That(profile.EmissiveTriangles, Has.Length.EqualTo(1));
            Assert.That(profile.EmissiveTriangles[0].TriangleIndex, Is.EqualTo(1));
            Assert.That(profile.EmissiveTriangles[0].Coverage, Is.EqualTo(1.0));
            Assert.That(first.Diagnostics.CompleteProfileCount, Is.EqualTo(1));
            Assert.That(first.Diagnostics.ProfileCacheMissCount, Is.EqualTo(1));
            Assert.That(first.Diagnostics.TextureCacheMissCount, Is.EqualTo(1));
            Assert.That(first.Diagnostics.TextureCacheHitCount, Is.EqualTo(1));
            Assert.That(second.Diagnostics.ProfileCacheHitCount, Is.EqualTo(1));
            Assert.That(second.Diagnostics.ProfileCacheMissCount, Is.Zero);
            Assert.That(second.Diagnostics.TextureCacheHitCount, Is.EqualTo(2));
        });

        MeshManager.MeshRegistrationData registration = CreateRegistration(
            subMesh,
            profile);
        Assert.That(
            registration.PrimitiveTransportProfile!.EmissiveTriangles[0].TriangleIndex,
            Is.EqualTo(1));
    }

    [Test]
    public void ContentAndMaterialChanges_InvalidatePrimitiveProfileCacheDeterministically()
    {
        byte[] leftDark = EncodeRgba(
            [
                0, 0, 0, 0,
                255, 255, 255, 255
            ],
            2,
            1);
        byte[] rightDark = EncodeRgba(
            [
                255, 255, 255, 255,
                0, 0, 0, 0
            ],
            2,
            1);
        ModelSubMesh subMesh = CreateTwoTriangleUvFixture();
        var builder = new RuntimePrimitiveTransportProfileBuilder();
        ModelMaterial firstMaterial = CreateSparseMaterial(leftDark, strength: 1.0f);
        ModelMaterial changedTexture = CreateSparseMaterial(rightDark, strength: 1.0f);
        ModelMaterial changedFactor = CreateSparseMaterial(rightDark, strength: 4.0f);

        RuntimePrimitiveTransportProfileBuildResult first =
            builder.Build([subMesh], [firstMaterial]);
        RuntimePrimitiveTransportProfileBuildResult textureChanged =
            builder.Build([subMesh], [changedTexture]);
        RuntimePrimitiveTransportProfileBuildResult factorChanged =
            builder.Build([subMesh], [changedFactor]);

        Assert.Multiple(() =>
        {
            Assert.That(textureChanged.Diagnostics.ProfileCacheMissCount, Is.EqualTo(1));
            Assert.That(factorChanged.Diagnostics.ProfileCacheMissCount, Is.EqualTo(1));
            Assert.That(
                first.Profiles[0].TextureSourceHashes[0],
                Is.Not.EqualTo(textureChanged.Profiles[0].TextureSourceHashes[0]));
            Assert.That(
                first.Profiles[0].InputHash,
                Is.Not.EqualTo(textureChanged.Profiles[0].InputHash));
            Assert.That(
                textureChanged.Profiles[0].InputHash,
                Is.Not.EqualTo(factorChanged.Profiles[0].InputHash));
            Assert.That(first.Profiles[0].EmissiveTriangles[0].TriangleIndex, Is.EqualTo(1));
            Assert.That(textureChanged.Profiles[0].EmissiveTriangles[0].TriangleIndex, Is.EqualTo(0));
            Assert.That(
                factorChanged.Profiles[0].MeanEmission.X,
                Is.EqualTo(textureChanged.Profiles[0].MeanEmission.X * 4.0).Within(1e-12));
        });
    }

    [Test]
    public void UnknownPrecompressedSource_IsHashAuthenticatedButFailsClosed()
    {
        byte[] malformed = [0xAB, 0x4B, 0x54, 0x58, 1, 2, 3, 4, 5, 6, 7, 8];
        byte[] changed = [0xAB, 0x4B, 0x54, 0x58, 8, 7, 6, 5, 4, 3, 2, 1];
        ModelSubMesh subMesh = CreateTwoTriangleUvFixture();
        var builder = new RuntimePrimitiveTransportProfileBuilder();

        RuntimePrimitiveTransportProfileBuildResult first =
            builder.Build([subMesh], [CreateMalformedMaterial(malformed)]);
        RuntimePrimitiveTransportProfileBuildResult second =
            builder.Build([subMesh], [CreateMalformedMaterial(changed)]);
        GiPrimitiveTransportProfile profile = first.Profiles.Single();

        Assert.Multiple(() =>
        {
            Assert.That(profile.Validate(), Is.Empty);
            Assert.That(profile.IsComplete, Is.False);
            Assert.That(profile.Quality, Is.EqualTo(GiPrimitiveTransportProfileQuality.Invalid));
            Assert.That(
                profile.Validity.HasFlag(
                    GiPrimitiveTransportProfileValidity.TextureSamplingComplete),
                Is.False);
            Assert.That(profile.EmissiveTriangles, Is.Empty);
            Assert.That(profile.TextureSourceHashes[0], Is.EqualTo(CookedHash.Bytes(malformed)));
            Assert.That(profile.InvalidReason, Does.Contain("failed closed"));
            Assert.That(first.Diagnostics.InvalidProfileCount, Is.EqualTo(1));
            Assert.That(first.Diagnostics.TextureAnalysisFailureCount, Is.EqualTo(1));
            Assert.That(first.Diagnostics.Summary, Does.Contain("KTX2"));
            Assert.That(second.Diagnostics.ProfileCacheMissCount, Is.EqualTo(1));
            Assert.That(second.Profiles[0].InputHash, Is.Not.EqualTo(profile.InputHash));
            Assert.That(
                ModelRenderUploadService.ConvertPrimitiveTransportProfile(profile),
                Is.SameAs(GiMaterialTransportProfile.Invalid));
        });
    }

    [Test]
    public void FileSourceReader_RejectsOversizedSparseFileWithoutReadingIt()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"njulf-runtime-transport-{Guid.NewGuid():N}.bin");
        try
        {
            using (FileStream stream = File.Create(path))
            {
                stream.SetLength(
                    TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes +
                    1L);
            }
            var source = new ModelTextureSource
            {
                DebugName = "oversized-runtime-source.bin",
                CacheIdentity = path,
                FilePath = path,
                SourceKind = TextureSourceKind.ExternalFile,
                ContainerKind = TextureContainerKind.StandardImage
            };

            bool read = RuntimePrimitiveTransportProfileBuilder.TryReadSourceBytes(
                source,
                out byte[] encoded,
                out string? failure);

            Assert.Multiple(() =>
            {
                Assert.That(read, Is.False);
                Assert.That(encoded, Is.Empty);
                Assert.That(failure, Does.Contain("hard limit").Or.Contain("expected a size"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LegacyPathOnlyBinding_IsNormalizedWithoutMutatingImportedMaterial()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfRuntimePrimitiveProfiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy-base.png");
        byte[] png = EncodeRgba([64, 128, 255, 255], 1, 1);
        File.WriteAllBytes(path, png);
        try
        {
            var material = new ModelMaterial
            {
                AlbedoTexturePath = path,
                Albedo = Vector4.One
            };
            var builder = new RuntimePrimitiveTransportProfileBuilder();

            RuntimePrimitiveTransportProfileBuildResult result =
                builder.Build([CreateTwoTriangleUvFixture()], [material]);
            GiPrimitiveTransportProfile profile = result.Profiles.Single();

            Assert.Multiple(() =>
            {
                Assert.That(profile.Validate(), Is.Empty);
                Assert.That(profile.IsComplete, Is.True);
                Assert.That(profile.BaseColorSamplingBinding.IsBound, Is.True);
                Assert.That(
                    profile.TextureSourceHashes[0],
                    Is.EqualTo(CookedHash.Bytes(png)));
                Assert.That(material.BaseColorTexture, Is.Null);
                Assert.That(material.AlbedoTexturePath, Is.EqualTo(path));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void OversizedRuntimeGeometry_IsRejectedWithoutTextureOrTriangleScan()
    {
        int triangleCount =
            RuntimePrimitiveTransportProfileBuilder.MaximumProfileTrianglesPerModel + 1;
        var indices = new uint[checked(triangleCount * 3)];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = (uint)(i % 3);
        var subMesh = new ModelSubMesh
        {
            Name = "over-budget",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            ],
            Indices = indices
        };
        var builder = new RuntimePrimitiveTransportProfileBuilder();

        RuntimePrimitiveTransportProfileBuildResult result =
            builder.Build([subMesh], [ModelMaterial.Default]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Profiles[0].Validate(), Is.Empty);
            Assert.That(
                result.Profiles[0].Quality,
                Is.EqualTo(GiPrimitiveTransportProfileQuality.Invalid));
            Assert.That(result.Profiles[0].TriangleCount, Is.Zero);
            Assert.That(
                result.Profiles[0].EmissiveSourceTriangleCount,
                Is.EqualTo(triangleCount));
            Assert.That(result.Diagnostics.ProfileCacheHitCount, Is.Zero);
            Assert.That(result.Diagnostics.ProfileCacheMissCount, Is.Zero);
            Assert.That(result.Diagnostics.TextureCacheMissCount, Is.Zero);
            Assert.That(result.Diagnostics.Summary, Does.Contain("hard per-model limit"));
        });
    }

    private static ModelMaterial CreateSparseMaterial(byte[] png, float strength)
    {
        ModelTextureSource source = CreateMemorySource("stable-texture-identity", png);
        ModelTextureSlot binding = CreateNearestBinding(source);
        return new ModelMaterial
        {
            Albedo = Vector4.One,
            Emissive = Vector4.One,
            EmissiveStrength = strength,
            AlphaMode = ModelAlphaMode.Mask,
            AlphaCutoff = 0.5f,
            BaseColorTexture = binding,
            EmissiveTexture = binding
        };
    }

    private static ModelMaterial CreateMalformedMaterial(byte[] bytes) => new()
    {
        BaseColorTexture = new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                DebugName = "unknown-precompressed.ktx2",
                CacheIdentity = "stable-malformed-identity",
                SourceKind = TextureSourceKind.EmbeddedMemory,
                ContainerKind = TextureContainerKind.Ktx2,
                Bytes = bytes,
                EncodedByteLength = bytes.Length
            },
            ColorSpace = TextureColorSpace.Srgb
        }
    };

    private static ModelTextureSource CreateMemorySource(
        string identity,
        byte[] bytes) => new()
        {
            DebugName = identity + ".png",
            CacheIdentity = identity,
            SourceKind = TextureSourceKind.EmbeddedMemory,
            ContainerKind = TextureContainerKind.StandardImage,
            Bytes = bytes,
            EncodedByteLength = bytes.Length
        };

    private static ModelTextureSlot CreateNearestBinding(
        ModelTextureSource source) => new()
        {
            Source = source,
            ColorSpace = TextureColorSpace.Srgb,
            Sampler = new TextureSamplerDescription(
            TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge,
            TextureFilterMode.Nearest,
            TextureFilterMode.Nearest,
            TextureMipFilterMode.Nearest,
            1.0f)
        };

    private static ModelSubMesh CreateTwoTriangleUvFixture() => new()
    {
        Name = "sparse-correlated",
        MaterialIndex = 0,
        Vertices =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(2f, 0f, 0f),
            new Vector3(3f, 0f, 0f),
            new Vector3(2f, 1f, 0f)
        ],
        TexCoords =
        [
            new Vector2(0.25f, 0.5f),
            new Vector2(0.25f, 0.5f),
            new Vector2(0.25f, 0.5f),
            new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f)
        ],
        Indices = [0, 1, 2, 3, 4, 5]
    };

    private static MeshManager.MeshRegistrationData CreateRegistration(
        ModelSubMesh subMesh,
        GiPrimitiveTransportProfile profile)
    {
        var vertices = new GPUVertex[subMesh.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new GPUVertex
            {
                Position = subMesh.Vertices[i],
                Normal = Vector3.UnitZ,
                Tangent = new Vector4(1f, 0f, 0f, 1f),
                TexCoord = subMesh.TexCoords[i],
                Color = Vector4.One
            };
        }
        return new MeshManager.MeshRegistrationData(
            vertices,
            subMesh.Indices,
            primitiveTransportProfile: profile);
    }

    private static byte[] EncodeRgba(
        ReadOnlySpan<byte> rgba,
        int width,
        int height) =>
        PngScreenshotEncoder.Encode(
            rgba,
            width,
            height,
            ScreenshotPixelFormat.Rgba8);
}
