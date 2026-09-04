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

        [TestCase(3.99f, 4.0f, 10.0f, 0)]
        [TestCase(4.0f, 4.0f, 10.0f, 1)]
        [TestCase(9.99f, 4.0f, 10.0f, 1)]
        [TestCase(10.0f, 4.0f, 10.0f, 2)]
        [TestCase(11.99f, 12.0f, 32.0f, 0)]
        [TestCase(12.0f, 12.0f, 32.0f, 1)]
        [TestCase(32.0f, 12.0f, 32.0f, 2)]
        public void SelectMeshletLodLevel_UsesConfiguredDistanceRatios(
            float distanceRatio,
            float lod1DistanceRatio,
            float lod2DistanceRatio,
            int expectedLod)
        {
            int lod = SceneDataBuilder.SelectMeshletLodLevel(
                distanceRatio,
                previousLodLevel: -1,
                hysteresisFraction: 0.0f,
                lod1DistanceRatio: lod1DistanceRatio,
                lod2DistanceRatio: lod2DistanceRatio);

            Assert.That(lod, Is.EqualTo(expectedLod));
        }

        [Test]
        public void SelectMeshletLodLevel_UsesConfiguredRatiosForHysteresis()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    SceneDataBuilder.SelectMeshletLodLevel(
                        4.5f,
                        previousLodLevel: 0,
                        hysteresisFraction: 0.15f,
                        lod1DistanceRatio: 4.0f,
                        lod2DistanceRatio: 10.0f),
                    Is.EqualTo(0));
                Assert.That(
                    SceneDataBuilder.SelectMeshletLodLevel(
                        4.7f,
                        previousLodLevel: 0,
                        hysteresisFraction: 0.15f,
                        lod1DistanceRatio: 4.0f,
                        lod2DistanceRatio: 10.0f),
                    Is.EqualTo(1));
                Assert.That(
                    SceneDataBuilder.SelectMeshletLodLevel(
                        29.0f,
                        previousLodLevel: 2,
                        hysteresisFraction: 0.15f,
                        lod1DistanceRatio: 12.0f,
                        lod2DistanceRatio: 32.0f),
                    Is.EqualTo(2));
                Assert.That(
                    SceneDataBuilder.SelectMeshletLodLevel(
                        26.0f,
                        previousLodLevel: 2,
                        hysteresisFraction: 0.15f,
                        lod1DistanceRatio: 12.0f,
                        lod2DistanceRatio: 32.0f),
                    Is.EqualTo(1));
            });
        }

        [Test]
        public void SelectMeshletLodLevel_NormalizesDirectThresholdInputs()
        {
            int lod = SceneDataBuilder.SelectMeshletLodLevel(
                10.0f,
                previousLodLevel: -1,
                hysteresisFraction: 0.0f,
                lod1DistanceRatio: float.NaN,
                lod2DistanceRatio: float.PositiveInfinity);

            Assert.That(lod, Is.EqualTo(2));
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

        [TestCase(
            MaterialForwardClass.SimpleOpaque,
            false,
            MaterialRenderMode.Opaque,
            GPUSceneInstanceClassification.SimpleOpaque)]
        [TestCase(
            MaterialForwardClass.SimpleOpaque,
            true,
            MaterialRenderMode.Opaque,
            GPUSceneInstanceClassification.SimpleNormalOpaque)]
        [TestCase(
            MaterialForwardClass.SimpleOpaqueNormal,
            false,
            MaterialRenderMode.Opaque,
            GPUSceneInstanceClassification.SimpleNormalOpaque)]
        [TestCase(
            MaterialForwardClass.FullOpaque,
            false,
            MaterialRenderMode.Opaque,
            GPUSceneInstanceClassification.FullOpaque)]
        [TestCase(
            MaterialForwardClass.SimpleOpaqueNormal,
            false,
            MaterialRenderMode.Mask,
            GPUSceneInstanceClassification.SimpleNormalOpaque |
            GPUSceneInstanceClassification.Masked)]
        [TestCase(
            MaterialForwardClass.FullOpaque,
            false,
            MaterialRenderMode.Mask,
            GPUSceneInstanceClassification.FullOpaque |
            GPUSceneInstanceClassification.Masked)]
        public void ClassifyGpuInstanceCandidate_MatchesForwardAndDepthBuckets(
            MaterialForwardClass forwardClass,
            bool hasVertexColor,
            MaterialRenderMode renderMode,
            GPUSceneInstanceClassification expected)
        {
            Assert.That(
                SceneDataBuilder.ClassifyGpuInstanceCandidate(
                    forwardClass,
                    hasVertexColor,
                    renderMode),
                Is.EqualTo(expected));
        }

        [Test]
        public void OpaqueMaterialComplexity_RoutesBothSubmissionPaths()
        {
            GPUMaterialData plain = CreateGpuMaterial(
                BindlessIndex.DefaultWhiteTexture,
                BindlessIndex.DefaultNormalTexture,
                BindlessIndex.DefaultBlackTexture,
                BindlessIndex.DefaultBlackTexture);
            GPUMaterialData normalMapped = plain;
            normalMapped.NormalTextureIndex =
                BindlessIndex.FirstDynamicTextureIndex;
            GPUMaterialData transformed = plain;
            transformed.BaseColorOffsetScale =
                new Vector4(0.1f, 0f, 1f, 1f);
            GPUMaterialData secondaryUv = plain;
            secondaryUv.TextureTexCoordSets =
                new Vector4(1f, 0f, 0f, 0f);
            GPUMaterialData masked = plain;
            masked.NormalScaleBias = new Vector4(1f, 1f, 0f, 0f);
            GPUMaterialData extensionMasked = masked;
            extensionMasked.FeatureFlags =
                (uint)MaterialFeatureFlags.Clearcoat;
            extensionMasked.ExtensionDataIndex = 0;

            AssertForwardRouting(
                "plain",
                plain,
                hasVertexColor: false,
                MaterialForwardClass.SimpleOpaque,
                masked: false);
            AssertForwardRouting(
                "normal",
                normalMapped,
                hasVertexColor: false,
                MaterialForwardClass.SimpleOpaqueNormal,
                masked: false);
            AssertForwardRouting(
                "transform",
                transformed,
                hasVertexColor: false,
                MaterialForwardClass.SimpleOpaqueNormal,
                masked: false);
            AssertForwardRouting(
                "secondary UV",
                secondaryUv,
                hasVertexColor: false,
                MaterialForwardClass.SimpleOpaqueNormal,
                masked: false);
            AssertForwardRouting(
                "vertex color",
                plain,
                hasVertexColor: true,
                MaterialForwardClass.SimpleOpaqueNormal,
                masked: false);
            AssertForwardRouting(
                "extension-free mask",
                masked,
                hasVertexColor: false,
                MaterialForwardClass.SimpleOpaqueNormal,
                masked: true);
            AssertForwardRouting(
                "extension mask",
                extensionMasked,
                hasVertexColor: false,
                MaterialForwardClass.FullOpaque,
                masked: true);
        }

        [Test]
        public void ClassifyGpuInstanceCandidate_EncodesDirectionalShadowOwnership()
        {
            GPUSceneInstanceClassification staticCaster =
                SceneDataBuilder.ClassifyGpuInstanceCandidate(
                    MaterialForwardClass.SimpleOpaque,
                    hasVertexColor: false,
                    MaterialRenderMode.Opaque,
                    castsDirectionalShadow: true,
                    dynamicDirectionalShadow: false);
            GPUSceneInstanceClassification dynamicCaster =
                SceneDataBuilder.ClassifyGpuInstanceCandidate(
                    MaterialForwardClass.Masked,
                    hasVertexColor: false,
                    MaterialRenderMode.Mask,
                    castsDirectionalShadow: true,
                    dynamicDirectionalShadow: true);

            Assert.Multiple(() =>
            {
                Assert.That(
                    staticCaster.HasFlag(GPUSceneInstanceClassification
                        .CastsDirectionalShadow),
                    Is.True);
                Assert.That(
                    staticCaster.HasFlag(GPUSceneInstanceClassification
                        .DynamicDirectionalShadow),
                    Is.False);
                Assert.That(
                    dynamicCaster.HasFlag(GPUSceneInstanceClassification
                        .CastsDirectionalShadow),
                    Is.True);
                Assert.That(
                    dynamicCaster.HasFlag(GPUSceneInstanceClassification
                        .DynamicDirectionalShadow),
                    Is.True);
                Assert.That(
                    dynamicCaster.HasFlag(GPUSceneInstanceClassification
                        .Masked),
                    Is.True);
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

        private static void AssertForwardRouting(
            string name,
            GPUMaterialData material,
            bool hasVertexColor,
            MaterialForwardClass expectedBucket,
            bool masked)
        {
            MaterialRenderMetadata metadata =
                MaterialRenderMetadata.FromGpuMaterial(material);
            MaterialForwardClass materialClass =
                MaterialForwardClassifier.Classify(material, metadata);
            MaterialForwardClass cpuBucket =
                SceneDataBuilder.ResolveOpaqueForwardBucket(
                    materialClass,
                    hasVertexColor);
            GPUSceneInstanceClassification gpuClassification =
                SceneDataBuilder.ClassifyGpuInstanceCandidate(
                    materialClass,
                    hasVertexColor,
                    metadata.RenderMode);
            GPUSceneInstanceClassification expectedGpuBucket =
                expectedBucket switch
                {
                    MaterialForwardClass.SimpleOpaque =>
                        GPUSceneInstanceClassification.SimpleOpaque,
                    MaterialForwardClass.SimpleOpaqueNormal =>
                        GPUSceneInstanceClassification.SimpleNormalOpaque,
                    _ => GPUSceneInstanceClassification.FullOpaque
                };

            Assert.Multiple(() =>
            {
                Assert.That(cpuBucket, Is.EqualTo(expectedBucket), name);
                Assert.That(
                    gpuClassification &
                    GPUSceneInstanceClassification.ForwardBucketMask,
                    Is.EqualTo(expectedGpuBucket),
                    name);
                Assert.That(
                    gpuClassification.HasFlag(
                        GPUSceneInstanceClassification.Masked),
                    Is.EqualTo(masked),
                    name);
            });
        }
    }
}
