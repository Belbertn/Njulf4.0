using System;
using System.Collections.Generic;
using System.Diagnostics;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class DdgiProbeVolumeManager : IDisposable
    {
        public const int AbsoluteMaxVolumeCount = 16;
        public const int AbsoluteMaxProbeCount = 65_536;

        private static readonly ulong VolumeMetadataBufferSize =
            GlobalIlluminationProbeVolumeData.HeaderSize +
            GlobalIlluminationProbeVolumeData.VolumeStride * AbsoluteMaxVolumeCount;
        private static readonly ulong MinProbeStateBufferSize = GlobalIlluminationProbeVolumeData.ProbeStateStride;
        private const ulong MinResourceBufferSize = 16;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly GPUDdgiProbeVolume[] _volumeScratch = new GPUDdgiProbeVolume[AbsoluteMaxVolumeCount];

        private BufferHandle _volumeMetadataBuffer;
        private BufferHandle _probeStateBuffer;
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _probeRelocationClassificationBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BindlessHeap? _registeredBindlessHeap;
        private ulong _probeStateBufferSize;
        private ulong _probeUpdateQueueBufferSize;
        private ulong _probeRelocationClassificationBufferSize;
        private ulong _irradianceAtlasBufferSize;
        private ulong _visibilityAtlasBufferSize;
        private int _volumeCount;
        private int _probeCount;
        private int _activeProbeCount;
        private int _raysPerProbe;
        private int _maxProbeUpdatesPerFrame;
        private int _updateCursor;
        private int _scheduledUpdateStartProbeIndex;
        private int _scheduledProbeUpdateCount;
        private ulong _textureBytes;
        private long _lastUploadMicroseconds;
        private bool _disposed;

        public DdgiProbeVolumeManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _volumeMetadataBuffer = _bufferManager.CreateDeviceBuffer(
                VolumeMetadataBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Probe Volume Metadata Buffer");
            EnsureProbeStateCapacity(0);
            EnsureProbeUpdateQueueCapacity(0);
            EnsureProbeRelocationClassificationCapacity(0);
            EnsureAtlasCapacity(
                ref _irradianceAtlasBuffer,
                ref _irradianceAtlasBufferSize,
                0,
                BindlessIndex.DdgiIrradianceAtlasBuffer,
                "DDGI Irradiance Atlas Buffer");
            EnsureAtlasCapacity(
                ref _visibilityAtlasBuffer,
                ref _visibilityAtlasBufferSize,
                0,
                BindlessIndex.DdgiVisibilityAtlasBuffer,
                "DDGI Visibility Atlas Buffer");
        }

        public int VolumeCount => _volumeCount;
        public int ProbeCount => _probeCount;
        public int ActiveProbeCount => _activeProbeCount;
        public int RaysPerProbe => _raysPerProbe;
        public int MaxProbeUpdatesPerFrame => _maxProbeUpdatesPerFrame;
        public int ScheduledUpdateStartProbeIndex => _scheduledUpdateStartProbeIndex;
        public int ScheduledProbeUpdateCount => _scheduledProbeUpdateCount;
        public ulong TextureBytes => _textureBytes;
        public ulong BufferBytes => VolumeMetadataBufferSize +
            _probeStateBufferSize +
            _probeUpdateQueueBufferSize +
            _probeRelocationClassificationBufferSize;
        public long LastUploadMicroseconds => _lastUploadMicroseconds;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiProbeVolumeBuffer,
                _bufferManager.GetBuffer(_volumeMetadataBuffer),
                0,
                VolumeMetadataBufferSize);
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiProbeStateBuffer,
                _bufferManager.GetBuffer(_probeStateBuffer),
                0,
                _probeStateBufferSize);
            RegisterIfValid(BindlessIndex.DdgiProbeUpdateQueueBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBufferSize);
            RegisterIfValid(
                BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                _probeRelocationClassificationBuffer,
                _probeRelocationClassificationBufferSize);
            RegisterIfValid(BindlessIndex.DdgiIrradianceAtlasBuffer, _irradianceAtlasBuffer, _irradianceAtlasBufferSize);
            RegisterIfValid(BindlessIndex.DdgiVisibilityAtlasBuffer, _visibilityAtlasBuffer, _visibilityAtlasBufferSize);
        }

        public void Upload(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (authoredVolumes == null)
                throw new ArgumentNullException(nameof(authoredVolumes));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI probe upload.", nameof(commandBuffer));

            long uploadStart = Stopwatch.GetTimestamp();
            _volumeCount = GlobalIlluminationProbeVolumeData.BuildVolumes(
                authoredVolumes,
                _settings.GlobalIllumination,
                _volumeScratch.AsSpan(0, AbsoluteMaxVolumeCount),
                out _probeCount,
                out _activeProbeCount,
                out _raysPerProbe,
                out _maxProbeUpdatesPerFrame);

            if (_probeCount > AbsoluteMaxProbeCount)
            {
                _probeCount = AbsoluteMaxProbeCount;
                _activeProbeCount = Math.Min(_activeProbeCount, AbsoluteMaxProbeCount);
            }

            EnsureProbeStateCapacity(_probeCount);
            _textureBytes = GlobalIlluminationProbeVolumeData.EstimateTextureBytes(_activeProbeCount);
            EnsureProbeUpdateQueueCapacity(_probeCount);
            EnsureProbeRelocationClassificationCapacity(_probeCount);
            EnsureAtlasCapacity(ref _irradianceAtlasBuffer, ref _irradianceAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateIrradianceAtlasBytes(_activeProbeCount), BindlessIndex.DdgiIrradianceAtlasBuffer, "DDGI Irradiance Atlas Buffer");
            EnsureAtlasCapacity(ref _visibilityAtlasBuffer, ref _visibilityAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount), BindlessIndex.DdgiVisibilityAtlasBuffer, "DDGI Visibility Atlas Buffer");

            GPUDdgiProbeVolumeHeader header = GlobalIlluminationProbeVolumeData.BuildHeader(
                _volumeCount,
                _probeCount,
                _activeProbeCount,
                _raysPerProbe,
                _maxProbeUpdatesPerFrame,
                _settings.GlobalIllumination,
                BindlessIndex.DdgiProbeStateBuffer);

            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _volumeMetadataBuffer,
                header,
                _volumeScratch.AsSpan(0, _volumeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        public int ScheduleProbeUpdates(bool enabled) => ScheduleProbeUpdates(enabled, null);

        public int ScheduleProbeUpdates(bool enabled, IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            if (!enabled || _activeProbeCount <= 0 || _maxProbeUpdatesPerFrame <= 0)
            {
                _scheduledUpdateStartProbeIndex = 0;
                _scheduledProbeUpdateCount = 0;
                return 0;
            }

            if (_updateCursor >= _activeProbeCount)
                _updateCursor = 0;

            _scheduledProbeUpdateCount = Math.Min(_maxProbeUpdatesPerFrame, _activeProbeCount);
            if (TryScheduleDirtyProbeUpdates(dirtyBounds, _scheduledProbeUpdateCount))
                return _scheduledProbeUpdateCount;

            _scheduledUpdateStartProbeIndex = _updateCursor;
            _updateCursor = (_updateCursor + _scheduledProbeUpdateCount) % _activeProbeCount;
            return _scheduledProbeUpdateCount;
        }

        private bool TryScheduleDirtyProbeUpdates(IReadOnlyList<BoundingBox>? dirtyBounds, int updateCount)
        {
            if (dirtyBounds == null || dirtyBounds.Count == 0 || _volumeCount <= 0 || updateCount <= 0)
                return false;

            int bestProbeIndex = -1;
            float bestScore = float.MaxValue;

            for (int dirtyIndex = 0; dirtyIndex < dirtyBounds.Count; dirtyIndex++)
            {
                BoundingBox dirtyBoundsItem = dirtyBounds[dirtyIndex];
                Vector3 dirtyCenter = dirtyBoundsItem.Center;

                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                {
                    GPUDdgiProbeVolume volume = _volumeScratch[volumeIndex];
                    Vector3 origin = ReadVector3(volume.OriginAndFirstProbeIndex);
                    Vector3 size = ReadVector3(volume.SizeAndProbeCountX);
                    Vector3 spacing = ReadVector3(volume.ProbeSpacingAndProbeCountY);
                    int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
                    int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
                    int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
                    int firstProbeIndex = Math.Clamp((int)MathF.Round(volume.OriginAndFirstProbeIndex.W), 0, _activeProbeCount - 1);

                    BoundingBox volumeBounds = new(origin, origin + size);
                    if (!dirtyBoundsItem.Intersects(volumeBounds) && !volumeBounds.Contains(dirtyCenter))
                        continue;

                    int x = spacing.X > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.X - origin.X) / spacing.X), 0, countX - 1) : 0;
                    int y = spacing.Y > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Y - origin.Y) / spacing.Y), 0, countY - 1) : 0;
                    int z = spacing.Z > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Z - origin.Z) / spacing.Z), 0, countZ - 1) : 0;
                    int localProbeIndex = x + y * countX + z * countX * countY;
                    int probeIndex = Math.Clamp(firstProbeIndex + localProbeIndex, 0, _activeProbeCount - 1);

                    Vector3 probePosition = origin + new Vector3(spacing.X * x, spacing.Y * y, spacing.Z * z);
                    float score = Vector3.DistanceSquared(probePosition, dirtyCenter);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestProbeIndex = probeIndex;
                    }
                }
            }

            if (bestProbeIndex < 0)
                return false;

            _scheduledUpdateStartProbeIndex = CalculateProbeUpdateStartForDirtyProbe(
                _activeProbeCount,
                updateCount,
                bestProbeIndex);
            _updateCursor = (_scheduledUpdateStartProbeIndex + updateCount) % _activeProbeCount;
            return true;
        }

        internal static int CalculateProbeUpdateStartForDirtyProbe(int activeProbeCount, int updateCount, int dirtyProbeIndex)
        {
            if (activeProbeCount <= 0 || updateCount <= 0)
                return 0;

            int clampedUpdateCount = Math.Min(updateCount, activeProbeCount);
            int clampedProbeIndex = Math.Clamp(dirtyProbeIndex, 0, activeProbeCount - 1);
            int maxStart = Math.Max(0, activeProbeCount - clampedUpdateCount);
            return Math.Clamp(clampedProbeIndex - clampedUpdateCount / 2, 0, maxStart);
        }

        private static Vector3 ReadVector3(Vector4 value) => new(value.X, value.Y, value.Z);

        private void EnsureProbeStateCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinProbeStateBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeStateStride));

            EnsureStorageBuffer(ref _probeStateBuffer, ref _probeStateBufferSize, requiredSize, BindlessIndex.DdgiProbeStateBuffer, "DDGI Probe State Buffer");
        }

        private void EnsureProbeUpdateQueueCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride));

            EnsureStorageBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBufferSize, requiredSize, BindlessIndex.DdgiProbeUpdateQueueBuffer, "DDGI Probe Update Queue Buffer");
        }

        private void EnsureProbeRelocationClassificationCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride));

            EnsureStorageBuffer(
                ref _probeRelocationClassificationBuffer,
                ref _probeRelocationClassificationBufferSize,
                requiredSize,
                BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                "DDGI Probe Relocation Classification Buffer");
        }

        private void EnsureAtlasCapacity(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            EnsureStorageBuffer(
                ref handle,
                ref currentSize,
                Math.Max(MinResourceBufferSize, requiredSize),
                bindlessIndex,
                debugName);
        }

        private void EnsureStorageBuffer(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            if (handle.IsValid && currentSize >= requiredSize)
                return;

            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);

            currentSize = requiredSize;
            handle = _bufferManager.CreateDeviceBuffer(
                currentSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                debugName);
            _registeredBindlessHeap?.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(handle),
                0,
                currentSize);
        }

        private void RegisterIfValid(int bindlessIndex, BufferHandle handle, ulong size)
        {
            if (!handle.IsValid || _registeredBindlessHeap == null)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(handle),
                0,
                size);
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
            if (_visibilityAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_visibilityAtlasBuffer);
            if (_irradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_irradianceAtlasBuffer);
            if (_probeRelocationClassificationBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeRelocationClassificationBuffer);
            if (_probeUpdateQueueBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeUpdateQueueBuffer);
            if (_probeStateBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeStateBuffer);
            if (_volumeMetadataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_volumeMetadataBuffer);
        }
    }
}
