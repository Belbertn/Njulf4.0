using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkQualitySequenceTests
{
    private const string HashA =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Commit =
        "0123456789abcdef0123456789abcdef01234567";

    [TestCase(
        SampleBenchmarkTrajectoryKind.Stationary,
        new[] { 0 })]
    [TestCase(
        SampleBenchmarkTrajectoryKind.BistroLoop,
        new[] { 0, 59, 60, 61, 68, 76, 179, 180, 181, 239 })]
    [TestCase(
        SampleBenchmarkTrajectoryKind.SponzaHorizontal,
        new[] { 0, 1, 118, 119, 120, 121, 178, 179, 180, 181, 298, 299 })]
    [TestCase(
        SampleBenchmarkTrajectoryKind.SponzaVertical,
        new[] { 0, 1, 239, 240, 479, 480, 719, 720, 958, 959 })]
    public void CheckpointCatalog_IsExactOrderedAndImmutable(
        SampleBenchmarkTrajectoryKind trajectory,
        int[] expected)
    {
        IReadOnlyList<int> actual =
            SampleBenchmarkQualityCheckpointCatalog.GetCheckpointIndices(
                trajectory);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(
                SampleBenchmarkQualityCheckpointCatalog.CreateFingerprint(
                    trajectory),
                Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(
                () => ((IList<int>)actual)[0] = 999,
                Throws.TypeOf<NotSupportedException>());
        });
    }

    [Test]
    public void CheckpointCatalog_RejectsMissingDuplicateReorderedAndEndpointOnly()
    {
        int[] exact = SampleBenchmarkQualityCheckpointCatalog
            .GetCheckpointIndices(SampleBenchmarkTrajectoryKind.BistroLoop)
            .ToArray();
        int[] missing = exact.Where(static frame => frame != 68).ToArray();
        int[] duplicate = exact.ToArray();
        duplicate[4] = duplicate[3];
        int[] reordered = exact.ToArray();
        (reordered[1], reordered[2]) = (reordered[2], reordered[1]);
        int[] endpointsOnly = [exact[0], exact[^1]];

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleBenchmarkQualityCheckpointCatalog
                    .RequireExactCheckpointOrder(
                        SampleBenchmarkTrajectoryKind.BistroLoop,
                        missing,
                        "test"),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                () => SampleBenchmarkQualityCheckpointCatalog
                    .RequireExactCheckpointOrder(
                        SampleBenchmarkTrajectoryKind.BistroLoop,
                        duplicate,
                        "test"),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                () => SampleBenchmarkQualityCheckpointCatalog
                    .RequireExactCheckpointOrder(
                        SampleBenchmarkTrajectoryKind.BistroLoop,
                        reordered,
                        "test"),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                () => SampleBenchmarkQualityCheckpointCatalog
                    .RequireExactCheckpointOrder(
                        SampleBenchmarkTrajectoryKind.BistroLoop,
                        endpointsOnly,
                        "test"),
                Throws.TypeOf<InvalidDataException>());
        });
    }

    [Test]
    public void TemporalComparer_UsesRmsFloorForBlackReference()
    {
        SampleEvidenceFileContent blackA = Pfm([0f, 0f, 0f]);
        SampleEvidenceFileContent blackB = Pfm([0f, 0f, 0f]);
        SampleEvidenceFileContent candidateA = Pfm([0f, 0f, 0f]);
        SampleEvidenceFileContent candidateB = Pfm([1e-10f, 0f, 0f]);

        double residual = SampleBenchmarkQualityTemporalComparer.Compare(
            blackA,
            blackB,
            candidateA,
            candidateB);

        Assert.That(
            residual,
            Is.EqualTo(1.0 / Math.Sqrt(3.0)).Within(1e-6));
    }

    [Test]
    public void Parser_ArmsStandaloneBistroSequenceAndBindsWorkloadIdentity()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--benchmark-quality-sequence=true",
            "--benchmark-quality-sequence-role=canonical",
            "--benchmark-quality-sequence-id=quality-bistro-001",
            "--benchmark-quality-sequence-report=quality.json",
            "--benchmark-quality-sequence-output-dir=quality-pfms",
            "--benchmark-quality-sequence-warmup-frames=480",
            "--benchmark-quality-sequence-max-settle-frames=4096",
            "--benchmark-quality-sequence-max-drain-frames=240",
            "--benchmark-quality-sequence-budget-profile=stress",
            "--benchmark-quality-sequence-variant=forward-gi-exact",
            "--benchmark-quality-sequence-trajectory=bistro-loop",
            "--performance-scenario=BistroQualityMotionRelight"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.False);
            Assert.That(options.BenchmarkQualitySequence.Enabled, Is.True);
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.Bistro));
            Assert.That(
                options.BenchmarkQualitySequence.SceneKind,
                Is.EqualTo(SampleSceneKind.Bistro));
            Assert.That(
                options.BenchmarkQualitySequence.Scenario,
                Is.EqualTo(SamplePerformanceScenario.BistroQualityMotionRelight));
            Assert.That(
                options.BenchmarkQualitySequence.CaptureVariant,
                Is.EqualTo(SampleBenchmarkCaptureVariant.ForwardGiExact));
            Assert.That(
                SampleBenchmarkQualityWorkloadIdentity.GetCaptureSceneKind(
                    options.BenchmarkQualitySequence.SceneKind),
                Is.EqualTo("Bistro"));
            Assert.That(options.EnableGpuTiming, Is.True);
            Assert.That(options.EnableAsyncCompute, Is.False);
        });
    }

    [Test]
    public void Parser_RejectsBistroVariantWithAuthoredMidRouteSettingsChange()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark-quality-sequence=true",
                "--benchmark-quality-sequence-role=canonical",
                "--benchmark-quality-sequence-id=quality-bistro-reflection",
                "--benchmark-quality-sequence-report=quality.json",
                "--benchmark-quality-sequence-output-dir=quality-pfms",
                "--benchmark-quality-sequence-warmup-frames=480",
                "--benchmark-quality-sequence-max-settle-frames=4096",
                "--benchmark-quality-sequence-max-drain-frames=240",
                "--benchmark-quality-sequence-budget-profile=stress",
                "--benchmark-quality-sequence-variant=baseline",
                "--benchmark-quality-sequence-trajectory=bistro-loop",
                "--bistro-quality-variant=hybrid-ray-query-ab",
                "--performance-scenario=BistroQualityMotionRelight"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "does not admit the HybridRayQueryAb"));
    }

    [Test]
    public void Parser_BindsSponzaCaptureSceneAndRejectsDisabledDetailBypass()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--benchmark-quality-sequence=true",
            "--benchmark-quality-sequence-role=canonical",
            "--benchmark-quality-sequence-id=quality-sponza-001",
            "--benchmark-quality-sequence-report=quality.json",
            "--benchmark-quality-sequence-output-dir=quality-pfms",
            "--benchmark-quality-sequence-max-settle-frames=4096",
            "--benchmark-quality-sequence-max-drain-frames=240",
            "--benchmark-quality-sequence-budget-profile=stress",
            "--benchmark-quality-sequence-variant=baseline",
            "--benchmark-quality-sequence-trajectory=sponza-horizontal",
            "--performance-scenario=GiSponzaRightWallStationary"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.SponzaPlaza));
            Assert.That(
                SampleBenchmarkQualityWorkloadIdentity.GetCaptureSceneKind(
                    options.BenchmarkQualitySequence.SceneKind),
                Is.EqualTo("Sponza"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--benchmark-quality-sequence-role=canonical",
                    "--benchmark-quality-sequence=false"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "cannot be supplied while"));
        });
    }

    [Test]
    public void Parser_RejectsTimingAndReferenceRoleConflicts()
    {
        string[] canonical =
        [
            "--benchmark-quality-sequence=true",
            "--benchmark-quality-sequence-role=canonical",
            "--benchmark-quality-sequence-id=quality-001",
            "--benchmark-quality-sequence-report=quality.json",
            "--benchmark-quality-sequence-output-dir=quality-pfms",
            "--benchmark-quality-sequence-max-settle-frames=4096",
            "--benchmark-quality-sequence-max-drain-frames=240",
            "--benchmark-quality-sequence-budget-profile=stress",
            "--benchmark-quality-sequence-variant=baseline",
            "--benchmark-quality-sequence-trajectory=stationary"
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                    canonical.Append("--benchmark=true").ToArray()),
                Throws.ArgumentException.With.Message.Contains("mutually exclusive"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                    canonical.Append(
                        "--benchmark-quality-sequence-reference-contract=reference.json")
                        .ToArray()),
                Throws.ArgumentException.With.Message.Contains(
                    "cannot consume reference or ROI"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                    canonical.Select(argument => argument ==
                            "--benchmark-quality-sequence-role=canonical"
                        ? "--benchmark-quality-sequence-role=repeat"
                        : argument).ToArray()),
                Throws.ArgumentException.With.Message.Contains(
                    "require reference and ROI"));
        });
    }

    [Test]
    public void ReferenceExecutionBounds_RejectWarmupSettlingAndDrainMismatch()
    {
        SampleBenchmarkQualitySequenceOptions options = CreateOptions(
            Path.GetTempPath(),
            SampleBenchmarkTrajectoryKind.Stationary,
            warmupFrames: 480);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleBenchmarkQualitySequenceReferenceLoader
                    .ValidateExecutionBounds(
                        options,
                        warmupFrameCount: 479,
                        options.MaximumAdditionalSettlingFrameCount,
                        options.MaximumReadbackDrainFrameCount),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains(
                    "warmup frame count"));
            Assert.That(
                () => SampleBenchmarkQualitySequenceReferenceLoader
                    .ValidateExecutionBounds(
                        options,
                        options.WarmupFrameCount,
                        options.MaximumAdditionalSettlingFrameCount + 1,
                        options.MaximumReadbackDrainFrameCount),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains(
                    "maximum settling"));
            Assert.That(
                () => SampleBenchmarkQualitySequenceReferenceLoader
                    .ValidateExecutionBounds(
                        options,
                        options.WarmupFrameCount,
                        options.MaximumAdditionalSettlingFrameCount,
                        options.MaximumReadbackDrainFrameCount + 1),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains(
                    "readback-drain"));
        });
    }

    [Test]
    public void ClosedRouteWarmup_ArmsAtExactLastWarmupFrame()
    {
        string directory = CreateTemporaryDirectory();
        int capturePhaseSynchronizations = 0;
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.BistroLoop,
                warmupFrames: SampleBistroQualityCaptureContract.LoopFrameCount);
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => { },
                () => capturePhaseSynchronizations++,
                () => HashA,
                (_, _) => true,
                path => new LinearHdrCaptureResult(
                    path,
                    LinearHdrCaptureState.Queued,
                    string.Empty));

            for (int frame = 0;
                 frame < SampleBistroQualityCaptureContract.LoopFrameCount;
                 frame++)
            {
                runner.OnFrameRendered(frame, ReadyDiagnostics(frame));
            }

            Assert.Multiple(() =>
            {
                Assert.That(runner.RouteStarted, Is.True);
                Assert.That(capturePhaseSynchronizations, Is.EqualTo(1));
                Assert.That(
                    runner.ResolveTrajectoryFrameIndexForNextRender(
                        SampleBistroQualityCaptureContract.LoopFrameCount),
                    Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void MovingWarmup_HoldsFrameZeroUntilPersistentPriorIsApplied()
    {
        string directory = CreateTemporaryDirectory();
        int capturePhaseSynchronizations = 0;
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.BistroLoop,
                SampleBistroQualityCaptureContract.LoopFrameCount);
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => { },
                () => capturePhaseSynchronizations++,
                () => HashA,
                (_, _) => true,
                path => new LinearHdrCaptureResult(
                    path,
                    LinearHdrCaptureState.Queued,
                    string.Empty));
            SimpleDdgiWarmStartTelemetry pending = WarmStartTelemetry(
                loadPending: true,
                priorActive: false);
            SimpleDdgiWarmStartTelemetry applied = WarmStartTelemetry(
                loadPending: false,
                priorActive: true);

            for (int frame = 0; frame < 3; frame++)
            {
                Assert.That(
                    runner.ResolveTrajectoryFrameIndexForNextRender(frame),
                    Is.Zero);
                runner.OnFrameRendered(
                    frame,
                    ReadyDiagnostics(frame, warmStart: pending));
            }

            Assert.That(
                runner.ResolveTrajectoryFrameIndexForNextRender(3),
                Is.Zero);
            runner.OnFrameRendered(
                3,
                ReadyDiagnostics(3, warmStart: applied));

            int lastWarmupFrame = 3 +
                SampleBistroQualityCaptureContract.LoopFrameCount - 1;
            for (int absoluteFrame = 4;
                 absoluteFrame <= lastWarmupFrame;
                 absoluteFrame++)
            {
                Assert.That(
                    runner.ResolveTrajectoryFrameIndexForNextRender(
                        absoluteFrame),
                    Is.EqualTo(absoluteFrame - 3));
                runner.OnFrameRendered(
                    absoluteFrame,
                    ReadyDiagnostics(absoluteFrame, warmStart: applied));
            }

            Assert.Multiple(() =>
            {
                Assert.That(runner.RouteStarted, Is.True);
                Assert.That(capturePhaseSynchronizations, Is.EqualTo(1));
                Assert.That(
                    runner.ResolveTrajectoryFrameIndexForNextRender(
                        lastWarmupFrame + 1),
                    Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void StationarySequence_AllowsOwningFrameQueuedThenCompletesOnHeldFrame()
    {
        string directory = CreateTemporaryDirectory();
        int exits = 0;
        LinearHdrCaptureResult? result = null;
        string? requestedPath = null;
        string? requestedToken = null;
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.Stationary,
                warmupFrames: 30);
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => exits++,
                () => { },
                () => HashA,
                (path, token) =>
                {
                    requestedPath = Path.GetFullPath(path);
                    requestedToken = token;
                    result = new LinearHdrCaptureResult(
                        requestedPath,
                        LinearHdrCaptureState.Queued,
                        string.Empty)
                    {
                        CaptureToken = token
                    };
                    return true;
                },
                path => result ?? new LinearHdrCaptureResult(
                    Path.GetFullPath(path),
                    LinearHdrCaptureState.Unknown,
                    string.Empty));

            for (int frame = 0; frame < 30; frame++)
                runner.OnFrameRendered(frame, ReadyDiagnostics(frame));

            SampleBenchmarkCameraPose pose = LivePose();
            runner.PrepareFrame(30, pose, null);
            runner.OnFrameRendered(
                30,
                ReadyDiagnostics(
                    30,
                    temporalSampleIndex: 0,
                    hybridReflectionPassEnabled: 1,
                    hybridReflectionHistoryValid: 0,
                    automaticPlanarReflectionActive: 1,
                    automaticPlanarCaptureCount: 1));

            Assert.Multiple(() =>
            {
                Assert.That(result?.State, Is.EqualTo(LinearHdrCaptureState.Queued));
                Assert.That(runner.Report, Is.Null);
                Assert.That(exits, Is.Zero);
                Assert.That(runner.HoldTrajectoryForReadbackDrain, Is.True);
            });

            float[] pixels = new float[checked(
                SampleBenchmarkQualityCheckpointCatalog.RequiredWidth *
                SampleBenchmarkQualityCheckpointCatalog.RequiredHeight * 3)];
            PfmLinearImageCodec.WriteAtomic(
                requestedPath!,
                pixels,
                SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
                SampleBenchmarkQualityCheckpointCatalog.RequiredHeight);
            result = new LinearHdrCaptureResult(
                requestedPath!,
                LinearHdrCaptureState.Completed,
                string.Empty)
            {
                CaptureToken = requestedToken!,
                FrameSerial = 30
            };
            runner.PrepareFrame(31, pose, null);
            runner.OnFrameRendered(
                31,
                ReadyDiagnostics(
                    31,
                    temporalSampleIndex: 1,
                    hybridReflectionPassEnabled: 1,
                    hybridReflectionHistoryValid: 1,
                    automaticPlanarReflectionActive: 1,
                    automaticPlanarCaptureCount: 0));

            Assert.Multiple(() =>
            {
                Assert.That(exits, Is.EqualTo(1));
                Assert.That(runner.Report, Is.Not.Null);
                Assert.That(runner.Report!.Passed, Is.True);
                Assert.That(runner.Report.TimingEligible, Is.False);
                Assert.That(runner.Report.ProductionTiming, Is.False);
                Assert.That(runner.Report.FirstRouteAbsoluteFrameIndex, Is.EqualTo(30));
                Assert.That(runner.Report.Checkpoints.Single().AbsoluteFrameIndex, Is.EqualTo(30));
                Assert.That(runner.Report.Checkpoints.Single().DdgiFrameSerial, Is.EqualTo(30));
                Assert.That(runner.Report.TrajectorySequenceHash, Does.Match(
                    "^sha256:[0-9a-f]{64}$"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Sequence_RejectsWrongTerminalPathAndFrameSerial()
    {
        string directory = CreateTemporaryDirectory();
        LinearHdrCaptureResult? result = null;
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.Stationary,
                warmupFrames: 30);
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => { },
                () => { },
                () => HashA,
                (path, token) =>
                {
                    result = new LinearHdrCaptureResult(
                        Path.Combine(directory, "wrong-output.pfm"),
                        LinearHdrCaptureState.Completed,
                        string.Empty)
                    {
                        CaptureToken = token,
                        FrameSerial = 999
                    };
                    return true;
                },
                _ => result!);
            for (int frame = 0; frame < 30; frame++)
                runner.OnFrameRendered(frame, ReadyDiagnostics(frame));

            runner.PrepareFrame(30, LivePose(), null);
            runner.OnFrameRendered(30, ReadyDiagnostics(30));

            Assert.Multiple(() =>
            {
                Assert.That(runner.Report, Is.Not.Null);
                Assert.That(runner.Report!.Passed, Is.False);
                Assert.That(
                    runner.Report.Failures,
                    Has.Some.Contains("result path differs"));
                Assert.That(
                    runner.Report.Failures,
                    Has.Some.Contains("serial 999 differs"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Sequence_RejectsOwningFramePostDrawSettingsMutation()
    {
        string directory = CreateTemporaryDirectory();
        string settingsFingerprint = HashA;
        LinearHdrCaptureResult? result = null;
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.Stationary,
                warmupFrames: 30);
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => { },
                () => { },
                () => settingsFingerprint,
                (path, token) =>
                {
                    float[] pixels = new float[checked(
                        SampleBenchmarkQualityCheckpointCatalog.RequiredWidth *
                        SampleBenchmarkQualityCheckpointCatalog.RequiredHeight * 3)];
                    PfmLinearImageCodec.WriteAtomic(
                        path,
                        pixels,
                        SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
                        SampleBenchmarkQualityCheckpointCatalog.RequiredHeight);
                    result = new LinearHdrCaptureResult(
                        Path.GetFullPath(path),
                        LinearHdrCaptureState.Completed,
                        string.Empty)
                    {
                        CaptureToken = token,
                        FrameSerial = 30
                    };
                    return true;
                },
                _ => result!);
            for (int frame = 0; frame < 30; frame++)
                runner.OnFrameRendered(frame, ReadyDiagnostics(frame));

            runner.PrepareFrame(30, LivePose(), null);
            settingsFingerprint = HashB;
            runner.OnFrameRendered(30, ReadyDiagnostics(30));

            Assert.Multiple(() =>
            {
                Assert.That(runner.Report, Is.Not.Null);
                Assert.That(runner.Report!.Passed, Is.False);
                Assert.That(
                    runner.Report.Failures,
                    Has.Some.Contains(
                        "settings fingerprint changed between pre-Draw and post-Draw"));
                Assert.That(
                    runner.Report.Checkpoints.Single().SettingsFingerprint,
                    Is.EqualTo(HashA));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void FailedCheckpoint_StillTraversesExactlyOneContinuousBistroRoute()
    {
        string directory = CreateTemporaryDirectory();
        LinearHdrCaptureResult? result = null;
        int exits = 0;
        int requestCount = 0;
        var observedRoute = new List<int>();
        try
        {
            SampleBenchmarkQualitySequenceOptions options = CreateOptions(
                directory,
                SampleBenchmarkTrajectoryKind.BistroLoop,
                SampleBistroQualityCaptureContract.LoopFrameCount) with
            {
                SceneKind = SampleSceneKind.Bistro
            };
            var runner = new SampleBenchmarkQualitySequenceRunner(
                options,
                SamplePerformanceScenario.Normal,
                () => exits++,
                () => { },
                () => HashA,
                (path, token) =>
                {
                    requestCount++;
                    result = new LinearHdrCaptureResult(
                        Path.GetFullPath(path),
                        LinearHdrCaptureState.Queued,
                        string.Empty)
                    {
                        CaptureToken = token
                    };
                    return true;
                },
                _ => result!);
            for (int frame = 0;
                 frame < SampleBistroQualityCaptureContract.LoopFrameCount;
                 frame++)
            {
                runner.OnFrameRendered(frame, ReadyDiagnostics(frame));
            }

            var contract = new SampleBistroQualityCaptureContract(
                SampleBistroQualityCaptureVariant.SunScaleStep);
            int firstAbsolute = SampleBistroQualityCaptureContract.LoopFrameCount;
            for (int routeFrame = 0;
                 routeFrame < SampleBistroQualityCaptureContract.LoopFrameCount;
                 routeFrame++)
            {
                int absolute = firstAbsolute + routeFrame;
                int resolved = runner.ResolveTrajectoryFrameIndexForNextRender(
                    absolute);
                observedRoute.Add(resolved);
                SampleBenchmarkCameraPose pose =
                    SampleBenchmarkTrajectory.ResolveCamera(
                        SampleBenchmarkTrajectoryKind.BistroLoop,
                        routeFrame,
                        SampleBistroQualityCaptureVariant.SunScaleStep)!;
                SampleBistroQualityFrameState state = contract.ResolveFrame(
                    SampleBistroQualityCaptureContract.FirstMeasuredFrame +
                    routeFrame);
                runner.PrepareFrame(absolute, pose, state);
                if (routeFrame == 1)
                {
                    result = result! with
                    {
                        State = LinearHdrCaptureState.Failed,
                        Error = "synthetic readback failure",
                        FrameSerial = 1_000
                    };
                }
                runner.OnFrameRendered(
                    absolute,
                    ReadyDiagnostics(
                        frameSerial: 1_000 + routeFrame,
                        pose: pose,
                        sceneKind: "Bistro"));
                if (routeFrame <
                    SampleBistroQualityCaptureContract.LoopFrameCount - 1)
                {
                    Assert.That(exits, Is.Zero);
                    Assert.That(
                        runner.HoldTrajectoryForReadbackDrain,
                        Is.False);
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    observedRoute,
                    Is.EqualTo(Enumerable.Range(
                        0,
                        SampleBistroQualityCaptureContract.LoopFrameCount)));
                Assert.That(requestCount, Is.EqualTo(1));
                Assert.That(exits, Is.EqualTo(1));
                Assert.That(runner.Report, Is.Not.Null);
                Assert.That(runner.Report!.Passed, Is.False);
                Assert.That(
                    runner.Report.Failures,
                    Has.Some.Contains("synthetic readback failure"));
                Assert.That(runner.HoldTrajectoryForReadbackDrain, Is.True);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void RouteSequenceHash_NormalizesCutOffsetAndRetainsAuthoredSceneState()
    {
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> firstRoute =
            CreateBistroRouteObservations(initialCutSerial: 100);
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> secondRoute =
            CreateBistroRouteObservations(initialCutSerial: 900);
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> missingRelightState =
            Array.AsReadOnly(firstRoute.Select(static observation =>
                observation with
                {
                    Diagnostics = observation.Diagnostics with
                    {
                        CaptureSceneStateHash = HashB
                    }
                }).ToArray());
        string first = CreateBistroRouteSequenceHash(firstRoute);
        string second = CreateBistroRouteSequenceHash(secondRoute);
        string missingRelight = CreateBistroRouteSequenceHash(
            missingRelightState);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(
                missingRelight,
                Is.Not.EqualTo(first),
                "authored per-frame scene-state changes must remain in the full-route identity");
            Assert.That(
                firstRoute.Select(static observation =>
                        observation.Diagnostics.CaptureSceneAssetHash)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Has.Length.EqualTo(1));
            Assert.That(
                firstRoute.Select(static observation =>
                        observation.Diagnostics.CaptureSceneStateHash)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Has.Length.EqualTo(2));
        });
    }

    private static SampleBenchmarkQualitySequenceOptions CreateOptions(
        string directory,
        SampleBenchmarkTrajectoryKind trajectory,
        int warmupFrames) => new()
    {
        Enabled = true,
        Role = SampleBenchmarkQualitySequenceRole.Canonical,
        SequenceId = "quality-test-001",
        ReportPath = Path.Combine(directory, "report.json"),
        OutputDirectory = Path.Combine(directory, "pfms"),
        WarmupFrameCount = warmupFrames,
        MaximumAdditionalSettlingFrameCount =
            SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount,
        MaximumReadbackDrainFrameCount = 10,
        BudgetProfileOverride = RenderBudgetProfileKind.StressUnlimited,
        CaptureVariant = SampleBenchmarkCaptureVariant.Baseline,
        SceneKind = SampleSceneKind.GlobalIlluminationTest,
        Scenario = SamplePerformanceScenario.Normal,
        Trajectory = trajectory,
        TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
            trajectory,
            SampleBistroQualityCaptureVariant.SunScaleStep),
        TrajectoryBistroVariant =
            SampleBistroQualityCaptureVariant.SunScaleStep
    };

    private static RendererDiagnostics ReadyDiagnostics(
        int frameSerial,
        SampleBenchmarkCameraPose? pose = null,
        string sceneKind = "GlobalIlluminationTest",
        ulong sceneContentRevision = 1,
        ulong cameraCutSerial = 7,
        string sceneStateHash = HashB,
        uint temporalSampleIndex = 0,
        int hybridReflectionPassEnabled = 0,
        int hybridReflectionHistoryValid = 0,
        int automaticPlanarReflectionActive = 0,
        int automaticPlanarCaptureCount = 0,
        SimpleDdgiWarmStartTelemetry? warmStart = null)
    {
        SampleBenchmarkCameraPose resolvedPose = pose ?? LivePose();
        PerformanceCaptureCameraMetadata camera = new(
            resolvedPose.Position.X,
            resolvedPose.Position.Y,
            resolvedPose.Position.Z,
            resolvedPose.Yaw,
            resolvedPose.Pitch,
            resolvedPose.FieldOfView,
            resolvedPose.NearPlane,
            resolvedPose.FarPlane,
            HashA,
            HashB,
            cameraCutSerial);
        var run = new PerformanceCaptureRunMetadata(
            sceneKind,
            "Normal",
            "Release",
            "1.0.0-test",
            Commit,
            HashB,
            1)
        {
            ExecutableHash = HashA,
            DirtyWorktreeState = "clean"
        };
        return RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            CaptureFrame = new PerformanceCaptureFrameMetadata(
                (ulong)frameSerial,
                (ulong)frameSerial,
                DdgiRuntimeWarmupState.SteadyState,
                frameSerial,
                frameSerial),
            CaptureCamera = camera,
            CaptureRun = run,
            CaptureRenderWidth =
                SampleBenchmarkQualityCheckpointCatalog.RequiredWidth,
            CaptureRenderHeight =
                SampleBenchmarkQualityCheckpointCatalog.RequiredHeight,
            CaptureSceneAssetHash = HashA,
            CaptureSceneStateHash = sceneStateHash,
            CaptureSceneContentRevision = sceneContentRevision,
            CaptureGpuDeviceName = "Test GPU",
            CaptureGpuDriverVersion = "Test Driver 1",
            ActiveBudgetProfile = RenderBudgetProfileKind.StressUnlimited,
            TemporalSampleIndex = temporalSampleIndex,
            HybridReflectionPassEnabled = hybridReflectionPassEnabled,
            HybridReflectionHistoryValid = hybridReflectionHistoryValid,
            AutomaticPlanarReflectionActive = automaticPlanarReflectionActive,
            AutomaticPlanarCaptureCount = automaticPlanarCaptureCount,
            SimpleDdgiWarmStart = warmStart ??
                SimpleDdgiWarmStartTelemetry.Disabled("test")
        };
    }

    private static SimpleDdgiWarmStartTelemetry WarmStartTelemetry(
        bool loadPending,
        bool priorActive) => new(
        Enabled: true,
        Eligible: true,
        LoadPending: loadPending,
        CacheFound: !loadPending,
        CacheAccepted: !loadPending,
        PriorActive: priorActive,
        ReadbackPending: false,
        SavePending: false,
        CachedVolumeCount: loadPending ? 0 : 2,
        CachedProbeCount: loadPending ? 0 : 4_392,
        AppliedProbeCount: priorActive ? 3_616 : 0,
        LoadedFileBytes: loadPending ? 0UL : 4_430_455UL,
        SavedFileBytes: 0UL,
        ReadbackBytes: 0UL,
        LoadCount: loadPending ? 0UL : 1UL,
        RejectCount: 0UL,
        ApplyCount: priorActive ? 1UL : 0UL,
        SaveCount: 0UL,
        CachePath: "test.njwarm",
        Status: loadPending ? "loading" : "applied");

    private static SampleBenchmarkCameraPose LivePose() => new(
        "test-live",
        new Vector3(1f, 2f, 3f),
        0.25f,
        -0.1f,
        1.0f,
        0.1f,
        1000f);

    private static SampleEvidenceFileContent Pfm(float[] pixels)
    {
        byte[] bytes = PfmLinearImageCodec.Encode(pixels, 1, 1);
        return new SampleEvidenceFileContent(
            "in-memory.pfm",
            bytes,
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant());
    }

    private static string CreateBistroRouteSequenceHash(
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> observations)
    {
        SampleBenchmarkQualitySequenceOptions options = CreateOptions(
            Path.GetTempPath(),
            SampleBenchmarkTrajectoryKind.BistroLoop,
            warmupFrames: 240) with
        {
            SceneKind = SampleSceneKind.Bistro
        };
        return SampleBenchmarkQualityRouteSequenceHasher.Create(
            options,
            observations);
    }

    private static IReadOnlyList<SampleBenchmarkQualityRouteObservation>
        CreateBistroRouteObservations(ulong initialCutSerial)
    {
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.SunScaleStep);
        var observations = new List<SampleBenchmarkQualityRouteObservation>();
        for (int routeFrame = 0;
             routeFrame < SampleBistroQualityCaptureContract.LoopFrameCount;
             routeFrame++)
        {
            SampleBenchmarkCameraPose pose = SampleBenchmarkTrajectory.ResolveCamera(
                SampleBenchmarkTrajectoryKind.BistroLoop,
                routeFrame,
                SampleBistroQualityCaptureVariant.SunScaleStep)!;
            bool afterRelight = routeFrame >=
                SampleBistroQualityCaptureContract.LightingEventStartFrame;
            ulong revision = afterRelight ? 2UL : 1UL;
            ulong cutSerial = afterRelight
                ? (ulong)(routeFrame -
                    SampleBistroQualityCaptureContract.LightingEventStartFrame)
                : initialCutSerial + (ulong)routeFrame;
            RendererDiagnostics diagnostics = ReadyDiagnostics(
                1_000 + routeFrame,
                pose,
                "Bistro",
                revision,
                cutSerial,
                afterRelight ? HashC : HashB);
            observations.Add(new SampleBenchmarkQualityRouteObservation(
                routeFrame,
                pose,
                contract.ResolveFrame(
                    SampleBistroQualityCaptureContract.FirstMeasuredFrame +
                    routeFrame),
                diagnostics,
                HashA,
                SampleMaterialGiProducerIdentityFactory.Create(
                    diagnostics,
                    HashA,
                    "StressUnlimited"),
                ActivationFrameState: null));
        }
        return Array.AsReadOnly(observations.ToArray());
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "benchmark-quality-sequence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
