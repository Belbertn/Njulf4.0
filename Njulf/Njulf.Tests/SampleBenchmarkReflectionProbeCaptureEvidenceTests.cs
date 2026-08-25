using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkReflectionProbeCaptureEvidenceTests
{
    private const ulong FirstFrameSerial = 40_000UL;

    [Test]
    public void VerifierRecomputesExactTopEightAndBackwardBudgetJoins()
    {
        SampleBenchmarkReport report = CreateReport();

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.True,
                string.Join(Environment.NewLine, verification.Failures));
            Assert.That(verification.RawRowCount,
                Is.EqualTo(SampleBenchmarkActivation.SponzaActivationFrameCount));
            Assert.That(verification.Digest, Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(
                verification.RecomputedEvidence.SlowestFrames.Select(
                    static frame => frame.MeasurementSampleIndex),
                Is.EqualTo(new[] { 5, 14, 11, 8, 17, 20, 23, 26 }));
            SampleReflectionProbeSlowFrame first =
                verification.RecomputedEvidence.SlowestFrames[0];
            Assert.That(first.SubmittedBudgetAvailable, Is.True);
            Assert.That(first.SubmittedBudgetMeasurementSampleIndex,
                Is.EqualTo(3));
            Assert.That(first.SubmittedBudgetFrameSerial,
                Is.EqualTo(FirstFrameSerial + 3UL));
            Assert.That(first.SubmittedBudget.ReservedMicroseconds,
                Is.EqualTo(
                    report.ReflectionProbeCaptureRawEvidence.Frames[3]
                        .CurrentBudget.ReservedMicroseconds));
            ReflectionProbeGpuBudgetSnapshot firstReplay =
                report.ReflectionProbeCaptureRawEvidence.Frames[2]
                    .CurrentBudget;
            Assert.That(firstReplay.FaceEstimateMicroseconds, Is.EqualTo(313));
            Assert.That(firstReplay.PrefilterEstimateMicroseconds, Is.EqualTo(95));
            Assert.That(firstReplay.CopyEstimateMicroseconds, Is.EqualTo(19));
        });
    }

    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("reordered")]
    [TestCase("index")]
    [TestCase("serial")]
    [TestCase("slot")]
    [TestCase("gpu-valid")]
    [TestCase("timing")]
    [TestCase("current")]
    [TestCase("completed")]
    [TestCase("budget")]
    public void RawRowTamperingFailsClosed(string mutation)
    {
        SampleBenchmarkReport report = CreateReport();
        List<SampleBenchmarkReflectionProbeRawFrame> frames =
            report.ReflectionProbeCaptureRawEvidence.Frames.ToList();
        switch (mutation)
        {
            case "missing":
                frames.RemoveAt(10);
                break;
            case "extra":
                frames.Add(frames[^1]);
                break;
            case "reordered":
                (frames[10], frames[11]) = (frames[11], frames[10]);
                break;
            case "index":
                frames[10] = frames[10] with { MeasurementSampleIndex = 11 };
                break;
            case "serial":
                frames[10] = frames[10] with
                {
                    CaptureFrameSerial = frames[10].CaptureFrameSerial + 1UL
                };
                break;
            case "slot":
                frames[10] = frames[10] with
                {
                    CaptureFrameSlot =
                        (frames[10].CaptureFrameSlot + 1) %
                        RenderingConstants.FramesInFlight
                };
                break;
            case "gpu-valid":
                frames[5] = frames[5] with { GpuTimingValid = 0 };
                break;
            case "timing":
                frames[5] = frames[5] with
                {
                    GpuCaptureMicroseconds =
                        frames[5].GpuCaptureMicroseconds + 1L
                };
                break;
            case "current":
                frames[10] = frames[10] with
                {
                    CurrentLifecycle = frames[10].CurrentLifecycle with
                    {
                        FrameSerial = frames[10].CurrentLifecycle.FrameSerial + 1UL
                    }
                };
                break;
            case "completed":
                frames[5] = frames[5] with
                {
                    CompletedLifecycle = frames[5].CompletedLifecycle with
                    {
                        FrameSlot =
                            (frames[5].CompletedLifecycle.FrameSlot + 1) %
                            RenderingConstants.FramesInFlight
                    }
                };
                break;
            case "budget":
                frames[3] = frames[3] with
                {
                    CurrentBudget = frames[3].CurrentBudget with
                    {
                        ReservedMicroseconds =
                            frames[3].CurrentBudget.ReservedMicroseconds + 1
                    }
                };
                break;
            default:
                Assert.Fail($"Unknown mutation '{mutation}'.");
                break;
        }
        report = report with
        {
            ReflectionProbeCaptureRawEvidence =
                report.ReflectionProbeCaptureRawEvidence with
                {
                    Frames = Array.AsReadOnly(frames.ToArray())
                }
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False, mutation);
    }

    [Test]
    public void CoherentWorstRowGpuInvalidationCannotShiftTheTopEight()
    {
        SampleBenchmarkReport report = CreateReport();
        List<SampleBenchmarkReflectionProbeRawFrame> frames =
            report.ReflectionProbeCaptureRawEvidence.Frames.ToList();
        int worstIndex = report.ReflectionProbeCaptureEvidence
            .SlowestFrames[0]
            .MeasurementSampleIndex;
        frames[worstIndex] = frames[worstIndex] with
        {
            GpuTimingValid = 0
        };
        SampleBenchmarkReflectionProbeRawEvidence raw =
            report.ReflectionProbeCaptureRawEvidence with
            {
                Frames = Array.AsReadOnly(frames.ToArray())
            };
        report = report with
        {
            GpuTimingValidSampleCount =
                SampleBenchmarkActivation.SponzaActivationFrameCount - 1,
            ReflectionProbeCaptureRawEvidence = raw,
            ReflectionProbeCaptureEvidence =
                SampleBenchmarkReflectionProbeCaptureEvaluator.Recompute(raw)
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False);
        Assert.That(verification.Failures,
            Has.Some.Contains("one valid GPU timing sample"));
    }

    [TestCase("budget")]
    [TestCase("estimate")]
    [TestCase("estimate-range")]
    [TestCase("reserved")]
    [TestCase("exhausted")]
    [TestCase("cold-history")]
    [TestCase("history-regression")]
    [TestCase("replay-timing")]
    [TestCase("invalid-completion-timing")]
    public void ImpossibleOrCoherentlyRecomputedPlannerEvidenceFailsClosed(
        string mutation)
    {
        SampleBenchmarkReport report = CreateReport();
        List<SampleBenchmarkReflectionProbeRawFrame> frames =
            report.ReflectionProbeCaptureRawEvidence.Frames.ToList();
        int index = mutation switch
        {
            "cold-history" => 0,
            "replay-timing" => 5,
            _ => 10
        };
        SampleBenchmarkReflectionProbeRawFrame frame = frames[index];
        frames[index] = mutation switch
        {
            "budget" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    BudgetMicroseconds = 499
                }
            },
            "estimate" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    FaceEstimateMicroseconds =
                        frame.CurrentBudget.FaceEstimateMicroseconds + 1
                }
            },
            "estimate-range" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    FaceEstimateMicroseconds = 0
                }
            },
            "reserved" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    ReservedMicroseconds =
                        frame.CurrentBudget.ReservedMicroseconds + 1
                }
            },
            "exhausted" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    BudgetExhausted = !frame.CurrentBudget.BudgetExhausted
                }
            },
            "cold-history" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    FaceEstimateMicroseconds = 101,
                    HasTimingHistory = false
                }
            },
            "history-regression" => frame with
            {
                CurrentBudget = frame.CurrentBudget with
                {
                    HasTimingHistory = false
                }
            },
            "replay-timing" => frame with
            {
                GpuCaptureMicroseconds = frame.GpuCaptureMicroseconds + 500
            },
            "invalid-completion-timing" => frame with
            {
                GpuCaptureMicroseconds = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        SampleBenchmarkReflectionProbeRawEvidence raw =
            report.ReflectionProbeCaptureRawEvidence with
            {
                Frames = Array.AsReadOnly(frames.ToArray())
            };
        report = report with
        {
            ReflectionProbeCaptureRawEvidence = raw,
            ReflectionProbeCaptureEvidence =
                SampleBenchmarkReflectionProbeCaptureEvaluator.Recompute(raw)
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False, mutation);
        Assert.That(verification.Failures,
            Has.Some.Contains("Reflection raw row"), mutation);
    }

    [Test]
    public void FinalRawRowMustMatchReportLastDiagnosticsExactly()
    {
        SampleBenchmarkReport report = CreateReport();
        report = report with
        {
            LastDiagnostics = report.LastDiagnostics with
            {
                ReflectionProbeCurrentCaptureBudget =
                    report.LastDiagnostics.ReflectionProbeCurrentCaptureBudget
                        with
                        {
                            ReservedMicroseconds = 1
                        }
            }
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False);
        Assert.That(verification.Failures,
            Has.Some.Contains("LastDiagnostics"));
    }

    [Test]
    public void AlteredJoinFailsEvenWhenLifecycleDuplicateIsCrossBound()
    {
        SampleBenchmarkReport report = CreateReport();
        List<SampleBenchmarkReflectionProbeRawFrame> rows =
            report.ReflectionProbeCaptureRawEvidence.Frames.ToList();
        List<SampleBenchmarkActivationExecutionFrameEvidence> activation =
            report.ActivationEvidence.ExecutionFrames.ToList();
        ReflectionProbeLifecycleFrameSnapshot changed =
            rows[5].CompletedLifecycle with
            {
                FrameSlot = rows[0].CurrentLifecycle.FrameSlot,
                FrameSerial = rows[0].CurrentLifecycle.FrameSerial
            };
        rows[5] = rows[5] with { CompletedLifecycle = changed };
        activation[5] = activation[5] with
        {
            ReflectionProbeCompletedLifecycle = changed
        };
        report = report with
        {
            ReflectionProbeCaptureRawEvidence =
                report.ReflectionProbeCaptureRawEvidence with
                {
                    Frames = Array.AsReadOnly(rows.ToArray())
                },
            ActivationEvidence = report.ActivationEvidence with
            {
                ExecutionFrames = Array.AsReadOnly(activation.ToArray())
            }
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False);
        Assert.That(verification.Failures,
            Has.Some.Contains("independently recomputed"));
    }

    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("reordered")]
    [TestCase("coherent-timing")]
    [TestCase("lifecycle")]
    [TestCase("budget")]
    public void StoredTopEightTamperingFailsClosed(string mutation)
    {
        SampleBenchmarkReport report = CreateReport();
        List<SampleReflectionProbeSlowFrame> rows =
            report.ReflectionProbeCaptureEvidence.SlowestFrames.ToList();
        switch (mutation)
        {
            case "missing":
                rows.RemoveAt(0);
                break;
            case "extra":
                rows.Add(rows[^1]);
                break;
            case "reordered":
                (rows[0], rows[1]) = (rows[1], rows[0]);
                break;
            case "coherent-timing":
                rows[0] = rows[0] with
                {
                    CompletedGpuMicroseconds =
                        rows[0].CompletedGpuMicroseconds + 1L,
                    GpuCaptureMicroseconds =
                        rows[0].GpuCaptureMicroseconds + 1L
                };
                break;
            case "lifecycle":
                rows[0] = rows[0] with
                {
                    CompletedLifecycle = rows[0].CompletedLifecycle with
                    {
                        FrameSerial =
                            rows[0].CompletedLifecycle.FrameSerial + 1UL
                    }
                };
                break;
            case "budget":
                rows[0] = rows[0] with
                {
                    SubmittedBudget = rows[0].SubmittedBudget with
                    {
                        ReservedMicroseconds =
                            rows[0].SubmittedBudget.ReservedMicroseconds + 1
                    }
                };
                break;
        }
        report = report with
        {
            ReflectionProbeCaptureEvidence =
                report.ReflectionProbeCaptureEvidence with
                {
                    SlowestFrames = Array.AsReadOnly(rows.ToArray())
                }
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False, mutation);
    }

    [Test]
    public void NonReflectionEvidenceRequiresExactCanonicalUnavailableShape()
    {
        SampleBenchmarkReport report = CreateNonReflectionReport();
        SampleBenchmarkReflectionProbeVerification accepted =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);
        SampleBenchmarkReflectionProbeVerification forgedRaw =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(
                report with
                {
                    ReflectionProbeCaptureRawEvidence =
                        SampleBenchmarkReflectionProbeRawEvidence.NotApplicable
                            with { MeasurementFrameCount = 1 }
                });
        SampleBenchmarkReflectionProbeVerification forgedResult =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(
                report with
                {
                    ReflectionProbeCaptureEvidence =
                        SampleReflectionProbeCaptureEvidence.NotApplicable with
                        {
                            Applicable = true
                        }
                });

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Passed, Is.True,
                string.Join(Environment.NewLine, accepted.Failures));
            Assert.That(accepted.RawRowCount, Is.Zero);
            Assert.That(accepted.Digest, Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(forgedRaw.Passed, Is.False);
            Assert.That(forgedResult.Passed, Is.False);
        });
    }

    [TestCase("raw-null")]
    [TestCase("raw-frames-null")]
    [TestCase("raw-schema")]
    [TestCase("result-null")]
    [TestCase("result-frames-null")]
    [TestCase("result-schema")]
    public void ReflectionEvidenceRequiresExactNonNullSchemasAndCollections(
        string mutation)
    {
        SampleBenchmarkReport report = CreateReport();
        report = mutation switch
        {
            "raw-null" => report with
            {
                ReflectionProbeCaptureRawEvidence = null!
            },
            "raw-frames-null" => report with
            {
                ReflectionProbeCaptureRawEvidence =
                    report.ReflectionProbeCaptureRawEvidence with
                    {
                        Frames = null!
                    }
            },
            "raw-schema" => report with
            {
                ReflectionProbeCaptureRawEvidence =
                    report.ReflectionProbeCaptureRawEvidence with
                    {
                        Schema = "forged"
                    }
            },
            "result-null" => report with
            {
                ReflectionProbeCaptureEvidence = null!
            },
            "result-frames-null" => report with
            {
                ReflectionProbeCaptureEvidence =
                    report.ReflectionProbeCaptureEvidence with
                    {
                        SlowestFrames = null!
                    }
            },
            "result-schema" => report with
            {
                ReflectionProbeCaptureEvidence =
                    report.ReflectionProbeCaptureEvidence with
                    {
                        Schema = "forged"
                    }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False, mutation);
    }

    [Test]
    public void ReflectionIdentityRequiresTheExactAuthoredRouteHash()
    {
        SampleBenchmarkReport report = CreateReport();
        report = report with
        {
            CaptureContract = report.CaptureContract with
            {
                TrajectoryRouteHash = Identity('f')
            }
        };

        SampleBenchmarkReflectionProbeVerification verification =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);

        Assert.That(verification.Passed, Is.False);
        Assert.That(verification.Failures,
            Has.Some.Contains("exact authored 300-frame Sponza activation"));
    }

    [Test]
    [NonParallelizable]
    public void VerificationDigestIsIndependentOfCurrentCulture()
    {
        SampleBenchmarkReport report = CreateReport();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            string arabicDigest =
                SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report)
                    .Digest;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nb-NO");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            string norwegianDigest =
                SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report)
                    .Digest;

            Assert.That(arabicDigest, Is.EqualTo(norwegianDigest));
            Assert.That(arabicDigest, Does.Match("^sha256:[0-9a-f]{64}$"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Test]
    public void ActivationVerificationCliRecomputesReflectionRowsFromExactReportBytes()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport report = CreateAuthenticatedReport(directory);
        string reportPath = Path.Combine(directory, "reflection-report.json");
        SampleBenchmarkRunner.WriteReport(report, reportPath);
        byte[] exactBytes = File.ReadAllBytes(reportPath);
        string exactSha256 = Convert.ToHexString(
            SHA256.HashData(exactBytes)).ToLowerInvariant();
        SampleBenchmarkReflectionProbeVerification expected =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Verify(report);
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkActivationVerificationCli.TryRun(
            [
                SampleBenchmarkActivationVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkActivationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkActivationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(result.Passed, Is.True,
                string.Join(Environment.NewLine, result.Failures));
            Assert.That(result.Schema,
                Is.EqualTo(
                    SampleBenchmarkActivationVerificationResult.CurrentSchema));
            Assert.That(result.ReportPath, Is.EqualTo(
                Path.GetFullPath(reportPath)));
            Assert.That(result.ReportSha256, Is.EqualTo(exactSha256));
            Assert.That(result.ReflectionProbeCaptureEvidenceDigest,
                Is.EqualTo(expected.Digest));
            Assert.That(result.ReflectionProbeCaptureRawRowCount,
                Is.EqualTo(SampleBenchmarkActivation.SponzaActivationFrameCount));
            Assert.That(result.ReflectionProbeCaptureResultRowCount,
                Is.EqualTo(SampleReflectionProbeCaptureEvidence.SlowFrameLimit));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    [Test]
    public void ActivationVerificationCliRejectsCoherentStoredTimingTamper()
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport report = CreateAuthenticatedReport(directory);
        List<SampleReflectionProbeSlowFrame> rows =
            report.ReflectionProbeCaptureEvidence.SlowestFrames.ToList();
        rows[0] = rows[0] with
        {
            CompletedGpuMicroseconds = rows[0].CompletedGpuMicroseconds + 1L,
            GpuCaptureMicroseconds = rows[0].GpuCaptureMicroseconds + 1L
        };
        report = report with
        {
            ReflectionProbeCaptureEvidence =
                report.ReflectionProbeCaptureEvidence with
                {
                    SlowestFrames = Array.AsReadOnly(rows.ToArray())
                }
        };
        string reportPath = Path.Combine(directory, "tampered-report.json");
        SampleBenchmarkRunner.WriteReport(report, reportPath);
        string exactSha256 = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(reportPath))).ToLowerInvariant();
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkActivationVerificationCli.TryRun(
            [
                SampleBenchmarkActivationVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);
        SampleBenchmarkActivationVerificationResult result =
            JsonSerializer.Deserialize<
                SampleBenchmarkActivationVerificationResult>(
                output.ToString(),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(result.Passed, Is.False);
            Assert.That(result.ReportSha256, Is.EqualTo(exactSha256));
            Assert.That(result.ReflectionProbeCaptureEvidenceDigest,
                Is.EqualTo("unavailable"));
            Assert.That(result.Failures,
                Has.Some.Contains("Reflection capture evidence"));
            Assert.That(error.ToString(), Is.Empty);
        });
    }

    [TestCase("raw-root")]
    [TestCase("result-root")]
    [TestCase("raw-frames")]
    [TestCase("raw-row-gpu-valid")]
    [TestCase("result-schema")]
    [TestCase("result-applicable")]
    [TestCase("result-row-budget")]
    public void ActivationVerificationCliRejectsOmittedRequiredEvidenceMembers(
        string mutation)
    {
        string directory = CreateDirectory();
        SampleBenchmarkReport report = CreateAuthenticatedReport(directory);
        string reportPath = Path.Combine(directory, "omitted-report.json");
        SampleBenchmarkRunner.WriteReport(report, reportPath);
        JsonObject root = JsonNode.Parse(File.ReadAllText(reportPath))!
            .AsObject();
        switch (mutation)
        {
            case "raw-root":
                root.Remove("ReflectionProbeCaptureRawEvidence");
                break;
            case "result-root":
                root.Remove("ReflectionProbeCaptureEvidence");
                break;
            case "raw-frames":
                root["ReflectionProbeCaptureRawEvidence"]!
                    .AsObject()
                    .Remove("Frames");
                break;
            case "raw-row-gpu-valid":
                root["ReflectionProbeCaptureRawEvidence"]!["Frames"]![0]!
                    .AsObject()
                    .Remove("GpuTimingValid");
                break;
            case "result-schema":
                root["ReflectionProbeCaptureEvidence"]!
                    .AsObject()
                    .Remove("Schema");
                break;
            case "result-applicable":
                root["ReflectionProbeCaptureEvidence"]!
                    .AsObject()
                    .Remove("Applicable");
                break;
            case "result-row-budget":
                root["ReflectionProbeCaptureEvidence"]!["SlowestFrames"]![0]!
                    .AsObject()
                    .Remove("SubmittedBudget");
                break;
            default:
                Assert.Fail($"Unknown mutation '{mutation}'.");
                break;
        }
        File.WriteAllText(reportPath, root.ToJsonString());
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkActivationVerificationCli.TryRun(
            [
                SampleBenchmarkActivationVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(output.ToString(), Is.Empty);
            Assert.That(error.ToString(), Does.Contain("required"));
        });
    }

    private static SampleBenchmarkReport CreateReport()
    {
        SampleBenchmarkOptions options = CreateReflectionOptions();
        RendererDiagnostics[] diagnostics = CreateDiagnostics();
        SampleBenchmarkReflectionProbeRawEvidence raw =
            SampleBenchmarkReflectionProbeCaptureEvaluator.CaptureRaw(
                diagnostics,
                options,
                SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
                diagnostics.Length);
        SampleReflectionProbeCaptureEvidence derived =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Recompute(raw);
        SampleBenchmarkActivationExecutionFrameEvidence[] activationFrames =
            diagnostics.Select(
                    (frame, index) =>
                        SampleBenchmarkActivationExecutionFrameEvidence.Create(
                            index,
                            frame))
                .ToArray();
        SampleBenchmarkTimingStats timing = CreateTiming(diagnostics.Length);
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            options,
            SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
            WarmupFrameCount: 2_688,
            MeasurementFrameCount: diagnostics.Length,
            FirstMeasurementFrameIndex: 2_688,
            LastMeasurementFrameIndex: 2_987,
            CpuFrameMilliseconds: timing,
            GpuFrameMilliseconds: timing,
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: diagnostics.Length,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: Array.Empty<SampleBenchmarkTimingStats>(),
            CpuStages: Array.Empty<SampleBenchmarkTimingStats>(),
            Findings: Array.Empty<SampleBenchmarkFinding>(),
            BudgetMetrics: Array.Empty<BudgetMetric>(),
            LastDiagnostics: diagnostics[^1])
        {
            CaptureContract = CreateReflectionContract(),
            ActivationEvidence = new SampleBenchmarkActivationEvidence(
                SampleBenchmarkActivationEvidence.CurrentSchema,
                SampleBenchmarkActivation.ReflectionRecapture,
                SampleBenchmarkActivation.CreateFingerprint(
                    SampleBenchmarkActivation.ReflectionRecapture),
                Passed: true,
                MeasuredSampleCount: diagnostics.Length,
                Failures: Array.Empty<string>())
            {
                ExecutionFrames = Array.AsReadOnly(activationFrames)
            },
            ReflectionProbeCaptureRawEvidence = raw,
            ReflectionProbeCaptureEvidence = derived
        };
    }

    private static SampleBenchmarkReport CreateAuthenticatedReport(
        string directory)
    {
        RendererDiagnostics[] diagnostics = CreateAuthenticatedDiagnostics(
            out RendererDiagnostics baseline,
            out IReadOnlyDictionary<int,
                ReflectionProbeRecaptureRequestSummary> requests);
        SampleBenchmarkActivationExecutionFrameEvidence baselineFrame =
            SampleBenchmarkActivationExecutionFrameEvidence.Create(-1, baseline);
        SampleBenchmarkActivationExecutionFrameEvidence[] activationFrames =
            diagnostics.Select(
                    (frame, index) =>
                        SampleBenchmarkActivationExecutionFrameEvidence.Create(
                            index,
                            frame))
                .ToArray();
        SampleBenchmarkActivationEvidence activationEvidence =
            SampleBenchmarkActivationEvidenceEvaluator.Evaluate(
                SampleBenchmarkActivation.ReflectionRecapture,
                SampleBenchmarkCaptureVariant.Baseline,
                diagnostics.Length,
                baselineFrame,
                activationFrames,
                requests,
                Array.Empty<SampleBenchmarkActivationFrameState>(),
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                qualitySequence: false);
        if (!activationEvidence.Passed)
        {
            throw new InvalidOperationException(
                "Synthetic authenticated reflection activation failed: " +
                string.Join(Environment.NewLine, activationEvidence.Failures));
        }

        SampleBenchmarkSponzaSceneAnimationBuild animation =
            CreatePhaseZeroAnimationBuild(
                Path.Combine(directory, "sponza-animation.bin"));
        SampleBenchmarkOptions options = CreateReflectionOptions() with
        {
            SponzaFixtureMode = SampleSponzaFixtureMode.AnimationDemo
        };
        SampleBenchmarkReflectionProbeRawEvidence raw =
            SampleBenchmarkReflectionProbeCaptureEvaluator.CaptureRaw(
                diagnostics,
                options,
                SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
                diagnostics.Length);
        SampleReflectionProbeCaptureEvidence derived =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Recompute(raw);
        SampleBenchmarkCaptureContract contract = CreateReflectionContract() with
        {
            SponzaFixtureMode = SampleSponzaFixtureMode.AnimationDemo,
            SponzaSceneAnimationFingerprint = animation.Evidence.Fingerprint,
            SponzaSceneAnimationMode = animation.Evidence.Mode,
            SponzaSceneAnimationConfigurationFingerprint =
                animation.Evidence.ConfigurationFingerprint,
            SponzaSceneAnimationSequenceHash =
                animation.Evidence.SequenceHash,
            SponzaSceneAnimationSidecarSha256 =
                animation.Evidence.SidecarSha256
        };
        SampleBenchmarkTimingStats timing = CreateTiming(diagnostics.Length);
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            options,
            SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle,
            2_688,
            diagnostics.Length,
            2_688,
            2_987,
            timing,
            timing,
            1,
            diagnostics.Length,
            string.Empty,
            Array.Empty<SampleBenchmarkTimingStats>(),
            Array.Empty<SampleBenchmarkTimingStats>(),
            Array.Empty<SampleBenchmarkFinding>(),
            Array.Empty<BudgetMetric>(),
            diagnostics[^1])
        {
            CaptureContract = contract,
            ActivationEvidence = activationEvidence,
            SponzaSceneAnimationEvidence = animation.Evidence,
            ReflectionProbeCaptureRawEvidence = raw,
            ReflectionProbeCaptureEvidence = derived
        };
    }

    private static SampleBenchmarkReport CreateNonReflectionReport()
    {
        SampleBenchmarkOptions options = new(
            Enabled: true,
            WarmupFrameCount: 1,
            MeasureFrameCount: 1,
            ReportPath: null);
        SampleBenchmarkTimingStats timing = CreateTiming(1);
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            options,
            SamplePerformanceScenario.Normal,
            1,
            1,
            1,
            1,
            timing,
            timing,
            1,
            1,
            string.Empty,
            Array.Empty<SampleBenchmarkTimingStats>(),
            Array.Empty<SampleBenchmarkTimingStats>(),
            Array.Empty<SampleBenchmarkFinding>(),
            Array.Empty<BudgetMetric>(),
            RendererDiagnostics.Empty);
    }

    private static SampleBenchmarkOptions CreateReflectionOptions() => new(
        Enabled: true,
        WarmupFrameCount: 2_688,
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

    private static SampleBenchmarkCaptureContract CreateReflectionContract() =>
        new(
            Comparable: true,
            ProductionTiming: true,
            PairId: "reflection-pair",
            Variant: SampleBenchmarkCaptureVariant.Baseline,
            IdentityHash: Identity('a'),
            Mismatches: Array.Empty<string>())
        {
            FullIdentityHash = Identity('b'),
            Trajectory = SampleBenchmarkTrajectory.SponzaHorizontalName,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                SampleBistroQualityCaptureVariant.SunScaleStep),
            TrajectoryFrameCount =
                SampleBenchmarkActivation.SponzaActivationFrameCount,
            TrajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                SampleBistroQualityCaptureVariant.SunScaleStep),
            TrajectorySequenceHash = Identity('d'),
            Activation = SampleBenchmarkActivation.ReflectionRecapture,
            ActivationFingerprint = SampleBenchmarkActivation.CreateFingerprint(
                SampleBenchmarkActivation.ReflectionRecapture)
        };

    private static RendererDiagnostics[] CreateDiagnostics()
    {
        var frames = new RendererDiagnostics[
            SampleBenchmarkActivation.SponzaActivationFrameCount];
        for (int index = 0; index < frames.Length; index++)
        {
            ulong serial = FirstFrameSerial + (ulong)index;
            ReflectionProbeLifecycleFrameSnapshot current = CreateLifecycle(
                index % RenderingConstants.FramesInFlight,
                serial,
                0,
                0,
                0);
            frames[index] = RendererDiagnostics.Empty with
            {
                CaptureRun = RendererDiagnostics.Empty.CaptureRun with
                {
                    Scenario = SamplePerformanceScenario
                        .GiSponzaReflectionProbeLifecycle.ToString()
                },
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    serial,
                    (ulong)index,
                    DdgiRuntimeWarmupState.SteadyState,
                    0,
                    0),
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                ReflectionProbeCount =
                    SampleBenchmarkActivation.SyntheticReflectionProbeCount,
                ReflectionProbeResolution =
                    SampleBenchmarkActivation.SponzaReflectionProbeResolution,
                ReflectionProbeMipCount =
                    SampleBenchmarkActivation.SponzaReflectionProbeMipCount,
                ReflectionProbeCurrentLifecycle = current
            };
        }

        long[] totals =
            [1_000, 9_000, 7_000, 8_000, 9_000, 6_000, 5_000, 4_000, 3_000, 2_000];
        for (int workload = 0; workload < totals.Length; workload++)
        {
            int origin = workload * 3;
            int completion = origin + 2;
            ReflectionProbeLifecycleFrameSnapshot submitted = CreateLifecycle(
                origin % RenderingConstants.FramesInFlight,
                FirstFrameSerial + (ulong)origin,
                workload + 1,
                workload + 11,
                workload + 21);
            ReflectionProbeGpuBudgetSnapshot budget = new(
                1_000 + workload,
                200 + workload,
                100 + workload,
                120 + workload,
                20 + workload,
                HasTimingHistory: true,
                BudgetExhausted: workload % 2 == 0);
            frames[origin] = frames[origin] with
            {
                ReflectionProbeCurrentLifecycle = submitted,
                ReflectionProbeCurrentCaptureBudget = budget
            };
            frames[completion] = frames[completion] with
            {
                GpuReflectionProbeCaptureMicroseconds = totals[workload] - 50,
                GpuReflectionProbePrefilterMicroseconds = 30,
                GpuReflectionProbePublishMicroseconds = 20,
                ReflectionProbeCompletedLifecycle = submitted
            };
        }
        ApplyPlannerSnapshots(frames);
        return frames;
    }

    private static void ApplyPlannerSnapshots(RendererDiagnostics[] frames)
    {
        int faceEstimate = 100;
        int prefilterEstimate = 125;
        int copyEstimate = 25;
        bool hasHistory = true;
        for (int index = 0; index < frames.Length; index++)
        {
            RendererDiagnostics frame = frames[index];
            if (index > 0 &&
                frame.ReflectionProbeCompletedLifecycle is
                {
                    Valid: true,
                    GpuTimingRecorded: true
                } completed)
            {
                ReflectionProbeLifecycleSnapshot work = completed.Lifecycle;
                faceEstimate = ReplayEstimate(
                    faceEstimate,
                    work.CaptureFaceUnitsThisFrame,
                    frame.GpuReflectionProbeCaptureMicroseconds,
                    ref hasHistory);
                prefilterEstimate = ReplayEstimate(
                    prefilterEstimate,
                    work.PrefilterMipUnitsThisFrame,
                    frame.GpuReflectionProbePrefilterMicroseconds,
                    ref hasHistory);
                copyEstimate = ReplayEstimate(
                    copyEstimate,
                    work.PublishCopyUnitsThisFrame,
                    frame.GpuReflectionProbePublishMicroseconds,
                    ref hasHistory);
            }
            ReflectionProbeLifecycleSnapshot current =
                frame.ReflectionProbeCurrentLifecycle.Lifecycle;
            int reserved = checked(
                current.CaptureFaceUnitsThisFrame * faceEstimate +
                current.PrefilterMipUnitsThisFrame * prefilterEstimate +
                current.PublishCopyUnitsThisFrame * copyEstimate);
            frames[index] = frame with
            {
                ReflectionProbeCurrentCaptureBudget = new(
                    500,
                    reserved,
                    faceEstimate,
                    prefilterEstimate,
                    copyEstimate,
                    hasHistory,
                    BudgetExhausted: reserved >= 500)
            };
        }
    }

    private static int ReplayEstimate(
        int previous,
        int unitCount,
        long timing,
        ref bool hasHistory)
    {
        if (unitCount <= 0 || timing <= 0)
            return previous;
        long sample = timing / unitCount;
        if (timing % unitCount != 0)
            sample++;
        sample = Math.Clamp(sample, 1L, 1_000_000L);
        hasHistory = true;
        return (int)Math.Clamp(
            (previous * 3L + sample + 2L) / 4L,
            1L,
            1_000_000L);
    }

    private static RendererDiagnostics[] CreateAuthenticatedDiagnostics(
        out RendererDiagnostics baseline,
        out IReadOnlyDictionary<int,
            ReflectionProbeRecaptureRequestSummary> requests)
    {
        int count = SampleBenchmarkActivation.SponzaActivationFrameCount;
        var frames = new RendererDiagnostics[count];
        var requestMap = new SortedDictionary<int,
            ReflectionProbeRecaptureRequestSummary>();
        int firstSlot = 0;
        ulong startedTotal = 0UL;
        ulong completedTotal = 0UL;
        ulong publishedTotal = 0UL;
        ulong faceTotal = 0UL;
        ulong mipTotal = 0UL;
        ulong copyTotal = 0UL;
        ReflectionProbeLifecycleSnapshot baselineLifecycle = CreateSnapshot(
            queued: 0,
            active: 0,
            ReflectionProbeCaptureState.Published,
            startedThisFrame: 0,
            completedThisFrame: 0,
            faces: 0,
            mips: 0,
            copies: 0,
            startedTotal,
            completedTotal,
            publishedTotal,
            faceTotal,
            mipTotal,
            copyTotal);
        baseline = RendererDiagnostics.Empty with
        {
            CaptureRun = RendererDiagnostics.Empty.CaptureRun with
            {
                Scenario = SamplePerformanceScenario
                    .GiSponzaReflectionProbeLifecycle.ToString()
            },
            ReflectionProbeCount =
                SampleBenchmarkActivation.SyntheticReflectionProbeCount,
            ReflectionProbeResolution =
                SampleBenchmarkActivation.SponzaReflectionProbeResolution,
            ReflectionProbeMipCount =
                SampleBenchmarkActivation.SponzaReflectionProbeMipCount,
            ReflectionProbeCurrentLifecycle = new(
                Valid: true,
                FrameSlot:
                    (firstSlot - 1 + RenderingConstants.FramesInFlight) %
                    RenderingConstants.FramesInFlight,
                FrameSerial: FirstFrameSerial - 1UL,
                GpuTimingRecorded: true,
                baselineLifecycle)
        };

        var submitted = new ReflectionProbeLifecycleFrameSnapshot[count];
        for (int index = 0; index < count; index++)
        {
            int offset = index %
                SampleBenchmarkActivation.ReflectionRecaptureIntervalFrames;
            if (offset == 0)
            {
                ReflectionProbeLifecycleSnapshot before = CreateSnapshot(
                    0,
                    0,
                    ReflectionProbeCaptureState.Published,
                    0,
                    0,
                    0,
                    0,
                    0,
                    startedTotal,
                    completedTotal,
                    publishedTotal,
                    faceTotal,
                    mipTotal,
                    copyTotal);
                ReflectionProbeLifecycleSnapshot after = before with
                {
                    QueuedCount =
                        SampleBenchmarkActivation.SyntheticReflectionProbeCount,
                    State = ReflectionProbeCaptureState.Queued
                };
                requestMap.Add(
                    index,
                    new ReflectionProbeRecaptureRequestSummary(
                        SampleBenchmarkActivation.SyntheticReflectionProbeCount,
                        SampleBenchmarkActivation.SyntheticReflectionProbeCount,
                        0,
                        0,
                        0,
                        before,
                        after));
            }

            int faces = offset < 12 ? 1 : 0;
            int mips = offset is >= 12 and < 26 ? 1 : 0;
            int copies = offset is 26 or 27 ? 1 : 0;
            int started = offset is 0 or 6 ? 1 : 0;
            int completed = copies;
            startedTotal += (ulong)started;
            completedTotal += (ulong)completed;
            publishedTotal += (ulong)copies;
            faceTotal += (ulong)faces;
            mipTotal += (ulong)mips;
            copyTotal += (ulong)copies;
            bool active = offset < 27;
            ReflectionProbeCaptureState state = offset switch
            {
                < 12 => ReflectionProbeCaptureState.CapturingFaces,
                < 26 => ReflectionProbeCaptureState.PrefilteringMips,
                26 => ReflectionProbeCaptureState.CopyReady,
                _ => ReflectionProbeCaptureState.Published
            };
            ReflectionProbeLifecycleSnapshot currentLifecycle = CreateSnapshot(
                queued: 0,
                active: active ? 1 : 0,
                state,
                started,
                completed,
                faces,
                mips,
                copies,
                startedTotal,
                completedTotal,
                publishedTotal,
                faceTotal,
                mipTotal,
                copyTotal);
            int slot = (firstSlot + index) % RenderingConstants.FramesInFlight;
            ulong serial = FirstFrameSerial + (ulong)index;
            ReflectionProbeLifecycleFrameSnapshot current = new(
                Valid: true,
                slot,
                serial,
                GpuTimingRecorded: true,
                currentLifecycle);
            submitted[index] = current;
            ReflectionProbeLifecycleFrameSnapshot completedFrame =
                index >= RenderingConstants.FramesInFlight
                    ? submitted[index - RenderingConstants.FramesInFlight]
                    : default;
            ReflectionProbeLifecycleSnapshot completedWork =
                completedFrame.Lifecycle;
            long capture = completedFrame.Valid
                ? completedWork.CaptureFaceUnitsThisFrame * 100L
                : 0L;
            long prefilter = completedFrame.Valid
                ? completedWork.PrefilterMipUnitsThisFrame * 125L
                : 0L;
            long publish = completedFrame.Valid
                ? completedWork.PublishCopyUnitsThisFrame * 25L
                : 0L;
            frames[index] = RendererDiagnostics.Empty with
            {
                CaptureRun = RendererDiagnostics.Empty.CaptureRun with
                {
                    Scenario = SamplePerformanceScenario
                        .GiSponzaReflectionProbeLifecycle.ToString()
                },
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    serial,
                    (ulong)index,
                    DdgiRuntimeWarmupState.SteadyState,
                    0,
                    0),
                GpuTimingSupported = 1,
                GpuTimingValid = 1,
                GpuReflectionProbeCaptureMicroseconds = capture,
                GpuReflectionProbePrefilterMicroseconds = prefilter,
                GpuReflectionProbePublishMicroseconds = publish,
                ReflectionProbeCount =
                    SampleBenchmarkActivation.SyntheticReflectionProbeCount,
                ReflectionProbeResolution =
                    SampleBenchmarkActivation.SponzaReflectionProbeResolution,
                ReflectionProbeMipCount =
                    SampleBenchmarkActivation.SponzaReflectionProbeMipCount,
                ReflectionProbeCurrentLifecycle = current,
                ReflectionProbeCompletedLifecycle = completedFrame,
                ReflectionProbeCurrentCaptureBudget = new(
                    500,
                    faces * 100 + mips * 125 + copies * 25,
                    100,
                    125,
                    25,
                    HasTimingHistory: true,
                    BudgetExhausted: false)
            };
        }
        requests = requestMap;
        return frames;
    }

    private static ReflectionProbeLifecycleSnapshot CreateSnapshot(
        int queued,
        int active,
        ReflectionProbeCaptureState state,
        int startedThisFrame,
        int completedThisFrame,
        int faces,
        int mips,
        int copies,
        ulong startedTotal,
        ulong completedTotal,
        ulong publishedTotal,
        ulong faceTotal,
        ulong mipTotal,
        ulong copyTotal) => new(
        queued,
        active,
        state,
        AwaitingGpuCompletionCount: 0,
        PublishedCount: SampleBenchmarkActivation.SyntheticReflectionProbeCount,
        startedThisFrame,
        completedThisFrame,
        faces,
        mips,
        copies,
        startedTotal,
        completedTotal,
        publishedTotal,
        faceTotal,
        mipTotal,
        copyTotal);

    private static SampleBenchmarkSponzaSceneAnimationBuild
        CreatePhaseZeroAnimationBuild(string path)
    {
        Scene scene = CreateScene();
        var observer = new SampleBenchmarkSponzaSceneAnimationObserver(
            SampleBenchmarkActivation.SponzaActivationFrameCount,
            SampleBenchmarkActivation.ReflectionRecapture,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal);
        observer.PrepareTimingFrame(
            scene,
            0,
            measurementFrame: false,
            hold: false);
        for (int frame = 0;
             frame < SampleBenchmarkActivation.SponzaActivationFrameCount;
             frame++)
        {
            observer.PrepareTimingFrame(
                scene,
                frame,
                measurementFrame: true,
                hold: false);
            observer.RecordTimingFrame(frame, frame);
        }
        return observer.BuildTiming(path);
    }

    private static Scene CreateScene()
    {
        var scene = new Scene();
        scene.Add(CreateObject(
            SampleBenchmarkSponzaSceneAnimationContract.JointObjectId,
            SampleBenchmarkSponzaSceneAnimationContract.JointName,
            SampleBenchmarkSponzaSceneAnimationContract.JointSubObject,
            CreateAnimator()));
        scene.Add(CreateObject(
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceObjectId,
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceName,
            SampleBenchmarkSponzaSceneAnimationContract.SurfaceSubObject,
            CreateAnimator()));
        return scene;
    }

    private static SkinnedRenderObject CreateObject(
        Guid id,
        string name,
        string subObject,
        Animator animator) => new("mesh", "material")
    {
        Id = id,
        Name = name,
        AssetReference = new SceneAssetReference
        {
            Path = SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
            SubObject = subObject
        },
        SkinIndex = 0,
        Animator = animator
    };

    private static Animator CreateAnimator()
    {
        var joint = new SkeletonJoint
        {
            Name = "Root",
            ParentIndex = -1,
            LocalBindPose = AnimationTransform.Identity,
            LocalBindTransform = Matrix4x4.Identity,
            InverseBindMatrix = Matrix4x4.Identity
        };
        var skeleton = new Skeleton
        {
            Name = "StrutSkeleton",
            Joints = [joint],
            RootJointIndex = 0
        };
        var skin = new Skin
        {
            Name = "StrutSkin",
            Skeleton = skeleton,
            JointIndices = [0],
            InverseBindMatrices = [Matrix4x4.Identity]
        };
        var clip = new AnimationClip
        {
            Name = "StrutMove",
            DurationSeconds = 2f,
            Channels =
            [
                new AnimationChannel
                {
                    TargetJointIndex = 0,
                    Path = AnimationChannelPath.Translation,
                    Sampler = new AnimationSampler
                    {
                        InputTimes = [0f, 2f],
                        OutputValues =
                        [
                            new Vector4(0f, 0f, 0f, 0f),
                            new Vector4(2f, 0f, 0f, 0f)
                        ]
                    }
                }
            ]
        };
        return new Animator(skeleton, [skin], [clip]);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "reflection-verifier-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ReflectionProbeLifecycleFrameSnapshot CreateLifecycle(
        int frameSlot,
        ulong frameSerial,
        int faces,
        int mips,
        int copies) => new(
        Valid: true,
        FrameSlot: frameSlot,
        FrameSerial: frameSerial,
        GpuTimingRecorded: true,
        Lifecycle: new ReflectionProbeLifecycleSnapshot(
            QueuedCount: 0,
            ActiveCount: faces + mips + copies > 0 ? 1 : 0,
            State: faces + mips + copies > 0
                ? ReflectionProbeCaptureState.CapturingFaces
                : ReflectionProbeCaptureState.Published,
            AwaitingGpuCompletionCount: 0,
            PublishedCount: SampleBenchmarkActivation.SyntheticReflectionProbeCount,
            CapturesStartedThisFrame: faces > 0 ? 1 : 0,
            CapturesCompletedThisFrame: copies > 0 ? 1 : 0,
            CaptureFaceUnitsThisFrame: faces,
            PrefilterMipUnitsThisFrame: mips,
            PublishCopyUnitsThisFrame: copies,
            CapturesStartedTotal: frameSerial,
            CapturesCompletedTotal: frameSerial,
            CapturesPublishedTotal: frameSerial,
            CaptureFaceUnitsTotal: (ulong)faces,
            PrefilterMipUnitsTotal: (ulong)mips,
            PublishCopyUnitsTotal: (ulong)copies));

    private static SampleBenchmarkTimingStats CreateTiming(int count) => new(
        "frame",
        count,
        1.0,
        1.0,
        1.0,
        1.0)
    {
        MedianMilliseconds = 1.0,
        P50Milliseconds = 1.0,
        P99Milliseconds = 1.0
    };

    private static string Identity(char value) =>
        "sha256:" + new string(value, 64);
}
