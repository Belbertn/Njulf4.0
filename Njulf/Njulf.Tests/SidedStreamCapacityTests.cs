using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
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

    [Test]
    public void AsymmetricLayout_UsesExactPerSideDitherBounds()
    {
        SidedStreamCapacityPlan plan =
            SceneOpaqueCompactionPass.ResolveSidedStreamCapacityPlan(
                candidateCount: 120,
                doubleSidedCandidateCount: 12,
                maximumEmissionMultiplier: 2,
                sidedStreams: true,
                asymmetricRequested: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.OneSidedCapacity, Is.EqualTo(216));
            Assert.That(plan.DoubleSidedBase, Is.EqualTo(216));
            Assert.That(plan.DoubleSidedCapacity, Is.EqualTo(24));
            Assert.That(plan.RequiredBackingElements, Is.EqualTo(240u));
            Assert.That(plan.Asymmetric, Is.True);
            Assert.That(plan.HasNonOverlappingRanges, Is.True);
        });
    }

    [TestCase(8192, 0, 2)]
    [TestCase(8192, 4096, 2)]
    [TestCase(8192, 8192, 2)]
    [TestCase(1, 1, 2)]
    public void StressLayouts_NeverOverlapOrOverflowBacking(
        int candidateCount,
        int doubleSidedCandidateCount,
        int emissionMultiplier)
    {
        SidedStreamCapacityPlan plan =
            SceneOpaqueCompactionPass.ResolveSidedStreamCapacityPlan(
                candidateCount,
                doubleSidedCandidateCount,
                emissionMultiplier,
                sidedStreams: true,
                asymmetricRequested: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.HasNonOverlappingRanges, Is.True);
            Assert.That(
                (ulong)plan.DoubleSidedBase +
                (ulong)plan.DoubleSidedCapacity,
                Is.LessThanOrEqualTo(plan.RequiredBackingElements));
            Assert.That(
                plan.TotalLogicalCapacity,
                Is.EqualTo(candidateCount * emissionMultiplier));
        });
    }

    [TestCase(-1)]
    [TestCase(121)]
    public void InvalidExactCount_FallsBackToSymmetricLayout(
        int doubleSidedCandidateCount)
    {
        SidedStreamCapacityPlan plan =
            SceneOpaqueCompactionPass.ResolveSidedStreamCapacityPlan(
                candidateCount: 120,
                doubleSidedCandidateCount,
                maximumEmissionMultiplier: 2,
                sidedStreams: true,
                asymmetricRequested: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.OneSidedCapacity, Is.EqualTo(240));
            Assert.That(plan.DoubleSidedBase, Is.EqualTo(240));
            Assert.That(plan.DoubleSidedCapacity, Is.EqualTo(240));
            Assert.That(plan.RequiredBackingElements, Is.EqualTo(480u));
            Assert.That(plan.Asymmetric, Is.False);
        });
    }

    [Test]
    public void MissingBuilderCounts_RejectsAsymmetricAdmission()
    {
        var sceneData = new Njulf.Rendering.Data.SceneRenderingData
        {
            SimpleOpaqueMeshletCount = 4,
            DoubleSidedSimpleOpaqueMeshletCount = 1,
            SidedStreamCandidateCountsValid = false
        };

        Assert.That(
            SceneOpaqueCompactionPass.SidedStreamCountsAreValid(
                sceneData,
                solidDepthCandidateCount: 0,
                maskedDepthCandidateCount: 0,
                directionalStaticShadowCandidateCount: 0,
                directionalDynamicShadowCandidateCount: 0),
            Is.False);
    }

    [Test]
    public void CompactionLayout_RemainsWithinPortablePushConstantBudget()
    {
        Assert.That(
            Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
            Is.LessThanOrEqualTo(256));
    }
}
