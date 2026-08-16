using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalRayShadowShaderContractTests
{
    [Test]
    public void HardMask_IsDeterministicFailClosedAndUsesSharedAlphaContract()
    {
        string shader = ReadRepoText(
            "Njulf.Shaders", "directional_ray_shadow.comp");
        string visibility = ReadRepoText(
            "Njulf.Shaders", "directional_ray_visibility.glsl");
        string sharedAlpha = ReadRepoText(
            "Njulf.Shaders", "ray_scene_alpha.glsl");
        string spatial = ReadRepoText(
            "Njulf.Shaders", "directional_shadow_spatial.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("GL_EXT_ray_query"));
            Assert.That(shader,
                Does.Contain("directional_ray_visibility.glsl"));
            Assert.That(visibility,
                Does.Contain("instanceMask & 0xffu"));
            Assert.That(visibility,
                Does.Contain("RaySceneCandidateBlocksDirectionalShadow"));
            Assert.That(visibility,
                Does.Contain("DIRECTIONAL_RAY_MAX_ALPHA_CANDIDATES"));
            Assert.That(shader,
                Does.Contain("ReconstructNormalAndFootprint"));
            Assert.That(shader,
                Does.Contain("SelectClosestDerivative"));
            Assert.That(shader, Does.Contain("atomicOr("));
            Assert.That(shader, Does.Contain("shared uint PackedVisibilityBytes[64]"));
            Assert.That(shader,
                Does.Contain("WriteStorageWord(pc.OutputBufferIndex, pixelIndex >> 2u, packed)"));
            Assert.That(spatial,
                Does.Contain("shared uint PackedVisibilityBytes[64]"));
            Assert.That(shader, Does.Contain("smoothstep("));
            Assert.That(visibility, Does.Contain("rayQueryTerminateEXT"));
            Assert.That(shader, Does.Not.Contain("random"));
            Assert.That(shader, Does.Not.Contain("history"));
            Assert.That(sharedAlpha,
                Does.Contain("DdgiAlphaCandidateOccupiesOpaqueTransport"));
            Assert.That(sharedAlpha,
                Does.Contain("material.NormalScaleBias.y"));
            Assert.That(sharedAlpha,
                Does.Contain("if (!RaySceneQueryInstanceIsValid(instance))"));
            Assert.That(sharedAlpha, Does.Contain("return true;"));
        });
    }

    [Test]
    public void SoftRayHistory_AdvancesSequenceAndAccumulatesHitMissOutcomes()
    {
        string visibility = ReadRepoText(
            "Njulf.Shaders", "directional_ray_visibility.glsl");
        string temporal = ReadRepoText(
            "Njulf.Shaders", "directional_shadow_temporal.comp");
        string spatial = ReadRepoText(
            "Njulf.Shaders", "directional_shadow_spatial.comp");

        Assert.Multiple(() =>
        {
            Assert.That(visibility,
                Does.Contain("frameIndex * 4u + sampleIndex"));
            Assert.That(visibility,
                Does.Not.Contain("frameIndex * 0xc2b2ae35u"));
            Assert.That(temporal,
                Does.Not.Contain("previousAge < max(pc.MaximumHistoryAge"));
            Assert.That(temporal,
                Does.Contain("1.0 - 1.0 / float(pc.MaximumHistoryAge)"));
            Assert.That(temporal,
                Does.Contain("else if (!currentHasBlocker && historyValid && historyHasBlocker)"));
            Assert.That(spatial,
                Does.Contain("max(hitWeight, 0.25)"));
        });
    }

    [Test]
    public void CascadedSampling_SharesGatherPathAndAdaptiveRadius()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string resolve = ReadRepoText(
            "Njulf.Shaders", "directional_csm_resolve.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string sampling = ReadRepoText(
            "Njulf.Shaders", "directional_csm_sampling.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(forward,
                Does.Contain("directional_csm_sampling.glsl"));
            Assert.That(resolve,
                Does.Contain("directional_csm_sampling.glsl"));
            Assert.That(sampling, Does.Contain("textureGather("));
            Assert.That(sampling,
                Does.Contain("vec4(gathered.w, gathered.z, gathered.x, gathered.y)"));
            Assert.That(common,
                Does.Contain("ResolveDirectionalShadowPcfRadius"));
        });
    }

    [Test]
    public void ForwardUsesMaskOnlyForOpaqueDepthOwnerAndRetainsLayeredCsmFallback()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(forward,
                Does.Contain("DirectionalRayShadowMaskSupportsReceiver"));
            Assert.That(forward,
                Does.Contain("DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_BASE_INDEX"));
            Assert.That(forward,
                Does.Contain("EvaluateDirectionalShadowForEffectiveMode"));
            Assert.That(forward,
                Does.Contain("abs(ownerDepth - gl_FragCoord.z)"));
            Assert.That(forward,
                Does.Contain("effectiveMode == 1u"));
            Assert.That(forward, Does.Contain("pixelIndex >> 2u"));
            Assert.That(forward, Does.Contain("& 0xffu"));
            Assert.That(forward, Does.Contain("return min("));
            Assert.That(forward,
                Does.Contain("EvaluateDirectionalTransparentRay"));
        });
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
