using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphBarrierPlannerTests
    {
        [Test]
        public void Build_GeneratesColorWriteToSampledReadImageBarrier()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(Image("Scene Color"));
            registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.ColorAttachmentWrite, PipelineStageFlags2.ColorAttachmentOutputBit));
            registry.AddPass(new RenderGraphPassDesc("Composite", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphBarrierPlan plan = RenderGraphBarrierPlanner.Build(registry.Compile());
            RenderGraphImageBarrierDesc transition = plan.Passes.Single(pass => pass.PassName == "Composite").ImageBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(transition.OldLayout, Is.EqualTo(ImageLayout.ColorAttachmentOptimal));
                Assert.That(transition.NewLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
                Assert.That(transition.SrcAccessMask, Is.EqualTo(AccessFlags2.ColorAttachmentWriteBit));
                Assert.That(transition.DstAccessMask, Is.EqualTo(AccessFlags2.ShaderSampledReadBit));
            });
        }

        [Test]
        public void Build_GeneratesComputeStorageToGraphicsSampledBarrier()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(Image("AO Raw", RenderTargetManager.AmbientOcclusionFormat));
            registry.AddPass(new RenderGraphPassDesc("AoPass", RenderGraphQueueClass.Compute)
                .Write(image, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit));

            RenderGraphImageBarrierDesc transition = RenderGraphBarrierPlanner.Build(registry.Compile())
                .Passes.Single(pass => pass.PassName == "Forward")
                .ImageBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(transition.OldLayout, Is.EqualTo(ImageLayout.General));
                Assert.That(transition.NewLayout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
                Assert.That(transition.SrcStageMask, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
                Assert.That(transition.DstStageMask, Is.EqualTo(PipelineStageFlags2.FragmentShaderBit));
            });
        }

        [Test]
        public void Build_GeneratesTransferToShaderBufferBarrier()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle buffer = registry.GetOrCreateBuffer(new RenderGraphBufferDesc(
                "Object Data",
                RenderGraphResourcePersistence.External)
            {
                ByteSize = 4096
            });
            registry.AddPass(new RenderGraphPassDesc("Upload", RenderGraphQueueClass.Transfer)
                .Write(buffer, RenderGraphResourceAccess.TransferWrite, PipelineStageFlags2.TransferBit));
            registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(buffer, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.MeshShaderBitExt));

            RenderGraphBufferBarrierDesc transition = RenderGraphBarrierPlanner.Build(registry.Compile())
                .Passes.Single(pass => pass.PassName == "Forward")
                .BufferBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(transition.SrcAccessMask, Is.EqualTo(AccessFlags2.TransferWriteBit));
                Assert.That(transition.DstAccessMask, Is.EqualTo(AccessFlags2.ShaderStorageReadBit));
                Assert.That(transition.Offset, Is.EqualTo(0));
                Assert.That(transition.Size, Is.EqualTo(Vk.WholeSize));
            });
        }

        [Test]
        public void Build_TracksPerMipImageRanges()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(new RenderGraphImageDesc(
                "Hi-Z",
                Format.R32Sfloat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                Width = 512,
                Height = 512,
                MipCount = 4
            });
            registry.AddPass(new RenderGraphPassDesc("WriteMip0", RenderGraphQueueClass.Compute)
                .Write(image, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit, baseMipLevel: 0, levelCount: 1));
            registry.AddPass(new RenderGraphPassDesc("ReadMip0WriteMip1", RenderGraphQueueClass.Compute)
            {
                HasExternalSideEffect = true
            }.Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.ComputeShaderBit, baseMipLevel: 0, levelCount: 1));

            RenderGraphImageBarrierDesc transition = RenderGraphBarrierPlanner.Build(registry.Compile())
                .Passes.Single(pass => pass.PassName == "ReadMip0WriteMip1")
                .ImageBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(transition.SubresourceRange.BaseMipLevel, Is.EqualTo(0));
                Assert.That(transition.SubresourceRange.LevelCount, Is.EqualTo(1));
                Assert.That(transition.SubresourceRange.AspectMask, Is.EqualTo(ImageAspectFlags.ColorBit));
            });
        }

        private static RenderGraphImageDesc Image(string name, Format format = Format.R16G16B16A16Sfloat)
        {
            return new RenderGraphImageDesc(
                name,
                format,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                Width = 1280,
                Height = 720
            };
        }
    }
}
