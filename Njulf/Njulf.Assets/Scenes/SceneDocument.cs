using System;
using System.Collections.Generic;

namespace Njulf.Assets.Scenes;

/// <summary>Versioned, renderer-independent source representation of an authorable scene.</summary>
public sealed class SceneDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Scene";
    public SceneColor AmbientLight { get; init; } = new(0.2f, 0.2f, 0.2f, 1f);
    public List<SceneObjectDocument> Objects { get; init; } = [];
    public List<SceneLightDocument> Lights { get; init; } = [];
    public List<SceneReflectionProbeDocument> ReflectionProbes { get; init; } = [];
    public List<SceneGlobalIlluminationProbeVolumeDocument> GiProbeVolumes { get; init; } = [];
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

/// <summary>Editable material values persisted independently of renderer-specific handles.</summary>
public sealed class SceneMaterialOverrideDocument
{
    public SceneColor Albedo { get; init; } = new(1f, 1f, 1f, 1f);
    public SceneColor Emissive { get; init; } = new(0f, 0f, 0f, 0f);
    public float Metallic { get; init; }
    public float Roughness { get; init; } = 1f;
    public float NormalScale { get; init; } = 1f;
    public float AlphaCutoff { get; init; } = 0.5f;
}

public sealed class SceneLightDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Light";
    public string Type { get; init; } = "Point";
    public SceneVector3 Position { get; init; }
    public SceneVector3 Direction { get; init; } = new(0f, -1f, 0f);
    public SceneVector3 Color { get; init; } = SceneVector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; } = 10f;
    public float SpotAngle { get; init; } = 0.5f;
    public bool CastsShadows { get; init; }
    public float ShadowStrength { get; init; } = 1f;
    public uint ShadowMapSizeOverride { get; init; }
    public float ShadowNearPlane { get; init; } = 0.1f;
    public float ShadowFarPlane { get; init; } = 100f;
    public int ShadowPriority { get; init; }
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
    public uint AuthoredMeshletStride { get; init; } = 1;
    public float CardHeight { get; init; } = 1f;
    public float CardWidth { get; init; } = 0.08f;
    public bool FarImpostorEnabled { get; init; }
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
    public string? DensityTexturePath { get; init; }
    public bool Visible { get; init; } = true;
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
public readonly record struct SceneVector3(float X, float Y, float Z)
{
    public static SceneVector3 One { get; } = new(1f, 1f, 1f);
}
public readonly record struct SceneQuaternion(float X, float Y, float Z, float W)
{
    public static SceneQuaternion Identity { get; } = new(0f, 0f, 0f, 1f);
}
public readonly record struct SceneColor(float R, float G, float B, float A);
