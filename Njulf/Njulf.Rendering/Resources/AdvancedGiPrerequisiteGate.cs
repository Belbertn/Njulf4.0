using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Njulf.Rendering.Resources;

/// <summary>
/// The frozen contracts required by the content-dependent DDGI baseline.  The
/// list is intentionally explicit: a broad "baseline ready" boolean would
/// make it far too easy to start an experimental allocator against a
/// provisional ABI.
/// </summary>
public enum AdvancedGiPrerequisiteContract : byte
{
    ManyLightTreePdf,
    SpatialEmissivePdf,
    DirectionalRadianceShAbi,
    CurrentPoseAccelerationStructure,
    RayInstanceAndAlphaAbi,
    StableStochasticIdentity,
    SourceCacheFactorization,
    RevisionTaxonomy,
    SparsePublication,
    ContentMemoryPlan,
    ReferenceCaptureCorpus
}

public enum AdvancedGiPrerequisiteFeature : byte
{
    ReceiverFeedback,
    OpacityMicromaps,
    DirectionalGuiding,
    TaggedCaustics,
    NearFieldResidual
}

public readonly record struct AdvancedGiFrozenContractEvidence(
    AdvancedGiPrerequisiteContract Contract,
    uint AbiRevision,
    string ArtifactSha256,
    bool Verified,
    string Detail)
{
    public bool IsWellFormed => AbiRevision != 0 && Verified &&
        IsSha256(ArtifactSha256) && !string.IsNullOrWhiteSpace(Detail);

    private static bool IsSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }
}

public readonly record struct AdvancedGiPrerequisiteGateResult(
    bool Passed,
    string QualificationId,
    string FailureDetail)
{
    public static AdvancedGiPrerequisiteGateResult Missing(string detail) => new(
        false, string.Empty, string.IsNullOrWhiteSpace(detail)
            ? "advanced-gi-prerequisite-manifest-missing"
            : detail);
}

/// <summary>
/// Versioned proof manifest used at a safe allocation transition.  It does
/// not infer success from code presence or a device extension.  A caller must
/// supply every frozen artifact and the feature-specific prerequisites before
/// any advanced GI mode may allocate GPU resources.
/// </summary>
public sealed class AdvancedGiPrerequisiteManifest
{
    public const uint SchemaRevision = 1;

    private readonly Dictionary<AdvancedGiPrerequisiteContract,
        AdvancedGiFrozenContractEvidence> _evidence = new();

    public uint ManifestSchemaRevision { get; init; } = SchemaRevision;
    public bool SpatialEmissiveAndCachedRelightingQualified { get; init; }
    public bool RefinementBricksQualified { get; init; }
    public bool AlphaConformancePassed { get; init; }
    public bool FeatureIsolatedReferenceCorpusAvailable { get; init; }

    public IReadOnlyDictionary<AdvancedGiPrerequisiteContract,
        AdvancedGiFrozenContractEvidence> Evidence => _evidence;

    public void Add(in AdvancedGiFrozenContractEvidence evidence)
    {
        if (!Enum.IsDefined(evidence.Contract))
            throw new ArgumentOutOfRangeException(nameof(evidence));
        _evidence[evidence.Contract] = evidence;
    }

    public AdvancedGiPrerequisiteGateResult Evaluate(
        AdvancedGiPrerequisiteFeature feature)
    {
        if (!Enum.IsDefined(feature))
            return AdvancedGiPrerequisiteGateResult.Missing("unknown-advanced-gi-feature");
        if (ManifestSchemaRevision != SchemaRevision)
        {
            return AdvancedGiPrerequisiteGateResult.Missing(
                "advanced-gi-prerequisite-manifest-schema-mismatch");
        }
        foreach (AdvancedGiPrerequisiteContract contract in
                 Enum.GetValues<AdvancedGiPrerequisiteContract>())
        {
            if (!_evidence.TryGetValue(contract, out AdvancedGiFrozenContractEvidence evidence) ||
                !evidence.IsWellFormed)
            {
                return AdvancedGiPrerequisiteGateResult.Missing(
                    $"missing-or-unverified-frozen-contract:{contract}");
            }
        }
        if (!FeatureIsolatedReferenceCorpusAvailable)
        {
            return AdvancedGiPrerequisiteGateResult.Missing(
                "feature-isolated-reference-corpus-unavailable");
        }
        if (feature == AdvancedGiPrerequisiteFeature.OpacityMicromaps &&
            !AlphaConformancePassed)
        {
            return AdvancedGiPrerequisiteGateResult.Missing(
                "alpha-conformance-gate-not-passed");
        }
        if (feature == AdvancedGiPrerequisiteFeature.DirectionalGuiding &&
            !SpatialEmissiveAndCachedRelightingQualified)
        {
            return AdvancedGiPrerequisiteGateResult.Missing(
                "spatial-emissive-and-cached-relighting-gate-not-passed");
        }
        if (feature == AdvancedGiPrerequisiteFeature.NearFieldResidual &&
            !RefinementBricksQualified)
        {
            return AdvancedGiPrerequisiteGateResult.Missing(
                "b3-refinement-brick-gate-not-passed");
        }

        return new AdvancedGiPrerequisiteGateResult(
            true,
            ComputeQualificationId(feature),
            "frozen-prerequisite-contracts-verified");
    }

    public string ComputeQualificationId(AdvancedGiPrerequisiteFeature feature)
    {
        var text = new StringBuilder();
        text.Append("advanced-gi-prerequisite/").Append(SchemaRevision)
            .Append('/').Append((byte)feature).Append('\n');
        foreach (AdvancedGiPrerequisiteContract contract in
                 Enum.GetValues<AdvancedGiPrerequisiteContract>())
        {
            if (!_evidence.TryGetValue(contract, out AdvancedGiFrozenContractEvidence item))
                return string.Empty;
            text.Append((byte)contract).Append(':').Append(item.AbiRevision)
                .Append(':').Append(item.ArtifactSha256.ToLowerInvariant()).Append('\n');
        }
        text.Append(SpatialEmissiveAndCachedRelightingQualified ? '1' : '0')
            .Append(RefinementBricksQualified ? '1' : '0')
            .Append(AlphaConformancePassed ? '1' : '0')
            .Append(FeatureIsolatedReferenceCorpusAvailable ? '1' : '0');
        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
