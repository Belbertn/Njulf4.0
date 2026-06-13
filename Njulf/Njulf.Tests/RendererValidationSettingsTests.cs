using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererValidationSettingsTests
{
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
}
