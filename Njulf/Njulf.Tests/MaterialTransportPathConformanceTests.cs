using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialTransportPathConformanceTests
{
    private const uint Clearcoat = 1u << 0;
    private const uint ClearcoatTexture = 1u << 1;
    private const uint Sheen = 1u << 4;
    private const uint SheenColorTexture = 1u << 5;
    private const uint Transmission = 1u << 9;
    private const uint TransmissionTexture = 1u << 10;
    private const uint Specular = 1u << 15;
    private const uint SpecularTexture = 1u << 16;
    private const uint SpecularColorTexture = 1u << 17;
    private const uint Ior = 1u << 24;

    [Test]
    public void FineCompactAndFarFieldPreserveCoveredEnergyAcrossMaterialMatrix()
    {
        foreach (Fixture fixture in CreateFixtures())
        {
            FineReference fine = IntegrateFineReference(fixture);
            GiPrimitiveTransportProfile cooked = GiPrimitiveTransportProfileGenerator.Generate(
                0,
                fixture.Mesh,
                fixture.Material,
                fixture.Textures);
            GiMaterialTransportProfile compact =
                ModelRenderUploadService.ConvertPrimitiveTransportProfile(cooked);
            var candidate = new FarFieldMaterialPayloadV2.Candidate(
                StablePrimitiveKey: 0x1234u,
                Coverage: compact.AlphaCoverage,
                DiffuseReflectance: compact.MeanDiffuseReflectance,
                EmissiveRadiance: compact.MeanEmissiveRadiance,
                GeometricNormal: Vector3.UnitZ,
                NormalCone: 0.1f,
                MaterialFlags: 0x35u,
                MaterialRevision: 17u,
                TransportProfileRevision: 23u,
                MaterialOcclusion: compact.MeanMaterialOcclusion);
            FarFieldMaterialPayloadV2.ResolveResult resolved =
                FarFieldMaterialPayloadV2.Resolve([candidate]);
            FarFieldMaterialPayloadV2.Candidate far =
                FarFieldMaterialPayloadV2.Unpack(resolved.Payload);

            Vector3 fineDiffuseEnergy = fine.ConditionalDiffuse * fine.Coverage;
            Vector3 compactDiffuseEnergy = compact.MeanDiffuseReflectance * compact.AlphaCoverage;
            Vector3 farDiffuseEnergy = far.DiffuseReflectance * far.Coverage;
            Vector3 fineEmissionEnergy = fine.ConditionalEmission * fine.Coverage;
            Vector3 compactEmissionEnergy = compact.MeanEmissiveRadiance * compact.AlphaCoverage;
            Vector3 farEmissionEnergy = far.EmissiveRadiance * far.Coverage;

            Assert.Multiple(() =>
            {
                Assert.That(cooked.Validate(), Is.Empty, fixture.Name);
                Assert.That(compact.Quality, Is.EqualTo(GiTransportProfileQuality.PrimitiveSurfaceSampling), fixture.Name);
                Assert.That(compact.AlphaCoverage, Is.EqualTo(fine.Coverage).Within(1e-6f), fixture.Name);
                Assert.That(
                    RelativeEnergyError(fineDiffuseEnergy, compactDiffuseEnergy),
                    Is.LessThanOrEqualTo(0.05f),
                    $"{fixture.Name}: fine -> compact diffuse energy");
                Assert.That(
                    RelativeEnergyError(compactDiffuseEnergy, farDiffuseEnergy),
                    Is.LessThanOrEqualTo(0.08f),
                    $"{fixture.Name}: compact -> far diffuse energy");
                Assert.That(
                    RelativeEnergyError(fineEmissionEnergy, compactEmissionEnergy),
                    Is.LessThanOrEqualTo(0.05f),
                    $"{fixture.Name}: fine -> compact emission energy");
                Assert.That(
                    RelativeEnergyError(compactEmissionEnergy, farEmissionEnergy),
                    Is.LessThanOrEqualTo(0.08f),
                    $"{fixture.Name}: compact -> far emission energy");
                Assert.That(far.MaterialRevision, Is.EqualTo(17u), fixture.Name);
                Assert.That(far.TransportProfileRevision, Is.EqualTo(23u), fixture.Name);
                Assert.That(
                    far.MaterialOcclusion,
                    Is.EqualTo(compact.MeanMaterialOcclusion).Within(0.001f),
                    $"{fixture.Name}: compact -> far material AO");
                Assert.That(
                    compact.MeanMaterialOcclusion,
                    Is.EqualTo(fine.ConditionalMaterialOcclusion).Within(0.01f),
                    $"{fixture.Name}: fine -> compact material AO");
                Assert.That(resolved.ConflictCount, Is.Zero, fixture.Name);
            });
        }
    }

    [Test]
    public void NonWhiteVertexColor_IsAppliedOnceAcrossFineCompactAndFarField()
    {
        Vector3 vertexRgb = new(0.25f, 0.50f, 0.75f);
        var mesh = new ModelSubMesh
        {
            Name = "non-white-vertex-color-parity",
            MaterialIndex = 0,
            Vertices =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            ],
            VertexColors =
            [
                new Vector4(vertexRgb.X, vertexRgb.Y, vertexRgb.Z, 1f),
                new Vector4(vertexRgb.X, vertexRgb.Y, vertexRgb.Z, 1f),
                new Vector4(vertexRgb.X, vertexRgb.Y, vertexRgb.Z, 1f)
            ],
            Indices = [0, 1, 2]
        };
        var material = new ModelMaterial
        {
            Albedo = new Vector4(0.80f, 0.60f, 0.40f, 1f),
            Metallic = 0f,
            Roughness = 0.6f
        };

        Vector3 fineBaseColor = new(
            material.Albedo.X * vertexRgb.X,
            material.Albedo.Y * vertexRgb.Y,
            material.Albedo.Z * vertexRgb.Z);
        Vector3 fineDiffuse =
            GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                fineBaseColor,
                metallic: material.Metallic);

        GiPrimitiveTransportProfile cooked =
            GiPrimitiveTransportProfileGenerator.Generate(0, mesh, material);
        GiMaterialTransportProfile compact =
            ModelRenderUploadService.ConvertPrimitiveTransportProfile(cooked);
        FarFieldMaterialPayloadV2.Candidate far = FarFieldMaterialPayloadV2.Unpack(
            FarFieldMaterialPayloadV2.Resolve(
            [
                new FarFieldMaterialPayloadV2.Candidate(
                    StablePrimitiveKey: 7u,
                    Coverage: compact.AlphaCoverage,
                    DiffuseReflectance: compact.MeanDiffuseReflectance,
                    EmissiveRadiance: Vector3.Zero,
                    GeometricNormal: Vector3.UnitZ,
                    NormalCone: 0f,
                    MaterialFlags: 0u,
                    MaterialRevision: 1u,
                    TransportProfileRevision: 1u)
            ]).Payload);

        Vector3 incorrectlyAppliedTwice = new(
            compact.MeanDiffuseReflectance.X * vertexRgb.X,
            compact.MeanDiffuseReflectance.Y * vertexRgb.Y,
            compact.MeanDiffuseReflectance.Z * vertexRgb.Z);

        Assert.Multiple(() =>
        {
            Assert.That(cooked.Validate(), Is.Empty);
            Assert.That(
                cooked.Quality,
                Is.EqualTo(GiPrimitiveTransportProfileQuality.FactorAndVertexColor));
            Assert.That(
                RelativeEnergyError(fineDiffuse, compact.MeanDiffuseReflectance),
                Is.LessThan(1e-5f),
                "The compact profile must include vertex RGB exactly once.");
            Assert.That(
                RelativeEnergyError(fineDiffuse, far.DiffuseReflectance),
                Is.LessThan(0.01f),
                "RGB10 far-field packing must preserve the same vertex-colored mean.");
            Assert.That(
                RelativeEnergyError(fineDiffuse, incorrectlyAppliedTwice),
                Is.GreaterThan(0.20f),
                "A second vertex-RGB multiplication must be observably rejected by the fixture.");
            Assert.That(
                far.DiffuseReflectance.X,
                Is.EqualTo(compact.MeanDiffuseReflectance.X).Within(1.0f / 1023.0f));
            Assert.That(
                far.DiffuseReflectance.Y,
                Is.EqualTo(compact.MeanDiffuseReflectance.Y).Within(1.0f / 1023.0f));
            Assert.That(
                far.DiffuseReflectance.Z,
                Is.EqualTo(compact.MeanDiffuseReflectance.Z).Within(1.0f / 1023.0f));
        });
    }

    private static IReadOnlyList<Fixture> CreateFixtures()
    {
        ModelSubMesh mesh = CreateTwoRegionMesh();
        var source = new ModelTextureSource
        {
            Bytes = [1],
            CacheIdentity = "transport-path-matrix",
            DebugName = "transport-path-matrix"
        };
        ModelTextureSlot clamp = CreateBinding(source, TextureWrapMode.ClampToEdge, Vector2.Zero);
        ModelTextureSlot wrapped = CreateBinding(source, TextureWrapMode.Repeat, new Vector2(0.5f, 0f));

        TextureTransportImage dielectricBase = CreateImage(
            [64, 128, 192, 255, 192, 64, 128, 255],
            TextureSemantic.Color,
            0x101);
        TextureTransportImage dielectricOcclusion = CreateImage(
            [32, 255, 255, 255, 224, 255, 255, 255],
            TextureSemantic.Data,
            0x111);
        var dielectric = new ModelMaterial
        {
            BaseColorTexture = clamp,
            OcclusionTexture = clamp,
            AmbientOcclusion = 0.65f
        };

        TextureTransportImage correlatedBase = CreateImage(
            [255, 255, 255, 255, 128, 32, 16, 255],
            TextureSemantic.Color,
            0x102);
        TextureTransportImage correlatedMr = CreateImage(
            [255, 255, 255, 255, 255, 128, 0, 255],
            TextureSemantic.Data,
            0x103);
        var metallic = new ModelMaterial
        {
            BaseColorTexture = clamp,
            MetallicRoughnessTexture = clamp,
            Metallic = 1f,
            Roughness = 0.8f
        };

        TextureTransportImage maskedBase = CreateImage(
            [255, 64, 32, 255, 16, 32, 255, 0],
            TextureSemantic.Color,
            0x104);
        TextureTransportImage maskedEmission = CreateImage(
            [128, 32, 16, 255, 0, 128, 255, 255],
            TextureSemantic.Color,
            0x105);
        var masked = new ModelMaterial
        {
            BaseColorTexture = clamp,
            EmissiveTexture = clamp,
            Emissive = new Vector4(1f, 0.5f, 0.25f, 1f),
            EmissiveStrength = 10f,
            AlphaMode = ModelAlphaMode.Mask,
            AlphaCutoff = 0.5f
        };

        TextureTransportImage extensionBase = CreateImage(
            [32, 96, 224, 255, 224, 160, 48, 255],
            TextureSemantic.Color,
            0x106);
        TextureTransportImage clearcoatImage = CreateImage(
            [64, 255, 255, 255, 192, 255, 255, 255],
            TextureSemantic.Data,
            0x107);
        TextureTransportImage sheenImage = CreateImage(
            [32, 96, 160, 255, 160, 64, 32, 255],
            TextureSemantic.Color,
            0x108);
        TextureTransportImage transmissionImage = CreateImage(
            [64, 255, 255, 255, 128, 255, 255, 255],
            TextureSemantic.Data,
            0x109);
        TextureTransportImage specularImage = CreateImage(
            [255, 255, 255, 96, 255, 255, 255, 224],
            TextureSemantic.Data,
            0x10a);
        TextureTransportImage specularColorImage = CreateImage(
            [224, 128, 64, 255, 64, 160, 224, 255],
            TextureSemantic.Color,
            0x10b);
        var extensions = new ModelMaterial
        {
            FeatureFlags = Clearcoat | ClearcoatTexture |
                           Sheen | SheenColorTexture |
                           Transmission | TransmissionTexture |
                           Ior |
                           Specular | SpecularTexture | SpecularColorTexture,
            Albedo = new Vector4(0.9f, 0.8f, 0.7f, 1f),
            Metallic = 0.15f,
            BaseColorTexture = wrapped,
            ClearcoatFactor = 0.7f,
            ClearcoatTexture = wrapped,
            SheenColor = new Vector4(0.3f, 0.2f, 0.1f, 1f),
            SheenColorTexture = wrapped,
            TransmissionFactor = 0.35f,
            Ior = 1.8f,
            TransmissionTexture = wrapped,
            SpecularFactor = 0.65f,
            SpecularTexture = wrapped,
            SpecularColor = new Vector4(0.9f, 0.7f, 0.5f, 1f),
            SpecularColorTexture = wrapped
        };

        return
        [
            new Fixture(
                "dielectric-textured",
                mesh,
                dielectric,
                new GiPrimitiveTextureInputs(
                    BaseColor: dielectricBase,
                    Occlusion: dielectricOcclusion)),
            new Fixture(
                "correlated-metallic",
                mesh,
                metallic,
                new GiPrimitiveTextureInputs(
                    BaseColor: correlatedBase,
                    MetallicRoughness: correlatedMr)),
            new Fixture(
                "masked-correlated-emission",
                mesh,
                masked,
                new GiPrimitiveTextureInputs(
                    BaseColor: maskedBase,
                    Emissive: maskedEmission)),
            new Fixture(
                "wrapped-extension-energy",
                mesh,
                extensions,
                new GiPrimitiveTextureInputs(
                    BaseColor: extensionBase,
                    Clearcoat: clearcoatImage,
                    SheenColor: sheenImage,
                    Transmission: transmissionImage,
                    Specular: specularImage,
                    SpecularColor: specularColorImage))
        ];
    }

    private static FineReference IntegrateFineReference(Fixture fixture)
    {
        double totalArea = 0.0;
        double coveredArea = 0.0;
        Vector3 diffuseIntegral = Vector3.Zero;
        Vector3 emissionIntegral = Vector3.Zero;
        double materialOcclusionIntegral = 0.0;
        for (int index = 0; index < fixture.Mesh.Indices.Length; index += 3)
        {
            uint i0 = fixture.Mesh.Indices[index];
            uint i1 = fixture.Mesh.Indices[index + 1];
            uint i2 = fixture.Mesh.Indices[index + 2];
            Vector3 p0 = fixture.Mesh.Vertices[i0];
            Vector3 p1 = fixture.Mesh.Vertices[i1];
            Vector3 p2 = fixture.Mesh.Vertices[i2];
            float area = 0.5f * Vector3.Cross(p1 - p0, p2 - p0).Length();
            Vector2 uv = (fixture.Mesh.TexCoords[i0] + fixture.Mesh.TexCoords[i1] + fixture.Mesh.TexCoords[i2]) / 3f;
            TextureTransportVector4 baseSample = Sample(
                fixture.Material.BaseColorTexture,
                fixture.Textures.BaseColor,
                uv);
            TextureTransportVector4 mrSample = Sample(
                fixture.Material.MetallicRoughnessTexture,
                fixture.Textures.MetallicRoughness,
                uv);
            float alpha = Math.Clamp(fixture.Material.Albedo.W * (float)baseSample.W, 0f, 1f);
            float coverage = fixture.Material.AlphaMode switch
            {
                ModelAlphaMode.Mask => alpha >= fixture.Material.AlphaCutoff ? 1f : 0f,
                ModelAlphaMode.Blend => alpha,
                _ => 1f
            };
            Vector3 baseColor = new(
                fixture.Material.Albedo.X * (float)baseSample.X,
                fixture.Material.Albedo.Y * (float)baseSample.Y,
                fixture.Material.Albedo.Z * (float)baseSample.Z);
            float metallic = Math.Clamp(
                fixture.Material.Metallic * (float)mrSample.Z,
                0f,
                1f);
            bool clearcoatEnabled = HasFeature(fixture.Material, Clearcoat);
            bool sheenEnabled = HasFeature(fixture.Material, Sheen);
            bool transmissionEnabled = HasFeature(fixture.Material, Transmission);
            bool specularEnabled = HasFeature(fixture.Material, Specular);
            TextureTransportVector4 clearcoatSample = SampleActive(
                fixture.Material,
                ClearcoatTexture,
                fixture.Material.ClearcoatTexture,
                fixture.Textures.Clearcoat,
                uv);
            TextureTransportVector4 sheenSample = SampleActive(
                fixture.Material,
                SheenColorTexture,
                fixture.Material.SheenColorTexture,
                fixture.Textures.SheenColor,
                uv);
            TextureTransportVector4 transmissionSample = SampleActive(
                fixture.Material,
                TransmissionTexture,
                fixture.Material.TransmissionTexture,
                fixture.Textures.Transmission,
                uv);
            TextureTransportVector4 specularSample = SampleActive(
                fixture.Material,
                SpecularTexture,
                fixture.Material.SpecularTexture,
                fixture.Textures.Specular,
                uv);
            TextureTransportVector4 specularColorSample = SampleActive(
                fixture.Material,
                SpecularColorTexture,
                fixture.Material.SpecularColorTexture,
                fixture.Textures.SpecularColor,
                uv);
            float clearcoatValue = clearcoatEnabled
                ? fixture.Material.ClearcoatFactor * (float)clearcoatSample.X
                : 0f;
            Vector3 sheenColor = sheenEnabled
                ? new Vector3(
                    fixture.Material.SheenColor.X * (float)sheenSample.X,
                    fixture.Material.SheenColor.Y * (float)sheenSample.Y,
                    fixture.Material.SheenColor.Z * (float)sheenSample.Z)
                : Vector3.Zero;
            float transmissionValue = transmissionEnabled
                ? fixture.Material.TransmissionFactor * (float)transmissionSample.X
                : 0f;
            float specularValue = specularEnabled
                ? fixture.Material.SpecularFactor * (float)specularSample.W
                : 1f;
            Vector3 specularColor = specularEnabled
                ? new Vector3(
                    fixture.Material.SpecularColor.X * (float)specularColorSample.X,
                    fixture.Material.SpecularColor.Y * (float)specularColorSample.Y,
                    fixture.Material.SpecularColor.Z * (float)specularColorSample.Z)
                : Vector3.One;
            Vector3 diffuse = fixture.Material.Unlit
                ? Vector3.Zero
                : GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                    baseColor,
                    metallic,
                    (transmissionEnabled ||
                     (fixture.Material.FeatureFlags & Ior) != 0)
                        ? fixture.Material.Ior
                        : 1.5f,
                    specularValue,
                    specularColor,
                    transmissionValue,
                    clearcoatValue,
                    sheenColor,
                    1f);
            TextureTransportVector4 emissiveSample = Sample(
                fixture.Material.EmissiveTexture,
                fixture.Textures.Emissive,
                uv);
            Vector3 emission = fixture.Material.Unlit
                ? Vector3.Zero
                : new Vector3(
                    fixture.Material.Emissive.X * (float)emissiveSample.X,
                    fixture.Material.Emissive.Y * (float)emissiveSample.Y,
                    fixture.Material.Emissive.Z * (float)emissiveSample.Z) * fixture.Material.EmissiveStrength;
            TextureTransportVector4 occlusionSample = Sample(
                fixture.Material.OcclusionTexture,
                fixture.Textures.Occlusion,
                uv);
            float materialOcclusion = Math.Clamp(
                1f + fixture.Material.AmbientOcclusion *
                ((float)occlusionSample.X - 1f),
                0f,
                1f);

            totalArea += area;
            coveredArea += area * coverage;
            diffuseIntegral += diffuse * (area * coverage);
            emissionIntegral += emission * (area * coverage);
            materialOcclusionIntegral += materialOcclusion * area * coverage;
        }

        float coverageMean = totalArea > 0.0 ? (float)(coveredArea / totalArea) : 0f;
        if (coveredArea <= 1e-12)
            return new FineReference(Vector3.Zero, Vector3.Zero, 1f, coverageMean);
        return new FineReference(
            diffuseIntegral / (float)coveredArea,
            emissionIntegral / (float)coveredArea,
            (float)(materialOcclusionIntegral / coveredArea),
            coverageMean);
    }

    private static TextureTransportVector4 Sample(
        ModelTextureSlot? binding,
        TextureTransportImage? image,
        Vector2 uv) => binding?.Source is not null && image is not null
        ? image.Sample(binding, uv)
        : TextureTransportVector4.One;

    private static TextureTransportVector4 SampleActive(
        ModelMaterial material,
        uint feature,
        ModelTextureSlot? binding,
        TextureTransportImage? image,
        Vector2 uv) => HasFeature(material, feature)
        ? Sample(binding, image, uv)
        : TextureTransportVector4.One;

    private static bool HasFeature(ModelMaterial material, uint feature) =>
        (material.FeatureFlags & feature) != 0;

    private static float RelativeEnergyError(Vector3 reference, Vector3 actual)
    {
        float referenceLuminance = Luminance(reference);
        float actualLuminance = Luminance(actual);
        if (referenceLuminance <= 1e-6f)
            return Math.Max(Math.Max(Math.Abs(actual.X), Math.Abs(actual.Y)), Math.Abs(actual.Z));
        return Math.Abs(actualLuminance - referenceLuminance) / referenceLuminance;
    }

    private static float Luminance(Vector3 value) =>
        Math.Max(value.X, 0f) * 0.2126f +
        Math.Max(value.Y, 0f) * 0.7152f +
        Math.Max(value.Z, 0f) * 0.0722f;

    private static ModelSubMesh CreateTwoRegionMesh() => new()
    {
        Name = "fine-compact-far-matrix",
        MaterialIndex = 0,
        Vertices =
        [
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
            new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f), new Vector3(2f, 1f, 0f)
        ],
        TexCoords =
        [
            new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f), new Vector2(0.25f, 0.5f),
            new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(0.75f, 0.5f)
        ],
        Indices = [0, 1, 2, 3, 4, 5]
    };

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

    private static TextureTransportImage CreateImage(
        byte[] pixels,
        TextureSemantic semantic,
        ulong hash) => TextureTransportImage.FromRgba8(
        pixels,
        2,
        1,
        TextureColorSpace.Linear,
        semantic,
        hash);

    private sealed record Fixture(
        string Name,
        ModelSubMesh Mesh,
        ModelMaterial Material,
        GiPrimitiveTextureInputs Textures);

    private readonly record struct FineReference(
        Vector3 ConditionalDiffuse,
        Vector3 ConditionalEmission,
        float ConditionalMaterialOcclusion,
        float Coverage);
}
