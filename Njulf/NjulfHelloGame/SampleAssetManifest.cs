using Njulf.Core.Math;
using Njulf.Assets;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal enum SampleAssetLoadTier
{
    Critical,
    Deferred
}

internal sealed record SampleAssetReference(
    string Path,
    ModelImportBackend ExpectedBackend,
    AssimpMaterialTextureConvention AssimpMaterialTextureConvention =
        AssimpMaterialTextureConvention.Standard,
    SampleAssetLoadTier LoadTier = SampleAssetLoadTier.Critical,
    float MaximumSamplerAnisotropy = 16f,
    bool RequireCooked = false)
{
    public string CreateContentIdentity() =>
        $"{ExpectedBackend}\u001f" +
        $"{AssimpMaterialTextureConvention}\u001f" +
        $"{MaximumSamplerAnisotropy:R}\u001f" +
        $"{RequireCooked}\u001f{Path}";

    public ContentLoadOptions CreateLoadOptions()
    {
        return new ContentLoadOptions
        {
            ImporterOptions = new ImporterOptions
            {
                Backend = ExpectedBackend,
                AssimpMaterialTextureConvention =
                    AssimpMaterialTextureConvention,
                MaximumSamplerAnisotropy = MaximumSamplerAnisotropy,
                ImportLights = true
            },
            RequireCooked = RequireCooked
        };
    }
}

internal sealed record SampleAssetManifest(
    SampleAssetReference ModelAsset,
    IReadOnlyList<SampleAssetReference> AddendumModelAssets,
    IReadOnlyList<SampleAssetReference> FoliageModelAssets,
    float ModelScale,
    CoreVector3 ModelPosition,
    float RotationSpeed,
    Color AmbientLight,
    bool EnableImportedModelLights)
{
    public string ModelPath => ModelAsset.Path;
    public IReadOnlyList<string> AddendumModelPaths => AddendumModelAssets.Select(asset => asset.Path).ToArray();
    public IReadOnlyList<string> FoliageModelPaths => FoliageModelAssets.Select(asset => asset.Path).ToArray();

    public IEnumerable<SampleAssetReference> EnumerateAssets()
    {
        yield return ModelAsset;
        foreach (SampleAssetReference asset in AddendumModelAssets)
            yield return asset;
        foreach (SampleAssetReference asset in FoliageModelAssets)
            yield return asset;
    }

    public IEnumerable<SampleAssetReference> EnumerateAssets(
        SampleAssetLoadTier tier) =>
        EnumerateAssets().Where(asset => asset.LoadTier == tier);

    public bool HasDeferredAssets =>
        EnumerateAssets().Any(static asset =>
            asset.LoadTier == SampleAssetLoadTier.Deferred);

    public static SampleAssetManifest NewSponza { get; } = new(
        new SampleAssetReference("NewSponza_Main_glTF_003.gltf", ModelImportBackend.SharpGltf),
        new[] { new SampleAssetReference("NewSponza_Curtains_glTF.gltf", ModelImportBackend.SharpGltf) },
        Array.Empty<SampleAssetReference>(),
        1.0f,
        CoreVector3.Zero,
        0.0f,
        new Color(0.025f, 0.03f, 0.04f, 1f),
        EnableImportedModelLights: false);

    public static SampleAssetManifest Bistro { get; } = new(
        new SampleAssetReference(
            "Assets/Bistro_v5_2/BistroExterior.fbx",
            ModelImportBackend.Assimp,
            AssimpMaterialTextureConvention.AmazonBistro,
            RequireCooked: true),
        // The exterior and interior FBXs share the same authored coordinate
        // system, so the loader's common model world places the interior in
        // the cafe without an additional offset, rotation, or scale.
        new[]
        {
            new SampleAssetReference(
                "Assets/Bistro_v5_2/BistroInterior.fbx",
                ModelImportBackend.Assimp,
                AssimpMaterialTextureConvention.AmazonBistro,
                SampleAssetLoadTier.Deferred,
                RequireCooked: true)
        },
        Array.Empty<SampleAssetReference>(),
        1.0f,
        CoreVector3.Zero,
        0.0f,
        new Color(0.02f, 0.02f, 0.025f, 1f),
        // The FBX contains a second, unshadowed directional light whose color
        // already has a very large source intensity baked into it. Combining it
        // with the sample's shadow-casting sun erases every cast shadow.
        EnableImportedModelLights: false);

    public CoreMatrix4x4 CreateModelWorld(float rotation)
    {
        CoreMatrix4x4 world =
            CoreMatrix4x4.CreateScale(new CoreVector3(ModelScale)) *
            CoreMatrix4x4.CreateRotationY(rotation);

        return ModelPosition == CoreVector3.Zero
            ? world
            : world * CoreMatrix4x4.CreateTranslation(ModelPosition);
    }
}
