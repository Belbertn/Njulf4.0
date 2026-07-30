using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public enum SampleMaterialGiFixtureKind : byte
{
    AnalyticSurface = 0,
    StaticAlphaCard = 1,
    SkinnedAlphaCard = 2,
    SeparateUvOcclusion = 3,
    ExtensionSurface = 4
}

public enum SampleMaterialGiCaptureSignal : byte
{
    DirectDiffuse = 0,
    DirectSpecular = 1,
    RawDdgiIrradiance = 2,
    FinalDdgiDiffuse = 3,
    RawSsgiEstimate = 4,
    FinalComposedIndirect = 5,
    MaterialDiffuseReflectance = 6,
    CompiledEmission = 7,
    MaterialOcclusion = 8,
    GiOwnershipWeights = 9,
    MaterialSidedness = 10
}

/// <summary>
/// One immutable analytic material fixture. Texture fields contain already
/// decoded linear samples, matching the shader helper boundary rather than
/// introducing image-decoder behavior into the CPU/GPU oracle.
/// </summary>
public sealed record SampleMaterialGiConformanceCase(
    string Name,
    string Group,
    string Description,
    SampleMaterialGiFixtureKind FixtureKind,
    MaterialDefinition Material,
    Vector4 BaseColorSample,
    Vector3 MetallicRoughnessSample,
    float OcclusionSample,
    Vector3 EmissiveSample,
    Vector4 VertexColor,
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    Vector3 ViewDirection,
    float NdotL,
    bool FrontFacing,
    Vector3 Irradiance)
{
    public GiMaterialSampleInputs CreateCpuInputs()
    {
        Vector3 correctedShadingNormal =
            GiMaterialReferenceEvaluator.CorrectShadingNormal(GeometricNormal, ShadingNormal);
        Vector3 normalizedView = NormalizeOr(ViewDirection, correctedShadingNormal);
        float nDotV = Math.Max(Vector3.Dot(correctedShadingNormal, normalizedView), 0f);
        return new GiMaterialSampleInputs(
            BaseColorSample,
            VertexColor,
            MetallicRoughnessSample,
            OcclusionSample,
            EmissiveSample,
            GeometricNormal,
            ShadingNormal,
            nDotV);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }
}

/// <summary>
/// Maps one required linear signal to an existing renderer debug view where
/// one exists. Direct diffuse/specular are named source attachments because
/// they are not representable by a GI or material debug enum.
/// </summary>
public sealed record SampleMaterialGiCaptureOutput(
    SampleMaterialGiCaptureSignal Signal,
    string FileStem,
    string SourceAttachment,
    GlobalIlluminationDebugView GlobalIlluminationDebugView,
    MaterialDebugView MaterialDebugView,
    string Description);

public sealed record SampleMaterialGiRendererSettings(
    RenderQualityPreset QualityPreset,
    bool GlobalIlluminationEnabled,
    bool DdgiEnabled,
    bool SsgiEnabled,
    bool FarFieldEnabled,
    string CaptureColorSpace,
    bool ExposureAppliedToLinearArtifacts,
    bool ToneMappingAppliedToLinearArtifacts);

/// <summary>
/// Runtime-populated provenance written next to a hardware capture. Contract
/// data stays fixed by <see cref="SampleMaterialGiConformanceCatalog.Fingerprint"/>;
/// device/build values are supplied by the capture runner and never guessed.
/// </summary>
public sealed record SampleMaterialGiCaptureMetadata(
    string SchemaVersion,
    string ContractFingerprint,
    string SceneSha256,
    DateTimeOffset CapturedAtUtc,
    string BuildCommit,
    string ShaderSha256,
    string Device,
    string Driver,
    SampleSceneKind SceneKind,
    SamplePerformanceScenario Scenario,
    int Width,
    int Height,
    int WarmupFrames,
    uint RandomSeed,
    SampleSponzaGiCameraBookmark Camera,
    float Exposure,
    Vector3 DirectionalLightDirection,
    Vector3 DirectionalLightColor,
    float DirectionalLightIntensity,
    SampleMaterialGiRendererSettings RendererSettings,
    IReadOnlyList<string> MaterialCases,
    SampleMaterialGiSemanticEvidenceContract SemanticEvidence,
    IReadOnlyList<SampleMaterialGiCaptureOutput> RequiredOutputs);

