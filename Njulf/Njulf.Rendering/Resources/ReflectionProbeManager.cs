using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using SysBuffer = System.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class ReflectionProbeManager : IDisposable
    {
        public const int AbsoluteMaxProbeCapacity = 256;
        private static readonly ulong MetadataBufferSize =
            ReflectionProbeData.HeaderSize + ReflectionProbeData.ProbeStride * AbsoluteMaxProbeCapacity;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly GPUReflectionProbe[] _probeScratch = new GPUReflectionProbe[AbsoluteMaxProbeCapacity];

        private BufferHandle _metadataBuffer;
        private int _activeProbeCount;
        private uint _probeMipCount;
        private ulong _estimatedBytes;
        private long _lastUploadMicroseconds;
        private bool _disposed;

        public ReflectionProbeManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _metadataBuffer = _bufferManager.CreateDeviceBuffer(
                MetadataBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ReflectionProbes,
                "Reflection Probe Metadata Buffer");

            UpdateResourceMetrics();
        }

        public int ActiveProbeCount => _activeProbeCount;
        public int ProbeCapacity => _settings.Reflections.MaxProbes;
        public uint ProbeResolution => _settings.Reflections.ProbeResolution;
        public uint ProbeMipCount => _probeMipCount;
        public ulong EstimatedBytes => _estimatedBytes;
        public ulong MetadataBufferBytes => MetadataBufferSize;
        public ulong CubemapArrayBytes => _estimatedBytes > MetadataBufferSize ? _estimatedBytes - MetadataBufferSize : 0;
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public int CapturesQueued => 0;
        public int CapturesCompleted => 0;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.ReflectionProbeBuffer,
                _bufferManager.GetBuffer(_metadataBuffer),
                0,
                MetadataBufferSize);
        }

        public void Upload(
            IReadOnlyList<ReflectionProbe> authoredProbes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (authoredProbes == null)
                throw new ArgumentNullException(nameof(authoredProbes));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for reflection probe upload.", nameof(commandBuffer));

            long uploadStart = Stopwatch.GetTimestamp();
            UpdateResourceMetrics();
            _activeProbeCount = ReflectionProbeData.BuildProbes(
                authoredProbes,
                _settings.Reflections,
                _probeScratch.AsSpan(0, AbsoluteMaxProbeCapacity));

            GPUReflectionProbeHeader header = ReflectionProbeData.BuildHeader(
                _activeProbeCount,
                _settings.Reflections,
                BindlessIndex.ReflectionProbeCubemapArrayTexture,
                BindlessIndex.ReflectionProbeDebugTexture,
                _probeMipCount);

            ulong uploadSize = ReflectionProbeData.HeaderSize + ReflectionProbeData.ProbeStride * (ulong)_activeProbeCount;
            if (uploadSize == ReflectionProbeData.HeaderSize)
                uploadSize = ReflectionProbeData.HeaderSize;

            var (stagingHandle, stagingOffset) = stagingRing.Allocate(uploadSize);
            void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
            byte* destination = (byte*)mappedData + stagingOffset;

            SysBuffer.MemoryCopy(&header, destination, ReflectionProbeData.HeaderSize, ReflectionProbeData.HeaderSize);
            if (_activeProbeCount > 0)
            {
                fixed (GPUReflectionProbe* probes = _probeScratch)
                {
                    SysBuffer.MemoryCopy(
                        probes,
                        destination + ReflectionProbeData.HeaderSize,
                        ReflectionProbeData.ProbeStride * (ulong)_activeProbeCount,
                        ReflectionProbeData.ProbeStride * (ulong)_activeProbeCount);
                }
            }

            _bufferManager.FlushBuffer(stagingHandle, stagingOffset, uploadSize);

            var region = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = 0,
                Size = uploadSize
            };

            _context.Api.CmdCopyBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(_metadataBuffer),
                1,
                &region);

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_metadataBuffer),
                Offset = 0,
                Size = uploadSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        private void UpdateResourceMetrics()
        {
            _probeMipCount = ReflectionProbeData.CalculateMipCount(_settings.Reflections.ProbeResolution);
            _estimatedBytes = ReflectionProbeData.EstimateCubemapArrayBytes(
                _settings.Reflections.MaxProbes,
                _settings.Reflections.ProbeResolution,
                _probeMipCount) + MetadataBufferSize;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_metadataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_metadataBuffer);
        }
    }
}
