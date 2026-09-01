using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Njulf.Rendering.Pipeline;

internal readonly record struct PipelineBinaryBlob(
    byte[] Key,
    byte[] Data);

internal readonly record struct PipelineBinaryLookup(
    PipelineArtifactSource Source,
    IReadOnlyList<PipelineBinaryBlob> Binaries);

internal readonly record struct PipelineBinaryStoreEntryCounts(
    int WritablePipelineCount,
    int SeedPipelineCount)
{
    internal static PipelineBinaryStoreEntryCounts Empty => new(0, 0);
}

internal sealed record PipelineBinaryStoreIdentity(
    uint VendorId,
    uint DeviceId,
    uint DriverVersion,
    uint ApiVersion,
    string GlobalKey,
    string ShaderBundleHash,
    string EngineAbiHash,
    string BuildConfigurationHash);

/// <summary>
/// Application-managed, content-addressed store for VK_KHR_pipeline_binary.
/// The driver key names each immutable blob; a small atomic manifest preserves
/// the ordered key list required to recreate a logical pipeline.
/// </summary>
internal sealed class PipelineBinaryStore
{
    internal const long MaximumWritableBytes = 512L * 1024 * 1024;
    internal const long MaximumManifestBytes = 32L * 1024 * 1024;
    private const int FormatVersion = 1;
    private readonly object _gate = new();
    private readonly PipelineBinaryStoreIdentity _identity;
    private readonly string _writableRoot;
    private readonly string _seedRoot;
    private readonly Dictionary<string, long> _lastAccessUpdates =
        new(StringComparer.Ordinal);
    private BinaryManifest? _writableManifest;
    private BinaryManifest? _seedManifest;

