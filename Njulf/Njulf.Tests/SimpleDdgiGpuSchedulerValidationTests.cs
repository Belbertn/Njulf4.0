using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerValidationTests
{
    [Test]
    public void CpuOracleIsDeterministicAndHonorsRequestAndRayBudgets()
    {
        var candidates = new GPUSimpleDdgiSchedulerCandidate[4];
        for (int i = 0; i < candidates.Length; i++)
        {
            candidates[i] = new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = (uint)i,
                VolumeIndex = 0,
                ExpectedPhysicalGeneration = 1,
                SequenceOrdinal = (uint)i,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    i < 2 ? SimpleDdgiSchedulerWorkClass.FreshExposedVisible : SimpleDdgiSchedulerWorkClass.NearMaintenance,
                    i < 2
                        ? SimpleDdgiSchedulerTransportCategory.HardSourceRepair
                        : SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    i < 2 ? SimpleDdgiSchedulerRayTier.Full : SimpleDdgiSchedulerRayTier.Maintenance,
                    SimpleDdgiSchedulerCandidateReason.Visible),
                ActiveRayCount = i < 2 ? 8u : 2u,
                SourceRayCount = i < 2 ? 8u : 0u
            };
        }

        var policies = new[]
        {
            new SimpleDdgiCpuVolumePolicy(
                ProbeCapacity: 4,
                MinimumQuota: 0,
                PreferredMaximumQuota: 4,
                SchedulingWeight: 1,
                Active: true)
        };
        var policy = new SimpleDdgiCpuSchedulePolicy(
            RequestBudget: 3,
            PrimaryRayBudget: 16,
            SourceCohortRayBudget: 16,
            SourceLightingGeneration: 7,
            ActiveVolumeCount: 1,
            DeterministicFixedBudget: true);

        var firstQueue = new GPUSimpleDdgiProbeUpdate[4];
        var secondQueue = new GPUSimpleDdgiProbeUpdate[4];
        var firstCounts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var secondCounts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var firstAccepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var secondAccepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var firstCursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var secondCursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];

        SimpleDdgiCpuScheduleResult first = SimpleDdgiCpuScheduleModel.Schedule(
            candidates, policies, policy, firstQueue, firstCounts, firstAccepted, firstCursors);
        SimpleDdgiCpuScheduleResult second = SimpleDdgiCpuScheduleModel.Schedule(
            candidates, policies, policy, secondQueue, secondCounts, secondAccepted, secondCursors);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.IsValid, Is.True);
            Assert.That(first.AcceptedRequestCount, Is.LessThanOrEqualTo(3));
            Assert.That(first.AcceptedPrimaryRayCount, Is.LessThanOrEqualTo(16));
            Assert.That(first.AcceptedSourceRayCount, Is.LessThanOrEqualTo(16));
            Assert.That(firstQueue, Is.EqualTo(secondQueue));
            Assert.That(firstCounts, Is.EqualTo(secondCounts));
            Assert.That(firstAccepted, Is.EqualTo(secondAccepted));
            Assert.That(firstCursors, Is.EqualTo(secondCursors));
        });
    }

    [Test]
    public void CpuOracleRejectsMalformedPackedCandidatesWithoutEmittingWork()
    {
        var candidates = new[]
        {
            new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = 1,
                VolumeIndex = 0,
                ExpectedPhysicalGeneration = 1,
                WorkClassAndTransport = 0x40u,
                RayTierAndReasonFlags = 0,
                ActiveRayCount = 1
            }
        };
        var queue = new GPUSimpleDdgiProbeUpdate[2];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        SimpleDdgiCpuScheduleResult result = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            new[] { new SimpleDdgiCpuVolumePolicy(1, 0, 1, 1, true) },
            new SimpleDdgiCpuSchedulePolicy(1, 1, 1, 1, 1, true),
            queue,
            counts,
            accepted,
            cursors);

        Assert.Multiple(() =>
        {
            Assert.That(result.InvalidCandidateCount, Is.EqualTo(1));
            Assert.That(result.AcceptedRequestCount, Is.EqualTo(0));
            Assert.That(counts, Is.All.EqualTo(0));
        });
    }

    [Test]
    public void RayBucketAndIndirectMathAreStableAtBoundaries()
    {
        Span<uint> unique = stackalloc uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
        int count = SimpleDdgiIndirectDispatchMath.DeduplicateRayBuckets(
            stackalloc uint[] { 64, 128, 64, 0, 256, 128 }, unique);
        uint[] bucketResult = unique[..count].ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(bucketResult, Is.EqualTo(new uint[] { 64, 128, 256 }));
            Assert.That(SimpleDdgiIndirectDispatchMath.RayGroupCount(0, 64), Is.EqualTo(0));
            Assert.That(SimpleDdgiIndirectDispatchMath.RayGroupCount(1, 64), Is.EqualTo(1));
            Assert.That(SimpleDdgiIndirectDispatchMath.RayGroupCount(65, 64), Is.EqualTo(65));
            Assert.That(SimpleDdgiIndirectDispatchMath.RequestThreadGroupCount(65), Is.EqualTo(2));
            Assert.That(SimpleDdgiIndirectDispatchMath.ProbeWorkgroupCount(65), Is.EqualTo(65));
        });
    }

    [Test]
    public void PersistentCursorRotatesLaneOrderAndAdvancesOnlyForAdmissions()
    {
        const int candidateCount = 4;
        var candidates = new GPUSimpleDdgiSchedulerCandidate[candidateCount];
        for (int i = 0; i < candidateCount; i++)
        {
            candidates[i] = new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = (uint)i,
                VolumeIndex = 0u,
                ExpectedPhysicalGeneration = 1u,
                SequenceOrdinal = (uint)i,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.VisibleDirty,
                    SimpleDdgiSchedulerTransportCategory.HardSourceRepair),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Full,
                    SimpleDdgiSchedulerCandidateReason.Visible),
                ActiveRayCount = 1u,
                SourceRayCount = 1u
            };
        }

        var policy = new SimpleDdgiCpuSchedulePolicy(
            RequestBudget: 2,
            PrimaryRayBudget: 16,
            SourceCohortRayBudget: 16,
            SourceLightingGeneration: 3u,
            ActiveVolumeCount: 1,
            DeterministicFixedBudget: true);
        var volume = new[] { new SimpleDdgiCpuVolumePolicy(4, 0, 4, 1, true) };
        var queue = new GPUSimpleDdgiProbeUpdate[4];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        int lane = SimpleDdgiSchedulerAbi.GetLaneIndex(
            0,
            SimpleDdgiSchedulerWorkClass.VisibleDirty,
            SimpleDdgiSchedulerTransportCategory.HardSourceRepair,
            SimpleDdgiSchedulerRayTier.Full);
        cursors[lane] = 2u;

        SimpleDdgiCpuScheduleResult first = SimpleDdgiCpuScheduleModel.Schedule(
            candidates, volume, policy, queue, counts, accepted, cursors);

        Assert.Multiple(() =>
        {
            Assert.That(first.AcceptedRequestCount, Is.EqualTo(2));
            Assert.That(queue[0].ProbeIndex, Is.EqualTo(2u));
            Assert.That(queue[1].ProbeIndex, Is.EqualTo(3u));
            Assert.That(cursors[lane], Is.EqualTo(0u));
            Assert.That(accepted[lane], Is.EqualTo(2));
        });

        Array.Clear(queue);
        Array.Clear(counts);
        Array.Clear(accepted);
        SimpleDdgiCpuScheduleResult second = SimpleDdgiCpuScheduleModel.Schedule(
            candidates, volume, policy, queue, counts, accepted, cursors);
        Assert.Multiple(() =>
        {
            Assert.That(second.AcceptedRequestCount, Is.EqualTo(2));
            Assert.That(queue[0].ProbeIndex, Is.EqualTo(0u));
            Assert.That(queue[1].ProbeIndex, Is.EqualTo(1u));
            Assert.That(cursors[lane], Is.EqualTo(2u));
        });

        Array.Clear(queue);
        Array.Clear(counts);
        Array.Clear(accepted);
        uint cursorBeforeRejection = cursors[lane];
        SimpleDdgiCpuScheduleResult rejected = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            volume,
            policy with { PrimaryRayBudget = 0u },
            queue,
            counts,
            accepted,
            cursors);
        Assert.Multiple(() =>
        {
            Assert.That(rejected.AcceptedRequestCount, Is.Zero);
            Assert.That(cursors[lane], Is.EqualTo(cursorBeforeRejection));
            Assert.That(accepted[lane], Is.Zero);
        });
    }
}
