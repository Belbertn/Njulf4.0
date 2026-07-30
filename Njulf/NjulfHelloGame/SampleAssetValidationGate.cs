using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Assets;
using Njulf.Assets.Cooked;

namespace NjulfHelloGame;

internal static class SampleAssetValidationGate
{
    private const string DefaultReportName = "sample-asset-validation-report.json";

    public static void Validate(string rootDirectory, SampleAssetManifest manifest)
    {
        if (IsBypassed())
            return;

        if (TryValidateCookedAssets(rootDirectory, manifest))
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

    private static bool TryValidateCookedAssets(string rootDirectory, SampleAssetManifest manifest)
    {
        SampleAssetReference[] assets = EnumerateManifestAssets(manifest).ToArray();
        string cookedBase = Path.Combine(rootDirectory, "Cooked");
        string platformRoot = Path.Combine(cookedBase, CookedPlatform.Current);
        string cookedRoot = Directory.Exists(platformRoot) ? platformRoot : cookedBase;
        if (assets.Any(asset => !File.Exists(Path.Combine(cookedRoot, "models", Path.GetFileNameWithoutExtension(asset.Path) + ".njmodel"))))
            return false;

        foreach (SampleAssetReference asset in assets)
        {
            string stem = Path.GetFileNameWithoutExtension(asset.Path);
            string packagePath = Path.Combine(cookedRoot, "models", stem + ".njmodel");
            string reportPath = Path.Combine(cookedRoot, "reports", stem + ".cook-report.json");
            if (!File.Exists(reportPath))
                throw new InvalidOperationException($"Cooked sample asset '{asset.Path}' is missing cook report '{reportPath}'.");
            AssetCookReport report;
            try
            {
                report = AssetCookReportJson.Read(reportPath);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException($"Cook report '{reportPath}' is invalid: {ex.Message}", ex);
            }
            if (!string.Equals(report.Status, "Succeeded", StringComparison.Ordinal))
                throw new InvalidOperationException($"Cooked sample asset '{asset.Path}' has unsuccessful cook status '{report.Status}' in '{reportPath}'.");
            using var reader = new CookedAssetReader(packagePath, CookedAssetKind.Model);
            if (reader.Header.SourceHash == 0 || report.AssetId == Guid.Empty)
                throw new InvalidOperationException($"Cooked sample asset '{asset.Path}' has incomplete identity or source-hash metadata.");
            string relativePackage = Path.GetRelativePath(cookedRoot, packagePath).Replace('\\', '/');
            if (!report.Outputs.TryGetValue(relativePackage, out ulong expectedHash) || CookedHash.File(packagePath) != expectedHash)
                throw new InvalidOperationException($"Cooked sample asset package '{packagePath}' does not match its cook report hash.");
        }
        return true;
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
