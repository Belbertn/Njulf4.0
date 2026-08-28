using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed partial class SampleBenchmarkDdgiTransientEvidenceTests
{
    private const int ValidFirstEdge = 60;
    private const int ValidSecondEdge = 180;
    private const int ValidFirstCertificate =
        ValidFirstEdge + CertificateOffset;
    private const int ValidSecondCertificate =
        ValidSecondEdge + CertificateOffset;

    [Test]
    public void FrozenEvaluatorAcceptsExactRawRowsAndRecomputesBothWindows()
    {
        SampleBenchmarkReport report = CreateValidTransientReport();

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientRawEvidence.Schema, Is.EqualTo(
                SampleBenchmarkDdgiTransientRawEvidence.CurrentSchema));
            Assert.That(report.DdgiTransientRawEvidence.Applicable, Is.True);
            Assert.That(
                report.DdgiTransientRawEvidence.MeasurementFrameCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(report.DdgiTransientRawEvidence.Frames,
                Has.Count.EqualTo(
                    SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Select(
                    static frame => frame.Schema),
                Is.All.EqualTo(
                    SampleBenchmarkDdgiTransientRawFrame.CurrentSchema));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Select(
                    static frame => frame.MeasurementSampleIndex),
                Is.EqualTo(Enumerable.Range(
                    0,
                    SampleBistroQualityCaptureContract.LoopFrameCount)));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Select(
                    static frame => frame.RouteFrameIndex),
                Is.EqualTo(Enumerable.Range(
                    0,
                    SampleBistroQualityCaptureContract.LoopFrameCount)));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Select(
                    static frame => frame.CaptureFrameSerial),
                Is.EqualTo(Enumerable.Range(
                        0,
                        SampleBistroQualityCaptureContract.LoopFrameCount)
                    .Select(static index => 10_000UL + (ulong)index)));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Select(
                    static frame => frame.Active),
                Is.All.EqualTo(1));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames[
                        ValidFirstEdge + RenderingConstants.FramesInFlight]
                    .CompletionObserved.Valid,
                Is.True);
            Assert.That(
                report.DdgiTransientRawEvidence.Frames[0]
                    .CompletionObserved.Submitted.FrameSerial,
                Is.EqualTo(9_998UL));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames[1]
                    .CompletionObserved.Submitted.FrameSerial,
                Is.EqualTo(9_999UL));
            Assert.That(
                report.DdgiTransientRawEvidence.Frames.Take(2).Select(
                    static frame =>
                        frame.CompletionObserved.Submitted.FrameSlot),
                Is.EqualTo(new[] { 0, 1 }));
            Assert.That(verification.Passed, Is.True,
                string.Join(Environment.NewLine, verification.Failures));
            Assert.That(verification.Failures, Is.Empty);
            Assert.That(verification.RawRowCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(verification.RecomputedEvidence.Applicable, Is.True);
            Assert.That(verification.RecomputedEvidence.Available, Is.True);
            Assert.That(verification.RecomputedEvidence.Windows,
                Has.Count.EqualTo(2));
            Assert.That(
                verification.RecomputedEvidence.Windows.Sum(
                    static window => window.Frames.Count),
                Is.EqualTo(2 * (CertificateOffset + 1)));
            Assert.That(verification.SemanticDigest,
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void VerificationCliBindsResultToExactAdmittedReportBytes()
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        string reportPath = WriteReport(report, "valid-report.json");
        byte[] exactBytes = File.ReadAllBytes(reportPath);
        string exactSha256 = Convert.ToHexString(
            SHA256.HashData(exactBytes)).ToLowerInvariant();
        SampleBenchmarkDdgiTransientVerification expected =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.Zero, run.Error);
            Assert.That(run.Error, Is.Empty);
            Assert.That(run.Result, Is.Not.Null);
            Assert.That(run.Result!.Kind, Is.EqualTo(
                SampleBenchmarkDdgiTransientVerificationResult.CurrentKind));
            Assert.That(run.Result.Schema, Is.EqualTo(
                SampleBenchmarkDdgiTransientVerificationResult.CurrentSchema));
            Assert.That(run.Result.Passed, Is.True,
                string.Join(Environment.NewLine, run.Result.Failures));
            Assert.That(run.Result.ReportPath,
                Is.EqualTo(Path.GetFullPath(reportPath)));
            Assert.That(run.Result.ReportSha256, Is.EqualTo(exactSha256));
            Assert.That(run.Result.ReportByteLength,
                Is.EqualTo(exactBytes.LongLength));
            Assert.That(run.Result.Applicable, Is.True);
            Assert.That(run.Result.Available, Is.True);
            Assert.That(run.Result.RawRowCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(run.Result.RecomputedWindowCount, Is.EqualTo(2));
            Assert.That(run.Result.RecomputedWindowFrameCount,
                Is.EqualTo(2 * (CertificateOffset + 1)));
            Assert.That(run.Result.SemanticDigest,
                Is.EqualTo(expected.SemanticDigest));
            Assert.That(run.Result.Failures, Is.Empty);
        });
    }

    [Test]
    public void NonApplicableEvidenceHasOneExactCanonicalShape()
    {
        SampleBenchmarkReport report = CreateCanonicalNonApplicableReport();

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);
        string reportPath = WriteReport(report, "not-applicable.json");
        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.True,
                string.Join(Environment.NewLine, verification.Failures));
            Assert.That(report.DdgiTransientRawEvidence.Schema, Is.EqualTo(
                SampleBenchmarkDdgiTransientRawEvidence.CurrentSchema));
            Assert.That(report.DdgiTransientRawEvidence.Applicable, Is.False);
            Assert.That(report.DdgiTransientRawEvidence.MeasurementFrameCount,
                Is.Zero);
            Assert.That(report.DdgiTransientRawEvidence.Frames, Is.Empty);
            Assert.That(report.DdgiTransientEvidence.Schema, Is.EqualTo(
                SampleBenchmarkDdgiTransientEvidence.CurrentSchema));
            Assert.That(report.DdgiTransientEvidence.Applicable, Is.False);
            Assert.That(report.DdgiTransientEvidence.Available, Is.False);
            Assert.That(report.DdgiTransientEvidence.Failures, Is.Empty);
            Assert.That(report.DdgiTransientEvidence.Windows, Is.Empty);
            Assert.That(run.ExitCode, Is.Zero, run.Error);
            Assert.That(run.Result!.Applicable, Is.False);
            Assert.That(run.Result.Available, Is.False);
            Assert.That(run.Result.RawRowCount, Is.Zero);
            Assert.That(run.Result.RecomputedWindowCount, Is.Zero);
            Assert.That(run.Result.RecomputedWindowFrameCount, Is.Zero);
            Assert.That(run.Result.SemanticDigest,
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void StationaryBistroScenarioIsCanonicalButNotTransientApplicable()
    {
        SampleBenchmarkReport report = CreateCanonicalNonApplicableReport();
        report = report with
        {
            Scenario = SamplePerformanceScenario.BistroQualityMotionRelight,
            LastDiagnostics = report.LastDiagnostics with
            {
                CaptureRun = report.LastDiagnostics.CaptureRun with
                {
                    Scenario = SamplePerformanceScenario
                        .BistroQualityMotionRelight.ToString()
                }
            }
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.True,
                string.Join(Environment.NewLine, verification.Failures));
            Assert.That(verification.RecomputedEvidence.Applicable, Is.False);
            Assert.That(verification.SemanticDigest,
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [TestCase("omitted-row")]
    [TestCase("extra-row")]
    [TestCase("reordered-rows")]
    [TestCase("wrong-index")]
    [TestCase("noncontiguous-serial")]
    [TestCase("serial-sentinel")]
    [TestCase("zero-generation")]
    [TestCase("inactive-outside-windows")]
    [TestCase("missing-completion")]
    [TestCase("topology-tamper")]
    public void EvaluatorRejectsRawRowIdentityAndSemanticTamper(string mutation)
    {
        SampleBenchmarkReport original = CreateValidTransientReport();
        SampleBenchmarkReport tampered = MutateRaw(original, mutation);

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(tampered);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures, Is.Not.Empty, mutation);
        });
    }

    [Test]
    public void EvaluatorRejectsStoredWindowTamperAgainstRawRecomputation()
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientWindow[] windows =
            report.DdgiTransientEvidence.Windows.ToArray();
        windows[0] = windows[0] with
        {
            ResponseClosureRouteFrameIndex =
                windows[0].ResponseClosureRouteFrameIndex + 1
        };
        report = report with
        {
            DdgiTransientEvidence = report.DdgiTransientEvidence with
            {
                Windows = Array.AsReadOnly(windows)
            }
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);
        string reportPath = WriteReport(report, "stored-window-tamper.json");
        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"));
            Assert.That(verification.Failures,
                Has.Some.Contains("does not exactly match"));
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Error, Is.Empty);
            Assert.That(run.Result, Is.Not.Null);
            Assert.That(run.Result!.Passed, Is.False);
            Assert.That(run.Result.Applicable, Is.True);
            Assert.That(run.Result.Available, Is.True);
            Assert.That(run.Result.RawRowCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(run.Result.RecomputedWindowCount, Is.EqualTo(2));
            Assert.That(run.Result.RecomputedWindowFrameCount,
                Is.EqualTo(2 * (CertificateOffset + 1)));
            Assert.That(run.Result.SemanticDigest, Is.EqualTo("unavailable"));
            Assert.That(run.Result.Failures,
                Has.Some.Contains("does not exactly match"));
        });
    }

    [TestCase("negative-timing")]
    [TestCase("outside-window-topology")]
    public void EvaluatorRejectsCoherentlyLaunderedRawCompletionTamper(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientRawFrame[] frames =
            report.DdgiTransientRawEvidence.Frames.ToArray();
        int completionRow = mutation == "negative-timing" ? 10 : 12;
        SimpleDdgiCompletedFrameEvidence completion =
            frames[completionRow].CompletionObserved;
        Assert.That(completion.Valid, Is.True);
        completion = mutation switch
        {
            "negative-timing" => completion with
            {
                GpuDdgiTotalMicroseconds = -1
            },
            "outside-window-topology" => completion with
            {
                Submitted = completion.Submitted with
                {
                    TransportTopologyGeneration =
                        completion.Submitted.TransportTopologyGeneration + 1u
                },
                SchedulerFeedbackTransportTopologyGeneration =
                    completion.SchedulerFeedbackTransportTopologyGeneration + 1u
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        frames[completionRow] = frames[completionRow] with
        {
            CompletionObserved = completion
        };
        SampleBenchmarkDdgiTransientRawEvidence raw =
            report.DdgiTransientRawEvidence with
            {
                Frames = Array.AsReadOnly(frames)
            };
        SampleBenchmarkDdgiTransientEvidence laundered =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(raw);
        report = report with
        {
            DdgiTransientRawEvidence = raw,
            DdgiTransientEvidence = laundered
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);
        CliRun run = RunCli(WriteReport(
            report,
            "coherently-laundered-completion.json"));
        IReadOnlyList<string> authenticatedFailures =
            SampleBenchmarkPairComparer.ValidateAuthenticatedEvidence(report);

        Assert.Multiple(() =>
        {
            Assert.That(laundered.Applicable, Is.True);
            Assert.That(laundered.Available, Is.False);
            Assert.That(laundered.Windows, Is.Empty);
            Assert.That(laundered.Failures,
                Has.Some.Contains(
                    mutation == "negative-timing"
                        ? "negative GPU timing"
                        : "tail generations/digest"));
            Assert.That(verification.Passed, Is.False);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"));
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Error, Is.Empty);
            Assert.That(run.Result!.Passed, Is.False);
            Assert.That(run.Result.Available, Is.False);
            Assert.That(run.Result.RecomputedWindowCount, Is.Zero);
            Assert.That(run.Result.SemanticDigest,
                Is.EqualTo("unavailable"));
            Assert.That(authenticatedFailures,
                Has.Some.StartsWith("DDGI transient evidence: ")
                    .And.Contains(
                        mutation == "negative-timing"
                            ? "negative GPU timing"
                            : "tail generations/digest"));
        });
    }

    [TestCase("valid-with-invalid-submitted")]
    [TestCase("warmup-zero-missing")]
    [TestCase("warmup-one-missing")]
    [TestCase("warmup-wrong-delay")]
    [TestCase("warmup-wrong-slot")]
    [TestCase("warmup-duplicate-submission")]
    public void EvaluatorRejectsInvalidWarmupCompletionOwnership(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientRawFrame[] frames =
            report.DdgiTransientRawEvidence.Frames.ToArray();
        SimpleDdgiCompletedFrameEvidence first = frames[0].CompletionObserved;
        Assert.That(first.Valid, Is.True);
        switch (mutation)
        {
            case "valid-with-invalid-submitted":
                first = first with
                {
                    Submitted = first.Submitted with { Valid = false }
                };
                break;
            case "warmup-zero-missing":
                first = default;
                break;
            case "warmup-one-missing":
                frames[1] = frames[1] with
                {
                    CompletionObserved = default
                };
                break;
            case "warmup-wrong-delay":
                first = first with
                {
                    Submitted = first.Submitted with
                    {
                        FrameSerial = first.Submitted.FrameSerial + 1UL
                    }
                };
                break;
            case "warmup-wrong-slot":
                first = first with
                {
                    Submitted = first.Submitted with
                    {
                        FrameSlot = (first.Submitted.FrameSlot + 1) %
                            RenderingConstants.FramesInFlight
                    }
                };
                break;
            case "warmup-duplicate-submission":
                frames[1] = frames[1] with
                {
                    CompletionObserved = first
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        if (mutation is not "warmup-duplicate-submission" and
            not "warmup-one-missing")
            frames[0] = frames[0] with { CompletionObserved = first };
        report = LaunderRaw(report, frames);

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures,
                Has.Some.Contains(
                    mutation == "valid-with-invalid-submitted"
                        ? "submitted identity is invalid"
                        : mutation is "warmup-zero-missing" or
                            "warmup-one-missing"
                            ? "missing the exact FramesInFlight-delayed"
                        : mutation == "warmup-wrong-slot"
                            ? "retained frame slot"
                            : "FramesInFlight observation delay"));
        });
    }

    [TestCase("ordinary-recording")]
    [TestCase("ordinary-pass-availability")]
    [TestCase("source-repair-availability")]
    [TestCase("audit-dispatch-availability")]
    [TestCase("audit-await-total")]
    public void EvaluatorRejectsNoncanonicalCompleteDerivedPassShape(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientRawFrame[] frames =
            report.DdgiTransientRawEvidence.Frames.ToArray();
        int origin = mutation switch
        {
            "ordinary-recording" or "ordinary-pass-availability" => 10,
            "source-repair-availability" => ValidFirstEdge,
            "audit-dispatch-availability" =>
                ValidFirstCertificate - 3,
            "audit-await-total" => ValidFirstCertificate - 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        int row = origin + RenderingConstants.FramesInFlight;
        SimpleDdgiCompletedFrameEvidence completed =
            frames[row].CompletionObserved;
        completed = mutation switch
        {
            "ordinary-recording" => completed with
            {
                Submitted = completed.Submitted with
                {
                    GpuTimingRecorded = false
                }
            },
            "ordinary-pass-availability" => completed with
            {
                GpuAcceleratedSolveTimingAvailable = false
            },
            "source-repair-availability" => completed with
            {
                GpuAcceleratedSolveTimingAvailable = true,
                GpuAcceleratedSolveMicroseconds = 1
            },
            "audit-dispatch-availability" => completed with
            {
                GpuScheduleTimingAvailable = true
            },
            "audit-await-total" => completed with
            {
                GpuDdgiTotalMicroseconds = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        frames[row] = frames[row] with { CompletionObserved = completed };
        report = LaunderRaw(report, frames);

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures,
                Has.Some.Contains(
                    mutation == "ordinary-recording"
                        ? "did not record the exact submitted GPU timing set"
                        : mutation == "audit-await-total"
                            ? "retained a DDGI total"
                            : "availability"));
        });
    }

    [TestCase("source-partition")]
    [TestCase("accepted-partition")]
    [TestCase("active-work")]
    [TestCase("considered-order")]
    [TestCase("compacted-order")]
    [TestCase("committed-order")]
    [TestCase("published-order")]
    [TestCase("cached-rays")]
    [TestCase("solve-visited")]
    public void EvaluatorRejectsNoncanonicalSchedulerCounterEquations(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientRawFrame[] frames =
            report.DdgiTransientRawEvidence.Frames.ToArray();
        int row = 10 + RenderingConstants.FramesInFlight;
        SimpleDdgiCompletedFrameEvidence completed =
            frames[row].CompletionObserved;
        completed = mutation switch
        {
            "source-partition" => completed with
            {
                SchedulerHardSourceParticipantCount =
                    completed.SchedulerHardSourceParticipantCount + 1u
            },
            "accepted-partition" => completed with
            {
                SchedulerAcceptedWorkCount =
                    completed.SchedulerAcceptedWorkCount + 1u
            },
            "active-work" => completed with
            {
                SchedulerActiveWorkCount =
                    completed.SchedulerActiveWorkCount - 1u
            },
            "considered-order" => completed with
            {
                SchedulerConsideredCandidateCount =
                    completed.SchedulerCompactedCandidateCount - 1u
            },
            "compacted-order" => completed with
            {
                SchedulerCompactedCandidateCount =
                    completed.SchedulerAcceptedWorkCount - 1u
            },
            "committed-order" => completed with
            {
                SchedulerCommittedWorkCount =
                    completed.SchedulerAcceptedWorkCount + 1u
            },
            "published-order" => completed with
            {
                SchedulerPublishedWorkCount =
                    completed.SchedulerCommittedWorkCount + 1u
            },
            "cached-rays" => completed with
            {
                SchedulerCachedRayCount =
                    completed.SchedulerCachedRayCount + 1u
            },
            "solve-visited" => completed with
            {
                SchedulerSolveVisitedCount =
                    completed.SchedulerSolveParticipantCount + 1u
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        frames[row] = frames[row] with { CompletionObserved = completed };
        report = LaunderRaw(report, frames);

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures,
                Has.Some.Contains("scheduler counter equations/order"));
        });
    }

    [TestCase("raw-final-completion")]
    [TestCase("diagnostics-frame-serial")]
    [TestCase("diagnostics-active")]
    [TestCase("diagnostics-generation")]
    [TestCase("gpu-supported")]
    [TestCase("gpu-valid-count")]
    [TestCase("last-gpu-valid")]
    public void EvaluatorRejectsFinalDiagnosticsAndGpuTimingDivergence(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        if (mutation == "raw-final-completion")
        {
            SampleBenchmarkDdgiTransientRawFrame[] frames =
                report.DdgiTransientRawEvidence.Frames.ToArray();
            SimpleDdgiCompletedFrameEvidence completed =
                frames[^1].CompletionObserved;
            frames[^1] = frames[^1] with
            {
                CompletionObserved = completed with
                {
                    SchedulerConsideredCandidateCount =
                        completed.SchedulerConsideredCandidateCount + 1u
                }
            };
            report = LaunderRaw(report, frames);
        }
        else
        {
            report = mutation switch
            {
                "diagnostics-frame-serial" => report with
                {
                    LastDiagnostics = report.LastDiagnostics with
                    {
                        CaptureFrame = report.LastDiagnostics.CaptureFrame with
                        {
                            FrameSerial = report.LastDiagnostics.CaptureFrame
                                .FrameSerial + 1UL
                        }
                    }
                },
                "diagnostics-active" => report with
                {
                    LastDiagnostics = report.LastDiagnostics with
                    {
                        SimpleDdgiActive = 0
                    }
                },
                "diagnostics-generation" => report with
                {
                    LastDiagnostics = report.LastDiagnostics with
                    {
                        SimpleDdgiSourceLightingGeneration =
                            report.LastDiagnostics
                                .SimpleDdgiSourceLightingGeneration + 1u
                    }
                },
                "gpu-supported" => report with { GpuTimingSupported = 0 },
                "gpu-valid-count" => report with
                {
                    GpuTimingValidSampleCount =
                        report.GpuTimingValidSampleCount - 1
                },
                "last-gpu-valid" => report with
                {
                    LastDiagnostics = report.LastDiagnostics with
                    {
                        GpuTimingValid = 0
                    }
                },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
        }

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures,
                Has.Some.Contains(
                    mutation.StartsWith("gpu", StringComparison.Ordinal) ||
                    mutation == "last-gpu-valid"
                        ? "exact GPU timing support"
                        : "raw row 239"));
        });
    }

    [TestCase("stationary-option-only")]
    [TestCase("normal-scenario-only")]
    [TestCase("capture-run-scenario")]
    public void EvaluatorRejectsApplicabilityIdentityRelabeling(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkOptions stationaryOptions = report.Options with
        {
            Trajectory = SampleBenchmarkTrajectoryKind.Stationary,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.Stationary,
                report.Options.TrajectoryBistroVariant)
        };
        report = mutation switch
        {
            "stationary-option-only" => report with
            {
                Options = stationaryOptions,
                DdgiTransientRawEvidence =
                    SampleBenchmarkDdgiTransientRawEvidence.NotApplicable,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidence.NotApplicable
            },
            "normal-scenario-only" => report with
            {
                Scenario = SamplePerformanceScenario.Normal,
                DdgiTransientRawEvidence =
                    SampleBenchmarkDdgiTransientRawEvidence.NotApplicable,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidence.NotApplicable
            },
            "capture-run-scenario" => report with
            {
                LastDiagnostics = report.LastDiagnostics with
                {
                    CaptureRun = report.LastDiagnostics.CaptureRun with
                    {
                        Scenario = SamplePerformanceScenario.Normal.ToString()
                    }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures, Is.Not.Empty, mutation);
        });
    }

    [TestCase("scheduler-mode")]
    [TestCase("pass-mask")]
    [TestCase("nondefault-timing")]
    public void EvaluatorRejectsUndefinedNestedValuesInIgnoredCompletion(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        SampleBenchmarkDdgiTransientRawFrame[] frames =
            report.DdgiTransientRawEvidence.Frames.ToArray();
        SimpleDdgiCompletedFrameEvidence ignored =
            frames[0].CompletionObserved with { Valid = false };
        ignored = mutation switch
        {
            "scheduler-mode" => ignored with
            {
                Submitted = ignored.Submitted with
                {
                    SchedulerMode = (SimpleDdgiSchedulerMode)byte.MaxValue
                }
            },
            "pass-mask" => ignored with
            {
                CompletedGpuTimingPasses =
                    (SimpleDdgiGpuPassMask)(1u << 31)
            },
            "nondefault-timing" => ignored with
            {
                GpuDdgiTotalMicroseconds = 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        frames[0] = frames[0] with { CompletionObserved = ignored };
        SampleBenchmarkDdgiTransientRawEvidence raw =
            report.DdgiTransientRawEvidence with
            {
                Frames = Array.AsReadOnly(frames)
            };
        report = report with
        {
            DdgiTransientRawEvidence = raw,
            DdgiTransientEvidence =
                SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(raw)
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures,
                Has.Some.Contains(
                    mutation == "scheduler-mode"
                        ? "undefined enum value"
                        : mutation == "pass-mask"
                            ? "unknown pass bits"
                            : "canonical default payload"));
        });
    }

    [TestCase("raw-count")]
    [TestCase("raw-frames")]
    [TestCase("derived-available")]
    [TestCase("derived-failure")]
    [TestCase("derived-window")]
    public void EvaluatorRejectsNoncanonicalNonApplicableShapes(
        string mutation)
    {
        SampleBenchmarkReport applicable = CreateValidTransientReport();
        SampleBenchmarkReport report = CreateCanonicalNonApplicableReport();
        report = mutation switch
        {
            "raw-count" => report with
            {
                DdgiTransientRawEvidence =
                    report.DdgiTransientRawEvidence with
                    {
                        MeasurementFrameCount = 1
                    }
            },
            "raw-frames" => report with
            {
                DdgiTransientRawEvidence =
                    report.DdgiTransientRawEvidence with
                    {
                        Frames = applicable.DdgiTransientRawEvidence.Frames
                    }
            },
            "derived-available" => report with
            {
                DdgiTransientEvidence = report.DdgiTransientEvidence with
                {
                    Available = true
                }
            },
            "derived-failure" => report with
            {
                DdgiTransientEvidence = report.DdgiTransientEvidence with
                {
                    Failures = Array.AsReadOnly(["forged"])
                }
            },
            "derived-window" => report with
            {
                DdgiTransientEvidence = report.DdgiTransientEvidence with
                {
                    Windows = applicable.DdgiTransientEvidence.Windows
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures, Is.Not.Empty, mutation);
        });
    }

    [TestCase("report-count")]
    [TestCase("option-count")]
    [TestCase("measurement-bounds")]
    [TestCase("option-fingerprint")]
    [TestCase("contract-route-hash")]
    [TestCase("contract-sequence-hash")]
    [TestCase("undefined-trajectory")]
    [TestCase("undefined-bistro-variant")]
    public void EvaluatorRejectsApplicableReportIdentityTamper(string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        report = mutation switch
        {
            "report-count" => report with
            {
                MeasurementFrameCount = report.MeasurementFrameCount - 1
            },
            "option-count" => report with
            {
                Options = report.Options with
                {
                    MeasureFrameCount = report.Options.MeasureFrameCount - 1
                }
            },
            "measurement-bounds" => report with
            {
                LastMeasurementFrameIndex =
                    report.LastMeasurementFrameIndex - 1
            },
            "option-fingerprint" => report with
            {
                Options = report.Options with
                {
                    TrajectoryFingerprint = "sha256:" + new string('1', 64)
                }
            },
            "contract-route-hash" => report with
            {
                CaptureContract = report.CaptureContract with
                {
                    TrajectoryRouteHash = "sha256:" + new string('2', 64)
                }
            },
            "contract-sequence-hash" => report with
            {
                CaptureContract = report.CaptureContract with
                {
                    TrajectorySequenceHash = "unavailable"
                }
            },
            "undefined-trajectory" => report with
            {
                Options = report.Options with
                {
                    Trajectory = (SampleBenchmarkTrajectoryKind)byte.MaxValue
                },
                DdgiTransientRawEvidence =
                    SampleBenchmarkDdgiTransientRawEvidence.NotApplicable,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidence.NotApplicable
            },
            "undefined-bistro-variant" => report with
            {
                Options = report.Options with
                {
                    TrajectoryBistroVariant =
                        (SampleBistroQualityCaptureVariant)byte.MaxValue
                },
                DdgiTransientRawEvidence =
                    SampleBenchmarkDdgiTransientRawEvidence.NotApplicable,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidence.NotApplicable
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures, Is.Not.Empty, mutation);
        });
    }

    [TestCase("raw-envelope")]
    [TestCase("raw-frame")]
    [TestCase("derived")]
    public void EvaluatorRejectsEveryTransientWireSchemaMismatch(
        string mutation)
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        if (mutation == "raw-envelope")
        {
            SampleBenchmarkDdgiTransientRawEvidence raw =
                report.DdgiTransientRawEvidence with
                {
                    Schema = "njulf-benchmark-ddgi-transient-raw-evidence/v0"
                };
            report = report with
            {
                DdgiTransientRawEvidence = raw,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(raw)
            };
        }
        else if (mutation == "raw-frame")
        {
            SampleBenchmarkDdgiTransientRawFrame[] frames =
                report.DdgiTransientRawEvidence.Frames.ToArray();
            frames[0] = frames[0] with
            {
                Schema = "njulf-benchmark-ddgi-transient-raw-frame/v0"
            };
            SampleBenchmarkDdgiTransientRawEvidence raw =
                report.DdgiTransientRawEvidence with
                {
                    Frames = Array.AsReadOnly(frames)
                };
            report = report with
            {
                DdgiTransientRawEvidence = raw,
                DdgiTransientEvidence =
                    SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(raw)
            };
        }
        else if (mutation == "derived")
        {
            report = report with
            {
                DdgiTransientEvidence = report.DdgiTransientEvidence with
                {
                    Schema = "njulf-benchmark-ddgi-transient-evidence/v0"
                }
            };
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        SampleBenchmarkDdgiTransientVerification verification =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);

        Assert.Multiple(() =>
        {
            Assert.That(verification.Passed, Is.False, mutation);
            Assert.That(verification.SemanticDigest,
                Is.EqualTo("unavailable"), mutation);
            Assert.That(verification.Failures, Is.Not.Empty, mutation);
        });
    }

    [TestCase("omit-raw-root")]
    [TestCase("omit-derived-root")]
    [TestCase("omit-report-schema")]
    [TestCase("omit-report-scenario")]
    [TestCase("omit-report-last-diagnostics")]
    [TestCase("omit-report-gpu-supported")]
    [TestCase("omit-report-gpu-valid-count")]
    [TestCase("omit-report-capture-contract")]
    [TestCase("omit-option-trajectory")]
    [TestCase("omit-option-bistro-variant")]
    [TestCase("undefined-option-trajectory")]
    [TestCase("undefined-option-bistro-variant")]
    [TestCase("omit-raw-frame-schema")]
    [TestCase("omit-derived-window-index")]
    [TestCase("omit-derived-frame-route-index")]
    [TestCase("omit-completed-zero")]
    [TestCase("omit-submitted-zero")]
    [TestCase("omit-tail-zero")]
    [TestCase("omit-summary-zero")]
    [TestCase("omit-generation-member")]
    [TestCase("omit-rgb-member")]
    [TestCase("omit-mismatch-member")]
    [TestCase("null-completion")]
    [TestCase("null-raw-row")]
    [TestCase("unknown-nested")]
    public void VerificationCliFailsClosedForMandatoryWireShapeTamper(
        string mutation)
    {
        string reportPath = WriteReport(
            CreateValidTransientReport(),
            $"nested-{mutation}.json");
        JsonObject root = JsonNode.Parse(File.ReadAllText(reportPath))!
            .AsObject();
        int certificateCompletionIndex = ValidFirstCertificate +
            RenderingConstants.FramesInFlight;
        JsonObject completion = RawFrame(
                root,
                certificateCompletionIndex)["CompletionObserved"]!
            .AsObject();
        JsonObject submitted = completion["Submitted"]!.AsObject();
        JsonObject tail = submitted["TailCertificate"]!.AsObject();
        JsonObject summary = tail["Summary"]!.AsObject();

        switch (mutation)
        {
            case "omit-raw-root":
                root.Remove("DdgiTransientRawEvidence");
                break;
            case "omit-derived-root":
                root.Remove("DdgiTransientEvidence");
                break;
            case "omit-report-schema":
                root.Remove("Schema");
                break;
            case "omit-report-scenario":
                root.Remove("Scenario");
                break;
            case "omit-report-last-diagnostics":
                root.Remove("LastDiagnostics");
                break;
            case "omit-report-gpu-supported":
                root.Remove("GpuTimingSupported");
                break;
            case "omit-report-gpu-valid-count":
                root.Remove("GpuTimingValidSampleCount");
                break;
            case "omit-report-capture-contract":
                root.Remove("CaptureContract");
                break;
            case "omit-option-trajectory":
                root["Options"]!.AsObject().Remove("Trajectory");
                break;
            case "omit-option-bistro-variant":
                root["Options"]!.AsObject()
                    .Remove("TrajectoryBistroVariant");
                break;
            case "undefined-option-trajectory":
                root["Options"]!["Trajectory"] = 255;
                break;
            case "undefined-option-bistro-variant":
                root["Options"]!["TrajectoryBistroVariant"] = 255;
                break;
            case "omit-raw-frame-schema":
                RawFrame(root, 0).Remove("Schema");
                break;
            case "omit-derived-window-index":
                root["DdgiTransientEvidence"]!["Windows"]![0]!
                    .AsObject()
                    .Remove("WindowIndex");
                break;
            case "omit-derived-frame-route-index":
                root["DdgiTransientEvidence"]!["Windows"]![0]!
                    ["Frames"]![0]!
                    .AsObject()
                    .Remove("RouteFrameIndex");
                break;
            case "omit-completed-zero":
                Assert.That(
                    completion["GpuAcceleratedSolveMicroseconds"]!
                        .GetValue<long>(),
                    Is.Zero);
                completion.Remove("GpuAcceleratedSolveMicroseconds");
                break;
            case "omit-submitted-zero":
                Assert.That(
                    submitted["CachedSweepCount"]!.GetValue<int>(),
                    Is.Zero);
                submitted.Remove("CachedSweepCount");
                break;
            case "omit-tail-zero":
                Assert.That(
                    tail["ExcludedStaleSourceCount"]!.GetValue<uint>(),
                    Is.Zero);
                tail.Remove("ExcludedStaleSourceCount");
                break;
            case "omit-summary-zero":
                Assert.That(
                    summary["NonFiniteCount"]!.GetValue<uint>(),
                    Is.Zero);
                summary.Remove("NonFiniteCount");
                break;
            case "omit-generation-member":
                summary["Generations"]!.AsObject().Remove("VolumeTable");
                break;
            case "omit-rgb-member":
                summary["FixedPointDefectChannels"]!
                    .AsObject()
                    .Remove("Red");
                break;
            case "omit-mismatch-member":
                summary["FirstNotResidentIdentity"]!
                    .AsObject()
                    .Remove("VirtualProbeIndex");
                break;
            case "null-completion":
                RawFrame(root, certificateCompletionIndex)
                    ["CompletionObserved"] = null;
                break;
            case "null-raw-row":
                root["DdgiTransientRawEvidence"]!["Frames"]![0] = null;
                break;
            case "unknown-nested":
                submitted["UnknownWireMember"] = 0;
                break;
            default:
                Assert.Fail($"Unknown mutation '{mutation}'.");
                break;
        }

        File.WriteAllText(reportPath, root.ToJsonString());
        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.EqualTo(1), mutation);
            Assert.That(run.Result, Is.Null, mutation);
            Assert.That(run.Output, Is.Empty, mutation);
            Assert.That(run.Error, Is.Not.Empty, mutation);
        });
    }

    [Test]
    public void VerificationCliFailsClosedForExplicitNullRawFrameCollection()
    {
        string reportPath = WriteReport(
            CreateValidTransientReport(),
            "null-raw-frames.json");
        JsonObject root = JsonNode.Parse(File.ReadAllText(reportPath))!
            .AsObject();
        root["DdgiTransientRawEvidence"]!["Frames"] = null;
        File.WriteAllText(reportPath, root.ToJsonString());

        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Output, Is.Empty);
            Assert.That(run.Result, Is.Null);
            Assert.That(run.Error, Does.Contain("Frames is null"));
        });
    }

    [Test]
    public void VerificationCliRejectsDuplicateNestedZeroValuedMember()
    {
        string reportPath = WriteReport(
            CreateValidTransientReport(),
            "duplicate-nested.json");
        string json = File.ReadAllText(reportPath);
        const string member = "\"ExcludedStaleSourceCount\": 0,";
        int memberIndex = json.IndexOf(member, StringComparison.Ordinal);
        Assert.That(memberIndex, Is.GreaterThanOrEqualTo(0));
        json = json.Insert(memberIndex + member.Length, member);
        File.WriteAllText(reportPath, json);

        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Output, Is.Empty);
            Assert.That(run.Error, Does.Contain("duplicate JSON property"));
        });
    }

    [Test]
    public void EveryTransientWireMemberIsExplicitlyRequired()
    {
        Type[] completeWireTypes =
        [
            typeof(SampleBenchmarkDdgiTransientRawEvidence),
            typeof(SampleBenchmarkDdgiTransientRawFrame),
            typeof(SampleBenchmarkDdgiTransientEvidence),
            typeof(SampleBenchmarkDdgiTransientWindow),
            typeof(SampleBenchmarkDdgiTransientFrame),
            typeof(SampleBenchmarkDdgiTransientVerificationResult),
            typeof(SimpleDdgiCompletedFrameEvidence),
            typeof(SimpleDdgiSubmittedFrameEvidence),
            typeof(SimpleDdgiTailCertificateFrameEvidence),
            typeof(SimpleDdgiTransportTailSummary),
            typeof(SimpleDdgiTransportGenerations),
            typeof(SimpleDdgiTransportMismatchIdentity),
            typeof(SimpleDdgiTransportRgbBounds)
        ];
        string[] missing = completeWireTypes
            .SelectMany(static type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property =>
                    property.SetMethod is not null &&
                    property.GetCustomAttribute<JsonIgnoreAttribute>() is null &&
                    property.GetCustomAttribute<JsonRequiredAttribute>() is null)
                .Select(property => $"{type.Name}.{property.Name}"))
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
        PropertyInfo[] reportRoots =
        [
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.Schema))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.Options))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.Scenario))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.GpuTimingSupported))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.GpuTimingValidSampleCount))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.LastDiagnostics))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.CaptureContract))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.DdgiTransientRawEvidence))!,
            typeof(SampleBenchmarkReport).GetProperty(
                nameof(SampleBenchmarkReport.DdgiTransientEvidence))!
        ];
        PropertyInfo[] applicabilityOptions =
        [
            typeof(SampleBenchmarkOptions).GetProperty(
                nameof(SampleBenchmarkOptions.Trajectory))!,
            typeof(SampleBenchmarkOptions).GetProperty(
                nameof(SampleBenchmarkOptions.TrajectoryBistroVariant))!
        ];

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty,
                "Optional transient wire members: " +
                string.Join(", ", missing));
            Assert.That(
                reportRoots.All(static property =>
                    property.GetCustomAttribute<JsonRequiredAttribute>()
                        is not null),
                Is.True,
                "DDGI report identity and both transient roots must be mandatory.");
            Assert.That(
                applicabilityOptions.All(static property =>
                    property.GetCustomAttribute<JsonRequiredAttribute>()
                        is not null),
                Is.True,
                "Both DDGI applicability option members must be mandatory.");
        });
    }

    [Test]
    public void VerificationCliRejectsLegacyBenchmarkSchemaSemantically()
    {
        Assert.That(
            MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema,
            Is.EqualTo("njulf-renderer-benchmark/v5"));
        SampleBenchmarkReport report = CreateValidTransientReport() with
        {
            Schema = "njulf-renderer-benchmark/v3"
        };

        CliRun run = RunCli(WriteReport(report, "legacy-v3-report.json"));

        Assert.Multiple(() =>
        {
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Error, Is.Empty);
            Assert.That(run.Result, Is.Not.Null);
            Assert.That(run.Result!.Passed, Is.False);
            Assert.That(run.Result.SemanticDigest,
                Is.EqualTo("unavailable"));
            Assert.That(run.Result.Failures,
                Has.Some.Contains("kind/schema is not canonical"));
        });
    }

    [Test]
    public void VerificationCliRejectsReadTwiceMutationAndSuppressesDigest()
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        byte[] admittedBytes = JsonSerializer.SerializeToUtf8Bytes(report);
        byte[] finalBytes = [.. admittedBytes, (byte)' ', (byte)'\n'];
        string path = Path.GetFullPath(Path.Combine(
            CreateVerifierDirectory(),
            "read-twice.json"));
        int readCount = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkDdgiTransientVerificationCli.TryRun(
            [
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                path
            ],
            output,
            error,
            _ => Content(
                path,
                readCount++ == 0 ? admittedBytes : finalBytes),
            out int exitCode);
        SampleBenchmarkDdgiTransientVerificationResult result =
            DeserializeResult(output.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(readCount, Is.EqualTo(2));
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(result.Passed, Is.False);
            Assert.That(result.SemanticDigest, Is.EqualTo("unavailable"));
            Assert.That(result.ReportByteLength,
                Is.EqualTo(finalBytes.LongLength));
            Assert.That(result.ReportSha256,
                Is.EqualTo(Content(path, finalBytes).Sha256));
            Assert.That(result.Failures,
                Has.Some.Contains("changed during DDGI transient verification"));
        });
    }

    [TestCase("path")]
    [TestCase("sha256")]
    public void VerificationCliRejectsDelegateIdentityThatDoesNotBindBytes(
        string mutation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            CreateValidTransientReport());
        string path = Path.GetFullPath(Path.Combine(
            CreateVerifierDirectory(),
            "delegate-identity.json"));
        SampleEvidenceFileContent valid = Content(path, bytes);
        int reads = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();

        bool handled = SampleBenchmarkDdgiTransientVerificationCli.TryRun(
            [
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                path
            ],
            output,
            error,
            _ =>
            {
                reads++;
                return mutation == "path"
                    ? valid with { Path = path + ".different" }
                    : valid with { Sha256 = new string('0', 64) };
            },
            out int exitCode);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(output.ToString(), Is.Empty);
            Assert.That(error.ToString(), Is.Not.Empty);
        });
    }

    [Test]
    public void VerificationCliRejectsMalformedArgumentsBeforeReading()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int reads = 0;

        bool handled = SampleBenchmarkDdgiTransientVerificationCli.TryRun(
            [
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                "first.json",
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                "second.json"
            ],
            output,
            error,
            _ =>
            {
                reads++;
                throw new AssertionException("Malformed arguments were read.");
            },
            out int exitCode);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.True);
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(reads, Is.Zero);
            Assert.That(output.ToString(), Is.Empty);
            Assert.That(error.ToString(), Does.Contain("must appear once"));
        });
    }

    [Test]
    public void VerificationCliRejectsReportAboveSixteenMiB()
    {
        string directory = CreateVerifierDirectory();
        string reportPath = Path.Combine(directory, "oversized.json");
        File.WriteAllBytes(
            reportPath,
            new byte[checked((int)SampleEvidenceFileIo.MaximumJsonBytes + 1)]);

        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Result, Is.Null);
            Assert.That(run.Output, Is.Empty);
            Assert.That(run.Error, Does.Contain("bounded limit"));
        });
    }

    [Test]
    public void VerificationCliRejectsJsonDeeperThanAdmissionLimit()
    {
        string reportPath = Path.Combine(
            CreateVerifierDirectory(),
            "too-deep.json");
        File.WriteAllText(
            reportPath,
            new string('[', 65) + "0" + new string(']', 65));

        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(run.Handled, Is.True);
            Assert.That(run.ExitCode, Is.EqualTo(1));
            Assert.That(run.Result, Is.Null);
            Assert.That(run.Output, Is.Empty);
            Assert.That(run.Error, Is.Not.Empty);
        });
    }

    [Test]
    [NonParallelizable]
    public void SemanticDigestIsIndependentOfCurrentCulture()
    {
        SampleBenchmarkReport report = CreateValidTransientReport();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            string arabic = SampleBenchmarkDdgiTransientEvidenceEvaluator
                .Verify(report)
                .SemanticDigest;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nb-NO");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            string norwegian = SampleBenchmarkDdgiTransientEvidenceEvaluator
                .Verify(report)
                .SemanticDigest;

            Assert.That(arabic, Is.EqualTo(norwegian));
            Assert.That(arabic, Does.Match("^sha256:[0-9a-f]{64}$"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Test]
    [NonParallelizable]
    public void ProgramDispatchesVerifierBeforeAnyRendererInitialization()
    {
        string reportPath = WriteReport(
            CreateValidTransientReport(),
            "program-early-exit.json");
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            exitCode = Program.Main(
            [
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                reportPath
            ]);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        SampleBenchmarkDdgiTransientVerificationResult result =
            DeserializeResult(output.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero, error.ToString());
            Assert.That(error.ToString(), Is.Empty);
            Assert.That(result.Passed, Is.True,
                string.Join(Environment.NewLine, result.Failures));
            Assert.That(result.RawRowCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
        });
    }

    [Test]
    public void MaximalCompleteRawReportFitsBoundedSixteenMiBAdmission()
    {
        SampleBenchmarkReport maximal = CreateReport(
            ValidFirstEdge,
            ValidSecondEdge,
            firstCertificate: ValidSecondEdge - 1,
            secondCertificate:
                SampleBistroQualityCaptureContract.LoopFrameCount -
                RenderingConstants.FramesInFlight - 1);
        string reportPath = WriteReport(
            maximal,
            "maximal-complete-raw-report.json");
        long byteLength = new FileInfo(reportPath).Length;
        int derivedFrameCount = maximal.DdgiTransientEvidence.Windows.Sum(
            static window => window.Frames.Count);
        CliRun run = RunCli(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(maximal.DdgiTransientEvidence.Available, Is.True,
                string.Join(
                    Environment.NewLine,
                    maximal.DdgiTransientEvidence.Failures));
            Assert.That(maximal.DdgiTransientEvidence.Windows,
                Has.Count.EqualTo(2));
            Assert.That(
                maximal.DdgiTransientEvidence.Windows[0].Frames,
                Has.Count.EqualTo(ValidSecondEdge - ValidFirstEdge));
            Assert.That(
                maximal.DdgiTransientEvidence.Windows[1].Frames,
                Has.Count.EqualTo(
                    SampleBistroQualityCaptureContract.LoopFrameCount -
                    RenderingConstants.FramesInFlight - ValidSecondEdge));
            Assert.That(derivedFrameCount, Is.EqualTo(178));
            Assert.That(byteLength, Is.GreaterThan(0));
            Assert.That(byteLength,
                Is.LessThanOrEqualTo(SampleEvidenceFileIo.MaximumJsonBytes));
            Assert.That(run.ExitCode, Is.Zero, run.Error);
            Assert.That(run.Result, Is.Not.Null);
            Assert.That(run.Result!.RecomputedWindowFrameCount,
                Is.EqualTo(178));
            Assert.That(
                JsonNode.Parse(File.ReadAllText(reportPath))!
                    ["DdgiTransientRawEvidence"]!["Frames"]!
                    .AsArray(),
                Has.Count.EqualTo(
                    SampleBistroQualityCaptureContract.LoopFrameCount));
        });
    }

    private static SampleBenchmarkReport CreateValidTransientReport() =>
        CreateReport(
            ValidFirstEdge,
            ValidSecondEdge,
            ValidFirstCertificate,
            ValidSecondCertificate);

    private static SampleBenchmarkReport CreateCanonicalNonApplicableReport()
    {
        SampleBenchmarkReport applicable = CreateValidTransientReport();
        SampleBenchmarkOptions options = applicable.Options with
        {
            Trajectory = SampleBenchmarkTrajectoryKind.Stationary,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.Stationary,
                applicable.Options.TrajectoryBistroVariant)
        };
        RendererDiagnostics last = applicable.LastDiagnostics with
        {
            CaptureRun = applicable.LastDiagnostics.CaptureRun with
            {
                Scenario = SamplePerformanceScenario.Normal.ToString()
            }
        };
        SampleBenchmarkCaptureContract contract =
            applicable.CaptureContract with
            {
                Trajectory = SampleBenchmarkTrajectory.StationaryName,
                TrajectoryFingerprint = options.TrajectoryFingerprint,
                TrajectoryFrameCount = 1,
                TrajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                    SampleBenchmarkTrajectoryKind.Stationary,
                    options.TrajectoryBistroVariant,
                    last.CaptureCamera)
            };
        return applicable with
        {
            Options = options,
            Scenario = SamplePerformanceScenario.Normal,
            LastDiagnostics = last,
            CaptureContract = contract,
            DdgiTransientRawEvidence =
                SampleBenchmarkDdgiTransientRawEvidence.NotApplicable,
            DdgiTransientEvidence =
                SampleBenchmarkDdgiTransientEvidence.NotApplicable
        };
    }

    private static SampleBenchmarkReport LaunderRaw(
        SampleBenchmarkReport report,
        SampleBenchmarkDdgiTransientRawFrame[] frames)
    {
        SampleBenchmarkDdgiTransientRawEvidence raw =
            report.DdgiTransientRawEvidence with
            {
                Frames = Array.AsReadOnly(frames)
            };
        return report with
        {
            DdgiTransientRawEvidence = raw,
            DdgiTransientEvidence =
                SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(raw)
        };
    }

    private static SampleBenchmarkReport MutateRaw(
        SampleBenchmarkReport report,
        string mutation)
    {
        SampleBenchmarkDdgiTransientRawEvidence raw =
            report.DdgiTransientRawEvidence;
        SampleBenchmarkDdgiTransientRawFrame[] frames = raw.Frames.ToArray();
        switch (mutation)
        {
            case "omitted-row":
                frames = frames.Skip(1).ToArray();
                break;
            case "extra-row":
                frames =
                [
                    .. frames,
                    frames[^1] with
                    {
                        MeasurementSampleIndex = frames.Length,
                        RouteFrameIndex = frames.Length,
                        CaptureFrameSerial =
                            frames[^1].CaptureFrameSerial + 1UL
                    }
                ];
                break;
            case "reordered-rows":
                (frames[10], frames[11]) = (frames[11], frames[10]);
                break;
            case "wrong-index":
                frames[10] = frames[10] with
                {
                    MeasurementSampleIndex = 11
                };
                break;
            case "noncontiguous-serial":
                frames[10] = frames[10] with
                {
                    CaptureFrameSerial = frames[10].CaptureFrameSerial + 2UL
                };
                break;
            case "serial-sentinel":
                frames[0] = frames[0] with
                {
                    CaptureFrameSerial = ulong.MaxValue
                };
                break;
            case "zero-generation":
                frames[100] = frames[100] with
                {
                    SourceLightingGeneration = 0u
                };
                break;
            case "inactive-outside-windows":
                frames[10] = frames[10] with { Active = 0 };
                break;
            case "missing-completion":
                frames[ValidFirstEdge + RenderingConstants.FramesInFlight] =
                    frames[
                        ValidFirstEdge + RenderingConstants.FramesInFlight]
                    with
                    {
                        CompletionObserved = default
                    };
                break;
            case "topology-tamper":
            {
                int index = ValidFirstEdge +
                    RenderingConstants.FramesInFlight;
                SimpleDdgiCompletedFrameEvidence completion =
                    frames[index].CompletionObserved;
                frames[index] = frames[index] with
                {
                    CompletionObserved = completion with
                    {
                        Submitted = completion.Submitted with
                        {
                            TransportTopologyGeneration =
                                completion.Submitted
                                    .TransportTopologyGeneration + 1u
                        }
                    }
                };
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        return report with
        {
            DdgiTransientRawEvidence = raw with
            {
                Frames = Array.AsReadOnly(frames)
            }
        };
    }

    private static JsonObject RawFrame(JsonObject root, int index) =>
        root["DdgiTransientRawEvidence"]!["Frames"]![index]!.AsObject();

    private static string WriteReport(
        SampleBenchmarkReport report,
        string fileName)
    {
        string reportPath = Path.Combine(CreateVerifierDirectory(), fileName);
        SampleBenchmarkRunner.WriteReport(report, reportPath);
        return reportPath;
    }

    private static string CreateVerifierDirectory()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ddgi-transient-verifier-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static CliRun RunCli(string reportPath)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        bool handled = SampleBenchmarkDdgiTransientVerificationCli.TryRun(
            [
                SampleBenchmarkDdgiTransientVerificationCli.VerifyOption,
                reportPath
            ],
            output,
            error,
            out int exitCode);
        string outputText = output.ToString();
        return new CliRun(
            handled,
            exitCode,
            outputText,
            error.ToString(),
            string.IsNullOrWhiteSpace(outputText)
                ? null
                : DeserializeResult(outputText));
    }

    private static SampleBenchmarkDdgiTransientVerificationResult
        DeserializeResult(string json) =>
        JsonSerializer.Deserialize<
            SampleBenchmarkDdgiTransientVerificationResult>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;

    private static SampleEvidenceFileContent Content(
        string path,
        byte[] bytes) =>
        new(
            Path.GetFullPath(path),
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private sealed record CliRun(
        bool Handled,
        int ExitCode,
        string Output,
        string Error,
        SampleBenchmarkDdgiTransientVerificationResult? Result);
}
