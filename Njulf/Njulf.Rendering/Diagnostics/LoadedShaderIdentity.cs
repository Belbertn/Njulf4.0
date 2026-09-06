using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Njulf.Rendering.Diagnostics;

public sealed record LoadedShaderModuleIdentity(
    string FileName, string Sha256, int ByteLength, string SourceKind, string SourceIdentity)
{
    internal static LoadedShaderModuleIdentity Capture(ResolvedShaderArtifact artifact) => new(
        artifact.FileName, Convert.ToHexString(SHA256.HashData(artifact.Bytes)).ToLowerInvariant(),
        artifact.Bytes.Length, artifact.SourceKind, artifact.SourceIdentity);
}

/// <summary>
/// Cumulative successful module creations in one Vulkan context, including prewarmed modules.
/// Source information describes the first creation of each distinct name/content pair.
/// It is not a list of live shader handles or proof of individual draw execution.
/// </summary>
public sealed record LoadedShaderIdentity(
    string Schema, string Fingerprint, long Generation,
    IReadOnlyList<LoadedShaderModuleIdentity> Modules, string FailureReason)
{
    public const string CurrentSchema = "njulf-loaded-shaders/v1";
    public static LoadedShaderIdentity Empty { get; } = new(
        CurrentSchema, "unavailable:loaded-shaders-not-recorded", 0,
        Array.Empty<LoadedShaderModuleIdentity>(), "No shader modules have been recorded.");

    public static string ComputeFingerprint(IReadOnlyList<LoadedShaderModuleIdentity> modules)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, CurrentSchema);
        Span<byte> number = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(number, modules.Count);
        hash.AppendData(number);
        foreach (LoadedShaderModuleIdentity module in modules
                     .OrderBy(m => m.FileName, StringComparer.Ordinal)
                     .ThenBy(m => m.Sha256, StringComparer.Ordinal))
        {
            AppendText(hash, module.FileName);
            BinaryPrimitives.WriteInt32LittleEndian(number, module.ByteLength);
            hash.AppendData(number);
            hash.AppendData(Convert.FromHexString(module.Sha256));
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <returns>Null for verified evidence, otherwise the reason it cannot be verified.</returns>
    public static string? Validate(LoadedShaderIdentity? identity)
    {
        if (identity == null || identity.Schema != CurrentSchema)
            return "Loaded shader identity is missing or has an unsupported schema; recapture is required.";
        if (identity.Modules == null || identity.Modules.Count == 0)
            return "Loaded shader inventory is empty.";
        if (identity.Generation != identity.Modules.Count)
            return "Loaded shader generation does not match its inventory.";
        if (identity.FailureReason != string.Empty)
            return "Loaded shader identity is invalid: " + identity.FailureReason;
        string? previousName = null;
        foreach (LoadedShaderModuleIdentity? module in identity.Modules)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.FileName) ||
                module.FileName.Length > 512 || !IsSha256(module.Sha256) ||
                module.ByteLength <= 0 || module.ByteLength > ShaderArtifactResolver.MaximumShaderModuleBytes ||
                module.ByteLength % sizeof(uint) != 0 ||
                module.SourceKind is not ("embedded" or "override" or "deployment") ||
                string.IsNullOrWhiteSpace(module.SourceIdentity))
                return "Loaded shader inventory contains an invalid module.";
            try
            {
                if (ShaderArtifactResolver.RuntimeFileName(module.FileName) != module.FileName)
                    return "Loaded shader filename is not canonical.";
            }
            catch (ArgumentException) { return "Loaded shader filename is not canonical."; }
            if (previousName != null && StringComparer.Ordinal.Compare(previousName, module.FileName) >= 0)
                return "Loaded shader filenames must be unique and sorted ordinally.";
            previousName = module.FileName;
        }
        return identity.Fingerprint == ComputeFingerprint(identity.Modules)
            ? null : "Loaded shader fingerprint does not match its inventory.";
    }

    public static string? Compare(LoadedShaderIdentity? left, LoadedShaderIdentity? right, bool requireSameInventory)
    {
        string? failure = Validate(left) ?? Validate(right);
        if (failure != null) return failure;
        if (requireSameInventory)
            return left!.Fingerprint == right!.Fingerprint ? null : "Loaded shader identities differ.";
        var expected = left!.Modules.ToDictionary(m => m.FileName, StringComparer.Ordinal);
        foreach (LoadedShaderModuleIdentity module in right!.Modules)
            if (expected.TryGetValue(module.FileName, out var other) &&
                (other.Sha256 != module.Sha256 || other.ByteLength != module.ByteLength))
                return $"Loaded shader '{module.FileName}' differs between feature-isolation variants.";
        return null;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void AppendText(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record LoadedShaderMeasurementEvidence(
    string StartFingerprint, long StartGeneration, string EndFingerprint, long EndGeneration)
{
    public static string? Validate(LoadedShaderIdentity? identity, LoadedShaderMeasurementEvidence? measurement)
    {
        string? failure = LoadedShaderIdentity.Validate(identity);
        if (failure != null) return failure;
        if (measurement == null)
            return "Loaded shader measurement boundaries are missing; recapture is required.";
        return measurement.StartFingerprint == identity!.Fingerprint &&
               measurement.EndFingerprint == identity.Fingerprint &&
               measurement.StartGeneration == identity.Generation &&
               measurement.EndGeneration == identity.Generation
            ? null : "Loaded shader identity changed during the measurement window.";
    }
}

internal sealed class ShaderModuleIdentityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Name, string Hash), LoadedShaderModuleIdentity> _modules = new();
    private long _generation;
    private LoadedShaderIdentity _snapshot = LoadedShaderIdentity.Empty;

    internal void Record(LoadedShaderModuleIdentity module)
    {
        lock (_gate)
        {
            if (_modules.TryAdd((module.FileName, module.Sha256), module))
                Volatile.Write(ref _generation, _generation + 1);
        }
    }

    internal LoadedShaderIdentity Snapshot()
    {
        LoadedShaderIdentity snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Generation == Volatile.Read(ref _generation)) return snapshot;
        lock (_gate)
        {
            if (_snapshot.Generation == _generation) return _snapshot;
            LoadedShaderModuleIdentity[] modules = _modules.Values
                .OrderBy(m => m.FileName, StringComparer.Ordinal)
                .ThenBy(m => m.Sha256, StringComparer.Ordinal).ToArray();
            string failure = modules.Select(m => m.FileName).Distinct(StringComparer.Ordinal).Count() != modules.Length
                ? "One shader filename created modules from different bytes in this Vulkan context." : string.Empty;
            snapshot = new(LoadedShaderIdentity.CurrentSchema, LoadedShaderIdentity.ComputeFingerprint(modules),
                _generation, Array.AsReadOnly(modules), failure);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }
}
