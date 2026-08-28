using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using System.Text.Json;

namespace Njulf.Tests;

[TestFixture]
public sealed class TailDdgiQualificationTests
{
    [Test]
    public void MathematicalQualification_CoversAllRequiredCasesAndBoundsError()
    {
        SampleTailDdgiMathQualificationReport report =
            SampleTailDdgiMathQualification.Run();
        foreach (SampleTailDdgiMathCaseResult item in report.Cases)
        {
            TestContext.Out.WriteLine(
                $"{item.Name}: J error={item.Jacobi.MeasuredAnalyticError:R} " +
                $"bound={item.Jacobi.ReportedTailBound:R}; A error={item.Accelerated.MeasuredAnalyticError:R} " +
                $"bound={item.Accelerated.ReportedTailBound:R}; passed={item.Passed}");
        }

        string[] expectedNames =
        [
            "q = 0.95 white enclosure",
            "q = 0.99 white enclosure",
            "2-probe chain",
            "20-probe chain",
            "128-probe chain",
            "reflected + transmitted thin sheet",
            "chromatic enclosure"
        ];
        Assert.Multiple(() =>
        {
            Assert.That(report.Cases.Select(static item => item.Name),
                Is.EqualTo(expectedNames));
            Assert.That(report.AccuracyPassed, Is.True);
            Assert.That(report.AccelerationPassed, Is.True);
            Assert.That(report.Passed, Is.True);
            Assert.That(report.SolveEpochReduction,
                Is.GreaterThanOrEqualTo(0.30));
        });

        foreach (SampleTailDdgiMathCaseResult item in report.Cases)
        {
            Assert.Multiple(() =>
            {
                Assert.That(item.Jacobi.MeasuredAnalyticError,
                    Is.LessThanOrEqualTo(
                        Math.BitIncrement(item.Jacobi.ReportedTailBound)),
                    item.Name + " TailJacobi");
                Assert.That(item.Accelerated.MeasuredAnalyticError,
                    Is.LessThanOrEqualTo(
                        Math.BitIncrement(item.Accelerated.ReportedTailBound)),
                    item.Name + " TailAccelerated");
                Assert.That(item.Jacobi.Tolerance,
                    Is.EqualTo(item.Accelerated.Tolerance).Within(0.01),
                    item.Name + " solver tolerance policy");
            });
        }
    }

