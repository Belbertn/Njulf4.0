using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkDdgiTransientEvidenceTests
{
    [TestCase(60, 180)]
    [TestCase(61, 181)]
    public void BistroSunStepJoinsTwoExactDelayedTransientWindows(
        int firstEdge,
        int secondEdge)
    {
        int firstCertificate = firstEdge + 3;
        int secondCertificate = secondEdge + 3;
        SampleBenchmarkReport report = CreateReport(
            firstEdge,
            secondEdge,
            firstCertificate,
            secondCertificate);

        SampleBenchmarkDdgiTransientEvidence evidence =
            report.DdgiTransientEvidence;
        Assert.Multiple(() =>
        {
            Assert.That(evidence.Applicable, Is.True);
            Assert.That(evidence.Available, Is.True);
            Assert.That(evidence.Failures, Is.Empty);
            Assert.That(evidence.Windows, Has.Count.EqualTo(2));
            Assert.That(report.CaptureContract.Mismatches.Any(mismatch =>
                mismatch.StartsWith(
                    "DDGI transient evidence unavailable:",
                    StringComparison.Ordinal)), Is.False);
        });

        SampleBenchmarkDdgiTransientWindow first = evidence.Windows[0];
        SampleBenchmarkDdgiTransientWindow second = evidence.Windows[1];
        Assert.Multiple(() =>
        {
            Assert.That(first.AuthoredEventRouteFrameIndex, Is.EqualTo(60));
            Assert.That(first.ObservedGenerationEdgeRouteFrameIndex, Is.EqualTo(firstEdge));
            Assert.That(first.GenerationResponseLatencyFrames, Is.EqualTo(firstEdge - 60));
            Assert.That(first.PreviousSourceLightingGeneration, Is.EqualTo(1u));
            Assert.That(first.SourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(first.AcceptedCertificateRouteFrameIndex, Is.EqualTo(firstCertificate));
            Assert.That(first.CertificateLatencyFrames, Is.EqualTo(3));
            Assert.That(
                first.Frames.Select(frame => frame.RouteFrameIndex),
                Is.EqualTo(Enumerable.Range(firstEdge, 4)));
            Assert.That(
                first.Frames.Select(frame => frame.CompletionObservedMeasurementSampleIndex),
                Is.EqualTo(Enumerable.Range(firstEdge + 2, 4)));
            Assert.That(first.FirstSubmittedFrameSerial, Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(first.LastSubmittedFrameSerial, Is.EqualTo(10_000UL + (ulong)firstCertificate));

            Assert.That(second.AuthoredEventRouteFrameIndex, Is.EqualTo(180));
            Assert.That(second.ObservedGenerationEdgeRouteFrameIndex, Is.EqualTo(secondEdge));
            Assert.That(second.GenerationResponseLatencyFrames, Is.EqualTo(secondEdge - 180));
            Assert.That(second.PreviousSourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(second.SourceLightingGeneration, Is.EqualTo(3u));
            Assert.That(second.AcceptedCertificateRouteFrameIndex, Is.EqualTo(secondCertificate));
            Assert.That(second.Frames, Has.Count.EqualTo(4));
        });

        SampleBenchmarkDdgiTransientFrame exact = first.Frames[2];
        int exactOrigin = firstEdge + 2;
        Assert.Multiple(() =>
        {
            Assert.That(exact.MeasurementSampleIndex, Is.EqualTo(exactOrigin));
            Assert.That(exact.CompletionObservedMeasurementSampleIndex, Is.EqualTo(exactOrigin + 2));
            Assert.That(exact.Completed.Submitted.FrameSerial,
                Is.EqualTo(10_000UL + (ulong)exactOrigin));
            Assert.That(exact.Completed.Submitted.SourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(exact.Completed.Submitted.CachedSweepCount, Is.EqualTo(exactOrigin % 5));
            Assert.That(exact.Completed.GpuAcceleratedSolveMicroseconds,
                Is.EqualTo(100 + exactOrigin));
            Assert.That(exact.Completed.GpuSchedulerTailAdmitMicroseconds,
                Is.EqualTo(200 + exactOrigin));
            Assert.That(exact.Completed.GpuSchedulerEmitMicroseconds,
                Is.EqualTo(300 + exactOrigin));
            Assert.That(exact.Completed.GpuSchedulerCommitMicroseconds,
                Is.EqualTo(400 + exactOrigin));
            Assert.That(exact.Completed.GpuDdgiTotalMicroseconds,
                Is.EqualTo(5_000 + exactOrigin));
            Assert.That(exact.Completed.SchedulerAcceptedWorkCount,
                Is.EqualTo((uint)(50 + exactOrigin)));
            Assert.That(exact.Completed.SchedulerCompactedCandidateCount,
                Is.EqualTo((uint)(70 + exactOrigin)));
            Assert.That(exact.Completed.SchedulerActiveWorkCount,
                Is.EqualTo((uint)(10 + exactOrigin)));
        });
    }

    [Test]
    public void MissingCompletedFrameFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            missingOrigin: 62);

        AssertUnavailable(
            report,
            "missing completed evidence for route frame 62");
    }

    [Test]
    public void WindowOverlappingNextGenerationEdgeFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: -1,
            secondCertificate: 184);

        AssertUnavailable(report, "overlapped the next source-lighting edge");
    }

    [Test]
    public void UncompletedFinalWindowFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: -1);

        AssertUnavailable(report, "did not complete with an accepted current tail certificate");
    }

    [Test]
    public void OutOfRouteGenerationEdgeFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            extraEdge: 100);

        AssertUnavailable(
            report,
            "expected exactly two source-lighting generation edges");
    }

    [Test]
    public void RendererFrameSerialZeroIsAValidJoinIdentity()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            serialBase: 0UL);

        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientEvidence.Available, Is.True);
            Assert.That(report.DdgiTransientEvidence.Failures.Any(failure =>
                failure.Contains("frame serial", StringComparison.Ordinal)), Is.False);
            Assert.That(report.DdgiTransientEvidence.Windows[0].FirstSubmittedFrameSerial,
                Is.EqualTo(60UL));
        });
    }

    [TestCase("commit", 61, "scheduler-commit GPU timing")]
    [TestCase("accelerated", 62, "cached sweeps without accelerated-solve GPU timing")]
    [TestCase("tail", 62, "scheduler candidates without tail-admit GPU timing")]
    [TestCase("emit", 62, "scheduler work without emit GPU timing")]
    public void ActiveWorkRequiresItsExactPerTargetTiming(
        string timing,
        int originIndex,
        string expectedFailure)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            missingTimingOrigin: originIndex,
            missingTiming: timing);

        AssertUnavailable(report, expectedFailure);
    }

    [Test]
    public void InactiveTargetPassesMayRetainUnavailableZeroTimings()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            inactiveTimingOrigin: 62);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True);
        SampleDdgiCompletedFrameEvidenceAssertions.AssertInactiveTargets(
            report.DdgiTransientEvidence.Windows[0].Frames[2].Completed);
    }

    private static void AssertUnavailable(
        SampleBenchmarkReport report,
        string expectedFailure)
    {
        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientEvidence.Applicable, Is.True);
            Assert.That(report.DdgiTransientEvidence.Available, Is.False);
            Assert.That(
                report.DdgiTransientEvidence.Failures,
                Has.Some.Contains(expectedFailure));
            Assert.That(report.CaptureContract.Comparable, Is.False);
            Assert.That(
                report.CaptureContract.Mismatches,
                Has.Some.Contains(
                    "DDGI transient evidence unavailable: "));
        });
    }

    private static SampleBenchmarkReport CreateReport(
        int firstEdge,
        int secondEdge,
        int firstCertificate,
        int secondCertificate,
        int missingOrigin = -1,
        int extraEdge = -1,
        ulong serialBase = 10_000UL,
        int missingTimingOrigin = -1,
        string missingTiming = "",
        int inactiveTimingOrigin = -1)
    {
        const int frameCount = SampleBistroQualityCaptureContract.LoopFrameCount;
        const int completionDelay = 2;
        var analyzer = new SampleBenchmarkAnalyzer();
        for (int sampleIndex = 0; sampleIndex < frameCount; sampleIndex++)
        {
            int completedOrigin = sampleIndex - completionDelay;
            SimpleDdgiCompletedFrameEvidence completed = completedOrigin >= 0
                ? completedOrigin == missingOrigin
                    ? default
                    : CreateCompleted(
                        completedOrigin,
                        SourceGeneration(
                            completedOrigin,
                            firstEdge,
                            secondEdge,
                            extraEdge),
                        completedOrigin == firstCertificate ||
                        completedOrigin == secondCertificate,
                        serialBase)
                : CreateCompleted(
                    originIndex: -100 + sampleIndex,
                    sourceGeneration: 1u,
                    certificate: false,
                    serialBase);
            if (completedOrigin == missingTimingOrigin)
            {
                completed = missingTiming switch
                {
                    "commit" => completed with
                    {
                        GpuSchedulerCommitTimingAvailable = false
                    },
                    "accelerated" => completed with
                    {
                        GpuAcceleratedSolveTimingAvailable = false
                    },
                    "tail" => completed with
                    {
                        GpuSchedulerTailAdmitTimingAvailable = false
                    },
                    "emit" => completed with
                    {
                        GpuSchedulerEmitTimingAvailable = false
                    },
                    _ => completed
                };
            }
            if (completedOrigin == inactiveTimingOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        CachedSweepCount = 0
                    },
                    GpuAcceleratedSolveTimingAvailable = false,
                    GpuSchedulerTailAdmitTimingAvailable = false,
                    GpuSchedulerEmitTimingAvailable = false,
                    GpuAcceleratedSolveMicroseconds = 0,
                    GpuSchedulerTailAdmitMicroseconds = 0,
                    GpuSchedulerEmitMicroseconds = 0,
                    SchedulerCompactedCandidateCount = 0,
                    SchedulerAcceptedWorkCount = 0,
                    SchedulerCommittedWorkCount = 0,
                    SchedulerPublishedWorkCount = 0,
                    SchedulerActiveWorkCount = 0
                };
            }
            SampleBenchmarkCameraPose pose =
                SampleBenchmarkTrajectory.ResolveCamera(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    sampleIndex,
                    SampleBistroQualityCaptureVariant.SunScaleStep)!;
            var camera = new PerformanceCaptureCameraMetadata(
                pose.Position.X,
                pose.Position.Y,
                pose.Position.Z,
                pose.Yaw,
                pose.Pitch,
                pose.FieldOfView,
                pose.NearPlane,
                pose.FarPlane,
                "synthetic-view",
                "synthetic-projection",
                0UL);
            analyzer.AddSample(
                RendererDiagnostics.Empty with
                {
                    CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
                    {
                        FrameSerial = serialBase + (ulong)sampleIndex,
                        FramesSinceSceneLoad = (ulong)sampleIndex,
                        WarmupState = DdgiRuntimeWarmupState.SteadyState
                    },
                    CaptureCamera = camera,
                    SimpleDdgiSourceLightingGeneration = SourceGeneration(
                        sampleIndex,
                        firstEdge,
                        secondEdge,
                        extraEdge),
                    SimpleDdgiCompletedFrameEvidence = completed
                },
                RenderBudgetSnapshot.Empty);
        }

        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: frameCount,
            ReportPath: null)
        {
            Trajectory = SampleBenchmarkTrajectoryKind.BistroLoop,
            TrajectoryBistroVariant = SampleBistroQualityCaptureVariant.SunScaleStep,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.BistroLoop,
                SampleBistroQualityCaptureVariant.SunScaleStep)
        };
        return analyzer.CreateReport(
            options,
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: frameCount,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: frameCount - 1);
    }

    private static uint SourceGeneration(
        int routeFrameIndex,
        int firstEdge,
        int secondEdge,
        int extraEdge)
    {
        if (extraEdge >= 0 && routeFrameIndex >= extraEdge && routeFrameIndex < secondEdge)
            return 99u;
        if (routeFrameIndex >= secondEdge)
            return 3u;
        return routeFrameIndex >= firstEdge ? 2u : 1u;
    }

    private static SimpleDdgiCompletedFrameEvidence CreateCompleted(
        int originIndex,
        uint sourceGeneration,
        bool certificate,
        ulong serialBase)
    {
        ulong frameSerial = originIndex >= 0
            ? serialBase + (ulong)originIndex
            : 9_000UL + (ulong)(originIndex + 100);
        uint transportGeneration = sourceGeneration + 100u;
        var generations = new SimpleDdgiTransportGenerations(
            VolumeTable: 6u,
            PhysicalOwnership: 6u,
            SourceLighting: sourceGeneration,
            SourceEpoch: 8u,
            TransportOperator: 9u,
            CanonicalField: transportGeneration,
            Solve: 11u,
            Audit: 12u,
            Queue: 13u,
            SchedulerResources: 14u);
        var submitted = new SimpleDdgiSubmittedFrameEvidence
        {
            Valid = true,
            FrameSlot = Math.Abs(originIndex) % 2,
            FrameSerial = frameSerial,
            GpuTimingRecorded = true,
            SchedulerMode = SimpleDdgiSchedulerMode.GpuResident,
            ActiveProbeCount = 2_048,
            VolumeResourceGeneration = 5u,
            TransportTopologyGeneration = 6u,
            SourceLightingGeneration = sourceGeneration,
            AdmittedSourceCohortGeneration = sourceGeneration,
            TransportGeneration = transportGeneration,
            PublishedPropagationGeneration = transportGeneration,
            LivePropagationSourceGeneration = sourceGeneration,
            SchedulerResourceGeneration = 14u,
            CachedSweepCount = Math.Max(0, originIndex) % 5,
            TailCertificationEnabled = true,
            TailCertificate = new SimpleDdgiTailCertificateFrameEvidence
            {
                Phase = certificate
                    ? SimpleDdgiTransportPhase.Certified
                    : SimpleDdgiTransportPhase.AcceleratedSolve,
                Reason = certificate
                    ? SimpleDdgiTransportCertificationReason.Certified
                    : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete,
                Generations = generations,
                SolveEpoch = 20u,
                AuditEpoch = 21u,
                ExpectedParticipantCount = 2_048u,
                AuditedParticipantCount = certificate ? 2_048u : 0u,
                ExpectedTexelCount = 131_072u,
                AuditedTexelCount = certificate ? 131_072u : 0u,
                AuditComplete = certificate,
                CertificateCurrent = certificate
            }
        };
        int exactIndex = Math.Max(0, originIndex);
        return new SimpleDdgiCompletedFrameEvidence
        {
            Valid = true,
            Submitted = submitted,
            GpuTimingAvailable = true,
            GpuAcceleratedSolveTimingAvailable = true,
            GpuSchedulerTailAdmitTimingAvailable = true,
            GpuSchedulerEmitTimingAvailable = true,
            GpuSchedulerCommitTimingAvailable = true,
            GpuDdgiTotalTimingAvailable = true,
            GpuAcceleratedSolveMicroseconds = 100 + exactIndex,
            GpuSchedulerTailAdmitMicroseconds = 200 + exactIndex,
            GpuSchedulerEmitMicroseconds = 300 + exactIndex,
            GpuSchedulerCommitMicroseconds = 400 + exactIndex,
            GpuDdgiTotalMicroseconds = 5_000 + exactIndex,
            SchedulerFeedbackAvailable = true,
            SchedulerFeedbackFrameAligned = true,
            SchedulerFeedbackGenerationAligned = true,
            SchedulerFeedbackFrameSerial = frameSerial,
            SchedulerFeedbackVolumeResourceGeneration = 5u,
            SchedulerFeedbackSchedulerResourceGeneration = 14u,
            SchedulerFeedbackSourceLightingGeneration = sourceGeneration,
            SchedulerFeedbackTransportGeneration = transportGeneration,
            SchedulerCompactedCandidateCount = (uint)(70 + exactIndex),
            SchedulerAcceptedWorkCount = (uint)(50 + exactIndex),
            SchedulerCommittedWorkCount = (uint)(40 + exactIndex),
            SchedulerPublishedWorkCount = (uint)(30 + exactIndex),
            SchedulerActiveWorkCount = (uint)(10 + exactIndex),
            SchedulerCachedParticipantCount = (uint)(5 + exactIndex),
            SchedulerCachedRayCount = (uint)(1_000 + exactIndex)
        };
    }

    private static class SampleDdgiCompletedFrameEvidenceAssertions
    {
        public static void AssertInactiveTargets(
            SimpleDdgiCompletedFrameEvidence completed)
        {
            Assert.Multiple(() =>
            {
                Assert.That(completed.GpuDdgiTotalTimingAvailable, Is.True);
                Assert.That(completed.GpuSchedulerCommitTimingAvailable, Is.True);
                Assert.That(completed.GpuAcceleratedSolveTimingAvailable, Is.False);
                Assert.That(completed.GpuSchedulerTailAdmitTimingAvailable, Is.False);
                Assert.That(completed.GpuSchedulerEmitTimingAvailable, Is.False);
                Assert.That(completed.GpuAcceleratedSolveMicroseconds, Is.Zero);
                Assert.That(completed.GpuSchedulerTailAdmitMicroseconds, Is.Zero);
                Assert.That(completed.GpuSchedulerEmitMicroseconds, Is.Zero);
            });
        }
    }
}
