using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkDdgiTransientEvidenceTests
{
    private const SimpleDdgiGpuPassMask OrdinaryBasePasses =
        SimpleDdgiGpuPassMask.Schedule |
        SimpleDdgiGpuPassMask.Trace |
        SimpleDdgiGpuPassMask.RelocateClassify |
        SimpleDdgiGpuPassMask.Publish |
        SimpleDdgiGpuPassMask.SchedulerCommit |
        SimpleDdgiGpuPassMask.ScheduleTailAdmit |
        SimpleDdgiGpuPassMask.ScheduleEmit;

    [TestCase(60, 180)]
    [TestCase(61, 181)]
    public void BistroSunStepJoinsTwoExactDelayedTransientWindows(
        int firstEdge,
        int secondEdge)
    {
        int firstCertificate = firstEdge + 4;
        int secondCertificate = secondEdge + 4;
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
            Assert.That(first.ObservedGenerationEdgeRouteFrameIndex,
                Is.EqualTo(firstEdge));
            Assert.That(first.GenerationResponseLatencyFrames,
                Is.EqualTo(firstEdge - 60));
            Assert.That(first.PreviousSourceLightingGeneration, Is.EqualTo(1u));
            Assert.That(first.SourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(first.AcceptedCertificateRouteFrameIndex,
                Is.EqualTo(firstCertificate));
            Assert.That(first.CertificateLatencyFrames, Is.EqualTo(4));
            Assert.That(
                first.Frames.Select(frame => frame.RouteFrameIndex),
                Is.EqualTo(Enumerable.Range(firstEdge, 5)));
            Assert.That(
                first.Frames.Select(frame =>
                    frame.CompletionObservedMeasurementSampleIndex),
                Is.EqualTo(Enumerable.Range(
                    firstEdge + RenderingConstants.FramesInFlight,
                    5)));
            Assert.That(first.FirstSubmittedFrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(first.LastSubmittedFrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstCertificate));

            Assert.That(second.AuthoredEventRouteFrameIndex, Is.EqualTo(180));
            Assert.That(second.ObservedGenerationEdgeRouteFrameIndex,
                Is.EqualTo(secondEdge));
            Assert.That(second.GenerationResponseLatencyFrames,
                Is.EqualTo(secondEdge - 180));
            Assert.That(second.PreviousSourceLightingGeneration, Is.EqualTo(2u));
            Assert.That(second.SourceLightingGeneration, Is.EqualTo(3u));
            Assert.That(second.AcceptedCertificateRouteFrameIndex,
                Is.EqualTo(secondCertificate));
            Assert.That(second.Frames, Has.Count.EqualTo(5));
        });

        SimpleDdgiCompletedFrameEvidence ordinary = first.Frames[0].Completed;
        Assert.Multiple(() =>
        {
            Assert.That(ordinary.Submitted.FrameSerial,
                Is.EqualTo(10_000UL + (ulong)firstEdge));
            Assert.That(ordinary.Submitted.SourceLightingGeneration,
                Is.EqualTo(2u));
            Assert.That(ordinary.Submitted.CachedSweepCount, Is.EqualTo(2));
            Assert.That(ordinary.GpuAcceleratedSolveMicroseconds,
                Is.EqualTo(100 + firstEdge));
            Assert.That(ordinary.GpuSchedulerTailAdmitMicroseconds,
                Is.EqualTo(200 + firstEdge));
            Assert.That(ordinary.GpuSchedulerEmitMicroseconds,
                Is.EqualTo(300 + firstEdge));
            Assert.That(ordinary.GpuSchedulerCommitMicroseconds,
                Is.EqualTo(400 + firstEdge));
            Assert.That(ordinary.GpuDdgiTotalMicroseconds,
                Is.EqualTo(5_000 + firstEdge));
            Assert.That(ordinary.SchedulerAcceptedWorkCount,
                Is.EqualTo((uint)(50 + firstEdge)));
            Assert.That(ordinary.SchedulerCompactedCandidateCount,
                Is.EqualTo((uint)(70 + firstEdge)));
            Assert.That(ordinary.SchedulerActiveWorkCount,
                Is.EqualTo((uint)(10 + firstEdge)));
        });

        SimpleDdgiCompletedFrameEvidence[] auditFrames = first.Frames
            .Select(static frame => frame.Completed)
            .Where(static completed => completed.Submitted.TailCertificate.Phase ==
                SimpleDdgiTransportPhase.AuditFrozen)
            .ToArray();
        Assert.That(auditFrames, Has.Length.EqualTo(2));
        SimpleDdgiCompletedFrameEvidence auditDispatch = auditFrames.Single(
            static completed => completed.GpuTransportAuditTimingAvailable);
        SimpleDdgiCompletedFrameEvidence auditAwait = auditFrames.Single(
            static completed => !completed.GpuTransportAuditTimingAvailable);
        Assert.Multiple(() =>
        {
            Assert.That(auditDispatch.Submitted.TailCertificate
                .AuditDispatchComplete, Is.True);
            Assert.That(auditDispatch.GpuDdgiTotalTimingAvailable, Is.True);
            Assert.That(auditAwait.GpuDdgiTotalTimingAvailable, Is.False);
            Assert.That(auditAwait.GpuTimingAvailable, Is.False);
            Assert.That(auditAwait.SchedulerFeedbackAvailable, Is.False);
            Assert.That((auditAwait.Submitted.IntendedGpuPasses &
                (SimpleDdgiGpuPassMask.TransportAudit |
                 SimpleDdgiGpuPassMask.Schedule |
                 SimpleDdgiGpuPassMask.SchedulerCommit)),
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
        });
        Assert.That(auditFrames.All(static completed =>
            completed.Submitted.TailCertificate.HasCompleteIdentity &&
            completed.Submitted.TailCertificate.HasDurableSummary &&
            !completed.SchedulerFeedbackAvailable &&
            (completed.Submitted.IntendedGpuPasses &
             (SimpleDdgiGpuPassMask.Schedule |
              SimpleDdgiGpuPassMask.SchedulerCommit)) == 0), Is.True);
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
    public void ReportJsonPersistsAlignedPassAndTailLifecycleEvidence()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184);

        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"IntendedGpuPasses\":"));
            Assert.That(json, Does.Contain("\"AdmittedGpuTimingPasses\":"));
            Assert.That(json, Does.Contain("\"CompletedGpuTimingPasses\":"));
            Assert.That(json, Does.Contain("\"GpuTransportAuditMicroseconds\":"));
            Assert.That(json, Does.Contain("\"SummaryDigest\":"));
            Assert.That(json, Does.Contain("\"FirstFrameSerial\":"));
            Assert.That(json, Does.Contain("\"FinalFrameSerial\":"));
            Assert.That(json, Does.Contain("\"ChunkCount\":"));
        });
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

        AssertUnavailable(
            report,
            "did not complete with an accepted current tail certificate");
    }

    [Test]
    public void GenerationEdgesOutsideAuthoredResponseRangeFailClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 62,
            secondEdge: 182,
            firstCertificate: 66,
            secondCertificate: 186);

        AssertUnavailable(report, "expected [60,61]");
    }

    [Test]
    public void NonSuccessorGenerationSequenceFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            forceBadGenerationSequence: true);

        AssertUnavailable(report, "expected wrap-safe +1 generation");
    }

    [Test]
    public void WrapSafeSuccessorGenerationSequenceIsAccepted()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            initialSourceGeneration: uint.MaxValue);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        Assert.Multiple(() =>
        {
            Assert.That(report.DdgiTransientEvidence.Windows[0]
                .PreviousSourceLightingGeneration, Is.EqualTo(uint.MaxValue));
            Assert.That(report.DdgiTransientEvidence.Windows[0]
                .SourceLightingGeneration, Is.EqualTo(1u));
            Assert.That(report.DdgiTransientEvidence.Windows[1]
                .SourceLightingGeneration, Is.EqualTo(2u));
        });
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

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        Assert.That(report.DdgiTransientEvidence.Windows[0]
            .FirstSubmittedFrameSerial, Is.EqualTo(60UL));
    }

    [TestCase(SimpleDdgiGpuPassMask.Trace)]
    [TestCase(SimpleDdgiGpuPassMask.RelocateClassify)]
    [TestCase(SimpleDdgiGpuPassMask.Publish)]
    [TestCase(SimpleDdgiGpuPassMask.AcceleratedSolve)]
    [TestCase(SimpleDdgiGpuPassMask.ScheduleTailAdmit)]
    [TestCase(SimpleDdgiGpuPassMask.ScheduleEmit)]
    [TestCase(SimpleDdgiGpuPassMask.SchedulerCommit)]
    public void MissingCompletedActivePassFailsClosed(
        SimpleDdgiGpuPassMask missingPass)
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            missingPassOrigin: 60,
            missingCompletedPass: missingPass);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void MissingTimestampAdmissionFailsClosedEvenWhenResultExists()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            missingAdmissionOrigin: 60);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void AuditDispatchWithMissingCompletedAuditTimingFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            missingPassOrigin: 62,
            missingCompletedPass: SimpleDdgiGpuPassMask.TransportAudit);

        AssertUnavailable(
            report,
            "exact intended/admitted/completed DDGI GPU pass coverage");
    }

    [Test]
    public void AuditAwaitRowThatFalselySuppliesAuditWorkFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            falseAuditWorkOnAwaitOrigin: 63);

        AssertUnavailable(report, "final submission serial");
    }

    [Test]
    public void AuditDispatchThatFalselySuppliesOrdinaryWorkFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            falseOrdinaryWorkOnAuditDispatchOrigin: 62);

        AssertUnavailable(report, "scheduler/solve/publication timing scopes");
    }

    [Test]
    public void CompleteLegacyTransportPathMayOmitAcceleratedAndDirectionalPasses()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            legacyPathOrigin: 60);

        Assert.That(report.DdgiTransientEvidence.Available, Is.True,
            string.Join(Environment.NewLine, report.DdgiTransientEvidence.Failures));
        SimpleDdgiCompletedFrameEvidence completed =
            report.DdgiTransientEvidence.Windows[0].Frames[0].Completed;
        Assert.Multiple(() =>
        {
            Assert.That(completed.GpuAcceleratedSolveTimingAvailable, Is.False);
            Assert.That(completed.GpuAcceleratedSolveMicroseconds, Is.Zero);
            Assert.That((completed.CompletedGpuTimingPasses &
                (SimpleDdgiGpuPassMask.Transport |
                 SimpleDdgiGpuPassMask.Blend)),
                Is.EqualTo(SimpleDdgiGpuPassMask.Transport |
                    SimpleDdgiGpuPassMask.Blend));
            Assert.That((completed.CompletedGpuTimingPasses &
                SimpleDdgiGpuPassMask.DirectionalRadiance),
                Is.EqualTo(SimpleDdgiGpuPassMask.None));
        });
    }

    [Test]
    public void NonContiguousRouteSerialFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            shiftedRouteSerialIndex: 61);

        AssertUnavailable(report, "expected contiguous serial");
    }

    [Test]
    public void WrongSubmittedFrameSlotFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            wrongSlotOrigin: 60);

        AssertUnavailable(report, "expected renderer slot");
    }

    [Test]
    public void WrongCompletionDelayFailsClosed()
    {
        SampleBenchmarkReport report = CreateReport(
            firstEdge: 60,
            secondEdge: 180,
            firstCertificate: 64,
            secondCertificate: 184,
            earlyCompletionOrigin: 60);

        AssertUnavailable(report, "expected exact FramesInFlight delay");
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
                Has.Some.Contains("DDGI transient evidence unavailable: "));
        });
    }

    private static SampleBenchmarkReport CreateReport(
        int firstEdge,
        int secondEdge,
        int firstCertificate,
        int secondCertificate,
        int missingOrigin = -1,
        ulong serialBase = 10_000UL,
        uint initialSourceGeneration = 1u,
        bool forceBadGenerationSequence = false,
        int missingPassOrigin = -1,
        SimpleDdgiGpuPassMask missingCompletedPass =
            SimpleDdgiGpuPassMask.None,
        int missingAdmissionOrigin = -1,
        int legacyPathOrigin = -1,
        int shiftedRouteSerialIndex = -1,
        int wrongSlotOrigin = -1,
        int earlyCompletionOrigin = -1,
        int falseAuditWorkOnAwaitOrigin = -1,
        int falseOrdinaryWorkOnAuditDispatchOrigin = -1)
    {
        const int frameCount = SampleBistroQualityCaptureContract.LoopFrameCount;
        var analyzer = new SampleBenchmarkAnalyzer();
        for (int sampleIndex = 0; sampleIndex < frameCount; sampleIndex++)
        {
            int completedOrigin = sampleIndex - RenderingConstants.FramesInFlight;
            if (earlyCompletionOrigin >= 0)
            {
                if (sampleIndex == earlyCompletionOrigin + 1)
                    completedOrigin = earlyCompletionOrigin;
                else if (sampleIndex ==
                         earlyCompletionOrigin + RenderingConstants.FramesInFlight)
                    completedOrigin = int.MinValue;
            }

            SimpleDdgiCompletedFrameEvidence completed;
            if (completedOrigin == int.MinValue || completedOrigin == missingOrigin)
            {
                completed = default;
            }
            else
            {
                bool measuredOrigin = completedOrigin >= 0;
                int syntheticOrigin = measuredOrigin
                    ? completedOrigin
                    : -100 + sampleIndex;
                uint sourceGeneration = measuredOrigin
                    ? SourceGeneration(
                        completedOrigin,
                        firstEdge,
                        secondEdge,
                        initialSourceGeneration,
                        forceBadGenerationSequence)
                    : initialSourceGeneration;
                bool certificate =
                    completedOrigin == firstCertificate ||
                    completedOrigin == secondCertificate;
                bool auditFrozen =
                    firstCertificate >= 0 &&
                    completedOrigin >= firstCertificate - 2 &&
                    completedOrigin < firstCertificate ||
                    secondCertificate >= 0 &&
                    completedOrigin >= secondCertificate - 2 &&
                    completedOrigin < secondCertificate;
                bool auditAwait =
                    firstCertificate >= 0 &&
                    completedOrigin == firstCertificate - 1 ||
                    secondCertificate >= 0 &&
                    completedOrigin == secondCertificate - 1;
                completed = CreateCompleted(
                    syntheticOrigin,
                    sourceGeneration,
                    certificate,
                    auditFrozen,
                    auditAwait,
                    serialBase,
                    legacyPath: completedOrigin == legacyPathOrigin);
            }

            if (completedOrigin == missingPassOrigin)
            {
                completed = RemoveCompletedPass(
                    completed,
                    missingCompletedPass);
            }
            if (completedOrigin == missingAdmissionOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        AdmittedGpuTimingPasses =
                            completed.Submitted.AdmittedGpuTimingPasses &
                            ~SimpleDdgiGpuPassMask.Trace
                    },
                    GpuTimingPassSetAligned = false
                };
            }
            if (completedOrigin == wrongSlotOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        FrameSlot = (completed.Submitted.FrameSlot + 1) %
                            RenderingConstants.FramesInFlight
                    }
                };
            }
            if (completedOrigin == falseAuditWorkOnAwaitOrigin)
            {
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        IntendedGpuPasses =
                            SimpleDdgiGpuPassMask.TransportAudit,
                        AdmittedGpuTimingPasses =
                            SimpleDdgiGpuPassMask.TransportAudit
                    },
                    GpuTimingAvailable = true,
                    GpuTimingPassSetAligned = true,
                    CompletedGpuTimingPasses =
                        SimpleDdgiGpuPassMask.TransportAudit,
                    GpuTransportAuditTimingAvailable = true,
                    GpuDdgiTotalTimingAvailable = true,
                    GpuTransportAuditMicroseconds = 123,
                    GpuDdgiTotalMicroseconds = 123
                };
            }
            if (completedOrigin == falseOrdinaryWorkOnAuditDispatchOrigin)
            {
                SimpleDdgiGpuPassMask falseMask =
                    completed.Submitted.IntendedGpuPasses |
                    SimpleDdgiGpuPassMask.Trace;
                completed = completed with
                {
                    Submitted = completed.Submitted with
                    {
                        IntendedGpuPasses = falseMask,
                        AdmittedGpuTimingPasses = falseMask
                    },
                    GpuTimingPassSetAligned = true,
                    CompletedGpuTimingPasses = falseMask
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
            ulong routeSerial = serialBase + (ulong)sampleIndex;
            if (sampleIndex == shiftedRouteSerialIndex)
                routeSerial++;
            analyzer.AddSample(
                RendererDiagnostics.Empty with
                {
                    CaptureFrame = PerformanceCaptureFrameMetadata.Unknown with
                    {
                        FrameSerial = routeSerial,
                        FramesSinceSceneLoad = (ulong)sampleIndex,
                        WarmupState = DdgiRuntimeWarmupState.SteadyState
                    },
                    CaptureCamera = camera,
                    SimpleDdgiSourceLightingGeneration = SourceGeneration(
                        sampleIndex,
                        firstEdge,
                        secondEdge,
                        initialSourceGeneration,
                        forceBadGenerationSequence),
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
            TrajectoryBistroVariant =
                SampleBistroQualityCaptureVariant.SunScaleStep,
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
        uint initialGeneration,
        bool forceBadGenerationSequence)
    {
        if (routeFrameIndex >= secondEdge)
        {
            return forceBadGenerationSequence
                ? 3u
                : AdvanceNonZero(AdvanceNonZero(initialGeneration));
        }
        if (routeFrameIndex >= firstEdge)
            return forceBadGenerationSequence ? 99u : AdvanceNonZero(initialGeneration);
        return initialGeneration;
    }

    private static uint AdvanceNonZero(uint generation)
    {
        uint next = unchecked(generation + 1u);
        return next == 0u ? 1u : next;
    }

    private static SimpleDdgiCompletedFrameEvidence CreateCompleted(
        int originIndex,
        uint sourceGeneration,
        bool certificate,
        bool auditFrozen,
        bool auditAwait,
        ulong serialBase,
        bool legacyPath)
    {
        ulong frameSerial = originIndex >= 0
            ? serialBase + (ulong)originIndex
            : 9_000UL + (ulong)(originIndex + 100);
        uint transportGeneration = AdvanceNonZero(
            unchecked(sourceGeneration + 100u));
        var generations = new SimpleDdgiTransportGenerations(
            VolumeTable: 6u,
            PhysicalOwnership: 6u,
            SourceLighting: sourceGeneration,
            SourceEpoch: 8u,
            TransportOperator: 9u,
            CanonicalField: transportGeneration,
            Solve: 11u,
            Audit: 12u,
            Queue: 14u,
            SchedulerResources: 14u);
        bool useLegacyPath = legacyPath || certificate;
        SimpleDdgiGpuPassMask passMask = auditFrozen
            ? auditAwait
                ? SimpleDdgiGpuPassMask.None
                : SimpleDdgiGpuPassMask.TransportAudit
            : OrdinaryBasePasses |
              (useLegacyPath
                  ? SimpleDdgiGpuPassMask.Transport |
                    SimpleDdgiGpuPassMask.Blend
                  : SimpleDdgiGpuPassMask.AcceleratedSolve);
        SimpleDdgiTailCertificateFrameEvidence tail = CreateTail(
            generations,
            certificate,
            auditFrozen,
            auditAwait,
            frameSerial);
        var submitted = new SimpleDdgiSubmittedFrameEvidence
        {
            Valid = true,
            FrameSlot = checked((int)(frameSerial %
                (ulong)RenderingConstants.FramesInFlight)),
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
            QueueTransactionGeneration = 14u,
            CachedSweepCount = auditFrozen ? 0 : useLegacyPath ? 1 : 2,
            TailCertificationEnabled = true,
            TailCertificate = tail,
            IntendedGpuPasses = passMask,
            AdmittedGpuTimingPasses = passMask
        };
        int exactIndex = Math.Max(0, originIndex);
        bool accelerated =
            (passMask & SimpleDdgiGpuPassMask.AcceleratedSolve) != 0;
        bool scheduler = !auditFrozen;
        bool auditDispatch = auditFrozen && !auditAwait;
        return new SimpleDdgiCompletedFrameEvidence
        {
            Valid = true,
            Submitted = submitted,
            GpuTimingAvailable = !auditAwait,
            GpuTimingPassSetAligned = true,
            CompletedGpuTimingPasses = passMask,
            GpuScheduleTimingAvailable = scheduler,
            GpuAcceleratedSolveTimingAvailable = accelerated,
            GpuSchedulerTailAdmitTimingAvailable = scheduler,
            GpuSchedulerEmitTimingAvailable = scheduler,
            GpuSchedulerCommitTimingAvailable = scheduler,
            GpuTransportAuditTimingAvailable = auditDispatch,
            GpuDdgiTotalTimingAvailable = !auditAwait,
            GpuAcceleratedSolveMicroseconds = accelerated
                ? 100 + exactIndex
                : 0,
            GpuSchedulerTailAdmitMicroseconds = scheduler
                ? 200 + exactIndex
                : 0,
            GpuSchedulerEmitMicroseconds = scheduler
                ? 300 + exactIndex
                : 0,
            GpuSchedulerCommitMicroseconds = scheduler
                ? 400 + exactIndex
                : 0,
            GpuTransportAuditMicroseconds = auditDispatch
                ? 150 + exactIndex
                : 0,
            GpuDdgiTotalMicroseconds = auditAwait ? 0 : 5_000 + exactIndex,
            SchedulerFeedbackAvailable = scheduler,
            SchedulerFeedbackFrameAligned = scheduler,
            SchedulerFeedbackGenerationAligned = scheduler,
            SchedulerFeedbackFrameSerial = scheduler ? frameSerial : 0UL,
            SchedulerFeedbackVolumeResourceGeneration = scheduler ? 5u : 0u,
            SchedulerFeedbackTransportTopologyGeneration = scheduler ? 6u : 0u,
            SchedulerFeedbackSchedulerResourceGeneration = scheduler ? 14u : 0u,
            SchedulerFeedbackQueueTransactionGeneration = scheduler ? 14u : 0u,
            SchedulerFeedbackSourceLightingGeneration = scheduler
                ? sourceGeneration
                : 0u,
            SchedulerFeedbackTransportGeneration = scheduler
                ? transportGeneration
                : 0u,
            SchedulerCompactedCandidateCount = scheduler
                ? (uint)(70 + exactIndex)
                : 0u,
            SchedulerAcceptedWorkCount = scheduler
                ? (uint)(50 + exactIndex)
                : 0u,
            SchedulerCommittedWorkCount = scheduler
                ? (uint)(40 + exactIndex)
                : 0u,
            SchedulerPublishedWorkCount = scheduler
                ? (uint)(30 + exactIndex)
                : 0u,
            SchedulerActiveWorkCount = scheduler
                ? (uint)(10 + exactIndex)
                : 0u,
            SchedulerCachedParticipantCount = scheduler
                ? (uint)(5 + exactIndex)
                : 0u,
            SchedulerCachedRayCount = scheduler
                ? (uint)(1_000 + exactIndex)
                : 0u
        };
    }

    private static SimpleDdgiTailCertificateFrameEvidence CreateTail(
        SimpleDdgiTransportGenerations generations,
        bool certificate,
        bool auditFrozen,
        bool auditAwait,
        ulong frameSerial)
    {
        ulong auditDispatchSerial = certificate
            ? frameSerial - 2UL
            : auditAwait
                ? frameSerial - 1UL
                : frameSerial;
        SimpleDdgiTransportTailSummary summary = certificate
            ? CreateCertifiedSummary(generations, frameSerial)
            : SimpleDdgiTransportTailSummary.Empty with
            {
                AuditEpoch = generations.Audit,
                Generations = generations,
                ExpectedParticipantCount = 2_048u,
                ExpectedTexelCount = 131_072u,
                FirstFrameSerial = auditFrozen ? auditDispatchSerial : 0UL,
                FinalFrameSerial = auditFrozen ? auditDispatchSerial : 0UL,
                ChunkCount = auditFrozen ? 1u : 0u,
                Reason = auditFrozen
                    ? SimpleDdgiTransportCertificationReason.AuditInProgress
                    : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete
            };
        return new SimpleDdgiTailCertificateFrameEvidence
        {
            Phase = certificate
                ? SimpleDdgiTransportPhase.Certified
                : auditFrozen
                    ? SimpleDdgiTransportPhase.AuditFrozen
                    : SimpleDdgiTransportPhase.AcceleratedSolve,
            Reason = certificate
                ? SimpleDdgiTransportCertificationReason.Certified
                : auditFrozen
                    ? SimpleDdgiTransportCertificationReason.AuditInProgress
                    : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete,
            Generations = generations,
            SolveEpoch = generations.Solve,
            AuditEpoch = generations.Audit,
            ExpectedParticipantCount = summary.ExpectedParticipantCount,
            AuditedParticipantCount = summary.AuditedParticipantCount,
            ExcludedInactiveCount = summary.ExcludedInactiveCount,
            ExcludedNotVisibleCount = summary.ExcludedNotVisibleCount,
            ExcludedStaleSourceCount = summary.ExcludedStaleSourceCount,
            ExcludedInvalidCacheCount = summary.ExcludedInvalidCacheCount,
            CacheIdentityFailureCount = summary.CacheIdentityFailureCount,
            CacheCardinalityFailureCount = summary.CacheCardinalityFailureCount,
            CacheSourceGenerationFailureCount =
                summary.CacheSourceGenerationFailureCount,
            CacheSourceEpochFailureCount =
                summary.CacheSourceEpochFailureCount,
            CachePhysicalGenerationFailureCount =
                summary.CachePhysicalGenerationFailureCount,
            ExpectedTexelCount = summary.ExpectedTexelCount,
            AuditedTexelCount = summary.AuditedTexelCount,
            NonFiniteCount = summary.NonFiniteCount,
            CounterOverflowCount = summary.CounterOverflowCount,
            AuditComplete = summary.IsComplete,
            CertificateCurrent = certificate,
            AuditFirstSubmissionFrameSerial = auditFrozen || certificate
                ? auditDispatchSerial
                : 0UL,
            AuditFinalSubmissionFrameSerial = auditFrozen || certificate
                ? auditDispatchSerial
                : 0UL,
            AuditPlannedChunkCount = auditFrozen || certificate ? 1u : 0u,
            AuditSubmittedChunkCount = auditFrozen || certificate ? 1u : 0u,
            AuditDispatchComplete = auditFrozen || certificate,
            Summary = summary,
            SummaryDigest = SimpleDdgiTailSummaryDigest.Compute(summary)
        };
    }

    private static SimpleDdgiTransportTailSummary CreateCertifiedSummary(
        SimpleDdgiTransportGenerations generations,
        ulong frameSerial) =>
        new()
        {
            AuditEpoch = generations.Audit,
            Generations = generations,
            ExpectedParticipantCount = 2_048u,
            AuditedParticipantCount = 2_048u,
            ExpectedTexelCount = 131_072u,
            AuditedTexelCount = 131_072u,
            FixedPointDefect = 0.001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.5f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.002f,
            RelativeTailBound = 0.002f,
            Tolerance = 0.01f,
            CanonicalQuantizationFloor = 0.001f,
            AuditMicroseconds = 40UL,
            FirstFrameSerial = frameSerial - 2UL,
            FinalFrameSerial = frameSerial - 2UL,
            ChunkCount = 1u,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.Certified
        };

    private static SimpleDdgiCompletedFrameEvidence RemoveCompletedPass(
        SimpleDdgiCompletedFrameEvidence completed,
        SimpleDdgiGpuPassMask pass)
    {
        SimpleDdgiCompletedFrameEvidence result = completed with
        {
            CompletedGpuTimingPasses =
                completed.CompletedGpuTimingPasses & ~pass,
            GpuTimingPassSetAligned = false
        };
        return pass switch
        {
            SimpleDdgiGpuPassMask.AcceleratedSolve => result with
            {
                GpuAcceleratedSolveTimingAvailable = false,
                GpuAcceleratedSolveMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.ScheduleTailAdmit => result with
            {
                GpuSchedulerTailAdmitTimingAvailable = false,
                GpuSchedulerTailAdmitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.ScheduleEmit => result with
            {
                GpuSchedulerEmitTimingAvailable = false,
                GpuSchedulerEmitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.SchedulerCommit => result with
            {
                GpuSchedulerCommitTimingAvailable = false,
                GpuSchedulerCommitMicroseconds = 0
            },
            SimpleDdgiGpuPassMask.TransportAudit => result with
            {
                GpuTransportAuditTimingAvailable = false,
                GpuTransportAuditMicroseconds = 0
            },
            _ => result
        };
    }
}
