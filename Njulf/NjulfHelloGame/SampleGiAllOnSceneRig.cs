using Njulf.Assets;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal sealed record SampleGiAllOnSceneRigSummary(
    string C1AssetPath,
    int C1RenderObjectCount,
    int C4HeroRenderObjectCount,
    CoreVector3 Anchor,
    float FixtureScale);

/// <summary>
/// Reusable, qualification-only scene content. The imported grass model owns
/// real MASK textures and a cooked C1 payload; the compact dielectric hero
/// guarantees non-empty tagged-caustic work in scenes whose authored
/// materials do not explicitly opt into C4.
/// </summary>
internal static class SampleGiAllOnSceneRig
{
    internal const string C1AssetPath =
        "Assets/ribbon_grass_tbdpec3r_ue_low/standard/" +
        "tbdpec3r_tier_3_nonUE.gltf";
    internal const int ExpectedMaskedMaterialCount = 2;

    private static readonly SampleAssetReference C1Asset = new(
        C1AssetPath,
        ModelImportBackend.SharpGltf,
        MaximumSamplerAnisotropy: 1f);

    public static SampleGiAllOnSceneRigSummary Configure(
        Scene scene,
        SampleSceneKind sceneKind,
        ContentManager content,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(meshManager);
        ArgumentNullException.ThrowIfNull(materialManager);
        if (!SampleGiAllOnQualificationContract.IsSupportedScene(sceneKind))
            throw new ArgumentOutOfRangeException(nameof(sceneKind));

        (CoreVector3 anchor, float targetHeight, float heroScale) =
            ResolvePlacement(sceneKind);
        Model asset = content.Load<Model>(
                C1AssetPath,
                C1Asset.CreateLoadOptions()) ??
            throw new InvalidOperationException(
                $"All-on GI C1 fixture '{C1AssetPath}' did not load.");
        Model instance = asset.CreateInstance() ??
            throw new InvalidOperationException(
                $"All-on GI C1 fixture '{C1AssetPath}' did not create an instance.");
        if (instance.RenderObjects.Count == 0)
        {
            throw new InvalidDataException(
                $"All-on GI C1 fixture '{C1AssetPath}' has no render objects.");
        }

        CoreMatrix4x4 world = CreateGroundedWorld(
            instance,
            anchor,
            targetHeight,
            out float fixtureScale);
        for (int index = 0; index < instance.RenderObjects.Count; index++)
        {
            RenderObject renderObject = instance.RenderObjects[index];
            renderObject.Name =
                $"GiAllOn.C1.MaskedFixture.{index}.{renderObject.Name}";
            renderObject.AssetReference = new SceneAssetReference
            {
                Path = C1AssetPath,
                SubObject = index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            };
            renderObject.WorldMatrix = world;
            renderObject.Visible = true;
            renderObject.IsStatic = true;
            scene.Add(renderObject);
        }

        int c4HeroCount = sceneKind == SampleSceneKind.MaterialShowcase
            ? 0
            : SampleMaterialShowcaseScene.ConfigurePortableGiAllOnHero(
                scene,
                meshManager,
                materialManager,
                anchor + ResolveHeroOffset(sceneKind),
                heroScale);
        return new SampleGiAllOnSceneRigSummary(
            C1AssetPath,
            instance.RenderObjects.Count,
            c4HeroCount,
            anchor,
            fixtureScale);
    }

    private static CoreMatrix4x4 CreateGroundedWorld(
        Model model,
        CoreVector3 anchor,
        float targetHeight,
        out float scale)
    {
        CoreVector3 size = model.BoundingBox.Size;
        if (!float.IsFinite(size.Y) || size.Y <= 0.0001f)
        {
            throw new InvalidDataException(
                "All-on GI C1 fixture has invalid source bounds.");
        }

        scale = targetHeight / size.Y;
        CoreVector3 center = model.BoundingBox.Center;
        CoreVector3 minimum = model.BoundingBox.Min;
        var translation = new CoreVector3(
            anchor.X - center.X * scale,
            anchor.Y - minimum.Y * scale,
            anchor.Z - center.Z * scale);
        return CoreMatrix4x4.CreateScale(new CoreVector3(scale)) *
               CoreMatrix4x4.CreateTranslation(translation);
    }

    private static (CoreVector3 Anchor, float TargetHeight, float HeroScale)
        ResolvePlacement(SampleSceneKind scene) => scene switch
        {
            SampleSceneKind.MaterialShowcase =>
                (new CoreVector3(3.25f, 0f, 4.05f), 1.35f, 1f),
            SampleSceneKind.SponzaPlaza =>
                (new CoreVector3(10.0f, 0f, 5.5f), 1.75f, 0.9f),
            SampleSceneKind.Bistro =>
                (new CoreVector3(-20.0f, 0f, 1.25f), 1.65f, 0.85f),
            _ => throw new ArgumentOutOfRangeException(nameof(scene))
        };

    private static CoreVector3 ResolveHeroOffset(SampleSceneKind scene) =>
        scene switch
        {
            SampleSceneKind.SponzaPlaza => new CoreVector3(0f, 0f, -2.2f),
            SampleSceneKind.Bistro => new CoreVector3(0f, 0f, 2.2f),
            _ => CoreVector3.Zero
        };
}
