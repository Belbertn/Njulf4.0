using System.Text.Json;

namespace Njulf.Assets.Cooked;

public sealed record CookedAssetDatabaseEntry
{
    public string SourcePath { get; init; } = string.Empty;
    public ulong SourceHash { get; init; }
    public ulong ImportSettingsHash { get; init; }
    public ulong DependencyHash { get; init; }
    public uint ToolVersion { get; init; }
    public string Status { get; init; } = "Unknown";
    public DateTimeOffset CookedAtUtc { get; init; }
    public IReadOnlyDictionary<string, ulong> Dependencies { get; init; } = new Dictionary<string, ulong>();
    public IReadOnlyDictionary<string, ulong> Outputs { get; init; } = new Dictionary<string, ulong>();
}

public sealed record CookedAssetDatabase
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumDatabaseBytes =
        AssetArtifactFileIo.DefaultMaximumJsonBytes;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public SortedDictionary<string, CookedAssetDatabaseEntry> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static CookedAssetDatabase Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new CookedAssetDatabase();
        try
        {
            byte[] snapshot = AssetArtifactFileIo.ReadBoundedSnapshot(
                path,
                MaximumDatabaseBytes,
                "Cooked asset database");
            CookedAssetDatabase database =
                CookedJson.Deserialize<CookedAssetDatabase>(
                snapshot,
                Path.GetFullPath(path),
                "asset database");
            if (database.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Cooked asset database '{path}' uses unsupported schema " +
                    $"version {database.SchemaVersion}.");
            }
            if (database.Assets is null)
            {
                throw new InvalidDataException(
                    $"Cooked asset database '{path}' has no asset table.");
            }

            var normalized = new SortedDictionary<
                string,
                CookedAssetDatabaseEntry>(
                StringComparer.OrdinalIgnoreCase);
            foreach ((string key, CookedAssetDatabaseEntry entry) in database.Assets)
            {
                if (string.IsNullOrWhiteSpace(key) ||
                    entry is null ||
                    string.IsNullOrWhiteSpace(entry.SourcePath) ||
                    string.IsNullOrWhiteSpace(entry.Status) ||
                    entry.Dependencies is null ||
                    entry.Outputs is null)
                {
                    throw new InvalidDataException(
                        $"Cooked asset database '{path}' contains an incomplete entry.");
                }
                if (!normalized.TryAdd(key, entry))
                {
                    throw new InvalidDataException(
                        $"Cooked asset database '{path}' contains duplicate " +
                        $"case-insensitive asset key '{key}'.");
                }
            }

            return database with { Assets = normalized };
        }
        catch (CookedAssetFormatException ex)
        {
            throw new InvalidDataException($"Cooked asset database '{path}' is invalid.", ex);
        }
    }

    public void SaveAtomic(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var options = new JsonSerializerOptions(CookedJson.Options) { WriteIndented = true };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(this, options);
        AssetArtifactFileIo.WriteAtomic(
            fullPath,
            payload,
            MaximumDatabaseBytes,
            "Cooked asset database");
    }
}

public sealed record AssetCookReport(
    string SourcePath,
    Guid AssetId,
    string Status,
    ModelImportBackend Backend,
    long ImportMilliseconds,
    long MeshMilliseconds,
    long TextureMilliseconds,
    long SerializationMilliseconds,
    int SubMeshCount,
    int MaterialCount,
    int TextureCount,
    int SkeletonCount,
    int SkinCount,
    int AnimationClipCount,
    int VertexCount,
    int IndexCount,
    int MeshletCount,
    IReadOnlyList<CookedTextureReport> Textures,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, ulong> Outputs)
{
    public int MeshletLod1Count { get; init; }
    public int MeshletLod2Count { get; init; }
}

public static class AssetCookReportJson
{
    public const int MaximumReportBytes =
        AssetArtifactFileIo.DefaultMaximumJsonBytes;

    public static AssetCookReport Read(string path)
    {
        byte[] snapshot = AssetArtifactFileIo.ReadBoundedSnapshot(
            path,
            MaximumReportBytes,
            "Asset cook report");
        try
        {
            AssetCookReport report =
                CookedJson.Deserialize<AssetCookReport>(
                snapshot,
                Path.GetFullPath(path),
                "cook report");
            if (string.IsNullOrWhiteSpace(report.SourcePath) ||
                report.AssetId == Guid.Empty ||
                string.IsNullOrWhiteSpace(report.Status) ||
                report.Textures is null ||
                report.Warnings is null ||
                report.Outputs is null)
            {
                throw new InvalidDataException(
                    $"Asset cook report '{path}' contains incomplete identity or collections.");
            }

            return report;
        }
        catch (CookedAssetFormatException exception)
        {
            throw new InvalidDataException(
                $"Asset cook report '{path}' is invalid.",
                exception);
        }
    }

    public static void WriteAtomic(string path, AssetCookReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = new JsonSerializerOptions(CookedJson.Options)
        {
            WriteIndented = true
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, options);
        AssetArtifactFileIo.WriteAtomic(
            path,
            payload,
            MaximumReportBytes,
            "Asset cook report");
    }
}

public sealed record AssetCookResult(AssetCookReport Report, bool Skipped);

public sealed record ModelCookOptions
{
    public ImporterOptions ImporterOptions { get; init; } = ImporterOptions.Default;
    public TextureCookOptions TextureOptions { get; init; } = new();
    public uint ToolVersion { get; init; } = 1;
    public bool Force { get; init; }
    public string Platform { get; init; } = CookedPlatform.Current;
    public bool UsePlatformSubdirectory { get; init; } = true;
    public string? SigningPrivateKey { get; init; }

    /// <summary>
    /// Offline-only C1 hook. When absent (the default), no OMM payload is
    /// cooked, stored, allocated, or loaded as an active renderer feature.
    /// </summary>
    public IOpacityMicromapModelPayloadProducer? OpacityMicromapPayloadProducer { get; init; }
}
