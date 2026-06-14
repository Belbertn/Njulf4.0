using System;
using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphImageAllocationPlannerTests
    {
        [Test]
        public void Build_ClassifiesGraphOwnedImportedAndExternalImages()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle transient = registry.GetOrCreateImage(Image(
                "Scene Color",
                RenderGraphResourcePersistence.Transient,
                width: 1280,
                height: 720));
            RenderGraphResourceHandle history = registry.GetOrCreateImage(Image(
                "TAA History",
                RenderGraphResourcePersistence.History,
                width: 1280,
                height: 720,
                historyRule: "invalidate-on-resolution-change"));
            RenderGraphResourceHandle swapchain = registry.GetOrCreateImage(Image(
                "Swapchain Color",
                RenderGraphResourcePersistence.Imported,
                width: 1280,
                height: 720));
            RenderGraphResourceHandle shadow = registry.GetOrCreateImage(Image(
                "Directional Shadow Map",
                RenderGraphResourcePersistence.External,
                width: 2048,
                height: 2048,
                format: Format.D32Sfloat));

            registry.AddPass(new RenderGraphPassDesc("ProduceSceneColor", RenderGraphQueueClass.Graphics) { NeverCull = true }
                .Write(transient, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("ProduceHistory", RenderGraphQueueClass.Graphics) { NeverCull = true }
                .Write(history, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("ReadImportedAndExternal", RenderGraphQueueClass.Graphics) { NeverCull = true }
                .Read(swapchain, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .Read(shadow, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(registry.Compile());

            Assert.Multiple(() =>
            {
                Assert.That(allocationPlan.GraphOwnedImageCount, Is.EqualTo(2));
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "Scene Color").Category, Is.EqualTo(RenderGraphImageAllocationCategory.TransientRenderTarget));
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "TAA History").Category, Is.EqualTo(RenderGraphImageAllocationCategory.PersistentHistory));
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "Swapchain Color").ShouldAllocate, Is.False);
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "Directional Shadow Map").Category, Is.EqualTo(RenderGraphImageAllocationCategory.ExternalResource));
                Assert.That(allocationPlan.GraphOwnedEstimatedBytes, Is.EqualTo(1280UL * 720UL * 8UL * 2UL));
                Assert.That(allocationPlan.ImportedEstimatedBytes, Is.EqualTo(1280UL * 720UL * 8UL));
                Assert.That(allocationPlan.ExternalEstimatedBytes, Is.EqualTo(2048UL * 2048UL * 4UL));
            });
        }

        [Test]
        public void Build_DerivesMinimalUsageFlagsFromDeclarations()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle color = registry.GetOrCreateImage(Image(
                "Scene Color",
                RenderGraphResourcePersistence.Transient,
                width: 640,
                height: 360));
            RenderGraphResourceHandle storage = registry.GetOrCreateImage(Image(
                "AO Raw",
                RenderGraphResourcePersistence.Transient,
                width: 320,
                height: 180,
                format: RenderTargetManager.AmbientOcclusionFormat));
            registry.AddPass(new RenderGraphPassDesc("ColorPass", RenderGraphQueueClass.Graphics) { NeverCull = true }
                .Write(color, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("AoPass", RenderGraphQueueClass.Compute) { NeverCull = true }
                .Write(storage, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));

            RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(registry.Compile());

            Assert.Multiple(() =>
            {
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "Scene Color").Usage, Is.EqualTo(ImageUsageFlags.ColorAttachmentBit));
                Assert.That(allocationPlan.Images.Single(image => image.Descriptor.Name == "AO Raw").Usage, Is.EqualTo(ImageUsageFlags.StorageBit));
            });
        }

        [Test]
        public void Build_CombinesImageUsageHintWithDeclaredUsage()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle scratch = registry.GetOrCreateImage(Image(
                "AO Scratch",
                RenderGraphResourcePersistence.Transient,
                width: 320,
                height: 180,
                format: RenderTargetManager.AmbientOcclusionFormat) with
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
            registry.AddPass(new RenderGraphPassDesc("AoBlurPass", RenderGraphQueueClass.Compute) { NeverCull = true }
                .ReadWrite(scratch, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));

            RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(registry.Compile());

            Assert.That(
                allocationPlan.Images.Single(image => image.Descriptor.Name == "AO Scratch").Usage,
                Is.EqualTo(ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit));
        }

        [Test]
        public void Build_RejectsGraphOwnedImageWithoutConcreteExtent()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(Image(
                "Scene Color",
                RenderGraphResourcePersistence.Transient,
                width: 0,
                height: 0));
            registry.AddPass(new RenderGraphPassDesc("ColorPass", RenderGraphQueueClass.Graphics) { NeverCull = true }
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            var ex = Assert.Throws<InvalidOperationException>(() => RenderGraphImageAllocationPlanner.Build(registry.Compile()));

            Assert.That(ex!.Message, Does.Contain("requires a concrete non-zero extent"));
        }

        private static RenderGraphImageDesc Image(
            string name,
            RenderGraphResourcePersistence persistence,
            uint width,
            uint height,
            Format format = Format.R16G16B16A16Sfloat,
            string historyRule = "")
        {
            return new RenderGraphImageDesc(
                name,
                format,
                RenderGraphResolutionClass.Scene,
                persistence)
            {
                Width = width,
                Height = height,
                HistoryInvalidationRule = historyRule
            };
        }
    }
}
