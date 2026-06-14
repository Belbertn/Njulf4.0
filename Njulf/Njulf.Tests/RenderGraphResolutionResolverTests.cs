using System;
using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphResolutionResolverTests
    {
        [Test]
        public void Resolve_UsesExpectedResolutionClasses()
        {
            var context = new RenderGraphResolutionContext(1920, 1080, 1536, 864);

            RenderGraphResolvedImageDesc swapchain = RenderGraphResolutionResolver.Resolve(Image("Swapchain", RenderGraphResolutionClass.Swapchain), context);
            RenderGraphResolvedImageDesc scene = RenderGraphResolutionResolver.Resolve(Image("Scene", RenderGraphResolutionClass.Scene), context);
            RenderGraphResolvedImageDesc half = RenderGraphResolutionResolver.Resolve(Image("Half", RenderGraphResolutionClass.HalfScene), context);
            RenderGraphResolvedImageDesc quarter = RenderGraphResolutionResolver.Resolve(Image("Quarter", RenderGraphResolutionClass.QuarterScene), context);

            Assert.Multiple(() =>
            {
                Assert.That((swapchain.Width, swapchain.Height), Is.EqualTo((1920u, 1080u)));
                Assert.That((scene.Width, scene.Height), Is.EqualTo((1536u, 864u)));
                Assert.That((half.Width, half.Height), Is.EqualTo((768u, 432u)));
                Assert.That((quarter.Width, quarter.Height), Is.EqualTo((384u, 216u)));
            });
        }

        [Test]
        public void Resolve_RoundsOddCustomScalesUp()
        {
            var context = new RenderGraphResolutionContext(1919, 1079, 1919, 1079);
            RenderGraphResolvedImageDesc half = RenderGraphResolutionResolver.Resolve(Image("Half", RenderGraphResolutionClass.HalfScene), context);
            RenderGraphResolvedImageDesc custom = RenderGraphResolutionResolver.Resolve(new RenderGraphImageDesc(
                "Custom",
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.CustomScale,
                RenderGraphResourcePersistence.Transient)
            {
                CustomResolutionScale = 0.333f
            }, context);

            Assert.Multiple(() =>
            {
                Assert.That((half.Width, half.Height), Is.EqualTo((960u, 540u)));
                Assert.That((custom.Width, custom.Height), Is.EqualTo(((uint)MathF.Ceiling(1919 * 0.333f), (uint)MathF.Ceiling(1079 * 0.333f))));
            });
        }

        [Test]
        public void Resolve_FixedRequiresExplicitExtent()
        {
            var context = new RenderGraphResolutionContext(1920, 1080, 1920, 1080);
            var fixedImage = Image("Fixed", RenderGraphResolutionClass.Fixed);

            var ex = Assert.Throws<InvalidOperationException>(() => RenderGraphResolutionResolver.Resolve(fixedImage, context));

            Assert.That(ex!.Message, Does.Contain("must declare Width and Height"));
        }

        [Test]
        public void Resolve_HistoryInvalidatesWhenMatchedSceneResolutionChanges()
        {
            var previous = new RenderGraphResolutionContext(1920, 1080, 1920, 1080);
            var current = new RenderGraphResolutionContext(1920, 1080, 1600, 900);
            var history = new RenderGraphImageDesc(
                "TAA History",
                RenderTargetManager.LdrSceneColorFormat,
                RenderGraphResolutionClass.HistoryMatchedScene,
                RenderGraphResourcePersistence.History)
            {
                HistoryInvalidationRule = "invalidate-on-resolution-change"
            };

            RenderGraphResolvedImageDesc resolved = RenderGraphResolutionResolver.Resolve(history, current, previous);

            Assert.Multiple(() =>
            {
                Assert.That((resolved.Width, resolved.Height), Is.EqualTo((1600u, 900u)));
                Assert.That(resolved.HistoryInvalidated, Is.True);
            });
        }

        [Test]
        public void ResolveAll_HandlesMultipleImages()
        {
            var context = RenderGraphResolutionContext.FromScale(1920, 1080, 0.75f);
            var images = new[]
            {
                Image("Scene", RenderGraphResolutionClass.Scene),
                Image("Bloom", RenderGraphResolutionClass.HalfScene)
            };

            var resolved = RenderGraphResolutionResolver.ResolveAll(images, context);

            Assert.That(resolved.Select(image => (image.Width, image.Height)), Is.EqualTo(new[] { (1440u, 810u), (720u, 405u) }));
        }

        [Test]
        public void Materialize_ProducesConcreteDeclarationPlan()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle color = registry.GetOrCreateImage(Image("Scene", RenderGraphResolutionClass.Scene));
            registry.AddPass(new RenderGraphPassDesc("WriteScene", RenderGraphQueueClass.Graphics)
            {
                NeverCull = true
            }
                .Write(
                    color,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit));
            RenderGraphDeclarationPlan declarationPlan = registry.Compile();

            RenderGraphMaterializedPlan materialized = RenderGraphResolutionMaterializer.Materialize(
                declarationPlan,
                new RenderGraphResolutionContext(1920, 1080, 1280, 720));
            RenderGraphImageAllocationPlan allocationPlan = RenderGraphImageAllocationPlanner.Build(materialized.DeclarationPlan);

            Assert.Multiple(() =>
            {
                Assert.That(materialized.DeclarationPlan.Images[0].Width, Is.EqualTo(1280u));
                Assert.That(materialized.DeclarationPlan.Images[0].Height, Is.EqualTo(720u));
                Assert.That(materialized.DeclarationPlan.Diagnostics.EstimatedResourceBytes, Is.GreaterThan(0));
                Assert.That(allocationPlan.GraphOwnedImageCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void Materialize_InvalidatesHistoryWhenResolutionChanges()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle history = registry.GetOrCreateImage(new RenderGraphImageDesc(
                "History",
                RenderTargetManager.LdrSceneColorFormat,
                RenderGraphResolutionClass.HistoryMatchedScene,
                RenderGraphResourcePersistence.History)
            {
                HistoryInvalidationRule = "invalidate-on-resolution-change"
            });
            registry.AddPass(new RenderGraphPassDesc("ReadHistory", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(
                history,
                RenderGraphResourceAccess.SampledRead,
                PipelineStageFlags2.FragmentShaderBit,
                usesAcrossFrames: true));
            RenderGraphDeclarationPlan declarationPlan = registry.Compile();

            RenderGraphMaterializedPlan materialized = RenderGraphResolutionMaterializer.Materialize(
                declarationPlan,
                new RenderGraphResolutionContext(1920, 1080, 1600, 900),
                new RenderGraphResolutionContext(1920, 1080, 1920, 1080));

            Assert.Multiple(() =>
            {
                Assert.That((materialized.DeclarationPlan.Images[0].Width, materialized.DeclarationPlan.Images[0].Height), Is.EqualTo((1600u, 900u)));
                Assert.That(materialized.ResolvedImages[0].HistoryInvalidated, Is.True);
            });
        }

        private static RenderGraphImageDesc Image(string name, RenderGraphResolutionClass resolutionClass)
        {
            return new RenderGraphImageDesc(
                name,
                RenderTargetManager.SceneColorFormat,
                resolutionClass,
                RenderGraphResourcePersistence.Transient);
        }
    }
}
