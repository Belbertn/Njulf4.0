using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SidedStreamCapacityTests
{
    [Test]
    public void LodTransitionExpansion_PreservesPublishedSecondRangeOffset()
    {
        int drawCapacity =
            SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                candidateCount: 120,
                publishedCapacity: 240,
                sidedStreams: true);

        Assert.That(drawCapacity, Is.EqualTo(240));
    }

    [Test]
    public void UnspecializedStream_RemainsDenseAtCandidateCount()
    {
        int drawCapacity =
            SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                candidateCount: 120,
                publishedCapacity: 256,
                sidedStreams: false);

        Assert.That(drawCapacity, Is.EqualTo(120));
    }

    [TestCase(-1, 32, true, 0)]
    [TestCase(32, -1, true, 0)]
    [TestCase(-1, -1, false, 0)]
    public void InvalidCounts_AreClampedBeforeCapacityResolution(
        int candidateCount,
        int publishedCapacity,
        bool sidedStreams,
        int expected)
    {
        Assert.That(
            SceneOpaqueCompactionPass.ResolveCompactedDrawStreamCapacity(
                candidateCount,
                publishedCapacity,
                sidedStreams),
            Is.EqualTo(expected));
    }
}
