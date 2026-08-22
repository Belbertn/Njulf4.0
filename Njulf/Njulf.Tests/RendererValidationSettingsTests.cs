using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererValidationSettingsTests
{
    [Test]
    public void BuildTierDefaultsToExpectedValidationMode()
    {
#if DEBUG || NJULF_DEVELOPMENT
        const RendererValidationMode expectedMode = RendererValidationMode.Standard;
#else
        const RendererValidationMode expectedMode = RendererValidationMode.Off;
#endif

        Assert.Multiple(() =>
        {
            Assert.That(RendererValidationSettings.Default.Mode, Is.EqualTo(expectedMode));
            Assert.That(
                Microsoft.Extensions.DependencyInjection.RenderingOptions.DefaultEnableValidation,
                Is.EqualTo(expectedMode != RendererValidationMode.Off));
        });
    }

    [Test]
    public void DebugUtilsAndLabelsRemainEnabledWhenValidationIsOff()
    {
        RendererValidationSettings settings = RendererValidationSettings.Default with
        {
            Mode = RendererValidationMode.Off
        };

        Assert.Multiple(() =>
        {
            Assert.That(settings.EnableValidation, Is.False);
            Assert.That(settings.EnableDebugUtils, Is.True);
            Assert.That(settings.EnableDebugLabels, Is.True);
        });
    }

    [TestCase("off", RendererValidationMode.Off)]
    [TestCase("standard", RendererValidationMode.Standard)]
    [TestCase("gpu", RendererValidationMode.GpuAssisted)]
    [TestCase("sync", RendererValidationMode.Synchronization)]
    [TestCase("all", RendererValidationMode.All)]
    public void ParsesOffStandardGpuSyncAndAll(string value, RendererValidationMode expected)
    {
        bool parsed = RendererValidationSettings.TryParseMode(value, out RendererValidationMode mode, out string? error);

        Assert.That(parsed, Is.True);
        Assert.That(error, Is.Null);
        Assert.That(mode, Is.EqualTo(expected));
    }

    [Test]
    public void InvalidValueFailsBeforeRendererConstruction()
    {
        bool parsed = RendererValidationSettings.TryParseMode("maximum", out _, out string? error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain("Invalid renderer validation mode"));
    }

    [TestCase(RendererValidationMode.Off, false, false, false, false, 0)]
    [TestCase(RendererValidationMode.Standard, false, false, false, false, 0)]
    [TestCase(RendererValidationMode.GpuAssisted, true, true, false, false, 2)]
    [TestCase(RendererValidationMode.Synchronization, false, false, true, false, 1)]
    [TestCase(RendererValidationMode.All, true, true, true, false, 3)]
    public void SelectsExactVulkanValidationFeatures(
        RendererValidationMode mode,
        bool gpuAssisted,
        bool reserveBindingSlot,
        bool synchronization,
        bool bestPractices,
        int featureCount)
    {
        RendererValidationFeatureSelection selection = (RendererValidationSettings.Default with
        {
            Mode = mode,
            EnableBestPractices = false
        }).FeatureSelection;

        Assert.Multiple(() =>
        {
            Assert.That(selection.GpuAssisted, Is.EqualTo(gpuAssisted));
            Assert.That(selection.ReserveGpuAssistedBindingSlot, Is.EqualTo(reserveBindingSlot));
            Assert.That(selection.SynchronizationValidation, Is.EqualTo(synchronization));
            Assert.That(selection.BestPractices, Is.EqualTo(bestPractices));
            Assert.That(selection.EnabledFeatureCount, Is.EqualTo(featureCount));
        });
    }

    [Test]
    public void BestPracticesIsEnabledOnlyWhenValidationIsActive()
    {
        RendererValidationFeatureSelection active = (RendererValidationSettings.Default with
        {
            Mode = RendererValidationMode.Standard,
            EnableBestPractices = true
        }).FeatureSelection;
        RendererValidationFeatureSelection inactive = (RendererValidationSettings.Default with
        {
            Mode = RendererValidationMode.Off,
            EnableBestPractices = true
        }).FeatureSelection;

        Assert.Multiple(() =>
        {
            Assert.That(active.BestPractices, Is.True);
            Assert.That(active.EnabledFeatureCount, Is.EqualTo(1));
            Assert.That(inactive, Is.EqualTo(RendererValidationFeatureSelection.None));
        });
    }
}
