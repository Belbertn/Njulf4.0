using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationEmergencyFallbackTests
{
    [Test]
    public void EmergencyFallback_GatesSimpleDdgiWithoutMutatingAuthoredSettings()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 1.25f;

        gi.EmergencyGiFallbackEnabled = true;

        Assert.Multiple(() =>
        {
            Assert.That(gi.UseDdgi, Is.True);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.EffectiveUseRayQueryBackend, Is.False);
            Assert.That(gi.IndirectIntensity, Is.EqualTo(1.25f));
        });
    }
}
