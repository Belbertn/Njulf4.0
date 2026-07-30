using System.Security.Cryptography;
using System.Text.Json;

namespace Njulf.Assets.Validation;

/// <summary>
/// Durable schema emitted by the semantic Khronos material/GI gate.
/// Keeping the reader contract in the runtime-independent Assets assembly lets
/// release hosts authenticate the prior gate without referencing AssetTool.
/// </summary>
public sealed record KhronosMaterialGiGateReport
{
    public int SchemaVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public bool Offline { get; init; }
    public IReadOnlyList<KhronosMaterialGiGateEntry> Entries { get; init; } =
        Array.Empty<KhronosMaterialGiGateEntry>();
}

public sealed record KhronosMaterialGiGateEntry
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public string ImportBackend { get; init; } = string.Empty;
    public string ImportBackendVersion { get; init; } = string.Empty;
    public int MaterialCount { get; init; }
    public int SubMeshCount { get; init; }
    public int PrimitiveProfileCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Failure { get; init; }
    public long ElapsedMilliseconds { get; init; }
}

public sealed record KhronosMaterialGiAuthenticatedGate(
    string ManifestPath,
    string GateReportPath,
    string ManifestSha256,
    string GateReportSha256,
    KhronosMaterialGiManifest Manifest,
    KhronosMaterialGiGateReport GateReport);

public static partial class KhronosMaterialGiConformance
{
    public const int GateReportSchemaVersion = 1;
    public const int MaximumGateReportBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Authenticates a previously passed semantic gate against the exact
    /// manifest bytes supplied to this invocation. Extra, missing, failed, or
    /// semantically inconsistent entries are rejected.
    /// </summary>
    public static KhronosMaterialGiAuthenticatedGate AuthenticatePassedGate(
        string manifestPath,
        string gateReportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateReportPath);

        string fullManifestPath = Path.GetFullPath(manifestPath);
        string fullReportPath = Path.GetFullPath(gateReportPath);
        byte[] manifestBytes = ReadBoundedFile(
            fullManifestPath,
            MaximumManifestBytes,
            "Khronos material manifest");
        byte[] reportBytes = ReadBoundedFile(
            fullReportPath,
            MaximumGateReportBytes,
            "Khronos material semantic-gate report");

        KhronosMaterialGiManifest manifest =
            DeserializeAndValidateManifest(manifestBytes, fullManifestPath);
        KhronosMaterialGiGateReport report;
        try
        {
            report = JsonSerializer.Deserialize<KhronosMaterialGiGateReport>(
                reportBytes,
                JsonOptions) ?? throw new InvalidDataException(
                $"Khronos material semantic-gate report '{fullReportPath}' deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Khronos material semantic-gate report '{fullReportPath}' is not valid JSON.",
                exception);
        }

