using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    public sealed unsafe class SurfaceCacheManager : IDisposable
    {
        public const int DefaultTileSize = 32;
        public const int MinTileSize = 8;
        public const int MaxTileSize = 128;
        public const int MaxCardsPerMesh = SurfaceCacheCardProjector.AxisCount;
        public const int InitialCardCapacity = 1024;

        private static readonly ulong SurfaceCardStride = (ulong)Marshal.SizeOf<GPUSurfaceCard>();
        private const int InitialWorkCapacityWords = 1_048_576;
        private const int SurfaceCacheGridResolution = 24;
        private const int SurfaceCacheGridMaxRefsPerCell = 24;
        private const int SurfaceCacheWorkHeaderWords = 12;
        private const int SurfaceCacheGridCellStrideWords = SurfaceCacheGridMaxRefsPerCell + 1;
        private const float SurfaceCacheCoarsestDdgiSdfCascadeVoxelSize = 1.0f;
        private const float SurfaceCacheSdfErrorPaddingMultiplier = 2.0f;
        private const float SurfaceCacheFarCascadeVoxelPadding =
            SurfaceCacheCoarsestDdgiSdfCascadeVoxelSize * SurfaceCacheSdfErrorPaddingMultiplier;
        private const uint SurfaceCacheCardFlagNew = 1u << 0;
        private const uint SurfaceCacheCardFlagDirty = 1u << 1;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly List<GPUSurfaceCard> _cards = new();
        private readonly List<int> _captureList = new();
        private readonly List<uint> _workWords = new();
        private BufferHandle _cardBuffer;
        private BufferHandle _workBuffer;
        private RenderTarget? _captureAtlas;
        private RenderTarget? _radianceAtlas;
        private int _cardCapacity;
        private int _workBufferCapacityWords;
        private int _atlasResolution;
        private int _cardAtlasResolution;
        private int _tileSize = DefaultTileSize;
        private int _nextLightTexel;
        private int _evictionCount;
        private ulong _meshSignature;
        private bool _atlasesRequireClear;
        private bool _disposed;

        public SurfaceCacheManager(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap bindlessHeap)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            EnsureCardCapacity(InitialCardCapacity);
            EnsureWorkCapacity(InitialWorkCapacityWords);
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

        internal SurfaceCacheFrameWork PrepareFrame(
            IReadOnlyList<AccelerationStructureManager.StaticOpaqueInstance> instances,
            int atlasResolution,
            int tileUpdateBudget,
            int texelLightBudget,
            uint frameIndex)
        {
            ArgumentNullException.ThrowIfNull(instances);

            int resolution = Math.Clamp(atlasResolution, 256, 8192);
            int tileSize = ResolveTileSize(resolution);
            EnsureAtlases(resolution);
            RebuildCardsIfNeeded(instances, tileSize, frameIndex);

            int totalCards = _cards.Count;
            BuildCaptureList(Math.Max(0, tileUpdateBudget), frameIndex);
            int tilesCaptured = _captureList.Count;
            MarkCapturedTiles(frameIndex);

            int totalTexels = checked(totalCards * tileSize * tileSize);
            int texelsLit = Math.Min(totalTexels, Math.Max(0, texelLightBudget));
            int firstTexel = totalTexels == 0 ? 0 : _nextLightTexel % totalTexels;
            _nextLightTexel = totalTexels == 0 ? 0 : (_nextLightTexel + texelsLit) % totalTexels;
            BuildAndUploadWorkBuffer();

            LastFrameTilesCaptured = tilesCaptured;
            LastFrameTexelsLit = texelsLit;
            LastFrameOccupancyPermille = CalculateOccupancyPermille(_cards, resolution);

            return new SurfaceCacheFrameWork(
                BindlessIndex.SurfaceCacheCardBuffer,
                totalCards,
                BindlessIndex.SurfaceCacheWorkBuffer,
                BindlessIndex.SurfaceCacheCaptureAtlasTexture,
                BindlessIndex.SurfaceCacheRadianceAtlasTexture,
                resolution,
                tileSize,
                0,
                tilesCaptured,
                firstTexel,
                texelsLit,
                LastFrameOccupancyPermille,
                _evictionCount,
                AtlasBytes,
                _atlasesRequireClear);
        }

        internal void MarkAtlasesCleared()
        {
            _atlasesRequireClear = false;
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
            _atlasesRequireClear = true;

            RegisterAtlas(BindlessIndex.SurfaceCacheCaptureAtlasTexture, _captureAtlas);
            RegisterAtlas(BindlessIndex.SurfaceCacheRadianceAtlasTexture, _radianceAtlas);
        }

        private void RegisterAtlas(int bindlessIndex, RenderTarget atlas)
        {
            _bindlessHeap.RegisterStorageImage(bindlessIndex, atlas.View, ImageLayout.General);
            _bindlessHeap.RegisterTexture(bindlessIndex, atlas.View, imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RebuildCardsIfNeeded(
            IReadOnlyList<AccelerationStructureManager.StaticOpaqueInstance> instances,
            int tileSize,
            uint frameIndex)
        {
            ulong meshSignature = CalculateInstanceSignature(instances);
            int requestedCardCount = checked(instances.Count * MaxCardsPerMesh);

            if (_tileSize == tileSize && _cardAtlasResolution == _atlasResolution && _meshSignature == meshSignature)
                return;

            _tileSize = tileSize;
            _cardAtlasResolution = _atlasResolution;
            _meshSignature = meshSignature;
            _cards.Clear();
            _captureList.Clear();
            _nextLightTexel = 0;

            var allocator = new SurfaceCacheAtlasShelfAllocator(_atlasResolution, tileSize);
            for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
            {
                AccelerationStructureManager.StaticOpaqueInstance instance = instances[instanceIndex];
                for (int axis = 0; axis < SurfaceCacheCardProjector.AxisCount; axis++)
                {
                    int cardTileSize = ResolveCardTileSize(instance, axis, tileSize);
                    if (!allocator.TryAllocate(cardTileSize, out SurfaceCacheAtlasAllocation allocation))
                        continue;

                    GPUSurfaceCard card = SurfaceCacheCardProjector.CreateCard(
                        checked((uint)instanceIndex),
                        axis,
                        instance.MeshInfo,
                        instance.WorldMatrix,
                        allocation,
                        0);
                    card.Flags = SurfaceCacheCardFlagNew | SurfaceCacheCardFlagDirty;
                    _cards.Add(card);
                }
            }

            _evictionCount = Math.Max(0, requestedCardCount - _cards.Count);
            EnsureCardCapacity(Math.Max(InitialCardCapacity, _cards.Count));
            UploadCards();
        }

        private void BuildCaptureList(int tileUpdateBudget, uint frameIndex)
        {
            _captureList.Clear();
            if (tileUpdateBudget <= 0 || _cards.Count == 0)
                return;

            for (int i = 0; i < _cards.Count; i++)
                _captureList.Add(i);

            _captureList.Sort((leftIndex, rightIndex) =>
            {
                GPUSurfaceCard left = _cards[leftIndex];
                GPUSurfaceCard right = _cards[rightIndex];
                int categoryCompare = CapturePriorityCategory(right).CompareTo(CapturePriorityCategory(left));
                if (categoryCompare != 0)
                    return categoryCompare;

                uint leftAge = frameIndex - left.LastCaptureFrame;
                uint rightAge = frameIndex - right.LastCaptureFrame;
                int ageCompare = rightAge.CompareTo(leftAge);
                return ageCompare != 0 ? ageCompare : leftIndex.CompareTo(rightIndex);
            });

            int texelBudget = checked(tileUpdateBudget * DefaultTileSize * DefaultTileSize);
            int consumedTexels = 0;
            int keepCount = 0;
            for (int i = 0; i < _captureList.Count; i++)
            {
                GPUSurfaceCard card = _cards[_captureList[i]];
                int cardTexels = CalculateCardTileTexelCount(card);
                if (consumedTexels + cardTexels > texelBudget && keepCount > 0)
                    break;

                consumedTexels += cardTexels;
                keepCount++;
            }

            if (_captureList.Count > keepCount)
                _captureList.RemoveRange(keepCount, _captureList.Count - keepCount);
        }

        private static int CapturePriorityCategory(in GPUSurfaceCard card)
        {
            if ((card.Flags & SurfaceCacheCardFlagNew) != 0)
                return 2;
            if ((card.Flags & SurfaceCacheCardFlagDirty) != 0)
                return 1;
            return 0;
        }

        private void MarkCapturedTiles(uint frameIndex)
        {
            if (_captureList.Count == 0)
                return;

            for (int i = 0; i < _captureList.Count; i++)
            {
                int cardIndex = _captureList[i];
                GPUSurfaceCard card = _cards[cardIndex];
                card.LastCaptureFrame = frameIndex;
                card.Flags &= ~(SurfaceCacheCardFlagNew | SurfaceCacheCardFlagDirty);
                _cards[cardIndex] = card;
            }

            UploadCards();
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

        private void EnsureWorkCapacity(int requiredWords)
        {
            if (_workBufferCapacityWords >= requiredWords && _workBuffer.IsValid)
                return;

            int nextCapacity = Math.Max(InitialWorkCapacityWords, _workBufferCapacityWords);
            while (nextCapacity < requiredWords)
                nextCapacity *= 2;

            BufferHandle oldBuffer = _workBuffer;
            _workBuffer = _bufferManager.CreateBuffer(
                checked((ulong)nextCapacity * sizeof(uint)),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Surface Cache Work Buffer ({nextCapacity} words)",
                MemoryBudgetCategory.RenderTargets);
            _workBufferCapacityWords = nextCapacity;
            _bindlessHeap.RegisterStorageBuffer(BindlessIndex.SurfaceCacheWorkBuffer, _bufferManager.GetBuffer(_workBuffer), 0, checked((ulong)_workBufferCapacityWords * sizeof(uint)));

            if (oldBuffer.IsValid)
                _bufferManager.DestroyBuffer(oldBuffer);

            if (_workWords.Count > 0)
                UploadWorkBuffer();
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

        private void BuildAndUploadWorkBuffer()
        {
            _workWords.Clear();
            int gridCellCount = SurfaceCacheGridResolution * SurfaceCacheGridResolution * SurfaceCacheGridResolution;
            int captureListOffset = SurfaceCacheWorkHeaderWords;
            int gridCellsOffset = captureListOffset + _captureList.Count;
            int totalWords = gridCellsOffset + gridCellCount * SurfaceCacheGridCellStrideWords;
            for (int i = 0; i < totalWords; i++)
                _workWords.Add(0u);

            CalculateGridBounds(out Vector3 gridMin, out float cellSize);
            _workWords[0] = SurfaceCacheGridResolution;
            _workWords[1] = SurfaceCacheGridMaxRefsPerCell;
            _workWords[2] = checked((uint)gridCellCount);
            _workWords[3] = checked((uint)_captureList.Count);
            _workWords[4] = FloatBits(gridMin.X);
            _workWords[5] = FloatBits(gridMin.Y);
            _workWords[6] = FloatBits(gridMin.Z);
            _workWords[7] = FloatBits(cellSize);
            _workWords[8] = checked((uint)captureListOffset);
            _workWords[9] = checked((uint)gridCellsOffset);
            _workWords[10] = checked((uint)_cards.Count);
            _workWords[11] = checked((uint)_tileSize);

            for (int i = 0; i < _captureList.Count; i++)
                _workWords[captureListOffset + i] = checked((uint)_captureList[i]);

            for (int cardIndex = 0; cardIndex < _cards.Count; cardIndex++)
                InsertCardIntoGrid(cardIndex, gridMin, cellSize, gridCellsOffset);

            EnsureWorkCapacity(_workWords.Count);
            UploadWorkBuffer();
        }

        private void UploadWorkBuffer()
        {
            void* mapped = _bufferManager.GetMappedPointer(_workBuffer);
            uint* destination = (uint*)mapped;
            for (int i = 0; i < _workWords.Count; i++)
                destination[i] = _workWords[i];
            _bufferManager.FlushBuffer(_workBuffer, 0, checked((ulong)_workWords.Count * sizeof(uint)));
        }

        private static int ResolveTileSize(int atlasResolution)
        {
            if (atlasResolution <= 1024)
                return 16;
            if (atlasResolution <= 2048)
                return DefaultTileSize;
            return MaxTileSize;
        }

        private static int ResolveCardTileSize(AccelerationStructureManager.StaticOpaqueInstance instance, int axis, int maxTileSize)
        {
            return ResolveCardTileSize(instance.MeshInfo, instance.WorldMatrix, axis, maxTileSize);
        }

        internal static int ResolveCardTileSize(MeshInfo meshInfo, Matrix4x4 worldMatrix, int axis, int maxTileSize)
        {
            System.Numerics.Vector3 extent = meshInfo.BoundingBoxMax - meshInfo.BoundingBoxMin;
            float projectedArea = axis switch
            {
                0 or 1 => MathF.Abs(extent.Y * extent.Z),
                2 or 3 => MathF.Abs(extent.X * extent.Z),
                _ => MathF.Abs(extent.X * extent.Y)
            };

            float scale = EstimateWorldScale(worldMatrix);
            float projectedSize = MathF.Sqrt(MathF.Max(projectedArea, 0.000001f)) * scale;
            int desired = projectedSize switch
            {
                >= 32.0f => maxTileSize,
                >= 16.0f => Math.Max(DefaultTileSize * 2, maxTileSize / 2),
                >= 4.0f => Math.Max(DefaultTileSize, maxTileSize / 4),
                >= 1.5f => Math.Max(MinTileSize * 2, maxTileSize / 8),
                _ => MinTileSize
            };
            return Math.Clamp(desired, MinTileSize, maxTileSize);
        }

        private static float EstimateWorldScale(Matrix4x4 matrix)
        {
            float sx = MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12 + matrix.M13 * matrix.M13);
            float sy = MathF.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22 + matrix.M23 * matrix.M23);
            float sz = MathF.Sqrt(matrix.M31 * matrix.M31 + matrix.M32 * matrix.M32 + matrix.M33 * matrix.M33);
            return MathF.Max(MathF.Max(sx, sy), MathF.Max(sz, 0.0001f));
        }

        private static int CalculateOccupancyPermille(IReadOnlyList<GPUSurfaceCard> cards, int atlasResolution)
        {
            if (atlasResolution <= 0)
                return 0;

            ulong occupiedTexels = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                uint tileSize = Math.Max(1u, (uint)MathF.Round(MathF.Max(cards[i].AtlasRect.Z, cards[i].AtlasRect.W)));
                occupiedTexels += (ulong)tileSize * tileSize;
            }

            ulong atlasTexels = checked((ulong)atlasResolution * (ulong)atlasResolution);
            return atlasTexels == 0UL
                ? 0
                : Math.Clamp((int)MathF.Round(occupiedTexels * 1000.0f / atlasTexels), 0, 1000);
        }

        private static int CalculateCardTileTexelCount(in GPUSurfaceCard card)
        {
            int tileSize = Math.Max(1, (int)MathF.Round(MathF.Max(card.AtlasRect.Z, card.AtlasRect.W)));
            return checked(tileSize * tileSize);
        }

        private void CalculateGridBounds(out Vector3 gridMin, out float cellSize)
        {
            CalculateGridBounds(_cards, out gridMin, out cellSize);
        }

        internal static void CalculateGridBounds(IReadOnlyList<GPUSurfaceCard> cards, out Vector3 gridMin, out float cellSize)
        {
            if (cards.Count == 0)
            {
                gridMin = Vector3.Zero;
                cellSize = 1.0f;
                return;
            }

            Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < cards.Count; i++)
            {
                CalculateCardBounds(cards[i], out Vector3 cardMin, out Vector3 cardMax);
                min = Vector3.Min(min, cardMin);
                max = Vector3.Max(max, cardMax);
            }

            Vector3 padding = new(MathF.Max(SurfaceCacheFarCascadeVoxelPadding, 0.001f));
            min -= padding;
            max += padding;
            Vector3 extent = Vector3.Max(max - min, new Vector3(0.001f));
            float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
            cellSize = MathF.Max(maxExtent / Math.Max(1, SurfaceCacheGridResolution - 1), 0.001f);
            gridMin = min;
        }

        private void InsertCardIntoGrid(int cardIndex, Vector3 gridMin, float cellSize, int gridCellsOffset)
        {
            CalculateCardBounds(_cards[cardIndex], out Vector3 cardMin, out Vector3 cardMax);
            int minX = ClampGridCoord((int)MathF.Floor((cardMin.X - gridMin.X) / cellSize));
            int minY = ClampGridCoord((int)MathF.Floor((cardMin.Y - gridMin.Y) / cellSize));
            int minZ = ClampGridCoord((int)MathF.Floor((cardMin.Z - gridMin.Z) / cellSize));
            int maxX = ClampGridCoord((int)MathF.Floor((cardMax.X - gridMin.X) / cellSize));
            int maxY = ClampGridCoord((int)MathF.Floor((cardMax.Y - gridMin.Y) / cellSize));
            int maxZ = ClampGridCoord((int)MathF.Floor((cardMax.Z - gridMin.Z) / cellSize));

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int cellIndex = x + y * SurfaceCacheGridResolution + z * SurfaceCacheGridResolution * SurfaceCacheGridResolution;
                        int baseWord = gridCellsOffset + cellIndex * SurfaceCacheGridCellStrideWords;
                        InsertCardIntoGridCell(cardIndex, baseWord);
                    }
                }
            }
        }

        private void InsertCardIntoGridCell(int cardIndex, int baseWord)
        {
            uint count = _workWords[baseWord];
            if (count < SurfaceCacheGridMaxRefsPerCell)
            {
                _workWords[baseWord + 1 + (int)count] = checked((uint)cardIndex);
                _workWords[baseWord] = count + 1u;
                return;
            }

            float candidatePriority = CalculateGridCardPriority(_cards[cardIndex]);
            int weakestOffset = -1;
            float weakestPriority = candidatePriority;
            for (int i = 0; i < SurfaceCacheGridMaxRefsPerCell; i++)
            {
                int existingIndex = checked((int)_workWords[baseWord + 1 + i]);
                float existingPriority = CalculateGridCardPriority(_cards[existingIndex]);
                if (existingPriority >= weakestPriority)
                    continue;

                weakestPriority = existingPriority;
                weakestOffset = i;
            }

            if (weakestOffset >= 0)
                _workWords[baseWord + 1 + weakestOffset] = checked((uint)cardIndex);
        }

        private static void CalculateCardBounds(in GPUSurfaceCard card, out Vector3 min, out Vector3 max)
        {
            Vector3 origin = card.WorldOriginAndTileSize.XYZ();
            Vector3 axisU = card.WorldAxisUAndHalfExtent.XYZ();
            Vector3 axisV = card.WorldAxisVAndHalfExtent.XYZ();
            Vector3 axisN = card.WorldAxisNAndDepthRange.XYZ();
            Vector3 spanU = axisU * MathF.Max(card.WorldAxisUAndHalfExtent.W * 2.0f, 0.0001f);
            Vector3 spanV = axisV * MathF.Max(card.WorldAxisVAndHalfExtent.W * 2.0f, 0.0001f);
            Vector3 spanN = axisN * MathF.Max(card.WorldAxisNAndDepthRange.W, 0.0001f);

            min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = origin;
                if ((i & 1) != 0)
                    corner += spanU;
                if ((i & 2) != 0)
                    corner += spanV;
                if ((i & 4) != 0)
                    corner += spanN;
                min = Vector3.Min(min, corner);
                max = Vector3.Max(max, corner);
            }

            Vector3 padding = new(SurfaceCacheFarCascadeVoxelPadding);
            min -= padding;
            max += padding;
        }

        private static float CalculateGridCardPriority(in GPUSurfaceCard card)
        {
            float width = MathF.Max(card.WorldAxisUAndHalfExtent.W * 2.0f, 0.0001f);
            float height = MathF.Max(card.WorldAxisVAndHalfExtent.W * 2.0f, 0.0001f);
            float depth = MathF.Max(card.WorldAxisNAndDepthRange.W, 0.0001f);
            float tileSize = MathF.Max(MathF.Max(card.AtlasRect.Z, card.AtlasRect.W), 1.0f);
            return width * height * depth * tileSize;
        }

        private static int ClampGridCoord(int value) => Math.Clamp(value, 0, SurfaceCacheGridResolution - 1);

        private struct SurfaceCacheAtlasShelfAllocator
        {
            private readonly int _resolution;
            private readonly int _maxTileSize;
            private int _x;
            private int _y;
            private int _rowHeight;

            public SurfaceCacheAtlasShelfAllocator(int resolution, int maxTileSize)
            {
                _resolution = Math.Max(1, resolution);
                _maxTileSize = Math.Clamp(maxTileSize, MinTileSize, MaxTileSize);
                _x = 0;
                _y = 0;
                _rowHeight = 0;
            }

            public bool TryAllocate(int size, out SurfaceCacheAtlasAllocation allocation)
            {
                size = Math.Clamp(size, MinTileSize, _maxTileSize);
                if (size > _resolution)
                {
                    allocation = default;
                    return false;
                }

                if (_x + size > _resolution)
                {
                    _x = 0;
                    _y += Math.Max(_rowHeight, size);
                    _rowHeight = 0;
                }

                if (_y + size > _resolution)
                {
                    allocation = default;
                    return false;
                }

                allocation = new SurfaceCacheAtlasAllocation(checked((uint)_x), checked((uint)_y), checked((uint)size));
                _x += size;
                _rowHeight = Math.Max(_rowHeight, size);
                return true;
            }
        }

        private static ulong CalculateInstanceSignature(IReadOnlyList<AccelerationStructureManager.StaticOpaqueInstance> instances)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            hash = Mix(hash, unchecked((uint)instances.Count), prime);
            for (int i = 0; i < instances.Count; i++)
            {
                AccelerationStructureManager.StaticOpaqueInstance instance = instances[i];
                hash = Mix(hash, unchecked((uint)instance.Mesh.Index), prime);
                hash = Mix(hash, instance.Mesh.Generation, prime);
                hash = Mix(hash, instance.MaterialIndex, prime);
                hash = Mix(hash, instance.MeshInfo.VertexOffset, prime);
                hash = Mix(hash, instance.MeshInfo.IndexOffset, prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M11), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M12), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M13), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M14), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M21), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M22), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M23), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M24), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M31), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M32), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M33), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M34), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M41), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M42), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M43), prime);
                hash = Mix(hash, FloatBits(instance.WorldMatrix.M44), prime);
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
            if (_workBuffer.IsValid)
                _bufferManager.DestroyBuffer(_workBuffer);
            GC.SuppressFinalize(this);
        }
    }

    public readonly record struct SurfaceCacheFrameWork(
        int CardBufferIndex,
        int CardCount,
        int WorkBufferIndex,
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
        ulong AtlasBytes,
        bool AtlasesRequireClear);
}
