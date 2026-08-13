using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AutoExposureSettingsTests
{
    [Test]
    public void DefaultsUsePhotographicHistogramMetering()
    {
        var settings = new AutoExposureSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.TargetLuminance, Is.EqualTo(0.125f));
            Assert.That(settings.MinExposure, Is.EqualTo(0.25f));
            Assert.That(settings.MaxExposure, Is.EqualTo(4.0f));
            Assert.That(settings.LowPercentile, Is.EqualTo(70.0f));
            Assert.That(settings.HighPercentile, Is.EqualTo(95.0f));
            Assert.That(settings.DarkToLightAdaptationSpeed, Is.EqualTo(3.0f));
            Assert.That(settings.LightToDarkAdaptationSpeed, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void PercentilesAlwaysRetainANonEmptyHistogramRange()
    {
        var settings = new AutoExposureSettings
        {
            LowPercentile = 99.99f,
            HighPercentile = 0.0f
        };

        Assert.That(settings.HighPercentile, Is.GreaterThan(settings.LowPercentile));
        Assert.That(settings.HighPercentile, Is.LessThanOrEqualTo(100.0f));
    }

    [Test]
    public void PushConstantsRetainMatchingScalarLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUAutoExposurePushConstants>(), Is.EqualTo(80));
            Assert.That(
                Marshal.OffsetOf<GPUAutoExposurePushConstants>(
                    nameof(GPUAutoExposurePushConstants.DarkToLightAdaptationSpeed)).ToInt32(),
                Is.EqualTo(40));
            Assert.That(
                Marshal.OffsetOf<GPUAutoExposurePushConstants>(
                    nameof(GPUAutoExposurePushConstants.LightToDarkAdaptationSpeed)).ToInt32(),
                Is.EqualTo(44));
            Assert.That(
                Marshal.OffsetOf<GPUAutoExposurePushConstants>(
                    nameof(GPUAutoExposurePushConstants.LowPercentile)).ToInt32(),
                Is.EqualTo(56));
            Assert.That(
                Marshal.OffsetOf<GPUAutoExposurePushConstants>(
                    nameof(GPUAutoExposurePushConstants.HighPercentile)).ToInt32(),
                Is.EqualTo(60));
            Assert.That(
                Marshal.OffsetOf<GPUAutoExposurePushConstants>(
                    nameof(GPUAutoExposurePushConstants.Mode)).ToInt32(),
                Is.EqualTo(64));
        });
    }
}
