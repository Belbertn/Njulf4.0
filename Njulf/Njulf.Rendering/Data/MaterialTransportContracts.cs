using System;
using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>
/// Renderer-owned material classification. Values are part of the CPU/GPU
/// transport contract and must remain stable when serialized or uploaded.
/// </summary>
public enum MaterialShadingModel : uint
{
    Pbr = 0,
    Unlit = 1,
    Foliage = 2,
    Decal = 3,
    SubsurfaceApproximation = 4,
    /// <summary>
    /// A zero-thickness dielectric sheet. Unlike cloth using the GI-only
    /// ThinSurface policy, this model opts into visible Fresnel reflection and
    /// tinted raster transmission in the transparent forward path.
    /// </summary>
    ThinGlass = 5
}

public enum MaterialAlphaMode : uint
{
    Opaque = 0,
    Mask = 1,
    Blend = 2
}

public enum GiParticipationOverride : byte
{
    Default = 0,
    Disabled = 1,
    Enabled = 2
}

public enum GiTransmissionPolicy : byte
{
    None = 0,
    RemoveFromOpaqueDiffuse = 1,
    ThinSurface = 2,
    Volume = 3,
    Unsupported = 255
}

/// <summary>
/// Declares how a physical transmission boundary is interpreted. Closed
/// volumes must be watertight and alternate entry/exit intersections; water
/// surfaces are open interfaces whose exterior is air and whose interior is
/// the authored material medium.
/// </summary>
public enum OpticalBoundaryKind : byte
{
    ClosedVolume = 0,
    WaterSurface = 1
}

/// <summary>
/// General caustic-caster authoring policy. Default automatically admits
/// validated closed dielectrics and water while mirrors and rough specular
/// materials remain explicit opt-ins.
/// </summary>
public enum GiCausticCasterPolicy : byte
{
    Default = 0,
    Disabled = 1,
    Mirror = 2,
    RoughSpecular = 3,
    DielectricPriority = 4
}

public enum GiTransportProfileQuality : byte
{
    Invalid = 0,
    MaterialFactors = 1,
    TextureStatistics = 2,
    PrimitiveSurfaceSampling = 3
}

[Flags]
public enum GiMaterialTransportFlags : uint
{
    None = 0,
    BaseStatisticsValid = 1u << 0,
    DiffuseProfileValid = 1u << 1,
    EmissionProfileValid = 1u << 2,
    AlphaProfileValid = 1u << 3,
    NormalProfileValid = 1u << 4,
    Unlit = 1u << 5,
    DoubleSided = 1u << 6,
    TransmissionRemovesOpaqueDiffuse = 1u << 7,
    EmitsIntoGi = 1u << 8,
    ReceivesIndirectDiffuse = 1u << 9,
    ReflectsIndirectDiffuse = 1u << 10,
    HasBaseColorTexture = 1u << 11,
    HasMetallicRoughnessTexture = 1u << 12,
    HasOcclusionTexture = 1u << 13,
    HasEmissiveTexture = 1u << 14,
    LegacyV1Fallback = 1u << 15,
    UnsupportedTransmission = 1u << 16,
    /// <summary>
    /// A compact consumer must sample the detailed material bindings when one
    /// of the profiles required by this material is invalid. This is an
    /// explicit correctness policy, not a claim that guessed averages are
    /// valid.
    /// </summary>
    CompactTextureFallback = 1u << 17,
    /// <summary>
    /// The raster surface is an explicitly authored geometry decal. This flag
    /// is consumed by forward shading so decals can use an independent
    /// indirect-lighting policy while sharing the transparent draw path.
    /// </summary>
    GeometryDecal = 1u << 18,
    ThinSurfaceTransmission = 1u << 19,
    TransmissionProfileValid = 1u << 20,
    HasTransmissionTexture = 1u << 21,
    VolumeTransmission = 1u << 22,
    WaterSurfaceBoundary = 1u << 23,
    QualityShift = 24,
    QualityMask = 0x0f00_0000u,
    /// <summary>
    /// An extension payload is present solely or additionally for optical
    /// boundary/caustic policy, even when no glTF lighting extension flag is set.
    /// </summary>
    OpticalPolicyPayload = 1u << 28,
    /// <summary>
    /// Distinguishes visible zero-thickness glass from opaque thin-surface
    /// transport such as curtains. This bit is consumed by raster shading and
    /// does not alter the canonical transmitted-diffuse GI lobe.
    /// </summary>
    ThinGlass = 1u << 29
}

