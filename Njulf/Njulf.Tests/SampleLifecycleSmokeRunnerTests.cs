using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleLifecycleSmokeRunnerTests
{
    [Test]
    public void MissingAssetSmoke_IsSkippedUnlessForced()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.MissingAssets,
            FrameCount: 1,
            SceneReloadCount: 0,
            StartupLogPath: null,
            HealthReportPath: null,
            RendererValidationMode.Off,
            FailOnValidationMessage: false,
            ForceMissingAssets: false,
            PerformanceScenario: SamplePerformanceScenario.Normal,
            EnableGpuTiming: false,
            EnableSceneGpuCompaction: false,
            EnableSceneIndirectDispatch: false,
            EnableSceneGpuLodSelection: false,
            EnableSceneGpuShadowCompaction: false,
            EnableSceneSubmissionValidation: false,
            EnableAsyncCompute: false,
            BaselineSnapshotDirectory: null);
        var runner = new SampleLifecycleSmokeRunner(options, (_, _) => { }, () => { }, () => { });

        runner.OnFrameRendered(0);

        Assert.Multiple(() =>
        {
            Assert.That(runner.Results, Has.Count.EqualTo(1));
            Assert.That(runner.Results[0].Name, Is.EqualTo("missing-assets"));
            Assert.That(runner.Results[0].Status, Is.EqualTo("skipped"));
        });
    }

    [Test]
    public void MissingAssetSmoke_RunsControlledScenarioWhenForced()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.MissingAssets,
            FrameCount: 1,
            SceneReloadCount: 0,
            StartupLogPath: null,
            HealthReportPath: null,
            RendererValidationMode.Off,
            FailOnValidationMessage: false,
            ForceMissingAssets: true,
            PerformanceScenario: SamplePerformanceScenario.Normal,
            EnableGpuTiming: false,
            EnableSceneGpuCompaction: false,
            EnableSceneIndirectDispatch: false,
            EnableSceneGpuLodSelection: false,
            EnableSceneGpuShadowCompaction: false,
            EnableSceneSubmissionValidation: false,
            EnableAsyncCompute: false,
            BaselineSnapshotDirectory: null);
        bool invoked = false;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => { },
            () => { },
            scenarios =>
            {
                invoked = true;
                Assert.That(scenarios, Has.Count.EqualTo(1));
                Assert.That(scenarios[0].Required, Is.True);
                return null;
            });

        runner.OnFrameRendered(0);

        Assert.Multiple(() =>
        {
            Assert.That(invoked, Is.True);
            Assert.That(runner.Results, Has.Count.EqualTo(1));
            Assert.That(runner.Results[0].Name, Is.EqualTo("missing-assets"));
            Assert.That(runner.Results[0].Status, Is.EqualTo("passed"));
        });
    }

    [Test]
    public void StartupSmoke_RecordsBindless3DTextureRoundTrip()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.Startup,
            FrameCount: 1,
            SceneReloadCount: 0,
            StartupLogPath: null,
            HealthReportPath: null,
            RendererValidationMode.Off,
            FailOnValidationMessage: false,
            ForceMissingAssets: false,
            PerformanceScenario: SamplePerformanceScenario.Normal,
            EnableGpuTiming: false,
            EnableSceneGpuCompaction: false,
            EnableSceneIndirectDispatch: false,
            EnableSceneGpuLodSelection: false,
            EnableSceneGpuShadowCompaction: false,
            EnableSceneSubmissionValidation: false,
            EnableAsyncCompute: false,
            BaselineSnapshotDirectory: null);
        bool invoked = false;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => { },
            () => { },
            runBindless3DTextureRoundTrip: () =>
            {
                invoked = true;
                return new Bindless3DTextureRoundTripSmokeResult(true, "round-trip ok");
            });

        runner.OnFrameRendered(0);

        Assert.Multiple(() =>
        {
            Assert.That(invoked, Is.True);
            Assert.That(runner.Results.Select(result => result.Name), Does.Contain("bindless-3d-texture-roundtrip"));
            SampleSmokeOperationResult result = runner.Results.Single(result => result.Name == "bindless-3d-texture-roundtrip");
            Assert.That(result.Status, Is.EqualTo("passed"));
            Assert.That(result.Detail, Is.EqualTo("round-trip ok"));
        });
    }

    [Test]
    public void StartupSmoke_RecordsBindless3DTextureRoundTripFailure()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.Startup,
            FrameCount: 1,
            SceneReloadCount: 0,
            StartupLogPath: null,
            HealthReportPath: null,
            RendererValidationMode.Off,
            FailOnValidationMessage: false,
            ForceMissingAssets: false,
            PerformanceScenario: SamplePerformanceScenario.Normal,
            EnableGpuTiming: false,
            EnableSceneGpuCompaction: false,
            EnableSceneIndirectDispatch: false,
            EnableSceneGpuLodSelection: false,
            EnableSceneGpuShadowCompaction: false,
            EnableSceneSubmissionValidation: false,
            EnableAsyncCompute: false,
            BaselineSnapshotDirectory: null);
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => { },
            () => { },
            runBindless3DTextureRoundTrip: () => new Bindless3DTextureRoundTripSmokeResult(false, "mismatch"));

        runner.OnFrameRendered(0);

        SampleSmokeOperationResult result = runner.Results.Single(result => result.Name == "bindless-3d-texture-roundtrip");
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("failed"));
            Assert.That(result.Detail, Is.EqualTo("mismatch"));
        });
    }
}
