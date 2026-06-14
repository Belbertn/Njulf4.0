using System;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphResourceRegistryTests
    {
        [Test]
        public void Compile_RejectsUndeclaredResourceHandle()
        {
            var registry = new RenderGraphResourceRegistry();
            var pass = new RenderGraphPassDesc("BrokenPass", RenderGraphQueueClass.Graphics)
                .Read(
                    new RenderGraphResourceHandle(RenderGraphResourceKind.Image, 99, 1),
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.FragmentShaderBit);
            registry.AddPass(pass);

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("undeclared image handle"));
        }

        [Test]
        public void Compile_RejectsTransientReadBeforeProducer()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient));
            registry.AddPass(new RenderGraphPassDesc("ReadPass", RenderGraphQueueClass.Graphics)
                .Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("reads transient resource"));
            Assert.That(ex.Message, Does.Contain("before any producer"));
        }

        [Test]
        public void Compile_AllowsFirstUseReadWriteWhenAccessIsWriteOnly()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient));
            registry.AddPass(new RenderGraphPassDesc("BuildPass", RenderGraphQueueClass.Compute)
                .ReadWrite(image, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.That(plan.Passes, Has.Count.EqualTo(1));
            Assert.That(plan.Usage.ImageUsages[image], Is.EqualTo(ImageUsageFlags.StorageBit));
        }

        [Test]
        public void Compile_RejectsWriteAfterWriteWithoutDependency()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient));
            registry.AddPass(new RenderGraphPassDesc("FirstWrite", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("SecondWrite", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("without an explicit dependency edge"));
        }

        [Test]
        public void Compile_AllowsWriteAfterWriteWithDependency()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient));
            registry.AddPass(new RenderGraphPassDesc("FirstWrite", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("SecondWrite", RenderGraphQueueClass.Graphics)
                .After("FirstWrite")
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.That(plan.Passes, Has.Count.EqualTo(2));
        }

        [Test]
        public void Compile_AllowsVisibilityCompactionToRewriteDrawBuffersWithDirectProducerDependency()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle opaqueDraws = registry.GetOrCreateBuffer(new RenderGraphBufferDesc(
                ProductionRenderGraphResources.OpaqueMeshletDrawBufferName,
                RenderGraphResourcePersistence.External)
            {
                ByteSize = 1024,
                Usage = BufferUsageFlags.StorageBufferBit
            });

            registry.AddPass(new RenderGraphPassDesc("GpuVisibilityPass", RenderGraphQueueClass.Compute)
                .Write(opaqueDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("DepthPrePass", RenderGraphQueueClass.Graphics)
                .Read(opaqueDraws, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.TaskShaderBitExt)
                .After("GpuVisibilityPass"));
            registry.AddPass(new RenderGraphPassDesc("HiZBuildPass", RenderGraphQueueClass.Compute)
                .After("DepthPrePass"));
            registry.AddPass(new RenderGraphPassDesc("GpuOcclusionCompactionPass", RenderGraphQueueClass.Compute)
                .After("GpuVisibilityPass")
                .After("HiZBuildPass")
                .Write(opaqueDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.That(
                plan.Passes.Single(pass => pass.Name == "GpuOcclusionCompactionPass").DependsOn,
                Does.Contain("GpuVisibilityPass"));
        }

        [Test]
        public void Compile_RejectsTransientUseAcrossFrames()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient));
            registry.AddPass(new RenderGraphPassDesc("HistoryLikePass", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit, usesAcrossFrames: true));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("uses transient resource"));
            Assert.That(ex.Message, Does.Contain("across frames"));
        }

        [Test]
        public void Compile_RejectsHistoryImageWithoutInvalidationRule()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle history = registry.GetOrCreateImage(new RenderGraphImageDesc(
                "TAA History",
                RenderTargetManager.LdrSceneColorFormat,
                RenderGraphResolutionClass.HistoryMatchedScene,
                RenderGraphResourcePersistence.History));
            registry.AddPass(new RenderGraphPassDesc("HistoryPass", RenderGraphQueueClass.Graphics)
                .Write(history, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("missing a history invalidation rule"));
        }

        [Test]
        public void Compile_DerivesImageAndBufferUsageFlags()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Imported));
            RenderGraphResourceHandle buffer = registry.GetOrCreateBuffer(new RenderGraphBufferDesc(
                "Debug Vertices",
                RenderGraphResourcePersistence.External)
            {
                Stride = 32,
                Count = 4
            });
            registry.AddPass(new RenderGraphPassDesc("DebugDrawPass", RenderGraphQueueClass.Graphics)
                .ReadWrite(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit)
                .Read(buffer, RenderGraphResourceAccess.VertexBufferRead, PipelineStageFlags2.VertexAttributeInputBit));

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.Multiple(() =>
            {
                Assert.That(plan.Usage.ImageUsages[image], Is.EqualTo(ImageUsageFlags.ColorAttachmentBit));
                Assert.That(plan.Usage.BufferUsages[buffer], Is.EqualTo(BufferUsageFlags.VertexBufferBit));
            });
        }

        [Test]
        public void GetOrCreateImage_MergesUsageHintsAcrossCompatibleDeclarations()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle first = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient) with
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
            RenderGraphResourceHandle second = registry.GetOrCreateImage(SceneColor(RenderGraphResourcePersistence.Transient) with
            {
                UsageHint = ImageUsageFlags.StorageBit
            });

            Assert.Multiple(() =>
            {
                Assert.That(second, Is.EqualTo(first));
                Assert.That(registry.Images.Single().UsageHint, Is.EqualTo(ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit));
            });
        }

        private static RenderGraphImageDesc SceneColor(RenderGraphResourcePersistence persistence)
        {
            return new RenderGraphImageDesc(
                "Scene Color",
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.Scene,
                persistence)
            {
                HistoryInvalidationRule = persistence == RenderGraphResourcePersistence.History
                    ? "invalidate-on-resolution-change"
                    : string.Empty
            };
        }
    }
}
