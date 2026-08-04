using System.Security.Cryptography;
using System.Text;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Animation;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

/// <summary>
/// Phase-0 fixture categories. These names are serialized into the capture
/// contract and therefore form part of the stable evidence surface.
/// </summary>
public enum SampleMaterialGiSceneFixtureCategory : byte
{
    WhiteDielectricCornellBox = 0,
    ColoredDielectricCornellBox = 1,
    MetallicSweep = 2,
    RoughnessAndDielectricF0Sweep = 3,
    SparseCheckerEmissionSweep = 4,
    SeparateUvOcclusion = 5,
    SingleAndDoubleSidedCards = 6,
    StaticAndSkinnedAlphaMaskCards = 7,
    UnlitSurface = 8,
    NearCompactFarTransitionCorridor = 9,
    LiveEditMaterialWall = 10,
    SimpleDdgiReceiver = 11,
    SupplementalOracleSurface = 12,
    WindingSidednessAndAlphaEvidence = 13
}

public enum SampleMaterialGiScenePrimitive : byte
{
    Box = 0,
    Sphere = 1,
    Card = 2,
    SkinnedCard = 3
}

public enum SampleMaterialGiSceneMaterialPreset : byte
{
    CatalogCase = 0,
    CornellWhite = 1,
    CornellRed = 2,
    CornellGreen = 3,
    TransitionReference = 4,
    LiveEditBaseColor = 5,
    LiveEditMetallic = 6,
    LiveEditEmission = 7,
    LiveEditAlphaCoverage = 8,
    HybridReceiver = 9,
    HybridBlocker = 10,
    HybridDetailEmitter = 11,
    SemanticBackdrop = 12,
    SemanticSingleSided = 13,
    SemanticDoubleSided = 14,
    SemanticSkinnedMaskSingleSided = 15,
    SemanticSkinnedMaskDoubleSided = 16
}

/// <summary>
/// A decomposed transform is used instead of a matrix so the JSON capture
/// contract is reviewable and structural tests can reject non-finite or
/// degenerate fixture placement.
/// </summary>
public readonly record struct SampleMaterialGiSceneTransform(
    CoreVector3 Position,
    CoreVector3 Scale,
    CoreVector3 RotationRadians)
{
    public CoreMatrix4x4 ToWorldMatrix() =>
        CoreMatrix4x4.CreateScale(Scale) *
        CoreMatrix4x4.CreateRotationX(RotationRadians.X) *
        CoreMatrix4x4.CreateRotationY(RotationRadians.Y) *
        CoreMatrix4x4.CreateRotationZ(RotationRadians.Z) *
        CoreMatrix4x4.CreateTranslation(Position);
}

/// <summary>
/// One stable scene object. CatalogCaseName is non-null exactly when the
/// object renders a numerical CPU/GPU oracle case.
/// </summary>
public sealed record SampleMaterialGiSceneFixture(
    string StableId,
    SampleMaterialGiSceneFixtureCategory Category,
    SampleMaterialGiScenePrimitive Primitive,
    SampleMaterialGiSceneMaterialPreset MaterialPreset,
    SampleMaterialGiSceneTransform Transform,
    string Role,
    string? CatalogCaseName = null);

public sealed record SampleMaterialGiConformanceSceneBuildSummary(
    int FixtureCount,
    int CatalogCaseFixtureCount,
    int SkinnedFixtureCount,
    int LiveEditTargetCount,
    string SceneFingerprint);

public sealed record SampleMaterialGiProbeVolumeFixture(
    string StableId,
    CoreVector3 Origin,
    CoreVector3 Size,
    int ProbeCountX,
    int ProbeCountY,
    int ProbeCountZ,
    int RaysPerProbe,
    int DirtyRaysPerProbe,
    int MaxProbeUpdatesPerFrame,
    float MaxRayDistance,
    float NormalBias,
    float ViewBias,
    float Intensity,
    float Hysteresis,
    float SteadyHysteresis,
    float DirtyHysteresis,
    int Priority,
    int UpdatePriority,
    float BlendDistance);

/// <summary>
/// Pure construction and validation of the Phase-0 scene manifest. Runtime
/// mesh/material allocation consumes this output but is not needed to test it.
/// </summary>
public static class SampleMaterialGiConformanceSceneLayout
{
    public const string CurrentSchemaVersion = "material-gi-conformance-scene/v2";
    public const int LiveEditSettleUpdateCount = 4;
    public const string ProbeVolumeStableId = "material-gi.probe-volume.main";
    public const string SemanticReferenceSingleFrontId =
        "material-gi.semantic.reference-single-front";
    public const string SemanticMirroredSingleFrontId =
        "material-gi.semantic.mirrored-single-front";
    public const string SemanticMirroredSingleBackId =
        "material-gi.semantic.mirrored-single-back";
    public const string SemanticMirroredDoubleBackId =
        "material-gi.semantic.mirrored-double-back";
    public const string SemanticSkinnedMaskSingleFrontId =
        "material-gi.semantic.skinned-mask-single-front";
    public const string SemanticSkinnedMaskSingleBackId =
        "material-gi.semantic.skinned-mask-single-back";
    public const string SemanticSkinnedMaskDoubleBackId =
        "material-gi.semantic.skinned-mask-double-back";

    public static SampleMaterialGiProbeVolumeFixture ProbeVolume { get; } = new(
        ProbeVolumeStableId,
        new CoreVector3(-5.2f, -0.2f, -5.2f),
        new CoreVector3(10.4f, 3.5f, 10.4f),
        ProbeCountX: 13,
        ProbeCountY: 5,
        ProbeCountZ: 13,
        RaysPerProbe: 192,
        DirtyRaysPerProbe: 256,
        MaxProbeUpdatesPerFrame: 845,
        MaxRayDistance: 15f,
        NormalBias: 0.04f,
        ViewBias: 0.1f,
        Intensity: 1f,
        Hysteresis: 0.82f,
        SteadyHysteresis: 0.94f,
        DirtyHysteresis: 0.5f,
        Priority: 192,
        UpdatePriority: 192,
        BlendDistance: 0.9f);

    public static IReadOnlyList<SampleMaterialGiSceneFixtureCategory> RequiredCategories { get; } =
        Array.AsReadOnly(
        [
            SampleMaterialGiSceneFixtureCategory.WhiteDielectricCornellBox,
            SampleMaterialGiSceneFixtureCategory.ColoredDielectricCornellBox,
            SampleMaterialGiSceneFixtureCategory.MetallicSweep,
            SampleMaterialGiSceneFixtureCategory.RoughnessAndDielectricF0Sweep,
            SampleMaterialGiSceneFixtureCategory.SparseCheckerEmissionSweep,
            SampleMaterialGiSceneFixtureCategory.SeparateUvOcclusion,
            SampleMaterialGiSceneFixtureCategory.SingleAndDoubleSidedCards,
            SampleMaterialGiSceneFixtureCategory.StaticAndSkinnedAlphaMaskCards,
            SampleMaterialGiSceneFixtureCategory.UnlitSurface,
            SampleMaterialGiSceneFixtureCategory.NearCompactFarTransitionCorridor,
            SampleMaterialGiSceneFixtureCategory.LiveEditMaterialWall,
            SampleMaterialGiSceneFixtureCategory.SimpleDdgiReceiver,
            SampleMaterialGiSceneFixtureCategory.WindingSidednessAndAlphaEvidence
        ]);

