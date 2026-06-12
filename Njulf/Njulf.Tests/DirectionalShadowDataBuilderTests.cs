using System;
using Njulf.Core.Camera;
using Njulf.Rendering.Data;
using NUnit.Framework;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalShadowDataBuilderTests
{
    [Test]
    public void CalculateCascadeSplits_AreMonotonicAndEndAtFarPlane()
    {
        float[] splits = DirectionalShadowDataBuilder.CalculateCascadeSplits(0.1f, 80f, 4);

        Assert.Multiple(() =>
        {
            Assert.That(splits[0], Is.GreaterThan(0.1f));
            Assert.That(splits[1], Is.GreaterThan(splits[0]));
            Assert.That(splits[2], Is.GreaterThan(splits[1]));
            Assert.That(splits[3], Is.EqualTo(80f).Within(0.0001f));
        });
    }

    [Test]
    public void Build_ProducesFiniteMatricesAndExpectedIndices()
    {
        var camera = CreateCamera();
        var settings = new ShadowSettings
        {
            DirectionalCascadeCount = 3,
            DirectionalShadowMapSize = 1024
        };

        GPUShadowData data = DirectionalShadowDataBuilder.Build(
            camera,
            new NumericsVector3(0.2f, -1f, -0.3f),
            settings,
            selectedLightIndex: 2);

        Assert.Multiple(() =>
        {
            AssertMatrixFinite(data.LightViewProjection0);
            AssertMatrixFinite(data.LightViewProjection1);
            AssertMatrixFinite(data.LightViewProjection2);
            Assert.That(data.Indices.X, Is.EqualTo(1f));
            Assert.That(data.Indices.Y, Is.EqualTo(3f));
            Assert.That(data.Indices.W, Is.EqualTo(2f));
            Assert.That(data.Settings.Z, Is.EqualTo(1024f));
        });
    }

    [Test]
    public void ShadowSettings_ClampToSupportedRanges()
    {
        var settings = new ShadowSettings
        {
            DirectionalShadowMapSize = 300,
            DirectionalCascadeCount = 99,
            MaxShadowDistance = -1f,
            NormalBias = 2f,
            SlopeScaledDepthBias = 99f,
            ConstantDepthBias = 1f,
            PcfRadius = 99
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.DirectionalShadowMapSize, Is.EqualTo(512));
            Assert.That(settings.DirectionalCascadeCount, Is.EqualTo(ShadowSettings.MaxDirectionalCascades));
            Assert.That(settings.MaxShadowDistance, Is.EqualTo(1f));
            Assert.That(settings.NormalBias, Is.EqualTo(1f));
            Assert.That(settings.SlopeScaledDepthBias, Is.EqualTo(16f));
            Assert.That(settings.ConstantDepthBias, Is.EqualTo(0.1f));
            Assert.That(settings.PcfRadius, Is.EqualTo(3));
        });
    }

    private static FirstPersonCamera CreateCamera()
    {
        var camera = new FirstPersonCamera(new CoreVector3(0f, 1.5f, 5f), yaw: 0.2f, pitch: -0.1f)
        {
            FieldOfView = MathF.PI / 3f,
            AspectRatio = 16f / 9f,
            NearPlane = 0.05f,
            FarPlane = 250f
        };
        camera.Update();
        return camera;
    }

    private static void AssertMatrixFinite(CoreMatrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
                Assert.That(float.IsFinite(matrix[row, column]), Is.True, $"matrix[{row},{column}]");
        }
    }
}
