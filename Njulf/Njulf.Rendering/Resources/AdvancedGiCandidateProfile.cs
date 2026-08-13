using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Exact authorization for an explicit measurement candidate. This is not
/// promotion evidence and is never accepted by AutoQualified.
/// </summary>
public readonly record struct AdvancedGiCandidateAuthorization(
    string AuthorizationId,
    string BuildCommit,
    string ShaderBundleSha256,
    string SettingsFingerprintSha256,
    AdvancedGiRuntimeContentBinding ContentBinding)
{
    public bool IsWellFormed =>
        AdvancedGiQualificationContract.IsCanonicalToken(
            AuthorizationId, 256) &&
        BuildCommit is { Length: >= 40 and <= 64 } &&
        string.Equals(BuildCommit, BuildCommit.ToLowerInvariant(),
            StringComparison.Ordinal) &&
        BuildCommit.All(Uri.IsHexDigit) &&
        AdvancedGiQualificationContract.NormalizeSha256(
            ShaderBundleSha256).Length == 64 &&
        AdvancedGiQualificationContract.NormalizeSha256(
            SettingsFingerprintSha256).Length == 64 &&
        ContentBinding.IsWellFormed;

    public bool MatchesRuntime(
        string buildCommit,
        string shaderBundleSha256,
        string settingsFingerprintSha256,
        in AdvancedGiRuntimeContentBinding contentBinding,
        out string reason)
    {
        if (!IsWellFormed)
        {
            reason = "advanced-gi-candidate-authorization-invalid";
            return false;
        }
        if (!string.Equals(BuildCommit, buildCommit, StringComparison.Ordinal))
        {
            reason = "advanced-gi-candidate-build-commit-mismatch";
            return false;
        }
        if (!HashEquals(ShaderBundleSha256, shaderBundleSha256))
        {
            reason = "advanced-gi-candidate-shader-bundle-mismatch";
            return false;
        }
        if (!HashEquals(SettingsFingerprintSha256,
                settingsFingerprintSha256))
        {
            reason = "advanced-gi-candidate-settings-mismatch";
            return false;
        }
        AdvancedGiRuntimeContentBinding expected = ContentBinding.Normalize();
        AdvancedGiRuntimeContentBinding actual = contentBinding.Normalize();
        if (!HashEquals(expected.CorpusSha256, actual.CorpusSha256) ||
            !string.Equals(expected.ContentProfileId,
                actual.ContentProfileId, StringComparison.Ordinal) ||
            !HashEquals(expected.SceneAssetSha256,
                actual.SceneAssetSha256))
        {
            reason = "advanced-gi-candidate-content-binding-mismatch";
            return false;
        }
        reason = "valid";
        return true;
    }

    internal static bool HashEquals(string? left, string? right)
    {
        string a = AdvancedGiQualificationContract.NormalizeSha256(left);
        string b = AdvancedGiQualificationContract.NormalizeSha256(right);
        return a.Length == 64 && b.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(a), Convert.FromHexString(b));
    }
}

public sealed record AdvancedGiCausticCandidateDocument
{
    public GiCausticAdmissionContext AdmissionContext { get; init; }
    public GiTaggedCausticCacheConfiguration Configuration { get; init; }
}

public sealed record AdvancedGiNearFieldCandidateDocument
{
    public SimpleDdgiNearFieldResidualAdmissionContext AdmissionContext
    {
        get;
        init;
    }
    public SimpleDdgiNearFieldResidualConfiguration Configuration { get; init; }
}

public sealed record AdvancedGiCandidateProfileDocument
{
    public const uint CurrentSchemaRevision = 1u;
    public uint SchemaRevision { get; init; } = CurrentSchemaRevision;
    public AdvancedGiCandidateAuthorization Authorization { get; init; }
    public AdvancedGiCausticCandidateDocument? Caustics { get; init; }
    public AdvancedGiNearFieldCandidateDocument? NearFieldResidual { get; init; }
}

