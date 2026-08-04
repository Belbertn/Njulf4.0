using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerPolicyTests
{
    [Test]
    public void EveryLaneRoundTripsThroughThePackedIndex()
    {
        for (int lane = 0; lane < SimpleDdgiSchedulerAbi.MaxLaneCount; lane++)
        {
            SimpleDdgiSchedulerAbi.DecodeLaneIndex(
                lane,
                out int volume,
                out SimpleDdgiSchedulerWorkClass workClass,
                out SimpleDdgiSchedulerTransportCategory transport,
                out SimpleDdgiSchedulerRayTier rayTier);
            Assert.That(
                SimpleDdgiSchedulerAbi.GetLaneIndex(volume, workClass, transport, rayTier),
                Is.EqualTo(lane));
        }
    }

    [Test]
    public void CandidateAndLifecyclePackingRejectOutOfRangeValues()
    {
        uint packedClass = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
            SimpleDdgiSchedulerWorkClass.VisibleDirty,
            SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation);
        SimpleDdgiSchedulerAbi.UnpackCandidateWorkClassAndTransport(
            packedClass,
            out SimpleDdgiSchedulerWorkClass workClass,
            out SimpleDdgiSchedulerTransportCategory transport);

        uint packedReason = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
            SimpleDdgiSchedulerRayTier.Maintenance,
            SimpleDdgiSchedulerCandidateReason.RegionalDirty |
            SimpleDdgiSchedulerCandidateReason.Visible);
        SimpleDdgiSchedulerAbi.UnpackCandidateRayTierAndReasons(
            packedReason,
            out SimpleDdgiSchedulerRayTier rayTier,
            out SimpleDdgiSchedulerCandidateReason reasons);

        Assert.Multiple(() =>
        {
            Assert.That(workClass, Is.EqualTo(SimpleDdgiSchedulerWorkClass.VisibleDirty));
            Assert.That(transport, Is.EqualTo(SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation));
            Assert.That(rayTier, Is.EqualTo(SimpleDdgiSchedulerRayTier.Maintenance));
            Assert.That(reasons, Is.EqualTo(SimpleDdgiSchedulerCandidateReason.RegionalDirty |
                SimpleDdgiSchedulerCandidateReason.Visible));
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(0, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackSchedulerProbeLifecycle(512, 0, 0, 0, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(0x00ffffffu)]
    [TestCase(0x01000000u)]
    public void GenerationPackingKeepsNonZeroPhysicalGenerations(uint generation)
    {
        if (generation == 0 || generation > SimpleDdgiSchedulerAbi.PhysicalGenerationMask)
        {
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(generation, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            return;
        }

        uint packed = SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(generation, uint.MaxValue);
        Assert.That(packed & SimpleDdgiSchedulerAbi.PhysicalGenerationMask, Is.EqualTo(generation));
        Assert.That(packed >> 24, Is.EqualTo(255u));
    }
}
