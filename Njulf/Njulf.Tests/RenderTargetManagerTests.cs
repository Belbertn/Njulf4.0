using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public unsafe class RenderTargetManagerTests
    {
        [Test]
        public void BindGraphImages_ClearsFacadeWhenOptionalGraphImageIsNoLongerLive()
        {
            var extent = new Extent2D { Width = 1600, Height = 900 };
            using var renderTargets = new RenderTargetManager(
                FakeContext(),
                extent,
                Format.D32Sfloat,
                fogEnabled: true);
            var fogHandle = new RenderGraphResourceHandle(RenderGraphResourceKind.Image, 0, 1);
            var fogImage = AllocatedImage(
                fogHandle,
                ProductionRenderGraphResources.FoggedSceneColorName,
                RenderTargetManager.FoggedSceneColorFormat,
                extent,
                imageHandle: 0x100,
                viewHandle: 0xe0);

            renderTargets.BindGraphImages(new Dictionary<RenderGraphResourceHandle, RenderGraphAllocatedImage>
            {
                [fogHandle] = fogImage
            });

            Assert.That(renderTargets.FoggedSceneColor.View.Handle, Is.EqualTo(0xe0));

            renderTargets.BindGraphImages(new Dictionary<RenderGraphResourceHandle, RenderGraphAllocatedImage>());

            Assert.Multiple(() =>
            {
                Assert.That(renderTargets.FoggedSceneColor.View.Handle, Is.Zero);
                Assert.That(renderTargets.FoggedSceneColor.Image.Handle, Is.Zero);
                Assert.That(renderTargets.FoggedSceneColor.Extent.Width, Is.EqualTo(1));
                Assert.That(renderTargets.FoggedSceneColor.Extent.Height, Is.EqualTo(1));
            });
        }

        private static VulkanContext FakeContext()
        {
            return (VulkanContext)RuntimeHelpers.GetUninitializedObject(typeof(VulkanContext));
        }

        private static RenderGraphAllocatedImage AllocatedImage(
            RenderGraphResourceHandle handle,
            string name,
            Format format,
            Extent2D extent,
            ulong imageHandle,
            ulong viewHandle)
        {
            return new RenderGraphAllocatedImage(
                handle,
                name,
                new Image(imageHandle),
                new ImageView(viewHandle),
                allocation: null,
                format,
                extent,
                mipCount: 1,
                arrayLayers: 1,
                ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit,
                RenderGraphImageAllocationCategory.TransientRenderTarget,
                estimatedBytes: RenderTarget.CalculateByteSize(extent.Width, extent.Height, format));
        }
    }
}
