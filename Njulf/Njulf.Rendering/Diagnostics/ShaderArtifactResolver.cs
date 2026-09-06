using System;
using System.IO;
using System.Reflection;

namespace Njulf.Rendering.Diagnostics;

internal sealed record ResolvedShaderArtifact(
    string FileName, byte[] Bytes, string SourceKind, string SourceIdentity);

/// <summary>The single byte-source policy for runtime modules and bundle identities.</summary>
internal static class ShaderArtifactResolver
{
    internal const string ResourcePrefix = "Njulf.Shaders.";
    internal const int MaximumShaderModuleBytes = 16 * 1024 * 1024;

    internal static string RuntimeFileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathFullyQualified(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Shader names must be unqualified file names.", nameof(name));
        return name.EndsWith(".spv", StringComparison.Ordinal) ? name : name + ".spv";
    }

    internal static ResolvedShaderArtifact Resolve(
        Assembly assembly, string name, string? overrideDirectory, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string fileName = RuntimeFileName(name);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            string path = Path.Combine(Path.GetFullPath(overrideDirectory), fileName);
            if (File.Exists(path))
                return ReadFile(fileName, path, "override");
        }

        foreach (string resource in new[] { ResourcePrefix + fileName, ResourcePrefix + fileName[..^4] })
        {
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream != null)
                return new(fileName, ReadBoundedSnapshot(stream, resource), "embedded", resource);
        }

        string[] candidates = [Path.Combine(baseDirectory, "Shaders", fileName), Path.Combine(baseDirectory, fileName)];
        foreach (string path in candidates)
        {
            try { return ReadFile(fileName, path, "deployment"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        throw new FileNotFoundException(
            $"Required shader '{fileName}' was not found. Searched embedded resources " +
            $"'{ResourcePrefix + fileName}' and '{ResourcePrefix + fileName[..^4]}', and files: " +
            string.Join(", ", candidates));
    }

    private static ResolvedShaderArtifact ReadFile(string name, string path, string kind)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return new(name, ReadBoundedSnapshot(stream, $"shader {kind} '{path}'"), kind, Path.GetFullPath(path));
    }

    internal static byte[] ReadBoundedSnapshot(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException($"{description} must be a readable, seekable snapshot.");
        long start = stream.Position;
        long length = checked(stream.Length - start);
        if (length <= 0 || length > MaximumShaderModuleBytes)
            throw new InvalidDataException($"{description} contains {length} bytes; expected a size in (0, {MaximumShaderModuleBytes}].");
        byte[] snapshot = GC.AllocateUninitializedArray<byte>(checked((int)length));
        try { stream.ReadExactly(snapshot); }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"{description} became shorter while it was read.", exception);
        }
        if (stream.ReadByte() != -1 || stream.Length - start != length)
            throw new InvalidDataException($"{description} changed length while it was read.");
        return snapshot;
    }
}
