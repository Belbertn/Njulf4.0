using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Njulf.Assets.Cooked;

namespace Njulf.Assets.Validation;

public sealed record KhronosMaterialGiManifest
{
    public int SchemaVersion { get; init; }
    public string Repository { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public IReadOnlyList<KhronosMaterialGiAsset> Assets { get; init; } = Array.Empty<KhronosMaterialGiAsset>();
}

public sealed record KhronosMaterialGiAsset
{
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public string License { get; init; } = string.Empty;
    public KhronosMaterialGiExpectations Expectations { get; init; } = new();
}

public sealed record KhronosMaterialGiExpectations
{
    public int MinimumMaterialCount { get; init; }
    public int MinimumUnlitCount { get; init; }
    public int MinimumEmissiveStrengthCount { get; init; }
    public float MinimumMaximumEmissiveStrength { get; init; }
    public int MinimumOpaqueCount { get; init; }
    public int MinimumMaskCount { get; init; }
    public int MinimumBlendCount { get; init; }
    public int MinimumDoubleSidedCount { get; init; }
}

public sealed record KhronosMaterialGiManifestSnapshot(
    string Path,
    string Sha256,
    KhronosMaterialGiManifest Manifest);

/// <summary>
/// Fail-closed, offline-testable contract for the official Khronos material
/// corpus used by the release gate. Network transport is deliberately owned by
/// the CLI; this type authenticates bytes and validates imported/cooked meaning.
/// </summary>
public static partial class KhronosMaterialGiConformance
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumAssetCount = 64;
    public const long MaximumAssetBytes = 64L * 1024L * 1024L;
    public const string OfficialRepository = "https://github.com/KhronosGroup/glTF-Sample-Assets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static KhronosMaterialGiManifest LoadManifest(string path)
    {
        return LoadManifestSnapshot(path).Manifest;
    }

    public static KhronosMaterialGiManifestSnapshot LoadManifestSnapshot(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = ReadBoundedFile(
            fullPath,
            MaximumManifestBytes,
            "Khronos material manifest");
        return new KhronosMaterialGiManifestSnapshot(
            fullPath,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            DeserializeAndValidateManifest(bytes, fullPath));
    }

