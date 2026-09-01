using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmazonBistroMaterialProfileTests
{
    private const uint FoliageFeature = 1u << 22;

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

    [TestCase("Foliage_Bux_Hedges46_BaseColor.dds")]
    [TestCase("Foliage_Flowers_BaseColor.dds")]
    [TestCase("Foliage_Ivy_leaf_a_BaseColor.dds")]
    [TestCase("Foliage_Leaves_BaseColor.dds")]
    [TestCase("Foliage_Linde_Tree_Large_Green_Leaves_BaseColor.dds")]
    [TestCase("Foliage_Linde_Tree_Large_Orange_Leaves_BaseColor.dds")]
    [TestCase("Plants_plants_BaseColor.dds")]
    public void Apply_KnownFoliageIdentityCreatesMaskedDoubleSidedFoliage(
        string textureName)
    {
        ModelMaterial material = CreateMaterial(textureName);

        bool applied = AmazonBistroMaterialProfile.Apply(
            @"C:\content\BistroExterior.fbx",
            material);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.True);
            Assert.That(material.AlphaMode, Is.EqualTo(ModelAlphaMode.Mask));
            Assert.That(material.AlphaCutoff, Is.EqualTo(0.5f));
            Assert.That(material.DoubleSided, Is.True);
            Assert.That(material.FeatureFlags & FoliageFeature, Is.Not.Zero);
            Assert.That(material.IsThinGlass, Is.False);
            Assert.That(material.TransmissionFactor, Is.Zero);
        });
    }

    [TestCase("Foliage_Ivy_branches_BaseColor.dds")]
    [TestCase("Foliage_Linde_Tree_Large_Trunk_BaseColor.dds")]
    [TestCase("Foliage_Trunk_BaseColor.dds")]
    [TestCase("Foliage_Paris_Flowers_BaseColor.dds")]
    [TestCase("Plants_Metal_Base_01_BaseColor.dds")]
    [TestCase("Foliage_Leaves_Normal.dds")]
    [TestCase("Generic_Foliage_BaseColor.dds")]
    public void Apply_FoliageLikeButUnlistedIdentityRemainsOpaque(
        string textureName)
    {
        ModelMaterial material = CreateMaterial(textureName);

        bool applied = AmazonBistroMaterialProfile.Apply(
            @"C:\content\BistroExterior.fbx",
            material);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.False);
            Assert.That(material.AlphaMode, Is.EqualTo(ModelAlphaMode.Opaque));
            Assert.That(material.DoubleSided, Is.False);
            Assert.That(material.FeatureFlags & FoliageFeature, Is.Zero);
            Assert.That(material.IsThinGlass, Is.False);
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
