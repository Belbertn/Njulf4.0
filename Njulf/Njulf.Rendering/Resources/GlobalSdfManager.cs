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
        public const int BacklogBrickUpdateBudgetFloor = 1024;
        private static readonly float[] CascadeVoxelSizes = [0.125f, 0.25f, 0.5f, 1.0f];
        private static readonly int[] CascadeBrickBudgetWeights = [4, 3, 2, 1];

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly GlobalSdfCascadeRuntime?[] _cascades = new GlobalSdfCascadeRuntime?[BindlessIndex.GlobalSdfTextureCount];
        private readonly GPUGlobalSdfCascade[] _cascadeScratch = new GPUGlobalSdfCascade[BindlessIndex.GlobalSdfTextureCount];
        private readonly List<IdleRefreshCandidate> _idleRefreshCandidateScratch = new();
        private readonly List<int> _idleRefreshBrickScratch = new();
        private BufferHandle _cascadeBuffer;
        private BufferHandle _candidateHistoryBuffer;
        private int _candidateHistoryCapacityWords;
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
        public int LastFramePriorityBricksUpdated { get; private set; }
        public int LastFrameDirtyBricksUpdated { get; private set; }
        public int LastFrameIdleRefreshBricksUpdated { get; private set; }
        public int LastFrameDirtyBrickBacklog { get; private set; }
        public int LastFrameBrickUpdateBudget { get; private set; }
        public int LastFrameCascadeCount { get; private set; }
        public int LastFrameResolution { get; private set; }
        public int LastFrameScrollDeltaCells { get; private set; }
        public int LastFrameCascade0ScrollDeltaCells { get; private set; }
        public int LastFrameScrollInvalidatedBricks { get; private set; }
        public int LastFrameCascade0ScrollInvalidatedBricks { get; private set; }

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
            LastFramePriorityBricksUpdated = 0;
            LastFrameDirtyBricksUpdated = 0;
            LastFrameIdleRefreshBricksUpdated = 0;
            LastFrameDirtyBrickBacklog = 0;
            LastFrameBrickUpdateBudget = 0;
            LastFrameCascadeCount = _cascades.Length;
            LastFrameResolution = resolution;
            LastFrameScrollDeltaCells = 0;
            LastFrameCascade0ScrollDeltaCells = 0;
            LastFrameScrollInvalidatedBricks = 0;
            LastFrameCascade0ScrollInvalidatedBricks = 0;

            Span<int> cascadeDirtyBacklogs = stackalloc int[BindlessIndex.GlobalSdfTextureCount];
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime? cascade = _cascades[i];
                if (cascade == null)
                    continue;

                cascadeDirtyBacklogs[i] = cascade.DirtyBrickCount;
                LastFrameDirtyBrickBacklog += cascadeDirtyBacklogs[i];
                LastFrameScrollDeltaCells += cascade.LastScrollDeltaCells;
                LastFrameScrollInvalidatedBricks += cascade.LastScrollInvalidatedBricks;
                if (i == 0)
                {
                    LastFrameCascade0ScrollDeltaCells = cascade.LastScrollDeltaCells;
                    LastFrameCascade0ScrollInvalidatedBricks = cascade.LastScrollInvalidatedBricks;
                }
            }

            int effectiveBrickBudget = CalculateEffectiveBrickUpdateBudget(brickBudget, LastFrameDirtyBrickBacklog);
            LastFrameBrickUpdateBudget = effectiveBrickBudget;
            if (effectiveBrickBudget <= 0)
                return Array.Empty<GlobalSdfUpdateJob>();

            var jobs = new List<GlobalSdfUpdateJob>(_cascades.Length * 4);
            Span<int> cascadeBudgets = stackalloc int[BindlessIndex.GlobalSdfTextureCount];
            CalculateCascadeBrickBudgets(effectiveBrickBudget, cascadeDirtyBacklogs[.._cascades.Length], cascadeBudgets);
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                int cascadeBudget = cascadeBudgets[i];
                if (cascadeBudget > 0)
                    SelectDirtyBrickJobs(i, cascade, jobs, cascadeBudget, cameraPosition);
            }

            return jobs;
        }

        internal static int CalculateEffectiveBrickUpdateBudget(int requestedBudget, int dirtyBrickBacklog)
        {
            if (requestedBudget <= 0)
                return 0;

            return dirtyBrickBacklog > 0
                ? Math.Max(requestedBudget, BacklogBrickUpdateBudgetFloor)
                : requestedBudget;
        }

        internal static void CalculateCascadeBrickBudgets(int brickBudget, int cascadeCount, Span<int> destination)
        {
            int count = Math.Clamp(cascadeCount, 0, Math.Min(destination.Length, CascadeBrickBudgetWeights.Length));
            destination.Clear();
            if (brickBudget <= 0 || count <= 0)
                return;

            int totalWeight = 0;
            for (int i = 0; i < count; i++)
                totalWeight += CascadeBrickBudgetWeights[i];

            int assigned = 0;
            Span<int> remainders = stackalloc int[CascadeBrickBudgetWeights.Length];
            for (int i = 0; i < count; i++)
            {
                int weightedBudget = brickBudget * CascadeBrickBudgetWeights[i];
                int quota = weightedBudget / totalWeight;
                destination[i] = quota;
                remainders[i] = weightedBudget - quota * totalWeight;
                assigned += quota;
            }

            int unassigned = brickBudget - assigned;
            while (unassigned > 0)
            {
                int bestIndex = 0;
                int bestRemainder = int.MinValue;
                for (int i = 0; i < count; i++)
                {
                    if (remainders[i] > bestRemainder)
                    {
                        bestIndex = i;
                        bestRemainder = remainders[i];
                    }
                }

                destination[bestIndex]++;
                remainders[bestIndex] = int.MinValue;
                unassigned--;
            }

            if (brickBudget >= count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (destination[i] > 0)
                        continue;

                    int donor = FindLargestCascadeBudget(destination[..count], exceptIndex: i);
                    if (donor < 0 || destination[donor] <= 1)
                        break;

                    destination[donor]--;
                    destination[i]++;
                }
            }

            int cascade0Cap = Math.Max(1, (brickBudget + 1) / 2);
            if (count > 1 && destination[0] > cascade0Cap)
            {
                int excess = destination[0] - cascade0Cap;
                destination[0] = cascade0Cap;
                int receiver = 1;
                while (excess > 0)
                {
                    destination[receiver]++;
                    receiver++;
                    if (receiver >= count)
                        receiver = 1;
                    excess--;
                }
            }
        }

        internal static void CalculateCascadeBrickBudgets(int brickBudget, ReadOnlySpan<int> dirtyBacklogs, Span<int> destination)
        {
            int count = Math.Clamp(dirtyBacklogs.Length, 0, Math.Min(destination.Length, CascadeBrickBudgetWeights.Length));
            CalculateCascadeBrickBudgets(brickBudget, count, destination);
            if (brickBudget <= 0 || count <= 0)
                return;

            bool hasDirtyBacklog = false;
            for (int i = 0; i < count; i++)
            {
                if (dirtyBacklogs[i] > 0)
                {
                    hasDirtyBacklog = true;
                    break;
                }
            }

            if (!hasDirtyBacklog)
                return;

            int redistributable = 0;
            for (int i = 0; i < count; i++)
            {
                int backlog = Math.Max(0, dirtyBacklogs[i]);
                if (backlog == 0)
                {
                    redistributable += destination[i];
                    destination[i] = 0;
                    continue;
                }

                if (destination[i] > backlog)
                {
                    redistributable += destination[i] - backlog;
                    destination[i] = backlog;
                }
            }

            while (redistributable > 0)
            {
                int receiver = FindLargestRemainingBacklog(destination[..count], dirtyBacklogs[..count]);
                if (receiver < 0)
                    break;

                destination[receiver]++;
                redistributable--;
            }
        }

        private static int FindLargestRemainingBacklog(ReadOnlySpan<int> budgets, ReadOnlySpan<int> dirtyBacklogs)
        {
            int bestIndex = -1;
            int bestRemaining = 0;
            for (int i = 0; i < budgets.Length; i++)
            {
                int remaining = Math.Max(0, dirtyBacklogs[i] - budgets[i]);
                if (remaining <= bestRemaining)
                    continue;

                bestIndex = i;
                bestRemaining = remaining;
            }

            return bestIndex;
        }

        private static int FindLargestCascadeBudget(ReadOnlySpan<int> budgets, int exceptIndex)
        {
            int bestIndex = -1;
            int bestBudget = int.MinValue;
            for (int i = 0; i < budgets.Length; i++)
            {
                if (i == exceptIndex || budgets[i] <= bestBudget)
                    continue;

                bestIndex = i;
                bestBudget = budgets[i];
            }

            return bestIndex;
        }

        private void EnsureResources(int resolution)
        {
            if (_resolution == resolution && _cascades[0]?.Volume != null)
                return;

            DestroyVolumes();
            _resolution = resolution;
            TextureBytes = 0;
            var extent = new Extent3D { Width = (uint)resolution, Height = (uint)resolution, Depth = (uint)resolution };
            EnsureCandidateHistoryBuffer(resolution);

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
                        generateFullMipChain: false));
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

        private void EnsureCandidateHistoryBuffer(int resolution)
        {
            int bricksPerAxis = Math.Max(1, (resolution + BrickSize - 1) / BrickSize);
            int requiredWords = checked(bricksPerAxis * bricksPerAxis * bricksPerAxis * BindlessIndex.GlobalSdfTextureCount * 2);
            if (_candidateHistoryBuffer.IsValid && _candidateHistoryCapacityWords == requiredWords)
            {
                ClearCandidateHistoryBuffer();
                return;
            }

            if (_candidateHistoryBuffer.IsValid)
                _bufferManager.DestroyBuffer(_candidateHistoryBuffer);

            ulong bufferSize = checked((ulong)requiredWords * sizeof(uint));
            _candidateHistoryBuffer = _bufferManager.CreateBuffer(
                bufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Global SDF Candidate History Buffer ({requiredWords} words)",
                MemoryBudgetCategory.RenderTargets);
            _candidateHistoryCapacityWords = requiredWords;
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.GlobalSdfCandidateHistoryBuffer, _bufferManager.GetBuffer(_candidateHistoryBuffer), 0, bufferSize);
            ClearCandidateHistoryBuffer();
        }

        private void ClearCandidateHistoryBuffer()
        {
            if (!_candidateHistoryBuffer.IsValid || _candidateHistoryCapacityWords <= 0)
                return;

            uint* words = (uint*)_bufferManager.GetMappedPointer(_candidateHistoryBuffer);
            for (int i = 0; i < _candidateHistoryCapacityWords; i++)
                words[i] = 0u;
            _bufferManager.FlushBuffer(_candidateHistoryBuffer, 0, checked((ulong)_candidateHistoryCapacityWords * sizeof(uint)));
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
                    MipCount = 1,
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

        public void MarkAllCascadesDirty()
        {
            for (int i = 0; i < _cascades.Length; i++)
                _cascades[i]?.MarkAllDirty();
        }

        private void SelectDirtyBrickJobs(
            int cascadeIndex,
            GlobalSdfCascadeRuntime cascade,
            List<GlobalSdfUpdateJob> jobs,
            int budget,
            Vector3 cameraPosition)
        {
            int remaining = budget;
            int selectedPriorityCount = cascade.SelectNearestPriorityDirtyBricks(
                cameraPosition,
                remaining,
                _idleRefreshCandidateScratch,
                _idleRefreshBrickScratch);
            for (int i = 0; i < selectedPriorityCount && remaining > 0; i++)
            {
                AddJob(cascadeIndex, cascade, jobs, _idleRefreshBrickScratch[i], 1);
                remaining--;
                LastFrameBricksUpdated++;
                LastFramePriorityBricksUpdated++;
            }

            while (remaining > 0)
            {
                int start = cascade.FindNextDirtyBrick();
                if (start < 0)
                    break;

                int count = cascade.ConsumeDirtyRun(start, remaining);
                AddJob(cascadeIndex, cascade, jobs, start, count);
                remaining -= count;
                LastFrameBricksUpdated += count;
                LastFrameDirtyBricksUpdated += count;
            }

            if (remaining <= 0 ||
                cascade.HasPriorityDirtyBricks ||
                cascade.HasDirtyBricks)
            {
                return;
            }

            int refreshCount = CalculateIdleRefreshBrickCount(remaining, cascade.IdleRefreshPendingBrickCount);
            if (refreshCount <= 0)
                return;

            int selectedCount = cascade.SelectNearestIdleRefreshBricks(
                cameraPosition,
                refreshCount,
                _idleRefreshCandidateScratch,
                _idleRefreshBrickScratch);
            for (int i = 0; i < selectedCount && remaining > 0; i++)
            {
                AddJob(cascadeIndex, cascade, jobs, _idleRefreshBrickScratch[i], 1);
                remaining--;
                LastFrameBricksUpdated++;
                LastFrameIdleRefreshBricksUpdated++;
            }
        }

        internal static int CalculateIdleRefreshBrickCount(int remainingBudget, int totalBricks)
        {
            if (remainingBudget <= 0 || totalBricks <= 0)
                return 0;

            return Math.Min(remainingBudget, totalBricks);
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

        internal readonly record struct IdleRefreshCandidate(int BrickIndex, float DistanceSquared);

        private void DestroyVolumes()
        {
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime? cascade = _cascades[i];
                if (cascade != null)
                    cascade.Volume.Dispose();

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
            if (_candidateHistoryBuffer.IsValid)
            {
                _bufferManager.DestroyBuffer(_candidateHistoryBuffer);
                _candidateHistoryBuffer = BufferHandle.Invalid;
                _candidateHistoryCapacityWords = 0;
            }
            GC.SuppressFinalize(this);
        }

        internal sealed class GlobalSdfCascadeRuntime
        {
            public GlobalSdfCascadeRuntime(VolumeTexture volume, float voxelSize, int resolution)
            {
                Volume = volume;
                VoxelSize = voxelSize;
                BricksPerAxis = Math.Max(1, (resolution + BrickSize - 1) / BrickSize);
                TotalBricks = checked(BricksPerAxis * BricksPerAxis * BricksPerAxis);
                _dirtyBricks = new bool[TotalBricks];
                _priorityDirtyBricks = new bool[TotalBricks];
                _idleRefreshPendingBricks = new bool[TotalBricks];
                MarkAllDirty();
            }

            public VolumeTexture Volume { get; }
            public float VoxelSize { get; }
            public int BricksPerAxis { get; }
            public int TotalBricks { get; }
            public int IdleRefreshPendingBrickCount => _idleRefreshPendingBrickCount;
            public Vector3 WorldMin { get; set; }
            public Vector3 WorldExtent { get; set; }
            public DdgiClipmapCell LogicalGridMinCell { get; private set; }
            public DdgiClipmapCell RingOffset { get; private set; }
            public bool HasDirtyBricks { get; private set; }
            public bool HasPriorityDirtyBricks { get; private set; }
            public int DirtyBrickCount { get; private set; }
            public int LastScrollDeltaCells { get; private set; }
            public int LastScrollInvalidatedBricks { get; private set; }

            private readonly bool[] _dirtyBricks;
            private readonly bool[] _priorityDirtyBricks;
            private readonly bool[] _idleRefreshPendingBricks;
            private int _idleRefreshPendingBrickCount;
            private int _dirtyScanIndex;
            private int _priorityDirtyScanIndex;
            private bool _initialized;

            public void UpdateClipmap(Vector3 cameraPosition, int resolution)
            {
                LastScrollDeltaCells = 0;
                LastScrollInvalidatedBricks = 0;
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
                LastScrollDeltaCells = checked((int)Math.Min(int.MaxValue, AbsLong(delta.X) + AbsLong(delta.Y) + AbsLong(delta.Z)));
                if (delta == DdgiClipmapCell.Zero)
                {
                    UpdateWorldMin();
                    return;
                }

                DdgiClipmapCell previousGridMin = LogicalGridMinCell;
                DdgiClipmapCell previousRingOffset = RingOffset;
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
                    LastScrollInvalidatedBricks = TotalBricks;
                }
                else
                {
                    LastScrollInvalidatedBricks = InvalidateChangedPhysicalBricks(previousGridMin, previousRingOffset);
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
                Array.Clear(_priorityDirtyBricks);
                MarkAllIdleRefreshPending();
                DirtyBrickCount = _dirtyBricks.Length;
                HasDirtyBricks = _dirtyBricks.Length > 0;
                HasPriorityDirtyBricks = false;
                _dirtyScanIndex = 0;
                _priorityDirtyScanIndex = 0;
            }

            public int FindNextPriorityDirtyBrick()
            {
                if (!HasPriorityDirtyBricks)
                    return -1;

                for (int i = 0; i < _priorityDirtyBricks.Length; i++)
                {
                    int index = (_priorityDirtyScanIndex + i) % _priorityDirtyBricks.Length;
                    if (_priorityDirtyBricks[index])
                    {
                        _priorityDirtyScanIndex = index;
                        return index;
                    }
                }

                return -1;
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

            public int ConsumePriorityDirtyRun(int start, int maxCount)
            {
                int count = 0;
                int limit = Math.Min(_priorityDirtyBricks.Length, start + Math.Max(0, maxCount));
                for (int i = start; i < limit && _priorityDirtyBricks[i]; i++)
                {
                    ConsumePriorityDirtyBrick(i);
                    count++;
                }

                _priorityDirtyScanIndex = (start + count) % _priorityDirtyBricks.Length;
                _dirtyScanIndex = _priorityDirtyScanIndex;
                HasDirtyBricks = DirtyBrickCount > 0;
                HasPriorityDirtyBricks = ContainsPriorityDirtyBrick();
                return count;
            }

            public int ConsumeDirtyRun(int start, int maxCount)
            {
                int count = 0;
                int limit = Math.Min(_dirtyBricks.Length, start + Math.Max(0, maxCount));
                for (int i = start; i < limit && _dirtyBricks[i]; i++)
                {
                    _dirtyBricks[i] = false;
                    _priorityDirtyBricks[i] = false;
                    DirtyBrickCount--;
                    ClearIdleRefreshPending(i);
                    count++;
                }

                _dirtyScanIndex = (start + count) % _dirtyBricks.Length;
                HasDirtyBricks = DirtyBrickCount > 0;
                HasPriorityDirtyBricks = ContainsPriorityDirtyBrick();
                return count;
            }

            public int SelectNearestPriorityDirtyBricks(
                Vector3 cameraPosition,
                int maxCount,
                List<IdleRefreshCandidate> candidates,
                List<int> destination)
            {
                int selectedCount = SelectNearestBricks(
                    cameraPosition,
                    maxCount,
                    _priorityDirtyBricks,
                    candidates,
                    destination);
                for (int i = 0; i < selectedCount; i++)
                    ConsumePriorityDirtyBrick(destination[i]);

                HasDirtyBricks = DirtyBrickCount > 0;
                HasPriorityDirtyBricks = ContainsPriorityDirtyBrick();
                return selectedCount;
            }

            internal bool IsPhysicalBrickDirty(int physicalBrickIndex)
            {
                return (uint)physicalBrickIndex < (uint)_dirtyBricks.Length && _dirtyBricks[physicalBrickIndex];
            }

            internal bool IsPhysicalBrickPriorityDirty(int physicalBrickIndex)
            {
                return (uint)physicalBrickIndex < (uint)_priorityDirtyBricks.Length && _priorityDirtyBricks[physicalBrickIndex];
            }

            internal DdgiClipmapCell GetLogicalCellForPhysicalBrick(int physicalBrickIndex)
            {
                return GetLogicalCellForPhysicalBrick(
                    physicalBrickIndex,
                    LogicalGridMinCell,
                    RingOffset,
                    BricksPerAxis);
            }

            internal static DdgiClipmapCell GetLogicalCellForPhysicalBrick(
                int physicalBrickIndex,
                DdgiClipmapCell logicalGridMin,
                DdgiClipmapCell ringOffset,
                int bricksPerAxis)
            {
                int xy = bricksPerAxis * bricksPerAxis;
                int physicalZ = physicalBrickIndex / xy;
                int rem = physicalBrickIndex - physicalZ * xy;
                int physicalY = rem / bricksPerAxis;
                int physicalX = rem - physicalY * bricksPerAxis;
                int logicalX = DdgiClipmapAddressing.PositiveModulo((long)physicalX - ringOffset.X, bricksPerAxis);
                int logicalY = DdgiClipmapAddressing.PositiveModulo((long)physicalY - ringOffset.Y, bricksPerAxis);
                int logicalZ = DdgiClipmapAddressing.PositiveModulo((long)physicalZ - ringOffset.Z, bricksPerAxis);
                return new DdgiClipmapCell(
                    logicalGridMin.X + logicalX,
                    logicalGridMin.Y + logicalY,
                    logicalGridMin.Z + logicalZ);
            }

            private int InvalidateChangedPhysicalBricks(DdgiClipmapCell previousGridMin, DdgiClipmapCell previousRingOffset)
            {
                int invalidated = 0;
                for (int physical = 0; physical < TotalBricks; physical++)
                {
                    DdgiClipmapCell previousLogical = GetLogicalCellForPhysicalBrick(
                        physical,
                        previousGridMin,
                        previousRingOffset,
                        BricksPerAxis);
                    DdgiClipmapCell currentLogical = GetLogicalCellForPhysicalBrick(physical);
                    if (previousLogical != currentLogical)
                    {
                        MarkPhysicalBrickDirty(physical, prioritize: true);
                        invalidated++;
                    }
                }

                return invalidated;
            }

            private void MarkLogicalRegionDirty(DdgiClipmapCell min, DdgiClipmapCell max, bool prioritize = false)
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
                            MarkPhysicalBrickDirty(physical, prioritize);
                        }
                    }
                }
            }

            private void MarkPhysicalBrickDirty(int physical, bool prioritize)
            {
                if ((uint)physical >= (uint)_dirtyBricks.Length)
                    return;

                if (!_dirtyBricks[physical])
                {
                    _dirtyBricks[physical] = true;
                    DirtyBrickCount++;
                }
                MarkIdleRefreshPending(physical);
                if (prioritize)
                {
                    _priorityDirtyBricks[physical] = true;
                    HasPriorityDirtyBricks = true;
                }
                HasDirtyBricks = true;
            }

            public int SelectNearestIdleRefreshBricks(
                Vector3 cameraPosition,
                int maxCount,
                List<IdleRefreshCandidate> candidates,
                List<int> destination)
            {
                int selectedCount = SelectNearestBricks(
                    cameraPosition,
                    Math.Min(Math.Max(0, maxCount), _idleRefreshPendingBrickCount),
                    _idleRefreshPendingBricks,
                    candidates,
                    destination);
                for (int i = 0; i < selectedCount; i++)
                    ClearIdleRefreshPending(destination[i]);

                return selectedCount;
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

            private void MarkAllIdleRefreshPending()
            {
                Array.Fill(_idleRefreshPendingBricks, true);
                _idleRefreshPendingBrickCount = _idleRefreshPendingBricks.Length;
            }

            private void MarkIdleRefreshPending(int physicalBrickIndex)
            {
                if ((uint)physicalBrickIndex >= (uint)_idleRefreshPendingBricks.Length ||
                    _idleRefreshPendingBricks[physicalBrickIndex])
                {
                    return;
                }

                _idleRefreshPendingBricks[physicalBrickIndex] = true;
                _idleRefreshPendingBrickCount++;
            }

            private void ClearIdleRefreshPending(int physicalBrickIndex)
            {
                if ((uint)physicalBrickIndex >= (uint)_idleRefreshPendingBricks.Length ||
                    !_idleRefreshPendingBricks[physicalBrickIndex])
                {
                    return;
                }

                _idleRefreshPendingBricks[physicalBrickIndex] = false;
                _idleRefreshPendingBrickCount--;
            }

            private void ConsumePriorityDirtyBrick(int physicalBrickIndex)
            {
                _priorityDirtyBricks[physicalBrickIndex] = false;
                if (_dirtyBricks[physicalBrickIndex])
                {
                    _dirtyBricks[physicalBrickIndex] = false;
                    DirtyBrickCount--;
                }
                ClearIdleRefreshPending(physicalBrickIndex);
            }

            private Vector3 CalculatePhysicalBrickCenter(int physicalBrickIndex, float brickWorldSize)
            {
                DdgiClipmapCell logicalCell = GetLogicalCellForPhysicalBrick(physicalBrickIndex);
                return WorldMin + new Vector3(
                    (logicalCell.X - LogicalGridMinCell.X + 0.5f) * brickWorldSize,
                    (logicalCell.Y - LogicalGridMinCell.Y + 0.5f) * brickWorldSize,
                    (logicalCell.Z - LogicalGridMinCell.Z + 0.5f) * brickWorldSize);
            }

            private float BrickWorldSize => VoxelSize * BrickSize;

            private int SelectNearestBricks(
                Vector3 cameraPosition,
                int maxCount,
                bool[] pendingBricks,
                List<IdleRefreshCandidate> candidates,
                List<int> destination)
            {
                if (pendingBricks == null)
                    throw new ArgumentNullException(nameof(pendingBricks));
                if (candidates == null)
                    throw new ArgumentNullException(nameof(candidates));
                if (destination == null)
                    throw new ArgumentNullException(nameof(destination));

                candidates.Clear();
                destination.Clear();
                int target = Math.Max(0, maxCount);
                if (target <= 0)
                    return 0;

                float brickWorldSize = BrickWorldSize;
                for (int i = 0; i < pendingBricks.Length; i++)
                {
                    if (!pendingBricks[i])
                        continue;

                    Vector3 center = CalculatePhysicalBrickCenter(i, brickWorldSize);
                    float distanceSquared = Vector3.DistanceSquared(center, cameraPosition);
                    InsertIdleRefreshCandidate(candidates, target, new IdleRefreshCandidate(i, distanceSquared));
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    int brickIndex = candidates[i].BrickIndex;
                    if (pendingBricks[brickIndex])
                        destination.Add(brickIndex);
                }

                return destination.Count;
            }

            private static void InsertIdleRefreshCandidate(
                List<IdleRefreshCandidate> candidates,
                int capacity,
                IdleRefreshCandidate candidate)
            {
                if (capacity <= 0)
                    return;

                int insert = candidates.Count;
                while (insert > 0 && CompareIdleRefreshCandidates(candidate, candidates[insert - 1]) < 0)
                    insert--;

                if (insert >= capacity)
                    return;

                if (candidates.Count < capacity)
                    candidates.Add(candidate);

                int last = Math.Min(candidates.Count - 1, capacity - 1);
                for (int i = last; i > insert; i--)
                    candidates[i] = candidates[i - 1];
                candidates[insert] = candidate;
            }

            private static int CompareIdleRefreshCandidates(IdleRefreshCandidate left, IdleRefreshCandidate right)
            {
                int distanceCompare = left.DistanceSquared.CompareTo(right.DistanceSquared);
                return distanceCompare != 0 ? distanceCompare : left.BrickIndex.CompareTo(right.BrickIndex);
            }

            private bool ContainsDirtyBrick()
            {
                for (int i = 0; i < _dirtyBricks.Length; i++)
                {
                    if (_dirtyBricks[i])
                        return true;
                }

                return false;
            }

            private bool ContainsPriorityDirtyBrick()
            {
                for (int i = 0; i < _priorityDirtyBricks.Length; i++)
                {
                    if (_priorityDirtyBricks[i])
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
