using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;

namespace NjulfHelloGame;

internal sealed class SampleSceneLoader
{
    private readonly IContentManager _content;
    private readonly MaterialManager _materialManager;
    private readonly SampleAssetManifest _manifest;
    private readonly List<RenderObject> _modelObjects = new();

    public SampleSceneLoader(
        IContentManager content,
        MaterialManager materialManager,
        SampleAssetManifest manifest)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));

        if (string.IsNullOrWhiteSpace(_manifest.ModelPath))
            throw new ArgumentException("The sample asset manifest must specify a model path.", nameof(manifest));
        if (_manifest.AddendumModelPaths == null)
            throw new ArgumentException("The sample asset manifest addendum model paths cannot be null.", nameof(manifest));
        foreach (string addendumModelPath in _manifest.AddendumModelPaths)
        {
            if (string.IsNullOrWhiteSpace(addendumModelPath))
                throw new ArgumentException("The sample asset manifest addendum model paths cannot be empty.", nameof(manifest));
        }
    }

    public IReadOnlyList<RenderObject> ModelObjects => _modelObjects;

    public Model Load(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        Model model = LoadModelInstance(_manifest.ModelPath);
        var addendumModels = new List<Model>(_manifest.AddendumModelPaths.Count);

        foreach (string addendumModelPath in _manifest.AddendumModelPaths)
            addendumModels.Add(LoadModelInstance(addendumModelPath));

        scene.Name = "Njulf Hello Scene";
        scene.AmbientLight = _manifest.AmbientLight;
        RemoveLoadedObjects(scene);

        CoreMatrix4x4 modelWorld = _manifest.CreateModelWorld(rotation: 0f);

        AddModelToScene(scene, model, modelWorld);
        foreach (Model addendumModel in addendumModels)
            AddModelToScene(scene, addendumModel, modelWorld);

        return model;
    }

    public void ApplyModelRotation(float rotation)
    {
        if (_modelObjects.Count == 0)
            return;

        CoreMatrix4x4 modelWorld = _manifest.CreateModelWorld(rotation);

        foreach (RenderObject renderObject in _modelObjects)
            renderObject.WorldMatrix = modelWorld;
    }

    private Model LoadModelInstance(string modelPath)
    {
        Model modelAsset = _content.Load<Model>(modelPath)
            ?? throw new InvalidOperationException($"Content manager returned null for sample model '{modelPath}'.");
        Model model = modelAsset.CreateInstance()
            ?? throw new InvalidOperationException($"Sample model '{modelPath}' did not create an instance.");
        ValidateUploadedModel(model, modelPath);

        return model;
    }

    private void AddModelToScene(Scene scene, Model model, CoreMatrix4x4 modelWorld)
    {
        foreach (RenderObject renderObject in model.RenderObjects)
        {
            renderObject.WorldMatrix = modelWorld;
            renderObject.Visible = true;
            scene.Add(renderObject);
            _modelObjects.Add(renderObject);
        }
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

        _modelObjects.Clear();
    }
}
