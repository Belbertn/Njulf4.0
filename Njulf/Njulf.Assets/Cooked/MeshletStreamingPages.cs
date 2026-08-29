using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Njulf.Core.Geometry;

namespace Njulf.Assets.Cooked;

[Flags]
public enum MeshletStreamingPageFlags : uint
{
    None = 0,
    Streamable = 1u << 0,
    Pinned = 1u << 1,
    Lod0 = 1u << 2,
    Lod1 = 1u << 3,
    Lod2 = 1u << 4,
    HierarchyGeometry = 1u << 5,
    Skinned = 1u << 6
}

public sealed record MeshletStreamingPageRecord(
    int PageId,
    int SubMeshIndex,
    int LogicalFirstMeshlet,
    int MeshletCount,
    MeshletStreamingPageFlags Flags,
    int FallbackPageId,
    long DataOffset,
    int StoredBytes,
    int UncompressedBytes,
    CookedCompression Compression,
    uint Crc32,
    ulong ContentHash)
{
    public int FallbackPageCount { get; init; } = 1;
}

public sealed record MeshletStreamingManifest(
    int SchemaVersion,
    int PageSizeBytes,
    string SidecarFileName,
    IReadOnlyList<MeshletStreamingPageRecord> Pages,
    long TotalStoredBytes,
    long TotalUncompressedBytes,
    int PinnedPageCount)
{
    public const int CurrentSchemaVersion = 1;
    public const int ProductionPageSizeBytes = 64 * 1024;
    public const int MaximumPageCount = 262_144;

    public void Validate(string ownerPath)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new CookedAssetFormatException(
                ownerPath,
                $"meshlet page schema {SchemaVersion} is unsupported");
        }
        if (PageSizeBytes != ProductionPageSizeBytes ||
            string.IsNullOrWhiteSpace(SidecarFileName) ||
            Path.IsPathRooted(SidecarFileName) ||
            Path.GetFileName(SidecarFileName) != SidecarFileName)
        {
            throw new CookedAssetFormatException(
                ownerPath,
                "meshlet page manifest has an invalid page size or sidecar path");
        }
        if (Pages is null || Pages.Count == 0 ||
            Pages.Count > MaximumPageCount ||
            PinnedPageCount < 0 || PinnedPageCount > Pages.Count ||
            TotalStoredBytes < 0 || TotalUncompressedBytes < 0)
        {
            throw new CookedAssetFormatException(
                ownerPath,
                "meshlet page manifest contains invalid aggregate counts");
        }

        long storedBytes = 0;
        long uncompressedBytes = 0;
        int pinnedPages = 0;
        long previousDataEnd = 0;
        var nextLogicalMeshlet = new Dictionary<int, int>();
        const MeshletStreamingPageFlags knownFlags =
            MeshletStreamingPageFlags.Streamable |
            MeshletStreamingPageFlags.Pinned |
            MeshletStreamingPageFlags.Lod0 |
            MeshletStreamingPageFlags.Lod1 |
            MeshletStreamingPageFlags.Lod2 |
            MeshletStreamingPageFlags.HierarchyGeometry |
            MeshletStreamingPageFlags.Skinned;
        const MeshletStreamingPageFlags geometryFlags =
            MeshletStreamingPageFlags.Lod0 |
            MeshletStreamingPageFlags.Lod1 |
            MeshletStreamingPageFlags.Lod2 |
            MeshletStreamingPageFlags.HierarchyGeometry;
        long minimumDataOffset = checked(
            MeshletStreamingPageFile.HeaderSize +
            (long)Pages.Count *
            MeshletStreamingPageFile.IndexRecordSize);
        minimumDataOffset = checked(
            (minimumDataOffset + 4095) / 4096 * 4096);
        for (int index = 0; index < Pages.Count; index++)
        {
            MeshletStreamingPageRecord page = Pages[index] ??
                throw new CookedAssetFormatException(
                    ownerPath,
                    $"meshlet streaming page {index} is null");
            MeshletStreamingPageFlags residency = page.Flags &
                (MeshletStreamingPageFlags.Streamable |
                 MeshletStreamingPageFlags.Pinned);
            MeshletStreamingPageFlags geometry = page.Flags &
                geometryFlags;
            if (page.PageId != index || page.SubMeshIndex < 0 ||
                page.LogicalFirstMeshlet < 0 || page.MeshletCount <= 0 ||
                page.FallbackPageId < 0 ||
                page.FallbackPageId >= Pages.Count ||
                page.FallbackPageCount <= 0 ||
                page.FallbackPageId >
                    Pages.Count - page.FallbackPageCount ||
                page.DataOffset < minimumDataOffset ||
                page.DataOffset % 4096 != 0 ||
                page.DataOffset < previousDataEnd ||
                page.StoredBytes <= 0 ||
                page.StoredBytes > PageSizeBytes ||
                page.UncompressedBytes <
                    MeshletStreamingPageCodec.HeaderSize +
                    Marshal.SizeOf<Meshlet>() ||
                page.UncompressedBytes > PageSizeBytes ||
                page.MeshletCount >
                    (page.UncompressedBytes -
                     MeshletStreamingPageCodec.HeaderSize) /
                    Marshal.SizeOf<Meshlet>() ||
                page.Compression is not
                    (CookedCompression.None or CookedCompression.Zstd) ||
                (page.Compression == CookedCompression.None &&
                 page.StoredBytes != page.UncompressedBytes) ||
                (page.Compression == CookedCompression.Zstd &&
                 page.StoredBytes >= page.UncompressedBytes) ||
                (page.Flags & ~knownFlags) != 0 ||
                residency is not (MeshletStreamingPageFlags.Streamable or
                    MeshletStreamingPageFlags.Pinned) ||
                !HasExactlyOneBit((uint)geometry) ||
                ((page.Flags & MeshletStreamingPageFlags.Skinned) != 0 &&
                 residency != MeshletStreamingPageFlags.Pinned))
            {
                throw new CookedAssetFormatException(
                    ownerPath,
                    $"meshlet streaming page {index} is malformed");
            }
            int expectedLogicalMeshlet = nextLogicalMeshlet.GetValueOrDefault(
                page.SubMeshIndex);
            if (page.LogicalFirstMeshlet != expectedLogicalMeshlet)
            {
                throw new CookedAssetFormatException(
                    ownerPath,
                    $"meshlet streaming page {index} breaks its submesh's logical meshlet sequence");
            }
            nextLogicalMeshlet[page.SubMeshIndex] = checked(
                page.LogicalFirstMeshlet + page.MeshletCount);
            if ((page.Flags & MeshletStreamingPageFlags.Pinned) != 0)
                pinnedPages++;
            storedBytes = checked(storedBytes + page.StoredBytes);
            uncompressedBytes = checked(
                uncompressedBytes + page.UncompressedBytes);
            previousDataEnd = checked(page.DataOffset + page.StoredBytes);
        }
        if (storedBytes != TotalStoredBytes ||
            uncompressedBytes != TotalUncompressedBytes ||
            pinnedPages != PinnedPageCount)
        {
            throw new CookedAssetFormatException(
                ownerPath,
                "meshlet page manifest aggregate values do not reconcile");
        }
        for (int index = 0; index < Pages.Count; index++)
        {
            MeshletStreamingPageRecord page = Pages[index];
            bool pinned =
                (page.Flags & MeshletStreamingPageFlags.Pinned) != 0;
            if ((pinned && page.FallbackPageId != page.PageId) ||
                (pinned && page.FallbackPageCount != 1))
            {
                throw new CookedAssetFormatException(
                    ownerPath,
                    $"meshlet streaming page {index} has an invalid pinned fallback");
            }
            MeshletStreamingPageFlags? fallbackGeometry = null;
            for (int fallbackIndex = page.FallbackPageId;
                 fallbackIndex <
                    page.FallbackPageId + page.FallbackPageCount;
                 fallbackIndex++)
            {
                MeshletStreamingPageRecord fallback = Pages[fallbackIndex];
                MeshletStreamingPageFlags geometry = fallback.Flags &
                    geometryFlags;
                if ((fallback.Flags & MeshletStreamingPageFlags.Pinned) == 0 ||
                    fallback.SubMeshIndex != page.SubMeshIndex ||
                    (!pinned &&
                     geometry ==
                        MeshletStreamingPageFlags.HierarchyGeometry) ||
                    (fallbackGeometry.HasValue &&
                     fallbackGeometry.Value != geometry))
                {
                    throw new CookedAssetFormatException(
                        ownerPath,
                        $"meshlet streaming page {index} has an incomplete coarse fallback group");
                }
                fallbackGeometry = geometry;
            }
        }
    }

    private static bool HasExactlyOneBit(uint value) =>
        value != 0 && (value & (value - 1)) == 0;
}

