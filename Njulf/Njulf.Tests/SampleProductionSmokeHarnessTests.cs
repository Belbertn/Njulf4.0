using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Core.Camera;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleProductionSmokeHarnessTests
{
    [Test]
    public void QualitySwitch_VisitsEveryShippingTierAndRollsBackInProcess()
    {
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode = AsyncComputeMode.ForceEnabledForValidation;
        settings.GlobalIllumination.FarFieldClipmapEnabled = true;
        settings.SceneSubmission.ValidationCompareCpuGpuLists = true;
        settings.Transparency.Mode = TransparencyMode.WeightedBlendedOit;
        var operations = new List<SampleSmokeOperationResult>();
        bool exited = false;
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => exited = true);

        for (int frame = 0; frame < 8 && !runner.Completed; frame++)
        {
            runner.OnFrameRendered(
                frame,
                RendererDiagnostics.Empty with
                {
                    ActiveQualityPreset = settings.QualityPreset,
                    GpuTimingSupported = 1,
                    GpuTimingEnabled = 1,
                    GpuTimingValid = 1,
                    TrackedGpuMemoryBytes = 1024,
                    GpuMemoryBudgetBytes = 4096,
                    MaterialPrimitiveProfileAbsoluteBudgetBytes =
                        RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                            settings.QualityPreset)
                },
                CreateAvailableBudget());
        }

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Is.Null);
            Assert.That(exited, Is.True);
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(
                settings.AsyncCompute.Mode,
                Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(settings.GlobalIllumination.FarFieldClipmapEnabled, Is.True);
            Assert.That(settings.SceneSubmission.ValidationCompareCpuGpuLists, Is.True);
            Assert.That(
                settings.Transparency.Mode,
                Is.EqualTo(TransparencyMode.WeightedBlendedOit));
            Assert.That(
                runner.Observations.Select(observation => observation.Preset),
                Is.EqualTo(new[]
                {
                    RenderQualityPreset.Low,
                    RenderQualityPreset.Medium,
                    RenderQualityPreset.High,
                    RenderQualityPreset.Ultra,
                    RenderQualityPreset.DdgiHigh
                }));
            Assert.That(
                operations.Single(operation => operation.Name == "quality-switch").Status,
                Is.EqualTo("passed"));
            Assert.That(
                operations.Single(operation => operation.Name == "device-loss-recovery").Status,
                Is.EqualTo("rejected-unsupported"));
        });
    }

    [Test]
    public void QualitySwitch_FailsOnTierBudgetViolation()
    {
        var settings = new RenderSettings();
        var operations = new List<SampleSmokeOperationResult>();
        var metric = new BudgetMetric(
            "GPU memory",
            2048,
            900,
            1024,
            "bytes",
            RenderBudgetStatus.OverBudget);
        RenderBudgetSnapshot budget = CreateAvailableBudget(metric) with
        {
            OverallStatus = RenderBudgetStatus.OverBudget
        };
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });

        runner.OnFrameRendered(0, RendererDiagnostics.Empty, budget);
        runner.OnFrameRendered(
            1,
            RendererDiagnostics.Empty with
            {
                ActiveQualityPreset = settings.QualityPreset,
                GpuTimingSupported = 1,
                GpuTimingEnabled = 1,
                GpuTimingValid = 1,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                        settings.QualityPreset)
            },
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("exceeded a release budget"));
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void QualitySwitch_FailsWhenAdmissionCapDoesNotFollowTier()
    {
        var settings = new RenderSettings();
        var operations = new List<SampleSmokeOperationResult>();
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });

        runner.OnFrameRendered(0, RendererDiagnostics.Empty, RenderBudgetSnapshot.Empty);
        runner.OnFrameRendered(
            1,
            RendererDiagnostics.Empty with
            {
                ActiveQualityPreset = settings.QualityPreset,
                GpuTimingSupported = 1,
                GpuTimingEnabled = 1,
                GpuTimingValid = 1,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    MaterialManager.MaximumPrimitiveProfileGpuBytes
            },
            RenderBudgetSnapshot.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("admission budget did not follow"));
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void QualitySwitch_FailsClosedWhenRequiredTierTelemetryIsUnavailable()
    {
        var settings = new RenderSettings();
        settings.Transparency.Mode = TransparencyMode.WeightedBlendedOit;
        string initialFingerprint = SampleRenderSettingsFingerprint.Capture(settings);
        var operations = new List<SampleSmokeOperationResult>();
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });
        var unavailableCpu = new BudgetMetric(
            "CPU renderer",
            0,
            1,
            2,
            "ms",
            RenderBudgetStatus.Unavailable);

        runner.OnFrameRendered(0, RendererDiagnostics.Empty, RenderBudgetSnapshot.Empty);
        runner.OnFrameRendered(
            1,
            RendererDiagnostics.Empty with
            {
                ActiveQualityPreset = settings.QualityPreset,
                GpuTimingSupported = 1,
                GpuTimingEnabled = 1,
                GpuTimingValid = 1,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                        settings.QualityPreset)
            },
            CreateAvailableBudget(unavailableCpu));

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("CPU renderer"));
            Assert.That(runner.Failure, Does.Contain("unavailable"));
            Assert.That(runner.Failure, Does.Contain("rollback completed"));
            Assert.That(
                SampleRenderSettingsFingerprint.Capture(settings),
                Is.EqualTo(initialFingerprint));
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void QualitySwitch_FailsClosedWhenOverallBudgetStatusIsUnavailable()
    {
        var settings = new RenderSettings();
        var operations = new List<SampleSmokeOperationResult>();
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });
        RenderBudgetSnapshot budget = CreateAvailableBudget() with
        {
            OverallStatus = RenderBudgetStatus.Unavailable
        };

        runner.OnFrameRendered(0, RendererDiagnostics.Empty, budget);
        runner.OnFrameRendered(
            1,
            RendererDiagnostics.Empty with
            {
                ActiveQualityPreset = settings.QualityPreset,
                GpuTimingSupported = 1,
                GpuTimingEnabled = 1,
                GpuTimingValid = 1,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                        settings.QualityPreset)
            },
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("overall budget status is unavailable"));
            Assert.That(runner.Failure, Does.Contain("rollback completed"));
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void RenderSettingsSnapshot_RejectsRestoreIntoAnotherInstance()
    {
        var captured = new RenderSettings();
        SampleRenderSettingsSnapshot snapshot =
            SampleRenderSettingsSnapshot.Capture(captured);

        Assert.That(
            () => snapshot.Restore(new RenderSettings()),
            Throws.ArgumentException.With.Message.Contains(
                "only restore the instance"));
    }

    [Test]
    public void BudgetMetricCoverage_FailsClosedOnMissingDiagnosticsAndNonFiniteAvailableData()
    {
        RenderBudgetSnapshot available = CreateAvailableBudget();
        SampleBudgetMetricCoverage missingDiagnostics =
            SampleBudgetMetricCoverage.Evaluate(
                available.Metrics,
                diagnostics: null,
                subject: "Qualification");
        var nonFiniteCpu = new BudgetMetric(
            "CPU renderer",
            double.NaN,
            1,
            2,
            "ms",
            RenderBudgetStatus.WithinBudget);
        SampleBudgetMetricCoverage nonFinite =
            SampleBudgetMetricCoverage.Evaluate(
                CreateAvailableBudget(nonFiniteCpu).Metrics,
                RendererDiagnostics.Empty,
                "Qualification");

        Assert.Multiple(() =>
        {
            Assert.That(missingDiagnostics.Passed, Is.False);
            Assert.That(missingDiagnostics.Failure, Does.Contain("diagnostics are missing"));
            Assert.That(nonFinite.Passed, Is.False);
            Assert.That(nonFinite.Failure, Does.Contain("non-finite telemetry"));
        });
    }

    [Test]
    public void BudgetMetricCoverage_RejectsStatusThatConcealsAnExceededThreshold()
    {
        var inconsistentCpu = new BudgetMetric(
            "CPU renderer",
            3,
            1,
            2,
            "ms",
            RenderBudgetStatus.WithinBudget);

        SampleBudgetMetricCoverage evaluation =
            SampleBudgetMetricCoverage.Evaluate(
                CreateAvailableBudget(inconsistentCpu).Metrics,
                RendererDiagnostics.Empty,
                "Qualification");

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("CPU renderer"));
            Assert.That(evaluation.Failure, Does.Contain("thresholds require"));
            Assert.That(evaluation.Failure, Does.Contain("OverBudget"));
        });
    }

    [Test]
    public void QualitySwitch_FailsWhenRollbackRestoresOnlyThePreset()
    {
        var settings = new RenderSettings();
        settings.Transparency.Mode = TransparencyMode.WeightedBlendedOit;
        RenderQualityPreset initialPreset = settings.QualityPreset;
        var operations = new List<SampleSmokeOperationResult>();
        var runner = new SampleQualitySwitchSmokeRunner(
            settings.ApplyQualityPreset,
            () => settings.ApplyQualityPreset(initialPreset),
            () => settings.QualityPreset,
            () => SampleRenderSettingsFingerprint.Capture(settings),
            () => "device-stable",
            operations.Add,
            () => { });

        for (int frame = 0; frame < 8 && !runner.Completed; frame++)
        {
            runner.OnFrameRendered(
                frame,
                RendererDiagnostics.Empty with
                {
                    ActiveQualityPreset = settings.QualityPreset,
                    GpuTimingSupported = 1,
                    GpuTimingEnabled = 1,
                    GpuTimingValid = 1,
                    MaterialPrimitiveProfileAbsoluteBudgetBytes =
                        RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                            settings.QualityPreset)
                },
                CreateAvailableBudget());
        }

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("complete render settings"));
            Assert.That(
                operations.Single(operation => operation.Name == "quality-switch").Status,
                Is.EqualTo("failed"));
        });
    }

    [Test]
    public void QualitySwitch_WaitsForPostSwitchGpuTimingInsteadOfAcceptingStaleResults()
    {
        var settings = new RenderSettings();
        var operations = new List<SampleSmokeOperationResult>();
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });

        runner.OnFrameRendered(0, RendererDiagnostics.Empty, CreateAvailableBudget());
        for (int frame = 1; frame <= 3; frame++)
        {
            runner.OnFrameRendered(
                frame,
                RendererDiagnostics.Empty with
                {
                    ActiveQualityPreset = settings.QualityPreset,
                    GpuTimingSupported = 1,
                    GpuTimingEnabled = 1,
                    GpuTimingValid = frame < 3 ? 1 : 0,
                    GpuTimingFrameLatency = 2,
                    MaterialPrimitiveProfileAbsoluteBudgetBytes =
                        RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                            settings.QualityPreset)
                },
                CreateAvailableBudget());
        }

        Assert.That(runner.Observations, Is.Empty);

        runner.OnFrameRendered(
            4,
            RendererDiagnostics.Empty with
            {
                ActiveQualityPreset = settings.QualityPreset,
                GpuTimingSupported = 1,
                GpuTimingEnabled = 1,
                GpuTimingValid = 1,
                GpuTimingFrameLatency = 2,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                        settings.QualityPreset)
            },
            CreateAvailableBudget());

        Assert.Multiple(() =>
        {
            Assert.That(runner.Failure, Is.Null);
            Assert.That(runner.Observations, Has.Count.EqualTo(1));
            Assert.That(runner.Observations[0].Preset, Is.EqualTo(RenderQualityPreset.Low));
            Assert.That(runner.Observations[0].FrameIndex, Is.EqualTo(4));
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.Medium));
        });
    }

    [Test]
    public void QualitySwitch_FailsClosedWhenFreshGpuTimingNeverCompletes()
    {
        var settings = new RenderSettings();
        var operations = new List<SampleSmokeOperationResult>();
        SampleQualitySwitchSmokeRunner runner = CreateQualitySwitchRunner(
            settings,
            operations.Add,
            () => { });

        for (int frame = 0; frame < 130 && !runner.Completed; frame++)
        {
            runner.OnFrameRendered(
                frame,
                RendererDiagnostics.Empty with
                {
                    ActiveQualityPreset = settings.QualityPreset,
                    GpuTimingSupported = 1,
                    GpuTimingEnabled = 1,
                    GpuTimingValid = 0,
                    GpuTimingFrameLatency = 2,
                    GpuTimingUnavailableReason = "synthetic timestamp timeout",
                    MaterialPrimitiveProfileAbsoluteBudgetBytes =
                        RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                            settings.QualityPreset)
                },
                CreateAvailableBudget());
        }

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("fresh GPU timing sample"));
            Assert.That(runner.Failure, Does.Contain("synthetic timestamp timeout"));
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void TextureHotReload_ValidatesRevisionPropagationDescriptorStabilityAndRollback()
    {
        var session = new FakeTextureHotReloadSession();
        var operations = new List<SampleSmokeOperationResult>();
        bool exited = false;
        var runner = new SampleTextureHotReloadSmokeRunner(
            session,
            () => "device-stable",
            operations.Add,
            () => exited = true);

        for (int frame = 0; frame < 8 && !runner.Completed; frame++)
            runner.OnFrameRendered(frame);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Is.Null);
            Assert.That(exited, Is.True);
            Assert.That(session.TextureRevision, Is.EqualTo(3));
            Assert.That(session.MaterialProfileRevision, Is.EqualTo(3));
            Assert.That(session.RestoreCalled, Is.True);
            Assert.That(
                operations.Single(operation => operation.Name == "texture-hot-reload").Status,
                Is.EqualTo("passed"));
        });
    }

    [Test]
    public void TextureHotReload_FailsClosedWhenRenderedCaptureIsRejected()
    {
        var session = new FakeTextureHotReloadSession
        {
            AcceptCaptures = false
        };
        var operations = new List<SampleSmokeOperationResult>();
        var runner = new SampleTextureHotReloadSmokeRunner(
            session,
            () => "device-stable",
            operations.Add,
            () => { });

        runner.OnFrameRendered(0);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain("rejected"));
            Assert.That(session.RestoreCalled, Is.True);
            Assert.That(operations.Single().Status, Is.EqualTo("failed"));
        });
    }

    [Test]
    public void DeterministicLongRunWorkload_MutatesAndRollsBackMaterialAndCamera()
    {
        using var materialManager = new MaterialManager();
        using var scene = new Scene();
        scene.Add(new RenderObject(new object(), materialManager.DefaultMaterialHandle));
        var camera = new FirstPersonCamera(new CoreVector3(1f, 2f, 3f), 0.2f, -0.1f);

        var workload = new SampleDeterministicLongRunWorkload(
            camera,
            scene,
            materialManager,
            mutationIntervalFrames: 2);
        workload.PrepareFrame(0);
        workload.PrepareFrame(1);
        workload.PrepareFrame(2);
        SampleLongRunWorkloadSummary summary = workload.Restore();

        Assert.Multiple(() =>
        {
            Assert.That(summary.PreparedFrameCount, Is.EqualTo(3));
            Assert.That(summary.MaterialMutationCount, Is.EqualTo(2));
            Assert.That(summary.MaterialRollbackSucceeded, Is.True);
            Assert.That(summary.CameraRollbackSucceeded, Is.True);
            Assert.That(camera.Position, Is.EqualTo(new CoreVector3(1f, 2f, 3f)));
        });
    }

    [Test]
    public void LongRunMonitor_WritesBoundedMachineReadableReport()
    {
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            "NjulfTests",
            Guid.NewGuid().ToString("N"),
            "long-run.json");
        using var materialManager = new MaterialManager();
        using var scene = new Scene();
        scene.Add(new RenderObject(new object(), materialManager.DefaultMaterialHandle));
        var camera = new FirstPersonCamera();
        SampleSmokeOptions options = CreateOptions(SampleSmokeMode.LongRun) with
        {
            FrameCount = 3,
            LongRunReportPath = reportPath,
            LongRunWarmupFrames = 0,
            LongRunSampleInterval = 1,
            LongRunMaxRetainedSamples = 2,
            LongRunMemoryGrowthToleranceBytes = 1024UL * 1024UL * 1024UL
        };
        var workload = new SampleDeterministicLongRunWorkload(
            camera,
            scene,
            materialManager,
            mutationIntervalFrames: 1);
        var monitor = new SampleLongRunMonitor(
            options,
            workload,
            () => new DescriptorPressureSnapshot(64, 4, 4, 64, 4, 4, 10),
            () => new string('a', 64));

        for (int frame = 0; frame < 3; frame++)
        {
            monitor.PrepareFrame(frame);
            monitor.PrepareFrame(frame);
            monitor.Sample(
                frame,
                CreateLongRunDiagnostics(),
                CreateAvailableBudget());
        }
        SampleLongRunCompletion completion = monitor.Complete();

        Assert.Multiple(() =>
        {
            Assert.That(completion.Passed, Is.True, completion.Failure);
            Assert.That(File.Exists(reportPath), Is.True);
            Assert.That(completion.Report.TotalSamples, Is.EqualTo(3));
            Assert.That(completion.Report.RetainedSamples, Has.Count.EqualTo(2));
            Assert.That(completion.Report.Workload.PreparedFrameCount, Is.EqualTo(3));
            Assert.That(completion.Report.DeviceLossRecovery.Status, Is.EqualTo("rejected-unsupported"));
            Assert.That(File.ReadAllText(reportPath), Does.Contain("\"SchemaVersion\": 4"));
        });
    }

    [Test]
    public void LongRunMonitor_FailsClosedWhenRequiredTelemetryIsUnavailable()
    {
        string reportPath = Path.Combine(
            Path.GetTempPath(),
            "NjulfTests",
            Guid.NewGuid().ToString("N"),
            "long-run-unavailable.json");
        using var materialManager = new MaterialManager();
        using var scene = new Scene();
        scene.Add(new RenderObject(new object(), materialManager.DefaultMaterialHandle));
        var workload = new SampleDeterministicLongRunWorkload(
            new FirstPersonCamera(),
            scene,
            materialManager,
            mutationIntervalFrames: 1);
        SampleSmokeOptions options = CreateOptions(SampleSmokeMode.LongRun) with
        {
            FrameCount = 3,
            LongRunReportPath = reportPath,
            LongRunWarmupFrames = 0,
            LongRunSampleInterval = 1,
            LongRunMaxRetainedSamples = 3,
            LongRunMemoryGrowthToleranceBytes = ulong.MaxValue
        };
        var monitor = new SampleLongRunMonitor(
            options,
            workload,
            () => new DescriptorPressureSnapshot(64, 4, 4, 64, 4, 4, 10),
            () => new string('a', 64));
        var unavailableGpu = new BudgetMetric(
            "GPU frame",
            0,
            1,
            2,
            "ms",
            RenderBudgetStatus.Unavailable);

        for (int frame = 0; frame < 3; frame++)
        {
            monitor.PrepareFrame(frame);
            monitor.Sample(
                frame,
                CreateLongRunDiagnostics(),
                CreateAvailableBudget(unavailableGpu));
        }

        SampleLongRunCompletion completion = monitor.Complete();

        Assert.Multiple(() =>
        {
            Assert.That(completion.Passed, Is.False);
            Assert.That(completion.Failure, Does.Contain("incomplete required renderer budget telemetry"));
            Assert.That(
                completion.Report.PostWarmupTelemetryCoverageFailureFrameCount,
                Is.EqualTo(3));
            Assert.That(
                completion.Report.TelemetryCoverageFailures,
                Has.Some.Contains("GPU frame"));
            Assert.That(File.Exists(reportPath), Is.True);
        });
    }

    [Test]
    public void LifecycleLongRun_StopsAtConfiguredDuration()
    {
        SampleSmokeOptions options = CreateOptions(SampleSmokeMode.LongRun) with
        {
            FrameCount = 0,
            LongRunMinutes = 0.5
        };
        TimeSpan elapsed = TimeSpan.Zero;
        bool exited = false;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => { },
            () => exited = true,
            elapsed: () => elapsed);

        runner.OnFrameRendered(0);
        elapsed = TimeSpan.FromMinutes(0.5);
        runner.OnFrameRendered(1);

        Assert.Multiple(() =>
        {
            Assert.That(exited, Is.True);
            Assert.That(
                runner.Results.Single(operation => operation.Name == "long-run-duration").Status,
                Is.EqualTo("passed"));
        });
    }

    private static SampleSmokeOptions CreateOptions(SampleSmokeMode mode) => new(
        mode,
        FrameCount: 3,
        SceneReloadCount: 1,
        StartupLogPath: null,
        HealthReportPath: null,
        RendererValidationMode.Off,
        FailOnValidationMessage: false,
        ForceMissingAssets: false,
        PerformanceScenario: SamplePerformanceScenario.Normal,
        EnableGpuTiming: false,
        EnableSceneGpuCompaction: false,
        EnableSceneIndirectDispatch: false,
        EnableSceneGpuLodSelection: false,
        EnableSceneGpuShadowCompaction: false,
        EnableSceneSubmissionValidation: false,
        EnableAsyncCompute: false,
        EnableFarFieldClipmap: false,
        EnableFarFieldForceAll: false,
        BaselineSnapshotDirectory: null);

    private static RenderBudgetSnapshot CreateAvailableBudget(
        BudgetMetric? replacement = null)
    {
        BudgetMetric[] metrics = SampleBudgetMetricCoverage
            .GetRequiredMetricNames(RendererDiagnostics.Empty)
            .Select(name => new BudgetMetric(
                name,
                0,
                1,
                2,
                "count",
                RenderBudgetStatus.WithinBudget))
            .ToArray();
        if (replacement != null)
        {
            int index = Array.FindIndex(
                metrics,
                metric => string.Equals(
                    metric.Name,
                    replacement.Name,
                    StringComparison.Ordinal));
            if (index >= 0)
                metrics[index] = replacement;
            else
                metrics = [.. metrics, replacement];
        }

        return RenderBudgetSnapshot.Empty with
        {
            Metrics = metrics,
            OverallStatus = metrics.Any(
                metric => metric.Status == RenderBudgetStatus.OverBudget)
                ? RenderBudgetStatus.OverBudget
                : RenderBudgetStatus.WithinBudget
        };
    }

    private static RendererDiagnostics CreateLongRunDiagnostics() =>
        RendererDiagnostics.Empty with
        {
            TrackedGpuMemoryBytes = 2048,
            GpuMemoryBudgetBytes = 4096,
            CaptureGpuDeviceName = "Synthetic long-run GPU",
            CaptureGpuDriverVersion = "1.0-test",
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                Commit = "0123456789abcdef0123456789abcdef01234567",
                ShaderBundleHash =
                    "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            }
        };

    private static SampleQualitySwitchSmokeRunner CreateQualitySwitchRunner(
        RenderSettings settings,
        Action<SampleSmokeOperationResult> record,
        Action exit)
    {
        SampleRenderSettingsSnapshot initialSettings =
            SampleRenderSettingsSnapshot.Capture(settings);
        return new SampleQualitySwitchSmokeRunner(
            settings.ApplyQualityPreset,
            () => initialSettings.Restore(settings),
            () => settings.QualityPreset,
            () => SampleRenderSettingsFingerprint.Capture(settings),
            () => "device-stable",
            record,
            exit);
    }

    private sealed class FakeTextureHotReloadSession : ISampleTextureHotReloadSession
    {
        private readonly HashSet<SampleTextureHotReloadCaptureStage> _queued = [];

        public bool AcceptCaptures { get; init; } = true;
        public bool RestoreCalled { get; private set; }
        public uint TextureRevision { get; private set; } = 1;
        public uint MaterialProfileRevision { get; private set; } = 1;
        public int BindlessDescriptorCount => 8;
        public int RenderedGeometryBindingCount => 1;
        public ulong SourceContentHash { get; private set; } = 10;
        public CoreVector3 MeanDiffuseReflectance { get; private set; } =
            new(0.1f, 0.2f, 0.3f);

        public TextureContentReloadResult ReloadReplacement()
        {
            TextureRevision = 2;
            MaterialProfileRevision = 2;
            SourceContentHash = 20;
            MeanDiffuseReflectance = new CoreVector3(0.4f, 0.5f, 0.6f);
            return new TextureContentReloadResult(true, 2, 20, 1);
        }

        public TextureContentReloadResult ReloadOriginal()
        {
            TextureRevision = 3;
            MaterialProfileRevision = 3;
            SourceContentHash = 10;
            MeanDiffuseReflectance = new CoreVector3(0.1f, 0.2f, 0.3f);
            return new TextureContentReloadResult(true, 3, 10, 1);
        }

        public bool QueueCapture(SampleTextureHotReloadCaptureStage stage)
        {
            if (!AcceptCaptures)
                return false;
            return _queued.Add(stage);
        }

        public SampleTextureHotReloadCapture GetCapture(
            SampleTextureHotReloadCaptureStage stage)
        {
            if (!_queued.Contains(stage))
            {
                return new SampleTextureHotReloadCapture(
                    stage,
                    LinearHdrCaptureState.Unknown,
                    string.Empty,
                    0,
                    0,
                    null,
                    string.Empty,
                    "not queued");
            }

            float[] pixels = stage == SampleTextureHotReloadCaptureStage.Replacement
                ? Enumerable.Repeat(0.8f, 12).ToArray()
                : Enumerable.Repeat(0.2f, 12).ToArray();
            return new SampleTextureHotReloadCapture(
                stage,
                LinearHdrCaptureState.Completed,
                $"{stage}.pfm",
                2,
                2,
                pixels,
                new string(stage == SampleTextureHotReloadCaptureStage.Replacement
                    ? 'b'
                    : 'a', 64),
                string.Empty);
        }

        public void Restore()
        {
            RestoreCalled = true;
        }
    }
}