    public static SampleMaterialGiSceneFixture[] CreateFixtures(
        IReadOnlyList<SampleMaterialGiConformanceCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var fixtures = new List<SampleMaterialGiSceneFixture>(cases.Count + 32);

        AddCornellRoom(
            fixtures,
            SampleMaterialGiSceneFixtureCategory.WhiteDielectricCornellBox,
            "cornell.white",
            centerX: -2.25f,
            coloredWalls: false);
        AddCornellRoom(
            fixtures,
            SampleMaterialGiSceneFixtureCategory.ColoredDielectricCornellBox,
            "cornell.colored",
            centerX: 2.25f,
            coloredWalls: true);
        AddTransitionCorridor(fixtures);
        AddLiveEditWall(fixtures);
        AddSimpleDdgiReceiverFixture(fixtures);
        AddWindingSidednessAndAlphaEvidence(fixtures);

        for (int index = 0; index < cases.Count; index++)
        {
            SampleMaterialGiConformanceCase materialCase = cases[index];
            int column = index % 10;
            int row = index / 10;
            SampleMaterialGiSceneFixtureCategory category = ResolveCategory(materialCase);
            SampleMaterialGiScenePrimitive primitive = ResolvePrimitive(materialCase);
            float rotationY = materialCase.Group == "sidedness" &&
                              materialCase.Name.Contains("back-face", StringComparison.Ordinal)
                ? MathF.PI
                : 0f;
            CoreVector3 scale = primitive == SampleMaterialGiScenePrimitive.Sphere
                ? new CoreVector3(0.22f)
                : new CoreVector3(0.48f, 0.48f, 1f);

            fixtures.Add(new SampleMaterialGiSceneFixture(
                $"material-gi.case.{materialCase.Name}",
                category,
                primitive,
                SampleMaterialGiSceneMaterialPreset.CatalogCase,
                new SampleMaterialGiSceneTransform(
                    new CoreVector3(-3.6f + column * 0.8f, 0.38f + row * 0.72f, 1.45f),
                    scale,
                    new CoreVector3(0f, rotationY, 0f)),
                $"Numerical oracle case '{materialCase.Name}'.",
                materialCase.Name));
        }

        Validate(fixtures, cases);
        return fixtures.ToArray();
    }

    public static string ComputeFingerprint(
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures,
        string caseFingerprint)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        if (string.IsNullOrWhiteSpace(caseFingerprint) ||
            caseFingerprint.Length != 64 ||
            caseFingerprint.Any(static value => !Uri.IsHexDigit(value)))
        {
            throw new ArgumentException(
                "The scene fingerprint requires the complete SHA-256 case identity.",
                nameof(caseFingerprint));
        }
        var builder = new StringBuilder(fixtures.Count * 192);
        Append(builder, CurrentSchemaVersion);
        Append(builder, caseFingerprint.ToLowerInvariant());
        AppendProbeVolume(builder, ProbeVolume);
        Append(builder, LiveEditSettleUpdateCount);
        Append(builder, SampleMaterialGiProceduralTextureSet.SchemaVersion);
        Append(builder, SampleMaterialGiProceduralTextureSet.ContentFingerprint);
        foreach (SampleMaterialGiSceneMaterialPreset preset in
                 Enum.GetValues<SampleMaterialGiSceneMaterialPreset>())
        {
            if (preset == SampleMaterialGiSceneMaterialPreset.CatalogCase)
                continue;
            Append(builder, preset.ToString());
            AppendMaterial(builder, CreatePresetMaterial(preset));
            if (preset is
                SampleMaterialGiSceneMaterialPreset.LiveEditBaseColor or
                SampleMaterialGiSceneMaterialPreset.LiveEditMetallic or
                SampleMaterialGiSceneMaterialPreset.LiveEditEmission or
                SampleMaterialGiSceneMaterialPreset.LiveEditAlphaCoverage)
            {
                AppendMaterial(builder, CreatePresetMaterial(preset, finalLiveEditState: true));
            }
        }
        foreach (SampleMaterialGiSceneFixture fixture in fixtures)
        {
            Append(builder, fixture.StableId);
            Append(builder, fixture.Category.ToString());
            Append(builder, fixture.Primitive.ToString());
            Append(builder, fixture.MaterialPreset.ToString());
            Append(builder, fixture.Transform.Position);
            Append(builder, fixture.Transform.Scale);
            Append(builder, fixture.Transform.RotationRadians);
            Append(builder, fixture.Role);
            Append(builder, fixture.CatalogCaseName ?? string.Empty);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    public static Guid CreateStableEntityId(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"Njulf.MaterialGi.Scene:{stableId}"), hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        // RFC 4122 variant/version bits make diagnostics recognize these as
        // name-derived identifiers while retaining deterministic payload bits.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    public static MaterialDefinition CreatePresetMaterial(
        SampleMaterialGiSceneMaterialPreset preset,
        bool finalLiveEditState = false)
    {
        MaterialDefinition material = preset switch
        {
            SampleMaterialGiSceneMaterialPreset.CornellWhite => CreatePbr(
                "Conformance.Scene.Cornell.White",
                new CoreVector3(0.74f, 0.74f, 0.70f),
                roughness: 0.92f),
            SampleMaterialGiSceneMaterialPreset.CornellRed => CreatePbr(
                "Conformance.Scene.Cornell.Red",
                new CoreVector3(0.72f, 0.055f, 0.035f),
                roughness: 0.94f),
            SampleMaterialGiSceneMaterialPreset.CornellGreen => CreatePbr(
                "Conformance.Scene.Cornell.Green",
                new CoreVector3(0.045f, 0.48f, 0.09f),
                roughness: 0.94f),
            SampleMaterialGiSceneMaterialPreset.TransitionReference => CreatePbr(
                "Conformance.Scene.Transition.Reference",
                new CoreVector3(0.38f, 0.56f, 0.82f),
                roughness: 0.78f),
            SampleMaterialGiSceneMaterialPreset.LiveEditBaseColor => CreatePbr(
                "Conformance.Scene.LiveEdit.BaseColor",
                finalLiveEditState
                    ? new CoreVector3(0.82f, 0.11f, 0.055f)
                    : new CoreVector3(0.42f, 0.42f, 0.42f),
                roughness: 0.68f),
            SampleMaterialGiSceneMaterialPreset.LiveEditMetallic => CreatePbr(
                "Conformance.Scene.LiveEdit.Metallic",
                new CoreVector3(0.68f, 0.58f, 0.34f),
                metallic: finalLiveEditState ? 1f : 0f,
                roughness: 0.3f),
            SampleMaterialGiSceneMaterialPreset.LiveEditEmission => CreatePbr(
                "Conformance.Scene.LiveEdit.Emission",
                new CoreVector3(0.08f, 0.08f, 0.09f),
                roughness: 0.55f) with
            {
                EmissiveFactor = finalLiveEditState
                    ? new CoreVector3(0.15f, 0.6f, 1f)
                    : CoreVector3.Zero,
                EmissiveStrength = finalLiveEditState ? 4f : 1f
            },
            SampleMaterialGiSceneMaterialPreset.LiveEditAlphaCoverage => CreatePbr(
                "Conformance.Scene.LiveEdit.AlphaCoverage",
                new CoreVector3(0.78f, 0.82f, 0.9f),
                roughness: 0.74f) with
            {
                AlphaMode = MaterialAlphaMode.Mask,
                AlphaCutoff = finalLiveEditState ? 0.72f : 0.28f,
                DoubleSided = true
            },
            SampleMaterialGiSceneMaterialPreset.HybridReceiver => CreatePbr(
                "Conformance.Scene.Hybrid.Receiver",
                new CoreVector3(0.68f, 0.69f, 0.72f),
                roughness: 0.9f),
            SampleMaterialGiSceneMaterialPreset.HybridBlocker => CreatePbr(
                "Conformance.Scene.Hybrid.Blocker",
                new CoreVector3(0.16f, 0.18f, 0.22f),
                roughness: 0.82f),
            SampleMaterialGiSceneMaterialPreset.HybridDetailEmitter => CreatePbr(
                "Conformance.Scene.Hybrid.DetailEmitter",
                new CoreVector3(0.04f, 0.04f, 0.04f),
                roughness: 0.65f) with
            {
                EmissiveFactor = new CoreVector3(1f, 0.28f, 0.06f),
                EmissiveStrength = 2.5f,
                ReceivesShadows = false
            },
            SampleMaterialGiSceneMaterialPreset.SemanticBackdrop => CreatePbr(
                "Conformance.Scene.Semantic.Backdrop",
                new CoreVector3(0.045f, 0.12f, 0.28f),
                roughness: 0.95f) with
            {
                // The sidedness debug view renders a double-sided front face
                // cyan, providing an unambiguous surface behind every witness.
                DoubleSided = true
            },
            SampleMaterialGiSceneMaterialPreset.SemanticSingleSided => CreatePbr(
                "Conformance.Scene.Semantic.SingleSided",
                new CoreVector3(0.82f, 0.095f, 0.035f),
                roughness: 0.78f),
            SampleMaterialGiSceneMaterialPreset.SemanticDoubleSided => CreatePbr(
                "Conformance.Scene.Semantic.DoubleSided",
                new CoreVector3(0.82f, 0.095f, 0.035f),
                roughness: 0.78f) with
            {
                DoubleSided = true
            },
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskSingleSided => CreatePbr(
                "Conformance.Scene.Semantic.SkinnedMask.SingleSided",
                new CoreVector3(0.82f, 0.095f, 0.035f),
                roughness: 0.78f) with
            {
                AlphaMode = MaterialAlphaMode.Mask,
                AlphaCutoff = 0.5f
            },
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskDoubleSided => CreatePbr(
                "Conformance.Scene.Semantic.SkinnedMask.DoubleSided",
                new CoreVector3(0.82f, 0.095f, 0.035f),
                roughness: 0.78f) with
            {
                AlphaMode = MaterialAlphaMode.Mask,
                AlphaCutoff = 0.5f,
                DoubleSided = true
            },
            SampleMaterialGiSceneMaterialPreset.CatalogCase =>
                throw new ArgumentOutOfRangeException(
                    nameof(preset),
                    "Catalog-case material definitions are resolved from the conformance catalog."),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
        };

        return MaterialDefinitionValidator.ValidateAndNormalize(material);
    }

