using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshletSystemQualificationTests
{
    [Test]
    public void Rtx3060Laptop_PassesOnlyWithAllProductionBudgets()
    {
        var device = new MeshletQualificationDevice(
            MeshletSystemQualificationContract.NvidiaVendorId,
            "NVIDIA GeForce RTX 3060 Laptop GPU",
            IntegratedGpu: false,
            VulkanApiVersion: 1);

        MeshletQualificationResult passed =
            MeshletSystemQualificationContract.Evaluate(
                device,
                CreatePassingEvidence());
        MeshletQualificationResult regressed =
            MeshletSystemQualificationContract.Evaluate(
                device,
                CreatePassingEvidence() with
                {
                    CandidateP95GpuMilliseconds = 10.3
                });

        Assert.Multiple(() =>
        {
            Assert.That(passed.Passed, Is.True);
            Assert.That(
                passed.Level,
                Is.EqualTo(
                    MeshletQualificationLevel.ProductionPerformance));
            Assert.That(regressed.Passed, Is.False);
            Assert.That(
                regressed.Detail,
                Does.Contain("performance-budget"));
        });
    }

    [Test]
    public void AmdIntegratedGpu_IsCorrectnessOnly()
    {
        var device = new MeshletQualificationDevice(
            MeshletSystemQualificationContract.AmdVendorId,
            "AMD Radeon Integrated Graphics",
            IntegratedGpu: true,
            VulkanApiVersion: 1);
        MeshletQualificationEvidence evidence =
            CreatePassingEvidence() with
            {
                BaselineP95CpuMilliseconds = 0,
                CandidateP95CpuMilliseconds = 0,
                BaselineP95GpuMilliseconds = 0,
                CandidateP95GpuMilliseconds = 0,
                WarmPageCacheHitRate = 0
            };

        MeshletQualificationResult result =
            MeshletSystemQualificationContract.Evaluate(
                device,
                evidence);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True);
            Assert.That(
                result.Level,
                Is.EqualTo(MeshletQualificationLevel.CorrectnessOnly));
            Assert.That(result.Detail, Does.Contain("not-qualified"));
        });
    }

    [Test]
    public void AnyCorrectnessFailureRejectsEveryDeviceTrack()
    {
        var device = new MeshletQualificationDevice(
            MeshletSystemQualificationContract.NvidiaVendorId,
            "RTX 3060 Laptop GPU",
            IntegratedGpu: false,
            VulkanApiVersion: 1);
        MeshletQualificationEvidence evidence =
            CreatePassingEvidence() with
            {
                BoundsFalseNegativeCount = 1
            };

        MeshletQualificationResult result =
            MeshletSystemQualificationContract.Evaluate(
                device,
                evidence);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(
                result.Level,
                Is.EqualTo(MeshletQualificationLevel.Rejected));
            Assert.That(result.Detail, Does.Contain("correctness-counter"));
        });
    }

    private static MeshletQualificationEvidence CreatePassingEvidence() =>
        new()
        {
            BuildCommit = new string('a', 40),
            ArtifactBundleSha256 = new string('b', 64),
            ReleaseBuild = true,
            CleanWorktree = true,
            IndependentRuns = 3,
            MeasuredFramesPerRun = 1000,
            EightFrameDitherVerified = true,
            DirectionalShadowParityVerified = true,
            TransparentOrderingVerified = true,
            SkinnedPinnedResidencyVerified = true,
            ConservativeRayProxyVerified = true,
            FullResidentFallbackVerified = true,
            MaximumReferenceImageDifference = 0.005,
            WarmPageCacheHitRate = 0.95,
            BaselineP95CpuMilliseconds = 5.0,
            CandidateP95CpuMilliseconds = 4.5,
            BaselineP95GpuMilliseconds = 10.0,
            CandidateP95GpuMilliseconds = 9.0,
            PeakPhysicalPageBytes =
                MeshletSystemQualificationContract
                    .MaximumPhysicalPageBytes,
            PeakUploadBytesPerFrame =
                MeshletSystemQualificationContract
                    .MaximumUploadBytesPerFrame
        };
}
