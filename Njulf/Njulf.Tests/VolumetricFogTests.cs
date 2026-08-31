using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class VolumetricFogTests
{
    private static readonly VolumetricFogCapabilities Supported = new(
        LayeredStorageImages: true,
        RequiredFormats: true,
        RayQueryEmissiveVisibility: true,
        QualifiedProfile: true);

    [TestCase(FogDebugView.None, false)]
    [TestCase(FogDebugView.FoggedScene, false)]
    [TestCase(FogDebugView.FogFactor, true)]
    [TestCase(FogDebugView.DistanceFog, true)]
    [TestCase(FogDebugView.Density, true)]
    [TestCase(FogDebugView.HistoryConfidence, true)]
    public void Composite_OnlyBypassesToneMappingForNormalizedFogDiagnostics(
        FogDebugView debugView,
        bool expectedDisplayReferred)
    {
        Assert.That(
            FogDebugViewPolicy.IsDisplayReferred(debugView),
            Is.EqualTo(expectedDisplayReferred));
    }

    [Test]
    public void LogarithmicSlices_AreMonotonicAndInvertible()
    {
        VolumetricFogQualityProfile profile =
            VolumetricFogQualityProfile.ForPreset(RenderQualityPreset.High);
        VolumetricFogGridLayout layout = VolumetricFogGridLayout.Create(
            2560, 1440, 0.1f, 1000f, 250f, profile);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Width, Is.EqualTo(222u));
            Assert.That(layout.Height, Is.EqualTo(128u));
            Assert.That(layout.Depth, Is.EqualTo(80u));
            Assert.That(layout.SliceBoundary(0), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(layout.SliceBoundary(layout.Depth),
                Is.EqualTo(250f).Within(1e-3f));
        });

        float previous = layout.SliceBoundary(0);
        for (uint slice = 1; slice <= layout.Depth; slice++)
        {
            float boundary = layout.SliceBoundary(slice);
            Assert.That(boundary, Is.GreaterThan(previous));
            Assert.That(layout.ContinuousSlice(boundary),
                Is.EqualTo(slice).Within(1e-3f));
            previous = boundary;
        }
    }

    [Test]
    public void HighGrid_UsesEightPixelFroxelsAtTheShowcaseResolution()
    {
        VolumetricFogQualityProfile profile =
            VolumetricFogQualityProfile.ForPreset(RenderQualityPreset.DdgiHigh);
        VolumetricFogGridLayout layout = VolumetricFogGridLayout.Create(
            1600, 900, 0.1f, 1000f, 36f, profile);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Width, Is.EqualTo(208u));
            Assert.That(layout.Height, Is.EqualTo(121u));
            Assert.That(layout.Depth, Is.EqualTo(80u));
            Assert.That(layout.PixelSize, Is.EqualTo(8u));
        });
    }

    [Test]
    public void Reconstruction_UsesJitterCorrectTrilinearHistoryAndBilinearUpsampling()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string common = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_common.glsl"));
        string temporal = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_temporal.comp"));
        string resolve = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_resolve.comp"));
        string composite = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_composite.comp"));

        Assert.Multiple(() =>
        {
            Assert.That(common,
                Does.Contain("FroxelTemporalSampleOffset"));
            Assert.That(common,
                Does.Contain("FroxelHistorySampler"));
            Assert.That(temporal,
                Does.Contain("FroxelSamplePreviousHistory"));
            Assert.That(temporal,
                Does.Contain("historyWorldPosition = worldPosition - velocity * deltaTime"));
            Assert.That(resolve,
                Does.Contain("FroxelIntegratedAtBoundary"));
            Assert.That(resolve,
                Does.Contain("FroxelCubicWeights"));
            Assert.That(composite,
                Does.Contain("float bilinearWeight"));
        });
    }

    [Test]
    public void DdgiBounceSidecar_DoesNotResizeWithLiveResidency()
    {
        string rendererDirectory = FindRepoDirectory("Njulf.Rendering");
        string source = File.ReadAllText(Path.Combine(
            rendererDirectory, "Pipeline", "FroxelFogRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount"));
            Assert.That(source, Does.Not.Contain(
                "int bounceProbeCapacity = Math.Max"));
        });
    }

    [Test]
    public void DdgiBounceSidecar_ResetsOnlyForPhysicalOwnershipReplacement()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                FroxelFogRenderer.RequiresDdgiSidecarReset(7u, 7u),
                Is.False);
            Assert.That(
                FroxelFogRenderer.RequiresDdgiSidecarReset(7u, 8u),
                Is.True);
        });
    }

    [Test]
    public void TemporalIntegration_FiltersExtinctionWithRadiance()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string temporal = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_temporal.comp"));
        string integrate = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_integrate.comp"));

        Assert.Multiple(() =>
        {
            Assert.That(temporal,
                Does.Contain("float accumulatedExtinction = mix("));
            Assert.That(integrate,
                Does.Contain("vec4 temporalSource = imageLoad(FroxelHistoryWrite"));
            Assert.That(integrate,
                Does.Contain("float extinction = max(temporalSource.a, 0.0)"));
        });
    }

    [Test]
    public void ProductionProfiles_ReserveDensePerClusterSourceLists()
    {
        VolumetricFogQualityProfile high =
            VolumetricFogQualityProfile.ForPreset(RenderQualityPreset.DdgiHigh);
        VolumetricFogQualityProfile ultra =
            VolumetricFogQualityProfile.ForPreset(RenderQualityPreset.Ultra);

        Assert.Multiple(() =>
        {
            Assert.That(high.ClusterReferenceCapacity,
                Is.GreaterThanOrEqualTo(192u));
            Assert.That(ultra.ClusterReferenceCapacity,
                Is.GreaterThanOrEqualTo(128u));
        });
    }

    [TestCase(RenderQualityPreset.Low)]
    [TestCase(RenderQualityPreset.Medium)]
    public void Auto_UsesAnalyticFogBelowHighClassPresets(
        RenderQualityPreset preset)
    {
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            enabled: true,
            FogTechnique.Auto,
            preset,
            Supported,
            plannedBytes: 1,
            budgetBytes: 1024);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.False);
            Assert.That(admission.Effective, Is.EqualTo(FogTechnique.Analytic));
            Assert.That(admission.Status, Is.EqualTo("analytic-selected"));
        });
    }

    [Test]
    public void Auto_FailsClosedUntilProfileIsQualified()
    {
        var unqualified = Supported with { QualifiedProfile = false };
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            enabled: true,
            FogTechnique.Auto,
            RenderQualityPreset.High,
            unqualified,
            plannedBytes: 1,
            budgetBytes: 1024);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.False);
            Assert.That(admission.Effective, Is.EqualTo(FogTechnique.Analytic));
            Assert.That(admission.Status,
                Is.EqualTo("froxel-profile-not-qualified"));
        });
    }

    [Test]
    public void ExplicitFroxel_AllowsUnqualifiedSingleScatteringBringUp()
    {
        var unqualified = Supported with { QualifiedProfile = false };
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            enabled: true,
            FogTechnique.Froxel,
            RenderQualityPreset.High,
            unqualified,
            plannedBytes: 512,
            budgetBytes: 1024);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.True);
            Assert.That(admission.Effective, Is.EqualTo(FogTechnique.Froxel));
            Assert.That(admission.PlannedBytes, Is.EqualTo(512));
        });
    }

    [Test]
    public void ExplicitFroxel_FailsClosedWithoutAHighClassProfile()
    {
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            enabled: true,
            FogTechnique.Froxel,
            RenderQualityPreset.Medium,
            Supported,
            plannedBytes: 512,
            budgetBytes: 1024);

        Assert.Multiple(() =>
        {
            Assert.That(admission.Active, Is.False);
            Assert.That(admission.Effective, Is.EqualTo(FogTechnique.Analytic));
            Assert.That(admission.Status,
                Is.EqualTo("froxel-quality-profile-unavailable"));
        });
    }

    [Test]
    public void Froxel_FailsClosedForAZeroSizedRenderExtent()
    {
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            enabled: true,
            FogTechnique.Froxel,
            RenderQualityPreset.High,
            Supported,
            plannedBytes: 0,
            budgetBytes: 1024);

        Assert.That(admission.Status,
            Is.EqualTo("froxel-render-extent-unavailable"));
        Assert.That(admission.Active, Is.False);
    }

    [Test]
    public void ProductionProfiles_FitTheirDedicatedMemoryBudgets()
    {
        AssertProfileFits(
            RenderQualityPreset.High,
            2560,
            1440,
            128UL * 1024UL * 1024UL);
        AssertProfileFits(
            RenderQualityPreset.Ultra,
            3840,
            2160,
            320UL * 1024UL * 1024UL);
    }

    [TestCase(RenderQualityPreset.High, 3840u, 2160u)]
    [TestCase(RenderQualityPreset.Ultra, 3840u, 2160u)]
    public void GridCaps_IncreasePixelSizeWithoutCroppingTheCameraFrustum(
        RenderQualityPreset preset,
        uint width,
        uint height)
    {
        VolumetricFogQualityProfile profile =
            VolumetricFogQualityProfile.ForPreset(preset);
        VolumetricFogGridLayout layout = VolumetricFogGridLayout.Create(
            width, height, 0.1f, 1000f, 500f, profile);
        uint horizontalCells = layout.Width - layout.GuardBandPixels * 2u;
        uint verticalCells = layout.Height - layout.GuardBandPixels * 2u;

        Assert.Multiple(() =>
        {
            Assert.That(layout.PixelSize, Is.GreaterThanOrEqualTo(profile.PixelSize));
            Assert.That((ulong)horizontalCells * layout.PixelSize,
                Is.GreaterThanOrEqualTo(width));
            Assert.That((ulong)verticalCells * layout.PixelSize,
                Is.GreaterThanOrEqualTo(height));
        });
    }

    [Test]
    public void HenyeyGreenstein_IsIsotropicAtZeroAndNumericallyNormalized()
    {
        float isotropic = 1f / (4f * MathF.PI);
        Assert.That(VolumetricFogMath.HenyeyGreenstein(0.37f, 0f),
            Is.EqualTo(isotropic).Within(1e-7f));

        const int sampleCount = 100_000;
        double integral = 0.0;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            float cosine = -1f + 2f * (sample + 0.5f) / sampleCount;
            integral += VolumetricFogMath.HenyeyGreenstein(cosine, 0.73f);
        }
        integral *= 4.0 * Math.PI / sampleCount;
        Assert.That(integral, Is.EqualTo(1.0).Within(2e-4));
    }

    [Test]
    public void TemporalAndMultipleScatteringGuards_FailClosed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VolumetricFogMath.RejectHistory(0.1f, 0.12f), Is.False);
            Assert.That(VolumetricFogMath.RejectHistory(0.1f, 0.3f), Is.True);
            Assert.That(VolumetricFogMath.BoundMultipleScattering(4f, 9f),
                Is.EqualTo(2f));
            Assert.That(VolumetricFogMath.BoundMultipleScattering(4f, -1f),
                Is.Zero);
        });
    }

    [Test]
    public void OutputEvidence_RequiresFiniteNonEmptyMediumAndLighting()
    {
        var counters = new GPUVolumetricFogDiagnostics
        {
            SampleCount = 4,
            MediumNonEmptyCount = 128,
            MaximumExtinctionQ = 2048,
            ExtinctionSumQ = 4096,
            DirectNonZeroCount = 96,
            MaximumDirectLuminanceQ = 512,
            DirectLuminanceSumQ = 1024,
            DdgiSupportedCount = 64,
            MaximumOpacityQ = 32768,
            TransmittanceSumQ = 131070,
            HistoryAcceptedCount = 80,
            HistoryRejectedCount = 48,
            HistoryRejectedInvalidCount = 10,
            HistoryRejectedBoundsCount = 8,
            HistoryRejectedExtinctionCount = 12,
            HistoryRejectedRadianceCount = 16,
            HistoryRejectedVelocityCount = 2,
            AdmittedSourceCount = 7
        };

        VolumetricFogOutputEvidence evidence =
            VolumetricFogOutputEvidence.FromGpuCounters(
                counters,
                readbackValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Produced, Is.True);
            Assert.That(evidence.MaximumExtinction, Is.EqualTo(2f));
            Assert.That(evidence.MeanExtinction, Is.EqualTo(1f));
            Assert.That(evidence.MaximumDirectLuminance, Is.EqualTo(2f));
            Assert.That(evidence.MeanDirectLuminance, Is.EqualTo(1f));
            Assert.That(evidence.MinimumTransmittance,
                Is.EqualTo(1f - 32768f / 65535f).Within(1e-6f));
            Assert.That(evidence.MeanTransmittance, Is.EqualTo(0.5f)
                .Within(1e-4f));
            Assert.That(evidence.AdmittedSourceCount, Is.EqualTo(7));
            Assert.That(
                evidence.HistoryRejectedInvalidFroxelCount +
                evidence.HistoryRejectedBoundsFroxelCount +
                evidence.HistoryRejectedExtinctionFroxelCount +
                evidence.HistoryRejectedRadianceFroxelCount +
                evidence.HistoryRejectedVelocityFroxelCount,
                Is.EqualTo(evidence.HistoryRejectedFroxelCount));
        });

        counters.NonFiniteCount = 1;
        Assert.That(VolumetricFogOutputEvidence.FromGpuCounters(
            counters, readbackValid: true).Produced, Is.False);
    }

    [Test]
    public void SourceClustering_UsesOneCooperativeWorkgroupPerSource()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string rendererDirectory = FindRepoDirectory("Njulf.Rendering");
        string shader = File.ReadAllText(Path.Combine(
            shaderDirectory, "froxel_source_cull.comp"));
        string renderer = File.ReadAllText(Path.Combine(
            rendererDirectory, "Pipeline", "FroxelFogRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(shader,
                Does.Contain("gl_WorkGroupID.x"));
            Assert.That(shader,
                Does.Contain("ordinal += gl_WorkGroupSize.x"));
            Assert.That(renderer,
                Does.Contain("Dispatch(commandBuffer, 1, sourceGroupX, sourceGroupY"));
        });
    }

    [Test]
    public void OutputEvidence_FallsBackOnlyForBrokenGpuOutput()
    {
        GPUVolumetricFogDiagnostics counters = default;

        VolumetricFogOutputEvidence pending =
            VolumetricFogOutputEvidence.FromGpuCounters(
                counters,
                readbackValid: false);
        VolumetricFogOutputEvidence missingExecution =
            VolumetricFogOutputEvidence.FromGpuCounters(
                counters,
                readbackValid: true);
        counters.SampleCount = 4;
        VolumetricFogOutputEvidence physicallyEmpty =
            VolumetricFogOutputEvidence.FromGpuCounters(
                counters,
                readbackValid: true);
        counters.NonFiniteCount = 1;
        VolumetricFogOutputEvidence nonFinite =
            VolumetricFogOutputEvidence.FromGpuCounters(
                counters,
                readbackValid: true);

        Assert.Multiple(() =>
        {
            Assert.That(pending.RequiresAnalyticFallback, Is.False);
            Assert.That(missingExecution.RequiresAnalyticFallback, Is.True);
            Assert.That(physicallyEmpty.RequiresAnalyticFallback, Is.False);
            Assert.That(physicallyEmpty.Produced, Is.False);
            Assert.That(nonFinite.RequiresAnalyticFallback, Is.True);
        });
    }

    [TestCase(RenderQualityPreset.Low, false, false)]
    [TestCase(RenderQualityPreset.Medium, false, false)]
    [TestCase(RenderQualityPreset.High, true, false)]
    [TestCase(RenderQualityPreset.DdgiHigh, true, false)]
    [TestCase(RenderQualityPreset.Ultra, true, true)]
    public void QualityPreset_OwnsIndependentVolumetricQualifications(
        RenderQualityPreset preset,
        bool singleScattering,
        bool multipleScattering)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(preset);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Fog.Volumetric.SingleScatteringQualified,
                Is.EqualTo(singleScattering));
            Assert.That(settings.Fog.Volumetric.MultipleScatteringQualified,
                Is.EqualTo(multipleScattering));
        });
    }

    [Test]
    public void HighDdgiTier_ProvisionsL2ForFogAndGlossyReceivers()
    {
        var settings = new GlobalIlluminationSettings();
        settings.ApplyDdgiQualityTier(DdgiQualityTier.DdgiHigh);

        Assert.Multiple(() =>
        {
            Assert.That(settings.ConfiguredContentDependentFeatures &
                DdgiContentFeature.DirectionalRadiance,
                Is.EqualTo(DdgiContentFeature.DirectionalRadiance));
            Assert.That(settings.EffectiveSimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(settings.EffectiveSimpleDdgiGlossyTransportMode,
                Is.EqualTo(SimpleDdgiGlossyTransportMode.RecursiveCertified));
        });
    }

    private static void AssertProfileFits(
        RenderQualityPreset preset,
        uint width,
        uint height,
        ulong budget)
    {
        VolumetricFogQualityProfile profile =
            VolumetricFogQualityProfile.ForPreset(preset);
        VolumetricFogGridLayout layout = VolumetricFogGridLayout.Create(
            width, height, 0.05f, 2000f, 500f, profile);
        ulong planned = checked(
            VolumetricFogMemoryPlan.Create(layout, profile).TotalBytes +
            2UL * 1024UL * 1024UL);
        Assert.That(planned, Is.LessThanOrEqualTo(budget), preset.ToString());
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new AssertionException($"Could not find repo directory '{name}'.");
    }
}
