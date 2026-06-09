using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ModelRenderUploadServiceTests
    {
        [Test]
        public void BuildGpuMaterialData_MapsImportedFactorsAndTextureIndices()
        {
            var material = new ModelMaterial
            {
                Albedo = new Vector4(0.25f, 0.5f, 0.75f, 0.8f),
                Emissive = new Vector4(0.1f, 0.2f, 0.3f, 1f),
                Metallic = 1.5f,
                Roughness = 0.01f,
                AmbientOcclusion = -1f,
                NormalScale = 0.65f
            };
            var textures = new MaterialTextureIndices(10, 11, 12, 13);

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(material, textures);

            Assert.Multiple(() =>
            {
                Assert.That(gpuMaterial.Albedo, Is.EqualTo(material.Albedo));
                Assert.That(gpuMaterial.Emissive, Is.EqualTo(material.Emissive));
                Assert.That(gpuMaterial.NormalScaleBias.X, Is.EqualTo(0.65f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.X, Is.EqualTo(1f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.Y, Is.EqualTo(0.04f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.Z, Is.EqualTo(0f));
                Assert.That(gpuMaterial.AlbedoTextureIndex, Is.EqualTo(10));
                Assert.That(gpuMaterial.NormalTextureIndex, Is.EqualTo(11));
                Assert.That(gpuMaterial.MetallicRoughnessTextureIndex, Is.EqualTo(12));
                Assert.That(gpuMaterial.EmissiveTextureIndex, Is.EqualTo(13));
            });
        }

        [Test]
        public void TextureCacheKey_IncludesColorSpaceAndMipPolicy()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "texture.png");

            string srgbKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: true);
            string linearKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: false);
            string noMipsKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: false, srgb: true);

            Assert.Multiple(() =>
            {
                Assert.That(srgbKey, Is.Not.EqualTo(linearKey));
                Assert.That(srgbKey, Is.Not.EqualTo(noMipsKey));
                Assert.That(srgbKey, Does.Contain(Path.GetFullPath(path)));
            });
        }

        [Test]
        public void MaterialTextureIndices_AcceptsCanonicalDefaultTextureIndices()
        {
            var textures = new MaterialTextureIndices(
                BindlessIndex.DefaultWhiteTexture,
                BindlessIndex.DefaultNormalTexture,
                BindlessIndex.DefaultBlackTexture,
                BindlessIndex.DefaultBlackTexture);

            var material = ModelRenderUploadService.BuildGpuMaterialData(ModelMaterial.Default, textures);

            Assert.Multiple(() =>
            {
                Assert.That(material.AlbedoTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
                Assert.That(material.NormalTextureIndex, Is.EqualTo(BindlessIndex.DefaultNormalTexture));
                Assert.That(material.MetallicRoughnessTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
                Assert.That(material.EmissiveTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
            });
        }
    }
}
