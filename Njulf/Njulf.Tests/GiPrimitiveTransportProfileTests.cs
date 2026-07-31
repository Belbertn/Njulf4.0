using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class GiPrimitiveTransportProfileTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NjulfPrimitiveTransportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Generator_UsesSurfaceAreaAndVertexColorDeterministically()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "area-weighted",
            MaterialIndex = 3,
            Vertices =
            [
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 2, 0)
            ],
            VertexColors =
            [
                new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
                new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1)
            ],
            Indices = [0, 1, 2, 3, 4, 5]
        };
        var material = new ModelMaterial
        {
            Albedo = Vector4.One,
            Metallic = 0f,
            Roughness = 0.75f,
            AmbientOcclusion = 0.8f,
            AlphaCutoff = 0.5f
        };

        GiPrimitiveTransportProfile first =
            GiPrimitiveTransportProfileGenerator.Generate(7, subMesh, material);
        GiPrimitiveTransportProfile second =
            GiPrimitiveTransportProfileGenerator.Generate(7, subMesh, material);

        Assert.Multiple(() =>
        {
            Assert.That(first.SubMeshIndex, Is.EqualTo(7));
            Assert.That(first.MaterialSlot, Is.EqualTo(3));
            Assert.That(first.SurfaceArea, Is.EqualTo(2.5).Within(1e-12));
            Assert.That(first.TriangleCount, Is.EqualTo(2));
            Assert.That(first.SampleCount, Is.EqualTo(2 * GiPrimitiveTransportProfile.SamplesPerTriangle));
            Assert.That(
                first.MeanDiffuseReflectance.X,
                Is.EqualTo(0.2 * (20.0 / 21.0) * 0.96 * 0.96).Within(1e-12));
            Assert.That(first.MeanDiffuseReflectance.Y, Is.Zero.Within(1e-12));
            Assert.That(
                first.MeanDiffuseReflectance.Z,
                Is.EqualTo(0.8 * (20.0 / 21.0) * 0.96 * 0.96).Within(1e-12));
            Assert.That(first.MeanAmbientOcclusion, Is.EqualTo(1.0).Within(1e-7));
            Assert.That(first.MeanRoughness, Is.EqualTo(0.75).Within(1e-12));
            Assert.That(first.AlphaCoverage, Is.EqualTo(1.0));
            Assert.That(first.Validity.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete), Is.True);
            Assert.That(first.Quality, Is.EqualTo(GiPrimitiveTransportProfileQuality.FactorAndVertexColor));
            Assert.That(first.InputHash, Is.EqualTo(second.InputHash));
            Assert.That(first.MeanDiffuseReflectance, Is.EqualTo(second.MeanDiffuseReflectance));
            Assert.That(first.EstimatedIntegrationError, Is.EqualTo(second.EstimatedIntegrationError));
            Assert.That(first.TextureSourceHashes, Is.EqualTo(second.TextureSourceHashes));
        });
    }

    [Test]
    public void Generator_CanonicalizesEmissiveFactorRoundingOutsideUnitRange()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "emissive-rounding",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0)
            ],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            Emissive = new Vector4(1f, -0.0000001f, 1.00000012f, 1f)
        };

        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(
                0,
                subMesh,
                material);

        Assert.Multiple(() =>
        {
            Assert.That(profile.CookedEmissiveFactor.X, Is.EqualTo(1.0));
            Assert.That(profile.CookedEmissiveFactor.Y, Is.Zero);
            Assert.That(profile.CookedEmissiveFactor.Z, Is.EqualTo(1.0));
            Assert.That(profile.Validate(), Is.Empty);
        });
    }

    [Test]
    public void Generator_AppliesStandaloneIorWithoutTransmissionAndHashesTheAuthoredValue()
    {
        const uint iorFeature = 1u << 24;
        var subMesh = new ModelSubMesh
        {
            Name = "standalone-ior",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0)
            ],
            Indices = [0, 1, 2]
        };
        var lowIor = new ModelMaterial
        {
            FeatureFlags = iorFeature,
            Albedo = Vector4.One,
            Ior = 1f,
            TransmissionFactor = 0f
        };
        var highIor = new ModelMaterial
        {
            FeatureFlags = iorFeature,
            Albedo = Vector4.One,
            Ior = 3f,
            TransmissionFactor = 0f
        };

        GiPrimitiveTransportProfile low =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, lowIor);
        GiPrimitiveTransportProfile high =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, highIor);

        Assert.Multiple(() =>
        {
            Assert.That(low.MeanDiffuseReflectance.X, Is.EqualTo(20.0 / 21.0).Within(1e-12));
            Assert.That(low.MeanDiffuseReflectance.Y, Is.EqualTo(20.0 / 21.0).Within(1e-12));
            Assert.That(low.MeanDiffuseReflectance.Z, Is.EqualTo(20.0 / 21.0).Within(1e-12));
            Assert.That(high.MeanDiffuseReflectance.X, Is.EqualTo(15.0 / 28.0).Within(1e-12));
            Assert.That(high.MeanDiffuseReflectance.Y, Is.EqualTo(15.0 / 28.0).Within(1e-12));
            Assert.That(high.MeanDiffuseReflectance.Z, Is.EqualTo(15.0 / 28.0).Within(1e-12));
            Assert.That(high.InputHash, Is.Not.EqualTo(low.InputHash));
            Assert.That(highIor.FeatureFlags & (1u << 9), Is.Zero);
            Assert.That(highIor.TransmissionFactor, Is.Zero);
            Assert.That(GiPrimitiveTransportProfile.CurrentAlgorithmVersion, Is.EqualTo(5u));
            Assert.That(high.AlgorithmVersion, Is.EqualTo(5u));
        });
    }

    [Test]
    public void Generator_UsesGltfOcclusionStrengthEquationAndMissingTextureIsNeutral()
    {
        TextureTransportImage occlusion = TextureTransportImage.FromRgba8(
            [64, 64, 64, 255],
            1,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Data,
            0x45);
        var source = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "occlusion",
            DebugName = "occlusion"
        };
        var subMesh = new ModelSubMesh
        {
            Name = "occlusion-strength",
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            AmbientOcclusion = 0.4f,
            OcclusionTexture = CreateBinding(source, TextureWrapMode.ClampToEdge, Vector2.Zero)
        };

        GiPrimitiveTransportProfile sampled = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            material,
            new GiPrimitiveTextureInputs(Occlusion: occlusion));
        GiPrimitiveTransportProfile missing = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial { AmbientOcclusion = 0.4f });

        double expected = 1.0 + 0.4 * (64.0 / 255.0 - 1.0);
        Assert.Multiple(() =>
        {
            Assert.That(sampled.MeanAmbientOcclusion, Is.EqualTo(expected).Within(1e-7));
            Assert.That(missing.MeanAmbientOcclusion, Is.EqualTo(1.0).Within(1e-7));
        });
    }

    [Test]
    public void Generator_AppliesUvTransformAndSamplerWrapping()
    {
        TextureTransportImage image = TextureTransportImage.FromRgba8(
            [
                255, 0, 0, 255,
                0, 0, 255, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            0x44);
        var source = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "two-pixels",
            DebugName = "two-pixels"
        };
        var subMesh = new ModelSubMesh
        {
            Name = "uv",
            MaterialIndex = 0,
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            TexCoords = [new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f)],
            Indices = [0, 1, 2]
        };
        ModelTextureSlot repeatHalfOffset = CreateBinding(
            source,
            TextureWrapMode.Repeat,
            new Vector2(0.5f, 0f));
        ModelTextureSlot repeatWholeOffset = CreateBinding(
            source,
            TextureWrapMode.Repeat,
            new Vector2(1f, 0f));
        ModelTextureSlot clampWholeOffset = CreateBinding(
            source,
            TextureWrapMode.ClampToEdge,
            new Vector2(1f, 0f));

        GiPrimitiveTransportProfile blue = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial { BaseColorTexture = repeatHalfOffset },
            new GiPrimitiveTextureInputs(BaseColor: image));
        GiPrimitiveTransportProfile repeatedRed = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial { BaseColorTexture = repeatWholeOffset },
            new GiPrimitiveTextureInputs(BaseColor: image));
        GiPrimitiveTransportProfile clampedBlue = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial { BaseColorTexture = clampWholeOffset },
            new GiPrimitiveTextureInputs(BaseColor: image));

        Assert.Multiple(() =>
        {
            Assert.That(blue.MeanDiffuseReflectance.X, Is.Zero.Within(1e-12));
            Assert.That(
                blue.MeanDiffuseReflectance.Z,
                Is.EqualTo((20.0 / 21.0) * 0.96 * 0.96).Within(1e-12));
            Assert.That(
                repeatedRed.MeanDiffuseReflectance.X,
                Is.EqualTo((20.0 / 21.0) * 0.96 * 0.96).Within(1e-12));
            Assert.That(repeatedRed.MeanDiffuseReflectance.Z, Is.Zero.Within(1e-12));
            Assert.That(clampedBlue.MeanDiffuseReflectance.X, Is.Zero.Within(1e-12));
            Assert.That(
                clampedBlue.MeanDiffuseReflectance.Z,
                Is.EqualTo((20.0 / 21.0) * 0.96 * 0.96).Within(1e-12));
        });
    }

    [Test]
    public void Generator_AppliesExtensionTexturesPerSampleAndHashesEveryTransportInput()
    {
        const uint clearcoat = 1u << 0;
        const uint clearcoatTexture = 1u << 1;
        const uint sheen = 1u << 4;
        const uint sheenColorTexture = 1u << 5;
        const uint transmission = 1u << 9;
        const uint transmissionTexture = 1u << 10;
        const uint specular = 1u << 15;
        const uint specularTexture = 1u << 16;
        const uint specularColorTexture = 1u << 17;
        var source = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "extensions",
            DebugName = "extensions"
        };
        ModelTextureSlot binding = CreateBinding(source, TextureWrapMode.ClampToEdge, Vector2.Zero);
        var subMesh = new ModelSubMesh
        {
            Name = "extension-energy",
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            FeatureFlags = clearcoat | clearcoatTexture |
                           sheen | sheenColorTexture |
                           transmission | transmissionTexture |
                           specular | specularTexture | specularColorTexture,
            Albedo = new Vector4(0.8f, 0.6f, 0.4f, 1f),
            Metallic = 0.25f,
            ClearcoatFactor = 0.5f,
            ClearcoatTexture = binding,
            SheenColor = new Vector4(0.5f, 0.25f, 0.75f, 1f),
            SheenColorTexture = binding,
            TransmissionFactor = 0.4f,
            Ior = 2f,
            TransmissionTexture = binding,
            SpecularFactor = 0.75f,
            SpecularTexture = binding,
            SpecularColor = new Vector4(0.8f, 0.6f, 0.4f, 1f),
            SpecularColorTexture = binding
        };
        TextureTransportImage clearcoatImage = CreatePixel(128, 255, 255, 255, 0x61);
        TextureTransportImage sheenImage = CreatePixel(64, 128, 192, 255, 0x62);
        TextureTransportImage transmissionImage = CreatePixel(102, 255, 255, 255, 0x63);
        TextureTransportImage specularImage = CreatePixel(255, 255, 255, 153, 0x64);
        TextureTransportImage specularColorImage = CreatePixel(204, 128, 64, 255, 0x65);
        var inputs = new GiPrimitiveTextureInputs(
            Clearcoat: clearcoatImage,
            SheenColor: sheenImage,
            Transmission: transmissionImage,
            Specular: specularImage,
            SpecularColor: specularColorImage);

        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material, inputs);

        double clearcoatValue = 0.5 * (128.0 / 255.0);
        double transmissionValue = 0.4 * (102.0 / 255.0);
        double specularValue = 0.75 * (153.0 / 255.0);
        double f0 = (1.0 / 3.0) * (1.0 / 3.0) * specularValue;
        double common = (1.0 - 0.25) * (1.0 - transmissionValue) * (1.0 - clearcoatValue * 0.04);
        double f0R = f0 * 0.8 * (204.0 / 255.0);
        double f0G = f0 * 0.6 * (128.0 / 255.0);
        double f0B = f0 * 0.4 * (64.0 / 255.0);
        double expectedR = 0.8 * common * (20.0 / 21.0) * Math.Pow(1.0 - f0R, 2.0) *
                           (1.0 - 0.5 * (64.0 / 255.0));
        double expectedG = 0.6 * common * (20.0 / 21.0) * Math.Pow(1.0 - f0G, 2.0) *
                           (1.0 - 0.25 * (128.0 / 255.0));
        double expectedB = 0.4 * common * (20.0 / 21.0) * Math.Pow(1.0 - f0B, 2.0) *
                           (1.0 - 0.75 * (192.0 / 255.0));

        material.SpecularFactor = 0.5f;
        GiPrimitiveTransportProfile changed =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material, inputs);
        material.Unlit = true;
        GiPrimitiveTransportProfile unlit =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material, inputs);

        Assert.Multiple(() =>
        {
            Assert.That(profile.MeanDiffuseReflectance.X, Is.EqualTo(expectedR).Within(1e-7));
            Assert.That(profile.MeanDiffuseReflectance.Y, Is.EqualTo(expectedG).Within(1e-7));
            Assert.That(profile.MeanDiffuseReflectance.Z, Is.EqualTo(expectedB).Within(1e-7));
            Assert.That(profile.TextureSourceHashes, Has.Length.EqualTo(10));
            Assert.That(profile.TextureSourceHashes[5..], Is.EqualTo(new ulong[] { 0x61, 0x62, 0x63, 0x64, 0x65 }));
            Assert.That(changed.InputHash, Is.Not.EqualTo(profile.InputHash));
            Assert.That(unlit.InputHash, Is.Not.EqualTo(changed.InputHash));
            Assert.That(unlit.MeanDiffuseReflectance.X, Is.Zero);
            Assert.That(unlit.MeanDiffuseReflectance.Y, Is.Zero);
            Assert.That(unlit.MeanDiffuseReflectance.Z, Is.Zero);
            Assert.That(unlit.MeanEmission.X, Is.Zero);
        });
    }

    [Test]
    public void Generator_PreservesCorrelatedBaseAndMetallicSamples()
    {
        TextureTransportImage baseColor = TextureTransportImage.FromRgba8(
            [
                255, 255, 255, 255,
                0, 0, 0, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            1);
        TextureTransportImage metallicRoughness = TextureTransportImage.FromRgba8(
            [
                255, 255, 255, 255,
                255, 255, 0, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Data,
            2);
        var source = new ModelTextureSource { Bytes = [1], CacheIdentity = "texture", DebugName = "texture" };
        ModelTextureSlot binding = CreateBinding(source, TextureWrapMode.ClampToEdge, Vector2.Zero);
        var subMesh = new ModelSubMesh
        {
            Name = "correlated",
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            TexCoords = [new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f)],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            BaseColorTexture = binding,
            MetallicRoughnessTexture = binding,
            Metallic = 1f
        };
        GiPrimitiveTransportProfile metallicWhite = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            material,
            new GiPrimitiveTextureInputs(BaseColor: baseColor, MetallicRoughness: metallicRoughness));

        subMesh.TexCoords =
        [
            new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f),
            new Vector2(0.75f, 0.5f)
        ];
        GiPrimitiveTransportProfile dielectricBlack = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            material,
            new GiPrimitiveTextureInputs(BaseColor: baseColor, MetallicRoughness: metallicRoughness));

        Assert.Multiple(() =>
        {
            Assert.That(metallicWhite.MeanMetallic, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(metallicWhite.MeanDiffuseReflectance.X, Is.Zero.Within(1e-12));
            Assert.That(dielectricBlack.MeanMetallic, Is.Zero.Within(1e-12));
            Assert.That(dielectricBlack.MeanDiffuseReflectance.X, Is.Zero.Within(1e-12));
        });
    }

    [Test]
    public void Generator_UsesAlphaModeCoverageSemantics()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "alpha-modes",
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            Indices = [0, 1, 2]
        };
        GiPrimitiveTransportProfile opaque = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial
            {
                Albedo = new Vector4(1f, 1f, 1f, 0.25f),
                AlphaMode = ModelAlphaMode.Opaque,
                AlphaCutoff = 2f
            });
        GiPrimitiveTransportProfile masked = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial
            {
                Albedo = new Vector4(1f, 1f, 1f, 0.25f),
                AlphaMode = ModelAlphaMode.Mask,
                AlphaCutoff = 0.5f
            });
        GiPrimitiveTransportProfile blended = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial
            {
                Albedo = new Vector4(1f, 1f, 1f, 1.5f),
                AlphaMode = ModelAlphaMode.Blend
            });

        Assert.Multiple(() =>
        {
            Assert.That(opaque.AlphaCoverage, Is.EqualTo(1.0));
            Assert.That(opaque.Validity.HasFlag(GiPrimitiveTransportProfileValidity.AlphaCoverage), Is.True);
            Assert.That(masked.AlphaCoverage, Is.Zero);
            Assert.That(masked.Validity.HasFlag(GiPrimitiveTransportProfileValidity.AlphaCoverage), Is.True);
            Assert.That(blended.AlphaCoverage, Is.EqualTo(1.0));
            Assert.That(blended.Validity.HasFlag(GiPrimitiveTransportProfileValidity.AlphaCoverage), Is.True);
            Assert.That(opaque.InputHash, Is.Not.EqualTo(masked.InputHash));
            Assert.That(masked.InputHash, Is.Not.EqualTo(blended.InputHash));
            Assert.That(
                () => GiPrimitiveTransportProfileGenerator.Generate(
                    0,
                    subMesh,
                    new ModelMaterial { AlphaCutoff = -0.01f }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => GiPrimitiveTransportProfileGenerator.Generate(
                    0,
                    subMesh,
                    new ModelMaterial { AlphaCutoff = float.NaN }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void Generator_MissingBoundTextureIsExplicitlyPartial()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "missing",
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            EmissiveTexture = new ModelTextureSlot
            {
                Source = new ModelTextureSource { Bytes = [1], CacheIdentity = "missing", DebugName = "missing" }
            }
        };

        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Quality, Is.EqualTo(GiPrimitiveTransportProfileQuality.PartialTextureData));
            Assert.That(profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.TextureSamplingComplete), Is.False);
            Assert.That(profile.Validity.HasFlag(GiPrimitiveTransportProfileValidity.Emission), Is.False);
            Assert.That(profile.InvalidReason, Does.Contain("unavailable"));
        });
    }

    [Test]
    public void Generator_RetainsOnlySpatiallyEmissiveTrianglesWithoutPrimitiveWideSmearing()
    {
        var source = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "emissive-black-white",
            DebugName = "emissive-black-white"
        };
        TextureTransportImage emissive = TextureTransportImage.FromRgba8(
            [
                0, 0, 0, 255,
                255, 255, 255, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Data,
            0x801);
        ModelTextureSlot binding = CreateBinding(
            source,
            TextureWrapMode.ClampToEdge,
            Vector2.Zero);
        ModelSubMesh subMesh = CreateTwoTriangleUvMesh();
        var material = new ModelMaterial
        {
            Emissive = Vector4.One,
            EmissiveTexture = binding
        };

        GiPrimitiveTransportProfile first = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            material,
            new GiPrimitiveTextureInputs(Emissive: emissive));
        GiPrimitiveTransportProfile second = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            material,
            new GiPrimitiveTextureInputs(Emissive: emissive));

        Assert.Multiple(() =>
        {
            Assert.That(first.EmissiveSourceTriangleCount, Is.EqualTo(2));
            Assert.That(first.EmissiveCandidateTriangleCount, Is.EqualTo(1));
            Assert.That(first.EmissiveTriangles, Has.Length.EqualTo(1));
            Assert.That(first.EmissiveTriangles[0].TriangleIndex, Is.EqualTo(1));
            Assert.That(first.EmissiveTriangles[0].LocalSurfaceArea, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(first.EmissiveTriangles[0].Coverage, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(first.EmissiveTriangles[0].CoveredMeanEmissiveTexture.X, Is.EqualTo(1.0));
            Assert.That(first.EmissiveOmittedCookedImportance, Is.Zero);
            Assert.That(first.EmissiveTriangles, Is.EqualTo(second.EmissiveTriangles));
            Assert.That(first.Validate(), Is.Empty);
        });
    }

    [Test]
    public void Generator_PreservesPerTriangleEmissionAndAlphaCorrelation()
    {
        var emissiveSource = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "emissive-white",
            DebugName = "emissive-white"
        };
        var baseSource = new ModelTextureSource
        {
            Bytes = [2],
            CacheIdentity = "alpha-black-white",
            DebugName = "alpha-black-white"
        };
        TextureTransportImage emissive = TextureTransportImage.FromRgba8(
            [
                255, 255, 255, 255,
                255, 255, 255, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Data,
            0x802);
        TextureTransportImage baseColor = TextureTransportImage.FromRgba8(
            [
                255, 255, 255, 0,
                255, 255, 255, 255
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            0x803);
        var material = new ModelMaterial
        {
            Albedo = Vector4.One,
            Emissive = Vector4.One,
            AlphaMode = ModelAlphaMode.Mask,
            AlphaCutoff = 0.5f,
            BaseColorTexture = CreateBinding(
                baseSource,
                TextureWrapMode.ClampToEdge,
                Vector2.Zero),
            EmissiveTexture = CreateBinding(
                emissiveSource,
                TextureWrapMode.ClampToEdge,
                Vector2.Zero)
        };

        GiPrimitiveTransportProfile profile = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            CreateTwoTriangleUvMesh(),
            material,
            new GiPrimitiveTextureInputs(
                BaseColor: baseColor,
                Emissive: emissive));

        Assert.Multiple(() =>
        {
            Assert.That(profile.AlphaCoverage, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(profile.EmissiveCandidateTriangleCount, Is.EqualTo(1));
            Assert.That(profile.EmissiveTriangles, Has.Length.EqualTo(1));
            Assert.That(profile.EmissiveTriangles[0].TriangleIndex, Is.EqualTo(1));
            Assert.That(profile.EmissiveTriangles[0].Coverage, Is.EqualTo(1.0).Within(1e-12));
            Assert.That(profile.EmissiveTotalCookedImportance, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(profile.Validate(), Is.Empty);
        });
    }

    [Test]
    public void Generator_EnforcesPrimitiveRecordCapAndConservesOmittedImportance()
    {
        const int triangleCount =
            GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPrimitive + 1;
        var vertices = new Vector3[triangleCount * 3];
        var indices = new uint[triangleCount * 3];
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int vertex = triangle * 3;
            float x = triangle * 2.0f;
            vertices[vertex] = new Vector3(x, 0, 0);
            vertices[vertex + 1] = new Vector3(x + 1, 0, 0);
            vertices[vertex + 2] = new Vector3(x, 1, 0);
            indices[vertex] = (uint)vertex;
            indices[vertex + 1] = (uint)(vertex + 1);
            indices[vertex + 2] = (uint)(vertex + 2);
        }
        var subMesh = new ModelSubMesh
        {
            Name = "primitive-cap",
            Vertices = vertices,
            Indices = indices
        };
        var material = new ModelMaterial { Emissive = Vector4.One };

        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material);

        Assert.Multiple(() =>
        {
            Assert.That(profile.EmissiveCandidateTriangleCount, Is.EqualTo(triangleCount));
            Assert.That(
                profile.EmissiveTriangles,
                Has.Length.EqualTo(GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPrimitive));
            Assert.That(
                profile.EmissiveTriangleFlags.HasFlag(
                    GiPrimitiveEmissiveTriangleFlags.PrimitiveRecordCapTruncated),
                Is.True);
            Assert.That(profile.EmissiveOmittedCookedImportance, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(
                profile.EmissiveRetainedCookedImportance + profile.EmissiveOmittedCookedImportance,
                Is.EqualTo(profile.EmissiveTotalCookedImportance).Within(1e-9));
            Assert.That(profile.Validate(), Is.Empty);
        });
    }

    [Test]
    public void PackageBudget_IsDeterministicAndMarksEveryPackageOmission()
    {
        ModelSubMesh subMesh = CreateTwoTriangleUvMesh();
        subMesh.Indices = [0, 1, 2];
        var material = new ModelMaterial { Emissive = Vector4.One };
        GiPrimitiveTransportProfile first =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material);
        GiPrimitiveTransportProfile second =
            GiPrimitiveTransportProfileGenerator.Generate(1, subMesh, material);

        IReadOnlyList<GiPrimitiveTransportProfile> bounded =
            GiPrimitiveTransportProfileGenerator.ApplyPackageEmissiveRecordBudget(
                [first, second],
                maximumRecords: 1);

        Assert.Multiple(() =>
        {
            Assert.That(bounded.Sum(static profile => profile.EmissiveTriangles.Length), Is.EqualTo(1));
            Assert.That(bounded[0].EmissiveTriangles, Has.Length.EqualTo(1));
            Assert.That(bounded[1].EmissiveTriangles, Is.Empty);
            Assert.That(
                bounded[1].EmissiveTriangleFlags.HasFlag(
                    GiPrimitiveEmissiveTriangleFlags.PackageRecordCapTruncated),
                Is.True);
            Assert.That(bounded[1].EmissiveOmittedCookedImportance, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(bounded.SelectMany(static profile => profile.Validate()), Is.Empty);
        });
    }

    [Test]
    public void ProfileValidation_RejectsMalformedTriangleIndexAndImportance()
    {
        ModelSubMesh subMesh = CreateTwoTriangleUvMesh();
        subMesh.Indices = [0, 1, 2];
        GiPrimitiveTransportProfile profile = GiPrimitiveTransportProfileGenerator.Generate(
            0,
            subMesh,
            new ModelMaterial { Emissive = Vector4.One });
        GiPrimitiveEmissiveTriangleRecord record = profile.EmissiveTriangles.Single();
        GiPrimitiveTransportProfile malformed = profile with
        {
            EmissiveTriangles =
            [
                record with
                {
                    TriangleIndex = profile.EmissiveSourceTriangleCount,
                    CookedImportance = record.CookedImportance * 2.0
                }
            ]
        };

        IReadOnlyList<string> errors = malformed.Validate();
        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("indices"));
            Assert.That(errors, Has.Some.Contains("inconsistent"));
        });
    }

    [Test]
    public void MaterialPackage_RoundTripsCompletePrimitiveProfiles()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "triangle",
            MaterialIndex = 0,
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial { Emissive = Vector4.One };
        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(0, subMesh, material);
        var table = new CookedMaterialTable([material])
        {
            PrimitiveTransportProfiles = [profile],
            PrimitiveTransportAlgorithmVersion = GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            HasCompleteTransportMetadata = true
        };
        string path = Path.Combine(_directory, "profiles.njmat");

        CookedPackage.WriteMaterials(path, table, 1, 2, 3);
        CookedMaterialTable loaded = CookedPackage.LoadMaterials(
            path,
            CookedAssetReaderFlags.None,
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.HasCompleteTransportMetadata, Is.True);
            Assert.That(loaded.PrimitiveTransportProfiles, Has.Count.EqualTo(1));
            Assert.That(loaded.PrimitiveTransportProfiles[0].InputHash, Is.EqualTo(profile.InputHash));
            Assert.That(loaded.PrimitiveTransportProfiles[0].MeanDiffuseReflectance, Is.EqualTo(profile.MeanDiffuseReflectance));
            Assert.That(loaded.PrimitiveTransportProfiles[0].TextureSourceHashes, Is.EqualTo(profile.TextureSourceHashes));
            Assert.That(loaded.PrimitiveTransportProfiles[0].EmissiveTriangles, Is.EqualTo(profile.EmissiveTriangles));
            Assert.That(loaded.PrimitiveTransportProfiles[0].EmissiveTriangleFlags, Is.EqualTo(profile.EmissiveTriangleFlags));
            Assert.That(loaded.PrimitiveTransportProfiles[0].Validate(), Is.Empty);
        });
    }

    [Test]
    public void MaterialPackage_RejectsPreviousHemisphericalTransportAlgorithm()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "stale-transport",
            MaterialIndex = 0,
            Vertices = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            Indices = [0, 1, 2]
        };
        GiPrimitiveTransportProfile staleProfile =
            GiPrimitiveTransportProfileGenerator
                .Generate(0, subMesh, ModelMaterial.Default) with
            {
                AlgorithmVersion = 2
            };
        var staleTable = new CookedMaterialTable([ModelMaterial.Default])
        {
            PrimitiveTransportProfiles = [staleProfile],
            PrimitiveTransportAlgorithmVersion = 2,
            HasCompleteTransportMetadata = true
        };
        string path = Path.Combine(_directory, "stale-profiles.njmat");

        Assert.That(
            () => CookedPackage.WriteMaterials(path, staleTable, 1, 2, 3),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("declares primitive algorithm 2, expected 5"));
        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public void Migrator_PreservesCurrentProfilesAndStripsStaleProfiles()
    {
        var subMesh = new ModelSubMesh
        {
            Name = "migration-triangle",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0)
            ],
            Indices = [0, 1, 2]
        };
        GiPrimitiveTransportProfile profile =
            GiPrimitiveTransportProfileGenerator.Generate(
                0,
                subMesh,
                ModelMaterial.Default);
        var currentTable = new CookedMaterialTable([ModelMaterial.Default])
        {
            PrimitiveTransportProfiles = [profile],
            PrimitiveTransportAlgorithmVersion =
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            HasCompleteTransportMetadata = true
        };
        string currentPath = Path.Combine(_directory, "current.njmat");
        string currentMigratedPath = Path.Combine(
            _directory,
            "current-migrated.njmat");
        CookedPackage.WriteMaterials(
            currentPath,
            currentTable,
            sourceHash: 1,
            settingsHash: 2,
            dependencyHash: 3);
        CookedAssetMigrator.MigrateFile(
            currentPath,
            currentMigratedPath);
        CookedMaterialTable currentMigrated = CookedPackage.LoadMaterials(
            currentMigratedPath,
            CookedAssetReaderFlags.None,
            out _);

        GiPrimitiveTransportProfile staleProfile = profile with
        {
            AlgorithmVersion =
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion - 1
        };
        var staleTable = currentTable with
        {
            PrimitiveTransportProfiles = [staleProfile],
            PrimitiveTransportAlgorithmVersion =
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion - 1
        };
        string stalePath = Path.Combine(_directory, "stale-raw.njmat");
        using (var writer = new CookedAssetWriter(
                   stalePath,
                   CookedAssetKind.Material))
        {
            writer.WriteSection(
                CookedSectionIds.Materials,
                CookedSectionFlags.Required,
                CookedJson.Serialize(staleTable));
            writer.Complete();
        }
        string staleMigratedPath = Path.Combine(
            _directory,
            "stale-migrated.njmat");
        CookedAssetMigrator.MigrateFile(stalePath, staleMigratedPath);
        CookedMaterialTable staleMigrated = CookedPackage.LoadMaterials(
            staleMigratedPath,
            CookedAssetReaderFlags.None,
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(
                currentMigrated.PrimitiveTransportProfiles,
                Has.Count.EqualTo(1));
            Assert.That(
                currentMigrated.PrimitiveTransportProfiles[0].InputHash,
                Is.EqualTo(profile.InputHash));
            Assert.That(
                currentMigrated.PrimitiveTransportAlgorithmVersion,
                Is.EqualTo(
                    GiPrimitiveTransportProfile.CurrentAlgorithmVersion));
            Assert.That(
                currentMigrated.HasCompleteTransportMetadata,
                Is.True);

            Assert.That(
                staleMigrated.PrimitiveTransportProfiles,
                Is.Empty);
            Assert.That(
                staleMigrated.PrimitiveTransportAlgorithmVersion,
                Is.Zero);
            Assert.That(
                staleMigrated.HasCompleteTransportMetadata,
                Is.False);
        });
    }

    private static ModelTextureSlot CreateBinding(
        ModelTextureSource source,
        TextureWrapMode wrap,
        Vector2 offset) => new()
        {
            Source = source,
            ColorSpace = TextureColorSpace.Linear,
            Offset = offset,
            Sampler = new TextureSamplerDescription(
            wrap,
            TextureWrapMode.ClampToEdge,
            TextureFilterMode.Nearest,
            TextureFilterMode.Nearest,
            TextureMipFilterMode.Nearest,
            1f)
        };

    private static ModelSubMesh CreateTwoTriangleUvMesh() => new()
    {
        Name = "two-spatial-triangles",
        MaterialIndex = 0,
        Vertices =
        [
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(2, 0, 0),
            new Vector3(3, 0, 0),
            new Vector3(2, 1, 0)
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

    private static TextureTransportImage CreatePixel(
        byte r,
        byte g,
        byte b,
        byte a,
        ulong hash) => TextureTransportImage.FromRgba8(
        [r, g, b, a],
        1,
        1,
        TextureColorSpace.Linear,
        TextureSemantic.Data,
        hash);
}
