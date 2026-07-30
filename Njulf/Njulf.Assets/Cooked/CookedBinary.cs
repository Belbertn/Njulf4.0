using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Njulf.Assets.Cooked;

public sealed class CookedAssetWriter : IDisposable
{
    private const int Alignment = 16;
    private readonly FileStream _stream;
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly CookedAssetKind _kind;
    private readonly CookedFormatVersion _version;
    private readonly uint _toolVersion;
    private readonly uint _flags;
    private readonly ulong _sourceHash;
    private readonly ulong _settingsHash;
    private readonly ulong _dependencyHash;
    private readonly List<CookedSectionEntry> _sections = new();
    private readonly HashSet<uint> _sectionIds = new();
    private ulong _cumulativeUncompressedBytes;
    private bool _completed;

    public CookedAssetWriter(
        string path,
        CookedAssetKind kind,
        ulong sourceHash = 0,
        ulong importSettingsHash = 0,
        ulong dependencyListHash = 0,
        uint buildToolVersion = 1,
        uint flags = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _temporaryPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_path)!,
            $".{System.IO.Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        _kind = kind;
        _version = CookedFormatVersions.For(kind);
        _toolVersion = buildToolVersion;
        _flags = flags;
        _sourceHash = sourceHash;
        _settingsHash = importSettingsHash;
        _dependencyHash = dependencyListHash;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 128, FileOptions.SequentialScan);
        _stream.Write(new byte[CookedAssetHeader.Size]);
    }

    public void WriteSection(uint sectionId, CookedSectionFlags flags, ReadOnlySpan<byte> data)
    {
        ValidateSectionAdmission(sectionId, checked((ulong)data.Length));
        CookedCompression compression = (CookedCompression)(((uint)flags >> 8) & 0xff);
        if (compression is CookedCompression.MeshoptVertex or CookedCompression.MeshoptIndex or CookedCompression.MeshoptIndexSequence)
            throw new NotSupportedException($"Use a typed meshoptimizer section writer for '{compression}'.");
        byte[] encoded = CookedCompressionCodec.Compress(data, compression);
        if (compression != CookedCompression.None && encoded.Length >= data.Length)
        {
            compression = CookedCompression.None;
            encoded = data.ToArray();
            flags = WithCompression(flags, compression);
        }
        WriteAdmittedSection(sectionId, flags, data, encoded);
    }

    public void WriteSection<T>(uint sectionId, CookedSectionFlags flags, ReadOnlySpan<T> data) where T : unmanaged =>
        WriteSection(sectionId, flags, MemoryMarshal.AsBytes(data));

    public void WriteMeshoptVertexSection<T>(uint sectionId, CookedSectionFlags flags, ReadOnlySpan<T> data) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        ValidateSectionAdmission(sectionId, checked((ulong)bytes.Length));
        byte[] encoded = MeshOptimizerCodec.EncodeVertexBuffer(bytes, data.Length, Marshal.SizeOf<T>());
        WriteAdmittedSection(sectionId, WithCompression(flags, CookedCompression.MeshoptVertex), bytes, encoded);
    }

    public void WriteMeshoptIndexSection(uint sectionId, CookedSectionFlags flags, ReadOnlySpan<uint> data, int vertexCount)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        ValidateSectionAdmission(sectionId, checked((ulong)bytes.Length));
        byte[] encoded = MeshOptimizerCodec.EncodeIndexBuffer(data, vertexCount, sequence: false);
        WriteAdmittedSection(sectionId, WithCompression(flags, CookedCompression.MeshoptIndex), bytes, encoded);
    }

    public void WriteMeshoptIndexSequenceSection(uint sectionId, CookedSectionFlags flags, ReadOnlySpan<uint> data, int vertexCount)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        ValidateSectionAdmission(sectionId, checked((ulong)bytes.Length));
        byte[] encoded = MeshOptimizerCodec.EncodeIndexBuffer(data, vertexCount, sequence: true);
        WriteAdmittedSection(sectionId, WithCompression(flags, CookedCompression.MeshoptIndexSequence), bytes, encoded);
    }

    private void WriteAdmittedSection(
        uint sectionId,
        CookedSectionFlags flags,
        ReadOnlySpan<byte> decoded,
        byte[] encoded)
    {
        CookedCompression compression =
            (CookedCompression)(((uint)flags >> 8) & 0xff);
        if (compression != CookedCompression.None &&
            encoded.Length >= decoded.Length)
        {
            flags = WithCompression(flags, CookedCompression.None);
            encoded = decoded.ToArray();
        }
        ValidateStoredSectionAndProjectedFile(
            sectionId,
            checked((ulong)encoded.Length));
        AlignStream();
        ulong offset = checked((ulong)_stream.Position);
        _stream.Write(encoded);
        _sectionIds.Add(sectionId);
        _cumulativeUncompressedBytes = checked(
            _cumulativeUncompressedBytes + (ulong)decoded.Length);
        _sections.Add(new CookedSectionEntry(sectionId, flags, offset, checked((ulong)encoded.Length), checked((ulong)decoded.Length), XxHash3.HashToUInt64(decoded)));
    }

    private static CookedSectionFlags WithCompression(CookedSectionFlags flags, CookedCompression compression) =>
        (CookedSectionFlags)(((uint)flags & ~(uint)CookedSectionFlags.CompressionMask) | ((uint)compression << 8));

    public void Complete()
    {
        if (_completed)
            return;
        ValidateProjectedAssetLength(
            checked((ulong)_stream.Position),
            checked((uint)_sections.Count));
        AlignStream();
        ulong tableOffset = checked((ulong)_stream.Position);
        Span<byte> entryBytes = stackalloc byte[CookedSectionEntry.Size];
        foreach (CookedSectionEntry section in _sections)
        {
            entryBytes.Clear();
            section.Write(entryBytes);
            _stream.Write(entryBytes);
        }
        var header = new CookedAssetHeader(
            CookedAssetHeader.ExpectedMagic,
            _kind,
            _version.Major,
            _version.Minor,
            CookedAssetHeader.ExpectedEndiannessMarker,
            _toolVersion,
            _flags,
            _sourceHash,
            _settingsHash,
            _dependencyHash,
            checked((uint)_sections.Count),
            tableOffset);
        Span<byte> headerBytes = stackalloc byte[CookedAssetHeader.Size];
        header.Write(headerBytes);
        _stream.Position = 0;
        _stream.Write(headerBytes);
        try
        {
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            File.Move(_temporaryPath, _path, overwrite: true);
            _completed = true;
        }
        catch
        {
            _stream.Dispose();
            if (File.Exists(_temporaryPath))
                File.Delete(_temporaryPath);
            throw;
        }
    }

    private void AlignStream()
    {
        int padding = (int)((Alignment - (_stream.Position % Alignment)) % Alignment);
        if (padding > 0)
            _stream.Write(new byte[padding]);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException($"Cooked asset writer for '{_path}' is already complete.");
    }

    private void ValidateSectionAdmission(
        uint sectionId,
        ulong uncompressedBytes)
    {
        ThrowIfCompleted();
        if (_sectionIds.Contains(sectionId))
        {
            throw new InvalidOperationException(
                $"Section '{CookedSectionIds.ToText(sectionId)}' was already " +
                $"written to '{_path}'.");
        }

        uint maximumSectionCount = _kind == CookedAssetKind.Texture
            ? CookedAssetReader.MaximumTextureMetadataSectionCount
            : CookedAssetReader.MaximumSectionCount;
        if ((uint)_sections.Count >= maximumSectionCount)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' cannot contain more than " +
                $"{maximumSectionCount} sections; the writer mirrors the " +
                "runtime reader limit.");
        }

        ulong maximumSectionBytes = _kind == CookedAssetKind.Texture
            ? CookedAssetReader.MaximumTextureMetadataSectionBytes
            : CookedAssetReader.MaximumSectionUncompressedBytes;
        if (uncompressedBytes > maximumSectionBytes)
        {
            throw new InvalidOperationException(
                $"Section '{CookedSectionIds.ToText(sectionId)}' contains " +
                $"{uncompressedBytes} uncompressed bytes, exceeding the " +
                $"{maximumSectionBytes}-byte runtime limit for {_kind}.");
        }

        ulong maximumCumulativeBytes = _kind == CookedAssetKind.Texture
            ? CookedAssetReader.MaximumTextureMetadataCumulativeBytes
            : CookedAssetReader.MaximumCumulativeUncompressedBytes;
        ulong cumulativeBytes;
        try
        {
            cumulativeBytes = checked(
                _cumulativeUncompressedBytes + uncompressedBytes);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' cumulative uncompressed " +
                "section size overflowed.");
        }
        if (cumulativeBytes > maximumCumulativeBytes)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' would contain " +
                $"{cumulativeBytes} cumulative uncompressed bytes, exceeding " +
                $"the {maximumCumulativeBytes}-byte runtime limit.");
        }
    }

    private void ValidateStoredSectionAndProjectedFile(
        uint sectionId,
        ulong storedBytes)
    {
        ulong maximumStoredBytes = _kind == CookedAssetKind.Texture
            ? CookedAssetReader.MaximumTextureMetadataStoredBytes
            : CookedAssetReader.MaximumSectionStoredBytes;
        if (storedBytes > maximumStoredBytes)
        {
            throw new InvalidOperationException(
                $"Section '{CookedSectionIds.ToText(sectionId)}' stores " +
                $"{storedBytes} bytes, exceeding the {maximumStoredBytes}-byte " +
                $"runtime limit for {_kind}.");
        }

        ulong alignedOffset = AlignUp(checked((ulong)_stream.Position));
        ulong payloadEnd;
        try
        {
            payloadEnd = checked(alignedOffset + storedBytes);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' projected length overflowed.");
        }
        ValidateProjectedAssetLength(
            payloadEnd,
            checked((uint)_sections.Count + 1));
    }

    private void ValidateProjectedAssetLength(
        ulong payloadEnd,
        uint sectionCount)
    {
        ulong finalLength;
        try
        {
            ulong tableOffset = AlignUp(payloadEnd);
            finalLength = checked(
                tableOffset +
                checked((ulong)sectionCount * CookedSectionEntry.Size));
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' projected length overflowed.");
        }
        if (finalLength > (ulong)CookedAssetReader.MaximumAssetBytes)
        {
            throw new InvalidOperationException(
                $"Cooked {_kind} asset '{_path}' would be {finalLength} bytes, " +
                $"exceeding the {CookedAssetReader.MaximumAssetBytes}-byte " +
                "runtime limit.");
        }
    }

    private static ulong AlignUp(ulong value) =>
        checked((value + (Alignment - 1UL)) & ~(Alignment - 1UL));

    public void Dispose()
    {
        _stream.Dispose();
        if (!_completed && File.Exists(_temporaryPath))
            File.Delete(_temporaryPath);
    }
}

