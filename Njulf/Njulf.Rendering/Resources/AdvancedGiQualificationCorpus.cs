using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

public sealed record AdvancedGiQualificationCorpusArtifact
{
    public string Role { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record AdvancedGiQualificationCorpusCase
{
    public string Id { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public AdvancedGiPrerequisiteFeature[] Features { get; init; } = [];
    public AdvancedGiQualificationCorpusArtifact[] Artifacts { get; init; } = [];
}

/// <summary>
/// Portable definition of the locked reference corpus. Paths are relative to
/// the manifest directory after pinning, while the canonical identity contains
/// only stable metadata and verified artifact hashes.
/// </summary>
public sealed record AdvancedGiQualificationCorpusDocument
{
    public const uint CurrentSchemaRevision = 1u;
    public uint SchemaRevision { get; init; } = CurrentSchemaRevision;
    public string CorpusId { get; init; } = string.Empty;
    public AdvancedGiQualificationCorpusCase[] Cases { get; init; } = [];
}

public sealed record AdvancedGiVerifiedQualificationCorpus(
    string ManifestPath,
    string CorpusId,
    string CorpusSha256,
    int CaseCount,
    int ArtifactCount,
    IReadOnlySet<AdvancedGiPrerequisiteFeature> CoveredFeatures);

/// <summary>
/// Pins and verifies the complete feature-isolated reference corpus. A corpus
/// hash is emitted only after every referenced byte count and SHA-256 matches;
/// a scenario-name list by itself can never become qualification identity.
/// </summary>
public static class AdvancedGiQualificationCorpusCodec
{
    private const int MaximumManifestBytes = 512 * 1024;
    private const int MaximumJsonDepth = 32;
    private const int MaximumCases = 64;
    private const int MaximumArtifactsPerCase = 16;
    private const long MaximumArtifactBytes = 2L * 1024L * 1024L * 1024L;
    private static readonly string[] RequiredRoles =
        ["scene", "camera-script", "settings", "reference"];
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static bool TryLoadAndVerify(
        string path,
        out AdvancedGiVerifiedQualificationCorpus? corpus,
        out string failureDetail)
    {
        corpus = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "advanced-gi-corpus-manifest-path-empty";
            return false;
        }

        try
        {
            string manifestPath = Path.GetFullPath(path);
            AdvancedGiQualificationCorpusDocument document = ReadDocument(
                manifestPath);
            ValidateMetadata(document, requirePins: true);
            string root = Path.GetDirectoryName(manifestPath) ??
                throw Invalid("advanced-gi-corpus-directory-invalid");
            int artifactCount = VerifyArtifacts(root, document);
            HashSet<AdvancedGiPrerequisiteFeature> coverage = document.Cases
                .SelectMany(static item => item.Features)
                .ToHashSet();
            corpus = new AdvancedGiVerifiedQualificationCorpus(
                manifestPath,
                document.CorpusId,
                ComputeCorpusSha256(document),
                document.Cases.Length,
                artifactCount,
                coverage);
            failureDetail = "valid";
            return true;
        }
        catch (FileNotFoundException)
        {
            failureDetail = "advanced-gi-corpus-artifact-not-found";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failureDetail = "advanced-gi-corpus-artifact-not-found";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "advanced-gi-corpus-access-denied";
            return false;
        }
        catch (InvalidDataException exception)
        {
            failureDetail = exception.Message;
            return false;
        }
        catch (IOException)
        {
            failureDetail = "advanced-gi-corpus-io-failure";
            return false;
        }
        catch (JsonException)
        {
            failureDetail = "advanced-gi-corpus-json-invalid";
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail = "advanced-gi-corpus-json-shape-unsupported";
            return false;
        }
        catch (CryptographicException)
        {
            failureDetail = "advanced-gi-corpus-hash-failure";
            return false;
        }
    }

    /// <summary>
    /// Converts an unpinned request into a verified manifest. The request uses
    /// the final schema but may leave byteLength at zero and sha256 empty.
    /// Publication is same-directory atomic and is followed by a full readback
    /// verification of the visible file.
    /// </summary>
    public static AdvancedGiVerifiedQualificationCorpus Pin(
        string rootPath,
        string requestPath,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string root = Path.GetFullPath(rootPath);
        string output = Path.GetFullPath(outputPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Directory.CreateDirectory(
            Path.GetDirectoryName(output) ?? throw Invalid(
                "advanced-gi-corpus-output-directory-invalid"));
        AdvancedGiQualificationCorpusDocument request = ReadDocument(
            Path.GetFullPath(requestPath));
        ValidateMetadata(request, requirePins: false);

        AdvancedGiQualificationCorpusCase[] cases = request.Cases
            .Select(item => item with
            {
                Artifacts = item.Artifacts.Select(artifact =>
                {
                    string source = ResolveContainedArtifact(
                        root, artifact.RelativePath);
                    if (string.Equals(source, output, pathComparison))
                    {
                        throw Invalid(
                            "advanced-gi-corpus-output-collides-with-artifact");
                    }
                    FileHash hash = HashStableFile(source);
                    return artifact with
                    {
                        RelativePath = MakePortablePath(
                            Path.GetDirectoryName(output)!, source),
                        ByteLength = hash.ByteLength,
                        Sha256 = hash.Sha256
                    };
                }).ToArray()
            }).ToArray();
        var pinned = request with { Cases = cases };
        ValidateMetadata(pinned, requirePins: true);
        WriteAtomic(output, JsonSerializer.SerializeToUtf8Bytes(
            pinned, JsonOptions));

        if (!TryLoadAndVerify(
                output,
                out AdvancedGiVerifiedQualificationCorpus? verified,
                out string detail) || verified is null)
        {
            throw Invalid(
                "advanced-gi-corpus-published-readback-failed:" + detail);
        }
        return verified;
    }

    public static string SerializeDocument(
        AdvancedGiQualificationCorpusDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    internal static string ComputeCorpusSha256(
        AdvancedGiQualificationCorpusDocument document)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AdvancedGiQualificationContract.Append(
            hash, "advanced-gi-reference-corpus/v1");
        AdvancedGiQualificationContract.Append(
            hash, document.SchemaRevision.ToString(CultureInfo.InvariantCulture));
        AdvancedGiQualificationContract.Append(hash, document.CorpusId);
        foreach (AdvancedGiQualificationCorpusCase item in document.Cases
                     .OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AdvancedGiQualificationContract.Append(hash, item.Id);
            AdvancedGiQualificationContract.Append(hash, item.Scenario);
            AdvancedGiQualificationContract.Append(hash, item.Description);
            foreach (AdvancedGiPrerequisiteFeature feature in item.Features
                         .OrderBy(static feature => feature))
            {
                AdvancedGiQualificationContract.Append(
                    hash, ((byte)feature).ToString(CultureInfo.InvariantCulture));
            }
            foreach (AdvancedGiQualificationCorpusArtifact artifact in
                     item.Artifacts.OrderBy(static artifact => artifact.Role,
                             StringComparer.Ordinal)
                         .ThenBy(static artifact => artifact.RelativePath,
                             StringComparer.Ordinal))
            {
                AdvancedGiQualificationContract.Append(hash, artifact.Role);
                AdvancedGiQualificationContract.Append(
                    hash, NormalizePortablePath(artifact.RelativePath));
                AdvancedGiQualificationContract.Append(
                    hash, artifact.ByteLength.ToString(
                        CultureInfo.InvariantCulture));
                AdvancedGiQualificationContract.Append(
                    hash,
                    AdvancedGiQualificationContract.NormalizeSha256(
                        artifact.Sha256));
            }
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static AdvancedGiQualificationCorpusDocument ReadDocument(
        string path)
    {
        byte[] bytes = BoundedFileReader.ReadStable(
            path, MaximumManifestBytes, "Advanced GI corpus manifest");
        StrictJsonContract.RejectDuplicateProperties(
            bytes, MaximumJsonDepth, "Advanced GI corpus manifest");
        return JsonSerializer.Deserialize<AdvancedGiQualificationCorpusDocument>(
                   bytes, JsonOptions) ??
               throw Invalid("advanced-gi-corpus-document-null");
    }

    private static void ValidateMetadata(
        AdvancedGiQualificationCorpusDocument document,
        bool requirePins)
    {
        if (document.SchemaRevision !=
            AdvancedGiQualificationCorpusDocument.CurrentSchemaRevision)
            throw Invalid("advanced-gi-corpus-schema-mismatch");
        if (!AdvancedGiQualificationContract.IsCanonicalToken(
                document.CorpusId, 256))
            throw Invalid("advanced-gi-corpus-id-invalid");
        if (document.Cases is null || document.Cases.Length is < 1 or >
            MaximumCases)
            throw Invalid("advanced-gi-corpus-case-count-invalid");

        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var coverage = new HashSet<AdvancedGiPrerequisiteFeature>();
        foreach (AdvancedGiQualificationCorpusCase item in document.Cases)
        {
            if (!AdvancedGiQualificationContract.IsCanonicalToken(
                    item.Id, 256) || !caseIds.Add(item.Id))
                throw Invalid("advanced-gi-corpus-case-id-invalid-or-duplicate");
            if (!AdvancedGiQualificationContract.IsCanonicalToken(
                    item.Scenario, 256) ||
                !AdvancedGiQualificationContract.IsCanonicalToken(
                    item.Description, 1_024))
                throw Invalid("advanced-gi-corpus-case-metadata-invalid");
            if (item.Features is null || item.Features.Length == 0 ||
                item.Features.Any(static feature => !Enum.IsDefined(feature)) ||
                item.Features.Distinct().Count() != item.Features.Length)
                throw Invalid("advanced-gi-corpus-feature-list-invalid");
            foreach (AdvancedGiPrerequisiteFeature feature in item.Features)
                coverage.Add(feature);
            if (item.Artifacts is null || item.Artifacts.Length is < 1 or >
                MaximumArtifactsPerCase)
                throw Invalid("advanced-gi-corpus-artifact-count-invalid");

            var roles = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (AdvancedGiQualificationCorpusArtifact artifact in
                     item.Artifacts)
            {
                if (!AdvancedGiQualificationContract.IsCanonicalToken(
                        artifact.Role, 64) || !roles.Add(artifact.Role))
                    throw Invalid(
                        "advanced-gi-corpus-artifact-role-invalid-or-duplicate");
                string path = NormalizePortablePath(artifact.RelativePath);
                if (!IsSafeRelativePath(path) || !paths.Add(path))
                    throw Invalid(
                        "advanced-gi-corpus-artifact-path-invalid-or-duplicate");
                if (requirePins &&
                    (artifact.ByteLength is <= 0 or > MaximumArtifactBytes ||
                     AdvancedGiQualificationContract.NormalizeSha256(
                         artifact.Sha256).Length != 64))
                    throw Invalid("advanced-gi-corpus-artifact-pin-invalid");
                if (!requirePins && (artifact.ByteLength < 0 ||
                    artifact.ByteLength > MaximumArtifactBytes))
                    throw Invalid("advanced-gi-corpus-artifact-length-invalid");
            }
            if (RequiredRoles.Any(role => !roles.Contains(role)))
                throw Invalid("advanced-gi-corpus-required-artifact-role-missing");
        }

        foreach (AdvancedGiPrerequisiteFeature feature in
                 Enum.GetValues<AdvancedGiPrerequisiteFeature>())
        {
            if (!coverage.Contains(feature))
                throw Invalid("advanced-gi-corpus-feature-coverage-incomplete");
        }
    }

    private static int VerifyArtifacts(
        string root,
        AdvancedGiQualificationCorpusDocument document)
    {
        int count = 0;
        foreach (AdvancedGiQualificationCorpusArtifact artifact in
                 document.Cases.SelectMany(static item => item.Artifacts))
        {
            string path = ResolveContainedArtifact(
                root, artifact.RelativePath);
            FileHash actual = HashStableFile(path);
            if (actual.ByteLength != artifact.ByteLength)
                throw Invalid("advanced-gi-corpus-artifact-length-mismatch");
            string expected =
                AdvancedGiQualificationContract.NormalizeSha256(
                    artifact.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual.Sha256),
                    Convert.FromHexString(expected)))
                throw Invalid("advanced-gi-corpus-artifact-hash-mismatch");
            count++;
        }
        return count;
    }

    private static FileHash HashStableFile(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength is <= 0 or > MaximumArtifactBytes)
            throw Invalid("advanced-gi-corpus-artifact-length-invalid");
        byte[] digest = SHA256.HashData(input);
        if (input.Length != admittedLength)
            throw new IOException(
                "Advanced GI corpus artifact changed length during hashing.");
        return new FileHash(
            admittedLength,
            Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static string ResolveContainedArtifact(
        string root,
        string relativePath)
    {
        string normalized = NormalizePortablePath(relativePath);
        if (!IsSafeRelativePath(normalized))
            throw Invalid("advanced-gi-corpus-artifact-path-invalid");
        string rootFull = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        string candidate = Path.GetFullPath(
            Path.Combine(rootFull, normalized.Replace('/',
                Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(
                rootFull + Path.DirectorySeparatorChar, comparison))
            throw Invalid("advanced-gi-corpus-artifact-escapes-root");
        RejectLinkedArtifactSegments(rootFull, candidate);
        return candidate;
    }

    private static void RejectLinkedArtifactSegments(
        string root,
        string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                return;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    "advanced-gi-corpus-artifact-linked-path-rejected");
            }
        }
    }

    private static string MakePortablePath(
        string directory,
        string source)
    {
        string relative = Path.GetRelativePath(directory, source)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!IsSafeRelativePath(relative))
            throw Invalid("advanced-gi-corpus-output-cannot-reference-artifact");
        return relative;
    }

    private static string NormalizePortablePath(string? path) =>
        path?.Trim().Replace('\\', '/') ?? string.Empty;

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2_048 ||
            path.Any(char.IsControl) || Path.IsPathRooted(path) ||
            path.Contains('\\'))
            return false;
        string[] segments = path.Split('/',
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
               segments.All(static segment => segment is not "." and not "..") &&
               string.Join('/', segments) == path;
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumManifestBytes)
            throw Invalid("advanced-gi-corpus-manifest-too-large");
        string directory = Path.GetDirectoryName(path) ??
            throw Invalid("advanced-gi-corpus-output-directory-invalid");
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = MaximumJsonDepth,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static InvalidDataException Invalid(string reason) => new(reason);
    private readonly record struct FileHash(long ByteLength, string Sha256);
}