[Flags]
public enum MaterialChangeMask : uint
{
    None = 0,
    RasterAppearance = 1u << 0,
    DiffuseTransport = 1u << 1,
    Emission = 1u << 2,
    AlphaCoverage = 1u << 3,
    Sidedness = 1u << 4,
    ShadingModel = 1u << 5,
    FarField = 1u << 6,
    TextureDependencies = 1u << 7,
    AccelerationStructure = 1u << 8,
    /// <summary>
    /// CPU-only automatic-planar receiver policy. This never changes the GPU
    /// material layout or forward shader classification.
    /// </summary>
    AutomaticPlanarReflection = 1u << 9,
    All = RasterAppearance |
          DiffuseTransport |
          Emission |
          AlphaCoverage |
          Sidedness |
          ShadingModel |
          FarField |
          TextureDependencies |
          AccelerationStructure |
          AutomaticPlanarReflection
}

/// <summary>
/// One independent glTF texture binding. A binding is equal only when its
/// image handle, sampler, UV set, and complete transform are equal.
/// </summary>
public sealed record MaterialTextureBinding
{
    public static MaterialTextureBinding Missing { get; } = new();

    public TextureHandle Texture { get; init; } = TextureHandle.Invalid;
    public TextureSamplerDescription Sampler { get; init; } = TextureSamplerDescription.Default;
    public int TexCoordSet { get; init; }
    public Vector2 Offset { get; init; } = Vector2.Zero;
    public Vector2 Scale { get; init; } = Vector2.One;
    public float RotationRadians { get; init; }

    public bool IsBound => Texture.IsValid;
}

/// <summary>
/// Authored extension values. Texture bindings stay independent even when they
/// happen to reference the same physical image.
/// </summary>
public sealed record MaterialExtensionDefinition
{
    public static MaterialExtensionDefinition None { get; } = new();

