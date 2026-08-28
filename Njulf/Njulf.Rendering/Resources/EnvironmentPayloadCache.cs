using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Njulf.Rendering.Resources;

internal readonly record struct EnvironmentPayloadCacheIdentity(
    string SourcePath,
    long SourceLength,
    long SourceLastWriteUtcTicks,
    uint EnvironmentSize,
    uint IrradianceSize,
    uint PrefilteredSize,
    uint PrefilteredMipCount,
    uint BytesPerPixel,
    uint ProcessingVersion);

internal readonly record struct EnvironmentPayloadCacheData(
    byte[] EnvironmentCubemap,
    byte[] IrradianceCubemap,
    byte[] PrefilteredCubemap)
{
    public long TotalBytes => checked(
        EnvironmentCubemap.LongLength +
        IrradianceCubemap.LongLength +
        PrefilteredCubemap.LongLength);
}

internal readonly record struct EnvironmentPayloadCacheReadResult(
    bool Hit,
    EnvironmentPayloadCacheData Payload,
    string Reason);

/// <summary>
/// Versioned, checksummed envelope for CPU-generated environment maps. The
/// identity includes the source-file stamp and every processing dimension, so
/// stale or incompatible data is rejected before any large payload allocation.
/// </summary>
internal static class EnvironmentPayloadCache
{
    internal const uint FormatVersion = 1;
    internal const uint HeaderSize = 112;
    internal const uint CurrentProcessingVersion = 1;
    private const int IdentityHashOffset = 16;
    private const int PayloadChecksumOffset = 80;

    public static EnvironmentPayloadCacheIdentity CreateIdentity(
        string sourcePath,
        uint environmentSize,
        uint irradianceSize,
        uint prefilteredSize,
        uint prefilteredMipCount,
        uint bytesPerPixel)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        source.Refresh();
        if (!source.Exists)
        {
            throw new FileNotFoundException(
                "The HDR environment source was not found.",
                fullPath);
        }

