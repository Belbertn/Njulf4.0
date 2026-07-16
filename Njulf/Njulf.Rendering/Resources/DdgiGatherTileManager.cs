using System;
using System.Collections.Generic;
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
    public sealed unsafe class DdgiGatherTileManager : IDisposable
    {
        public const uint TileSize = 16;
        public const uint InvalidVolumeIndex = uint.MaxValue;
        public const uint HeaderEnabledFlag = 1u << 0;
        // The legacy and Simple-DDGI paths deliberately reuse the same small
        // bindless buffer.  Tag the producer so a frame that is changing GI
        // modes can never reinterpret a legacy tile as a Simple-DDGI candidate
        // table.  The legacy forward path only tests HeaderEnabledFlag and is
        // therefore ABI-compatible with this additive bit.
        public const uint HeaderSimpleDdgiFlag = 1u << 1;
        public const uint TileLocalVolumeValidFlag = 1u << 0;
        public const uint TilePrimaryClipmapValidFlag = 1u << 1;
        public const uint TileSecondaryClipmapValidFlag = 1u << 2;
        public const uint TileFallbackFlag = 1u << 3;
        // More than two projected Simple-DDGI volumes touch the tile. This is
        // diagnostic/heatmap evidence only: the first two entries retain the
        // deterministic highest-priority candidates, and shader-side world
        // validation still falls back to the bounded table walk if either is
        // insufficient. Keeping this distinct from TileFallbackFlag prevents
        // nested camera rings from disabling the common fast path wholesale.
        public const uint TileSimpleCandidateOverflowFlag = 1u << 4;

        private static readonly ulong HeaderSize = (ulong)Marshal.SizeOf<GPUDdgiGatherTileHeader>();
        private static readonly ulong TileStride = (ulong)Marshal.SizeOf<GPUDdgiGatherTile>();
        private const ulong MinBufferSize = 16;
        private const float MinCandidateBlendWeight = 0.0001f;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly List<RetiredBufferResource> _retiredBuffers = new();
        private GPUDdgiGatherTile[] _tileScratch = Array.Empty<GPUDdgiGatherTile>();
        private BufferHandle _buffer;
        private ulong _bufferSize;
        private BindlessHeap? _registeredBindlessHeap;
        private ulong _frameSerial;
        private bool _disposed;

        public DdgiGatherTileManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            EnsureCapacity(1);
        }

        public int LastTileCount { get; private set; }
        public int LastTileCountX { get; private set; }
        public int LastTileCountY { get; private set; }
        public int LastSelectedLocalTileCount { get; private set; }
        public int LastSelectedClipmapTileCount { get; private set; }
        public int LastFallbackTileCount { get; private set; }
        public ulong CurrentBufferBytes => _bufferSize;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiGatherTileBuffer,
                _bufferManager.GetBuffer(_buffer),
                0,
                _bufferSize);
        }

        public void Upload(
            DdgiFrameLayout layout,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            Upload(
                layout,
                viewProjection,
                screenWidth,
                screenHeight,
                stagingRing,
                commandBuffer,
                DdgiGatherSupportReadiness.Steady);
        }

        public void Upload(
            DdgiFrameLayout layout,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            DdgiGatherSupportReadiness supportReadiness)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI gather tile upload.", nameof(commandBuffer));

            BeginFrameResourceRetirement();
            uint tileCountX = CalculateTileCount(screenWidth);
            uint tileCountY = CalculateTileCount(screenHeight);
            int tileCount = checked((int)(tileCountX * tileCountY));
            EnsureCapacity(Math.Max(tileCount, 1));
            BuildResult result = BuildTiles(
                layout,
                viewProjection,
                screenWidth,
                screenHeight,
                _tileScratch.AsSpan(0, tileCount),
                supportReadiness);

            LastTileCount = tileCount;
            LastTileCountX = checked((int)result.Header.TileCountX);
            LastTileCountY = checked((int)result.Header.TileCountY);
            LastSelectedLocalTileCount = result.SelectedLocalTileCount;
            LastSelectedClipmapTileCount = result.SelectedClipmapTileCount;
            LastFallbackTileCount = result.FallbackTileCount;

            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _buffer,
                result.Header,
                _tileScratch.AsSpan(0, tileCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        /// <summary>
        /// Uploads the bounded two-candidate selector used by the fixed-grid
        /// Simple-DDGI forward gather.  The buffer ABI is deliberately shared
        /// with the legacy DDGI tile selector because the two GI paths are
        /// mutually exclusive.  This keeps the fast path to one small static
        /// storage-buffer binding rather than adding an otherwise idle bindless
        /// resource.
        ///
        /// The first and second candidate fields are table indices in priority
        /// order.  They are conservative screen-tile candidates only: the
        /// shader still validates the receiver's unbiased world position before
        /// sampling and falls back to its bounded table selector for an
        /// uncovered/ambiguous tile.  Thus an imprecise projected AABB can cost
        /// a metadata check, but can never change lighting ownership.
        /// </summary>
        public void UploadSimple(
            ReadOnlySpan<GPUSimpleDdgiVolume> volumes,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for Simple-DDGI gather tile upload.", nameof(commandBuffer));

            BeginFrameResourceRetirement();
            uint tileCountX = CalculateTileCount(screenWidth);
            uint tileCountY = CalculateTileCount(screenHeight);
            int tileCount = checked((int)(tileCountX * tileCountY));
            EnsureCapacity(Math.Max(tileCount, 1));
            BuildResult result = BuildSimpleTiles(
                volumes,
                viewProjection,
                screenWidth,
                screenHeight,
                _tileScratch.AsSpan(0, tileCount));

            LastTileCount = tileCount;
            LastTileCountX = checked((int)result.Header.TileCountX);
            LastTileCountY = checked((int)result.Header.TileCountY);
            LastSelectedLocalTileCount = result.SelectedLocalTileCount;
            LastSelectedClipmapTileCount = result.SelectedClipmapTileCount;
            LastFallbackTileCount = result.FallbackTileCount;

            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _buffer,
                result.Header,
                _tileScratch.AsSpan(0, tileCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        internal static BuildResult BuildTiles(
            DdgiFrameLayout layout,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            Span<GPUDdgiGatherTile> destination)
        {
            return BuildTiles(
                layout,
                viewProjection,
                screenWidth,
                screenHeight,
                destination,
                default(DdgiGatherSupportReadiness).NormalizeOrSteady());
        }

        internal static BuildResult BuildTiles(
            DdgiFrameLayout layout,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            Span<GPUDdgiGatherTile> destination,
            DdgiGatherSupportReadiness supportReadiness)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));
            supportReadiness = supportReadiness.NormalizeForRuntime();

            uint tileCountX = CalculateTileCount(screenWidth);
            uint tileCountY = CalculateTileCount(screenHeight);
            int tileCount = checked((int)(tileCountX * tileCountY));
            if (destination.Length < tileCount)
                throw new ArgumentException("Destination span is too small for the requested DDGI gather tile grid.", nameof(destination));

            uint primaryClipmap = InvalidVolumeIndex;
            uint secondaryClipmap = InvalidVolumeIndex;
            float secondaryBlend = 0.0f;
            SelectClipmapCandidates(layout.VolumeMetadata, out primaryClipmap, out secondaryClipmap, out secondaryBlend);

            int selectedClipmapTileCount = 0;
            int selectedLocalTileCount = 0;
            int fallbackTileCount = 0;

            for (int i = 0; i < tileCount; i++)
            {
                uint flags = 0;
                if (primaryClipmap != InvalidVolumeIndex)
                    flags |= TilePrimaryClipmapValidFlag;
                if (secondaryClipmap != InvalidVolumeIndex)
                    flags |= TileSecondaryClipmapValidFlag;

                destination[i] = new GPUDdgiGatherTile
                {
                    LocalVolumeIndex = InvalidVolumeIndex,
                    PrimaryClipmapVolumeIndex = primaryClipmap,
                    SecondaryClipmapVolumeIndex = secondaryClipmap,
                    Flags = flags,
                    BlendWeights = new Vector4(
                        0.0f,
                        primaryClipmap != InvalidVolumeIndex ? Math.Clamp(1.0f - secondaryBlend, 0.0f, 1.0f) * supportReadiness.PrimaryClipmap : 0.0f,
                        secondaryClipmap != InvalidVolumeIndex ? Math.Clamp(secondaryBlend, 0.0f, 1.0f) * supportReadiness.SecondaryClipmap : 0.0f,
                        0.0f)
                };
            }

            for (int volumeIndex = 0; volumeIndex < layout.Volumes.Count; volumeIndex++)
            {
                if (volumeIndex >= layout.VolumeMetadata.Count)
                    break;

                DdgiProbeVolumeRuntimeMetadata metadata = layout.VolumeMetadata[volumeIndex];
                if (metadata.Kind != DdgiProbeVolumeKind.Authored)
                    continue;

                GlobalIlluminationProbeVolume? volume = layout.Volumes[volumeIndex];
                if (volume == null || !volume.Enabled)
                    continue;

                if (!TryProjectVolumeTiles(volume.Bounds, viewProjection, screenWidth, screenHeight, tileCountX, tileCountY, out TileRect rect))
                    continue;

                for (int tileY = rect.MinY; tileY <= rect.MaxY; tileY++)
                {
                    int rowOffset = checked(tileY * (int)tileCountX);
                    for (int tileX = rect.MinX; tileX <= rect.MaxX; tileX++)
                    {
                        int tileIndex = rowOffset + tileX;
                        if (destination[tileIndex].LocalVolumeIndex != InvalidVolumeIndex)
                            continue;

                        GPUDdgiGatherTile tile = destination[tileIndex];
                        tile.LocalVolumeIndex = checked((uint)volumeIndex);
                        tile.Flags |= TileLocalVolumeValidFlag;
                        tile.BlendWeights.X = supportReadiness.Local;
                        destination[tileIndex] = tile;
                    }
                }
            }

            // Fallback means the fast gather selector has no usable weighted candidate for this tile.
            // DDGI may still be active; the forward shader can recover by exhaustive sampling.
            for (int i = 0; i < tileCount; i++)
            {
                GPUDdgiGatherTile tile = destination[i];
                bool hasLocalCandidate = HasUsableCandidateWeight(tile.Flags, TileLocalVolumeValidFlag, tile.BlendWeights.X);
                bool hasClipmapCandidate =
                    HasUsableCandidateWeight(tile.Flags, TilePrimaryClipmapValidFlag, tile.BlendWeights.Y) ||
                    HasUsableCandidateWeight(tile.Flags, TileSecondaryClipmapValidFlag, tile.BlendWeights.Z);
                if (hasLocalCandidate)
                    selectedLocalTileCount++;
                if (hasClipmapCandidate)
                    selectedClipmapTileCount++;

                if (!layout.IsDdgiActive || hasLocalCandidate || hasClipmapCandidate)
                    continue;

                tile.Flags |= TileFallbackFlag;
                tile.BlendWeights.W = 1.0f;
                destination[i] = tile;
                fallbackTileCount++;
            }

            GPUDdgiGatherTileHeader header = new()
            {
                TileCountX = tileCountX,
                TileCountY = tileCountY,
                TileSize = TileSize,
                Flags = layout.IsDdgiActive && tileCount > 0 ? HeaderEnabledFlag : 0u
            };
            return new BuildResult(header, tileCount, selectedLocalTileCount, selectedClipmapTileCount, fallbackTileCount);
        }

        /// <summary>
        /// Builds the Simple-DDGI screen-tile candidate table without allocating.
        /// Volume order is the layout compiler's deterministic ownership order:
        /// receiver-priority authored volumes precede camera rings, and finer
        /// rings precede coarser rings. Each tile retains the two highest-priority
        /// projected candidates used by the hot path; the shader discovers its
        /// bounded third candidate from metadata only when both lack support.
        /// </summary>
        internal static BuildResult BuildSimpleTiles(
            ReadOnlySpan<GPUSimpleDdgiVolume> volumes,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            Span<GPUDdgiGatherTile> destination)
        {
            uint tileCountX = CalculateTileCount(screenWidth);
            uint tileCountY = CalculateTileCount(screenHeight);
            int tileCount = checked((int)(tileCountX * tileCountY));
            if (destination.Length < tileCount)
                throw new ArgumentException("Destination span is too small for the requested Simple-DDGI gather tile grid.", nameof(destination));

            for (int i = 0; i < tileCount; i++)
            {
                destination[i] = new GPUDdgiGatherTile
                {
                    LocalVolumeIndex = InvalidVolumeIndex,
                    PrimaryClipmapVolumeIndex = InvalidVolumeIndex,
                    SecondaryClipmapVolumeIndex = InvalidVolumeIndex,
                    Flags = 0u,
                    BlendWeights = Vector4.Zero
                };
            }

            bool hasEnabledVolume = false;
            int volumeCount = Math.Min(volumes.Length, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
            for (int volumeIndex = 0; volumeIndex < volumeCount; volumeIndex++)
            {
                if (!TryGetSimpleVolumeBounds(volumes[volumeIndex], out BoundingBox bounds))
                    continue;

                hasEnabledVolume = true;
                if (!TryProjectVolumeTiles(bounds, viewProjection, screenWidth, screenHeight, tileCountX, tileCountY, out TileRect rect))
                    continue;

                for (int tileY = rect.MinY; tileY <= rect.MaxY; tileY++)
                {
                    int rowOffset = checked(tileY * (int)tileCountX);
                    for (int tileX = rect.MinX; tileX <= rect.MaxX; tileX++)
                    {
                        int tileIndex = rowOffset + tileX;
                        AddSimpleCandidate(ref destination[tileIndex], checked((uint)volumeIndex));
                    }
                }
            }

            int selectedPrimaryTileCount = 0;
            int selectedSecondaryTileCount = 0;
            int fallbackTileCount = 0;
            for (int i = 0; i < tileCount; i++)
            {
                ref GPUDdgiGatherTile tile = ref destination[i];
                bool hasPrimary = HasUsableCandidateWeight(
                    tile.Flags,
                    TileLocalVolumeValidFlag,
                    tile.BlendWeights.X);
                bool hasSecondary = HasUsableCandidateWeight(
                    tile.Flags,
                    TilePrimaryClipmapValidFlag,
                    tile.BlendWeights.Y);
                if (hasPrimary)
                    selectedPrimaryTileCount++;
                if (hasSecondary)
                    selectedSecondaryTileCount++;

                if (!hasEnabledVolume)
                    continue;

                bool requiresBoundedTableFallback = (tile.Flags & TileFallbackFlag) != 0u;
                if (!hasPrimary)
                {
                    // A valid Simple-DDGI layout may be offscreen or projection
                    // ambiguous for a tile.  Mark this explicitly so diagnostics
                    // distinguish a safe bounded-table fallback from a disabled GI
                    // path.
                    tile.Flags |= TileFallbackFlag;
                    tile.BlendWeights.W = 1.0f;
                    requiresBoundedTableFallback = true;
                }

                if (requiresBoundedTableFallback)
                    fallbackTileCount++;
            }

            GPUDdgiGatherTileHeader header = new()
            {
                TileCountX = tileCountX,
                TileCountY = tileCountY,
                TileSize = TileSize,
                Flags = hasEnabledVolume && tileCount > 0
                    ? HeaderEnabledFlag | HeaderSimpleDdgiFlag
                    : 0u
            };
            return new BuildResult(
                header,
                tileCount,
                selectedPrimaryTileCount,
                selectedSecondaryTileCount,
                fallbackTileCount);
        }

        internal readonly record struct BuildResult(
            GPUDdgiGatherTileHeader Header,
            int TileCount,
            int SelectedLocalTileCount,
            int SelectedClipmapTileCount,
            int FallbackTileCount);

        public readonly record struct DdgiGatherSupportReadiness(
            float Local,
            float PrimaryClipmap,
            float SecondaryClipmap)
        {
            public static DdgiGatherSupportReadiness Steady { get; } = new(1.0f, 1.0f, 1.0f);

            public DdgiGatherSupportReadiness NormalizeForRuntime()
            {
                return new DdgiGatherSupportReadiness(
                    ClampRuntimeReadiness(Local),
                    ClampRuntimeReadiness(PrimaryClipmap),
                    ClampRuntimeReadiness(SecondaryClipmap));
            }

            public DdgiGatherSupportReadiness NormalizeOrSteady()
            {
                if (Local == 0.0f && PrimaryClipmap == 0.0f && SecondaryClipmap == 0.0f)
                    return Steady;

                return new DdgiGatherSupportReadiness(
                    ClampLegacyReadiness(Local),
                    ClampLegacyReadiness(PrimaryClipmap),
                    ClampLegacyReadiness(SecondaryClipmap));
            }

            private static float ClampRuntimeReadiness(float value)
            {
                if (!float.IsFinite(value))
                    return 0.0f;
                return Math.Clamp(value, 0.0f, 1.0f);
            }

            private static float ClampLegacyReadiness(float value)
            {
                if (!float.IsFinite(value))
                    return 1.0f;
                return Math.Clamp(value, 0.0f, 1.0f);
            }
        }

        private static bool HasUsableCandidateWeight(uint flags, uint candidateFlag, float weight)
        {
            return (flags & candidateFlag) != 0u &&
                   float.IsFinite(weight) &&
                   weight > MinCandidateBlendWeight;
        }

        private static uint CalculateTileCount(uint pixels) =>
            Math.Max(1u, (pixels + TileSize - 1u) / TileSize);

        private static void AddSimpleCandidate(ref GPUDdgiGatherTile tile, uint volumeIndex)
        {
            // The input volume table is already sorted by authored priority,
            // purpose, spacing, and source ordinal.  Never replace an earlier
            // candidate: doing so would make a screen projection artifact alter
            // the layout compiler's receiver-ownership order.
            if (tile.LocalVolumeIndex == InvalidVolumeIndex)
            {
                tile.LocalVolumeIndex = volumeIndex;
                tile.Flags |= TileLocalVolumeValidFlag;
                tile.BlendWeights.X = 1.0f;
                return;
            }

            if (tile.PrimaryClipmapVolumeIndex == InvalidVolumeIndex)
            {
                tile.PrimaryClipmapVolumeIndex = volumeIndex;
                tile.Flags |= TilePrimaryClipmapValidFlag | TileSecondaryClipmapValidFlag;
                // Y is the second candidate's availability. Z is an explicit
                // transition/recovery hint for visual heatmaps and captures.
                tile.BlendWeights.Y = 1.0f;
                tile.BlendWeights.Z = 1.0f;
                return;
            }

            // The Simple-DDGI tile hot path is intentionally restricted to two
            // candidates. The table is sorted in deterministic ownership order,
            // so the retained entries remain the two highest priorities. Record
            // the overflow for capture heatmaps without
            // turning every nested-ring tile into a full-table lookup. If the
            // retained metadata is insufficient at a receiver, the shader's
            // unbiased validation naturally uses its existing bounded fallback.
            tile.Flags |= TileSimpleCandidateOverflowFlag;
        }

        private static bool TryGetSimpleVolumeBounds(in GPUSimpleDdgiVolume volume, out BoundingBox bounds)
        {
            Vector3 min = new(
                volume.WorldMinAndEdgeFade.X,
                volume.WorldMinAndEdgeFade.Y,
                volume.WorldMinAndEdgeFade.Z);
            Vector3 max = new(
                volume.WorldMaxAndKind.X,
                volume.WorldMaxAndKind.Y,
                volume.WorldMaxAndKind.Z);
            if (!IsFinite(min.X) || !IsFinite(min.Y) || !IsFinite(min.Z) ||
                !IsFinite(max.X) || !IsFinite(max.Y) || !IsFinite(max.Z) ||
                max.X < min.X || max.Y < min.Y || max.Z < min.Z)
            {
                bounds = default;
                return false;
            }

            bounds = new BoundingBox(min, max);
            return true;
        }

        private static void SelectClipmapCandidates(
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> metadata,
            out uint primaryClipmap,
            out uint secondaryClipmap,
            out float secondaryBlend)
        {
            primaryClipmap = InvalidVolumeIndex;
            secondaryClipmap = InvalidVolumeIndex;
            secondaryBlend = 0.0f;
            int primaryCascade = int.MaxValue;
            int secondaryCascade = int.MaxValue;

            for (int i = 0; i < metadata.Count; i++)
            {
                DdgiProbeVolumeRuntimeMetadata volume = metadata[i];
                if (volume.Kind != DdgiProbeVolumeKind.CameraClipmap)
                    continue;

                int cascade = volume.CascadeIndex < 0 ? int.MaxValue - 1 : volume.CascadeIndex;
                if (cascade < primaryCascade)
                {
                    secondaryClipmap = primaryClipmap;
                    secondaryCascade = primaryCascade;
                    primaryClipmap = checked((uint)i);
                    primaryCascade = cascade;
                    secondaryBlend = Math.Clamp(volume.EdgeBlendFraction, 0.0f, 1.0f);
                }
                else if (cascade < secondaryCascade)
                {
                    secondaryClipmap = checked((uint)i);
                    secondaryCascade = cascade;
                }
            }

            if (secondaryClipmap == InvalidVolumeIndex)
                secondaryBlend = 0.0f;
        }

        private static bool TryProjectVolumeTiles(
            BoundingBox bounds,
            Matrix4x4 viewProjection,
            uint screenWidth,
            uint screenHeight,
            uint tileCountX,
            uint tileCountY,
            out TileRect rect)
        {
            rect = default;
            if (tileCountX == 0 || tileCountY == 0)
                return false;

            Vector3 min = bounds.Min;
            Vector3 max = bounds.Max;
            Span<Vector3> corners = stackalloc Vector3[8]
            {
                new(min.X, min.Y, min.Z),
                new(max.X, min.Y, min.Z),
                new(min.X, max.Y, min.Z),
                new(max.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z),
                new(max.X, min.Y, max.Z),
                new(min.X, max.Y, max.Z),
                new(max.X, max.Y, max.Z)
            };

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector4 clip = TransformHomogeneous(corners[i], viewProjection);
                if (!IsFinite(clip.X) || !IsFinite(clip.Y) || !IsFinite(clip.W))
                    continue;

                if (clip.W <= 0.0001f)
                {
                    rect = new TileRect(0, 0, checked((int)tileCountX - 1), checked((int)tileCountY - 1));
                    return true;
                }

                float invW = 1.0f / clip.W;
                float ndcX = clip.X * invW;
                float ndcY = clip.Y * invW;
                minX = MathF.Min(minX, ndcX);
                minY = MathF.Min(minY, ndcY);
                maxX = MathF.Max(maxX, ndcX);
                maxY = MathF.Max(maxY, ndcY);
            }

            if (!IsFinite(minX) || !IsFinite(minY) || !IsFinite(maxX) || !IsFinite(maxY))
                return false;
            if (maxX < -1.0f || minX > 1.0f || maxY < -1.0f || minY > 1.0f)
                return false;

            float width = Math.Max(screenWidth, 1u);
            float height = Math.Max(screenHeight, 1u);
            float minPixelX = ((minX * 0.5f) + 0.5f) * width;
            float maxPixelX = ((maxX * 0.5f) + 0.5f) * width;
            float minPixelY = ((minY * 0.5f) + 0.5f) * height;
            float maxPixelY = ((maxY * 0.5f) + 0.5f) * height;

            rect = new TileRect(
                ClampTileIndex(MathF.Floor(minPixelX / TileSize), tileCountX),
                ClampTileIndex(MathF.Floor(minPixelY / TileSize), tileCountY),
                ClampTileIndex(MathF.Floor(maxPixelX / TileSize), tileCountX),
                ClampTileIndex(MathF.Floor(maxPixelY / TileSize), tileCountY));
            return rect.MinX <= rect.MaxX && rect.MinY <= rect.MaxY;
        }

        private static int ClampTileIndex(float value, uint tileCount)
        {
            if (tileCount == 0)
                return 0;

            return Math.Clamp((int)value, 0, checked((int)tileCount - 1));
        }

        private static Vector4 TransformHomogeneous(Vector3 position, Matrix4x4 matrix) =>
            new(
                position.X * matrix.M11 + position.Y * matrix.M21 + position.Z * matrix.M31 + matrix.M41,
                position.X * matrix.M12 + position.Y * matrix.M22 + position.Z * matrix.M32 + matrix.M42,
                position.X * matrix.M13 + position.Y * matrix.M23 + position.Z * matrix.M33 + matrix.M43,
                position.X * matrix.M14 + position.Y * matrix.M24 + position.Z * matrix.M34 + matrix.M44);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void EnsureCapacity(int tileCount)
        {
            if (_tileScratch.Length < tileCount)
                _tileScratch = new GPUDdgiGatherTile[tileCount];

            ulong requiredSize = Math.Max(MinBufferSize, checked(HeaderSize + TileStride * (ulong)Math.Max(tileCount, 1)));
            if (_buffer.IsValid && _bufferSize >= requiredSize)
                return;

            if (_buffer.IsValid)
                RetireBufferResource(_buffer);

            _bufferSize = requiredSize;
            _buffer = _bufferManager.CreateDeviceBuffer(
                _bufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Gather Tile Buffer");
            _registeredBindlessHeap?.RegisterStorageBuffer(
                BindlessIndex.DdgiGatherTileBuffer,
                _bufferManager.GetBuffer(_buffer),
                0,
                _bufferSize);
        }

        private void BeginFrameResourceRetirement()
        {
            _frameSerial++;
            DrainRetiredResources(force: false);
        }

        private void RetireBufferResource(BufferHandle buffer)
        {
            if (!buffer.IsValid)
                return;

            _retiredBuffers.Add(new RetiredBufferResource(
                buffer,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
        }

        private void DrainRetiredResources(bool force)
        {
            for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            {
                RetiredBufferResource retired = _retiredBuffers[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBuffers.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_buffer.IsValid)
                _bufferManager.DestroyBuffer(_buffer);
            DrainRetiredResources(force: true);
            _disposed = true;
        }

        private readonly record struct TileRect(int MinX, int MinY, int MaxX, int MaxY);
        private readonly record struct RetiredBufferResource(BufferHandle Buffer, ulong RetireAfterFrameSerial);
    }
}
