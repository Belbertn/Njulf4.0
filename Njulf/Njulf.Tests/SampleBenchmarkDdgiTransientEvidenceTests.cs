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
    private const int SolveOffset = 3;
    private const int TriggerOffset = 5;
    private const int FirstAuditOffset = 7;
    private const int CertificateOffset = 10;

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
        int firstCertificate = firstEdge + CertificateOffset;
        int secondCertificate = secondEdge + CertificateOffset;
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
            Assert.That(first.CertificateLatencyFrames,
                Is.EqualTo(CertificateOffset));
            Assert.That(
                first.Frames.Select(frame => frame.RouteFrameIndex),
                Is.EqualTo(Enumerable.Range(firstEdge, CertificateOffset + 1)));
            Assert.That(
                first.Frames.Select(frame =>
                    frame.CompletionObservedMeasurementSampleIndex),
                Is.EqualTo(Enumerable.Range(
                    firstEdge + RenderingConstants.FramesInFlight,
                    CertificateOffset + 1)));
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
            Assert.That(second.Frames,
                Has.Count.EqualTo(CertificateOffset + 1));
        });

        SimpleDdgiCompletedFrameEvidence sourceRepair = first.Frames[0].Completed;
        SimpleDdgiCompletedFrameEvidence ordinary =
            first.Frames[SolveOffset].Completed;
        SimpleDdgiCompletedFrameEvidence trigger =
            first.Frames[TriggerOffset].Completed;
        SimpleDdgiCompletedFrameEvidence certified = first.Frames[^1].Completed;
        Assert.Multiple(() =>
        {
            Assert.That(sourceRepair.Submitted.FrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(sourceRepair.Submitted.SchedulerFrameSerial,
                Is.EqualTo(10_001UL + (ulong)firstEdge));
            Assert.That(sourceRepair.Submitted.TailCertificate.Phase,
                Is.EqualTo(SimpleDdgiTransportPhase.SourceRepair));
            Assert.That(sourceRepair.Submitted.AdmittedSourceCohortGeneration,
                Is.Zero);
            Assert.That(sourceRepair.Submitted.LivePropagationSourceGeneration,
                Is.Zero);
            Assert.That(sourceRepair.Submitted.CachedSweepCount, Is.Zero);
            Assert.That(sourceRepair.GpuAcceleratedSolveTimingAvailable,
                Is.False);
            Assert.That(sourceRepair.SchedulerSolveEpoch, Is.Zero);
            Assert.That(sourceRepair.SchedulerSolveVisitedCount, Is.Zero);
            Assert.That(sourceRepair.SchedulerSolveParticipantCount,
                Is.LessThan(AuditParticipantCount));
            Assert.That(sourceRepair.CompletedGpuTimingPasses &
                    (SimpleDdgiGpuPassMask.Transport |
                     SimpleDdgiGpuPassMask.Blend),
                Is.EqualTo(SimpleDdgiGpuPassMask.Transport |
                    SimpleDdgiGpuPassMask.Blend));
            Assert.That(sourceRepair.GpuUrgentRelightTimingAvailable, Is.True);
            Assert.That(sourceRepair.GpuUrgentRelightMicroseconds,
                Is.EqualTo(450 + firstEdge));
            Assert.That(sourceRepair.GpuDdgiTotalMicroseconds,
                Is.EqualTo(5_000 + firstEdge));
            Assert.That(sourceRepair.SchedulerActiveSourceMutationCount,
                Is.GreaterThan(0u));
            Assert.That(ordinary.Submitted.TailCertificate.Phase,
                Is.EqualTo(SimpleDdgiTransportPhase.AcceleratedSolve));
            Assert.That(ordinary.Submitted.FrameSerialsValid, Is.True);
            Assert.That(ordinary.Submitted.ActiveProbeCount,
                Is.EqualTo(AuditActiveProbeCount));
            Assert.That(ordinary.Submitted.AuditPhysicalProbeCount,
                Is.EqualTo(AuditPhysicalProbeCount));
            Assert.That(ordinary.Submitted.SourceLightingGeneration,
                Is.EqualTo(2u));
            Assert.That(ordinary.Submitted.AdmittedSourceCohortGeneration,
                Is.EqualTo(2u));
            Assert.That(ordinary.Submitted.LivePropagationSourceGeneration,
                Is.Zero);
            Assert.That(ordinary.Submitted.CachedSweepCount, Is.EqualTo(2));
            Assert.That(ordinary.GpuAcceleratedSolveMicroseconds,
                Is.EqualTo(100 + firstEdge + SolveOffset));
            Assert.That(ordinary.GpuSchedulerTailAdmitMicroseconds,
                Is.EqualTo(200 + firstEdge + SolveOffset));
            Assert.That(ordinary.GpuSchedulerEmitMicroseconds,
                Is.EqualTo(300 + firstEdge + SolveOffset));
            Assert.That(ordinary.GpuSchedulerCommitMicroseconds,
                Is.EqualTo(400 + firstEdge + SolveOffset));
            Assert.That(ordinary.GpuUrgentRelightTimingAvailable, Is.False);
            Assert.That(ordinary.GpuUrgentRelightMicroseconds, Is.Zero);
            Assert.That(ordinary.GpuDdgiTotalMicroseconds,
                Is.EqualTo(5_000 + firstEdge + SolveOffset));
            Assert.That(ordinary.SchedulerAcceptedWorkCount,
                Is.EqualTo((uint)(50 + firstEdge + SolveOffset)));
            Assert.That(ordinary.SchedulerCompactedCandidateCount,
                Is.EqualTo((uint)(70 + firstEdge + SolveOffset)));
            Assert.That(ordinary.SchedulerActiveWorkCount,
                Is.EqualTo((uint)(10 + firstEdge + SolveOffset)));
            Assert.That(ordinary.SchedulerSolveEpoch,
                Is.EqualTo(ordinary.Submitted.TailCertificate.SolveEpoch));
            Assert.That(ordinary.SchedulerSolveParticipantCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(ordinary.SchedulerSolveVisitedCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(ordinary.SchedulerActiveSourceMutationCount, Is.Zero);
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(ordinary.SchedulerFeedbackFrameSerial +
                    (ulong)RenderingConstants.FramesInFlight));
            Assert.That(trigger.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(AdvanceNonZero(
                    ordinary.SchedulerFeedbackTransportGeneration)));
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
            Assert.That(auditAwait.Submitted.IntendedGpuPasses,
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
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
    public void AuditFreezesLaggedPublishedGenerationAndCertificateCatchesUp()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 72,
            secondCertificate: 190,
            additionalMutatingDrainOrigin: 65);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        SimpleDdgiCompletedFrameEvidence[] frozen = frames
            .Select(static frame => frame.Completed)
            .Where(static completed => completed.Submitted.TailCertificate.Phase ==
                SimpleDdgiTransportPhase.AuditFrozen)
            .ToArray();
        SimpleDdgiCompletedFrameEvidence certified = frames[^1].Completed;
        uint laggedPublishedGeneration = frozen[0].Submitted
            .PublishedPropagationGeneration;
        Assert.Multiple(() =>
        {
            Assert.That(frozen, Has.Length.EqualTo(3));
            Assert.That(frozen.All(completed => completed.Submitted
                .PublishedPropagationGeneration == laggedPublishedGeneration),
                Is.True);
            Assert.That(frozen.All(completed => completed.Submitted
                .TransportGeneration == completed.Submitted.TailCertificate
                    .Generations.CanonicalField), Is.True);
            Assert.That(AdvanceNonZero(laggedPublishedGeneration),
                Is.EqualTo(frozen[0].Submitted.TransportGeneration));
            Assert.That(certified.Submitted.PublishedPropagationGeneration,
                Is.EqualTo(certified.Submitted.TransportGeneration));
            Assert.That(certified.Submitted.PublishedPropagationGeneration,
                Is.Not.EqualTo(laggedPublishedGeneration));
        });
    }

    [TestCase(5u, 0, 5u)]
    [TestCase(5u, 1, 6u)]
    [TestCase(uint.MaxValue, 1, 1u)]
    public void CertifiedClosureAcceptsSameOrOneStepLogicalVolumeGeneration(
        uint frozenVolumeGeneration,
        int certificateAdvanceCount,
        uint expectedCertificateGeneration)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            auditVolumeResourceGeneration: frozenVolumeGeneration,
            certificateVolumeAdvanceCount: certificateAdvanceCount);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        Assert.Multiple(() =>
        {
            Assert.That(frames.Skip(FirstAuditOffset).Take(3).All(frame =>
                    frame.Completed.Submitted.VolumeResourceGeneration ==
                    frozenVolumeGeneration), Is.True);
            Assert.That(frames[^1].Completed.Submitted.VolumeResourceGeneration,
                Is.EqualTo(expectedCertificateGeneration));
        });
    }

    [TestCase(2, false)]
    [TestCase(0, true)]
    public void CertifiedClosureRejectsInvalidLogicalVolumeTransition(
        int certificateAdvanceCount,
        bool certificateVolumeZero)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            certificateVolumeAdvanceCount: certificateAdvanceCount,
            certificateVolumeZero: certificateVolumeZero);

        AssertUnavailable(
            report,
            "certificate does not close the exact observed dispatch/await lifecycle");
    }

    [TestCase("frozen-canonical", 68)]
    [TestCase("frozen-source", 68)]
    [TestCase("frozen-physical", 68)]
    [TestCase("frozen-queue", 68)]
    [TestCase("frozen-logical-volume", 68)]
    [TestCase("frozen-published", 68)]
    [TestCase("frozen-published-zero", 68)]
    public void AuditFrozenRowsRejectChangedSubmittedIdentity(
        string mutation,
        int mutationOrigin)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongCertificateFeedbackOrigin: mutationOrigin,
            wrongCertificateFeedbackField: mutation);

        AssertUnavailable(report, "changed its frozen audit plan");
    }

    [TestCase("frozen-canonical")]
    [TestCase("frozen-source")]
    [TestCase("certified-physical")]
    [TestCase("frozen-queue")]
    [TestCase("frozen-published")]
    public void CertifiedRowRejectsChangedFrozenSubmittedIdentity(string mutation)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongCertificateFeedbackOrigin: 70,
            wrongCertificateFeedbackField: mutation);

        AssertUnavailable(
            report,
            "retained a Certified tail row without an exact current numerical/lifecycle certificate");
    }

    [Test]
    public void NonGreedyThreeChunkAuditFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            nonGreedyAuditOrigin: 67);

        AssertUnavailable(report, "greedy dispatch required 2");
    }

    [Test]
    public void MissingCompletedFrameFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190);

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
            firstCertificate: 70,
            secondCertificate: 190);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        SimpleDdgiCompletedFrameEvidence solve = frames[SolveOffset].Completed;
        SimpleDdgiCompletedFrameEvidence trigger =
            frames[TriggerOffset].Completed;
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
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(solve.SchedulerFeedbackFrameSerial +
                    (ulong)RenderingConstants.FramesInFlight));
            Assert.That(frames[FirstAuditOffset].Completed.Submitted.SchedulerFrameSerial,
                Is.EqualTo(trigger.SchedulerFeedbackFrameSerial +
                    (ulong)RenderingConstants.FramesInFlight));
            Assert.That(trigger.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(AdvanceNonZero(
                    solve.SchedulerFeedbackTransportGeneration)));
            Assert.That(trigger.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(tail.Generations.CanonicalField));
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
    public void TransportGenerationReplayAcceptsWrapAndExtraDrainMutation()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 72,
            secondCertificate: 190,
            firstWindowInitialTransportGeneration: uint.MaxValue - 1u,
            additionalMutatingDrainOrigin: 65);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        SimpleDdgiCompletedFrameEvidence solve = frames[SolveOffset].Completed;
        SimpleDdgiCompletedFrameEvidence extraMutation = frames[5].Completed;
        SimpleDdgiCompletedFrameEvidence inFlightPredecessor =
            frames[6].Completed;
        SimpleDdgiCompletedFrameEvidence trigger = frames[7].Completed;
        SimpleDdgiTailCertificateFrameEvidence tail =
            frames[^1].Completed.Submitted.TailCertificate;

        Assert.Multiple(() =>
        {
            Assert.That(solve.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(uint.MaxValue));
            Assert.That(extraMutation.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(1u));
            Assert.That(extraMutation.SchedulerPublishedWorkCount,
                Is.GreaterThan(0u));
            Assert.That(extraMutation.SchedulerActiveCanonicalMutationCount,
                Is.GreaterThan(0u));
            Assert.That(extraMutation.Submitted.LivePropagationSourceGeneration,
                Is.EqualTo(extraMutation.Submitted.SourceLightingGeneration),
                "Live ownership is guaranteed at solve + FramesInFlight even " +
                "when later mutating drain packets delay the trigger.");
            Assert.That(inFlightPredecessor
                .SchedulerFeedbackTransportGeneration, Is.EqualTo(1u));
            Assert.That(inFlightPredecessor
                .SchedulerActiveCanonicalMutationCount, Is.GreaterThan(0u));
            Assert.That(trigger.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(2u));
            Assert.That(tail.Generations.CanonicalField, Is.EqualTo(2u));
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(solve.SchedulerFeedbackFrameSerial + 4UL));
            Assert.That(frames[9].Completed.Submitted.SchedulerFrameSerial,
                Is.EqualTo(trigger.SchedulerFeedbackFrameSerial + 2UL));
        });
    }

    [Test]
    public void TransportGenerationReplayAcceptsNominalMaxToOneWrap()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            firstWindowInitialTransportGeneration: uint.MaxValue - 1u);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        Assert.Multiple(() =>
        {
            Assert.That(frames[SolveOffset].Completed
                .SchedulerFeedbackTransportGeneration,
                Is.EqualTo(uint.MaxValue));
            Assert.That(frames[TriggerOffset].Completed
                .SchedulerFeedbackTransportGeneration, Is.EqualTo(1u));
            Assert.That(frames[^1].Completed.Submitted.TailCertificate
                .Generations.CanonicalField, Is.EqualTo(1u));
        });
    }

    [Test]
    public void TransportGenerationReplayAcceptsExactPredecessorSolveWitness()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 71,
            secondCertificate: 190,
            firstWindowPredecessorSolve: true);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine,
                report.DdgiTransientEvidence.Failures));
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames =
            report.DdgiTransientEvidence.Windows[0].Frames;
        SimpleDdgiCompletedFrameEvidence precedingMutation =
            frames[SolveOffset].Completed;
        SimpleDdgiCompletedFrameEvidence solve =
            frames[SolveOffset + 1].Completed;
        SimpleDdgiCompletedFrameEvidence trigger =
            frames[TriggerOffset + 1].Completed;
        SimpleDdgiTailCertificateFrameEvidence tail =
            frames[^1].Completed.Submitted.TailCertificate;

        Assert.Multiple(() =>
        {
            Assert.That(precedingMutation.SchedulerSolveVisitedCount,
                Is.EqualTo(AuditParticipantCount - 1u));
            Assert.That(precedingMutation
                .SchedulerActiveCanonicalMutationCount, Is.GreaterThan(0u));
            Assert.That(solve.SchedulerSolveVisitedCount,
                Is.EqualTo(AuditParticipantCount));
            Assert.That(solve.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(precedingMutation
                    .SchedulerFeedbackTransportGeneration));
            Assert.That(solve.SchedulerFeedbackTransportGeneration,
                Is.Not.EqualTo(tail.Generations.CanonicalField));
            Assert.That(trigger.SchedulerFeedbackTransportGeneration,
                Is.EqualTo(tail.Generations.CanonicalField));
            Assert.That(trigger.SchedulerFeedbackFrameSerial,
                Is.EqualTo(solve.SchedulerFeedbackFrameSerial + 2UL));
            Assert.That(frames[FirstAuditOffset + 1].Completed.Submitted
                    .SchedulerFrameSerial,
                Is.EqualTo(trigger.SchedulerFeedbackFrameSerial + 2UL));
        });
    }

    [Test]
    public void WindowOverlappingNextGenerationEdgeFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: -1,
            secondCertificate: 190);

        AssertUnavailable(report, "overlapped the next source-lighting edge");
    }

    [Test]
    public void UncompletedFinalWindowFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
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
            firstCertificate: 72,
            secondCertificate: 192);

        AssertUnavailable(report, "expected [60,61]");
    }

    [Test]
    public void NonSuccessorGenerationSequenceFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            forceBadGenerationSequence: true);

        AssertUnavailable(report, "expected wrap-safe +1 generation");
    }

    [Test]
    public void WrapSafeSuccessorGenerationSequenceIsAccepted()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190,
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
        int missingPassOrigin = missingPass ==
            SimpleDdgiGpuPassMask.AcceleratedSolve
                ? 60 + SolveOffset
                : 60;
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            missingPassOrigin: missingPassOrigin,
            missingCompletedPass: missingPass,
            legacyPathOrigin: -1);

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
            firstCertificate: 70,
            secondCertificate: 190,
            omitUrgentRelightOrigin: 60);

        AssertUnavailable(report, "no urgent-relight parent timing scope");
    }

    [Test]
    public void MissingTimestampAdmissionFailsClosedEvenWhenResultExists()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190,
            missingPassOrigin: 67,
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
            firstCertificate: 70,
            secondCertificate: 190,
            falseAuditWorkOnAwaitOrigin: 69);

        AssertUnavailable(report, "advanced the frozen audit by 0 chunks");
    }

    [Test]
    public void AuditDispatchThatFalselySuppliesOrdinaryWorkFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            falseOrdinaryWorkOnAuditDispatchOrigin: 67);

        AssertUnavailable(report, "scheduler/solve/publication timing scopes");
    }

    [Test]
    public void CompleteLegacyTransportPathMayOmitAcceleratedAndDirectionalPasses()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
    public void AcceleratedSolvePhaseRejectsLegacyTransportPath()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            legacyPathOrigin: 60 + SolveOffset);

        AssertUnavailable(report, "transport pass mask does not match tail phase");
    }

    [Test]
    public void NonContiguousRouteSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            shiftedRouteSerialIndex: 61);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void WrongSubmittedFrameSlotFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongSlotOrigin: 60);

        AssertUnavailable(report, "expected renderer slot");
    }

    [Test]
    public void WrongCompletionDelayFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            earlyCompletionOrigin: 60);

        AssertUnavailable(report, "expected exact FramesInFlight delay");
    }

    [Test]
    public void DuplicateSchedulerSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongSchedulerSerialOrigin: 60);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void SchedulerSerialCanHaveIndependentDisabledPrehistoryOffset()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190,
            skippedSchedulerSerialOrigin: 20);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void SchedulerSerialSentinelWrapFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            schedulerSerialBase: ulong.MaxValue - 60UL);

        AssertUnavailable(report, "cannot advance without entering a sentinel");
    }

    [Test]
    public void InvalidSchedulerSerialSentinelFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            invalidSchedulerSerialOrigin: 20);

        AssertUnavailable(report, "retained invalid renderer/scheduler serials");
    }

    [Test]
    public void InactiveMeasuredRouteFrameFailsClosedIncludingUncompletedTail()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190,
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
            firstCertificate: 70,
            secondCertificate: 190,
            legacyChannelCertificateOrigin: mutation == "legacy-channel"
                ? 70
                : -1,
            falsePhysicalCountCertificateOrigin:
                mutation == "physical-cardinality" ? 70 : -1,
            falsePopulationCertificateOrigin:
                mutation == "population-partition" ? 70 : -1,
            wrongCertificateFeedbackOrigin: mutation.StartsWith(
                "feedback-",
                StringComparison.Ordinal) ? 63 : -1,
            wrongCertificateFeedbackField: mutation,
            equalFinalCertificateOrigin: mutation == "equal-final" ? 70 : -1);

        AssertUnavailable(
            report,
            mutation.StartsWith("feedback-", StringComparison.Ordinal)
                ? "solve-feedback packet is not the exact complete"
                : "Certified tail row without an exact");
    }

    [TestCase("trigger-epoch", 65, "audit-trigger feedback packet")]
    [TestCase("trigger-participant", 65, "audit-trigger feedback packet")]
    [TestCase("trigger-canonical-mutation", 65, "audit-trigger feedback packet")]
    [TestCase("trigger-source-mutation", 65, "audit-trigger feedback packet")]
    [TestCase("trigger-blocking-source", 65, "audit-trigger feedback packet")]
    [TestCase("trigger-missing", 65, "missing the exact earlier audit-trigger")]
    [TestCase("solve-missing", 63, "missing the exact earlier solve-feedback")]
    [TestCase("solve-topology", 63, "solve-feedback packet is not the exact complete")]
    [TestCase("solve-published", 63, "solve-feedback packet is not the exact complete")]
    [TestCase("solve-canonical-mutation", 63, "solve-feedback packet is not the exact complete")]
    [TestCase("solve-source-mutation", 63, "solve-feedback packet is not the exact complete")]
    [TestCase("source-repair-admitted-current", 61, "invalid delayed SourceRepair ownership/epoch state")]
    [TestCase("source-repair-live-current", 61, "invalid delayed SourceRepair ownership/epoch state")]
    [TestCase("source-repair-solve-epoch", 60, "invalid delayed SourceRepair ownership/epoch state")]
    [TestCase("source-repair-solve-visited", 60, "invalid delayed SourceRepair ownership/epoch state")]
    [TestCase("source-repair-admission-participant", 61, "exact delayed source-admission feedback packet")]
    [TestCase("source-repair-admission-blocking", 61, "exact delayed source-admission feedback packet")]
    [TestCase("source-repair-page-pass", 60, "phase-specific Bistro DDGI pass mask")]
    [TestCase("source-repair-foliage-pass", 60, "phase-specific Bistro DDGI pass mask")]
    [TestCase("audit-await-page-pass", 69, "phase-specific Bistro DDGI pass mask")]
    [TestCase("source-repair-accelerated-path", 60, "transport pass mask does not match tail phase")]
    [TestCase("accelerated-admitted-missing", 63, "invalid admitted/live source generation")]
    [TestCase("accelerated-live-early", 63, "first AcceleratedSolve submission claimed live")]
    [TestCase("trigger-admitted-missing", 65, "invalid admitted/live source generation")]
    [TestCase("trigger-live-missing", 65, "audit trigger did not retain the current")]
    [TestCase("trigger-queue", 65, "audit-trigger feedback packet")]
    [TestCase("feedback-reordered", 70, "Certified tail row without an exact")]
    [TestCase("replay-zero", 61, "zero transport generation during replay")]
    [TestCase("replay-skipped", 61, "neither current")]
    [TestCase("post-trigger-canonical-mutation", 66, "post-trigger in-flight feedback")]
    [TestCase("post-trigger-source-mutation", 66, "post-trigger in-flight feedback")]
    [TestCase("post-trigger-blocking-source", 66, "post-trigger in-flight feedback")]
    public void TwoPacketAuditFeedbackProvenanceFailsClosed(
        string mutation,
        int mutationOrigin,
        string expectedFailure)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongCertificateFeedbackOrigin: mutationOrigin,
            wrongCertificateFeedbackField: mutation);

        AssertUnavailable(report, expectedFailure);
    }

    [TestCase("certified-participant")]
    [TestCase("certified-generation")]
    [TestCase("certified-canonical-mutation")]
    [TestCase("certified-source-mutation")]
    [TestCase("certified-blocking-source")]
    public void CertifiedClosureRequiresCurrentQuiescentFeedback(string mutation)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 70,
            secondCertificate: 190,
            wrongCertificateFeedbackOrigin: 70,
            wrongCertificateFeedbackField: mutation);

        AssertUnavailable(
            report,
            "Certified closure is not an exact quiescent");
    }

    [Test]
    public void OrdinaryMutationAfterCompletedAuditRequiresFreshLifecycle()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 71,
            secondCertificate: 190,
            interposedPostAuditMutationOrigin: 70);

        AssertUnavailable(
            report,
            "resumed ordinary work after the frozen audit began");
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
        int inactiveRouteIndex = -1,
        int additionalMutatingDrainOrigin = -1,
        uint? firstWindowInitialTransportGeneration = null,
        bool firstWindowPredecessorSolve = false,
        int interposedPostAuditMutationOrigin = -1,
        uint auditVolumeResourceGeneration = 5u,
        int certificateVolumeAdvanceCount = 0,
        bool certificateVolumeZero = false,
        int nonGreedyAuditOrigin = -1)
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
                bool firstInterposedPostAuditMutation =
                    firstCertificate >= 0 &&
                    interposedPostAuditMutationOrigin == firstCertificate - 1;
                bool secondInterposedPostAuditMutation =
                    secondCertificate >= 0 &&
                    interposedPostAuditMutationOrigin == secondCertificate - 1;
                int firstAuditOrigin = firstCertificate -
                    (firstInterposedPostAuditMutation ? 4 : 3);
                int secondAuditOrigin = secondCertificate -
                    (secondInterposedPostAuditMutation ? 4 : 3);
                bool auditFrozen =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstAuditOrigin &&
                    completedOrigin <= firstAuditOrigin + 2 ||
                    secondCertificate >= 0 &&
                    completedOrigin >= secondAuditOrigin &&
                    completedOrigin <= secondAuditOrigin + 2;
                bool auditAwait =
                    firstCertificate >= 0 &&
                    completedOrigin == firstAuditOrigin + 2 ||
                    secondCertificate >= 0 &&
                    completedOrigin == secondAuditOrigin + 2;
                int enclosingCertificate =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstEdge &&
                    completedOrigin <= firstCertificate
                        ? firstCertificate
                        : secondCertificate >= 0 &&
                          completedOrigin >= secondEdge &&
                          completedOrigin <= secondCertificate
                            ? secondCertificate
                            : syntheticOrigin + CertificateOffset;
                int enclosingSourceEdgeOrigin =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstEdge &&
                    completedOrigin <= firstCertificate
                        ? firstEdge
                        : secondCertificate >= 0 &&
                          completedOrigin >= secondEdge &&
                          completedOrigin <= secondCertificate
                            ? secondEdge
                            : syntheticOrigin;
                int enclosingSolveOrigin =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstEdge &&
                    completedOrigin <= firstCertificate
                        ? firstEdge + SolveOffset +
                          (firstWindowPredecessorSolve ? 1 : 0)
                        : secondCertificate >= 0 &&
                          completedOrigin >= secondEdge &&
                          completedOrigin <= secondCertificate
                            ? secondEdge + SolveOffset
                            : syntheticOrigin;
                int enclosingFirstAuditOrigin =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstEdge &&
                    completedOrigin <= firstCertificate
                        ? firstAuditOrigin
                        : secondCertificate >= 0 &&
                          completedOrigin >= secondEdge &&
                          completedOrigin <= secondCertificate
                            ? secondAuditOrigin
                            : syntheticOrigin + FirstAuditOffset;
                int enclosingAdditionalMutationOrigin =
                    additionalMutatingDrainOrigin >= enclosingSolveOrigin &&
                    additionalMutatingDrainOrigin <= enclosingCertificate
                        ? additionalMutatingDrainOrigin
                        : -1;
                uint initialTransportGeneration =
                    firstWindowInitialTransportGeneration.HasValue &&
                    enclosingSourceEdgeOrigin == firstEdge
                        ? firstWindowInitialTransportGeneration.Value
                        : AdvanceNonZero(unchecked(sourceGeneration + 100u));
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
                    certificateOrigin: enclosingCertificate,
                    firstAuditOrigin: enclosingFirstAuditOrigin,
                    sourceEdgeOrigin: enclosingSourceEdgeOrigin,
                    solveOrigin: enclosingSolveOrigin,
                    additionalMutatingDrainOrigin:
                        enclosingAdditionalMutationOrigin,
                    initialTransportGeneration: initialTransportGeneration,
                    predecessorSolve: firstWindowPredecessorSolve &&
                        enclosingSolveOrigin == firstEdge + SolveOffset + 1,
                    interposedPostAuditMutationOrigin:
                        interposedPostAuditMutationOrigin,
                    auditVolumeResourceGeneration:
                        auditVolumeResourceGeneration,
                    certificateVolumeAdvanceCount:
                        certificateVolumeAdvanceCount,
                    certificateVolumeZero: certificateVolumeZero);
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
            if (completedOrigin == nonGreedyAuditOrigin)
            {
                completed = MutateAuditSubmittedChunkCount(
                    completed,
                    submittedChunkCount: 1u);
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
                    "solve-published" => completed with
                    {
                        SchedulerPublishedWorkCount = 0u
                    },
                    "solve-canonical-mutation" => completed with
                    {
                        SchedulerActiveCanonicalMutationCount = 0u
                    },
                    "solve-source-mutation" => completed with
                    {
                        SchedulerActiveSourceMutationCount = 1u
                    },
                    "source-repair-admitted-current" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            AdmittedSourceCohortGeneration =
                                completed.Submitted.SourceLightingGeneration
                        }
                    },
                    "source-repair-live-current" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            LivePropagationSourceGeneration =
                                completed.Submitted.SourceLightingGeneration
                        }
                    },
                    "source-repair-solve-epoch" => completed with
                    {
                        SchedulerSolveEpoch = 1u
                    },
                    "source-repair-solve-visited" => completed with
                    {
                        SchedulerSolveVisitedCount = 1u
                    },
                    "source-repair-admission-participant" => completed with
                    {
                        SchedulerSolveParticipantCount =
                            completed.SchedulerSolveParticipantCount - 1u
                    },
                    "source-repair-admission-blocking" => completed with
                    {
                        SchedulerBlockingTailSourceWorkCount = 1u
                    },
                    "source-repair-page-pass" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            IntendedGpuPasses = completed.Submitted
                                .IntendedGpuPasses |
                                SimpleDdgiGpuPassMask.PageDemand,
                            AdmittedGpuTimingPasses = completed.Submitted
                                .AdmittedGpuTimingPasses |
                                SimpleDdgiGpuPassMask.PageDemand
                        },
                        CompletedGpuTimingPasses =
                            completed.CompletedGpuTimingPasses |
                            SimpleDdgiGpuPassMask.PageDemand
                    },
                    "source-repair-foliage-pass" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            IntendedGpuPasses = completed.Submitted
                                .IntendedGpuPasses |
                                SimpleDdgiGpuPassMask.FoliageProxyGeneration,
                            AdmittedGpuTimingPasses = completed.Submitted
                                .AdmittedGpuTimingPasses |
                                SimpleDdgiGpuPassMask.FoliageProxyGeneration
                        },
                        CompletedGpuTimingPasses =
                            completed.CompletedGpuTimingPasses |
                            SimpleDdgiGpuPassMask.FoliageProxyGeneration
                    },
                    "audit-await-page-pass" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            IntendedGpuPasses = SimpleDdgiGpuPassMask.PageDemand,
                            AdmittedGpuTimingPasses =
                                SimpleDdgiGpuPassMask.PageDemand
                        },
                        CompletedGpuTimingPasses =
                            SimpleDdgiGpuPassMask.PageDemand
                    },
                    "source-repair-accelerated-path" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            IntendedGpuPasses = (completed.Submitted
                                .IntendedGpuPasses &
                                ~(SimpleDdgiGpuPassMask.Transport |
                                  SimpleDdgiGpuPassMask.Blend)) |
                                SimpleDdgiGpuPassMask.AcceleratedSolve,
                            AdmittedGpuTimingPasses = (completed.Submitted
                                .AdmittedGpuTimingPasses &
                                ~(SimpleDdgiGpuPassMask.Transport |
                                  SimpleDdgiGpuPassMask.Blend)) |
                                SimpleDdgiGpuPassMask.AcceleratedSolve,
                            CachedSweepCount = 1
                        },
                        CompletedGpuTimingPasses = (completed
                            .CompletedGpuTimingPasses &
                            ~(SimpleDdgiGpuPassMask.Transport |
                              SimpleDdgiGpuPassMask.Blend)) |
                            SimpleDdgiGpuPassMask.AcceleratedSolve,
                        GpuAcceleratedSolveTimingAvailable = true,
                        GpuAcceleratedSolveMicroseconds = 123
                    },
                    "accelerated-admitted-missing" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            AdmittedSourceCohortGeneration = 0u
                        }
                    },
                    "accelerated-live-early" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            LivePropagationSourceGeneration =
                                completed.Submitted.SourceLightingGeneration
                        }
                    },
                    "trigger-admitted-missing" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            AdmittedSourceCohortGeneration = 0u
                        }
                    },
                    "trigger-live-missing" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            LivePropagationSourceGeneration = 0u
                        }
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
                    "replay-zero" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportGeneration = 0u,
                            PublishedPropagationGeneration = 0u
                        },
                        SchedulerFeedbackTransportGeneration = 0u
                    },
                    "replay-skipped" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportGeneration = AdvanceNonZero(AdvanceNonZero(
                                completed.Submitted.TransportGeneration)),
                            PublishedPropagationGeneration =
                                AdvanceNonZero(AdvanceNonZero(
                                    completed.Submitted.TransportGeneration))
                        },
                        SchedulerFeedbackTransportGeneration =
                            AdvanceNonZero(AdvanceNonZero(
                                completed.Submitted.TransportGeneration))
                    },
                    "post-trigger-canonical-mutation" => completed with
                    {
                        SchedulerPublishedWorkCount = 1u,
                        SchedulerActiveCanonicalMutationCount = 1u
                    },
                    "post-trigger-source-mutation" => completed with
                    {
                        SchedulerActiveSourceMutationCount = 1u
                    },
                    "post-trigger-blocking-source" => completed with
                    {
                        SchedulerBlockingTailSourceWorkCount = 1u
                    },
                    "certified-participant" => completed with
                    {
                        SchedulerSolveParticipantCount =
                            completed.SchedulerSolveParticipantCount - 1u
                    },
                    "certified-generation" => completed with
                    {
                        SchedulerFeedbackTransportGeneration = AdvanceNonZero(
                            completed.SchedulerFeedbackTransportGeneration)
                    },
                    "certified-canonical-mutation" => completed with
                    {
                        SchedulerPublishedWorkCount = 1u,
                        SchedulerActiveCanonicalMutationCount = 1u
                    },
                    "certified-source-mutation" => completed with
                    {
                        SchedulerActiveSourceMutationCount = 1u
                    },
                    "certified-blocking-source" => completed with
                    {
                        SchedulerBlockingTailSourceWorkCount = 1u
                    },
                    "frozen-canonical" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportGeneration = AdvanceNonZero(
                                completed.Submitted.TransportGeneration)
                        }
                    },
                    "frozen-source" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            SourceLightingGeneration = AdvanceNonZero(
                                completed.Submitted.SourceLightingGeneration)
                        }
                    },
                    "frozen-physical" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportTopologyGeneration = 7u
                        }
                    },
                    "frozen-queue" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            QueueTransactionGeneration = 15u,
                            SchedulerResourceGeneration = 15u
                        }
                    },
                    "frozen-logical-volume" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            VolumeResourceGeneration = AdvanceNonZero(
                                completed.Submitted.VolumeResourceGeneration)
                        }
                    },
                    "frozen-published" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            PublishedPropagationGeneration = AdvanceNonZero(
                                completed.Submitted.PublishedPropagationGeneration)
                        }
                    },
                    "frozen-published-zero" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            PublishedPropagationGeneration = 0u
                        }
                    },
                    "certified-physical" => completed with
                    {
                        Submitted = completed.Submitted with
                        {
                            TransportTopologyGeneration = 7u
                        },
                        SchedulerFeedbackTransportTopologyGeneration = 7u
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

    private static uint PreviousNonZero(uint generation) =>
        generation == 1u ? uint.MaxValue : generation - 1u;

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
        int certificateOrigin,
        int firstAuditOrigin,
        int sourceEdgeOrigin,
        int solveOrigin,
        int additionalMutatingDrainOrigin,
        uint initialTransportGeneration,
        bool predecessorSolve,
        int interposedPostAuditMutationOrigin,
        uint auditVolumeResourceGeneration,
        int certificateVolumeAdvanceCount,
        bool certificateVolumeZero)
    {
        ulong frameSerial = originIndex >= 0
            ? routeSerialBase + (ulong)originIndex
            : 9_000UL + (ulong)(originIndex + 100);
        ulong schedulerFrameSerial = originIndex >= 0
            ? unchecked(schedulerSerialBase + (ulong)originIndex)
            : 8_000UL + (ulong)(originIndex + 100);
        uint transportGeneration = initialTransportGeneration;
        int acceleratedSolveOrigin = predecessorSolve
            ? solveOrigin - 1
            : solveOrigin;
        bool hasSourceRepairPhase = sourceEdgeOrigin < acceleratedSolveOrigin;
        if (hasSourceRepairPhase &&
            originIndex >= sourceEdgeOrigin +
                RenderingConstants.FramesInFlight)
        {
            transportGeneration = AdvanceNonZero(transportGeneration);
        }
        int firstCanonicalAdvanceOrigin = predecessorSolve
            ? solveOrigin - 1
            : solveOrigin;
        if (originIndex >= firstCanonicalAdvanceOrigin +
            RenderingConstants.FramesInFlight)
        {
            transportGeneration = AdvanceNonZero(transportGeneration);
        }
        if (additionalMutatingDrainOrigin >= 0 &&
            originIndex >= additionalMutatingDrainOrigin +
                RenderingConstants.FramesInFlight)
        {
            transportGeneration = AdvanceNonZero(transportGeneration);
        }
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
        SimpleDdgiTransportPhase phase = certificate
            ? SimpleDdgiTransportPhase.Certified
            : auditFrozen
                ? SimpleDdgiTransportPhase.AuditFrozen
                : originIndex < acceleratedSolveOrigin
                    ? SimpleDdgiTransportPhase.SourceRepair
                    : SimpleDdgiTransportPhase.AcceleratedSolve;
        bool useLegacyPath = legacyPath ||
            phase != SimpleDdgiTransportPhase.AcceleratedSolve;
        SimpleDdgiGpuPassMask passMask = auditFrozen
            ? auditAwait
                ? SimpleDdgiGpuPassMask.None
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
            : schedulerFrameSerial + CertificateOffset;
        ulong firstAuditSchedulerFrameSerial = originIndex >= 0
            ? unchecked(schedulerSerialBase + (ulong)firstAuditOrigin)
            : certificateSchedulerFrameSerial - 3UL;
        ulong solveFeedbackFrameSerial = originIndex >= 0
            ? unchecked(schedulerSerialBase + (ulong)solveOrigin)
            : schedulerFrameSerial;
        ulong triggerFeedbackFrameSerial = firstAuditSchedulerFrameSerial -
            (ulong)RenderingConstants.FramesInFlight;
        bool solveFeedbackWitness =
            schedulerFrameSerial == solveFeedbackFrameSerial;
        bool sourceRepairMutation = hasSourceRepairPhase &&
            originIndex == sourceEdgeOrigin;
        bool canonicalMutation = sourceRepairMutation ||
            solveFeedbackWitness ||
            (predecessorSolve && originIndex == solveOrigin - 1) ||
            originIndex == additionalMutatingDrainOrigin ||
            (additionalMutatingDrainOrigin >= 0 &&
             originIndex == additionalMutatingDrainOrigin + 1) ||
            originIndex == interposedPostAuditMutationOrigin;
        bool drainSubmission = originIndex >=
            solveOrigin + RenderingConstants.FramesInFlight;
        SimpleDdgiTailCertificateFrameEvidence tail = CreateTail(
            generations,
            certificate,
            auditFrozen,
            auditAwait,
            phase,
            schedulerFrameSerial,
            firstAuditSchedulerFrameSerial,
            solveFeedbackFrameSerial,
            triggerFeedbackFrameSerial);
        uint certificateVolumeResourceGeneration =
            auditVolumeResourceGeneration;
        for (int advance = 0; advance < certificateVolumeAdvanceCount; advance++)
        {
            certificateVolumeResourceGeneration = AdvanceNonZero(
                certificateVolumeResourceGeneration);
        }
        uint submittedVolumeResourceGeneration = auditFrozen
            ? auditVolumeResourceGeneration
            : certificate
                ? certificateVolumeZero
                    ? 0u
                    : certificateVolumeResourceGeneration
                : 5u;
        uint submittedPublishedPropagationGeneration = auditFrozen &&
            additionalMutatingDrainOrigin >= 0
                ? PreviousNonZero(transportGeneration)
                : transportGeneration;
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
            VolumeResourceGeneration = submittedVolumeResourceGeneration,
            TransportTopologyGeneration = 6u,
            SourceLightingGeneration = sourceGeneration,
            AdmittedSourceCohortGeneration =
                phase == SimpleDdgiTransportPhase.SourceRepair
                    ? 0u
                    : sourceGeneration,
            TransportGeneration = transportGeneration,
            PublishedPropagationGeneration =
                submittedPublishedPropagationGeneration,
            LivePropagationSourceGeneration =
                originIndex >= solveOrigin +
                    RenderingConstants.FramesInFlight
                    ? sourceGeneration
                    : 0u,
            SchedulerResourceGeneration = 14u,
            QueueTransactionGeneration = 14u,
            CachedSweepCount =
                phase == SimpleDdgiTransportPhase.AcceleratedSolve ? 2 : 0,
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
            SchedulerFeedbackVolumeResourceGeneration = scheduler
                ? submittedVolumeResourceGeneration
                : 0u,
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
                ? canonicalMutation
                    ? (uint)(30 + exactIndex)
                    : 0u
                : 0u,
            SchedulerActiveWorkCount = scheduler
                ? (uint)(10 + exactIndex)
                : 0u,
            SchedulerCachedParticipantCount = scheduler
                ? (uint)(5 + exactIndex)
                : 0u,
            SchedulerSolveParticipantCount = scheduler
                ? sourceRepairMutation
                    ? AuditParticipantCount - 128u
                    : AuditParticipantCount
                : 0u,
            SchedulerSolveVisitedCount = scheduler
                ? phase == SimpleDdgiTransportPhase.SourceRepair ||
                  certificate || drainSubmission
                    ? 0u
                    : predecessorSolve && originIndex < solveOrigin
                        ? AuditParticipantCount - 1u
                        : AuditParticipantCount
                : 0u,
            SchedulerSolveEpoch = scheduler
                ? phase == SimpleDdgiTransportPhase.SourceRepair ||
                  certificate || drainSubmission
                    ? 0u
                    : generations.Solve
                : 0u,
            SchedulerActiveCanonicalMutationCount = scheduler &&
                canonicalMutation
                    ? 1u
                    : 0u,
            SchedulerActiveSourceMutationCount = sourceRepairMutation
                ? 1u
                : 0u,
            SchedulerBlockingTailSourceWorkCount = sourceRepairMutation
                ? 1u
                : 0u,
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
        SimpleDdgiTransportPhase phase,
        ulong schedulerFrameSerial,
        ulong firstAuditSchedulerSerial,
        ulong solveFeedbackSchedulerSerial,
        ulong triggerFeedbackSchedulerSerial)
    {
        ulong finalAuditSchedulerSerial = firstAuditSchedulerSerial + 1UL;
        uint submittedChunkCount = certificate || auditAwait
            ? AuditChunkCount
            : schedulerFrameSerial == firstAuditSchedulerSerial
                ? 2u
                : AuditChunkCount;
        ulong currentFinalSchedulerSerial = submittedChunkCount == 2u
            ? firstAuditSchedulerSerial
            : finalAuditSchedulerSerial;
        SimpleDdgiTransportTailSummary summary = certificate
            ? CreateCertifiedSummary(
                generations,
                firstAuditSchedulerSerial,
                finalAuditSchedulerSerial,
                solveFeedbackSchedulerSerial,
                triggerFeedbackSchedulerSerial)
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
                    : phase == SimpleDdgiTransportPhase.SourceRepair
                        ? SimpleDdgiTransportCertificationReason.SourceRepairRequired
                        : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete
            };
        return new SimpleDdgiTailCertificateFrameEvidence
        {
            Phase = phase,
            Reason = certificate
                ? SimpleDdgiTransportCertificationReason.Certified
                : auditFrozen
                    ? SimpleDdgiTransportCertificationReason.AuditInProgress
                    : phase == SimpleDdgiTransportPhase.SourceRepair
                        ? SimpleDdgiTransportCertificationReason.SourceRepairRequired
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
        ulong finalAuditSchedulerSerial,
        ulong solveFeedbackSchedulerSerial,
        ulong triggerFeedbackSchedulerSerial) =>
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
            AuditSolveFeedbackFrameSerial = solveFeedbackSchedulerSerial,
            AuditTriggerFeedbackFrameSerial = triggerFeedbackSchedulerSerial,
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
        MutateAuditSubmittedChunkCount(
            SimpleDdgiCompletedFrameEvidence completed,
            uint submittedChunkCount)
    {
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        SimpleDdgiTransportTailSummary summary = tail.Summary with
        {
            FinalFrameSerial = completed.Submitted.SchedulerFrameSerial,
            ChunkCount = submittedChunkCount
        };
        tail = tail with
        {
            AuditFinalSubmissionFrameSerial =
                completed.Submitted.SchedulerFrameSerial,
            AuditSubmittedChunkCount = submittedChunkCount,
            AuditDispatchComplete = submittedChunkCount ==
                tail.AuditPlannedChunkCount,
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
