using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class AntiAliasingRepairTests
{
    [TestCase(AntiAliasingMode.SmaaLow, 0, 0.50f, 0.15f, 4, 0, 0.0f, false, false)]
    [TestCase(AntiAliasingMode.SmaaMedium, 1, 0.75f, 0.10f, 8, 0, 0.0f, false, false)]
    [TestCase(AntiAliasingMode.SmaaHigh, 2, 1.00f, 0.10f, 16, 8, 25.0f, true, true)]
    public void SmaaModes_ExposeCanonicalDistinctPresets(
        AntiAliasingMode mode,
        int quality,
        float resolutionScale,
        float threshold,
        int searchSteps,
        int diagonalSteps,
        float cornerRounding,
        bool diagonalEnabled,
        bool cornerEnabled)
    {
        var settings = new AntiAliasingSettings { Mode = mode };

        Assert.Multiple(() =>
        {
            Assert.That(settings.EffectiveSmaaQuality, Is.EqualTo(quality));
            Assert.That(settings.EffectiveSmaaResolutionScale, Is.EqualTo(resolutionScale));
            Assert.That(settings.EffectiveSmaaThreshold, Is.EqualTo(threshold));
            Assert.That(settings.EffectiveSmaaMaxSearchSteps, Is.EqualTo(searchSteps));
            Assert.That(settings.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(diagonalSteps));
            Assert.That(settings.EffectiveSmaaCornerRounding, Is.EqualTo(cornerRounding));
            Assert.That(settings.EffectiveSmaaDiagonalEnabled, Is.EqualTo(diagonalEnabled));
            Assert.That(settings.EffectiveSmaaCornerEnabled, Is.EqualTo(cornerEnabled));
            Assert.That(settings.EffectiveSmaaSpatialSampleCount, Is.EqualTo(1));
            Assert.That(settings.EffectiveSmaaUsesSpatialMultisampling, Is.False);
        });
    }

    [TestCase(AntiAliasingMode.SmaaLow, 800u, 450u)]
    [TestCase(AntiAliasingMode.SmaaMedium, 1200u, 675u)]
    [TestCase(AntiAliasingMode.SmaaHigh, 1600u, 900u)]
    [TestCase(AntiAliasingMode.Taa, 1600u, 900u)]
    public void AntiAliasingExtent_MatchesTheSelectedQuality(
        AntiAliasingMode mode,
        uint expectedWidth,
        uint expectedHeight)
    {
        Extent2D actual = RenderTargetManager.CalculateAntiAliasingExtent(
            new Extent2D { Width = 1600, Height = 900 },
            mode);

        Assert.That((actual.Width, actual.Height),
            Is.EqualTo((expectedWidth, expectedHeight)));
    }

    [Test]
    public void CanonicalSmaaLookupPayloads_HaveExactDimensionsAndContent()
    {
        byte[] area = SmaaLookupData.DecodeArea();
        byte[] search = SmaaLookupData.DecodeSearch();

        Assert.Multiple(() =>
        {
            Assert.That(area, Has.Length.EqualTo(checked((int)(160u * 560u * 2u))));
            Assert.That(search, Has.Length.EqualTo(checked((int)(64u * 16u))));
            Assert.That(
                Convert.ToHexString(SHA256.HashData(area)),
                Is.EqualTo("35065CEF2A02CABCAD711D6BF430239AE64E27D71C4E4FA06F29CCE2C992F0D2"));
            Assert.That(
                Convert.ToHexString(SHA256.HashData(search)),
                Is.EqualTo("3694EAE5E9D44B8EBB4415A13F8C7B94DC08A2FC86658434D771C4610FE5744D"));
        });
    }

    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void HaltonJitter_IsExactlyZeroMeanAcrossEachSupportedCycle(int sampleCount)
    {
        float sumX = 0.0f;
        float sumY = 0.0f;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            var jitter = AntiAliasingJitter.GetHaltonJitter(
                sample,
                sampleCount,
                1600,
                900,
                enabled: true);
            sumX += jitter.X;
            sumY += jitter.Y;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sumX, Is.EqualTo(0.0f).Within(1e-7f));
            Assert.That(sumY, Is.EqualTo(0.0f).Within(1e-7f));
        });
    }

    [Test]
    public void TaaDefaultsFavorStableHistoryAndRejectInvalidInputs()
    {
        var settings = new AntiAliasingSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.TaaFeedbackMin, Is.EqualTo(0.85f));
            Assert.That(settings.TaaFeedbackMax, Is.EqualTo(0.95f));
        });

        settings.TaaFeedbackMin = 0.1f;
        settings.TaaVelocityRejectionScale = float.NaN;
        Assert.Multiple(() =>
        {
            Assert.That(settings.TaaFeedbackMin, Is.EqualTo(0.5f));
            Assert.That(settings.TaaVelocityRejectionScale, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void TaaPushAbi_AlignsJitterVectorsForStd430()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUAntiAliasingPushConstants>(), Is.EqualTo(120));
            Assert.That(
                Marshal.OffsetOf<GPUAntiAliasingPushConstants>(
                    nameof(GPUAntiAliasingPushConstants.TaaCurrentJitterUv)).ToInt32(),
                Is.EqualTo(104));
            Assert.That(
                Marshal.OffsetOf<GPUAntiAliasingPushConstants>(
                    nameof(GPUAntiAliasingPushConstants.TaaPreviousJitterUv)).ToInt32(),
                Is.EqualTo(112));
        });
    }

    [Test]
    public void TaaResolve_ReprojectsWithRawVelocityButRejectsWithPhysicalVelocityAndDepth()
    {
        string shader = ReadRepoText("Njulf.Shaders", "taa_resolve.frag");
        string pass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "AntiAliasingPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec2 historyUv = inUv - rawVelocity;"));
            Assert.That(shader, Does.Contain("vec2 physicalVelocity = rawVelocity - jitterVelocity;"));
            Assert.That(shader, Does.Contain("secondMoment - firstMoment * firstMoment"));
            Assert.That(shader, Does.Contain("float previousDepth = historySample.a;"));
            Assert.That(shader, Does.Contain("bool depthConsistent"));
            Assert.That(shader, Does.Not.Contain("(current - localAverage)"));
            Assert.That(pass, Does.Contain("sceneData.MotionVectorsEnabled != 0"));
            Assert.That(pass, Does.Contain("sceneData.CaptureCameraCutSerial"));
            Assert.That(pass, Does.Contain("sceneData.SceneContentRevision"));
        });
    }

    [Test]
    public void SmaaShaders_UseCanonicalLookupAddressingAndNeighborhoodChannels()
    {
        string edge = ReadRepoText("Njulf.Shaders", "smaa_edge.frag");
        string blend = ReadRepoText("Njulf.Shaders", "smaa_blend_weight.frag");
        string neighborhood = ReadRepoText("Njulf.Shaders", "smaa_neighborhood.frag");

        Assert.Multiple(() =>
        {
            Assert.That(edge, Does.Contain("2.0 * delta"));
            Assert.That(blend, Does.Contain("vec2(160.0, 560.0)"));
            Assert.That(blend, Does.Contain("vec2(64.0, 16.0)"));
            Assert.That(blend, Does.Contain("SearchLength("));
            Assert.That(blend, Does.Contain("AreaDiagonal("));
            Assert.That(neighborhood, Does.Contain("weights.wz = SampleBlend(inUv).xz;"));
            Assert.That(neighborhood, Does.Not.Contain("SmaaQuality"));
        });
    }

    private static string ReadRepoText(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }
}
