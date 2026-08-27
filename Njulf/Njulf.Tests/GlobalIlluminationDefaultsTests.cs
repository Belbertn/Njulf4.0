using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationDefaultsTests
{
    [Test]
    public void ReceiverCacheMode_DdgiHighDefaultsTemporalAndExactRemainsSelectable()
    {
        var settings = new RenderSettings();
        Assert.That(
            settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
            Is.EqualTo(SimpleDdgiReceiverCacheMode.TemporalAdaptive));

        settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
            SimpleDdgiReceiverCacheMode.Exact;
        Assert.That(
            settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
            Is.EqualTo(SimpleDdgiReceiverCacheMode.Exact));
    }

    [TestCase(RenderQualityPreset.Low)]
    [TestCase(RenderQualityPreset.Medium)]
    [TestCase(RenderQualityPreset.High)]
    [TestCase(RenderQualityPreset.Ultra)]
    [TestCase(RenderQualityPreset.DdgiHigh)]
    public void SurfaceAwareReceiverCache_IsExplicitAndExactRemainsAuthoritative(
        RenderQualityPreset preset)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardPlusPass.ShouldConsumeSimpleDdgiReceiverCache(
                    preset,
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial,
                    forceLegacyBenchmark: false,
                    forceExact: false),
                Is.True);
            Assert.That(
                ForwardPlusPass.ShouldConsumeSimpleDdgiReceiverCache(
                    preset,
                    SimpleDdgiReceiverCacheMode.Exact,
                    forceLegacyBenchmark: false,
                    forceExact: false),
                Is.False);
            Assert.That(
                ForwardPlusPass.ShouldConsumeSimpleDdgiReceiverCache(
                    preset,
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial,
                    forceLegacyBenchmark: false,
                    forceExact: true),
                Is.False);
            Assert.That(
                ForwardPlusPass.ShouldConsumeSimpleDdgiReceiverCache(
                    preset,
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial,
                    forceLegacyBenchmark: true,
                    forceExact: false),
                Is.True);
            Assert.That(
                ForwardPlusPass.ShouldConsumeSimpleDdgiReceiverCache(
                    preset,
                    SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial,
                    forceLegacyBenchmark: true,
                    forceExact: true),
                Is.False,
                "The exact oracle must win conflicting capture controls.");

            var presetSettings = new RenderSettings();
            presetSettings.ApplyQualityPreset(preset);
            SimpleDdgiReceiverCacheMode expected = preset is
                RenderQualityPreset.Medium or RenderQualityPreset.DdgiHigh
                    ? SimpleDdgiReceiverCacheMode.TemporalAdaptive
                    : SimpleDdgiReceiverCacheMode.Exact;
            Assert.That(
                presetSettings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
                Is.EqualTo(expected));
        });
    }

    [Test]
    public void ReceiverCacheMode_RoundTripsExplicitLegacyBenchmarkIntent()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-receiver-cache-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiReceiverCacheMode =
                SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark;
            settings.Save(path);

            Assert.That(
                RenderSettings.Load(path).GlobalIllumination
                    .SimpleDdgiReceiverCacheMode,
                Is.EqualTo(
                    SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void DdgiHigh_EmissiveBudgetCoversAuthenticatedSponzaPopulation()
    {
        const int authenticatedSponzaEmissiveTriangleCount = 10_306;
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);

        Assert.Multiple(() =>
        {
            Assert.That(
                GlobalIlluminationSettings.MaxDdgiEmissiveTriangleBudget,
                Is.EqualTo(16_384));
            Assert.That(
                settings.GlobalIllumination.DdgiEmissiveTriangleBudget,
                Is.GreaterThanOrEqualTo(
                    authenticatedSponzaEmissiveTriangleCount));
        });
    }

    [Test]
    public void SimpleDdgiHeaderBitPacking_PreservesFullFrameAndFlagWords()
    {
        const uint frameIndex = 0xf1234567u;
        const uint flags = 0xc0debeefu;

        float packedFrameIndex = SimpleDdgiVolumeManager.PackHeaderWord(frameIndex);
        float packedFlags = SimpleDdgiVolumeManager.PackHeaderWord(flags);

        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.SingleToUInt32Bits(packedFrameIndex), Is.EqualTo(frameIndex));
            Assert.That(BitConverter.SingleToUInt32Bits(packedFlags), Is.EqualTo(flags));
        });
    }

    [Test]
    public void NewGlobalIlluminationSettings_SelectSimpleDdgi()
    {
        var settings = new GlobalIlluminationSettings
        {
            Enabled = true,
            Mode = GlobalIlluminationMode.Ddgi,
            UseDdgi = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.EffectiveUseDdgi, Is.True);
        });
    }

    [TestCase(RenderQualityPreset.Low,
        SimpleDdgiNearFieldResidualMode.Off)]
    [TestCase(RenderQualityPreset.Medium,
        SimpleDdgiNearFieldResidualMode.Off)]
    [TestCase(RenderQualityPreset.High,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    [TestCase(RenderQualityPreset.Ultra,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    [TestCase(RenderQualityPreset.DdgiHigh,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    public void QualityPresets_SelectBoundedAdvancedGiPaths(
        RenderQualityPreset preset,
        SimpleDdgiNearFieldResidualMode expectedNearFieldResidualMode)
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            expectedNearFieldResidualMode ==
                SimpleDdgiNearFieldResidualMode.Off
                    ? SimpleDdgiNearFieldResidualMode.HiZAdaptive
                    : SimpleDdgiNearFieldResidualMode.Off;

        settings.ApplyQualityPreset(preset);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(gi.DdgiOpacityMicromapMode,
                Is.EqualTo(DdgiOpacityMicromapMode.ExtFourStateExperiment));
            Assert.That(gi.SimpleDdgiDirectionalGuidingMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment));
            Assert.That(gi.GiCausticMode,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(gi.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(expectedNearFieldResidualMode));
            Assert.That(gi.DdgiRayTracingPipelineExperimentEnabled, Is.False,
                "C2/SER remains explicitly excluded.");
        });
    }

    [Test]
    public void DirectionalGuiding_RemainsAnExplicitPersistableOptOut()
    {
        var settings = new GlobalIlluminationSettings();

        settings.SimpleDdgiDirectionalGuidingMode =
            SimpleDdgiDirectionalGuidingMode.Off;

        Assert.Multiple(() =>
        {
            Assert.That(settings.SimpleDdgiDirectionalGuidingMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.Off));
            Assert.That(
                settings.SimpleDdgiDirectionalRayGuidingExperimentEnabled,
                Is.False);
        });
    }

    [Test]
    public void NearFieldResidualMode_PreservesDurableValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That((uint)SimpleDdgiNearFieldResidualMode.Off, Is.Zero);
            Assert.That((uint)SimpleDdgiNearFieldResidualMode.Reference,
                Is.EqualTo(1u));
            Assert.That((uint)SimpleDdgiNearFieldResidualMode
                .HiZHalfResolutionExperiment, Is.EqualTo(2u));
            Assert.That((uint)SimpleDdgiNearFieldResidualMode.AutoQualified,
                Is.EqualTo(3u));
            Assert.That((uint)SimpleDdgiNearFieldResidualMode.HiZAdaptive,
                Is.EqualTo(4u));
        });
    }

    [Test]
    public void ExplicitAdvancedGiModesDoNotRequirePromotionManifest()
    {
        AdvancedGiPrerequisiteGateResult missing =
            AdvancedGiPrerequisiteGateResult.Missing("not-configured");

        Assert.Multiple(() =>
        {
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                missing), Is.True);
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                DdgiOpacityMicromapMode.ExtFourStateExperiment,
                missing), Is.True);
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment,
                missing), Is.True);
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                GiCausticMode.WorldCacheExperiment,
                missing), Is.True);
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                SimpleDdgiNearFieldResidualMode
                    .HiZAdaptive,
                missing), Is.True);
            Assert.That(AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                GiCausticMode.AutoQualified,
                missing), Is.False);
        });
    }

    [Test]
    public void NewGlobalIlluminationSettings_PreserveLowEnergyTransportPropagation()
    {
        var settings = new GlobalIlluminationSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.SimpleDdgiStoragePackingMode,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
            Assert.That(settings.SimpleDdgiSourceCacheLayoutMode,
                Is.EqualTo(SimpleDdgiSourceCacheLayoutMode.Auto));
            Assert.That(settings.SimpleDdgiRefinementBricksEnabled, Is.True);
            Assert.That(
                settings.SimpleDdgiRefinementMinimumEmissiveLuminanceNits,
                Is.EqualTo(200f));
            Assert.That(settings.SimpleDdgiNearVisibilitySidecarEnabled, Is.True);
            Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold,
                Is.EqualTo(1.0f));
            Assert.That(settings.SimpleDdgiSampledAtlasEnabled, Is.True);
            Assert.That(settings.SimpleDdgiSampledAtlasCoverageMode,
                Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant));
            Assert.That(settings.SimpleDdgiTransportTailRelativeTolerance, Is.EqualTo(0.025f));
            Assert.That(settings.SimpleDdgiTransportMaximumSolverGenerations, Is.EqualTo(8));
            Assert.That(settings.SimpleDdgiTransportSourceRefreshFrames, Is.EqualTo(2_048));
        });
    }

    [TestCase(RenderQualityPreset.Low, false, SimpleDdgiSampledAtlasCoverageMode.Disabled)]
    [TestCase(RenderQualityPreset.Medium, false, SimpleDdgiSampledAtlasCoverageMode.Disabled)]
    [TestCase(RenderQualityPreset.High, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    [TestCase(RenderQualityPreset.Ultra, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    [TestCase(RenderQualityPreset.DdgiHigh, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    public void QualityPreset_UsesPromotedSimpleDdgiStorageDefaults(
        RenderQualityPreset preset,
        bool expectedSampledAtlasEnabled,
        SimpleDdgiSampledAtlasCoverageMode expectedCoverageMode)
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(preset);
        bool highTier = preset is RenderQualityPreset.High or
            RenderQualityPreset.Ultra or RenderQualityPreset.DdgiHigh;

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.SimpleDdgiStoragePackingMode,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
            Assert.That(settings.GlobalIllumination.SimpleDdgiSourceCacheLayoutMode,
                Is.EqualTo(SimpleDdgiSourceCacheLayoutMode.Auto));
            Assert.That(settings.GlobalIllumination.SimpleDdgiRefinementBricksEnabled,
                Is.EqualTo(highTier));
            Assert.That(settings.GlobalIllumination.SimpleDdgiNearVisibilitySidecarEnabled,
                Is.EqualTo(highTier));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold,
                Is.EqualTo(1.0f));
            Assert.That(settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled,
                Is.EqualTo(expectedSampledAtlasEnabled));
            Assert.That(settings.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode,
                Is.EqualTo(expectedCoverageMode));
        });
    }

    [TestCase(RenderQualityPreset.Low, 4_096)]
    [TestCase(RenderQualityPreset.Medium, 3_072)]
    [TestCase(RenderQualityPreset.High, 2_048)]
    [TestCase(RenderQualityPreset.Ultra, 1_536)]
    [TestCase(RenderQualityPreset.DdgiHigh, 2_048)]
    public void QualityPreset_SourceRefreshLeavesACompleteSolverQuietWindow(
        RenderQualityPreset preset,
        int expectedRefreshFrames)
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(preset);

        Assert.Multiple(() =>
        {
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiTransportSourceRefreshFrames,
                Is.EqualTo(expectedRefreshFrames));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiTransportSourceRefreshFrames,
                Is.GreaterThanOrEqualTo(120));
        });
    }

    [TestCase(RenderQualityPreset.Low)]
    [TestCase(RenderQualityPreset.Medium)]
    [TestCase(RenderQualityPreset.High)]
    [TestCase(RenderQualityPreset.Ultra)]
    [TestCase(RenderQualityPreset.DdgiHigh)]
    public void QualityPreset_RestoresSimpleDdgiAsDefault(RenderQualityPreset preset)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(preset);

        if (settings.GlobalIllumination.Enabled && settings.GlobalIllumination.UseDdgi)
        {
            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance, Is.EqualTo(0.025f));
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportMaximumSolverGenerations, Is.EqualTo(8));
        });
    }

    [TestCase(RenderQualityPreset.Low, 2, 16, 8, 16, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(RenderQualityPreset.Medium, 2, 22, 11, 22, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(RenderQualityPreset.High, 3, 28, 14, 28, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    [TestCase(RenderQualityPreset.Ultra, 3, 32, 16, 32, SimpleDdgiProbeResidencyMode.SparseNearRing, 1_440, 1_152)]
    [TestCase(RenderQualityPreset.DdgiHigh, 3, 28, 14, 28, SimpleDdgiProbeResidencyMode.Dense, 0, 0)]
    public void QualityPreset_ReplacesTheCompleteSimpleDdgiProfileRegardlessOfPriorTier(
        RenderQualityPreset preset,
        int expectedRingCount,
        int expectedNearX,
        int expectedNearY,
        int expectedNearZ,
        SimpleDdgiProbeResidencyMode expectedResidencyMode,
        int expectedPageBudget,
        int expectedMinimumPageBudget)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(
            preset == RenderQualityPreset.Ultra
                ? RenderQualityPreset.Low
                : RenderQualityPreset.Ultra);

        settings.ApplyQualityPreset(preset);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiRingCount, Is.EqualTo(expectedRingCount));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(expectedNearX));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(expectedNearY));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(expectedNearZ));
            Assert.That(gi.SimpleDdgiProbeResidencyMode, Is.EqualTo(expectedResidencyMode));
            Assert.That(gi.SimpleDdgiSparsePhysicalPageBudget, Is.EqualTo(expectedPageBudget));
            Assert.That(
                gi.SimpleDdgiSparseMinimumPhysicalPageBudget,
                Is.EqualTo(expectedMinimumPageBudget));
        });
    }

    [Test]
    public void QualityPreset_PrepublicationGuardRejectsWithoutPartialMutation()
    {
        var settings = new RenderSettings();
        RenderQualityPreset originalPreset = settings.QualityPreset;
        float originalResolutionScale = settings.ResolutionScale;
        settings.QualityPresetChanging += preset =>
        {
            if (preset == RenderQualityPreset.Low)
                throw new InvalidOperationException("tier budget rejected");
        };

        Assert.That(
            () => settings.ApplyQualityPreset(RenderQualityPreset.Low),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("tier budget rejected"));
        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset, Is.EqualTo(originalPreset));
            Assert.That(settings.ResolutionScale, Is.EqualTo(originalResolutionScale));
        });
    }

    [Test]
    public void SettingsFileWithoutBackendSelector_DefaultsToSimpleDdgi()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-simple-ddgi-default-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "QualityPreset": "DdgiHigh",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "Ddgi",
                    "UseDdgi": true
                  }
                }
                """);

            RenderSettings settings = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
                Assert.That(
                    settings.GlobalIllumination.SimpleDdgiSchedulerMode,
                    Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
                Assert.That(
                    settings.GlobalIllumination.SimpleDdgiStoragePackingMode,
                    Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
                Assert.That(
                    settings.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode,
                    Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant));
                Assert.That(
                    settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled,
                    Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestCase(RenderQualityPreset.Low, false, SimpleDdgiSampledAtlasCoverageMode.Disabled)]
    [TestCase(RenderQualityPreset.Medium, false, SimpleDdgiSampledAtlasCoverageMode.Disabled)]
    [TestCase(RenderQualityPreset.High, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    [TestCase(RenderQualityPreset.Ultra, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    [TestCase(RenderQualityPreset.DdgiHigh, true, SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant)]
    public void SettingsFileWithoutRepresentationOverrides_InheritsPromotedTierDefaults(
        RenderQualityPreset preset,
        bool expectedSampledAtlasEnabled,
        SimpleDdgiSampledAtlasCoverageMode expectedCoverageMode)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-promoted-ddgi-default-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, $$"""
                {
                  "QualityPreset": "{{preset}}",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "Ddgi",
                    "UseDdgi": true
                  }
                }
                """);

            GlobalIlluminationSettings gi = RenderSettings.Load(path).GlobalIllumination;

            Assert.Multiple(() =>
            {
                Assert.That(gi.SimpleDdgiStoragePackingMode,
                    Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
                Assert.That(gi.SimpleDdgiSampledAtlasEnabled,
                    Is.EqualTo(expectedSampledAtlasEnabled));
                Assert.That(gi.SimpleDdgiSampledAtlasCoverageMode,
                    Is.EqualTo(expectedCoverageMode));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SettingsFileWithRemovedLegacySelector_IsIgnored()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"render-settings-removed-legacy-ddgi-selector-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "QualityPreset": "DdgiHigh",
                  "GlobalIllumination": {
                    "Enabled": true,
                    "Mode": "Ddgi",
                    "UseDdgi": true,
                    "UseRayQueryBackend": true,
                    "DdgiSimpleEnabled": false
                  }
                }
                """);

            RenderSettings settings = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
