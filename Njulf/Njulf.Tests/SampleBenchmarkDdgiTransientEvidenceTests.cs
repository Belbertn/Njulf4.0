using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkDdgiTransientEvidenceTests
{
    private const int AuditPhysicalProbeCount = 768;
    private const int AuditActiveProbeCount = 512;
    private const uint AuditParticipantCount = 512u;
    private const uint AuditChunkCount = 3u;
    private const uint AuditTexelCount = AuditParticipantCount * 64u;

    private const SimpleDdgiGpuPassMask OrdinaryBasePasses =
        SimpleDdgiGpuPassMask.Schedule |
        SimpleDdgiGpuPassMask.Trace |
        SimpleDdgiGpuPassMask.RelocateClassify |
        SimpleDdgiGpuPassMask.Publish |
        SimpleDdgiGpuPassMask.SchedulerCommit |
        SimpleDdgiGpuPassMask.ScheduleTailAdmit |
        SimpleDdgiGpuPassMask.ScheduleEmit;

    [TestCase(60, 180)]
    [TestCase(61, 181)]
    public void BistroSunStepJoinsTwoExactDelayedTransientWindows(
        int firstEdge,
        int secondEdge)
    {
        int firstCertificate = firstEdge + 5;
        int secondCertificate = secondEdge + 5;
        SampleBenchmarkReport report = CreateReport(
            firstEdge,
            secondEdge,
            firstCertificate,
            secondCertificate);

        SampleBenchmarkDdgiTransientEvidence evidence =
            report.DdgiTransientEvidence;
        Assert.Multiple(() =>
        {
            Assert.That(evidence.Applicable, Is.True);
            Assert.That(evidence.Available, Is.True);
            Assert.That(evidence.Failures, Is.Empty);
            Assert.That(evidence.Windows, Has.Count.EqualTo(2));
            Assert.That(report.CaptureContract.Mismatches.Any(mismatch =>
                mismatch.StartsWith(
                    "DDGI transient evidence unavailable:",
                    StringComparison.Ordinal)), Is.False);
        });

        SampleBenchmarkDdgiTransientWindow first = evidence.Windows[0];
        SampleBenchmarkDdgiTransientWindow second = evidence.Windows[1];
        Assert.Multiple(() =>
        {
            Assert.That(first.AuthoredEventRouteFrameIndex, Is.EqualTo(60));
            Assert.That(first.ObservedGenerationEdgeRouteFrameIndex,
                Is.EqualTo(firstEdge));
            Assert.That(first.GenerationResponseLatencyFrames,
                Is.EqualTo(firstEdge - 60));
            Assert.That(first.PreviousSourceLightingGeneration, Is.EqualTo(1u));
            Assert.That(first.SourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(first.AcceptedCertificateRouteFrameIndex,
                Is.EqualTo(firstCertificate));
            Assert.That(first.CertificateLatencyFrames, Is.EqualTo(5));
            Assert.That(
                first.Frames.Select(frame => frame.RouteFrameIndex),
                Is.EqualTo(Enumerable.Range(firstEdge, 6)));
            Assert.That(
                first.Frames.Select(frame =>
                    frame.CompletionObservedMeasurementSampleIndex),
                Is.EqualTo(Enumerable.Range(
                    firstEdge + RenderingConstants.FramesInFlight,
                    6)));
            Assert.That(first.FirstSubmittedFrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(first.LastSubmittedFrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstCertificate));
            Assert.That(first.FirstSubmittedSchedulerFrameSerial,
                Is.EqualTo(10_001UL + (ulong)firstEdge));
            Assert.That(first.LastSubmittedSchedulerFrameSerial,
                Is.EqualTo(10_001UL + (ulong)firstCertificate));

            Assert.That(second.AuthoredEventRouteFrameIndex, Is.EqualTo(180));
            Assert.That(second.ObservedGenerationEdgeRouteFrameIndex,
                Is.EqualTo(secondEdge));
            Assert.That(second.GenerationResponseLatencyFrames,
                Is.EqualTo(secondEdge - 180));
            Assert.That(second.PreviousSourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(second.SourceLightingGeneration, Is.EqualTo(3u));
            Assert.That(second.AcceptedCertificateRouteFrameIndex,
                Is.EqualTo(secondCertificate));
            Assert.That(second.Frames, Has.Count.EqualTo(6));
        });

        SimpleDdgiCompletedFrameEvidence ordinary = first.Frames[0].Completed;
        SimpleDdgiCompletedFrameEvidence trigger = first.Frames[1].Completed;
        SimpleDdgiCompletedFrameEvidence certified = first.Frames[^1].Completed;
        Assert.Multiple(() =>
        {
            Assert.That(ordinary.Submitted.FrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(ordinary.Submitted.SchedulerFrameSerial,
                Is.EqualTo(10_001UL + (ulong)firstEdge));
            Assert.That(ordinary.Submitted.FrameSerialsValid, Is.True);
            Assert.That(ordinary.Submitted.ActiveProbeCount,
                Is.EqualTo(AuditActiveProbeCount));
            Assert.That(ordinary.Submitted.AuditPhysicalProbeCount,
                Is.EqualTo(AuditPhysicalProbeCount));
            Assert.That(ordinary.Submitted.SourceLightingGeneration,
                Is.EqualTo(2u));
            Assert.That(ordinary.Submitted.CachedSweepCount, Is.EqualTo(2));
            Assert.That(ordinary.GpuAcceleratedSolveMicroseconds,
                Is.EqualTo(100 + firstEdge));
            Assert.That(ordinary.GpuSchedulerTailAdmitMicroseconds,
                Is.EqualTo(200 + firstEdge));
            Assert.That(ordinary.GpuSchedulerEmitMicroseconds,
                Is.EqualTo(300 + firstEdge));
            Assert.That(ordinary.GpuSchedulerCommitMicroseconds,
                Is.EqualTo(400 + firstEdge));
            Assert.That(ordinary.GpuUrgentRelightTimingAvailable, Is.True);
            Assert.That(ordinary.GpuUrgentRelightMicroseconds,
                Is.EqualTo(450 + firstEdge));
            Assert.That(ordinary.GpuDdgiTotalMicroseconds,
                Is.EqualTo(5_000 + firstEdge));
            Assert.That(ordinary.SchedulerAcceptedWorkCount,
                Is.EqualTo((uint)(50 + firstEdge)));
            Assert.That(ordinary.SchedulerCompactedCandidateCount,
                Is.EqualTo((uint)(70 + firstEdge)));
            Assert.That(ordinary.SchedulerActiveWorkCount,
                Is.EqualTo((uint)(10 + firstEdge)));
            Assert.That(ordinary.SchedulerSolveEpoch,
                Is.EqualTo(ordinary.Submitted.TailCertificate.SolveEpoch));
            Assert.That(ordinary.SchedulerSolveParticipantCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(ordinary.SchedulerSolveVisitedCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(ordinary.SchedulerFeedbackFrameSerial + 1UL));
            Assert.That(trigger.SchedulerSolveEpoch, Is.Zero);
            Assert.That(trigger.SchedulerSolveParticipantCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(trigger.SchedulerSolveVisitedCount, Is.Zero);
            Assert.That(trigger.SchedulerActiveCanonicalMutationCount, Is.Zero);
            Assert.That(trigger.SchedulerActiveSourceMutationCount, Is.Zero);
            Assert.That(trigger.SchedulerBlockingTailSourceWorkCount, Is.Zero);
            Assert.That(certified.SchedulerSolveEpoch, Is.Zero);
            Assert.That(certified.SchedulerSolveVisitedCount, Is.Zero);
            Assert.That(certified.Submitted.TailCertificate
                .AuditSolveFeedbackFrameSerial,
                Is.EqualTo(ordinary.SchedulerFeedbackFrameSerial));
            Assert.That(certified.Submitted.TailCertificate
                .AuditTriggerFeedbackFrameSerial,
                Is.EqualTo(trigger.SchedulerFeedbackFrameSerial));
        });

        SimpleDdgiCompletedFrameEvidence[] auditFrames = first.Frames
            .Select(static frame => frame.Completed)
            .Where(static completed => completed.Submitted.TailCertificate.Phase ==
                SimpleDdgiTransportPhase.AuditFrozen)
            .ToArray();
        Assert.That(auditFrames, Has.Length.EqualTo(3));
        SimpleDdgiCompletedFrameEvidence[] auditDispatches = auditFrames.Where(
            static completed => completed.GpuTransportAuditTimingAvailable)
            .ToArray();
        SimpleDdgiCompletedFrameEvidence auditAwait = auditFrames.Single(
            static completed => !completed.GpuTransportAuditTimingAvailable);
        Assert.Multiple(() =>
        {
            Assert.That(auditDispatches, Has.Length.EqualTo(2));
            Assert.That(auditDispatches.Select(static completed => completed
                .Submitted.TailCertificate.AuditSubmittedChunkCount),
                Is.EqualTo(new[] { 2u, 3u }));
            Assert.That(auditDispatches[0].Submitted.TailCertificate
                .AuditDispatchComplete, Is.False);
            Assert.That(auditDispatches[1].Submitted.TailCertificate
                .AuditDispatchComplete, Is.True);
            Assert.That(auditDispatches.All(static completed =>
                completed.GpuDdgiTotalTimingAvailable), Is.True);
            Assert.That(auditAwait.GpuDdgiTotalTimingAvailable, Is.False);
            Assert.That(auditAwait.GpuTimingAvailable, Is.False);
            Assert.That(auditAwait.SchedulerFeedbackAvailable, Is.False);
            Assert.That((auditAwait.Submitted.IntendedGpuPasses &
                (SimpleDdgiGpuPassMask.TransportAudit |
                 SimpleDdgiGpuPassMask.Schedule |
                 SimpleDdgiGpuPassMask.SchedulerCommit)),
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
            Assert.That((auditAwait.Submitted.IntendedGpuPasses &
                (SimpleDdgiGpuPassMask.PageDemand |
                 SimpleDdgiGpuPassMask.PageResidency |
                 SimpleDdgiGpuPassMask.PageFeedback)),
                Is.EqualTo(
                    SimpleDdgiGpuPassMask.PageDemand |
                    SimpleDdgiGpuPassMask.PageResidency |
                    SimpleDdgiGpuPassMask.PageFeedback));
        });
        Assert.That(auditFrames.All(static completed =>
            completed.Submitted.TailCertificate.HasCompleteIdentity &&
            completed.Submitted.TailCertificate.HasDurableSummary &&
            !completed.SchedulerFeedbackAvailable &&
            (completed.Submitted.IntendedGpuPasses &
             (SimpleDdgiGpuPassMask.Schedule |
              SimpleDdgiGpuPassMask.SchedulerCommit)) == 0), Is.True);
    }

    [Test]
    public void MissingCompletedFrameFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            missingOrigin: 62);

        AssertUnavailable(
            report,
            "has no exact completed scheduler identity");
    }

    [Test]
    public void ReportJsonPersistsAlignedPassAndTailLifecycleEvidence()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185);

        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"IntendedGpuPasses\":"));
            Assert.That(json, Does.Contain("\"AdmittedGpuTimingPasses\":"));
            Assert.That(json, Does.Contain("\"CompletedGpuTimingPasses\":"));
            Assert.That(json, Does.Contain("\"GpuTransportAuditMicroseconds\":"));
            Assert.That(json, Does.Contain("\"GpuUrgentRelightMicroseconds\":"));
            Assert.That(json, Does.Contain("\"SchedulerFrameSerial\":"));
            Assert.That(json, Does.Contain(
                "\"FirstSubmittedSchedulerFrameSerial\":"));
            Assert.That(json, Does.Contain("\"AuditPhysicalProbeCount\":"));
            Assert.That(json, Does.Contain("\"SummaryDigest\":"));
            Assert.That(json, Does.Contain(
                "\"AuditSolveFeedbackFrameSerial\":"));
            Assert.That(json, Does.Contain(
                "\"AuditTriggerFeedbackFrameSerial\":"));
            Assert.That(json, Does.Contain("\"ChannelEvidenceVersion\":1"));
            Assert.That(json, Does.Contain("\"FirstFrameSerial\":"));
            Assert.That(json, Does.Contain("\"FinalFrameSerial\":"));
            Assert.That(json, Does.Contain("\"ChunkCount\":"));
        });
    }

    [Test]
    public void CertifiedCurrentFeedbackUsesPriorSolveAndTriggerPackets()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        SimpleDdgiCompletedFrameEvidence solve = frames[0].Completed;
        SimpleDdgiCompletedFrameEvidence trigger = frames[1].Completed;
        SimpleDdgiCompletedFrameEvidence certified = frames[^1].Completed;
        SimpleDdgiTailCertificateFrameEvidence tail =
            certified.Submitted.TailCertificate;

        Assert.Multiple(() =>
        {
            Assert.That(solve.SchedulerFeedbackFrameSerial,
                Is.EqualTo(tail.AuditSolveFeedbackFrameSerial));
            Assert.That(solve.SchedulerSolveEpoch,
                Is.EqualTo(tail.SolveEpoch));
            Assert.That(solve.SchedulerSolveParticipantCount,
                Is.EqualTo(tail.ExpectedParticipantCount));
            Assert.That(solve.SchedulerSolveVisitedCount,
                Is.EqualTo(tail.ExpectedParticipantCount));
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(tail.AuditTriggerFeedbackFrameSerial));
            Assert.That(trigger.SchedulerSolveEpoch, Is.Zero);
            Assert.That(trigger.SchedulerSolveVisitedCount, Is.Zero);
            Assert.That(certified.SchedulerSolveEpoch, Is.Zero,
                "Certified Upload writes no active solve epoch.");
            Assert.That(certified.SchedulerSolveVisitedCount, Is.Zero,
                "Certified feedback cannot re-prove the earlier solve visit.");
            Assert.That(certified.SchedulerSolveParticipantCount,
                Is.EqualTo(tail.ExpectedParticipantCount),
                "The independent participant reduction may remain populated.");
        });
    }

    [Test]
    public void WindowOverlappingNextGenerationEdgeFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: -1,
            secondCertificate: 185);

        AssertUnavailable(report, "overlapped the next source-lighting edge");
    }

    [Test]
    public void UncompletedFinalWindowFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: -1);

        AssertUnavailable(
            report,
            "did not complete with an accepted current tail certificate");
    }

    [Test]
    public void GenerationEdgesOutsideAuthoredResponseRangeFailClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 62,
            secondEdge: 182,
            firstCertificate: 67,
            secondCertificate: 187);

        AssertUnavailable(report, "expected [60,61]");
    }

    [Test]
    public void NonSuccessorGenerationSequenceFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            forceBadGenerationSequence: true);

        AssertUnavailable(report, "expected wrap-safe +1 generation");
    }

    [Test]
    public void WrapSafeSuccessorGenerationSequenceIsAccepted()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            initialSourceGeneration: uint.MaxValue);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientEvidence.Windows[0]
                .PreviousSourceLightingGeneration, Is.EqualTo(uint.MaxValue));
            Assert.That(report.DdgiTransientEvidence.Windows[0]
                .SourceLightingGeneration, Is.EqualTo(1u));
            Assert.That(report.DdgiTransientEvidence.Windows[1]
                .SourceLightingGeneration, Is.EqualTo(2u));
        });
    }

    [Test]
    public void RendererFrameSerialZeroIsAValidJoinIdentity()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            serialBase: 0UL);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        Assert.That(report.DdgiTransientEvidence.Windows[0]
            .FirstSubmittedFrameSerial, Is.EqualTo(60UL));
    }

    [TestCase(SimpleDdgiGpuPassMask.Schedule)]
    [TestCase(SimpleDdgiGpuPassMask.Trace)]
    [TestCase(SimpleDdgiGpuPassMask.RelocateClassify)]
    [TestCase(SimpleDdgiGpuPassMask.Publish)]
    [TestCase(SimpleDdgiGpuPassMask.AcceleratedSolve)]
    [TestCase(SimpleDdgiGpuPassMask.Transport)]
    [TestCase(SimpleDdgiGpuPassMask.Blend)]
    [TestCase(SimpleDdgiGpuPassMask.ScheduleTailAdmit)]
    [TestCase(SimpleDdgiGpuPassMask.ScheduleEmit)]
    [TestCase(SimpleDdgiGpuPassMask.SchedulerCommit)]
    [TestCase(SimpleDdgiGpuPassMask.UrgentRelight)]
    public void MissingCompletedActivePassFailsClosed(
        SimpleDdgiGpuPassMask missingPass)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            missingPassOrigin: 60,
            missingCompletedPass: missingPass,
            legacyPathOrigin: missingPass is
                SimpleDdgiGpuPassMask.Transport or
                SimpleDdgiGpuPassMask.Blend
                    ? 60
                    : -1);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void GenerationEdgeWithoutUrgentRelightScopeFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            omitUrgentRelightOrigin: 60);

        AssertUnavailable(report, "no urgent-relight parent timing scope");
    }

    [Test]
    public void MissingTimestampAdmissionFailsClosedEvenWhenResultExists()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            missingAdmissionOrigin: 60);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void AuditDispatchWithMissingCompletedAuditTimingFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            missingPassOrigin: 62,
            missingCompletedPass: SimpleDdgiGpuPassMask.TransportAudit);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void AuditAwaitRowThatFalselySuppliesAuditWorkFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            falseAuditWorkOnAwaitOrigin: 64);

        AssertUnavailable(report, "advanced the frozen audit by 0 chunks");
    }

    [Test]
    public void AuditDispatchThatFalselySuppliesOrdinaryWorkFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            falseOrdinaryWorkOnAuditDispatchOrigin: 62);

        AssertUnavailable(report, "scheduler/solve/publication timing scopes");
    }

    [Test]
    public void CompleteLegacyTransportPathMayOmitAcceleratedAndDirectionalPasses()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            legacyPathOrigin: 60);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        SimpleDdgiCompletedFrameEvidence completed =
            report.DdgiTransientEvidence.Windows[0].Frames[0].Completed;
        Assert.Multiple(() =>
        {
            Assert.That(completed.GpuAcceleratedSolveTimingAvailable, Is.False);
            Assert.That(completed.GpuAcceleratedSolveMicroseconds, Is.Zero);
            Assert.That((completed.CompletedGpuTimingPasses &
                (SimpleDdgiGpuPassMask.Transport |
                 SimpleDdgiGpuPassMask.Blend)),
                Is.EqualTo(SimpleDdgiGpuPassMask.Transport |
                    SimpleDdgiGpuPassMask.Blend));
            Assert.That((completed.CompletedGpuTimingPasses &
                SimpleDdgiGpuPassMask.DirectionalRadiance),
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
        });
    }

    [Test]
    public void NonContiguousRouteSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            shiftedRouteSerialIndex: 61);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void WrongSubmittedFrameSlotFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            wrongSlotOrigin: 60);

        AssertUnavailable(report, "expected renderer slot");
    }

    [Test]
    public void WrongCompletionDelayFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            earlyCompletionOrigin: 60);

        AssertUnavailable(report, "expected exact FramesInFlight delay");
    }

    [Test]
    public void DuplicateSchedulerSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            wrongSchedulerSerialOrigin: 60);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void SchedulerSerialCanHaveIndependentDisabledPrehistoryOffset()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            schedulerSerialBase: 500UL);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        Assert.That(
            report.DdgiTransientEvidence.Windows[0]
                .FirstSubmittedSchedulerFrameSerial,
            Is.EqualTo(560UL));
    }

    [Test]
    public void SkippedSchedulerSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            skippedSchedulerSerialOrigin: 20);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void SchedulerSerialSentinelWrapFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            schedulerSerialBase: ulong.MaxValue - 60UL);

        AssertUnavailable(report, "cannot advance without entering a sentinel");
    }

    [Test]
    public void InvalidSchedulerSerialSentinelFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            invalidSchedulerSerialOrigin: 20);

        AssertUnavailable(report, "retained invalid renderer/scheduler serials");
    }

    [Test]
    public void InactiveMeasuredRouteFrameFailsClosedIncludingUncompletedTail()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            inactiveRouteIndex:
                SampleBistroQualityCaptureContract.LoopFrameCount - 1);

        AssertUnavailable(report, "is not Simple-DDGI active");
    }

    [Test]
    public void FeedbackMustUseSchedulerSerialDomainRatherThanRouteSerial()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            wrongFeedbackSerialOrigin: 60);

        AssertUnavailable(report, "scheduler feedback retained the wrong frame serial");
    }

    [TestCase("legacy-channel")]
    [TestCase("physical-cardinality")]
    [TestCase("population-partition")]
    [TestCase("equal-final")]
    [TestCase("feedback-participant")]
    [TestCase("feedback-visited")]
    [TestCase("feedback-epoch")]
    public void ForgedCertificateArtifactsFailClosed(string mutation)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            legacyChannelCertificateOrigin: mutation == "legacy-channel"
                ? 65
                : -1,
            falsePhysicalCountCertificateOrigin:
                mutation == "physical-cardinality" ? 65 : -1,
            falsePopulationCertificateOrigin:
                mutation == "population-partition" ? 65 : -1,
            wrongCertificateFeedbackOrigin: mutation.StartsWith(
                "feedback-",
                StringComparison.Ordinal) ? 60 : -1,
            wrongCertificateFeedbackField: mutation,
            equalFinalCertificateOrigin: mutation == "equal-final" ? 65 : -1);

        AssertUnavailable(
            report,
            mutation.StartsWith("feedback-", StringComparison.Ordinal)
                ? "solve-feedback packet is not the exact complete"
                : "Certified tail row without an exact");
    }

    [TestCase("trigger-epoch", 61, "audit-trigger feedback packet")]
    [TestCase("trigger-participant", 61, "audit-trigger feedback packet")]
    [TestCase("trigger-canonical-mutation", 61, "audit-trigger feedback packet")]
    [TestCase("trigger-source-mutation", 61, "audit-trigger feedback packet")]
    [TestCase("trigger-blocking-source", 61, "audit-trigger feedback packet")]
    [TestCase("trigger-missing", 61, "missing the exact earlier audit-trigger")]
    [TestCase("solve-missing", 60, "missing the exact earlier solve-feedback")]
    [TestCase("solve-topology", 60, "solve-feedback packet is not the exact complete")]
    [TestCase("trigger-queue", 61, "audit-trigger feedback packet")]
    [TestCase("feedback-reordered", 65, "Certified tail row without an exact")]
    public void TwoPacketAuditFeedbackProvenanceFailsClosed(
        string mutation,
        int mutationOrigin,
        string expectedFailure)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 65,
            secondCertificate: 185,
            wrongCertificateFeedbackOrigin: mutationOrigin,
            wrongCertificateFeedbackField: mutation);

        AssertUnavailable(report, expectedFailure);
    }

    private static void AssertUnavailable(
        SampleBenchmarkReport report,
        string expectedFailure)
    {
        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientEvidence.Applicable, Is.True);
            Assert.That(report.DdgiTransientEvidence.Available, Is.False);
            Assert.That(
                report.DdgiTransientEvidence.Failures,
                Has.Some.Contains(expectedFailure));
            Assert.That(report.CaptureContract.Comparable, Is.False);
            Assert.That(
                report.CaptureContract.Mismatches,
                Has.Some.Contains("DDGI transient evidence unavailable: "));
        });
    }

    private static SampleBenchmarkReport CreateReport(
        int firstEdge,
        int secondEdge,
        int firstCertificate,
        int secondCertificate,
        int missingOrigin = -1,
        ulong serialBase = 10_000UL,
        uint initialSourceGeneration = 1u,
        bool forceBadGenerationSequence = false,
        int missingPassOrigin = -1,
        SimpleDdgiGpuPassMask missingCompletedPass =
            SimpleDdgiGpuPassMask.None,
        int missingAdmissionOrigin = -1,
        int legacyPathOrigin = -1,
        int shiftedRouteSerialIndex = -1,
        int wrongSlotOrigin = -1,
        int earlyCompletionOrigin = -1,
        int falseAuditWorkOnAwaitOrigin = -1,
        int falseOrdinaryWorkOnAuditDispatchOrigin = -1,
        int omitUrgentRelightOrigin = -1,
        int wrongSchedulerSerialOrigin = -1,
        int wrongFeedbackSerialOrigin = -1,
        int legacyChannelCertificateOrigin = -1,
        int falsePhysicalCountCertificateOrigin = -1,
        int falsePopulationCertificateOrigin = -1,
        int equalFinalCertificateOrigin = -1,
        ulong? schedulerSerialBase = null,
        int skippedSchedulerSerialOrigin = -1,
        int invalidSchedulerSerialOrigin = -1,
        int wrongCertificateFeedbackOrigin = -1,
        string? wrongCertificateFeedbackField = null,
        int inactiveRouteIndex = -1)
    {
        const int frameCount = SampleBistroQualityCaptureContract.LoopFrameCount;
        ulong resolvedSchedulerSerialBase = schedulerSerialBase ??
            checked(serialBase + 1UL);
        var analyzer = new SampleBenchmarkAnalyzer();
        for (int sampleIndex = 0; sampleIndex < frameCount; sampleIndex++)
        {
            int completedOrigin = sampleIndex - RenderingConstants.FramesInFlight;
            if (earlyCompletionOrigin >= 0)
            {
                if (sampleIndex == earlyCompletionOrigin + 1)
                    completedOrigin = earlyCompletionOrigin;
                else if (sampleIndex ==
                         earlyCompletionOrigin + RenderingConstants.FramesInFlight)
                    completedOrigin = int.MinValue;
            }

            SimpleDdgiCompletedFrameEvidence completed;
            if (completedOrigin == int.MinValue || completedOrigin == missingOrigin)
            {
                completed = default;
            }
            else
            {
                bool measuredOrigin = completedOrigin >= 0;
                int syntheticOrigin = measuredOrigin
                    ? completedOrigin
                    : -100 + sampleIndex;
                uint sourceGeneration = measuredOrigin
                    ? SourceGeneration(
                        completedOrigin,
                        firstEdge,
                        secondEdge,
                        initialSourceGeneration,
                        forceBadGenerationSequence)
                    : initialSourceGeneration;
                bool certificate =
                    completedOrigin == firstCertificate ||
                    completedOrigin == secondCertificate;
                bool auditFrozen =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstCertificate - 3 &&
                    completedOrigin < firstCertificate ||
                    secondCertificate >= 0 &&
                    completedOrigin >= secondCertificate - 3 &&
                    completedOrigin < secondCertificate;
                bool auditAwait =
                    firstCertificate >= 0 &&
                    completedOrigin == firstCertificate - 1 ||
                    secondCertificate >= 0 &&
                    completedOrigin == secondCertificate - 1;
                int enclosingCertificate =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstCertificate - 5 &&
                    completedOrigin <= firstCertificate
                        ? firstCertificate
                        : secondCertificate >= 0 &&
                          completedOrigin >= secondCertificate - 5 &&
                          completedOrigin <= secondCertificate
                            ? secondCertificate
                            : syntheticOrigin + 5;
                completed = CreateCompleted(
                    syntheticOrigin,
                    sourceGeneration,
                    certificate,
                    auditFrozen,
                    auditAwait,
                    serialBase,
                    resolvedSchedulerSerialBase,
                    legacyPath: completedOrigin == legacyPathOrigin,
                    urgentRelight:
                        completedOrigin == firstEdge ||
                        completedOrigin == secondEdge,
                    certificateOrigin: enclosingCertificate);
            }

            if (skippedSchedulerSerialOrigin >= 0 &&
                completedOrigin == skippedSchedulerSerialOrigin)
            {
                ulong skipped = checked(
                    completed.Submitted.SchedulerFrameSerial + 1UL);
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        SchedulerFrameSerial = skipped
                    },
                    SchedulerFeedbackFrameSerial = skipped
                };
            }
            if (invalidSchedulerSerialOrigin >= 0 &&
                completedOrigin == invalidSchedulerSerialOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        SchedulerFrameSerial = ulong.MaxValue
                    },
                    SchedulerFeedbackFrameSerial = ulong.MaxValue
                };
            }

            if (completedOrigin == missingPassOrigin)
            {
                completed = RemoveCompletedPass(
                    completed,
                    missingCompletedPass);
            }
            if (completedOrigin == missingAdmissionOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        AdmittedGpuTimingPasses =
                            completed.Submitted.AdmittedGpuTimingPasses &
                            ~SimpleDdgiGpuPassMask.Trace
                    },
                    GpuTimingPassSetAligned = false
                };
            }
            if (completedOrigin == wrongSlotOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        FrameSlot = (completed.Submitted.FrameSlot + 1) %
                            RenderingConstants.FramesInFlight
                    }
                };
            }
            if (completedOrigin == falseAuditWorkOnAwaitOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        IntendedGpuPasses =
                            SimpleDdgiGpuPassMask.TransportAudit,
                        AdmittedGpuTimingPasses =
                            SimpleDdgiGpuPassMask.TransportAudit
                    },
                    GpuTimingAvailable = true,
                    GpuTimingPassSetAligned = true,
                    CompletedGpuTimingPasses =
                        SimpleDdgiGpuPassMask.TransportAudit,
                    GpuTransportAuditTimingAvailable = true,
                    GpuDdgiTotalTimingAvailable = true,
                    GpuTransportAuditMicroseconds = 123,
                    GpuDdgiTotalMicroseconds = 123
                };
            }
            if (completedOrigin == falseOrdinaryWorkOnAuditDispatchOrigin)
            {
                SimpleDdgiGpuPassMask falseMask =
                    completed.Submitted.IntendedGpuPasses |
                    SimpleDdgiGpuPassMask.Trace;
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        IntendedGpuPasses = falseMask,
                        AdmittedGpuTimingPasses = falseMask
                    },
                    GpuTimingPassSetAligned = true,
                    CompletedGpuTimingPasses = falseMask
                };
            }
            if (completedOrigin == omitUrgentRelightOrigin)
            {
                SimpleDdgiGpuPassMask mask =
                    completed.Submitted.IntendedGpuPasses &
                    ~SimpleDdgiGpuPassMask.UrgentRelight;
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        IntendedGpuPasses = mask,
                        AdmittedGpuTimingPasses = mask
                    },
                    CompletedGpuTimingPasses = mask,
                    GpuTimingPassSetAligned = true,
                    GpuUrgentRelightTimingAvailable = false,
                    GpuUrgentRelightMicroseconds = 0
                };
            }
            if (completedOrigin == wrongSchedulerSerialOrigin)
            {
                ulong duplicate = completed.Submitted.SchedulerFrameSerial - 1UL;
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        SchedulerFrameSerial = duplicate
                    },
                    SchedulerFeedbackFrameSerial = duplicate
                };
            }
            if (completedOrigin == wrongFeedbackSerialOrigin)
            {
                completed = completed with
                {
                    SchedulerFeedbackFrameSerial =
                        completed.Submitted.FrameSerial
                };
            }
            if (completedOrigin == legacyChannelCertificateOrigin)
            {
                completed = MutateCertificateSummary(
                    completed,
                    static summary => summary with
                    {
                        ChannelEvidenceVersion = 0u
                    });
            }
            if (completedOrigin == falsePhysicalCountCertificateOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        AuditPhysicalProbeCount =
                            AuditPhysicalProbeCount + 1
                    }
                };
            }
            if (completedOrigin == falsePopulationCertificateOrigin)
            {
                SimpleDdgiTailCertificateFrameEvidence tail =
                    completed.Submitted.TailCertificate;
                SimpleDdgiTransportTailSummary summary = tail.Summary with
                {
                    ExcludedInactiveCount =
                        tail.Summary.ExcludedInactiveCount - 1u
                };
                tail = tail with
                {
                    ExcludedInactiveCount = summary.ExcludedInactiveCount,
                    Summary = summary,
                    SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
                };
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        TailCertificate = tail
                    }
                };
            }
            if (completedOrigin == equalFinalCertificateOrigin)
            {
                ulong finalSerial =
                    completed.Submitted.SchedulerFrameSerial;
                ulong firstSerial = finalSerial - 1UL;
                completed = MutateCertificateSummary(
                    completed,
                    summary => summary with
                    {
                        FirstFrameSerial = firstSerial,
                        FinalFrameSerial = finalSerial
                    },
                    firstSerial,
                    finalSerial);
            }
            if (wrongCertificateFeedbackOrigin >= 0 &&
                completedOrigin == wrongCertificateFeedbackOrigin)
            {
                completed = wrongCertificateFeedbackField switch
                {
                    "feedback-participant" => completed with
                    {
                        SchedulerSolveParticipantCount =
                            completed.SchedulerSolveParticipantCount - 1u
                    },
                    "feedback-visited" => completed with
                    {
                        SchedulerSolveVisitedCount =
                            completed.SchedulerSolveVisitedCount - 1u
                    },
                    "feedback-epoch" => completed with
                    {
                        SchedulerSolveEpoch = completed.SchedulerSolveEpoch + 1u
                    },
                    "trigger-epoch" => completed with
                    {
                        SchedulerSolveEpoch = 1u
                    },
                    "trigger-participant" => completed with
                    {
                        SchedulerSolveParticipantCount =
                            completed.SchedulerSolveParticipantCount - 1u
                    },
                    "trigger-canonical-mutation" => completed with
                    {
                        SchedulerActiveCanonicalMutationCount = 1u
                    },
                    "trigger-source-mutation" => completed with
                    {
                        SchedulerActiveSourceMutationCount = 1u
                    },
                    "trigger-blocking-source" => completed with
                    {
                        SchedulerBlockingTailSourceWorkCount = 1u
                    },
                    "trigger-missing" => completed with
                    {
                        SchedulerFeedbackAvailable = false,
                        SchedulerFeedbackFrameAligned = false,
                        SchedulerFeedbackGenerationAligned = false,
                        SchedulerFeedbackFrameSerial = 0UL
                    },
                    "solve-missing" => completed with
                    {
                        SchedulerFeedbackAvailable = false,
                        SchedulerFeedbackFrameAligned = false,
                        SchedulerFeedbackGenerationAligned = false,
                        SchedulerFeedbackFrameSerial = 0UL
                    },
                    "solve-topology" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportTopologyGeneration = 7u
                        },
                        SchedulerFeedbackTransportTopologyGeneration = 7u
                    },
                    "trigger-queue" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            SchedulerResourceGeneration = 15u,
                            QueueTransactionGeneration = 15u
                        },
                        SchedulerFeedbackSchedulerResourceGeneration = 15u,
                        SchedulerFeedbackQueueTransactionGeneration = 15u
                    },
                    "feedback-reordered" =>
                        MutateAuditFeedbackLifecycle(completed),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(wrongCertificateFeedbackField))
                };
            }

            SampleBenchmarkCameraPose pose =
                SampleBenchmarkTrajectory.ResolveCamera(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    sampleIndex,
                    SampleBistroQualityCaptureVariant.SunScaleStep)!;
            var camera = new PerformanceCaptureCameraMetadata(
                pose.Position.X,
                pose.Position.Y,
                pose.Position.Z,
                pose.Yaw,
                pose.Pitch,
                pose.FieldOfView,
                pose.NearPlane,
                pose.FarPlane,
                "synthetic-view",
                "synthetic-projection",
                0UL);
            ulong routeSerial = serialBase + (ulong)sampleIndex;
            if (sampleIndex == shiftedRouteSerialIndex)
                routeSerial++;
            analyzer.AddSample(
                RendererDiagnostics.Empty with
                {
                    CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
                    {
                        FrameSerial = routeSerial,
                        FramesSinceSceneLoad = (ulong)sampleIndex,
                        WarmupState = DdgiRuntimeWarmupState.SteadyState
                    },
                    CaptureCamera = camera,
                    SimpleDdgiActive = sampleIndex == inactiveRouteIndex ? 0 : 1,
                    SimpleDdgiSourceLightingGeneration = SourceGeneration(
                        sampleIndex,
                        firstEdge,
                        secondEdge,
                        initialSourceGeneration,
                        forceBadGenerationSequence),
                    SimpleDdgiCompletedFrameEvidence = completed
                },
                RenderBudgetSnapshot.Empty);
        }

        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: frameCount,
            ReportPath: null)
        {
            Trajectory = SampleBenchmarkTrajectoryKind.BistroLoop,
            TrajectoryBistroVariant =
                SampleBistroQualityCaptureVariant.SunScaleStep,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.BistroLoop,
                SampleBistroQualityCaptureVariant.SunScaleStep)
        };
        return analyzer.CreateReport(
            options,
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: frameCount,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: frameCount - 1);
    }

    private static uint SourceGeneration(
        int routeFrameIndex,
        int firstEdge,
        int secondEdge,
        uint initialGeneration,
        bool forceBadGenerationSequence)
    {
        if (routeFrameIndex >= secondEdge)
        {
            return forceBadGenerationSequence
                ? 3u
                : AdvanceNonZero(AdvanceNonZero(initialGeneration));
        }
        if (routeFrameIndex >= firstEdge)
            return forceBadGenerationSequence ? 99u : AdvanceNonZero(initialGeneration);
        return initialGeneration;
    }

    private static uint AdvanceNonZero(uint generation)
    {
        uint next = unchecked(generation + 1u);
        return next == 0u ? 1u : next;
    }

    private static SimpleDdgiCompletedFrameEvidence CreateCompleted(
        int originIndex,
        uint sourceGeneration,
        bool certificate,
        bool auditFrozen,
        bool auditAwait,
        ulong routeSerialBase,
        ulong schedulerSerialBase,
        bool legacyPath,
        bool urgentRelight,
        int certificateOrigin)
    {
        ulong frameSerial = originIndex >= 0
            ? routeSerialBase + (ulong)originIndex
            : 9_000UL + (ulong)(originIndex + 100);
        ulong schedulerFrameSerial = originIndex >= 0
            ? unchecked(schedulerSerialBase + (ulong)originIndex)
            : 8_000UL + (ulong)(originIndex + 100);
        uint transportGeneration = AdvanceNonZero(
            unchecked(sourceGeneration + 100u));
        var generations = new SimpleDdgiTransportGenerations(
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
        bool useLegacyPath = legacyPath || certificate;
        SimpleDdgiGpuPassMask passMask = auditFrozen
            ? auditAwait
                ? SimpleDdgiGpuPassMask.PageDemand |
                  SimpleDdgiGpuPassMask.PageResidency |
                  SimpleDdgiGpuPassMask.PageFeedback
                : SimpleDdgiGpuPassMask.TransportAudit
            : OrdinaryBasePasses |
              (useLegacyPath
                  ? SimpleDdgiGpuPassMask.Transport |
                    SimpleDdgiGpuPassMask.Blend
                  : SimpleDdgiGpuPassMask.AcceleratedSolve) |
              (urgentRelight
                  ? SimpleDdgiGpuPassMask.UrgentRelight
                  : SimpleDdgiGpuPassMask.None);
        ulong certificateSchedulerFrameSerial = originIndex >= 0
            ? unchecked(schedulerSerialBase + (ulong)certificateOrigin)
            : schedulerFrameSerial + 5UL;
        ulong solveFeedbackFrameSerial = certificateSchedulerFrameSerial - 5UL;
        ulong triggerFeedbackFrameSerial = certificateSchedulerFrameSerial - 4UL;
        bool solveFeedbackWitness =
            schedulerFrameSerial == solveFeedbackFrameSerial;
        bool triggerFeedbackWitness =
            schedulerFrameSerial == triggerFeedbackFrameSerial;
        SimpleDdgiTailCertificateFrameEvidence tail = CreateTail(
            generations,
            certificate,
            auditFrozen,
            auditAwait,
            schedulerFrameSerial,
            certificateSchedulerFrameSerial);
        var submitted = new SimpleDdgiSubmittedFrameEvidence
        {
            Valid = true,
            FrameSlot = checked((int)(frameSerial %
                (ulong)RenderingConstants.FramesInFlight)),
            FrameSerial = frameSerial,
            SchedulerFrameSerial = schedulerFrameSerial,
            GpuTimingRecorded = true,
            SchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            ActiveProbeCount = AuditActiveProbeCount,
            AuditPhysicalProbeCount = AuditPhysicalProbeCount,
            VolumeResourceGeneration = 5u,
            TransportTopologyGeneration = 6u,
            SourceLightingGeneration = sourceGeneration,
            AdmittedSourceCohortGeneration = sourceGeneration,
            TransportGeneration = transportGeneration,
            PublishedPropagationGeneration = transportGeneration,
            LivePropagationSourceGeneration = sourceGeneration,
            SchedulerResourceGeneration = 14u,
            QueueTransactionGeneration = 14u,
            CachedSweepCount = auditFrozen ? 0 : useLegacyPath ? 1 : 2,
            TailCertificationEnabled = true,
            TailCertificate = tail,
            IntendedGpuPasses = passMask,
            AdmittedGpuTimingPasses = passMask
        };
        int exactIndex = Math.Max(0, originIndex);
        bool accelerated =
            (passMask & SimpleDdgiGpuPassMask.AcceleratedSolve) != 0;
        bool scheduler = !auditFrozen;
        bool auditDispatch = auditFrozen && !auditAwait;
        return new SimpleDdgiCompletedFrameEvidence
        {
            Valid = true,
            Submitted = submitted,
            GpuTimingAvailable = !auditAwait,
            GpuTimingPassSetAligned = true,
            CompletedGpuTimingPasses = passMask,
            GpuScheduleTimingAvailable = scheduler,
            GpuAcceleratedSolveTimingAvailable = accelerated,
            GpuSchedulerTailAdmitTimingAvailable = scheduler,
            GpuSchedulerEmitTimingAvailable = scheduler,
            GpuSchedulerCommitTimingAvailable = scheduler,
            GpuTransportAuditTimingAvailable = auditDispatch,
            GpuUrgentRelightTimingAvailable = urgentRelight,
            GpuDdgiTotalTimingAvailable = !auditAwait,
            GpuAcceleratedSolveMicroseconds = accelerated
                ? 100 + exactIndex
                : 0,
            GpuSchedulerTailAdmitMicroseconds = scheduler
                ? 200 + exactIndex
                : 0,
            GpuSchedulerEmitMicroseconds = scheduler
                ? 300 + exactIndex
                : 0,
            GpuSchedulerCommitMicroseconds = scheduler
                ? 400 + exactIndex
                : 0,
            GpuTransportAuditMicroseconds = auditDispatch
                ? 150 + exactIndex
                : 0,
            GpuUrgentRelightMicroseconds = urgentRelight
                ? 450 + exactIndex
                : 0,
            GpuDdgiTotalMicroseconds = auditAwait ? 0 : 5_000 + exactIndex,
            SchedulerFeedbackAvailable = scheduler,
            SchedulerFeedbackFrameAligned = scheduler,
            SchedulerFeedbackGenerationAligned = scheduler,
            SchedulerFeedbackFrameSerial = scheduler
                ? schedulerFrameSerial
                : 0UL,
            SchedulerFeedbackVolumeResourceGeneration = scheduler ? 5u : 0u,
            SchedulerFeedbackTransportTopologyGeneration = scheduler ? 6u : 0u,
            SchedulerFeedbackSchedulerResourceGeneration = scheduler ? 14u : 0u,
            SchedulerFeedbackQueueTransactionGeneration = scheduler ? 14u : 0u,
            SchedulerFeedbackSourceLightingGeneration = scheduler
                ? sourceGeneration
                : 0u,
            SchedulerFeedbackTransportGeneration = scheduler
                ? transportGeneration
                : 0u,
            SchedulerCompactedCandidateCount = scheduler
                ? (uint)(70 + exactIndex)
                : 0u,
            SchedulerAcceptedWorkCount = scheduler
                ? (uint)(50 + exactIndex)
                : 0u,
            SchedulerCommittedWorkCount = scheduler
                ? (uint)(40 + exactIndex)
                : 0u,
            SchedulerPublishedWorkCount = scheduler
                ? (uint)(30 + exactIndex)
                : 0u,
            SchedulerActiveWorkCount = scheduler
                ? (uint)(10 + exactIndex)
                : 0u,
            SchedulerCachedParticipantCount = scheduler
                ? (uint)(5 + exactIndex)
                : 0u,
            SchedulerSolveParticipantCount = scheduler
                ? AuditParticipantCount
                : 0u,
            SchedulerSolveVisitedCount = scheduler
                ? certificate || triggerFeedbackWitness
                    ? 0u
                    : AuditParticipantCount
                : 0u,
            SchedulerSolveEpoch = scheduler
                ? certificate || triggerFeedbackWitness
                    ? 0u
                    : generations.Solve
                : 0u,
            SchedulerActiveCanonicalMutationCount = scheduler &&
                !triggerFeedbackWitness
                    ? solveFeedbackWitness ? 1u : 2u
                    : 0u,
            SchedulerActiveSourceMutationCount = 0u,
            SchedulerBlockingTailSourceWorkCount = 0u,
            SchedulerCachedRayCount = scheduler
                ? (uint)(1_000 + exactIndex)
                : 0u
        };
    }

    private static SimpleDdgiTailCertificateFrameEvidence CreateTail(
        SimpleDdgiTransportGenerations generations,
        bool certificate,
        bool auditFrozen,
        bool auditAwait,
        ulong schedulerFrameSerial,
        ulong certificateSchedulerFrameSerial)
    {
        ulong firstAuditSchedulerSerial = certificateSchedulerFrameSerial - 3UL;
        ulong finalAuditSchedulerSerial = certificateSchedulerFrameSerial - 2UL;
        ulong solveFeedbackSchedulerSerial =
            certificateSchedulerFrameSerial - 5UL;
        ulong triggerFeedbackSchedulerSerial =
            certificateSchedulerFrameSerial - 4UL;
        uint submittedChunkCount = certificate || auditAwait
            ? AuditChunkCount
            : schedulerFrameSerial + 3UL == certificateSchedulerFrameSerial
                ? 2u
                : AuditChunkCount;
        ulong currentFinalSchedulerSerial = submittedChunkCount == 2u
            ? firstAuditSchedulerSerial
            : finalAuditSchedulerSerial;
        SimpleDdgiTransportTailSummary summary = certificate
            ? CreateCertifiedSummary(
                generations,
                firstAuditSchedulerSerial,
                finalAuditSchedulerSerial)
            : SimpleDdgiTransportTailSummary.Empty with
            {
                AuditEpoch = generations.Audit,
                Generations = generations,
                ExpectedParticipantCount = AuditParticipantCount,
                ExpectedTexelCount = AuditTexelCount,
                AuditSolveFeedbackFrameSerial = auditFrozen
                    ? solveFeedbackSchedulerSerial
                    : 0UL,
                AuditTriggerFeedbackFrameSerial = auditFrozen
                    ? triggerFeedbackSchedulerSerial
                    : 0UL,
                FirstFrameSerial = auditFrozen
                    ? firstAuditSchedulerSerial
                    : 0UL,
                FinalFrameSerial = auditFrozen
                    ? currentFinalSchedulerSerial
                    : 0UL,
                ChunkCount = auditFrozen ? submittedChunkCount : 0u,
                Reason = auditFrozen
                    ? SimpleDdgiTransportCertificationReason.AuditInProgress
                    : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete
            };
        return new SimpleDdgiTailCertificateFrameEvidence
        {
            Phase = certificate
                ? SimpleDdgiTransportPhase.Certified
                : auditFrozen
                    ? SimpleDdgiTransportPhase.AuditFrozen
                    : SimpleDdgiTransportPhase.AcceleratedSolve,
            Reason = certificate
                ? SimpleDdgiTransportCertificationReason.Certified
                : auditFrozen
                    ? SimpleDdgiTransportCertificationReason.AuditInProgress
                    : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete,
            Generations = generations,
            SolveEpoch = generations.Solve,
            AuditEpoch = generations.Audit,
            ExpectedParticipantCount = summary.ExpectedParticipantCount,
            AuditedParticipantCount = summary.AuditedParticipantCount,
            ExcludedInactiveCount = summary.ExcludedInactiveCount,
            ExcludedNotVisibleCount = summary.ExcludedNotVisibleCount,
            ExcludedStaleSourceCount = summary.ExcludedStaleSourceCount,
            ExcludedInvalidCacheCount = summary.ExcludedInvalidCacheCount,
            CacheIdentityFailureCount = summary.CacheIdentityFailureCount,
            CacheCardinalityFailureCount = summary.CacheCardinalityFailureCount,
            CacheSourceGenerationFailureCount =
                summary.CacheSourceGenerationFailureCount,
            CacheSourceEpochFailureCount =
                summary.CacheSourceEpochFailureCount,
            CachePhysicalGenerationFailureCount =
                summary.CachePhysicalGenerationFailureCount,
            ExpectedTexelCount = summary.ExpectedTexelCount,
            AuditedTexelCount = summary.AuditedTexelCount,
            NonFiniteCount = summary.NonFiniteCount,
            CounterOverflowCount = summary.CounterOverflowCount,
            AuditComplete = summary.IsComplete,
            CertificateCurrent = certificate,
            AuditSolveFeedbackFrameSerial = auditFrozen || certificate
                ? solveFeedbackSchedulerSerial
                : 0UL,
            AuditTriggerFeedbackFrameSerial = auditFrozen || certificate
                ? triggerFeedbackSchedulerSerial
                : 0UL,
            AuditFirstSubmissionFrameSerial = auditFrozen || certificate
                ? firstAuditSchedulerSerial
                : 0UL,
            AuditFinalSubmissionFrameSerial = auditFrozen || certificate
                ? currentFinalSchedulerSerial
                : 0UL,
            AuditPlannedChunkCount = auditFrozen || certificate
                ? AuditChunkCount
                : 0u,
            AuditSubmittedChunkCount = auditFrozen || certificate
                ? submittedChunkCount
                : 0u,
            AuditDispatchComplete = (auditFrozen || certificate) &&
                submittedChunkCount == AuditChunkCount,
            Summary = summary,
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
        };
    }

    private static SimpleDdgiTransportTailSummary CreateCertifiedSummary(
        SimpleDdgiTransportGenerations generations,
        ulong firstAuditSchedulerSerial,
        ulong finalAuditSchedulerSerial) =>
        new()
        {
            AuditEpoch = generations.Audit,
            Generations = generations,
            ExpectedParticipantCount = AuditParticipantCount,
            AuditedParticipantCount = AuditParticipantCount,
            ExcludedInactiveCount =
                (uint)(AuditPhysicalProbeCount - AuditActiveProbeCount),
            ExpectedTexelCount = AuditTexelCount,
            AuditedTexelCount = AuditTexelCount,
            FixedPointDefect = 0.001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.5f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.002f,
            RelativeTailBound = 0.002f,
            Tolerance = 0.01f,
            CanonicalQuantizationFloor = 0.001f,
            ChannelEvidenceVersion =
                SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion,
            FixedPointDefectChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.001f),
            FieldMagnitudeChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(1.0f),
            ObservedContractionChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.5f),
            CertifiedContractionChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.5f),
            AbsoluteTailBoundChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.002f),
            RelativeTailBoundChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.002f),
            CanonicalQuantizationFloorChannels =
                SimpleDdgiTransportRgbBounds.Broadcast(0.001f),
            AuditMicroseconds = 40UL,
            AuditSolveFeedbackFrameSerial = firstAuditSchedulerSerial - 2UL,
            AuditTriggerFeedbackFrameSerial = firstAuditSchedulerSerial - 1UL,
            FirstFrameSerial = firstAuditSchedulerSerial,
            FinalFrameSerial = finalAuditSchedulerSerial,
            ChunkCount = AuditChunkCount,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.Certified
        };

    private static SimpleDdgiCompletedFrameEvidence RemoveCompletedPass(
        SimpleDdgiCompletedFrameEvidence completed,
        SimpleDdgiGpuPassMask pass)
    {
        SimpleDdgiCompletedFrameEvidence result = completed with
        {
            CompletedGpuTimingPasses =
                completed.CompletedGpuTimingPasses & ~pass,
            GpuTimingPassSetAligned = false
        };
        return pass switch
        {
            SimpleDdgiGpuPassMask.AcceleratedSolve => result with
            {
                GpuAcceleratedSolveTimingAvailable = false,
                GpuAcceleratedSolveMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.ScheduleTailAdmit => result with
            {
                GpuSchedulerTailAdmitTimingAvailable = false,
                GpuSchedulerTailAdmitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.ScheduleEmit => result with
            {
                GpuSchedulerEmitTimingAvailable = false,
                GpuSchedulerEmitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.SchedulerCommit => result with
            {
                GpuSchedulerCommitTimingAvailable = false,
                GpuSchedulerCommitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.TransportAudit => result with
            {
                GpuTransportAuditTimingAvailable = false,
                GpuTransportAuditMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.UrgentRelight => result with
            {
                GpuUrgentRelightTimingAvailable = false,
                GpuUrgentRelightMicroseconds = 0
            },
            _ => result
        };
    }

    private static SimpleDdgiCompletedFrameEvidence MutateCertificateSummary(
        SimpleDdgiCompletedFrameEvidence completed,
        Func<SimpleDdgiTransportTailSummary,
            SimpleDdgiTransportTailSummary> mutate,
        ulong? firstFrameSerial = null,
        ulong? finalFrameSerial = null)
    {
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        SimpleDdgiTransportTailSummary summary = mutate(tail.Summary);
        tail = tail with
        {
            AuditFirstSubmissionFrameSerial =
                firstFrameSerial ?? tail.AuditFirstSubmissionFrameSerial,
            AuditFinalSubmissionFrameSerial =
                finalFrameSerial ?? tail.AuditFinalSubmissionFrameSerial,
            Summary = summary,
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
        };
        return completed with
        {
            Submitted = completed.Submitted with
            {
                TailCertificate = tail
            }
        };
    }

    private static SimpleDdgiCompletedFrameEvidence
        MutateAuditFeedbackLifecycle(SimpleDdgiCompletedFrameEvidence completed)
    {
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        SimpleDdgiTransportTailSummary summary = tail.Summary with
        {
            AuditSolveFeedbackFrameSerial =
                tail.AuditTriggerFeedbackFrameSerial
        };
        tail = tail with
        {
            AuditSolveFeedbackFrameSerial =
                summary.AuditSolveFeedbackFrameSerial,
            Summary = summary,
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
        };
        return completed with
        {
            Submitted = completed.Submitted with
            {
                TailCertificate = tail
            }
        };
    }
}
