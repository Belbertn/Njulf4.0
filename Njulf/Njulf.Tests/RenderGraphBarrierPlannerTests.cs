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
        public void Build_GeneratesDepthAttachmentToSampledReadBarrier()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle depth = registry.GetOrCreateImage(Image("Scene Depth", Format.D32Sfloat));
            registry.AddPass(new RenderGraphPassDesc("DepthPrePass", RenderGraphQueueClass.Graphics)
                .Write(depth, RenderGraphResourceAccess.DepthStencilAttachmentWrite, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit));
            registry.AddPass(new RenderGraphPassDesc("HiZBuildPass", RenderGraphQueueClass.Compute)
            {
                HasExternalSideEffect = true
            }.Read(depth, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.ComputeShaderBit)
                .After("DepthPrePass"));
            registry.AddPass(new RenderGraphPassDesc("AmbientOcclusionPass", RenderGraphQueueClass.Compute)
            {
                HasExternalSideEffect = true
            }.Read(depth, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.ComputeShaderBit)
                .After("HiZBuildPass"));

            RenderGraphBarrierPlan plan = RenderGraphBarrierPlanner.Build(registry.Compile());
            RenderGraphPassBarrierBatch hizBatch = plan.Passes.Single(pass => pass.PassName == "HiZBuildPass");
            RenderGraphPassBarrierBatch aoBatch = plan.Passes.Single(pass => pass.PassName == "AmbientOcclusionPass");
            RenderGraphImageBarrierDesc hizTransition = hizBatch.ImageBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(hizTransition.OldLayout, Is.EqualTo(ImageLayout.DepthStencilAttachmentOptimal));
                Assert.That(hizTransition.NewLayout, Is.EqualTo(ImageLayout.DepthStencilReadOnlyOptimal));
                Assert.That(aoBatch.ImageBarriers, Is.Empty);
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
        public void Build_AddsQueueFamilyOwnershipTransferForAsyncProducer()
        {
            var registry = new RenderGraphResourceRegistry();
            RenderGraphResourceHandle image = registry.GetOrCreateImage(Image("AO Raw", RenderTargetManager.AmbientOcclusionFormat));
            registry.AddPass(new RenderGraphPassDesc("AoPass", RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Compute
            }.SupportsQueue(RenderGraphQueueClass.Graphics)
                .Write(image, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(image, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.FragmentShaderBit)
                .After("AoPass"));

            RenderGraphDeclarationPlan graph = registry.Compile();
            AsyncComputeDeviceProfile device = AsyncComputeDeviceProfile.FromQueueFamilies(0, 1, 0, true, true, true);
            AsyncSchedulePlan schedule = AsyncComputeScheduler.Build(
                graph,
                device,
                AsyncComputeMode.Aggressive,
                new[] { new AsyncPassSchedulingHint("AoPass", true, RenderGraphQueueClass.Compute, 1000, false, false) });

            RenderGraphImageBarrierDesc transition = RenderGraphBarrierPlanner.Build(graph, schedule, device)
                .Passes.Single(pass => pass.PassName == "Forward")
                .ImageBarriers.Single();

            Assert.Multiple(() =>
            {
                Assert.That(transition.RequiresQueueOwnershipTransfer, Is.True);
                Assert.That(transition.SrcQueueFamilyIndex, Is.EqualTo(1));
                Assert.That(transition.DstQueueFamilyIndex, Is.EqualTo(0));
            });
        }

        [Test]
        public void Build_DoesNotAddOwnershipTransferForSharedQueueFamilies()
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
                HasExternalSideEffect = true
            }.Write(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
            registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
            {
                HasExternalSideEffect = true
            }.Read(buffer, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.MeshShaderBitExt)
                .After("Culling"));

            RenderGraphDeclarationPlan graph = registry.Compile();
            AsyncComputeDeviceProfile device = AsyncComputeDeviceProfile.FromQueueFamilies(0, 0, 0, true, true, true);
            AsyncSchedulePlan schedule = AsyncComputeScheduler.Build(
                graph,
                device,
                AsyncComputeMode.Aggressive,
                Array.Empty<AsyncPassSchedulingHint>());

            RenderGraphBufferBarrierDesc transition = RenderGraphBarrierPlanner.Build(graph, schedule, device)
                .Passes.Single(pass => pass.PassName == "Forward")
                .BufferBarriers.Single();

            Assert.That(transition.RequiresQueueOwnershipTransfer, Is.False);
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

        [Test]
        public void HiZDepthPyramid_DefaultAfterExplicitDeclarationKeepsFullMipCount()
        {
            var registry = new RenderGraphResourceRegistry();

            RenderGraphResourceHandle explicitHandle = ProductionRenderGraphResources.HiZDepthPyramid(registry, 9);
            RenderGraphResourceHandle reusedHandle = ProductionRenderGraphResources.HiZDepthPyramid(registry);

            Assert.Multiple(() =>
            {
                Assert.That(reusedHandle, Is.EqualTo(explicitHandle));
                Assert.That(registry.Images[explicitHandle.Index].MipCount, Is.EqualTo(9));
            });
        }

        [Test]
        public void HiZDepthPyramid_ExplicitDeclarationAfterDefaultFailsInsteadOfKeepingOneMip()
        {
            var registry = new RenderGraphResourceRegistry();

            ProductionRenderGraphResources.HiZDepthPyramid(registry);

            InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(
                () => ProductionRenderGraphResources.HiZDepthPyramid(registry, 9));
            Assert.That(exception?.Message, Does.Contain("mips 1"));
        }

        [Test]
        public void QueueStageMask_ComputeQueueMapsShaderReadStagesToComputeShader()
        {
            PipelineStageFlags2 sanitized = RenderGraphQueueStageMask.Sanitize(
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.TaskShaderBitExt,
                AccessFlags2.ShaderSampledReadBit,
                RenderGraphQueueClass.Compute);

            Assert.That(sanitized, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
        }

        [Test]
        public void QueueStageMask_ComputeQueueKeepsTransferStages()
        {
            PipelineStageFlags2 sanitized = RenderGraphQueueStageMask.Sanitize(
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                RenderGraphQueueClass.Compute);

            Assert.That(sanitized, Is.EqualTo(PipelineStageFlags2.TransferBit));
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
