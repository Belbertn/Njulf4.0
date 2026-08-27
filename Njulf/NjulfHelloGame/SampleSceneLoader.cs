using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Core.Foliage;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Njulf.Assets.Scenes;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal sealed class SampleSceneLoader
{
    private readonly IContentManager _content;
    private readonly MaterialManager _materialManager;
    private readonly MeshManager _meshManager;
    private readonly SampleAssetManifest _manifest;
    private readonly LightManager _lightManager;
    private readonly bool _loadSceneDocument;
    private readonly SampleSponzaFixtureMode _sponzaFixtureMode;
    private readonly List<RenderObject> _modelObjects = new();
    private readonly List<RenderObject> _stressObjects = new();
    private readonly List<StaticInstanceBatch> _stressBatches = new();
    private BoundingBox? _loadedModelBounds;
    private ModelLightRuntimeController? _modelLightController;

    public SampleSceneLoader(
        IContentManager content,
        MaterialManager materialManager,
        MeshManager meshManager,
        LightManager lightManager,
        SampleAssetManifest manifest,
        bool loadSceneDocument = false,
        SampleSponzaFixtureMode sponzaFixtureMode =
            SampleSponzaFixtureMode.Architecture)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _loadSceneDocument = loadSceneDocument;
        _sponzaFixtureMode = sponzaFixtureMode;

        if (!Enum.IsDefined(_sponzaFixtureMode))
            throw new ArgumentOutOfRangeException(nameof(sponzaFixtureMode));

        if (string.IsNullOrWhiteSpace(_manifest.ModelPath))
            throw new ArgumentException("The sample asset manifest must specify a model path.", nameof(manifest));
        if (_manifest.AddendumModelPaths == null)
            throw new ArgumentException("The sample asset manifest addendum model paths cannot be null.", nameof(manifest));
        if (_manifest.FoliageModelPaths == null)
            throw new ArgumentException("The sample asset manifest foliage model paths cannot be null.", nameof(manifest));
        foreach (string addendumModelPath in _manifest.AddendumModelPaths)
        {
            if (string.IsNullOrWhiteSpace(addendumModelPath))
                throw new ArgumentException("The sample asset manifest addendum model paths cannot be empty.", nameof(manifest));
        }
        foreach (string foliageModelPath in _manifest.FoliageModelPaths)
        {
            if (string.IsNullOrWhiteSpace(foliageModelPath))
                throw new ArgumentException("The sample asset manifest foliage model paths cannot be empty.", nameof(manifest));
        }
    }

    public IReadOnlyList<RenderObject> ModelObjects => _modelObjects;
    public BoundingBox? LoadedModelBounds => _loadedModelBounds;
    public bool LoadedFromDocument { get; private set; }
    public string ScenePath { get; } = Path.Combine(AppContext.BaseDirectory, "Scenes", "SampleScene.njscene.json");

    public Model Load(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        LoadedFromDocument = _loadSceneDocument && !string.Equals(
            Environment.GetEnvironmentVariable("NJULF_SAMPLE_CODE_SCENE"),
            "true",
            StringComparison.OrdinalIgnoreCase) && File.Exists(ScenePath);
        if (LoadedFromDocument)
        {
            RemoveLoadedObjects(scene);
            _loadedModelBounds = null;
            SceneDocument document = SceneDocumentJson.Read(ScenePath);
            ApplySponzaFixtureMode(document, _sponzaFixtureMode);
            new SceneDocumentLoader(_content).Populate(
                document,
                scene,
                new LightManagerSceneLightStore(_lightManager),
                materials: new MaterialManagerSceneMaterialOverrideStore(_materialManager));
            _modelObjects.AddRange(scene.RenderObjects);
            foreach (RenderObject renderObject in scene.RenderObjects)
                IncludeRenderObjectBounds(renderObject);
            AddStressSceneIfRequested(scene);

            // SceneDocumentLoader already loaded the authoritative model and
            // transferred instance render objects into the scene. Returning a
            // second instance here would leave its resource leases without an
            // owner after this method returns. The cached asset is sufficient
            // for the read-only scene summary and remains owned by ContentManager.
            return LoadModelAsset(_manifest.ModelPath);
        }

        Model model = LoadModelInstance(_manifest.ModelPath);
        var addendumModels = new List<Model>(_manifest.AddendumModelPaths.Count);

        foreach (string addendumModelPath in _manifest.AddendumModelPaths)
            addendumModels.Add(LoadModelInstance(addendumModelPath));
        var foliageModels = new List<Model>(_manifest.FoliageModelPaths.Count);
        foreach (string foliageModelPath in _manifest.FoliageModelPaths)
            foliageModels.Add(LoadModelInstance(foliageModelPath));

        scene.Name = "Njulf Hello Scene";
        scene.AmbientLight = _manifest.AmbientLight;
        RemoveLoadedObjects(scene);
        _loadedModelBounds = null;

        CoreMatrix4x4 modelWorld = _manifest.CreateModelWorld(rotation: 0f);

        AddModelToScene(scene, model, _manifest.ModelPath, modelWorld);
        for (int i = 0; i < addendumModels.Count; i++)
            AddModelToScene(scene, addendumModels[i], _manifest.AddendumModelPaths[i], modelWorld);
        for (int i = 0; i < foliageModels.Count; i++)
            AddModelAsFoliage(scene, foliageModels[i], _manifest.FoliageModelPaths[i], modelWorld);
        AddStressSceneIfRequested(scene);

        if (_manifest.EnableImportedModelLights)
        {
            _modelLightController = ModelLightRuntimeController.Attach(
                scene,
                _content,
                new LightManagerSceneLightStore(_lightManager),
                LoadModelAsset);
        }

        return model;
    }

    internal static void ApplySponzaFixtureMode(
        SceneDocument document,
        SampleSponzaFixtureMode fixtureMode)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(fixtureMode))
            throw new ArgumentOutOfRangeException(nameof(fixtureMode));
        if (fixtureMode == SampleSponzaFixtureMode.AnimationDemo)
            return;

        document.Objects.RemoveAll(static item =>
            string.Equals(
                item.Model.Path,
                SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                StringComparison.Ordinal));
        document.Dependencies.RemoveAll(static item =>
            string.Equals(
                item.Path,
                SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                StringComparison.Ordinal));
    }

    public void EnableImportedModelLights()
    {
        if (_modelLightController == null ||
            _modelLightController.ImportedModelLightsEnabled)
        {
            return;
        }

        _modelLightController.SetImportedModelLightsEnabled(true);
        if (_modelLightController.ImportedLightDefinitionCount == 0)
        {
            Console.WriteLine(
                $"'{_manifest.ModelPath}' defines no glTF punctual lights; " +
                "its emissive materials remain part of the model import.");
            return;
        }

        Console.WriteLine(
            $"Imported model lights: definitions={_modelLightController.ImportedLightDefinitionCount}, " +
            $"active={_modelLightController.ActiveLightCount}.");
    }

    public void ApplyModelRotation(float rotation)
    {
        if (_modelObjects.Count == 0)
            return;

        CoreMatrix4x4 modelWorld = _manifest.CreateModelWorld(rotation);

        foreach (RenderObject renderObject in _modelObjects)
            renderObject.WorldMatrix = modelWorld;
    }

    private void AddStressSceneIfRequested(Scene scene)
    {
        string? mode = Environment.GetEnvironmentVariable("NJULF_SAMPLE_STRESS_MODE");
        if (!string.Equals(mode, "batch", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "objects", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RenderObject? source = _modelObjects.Count > 0 ? _modelObjects[0] : null;
        if (source?.Mesh is not MeshHandle || source.Material is not MaterialHandle)
            return;

        int count = 1000;
        string? countText = Environment.GetEnvironmentVariable("NJULF_SAMPLE_STRESS_COUNT");
        if (int.TryParse(countText, out int requestedCount))
            count = Math.Clamp(requestedCount, 1, 5000);

        List<CoreMatrix4x4> transforms = CreateStressTransforms(count);
        if (string.Equals(mode, "batch", StringComparison.OrdinalIgnoreCase))
        {
            var batch = new StaticInstanceBatch(transforms)
            {
                Name = $"StaticStressBatch_{count}",
                Mesh = source.Mesh,
                Material = source.Material,
                Visible = true
            };
            scene.Add(batch);
            _stressBatches.Add(batch);
            return;
        }

        for (int i = 0; i < transforms.Count; i++)
        {
            var renderObject = new RenderObject(source.Mesh!, source.Material!)
            {
                Name = $"StaticStressObject_{i}",
                WorldMatrix = transforms[i],
                Visible = true,
                IsStatic = true
            };
            scene.Add(renderObject);
            _stressObjects.Add(renderObject);
        }
    }

    private static List<CoreMatrix4x4> CreateStressTransforms(int count)
    {
        var transforms = new List<CoreMatrix4x4>(count);
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        const float spacing = 4.0f;
        float half = (side - 1) * spacing * 0.5f;

        for (int i = 0; i < count; i++)
        {
            int x = i % side;
            int z = i / side;
            transforms.Add(CoreMatrix4x4.CreateTranslation(new Njulf.Core.Math.Vector3(
                x * spacing - half,
                0f,
                z * spacing - half + 20f)));
        }

        return transforms;
    }

    private Model LoadModelInstance(string modelPath)
    {
        Model modelAsset = LoadModelAsset(modelPath);
        Model model = modelAsset.CreateInstance()
            ?? throw new InvalidOperationException($"Sample model '{modelPath}' did not create an instance.");
        ValidateUploadedModel(model, modelPath);

        return model;
    }

    private Model LoadModelAsset(string modelPath)
    {
        Model modelAsset = (_content as ContentManager)?.Load<Model>(
            modelPath,
            CreateModelLoadOptions(modelPath)) ??
            _content.Load<Model>(modelPath)
            ?? throw new InvalidOperationException($"Content manager returned null for sample model '{modelPath}'.");
        ValidateUploadedModel(modelAsset, modelPath);
        return modelAsset;
    }

    /// <summary>
    /// Loads scene metadata with the same importer semantics as the visible
    /// sample model. Editor-side light discovery must not issue a second,
    /// default-convention request for an already-authored placement.
    /// </summary>
    internal Model LoadModelForRuntimeMetadata(string modelPath)
    {
        bool declaredByManifest = EnumerateManifestAssets().Any(candidate =>
            string.Equals(candidate.Path, modelPath, StringComparison.Ordinal));
        if (declaredByManifest)
            return LoadModelAsset(modelPath);

        return _content.Load<Model>(modelPath) ??
            throw new InvalidOperationException(
                $"Content manager returned null for scene model '{modelPath}'.");
    }

    private ContentLoadOptions CreateModelLoadOptions(string modelPath)
    {
        SampleAssetReference? asset = EnumerateManifestAssets()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Path,
                modelPath,
                StringComparison.Ordinal));
        if (asset == null)
        {
            throw new InvalidOperationException(
                $"Model '{modelPath}' is not declared by the sample asset manifest.");
        }

        return new ContentLoadOptions
        {
            ImporterOptions = new ImporterOptions
            {
                Backend = asset.ExpectedBackend,
                AssimpMaterialTextureConvention = asset.AssimpMaterialTextureConvention,
                ImportLights = true
            }
        };
    }

    private IEnumerable<SampleAssetReference> EnumerateManifestAssets()
    {
        yield return _manifest.ModelAsset;
        foreach (SampleAssetReference asset in _manifest.AddendumModelAssets)
            yield return asset;
        foreach (SampleAssetReference asset in _manifest.FoliageModelAssets)
            yield return asset;
    }

    private void AddModelToScene(Scene scene, Model model, string modelPath, CoreMatrix4x4 modelWorld)
    {
        for (int i = 0; i < model.RenderObjects.Count; i++)
        {
            RenderObject renderObject = model.RenderObjects[i];
            renderObject.AssetReference = new SceneAssetReference { Path = modelPath, SubObject = i.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            renderObject.WorldMatrix = modelWorld;
            renderObject.Visible = true;
            renderObject.IsStatic = renderObject is not SkinnedRenderObject;
            scene.Add(renderObject);
            _modelObjects.Add(renderObject);
            IncludeRenderObjectBounds(renderObject);
        }
    }

    private void AddModelAsFoliage(Scene scene, Model model, string modelPath, CoreMatrix4x4 modelWorld)
    {
        uint seed = 0x17A1_0000u;
        for (int objectIndex = 0; objectIndex < model.RenderObjects.Count; objectIndex++)
        {
            RenderObject renderObject = model.RenderObjects[objectIndex];
            var assetReference = new SceneAssetReference { Path = modelPath, SubObject = objectIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                continue;
            if (renderObject.Material is not MaterialHandle materialHandle || !materialHandle.IsValid)
                continue;

            if (IsRigidFoliageGeometry(renderObject.Name))
            {
                renderObject.WorldMatrix = modelWorld;
                renderObject.Visible = true;
                renderObject.IsStatic = true;
                renderObject.AssetReference = assetReference;
                scene.Add(renderObject);
                _modelObjects.Add(renderObject);
                IncludeRenderObjectBounds(renderObject);
                continue;
            }

            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            BoundingBox bounds = TransformBounds(
                new BoundingBox(
                    ToCoreVector(meshInfo.BoundingBoxMin),
                    ToCoreVector(meshInfo.BoundingBoxMax)),
                modelWorld);

            var prototype = new FoliagePrototype
            {
                Name = $"Foliage.{renderObject.Name}",
                Mesh = meshHandle,
                Material = materialHandle,
                GeometryMode = FoliageGeometryMode.BillboardCards,
                AssetReference = assetReference
            };
            prototype.CardHeight = 0.26f;
            prototype.CardWidth = 0.18f;
            prototype.AuthoredMeshletStride = 1u;
            prototype.Lod.Lod0Distance = 5f;
            prototype.Lod.Lod1Distance = 10f;
            prototype.Lod.Lod2Distance = 180f;
            prototype.Wind.Strength = renderObject.Name.Contains("Leaves", StringComparison.OrdinalIgnoreCase) ? 0.18f : 0.04f;
            prototype.Wind.Frequency = 0.65f;
            prototype.Wind.Flutter = 0.08f;
            prototype.Lighting.WrapDiffuse = 0.42f;
            prototype.Lighting.Backlight = 0.30f;

            scene.Add(new FoliagePatch(prototype, bounds)
            {
                Name = $"FoliagePatch.{renderObject.Name}",
                Density = 48f,
                Seed = seed++,
                InstancePosition = ExtractTranslation(modelWorld),
                InstanceScale = ExtractUniformScale(modelWorld),
                Visible = renderObject.Visible
            });
            IncludeBounds(bounds);
        }
    }

    private void IncludeRenderObjectBounds(RenderObject renderObject)
    {
        if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
            return;

        MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
        IncludeBounds(TransformBounds(
            new BoundingBox(
                ToCoreVector(meshInfo.BoundingBoxMin),
                ToCoreVector(meshInfo.BoundingBoxMax)),
            renderObject.WorldMatrix));
    }

    private void IncludeBounds(BoundingBox bounds)
    {
        _loadedModelBounds = _loadedModelBounds.HasValue
            ? Union(_loadedModelBounds.Value, bounds)
            : bounds;
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right) =>
        new(CoreVector3.Min(left.Min, right.Min), CoreVector3.Max(left.Max, right.Max));

    private static bool IsRigidFoliageGeometry(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name.Contains("Lianas", StringComparison.OrdinalIgnoreCase);
    }

    private static CoreVector3 ExtractTranslation(CoreMatrix4x4 world)
    {
        return new CoreVector3(world.M41, world.M42, world.M43);
    }

    private static float ExtractUniformScale(CoreMatrix4x4 world)
    {
        float x = MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13);
        float y = MathF.Sqrt(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23);
        float z = MathF.Sqrt(world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33);
        float scale = (x + y + z) / 3f;
        return float.IsFinite(scale) && scale > 0f ? scale : 1f;
    }

    private static BoundingBox TransformBounds(BoundingBox bounds, CoreMatrix4x4 world)
    {
        CoreVector3 min = bounds.Min;
        CoreVector3 max = bounds.Max;
        Span<CoreVector3> corners = stackalloc CoreVector3[8]
        {
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, max.Z)
        };

        CoreVector3 transformedMin = TransformPoint(corners[0], world);
        CoreVector3 transformedMax = transformedMin;
        for (int i = 1; i < corners.Length; i++)
        {
            CoreVector3 point = TransformPoint(corners[i], world);
            transformedMin = CoreVector3.Min(transformedMin, point);
            transformedMax = CoreVector3.Max(transformedMax, point);
        }

        return new BoundingBox(transformedMin, transformedMax);
    }

    private static CoreVector3 TransformPoint(CoreVector3 point, CoreMatrix4x4 world)
    {
        return new CoreVector3(
            point.X * world.M11 + point.Y * world.M21 + point.Z * world.M31 + world.M41,
            point.X * world.M12 + point.Y * world.M22 + point.Z * world.M32 + world.M42,
            point.X * world.M13 + point.Y * world.M23 + point.Z * world.M33 + world.M43);
    }

    private static CoreVector3 ToCoreVector(System.Numerics.Vector3 value)
    {
        return new CoreVector3(value.X, value.Y, value.Z);
    }

    private void ValidateUploadedModel(Model model, string modelPath)
    {
        if (model.RenderObjects.Count == 0)
            throw new InvalidOperationException($"Sample model '{modelPath}' did not produce renderable objects.");

        for (int i = 0; i < model.RenderObjects.Count; i++)
        {
            RenderObject renderObject = model.RenderObjects[i];

            if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                throw new InvalidOperationException($"Sample model '{modelPath}' render object '{renderObject.Name}' does not contain a valid GPU mesh handle.");
            if (renderObject.Material is not MaterialHandle materialHandle || !materialHandle.IsValid)
                throw new InvalidOperationException($"Sample model '{modelPath}' render object '{renderObject.Name}' does not contain a valid GPU material handle.");

            try
            {
                MaterialManager.ValidateMaterialTextureIndices(_materialManager.GetMaterialData(materialHandle));
                GPUMaterialExtensionData? extensionData = _materialManager.GetMaterialExtensionData(materialHandle);
                if (extensionData.HasValue)
                    MaterialManager.ValidateMaterialExtensionTextureIndices(extensionData.Value);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Sample model '{modelPath}' render object '{renderObject.Name}' has invalid material texture indices.",
                    ex);
            }
        }
    }

    private void RemoveLoadedObjects(Scene scene)
    {
        foreach (RenderObject renderObject in _modelObjects)
            scene.Remove(renderObject);
        foreach (RenderObject renderObject in _stressObjects)
            scene.Remove(renderObject);
        foreach (StaticInstanceBatch batch in _stressBatches)
            scene.Remove(batch);

        _modelObjects.Clear();
        _stressObjects.Clear();
        _stressBatches.Clear();
    }
}
