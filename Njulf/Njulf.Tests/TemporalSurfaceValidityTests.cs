using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TemporalSurfaceValidityTests
{
    [Test]
    public void Codec_RoundTripsNormalAndPreservesFourTapMask()
    {
        Vector3 normal = new Vector3(0.37f, -0.51f, 0.78f).Normalized();
        uint packed = TemporalSurfaceValidityCodec.PackNormal(normal, 0b1010u);
        Vector3 decoded = TemporalSurfaceValidityCodec.UnpackNormal(packed);

        Assert.Multiple(() =>
        {
            Assert.That(TemporalSurfaceValidityCodec.AbiVersion, Is.EqualTo(1u));
            Assert.That(TemporalSurfaceValidityCodec.WordsPerPixel, Is.EqualTo(4));
            Assert.That(TemporalSurfaceValidityCodec.UnpackTapMask(packed), Is.EqualTo(0b1010u));
            Assert.That(Vector3.Dot(normal, decoded), Is.GreaterThan(0.9999f));
        });
    }

    [TestCase(4.75f, 8.75f, 0b0001u)]
    [TestCase(4.25f, 8.75f, 0b0010u)]
    [TestCase(4.75f, 8.25f, 0b0100u)]
    [TestCase(4.25f, 8.25f, 0b1000u)]
    public void NearestTap_UsesDocumentedBilinearOrder(
        float x,
        float y,
        uint expected)
    {
        Assert.That(
            TemporalSurfaceValidityCodec.ResolveNearestTapBit(new Vector2(x, y)),
            Is.EqualTo(expected));
    }

    [Test]
    public void DormantSharedProducerHasNoGraphTraffic()
    {
        Assert.That(SurfaceInputPolicy.SharedValidityEnabled, Is.False);
        Assert.That(ProductionRenderPipelineDeclaration.Instance.CreatePassResourceDeclarations()
            .SelectMany(pass => pass.Usages)
            .Any(usage => usage.Resource == RenderGraphResourceId.TemporalSurfaceValidityHistory), Is.False);
    }

    [Test]
    public void DirectionalConsumer_UsesSharedTapAsPrefilterAndRetainsLocalChecks()
    {
        string shader = ReadRepoText(
            "Njulf.Shaders",
            "directional_shadow_temporal.comp");
        int sharedPrefilter = shader.IndexOf(
            "TemporalSurfaceNearestTapBit(",
            System.StringComparison.Ordinal);
        int localHistoryLoad = shader.IndexOf(
            "previousMoments = unpackHalf2x16",
            System.StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(
                Marshal.SizeOf<GPUDirectionalShadowTemporalPushConstants>(),
                Is.EqualTo(128));
            Assert.That(shader,
                Does.Contain("(pc.TemporalFlags & 4u) != 0u"));
            Assert.That(sharedPrefilter, Is.GreaterThanOrEqualTo(0));
            Assert.That(localHistoryLoad, Is.GreaterThan(sharedPrefilter));
            Assert.That(shader,
                Does.Contain("previousSignature != (currentSignature & 0xffu)"));
            Assert.That(shader,
                Does.Contain("abs(previousDistance.y - currentViewDistance)"));
            Assert.That(shader,
                Does.Contain("dot(previousNormal, currentNormal) < pc.NormalThreshold"));
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