    private static MaterialDefinition CreatePbr(
        string name,
        CoreVector3 color,
        float metallic = 0f,
        float roughness = 1f) =>
        new()
        {
            Name = name,
            BaseColorFactor = new CoreVector4(color, 1f),
            MetallicFactor = metallic,
            RoughnessFactor = roughness
        };

    private static void AddCornellRoom(
        ICollection<SampleMaterialGiSceneFixture> fixtures,
        SampleMaterialGiSceneFixtureCategory category,
        string prefix,
        float centerX,
        bool coloredWalls)
    {
        const float centerZ = -1.65f;
        const float width = 3.9f;
        const float height = 2.9f;
        const float depth = 3.4f;
        SampleMaterialGiSceneMaterialPreset left = coloredWalls
            ? SampleMaterialGiSceneMaterialPreset.CornellRed
            : SampleMaterialGiSceneMaterialPreset.CornellWhite;
        SampleMaterialGiSceneMaterialPreset right = coloredWalls
            ? SampleMaterialGiSceneMaterialPreset.CornellGreen
            : SampleMaterialGiSceneMaterialPreset.CornellWhite;

        AddContext(
            fixtures, $"{prefix}.floor", category, SampleMaterialGiSceneMaterialPreset.CornellWhite,
            new CoreVector3(centerX, 0.05f, centerZ), new CoreVector3(width, 0.1f, depth), "Diffuse floor.");
        AddContext(
            fixtures, $"{prefix}.ceiling", category, SampleMaterialGiSceneMaterialPreset.CornellWhite,
            new CoreVector3(centerX, height - 0.05f, centerZ), new CoreVector3(width, 0.1f, depth), "Diffuse ceiling.");
        AddContext(
            fixtures, $"{prefix}.back", category, SampleMaterialGiSceneMaterialPreset.CornellWhite,
            new CoreVector3(centerX, height * 0.5f, centerZ - depth * 0.5f), new CoreVector3(width, height, 0.1f), "Diffuse back wall.");
        AddContext(
            fixtures, $"{prefix}.left", category, left,
            new CoreVector3(centerX - width * 0.5f, height * 0.5f, centerZ), new CoreVector3(0.1f, height, depth), "Left bounce wall.");
        AddContext(
            fixtures, $"{prefix}.right", category, right,
            new CoreVector3(centerX + width * 0.5f, height * 0.5f, centerZ), new CoreVector3(0.1f, height, depth), "Right bounce wall.");
        AddContext(
            fixtures, $"{prefix}.block", category, SampleMaterialGiSceneMaterialPreset.CornellWhite,
            new CoreVector3(centerX + (coloredWalls ? 0.38f : -0.38f), 0.48f, centerZ - 0.25f),
            new CoreVector3(0.78f, 0.96f, 0.78f),
            "Interior bounce block.",
            rotationY: coloredWalls ? -0.31f : 0.27f);
    }

    private static void AddTransitionCorridor(ICollection<SampleMaterialGiSceneFixture> fixtures)
    {
        SampleMaterialGiSceneFixtureCategory category =
            SampleMaterialGiSceneFixtureCategory.NearCompactFarTransitionCorridor;
        AddContext(
            fixtures, "transition.corridor.floor", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(0f, -0.06f, -24f), new CoreVector3(2.2f, 0.08f, 56f),
            "Reference floor spanning detailed, compact, and far-field distances.");
        AddContext(
            fixtures, "transition.corridor.left-rail", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(-1.05f, 0.22f, -24f), new CoreVector3(0.08f, 0.44f, 56f),
            "Left corridor energy reference.");
        AddContext(
            fixtures, "transition.corridor.right-rail", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(1.05f, 0.22f, -24f), new CoreVector3(0.08f, 0.44f, 56f),
            "Right corridor energy reference.");
        AddContext(
            fixtures, "transition.near", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(0f, 0.35f, 3.55f), new CoreVector3(0.52f, 0.7f, 0.52f),
            "Detailed textured-transport marker.");
        AddContext(
            fixtures, "transition.compact", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(0f, 0.7f, -12f), new CoreVector3(1.15f, 1.4f, 1.15f),
            "Compact-transport marker.");
        AddContext(
            fixtures, "transition.far", category,
            SampleMaterialGiSceneMaterialPreset.TransitionReference,
            new CoreVector3(0f, 2.4f, -55f), new CoreVector3(4f, 4.8f, 4f),
            "Far-field transport marker.");
    }

    private static void AddLiveEditWall(ICollection<SampleMaterialGiSceneFixture> fixtures)
    {
        (string Id, SampleMaterialGiSceneMaterialPreset Preset, string Role)[] targets =
        [
            ("live-edit.base-color", SampleMaterialGiSceneMaterialPreset.LiveEditBaseColor, "Transactional base-color edit."),
            ("live-edit.metallic", SampleMaterialGiSceneMaterialPreset.LiveEditMetallic, "Transactional metallic edit."),
            ("live-edit.emission", SampleMaterialGiSceneMaterialPreset.LiveEditEmission, "Transactional emission edit."),
            ("live-edit.alpha-coverage", SampleMaterialGiSceneMaterialPreset.LiveEditAlphaCoverage, "Transactional alpha-coverage edit.")
        ];

        for (int index = 0; index < targets.Length; index++)
        {
            (string id, SampleMaterialGiSceneMaterialPreset preset, string role) = targets[index];
            fixtures.Add(new SampleMaterialGiSceneFixture(
                $"material-gi.{id}",
                SampleMaterialGiSceneFixtureCategory.LiveEditMaterialWall,
                SampleMaterialGiScenePrimitive.Card,
                preset,
                new SampleMaterialGiSceneTransform(
                    new CoreVector3(-4.35f, 0.42f + index * 0.66f, 2.35f),
                    new CoreVector3(0.48f, 0.54f, 1f),
                    CoreVector3.Zero),
                role));
        }
    }

