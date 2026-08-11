using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Njulf.Rendering.Data;

/// <summary>
/// Exact producer identity for a persistent Simple-DDGI prior. Every component
/// is independently retained in the file header so a cache can never be
/// admitted merely because a lossy aggregate happened to match.
/// </summary>
public sealed class SimpleDdgiWarmStartIdentity
{
    public const int ComponentHashBytes = 32;

    public SimpleDdgiWarmStartIdentity(
        byte[] sceneHash,
        byte[] meshHash,
        byte[] transformHash,
        byte[] materialTransportHash,
        byte[] environmentHash,
        byte[] layoutHash,
        byte[] directionCodebookHash,
        byte[] shaderAbiHash)
    {
        SceneHash = CopyHash(sceneHash, nameof(sceneHash));
        MeshHash = CopyHash(meshHash, nameof(meshHash));
        TransformHash = CopyHash(transformHash, nameof(transformHash));
        MaterialTransportHash = CopyHash(
            materialTransportHash,
            nameof(materialTransportHash));
        EnvironmentHash = CopyHash(environmentHash, nameof(environmentHash));
        LayoutHash = CopyHash(layoutHash, nameof(layoutHash));
        DirectionCodebookHash = CopyHash(
            directionCodebookHash,
            nameof(directionCodebookHash));
        ShaderAbiHash = CopyHash(shaderAbiHash, nameof(shaderAbiHash));
    }

    public byte[] SceneHash { get; }
    public byte[] MeshHash { get; }
    public byte[] TransformHash { get; }
    public byte[] MaterialTransportHash { get; }
    public byte[] EnvironmentHash { get; }
    public byte[] LayoutHash { get; }
    public byte[] DirectionCodebookHash { get; }
    public byte[] ShaderAbiHash { get; }

    public bool IsCompatibleWith(SimpleDdgiWarmStartIdentity? other)
    {
        return other != null &&
            Equal(SceneHash, other.SceneHash) &&
            Equal(MeshHash, other.MeshHash) &&
            Equal(TransformHash, other.TransformHash) &&
            Equal(MaterialTransportHash, other.MaterialTransportHash) &&
            Equal(EnvironmentHash, other.EnvironmentHash) &&
            Equal(LayoutHash, other.LayoutHash) &&
            Equal(DirectionCodebookHash, other.DirectionCodebookHash) &&
            Equal(ShaderAbiHash, other.ShaderAbiHash);
    }

