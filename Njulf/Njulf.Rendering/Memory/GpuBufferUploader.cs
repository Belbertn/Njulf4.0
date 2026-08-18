using System;
using System.Buffers;
using System.Collections.Generic;
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

    /// <summary>
    /// Describes a contiguous destination element range whose source records are
    /// packed consecutively in the span passed to <see cref="GpuBufferUploader.UploadRunsToBuffer{T}"/>.
    /// </summary>
    public readonly struct BufferUploadRun
    {
        public BufferUploadRun(int destinationElementIndex, int elementCount)
        {
            if (destinationElementIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(destinationElementIndex));
            if (elementCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(elementCount));

            DestinationElementIndex = destinationElementIndex;
            ElementCount = elementCount;
        }

        public int DestinationElementIndex { get; }
        public int ElementCount { get; }
    }

    public static unsafe class GpuBufferUploader
    {
        /// <summary>
        /// Uploads packed, non-overlapping runs with one staging allocation and
        /// one destination barrier. This avoids rewriting GPU-owned records when
        /// the CPU only invalidates a sparse set of slots.
        /// </summary>
        public static UploadResult UploadRunsToBuffer<T>(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            ReadOnlySpan<T> packedData,
            IReadOnlyList<BufferUploadRun> runs,
            UploadBarrierDescription? barrierDescription = null)
            where T : unmanaged
        {
            if (packedData.IsEmpty || runs == null || runs.Count == 0)
                return new UploadResult(false, 0, BufferHandle.Invalid, 0);

            ValidateUploadInputs(context, bufferManager, stagingRing, commandBuffer, destination);
            int expectedElementCount = 0;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                expectedElementCount = checked(expectedElementCount + runs[runIndex].ElementCount);
            if (expectedElementCount != packedData.Length)
                throw new ArgumentException("Packed data must contain exactly the records described by the upload runs.", nameof(packedData));

            ulong byteCount = checked((ulong)packedData.Length * (ulong)sizeof(T));
            var (stagingBuffer, stagingOffset) = stagingRing.Allocate(byteCount);
            void* mappedData = bufferManager.GetMappedPointer(stagingBuffer);
            fixed (T* source = packedData)
            {
                global::System.Buffer.MemoryCopy(source, (byte*)mappedData + stagingOffset, byteCount, byteCount);
            }
            bufferManager.FlushBuffer(stagingBuffer, stagingOffset, byteCount);

            VkBuffer stagingVkBuffer = bufferManager.GetBuffer(stagingBuffer);
            VkBuffer destinationVkBuffer = bufferManager.GetBuffer(destination);
            BufferCopy[] copyRegions =
                ArrayPool<BufferCopy>.Shared.Rent(runs.Count);
            try
            {
                ulong sourceElementOffset = 0;
                for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    BufferUploadRun run = runs[runIndex];
                    copyRegions[runIndex] = new BufferCopy
                    {
                        SrcOffset = checked(
                            stagingOffset +
                            sourceElementOffset * (ulong)sizeof(T)),
                        DstOffset = checked(
                            (ulong)run.DestinationElementIndex *
                            (ulong)sizeof(T)),
                        Size = checked(
                            (ulong)run.ElementCount * (ulong)sizeof(T))
                    };
                    sourceElementOffset += (ulong)run.ElementCount;
                }

                // Vulkan accepts a disjoint region array in one copy command.
                // Recording one command per sparse run made a correct exposed-
                // slab invalidation needlessly expensive and encouraged callers
                // to replace it with an unsafe whole-buffer clear.
                fixed (BufferCopy* copyRegionPointer = copyRegions)
                {
                    context.Api.CmdCopyBuffer(
                        commandBuffer,
                        stagingVkBuffer,
                        destinationVkBuffer,
                        checked((uint)runs.Count),
                        copyRegionPointer);
                }
            }
            finally
            {
                ArrayPool<BufferCopy>.Shared.Return(copyRegions);
            }

            if (barrierDescription.HasValue)
            {
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
                    Size = barrierInfo.Size
                };
                var dependency = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = 1,
                    PBufferMemoryBarriers = &barrier
                };
                context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
            }

            return new UploadResult(true, byteCount, stagingBuffer, stagingOffset);
        }

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
