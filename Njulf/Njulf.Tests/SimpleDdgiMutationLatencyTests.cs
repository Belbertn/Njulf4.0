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
        tracker.RecordAffectedRegionConvergence(14u);
        tracker.RecordCertifiedConvergence(18u);

        SimpleDdgiMutationLatencySnapshot snapshot = tracker.GetSnapshot(
            SimpleDdgiMutationClass.Light);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FirstVisibleResponse.SampleCount, Is.EqualTo(1));
            Assert.That(snapshot.FirstVisibleResponse.P99Frames, Is.EqualTo(2));
            Assert.That(snapshot.AffectedRegionConvergence.SampleCount,
                Is.EqualTo(1));
            Assert.That(snapshot.AffectedRegionConvergence.P99Frames,
                Is.EqualTo(4));
            Assert.That(snapshot.CertifiedConvergence.SampleCount, Is.EqualTo(1));
            Assert.That(snapshot.CertifiedConvergence.P99Frames, Is.EqualTo(8));
            Assert.That(snapshot.EventPending, Is.False);
        });
    }

    [Test]
    public void DelayedGpuWitnessCannotCloseANewerMutationGeneration()
    {
        var tracker = new SimpleDdgiMutationLatencyTracker();
        ulong lightGeneration = tracker.Begin(
            SimpleDdgiMutationClass.Light,
            10u);
        ulong materialGeneration = tracker.Begin(
            SimpleDdgiMutationClass.Material,
            11u);

        tracker.RecordFirstVisibleResponse(lightGeneration, 11u);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetSnapshot(SimpleDdgiMutationClass.Light)
                .FirstVisibleResponse.SampleCount, Is.EqualTo(1));
            Assert.That(tracker.GetSnapshot(SimpleDdgiMutationClass.Material)
                .FirstVisibleResponse.SampleCount, Is.Zero);
            Assert.That(tracker.GetSnapshot(SimpleDdgiMutationClass.Material)
                .FirstResponsePending, Is.True);
        });

        tracker.RecordFirstVisibleResponse(materialGeneration, 12u);
        Assert.That(tracker.GetSnapshot(SimpleDdgiMutationClass.Material)
            .FirstVisibleResponse.P95Frames, Is.EqualTo(1));
    }

    [Test]
    public void SceneAttachmentBootstrapIsExcludedFromRuntimeClassPercentiles()
    {
        var tracker = new SimpleDdgiMutationLatencyTracker();
        ulong bootstrapGeneration = tracker.Begin(
            SimpleDdgiMutationClass.Topology,
            0u,
            coldStart: true);
        tracker.RecordFirstVisibleResponse(bootstrapGeneration, 1u);
        tracker.RecordAffectedRegionConvergence(bootstrapGeneration, 8u);
        tracker.RecordCertifiedConvergence(bootstrapGeneration, 40u);

        SimpleDdgiMutationLatencyTelemetry telemetry = tracker.GetTelemetry();

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.Topology.FirstVisibleResponse.SampleCount,
                Is.Zero);
            Assert.That(telemetry.Topology.CertifiedConvergence.SampleCount,
                Is.Zero);
            Assert.That(telemetry.ColdStart.FirstVisibleResponse.SampleCount,
                Is.EqualTo(1));
            Assert.That(telemetry.ColdStart.AffectedRegionConvergence.P95Frames,
                Is.EqualTo(8));
            Assert.That(telemetry.ColdStart.CertifiedConvergence.P95Frames,
                Is.EqualTo(40));
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

    [Test]
    public void SettledIntentionalMutationsPopulateP95ForEveryClass()
    {
        var tracker = new SimpleDdgiMutationLatencyTracker();
        SimpleDdgiMutationClass[] classes =
        [
            SimpleDdgiMutationClass.Environment,
            SimpleDdgiMutationClass.Light,
            SimpleDdgiMutationClass.Emissive,
            SimpleDdgiMutationClass.Material,
            SimpleDdgiMutationClass.Transform,
            SimpleDdgiMutationClass.Topology
        ];
        uint frame = 10u;
        foreach (SimpleDdgiMutationClass mutationClass in classes)
        {
            for (int sample = 0;
                 sample < SimpleDdgiMutationLatencyTracker.MinimumP95SampleCount;
                 sample++)
            {
                ulong generation = tracker.Begin(mutationClass, frame);
                tracker.RecordFirstVisibleResponse(generation, frame + 1u);
                tracker.RecordAffectedRegionConvergence(generation, frame + 8u);
                tracker.RecordCertifiedConvergence(generation, frame + 32u);
                frame += 40u;
            }
        }

        foreach (SimpleDdgiMutationLatencySnapshot snapshot in
                 tracker.GetTelemetry().Enumerate())
        {
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.FirstVisibleResponse.SampleCount,
                    Is.EqualTo(SimpleDdgiMutationLatencyTracker
                        .MinimumP95SampleCount));
                Assert.That(snapshot.FirstVisibleResponse.P95Frames,
                    Is.EqualTo(1));
                Assert.That(snapshot.AffectedRegionConvergence.P95Frames,
                    Is.EqualTo(8));
                Assert.That(snapshot.CertifiedConvergence.P95Frames,
                    Is.EqualTo(32));
                Assert.That(snapshot.EventPending, Is.False);
            });
        }
    }

    [Test]
    public void ResidentFeedbackSeparatesPublicationFromSettling()
    {
        var publication = new GPUSimpleDdgiSchedulerFeedback
        {
            PublishedCount = 1u
        };
        var settled = new GPUSimpleDdgiSchedulerFeedback();

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager
                .HasReceiverVisibleMutationPublication(publication, 1u, 0u),
                Is.True);
            Assert.That(SimpleDdgiVolumeManager
                .HasReceiverVisibleMutationPublication(publication, 0u, 0u),
                Is.False);
            Assert.That(SimpleDdgiVolumeManager
                .HasAffectedRegionConvergenceEvidence(settled, 0u, 0u),
                Is.True);
            settled.PendingSolverCount = 1u;
            Assert.That(SimpleDdgiVolumeManager
                .HasAffectedRegionConvergenceEvidence(settled, 0u, 0u),
                Is.False);
        });
    }

    private static DdgiDirtyRegion Region(DdgiDirtyReason reason) =>
        new(
            new BoundingBox(new Vector3(-1.0f), new Vector3(1.0f)),
            reason);
}
