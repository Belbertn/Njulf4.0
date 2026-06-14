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
        public int ProbeCapacity => RuntimeProbeCapacity;
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
            _activeProbeCount = ReflectionProbeData.BuildProbes(
                authoredProbes,
                _settings.Reflections,
                _probeScratch.AsSpan(0, AbsoluteMaxProbeCapacity));
            UpdateResourceMetrics();

            GPUReflectionProbeHeader header = ReflectionProbeData.BuildHeader(
                _activeProbeCount,
                _settings.Reflections,
                BindlessIndex.ReflectionProbeCubemapArrayTexture,
                BindlessIndex.ReflectionProbeDebugTexture,
                _probeMipCount);

            ulong uploadSize = ReflectionProbeData.HeaderSize + ReflectionProbeData.ProbeStride * (ulong)_activeProbeCount;
            if (uploadSize == ReflectionProbeData.HeaderSize)
                uploadSize = ReflectionProbeData.HeaderSize;

            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _metadataBuffer,
                header,
                _probeScratch.AsSpan(0, _activeProbeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        private void UpdateResourceMetrics()
        {
            _probeMipCount = ReflectionProbeData.CalculateMipCount(_settings.Reflections.ProbeResolution);
            _estimatedBytes = ReflectionProbeData.EstimateCubemapArrayBytes(
                RuntimeProbeCapacity,
                _settings.Reflections.ProbeResolution,
                _probeMipCount) + MetadataBufferSize;
        }

        private int RuntimeProbeCapacity => Math.Min(
            AbsoluteMaxProbeCapacity,
            Math.Max(_activeProbeCount, _settings.Reflections.MaxProbes));

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