    public float ClearcoatFactor { get; init; }
    public float ClearcoatRoughness { get; init; }
    public float ClearcoatNormalScale { get; init; } = 1f;
    public MaterialTextureBinding Clearcoat { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding ClearcoatRoughnessTexture { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding ClearcoatNormal { get; init; } = MaterialTextureBinding.Missing;

    public Vector3 SheenColorFactor { get; init; } = Vector3.Zero;
    public float SheenRoughness { get; init; }
    public MaterialTextureBinding SheenColor { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding SheenRoughnessTexture { get; init; } = MaterialTextureBinding.Missing;

    public float AnisotropyStrength { get; init; }
    public float AnisotropyRotation { get; init; }
    public MaterialTextureBinding Anisotropy { get; init; } = MaterialTextureBinding.Missing;

    public float TransmissionFactor { get; init; }
    public float Ior { get; init; } = 1.5f;
    public float ThicknessFactor { get; init; }
    public float AttenuationDistance { get; init; } = float.PositiveInfinity;
    public Vector3 AttenuationColor { get; init; } = Vector3.One;
    public MaterialTextureBinding Transmission { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding Thickness { get; init; } = MaterialTextureBinding.Missing;
    public GiTransmissionPolicy TransmissionPolicy { get; init; } = GiTransmissionPolicy.None;
    public OpticalBoundaryKind OpticalBoundary { get; init; } =
        OpticalBoundaryKind.ClosedVolume;
    public GiCausticCasterPolicy CausticCasterPolicy { get; init; } =
        GiCausticCasterPolicy.Default;
    /// <summary>
    /// Explicit C4 authoring intent. This value is ignored by all canonical
    /// DDGI transport paths; only the separate tagged-caustic system consumes
    /// it after topology/current-pose admission.
    /// </summary>
    public GiCausticParticipationMode CausticParticipation { get; init; } =
        GiCausticParticipationMode.None;
    /// <summary>
    /// Renderer-authored diffuse tint for a zero-thickness transmission lobe.
    /// This is independent of raster alpha and volume attenuation.
    /// </summary>
    public Vector3 ThinTransmissionTint { get; init; } = Vector3.One;

    /// <summary>Primary and secondary world-space UV flow velocities.</summary>
    public Vector2 WaterNormalVelocity0 { get; init; } = new(0.035f, 0.012f);
    public Vector2 WaterNormalVelocity1 { get; init; } = new(-0.018f, 0.027f);
    /// <summary>UV frequencies used when the normal texture is sampled twice.</summary>
    public float WaterNormalUvScale0 { get; init; } = 1f;
    public float WaterNormalUvScale1 { get; init; } = 1.73f;

    public float SpecularFactor { get; init; } = 1f;
    public Vector3 SpecularColorFactor { get; init; } = Vector3.One;
    public MaterialTextureBinding Specular { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding SpecularColor { get; init; } = MaterialTextureBinding.Missing;

    public float IridescenceFactor { get; init; }
    public float IridescenceIor { get; init; } = 1.3f;
    public float IridescenceThicknessMinimum { get; init; } = 100f;
    public float IridescenceThicknessMaximum { get; init; } = 400f;
    public MaterialTextureBinding Iridescence { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding IridescenceThickness { get; init; } = MaterialTextureBinding.Missing;

    public float Dispersion { get; init; }
    public Vector3 SubsurfaceColor { get; init; } = Vector3.One;
    public float SubsurfaceStrength { get; init; }
    public MaterialTextureBinding Subsurface { get; init; } = MaterialTextureBinding.Missing;
}

/// <summary>
/// Immutable authored source of truth used by runtime and editor material
/// changes. Derived GPU and GI fields are deliberately absent.
/// </summary>
public sealed record MaterialDefinition
{
    public static MaterialDefinition Default { get; } = new();

    public string Name { get; init; } = "DefaultMaterial";
    public Vector4 BaseColorFactor { get; init; } = Vector4.One;
    public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;
    public float EmissiveStrength { get; init; } = 1f;
    public EmissivePhotometricUnit EmissiveUnit { get; init; } =
        EmissivePhotometricUnit.SceneLinearRadiance;
    /// <summary>
    /// Deliberate non-physical multiplier applied after unit conversion. Its
    /// energy effect is exposed separately by diagnostics and the editor.
    /// </summary>
    public float EmissiveArtisticMultiplier { get; init; } = 1f;
    public float MetallicFactor { get; init; }
    public float RoughnessFactor { get; init; } = 1f;
    public float OcclusionStrength { get; init; } = 1f;
    public float NormalScale { get; init; } = 1f;

    public MaterialTextureBinding BaseColor { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding Normal { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding MetallicRoughness { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding Occlusion { get; init; } = MaterialTextureBinding.Missing;
    public MaterialTextureBinding Emissive { get; init; } = MaterialTextureBinding.Missing;

    public MaterialAlphaMode AlphaMode { get; init; }
    public float AlphaCutoff { get; init; } = 0.5f;
    public bool DoubleSided { get; init; }
    public bool ReceivesShadows { get; init; } = true;
    /// <summary>
    /// Allows rigid planar surfaces using this material to compete for the
    /// automatic planar-capture budget. Explicit authoring is required.
    /// </summary>
    public bool AutomaticPlanarReflectionEnabled { get; init; }
    /// <summary>
    /// Optional renderer-specific blend policy. When unset, the compiler
    /// derives the conventional blend mode from <see cref="AlphaMode"/> and
    /// enabled material features.
    /// </summary>
    public MaterialBlendMode? RenderBlendModeOverride { get; init; }
    public MaterialShadingModel ShadingModel { get; init; } = MaterialShadingModel.Pbr;
    public MaterialFeatureFlags FeatureFlags { get; init; }
    public MaterialExtensionDefinition Extensions { get; init; } = MaterialExtensionDefinition.None;

    public GiParticipationOverride DiffuseGiParticipation { get; init; } = GiParticipationOverride.Default;
    public GiParticipationOverride EmissionGiParticipation { get; init; } = GiParticipationOverride.Default;
    public bool IsGeometryDecal { get; init; }
    public int DecalLayer { get; init; }
    public float DecalDepthBias { get; init; }

    public bool ReceivesIndirectDiffuse =>
        DiffuseGiParticipation != GiParticipationOverride.Disabled &&
        ShadingModel != MaterialShadingModel.Unlit &&
        (ShadingModel != MaterialShadingModel.Decal || IsGeometryDecal);

    public bool ReflectsIndirectDiffuse =>
        DiffuseGiParticipation == GiParticipationOverride.Enabled ||
        DiffuseGiParticipation == GiParticipationOverride.Default &&
        (ShadingModel is MaterialShadingModel.Pbr or
            MaterialShadingModel.Foliage or
            MaterialShadingModel.SubsurfaceApproximation or
            MaterialShadingModel.ThinGlass ||
         ShadingModel == MaterialShadingModel.Decal && IsGeometryDecal);

    public bool EmitsIntoGi =>
        EmissionGiParticipation == GiParticipationOverride.Enabled ||
        EmissionGiParticipation == GiParticipationOverride.Default &&
        ShadingModel != MaterialShadingModel.Unlit;
}

/// <summary>
/// Canonical compact transport profile. Validity is represented only by flags;
/// a zero value is always a valid physical value when its flag is set.
/// </summary>
public sealed record GiMaterialTransportProfile
{
    public static GiMaterialTransportProfile Invalid { get; } = new();

    public uint AlgorithmVersion { get; init; }
    public ulong SourceContentHash { get; init; }
    public ulong PrimitiveContentHash { get; init; }
    public GiMaterialTransportFlags Flags { get; init; }
    public GiTransportProfileQuality Quality { get; init; }
    public Vector3 MeanDiffuseReflectance { get; init; } = Vector3.Zero;
    public Vector3 MeanTransmittedDiffuseReflectance { get; init; } = Vector3.Zero;
    public Vector3 MeanEmissiveRadiance { get; init; } = Vector3.Zero;
    public float EmissiveImportance { get; init; }
    public EmissivePhotometricUnit EmissiveUnit { get; init; }
    public float EffectiveEmissiveScale { get; init; } = 1f;
    public float EmissiveArtisticMultiplier { get; init; } = 1f;
    public float AverageEmissiveLuminanceNits { get; init; }
    /// <summary>
    /// Source-resolution peak estimate derived from cooked texture luminance
    /// statistics. <see cref="PeakEmissiveLuminanceValid"/> distinguishes a
    /// physical zero from unavailable texture statistics.
    /// </summary>
    public float PeakEmissiveLuminanceNits { get; init; }
    public bool PeakEmissiveLuminanceValid { get; init; }
    public float MeanMaterialOcclusion { get; init; } = 1f;
    public float AlphaCoverage { get; init; } = 1f;
    public float MeanMetallic { get; init; }
    public float MeanRoughness { get; init; } = 1f;
    public float NormalVariance { get; init; }

    public bool Has(GiMaterialTransportFlags flag) => (Flags & flag) == flag;
}

public readonly record struct MaterialAspectRevisions(
    uint Material,
    uint DiffuseTransport,
    uint Emission,
    uint AlphaCoverage,
    uint Sidedness,
    uint ShadingModel,
    uint FarField)
{
    public static MaterialAspectRevisions Initial { get; } = new(1, 1, 1, 1, 1, 1, 1);
}

public sealed record MaterialChangedEvent(
    MaterialHandle Handle,
    MaterialChangeMask ChangeMask,
    MaterialAspectRevisions Revisions);

/// <summary>
/// CPU representation of the shared shader surface contract.
/// </summary>
public readonly record struct GiSurfaceSample(
    Vector3 CanonicalGeometricNormal,
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    Vector3 DirectionalDiffuseBase,
    Vector3 DielectricF0,
    Vector3 DiffuseReflectance,
    Vector3 TransmittedDiffuseReflectance,
    Vector3 EmissiveRadiance,
    float MaterialOcclusion,
    float Opacity,
    float Metallic,
    float Roughness,
    GiMaterialTransportFlags Flags);