        string manifestSha256 = ComputeSha256(manifestBytes);
        string reportSha256 = ComputeSha256(reportBytes);
        IReadOnlyList<string> errors = ValidatePassedGateReport(
            manifest,
            manifestSha256,
            report);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(
                $"Khronos material semantic-gate report '{fullReportPath}' is not authenticated: " +
                string.Join(" ", errors));
        }

        return new KhronosMaterialGiAuthenticatedGate(
            fullManifestPath,
            fullReportPath,
            manifestSha256,
            reportSha256,
            manifest,
            report);
    }

    public static IReadOnlyList<string> ValidatePassedGateReport(
        KhronosMaterialGiManifest manifest,
        string manifestSha256,
        KhronosMaterialGiGateReport report)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        ArgumentNullException.ThrowIfNull(report);

        var errors = new List<string>();
        if (report.SchemaVersion != GateReportSchemaVersion)
        {
            errors.Add(
                $"Gate schema {report.SchemaVersion} is unsupported; expected {GateReportSchemaVersion}.");
        }
        if (!string.Equals(report.Status, "Passed", StringComparison.Ordinal))
            errors.Add($"Gate status must be 'Passed', not '{report.Status}'.");
        if (!string.Equals(report.Repository, OfficialRepository, StringComparison.Ordinal))
            errors.Add("Gate repository does not identify the official Khronos source.");
        if (!string.Equals(report.Commit, manifest.Commit, StringComparison.Ordinal))
            errors.Add("Gate commit does not match the pinned manifest commit.");
        if (!FixedTimeSha256Equals(report.ManifestSha256, manifestSha256))
            errors.Add("Gate manifest SHA-256 does not match the supplied manifest bytes.");
        if (report.StartedAtUtc == default ||
            report.CompletedAtUtc == default ||
            report.CompletedAtUtc < report.StartedAtUtc)
        {
            errors.Add("Gate timestamps are missing or out of order.");
        }

        IReadOnlyList<KhronosMaterialGiGateEntry> entries =
            report.Entries ?? Array.Empty<KhronosMaterialGiGateEntry>();
        if (entries.Count != manifest.Assets.Count)
        {
            errors.Add(
                $"Gate contains {entries.Count} entries; the manifest requires {manifest.Assets.Count}.");
        }

        var entriesByName = new Dictionary<string, KhronosMaterialGiGateEntry>(
            StringComparer.Ordinal);
        foreach (KhronosMaterialGiGateEntry? entry in entries)
        {
            if (entry is null)
            {
                errors.Add("Gate entries cannot be null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Name) ||
                !entriesByName.TryAdd(entry.Name, entry))
            {
                errors.Add($"Gate entry name '{entry.Name}' is empty or duplicated.");
            }
        }

        foreach (KhronosMaterialGiAsset asset in manifest.Assets)
        {
            if (!entriesByName.TryGetValue(asset.Name, out KhronosMaterialGiGateEntry? entry))
            {
                errors.Add($"Gate is missing required asset '{asset.Name}'.");
                continue;
            }

            if (!string.Equals(entry.Status, "Passed", StringComparison.Ordinal))
                errors.Add($"Gate entry '{asset.Name}' did not pass.");
            if (!FixedTimeSha256Equals(entry.Sha256, asset.Sha256))
                errors.Add($"Gate entry '{asset.Name}' source SHA-256 does not match the manifest.");
            if (entry.Bytes != asset.Bytes)
            {
                errors.Add(
                    $"Gate entry '{asset.Name}' reports {entry.Bytes} bytes; expected {asset.Bytes}.");
            }
            if (!string.IsNullOrWhiteSpace(entry.Failure))
                errors.Add($"Gate entry '{asset.Name}' retains a failure diagnostic.");
            if (string.IsNullOrWhiteSpace(entry.ImportBackend) ||
                string.IsNullOrWhiteSpace(entry.ImportBackendVersion))
            {
                errors.Add($"Gate entry '{asset.Name}' has no importer provenance.");
            }
            if (entry.MaterialCount < asset.Expectations.MinimumMaterialCount)
            {
                errors.Add(
                    $"Gate entry '{asset.Name}' reports {entry.MaterialCount} materials; " +
                    $"at least {asset.Expectations.MinimumMaterialCount} are required.");
            }
            if (entry.SubMeshCount <= 0)
                errors.Add($"Gate entry '{asset.Name}' reports no submeshes.");
            if (entry.PrimitiveProfileCount != entry.SubMeshCount)
            {
                errors.Add(
                    $"Gate entry '{asset.Name}' has {entry.PrimitiveProfileCount} primitive profiles " +
                    $"for {entry.SubMeshCount} submeshes.");
            }
            if (entry.Warnings is null)
                errors.Add($"Gate entry '{asset.Name}' has a null warnings collection.");
            if (entry.ElapsedMilliseconds < 0)
                errors.Add($"Gate entry '{asset.Name}' has a negative elapsed duration.");
        }

        foreach (string extraName in entriesByName.Keys.Except(
                     manifest.Assets.Select(static asset => asset.Name),
                     StringComparer.Ordinal))
        {
            errors.Add($"Gate contains unexpected asset '{extraName}'.");
        }

        return errors;
    }

    private static KhronosMaterialGiManifest DeserializeAndValidateManifest(
        ReadOnlySpan<byte> json,
        string source)
    {
        KhronosMaterialGiManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<KhronosMaterialGiManifest>(
                json,
                JsonOptions) ?? throw new InvalidDataException(
                $"Khronos material manifest '{source}' deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Khronos material manifest '{source}' is not valid JSON.",
                exception);
        }

        IReadOnlyList<string> errors = ValidateManifest(manifest);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(
                $"Khronos material manifest '{source}' is invalid: {string.Join(" ", errors)}");
        }

        return manifest;
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, string description)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        long length = stream.Length;
        if (length <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"{description} '{path}' is {length} bytes; expected a size in (0, {maximumBytes}].");
        }

        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        if (stream.Length != length)
            throw new IOException($"{description} '{path}' changed while it was being authenticated.");
        return bytes;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeSha256Equals(string? left, string? right)
    {
        if (left is null || right is null ||
            !Sha256Pattern().IsMatch(left) ||
            !Sha256Pattern().IsMatch(right))
        {
            return false;
        }

        byte[] leftBytes = Convert.FromHexString(left);
        byte[] rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
