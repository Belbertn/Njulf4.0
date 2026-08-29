using System;
using System.Collections.Generic;

namespace Njulf.Assets.Scenes;

/// <summary>Versioned, renderer-independent source representation of an authorable scene.</summary>
public sealed class SceneDocument
{
    public const int CurrentSchemaVersion = 10;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Scene";
    public SceneColor AmbientLight { get; init; } = new(0.2f, 0.2f, 0.2f, 1f);
    public bool ImportedModelLightsEnabled { get; init; }
    public List<SceneObjectDocument> Objects { get; init; } = [];
    public List<SceneLightDocument> Lights { get; init; } = [];
    public List<SceneReflectionProbeDocument> ReflectionProbes { get; init; } = [];
    public List<SceneGlobalIlluminationProbeVolumeDocument> GiProbeVolumes { get; init; } = [];
    public List<SceneVolumetricDensityVolumeDocument> VolumetricDensityVolumes { get; init; } = [];
    public List<SceneInstanceBatchDocument> InstanceBatches { get; init; } = [];
    public List<SceneFoliagePrototypeDocument> FoliagePrototypes { get; init; } = [];
    public List<SceneFoliagePatchDocument> FoliagePatches { get; init; } = [];
    public List<SceneParticleEffectDocument> ParticleEffects { get; init; } = [];
    public List<SceneAssetDependency> Dependencies { get; init; } = [];
}

public sealed class SceneObjectDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "RenderObject";
    public required SceneAssetReferenceDocument Model { get; init; }
    public SceneVector3 Position { get; init; }
    public SceneQuaternion Rotation { get; init; } = SceneQuaternion.Identity;
    public SceneVector3 Scale { get; init; } = SceneVector3.One;
    public bool Visible { get; init; } = true;
    public bool IsStatic { get; init; }
    public SceneMaterialOverrideDocument? MaterialOverride { get; init; }
}

/// <summary>
/// Optional authored material overrides persisted independently of
/// renderer-specific handles. A missing value means "retain the value from the
/// referenced asset"; explicit zero/false/default policy values remain
/// distinguishable from absence.
/// </summary>
public sealed class SceneMaterialOverrideDocument
{
    /// <summary>
    /// Stable token used to persist an explicitly cleared renderer blend-mode
    /// override. A missing <see cref="RenderBlendModeOverride"/> retains the
    /// referenced asset's policy.
    /// </summary>
    public const string AutomaticBlendMode = "Automatic";

