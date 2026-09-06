using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SurfaceInputReuseTests
{
    [TestCase("eligible", true)]
    [TestCase("disabled", false)]
    [TestCase("no-depth", false)]
    [TestCase("no-compaction", false)]
    [TestCase("missing-target", false)]
    [TestCase("no-motion-consumer", false)]
    [TestCase("camera-only", false)]
    [TestCase("visibility", false)]
    [TestCase("foliage", false)]
    [TestCase("skinning", false)]
    public void FusionRequiresTheCompleteRasterContract(string condition, bool expected)
    {
        var scene = new SceneRenderingData
        {
            DepthPrePassEnabled = condition != "no-depth",
            FoliageClusterCount = condition == "foliage" ? 1 : 0,
            SkinnedObjectCount = condition == "skinning" ? 1 : 0
        };
        Assert.That(SurfaceInputPolicy.CanFuse(scene,
            condition != "disabled", condition != "no-compaction",
            condition != "missing-target", condition != "no-motion-consumer",
            condition == "camera-only", condition == "visibility"), Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GraphTracksTheActualProducerAndDistinctHistoryBanks(bool fusion)
    {
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations(fusion)
            .ToDictionary(pass => pass.PassName);
        var depth = declarations["DepthPrePass"].Usages;
        var temporal = declarations["DirectionalShadowTemporalPass"].Usages;
        var current = temporal.Single(usage =>
            usage.Resource == RenderGraphResourceId.DirectionalShadowHistory &&
            usage.HistoryBinding == RenderGraphHistoryBindingSelection.Current);
        var previous = temporal.Single(usage =>
            usage.Resource == RenderGraphResourceId.DirectionalShadowHistory &&
            usage.HistoryBinding == RenderGraphHistoryBindingSelection.Previous);

        Assert.Multiple(() =>
        {
            Assert.That(depth.Any(usage => usage.Resource == RenderGraphResourceId.MotionVectors), Is.EqualTo(fusion));
            Assert.That(depth.Any(usage => usage.Resource == RenderGraphResourceId.SurfaceReceiverIdentity), Is.EqualTo(fusion));
            Assert.That(current.Access, Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(previous.Access, Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(declarations.Values.SelectMany(pass => pass.Usages)
                .Any(usage => usage.Resource == RenderGraphResourceId.TemporalSurfaceValidityHistory), Is.False);
        });
        if (fusion)
        {
            var identity = depth.Single(usage => usage.Resource == RenderGraphResourceId.SurfaceReceiverIdentity);
            var scratch = depth.Single(usage => usage.Resource == RenderGraphResourceId.DirectionalShadowScratch);
            Assert.Multiple(() =>
            {
                Assert.That(identity.FinalImageLayout, Is.EqualTo(ImageLayout.TransferSrcOptimal));
                Assert.That(scratch.AccessMask.HasFlag(AccessFlags2.TransferWriteBit), Is.True);
                Assert.That(scratch.HistoryBinding, Is.EqualTo(RenderGraphHistoryBindingSelection.Current));
            });
        }
    }

    [TestCase(false, false, 10ul, false)]
    [TestCase(true, false, 10ul, false)]
    [TestCase(true, true, 9ul, false)]
    [TestCase(true, true, 10ul, true)]
    public void OnlyCompletedCurrentDepthCanSuppressTheSeparateMotionDraw(
        bool fused, bool depthCompleted, ulong depthFrameSerial, bool expected)
    {
        var scene = new SceneRenderingData
        {
            DdgiFrameSerial = 10,
            DepthMotionFusionCompleted = fused,
            DepthPrePassCompleted = depthCompleted,
            DepthPrePassFrameSerial = depthFrameSerial
        };
        Assert.That(scene.HasCurrentDepthMotion, Is.EqualTo(expected));
    }
}