    internal PipelineBinaryStore(
        PipelineBinaryStoreIdentity identity,
        string? writableRoot = null,
        string? seedRoot = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.GlobalKey))
            throw new ArgumentException("A Vulkan pipeline-binary global key is required.", nameof(identity));

        string writableBase = string.IsNullOrWhiteSpace(writableRoot)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Njulf",
                "PipelineBinaries",
                "v1")
            : Path.GetFullPath(writableRoot);
        string seedBase = string.IsNullOrWhiteSpace(seedRoot)
            ? Path.Combine(AppContext.BaseDirectory, "PipelineBinarySeeds", "v1")
            : Path.GetFullPath(seedRoot);
        _writableRoot = Path.Combine(writableBase, identity.GlobalKey);
        _seedRoot = Path.Combine(seedBase, identity.GlobalKey);
    }

    internal string WritableRoot => _writableRoot;

    internal PipelineBinaryStoreEntryCounts CountEntries()
    {
        lock (_gate)
        {
            // Health reporting is a one-shot startup operation. Refresh both
            // manifests here instead of adding filesystem work to Telemetry.
            _writableManifest = ReadManifest(_writableRoot);
            _seedManifest = ReadManifest(_seedRoot);
            return new PipelineBinaryStoreEntryCounts(
                _writableManifest.Pipelines.Count,
                _seedManifest.Pipelines.Count);
        }
    }

    internal bool TryLoad(
        ReadOnlySpan<byte> pipelineKey,
        out PipelineBinaryLookup lookup)
    {
        if (pipelineKey.IsEmpty)
            throw new ArgumentException("A pipeline key is required.", nameof(pipelineKey));

        string key = Convert.ToHexString(pipelineKey);
        lock (_gate)
        {
            // Refresh the writable index so binaries published by another
            // process are visible without reconstructing the renderer.
            _writableManifest = ReadManifest(_writableRoot);
            if (TryLoadFromManifest(
                    GetWritableManifest(),
                    _writableRoot,
                    key,
                    PipelineArtifactSource.WritableBinary,
                    out lookup))
            {
                _lastAccessUpdates[key] = DateTime.UtcNow.Ticks;
                return true;
            }
            return TryLoadFromManifest(
                GetSeedManifest(),
                _seedRoot,
                key,
                PipelineArtifactSource.SeedBinary,
                out lookup);
        }
    }

    internal void Save(
        PipelineArtifactId artifactId,
        ReadOnlySpan<byte> pipelineKey,
        IReadOnlyList<PipelineBinaryBlob> binaries)
    {
        ArgumentNullException.ThrowIfNull(binaries);
        if (pipelineKey.IsEmpty)
            throw new ArgumentException("A pipeline key is required.", nameof(pipelineKey));
        if (binaries.Count == 0)
            return;

        long admittedBytes = 0;
        foreach (PipelineBinaryBlob binary in binaries)
        {
            if (binary.Key is not { Length: > 0 and <= 32 } ||
                binary.Data is not { Length: > 0 })
            {
                throw new ArgumentException(
                    "Pipeline binary keys must be 1-32 bytes and payloads " +
                    "must not be empty.",
                    nameof(binaries));
            }
            admittedBytes = checked(admittedBytes + binary.Data.LongLength);
            if (admittedBytes > MaximumWritableBytes)
            {
                throw new ArgumentException(
                    "A pipeline binary set exceeds the writable-store budget.",
                    nameof(binaries));
            }
        }

        lock (_gate)
        {
            using FileStream storeLock = AcquireStoreLock(_writableRoot);
            CleanupOrphanedTemporaryFiles(_writableRoot);
            _writableManifest = ReadManifest(_writableRoot);
            Directory.CreateDirectory(BlobDirectory(_writableRoot));
            BinaryManifest manifest = GetWritableManifest();
            ApplyLastAccessUpdates(manifest);
            var references = new List<BinaryBlobReference>(binaries.Count);
            foreach (PipelineBinaryBlob binary in binaries)
            {
                string binaryKey = Convert.ToHexString(binary.Key);
                string checksum = Convert.ToHexString(
                    SHA256.HashData(binary.Data));
                string blobPath = BlobPath(_writableRoot, binaryKey);
                if (!File.Exists(blobPath))
                    WriteBlobAtomically(blobPath, binary.Data);
                references.Add(new BinaryBlobReference(
                    binaryKey,
                    binary.Data.LongLength,
                    checksum));
            }

            string key = Convert.ToHexString(pipelineKey);
            manifest.Pipelines[key] = new BinaryPipelineEntry(
                artifactId.Value,
                references,
                DateTime.UtcNow.Ticks);
            CollectToBudget(manifest);
            WriteManifestAtomically(manifest);
            _lastAccessUpdates.Clear();
        }
    }

    internal void InvalidateWritable(ReadOnlySpan<byte> pipelineKey)
    {
        if (pipelineKey.IsEmpty)
            throw new ArgumentException("A pipeline key is required.", nameof(pipelineKey));

        string key = Convert.ToHexString(pipelineKey);
        lock (_gate)
        {
            using FileStream storeLock = AcquireStoreLock(_writableRoot);
            CleanupOrphanedTemporaryFiles(_writableRoot);
            _writableManifest = ReadManifest(_writableRoot);
            BinaryManifest manifest = GetWritableManifest();
            ApplyLastAccessUpdates(manifest);
            if (!manifest.Pipelines.Remove(key))
                return;
            DeleteUnreferencedBlobs(manifest);
            WriteManifestAtomically(manifest);
            _lastAccessUpdates.Clear();
        }
    }

    private bool TryLoadFromManifest(
        BinaryManifest manifest,
        string root,
        string pipelineKey,
        PipelineArtifactSource source,
        out PipelineBinaryLookup lookup)
    {
        lookup = default;
        if (!manifest.Pipelines.TryGetValue(
                pipelineKey,
                out BinaryPipelineEntry? entry))
        {
            return false;
        }

        var binaries = new List<PipelineBinaryBlob>(entry.Binaries.Count);
        long admittedBytes = 0;
        foreach (BinaryBlobReference binary in entry.Binaries)
        {
            byte[] expectedChecksum;
            byte[] binaryKey;
            try
            {
                expectedChecksum = Convert.FromHexString(binary.Sha256);
                binaryKey = Convert.FromHexString(binary.Key);
            }
            catch (FormatException)
            {
                return false;
            }
            if (binaryKey.Length is 0 or > 32 ||
                expectedChecksum.Length != 32 ||
                binary.Length <= 0)
                return false;
            try
            {
                admittedBytes = checked(admittedBytes + binary.Length);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (admittedBytes > MaximumWritableBytes)
                return false;

            string path = BlobPath(root, Convert.ToHexString(binaryKey));
            byte[] data;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != binary.Length ||
                    info.Length <= 0 || info.Length > MaximumWritableBytes)
                {
                    return false;
                }
                data = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(data),
                    expectedChecksum))
            {
                return false;
            }
            binaries.Add(new PipelineBinaryBlob(binaryKey, data));
        }

        if (binaries.Count == 0)
            return false;
        lookup = new PipelineBinaryLookup(source, binaries);
        return true;
    }

    private void ApplyLastAccessUpdates(BinaryManifest manifest)
    {
        foreach ((string key, long timestamp) in _lastAccessUpdates)
        {
            if (manifest.Pipelines.TryGetValue(
                    key,
                    out BinaryPipelineEntry? entry))
            {
                manifest.Pipelines[key] = entry with
                {
                    LastAccessUtcTicks = timestamp
                };
            }
        }
    }

    private BinaryManifest GetWritableManifest() =>
        _writableManifest ??= ReadManifest(_writableRoot);

    private BinaryManifest GetSeedManifest() =>
        _seedManifest ??= ReadManifest(_seedRoot);

    private BinaryManifest ReadManifest(string root)
    {
        string path = ManifestPath(root);
        if (!File.Exists(path))
            return CreateEmptyManifest();
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumManifestBytes)
                return CreateEmptyManifest();
            BinaryManifest? manifest = JsonSerializer.Deserialize<BinaryManifest>(
                File.ReadAllBytes(path));
            if (manifest == null || !IsCompatible(manifest))
                return CreateEmptyManifest();

            // The driver pipeline key is derived from the complete create info.
            // Matching keys under the same device/driver global key therefore
            // remain safe across shader-bundle and build revisions. Upgrade the
            // in-memory identity so a later writable publication records the
            // current provenance while retaining still-usable entries.
            return manifest.Identity == _identity
                ? manifest
                : manifest with { Identity = _identity };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException)
        {
            return CreateEmptyManifest();
        }
    }

    private BinaryManifest CreateEmptyManifest() => new(
        FormatVersion,
        _identity,
        new Dictionary<string, BinaryPipelineEntry>(
            StringComparer.Ordinal));

    private bool IsCompatible(BinaryManifest manifest) =>
        manifest.FormatVersion == FormatVersion &&
        manifest.Pipelines != null &&
        manifest.Identity.VendorId == _identity.VendorId &&
        manifest.Identity.DeviceId == _identity.DeviceId &&
        manifest.Identity.DriverVersion == _identity.DriverVersion &&
        manifest.Identity.ApiVersion == _identity.ApiVersion &&
        string.Equals(
            manifest.Identity.GlobalKey,
            _identity.GlobalKey,
            StringComparison.Ordinal) &&
        string.Equals(
            manifest.Identity.EngineAbiHash,
            _identity.EngineAbiHash,
            StringComparison.Ordinal);

    private void CollectToBudget(BinaryManifest manifest)
    {
        DeleteUnreferencedBlobs(manifest);
        long total = EnumerateBlobFiles()
            .Sum(path => new FileInfo(path).Length);
        if (total <= MaximumWritableBytes)
            return;

        foreach ((string key, BinaryPipelineEntry entry) in
                 manifest.Pipelines
                     .OrderBy(pair => pair.Value.LastAccessUtcTicks)
                     .ToArray())
        {
            manifest.Pipelines.Remove(key);
            DeleteUnreferencedBlobs(manifest);
            total = EnumerateBlobFiles()
                .Sum(path => new FileInfo(path).Length);
            if (total <= MaximumWritableBytes)
                break;
        }
    }

    private void DeleteUnreferencedBlobs(BinaryManifest manifest)
    {
        var referenced = manifest.Pipelines.Values
            .SelectMany(entry => entry.Binaries)
            .Select(binary => binary.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string path in EnumerateBlobFiles())
        {
            if (referenced.Contains(Path.GetFileNameWithoutExtension(path)))
                continue;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Another process may still be reading the immutable blob.
            }
            catch (UnauthorizedAccessException)
            {
                // Collection is best effort; the hard budget is rechecked later.
            }
        }
    }

    private IEnumerable<string> EnumerateBlobFiles()
    {
        string directory = BlobDirectory(_writableRoot);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.bin")
            : Array.Empty<string>();
    }

    private void WriteManifestAtomically(BinaryManifest manifest)
    {
        Directory.CreateDirectory(_writableRoot);
        string path = ManifestPath(_writableRoot);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        WriteBlobAtomically(path, json);
    }

    private static void WriteBlobAtomically(string path, ReadOnlySpan<byte> data)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Binary-store path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}." +
            $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static FileStream AcquireStoreLock(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        string lockPath = Path.Combine(fullRoot, ".store.lock");
        IOException? lastFailure = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        }

        throw new IOException(
            $"Timed out acquiring pipeline-binary store lock '{lockPath}'.",
            lastFailure);
    }

    private static void CleanupOrphanedTemporaryFiles(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (string path in Directory.EnumerateFiles(
                     root,
                     ".*.tmp",
                     SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A live writer can still own a candidate.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup remains best effort.
            }
        }
    }

    private static string ManifestPath(string root) =>
        Path.Combine(root, "manifest.json");

    private static string BlobDirectory(string root) =>
        Path.Combine(root, "blobs");

    private static string BlobPath(string root, string binaryKey) =>
        Path.Combine(BlobDirectory(root), $"{binaryKey}.bin");

    private sealed record BinaryManifest(
        int FormatVersion,
        PipelineBinaryStoreIdentity Identity,
        Dictionary<string, BinaryPipelineEntry> Pipelines);

    private sealed record BinaryPipelineEntry(
        string ArtifactId,
        List<BinaryBlobReference> Binaries,
        long LastAccessUtcTicks);

    private sealed record BinaryBlobReference(
        string Key,
        long Length,
        string Sha256);
}
