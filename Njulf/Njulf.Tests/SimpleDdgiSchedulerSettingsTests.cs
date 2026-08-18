using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSchedulerSettingsTests
{
    [Test]
    public void SchedulerModeSanitizesUnknownValuesToCpuReference()
    {
        Assert.Multiple(() =>
        {
            Assert.That(((SimpleDdgiSchedulerMode)99u).Sanitize(), Is.EqualTo(SimpleDdgiSchedulerMode.CpuReference));
            Assert.That(SimpleDdgiSchedulerMode.CpuReference.IsGpuMode(), Is.False);
            Assert.That(SimpleDdgiSchedulerMode.GpuMirror.IsGpuMode(), Is.True);
            Assert.That(SimpleDdgiSchedulerMode.GpuResident.IsGpuMode(), Is.True);
        });
    }

    [Test]
    public void NewRenderSettingsEnableTheGpuResidentPlanByDefault()
    {
        RenderSettings settings = new();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;

        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiSchedulerMode, Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(gi.UseRayQueryBackend, Is.True);
            Assert.That(gi.SimpleDdgiTransportV2Enabled, Is.True);
            Assert.That(gi.SimpleDdgiClassificationSchedulingEnabled, Is.True);
            Assert.That(gi.SimpleDdgiStructuredGatherEnabled, Is.True);
            Assert.That(gi.SimpleDdgiToroidalScrollingEnabled, Is.True);
            Assert.That(gi.SimpleDdgiRegionalInvalidationEnabled, Is.True);
            Assert.That(gi.SimpleDdgiReceiverContributionFeedbackEnabled, Is.True);
            Assert.That(gi.SimpleDdgiPersistentWarmStartEnabled, Is.True);
            Assert.That(gi.SimpleDdgiSchedulerReentryStableFrameCount, Is.EqualTo(120));
            Assert.That(gi.SimpleDdgiProbeResidencyMode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Dense));
            Assert.That(gi.SimpleDdgiSparsePhysicalPageBudget, Is.Zero);
            Assert.That(gi.SimpleDdgiSparseMinimumPhysicalPageBudget, Is.Zero);
            Assert.That(gi.SimpleDdgiSparseRetentionFrames, Is.EqualTo(120));
            Assert.That(gi.SimpleDdgiSparseMaximumAdmissionsPerFrame, Is.EqualTo(64));
            Assert.That(gi.SimpleDdgiSparseMaximumReceiverFeedbackRequests, Is.EqualTo(2_048));
            Assert.That(gi.SimpleDdgiSparseInactiveRetryFrames, Is.EqualTo(300));
            Assert.That(gi.SimpleDdgiViewForwardPlacementFraction,
                Is.EqualTo(0.6f));
        });
    }

    [Test]
    public void ViewForwardPlacementFraction_ClampsAndRoundTrips()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-forward-placement-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction =
                2.0f;
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction,
                Is.EqualTo(1.0f));

            settings.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction =
                0.72f;
            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);
            Assert.That(
                loaded.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction,
                Is.EqualTo(0.72f));

            loaded.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction =
                -1.0f;
            Assert.That(
                loaded.GlobalIllumination.SimpleDdgiViewForwardPlacementFraction,
                Is.Zero);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestCase(DdgiQualityTier.DdgiLow, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(DdgiQualityTier.DdgiMedium, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(DdgiQualityTier.DdgiHigh, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(DdgiQualityTier.DdgiUltra, SimpleDdgiProbeResidencyMode.SparseNearRing, 1_440, 1_152)]
    public void QualityTiersSelectExplicitResidencyPolicy(
        DdgiQualityTier tier,
        SimpleDdgiProbeResidencyMode expectedMode,
        int expectedBudget,
        int expectedMinimumBudget)
    {
        GlobalIlluminationSettings gi = new();

        gi.ApplyDdgiQualityTier(tier);

        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiProbeResidencyMode, Is.EqualTo(expectedMode));
            Assert.That(gi.SimpleDdgiSparsePhysicalPageBudget, Is.EqualTo(expectedBudget));
            Assert.That(gi.SimpleDdgiSparseMinimumPhysicalPageBudget, Is.EqualTo(expectedMinimumBudget));
            Assert.That(gi.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                Is.EqualTo(tier == DdgiQualityTier.DdgiUltra ? 4_096 : 2_048));
            Assert.That(gi.SimpleDdgiSparseInactiveRetryFrames, Is.EqualTo(300));
        });
    }

    [Test]
    public void SchedulerModeRoundTripsThroughRenderSettingsFile()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-scheduler-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiSchedulerMode = SimpleDdgiSchedulerMode.GpuResident;
            settings.GlobalIllumination.SimpleDdgiSchedulerReentryStableFrameCount = 240;
            settings.GlobalIllumination.SimpleDdgiProbeResidencyMode =
                SimpleDdgiProbeResidencyMode.Shadow;
            settings.GlobalIllumination.SimpleDdgiSparsePhysicalPageBudget = 777;
            settings.GlobalIllumination.SimpleDdgiSparseMinimumPhysicalPageBudget = 640;
            settings.GlobalIllumination.SimpleDdgiSparseRetentionFrames = 180;
            settings.GlobalIllumination.SimpleDdgiSparseMaximumAdmissionsPerFrame = 48;
            settings.GlobalIllumination.SimpleDdgiSparseMaximumReceiverFeedbackRequests = 999;
            settings.GlobalIllumination.SimpleDdgiSparseInactiveRetryFrames = 450;
            settings.GlobalIllumination.SimpleDdgiReceiverContributionFeedbackEnabled = false;
            settings.GlobalIllumination.SimpleDdgiPersistentWarmStartEnabled = false;
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiSchedulerMode,
                    Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiSchedulerReentryStableFrameCount,
                    Is.EqualTo(240));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiProbeResidencyMode,
                    Is.EqualTo(SimpleDdgiProbeResidencyMode.Shadow));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparsePhysicalPageBudget,
                    Is.EqualTo(777));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparseMinimumPhysicalPageBudget,
                    Is.EqualTo(640));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparseRetentionFrames,
                    Is.EqualTo(180));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    Is.EqualTo(48));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                    Is.EqualTo(999));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSparseInactiveRetryFrames,
                    Is.EqualTo(450));
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiReceiverContributionFeedbackEnabled,
                    Is.False);
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiPersistentWarmStartEnabled,
                    Is.False);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SchedulerModeHasExplicitSmokeOverride()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
            new[] { "--simple-ddgi-scheduler-mode=gpu-resident" });

        Assert.Multiple(() =>
        {
            Assert.That(options.SimpleDdgiSchedulerModeOverride, Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
        });
    }

    [Test]
    public void SparseResidencyPolicyHasCompleteSmokeOverrides()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--simple-ddgi-residency-mode=shadow",
            "--simple-ddgi-sparse-page-budget=777",
            "--simple-ddgi-sparse-min-page-budget=640",
            "--simple-ddgi-sparse-retention-frames=180",
            "--simple-ddgi-sparse-max-admissions=48",
            "--simple-ddgi-sparse-max-feedback=999",
            "--simple-ddgi-sparse-inactive-retry-frames=450"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.SimpleDdgiProbeResidencyModeOverride,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Shadow));
            Assert.That(options.SimpleDdgiSparsePhysicalPageBudgetOverride, Is.EqualTo(777));
            Assert.That(options.SimpleDdgiSparseMinimumPhysicalPageBudgetOverride, Is.EqualTo(640));
            Assert.That(options.SimpleDdgiSparseRetentionFramesOverride, Is.EqualTo(180));
            Assert.That(options.SimpleDdgiSparseMaximumAdmissionsOverride, Is.EqualTo(48));
            Assert.That(options.SimpleDdgiSparseMaximumReceiverFeedbackOverride, Is.EqualTo(999));
            Assert.That(options.SimpleDdgiSparseInactiveRetryFramesOverride, Is.EqualTo(450));
            Assert.That(options.Enabled, Is.True);
        });
    }
}