    private static void AddSimpleDdgiReceiverFixture(ICollection<SampleMaterialGiSceneFixture> fixtures)
    {
        SampleMaterialGiSceneFixtureCategory category =
            SampleMaterialGiSceneFixtureCategory.SimpleDdgiReceiver;
        AddContext(
            fixtures, "hybrid-overlap.receiver", category,
            SampleMaterialGiSceneMaterialPreset.HybridReceiver,
            new CoreVector3(3.95f, 0.08f, 3.25f), new CoreVector3(1.35f, 0.12f, 1.55f),
            "Simple DDGI diffuse receiver.");
        AddContext(
            fixtures, "hybrid-overlap.thin-blocker", category,
            SampleMaterialGiSceneMaterialPreset.HybridBlocker,
            new CoreVector3(3.95f, 0.68f, 3.15f), new CoreVector3(0.16f, 1.2f, 1.05f),
            "High-frequency local occluder.");
        fixtures.Add(new SampleMaterialGiSceneFixture(
            "material-gi.hybrid-overlap.detail-emitter",
            category,
            SampleMaterialGiScenePrimitive.Card,
            SampleMaterialGiSceneMaterialPreset.HybridDetailEmitter,
            new SampleMaterialGiSceneTransform(
                new CoreVector3(3.62f, 1.35f, 2.78f),
                new CoreVector3(0.48f, 0.48f, 1f),
                CoreVector3.Zero),
            "Local radiance detail visible to both estimators."));
    }

    private static void AddWindingSidednessAndAlphaEvidence(
        ICollection<SampleMaterialGiSceneFixture> fixtures)
    {
        const float foregroundZ = 3.46f;
        const float backdropZ = 3.36f;
        const float topY = 2.35f;
        const float bottomY = 0.78f;

        AddSemanticWitness(
            fixtures,
            SemanticReferenceSingleFrontId,
            SampleMaterialGiScenePrimitive.Card,
            SampleMaterialGiSceneMaterialPreset.SemanticSingleSided,
            new CoreVector3(-1.8f, topY, foregroundZ),
            mirrored: false,
            backFacing: false,
            role: "Positive-determinant single-sided front-face control.",
            backdropZ: backdropZ);
        AddSemanticWitness(
            fixtures,
            SemanticMirroredSingleFrontId,
            SampleMaterialGiScenePrimitive.Card,
            SampleMaterialGiSceneMaterialPreset.SemanticSingleSided,
            new CoreVector3(-0.6f, topY, foregroundZ),
            mirrored: true,
            backFacing: false,
            role: "Negative-determinant single-sided front face; winding must be corrected.",
            backdropZ: backdropZ);
        AddSemanticWitness(
            fixtures,
            SemanticMirroredSingleBackId,
            SampleMaterialGiScenePrimitive.Card,
            SampleMaterialGiSceneMaterialPreset.SemanticSingleSided,
            new CoreVector3(0.6f, topY, foregroundZ),
            mirrored: true,
            backFacing: true,
            role: "Negative-determinant single-sided back face; corrected winding must remain rejected.",
            backdropZ: backdropZ);
        AddSemanticWitness(
            fixtures,
            SemanticMirroredDoubleBackId,
            SampleMaterialGiScenePrimitive.Card,
            SampleMaterialGiSceneMaterialPreset.SemanticDoubleSided,
            new CoreVector3(1.8f, topY, foregroundZ),
            mirrored: true,
            backFacing: true,
            role: "Negative-determinant double-sided back face; corrected winding must report logical back-facing.",
            backdropZ: backdropZ);

        AddSemanticWitness(
            fixtures,
            SemanticSkinnedMaskSingleFrontId,
            SampleMaterialGiScenePrimitive.SkinnedCard,
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskSingleSided,
            new CoreVector3(-1.25f, bottomY, foregroundZ),
            mirrored: false,
            backFacing: false,
            role: "Skinned alpha-mask single-sided front face with named opaque and discarded regions.",
            backdropZ: backdropZ);
        AddSemanticWitness(
            fixtures,
            SemanticSkinnedMaskSingleBackId,
            SampleMaterialGiScenePrimitive.SkinnedCard,
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskSingleSided,
            new CoreVector3(0f, bottomY, foregroundZ),
            mirrored: false,
            backFacing: true,
            role: "Skinned alpha-mask single-sided back face; an opaque texel region must still be rejected.",
            backdropZ: backdropZ);
        AddSemanticWitness(
            fixtures,
            SemanticSkinnedMaskDoubleBackId,
            SampleMaterialGiScenePrimitive.SkinnedCard,
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskDoubleSided,
            new CoreVector3(1.25f, bottomY, foregroundZ),
            mirrored: false,
            backFacing: true,
            role: "Skinned alpha-mask double-sided back face with named opaque and discarded regions.",
            backdropZ: backdropZ);
    }

    private static void AddSemanticWitness(
        ICollection<SampleMaterialGiSceneFixture> fixtures,
        string stableId,
        SampleMaterialGiScenePrimitive primitive,
        SampleMaterialGiSceneMaterialPreset material,
        CoreVector3 position,
        bool mirrored,
        bool backFacing,
        string role,
        float backdropZ)
    {
        fixtures.Add(new SampleMaterialGiSceneFixture(
            $"{stableId}.backdrop",
            SampleMaterialGiSceneFixtureCategory.WindingSidednessAndAlphaEvidence,
            SampleMaterialGiScenePrimitive.Box,
            SampleMaterialGiSceneMaterialPreset.SemanticBackdrop,
            new SampleMaterialGiSceneTransform(
                new CoreVector3(position.X, position.Y, backdropZ),
                new CoreVector3(0.94f, 0.94f, 0.08f),
                CoreVector3.Zero),
            $"Double-sided semantic backdrop for '{stableId}'."));
        fixtures.Add(new SampleMaterialGiSceneFixture(
            stableId,
            SampleMaterialGiSceneFixtureCategory.WindingSidednessAndAlphaEvidence,
            primitive,
            material,
            new SampleMaterialGiSceneTransform(
                position,
                new CoreVector3(mirrored ? -0.76f : 0.76f, 0.76f, 1f),
                new CoreVector3(0f, backFacing ? MathF.PI : 0f, 0f)),
            role));
    }

    private static void AddContext(
        ICollection<SampleMaterialGiSceneFixture> fixtures,
        string suffix,
        SampleMaterialGiSceneFixtureCategory category,
        SampleMaterialGiSceneMaterialPreset material,
        CoreVector3 position,
        CoreVector3 scale,
        string role,
        float rotationY = 0f)
    {
        fixtures.Add(new SampleMaterialGiSceneFixture(
            $"material-gi.{suffix}",
            category,
            SampleMaterialGiScenePrimitive.Box,
            material,
            new SampleMaterialGiSceneTransform(
                position,
                scale,
                new CoreVector3(0f, rotationY, 0f)),
            role));
    }

    private static SampleMaterialGiSceneFixtureCategory ResolveCategory(
        SampleMaterialGiConformanceCase materialCase)
    {
        return materialCase.Group switch
        {
            "metallic-sweep" => SampleMaterialGiSceneFixtureCategory.MetallicSweep,
            "roughness-sweep" or "dielectric-f0-sweep" =>
                SampleMaterialGiSceneFixtureCategory.RoughnessAndDielectricF0Sweep,
            "emission-sweep" => SampleMaterialGiSceneFixtureCategory.SparseCheckerEmissionSweep,
            "occlusion" => SampleMaterialGiSceneFixtureCategory.SeparateUvOcclusion,
            "alpha" => SampleMaterialGiSceneFixtureCategory.StaticAndSkinnedAlphaMaskCards,
            "sidedness" => SampleMaterialGiSceneFixtureCategory.SingleAndDoubleSidedCards,
            "unlit" => SampleMaterialGiSceneFixtureCategory.UnlitSurface,
            _ => SampleMaterialGiSceneFixtureCategory.SupplementalOracleSurface
        };
    }

