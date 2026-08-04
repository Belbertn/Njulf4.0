using Njulf.Rendering;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DeviceRequirementReportTests
{
    [Test]
    public void SimpleDdgiMode_RemainsTheOnlyEnabledGiMode()
    {
        var settings = new GlobalIlluminationSettings
        {
            Enabled = true,
            Mode = GlobalIlluminationMode.Ddgi,
            UseDdgi = true,
            UseRayQueryBackend = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(VulkanRenderer.ResolveEffectiveGlobalIlluminationMode(settings, rayQuerySupported: false), Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(VulkanRenderer.ResolveEffectiveGlobalIlluminationMode(settings, rayQuerySupported: true), Is.EqualTo(GlobalIlluminationMode.Ddgi));
        });
    }
}
