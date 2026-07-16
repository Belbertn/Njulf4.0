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
    public int SchemaVersion { get; init; } = 1;
    public SortedDictionary<string, CookedAssetDatabaseEntry> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static CookedAssetDatabase Load(string path)
    {
        if (!File.Exists(path))
            return new CookedAssetDatabase();
        try
        {
            return JsonSerializer.Deserialize<CookedAssetDatabase>(File.ReadAllBytes(path), CookedJson.Options)
                ?? new CookedAssetDatabase();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Cooked asset database '{path}' is invalid.", ex);
        }
    }

    public void SaveAtomic(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        var options = new JsonSerializerOptions(CookedJson.Options) { WriteIndented = true };
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, fullPath, overwrite: true);
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
}
