using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Graphics;

namespace Njulf.Assets;

public partial class ContentManager
{
    private static readonly JsonSerializerOptions EffectJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private sealed record EffectParameterDocument(string Name, EffectParameterType Type, JsonElement Default,
        float? Minimum = null, float? Maximum = null)
    {
        public EffectParameterDefinition ToDefinition()
        {
            object value = Type switch
            {
                EffectParameterType.Float => Default.GetSingle(),
                EffectParameterType.Int => Default.GetInt32(),
                EffectParameterType.UInt => Default.GetUInt32(),
                EffectParameterType.Vector2 when Default.GetArrayLength() == 2 => new Vector2(Default[0].GetSingle(), Default[1].GetSingle()),
                EffectParameterType.Vector3 when Default.GetArrayLength() == 3 => new Vector3(Default[0].GetSingle(), Default[1].GetSingle(), Default[2].GetSingle()),
                EffectParameterType.Vector4 when Default.GetArrayLength() == 4 => new Vector4(Default[0].GetSingle(), Default[1].GetSingle(), Default[2].GetSingle(), Default[3].GetSingle()),
                _ => throw new InvalidDataException($"Invalid default for effect parameter '{Name}'.")
            };
            return new(Name, Type, value, Minimum, Maximum);
        }
    }
    private sealed record EffectDocument(int SchemaVersion, ShaderEffectKind Kind, string Shader,
        EffectParameterDocument[]? Parameters = null, EffectResourceDefinition[]? Resources = null,
        EffectDispatchSize WorkgroupSize = default);

    private ShaderEffectAsset LoadEffectAsset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(GetFullPath(path));
        if (!fullPath.EndsWith(".njeffect.json", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Shader effect assets use .njeffect.json manifests.");
        string key = typeof(ShaderEffectAsset).FullName + "|" + fullPath;
        lock (_stateLock)
        {
            ThrowIfDisposed();
            ValidateAcquisition(_currentAcquisition.Value!);
            if (TryGetCachedAsset(key, out var cached)) return (ShaderEffectAsset)cached;
        }
        ShaderEffectAsset asset;
        try
        {
            var document = JsonSerializer.Deserialize<EffectDocument>(ReadContentSnapshot(fullPath, 1024 * 1024), EffectJsonOptions)
                ?? throw new InvalidDataException("Empty effect manifest.");
            if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported effect schema version.");
            ArgumentException.ThrowIfNullOrWhiteSpace(document.Shader);
            if (Path.IsPathRooted(document.Shader) || !document.Shader.EndsWith(".spv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Effect shaders must use relative .spv paths.");
            string shaderPath = Path.GetFullPath(document.Shader, Path.GetDirectoryName(fullPath)!);
            string assetName = Path.GetRelativePath(Path.GetFullPath(_rootDirectory), fullPath).Replace('\\', '/');
            asset = new(assetName, document.Kind, ReadContentSnapshot(shaderPath, 16 * 1024 * 1024),
                document.Parameters?.Select(p => p.ToDefinition()), document.Resources, document.WorkgroupSize);
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException or InvalidDataException or FormatException)
        {
            throw new InvalidDataException(ContentDiagnostic.Format(fullPath, failure), failure);
        }
        lock (_stateLock)
        {
            ThrowIfDisposed();
            ValidateAcquisition(_currentAcquisition.Value!);
            if (TryGetCachedAsset(key, out var winner)) return (ShaderEffectAsset)winner;
            PublishWithDependencies(key, asset, null);
            return asset;
        }
    }
}
