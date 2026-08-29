using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererStartupLatencyPolicyTests
{
    [TestCase(true, 5_000_000L, true, true)]
    [TestCase(true, 5_000_001L, false, true)]
    [TestCase(true, 10_000_001L, false, false)]
    [TestCase(false, 15_000_000L, true, true)]
    [TestCase(false, 15_000_001L, false, true)]
    [TestCase(false, 30_000_001L, false, false)]
    public void EvaluationUsesWarmAndApplicationColdThresholds(
        bool applicationCacheLoaded,
        long elapsedMicroseconds,
        bool meetsTarget,
        bool meetsHardLimit)
    {
        RendererStartupLatencyEvaluation result =
            RendererStartupLatencyPolicy.Evaluate(
                elapsedMicroseconds,
                applicationCacheLoaded);

        Assert.Multiple(() =>
        {
            Assert.That(result.MeetsAspirationalTarget, Is.EqualTo(meetsTarget));
            Assert.That(result.MeetsHardLimit, Is.EqualTo(meetsHardLimit));
            Assert.That(
                result.CacheClass,
                Is.EqualTo(applicationCacheLoaded
                    ? RendererStartupCacheClass.Warm
                    : RendererStartupCacheClass.ApplicationCold));
        });
    }

    [Test]
    public void EvaluationRejectsNegativeElapsedTime()
    {
        Assert.That(
            () => RendererStartupLatencyPolicy.Evaluate(-1, true),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, true)]
    public void HardLimitFailureRequiresExplicitEnforcement(
        int gateMode,
        bool expectedFailure)
    {
        RendererStartupLatencyEvaluation evaluation =
            RendererStartupLatencyPolicy.Evaluate(
                elapsedMicroseconds: 10_000_001,
                applicationPipelineCacheLoaded: true);

        Assert.That(
            RendererStartupLatencyPolicy.ShouldFail(
                evaluation,
                (RendererStartupLatencyGateMode)gateMode),
            Is.EqualTo(expectedFailure));
    }

    [Test]
    public void CacheWarmEligibilityRequiresExactWritableProvenance()
    {
        var exact = new GiPipelineCacheTelemetry(
            CacheLoaded: true,
            RuntimeCacheLoaded: true,
            SeedCacheLoaded: false,
            CacheRejected: false,
            CacheSaved: false,
            LoadedPayloadBytes: 1,
            SavedPayloadBytes: 0,
            PipelineCreationCount: 0,
            PipelineCreationMicroseconds: 0,
            RenderCriticalPipelineCreationCount: 0,
            CachePath: "cache",
            LoadStatus: "loaded",
            LastCreatedPipeline: string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(exact.WarmEligible, Is.True);
            Assert.That(
                (exact with { ShaderBundleChanged = true }).WarmEligible,
                Is.False);
            Assert.That(
                (exact with { BuildConfigurationChanged = true }).WarmEligible,
                Is.False);
            Assert.That(
                (exact with { LegacyEnvelopeLoaded = true }).WarmEligible,
                Is.False);
            Assert.That(
                (exact with { PipelineCompileMissCount = 1 }).WarmEligible,
                Is.False);
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    SeedCacheLoaded = true
                }).WarmEligible,
                Is.False);
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    PipelineCreationCount = 3,
                    WritableBinaryHitCount = 3
                }).WarmEligible,
                Is.True);
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    PipelineCreationCount = 3,
                    WritableBinaryHitCount = 2
                }).WarmEligible,
                Is.False);
        });
    }
}
