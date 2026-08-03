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
}
