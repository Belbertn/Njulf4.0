using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Durable cold-start transaction for Advanced GI. The referenced render
/// settings file is loaded before Vulkan optional features and immutable graph
/// branches are selected.
/// </summary>
public sealed record AdvancedGiStartupProfileDocument
{
    public const uint CurrentSchemaRevision = 2u;

    public uint SchemaRevision { get; init; } = CurrentSchemaRevision;
    public string RenderSettingsPath { get; init; } = string.Empty;
    public string RenderSettingsSha256 { get; init; } = string.Empty;
    public string SettingsFingerprintSha256 { get; init; } = string.Empty;
    public AdvancedGiRuntimeContentBinding ContentBinding { get; init; } =
        AdvancedGiRuntimeContentBinding.Empty;
    public string? PrerequisiteManifestPath { get; init; }
    public string? QualificationManifestPath { get; init; }
    public string? RuntimeEvidenceBundlePath { get; init; }
    public string? CandidateProfilePath { get; init; }
}

public sealed record AdvancedGiStartupProfile(
    string ProfilePath,
    RenderSettings Settings,
    string RenderSettingsSha256,
    string SettingsFingerprintSha256,
    AdvancedGiRuntimeContentBinding ContentBinding,
    string? PrerequisiteManifestPath,
    string? QualificationManifestPath,
    string? RuntimeEvidenceBundlePath,
    string? CandidateProfilePath);

