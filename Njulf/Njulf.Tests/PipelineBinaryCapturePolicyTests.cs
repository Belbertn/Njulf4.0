using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PipelineBinaryCapturePolicyTests
{
    [TestCase(1, true, false, false, true, false)]
    [TestCase(3, true, false, false, true, false)]
    [TestCase(2, true, true, true, false, true)]
    [TestCase(2, false, false, false, true, false)]
    [TestCase(0, true, true, false, true, false)]
    [TestCase(0, true, false, true, true, false)]
    [TestCase(0, true, false, false, false, false)]
    [TestCase(0, true, false, false, true, true)]
    public void ShouldCapture_UsesExplicitCaptureOrColdAutoMissPolicy(
        int mode,
        bool storeAvailable,
        bool driverInternalCache,
        bool applicationCacheLikelyWarm,
        bool autoCaptureEnabled,
        bool expected)
    {
        Assert.That(
            PipelineBinaryCapturePolicy.ShouldCapture(
                (RendererPipelineBinaryCacheMode)mode,
                storeAvailable,
                driverInternalCache,
                applicationCacheLikelyWarm,
                autoCaptureEnabled),
            Is.EqualTo(expected));
    }

    [TestCase(null, true)]
    [TestCase("on", true)]
    [TestCase("true", true)]
    [TestCase("1", true)]
    [TestCase("off", false)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    public void AutoCaptureKillSwitchSupportsExplicitOnAndOff(
        string? requested,
        bool expected)
    {
        Assert.That(
            RendererBuildConfiguration
                .ResolvePipelineBinaryAutoCaptureEnabled(requested),
            Is.EqualTo(expected));
    }

    [Test]
    public void InvalidAutoCaptureKillSwitchIsRejected()
    {
        Assert.That(
            () => RendererBuildConfiguration
                .ResolvePipelineBinaryAutoCaptureEnabled("sometimes"),
            Throws.InvalidOperationException);
    }
}
