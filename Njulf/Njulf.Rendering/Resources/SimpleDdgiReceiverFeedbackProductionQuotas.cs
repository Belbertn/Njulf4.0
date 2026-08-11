using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Bounded producer work used to reserve the exact B1 capture ranges at a
/// resource transition. Counts describe possible producer invocations before
/// the shared sampling period is applied; they are never read from a partially
/// recorded frame.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackProductionWorkload(
    ulong SourceScreenTileCount,
    ulong FogWorkgroupCount,
    uint MaximumParticleCount,
    ulong ReflectionCaptureTileCount,
    uint MaximumTransparentLayersPerTile)
{
    public const uint DefaultMaximumTransparentLayersPerTile = 8u;
}

/// <summary>
/// Immutable production reservation policy for B1. The fallback range is
/// deliberately as large as every ordinary producer range combined because
/// any exact gather can resolve through refinement/base ownership. This keeps
/// fallback traffic out of the small shared-overflow range.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackProductionQuotaPlan(
    uint SamplingPeriod,
    uint SafetyMarginRecords,
    SimpleDdgiReceiverFeedbackProducerCapacities ProducerCapacities,
    ulong OrdinaryProducerRecordCount)
{
    public const int NonOpaqueQuotaCount = 6;

    public void WriteNonOpaqueQuotas(
        Span<SimpleDdgiReceiverFeedbackProducerQuota> destination)
    {
        if (destination.Length < NonOpaqueQuotaCount)
            throw new ArgumentException(
                "Six non-opaque B1 producer reservations are required.",
                nameof(destination));

        destination[0] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage,
            ProducerCapacities.AlphaMaskOrFoliage);
        destination[1] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit,
            ProducerCapacities.TransparentWeightedOit);
        destination[2] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.Particles,
            ProducerCapacities.Particles);
        destination[3] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.Fog,
            ProducerCapacities.Fog);
        destination[4] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.ReflectionCapture,
            ProducerCapacities.ReflectionCapture);
        destination[5] = new SimpleDdgiReceiverFeedbackProducerQuota(
            SimpleDdgiReceiverFeedbackProducer.RefinementOrBaseFallback,
            ProducerCapacities.RefinementOrBaseFallback);
    }
}

/// <summary>
/// Compiles conservative, disjoint B1 append reservations. Screen, fog, and
/// reflection work is stratified and therefore has an exact ceiling. Particle
/// identity is hash sampled to remain stable across compaction; its ceiling
/// adds an eight-sigma binomial margin and an absolute tail allowance. Any
/// rarer tail or content that exceeds the declared transparent-layer bound
/// still invalidates the complete generation in the GPU producer contract.
/// </summary>
public static class SimpleDdgiReceiverFeedbackProductionQuotaPlanner
{
    private const double ParticleSigmaMargin = 8.0;
    private const ulong ParticleAbsoluteSampleMargin = 16UL;
    private const ulong SharedOverflowDivisor = 16UL;
    private const uint MinimumSharedOverflowRecords = 256u;

