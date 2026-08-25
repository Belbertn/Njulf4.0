using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MotionVectorCameraReprojectionTests
{
    [Test]
    public void StaticReflectionOnlyScene_UsesCameraReprojection()
    {
        var scene = new SceneRenderingData();

        Assert.That(
            MotionVectorPass.ShouldUseCameraOnlyReprojection(
                SurfaceHistoryConsumer.Reflection,
                scene),
            Is.True);
    }

    [TestCase(SurfaceHistoryConsumer.TemporalAntiAliasing)]
    [TestCase(SurfaceHistoryConsumer.DirectionalCsmTemporal)]
    [TestCase(SurfaceHistoryConsumer.DirectionalRaySoft)]
    [TestCase(SurfaceHistoryConsumer.NearFieldResidual)]
    public void OtherTemporalConsumer_RequiresAuthoredMotionVectors(
        SurfaceHistoryConsumer additionalConsumer)
    {
        var scene = new SceneRenderingData();

        Assert.That(
            MotionVectorPass.ShouldUseCameraOnlyReprojection(
                SurfaceHistoryConsumer.Reflection | additionalConsumer,
                scene),
            Is.False);
    }

    [Test]
    public void DynamicGeometry_RequiresAuthoredMotionVectors()
    {
        var scene = new SceneRenderingData
        {
            AccelerationStructureDynamicBottomLevelCount = 1
        };

        Assert.That(
            MotionVectorPass.ShouldUseCameraOnlyReprojection(
                SurfaceHistoryConsumer.Reflection,
                scene),
            Is.False);
    }

    [Test]
    public void EnabledFoliageMotionWithoutSubmittedClusters_UsesCameraReprojection()
    {
        var scene = new SceneRenderingData
        {
            FoliageMotionVectorsEnabled = true,
            FoliageClusterCount = 0
        };

        Assert.That(
            MotionVectorPass.ShouldUseCameraOnlyReprojection(
                SurfaceHistoryConsumer.Reflection,
                scene),
            Is.True);
    }

    [Test]
    public void SubmittedFoliageMotion_RequiresAuthoredMotionVectors()
    {
        var scene = new SceneRenderingData
        {
            FoliageMotionVectorsEnabled = true,
            FoliageClusterCount = 1
        };

        Assert.That(
            MotionVectorPass.ShouldUseCameraOnlyReprojection(
                SurfaceHistoryConsumer.Reflection,
                scene),
            Is.False);
    }
}
