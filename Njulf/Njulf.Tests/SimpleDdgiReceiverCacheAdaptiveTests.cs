using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverCacheAdaptiveTests
{
    [Test]
    public void Abi_CoversTheCompleteCanonicalWorkWithoutOverflow()
    {
        const uint cacheWidth = 960u;
        const uint cacheHeight = 540u;
        const uint gatherWidth = 160u;
        const uint gatherHeight = 90u;
        ulong gatherBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredGatherWorkBytes(
                gatherWidth,
                gatherHeight);
        ulong resolveBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredResolveTileBytes(
                cacheWidth,
                cacheHeight);
        ulong missingPrefixBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredMissingPrefixBytes(
                gatherWidth,
                gatherHeight);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.TileWidth(cacheWidth),
                Is.EqualTo(120u));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.TileHeight(cacheHeight),
                Is.EqualTo(68u));
            Assert.That(gatherBytes, Is.EqualTo(115_200UL));
            Assert.That(resolveBytes, Is.EqualTo(65_280UL));
            Assert.That(missingPrefixBytes, Is.EqualTo(900UL));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi
                    .CapacitiesCoverCanonicalWork(
                        cacheWidth,
                        cacheHeight,
                        gatherWidth,
                        gatherHeight,
                        gatherBytes,
                        resolveBytes),
                Is.True);
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi
                    .CapacitiesCoverCanonicalWork(
                        cacheWidth,
                        cacheHeight,
                        gatherWidth,
                        gatherHeight,
                        gatherBytes - 1UL,
                        resolveBytes),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.GatherIndirectByteOffset,
                Is.EqualTo(16UL));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.ResolveIndirectByteOffset,
                Is.EqualTo(28UL));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi
                    .MissingFeedbackIndirectByteOffset,
                Is.EqualTo(72UL));
        });
    }

    [Test]
    public void PushAndMetadataAbi_RemainFixedAndAligned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Marshal.SizeOf<
                    GPUSimpleDdgiReceiverCacheAdaptivePushConstants>(),
                Is.EqualTo(128));
            Assert.That(
                Marshal.OffsetOf<
                    GPUSimpleDdgiReceiverCacheAdaptivePushConstants>(
                    nameof(GPUSimpleDdgiReceiverCacheAdaptivePushConstants
                        .HistoryAndPresetFlags)).ToInt32(),
                Is.EqualTo(108));
            Assert.That(
                Marshal.OffsetOf<
                    GPUSimpleDdgiReceiverCacheAdaptivePushConstants>(
                    nameof(GPUSimpleDdgiReceiverCacheAdaptivePushConstants
                        .ClassifyPhase)).ToInt32(),
                Is.EqualTo(124));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.MetadataEntryBytes,
                Is.EqualTo(16UL));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                Is.EqualTo(96UL));
            Assert.That(
                SimpleDdgiReceiverCacheAdaptiveAbi
                    .PublicationSkippedTileCountWord,
                Is.EqualTo(23u));
            Assert.That(
                SimpleDdgiReceiverPublicationAbi.ByteCount,
                Is.EqualTo(8UL));
        });
    }

    [TestCase(false, false, 0u, 0.0f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Full)]
    [TestCase(true, true, 0u, 0.0f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Full)]
    [TestCase(true, false, 1u, 0.0f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Full)]
    [TestCase(true, false, 0u, 0.05f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Half)]
    [TestCase(true, false, 0u, 0.02f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Quarter)]
    [TestCase(true, false, 0u, 0.0f, 1.0f, 0.0f, 1.0f, 0u,
        SimpleDdgiReceiverCacheRate.Reuse)]
    public void Scheduler_SelectsBoundedRate(
        bool historyValid,
        bool epochChanged,
        uint rejected,
        float depthGradient,
        float minimumNormalDot,
        float motion,
        float confidence,
        uint age,
        SimpleDdgiReceiverCacheRate expected)
    {
        SimpleDdgiReceiverCacheRate actual =
            SimpleDdgiReceiverCacheRateSelector.Select(
                new SimpleDdgiReceiverCacheRateInput(
                    historyValid,
                    epochChanged,
                    rejected,
                    depthGradient,
                    minimumNormalDot,
                    motion,
                    confidence,
                    age),
                SimpleDdgiReceiverCacheRateThresholds.ForPreset(
                    RenderQualityPreset.DdgiHigh));

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Scheduler_ForcesRefreshAtBoundedAge()
    {
        SimpleDdgiReceiverCacheRateThresholds thresholds =
            SimpleDdgiReceiverCacheRateThresholds.ForPreset(
                RenderQualityPreset.Ultra);
        SimpleDdgiReceiverCacheRate rate =
            SimpleDdgiReceiverCacheRateSelector.Select(
                new SimpleDdgiReceiverCacheRateInput(
                    true,
                    false,
                    0u,
                    0.0f,
                    1.0f,
                    0.0f,
                    1.0f,
                    thresholds.MaximumHistoryAge),
                thresholds);

        Assert.That(rate, Is.EqualTo(SimpleDdgiReceiverCacheRate.Full));
    }

    [Test]
    public void HistorySerial_RequiresOneImmediatelyPreviousFrame()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiReceiverCacheHistoryIdentity
                    .IsImmediatelyPrevious(101UL, 100UL),
                Is.True);
            Assert.That(
                SimpleDdgiReceiverCacheHistoryIdentity
                    .IsImmediatelyPrevious(102UL, 100UL),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverCacheHistoryIdentity
                    .IsImmediatelyPrevious(1UL, 0UL),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverCacheHistoryIdentity
                    .IsImmediatelyPrevious(0UL, ulong.MaxValue),
                Is.False);
        });
    }

    [Test]
    public void HistoryIdentity_PreservesCompatibleToroidalScrollHistory()
    {
        var previous = new SimpleDdgiReceiverCacheHistoryIdentity(
            960u,
            540u,
            160u,
            90u,
            1,
            2,
            3,
            4,
            10UL,
            20UL,
            30u,
            40u,
            50u,
            60u,
            70u,
            71u,
            72u,
            73u,
            74u,
            75u,
            80u,
            90UL,
            SimpleDdgiReceiverCacheMode.TemporalAdaptive,
            100u);
        SimpleDdgiReceiverCacheHistoryIdentity scrolled = previous with
        {
            VolumeResourceGeneration = 41u
        };
        SimpleDdgiReceiverCacheHistoryIdentity replaced = scrolled with
        {
            TransportTopologyGeneration = 51u
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                previous.IsHistoryCompatibleWith(scrolled),
                Is.True);
            Assert.That(
                previous.IsHistoryCompatibleWith(replaced),
                Is.False);
        });
    }

    [Test]
    public void PublicationGeneration_ReusesOnlyAnIdenticalPublication()
    {
        var tracker = new SimpleDdgiReceiverPublicationTracker();
        SimpleDdgiReceiverPublicationIdentity identity = default;
        identity = identity with
        {
            CacheWidth = 960u,
            CacheHeight = 540u,
            GatherWidth = 160u,
            GatherHeight = 90u,
            MaterialRevision = 7u,
            PublishedRadiometricGeneration = 11u,
            PublishedPropagationGeneration = 13u,
            Mode = SimpleDdgiReceiverCacheMode.TemporalAdaptive,
            ResourceGeneration = 3u
        };

        SimpleDdgiReceiverPublicationUpdate first =
            tracker.Update(identity, enabled: true, fallbackStamp: 99u);
        SimpleDdgiReceiverPublicationUpdate hit =
            tracker.Update(identity, enabled: true, fallbackStamp: 100u);
        SimpleDdgiReceiverPublicationUpdate changed = tracker.Update(
            identity with { MaterialRevision = 8u },
            enabled: true,
            fallbackStamp: 101u);

        Assert.Multiple(() =>
        {
            Assert.That(first.Stamp, Is.EqualTo(1u));
            Assert.That(first.ResetDependentCache, Is.True);
            Assert.That(
                first.ChangedRegionMask,
                Is.EqualTo(SimpleDdgiReceiverPublicationAbi.GlobalRegionBit));
            Assert.That(hit.Stamp, Is.EqualTo(first.Stamp));
            Assert.That(hit.IdentityChanged, Is.False);
            Assert.That(hit.ChangedRegionMask, Is.Zero);
            Assert.That(changed.Stamp, Is.EqualTo(2u));
            Assert.That(changed.IdentityChanged, Is.True);
            Assert.That(tracker.StableIdentityHitCount, Is.EqualTo(1UL));
            Assert.That(tracker.DirtyIdentityCount, Is.EqualTo(2UL));
        });
    }

    [Test]
    public void PublicationGeneration_ReenableAndWrapRequireClear()
    {
        var tracker = new SimpleDdgiReceiverPublicationTracker();
        SimpleDdgiReceiverPublicationIdentity identity = default;
        _ = tracker.Update(identity, enabled: true, fallbackStamp: 5u);
        SimpleDdgiReceiverPublicationUpdate baseline =
            tracker.Update(identity, enabled: false, fallbackStamp: 17u);
        SimpleDdgiReceiverPublicationUpdate reenabled =
            tracker.Update(identity, enabled: true, fallbackStamp: 18u);
        uint wrapped = SimpleDdgiReceiverPublicationTracker.NextGeneration(
            uint.MaxValue,
            out bool didWrap);

        Assert.Multiple(() =>
        {
            Assert.That(baseline.Stamp, Is.EqualTo(17u));
            Assert.That(baseline.Enabled, Is.False);
            Assert.That(reenabled.Stamp, Is.EqualTo(2u));
            Assert.That(reenabled.ResetDependentCache, Is.True);
            Assert.That(wrapped, Is.EqualTo(1u));
            Assert.That(didWrap, Is.True);
        });
    }

    [Test]
    public void FrameToken_IsGenerationAndFrameScoped()
    {
        var token = new SimpleDdgiReceiverCacheFrameToken(
            42UL,
            7u,
            1,
            960u,
            540u,
            120u,
            68u,
            new BufferHandle(1, 1u),
            new BufferHandle(2, 1u),
            new BufferHandle(3, 1u),
            new BufferHandle(4, 1u));

        Assert.Multiple(() =>
        {
            Assert.That(token.IsAvailable, Is.True);
            Assert.That(token.Matches(42UL, 7u), Is.True);
            Assert.That(token.Matches(43UL, 7u), Is.False);
            Assert.That(token.Matches(42UL, 8u), Is.False);
            Assert.That(
                SimpleDdgiReceiverCacheFrameToken.Unavailable.IsAvailable,
                Is.False);
        });
    }

    [Test]
    public void MotionHistoryPolicy_AdmitsTemporalReceiverCacheExplicitly()
    {
        var settings = new RenderSettings();
        SurfaceHistoryConsumer consumers = SurfaceHistoryPolicy.Resolve(
            settings,
            nearFieldResidualActive: false,
            simpleDdgiReceiverCacheActive: true);

        Assert.That(
            consumers.HasFlag(SurfaceHistoryConsumer.SimpleDdgiReceiverCache),
            Is.True);
        Assert.That(consumers.RequiresMotionVectors(), Is.True);
    }

    [Test]
    public void AdaptivePipelineSpecialization_TracksRowMajorFeatureControl()
    {
        var settings = new RenderSettings();
        settings.PerformanceOptimizations.EnabledFeatures =
            PerformanceOptimizationFeature.RowMajorSpatialDdgiGather |
            PerformanceOptimizationFeature.SharedDdgiResolveStaging;
        uint optimized = Njulf.Rendering.Pipeline.ForwardPlusPass
            .ResolveAdaptiveReceiverPerformanceSpecializationMask(settings);
        settings.PerformanceOptimizations.Enabled = false;
        uint rollback = Njulf.Rendering.Pipeline.ForwardPlusPass
            .ResolveAdaptiveReceiverPerformanceSpecializationMask(settings);

        Assert.Multiple(() =>
        {
            Assert.That(optimized, Is.EqualTo((uint)(
                PerformanceOptimizationFeature.RowMajorSpatialDdgiGather |
                PerformanceOptimizationFeature.SharedDdgiResolveStaging)));
            Assert.That(rollback, Is.Zero);
            Assert.That(Njulf.Rendering.Pipeline.ForwardPlusPass
                    .UsesAdaptiveReceiverPerformanceSpecialization(
                        "ddgi_simple_receiver_cache_classify.comp.spv"),
                Is.True);
            Assert.That(Njulf.Rendering.Pipeline.ForwardPlusPass
                    .UsesAdaptiveReceiverPerformanceSpecialization(
                        "ddgi_simple_receiver_cache_resolve_adaptive.comp.spv"),
                Is.True);
        });
    }

    [Test]
    public void ShaderStages_PreserveCanonicalFallbackAndIndirectOwnership()
    {
        string classify = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_receiver_cache_classify.comp");
        string gather = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_receiver_cache.comp");
        string resolve = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_receiver_cache_resolve.comp");
        string runtime = ReadRepoText(
            "Njulf.Rendering", "Pipeline",
            "ForwardPlusPass.AdaptiveReceiverCache.cs");
        string pass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string shaderProject = ReadRepoText(
            "Njulf.Shaders", "Njulf.Shaders.csproj");

        Assert.Multiple(() =>
        {
            Assert.That(classify, Does.Contain("ClassifyTile();"));
            Assert.That(classify, Does.Contain("ScheduleGatherWork();"));
            Assert.That(classify, Does.Contain(
                "NjulfRowMajorSpatialGatherEnabled()"));
            Assert.That(classify, Does.Contain(
                "uvec2(0xffffffffu)"));
            Assert.That(classify, Does.Contain(
                "? gatherCapacity"));
            Assert.That(classify, Does.Contain("FinalizeIndirectArguments();"));
            Assert.That(classify, Does.Contain("SeedCanonicalHistory();"));
            Assert.That(classify, Does.Contain(
                "SeedCanonicalGatherStamps();"));
            Assert.That(classify, Does.Contain(
                "RECEIVER_ADAPTIVE_PUBLICATION_HIT_COUNT"));
            Assert.That(classify, Does.Contain(
                "ReceiverGatherStamps.Entries[entryIndex] == pc.FrameStamp"));
            Assert.That(classify, Does.Contain(
                "CountMissingFeedbackWork();"));
            Assert.That(classify, Does.Contain(
                "PrefixMissingFeedbackWork();"));
            Assert.That(classify, Does.Contain(
                "ScatterMissingFeedbackWork();"));
            Assert.That(classify, Does.Contain(
                "ReceiverMissingPrefixes.Entries[gl_WorkGroupID.x]"));
            Assert.That(gather, Does.Contain(
                "ReceiverGatherWork.Entries[workIndex]"));
            Assert.That(gather, Does.Contain(
                "? pc.CacheWidth * pc.CacheHeight"));
            Assert.That(resolve, Does.Contain(
                "ReceiverResolveTiles.Entries[resolveWorkIndex]"));
            Assert.That(resolve, Does.Contain(
                "shared ReceiverCacheGatherCandidate ReceiverSharedGatherCandidates["));
            Assert.That(resolve, Does.Contain(
                "TryLoadGatherCandidateGlobal("));
            Assert.That(resolve, Does.Contain(
                "NjulfSharedDdgiResolveStagingEnabled()"));
            Assert.That(resolve, Does.Contain(
                "StageReceiverGatherCandidates("));
            Assert.That(resolve, Does.Contain(
                "candidate = ReceiverSharedGatherCandidates[sharedIndex]"));
            Assert.That(runtime, Does.Contain("CmdDispatchIndirect("));
            Assert.That(pass, Does.Contain(
                "ddgi_simple_receiver_cache_adaptive_b1.comp.spv"));
            Assert.That(pass, Does.Contain(
                "ddgi_simple_receiver_cache_adaptive_b1_missing.comp.spv"));
            Assert.That(runtime, Does.Contain(
                "PSpecializationInfo = &specializationInfo"));
            Assert.That(shaderProject, Does.Contain(
                "NJULF_DDGI_RECEIVER_CACHE_PRESERVE_ADAPTIVE_CONTRIBUTION=1"));
            Assert.That(shaderProject, Does.Contain(
                "NJULF_DDGI_RECEIVER_CACHE_MISSING_FEEDBACK=1"));
            Assert.That(gather, Does.Contain(
                "RECEIVER_ADAPTIVE_MISSING_FEEDBACK_COUNT"));
            Assert.That(gather, Does.Contain(
                "NJULF_PERFORMANCE_DDGI_PUBLICATION_REUSE"));
            Assert.That(runtime, Does.Contain(
                "SimpleDdgiReceiverPublicationTracker"));
            Assert.That(runtime, Does.Contain(
                "CapacitiesCoverCanonicalWork("));
            Assert.That(pass, Does.Contain(
                "FeedbackVariantRequiresExact"));
            Assert.That(pass, Does.Contain(
                "DispatchCanonicalSimpleDdgiReceiverCache("));
            Assert.That(pass, Does.Not.Contain(
                "DispatchSimpleDdgiReceiverFeedbackGather("));
        });
    }

    private static string ReadRepoText(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }
}
