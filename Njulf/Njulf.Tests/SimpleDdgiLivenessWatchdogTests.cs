using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiLivenessWatchdogTests
{
    [Test]
    public void Watchdog_ReportsFirstUnselectedEligibleStageOnlyAfterDerivedLatency()
    {
        var watchdog = new SimpleDdgiLivenessWatchdog(
            framesInFlight: 1,
            schedulerFeedbackLatencyFrames: 1,
            residencyFeedbackLatencyFrames: 1,
            publicationReadbackLatencyFrames: 1);

        SimpleDdgiLivenessWatchdogResult result = default;
        for (ulong frame = 1; frame <= 6; frame++)
            result = watchdog.Evaluate(CreateTelemetry(frame, eligible: 3u));

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.LatencyBoundFrames, Is.EqualTo(4));
            Assert.That(result.Active, Is.EqualTo(1));
            Assert.That(result.StallDetected, Is.EqualTo(1));
            Assert.That(result.ElapsedFrames, Is.EqualTo(5));
            Assert.That(result.FirstStalledStage,
                Is.EqualTo(SimpleDdgiLivenessStage.EligibleProbeNotSelected));
            Assert.That(result.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.SchedulerDidNotSelect));
        });
    }

    [Test]
    public void Watchdog_ResetsItsWindowWhenACommitOrGenerationChangeOccurs()
    {
        var watchdog = new SimpleDdgiLivenessWatchdog(1, 1, 1, 1);

        _ = watchdog.Evaluate(CreateTelemetry(1, eligible: 1u));
        SimpleDdgiLivenessWatchdogResult beforeProgress =
            watchdog.Evaluate(CreateTelemetry(6, eligible: 1u));
        SimpleDdgiLivenessWatchdogResult afterCommit = watchdog.Evaluate(
            CreateTelemetry(7, eligible: 1u, selected: 1u, dispatched: 1u,
                committed: 1u, published: 1u));
        SimpleDdgiLivenessWatchdogResult afterGenerationChange = watchdog.Evaluate(
            CreateTelemetry(8, eligible: 1u, volumeGeneration: 2u));

        Assert.Multiple(() =>
        {
            Assert.That(beforeProgress.StallDetected, Is.EqualTo(1));
            Assert.That(afterCommit.StallDetected, Is.EqualTo(0));
            Assert.That(afterCommit.ElapsedFrames, Is.EqualTo(0));
            Assert.That(afterGenerationChange.StallDetected, Is.EqualTo(0));
            Assert.That(afterGenerationChange.ElapsedFrames, Is.EqualTo(0));
        });
    }

    [Test]
    public void Watchdog_TreatsSuppressedAndInitializingVisiblePagesAsExplicitBlocks()
    {
        var watchdog = new SimpleDdgiLivenessWatchdog(1, 1, 1, 1);

        SimpleDdgiLivenessWatchdogResult suppressed = watchdog.Evaluate(
            CreateTelemetry(1, visibleDemand: 4u, suppressedDemand: 4u));
        SimpleDdgiLivenessWatchdogResult initializing = watchdog.Evaluate(
            CreateTelemetry(2, visibleDemand: 4u, initializingDemand: 4u));

        Assert.Multiple(() =>
        {
            Assert.That(suppressed.Active, Is.EqualTo(0));
            Assert.That(suppressed.FirstStalledStage,
                Is.EqualTo(SimpleDdgiLivenessStage.DemandWithoutAdmissionCandidate));
            Assert.That(suppressed.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.SuppressedEmptyPages));
            Assert.That(initializing.Active, Is.EqualTo(0));
            Assert.That(initializing.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.InitializingOrUnpublishedPages));
        });
    }

    [Test]
    public void Watchdog_FailsClosedForGenerationRejectionAndTransactionAbort()
    {
        var watchdog = new SimpleDdgiLivenessWatchdog(1, 1, 1, 1);
        SimpleDdgiLivenessWatchdogResult rejected = watchdog.Evaluate(
            CreateTelemetry(
                1,
                eligible: 1u,
                feedbackRejection: SimpleDdgiLivenessBlockReason.GenerationMismatch));
        SimpleDdgiLivenessWatchdogResult aborted = watchdog.Evaluate(
            CreateTelemetry(
                2,
                eligible: 1u,
                transactionAborts: new SimpleDdgiTransactionAbortDeltas(
                    TraceUnavailable: 1u,
                    RelocatePrerequisite: 0u,
                    TransportPrerequisite: 0u,
                    BlendPrerequisite: 0u,
                    PublishPrerequisite: 0u,
                    AcceleratedSolvePrerequisite: 0u,
                    SchedulerModeTransition: 0u,
                    Disabled: 0u,
                    Unknown: 0u)));

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Active, Is.EqualTo(0));
            Assert.That(rejected.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.GenerationMismatch));
            Assert.That(aborted.Active, Is.EqualTo(0));
            Assert.That(aborted.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.TransactionAbort));
        });
    }

    [Test]
    public void Watchdog_ClassifiesSelectedButUndispatchedWork()
    {
        var watchdog = new SimpleDdgiLivenessWatchdog(1, 1, 1, 1);
        SimpleDdgiLivenessWatchdogResult result = default;
        for (ulong frame = 1; frame <= 6; frame++)
            result = watchdog.Evaluate(
                CreateTelemetry(frame, eligible: 1u, selected: 1u));

        Assert.Multiple(() =>
        {
            Assert.That(result.StallDetected, Is.EqualTo(1));
            Assert.That(result.FirstStalledStage,
                Is.EqualTo(SimpleDdgiLivenessStage.SelectedRequestNotDispatched));
            Assert.That(result.BlockingReason,
                Is.EqualTo(SimpleDdgiLivenessBlockReason.NoIndirectDispatch));
        });
    }

    [Test]
    public void ReasonDeltaBanks_CombineWithoutOverflowOrDroppingCauses()
    {
        SimpleDdgiTransactionAbortDeltas aborts =
            SimpleDdgiTransactionAbortDeltas.Combine(
                new SimpleDdgiTransactionAbortDeltas(
                    TraceUnavailable: uint.MaxValue,
                    RelocatePrerequisite: 0u,
                    TransportPrerequisite: 0u,
                    BlendPrerequisite: 0u,
                    PublishPrerequisite: 0u,
                    AcceleratedSolvePrerequisite: 0u,
                    SchedulerModeTransition: 0u,
                    Disabled: 0u,
                    Unknown: 0u),
                new SimpleDdgiTransactionAbortDeltas(
                    TraceUnavailable: 1u,
                    RelocatePrerequisite: 2u,
                    TransportPrerequisite: 0u,
                    BlendPrerequisite: 0u,
                    PublishPrerequisite: 0u,
                    AcceleratedSolvePrerequisite: 0u,
                    SchedulerModeTransition: 0u,
                    Disabled: 0u,
                    Unknown: 0u));
        SimpleDdgiSourceCacheInvalidationDeltas invalidations =
            SimpleDdgiSourceCacheInvalidationDeltas.Combine(
                new SimpleDdgiSourceCacheInvalidationDeltas(
                    LightingSignature: 1u,
                    TransportActivation: 0u,
                    SourceCalibration: 0u,
                    SourceCacheResourceRecreated: 0u,
                    AtlasCleared: 0u,
                    TailRecovery: 0u,
                    Unknown: 0u),
                new SimpleDdgiSourceCacheInvalidationDeltas(
                    LightingSignature: 2u,
                    TransportActivation: 0u,
                    SourceCalibration: 0u,
                    SourceCacheResourceRecreated: 0u,
                    AtlasCleared: 0u,
                    TailRecovery: 4u,
                    Unknown: 0u));

        Assert.Multiple(() =>
        {
            Assert.That(aborts.TraceUnavailable, Is.EqualTo(uint.MaxValue));
            Assert.That(aborts.RelocatePrerequisite, Is.EqualTo(2u));
            Assert.That(aborts.Any, Is.True);
            Assert.That(invalidations.LightingSignature, Is.EqualTo(3u));
            Assert.That(invalidations.TailRecovery, Is.EqualTo(4u));
            Assert.That(invalidations.Any, Is.True);
        });
    }

    private static SimpleDdgiLivenessTelemetry CreateTelemetry(
        ulong frameSerial,
        uint eligible = 0u,
        uint selected = 0u,
        uint dispatched = 0u,
        uint committed = 0u,
        uint published = 0u,
        uint visibleDemand = 0u,
        uint suppressedDemand = 0u,
        uint initializingDemand = 0u,
        uint volumeGeneration = 1u,
        SimpleDdgiLivenessBlockReason feedbackRejection =
            SimpleDdgiLivenessBlockReason.None,
        SimpleDdgiTransactionAbortDeltas transactionAborts = default)
    {
        return SimpleDdgiLivenessTelemetry.Empty with
        {
            Generations = new SimpleDdgiGenerationTuple(
                FrameSerial: frameSerial,
                SchedulerFeedbackFrameSerial: frameSerial,
                ResidencyFeedbackFrameSerial: frameSerial,
                VolumeTableGeneration: volumeGeneration,
                SchedulerArenaGeneration: 1u,
                ResidencyArenaGeneration: 1u,
                SourceLightingGeneration: 1u,
                TransportGeneration: 1u),
            SchedulerFeedbackValid = 1,
            ResidencyFeedbackValid = 1,
            FeedbackGenerationsCompatible = 1,
            GlobalConvergencePending = 1,
            LocalConvergencePending = 0,
            EligibleProbeCount = eligible,
            SelectedRequestCount = selected,
            IndirectDispatchRequestCount = dispatched,
            CommittedUpdateCount = committed,
            BlendedUpdateCount = published,
            CoherentPublicationCount = published,
            VisibleDemandPageCount = visibleDemand,
            VisibleDemandSuppressedCount = suppressedDemand,
            VisibleDemandInitializingOrUnpublishedCount = initializingDemand,
            EffectiveRequestBudget = 8u,
            EffectiveRayBudget = 64u,
            FeedbackRejectionReason = feedbackRejection,
            TransactionAbortDeltas = transactionAborts
        };
    }
}