    public byte[] ComputeAggregateHash()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Append(hash, SceneHash);
        Append(hash, MeshHash);
        Append(hash, TransformHash);
        Append(hash, MaterialTransportHash);
        Append(hash, EnvironmentHash);
        Append(hash, LayoutHash);
        Append(hash, DirectionCodebookHash);
        Append(hash, ShaderAbiHash);
        return hash.GetHashAndReset();
    }

    internal IEnumerable<byte[]> Components
    {
        get
        {
            yield return SceneHash;
            yield return MeshHash;
            yield return TransformHash;
            yield return MaterialTransportHash;
            yield return EnvironmentHash;
            yield return LayoutHash;
            yield return DirectionCodebookHash;
            yield return ShaderAbiHash;
        }
    }

    private static byte[] CopyHash(byte[] value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != ComponentHashBytes)
        {
            throw new ArgumentException(
                $"A warm-start identity component must contain exactly {ComponentHashBytes} bytes.",
                parameterName);
        }
        return (byte[])value.Clone();
    }

    private static bool Equal(byte[] left, byte[] right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(left, right);

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

/// <summary>
/// Scene-side identity known before the camera-relative volume layout is
/// compiled. The volume manager appends layout and direction-codebook hashes.
/// </summary>
public sealed record SimpleDdgiWarmStartSceneIdentity(
    byte[] SceneHash,
    byte[] MeshHash,
    byte[] TransformHash,
    byte[] MaterialTransportHash,
    byte[] EnvironmentHash,
    byte[] ShaderAbiHash,
    bool Eligible,
    string IneligibleReason);

/// <summary>
/// One dense volume captured in physical/toroidal order. Exact floating-point
/// lattice anchors and physical offsets make every payload probe independently
/// world keyed while preserving contiguous GPU copies for the common
/// identical-origin case. A later origin is compatible only when its delta from
/// this anchor is an integral number of probe cells.
/// </summary>
public sealed record SimpleDdgiWarmStartVolumeData(
    int SourceOrdinal,
    int Kind,
    uint SpacingBits,
    uint OriginXBits,
    uint OriginYBits,
    uint OriginZBits,
    int CountX,
    int CountY,
    int CountZ,
    int PhysicalOffsetX,
    int PhysicalOffsetY,
    int PhysicalOffsetZ,
    byte[] Irradiance,
    byte[] Visibility,
    byte[] ReceiverProbes)
{
    public int ProbeCount => checked(CountX * CountY * CountZ);
}

public sealed record SimpleDdgiWarmStartArchive(
    SimpleDdgiWarmStartIdentity Identity,
    IReadOnlyList<SimpleDdgiWarmStartVolumeData> Volumes)
{
    public int ProbeCount
    {
        get
        {
            int count = 0;
            foreach (SimpleDdgiWarmStartVolumeData volume in Volumes)
                count = checked(count + volume.ProbeCount);
            return count;
        }
    }
}

public readonly record struct SimpleDdgiWarmStartLoadResult(
    bool Found,
    bool Accepted,
    SimpleDdgiWarmStartArchive? Archive,
    ulong FileBytes,
    string Path,
    string Status);

public readonly record struct SimpleDdgiWarmStartSaveResult(
    bool Saved,
    ulong FileBytes,
    string Path,
    string Status);

/// <summary>
/// Checksummed, bounded and compressed persistent archive. Decode validates the
/// complete identity before allocating or exposing any payload records.
/// </summary>
internal static class SimpleDdgiWarmStartFileCodec
{
    private static ReadOnlySpan<byte> Magic => "NJDDGIW1"u8;
    internal const uint FormatVersion = 2;
    internal const int IdentityComponentCount = 8;
    internal const int HeaderSize = 368;
    internal const int MaximumProbeCount = 65_536;
    internal const int MaximumVolumeCount = 32;
    internal const int MaximumUncompressedBytes = 128 * 1024 * 1024;
    internal const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int PayloadVersion = 2;
    private const int IrradianceBytesPerProbe = 512;
    private const int VisibilityBytesPerProbe = 1_024;
    private const int ReceiverBytesPerProbe = 16;

    public static byte[] Encode(SimpleDdgiWarmStartArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ValidateArchive(archive);

        byte[] payload = EncodePayload(archive.Volumes);
        if (payload.Length > MaximumUncompressedBytes)
            throw new InvalidDataException("Warm-start payload exceeds the admitted bound.");

        byte[] compressed;
        using (var output = new MemoryStream(payload.Length))
        {
            using (var brotli = new BrotliStream(
                       output,
                       CompressionLevel.Fastest,
                       leaveOpen: true))
            {
                brotli.Write(payload);
            }
            compressed = output.ToArray();
        }

        int totalBytes = checked(HeaderSize + compressed.Length);
        if (totalBytes > MaximumFileBytes)
            throw new InvalidDataException("Warm-start file exceeds the admitted bound.");

        byte[] encoded = new byte[totalBytes];
        Span<byte> header = encoded.AsSpan(0, HeaderSize);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 1u); // Brotli.
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[20..],
            checked((uint)archive.Volumes.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[24..],
            checked((uint)archive.ProbeCount));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[32..],
            checked((ulong)payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[40..],
            checked((ulong)compressed.Length));

        int identityOffset = 48;
        foreach (byte[] component in archive.Identity.Components)
        {
            component.CopyTo(header[identityOffset..]);
            identityOffset += SimpleDdgiWarmStartIdentity.ComponentHashBytes;
        }
        SHA256.HashData(payload, header[304..336]);
        SHA256.HashData(compressed, header[336..368]);
        compressed.CopyTo(encoded.AsSpan(HeaderSize));
        return encoded;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> encoded,
        SimpleDdgiWarmStartIdentity expectedIdentity,
        out SimpleDdgiWarmStartArchive? archive,
        out string reason)
    {
        archive = null;
        reason = string.Empty;
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        try
        {
            if (encoded.Length < HeaderSize)
                return Reject("Warm-start file is truncated.", out reason);
            if (encoded.Length > MaximumFileBytes)
                return Reject("Warm-start file exceeds the admitted bound.", out reason);
            if (!encoded[..8].SequenceEqual(Magic))
                return Reject("Warm-start magic is not recognized.", out reason);
            if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]) !=
                FormatVersion)
            {
                return Reject("Warm-start format version does not match.", out reason);
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[12..]) !=
                HeaderSize)
            {
                return Reject("Warm-start header size does not match.", out reason);
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[16..]) != 1u)
                return Reject("Warm-start compression mode is unsupported.", out reason);

            uint volumeCount = BinaryPrimitives.ReadUInt32LittleEndian(encoded[20..]);
            uint probeCount = BinaryPrimitives.ReadUInt32LittleEndian(encoded[24..]);
            if (volumeCount > MaximumVolumeCount || probeCount > MaximumProbeCount)
                return Reject("Warm-start cardinality exceeds the admitted bound.", out reason);

            ulong uncompressedLength =
                BinaryPrimitives.ReadUInt64LittleEndian(encoded[32..]);
            ulong compressedLength =
                BinaryPrimitives.ReadUInt64LittleEndian(encoded[40..]);
            if (uncompressedLength > MaximumUncompressedBytes ||
                compressedLength > MaximumFileBytes - HeaderSize ||
                compressedLength != checked((ulong)(encoded.Length - HeaderSize)))
            {
                return Reject("Warm-start payload length is invalid.", out reason);
            }

            int identityOffset = 48;
            foreach (byte[] expected in expectedIdentity.Components)
            {
                ReadOnlySpan<byte> actual = encoded.Slice(
                    identityOffset,
                    SimpleDdgiWarmStartIdentity.ComponentHashBytes);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    return Reject(
                        "Warm-start producer identity does not match.",
                        out reason);
                }
                identityOffset += SimpleDdgiWarmStartIdentity.ComponentHashBytes;
            }

            ReadOnlySpan<byte> compressed = encoded[HeaderSize..];
            Span<byte> compressedHash = stackalloc byte[32];
            SHA256.HashData(compressed, compressedHash);
            if (!CryptographicOperations.FixedTimeEquals(
                    compressedHash,
                    encoded[336..368]))
            {
                return Reject("Warm-start compressed checksum failed.", out reason);
            }

            byte[] payload = new byte[checked((int)uncompressedLength)];
            using (var input = new MemoryStream(
                       compressed.ToArray(),
                       writable: false))
            using (var brotli = new BrotliStream(
                       input,
                       CompressionMode.Decompress,
                       leaveOpen: false))
            {
                int offset = 0;
                while (offset < payload.Length)
                {
                    int read = brotli.Read(payload, offset, payload.Length - offset);
                    if (read == 0)
                    {
                        return Reject(
                            "Warm-start decompressed length is invalid.",
                            out reason);
                    }
                    offset += read;
                }
                if (brotli.ReadByte() >= 0)
                {
                    return Reject(
                        "Warm-start decompressed payload exceeds its declared bound.",
                        out reason);
                }
            }

            Span<byte> payloadHash = stackalloc byte[32];
            SHA256.HashData(payload, payloadHash);
            if (!CryptographicOperations.FixedTimeEquals(
                    payloadHash,
                    encoded[304..336]))
            {
                return Reject("Warm-start payload checksum failed.", out reason);
            }

            if (!TryDecodePayload(
                    payload,
                    checked((int)volumeCount),
                    checked((int)probeCount),
                    out IReadOnlyList<SimpleDdgiWarmStartVolumeData> volumes,
                    out reason))
            {
                return false;
            }

            archive = new SimpleDdgiWarmStartArchive(expectedIdentity, volumes);
            reason = "Compatible certified warm-start cache loaded.";
            return true;
        }
        catch (Exception ex) when (ex is
            InvalidDataException or IOException or OverflowException or
            ArgumentException or CryptographicException)
        {
            archive = null;
            reason = $"Warm-start decode rejected: {ex.Message}";
            return false;
        }
    }

    private static byte[] EncodePayload(
        IReadOnlyList<SimpleDdgiWarmStartVolumeData> volumes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PayloadVersion);
        writer.Write(volumes.Count);
        foreach (SimpleDdgiWarmStartVolumeData volume in volumes)
        {
            writer.Write(volume.SourceOrdinal);
            writer.Write(volume.Kind);
            writer.Write(volume.SpacingBits);
            writer.Write(volume.OriginXBits);
            writer.Write(volume.OriginYBits);
            writer.Write(volume.OriginZBits);
            writer.Write(volume.CountX);
            writer.Write(volume.CountY);
            writer.Write(volume.CountZ);
            writer.Write(volume.PhysicalOffsetX);
            writer.Write(volume.PhysicalOffsetY);
            writer.Write(volume.PhysicalOffsetZ);
            writer.Write(volume.Irradiance.Length);
            writer.Write(volume.Visibility.Length);
            writer.Write(volume.ReceiverProbes.Length);
            writer.Write(volume.Irradiance);
            writer.Write(volume.Visibility);
            writer.Write(volume.ReceiverProbes);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static bool TryDecodePayload(
        byte[] payload,
        int expectedVolumeCount,
        int expectedProbeCount,
        out IReadOnlyList<SimpleDdgiWarmStartVolumeData> volumes,
        out string reason)
    {
        volumes = Array.Empty<SimpleDdgiWarmStartVolumeData>();
        reason = string.Empty;
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != PayloadVersion)
            return Reject("Warm-start payload version does not match.", out reason);
        int volumeCount = reader.ReadInt32();
        if (volumeCount != expectedVolumeCount ||
            volumeCount < 0 || volumeCount > MaximumVolumeCount)
        {
            return Reject("Warm-start volume count is inconsistent.", out reason);
        }

        var result = new List<SimpleDdgiWarmStartVolumeData>(volumeCount);
        int totalProbeCount = 0;
        for (int i = 0; i < volumeCount; i++)
        {
            int sourceOrdinal = reader.ReadInt32();
            int kind = reader.ReadInt32();
            uint spacingBits = reader.ReadUInt32();
            uint originXBits = reader.ReadUInt32();
            uint originYBits = reader.ReadUInt32();
            uint originZBits = reader.ReadUInt32();
            int countX = reader.ReadInt32();
            int countY = reader.ReadInt32();
            int countZ = reader.ReadInt32();
            int physicalOffsetX = reader.ReadInt32();
            int physicalOffsetY = reader.ReadInt32();
            int physicalOffsetZ = reader.ReadInt32();
            int irradianceLength = reader.ReadInt32();
            int visibilityLength = reader.ReadInt32();
            int receiverLength = reader.ReadInt32();

            float spacing = BitConverter.UInt32BitsToSingle(spacingBits);
            if (!float.IsFinite(spacing) || spacing <= 0.0f ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(originXBits)) ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(originYBits)) ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(originZBits)) ||
                countX <= 0 || countY <= 0 || countZ <= 0)
                return Reject("Warm-start volume dimensions are invalid.", out reason);
            int probeCount = checked(countX * countY * countZ);
            totalProbeCount = checked(totalProbeCount + probeCount);
            if (totalProbeCount > MaximumProbeCount ||
                physicalOffsetX < 0 || physicalOffsetX >= countX ||
                physicalOffsetY < 0 || physicalOffsetY >= countY ||
                physicalOffsetZ < 0 || physicalOffsetZ >= countZ ||
                irradianceLength != checked(probeCount * IrradianceBytesPerProbe) ||
                visibilityLength != checked(probeCount * VisibilityBytesPerProbe) ||
                receiverLength != checked(probeCount * ReceiverBytesPerProbe))
            {
                return Reject("Warm-start volume payload shape is invalid.", out reason);
            }

            int remaining = checked((int)(stream.Length - stream.Position));
            int required = checked(
                irradianceLength + visibilityLength + receiverLength);
            if (required > remaining)
                return Reject("Warm-start volume payload is truncated.", out reason);

            byte[] irradiance = reader.ReadBytes(irradianceLength);
            byte[] visibility = reader.ReadBytes(visibilityLength);
            byte[] receiver = reader.ReadBytes(receiverLength);
            result.Add(new SimpleDdgiWarmStartVolumeData(
                sourceOrdinal,
                kind,
                spacingBits,
                originXBits,
                originYBits,
                originZBits,
                countX,
                countY,
                countZ,
                physicalOffsetX,
                physicalOffsetY,
                physicalOffsetZ,
                irradiance,
                visibility,
                receiver));
        }

        if (totalProbeCount != expectedProbeCount ||
            stream.Position != stream.Length)
        {
            return Reject("Warm-start payload cardinality is inconsistent.", out reason);
        }

        volumes = result;
        return true;
    }

    private static void ValidateArchive(SimpleDdgiWarmStartArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive.Identity);
        ArgumentNullException.ThrowIfNull(archive.Volumes);
        if (archive.Volumes.Count > MaximumVolumeCount ||
            archive.ProbeCount > MaximumProbeCount)
        {
            throw new InvalidDataException(
                "Warm-start archive cardinality exceeds the admitted bound.");
        }
        foreach (SimpleDdgiWarmStartVolumeData volume in archive.Volumes)
        {
            ArgumentNullException.ThrowIfNull(volume);
            int probeCount = volume.ProbeCount;
            float spacing = BitConverter.UInt32BitsToSingle(
                volume.SpacingBits);
            if (!float.IsFinite(spacing) || spacing <= 0.0f ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(
                    volume.OriginXBits)) ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(
                    volume.OriginYBits)) ||
                !float.IsFinite(BitConverter.UInt32BitsToSingle(
                    volume.OriginZBits)) ||
                probeCount <= 0 ||
                volume.PhysicalOffsetX < 0 ||
                volume.PhysicalOffsetX >= volume.CountX ||
                volume.PhysicalOffsetY < 0 ||
                volume.PhysicalOffsetY >= volume.CountY ||
                volume.PhysicalOffsetZ < 0 ||
                volume.PhysicalOffsetZ >= volume.CountZ ||
                volume.Irradiance.Length !=
                    checked(probeCount * IrradianceBytesPerProbe) ||
                volume.Visibility.Length !=
                    checked(probeCount * VisibilityBytesPerProbe) ||
                volume.ReceiverProbes.Length !=
                    checked(probeCount * ReceiverBytesPerProbe))
            {
                throw new InvalidDataException(
                    "Warm-start archive contains an invalid volume payload.");
            }
        }
    }

    private static bool Reject(string message, out string reason)
    {
        reason = message;
        return false;
    }
}