public sealed record MeshletStreamingPagePayload(
    Meshlet[] Meshlets,
    uint[] LocalVertexIndices,
    uint[] LocalTriangleIndices);

public sealed class MeshletStreamingPageBundle
{
    private const int DataAlignment = 4096;
    private readonly EncodedPage[] _pages;

    private MeshletStreamingPageBundle(
        MeshletStreamingManifest manifest,
        EncodedPage[] pages)
    {
        Manifest = manifest;
        _pages = pages;
    }

    public MeshletStreamingManifest Manifest { get; }

    public static MeshletStreamingPageBundle Build(
        CookedMeshPayload payload,
        string sidecarFileName)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarFileName);
        if (Path.GetFileName(sidecarFileName) != sidecarFileName)
        {
            throw new ArgumentException(
                "A meshlet page sidecar name must be a single relative file name.",
                nameof(sidecarFileName));
        }

        var pages = new List<PendingPage>();
        for (int subMeshIndex = 0;
             subMeshIndex < payload.SubMeshes.Count;
             subMeshIndex++)
        {
            CookedSubMeshRecord subMesh =
                payload.SubMeshes[subMeshIndex];
            bool skinned = subMesh.SkinIndex >= 0 ||
                subMesh.SkinningCount != 0;
            bool hasLod1 = subMesh.MeshletLod1Count != 0;
            bool hasLod2 = subMesh.MeshletLod2Count != 0;
            AppendRange(
                payload,
                subMeshIndex,
                payload.MeshletsLod0.AsSpan(
                    subMesh.MeshletOffset,
                    subMesh.MeshletCount),
                logicalFirstMeshlet: 0,
                skinned || (!hasLod1 && !hasLod2)
                    ? MeshletStreamingPageFlags.Pinned |
                      (skinned ? MeshletStreamingPageFlags.Skinned : 0) |
                      MeshletStreamingPageFlags.Lod0
                    : MeshletStreamingPageFlags.Streamable |
                      MeshletStreamingPageFlags.Lod0,
                pages);
            AppendRange(
                payload,
                subMeshIndex,
                payload.MeshletsLod1.AsSpan(
                    subMesh.MeshletLod1Offset,
                    subMesh.MeshletLod1Count),
                subMesh.MeshletCount,
                skinned || !hasLod2
                    ? MeshletStreamingPageFlags.Pinned |
                      (skinned ? MeshletStreamingPageFlags.Skinned : 0) |
                      MeshletStreamingPageFlags.Lod1
                    : MeshletStreamingPageFlags.Streamable |
                      MeshletStreamingPageFlags.Lod1,
                pages);
            AppendRange(
                payload,
                subMeshIndex,
                payload.MeshletsLod2.AsSpan(
                    subMesh.MeshletLod2Offset,
                    subMesh.MeshletLod2Count),
                checked(subMesh.MeshletCount +
                        subMesh.MeshletLod1Count),
                MeshletStreamingPageFlags.Pinned |
                (skinned ? MeshletStreamingPageFlags.Skinned : 0) |
                MeshletStreamingPageFlags.Lod2,
                pages);
            AppendRange(
                payload,
                subMeshIndex,
                payload.HierarchyMeshlets.AsSpan(
                    subMesh.HierarchyMeshletOffset,
                    subMesh.HierarchyMeshletCount),
                checked(subMesh.MeshletCount +
                        subMesh.MeshletLod1Count +
                        subMesh.MeshletLod2Count),
                MeshletStreamingPageFlags.Pinned |
                (skinned ? MeshletStreamingPageFlags.Skinned : 0) |
                MeshletStreamingPageFlags.HierarchyGeometry,
                pages);
        }

        if (pages.Count == 0)
            throw new InvalidOperationException(
                "A cooked mesh must produce at least one streaming page.");

        FallbackRange[] fallbackPages = ResolveFallbackPages(
            payload.SubMeshes.Count,
            pages);
        var encodedPages = new EncodedPage[pages.Count];
        long indexEnd = checked(
            MeshletStreamingPageFile.HeaderSize +
            (long)pages.Count * MeshletStreamingPageFile.IndexRecordSize);
        long dataOffset = AlignUp(indexEnd, DataAlignment);
        long totalStoredBytes = 0;
        long totalUncompressedBytes = 0;
        int pinnedPageCount = 0;
        var records = new MeshletStreamingPageRecord[pages.Count];
        for (int index = 0; index < pages.Count; index++)
        {
            PendingPage page = pages[index];
            byte[] decoded = MeshletStreamingPageCodec.Encode(
                page.Payload);
            byte[] compressed = CookedCompressionCodec.Compress(
                decoded,
                CookedCompression.Zstd);
            CookedCompression compression = CookedCompression.Zstd;
            byte[] stored = compressed;
            if (compressed.Length >= decoded.Length)
            {
                compression = CookedCompression.None;
                stored = decoded;
            }
            uint crc = Crc32.HashToUInt32(decoded);
            ulong contentHash = XxHash64.HashToUInt64(decoded);
            records[index] = new MeshletStreamingPageRecord(
                index,
                page.SubMeshIndex,
                page.LogicalFirstMeshlet,
                page.Payload.Meshlets.Length,
                page.Flags,
                fallbackPages[index].FirstPageId,
                dataOffset,
                stored.Length,
                decoded.Length,
                compression,
                crc,
                contentHash)
            {
                FallbackPageCount = fallbackPages[index].PageCount
            };
            encodedPages[index] = new EncodedPage(stored);
            totalStoredBytes = checked(
                totalStoredBytes + stored.Length);
            totalUncompressedBytes = checked(
                totalUncompressedBytes + decoded.Length);
            if ((page.Flags & MeshletStreamingPageFlags.Pinned) != 0)
                pinnedPageCount++;
            dataOffset = AlignUp(
                checked(dataOffset + stored.Length),
                DataAlignment);
        }

        var manifest = new MeshletStreamingManifest(
            MeshletStreamingManifest.CurrentSchemaVersion,
            MeshletStreamingManifest.ProductionPageSizeBytes,
            sidecarFileName,
            records,
            totalStoredBytes,
            totalUncompressedBytes,
            pinnedPageCount);
        manifest.Validate(sidecarFileName);
        return new MeshletStreamingPageBundle(
            manifest,
            encodedPages);
    }

    /// <summary>
    /// Returns a bundle whose immutable sidecar name is derived from every
    /// exact on-disk byte, including the authenticated header, index metadata,
    /// alignment padding, and stored page payloads. Writing a new package
    /// generation therefore cannot replace the sidecar still referenced by the
    /// previous atomic package generation.
    /// </summary>
    public MeshletStreamingPageBundle WithContentAddressedSidecarName(
        string packageFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFileName);
        string fileName = Path.GetFileName(packageFileName);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] headerAndIndex =
            MeshletStreamingPageFile.CreateHeaderAndIndex(Manifest);
        hash.AppendData(headerAndIndex);
        long position = headerAndIndex.LongLength;
        Span<byte> zeroPadding = stackalloc byte[DataAlignment];
        zeroPadding.Clear();
        foreach (MeshletStreamingPageRecord page in Manifest.Pages)
        {
            if (position > page.DataOffset)
            {
                throw new InvalidOperationException(
                    "Meshlet page offsets overlap their index or a previous page.");
            }
            long paddingBytes = page.DataOffset - position;
            while (paddingBytes > 0)
            {
                int count = checked((int)Math.Min(
                    paddingBytes,
                    zeroPadding.Length));
                hash.AppendData(zeroPadding[..count]);
                paddingBytes -= count;
            }
            byte[] storedBytes = _pages[page.PageId].StoredBytes;
            if (storedBytes.Length != page.StoredBytes)
            {
                throw new InvalidOperationException(
                    $"Meshlet page {page.PageId} no longer matches its immutable manifest.");
            }
            hash.AppendData(storedBytes);
            position = checked(page.DataOffset + storedBytes.LongLength);
        }
        string identifier = Convert.ToHexString(
                hash.GetHashAndReset().AsSpan(0, 16))
            .ToLowerInvariant();
        string sidecarName = $"{fileName}.meshlets-{identifier}.pages";
        MeshletStreamingManifest manifest = Manifest with
        {
            SidecarFileName = sidecarName
        };
        manifest.Validate(fileName);
        return new MeshletStreamingPageBundle(manifest, _pages);
    }

    public void WriteSidecar(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetFileName(path),
                Manifest.SidecarFileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The output path must use the immutable sidecar name authenticated by the manifest.",
                nameof(path));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                MeshletStreamingPageFile.WriteHeaderAndIndex(
                    stream,
                    Manifest);
                foreach (MeshletStreamingPageRecord page in Manifest.Pages)
                {
                    if (stream.Position > page.DataOffset)
                    {
                        throw new InvalidOperationException(
                            "Meshlet page offsets overlap their index or a previous page.");
                    }
                    WriteZeroPadding(
                        stream,
                        page.DataOffset - stream.Position);
                    stream.Write(_pages[page.PageId].StoredBytes);
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void AppendRange(
        CookedMeshPayload payload,
        int subMeshIndex,
        ReadOnlySpan<Meshlet> sourceMeshlets,
        int logicalFirstMeshlet,
        MeshletStreamingPageFlags flags,
        ICollection<PendingPage> pages)
    {
        if (sourceMeshlets.IsEmpty)
            return;
        CookedSubMeshRecord subMesh =
            payload.SubMeshes[subMeshIndex];
        int first = 0;
        while (first < sourceMeshlets.Length)
        {
            var meshlets = new List<Meshlet>();
            var vertexIndices = new List<uint>();
            var triangleIndices = new List<uint>();
            int pageFirst = first;
            while (first < sourceMeshlets.Length)
            {
                Meshlet source = sourceMeshlets[first];
                int projectedBytes = checked(
                    MeshletStreamingPageCodec.HeaderSize +
                    (meshlets.Count + 1) * Marshal.SizeOf<Meshlet>() +
                    (vertexIndices.Count +
                     checked((int)source.LocalVertexCount)) * sizeof(uint) +
                    (triangleIndices.Count +
                     checked((int)source.LocalTriangleCount * 3)) *
                    sizeof(uint));
                if (projectedBytes >
                        MeshletStreamingManifest.ProductionPageSizeBytes &&
                    meshlets.Count != 0)
                {
                    break;
                }
                if (projectedBytes >
                    MeshletStreamingManifest.ProductionPageSizeBytes)
                {
                    throw new InvalidOperationException(
                        $"Submesh '{subMesh.Name}' contains a meshlet larger than one production streaming page.");
                }

                Meshlet rebased = source;
                rebased.LocalVertexOffset = checked(
                    (uint)vertexIndices.Count);
                rebased.LocalTriangleOffset = checked(
                    (uint)triangleIndices.Count);
                meshlets.Add(rebased);
                vertexIndices.AddRange(payload.MeshletVertices.AsSpan(
                    checked(subMesh.MeshletVertexOffset +
                            (int)source.LocalVertexOffset),
                    checked((int)source.LocalVertexCount)).ToArray());
                triangleIndices.AddRange(payload.MeshletTriangles.AsSpan(
                    checked(subMesh.MeshletTriangleOffset +
                            (int)source.LocalTriangleOffset),
                    checked((int)source.LocalTriangleCount * 3)).ToArray());
                first++;
            }

            pages.Add(new PendingPage(
                subMeshIndex,
                checked(logicalFirstMeshlet + pageFirst),
                flags,
                new MeshletStreamingPagePayload(
                    meshlets.ToArray(),
                    vertexIndices.ToArray(),
                    triangleIndices.ToArray())));
        }
    }

    private static FallbackRange[] ResolveFallbackPages(
        int subMeshCount,
        IReadOnlyList<PendingPage> pages)
    {
        var coarseRanges = new FallbackRange[subMeshCount];
        for (int subMeshIndex = 0;
             subMeshIndex < subMeshCount;
             subMeshIndex++)
        {
            MeshletStreamingPageFlags selectedLod = 0;
            foreach (MeshletStreamingPageFlags candidate in new[]
                     {
                         MeshletStreamingPageFlags.Lod2,
                         MeshletStreamingPageFlags.Lod1,
                         MeshletStreamingPageFlags.Lod0
                     })
            {
                if (pages.Any(page =>
                    page.SubMeshIndex == subMeshIndex &&
                    (page.Flags & MeshletStreamingPageFlags.Pinned) != 0 &&
                    (page.Flags & candidate) != 0))
                {
                    selectedLod = candidate;
                    break;
                }
            }
            int first = -1;
            int count = 0;
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                PendingPage page = pages[pageIndex];
                if (page.SubMeshIndex == subMeshIndex &&
                    (page.Flags & MeshletStreamingPageFlags.Pinned) != 0 &&
                    (page.Flags & selectedLod) != 0)
                {
                    if (first < 0)
                        first = pageIndex;
                    count++;
                }
            }
            if (first < 0 || count == 0)
            {
                throw new InvalidOperationException(
                    "Every streamed submesh requires at least one pinned coarse fallback page.");
            }
            coarseRanges[subMeshIndex] = new FallbackRange(first, count);
        }
        var fallback = new FallbackRange[pages.Count];
        for (int index = 0; index < pages.Count; index++)
        {
            fallback[index] =
                (pages[index].Flags & MeshletStreamingPageFlags.Pinned) != 0
                    ? new FallbackRange(index, 1)
                    : coarseRanges[pages[index].SubMeshIndex];
        }
        return fallback;
    }

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static void WriteZeroPadding(Stream stream, long bytes)
    {
        Span<byte> zeros = stackalloc byte[4096];
        while (bytes > 0)
        {
            int count = checked((int)Math.Min(bytes, zeros.Length));
            stream.Write(zeros[..count]);
            bytes -= count;
        }
    }

    private sealed record PendingPage(
        int SubMeshIndex,
        int LogicalFirstMeshlet,
        MeshletStreamingPageFlags Flags,
        MeshletStreamingPagePayload Payload);

    private sealed record EncodedPage(byte[] StoredBytes);

    private readonly record struct FallbackRange(
        int FirstPageId,
        int PageCount);
}

