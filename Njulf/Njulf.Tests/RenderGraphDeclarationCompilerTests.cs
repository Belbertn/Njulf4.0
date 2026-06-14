using System;
using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphDeclarationCompilerTests
    {
        [Test]
        public void Compile_TopologicallySortsDeclaredDependencies()
        {
            var registry = new RenderGraphResourceRegistry();
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.After("Geometry"));
            registry.AddPass(new RenderGraphPassDesc("Geometry", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            });

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.That(plan.Diagnostics.CompiledPassOrder, Is.EqualTo(new[] { "Geometry", "Composite" }));
        }

        [Test]
        public void Compile_ReportsDependencyCyclesWithPassNames()
        {
            var registry = new RenderGraphResourceRegistry();
            registry.AddPass(new RenderGraphPassDesc("A", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.After("B"));
            registry.AddPass(new RenderGraphPassDesc("B", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.After("A"));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("dependency cycle"));
            Assert.That(ex.Message, Does.Contain("A"));
            Assert.That(ex.Message, Does.Contain("B"));
        }

        [Test]
        public void Compile_CullsDisabledAndUnusedOutputPasses()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle disabledImage = registry.GetOrCreateImage(SceneColor("Disabled Output"));
            RenderGraphResourceHandle unusedImage = registry.GetOrCreateImage(SceneColor("Unused Output"));
            registry.AddPass(new RenderGraphPassDesc("DisabledPass", RenderGraphQueueClass.Graphics)
            {
                IsEnabled = false
            }.Write(disabledImage, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("UnusedProducer", RenderGraphQueueClass.Graphics)
                .Write(unusedImage, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));

            RenderGraphDeclarationPlan plan = registry.Compile();

            Assert.Multiple(() =>
            {
                Assert.That(plan.Diagnostics.CulledPasses, Does.Contain("DisabledPass"));
                Assert.That(plan.Diagnostics.CulledPasses, Does.Contain("UnusedProducer"));
                Assert.That(plan.Diagnostics.CompiledPassOrder, Is.Empty);
            });
        }

        [Test]
        public void Compile_ComputesResourceLifetimesAndBarrierDiagnostics()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(SceneColor());
            registry.AddPass(new RenderGraphPassDesc("GBuffer", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDeclarationPlan plan = registry.Compile();
            RenderGraphResourceLifetime lifetime = plan.Diagnostics.ResourceLifetimes.Single(resource => resource.Name == "Scene Color");

            Assert.Multiple(() =>
            {
                Assert.That(plan.Diagnostics.CompiledPassOrder, Is.EqualTo(new[] { "GBuffer", "Composite" }));
                Assert.That(lifetime.FirstUsePassIndex, Is.EqualTo(0));
                Assert.That(lifetime.LastUsePassIndex, Is.EqualTo(1));
                Assert.That(plan.Diagnostics.EstimatedResourceBytes, Is.EqualTo(1280UL * 720UL * 8UL));
                Assert.That(plan.Diagnostics.BarrierCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void Compile_RejectsAsyncPassWithUnsupportedPreferredQueue()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle buffer = registry.GetOrCreateBuffer(new RenderGraphBufferDesc(
                "Visibility",
                RenderGraphResourcePersistence.External)
            {
                ByteSize = 1024,
                Usage = BufferUsageFlags.StorageBufferBit
            });

            registry.AddPass(new RenderGraphPassDesc("Culling", RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Transfer,
                HasExternalSideEffect = true
            }.Write(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));

            var ex = Assert.Throws<InvalidOperationException>(() => registry.Compile());

            Assert.That(ex!.Message, Does.Contain("prefers unsupported queue"));
        }

        [Test]
        public void GetOrCreateImage_RejectsIncompatibleDuplicateDescriptors()
        {
            var registry = new RenderGraphResourceRegistry();
            registry.GetOrCreateImage(SceneColor());

            var ex = Assert.Throws<InvalidOperationException>(() => registry.GetOrCreateImage(new RenderGraphImageDesc(
                "Scene Color",
                Format.R8Unorm,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                Width = 1280,
                Height = 720
            }));

            Assert.That(ex!.Message, Does.Contain("incompatible descriptors"));
        }

        private static RenderGraphImageDesc SceneColor()
        {
            return SceneColor("Scene Color");
        }

        private static RenderGraphImageDesc SceneColor(string name)
        {
            return new RenderGraphImageDesc(
                name,
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                Width = 1280,
                Height = 720
            };
        }
    }
}
