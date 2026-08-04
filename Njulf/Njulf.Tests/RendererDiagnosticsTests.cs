using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererDiagnosticsTests
{
    [Test]
    public void GlobalIlluminationDefaults_SelectSimpleDdgiOnly()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;

        Assert.Multiple(() =>
        {
            Assert.That(gi.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(gi.UseDdgi, Is.True);
            Assert.That(gi.EffectiveUseDdgi, Is.True);
        });
    }

    [Test]
    public void EmergencyFallback_DisablesSimpleDdgiWithoutChangingAuthoredSelection()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.EmergencyGiFallbackEnabled = true;

        Assert.Multiple(() =>
        {
            Assert.That(gi.UseDdgi, Is.True);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.False);
        });
    }

    [Test]
    public void QualityTier_PreservesSimpleDdgiConfiguration()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiUltra);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiUltra));
            Assert.That(settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame, Is.GreaterThan(0));
            Assert.That(settings.GlobalIllumination.SimpleDdgiRaysPerProbe, Is.GreaterThan(0));
        });
    }
}
