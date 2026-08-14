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
