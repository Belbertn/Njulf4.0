using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiRefinementFocusTrackerTests
{
    [Test]
    public void StaticView_LatchesFirstBaseWitnessAndIgnoresNoisySuccessors()
    {
        var tracker = new SimpleDdgiRefinementFocusTracker();
        var fallback = new Vector3(0f, 0f, -2f);
        var firstWitness = new Vector3(3f, 1f, -4f);
        var noisyWitness = new Vector3(-20f, 8f, 17f);

        Vector3 first = tracker.Resolve(
            fallback,
            Vector3.Zero,
            Vector3.Forward,
            0.75f,
            cameraCutSerial: 0,
            sceneContentRevision: 7,
            firstWitness);
        Vector3 second = tracker.Resolve(
            fallback,
            Vector3.Zero,
            Vector3.Forward,
            0.75f,
            cameraCutSerial: 0,
            sceneContentRevision: 7,
            noisyWitness);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(firstWitness));
            Assert.That(second, Is.EqualTo(firstWitness));
            Assert.That(tracker.HasMeasuredFocus, Is.True);
        });
    }

    [Test]
    public void CumulativeCameraMovement_ReacquiresAVisibleWitness()
    {
        var tracker = new SimpleDdgiRefinementFocusTracker();
        var initialWitness = new Vector3(1f, 0f, -3f);
        var replacementWitness = new Vector3(9f, 2f, -6f);
        tracker.Resolve(
            Vector3.Forward,
            Vector3.Zero,
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 11,
            initialWitness);

        Vector3 belowThreshold = tracker.Resolve(
            new Vector3(0.6f, 0f, -1f),
            new Vector3(0.6f, 0f, 0f),
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 11,
            replacementWitness);
        Vector3 transitionFrame = tracker.Resolve(
            new Vector3(1.1f, 0f, -1f),
            new Vector3(1.1f, 0f, 0f),
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 11,
            replacementWitness);
        Vector3 afterFreshFeedback = tracker.Resolve(
            new Vector3(1.1f, 0f, -1f),
            new Vector3(1.1f, 0f, 0f),
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 11,
            replacementWitness);

        Assert.Multiple(() =>
        {
            Assert.That(belowThreshold, Is.EqualTo(initialWitness));
            Assert.That(transitionFrame, Is.EqualTo(initialWitness));
            Assert.That(afterFreshFeedback, Is.EqualTo(replacementWitness));
        });
    }

    [Test]
    public void CameraCutOrSceneRevision_ReacquiresInsteadOfKeepingStaleFocus()
    {
        var tracker = new SimpleDdgiRefinementFocusTracker();
        var firstWitness = new Vector3(2f, 0f, -2f);
        var cutWitness = new Vector3(4f, 0f, -4f);
        var newSceneWitness = new Vector3(6f, 0f, -6f);
        tracker.Resolve(
            Vector3.Forward,
            Vector3.Zero,
            Vector3.Forward,
            1f,
            cameraCutSerial: 1,
            sceneContentRevision: 20,
            firstWitness);

        Vector3 afterCut = tracker.Resolve(
            Vector3.Forward,
            Vector3.Zero,
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 20,
            cutWitness);
        Vector3 afterSceneChange = tracker.Resolve(
            Vector3.Forward,
            Vector3.Zero,
            Vector3.Forward,
            1f,
            cameraCutSerial: 2,
            sceneContentRevision: 21,
            newSceneWitness);

        Assert.Multiple(() =>
        {
            Assert.That(afterCut, Is.EqualTo(cutWitness));
            Assert.That(afterSceneChange, Is.EqualTo(newSceneWitness));
        });
    }
}