    public static bool TryCompile(
        in SimpleDdgiReceiverFeedbackProductionWorkload workload,
        double screenSamplingProbability,
        uint maximumUniqueGatherOwnersPerTile,
        out SimpleDdgiReceiverFeedbackProductionQuotaPlan plan,
        out string reason)
    {
        plan = default;
        reason = string.Empty;
        if (workload.SourceScreenTileCount == 0UL ||
            !double.IsFinite(screenSamplingProbability) ||
            screenSamplingProbability <= 0.0 ||
            screenSamplingProbability > 1.0 ||
            maximumUniqueGatherOwnersPerTile == 0u ||
            maximumUniqueGatherOwnersPerTile >
                SimpleDdgiReceiverFeedbackCaptureSourceAbi
                    .MaximumUniqueGatherOwnersPerTile ||
            workload.MaximumTransparentLayersPerTile == 0u)
        {
            reason = "receiver-feedback-production-workload-invalid";
            return false;
        }

        try
        {
            ulong sampledScreenTiles = Math.Min(
                workload.SourceScreenTileCount,
                checked((ulong)Math.Ceiling(
                    workload.SourceScreenTileCount *
                    screenSamplingProbability)));
            if (sampledScreenTiles == 0UL)
            {
                reason = "receiver-feedback-production-sampling-selected-no-tiles";
                return false;
            }

            ulong samplingPeriod64 = DivideRoundUp(
                workload.SourceScreenTileCount,
                sampledScreenTiles);
            if (samplingPeriod64 == 0UL || samplingPeriod64 > uint.MaxValue)
            {
                reason = "receiver-feedback-production-sampling-period-not-representable";
                return false;
            }

            uint samplingPeriod = checked((uint)samplingPeriod64);
            ulong opaqueRecords = MultiplyOwners(
                sampledScreenTiles,
                maximumUniqueGatherOwnersPerTile);
            ulong alphaRecords = opaqueRecords;
            ulong transparentCandidates = checked(
                workload.SourceScreenTileCount *
                workload.MaximumTransparentLayersPerTile);
            ulong transparentRecords = MultiplyOwners(
                DivideRoundUp(transparentCandidates, samplingPeriod),
                maximumUniqueGatherOwnersPerTile);
            ulong particleSamples = ConservativeParticleSampleCapacity(
                workload.MaximumParticleCount,
                samplingPeriod);
            ulong particleRecords = MultiplyOwners(
                particleSamples,
                maximumUniqueGatherOwnersPerTile);
            ulong fogRecords = MultiplyOwners(
                DivideRoundUp(workload.FogWorkgroupCount, samplingPeriod),
                maximumUniqueGatherOwnersPerTile);
            ulong reflectionRecords = MultiplyOwners(
                DivideRoundUp(
                    workload.ReflectionCaptureTileCount,
                    samplingPeriod),
                maximumUniqueGatherOwnersPerTile);

            ulong ordinaryRecords = checked(
                opaqueRecords +
                alphaRecords +
                transparentRecords +
                particleRecords +
                fogRecords +
                reflectionRecords);
            // Every producer can route an exact gather to producer 6. Reserving
            // the complete ordinary ceiling is required for an all-fallback
            // frame and prevents the 256-record shared range becoming normal
            // operating capacity.
            ulong fallbackRecords = ordinaryRecords;
            ulong safetyRecords = Math.Max(
                MinimumSharedOverflowRecords,
                DivideRoundUp(ordinaryRecords, SharedOverflowDivisor));
            if (opaqueRecords > uint.MaxValue ||
                alphaRecords > uint.MaxValue ||
                transparentRecords > uint.MaxValue ||
                particleRecords > uint.MaxValue ||
                fogRecords > uint.MaxValue ||
                reflectionRecords > uint.MaxValue ||
                fallbackRecords > uint.MaxValue ||
                safetyRecords > uint.MaxValue)
            {
                reason = "receiver-feedback-production-quota-not-representable";
                return false;
            }

            plan = new SimpleDdgiReceiverFeedbackProductionQuotaPlan(
                samplingPeriod,
                checked((uint)safetyRecords),
                new SimpleDdgiReceiverFeedbackProducerCapacities(
                    checked((uint)opaqueRecords),
                    checked((uint)alphaRecords),
                    checked((uint)transparentRecords),
                    checked((uint)particleRecords),
                    checked((uint)fogRecords),
                    checked((uint)reflectionRecords),
                    checked((uint)fallbackRecords)),
                ordinaryRecords);
            reason = "valid";
            return true;
        }
        catch (OverflowException)
        {
            reason = "receiver-feedback-production-quota-overflow";
            return false;
        }
    }

    private static ulong ConservativeParticleSampleCapacity(
        uint candidateCount,
        uint samplingPeriod)
    {
        if (candidateCount == 0u)
            return 0UL;
        if (samplingPeriod <= 1u)
            return candidateCount;

        double probability = 1.0 / samplingPeriod;
        double mean = candidateCount * probability;
        double deviation = Math.Sqrt(
            candidateCount * probability * (1.0 - probability));
        ulong bounded = checked((ulong)Math.Ceiling(
            mean + ParticleSigmaMargin * deviation +
            ParticleAbsoluteSampleMargin));
        return Math.Min((ulong)candidateCount, bounded);
    }

    private static ulong MultiplyOwners(ulong samples, uint ownerCount) =>
        checked(samples * ownerCount);

    private static ulong DivideRoundUp(ulong value, ulong divisor)
    {
        if (value == 0UL)
            return 0UL;
        return checked(1UL + (value - 1UL) / divisor);
    }
}
