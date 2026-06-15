using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class VisibilityFirstFramePlannerTests
{
    [Test]
    public void ProductionPassOrder_SatisfiesVisibilityFirstAudit()
    {
        FrameOrderAudit audit = VisibilityFirstFramePlanner.Audit(ProductionRenderPipeline.PassOrder);

        Assert.That(audit.IsValid, Is.True, audit.Dump());
        Assert.That(audit.Entries.Select(entry => entry.PassName), Is.SupersetOf(ProductionRenderPipeline.PassOrder));
        Assert.That((audit.Entries.Single(entry => entry.PassName == "GpuVisibilityPass").Roles & FramePassRole.VisibilityProducer) != 0, Is.True);
        Assert.That((audit.Entries.Single(entry => entry.PassName == "HiZBuildPass").Roles & FramePassRole.VisibilityProducer) != 0, Is.True);
        Assert.That((audit.Entries.Single(entry => entry.PassName == "TiledLightCullingPass").Roles & FramePassRole.LightCulling) != 0, Is.True);
        Assert.That((audit.Entries.Single(entry => entry.PassName == "ForwardPlusPass").Roles & FramePassRole.VisibilityConsumer) != 0, Is.True);
    }

    [Test]
    public void Audit_FailsWhenForwardRunsBeforeLightCulling()
    {
        string[] order = ProductionRenderPipeline.PassOrder.ToArray();
        int forward = System.Array.IndexOf(order, "ForwardPlusPass");
        int light = System.Array.IndexOf(order, "TiledLightCullingPass");
        (order[forward], order[light]) = (order[light], order[forward]);

        FrameOrderAudit audit = VisibilityFirstFramePlanner.Audit(order);

        Assert.That(audit.IsValid, Is.False);
        Assert.That(audit.Errors, Has.Some.Contains("TiledLightCullingPass"));
    }

    [Test]
    public void Audit_FailsWhenDepthRunsBeforeGpuVisibility()
    {
        string[] order = ProductionRenderPipeline.PassOrder.ToArray();
        int visibility = System.Array.IndexOf(order, "GpuVisibilityPass");
        int depth = System.Array.IndexOf(order, "DepthPrePass");
        (order[visibility], order[depth]) = (order[depth], order[visibility]);

        FrameOrderAudit audit = VisibilityFirstFramePlanner.Audit(order);

        Assert.That(audit.IsValid, Is.False);
        Assert.That(audit.Errors, Has.Some.Contains("GpuVisibilityPass"));
    }

    [Test]
    public void ZeroWorkPasses_AreMarkedSkippableExceptRequiredCompositePath()
    {
        FrameOrderAudit audit = VisibilityFirstFramePlanner.Audit(ProductionRenderPipeline.PassOrder);

        Assert.That(audit.Entries.Single(entry => entry.PassName == "DepthPrePass").CanSkipWhenZeroWork, Is.True);
        Assert.That(audit.Entries.Single(entry => entry.PassName == "TransparentForwardPass").CanSkipWhenZeroWork, Is.True);
        Assert.That(audit.Entries.Single(entry => entry.PassName == "ToneMapCompositePass").CanSkipWhenZeroWork, Is.False);
        Assert.That(audit.Entries.Single(entry => entry.PassName == "AntiAliasingPass").CanSkipWhenZeroWork, Is.False);
    }

    [Test]
    public void RuntimePolicy_SkipsDepthConsumersWhenInputsHaveNoWork()
    {
        var sceneData = new SceneRenderingData
        {
            DepthPrePassEnabled = false,
            HiZBuildEnabled = true,
            HiZMipCount = 8,
            LocalLightCount = 4,
            TransparentPassEnabled = true,
            ParticlesEnabled = true,
            RenderedParticleCount = 10
        };

        Assert.Multiple(() =>
        {
            Assert.That(FramePassRuntimePolicy.ShouldExecute("DepthPrePass", sceneData), Is.False);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("HiZBuildPass", sceneData), Is.False);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("TiledLightCullingPass", sceneData), Is.False);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("TransparentForwardPass", sceneData), Is.False);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("ParticlePass", sceneData), Is.False);
        });
    }

    [Test]
    public void RuntimePolicy_EnablesCompactedVisibleWorkConsumers()
    {
        var sceneData = new SceneRenderingData
        {
            DepthPrePassEnabled = true,
            HiZBuildEnabled = true,
            HiZMipCount = 8,
            LocalLightCount = 4,
            TransparentPassEnabled = true,
            GpuVisibilityDrawCapacity = 3,
            TransparencyMode = TransparencyMode.WeightedBlendedOit,
            ParticlesEnabled = true,
            RenderedParticleCount = 6
        };
        sceneData.ParticleBatches.Add(new GPUParticleBatch { Count = 6 });

        Assert.Multiple(() =>
        {
            Assert.That(FramePassRuntimePolicy.ShouldExecute("DepthPrePass", sceneData), Is.True);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("HiZBuildPass", sceneData), Is.True);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("TiledLightCullingPass", sceneData), Is.True);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("TransparentForwardPass", sceneData), Is.True);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("WeightedOitCompositePass", sceneData), Is.True);
            Assert.That(FramePassRuntimePolicy.ShouldExecute("ParticlePass", sceneData), Is.True);
        });
    }
}
