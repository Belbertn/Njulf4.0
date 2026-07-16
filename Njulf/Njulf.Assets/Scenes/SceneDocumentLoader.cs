using System;
using System.Collections.Generic;
using Njulf.Core.Foliage;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>Materializes a source scene document without taking a renderer dependency.</summary>
public sealed class SceneDocumentLoader
{
    private readonly IContentManager _content;

    public SceneDocumentLoader(IContentManager content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public Scene Load(SceneDocument document, ISceneLightStore? lights = null, ISceneParticleEffectStore? particleEffects = null, ISceneMaterialOverrideStore? materials = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        var scene = new Scene { Id = document.Id, Name = document.Name, AmbientLight = ToColor(document.AmbientLight) };
        Populate(document, scene, lights, particleEffects, materials);
        return scene;
    }

    public void Populate(SceneDocument document, Scene scene, ISceneLightStore? lights = null, ISceneParticleEffectStore? particleEffects = null, ISceneMaterialOverrideStore? materials = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(scene);
        ValidateDocument(document);

        if (scene.RenderObjects.Count != 0 || scene.ReflectionProbes.Count != 0 || scene.GlobalIlluminationProbeVolumes.Count != 0 ||
            scene.StaticInstanceBatches.Count != 0 || scene.FoliagePatches.Count != 0 || scene.ParticleEffects.Count != 0)
        {
            throw new InvalidOperationException("SceneDocumentLoader.Populate requires an empty scene. Clear and dispose the destination before reloading.");
        }

        scene.Id = document.Id;
        scene.Name = document.Name;
        scene.AmbientLight = ToColor(document.AmbientLight);
        var modelInstances = new Dictionary<string, ModelInstanceCursor>(StringComparer.Ordinal);
        foreach (SceneObjectDocument record in document.Objects)
            LoadObject(scene, record, modelInstances, materials);
        foreach (SceneReflectionProbeDocument record in document.ReflectionProbes)
            scene.Add(ToReflectionProbe(record));
        foreach (SceneGlobalIlluminationProbeVolumeDocument record in document.GiProbeVolumes)
            scene.Add(ToGiProbeVolume(record));

        var prototypes = new Dictionary<Guid, FoliagePrototype>();
        foreach (SceneFoliagePrototypeDocument record in document.FoliagePrototypes)
        {
            FoliagePrototype prototype = LoadFoliagePrototype(record, modelInstances);
            prototypes.Add(record.Id, prototype);
            scene.Add(prototype);
        }
        foreach (SceneFoliagePatchDocument record in document.FoliagePatches)
        {
            if (!prototypes.TryGetValue(record.PrototypeId, out FoliagePrototype? prototype))
                throw new InvalidDataException($"Foliage patch '{record.Name}' ({record.Id}) references missing prototype '{record.PrototypeId}'.");
            scene.Add(new FoliagePatch(prototype, ToBoundingBox(record.Bounds))
            {
                Id = record.Id,
                Name = record.Name,
                InstancePosition = ToVector3(record.InstancePosition),
                InstanceScale = record.InstanceScale,
                Density = record.Density,
                Seed = record.Seed,
                DensityTexturePath = record.DensityTexturePath,
                Visible = record.Visible
            });
        }
        foreach (SceneInstanceBatchDocument record in document.InstanceBatches)
            LoadInstanceBatch(scene, record, modelInstances);

        if (document.ParticleEffects.Count != 0 && particleEffects == null)
            throw new InvalidOperationException("The document contains particle effects, but no ISceneParticleEffectStore was supplied.");
        if (particleEffects != null)
            foreach (SceneParticleEffectDocument record in document.ParticleEffects)
                scene.Add(LoadParticleEffect(record, particleEffects));

        if (lights != null)
        {
            lights.Clear();
            foreach (SceneLightDocument record in document.Lights)
                lights.Add(record.Id, record);
        }
        else if (document.Lights.Count != 0)
        {
            throw new InvalidOperationException("The document contains lights, but no ISceneLightStore was supplied.");
        }
    }

    private void LoadObject(Scene scene, SceneObjectDocument record, Dictionary<string, ModelInstanceCursor> modelInstances, ISceneMaterialOverrideStore? materials)
    {
        RenderObject source = LoadSingleRenderObject(record.Model, record.Id, record.Name, modelInstances);
        source.Id = record.Id;
        source.Name = record.Name;
        source.AssetReference = ToAssetReference(record.Model);
        source.Position = ToVector3(record.Position);
        source.Rotation = ToQuaternion(record.Rotation);
        source.Scale = ToVector3(record.Scale);
        source.Visible = record.Visible;
        source.IsStatic = record.IsStatic;
        if (record.MaterialOverride != null)
        {
            if (materials == null)
                throw new InvalidOperationException($"Scene object '{record.Name}' ({record.Id}) has a material override, but no ISceneMaterialOverrideStore was supplied.");
            materials.Apply(source, record.MaterialOverride);
        }
        scene.Add(source);
        if (source is SkinnedRenderObject { Animator: { Clips.Count: > 0 } animator })
            animator.Play(animator.Clips[0], loop: true);
        if (source is IUpdateable updateable)
            scene.Add(updateable);
    }

    private FoliagePrototype LoadFoliagePrototype(SceneFoliagePrototypeDocument record, Dictionary<string, ModelInstanceCursor> modelInstances)
    {
        RenderObject source = LoadSingleRenderObject(record.Model, record.Id, record.Name, modelInstances);
        return new FoliagePrototype
        {
            Id = record.Id,
            Name = record.Name,
            AssetReference = ToAssetReference(record.Model),
            Mesh = source.Mesh,
            Material = source.Material,
            GeometryMode = ParseEnum<FoliageGeometryMode>(record.GeometryMode, record.Id, record.Name),
            AuthoredMeshletStride = record.AuthoredMeshletStride,
            CardHeight = record.CardHeight,
            CardWidth = record.CardWidth,
            FarImpostorEnabled = record.FarImpostorEnabled
        }.WithSettings(record);
    }

    private void LoadInstanceBatch(Scene scene, SceneInstanceBatchDocument record, Dictionary<string, ModelInstanceCursor> modelInstances)
    {
        RenderObject source = LoadSingleRenderObject(record.Model, record.Id, record.Name, modelInstances);
        var matrices = new List<Matrix4x4>(record.Instances.Count);
        foreach (SceneTransformDocument transform in record.Instances)
            matrices.Add(Compose(transform));
        scene.Add(new StaticInstanceBatch(matrices)
        {
            Id = record.Id,
            Name = record.Name,
            AssetReference = ToAssetReference(record.Model),
            Mesh = source.Mesh,
            Material = source.Material,
            Visible = record.Visible
        });
    }

    private static ParticleEffectInstance LoadParticleEffect(SceneParticleEffectDocument record, ISceneParticleEffectStore effects)
    {
        SceneAssetReference reference = ToAssetReference(record.Effect);
        Njulf.Core.Vfx.ParticleEffect effect = effects.Load(reference)
            ?? throw new InvalidDataException($"Particle effect '{record.Name}' ({record.Id}) could not be loaded from '{reference.Path}'.");
        var instance = new ParticleEffectInstance(effect)
        {
            Id = record.Id,
            Name = record.Name,
            AssetReference = reference,
            WorldMatrix = Compose(record.Transform),
            Visible = record.Visible,
            RandomSeed = record.RandomSeed
        };
        if (record.Stopped)
            instance.Stop(clearParticles: false);
        else if (record.Paused)
            instance.Pause();
        else if (record.Playing)
            instance.Play();
        return instance;
    }

    private RenderObject LoadSingleRenderObject(SceneAssetReferenceDocument asset, Guid recordId, string recordName, Dictionary<string, ModelInstanceCursor> modelInstances)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(asset.Path) || string.IsNullOrWhiteSpace(asset.SubObject))
                throw new InvalidDataException("Model references require non-empty path and sub-object values.");
            if (!modelInstances.TryGetValue(asset.Path, out ModelInstanceCursor? cursor))
            {
                Model model = _content.Load<Model>(asset.Path)
                    ?? throw new InvalidOperationException("Content manager returned null.");
                cursor = new ModelInstanceCursor(model.CreateInstance());
                modelInstances.Add(asset.Path, cursor);
            }
            IReadOnlyList<RenderObject> candidates = cursor.Instance.RenderObjects;
            RenderObject? selected = SelectSubObject(candidates, asset.SubObject);
            if (selected == null)
                throw new InvalidDataException($"Sub-object selector '{asset.SubObject}' matched no render object.");
            if (cursor.Used.Contains(selected))
            {
                Model model = _content.Load<Model>(asset.Path)
                    ?? throw new InvalidOperationException("Content manager returned null.");
                cursor.Reset(model.CreateInstance());
                selected = SelectSubObject(cursor.Instance.RenderObjects, asset.SubObject)
                    ?? throw new InvalidDataException($"Sub-object selector '{asset.SubObject}' matched no render object.");
            }
            cursor.Used.Add(selected);
            return selected;
        }
        catch (Exception error) when (error is not InvalidDataException || !error.Message.Contains(recordId.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unable to load scene record '{recordName}' ({recordId}) from model '{asset.Path}' (sub-object '{asset.SubObject}').", error);
        }
    }

    private sealed class ModelInstanceCursor(Model instance)
    {
        public Model Instance { get; private set; } = instance;
        public HashSet<RenderObject> Used { get; } = [];
        public void Reset(Model next) { Instance = next; Used.Clear(); }
    }

    private static RenderObject? SelectSubObject(IReadOnlyList<RenderObject> objects, string selector)
    {
        if (selector == "*")
        {
            if (objects.Count != 1)
                throw new InvalidDataException($"Selector '*' requires a model with exactly one render object; the model contains {objects.Count}. Use an explicit name or index.");
            return objects[0];
        }
        if (int.TryParse(selector, out int index))
            return index >= 0 && index < objects.Count ? objects[index] : null;
        foreach (RenderObject item in objects)
            if (string.Equals(item.Name, selector, StringComparison.Ordinal))
                return item;
        return null;
    }

    private static ReflectionProbe ToReflectionProbe(SceneReflectionProbeDocument record) => new()
    {
        Id = record.Id, Name = record.Name, Position = ToVector3(record.Position), Rotation = ToQuaternion(record.Rotation),
        Shape = ParseEnum<ReflectionProbeShape>(record.Shape, record.Id, record.Name), BoxExtents = ToVector3(record.BoxExtents), Radius = record.Radius,
        BlendDistance = record.BlendDistance, Intensity = record.Intensity, Priority = record.Priority, CubemapPath = record.CubemapPath, BoxProjection = record.BoxProjection
    };

    private static GlobalIlluminationProbeVolume ToGiProbeVolume(SceneGlobalIlluminationProbeVolumeDocument record) => new()
    {
        Id = record.Id, Name = record.Name, Enabled = record.Enabled, Origin = ToVector3(record.Origin), Size = ToVector3(record.Size), Interior = record.Interior,
        QualityClass = ParseEnum<GlobalIlluminationProbeVolumeQualityClass>(record.QualityClass, record.Id, record.Name), Priority = record.Priority, BlendDistance = record.BlendDistance,
        StreamingCellId = record.StreamingCellId, ProbeCountX = record.ProbeCountX, ProbeCountY = record.ProbeCountY, ProbeCountZ = record.ProbeCountZ,
        RaysPerProbe = record.RaysPerProbe, MaxProbeUpdatesPerFrame = record.MaxProbeUpdatesPerFrame, NormalBias = record.NormalBias, ViewBias = record.ViewBias,
        MaxRayDistance = record.MaxRayDistance, Intensity = record.Intensity, Hysteresis = record.Hysteresis, SteadyHysteresis = record.SteadyHysteresis,
        DirtyHysteresis = record.DirtyHysteresis, UpdatePriority = record.UpdatePriority, DirtyRaysPerProbe = record.DirtyRaysPerProbe
    };

    private static void ValidateDocument(SceneDocument document)
    {
        if (document.SchemaVersion != SceneDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported scene schema version {document.SchemaVersion}.");
        if (document.Id == Guid.Empty)
            throw new InvalidDataException("Scene documents require a non-empty ID.");
        var ids = new HashSet<Guid>();
        AddIds(document.Objects, static item => item.Id, "object");
        AddIds(document.Lights, static item => item.Id, "light");
        AddIds(document.ReflectionProbes, static item => item.Id, "reflection probe");
        AddIds(document.GiProbeVolumes, static item => item.Id, "GI probe volume");
        AddIds(document.InstanceBatches, static item => item.Id, "instance batch");
        AddIds(document.FoliagePrototypes, static item => item.Id, "foliage prototype");
        AddIds(document.FoliagePatches, static item => item.Id, "foliage patch");
        AddIds(document.ParticleEffects, static item => item.Id, "particle effect");
        return;
        void AddIds<T>(IEnumerable<T> records, Func<T, Guid> getId, string kind)
        {
            foreach (T record in records)
            {
                Guid id = getId(record);
                if (id == Guid.Empty || !ids.Add(id))
                    throw new InvalidDataException($"Scene contains an invalid or duplicate {kind} ID '{id}'.");
            }
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, Guid id, string name) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : throw new InvalidDataException($"Scene record '{name}' ({id}) contains unsupported {typeof(TEnum).Name} value '{value}'.");
    private static SceneAssetReference ToAssetReference(SceneAssetReferenceDocument source) => new() { Path = source.Path, SubObject = source.SubObject, ContentHash = source.ContentHash };
    private static Vector3 ToVector3(SceneVector3 source) => new(source.X, source.Y, source.Z);
    private static Quaternion ToQuaternion(SceneQuaternion source) => new Quaternion(source.X, source.Y, source.Z, source.W).Normalized();
    private static Color ToColor(SceneColor source) => new(source.R, source.G, source.B, source.A);
    private static BoundingBox ToBoundingBox(SceneBoundingBox source) => new(ToVector3(source.Min), ToVector3(source.Max));
    private static Matrix4x4 Compose(SceneTransformDocument source) => Matrix4x4.CreateScale(ToVector3(source.Scale)) * ToQuaternion(source.Rotation).ToMatrix4x4() * Matrix4x4.CreateTranslation(ToVector3(source.Position));
}

internal static class SceneFoliagePrototypeDocumentExtensions
{
    public static FoliagePrototype WithSettings(this FoliagePrototype prototype, SceneFoliagePrototypeDocument source)
    {
        prototype.Lod.Lod0Distance = source.Lod.Lod0Distance;
        prototype.Lod.Lod1Distance = source.Lod.Lod1Distance;
        prototype.Lod.Lod2Distance = source.Lod.Lod2Distance;
        prototype.Wind.Strength = source.Wind.Strength;
        prototype.Wind.Frequency = source.Wind.Frequency;
        prototype.Wind.Flutter = source.Wind.Flutter;
        prototype.Lighting.WrapDiffuse = source.Lighting.WrapDiffuse;
        prototype.Lighting.Backlight = source.Lighting.Backlight;
        prototype.Lighting.NormalBend = source.Lighting.NormalBend;
        prototype.MarkSettingsChanged();
        return prototype;
    }
}