/// <summary>
/// Phase-0 material/GI oracle and capture contract. Its cases are consumed by
/// both the windowless Vulkan compute test and the material-showcase capture,
/// preventing a hand-authored image fixture from drifting away from the
/// numerical oracle.
/// </summary>
public static class SampleMaterialGiConformanceCatalog
{
    public const string CurrentSchemaVersion = "material-gi-conformance/v2";
    public const int LockedWidth = 1600;
    public const int LockedHeight = 900;
    public const int WarmupFrameCount = 360;
    public const uint FixedRandomSeed = 0x2026_0728u;
    public const float LockedExposure = 1f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SampleSceneKind SceneKind => SampleSceneKind.MaterialShowcase;

    public static SamplePerformanceScenario Scenario => SamplePerformanceScenario.Normal;

    public static SampleSponzaGiCameraBookmark Camera { get; } = new(
        "MaterialGiConformanceOverview",
        new Vector3(0f, 1.65f, 7.8f),
        0f,
        -0.11f,
        MathF.PI / 3f,
        0.05f,
        100f);

    public static Vector3 DirectionalLightDirection { get; } =
        Normalize(new Vector3(-0.42f, -0.82f, -0.38f));

    public static Vector3 DirectionalLightColor { get; } = Vector3.One;

    public const float DirectionalLightIntensity = 3.5f;

    public static SampleMaterialGiRendererSettings RendererSettings { get; } = new(
        RenderQualityPreset.DdgiHigh,
        GlobalIlluminationEnabled: true,
        DdgiEnabled: true,
        SsgiEnabled: true,
        FarFieldEnabled: true,
        CaptureColorSpace: "linear-scRGB-float",
        ExposureAppliedToLinearArtifacts: false,
        ToneMappingAppliedToLinearArtifacts: false);

    public static IReadOnlyList<SampleMaterialGiCaptureOutput> RequiredOutputs { get; } =
        Array.AsReadOnly<SampleMaterialGiCaptureOutput>(
        [
            new(
                SampleMaterialGiCaptureSignal.DirectDiffuse,
                "direct-diffuse",
                "linear-direct-diffuse",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.None,
                "Linear direct diffuse before exposure and tone mapping."),
            new(
                SampleMaterialGiCaptureSignal.DirectSpecular,
                "direct-specular",
                "linear-direct-specular",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.None,
                "Linear direct specular before exposure and tone mapping."),
            new(
                SampleMaterialGiCaptureSignal.RawDdgiIrradiance,
                "ddgi-irradiance",
                "renderer-debug-view",
                GlobalIlluminationDebugView.DdgiIrradiance,
                MaterialDebugView.None,
                "Raw DDGI irradiance before the receiver BRDF."),
            new(
                SampleMaterialGiCaptureSignal.FinalDdgiDiffuse,
                "ddgi-final-diffuse",
                "renderer-debug-view",
                GlobalIlluminationDebugView.DdgiFinalDiffuse,
                MaterialDebugView.None,
                "Final DDGI diffuse after the canonical receiver response."),
            new(
                SampleMaterialGiCaptureSignal.RawSsgiEstimate,
                "ssgi-raw",
                "renderer-debug-view",
                GlobalIlluminationDebugView.SsgiRaw,
                MaterialDebugView.None,
                "Raw SSGI diffuse estimate."),
            new(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                "indirect-composed",
                "renderer-debug-view",
                GlobalIlluminationDebugView.FinalIndirect,
                MaterialDebugView.None,
                "Final ownership-partitioned indirect diffuse."),
            new(
                SampleMaterialGiCaptureSignal.MaterialDiffuseReflectance,
                "material-diffuse-reflectance",
                "renderer-debug-view",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.CanonicalDiffuseReflectance,
                "Canonical hemispherical material diffuse reflectance."),
            new(
                SampleMaterialGiCaptureSignal.CompiledEmission,
                "material-emission",
                "renderer-debug-view",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.CompiledEmission,
                "Compiled linear HDR emissive radiance."),
            new(
                SampleMaterialGiCaptureSignal.MaterialOcclusion,
                "material-occlusion",
                "renderer-debug-view",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.MaterialOcclusion,
                "glTF material occlusion, kept separate from screen-space AO."),
            new(
                SampleMaterialGiCaptureSignal.GiOwnershipWeights,
                "gi-ownership",
                "renderer-debug-view",
                GlobalIlluminationDebugView.DdgiEffectiveWeight,
                MaterialDebugView.None,
                "DDGI support/ownership weight; pair with the final-composition output."),
            new(
                SampleMaterialGiCaptureSignal.MaterialSidedness,
                "material-sidedness",
                "renderer-debug-view",
                GlobalIlluminationDebugView.None,
                MaterialDebugView.Sidedness,
                "Post-coverage semantic sidedness used by the named winding/alpha evidence gate.")
        ]);

