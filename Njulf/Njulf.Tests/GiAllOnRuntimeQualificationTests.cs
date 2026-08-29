using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Njulf.Rendering;
using NjulfHelloGame;
using NUnit.Framework;
using System.Text.Json;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiAllOnRuntimeQualificationTests
{
    [Test]
    public void SettingsAloneCannotPassRuntimeQualification()
    {
        var accumulator = new GiAllOnRuntimeQualificationAccumulator();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverCache =
                SimpleDdgiReceiverCacheDiagnostics.Exact(
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                    SimpleDdgiReceiverCacheFallbackReason.ResourceUnavailable,
                    "test-resource-unavailable"),
            SimpleDdgiTransportAccelerationEnabled = true,
            SimpleDdgiTransportAcceleratedSweepCount = 2,
            GiRoadmapExperiments =
                GiRoadmapExperimentDiagnostics.Disabled with
                {
                    Modes = RequestedModes()
                }
        };

        Assert.That(accumulator.Observe(diagnostics), Is.True);
        GiAllOnRuntimeQualificationSnapshot snapshot = accumulator.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ObservedAllOnFrameCount, Is.EqualTo(1));
            Assert.That(snapshot.ReceiverCache.Requested, Is.True);
            Assert.That(snapshot.ReceiverCache.Effective, Is.False);
            Assert.That(snapshot.AcceleratedTransportSolver.Executed, Is.False);
            Assert.That(snapshot.OpacityMicromaps.Consumed, Is.False);
            Assert.That(snapshot.Passed, Is.False);
        });
    }

    [Test]
    public void NonAllOnFramesAreRejectedWithMachineReadableReason()
    {
        var accumulator = new GiAllOnRuntimeQualificationAccumulator();

        Assert.That(
            accumulator.Observe(RendererDiagnostics.Empty),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(accumulator.Snapshot.ObservedAllOnFrameCount, Is.Zero);
            Assert.That(accumulator.Snapshot.RejectedNonAllOnFrameCount,
                Is.EqualTo(1));
            Assert.That(accumulator.Snapshot.LastRequestMismatchDetail,
                Does.Contain("receiver-cache-not-requested"));
        });
    }

    [Test]
    public void CompleteRuntimeSnapshotPassesEveryStrictCriterion()
    {
        GiAllOnRuntimeQualificationSnapshot snapshot = PassingSnapshot();

        SampleGiAllOnQualificationCriterion[] criteria =
            SampleGiAllOnQualificationRunner.Evaluate(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Passed, Is.True);
            Assert.That(criteria, Is.Not.Empty);
            Assert.That(criteria.All(static criterion => criterion.Passed),
                Is.True);
        });
    }

    [Test]
    public void PassedQualification_OwnsTheEarlySmokeTerminalCondition()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleGiAllOnQualificationContract
                    .RequiresGeneralSmokeCompletion(
                        allOnQualificationPassed: true),
                Is.False);
            Assert.That(
                SampleGiAllOnQualificationContract
                    .RequiresGeneralSmokeCompletion(
                        allOnQualificationPassed: false),
                Is.True);
        });
    }

    [Test]
    public void FatalRuntimeEvidenceInvalidatesOtherwiseCompleteSnapshot()
    {
        GiAllOnRuntimeQualificationSnapshot snapshot = PassingSnapshot() with
        {
            FatalRuntimeFailureObserved = true,
            FatalRuntimeFailureDetail = "receiver-cache-adaptive-overflow"
        };

        SampleGiAllOnQualificationCriterion[] criteria =
            SampleGiAllOnQualificationRunner.Evaluate(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Passed, Is.False);
            Assert.That(criteria.Single(criterion =>
                    criterion.Name == "fatal-runtime-health").Passed,
                Is.False);
        });
    }

    [Test]
    public void ValidationMessagesAreFatalRuntimeEvidence()
    {
        var accumulator = new GiAllOnRuntimeQualificationAccumulator();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ValidationErrorMessageCount = 1,
            SimpleDdgiReceiverCache =
                SimpleDdgiReceiverCacheDiagnostics.Exact(
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                    SimpleDdgiReceiverCacheFallbackReason.ResourceUnavailable,
                    "test-resource-unavailable"),
            SimpleDdgiTransportAccelerationEnabled = true,
            SimpleDdgiTransportAcceleratedSweepCount = 2,
            GiRoadmapExperiments =
                GiRoadmapExperimentDiagnostics.Disabled with
                {
                    Modes = RequestedModes()
                }
        };

        Assert.That(accumulator.Observe(diagnostics), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(accumulator.Snapshot.FatalRuntimeFailureObserved,
                Is.True);
            Assert.That(accumulator.Snapshot.FatalRuntimeFailureDetail,
                Does.Contain("vulkan-validation"));
            Assert.That(accumulator.Snapshot.Passed, Is.False);
        });
    }

    [Test]
    public void CausticTransactionalStagesAcrossAllOnFramesQualifyExecution()
    {
        var accumulator = new GiAllOnRuntimeQualificationAccumulator();
        RendererDiagnostics buildFrame = RequestedAllOnDiagnostics(
            GiCausticTimedStage.Task |
            GiCausticTimedStage.Trace |
            GiCausticTimedStage.CacheBuild);

        Assert.That(accumulator.Observe(buildFrame), Is.True);
        Assert.That(accumulator.Snapshot.TaggedCaustics.Executed, Is.False,
            "A cache-build frame alone must not qualify the full C4 graph.");

        RendererDiagnostics consumeFrame = RequestedAllOnDiagnostics(
            GiCausticTimedStage.Resolve |
            GiCausticTimedStage.Composite);
        Assert.That(accumulator.Observe(consumeFrame), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(accumulator.Snapshot.ObservedAllOnFrameCount,
                Is.EqualTo(2));
            Assert.That(accumulator.Snapshot.TaggedCaustics.Requested, Is.True);
            Assert.That(accumulator.Snapshot.TaggedCaustics.Supported, Is.True);
            Assert.That(accumulator.Snapshot.TaggedCaustics.Effective, Is.True);
            Assert.That(accumulator.Snapshot.TaggedCaustics.Executed, Is.True);
            Assert.That(accumulator.Snapshot.TaggedCaustics.Consumed, Is.False,
                "Stage timings cannot manufacture authoritative publication evidence.");
            Assert.That(accumulator.Snapshot.TaggedCaustics.Passed, Is.False);
        });
    }

    [Test]
    public void GuidingPendingReadback_IsNotAProductionFault()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                VulkanRenderer.IsFatalSimpleDdgiGuidingRuntimeCapability(
                    SimpleDdgiGuidingGpuCapabilityReason.None),
                Is.False);
            Assert.That(
                VulkanRenderer.IsFatalSimpleDdgiGuidingRuntimeCapability(
                    SimpleDdgiGuidingGpuCapabilityReason
                        .HeaderReadbackRejected),
                Is.True);
            Assert.That(
                VulkanRenderer.IsFatalSimpleDdgiGuidingRuntimeCapability(
                    SimpleDdgiGuidingGpuCapabilityReason
                        .SampleReadbackRejected),
                Is.True);
        });
    }

    [Test]
    public void HostGuardReplacesStaleReportAndPublishesTerminalFailure()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "gi-all-on-host-guard",
            TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "report.json");
        File.WriteAllText(reportPath, "{\"Status\":\"passed\"}");

        using (var guard = new SampleGiAllOnQualificationHostFailureGuard(
                   reportPath,
                   SampleSceneKind.MaterialShowcase))
        {
            using (JsonDocument inProgress = JsonDocument.Parse(
                       File.ReadAllText(reportPath)))
            {
                Assert.That(
                    inProgress.RootElement.GetProperty("Status").GetString(),
                    Is.EqualTo("in-progress"));
            }

            guard.RecordHostFailure("renderer-startup-failed");
            Assert.That(guard.CompleteHostRun(1), Is.False);
        }

        using JsonDocument failed = JsonDocument.Parse(
            File.ReadAllText(reportPath));
        Assert.Multiple(() =>
        {
            Assert.That(
                failed.RootElement.GetProperty("Status").GetString(),
                Is.EqualTo("failed"));
            Assert.That(
                failed.RootElement.GetProperty("Passed").GetBoolean(),
                Is.False);
            Assert.That(File.Exists(reportPath + ".tmp"), Is.False);
            Assert.That(File.ReadAllText(reportPath),
                Does.Contain("renderer-startup-failed"));
        });
    }

    [Test]
    public void QualificationIsolationDisablesOnlyUnrelatedPipelineFamilies()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        settings.Transparency.ThickTransmissionMode =
            ThickTransmissionMode.RayQuery;
        settings.Transparency.DispersionMode = DispersionMode.RgbTriplet;
        settings.Reflections.Enabled = true;
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.SimpleDdgiReceiverCacheMode =
            SimpleDdgiReceiverCacheMode.TemporalAdaptive;
        gi.SimpleDdgiTransportAccelerationEnabled = true;
        gi.SimpleDdgiTransportAcceleratedSweepCount = 2;
        gi.DdgiOpacityMicromapMode =
            DdgiOpacityMicromapMode.ExtFourStateExperiment;
        gi.SimpleDdgiDirectionalGuidingMode =
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment;
        gi.GiCausticMode = GiCausticMode.WorldCacheExperiment;

        SampleGiAllOnQualificationContract.ApplyIsolationSettings(
            settings,
            SampleSceneKind.MaterialShowcase);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.ThickTransmissionMode,
                Is.EqualTo(ThickTransmissionMode.Approximation));
            Assert.That(settings.Transparency.DispersionMode,
                Is.EqualTo(DispersionMode.Off));
            Assert.That(settings.Reflections.Enabled, Is.False);
            Assert.That(gi.SimpleDdgiRingCount, Is.Zero);
            Assert.That(gi.SimpleDdgiReceiverCacheMode,
                Is.EqualTo(SimpleDdgiReceiverCacheMode.TemporalAdaptive));
            Assert.That(gi.SimpleDdgiTransportAccelerationEnabled, Is.True);
            Assert.That(gi.SimpleDdgiTransportAcceleratedSweepCount,
                Is.EqualTo(2));
            Assert.That(gi.DdgiOpacityMicromapMode,
                Is.EqualTo(DdgiOpacityMicromapMode.ExtFourStateExperiment));
            Assert.That(gi.SimpleDdgiDirectionalGuidingMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment));
            Assert.That(gi.GiCausticMode,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
        });
    }

    private static GiAllOnRuntimeQualificationSnapshot PassingSnapshot()
    {
        var complete = new GiAllOnFeatureRuntimeEvidence(
            Requested: true,
            Supported: true,
            Effective: true,
            Executed: true,
            Consumed: true,
            Detail: "runtime-complete");
        return new GiAllOnRuntimeQualificationSnapshot
        {
            FirstFrameSerial = 100,
            LastFrameSerial = 102,
            ObservedAllOnFrameCount = 3,
            SimultaneouslyEffectiveFrameObserved = true,
            ReceiverCache = complete,
            AcceleratedTransportSolver = complete,
            OpacityMicromaps = complete,
            DirectionalGuiding = complete,
            TaggedCaustics = complete,
            CurrentTailCertificateObserved = true
        };
    }

    private static RendererDiagnostics RequestedAllOnDiagnostics(
        GiCausticTimedStage causticStages)
    {
        var causticResource = new GiCausticGpuRuntimeSnapshot(
            GiCausticGpuResourceState.ReadyForBuild,
            IsEffectivelyEnabled: true,
            AllocationEpoch: 1UL,
            AllocatedBytes: 1UL,
            DescriptorCount: GiCausticGpuAbi.DescriptorCount,
            PhotonReadBankIndex: -1,
            PhotonWriteBankIndex: 0,
            CacheReadBankIndex: -1,
            CacheWriteBankIndex: 0,
            ReadableGeneration: 0u,
            PendingGeneration: 0u,
            PublicationFailureCount: 0UL,
            InvalidationCount: 0UL,
            AllocationFailureCount: 0UL,
            MemoryRequirements: GiCausticGpuMemoryRequirements.Empty,
            Reason: "test-ready");
        var caustics = new GiCausticDiagnostics
        {
            State = GiCausticTelemetryState.ResourceIncomplete,
            Runtime = new GiCausticVulkanRuntimeDiagnostics(
                GiCausticVulkanRuntimeCapabilityReason.None,
                TaggedTransportProducerAvailable: true,
                DeterministicCacheBuildQualified: true,
                DescriptorContextRegistered: true,
                HeaderReadbackPending: false,
                Resource: causticResource,
                Detail: "test-active"),
            Timings = new GiCausticStageTimings(
                TaskMicroseconds: 1,
                TraceMicroseconds: 1,
                CacheBuildMicroseconds: 1,
                ResolveMicroseconds: 1,
                CompositeMicroseconds: 1,
                AvailableStages: causticStages),
            Reason = "test-active"
        };

        return RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverCache =
                SimpleDdgiReceiverCacheDiagnostics.Exact(
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                    SimpleDdgiReceiverCacheFallbackReason.ResourceUnavailable,
                    "test-resource-unavailable"),
            SimpleDdgiTransportAccelerationEnabled = true,
            SimpleDdgiTransportAcceleratedSweepCount = 2,
            GiRoadmapExperiments =
                GiRoadmapExperimentDiagnostics.Disabled with
                {
                    Modes = RequestedModes(),
                    CausticRuntime = caustics
                }
        };
    }

    private static GiRoadmapExperimentModeDiagnostics RequestedModes() => new(
        GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>.Disabled(
            SimpleDdgiReceiverFeedbackMode.Off),
        Active(
            DdgiOpacityMicromapMode.ExtFourStateExperiment),
        Active(
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment),
        Active(
            GiCausticMode.WorldCacheExperiment),
        GiExperimentModeState<SimpleDdgiNearFieldResidualMode>.Disabled(
            SimpleDdgiNearFieldResidualMode.Off));

    private static GiExperimentModeState<TMode> Active<TMode>(
        TMode mode)
        where TMode : struct, Enum => new(
        mode,
        mode,
        mode,
        mode,
        GiExperimentFallbackReason.None,
        "active",
        string.Empty);
}
