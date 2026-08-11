using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Versioned startup container for feature-specific evidence that cannot be
/// represented by the common device qualification manifest. C4 binds an exact
/// authored world/cache layout; C5 binds an exact post-B3 source/profile/layout.
/// </summary>
public sealed record AdvancedGiRuntimeEvidenceBundleDocument
{
    public const uint CurrentSchemaRevision = 1u;
    public const int MaximumDocumentBytes = 512 * 1024;

    public uint SchemaRevision { get; init; } = CurrentSchemaRevision;
    public GiCausticRuntimeEvidenceDocument? Caustics { get; init; }
    public SimpleDdgiNearFieldResidualRuntimeEvidenceDocument? NearFieldResidual
    {
        get;
        init;
    }
}

public sealed record GiCausticRuntimeEvidenceDocument
{
    public GiCausticQualificationEvidence Evidence { get; init; }
    public GiCausticAdmissionContext AdmissionContext { get; init; }
    public GiTaggedCausticCacheConfiguration Configuration { get; init; }
}

public sealed record SimpleDdgiNearFieldResidualRuntimeEvidenceDocument
{
    public SimpleDdgiNearFieldResidualQualificationEvidence Evidence
    {
        get;
        init;
    }
    public SimpleDdgiNearFieldResidualAdmissionContext AdmissionContext
    {
        get;
        init;
    }
    public SimpleDdgiNearFieldResidualConfiguration Configuration
    {
        get;
        init;
    }
}

/// <summary>
/// Strict bounded codec for the application-owned C4/C5 startup evidence.
/// Loading validates the same plans used by renderer admission; JSON success
/// alone can never make a feature active.
/// </summary>
public static class AdvancedGiRuntimeEvidenceBundleCodec
{
    private const int MaximumJsonDepth = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static string Serialize(
        AdvancedGiRuntimeEvidenceBundleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryValidate(document, out string failure))
            throw new ArgumentException(failure, nameof(document));
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        out AdvancedGiRuntimeEvidenceBundleDocument bundle,
        out string failureDetail)
    {
        bundle = new AdvancedGiRuntimeEvidenceBundleDocument();
        if (utf8Json.IsEmpty ||
            utf8Json.Length >
                AdvancedGiRuntimeEvidenceBundleDocument.MaximumDocumentBytes)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-size-invalid";
            return false;
        }

        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                utf8Json,
                MaximumJsonDepth,
                "Advanced GI runtime evidence bundle");
            AdvancedGiRuntimeEvidenceBundleDocument? parsed =
                JsonSerializer.Deserialize<
                    AdvancedGiRuntimeEvidenceBundleDocument>(
                    utf8Json,
                    JsonOptions);
            if (parsed is null)
            {
                failureDetail =
                    "advanced-gi-runtime-evidence-bundle-null";
                return false;
            }
            if (!TryValidate(parsed, out failureDetail))
                return false;

            bundle = parsed;
            failureDetail = "valid";
            return true;
        }
        catch (JsonException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-json-invalid";
            return false;
        }
        catch (InvalidDataException exception)
        {
            failureDetail = NormalizeFailure(exception.Message);
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-json-shape-unsupported";
            return false;
        }
        catch (OverflowException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-arithmetic-overflow";
            return false;
        }
    }

    public static bool TryLoad(
        string path,
        out AdvancedGiRuntimeEvidenceBundleDocument bundle,
        out string failureDetail)
    {
        bundle = new AdvancedGiRuntimeEvidenceBundleDocument();
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-path-empty";
            return false;
        }

        try
        {
            byte[] bytes = BoundedFileReader.ReadStable(
                Path.GetFullPath(path),
                AdvancedGiRuntimeEvidenceBundleDocument.MaximumDocumentBytes,
                "Advanced GI runtime evidence bundle");
            return TryDeserialize(bytes, out bundle, out failureDetail);
        }
        catch (FileNotFoundException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-not-found";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-not-found";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-access-denied";
            return false;
        }
        catch (IOException)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-io-failure";
            return false;
        }
    }

    public static bool TryValidate(
        AdvancedGiRuntimeEvidenceBundleDocument? bundle,
        out string failureDetail)
    {
        if (bundle is null)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-null";
            return false;
        }
        if (bundle.SchemaRevision !=
            AdvancedGiRuntimeEvidenceBundleDocument.CurrentSchemaRevision)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-schema-mismatch";
            return false;
        }
        if (bundle.Caustics is null && bundle.NearFieldResidual is null)
        {
            failureDetail =
                "advanced-gi-runtime-evidence-bundle-empty";
            return false;
        }

        if (bundle.Caustics is { } caustics &&
            !TryValidateCaustics(caustics, out failureDetail))
        {
            return false;
        }
        if (bundle.NearFieldResidual is { } nearField &&
            !TryValidateNearField(nearField, out failureDetail))
        {
            return false;
        }

        failureDetail = "valid";
        return true;
    }

    private static bool TryValidateCaustics(
        GiCausticRuntimeEvidenceDocument document,
        out string failureDetail)
    {
        GiCausticQualificationEvidence evidence = document.Evidence;
        GiTaggedCausticCachePlan plan =
            GiTaggedCausticCacheExperiment.CreatePlan(
                document.Configuration,
                new GiTaggedCausticCacheQualification(
                    SeparateOwnershipImplemented: true,
                    DiffuseTransportFeedDisabled: true,
                    ReferenceParityPassed:
                        evidence.CpuGpuPdfAndThroughputParity,
                    StabilityProofPassed:
                        evidence.PublicationAndMotionStabilityPassed,
                    QualityPerMillisecondImproved:
                        evidence.QualityPerMillisecondImproved),
                evidence,
                document.AdmissionContext);
        if (!plan.Active)
        {
            failureDetail = "advanced-gi-runtime-evidence-C4-invalid:" +
                NormalizeFailure(plan.Status);
            return false;
        }

        failureDetail = "valid";
        return true;
    }

    private static bool TryValidateNearField(
        SimpleDdgiNearFieldResidualRuntimeEvidenceDocument document,
        out string failureDetail)
    {
        SimpleDdgiNearFieldResidualQualificationEvidence evidence =
            document.Evidence;
        var prerequisites = new SimpleDdgiNearFieldResidualPrerequisites(
            RefinementBricksActive: true,
            RefinementQualityGatePassed: true,
            RemainingContactScaleErrorMeasured: true,
            SourceOwnershipImplemented: true,
            DisocclusionRejectionImplemented: true,
            CameraAndScreenEdgeStabilityPassed:
                evidence.TemporalStabilityVerified,
            ReferenceErrorPerMillisecondImproved:
                evidence.WholeFrameRegressionVerified,
            NoDoubleCountingOrFalseDarkening:
                evidence.SignedResidualEnergyVerified &&
                evidence.TraceSourceIndependenceVerified);
        SimpleDdgiNearFieldResidualPlan plan =
            SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                document.Configuration,
                prerequisites,
                evidence,
                document.AdmissionContext);
        if (!plan.Active)
        {
            failureDetail = "advanced-gi-runtime-evidence-C5-invalid:" +
                NormalizeFailure(plan.Status);
            return false;
        }

        failureDetail = "valid";
        return true;
    }

    private static string NormalizeFailure(string? failure)
    {
        string normalized = string.IsNullOrWhiteSpace(failure)
            ? "invalid"
            : failure.Trim();
        return normalized.Length <= 512
            ? normalized
            : normalized[..512];
    }
}
