using Njulf.Assets;
using Njulf.Assets.Cooked;
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
                Assert.That(gpuMaterial.DdgiAverageAlbedo, Is.EqualTo(new Vector4(0.25f, 0.5f, 0.75f, 0.8f)));
                Assert.That(gpuMaterial.DdgiAverageEmissive.X, Is.EqualTo(0.1f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.Y, Is.EqualTo(0.2f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.Z, Is.EqualTo(0.3f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.W, Is.EqualTo(0.2126f * 0.1f + 0.7152f * 0.2f + 0.0722f * 0.3f).Within(0.0001f));
                Assert.That(gpuMaterial.DdgiMaterialPolicy.X, Is.EqualTo(0f));
                Assert.That(gpuMaterial.DdgiMaterialPolicy.Y, Is.EqualTo(0f));
                Assert.That((uint)gpuMaterial.DdgiMaterialPolicy.W, Is.EqualTo(4u));
            });
        }

        [Test]
        public void BuildGpuMaterialData_MultipliesLinearTextureAverageByBaseColorFactorExactlyOnce()
        {
            var material = new ModelMaterial
            {
                Albedo = new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
                AlbedoTexturePath = "base.png",
                DdgiBaseColorTextureAverageLinear = new Vector4(0.5f, 0.25f, 0.1f, 0.75f)
            };

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(
                material,
                new MaterialTextureIndices(10, 11, 12, 13),
                runtimeBaseColorTextureAverageLinear: new Vector4(0.9f, 0.9f, 0.9f, 0.9f));

            Assert.Multiple(() =>
            {
                Assert.That(gpuMaterial.DdgiAverageAlbedo.X, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(gpuMaterial.DdgiAverageAlbedo.Y, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(gpuMaterial.DdgiAverageAlbedo.Z, Is.EqualTo(0.06f).Within(0.0001f));
                Assert.That(gpuMaterial.DdgiAverageAlbedo.W, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That((uint)gpuMaterial.DdgiMaterialPolicy.W, Is.EqualTo(5u));
            });
        }

        [Test]
        public void BuildGpuMaterialData_UsesRuntimeLinearTextureAverageWhenCookedMetadataIsAbsent()
        {
            var material = new ModelMaterial
            {
                Albedo = new Vector4(0.4f, 0.5f, 0.8f, 1f),
                AlbedoTexturePath = "raw-base.png"
            };

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(
                material,
                new MaterialTextureIndices(10, 11, 12, 13),
                runtimeBaseColorTextureAverageLinear: new Vector4(0.25f, 0.4f, 0.5f, 1f));

            Assert.That(
                gpuMaterial.DdgiAverageAlbedo,
                Is.EqualTo(new Vector4(0.1f, 0.2f, 0.4f, 1f)));
            Assert.That((uint)gpuMaterial.DdgiMaterialPolicy.W, Is.EqualTo(5u));
        }

        [Test]
        public void BuildGpuMaterialData_PreservesValidBlackCompactAlbedo()
        {
            var material = new ModelMaterial
            {
                Albedo = Vector4.One,
                AlbedoTexturePath = "black.png",
                DdgiBaseColorTextureAverageLinear = Vector4.Zero
            };

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(
                material,
                new MaterialTextureIndices(10, 11, 12, 13));

            Assert.Multiple(() =>
            {
                Assert.That(gpuMaterial.DdgiAverageAlbedo, Is.EqualTo(Vector4.Zero));
                Assert.That((uint)gpuMaterial.DdgiMaterialPolicy.W, Is.EqualTo(5u));
            });
        }

        [Test]
        public void BuildGpuMaterialData_BakesDdgiEmissiveStrengthAndTexturePolicy()
        {
            var material = new ModelMaterial
            {
                Albedo = new Vector4(0.2f, 0.4f, 0.6f, 1.0f),
                Emissive = new Vector4(0.5f, 0.25f, 0.125f, 1.0f),
                EmissiveStrength = 4.0f,
                AlphaMode = ModelAlphaMode.Mask,
                AlbedoTexturePath = "base.png",
                EmissiveTexturePath = "emissive.png"
            };

            GPUMaterialData gpuMaterial = ModelRenderUploadService.BuildGpuMaterialData(
                material,
                new MaterialTextureIndices(10, 11, 12, 13));

            Assert.Multiple(() =>
            {
                Assert.That(gpuMaterial.DdgiAverageAlbedo, Is.EqualTo(new Vector4(0.2f, 0.4f, 0.6f, 1.0f)));
                Assert.That(gpuMaterial.DdgiAverageEmissive.X, Is.EqualTo(2.0f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.Y, Is.EqualTo(1.0f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.Z, Is.EqualTo(0.5f));
                Assert.That(gpuMaterial.DdgiAverageEmissive.W, Is.EqualTo(ModelRenderUploadService.CalculateDdgiEmissiveImportance(2.0f, 1.0f, 0.5f)).Within(0.0001f));
                Assert.That(gpuMaterial.DdgiMaterialPolicy.X, Is.EqualTo(1f));
                Assert.That(gpuMaterial.DdgiMaterialPolicy.Y, Is.EqualTo(2f));
                Assert.That(gpuMaterial.DdgiMaterialPolicy.Z, Is.EqualTo(gpuMaterial.DdgiAverageEmissive.W).Within(0.0001f));
                Assert.That((uint)gpuMaterial.DdgiMaterialPolicy.W, Is.EqualTo(3u));
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
            GPUMaterialData aboveOne = ModelRenderUploadService.BuildGpuMaterialData(
                new ModelMaterial { AlphaMode = ModelAlphaMode.Mask, AlphaCutoff = 1.25f },
                textures);
            MaterialRenderMetadata aboveOneMetadata =
                ModelRenderUploadService.BuildMaterialRenderMetadata(
                    new ModelMaterial
                    {
                        AlphaMode = ModelAlphaMode.Mask,
                        AlphaCutoff = 1.25f
                    });
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
                Assert.That(aboveOne.NormalScaleBias.Z, Is.EqualTo(1.25f));
                Assert.That(aboveOneMetadata.AlphaCutoff, Is.EqualTo(1.25f));
                Assert.That(
                    () => ModelRenderUploadService.BuildGpuMaterialData(
                        new ModelMaterial { AlphaCutoff = -0.25f },
                        textures),
                    Throws.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => ModelRenderUploadService.BuildMaterialRenderMetadata(
                        new ModelMaterial { AlphaCutoff = -0.25f }),
                    Throws.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => ModelRenderUploadService.BuildGpuMaterialData(
                        new ModelMaterial { AlphaCutoff = float.NaN },
                        textures),
                    Throws.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => ModelRenderUploadService.BuildMaterialRenderMetadata(
                        new ModelMaterial { AlphaCutoff = float.NegativeInfinity }),
                    Throws.InstanceOf<ArgumentOutOfRangeException>());
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
        public void TextureImageCacheKey_SharesPhysicalImageAcrossSamplerStates()
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

            string repeatDescriptorKey = TextureManager.CreateTextureCacheKey(
                path,
                generateMipmaps: true,
                srgb: true,
                samplerDescription: repeat);
            string clampDescriptorKey = TextureManager.CreateTextureCacheKey(
                path,
                generateMipmaps: true,
                srgb: true,
                samplerDescription: clampNearest);
            string imageKey = TextureManager.CreateTextureImageCacheKey(
                path,
                generateMipmaps: true,
                srgb: true);

            Assert.Multiple(() =>
            {
                Assert.That(repeatDescriptorKey, Is.Not.EqualTo(clampDescriptorKey));
                Assert.That(repeatDescriptorKey, Does.StartWith(imageKey));
                Assert.That(clampDescriptorKey, Does.StartWith(imageKey));
                Assert.That(imageKey, Does.Not.Contain("sampler="));
            });
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
        public void RequiresAlphaCoveragePreservingMips_TracksMaskedAndFoliageMaterials()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ModelRenderUploadService.RequiresAlphaCoveragePreservingMips(new ModelMaterial { AlphaMode = ModelAlphaMode.Opaque }), Is.False);
                Assert.That(ModelRenderUploadService.RequiresAlphaCoveragePreservingMips(new ModelMaterial { AlphaMode = ModelAlphaMode.Mask }), Is.True);
                Assert.That(ModelRenderUploadService.RequiresAlphaCoveragePreservingMips(new ModelMaterial { FeatureFlags = (uint)MaterialFeatureFlags.Foliage }), Is.True);
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
                BindlessIndex.DefaultWhiteTexture);

            var material = ModelRenderUploadService.BuildGpuMaterialData(ModelMaterial.Default, textures);

            Assert.Multiple(() =>
            {
                Assert.That(material.AlbedoTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
                Assert.That(material.NormalTextureIndex, Is.EqualTo(BindlessIndex.DefaultNormalTexture));
                Assert.That(material.MetallicRoughnessTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
                Assert.That(material.EmissiveTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
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
                Assert.That(data.Dispersion, Is.EqualTo(new Vector4(0.65f, 1f, 1f, 1f)));
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
        public void BuildMaterialRenderMetadata_KeepsExplicitThinTransmissionOpaque()
        {
            MaterialRenderMetadata metadata = ModelRenderUploadService.BuildMaterialRenderMetadata(
                new ModelMaterial
                {
                    FeatureFlags = (uint)MaterialFeatureFlags.Transmission,
                    TransmissionFactor = 0.4f,
                    GiTransmissionPolicy = ModelGiTransmissionPolicy.ThinSurface,
                    AlphaMode = ModelAlphaMode.Opaque
                });

            Assert.Multiple(() =>
            {
                Assert.That(metadata.BlendMode, Is.EqualTo(MaterialBlendMode.Opaque));
                Assert.That(metadata.TransmissionPolicy, Is.EqualTo(GiTransmissionPolicy.ThinSurface));
            });
        }

        [Test]
        public void DefaultMaterial_HasNeutralEmissiveStrength()
        {
            Assert.That(ModelMaterial.Default.EmissiveStrength, Is.EqualTo(1f));
        }

        [Test]
        public void BuildGpuVertices_DerivesTangentHandednessFromBitangent()
        {
            var subMesh = new ModelSubMesh
            {
                Vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f)
                },
                Normals = new[]
                {
                    Vector3.UnitZ,
                    Vector3.UnitZ,
                    Vector3.UnitZ
                },
                Tangents = new[]
                {
                    Vector3.UnitX,
                    Vector3.UnitX,
                    Vector3.UnitX
                },
                Bitangents = new[]
                {
                    -Vector3.UnitY,
                    -Vector3.UnitY,
                    -Vector3.UnitY
                },
                TexCoords = new[]
                {
                    Vector2.Zero,
                    Vector2.Zero,
                    Vector2.Zero
                },
                Indices = new uint[] { 0, 1, 2 }
            };

            GPUVertex[] vertices = InvokeBuildGpuVertices(subMesh);

            Assert.That(vertices.Select(v => v.Tangent.W), Is.All.EqualTo(-1f));
        }

        [Test]
        public void BuildGpuVertices_DefaultsMissingImportedVertexColorsToWhite()
        {
            var subMesh = new ModelSubMesh
            {
                Vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f)
                },
                Normals = new[]
                {
                    Vector3.UnitZ,
                    Vector3.UnitZ,
                    Vector3.UnitZ
                },
                TexCoords = new[]
                {
                    Vector2.Zero,
                    Vector2.Zero,
                    Vector2.Zero
                },
                Indices = new uint[] { 0, 1, 2 }
            };

            GPUVertex[] vertices = InvokeBuildGpuVertices(subMesh);

            Assert.That(vertices.Select(v => v.Color), Is.All.EqualTo(GPUVertex.DefaultColor));
        }

        [Test]
        public void MeshManagerPositionOnlyGpuVertices_DefaultsVertexColorsToWhite()
        {
            var positions = new[]
            {
                new System.Numerics.Vector3(0f, 0f, 0f),
                new System.Numerics.Vector3(1f, 0f, 0f),
                new System.Numerics.Vector3(0f, 1f, 0f)
            };
            uint[] indices = { 0, 1, 2 };

            GPUVertex[] vertices = InvokeBuildGpuVertices(positions, indices);

            Assert.That(vertices.Select(v => v.Color), Is.All.EqualTo(GPUVertex.DefaultColor));
        }

        [Test]
        public void MeshUploadStagingSizer_AccountsForAlignmentAcrossBatchedMeshes()
        {
            ulong offset = 0;
            offset = InvokeAddUploadStagingBytes(offset, 12);
            offset = InvokeAddUploadStagingBytes(offset, 7);

            Assert.That(offset, Is.EqualTo(263UL));
        }

        [Test]
        public void UploadModel_WhenSecondMaterialTextureLoadFails_RollsBackPendingTexturesAndCommittedMaterial()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailTextureLoadCall = 8
            };
            var service = new ModelRenderUploadService(backend);

            InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(
                () => service.UploadModel(CreateRollbackTestModel()));

            Assert.Multiple(() =>
            {
                Assert.That(failure!.Message, Is.EqualTo("Injected texture load failure."));
                Assert.That(backend.AcquiredTextures, Has.Count.EqualTo(7));
                Assert.That(
                    backend.DirectTextureReleaseCalls.Select(static handle => handle.Index),
                    Is.EqualTo(new[] { 106, 105 }));
                Assert.That(
                    backend.MaterialReleaseCalls.Select(static handle => handle.Index),
                    Is.EqualTo(new[] { 200 }));
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "texture:106",
                            "texture:105",
                            "material:200"
                        }));
                Assert.That(backend.EveryAcquiredTextureWasReleasedExactlyOnce, Is.True);
            });
        }

        [Test]
        public void UploadModel_WhenMeshRegistrationFails_ReleasesEveryMaterialInReverseOrder()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailMeshRegistration = true
            };
            var service = new ModelRenderUploadService(backend);

            InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(
                () => service.UploadModel(CreateRollbackTestModel()));

            Assert.Multiple(() =>
            {
                Assert.That(failure!.Message, Is.EqualTo("Injected mesh registration failure."));
                Assert.That(backend.AcquiredTextures, Has.Count.EqualTo(10));
                Assert.That(backend.DirectTextureReleaseCalls, Is.Empty);
                Assert.That(
                    backend.MaterialReleaseCalls.Select(static handle => handle.Index),
                    Is.EqualTo(new[] { 200, 201, 200 }));
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "material:200",
                            "material:201",
                            "material:200"
                        }));
                Assert.That(
                    backend.NoOutstandingTextureReferences,
                    Is.True);
            });
        }

        [TestCase(
            (int)ModelUploadPublicationStage.AfterMeshRegistration,
            1)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterMeshRegistration,
            2)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterMeshRegistration,
            3)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterMeshRegistration,
            4)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterRenderObjectAttachment,
            1)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterRenderObjectAttachment,
            2)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterRenderObjectAttachment,
            3)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterRenderObjectAttachment,
            4)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterBaseMaterialTransfer,
            1)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterBaseMaterialTransfer,
            2)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterBaseMaterialTransfer,
            3)]
        [TestCase(
            (int)ModelUploadPublicationStage.AfterBaseMaterialTransfer,
            4)]
        public void PublicationRollbackFailure_DisposeRetriesOnlyFailedOccurrence(
            int stageValue,
            int failedRollbackCall)
        {
            ModelUploadPublicationStage stage =
                (ModelUploadPublicationStage)stageValue;
            var backend = new RecordingModelRenderUploadBackend
            {
                FailRollbackCall = failedRollbackCall
            };
            var service =
                new ModelRenderUploadService(backend);
            service.UploadPublicationFaultInjector =
                currentStage =>
                {
                    if (currentStage == stage)
                    {
                        throw new InvalidOperationException(
                            $"Injected publication failure at {stage}.");
                    }
                };
            string[] expectedInitialRollback =
            [
                "mesh:0",
                "material:200",
                "material:201",
                "material:200"
            ];

            Assert.That(
                () => service.UploadModel(
                    CreateRollbackTestModel()),
                Throws.TypeOf<AggregateException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(expectedInitialRollback));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.EqualTo(1));
                Assert.That(
                    service.LastUploadDiagnostics.ModelName,
                    Is.Empty);
            });

            string failedResource =
                expectedInitialRollback[
                    failedRollbackCall - 1];
            service.Dispose();
            int callsAfterCompletion =
                backend.RollbackCalls.Count;
            service.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        expectedInitialRollback.Concat(
                            new[] { failedResource })));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.NoOutstandingTextureReferences,
                    Is.True);
                Assert.That(
                    backend.NoOutstandingMeshReferences,
                    Is.True);
                Assert.That(
                    backend.NoOutstandingMaterialReferences,
                    Is.True);
                Assert.That(
                    backend.RollbackCalls.Count,
                    Is.EqualTo(callsAfterCompletion));
            });
        }

        [Test]
        public void InvalidPrimitiveProfile_FallbackRetainRollbackIsDurable()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                RollbackFailureResource = "material:200",
                RemainingRollbackFailures = 1
            };
            var service =
                new ModelRenderUploadService(backend);
            service.UploadPublicationFaultInjector =
                stage =>
                {
                    if (stage ==
                        ModelUploadPublicationStage
                            .AfterPrimitiveMaterialRegistration)
                    {
                        throw new InvalidOperationException(
                            "Injected failure after fallback material retain.");
                    }
                };

            Assert.That(
                () => service.UploadModel(
                    CreateRollbackTestModel()),
                Throws.TypeOf<AggregateException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.MaterialRetainCalls
                        .Select(static handle => handle.Index),
                    Is.EqualTo(new[] { 200 }));
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "material:200",
                            "material:201",
                            "material:200"
                        }));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.EqualTo(1));
            });

            service.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "material:200",
                            "material:201",
                            "material:200",
                            "material:200"
                        }));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.NoOutstandingTextureReferences,
                    Is.True);
                Assert.That(
                    backend.NoOutstandingMaterialReferences,
                    Is.True);
            });
        }

        [Test]
        public void PrimitiveTextureRetain_RegistrationAndReleaseFailureRemainDurablyOwned()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailPrimitiveMaterialRegistration = true,
                RollbackFailureResource = "texture:101",
                RemainingRollbackFailures = 1
            };
            var service =
                new ModelRenderUploadService(backend);

            Exception? uploadFailure = null;
            Njulf.Core.Scene.Model? unexpectedlyUploaded = null;
            try
            {
                unexpectedlyUploaded =
                    service.UploadModel(
                        CreateValidTexturedRollbackTestModel());
            }
            catch (Exception failure)
            {
                uploadFailure = failure;
            }
            if (unexpectedlyUploaded != null)
            {
                unexpectedlyUploaded.Dispose();
                Assert.Fail(
                    "Expected primitive material registration to fail. " +
                    $"Registrations={backend.PrimitiveMaterialRegistrationCalls}; " +
                    $"diagnostics={service.LastUploadDiagnostics.PrimitiveProfileDiagnostic}");
            }
            Assert.That(
                uploadFailure,
                Is.TypeOf<AggregateException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.PrimitiveMaterialRegistrationCalls,
                    Is.EqualTo(1));
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "texture:101",
                            "texture:100",
                            "material:200"
                        }));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.EqualTo(1));
                Assert.That(
                    service.LastUploadDiagnostics.ModelName,
                    Is.Empty);
            });

            service.Dispose();
            int callsAfterCompletion =
                backend.RollbackCalls.Count;
            service.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.RollbackCalls,
                    Is.EqualTo(
                        new[]
                        {
                            "texture:101",
                            "texture:100",
                            "material:200",
                            "texture:101"
                        }));
                Assert.That(
                    backend.DirectTextureReleaseCalls
                        .Select(static handle => handle.Index),
                    Is.EqualTo(new[] { 101, 100, 101 }));
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.NoOutstandingTextureReferences,
                    Is.True);
                Assert.That(
                    backend.NoOutstandingMaterialReferences,
                    Is.True);
                Assert.That(
                    backend.RollbackCalls.Count,
                    Is.EqualTo(callsAfterCompletion));
            });
        }

        [TestCase("texture:106")]
        [TestCase("texture:105")]
        [TestCase("material:200")]
        public void FailedMaterialRollback_DisposeRetriesOnlyFailedOccurrence(
            string failedResource)
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailTextureLoadCall = 8,
                RollbackFailureResource = failedResource,
                RemainingRollbackFailures = 1
            };
            var service =
                new ModelRenderUploadService(backend);

            Assert.That(
                () => service.UploadModel(
                    CreateRollbackTestModel()),
                Throws.TypeOf<AggregateException>());
            Assert.That(
                service.PendingMaterialRollbackResourceCount,
                Is.EqualTo(1));

            service.Dispose();
            int callsAfterCompletion =
                backend.RollbackCalls.Count;
            service.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.RollbackCalls.Count(
                        item =>
                            item == failedResource),
                    Is.EqualTo(2));
                Assert.That(
                    backend.RollbackCalls
                        .Where(
                            item =>
                                item != failedResource)
                        .GroupBy(static item => item)
                        .Select(
                            static group =>
                                group.Count()),
                    Is.All.EqualTo(1));
                Assert.That(
                    backend.RollbackCalls.Count,
                    Is.EqualTo(callsAfterCompletion));
                Assert.That(
                    backend
                        .EveryAcquiredTextureWasReleasedExactlyOnce,
                    Is.True);
            });
        }

        [Test]
        public void PendingMaterialRollback_SubsequentUploadDrainsBeforeValidation()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailTextureLoadCall = 8,
                RollbackFailureResource = "texture:106",
                RemainingRollbackFailures = 1
            };
            using var service =
                new ModelRenderUploadService(backend);

            Assert.That(
                () => service.UploadModel(
                    CreateRollbackTestModel()),
                Throws.TypeOf<AggregateException>());
            Assert.That(
                service.PendingMaterialRollbackResourceCount,
                Is.EqualTo(1));

            Assert.That(
                () => service.UploadModel(null!),
                Throws.ArgumentNullException);

            Assert.Multiple(() =>
            {
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.RollbackCalls.Count(
                        static item =>
                            item == "texture:106"),
                    Is.EqualTo(2));
                Assert.That(
                    backend
                        .EveryAcquiredTextureWasReleasedExactlyOnce,
                    Is.True);
            });
        }

        [Test]
        public void PendingMaterialRollback_RepeatedDisposeFailureRetainsOwnershipAndClosesUploads()
        {
            var backend = new RecordingModelRenderUploadBackend
            {
                FailTextureLoadCall = 8,
                RollbackFailureResource = "texture:106",
                RemainingRollbackFailures = 2
            };
            var service =
                new ModelRenderUploadService(backend);

            Assert.That(
                () => service.UploadModel(
                    CreateRollbackTestModel()),
                Throws.TypeOf<AggregateException>());
            Assert.That(
                service.Dispose,
                Throws.TypeOf<AggregateException>());
            Assert.Multiple(() =>
            {
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.EqualTo(1));
                Assert.That(
                    () => service.UploadModel(
                        CreateRollbackTestModel()),
                    Throws.TypeOf<ObjectDisposedException>());
            });

            service.Dispose();
            int callsAfterCompletion =
                backend.RollbackCalls.Count;
            service.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(
                    service.PendingMaterialRollbackResourceCount,
                    Is.Zero);
                Assert.That(
                    backend.RollbackCalls.Count(
                        static item =>
                            item == "texture:106"),
                    Is.EqualTo(3));
                Assert.That(
                    backend.RollbackCalls.Count,
                    Is.EqualTo(callsAfterCompletion));
                Assert.That(
                    backend
                        .EveryAcquiredTextureWasReleasedExactlyOnce,
                    Is.True);
            });
        }

        private static ModelMesh CreateRollbackTestModel()
        {
            var model = new ModelMesh
            {
                Name = "Rollback test model",
                Vertices =
                [
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f)
                ],
                Normals =
                [
                    Vector3.UnitZ,
                    Vector3.UnitZ,
                    Vector3.UnitZ
                ],
                TexCoords =
                [
                    Vector2.Zero,
                    Vector2.UnitX,
                    Vector2.UnitY
                ],
                Indices = [0u, 1u, 2u]
            };
            model.Materials.Add(CreateInvalidTexturedMaterial("First", 0));
            model.Materials.Add(CreateInvalidTexturedMaterial("Second", 5));
            return model;
        }

        private static ModelMesh CreateValidTexturedRollbackTestModel()
        {
            byte[] png =
                Njulf.Rendering.Debug.PngScreenshotEncoder.Encode(
                    [
                        0, 0, 0, 0,
                        255, 255, 255, 255
                    ],
                    width: 2,
                    height: 1,
                    Njulf.Rendering.Debug.ScreenshotPixelFormat.Rgba8);
            Vector3[] vertices =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(2f, 0f, 0f),
                new Vector3(3f, 0f, 0f),
                new Vector3(2f, 1f, 0f)
            ];
            Vector3[] normals =
                Enumerable.Repeat(
                        Vector3.UnitZ,
                        vertices.Length)
                    .ToArray();
            Vector2[] texCoords =
            [
                new Vector2(0.25f, 0.5f),
                new Vector2(0.25f, 0.5f),
                new Vector2(0.25f, 0.5f),
                new Vector2(0.75f, 0.5f),
                new Vector2(0.75f, 0.5f),
                new Vector2(0.75f, 0.5f)
            ];
            uint[] indices =
                [0u, 1u, 2u, 3u, 4u, 5u];
            var model = new ModelMesh
            {
                Name = "Primitive texture rollback test model",
                Vertices = vertices,
                Normals = normals,
                TexCoords = texCoords,
                Indices = indices
            };
            var source =
                new ModelTextureSource
                {
                    DebugName = "sparse-two-pixel.png",
                    SourceKind =
                        TextureSourceKind.EmbeddedMemory,
                    ContainerKind =
                        TextureContainerKind.StandardImage,
                    Bytes = png,
                    MimeType = "image/png",
                    CacheIdentity =
                        "rollback-test:sparse-two-pixel",
                    EncodedByteLength = png.Length
                };
            var binding =
                new ModelTextureSlot
                {
                    Source = source,
                    ColorSpace =
                        TextureColorSpace.Srgb,
                    Sampler =
                        new TextureSamplerDescription(
                            TextureWrapMode.ClampToEdge,
                            TextureWrapMode.ClampToEdge,
                            TextureFilterMode.Nearest,
                            TextureFilterMode.Nearest,
                            TextureMipFilterMode.Nearest,
                            1f)
                };
            model.Materials.Add(
                new ModelMaterial
                {
                    Name = "Valid textured material",
                    Albedo = Vector4.One,
                    Emissive = Vector4.One,
                    EmissiveStrength = 1f,
                    AlphaMode = ModelAlphaMode.Mask,
                    AlphaCutoff = 0.5f,
                    BaseColorTexture = binding,
                    EmissiveTexture = binding
                });
            model.SubMeshes.Add(
                new ModelSubMesh
                {
                    Name = "Sparse correlated primitive",
                    MaterialIndex = 0,
                    Vertices = vertices,
                    Normals = normals,
                    TexCoords = texCoords,
                    Indices = indices
                });
            return model;
        }

        private static ModelMaterial CreateInvalidTexturedMaterial(string name, int identityOffset)
        {
            return new ModelMaterial
            {
                Name = name,
                BaseColorTexture = CreateInvalidTextureSlot($"{name}-{identityOffset + 0}"),
                NormalTexture = CreateInvalidTextureSlot($"{name}-{identityOffset + 1}"),
                MetallicRoughnessTexture = CreateInvalidTextureSlot($"{name}-{identityOffset + 2}"),
                OcclusionTexture = CreateInvalidTextureSlot($"{name}-{identityOffset + 3}"),
                EmissiveTexture = CreateInvalidTextureSlot($"{name}-{identityOffset + 4}")
            };
        }

        private static ModelTextureSlot CreateInvalidTextureSlot(string identity)
        {
            return new ModelTextureSlot
            {
                Source = new ModelTextureSource
                {
                    DebugName = identity,
                    CacheIdentity = $"rollback-test:{identity}",
                    Bytes = [0x00],
                    EncodedByteLength = 1
                }
            };
        }

        private static GPUVertex[] InvokeBuildGpuVertices(ModelSubMesh subMesh)
        {
            MethodInfo method = typeof(ModelRenderUploadService).GetMethod(
                "BuildGpuVertices",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ModelSubMesh) },
                modifiers: null)
                ?? throw new MissingMethodException(nameof(ModelRenderUploadService), "BuildGpuVertices");

            return (GPUVertex[])method.Invoke(null, new object[] { subMesh })!;
        }

        private static GPUVertex[] InvokeBuildGpuVertices(System.Numerics.Vector3[] positions, uint[] indices)
        {
            MethodInfo method = typeof(MeshManager).GetMethod(
                "BuildGpuVertices",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(System.Numerics.Vector3[]), typeof(uint[]) },
                modifiers: null)
                ?? throw new MissingMethodException(nameof(MeshManager), "BuildGpuVertices");

            return (GPUVertex[])method.Invoke(null, new object[] { positions, indices })!;
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

        private sealed class RecordingModelRenderUploadBackend : IModelRenderUploadBackend
        {
            private readonly List<TextureHandle> _pendingTextureOwnership = new();
            private readonly Dictionary<TextureHandle, int> _outstandingTextureReferences = new();
            private readonly Dictionary<TextureHandle, int> _textureReleaseOccurrences = new();
            private readonly Dictionary<TextureHandle, TextureTransportStatistics> _textureStatistics = new();
            private readonly Dictionary<MaterialHandle, MaterialDefinition> _materialDefinitions = new();
            private readonly Dictionary<MaterialHandle, TextureHandle[]> _materialTextures = new();
            private readonly Dictionary<MaterialHandle, int> _materialReferences = new();
            private readonly Dictionary<MeshHandle, int> _meshReferences = new();
            private int _nextTextureIndex = 100;
            private int _nextMaterialIndex = 200;
            private int _nextMeshIndex;
            private int _textureLoadCalls;
            private int _rollbackAttemptCalls;

            public int FailTextureLoadCall { get; init; }

            public bool FailMeshRegistration { get; init; }

            public bool FailPrimitiveMaterialRegistration { get; init; }

            public string? RollbackFailureResource { get; init; }

            public int RemainingRollbackFailures { get; set; }

            public int FailRollbackCall { get; init; }

            public List<TextureHandle> AcquiredTextures { get; } = new();

            public List<TextureHandle> DirectTextureReleaseCalls { get; } = new();

            public List<MaterialHandle> MaterialReleaseCalls { get; } = new();

            public List<MaterialHandle> MaterialRetainCalls { get; } = new();

            public List<MeshHandle> MeshReleaseCalls { get; } = new();

            public List<string> RollbackCalls { get; } = new();

            public int PrimitiveMaterialRegistrationCalls { get; private set; }

            public bool EveryAcquiredTextureWasReleasedExactlyOnce =>
                AcquiredTextures.Count > 0 &&
                AcquiredTextures.All(
                    handle =>
                        _outstandingTextureReferences.GetValueOrDefault(handle) == 0 &&
                        _textureReleaseOccurrences.GetValueOrDefault(handle) == 1);

            public bool NoOutstandingTextureReferences =>
                AcquiredTextures.Count > 0 &&
                AcquiredTextures.All(
                    handle =>
                        _outstandingTextureReferences
                            .GetValueOrDefault(handle) ==
                        0);

            public bool NoOutstandingMeshReferences =>
                _meshReferences.Count == 0;

            public bool NoOutstandingMaterialReferences =>
                _materialReferences.Count == 0;

            public TextureHandle DefaultWhiteTexture { get; } = new(1, 1);

            public TextureHandle DefaultNormalTexture { get; } = new(2, 1);

            public TextureHandle DefaultBlackTexture { get; } = new(3, 1);

            public void InitializeDefaultTextures()
            {
            }

            public TextureHandle LoadTexture(
                ModelTextureSource source,
                TextureSamplerDescription samplerDescription,
                bool generateMipmaps,
                bool srgb,
                bool requireWithinMemoryBudget,
                TextureSemantic semantic,
                RuntimeTextureMipPolicy mipPolicy)
            {
                TextureHandle handle =
                    AcquireTexture();
                if (source.Bytes is { Length: > 0 } bytes)
                {
                    _textureStatistics[handle] =
                        new TextureTransportStatistics
                        {
                            Status =
                                TextureTransportStatisticsStatus.Valid,
                            Validity =
                                TextureTransportStatisticsValidity
                                    .SourceContentHash |
                                TextureTransportStatisticsValidity
                                    .DecodedPixels |
                                TextureTransportStatisticsValidity
                                    .LinearChannelMoments |
                                TextureTransportStatisticsValidity
                                    .EmissiveLuminanceMoments |
                                TextureTransportStatisticsValidity
                                    .AlphaHistogram,
                            SourceContentHash =
                                CookedHash.Bytes(bytes),
                            Semantic = semantic,
                            ColorSpace = source.ContainerKind ==
                                         TextureContainerKind.StandardImage &&
                                         srgb
                                ? TextureColorSpace.Srgb
                                : TextureColorSpace.Linear,
                            Width = 1,
                            Height = 1,
                            PixelCount = 1,
                            LinearChannelMean =
                                TextureTransportVector4.One,
                            LinearChannelSecondMoment =
                                TextureTransportVector4.One,
                            Decoder =
                                "ModelRenderUploadServiceTests fixture",
                            AlphaHistogram =
                                Enumerable.Range(
                                        0,
                                        TextureTransportStatistics
                                            .AlphaHistogramBinCount)
                                    .Select(
                                        static index =>
                                            index ==
                                            TextureTransportStatistics
                                                .AlphaHistogramBinCount -
                                            1
                                                ? 1UL
                                                : 0UL)
                                    .ToArray()
                        };
                }

                return handle;
            }

            public TextureHandle LoadOptionalTextureFromFile(
                string? path,
                TextureHandle fallback,
                bool generateMipmaps,
                bool srgb,
                TextureSemantic semantic,
                RuntimeTextureMipPolicy mipPolicy)
            {
                return string.IsNullOrWhiteSpace(path) ? fallback : AcquireTexture();
            }

            public int GetBindlessTextureIndex(TextureHandle handle)
            {
                return handle.Index;
            }

            public bool TryGetTextureTransportStatistics(
                TextureHandle handle,
                out TextureTransportStatistics statistics)
            {
                return _textureStatistics.TryGetValue(
                    handle,
                    out statistics!);
            }

            public void RetainTexture(TextureHandle handle)
            {
                IncrementTextureReference(handle);
                _pendingTextureOwnership.Add(handle);
            }

            public void ReleaseTexture(TextureHandle handle)
            {
                DirectTextureReleaseCalls.Add(handle);
                RecordRollbackAttempt(
                    $"texture:{handle.Index}");
                ReleaseTextureReference(handle);
                int pendingIndex =
                    _pendingTextureOwnership.LastIndexOf(
                        handle);
                if (pendingIndex >= 0)
                {
                    _pendingTextureOwnership.RemoveAt(
                        pendingIndex);
                }
            }

            public MaterialHandle RegisterMaterialDefinition(MaterialDefinition definition)
            {
                return RegisterMaterial(definition);
            }

            public MaterialHandle RegisterMaterialDefinition(
                MaterialDefinition definition,
                GiMaterialTransportProfile primitiveProfile)
            {
                PrimitiveMaterialRegistrationCalls++;
                if (FailPrimitiveMaterialRegistration)
                {
                    throw new InvalidOperationException(
                        "Injected primitive material registration failure.");
                }

                return RegisterMaterial(definition);
            }

            public MaterialDefinition GetMaterialDefinition(MaterialHandle handle)
            {
                return _materialDefinitions[handle];
            }

            public IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle)
            {
                return _materialTextures[handle];
            }

            public void ReleaseMaterial(MaterialHandle handle)
            {
                if (!_materialTextures.TryGetValue(handle, out TextureHandle[]? textures) ||
                    !_materialReferences.TryGetValue(handle, out int references) ||
                    references <= 0)
                {
                    throw new InvalidOperationException($"Material {handle.Index} was released more than once.");
                }

                MaterialReleaseCalls.Add(handle);
                RecordRollbackAttempt(
                    $"material:{handle.Index}");
                foreach (TextureHandle texture in textures)
                    ReleaseTextureReference(texture);
                references--;
                if (references == 0)
                {
                    _materialReferences.Remove(handle);
                    _materialTextures.Remove(handle);
                    _materialDefinitions.Remove(handle);
                }
                else
                {
                    _materialReferences[handle] = references;
                }
            }

            public void RetainMaterial(MaterialHandle handle)
            {
                if (!_materialTextures.TryGetValue(handle, out TextureHandle[]? textures) ||
                    !_materialReferences.TryGetValue(handle, out int references) ||
                    references <= 0)
                {
                    throw new InvalidOperationException($"Material {handle.Index} is not live.");
                }

                foreach (TextureHandle texture in textures)
                    IncrementTextureReference(texture);
                _materialReferences[handle] =
                    checked(references + 1);
                MaterialRetainCalls.Add(handle);
            }

            public MeshHandle[] RegisterMeshes(IReadOnlyList<MeshManager.MeshRegistrationData> meshes)
            {
                if (FailMeshRegistration)
                    throw new InvalidOperationException("Injected mesh registration failure.");

                var handles = new MeshHandle[meshes.Count];
                for (int i = 0; i < handles.Length; i++)
                {
                    handles[i] =
                        new MeshHandle(_nextMeshIndex++, 1);
                    _meshReferences.Add(handles[i], 1);
                }
                return handles;
            }

            public void RetainMesh(MeshHandle handle)
            {
                if (!_meshReferences.TryGetValue(handle, out int references) ||
                    references <= 0)
                {
                    throw new InvalidOperationException($"Mesh {handle.Index} is not live.");
                }

                _meshReferences[handle] =
                    checked(references + 1);
            }

            public void ReleaseMesh(MeshHandle handle)
            {
                if (!_meshReferences.TryGetValue(handle, out int references) ||
                    references <= 0)
                {
                    throw new InvalidOperationException($"Mesh {handle.Index} was released more than acquired.");
                }

                MeshReleaseCalls.Add(handle);
                RecordRollbackAttempt(
                    $"mesh:{handle.Index}");
                references--;
                if (references == 0)
                    _meshReferences.Remove(handle);
                else
                    _meshReferences[handle] = references;
            }

            private TextureHandle AcquireTexture()
            {
                _textureLoadCalls++;
                if (_textureLoadCalls == FailTextureLoadCall)
                    throw new InvalidOperationException("Injected texture load failure.");

                var handle = new TextureHandle(_nextTextureIndex++, 1);
                AcquiredTextures.Add(handle);
                IncrementTextureReference(handle);
                _pendingTextureOwnership.Add(handle);
                return handle;
            }

            private MaterialHandle RegisterMaterial(MaterialDefinition definition)
            {
                var handle = new MaterialHandle(_nextMaterialIndex++, 1);
                _materialDefinitions.Add(handle, definition);
                _materialTextures.Add(handle, _pendingTextureOwnership.ToArray());
                _materialReferences.Add(handle, 1);
                _pendingTextureOwnership.Clear();
                return handle;
            }

            private void IncrementTextureReference(TextureHandle handle)
            {
                _outstandingTextureReferences[handle] =
                    checked(_outstandingTextureReferences.GetValueOrDefault(handle) + 1);
            }

            private void RecordRollbackAttempt(
                string resource)
            {
                RollbackCalls.Add(resource);
                int attempt =
                    Interlocked.Increment(
                        ref _rollbackAttemptCalls);
                bool failAbsoluteCall =
                    FailRollbackCall > 0 &&
                    attempt == FailRollbackCall;
                bool failResource =
                    RemainingRollbackFailures > 0 &&
                    string.Equals(
                        resource,
                        RollbackFailureResource,
                        StringComparison.Ordinal);
                if (!failAbsoluteCall &&
                    !failResource)
                {
                    return;
                }

                if (failResource)
                    RemainingRollbackFailures--;
                throw new InvalidOperationException(
                    $"Injected rollback failure for {resource}.");
            }

            private void ReleaseTextureReference(TextureHandle handle)
            {
                int outstanding = _outstandingTextureReferences.GetValueOrDefault(handle);
                if (outstanding <= 0)
                    throw new InvalidOperationException($"Texture {handle.Index} was released more than acquired.");

                _outstandingTextureReferences[handle] = outstanding - 1;
                _textureReleaseOccurrences[handle] =
                    checked(_textureReleaseOccurrences.GetValueOrDefault(handle) + 1);
            }
        }
    }
}