public sealed class CookedAssetReader : IDisposable
{
    internal const long MaximumAssetBytes = 1024L * 1024L * 1024L;
    internal const uint MaximumSectionCount = 256;
    internal const ulong MaximumSectionUncompressedBytes =
        512UL * 1024UL * 1024UL;
    internal const ulong MaximumSectionStoredBytes =
        512UL * 1024UL * 1024UL;
    internal const ulong MaximumCumulativeUncompressedBytes =
        1024UL * 1024UL * 1024UL;
    internal const uint MaximumTextureMetadataSectionCount = 8;
    internal const ulong MaximumTextureMetadataSectionBytes =
        2UL * 1024UL * 1024UL;
    internal const ulong MaximumTextureMetadataCumulativeBytes =
        4UL * 1024UL * 1024UL;
    internal const ulong MaximumTextureMetadataStoredBytes =
        8UL * 1024UL * 1024UL;

    private static readonly HashSet<uint> KnownSections = typeof(CookedSectionIds)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(uint))
        .Select(field => (uint)field.GetValue(null)!)
        .ToHashSet();

    private readonly FileStream? _stream;
    private readonly SafeFileHandle? _handle;
    private readonly ReadOnlyMemory<byte>? _content;
    private readonly Dictionary<uint, CookedSectionEntry> _sections;
    private readonly CookedAssetReaderFlags _flags;
    private readonly long _length;
    private readonly MemoryMappedFile? _mapping;

    public CookedAssetReader(string path, CookedAssetKind? expectedKind = null, CookedAssetReaderFlags flags = CookedAssetReaderFlags.None, ulong? expectedSourceHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _flags = flags;
        _content = null;
        try
        {
            _stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CookedAssetFormatException(Path, $"could not open file ({ex.Message})");
        }
        _handle = _stream.SafeFileHandle;
        _length = _stream.Length;
        try
        {
            ValidateAssetLength();
            if (_flags.HasFlag(CookedAssetReaderFlags.PreferMemoryMapped) ||
                _length >= 4 * 1024 * 1024)
            {
                _mapping = MemoryMappedFile.CreateFromFile(
                    _stream,
                    null,
                    0,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    leaveOpen: true);
            }
            Span<byte> headerBytes = stackalloc byte[CookedAssetHeader.Size];
            ReadExactly(headerBytes, 0, "header");
            Header = CookedAssetHeader.Read(headerBytes, Path);
            ValidateHeader(expectedKind, expectedSourceHash);
            _sections = ReadSectionTable();
        }
        catch
        {
            _mapping?.Dispose();
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a reader over an immutable caller-owned byte snapshot. This is
    /// used when signature verification and parsing must be bound to exactly
    /// the same bytes instead of two independently opened path snapshots.
    /// </summary>
    public CookedAssetReader(
        ReadOnlyMemory<byte> content,
        string sourcePath,
        CookedAssetKind? expectedKind = null,
        CookedAssetReaderFlags flags = CookedAssetReaderFlags.None,
        ulong? expectedSourceHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Path = System.IO.Path.GetFullPath(sourcePath);
        _flags = flags;
        _content = content;
        _stream = null;
        _handle = null;
        _mapping = null;
        _length = content.Length;
        ValidateAssetLength();
        Span<byte> headerBytes = stackalloc byte[CookedAssetHeader.Size];
        ReadExactly(headerBytes, 0, "header");
        Header = CookedAssetHeader.Read(headerBytes, Path);
        ValidateHeader(expectedKind, expectedSourceHash);
        _sections = ReadSectionTable();
    }

    public string Path { get; }
    public CookedAssetHeader Header { get; }
    public IReadOnlyCollection<CookedSectionEntry> Sections => _sections.Values;
    public long BytesRead { get; private set; }

    public bool TryGetSection(uint sectionId, out ReadOnlyMemory<byte> data)
    {
        if (!_sections.TryGetValue(sectionId, out CookedSectionEntry entry))
        {
            data = default;
            return false;
        }
        if (entry.Compression is CookedCompression.MeshoptVertex or CookedCompression.MeshoptIndex or CookedCompression.MeshoptIndexSequence)
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(sectionId)}' requires a typed meshoptimizer read");
        var bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)entry.UncompressedSize));
        try
        {
            if (entry.Compression == CookedCompression.None)
            {
                ReadExactly(bytes, checked((long)entry.Offset), $"section '{CookedSectionIds.ToText(entry.SectionId)}'");
                BytesRead += bytes.Length;
            }
            else
            {
                byte[] encoded = ReadStoredSection(entry);
                CookedCompressionCodec.Decompress(encoded, bytes, entry.Compression);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(sectionId)}' could not be decompressed ({ex.Message})");
        }
        VerifyHash(entry, bytes);
        data = bytes;
        return true;
    }

    public ReadOnlyMemory<byte> GetRequiredSection(uint sectionId)
    {
        if (!TryGetSection(sectionId, out ReadOnlyMemory<byte> data))
            throw new CookedAssetFormatException(Path, $"required section '{CookedSectionIds.ToText(sectionId)}' is missing");
        return data;
    }

    public T[] ReadSection<T>(uint sectionId) where T : unmanaged
    {
        if (!_sections.TryGetValue(sectionId, out CookedSectionEntry entry))
            throw new CookedAssetFormatException(Path, $"required section '{CookedSectionIds.ToText(sectionId)}' is missing");
        return ReadTypedSection<T>(entry);
    }

    public bool TryReadSection<T>(uint sectionId, out T[] data) where T : unmanaged
    {
        if (!_sections.TryGetValue(sectionId, out CookedSectionEntry entry))
        {
            data = Array.Empty<T>();
            return false;
        }
        data = ReadTypedSection<T>(entry);
        return true;
    }

    private T[] ReadTypedSection<T>(CookedSectionEntry entry) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        if (entry.UncompressedSize % (ulong)size != 0)
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' size is not a multiple of {typeof(T).Name} ({size} bytes)");
        var result = GC.AllocateUninitializedArray<T>(checked((int)(entry.UncompressedSize / (ulong)size)));
        Span<byte> bytes = MemoryMarshal.AsBytes(result.AsSpan());
        try
        {
            if (entry.Compression == CookedCompression.None)
            {
                ReadExactly(bytes, checked((long)entry.Offset), $"section '{CookedSectionIds.ToText(entry.SectionId)}'");
                BytesRead += bytes.Length;
                VerifyHash(entry, bytes);
                return result;
            }
            byte[] encoded = ReadStoredSection(entry);
            switch (entry.Compression)
            {
                case CookedCompression.MeshoptVertex:
                    MeshOptimizerCodec.DecodeVertexBuffer(encoded, bytes, result.Length, size);
                    break;
                case CookedCompression.MeshoptIndex:
                case CookedCompression.MeshoptIndexSequence:
                    if (typeof(T) != typeof(uint))
                        throw new InvalidDataException($"{entry.Compression} sections must be read as UInt32.");
                    MeshOptimizerCodec.DecodeIndexBuffer(encoded, MemoryMarshal.Cast<T, uint>(result.AsSpan()), entry.Compression == CookedCompression.MeshoptIndexSequence);
                    break;
                default:
                    CookedCompressionCodec.Decompress(encoded, bytes, entry.Compression);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' could not be decompressed ({ex.Message})");
        }
        VerifyHash(entry, bytes);
        return result;
    }

    private byte[] ReadStoredSection(CookedSectionEntry entry)
    {
        var encoded = GC.AllocateUninitializedArray<byte>(checked((int)entry.CompressedSize));
        ReadExactly(encoded, checked((long)entry.Offset), $"section '{CookedSectionIds.ToText(entry.SectionId)}'");
        BytesRead += encoded.Length;
        return encoded;
    }

    private void ValidateHeader(CookedAssetKind? expectedKind, ulong? expectedSourceHash)
    {
        if (Header.Magic != CookedAssetHeader.ExpectedMagic)
            throw new CookedAssetFormatException(Path, $"wrong magic 0x{Header.Magic:x8}");
        if (Header.EndiannessMarker != CookedAssetHeader.ExpectedEndiannessMarker)
            throw new CookedAssetFormatException(Path, $"wrong endianness marker 0x{Header.EndiannessMarker:x8}");
        if (!Enum.IsDefined(Header.AssetKind))
            throw new CookedAssetFormatException(Path, $"unknown asset kind {(ushort)Header.AssetKind}");
        if (expectedKind.HasValue && Header.AssetKind != expectedKind.Value)
            throw new CookedAssetFormatException(Path, $"asset kind is {Header.AssetKind}, expected {expectedKind.Value}");
        CookedFormatVersion supported = CookedFormatVersions.For(Header.AssetKind);
        if (Header.FormatMajor != supported.Major)
            throw new CookedAssetFormatException(Path, $"format major {Header.FormatMajor} is incompatible with supported major {supported.Major}");
        if (Header.FormatMinor > supported.Minor)
            throw new CookedAssetFormatException(Path, $"format minor {Header.FormatMinor} is newer than supported minor {supported.Minor}");
        if (expectedSourceHash.HasValue && Header.SourceHash != expectedSourceHash.Value && _flags.HasFlag(CookedAssetReaderFlags.StrictSourceHash))
            throw new CookedAssetHashException(Path, $"source hash 0x{Header.SourceHash:x16} does not match 0x{expectedSourceHash.Value:x16}");
    }

    private Dictionary<uint, CookedSectionEntry> ReadSectionTable()
    {
        uint maximumSectionCount = Header.AssetKind == CookedAssetKind.Texture
            ? MaximumTextureMetadataSectionCount
            : MaximumSectionCount;
        if (Header.SectionCount > maximumSectionCount)
        {
            throw new CookedAssetFormatException(
                Path,
                $"section count {Header.SectionCount} exceeds the " +
                $"{maximumSectionCount}-section runtime limit for {Header.AssetKind}");
        }

        ulong tableBytes = checked((ulong)Header.SectionCount * CookedSectionEntry.Size);
        if (Header.SectionTableOffset < CookedAssetHeader.Size || Header.SectionTableOffset > (ulong)_length || tableBytes > (ulong)_length - Header.SectionTableOffset)
            throw new CookedAssetFormatException(Path, "section table is outside the file");
        var result = new Dictionary<uint, CookedSectionEntry>(checked((int)Header.SectionCount));
        ulong cumulativeUncompressedBytes = 0;
        ulong maximumCumulativeBytes =
            Header.AssetKind == CookedAssetKind.Texture
                ? MaximumTextureMetadataCumulativeBytes
                : MaximumCumulativeUncompressedBytes;
        Span<byte> entryBytes = stackalloc byte[CookedSectionEntry.Size];
        for (uint i = 0; i < Header.SectionCount; i++)
        {
            ReadExactly(entryBytes, checked((long)Header.SectionTableOffset + (long)i * CookedSectionEntry.Size), $"section table entry {i}");
            CookedSectionEntry entry = CookedSectionEntry.Read(entryBytes);
            ValidateEntry(entry);
            try
            {
                cumulativeUncompressedBytes = checked(
                    cumulativeUncompressedBytes +
                    entry.UncompressedSize);
            }
            catch (OverflowException)
            {
                throw new CookedAssetFormatException(
                    Path,
                    "cumulative uncompressed section size overflowed");
            }
            if (cumulativeUncompressedBytes > maximumCumulativeBytes)
            {
                throw new CookedAssetFormatException(
                    Path,
                    $"cumulative uncompressed section size " +
                    $"{cumulativeUncompressedBytes} bytes exceeds the " +
                    $"{maximumCumulativeBytes}-byte runtime limit for " +
                    $"{Header.AssetKind}");
            }
            if (!result.TryAdd(entry.SectionId, entry))
                throw new CookedAssetFormatException(Path, $"duplicate section '{CookedSectionIds.ToText(entry.SectionId)}'");
            if (!KnownSections.Contains(entry.SectionId) && entry.Flags.HasFlag(CookedSectionFlags.Required))
                throw new CookedAssetFormatException(Path, $"unknown required section '{CookedSectionIds.ToText(entry.SectionId)}'");
        }
        CookedSectionEntry[] nonEmpty = result.Values
            .Where(entry => entry.CompressedSize > 0)
            .OrderBy(entry => entry.Offset)
            .ToArray();
        for (int i = 1; i < nonEmpty.Length; i++)
        {
            ulong previousEnd = checked(nonEmpty[i - 1].Offset + nonEmpty[i - 1].CompressedSize);
            if (nonEmpty[i].Offset < previousEnd)
                throw new CookedAssetFormatException(Path, $"sections '{CookedSectionIds.ToText(nonEmpty[i - 1].SectionId)}' and '{CookedSectionIds.ToText(nonEmpty[i].SectionId)}' overlap");
        }
        return result;
    }

    private void ValidateEntry(CookedSectionEntry entry)
    {
        if ((entry.Offset & 15) != 0)
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' is not 16-byte aligned");
        if (entry.Offset < CookedAssetHeader.Size || entry.Offset > Header.SectionTableOffset)
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' offset is outside the payload area");
        if (entry.CompressedSize > Header.SectionTableOffset - entry.Offset)
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' extends beyond the payload area");
        if (entry.Compression == CookedCompression.None && entry.CompressedSize != entry.UncompressedSize)
            throw new CookedAssetFormatException(Path, $"uncompressed section '{CookedSectionIds.ToText(entry.SectionId)}' has inconsistent sizes");
        if (!Enum.IsDefined(entry.Compression))
            throw new CookedAssetFormatException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' has unknown compression {(byte)entry.Compression}");
        ulong maximumStoredBytes =
            Header.AssetKind == CookedAssetKind.Texture
                ? MaximumTextureMetadataStoredBytes
                : MaximumSectionStoredBytes;
        if (entry.CompressedSize > maximumStoredBytes)
        {
            throw new CookedAssetFormatException(
                Path,
                $"section '{CookedSectionIds.ToText(entry.SectionId)}' " +
                $"stores {entry.CompressedSize} bytes; the runtime limit for " +
                $"{Header.AssetKind} is {maximumStoredBytes} bytes");
        }
        ulong maximumSectionBytes =
            Header.AssetKind == CookedAssetKind.Texture
                ? MaximumTextureMetadataSectionBytes
                : MaximumSectionUncompressedBytes;
        if (entry.UncompressedSize > maximumSectionBytes)
        {
            throw new CookedAssetFormatException(
                Path,
                $"section '{CookedSectionIds.ToText(entry.SectionId)}' " +
                $"declares {entry.UncompressedSize} uncompressed bytes; " +
                $"the runtime limit for {Header.AssetKind} is " +
                $"{maximumSectionBytes} bytes");
        }
    }

    private void ValidateAssetLength()
    {
        if (_length < CookedAssetHeader.Size)
        {
            throw new CookedAssetFormatException(
                Path,
                $"file is shorter than the {CookedAssetHeader.Size}-byte header");
        }
        if (_length > MaximumAssetBytes)
        {
            throw new CookedAssetFormatException(
                Path,
                $"asset length {_length} bytes exceeds the " +
                $"{MaximumAssetBytes}-byte runtime limit");
        }
    }

    private void VerifyHash(CookedSectionEntry entry, ReadOnlySpan<byte> bytes)
    {
        if (_flags.HasFlag(CookedAssetReaderFlags.SkipHashValidation))
            return;
        ulong actual = XxHash3.HashToUInt64(bytes);
        if (actual != entry.ContentHash)
            throw new CookedAssetHashException(Path, $"section '{CookedSectionIds.ToText(entry.SectionId)}' expected 0x{entry.ContentHash:x16}, got 0x{actual:x16}");
    }

    private unsafe void ReadExactly(Span<byte> destination, long offset, string description)
    {
        if (_content is { } content)
        {
            if (offset < 0 ||
                offset > content.Length ||
                destination.Length > content.Length - offset)
            {
                throw new CookedAssetFormatException(
                    Path,
                    $"unexpected end of file while reading {description}");
            }

            content.Span.Slice(checked((int)offset), destination.Length)
                .CopyTo(destination);
            return;
        }

        if (_mapping is not null && destination.Length >= 64 * 1024)
        {
            using MemoryMappedViewAccessor view = _mapping.CreateViewAccessor(offset, destination.Length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                new ReadOnlySpan<byte>(pointer + view.PointerOffset, destination.Length).CopyTo(destination);
                return;
            }
            finally
            {
                if (pointer is not null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        int read = 0;
        while (read < destination.Length)
        {
            int count = RandomAccess.Read(_handle!, destination[read..], offset + read);
            if (count == 0)
                throw new CookedAssetFormatException(Path, $"unexpected end of file while reading {description}");
            read += count;
        }
    }

    public void Dispose()
    {
        _mapping?.Dispose();
        _stream?.Dispose();
    }
}

public sealed class CookedStringTableBuilder
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    private readonly List<string> _strings = new();

    public int Add(string? value)
    {
        value ??= string.Empty;
        if (_indices.TryGetValue(value, out int existing))
            return existing;
        int index = _strings.Count;
        _strings.Add(value);
        _indices.Add(value, index);
        return index;
    }

    public byte[] Build()
    {
        var encoded = _strings.Select(Encoding.UTF8.GetBytes).ToArray();
        int headerSize = sizeof(int) + (_strings.Count + 1) * sizeof(int);
        int byteCount = encoded.Sum(bytes => bytes.Length);
        var result = new byte[checked(headerSize + byteCount)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, 4), _strings.Count);
        int cursor = headerSize;
        for (int i = 0; i < encoded.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4 + i * 4, 4), cursor - headerSize);
            encoded[i].CopyTo(result, cursor);
            cursor += encoded[i].Length;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4 + encoded.Length * 4, 4), byteCount);
        return result;
    }
}

