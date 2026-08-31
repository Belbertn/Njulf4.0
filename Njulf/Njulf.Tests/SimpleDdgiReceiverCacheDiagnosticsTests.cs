using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverCacheDiagnosticsTests
{
    [Test]
    public void LifetimeAccumulator_IgnoresUnavailableFramesAndSumsEvidence()
    {
        var accumulator = new SimpleDdgiReceiverCacheLifetimeAccumulator();
        accumulator.Observe(SimpleDdgiReceiverCacheGpuCounters.Unavailable);
        accumulator.Observe(CreateCounters(
            resolveCandidates: 10,
            resolveValid: 8,
            forwardCandidates: 100,
            forwardAccepted: 85,
            exactFallbacks: 15,
            directionalEvaluations: 72));
        accumulator.Observe(CreateCounters(
            resolveCandidates: 4,
            resolveValid: 3,
            forwardCandidates: 40,
            forwardAccepted: 30,
            exactFallbacks: 10,
            directionalEvaluations: 25));

        SimpleDdgiReceiverCacheLifetimeCounters lifetime =
            accumulator.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(lifetime.ObservedFrameCount, Is.EqualTo(2UL));
            Assert.That(lifetime.ResolveCandidateCount, Is.EqualTo(14UL));
            Assert.That(lifetime.ResolveValidCount, Is.EqualTo(11UL));
            Assert.That(lifetime.ForwardCandidateCount, Is.EqualTo(140UL));
            Assert.That(lifetime.ForwardAcceptedCount, Is.EqualTo(115UL));
            Assert.That(lifetime.ExactFallbackFragmentCount, Is.EqualTo(25UL));
            Assert.That(lifetime.DirectionalCacheEvaluationCount,
                Is.EqualTo(97UL));
        });
    }

    [Test]
    public void LifetimeAccumulator_SaturatesInsteadOfWrapping()
    {
        var accumulator = new SimpleDdgiReceiverCacheLifetimeAccumulator();
        accumulator.Observe(CreateCounters(
            resolveCandidates: ulong.MaxValue,
            resolveValid: ulong.MaxValue,
            forwardCandidates: ulong.MaxValue,
            forwardAccepted: ulong.MaxValue,
            exactFallbacks: ulong.MaxValue,
            directionalEvaluations: ulong.MaxValue));
        accumulator.Observe(CreateCounters(
            resolveCandidates: 1,
            resolveValid: 1,
            forwardCandidates: 1,
            forwardAccepted: 1,
            exactFallbacks: 1,
            directionalEvaluations: 1));

        SimpleDdgiReceiverCacheLifetimeCounters lifetime =
            accumulator.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(lifetime.ObservedFrameCount, Is.EqualTo(2UL));
            Assert.That(lifetime.ResolveCandidateCount,
                Is.EqualTo(ulong.MaxValue));
            Assert.That(lifetime.ForwardAcceptedCount,
                Is.EqualTo(ulong.MaxValue));
            Assert.That(lifetime.DirectionalCacheEvaluationCount,
                Is.EqualTo(ulong.MaxValue));
        });
    }

    private static SimpleDdgiReceiverCacheGpuCounters CreateCounters(
        ulong resolveCandidates,
        ulong resolveValid,
        ulong forwardCandidates,
        ulong forwardAccepted,
        ulong exactFallbacks,
        ulong directionalEvaluations) => new(
            ReadbackValid: 1,
            ResolveCandidateCount: resolveCandidates,
            ResolveValidCount: resolveValid,
            ResolveInvalidOrNonFiniteRejectCount: 0,
            ResolveDepthOrPositionRejectCount: 0,
            ResolvePlaneRejectCount: 0,
            ResolveNormalRejectCount: 0,
            ResolveInsufficientSupportRejectCount: 0,
            ForwardCandidateCount: forwardCandidates,
            ForwardAcceptedCount: forwardAccepted,
            ForwardInvalidOrNonFiniteRejectCount: 0,
            ForwardDepthOrPositionRejectCount: 0,
            ForwardPlaneRejectCount: 0,
            ForwardNormalRejectCount: 0,
            ForwardInsufficientSupportRejectCount: 0,
            ExactFallbackFragmentCount: exactFallbacks,
            LegacyFragmentCount: 0,
            DirectionalCacheEvaluationCount: directionalEvaluations);
}
