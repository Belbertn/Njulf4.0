using System.Linq;
using System.Text.Json;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkAnalyzerTests
{
    [Test]
    public void BuildStats_ClampsFloatingPointMeanToObservedRange()
    {
        double[] samples = Enumerable.Repeat(0.001, 240).ToArray();

        SampleBenchmarkTimingStats stats = SampleBenchmarkAnalyzer.BuildStats(
            "constant timer",
            samples);

        Assert.Multiple(() =>
        {
            Assert.That(stats.Count, Is.EqualTo(samples.Length));
            Assert.That(stats.MinMilliseconds, Is.EqualTo(0.001));
            Assert.That(stats.AverageMilliseconds, Is.EqualTo(0.001));
            Assert.That(stats.MaxMilliseconds, Is.EqualTo(0.001));
            Assert.That(stats.P50Milliseconds, Is.EqualTo(0.001));
            Assert.That(stats.P95Milliseconds, Is.EqualTo(0.001));
            Assert.That(stats.P99Milliseconds, Is.EqualTo(0.001));
        });
    }

    [Test]
    public void ProducerIdentity_FreezesSettingsBeforePostMeasurementHdrMutation()
    {
        string reportPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"benchmark-settings-identity-{Guid.NewGuid():N}.json");
        const string measuredFingerprint =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string postMeasurementFingerprint =
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string currentFingerprint = measuredFingerprint;
        bool exited = false;
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: 1,
            ReportPath: reportPath)
        {
            HdrReferencePath = "post-measurement-reference.pfm",
            MaximumAdditionalSettlingFrameCount = 0
        };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                Commit = "0123456789abcdef0123456789abcdef01234567",
                ShaderBundleHash =
                    "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            },
            CaptureGpuDeviceName = "Synthetic benchmark GPU",
            CaptureGpuDriverVersion = "1.0-test"
        };

        try
        {
            var runner = new SampleBenchmarkRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => exited = true,
                () => currentFingerprint,
                _ =>
                {
                    currentFingerprint = postMeasurementFingerprint;
                    return false;
                },
                _ => new LinearHdrCaptureResult(
                    string.Empty,
                    LinearHdrCaptureState.Unknown,
                    string.Empty));

            runner.OnFrameRendered(
                frameIndex: 0,
                diagnostics,
                RenderBudgetSnapshot.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(exited, Is.True);
                Assert.That(currentFingerprint, Is.EqualTo(postMeasurementFingerprint));
                Assert.That(
                    runner.Report?.ProducerIdentity?.SettingsFingerprint,
                    Is.EqualTo(measuredFingerprint["sha256:".Length..]));
            });
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [Test]
    public void ProgressiveBootstrapFrames_DoNotConsumeBenchmarkSettlingWindow()
    {
        string reportPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"benchmark-progressive-startup-{Guid.NewGuid():N}.json");
        const string settingsFingerprint =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        bool exited = false;
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: 1,
            ReportPath: reportPath)
        {
            MaximumAdditionalSettlingFrameCount = 29
        };
        RendererDiagnostics production = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000,
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                Commit = "0123456789abcdef0123456789abcdef01234567",
                ShaderBundleHash =
                    "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            },
            CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
            {
                WarmupState = DdgiRuntimeWarmupState.SteadyState,
                TransportConvergencePending = false
            },
            CaptureGpuDeviceName = "Synthetic benchmark GPU",
            CaptureGpuDriverVersion = "1.0-test"
        };

        try
        {
            var runner = new SampleBenchmarkRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => exited = true,
                () => settingsFingerprint);

            for (int frame = 0; frame < 100; frame++)
            {
                runner.OnFrameRendered(
                    frame,
                    RendererDiagnostics.Empty,
                    RenderBudgetSnapshot.Empty);
            }

            Assert.That(exited, Is.False);
            for (int frame = 100; frame < 130; frame++)
            {
                runner.OnFrameRendered(
                    frame,
                    production,
                    RenderBudgetSnapshot.Empty);
            }
            SampleBenchmarkReport report = runner.Report ??
                throw new AssertionException(
                    "The production diagnostic did not complete the benchmark.");
            MaterialGiProducerIdentity producer = report.ProducerIdentity ??
                throw new AssertionException(
                    "The benchmark did not publish a producer identity.");

            Assert.Multiple(() =>
            {
                Assert.That(exited, Is.True);
                Assert.That(
                    report.AdditionalSettlingFrameCount,
                    Is.EqualTo(29));
                Assert.That(report.SettlingWaitTimedOut, Is.False);
                Assert.That(
                    producer.BuildCommit,
                    Is.EqualTo(production.CaptureRun.Commit));
            });
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [Test]
    public void ProductionTiming_WaitsForRequestedReceiverCachePublication()
    {
        string reportPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"benchmark-deferred-receiver-bank-{Guid.NewGuid():N}.json");
        const string settingsFingerprint =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        bool exited = false;
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: 1,
            ReportPath: reportPath)
        {
            RequireProductionTiming = true,
            CapturePairId = "deferred-receiver-bank-test",
            MaximumAdditionalSettlingFrameCount = 29
        };
        RendererDiagnostics exactFallback = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000,
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                Commit = "0123456789abcdef0123456789abcdef01234567",
                ShaderBundleHash =
                    "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            },
            CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
            {
                WarmupState = DdgiRuntimeWarmupState.SteadyState,
                TransportConvergencePending = false
            },
            CaptureGpuDeviceName = "Synthetic benchmark GPU",
            CaptureGpuDriverVersion = "1.0-test",
            SimpleDdgiReceiverCache = SimpleDdgiReceiverCacheDiagnostics.Exact(
                SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                SimpleDdgiReceiverCacheFallbackReason.DispatchUnavailable,
                "receiver bank is still compiling"),
            ForwardGiExactGatherUsed = 1
        };
        RendererDiagnostics cacheActive = exactFallback with
        {
            SimpleDdgiReceiverCache = SimpleDdgiReceiverCacheDiagnostics.Active(
                SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                SimpleDdgiReceiverCacheFallbackReason.None,
                string.Empty,
                radianceBytes: 1,
                surfaceSidecarBytes: 1,
                pipelineArtifact: "receiver-cache-temporal-adaptive"),
            ForwardGiReceiverCacheGenerated = 1,
            ForwardGiReceiverCacheConsumed = 1,
            ForwardGiExactGatherUsed = 0
        };

        try
        {
            var runner = new SampleBenchmarkRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => exited = true,
                () => settingsFingerprint);

            for (int frame = 0; frame < 100; frame++)
            {
                runner.OnFrameRendered(
                    frame,
                    exactFallback,
                    RenderBudgetSnapshot.Empty);
            }

            Assert.That(exited, Is.False);
            for (int frame = 100; frame < 130; frame++)
            {
                runner.OnFrameRendered(
                    frame,
                    cacheActive,
                    RenderBudgetSnapshot.Empty);
            }

            SampleBenchmarkReport report = runner.Report ??
                throw new AssertionException(
                    "The active receiver cache did not complete the benchmark.");
            Assert.Multiple(() =>
            {
                Assert.That(exited, Is.True);
                Assert.That(report.SettlingWaitTimedOut, Is.False);
                Assert.That(
                    report.AdditionalSettlingFrameCount,
                    Is.EqualTo(29));
                Assert.That(
                    report.LastDiagnostics.ForwardGiReceiverCacheConsumed,
                    Is.EqualTo(1));
                Assert.That(
                    report.LastDiagnostics.ForwardGiExactGatherUsed,
                    Is.Zero);
            });
        }
        finally
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);
        }
    }

    [TestCase(SamplePerformanceScenario.GiMovingPointLight)]
    [TestCase(SamplePerformanceScenario.GiMovingRigidObject)]
    public void DynamicQualificationScenario_FreezesAfterBoundedBenchmarkDisturbance(
        SamplePerformanceScenario scenario)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                HelloGame.ShouldFreezeBenchmarkDynamicScenario(
                    scenario,
                    benchmarkEnabled: true,
                    HelloGame.BenchmarkDynamicScenarioDisturbanceFrameCount - 1),
                Is.False);
            Assert.That(
                HelloGame.ShouldFreezeBenchmarkDynamicScenario(
                    scenario,
                    benchmarkEnabled: true,
                    HelloGame.BenchmarkDynamicScenarioDisturbanceFrameCount),
                Is.True);
            Assert.That(
                HelloGame.ShouldFreezeBenchmarkDynamicScenario(
                    scenario,
                    benchmarkEnabled: false,
                    HelloGame.BenchmarkDynamicScenarioDisturbanceFrameCount),
                Is.False,
                "Interactive scenarios must remain animated.");
        });
    }

    [Test]
    public void StaticQualificationScenario_IsNeverFrozenByDynamicBenchmarkControl()
    {
        Assert.That(
            HelloGame.ShouldFreezeBenchmarkDynamicScenario(
                SamplePerformanceScenario.GiSimpleDdgiFurnace,
                benchmarkEnabled: true,
                int.MaxValue),
            Is.False);
    }

    [Test]
    public void BistroStartupCamera_UsesRepresentativePlayBookmark()
    {
        var preset = HelloGame.GetCameraPreset(SampleSceneKind.Bistro);

        Assert.Multiple(() =>
        {
            Assert.That(
                preset.Position,
                Is.EqualTo(new Njulf.Core.Math.Vector3(-16.003326f, 2.5132222f, 1.2387409f)));
            Assert.That(preset.Yaw, Is.EqualTo(1.6121571f));
            Assert.That(preset.Pitch, Is.EqualTo(0.0660575f));
            Assert.That(preset.FarPlane, Is.EqualTo(500.0f));
        });
    }

    [Test]
    public void FastTraversalBenchmarkCamera_StreamsThenExercisesFullRingCuts()
    {
        var start = HelloGame.ResolveFastTraversalBenchmarkCameraPose(5);
        var firstStreamingStep =
            HelloGame.ResolveFastTraversalBenchmarkCameraPose(6);
        var firstArrival =
            HelloGame.ResolveFastTraversalBenchmarkCameraPose(17);
        var returnCut =
            HelloGame.ResolveFastTraversalBenchmarkCameraPose(18);
        var finalCut =
            HelloGame.ResolveFastTraversalBenchmarkCameraPose(23);
        var settled =
            HelloGame.ResolveFastTraversalBenchmarkCameraPose(300);

        Assert.Multiple(() =>
        {
            Assert.That(firstStreamingStep.Position.Z,
                Is.LessThan(start.Position.Z));
            Assert.That(firstArrival.Position.Z,
                Is.EqualTo(-28.5f).Within(1e-5f));
            Assert.That(returnCut.Position,
                Is.EqualTo(start.Position));
            Assert.That(finalCut.Position,
                Is.EqualTo(firstArrival.Position));
            Assert.That(
                Njulf.Core.Math.Vector3.Distance(
                    start.Position,
                    firstArrival.Position),
                Is.EqualTo(35.0f).Within(1e-5f),
                "The cut must cover a complete 28x1.25 m near-ring width.");
            Assert.That(finalCut.Yaw,
                Is.EqualTo(MathF.PI).Within(1e-5f));
            Assert.That(settled,
                Is.EqualTo(finalCut));
        });
    }

    [Test]
    public void ResolvedGiSettingsDifference_ReportsNamedFieldsAndHonorsTheBound()
    {
        var expected = new ResolvedGiSettingsMetadata(
            "first",
            string.Empty,
            ["alpha=1", "beta=two", "removed=present"]);
        var actual = new ResolvedGiSettingsMetadata(
            "second",
            string.Empty,
            ["alpha=2", "beta=two", "added=present"]);

        IReadOnlyList<string> differences =
            SampleBenchmarkAnalyzer.DescribeResolvedGiSettingsDifferences(
                expected,
                actual,
                maximumDifferenceCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(differences, Has.Count.EqualTo(2));
            Assert.That(differences[0], Is.EqualTo("'added' from <missing> to 'present'"));
            Assert.That(differences[1], Is.EqualTo("'alpha' from '1' to '2'"));
        });
    }

    [Test]
    public void MeasurementReadiness_WaitsForGpuTimingSettlementAndStableCapacity()
    {
        RendererDiagnostics ready = RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    ReadbackValid = 1,
                    ParticipatingProbeCount = 1_000,
                    SourceRepairProbeCount = 50,
                    ConvergedProbeCount = 950
                },
            CaptureFrame = new PerformanceCaptureFrameMetadata(
                800,
                800,
                DdgiRuntimeWarmupState.SteadyState,
                800,
                800)
            {
                TransportConvergencePending = false
            },
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true
                }
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(SampleBenchmarkRunner.IsReadyForMeasurement(ready), Is.True);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(
                    ready with { GpuTimingValid = 0 }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    CaptureFrame = ready.CaptureFrame with
                    {
                        TransportConvergencePending = true
                    }
                }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    SimpleDdgiUploadTiming = ready.SimpleDdgiUploadTiming with
                    {
                        CapacityDetails = ready.SimpleDdgiUploadTiming.CapacityDetails with
                        {
                            StableKeyHit = false
                        }
                    }
                }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    SimpleDdgiTransportConvergence =
                        ready.SimpleDdgiTransportConvergence with
                        {
                            SourceRepairProbeCount = 51
                        }
                }),
                Is.False);
        });
    }

    [Test]
    public void MeasurementReadiness_TailModeRequiresACompleteCurrentCertificate()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportTailCertificationEnabled = true,
            CaptureFrame = new PerformanceCaptureFrameMetadata(
                800,
                800,
                DdgiRuntimeWarmupState.SteadyState,
                800,
                800)
            {
                // A current certificate is the V2 authority even if legacy
                // capture metadata arrives one frame late.
                TransportConvergencePending = true
            },
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true
                }
            },
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    TailAuditComplete = true,
                    TailCertificateCurrent = true,
                    TailExpectedParticipantCount = 2,
                    TailAuditedParticipantCount = 2,
                    TailExpectedTexelCount = 128,
                    TailAuditedTexelCount = 128
                }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(diagnostics),
                Is.True);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(diagnostics with
                {
                    SimpleDdgiTransportConvergence =
                        diagnostics.SimpleDdgiTransportConvergence with
                        {
                            TailCertificateCurrent = false
                        }
                }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(diagnostics with
                {
                    SimpleDdgiTransportConvergence =
                        diagnostics.SimpleDdgiTransportConvergence with
                        {
                            TailAuditedTexelCount = 127
                        }
                }),
                Is.False);
        });
    }

    [Test]
    public void MeasurementReadiness_MovingRouteRequiresTimingAndStableCapacityNotAStationaryCertificate()
    {
        RendererDiagnostics moving = RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportTailCertificationEnabled = true,
            CaptureFrame = new PerformanceCaptureFrameMetadata(
                800,
                800,
                DdgiRuntimeWarmupState.NearCascadeWarmup,
                800,
                800)
            {
                TransportConvergencePending = true
            },
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true
                }
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(
                    moving,
                    SampleBenchmarkTrajectoryKind.BistroLoop),
                Is.True);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(moving),
                Is.False,
                "Stationary captures still require a current certificate.");
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(
                    moving with { GpuTimingValid = 0 },
                    SampleBenchmarkTrajectoryKind.BistroLoop),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(
                    moving with
                    {
                        SimpleDdgiUploadTiming =
                            moving.SimpleDdgiUploadTiming with
                            {
                                CapacityDetails =
                                    moving.SimpleDdgiUploadTiming.CapacityDetails with
                                    {
                                        StableKeyHit = false
                                    }
                            }
                    },
                    SampleBenchmarkTrajectoryKind.BistroLoop),
                Is.False);
        });
    }

    [Test]
    public void ProductionGate_MovingRouteRequiresAuthenticatedDynamicReadinessNotStationaryTail()
    {
        RendererDiagnostics moving = RendererDiagnostics.Empty with
        {
            GlobalIlluminationDdgiActive = 1,
            GpuTimingValid = 1,
            DdgiCacheGeneration = 1,
            DdgiWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            DdgiWarmedVisibleProbeFraction = 1.0f,
            DdgiWarmedLocalProbeFraction = 1.0f,
            DdgiWarmedCascade0ProbeFraction = 1.0f,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportTailCertificationEnabled = true,
            SimpleDdgiTransportGlobalConvergencePending = 1,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    TransitionCount = 0
                }
            },
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleDdgiProductionGate.IsPhase10CacheWarmupReady(
                    moving,
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: true),
                Is.True);
            Assert.That(
                SampleDdgiProductionGate.IsPhase10WarmupProgressValid(
                    moving,
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: true),
                Is.True);
            Assert.That(
                SampleDdgiProductionGate.IsSimpleDdgiTransportQualified(
                    moving,
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: true),
                Is.True);
            Assert.That(
                SampleDdgiProductionGate.IsSimpleDdgiTransportQualified(
                    moving,
                    movingTrajectory: false,
                    authenticatedMovingTrajectory: false),
                Is.False,
                "Stationary qualification still requires a current tail certificate.");
        });
    }

    [Test]
    public void ProductionGate_MovingRouteFailsClosedWithoutAuthenticationOrStableCapacity()
    {
        RendererDiagnostics moving = RendererDiagnostics.Empty with
        {
            GlobalIlluminationDdgiActive = 1,
            GpuTimingValid = 1,
            DdgiCacheGeneration = 1,
            DdgiWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
            DdgiWarmedVisibleProbeFraction = 1.0f,
            DdgiWarmedLocalProbeFraction = 1.0f,
            DdgiWarmedCascade0ProbeFraction = 1.0f,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    TransitionCount = 0
                }
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleDdgiProductionGate.IsPhase10CacheWarmupReady(
                    moving,
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: false),
                Is.False);
            Assert.That(
                SampleDdgiProductionGate.IsPhase10WarmupProgressValid(
                    moving,
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: false),
                Is.False);
            Assert.That(
                SampleDdgiProductionGate.IsSimpleDdgiTransportQualified(
                    moving with
                    {
                        SimpleDdgiUploadTiming = moving.SimpleDdgiUploadTiming with
                        {
                            CapacityDetails =
                                moving.SimpleDdgiUploadTiming.CapacityDetails with
                                {
                                    TransitionCount = 1
                                }
                        }
                    },
                    movingTrajectory: true,
                    authenticatedMovingTrajectory: true),
                Is.False);
        });
    }

    [Test]
    public void CreateReport_MovingRouteAuthenticatesDynamicDdgiStateInsteadOfDemandingStationaryState()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        for (int index = 0; index < 2; index++)
        {
            analyzer.AddSample(RendererDiagnostics.Empty with
            {
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    (ulong)index,
                    (ulong)index,
                    index == 0
                        ? DdgiRuntimeWarmupState.NearCascadeWarmup
                        : DdgiRuntimeWarmupState.Recovery,
                    1,
                    1)
                {
                    TransportConvergencePending = true
                },
                ResolvedGiSettings = new ResolvedGiSettingsMetadata(
                    $"dynamic-{index}",
                    string.Empty,
                    [$"route-frame={index}"])
            }, RenderBudgetSnapshot.Empty);
        }

        SampleBenchmarkOptions options = new(true, 0, 2, null)
        {
            Trajectory = SampleBenchmarkTrajectoryKind.BistroLoop,
            TrajectoryBistroVariant =
                SampleBistroQualityCaptureVariant.SteadyMotion,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.BistroLoop,
                SampleBistroQualityCaptureVariant.SteadyMotion)
        };
        SampleBenchmarkReport report = analyzer.CreateReport(
            options,
            SamplePerformanceScenario.BistroQualityMotionRelight,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.CaptureContract.Mismatches,
                Has.None.Contains("warmup state is"));
            Assert.That(
                report.CaptureContract.Mismatches,
                Has.None.Contains("Resolved GI settings changed"));
            Assert.That(
                report.CaptureContract.TrajectorySequenceHash,
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [TestCase(1, 1_000, 50, 0, 950, 0, true)]
    [TestCase(1, 1_000, 51, 0, 949, 0, false)]
    [TestCase(1, 1_000, 200, 200, 800, 0, true)]
    [TestCase(1, 1_000, 200, 149, 800, 0, false)]
    [TestCase(1, 1_000, 0, 0, 800, 150, true)]
    [TestCase(0, 1_000, 0, 0, 1_000, 0, false)]
    [TestCase(1, 0, 0, 0, 0, 0, false)]
    public void MeasurementReadiness_RequiresNinetyFivePercentSourceReadyPopulation(
        int readbackValid,
        int participants,
        int sourceRepair,
        int routineSourceRepair,
        int converged,
        int routineMaintenancePending,
        bool expected)
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    ReadbackValid = readbackValid,
                    ParticipatingProbeCount = participants,
                    SourceRepairProbeCount = sourceRepair,
                    RoutineSourceRepairProbeCount = routineSourceRepair,
                    ConvergedProbeCount = converged,
                    RoutineMaintenancePendingProbeCount =
                        routineMaintenancePending
                }
        };

        Assert.That(
            SampleBenchmarkRunner.HasSourceReadySimpleDdgiTransportPopulation(
                diagnostics),
            Is.EqualTo(expected));
    }

    [Test]
    public void CreateReport_RanksSlowestGpuPassAndBudgetFindings()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var budget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GPU frame",
                    Value: 17.0,
                    WarningThreshold: 13.6,
                    FailureThreshold: 16.0,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget)
            ],
            OverallStatus = RenderBudgetStatus.OverBudget
        };

        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_000,
            GpuFrameMicroseconds = 17_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuForwardOpaqueMicroseconds = 5_000,
            GpuBloomUpsampleMicroseconds = 1_000
        }, budget);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_500,
            GpuFrameMicroseconds = 18_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuForwardOpaqueMicroseconds = 6_000,
            GpuBloomUpsampleMicroseconds = 1_500
        }, budget);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 1, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 1,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 1,
            lastMeasurementFrameIndex: 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.GpuFrameMilliseconds.Count, Is.EqualTo(2));
            Assert.That(report.GpuPasses[0].Name, Is.EqualTo("ForwardPlusPass"));
            Assert.That(report.GpuPasses[0].P95Milliseconds, Is.EqualTo(6.0));
            Assert.That(report.Findings.First().Subject, Is.EqualTo("ForwardPlusPass"));
            Assert.That(report.Findings.Any(finding => finding.Category == "budget" && finding.Subject == "GPU frame"), Is.True);
        });
    }

    [Test]
    public void CreateReport_IncludesGlobalIlluminationGpuPasses()
    {
        var analyzer = new SampleBenchmarkAnalyzer();

        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_000,
            GpuFrameMicroseconds = 7_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            SimpleDdgiActive = 1,
            GpuSimpleDdgiPageDemandMicroseconds = 40,
            GpuSimpleDdgiPageResidencyMicroseconds = 80,
            GpuSimpleDdgiPageFeedbackMicroseconds = 20,
            GpuSimpleDdgiScheduleMicroseconds = 300,
            GpuSimpleDdgiTraceMicroseconds = 1_000,
            GpuSimpleDdgiTransportMicroseconds = 200,
            GpuSimpleDdgiBlendMicroseconds = 250,
            GpuSimpleDdgiCommitMicroseconds = 250,
            GpuDdgiUpdateMicroseconds = 1_500,
            GpuGiCompositeMicroseconds = 500
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.GiCornellRoom,
            warmupFrameCount: 0,
            measurementFrameCount: 1,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiPageDemandPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiPageResidencyPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiPageFeedbackPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiSchedulePass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiTracePass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiTransportPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiBlendPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SimpleDdgiSchedulerCommitPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "GlobalIlluminationCompositePass"), Is.True);
        });
    }

    [Test]
    public void CreateReport_ReconcilesMotionVectorTimingWithGpuFrame()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuMotionVectorMicroseconds = 573,
            GpuFrameMicroseconds = 573
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 1,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.GpuPasses.Single(pass => pass.Name == "MotionVectorPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.573));
            Assert.That(report.GpuIndependentPassSumMilliseconds.AverageMilliseconds,
                Is.EqualTo(0.573));
            Assert.That(report.GpuUnexplainedMilliseconds.AverageMilliseconds,
                Is.Zero);
            Assert.That(report.CaptureContract.Mismatches.Any(mismatch =>
                mismatch.Contains("GPU pass sum differs", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void CreateReport_ReconcilesIndependentShadowAndPlanarTimingsWithGpuFrame()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuDirectionalRayShadowMicroseconds = 101,
            GpuAreaRayShadowMicroseconds = 102,
            GpuDirectionalShadowTemporalMicroseconds = 103,
            GpuDirectionalShadowSpatialMicroseconds = 104,
            GpuAutomaticPlanarCaptureMicroseconds = 105,
            GpuFrameMicroseconds = 515
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 1,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "DirectionalRayShadowPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.101));
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "AreaRayShadowPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.102));
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "DirectionalShadowTemporalPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.103));
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "DirectionalShadowSpatialPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.104));
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "AutomaticPlanarReflectionPass")
                    .AverageMilliseconds,
                Is.EqualTo(0.105));
            Assert.That(
                report.GpuIndependentPassSumMilliseconds.AverageMilliseconds,
                Is.EqualTo(0.515));
            Assert.That(report.GpuUnexplainedMilliseconds.AverageMilliseconds,
                Is.Zero);
            Assert.That(report.CaptureContract.Mismatches.Any(mismatch =>
                mismatch.Contains("GPU pass sum differs", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void CreateReport_TimingBudgetsUseMeasurementP95InsteadOfWorstRollingValue()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var rollingWorst = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "CPU renderer",
                    Value: 100,
                    WarningThreshold: 8.5,
                    FailureThreshold: 10,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    "GPU frame",
                    Value: 100,
                    WarningThreshold: 8.5,
                    FailureThreshold: 10,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget)
            ]
        };

        for (int index = 0; index < 20; index++)
        {
            long microseconds = index == 19 ? 100_000 : 1_000;
            analyzer.AddSample(
                RendererDiagnostics.Empty with
                {
                    CpuTotalDrawSceneMicroseconds = microseconds,
                    GpuFrameMicroseconds = microseconds,
                    GpuTimingSupported = 1,
                    GpuTimingValid = 1
                },
                rollingWorst);
        }

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 20, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 20,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 19);

        Assert.Multiple(() =>
        {
            BudgetMetric cpu = report.BudgetMetrics.Single(
                metric => metric.Name == "CPU renderer");
            BudgetMetric gpu = report.BudgetMetrics.Single(
                metric => metric.Name == "GPU frame");
            Assert.That(cpu.Value, Is.EqualTo(1.0));
            Assert.That(cpu.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(gpu.Value, Is.EqualTo(1.0));
            Assert.That(gpu.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(report.CpuFrameMilliseconds.MaxMilliseconds, Is.EqualTo(100.0));
            Assert.That(report.GpuFrameMilliseconds.MaxMilliseconds, Is.EqualTo(100.0));
        });
    }

    [Test]
    public void CreateReport_MaterialTimingBudgetsContainOnlyMeasurementWindowEvents()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        RendererDiagnostics baseline = RendererDiagnostics.Empty with
        {
            MaterialCompileTimingSampleCount = 2,
            MaterialUploadTimingSampleCount = 2
        };
        analyzer.SetMeasurementBaseline(baseline);
        var staleRollingBudget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
                    4.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiUploadP95MetricName,
                    4.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiPipelineP95MetricName,
                    8.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget)
            ]
        };

        analyzer.AddSample(baseline, staleRollingBudget);
        analyzer.AddSample(
            baseline with
            {
                MaterialCompileTimingSampleCount = 3,
                MaterialUploadTimingSampleCount = 3,
                MaterialLastCompileMicroseconds = 100,
                MaterialLastUploadMicroseconds = 100
            },
            staleRollingBudget);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiCompileP95MetricName).Value,
                Is.EqualTo(0.1));
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiUploadP95MetricName).Value,
                Is.EqualTo(0.1));
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiPipelineP95MetricName).Value,
                Is.EqualTo(0.2));
            Assert.That(
                report.BudgetMetrics.Where(metric =>
                    metric.Name.StartsWith("Material GI", StringComparison.Ordinal))
                    .All(metric => metric.Status == RenderBudgetStatus.WithinBudget),
                Is.True);
            Assert.That(
                report.MaterialTimingEvidence.CompileSequenceExact,
                Is.True);
            Assert.That(
                report.MaterialTimingEvidence.UploadSequenceExact,
                Is.True);
            Assert.That(report.MaterialTimingEvidence.Compile.Count, Is.EqualTo(1));
            Assert.That(report.MaterialTimingEvidence.Upload.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateReport_ExportsSimpleDdgiCpuPhasesAndCombinedTransportDistribution()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            CpuTotalDrawSceneMicroseconds = 3_000,
            GpuFrameMicroseconds = 5_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuSimpleDdgiTransportMicroseconds = 900,
            GpuSimpleDdgiBlendMicroseconds = 200,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                TotalMicroseconds = 1_200,
                CapacityMicroseconds = 25,
                QueueBuildMicroseconds = 300,
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    PredicateMicroseconds = 4
                }
            }
        }, RenderBudgetSnapshot.Empty);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            CpuTotalDrawSceneMicroseconds = 3_100,
            GpuFrameMicroseconds = 5_200,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuSimpleDdgiTransportMicroseconds = 1_100,
            GpuSimpleDdgiBlendMicroseconds = 300,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                TotalMicroseconds = 1_400,
                CapacityMicroseconds = 30,
                QueueBuildMicroseconds = 350,
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    PredicateMicroseconds = 5
                }
            }
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiUpload")
                    .P95Milliseconds,
                Is.EqualTo(1.4));
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiUpload.Capacity")
                    .P95Milliseconds,
                Is.EqualTo(0.03));
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiCapacity.Predicate")
                    .Count,
                Is.EqualTo(2));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.Count, Is.EqualTo(2));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.MedianMilliseconds, Is.EqualTo(1.25));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.P95Milliseconds, Is.EqualTo(1.4));
        });
    }

    [Test]
    public void CreateReport_RetainsWorstBudgetMetricAcrossAllMeasurementFrames()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var overBudget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "DDGI total memory",
                    Value: 257,
                    WarningThreshold: 200,
                    FailureThreshold: 256,
                    Unit: "MiB",
                    Status: RenderBudgetStatus.OverBudget)
            ]
        };
        var recovered = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "DDGI total memory",
                    Value: 128,
                    WarningThreshold: 200,
                    FailureThreshold: 256,
                    Unit: "MiB",
                    Status: RenderBudgetStatus.WithinBudget)
            ]
        };
        RendererDiagnostics validTiming = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000
        };

        analyzer.AddSample(validTiming, overBudget);
        analyzer.AddSample(validTiming, recovered);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        BudgetMetric metric = report.BudgetMetrics.Single(
            candidate => candidate.Name == "DDGI total memory");
        Assert.Multiple(() =>
        {
            Assert.That(metric.Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            Assert.That(metric.Value, Is.EqualTo(257));
            Assert.That(
                report.Findings.Any(
                    finding => finding.Category == "budget" &&
                               finding.Subject == "DDGI total memory"),
                Is.True);
        });
    }

    [TestCase(RenderBudgetStatus.Unavailable)]
    [TestCase(RenderBudgetStatus.Unknown)]
    public void CreateReport_RetainsIncompleteCoverageFromAnyMeasurementFrame(
        RenderBudgetStatus incompleteStatus)
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var unavailable = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GI GPU",
                    Value: 0,
                    WarningThreshold: 5,
                    FailureThreshold: 6,
                    Unit: "ms",
                    Status: incompleteStatus)
            ]
        };
        var available = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GI GPU",
                    Value: 2,
                    WarningThreshold: 5,
                    FailureThreshold: 6,
                    Unit: "ms",
                    Status: RenderBudgetStatus.WithinBudget)
            ]
        };
        RendererDiagnostics validTiming = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000
        };

        analyzer.AddSample(validTiming, unavailable);
        analyzer.AddSample(validTiming, available);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        BudgetMetric metric = report.BudgetMetrics.Single(
            candidate => candidate.Name == "GI GPU");
        Assert.That(metric.Status, Is.EqualTo(incompleteStatus));
    }

    [Test]
    public void CreateReport_IdleDdgiFrameDoesNotPoisonMeasuredUpdateBudget()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RendererDiagnostics idle = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiActiveProbeCount = 100,
            DdgiProbeUpdateRequestBudget = 64,
            DdgiProbesUpdated = 0
        };
        RendererDiagnostics active = idle with { DdgiProbesUpdated = 1 };
        var evaluator = new RenderBudgetEvaluator();
        var upload = new UploadBudgetSnapshot(
            0, profile.UploadBudgetBytesPerFrame, 0, 0, [],
            RenderBudgetStatus.WithinBudget);
        var stalls = new RuntimeStallSnapshot(
            0, 0, RuntimeStallReason.Unknown, 0, []);

        analyzer.AddSample(idle, evaluator.Evaluate(
            profile, idle, MemoryBudgetSnapshot.Empty, upload, stalls));
        analyzer.AddSample(active, evaluator.Evaluate(
            profile, active, MemoryBudgetSnapshot.Empty, upload, stalls));

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);
        BudgetMetric requestBudget = report.BudgetMetrics.Single(candidate =>
            candidate.Name == "DDGI update request budget");
        BudgetMetric updatedProbes = report.BudgetMetrics.Single(candidate =>
            candidate.Name == "DDGI probes updated");

        Assert.Multiple(() =>
        {
            Assert.That(requestBudget.Status,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(requestBudget.Value, Is.EqualTo(1));
            Assert.That(updatedProbes.Status,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(updatedProbes.Value, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateReport_ExportsDeterministicTopEightCorrelatedCpuSpikeFrames()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        long[] totals = [1_000, 9_000, 7_000, 8_000, 9_000, 6_000, 5_000, 4_000, 3_000, 2_000];
        for (int index = 0; index < totals.Length; index++)
        {
            analyzer.AddSample(RendererDiagnostics.Empty with
            {
                CpuTotalDrawSceneMicroseconds = totals[index],
                CpuSceneBuildMicroseconds = 100 + index,
                CpuPayloadSignatureMicroseconds = 200 + index,
                CpuObjectCullMicroseconds = 300 + index,
                CpuMeshletCullMicroseconds = 400 + index,
                CpuStaticBatchBuildMicroseconds = 500 + index,
                CpuUploadMicroseconds = 600 + index,
                CpuMaterialUploadMicroseconds = 700 + index,
                CpuAccelerationStructureBuildMicroseconds = 800 + index,
                CpuAccelerationStructureBlasBuildMicroseconds = 810 + index,
                CpuAccelerationStructureBlasCompactionMicroseconds = 820 + index,
                CpuAccelerationStructureTlasBuildMicroseconds = 830 + index,
                CpuAccelerationStructureInstanceUploadMicroseconds = 840 + index,
                CpuPrimaryCommandRecordMicroseconds = 900 + index,
                CpuSecondaryCommandRecordMicroseconds = 1_000 + index,
                CpuWaitForFrameFenceMicroseconds = 1_100 + index,
                RuntimeStallMicrosecondsThisFrame = 1_150 + index,
                CpuReflectionProbeCaptureRecordMicroseconds = 1_160 + index,
                CpuReflectionProbePrefilterRecordMicroseconds = 1_170 + index,
                CpuQueueSubmitMicroseconds = 90_000 + index,
                CpuPresentMicroseconds = 91_000 + index,
                ScenePayloadRebuilt = index % 2 == 0 ? 1 : 0,
                CameraDrivenCpuDrawListRebuilt = index == 4 ? 1 : 0,
                HiZPolicyCameraCut = index == 4 ? 1 : 0,
                SceneUploadCount = 1_200 + index,
                SceneUploadSkipped = 1_300 + index,
                VisibleObjectCount = 1_400 + index,
                VisibleMeshletCount = 1_500 + index,
                StaticInstanceBatchCount = 1_600 + index,
                StaticInstanceCount = 1_700 + index,
                VisibleStaticInstanceCount = 1_800 + index,
                CulledStaticInstanceCount = 1_900 + index,
                StaticBatchMeshletDrawCommandCount = 2_000 + index,
                MaterialCount = 2_100 + index,
                MaterialRevision = (uint)(2_200 + index),
                TransparentSortCandidateCount = 2_210 + index,
                TransparentSortMicroseconds = 2_220 + index,
                ReflectionProbeCapturesQueued = 2_230 + index,
                ReflectionProbeCapturesCompleted = 2_240 + index,
                ReflectionProbeCapturesCompletedTotal = (ulong)(2_250 + index),
                ObjectCandidatesCpu = 2_300 + index,
                ObjectFrustumCulledCpu = 2_400 + index,
                MeshletCandidatesCpu = 2_500 + index,
                MeshletFrustumCulledCpu = 2_600 + index,
                MeshletLodSkippedCpu = 2_700 + index,
                MeshletLod0SubmittedCpu = 2_800 + index,
                MeshletLod1SubmittedCpu = 2_900 + index,
                MeshletLod2SubmittedCpu = 3_000 + index,
                MeshletCountSubmittedCpu = 3_100 + index,
                SceneSubmissionActiveMode = SceneSubmissionMode.GpuCompactedIndirect,
                SceneSubmissionCpuCandidateCount = 3_200 + index,
                SceneSubmissionGpuOpaqueCandidateCount = 3_300 + index,
                SceneSubmissionGpuOpaqueFrustumRejectedCount = 3_400 + index,
                SceneSubmissionGpuLod0EmittedCount = 3_500 + index,
                SceneSubmissionGpuLod1EmittedCount = 3_600 + index,
                SceneSubmissionGpuLod2EmittedCount = 3_700 + index,
                SceneSubmissionGpuMissingLodFallbackCount = 3_800 + index,
                SceneSubmissionGpuOpaqueLodDecimatedCount = 3_900 + index,
                AccelerationStructureBlasBuildCount = 4_000 + index,
                AccelerationStructureBlasCompactionQueryCount = 4_010 + index,
                AccelerationStructureBlasCompactionCount = 4_020 + index,
                AccelerationStructureBlasCompactionPendingCount = 4_030 + index,
                AccelerationStructureBlasCompactionQueryOverflowCount = 4_040 + index,
                AccelerationStructureBlasCompactionQueryReadbackFailureCount = 4_050 + index,
                AccelerationStructureTlasBuildCount = 4_100 + index,
                AccelerationStructureTlasUpdateCount = 4_200 + index,
                AccelerationStructureTlasSkipCount = 4_300 + index,
                UploadedBytes = (ulong)(5_000 + index),
                StableSceneInputUploadBytes = (ulong)(5_100 + index),
                CpuCandidateListUploadBytes = (ulong)(5_200 + index),
                ObjectUploadBytes = (ulong)(5_300 + index),
                InstanceUploadBytes = (ulong)(5_400 + index),
                MeshletDrawUploadBytes = (ulong)(5_500 + index),
                TransparentMeshletDrawUploadBytes = (ulong)(5_600 + index),
                SolidDepthMeshletDrawUploadBytes = (ulong)(5_700 + index),
                MaskedDepthMeshletDrawUploadBytes = (ulong)(5_800 + index),
                MaterialUploadBytes = (ulong)(5_900 + index),
                MaterialExtensionUploadBytes = (ulong)(6_000 + index),
                LightUploadBytes = (ulong)(6_100 + index),
                AccelerationStructureInstanceUploadBytes = (ulong)(6_200 + index),
                AccelerationStructureRayQueryMetadataUploadBytes = (ulong)(6_300 + index),
                CaptureSceneContentRevision = (ulong)(6_400 + index),
                CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
                {
                    FrameSerial = (ulong)(6_500 + index),
                    FramesSinceSceneLoad = (ulong)(6_600 + index)
                },
                CaptureSceneAssetHash = $"asset-{index}",
                CaptureSceneStateHash = $"state-{index}"
            }, RenderBudgetSnapshot.Empty);
        }

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, totals.Length, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: totals.Length,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: totals.Length - 1);

        IReadOnlyList<SampleBenchmarkCpuSlowFrame> slowest =
            report.CpuSpikeEvidence.SlowestFrames;
        SampleBenchmarkCpuSlowFrame correlated = slowest[1];
        string[] exportedPropertyNames = typeof(SampleBenchmarkCpuSlowFrame)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(slowest, Has.Count.EqualTo(8));
            Assert.That(
                slowest.Select(static frame => frame.MeasurementSampleIndex),
                Is.EqualTo(new[] { 1, 4, 3, 2, 5, 6, 7, 8 }));
            Assert.That(correlated.CpuPayloadSignatureMicroseconds, Is.EqualTo(204));
            Assert.That(correlated.CpuMeshletCullMicroseconds, Is.EqualTo(404));
            Assert.That(correlated.CpuStaticBatchBuildMicroseconds, Is.EqualTo(504));
            Assert.That(correlated.CpuAccelerationStructureTlasBuildMicroseconds, Is.EqualTo(834));
            Assert.That(correlated.CpuSecondaryCommandRecordMicroseconds, Is.EqualTo(1_004));
            Assert.That(correlated.CpuWaitForFrameFenceMicroseconds, Is.EqualTo(1_104));
            Assert.That(correlated.RuntimeStallMicrosecondsThisFrame, Is.EqualTo(1_154));
            Assert.That(correlated.CpuReflectionProbeCaptureRecordMicroseconds, Is.EqualTo(1_164));
            Assert.That(correlated.CpuReflectionProbePrefilterRecordMicroseconds, Is.EqualTo(1_174));
            Assert.That(correlated.CameraDrivenCpuDrawListRebuilt, Is.EqualTo(1));
            Assert.That(correlated.HiZPolicyCameraCut, Is.EqualTo(1));
            Assert.That(correlated.TransparentSortCandidateCount, Is.EqualTo(2_214));
            Assert.That(correlated.ReflectionProbeCapturesQueued, Is.EqualTo(2_234));
            Assert.That(correlated.ReflectionProbeCapturesCompleted, Is.EqualTo(2_244));
            Assert.That(correlated.MeshletCandidatesCpu, Is.EqualTo(2_504));
            Assert.That(correlated.SceneSubmissionGpuLod2EmittedCount, Is.EqualTo(3_704));
            Assert.That(correlated.MaterialUploadBytes, Is.EqualTo(5_904));
            Assert.That(correlated.AccelerationStructureInstanceUploadBytes, Is.EqualTo(6_204));
            Assert.That(correlated.AccelerationStructureBlasCompactionQueryCount, Is.EqualTo(4_014));
            Assert.That(correlated.AccelerationStructureBlasCompactionCount, Is.EqualTo(4_024));
            Assert.That(correlated.CaptureFrameSerial, Is.EqualTo(6_504));
            Assert.That(correlated.CaptureFramesSinceSceneLoad, Is.EqualTo(6_604));
            Assert.That(correlated.CaptureSceneStateHash, Is.EqualTo("state-4"));
            Assert.That(exportedPropertyNames,
                Does.Not.Contain(nameof(RendererDiagnostics.CpuQueueSubmitMicroseconds)));
            Assert.That(exportedPropertyNames,
                Does.Not.Contain(nameof(RendererDiagnostics.CpuPresentMicroseconds)));
            Assert.That(exportedPropertyNames,
                Does.Not.Contain(nameof(RendererDiagnostics.RuntimeWorstStallReason)));
        });
    }

    [Test]
    public void CreateReport_ReconcilesReflectionPublishTimingWithGpuFrame()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuReflectionProbePublishMicroseconds = 417,
            GpuFrameMicroseconds = 417
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
            warmupFrameCount: 0,
            measurementFrameCount: 1,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.GpuPasses.Single(pass =>
                        pass.Name == "ReflectionProbePublish")
                    .AverageMilliseconds,
                Is.EqualTo(0.417));
            Assert.That(
                report.GpuIndependentPassSumMilliseconds.AverageMilliseconds,
                Is.EqualTo(0.417));
            Assert.That(
                report.GpuUnexplainedMilliseconds.AverageMilliseconds,
                Is.Zero);
            Assert.That(report.CaptureContract.Mismatches.Any(mismatch =>
                mismatch.Contains(
                    "GPU pass sum differs",
                    StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void CreateReport_ExportsDeterministicTopEightAlignedReflectionWorkloads()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        long[] totals =
            [1_000, 9_000, 7_000, 8_000, 9_000, 6_000, 5_000, 4_000, 3_000, 2_000];
        const ulong firstFrameSerial = 10_000UL;
        for (int workloadIndex = 0; workloadIndex < totals.Length; workloadIndex++)
        {
            int originIndex = workloadIndex * 2;
            int frameSlot = originIndex % RenderingConstants.FramesInFlight;
            ulong frameSerial = firstFrameSerial + (ulong)originIndex;
            ReflectionProbeLifecycleFrameSnapshot submitted =
                CreateReflectionLifecycleFrame(
                    frameSlot,
                    frameSerial,
                    captureFaceUnits: workloadIndex + 1,
                    prefilterMipUnits: workloadIndex + 11,
                    publishCopyUnits: workloadIndex + 21);
            ReflectionProbeGpuBudgetSnapshot budget = new(
                BudgetMicroseconds: 1_000 + workloadIndex,
                ReservedMicroseconds: 200 + workloadIndex,
                FaceEstimateMicroseconds: 100 + workloadIndex,
                PrefilterEstimateMicroseconds: 120 + workloadIndex,
                CopyEstimateMicroseconds: 20 + workloadIndex,
                HasTimingHistory: true,
                BudgetExhausted: workloadIndex % 2 == 0);

            // Workload four deliberately lacks its origin inside the measured
            // window. The later completion must remain rankable while its
            // submitted budget is explicitly unavailable.
            analyzer.AddSample(RendererDiagnostics.Empty with
            {
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    frameSerial,
                    (ulong)originIndex,
                    DdgiRuntimeWarmupState.SteadyState,
                    0,
                    0),
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                ReflectionProbeCurrentLifecycle = submitted,
                ReflectionProbeCurrentCaptureBudget = budget
            }, RenderBudgetSnapshot.Empty);
            int completionIndex = originIndex + 1;
            ulong completionFrameSerial = firstFrameSerial +
                (ulong)completionIndex;
            ReflectionProbeLifecycleFrameSnapshot completed =
                workloadIndex == 4
                    ? submitted with
                    {
                        FrameSlot = 0,
                        FrameSerial = firstFrameSerial - 1UL
                    }
                    : submitted;
            analyzer.AddSample(RendererDiagnostics.Empty with
            {
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    completionFrameSerial,
                    (ulong)completionIndex,
                    DdgiRuntimeWarmupState.SteadyState,
                    0,
                    0),
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                GpuReflectionProbeCaptureMicroseconds = totals[workloadIndex] - 50,
                GpuReflectionProbePrefilterMicroseconds = 30,
                GpuReflectionProbePublishMicroseconds = 20,
                GpuFrameMicroseconds = totals[workloadIndex],
                ReflectionProbeCompletedLifecycle = completed,
                ReflectionProbeCurrentLifecycle =
                    CreateReflectionLifecycleFrame(
                        completionIndex % RenderingConstants.FramesInFlight,
                        completionFrameSerial,
                        captureFaceUnits: 0,
                        prefilterMipUnits: 0,
                        publishCopyUnits: 0),
                ReflectionProbeCurrentCaptureBudget = budget with
                {
                    ReservedMicroseconds = 900 + workloadIndex
                }
            }, RenderBudgetSnapshot.Empty);
        }
        for (int index = totals.Length * 2;
             index < SampleBenchmarkActivation.SponzaActivationFrameCount;
             index++)
        {
            ulong frameSerial = firstFrameSerial + (ulong)index;
            analyzer.AddSample(RendererDiagnostics.Empty with
            {
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    frameSerial,
                    (ulong)index,
                    DdgiRuntimeWarmupState.SteadyState,
                    0,
                    0),
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                ReflectionProbeCurrentLifecycle =
                    CreateReflectionLifecycleFrame(
                        index % RenderingConstants.FramesInFlight,
                        frameSerial,
                        captureFaceUnits: 0,
                        prefilterMipUnits: 0,
                        publishCopyUnits: 0),
                ReflectionProbeCurrentCaptureBudget = default
            }, RenderBudgetSnapshot.Empty);
        }

        SampleBenchmarkOptions options = new(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount:
                SampleBenchmarkActivation.SponzaActivationFrameCount,
            ReportPath: null)
        {
            CaptureVariant = SampleBenchmarkCaptureVariant.Baseline,
            Activation = SampleBenchmarkActivation.ReflectionRecapture,
            ActivationFingerprint = SampleBenchmarkActivation.CreateFingerprint(
                SampleBenchmarkActivation.ReflectionRecapture),
            Trajectory = SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                SampleBistroQualityCaptureVariant.SunScaleStep)
        };

        SampleBenchmarkReport report = analyzer.CreateReport(
            options,
            SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
            warmupFrameCount: 0,
            measurementFrameCount:
                SampleBenchmarkActivation.SponzaActivationFrameCount,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex:
                SampleBenchmarkActivation.SponzaActivationFrameCount - 1);

        IReadOnlyList<SampleReflectionProbeSlowFrame> slowest =
            report.ReflectionProbeCaptureEvidence.SlowestFrames;
        SampleReflectionProbeSlowFrame aligned = slowest[0];
        SampleReflectionProbeSlowFrame unavailable = slowest[1];
        Assert.Multiple(() =>
        {
            Assert.That(slowest, Has.Count.EqualTo(8));
            Assert.That(report.ReflectionProbeCaptureRawEvidence.Frames,
                Has.Count.EqualTo(
                    SampleBenchmarkActivation.SponzaActivationFrameCount));
            Assert.That(report.ReflectionProbeCaptureEvidence.Applicable,
                Is.True);
            Assert.That(
                slowest.Select(static frame => frame.MeasurementSampleIndex),
                Is.EqualTo(new[] { 3, 9, 7, 5, 11, 13, 15, 17 }));
            Assert.That(aligned.CompletedGpuMicroseconds, Is.EqualTo(9_000));
            Assert.That(aligned.GpuCaptureMicroseconds, Is.EqualTo(8_950));
            Assert.That(aligned.GpuPrefilterMicroseconds, Is.EqualTo(30));
            Assert.That(aligned.GpuPublishMicroseconds, Is.EqualTo(20));
            Assert.That(aligned.CompletedLifecycle.FrameSerial, Is.EqualTo(10_002UL));
            Assert.That(aligned.CompletedLifecycle.FrameSlot, Is.EqualTo(0));
            Assert.That(
                aligned.CompletedLifecycle.Lifecycle.CaptureFaceUnitsThisFrame,
                Is.EqualTo(2));
            Assert.That(
                aligned.CompletedLifecycle.Lifecycle.PrefilterMipUnitsThisFrame,
                Is.EqualTo(12));
            Assert.That(
                aligned.CompletedLifecycle.Lifecycle.PublishCopyUnitsThisFrame,
                Is.EqualTo(22));
            Assert.That(aligned.SubmittedBudgetAvailable, Is.True);
            Assert.That(aligned.SubmittedBudgetMeasurementSampleIndex, Is.EqualTo(2));
            Assert.That(aligned.SubmittedBudgetFrameSerial, Is.EqualTo(10_002UL));
            Assert.That(aligned.SubmittedBudgetFrameSlot, Is.EqualTo(0));
            Assert.That(aligned.SubmittedBudget.ReservedMicroseconds,
                Is.EqualTo(201),
                "the completed timing sample's newer 901us budget must not be attached");
            Assert.That(unavailable.MeasurementSampleIndex, Is.EqualTo(9));
            Assert.That(unavailable.SubmittedBudgetAvailable, Is.False);
            Assert.That(unavailable.SubmittedBudgetMeasurementSampleIndex, Is.EqualTo(-1));
        });

        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"reflection-capture-evidence-{Guid.NewGuid():N}.json");
        try
        {
            SampleBenchmarkRunner.WriteReport(report, path);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement jsonFrame = document.RootElement
                .GetProperty("ReflectionProbeCaptureEvidence")
                .GetProperty("SlowestFrames")[0];
            Assert.Multiple(() =>
            {
                Assert.That(
                    jsonFrame.GetProperty("CompletedLifecycle")
                        .GetProperty("FrameSerial").GetUInt64(),
                    Is.EqualTo(10_002UL));
                Assert.That(
                    jsonFrame.GetProperty("CompletedLifecycle")
                        .GetProperty("Lifecycle")
                        .GetProperty("CaptureFaceUnitsThisFrame").GetInt32(),
                    Is.EqualTo(2));
                Assert.That(
                    jsonFrame.GetProperty("SubmittedBudgetAvailable").GetBoolean(),
                    Is.True);
                Assert.That(
                    jsonFrame.GetProperty("SubmittedBudgetFrameSerial").GetUInt64(),
                    Is.EqualTo(10_002UL));
                Assert.That(
                    jsonFrame.GetProperty("SubmittedBudget")
                        .GetProperty("ReservedMicroseconds").GetInt32(),
                    Is.EqualTo(201));
                Assert.That(
                    jsonFrame.GetProperty("GpuPublishMicroseconds").GetInt64(),
                    Is.EqualTo(20));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void CreateReport_EmitsEmptyCpuCohortsAndNoSlowFramesWithoutSamples()
    {
        var analyzer = new SampleBenchmarkAnalyzer();

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 0, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 0,
            firstMeasurementFrameIndex: -1,
            lastMeasurementFrameIndex: -1);

        Assert.Multiple(() =>
        {
            Assert.That(report.CpuSpikeEvidence.Rebuilt.FrameCount, Is.Zero);
            Assert.That(report.CpuSpikeEvidence.Rebuilt.TotalDrawSceneMilliseconds.Count, Is.Zero);
            Assert.That(report.CpuSpikeEvidence.Stable.FrameCount, Is.Zero);
            Assert.That(report.CpuSpikeEvidence.Stable.TotalDrawSceneMilliseconds.Count, Is.Zero);
            Assert.That(report.CpuSpikeEvidence.SlowestFrames, Is.Empty);
            Assert.That(report.ReflectionProbeCaptureEvidence.SlowestFrames, Is.Empty);
        });
    }

    [Test]
    public void CreateReport_SplitsRebuiltAndStableCpuCohorts()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            ScenePayloadRebuilt = 1,
            CameraDrivenCpuDrawListRebuilt = 1,
            CpuTotalDrawSceneMicroseconds = 10_000,
            CpuSceneBuildMicroseconds = 4_000,
            CpuMeshletCullMicroseconds = 2_000
        }, RenderBudgetSnapshot.Empty);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            ScenePayloadRebuilt = 0,
            CpuTotalDrawSceneMicroseconds = 1_000,
            CpuSceneBuildMicroseconds = 400,
            CpuMeshletCullMicroseconds = 200
        }, RenderBudgetSnapshot.Empty);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            ScenePayloadRebuilt = 1,
            CpuTotalDrawSceneMicroseconds = 30_000,
            CpuSceneBuildMicroseconds = 12_000,
            CpuMeshletCullMicroseconds = 6_000
        }, RenderBudgetSnapshot.Empty);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            ScenePayloadRebuilt = 0,
            CpuTotalDrawSceneMicroseconds = 3_000,
            CpuSceneBuildMicroseconds = 1_200,
            CpuMeshletCullMicroseconds = 600
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 4, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 4,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 3);

        SampleBenchmarkCpuCohortEvidence rebuilt = report.CpuSpikeEvidence.Rebuilt;
        SampleBenchmarkCpuCohortEvidence stable = report.CpuSpikeEvidence.Stable;
        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.FrameCount, Is.EqualTo(2));
            Assert.That(rebuilt.ScenePayloadRebuiltFrameCount, Is.EqualTo(2));
            Assert.That(rebuilt.CameraDrivenCpuDrawListRebuiltFrameCount, Is.EqualTo(1));
            Assert.That(rebuilt.TotalDrawSceneMilliseconds.AverageMilliseconds, Is.EqualTo(20));
            Assert.That(rebuilt.TotalDrawSceneMilliseconds.P95Milliseconds, Is.EqualTo(30));
            Assert.That(rebuilt.SceneBuildMilliseconds.AverageMilliseconds, Is.EqualTo(8));
            Assert.That(rebuilt.MeshletCullMilliseconds.P95Milliseconds, Is.EqualTo(6));
            Assert.That(stable.FrameCount, Is.EqualTo(2));
            Assert.That(stable.ScenePayloadRebuiltFrameCount, Is.Zero);
            Assert.That(stable.CameraDrivenCpuDrawListRebuiltFrameCount, Is.Zero);
            Assert.That(stable.TotalDrawSceneMilliseconds.AverageMilliseconds, Is.EqualTo(2));
            Assert.That(stable.TotalDrawSceneMilliseconds.P95Milliseconds, Is.EqualTo(3));
            Assert.That(stable.SceneBuildMilliseconds.AverageMilliseconds, Is.EqualTo(0.8));
            Assert.That(stable.MeshletCullMilliseconds.P95Milliseconds, Is.EqualTo(0.6));
        });
    }

    private static ReflectionProbeLifecycleFrameSnapshot CreateReflectionLifecycleFrame(
        int frameSlot,
        ulong frameSerial,
        int captureFaceUnits,
        int prefilterMipUnits,
        int publishCopyUnits) => new(
        Valid: true,
        FrameSlot: frameSlot,
        FrameSerial: frameSerial,
        GpuTimingRecorded: true,
        Lifecycle: new ReflectionProbeLifecycleSnapshot(
            QueuedCount: 1,
            ActiveCount: 1,
            State: ReflectionProbeCaptureState.CapturingFaces,
            AwaitingGpuCompletionCount: 0,
            PublishedCount: 0,
            CapturesStartedThisFrame: 1,
            CapturesCompletedThisFrame: 0,
            CaptureFaceUnitsThisFrame: captureFaceUnits,
            PrefilterMipUnitsThisFrame: prefilterMipUnits,
            PublishCopyUnitsThisFrame: publishCopyUnits,
            CapturesStartedTotal: frameSerial,
            CapturesCompletedTotal: frameSerial - 1,
            CapturesPublishedTotal: frameSerial - 1,
            CaptureFaceUnitsTotal: (ulong)captureFaceUnits,
            PrefilterMipUnitsTotal: (ulong)prefilterMipUnits,
            PublishCopyUnitsTotal: (ulong)publishCopyUnits));
}
