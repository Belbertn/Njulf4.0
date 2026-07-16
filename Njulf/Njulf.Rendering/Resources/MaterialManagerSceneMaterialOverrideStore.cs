using System;
using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Persists the editable v1 material surface while retaining texture bindings and flags.</summary>
public sealed class MaterialManagerSceneMaterialOverrideStore : ISceneMaterialOverrideStore
{
    private readonly MaterialManager _materials;

    public MaterialManagerSceneMaterialOverrideStore(MaterialManager materials)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    public void Apply(RenderObject renderObject, SceneMaterialOverrideDocument source)
    {
        if (renderObject.Material is not MaterialHandle handle)
            throw new InvalidOperationException($"Scene object '{renderObject.Name}' ({renderObject.Id}) has no material handle.");
        GPUMaterialData material = _materials.GetMaterialData(handle);
        material.Albedo = new Vector4(source.Albedo.R, source.Albedo.G, source.Albedo.B, source.Albedo.A);
        material.Emissive = new Vector4(source.Emissive.R, source.Emissive.G, source.Emissive.B, source.Emissive.A);
        material.MetallicRoughnessAO.X = source.Metallic;
        material.MetallicRoughnessAO.Y = source.Roughness;
        material.NormalScaleBias.X = source.NormalScale;
        material.NormalScaleBias.Z = source.AlphaCutoff;
        _materials.UpdateMaterial(handle, material);
    }

    public SceneMaterialOverrideDocument? Capture(RenderObject renderObject)
    {
        if (renderObject.Material is not MaterialHandle handle)
            return null;
        GPUMaterialData material = _materials.GetMaterialData(handle);
        return new SceneMaterialOverrideDocument
        {
            Albedo = new SceneColor(material.Albedo.X, material.Albedo.Y, material.Albedo.Z, material.Albedo.W),
            Emissive = new SceneColor(material.Emissive.X, material.Emissive.Y, material.Emissive.Z, material.Emissive.W),
            Metallic = material.MetallicRoughnessAO.X,
            Roughness = material.MetallicRoughnessAO.Y,
            NormalScale = material.NormalScaleBias.X,
            AlphaCutoff = material.NormalScaleBias.Z
        };
    }
}
