using Njulf.Core.Math;
using Njulf.Assets;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal sealed record SampleAssetReference(
    string Path,
    ModelImportBackend ExpectedBackend);

internal sealed record SampleAssetManifest(
    SampleAssetReference ModelAsset,
    IReadOnlyList<SampleAssetReference> AddendumModelAssets,
    IReadOnlyList<SampleAssetReference> FoliageModelAssets,
    float ModelScale,
    CoreVector3 ModelPosition,
    float RotationSpeed,
    Color AmbientLight)
{
    public string ModelPath => ModelAsset.Path;
    public IReadOnlyList<string> AddendumModelPaths => AddendumModelAssets.Select(asset => asset.Path).ToArray();
    public IReadOnlyList<string> FoliageModelPaths => FoliageModelAssets.Select(asset => asset.Path).ToArray();

    public static SampleAssetManifest NewSponza { get; } = new(
        new SampleAssetReference("NewSponza_Main_glTF_003.gltf", ModelImportBackend.SharpGltf),
        new[] { new SampleAssetReference("NewSponza_Curtains_glTF.gltf", ModelImportBackend.SharpGltf) },
        new[]
        {
            new SampleAssetReference(
                "Assets/ribbon_grass_tbdpec3r_ue_low/standard/tbdpec3r_tier_3_nonUE.gltf",
                ModelImportBackend.SharpGltf)
        },
        1.0f,
        CoreVector3.Zero,
        0.0f,
        new Color(0.025f, 0.03f, 0.04f, 1f));

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
