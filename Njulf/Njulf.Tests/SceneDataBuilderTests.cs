using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class SceneDataBuilderTests
    {
        [Test]
        public void ExtractFrustum_VisibleBoxInFrontOfCamera_Intersects()
        {
            Matrix4x4 viewProjection = Matrix4x4.Identity *
                                       Matrix4x4.CreatePerspectiveFieldOfView(
                                           (float)System.Math.PI / 2f,
                                           1f,
                                           0.1f,
                                           10f);

            Frustum frustum = SceneDataBuilder.ExtractFrustum(viewProjection);
            var bounds = new BoundingBox(
                new Vector3(-0.5f, -0.5f, -2f),
                new Vector3(0.5f, 0.5f, -1f));

            Assert.That(SceneDataBuilder.IntersectsFrustum(bounds, frustum), Is.True);
        }

        [Test]
        public void ExtractFrustum_BoxBehindCamera_IsCulled()
        {
            Matrix4x4 viewProjection = Matrix4x4.Identity *
                                       Matrix4x4.CreatePerspectiveFieldOfView(
                                           (float)System.Math.PI / 2f,
                                           1f,
                                           0.1f,
                                           10f);

            Frustum frustum = SceneDataBuilder.ExtractFrustum(viewProjection);
            var bounds = new BoundingBox(
                new Vector3(-0.5f, -0.5f, 1f),
                new Vector3(0.5f, 0.5f, 2f));

            Assert.That(SceneDataBuilder.IntersectsFrustum(bounds, frustum), Is.False);
        }

        [Test]
        public void ExtractFrustum_BoxPastFarPlane_IsCulled()
        {
            Matrix4x4 viewProjection = Matrix4x4.Identity *
                                       Matrix4x4.CreatePerspectiveFieldOfView(
                                           (float)System.Math.PI / 2f,
                                           1f,
                                           0.1f,
                                           10f);

            Frustum frustum = SceneDataBuilder.ExtractFrustum(viewProjection);
            var bounds = new BoundingBox(
                new Vector3(-0.5f, -0.5f, -20f),
                new Vector3(0.5f, 0.5f, -15f));

            Assert.That(SceneDataBuilder.IntersectsFrustum(bounds, frustum), Is.False);
        }

        [Test]
        public void TransformBoundingBox_UsesEngineRowVectorConvention()
        {
            var localBounds = new BoundingBox(
                new Vector3(-1f, -2f, -3f),
                new Vector3(1f, 2f, 3f));

            BoundingBox transformed = SceneDataBuilder.TransformBoundingBox(
                localBounds,
                Matrix4x4.CreateTranslation(new Vector3(10f, 20f, -5f)));

            Assert.That(transformed.Min, Is.EqualTo(new Vector3(9f, 18f, -8f)));
            Assert.That(transformed.Max, Is.EqualTo(new Vector3(11f, 22f, -2f)));
        }

        [Test]
        public void ResolveRenderObjectMaterialHandle_NullAndZeroUseDefaultHandle()
        {
            var defaultHandle = new MaterialHandle(0, 1);

            Assert.Multiple(() =>
            {
                Assert.That(SceneDataBuilder.ResolveRenderObjectMaterialHandle(null, defaultHandle, "object"), Is.EqualTo(defaultHandle));
                Assert.That(SceneDataBuilder.ResolveRenderObjectMaterialHandle(0, defaultHandle, "object"), Is.EqualTo(defaultHandle));
            });
        }

        [Test]
        public void ResolveRenderObjectMaterialHandle_MaterialHandlePassesThrough()
        {
            var defaultHandle = new MaterialHandle(0, 1);
            var materialHandle = new MaterialHandle(4, 2);

            MaterialHandle resolved = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                materialHandle,
                defaultHandle,
                "object");

            Assert.That(resolved, Is.EqualTo(materialHandle));
        }

        [Test]
        public void ResolveRenderObjectMaterialHandle_RejectsRawGpuMaterialsAndNonZeroIndices()
        {
            var defaultHandle = new MaterialHandle(0, 1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    () => SceneDataBuilder.ResolveRenderObjectMaterialHandle(CreateGpuMaterial(8, 9, 10, 11), defaultHandle, "raw"),
                    Throws.InvalidOperationException.With.Message.Contains("unsupported material type"));
                Assert.That(
                    () => SceneDataBuilder.ResolveRenderObjectMaterialHandle(7, defaultHandle, "index"),
                    Throws.InvalidOperationException.With.Message.Contains("unsupported material type"));
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
    }
}
