using System.Text.Json;

namespace Njulf.Assets;

/// <summary>
/// Bounded immutable reads and durable same-directory publication for asset
/// metadata. Keeping these mechanics in one place prevents validation and
/// cooker artifacts from regressing to unbounded whole-file reads or
/// process-shared fixed temporary names.
/// </summary>
internal static class AssetArtifactFileIo
{
    internal const int DefaultMaximumJsonBytes = 64 * 1024 * 1024;
    internal const int MaximumCookSourceBytes = 512 * 1024 * 1024;

    internal static byte[] ReadBoundedSnapshot(
        string path,
        int maximumBytes,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        string fullPath = Path.GetFullPath(path);
        using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength <= 0)
        {
            throw new InvalidDataException(
                $"{description} '{fullPath}' is empty.");
        }
        if (admittedLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"{description} '{fullPath}' contains {admittedLength} bytes, " +
                $"exceeding the {maximumBytes}-byte limit.");
        }

        byte[] snapshot = GC.AllocateUninitializedArray<byte>(
            checked((int)admittedLength));
        try
        {
            input.ReadExactly(snapshot);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"{description} '{fullPath}' became shorter during its bounded read.",
                exception);
        }

        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new InvalidDataException(
                $"{description} '{fullPath}' changed length during its bounded read.");
        }

        return snapshot;
    }

    internal static void ValidateUniqueJsonPropertyNames(
        ReadOnlySpan<byte> json,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        try
        {
            var reader = new Utf8JsonReader(
                json,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            var objectProperties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectProperties.Push(
                            new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (objectProperties.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"{description} contains an unmatched JSON object terminator.");
                        }
                        objectProperties.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (objectProperties.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"{description} contains a property outside a JSON object.");
                        }
                        string propertyName =
                            reader.GetString() ?? string.Empty;
                        if (!objectProperties.Peek().Add(propertyName))
                        {
                            throw new InvalidDataException(
                                $"{description} contains duplicate JSON property " +
                                $"'{propertyName}'.");
                        }
                        break;
                }
            }

            if (objectProperties.Count != 0)
            {
                throw new InvalidDataException(
                    $"{description} contains an unterminated JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{description} is not valid JSON.",
                exception);
        }
    }

    internal static void WriteAtomic(
        string path,
        ReadOnlySpan<byte> payload,
        int maximumBytes,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (payload.IsEmpty)
            throw new InvalidOperationException($"{description} output is empty.");
        if (payload.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"{description} output contains {payload.Length} bytes, exceeding " +
                $"the {maximumBytes}-byte limit.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"{description} path '{fullPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = CreateSiblingTemporaryPath(fullPath, "tmp");

        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static void CopyAtomic(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        string fullSourcePath = Path.GetFullPath(sourcePath);
        string fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(
                fullSourcePath,
                fullDestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException(
                $"{description} path '{fullDestinationPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath =
            CreateSiblingTemporaryPath(fullDestinationPath, "copy");

        try
        {
            using var input = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.SequentialScan);
            long admittedLength = input.Length;
            if (admittedLength < 0 || admittedLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"{description} '{fullSourcePath}' contains {admittedLength} bytes, " +
                    $"exceeding the {maximumBytes}-byte limit.");
            }

            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       options: FileOptions.WriteThrough))
            {
                byte[] buffer = GC.AllocateUninitializedArray<byte>(
                    128 * 1024);
                long remaining = admittedLength;
                while (remaining > 0)
                {
                    int requested = checked((int)Math.Min(
                        buffer.Length,
                        remaining));
                    int read = input.Read(buffer, 0, requested);
                    if (read == 0)
                    {
                        throw new InvalidDataException(
                            $"{description} '{fullSourcePath}' became shorter " +
                            "during its bounded copy.");
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                }

                if (input.ReadByte() != -1 ||
                    input.Length != admittedLength)
                {
                    throw new InvalidDataException(
                        $"{description} '{fullSourcePath}' changed length during its bounded copy.");
                }

                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static string CreateSiblingTemporaryPath(
        string destinationPath,
        string purpose)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Artifact path '{fullPath}' has no parent directory.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}." +
            $"{Guid.NewGuid():N}.{purpose}");
    }
}
