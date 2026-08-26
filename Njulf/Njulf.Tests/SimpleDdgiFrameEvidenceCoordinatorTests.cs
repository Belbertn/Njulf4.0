using System.Reflection;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiFrameEvidenceCoordinatorTests
{
    [Test]
    public void AbortedPendingCaptureNeverBecomesFenceCompleteEvidence()
    {
        var coordinator = new SimpleDdgiFrameEvidenceCoordinator(2);
        coordinator.CapturePending(
            0,
            new SimpleDdgiSubmittedWorkload(CreateSubmitted(0, 10UL)));

        coordinator.AbortPendingSubmission();
        SimpleDdgiCompletedFrameEvidence completed =
            coordinator.CompleteAfterFence(0, CreateCompletion());

        Assert.Multiple(() =>
        {
            Assert.That(completed.Valid, Is.False);
            Assert.That(
                coordinator.CaptureSnapshot().HasPendingCapture,
                Is.False);
            Assert.That(
                coordinator.CostEstimate.AcceptedSampleCount,
                Is.Zero);
        });
    }

    [Test]
    public void SuccessfulSubmissionTrainsOnlyAtFenceCompletion()
    {
        var coordinator = new SimpleDdgiFrameEvidenceCoordinator(2);
        coordinator.CapturePending(
            1,
            new SimpleDdgiSubmittedWorkload(CreateSubmitted(1, 20UL)));

        coordinator.CommitSuccessfulSubmission(1);
        Assert.That(
            coordinator.CostEstimate.AcceptedSampleCount,
            Is.Zero);

        SimpleDdgiCompletedFrameEvidence completed =
            coordinator.CompleteAfterFence(1, CreateCompletion());
        SimpleDdgiFrameEvidenceSnapshot snapshot =
            coordinator.CaptureSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(completed.Valid, Is.True);
            Assert.That(completed.Submitted.FrameSerial, Is.EqualTo(20UL));
            Assert.That(snapshot.CostEstimate.AcceptedSampleCount,
                Is.EqualTo(1UL));
            Assert.That(snapshot.SourceCacheObservation.Valid, Is.True);
            Assert.That(snapshot.SourceCacheObservation.FrameSerial,
                Is.EqualTo(20UL));
        });
    }

    [Test]
    public void DisabledLivenessResetsPublishedState()
    {
        var coordinator = new SimpleDdgiFrameEvidenceCoordinator(2);
        SimpleDdgiLivenessRequest active = default(SimpleDdgiLivenessRequest)
            with
        {
            Active = true,
            FrameSerial = 30UL,
            ProbesUpdated = 4,
            ReceiverRecordsPublishedCount = 2,
            ConfiguredPrimaryRayBudget = 128
        };

        SimpleDdgiLivenessSnapshot observed =
            coordinator.EvaluateLiveness(active);
        SimpleDdgiLivenessSnapshot disabled =
            coordinator.EvaluateLiveness(default);

        Assert.Multiple(() =>
        {
            Assert.That(observed.Telemetry.SelectedRequestCount,
                Is.EqualTo(4u));
            Assert.That(observed.Telemetry.CoherentPublicationCount,
                Is.EqualTo(2u));
            Assert.That(disabled,
                Is.EqualTo(SimpleDdgiLivenessSnapshot.Empty));
        });
    }

    [Test]
    public void RendererRetainsOnlyCoordinatorLevelEvidenceState()
    {
        FieldInfo[] evidenceFields = typeof(VulkanRenderer)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.Contains(
                "simpleDdgiFrameEvidence",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.That(evidenceFields, Has.Length.EqualTo(1));
        Assert.That(
            evidenceFields[0].FieldType,
            Is.EqualTo(typeof(SimpleDdgiFrameEvidenceCoordinator)));
    }

    private static SimpleDdgiSubmittedFrameEvidence CreateSubmitted(
        int frameSlot,
        ulong frameSerial) => new()
        {
            Valid = true,
            FrameSlot = frameSlot,
            FrameSerial = frameSerial,
            SchedulerFrameSerial = frameSerial,
            ScheduledPrimaryRayCount = 100UL,
            VisibilityRayCount = 25UL,
            SourceCacheLayoutIdentity = 7UL
        };

    private static SimpleDdgiFenceCompletedEvidence CreateCompletion() =>
        new(
            new FrameTimingSnapshot(Array.Empty<PassTiming>()),
            default,
            default,
            default,
            SchedulerFeedbackAvailable: false,
            default,
            SchedulerFeedbackTransportTopologyGeneration: 0u,
            SchedulerActiveCanonicalMutationCount: 0u,
            SchedulerActiveSourceMutationCount: 0u);
}
