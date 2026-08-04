using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiAtmosphereAdmissionControllerTests
{
    [Test]
    public void LatestCandidateIsCoalescedUntilCurrentSourceCohortReleases()
    {
        var controller = new GiAtmosphereAdmissionController();
        GiAtmosphereCohortFeedback inactive = new(false, 0, false, 0);
        GiAtmosphereAdmissionDecision bootstrap = controller.Update(new GiAtmosphereAdmissionInput(10, inactive));
        Assert.That(bootstrap.Action, Is.EqualTo(GiAtmosphereAdmissionAction.AdmitPendingCandidate));
        Assert.That(bootstrap.AdmittedGeneration, Is.EqualTo(1));

        GiAtmosphereCohortFeedback refreshing = new(true, 100, true, 100);
        GiAtmosphereAdmissionDecision firstPending = controller.Update(new GiAtmosphereAdmissionInput(20, refreshing));
        GiAtmosphereAdmissionDecision latestPending = controller.Update(new GiAtmosphereAdmissionInput(30, refreshing));
        Assert.Multiple(() =>
        {
            Assert.That(firstPending.AdmittedSignature, Is.EqualTo(10));
            Assert.That(latestPending.Action, Is.EqualTo(GiAtmosphereAdmissionAction.ReplacePendingCandidate));
            Assert.That(latestPending.CoalescedCount, Is.EqualTo(1));
            Assert.That(latestPending.AdmittedGeneration, Is.EqualTo(1));
        });

        GiAtmosphereCohortFeedback released = new(true, 100, false, 0);
        GiAtmosphereAdmissionDecision admitted = controller.Update(new GiAtmosphereAdmissionInput(30, released));
        Assert.Multiple(() =>
        {
            Assert.That(admitted.Action, Is.EqualTo(GiAtmosphereAdmissionAction.AdmitPendingCandidate));
            Assert.That(admitted.AdmittedSignature, Is.EqualTo(30));
            Assert.That(admitted.AdmittedGeneration, Is.EqualTo(2));
            Assert.That(admitted.HasPendingCandidate, Is.False);
        });
    }

    [Test]
    public void HardInvalidationRestartsWithoutWaitingForCohort()
    {
        var controller = new GiAtmosphereAdmissionController();
        controller.Update(new GiAtmosphereAdmissionInput(1, default));
        GiAtmosphereAdmissionDecision decision = controller.Update(
            new GiAtmosphereAdmissionInput(2, new GiAtmosphereCohortFeedback(true, 5, true, 5), true));
        Assert.That(decision.Action, Is.EqualTo(GiAtmosphereAdmissionAction.HardRestartWithCandidate));
        Assert.That(decision.AdmittedSignature, Is.EqualTo(2));
    }

    [Test]
    public void ExactGenerationAndVisiblePropagationBoundariesAreRequired()
    {
        var controller = new GiAtmosphereAdmissionController();
        controller.Update(new GiAtmosphereAdmissionInput(1, default));

        GiAtmosphereCohortFeedback sourceNotAdmitted = new(
            ConsumesSteppedAtmosphere: true,
            ParticipatingProbeCount: 4,
            SourceCohortActive: false,
            StaleParticipatingProbeCount: 0,
            VisiblePublicationBoundaryComplete: true,
            MinimumPropagationBoundaryComplete: true,
            VolumeResourceGeneration: 8,
            SourceCohortGeneration: 2,
            AdmittedSourceCohortGeneration: 1,
            PropagationGeneration: 3,
            PublishedPropagationGeneration: 3,
            VisiblePriorityParticipatingProbeCount: 2,
            VisiblePrioritySourceReadyProbeCount: 2,
            VisiblePriorityPublishedProbeCount: 2,
            QuietPeriodComplete: true);
        GiAtmosphereAdmissionDecision mismatch = controller.Update(
            new GiAtmosphereAdmissionInput(2, sourceNotAdmitted,
                CurrentVolumeResourceGeneration: 8,
                CurrentSourceCohortGeneration: 2,
                CurrentPropagationGeneration: 3));
        Assert.That(mismatch.Reason, Is.EqualTo(GiAtmosphereAdmissionReason.FeedbackGenerationMismatch));

        GiAtmosphereCohortFeedback visiblePending = sourceNotAdmitted with
        {
            AdmittedSourceCohortGeneration = 2,
            VisiblePrioritySourceReadyProbeCount = 1,
            VisiblePriorityPublishedProbeCount = 1
        };
        GiAtmosphereAdmissionDecision visibleHold = controller.Update(
            new GiAtmosphereAdmissionInput(2, visiblePending,
                CurrentVolumeResourceGeneration: 8,
                CurrentSourceCohortGeneration: 2,
                CurrentPropagationGeneration: 3));
        Assert.That(visibleHold.Reason, Is.EqualTo(GiAtmosphereAdmissionReason.PublicationBoundaryPending));

        GiAtmosphereCohortFeedback propagationPending = visiblePending with
        {
            VisiblePrioritySourceReadyProbeCount = 2,
            VisiblePriorityPublishedProbeCount = 2,
            PublishedPropagationGeneration = 2
        };
        GiAtmosphereAdmissionDecision propagationHold = controller.Update(
            new GiAtmosphereAdmissionInput(2, propagationPending,
                CurrentVolumeResourceGeneration: 8,
                CurrentSourceCohortGeneration: 2,
                CurrentPropagationGeneration: 3));
        Assert.That(propagationHold.Reason, Is.EqualTo(GiAtmosphereAdmissionReason.FeedbackGenerationMismatch));

        GiAtmosphereCohortFeedback released = propagationPending with
        {
            PublishedPropagationGeneration = 3
        };
        GiAtmosphereAdmissionDecision admitted = controller.Update(
            new GiAtmosphereAdmissionInput(2, released,
                CurrentVolumeResourceGeneration: 8,
                CurrentSourceCohortGeneration: 2,
                CurrentPropagationGeneration: 3));
        Assert.That(admitted.Action, Is.EqualTo(GiAtmosphereAdmissionAction.AdmitPendingCandidate));
    }
}
