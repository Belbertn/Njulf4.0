using System;
using System.IO;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Bounded same-directory JSON publication used by renderer-owned evidence
/// writers. The temporary file is exclusive, flushed through the OS cache,
/// atomically committed, and verified from one stable read handle.
/// </summary>
internal static class DurableJsonFileWriter
{
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;

    public static string Write(
        string path,
        ReadOnlySpan<byte> payload,
        string role)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(
                "A JSON output path is required.",
                nameof(path));
        if (string.IsNullOrWhiteSpace(role) || role.Length > 512)
            throw new ArgumentException(
                "A bounded JSON output role is required.",
                nameof(role));
        if (payload.IsEmpty ||
            payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"{role} payload length {payload.Length} is outside the " +
                $"1..{MaximumPayloadBytes} byte bound.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new IOException(
                $"Could not resolve a directory for {role} '{fullPath}'.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            using var input = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (input.Length != payload.Length ||
                input.Length <= 0 ||
                input.Length > MaximumPayloadBytes)
            {
                throw new IOException(
                    $"Published {role} '{fullPath}' has an unexpected bounded length.");
            }
            var readback = new byte[payload.Length];
            input.ReadExactly(readback);
            if (input.ReadByte() != -1 ||
                input.Length != payload.Length ||
                !readback.AsSpan().SequenceEqual(payload))
            {
                throw new IOException(
                    $"Published {role} '{fullPath}' differs from the committed payload.");
            }
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