    public string? Name { get; init; }
    public SceneColor? Albedo { get; init; }
    /// <summary>
    /// V1 compatibility field. Schema-v3 documents write
    /// <see cref="EmissiveColor"/> and <see cref="EmissiveStrength"/>
    /// separately.
    /// </summary>
    public SceneColor? Emissive { get; init; }
    public SceneColor? EmissiveColor { get; init; }
    public float? EmissiveStrength { get; init; }
    /// <summary>SceneLinearRadiance or LuminanceNits.</summary>
    public string? EmissiveUnit { get; init; }
    public float? EmissiveArtisticMultiplier { get; init; }
    public float? Metallic { get; init; }
    public float? Roughness { get; init; }
    public float? OcclusionStrength { get; init; }
    public float? NormalScale { get; init; }
    public string? AlphaMode { get; init; }
    public float? AlphaCutoff { get; init; }
    public bool? DoubleSided { get; init; }
    public bool? ReceivesShadows { get; init; }
    /// <summary>
    /// A renderer blend-mode name, or <see cref="AutomaticBlendMode"/> to
    /// explicitly clear a renderer-specific override.
    /// </summary>
    public string? RenderBlendModeOverride { get; init; }
    public string? ShadingModel { get; init; }
    /// <summary>Default, Enabled, or Disabled. Missing retains the asset value.</summary>
    public string? DiffuseGiParticipation { get; init; }
    /// <summary>Default, Enabled, or Disabled. Missing retains the asset value.</summary>
    public string? EmissionGiParticipation { get; init; }
    /// <summary>
    /// Schema-v2 compatibility field. Prefer
    /// <see cref="EmissionGiParticipation"/>.
    /// </summary>
    public bool? EmitsIntoGi { get; init; }
    /// <summary>
    /// Schema-v2 compatibility field. Prefer
    /// <see cref="DiffuseGiParticipation"/>.
    /// </summary>
    public bool? ReceivesDiffuseGi { get; init; }
    /// <summary>None, ThinSurface, Volume, or Unsupported.</summary>
    public string? GiTransmissionPolicy { get; init; }
    /// <summary>Generic transmission factor; supersedes the thin-only name.</summary>
    public float? TransmissionFactor { get; init; }
    public float? ThinTransmissionFactor { get; init; }
    public SceneColor? ThinTransmissionTint { get; init; }
    public float? Ior { get; init; }
    public float? ThicknessFactor { get; init; }
    /// <summary>Zero represents infinite/no absorption distance.</summary>
    public float? AttenuationDistance { get; init; }
    public SceneColor? AttenuationColor { get; init; }
    /// <summary>ClosedVolume or WaterSurface.</summary>
    public string? OpticalBoundaryKind { get; init; }
    /// <summary>Default, Disabled, Mirror, RoughSpecular, or DielectricPriority.</summary>
    public string? GiCausticCasterPolicy { get; init; }
    public SceneVector2? WaterNormalVelocity0 { get; init; }
    public SceneVector2? WaterNormalVelocity1 { get; init; }
    public float? WaterNormalUvScale0 { get; init; }
    public float? WaterNormalUvScale1 { get; init; }
    public float? Dispersion { get; init; }
}

public sealed class SceneLightDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Light";
    public string Type { get; init; } = "Point";
    public SceneVector3 Position { get; init; }
    public SceneVector3 Direction { get; init; } = new(0f, -1f, 0f);
    public SceneVector3 Up { get; init; } = new(0f, 0f, 1f);
    /// <summary>
    /// Rectangle width/height; disk equal X/Y diameters; tube length/diameter.
    /// </summary>
    public SceneVector2 Size { get; init; } = new(1f, 1f);
    public bool TwoSided { get; init; }
    public SceneVector3 Color { get; init; } = SceneVector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; } = 10f;
    public float SpotAngle { get; init; } = 0.5f;
    public float InnerSpotAngle { get; init; }
    public string AttenuationMode { get; init; } = "LegacyWindowed";
    public float AttenuationConstant { get; init; } = 1f;
    public float AttenuationLinear { get; init; }
    public float AttenuationQuadratic { get; init; }
    public bool CastsShadows { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public uint ShadowMapSizeOverride { get; init; }
    public float ShadowNearPlane { get; init; } = 0.1f;
    public float ShadowFarPlane { get; init; } = 100f;
    public int ShadowPriority { get; init; }
    public SceneAssetReferenceDocument? IesProfile { get; init; }
    public float IesRotationRadians { get; init; }
}

public sealed class SceneReflectionProbeDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "ReflectionProbe";
    public SceneVector3 Position { get; init; }
    public SceneQuaternion Rotation { get; init; } = SceneQuaternion.Identity;
    public string Shape { get; init; } = "Box";
    public SceneVector3 BoxExtents { get; init; } = new(5f, 5f, 5f);
    public float Radius { get; init; } = 5f;
    public float BlendDistance { get; init; } = 1f;
    public float Intensity { get; init; } = 1f;
    public int Priority { get; init; }
    public string? CubemapPath { get; init; }
    public bool BoxProjection { get; init; } = true;
}

