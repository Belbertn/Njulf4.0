using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Njulf.Assets.Scenes;

public static class SceneDocumentJson
{
    internal const int MaximumDocumentBytes = 64 * 1024 * 1024;

    private static readonly HashSet<string> KnownRootFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "name", "ambientLight", "importedModelLightsEnabled", "objects", "lights", "reflectionProbes",
        "giProbeVolumes", "instanceBatches", "foliagePrototypes", "foliagePatches", "particleEffects", "dependencies"
    };

    /// <summary>Receives forward-compatibility notices for fields this schema version does not consume.</summary>
    public static event Action<string>? Warning;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static SceneDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] json = ReadBoundedSnapshot(fullPath);
        WarnAboutUnknownRootFields(json, fullPath);
        SceneDocument document =
            JsonSerializer.Deserialize<SceneDocument>(json, Options)
            ?? throw new InvalidDataException(
                $"Scene document '{fullPath}' is empty or invalid.");
        if (document.SchemaVersion < 1 || document.SchemaVersion > SceneDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Scene document '{fullPath}' uses unsupported schema version {document.SchemaVersion}.");
        }

        return SceneDocumentCompatibility.MaterializeLegacyMaterialOverrideDefaults(
            document);
    }

    private static void WarnAboutUnknownRootFields(
        ReadOnlyMemory<byte> json,
        string path)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            return;
        foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            if (!KnownRootFields.Contains(property.Name))
                Warning?.Invoke($"Scene document '{path}' contains unknown field '{property.Name}'; it was ignored.");
    }

    public static string Serialize(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SceneDocument.CurrentSchemaVersion)
            throw new InvalidOperationException($"Cannot write unsupported scene schema version {document.SchemaVersion}.");
        return JsonSerializer.Serialize(Normalize(document), Options) + Environment.NewLine;
    }

    public static void WriteAtomic(string path, SceneDocument document, bool createBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] payload = Encoding.UTF8.GetBytes(Serialize(document));
        if (payload.Length > MaximumDocumentBytes)
        {
            throw new InvalidOperationException(
                $"Scene document output contains {payload.Length} bytes, exceeding " +
                $"the {MaximumDocumentBytes}-byte limit.");
        }

        string fullPath = Path.GetFullPath(path);
        string directory =
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Scene document path '{fullPath}' has no parent directory.");
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
                       bufferSize: 64 * 1024,
                       options: FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    createBackup ? fullPath + ".bak" : null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadBoundedSnapshot(string fullPath)
    {
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
                $"Scene document '{fullPath}' is empty.");
        }

        if (admittedLength > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Scene document '{fullPath}' contains {admittedLength} bytes, exceeding " +
                $"the {MaximumDocumentBytes}-byte limit.");
        }

        var bytes = new byte[checked((int)admittedLength)];
        try
        {
            input.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"Scene document '{fullPath}' became shorter during its bounded read.",
                exception);
        }

        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new InvalidDataException(
                $"Scene document '{fullPath}' changed length during its bounded read.");
        }

        return bytes;
    }

    private static SceneDocument Normalize(SceneDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        Id = document.Id,
        Name = document.Name,
        AmbientLight = document.AmbientLight,
        ImportedModelLightsEnabled = document.ImportedModelLightsEnabled,
        Objects = document.Objects.OrderBy(static item => item.Id).ToList(),
        Lights = document.Lights.OrderBy(static item => item.Id).ToList(),
        ReflectionProbes = document.ReflectionProbes.OrderBy(static item => item.Id).ToList(),
        GiProbeVolumes = document.GiProbeVolumes.OrderBy(static item => item.Id).ToList(),
        VolumetricDensityVolumes = document.VolumetricDensityVolumes
            .OrderBy(static item => item.Id)
            .ToList(),
        InstanceBatches = document.InstanceBatches.OrderBy(static item => item.Id).Select(Normalize).ToList(),
        FoliagePrototypes = document.FoliagePrototypes.OrderBy(static item => item.Id).ToList(),
        FoliagePatches = document.FoliagePatches.OrderBy(static item => item.Id).ToList(),
        ParticleEffects = document.ParticleEffects.OrderBy(static item => item.Id).ToList(),
        Dependencies = document.Dependencies.OrderBy(static item => item.Path, StringComparer.Ordinal).ToList()
    };

    private static SceneInstanceBatchDocument Normalize(SceneInstanceBatchDocument batch) => new()
    {
        Id = batch.Id,
        Name = batch.Name,
        Model = batch.Model,
        Visible = batch.Visible,
        // Instance order is retained: callers may use it for deterministic procedural variation.
        Instances = batch.Instances.ToList()
    };
}
