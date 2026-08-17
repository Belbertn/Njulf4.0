using System.Numerics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBistroLightingProfileTests
{
    [Test]
    public void DirectionalKey_PreservesFbxRadianceAndCastsShadows()
    {
        Light light = SampleBistroLightingProfile.CreateDirectionalKey();
        Vector3 reconstructedRadiance = light.Color * light.Intensity;

        Assert.Multiple(() =>
        {
            Assert.That(light.Type, Is.EqualTo(LightType.Directional));
            Assert.That(light.Direction.Length(), Is.EqualTo(1.0f).Within(0.00001f));
            Assert.That(
                Vector3.Distance(light.Direction, SampleBistroLightingProfile.SourceDirection),
                Is.LessThan(0.00001f));
            Assert.That(
                Vector3.Distance(reconstructedRadiance, SampleBistroLightingProfile.SourceRadiance),
                Is.LessThan(0.0001f));
            Assert.That(light.CastsShadows, Is.True);
            Assert.That(light.ShadowStrength, Is.EqualTo(1.0f));
            Assert.That(light.ShadowPriority, Is.EqualTo(10));
        });
    }
}