    public static IReadOnlyList<SampleMaterialGiConformanceCase> Cases { get; } =
        Array.AsReadOnly(CreateCases());

    public static string CaseFingerprint { get; } = ComputeCaseFingerprint();

    public static IReadOnlyList<SampleMaterialGiSceneFixture> SceneFixtures { get; } =
        Array.AsReadOnly(SampleMaterialGiConformanceSceneLayout.CreateFixtures(Cases));

    public static SampleMaterialGiSemanticEvidenceContract SemanticEvidence { get; } =
        SampleMaterialGiSemanticEvidenceGate.CreateContract(
            SceneFixtures,
            Camera,
            LockedWidth,
            LockedHeight);

    public static string SceneFingerprint { get; } =
        SampleMaterialGiConformanceSceneLayout.ComputeFingerprint(
            SceneFixtures,
            CaseFingerprint);

    public static string Fingerprint { get; } = ComputeFingerprint();

    public static SampleMaterialGiCaptureMetadata CreateCaptureMetadata(
        DateTimeOffset capturedAtUtc,
        string buildCommit,
        string shaderSha256,
        string device,
        string driver)
    {
        RequireValue(buildCommit, nameof(buildCommit));
        RequireSha256(shaderSha256, nameof(shaderSha256));
        RequireValue(device, nameof(device));
        RequireValue(driver, nameof(driver));

        return new SampleMaterialGiCaptureMetadata(
            CurrentSchemaVersion,
            Fingerprint,
            SceneFingerprint,
            capturedAtUtc.ToUniversalTime(),
            buildCommit.Trim(),
            shaderSha256.Trim().ToLowerInvariant(),
            device.Trim(),
            driver.Trim(),
            SceneKind,
            Scenario,
            LockedWidth,
            LockedHeight,
            WarmupFrameCount,
            FixedRandomSeed,
            Camera,
            LockedExposure,
            DirectionalLightDirection,
            DirectionalLightColor,
            DirectionalLightIntensity,
            RendererSettings,
            Array.AsReadOnly(Cases.Select(static value => value.Name).ToArray()),
            SemanticEvidence,
            RequiredOutputs);
    }

    public static void WriteContract(string outputDirectory)
    {
        RequireValue(outputDirectory, nameof(outputDirectory));
        Directory.CreateDirectory(outputDirectory);
        WriteJsonAtomically(
            Path.Combine(outputDirectory, "material-gi-conformance-contract.json"),
            new
            {
                schemaVersion = CurrentSchemaVersion,
                fingerprint = Fingerprint,
                caseFingerprint = CaseFingerprint,
                sceneSchemaVersion = SampleMaterialGiConformanceSceneLayout.CurrentSchemaVersion,
                sceneFingerprint = SceneFingerprint,
                sceneKind = SceneKind,
                scenario = Scenario,
                width = LockedWidth,
                height = LockedHeight,
                warmupFrames = WarmupFrameCount,
                randomSeed = FixedRandomSeed,
                camera = Camera,
                exposure = LockedExposure,
                directionalLightDirection = DirectionalLightDirection,
                directionalLightColor = DirectionalLightColor,
                directionalLightIntensity = DirectionalLightIntensity,
                rendererSettings = RendererSettings,
                cases = Cases,
                sceneFixtures = SceneFixtures,
                probeVolume = SampleMaterialGiConformanceSceneLayout.ProbeVolume,
                semanticEvidence = SemanticEvidence,
                requiredOutputs = RequiredOutputs
            });
    }

    public static void WriteCaptureMetadata(
        string outputDirectory,
        SampleMaterialGiCaptureMetadata metadata)
    {
        RequireValue(outputDirectory, nameof(outputDirectory));
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(metadata.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(metadata.ContractFingerprint, Fingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capture metadata does not belong to the current material/GI conformance contract.",
                nameof(metadata));
        }

        Directory.CreateDirectory(outputDirectory);
        WriteJsonAtomically(
            Path.Combine(outputDirectory, "material-gi-conformance-capture.json"),
            metadata);
    }

