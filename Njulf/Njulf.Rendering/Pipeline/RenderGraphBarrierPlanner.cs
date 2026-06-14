using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed record RenderGraphBarrierPlan(IReadOnlyList<RenderGraphPassBarrierBatch> Passes)
    {
        public static RenderGraphBarrierPlan Empty { get; } = new([]);
        public int BarrierCount => Passes.Sum(pass => pass.ImageBarriers.Count + pass.BufferBarriers.Count);
    }

    public sealed record RenderGraphPassBarrierBatch(
        string PassName,
        IReadOnlyList<RenderGraphImageBarrierDesc> ImageBarriers,
        IReadOnlyList<RenderGraphBufferBarrierDesc> BufferBarriers);

    public sealed record RenderGraphImageBarrierDesc(
        RenderGraphResourceHandle Handle,
        string ResourceName,
        string ProducerPassName,
        string ConsumerPassName,
        PipelineStageFlags2 SrcStageMask,
        AccessFlags2 SrcAccessMask,
        PipelineStageFlags2 DstStageMask,
        AccessFlags2 DstAccessMask,
        ImageLayout OldLayout,
        ImageLayout NewLayout,
        ImageSubresourceRange SubresourceRange)
    {
        public uint SrcQueueFamilyIndex { get; init; } = Vk.QueueFamilyIgnored;
        public uint DstQueueFamilyIndex { get; init; } = Vk.QueueFamilyIgnored;
        public bool RequiresQueueOwnershipTransfer => SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
            DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
            SrcQueueFamilyIndex != DstQueueFamilyIndex;
    }

    public sealed record RenderGraphBufferBarrierDesc(
        RenderGraphResourceHandle Handle,
        string ResourceName,
        string ProducerPassName,
        string ConsumerPassName,
        PipelineStageFlags2 SrcStageMask,
        AccessFlags2 SrcAccessMask,
        PipelineStageFlags2 DstStageMask,
        AccessFlags2 DstAccessMask,
        ulong Offset,
        ulong Size)
    {
        public uint SrcQueueFamilyIndex { get; init; } = Vk.QueueFamilyIgnored;
        public uint DstQueueFamilyIndex { get; init; } = Vk.QueueFamilyIgnored;
        public bool RequiresQueueOwnershipTransfer => SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
            DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
            SrcQueueFamilyIndex != DstQueueFamilyIndex;
    }

    public static class RenderGraphBarrierPlanner
    {
        public static RenderGraphBarrierPlan Build(RenderGraphDeclarationPlan declarationPlan)
        {
            return Build(declarationPlan, asyncSchedule: null, device: null);
        }

        public static RenderGraphBarrierPlan Build(
            RenderGraphDeclarationPlan declarationPlan,
            AsyncSchedulePlan? asyncSchedule,
            AsyncComputeDeviceProfile? device)
        {
            if (declarationPlan == null)
                throw new ArgumentNullException(nameof(declarationPlan));

            var passByName = declarationPlan.Passes.ToDictionary(pass => pass.Name, StringComparer.Ordinal);
            Dictionary<string, ScheduledPass> scheduledByName = asyncSchedule?.Passes.ToDictionary(pass => pass.PassName, StringComparer.Ordinal) ??
                new Dictionary<string, ScheduledPass>(StringComparer.Ordinal);
            var imageStates = new Dictionary<RenderGraphResourceHandle, ResourceState>();
            var bufferStates = new Dictionary<RenderGraphResourceHandle, ResourceState>();
            var batches = new List<RenderGraphPassBarrierBatch>();

            foreach (string passName in declarationPlan.Diagnostics.CompiledPassOrder)
            {
                RenderGraphPassDesc pass = passByName[passName];
                var imageBarriers = new List<RenderGraphImageBarrierDesc>();
                var bufferBarriers = new List<RenderGraphBufferBarrierDesc>();

                RenderGraphQueueClass passQueue = scheduledByName.TryGetValue(pass.Name, out ScheduledPass? scheduled)
                    ? scheduled.Queue
                    : pass.Queue;

                ProcessUses(pass.Name, pass.Reads, imageBarriers, bufferBarriers, imageStates, bufferStates, declarationPlan, passQueue, device);
                ProcessUses(pass.Name, pass.Writes, imageBarriers, bufferBarriers, imageStates, bufferStates, declarationPlan, passQueue, device);
                ProcessUses(pass.Name, pass.ReadWrites, imageBarriers, bufferBarriers, imageStates, bufferStates, declarationPlan, passQueue, device);

                batches.Add(new RenderGraphPassBarrierBatch(passName, imageBarriers, bufferBarriers));
            }

            return new RenderGraphBarrierPlan(batches);
        }

        private static void ProcessUses(
            string passName,
            IReadOnlyList<RenderGraphResourceUse> uses,
            List<RenderGraphImageBarrierDesc> imageBarriers,
            List<RenderGraphBufferBarrierDesc> bufferBarriers,
            Dictionary<RenderGraphResourceHandle, ResourceState> imageStates,
            Dictionary<RenderGraphResourceHandle, ResourceState> bufferStates,
            RenderGraphDeclarationPlan declarationPlan,
            RenderGraphQueueClass queue,
            AsyncComputeDeviceProfile? device)
        {
            foreach (RenderGraphResourceUse use in uses)
            {
                AccessState next = ResolveAccessState(use, declarationPlan);
                if (use.Handle.Kind == RenderGraphResourceKind.Image)
                {
                    if (!imageStates.TryGetValue(use.Handle, out ResourceState previous))
                    {
                        if (IsWrite(use.Access))
                        {
                            imageBarriers.Add(CreateInitialImageBarrier(use, passName, next, declarationPlan));
                            imageStates[use.Handle] = new ResourceState(next, use, queue, passName);
                        }
                        else
                        {
                            imageStates[use.Handle] = new ResourceState(next, use, queue, passName);
                        }

                        continue;
                    }

                    if (previous.AccessState != next || !SameSubresource(previous.Use, use) || previous.Queue != queue)
                        imageBarriers.Add(AddOwnership(CreateImageBarrier(use, previous, passName, next, declarationPlan), previous.Queue, queue, device));
                    imageStates[use.Handle] = new ResourceState(next, use, queue, passName);
                }
                else
                {
                    if (!bufferStates.TryGetValue(use.Handle, out ResourceState previous))
                    {
                        bufferStates[use.Handle] = new ResourceState(next, use, queue, passName);
                        continue;
                    }

                    if (previous.AccessState != next || previous.Queue != queue)
                        bufferBarriers.Add(AddOwnership(CreateBufferBarrier(use, previous, passName, next, declarationPlan), previous.Queue, queue, device));
                    bufferStates[use.Handle] = new ResourceState(next, use, queue, passName);
                }
            }
        }

        private static RenderGraphImageBarrierDesc CreateInitialImageBarrier(
            RenderGraphResourceUse use,
            string passName,
            AccessState next,
            RenderGraphDeclarationPlan declarationPlan)
        {
            return new RenderGraphImageBarrierDesc(
                use.Handle,
                GetImageName(use.Handle, declarationPlan),
                string.Empty,
                passName,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                next.Stage,
                next.Access,
                ImageLayout.Undefined,
                next.Layout,
                CreateRange(use, declarationPlan.Images[use.Handle.Index].Format));
        }

        private static RenderGraphImageBarrierDesc CreateImageBarrier(
            RenderGraphResourceUse use,
            ResourceState previous,
            string consumerPassName,
            AccessState next,
            RenderGraphDeclarationPlan declarationPlan)
        {
            return new RenderGraphImageBarrierDesc(
                use.Handle,
                GetImageName(use.Handle, declarationPlan),
                previous.PassName,
                consumerPassName,
                previous.AccessState.Stage,
                previous.AccessState.Access,
                next.Stage,
                next.Access,
                previous.AccessState.Layout,
                next.Layout,
                CreateRange(use, declarationPlan.Images[use.Handle.Index].Format));
        }

        private static RenderGraphBufferBarrierDesc CreateBufferBarrier(
            RenderGraphResourceUse use,
            ResourceState previous,
            string consumerPassName,
            AccessState next,
            RenderGraphDeclarationPlan declarationPlan)
        {
            return new RenderGraphBufferBarrierDesc(
                use.Handle,
                declarationPlan.Buffers[use.Handle.Index].Name,
                previous.PassName,
                consumerPassName,
                previous.AccessState.Stage,
                previous.AccessState.Access,
                next.Stage,
                next.Access,
                0,
                Vk.WholeSize);
        }

        private static RenderGraphImageBarrierDesc AddOwnership(
            RenderGraphImageBarrierDesc barrier,
            RenderGraphQueueClass previousQueue,
            RenderGraphQueueClass nextQueue,
            AsyncComputeDeviceProfile? device)
        {
            if (device == null || previousQueue == nextQueue)
                return barrier;

            uint src = QueueFamily(previousQueue, device);
            uint dst = QueueFamily(nextQueue, device);
            if (src == dst)
                return barrier;

            return barrier with
            {
                SrcQueueFamilyIndex = src,
                DstQueueFamilyIndex = dst
            };
        }

        private static RenderGraphBufferBarrierDesc AddOwnership(
            RenderGraphBufferBarrierDesc barrier,
            RenderGraphQueueClass previousQueue,
            RenderGraphQueueClass nextQueue,
            AsyncComputeDeviceProfile? device)
        {
            if (device == null || previousQueue == nextQueue)
                return barrier;

            uint src = QueueFamily(previousQueue, device);
            uint dst = QueueFamily(nextQueue, device);
            if (src == dst)
                return barrier;

            return barrier with
            {
                SrcQueueFamilyIndex = src,
                DstQueueFamilyIndex = dst
            };
        }

        private static uint QueueFamily(RenderGraphQueueClass queue, AsyncComputeDeviceProfile device)
        {
            return queue switch
            {
                RenderGraphQueueClass.Compute => device.ComputeQueueFamily,
                RenderGraphQueueClass.Transfer => device.TransferQueueFamily,
                _ => device.GraphicsQueueFamily
            };
        }

        private static ImageSubresourceRange CreateRange(RenderGraphResourceUse use, Format format)
        {
            return new ImageSubresourceRange
            {
                AspectMask = GetAspectMask(format),
                BaseMipLevel = use.BaseMipLevel,
                LevelCount = use.LevelCount,
                BaseArrayLayer = use.BaseArrayLayer,
                LayerCount = use.LayerCount
            };
        }

        private static AccessState ResolveAccessState(RenderGraphResourceUse use, RenderGraphDeclarationPlan declarationPlan)
        {
            AccessState state = ResolveAccessState(use.Access);
            if (use.Handle.Kind == RenderGraphResourceKind.Image &&
                use.Access == RenderGraphResourceAccess.SampledRead &&
                (GetAspectMask(declarationPlan.Images[use.Handle.Index].Format) & ImageAspectFlags.DepthBit) != 0)
            {
                state = state with
                {
                    Access = AccessFlags2.ShaderSampledReadBit,
                    Layout = ImageLayout.DepthStencilReadOnlyOptimal
                };
            }

            return state with { Stage = use.Stages == 0 ? state.Stage : use.Stages };
        }

        private static AccessState ResolveAccessState(RenderGraphResourceAccess access)
        {
            return access switch
            {
                RenderGraphResourceAccess.SampledRead => new AccessState(PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit, ImageLayout.ShaderReadOnlyOptimal),
                RenderGraphResourceAccess.ColorAttachmentRead => new AccessState(PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit, ImageLayout.ColorAttachmentOptimal),
                RenderGraphResourceAccess.ColorAttachmentWrite => new AccessState(PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit, ImageLayout.ColorAttachmentOptimal),
                RenderGraphResourceAccess.DepthStencilAttachmentRead => new AccessState(PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit, ImageLayout.DepthStencilReadOnlyOptimal),
                RenderGraphResourceAccess.DepthStencilAttachmentWrite => new AccessState(PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit, ImageLayout.DepthStencilAttachmentOptimal),
                RenderGraphResourceAccess.StorageRead => new AccessState(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit, ImageLayout.General),
                RenderGraphResourceAccess.StorageWrite => new AccessState(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageWriteBit, ImageLayout.General),
                RenderGraphResourceAccess.TransferRead => new AccessState(PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit, ImageLayout.TransferSrcOptimal),
                RenderGraphResourceAccess.TransferWrite => new AccessState(PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, ImageLayout.TransferDstOptimal),
                RenderGraphResourceAccess.VertexBufferRead => new AccessState(PipelineStageFlags2.VertexAttributeInputBit, AccessFlags2.VertexAttributeReadBit, ImageLayout.Undefined),
                RenderGraphResourceAccess.IndexBufferRead => new AccessState(PipelineStageFlags2.IndexInputBit, AccessFlags2.IndexReadBit, ImageLayout.Undefined),
                RenderGraphResourceAccess.IndirectCommandRead => new AccessState(PipelineStageFlags2.DrawIndirectBit, AccessFlags2.IndirectCommandReadBit, ImageLayout.Undefined),
                RenderGraphResourceAccess.UniformRead => new AccessState(PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.UniformReadBit, ImageLayout.Undefined),
                _ => new AccessState(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit, ImageLayout.General)
            };
        }

        private static bool IsWrite(RenderGraphResourceAccess access)
        {
            return access is RenderGraphResourceAccess.ColorAttachmentWrite or
                RenderGraphResourceAccess.DepthStencilAttachmentWrite or
                RenderGraphResourceAccess.StorageWrite or
                RenderGraphResourceAccess.TransferWrite;
        }

        private static bool SameSubresource(RenderGraphResourceUse left, RenderGraphResourceUse right)
        {
            return left.BaseMipLevel == right.BaseMipLevel &&
                   left.LevelCount == right.LevelCount &&
                   left.BaseArrayLayer == right.BaseArrayLayer &&
                   left.LayerCount == right.LayerCount;
        }

        private static ImageAspectFlags GetAspectMask(Format format)
        {
            return format switch
            {
                Format.D16Unorm or Format.D32Sfloat => ImageAspectFlags.DepthBit,
                Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.ColorBit
            };
        }

        private static string GetImageName(RenderGraphResourceHandle handle, RenderGraphDeclarationPlan declarationPlan)
        {
            return declarationPlan.Images[handle.Index].Name;
        }

        private readonly record struct AccessState(PipelineStageFlags2 Stage, AccessFlags2 Access, ImageLayout Layout);

        private readonly record struct ResourceState(AccessState AccessState, RenderGraphResourceUse Use, RenderGraphQueueClass Queue, string PassName);
    }
}
