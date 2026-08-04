using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SamplePlazaGlobalIlluminationTests
{
    [Test]
    public void ValidationProfile_ConfiguresSimpleDdgi()
    {
        var settings = new RenderSettings();
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.SimpleDdgiRingCount, Is.GreaterThan(0));
        });
    }
}
