using System.Linq;
using Njulf.Core.Math;
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
        public void DefaultMaterial_UsesCanonicalBindlessDefaults()
        {
            using var manager = new MaterialManager();

            GPUMaterialData material = manager.GetMaterialData(manager.DefaultMaterialHandle);

            Assert.Multiple(() =>
            {
                Assert.That(manager.DefaultMaterialHandle.IsValid, Is.True);
                Assert.That(manager.ResolveMaterialIndex(manager.DefaultMaterialHandle), Is.EqualTo(0));
                Assert.That(material.AlbedoTextureIndex, Is.EqualTo(BindlessIndex.DefaultWhiteTexture));
                Assert.That(material.NormalTextureIndex, Is.EqualTo(BindlessIndex.DefaultNormalTexture));
                Assert.That(material.MetallicRoughnessTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
                Assert.That(material.EmissiveTextureIndex, Is.EqualTo(BindlessIndex.DefaultBlackTexture));
                Assert.That(() => MaterialManager.ValidateMaterialTextureIndices(material), Throws.Nothing);
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

            Assert.Multiple(() =>
            {
                Assert.That(snapshot, Has.Length.EqualTo(3));
                Assert.That(snapshot[firstHandle.Index], Is.EqualTo(first));
                Assert.That(snapshot[secondHandle.Index], Is.EqualTo(second));
                Assert.That(snapshot.Select(m => m.AlbedoTextureIndex), Does.Contain(BindlessIndex.DefaultWhiteTexture));
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
                TexCoordOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                AlbedoTextureIndex = albedoTextureIndex,
                NormalTextureIndex = normalTextureIndex,
                MetallicRoughnessTextureIndex = metallicRoughnessTextureIndex,
                EmissiveTextureIndex = emissiveTextureIndex
            };
        }
    }
}
