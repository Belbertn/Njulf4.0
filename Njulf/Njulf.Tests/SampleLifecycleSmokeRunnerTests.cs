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
            ForceMissingAssets: false);
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
            ForceMissingAssets: true);
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
}