/// <summary>
/// Filesystem boundary kept separate from the renderer state machine. The
/// caller runs these synchronous methods on a worker and never waits during a
/// render-critical frame.
/// </summary>
internal sealed class SimpleDdgiWarmStartCacheStore
{
    private readonly string _directory;

    public SimpleDdgiWarmStartCacheStore(string? directory = null)
    {
        string? environmentDirectory = Environment.GetEnvironmentVariable(
            "NJULF_DDGI_WARM_CACHE_DIR");
        _directory = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(directory)
                ? directory
                : !string.IsNullOrWhiteSpace(environmentDirectory)
                    ? environmentDirectory
                    : Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "Njulf",
                        "DdgiWarmStart"));
    }

    public string GetPath(SimpleDdgiWarmStartIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string key = Convert.ToHexString(identity.ComputeAggregateHash())
            .ToLowerInvariant();
        return Path.Combine(_directory, $"ddgi-{key}.njwarm");
    }

    public bool HasCandidate(SimpleDdgiWarmStartIdentity identity) =>
        File.Exists(GetPath(identity));

    public SimpleDdgiWarmStartLoadResult Load(
        SimpleDdgiWarmStartIdentity identity)
    {
        string path = GetPath(identity);
        try
        {
            if (!File.Exists(path))
            {
                return new SimpleDdgiWarmStartLoadResult(
                    false,
                    false,
                    null,
                    0UL,
                    path,
                    "No persistent warm-start cache was present.");
            }

            var info = new FileInfo(path);
            if (info.Length < SimpleDdgiWarmStartFileCodec.HeaderSize ||
                info.Length > SimpleDdgiWarmStartFileCodec.MaximumFileBytes)
            {
                return new SimpleDdgiWarmStartLoadResult(
                    true,
                    false,
                    null,
                    checked((ulong)Math.Max(info.Length, 0L)),
                    path,
                    "Persistent warm-start file length is invalid.");
            }

            byte[] encoded = File.ReadAllBytes(path);
            bool accepted = SimpleDdgiWarmStartFileCodec.TryDecode(
                encoded,
                identity,
                out SimpleDdgiWarmStartArchive? archive,
                out string reason);
            return new SimpleDdgiWarmStartLoadResult(
                true,
                accepted,
                archive,
                checked((ulong)encoded.Length),
                path,
                reason);
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or ArgumentException or
            CryptographicException or OverflowException)
        {
            return new SimpleDdgiWarmStartLoadResult(
                File.Exists(path),
                false,
                null,
                0UL,
                path,
                $"Persistent warm-start load skipped: {ex.Message}");
        }
    }

    public SimpleDdgiWarmStartSaveResult Save(
        SimpleDdgiWarmStartArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string path = GetPath(archive.Identity);
        try
        {
            byte[] encoded = SimpleDdgiWarmStartFileCodec.Encode(archive);
            WriteAtomically(path, encoded);
            return new SimpleDdgiWarmStartSaveResult(
                true,
                checked((ulong)encoded.Length),
                path,
                "Certified persistent warm-start cache saved.");
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or ArgumentException or
            CryptographicException or OverflowException or InvalidDataException)
        {
            return new SimpleDdgiWarmStartSaveResult(
                false,
                0UL,
                path,
                $"Persistent warm-start save skipped: {ex.Message}");
        }
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> data)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Warm-start path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public readonly record struct SimpleDdgiWarmStartTelemetry(
    bool Enabled,
    bool Eligible,
    bool LoadPending,
    bool CacheFound,
    bool CacheAccepted,
    bool PriorActive,
    bool ReadbackPending,
    bool SavePending,
    int CachedVolumeCount,
    int CachedProbeCount,
    int AppliedProbeCount,
    ulong LoadedFileBytes,
    ulong SavedFileBytes,
    ulong ReadbackBytes,
    ulong LoadCount,
    ulong RejectCount,
    ulong ApplyCount,
    ulong SaveCount,
    string CachePath,
    string Status)
{
    public static SimpleDdgiWarmStartTelemetry Disabled(string reason) =>
        new(
            false, false, false, false, false, false, false, false,
            0, 0, 0, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL,
            string.Empty,
            reason ?? string.Empty);
}