public static class MeshletStreamingPageCodec
{
    internal const int HeaderSize = 24;
    private const uint Magic = 0x3247504d; // MPG2
    private const uint Version = 1;

    public static byte[] Encode(MeshletStreamingPagePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload, static detail =>
            new ArgumentException(detail, nameof(payload)));
        int meshletBytes = checked(
            payload.Meshlets.Length * Marshal.SizeOf<Meshlet>());
        int vertexBytes = checked(
            payload.LocalVertexIndices.Length * sizeof(uint));
        int triangleBytes = checked(
            payload.LocalTriangleIndices.Length * sizeof(uint));
        int totalBytes = checked(
            HeaderSize + meshletBytes + vertexBytes + triangleBytes);
        if (totalBytes >
            MeshletStreamingManifest.ProductionPageSizeBytes)
        {
            throw new ArgumentException(
                "A decoded meshlet page exceeds the production page size.",
                nameof(payload));
        }

        var result = new byte[totalBytes];
        Span<byte> header = result.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], Version);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[8..12], payload.Meshlets.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[12..16], payload.LocalVertexIndices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[16..20], payload.LocalTriangleIndices.Length);
        MemoryMarshal.AsBytes(payload.Meshlets.AsSpan()).CopyTo(
            result.AsSpan(HeaderSize, meshletBytes));
        MemoryMarshal.AsBytes(payload.LocalVertexIndices.AsSpan()).CopyTo(
            result.AsSpan(HeaderSize + meshletBytes, vertexBytes));
        MemoryMarshal.AsBytes(payload.LocalTriangleIndices.AsSpan()).CopyTo(
            result.AsSpan(
                HeaderSize + meshletBytes + vertexBytes,
                triangleBytes));
        return result;
    }

    public static MeshletStreamingPagePayload Decode(
        ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize ||
            data.Length >
                MeshletStreamingManifest.ProductionPageSizeBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]) != Version ||
            data[20..HeaderSize].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                "Meshlet streaming page header is invalid.");
        }
        int meshletCount = BinaryPrimitives.ReadInt32LittleEndian(
            data[8..12]);
        int vertexCount = BinaryPrimitives.ReadInt32LittleEndian(
            data[12..16]);
        int triangleCount = BinaryPrimitives.ReadInt32LittleEndian(
            data[16..20]);
        if (meshletCount <= 0 ||
            meshletCount >
                MeshletStreamingManifest.ProductionPageSizeBytes /
                Marshal.SizeOf<Meshlet>() ||
            vertexCount < 0 ||
            vertexCount >
                MeshletStreamingManifest.ProductionPageSizeBytes /
                sizeof(uint) ||
            triangleCount < 0 ||
            triangleCount >
                MeshletStreamingManifest.ProductionPageSizeBytes /
                sizeof(uint))
        {
            throw new InvalidDataException("Meshlet page counts are invalid.");
        }
        int meshletBytes = checked(
            meshletCount * Marshal.SizeOf<Meshlet>());
        int vertexBytes = checked(vertexCount * sizeof(uint));
        int triangleBytes = checked(triangleCount * sizeof(uint));
        int expectedBytes = checked(
            HeaderSize + meshletBytes + vertexBytes + triangleBytes);
        if (expectedBytes != data.Length)
            throw new InvalidDataException("Meshlet page byte count is invalid.");

        var meshlets = new Meshlet[meshletCount];
        var vertices = new uint[vertexCount];
        var triangles = new uint[triangleCount];
        data.Slice(HeaderSize, meshletBytes).CopyTo(
            MemoryMarshal.AsBytes(meshlets.AsSpan()));
        data.Slice(HeaderSize + meshletBytes, vertexBytes).CopyTo(
            MemoryMarshal.AsBytes(vertices.AsSpan()));
        data.Slice(
            HeaderSize + meshletBytes + vertexBytes,
            triangleBytes).CopyTo(
                MemoryMarshal.AsBytes(triangles.AsSpan()));
        var payload = new MeshletStreamingPagePayload(
            meshlets,
            vertices,
            triangles);
        ValidatePayload(payload, static detail =>
            new InvalidDataException(detail));
        return payload;
    }

    private static void ValidatePayload(
        MeshletStreamingPagePayload payload,
        Func<string, Exception> createException)
    {
        if (payload.Meshlets is null ||
            payload.LocalVertexIndices is null ||
            payload.LocalTriangleIndices is null ||
            payload.Meshlets.Length == 0)
        {
            throw createException(
                "A meshlet page must contain non-null, non-empty meshlet data.");
        }

        uint nextVertex = 0;
        uint nextTriangleIndex = 0;
        for (int meshletIndex = 0;
             meshletIndex < payload.Meshlets.Length;
             meshletIndex++)
        {
            Meshlet meshlet = payload.Meshlets[meshletIndex];
            if (meshlet.LocalVertexCount is 0 or >
                    MeshletBuilder.DefaultMaxVerticesPerMeshlet ||
                meshlet.LocalTriangleCount is 0 or >
                    MeshletBuilder.DefaultMaxTrianglesPerMeshlet ||
                meshlet.LocalVertexOffset != nextVertex ||
                meshlet.LocalTriangleOffset != nextTriangleIndex)
            {
                throw createException(
                    $"Meshlet page record {meshletIndex} has invalid or non-canonical local ranges.");
            }
            uint triangleIndexCount = checked(
                meshlet.LocalTriangleCount * 3u);
            nextVertex = checked(nextVertex + meshlet.LocalVertexCount);
            nextTriangleIndex = checked(
                nextTriangleIndex + triangleIndexCount);
            if (nextVertex > payload.LocalVertexIndices.Length ||
                nextTriangleIndex > payload.LocalTriangleIndices.Length)
            {
                throw createException(
                    $"Meshlet page record {meshletIndex} exceeds its local streams.");
            }
            int triangleStart = checked(
                (int)meshlet.LocalTriangleOffset);
            int triangleEnd = checked((int)nextTriangleIndex);
            for (int triangleIndex = triangleStart;
                 triangleIndex < triangleEnd;
                 triangleIndex++)
            {
                if (payload.LocalTriangleIndices[triangleIndex] >=
                    meshlet.LocalVertexCount)
                {
                    throw createException(
                        $"Meshlet page record {meshletIndex} contains an out-of-range local triangle vertex.");
                }
            }
        }
        if (nextVertex != payload.LocalVertexIndices.Length ||
            nextTriangleIndex != payload.LocalTriangleIndices.Length)
        {
            throw createException(
                "Meshlet page local streams contain unreferenced trailing data.");
        }
    }
}

