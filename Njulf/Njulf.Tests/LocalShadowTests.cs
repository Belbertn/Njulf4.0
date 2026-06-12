using System;
using System.Numerics;
using Njulf.Core.Camera;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class LocalShadowTests
    {
        [Test]
        public void LocalShadowSettings_ClampToSupportedRanges()
        {
            var settings = new ShadowSettings
            {
                MaxShadowedSpotLights = 99,
                SpotShadowAtlasSize = 9000,
                SpotShadowTileSize = 64,
                SpotNormalBias = -1f,
                SpotConstantDepthBias = 1f,
                SpotSlopeScaledDepthBias = 99f,
                SpotPcfRadius = 99,
                MaxShadowedPointLights = 99,
                PointShadowMapSize = 7,
                PointNormalBias = -1f,
                PointConstantDepthBias = 1f,
                PointSlopeScaledDepthBias = 99f,
                PointPcfRadius = 99
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.MaxShadowedSpotLights, Is.EqualTo(32));
                Assert.That(settings.SpotShadowAtlasSize, Is.EqualTo(8192));
                Assert.That(settings.SpotShadowTileSize, Is.EqualTo(128));
                Assert.That(settings.SpotNormalBias, Is.EqualTo(0f));
                Assert.That(settings.SpotConstantDepthBias, Is.EqualTo(0.1f));
                Assert.That(settings.SpotSlopeScaledDepthBias, Is.EqualTo(16f));
                Assert.That(settings.SpotPcfRadius, Is.EqualTo(3));
                Assert.That(settings.MaxShadowedPointLights, Is.EqualTo(4));
                Assert.That(settings.PointShadowMapSize, Is.EqualTo(128));
                Assert.That(settings.PointNormalBias, Is.EqualTo(0f));
                Assert.That(settings.PointConstantDepthBias, Is.EqualTo(0.1f));
                Assert.That(settings.PointSlopeScaledDepthBias, Is.EqualTo(16f));
                Assert.That(settings.PointPcfRadius, Is.EqualTo(3));
            });
        }

        [Test]
        public void SpotAtlasCapacityAndTileRects_AreStable()
        {
            Assert.That(LocalShadowAllocator.CalculateSpotAtlasCapacity(2048, 512), Is.EqualTo(16));

            SpotShadowAtlasRect rect = LocalShadowAllocator.GetSpotTileRect(2048, 512, 5);
            Assert.Multiple(() =>
            {
                Assert.That(rect.X, Is.EqualTo(512));
                Assert.That(rect.Y, Is.EqualTo(512));
                Assert.That(rect.Width, Is.EqualTo(512));
                Assert.That(rect.Height, Is.EqualTo(512));
            });
        }

        [Test]
        public void Selector_EnforcesBudgetsAndPrefersPriority()
        {
            var camera = CreateCamera();
            var settings = new ShadowSettings { MaxShadowedSpotLights = 1, MaxShadowedPointLights = 1 };
            Light[] lights =
            [
                Spot(priority: 0, x: 0f),
                Spot(priority: 10, x: 1f),
                Point(priority: 0, x: 2f),
                Point(priority: 20, x: 3f)
            ];

            LocalShadowSelection selection = new LocalShadowSelector().Select(lights, camera, settings);

            Assert.Multiple(() =>
            {
                Assert.That(selection.SpotCandidateCount, Is.EqualTo(2));
                Assert.That(selection.PointCandidateCount, Is.EqualTo(2));
                Assert.That(selection.SpotLights, Has.Length.EqualTo(1));
                Assert.That(selection.PointLights, Has.Length.EqualTo(1));
                Assert.That(selection.SpotLights[0].LightIndex, Is.EqualTo(1));
                Assert.That(selection.PointLights[0].LightIndex, Is.EqualTo(3));
                Assert.That(selection.SpotRejectedByBudgetCount, Is.EqualTo(1));
                Assert.That(selection.PointRejectedByBudgetCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void ShadowIndexMap_MapsOnlySelectedLights()
        {
            SelectedLocalShadow[] spots = [new(2, Spot(priority: 0, x: 0f), 1f)];
            SelectedLocalShadow[] points = [new(4, Point(priority: 0, x: 0f), 1f)];

            GPULocalLightShadowIndex[] map = LocalShadowDataBuilder.BuildShadowIndexMap(5, spots, points);

            Assert.Multiple(() =>
            {
                Assert.That(map[0].SpotShadowIndex, Is.EqualTo(-1));
                Assert.That(map[2].SpotShadowIndex, Is.EqualTo(0));
                Assert.That(map[4].PointShadowIndex, Is.EqualTo(0));
            });
        }

        [Test]
        public void LocalShadowMatrices_AreFinite()
        {
            GPUSpotShadow[] spotShadows = LocalShadowDataBuilder.BuildSpotShadows(
                [new SelectedLocalShadow(0, Spot(priority: 0, x: 0f), 1f)],
                new ShadowSettings());
            GPUPointShadow[] pointShadows = LocalShadowDataBuilder.BuildPointShadows(
                [new SelectedLocalShadow(0, Point(priority: 0, x: 0f), 1f)],
                new ShadowSettings());

            AssertMatrixFinite(spotShadows[0].LightViewProjection);
            AssertMatrixFinite(pointShadows[0].FaceViewProjection0);
            AssertMatrixFinite(pointShadows[0].FaceViewProjection5);
        }

        private static FirstPersonCamera CreateCamera()
        {
            var camera = new FirstPersonCamera(new CoreVector3(0f, 0f, 8f), 0f, 0f);
            camera.Update();
            return camera;
        }

        private static Light Spot(int priority, float x)
        {
            return new Light
            {
                Type = LightType.Spot,
                Position = new Vector3(x, 2f, 0f),
                Direction = new Vector3(0f, -1f, 0f),
                Color = Vector3.One,
                Intensity = 10f,
                Range = 8f,
                SpotAngle = MathF.PI / 5f,
                CastsShadows = true,
                ShadowPriority = priority
            };
        }

        private static Light Point(int priority, float x)
        {
            return new Light
            {
                Type = LightType.Point,
                Position = new Vector3(x, 2f, 0f),
                Color = Vector3.One,
                Intensity = 10f,
                Range = 8f,
                CastsShadows = true,
                ShadowPriority = priority
            };
        }

        private static void AssertMatrixFinite(Njulf.Core.Math.Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                    Assert.That(float.IsFinite(matrix[row, col]), Is.True, $"Matrix element [{row},{col}] must be finite.");
            }
        }
    }
}
