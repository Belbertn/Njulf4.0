using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;
namespace Njulf.Tests;

[TestFixture]
public sealed class AmdDenoisingIntegrationTests
{
    [Test]
    public void OpticalMotionReplaySelectsShowcaseAndCapturesAdjacentFrames()
    {
        var options = SampleSmokeOptionsParser.Parse(["--benchmark=true", "--benchmark-trajectory", "optical-motion"]);
        Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.MaterialShowcase));
        const SampleBenchmarkTrajectoryKind kind = SampleBenchmarkTrajectoryKind.OpticalMotion;
        var first = SampleBenchmarkTrajectory.ResolveCamera(kind, 0, SampleBistroQualityCaptureVariant.SunScaleStep);
        var moved = SampleBenchmarkTrajectory.ResolveCamera(kind, 60, SampleBistroQualityCaptureVariant.SunScaleStep);
        Assert.That(moved, Is.Not.EqualTo(first));
        Assert.That(SampleBenchmarkTrajectory.ResolveCamera(kind, 60, SampleBistroQualityCaptureVariant.SunScaleStep), Is.EqualTo(moved));
        Assert.That(SampleBenchmarkQualityCheckpointCatalog.GetTemporalPairs(kind), Is.Not.Empty);
        Assert.Throws<ArgumentException>(() => SampleSmokeOptionsParser.Parse(
            ["--benchmark=true", "--benchmark-trajectory", "optical-motion", "--scene", "AnalyticalAreaLights"]));
    }
    [Test] public void SettingsSnapshotsPreserveIndependentDenoisingControls()
    {
        var settings=new RenderSettings();
        settings.Reflections.Denoiser=ReflectionDenoiser.Amd;
        settings.Shadows.AreaDenoisingEnabled=true;
        settings.OpticalDenoising.CompactLayers=true;
        settings.Reflections.AmdHalfResolution=false;
        settings.OpticalDenoising.ReducedResolutionShading=false;
        var snapshot=settings.CreateSnapshot();
        settings.Reflections.Denoiser=ReflectionDenoiser.Off;
        settings.Shadows.AreaDenoisingEnabled=false;
        settings.OpticalDenoising.CompactLayers=false;
        settings.Reflections.AmdHalfResolution=true;
        settings.OpticalDenoising.ReducedResolutionShading=true;
        Assert.Multiple(()=>{
            Assert.That(snapshot.Reflections.Denoiser,Is.EqualTo(ReflectionDenoiser.Amd));
            Assert.That(snapshot.Shadows.AreaDenoisingEnabled,Is.True);
            Assert.That(snapshot.OpticalDenoising.CompactLayers,Is.True);
            Assert.That(snapshot.Reflections.AmdHalfResolution,Is.False);
            Assert.That(snapshot.OpticalDenoising.ReducedResolutionShading,Is.False);
        });
    }
    [TestCase("full", false)]
    [TestCase("half", true)]
    public void ResolutionOverridesRoundTrip(string resolution, bool reduced)
    {
        var options=SampleSmokeOptionsParser.Parse(["--amd-reflection-resolution",resolution,"--optical-shading-resolution",resolution]);
        Assert.That(options.AmdHalfResolutionOverride,Is.EqualTo(reduced));
        Assert.That(options.OpticalReducedResolutionShadingOverride,Is.EqualTo(reduced));
    }
    [TestCase("--amd-reflection-resolution")]
    [TestCase("--optical-shading-resolution")]
    public void ResolutionOverrideRejectsUnknownValues(string option)
    {
        Assert.Throws<ArgumentException>(()=>SampleSmokeOptionsParser.Parse([option,"quarter"]));
    }
    [Test] public void AreaDenoisingIsChargedToTotalGpuTime()
    {
        var scene=new SceneRenderingData { GpuAreaRayShadowMicroseconds=100,GpuAreaShadowDenoiseMicroseconds=200, DenoisingStageMicroseconds=new Dictionary<string,long>{{"Denoising/ShadowPublish",100}} };
        Assert.That(RendererDiagnosticsAssembler.CalculateGpuFrameMicroseconds(scene),Is.EqualTo(300));
    }
    [Test] public void CaptureOverridesSelectAllThreePaths()
    {
        var options=SampleSmokeOptionsParser.Parse(["--reflection-denoiser","amd","--area-denoising","on","--optical-denoising","compact"]);
        Assert.That(options.ReflectionDenoiserOverride,Is.EqualTo("amd"));
        Assert.That(options.AreaDenoisingOverride,Is.True);
        Assert.That(options.OpticalDenoisingMode,Is.EqualTo("compact"));
    }
}
