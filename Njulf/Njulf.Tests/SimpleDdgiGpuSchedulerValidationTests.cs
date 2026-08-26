using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerValidationTests
{
    [Test]
    public void RuntimeDiagnostics_ExportExactCommitRejections()
    {
        string projector = ReadRepoText(
                "Njulf.Rendering",
                "Core",
                "DdgiFrameDataProjector.cs")
            .ReplaceLineEndings("\n");

        Assert.That(
            projector,
            Does.Match(
                @"SimpleDdgiSchedulerFeedbackFailedCommitCount\s*=\s*schedulerFeedback\.FailedCommitCount;"));

        string feedbackShader = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_feedback.comp");
        Assert.That(
            feedbackShader,
            Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_COMMIT_REJECTED));"));
        Assert.That(
            feedbackShader,
            Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_COMMIT_FAILURE_OFFSET +"));

        string commitShader = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_commit_local.comp");
        Assert.Multiple(() =>
        {
            Assert.That(commitShader, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_CLASSIFICATION"));
            Assert.That(commitShader, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_GENERATION"));
            Assert.That(commitShader, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COMMIT_FAILURE_CACHE_EPOCH_OR_CARDINALITY"));
            Assert.That(commitShader, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_TRANSACTION_PREDICATE_MASK"));
            Assert.That(
                SimpleDdgiSchedulerAbi.FeedbackCommitFailureOffsetWords +
                SimpleDdgiSchedulerAbi.CommitFailureCategoryCount,
                Is.LessThanOrEqualTo(
                    SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes /
                    sizeof(uint)));
            Assert.That(
                64 + SimpleDdgiSchedulerAbi.MaxLaneCount,
                Is.LessThanOrEqualTo(
                    SimpleDdgiSchedulerAbi.FeedbackCommitFailureOffsetWords));
            Assert.That(
                SimpleDdgiSchedulerAbi.FeedbackCacheReadFailureOffsetWords,
                Is.LessThan(
                    SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes /
                    sizeof(uint)));
            Assert.That(
                SimpleDdgiSchedulerAbi.FeedbackEligibleClassOffsetWords,
                Is.EqualTo(976));
            Assert.That(
                SimpleDdgiSchedulerAbi.FeedbackEligibleRingOffsetWords,
                Is.EqualTo(
                    SimpleDdgiSchedulerAbi.FeedbackEligibleClassOffsetWords +
                    (int)SimpleDdgiSchedulerWorkClass.Count));
            Assert.That(
                SimpleDdgiSchedulerAbi.FeedbackEligibleRingOffsetWords + 3,
                Is.LessThanOrEqualTo(
                    SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes /
                    sizeof(uint)));
            Assert.That(
                SimpleDdgiGpuSchedulerLayout.CounterBytes,
                Is.EqualTo(96 * sizeof(uint)));
        });
    }

    [Test]
    public void CommitSourceCacheStride_UsesCanonicalSidecarAccounting()
    {
        string storageAbi = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_storage_abi.glsl").ReplaceLineEndings("\n");
        string commitShader = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_commit_local.comp").ReplaceLineEndings("\n");

        Assert.Multiple(() =>
        {
            Assert.That(storageAbi, Does.Contain(
                "uint SimpleDdgiStorageSidecarWordsPerRay(uint layoutFlags)"));
            Assert.That(storageAbi, Does.Contain(
                "SimpleDdgiStorageUsesRecursiveGlossySidecar(layoutFlags) ? 1u : 0u"));
            Assert.That(storageAbi, Does.Contain(
                "SimpleDdgiStorageUsesVolumePathSidecar(layoutFlags)\n" +
                "            ? SIMPLE_DDGI_STORAGE_VOLUME_PATH_SIDECAR_WORDS : 0u"));
            Assert.That(commitShader, Does.Contain(
                "TryResolveSimpleDdgiStorageStrideFromWordsPerProbe("));
            Assert.That(commitShader, Does.Not.Contain(
                "uint sidecarWordsPerRay ="));
            Assert.That(commitShader, Does.Not.Contain(
                "uint encodedWordsPerRay = cacheWordsPerProbe / raysPerProbe;"));
        });
    }

    [Test]
    public void SchedulerEligibilityEvidence_UsesBoundedExactCountersAndFeedbackTail()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_shared.glsl");
        string classify = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_classify.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_feedback.comp");
        string scheduler = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiGpuScheduler.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE_CLASS_BASE = 80u"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE_RING_BASE = 87u"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_ELIGIBLE_CLASS_OFFSET = 976u"));
            Assert.That(classify, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_ELIGIBLE_CLASS_BASE + workClass"));
            Assert.That(classify, Does.Contain(
                "min(SchedulerVolumeRing(volumeIndex), 2u)"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_ELIGIBLE_CLASS_OFFSET + workClass"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_ELIGIBLE_RING_OFFSET + ring"));
            Assert.That(scheduler, Does.Contain("_lastEligibilityEvidence = new("));
            Assert.That(scheduler,
                Does.Contain("public SimpleDdgiSchedulerEligibilityEvidence LastEligibilityEvidence"));
        });
    }

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
                    i < 2
                        ? SimpleDdgiSchedulerWorkClass.FreshExposedVisible
                        : SimpleDdgiSchedulerWorkClass.NearMaintenance,
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
    public void CpuOracle_UrgentSourceRepairBypassesRoutineCadenceBudget()
    {
        var candidates = new GPUSimpleDdgiSchedulerCandidate[3];
        for (int i = 0; i < candidates.Length; i++)
        {
            candidates[i] = new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = (uint)i,
                VolumeIndex = 0,
                ExpectedPhysicalGeneration = 1,
                SequenceOrdinal = (uint)i,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.FreshExposedVisible,
                    SimpleDdgiSchedulerTransportCategory.HardSourceRepair),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Full,
                    SimpleDdgiSchedulerCandidateReason.Fresh |
                    SimpleDdgiSchedulerCandidateReason.ScrollExposed),
                ActiveRayCount = 8u,
                SourceRayCount = 8u
            };
        }

        var queue = new GPUSimpleDdgiProbeUpdate[3];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        SimpleDdgiCpuScheduleResult result = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            new[] { new SimpleDdgiCpuVolumePolicy(3, 0, 3, 1, true) },
            new SimpleDdgiCpuSchedulePolicy(
                RequestBudget: 3,
                PrimaryRayBudget: 24,
                SourceCohortRayBudget: 8,
                SourceLightingGeneration: 1,
                ActiveVolumeCount: 1,
                DeterministicFixedBudget: true),
            queue,
            counts,
            accepted,
            cursors);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.AcceptedRequestCount, Is.EqualTo(3));
            Assert.That(result.AcceptedPrimaryRayCount, Is.EqualTo(24));
            Assert.That(result.AcceptedSourceRayCount, Is.EqualTo(24));
            Assert.That(result.SourceCohortRejectedCount, Is.Zero);
        });
    }

    [Test]
    public void CpuOracle_LightingSourceRepairRemainsCadenceLimited()
    {
        var candidates = new GPUSimpleDdgiSchedulerCandidate[3];
        for (int i = 0; i < candidates.Length; i++)
        {
            candidates[i] = new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = (uint)i,
                VolumeIndex = 0,
                ExpectedPhysicalGeneration = 1,
                SequenceOrdinal = (uint)i,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.VisibleDirty,
                    SimpleDdgiSchedulerTransportCategory.HardSourceRepair),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Full,
                    SimpleDdgiSchedulerCandidateReason.RegionalDirty |
                    SimpleDdgiSchedulerCandidateReason.Visible),
                ActiveRayCount = 8u,
                SourceRayCount = 8u
            };
        }

        var queue = new GPUSimpleDdgiProbeUpdate[3];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        SimpleDdgiCpuScheduleResult result = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            new[] { new SimpleDdgiCpuVolumePolicy(3, 0, 3, 1, true) },
            new SimpleDdgiCpuSchedulePolicy(
                RequestBudget: 3,
                PrimaryRayBudget: 24,
                SourceCohortRayBudget: 8,
                SourceLightingGeneration: 2,
                ActiveVolumeCount: 1,
                DeterministicFixedBudget: true),
            queue,
            counts,
            accepted,
            cursors);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.AcceptedRequestCount, Is.EqualTo(1));
            Assert.That(result.AcceptedPrimaryRayCount, Is.EqualTo(8));
            Assert.That(result.AcceptedSourceRayCount, Is.EqualTo(8));
            Assert.That(result.SourceCohortRejectedCount, Is.EqualTo(2));
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
    public void CachedV2UpdateCarriesFullSourceSequenceWithoutChargingPrimaryRays()
    {
        var candidates = new[]
        {
            new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = 100u,
                VolumeIndex = 0u,
                ExpectedPhysicalGeneration = 7u,
                SequenceOrdinal = 0u,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.NearMaintenance,
                    SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Maintenance,
                    SimpleDdgiSchedulerCandidateReason.ConvergencePending),
                ActiveRayCount = 16u,
                SourceRayCount = 128u
            }
        };
        var queue = new GPUSimpleDdgiProbeUpdate[1];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];

        SimpleDdgiCpuScheduleResult result = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            new[] { new SimpleDdgiCpuVolumePolicy(1, 0, 1, 1, true) },
            new SimpleDdgiCpuSchedulePolicy(1, 0, 0, 11u, 1, true),
            queue,
            counts,
            accepted,
            cursors);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.AcceptedRequestCount, Is.EqualTo(1));
            Assert.That(result.AcceptedPrimaryRayCount, Is.Zero);
            Assert.That(result.AcceptedSourceRayCount, Is.Zero);
            Assert.That(queue[0].ProbeIndex, Is.EqualTo(100u));
            Assert.That(queue[0].SourceRayCount, Is.EqualTo(128u));
            Assert.That(
                (queue[0].Flags & SimpleDdgiSchedulerAbi.UpdateRayCountMask) >>
                (int)SimpleDdgiSchedulerAbi.UpdateRayCountShift,
                Is.EqualTo(128u));
            Assert.That(
                queue[0].Flags & SimpleDdgiSchedulerAbi.UpdateSourceRefreshFlag,
                Is.Zero);
        });
    }

    [Test]
    public void CpuOracleCarriesPerProbeSourceIdentityAcrossRefreshAndCachedWork()
    {
        var candidates = new[]
        {
            new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = 1u,
                VolumeIndex = 0u,
                ExpectedPhysicalGeneration = 3u,
                SequenceOrdinal = 0u,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.FreshExposedVisible,
                    SimpleDdgiSchedulerTransportCategory.HardSourceRepair),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Full,
                    SimpleDdgiSchedulerCandidateReason.Fresh),
                ActiveRayCount = 8u,
                SourceRayCount = 8u
            },
            new GPUSimpleDdgiSchedulerCandidate
            {
                ProbeIndex = 2u,
                VolumeIndex = 0u,
                ExpectedPhysicalGeneration = 5u,
                SequenceOrdinal = 1u,
                WorkClassAndTransport = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
                    SimpleDdgiSchedulerWorkClass.NearMaintenance,
                    SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation),
                RayTierAndReasonFlags = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
                    SimpleDdgiSchedulerRayTier.Maintenance,
                    SimpleDdgiSchedulerCandidateReason.ConvergencePending),
                ActiveRayCount = 2u,
                SourceRayCount = 8u
            }
        };
        var queue = new GPUSimpleDdgiProbeUpdate[2];
        var counts = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var accepted = new int[SimpleDdgiSchedulerAbi.MaxLaneCount];
        var cursors = new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];

        SimpleDdgiCpuScheduleResult result = SimpleDdgiCpuScheduleModel.Schedule(
            candidates,
            new[] { new SimpleDdgiCpuVolumePolicy(2, 0, 2, 1, true) },
            new SimpleDdgiCpuSchedulePolicy(2, 8, 8, 101u, 1, true),
            new uint[] { 0u, 41u, 43u },
            new uint[] { 0u, 9u, 17u },
            queue,
            counts,
            accepted,
            cursors);

        GPUSimpleDdgiProbeUpdate refreshed = queue[0].ProbeIndex == 1u ? queue[0] : queue[1];
        GPUSimpleDdgiProbeUpdate cached = queue[0].ProbeIndex == 2u ? queue[0] : queue[1];
        Assert.Multiple(() =>
        {
            Assert.That(result.AcceptedRequestCount, Is.EqualTo(2));
            Assert.That(refreshed.ProbeIndex, Is.EqualTo(1u));
            Assert.That(refreshed.SourceLightingGeneration, Is.EqualTo(101u));
            Assert.That(refreshed.SourceEpoch, Is.EqualTo(10u));
            Assert.That(cached.ProbeIndex, Is.EqualTo(2u));
            Assert.That(cached.SourceLightingGeneration, Is.EqualTo(43u));
            Assert.That(cached.SourceEpoch, Is.EqualTo(17u));
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

    [Test]
    public void FallbackStateExportRejectsPartialOrGenerationlessProbeRecords()
    {
        var scheduler = new GPUSimpleDdgiSchedulerProbeState
        {
            CommittedSourceLightingGeneration = 7u,
            SourceEpoch = 11u,
            OwningTransportTopologyGeneration = 13u,
            CacheProbeBaseWordPlusOne = 1u,
            PackedTransportAndLifecycle =
                SimpleDdgiSchedulerAbi.PackSchedulerProbeLifecycle(
                    64u,
                    3u,
                    2u,
                    0u,
                    0u)
        };
        var state = new GPUSimpleDdgiProbeState
        {
            RelocationAndActive = new Njulf.Core.Math.Vector4(
                0.1f,
                0.0f,
                -0.1f,
                1.0f),
            Flags = 5u << 8,
            Classification = 0u
        };
        GPUSimpleDdgiSchedulerProbeState zeroEpoch = scheduler;
        zeroEpoch.SourceEpoch = 0u;
        GPUSimpleDdgiSchedulerProbeState partialTransaction = scheduler;
        partialTransaction.PackedTransportAndLifecycle =
            SimpleDdgiSchedulerAbi.PackSchedulerProbeLifecycle(
                64u,
                3u,
                2u,
                0u,
                1u);
        GPUSimpleDdgiProbeState zeroGeneration = state;
        zeroGeneration.Flags = 0u;
        GPUSimpleDdgiSchedulerProbeState zeroCacheBase = scheduler;
        zeroCacheBase.CacheProbeBaseWordPlusOne = 0u;

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.IsValidGpuSchedulerFallbackRecord(
                    scheduler,
                    state,
                    13u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsValidGpuSchedulerFallbackRecord(
                    zeroEpoch,
                    state,
                    13u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.IsValidGpuSchedulerFallbackRecord(
                    partialTransaction,
                    state,
                    13u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.IsValidGpuSchedulerFallbackRecord(
                    scheduler,
                    zeroGeneration,
                    13u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.IsValidGpuSchedulerFallbackRecord(
                    zeroCacheBase,
                    state,
                    13u),
                Is.False);
        });
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }
}