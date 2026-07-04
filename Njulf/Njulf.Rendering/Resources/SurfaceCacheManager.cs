using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class SurfaceCacheManager : IDisposable
    {
        public const int DefaultTileSize = 32;
        public const int MaxCardsPerMesh = SurfaceCacheCardProjector.AxisCount;
        public const int InitialCardCapacity = 1024;

        private static readonly ulong SurfaceCardStride = (ulong)Marshal.SizeOf<GPUSurfaceCard>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly MeshManager _meshManager;
        private readonly List<GPUSurfaceCard> _cards = new();
        private BufferHandle _cardBuffer;
        private RenderTarget? _captureAtlas;
        private RenderTarget? _radianceAtlas;
        private int _cardCapacity;
        private int _atlasResolution;
        private int _tileSize = DefaultTileSize;
        private int _nextCaptureTile;
        private int _nextLightTexel;
        private int _evictionCount;
        private ulong _meshSignature;
        private bool _disposed;

        public SurfaceCacheManager(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap bindlessHeap,
            MeshManager meshManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            EnsureCardCapacity(InitialCardCapacity);
        }

        public RenderTarget? CaptureAtlas => _captureAtlas;
        public RenderTarget? RadianceAtlas => _radianceAtlas;
        public int CardCount => _cards.Count;
        public int AtlasResolution => _atlasResolution;
        public int TileSize => _tileSize;
        public ulong AtlasBytes => (_captureAtlas?.EstimatedByteSize ?? 0UL) + (_radianceAtlas?.EstimatedByteSize ?? 0UL);
        public ulong CardBufferBytes => (ulong)_cardCapacity * SurfaceCardStride;
        public int LastFrameTilesCaptured { get; private set; }
        public int LastFrameTexelsLit { get; private set; }
        public int LastFrameOccupancyPermille { get; private set; }
        public int EvictionCount => _evictionCount;

        public SurfaceCacheFrameWork PrepareFrame(int atlasResolution, int tileUpdateBudget, int texelLightBudget, uint frameIndex)
        {
            int resolution = Math.Clamp(atlasResolution, 256, 8192);
            int tileSize = ResolveTileSize(resolution);
            EnsureAtlases(resolution);
            RebuildCardsIfNeeded(tileSize, frameIndex);

            int totalCards = _cards.Count;
            int tileBudget = Math.Clamp(tileUpdateBudget, 0, Math.Max(totalCards, tileUpdateBudget));
            int tilesCaptured = Math.Min(totalCards, tileBudget);
            int firstTile = totalCards == 0 ? 0 : _nextCaptureTile % totalCards;
            MarkCapturedTiles(firstTile, tilesCaptured, frameIndex);
            _nextCaptureTile = totalCards == 0 ? 0 : (_nextCaptureTile + tilesCaptured) % totalCards;

            int totalTexels = checked(totalCards * tileSize * tileSize);
            int texelsLit = Math.Min(totalTexels, Math.Max(0, texelLightBudget));
            int firstTexel = totalTexels == 0 ? 0 : _nextLightTexel % totalTexels;
            _nextLightTexel = totalTexels == 0 ? 0 : (_nextLightTexel + texelsLit) % totalTexels;

            LastFrameTilesCaptured = tilesCaptured;
            LastFrameTexelsLit = texelsLit;
            LastFrameOccupancyPermille = CalculateOccupancyPermille(totalCards, resolution, tileSize);

            return new SurfaceCacheFrameWork(
                BindlessIndex.SurfaceCacheCardBuffer,
                totalCards,
                BindlessIndex.SurfaceCacheCaptureAtlasTexture,
                BindlessIndex.SurfaceCacheRadianceAtlasTexture,
                resolution,
                tileSize,
                firstTile,
                tilesCaptured,
                firstTexel,
                texelsLit,
                LastFrameOccupancyPermille,
                _evictionCount,
                AtlasBytes);
        }

        private void EnsureAtlases(int resolution)
        {
            if (_atlasResolution == resolution && _captureAtlas != null && _radianceAtlas != null)
                return;

            _captureAtlas?.Dispose();
            _radianceAtlas?.Dispose();
            _atlasResolution = resolution;

            var extent = new Extent2D { Width = (uint)resolution, Height = (uint)resolution };
            var descriptor = new RenderTargetDescriptor(colorAttachment: true, sampled: true, storage: true, transferDestination: true);
            _captureAtlas = new RenderTarget(_context, "Surface Cache Capture Atlas", Format.R16G16B16A16Sfloat, extent, descriptor);
            _radianceAtlas = new RenderTarget(_context, "Surface Cache Radiance Atlas", Format.R16G16B16A16Sfloat, extent, descriptor);

            RegisterAtlas(BindlessIndex.SurfaceCacheCaptureAtlasTexture, _captureAtlas);
            RegisterAtlas(BindlessIndex.SurfaceCacheRadianceAtlasTexture, _radianceAtlas);
        }

        private void RegisterAtlas(int bindlessIndex, RenderTarget atlas)
        {
            _bindlessHeap.RegisterStorageImage(bindlessIndex, atlas.View, ImageLayout.General);
            _bindlessHeap.RegisterTexture(bindlessIndex, atlas.View, imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RebuildCardsIfNeeded(int tileSize, uint frameIndex)
        {
            IReadOnlyList<MeshSnapshot> meshes = _meshManager.GetMeshSnapshots();
            ulong meshSignature = CalculateMeshSignature(meshes);
            int maxCards = CalculateTileCapacity(_atlasResolution, tileSize);
            int requestedCardCount = checked(meshes.Count * MaxCardsPerMesh);
            int cardCount = Math.Min(requestedCardCount, maxCards);

            if (_tileSize == tileSize && _cards.Count == cardCount && _meshSignature == meshSignature)
                return;

            _tileSize = tileSize;
            _meshSignature = meshSignature;
            _evictionCount = Math.Max(0, requestedCardCount - maxCards);
            EnsureCardCapacity(Math.Max(InitialCardCapacity, cardCount));
            _cards.Clear();
            _nextCaptureTile = 0;
            _nextLightTexel = 0;

            for (int meshIndex = 0; meshIndex < meshes.Count && _cards.Count < cardCount; meshIndex++)
            {
                MeshSnapshot snapshot = meshes[meshIndex];
                for (int axis = 0; axis < SurfaceCacheCardProjector.AxisCount && _cards.Count < cardCount; axis++)
                {
                    SurfaceCacheAtlasAllocation allocation = AllocateTile(_cards.Count, tileSize);
                    _cards.Add(SurfaceCacheCardProjector.CreateCard(
                        checked((uint)snapshot.Mesh.Index),
                        axis,
                        snapshot.MeshInfo,
                        allocation,
                        frameIndex));
                }
            }

            UploadCards();
        }

        private void MarkCapturedTiles(int firstTile, int tileCount, uint frameIndex)
        {
            if (tileCount <= 0 || _cards.Count == 0)
                return;

            for (int i = 0; i < tileCount; i++)
            {
                int cardIndex = (firstTile + i) % _cards.Count;
                GPUSurfaceCard card = _cards[cardIndex];
                card.LastCaptureFrame = frameIndex;
                _cards[cardIndex] = card;
            }

            UploadCards();
        }

        private SurfaceCacheAtlasAllocation AllocateTile(int tileIndex, int tileSize)
        {
            int tilesPerAxis = Math.Max(1, _atlasResolution / tileSize);
            int x = (tileIndex % tilesPerAxis) * tileSize;
            int y = (tileIndex / tilesPerAxis) * tileSize;
            return new SurfaceCacheAtlasAllocation(checked((uint)x), checked((uint)y), checked((uint)tileSize));
        }

        private void EnsureCardCapacity(int requiredCount)
        {
            if (_cardCapacity >= requiredCount && _cardBuffer.IsValid)
                return;

            int nextCapacity = Math.Max(InitialCardCapacity, _cardCapacity);
            while (nextCapacity < requiredCount)
                nextCapacity *= 2;

            BufferHandle oldBuffer = _cardBuffer;
            _cardBuffer = _bufferManager.CreateBuffer(
                checked((ulong)nextCapacity * SurfaceCardStride),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Surface Cache Card Buffer ({nextCapacity} records)",
                MemoryBudgetCategory.RenderTargets);
            _cardCapacity = nextCapacity;
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.SurfaceCacheCardBuffer, _bufferManager.GetBuffer(_cardBuffer), 0, CardBufferBytes);

            if (oldBuffer.IsValid)
                _bufferManager.DestroyBuffer(oldBuffer);

            if (_cards.Count > 0)
                UploadCards();
        }

        private void UploadCards()
        {
            if (_cards.Count == 0)
                return;

            void* mapped = _bufferManager.GetMappedPointer(_cardBuffer);
            GPUSurfaceCard* destination = (GPUSurfaceCard*)mapped;
            for (int i = 0; i < _cards.Count; i++)
                destination[i] = _cards[i];
            _bufferManager.FlushBuffer(_cardBuffer, 0, checked((ulong)_cards.Count * SurfaceCardStride));
        }

        private static int ResolveTileSize(int atlasResolution)
        {
            if (atlasResolution <= 1024)
                return 16;
            return DefaultTileSize;
        }

        private static int CalculateTileCapacity(int atlasResolution, int tileSize)
        {
            int tilesPerAxis = Math.Max(1, atlasResolution / Math.Max(1, tileSize));
            return tilesPerAxis * tilesPerAxis;
        }

        private static int CalculateOccupancyPermille(int cardCount, int atlasResolution, int tileSize)
        {
            int capacity = CalculateTileCapacity(atlasResolution, tileSize);
            if (capacity == 0)
                return 0;
            return Math.Clamp((int)MathF.Round(cardCount * 1000.0f / capacity), 0, 1000);
        }

        private static ulong CalculateMeshSignature(IReadOnlyList<MeshSnapshot> meshes)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < meshes.Count; i++)
            {
                MeshSnapshot snapshot = meshes[i];
                hash = Mix(hash, unchecked((uint)snapshot.Mesh.Index), prime);
                hash = Mix(hash, snapshot.Mesh.Generation, prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMin.X), prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMin.Y), prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMin.Z), prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMax.X), prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMax.Y), prime);
                hash = Mix(hash, FloatBits(snapshot.MeshInfo.BoundingBoxMax.Z), prime);
            }

            return hash;
        }

        private static ulong Mix(ulong hash, uint value, ulong prime) => (hash ^ value) * prime;
        private static uint FloatBits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _captureAtlas?.Dispose();
            _radianceAtlas?.Dispose();
            if (_cardBuffer.IsValid)
                _bufferManager.DestroyBuffer(_cardBuffer);
            GC.SuppressFinalize(this);
        }
    }

    public readonly record struct SurfaceCacheFrameWork(
        int CardBufferIndex,
        int CardCount,
        int CaptureAtlasTextureIndex,
        int RadianceAtlasTextureIndex,
        int AtlasResolution,
        int TileSize,
        int FirstTileIndex,
        int TilesCaptured,
        int FirstTexelIndex,
        int TexelsLit,
        int AtlasOccupancyPermille,
        int EvictionCount,
        ulong AtlasBytes);
}
