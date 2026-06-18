using Njulf.Core.Math;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal sealed record SampleAssetManifest(
    string ModelPath,
    IReadOnlyList<string> AddendumModelPaths,
    IReadOnlyList<string> FoliageModelPaths,
    float ModelScale,
    CoreVector3 ModelPosition,
    float RotationSpeed,
    Color AmbientLight)
{
    public static SampleAssetManifest NewSponza { get; } = new(
        "NewSponza_Main_glTF_003.gltf",
        new[] { "NewSponza_Curtains_glTF.gltf" },
        Array.Empty<string>(),
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