    private static SampleMaterialGiScenePrimitive ResolvePrimitive(
        SampleMaterialGiConformanceCase materialCase)
    {
        if (materialCase.FixtureKind == SampleMaterialGiFixtureKind.SkinnedAlphaCard)
            return SampleMaterialGiScenePrimitive.SkinnedCard;
        if (materialCase.FixtureKind is
            SampleMaterialGiFixtureKind.StaticAlphaCard or
            SampleMaterialGiFixtureKind.SeparateUvOcclusion)
        {
            return SampleMaterialGiScenePrimitive.Card;
        }
        if (materialCase.Group is "emission-sweep" or "sidedness" or "unlit")
            return SampleMaterialGiScenePrimitive.Card;
        return SampleMaterialGiScenePrimitive.Sphere;
    }

    private static void Validate(
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures,
        IReadOnlyList<SampleMaterialGiConformanceCase> cases)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var entityIds = new HashSet<Guid>();
        foreach (SampleMaterialGiSceneFixture fixture in fixtures)
        {
            if (string.IsNullOrWhiteSpace(fixture.StableId) || !ids.Add(fixture.StableId))
                throw new InvalidOperationException("Every material/GI scene fixture needs a unique stable ID.");
            if (!entityIds.Add(CreateStableEntityId(fixture.StableId)))
                throw new InvalidOperationException($"Fixture '{fixture.StableId}' has a duplicate stable entity ID.");
            if (string.IsNullOrWhiteSpace(fixture.Role))
                throw new InvalidOperationException($"Fixture '{fixture.StableId}' needs a documented role.");
            ValidateTransform(fixture);
            if (fixture.MaterialPreset == SampleMaterialGiSceneMaterialPreset.CatalogCase &&
                string.IsNullOrWhiteSpace(fixture.CatalogCaseName))
            {
                throw new InvalidOperationException(
                    $"Catalog fixture '{fixture.StableId}' does not identify its oracle case.");
            }
            if (fixture.MaterialPreset != SampleMaterialGiSceneMaterialPreset.CatalogCase &&
                fixture.CatalogCaseName != null)
            {
                throw new InvalidOperationException(
                    $"Context fixture '{fixture.StableId}' unexpectedly identifies an oracle case.");
            }
        }

        foreach (SampleMaterialGiSceneFixtureCategory required in RequiredCategories)
        {
            if (!fixtures.Any(value => value.Category == required))
                throw new InvalidOperationException($"Required Phase-0 fixture category '{required}' is absent.");
        }

