using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiMutationLatencyTests
{
    [Test]
    public void FirstVisibleAndCertifiedLatencyUseSeparateDistributions()
    {
        var tracker = new SimpleDdgiMutationLatencyTracker();
        tracker.Begin(SimpleDdgiMutationClass.Light, 10u);
        tracker.RecordFirstVisibleResponse(12u);
        tracker.RecordCertifiedConvergence(18u);

        SimpleDdgiMutationLatencySnapshot snapshot = tracker.GetSnapshot(
            SimpleDdgiMutationClass.Light);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FirstVisibleResponse.SampleCount, Is.EqualTo(1));
            Assert.That(snapshot.FirstVisibleResponse.P99Frames, Is.EqualTo(2));
            Assert.That(snapshot.CertifiedConvergence.SampleCount, Is.EqualTo(1));
            Assert.That(snapshot.CertifiedConvergence.P99Frames, Is.EqualTo(8));
            Assert.That(snapshot.EventPending, Is.False);
        });
    }

    [Test]
    public void SupersededEditIsCensoredAndLatestEditOwnsTheSample()
    {
        var tracker = new SimpleDdgiMutationLatencyTracker();
        tracker.Begin(SimpleDdgiMutationClass.Transform, 10u);
        tracker.Begin(SimpleDdgiMutationClass.Transform, 12u);
        tracker.RecordFirstVisibleResponse(13u);
        tracker.RecordCertifiedConvergence(15u);

        SimpleDdgiMutationLatencySnapshot snapshot = tracker.GetSnapshot(
            SimpleDdgiMutationClass.Transform);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FirstVisibleResponse.P95Frames, Is.EqualTo(1));
            Assert.That(snapshot.CertifiedConvergence.P95Frames, Is.EqualTo(3));
            Assert.That(snapshot.FirstVisibleResponse.CensoredCount, Is.EqualTo(1));
            Assert.That(snapshot.CertifiedConvergence.CensoredCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MutationClassifierPreservesAllSixProductionClasses()
    {
        DdgiDirtyRegion[] regions =
        [
            Region(DdgiDirtyReason.LocalLightChanged),
            Region(DdgiDirtyReason.EmissiveChanged),
            Region(DdgiDirtyReason.MaterialChanged),
            Region(DdgiDirtyReason.TransformChanged),
            Region(DdgiDirtyReason.GeometryRemoved)
        ];

        SimpleDdgiMutationClass classes =
            SimpleDdgiVolumeManager.ResolveMutationClasses(
                regions,
                DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonEmissive,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight);

        Assert.That(classes, Is.EqualTo(
            SimpleDdgiMutationClass.Environment |
            SimpleDdgiMutationClass.Light |
            SimpleDdgiMutationClass.Emissive |
            SimpleDdgiMutationClass.Material |
            SimpleDdgiMutationClass.Transform |
            SimpleDdgiMutationClass.Topology));
    }

    private static DdgiDirtyRegion Region(DdgiDirtyReason reason) =>
        new(
            new BoundingBox(new Vector3(-1.0f), new Vector3(1.0f)),
            reason);
}