public sealed class SceneGlobalIlluminationProbeVolumeDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "GI Probe Volume";
    public bool Enabled { get; init; } = true;
    public SceneVector3 Origin { get; init; }
    public SceneVector3 Size { get; init; } = new(24f, 12f, 24f);
    public bool Interior { get; init; }
    public string QualityClass { get; init; } = "Medium";
    public int Priority { get; init; }
    public float BlendDistance { get; init; }
    public int StreamingCellId { get; init; }
    public int ProbeCountX { get; init; } = 12;
    public int ProbeCountY { get; init; } = 6;
    public int ProbeCountZ { get; init; } = 12;
    public int RaysPerProbe { get; init; } = 96;
    public int MaxProbeUpdatesPerFrame { get; init; } = 256;
    public float NormalBias { get; init; } = 0.2f;
    public float ViewBias { get; init; } = 0.5f;
    public float MaxRayDistance { get; init; } = 16f;
    public float Intensity { get; init; } = 1f;
    public float Hysteresis { get; init; } = 0.97f;
    public float SteadyHysteresis { get; init; } = 0.97f;
    public float DirtyHysteresis { get; init; } = 0.72f;
    public int UpdatePriority { get; init; }
    public int DirtyRaysPerProbe { get; init; } = 64;
}

public sealed class SceneVolumetricDensityVolumeDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Density Volume";
    public bool Enabled { get; init; } = true;
    public SceneVector3 Position { get; init; }
    public SceneQuaternion Rotation { get; init; } = SceneQuaternion.Identity;
    public string Shape { get; init; } = "Box";
    public SceneVector3 BoxExtents { get; init; } = new(5f, 5f, 5f);
    public float Radius { get; init; } = 5f;
    public float EdgeFade { get; init; } = 1f;
    public float DensityMultiplier { get; init; } = 1f;
    public float ExtinctionPerMeter { get; init; } = 0.08f;
    public SceneVector3 ScatteringAlbedo { get; init; } = new(0.9f, 0.9f, 0.9f);
    public float Anisotropy { get; init; } = 0.2f;
    public int Priority { get; init; }
    public float NoiseScale { get; init; } = 0.1f;
    public float NoiseStrength { get; init; } = 0.5f;
    public float NoiseContrast { get; init; } = 1f;
    public uint NoiseSeed { get; init; } = 1u;
    public SceneVector3 FlowVelocity { get; init; }
}

public sealed class SceneInstanceBatchDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "StaticInstanceBatch";
    public required SceneAssetReferenceDocument Model { get; init; }
    public bool Visible { get; init; } = true;
    public List<SceneTransformDocument> Instances { get; init; } = [];
}

public sealed class SceneFoliagePrototypeDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "FoliagePrototype";
    public required SceneAssetReferenceDocument Model { get; init; }
    public string GeometryMode { get; init; } = "Mesh";
    public float CardHeight { get; init; } = 1f;
    public float CardWidth { get; init; } = 0.08f;
    public bool FarImpostorEnabled { get; init; }
    public bool CastShadows { get; init; } = true;
    public bool TwoSided { get; init; } = true;
    public SceneFoliageImpostorDocument? Impostor { get; init; }
    public SceneFoliageLodDocument Lod { get; init; } = new();
    public SceneFoliageWindDocument Wind { get; init; } = new();
    public SceneFoliageLightingDocument Lighting { get; init; } = new();
}

public sealed class SceneFoliagePatchDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "FoliagePatch";
    public Guid PrototypeId { get; init; }
    public SceneBoundingBox Bounds { get; init; } = new(new SceneVector3(), new SceneVector3());
    public SceneVector3 InstancePosition { get; init; }
    public float InstanceScale { get; init; } = 1f;
    public float Density { get; init; } = 1f;
    public uint Seed { get; init; } = 1;
    public string PlacementMode { get; init; } = "ProceduralSurface";
    public SceneFoliagePlacementDocument Placement { get; init; } = new();
    public SceneFoliageDensityMapDocument? DensityMap { get; init; }
    /// <summary>Schema 9 compatibility path. Schema 10 writes DensityMap.</summary>
    public string? DensityTexturePath { get; init; }
    public bool Visible { get; init; } = true;
}