        string[] expectedCases = cases
            .Select(static value => value.Name)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] sceneCases = fixtures
            .Where(static value => value.CatalogCaseName != null)
            .Select(static value => value.CatalogCaseName!)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (!expectedCases.SequenceEqual(sceneCases, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The rendered material/GI scene must contain every numerical oracle case exactly once.");
        }

        int liveEditTargets = fixtures.Count(
            static value => value.Category == SampleMaterialGiSceneFixtureCategory.LiveEditMaterialWall);
        if (liveEditTargets != LiveEditSettleUpdateCount)
            throw new InvalidOperationException("The live-edit wall and deterministic edit sequence are out of sync.");

        SampleMaterialGiSceneFixture[] transitionMarkers = fixtures
            .Where(static value => value.StableId is
                "material-gi.transition.near" or
                "material-gi.transition.compact" or
                "material-gi.transition.far")
            .OrderByDescending(static value => value.Transform.Position.Z)
            .ToArray();
        if (transitionMarkers.Length != 3 ||
            !(transitionMarkers[0].Transform.Position.Z >
              transitionMarkers[1].Transform.Position.Z &&
              transitionMarkers[1].Transform.Position.Z >
              transitionMarkers[2].Transform.Position.Z))
        {
            throw new InvalidOperationException("Near/compact/far transition markers are not ordered by distance.");
        }

        ValidateSemanticWitnesses(fixtures);
    }

    private static void ValidateSemanticWitnesses(
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures)
    {
        string[] mirroredIds =
        [
            SemanticMirroredSingleFrontId,
            SemanticMirroredSingleBackId,
            SemanticMirroredDoubleBackId
        ];
        foreach (string stableId in mirroredIds)
        {
            SampleMaterialGiSceneFixture fixture = fixtures.SingleOrDefault(value =>
                    string.Equals(value.StableId, stableId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Mirrored semantic witness '{stableId}' is absent.");
            float determinant =
                fixture.Transform.Scale.X *
                fixture.Transform.Scale.Y *
                fixture.Transform.Scale.Z;
            if (!(determinant < 0f))
            {
                throw new InvalidOperationException(
                    $"Semantic witness '{stableId}' must have a negative determinant.");
            }
        }

        string[] skinnedIds =
        [
            SemanticSkinnedMaskSingleFrontId,
            SemanticSkinnedMaskSingleBackId,
            SemanticSkinnedMaskDoubleBackId
        ];
        foreach (string stableId in skinnedIds)
        {
            SampleMaterialGiSceneFixture fixture = fixtures.SingleOrDefault(value =>
                    string.Equals(value.StableId, stableId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Skinned semantic witness '{stableId}' is absent.");
            if (fixture.Primitive != SampleMaterialGiScenePrimitive.SkinnedCard)
            {
                throw new InvalidOperationException(
                    $"Semantic witness '{stableId}' must use the skinned-card render path.");
            }
        }
    }

    private static void ValidateTransform(SampleMaterialGiSceneFixture fixture)
    {
        SampleMaterialGiSceneTransform transform = fixture.Transform;
        if (!IsFinite(transform.Position) ||
            !IsFinite(transform.Scale) ||
            !IsFinite(transform.RotationRadians))
        {
            throw new InvalidOperationException($"Fixture '{fixture.StableId}' has a non-finite transform.");
        }
        if (MathF.Abs(transform.Scale.X) <= 1e-6f ||
            MathF.Abs(transform.Scale.Y) <= 1e-6f ||
            MathF.Abs(transform.Scale.Z) <= 1e-6f)
            throw new InvalidOperationException($"Fixture '{fixture.StableId}' has a degenerate transform.");
    }

    private static bool IsFinite(CoreVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void AppendProbeVolume(
        StringBuilder builder,
        SampleMaterialGiProbeVolumeFixture volume)
    {
        Append(builder, volume.StableId);
        Append(builder, volume.Origin);
        Append(builder, volume.Size);
        Append(builder, volume.ProbeCountX);
        Append(builder, volume.ProbeCountY);
        Append(builder, volume.ProbeCountZ);
        Append(builder, volume.RaysPerProbe);
        Append(builder, volume.DirtyRaysPerProbe);
        Append(builder, volume.MaxProbeUpdatesPerFrame);
        Append(builder, volume.MaxRayDistance);
        Append(builder, volume.NormalBias);
        Append(builder, volume.ViewBias);
        Append(builder, volume.Intensity);
        Append(builder, volume.Hysteresis);
        Append(builder, volume.SteadyHysteresis);
        Append(builder, volume.DirtyHysteresis);
        Append(builder, volume.Priority);
        Append(builder, volume.UpdatePriority);
        Append(builder, volume.BlendDistance);
    }

    private static void AppendMaterial(StringBuilder builder, MaterialDefinition material)
    {
        Append(builder, material.Name);
        Append(builder, material.BaseColorFactor.X);
        Append(builder, material.BaseColorFactor.Y);
        Append(builder, material.BaseColorFactor.Z);
        Append(builder, material.BaseColorFactor.W);
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
        Append(builder, material.ShadingModel.ToString());
        Append(builder, (int)material.FeatureFlags);
        Append(builder, material.DiffuseGiParticipation.ToString());
        Append(builder, material.EmissionGiParticipation.ToString());
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, float value) =>
        Append(builder, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, CoreVector3 value)
    {
        Append(builder, value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, value.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Small source-resolution textures deliberately exercise independent
/// bindings and compact statistics without relying on external assets.
/// </summary>
internal static class SampleMaterialGiProceduralTextureSet
{
    public const string SchemaVersion = "material-gi-procedural-textures/v1";
    public const int Width = 8;
    public const int Height = 8;

    public static byte[] EmissiveSparseCheckerRgba { get; } = CreateEmissiveSparseChecker();
    public static byte[] SeparateUvOcclusionRgba { get; } = CreateOcclusion();
    public static byte[] AlphaMaskRgba { get; } = CreateAlphaMask();

    public static string ContentFingerprint { get; } = ComputeContentFingerprint();

    private static byte[] CreateEmissiveSparseChecker()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                bool active = ((x / 2) + (y / 2) * 3) % 4 == 0;
                int offset = (y * Width + x) * 4;
                pixels[offset] = active ? (byte)255 : (byte)0;
                pixels[offset + 1] = active ? (byte)176 : (byte)0;
                pixels[offset + 2] = active ? (byte)64 : (byte)0;
                pixels[offset + 3] = 255;
            }
        return pixels;
    }

    private static byte[] CreateOcclusion()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                byte value = (byte)(32 + ((x * 5 + y * 3) % 8) * 31);
                int offset = (y * Width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        return pixels;
    }

    private static byte[] CreateAlphaMask()
    {
        byte[] alpha = [0, 128, 255, 128];
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                pixels[offset] = 232;
                pixels[offset + 1] = 242;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = alpha[((x / 2) + (y / 2)) % alpha.Length];
            }
        return pixels;
    }

    private static string ComputeContentFingerprint()
    {
        byte[] combined = new byte[
            EmissiveSparseCheckerRgba.Length +
            SeparateUvOcclusionRgba.Length +
            AlphaMaskRgba.Length];
        int offset = 0;
        EmissiveSparseCheckerRgba.CopyTo(combined, offset);
        offset += EmissiveSparseCheckerRgba.Length;
        SeparateUvOcclusionRgba.CopyTo(combined, offset);
        offset += SeparateUvOcclusionRgba.Length;
        AlphaMaskRgba.CopyTo(combined, offset);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }
}

/// <summary>
/// Builds the descriptor-backed scene selected exclusively by
/// --material-gi-capture-dir. The ordinary interactive material showcase
/// remains intentionally unchanged.
/// </summary>
internal static class SampleMaterialGiConformanceScene
{
    private const int SphereLatitudeSegments = 16;
    private const int SphereLongitudeSegments = 32;

    public static bool IsCaptureSceneRequested(SampleSmokeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.SceneKind == SampleSceneKind.MaterialShowcase &&
               !string.IsNullOrWhiteSpace(options.MaterialGiCaptureDirectory);
    }

    public static SampleMaterialGiConformanceSceneBuildSummary Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager,
        TextureManager textureManager)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(meshManager);
        ArgumentNullException.ThrowIfNull(materialManager);
        ArgumentNullException.ThrowIfNull(textureManager);

        scene.Name = "Njulf Material-GI Conformance";
        scene.Id = SampleMaterialGiConformanceSceneLayout.CreateStableEntityId("material-gi.scene");
        scene.AmbientLight = new Njulf.Core.Math.Color(0f, 0f, 0f, 1f);

        MeshHandle boxMesh = meshManager.RegisterMesh(CreateBoxVertices(), CreateBoxIndices());
        MeshHandle sphereMesh = meshManager.RegisterMesh(CreateSphereVertices(), CreateSphereIndices());
        GPUVertex[] cardVertices = CreateCardVertices();
        uint[] cardIndices = CreateCardIndices();
        MeshHandle cardMesh = meshManager.RegisterMesh(cardVertices, cardIndices);
        MeshHandle skinnedCardMesh = meshManager.RegisterMeshes(
        [
            new MeshManager.MeshRegistrationData(
                cardVertices,
                cardIndices,
                generateMeshlets: true,
                skinningData: CreateSingleJointSkinningData(cardVertices.Length))
        ])[0];

        ProceduralTextures textures = CreateProceduralTextures(textureManager);
        var cases = SampleMaterialGiConformanceCatalog.Cases.ToDictionary(
            static value => value.Name,
            StringComparer.Ordinal);
        var liveEditTargets = new List<LiveEditTarget>(
            SampleMaterialGiConformanceSceneLayout.LiveEditSettleUpdateCount);
        int catalogCaseFixtures = 0;
        int skinnedFixtures = 0;

        foreach (SampleMaterialGiSceneFixture fixture in SampleMaterialGiConformanceCatalog.SceneFixtures)
        {
            MaterialDefinition definition;
            if (fixture.CatalogCaseName != null)
            {
                if (!cases.TryGetValue(fixture.CatalogCaseName, out SampleMaterialGiConformanceCase? materialCase))
                {
                    throw new InvalidOperationException(
                        $"Scene fixture '{fixture.StableId}' references unknown case '{fixture.CatalogCaseName}'.");
                }
                definition = CreateCaseMaterial(materialCase, textures);
                catalogCaseFixtures++;
            }
            else
            {
                definition = SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(fixture.MaterialPreset);
                definition = ApplyPresetTexture(definition, fixture.MaterialPreset, textures);
            }

            MaterialHandle material = materialManager.RegisterMaterialDefinition(definition);
            MeshHandle mesh = fixture.Primitive switch
            {
                SampleMaterialGiScenePrimitive.Box => boxMesh,
                SampleMaterialGiScenePrimitive.Sphere => sphereMesh,
                SampleMaterialGiScenePrimitive.Card => cardMesh,
                SampleMaterialGiScenePrimitive.SkinnedCard => skinnedCardMesh,
                _ => throw new ArgumentOutOfRangeException(nameof(fixture.Primitive), fixture.Primitive, null)
            };
            RenderObject renderObject;
            if (fixture.Primitive == SampleMaterialGiScenePrimitive.SkinnedCard)
            {
                renderObject = CreateSkinnedCard(mesh, material);
                skinnedFixtures++;
            }
            else
            {
                renderObject = new RenderObject(mesh, material);
            }

            renderObject.Id = SampleMaterialGiConformanceSceneLayout.CreateStableEntityId(fixture.StableId);
            renderObject.Name = fixture.StableId;
            renderObject.WorldMatrix = fixture.Transform.ToWorldMatrix();
            renderObject.Visible = true;
            renderObject.IsStatic = fixture.Primitive != SampleMaterialGiScenePrimitive.SkinnedCard;
            scene.Add(renderObject);
            if (renderObject is SkinnedRenderObject skinned)
                scene.Add((IUpdateable)skinned);

            if (fixture.Category == SampleMaterialGiSceneFixtureCategory.LiveEditMaterialWall)
                liveEditTargets.Add(new LiveEditTarget(fixture, material));
        }

        if (catalogCaseFixtures != SampleMaterialGiConformanceCatalog.Cases.Count)
            throw new InvalidOperationException("Not every material/GI oracle case was instantiated.");
        if (liveEditTargets.Count != SampleMaterialGiConformanceSceneLayout.LiveEditSettleUpdateCount)
            throw new InvalidOperationException("The deterministic live-edit wall is incomplete.");

        scene.Add(new SampleMaterialGiLiveEditController(materialManager, liveEditTargets));
        ValidateBuiltScene(scene);

        return new SampleMaterialGiConformanceSceneBuildSummary(
            SampleMaterialGiConformanceCatalog.SceneFixtures.Count,
            catalogCaseFixtures,
            skinnedFixtures,
            liveEditTargets.Count,
            SampleMaterialGiConformanceCatalog.SceneFingerprint);
    }

    private static void ValidateBuiltScene(Scene scene)
    {
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures =
            SampleMaterialGiConformanceCatalog.SceneFixtures;
        if (scene.RenderObjects.Count != fixtures.Count)
        {
            throw new InvalidOperationException(
                $"Material/GI scene built {scene.RenderObjects.Count} objects for {fixtures.Count} fixtures.");
        }

        var actualIds = scene.RenderObjects.Select(static value => value.Id).ToHashSet();
        foreach (SampleMaterialGiSceneFixture fixture in fixtures)
        {
            Guid expected = SampleMaterialGiConformanceSceneLayout.CreateStableEntityId(fixture.StableId);
            if (!actualIds.Contains(expected))
                throw new InvalidOperationException($"Scene did not instantiate fixture '{fixture.StableId}'.");
        }
    }

    private static MaterialDefinition CreateCaseMaterial(
        SampleMaterialGiConformanceCase materialCase,
        ProceduralTextures textures)
    {
        MaterialDefinition definition = materialCase.Material with
        {
            Name = $"Conformance.Scene.Case.{materialCase.Name}"
        };

        if (materialCase.Group == "emission-sweep")
        {
            definition = definition with
            {
                Emissive = CreateBinding(textures.EmissiveSparseChecker, texCoordSet: 0)
            };
        }
        if (materialCase.FixtureKind == SampleMaterialGiFixtureKind.SeparateUvOcclusion)
        {
            definition = definition with
            {
                Occlusion = CreateBinding(textures.SeparateUvOcclusion, texCoordSet: 1)
            };
        }
        if (materialCase.FixtureKind is
            SampleMaterialGiFixtureKind.StaticAlphaCard or
            SampleMaterialGiFixtureKind.SkinnedAlphaCard)
        {
            definition = definition with
            {
                BaseColor = CreateBinding(textures.AlphaMask, texCoordSet: 0)
            };
        }

        return MaterialDefinitionValidator.ValidateAndNormalize(definition);
    }

    private static MaterialDefinition ApplyPresetTexture(
        MaterialDefinition definition,
        SampleMaterialGiSceneMaterialPreset preset,
        ProceduralTextures textures)
    {
        if (preset is
            SampleMaterialGiSceneMaterialPreset.LiveEditAlphaCoverage or
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskSingleSided or
            SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskDoubleSided)
        {
            return definition with
            {
                BaseColor = CreateBinding(textures.AlphaMask, texCoordSet: 0)
            };
        }
        return definition;
    }

    private static MaterialTextureBinding CreateBinding(TextureHandle texture, int texCoordSet) =>
        new()
        {
            Texture = texture,
            Sampler = TextureSamplerDescription.Default,
            TexCoordSet = texCoordSet,
            Offset = CoreVector2.Zero,
            Scale = CoreVector2.One,
            RotationRadians = 0f
        };

    private static ProceduralTextures CreateProceduralTextures(TextureManager textureManager)
    {
        return new ProceduralTextures(
            CreateProceduralTexture(
                textureManager,
                "MaterialGi.EmissiveSparseChecker",
                SampleMaterialGiProceduralTextureSet.EmissiveSparseCheckerRgba,
                Format.R8G8B8A8Srgb,
                TextureColorSpace.Srgb,
                TextureSemantic.Color),
            CreateProceduralTexture(
                textureManager,
                "MaterialGi.SeparateUvOcclusion",
                SampleMaterialGiProceduralTextureSet.SeparateUvOcclusionRgba,
                Format.R8G8B8A8Unorm,
                TextureColorSpace.Linear,
                TextureSemantic.Scalar),
            CreateProceduralTexture(
                textureManager,
                "MaterialGi.AlphaMask",
                SampleMaterialGiProceduralTextureSet.AlphaMaskRgba,
                Format.R8G8B8A8Srgb,
                TextureColorSpace.Srgb,
                TextureSemantic.Color));
    }

    private static TextureHandle CreateProceduralTexture(
        TextureManager textureManager,
        string debugName,
        byte[] pixels,
        Format format,
        TextureColorSpace colorSpace,
        TextureSemantic semantic)
    {
        const uint mipLevels = 4;
        TextureHandle texture = textureManager.CreateTexture(
            SampleMaterialGiProceduralTextureSet.Width,
            SampleMaterialGiProceduralTextureSet.Height,
            format,
            mipLevels,
            debugName: debugName);
        textureManager.UploadTextureData(
            texture,
            pixels,
            SampleMaterialGiProceduralTextureSet.Width,
            SampleMaterialGiProceduralTextureSet.Height,
            format,
            generateMipmaps: true);
        TextureTransportStatistics statistics = TextureTransportImage.FromRgba8(
            pixels,
            SampleMaterialGiProceduralTextureSet.Width,
            SampleMaterialGiProceduralTextureSet.Height,
            colorSpace,
            semantic,
            CookedHash.Bytes(pixels),
            SampleMaterialGiProceduralTextureSet.SchemaVersion).Statistics;
        textureManager.PublishTextureTransportStatistics(texture, statistics);
        return texture;
    }

    private static SkinnedRenderObject CreateSkinnedCard(MeshHandle mesh, MaterialHandle material)
    {
        var joint = new SkeletonJoint
        {
            Name = "MaterialGi.Card.Root",
            ParentIndex = -1,
            LocalBindPose = AnimationTransform.Identity,
            LocalBindTransform = CoreMatrix4x4.Identity,
            InverseBindMatrix = CoreMatrix4x4.Identity
        };
        var skeleton = new Skeleton
        {
            Name = "MaterialGi.Card.Skeleton",
            Joints = [joint],
            RootJointIndex = 0
        };
        var skin = new Skin
        {
            Name = "MaterialGi.Card.Skin",
            Skeleton = skeleton,
            JointIndices = [0],
            InverseBindMatrices = [CoreMatrix4x4.Identity]
        };
        return new SkinnedRenderObject(mesh, material)
        {
            SkinIndex = 0,
            Animator = new Animator(skeleton, [skin]),
            SkinningBindTransform = CoreMatrix4x4.Identity,
            SkinningEnabled = false
        };
    }

    private static GPUVertexSkinningData[] CreateSingleJointSkinningData(int vertexCount)
    {
        var result = new GPUVertexSkinningData[vertexCount];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new GPUVertexSkinningData
            {
                Joint0 = 0,
                Weight0 = 1f
            };
        }
        return result;
    }

    private static GPUVertex[] CreateCardVertices() =>
    [
        CreateVertex(new CoreVector3(-0.5f, -0.5f, 0f), CoreVector3.UnitZ, new CoreVector2(0f, 1f), new CoreVector2(0.15f, 0.85f), CoreVector3.UnitX),
        CreateVertex(new CoreVector3(0.5f, -0.5f, 0f), CoreVector3.UnitZ, new CoreVector2(1f, 1f), new CoreVector2(0.15f, 0.15f), CoreVector3.UnitX),
        CreateVertex(new CoreVector3(0.5f, 0.5f, 0f), CoreVector3.UnitZ, new CoreVector2(1f, 0f), new CoreVector2(0.85f, 0.15f), CoreVector3.UnitX),
        CreateVertex(new CoreVector3(-0.5f, 0.5f, 0f), CoreVector3.UnitZ, new CoreVector2(0f, 0f), new CoreVector2(0.85f, 0.85f), CoreVector3.UnitX)
    ];

    private static uint[] CreateCardIndices() => [0u, 2u, 1u, 0u, 3u, 2u];

    private static GPUVertex[] CreateBoxVertices()
    {
        var vertices = new List<GPUVertex>(24);
        AddFace(vertices, new CoreVector3(0f, 0f, 0.5f), CoreVector3.UnitZ, CoreVector3.UnitX, CoreVector3.UnitY);
        AddFace(vertices, new CoreVector3(0f, 0f, -0.5f), -CoreVector3.UnitZ, -CoreVector3.UnitX, CoreVector3.UnitY);
        AddFace(vertices, new CoreVector3(0.5f, 0f, 0f), CoreVector3.UnitX, -CoreVector3.UnitZ, CoreVector3.UnitY);
        AddFace(vertices, new CoreVector3(-0.5f, 0f, 0f), -CoreVector3.UnitX, CoreVector3.UnitZ, CoreVector3.UnitY);
        AddFace(vertices, new CoreVector3(0f, 0.5f, 0f), CoreVector3.UnitY, CoreVector3.UnitX, -CoreVector3.UnitZ);
        AddFace(vertices, new CoreVector3(0f, -0.5f, 0f), -CoreVector3.UnitY, CoreVector3.UnitX, CoreVector3.UnitZ);
        return vertices.ToArray();
    }

    private static void AddFace(
        ICollection<GPUVertex> vertices,
        CoreVector3 center,
        CoreVector3 normal,
        CoreVector3 right,
        CoreVector3 up)
    {
        vertices.Add(CreateVertex(center - right * 0.5f - up * 0.5f, normal, new CoreVector2(0f, 1f), new CoreVector2(0f, 1f), right));
        vertices.Add(CreateVertex(center + right * 0.5f - up * 0.5f, normal, new CoreVector2(1f, 1f), new CoreVector2(1f, 1f), right));
        vertices.Add(CreateVertex(center + right * 0.5f + up * 0.5f, normal, new CoreVector2(1f, 0f), new CoreVector2(1f, 0f), right));
        vertices.Add(CreateVertex(center - right * 0.5f + up * 0.5f, normal, new CoreVector2(0f, 0f), new CoreVector2(0f, 0f), right));
    }

    private static uint[] CreateBoxIndices()
    {
        var indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint vertex = face * 4;
            int index = checked((int)face * 6);
            indices[index] = vertex;
            indices[index + 1] = vertex + 2;
            indices[index + 2] = vertex + 1;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 3;
            indices[index + 5] = vertex + 2;
        }
        return indices;
    }

    private static GPUVertex[] CreateSphereVertices()
    {
        var vertices = new List<GPUVertex>(
            2 + (SphereLatitudeSegments - 1) * SphereLongitudeSegments)
        {
            CreateSphereVertex(CoreVector3.UnitY, 0f, 0f)
        };
        for (int latitude = 1; latitude < SphereLatitudeSegments; latitude++)
        {
            float v = (float)latitude / SphereLatitudeSegments;
            float theta = v * MathF.PI;
            float y = MathF.Cos(theta);
            float radius = MathF.Sin(theta);
            for (int longitude = 0; longitude < SphereLongitudeSegments; longitude++)
            {
                float u = (float)longitude / SphereLongitudeSegments;
                float phi = u * MathF.Tau;
                vertices.Add(CreateSphereVertex(
                    new CoreVector3(radius * MathF.Cos(phi), y, radius * MathF.Sin(phi)),
                    u,
                    v));
            }
        }
        vertices.Add(CreateSphereVertex(CoreVector3.Down, 0f, 1f));
        return vertices.ToArray();
    }

    private static uint[] CreateSphereIndices()
    {
        var indices = new List<uint>(SphereLatitudeSegments * SphereLongitudeSegments * 6);
        uint bottom = (uint)(1 + (SphereLatitudeSegments - 1) * SphereLongitudeSegments);
        for (int longitude = 0; longitude < SphereLongitudeSegments; longitude++)
        {
            indices.Add(0);
            indices.Add(SphereRingVertex(0, longitude + 1));
            indices.Add(SphereRingVertex(0, longitude));
        }
        for (int latitude = 0; latitude < SphereLatitudeSegments - 2; latitude++)
            for (int longitude = 0; longitude < SphereLongitudeSegments; longitude++)
            {
                uint upper = SphereRingVertex(latitude, longitude);
                uint upperNext = SphereRingVertex(latitude, longitude + 1);
                uint lower = SphereRingVertex(latitude + 1, longitude);
                uint lowerNext = SphereRingVertex(latitude + 1, longitude + 1);
                indices.Add(upper);
                indices.Add(upperNext);
                indices.Add(lower);
                indices.Add(upperNext);
                indices.Add(lowerNext);
                indices.Add(lower);
            }
        int lastRing = SphereLatitudeSegments - 2;
        for (int longitude = 0; longitude < SphereLongitudeSegments; longitude++)
        {
            indices.Add(bottom);
            indices.Add(SphereRingVertex(lastRing, longitude));
            indices.Add(SphereRingVertex(lastRing, longitude + 1));
        }
        return indices.ToArray();
    }

    private static uint SphereRingVertex(int ring, int longitude)
    {
        int wrapped = longitude % SphereLongitudeSegments;
        if (wrapped < 0)
            wrapped += SphereLongitudeSegments;
        return (uint)(1 + ring * SphereLongitudeSegments + wrapped);
    }

    private static GPUVertex CreateSphereVertex(CoreVector3 normal, float u, float v)
    {
        float phi = u * MathF.Tau;
        return CreateVertex(
            normal * 0.5f,
            normal,
            new CoreVector2(u, v),
            new CoreVector2((u + 0.25f) % 1f, 1f - v),
            new CoreVector3(-MathF.Sin(phi), 0f, MathF.Cos(phi)));
    }

    private static GPUVertex CreateVertex(
        CoreVector3 position,
        CoreVector3 normal,
        CoreVector2 uv0,
        CoreVector2 uv1,
        CoreVector3 tangent) =>
        new()
        {
            Position = position,
            Normal = normal,
            TexCoord = uv0,
            TexCoord2 = uv1,
            Tangent = new CoreVector4(tangent, 1f),
            Color = GPUVertex.DefaultColor
        };

    private readonly record struct ProceduralTextures(
        TextureHandle EmissiveSparseChecker,
        TextureHandle SeparateUvOcclusion,
        TextureHandle AlphaMask);

    private readonly record struct LiveEditTarget(
        SampleMaterialGiSceneFixture Fixture,
        MaterialHandle Material);

    private sealed class SampleMaterialGiLiveEditController : IUpdateable
    {
        private readonly MaterialManager _materialManager;
        private readonly IReadOnlyList<LiveEditTarget> _targets;
        private int _nextTarget;

        public SampleMaterialGiLiveEditController(
            MaterialManager materialManager,
            IReadOnlyList<LiveEditTarget> targets)
        {
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public bool Enabled { get; set; } = true;
        public int UpdateOrder { get; set; } = -10_000;

        public void Update(float deltaTime)
        {
            _ = deltaTime;
            if (!Enabled)
                return;
            if (_nextTarget >= _targets.Count)
            {
                Enabled = false;
                return;
            }

            LiveEditTarget target = _targets[_nextTarget++];
            MaterialDefinition final = SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(
                target.Fixture.MaterialPreset,
                finalLiveEditState: true);
            MaterialDefinition current = _materialManager.GetMaterialDefinition(target.Material);
            // Preserve independent texture bindings while editing authored
            // factors/cutoffs. This is the path an editor uses in production.
            final = final with
            {
                BaseColor = current.BaseColor,
                Normal = current.Normal,
                MetallicRoughness = current.MetallicRoughness,
                Occlusion = current.Occlusion,
                Emissive = current.Emissive
            };
            _materialManager.UpdateMaterialDefinition(target.Material, final);
            if (_nextTarget >= _targets.Count)
                Enabled = false;
        }
    }
}
