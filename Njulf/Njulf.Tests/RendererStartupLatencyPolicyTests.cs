using Njulf.Core.Interfaces;
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

    [TestCase(RendererStartupMilestone.BootstrapPresent, false, false,
        3_000_000L, 5_000_000L, true)]
    [TestCase(RendererStartupMilestone.ScenePresent, false, false,
        0L, 0L, false)]
    [TestCase(RendererStartupMilestone.FullQualityPresent, true, false,
        0L, 0L, false)]
    [TestCase(RendererStartupMilestone.VisibleContentPresent, true, false,
        5_000_000L, 10_000_000L, true)]
    [TestCase(RendererStartupMilestone.VisibleContentPresent, false, true,
        15_000_000L, 30_000_000L, true)]
    [TestCase(RendererStartupMilestone.VisibleContentPresent, false, false,
        15_000_000L, 30_000_000L, true)]
    public void ProgressiveMilestonesHaveIndependentBudgets(
        RendererStartupMilestone milestone,
        bool warm,
        bool seed,
        long expectedTarget,
        long expectedHardLimit,
        bool gateApplies)
    {
        RendererStartupMilestoneLatencyEvaluation evaluation =
            RendererStartupLatencyPolicy.EvaluateMilestone(
                milestone,
                elapsedMicroseconds: 1,
                warm,
                seed);

        Assert.Multiple(() =>
        {
            Assert.That(
                evaluation.AspirationalTargetMicroseconds,
                Is.EqualTo(expectedTarget));
            Assert.That(
                evaluation.HardLimitMicroseconds,
                Is.EqualTo(expectedHardLimit));
            Assert.That(evaluation.GateApplies, Is.EqualTo(gateApplies));
        });
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
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    SeedCacheLoaded = true
                }).QualifiedSeedEligible,
                Is.True);
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    SeedCacheLoaded = true,
                    ShaderBundleChanged = true
                }).QualifiedSeedEligible,
                Is.False);
            Assert.That(
                (exact with
                {
                    RuntimeCacheLoaded = false,
                    PipelineCreationCount = 3,
                    SeedBinaryHitCount = 3
                }).QualifiedSeedEligible,
                Is.True);
        });
    }

    [Test]
    public void RenderCriticalCacheWindowWaitsForAConcreteRenderThread()
    {
        string source = File.ReadAllText(
            FindSourceFile(
                "Njulf.Rendering",
                "VulkanRenderer.cs"));
        int beginFrame = source.IndexOf(
            "public bool BeginFrame()",
            StringComparison.Ordinal);
        int ensureCanBeginFrame = source.IndexOf(
            "_lifetime.EnsureCanBeginFrame();",
            beginFrame,
            StringComparison.Ordinal);
        int markRenderCriticalFrames = source.IndexOf(
            "MarkPipelineCacheRenderCriticalFramesStarted();",
            ensureCanBeginFrame,
            StringComparison.Ordinal);
        int prepareSceneCore = source.IndexOf(
            "private void PrepareSceneCore(",
            StringComparison.Ordinal);
        int markRenderCriticalFramesMethod = source.IndexOf(
            "private void MarkPipelineCacheRenderCriticalFramesStarted()",
            prepareSceneCore,
            StringComparison.Ordinal);
        string prepareSceneSource = source[
            prepareSceneCore..markRenderCriticalFramesMethod];

        Assert.Multiple(() =>
        {
            Assert.That(beginFrame, Is.GreaterThanOrEqualTo(0));
            Assert.That(ensureCanBeginFrame, Is.GreaterThan(beginFrame));
            Assert.That(
                markRenderCriticalFrames,
                Is.GreaterThan(ensureCanBeginFrame));
            Assert.That(
                prepareSceneSource,
                Does.Not.Contain(".MarkRenderCriticalFramesStarted("));
            Assert.That(
                source,
                Does.Contain(
                    "pipelineCache.MarkRenderCriticalFramesStarted(renderThreadId);"));
            Assert.That(
                source,
                Does.Contain("renderThreadId <= 0"));
        });
    }

    private static string FindSourceFile(params string[] segments)
    {
        DirectoryInfo? directory =
            new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = directory.FullName;
            foreach (string segment in segments)
                candidate = Path.Combine(candidate, segment);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate source file '{Path.Combine(segments)}'.");
    }
}
