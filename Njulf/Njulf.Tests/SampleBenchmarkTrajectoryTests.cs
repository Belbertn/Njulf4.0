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
}
