using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using System.Reflection;

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
                Assert.That(gpuMaterial.NormalScaleBias.Y, Is.EqualTo(0f));
                Assert.That(gpuMaterial.NormalScaleBias.Z, Is.EqualTo(0.5f));
                Assert.That(gpuMaterial.NormalScaleBias.W, Is.EqualTo(0f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.X, Is.EqualTo(1f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.Y, Is.EqualTo(0.04f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.Z, Is.EqualTo(0f));
                Assert.That(gpuMaterial.MetallicRoughnessAO.W, Is.EqualTo(0f));
                Assert.That(gpuMaterial.AlbedoTextureIndex, Is.EqualTo(10));
                Assert.That(gpuMaterial.NormalTextureIndex, Is.EqualTo(11));
                Assert.That(gpuMaterial.MetallicRoughnessTextureIndex, Is.EqualTo(12));
                Assert.That(gpuMaterial.EmissiveTextureIndex, Is.EqualTo(13));
            });
        }

        [Test]
        public void BuildGpuMaterialData_EncodesAlphaModeCutoffAndDoubleSided()
        {
            var textures = new MaterialTextureIndices(10, 11, 12, 13);

            GPUMaterialData opaque = ModelRenderUploadService.BuildGpuMaterialData(
                new ModelMaterial { AlphaMode = ModelAlphaMode.Opaque, AlphaCutoff = 0.25f },
                textures);
            GPUMaterialData mask = ModelRenderUploadService.BuildGpuMaterialData(
                new ModelMaterial { AlphaMode = ModelAlphaMode.Mask, AlphaCutoff = 0.35f, DoubleSided = true },
                textures);
            GPUMaterialData blend = ModelRenderUploadService.BuildGpuMaterialData(
                new ModelMaterial { AlphaMode = ModelAlphaMode.Blend, AlphaCutoff = 0.45f },
                textures);

            Assert.Multiple(() =>
            {
                Assert.That(opaque.NormalScaleBias.Y, Is.EqualTo(0f));
                Assert.That(opaque.NormalScaleBias.Z, Is.EqualTo(0.25f));
                Assert.That(opaque.NormalScaleBias.W, Is.EqualTo(0f));
                Assert.That(mask.NormalScaleBias.Y, Is.EqualTo(1f));
                Assert.That(mask.NormalScaleBias.Z, Is.EqualTo(0.35f));
                Assert.That(mask.NormalScaleBias.W, Is.EqualTo(1f));
                Assert.That(blend.NormalScaleBias.Y, Is.EqualTo(2f));
                Assert.That(blend.NormalScaleBias.Z, Is.EqualTo(0.45f));
                Assert.That(blend.NormalScaleBias.W, Is.EqualTo(0f));
                Assert.That(MaterialRenderModeExtensions.FromGpuMaterial(blend), Is.EqualTo(MaterialRenderMode.Blend));
            });
        }

        [Test]
        public void BuildGpuMaterialData_EnablesOcclusionSamplingOnlyForSharedOrmTexture()
        {
            string sharedTexture = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shared-orm.png");
            var material = new ModelMaterial
            {
                MetallicRoughnessTexturePath = sharedTexture,
                OcclusionTexturePath = sharedTexture
            };
            var textures = new MaterialTextureIndices(10, 11, 12, 13);

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(material, textures);

            Assert.That(gpuMaterial.MetallicRoughnessAO.W, Is.EqualTo(1f));
        }

        [Test]
        public void BuildGpuMaterialData_DisablesOcclusionSamplingForMetallicRoughnessOnlyTexture()
        {
            var material = new ModelMaterial
            {
                MetallicRoughnessTexturePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "roughness-metallic.png")
            };
            var textures = new MaterialTextureIndices(10, 11, 12, 13);

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(material, textures);

            Assert.That(gpuMaterial.MetallicRoughnessAO.W, Is.EqualTo(0f));
        }

        [Test]
        public void TextureCacheKey_IncludesColorSpaceAndMipPolicy()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "texture.png");

            string srgbKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: true);
            string linearKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: false);
            string noMipsKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: false, srgb: true);
            string cappedKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: true, maxDimension: 1024);

            Assert.Multiple(() =>
            {
                Assert.That(srgbKey, Is.Not.EqualTo(linearKey));
                Assert.That(srgbKey, Is.Not.EqualTo(noMipsKey));
                Assert.That(srgbKey, Is.Not.EqualTo(cappedKey));
                Assert.That(srgbKey, Does.Contain(Path.GetFullPath(path)));
            });
        }

        [Test]
        public void TextureCacheKey_IncludesSamplerIdentity()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "texture.png");
            var repeat = TextureSamplerDescription.Default;
            var clampNearest = new TextureSamplerDescription(
                TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge,
                TextureFilterMode.Nearest,
                TextureFilterMode.Nearest,
                TextureMipFilterMode.Nearest,
                1f);

            string repeatKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: true, samplerDescription: repeat);
            string clampKey = TextureManager.CreateTextureCacheKey(path, generateMipmaps: true, srgb: true, samplerDescription: clampNearest);

            Assert.That(repeatKey, Is.Not.EqualTo(clampKey));
        }

        [Test]
        public void BuildGpuMaterialData_PacksPerSlotTextureTransformsAndUvSets()
        {
            var material = new ModelMaterial
            {
                BaseColorTexture = new ModelTextureSlot
                {
                    Offset = new Vector2(0.1f, 0.2f),
                    Scale = new Vector2(2f, 3f),
                    RotationRadians = 0.25f,
                    TexCoordSet = 1
                },
                NormalTexture = new ModelTextureSlot
                {
                    Offset = new Vector2(0.3f, 0.4f),
                    Scale = new Vector2(4f, 5f),
                    RotationRadians = 0.5f,
                    TexCoordSet = 0
                },
                MetallicRoughnessTexture = new ModelTextureSlot
                {
                    Offset = new Vector2(0.5f, 0.6f),
                    Scale = new Vector2(6f, 7f),
                    RotationRadians = 0.75f,
                    TexCoordSet = 1
                },
                EmissiveTexture = new ModelTextureSlot
                {
                    Offset = new Vector2(0.7f, 0.8f),
                    Scale = new Vector2(8f, 9f),
                    RotationRadians = 1.0f,
                    TexCoordSet = 1
                }
            };

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(
                material,
                new MaterialTextureIndices(10, 11, 12, 13));

            Assert.Multiple(() =>
            {
                Assert.That(gpuMaterial.BaseColorOffsetScale, Is.EqualTo(new Vector4(0.1f, 0.2f, 2f, 3f)));
                Assert.That(gpuMaterial.NormalOffsetScale, Is.EqualTo(new Vector4(0.3f, 0.4f, 4f, 5f)));
                Assert.That(gpuMaterial.MetallicRoughnessOffsetScale, Is.EqualTo(new Vector4(0.5f, 0.6f, 6f, 7f)));
                Assert.That(gpuMaterial.EmissiveOffsetScale, Is.EqualTo(new Vector4(0.7f, 0.8f, 8f, 9f)));
                Assert.That(gpuMaterial.TextureRotations, Is.EqualTo(new Vector4(0.25f, 0.5f, 0.75f, 1.0f)));
                Assert.That(gpuMaterial.TextureTexCoordSets, Is.EqualTo(new Vector4(1f, 0f, 1f, 1f)));
            });
        }

        [Test]
        public void ShouldGenerateAlbedoMipmaps_DisablesMipmapsForBlendMaterials()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ModelRenderUploadService.ShouldGenerateAlbedoMipmaps(new ModelMaterial { AlphaMode = ModelAlphaMode.Opaque }), Is.True);
                Assert.That(ModelRenderUploadService.ShouldGenerateAlbedoMipmaps(new ModelMaterial { AlphaMode = ModelAlphaMode.Mask }), Is.True);
                Assert.That(ModelRenderUploadService.ShouldGenerateAlbedoMipmaps(new ModelMaterial { AlphaMode = ModelAlphaMode.Blend }), Is.False);
            });
        }

        [Test]
        public void TryDownscaleRgba_ClampsLargestDimensionAndPreservesAspect()
        {
            byte[] source = new byte[4 * 2 * 4];
            for (int i = 0; i < source.Length; i++)
                source[i] = (byte)i;

            bool downscaled = TextureManager.TryDownscaleRgba(
                source,
                sourceWidth: 4,
                sourceHeight: 2,
                maxDimension: 2,
                out byte[]? result,
                out uint width,
                out uint height);

            Assert.Multiple(() =>
            {
                Assert.That(downscaled, Is.True);
                Assert.That(width, Is.EqualTo(2));
                Assert.That(height, Is.EqualTo(1));
                Assert.That(result, Is.Not.Null);
                Assert.That(result!.Length, Is.EqualTo(2 * 1 * 4));
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

        [Test]
        public void BuildGpuMaterialExtensionData_ClampsAndMapsFactors()
        {
            var material = new ModelMaterial
            {
                FeatureFlags = (uint)(MaterialFeatureFlags.Clearcoat |
                    MaterialFeatureFlags.Transmission |
                    MaterialFeatureFlags.EmissiveStrength |
                    MaterialFeatureFlags.Specular |
                    MaterialFeatureFlags.Iridescence |
                    MaterialFeatureFlags.Dispersion),
                ClearcoatFactor = 2f,
                ClearcoatRoughness = -1f,
                ClearcoatNormalScale = 8f,
                EmissiveStrength = 256f,
                TransmissionFactor = 1.5f,
                Ior = 5f,
                ThicknessFactor = -2f,
                AttenuationDistance = 4f,
                AttenuationColor = new Vector4(0.5f, -1f, 2f, 1f),
                SpecularFactor = 2f,
                SpecularColor = new Vector4(0.2f, 0.4f, 1.5f, 1f),
                IridescenceFactor = 2f,
                IridescenceIor = 6f,
                IridescenceThicknessMinimum = -10f,
                IridescenceThicknessMaximum = 550f,
                Dispersion = 0.65f,
                SpecularTexture = new ModelTextureSlot
                {
                    Offset = new Vector2(0.1f, 0.2f),
                    Scale = new Vector2(0.3f, 0.4f),
                    RotationRadians = 0.5f,
                    TexCoordSet = 1
                }
            };
            var textures = new MaterialExtensionTextureIndices(20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32);

            GPUMaterialExtensionData data = ModelRenderUploadService.BuildGpuMaterialExtensionData(material, textures);

            Assert.Multiple(() =>
            {
                Assert.That(data.Clearcoat, Is.EqualTo(new Vector4(1f, 0f, 4f, 128f)));
                Assert.That(data.Transmission, Is.EqualTo(new Vector4(1f, 3f, 0f, 4f)));
                Assert.That(data.AttenuationColor, Is.EqualTo(new Vector4(0.5f, 0f, 2f, 0f)));
                Assert.That(data.SpecularColor, Is.EqualTo(new Vector4(0.2f, 0.4f, 1.5f, 1f)));
                Assert.That(data.Iridescence, Is.EqualTo(new Vector4(1f, 3f, 0f, 550f)));
                Assert.That(data.Dispersion, Is.EqualTo(new Vector4(0.65f, 0f, 0f, 0f)));
                Assert.That(data.SpecularOffsetScale, Is.EqualTo(new Vector4(0.1f, 0.2f, 0.3f, 0.4f)));
                Assert.That(data.ExtensionTextureRotations2.X, Is.EqualTo(0.5f));
                Assert.That(data.ExtensionTextureTexCoordSets2.X, Is.EqualTo(1f));
                Assert.That(data.ClearcoatTextureIndex, Is.EqualTo(20));
                Assert.That(data.SubsurfaceTextureIndex, Is.EqualTo(28));
                Assert.That(data.SpecularTextureIndex, Is.EqualTo(29));
                Assert.That(data.IridescenceThicknessTextureIndex, Is.EqualTo(32));
            });
        }

        [Test]
        public void BuildMaterialRenderMetadata_ClassifiesTransmissionAsTransparent()
        {
            MaterialRenderMetadata metadata = ModelRenderUploadService.BuildMaterialRenderMetadata(
                new ModelMaterial
                {
                    FeatureFlags = (uint)MaterialFeatureFlags.Transmission,
                    AlphaMode = ModelAlphaMode.Opaque
                });

            Assert.That(metadata.BlendMode, Is.EqualTo(MaterialBlendMode.AlphaBlend));
        }

        [Test]
        public void DefaultMaterial_HasNeutralEmissiveStrength()
        {
            Assert.That(ModelMaterial.Default.EmissiveStrength, Is.EqualTo(1f));
        }

        [Test]
        public void MeshUploadStagingSizer_AccountsForAlignmentAcrossBatchedMeshes()
        {
            ulong offset = 0;
            offset = InvokeAddUploadStagingBytes(offset, 12);
            offset = InvokeAddUploadStagingBytes(offset, 7);

            Assert.That(offset, Is.EqualTo(263UL));
        }

        private static ulong InvokeAddUploadStagingBytes(ulong currentOffset, ulong size)
        {
            MethodInfo method = typeof(MeshManager).GetMethod(
                "AddUploadStagingBytes",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ulong), typeof(ulong) },
                modifiers: null)
                ?? throw new MissingMethodException(nameof(MeshManager), "AddUploadStagingBytes");

            return (ulong)method.Invoke(null, new object[] { currentOffset, size })!;
        }
    }
}
