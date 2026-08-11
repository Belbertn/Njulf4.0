using Microsoft.Extensions.DependencyInjection;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class VulkanExtOpacityMicromapDeviceEnablementTests
{
    [Test]
    public void OptionalFeatureParsing_RequiresAnExplicitTrueValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("1"), Is.True);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("true"), Is.True);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("TRUE"), Is.True);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean(null), Is.False);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("0"), Is.False);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("false"), Is.False);
            Assert.That(VulkanOptionalDeviceFeatures.ParseBoolean("yes"), Is.False);
        });
    }

    [Test]
    public void DeviceEnablement_RequiresRequestRayQueryAndEveryExtDependency()
    {
        VulkanExtOpacityMicromapDeviceSupport supported = FullySupported();
        var requested = new VulkanOptionalDeviceFeatures(
            EnableExtOpacityMicromap: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    VulkanOptionalDeviceFeatures.Disabled,
                    supported,
                    rayQuerySupported: true),
                Is.False,
                "An advertised EXT implementation must not change device creation without a request.");
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported,
                    rayQuerySupported: false),
                Is.False,
                "C1 only augments the existing ray-query route.");
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported,
                    rayQuerySupported: true),
                Is.True);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { ExtensionAdvertised = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { MicromapFeatureSupported = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { AccelerationStructureExtensionSupported = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { AccelerationStructureFeatureSupported = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { BufferDeviceAddressSupported = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { DeferredHostOperationsExtensionSupported = false },
                    rayQuerySupported: true),
                Is.False);
            Assert.That(
                VulkanContext.ShouldEnableOpacityMicromapExt(
                    requested,
                    supported with { MaximumFourStateSubdivisionLevel = 0U },
                    rayQuerySupported: true),
                Is.False);
        });
    }

    [Test]
    public void RenderingOptions_ExposeAnExplicitProgrammaticOverride()
    {
        var options = new RenderingOptions
        {
            OptionalDeviceFeatures = VulkanOptionalDeviceFeatures.Disabled
        };

        options.EnableExtOpacityMicromap = true;

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableExtOpacityMicromap, Is.True);
            Assert.That(options.OptionalDeviceFeatures.EnableExtOpacityMicromap,
                Is.True);
        });
    }

    [Test]
    public void DefaultC1Mode_RequestsTheOptionalDeviceChainButExplicitDisableWins()
    {
        var settings = new GlobalIlluminationSettings();
        Assert.That(
            RenderingOptions.ShouldRequestExtOpacityMicromap(settings),
            Is.True);

        settings.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Off;
        Assert.That(
            RenderingOptions.ShouldRequestExtOpacityMicromap(settings),
            Is.False);

        var explicitlyDisabled = new RenderingOptions
        {
            OptionalDeviceFeatures = VulkanOptionalDeviceFeatures.Disabled
        };
        Assert.That(explicitlyDisabled.EnableExtOpacityMicromap, Is.False);
    }

    [Test]
    public void RenderingOptions_ExposePreInitializationAdvancedGiState()
    {
        string prerequisitePath = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-prerequisite.json");
        string qualificationPath = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-qualification.json");
        string runtimeEvidencePath = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-runtime-evidence.json");
        var options = new RenderingOptions
        {
            AdvancedGiPrerequisiteManifestPath = prerequisitePath,
            AdvancedGiQualificationManifestPath = qualificationPath,
            AdvancedGiRuntimeEvidenceBundlePath = runtimeEvidencePath
        };

        options.InitialSettings.GlobalIllumination
            .SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.ExactCompacted;

        Assert.Multiple(() =>
        {
            Assert.That(options.AdvancedGiPrerequisiteManifestPath,
                Is.EqualTo(Path.GetFullPath(prerequisitePath)));
            Assert.That(options.AdvancedGiQualificationManifestPath,
                Is.EqualTo(Path.GetFullPath(qualificationPath)));
            Assert.That(options.AdvancedGiRuntimeEvidenceBundlePath,
                Is.EqualTo(Path.GetFullPath(runtimeEvidencePath)));
            Assert.That(options.InitialSettings.GlobalIllumination
                    .SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
        });
    }

    private static VulkanExtOpacityMicromapDeviceSupport FullySupported() => new(
        ExtensionAdvertised: true,
        MicromapFeatureSupported: true,
        AccelerationStructureExtensionSupported: true,
        AccelerationStructureFeatureSupported: true,
        BufferDeviceAddressSupported: true,
        DeferredHostOperationsExtensionSupported: true,
        MaximumFourStateSubdivisionLevel: 4U);
}
