using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Memory
{
    public unsafe delegate void GpuUploadWriter(void* destination, ulong byteCount);

    public readonly struct UploadResult
    {
        public UploadResult(bool recorded, ulong byteCount, BufferHandle stagingBuffer, ulong stagingOffset)
        {
            Recorded = recorded;
            ByteCount = byteCount;
            StagingBuffer = stagingBuffer;
            StagingOffset = stagingOffset;
        }

        public bool Recorded { get; }
        public ulong ByteCount { get; }
        public BufferHandle StagingBuffer { get; }
        public ulong StagingOffset { get; }
    }

    public readonly struct UploadBarrierDescription
    {
        public UploadBarrierDescription(
            PipelineStageFlags2 dstStageMask,
            AccessFlags2 dstAccessMask,
            ulong destinationOffset = 0,
            ulong size = Vk.WholeSize)
        {
            DstStageMask = dstStageMask;
            DstAccessMask = dstAccessMask;
            DestinationOffset = destinationOffset;
            Size = size;
        }

        public PipelineStageFlags2 DstStageMask { get; }
        public AccessFlags2 DstAccessMask { get; }
        public ulong DestinationOffset { get; }
        public ulong Size { get; }
    }

    public static unsafe class GpuBufferUploader
    {
        public static UploadResult UploadSpanToBuffer<T>(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            ReadOnlySpan<T> data,
            ulong destinationOffset = 0,
            UploadBarrierDescription? barrierDescription = null)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return new UploadResult(false, 0, BufferHandle.Invalid, 0);

            ValidateUploadInputs(context, bufferManager, stagingRing, commandBuffer, destination);

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(T));
            var (stagingBuffer, stagingOffset) = stagingRing.Allocate(dataSize);
            void* mappedData = bufferManager.GetMappedPointer(stagingBuffer);

            fixed (T* source = data)
            {
                global::System.Buffer.MemoryCopy(
                    source,
                    (byte*)mappedData + stagingOffset,
                    dataSize,
                    dataSize);
            }

            bufferManager.FlushBuffer(stagingBuffer, stagingOffset, dataSize);
            RecordCopyAndOptionalBarrier(
                context,
                bufferManager,
                commandBuffer,
                stagingBuffer,
                stagingOffset,
                destination,
                destinationOffset,
                dataSize,
                barrierDescription);

            return new UploadResult(true, dataSize, stagingBuffer, stagingOffset);
        }

        public static UploadResult UploadValueToBuffer<T>(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            in T value,
            ulong destinationOffset = 0,
            UploadBarrierDescription? barrierDescription = null)
            where T : unmanaged
        {
            fixed (T* valuePtr = &value)
            {
                return UploadSpanToBuffer(
                    context,
                    bufferManager,
                    stagingRing,
                    commandBuffer,
                    destination,
                    new ReadOnlySpan<T>(valuePtr, 1),
                    destinationOffset,
                    barrierDescription);
            }
        }

        public static UploadResult UploadPaddedSpanToBuffer<T>(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            ReadOnlySpan<T> data,
            int elementCapacity,
            ulong destinationOffset = 0,
            UploadBarrierDescription? barrierDescription = null)
            where T : unmanaged
        {
            if (elementCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(elementCapacity));
            if (data.Length > elementCapacity)
                throw new InvalidOperationException($"Upload has {data.Length} records, but capacity is {elementCapacity}.");
            if (elementCapacity == 0)
                return new UploadResult(false, 0, BufferHandle.Invalid, 0);

            ValidateUploadInputs(context, bufferManager, stagingRing, commandBuffer, destination);

            ulong dataSize = checked((ulong)elementCapacity * (ulong)sizeof(T));
            var (stagingBuffer, stagingOffset) = stagingRing.Allocate(dataSize);
            void* mappedData = bufferManager.GetMappedPointer(stagingBuffer);
            byte* destinationBytes = (byte*)mappedData + stagingOffset;
            new Span<byte>(destinationBytes, checked((int)dataSize)).Clear();

            if (!data.IsEmpty)
            {
                fixed (T* source = data)
                {
                    global::System.Buffer.MemoryCopy(
                        source,
                        destinationBytes,
                        dataSize,
                        checked((ulong)data.Length * (ulong)sizeof(T)));
                }
            }

            bufferManager.FlushBuffer(stagingBuffer, stagingOffset, dataSize);
            RecordCopyAndOptionalBarrier(
                context,
                bufferManager,
                commandBuffer,
                stagingBuffer,
                stagingOffset,
                destination,
                destinationOffset,
                dataSize,
                barrierDescription);

            return new UploadResult(true, dataSize, stagingBuffer, stagingOffset);
        }

        public static UploadResult UploadBytesToBuffer(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            ulong byteCount,
            GpuUploadWriter writer,
            ulong destinationOffset = 0,
            UploadBarrierDescription? barrierDescription = null)
        {
            if (byteCount == 0)
                return new UploadResult(false, 0, BufferHandle.Invalid, 0);
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            ValidateUploadInputs(context, bufferManager, stagingRing, commandBuffer, destination);

            var (stagingBuffer, stagingOffset) = stagingRing.Allocate(byteCount);
            void* mappedData = bufferManager.GetMappedPointer(stagingBuffer);
            writer((byte*)mappedData + stagingOffset, byteCount);

            bufferManager.FlushBuffer(stagingBuffer, stagingOffset, byteCount);
            RecordCopyAndOptionalBarrier(
                context,
                bufferManager,
                commandBuffer,
                stagingBuffer,
                stagingOffset,
                destination,
                destinationOffset,
                byteCount,
                barrierDescription);

            return new UploadResult(true, byteCount, stagingBuffer, stagingOffset);
        }

        public static UploadResult UploadHeaderAndSpanToBuffer<THeader, TElement>(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            in THeader header,
            ReadOnlySpan<TElement> elements,
            ulong destinationOffset = 0,
            UploadBarrierDescription? barrierDescription = null)
            where THeader : unmanaged
            where TElement : unmanaged
        {
            ulong headerSize = (ulong)sizeof(THeader);
            ulong elementBytes = checked((ulong)elements.Length * (ulong)sizeof(TElement));
            ulong byteCount = checked(headerSize + elementBytes);
            ValidateUploadInputs(context, bufferManager, stagingRing, commandBuffer, destination);

            var (stagingBuffer, stagingOffset) = stagingRing.Allocate(byteCount);
            void* mappedData = bufferManager.GetMappedPointer(stagingBuffer);
            byte* destinationBytes = (byte*)mappedData + stagingOffset;

            fixed (THeader* headerSource = &header)
            {
                global::System.Buffer.MemoryCopy(headerSource, destinationBytes, headerSize, headerSize);
            }

            if (!elements.IsEmpty)
            {
                fixed (TElement* elementSource = elements)
                {
                    global::System.Buffer.MemoryCopy(
                        elementSource,
                        destinationBytes + headerSize,
                        elementBytes,
                        elementBytes);
                }
            }

            bufferManager.FlushBuffer(stagingBuffer, stagingOffset, byteCount);
            RecordCopyAndOptionalBarrier(
                context,
                bufferManager,
                commandBuffer,
                stagingBuffer,
                stagingOffset,
                destination,
                destinationOffset,
                byteCount,
                barrierDescription);

            return new UploadResult(true, byteCount, stagingBuffer, stagingOffset);
        }

        private static void ValidateUploadInputs(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (bufferManager == null)
                throw new ArgumentNullException(nameof(bufferManager));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for buffer upload.", nameof(commandBuffer));
            if (!destination.IsValid)
                throw new ArgumentException("A valid destination buffer is required for buffer upload.", nameof(destination));
        }

        private static void RecordCopyAndOptionalBarrier(
            VulkanContext context,
            BufferManager bufferManager,
            CommandBuffer commandBuffer,
            BufferHandle stagingBuffer,
            ulong stagingOffset,
            BufferHandle destination,
            ulong destinationOffset,
            ulong dataSize,
            UploadBarrierDescription? barrierDescription)
        {
            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = destinationOffset,
                Size = dataSize
            };

            VkBuffer stagingVkBuffer = bufferManager.GetBuffer(stagingBuffer);
            VkBuffer destinationVkBuffer = bufferManager.GetBuffer(destination);
            context.Api.CmdCopyBuffer(commandBuffer, stagingVkBuffer, destinationVkBuffer, 1, &copy);

            if (!barrierDescription.HasValue)
                return;

            UploadBarrierDescription barrierInfo = barrierDescription.Value;
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = barrierInfo.DstStageMask,
                DstAccessMask = barrierInfo.DstAccessMask,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = destinationVkBuffer,
                Offset = barrierInfo.DestinationOffset,
                Size = barrierInfo.Size == Vk.WholeSize ? dataSize : barrierInfo.Size
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }
}
