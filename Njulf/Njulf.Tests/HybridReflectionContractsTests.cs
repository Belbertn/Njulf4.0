using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class HybridReflectionContractsTests
{
    [Test]
    public void ReceiverShaderSpecialization_TracksEffectiveFeatureMask()
    {
        var settings = new RenderSettings();
        settings.PerformanceOptimizations.EnabledFeatures =
            PerformanceOptimizationFeature
                .HybridOwnershipProjectionElision |
            PerformanceOptimizationFeature.ScreenLocalReceiverAdmission;
        uint optimized = MeshPipeline
            .ResolveForwardPerformanceSpecializationMask(settings);
        settings.PerformanceOptimizations.Enabled = false;
        uint baseline = MeshPipeline
            .ResolveForwardPerformanceSpecializationMask(settings);

        Assert.Multiple(() =>
        {
            Assert.That(
                optimized,
                Is.EqualTo((uint)(
                    PerformanceOptimizationFeature
                        .HybridOwnershipProjectionElision |
                    PerformanceOptimizationFeature
                        .ScreenLocalReceiverAdmission)));
            Assert.That(baseline, Is.Zero);
            Assert.That(
                MeshPipeline.UsesForwardPerformanceSpecialization(
                    "forward_opaque_ddgi_cache_required.frag.spv"),
                Is.True);
            Assert.That(
                MeshPipeline.UsesForwardPerformanceSpecialization(
                    "forward_opaque_ddgi.frag.spv"),
                Is.False);
            Assert.That(
                MeshPipeline.UsesForwardPerformanceSpecialization(
                    "forward_opaque_ddgi_hybrid_reflection.frag.spv"),
                Is.True);
            Assert.That(
                MeshPipeline.ForwardPerformanceSpecializationConstantId,
                Is.EqualTo(31u));
            Assert.That(
                MeshPipeline.ForwardReceiverCacheLaneSpecializationConstantId,
                Is.EqualTo(30u));
            Assert.That(
                MeshPipeline.ForwardReceiverCacheCombinedLane,
                Is.Zero);
            Assert.That(
                MeshPipeline.ForwardReceiverCacheAcceptedLane,
                Is.EqualTo(1u));
            Assert.That(
                MeshPipeline.ForwardReceiverCacheExactFallbackLane,
                Is.EqualTo(2u));
        });
    }

    [Test]
    public void SplitProgramSelector_DoesNotFragmentNativePipelineIdentity()
    {
        var settings = new RenderSettings();
        settings.PerformanceOptimizations.EnabledFeatures =
            PerformanceOptimizationFeature.HybridOwnershipProjectionElision |
            PerformanceOptimizationFeature.SplitHybridForwardPrograms;

        uint splitMask = MeshPipeline
            .ResolveForwardPerformanceSpecializationMask(settings);
        (int splitFirst, int splitLimit) = MeshPipeline
            .ResolveHybridReflectionPerformancePipelineRange(settings);

        settings.PerformanceOptimizations.EnabledFeatures &=
            ~PerformanceOptimizationFeature.SplitHybridForwardPrograms;
        uint combinedMask = MeshPipeline
            .ResolveForwardPerformanceSpecializationMask(settings);
        (int combinedFirst, int combinedLimit) = MeshPipeline
            .ResolveHybridReflectionPerformancePipelineRange(settings);

        Assert.Multiple(() =>
        {
            Assert.That(splitMask, Is.EqualTo(combinedMask));
            Assert.That(
                splitMask,
                Is.EqualTo((uint)PerformanceOptimizationFeature
                    .HybridOwnershipProjectionElision));
            Assert.That((splitFirst, splitLimit), Is.EqualTo((2, 4)));
            Assert.That((combinedFirst, combinedLimit), Is.EqualTo((1, 2)));
        });
    }

    [TestCase(Format.R32G32B32A32Uint, 16)]
    [TestCase(Format.R16G16B16A16Sfloat, 8)]
    [TestCase(Format.R16G16Sfloat, 4)]
    [TestCase(Format.R32G32Uint, 8)]
    public void RenderTargetAccounting_CoversEveryHybridReflectionFormat(
        Format format,
        int expectedBytesPerPixel)
    {
        Assert.That(
            RenderTarget.CalculateByteSize(1u, 1u, format),
            Is.EqualTo((ulong)expectedBytesPerPixel));
    }

    [Test]
    public void ModeResolver_PreservesHybridWhenEveryCapabilityIsReady()
    {
        var settings = new ReflectionSettings
        {
            Mode = ReflectionMode.HybridRayQuery
        };

        ReflectionModeResolution resolution = ReflectionModeResolver.Resolve(
            settings,
            new ReflectionModeCapabilities(true, true, true, true, true));

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Requested,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(resolution.Effective,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(resolution.Reason,
                Is.EqualTo(ReflectionFallbackReason.None));
            Assert.That(resolution.UsesDeferredPath, Is.True);
            Assert.That(resolution.UsesRayQueries, Is.True);
        });
    }

    [Test]
    public void ModeResolver_DemotesHybridToSsrWhenRayQueriesAreUnavailable()
    {
        var settings = new ReflectionSettings
        {
            Mode = ReflectionMode.HybridRayQuery
        };

        ReflectionModeResolution resolution = ReflectionModeResolver.Resolve(
            settings,
            new ReflectionModeCapabilities(true, true, false, true, true));

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Effective,
                Is.EqualTo(ReflectionMode.StaticProbesAndSsr));
            Assert.That(resolution.Reason,
                Is.EqualTo(ReflectionFallbackReason.RayQueryUnsupported));
            Assert.That(resolution.UsesDeferredPath, Is.True);
            Assert.That(resolution.UsesRayQueries, Is.False);
        });
    }

    [TestCase(false, true, ReflectionFallbackReason.ReceiverPayloadUnavailable)]
    [TestCase(true, false, ReflectionFallbackReason.HiZUnavailable)]
    public void ModeResolver_DemotesToProbesWhenScreenPathIsUnavailable(
        bool receiverPayload,
        bool hiZ,
        ReflectionFallbackReason expectedReason)
    {
        var settings = new ReflectionSettings
        {
            Mode = ReflectionMode.HybridRayQuery
        };

        ReflectionModeResolution resolution = ReflectionModeResolver.Resolve(
            settings,
            new ReflectionModeCapabilities(
                receiverPayload, hiZ, true, true, true));

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Effective,
                Is.EqualTo(ReflectionMode.StaticProbes));
            Assert.That(resolution.Reason, Is.EqualTo(expectedReason));
            Assert.That(resolution.UsesDeferredPath, Is.False);
        });
    }

    [Test]
    public void BudgetPlanner_UsesRoughnessBandsAndStrictGlobalCapacity()
    {
        var settings = new ReflectionSettings
        {
            SsrFullResolutionRoughness = 0.2f,
            SsrHalfResolutionRoughness = 0.5f,
            SsrQuarterResolutionRoughness = 0.8f,
            RayQueryPixelBudgetFraction = 0.125f
        };

        Assert.Multiple(() =>
        {
            Assert.That(HybridReflectionBudgetPlanner.ResolveResolutionTier(
                    settings, 0.2f),
                Is.EqualTo(ReflectionResolutionTier.Full));
            Assert.That(HybridReflectionBudgetPlanner.ResolveResolutionTier(
                    settings, 0.21f),
                Is.EqualTo(ReflectionResolutionTier.Half));
            Assert.That(HybridReflectionBudgetPlanner.ResolveResolutionTier(
                    settings, 0.51f),
                Is.EqualTo(ReflectionResolutionTier.Quarter));
            Assert.That(HybridReflectionBudgetPlanner.ResolveResolutionTier(
                    settings, 0.81f),
                Is.EqualTo(ReflectionResolutionTier.AnalyticFallback));
            Assert.That(HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
                    settings, 1920u, 1080u),
                Is.EqualTo(259_200u));

            settings.RayQueryPixelBudgetFraction = 0.0f;
            Assert.That(HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
                    settings, 1920u, 1080u),
                Is.Zero);
        });
    }

    [Test]
    public void BudgetPlanner_ReservesFullRateForSharpGlassAndMirrors()
    {
        var settings = new ReflectionSettings
        {
            SsrFullResolutionRoughness = 0.2f,
            SsrHalfResolutionRoughness = 0.5f,
            SsrQuarterResolutionRoughness = 0.8f
        };

        Assert.Multiple(() =>
        {
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.05f, 0.04f,
                        1.0f, ReflectionLobeFlags.Transmissive),
                Is.EqualTo(ReflectionResolutionTier.Full),
                "A smooth transmitted window keeps full-rate scene detail.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.05f, 0.90f,
                        1.0f, ReflectionLobeFlags.None),
                Is.EqualTo(ReflectionResolutionTier.Full),
                "A smooth high-F0 mirror keeps full-rate scene detail.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.15f, 0.04f,
                        1.0f, ReflectionLobeFlags.None),
                Is.EqualTo(ReflectionResolutionTier.Half),
                "Ordinary glossy dielectrics retain geometry at lower rate.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.20f, 0.90f,
                        1.0f, ReflectionLobeFlags.BroadAnisotropic),
                Is.EqualTo(ReflectionResolutionTier.Half),
                "A still-sharp anisotropic conductor is demoted exactly once.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.40f, 0.90f,
                        1.0f, ReflectionLobeFlags.BroadAnisotropic),
                Is.EqualTo(ReflectionResolutionTier.Quarter),
                "Brushed metal keeps sparse scene grounding without detailed rays.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.60f, 0.04f,
                        1.0f, ReflectionLobeFlags.None),
                Is.EqualTo(ReflectionResolutionTier.AnalyticFallback),
                "Broad low-F0 surfaces stay on the DDGI analytic base instead of tracing scene detail.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.60f, 0.90f,
                        1.0f, ReflectionLobeFlags.None),
                Is.EqualTo(ReflectionResolutionTier.Quarter),
                "High-F0 surfaces retain sparse geometric detail in the quarter band.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.60f, 0.04f,
                        1.0f, ReflectionLobeFlags.Transmissive),
                Is.EqualTo(ReflectionResolutionTier.Quarter),
                "Windows retain sparse geometric detail in the quarter band.");
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveAdaptiveResolutionTier(settings, 0.85f, 0.90f,
                        1.0f, ReflectionLobeFlags.None),
                Is.EqualTo(ReflectionResolutionTier.AnalyticFallback),
                "Only the configured roughness cutoff selects analytic fallback.");
        });
    }

    [Test]
    public void BudgetPlanner_UsesConservativeThenFeedbackDrivenSpatialAdmission()
    {
        const uint width = 1920u;
        const uint height = 1080u;
        const uint capacity = 16_200u;

        uint coldThreshold = HybridReflectionBudgetPlanner
            .ResolveRayQueryAdmissionThreshold(
                capacity, width, height, 0u,
                previousRequestCountValid: false);
        uint feedbackThreshold = HybridReflectionBudgetPlanner
            .ResolveRayQueryAdmissionThreshold(
                capacity, width, height, 173_698u,
                previousRequestCountValid: true);
        uint unboundedThreshold = HybridReflectionBudgetPlanner
            .ResolveRayQueryAdmissionThreshold(
                capacity, width, height, 1_000u,
                previousRequestCountValid: true);

        double coldProbability = coldThreshold / (double)uint.MaxValue;
        double feedbackProbability = feedbackThreshold / (double)uint.MaxValue;
        Assert.Multiple(() =>
        {
            Assert.That(coldProbability,
                Is.EqualTo(capacity * 0.9 / (width * (double)height))
                    .Within(1.0e-8));
            Assert.That(feedbackProbability,
                Is.EqualTo(capacity * 0.9 / 173_698.0)
                    .Within(1.0e-8));
            Assert.That(feedbackThreshold, Is.GreaterThan(coldThreshold));
            Assert.That(unboundedThreshold, Is.EqualTo(uint.MaxValue));
            Assert.That(HybridReflectionBudgetPlanner
                    .ResolveRayQueryAdmissionThreshold(
                        0u, width, height, 173_698u, true),
                Is.Zero);
        });
    }

    [Test]
    public void BudgetController_UsesCompletedTimingCadenceAndOverflowReduction()
    {
        var controller = new HybridReflectionBudgetController();

        HybridReflectionBudgetDecision firstReduction = controller.Observe(
            new HybridReflectionBudgetSample(4_000, true, false));
        HybridReflectionBudgetDecision overflowReduction = controller.Observe(
            new HybridReflectionBudgetSample(4_000, true, true));
        HybridReflectionBudgetDecision invalid = controller.Observe(
            new HybridReflectionBudgetSample(-1, false, false));

        Assert.Multiple(() =>
        {
            Assert.That(firstReduction.Changed, Is.True);
            Assert.That(firstReduction.QualityStep,
                Is.EqualTo(HybridReflectionQualityStep.Balanced));
            Assert.That(overflowReduction.Changed, Is.True,
                "A verified overflow reduces one step even inside the ordinary cadence window.");
            Assert.That(overflowReduction.QualityStep,
                Is.EqualTo(HybridReflectionQualityStep.Reduced));
            Assert.That(invalid.Changed, Is.False);
            Assert.That(invalid.QualityStep,
                Is.EqualTo(HybridReflectionQualityStep.Reduced));
        });

        controller.Reset(HybridReflectionQualityStep.Reduced);
        HybridReflectionBudgetDecision decision = controller.Current;
        for (int frame = 0;
             frame < HybridReflectionBudgetController
                 .UnderBudgetFramesBeforeIncrease - 1;
             frame++)
        {
            decision = controller.Observe(
                new HybridReflectionBudgetSample(1_000, true, false));
        }
        Assert.That(decision.QualityStep,
            Is.EqualTo(HybridReflectionQualityStep.Reduced));
        Assert.That(decision.Changed, Is.False);

        decision = controller.Observe(
            new HybridReflectionBudgetSample(1_000, true, false));
        Assert.Multiple(() =>
        {
            Assert.That(decision.Changed, Is.True);
            Assert.That(decision.QualityStep,
                Is.EqualTo(HybridReflectionQualityStep.Balanced));
            Assert.That(decision.EwmaGpuMilliseconds,
                Is.EqualTo(1.0).Within(1.0e-9));
        });
    }

    [Test]
    public void HiZPolicy_RetainsPyramidWithoutEnablingOcclusion()
    {
        var settings = new ReflectionSettings
        {
            Mode = ReflectionMode.HybridRayQuery
        };
        var disabledOcclusion = default(HiZVisibilityPolicyDecision) with
        {
            BuildHiZ = false,
            UseHiZForOcclusion = false,
            Reason = "Hi-Z occlusion disabled."
        };

        bool required = HybridReflectionHiZPolicy.RequiresPyramid(
            settings,
            reflectionsAllowed: true);
        HiZVisibilityPolicyDecision retained =
            HybridReflectionHiZPolicy.RetainPyramid(
                disabledOcclusion,
                required,
                sceneChanged: true,
                cameraCut: true);

        Assert.Multiple(() =>
        {
            Assert.That(required, Is.True);
            Assert.That(retained.BuildHiZ, Is.True);
            Assert.That(retained.UseHiZForOcclusion, Is.False);
            Assert.That(retained.SceneChanged, Is.True);
            Assert.That(retained.CameraCut, Is.True);
            Assert.That(retained.PyramidInvalidated, Is.True);
            Assert.That(retained.Reason,
                Does.Contain("active for hybrid reflections"));
            Assert.That(HybridReflectionHiZPolicy.RequiresPyramid(
                settings, reflectionsAllowed: false), Is.False);
        });
    }

    [Test]
    public void QualityPresets_SelectApprovedReflectionTiers()
    {
        var low = new RenderSettings();
        var medium = new RenderSettings();
        var high = new RenderSettings();
        var ddgiHigh = new RenderSettings();
        var ultra = new RenderSettings();
        foreach (RenderSettings settings in new[]
                 {
                     low, medium, high, ddgiHigh, ultra
                 })
        {
            settings.Reflections.Enabled = false;
            settings.Reflections.Mode = ReflectionMode.Disabled;
        }
        low.ApplyQualityPreset(RenderQualityPreset.Low);
        medium.ApplyQualityPreset(RenderQualityPreset.Medium);
        high.ApplyQualityPreset(RenderQualityPreset.High);
        ddgiHigh.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        ultra.ApplyQualityPreset(RenderQualityPreset.Ultra);

        Assert.Multiple(() =>
        {
            Assert.That(low.Reflections.Enabled, Is.True);
            Assert.That(low.Reflections.Mode,
                Is.EqualTo(ReflectionMode.StaticProbes));
            Assert.That(medium.Reflections.Enabled, Is.True);
            Assert.That(medium.Reflections.Mode,
                Is.EqualTo(ReflectionMode.StaticProbesAndSsr));
            Assert.That(medium.Reflections.RayQueryPixelBudgetFraction,
                Is.Zero);
            Assert.That(medium.Transparency.SampleReflections, Is.False);
            Assert.That(medium.Transparency.SceneReflectionRayTaskBudget,
                Is.Zero);
            Assert.That(medium.Transparency.SceneReflectionSsrSampleBudget,
                Is.Zero);
            Assert.That(high.Reflections.Enabled, Is.True);
            Assert.That(high.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(high.Transparency.SampleReflections, Is.True);
            Assert.That(high.Transparency.SceneReflectionRayTaskBudget,
                Is.EqualTo(65_536));
            Assert.That(high.Transparency.SceneReflectionSsrSampleBudget,
                Is.EqualTo(4_194_304));
            Assert.That(ddgiHigh.Reflections.Enabled, Is.True);
            Assert.That(ddgiHigh.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(ddgiHigh.Transparency.SampleReflections, Is.True);
            Assert.That(ddgiHigh.Transparency.SceneReflectionRayTaskBudget,
                Is.EqualTo(65_536));
            Assert.That(ddgiHigh.Transparency.SceneReflectionSsrSampleBudget,
                Is.EqualTo(4_194_304));
            Assert.That(ultra.Reflections.Enabled, Is.True);
            Assert.That(ultra.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(ultra.Transparency.SampleReflections, Is.True);
            Assert.That(ultra.Transparency.SceneReflectionRayTaskBudget,
                Is.EqualTo(131_072));
            Assert.That(ultra.Transparency.SceneReflectionSsrSampleBudget,
                Is.EqualTo(8_388_608));
            Assert.That(ultra.Reflections.RayQueryPixelBudgetFraction,
                Is.GreaterThan(high.Reflections.RayQueryPixelBudgetFraction));
            Assert.That(HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
                    high.Reflections, 1920u, 1080u),
                Is.EqualTo(16_200u));
            Assert.That(HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
                    ddgiHigh.Reflections, 1920u, 1080u),
                Is.EqualTo(16_200u));
            Assert.That(HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
                    ultra.Reflections, 1920u, 1080u),
                Is.EqualTo(32_400u));
        });
    }

    [Test]
    public void SettingsFile_RoundTripsHybridQualityControls()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "njulf-hybrid-reflection-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new RenderSettings();
            settings.Reflections.Mode = ReflectionMode.HybridRayQuery;
            settings.Reflections.SsrFullResolutionRoughness = 0.17f;
            settings.Reflections.SsrHalfResolutionRoughness = 0.48f;
            settings.Reflections.SsrQuarterResolutionRoughness = 0.79f;
            settings.Reflections.SsrMaxSteps = 73;
            settings.Reflections.SsrMaxDistance = 91.0f;
            settings.Reflections.SsrConfidenceThreshold = 0.68f;
            settings.Reflections.RayQueryPixelBudgetFraction = 0.19f;
            settings.Reflections.RayQueryHitLightLimit = 5;
            settings.Reflections.TemporalHistoryLength = 23;
            settings.Reflections.SpatialFilterPassCount = 3;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Reflections.Mode,
                    Is.EqualTo(ReflectionMode.HybridRayQuery));
                Assert.That(loaded.Reflections.SsrFullResolutionRoughness,
                    Is.EqualTo(0.17f));
                Assert.That(loaded.Reflections.SsrHalfResolutionRoughness,
                    Is.EqualTo(0.48f));
                Assert.That(loaded.Reflections.SsrQuarterResolutionRoughness,
                    Is.EqualTo(0.79f));
                Assert.That(loaded.Reflections.SsrMaxSteps, Is.EqualTo(73));
                Assert.That(loaded.Reflections.SsrMaxDistance,
                    Is.EqualTo(91.0f));
                Assert.That(loaded.Reflections.SsrConfidenceThreshold,
                    Is.EqualTo(0.68f));
                Assert.That(loaded.Reflections.RayQueryPixelBudgetFraction,
                    Is.EqualTo(0.19f));
                Assert.That(loaded.Reflections.RayQueryHitLightLimit,
                    Is.EqualTo(5));
                Assert.That(loaded.Reflections.TemporalHistoryLength,
                    Is.EqualTo(23));
                Assert.That(loaded.Reflections.SpatialFilterPassCount,
                    Is.EqualTo(3));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void HistoryRevision_ReportsEveryChangedValidityInput()
    {
        HybridReflectionHistoryRevision previous = Revision();
        HybridReflectionHistoryRevision current = previous with
        {
            Width = previous.Width + 1u,
            Mode = ReflectionMode.StaticProbesAndSsr,
            ReceiverPayloadAbiVersion = previous.ReceiverPayloadAbiVersion + 1u,
            FullResolutionRoughness = 0.3f,
            RaySceneGeneration = previous.RaySceneGeneration + 1u,
            DdgiTopologyGeneration = previous.DdgiTopologyGeneration + 1u,
            MaterialRevision = previous.MaterialRevision + 1u,
            ReflectionProbeRevision = previous.ReflectionProbeRevision + 1UL,
            EnvironmentGeneration = previous.EnvironmentGeneration + 1u,
            CameraCutSerial = previous.CameraCutSerial + 1UL
        };

        ReflectionHistoryResetReason reasons =
            current.ResolveResetReasons(previous, hasHistory: true);
        ReflectionHistorySourceInvalidation sourceInvalidations =
            current.ResolveSourceInvalidations(previous, hasHistory: true);

        Assert.Multiple(() =>
        {
            Assert.That(reasons.HasFlag(
                ReflectionHistoryResetReason.ExtentChanged), Is.True);
            Assert.That(reasons.HasFlag(
                ReflectionHistoryResetReason.ModeChanged), Is.True);
            Assert.That(reasons.HasFlag(
                ReflectionHistoryResetReason.ReceiverPayloadAbiChanged), Is.True);
            Assert.That(reasons.HasFlag(
                ReflectionHistoryResetReason.CameraCut), Is.True);
            Assert.That(sourceInvalidations.HasFlag(
                ReflectionHistorySourceInvalidation.RayScene), Is.True);
            Assert.That(sourceInvalidations.HasFlag(
                ReflectionHistorySourceInvalidation.Ddgi), Is.True);
            Assert.That(sourceInvalidations.HasFlag(
                ReflectionHistorySourceInvalidation.LocalProbe), Is.True);
            Assert.That(sourceInvalidations.HasFlag(
                ReflectionHistorySourceInvalidation.Environment), Is.True);
            Assert.That(sourceInvalidations.HasFlag(
                ReflectionHistorySourceInvalidation.Planar), Is.False);
            Assert.That(current.ResolveResetReasons(previous, hasHistory: false),
                Is.EqualTo(ReflectionHistoryResetReason.InitialFrame));
        });
    }

    [Test]
    public void GpuPushConstants_StayWithinTheFrozenVulkanRange()
    {
        int[] sizes =
        [
            Marshal.SizeOf<GPUHybridReflectionSsrPushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionRayPushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionResolvePushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionTemporalPushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionSpatialPushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionCompositePushConstants>(),
            Marshal.SizeOf<GPUHybridReflectionDdgiPushConstants>()
        ];

        Assert.Multiple(() =>
        {
            Assert.That(sizes[0],
                Is.EqualTo(HybridReflectionGpuContract.MaximumPushConstantBytes));
            Assert.That(sizes[2], Is.EqualTo(120));
            Assert.That(sizes, Has.All.LessThanOrEqualTo(
                HybridReflectionGpuContract.MaximumPushConstantBytes));
            Assert.That(sizes[6], Is.EqualTo(120));
            Assert.That(HybridReflectionGpuContract.CounterWords, Is.EqualTo(16u));
            Assert.That(HybridReflectionGpuContract.HistoryMetadataWords,
                Is.EqualTo(2u));
        });
    }

    [Test]
    public void ProductionGraph_OrdersTheCompleteChainBeforeTransparency()
    {
        string[] expected =
        [
            "SkyboxPass",
            "AutomaticPlanarReflectionPass",
            "HybridReflectionDdgiBasePass",
            "HybridReflectionSsrPass",
            "HybridReflectionRayQueryPass",
            "HybridReflectionResolvePass",
            "HybridReflectionTemporalPass",
            "HybridReflectionSpatialPass",
            "HybridReflectionCompositePass",
            "OpaqueSceneColorSnapshotPass",
            "TransparentForwardPass"
        ];
        var order = ProductionRenderPipelineDeclaration.Instance.PassOrder
            .ToList();
        int start = order.IndexOf(expected[0]);

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            for (int index = 0; index < expected.Length; index++)
                Assert.That(order[start + index], Is.EqualTo(expected[index]));
        });
    }

    [Test]
    public void ProductionGraph_DeclaresReceiverRayAndIndirectDependencies()
    {
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        RenderGraphResourceUsage receiverWrite = declarations["ForwardPlusPass"]
            .Usages.Single(usage => usage.Resource ==
                RenderGraphResourceId.HybridReflectionReceiverPayload);
        RenderGraphResourceUsage indirectRead =
            declarations["HybridReflectionRayQueryPass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionIndirectArguments);
        RenderGraphResourceUsage spatialHistory =
            declarations["HybridReflectionSpatialPass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionHistory &&
                    usage.Access == RenderGraphResourceAccess.Read &&
                    usage.HistoryBinding ==
                        RenderGraphHistoryBindingSelection.Current);
        RenderGraphResourceUsage spatialRaw =
            declarations["HybridReflectionSpatialPass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionRawRadiance);
        RenderGraphResourceUsage snapshotSceneColor =
            declarations["OpaqueSceneColorSnapshotPass"].Usages.Single(
                usage => usage.Resource == RenderGraphResourceId.SceneColor);
        RenderGraphResourceUsage snapshotWrite =
            declarations["OpaqueSceneColorSnapshotPass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionHistory &&
                    usage.HistoryBinding ==
                        RenderGraphHistoryBindingSelection.Previous);

        Assert.Multiple(() =>
        {
            Assert.That(receiverWrite.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(receiverWrite.StageMask &
                PipelineStageFlags2.ColorAttachmentOutputBit, Is.Not.Zero);
            Assert.That(declarations["HybridReflectionRayQueryPass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.TlasStorage),
                Is.True);
            Assert.That(indirectRead.StageMask & PipelineStageFlags2.DrawIndirectBit,
                Is.Not.Zero);
            Assert.That(indirectRead.AccessMask & AccessFlags2.IndirectCommandReadBit,
                Is.Not.Zero);
            Assert.That(spatialHistory.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(spatialHistory.HistoryBinding,
                Is.EqualTo(RenderGraphHistoryBindingSelection.Current));
            Assert.That(spatialRaw.Access,
                Is.EqualTo(RenderGraphResourceAccess.ReadWrite));
            Assert.That(declarations["HybridReflectionResolvePass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.ReflectionProbeCubemaps), Is.True);
            Assert.That(declarations["HybridReflectionDdgiBasePass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionDdgiCohorts &&
                    usage.Access == RenderGraphResourceAccess.Write), Is.True);
            Assert.That(declarations["HybridReflectionCompositePass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.SceneColor &&
                    usage.Access == RenderGraphResourceAccess.ReadWrite), Is.True);
            Assert.That(declarations["HybridReflectionCompositePass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionReceiverPayload &&
                    usage.Access == RenderGraphResourceAccess.Read), Is.True);
            Assert.That(snapshotSceneColor.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(snapshotWrite.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(declarations["TransparentForwardPass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionHistory &&
                    usage.Access == RenderGraphResourceAccess.Read &&
                    usage.HistoryBinding ==
                        RenderGraphHistoryBindingSelection.Previous &&
                    (usage.StageMask & PipelineStageFlags2.FragmentShaderBit) != 0),
                Is.True);
            Assert.That(declarations["WeightedTransparentPass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.HybridReflectionHistory &&
                    usage.Access == RenderGraphResourceAccess.Read &&
                    usage.HistoryBinding ==
                        RenderGraphHistoryBindingSelection.Previous &&
                    (usage.StageMask & PipelineStageFlags2.FragmentShaderBit) != 0),
                Is.True);
        });
    }

    [Test]
    public void ReflectionPasses_AreGraphicsQueueComputeByDesign()
    {
        string[] passes =
        [
            "HybridReflectionSsrPass",
            "HybridReflectionRayQueryPass",
            "HybridReflectionResolvePass",
            "HybridReflectionTemporalPass",
            "HybridReflectionSpatialPass",
            "HybridReflectionCompositePass",
            "OpaqueSceneColorSnapshotPass"
        ];

        Assert.That(passes.Select(AsyncComputePassCatalog.GetClassification),
            Has.All.EqualTo(
                AsyncComputePassClassification.GraphicsQueueComputeByDesign));
    }

    [Test]
    public void ReflectionPasses_RespectFeatureIsolation()
    {
        string[] passes =
        [
            "HybridReflectionSsrPass",
            "HybridReflectionRayQueryPass",
            "HybridReflectionResolvePass",
            "HybridReflectionTemporalPass",
            "HybridReflectionSpatialPass",
            "HybridReflectionCompositePass",
            "OpaqueSceneColorSnapshotPass"
        ];

        Assert.Multiple(() =>
        {
            Assert.That(passes.All(pass =>
                RenderFeatureIsolationPolicy.ShouldExecutePass(
                    RenderFeatureIsolationMode.Reflections, pass)), Is.True);
            Assert.That(passes.Any(pass =>
                RenderFeatureIsolationPolicy.ShouldExecutePass(
                    RenderFeatureIsolationMode.Geometry, pass)), Is.False);
            Assert.That(passes.Any(pass =>
                RenderFeatureIsolationPolicy.ShouldExecutePass(
                    RenderFeatureIsolationMode.PostProcessing, pass)), Is.False);
        });
    }

    [Test]
    public void RaySceneRequirement_UsesSharedAlphaMaskPolicyOnly()
    {
        var settings = new ReflectionSettings
        {
            Mode = ReflectionMode.HybridRayQuery,
            SsrMaxDistance = 123.0f
        };

        RaySceneRequirement requirement =
            RaySceneRequirement.ForReflections(settings);

        Assert.Multiple(() =>
        {
            Assert.That(requirement.Consumers,
                Is.EqualTo(RaySceneConsumer.Reflection));
            Assert.That(requirement.RequiredCategories,
                Is.EqualTo(RaySceneGeometryCategory.DirectionalShadowDefault));
            Assert.That(requirement.RequiredCategories.HasFlag(
                RaySceneGeometryCategory.AlphaTested), Is.True);
            Assert.That(requirement.RequiredCategories.HasFlag(
                RaySceneGeometryCategory.FoliageAlphaTested), Is.True);
            Assert.That(requirement.RequiredCategories.HasFlag(
                RaySceneGeometryCategory.AlphaBlend), Is.False);
            Assert.That(requirement.RequiredCategories.HasFlag(
                RaySceneGeometryCategory.ThinTransmission), Is.False);
            Assert.That(requirement.MaximumRayDistance, Is.EqualTo(123.0f));
            Assert.That(requirement.RequiresCurrentPose, Is.True);
        });
    }

    [Test]
    public void SharedRayScene_PreservesDdgiTransparencyWithReflections()
    {
        string renderer = ReadRepoText("Njulf.Rendering",
            "VulkanRenderer.cs");
        string normalized = renderer.Replace("\r\n", "\n",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(normalized, Does.Contain(
                "ddgiRaySceneEnabled\n" +
                "                        ? gi.EffectiveDdgiTransparentGeometryMode"));
            Assert.That(renderer, Does.Not.Contain(
                "ddgiRaySceneEnabled && !reflectionRaySceneRequested"));
        });
    }

    [Test]
    public void VulkanRuntime_ResetHeadersUseSingleOrderedTransferWrites()
    {
        string runtime = ReadRepoText("Njulf.Rendering", "Pipeline",
            "HybridReflectionVulkanRuntime.cs");
        int resetStart = runtime.IndexOf(
            "private void ResetTaskAndCounterBuffers",
            StringComparison.Ordinal);
        int resetEnd = runtime.IndexOf(
            "private void BindPipelineAndDescriptors",
            resetStart,
            StringComparison.Ordinal);

        Assert.That(resetStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(resetEnd, Is.GreaterThan(resetStart));
        string reset = runtime[resetStart..resetEnd];

        Assert.Multiple(() =>
        {
            Assert.That(reset.Split("CmdUpdateBuffer",
                    StringSplitOptions.None).Length - 1,
                Is.EqualTo(3),
                "Task, indirect, and classification-tile headers must each be stamped atomically.");
            Assert.That(reset.Split("CmdFillBuffer",
                    StringSplitOptions.None).Length - 1,
                Is.EqualTo(1),
                "Only the homogeneous counter range is fill-cleared.");
            Assert.That(reset, Does.Not.Contain(
                "CmdFillBuffer(commandBuffer, task"));
            Assert.That(reset, Does.Not.Contain(
                "CmdFillBuffer(commandBuffer, indirect"));
            Assert.That(reset, Does.Contain("PipelineStageFlags2.CopyBit"));
            Assert.That(reset, Does.Contain("PipelineStageFlags2.ClearBit"));
            Assert.That(reset, Does.Contain("TaskHeaderBytes"));
            Assert.That(reset, Does.Contain("IndirectBytes"));
            Assert.That(reset, Does.Contain("ExecuteBufferBarriers"));
        });
    }

    [Test]
    public void HybridReceiverShader_SpecializesOnlyRejectedForwardDebugViews()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag")
            .ReplaceLineEndings("\n");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline",
            "ForwardPlusPass.cs").ReplaceLineEndings("\n");
        int admissionStart = pass.IndexOf(
            "private bool TryGetHybridReflectionReceiverBinding(",
            StringComparison.Ordinal);
        int admissionEnd = pass.IndexOf(
            "internal static bool ShouldApplyDdgi(",
            admissionStart,
            StringComparison.Ordinal);
        Assert.That(admissionStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(admissionEnd, Is.GreaterThan(admissionStart));
        string admission = pass[admissionStart..admissionEnd];

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Contain(
                "NJULF_PERFORMANCE_STATIC_SHADER_SPECIALIZATION"));
            Assert.That(forward, Does.Contain(
                "bool NjulfHybridDebugViewsStaticNone()"));
            Assert.That(forward, Does.Contain(
                "if (NjulfHybridDebugViewsStaticNone())\n" +
                "        return DEBUG_VIEW_NONE;"));
            Assert.That(forward, Does.Contain(
                "#define FORWARD_INCOMPATIBLE_DEBUG_VIEWS_STATIC_NONE 1"));
            Assert.That(forward, Does.Contain(
                "#if !FORWARD_INCOMPATIBLE_DEBUG_VIEWS_STATIC_NONE\n" +
                "    if (environment.DebugView == ENVIRONMENT_DEBUG_AMBIENT_OCCLUSION)"));
            Assert.That(admission, Does.Contain(
                "sceneData.DebugViewMode != 0u"));
            Assert.That(admission, Does.Contain(
                "sceneData.AmbientOcclusionDebugView != AmbientOcclusionDebugView.None"));
            Assert.That(admission, Does.Contain(
                "sceneData.TransparencyDebugView != TransparencyDebugView.None"));
            Assert.That(admission, Does.Contain(
                "sceneData.AnimationDebugView != AnimationDebugView.None"));
            Assert.That(admission, Does.Contain(
                "_settings.GlobalIllumination.DebugView !="));
            Assert.That(admission, Does.Contain(
                "_settings.Environment.DebugView != EnvironmentDebugView.None"));
            Assert.That(admission, Does.Contain(
                "hybrid-reflection-incompatible-debug-view-active"));
        });
    }

    [Test]
    public void HybridReceiverCacheSplit_StaticBentNormalModeMatchesAdmission()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag")
            .ReplaceLineEndings("\n");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline",
            "ForwardPlusPass.cs").ReplaceLineEndings("\n");
        string shaderProject = ReadRepoText(
            "Njulf.Shaders", "Njulf.Shaders.csproj");
        string contract = ReadRepoText(
            "Njulf.Rendering", "Pipeline",
            "ForwardHybridReflectionReceiverContract.cs");
        int admissionStart = pass.IndexOf(
            "private bool ShouldUseHybridReflectionReceiverCacheSplit(",
            StringComparison.Ordinal);
        int admissionEnd = pass.IndexOf(
            "private bool ShouldUseProductionForwardGiDisabledPipeline(",
            admissionStart,
            StringComparison.Ordinal);
        Assert.That(admissionStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(admissionEnd, Is.GreaterThan(admissionStart));
        string admission = pass[admissionStart..admissionEnd];

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Contain(
                "bool NjulfReceiverCacheSplitLane()"));
            Assert.That(forward, Does.Contain(
                "if (NjulfReceiverCacheSplitLane())\n" +
                "        return 0u;"));
            Assert.That(forward, Does.Contain(
                "#if !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY\n" +
                "    bool globalIlluminationEnabled"));
            Assert.That(shaderProject, Does.Contain(
                "cache_exact_fallback_hybrid_reflection.frag"));
            Assert.That(shaderProject, Does.Contain(
                "cache_combined_hybrid_reflection.frag"));
            Assert.That(contract, Does.Contain(
                "receiverCacheExactFallbackOnly"));
            Assert.That(contract, Does.Contain(
                "receiverCacheCombined"));
            Assert.That(admission, Does.Contain(
                "sceneData.AmbientOcclusionBentNormalMode ==\n" +
                "                       AmbientOcclusionBentNormalMode.Off"));
            Assert.That(admission, Does.Not.Contain(
                "AmbientOcclusionBentNormalMode.EnvironmentAndDdgi"));
        });
    }

    [Test]
    public void ShaderSources_ContainStrictFallbackShadingAndDebugContracts()
    {
        string ssr = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_ssr.comp");
        string ray = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_ray_query.comp");
        string ddgiBase = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_ddgi_base.comp");
        string resolve = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_resolve.comp");
        string temporal = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_temporal.comp");
        string spatial = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_spatial.comp");
        string composite = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_composite.comp");
        string opaqueSnapshot = ReadRepoText("Njulf.Shaders",
            "opaque_scene_color_snapshot.comp");
        string compute = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_compute.glsl");
        string payload = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_payload.glsl");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string runtime = ReadRepoText("Njulf.Rendering", "Pipeline",
            "HybridReflectionVulkanRuntime.cs");
        string normalizedForward = forward.Replace("\r\n", "\n",
            StringComparison.Ordinal);
        string resolveMain = resolve[resolve.IndexOf("void main()",
            StringComparison.Ordinal)..];
        int probeFallback = resolve.IndexOf("HybridSampleLocalProbe",
            StringComparison.Ordinal);
        int environmentFallback = resolve.IndexOf(
            "SampleEnvironmentPrefilteredRadiance",
            StringComparison.Ordinal);
        int transparentSsrStart = forward.IndexOf(
            "bool ForwardTraceTransparentSsr(",
            StringComparison.Ordinal);
        int transparentRayStart = forward.IndexOf(
            "bool ForwardTraceTransparentRayReflection(",
            StringComparison.Ordinal);
        string transparentSsr = forward[transparentSsrStart..transparentRayStart];
        int transparentSsrReservation = transparentSsr.IndexOf(
            "ForwardTryReserveTransparentSsr(header, reservedSamples)",
            StringComparison.Ordinal);
        int firstTransparentHiZAccess = transparentSsr.IndexOf(
            "textureQueryLevels(",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(ssr, Does.Contain("HybridAppendRayTask"));
            Assert.That(ssr, Does.Contain("RayAdmissionThreshold"));
            Assert.That(compute, Does.Contain(
                "HYBRID_REFLECTION_MINIMUM_RAY_IMPORTANCE = 0.12"));
            Assert.That(ssr, Does.Contain(
                "HybridReflectionPayloadF0(payload)"));
            Assert.That(compute, Does.Contain(
                "rayImportance < HYBRID_REFLECTION_MINIMUM_RAY_IMPORTANCE"));
            Assert.That(compute, Does.Contain(
                "HybridAccumulateScreenCounter"));
            Assert.That(ssr, Does.Contain(
                "HybridResolveAdaptiveReflectionTier"));
            Assert.That(ssr, Does.Contain("if (lane != phase)"));
            Assert.That(ssr, Does.Contain(
                "HYBRID_REFLECTION_REASON_RAY_BUDGET"));
            Assert.That(ssr, Does.Contain(
                "trace.Confidence >= pc.ConfidenceThreshold"));
            Assert.That(ssr, Does.Not.Contain(
                "invocation.y * 0x9e3779b9u) ^\n        pc.TemporalSampleIndex"));
            Assert.That(ssr, Does.Contain("HYBRID_REFLECTION_REASON_DISOCCLUDED"));
            Assert.That(ssr, Does.Contain("result.HitUv = refinedUv"));
            Assert.That(ssr, Does.Not.Contain(
                "float lod = roughness * 4.0"));
            Assert.That(ray, Does.Contain(
                "RaySceneCandidateBlocksDirectionalShadow"));
            Assert.That(ray, Does.Contain("HybridSampleEmissive"));
            Assert.That(ray, Does.Contain("HybridEvaluatePbrLight"));
            Assert.That(ray, Does.Contain("SampleSimpleDdgiIrradiance"));
            Assert.That(ddgiBase, Does.Contain(
                "SetSimpleDdgiDirectionalRadianceQuery"));
            Assert.That(resolve, Does.Contain(
                "HYBRID_REFLECTION_SOURCE_DDGI"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridDdgiSameSurface"));
            Assert.That(ddgiBase, Does.Contain(
                "params, worldPosition, traceNormal, viewDirection"));
            Assert.That(ddgiBase, Does.Contain(
                "vec3 referenceNormal = HybridReflectionTraceNormal"));
            Assert.That(ddgiBase, Does.Contain(
                "abs(candidateDistance - receiverDistance) <= tolerance"));
            Assert.That(ddgiBase, Does.Not.Contain(
                "distance(referenceWorldPosition, candidateWorldPosition)"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridPackDdgiCohort"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridDdgiCohorts"));
            Assert.That(ddgiBase, Does.Contain(
                "ReconstructionPass"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridFindDdgiCohort"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridPrepareDdgiSharedCohorts"));
            Assert.That(ddgiBase, Does.Contain(
                "memoryBarrierShared();"));
            Assert.That(ddgiBase, Does.Contain(
                "vec2 coarsePosition"));
            Assert.That(ddgiBase, Does.Contain(
                "weightedRadiance += cohortRadiance * weight"));
            Assert.That(ddgiBase, Does.Contain(
                "weightedConfidence += cohortConfidence * weight"));
            Assert.That(ddgiBase, Does.Contain(
                "uint centreLow = (scale - 1u) / 2u"));
            Assert.That(ddgiBase, Does.Not.Contain(
                "HybridTryDdgiTile("));
            Assert.That(ddgiBase, Does.Contain(
                "if (!valid)"));
            Assert.That(ddgiBase, Does.Contain(
                "valid = HybridEvaluateDdgiReflection("));
            Assert.That(ddgiBase, Does.Contain(
                "never a sampling hole"));
            Assert.That(ddgiBase, Does.Contain(
                "HybridReflectionRequiresSharpDetail(payload)"));
            Assert.That(ddgiBase, Does.Contain(
                "HYBRID_REFLECTION_REASON_DISOCCLUDED"));
            Assert.That(ddgiBase, Does.Contain(
                "ReceiverScale"));
            Assert.That(ddgiBase, Does.Contain(
                "SetSimpleDdgiDirectionalRadianceQueryEligibilityWeight(1.0)"));
            Assert.That(probeFallback, Is.GreaterThanOrEqualTo(0));
            Assert.That(environmentFallback, Is.GreaterThan(probeFallback));
            Assert.That(resolveMain, Does.Contain(
                "if (!HybridMetadataValid(metadata.x))"));
            Assert.That(resolveMain, Does.Not.Contain(
                "else if (source == HYBRID_REFLECTION_SOURCE_SSR)"));
            Assert.That(resolveMain, Does.Not.Contain("ssrTrust"));
            Assert.That(resolveMain, Does.Not.Contain(
                "if (resolutionSkip)"));
            Assert.That(resolveMain, Does.Contain(
                "source == HYBRID_REFLECTION_SOURCE_ENVIRONMENT"));
            Assert.That(resolveMain, Does.Contain(
                "? max(environment.SpecularIntensity, 0.0)"));
            Assert.That(resolveMain, Does.Contain(
                "incidentRadiance * incidentRadianceScale"));
            Assert.That(resolveMain, Does.Contain(
                "HybridLimitReflectionRadiance(contribution"));
            Assert.That(compute, Does.Contain(
                "vec3 HybridLimitBroadReflectionRadiance("));
            Assert.That(resolveMain, Does.Contain(
                "geometricSource && !sharpImportant && analyticReferenceAvailable"));
            Assert.That(resolveMain, Does.Contain(
                "HybridLimitBroadReflectionRadiance("));
            Assert.That(resolveMain, Does.Contain(
                "source = HYBRID_REFLECTION_SOURCE_DDGI"));
            Assert.That(resolveMain, Does.Not.Contain(
                "roughDielectricAnalyticWeight"));
            Assert.That(resolveMain, Does.Contain(
                "Do not replace a valid broad DDGI lobe with the global sky"));
            Assert.That(resolveMain, Does.Contain(
                "dot(reflectionNormal, viewDirection)"));
            Assert.That(temporal, Does.Contain("HybridMotionVectors"));
            Assert.That(temporal, Does.Contain(
                "vec3 currentNormal = HybridReflectionTraceNormal(payload)"));
            Assert.That(temporal, Does.Not.Contain(
                "currentNormal =\n        HybridReflectionPayloadShadingNormal"));
            Assert.That(temporal, Does.Contain(
                "HYBRID_REFLECTION_REASON_RESOLUTION_SKIP"));
            Assert.That(temporal, Does.Contain(
                "Gather four bilinear history taps"));
            Assert.That(temporal, Does.Contain("reuseSparseHistory"));
            Assert.That(temporal, Does.Contain("previousGeometricSource"));
            Assert.That(temporal, Does.Contain(
                "previousSource == HYBRID_REFLECTION_SOURCE_SSR"));
            Assert.That(temporal, Does.Contain(
                "previousSource == HYBRID_REFLECTION_SOURCE_RAY_QUERY"));
            Assert.That(temporal, Does.Contain(
                "HybridLimitReflectionRadiance"));
            Assert.That(temporal, Does.Contain(
                "float motionPixels = length(motion * dimensions)"));
            Assert.That(temporal, Does.Contain(
                "float sparseMotionWeight = 1.0 - smoothstep("));
            Assert.That(temporal, Does.Contain(
                "previousSparseAge < sparseHistoryAgeLimit"));
            Assert.That(temporal, Does.Contain(
                "mix(current.rgb, clippedHistory, sparseHistoryWeight)"));
            Assert.That(temporal, Does.Contain(
                "reuseSparseHistory && sparseHistoryWeight >= 0.5"));
            Assert.That(temporal, Does.Contain(
                "previous.a * 0.97"));
            Assert.That(temporal, Does.Not.Contain(
                "historyWeight = min(pc.MaximumHistoryWeight, 0.85)"));
            Assert.That(temporal, Does.Contain(
                "neighborhoodMaximum * 1.5 + vec3(0.05)"));
            Assert.That(temporal, Does.Contain(
                "previousVariance = max(previousVariance, 0.0)"));
            Assert.That(spatial, Does.Contain(
                "imageLoad(HybridHistoryCurrent, pixel)"));
            Assert.That(spatial, Does.Contain(
                "imageStore(HybridRawRadiance, pixel, value)"));
            Assert.That(spatial, Does.Not.Contain(
                "imageStore(HybridHistoryCurrent, pixel, value)"));
            Assert.That(composite, Does.Contain(
                "imageLoad(HybridRawRadiance, pixel)"));
            Assert.That(composite, Does.Contain("pc.DebugView == 13u"));
            Assert.That(composite, Does.Contain("pc.DebugView == 14u"));
            Assert.That(composite, Does.Contain("pc.DebugView == 15u"));
            Assert.That(composite, Does.Contain("pc.DebugView == 16u"));
            Assert.That(composite, Does.Contain("pc.DebugView == 11u"));
            Assert.That(resolveMain, Does.Contain(
                "pc.ReflectionDebugView == 11u"));
            Assert.That(composite, Does.Contain(
                "HybridResolveAdaptiveReflectionTier"));
            Assert.That(opaqueSnapshot, Does.Contain(
                "imageStore(OpaqueSnapshot"));
            Assert.That(opaqueSnapshot, Does.Contain(
                "imageLoad(OpaqueSceneColor"));
            Assert.That(forward, Does.Contain(
                "OPAQUE_SCENE_COLOR_SNAPSHOT_TEXTURE_INDEX"));
            Assert.That(forward, Does.Contain(
                "EvaluateTransparentReflectionSpecular("));
            Assert.That(forward, Does.Contain(
                "ForwardTraceTransparentSsr("));
            Assert.That(forward, Does.Contain(
                "ForwardTryReserveTransparentSsr("));
            Assert.That(forward, Does.Contain(
                "reservedSamples = max(header.SsrMaximumSteps, 8u) * 2u"));
            Assert.That(forward, Does.Contain(
                "header.SceneReflectionSsrSampleBudget /"));
            Assert.That(forward, Does.Contain(
                "TRANSPARENT_REFLECTION_SSR_ACTUAL_SAMPLE_COUNTER"));
            Assert.That(forward, Does.Contain(
                "TRANSPARENT_REFLECTION_SSR_BUDGET_REJECT_COUNTER"));
            Assert.That(transparentSsrReservation, Is.GreaterThanOrEqualTo(0));
            Assert.That(firstTransparentHiZAccess,
                Is.GreaterThan(transparentSsrReservation),
                "Transparent SSR must reject over-budget work before any Hi-Z access.");
            Assert.That(forward, Does.Contain(
                "ForwardTraceTransparentRayReflection("));
            Assert.That(forward, Does.Contain(
                "dot(reflectionDirection, geometricNormal) <= 0.001"));
            Assert.That(forward, Does.Contain(
                "NjulfOffsetRayOrigin(worldPosition, geometricNormal)"));
            Assert.That(normalizedForward, Does.Contain(
                "fragWorldPosition,\n        geometricNormal,\n" +
                "        reflectionDirection"));
            Assert.That(normalizedForward, Does.Not.Contain(
                "fragWorldPosition,\n        normal,\n" +
                "        reflectionDirection"));
            Assert.That(ray, Does.Contain(
                "NjulfOffsetRayOrigin(worldPosition, geometricNormal)"));
            Assert.That(common, Does.Contain(
                "vec3 NjulfOffsetRayOrigin("));
            Assert.That(common, Does.Contain("floatBitsToInt(position)"));
            Assert.That(forward, Does.Contain(
                "ForwardMaterialSamplesSceneReflections("));
            Assert.That(common, Does.Contain(
                "UnpackForwardMaterialBlendMode(controls1.x)"));
            Assert.That(forward, Does.Contain(
                "TRANSPARENT_REFLECTION_BUDGET_REJECT_COUNTER"));
            Assert.That(forward, Does.Contain(
                "fallbackSpecular * (1.0 - geometricWeight)"));
            Assert.That(forward, Does.Contain(
                "Environment.SpecularIntensity owns only the global IBL share"));
            Assert.That(forward, Does.Contain(
                "globalLod) * header.GlobalFallbackIntensity *"));
            Assert.That(forward, Does.Not.Contain(
                "geometric.Radiance * header.Intensity *\n" +
                "        (fresnel * brdf.x + brdf.y) * environment.SpecularIntensity"));
            Assert.That(forward, Does.Not.Contain(
                "transparentReflectionEnergyBoost"));
            Assert.That(normalizedForward, Does.Contain(
                "#if !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT\n" +
                "    if (reflectionDebugActive)"));
            Assert.That(forward, Does.Contain(
                "uvec3(fragObjectIndex, fragMaterialIndex, fragMeshletIndex)"));
            Assert.That(forward, Does.Contain(
                "pow(indirectAo, 1.0 + roughness)"));
            Assert.That(forward, Does.Contain(
                "float roughnessFootprintVariance"));
            Assert.That(forward, Does.Contain(
                "roughnessDx = dFdx(physicalRoughness)"));
            Assert.That(forward, Does.Contain(
                "roughnessDy = dFdy(physicalRoughness)"));
            Assert.That(forward, Does.Contain(
                "alphaSquared + normalVariance + roughnessFootprintVariance"));
            Assert.That(forward, Does.Contain(
                "SampleMaterialTextureFootprint("));
            Assert.That(forward, Does.Contain(
                "reflectionFootprintRoughness = max("));
            Assert.That(forward, Does.Contain(
                "representableFrequencyShare = mix(0.55, 0.85, roughness)"));
            Assert.That(forward, Does.Contain(
                "transmissionFactor >= 0.05"));
            Assert.That(forward, Does.Contain(
                "anisotropyStrength >= 0.35"));
            Assert.That(resolve, Does.Contain(
                "vec3 fresnel = HybridFresnelSchlickRoughness"));
            Assert.That(payload, Does.Contain(
                "NJULF_HYBRID_REFLECTION_PAYLOAD_ABI_VERSION = 5u"));
            Assert.That(payload, Does.Contain(
                "NJULF_HYBRID_REFLECTION_SPECULAR_OCCLUSION_MASK = 0x3fu"));
            Assert.That(ReflectionSettings.ReceiverPayloadAbiVersion,
                Is.EqualTo(5u));
            Assert.That(runtime, Does.Contain(
                "SynchronizePreviousHybridFrame(commandBuffer)"));
            Assert.That(runtime, Does.Contain(
                "Hybrid Reflection Counter Readback"));
            Assert.That(runtime, Does.Contain(
                "RecordCounterReadback(commandBuffer, bank)"));
            Assert.That(runtime, Does.Contain(
                "private void SynchronizePreviousHybridFrame"));
            Assert.That(runtime, Does.Contain(
                "SrcAccessMask = AccessFlags2.ShaderStorageReadBit |\n" +
                "                AccessFlags2.ShaderStorageWriteBit |\n" +
                "                AccessFlags2.ShaderSampledReadBit"));
            Assert.That(runtime, Does.Contain(
                "PipelineStageFlags2.TransferBit"));
        });
    }

    [Test]
    public void DdgiReflectionPreview_StabilizesOnlyTheRoughL1Lobe()
    {
        string ddgiBase = ReadRepoText("Njulf.Shaders",
            "hybrid_reflection_ddgi_base.comp");
        string shared = ReadRepoText("Njulf.Shaders",
            "ddgi_simple_shared.glsl");
        string radianceSh = ReadRepoText("Njulf.Shaders",
            "ddgi_radiance_sh.glsl");
        int ordinaryEvaluatorStart = radianceSh.IndexOf(
            "bool EvaluateSimpleDdgiRadianceShRecord(",
            StringComparison.Ordinal);
        int previewDecoderStart = radianceSh.IndexOf(
            "bool EvaluateSimpleDdgiRadianceShL1PreviewRecordWithBasis(",
            StringComparison.Ordinal);
        Assert.That(ordinaryEvaluatorStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(previewDecoderStart, Is.GreaterThan(ordinaryEvaluatorStart));
        string ordinaryEvaluator = radianceSh[
            ordinaryEvaluatorStart..previewDecoderStart];
        string previewDecoder = radianceSh[previewDecoderStart..];

        Assert.Multiple(() =>
        {
            Assert.That(ddgiBase, Does.Contain(
                "float broadLobeWeight = smoothstep(0.45, 0.85, roughness)"));
            Assert.That(ddgiBase, Does.Contain(
                "mix(1.0, 0.25, broadLobeWeight)"));
            Assert.That(shared, Does.Contain(
                "float SimpleDdgiDirectionalRadianceQueryL1Scale = 1.0"));
            Assert.That(shared, Does.Contain(
                "SetSimpleDdgiDirectionalRadianceQueryL1Scale(float scale)"));
            Assert.That(previewDecoder, Does.Contain(
                "bandScales.y *= clamp(l1Scale, 0.0, 1.0)"));
            Assert.That(previewDecoder, Does.Not.Contain(
                "bandScales.x *= clamp(l1Scale"),
                "The L0 mean carries bounce energy and must remain unchanged.");
            Assert.That(ordinaryEvaluator, Does.Not.Contain("l1Scale"),
                "The ordinary directional SH evaluator must remain unchanged.");
            Assert.That(ddgiBase.Split(
                    "SetSimpleDdgiDirectionalRadianceQueryL1Scale(",
                    StringSplitOptions.None).Length - 1,
                Is.EqualTo(1),
                "Only the hybrid reflection preview may opt into damping.");
        });
    }

    [Test]
    public void ExactReceiverPublication_DefersCacheSpecializationsWithoutRenderTimeCreation()
    {
        string runtime = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "HybridReflectionVulkanRuntime.cs");
        string mesh = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "MeshPipeline.cs");
        string renderer = ReadRepoText(
            "Njulf.Rendering",
            "VulkanRenderer.cs");
        string forward = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");

        int completedRuntimeDeferral = runtime.IndexOf(
            "if (_initializationState == 2 &&",
            StringComparison.Ordinal);
        int publicationWithdrawn = runtime.IndexOf(
            "ScreenPipelinesAvailable = false;",
            completedRuntimeDeferral,
            StringComparison.Ordinal);
        int backgroundPublication = runtime.IndexOf(
            "PrepareDeferredPublication(",
            publicationWithdrawn,
            StringComparison.Ordinal);
        int publicationRestored = runtime.IndexOf(
            "ScreenPipelinesAvailable = true;",
            backgroundPublication,
            StringComparison.Ordinal);
        int exactPreparationStart = mesh.IndexOf(
            "public bool TryPrepareHybridReflectionExactPipelines(",
            StringComparison.Ordinal);
        int performancePreparationStart = mesh.IndexOf(
            "public bool TryPrepareHybridReflectionPerformancePipelines(",
            exactPreparationStart,
            StringComparison.Ordinal);
        int performancePreparationEnd = mesh.IndexOf(
            "private bool TryPrepareHybridReflectionPipelineRange(",
            performancePreparationStart,
            StringComparison.Ordinal);
        string exactPreparation =
            mesh[exactPreparationStart..performancePreparationStart];
        string performancePreparation =
            mesh[performancePreparationStart..performancePreparationEnd];
        int fullQualityStart = renderer.IndexOf(
            "private void PrepareHybridReflectionsForFullQuality()",
            StringComparison.Ordinal);
        int fullQualityEnd = renderer.IndexOf(
            "private bool BeginProgressiveFrame()",
            fullQualityStart,
            StringComparison.Ordinal);
        string fullQuality = renderer[fullQualityStart..fullQualityEnd];
        int postFirstPresent = renderer.IndexOf(
            "Pipeline.Prepare.PostFirstPresentHybridReflectionSpecializations",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(completedRuntimeDeferral,
                Is.GreaterThanOrEqualTo(0));
            Assert.That(publicationWithdrawn,
                Is.GreaterThan(completedRuntimeDeferral));
            Assert.That(backgroundPublication,
                Is.GreaterThan(publicationWithdrawn));
            Assert.That(publicationRestored,
                Is.GreaterThan(backgroundPublication),
                "Screen pipelines must remain unavailable until background preparation returns successfully.");
            Assert.That(exactPreparation, Does.Contain(
                "HybridReflectionExactLane"));
            Assert.That(exactPreparation, Does.Not.Contain(
                "HybridReflectionCacheCombinedPipelineLane"));
            Assert.That(performancePreparation, Does.Contain(
                "ResolveHybridReflectionPerformancePipelineRange("));
            Assert.That(performancePreparation, Does.Contain(
                "range.FirstReceiverLane"));
            Assert.That(performancePreparation, Does.Contain(
                "range.ReceiverLaneLimit"));
            Assert.That(mesh, Does.Contain(
                "? (HybridReflectionCacheAcceptedPipelineLane,"));
            Assert.That(mesh, Does.Contain(
                ": (HybridReflectionCacheCombinedPipelineLane,"));
            Assert.That(mesh, Does.Contain(
                "private const int HybridReflectionLaneCount = 4;"));
            Assert.That(mesh, Does.Contain(
                "RendererBuildConfiguration.FastPipelineStartup"));
            Assert.That(mesh, Does.Contain(
                "familyCount = RendererBuildConfiguration.FastPipelineStartup"));
            Assert.That(renderer, Does.Contain(
                "ref _hybridReflectionReceiverPipelinesPrepared"));
            Assert.That(renderer, Does.Contain(
                "ref _hybridReflectionReceiverPerformancePipelinesPrepared"));
            Assert.That(fullQuality, Does.Contain(
                "PrepareHybridReflectionReceiverPipelines)"));
            Assert.That(fullQuality, Does.Contain(
                "!RendererBuildConfiguration.ProgressivePipelineStartup"));
            Assert.That(fullQuality, Does.Contain(
                "PrepareHybridReflectionReceiverPerformancePipelines()"));
            Assert.That(postFirstPresent,
                Is.GreaterThan(fullQualityStart));
            Assert.That(renderer[postFirstPresent..], Does.Contain(
                "PrepareHybridReflectionReceiverPerformancePipelines"));
            Assert.That(renderer[postFirstPresent..], Does.Contain(
                "exact canonical rendering retained"));
            Assert.That(forward, Does.Contain(
                "AreHybridReflectionExactPipelinesReady"));
            Assert.That(forward, Does.Not.Contain(
                "TryPrepareHybridReflectionPipelines"),
                "Render-time command preparation must remain lookup-only.");
            Assert.That(forward, Does.Contain(
                "SimpleDdgiReceiverCacheFallbackReason.PipelineUnavailable"));
            Assert.That(forward, Does.Contain(
                "exact pipeline selected"));
            Assert.That(renderer, Does.Contain(
                "!runtime.ScreenPipelinesAvailable"));
        });
    }

    private static HybridReflectionHistoryRevision Revision() => new(
        Width: 1920u,
        Height: 1080u,
        Mode: ReflectionMode.HybridRayQuery,
        ReceiverPayloadAbiVersion: ReflectionSettings.ReceiverPayloadAbiVersion,
        FullResolutionRoughness: 0.2f,
        HalfResolutionRoughness: 0.5f,
        QuarterResolutionRoughness: 0.8f,
        RaySceneGeneration: 3u,
        DdgiTopologyGeneration: 4u,
        MaterialRevision: 5u,
        ReflectionProbeRevision: 6UL,
        EnvironmentGeneration: 7u,
        CameraCutSerial: 8UL);

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
