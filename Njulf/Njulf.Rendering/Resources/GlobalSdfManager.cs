using System;
using System.Collections.Generic;
using Njulf.Core.Math;
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

        public IReadOnlyList<GlobalSdfUpdateJob> PrepareUpdateJobs(Vector3 cameraPosition, int requestedResolution, int brickBudget)
        {
            int resolution = Math.Clamp(requestedResolution, 32, 512);
            EnsureResources(resolution);
            UpdateCascadeOrigins(cameraPosition);
            UploadCascadeMetadata();

            LastFrameBricksUpdated = 0;
            LastFrameCascadeCount = _cascades.Length;
            LastFrameResolution = resolution;

            if (brickBudget <= 0)
                return Array.Empty<GlobalSdfUpdateJob>();

            int remaining = brickBudget;
            var jobs = new List<GlobalSdfUpdateJob>(_cascades.Length);
            for (int i = 0; i < _cascades.Length && remaining > 0; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                int bricksPerAxis = cascade.BricksPerAxis;
                int totalBricks = bricksPerAxis * bricksPerAxis * bricksPerAxis;
                int start = cascade.NextBrickIndex;
                int brickCount = Math.Min(remaining, totalBricks - start);
                cascade.NextBrickIndex = (cascade.NextBrickIndex + brickCount) % totalBricks;
                remaining -= brickCount;
                LastFrameBricksUpdated += brickCount;

                jobs.Add(new GlobalSdfUpdateJob(
                    i,
                    BindlessIndex.GlobalSdfTextureBase + i,
                    cascade.Volume,
                    cascade.WorldMin,
                    cascade.WorldExtent,
                    cascade.VoxelSize,
                    resolution,
                    bricksPerAxis,
                    start,
                    brickCount));
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
                    new VolumeTextureDescriptor(sampled: true, storage: true));
                int bindlessIndex = BindlessIndex.GlobalSdfTextureBase + i;
                _bindlessHeap.RegisterStorageImage(bindlessIndex, volume.View, ImageLayout.General);
                _bindlessHeap.RegisterTexture(bindlessIndex, volume.View, imageLayout: ImageLayout.ShaderReadOnlyOptimal);
                _cascades[i] = new GlobalSdfCascadeRuntime(volume, CascadeVoxelSizes[i], resolution);
                TextureBytes += volume.EstimatedByteSize;
            }
        }

        private void EnsureCascadeBuffer()
        {
            if (_cascadeBuffer.IsValid)
                return;

            ulong bufferSize = checked((ulong)BindlessIndex.GlobalSdfTextureCount * (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUGlobalSdfCascade>());
            _cascadeBuffer = _bufferManager.CreateBuffer(
                bufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                "Global SDF Cascade Metadata Buffer",
                MemoryBudgetCategory.RenderTargets);
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.GlobalSdfCascadeBuffer, _bufferManager.GetBuffer(_cascadeBuffer), 0, bufferSize);
        }

        private void UploadCascadeMetadata()
        {
            EnsureCascadeBuffer();
            GPUGlobalSdfCascade* mapped = (GPUGlobalSdfCascade*)_bufferManager.GetMappedPointer(_cascadeBuffer);
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                mapped[i] = new GPUGlobalSdfCascade
                {
                    WorldMinAndVoxelSize = new Vector4(cascade.WorldMin.X, cascade.WorldMin.Y, cascade.WorldMin.Z, cascade.VoxelSize),
                    WorldExtentAndInvVoxelSize = new Vector4(cascade.WorldExtent.X, cascade.WorldExtent.Y, cascade.WorldExtent.Z, 1.0f / Math.Max(cascade.VoxelSize, 0.0001f)),
                    TextureIndex = checked((uint)(BindlessIndex.GlobalSdfTextureBase + i)),
                    Resolution = checked((uint)_resolution),
                    MipCount = 1,
                    Flags = 0
                };
            }

            ulong bufferSize = checked((ulong)_cascades.Length * (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUGlobalSdfCascade>());
            _bufferManager.FlushBuffer(_cascadeBuffer, 0, bufferSize);
        }

        private void UpdateCascadeOrigins(Vector3 cameraPosition)
        {
            for (int i = 0; i < _cascades.Length; i++)
            {
                GlobalSdfCascadeRuntime cascade = _cascades[i] ?? throw new InvalidOperationException("Global SDF cascade resources were not initialized.");
                float extent = cascade.VoxelSize * Math.Max(1, _resolution);
                Vector3 snappedCenter = new(
                    MathF.Floor(cameraPosition.X / cascade.VoxelSize) * cascade.VoxelSize,
                    MathF.Floor(cameraPosition.Y / cascade.VoxelSize) * cascade.VoxelSize,
                    MathF.Floor(cameraPosition.Z / cascade.VoxelSize) * cascade.VoxelSize);
                cascade.WorldMin = snappedCenter - new Vector3(extent * 0.5f);
                cascade.WorldExtent = new Vector3(extent);
            }
        }

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
            }

            public VolumeTexture Volume { get; }
            public float VoxelSize { get; }
            public int BricksPerAxis { get; }
            public int NextBrickIndex { get; set; }
            public Vector3 WorldMin { get; set; }
            public Vector3 WorldExtent { get; set; }
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
        int BrickCount);
}
