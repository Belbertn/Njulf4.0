using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleVolumetricTemporalCaptureTests
{
    [TestCase(RenderQualityPreset.High, 2560, 1440, 222u, 128u, 80u, 2_000L)]
    [TestCase(RenderQualityPreset.DdgiHigh, 2560, 1440, 222u, 128u, 80u, 2_000L)]
    [TestCase(RenderQualityPreset.Ultra, 3840, 2160, 304u, 175u, 104u, 8_000L)]
    public void ContractLocksApprovedResolutionGridAndBudget(
        RenderQualityPreset preset,
        int width,
        int height,
        uint gridWidth,
        uint gridHeight,
        uint gridDepth,
        long gpuBudget)
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleVolumetricTemporalCaptureContract.GetDimensions(preset),
                Is.EqualTo((width, height)));
            Assert.That(
                SampleVolumetricTemporalCaptureContract.GetExpectedGrid(preset),
                Is.EqualTo((gridWidth, gridHeight, gridDepth)));
            Assert.That(
                SampleVolumetricTemporalCaptureContract
                    .GetGpuBudgetMicroseconds(preset),
                Is.EqualTo(gpuBudget));
            Assert.That(
                SampleVolumetricTemporalCaptureContract.CreateFingerprint(preset),
                Does.StartWith("sha256:"));
        });
    }

    [Test]
    public void ContractRejectsLowResolutionPresets()
    {
        Assert.That(
            () => SampleVolumetricTemporalCaptureContract.GetDimensions(
                RenderQualityPreset.Low),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
