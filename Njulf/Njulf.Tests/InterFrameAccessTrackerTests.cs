using Njulf.Rendering.Pipeline;
using System.Linq;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class InterFrameAccessTrackerTests
{
    private static readonly InterFrameAccessTracker.Allocation Buffer =
        new(RenderGraphConcreteResourceKind.Buffer, 1, 1);

    [TestCase(false, false, false)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public void SubmittedAccess_OrdersOnlyConflicts(bool priorWrite, bool nextWrite, bool expectedBarrier)
    {
        var tracker = new InterFrameAccessTracker();
        tracker.BeginRecording();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, priorWrite), priorWrite);
        tracker.CommitSubmission();
        MemoryBarrier2? barrier = tracker.Access(Buffer, Scope(PipelineStageFlags2.FragmentShaderBit, nextWrite), nextWrite);

        Assert.That(barrier.HasValue, Is.EqualTo(expectedBarrier));
        if (!expectedBarrier)
            return;
        Assert.Multiple(() =>
        {
            Assert.That(barrier!.Value.SrcStageMask, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
            Assert.That(barrier.Value.DstStageMask, Is.EqualTo(PipelineStageFlags2.FragmentShaderBit));
            Assert.That(barrier.Value.SrcAccessMask, Is.EqualTo(priorWrite ? AccessFlags2.ShaderStorageWriteBit : AccessFlags2.None));
            Assert.That(barrier.Value.DstAccessMask, Is.EqualTo(priorWrite ? Scope(PipelineStageFlags2.FragmentShaderBit, nextWrite).Access : AccessFlags2.None));
        });
    }

    [Test]
    public void Overwrite_WaitsForAllReadersAcrossSkippedFrames()
    {
        var tracker = new InterFrameAccessTracker();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.FragmentShaderBit, false), false);
        tracker.CommitSubmission();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, false), false);
        tracker.CommitSubmission();
        tracker.CommitSubmission(); // Resource is unused for one submitted frame.
        var barrier = tracker.Access(Buffer, Scope(PipelineStageFlags2.TransferBit, true), true);
        Assert.That(barrier!.Value.SrcStageMask, Is.EqualTo(PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit));
    }

    [Test]
    public void Read_DoesNotLoseWriterVisibilityForAnotherConsumerStage()
    {
        var tracker = new InterFrameAccessTracker();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, true), true);
        tracker.CommitSubmission();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, false), false);
        tracker.CommitSubmission();
        var barrier = tracker.Access(Buffer, Scope(PipelineStageFlags2.FragmentShaderBit, false), false);
        Assert.That(barrier!.Value.SrcAccessMask, Is.EqualTo(AccessFlags2.ShaderStorageWriteBit));
    }

    [Test]
    public void AbandonedRecordingAndNewAllocation_DoNotBecomeSubmittedHistory()
    {
        var tracker = new InterFrameAccessTracker();
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, true), true);
        tracker.BeginRecording();
        Assert.That(tracker.Access(Buffer, Scope(PipelineStageFlags2.FragmentShaderBit, false), false), Is.Null);
        tracker.Access(Buffer, Scope(PipelineStageFlags2.ComputeShaderBit, true), true);
        tracker.CommitSubmission();
        Assert.That(tracker.Access(Buffer with { Generation = 2 }, Scope(PipelineStageFlags2.FragmentShaderBit, false), false), Is.Null);
    }

    private static InterFrameAccessTracker.Scope Scope(PipelineStageFlags2 stages, bool write) =>
        new(stages, (stages & PipelineStageFlags2.TransferBit) != 0
            ? (write ? AccessFlags2.TransferWriteBit : AccessFlags2.TransferReadBit)
            : (write ? AccessFlags2.ShaderStorageWriteBit : AccessFlags2.ShaderStorageReadBit));

    [Test]
    public void ProductionDepthAndMotion_ReusePreludeCoverageWithoutFullBarrier()
    {
        using var graph = new RenderGraph();
        foreach (var pass in ProductionRenderPipelineDeclaration.Instance.CreatePassResourceDeclarations(false))
            graph.DeclarePassResources(pass.PassName, pass.Usages.ToArray());
        graph.BeginInterFrameRecording(true);
        graph.CoverInterFrameAccesses(PipelineStageFlags2.TransferBit | PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.ColorAttachmentOutputBit | PipelineStageFlags2.EarlyFragmentTestsBit |
            PipelineStageFlags2.LateFragmentTestsBit);
        var depth = graph.PlanInterFrameBarrier("DepthPrePass", 0);
        Assert.That(depth.HasValue, Is.True);
        Assert.That(depth!.Value.DstStageMask, Is.EqualTo(PipelineStageFlags2.DrawIndirectBit |
            PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.FragmentShaderBit));
        Assert.That(graph.PlanInterFrameBarrier("MotionVectorPass", 0), Is.Null);
        graph.BeginInterFrameRecording(true); // An abandoned recording must not retain coverage.
        Assert.That(graph.PlanInterFrameBarrier("DepthPrePass", 0).HasValue, Is.True);
        graph.BeginInterFrameRecording(false);
        Assert.That(graph.PlanInterFrameBarrier("DepthPrePass", 0), Is.Null);
    }

    [Test]
    public void ProductionMotionVectorImage_OrdersColorWriteBeforeComputeReadWithoutFallback()
    {
        var usages = ProductionRenderPipelineDeclaration.Instance.CreatePassResourceDeclarations(false)
            .SelectMany(pass => pass.Usages).Where(usage => usage.Resource == RenderGraphResourceId.MotionVectors).ToArray();
        using var graph = new RenderGraph();
        graph.DeclarePassResources("producer", usages.First(usage => usage.Access == RenderGraphResourceAccess.Write));
        graph.DeclarePassResources("consumer", usages.First(usage => usage.Access == RenderGraphResourceAccess.Read &&
            usage.StageMask == PipelineStageFlags2.ComputeShaderBit));
        graph.ConcreteResourceBindings.Replace([RenderGraphConcreteResourceBinding.ForImage(
            RenderGraphResourceId.MotionVectors, "motion", new Image(1),
            new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            ImageLayout.ShaderReadOnlyOptimal, [0u], 0u,
            lifetime: RenderGraphResourceLifetime.Transient)]);
        graph.BeginInterFrameRecording(true);
        graph.CoverInterFrameAccesses(PipelineStageFlags2.AllCommandsBit); // Priming still records the writer.
        Assert.That(graph.PlanInterFrameBarrier("producer", 0), Is.Null);
        graph.CommitInterFrameSubmission();
        graph.BeginInterFrameRecording(true);
        var barrier = graph.PlanInterFrameBarrier("consumer", 1);
        Assert.That(barrier.HasValue, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(barrier!.Value.SrcStageMask, Is.EqualTo(PipelineStageFlags2.ColorAttachmentOutputBit));
            Assert.That(barrier.Value.SrcAccessMask, Is.EqualTo(
                AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit));
            Assert.That(barrier.Value.DstStageMask, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
            Assert.That(barrier.Value.DstAccessMask, Is.EqualTo(AccessFlags2.ShaderSampledReadBit));
            Assert.That(graph.InterFrameConservativePassCount, Is.Zero);
        });
    }
}
