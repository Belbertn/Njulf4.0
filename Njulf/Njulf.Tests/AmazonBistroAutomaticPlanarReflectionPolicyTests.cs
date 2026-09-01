using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmazonBistroAutomaticPlanarReflectionPolicyTests
{
    [Test]
    public void Apply_OptsInOnlyReviewedExteriorWetPavementMaterial()
    {
        ModelMaterial reviewed = CreateMaterial(
            "Pavement_Ground_Wet_BaseColor.dds");
        ModelMaterial wrongMaterial = CreateMaterial(
            "Pavement_Ground_Dry_BaseColor.dds");
        ModelMaterial wrongAsset = CreateMaterial(
            "Pavement_Ground_Wet_BaseColor.dds");
        ModelMaterial sponza = CreateMaterial(
            "Pavement_Ground_Wet_BaseColor.dds");

        bool reviewedApplied =
            AmazonBistroAutomaticPlanarReflectionPolicy.Apply(
                @"C:\content\BistroExterior.fbx",
                reviewed);
        bool wrongMaterialApplied =
            AmazonBistroAutomaticPlanarReflectionPolicy.Apply(
                @"C:\content\BistroExterior.fbx",
                wrongMaterial);
        bool wrongAssetApplied =
            AmazonBistroAutomaticPlanarReflectionPolicy.Apply(
                @"C:\content\BistroInterior.fbx",
                wrongAsset);
        bool sponzaApplied =
            AmazonBistroAutomaticPlanarReflectionPolicy.Apply(
                @"C:\content\NewSponza_Main_glTF.gltf",
                sponza);

        Assert.Multiple(() =>
        {
            Assert.That(reviewedApplied, Is.True);
            Assert.That(reviewed.AutomaticPlanarReflectionEnabled, Is.True);
            Assert.That(reviewed.Name, Is.EqualTo("Pavement_Ground_Wet"));
            Assert.That(wrongMaterialApplied, Is.False);
            Assert.That(wrongMaterial.AutomaticPlanarReflectionEnabled, Is.False);
            Assert.That(wrongAssetApplied, Is.False);
            Assert.That(wrongAsset.AutomaticPlanarReflectionEnabled, Is.False);
            Assert.That(sponzaApplied, Is.False);
            Assert.That(sponza.AutomaticPlanarReflectionEnabled, Is.False);
        });
    }

    [TestCase("Pavement_Ground_Wet_BaseColor.dds", true)]
    [TestCase("pavement_ground_wet_basecolor.DDS", true)]
    [TestCase("Pavement_Ground_Wet_Normal.dds", false)]
    [TestCase("Pavement_Ground_Wet_BaseColor_Backup.dds", false)]
    public void Apply_RequiresExactReviewedBaseColorIdentity(
        string textureName,
        bool expected)
    {
        ModelMaterial material = CreateMaterial(textureName);

        bool applied = AmazonBistroAutomaticPlanarReflectionPolicy.Apply(
            @"C:\content\BistroExterior.fbx",
            material);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.EqualTo(expected));
            Assert.That(
                material.AutomaticPlanarReflectionEnabled,
                Is.EqualTo(expected));
        });
    }

    private static ModelMaterial CreateMaterial(string textureName) => new()
    {
        Name = "Material_105",
        BaseColorTexture = new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                CacheIdentity = @"C:\content\Textures\" + textureName,
                FilePath = @"C:\content\Textures\" + textureName,
                DebugName = textureName
            },
            ColorSpace = TextureColorSpace.Srgb
        }
    };
}
