using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

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
        private const uint ResourceSignatureLayoutVersion = 2;
        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly GPUDdgiProbeVolume[] _volumeScratch = new GPUDdgiProbeVolume[AbsoluteMaxVolumeCount];
        private readonly GPUDdgiProbeUpdateRequest[] _probeUpdateRequestScratch = new GPUDdgiProbeUpdateRequest[AbsoluteMaxProbeCount];
        private readonly byte[] _probeUpdateRequestMarks = new byte[AbsoluteMaxProbeCount];

        private BufferHandle _volumeMetadataBuffer;
        private BufferHandle _probeStateBuffer;
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _probeRelocationClassificationBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BufferHandle _recursiveProbeStateBuffer;
        private BufferHandle _recursiveIrradianceAtlasBuffer;
        private BufferHandle _recursiveVisibilityAtlasBuffer;
        private BindlessHeap? _registeredBindlessHeap;
        private ulong _probeStateBufferSize;
        private ulong _probeUpdateQueueBufferSize;
        private ulong _probeRelocationClassificationBufferSize;
        private ulong _irradianceAtlasBufferSize;
        private ulong _visibilityAtlasBufferSize;
        private ulong _recursiveProbeStateBufferSize;
        private ulong _recursiveIrradianceAtlasBufferSize;
        private ulong _recursiveVisibilityAtlasBufferSize;
        private int _volumeCount;
        private int _probeCount;
        private int _activeProbeCount;
        private int _raysPerProbe;
        private int _maxProbeUpdatesPerFrame;
        private int _lastProbeUpdateRequestBudget;
        private int _updateCursor;
        private int _scheduledUpdateStartProbeIndex;
        private int _scheduledProbeUpdateCount;
        private int _lastNewProbeUpdateCount;
        private int _lastFrustumProbeUpdateCount;
        private int _lastOutsideFrustumProbeUpdateCount;
        private int _lastDirtyBoundsProbeUpdateCount;
        private int _lastAgeRefreshProbeUpdateCount;
        private int _lastResourceReinitializationCount;
        private int _totalResourceReinitializationCount;
        private ulong _textureBytes;
        private long _lastUploadMicroseconds;
        private long _lastGpuUpdateMicroseconds;
        private ulong _lastResourceSignature;
        private bool _hasResourceSignature;
        private bool _wasDdgiEnabled;
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
            EnsureRecursiveProbeStateCapacity(0);
            EnsureAtlasCapacity(
                ref _recursiveIrradianceAtlasBuffer,
                ref _recursiveIrradianceAtlasBufferSize,
                0,
                BindlessIndex.DdgiRecursiveIrradianceAtlasBuffer,
                "DDGI Recursive Irradiance Atlas Buffer");
            EnsureAtlasCapacity(
                ref _recursiveVisibilityAtlasBuffer,
                ref _recursiveVisibilityAtlasBufferSize,
                0,
                BindlessIndex.DdgiRecursiveVisibilityAtlasBuffer,
                "DDGI Recursive Visibility Atlas Buffer");
        }

        public int VolumeCount => _volumeCount;
        public int ProbeCount => _probeCount;
        public int ActiveProbeCount => _activeProbeCount;
        public int RaysPerProbe => _raysPerProbe;
        public int MaxProbeUpdatesPerFrame => _maxProbeUpdatesPerFrame;
        public int LastProbeUpdateRequestBudget => _lastProbeUpdateRequestBudget;
        public int ScheduledUpdateStartProbeIndex => _scheduledUpdateStartProbeIndex;
        public int ScheduledProbeUpdateCount => _scheduledProbeUpdateCount;
        public int LastNewProbeUpdateCount => _lastNewProbeUpdateCount;
        public int LastFrustumProbeUpdateCount => _lastFrustumProbeUpdateCount;
        public int LastOutsideFrustumProbeUpdateCount => _lastOutsideFrustumProbeUpdateCount;
        public int LastDirtyBoundsProbeUpdateCount => _lastDirtyBoundsProbeUpdateCount;
        public int LastAgeRefreshProbeUpdateCount => _lastAgeRefreshProbeUpdateCount;
        public int LastResourceReinitializationCount => _lastResourceReinitializationCount;
        public int TotalResourceReinitializationCount => _totalResourceReinitializationCount;
        public ulong TextureBytes => _textureBytes;
        public ulong BufferBytes => VolumeMetadataBufferSize +
            _probeStateBufferSize +
            _probeUpdateQueueBufferSize +
            _probeRelocationClassificationBufferSize +
            _recursiveProbeStateBufferSize;
        public long LastUploadMicroseconds => _lastUploadMicroseconds;

        public void ReportCompletedGpuUpdateMicroseconds(long gpuUpdateMicroseconds)
        {
            _lastGpuUpdateMicroseconds = Math.Max(0, gpuUpdateMicroseconds);
        }

        public bool IsProbeScheduledForUpdate(int probeIndex)
        {
            return (uint)probeIndex < (uint)_activeProbeCount &&
                _probeUpdateRequestMarks[probeIndex] != 0;
        }

        public bool TryGetScheduledProbeUpdateFlags(int probeIndex, out uint flags, out uint priority)
        {
            if ((uint)probeIndex >= (uint)_activeProbeCount || _scheduledProbeUpdateCount <= 0)
            {
                flags = 0u;
                priority = 0u;
                return false;
            }

            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                if (request.ProbeIndex != (uint)probeIndex)
                    continue;

                flags = request.Flags;
                priority = request.Priority;
                return true;
            }

            flags = 0u;
            priority = 0u;
            return false;
        }

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
            RegisterIfValid(BindlessIndex.DdgiRecursiveProbeStateBuffer, _recursiveProbeStateBuffer, _recursiveProbeStateBufferSize);
            RegisterIfValid(BindlessIndex.DdgiRecursiveIrradianceAtlasBuffer, _recursiveIrradianceAtlasBuffer, _recursiveIrradianceAtlasBufferSize);
            RegisterIfValid(BindlessIndex.DdgiRecursiveVisibilityAtlasBuffer, _recursiveVisibilityAtlasBuffer, _recursiveVisibilityAtlasBufferSize);
        }

        public void Upload(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            Upload(authoredVolumes, null, stagingRing, commandBuffer);
        }

        public void Upload(
            DdgiFrameLayout layout,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            Upload(layout.Volumes, layout.VolumeMetadata, stagingRing, commandBuffer);
        }

        private void Upload(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata>? runtimeMetadata,
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
            _lastResourceReinitializationCount = 0;
            _volumeCount = GlobalIlluminationProbeVolumeData.BuildVolumes(
                authoredVolumes,
                _settings.GlobalIllumination,
                _volumeScratch.AsSpan(0, AbsoluteMaxVolumeCount),
                out _probeCount,
                out _activeProbeCount,
                out _raysPerProbe,
                out _maxProbeUpdatesPerFrame,
                runtimeMetadata);

            if (_probeCount > AbsoluteMaxProbeCount)
            {
                _probeCount = AbsoluteMaxProbeCount;
                _activeProbeCount = Math.Min(_activeProbeCount, AbsoluteMaxProbeCount);
            }

            bool resourcesRecreated = EnsureProbeStateCapacity(_probeCount);
            resourcesRecreated |= EnsureRecursiveProbeStateCapacity(_probeCount);
            ulong atlasBytes = GlobalIlluminationProbeVolumeData.EstimateTextureBytes(_activeProbeCount);
            _textureBytes = checked(atlasBytes * 2UL);
            resourcesRecreated |= EnsureProbeUpdateQueueCapacity(_probeCount);
            resourcesRecreated |= EnsureProbeRelocationClassificationCapacity(_probeCount);
            resourcesRecreated |= EnsureAtlasCapacity(ref _irradianceAtlasBuffer, ref _irradianceAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateIrradianceAtlasBytes(_activeProbeCount), BindlessIndex.DdgiIrradianceAtlasBuffer, "DDGI Irradiance Atlas Buffer");
            resourcesRecreated |= EnsureAtlasCapacity(ref _visibilityAtlasBuffer, ref _visibilityAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount), BindlessIndex.DdgiVisibilityAtlasBuffer, "DDGI Visibility Atlas Buffer");
            resourcesRecreated |= EnsureAtlasCapacity(ref _recursiveIrradianceAtlasBuffer, ref _recursiveIrradianceAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateIrradianceAtlasBytes(_activeProbeCount), BindlessIndex.DdgiRecursiveIrradianceAtlasBuffer, "DDGI Recursive Irradiance Atlas Buffer");
            resourcesRecreated |= EnsureAtlasCapacity(ref _recursiveVisibilityAtlasBuffer, ref _recursiveVisibilityAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount), BindlessIndex.DdgiRecursiveVisibilityAtlasBuffer, "DDGI Recursive Visibility Atlas Buffer");

            bool ddgiEnabled = _settings.GlobalIllumination.EffectiveUseDdgi && _activeProbeCount > 0;
            ulong resourceSignature = CreateResourceSignature(
                _volumeScratch.AsSpan(0, _volumeCount),
                _probeCount,
                _activeProbeCount,
                _raysPerProbe,
                _maxProbeUpdatesPerFrame,
                CreateProbeUpdateModeSignature(_settings.GlobalIllumination));
            bool shouldInitializeResources = ddgiEnabled &&
                (resourcesRecreated ||
                 !_wasDdgiEnabled ||
                 !_hasResourceSignature ||
                 resourceSignature != _lastResourceSignature);

            if (shouldInitializeResources)
            {
                InitializePersistentResources(stagingRing, commandBuffer, resourceSignature);
                _lastResourceReinitializationCount = 1;
                _totalResourceReinitializationCount++;
                _updateCursor = 0;
            }

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
            _wasDdgiEnabled = ddgiEnabled;
            if (!ddgiEnabled)
                _hasResourceSignature = false;
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        public int ScheduleProbeUpdates(bool enabled) => ScheduleProbeUpdates(enabled, (IReadOnlyList<BoundingBox>?)null);

        public int ScheduleProbeUpdates(bool enabled, IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            return ScheduleProbeUpdates(enabled, null, dirtyBounds);
        }

        public int ScheduleProbeUpdates(bool enabled, DdgiFrameLayout layout)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            return ScheduleProbeUpdates(enabled, layout, layout.DirtyBounds);
        }

        public int ScheduleProbeUpdates(bool enabled, DdgiFrameLayout layout, ulong frameIndex)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            int scheduled = ScheduleProbeUpdates(enabled, layout, layout.DirtyBounds);
            if (enabled && scheduled > 0)
                MarkScheduledClipmapCellsUpdated(layout, frameIndex);
            return scheduled;
        }

        private int ScheduleProbeUpdates(
            bool enabled,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            if (!enabled || _activeProbeCount <= 0 || _maxProbeUpdatesPerFrame <= 0)
            {
                _scheduledUpdateStartProbeIndex = 0;
                _scheduledProbeUpdateCount = 0;
                _lastProbeUpdateRequestBudget = 0;
                ResetScheduleDiagnostics();
                return 0;
            }

            if (_updateCursor >= _activeProbeCount)
                _updateCursor = 0;

            int requestBudget = DdgiProbeUpdateScheduler.CalculateTimeBudgetedRequestCount(
                _maxProbeUpdatesPerFrame,
                _activeProbeCount,
                _settings.GlobalIllumination.EffectiveDdgiProbeUpdateTimeBudgetMilliseconds,
                _lastGpuUpdateMicroseconds);
            _lastProbeUpdateRequestBudget = requestBudget;
            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                _volumeScratch.AsSpan(0, _volumeCount),
                layout,
                dirtyBounds,
                _activeProbeCount,
                requestBudget,
                _updateCursor,
                _settings.GlobalIllumination,
                _probeUpdateRequestScratch.AsSpan(0, Math.Min(requestBudget, _probeUpdateRequestScratch.Length)),
                _probeUpdateRequestMarks);

            _scheduledProbeUpdateCount = result.RequestCount;
            _scheduledUpdateStartProbeIndex = result.CompatibilityStartProbeIndex;
            _updateCursor = result.NextUpdateCursor;
            BuildScheduleDiagnostics();
            return _scheduledProbeUpdateCount;
        }

        private void ResetScheduleDiagnostics()
        {
            _lastNewProbeUpdateCount = 0;
            _lastFrustumProbeUpdateCount = 0;
            _lastOutsideFrustumProbeUpdateCount = 0;
            _lastDirtyBoundsProbeUpdateCount = 0;
            _lastAgeRefreshProbeUpdateCount = 0;
        }

        private void BuildScheduleDiagnostics()
        {
            ResetScheduleDiagnostics();
            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                uint flags = _probeUpdateRequestScratch[i].Flags;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0)
                    _lastNewProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag) != 0)
                    _lastFrustumProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag) != 0)
                    _lastOutsideFrustumProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag) != 0)
                    _lastDirtyBoundsProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag) != 0)
                    _lastAgeRefreshProbeUpdateCount++;
            }
        }

        private void MarkScheduledClipmapCellsUpdated(DdgiFrameLayout layout, ulong frameIndex)
        {
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> metadata = layout.VolumeMetadata;
            IReadOnlyList<DdgiClipmapCascadeState> cascades = layout.CameraRelativeCascades;
            if (metadata.Count == 0 || cascades.Count == 0)
                return;

            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                if ((request.Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag) == 0)
                    continue;
                if (request.VolumeIndex >= metadata.Count)
                    continue;

                DdgiProbeVolumeRuntimeMetadata volumeMetadata = metadata[checked((int)request.VolumeIndex)];
                if (volumeMetadata.Kind != DdgiProbeVolumeKind.CameraClipmap)
                    continue;

                for (int cascadeIndex = 0; cascadeIndex < cascades.Count; cascadeIndex++)
                {
                    DdgiClipmapCascadeState cascade = cascades[cascadeIndex];
                    if (cascade.CascadeIndex != volumeMetadata.CascadeIndex)
                        continue;

                    cascade.MarkLogicalCellUpdated(
                        new DdgiClipmapCell(request.LogicalCellX, request.LogicalCellY, request.LogicalCellZ),
                        frameIndex);
                    break;
                }
            }
        }

        public void UploadScheduledProbeUpdateQueue(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (_scheduledProbeUpdateCount <= 0)
                return;
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI probe update queue upload.", nameof(commandBuffer));

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _probeUpdateQueueBuffer,
                _probeUpdateRequestScratch.AsSpan(0, _scheduledProbeUpdateCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        }

        public void SnapshotRecursiveProbeData(CommandBuffer commandBuffer)
        {
            if (_activeProbeCount <= 0)
                return;
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI recursive snapshot copy.", nameof(commandBuffer));

            CopyRecursiveSnapshotBuffer(
                commandBuffer,
                _probeStateBuffer,
                _recursiveProbeStateBuffer,
                Math.Min(_probeStateBufferSize, _recursiveProbeStateBufferSize));
            CopyRecursiveSnapshotBuffer(
                commandBuffer,
                _irradianceAtlasBuffer,
                _recursiveIrradianceAtlasBuffer,
                Math.Min(_irradianceAtlasBufferSize, _recursiveIrradianceAtlasBufferSize));
            CopyRecursiveSnapshotBuffer(
                commandBuffer,
                _visibilityAtlasBuffer,
                _recursiveVisibilityAtlasBuffer,
                Math.Min(_visibilityAtlasBufferSize, _recursiveVisibilityAtlasBufferSize));
        }

        private void CopyRecursiveSnapshotBuffer(
            CommandBuffer commandBuffer,
            BufferHandle sourceHandle,
            BufferHandle destinationHandle,
            ulong byteCount)
        {
            if (!sourceHandle.IsValid || !destinationHandle.IsValid || byteCount == 0)
                return;

            VkBuffer source = _bufferManager.GetBuffer(sourceHandle);
            VkBuffer destination = _bufferManager.GetBuffer(destinationHandle);
            InsertShaderToTransferReadBarrier(commandBuffer, source, byteCount);

            var region = new BufferCopy
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = byteCount
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &region);

            InsertTransferReadToShaderBarrier(commandBuffer, source, byteCount);
            InsertTransferWriteToShaderReadBarrier(commandBuffer, destination, byteCount);
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

        private bool EnsureProbeStateCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinProbeStateBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeStateStride));

            return EnsureStorageBuffer(ref _probeStateBuffer, ref _probeStateBufferSize, requiredSize, BindlessIndex.DdgiProbeStateBuffer, "DDGI Probe State Buffer");
        }

        private bool EnsureRecursiveProbeStateCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinProbeStateBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeStateStride));

            return EnsureStorageBuffer(
                ref _recursiveProbeStateBuffer,
                ref _recursiveProbeStateBufferSize,
                requiredSize,
                BindlessIndex.DdgiRecursiveProbeStateBuffer,
                "DDGI Recursive Probe State Buffer");
        }

        private bool EnsureProbeUpdateQueueCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride));

            return EnsureStorageBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBufferSize, requiredSize, BindlessIndex.DdgiProbeUpdateQueueBuffer, "DDGI Probe Update Queue Buffer");
        }

        private bool EnsureProbeRelocationClassificationCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride));

            return EnsureStorageBuffer(
                ref _probeRelocationClassificationBuffer,
                ref _probeRelocationClassificationBufferSize,
                requiredSize,
                BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                "DDGI Probe Relocation Classification Buffer");
        }

        private bool EnsureAtlasCapacity(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            return EnsureStorageBuffer(
                ref handle,
                ref currentSize,
                Math.Max(MinResourceBufferSize, requiredSize),
                bindlessIndex,
                debugName);
        }

        private bool EnsureStorageBuffer(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            if (handle.IsValid && currentSize >= requiredSize)
                return false;

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
            return true;
        }

        private void InitializePersistentResources(StagingRing stagingRing, CommandBuffer commandBuffer, ulong resourceSignature)
        {
            ClearStorageBuffer(commandBuffer, _probeStateBuffer, _probeStateBufferSize);
            ClearStorageBuffer(commandBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBufferSize);
            ClearStorageBuffer(commandBuffer, _probeRelocationClassificationBuffer, _probeRelocationClassificationBufferSize);
            ClearStorageBuffer(commandBuffer, _irradianceAtlasBuffer, _irradianceAtlasBufferSize);
            ClearStorageBuffer(commandBuffer, _recursiveProbeStateBuffer, _recursiveProbeStateBufferSize);
            ClearStorageBuffer(commandBuffer, _recursiveIrradianceAtlasBuffer, _recursiveIrradianceAtlasBufferSize);
            ClearStorageBuffer(commandBuffer, _recursiveVisibilityAtlasBuffer, _recursiveVisibilityAtlasBufferSize);
            UploadInitializedVisibilityAtlas(stagingRing, commandBuffer);

            _lastResourceSignature = resourceSignature;
            _hasResourceSignature = true;
        }

        private void ClearStorageBuffer(CommandBuffer commandBuffer, BufferHandle handle, ulong size)
        {
            if (!handle.IsValid || size == 0)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _context.Api.CmdFillBuffer(commandBuffer, buffer, 0, size, 0u);
            InsertTransferToShaderBarrier(commandBuffer, buffer, size);
        }

        private void UploadInitializedVisibilityAtlas(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            ulong byteCount = GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount);
            if (byteCount == 0)
                return;

            GpuBufferUploader.UploadBytesToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _visibilityAtlasBuffer,
                byteCount,
                WriteVisibilityAtlasInitializationPayload,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        }

        private void WriteVisibilityAtlasInitializationPayload(void* destination, ulong byteCount)
        {
            Span<byte> bytes = new(destination, checked((int)byteCount));
            CreateVisibilityAtlasInitializationPayload(
                _volumeScratch.AsSpan(0, _volumeCount),
                _activeProbeCount,
                GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe,
                bytes);
        }

        private void InsertTransferToShaderBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong size)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = size
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void InsertShaderToTransferReadBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                size,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);
        }

        private void InsertTransferReadToShaderBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                size,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }

        private void InsertTransferWriteToShaderReadBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                size,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit);
        }

        private void InsertBufferBarrier(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            ulong size,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = sourceStage,
                SrcAccessMask = sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = size
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        internal static void CreateVisibilityAtlasInitializationPayload(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            uint visibilityTexelsPerProbe,
            Span<byte> destination)
        {
            if (activeProbeCount <= 0 || visibilityTexelsPerProbe == 0)
                return;

            int texelCount = checked((int)(visibilityTexelsPerProbe * visibilityTexelsPerProbe));
            int expectedBytes = checked(activeProbeCount * texelCount * sizeof(uint));
            if (destination.Length < expectedBytes)
                throw new ArgumentException("Destination is too small for the DDGI visibility atlas initialization payload.", nameof(destination));

            Span<uint> words = MemoryMarshal.Cast<byte, uint>(destination[..expectedBytes]);
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                int firstProbe = Math.Clamp((int)MathF.Round(volume.OriginAndFirstProbeIndex.W), 0, activeProbeCount);
                int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
                int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
                int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
                int probeCount = Math.Min(checked(countX * countY * countZ), activeProbeCount - firstProbe);
                if (probeCount <= 0)
                    continue;

                float maxDistance = MathF.Max(volume.BiasAndProbeCountZ.Z > 0.0f ? volume.BiasAndProbeCountZ.Z : 16.0f, 0.1f);
                uint packedMoments = PackHalf2(maxDistance, maxDistance * maxDistance);
                int probeEnd = firstProbe + probeCount;
                for (int probeIndex = firstProbe; probeIndex < probeEnd; probeIndex++)
                {
                    int baseWord = probeIndex * texelCount;
                    words.Slice(baseWord, texelCount).Fill(packedMoments);
                }
            }
        }

        internal static ulong CreateResourceSignature(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int probeCount,
            int activeProbeCount,
            int raysPerProbe,
            int maxProbeUpdatesPerFrame,
            uint probeUpdateModeFlags = 0u)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, ResourceSignatureLayoutVersion);
            hash = HashAdd(hash, volumes.Length);
            hash = HashAdd(hash, probeCount);
            hash = HashAdd(hash, activeProbeCount);
            hash = HashAdd(hash, raysPerProbe);
            hash = HashAdd(hash, probeUpdateModeFlags);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.IrradianceTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rgba16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rg16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeStateStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride);
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                hash = HashAdd(hash, i);
                hash = HashAdd(hash, volume.OriginAndFirstProbeIndex.W);
                hash = HashAdd(hash, volume.ClipmapGridMinAndKind.W);
                hash = HashAdd(hash, volume.ClipmapRingOffsetAndCascade.W);
                hash = HashAdd(hash, volume.SizeAndProbeCountX.W);
                hash = HashAdd(hash, volume.ProbeSpacingAndProbeCountY);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.X);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Y);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Z);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.W);
                hash = HashAdd(hash, volume.RayAndUpdateParams.X);
                hash = HashAdd(hash, volume.RayAndUpdateParams.Z);
                hash = HashAdd(hash, volume.RayAndUpdateParams.W);
                hash = HashAdd(hash, volume.DebugColorAndFlags.W);
            }

            return hash;
        }

        private static uint CreateProbeUpdateModeSignature(GlobalIlluminationSettings settings)
        {
            uint flags = 0u;
            if (settings.DdgiProbeRelocationEnabled)
                flags |= GlobalIlluminationProbeVolumeData.ProbeRelocationEnabledFlag;
            if (settings.DdgiProbeClassificationEnabled)
                flags |= GlobalIlluminationProbeVolumeData.ProbeClassificationEnabledFlag;
            return flags;
        }

        private static uint PackHalf2(float x, float y)
        {
            uint hx = BitConverter.HalfToUInt16Bits((Half)x);
            uint hy = BitConverter.HalfToUInt16Bits((Half)y);
            return hx | (hy << 16);
        }

        private static ulong HashAdd(ulong hash, Vector4 value)
        {
            hash = HashAdd(hash, value.X);
            hash = HashAdd(hash, value.Y);
            hash = HashAdd(hash, value.Z);
            return HashAdd(hash, value.W);
        }

        private static ulong HashAdd(ulong hash, int value) => HashAdd(hash, unchecked((uint)value));

        private static ulong HashAdd(ulong hash, ulong value)
        {
            hash = HashAdd(hash, unchecked((uint)value));
            return HashAdd(hash, unchecked((uint)(value >> 32)));
        }

        private static ulong HashAdd(ulong hash, float value) => HashAdd(hash, BitConverter.SingleToUInt32Bits(value));

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 8) & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 16) & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 24) & 0xff;
            hash *= HashPrime;
            return hash;
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
            if (_recursiveVisibilityAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_recursiveVisibilityAtlasBuffer);
            if (_recursiveIrradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_recursiveIrradianceAtlasBuffer);
            if (_recursiveProbeStateBuffer.IsValid)
                _bufferManager.DestroyBuffer(_recursiveProbeStateBuffer);
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
