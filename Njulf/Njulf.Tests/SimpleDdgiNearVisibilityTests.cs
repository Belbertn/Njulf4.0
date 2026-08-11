using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearVisibilityTests
{
    [Test]
    public void ThinWall_ConfidentNearDepthClampsMeasuredMomentLeak()
    {
        var query = Query(
            momentMean: 3.0f,
            momentSecond: 9.25f,
            receiverDistance: 2.5f,
            conservativeDepth: 1.0f,
            confidence: 1.0f);

        SimpleDdgiNearVisibilityEvaluation result =
            SimpleDdgiNearVisibility.Evaluate(query);

        Assert.Multiple(() =>
        {
            Assert.That(result.Disposition,
                Is.EqualTo(SimpleDdgiNearVisibilityDisposition.Applied));
            Assert.That(result.MomentVisibility, Is.EqualTo(1.0f));
            Assert.That(result.FinalVisibility, Is.EqualTo(0.02f).Within(0.001f));
            Assert.That(result.FinalVisibility,
                Is.LessThan(result.MomentVisibility * 0.05f));
        });
    }

    [TestCase(0.40f, 3.0f, 2.5f,
        SimpleDdgiNearVisibilityDisposition.InsufficientConfidence,
        TestName = "Doorway_low_directional_coverage_is_unchanged")]
    [TestCase(1.00f, 1.05f, 2.5f,
        SimpleDdgiNearVisibilityDisposition.NoMomentDiscrepancy,
        TestName = "Foliage_card_without_moment_discrepancy_is_unchanged")]
    [TestCase(1.00f, 3.00f, 0.99f,
        SimpleDdgiNearVisibilityDisposition.ReceiverInFront,
        TestName = "Receiver_in_front_of_occluder_is_unchanged")]
    public void FalseOcclusionGuards_KeepOrdinaryMomentVisibility(
        float confidence,
        float momentMean,
        float receiverDistance,
        SimpleDdgiNearVisibilityDisposition expectedDisposition)
    {
        var query = Query(
            momentMean,
            momentSecond: momentMean * momentMean + 0.25f,
            receiverDistance,
            conservativeDepth: 1.0f,
            confidence);

        SimpleDdgiNearVisibilityEvaluation result =
            SimpleDdgiNearVisibility.Evaluate(query);

        Assert.Multiple(() =>
        {
            Assert.That(result.Disposition, Is.EqualTo(expectedDisposition));
            Assert.That(result.Applied, Is.False);
            Assert.That(result.FinalVisibility,
                Is.EqualTo(result.MomentVisibility).Within(1.0e-6f));
        });
    }

    [Test]
    public void MovingOccluder_MissEvidenceReleasesHistoryInOneRefresh()
    {
        var previous = new SimpleDdgiNearVisibilitySample(1.0f, 1.0f);
        var currentMiss = SimpleDdgiNearVisibilitySample.Empty;

        SimpleDdgiNearVisibilitySample blended =
            SimpleDdgiNearVisibility.BlendEvidence(
                previous,
                currentMiss,
                texelHysteresis: 0.97f,
                historyValid: true,
                freshUpdate: false,
                probeSpacing: 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(blended.ConservativeDepth, Is.EqualTo(1.0f));
            Assert.That(blended.Confidence, Is.EqualTo(0.20f).Within(1.0e-6f));
            Assert.That(blended.Confidence,
                Is.LessThan(SimpleDdgiNearVisibility.MinimumConfidence));
        });
    }

    [Test]
    public void VolumeEligibility_IsLimitedToNearRingAndRefinement()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiNearVisibility.UsesSidecar(
                SimpleDdgiVolumeKind.CameraRing, 10_000), Is.True);
            Assert.That(SimpleDdgiNearVisibility.UsesSidecar(
                SimpleDdgiVolumeKind.RefinementBrick, 30_000), Is.True);
            Assert.That(SimpleDdgiNearVisibility.UsesSidecar(
                SimpleDdgiVolumeKind.CameraRing, 10_001), Is.False);
            Assert.That(SimpleDdgiNearVisibility.UsesSidecar(
                SimpleDdgiVolumeKind.Authored, 0), Is.False);
        });
    }

    [Test]
    public void PackedSidecar_RoundTripsAtCanonicalFourByteStride()
    {
        var source = new SimpleDdgiNearVisibilitySample(1.375f, 0.8125f);
        SimpleDdgiNearVisibilitySample decoded =
            SimpleDdgiNearVisibility.Unpack(
                SimpleDdgiNearVisibility.Pack(source));

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiNearVisibility.BytesPerTexel,
                Is.EqualTo(SimpleDdgiVisibilityPacking.BytesPerTexel));
            Assert.That(decoded, Is.EqualTo(source));
            Assert.That(SimpleDdgiNearVisibility.Unpack(
                SimpleDdgiNearVisibility.Pack(
                    SimpleDdgiNearVisibilitySample.Empty)),
                Is.EqualTo(SimpleDdgiNearVisibilitySample.Empty));
        });
    }

    [Test]
    public void MemoryAdmission_IsIndependentAndCannotEvictCanonicalPayload()
    {
        const int probes = 1_024;
        const ulong required = probes *
            SimpleDdgiMemoryPlan.VisibilityBytesPerProbe * 2UL;
        SimpleDdgiMemoryPlan rejected = CreatePlan(required - 1UL);
        SimpleDdgiMemoryPlan admitted = CreatePlan(required);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.PhysicalProbeCapacity,
                Is.EqualTo(admitted.PhysicalProbeCapacity));
            Assert.That(rejected.IrradianceAtlasBytes,
                Is.EqualTo(admitted.IrradianceAtlasBytes));
            Assert.That(rejected.VisibilityAtlasBytes,
                Is.EqualTo(admitted.VisibilityAtlasBytes));
            Assert.That(rejected.NearVisibilitySidecarAdmitted, Is.False);
            Assert.That(rejected.NearVisibilitySidecarFallbackReason,
                Is.EqualTo("independent-memory-budget"));
            Assert.That(admitted.NearVisibilitySidecarAdmitted, Is.True);
            Assert.That(admitted.NearVisibilitySidecarBytes,
                Is.EqualTo(required / 2UL));
            Assert.That(admitted.NearVisibilityPrivateSidecarBytes,
                Is.EqualTo(required / 2UL));
            Assert.That(admitted.LiveBytes - rejected.LiveBytes,
                Is.EqualTo(required));
        });
    }

    [Test]
    public void ShaderContract_PublishesSidecarInsideResidentTransaction()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_shared.glsl");
        string blend = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_blend.comp");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit_local.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_FLAG_NEAR_VISIBILITY_SIDECAR"));
            Assert.That(shared, Does.Contain(
                "float SimpleDdgiApplyNearVisibilitySidecar("));
            Assert.That(shared, Does.Contain(
                "momentMean <= conservativeDepth + discrepancyMargin"));
            Assert.That(blend, Does.Contain(
                "currentConfidence < 0.65"));
            Assert.That(blend, Does.Contain(
                "pc.PrivateVisibilityAtlasOffsetWords +"));
            Assert.That(commit, Does.Contain(
                "SchedulerVolumeUsesNearVisibilitySidecar(volumeIndex)"));
            Assert.That(commit, Does.Contain(
                "publicNearVisibilityBase + word"));
        });
    }

    private static SimpleDdgiNearVisibilityQuery Query(
        float momentMean,
        float momentSecond,
        float receiverDistance,
        float conservativeDepth,
        float confidence) => new(
            momentMean,
            momentSecond,
            receiverDistance,
            ProbeSpacing: 0.5f,
            ArchitecturalThickness: 0.10f,
            new SimpleDdgiNearVisibilitySample(
                conservativeDepth,
                confidence),
            SimpleDdgiVolumeKind.CameraRing,
            SourceOrdinal: 10_000);

    private static SimpleDdgiMemoryPlan CreatePlan(ulong sidecarBudget) =>
        SimpleDdgiMemoryPlan.Create(
            probeCount: 1_024,
            updateRequestCapacity: 128,
            rayCapacity: 128,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            schedulerActiveVolumeCount: 3,
            nearVisibilitySidecarRequested: true,
            nearVisibilitySidecarBudgetBytes: sidecarBudget);

    private static string ReadRepoText(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeParts)} from the test output directory.");
    }
}
