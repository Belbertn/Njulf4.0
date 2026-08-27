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
                Is.EqualTo(80UL));
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

        Assert.Multiple(() =>
        {
            Assert.That(classify, Does.Contain("ClassifyTile();"));
            Assert.That(classify, Does.Contain("CompactGatherWork();"));
            Assert.That(classify, Does.Contain("FinalizeIndirectArguments();"));
            Assert.That(classify, Does.Contain("SeedCanonicalHistory();"));
            Assert.That(gather, Does.Contain(
                "ReceiverGatherWork.Entries[workIndex]"));
            Assert.That(resolve, Does.Contain(
                "ReceiverResolveTiles.Entries[resolveWorkIndex]"));
            Assert.That(runtime, Does.Contain("CmdDispatchIndirect("));
            Assert.That(runtime, Does.Contain(
                "CapacitiesCoverCanonicalWork("));
            Assert.That(pass, Does.Contain(
                "FeedbackVariantRequiresExact"));
            Assert.That(pass, Does.Contain(
                "DispatchCanonicalSimpleDdgiReceiverCache("));
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
