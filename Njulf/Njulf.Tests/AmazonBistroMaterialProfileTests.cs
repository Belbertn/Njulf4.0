using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmazonBistroMaterialProfileTests
{
    [TestCase("MASTER_Glass_Exterior_BaseColor.dds", 0.94f, 0.08f)]
    [TestCase("TransparentGlass_BaseColor.dds", 0.96f, 0.05f)]
    [TestCase("MASTER_Glass_Dirty_BaseColor.dds", 0.78f, 0.28f)]
    [TestCase("MASTER_Glass_Dirty_MASKED_BaseColor.dds", 0.70f, 0.34f)]
    [TestCase("MASTER_Frosted_Glass_BaseColor.dds", 0.58f, 0.72f)]
    [TestCase("MASTER_Interior_01_Frozen_Glass_BaseColor.dds", 0.58f, 0.72f)]
    public void Apply_KnownArchitecturalGlassIdentityCreatesThinDielectric(
        string textureName,
        float transmission,
        float roughness)
    {
        ModelMaterial material = CreateMaterial(textureName);

        bool applied = AmazonBistroMaterialProfile.Apply(
            @"C:\content\BistroInterior.fbx",
            material);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.True);
            Assert.That(material.IsThinGlass, Is.True);
            Assert.That(material.AlphaMode, Is.EqualTo(ModelAlphaMode.Blend));
            Assert.That(material.DoubleSided, Is.True);
            Assert.That(material.GiTransmissionPolicy,
                Is.EqualTo(ModelGiTransmissionPolicy.ThinSurface));
            Assert.That(material.TransmissionFactor,
                Is.EqualTo(transmission).Within(1e-6f));
            Assert.That(material.Roughness, Is.EqualTo(roughness).Within(1e-6f));
            Assert.That(material.Metallic, Is.Zero);
            Assert.That(material.ThicknessFactor, Is.Zero);
            Assert.That(material.Ior, Is.InRange(1.5f, 1.52f));
            Assert.That(material.FeatureFlags & (1u << 9), Is.Not.Zero,
                "visible transmission feature");
            Assert.That(material.FeatureFlags & (1u << 24), Is.Not.Zero,
                "physical IOR feature");
            Assert.That(material.ThinTransmissionTint, Is.Not.EqualTo(Vector4.Zero));
        });
    }

    [Test]
    public void Apply_RequiresBothBistroAssetAndExactStableTextureIdentity()
    {
        ModelMaterial wrongAsset = CreateMaterial(
            "MASTER_Glass_Exterior_BaseColor.dds");
        ModelMaterial wrongTexture = CreateMaterial("GenericWindow_BaseColor.dds");

        bool wrongAssetApplied = AmazonBistroMaterialProfile.Apply(
            @"C:\content\OtherScene.fbx",
            wrongAsset);
        bool wrongTextureApplied = AmazonBistroMaterialProfile.Apply(
            @"C:\content\BistroExterior.fbx",
            wrongTexture);

        Assert.Multiple(() =>
        {
            Assert.That(wrongAssetApplied, Is.False);
            Assert.That(wrongTextureApplied, Is.False);
            Assert.That(wrongAsset.IsThinGlass, Is.False);
            Assert.That(wrongTexture.IsThinGlass, Is.False);
            Assert.That(wrongTexture.AlphaMode, Is.EqualTo(ModelAlphaMode.Opaque));
        });
    }

    private static ModelMaterial CreateMaterial(string textureName) => new()
    {
        Name = "Material_42",
        Metallic = 1.0f,
        Roughness = 1.0f,
        BaseColorTexture = new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                DebugName = textureName,
                CacheIdentity = @"C:\content\Textures\" + textureName,
                FilePath = @"C:\content\Textures\" + textureName
            },
            ColorSpace = TextureColorSpace.Srgb
        }
    };
}
