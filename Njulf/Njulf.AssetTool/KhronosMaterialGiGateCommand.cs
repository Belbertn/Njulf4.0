using System.Buffers;
using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Assets.Validation;

namespace Njulf.AssetTool;

internal static class KhronosMaterialGiGateCommand
{
    private const int ReportSchemaVersion =
        KhronosMaterialGiConformance.GateReportSchemaVersion;
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "khronos-material-gi-assets.json");
        string? cacheRoot = null;
        string? outputRoot = null;
        string? reportPath = null;
        bool offline = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest": manifestPath = RequireValue(args, ref i, "--manifest"); break;
                case "--cache": cacheRoot = RequireValue(args, ref i, "--cache"); break;
                case "--out": outputRoot = RequireValue(args, ref i, "--out"); break;
                case "--report": reportPath = RequireValue(args, ref i, "--report"); break;
                case "--offline": offline = true; break;
                default: throw new ArgumentException($"Unknown Khronos material-GI gate option '{args[i]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(cacheRoot) ||
            string.IsNullOrWhiteSpace(outputRoot) ||
            string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException(
                "khronos-material-gi requires --cache <folder> --out <folder> --report <json>.");
        }

        manifestPath = Path.GetFullPath(manifestPath);
        cacheRoot = Path.GetFullPath(cacheRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        reportPath = Path.GetFullPath(reportPath);
        KhronosMaterialGiManifestSnapshot manifestSnapshot =
            KhronosMaterialGiConformance.LoadManifestSnapshot(manifestPath);
        KhronosMaterialGiManifest manifest = manifestSnapshot.Manifest;
        string manifestSha256 = manifestSnapshot.Sha256;
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        var entries = new List<KhronosMaterialGiGateEntry>(manifest.Assets.Count);
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(outputRoot);

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Njulf-AssetTool-MaterialGiGate/1.0");
        using var cooker = new ModelAssetCooker();
        foreach (KhronosMaterialGiAsset asset in manifest.Assets)
        {
            DateTimeOffset assetStartedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                string cachePath = ResolveCachePath(cacheRoot, manifest.Commit, asset);
                byte[] payload = await GetAuthenticatedPayloadAsync(
                    client,
                    manifest,
                    asset,
                    cachePath,
                    offline).ConfigureAwait(false);

                ModelImportResult import;
                using (var importer = new ModelImporter())
                {
                    import = importer.ImportDetailed(
                        cachePath,
                        new ImporterOptions { Backend = ModelImportBackend.SharpGltf });
                }
                ModelMesh mesh = import.EnsureImported();
                IReadOnlyList<string> importedErrors =
                    KhronosMaterialGiConformance.ValidateImported(asset, mesh);
                if (importedErrors.Count != 0)
                {
                    throw new InvalidDataException(
                        $"Imported Khronos asset '{asset.Name}' failed semantics: {string.Join(" ", importedErrors)}");
                }

                var cookOptions = new ModelCookOptions
                {
                    ImporterOptions = new ImporterOptions { Backend = ModelImportBackend.SharpGltf },
                    TextureOptions = new TextureCookOptions(
                        MaxDimension: 2048,
                        TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8),
                    Force = true,
                    Platform = CookedPlatform.Current
                };
                AssetCookResult cook = cooker.CookModel(cachePath, outputRoot, cookOptions);
                if (!string.Equals(cook.Report.Status, "Succeeded", StringComparison.Ordinal))
                    throw new InvalidDataException($"Cook status was '{cook.Report.Status}'.");
                string packagePath = Path.Combine(
                    CookedPlatform.ResolveOutputRoot(outputRoot, cookOptions.Platform),
                    "models",
                    Path.GetFileNameWithoutExtension(cachePath) + ".njmodel");
                CookedModelAsset cooked = CookedPackage.LoadModel(packagePath);
                IReadOnlyList<string> cookedErrors =
                    KhronosMaterialGiConformance.ValidateCooked(asset, cooked);
                if (cookedErrors.Count != 0)
                {
                    throw new InvalidDataException(
                        $"Cooked Khronos asset '{asset.Name}' failed semantics: {string.Join(" ", cookedErrors)}");
                }

                entries.Add(new KhronosMaterialGiGateEntry(
                    asset.Name,
                    "Passed",
                    asset.Sha256,
                    payload.LongLength,
                    import.BackendName,
                    import.BackendVersion,
                    cook.Report.MaterialCount,
                    cook.Report.SubMeshCount,
                    cooked.Materials.PrimitiveTransportProfiles.Count,
                    cook.Report.Warnings,
                    null,
                    (long)(DateTimeOffset.UtcNow - assetStartedAtUtc).TotalMilliseconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entries.Add(new KhronosMaterialGiGateEntry(
                    asset.Name,
                    "Failed",
                    asset.Sha256,
                    0,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0,
                    Array.Empty<string>(),
                    $"{ex.GetType().Name}: {ex.Message}",
                    (long)(DateTimeOffset.UtcNow - assetStartedAtUtc).TotalMilliseconds));
            }
        }

        bool success = entries.Count == manifest.Assets.Count &&
                       entries.All(static entry => entry.Status == "Passed");
        var report = new KhronosMaterialGiGateReport(
            ReportSchemaVersion,
            success ? "Passed" : "Failed",
            KhronosMaterialGiConformance.OfficialRepository,
            manifest.Commit,
            manifestSha256,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            offline,
            entries);
        await WriteReportAtomicallyAsync(reportPath, report).ConfigureAwait(false);
        foreach (KhronosMaterialGiGateEntry entry in entries)
        {
            Console.WriteLine(
                $"{entry.Status}: {entry.Name} materials={entry.MaterialCount} " +
                $"submeshes={entry.SubMeshCount} profiles={entry.PrimitiveProfileCount} " +
                $"elapsed={entry.ElapsedMilliseconds}ms" +
                (entry.Failure is null ? string.Empty : $" failure='{entry.Failure}'"));
        }
        Console.WriteLine($"Khronos material-GI gate {report.Status}; report='{reportPath}'.");
        return success ? 0 : 1;
    }

    private static async Task<byte[]> GetAuthenticatedPayloadAsync(
        HttpClient client,
        KhronosMaterialGiManifest manifest,
        KhronosMaterialGiAsset asset,
        string cachePath,
        bool offline)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                byte[] cached = await ReadExactFileAsync(
                    cachePath,
                    asset.Bytes,
                    $"Khronos cache entry '{asset.Name}'").ConfigureAwait(false);
                KhronosMaterialGiConformance.VerifyPayload(asset, cached);
                return cached;
            }
            catch (Exception exception) when (
                !offline &&
                exception is InvalidDataException or IOException)
            {
                // Preserve the bad cache entry until an authenticated replacement
                // has been downloaded and atomically published below.
            }
        }
        if (offline)
            throw new InvalidDataException($"No authenticated offline cache entry exists for '{asset.Name}'.");

        string url = KhronosMaterialGiConformance.BuildDownloadUrl(manifest, asset);
        using HttpResponseMessage response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != asset.Bytes)
        {
            throw new InvalidDataException(
                $"Khronos asset '{asset.Name}' declared HTTP length {length}; expected {asset.Bytes}.");
        }
        await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var destination = new MemoryStream(checked((int)asset.Bytes));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (destination.Length + read > asset.Bytes)
                {
                    throw new InvalidDataException(
                        $"Khronos asset '{asset.Name}' exceeded its pinned {asset.Bytes}-byte length.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        byte[] payload = destination.ToArray();
        KhronosMaterialGiConformance.VerifyPayload(asset, payload);
        string directory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteDurableFileAsync(temporaryPath, payload).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return payload;
    }

    private static string ResolveCachePath(
        string cacheRoot,
        string commit,
        KhronosMaterialGiAsset asset)
    {
        string commitRoot = Path.GetFullPath(Path.Combine(cacheRoot, commit));
        string path = Path.GetFullPath(Path.Combine(commitRoot, Path.GetFileName(asset.RelativePath)));
        string prefix = commitRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cache path for '{asset.Name}' escaped the requested root.");
        return path;
    }

    private static async Task WriteReportAtomicallyAsync(
        string path,
        KhronosMaterialGiGateReport report)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report, ReportJson);
            if (json.Length <= 0 ||
                json.Length >
                    KhronosMaterialGiConformance.MaximumGateReportBytes)
            {
                throw new InvalidDataException(
                    "Khronos material-GI report exceeds its bounded evidence contract.");
            }
            await WriteDurableFileAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<byte[]> ReadExactFileAsync(
        string path,
        long expectedBytes,
        string description)
    {
        if (expectedBytes is <= 0 or > KhronosMaterialGiConformance.MaximumAssetBytes)
            throw new InvalidDataException($"{description} has an invalid expected length.");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"{description} is {stream.Length} bytes; expected exactly {expectedBytes}.");
        }

        byte[] bytes = new byte[checked((int)expectedBytes)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (stream.Length != expectedBytes)
            throw new IOException($"{description} changed while it was being read.");
        return bytes;
    }

    private static async Task WriteDurableFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private sealed record KhronosMaterialGiGateReport(
        int SchemaVersion,
        string Status,
        string Repository,
        string Commit,
        string ManifestSha256,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        bool Offline,
        IReadOnlyList<KhronosMaterialGiGateEntry> Entries);

    private sealed record KhronosMaterialGiGateEntry(
        string Name,
        string Status,
        string Sha256,
        long Bytes,
        string ImportBackend,
        string ImportBackendVersion,
        int MaterialCount,
        int SubMeshCount,
        int PrimitiveProfileCount,
        IReadOnlyList<string> Warnings,
        string? Failure,
        long ElapsedMilliseconds);
}
