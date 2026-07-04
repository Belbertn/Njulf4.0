using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class GlobalSdfManager : IDisposable
    {
        public const int BrickSize = 8;
        private static readonly float[] CascadeVoxelSizes = [0.125f, 0.25f, 0.5f, 1.0f];

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly GlobalSdfCascadeRuntime?[] _cascades = new GlobalSdfCascadeRuntime?[BindlessIndex.GlobalSdfTextureCount];
        private readonly GPUGlobalSdfCascade[] _cascadeScratch = new GPUGlobalSdfCascade[BindlessIndex.GlobalSdfTextureCount];
        private BufferHandle _cascadeBuffer;
        private int _resolution;
        private bool _disposed;

        public GlobalSdfManager(VulkanContext context, BufferManager bufferManager, BindlessHeap bindlessHeap)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            EnsureCascadeBuffer();
        }

        public int CascadeCount => _cascades.Length;
        public int Resolution => _resolution;
        public ulong TextureBytes { get; private set; }
        public int LastFrameBricksUpdated { get; private set; }
        public int LastFrameCascadeCount { get; private set; }
        public int LastFrameResolution { get; private set; }

        public IReadOnlyList<GlobalSdfUpdateJob> PrepareUpdateJobs(
            Vector3 cameraPosition,
            int requestedResolution,
            int brickBudget,
            DdgiFrameLayout? ddgiLayout = null)
        {
            int resolution = AlignResolutionToBrickSize(requestedResolution);
            EnsureResources(resolution);
            UpdateCascadeClipmaps(cameraPosition);
            ApplyDdgiEvents(ddgiLayout);
            BuildCascadeMetadata();

            LastFrameBricksUpdated = 0;
            LastFrameCascadeCount = _cascades.Length;
            LastFrameResolution = resolution;

            if (brickBudget <= 0)
                return Array.Empty<GlobalSdfUpdateJob>();

            int remaining = brickBudget;
            var jobs = new List<GlobalSdfUpdateJob>(_cascades.Length * 4);
            for (int i = 0; i < _cascades.Length && remaining > 0; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                SelectDirtyBrickJobs(i, cascade, jobs, ref remaining);
            }

            return jobs;
        }

        private void EnsureResources(int resolution)
        {
            if (_resolution == resolution && _cascades[0]?.Volume != null)
                return;

            DestroyVolumes();
            _resolution = resolution;
            TextureBytes = 0;
            var extent = new Extent3D { Width = (uint)resolution, Height = (uint)resolution, Depth = (uint)resolution };

            for (int i = 0; i < _cascades.Length; i++)
            {
                var volume = new VolumeTexture(
                    _context,
                    $"Global SDF Cascade {i}",
                    Format.R16Sfloat,
                    extent,
                    new VolumeTextureDescriptor(
                        sampled: true,
                        storage: true,
                        transferSource: true,
                        transferDestination: true,
                        generateFullMipChain: true));
                int bindlessIndex = BindlessIndex.GlobalSdfTextureBase + i;
                _bindlessHeap.RegisterStorageImage(bindlessIndex, volume.StorageView, ImageLayout.General);
                _bindlessHeap.RegisterTexture(bindlessIndex, volume.View, imageLayout: ImageLayout.ShaderReadOnlyOptimal);
                _cascades[i] = new GlobalSdfCascadeRuntime(volume, CascadeVoxelSizes[i], resolution);
                TextureBytes += volume.EstimatedByteSize;
            }
        }

        private static int AlignResolutionToBrickSize(int requestedResolution)
        {
            int clamped = Math.Clamp(requestedResolution, 32, 512);
            int aligned = ((clamped + BrickSize - 1) / BrickSize) * BrickSize;
            return Math.Clamp(aligned, 32, 512);
        }

        private void EnsureCascadeBuffer()
        {
            if (_cascadeBuffer.IsValid)
                return;

            ulong bufferSize = checked((ulong)BindlessIndex.GlobalSdfTextureCount * (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUGlobalSdfCascade>());
            _cascadeBuffer = _bufferManager.CreateBuffer(
                bufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferDevice,
                default,
                "Global SDF Cascade Metadata Buffer",
                MemoryBudgetCategory.RenderTargets);
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.GlobalSdfCascadeBuffer, _bufferManager.GetBuffer(_cascadeBuffer), 0, bufferSize);
        }

        public void UploadCascadeMetadata(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for global SDF metadata upload.", nameof(commandBuffer));

            EnsureCascadeBuffer();
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _cascadeBuffer,
                _cascadeScratch.AsSpan(0, _cascades.Length),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private void BuildCascadeMetadata()
        {
            EnsureCascadeBuffer();
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                _cascadeScratch[i] = new GPUGlobalSdfCascade
                {
                    WorldMinAndVoxelSize = new Vector4(cascade.WorldMin.X, cascade.WorldMin.Y, cascade.WorldMin.Z, cascade.VoxelSize),
                    WorldExtentAndInvVoxelSize = new Vector4(cascade.WorldExtent.X, cascade.WorldExtent.Y, cascade.WorldExtent.Z, 1.0f / Math.Max(cascade.VoxelSize, 0.0001f)),
                    TextureIndex = checked((uint)(BindlessIndex.GlobalSdfTextureBase + i)),
                    Resolution = checked((uint)_resolution),
                    MipCount = cascade.Volume.MipLevels,
                    Flags = 0,
                    LogicalGridMinX = cascade.LogicalGridMinCell.X,
                    LogicalGridMinY = cascade.LogicalGridMinCell.Y,
                    LogicalGridMinZ = cascade.LogicalGridMinCell.Z,
                    RingOffsetX = cascade.RingOffset.X,
                    RingOffsetY = cascade.RingOffset.Y,
                    RingOffsetZ = cascade.RingOffset.Z,
                    BricksPerAxis = checked((uint)cascade.BricksPerAxis),
                    Padding0 = 0
                };
            }
        }

        private void UpdateCascadeClipmaps(Vector3 cameraPosition)
        {
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                cascade.UpdateClipmap(cameraPosition, _resolution);
            }
        }

        private void ApplyDdgiEvents(DdgiFrameLayout? ddgiLayout)
        {
            if (ddgiLayout == null || !ddgiLayout.IsDdgiActive)
                return;

            if (ddgiLayout.MovementClass is DdgiCameraMovementClass.LayoutChanged or
                DdgiCameraMovementClass.FirstActivation or
                DdgiCameraMovementClass.Teleport)
            {
                MarkAllCascadesDirty();
            }
            else if (ddgiLayout.FastCameraMovement && _cascades[0] != null)
            {
                _cascades[0]!.MarkAllDirty();
            }

            for (int i = 0; i < ddgiLayout.DirtyProbeRequests.Count; i++)
                MarkDirtyProbeRequest(ddgiLayout, ddgiLayout.DirtyProbeRequests[i]);

            for (int i = 0; i < ddgiLayout.DirtyRegions.Count; i++)
                MarkDirtyWorldBounds(ddgiLayout.DirtyRegions[i].Bounds);
        }

        private void MarkDirtyProbeRequest(DdgiFrameLayout ddgiLayout, DdgiFrameLayoutDirtyProbeRequest request)
        {
            if ((uint)request.VolumeIndex >= (uint)ddgiLayout.Volumes.Count)
                return;

            GlobalIlluminationProbeVolume volume = ddgiLayout.Volumes[request.VolumeIndex];
            Vector3 spacing = volume.ProbeSpacing;
            if (!IsPositiveFinite(spacing.X) || !IsPositiveFinite(spacing.Y) || !IsPositiveFinite(spacing.Z))
                return;

            Vector3 min = new(
                MathF.Min(request.MinCell.X, request.MaxCell.X) * spacing.X,
                MathF.Min(request.MinCell.Y, request.MaxCell.Y) * spacing.Y,
                MathF.Min(request.MinCell.Z, request.MaxCell.Z) * spacing.Z);
            Vector3 max = new(
                (MathF.Max(request.MinCell.X, request.MaxCell.X) + 1.0f) * spacing.X,
                (MathF.Max(request.MinCell.Y, request.MaxCell.Y) + 1.0f) * spacing.Y,
                (MathF.Max(request.MinCell.Z, request.MaxCell.Z) + 1.0f) * spacing.Z);
            MarkDirtyWorldBounds(new BoundingBox(Vector3.Min(min, max), Vector3.Max(min, max)));
        }

        private void MarkDirtyWorldBounds(BoundingBox bounds)
        {
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime? cascade = _cascades[i];
                if (cascade == null || !cascade.Intersects(bounds))
                    continue;

                cascade.MarkWorldBoundsDirty(bounds);
            }
        }

        private void MarkAllCascadesDirty()
        {
            for (int i = 0; i < _cascades.Length; i++)
                _cascades[i]?.MarkAllDirty();
        }

        private void SelectDirtyBrickJobs(
            int cascadeIndex,
            GlobalSdfCascadeRuntime cascade,
            List<GlobalSdfUpdateJob> jobs,
            ref int remaining)
        {
            while (remaining > 0)
            {
                int start = cascade.FindNextDirtyBrick();
                if (start < 0)
                    break;

                int count = cascade.ConsumeDirtyRun(start, remaining);
                AddJob(cascadeIndex, cascade, jobs, start, count);
                remaining -= count;
                LastFrameBricksUpdated += count;
            }

            if (remaining <= 0 || cascade.HasDirtyBricks)
                return;

            int refreshCount = Math.Min(remaining, cascade.TotalBricks);
            while (refreshCount > 0 && remaining > 0)
            {
                int start = cascade.NextRefreshBrickIndex;
                int count = Math.Min(refreshCount, cascade.TotalBricks - start);
                AddJob(cascadeIndex, cascade, jobs, start, count);
                cascade.NextRefreshBrickIndex = (start + count) % cascade.TotalBricks;
                remaining -= count;
                refreshCount -= count;
                LastFrameBricksUpdated += count;
            }
        }

        private void AddJob(
            int cascadeIndex,
            GlobalSdfCascadeRuntime cascade,
            List<GlobalSdfUpdateJob> jobs,
            int brickStartIndex,
            int brickCount)
        {
            if (brickCount <= 0)
                return;

            jobs.Add(new GlobalSdfUpdateJob(
                cascadeIndex,
                BindlessIndex.GlobalSdfTextureBase + cascadeIndex,
                cascade.Volume,
                cascade.WorldMin,
                cascade.WorldExtent,
                cascade.VoxelSize,
                _resolution,
                cascade.BricksPerAxis,
                brickStartIndex,
                brickCount,
                cascade.LogicalGridMinCell,
                cascade.RingOffset));
        }

        private static bool IsPositiveFinite(float value) => float.IsFinite(value) && value > 0.0f;

        private void DestroyVolumes()
        {
            for (int i = 0; i < _cascades.Length; i++)
            {
                _cascades[i]?.Volume.Dispose();
                _cascades[i] = default;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyVolumes();
            if (_cascadeBuffer.IsValid)
            {
                _bufferManager.DestroyBuffer(_cascadeBuffer);
                _cascadeBuffer = BufferHandle.Invalid;
            }
            GC.SuppressFinalize(this);
        }

        private sealed class GlobalSdfCascadeRuntime
        {
            public GlobalSdfCascadeRuntime(VolumeTexture volume, float voxelSize, int resolution)
            {
                Volume = volume;
                VoxelSize = voxelSize;
                BricksPerAxis = Math.Max(1, (resolution + BrickSize - 1) / BrickSize);
                TotalBricks = checked(BricksPerAxis * BricksPerAxis * BricksPerAxis);
                _dirtyBricks = new bool[TotalBricks];
                MarkAllDirty();
            }

            public VolumeTexture Volume { get; }
            public float VoxelSize { get; }
            public int BricksPerAxis { get; }
            public int TotalBricks { get; }
            public int NextRefreshBrickIndex { get; set; }
            public Vector3 WorldMin { get; set; }
            public Vector3 WorldExtent { get; set; }
            public DdgiClipmapCell LogicalGridMinCell { get; private set; }
            public DdgiClipmapCell RingOffset { get; private set; }
            public bool HasDirtyBricks { get; private set; }

            private readonly bool[] _dirtyBricks;
            private int _dirtyScanIndex;
            private bool _initialized;

            public void UpdateClipmap(Vector3 cameraPosition, int resolution)
            {
                float brickWorldSize = BrickWorldSize;
                DdgiClipmapCell nextGridMin = CameraRelativeDdgiClipmapController.CalculateCenteredGridMinimum(
                    cameraPosition,
                    brickWorldSize,
                    BricksPerAxis,
                    BricksPerAxis,
                    BricksPerAxis);
                WorldExtent = new Vector3(VoxelSize * Math.Max(1, resolution));

                if (!_initialized)
                {
                    LogicalGridMinCell = nextGridMin;
                    RingOffset = DdgiClipmapCell.Zero;
                    _initialized = true;
                    MarkAllDirty();
                    UpdateWorldMin();
                    return;
                }

                DdgiClipmapCell delta = CameraRelativeDdgiClipmapController.SubtractSaturating(nextGridMin, LogicalGridMinCell);
                if (delta == DdgiClipmapCell.Zero)
                {
                    UpdateWorldMin();
                    return;
                }

                LogicalGridMinCell = nextGridMin;
                RingOffset = new DdgiClipmapCell(
                    DdgiClipmapAddressing.PositiveModulo((long)RingOffset.X + delta.X, BricksPerAxis),
                    DdgiClipmapAddressing.PositiveModulo((long)RingOffset.Y + delta.Y, BricksPerAxis),
                    DdgiClipmapAddressing.PositiveModulo((long)RingOffset.Z + delta.Z, BricksPerAxis));

                if (AbsLong(delta.X) >= BricksPerAxis ||
                    AbsLong(delta.Y) >= BricksPerAxis ||
                    AbsLong(delta.Z) >= BricksPerAxis)
                {
                    MarkAllDirty();
                }
                else
                {
                    InvalidateMovedAxisSlab(delta.X, BricksPerAxis, Axis.X);
                    InvalidateMovedAxisSlab(delta.Y, BricksPerAxis, Axis.Y);
                    InvalidateMovedAxisSlab(delta.Z, BricksPerAxis, Axis.Z);
                }

                UpdateWorldMin();
            }

            public bool Intersects(BoundingBox bounds)
            {
                BoundingBox cascadeBounds = new(WorldMin, WorldMin + WorldExtent);
                return cascadeBounds.Intersects(bounds);
            }

            public void MarkWorldBoundsDirty(BoundingBox bounds)
            {
                float brickWorldSize = BrickWorldSize;
                DdgiClipmapCell min = ClampWorldToLogicalCell(bounds.Min, brickWorldSize);
                DdgiClipmapCell max = ClampWorldToLogicalCell(bounds.Max, brickWorldSize);
                MarkLogicalRegionDirty(
                    new DdgiClipmapCell(
                        Math.Min(min.X, max.X),
                        Math.Min(min.Y, max.Y),
                        Math.Min(min.Z, max.Z)),
                    new DdgiClipmapCell(
                        Math.Max(min.X, max.X),
                        Math.Max(min.Y, max.Y),
                        Math.Max(min.Z, max.Z)));
            }

            public void MarkAllDirty()
            {
                Array.Fill(_dirtyBricks, true);
                HasDirtyBricks = _dirtyBricks.Length > 0;
                _dirtyScanIndex = 0;
            }

            public int FindNextDirtyBrick()
            {
                if (!HasDirtyBricks)
                    return -1;

                for (int i = 0; i < _dirtyBricks.Length; i++)
                {
                    int index = (_dirtyScanIndex + i) % _dirtyBricks.Length;
                    if (_dirtyBricks[index])
                    {
                        _dirtyScanIndex = index;
                        return index;
                    }
                }

                HasDirtyBricks = false;
                return -1;
            }

            public int ConsumeDirtyRun(int start, int maxCount)
            {
                int count = 0;
                int limit = Math.Min(_dirtyBricks.Length, start + Math.Max(0, maxCount));
                for (int i = start; i < limit && _dirtyBricks[i]; i++)
                {
                    _dirtyBricks[i] = false;
                    count++;
                }

                _dirtyScanIndex = (start + count) % _dirtyBricks.Length;
                HasDirtyBricks = ContainsDirtyBrick();
                return count;
            }

            private void InvalidateMovedAxisSlab(int delta, int brickCount, Axis axis)
            {
                if (delta == 0)
                    return;

                int slabSize = Math.Abs(delta);
                int start = delta > 0 ? brickCount - slabSize : 0;
                int end = delta > 0 ? brickCount - 1 : slabSize - 1;

                DdgiClipmapCell min = LogicalGridMinCell;
                DdgiClipmapCell max = new(
                    LogicalGridMinCell.X + BricksPerAxis - 1,
                    LogicalGridMinCell.Y + BricksPerAxis - 1,
                    LogicalGridMinCell.Z + BricksPerAxis - 1);

                switch (axis)
                {
                    case Axis.X:
                        min = min with { X = LogicalGridMinCell.X + start };
                        max = max with { X = LogicalGridMinCell.X + end };
                        break;
                    case Axis.Y:
                        min = min with { Y = LogicalGridMinCell.Y + start };
                        max = max with { Y = LogicalGridMinCell.Y + end };
                        break;
                    case Axis.Z:
                        min = min with { Z = LogicalGridMinCell.Z + start };
                        max = max with { Z = LogicalGridMinCell.Z + end };
                        break;
                }

                MarkLogicalRegionDirty(min, max);
            }

            private void MarkLogicalRegionDirty(DdgiClipmapCell min, DdgiClipmapCell max)
            {
                DdgiClipmapCell clampedMin = new(
                    Math.Clamp(min.X, LogicalGridMinCell.X, LogicalGridMinCell.X + BricksPerAxis - 1),
                    Math.Clamp(min.Y, LogicalGridMinCell.Y, LogicalGridMinCell.Y + BricksPerAxis - 1),
                    Math.Clamp(min.Z, LogicalGridMinCell.Z, LogicalGridMinCell.Z + BricksPerAxis - 1));
                DdgiClipmapCell clampedMax = new(
                    Math.Clamp(max.X, LogicalGridMinCell.X, LogicalGridMinCell.X + BricksPerAxis - 1),
                    Math.Clamp(max.Y, LogicalGridMinCell.Y, LogicalGridMinCell.Y + BricksPerAxis - 1),
                    Math.Clamp(max.Z, LogicalGridMinCell.Z, LogicalGridMinCell.Z + BricksPerAxis - 1));

                for (int z = clampedMin.Z; z <= clampedMax.Z; z++)
                {
                    for (int y = clampedMin.Y; y <= clampedMax.Y; y++)
                    {
                        for (int x = clampedMin.X; x <= clampedMax.X; x++)
                        {
                            int physical = DdgiClipmapAddressing.CalculateLocalPhysicalProbeIndex(
                                new DdgiClipmapCell(x, y, z),
                                LogicalGridMinCell,
                                RingOffset,
                                BricksPerAxis,
                                BricksPerAxis,
                                BricksPerAxis);
                            _dirtyBricks[physical] = true;
                            HasDirtyBricks = true;
                        }
                    }
                }
            }

            private DdgiClipmapCell ClampWorldToLogicalCell(Vector3 worldPosition, float brickWorldSize)
            {
                return new DdgiClipmapCell(
                    Math.Clamp(FloorToCell(worldPosition.X, brickWorldSize), LogicalGridMinCell.X, LogicalGridMinCell.X + BricksPerAxis - 1),
                    Math.Clamp(FloorToCell(worldPosition.Y, brickWorldSize), LogicalGridMinCell.Y, LogicalGridMinCell.Y + BricksPerAxis - 1),
                    Math.Clamp(FloorToCell(worldPosition.Z, brickWorldSize), LogicalGridMinCell.Z, LogicalGridMinCell.Z + BricksPerAxis - 1));
            }

            private void UpdateWorldMin()
            {
                float brickWorldSize = BrickWorldSize;
                WorldMin = new Vector3(
                    CellToWorld(LogicalGridMinCell.X, brickWorldSize),
                    CellToWorld(LogicalGridMinCell.Y, brickWorldSize),
                    CellToWorld(LogicalGridMinCell.Z, brickWorldSize));
            }

            private float BrickWorldSize => VoxelSize * BrickSize;

            private bool ContainsDirtyBrick()
            {
                for (int i = 0; i < _dirtyBricks.Length; i++)
                {
                    if (_dirtyBricks[i])
                        return true;
                }

                return false;
            }

            private static int FloorToCell(float value, float spacing)
            {
                if (!float.IsFinite(value) || !float.IsFinite(spacing) || spacing <= 0.0f)
                    return 0;

                double cell = Math.Floor(value / spacing);
                if (cell <= int.MinValue)
                    return int.MinValue;
                if (cell >= int.MaxValue)
                    return int.MaxValue;

                return (int)cell;
            }

            private static float CellToWorld(int cell, float spacing)
            {
                double world = (double)cell * spacing;
                if (world <= -float.MaxValue)
                    return -float.MaxValue;
                if (world >= float.MaxValue)
                    return float.MaxValue;

                return (float)world;
            }

            private static long AbsLong(int value)
            {
                return value == int.MinValue ? (long)int.MaxValue + 1L : Math.Abs(value);
            }

            private enum Axis
            {
                X,
                Y,
                Z
            }
        }
    }

    public sealed record GlobalSdfUpdateJob(
        int CascadeIndex,
        int TextureIndex,
        VolumeTexture Volume,
        Vector3 WorldMin,
        Vector3 WorldExtent,
        float VoxelSize,
        int Resolution,
        int BricksPerAxis,
        int BrickStartIndex,
        int BrickCount,
        DdgiClipmapCell LogicalGridMinCell,
        DdgiClipmapCell RingOffset);
}
