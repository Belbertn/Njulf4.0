using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DeviceRequirementReportTests
{
    [Test]
    public void GroupsMissingExtensionsAndFeatures()
    {
        var report = new DeviceRequirementReport(
            "Test GPU",
            1,
            2,
            "1.3.0",
            "1.0.0",
            new[] { "VK_EXT_debug_utils" },
            new[] { "VK_LAYER_KHRONOS_validation" },
            new[] { "VK_EXT_mesh_shader" },
            new[] { "taskShader" },
            new[] { "present" },
            IsSupported: false);

        string summary = report.FormatSummary();

        Assert.That(summary, Does.Contain("missing instance extensions: VK_EXT_debug_utils"));
        Assert.That(summary, Does.Contain("missing instance layers: VK_LAYER_KHRONOS_validation"));
        Assert.That(summary, Does.Contain("missing device extensions: VK_EXT_mesh_shader"));
        Assert.That(summary, Does.Contain("missing features: taskShader"));
        Assert.That(summary, Does.Contain("missing queue families: present"));
    }

    [Test]
    public void RayQueryCapability_RequiresAllOptionalExtensionsAndFeatures()
    {
        var extensions = new HashSet<string>
        {
            VulkanContext.AccelerationStructureExtensionName,
            VulkanContext.RayQueryExtensionName,
            VulkanContext.Spirv14ExtensionName,
            VulkanContext.ShaderFloatControlsExtensionName
        };

        VulkanContext.RayQueryCapability supported = VulkanContext.EvaluateRayQuerySupport(
            extensions,
            accelerationStructureFeature: true,
            rayQueryFeature: true);
        VulkanContext.RayQueryCapability missing = VulkanContext.EvaluateRayQuerySupport(
            new HashSet<string> { VulkanContext.AccelerationStructureExtensionName },
            accelerationStructureFeature: false,
            rayQueryFeature: false);

        Assert.Multiple(() =>
        {
            Assert.That(supported.Supported, Is.True);
            Assert.That(supported.MissingRequirements, Is.Empty);
            Assert.That(missing.Supported, Is.False);
            Assert.That(missing.MissingRequirements, Does.Contain(VulkanContext.RayQueryExtensionName));
            Assert.That(missing.MissingRequirements, Does.Contain(VulkanContext.Spirv14ExtensionName));
            Assert.That(missing.MissingRequirements, Does.Contain(VulkanContext.ShaderFloatControlsExtensionName));
            Assert.That(missing.MissingRequirements, Does.Contain("accelerationStructure"));
            Assert.That(missing.MissingRequirements, Does.Contain("rayQuery"));
        });
    }

    [Test]
    public void RayQueryHybridMode_FallsBackToHybridWhenRayQueryUnsupported()
    {
        var settings = new GlobalIlluminationSettings
        {
            Enabled = true,
            Mode = GlobalIlluminationMode.RayQueryHybrid,
            UseRayQueryBackend = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                Njulf.Rendering.VulkanRenderer.ResolveEffectiveGlobalIlluminationMode(settings, rayQuerySupported: false),
                Is.EqualTo(GlobalIlluminationMode.Hybrid));
            Assert.That(
                Njulf.Rendering.VulkanRenderer.ResolveEffectiveGlobalIlluminationMode(settings, rayQuerySupported: true),
                Is.EqualTo(GlobalIlluminationMode.RayQueryHybrid));
        });
    }
}
