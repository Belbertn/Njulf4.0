using System.Linq;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public class RenderGraphDiagnosticExporterTests
{
    [Test]
    public void Export_IncludesCompiledPassesCulledPassesLifetimesBarriersAndDot()
    {
        var registry = new RenderGraphResourceRegistry();
        RenderGraphResourceHandle color = registry.GetOrCreateImage(new RenderGraphImageDesc(
            "Scene Color",
            Format.R16G16B16A16Sfloat,
            RenderGraphResolutionClass.Scene,
            RenderGraphResourcePersistence.Transient)
        {
            Width = 1280,
            Height = 720
        });

        registry.AddPass(new RenderGraphPassDesc("Clear", RenderGraphQueueClass.Graphics) { NeverCull = true }
            .Write(color, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
        registry.AddPass(new RenderGraphPassDesc("Read", RenderGraphQueueClass.Graphics) { NeverCull = true }
            .Read(color, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
            .After("Clear"));
        registry.AddPass(new RenderGraphPassDesc("DeadPost", RenderGraphQueueClass.Compute)
            .Write(color, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .After("Clear")
            .After("Read"));

        RenderGraphDeclarationPlan declarationPlan = registry.Compile();
        RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(declarationPlan);
        RenderGraphBarrierPlan barrierPlan = RenderGraphBarrierPlanner.Build(declarationPlan);
        RenderGraphAliasPlan aliasPlan = RenderGraphAliasPlanner.Build(declarationPlan, allocationPlan);

        RenderGraphDiagnosticSnapshot snapshot = RenderGraphDiagnosticExporter.Export(declarationPlan, barrierPlan, aliasPlan);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.CompiledPassOrder, Is.EqualTo(new[] { "Clear", "Read" }));
            Assert.That(snapshot.CulledPasses, Does.Contain("DeadPost"));
            Assert.That(snapshot.ResourceLifetimes.Single().Name, Is.EqualTo("Scene Color"));
            Assert.That(snapshot.BarrierCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(snapshot.DotGraph, Does.Contain("digraph RenderGraph"));
            Assert.That(snapshot.DotGraph, Does.Contain("DeadPost"));
            Assert.That(snapshot.DotGraph, Does.Contain("Scene Color"));
        });
    }
}