    [Test]
    public void RuntimeEvidence_RecordsPercentilesCoverageWorkAndMemory()
    {
        RendererDiagnostics[] samples =
        [
            CreateDiagnostics(1_000, 100, 20),
            CreateDiagnostics(2_000, 200, 30),
            CreateDiagnostics(100_000, 300, 40)
        ];
        var observation = new SampleTailDdgiRunObservation(
            ObservedFrameCount: 20,
            ActiveFrameCount: 18,
            SolveEpochCount: 8,
            ConvergenceFrameCount: 12,
            CurrentCertificateFrameCount: 7,
            StaticConvergedWithoutCurrentCertificateCount: 0,
            CachedTransportRayEvaluationCount: 50_000UL,
            CachedSolverIterationCount: 16UL,
            AuditChunkCount: 8UL)
        {
            PrimaryProbeCount = 18UL,
            PrimaryRayCount = 1_800UL,
            RayQueryCount = 1_800UL,
            ShadowRayCount = 3_600UL,
            EstimatedShadowRayUpperBound = 3_600UL
        };

        SampleTailDdgiRuntimeEvidence evidence =
            SampleTailDdgiRuntimeEvidenceBuilder.Create(
                samples,
                observation,
                SampleBenchmarkCaptureVariant.TailAccelerated);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Available, Is.True);
            Assert.That(evidence.GiGpuMilliseconds.Count, Is.EqualTo(3));
            Assert.That(evidence.GiGpuMilliseconds.P50Milliseconds,
                Is.EqualTo(2.0));
            Assert.That(evidence.GiGpuMilliseconds.P95Milliseconds,
                Is.EqualTo(100.0));
            Assert.That(evidence.GiGpuMilliseconds.P99Milliseconds,
                Is.EqualTo(100.0));
            Assert.That(evidence.PrimaryProbeCount, Is.EqualTo(6UL));
            Assert.That(evidence.PrimaryRayCount, Is.EqualTo(600UL));
            Assert.That(evidence.RayQueryCount, Is.EqualTo(600UL));
            Assert.That(evidence.ShadowRayCount, Is.EqualTo(1_200UL));
            Assert.That(evidence.CachedTransportRayEvaluationCount,
                Is.EqualTo(180UL));
            Assert.That(evidence.CachedSolverIterationCount, Is.EqualTo(6UL));
            Assert.That(evidence.AuditChunkCount, Is.EqualTo(3UL));
            Assert.That(evidence.RunCachedTransportRayEvaluationCount,
                Is.EqualTo(50_000UL));
            Assert.That(evidence.RunCachedSolverIterationCount, Is.EqualTo(16UL));
            Assert.That(evidence.RunAuditChunkCount, Is.EqualTo(8UL));
            Assert.That(evidence.RunPrimaryProbeCount, Is.EqualTo(18UL));
            Assert.That(evidence.RunPrimaryRayCount, Is.EqualTo(1_800UL));
            Assert.That(evidence.RunRayQueryCount, Is.EqualTo(1_800UL));
            Assert.That(evidence.RunShadowRayCount, Is.EqualTo(3_600UL));
            Assert.That(evidence.ExpectedParticipantCount,
                Is.EqualTo(evidence.AuditedParticipantCount));
            Assert.That(evidence.ExpectedTexelCount,
                Is.EqualTo(evidence.AuditedTexelCount));
            Assert.That(evidence.ReceiverProbeBytes, Is.EqualTo(16_384UL));
            Assert.That(evidence.SchedulerAuditReadbackBytes, Is.EqualTo(3_072UL));
        });
    }

    [Test]
    public void TailCertificateAcceptance_AllowsSparseDomainExclusionsButRejectsInvalidEvidence()
    {
        RendererDiagnostics sparseCertified =
            CreateDiagnostics(1_000, 128, 256) with
            {
                SimpleDdgiTransportConvergence =
                CreateTailTelemetry(certificateCurrent: true) with
                {
                    TailExcludedNotVisibleCount = 9_376u
                }
            };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkRunner.HasAcceptedCurrentSimpleDdgiTailCertificate(
                    sparseCertified),
                Is.True,
                "Nonresident virtual probes are outside the frozen sparse participant domain.");
            Assert.That(
                SampleBenchmarkRunner.HasAcceptedCurrentSimpleDdgiTailCertificate(
                    sparseCertified with
                    {
                        SimpleDdgiTransportConvergence =
                        sparseCertified.SimpleDdgiTransportConvergence with
                        {
                            TailExcludedStaleSourceCount = 1u
                        }
                    }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.HasAcceptedCurrentSimpleDdgiTailCertificate(
                    sparseCertified with
                    {
                        SimpleDdgiTransportConvergence =
                        sparseCertified.SimpleDdgiTransportConvergence with
                        {
                            TailCacheIdentityFailureCount = 1u
                        }
                    }),
                Is.False);
        });
    }

    [Test]
    public void RunObserver_RejectsStaticConvergedWithoutCurrentCertificate()
    {
        var observer = new SampleTailDdgiRunObserver();
        RendererDiagnostics diagnostics = CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiTrackingState = SimpleDdgiTrackingState.StaticConverged,
            SimpleDdgiTransportConvergence =
            CreateTailTelemetry(certificateCurrent: false)
        };

        observer.Observe(diagnostics);
        SampleTailDdgiRunObservation snapshot = observer.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ActiveFrameCount, Is.EqualTo(1));
            Assert.That(snapshot.StaticConvergedWithoutCurrentCertificateCount,
                Is.EqualTo(1));
            Assert.That(snapshot.ConvergenceFrameCount, Is.Zero);
        });
    }

    [Test]
    public void RunObserver_RecordsExactResidentSourceAndSolveProgress()
    {
        var observer = new SampleTailDdgiRunObserver();
        RendererDiagnostics baseline = CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiSchedulerFeedbackValid = 1,
            SimpleDdgiSchedulerFeedbackPendingSourceCount = 4u,
            SimpleDdgiSchedulerFeedbackSolveParticipantCount = 100u
        };

        observer.Observe(baseline);
        observer.Observe(baseline with
        {
            SimpleDdgiSchedulerFeedbackPendingSourceCount = 0u,
            SimpleDdgiSchedulerFeedbackSolveEpoch = 7u,
            SimpleDdgiSchedulerFeedbackSolveVisitedCount = 40u
        });
        observer.Observe(baseline with
        {
            SimpleDdgiSchedulerFeedbackPendingSourceCount = 0u,
            SimpleDdgiSchedulerFeedbackSolveEpoch = 7u,
            SimpleDdgiSchedulerFeedbackSolveVisitedCount = 100u
        });

        SampleTailDdgiRunObservation snapshot = observer.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FirstSourceReadyFrameCount, Is.EqualTo(2));
            Assert.That(snapshot.FirstSolveEpochFrameCount, Is.EqualTo(2));
            Assert.That(snapshot.MaximumSolveParticipantCount, Is.EqualTo(100u));
            Assert.That(snapshot.MaximumSolveVisitedCount, Is.EqualTo(100u));
        });
    }

    [Test]
    public void RunObserver_CountsSolveEpochOnceAcrossMutableGenerationUpdates()
    {
        var observer = new SampleTailDdgiRunObserver();
        SimpleDdgiTransportConvergenceTelemetry first =
            CreateTailTelemetry(certificateCurrent: false) with
            {
                TailSolveEpoch = 4u,
                TailGenerations = new SimpleDdgiTransportGenerations(
                    1u, 2u, 3u, 4u, 5u, 6u, 4u, 1u, 7u, 8u)
            };
        SimpleDdgiTransportConvergenceTelemetry sameEpochNewCanonical = first with
        {
            TailGenerations = first.TailGenerations with
            {
                CanonicalField = 9u,
                Queue = 10u
            }
        };
        SimpleDdgiTransportConvergenceTelemetry nextEpoch =
            sameEpochNewCanonical with { TailSolveEpoch = 5u };

        observer.Observe(CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiTransportConvergence = first
        });
        observer.Observe(CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiTransportConvergence = sameEpochNewCanonical
        });
        observer.Observe(CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiTransportConvergence = nextEpoch
        });

        Assert.That(observer.Snapshot().SolveEpochCount, Is.EqualTo(2));
    }

    [Test]
    public void RunObserver_ConvergenceFramesStartAtFirstSolveEpoch()
    {
        var observer = new SampleTailDdgiRunObserver();
        RendererDiagnostics sourceRepair = CreateDiagnostics(1_000, 128, 256) with
        {
            SimpleDdgiTrackingState = SimpleDdgiTrackingState.TrackingSourceCohort,
            SimpleDdgiSchedulerFeedbackValid = 1,
            SimpleDdgiSchedulerFeedbackPendingSourceCount = 2u,
            SimpleDdgiSchedulerFeedbackSolveEpoch = 0u,
            SimpleDdgiTransportConvergence =
            CreateTailTelemetry(certificateCurrent: false) with
            {
                TailSolveEpoch = 0u
            }
        };
        RendererDiagnostics solving = sourceRepair with
        {
            SimpleDdgiTrackingState = SimpleDdgiTrackingState.StaticConverging,
            SimpleDdgiSchedulerFeedbackPendingSourceCount = 0u,
            SimpleDdgiSchedulerFeedbackSolveEpoch = 1u,
            SimpleDdgiTransportConvergence =
            CreateTailTelemetry(certificateCurrent: false) with
            {
                TailSolveEpoch = 1u
            }
        };

        observer.Observe(sourceRepair);
        observer.Observe(sourceRepair);
        observer.Observe(solving);
        observer.Observe(solving);
        observer.Observe(solving with
        {
            SimpleDdgiTrackingState = SimpleDdgiTrackingState.StaticConverged,
            SimpleDdgiTransportConvergence =
            solving.SimpleDdgiTransportConvergence with
            {
                TailCertificateCurrent = true
            }
        });

        SampleTailDdgiRunObservation observation = observer.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(observation.FirstSolveEpochFrameCount, Is.EqualTo(3));
            Assert.That(observation.ConvergenceFrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void Qualification_MissingProductionEvidenceFailsClosed()
    {
        var soak = new SampleTailDdgiLongSoakEvidence(
            Path: "missing.json",
            Status: "failed",
            Failure: "missing",
            BuildConfiguration: string.Empty,
            ElapsedSeconds: 0,
            RequestedFrameCount: 0,
            RequestedMinutes: 0,
            ExpectedSampleCount: 0,
            TotalSamples: 0,
            ManagedMemoryStable: false,
            GpuMemoryStable: false,
            BudgetViolationFrameCount: 1,
            TelemetryCoverageFailureFrameCount: 1,
            TextureExhaustionSampleCount: 1,
            SamplerExhaustionSampleCount: 1,
            ProducerIdentity: new MaterialGiProducerIdentity());
        var input = new SampleTailDdgiQualificationInput(
            Array.Empty<SampleTailDdgiQualificationReportArtifact>(),
            Array.Empty<SampleTailDdgiQualificationReportArtifact>(),
            Array.Empty<SampleTailDdgiQualificationReportArtifact>(),
            soak);

        SampleTailDdgiQualificationReport report =
            SampleTailDdgiQualificationEvaluator.Evaluate(input);

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.False);
            Assert.That(report.Mathematics.Passed, Is.True);
            Assert.That(report.Failures.Select(static item => item.Name),
                Does.Contain("three-tail-jacobi-repetitions"));
            Assert.That(report.Failures.Select(static item => item.Name),
                Does.Contain("required-runtime-scenarios"));
            Assert.That(report.Failures.Select(static item => item.Name),
                Does.Contain("long-soak-stability"));
        });
    }

    [Test]
    public void Qualification_CompleteSyntheticProductionBundlePasses()
    {
        SampleTailDdgiQualificationReportArtifact[] jacobi = Enumerable.Range(1, 3)
            .Select(index => new SampleTailDdgiQualificationReportArtifact(
                $"tail-jacobi-{index}",
                $"jacobi-{index}.json",
                CreateQualificationReport(
                    SampleBenchmarkCaptureVariant.TailJacobi,
                    acceleration: false,
                    SamplePerformanceScenario.GiSimpleDdgiFurnace,
                    solveEpochs: 10,
                    convergenceFrames: 100)))
            .ToArray();
        SampleTailDdgiQualificationReportArtifact[] accelerated = Enumerable.Range(1, 3)
            .Select(index => new SampleTailDdgiQualificationReportArtifact(
                $"tail-accelerated-{index}",
                $"accelerated-{index}.json",
                CreateQualificationReport(
                    SampleBenchmarkCaptureVariant.TailAccelerated,
                    acceleration: true,
                    SamplePerformanceScenario.GiSimpleDdgiFurnace,
                    solveEpochs: 6,
                    convergenceFrames: 60)))
            .ToArray();
        // Runtime percentile jitter is recorded, while every repetition is
        // independently held to its production timing budget. It must not
        // invalidate an otherwise exact producer/render-state identity.
        accelerated[2] = accelerated[2] with
        {
            Report = accelerated[2].Report with
            {
                CpuFrameMilliseconds = CompleteTiming("timing", 2.0)
            }
        };
        (string Role, SamplePerformanceScenario Scenario)[] scenarioSpecs =
        [
            ("scroll", SamplePerformanceScenario.GiLocalVolumeStreaming),
            ("teleport", SamplePerformanceScenario.GiFastTraversalTeleport),
            ("source-change", SamplePerformanceScenario.GiMovingPointLight),
            ("relocation", SamplePerformanceScenario.GiMovingRigidObject),
            ("high-albedo", SamplePerformanceScenario.GiSimpleDdgiFurnace),
            ("thin-wall", SamplePerformanceScenario.GiThinWallLeakTest)
        ];
        SampleTailDdgiQualificationReportArtifact[] scenarios = scenarioSpecs
            .Select(item => new SampleTailDdgiQualificationReportArtifact(
                item.Role,
                item.Role + ".json",
                CreateQualificationReport(
                    SampleBenchmarkCaptureVariant.TailAccelerated,
                    acceleration: true,
                    item.Scenario,
                    solveEpochs: 6,
                    convergenceFrames: 60)))
            .ToArray();
        MaterialGiProducerIdentity producer = CreateProducerIdentity();
        var soak = new SampleTailDdgiLongSoakEvidence(
            Path: "long-soak.json",
            Status: "passed",
            Failure: null,
            BuildConfiguration: "ShippingPerformance",
            ElapsedSeconds: 60.0,
            RequestedFrameCount: 3_600,
            RequestedMinutes: 0.0,
            ExpectedSampleCount: 232,
            TotalSamples: 232,
            ManagedMemoryStable: true,
            GpuMemoryStable: true,
            BudgetViolationFrameCount: 0,
            TelemetryCoverageFailureFrameCount: 0,
            TextureExhaustionSampleCount: 0,
            SamplerExhaustionSampleCount: 0,
            ProducerIdentity: producer)
        {
            QualificationProfile = SampleTailDdgiLongSoakProfile.Name,
            GiGpuMetricSource =
                SampleTailDdgiLongSoakProfile.GiGpuMetricSource,
            CaptureRenderWidth = 1920,
            CaptureRenderHeight = 1080,
            TimingGatesPassed = true
        };

        SampleTailDdgiQualificationReport result =
            SampleTailDdgiQualificationEvaluator.Evaluate(
                new SampleTailDdgiQualificationInput(
                    jacobi,
                    accelerated,
                    scenarios,
                    soak));

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True,
                string.Join(Environment.NewLine,
                    result.Failures.Select(static failure => failure.Name + ": " + failure.Detail)));
            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.RuntimeSolveEpochReduction,
                Is.EqualTo(0.40).Within(1e-9));
            Assert.That(result.RuntimeConvergenceFrameReduction,
                Is.EqualTo(0.40).Within(1e-9));
            Assert.That(result.ScenarioRuns, Has.Count.EqualTo(6));
        });
    }

    [Test]
    public void CaptureVariants_ConfigureJacobiAndAcceleratedTailModes()
    {
        var settings = new RenderSettings();

        string jacobi = SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.TailJacobi);
        Assert.Multiple(() =>
        {
            Assert.That(jacobi, Is.EqualTo("tail-jacobi"));
            Assert.That(settings.GlobalIllumination.SimpleDdgiSchedulerMode,
                Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportV2Enabled,
                Is.True);
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled,
                Is.True);
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled,
                Is.False);
        });

        string accelerated = SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.TailAccelerated);
        Assert.Multiple(() =>
        {
            Assert.That(accelerated, Is.EqualTo("tail-accelerated"));
            Assert.That(settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled,
                Is.True);
        });
    }

    [Test]
    public void TailLongSoakProfile_MatchesAcceleratedBenchmarkRenderSettings()
    {
        var benchmark = new RenderSettings();
        benchmark.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        benchmark.PerformanceBudgets.ActiveProfile =
            RenderBudgetProfileKind.HighSpec1440p60;
        benchmark.GlobalIllumination.DdgiAdaptiveBudgetingEnabled = false;
        benchmark.Particles.FixedSimulationDeltaSeconds =
            HelloGame.BenchmarkSimulationDeltaSeconds;
        SampleBenchmarkCaptureVariant.Apply(
            benchmark,
            SampleBenchmarkCaptureVariant.TailAccelerated);

        var soak = new RenderSettings();
        soak.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        SampleTailDdgiLongSoakProfile.Apply(soak);

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleRenderSettingsFingerprint.Capture(soak),
                Is.EqualTo(
                    SampleRenderSettingsFingerprint.Capture(benchmark)));
            Assert.That(
                soak.PerformanceBudgets.ActiveProfile,
                Is.EqualTo(RenderBudgetProfileKind.HighSpec1440p60));
            Assert.That(
                soak.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled,
                Is.True);
            Assert.That(
                soak.GlobalIllumination.DdgiAdaptiveBudgetingEnabled,
                Is.False);
        });
    }

    [Test]
    public void TailLongSoakBudget_UsesExactDdgiTimingAndSeparatesMaterialStress()
    {
        BudgetMetric[] metrics =
        [
            new BudgetMetric(
                "GI GPU",
                0.0,
                2.55,
                3.0,
                "ms",
                RenderBudgetStatus.Unavailable),
            new BudgetMetric(
                RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
                0.9,
                0.2125,
                0.25,
                "ms",
                RenderBudgetStatus.OverBudget),
            new BudgetMetric(
                "GPU frame",
                7.0,
                10.2,
                12.0,
                "ms",
                RenderBudgetStatus.WithinBudget)
        ];
        RenderBudgetSnapshot budget = RenderBudgetSnapshot.Empty with
        {
            Profile = RenderBudgetProfile.HighSpec1440p60,
            Metrics = metrics,
            OverallStatus = RenderBudgetStatus.OverBudget
        };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            GpuDdgiUpdateMicroseconds = 1_250,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportTailCertificationEnabled = true,
            SimpleDdgiTransportAccelerationEnabled = true,
            MaterialCompileTimingSampleCount = 12,
            MaterialUploadTimingSampleCount = 8
        };

        SampleTailDdgiLongSoakBudgetProjection projection =
            SampleTailDdgiLongSoakProfile.ProjectBudget(
                budget,
                diagnostics);
        BudgetMetric giGpu = projection.Budget.Metrics.Single(static metric => metric.Name == "GI GPU");
        BudgetMetric materialCompile = projection.Budget.Metrics.Single(static metric =>
            metric.Name ==
            RenderBudgetEvaluator.MaterialGiCompileP95MetricName);

        Assert.Multiple(() =>
        {
            Assert.That(giGpu.Value, Is.EqualTo(1.25));
            Assert.That(
                giGpu.Status,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(
                materialCompile.Status,
                Is.EqualTo(RenderBudgetStatus.Unavailable));
            Assert.That(
                projection.Budget.OverallStatus,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(
                projection.CoverageDiagnostics.MaterialCompileTimingSampleCount,
                Is.Zero);
            Assert.That(
                projection.CoverageDiagnostics.MaterialUploadTimingSampleCount,
                Is.Zero);
        });
    }

    [Test]
    public void ProductionDefaults_EnableCertifiedJacobiPath()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;

        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiSchedulerMode,
                Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(gi.SimpleDdgiTransportV2Enabled, Is.True);
            Assert.That(gi.SimpleDdgiTransportTailCertificationEnabled, Is.True);
            Assert.That(gi.SimpleDdgiTransportAccelerationEnabled, Is.False);
            Assert.That(gi.SimpleDdgiTransportAcceleratedSweepCount,
                Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public void QualificationCli_MissingManifestFailsAsInputError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        bool handled = SampleTailDdgiQualificationCli.TryRun(
            ["--tail-ddgi-qualification", "definitely-missing-tail-manifest.json"],
            output,
            error,
            out int exitCode);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.EqualTo(64));
            Assert.That(error.ToString(), Does.Contain("qualification command failed"));
        });
    }

    [Test]
    public void QualificationJsonContract_ReadsNamedLongRunBudgetStatus()
    {
        RenderBudgetStatus status = JsonSerializer.Deserialize<RenderBudgetStatus>(
            "\"OverBudget\"",
            SampleTailDdgiQualificationCli.CreateJsonOptions());

        Assert.That(status, Is.EqualTo(RenderBudgetStatus.OverBudget));
    }

    private static RendererDiagnostics CreateDiagnostics(
        long giMicroseconds,
        ulong sourceRays,
        ulong cachedTransportRays) => RendererDiagnostics.Empty with
    {
        SimpleDdgiActive = 1,
        SimpleDdgiTransportV2Active = 1,
        GpuTimingValid = 1,
        GpuDdgiUpdateMicroseconds = giMicroseconds,
        GpuSimpleDdgiAcceleratedSolveMicroseconds = giMicroseconds / 4,
        GpuSimpleDdgiTransportAuditMicroseconds = giMicroseconds / 10,
        SimpleDdgiTransportSourceRefreshProbeCount = 2,
        SimpleDdgiTransportSourceRayCount = sourceRays,
        SimpleDdgiTransportSolveRayCount = checked(
            sourceRays + cachedTransportRays),
        SimpleDdgiTransportCachedSweepCount = 2,
        SimpleDdgiTransportAuditChunkCount = 1,
        DdgiTraceRayCount = checked((uint)sourceRays),
        DdgiVisibilityRayCount = sourceRays * 2UL,
        DdgiEstimatedShadowRayUpperBound = sourceRays * 2UL,
        SimpleDdgiTrackingState = SimpleDdgiTrackingState.StaticConverged,
        SimpleDdgiSchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiTransportTailCertificationEnabled = true,
        SimpleDdgiTransportAccelerationEnabled = true,
        SimpleDdgiTransportConvergence = CreateTailTelemetry(certificateCurrent: true),
        TrackedGpuMemoryBytes = 100_000_000UL,
        GpuMemoryBudgetBytes = 200_000_000UL,
        DdgiTextureBytes = 4_096UL,
        DdgiBufferBytes = 8_192UL,
        SimpleDdgiSchedulerArenaBytes = 32_768UL,
        SimpleDdgiSchedulerFeedbackReadbackBytes = 1_536UL,
        SimpleDdgiSchedulerAuditReadbackBytes = 3_072UL,
        SimpleDdgiReceiverProbeBytes = 16_384UL,
        SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
        {
            CapacityDetails = new SimpleDdgiCapacityTiming
            {
                ReceiverProbes = new SimpleDdgiCapacityResourceTelemetry(
                    PreviousBytes: 0UL,
                    RequiredBytes: 0UL,
                    Transitioned: false)
            }
        }
    };

    private static SimpleDdgiTransportConvergenceTelemetry CreateTailTelemetry(
        bool certificateCurrent) =>
        SimpleDdgiTransportConvergenceTelemetry.Empty with
        {
            TailSolveEpoch = 4u,
            TailExpectedParticipantCount = 2u,
            TailAuditedParticipantCount = 2u,
            TailExcludedInactiveCount = 1u,
            TailExcludedNotVisibleCount = 0u,
            TailExpectedTexelCount = 64u,
            TailAuditedTexelCount = 64u,
            TailAbsoluteBound = 0.005f,
            TailTolerance = 0.01f,
            TailAuditComplete = true,
            TailCertificateCurrent = certificateCurrent
        };

    private static SampleBenchmarkReport CreateQualificationReport(
        string variant,
        bool acceleration,
        SamplePerformanceScenario scenario,
        int solveEpochs,
        int convergenceFrames)
    {
        string accelerationValue = acceleration ? "1" : "0";
        string settingsHash =
            (acceleration ? "settings-accelerated" : "settings-jacobi") +
            "-" + scenario;
        var run = new PerformanceCaptureRunMetadata(
            SceneKind: "GlobalIlluminationTest",
            Scenario: scenario.ToString(),
            BuildConfiguration: "ShippingPerformance",
            ApplicationVersion: "1.0",
            Commit: "commit",
            ShaderBundleHash: "shader",
            SettingsSchemaVersion: 1)
        {
            ExecutableHash = "executable",
            DirtyWorktreeState = "clean"
        };
        RendererDiagnostics diagnostics = CreateDiagnostics(1_000, 128, 256) with
        {
            CaptureGpuDeviceName = "qualification-gpu",
            CaptureGpuDriverVersion = "qualification-driver",
            CaptureRenderWidth = 1920,
            CaptureRenderHeight = 1080,
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            CaptureSceneAssetHash = "scene-asset",
            CaptureSceneContentRevision = 1UL,
            CaptureSceneStateHash = "scene-state",
            CaptureCamera = PerformanceCaptureCameraMetadata.Unknown with
            {
                ViewHash = "view",
                ProjectionHash = "projection"
            },
            CaptureRun = run,
            CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
            {
                DdgiCacheGeneration = 1u
            },
            ResolvedGiSettings = new ResolvedGiSettingsMetadata(
                settingsHash,
                settingsHash,
                [
                    "gi.simpleDdgi.transport.accelerationEnabled=" +
                    accelerationValue,
                    "gi.simpleDdgi.transport.tailCertificationEnabled=1",
                    "gi.simpleDdgi.transport.tailRelativeTolerance=0.001"
                ]),
            SimpleDdgiTransportAccelerationEnabled = acceleration,
            SimpleDdgiTransportTailRelativeTolerance = 0.001f
        };
        SampleBenchmarkTimingStats timing = CompleteTiming("timing", 1.0);
        const SampleBenchmarkTrajectoryKind trajectory =
            SampleBenchmarkTrajectoryKind.Stationary;
        const SampleBistroQualityCaptureVariant bistroVariant =
            SampleBistroQualityCaptureVariant.SunScaleStep;
        string trajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                trajectory,
                bistroVariant);
        var evidence = new SampleTailDdgiRuntimeEvidence
        {
            Available = true,
            Variant = variant,
            GiGpuMilliseconds = CompleteTiming("GI GPU", 1.0),
            AcceleratedSolveGpuMilliseconds =
                CompleteTiming("Simple DDGI accelerated solve GPU", acceleration ? 0.4 : 0.0),
            AuditGpuMilliseconds = CompleteTiming("Simple DDGI audit GPU", 0.1),
            PrimaryProbeCount = 240UL,
            PrimaryRayCount = 30_720UL,
            RayQueryCount = 30_720UL,
            ShadowRayCount = 61_440UL,
            EstimatedShadowRayUpperBound = 61_440UL,
            CachedTransportRayEvaluationCount = 100_000UL,
            CachedSolverIterationCount = acceleration ? 12UL : 6UL,
            AuditChunkCount = 6UL,
            RunCachedTransportRayEvaluationCount = 1_000_000UL,
            RunCachedSolverIterationCount = acceleration ? 120UL : 60UL,
            RunAuditChunkCount = 20UL,
            RunPrimaryProbeCount = 240UL,
            RunPrimaryRayCount = 30_720UL,
            RunRayQueryCount = 30_720UL,
            RunShadowRayCount = 61_440UL,
            RunEstimatedShadowRayUpperBound = 61_440UL,
            RunObservedFrameCount = 300,
            RunActiveFrameCount = 300,
            SolveEpochCount = solveEpochs,
            ConvergenceFrameCount = convergenceFrames,
            CurrentCertificateFrameCount = 120,
            StaticConvergedWithoutCurrentCertificateCount = 0,
            FinalTrackingState = SimpleDdgiTrackingState.StaticConverged,
            SchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            TailCertificationEnabled = true,
            AccelerationEnabled = acceleration,
            ExpectedParticipantCount = 128u,
            AuditedParticipantCount = 128u,
            ExcludedInactiveCount = 4u,
            ExcludedNotVisibleCount = 9_376u,
            ExpectedTexelCount = 8_192u,
            AuditedTexelCount = 8_192u,
            FinalTailBound = 0.005f,
            FinalTailTolerance = 0.01f,
            FinalAuditComplete = true,
            FinalCertificateCurrent = true,
            TrackedGpuMemoryBytes = 100_000_000UL,
            GpuMemoryBudgetBytes = 200_000_000UL,
            DdgiTextureBytes = 8_192UL,
            DdgiBufferBytes = 16_384UL,
            ReceiverProbeBytes = 2_048UL,
            SchedulerArenaBytes = 65_536UL,
            SchedulerFeedbackReadbackBytes = 1_536UL,
            SchedulerAuditReadbackBytes = 3_072UL
        };
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 30,
            MeasureFrameCount: 120,
            ReportPath: null)
        {
            CapturePairId = "tail-ddgi-production-01",
            CaptureVariant = variant,
            Trajectory = trajectory,
            TrajectoryBistroVariant = bistroVariant,
            TrajectoryFingerprint = trajectoryFingerprint,
            RequireProductionTiming = true,
            HdrReferencePath = "reference.hdr",
            HdrCandidatePath = variant + ".hdr"
        };
        var report = new SampleBenchmarkReport(
            Kind: "njulf-renderer-benchmark",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Options: options,
            Scenario: scenario,
            WarmupFrameCount: 30,
            MeasurementFrameCount: 120,
            FirstMeasurementFrameIndex: 30,
            LastMeasurementFrameIndex: 149,
            CpuFrameMilliseconds: timing,
            GpuFrameMilliseconds: timing,
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: 120,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: Array.Empty<SampleBenchmarkTimingStats>(),
            CpuStages: Array.Empty<SampleBenchmarkTimingStats>(),
            Findings: Array.Empty<SampleBenchmarkFinding>(),
            BudgetMetrics: Array.Empty<BudgetMetric>(),
            LastDiagnostics: diagnostics)
        {
            CaptureContract = new SampleBenchmarkCaptureContract(
                Comparable: true,
                ProductionTiming: true,
                PairId: "tail-ddgi-production-01",
                Variant: variant,
                IdentityHash: "identity-" + variant,
                Mismatches: Array.Empty<string>())
            {
                FullIdentityHash = "full-identity-" + variant,
                Trajectory = SampleBenchmarkTrajectory.GetName(trajectory),
                TrajectoryFingerprint = trajectoryFingerprint,
                TrajectoryFrameCount =
                    SampleBenchmarkTrajectory.GetFrameCount(trajectory),
                TrajectoryRouteHash =
                    SampleBenchmarkTrajectory.CreateRouteHash(
                        trajectory,
                        bistroVariant,
                        diagnostics.CaptureCamera),
                TrajectorySequenceHash = Sha256('1')
            },
            DdgiProductionGate = new SampleDdgiProductionGateReport(
                Passed: true,
                Criteria: Array.Empty<SampleDdgiProductionGateCriterion>()),
            HdrDifference = new SampleBenchmarkHdrDifference(
                Available: true,
                Passed: true,
                ReferencePath: "reference.hdr",
                CandidatePath: variant + ".hdr",
                ReferenceSha256: "reference",
                CandidateSha256: "candidate",
                Width: 1920,
                Height: 1080,
                Rmse: 0.001,
                RelativeRmse: 0.001,
                MeanAbsoluteError: 0.001,
                MaximumAbsoluteError: 0.01,
                MaximumRelativeRmse: 0.12,
                FailureReason: string.Empty),
            ProducerIdentity = CreateProducerIdentity(),
            TailDdgiEvidence = evidence,
            ActivationEvidence = CanonicalNoActivationEvidence(120)
        };
        return report;
    }

    private static SampleBenchmarkTimingStats CompleteTiming(
        string name,
        double milliseconds) =>
        new(
            name,
            Count: 120,
            AverageMilliseconds: milliseconds,
            MinMilliseconds: milliseconds,
            MaxMilliseconds: milliseconds,
            P95Milliseconds: milliseconds)
        {
            MedianMilliseconds = milliseconds,
            P50Milliseconds = milliseconds,
            P99Milliseconds = milliseconds
        };

    private static SampleBenchmarkActivationEvidence
        CanonicalNoActivationEvidence(int sampleCount) => new(
        SampleBenchmarkActivationEvidence.CurrentSchema,
        SampleBenchmarkActivation.None,
        SampleBenchmarkActivation.CreateFingerprint(
            SampleBenchmarkActivation.None),
        Passed: true,
        MeasuredSampleCount: sampleCount,
        Failures: Array.Empty<string>());

    private static string Sha256(char digit) =>
        "sha256:" + new string(digit, 64);

    private static MaterialGiProducerIdentity CreateProducerIdentity() => new()
    {
        BuildCommit = "commit",
        ShaderFingerprint = "shader",
        SettingsFingerprint = "settings-accelerated",
        GpuName = "qualification-gpu",
        DriverVersion = "qualification-driver",
        QualityTier = "DdgiHigh"
    };
}
