using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Assets;

namespace NjulfHelloGame;

internal static class SampleAssetValidationGate
{
    private const string DefaultReportName = "sample-asset-validation-report.json";

    public static void Validate(string rootDirectory, SampleAssetManifest manifest)
    {
        if (IsBypassed())
            return;

        string reportPath = Environment.GetEnvironmentVariable("NJULF_SAMPLE_ASSET_VALIDATION_REPORT") ?? Path.Combine(rootDirectory, DefaultReportName);
        if (!File.Exists(reportPath))
        {
            throw new InvalidOperationException(
                $"Sample asset validation report was not found at '{reportPath}'. " +
                "Run Njulf.AssetTool report for the sample assets, or set NJULF_SAMPLE_ALLOW_UNVALIDATED_ASSETS=true for local experiments.");
        }

        AssetValidationReport report = AssetValidationJson.ReadReport(reportPath);
        Dictionary<string, AssetValidationEntry> accepted = report.Entries
            .Where(entry => entry.Status is AssetValidationStatus.Accepted or AssetValidationStatus.AcceptedWithWarnings)
            .SelectMany(entry => new[]
            {
                new KeyValuePair<string, AssetValidationEntry>(Normalize(entry.RelativePath), entry),
                new KeyValuePair<string, AssetValidationEntry>(Normalize(Path.GetFileName(entry.AssetPath)), entry)
            })
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        foreach (SampleAssetReference asset in EnumerateManifestAssets(manifest))
        {
            string assetPath = asset.Path;
            if (!TryGetEntry(accepted, assetPath, out AssetValidationEntry? entry))
            {
                throw new InvalidOperationException(
                    $"Sample asset '{assetPath}' is not covered by a successful validation report at '{reportPath}'. " +
                    "Validate the asset before referencing it from the sample scene.");
            }

            if (entry == null)
                throw new InvalidOperationException(
                    $"Sample asset '{assetPath}' resolved to an empty validation report entry.");
            if (asset.ExpectedBackend != ModelImportBackend.Auto && entry.Backend != asset.ExpectedBackend)
            {
                throw new InvalidOperationException(
                    $"Sample asset '{assetPath}' was validated with backend '{entry.Backend}', but the sample manifest expects '{asset.ExpectedBackend}'. " +
                    "Regenerate the validation report for the selected sample import path.");
            }
        }
    }

    private static bool IsBypassed()
    {
        string? value = Environment.GetEnvironmentVariable("NJULF_SAMPLE_ALLOW_UNVALIDATED_ASSETS");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SampleAssetReference> EnumerateManifestAssets(SampleAssetManifest manifest)
    {
        yield return manifest.ModelAsset;
        foreach (SampleAssetReference asset in manifest.AddendumModelAssets)
            yield return asset;
        foreach (SampleAssetReference asset in manifest.FoliageModelAssets)
            yield return asset;
    }

    private static bool TryGetEntry(
        IReadOnlyDictionary<string, AssetValidationEntry> accepted,
        string assetPath,
        out AssetValidationEntry? entry)
    {
        return accepted.TryGetValue(Normalize(assetPath), out entry) ||
            accepted.TryGetValue(Normalize(Path.GetFileName(assetPath)), out entry);
    }

    private static string Normalize(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').TrimStart('.', '/');
    }
}
