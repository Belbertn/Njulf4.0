using Njulf.Rendering;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ForwardClusterGridTests
{
    [Test]
    public void DepthSlices_AreLogarithmicAndClamped()
    {
        uint previous = 0;
        for (int sample = 0; sample <= 1000; sample++)
        {
            float t = sample / 1000.0f;
            float depth = RenderingConstants.ForwardClusterNearPlane *
                MathF.Pow(
                    RenderingConstants.ForwardClusterFarPlane /
                    RenderingConstants.ForwardClusterNearPlane,
                    t);
            uint slice = RenderingConstants
                .CalculateForwardClusterDepthSlice(depth);
            Assert.That(slice, Is.GreaterThanOrEqualTo(previous));
            previous = slice;
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                RenderingConstants.CalculateForwardClusterDepthSlice(-1f),
                Is.Zero);
            Assert.That(
                RenderingConstants.CalculateForwardClusterDepthSlice(
                    float.MaxValue),
                Is.EqualTo(
                    RenderingConstants.ForwardClusterDepthSliceCount - 1));
        });
    }

    [Test]
    public void ClusterIndices_AreDenseAcrossDepthSlices()
    {
        const uint tileCountX = 120;
        const uint tileCountY = 68;
        uint clusterCount = RenderingConstants.CalculateForwardClusterCount(
            tileCountX,
            tileCountY);
        uint last = RenderingConstants.CalculateForwardClusterIndex(
            tileCountX - 1,
            tileCountY - 1,
            RenderingConstants.ForwardClusterDepthSliceCount - 1,
            tileCountX,
            tileCountY);

        Assert.Multiple(() =>
        {
            Assert.That(last + 1, Is.EqualTo(clusterCount));
            Assert.That(
                clusterCount,
                Is.EqualTo(
                    tileCountX * tileCountY *
                    RenderingConstants.ForwardClusterDepthSliceCount));
        });
    }
}
