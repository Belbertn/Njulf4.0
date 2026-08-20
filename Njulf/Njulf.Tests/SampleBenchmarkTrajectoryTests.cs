using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkTrajectoryTests
{
    [TestCase("stationary", SampleBenchmarkTrajectoryKind.Stationary, 1, false)]
    [TestCase("bistro-presentation", SampleBenchmarkTrajectoryKind.BistroPresentation, 1, false)]
    [TestCase("bistro-loop", SampleBenchmarkTrajectoryKind.BistroLoop, 240, true)]
    [TestCase("sponza-low", SampleBenchmarkTrajectoryKind.SponzaLow, 1, false)]
    [TestCase("sponza-high", SampleBenchmarkTrajectoryKind.SponzaHigh, 1, false)]
    [TestCase("sponza-horizontal", SampleBenchmarkTrajectoryKind.SponzaHorizontal, 300, true)]
    [TestCase("sponza-vertical", SampleBenchmarkTrajectoryKind.SponzaVertical, 960, true)]
    public void NamedContracts_ParseWithLockedFrameCounts(
        string name,
        SampleBenchmarkTrajectoryKind expected,
        int frameCount,
        bool moving)
    {
        SampleBenchmarkTrajectoryKind parsed =
            SampleBenchmarkTrajectory.Parse(name);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(expected));
            Assert.That(SampleBenchmarkTrajectory.GetName(parsed), Is.EqualTo(name));
            Assert.That(
                SampleBenchmarkTrajectory.GetFrameCount(parsed),
                Is.EqualTo(frameCount));
            Assert.That(SampleBenchmarkTrajectory.IsMoving(parsed), Is.EqualTo(moving));
            Assert.That(
                SampleBenchmarkTrajectory.CreateFingerprint(
                    parsed,
                    SampleBistroQualityCaptureVariant.SunScaleStep),
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void CameraValidation_AcceptsAuthoredPoseAndRejectsDrift()
    {
        const SampleBenchmarkTrajectoryKind kind =
            SampleBenchmarkTrajectoryKind.SponzaVertical;
        const int frameIndex = 511;
        SampleBenchmarkCameraPose expected =
            SampleBenchmarkTrajectory.ResolveCamera(
                kind,
                frameIndex,
                SampleBistroQualityCaptureVariant.SunScaleStep)!;
        var captured = new PerformanceCaptureCameraMetadata(
            expected.Position.X,
            expected.Position.Y,
            expected.Position.Z,
            expected.Yaw,
            expected.Pitch,
            expected.FieldOfView,
            expected.NearPlane,
            expected.FarPlane,
            "view",
            "projection",
            0);

        IReadOnlyList<string> exact = SampleBenchmarkTrajectory.ValidateCamera(
            kind,
            frameIndex,
            SampleBistroQualityCaptureVariant.SunScaleStep,
            captured);
        IReadOnlyList<string> drifted = SampleBenchmarkTrajectory.ValidateCamera(
            kind,
            frameIndex,
            SampleBistroQualityCaptureVariant.SunScaleStep,
            captured with { PositionY = captured.PositionY + 0.01f });

        Assert.Multiple(() =>
        {
            Assert.That(exact, Is.Empty);
            Assert.That(drifted, Has.Count.EqualTo(1));
            Assert.That(drifted[0], Does.StartWith("position Y expected"));
        });
    }

    [Test]
    public void MovingTrajectory_WarmupAndRunnerProgressAreMeasurementRelative()
    {
        const SampleBenchmarkTrajectoryKind kind =
            SampleBenchmarkTrajectoryKind.SponzaVertical;
        var options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: 0,
            MeasureFrameCount: 2,
            ReportPath: null)
        {
            Trajectory = kind,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                kind,
                SampleBistroQualityCaptureVariant.SunScaleStep),
            MaximumAdditionalSettlingFrameCount = 0
        };
        var runner = new SampleBenchmarkRunner(
            options,
            SamplePerformanceScenario.Normal,
            () => { },
            () => "settings");

        int before = runner.ResolveTrajectoryFrameIndexForNextRender(959);
        runner.OnFrameRendered(
            959,
            RendererDiagnostics.Empty,
            RenderBudgetSnapshot.Empty);
        int started = runner.ResolveTrajectoryFrameIndexForNextRender(1_920);
        runner.OnFrameRendered(
            1_920,
            RendererDiagnostics.Empty,
            RenderBudgetSnapshot.Empty);
        int afterFirstSample =
            runner.ResolveTrajectoryFrameIndexForNextRender(2_880);

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkTrajectory.GetWarmupFrameIndex(kind, 959),
                Is.Zero);
            Assert.That(
                SampleBenchmarkTrajectory.GetWarmupFrameIndex(kind, 1_920),
                Is.Zero);
            Assert.That(before, Is.Zero);
            Assert.That(runner.MovingTrajectoryMeasurementStarted, Is.True);
            Assert.That(started, Is.Zero);
            Assert.That(afterFirstSample, Is.EqualTo(1));
        });
    }

    [Test]
    public void ClosedRoutesWarmAuthoredCyclesAndStartAfterTheirLastPose()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    239),
                Is.EqualTo(239));
            Assert.That(
                SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    240),
                Is.Zero);
            Assert.That(
                SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    239),
                Is.True);
            Assert.That(
                SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                    SampleBenchmarkTrajectoryKind.BistroLoop,
                    238),
                Is.False);
            Assert.That(
                SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                    SampleBenchmarkTrajectoryKind.SponzaVertical,
                    511),
                Is.True);
        });
    }

    [Test]
    public void RouteHash_IsStableAndDistinguishesAuthoredRoutes()
    {
        string horizontal = SampleBenchmarkTrajectory.CreateRouteHash(
            SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            SampleBistroQualityCaptureVariant.SunScaleStep);
        string horizontalRepeat = SampleBenchmarkTrajectory.CreateRouteHash(
            SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            SampleBistroQualityCaptureVariant.SunScaleStep);
        string vertical = SampleBenchmarkTrajectory.CreateRouteHash(
            SampleBenchmarkTrajectoryKind.SponzaVertical,
            SampleBistroQualityCaptureVariant.SunScaleStep);

        Assert.Multiple(() =>
        {
            Assert.That(horizontal, Is.EqualTo(horizontalRepeat));
            Assert.That(horizontal, Does.Match("^sha256:[0-9a-f]{64}$"));
            Assert.That(vertical, Is.Not.EqualTo(horizontal));
        });
    }

    [Test]
    public void Analyzer_ValidatesNamedStaticBookmarkInsteadOfOnlyInvariance()
    {
        const SampleBenchmarkTrajectoryKind kind =
            SampleBenchmarkTrajectoryKind.SponzaHigh;
        SampleBenchmarkCameraPose expected =
            SampleBenchmarkTrajectory.ResolveCamera(
                kind,
                0,
                SampleBistroQualityCaptureVariant.SunScaleStep)!;
        var camera = new PerformanceCaptureCameraMetadata(
            expected.Position.X + 0.25f,
            expected.Position.Y,
            expected.Position.Z,
            expected.Yaw,
            expected.Pitch,
            expected.FieldOfView,
            expected.NearPlane,
            expected.FarPlane,
            "view",
            "projection",
            0);
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(
            RendererDiagnostics.Empty with { CaptureCamera = camera },
            RenderBudgetSnapshot.Empty);
        var options = new SampleBenchmarkOptions(true, 0, 1, null)
        {
            Trajectory = kind,
            TrajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                kind,
                SampleBistroQualityCaptureVariant.SunScaleStep)
        };

        SampleBenchmarkReport report = analyzer.CreateReport(
            options,
            SamplePerformanceScenario.Normal,
            0,
            1,
            0,
            0);

        Assert.That(
            report.CaptureContract.Mismatches,
            Has.Some.Contains("trajectory camera position X expected"));
    }

    [Test]
    public void Parser_ArmsBistroMovingBenchmarkWithDeterministicIdentity()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--benchmark=true",
            "--benchmark-trajectory=bistro-loop",
            "--benchmark-measure-frames=240",
            "--bistro-quality-variant=steady-motion"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.Bistro));
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(
                options.Benchmark.Trajectory,
                Is.EqualTo(SampleBenchmarkTrajectoryKind.BistroLoop));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(240));
            Assert.That(
                options.Benchmark.TrajectoryBistroVariant,
                Is.EqualTo(SampleBistroQualityCaptureVariant.SteadyMotion));
            Assert.That(
                options.Benchmark.TrajectoryFingerprint,
                Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void Parser_RejectsTrajectorySceneMismatch()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark=true",
                "--benchmark-trajectory=sponza-horizontal",
                "--scene=bistro"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "requires the Sponza plaza scene"));
    }

    [Test]
    public void Parser_DefaultsMovingBenchmarkToOneCompleteCycle()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--benchmark-trajectory=sponza-vertical"
        ]);

        Assert.That(
            options.Benchmark.MeasureFrameCount,
            Is.EqualTo(960));
    }

    [Test]
    public void Parser_RejectsPartialMovingCycleWithoutProductionFlag()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark-trajectory=sponza-vertical",
                "--benchmark-measure-frames=120"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "must measure exactly one complete 'sponza-vertical' cycle"));
    }

    [TestCase("bistro-loop", "GiSponzaRightWallStationary")]
    [TestCase("sponza-horizontal", "BistroQualityMotionRelight")]
    [TestCase("sponza-horizontal", "GiFastTraversalTeleport")]
    public void Parser_RejectsScenarioTrajectoryOwnershipConflicts(
        string trajectory,
        string scenario)
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                $"--benchmark-trajectory={trajectory}",
                $"--performance-scenario={scenario}"
            ]),
            Throws.ArgumentException.With.Message.Contains("cannot be combined"));
    }

    [TestCase("--benchmark-hdr-max-relative-rmse=0.005")]
    [TestCase("--benchmark-hdr-max-flip-p95=0.02")]
    [TestCase("--benchmark-hdr-quality-contract=quality.json")]
    public void Parser_RejectsQualityGateWithoutHdrReference(string gate)
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse([gate]),
            Throws.ArgumentException.With.Message.Contains(
                "quality gates require --benchmark-hdr-reference"));
    }
}