        return new EnvironmentPayloadCacheIdentity(
            fullPath,
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            environmentSize,
            irradianceSize,
            prefilteredSize,
            prefilteredMipCount,
            bytesPerPixel,
            CurrentProcessingVersion);
    }

    public static string GetCacheFileName(
        in EnvironmentPayloadCacheIdentity identity) =>
        Convert.ToHexString(ComputeIdentityHash(identity)).ToLowerInvariant() +
        ".njenv";

    public static async Task<EnvironmentPayloadCacheReadResult> TryReadAsync(
        string path,
        EnvironmentPayloadCacheIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new EnvironmentPayloadCacheReadResult(
                false,
                default,
                "cache file is absent");
        }

        try
        {
            long expectedEnvironmentBytes = EstimateCubeBytes(
                identity.EnvironmentSize,
                mipLevels: 1,
                identity.BytesPerPixel);
            long expectedIrradianceBytes = EstimateCubeBytes(
                identity.IrradianceSize,
                mipLevels: 1,
                identity.BytesPerPixel);
            long expectedPrefilteredBytes = EstimateCubeBytes(
                identity.PrefilteredSize,
                identity.PrefilteredMipCount,
                identity.BytesPerPixel);
            long expectedPayloadBytes = checked(
                expectedEnvironmentBytes +
                expectedIrradianceBytes +
                expectedPrefilteredBytes);
            long expectedFileBytes = checked(HeaderSize + expectedPayloadBytes);

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedFileBytes)
            {
                return Miss(
                    $"file length {stream.Length} does not match expected " +
                    $"length {expectedFileBytes}");
            }

            byte[] header = new byte[HeaderSize];
            await stream.ReadExactlyAsync(header, cancellationToken)
                .ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual("NJENVC01"u8))
                return Miss("magic does not match");
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) !=
                FormatVersion)
            {
                return Miss("format version does not match");
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) !=
                HeaderSize)
            {
                return Miss("header size does not match");
            }

            byte[] expectedIdentityHash = ComputeIdentityHash(identity);
            if (!CryptographicOperations.FixedTimeEquals(
                    header.AsSpan(IdentityHashOffset, 32),
                    expectedIdentityHash))
            {
                return Miss("source or processing identity does not match");
            }

            ulong encodedEnvironmentBytes =
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
            ulong encodedIrradianceBytes =
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56));
            ulong encodedPrefilteredBytes =
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(64));
            ulong encodedPayloadBytes =
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            if (encodedEnvironmentBytes != (ulong)expectedEnvironmentBytes ||
                encodedIrradianceBytes != (ulong)expectedIrradianceBytes ||
                encodedPrefilteredBytes != (ulong)expectedPrefilteredBytes ||
                encodedPayloadBytes != (ulong)expectedPayloadBytes)
            {
                return Miss("payload dimensions do not match");
            }

            byte[] environment = new byte[checked((int)expectedEnvironmentBytes)];
            byte[] irradiance = new byte[checked((int)expectedIrradianceBytes)];
            byte[] prefiltered = new byte[checked((int)expectedPrefilteredBytes)];
            await stream.ReadExactlyAsync(environment, cancellationToken)
                .ConfigureAwait(false);
            await stream.ReadExactlyAsync(irradiance, cancellationToken)
                .ConfigureAwait(false);
            await stream.ReadExactlyAsync(prefiltered, cancellationToken)
                .ConfigureAwait(false);

            byte[] actualChecksum = ComputePayloadChecksum(
                environment,
                irradiance,
                prefiltered);
            if (!CryptographicOperations.FixedTimeEquals(
                    header.AsSpan(PayloadChecksumOffset, 32),
                    actualChecksum))
            {
                return Miss("payload checksum does not match");
            }

            return new EnvironmentPayloadCacheReadResult(
                true,
                new EnvironmentPayloadCacheData(
                    environment,
                    irradiance,
                    prefiltered),
                "validated cache hit");
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            OverflowException or
            ArgumentException)
        {
            return Miss(exception.Message);
        }

        static EnvironmentPayloadCacheReadResult Miss(string reason) =>
            new(false, default, reason);
    }

    public static async Task WriteAsync(
        string path,
        EnvironmentPayloadCacheIdentity identity,
        EnvironmentPayloadCacheData payload,
        CancellationToken cancellationToken = default)
    {
        long expectedEnvironmentBytes = EstimateCubeBytes(
            identity.EnvironmentSize,
            mipLevels: 1,
            identity.BytesPerPixel);
        long expectedIrradianceBytes = EstimateCubeBytes(
            identity.IrradianceSize,
            mipLevels: 1,
            identity.BytesPerPixel);
        long expectedPrefilteredBytes = EstimateCubeBytes(
            identity.PrefilteredSize,
            identity.PrefilteredMipCount,
            identity.BytesPerPixel);
        if (payload.EnvironmentCubemap.LongLength != expectedEnvironmentBytes ||
            payload.IrradianceCubemap.LongLength != expectedIrradianceBytes ||
            payload.PrefilteredCubemap.LongLength != expectedPrefilteredBytes)
        {
            throw new InvalidDataException(
                "Environment payload dimensions do not match the cache identity.");
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "Environment cache path has no directory.",
                nameof(path));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + ".tmp-" +
                               Guid.NewGuid().ToString("N");
        byte[] header = CreateHeader(identity, payload);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await stream.WriteAsync(header, cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(
                        payload.EnvironmentCubemap,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(
                        payload.IrradianceCubemap,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(
                        payload.PrefilteredCubemap,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] CreateHeader(
        in EnvironmentPayloadCacheIdentity identity,
        in EnvironmentPayloadCacheData payload)
    {
        byte[] header = new byte[HeaderSize];
        "NJENVC01"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8),
            FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(12),
            HeaderSize);
        ComputeIdentityHash(identity).CopyTo(
            header,
            IdentityHashOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(48),
            checked((ulong)payload.EnvironmentCubemap.LongLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(56),
            checked((ulong)payload.IrradianceCubemap.LongLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(64),
            checked((ulong)payload.PrefilteredCubemap.LongLength));
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(72),
            checked((ulong)payload.TotalBytes));
        ComputePayloadChecksum(
                payload.EnvironmentCubemap,
                payload.IrradianceCubemap,
                payload.PrefilteredCubemap)
            .CopyTo(header, PayloadChecksumOffset);
        return header;
    }

    private static byte[] ComputeIdentityHash(
        in EnvironmentPayloadCacheIdentity identity)
    {
        string normalizedPath = Path.GetFullPath(identity.SourcePath)
            .ToUpperInvariant();
        byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
        byte[] scalars = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(
            scalars.AsSpan(0),
            identity.SourceLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            scalars.AsSpan(8),
            identity.SourceLastWriteUtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(16),
            identity.EnvironmentSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(20),
            identity.IrradianceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(24),
            identity.PrefilteredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(28),
            identity.PrefilteredMipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(32),
            identity.BytesPerPixel);
        BinaryPrimitives.WriteUInt32LittleEndian(
            scalars.AsSpan(36),
            identity.ProcessingVersion);
        BinaryPrimitives.WriteInt32LittleEndian(
            scalars.AsSpan(40),
            pathBytes.Length);

        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(scalars);
        hash.AppendData(pathBytes);
        return hash.GetHashAndReset();
    }

    private static byte[] ComputePayloadChecksum(
        byte[] environment,
        byte[] irradiance,
        byte[] prefiltered)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(environment);
        hash.AppendData(irradiance);
        hash.AppendData(prefiltered);
        return hash.GetHashAndReset();
    }

    private static long EstimateCubeBytes(
        uint size,
        uint mipLevels,
        uint bytesPerPixel)
    {
        long total = 0;
        uint mipSize = size;
        for (uint mip = 0; mip < mipLevels; mip++)
        {
            total = checked(
                total +
                (long)mipSize * mipSize * 6L * bytesPerPixel);
            mipSize = Math.Max(1u, mipSize / 2u);
        }

        return total;
    }
}
