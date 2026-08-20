using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTransientFrameEvidenceTests
{
    private const SimpleDdgiGpuPassMask CompletePassMask =
        SimpleDdgiGpuPassMask.FoliageProxyGeneration |
        SimpleDdgiGpuPassMask.PageDemand |
        SimpleDdgiGpuPassMask.PageResidency |
        SimpleDdgiGpuPassMask.PageFeedback |
        SimpleDdgiGpuPassMask.Schedule |
        SimpleDdgiGpuPassMask.Trace |
        SimpleDdgiGpuPassMask.DirectionalRadiance |
        SimpleDdgiGpuPassMask.AcceleratedSolve |
        SimpleDdgiGpuPassMask.Transport |
        SimpleDdgiGpuPassMask.Blend |
        SimpleDdgiGpuPassMask.TransportAudit |
        SimpleDdgiGpuPassMask.RelocateClassify |
        SimpleDdgiGpuPassMask.Publish |
        SimpleDdgiGpuPassMask.SchedulerCommit |
        SimpleDdgiGpuPassMask.ScheduleTailAdmit |
        SimpleDdgiGpuPassMask.ScheduleEmit;

    [Test]
    public void SubmittedRingPairsEachCompletedSlotWithItsExactWorkAndTimings()
    {
        var ring = new SimpleDdgiSubmittedFrameRing();
        SimpleDdgiSubmittedFrameEvidence first = CreateSubmitted(
            frameSlot: 0,
            frameSerial: (2UL << 32) | 17UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 2);
        SimpleDdgiSubmittedFrameEvidence second = CreateSubmitted(
            frameSlot: 1,
            frameSerial: (3UL << 32) | 29UL,
            sourceGeneration: 22u,
            transportGeneration: 32u,
            cachedSweepCount: 5);
        ring.MarkSubmitted(0, first);
        ring.MarkSubmitted(1, second);

        var firstTimings = new FrameTimingSnapshot(
        [
            new PassTiming("DdgiFoliageProxyGenerationPass", 0, 1, true),
            new PassTiming("SimpleDdgiPageDemandPass", 0, 2, true),
            new PassTiming("SimpleDdgiPageResidencyPass", 0, 3, true),
            new PassTiming("SimpleDdgiPageFeedbackPass", 0, 4, true),
            new PassTiming("SimpleDdgiSchedulePass", 0, 5, true),
            new PassTiming("SimpleDdgiTracePass", 0, 6, true),
            new PassTiming("SimpleDdgiDirectionalRadiancePass", 0, 7, true),
            new PassTiming("SimpleDdgiAcceleratedSolvePass", 0, 8, true),
            new PassTiming("SimpleDdgiTransportPass", 0, 9, true),
            new PassTiming("SimpleDdgiBlendPass", 0, 10, true),
            new PassTiming("SimpleDdgiTransportAuditPass", 0, 11, true),
            new PassTiming("SimpleDdgiRelocateClassifyPass", 0, 12, true),
            new PassTiming("SimpleDdgiPublishPass", 0, 13, true),
            new PassTiming("SimpleDdgiSchedulerCommitPass", 0, 14, true),
            new PassTiming("SimpleDdgiSchedule.TailAdmit", 0, 101, true),
            new PassTiming("SimpleDdgiSchedule.Emit", 0, 103, true)
        ]);
        GPUSimpleDdgiSchedulerFeedback firstFeedback = CreateFeedback(first) with
        {
            ConsideredCount = 91,
            EligibleCount = 71,
            AcceptedCount = 51,
            CommittedCount = 41,
            PublishedCount = 31,
            SourceProbeUsed = 5,
            HardSourceProbeUsed = 3,
            RoutineSourceProbeUsed = 2,
            CachedSolverProbeUsed = 7,
            SolveEpochParticipantCount = 19,
            SolveEpochVisitedCount = 17,
            SolveEpoch = 13,
            PrimaryRayUsed = 1_000,
            SourceAchievedRays = 600,
            TransportRayUsed = 950
        };

        Assert.That(
            ring.TryPeek(0, out SimpleDdgiSubmittedFrameEvidence peeked),
            Is.True);
        Assert.That(peeked, Is.EqualTo(first));
        Assert.That(
            ring.TryConsume(0, out SimpleDdgiSubmittedFrameEvidence completedFirst),
            Is.True);
        SimpleDdgiCompletedFrameEvidence completed =
            SimpleDdgiFrameEvidenceFactory.Complete(
                completedFirst,
                firstTimings,
                schedulerFeedbackAvailable: true,
                firstFeedback,
                first.TransportTopologyGeneration);

        Assert.Multiple(() =>
        {
            Assert.That(completed.Submitted, Is.EqualTo(first));
            Assert.That(completed.GpuTimingAvailable, Is.True);
            Assert.That(completed.GpuTimingPassSetAligned, Is.True);
            Assert.That(completed.CompletedGpuTimingPasses,
                Is.EqualTo(CompletePassMask));
            Assert.That(completed.GpuAcceleratedSolveTimingAvailable, Is.True);
            Assert.That(completed.GpuSchedulerTailAdmitTimingAvailable, Is.True);
            Assert.That(completed.GpuSchedulerEmitTimingAvailable, Is.True);
            Assert.That(completed.GpuSchedulerCommitTimingAvailable, Is.True);
            Assert.That(completed.GpuDdgiTotalTimingAvailable, Is.True);
            Assert.That(completed.GpuAcceleratedSolveMicroseconds, Is.EqualTo(8));
            Assert.That(completed.GpuSchedulerTailAdmitMicroseconds, Is.EqualTo(101));
            Assert.That(completed.GpuSchedulerEmitMicroseconds, Is.EqualTo(103));
            Assert.That(completed.GpuSchedulerCommitMicroseconds, Is.EqualTo(14));
            Assert.That(completed.GpuDdgiTotalMicroseconds, Is.EqualTo(105));
            Assert.That(completed.SchedulerFeedbackFrameAligned, Is.True);
            Assert.That(completed.SchedulerFeedbackGenerationAligned, Is.True);
            Assert.That(completed.SchedulerFeedbackQueueTransactionGeneration,
                Is.EqualTo(first.QueueTransactionGeneration));
            Assert.That(completed.SchedulerFeedbackTransportTopologyGeneration,
                Is.EqualTo(first.TransportTopologyGeneration));
            Assert.That(completed.SchedulerCompactedCandidateCount, Is.EqualTo(71));
            Assert.That(completed.SchedulerAcceptedWorkCount, Is.EqualTo(51));
            Assert.That(completed.SchedulerCommittedWorkCount, Is.EqualTo(41));
            Assert.That(completed.SchedulerPublishedWorkCount, Is.EqualTo(31));
            Assert.That(completed.SchedulerActiveWorkCount, Is.EqualTo(12));
            Assert.That(completed.SchedulerCachedParticipantCount, Is.EqualTo(7));
            Assert.That(completed.SchedulerCachedRayCount, Is.EqualTo(350));
            Assert.That(completed.SchedulerSolveParticipantCount, Is.EqualTo(19));
            Assert.That(completed.SchedulerSolveVisitedCount, Is.EqualTo(17));
            Assert.That(completed.SchedulerSolveEpoch, Is.EqualTo(13));
            Assert.That(first.TailCertificate.IsAcceptedFor(first), Is.True);
            Assert.That(first.TailCertificate.Generations.VolumeTable,
                Is.Not.EqualTo(first.VolumeResourceGeneration));
            Assert.That(first.TailCertificate.Generations.VolumeTable,
                Is.EqualTo(first.TransportTopologyGeneration));
            Assert.That(first.TailCertificate.Generations.PhysicalOwnership,
                Is.EqualTo(first.TransportTopologyGeneration));
        });

        Assert.That(ring.TryPeek(0, out _), Is.False);
        Assert.That(
            ring.TryPeek(1, out SimpleDdgiSubmittedFrameEvidence stillPending),
            Is.True,
            "Consuming slot 0 must not phase-shift or clear slot 1.");
        Assert.That(stillPending, Is.EqualTo(second));

        var secondTimings = new FrameTimingSnapshot(
        [
            new PassTiming("SimpleDdgiAcceleratedSolvePass", 0, 999, true),
            new PassTiming("SimpleDdgiSchedulePass", 0, 17, true)
        ]);
        Assert.That(
            ring.TryConsume(1, out SimpleDdgiSubmittedFrameEvidence completedSecond),
            Is.True);
        SimpleDdgiCompletedFrameEvidence deliberatelyMismatched =
            SimpleDdgiFrameEvidenceFactory.Complete(
                completedSecond,
                secondTimings,
                schedulerFeedbackAvailable: true,
                firstFeedback,
                first.TransportTopologyGeneration);
        Assert.Multiple(() =>
        {
            Assert.That(deliberatelyMismatched.Submitted, Is.EqualTo(second));
            Assert.That(deliberatelyMismatched.GpuAcceleratedSolveMicroseconds,
                Is.EqualTo(999));
            Assert.That(deliberatelyMismatched.SchedulerFeedbackFrameAligned, Is.False);
            Assert.That(deliberatelyMismatched.SchedulerFeedbackGenerationAligned, Is.False);
            Assert.That(deliberatelyMismatched.GpuTimingPassSetAligned, Is.False);
        });
    }

    [TestCase("SimpleDdgiSchedulePass", SimpleDdgiGpuPassMask.Schedule)]
    [TestCase("SimpleDdgiTracePass", SimpleDdgiGpuPassMask.Trace)]
    [TestCase("SimpleDdgiRelocateClassifyPass",
        SimpleDdgiGpuPassMask.RelocateClassify)]
    [TestCase("SimpleDdgiAcceleratedSolvePass",
        SimpleDdgiGpuPassMask.AcceleratedSolve)]
    [TestCase("SimpleDdgiTransportPass", SimpleDdgiGpuPassMask.Transport)]
    [TestCase("SimpleDdgiBlendPass", SimpleDdgiGpuPassMask.Blend)]
    [TestCase("SimpleDdgiTransportAuditPass",
        SimpleDdgiGpuPassMask.TransportAudit)]
    [TestCase("SimpleDdgiPublishPass", SimpleDdgiGpuPassMask.Publish)]
    [TestCase("SimpleDdgiSchedulerCommitPass",
        SimpleDdgiGpuPassMask.SchedulerCommit)]
    [TestCase("SimpleDdgiSchedule.TailAdmit",
        SimpleDdgiGpuPassMask.ScheduleTailAdmit)]
    [TestCase("SimpleDdgiSchedule.Emit", SimpleDdgiGpuPassMask.ScheduleEmit)]
    public void TimestampScopeNamesMapToStablePassBits(
        string passName,
        SimpleDdgiGpuPassMask expected)
    {
        Assert.That(SimpleDdgiGpuPassContract.FromPassName(passName),
            Is.EqualTo(expected));
    }

    [Test]
    public void CaptureSubmittedCopiesTailStateWithoutRetainingReferenceTelemetry()
    {
        SimpleDdgiTransportGenerations generations = CreateGenerations(
            sourceGeneration: 9u,
            transportGeneration: 12u);
        using var sceneData = new SceneRenderingData
        {
            DdgiFrameSerial = 77UL,
            SimpleDdgiActive = 1,
            SimpleDdgiSchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            DdgiActiveProbeCount = 2_048,
            SimpleDdgiVolumeResourceGeneration = 5u,
            SimpleDdgiTransportTopologyGeneration = 6u,
            SimpleDdgiSourceLightingGeneration = generations.SourceLighting,
            SimpleDdgiAdmittedSourceCohortGeneration = 9u,
            SimpleDdgiTransportGeneration = generations.CanonicalField,
            SimpleDdgiPublishedPropagationGeneration = 12u,
            SimpleDdgiLivePropagationSourceGeneration = 9u,
            SimpleDdgiSchedulerResourceGeneration = generations.SchedulerResources,
            SimpleDdgiTransportCachedSweepCount = 4,
            SimpleDdgiTransportTailCertificationEnabled = true,
            DdgiScheduledPrimaryRayCount = 7_000UL,
            DdgiVisibilityRayCount = 1_500UL,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty
        };
        SimpleDdgiTailCertificateFrameEvidence tail = CreateTailCertificate(
            generations,
            participantCount: 200u,
            firstFrameSerial: 70UL,
            finalFrameSerial: 76UL) with
        {
            ExcludedInactiveCount = 7u,
            ExcludedNotVisibleCount = 11u,
            Summary = CreateCertifiedSummary(
                generations,
                participantCount: 200u,
                firstFrameSerial: 70UL,
                finalFrameSerial: 76UL) with
            {
                ExcludedInactiveCount = 7u,
                ExcludedNotVisibleCount = 11u
            }
        };
        tail = tail with
        {
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(tail.Summary)
        };

        SimpleDdgiSubmittedFrameEvidence submitted =
            SimpleDdgiFrameEvidenceFactory.CaptureSubmitted(
                frameSlot: 1,
                sceneData,
                gpuTimingRecorded: true,
                intendedGpuPasses: CompletePassMask,
                admittedGpuTimingPasses: CompletePassMask,
                queueTransactionGeneration: generations.Queue,
                tail,
                sourceCacheLayoutIdentity: 0xabcdefUL);

        Assert.Multiple(() =>
        {
            Assert.That(submitted.Valid, Is.True);
            Assert.That(submitted.FrameSlot, Is.EqualTo(1));
            Assert.That(submitted.FrameSerial, Is.EqualTo(77UL));
            Assert.That(submitted.ActiveProbeCount, Is.EqualTo(2_048));
            Assert.That(submitted.CachedSweepCount, Is.EqualTo(4));
            Assert.That(submitted.ScheduledPrimaryRayCount, Is.EqualTo(7_000UL));
            Assert.That(submitted.VisibilityRayCount, Is.EqualTo(1_500UL));
            Assert.That(submitted.TailCertificate.SolveEpoch,
                Is.EqualTo(generations.Solve));
            Assert.That(submitted.TailCertificate.AuditEpoch,
                Is.EqualTo(generations.Audit));
            Assert.That(submitted.TailCertificate.ExcludedInactiveCount, Is.EqualTo(7u));
            Assert.That(submitted.TailCertificate.ExcludedNotVisibleCount, Is.EqualTo(11u));
            Assert.That(submitted.TailCertificate.IsAcceptedFor(submitted), Is.True);
        });
    }

    [Test]
    public void PerTargetAvailabilityDistinguishesAbsentInactivePassesFromRecordedZero()
    {
        SimpleDdgiSubmittedFrameEvidence submitted = CreateSubmitted(
            frameSlot: 0,
            frameSerial: 50UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 0) with
        {
            IntendedGpuPasses =
                SimpleDdgiGpuPassMask.Schedule |
                SimpleDdgiGpuPassMask.SchedulerCommit,
            AdmittedGpuTimingPasses =
                SimpleDdgiGpuPassMask.Schedule |
                SimpleDdgiGpuPassMask.SchedulerCommit
        };
        var timings = new FrameTimingSnapshot(
        [
            // The recorder marks a sub-microsecond/quantized-zero duration as
            // unavailable, but the named query entry still proves the pass
            // was recorded. Absence is represented by no entry at all.
            new PassTiming("SimpleDdgiSchedulePass", 0, 0, false),
            new PassTiming("SimpleDdgiSchedulerCommitPass", 0, 0, false)
        ]);
        GPUSimpleDdgiSchedulerFeedback feedback = CreateFeedback(submitted);

        SimpleDdgiCompletedFrameEvidence completed =
            SimpleDdgiFrameEvidenceFactory.Complete(
                submitted,
                timings,
                schedulerFeedbackAvailable: true,
                feedback,
                submitted.TransportTopologyGeneration);

        Assert.Multiple(() =>
        {
            Assert.That(completed.GpuDdgiTotalTimingAvailable, Is.True);
            Assert.That(completed.GpuSchedulerCommitTimingAvailable, Is.True);
            Assert.That(completed.GpuSchedulerCommitMicroseconds, Is.Zero);
            Assert.That(completed.GpuAcceleratedSolveTimingAvailable, Is.False);
            Assert.That(completed.GpuSchedulerTailAdmitTimingAvailable, Is.False);
            Assert.That(completed.GpuSchedulerEmitTimingAvailable, Is.False);
            Assert.That(completed.GpuAcceleratedSolveMicroseconds, Is.Zero);
            Assert.That(completed.GpuSchedulerTailAdmitMicroseconds, Is.Zero);
            Assert.That(completed.GpuSchedulerEmitMicroseconds, Is.Zero);
        });
    }

    [Test]
    public void CompletionRejectsLogicalPhysicalTopologyOrQueueEpochMismatches()
    {
        SimpleDdgiSubmittedFrameEvidence submitted = CreateSubmitted(
            frameSlot: 0,
            frameSerial: 80UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 0) with
        {
            IntendedGpuPasses =
                SimpleDdgiGpuPassMask.Schedule |
                SimpleDdgiGpuPassMask.SchedulerCommit,
            AdmittedGpuTimingPasses =
                SimpleDdgiGpuPassMask.Schedule |
                SimpleDdgiGpuPassMask.SchedulerCommit
        };
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("SimpleDdgiSchedulePass", 0, 3, true),
            new PassTiming("SimpleDdgiSchedulerCommitPass", 0, 5, true)
        ]);
        GPUSimpleDdgiSchedulerFeedback feedback = CreateFeedback(submitted);

        SimpleDdgiCompletedFrameEvidence aligned =
            SimpleDdgiFrameEvidenceFactory.Complete(
                submitted,
                timings,
                schedulerFeedbackAvailable: true,
                feedback,
                submitted.TransportTopologyGeneration);
        SimpleDdgiCompletedFrameEvidence wrongPhysicalTopology =
            SimpleDdgiFrameEvidenceFactory.Complete(
                submitted,
                timings,
                schedulerFeedbackAvailable: true,
                feedback,
                schedulerFeedbackTransportTopologyGeneration:
                    submitted.VolumeResourceGeneration);
        GPUSimpleDdgiSchedulerFeedback wrongQueue = feedback;
        wrongQueue.QueueTransactionGeneration++;
        SimpleDdgiCompletedFrameEvidence wrongQueueEpoch =
            SimpleDdgiFrameEvidenceFactory.Complete(
                submitted,
                timings,
                schedulerFeedbackAvailable: true,
                wrongQueue,
                submitted.TransportTopologyGeneration);

        Assert.Multiple(() =>
        {
            Assert.That(submitted.VolumeResourceGeneration,
                Is.Not.EqualTo(submitted.TransportTopologyGeneration));
            Assert.That(aligned.SchedulerFeedbackGenerationAligned, Is.True);
            Assert.That(wrongPhysicalTopology.SchedulerFeedbackGenerationAligned,
                Is.False);
            Assert.That(wrongQueueEpoch.SchedulerFeedbackGenerationAligned,
                Is.False);
        });
    }

    [Test]
    public void EmptyAuditAwaitMaskIsExactlyAlignedButHasNoDdgiTotal()
    {
        SimpleDdgiSubmittedFrameEvidence submitted = CreateSubmitted(
            frameSlot: 0,
            frameSerial: 82UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 0) with
        {
            IntendedGpuPasses = SimpleDdgiGpuPassMask.None,
            AdmittedGpuTimingPasses = SimpleDdgiGpuPassMask.None
        };

        SimpleDdgiCompletedFrameEvidence completed =
            SimpleDdgiFrameEvidenceFactory.Complete(
                submitted,
                FrameTimingSnapshot.Empty,
                schedulerFeedbackAvailable: false,
                feedback: default,
                schedulerFeedbackTransportTopologyGeneration: 0u);

        Assert.Multiple(() =>
        {
            Assert.That(completed.GpuTimingPassSetAligned, Is.True);
            Assert.That(completed.CompletedGpuTimingPasses,
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
            Assert.That(completed.GpuTimingAvailable, Is.False);
            Assert.That(completed.GpuDdgiTotalTimingAvailable, Is.False);
            Assert.That(completed.GpuDdgiTotalMicroseconds, Is.Zero);
        });
    }

    [TestCase("phase")]
    [TestCase("reason")]
    [TestCase("solve-epoch")]
    [TestCase("queue-cohort")]
    [TestCase("source-cohort")]
    [TestCase("published-cohort")]
    [TestCase("digest")]
    [TestCase("audit-lifecycle")]
    [TestCase("numerical-proof")]
    [TestCase("copied-count")]
    public void CertificateAcceptanceRequiresExactDurableIdentity(
        string mutation)
    {
        SimpleDdgiSubmittedFrameEvidence submitted = CreateSubmitted(
            frameSlot: 0,
            frameSerial: 90UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 2);
        SimpleDdgiTailCertificateFrameEvidence tail = submitted.TailCertificate;

        switch (mutation)
        {
            case "phase":
                tail = tail with { Phase = SimpleDdgiTransportPhase.Tracking };
                break;
            case "reason":
                tail = tail with
                {
                    Reason = SimpleDdgiTransportCertificationReason.Tracking
                };
                break;
            case "solve-epoch":
                tail = tail with { SolveEpoch = tail.SolveEpoch + 1u };
                break;
            case "queue-cohort":
                submitted = submitted with
                {
                    QueueTransactionGeneration =
                        submitted.QueueTransactionGeneration + 1u
                };
                break;
            case "source-cohort":
                submitted = submitted with
                {
                    LivePropagationSourceGeneration =
                        submitted.SourceLightingGeneration + 1u
                };
                break;
            case "published-cohort":
                submitted = submitted with
                {
                    PublishedPropagationGeneration =
                        submitted.TransportGeneration + 1u
                };
                break;
            case "digest":
                tail = tail with { SummaryDigest = tail.SummaryDigest ^ 1UL };
                break;
            case "audit-lifecycle":
            {
                SimpleDdgiTransportTailSummary summary = tail.Summary with
                {
                    ChunkCount = 0u
                };
                tail = tail with
                {
                    Summary = summary,
                    SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
                };
                break;
            }
            case "numerical-proof":
            {
                SimpleDdgiTransportTailSummary summary = tail.Summary with
                {
                    AbsoluteTailBound = float.NaN
                };
                tail = tail with
                {
                    Summary = summary,
                    SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
                };
                break;
            }
            case "copied-count":
                tail = tail with
                {
                    ExcludedInactiveCount = tail.ExcludedInactiveCount + 1u
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        submitted = submitted with { TailCertificate = tail };
        Assert.That(tail.IsAcceptedFor(submitted), Is.False);
    }

    [Test]
    public void ConsumedSlotCannotBeObservedTwiceAfterAnObserverThrows()
    {
        var ring = new SimpleDdgiSubmittedFrameRing();
        SimpleDdgiSubmittedFrameEvidence submitted = CreateSubmitted(
            frameSlot: 0,
            frameSerial: 100UL,
            sourceGeneration: 21u,
            transportGeneration: 31u,
            cachedSweepCount: 1);
        ring.MarkSubmitted(0, submitted);

        Assert.That(
            ring.TryConsume(0, out SimpleDdgiSubmittedFrameEvidence local),
            Is.True);
        Assert.Throws<InvalidOperationException>(() => ObserveAndThrow(local));
        Assert.That(ring.TryConsume(0, out _), Is.False,
            "A retry must not reacquire a slot whose workload already mutated an observer.");
    }

    [Test]
    public void RendererConsumesFenceCompleteSlotBeforeAnyWorkloadObservation()
    {
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        int beginFrame = renderer.IndexOf(
            "public bool BeginFrame()",
            StringComparison.Ordinal);
        int consume = renderer.IndexOf(
            "_simpleDdgiSubmittedFrameRing.TryConsume(",
            beginFrame,
            StringComparison.Ordinal);
        int observe = renderer.IndexOf(
            "ObserveCompletedSimpleDdgiWorkload(",
            beginFrame,
            StringComparison.Ordinal);
        int complete = renderer.IndexOf(
            "CompleteSimpleDdgiSubmittedFrame(",
            beginFrame,
            StringComparison.Ordinal);
        int observeMethod = renderer.IndexOf(
            "private void ObserveCompletedSimpleDdgiWorkload(",
            StringComparison.Ordinal);
        int captureMethod = renderer.IndexOf(
            "private void CapturePendingSimpleDdgiSubmittedFrame(",
            observeMethod,
            StringComparison.Ordinal);
        int completeMethod = renderer.IndexOf(
            "private void CompleteSimpleDdgiSubmittedFrame(",
            captureMethod,
            StringComparison.Ordinal);
        int nextMethod = renderer.IndexOf(
            "private void ScheduleReflectionProbeRecapturesFromGi(",
            completeMethod,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(beginFrame, Is.GreaterThanOrEqualTo(0));
            Assert.That(consume, Is.GreaterThan(beginFrame));
            Assert.That(observe, Is.GreaterThan(consume));
            Assert.That(complete, Is.GreaterThan(observe));
            Assert.That(renderer[observeMethod..captureMethod],
                Does.Not.Contain("TryPeek("));
            Assert.That(renderer[completeMethod..nextMethod],
                Does.Not.Contain("TryConsume("));
        });
    }

    [Test]
    public void SubmittedRingAndCompletionFactoryAllocateNothingPerFrame()
    {
        var ring = new SimpleDdgiSubmittedFrameRing();
        SimpleDdgiTransportGenerations generations = CreateGenerations(21u, 31u);
        SimpleDdgiGpuPassMask allocationPassMask =
            SimpleDdgiGpuPassMask.Schedule |
            SimpleDdgiGpuPassMask.ScheduleTailAdmit |
            SimpleDdgiGpuPassMask.ScheduleEmit |
            SimpleDdgiGpuPassMask.SchedulerCommit |
            SimpleDdgiGpuPassMask.AcceleratedSolve;
        SimpleDdgiTailCertificateFrameEvidence tail =
            CreateTailCertificate(generations);
        using var sceneData = new SceneRenderingData
        {
            DdgiFrameSerial = 1UL,
            SimpleDdgiActive = 1,
            SimpleDdgiSchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            DdgiActiveProbeCount = 2_048,
            SimpleDdgiVolumeResourceGeneration = 5u,
            SimpleDdgiTransportTopologyGeneration = 6u,
            SimpleDdgiSourceLightingGeneration = generations.SourceLighting,
            SimpleDdgiAdmittedSourceCohortGeneration = generations.SourceLighting,
            SimpleDdgiTransportGeneration = generations.CanonicalField,
            SimpleDdgiPublishedPropagationGeneration = generations.CanonicalField,
            SimpleDdgiLivePropagationSourceGeneration = generations.SourceLighting,
            SimpleDdgiSchedulerResourceGeneration = generations.SchedulerResources,
            SimpleDdgiTransportCachedSweepCount = 1,
            SimpleDdgiTransportTailCertificationEnabled = true,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty
        };
        SimpleDdgiSubmittedFrameEvidence template =
            SimpleDdgiFrameEvidenceFactory.CaptureSubmitted(
                0,
                sceneData,
                gpuTimingRecorded: true,
                intendedGpuPasses: allocationPassMask,
                admittedGpuTimingPasses: allocationPassMask,
                queueTransactionGeneration: generations.Queue,
                tail,
                sourceCacheLayoutIdentity: 99UL);
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("SimpleDdgiSchedulePass", 0, 10, true),
            new PassTiming("SimpleDdgiSchedule.TailAdmit", 0, 2, true),
            new PassTiming("SimpleDdgiSchedule.Emit", 0, 3, true),
            new PassTiming("SimpleDdgiSchedulerCommitPass", 0, 5, true),
            new PassTiming("SimpleDdgiAcceleratedSolvePass", 0, 7, true)
        ]);
        GPUSimpleDdgiSchedulerFeedback feedback = CreateFeedback(template);

        ring.MarkSubmitted(0, template);
        Assert.That(ring.TryConsume(0, out SimpleDdgiSubmittedFrameEvidence warmup), Is.True);
        _ = SimpleDdgiFrameEvidenceFactory.Complete(
            warmup,
            timings,
            schedulerFeedbackAvailable: true,
            feedback,
            template.TransportTopologyGeneration);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            int slot = iteration % ring.FrameSlotCount;
            ulong serial = (ulong)iteration + 100UL;
            sceneData.DdgiFrameSerial = serial;
            SimpleDdgiSubmittedFrameEvidence submitted =
                SimpleDdgiFrameEvidenceFactory.CaptureSubmitted(
                    slot,
                    sceneData,
                    gpuTimingRecorded: true,
                    intendedGpuPasses: allocationPassMask,
                    admittedGpuTimingPasses: allocationPassMask,
                    queueTransactionGeneration: generations.Queue,
                    tail,
                    sourceCacheLayoutIdentity: 99UL);
            GPUSimpleDdgiSchedulerFeedback aligned = feedback;
            aligned.FrameSerialLow = unchecked((uint)serial);
            aligned.FrameSerialHigh = unchecked((uint)(serial >> 32));
            ring.MarkSubmitted(slot, submitted);
            if (!ring.TryPeek(slot, out SimpleDdgiSubmittedFrameEvidence peeked) ||
                !ring.TryConsume(slot, out SimpleDdgiSubmittedFrameEvidence consumed))
            {
                throw new InvalidOperationException("Submitted evidence was not retained.");
            }
            SimpleDdgiCompletedFrameEvidence completed =
                SimpleDdgiFrameEvidenceFactory.Complete(
                    consumed,
                    timings,
                    schedulerFeedbackAvailable: true,
                    aligned,
                    submitted.TransportTopologyGeneration);
            checksum += completed.GpuDdgiTotalMicroseconds +
                (long)peeked.FrameSerial +
                unchecked((long)(SimpleDdgiTailSummaryDigest.Compute(
                    tail.Summary) & 0x7fffUL));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(checksum, Is.GreaterThan(0));
            Assert.That(allocated, Is.Zero);
            Assert.That(ring.FrameSlotCount, Is.EqualTo(RenderingConstants.FramesInFlight));
        });
    }

    private static SimpleDdgiSubmittedFrameEvidence CreateSubmitted(
        int frameSlot,
        ulong frameSerial,
        uint sourceGeneration,
        uint transportGeneration,
        int cachedSweepCount)
    {
        SimpleDdgiTransportGenerations generations = CreateGenerations(
            sourceGeneration,
            transportGeneration);
        return new SimpleDdgiSubmittedFrameEvidence
        {
            Valid = true,
            FrameSlot = frameSlot,
            FrameSerial = frameSerial,
            GpuTimingRecorded = true,
            SchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            ActiveProbeCount = 2_048,
            VolumeResourceGeneration = 5u,
            TransportTopologyGeneration = 6u,
            SourceLightingGeneration = sourceGeneration,
            AdmittedSourceCohortGeneration = sourceGeneration,
            TransportGeneration = transportGeneration,
            PublishedPropagationGeneration = transportGeneration,
            LivePropagationSourceGeneration = sourceGeneration,
            SchedulerResourceGeneration = generations.SchedulerResources,
            QueueTransactionGeneration = generations.Queue,
            CachedSweepCount = cachedSweepCount,
            TailCertificationEnabled = true,
            TailCertificate = CreateTailCertificate(generations),
            IntendedGpuPasses = CompletePassMask,
            AdmittedGpuTimingPasses = CompletePassMask,
            SourceCacheLayoutIdentity = 99UL,
            ScheduledPrimaryRayCount = 8_192UL,
            VisibilityRayCount = 2_048UL
        };
    }

    private static SimpleDdgiTransportGenerations CreateGenerations(
        uint sourceGeneration,
        uint transportGeneration) =>
        new(
            VolumeTable: 6u,
            PhysicalOwnership: 6u,
            SourceLighting: sourceGeneration,
            SourceEpoch: 8u,
            TransportOperator: 9u,
            CanonicalField: transportGeneration,
            Solve: 11u,
            Audit: 12u,
            Queue: 14u,
            SchedulerResources: 14u);

    private static GPUSimpleDdgiSchedulerFeedback CreateFeedback(
        in SimpleDdgiSubmittedFrameEvidence submitted) =>
        new()
        {
            FrameSerialLow = unchecked((uint)submitted.FrameSerial),
            FrameSerialHigh = unchecked((uint)(submitted.FrameSerial >> 32)),
            VolumeTableGeneration = submitted.VolumeResourceGeneration,
            SchedulerResourceGeneration = submitted.SchedulerResourceGeneration,
            QueueTransactionGeneration = submitted.QueueTransactionGeneration,
            SourceLightingGeneration = submitted.SourceLightingGeneration,
            TransportGeneration = submitted.TransportGeneration
        };

    private static void ObserveAndThrow(
        in SimpleDdgiSubmittedFrameEvidence submitted)
    {
        if (!submitted.Valid)
            throw new ArgumentException("Expected valid evidence.", nameof(submitted));
        throw new InvalidOperationException("Synthetic observer failure.");
    }

    private static string ReadRepoText(params string[] relativeParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{Path.Combine(relativeParts)}'.");
    }

    private static SimpleDdgiTailCertificateFrameEvidence CreateTailCertificate(
        SimpleDdgiTransportGenerations generations,
        uint participantCount = 2_048u,
        ulong firstFrameSerial = 40UL,
        ulong finalFrameSerial = 41UL)
    {
        SimpleDdgiTransportTailSummary summary = CreateCertifiedSummary(
            generations,
            participantCount,
            firstFrameSerial,
            finalFrameSerial);
        return new SimpleDdgiTailCertificateFrameEvidence
        {
            Phase = SimpleDdgiTransportPhase.Certified,
            Reason = SimpleDdgiTransportCertificationReason.Certified,
            Generations = generations,
            SolveEpoch = generations.Solve,
            AuditEpoch = generations.Audit,
            ExpectedParticipantCount = participantCount,
            AuditedParticipantCount = participantCount,
            ExpectedTexelCount = checked(participantCount * 64u),
            AuditedTexelCount = checked(participantCount * 64u),
            AuditComplete = true,
            CertificateCurrent = true,
            AuditFirstSubmissionFrameSerial = firstFrameSerial,
            AuditFinalSubmissionFrameSerial = finalFrameSerial,
            AuditPlannedChunkCount = 2u,
            AuditSubmittedChunkCount = 2u,
            AuditDispatchComplete = true,
            Summary = summary,
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
        };
    }

    private static SimpleDdgiTransportTailSummary CreateCertifiedSummary(
        SimpleDdgiTransportGenerations generations,
        uint participantCount,
        ulong firstFrameSerial,
        ulong finalFrameSerial) =>
        new()
        {
            AuditEpoch = generations.Audit,
            Generations = generations,
            ExpectedParticipantCount = participantCount,
            AuditedParticipantCount = participantCount,
            ExpectedTexelCount = checked(participantCount * 64u),
            AuditedTexelCount = checked(participantCount * 64u),
            FixedPointDefect = 0.001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.5f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.002f,
            RelativeTailBound = 0.002f,
            Tolerance = 0.01f,
            CanonicalQuantizationFloor = 0.001f,
            AuditMicroseconds = 25UL,
            FirstFrameSerial = firstFrameSerial,
            FinalFrameSerial = finalFrameSerial,
            ChunkCount = 2u,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.Certified
        };
}
