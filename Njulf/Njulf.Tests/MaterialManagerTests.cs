using System.Linq;
using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class MaterialManagerTests
    {
        [Test]
        public void Constructor_DefaultsCanonicalMaterialsToLegacyTransport()
        {
            using var manager = new MaterialManager();

            GPUMaterialData material =
                manager.GetMaterialData(manager.DefaultMaterialHandle);

            Assert.Multiple(() =>
            {
                Assert.That(manager.TransportV2Enabled, Is.False);
                Assert.That(
                    ((GiMaterialTransportFlags)material.TransportFlags)
                    .HasFlag(GiMaterialTransportFlags.LegacyV1Fallback),
                    Is.True);
                Assert.That(
                    manager.Diagnostics.ActiveLegacyV1FallbackCount,
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void UpdateMaterial_InvalidatesDeduplicationAndMarksContentDirty()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = MaterialManager.CreateDefaultMaterial();
            material.Albedo.W = 0.9f;
            MaterialHandle handle = manager.RegisterMaterial(material);
            uint beforeRevision = manager.MaterialDataRevision;

            material.MetallicRoughnessAO.X = 0.75f;
            manager.UpdateMaterial(handle, material);

            MaterialHandle newlyRegistered = manager.RegisterMaterial(MaterialManager.CreateDefaultMaterial());
            Assert.Multiple(() =>
            {
                Assert.That(manager.GetMaterialData(handle).MetallicRoughnessAO.X, Is.EqualTo(0.75f));
                Assert.That(manager.MaterialDataRevision, Is.GreaterThan(beforeRevision));
                Assert.That(newlyRegistered, Is.Not.EqualTo(handle));
            });
        }
        [Test]
        public void DefaultMaterial_UsesCanonicalBindlessDefaults()
        {
            using var manager = new MaterialManager();

            GPUMaterialData material = manager.GetMaterialData(manager.DefaultMaterialHandle);
            MaterialRenderMetadata metadata = manager.GetMaterialMetadata(manager.DefaultMaterialHandle);

            Assert.Multiple(() =>
            {
                Assert.That(manager.DefaultMaterialHandle.IsValid, Is.True);
                Assert.That(manager.ResolveMaterialIndex(manager.DefaultMaterialHandle), Is.EqualTo(0));
                Assert.That(MaterialRenderModeExtensions.FromGpuMaterial(material), Is.EqualTo(MaterialRenderMode.Opaque));
                Assert.That(material.NormalScaleBias.X, Is.EqualTo(1f));
                Assert.That(material.NormalScaleBias.Y, Is.EqualTo(0f));
                Assert.That(material.NormalScaleBias.Z, Is.EqualTo(0.5f));
                Assert.That(material.NormalScaleBias.W, Is.EqualTo(0f));
                Assert.That(material.AlbedoTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
                Assert.That(material.NormalTextureIndex, Is.EqualTo(BindlessIndex.DefaultNormalTexture));
                Assert.That(material.MetallicRoughnessTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
                Assert.That(material.EmissiveTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
                Assert.That(() => MaterialManager.ValidateMaterialTextureIndices(material), Throws.Nothing);
                Assert.That(metadata.BlendMode, Is.EqualTo(MaterialBlendMode.Opaque));
                Assert.That(metadata.IsGeometryDecal, Is.False);
                Assert.That(metadata.DoubleSided, Is.False);
                Assert.That(metadata.ReceivesShadows, Is.True);
            });
        }

        [Test]
        public void IdenticalGpuMaterialsWithDifferentMetadata_CreateDistinctHandles()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = CreateGpuMaterial(8, 9, 10, 11);

            MaterialHandle normal = manager.RegisterMaterial(material);
            MaterialHandle decal = manager.RegisterMaterial(
                material,
                new MaterialRenderMetadata
                {
                    BlendMode = MaterialBlendMode.AlphaBlend,
                    SurfaceFlags = MaterialSurfaceFlags.GeometryDecal | MaterialSurfaceFlags.ReceivesShadows,
                    DecalLayer = 2,
                    DecalDepthBias = 0.001f
                });

            Assert.Multiple(() =>
            {
                Assert.That(normal, Is.Not.EqualTo(decal));
                Assert.That(manager.GetMaterialMetadata(decal).IsGeometryDecal, Is.True);
                Assert.That(manager.GetMaterialMetadata(decal).DecalLayer, Is.EqualTo(2));
            });
        }

        [Test]
        public void IdenticalMaterials_AreDeduplicated()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = CreateGpuMaterial(8, 9, 10, 11);

            MaterialHandle first = manager.RegisterMaterial(material);
            MaterialHandle second = manager.RegisterMaterial(material);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(second));
                Assert.That(manager.RegisteredMaterialCount, Is.EqualTo(2));
                Assert.That(manager.ResolveMaterialIndex(first), Is.EqualTo(1));
            });
        }

        [Test]
        public void DifferentMaterialData_CreateDistinctHandles()
        {
            using var manager = new MaterialManager();

            MaterialHandle first = manager.RegisterMaterial(CreateGpuMaterial(8, 9, 10, 11));
            MaterialHandle second = manager.RegisterMaterial(CreateGpuMaterial(12, 9, 10, 11));

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Not.EqualTo(second));
                Assert.That(manager.RegisteredMaterialCount, Is.EqualTo(3));
                Assert.That(manager.ResolveMaterialIndex(first), Is.EqualTo(1));
                Assert.That(manager.ResolveMaterialIndex(second), Is.EqualTo(2));
            });
        }

        [Test]
        public void DestroyAndReuse_InvalidatesOldGenerationHandle()
        {
            using var manager = new MaterialManager();

            MaterialHandle oldHandle = manager.RegisterMaterial(CreateGpuMaterial(8, 9, 10, 11));
            manager.DestroyMaterial(oldHandle);
            MaterialHandle newHandle = manager.RegisterMaterial(CreateGpuMaterial(12, 9, 10, 11));

            Assert.Multiple(() =>
            {
                Assert.That(newHandle.Index, Is.EqualTo(oldHandle.Index));
                Assert.That(newHandle.Generation, Is.Not.EqualTo(oldHandle.Generation));
                Assert.That(
                    () => manager.ResolveMaterialIndex(oldHandle),
                    Throws.InvalidOperationException.With.Message.Contains("generation mismatch"));
            });
        }

        [Test]
        public void InvalidMaterialData_IsRejected()
        {
            using var manager = new MaterialManager();

            Assert.That(
                () => manager.RegisterMaterial(CreateGpuMaterial(BindlessIndex.MaxTextures, 9, 10, 11)),
                Throws.InvalidOperationException.With.Message.Contains(nameof(GPUMaterialData.AlbedoTextureIndex)));
        }

        [Test]
        public void SnapshotGrowth_PreservesExistingMaterialData()
        {
            using var manager = new MaterialManager();
            GPUMaterialData first = CreateGpuMaterial(8, 9, 10, 11);
            GPUMaterialData second = CreateGpuMaterial(12, 13, 14, 15);

            MaterialHandle firstHandle = manager.RegisterMaterial(first);
            MaterialHandle secondHandle = manager.RegisterMaterial(second);
            GPUMaterialData[] snapshot = manager.GetMaterialDataSnapshot();
            first.TransportFlags |= (uint)GiMaterialTransportFlags.LegacyV1Fallback;
            second.TransportFlags |= (uint)GiMaterialTransportFlags.LegacyV1Fallback;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot, Has.Length.EqualTo(3));
                Assert.That(snapshot[firstHandle.Index], Is.EqualTo(first));
                Assert.That(snapshot[secondHandle.Index], Is.EqualTo(second));
                Assert.That(snapshot.Select(m => m.AlbedoTextureIndex), Does.Contain(BindlessIndex.DefaultWhiteTexture));
            });
        }

        [Test]
        public void ExtensionMaterial_RegistersPayloadAndDeduplicates()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = CreateGpuMaterial(8, 9, 10, 11);
            material.FeatureFlags = (uint)MaterialFeatureFlags.Clearcoat;
            GPUMaterialExtensionData extension = CreateDefaultExtensionData();
            extension.Clearcoat = new Vector4(1f, 0.04f, 1f, 1f);

            MaterialHandle first = manager.RegisterMaterial(material, extension, MaterialRenderMetadata.FromGpuMaterial(material));
            MaterialHandle second = manager.RegisterMaterial(material, extension, MaterialRenderMetadata.FromGpuMaterial(material));
            GPUMaterialData stored = manager.GetMaterialData(first);
            GPUMaterialExtensionData? storedExtension = manager.GetMaterialExtensionData(first);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(second));
                Assert.That(manager.MaterialExtensionDataCount, Is.EqualTo(1));
                Assert.That(stored.FeatureFlags, Is.EqualTo((uint)MaterialFeatureFlags.Clearcoat));
                Assert.That(stored.ExtensionDataIndex, Is.EqualTo(0));
                Assert.That(storedExtension.HasValue, Is.True);
                Assert.That(storedExtension!.Value.Clearcoat, Is.EqualTo(extension.Clearcoat));
            });
        }

        [Test]
        public void ExtensionStorage_IsReusedAcrossAuthoredTogglesAndSlotDestruction()
        {
            using var manager = new MaterialManager();
            var extensionDefinition = new MaterialDefinition
            {
                Name = "ExtensionChurn",
                FeatureFlags = MaterialFeatureFlags.Clearcoat,
                Extensions = new MaterialExtensionDefinition
                {
                    ClearcoatFactor = 0.5f,
                    ClearcoatRoughness = 0.25f
                }
            };
            MaterialHandle handle =
                manager.RegisterMaterialDefinition(extensionDefinition);
            int originalExtensionIndex =
                manager.GetMaterialData(handle).ExtensionDataIndex;

            for (int iteration = 0; iteration < 32; iteration++)
            {
                manager.UpdateMaterialDefinition(
                    handle,
                    extensionDefinition with
                    {
                        FeatureFlags = MaterialFeatureFlags.None,
                        Extensions = MaterialExtensionDefinition.None
                    });
                Assert.That(
                    manager.GetMaterialData(handle).ExtensionDataIndex,
                    Is.EqualTo(-1),
                    $"extension removal {iteration}");

                manager.UpdateMaterialDefinition(
                    handle,
                    extensionDefinition with
                    {
                        Extensions = extensionDefinition.Extensions with
                        {
                            ClearcoatFactor = 0.25f + iteration * 0.01f
                        }
                    });
                Assert.That(
                    manager.GetMaterialData(handle).ExtensionDataIndex,
                    Is.EqualTo(originalExtensionIndex),
                    $"extension recreation {iteration}");
            }

            manager.DestroyMaterial(handle);
            MaterialHandle replacement =
                manager.RegisterMaterialDefinition(extensionDefinition with
                {
                    Name = "ExtensionReplacement"
                });

            Assert.Multiple(() =>
            {
                Assert.That(originalExtensionIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    manager.GetMaterialData(replacement).ExtensionDataIndex,
                    Is.EqualTo(originalExtensionIndex));
                Assert.That(manager.MaterialExtensionDataCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void InvalidExtensionTextureIndex_IsRejectedWithFieldName()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = CreateGpuMaterial(8, 9, 10, 11);
            material.FeatureFlags = (uint)MaterialFeatureFlags.Sheen;
            GPUMaterialExtensionData extension = CreateDefaultExtensionData();
            extension.SheenColorTextureIndex = BindlessIndex.MaxTextures;

            Assert.That(
                () => manager.RegisterMaterial(material, extension, MaterialRenderMetadata.FromGpuMaterial(material)),
                Throws.InvalidOperationException.With.Message.Contains(nameof(GPUMaterialExtensionData.SheenColorTextureIndex)));
        }

        [Test]
        public void FoliageFeatureMaterial_DoesNotRequireExtensionPayload()
        {
            using var manager = new MaterialManager();
            GPUMaterialData material = CreateGpuMaterial(8, 9, 10, 11);
            material.FeatureFlags = (uint)MaterialFeatureFlags.Foliage;

            MaterialHandle handle = manager.RegisterMaterial(material, MaterialRenderMetadata.FromGpuMaterial(material));
            GPUMaterialData stored = manager.GetMaterialData(handle);

            Assert.Multiple(() =>
            {
                Assert.That(stored.FeatureFlags, Is.EqualTo((uint)MaterialFeatureFlags.Foliage));
                Assert.That(stored.ExtensionDataIndex, Is.EqualTo(-1));
                Assert.That(manager.MaterialExtensionDataCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void Diagnostics_ReportCurrentFallbackAndInvalidProfileGauges()
        {
            using var manager = new MaterialManager();
            manager.SetTransportV2Enabled(true);
            MaterialHandle authored = manager.RegisterMaterialDefinition(
                new MaterialDefinition { Name = "Authored" });

            Assert.Multiple(() =>
            {
                Assert.That(manager.Diagnostics.ActiveLegacyV1FallbackCount, Is.Zero);
                Assert.That(manager.Diagnostics.ActiveInvalidProfileCount, Is.Zero);
            });

            manager.SetTransportV2Enabled(false);
            Assert.Multiple(() =>
            {
                Assert.That(
                    manager.Diagnostics.ActiveLegacyV1FallbackCount,
                    Is.EqualTo(2),
                    "The permanent default and the authored material are both active rollback payloads.");
                Assert.That(manager.Diagnostics.ActiveInvalidProfileCount, Is.Zero);
            });

            manager.SetTransportV2Enabled(true);
            MaterialHandle legacy = manager.RegisterMaterial(
                CreateGpuMaterial(8, 9, 10, 11));
            Assert.Multiple(() =>
            {
                Assert.That(manager.Diagnostics.ActiveLegacyV1FallbackCount, Is.EqualTo(1));
                Assert.That(manager.Diagnostics.ActiveInvalidProfileCount, Is.EqualTo(1));
                Assert.That(manager.Diagnostics.LegacyV1FallbackCount, Is.EqualTo(1));
            });

            manager.DestroyMaterial(legacy);
            Assert.Multiple(() =>
            {
                Assert.That(manager.Diagnostics.ActiveLegacyV1FallbackCount, Is.Zero);
                Assert.That(manager.Diagnostics.ActiveInvalidProfileCount, Is.Zero);
            });
            manager.DestroyMaterial(authored);
        }

        [Test]
        public void RawEditOfAuthoredMaterial_IsReclassifiedAsActiveV1Fallback()
        {
            using var manager = new MaterialManager();
            manager.SetTransportV2Enabled(true);
            MaterialHandle handle = manager.RegisterMaterialDefinition(
                new MaterialDefinition { Name = "AuthoredThenRawEdited" });
            GPUMaterialData raw = manager.GetMaterialData(handle);
            raw.Albedo.X = 0.25f;

            manager.UpdateMaterial(handle, raw);

            Assert.Multiple(() =>
            {
                Assert.That(manager.Diagnostics.ActiveLegacyV1FallbackCount, Is.EqualTo(1));
                Assert.That(manager.Diagnostics.ActiveInvalidProfileCount, Is.EqualTo(1));
                Assert.That(
                    ((GiMaterialTransportFlags)manager.GetMaterialData(handle).TransportFlags)
                        .HasFlag(GiMaterialTransportFlags.LegacyV1Fallback),
                    Is.True);
            });
        }

        [Test]
        public void ContentRevision_IsStableForDeduplicationAndChangesWhenASlotIsReused()
        {
            using var manager = new MaterialManager();
            MaterialHandle first = manager.RegisterMaterial(CreateGpuMaterial(8, 9, 10, 11));
            uint firstRevision = manager.GetMaterialContentRevision(first.Index);

            MaterialHandle duplicate = manager.RegisterMaterial(CreateGpuMaterial(8, 9, 10, 11));
            manager.DestroyMaterial(first);
            MaterialHandle replacement = manager.RegisterMaterial(CreateGpuMaterial(12, 9, 10, 11));
            uint replacementRevision = manager.GetMaterialContentRevision(replacement.Index);

            Assert.Multiple(() =>
            {
                Assert.That(firstRevision, Is.GreaterThan(0u));
                Assert.That(duplicate, Is.EqualTo(first));
                Assert.That(manager.GetMaterialContentRevision(-1), Is.EqualTo(0u));
                Assert.That(replacement.Index, Is.EqualTo(first.Index));
                Assert.That(replacementRevision, Is.Not.EqualTo(firstRevision));
            });
        }

        [Test]
        public void SceneOverride_RejectsInvalidAlphaBeforeCopyOnWriteAndPreservesCutoffAboveOne()
        {
            using var manager = new MaterialManager();
            MaterialHandle original = manager.RegisterMaterialDefinition(
                new MaterialDefinition
                {
                    Name = "Scene alpha override",
                    AlphaMode = MaterialAlphaMode.Mask,
                    AlphaCutoff = 0.5f
                });
            var renderObject = new RenderObject { Name = "Masked card", Material = original };
            var store = new MaterialManagerSceneMaterialOverrideStore(manager);
            int materialCountBeforeInvalidEdit = manager.RegisteredMaterialCount;

            Assert.That(
                () => store.Apply(
                    renderObject,
                    new SceneMaterialOverrideDocument { AlphaCutoff = -0.01f }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => store.Apply(
                    renderObject,
                    new SceneMaterialOverrideDocument { Metallic = float.NaN }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => store.Apply(
                    renderObject,
                    new SceneMaterialOverrideDocument { ShadingModel = "future-unknown-model" }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.Multiple(() =>
            {
                Assert.That(renderObject.Material, Is.EqualTo(original));
                Assert.That(manager.RegisteredMaterialCount, Is.EqualTo(materialCountBeforeInvalidEdit));
                Assert.That(manager.GetMaterialDefinition(original).AlphaCutoff, Is.EqualTo(0.5f));
            });

            store.Apply(
                renderObject,
                new SceneMaterialOverrideDocument { AlphaCutoff = 1.25f });
            MaterialHandle edited = (MaterialHandle)renderObject.Material!;

            Assert.Multiple(() =>
            {
                Assert.That(manager.GetMaterialDefinition(edited).AlphaCutoff, Is.EqualTo(1.25f));
                Assert.That(store.Capture(renderObject)!.AlphaCutoff, Is.EqualTo(1.25f));
            });
        }

        private static GPUMaterialData CreateGpuMaterial(
            int albedoTextureIndex,
            int normalTextureIndex,
            int metallicRoughnessTextureIndex,
            int emissiveTextureIndex)
        {
            return new GPUMaterialData
            {
                Albedo = new Vector4(1f, 0.5f, 0.25f, 1f),
                Emissive = Vector4.Zero,
                NormalScaleBias = new Vector4(1f, 0f, 0f, 0f),
                MetallicRoughnessAO = new Vector4(0.2f, 0.7f, 1f, 0f),
                BaseColorOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                NormalOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                MetallicRoughnessOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                EmissiveOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                TextureRotations = Vector4.Zero,
                TextureTexCoordSets = Vector4.Zero,
                AlbedoTextureIndex = albedoTextureIndex,
                NormalTextureIndex = normalTextureIndex,
                MetallicRoughnessTextureIndex = metallicRoughnessTextureIndex,
                EmissiveTextureIndex = emissiveTextureIndex,
                FeatureFlags = 0u,
                ExtensionDataIndex = -1
            };
        }

        private static GPUMaterialExtensionData CreateDefaultExtensionData()
        {
            return new GPUMaterialExtensionData
            {
                Clearcoat = new Vector4(0f, 0f, 1f, 1f),
                SheenColor = Vector4.Zero,
                Anisotropy = Vector4.Zero,
                Transmission = new Vector4(0f, 1.5f, 0f, 0f),
                AttenuationColor = new Vector4(1f, 1f, 1f, 0f),
                Subsurface = Vector4.Zero,
                ClearcoatTextureIndex = BindlessIndex.DefaultWhiteTexture,
                ClearcoatRoughnessTextureIndex = BindlessIndex.DefaultWhiteTexture,
                ClearcoatNormalTextureIndex = BindlessIndex.DefaultNormalTexture,
                SheenColorTextureIndex = BindlessIndex.DefaultWhiteTexture,
                SheenRoughnessTextureIndex = BindlessIndex.DefaultWhiteTexture,
                AnisotropyTextureIndex = BindlessIndex.DefaultWhiteTexture,
                TransmissionTextureIndex = BindlessIndex.DefaultWhiteTexture,
                ThicknessTextureIndex = BindlessIndex.DefaultWhiteTexture,
                SubsurfaceTextureIndex = BindlessIndex.DefaultWhiteTexture
            };
        }
    }
}
