using System.Linq;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiFeatureStateFactoryTests
{
    [Test]
    public void Create_RequestedPagedFarFieldWithoutPoolIsFallbackNotActive()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            FarFieldPagedFeatureEnabled = 1,
            FarFieldPagedMode = 0,
            FarFieldPagePoolCapacity = 0
        };

        GiFeatureState state = GiFeatureStateFactory.Create(diagnostics)
            .Single(feature => feature.Name == "paged-far-field");

        Assert.Multiple(() =>
        {
            Assert.That(state.Requested, Is.True);
            Assert.That(state.Active, Is.False);
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(state.Reason, Does.Contain("page pool"));
        });
    }

    [Test]
    public void Create_AllocatedPagedFarFieldWithoutPublishedPagesRemainsFallback()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            FarFieldPagedFeatureEnabled = 1,
            FarFieldPagedMode = 1,
            FarFieldPagePoolCapacity = 16,
            FarFieldResidentPageCount = 0
        };

        GiFeatureState state = GiFeatureStateFactory.Create(diagnostics)
            .Single(feature => feature.Name == "paged-far-field");

        Assert.Multiple(() =>
        {
            Assert.That(state.Active, Is.False);
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(state.Reason, Does.Contain("no page has been published"));
        });
    }

    [Test]
    public void Create_PublishedPagedFarFieldWithoutPendingBakesIsActive()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            FarFieldPagedFeatureEnabled = 1,
            FarFieldPagedMode = 1,
            FarFieldPagePoolCapacity = 16,
            FarFieldResidentPageCount = 12,
            FarFieldPendingPageCount = 0
        };

        GiFeatureState state = GiFeatureStateFactory.Create(diagnostics)
            .Single(feature => feature.Name == "paged-far-field");

        Assert.Multiple(() =>
        {
            Assert.That(state.Active, Is.True);
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Active));
            Assert.That(state.Reason, Does.Contain("every resident page is published"));
        });
    }

    [Test]
    public void Create_RejectedAccelerationStructureResidentSetIsFallbackAndStructuredError()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            StreamedGiAccelerationStructuresFeatureEnabled = 1,
            AccelerationStructureStreamingEnabled = 1,
            AccelerationStructureBlasBudgetRejectedCount = 12,
            AccelerationStructureFallbackReason = "no partial TLAS was published"
        };

        IReadOnlyList<GiFeatureState> states = GiFeatureStateFactory.Create(diagnostics);
        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(
            diagnostics,
            new GiWarningEvaluator().Evaluate(diagnostics),
            states);
        GiFeatureState state = states.Single(feature => feature.Name == "gi-acceleration-structure-streaming");
        GiDiagnosticWarning warning = warnings.Single(
            candidate => candidate.Code == GiDiagnosticWarningCode.AccelerationStructureIncomplete);

        Assert.Multiple(() =>
        {
            Assert.That(state.Active, Is.False);
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(state.Reason, Does.Contain("no partial TLAS"));
            Assert.That(warning.Severity, Is.EqualTo(GiDiagnosticSeverity.Error));
            Assert.That(warning.ObservedValue, Is.EqualTo(12));
        });
    }

    [Test]
    public void Create_AsyncFallbackCarriesReasonAndStructuredWarning()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            AsyncComputeRequested = 1,
            AsyncComputeSupported = 1,
            AsyncComputeEnabled = 0,
            AsyncComputeLastFallbackReason = "queue ownership validation failed"
        };
        IReadOnlyList<GiFeatureState> states = GiFeatureStateFactory.Create(diagnostics);
        GiWarningEvaluationResult evaluation = new GiWarningEvaluator().Evaluate(diagnostics);
        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(diagnostics, evaluation, states);
        GiFeatureState state = states.Single(feature => feature.Name == "async-compute");

        Assert.Multiple(() =>
        {
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(state.Reason, Does.Contain("queue ownership"));
            Assert.That(warnings.Single(warning => warning.Code == GiDiagnosticWarningCode.AsyncComputeFallback).Message,
                Does.Contain("queue ownership"));
        });
    }

    [Test]
    public void Create_EmergencyGiFallbackKeepsAuthoredRequestsVisibleAndEmitsRollbackWarning()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationRequested = 1,
            GlobalIlluminationRequestedMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationEmergencyFallbackEnabled = 1,
            GlobalIlluminationFallbackReason = "Emergency GI fallback is enabled.",
            GlobalIlluminationDdgiRequested = 1,
            SimpleDdgiRequested = 1,
            GlobalIlluminationRayQueryRequested = 1,
            GlobalIlluminationRayQuerySupported = 1,
            FarFieldPagedFeatureEnabled = 1,
            SimpleDdgiSampledAtlasRequested = 1,
            StreamedGiAccelerationStructuresFeatureEnabled = 1,
            DdgiDetailedCountersRequested = 1
        };

        IReadOnlyList<GiFeatureState> states = GiFeatureStateFactory.Create(diagnostics);
        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(
            diagnostics,
            new GiWarningEvaluator().Evaluate(diagnostics),
            states);

        GiFeatureState global = states.Single(feature => feature.Name == "global-illumination");
        GiFeatureState simple = states.Single(feature => feature.Name == "simple-ddgi");
        GiFeatureState emergency = states.Single(feature => feature.Name == "emergency-gi-fallback");

        Assert.Multiple(() =>
        {
            Assert.That(global.Requested, Is.True);
            Assert.That(global.Active, Is.False);
            Assert.That(global.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(simple.Requested, Is.True);
            Assert.That(simple.Active, Is.False);
            Assert.That(simple.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(emergency.Active, Is.True);
            Assert.That(emergency.Status, Is.EqualTo(GiFeatureStateStatus.Active));
            Assert.That(warnings.Any(warning => warning.Code == GiDiagnosticWarningCode.EmergencyGiFallbackActive), Is.True);
        });
    }

    [Test]
    public void Create_DegradedLayoutAndRequestedBudgetOverrunEmitStructuredActionableWarnings()
    {
        var layout = new SimpleDdgiLayoutTelemetry(
            IsAvailable: true,
            Tier: DdgiQualityTier.DdgiHigh,
            AdmissionMode: SimpleDdgiLayoutAdmissionMode.Degrade,
            ProbeBudget: 16,
            PersistentMemoryBudgetBytes: 1_000,
            VolumeBudget: 1,
            RequestedProbeCount: 32,
            AcceptedProbeCount: 16,
            RequestedPersistentBytes: 2_000,
            AcceptedPersistentBytes: 1_000,
            RequestedVolumeCount: 2,
            AcceptedVolumeCount: 1,
            RejectedVolumeCount: 1,
            WasDegraded: true,
            Summary: "requested=32 accepted=16 probeBudget=16 persistent=1000/1000 rejected=1",
            Volumes:
            [
                new SimpleDdgiLayoutVolumeTelemetry(
                    "hero", 0, true, SimpleDdgiVolumePurpose.ReceiverHero, 100, 1.0f,
                    16, 16, 1_000, SimpleDdgiLayoutDecision.Accepted, "accepted"),
                new SimpleDdgiLayoutVolumeTelemetry(
                    "transition", 1, false, SimpleDdgiVolumePurpose.TransitionSupport, 0, 3.0f,
                    16, 0, 1_000, SimpleDdgiLayoutDecision.RejectedBudget, "probe-and-memory-budget")
            ]);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiLayout = layout,
            CaptureRun = new PerformanceCaptureRunMetadata("Sponza", "LayoutGate", "Release", "1", "c", "s", 4)
        };

        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(
            diagnostics,
            new GiWarningEvaluator().Evaluate(diagnostics),
            GiFeatureStateFactory.Create(diagnostics));

        GiDiagnosticWarning degraded = warnings.Single(warning => warning.Code == GiDiagnosticWarningCode.SimpleDdgiLayoutDegraded);
        GiDiagnosticWarning probeOverrun = warnings.Single(warning =>
            warning.Code == GiDiagnosticWarningCode.GiBudgetOverrun &&
            warning.Feature == "simple-ddgi-layout-probes");

        Assert.Multiple(() =>
        {
            Assert.That(degraded.Severity, Is.EqualTo(GiDiagnosticSeverity.Warning));
            Assert.That(degraded.Message, Does.Contain("rejected"));
            Assert.That(degraded.RecommendedAction, Does.Contain("per-volume"));
            Assert.That(probeOverrun.ObservedValue, Is.EqualTo(32));
            Assert.That(probeOverrun.Threshold, Is.EqualTo(16));
            Assert.That(warnings.Any(warning =>
                warning.Code == GiDiagnosticWarningCode.GiBudgetOverrun &&
                warning.Feature == "simple-ddgi-layout-memory"), Is.True);
        });
    }

    [Test]
    public void Create_OptionalLayoutRejectionRemainsAuditableWithoutPoisoningRequiredLayout()
    {
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new SimpleDdgiLayoutVolumeRequest(
                "required-ring", 0, false, SimpleDdgiVolumePurpose.TransitionSupport,
                0, 1.0f, 16),
            new SimpleDdgiLayoutVolumeRequest(
                "optional-refinement", 1, false, SimpleDdgiVolumePurpose.ReceiverHero,
                0, 0.5f, 4)
            {
                AdmissionClass = SimpleDdgiLayoutAdmissionClass.Optional
            }
        ];
        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            requests,
            new SimpleDdgiLayoutBudget(
                DdgiQualityTier.DdgiHigh,
                ProbeBudget: 16,
                PersistentMemoryBudgetBytes: ulong.MaxValue,
                VolumeBudget: 2),
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject);
        SimpleDdgiLayoutTelemetry layout = SimpleDdgiLayoutTelemetryFactory.Create(report);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiLayout = layout
        };

        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(
            diagnostics,
            new GiWarningEvaluator().Evaluate(diagnostics),
            GiFeatureStateFactory.Create(diagnostics));

        Assert.Multiple(() =>
        {
            Assert.That(layout.WasDegraded, Is.True);
            Assert.That(layout.HasRequiredDegradation, Is.False);
            Assert.That(layout.RequiredRejectedVolumeCount, Is.Zero);
            Assert.That(layout.OptionalRejectedVolumeCount, Is.EqualTo(1));
            Assert.That(layout.Volumes.Single(volume =>
                    volume.Id == "optional-refinement").AdmissionClass,
                Is.EqualTo(SimpleDdgiLayoutAdmissionClass.Optional));
            Assert.That(warnings.Any(warning =>
                    warning.Code == GiDiagnosticWarningCode.SimpleDdgiLayoutDegraded),
                Is.False);
            Assert.That(warnings.Any(warning =>
                    warning.Code == GiDiagnosticWarningCode.GiBudgetOverrun &&
                    warning.Feature.StartsWith("simple-ddgi-layout", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void Create_SteppedSunLagBeyondDeclaredSweepEmitsStructuredWarning()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportSourceCohortTransitionActive = 1,
            SimpleDdgiTransportSourceRefreshTargetProbeCount = 12,
            SimpleDdgiTransportSourceRefreshCapacityShortfall = 3,
            SimpleDdgiTransportSourceRefreshFrames = 240,
            SimpleDdgiTransportSourceStepAgeP95Frames = 310
        };

        IReadOnlyList<GiDiagnosticWarning> warnings = GiDiagnosticWarningFactory.Create(
            diagnostics,
            new GiWarningEvaluator().Evaluate(diagnostics),
            GiFeatureStateFactory.Create(diagnostics));
        GiDiagnosticWarning capacity = warnings.Single(warning =>
            warning.Code == GiDiagnosticWarningCode.SourceSweepBudgetExceeded &&
            warning.Feature == "simple-ddgi-source-sweep-capacity");
        GiDiagnosticWarning lag = warnings.Single(warning =>
            warning.Code == GiDiagnosticWarningCode.SourceSweepBudgetExceeded &&
            warning.Feature == "simple-ddgi-source-sweep-lag");

        Assert.Multiple(() =>
        {
            Assert.That(capacity.ObservedValue, Is.EqualTo(12));
            Assert.That(capacity.Threshold, Is.EqualTo(9));
            Assert.That(lag.ObservedValue, Is.EqualTo(310));
            Assert.That(lag.Threshold, Is.EqualTo(240));
            Assert.That(lag.Freshness, Is.EqualTo(GiMetricFreshness.DelayedReadback));
        });
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void Create_FrozenSparseResidencyIsFallbackNotActive(
        bool stateValid,
        bool sparseAuthoritative)
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiProbeResidency = new SimpleDdgiProbeResidencyTelemetry(
                IsAvailable: true,
                Mode: SimpleDdgiProbeResidencyMode.SparseNearRing,
                SparseAuthoritative: sparseAuthoritative,
                FallbackReason: stateValid
                    ? "residency validation stopped mutation"
                    : "residency mapping invalid")
            {
                MutationFrozen = true,
                ResidencyStateValid = stateValid
            }
        };

        GiFeatureState state = GiFeatureStateFactory.Create(diagnostics)
            .Single(feature => feature.Name == "simple-ddgi-probe-residency");

        Assert.Multiple(() =>
        {
            Assert.That(state.Requested, Is.True);
            Assert.That(state.Active, Is.False);
            Assert.That(state.Status, Is.EqualTo(GiFeatureStateStatus.Fallback));
            Assert.That(state.Reason, Does.Contain("residency"));
        });
    }

    [Test]
    public void LayoutTelemetryFactory_RetainsRejectedRequestedBytesIncludingSampledAtlasReservation()
    {
        const int configuredUpdates = 4;
        const int rays = 8;
        var request = new SimpleDdgiLayoutVolumeRequest(
            "rejected-hero",
            0,
            true,
            SimpleDdgiVolumePurpose.ReceiverHero,
            100,
            1.0f,
            16);
        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            [request],
            new SimpleDdgiLayoutBudget(DdgiQualityTier.DdgiLow, 0, 0, 0),
            sampledAtlasRequested: true,
            SimpleDdgiLayoutAdmissionMode.Degrade,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: configuredUpdates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        // Deliberately omit allocation settings here. The capture factory must
        // persist the compiler's exact evidence rather than reconstructing it.
        SimpleDdgiLayoutTelemetry telemetry =
            SimpleDdgiLayoutTelemetryFactory.Create(report);
        SimpleDdgiLayoutVolumeTelemetry rejected = telemetry.Volumes.Single();
        ulong expectedBytes = SimpleDdgiMemoryPlan.Create(
            probeCount: 16,
            updateRequestCapacity: configuredUpdates * 2,
            rayCapacity: rays,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight).LiveBytes;

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Decision, Is.EqualTo(SimpleDdgiLayoutDecision.RejectedVolumeLimit));
            Assert.That(rejected.AcceptedProbeCount, Is.Zero);
            Assert.That(rejected.RequestedPersistentBytes, Is.EqualTo(expectedBytes));
            Assert.That(
                rejected.RequestedPersistentBytes,
                Is.EqualTo(report.Volumes.Single().RequestedPersistentBytes));
        });
    }

    [Test]
    public void ResolvedGiSettings_StableHashIncludesLayoutBudgetFallbackAndLightingInputs()
    {
        var layout = new SimpleDdgiLayoutTelemetry(
            true,
            DdgiQualityTier.DdgiHigh,
            SimpleDdgiLayoutAdmissionMode.Degrade,
            128,
            4_096,
            4,
            64,
            64,
            2_048,
            2_048,
            1,
            1,
            0,
            false,
            "within-budget",
            [new SimpleDdgiLayoutVolumeTelemetry(
                "upper-facade", 0, true, SimpleDdgiVolumePurpose.ReceiverHero, 100, 1.0f,
                64, 64, 2_048, SimpleDdgiLayoutDecision.Accepted, "accepted")]);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationRequested = 1,
            GlobalIlluminationRequestedMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiRequested = 1,
            SimpleDdgiRequested = 1,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            GlobalIlluminationIndirectIntensity = 1.0f,
            GlobalIlluminationEnvironmentFallbackIntensity = 0.75f,
            DdgiAtlasMemoryBudgetBytes = 4_096,
            SimpleDdgiLayout = layout,
            SimpleDdgiSampledAtlasRequested = 1,
            SimpleDdgiSampledAtlasFallbackReason = "mirror-not-supported",
            SkyIntensity = 1.2f,
            DiffuseIblIntensity = 0.8f,
            SpecularIblIntensity = 0.6f,
            Exposure = 1.0f,
            DirectionalShadowsEnabled = 1,
            DirectionalShadowCascadeCount = 4
        };

        ResolvedGiSettingsMetadata first = ResolvedGiSettingsMetadataFactory.Create(diagnostics);
        ResolvedGiSettingsMetadata repeat = ResolvedGiSettingsMetadataFactory.Create(diagnostics);
        ResolvedGiSettingsMetadata changedLayout = ResolvedGiSettingsMetadataFactory.Create(diagnostics with
        {
            SimpleDdgiLayout = layout with { ProbeBudget = 96 }
        });
        ResolvedGiSettingsMetadata changedFallback = ResolvedGiSettingsMetadataFactory.Create(diagnostics with
        {
            SimpleDdgiSampledAtlasFallbackReason = "allocation-budget"
        });
        ResolvedGiSettingsMetadata changedLighting = ResolvedGiSettingsMetadataFactory.Create(diagnostics with
        {
            GlobalIlluminationIndirectIntensity = 1.1f
        });
        ResolvedGiSettingsMetadata changedRuntimeTelemetry = ResolvedGiSettingsMetadataFactory.Create(diagnostics with
        {
            SimpleDdgiTransportSourceRefreshFrames = 99,
            SimpleDdgiTransportSourceRefreshTargetProbeCount = 77,
            SimpleDdgiTransportSourceRefreshCapacityShortfall = 3,
            SimpleDdgiTransportSourceCohortTransitionActive = 1,
            SimpleDdgiTransportSourceCohortTransitionCount = 12,
            SimpleDdgiTransportSourceCohortElapsedFrames = 34,
            SimpleDdgiTransportSourceStepStaleProbeCount = 56,
            SimpleDdgiTransportSourceStepAgeP95Frames = 78,
            SimpleDdgiTransportSourceStepAgeMaximumFrames = 90,
            SimpleDdgiTransportGlobalConvergenceElapsedFrames = 123,
            SimpleDdgiTransportCalibrationChangeCount = 456,
            DdgiScheduledRequestBudget = 17,
            DdgiScheduledPrimaryRayBudget = 18,
            DdgiAdaptiveBudgetScale = 0.5f,
            DdgiAdaptiveBudgetReduced = 1,
            DdgiEmergencyDegradeActive = 1,
            DdgiAdaptiveBudgetReason = "frame-pressure",
            DdgiEffectiveMaxShadedLights = 1,
            DdgiLightSelectionMode = "disabled-for-cache-reuse-frame",
            DdgiEmissiveSourceRevision = 999,
            SimpleDdgiSchedulerPolicy = diagnostics.SimpleDdgiSchedulerPolicy with
            {
                EffectiveRequestBudget = 17,
                PressureReason = "FeedbackReducedBudget"
            }
        });
        RendererDiagnostics featureReasonDiagnostics = diagnostics with
        {
            GiFeatureStates = [new GiFeatureState(
                "simple-ddgi-transport-v2",
                true,
                true,
                true,
                true,
                GiFeatureStateStatus.Active,
                "13000/13117 probes converged after 71 frames")]
        };
        ResolvedGiSettingsMetadata featureReasonFirst =
            ResolvedGiSettingsMetadataFactory.Create(featureReasonDiagnostics);
        ResolvedGiSettingsMetadata featureReasonChanged =
            ResolvedGiSettingsMetadataFactory.Create(featureReasonDiagnostics with
            {
                GiFeatureStates = [featureReasonDiagnostics.GiFeatureStates[0] with
                {
                    Reason = "13100/13117 probes converged after 72 frames"
                }]
            });

        Assert.Multiple(() =>
        {
            Assert.That(repeat.StableHash, Is.EqualTo(first.StableHash));
            Assert.That(changedLayout.StableHash, Is.Not.EqualTo(first.StableHash));
            Assert.That(changedFallback.StableHash, Is.Not.EqualTo(first.StableHash));
            Assert.That(changedLighting.StableHash, Is.Not.EqualTo(first.StableHash));
            Assert.That(changedRuntimeTelemetry.StableHash, Is.EqualTo(first.StableHash));
            Assert.That(featureReasonChanged.StableHash, Is.EqualTo(featureReasonFirst.StableHash));
            Assert.That(first.EffectiveSettings, Does.Contain("layout.probeBudget=128"));
            Assert.That(first.EffectiveSettings, Does.Contain("gi.indirectIntensity=1"));
            Assert.That(first.EffectiveSettings.Any(setting => setting.StartsWith("feature[", System.StringComparison.Ordinal)), Is.True);
        });
    }
}