public sealed class SceneFoliagePlacementDocument
{
    public float Density { get; init; } = 1f;
    public float MinimumSpacing { get; init; } = 1f;
    public SceneVector2 ScaleRange { get; init; } = new(0.85f, 1.15f);
    public SceneVector2 YawRangeDegrees { get; init; } = new(0f, 360f);
    public bool AlignToSurfaceNormal { get; init; }
    public SceneVector2 AltitudeRange { get; init; } = new(-100_000f, 100_000f);
    public SceneVector2 SlopeRangeDegrees { get; init; } = new(0f, 90f);
    public uint BiomeMask { get; init; } = uint.MaxValue;
    public bool AllowWater { get; init; }
    public bool AllowRoads { get; init; }
    public bool RespectExclusions { get; init; } = true;
    public uint Seed { get; init; } = 1;
    public float CellSize { get; init; } = 16f;
}

public sealed class SceneFoliageDensityMapDocument
{
    public string SourcePath { get; init; } = string.Empty;
    public string? ContentHash { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Format { get; init; } = "R8UNorm";
    public SceneVector2 WorldToUvScale { get; init; } = new(1f, 1f);
    public SceneVector2 WorldToUvOffset { get; init; }
    public uint Revision { get; init; } = 1;
}

public sealed class SceneFoliageImpostorDocument
{
    public string AlbedoOpacityAtlasPath { get; init; } = string.Empty;
    public string NormalAtlasPath { get; init; } = string.Empty;
    public string DepthAtlasPath { get; init; } = string.Empty;
    public int ViewCount { get; init; }
    public int AtlasWidth { get; init; }
    public int AtlasHeight { get; init; }
    public List<SceneFoliageImpostorViewDocument> Views { get; init; } = [];
    public SceneBoundingBox SourceBounds { get; init; } =
        new(new SceneVector3(), new SceneVector3());
    public SceneVector3 Pivot { get; init; }
    public float Scale { get; init; } = 1f;
    public string? ContentHash { get; init; }
}

public sealed class SceneFoliageImpostorViewDocument
{
    public SceneVector3 Direction { get; init; }
    /// <summary>Normalized atlas X, Y, width, and height.</summary>
    public SceneVector4 AtlasRectangle { get; init; }
}

public sealed class SceneParticleEffectDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "ParticleEffect";
    public required SceneAssetReferenceDocument Effect { get; init; }
    public SceneTransformDocument Transform { get; init; } = new();
    public bool Visible { get; init; } = true;
    public bool Playing { get; init; } = true;
    public bool Paused { get; init; }
    public bool Stopped { get; init; }
    public uint RandomSeed { get; init; } = 1;
}

public sealed class SceneTransformDocument
{
    public SceneVector3 Position { get; init; }
    public SceneQuaternion Rotation { get; init; } = SceneQuaternion.Identity;
    public SceneVector3 Scale { get; init; } = SceneVector3.One;
}

public sealed record SceneBoundingBox(SceneVector3 Min, SceneVector3 Max);
public sealed class SceneFoliageLodDocument { public float Lod0Distance { get; init; } = 20f; public float Lod1Distance { get; init; } = 60f; public float Lod2Distance { get; init; } = 140f; }
public sealed class SceneFoliageWindDocument { public float Strength { get; init; } = 0.35f; public float Frequency { get; init; } = 0.7f; public float Flutter { get; init; } = 0.15f; }
public sealed class SceneFoliageLightingDocument { public float WrapDiffuse { get; init; } = 0.35f; public float Backlight { get; init; } = 0.25f; public float NormalBend { get; init; } = 0.5f; }

public sealed record SceneAssetReferenceDocument(string Path, string SubObject = "*", string? ContentHash = null);
public sealed record SceneAssetDependency(string Path, string? ContentHash = null);
public readonly record struct SceneVector2(float X, float Y);
public readonly record struct SceneVector3(float X, float Y, float Z)
{
    public static SceneVector3 One { get; } = new(1f, 1f, 1f);
}
public readonly record struct SceneVector4(float X, float Y, float Z, float W);
public readonly record struct SceneQuaternion(float X, float Y, float Z, float W)
{
    public static SceneQuaternion Identity { get; } = new(0f, 0f, 0f, 1f);
}
public readonly record struct SceneColor(float R, float G, float B, float A);