public sealed class MeshletStreamingPageFile : IDisposable
{
    internal const int HeaderSize = 64;
    internal const int IndexRecordSize = 64;
    private const ulong Magic = 0x32454741504d4a4eUL; // NJMPAGE2
    private const uint Version = 1;
    private readonly SafeFileHandle _handle;
    private readonly Dictionary<int, MeshletStreamingPageRecord> _pages;
    private bool _disposed;

    private MeshletStreamingPageFile(
        string path,
        SafeFileHandle handle,
        MeshletStreamingManifest manifest)
    {
        Path = path;
        _handle = handle;
        Manifest = manifest;
        _pages = manifest.Pages.ToDictionary(
            static page => page.PageId);
    }

    public string Path { get; }
    public MeshletStreamingManifest Manifest { get; }

    public static MeshletStreamingPageFile Open(
        string meshPackagePath,
        MeshletStreamingManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshPackagePath);
        ArgumentNullException.ThrowIfNull(manifest);
        meshPackagePath = System.IO.Path.GetFullPath(meshPackagePath);
        manifest.Validate(meshPackagePath);
        string directory = System.IO.Path.GetDirectoryName(
            meshPackagePath)!;
        string path = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(directory, manifest.SidecarFileName));
        string relative = System.IO.Path.GetRelativePath(directory, path);
        if (relative == ".." || relative.StartsWith(
                ".." + System.IO.Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new CookedAssetFormatException(
                meshPackagePath,
                "meshlet page sidecar escapes its package directory");
        }

        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            ValidateHeaderAndIndex(handle, path, manifest);
            return new MeshletStreamingPageFile(
                path,
                handle,
                manifest);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async ValueTask<byte[]> ReadPageAsync(
        int pageId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pages.TryGetValue(
                pageId,
                out MeshletStreamingPageRecord? page))
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }
        var stored = GC.AllocateUninitializedArray<byte>(
            page.StoredBytes);
        await ReadExactlyAsync(
            _handle,
            stored,
            page.DataOffset,
            cancellationToken).ConfigureAwait(false);
        var decoded = GC.AllocateUninitializedArray<byte>(
            page.UncompressedBytes);
        try
        {
            CookedCompressionCodec.Decompress(
                stored,
                decoded,
                page.Compression);
        }
        catch (Exception ex) when (
            ex is not StackOverflowException and
            not OutOfMemoryException)
        {
            throw new InvalidDataException(
                $"Meshlet streaming page {pageId} could not be decoded.",
                ex);
        }
        if (Crc32.HashToUInt32(decoded) != page.Crc32 ||
            XxHash64.HashToUInt64(decoded) != page.ContentHash)
        {
            throw new InvalidDataException(
                $"Meshlet streaming page {pageId} failed content authentication.");
        }
        return decoded;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _handle.Dispose();
    }

    internal static void WriteHeaderAndIndex(
        Stream stream,
        MeshletStreamingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(CreateHeaderAndIndex(manifest));
    }

    internal static byte[] CreateHeaderAndIndex(
        MeshletStreamingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var result = new byte[checked(
            HeaderSize + manifest.Pages.Count * IndexRecordSize)];
        Span<byte> header = result.AsSpan(0, HeaderSize);
        Span<byte> indexBytes = result.AsSpan(HeaderSize);
        for (int index = 0; index < manifest.Pages.Count; index++)
        {
            WriteIndexRecord(
                indexBytes.Slice(index * IndexRecordSize,
                    IndexRecordSize),
                manifest.Pages[index]);
        }
        ulong indexHash = XxHash64.HashToUInt64(indexBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0..8], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], Version);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[12..16], manifest.PageSizeBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[16..20], manifest.Pages.Count);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[20..24], IndexRecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..32], HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(
            header[32..40],
            manifest.Pages.Min(static page => page.DataOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[40..48], indexHash);
        return result;
    }

    private static void ValidateHeaderAndIndex(
        SafeFileHandle handle,
        string path,
        MeshletStreamingManifest manifest)
    {
        var header = new byte[HeaderSize];
        ReadExactly(handle, header, 0);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[0..8]) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]) != Version ||
            BinaryPrimitives.ReadInt32LittleEndian(header[12..16]) !=
                manifest.PageSizeBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(header[16..20]) !=
                manifest.Pages.Count ||
            BinaryPrimitives.ReadInt32LittleEndian(header[20..24]) !=
                IndexRecordSize ||
            BinaryPrimitives.ReadInt64LittleEndian(header[24..32]) !=
                HeaderSize ||
            BinaryPrimitives.ReadInt64LittleEndian(header[32..40]) !=
                manifest.Pages.Min(static page => page.DataOffset) ||
            header.AsSpan(48).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"Meshlet page sidecar '{path}' has an invalid header.");
        }
        var indexBytes = new byte[checked(
            manifest.Pages.Count * IndexRecordSize)];
        ReadExactly(handle, indexBytes, HeaderSize);
        if (XxHash64.HashToUInt64(indexBytes) !=
            BinaryPrimitives.ReadUInt64LittleEndian(header[40..48]))
        {
            throw new InvalidDataException(
                $"Meshlet page sidecar '{path}' has a corrupt index.");
        }
        for (int index = 0; index < manifest.Pages.Count; index++)
        {
            MeshletStreamingPageRecord decoded = ReadIndexRecord(
                indexBytes.AsSpan(index * IndexRecordSize,
                    IndexRecordSize));
            if (decoded != manifest.Pages[index])
            {
                throw new InvalidDataException(
                    $"Meshlet page sidecar '{path}' index does not match its authenticated package manifest.");
            }
        }
        long fileLength = RandomAccess.GetLength(handle);
        MeshletStreamingPageRecord finalPage = manifest.Pages[^1];
        long requiredLength = checked(
            finalPage.DataOffset + finalPage.StoredBytes);
        if (fileLength != requiredLength)
        {
            throw new InvalidDataException(
                $"Meshlet page sidecar '{path}' has an invalid file length.");
        }
    }

    private static void WriteIndexRecord(
        Span<byte> destination,
        MeshletStreamingPageRecord page)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[0..4], page.PageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], (uint)page.Flags);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], page.SubMeshIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], page.LogicalFirstMeshlet);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..20], page.MeshletCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..24], page.FallbackPageId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..32], page.DataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..36], page.StoredBytes);
        BinaryPrimitives.WriteInt32LittleEndian(destination[36..40], page.UncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..44], page.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..48], (uint)page.Compression);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..56], page.ContentHash);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[56..60], page.FallbackPageCount);
    }

    private static MeshletStreamingPageRecord ReadIndexRecord(
        ReadOnlySpan<byte> source)
    {
        if (source.Length != IndexRecordSize ||
            source[60..64].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                "Meshlet page sidecar index has non-canonical reserved bytes.");
        }
        return new MeshletStreamingPageRecord(
            BinaryPrimitives.ReadInt32LittleEndian(source[0..4]),
            BinaryPrimitives.ReadInt32LittleEndian(source[8..12]),
            BinaryPrimitives.ReadInt32LittleEndian(source[12..16]),
            BinaryPrimitives.ReadInt32LittleEndian(source[16..20]),
            (MeshletStreamingPageFlags)
                BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
            BinaryPrimitives.ReadInt32LittleEndian(source[20..24]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..32]),
            BinaryPrimitives.ReadInt32LittleEndian(source[32..36]),
            BinaryPrimitives.ReadInt32LittleEndian(source[36..40]),
            (CookedCompression)
                BinaryPrimitives.ReadUInt32LittleEndian(source[44..48]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[40..44]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[48..56]))
        {
            FallbackPageCount =
                BinaryPrimitives.ReadInt32LittleEndian(source[56..60])
        };
    }

    private static void ReadExactly(
        SafeFileHandle handle,
        Memory<byte> destination,
        long offset)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = RandomAccess.Read(
                handle,
                destination.Span[completed..],
                checked(offset + completed));
            if (read == 0)
                throw new EndOfStreamException();
            completed += read;
        }
    }

    private static async ValueTask ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long offset,
        CancellationToken cancellationToken)
    {
        int completed = 0;
        while (completed < destination.Length)
        {
            int read = await RandomAccess.ReadAsync(
                handle,
                destination[completed..],
                checked(offset + completed),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            completed += read;
        }
    }
}