public sealed class CookedStringTable
{
    private readonly string[] _values;
    private CookedStringTable(string[] values) => _values = values;
    public int Count => _values.Length;
    public string this[int index] => (uint)index < (uint)_values.Length ? _values[index] : throw new IndexOutOfRangeException();

    public static CookedStringTable Parse(ReadOnlySpan<byte> data, string path)
    {
        if (data.Length < 8)
            throw new CookedAssetFormatException(path, "string table is truncated");
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[..4]);
        if (count < 0 || count > (data.Length - 4) / 4 - 1)
            throw new CookedAssetFormatException(path, "string table count is invalid");
        int payloadOffset = checked(4 + (count + 1) * 4);
        var values = new string[count];
        int previous = 0;
        for (int i = 0; i <= count; i++)
        {
            int current = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4 + i * 4, 4));
            if (current < previous || current > data.Length - payloadOffset)
                throw new CookedAssetFormatException(path, "string table offsets are invalid");
            if (i > 0)
                values[i - 1] = Encoding.UTF8.GetString(data.Slice(payloadOffset + previous, current - previous));
            previous = current;
        }
        return new CookedStringTable(values);
    }
}

public static class CookedHash
{
    public static ulong Bytes(ReadOnlySpan<byte> data) => XxHash3.HashToUInt64(data);
    public static ulong File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
        var hash = new XxHash3();
        byte[] buffer = GC.AllocateUninitializedArray<byte>(1024 * 128);
        int read;
        while ((read = stream.Read(buffer)) != 0)
            hash.Append(buffer.AsSpan(0, read));
        return hash.GetCurrentHashAsUInt64();
    }

    public static ulong Ordered(IEnumerable<(string Name, ulong Hash)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var hash = new XxHash3();
        hash.Append("NJULF\0ORDERED-HASH\0V2\0"u8);
        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        Span<byte> hashBytes = stackalloc byte[sizeof(ulong)];
        IEnumerable<(string Name, ulong Hash)> normalized = values.Select(
            static item =>
            {
                ArgumentNullException.ThrowIfNull(item.Name);
                return (item.Name.Replace('\\', '/'), item.Hash);
            });
        foreach ((string name, ulong value) in normalized
                     .OrderBy(static item => item.Name, StringComparer.Ordinal)
                     .ThenBy(static item => item.Hash))
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint nameLength = checked((uint)nameBytes.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                lengthBytes,
                nameLength);
            hash.Append(lengthBytes);
            hash.Append(nameBytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                hashBytes,
                value);
            hash.Append(hashBytes);
        }
        return hash.GetCurrentHashAsUInt64();
    }
}
