using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleLifecycleSmokeRunnerTests
{
    [TestCase(SampleSmokeMode.QualitySwitch)]
    [TestCase(SampleSmokeMode.TextureHotReload)]
    public void SpecializedProductionSmoke_OwnsExitBeyondGenericFrameBudget(
        SampleSmokeMode mode)
    {
        var options = new SampleSmokeOptions(
            mode,
            FrameCount: 1,
            SceneReloadCount: 0,
            StartupLogPath: null,
            HealthReportPath: null,
            RendererValidationMode.Off,
            FailOnValidationMessage: false,
            ForceMissingAssets: false,
            PerformanceScenario: SamplePerformanceScenario.Normal,
            EnableGpuTiming: true,
            EnableSceneGpuCompaction: false,
            EnableSceneIndirectDispatch: false,
            EnableSceneGpuLodSelection: false,
            EnableSceneGpuShadowCompaction: false,
            EnableSceneSubmissionValidation: false,
            EnableAsyncCompute: false,
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
            BaselineSnapshotDirectory: null);
        int exitCount = 0;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => { },
            () => exitCount++);

        for (int frame = 0; frame < 200; frame++)
            runner.OnFrameRendered(frame);

        Assert.That(exitCount, Is.Zero);
    }

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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
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
    public void SceneReloadSmoke_RequiresACompletedPostReloadFrameBeforePassing()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.SceneReload,
            FrameCount: 3,
            SceneReloadCount: 1,
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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
            BaselineSnapshotDirectory: null);
        int reloadCount = 0;
        bool exited = false;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => { },
            () => reloadCount++,
            () => exited = true);

        runner.OnFrameRendered(0);
        runner.OnFrameRendered(1);

        Assert.Multiple(() =>
        {
            Assert.That(reloadCount, Is.EqualTo(1));
            Assert.That(runner.Results, Is.Empty);
            Assert.That(exited, Is.False);
        });

        runner.OnFrameRendered(2);

        Assert.Multiple(() =>
        {
            Assert.That(exited, Is.True);
            Assert.That(runner.Results, Has.Count.EqualTo(1));
            Assert.That(runner.Results[0].Status, Is.EqualTo("passed"));
            Assert.That(runner.Results[0].Detail, Does.Contain("postReloadFrameObserved=true"));
        });
    }

    [Test]
    public void AllSmoke_CompletesEveryMutationAndPostMutationFrameBeforeExiting()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.All,
            FrameCount: 3,
            SceneReloadCount: 1,
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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
            BaselineSnapshotDirectory: null);
        var resizes = new List<(int Width, int Height)>();
        var resizeFrames = new List<int>();
        int reloadCount = 0;
        int exitCount = 0;
        int currentFrame = -1;
        SampleLifecycleSmokeRunner? runner = null;
        runner = new SampleLifecycleSmokeRunner(
            options,
            (width, height) =>
            {
                resizes.Add((width, height));
                resizeFrames.Add(currentFrame);
                runner!.OnFramebufferMutationObserved(
                    succeeded: true,
                    $"observed={width}x{height}");
            },
            () => reloadCount++,
            () => exitCount++,
            initialWindowSize: () => (1600, 900));

        for (currentFrame = 0; currentFrame <= 6; currentFrame++)
        {
            runner.OnUpdate(currentFrame);
            runner.OnFrameRendered(currentFrame);
        }

        Assert.Multiple(() =>
        {
            Assert.That(reloadCount, Is.EqualTo(1));
            Assert.That(resizes, Does.Contain((800, 600)));
            Assert.That(resizes[^1], Is.EqualTo((800, 600)));
            Assert.That(
                resizeFrames.GroupBy(frame => frame).Select(group => group.Count()),
                Has.All.EqualTo(1));
            Assert.That(
                runner.Results.Any(result => result.Name == "scene-reload"),
                Is.False);
            Assert.That(exitCount, Is.Zero);
        });

        currentFrame = 7;
        runner.OnFrameRendered(currentFrame);

        Assert.Multiple(() =>
        {
            Assert.That(exitCount, Is.EqualTo(1));
            Assert.That(runner.Results.Count(result => result.Name == "resize"), Is.EqualTo(3));
            Assert.That(runner.Results.Any(result => result.Name == "minimize-zero-framebuffer"), Is.True);
            Assert.That(runner.Results.Any(result => result.Name == "restore-framebuffer"), Is.True);
            Assert.That(runner.Results.Any(result => result.Name == "fullscreen"), Is.True);
            Assert.That(
                runner.Results.Any(
                    result => result.Name == "scene-reload" &&
                              result.Detail!.Contains("postReloadFrameObserved=true")),
                Is.True);
        });
    }

    [Test]
    public void MinimizeSmoke_SchedulesRestoreFromUpdateAfterZeroFramebufferIsObserved()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.Minimize,
            FrameCount: 4,
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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
            BaselineSnapshotDirectory: null);
        var requests = new List<(int Width, int Height)>();
        bool exited = false;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (width, height) => requests.Add((width, height)),
            () => { },
            () => exited = true,
            initialWindowSize: () => (1600, 900));

        runner.OnFrameRendered(0);
        runner.OnFrameRendered(1);

        Assert.Multiple(() =>
        {
            Assert.That(requests, Is.EqualTo(new[] { (0, 0) }));
            Assert.That(runner.Results, Is.Empty);
            Assert.That(exited, Is.False);
        });

        runner.OnFramebufferMutationObserved(
            succeeded: true,
            "framebuffer=0x0, state=Minimized");
        // No rendered callback is required between minimize and restore. The
        // host update loop remains alive while BeginFrame is suppressed.
        runner.OnUpdate(nextRenderedFrameIndex: 2);

        Assert.That(requests, Is.EqualTo(new[] { (0, 0), (1600, 900) }));
        runner.OnFramebufferMutationObserved(
            succeeded: true,
            "framebuffer=1600x900, state=Normal");
        runner.OnFrameRendered(3);

        Assert.Multiple(() =>
        {
            Assert.That(exited, Is.True);
            Assert.That(
                runner.Results.Select(result => (result.Name, result.Status)),
                Is.EqualTo(
                new[]
                {
                    ("minimize-zero-framebuffer", "passed"),
                    ("restore-framebuffer", "passed")
                }));
        });
    }

    [Test]
    public void AllSmoke_DoesNotReloadSceneWhileFramebufferMutationIsPending()
    {
        var options = new SampleSmokeOptions(
            SampleSmokeMode.All,
            FrameCount: 3,
            SceneReloadCount: 1,
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
            EnableFarFieldClipmap: false,
            EnableFarFieldForceAll: false,
            BaselineSnapshotDirectory: null);
        int resizeRequests = 0;
        int reloadRequests = 0;
        var runner = new SampleLifecycleSmokeRunner(
            options,
            (_, _) => resizeRequests++,
            () => reloadRequests++,
            () => { });

        for (int frame = 0; frame < 12; frame++)
        {
            runner.OnUpdate(frame);
            runner.OnFrameRendered(frame);
        }

        Assert.Multiple(() =>
        {
            Assert.That(resizeRequests, Is.EqualTo(1));
            Assert.That(reloadRequests, Is.Zero);
            Assert.That(
                runner.Results.Any(result => result.Name == "scene-reload"),
                Is.False);
        });
    }
}
