using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderBudgetEvaluatorTests
{
    [Test]
    public void StressUnlimited_UsesFiniteCapsForStrictReportJson()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.StressUnlimited;

        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(profile.TargetFrameMilliseconds), Is.True);
            Assert.That(double.IsFinite(profile.CpuFrameBudgetMilliseconds), Is.True);
            Assert.That(double.IsFinite(profile.GpuFrameBudgetMilliseconds), Is.True);
            Assert.That(double.IsFinite(profile.GlobalIlluminationGpuBudgetMilliseconds), Is.True);
            Assert.That(double.IsFinite(profile.GlobalIlluminationCpuBudgetMilliseconds), Is.True);
            Assert.That(
                () => System.Text.Json.JsonSerializer.Serialize(profile),
                Throws.Nothing);
        });
    }

    [Test]
    public void Classification_UsesWarningAndFailureThresholds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RenderBudgetEvaluator.Classify(84, 100), Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(RenderBudgetEvaluator.Classify(86, 100), Is.EqualTo(RenderBudgetStatus.Warning));
            Assert.That(RenderBudgetEvaluator.Classify(101, 100), Is.EqualTo(RenderBudgetStatus.OverBudget));
        });
    }

    [Test]
    public void Evaluation_AcceptsSimpleDdgiDiagnostics()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 32,
            SimpleDdgiAtlasBytes = 1024
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        Assert.That(snapshot.Profile.Kind, Is.EqualTo(profile.Kind));
    }

    [Test]
    public void Evaluation_SplitsInteractiveAndScaledCertificationDeadlines()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        var samples = new SimpleDdgiLatencyDistribution(
            SimpleDdgiMutationLatencyTracker.MinimumP95SampleCount,
            1,
            1,
            1,
            1,
            0);
        SimpleDdgiMutationLatencySnapshot light =
            SimpleDdgiMutationLatencyTelemetry.Empty.Light with
            {
                FirstVisibleResponse = samples,
                AffectedRegionConvergence = samples with
                {
                    P50Frames = 8,
                    P95Frames = 8,
                    P99Frames = 8,
                    MaximumFrames = 8
                },
                CertifiedConvergence = samples with
                {
                    P50Frames = 72,
                    P95Frames = 72,
                    P99Frames = 72,
                    MaximumFrames = 72
                }
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 32,
            SimpleDdgiMutationLatency =
                SimpleDdgiMutationLatencyTelemetry.Empty with { Light = light },
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    TailConvergenceDeadlineFrames = 96
                }
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0,
                [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        BudgetMetric first = snapshot.Metrics.Single(metric =>
            metric.Name == "DDGI Light first-visible latency");
        BudgetMetric affected = snapshot.Metrics.Single(metric =>
            metric.Name == "DDGI Light affected-region latency");
        BudgetMetric certified = snapshot.Metrics.Single(metric =>
            metric.Name == "DDGI Light certified latency");

        Assert.Multiple(() =>
        {
            Assert.That(first.FailureThreshold, Is.EqualTo(1));
            Assert.That(first.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(affected.FailureThreshold, Is.EqualTo(8));
            Assert.That(affected.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(certified.FailureThreshold, Is.EqualTo(96));
            Assert.That(certified.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
        });
    }

    [Test]
    public void Evaluation_SumsExplicitPackedResourcesAndActualMirrorAllocation()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        SimpleDdgiStorageDiagnostics storage =
            SimpleDdgiStorageDiagnostics.Unavailable with
            {
                IsAvailable = true,
                CanonicalIrradianceBytes = 100UL,
                CanonicalVisibilityBytes = 200UL,
                SourceCacheBytes = 400UL,
                MirrorTotalBytes = 500UL,
                MirrorAllocatedBytes = 550UL
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 32,
            DdgiAtlasMemoryBudgetBytes = 10_000UL,
            // Deliberately misleading compatibility aggregates: the explicit
            // storage schema must be the sole authority when it is available.
            SimpleDdgiAtlasBytes = 99_999UL,
            SimpleDdgiTransportIrradianceAtlasBytes = 300UL,
            SimpleDdgiTransportSourceCacheBytes = 88_888UL,
            SimpleDdgiStorage = storage
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0,
                [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));
        BudgetMetric metric = snapshot.Metrics.Single(entry =>
            entry.Name == "DDGI atlas memory");

        Assert.That(metric.Value, Is.EqualTo(1_550.0));
    }

    [Test]
    public void Evaluation_TreatsIdleDdgiUpdateCountersAsMeasuredZeroWork()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiActiveProbeCount = 100,
            DdgiProbeUpdateRequestBudget = 64,
            DdgiProbesUpdated = 0
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0,
                [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        BudgetMetric requestBudget = snapshot.Metrics.Single(entry =>
            entry.Name == "DDGI update request budget");
        BudgetMetric updatedProbes = snapshot.Metrics.Single(entry =>
            entry.Name == "DDGI probes updated");

        Assert.Multiple(() =>
        {
            Assert.That(requestBudget.Value, Is.Zero);
            Assert.That(requestBudget.FailureThreshold, Is.EqualTo(64));
            Assert.That(requestBudget.Status,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(updatedProbes.Value, Is.Zero);
            Assert.That(updatedProbes.FailureThreshold, Is.EqualTo(99));
            Assert.That(updatedProbes.Status,
                Is.EqualTo(RenderBudgetStatus.WithinBudget));
        });
    }
}
