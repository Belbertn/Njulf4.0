using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>Builds deterministic source documents from live scene state and writes them atomically.</summary>
public sealed class SceneDocumentWriter
{
    private readonly HashSet<string> _backedUpPaths = new(StringComparer.OrdinalIgnoreCase);

    public SceneDocument CreateDocument(Scene scene, ISceneLightStore? lights = null, ISceneMaterialOverrideStore? materials = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var dependencies = new Dictionary<string, string?>(StringComparer.Ordinal);
        ModelLightRuntimeController? importedLights =
            scene.GetComponent<ModelLightRuntimeController>();
        var document = new SceneDocument
        {
            Id = scene.Id,
            Name = scene.Name,
            AmbientLight = ToSceneColor(scene.AmbientLight),
            ImportedModelLightsEnabled =
                importedLights?.ImportedModelLightsEnabled ?? false,
            Objects = scene.RenderObjects
                .Where(static item => item.PersistInSceneDocument)
                .Select(item => ToObject(item, dependencies, materials))
                .ToList(),
            ReflectionProbes = scene.ReflectionProbes.Select(ToReflectionProbe).ToList(),
            GiProbeVolumes = scene.GlobalIlluminationProbeVolumes.Select(ToGiProbeVolume).ToList(),
            VolumetricDensityVolumes = scene.VolumetricDensityVolumes.Select(ToVolumetricDensityVolume).ToList(),
            InstanceBatches = scene.StaticInstanceBatches.Select(item => ToInstanceBatch(item, dependencies)).ToList(),
            FoliagePrototypes = scene.FoliagePrototypes.Select(item => ToFoliagePrototype(item, dependencies)).ToList(),
            FoliagePatches = scene.FoliagePatches.Select(ToFoliagePatch).ToList(),
            ParticleEffects = scene.ParticleEffects.Select(item => ToParticleEffect(item, dependencies)).ToList(),
            Lights = lights?.Enumerate()
                .Where(light => importedLights?.IsImportedLight(light.Id) != true)
                .ToList() ?? [],
            Dependencies = []
        };
        foreach (SceneLightDocument light in document.Lights)
        {
            if (light.IesProfile is { } profile)
                AddDependency(profile, light.Id, light.Name, dependencies);
        }
        document.Dependencies.AddRange(dependencies.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new SceneAssetDependency(pair.Key, pair.Value)));
        return document;
    }

    public void Write(string path, Scene scene, ISceneLightStore? lights = null, ISceneMaterialOverrideStore? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        // Do not consume the one-session backup on an initial export where there is nothing to protect.
        bool createBackup = File.Exists(fullPath) && _backedUpPaths.Add(fullPath);
        SceneDocumentJson.WriteAtomic(fullPath, CreateDocument(scene, lights, materials), createBackup);
    }

    private static SceneObjectDocument ToObject(RenderObject source, Dictionary<string, string?> dependencies, ISceneMaterialOverrideStore? materials) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Model = ToAsset(source.AssetReference, source.Id, source.Name, dependencies),
        Position = ToSceneVector(source.Position),
        Rotation = ToSceneQuaternion(source.Rotation),
        Scale = ToSceneVector(source.Scale),
        Visible = source.Visible,
        IsStatic = source.IsStatic,
        MaterialOverride = materials?.Capture(source)
    };

    private static SceneReflectionProbeDocument ToReflectionProbe(ReflectionProbe source) => new()
    {
        Id = source.Id, Name = source.Name, Position = ToSceneVector(source.Position), Rotation = ToSceneQuaternion(source.Rotation),
        Shape = source.Shape.ToString(), BoxExtents = ToSceneVector(source.BoxExtents), Radius = source.Radius, BlendDistance = source.BlendDistance,
        Intensity = source.Intensity, Priority = source.Priority, CubemapPath = source.CubemapPath, BoxProjection = source.BoxProjection
    };

    private static SceneGlobalIlluminationProbeVolumeDocument ToGiProbeVolume(GlobalIlluminationProbeVolume source) => new()
    {
        Id = source.Id, Name = source.Name, Enabled = source.Enabled, Origin = ToSceneVector(source.Origin), Size = ToSceneVector(source.Size),
        Interior = source.Interior, QualityClass = source.QualityClass.ToString(), Priority = source.Priority, BlendDistance = source.BlendDistance,
        StreamingCellId = source.StreamingCellId, ProbeCountX = source.ProbeCountX, ProbeCountY = source.ProbeCountY, ProbeCountZ = source.ProbeCountZ,
        RaysPerProbe = source.RaysPerProbe, MaxProbeUpdatesPerFrame = source.MaxProbeUpdatesPerFrame, NormalBias = source.NormalBias, ViewBias = source.ViewBias,
        MaxRayDistance = source.MaxRayDistance, Intensity = source.Intensity, Hysteresis = source.Hysteresis, SteadyHysteresis = source.SteadyHysteresis,
        DirtyHysteresis = source.DirtyHysteresis, UpdatePriority = source.UpdatePriority, DirtyRaysPerProbe = source.DirtyRaysPerProbe
    };

    private static SceneVolumetricDensityVolumeDocument ToVolumetricDensityVolume(
        VolumetricDensityVolume source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Enabled = source.Enabled,
        Position = ToSceneVector(source.Position),
        Rotation = ToSceneQuaternion(source.Rotation),
        Shape = source.Shape.ToString(),
        BoxExtents = ToSceneVector(source.BoxExtents),
        Radius = source.Radius,
        EdgeFade = source.EdgeFade,
        DensityMultiplier = source.DensityMultiplier,
        ExtinctionPerMeter = source.ExtinctionPerMeter,
        ScatteringAlbedo = ToSceneVector(source.ScatteringAlbedo),
        Anisotropy = source.Anisotropy,
        Priority = source.Priority,
        NoiseScale = source.NoiseScale,
        NoiseStrength = source.NoiseStrength,
        NoiseContrast = source.NoiseContrast,
        NoiseSeed = source.NoiseSeed,
        FlowVelocity = ToSceneVector(source.FlowVelocity)
    };

    private static SceneInstanceBatchDocument ToInstanceBatch(StaticInstanceBatch source, Dictionary<string, string?> dependencies) => new()
    {
        Id = source.Id, Name = source.Name, Model = ToAsset(source.AssetReference, source.Id, source.Name, dependencies), Visible = source.Visible,
        Instances = source.WorldMatrices.Select(ToTransform).ToList()
    };

    private static SceneFoliagePrototypeDocument ToFoliagePrototype(FoliagePrototype source, Dictionary<string, string?> dependencies) => new()
    {
        Id = source.Id, Name = source.Name, Model = ToAsset(source.AssetReference, source.Id, source.Name, dependencies), GeometryMode = source.GeometryMode.ToString(),
        CardHeight = source.CardHeight, CardWidth = source.CardWidth,
        FarImpostorEnabled = source.FarImpostorEnabled,
        CastShadows = source.CastShadows, TwoSided = source.TwoSided,
        Impostor = source.Impostor == null ? null : new SceneFoliageImpostorDocument
        {
            AlbedoOpacityAtlasPath = source.Impostor.AlbedoOpacityAtlasPath,
            NormalAtlasPath = source.Impostor.NormalAtlasPath,
            DepthAtlasPath = source.Impostor.DepthAtlasPath,
            ViewCount = source.Impostor.ViewCount,
            AtlasWidth = source.Impostor.AtlasWidth,
            AtlasHeight = source.Impostor.AtlasHeight,
            Views = Enumerable.Range(0, source.Impostor.ViewCount)
                .Where(index =>
                    index < source.Impostor.ViewDirections.Length &&
                    index < source.Impostor.AtlasRectangles.Length)
                .Select(index => new SceneFoliageImpostorViewDocument
                {
                    Direction = ToSceneVector(
                        source.Impostor.ViewDirections[index]),
                    AtlasRectangle = new SceneVector4(
                        source.Impostor.AtlasRectangles[index].X,
                        source.Impostor.AtlasRectangles[index].Y,
                        source.Impostor.AtlasRectangles[index].Z,
                        source.Impostor.AtlasRectangles[index].W)
                })
                .ToList(),
            SourceBounds = new SceneBoundingBox(
                ToSceneVector(source.Impostor.SourceBounds.Min),
                ToSceneVector(source.Impostor.SourceBounds.Max)),
            Pivot = ToSceneVector(source.Impostor.Pivot),
            Scale = source.Impostor.Scale,
            ContentHash = source.Impostor.ContentHash
        },
        Lod = new SceneFoliageLodDocument { Lod0Distance = source.Lod.Lod0Distance, Lod1Distance = source.Lod.Lod1Distance, Lod2Distance = source.Lod.Lod2Distance },
        Wind = new SceneFoliageWindDocument { Strength = source.Wind.Strength, Frequency = source.Wind.Frequency, Flutter = source.Wind.Flutter },
        Lighting = new SceneFoliageLightingDocument { WrapDiffuse = source.Lighting.WrapDiffuse, Backlight = source.Lighting.Backlight, NormalBend = source.Lighting.NormalBend }
    };

    private static SceneFoliagePatchDocument ToFoliagePatch(FoliagePatch source) => new()
    {
        Id = source.Id, Name = source.Name, PrototypeId = source.Prototype.Id,
        Bounds = new SceneBoundingBox(ToSceneVector(source.Bounds.Min), ToSceneVector(source.Bounds.Max)), InstancePosition = ToSceneVector(source.InstancePosition),
        InstanceScale = source.InstanceScale, Density = source.Density, Seed = source.Seed,
        PlacementMode = source.PlacementMode.ToString(),
        Placement = new SceneFoliagePlacementDocument
        {
            Density = source.Placement.Density,
            MinimumSpacing = source.Placement.MinimumSpacing,
            ScaleRange = new SceneVector2(source.Placement.ScaleRange.X, source.Placement.ScaleRange.Y),
            YawRangeDegrees = new SceneVector2(source.Placement.YawRangeDegrees.X, source.Placement.YawRangeDegrees.Y),
            AlignToSurfaceNormal = source.Placement.AlignToSurfaceNormal,
            AltitudeRange = new SceneVector2(source.Placement.AltitudeRange.X, source.Placement.AltitudeRange.Y),
            SlopeRangeDegrees = new SceneVector2(source.Placement.SlopeRangeDegrees.X, source.Placement.SlopeRangeDegrees.Y),
            BiomeMask = source.Placement.BiomeMask,
            AllowWater = source.Placement.AllowWater,
            AllowRoads = source.Placement.AllowRoads,
            RespectExclusions = source.Placement.RespectExclusions,
            Seed = source.Placement.Seed,
            CellSize = source.Placement.CellSize
        },
        DensityMap = source.DensityMap == null ? null : new SceneFoliageDensityMapDocument
        {
            SourcePath = source.DensityMap.SourcePath,
            ContentHash = source.DensityMap.ContentHash,
            Width = source.DensityMap.Width,
            Height = source.DensityMap.Height,
            Format = source.DensityMap.Format.ToString(),
            WorldToUvScale = new SceneVector2(source.DensityMap.WorldToUvScale.X, source.DensityMap.WorldToUvScale.Y),
            WorldToUvOffset = new SceneVector2(source.DensityMap.WorldToUvOffset.X, source.DensityMap.WorldToUvOffset.Y),
            Revision = source.DensityMap.Revision
        },
        Visible = source.Visible
    };

    private static SceneParticleEffectDocument ToParticleEffect(ParticleEffectInstance source, Dictionary<string, string?> dependencies) => new()
    {
        Id = source.Id, Name = source.Name, Effect = ToAsset(source.AssetReference, source.Id, source.Name, dependencies), Transform = ToTransform(source.WorldMatrix),
        Visible = source.Visible, Playing = source.Playing, Paused = source.Paused, Stopped = source.Stopped, RandomSeed = source.RandomSeed
    };

    private static SceneAssetReferenceDocument ToAsset(SceneAssetReference? source, Guid id, string name, Dictionary<string, string?> dependencies)
    {
        if (source == null)
            throw new InvalidOperationException($"Scene entity '{name}' ({id}) has no source asset reference and cannot be serialized.");
        source.Validate();
        if (dependencies.TryGetValue(source.Path, out string? existingHash))
        {
            if (!string.Equals(existingHash, source.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Asset '{source.Path}' is referenced with conflicting content hashes.");
        }
        else
            dependencies.Add(source.Path, source.ContentHash);
        return new SceneAssetReferenceDocument(source.Path, source.SubObject, source.ContentHash);
    }

    private static void AddDependency(
        SceneAssetReferenceDocument source,
        Guid id,
        string name,
        Dictionary<string, string?> dependencies)
    {
        if (string.IsNullOrWhiteSpace(source.Path))
            throw new InvalidOperationException(
                $"Scene light '{name}' ({id}) has an empty IES profile path.");
        if (dependencies.TryGetValue(source.Path, out string? existingHash) &&
            !string.Equals(existingHash, source.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Asset '{source.Path}' is referenced with conflicting content hashes.");
        }
        dependencies[source.Path] = source.ContentHash;
    }

    private static SceneTransformDocument ToTransform(Matrix4x4 world)
    {
        var numeric = new System.Numerics.Matrix4x4(
            world.M11, world.M12, world.M13, world.M14,
            world.M21, world.M22, world.M23, world.M24,
            world.M31, world.M32, world.M33, world.M34,
            world.M41, world.M42, world.M43, world.M44);
        if (!System.Numerics.Matrix4x4.Decompose(numeric, out System.Numerics.Vector3 numericScale, out System.Numerics.Quaternion numericRotation, out System.Numerics.Vector3 numericPosition))
            throw new InvalidOperationException("A static batch or particle instance contains a non-TRS matrix that the source scene format cannot represent.");
        Vector3 scale = new(numericScale.X, numericScale.Y, numericScale.Z);
        Quaternion rotation = new(numericRotation.X, numericRotation.Y, numericRotation.Z, numericRotation.W);
        Vector3 position = new(numericPosition.X, numericPosition.Y, numericPosition.Z);
        return new SceneTransformDocument { Position = ToSceneVector(position), Rotation = ToSceneQuaternion(rotation), Scale = ToSceneVector(scale) };
    }

    private static SceneVector3 ToSceneVector(Vector3 source) => new(source.X, source.Y, source.Z);
    private static SceneQuaternion ToSceneQuaternion(Quaternion source) => new(source.X, source.Y, source.Z, source.W);
    private static SceneColor ToSceneColor(Color source) => new(source.R, source.G, source.B, source.A);
}
