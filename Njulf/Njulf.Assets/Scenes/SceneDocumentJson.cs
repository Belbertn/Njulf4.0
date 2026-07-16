using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Njulf.Assets.Scenes;

public static class SceneDocumentJson
{
    private static readonly HashSet<string> KnownRootFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "id", "name", "ambientLight", "objects", "lights", "reflectionProbes",
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
        string json = File.ReadAllText(path);
        WarnAboutUnknownRootFields(json, path);
        SceneDocument document = JsonSerializer.Deserialize<SceneDocument>(json, Options)
            ?? throw new InvalidDataException($"Scene document '{path}' is empty or invalid.");
        if (document.SchemaVersion != SceneDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Scene document '{path}' uses unsupported schema version {document.SchemaVersion}.");
        return document;
    }

    private static void WarnAboutUnknownRootFields(string json, string path)
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
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (createBackup && File.Exists(fullPath))
            File.Copy(fullPath, fullPath + ".bak", overwrite: true);

        string temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(document));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static SceneDocument Normalize(SceneDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        Id = document.Id,
        Name = document.Name,
        AmbientLight = document.AmbientLight,
        Objects = document.Objects.OrderBy(static item => item.Id).ToList(),
        Lights = document.Lights.OrderBy(static item => item.Id).ToList(),
        ReflectionProbes = document.ReflectionProbes.OrderBy(static item => item.Id).ToList(),
        GiProbeVolumes = document.GiProbeVolumes.OrderBy(static item => item.Id).ToList(),
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
