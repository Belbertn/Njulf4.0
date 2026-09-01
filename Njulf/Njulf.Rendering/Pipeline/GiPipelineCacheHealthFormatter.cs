using System;
using System.Globalization;

namespace Njulf.Rendering.Pipeline;

internal readonly record struct GiPipelineCacheHealthReport(
    string Summary,
    string? Advice);

internal static class GiPipelineCacheHealthFormatter
{
    internal static GiPipelineCacheHealthReport Format(
        in GiPipelineCacheTelemetry telemetry,
        RendererPipelineBinaryCacheMode binaryMode,
        in PipelineBinaryStoreEntryCounts entryCounts)
    {
        string cacheState = telemetry.CacheRejected
            ? "rejected"
            : telemetry.RuntimeCacheLoaded
                ? "writable-loaded"
                : telemetry.SeedCacheLoaded
                    ? "seed-loaded"
                    : "empty";
        string provenance = DescribeProvenance(telemetry);
        string binaryAvailability = binaryMode ==
                                    RendererPipelineBinaryCacheMode.Off
            ? "disabled"
            : telemetry.PipelineBinaryCacheEnabled
                ? "available"
                : "unavailable";
        string summary = FormattableString.Invariant(
            $"Pipeline cache: vk={cacheState}, provenance={provenance}, payload={FormatBytes(telemetry.LoadedPayloadBytes)}, binaries={binaryMode.ToString().ToLowerInvariant()}/{binaryAvailability}, writable-entries={entryCounts.WritablePipelineCount}, seed-entries={entryCounts.SeedPipelineCount}; {telemetry.LoadStatus}");

        string? advice = null;
        if (telemetry.CacheRejected)
        {
            advice =
                "WARNING [Njulf.PipelineCache]: cached Vulkan data was " +
                "rejected; this launch will compile misses and republish " +
                "after a full-quality present.";
        }
        else if (telemetry.SeedCacheLoaded && HasStaleProvenance(telemetry))
        {
            advice =
                "WARNING [Njulf.PipelineCache]: the deployment seed has " +
                "stale provenance; unchanged pipelines may hit and changed " +
                "pipelines will compile. Refresh it with " +
                "./tools/export-pipeline-seeds.ps1 after a qualified warm run.";
        }
        else if (telemetry.RuntimeCacheLoaded &&
                 HasStaleProvenance(telemetry))
        {
            advice =
                "WARNING [Njulf.PipelineCache]: the writable cache has stale " +
                "provenance; unchanged pipelines may hit and changed " +
                "pipelines will compile before the cache is republished.";
        }

        return new GiPipelineCacheHealthReport(summary, advice);
    }

    private static bool HasStaleProvenance(
        in GiPipelineCacheTelemetry telemetry) =>
        telemetry.ShaderBundleChanged ||
        telemetry.BuildConfigurationChanged ||
        telemetry.LegacyEnvelopeLoaded;

    private static string DescribeProvenance(
        in GiPipelineCacheTelemetry telemetry)
    {
        if (telemetry.LegacyEnvelopeLoaded)
            return "legacy";
        if (telemetry.ShaderBundleChanged &&
            telemetry.BuildConfigurationChanged)
        {
            return "stale-shader-and-build";
        }
        if (telemetry.ShaderBundleChanged)
            return "stale-shader";
        if (telemetry.BuildConfigurationChanged)
            return "stale-build";
        return telemetry.CacheLoaded ? "current" : "none";
    }

    private static string FormatBytes(ulong bytes)
    {
        const double kibibyte = 1024.0;
        const double mebibyte = 1024.0 * 1024.0;
        if (bytes >= mebibyte)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes / mebibyte:0.0} MiB");
        }
        if (bytes >= kibibyte)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes / kibibyte:0.0} KiB");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }
}
