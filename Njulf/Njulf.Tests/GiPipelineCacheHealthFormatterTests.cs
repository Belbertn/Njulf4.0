using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiPipelineCacheHealthFormatterTests
{
    [Test]
    public void Format_CurrentWritableCacheReportsModePayloadAndEntryCounts()
    {
        GiPipelineCacheTelemetry telemetry = GiPipelineCacheTelemetry.Empty with
        {
            CacheLoaded = true,
            RuntimeCacheLoaded = true,
            LoadedPayloadBytes = 2UL * 1024 * 1024,
            PipelineBinaryCacheEnabled = true,
            LoadStatus = "Compatible writable cache loaded."
        };

        GiPipelineCacheHealthReport report =
            GiPipelineCacheHealthFormatter.Format(
                telemetry,
                RendererPipelineBinaryCacheMode.Auto,
                new PipelineBinaryStoreEntryCounts(17, 5));

        Assert.Multiple(() =>
        {
            Assert.That(report.Summary,
                Does.Contain("vk=writable-loaded, provenance=current"));
            Assert.That(report.Summary, Does.Contain("payload=2.0 MiB"));
            Assert.That(report.Summary,
                Does.Contain("binaries=auto/available"));
            Assert.That(report.Summary,
                Does.Contain("writable-entries=17, seed-entries=5"));
            Assert.That(report.Advice, Is.Null);
        });
    }

    [Test]
    public void Format_StaleSeedExplainsPartialReuseAndRefreshCommand()
    {
        GiPipelineCacheTelemetry telemetry = GiPipelineCacheTelemetry.Empty with
        {
            CacheLoaded = true,
            SeedCacheLoaded = true,
            ShaderBundleChanged = true,
            LoadStatus = "Compatible cache loaded from a different shader bundle."
        };

        GiPipelineCacheHealthReport report =
            GiPipelineCacheHealthFormatter.Format(
                telemetry,
                RendererPipelineBinaryCacheMode.Auto,
                PipelineBinaryStoreEntryCounts.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(report.Summary,
                Does.Contain("vk=seed-loaded, provenance=stale-shader"));
            Assert.That(report.Advice,
                Does.Contain("unchanged pipelines may hit"));
            Assert.That(report.Advice,
                Does.Contain("./tools/export-pipeline-seeds.ps1"));
        });
    }

    [Test]
    public void Format_RejectedCacheWarnsThatMissesWillCompile()
    {
        GiPipelineCacheTelemetry telemetry = GiPipelineCacheTelemetry.Empty with
        {
            CacheRejected = true,
            LoadStatus = "Driver rejected cached data."
        };

        GiPipelineCacheHealthReport report =
            GiPipelineCacheHealthFormatter.Format(
                telemetry,
                RendererPipelineBinaryCacheMode.Off,
                PipelineBinaryStoreEntryCounts.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(report.Summary, Does.Contain("vk=rejected"));
            Assert.That(report.Summary,
                Does.Contain("binaries=off/disabled"));
            Assert.That(report.Advice,
                Does.Contain("this launch will compile misses"));
        });
    }

    [Test]
    public void Format_UnsupportedBinaryExtensionIsDistinctFromOff()
    {
        GiPipelineCacheHealthReport report =
            GiPipelineCacheHealthFormatter.Format(
                GiPipelineCacheTelemetry.Empty,
                RendererPipelineBinaryCacheMode.Capture,
                PipelineBinaryStoreEntryCounts.Empty);

        Assert.That(report.Summary,
            Does.Contain("binaries=capture/unavailable"));
    }
}
