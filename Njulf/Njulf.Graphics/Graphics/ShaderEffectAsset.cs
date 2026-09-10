using System.Buffers.Binary;
using Njulf.Core.Math;

namespace Njulf.Graphics;

public enum ShaderEffectKind { Fullscreen, Compute }
public enum EffectParameterType { Float, Int, UInt, Vector2, Vector3, Vector4 }
public enum EffectResourceKind { SampledTexture2D, StorageImage2D, StorageBuffer }
public enum EffectResourceAccess { Read, Write, ReadWrite }

/// <summary>A named value. Each parameter occupies one 16-byte push-constant slot in declaration order.</summary>
public sealed record EffectParameterDefinition(string Name, EffectParameterType Type, object DefaultValue,
    float? Minimum = null, float? Maximum = null);
/// <summary>A binding in descriptor set zero. Storage images use rgba16f; sampled images use linear clamp sampling.</summary>
public sealed record EffectResourceDefinition(string Name, uint Binding, EffectResourceKind Kind,
    EffectResourceAccess Access = EffectResourceAccess.Read);
/// <summary>Positive thread counts, or the shader's literal local workgroup dimensions.</summary>
public readonly record struct EffectDispatchSize(uint X, uint Y = 1, uint Z = 1);

/// <summary>Immutable CPU shader bytes and authored interface metadata, shared independently of GPU registrations.</summary>
public sealed class ShaderEffectAsset
{
    private readonly byte[] _code;
    private readonly byte[] _defaults;
    public string Name { get; }
    public ShaderEffectKind Kind { get; }
    public IReadOnlyList<EffectParameterDefinition> Parameters { get; }
    public IReadOnlyList<EffectResourceDefinition> Resources { get; }
    public EffectDispatchSize WorkgroupSize { get; }
    public ReadOnlySpan<byte> ShaderCode => _code;
    internal ReadOnlySpan<byte> DefaultBytes => _defaults;

    public ShaderEffectAsset(string name, ShaderEffectKind kind, ReadOnlySpan<byte> shaderCode,
        IEnumerable<EffectParameterDefinition>? parameters = null,
        IEnumerable<EffectResourceDefinition>? resources = null, EffectDispatchSize workgroupSize = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (shaderCode.Length < 20 || shaderCode.Length > 16 * 1024 * 1024 || shaderCode.Length % 4 != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(shaderCode) != 0x07230203)
            throw new InvalidDataException($"Effect '{name}' requires bounded SPIR-V bytecode.");
        Name = name; Kind = kind; _code = shaderCode.ToArray();
        var values = parameters?.ToArray() ?? [];
        if (values.Length > 8) throw new ArgumentException("Effects support at most eight 16-byte parameter slots.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        _defaults = new byte[values.Length * 16];
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            ArgumentNullException.ThrowIfNull(value);
            if (string.IsNullOrWhiteSpace(value.Name) || !names.Add(value.Name)) throw new ArgumentException("Parameter names must be unique and nonempty.");
            if (value.Minimum is { } min && !float.IsFinite(min) || value.Maximum is { } max && !float.IsFinite(max) || value.Minimum > value.Maximum)
                throw new ArgumentException($"Invalid range hints for '{value.Name}'.");
            WriteParameter(value, value.DefaultValue, _defaults.AsSpan(i * 16, 16));
        }
        Parameters = Array.AsReadOnly(values);
        var bindings = resources?.ToArray() ?? [];
        names.Clear();
        var slots = new HashSet<uint>();
        foreach (var resource in bindings)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (string.IsNullOrWhiteSpace(resource.Name) || !names.Add(resource.Name) || !slots.Add(resource.Binding))
                throw new ArgumentException("Resource names and binding numbers must be unique.");
            if (resource.Name == "$destination" || !Enum.IsDefined(resource.Kind) || !Enum.IsDefined(resource.Access))
                throw new ArgumentException("Invalid resource definition.");
            if (resource.Kind == EffectResourceKind.SampledTexture2D && resource.Access != EffectResourceAccess.Read)
                throw new ArgumentException("Sampled textures are read-only.");
            if (kind == ShaderEffectKind.Fullscreen && resource.Access != EffectResourceAccess.Read)
                throw new ArgumentException("Fullscreen shaders write only their color output; use compute for storage writes.");
        }
        Resources = Array.AsReadOnly(bindings);
        if (kind == ShaderEffectKind.Compute && (workgroupSize.X == 0 || workgroupSize.Y == 0 || workgroupSize.Z == 0))
            throw new ArgumentException("Compute assets require literal, positive workgroup dimensions.");
        WorkgroupSize = workgroupSize;
    }

    internal int ParameterIndex(string name)
    {
        for (int i = 0; i < Parameters.Count; i++) if (Parameters[i].Name == name) return i;
        throw new ArgumentException($"Effect '{Name}' has no parameter '{name}'.", nameof(name));
    }

    internal static void WriteParameter(EffectParameterDefinition definition, object value, Span<byte> bytes)
    {
        bytes.Clear();
        bool valid = definition.Type switch
        {
            EffectParameterType.Float => value is float,
            EffectParameterType.Int => value is int,
            EffectParameterType.UInt => value is uint,
            EffectParameterType.Vector2 => value is Vector2,
            EffectParameterType.Vector3 => value is Vector3,
            EffectParameterType.Vector4 => value is Vector4,
            _ => false
        };
        if (!valid) throw new ArgumentException($"Parameter '{definition.Name}' requires {definition.Type}.");
        switch (value)
        {
            case int n: BinaryPrimitives.WriteInt32LittleEndian(bytes, n); return;
            case uint n: BinaryPrimitives.WriteUInt32LittleEndian(bytes, n); return;
            case float n: WriteFloat(bytes, n); return;
            case Vector2 v: WriteFloat(bytes, v.X); WriteFloat(bytes[4..], v.Y); return;
            case Vector3 v: WriteFloat(bytes, v.X); WriteFloat(bytes[4..], v.Y); WriteFloat(bytes[8..], v.Z); return;
            case Vector4 v: WriteFloat(bytes, v.X); WriteFloat(bytes[4..], v.Y); WriteFloat(bytes[8..], v.Z); WriteFloat(bytes[12..], v.W); return;
        }
    }
    private static void WriteFloat(Span<byte> bytes, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Effect parameters must be finite.");
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
    }
}
