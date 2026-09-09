using System.Text.Json;
using System;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;


public static class AssetCookReportJson
{
    public const int MaximumReportBytes =
        AssetArtifactFileIo.DefaultMaximumJsonBytes;

    public static AssetCookReport Read(string path)
    {
        byte[] snapshot = AssetArtifactFileIo.ReadBoundedSnapshot(
            path,
            MaximumReportBytes,
            "Asset cook report");
        try
        {
            AssetCookReport report =
                CookedJson.Deserialize<AssetCookReport>(
                snapshot,
                Path.GetFullPath(path),
                "cook report");
            if (string.IsNullOrWhiteSpace(report.SourcePath) ||
                report.AssetId == Guid.Empty ||
                string.IsNullOrWhiteSpace(report.Status) ||
                report.Textures is null ||
                report.Warnings is null ||
                report.Outputs is null)
            {
                throw new InvalidDataException(
                    $"Asset cook report '{path}' contains incomplete identity or collections.");
            }

            return report;
        }
        catch (CookedAssetFormatException exception)
        {
            throw new InvalidDataException(
                $"Asset cook report '{path}' is invalid.",
                exception);
        }
    }

    public static void WriteAtomic(string path, AssetCookReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var options = new JsonSerializerOptions(CookedJson.Options)
        {
            WriteIndented = true
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, options);
        AssetArtifactFileIo.WriteAtomic(
            path,
            payload,
            MaximumReportBytes,
            "Asset cook report");
    }
}
