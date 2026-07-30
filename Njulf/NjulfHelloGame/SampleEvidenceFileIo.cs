using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

internal readonly record struct SampleEvidenceFileContent(
    string Path,
    byte[] Bytes,
    string Sha256);

/// <summary>
/// Bounded, same-handle evidence input. The file is opened without write or
/// delete sharing, admitted by length before allocation, read exactly once,
/// and hashed from the exact byte array returned to the parser.
/// </summary>
internal static class SampleEvidenceFileIo
{
    private const int MaximumPathCharacters = 32_767;
    private const int MaximumRoleCharacters = 512;

    public const long MaximumJsonBytes = 16L * 1024L * 1024L;
    public const long MaximumLinearFloatImageBytes =
        PfmLinearImageCodec.MaximumEncodedBytes;

    public static void ValidateStrictJson(
        ReadOnlySpan<byte> utf8Json,
        int maximumDepth,
        string role)
    {
        if (utf8Json.IsEmpty)
            throw new InvalidDataException($"{role} JSON is empty.");
        if (maximumDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (string.IsNullOrWhiteSpace(role) ||
            role.Length > MaximumRoleCharacters)
        {
            throw new ArgumentException(
                "A bounded evidence role is required.",
                nameof(role));
        }

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth
            });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.PropertyName:
                    if (!containers.TryPeek(out HashSet<string>? names) ||
                        names is null)
                    {
                        throw new InvalidDataException(
                            $"{role} contains a JSON property outside an object.");
                    }
                    string propertyName = reader.GetString() ??
                        throw new InvalidDataException(
                            $"{role} contains a null JSON property name.");
                    if (!names.Add(propertyName))
                    {
                        throw new InvalidDataException(
                            $"{role} contains a duplicate JSON property.");
                    }
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"{role} contains unbalanced JSON containers.");
                    }
                    containers.Pop();
                    break;
            }
        }
        if (containers.Count != 0)
            throw new InvalidDataException($"{role} contains unbalanced JSON containers.");
    }

    public static SampleEvidenceFileContent WriteAtomic(
        string path,
        ReadOnlySpan<byte> payload,
        long maximumBytes,
        string role)
    {
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (payload.IsEmpty)
            throw new ArgumentException(
                "An evidence payload must not be empty.",
                nameof(payload));
        if (payload.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"{role} contains {payload.Length} bytes; " +
                $"the bounded limit is {maximumBytes} bytes.");
        }
        // Read performs the shared path, bound, and role admission checks. Do
        // the equivalent checks before publishing so invalid input cannot
        // create directories or temporary files.
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(
                "An evidence file path is required.",
                nameof(path));
        if (path.Length > MaximumPathCharacters)
            throw new ArgumentException(
                "The evidence file path is too long.",
                nameof(path));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException(
                "An evidence role is required.",
                nameof(role));
        if (role.Length > MaximumRoleCharacters)
            throw new ArgumentException(
                "The evidence role is too long.",
                nameof(role));

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new IOException(
                $"Could not resolve an evidence directory for '{fullPath}'.");
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

            SampleEvidenceFileContent published =
                Read(fullPath, maximumBytes, role);
            if (!published.Bytes.AsSpan().SequenceEqual(payload))
            {
                throw new IOException(
                    $"{role} '{fullPath}' differs from the committed payload.");
            }
            return published;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static SampleEvidenceFileContent Read(
        string path,
        long maximumBytes,
        string role)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An evidence file path is required.", nameof(path));
        if (path.Length > MaximumPathCharacters)
            throw new ArgumentException("The evidence file path is too long.", nameof(path));
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("An evidence role is required.", nameof(role));
        if (role.Length > MaximumRoleCharacters)
            throw new ArgumentException("The evidence role is too long.", nameof(role));

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
                $"{role} '{fullPath}' is empty.");
        }
        if (admittedLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"{role} '{fullPath}' contains {admittedLength} bytes; " +
                $"the bounded limit is {maximumBytes} bytes.");
        }

        var bytes = new byte[checked((int)admittedLength)];
        try
        {
            input.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"{role} '{fullPath}' became shorter while it was being read.",
                exception);
        }

        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new InvalidDataException(
                $"{role} '{fullPath}' changed length while it was being read.");
        }

        return new SampleEvidenceFileContent(
            fullPath,
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
