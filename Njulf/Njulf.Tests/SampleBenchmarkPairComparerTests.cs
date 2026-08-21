using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkPairComparerTests
{
    [Test]
    public void Compare_EnforcesFivePercentRepeatabilityForLockedRuns()
    {
        SampleBenchmarkReport baseline = CreateReport(
            pairId: "locked-pair",
            identityHash: "sha256:locked-state",
            variant: "baseline",
            gpuFrameP95: 10.0,
            forwardP95: 4.0);
        SampleBenchmarkReport repeat = CreateReport(
            pairId: "locked-pair",
            identityHash: "sha256:locked-state",
            variant: "baseline",
            gpuFrameP95: 10.4,
            forwardP95: 4.1);

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(baseline, repeat);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.True);
            Assert.That(comparison.RepeatabilityPassed, Is.True);
            Assert.That(comparison.Failures, Is.Empty);
        });
    }

    [Test]
    public void Compare_RejectsRepeatabilityDriftButAllowsIntentionalAbMovement()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            gpuFrameP95: 10.0,
            forwardP95: 4.0);
        SampleBenchmarkReport variant = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "far-field-forced-old",
            gpuFrameP95: 15.0,
            forwardP95: 8.0);

        SampleBenchmarkPairComparison repeat =
            SampleBenchmarkPairComparer.Compare(baseline, variant);
        SampleBenchmarkPairComparison ab =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                variant,
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(repeat.Comparable, Is.False);
            Assert.That(repeat.RepeatabilityPassed, Is.False);
            Assert.That(repeat.Failures, Is.Not.Empty);
            Assert.That(ab.Comparable, Is.True);
            Assert.That(ab.RepeatabilityPassed, Is.False);
            Assert.That(ab.Failures, Is.Empty);
        });
    }

    [Test]
    public void Compare_RejectsDifferentLockedIdentityEvenForAbRuns()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:state-a",
            "baseline",
            10.0,
            4.0);
        SampleBenchmarkReport variant = CreateReport(
            "locked-pair",
            "sha256:state-b",
            "decals-disabled",
            9.0,
            3.0);

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                variant,
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.False);
            Assert.That(
                comparison.Failures,
                Does.Contain("Locked capture identities differ."));
        });
    }

    [Test]
    public void Compare_RejectsEveryMovingTrajectoryIdentityMismatch()
    {
        SampleBenchmarkReport baseline = WithMovingTrajectory(CreateReport(
            "locked-pair",
            "sha256:state-a",
            "baseline",
            10.0,
            4.0));
        SampleBenchmarkCaptureContract contract = baseline.CaptureContract;
        SampleBenchmarkReport[] mismatches =
        [
            baseline with
            {
                CaptureContract = contract with
                {
                    Trajectory = SampleBenchmarkTrajectory.SponzaVerticalName,
                    TrajectoryFrameCount =
                        SampleBenchmarkTrajectory.GetFrameCount(
                            SampleBenchmarkTrajectoryKind.SponzaVertical)
                }
            },
            baseline with
            {
                CaptureContract = contract with
                {
                    TrajectoryFingerprint = Sha256('b')
                }
            },
            baseline with
            {
                CaptureContract = contract with
                {
                    TrajectoryFrameCount = contract.TrajectoryFrameCount - 1
                }
            },
            baseline with
            {
                CaptureContract = contract with
                {
                    TrajectoryRouteHash = Sha256('c')
                }
            }
        ];
        string[] expectedFailures =
        [
            "Capture trajectories differ.",
            "Capture trajectory fingerprints differ.",
            "Capture trajectory frame counts do not match their authored contracts.",
            "Capture trajectory routes differ."
        ];

        Assert.Multiple(() =>
        {
            for (int index = 0; index < mismatches.Length; index++)
            {
                SampleBenchmarkPairComparison comparison =
                    SampleBenchmarkPairComparer.Compare(
                        baseline,
                        mismatches[index],
                        requireRepeatability: false);
                Assert.That(comparison.Comparable, Is.False, expectedFailures[index]);
                Assert.That(
                    comparison.Failures,
                    Does.Contain(expectedFailures[index]));
            }
        });
    }

    [Test]
    public void Compare_RejectsUnavailableTrajectoryEvidence()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:state-a",
            "baseline",
            10.0,
            4.0);
        SampleBenchmarkReport missing = baseline with
        {
            CaptureContract = baseline.CaptureContract with
            {
                TrajectoryFingerprint = "unavailable",
                TrajectoryRouteHash = "unavailable",
                TrajectorySequenceHash = "unavailable"
            }
        };

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                missing,
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.False);
            Assert.That(
                comparison.Failures,
                Does.Contain("Capture trajectory fingerprints are missing or invalid."));
            Assert.That(
                comparison.Failures,
                Does.Contain("Capture trajectory route hashes are missing or invalid."));
            Assert.That(
                comparison.Failures,
                Does.Contain("Capture trajectory sequence hashes are missing or invalid."));
        });
    }

    [Test]
    public void Compare_RejectsIdenticallyForgedNonSponzaAnimationEvidence()
    {
        SampleBenchmarkReport report = CreateReport(
            "forged-unavailable-animation",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0);
        string sidecarPath = Path.GetFullPath("forged-sponza-animation.bin");
        SampleBenchmarkSponzaSceneAnimationEvidence forgedEvidence =
            SampleBenchmarkSponzaSceneAnimationEvidence.Unavailable with
            {
                ConfigurationFingerprint = Sha256('a'),
                SequenceHash = Sha256('b'),
                SidecarPath = sidecarPath,
                SidecarSha256 = new string('c', 64)
            };
        SampleBenchmarkCaptureContract forgedContract =
            report.CaptureContract with
            {
                SponzaSceneAnimationConfigurationFingerprint = Sha256('a'),
                SponzaSceneAnimationSequenceHash = Sha256('b'),
                SponzaSceneAnimationSidecarSha256 = new string('c', 64)
            };
        SampleBenchmarkReport left = report with
        {
            SponzaSceneAnimationEvidence = forgedEvidence,
            CaptureContract = forgedContract
        };
        SampleBenchmarkReport right = left with
        {
            CapturedAtUtc = left.CapturedAtUtc.AddSeconds(1)
        };

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(left, right);
        SampleBenchmarkReport nullCollection = report with
        {
            SponzaSceneAnimationEvidence =
                SampleBenchmarkSponzaSceneAnimationEvidence.Unavailable with
                {
                    Failures = null!
                }
        };
        SampleBenchmarkPairComparison nullComparison =
            SampleBenchmarkPairComparer.Compare(
                nullCollection,
                nullCollection);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.False);
            Assert.That(
                comparison.Failures,
                Has.Some.Contains("canonical unavailable Sponza animation"));
            Assert.That(nullComparison.Comparable, Is.False);
            Assert.That(
                nullComparison.Failures,
                Has.Some.Contains("canonical unavailable Sponza animation"));
        });
    }

    [Test]
    public void Compare_RejectsIgnoredNoActivationAggregatesAndNullLists()
    {
        SampleBenchmarkReport report = CreateReport(
            "forged-no-activation",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0);
        SampleBenchmarkActivationEvidence canonical =
            report.ActivationEvidence;
        SampleBenchmarkActivationEvidence[] forged =
        [
            canonical with
            {
                ReflectionSubmittedWorkFrameCount = 1,
                ReflectionCompletedDelta = 1
            },
            canonical with
            {
                DirectionalStaticReuseFrameCount = 1,
                DirectionalTruthfulCacheFrameCount = 1
            },
            canonical with
            {
                ForwardSuppressedFrameCount = 1,
                ForwardDisabledPipelineFrameCount = 1
            },
            canonical with
            {
                ReflectionRequests = null!
            }
        ];

        Assert.Multiple(() =>
        {
            foreach (SampleBenchmarkActivationEvidence evidence in forged)
            {
                SampleBenchmarkReport forgedReport = report with
                {
                    ActivationEvidence = evidence
                };
                SampleBenchmarkPairComparison comparison =
                    SampleBenchmarkPairComparer.Compare(
                        forgedReport,
                        forgedReport);
                Assert.That(comparison.Comparable, Is.False);
                Assert.That(
                    comparison.Failures,
                    Has.Some.Contains(
                        "no-activation report contains activation work evidence"));
            }
        });
    }

    [Test]
    public void Compare_ObservedSequenceIsExactForSameRoleAndMayDifferForIntentionalVariantAb()
    {
        SampleBenchmarkReport baseline = WithMovingTrajectory(CreateReport(
            "locked-pair",
            "sha256:state-a",
            "baseline",
            10.0,
            4.0));
        SampleBenchmarkReport changedSequence = baseline with
        {
            CaptureContract = baseline.CaptureContract with
            {
                TrajectorySequenceHash = Sha256('e')
            }
        };

        SampleBenchmarkPairComparison repeat =
            SampleBenchmarkPairComparer.Compare(baseline, changedSequence);
        SampleBenchmarkPairComparison sameRoleCrossBuild =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                changedSequence,
                requireRepeatability: false);
        SampleBenchmarkPairComparison ab =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                changedSequence with
                {
                    CaptureContract = changedSequence.CaptureContract with
                    {
                        Variant =
                            SampleBenchmarkCaptureVariant.FarFieldForcedOld
                    }
                },
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(repeat.Comparable, Is.False);
            Assert.That(
                repeat.Failures,
                Does.Contain("Capture trajectory sequences differ."));
            Assert.That(sameRoleCrossBuild.Comparable, Is.False);
            Assert.That(
                sameRoleCrossBuild.Failures,
                Does.Contain("Capture trajectory sequences differ."));
            Assert.That(ab.Comparable, Is.True);
        });
    }

    [Test]
    public void Compare_ModelsDisabledPassAsZeroForAbButRejectsItForRepeat()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0);
        SampleBenchmarkReport disabled = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "decals-disabled",
            8.0,
            0.0) with
        {
            GpuPasses = []
        };

        SampleBenchmarkPairComparison repeat =
            SampleBenchmarkPairComparer.Compare(baseline, disabled);
        SampleBenchmarkPairComparison ab =
            SampleBenchmarkPairComparer.Compare(
                baseline,
                disabled,
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(repeat.Comparable, Is.False);
            Assert.That(
                repeat.Failures,
                Does.Contain("Repeat is missing baseline GPU pass 'ForwardPlusPass'."));
            Assert.That(ab.Comparable, Is.True);
            Assert.That(
                ab.Metrics.Single(metric => metric.Name == "ForwardPlusPass")
                    .VariantP95Milliseconds,
                Is.Zero);
        });
    }

    [Test]
    public void Compare_ReportsButDoesNotGateSubBudgetMinorPasses()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.0),
                Stats("MinorPass", 0.49)
            ]
        };
        SampleBenchmarkReport repeat = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.0),
                Stats("MinorPass", 0.10)
            ]
        };

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(baseline, repeat);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.True);
            Assert.That(
                comparison.Metrics.Single(metric => metric.Name == "MinorPass")
                    .WithinTolerance,
                Is.True);
        });
    }

    [Test]
    public void Compare_GatesPassAtMajorBudgetBoundary()
    {
        SampleBenchmarkReport baseline = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.0),
                Stats("MajorPass", 0.50)
            ]
        };
        SampleBenchmarkReport repeat = CreateReport(
            "locked-pair",
            "sha256:locked-state",
            "baseline",
            10.0,
            4.0) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.0),
                Stats("MajorPass", 0.60)
            ]
        };

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(baseline, repeat);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.False);
            Assert.That(
                comparison.Failures,
                Has.Some.Contains("MajorPass P95 differs"));
        });
    }

    [Test]
    public void Compare_ProducesForwardGiPairedEstimateFromControlledVariants()
    {
        SampleBenchmarkReport disabled = CreateReport(
            "forward-pair",
            "sha256:locked-state",
            SampleBenchmarkCaptureVariant.ForwardGiDisabled,
            10.0,
            4.0) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.0),
                Stats("ForwardGiGatherPass", 4.0)
            ]
        };
        SampleBenchmarkReport enabled = CreateReport(
            "forward-pair",
            "sha256:locked-state",
            SampleBenchmarkCaptureVariant.ForwardGiEnabled,
            10.1,
            4.12) with
        {
            GpuPasses =
            [
                Stats("ForwardPlusPass", 4.12),
                Stats("ForwardGiGatherPass", 4.12)
            ]
        };

        SampleBenchmarkPairComparison comparison =
            SampleBenchmarkPairComparer.Compare(
                disabled,
                enabled,
                requireRepeatability: false);

        Assert.Multiple(() =>
        {
            Assert.That(comparison.Comparable, Is.True);
            Assert.That(comparison.ForwardGiGatherEstimate, Is.Not.Null);
            Assert.That(
                comparison.ForwardGiGatherEstimate!.Attribution,
                Is.EqualTo(GiTimingAttribution.PairedEstimate));
            Assert.That(
                comparison.ForwardGiGatherEstimate.IncrementalP95Milliseconds,
                Is.EqualTo(0.12).Within(1e-9));
        });
    }

    [Test]
    public void PairComparisonCli_WritesBoundedMachineReadableReport()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-pair-comparison",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string baselinePath = Path.Combine(directory, "baseline.json");
        string repeatPath = Path.Combine(directory, "repeat.json");
        string comparisonPath = Path.Combine(directory, "comparison.json");
        File.WriteAllText(
            baselinePath,
            JsonSerializer.Serialize(CreateReport(
                "locked-pair",
                "sha256:locked-state",
                "baseline",
                10.0,
                4.0)));
        File.WriteAllText(
            repeatPath,
            JsonSerializer.Serialize(CreateReport(
                "locked-pair",
                "sha256:locked-state",
                "baseline",
                10.2,
                4.1)));
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkPairComparisonCli.TryRun(
            [
                SampleBenchmarkPairComparisonCli.CompareOption,
                baselinePath,
                repeatPath,
                SampleBenchmarkPairComparisonCli.ReportOption,
                comparisonPath
            ],
            output,
            error,
            out int exitCode);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(File.Exists(comparisonPath), Is.True);
            Assert.That(output.ToString(), Does.Contain("comparison passed"));
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(
                File.ReadAllText(comparisonPath),
                Does.Contain("\"Comparable\": true"));
        });
    }

    [Test]
    public void CaptureVariants_ApplyIndependentDecalAndFarFieldSwitches()
    {
        var settings = new RenderSettings();

        Assert.That(
            SampleBenchmarkCaptureVariant.Apply(
                settings,
                SampleBenchmarkCaptureVariant.DecalShadowsDisabled),
            Is.EqualTo(SampleBenchmarkCaptureVariant.DecalShadowsDisabled));
        Assert.That(settings.Decals.ReceiveShadows, Is.False);

        settings = new RenderSettings();
        Assert.That(
            SampleBenchmarkCaptureVariant.Apply(settings, "decal-material:17"),
            Is.EqualTo("decal-material:17"));
        Assert.That(settings.Decals.IsolatedMaterialIndex, Is.EqualTo(17));

        settings = new RenderSettings();
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.FarFieldForcedOld);
        Assert.That(
            settings.GlobalIllumination
                .SimpleDdgiForceLegacyFarFieldFallbackEvaluation,
            Is.True);

        settings = new RenderSettings();
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.ForwardGiDisabled);
        Assert.That(
            settings.Diagnostics.SuppressForwardGiGatherForBenchmark,
            Is.True);
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.ForwardGiEnabled);
        Assert.That(
            settings.Diagnostics.SuppressForwardGiGatherForBenchmark,
            Is.False);

        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.ForwardGiExact);
        Assert.Multiple(() =>
        {
            Assert.That(
                settings.Diagnostics.ForceExactForwardGiGatherForBenchmark,
                Is.True);
            Assert.That(
                settings.Diagnostics.SuppressForwardGiGatherForBenchmark,
                Is.False);
        });
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.ForwardGiEnabled);
        Assert.That(
            settings.Diagnostics.ForceExactForwardGiGatherForBenchmark,
            Is.False);
    }

    private static SampleBenchmarkReport CreateReport(
        string pairId,
        string identityHash,
        string variant,
        double gpuFrameP95,
        double forwardP95)
    {
        const SamplePerformanceScenario scenario =
            SamplePerformanceScenario.GiSponzaRightWallStationary;
        const SampleBenchmarkTrajectoryKind trajectory =
            SampleBenchmarkTrajectoryKind.Stationary;
        const SampleBistroQualityCaptureVariant bistroVariant =
            SampleBistroQualityCaptureVariant.SunScaleStep;
        RendererDiagnostics lastDiagnostics = RendererDiagnostics.Empty with
        {
            CaptureRun = RendererDiagnostics.Empty.CaptureRun with
            {
                Scenario = scenario.ToString()
            }
        };
        string trajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                trajectory,
                bistroVariant);
        SampleBenchmarkTimingStats cpu = Stats("CPU frame", 5.0);
        SampleBenchmarkTimingStats gpu = Stats("GPU frame", gpuFrameP95);
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            new SampleBenchmarkOptions(
                Enabled: true,
                WarmupFrameCount: 120,
                MeasureFrameCount: 120,
                ReportPath: null)
            {
                Trajectory = trajectory,
                TrajectoryBistroVariant = bistroVariant,
                TrajectoryFingerprint = trajectoryFingerprint
            },
            scenario,
            WarmupFrameCount: 120,
            MeasurementFrameCount: 120,
            FirstMeasurementFrameIndex: 120,
            LastMeasurementFrameIndex: 239,
            CpuFrameMilliseconds: cpu,
            GpuFrameMilliseconds: gpu,
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: 120,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: [Stats("ForwardPlusPass", forwardP95)],
            CpuStages: [],
            Findings: [],
            BudgetMetrics: [],
            LastDiagnostics: lastDiagnostics)
        {
            ActivationEvidence = CanonicalNoActivationEvidence(120),
            CaptureContract = new SampleBenchmarkCaptureContract(
                Comparable: true,
                ProductionTiming: true,
                PairId: pairId,
                Variant: variant,
                IdentityHash: identityHash,
                Mismatches: [])
            {
                FullIdentityHash = identityHash + ":" + variant,
                Trajectory = SampleBenchmarkTrajectory.GetName(trajectory),
                TrajectoryFingerprint = trajectoryFingerprint,
                TrajectoryFrameCount = 1,
                TrajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                    trajectory,
                    bistroVariant,
                    lastDiagnostics.CaptureCamera),
                TrajectorySequenceHash = Sha256('2')
            }
        };
    }

    private static SampleBenchmarkActivationEvidence
        CanonicalNoActivationEvidence(int sampleCount) => new(
            SampleBenchmarkActivationEvidence.CurrentSchema,
            SampleBenchmarkActivation.None,
            SampleBenchmarkActivation.CreateFingerprint(
                SampleBenchmarkActivation.None),
            Passed: true,
            MeasuredSampleCount: sampleCount,
            Failures: Array.Empty<string>());

    private static SampleBenchmarkReport WithMovingTrajectory(
        SampleBenchmarkReport report)
    {
        const SampleBenchmarkTrajectoryKind trajectory =
            SampleBenchmarkTrajectoryKind.BistroLoop;
        const SampleBistroQualityCaptureVariant bistroVariant =
            SampleBistroQualityCaptureVariant.SunScaleStep;
        int frameCount = SampleBenchmarkTrajectory.GetFrameCount(trajectory);
        string fingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
            trajectory,
            bistroVariant);
        return report with
        {
            Scenario = SamplePerformanceScenario.Normal,
            Options = report.Options with
            {
                MeasureFrameCount = frameCount,
                Trajectory = trajectory,
                TrajectoryBistroVariant = bistroVariant,
                TrajectoryFingerprint = fingerprint
            },
            MeasurementFrameCount = frameCount,
            LastMeasurementFrameIndex = checked(
                report.FirstMeasurementFrameIndex + frameCount - 1),
            GpuTimingValidSampleCount = frameCount,
            LastDiagnostics = report.LastDiagnostics with
            {
                CaptureRun = report.LastDiagnostics.CaptureRun with
                {
                    Scenario = SamplePerformanceScenario.Normal.ToString()
                }
            },
            ActivationEvidence = CanonicalNoActivationEvidence(frameCount),
            CaptureContract = report.CaptureContract with
            {
                Trajectory = SampleBenchmarkTrajectory.BistroLoopName,
                TrajectoryFingerprint = fingerprint,
                TrajectoryFrameCount = frameCount,
                TrajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                    trajectory,
                    bistroVariant),
                TrajectorySequenceHash = Sha256('d')
            }
        };
    }

    private static string Sha256(char digit) =>
        "sha256:" + new string(digit, 64);

    private static SampleBenchmarkTimingStats Stats(string name, double p95) =>
        new(name, 120, p95, p95, p95, p95)
        {
            MedianMilliseconds = p95
        };
}
