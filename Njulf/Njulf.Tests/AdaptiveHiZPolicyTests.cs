using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdaptiveHiZPolicyTests
{
    [Test]
    public void ShouldSuppress_WhenRecentCountersAreUnavailable()
    {
        var sceneData = SceneWithObjects(1024);

        bool suppress = AdaptiveHiZPolicy.ShouldSuppress(
            sceneData,
            default,
            default,
            lastHiZCostMicroseconds: 0,
            lastForwardCostMicroseconds: 0);

        Assert.That(suppress, Is.True);
    }

    [Test]
    public void ShouldSuppress_WhenRenderableObjectCountIsSmall()
    {
        var sceneData = SceneWithObjects(AdaptiveHiZPolicy.SmallRenderableObjectCount);
        var counters = new GPUVisibilityCounters
        {
            InputObjectCount = 1024,
            OpaqueMeshletCount = 4096,
            OcclusionTestedObjectCount = 2048,
            OcclusionRejectedObjectCount = 512
        };

        bool suppress = AdaptiveHiZPolicy.ShouldSuppress(
            sceneData,
            default,
            counters,
            lastHiZCostMicroseconds: 100,
            lastForwardCostMicroseconds: 2000);

        Assert.That(suppress, Is.True);
    }

    [Test]
    public void ShouldSuppress_WhenMeasuredCullRateIsTooLow()
    {
        var sceneData = SceneWithObjects(1024);
        var counters = new GPUVisibilityCounters
        {
            InputObjectCount = 1024,
            OpaqueMeshletCount = 4096,
            OcclusionTestedObjectCount = AdaptiveHiZPolicy.MinMeasuredOcclusionTests,
            OcclusionRejectedObjectCount = 4
        };

        bool suppress = AdaptiveHiZPolicy.ShouldSuppress(
            sceneData,
            default,
            counters,
            lastHiZCostMicroseconds: 0,
            lastForwardCostMicroseconds: 0);

        Assert.That(suppress, Is.True);
    }

    [Test]
    public void ShouldSuppress_WhenHiZCostExceedsEstimatedForwardBenefit()
    {
        var sceneData = SceneWithObjects(2048);
        var counters = new GPUVisibilityCounters
        {
            InputObjectCount = 2048,
            OpaqueMeshletCount = 8192,
            OcclusionTestedObjectCount = 1000,
            OcclusionRejectedObjectCount = 100
        };

        bool suppress = AdaptiveHiZPolicy.ShouldSuppress(
            sceneData,
            default,
            counters,
            lastHiZCostMicroseconds: 200,
            lastForwardCostMicroseconds: 1000);

        Assert.That(suppress, Is.True);
    }

    [Test]
    public void ShouldNotSuppress_WhenCullRateAndBenefitAreUseful()
    {
        var sceneData = SceneWithObjects(2048);
        var counters = new GPUVisibilityCounters
        {
            InputObjectCount = 2048,
            OpaqueMeshletCount = 8192,
            OcclusionTestedObjectCount = 1000,
            OcclusionRejectedObjectCount = 400
        };

        bool suppress = AdaptiveHiZPolicy.ShouldSuppress(
            sceneData,
            default,
            counters,
            lastHiZCostMicroseconds: 100,
            lastForwardCostMicroseconds: 1000);

        Assert.That(suppress, Is.False);
    }

    private static SceneRenderingData SceneWithObjects(int objectCount)
    {
        return new SceneRenderingData
        {
            ObjectCount = objectCount,
            SolidObjectCount = objectCount,
            GpuSceneObjectCount = objectCount,
            GpuSceneInstanceCount = objectCount
        };
    }
}