public static class AdvancedGiStartupProfileCodec
{
    private const int MaximumProfileBytes = 64 * 1024;
    private const int MaximumJsonDepth = 24;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static bool TryLoad(
        string path,
        out AdvancedGiStartupProfile? profile,
        out string failureDetail)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "advanced-gi-startup-profile-path-empty";
            return false;
        }

        try
        {
            string profilePath = Path.GetFullPath(path);
            byte[] bytes = BoundedFileReader.ReadStable(
                profilePath,
                MaximumProfileBytes,
                "Advanced GI startup profile");
            StrictJsonContract.RejectDuplicateProperties(
                bytes,
                MaximumJsonDepth,
                "Advanced GI startup profile");
            AdvancedGiStartupProfileDocument document =
                JsonSerializer.Deserialize<AdvancedGiStartupProfileDocument>(
                    bytes,
                    JsonOptions) ?? throw Invalid(
                    "advanced-gi-startup-profile-null");
            if (document.SchemaRevision !=
                AdvancedGiStartupProfileDocument.CurrentSchemaRevision)
            {
                throw Invalid("advanced-gi-startup-profile-schema-mismatch");
            }
            if (!document.ContentBinding.IsWellFormed)
            {
                throw Invalid(
                    "advanced-gi-startup-profile-content-binding-invalid");
            }
            string expectedFingerprint =
                AdvancedGiQualificationContract.NormalizeSha256(
                    document.SettingsFingerprintSha256);
            if (expectedFingerprint.Length != 64)
            {
                throw Invalid(
                    "advanced-gi-startup-profile-settings-fingerprint-invalid");
            }
            string expectedSettingsSha256 =
                AdvancedGiQualificationContract.NormalizeSha256(
                    document.RenderSettingsSha256);
            if (expectedSettingsSha256.Length != 64)
            {
                throw Invalid(
                    "advanced-gi-startup-profile-render-settings-hash-invalid");
            }

            string directory = Path.GetDirectoryName(profilePath) ??
                throw Invalid("advanced-gi-startup-profile-directory-invalid");
            string settingsPath = ResolvePath(
                directory,
                document.RenderSettingsPath,
                required: true)!;
            RenderSettings settings = RenderSettings.Load(settingsPath);
            string actualSettingsSha256 =
                settings.ComputePersistenceSha256();
            if (!HashEquals(actualSettingsSha256, expectedSettingsSha256))
            {
                throw Invalid(
                    "advanced-gi-startup-profile-render-settings-hash-mismatch");
            }
            string actualFingerprint = AdvancedGiSettingsFingerprint.Compute(
                settings.GlobalIllumination);
            if (!HashEquals(actualFingerprint, expectedFingerprint))
            {
                throw Invalid(
                    "advanced-gi-startup-profile-settings-fingerprint-mismatch");
            }

            profile = new AdvancedGiStartupProfile(
                profilePath,
                settings,
                "sha256:" + expectedSettingsSha256,
                "sha256:" + expectedFingerprint,
                document.ContentBinding.Normalize(),
                ResolvePath(directory, document.PrerequisiteManifestPath),
                ResolvePath(directory, document.QualificationManifestPath),
                ResolvePath(directory, document.RuntimeEvidenceBundlePath),
                ResolvePath(directory, document.CandidateProfilePath));
            failureDetail = "valid";
            return true;
        }
        catch (FileNotFoundException)
        {
            failureDetail = "advanced-gi-startup-profile-or-settings-not-found";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failureDetail = "advanced-gi-startup-profile-or-settings-not-found";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "advanced-gi-startup-profile-access-denied";
            return false;
        }
        catch (InvalidDataException exception)
        {
            failureDetail = exception.Message;
            return false;
        }
        catch (IOException)
        {
            failureDetail = "advanced-gi-startup-profile-IO-failure";
            return false;
        }
        catch (JsonException)
        {
            failureDetail = "advanced-gi-startup-profile-JSON-invalid";
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail = "advanced-gi-startup-profile-JSON-shape-unsupported";
            return false;
        }
    }

    /// <summary>
    /// Writes the settings first and the profile last using same-directory
    /// atomic replacement, so a visible profile never references a partial
    /// settings file.
    /// </summary>
    public static void Save(
        string profilePath,
        RenderSettings settings,
        in AdvancedGiRuntimeContentBinding contentBinding,
        string? prerequisiteManifestPath = null,
        string? qualificationManifestPath = null,
        string? runtimeEvidenceBundlePath = null,
        string? candidateProfilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ArgumentNullException.ThrowIfNull(settings);
        if (!contentBinding.IsWellFormed)
        {
            throw new ArgumentException(
                "A complete corpus/content-profile/scene-asset binding is required.",
                nameof(contentBinding));
        }

        string fullProfilePath = Path.GetFullPath(profilePath);
        string directory = Path.GetDirectoryName(fullProfilePath) ??
            throw new InvalidOperationException(
                "Advanced GI startup profile has no parent directory.");
        Directory.CreateDirectory(directory);
        string renderSettingsSha256 = settings.ComputePersistenceSha256();
        string settingsFingerprint = AdvancedGiSettingsFingerprint.Compute(
            settings.GlobalIllumination);
        string normalizedFingerprint =
            AdvancedGiQualificationContract.NormalizeSha256(
                renderSettingsSha256);
        string settingsFileName =
            Path.GetFileNameWithoutExtension(fullProfilePath) + "." +
            normalizedFingerprint + ".render-settings.json";
        string settingsPath = Path.Combine(directory, settingsFileName);
        settings.Save(settingsPath);

        var document = new AdvancedGiStartupProfileDocument
        {
            RenderSettingsPath = settingsFileName,
            RenderSettingsSha256 = renderSettingsSha256,
            SettingsFingerprintSha256 = settingsFingerprint,
            ContentBinding = contentBinding.Normalize(),
            PrerequisiteManifestPath = MakePortablePath(
                directory, prerequisiteManifestPath),
            QualificationManifestPath = MakePortablePath(
                directory, qualificationManifestPath),
            RuntimeEvidenceBundlePath = MakePortablePath(
                directory, runtimeEvidenceBundlePath),
            CandidateProfilePath = MakePortablePath(
                directory, candidateProfilePath)
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            document,
            JsonOptions);
        if (payload.Length > MaximumProfileBytes)
        {
            throw new InvalidOperationException(
                "Advanced GI startup profile exceeds its bounded file size.");
        }
        WriteAtomic(fullProfilePath, payload);
    }

    public static string SerializeDocument(
        AdvancedGiStartupProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static string? ResolvePath(
        string directory,
        string? value,
        bool required = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw Invalid("advanced-gi-startup-profile-required-path-empty");
            return null;
        }
        string trimmed = value.Trim();
        if (trimmed.Length > 2_048 || trimmed.Any(char.IsControl))
            throw Invalid("advanced-gi-startup-profile-path-invalid");
        return Path.GetFullPath(
            Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(directory, trimmed));
    }

    private static string? MakePortablePath(string directory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string fullPath = Path.GetFullPath(value.Trim());
        string relative = Path.GetRelativePath(directory, fullPath);
        return relative.Length < fullPath.Length
            ? relative.Replace(Path.DirectorySeparatorChar, '/')
            : fullPath;
    }

    private static bool HashEquals(string actual, string expected)
    {
        string left = AdvancedGiQualificationContract.NormalizeSha256(actual);
        string right = AdvancedGiQualificationContract.NormalizeSha256(expected);
        return left.Length == 64 && right.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> payload)
    {
        string directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("Output path has no directory.");
        string temporary = Path.Combine(
            directory,
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

    private static InvalidDataException Invalid(string reason) => new(reason);

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
}
