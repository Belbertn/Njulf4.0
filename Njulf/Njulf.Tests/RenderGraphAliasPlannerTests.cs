using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphAliasPlannerTests
    {
        [Test]
        public void Build_AliasesCompatibleNonOverlappingTransientImages()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle a = registry.GetOrCreateImage(Image("AO Scratch"));
            RenderGraphResourceHandle b = registry.GetOrCreateImage(Image("SMAA Edges"));
            registry.AddPass(new RenderGraphPassDesc("WriteA", RenderGraphQueueClass.Compute)
                .Write(a, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("ReadA", RenderGraphQueueClass.Compute)
                .Read(a, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("WriteB", RenderGraphQueueClass.Graphics)
                .Write(b, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("ReadB", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(b, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDeclarationPlan declarationPlan = registry.Compile();
            RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(declarationPlan);
            RenderGraphAliasPlan aliasPlan = RenderGraphAliasPlanner.Build(declarationPlan, allocationPlan);

            Assert.Multiple(() =>
            {
                Assert.That(aliasPlan.Groups, Has.Count.EqualTo(1));
                Assert.That(aliasPlan.Groups[0].Images.Select(image => image.Descriptor.Name), Is.EqualTo(new[] { "AO Scratch", "SMAA Edges" }));
                Assert.That(aliasPlan.UnaliasedTransientBytes, Is.EqualTo(1024UL * 1024UL * 8UL * 2UL));
                Assert.That(aliasPlan.PeakTransientBytes, Is.EqualTo(1024UL * 1024UL * 8UL));
                Assert.That(aliasPlan.AliasedBytesSaved, Is.EqualTo(1024UL * 1024UL * 8UL));
            });
        }

        [Test]
        public void Build_DoesNotAliasOverlappingLifetimes()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle a = registry.GetOrCreateImage(Image("A"));
            RenderGraphResourceHandle b = registry.GetOrCreateImage(Image("B"));
            registry.AddPass(new RenderGraphPassDesc("WriteA", RenderGraphQueueClass.Graphics)
                .Write(a, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("WriteB", RenderGraphQueueClass.Graphics)
                .Write(b, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("ReadA", RenderGraphQueueClass.Graphics)
                .Read(a, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));
            registry.AddPass(new RenderGraphPassDesc("ReadB", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(b, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDeclarationPlan declarationPlan = registry.Compile();
            RenderGraphAliasPlan aliasPlan = RenderGraphAliasPlanner.Build(
                declarationPlan,
                RenderGraphImageAllocationPlanner.Build(declarationPlan));

            Assert.Multiple(() =>
            {
                Assert.That(aliasPlan.Groups, Has.Count.EqualTo(2));
                Assert.That(aliasPlan.PeakTransientBytes, Is.EqualTo(aliasPlan.UnaliasedTransientBytes));
                Assert.That(aliasPlan.AliasedBytesSaved, Is.EqualTo(0));
            });
        }

        [Test]
        public void Build_ExcludesHistoryImportedAndExternalImages()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle transient = registry.GetOrCreateImage(Image("Transient"));
            RenderGraphResourceHandle history = registry.GetOrCreateImage(Image("History", RenderGraphResourcePersistence.History, "invalidate-on-resolution-change"));
            registry.GetOrCreateImage(Image("Imported", RenderGraphResourcePersistence.Imported));
            registry.GetOrCreateImage(Image("External", RenderGraphResourcePersistence.External));
            registry.AddPass(new RenderGraphPassDesc("WriteTransient", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Write(transient, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("WriteHistory", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Write(history, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            RenderGraphDeclarationPlan declarationPlan = registry.Compile();
            RenderGraphAliasPlan aliasPlan = RenderGraphAliasPlanner.Build(
                declarationPlan,
                RenderGraphImageAllocationPlanner.Build(declarationPlan));

            Assert.That(aliasPlan.Groups.SelectMany(group => group.Images).Select(image => image.Descriptor.Name), Is.EqualTo(new[] { "Transient" }));
        }

        [Test]
        public void Build_CanDisableAliasingForDiagnostics()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle a = registry.GetOrCreateImage(Image("A"));
            registry.AddPass(new RenderGraphPassDesc("WriteA", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Write(a, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            RenderGraphDeclarationPlan declarationPlan = registry.Compile();
            RenderGraphAliasPlan aliasPlan = RenderGraphAliasPlanner.Build(
                declarationPlan,
                RenderGraphImageAllocationPlanner.Build(declarationPlan),
                RenderGraphAliasSettings.Disabled);

            Assert.Multiple(() =>
            {
                Assert.That(aliasPlan.Groups, Is.Empty);
                Assert.That(aliasPlan.PeakTransientBytes, Is.EqualTo(aliasPlan.UnaliasedTransientBytes));
            });
        }

        private static RenderGraphImageDesc Image(
            string name,
            RenderGraphResourcePersistence persistence = RenderGraphResourcePersistence.Transient,
            string historyRule = "")
        {
            return new RenderGraphImageDesc(
                name,
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.Scene,
                persistence)
            {
                Width = 1024,
                Height = 1024,
                HistoryInvalidationRule = historyRule
            };
        }
    }
}
