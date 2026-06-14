using System;
using System.Linq;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphDescriptorPlannerTests
    {
        [Test]
        public void Build_MapsKnownGraphTargetsToStaticBindlessIndices()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle hdr = registry.GetOrCreateImage(Image("HDR Scene Color"));
            RenderGraphResourceHandle depth = registry.GetOrCreateImage(Image("Scene Depth", Format.D32Sfloat));
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Read(hdr, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .Read(depth, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDescriptorPlan plan = RenderGraphDescriptorPlanner.Build(registry.Compile());

            Assert.Multiple(() =>
            {
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "HDR Scene Color").BindlessIndex, Is.EqualTo(BindlessIndex.HdrSceneColorTexture));
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "Scene Depth").BindlessIndex, Is.EqualTo(BindlessIndex.DepthTexture));
                Assert.That(plan.ImageBindings.All(binding => binding.ExpectedLayout == ImageLayout.ShaderReadOnlyOptimal), Is.True);
            });
        }

        [Test]
        public void Build_MapsBloomMipNamesToContiguousStaticIndices()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle extract = registry.GetOrCreateImage(Image("Bloom Extract"));
            RenderGraphResourceHandle mip1 = registry.GetOrCreateImage(Image("Bloom Mip 1"));
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Read(extract, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .Read(mip1, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDescriptorPlan plan = RenderGraphDescriptorPlanner.Build(registry.Compile());

            Assert.Multiple(() =>
            {
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "Bloom Extract").BindlessIndex, Is.EqualTo(BindlessIndex.BloomMipTextureBase));
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "Bloom Mip 1").BindlessIndex, Is.EqualTo(BindlessIndex.BloomMipTextureBase + 1));
            });
        }

        [Test]
        public void Build_AssignsUnknownSampledImagesToDynamicRange()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle custom = registry.GetOrCreateImage(Image("Custom Debug Texture"));
            registry.AddPass(new RenderGraphPassDesc("Debug", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(custom, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDescriptorPlan plan = RenderGraphDescriptorPlanner.Build(registry.Compile());

            RenderGraphImageDescriptorBinding binding = plan.ImageBindings.Single();
            Assert.Multiple(() =>
            {
                Assert.That(binding.BindlessIndex, Is.EqualTo(BindlessIndex.FirstDynamicTextureIndex));
                Assert.That(binding.IsStaticIndex, Is.False);
            });
        }

        [Test]
        public void Build_MapsSmaaLookupTexturesToStaticIndices()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle area = registry.GetOrCreateImage(Image("SMAA Area Texture", Format.R8G8B8A8Unorm));
            RenderGraphResourceHandle search = registry.GetOrCreateImage(Image("SMAA Search Texture", Format.R8G8B8A8Unorm));
            registry.AddPass(new RenderGraphPassDesc("AntiAliasingPass", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Read(area, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .Read(search, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphDescriptorPlan plan = RenderGraphDescriptorPlanner.Build(registry.Compile());

            Assert.Multiple(() =>
            {
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "SMAA Area Texture").BindlessIndex, Is.EqualTo(BindlessIndex.SmaaAreaTexture));
                Assert.That(plan.ImageBindings.Single(binding => binding.ResourceName == "SMAA Search Texture").BindlessIndex, Is.EqualTo(BindlessIndex.SmaaSearchTexture));
            });
        }

        [Test]
        public void Build_RejectsDuplicateStaticIndexExceptExplicitHistoryPingPong()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle extract = registry.GetOrCreateImage(Image("Bloom Extract"));
            RenderGraphResourceHandle mip0 = registry.GetOrCreateImage(Image("Bloom Mip 0"));
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }
                .Read(extract, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .Read(mip0, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            var ex = Assert.Throws<InvalidOperationException>(() => RenderGraphDescriptorPlanner.Build(registry.Compile()));

            Assert.That(ex!.Message, Does.Contain("descriptor index"));
        }

        private static RenderGraphImageDesc Image(string name, Format format = Format.R16G16B16A16Sfloat)
        {
            return new RenderGraphImageDesc(
                name,
                format,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Imported)
            {
                Width = 256,
                Height = 256
            };
        }
    }
}