    private static SampleMaterialGiConformanceCase[] CreateCases()
    {
        var cases = new List<SampleMaterialGiConformanceCase>();
        MaterialDefinition baseline = new()
        {
            Name = "Conformance.BaselineDielectric",
            BaseColorFactor = new Vector4(0.82f, 0.48f, 0.21f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.5f
        };

        cases.Add(Case(
            "base-factor-texture-vertex-linear",
            "core",
            baseline,
            baseSample: new Vector4(0.6f, 0.75f, 0.9f, 0.8f),
            vertexColor: new Vector4(0.75f, 0.5f, 0.25f, 0.625f),
            description: "Base factor, decoded linear texture, and vertex color multiply exactly once."));

        foreach (float metallic in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            cases.Add(Case(
                $"metallic-{metallic.ToString("0.00", CultureInfo.InvariantCulture)}",
                "metallic-sweep",
                baseline with
                {
                    Name = $"Conformance.Metallic.{metallic:R}",
                    MetallicFactor = metallic
                },
                description: "Metallic sweep locks the gradual removal of diffuse energy."));
        }

        foreach (float roughness in new[] { 0f, 0.5f, 1f })
        {
            cases.Add(Case(
                $"roughness-{roughness.ToString("0.00", CultureInfo.InvariantCulture)}",
                "roughness-sweep",
                baseline with
                {
                    Name = $"Conformance.Roughness.{roughness:R}",
                    RoughnessFactor = roughness
                },
                metallicRoughnessSample: new Vector3(0.8f, 0.75f, 0.4f),
                description: "Authored roughness is multiplied by texture G and floored only at evaluation."));
        }

        foreach (float ior in new[] { 1f, 1.5f, 3f })
        {
            cases.Add(Case(
                $"dielectric-f0-ior-{ior.ToString("0.0", CultureInfo.InvariantCulture)}",
                "dielectric-f0-sweep",
                baseline with
                {
                    Name = $"Conformance.Ior.{ior:R}",
                    FeatureFlags = MaterialFeatureFlags.Ior,
                    Extensions = MaterialExtensionDefinition.None with
                    {
                        Ior = ior,
                        TransmissionFactor = 0f
                    }
                },
                fixtureKind: SampleMaterialGiFixtureKind.ExtensionSurface,
                viewDirection: Normalize(new Vector3(0.8f, 0.6f, 0f)),
                description: "IOR changes dielectric F0 even when opaque transmission removal is zero."));
        }

        foreach (float strength in new[] { 0f, 0.5f, 1f, 10f })
        {
            cases.Add(Case(
                $"emission-strength-{strength.ToString("0.0", CultureInfo.InvariantCulture)}",
                "emission-sweep",
                baseline with
                {
                    Name = $"Conformance.Emission.{strength:R}",
                    EmissiveFactor = new Vector3(0.5f, 0.25f, 0.125f),
                    EmissiveStrength = strength
                },
                emissiveSample: new Vector3(0.25f, 0.5f, 1f),
                description: "Emission scales in linear HDR without albedo, metallic, or AO modulation."));
        }

        cases.Add(Case(
            "ao-strength-zero",
            "occlusion",
            baseline with { Name = "Conformance.AoNeutral", OcclusionStrength = 0f },
            occlusionSample: 0.15f,
            fixtureKind: SampleMaterialGiFixtureKind.SeparateUvOcclusion,
            description: "AO strength zero is exactly neutral for a separate-UV occlusion sample."));
        cases.Add(Case(
            "ao-strength-one",
            "occlusion",
            baseline with { Name = "Conformance.AoFull", OcclusionStrength = 1f },
            occlusionSample: 0.15f,
            fixtureKind: SampleMaterialGiFixtureKind.SeparateUvOcclusion,
            description: "AO strength one reproduces the independent occlusion sample."));

        MaterialDefinition alpha = baseline with
        {
            Name = "Conformance.Alpha",
            AlphaMode = MaterialAlphaMode.Mask,
            AlphaCutoff = 0.5f
        };
        cases.Add(Case(
            "alpha-mask-equality-static",
            "alpha",
            alpha,
            baseSample: new Vector4(1f, 1f, 1f, 0.5f),
            fixtureKind: SampleMaterialGiFixtureKind.StaticAlphaCard,
            description: "Static mask equality is covered because glTF uses alpha >= cutoff."));
        cases.Add(Case(
            "alpha-mask-equality-skinned",
            "alpha",
            alpha with { Name = "Conformance.AlphaSkinned" },
            baseSample: new Vector4(1f, 1f, 1f, 0.5f),
            fixtureKind: SampleMaterialGiFixtureKind.SkinnedAlphaCard,
            description: "Skinning remains orthogonal to the exact alpha equality rule."));
        cases.Add(Case(
            "alpha-mask-cutoff-above-one",
            "alpha",
            alpha with
            {
                Name = "Conformance.AlphaAboveOne",
                AlphaCutoff = 1.01f
            },
            fixtureKind: SampleMaterialGiFixtureKind.StaticAlphaCard,
            description: "A legal cutoff above one remains fully uncovered and is never clamped."));
        cases.Add(Case(
            "alpha-blend-positive-raster-only",
            "alpha",
            baseline with
            {
                Name = "Conformance.AlphaBlendPositive",
                AlphaMode = MaterialAlphaMode.Blend
            },
            baseSample: new Vector4(1f, 1f, 1f, 0.25f),
            description: "Positive BLEND alpha survives raster coverage but never occupies opaque GI transport."));
        cases.Add(Case(
            "alpha-blend-zero",
            "alpha",
            baseline with
            {
                Name = "Conformance.AlphaBlendZero",
                AlphaMode = MaterialAlphaMode.Blend
            },
            baseSample: new Vector4(1f, 1f, 1f, 0f),
            description: "Zero BLEND alpha is rejected by raster coverage and opaque GI transport."));

        cases.Add(Case(
            "single-sided-front-face",
            "sidedness",
            baseline with { Name = "Conformance.SingleFront", DoubleSided = false },
            frontFacing: true,
            description: "A single-sided front face participates."));
        cases.Add(Case(
            "single-sided-back-face",
            "sidedness",
            baseline with { Name = "Conformance.SingleBack", DoubleSided = false },
            frontFacing: false,
            description: "A single-sided back face is rejected."));
        cases.Add(Case(
            "double-sided-back-face",
            "sidedness",
            baseline with { Name = "Conformance.DoubleBack", DoubleSided = true },
            frontFacing: false,
            description: "A double-sided back face participates without changing diffuse energy."));

        MaterialDefinition unlit = baseline with
        {
            Name = "Conformance.Unlit",
            ShadingModel = MaterialShadingModel.Unlit,
            EmissiveFactor = new Vector3(0.2f, 0.4f, 0.8f),
            EmissiveStrength = 10f
        };
        cases.Add(Case(
            "unlit-visibility-only",
            "unlit",
            unlit,
            description: "Unlit is alpha/sidedness visibility only and neither reflects nor emits into GI."));
        cases.Add(Case(
            "unlit-explicit-emission",
            "unlit",
            unlit with
            {
                Name = "Conformance.UnlitExplicitEmission",
                EmissionGiParticipation = GiParticipationOverride.Enabled
            },
            emissiveSample: new Vector3(0.5f, 0.25f, 1f),
            description: "The named engine override explicitly opts an unlit surface into GI emission."));

        cases.Add(Case(
            "extensions-diffuse-energy-combined",
            "extensions",
            baseline with
            {
                Name = "Conformance.ExtensionEnergy",
                FeatureFlags =
                    MaterialFeatureFlags.Clearcoat |
                    MaterialFeatureFlags.Sheen |
                    MaterialFeatureFlags.Transmission |
                    MaterialFeatureFlags.Ior |
                    MaterialFeatureFlags.Specular,
                Extensions = MaterialExtensionDefinition.None with
                {
                    ClearcoatFactor = 0.75f,
                    SheenColorFactor = new Vector3(0.15f, 0.3f, 0.45f),
                    TransmissionFactor = 0.35f,
                    Ior = 1.65f,
                    SpecularFactor = 0.6f,
                    SpecularColorFactor = new Vector3(0.8f, 0.6f, 0.4f)
                }
            },
            fixtureKind: SampleMaterialGiFixtureKind.ExtensionSurface,
            viewDirection: Normalize(new Vector3(0.8f, 0.6f, 0f)),
            description: "Supported clearcoat, sheen, transmission, IOR, and specular terms reduce diffuse energy."));
        cases.Add(Case(
            "extensions-directional-lobes-owned-elsewhere",
            "extensions",
            baseline with
            {
                Name = "Conformance.DirectionalExtensions",
                FeatureFlags =
                    MaterialFeatureFlags.Anisotropy |
                    MaterialFeatureFlags.Iridescence |
                    MaterialFeatureFlags.Dispersion,
                Extensions = MaterialExtensionDefinition.None with
                {
                    AnisotropyStrength = 0.8f,
                    IridescenceFactor = 1f,
                    Dispersion = 0.5f
                }
            },
            fixtureKind: SampleMaterialGiFixtureKind.ExtensionSurface,
            description: "Directional specular-only lobes do not invent diffuse probe energy."));

        cases.Add(Case(
            "shading-normal-invalid-hemisphere",
            "normal",
            baseline with { Name = "Conformance.InvalidNormalHemisphere" },
            shadingNormal: Vector3.Down,
            description: "An invalid shading-normal hemisphere falls back to the geometric normal."));
        cases.Add(Case(
            "diffuse-brdf-backlit",
            "brdf",
            baseline with { Name = "Conformance.Backlit" },
            nDotL: 0f,
            description: "The local diffuse BRDF is exactly zero for a non-positive incident cosine."));
        cases.Add(Case(
            "diffuse-brdf-angular-grazing",
            "brdf",
            baseline with { Name = "Conformance.DirectionalAngular" },
            viewDirection: Normalize(new Vector3(0.8f, 0.6f, 0f)),
            nDotL: 0.2f,
            description: "Directional diffuse evaluates independent grazing incoming and outgoing Fresnel losses."));
        cases.Add(Case(
            "diffuse-brdf-material-f0",
            "brdf",
            baseline with
            {
                Name = "Conformance.DirectionalMaterialF0",
                FeatureFlags =
                    MaterialFeatureFlags.Ior |
                    MaterialFeatureFlags.Specular,
                Extensions = MaterialExtensionDefinition.None with
                {
                    Ior = 2.4f,
                    TransmissionFactor = 0f,
                    SpecularFactor = 0.85f,
                    SpecularColorFactor = new Vector3(1f, 0.55f, 0.2f)
                }
            },
            fixtureKind: SampleMaterialGiFixtureKind.ExtensionSurface,
            nDotL: 0.35f,
            description: "Directional diffuse consumes the material IOR, specular factor, and tinted dielectric F0."));
        cases.Add(Case(
            "diffuse-brdf-passive-white",
            "brdf",
            baseline with
            {
                Name = "Conformance.DirectionalPassiveWhite",
                BaseColorFactor = Vector4.One,
                MetallicFactor = 0f
            },
            nDotL: 1f,
            description: "A unit-white passive base remains bounded after both Fresnel energy losses."));
        cases.Add(Case(
            "diffuse-brdf-metal-zero",
            "brdf",
            baseline with
            {
                Name = "Conformance.DirectionalMetalZero",
                BaseColorFactor = Vector4.One,
                MetallicFactor = 1f
            },
            nDotL: 1f,
            description: "Fully metallic material has exactly zero directional diffuse BRDF."));

        ValidateCases(cases);
        return cases.ToArray();
    }

    private static SampleMaterialGiConformanceCase Case(
        string name,
        string group,
        MaterialDefinition material,
        Vector4? baseSample = null,
        Vector3? metallicRoughnessSample = null,
        float occlusionSample = 1f,
        Vector3? emissiveSample = null,
        Vector4? vertexColor = null,
        Vector3? geometricNormal = null,
        Vector3? shadingNormal = null,
        Vector3? viewDirection = null,
        float nDotL = 0.7f,
        bool frontFacing = true,
        Vector3? irradiance = null,
        SampleMaterialGiFixtureKind fixtureKind = SampleMaterialGiFixtureKind.AnalyticSurface,
        string description = "")
    {
        return new SampleMaterialGiConformanceCase(
            name,
            group,
            description,
            fixtureKind,
            material,
            baseSample ?? Vector4.One,
            metallicRoughnessSample ?? Vector3.One,
            occlusionSample,
            emissiveSample ?? Vector3.One,
            vertexColor ?? Vector4.One,
            geometricNormal ?? Vector3.UnitY,
            shadingNormal ?? Vector3.UnitY,
            viewDirection ?? Vector3.UnitY,
            nDotL,
            frontFacing,
            irradiance ?? new Vector3(1.5f, 0.75f, 0.25f));
    }

    private static void ValidateCases(IReadOnlyList<SampleMaterialGiConformanceCase> cases)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SampleMaterialGiConformanceCase value in cases)
        {
            if (string.IsNullOrWhiteSpace(value.Name) || !names.Add(value.Name))
                throw new InvalidOperationException("Every material/GI conformance case needs a unique stable name.");
            RequireValue(value.Group, nameof(value.Group));
            RequireValue(value.Description, nameof(value.Description));
            ArgumentNullException.ThrowIfNull(value.Material);
            _ = MaterialDefinitionValidator.ValidateAndNormalize(value.Material);
            EnsureFinite(value.BaseColorSample, value.Name);
            EnsureFinite(value.MetallicRoughnessSample, value.Name);
            EnsureFinite(value.OcclusionSample, value.Name);
            EnsureFinite(value.EmissiveSample, value.Name);
            EnsureFinite(value.VertexColor, value.Name);
            EnsureFinite(value.GeometricNormal, value.Name);
            EnsureFinite(value.ShadingNormal, value.Name);
            EnsureFinite(value.ViewDirection, value.Name);
            EnsureFinite(value.NdotL, value.Name);
            EnsureFinite(value.Irradiance, value.Name);
        }
    }

    private static string ComputeFingerprint()
    {
        var builder = new StringBuilder(16_384);
        Append(builder, CurrentSchemaVersion);
        Append(builder, SceneFingerprint);
        Append(builder, SceneKind.ToString());
        Append(builder, Scenario.ToString());
        Append(builder, LockedWidth);
        Append(builder, LockedHeight);
        Append(builder, WarmupFrameCount);
        Append(builder, FixedRandomSeed);
        Append(builder, LockedExposure);
        AppendCamera(builder, Camera);
        Append(builder, DirectionalLightDirection);
        Append(builder, DirectionalLightColor);
        Append(builder, DirectionalLightIntensity);
        Append(builder, RendererSettings.QualityPreset.ToString());
        Append(builder, RendererSettings.GlobalIlluminationEnabled ? 1 : 0);
        Append(builder, RendererSettings.DdgiEnabled ? 1 : 0);
        Append(builder, RendererSettings.SsgiEnabled ? 1 : 0);
        Append(builder, RendererSettings.FarFieldEnabled ? 1 : 0);
        Append(builder, RendererSettings.CaptureColorSpace);
        Append(builder, RendererSettings.ExposureAppliedToLinearArtifacts ? 1 : 0);
        Append(builder, RendererSettings.ToneMappingAppliedToLinearArtifacts ? 1 : 0);
        Append(builder, SemanticEvidence.Fingerprint);
        foreach (SampleMaterialGiCaptureOutput output in RequiredOutputs)
        {
            Append(builder, output.Signal.ToString());
            Append(builder, output.FileStem);
            Append(builder, output.SourceAttachment);
            Append(builder, output.GlobalIlluminationDebugView.ToString());
            Append(builder, output.MaterialDebugView.ToString());
            Append(builder, output.Description);
        }

        foreach (SampleMaterialGiConformanceCase value in Cases)
            AppendCase(builder, value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string ComputeCaseFingerprint()
    {
        var builder = new StringBuilder(16_384);
        Append(builder, "material-gi-conformance-cases/v1");
        foreach (SampleMaterialGiConformanceCase value in Cases)
            AppendCase(builder, value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendCase(
        StringBuilder builder,
        SampleMaterialGiConformanceCase value)
    {
        Append(builder, value.Name);
        Append(builder, value.Group);
        Append(builder, value.Description);
        Append(builder, value.FixtureKind.ToString());
        AppendMaterial(builder, value.Material);
        Append(builder, value.BaseColorSample);
        Append(builder, value.MetallicRoughnessSample);
        Append(builder, value.OcclusionSample);
        Append(builder, value.EmissiveSample);
        Append(builder, value.VertexColor);
        Append(builder, value.GeometricNormal);
        Append(builder, value.ShadingNormal);
        Append(builder, value.ViewDirection);
        Append(builder, value.NdotL);
        Append(builder, value.FrontFacing ? 1 : 0);
        Append(builder, value.Irradiance);
    }

    private static void AppendMaterial(StringBuilder builder, MaterialDefinition material)
    {
        Append(builder, material.Name);
        Append(builder, material.BaseColorFactor);
        Append(builder, material.EmissiveFactor);
        Append(builder, material.EmissiveStrength);
        Append(builder, material.MetallicFactor);
        Append(builder, material.RoughnessFactor);
        Append(builder, material.OcclusionStrength);
        Append(builder, material.NormalScale);
        Append(builder, material.AlphaMode.ToString());
        Append(builder, material.AlphaCutoff);
        Append(builder, material.DoubleSided ? 1 : 0);
        Append(builder, material.ReceivesShadows ? 1 : 0);
        Append(builder, material.RenderBlendModeOverride?.ToString() ?? "default");
        Append(builder, material.ShadingModel.ToString());
        Append(builder, (uint)material.FeatureFlags);
        Append(builder, material.DiffuseGiParticipation.ToString());
        Append(builder, material.EmissionGiParticipation.ToString());
        Append(builder, material.IsGeometryDecal ? 1 : 0);
        Append(builder, material.DecalLayer);
        Append(builder, material.DecalDepthBias);
        AppendBinding(builder, material.BaseColor);
        AppendBinding(builder, material.Normal);
        AppendBinding(builder, material.MetallicRoughness);
        AppendBinding(builder, material.Occlusion);
        AppendBinding(builder, material.Emissive);
        MaterialExtensionDefinition extension = material.Extensions;
        Append(builder, extension.ClearcoatFactor);
        Append(builder, extension.ClearcoatRoughness);
        Append(builder, extension.ClearcoatNormalScale);
        Append(builder, extension.SheenColorFactor);
        Append(builder, extension.SheenRoughness);
        Append(builder, extension.AnisotropyStrength);
        Append(builder, extension.AnisotropyRotation);
        Append(builder, extension.TransmissionFactor);
        Append(builder, extension.Ior);
        Append(builder, extension.ThicknessFactor);
        Append(builder, extension.AttenuationDistance);
        Append(builder, extension.AttenuationColor);
        Append(builder, extension.SpecularFactor);
        Append(builder, extension.SpecularColorFactor);
        Append(builder, extension.IridescenceFactor);
        Append(builder, extension.IridescenceIor);
        Append(builder, extension.IridescenceThicknessMinimum);
        Append(builder, extension.IridescenceThicknessMaximum);
        Append(builder, extension.Dispersion);
        Append(builder, extension.SubsurfaceColor);
        Append(builder, extension.SubsurfaceStrength);
        AppendBinding(builder, extension.Clearcoat);
        AppendBinding(builder, extension.ClearcoatRoughnessTexture);
        AppendBinding(builder, extension.ClearcoatNormal);
        AppendBinding(builder, extension.SheenColor);
        AppendBinding(builder, extension.SheenRoughnessTexture);
        AppendBinding(builder, extension.Anisotropy);
        AppendBinding(builder, extension.Transmission);
        AppendBinding(builder, extension.Thickness);
        AppendBinding(builder, extension.Specular);
        AppendBinding(builder, extension.SpecularColor);
        AppendBinding(builder, extension.Iridescence);
        AppendBinding(builder, extension.IridescenceThickness);
        AppendBinding(builder, extension.Subsurface);
    }

    private static void AppendBinding(StringBuilder builder, MaterialTextureBinding binding)
    {
        Append(builder, binding.Texture.Index);
        Append(builder, binding.Texture.Generation);
        Append(builder, binding.Sampler.WrapU.ToString());
        Append(builder, binding.Sampler.WrapV.ToString());
        Append(builder, binding.Sampler.MinFilter.ToString());
        Append(builder, binding.Sampler.MagFilter.ToString());
        Append(builder, binding.Sampler.MipFilter.ToString());
        Append(builder, binding.Sampler.MaxAnisotropy);
        Append(builder, binding.TexCoordSet);
        Append(builder, binding.Offset.X);
        Append(builder, binding.Offset.Y);
        Append(builder, binding.Scale.X);
        Append(builder, binding.Scale.Y);
        Append(builder, binding.RotationRadians);
    }

    private static void AppendCamera(StringBuilder builder, SampleSponzaGiCameraBookmark camera)
    {
        Append(builder, camera.Name);
        Append(builder, camera.Position);
        Append(builder, camera.Yaw);
        Append(builder, camera.Pitch);
        Append(builder, camera.FieldOfView);
        Append(builder, camera.NearPlane);
        Append(builder, camera.FarPlane);
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value).Append('\n');

    private static void Append(StringBuilder builder, int value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Append(StringBuilder builder, uint value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Append(StringBuilder builder, float value) =>
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

    private static void Append(StringBuilder builder, Vector3 value)
    {
        Append(builder, value.X);
        Append(builder, value.Y);
        Append(builder, value.Z);
    }

    private static void Append(StringBuilder builder, Vector4 value)
    {
        Append(builder, value.X);
        Append(builder, value.Y);
        Append(builder, value.Z);
        Append(builder, value.W);
    }

    private static Vector3 Normalize(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return value / MathF.Sqrt(lengthSquared);
    }

    private static void EnsureFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new InvalidOperationException($"Conformance case '{name}' contains a non-finite scalar.");
    }

    private static void EnsureFinite(Vector3 value, string name)
    {
        EnsureFinite(value.X, name);
        EnsureFinite(value.Y, name);
        EnsureFinite(value.Z, name);
    }

    private static void EnsureFinite(Vector4 value, string name)
    {
        EnsureFinite(value.X, name);
        EnsureFinite(value.Y, name);
        EnsureFinite(value.Z, name);
        EnsureFinite(value.W, name);
    }

    private static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
    }

    private static void RequireSha256(string value, string parameterName)
    {
        RequireValue(value, parameterName);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A lowercase or uppercase 64-character SHA-256 value is required.", parameterName);
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        SampleEvidenceFileIo.WriteAtomic(
            path,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Material/GI conformance report");
    }
}
