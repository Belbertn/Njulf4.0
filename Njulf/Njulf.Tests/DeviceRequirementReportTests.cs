using Njulf.Rendering.Diagnostics;
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
}
