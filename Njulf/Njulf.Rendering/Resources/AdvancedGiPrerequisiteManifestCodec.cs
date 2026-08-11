using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Persisted, reviewable form of <see cref="AdvancedGiPrerequisiteManifest"/>.
/// The runtime never upgrades a malformed, unknown, or incomplete document:
/// callers receive an empty manifest and therefore retain the canonical GI
/// path.  This makes evidence files safe to distribute with captures without
/// allowing a partially written file to enable experimental GPU work.
/// </summary>
public sealed record AdvancedGiPrerequisiteManifestDocument(
    uint SchemaRevision,
    bool SpatialEmissiveAndCachedRelightingQualified,
    bool RefinementBricksQualified,
    bool AlphaConformancePassed,
    bool FeatureIsolatedReferenceCorpusAvailable,
    IReadOnlyList<AdvancedGiFrozenContractEvidence> Evidence);

public static class AdvancedGiPrerequisiteManifestCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    public static AdvancedGiPrerequisiteManifestDocument ToDocument(
        AdvancedGiPrerequisiteManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new AdvancedGiPrerequisiteManifestDocument(
            manifest.ManifestSchemaRevision,
            manifest.SpatialEmissiveAndCachedRelightingQualified,
            manifest.RefinementBricksQualified,
            manifest.AlphaConformancePassed,
            manifest.FeatureIsolatedReferenceCorpusAvailable,
            Enum.GetValues<AdvancedGiPrerequisiteContract>()
                .Where(contract => manifest.Evidence.ContainsKey(contract))
                .Select(contract => manifest.Evidence[contract])
                .ToArray());
    }

    public static string Serialize(AdvancedGiPrerequisiteManifest manifest) =>
        JsonSerializer.Serialize(ToDocument(manifest), JsonOptions);

    public static bool TryDeserialize(
        string? json,
        out AdvancedGiPrerequisiteManifest manifest,
        out string failureDetail)
    {
        manifest = new AdvancedGiPrerequisiteManifest();
        if (string.IsNullOrWhiteSpace(json))
        {
            failureDetail = "advanced-gi-prerequisite-manifest-empty";
            return false;
        }

        try
        {
            AdvancedGiPrerequisiteManifestDocument? document =
                JsonSerializer.Deserialize<AdvancedGiPrerequisiteManifestDocument>(json, JsonOptions);
            if (document is null)
            {
                failureDetail = "advanced-gi-prerequisite-manifest-null";
                return false;
            }

            return TryCreate(document, out manifest, out failureDetail);
        }
        catch (JsonException)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-json-invalid";
            return false;
        }
        catch (NotSupportedException)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-json-shape-unsupported";
            return false;
        }
    }

    public static bool TryLoad(
        string path,
        out AdvancedGiPrerequisiteManifest manifest,
        out string failureDetail)
    {
        manifest = new AdvancedGiPrerequisiteManifest();
        if (string.IsNullOrWhiteSpace(path))
        {
            failureDetail = "advanced-gi-prerequisite-manifest-path-empty";
            return false;
        }

        try
        {
            return TryDeserialize(File.ReadAllText(path), out manifest, out failureDetail);
        }
        catch (IOException)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-file-unreadable";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-file-access-denied";
            return false;
        }
    }

    private static bool TryCreate(
        AdvancedGiPrerequisiteManifestDocument document,
        out AdvancedGiPrerequisiteManifest manifest,
        out string failureDetail)
    {
        manifest = new AdvancedGiPrerequisiteManifest();
        if (document.SchemaRevision != AdvancedGiPrerequisiteManifest.SchemaRevision)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-schema-mismatch";
            return false;
        }
        if (document.Evidence is null)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-evidence-missing";
            return false;
        }

        var seen = new HashSet<AdvancedGiPrerequisiteContract>();
        foreach (AdvancedGiFrozenContractEvidence evidence in document.Evidence)
        {
            if (!Enum.IsDefined(evidence.Contract) || !seen.Add(evidence.Contract))
            {
                failureDetail = "advanced-gi-prerequisite-manifest-evidence-duplicate-or-unknown";
                return false;
            }
            if (!evidence.IsWellFormed)
            {
                failureDetail = "advanced-gi-prerequisite-manifest-evidence-malformed";
                return false;
            }
        }

        // Do not accept an incomplete evidence list merely because the caller
        // only wants to evaluate one feature.  The Phase 0 freeze is atomic.
        if (seen.Count != Enum.GetValues<AdvancedGiPrerequisiteContract>().Length)
        {
            failureDetail = "advanced-gi-prerequisite-manifest-evidence-incomplete";
            return false;
        }

        var loaded = new AdvancedGiPrerequisiteManifest
        {
            ManifestSchemaRevision = document.SchemaRevision,
            SpatialEmissiveAndCachedRelightingQualified =
                document.SpatialEmissiveAndCachedRelightingQualified,
            RefinementBricksQualified = document.RefinementBricksQualified,
            AlphaConformancePassed = document.AlphaConformancePassed,
            FeatureIsolatedReferenceCorpusAvailable =
                document.FeatureIsolatedReferenceCorpusAvailable
        };
        foreach (AdvancedGiFrozenContractEvidence evidence in document.Evidence)
            loaded.Add(evidence);

        manifest = loaded;
        failureDetail = "valid";
        return true;
    }
}
