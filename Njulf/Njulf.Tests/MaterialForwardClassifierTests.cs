using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class MaterialForwardClassifierTests
    {
        [Test]
        public void Classify_DefaultOpaqueMaterial_UsesSimpleOpaque()
        {
            GPUMaterialData material = CreateDefaultMaterial();

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.SimpleOpaque));
        }

        [Test]
        public void Classify_OpaqueNormalMap_UsesSimpleOpaqueNormal()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.NormalTextureIndex = BindlessIndex.FirstDynamicTextureIndex;
            material.NormalScaleBias = new Vector4(0.8f, 0f, 0.5f, 0f);

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
            Assert.That(MaterialForwardClassifier.IsSimpleOpaque(materialClass), Is.False);
        }

        [Test]
        public void Classify_ExtensionMaterial_UsesFullOpaque()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.FeatureFlags = (uint)MaterialFeatureFlags.Clearcoat;
            material.ExtensionDataIndex = 0;

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.FullOpaque));
        }

        [Test]
        public void Classify_TextureTransform_UsesFullOpaque()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.BaseColorOffsetScale = new Vector4(0.1f, 0f, 1f, 1f);

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.FullOpaque));
        }

        [Test]
        public void Classify_MaskedAndTransparentMaterials_KeepDedicatedPaths()
        {
            GPUMaterialData masked = CreateDefaultMaterial();
            masked.NormalScaleBias = new Vector4(1f, 1f, 0.5f, 0f);

            GPUMaterialData transparent = CreateDefaultMaterial();
            transparent.NormalScaleBias = new Vector4(1f, 2f, 0.5f, 0f);

            Assert.Multiple(() =>
            {
                Assert.That(
                    MaterialForwardClassifier.Classify(masked, MaterialRenderMetadata.FromGpuMaterial(masked)),
                    Is.EqualTo(MaterialForwardClass.Masked));
                Assert.That(
                    MaterialForwardClassifier.Classify(transparent, MaterialRenderMetadata.FromGpuMaterial(transparent)),
                    Is.EqualTo(MaterialForwardClass.Transparent));
            });
        }

        private static GPUMaterialData CreateDefaultMaterial()
        {
            return new GPUMaterialData
            {
                Albedo = Vector4.One,
                Emissive = Vector4.Zero,
                NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f),
                MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
                BaseColorOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                NormalOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                MetallicRoughnessOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                EmissiveOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                TextureRotations = Vector4.Zero,
                TextureTexCoordSets = Vector4.Zero,
                AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
                NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
                MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
                EmissiveTextureIndex = BindlessIndex.DefaultBlackTexture,
                FeatureFlags = 0u,
                ExtensionDataIndex = -1
            };
        }
    }
}