    public static IReadOnlyList<string> ValidateManifest(KhronosMaterialGiManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Schema {manifest.SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
        if (!IsCanonicalBoundedText(manifest.Repository, 256) ||
            !string.Equals(
                manifest.Repository.TrimEnd('/'),
                OfficialRepository,
                StringComparison.Ordinal))
        {
            errors.Add($"Repository must be the official '{OfficialRepository}'.");
        }
        if (manifest.Commit is null || !CommitPattern().IsMatch(manifest.Commit))
            errors.Add("Commit must be a full lowercase 40-character Git object ID.");
        if (manifest.Assets is null ||
            manifest.Assets.Count == 0 ||
            manifest.Assets.Count > MaximumAssetCount)
        {
            errors.Add(
                $"The manifest must contain between 1 and {MaximumAssetCount} Khronos material assets.");
            return errors;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (KhronosMaterialGiAsset? asset in manifest.Assets)
        {
            if (asset is null)
            {
                errors.Add("Asset entries cannot be null.");
                continue;
            }
            if (!IsCanonicalBoundedText(asset.Name, 128) ||
                !string.Equals(Path.GetFileName(asset.Name), asset.Name, StringComparison.Ordinal) ||
                asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !names.Add(asset.Name))
                errors.Add($"Asset name '{asset.Name}' is empty or duplicated.");
            if (!IsCanonicalBoundedText(asset.RelativePath, 512) ||
                !IsSafeRelativeAssetPath(asset.RelativePath))
                errors.Add($"Asset '{asset.Name}' has unsafe relative path '{asset.RelativePath}'.");
            if (asset.Sha256 is null || !Sha256Pattern().IsMatch(asset.Sha256))
                errors.Add($"Asset '{asset.Name}' must pin a lowercase SHA-256 digest.");
            if (asset.Bytes is <= 0 or > MaximumAssetBytes)
                errors.Add($"Asset '{asset.Name}' byte count {asset.Bytes} is outside (0, {MaximumAssetBytes}].");
            if (!IsCanonicalBoundedText(asset.License, 1024))
                errors.Add($"Asset '{asset.Name}' must retain its license attribution.");
            if (asset.Expectations is null)
            {
                errors.Add($"Asset '{asset.Name}' has no semantic expectations.");
            }
            else if (!HasValidExpectations(asset.Expectations))
            {
                errors.Add(
                    $"Asset '{asset.Name}' has negative, non-finite, or implausibly large semantic expectations.");
            }
        }
        return errors;
    }

    public static string BuildDownloadUrl(KhronosMaterialGiManifest manifest, KhronosMaterialGiAsset asset)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(asset);
        if (ValidateManifest(manifest).Count != 0 || !manifest.Assets.Contains(asset))
            throw new InvalidDataException("Only validated entries from the supplied official manifest may be downloaded.");
        return $"https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/{manifest.Commit}/{asset.RelativePath}";
    }

    public static void VerifyPayload(KhronosMaterialGiAsset asset, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (payload.Length != asset.Bytes)
            throw new InvalidDataException($"Khronos asset '{asset.Name}' is {payload.Length} bytes; expected {asset.Bytes}.");
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(asset.Sha256);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                $"Khronos asset '{asset.Name}' has an invalid pinned SHA-256 digest.",
                ex);
        }
        if (expected.Length != 32)
            throw new InvalidDataException($"Khronos asset '{asset.Name}' has an invalid pinned SHA-256 digest.");
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(payload, actual);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                $"Khronos asset '{asset.Name}' failed SHA-256 authentication: " +
                $"expected {asset.Sha256}, received {Convert.ToHexString(actual).ToLowerInvariant()}.");
        }
    }

    public static IReadOnlyList<string> ValidateImported(
        KhronosMaterialGiAsset asset,
        ModelMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(mesh);
        return ValidateMaterials(asset, mesh.Materials);
    }

    public static IReadOnlyList<string> ValidateCooked(
        KhronosMaterialGiAsset asset,
        CookedModelAsset cooked)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(cooked);
        var errors = new List<string>(ValidateMaterials(asset, cooked.Materials.Materials));
        if (!cooked.Materials.HasCompleteTransportMetadata)
            errors.Add("Cooked material table does not declare complete transport metadata.");
        if (cooked.Materials.PrimitiveTransportAlgorithmVersion != GiPrimitiveTransportProfile.CurrentAlgorithmVersion)
            errors.Add("Cooked primitive transport algorithm is stale.");
        if (cooked.Materials.PrimitiveTransportProfiles.Count != cooked.Mesh.SubMeshes.Count)
        {
            errors.Add(
                $"Cooked profile count {cooked.Materials.PrimitiveTransportProfiles.Count} does not match " +
                $"submesh count {cooked.Mesh.SubMeshes.Count}.");
        }
        foreach (GiPrimitiveTransportProfile profile in cooked.Materials.PrimitiveTransportProfiles)
        {
            foreach (string error in profile.Validate())
                errors.Add($"Primitive {profile.SubMeshIndex}: {error}");
            if ((uint)profile.MaterialSlot < (uint)cooked.Materials.Materials.Count &&
                cooked.Materials.Materials[profile.MaterialSlot].Unlit &&
                (profile.MeanDiffuseReflectance.X != 0.0 ||
                 profile.MeanDiffuseReflectance.Y != 0.0 ||
                 profile.MeanDiffuseReflectance.Z != 0.0 ||
                 profile.MeanEmission.X != 0.0 ||
                 profile.MeanEmission.Y != 0.0 ||
                 profile.MeanEmission.Z != 0.0))
            {
                errors.Add($"Unlit primitive {profile.SubMeshIndex} participates in diffuse or emissive GI.");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> ValidateMaterials(
        KhronosMaterialGiAsset asset,
        IReadOnlyList<ModelMaterial> materials)
    {
        KhronosMaterialGiExpectations expected = asset.Expectations;
        int unlit = materials.Count(static material => material.Unlit);
        int emissiveStrength = materials.Count(static material =>
            (material.FeatureFlags & ModelMaterialFeatureBits.EmissiveStrength) != 0);
        float maximumEmissiveStrength = materials.Count == 0
            ? 0f
            : materials.Max(static material => material.EmissiveStrength);
        int opaque = materials.Count(static material => material.AlphaMode == ModelAlphaMode.Opaque);
        int mask = materials.Count(static material => material.AlphaMode == ModelAlphaMode.Mask);
        int blend = materials.Count(static material => material.AlphaMode == ModelAlphaMode.Blend);
        int doubleSided = materials.Count(static material => material.DoubleSided);
        var errors = new List<string>();
        RequireMinimum(materials.Count, expected.MinimumMaterialCount, "materials", errors);
        RequireMinimum(unlit, expected.MinimumUnlitCount, "unlit materials", errors);
        RequireMinimum(emissiveStrength, expected.MinimumEmissiveStrengthCount, "emissive-strength materials", errors);
        if (maximumEmissiveStrength < expected.MinimumMaximumEmissiveStrength)
        {
            errors.Add(
                $"Maximum emissive strength {maximumEmissiveStrength} is below " +
                $"{expected.MinimumMaximumEmissiveStrength}.");
        }
        RequireMinimum(opaque, expected.MinimumOpaqueCount, "opaque materials", errors);
        RequireMinimum(mask, expected.MinimumMaskCount, "masked materials", errors);
        RequireMinimum(blend, expected.MinimumBlendCount, "blended materials", errors);
        RequireMinimum(doubleSided, expected.MinimumDoubleSidedCount, "double-sided materials", errors);
        return errors;
    }

    private static void RequireMinimum(int actual, int minimum, string name, ICollection<string> errors)
    {
        if (actual < minimum)
            errors.Add($"Found {actual} {name}; expected at least {minimum}.");
    }

    private static bool IsSafeRelativeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\'))
            return false;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
               segments.All(static segment => segment is not "." and not "..") &&
               string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidExpectations(
        KhronosMaterialGiExpectations expectations)
    {
        int[] counts =
        [
            expectations.MinimumMaterialCount,
            expectations.MinimumUnlitCount,
            expectations.MinimumEmissiveStrengthCount,
            expectations.MinimumOpaqueCount,
            expectations.MinimumMaskCount,
            expectations.MinimumBlendCount,
            expectations.MinimumDoubleSidedCount
        ];
        return counts.All(static count => count is >= 0 and <= 1_000_000) &&
               float.IsFinite(expectations.MinimumMaximumEmissiveStrength) &&
               expectations.MinimumMaximumEmissiveStrength is >= 0f and <= 1_000_000f;
    }

    private static bool IsCanonicalBoundedText(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
