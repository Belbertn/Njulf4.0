using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmazonBistroAutomaticPlanarReflectionPolicyTests
{
    [Test]
    public void Apply_OptsInOnlyReviewedExteriorWetPavementMaterial()
    {
        var reviewed = new ModelMaterial { Name = "Pavement_Ground_Wet" };
        var wrongMaterial = new ModelMaterial { Name = "Pavement_Ground_Dry" };
        var wrongAsset = new ModelMaterial { Name = "Pavement_Ground_Wet" };
        var sponza = new ModelMaterial { Name = "Pavement_Ground_Wet" };

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
            Assert.That(wrongMaterialApplied, Is.False);
            Assert.That(wrongMaterial.AutomaticPlanarReflectionEnabled, Is.False);
            Assert.That(wrongAssetApplied, Is.False);
            Assert.That(wrongAsset.AutomaticPlanarReflectionEnabled, Is.False);
            Assert.That(sponzaApplied, Is.False);
            Assert.That(sponza.AutomaticPlanarReflectionEnabled, Is.False);
        });
    }
}
