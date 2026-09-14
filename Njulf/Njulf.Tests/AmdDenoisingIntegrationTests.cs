using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;
namespace Njulf.Tests;

[TestFixture]
public sealed class AmdDenoisingIntegrationTests
{
    [Test] public void SettingsSnapshotsPreserveIndependentDenoisingControls()
    {
        var settings=new RenderSettings();
        settings.Reflections.Denoiser=ReflectionDenoiser.Amd;
        settings.Shadows.AreaDenoisingEnabled=true;
        settings.OpticalDenoising.CompactLayers=true;
        var snapshot=settings.CreateSnapshot();
        settings.Reflections.Denoiser=ReflectionDenoiser.Off;
        settings.Shadows.AreaDenoisingEnabled=false;
        settings.OpticalDenoising.CompactLayers=false;
        Assert.Multiple(()=>{
            Assert.That(snapshot.Reflections.Denoiser,Is.EqualTo(ReflectionDenoiser.Amd));
            Assert.That(snapshot.Shadows.AreaDenoisingEnabled,Is.True);
            Assert.That(snapshot.OpticalDenoising.CompactLayers,Is.True);
        });
    }
    [Test] public void AreaDenoisingIsChargedToTotalGpuTime()
    {
        var scene=new SceneRenderingData { GpuAreaRayShadowMicroseconds=100,GpuAreaShadowDenoiseMicroseconds=200 };
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
