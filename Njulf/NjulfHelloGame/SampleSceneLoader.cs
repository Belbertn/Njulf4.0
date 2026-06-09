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
    }

    public IReadOnlyList<RenderObject> ModelObjects => _modelObjects;

    public Model Load(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        Model model = _content.Load<Model>(_manifest.ModelPath);
        ValidateUploadedModel(model);

        scene.Name = "Njulf Hello Scene";
        scene.AmbientLight = _manifest.AmbientLight;
        _modelObjects.Clear();

        CoreMatrix4x4 modelWorld = _manifest.CreateModelWorld(rotation: 0f);

        foreach (RenderObject renderObject in model.RenderObjects)
        {
            renderObject.WorldMatrix = modelWorld;
            renderObject.Visible = true;
            scene.Add(renderObject);
            _modelObjects.Add(renderObject);
        }

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

    private void ValidateUploadedModel(Model model)
    {
        if (model.RenderObjects.Count == 0)
            throw new InvalidOperationException("The sample model did not produce renderable objects.");

        for (int i = 0; i < model.RenderObjects.Count; i++)
        {
            RenderObject renderObject = model.RenderObjects[i];

            if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                throw new InvalidOperationException($"Render object '{renderObject.Name}' does not contain a valid GPU mesh handle.");
            if (renderObject.Material is not MaterialHandle materialHandle || !materialHandle.IsValid)
                throw new InvalidOperationException($"Render object '{renderObject.Name}' does not contain a valid GPU material handle.");

            MaterialManager.ValidateMaterialTextureIndices(_materialManager.GetMaterialData(materialHandle));
        }
    }
}
