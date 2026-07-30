using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Njulf.Assets.Cooked;

public enum CookedAssetKind : ushort
{
    Model = 1,
    Mesh = 2,
    Material = 3,
    Texture = 4,
    Animation = 5
}

public enum CookedCompression : byte
{
    None = 0,
    Lz4 = 1,
    Zstd = 2,
    MeshoptVertex = 3,
    MeshoptIndex = 4,
    MeshoptIndexSequence = 5
}

[Flags]
public enum CookedSectionFlags : uint
{
    None = 0,
    Required = 1u << 0,
    CompressionMask = 0xffu << 8,
    Lz4 = (uint)CookedCompression.Lz4 << 8,
    Zstd = (uint)CookedCompression.Zstd << 8,
    MeshoptVertex = (uint)CookedCompression.MeshoptVertex << 8,
    MeshoptIndex = (uint)CookedCompression.MeshoptIndex << 8,
    MeshoptIndexSequence = (uint)CookedCompression.MeshoptIndexSequence << 8
}

[Flags]
public enum CookedAssetReaderFlags
{
    None = 0,
    SkipHashValidation = 1 << 0,
    StrictSourceHash = 1 << 1,
    PreferMemoryMapped = 1 << 2,
    RequireSignature = 1 << 3
}

public readonly record struct CookedFormatVersion(ushort Major, ushort Minor);

public static class CookedFormatVersions
{
    public static CookedFormatVersion Model { get; } = new(1, 1);
    public static CookedFormatVersion Mesh { get; } = new(1, 1);
    public static CookedFormatVersion Material { get; } = new(1, 2);
    public static CookedFormatVersion Texture { get; } = new(1, 3);
    public static CookedFormatVersion Animation { get; } = new(1, 1);

    public static CookedFormatVersion For(CookedAssetKind kind) => kind switch
    {
        CookedAssetKind.Model => Model,
        CookedAssetKind.Mesh => Mesh,
        CookedAssetKind.Material => Material,
        CookedAssetKind.Texture => Texture,
        CookedAssetKind.Animation => Animation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown cooked asset kind.")
    };
}

public static class CookedSectionIds
{
    public static readonly uint StringTable = FourCc("STRT");
    public static readonly uint Manifest = FourCc("MANF");
    public static readonly uint Metadata = FourCc("META");
    public static readonly uint SubMeshes = FourCc("SUBM");
    public static readonly uint VertexPositions = FourCc("VPOS");
    public static readonly uint VertexNormals = FourCc("VNRM");
    public static readonly uint VertexUvColors = FourCc("VUVC");
    public static readonly uint VertexSkinning = FourCc("VSKN");
    public static readonly uint Indices = FourCc("INDX");
    public static readonly uint Meshlets0 = FourCc("MLT0");
    public static readonly uint Meshlets1 = FourCc("MLT1");
    public static readonly uint Meshlets2 = FourCc("MLT2");
    public static readonly uint MeshletVertices = FourCc("MLVX");
    public static readonly uint MeshletTriangles = FourCc("MLTR");
    public static readonly uint DrawRanges = FourCc("DRWR");
    public static readonly uint Bounds = FourCc("BNDS");
    public static readonly uint Materials = FourCc("MATS");
    public static readonly uint Animation = FourCc("ANIM");

    public static uint FourCc(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 4 || value.Any(c => c > 0x7f))
            throw new ArgumentException("A FourCC must contain exactly four ASCII characters.", nameof(value));
        return (uint)value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);
    }

    public static string ToText(uint value) => string.Create(4, value, static (chars, id) =>
    {
        chars[0] = (char)(id & 0xff);
        chars[1] = (char)((id >> 8) & 0xff);
        chars[2] = (char)((id >> 16) & 0xff);
        chars[3] = (char)((id >> 24) & 0xff);
    });
}

public readonly record struct CookedAssetHeader(
    uint Magic,
    CookedAssetKind AssetKind,
    ushort FormatMajor,
    ushort FormatMinor,
    uint EndiannessMarker,
    uint BuildToolVersion,
    uint Flags,
    ulong SourceHash,
    ulong ImportSettingsHash,
    ulong DependencyListHash,
    uint SectionCount,
    ulong SectionTableOffset)
{
    public const int Size = 64;
    public const uint ExpectedMagic = 0x41434a4e; // NJCA in little endian.
    public const uint ExpectedEndiannessMarker = 0x01020304;

    internal void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Header destination must contain at least {Size} bytes.", nameof(destination));
        destination[..Size].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], (ushort)AssetKind);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], FormatMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], FormatMinor);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..12], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], EndiannessMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], BuildToolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], SourceHash);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], ImportSettingsHash);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..48], DependencyListHash);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[48..52], SectionCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[56..64], SectionTableOffset);
    }

    internal static CookedAssetHeader Read(ReadOnlySpan<byte> source, string path)
    {
        if (source.Length < Size)
            throw new CookedAssetFormatException(path, $"file is shorter than the {Size}-byte header");
        ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source[10..12]);
        if (headerSize != Size)
            throw new CookedAssetFormatException(path, $"unsupported header size {headerSize}");
        return new CookedAssetHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            (CookedAssetKind)BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[8..10]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[40..48]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[48..52]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[56..64]));
    }
}

public readonly record struct CookedSectionEntry(
    uint SectionId,
    CookedSectionFlags Flags,
    ulong Offset,
    ulong CompressedSize,
    ulong UncompressedSize,
    ulong ContentHash)
{
    public const int Size = 40;
    public CookedCompression Compression => (CookedCompression)(((uint)Flags >> 8) & 0xff);

    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], SectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], (uint)Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..16], Offset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], CompressedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], UncompressedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], ContentHash);
    }

    internal static CookedSectionEntry Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
        (CookedSectionFlags)BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[8..16]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]));
}

public sealed class CookedAssetFormatException : IOException
{
    public CookedAssetFormatException(string path, string reason)
        : base($"Cooked asset '{path}' is invalid: {reason}.") => AssetPath = path;
    public string AssetPath { get; }
}

public sealed class CookedAssetHashException : IOException
{
    public CookedAssetHashException(string path, string reason)
        : base($"Cooked asset '{path}' failed hash validation: {reason}.") => AssetPath = path;
    public string AssetPath { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CookedVertexPositionStream
{
    public Njulf.Core.Math.Vector4 Position;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CookedVertexNormalTangentStream
{
    public Njulf.Core.Math.Vector4 Normal;
    public Njulf.Core.Math.Vector4 Tangent;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CookedVertexUvColorStream
{
    public Njulf.Core.Math.Vector2 TexCoord;
    public Njulf.Core.Math.Vector2 TexCoord2;
    public Njulf.Core.Math.Vector4 Color;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CookedVertexSkinningData
{
    public uint Joint0, Joint1, Joint2, Joint3;
    public float Weight0, Weight1, Weight2, Weight3;
}
