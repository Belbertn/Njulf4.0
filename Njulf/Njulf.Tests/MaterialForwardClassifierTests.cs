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
            Assert.That(MaterialForwardClassifier.IsSimpleNormalOpaque(materialClass), Is.True);
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
        public void Classify_TextureTransform_UsesSimpleFullInput()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.BaseColorOffsetScale = new Vector4(0.1f, 0f, 1f, 1f);

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(
                materialClass,
                Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
        }

        [Test]
        public void Classify_SecondaryUv_UsesSimpleFullInput()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.TextureTexCoordSets = new Vector4(1f, 0f, 0f, 0f);

            MaterialForwardClass materialClass =
                MaterialForwardClassifier.Classify(
                    material,
                    MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(
                materialClass,
                Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
        }

        [Test]
        public void Classify_ExtensionFreeMask_UsesSimpleFullInput()
        {
            GPUMaterialData masked = CreateDefaultMaterial();
            masked.NormalScaleBias = new Vector4(1f, 1f, 0.5f, 0f);

            MaterialForwardClass materialClass =
                MaterialForwardClassifier.Classify(
                    masked,
                    MaterialRenderMetadata.FromGpuMaterial(masked));

            Assert.That(
                materialClass,
                Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
        }

        [Test]
        public void Classify_ExtensionBearingMask_UsesFullOpaque()
        {
            GPUMaterialData masked = CreateDefaultMaterial();
            masked.NormalScaleBias = new Vector4(1f, 1f, 0.5f, 0f);
            masked.FeatureFlags = (uint)MaterialFeatureFlags.Clearcoat;
            masked.ExtensionDataIndex = 0;

            MaterialForwardClass materialClass =
                MaterialForwardClassifier.Classify(
                    masked,
                    MaterialRenderMetadata.FromGpuMaterial(masked));

            Assert.That(
                materialClass,
                Is.EqualTo(MaterialForwardClass.FullOpaque));
        }

        [Test]
        public void Classify_TransparentMaterial_KeepsDedicatedPath()
        {
            GPUMaterialData transparent = CreateDefaultMaterial();
            transparent.NormalScaleBias = new Vector4(1f, 2f, 0.5f, 0f);

            Assert.That(
                MaterialForwardClassifier.Classify(
                    transparent,
                    MaterialRenderMetadata.FromGpuMaterial(transparent)),
                Is.EqualTo(MaterialForwardClass.Transparent));
        }

        [Test]
        public void Classify_VisibleThinGlass_UsesDedicatedTransparentClass()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.FeatureFlags = (uint)MaterialFeatureFlags.Transmission;
            material.TransportFlags = (uint)(
                GiMaterialTransportFlags.ThinSurfaceTransmission |
                GiMaterialTransportFlags.ThinGlass);
            material.NormalScaleBias = new Vector4(1f, 2f, 0.5f, 1f);
            var metadata = new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                ShadingModel = MaterialShadingModel.ThinGlass,
                TransmissionPolicy = GiTransmissionPolicy.ThinSurface,
                SurfaceFlags = MaterialSurfaceFlags.DoubleSided |
                    MaterialSurfaceFlags.ReceivesShadows
            };

            MaterialForwardClass materialClass =
                MaterialForwardClassifier.Classify(material, metadata);

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.ThinGlass));
        }

        [Test]
        public void Classify_Bc5NormalMap_StaysOnSimpleOpaqueNormalPath()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.NormalTextureIndex = BindlessIndex.FirstDynamicTextureIndex;
            material.NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f);
            material.FeatureFlags = (uint)MaterialFeatureFlags.CompressedNormalBc5;

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
        }

        [Test]
        public void Classify_DirectXNormalMap_StaysOnSimpleOpaqueNormalPath()
        {
            GPUMaterialData material = CreateDefaultMaterial();
            material.NormalTextureIndex = BindlessIndex.FirstDynamicTextureIndex;
            material.NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f);
            material.FeatureFlags = (uint)(
                MaterialFeatureFlags.CompressedNormalBc5 |
                MaterialFeatureFlags.NormalMapGreenInverted);

            MaterialForwardClass materialClass = MaterialForwardClassifier.Classify(
                material,
                MaterialRenderMetadata.FromGpuMaterial(material));

            Assert.That(materialClass, Is.EqualTo(MaterialForwardClass.SimpleOpaqueNormal));
        }

        [Test]
        public void SceneReflectionReceiverClassification_ExcludesNonPhysicalBlendsAndUnlitDecals()
        {
            var receiver = new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                ShadingModel = MaterialShadingModel.ThinGlass
            };

            Assert.Multiple(() =>
            {
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver),
                    Is.True);
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver with
                {
                    BlendMode = MaterialBlendMode.PremultipliedAlpha,
                    ShadingModel = MaterialShadingModel.Pbr
                }), Is.True);
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver with
                {
                    BlendMode = MaterialBlendMode.Additive
                }), Is.False);
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver with
                {
                    BlendMode = MaterialBlendMode.Multiply
                }), Is.False);
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver with
                {
                    ShadingModel = MaterialShadingModel.Unlit
                }), Is.False);
                Assert.That(SceneDataBuilder.ReceivesSceneReflections(receiver with
                {
                    ShadingModel = MaterialShadingModel.Decal,
                    SurfaceFlags = MaterialSurfaceFlags.GeometryDecal
                }), Is.False);
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
