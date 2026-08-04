using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionProbeRecapturePolicyTests
{
    [Test]
    public void Observe_CoalescesStableVersionsAndMergesReasons()
    {
        var policy = new ReflectionProbeRecapturePolicy();
        var first = new ReflectionCaptureVersion(1, 1, 1, 0, 1, 1, 1);
        ReflectionProbeRecaptureDecision initial = policy.Observe(
            first,
            ReflectionCaptureReason.InitialLoad,
            currentFrame: 1UL);
        policy.MarkStarted(first, currentFrame: 1UL);

        ReflectionProbeRecaptureDecision stable = policy.Observe(
            first,
            ReflectionCaptureReason.EnvironmentChanged,
            currentFrame: 2UL);
        ReflectionProbeRecaptureDecision forced = policy.Observe(
            first,
            ReflectionCaptureReason.Manual,
            currentFrame: 2UL,
            bypassInterval: true);

        Assert.Multiple(() =>
        {
            Assert.That(initial.RequestCapture, Is.True);
            Assert.That(stable.RequestCapture, Is.False);
            Assert.That(stable.Coalesced, Is.True);
            Assert.That(forced.RequestCapture, Is.True);
            Assert.That(forced.Reasons, Is.EqualTo(ReflectionCaptureReason.Manual));
        });
    }

    [Test]
    public void Observe_DefersUntilMinimumIntervalAndReleasesExactlyOnce()
    {
        var policy = new ReflectionProbeRecapturePolicy();
        var first = new ReflectionCaptureVersion(1, 1, 1, 0, 1, 1, 1);
        var second = first with { SceneRadianceRevision = 2 };
        policy.Observe(first, ReflectionCaptureReason.InitialLoad, currentFrame: 10UL);
        policy.MarkStarted(first, currentFrame: 10UL);

        ReflectionProbeRecaptureDecision deferred = policy.Observe(
            second,
            ReflectionCaptureReason.SceneChanged,
            currentFrame: 11UL,
            minimumIntervalFrames: 5UL);
        Assert.That(deferred, Is.EqualTo(new ReflectionProbeRecaptureDecision(
            false,
            false,
            ReflectionCaptureReason.SceneChanged,
            second,
            2UL,
            Deferred: true)));

        Assert.That(policy.TryReleaseDeferred(14UL, out _), Is.False);
        Assert.That(policy.TryReleaseDeferred(15UL, out ReflectionProbeRecaptureDecision released), Is.True);
        Assert.That(released.RequestCapture, Is.True);
        Assert.That(policy.TryReleaseDeferred(15UL, out _), Is.False);
    }
}
