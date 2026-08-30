using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ForwardVisibilityCapacityTests
{
    [Test]
    public void CornellTransition_UsesExpandedCompactedCapacity()
    {
        ForwardVisibilityCapacityPlan plan =
            ForwardVisibilityCompactionPass.ResolveCapacityPlan(
                simpleCompactedCapacity: 14,
                simpleNormalCompactedCapacity: 1,
                fullCompactedCapacity: 1,
                sidedStreams: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SimpleInputCapacity, Is.EqualTo(14));
            Assert.That(plan.DispatchCandidateCount, Is.EqualTo(14));
            Assert.That(plan.SimpleBackingElementCount, Is.EqualTo(28u));
            Assert.That(
                ForwardVisibilityCompactionPass.CapacityBackingsCoverPlan(
                    plan,
                    simpleBackingElementCount: 28u,
                    simpleNormalBackingElementCount: 2u,
                    fullBackingElementCount: 2u),
                Is.True);
        });
    }

    [Test]
    public void CornellTransition_RejectsOriginalSevenMeshletBacking()
    {
        ForwardVisibilityCapacityPlan plan =
            ForwardVisibilityCompactionPass.ResolveCapacityPlan(
                simpleCompactedCapacity: 14,
                simpleNormalCompactedCapacity: 1,
                fullCompactedCapacity: 1,
                sidedStreams: true);

        Assert.That(
            ForwardVisibilityCompactionPass.CapacityBackingsCoverPlan(
                plan,
                simpleBackingElementCount: 14u,
                simpleNormalBackingElementCount: 2u,
                fullBackingElementCount: 2u),
            Is.False);
    }

    [TestCase(true, 14u, 2u)]
    [TestCase(false, 7u, 1u)]
    public void NonTransitionCapacity_PreservesSidedLayout(
        bool sidedStreams,
        uint expectedSimpleBacking,
        uint expectedEmptyBacking)
    {
        ForwardVisibilityCapacityPlan plan =
            ForwardVisibilityCompactionPass.ResolveCapacityPlan(
                simpleCompactedCapacity: 7,
                simpleNormalCompactedCapacity: 0,
                fullCompactedCapacity: 0,
                sidedStreams);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SimpleInputCapacity, Is.EqualTo(7));
            Assert.That(plan.DispatchCandidateCount, Is.EqualTo(7));
            Assert.That(
                plan.SimpleBackingElementCount,
                Is.EqualTo(expectedSimpleBacking));
            Assert.That(
                plan.SimpleNormalBackingElementCount,
                Is.EqualTo(expectedEmptyBacking));
            Assert.That(
                plan.FullBackingElementCount,
                Is.EqualTo(expectedEmptyBacking));
        });
    }

    [Test]
    public void CompactedForwardPaths_UsePublishedCapacitiesInsteadOfBaseCounts()
    {
        var sceneData = new SceneRenderingData
        {
            SimpleOpaqueMeshletCount = 7,
            SimpleNormalOpaqueMeshletCount = 3,
            FullOpaqueMeshletCount = 2,
            SceneSubmissionGpuCompactedSimpleOpaqueCapacity = 14,
            SceneSubmissionGpuCompactedSimpleNormalOpaqueCapacity = 6,
            SceneSubmissionGpuCompactedFullOpaqueCapacity = 4,
            SceneSubmissionGpuCompactedOpaqueCapacity = 32
        };

        ForwardPlusPass.CompactedForwardCapacityPlan plan =
            ForwardPlusPass.ResolveCompactedForwardCapacityPlan(sceneData);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SimpleCapacity, Is.EqualTo(14));
            Assert.That(plan.SimpleNormalCapacity, Is.EqualTo(6));
            Assert.That(plan.FullCapacity, Is.EqualTo(4));
            Assert.That(plan.AggregateCapacity, Is.EqualTo(32));
        });
    }
}