public static class AdvancedGiCandidateProfileCodec
{
    private const int MaximumProfileBytes = 256 * 1024;
    private const int MaximumJsonDepth = 48;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static bool TryLoad(
        string path,
        out AdvancedGiCandidateProfileDocument? profile,
        out string failureDetail)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "advanced-gi-candidate-profile-path-empty";
            return false;
        }
        try
        {
            byte[] bytes = BoundedFileReader.ReadStable(
                Path.GetFullPath(path),
                MaximumProfileBytes,
                "Advanced GI candidate profile");
            StrictJsonContract.RejectDuplicateProperties(
                bytes,
                MaximumJsonDepth,
                "Advanced GI candidate profile");
            AdvancedGiCandidateProfileDocument document =
                JsonSerializer.Deserialize<AdvancedGiCandidateProfileDocument>(
                    bytes,
                    JsonOptions) ?? throw Invalid(
                    "advanced-gi-candidate-profile-null");
            Validate(document);
            profile = document;
            failureDetail = "valid";
            return true;
        }
        catch (FileNotFoundException)
        {
            failureDetail = "advanced-gi-candidate-profile-not-found";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failureDetail = "advanced-gi-candidate-profile-not-found";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "advanced-gi-candidate-profile-access-denied";
            return false;
        }
        catch (InvalidDataException exception)
        {
            failureDetail = exception.Message;
            return false;
        }
        catch (IOException)
        {
            failureDetail = "advanced-gi-candidate-profile-IO-failure";
            return false;
        }
        catch (JsonException)
        {
            failureDetail = "advanced-gi-candidate-profile-JSON-invalid";
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail =
                "advanced-gi-candidate-profile-JSON-shape-unsupported";
            return false;
        }
        catch (OverflowException)
        {
            failureDetail =
                "advanced-gi-candidate-profile-arithmetic-overflow";
            return false;
        }
    }

    public static string SerializeDocument(
        AdvancedGiCandidateProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static void Validate(AdvancedGiCandidateProfileDocument document)
    {
        if (document.SchemaRevision !=
            AdvancedGiCandidateProfileDocument.CurrentSchemaRevision)
        {
            throw Invalid("advanced-gi-candidate-profile-schema-mismatch");
        }
        if (!document.Authorization.IsWellFormed)
            throw Invalid("advanced-gi-candidate-authorization-invalid");
        if (document.Caustics is null && document.NearFieldResidual is null)
            throw Invalid("advanced-gi-candidate-profile-feature-missing");
        if (document.Caustics is { } caustics)
        {
            if (!caustics.AdmissionContext.TryValidate(out _) ||
                !caustics.Configuration.Enabled ||
                caustics.Configuration.MemoryBudgetBytes == 0UL)
            {
                throw Invalid("advanced-gi-C4-candidate-shape-invalid");
            }
            GiTaggedCausticCachePlan plan =
                GiTaggedCausticCacheExperiment.CreateCandidatePlan(
                    caustics.Configuration,
                    caustics.AdmissionContext,
                    document.Authorization);
            if (!plan.Active)
            {
                throw Invalid(
                    "advanced-gi-C4-candidate-plan-invalid:" + plan.Status);
            }
        }
        if (document.NearFieldResidual is { } nearField)
        {
            if (!nearField.AdmissionContext.TryValidate(out _) ||
                !nearField.Configuration.Enabled ||
                nearField.Configuration.MemoryBudgetBytes == 0UL ||
                !nearField.Configuration.SourceContract.IsValid)
            {
                throw Invalid("advanced-gi-C5-candidate-shape-invalid");
            }
            var prerequisites =
                new SimpleDdgiNearFieldResidualPrerequisites(
                    RefinementBricksActive: true,
                    RefinementQualityGatePassed: true,
                    RemainingContactScaleErrorMeasured: false,
                    SourceOwnershipImplemented: true,
                    DisocclusionRejectionImplemented: true,
                    CameraAndScreenEdgeStabilityPassed: false,
                    ReferenceErrorPerMillisecondImproved: false,
                    NoDoubleCountingOrFalseDarkening: false);
            SimpleDdgiNearFieldResidualPlan plan =
                SimpleDdgiNearFieldResidualExperiment.CreateCandidatePlan(
                    nearField.Configuration,
                    prerequisites,
                    nearField.AdmissionContext,
                    document.Authorization);
            if (!plan.Active)
            {
                throw Invalid(
                    "advanced-gi-C5-candidate-plan-invalid:" + plan.Status);
            }
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
