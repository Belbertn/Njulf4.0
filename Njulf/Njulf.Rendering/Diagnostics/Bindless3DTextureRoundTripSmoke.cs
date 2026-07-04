using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record Bindless3DTextureRoundTripSmokeResult(bool Passed, string Detail);

    public static unsafe class Bindless3DTextureRoundTripSmoke
    {
        private const string ShaderName = "bindless_3d_texture_smoke.comp.spv";
        private const string EntryPoint = "main";
        private const uint Extent = 4u;
        private const uint FrameIndex = 7u;
        private const uint ProbeX = 1u;
        private const uint ProbeY = 2u;
        private const uint ProbeZ = 3u;
        private const float Tolerance = 0.002f;

        public static Bindless3DTextureRoundTripSmokeResult Run(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap bindlessHeap)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(bufferManager);
            ArgumentNullException.ThrowIfNull(bindlessHeap);

            VolumeTexture? source = null;
            VolumeTexture? destination = null;
            BufferHandle readback = BufferHandle.Invalid;
            int sourceIndex = -1;
            int destinationIndex = -1;
            PipelineLayout pipelineLayout = default;
            PipelineCache pipelineCache = default;
            VkPipeline pipeline = default;

            try
            {
                var extent = new Extent3D(Extent, Extent, Extent);
                source = new VolumeTexture(
                    context,
                    "Bindless3DTextureSmoke.Source",
                    Format.R16Sfloat,
                    extent,
                    new VolumeTextureDescriptor(sampled: true, transferDestination: true));
                destination = new VolumeTexture(
                    context,
                    "Bindless3DTextureSmoke.Destination",
                    Format.R16Sfloat,
                    extent,
                    new VolumeTextureDescriptor(sampled: false, storage: true, transferSource: true));

                sourceIndex = bindlessHeap.AllocateTextureIndex(source.View);
                destinationIndex = bindlessHeap.AllocateStorageImageIndex(destination.StorageView, ImageLayout.General);
                readback = bufferManager.CreateBuffer(
                    sizeof(ushort),
                    BufferUsageFlags.TransferDstBit,
                    GpuAllocator.MemoryUsage.AutoPreferHost,
                    GpuAllocator.AllocationCreateFlags.MappedBit |
                    GpuAllocator.AllocationCreateFlags.HostAccessRandomBit,
                    "Bindless3DTextureSmoke.Readback",
                    MemoryBudgetCategory.StagingBuffers);

                CreatePipelineObjects(context, bindlessHeap, out pipelineLayout, out pipelineCache, out pipeline);
                RecordRoundTrip(
                    context,
                    bufferManager,
                    bindlessHeap,
                    source,
                    destination,
                    readback,
                    pipelineLayout,
                    pipeline,
                    (uint)sourceIndex,
                    (uint)destinationIndex);

                bufferManager.InvalidateBuffer(readback, 0, sizeof(ushort));
                ushort raw = *(ushort*)bufferManager.GetMappedPointer(readback);
                float actual = HalfToSingle(raw);
                float expected = ((ProbeX ^ ProbeY ^ ProbeZ ^ FrameIndex) & 255u) / 255.0f;
                float error = Math.Abs(actual - expected);
                bool passed = error <= Tolerance;
                string detail = $"voxel={ProbeX},{ProbeY},{ProbeZ}, raw=0x{raw:X4}, actual={actual:F5}, expected={expected:F5}, error={error:F5}";
                return new Bindless3DTextureRoundTripSmokeResult(passed, detail);
            }
            catch (Exception ex)
            {
                return new Bindless3DTextureRoundTripSmokeResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (pipeline.Handle != 0)
                    context.Api.DestroyPipeline(context.Device, pipeline, null);
                if (pipelineCache.Handle != 0)
                    context.Api.DestroyPipelineCache(context.Device, pipelineCache, null);
                if (pipelineLayout.Handle != 0)
                    context.Api.DestroyPipelineLayout(context.Device, pipelineLayout, null);
                if (sourceIndex >= 0)
                    bindlessHeap.FreeTextureIndex(sourceIndex);
                if (destinationIndex >= 0)
                    bindlessHeap.FreeTextureIndex(destinationIndex);
                if (readback.IsValid)
                    bufferManager.DestroyBuffer(readback);
                destination?.Dispose();
                source?.Dispose();
            }
        }

        private static void RecordRoundTrip(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap bindlessHeap,
            VolumeTexture source,
            VolumeTexture destination,
            BufferHandle readback,
            PipelineLayout pipelineLayout,
            VkPipeline pipeline,
            uint sourceIndex,
            uint destinationIndex)
        {
            VulkanContext.SingleTimeCommandContext singleTime = context.BeginSingleTimeCommands();
            try
            {
                CommandBuffer cmd = singleTime.CommandBuffer;
                source.TransitionToTransferDestination(cmd);
                var clearColor = new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f);
                var clearRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                };
                context.Api.CmdClearColorImage(cmd, source.Image, ImageLayout.TransferDstOptimal, &clearColor, 1, &clearRange);

                source.TransitionToShaderRead(cmd);
                destination.TransitionToStorageReadWrite(cmd);

                context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
                DescriptorSet* sets = stackalloc DescriptorSet[2]
                {
                    bindlessHeap.StorageBufferSet,
                    bindlessHeap.TextureSamplerSet
                };
                context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    pipelineLayout,
                    0,
                    2,
                    sets,
                    0,
                    null);

                var push = new Bindless3DTextureSmokePushConstants
                {
                    VolumeTextureIndex = sourceIndex,
                    StorageImageIndex = destinationIndex,
                    ExtentX = Extent,
                    ExtentY = Extent,
                    ExtentZ = Extent,
                    FrameIndex = FrameIndex
                };
                context.Api.CmdPushConstants(
                    cmd,
                    pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)sizeof(Bindless3DTextureSmokePushConstants),
                    &push);
                context.Api.CmdDispatch(cmd, 1, 1, 1);

                destination.TransitionToTransferSource(cmd);
                VkBuffer readbackBuffer = bufferManager.GetBuffer(readback);
                var copy = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    ImageOffset = new Offset3D((int)ProbeX, (int)ProbeY, (int)ProbeZ),
                    ImageExtent = new Extent3D(1, 1, 1)
                };
                context.Api.CmdCopyImageToBuffer(
                    cmd,
                    destination.Image,
                    ImageLayout.TransferSrcOptimal,
                    readbackBuffer,
                    1,
                    &copy);

                context.EndSingleTimeCommands(singleTime);
            }
            catch
            {
                context.Api.FreeCommandBuffers(context.Device, singleTime.CommandPool, 1, &singleTime.CommandBuffer);
                throw;
            }
        }

        private static void CreatePipelineObjects(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            out PipelineLayout pipelineLayout,
            out PipelineCache pipelineCache,
            out VkPipeline pipeline)
        {
            pipelineLayout = default;
            pipelineCache = default;
            pipeline = default;
            nint entryPointName = SilkMarshal.StringToPtr(EntryPoint);
            ShaderModule shaderModule = default;

            try
            {
                DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2]
                {
                    bindlessHeap.StorageBufferSetLayout,
                    bindlessHeap.TextureSamplerSetLayout
                };
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)sizeof(Bindless3DTextureSmokePushConstants)
                };
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 2,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create bindless 3D texture smoke pipeline layout", result);
                context.SetDebugName(pipelineLayout.Handle, ObjectType.PipelineLayout, "Bindless3DTextureSmoke Pipeline Layout");

                var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
                result = context.Api.CreatePipelineCache(context.Device, &cacheInfo, null, out pipelineCache);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create bindless 3D texture smoke pipeline cache", result);

                shaderModule = ShaderModuleLoader.Load(context, ShaderName);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)entryPointName
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = pipelineLayout,
                    BasePipelineIndex = -1
                };
                result = context.Api.CreateComputePipelines(context.Device, pipelineCache, 1, &pipelineInfo, null, out pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create bindless 3D texture smoke compute pipeline", result);
                context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "Bindless3DTextureSmoke Compute Pipeline");
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    context.Api.DestroyShaderModule(context.Device, shaderModule, null);
                SilkMarshal.Free(entryPointName);
            }
        }

        private static float HalfToSingle(ushort value)
        {
            uint sign = (uint)(value >> 15) & 0x1u;
            uint exponent = (uint)(value >> 10) & 0x1Fu;
            uint mantissa = (uint)value & 0x3FFu;
            float signScale = sign == 0u ? 1.0f : -1.0f;

            if (exponent == 0u)
                return mantissa == 0u
                    ? signScale * 0.0f
                    : signScale * MathF.Pow(2.0f, -14.0f) * (mantissa / 1024.0f);
            if (exponent == 31u)
                return mantissa == 0u ? signScale * float.PositiveInfinity : float.NaN;

            return signScale * MathF.Pow(2.0f, (int)exponent - 15) * (1.0f + mantissa / 1024.0f);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Bindless3DTextureSmokePushConstants
        {
            public uint VolumeTextureIndex;
            public uint StorageImageIndex;
            public uint ExtentX;
            public uint ExtentY;
            public uint ExtentZ;
            public uint FrameIndex;
        }
    }
}
