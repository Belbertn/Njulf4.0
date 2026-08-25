using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AnalyticalAreaLightShaderContractTests
{
    [Test]
    public void ForwardRasterPath_UsesLtcAndScheduledAreaVisibility()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string ltc = ReadRepoText("Njulf.Shaders", "area_lighting.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Contain("area_lighting.glsl"));
            Assert.That(forward, Does.Contain("EvaluateNjulfAreaLightLtc("));
            Assert.That(forward, Does.Contain("EvaluateAreaRayShadowMask("));
            Assert.That(forward,
                Does.Contain("if (ForwardReflectionCaptureEnabled() ||"));
            Assert.That(forward,
                Does.Contain("AREA_RAY_SHADOW_MASK_BUFFER_BASE_INDEX"));
            Assert.That(ltc,
                Does.Contain("AREA_LIGHT_LTC_MATRIX_TEXTURE_INDEX"));
            Assert.That(ltc,
                Does.Contain("AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX"));
            Assert.That(ltc, Does.Contain("NjulfLtcQuadIntegral("));
            Assert.That(ltc, Does.Contain("NjulfLtcDiskIntegral("));
            Assert.That(ltc, Does.Contain("NjulfLtcTubeIntegral("));
        });
    }

    [Test]
    public void DdgiHitPath_UsesSurfaceAndDiscreteLightPdfs()
    {
        string hit = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(hit,
                Does.Contain("DdgiSampleAreaLightSurface("));
            Assert.That(hit,
                Does.Contain("distanceSquared * emitter.areaPdf"));
            Assert.That(hit,
                Does.Contain("float(sampleCount) * lightSample.pdf"));
            Assert.That(hit,
                Does.Contain("DDGI_AREA_LIGHT_VISIBILITY_RAY_COUNTER"));
            Assert.That(hit, Does.Contain("TraceLightVisibility("));
        });
    }

    [Test]
    public void AreaShadowPath_SamplesEmittersAndPacksFourVisibilitySlots()
    {
        string shader = ReadRepoText("Njulf.Shaders", "area_ray_shadow.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("GL_EXT_ray_query"));
            Assert.That(shader,
                Does.Contain("directional_ray_visibility.glsl"));
            Assert.That(shader,
                Does.Contain("NjulfSampleAreaLightSurface("));
            Assert.That(shader,
                Does.Contain("DirectionalTraceVisibility("));
            Assert.That(shader,
                Does.Contain("min(pc.SelectedLightCount, 4u)"));
            Assert.That(shader,
                Does.Contain("uint shift = slot * 8u"));
            Assert.That(shader,
                Does.Contain("WriteStorageWord(pc.OutputBufferIndex, pixelIndex, packed)"));
        });
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
