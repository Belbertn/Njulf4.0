using System;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Sparse forward-receiver telemetry for directional shadows. Counts are sampled on a
    /// fixed 16x16 screen grid, so they are intended for relative diagnosis rather than
    /// per-pixel totals.
    /// </summary>
    public readonly record struct DirectionalShadowReceiverCounters(
        int ReadbackValid,
        uint[] PrimarySelectionCounts,
        uint[] ProjectionRejectedCounts,
        uint[] UvDepthRejectedCounts,
        uint[] FallbackCounts,
        uint[] TransitionBlendCounts,
        uint UnresolvedCount)
    {
        public static DirectionalShadowReceiverCounters Empty { get; } = new(
            ReadbackValid: 0,
            PrimarySelectionCounts: Array.Empty<uint>(),
            ProjectionRejectedCounts: Array.Empty<uint>(),
            UvDepthRejectedCounts: Array.Empty<uint>(),
            FallbackCounts: Array.Empty<uint>(),
            TransitionBlendCounts: Array.Empty<uint>(),
            UnresolvedCount: 0);
    }

    /// <summary>
    /// Capture-facing state for the directional-shadow transport path. This is deliberately
    /// grouped so snapshots keep the cascade configuration, cache state, caster coverage, and
    /// receiver-side recovery counters together.
    /// </summary>
    public sealed record DirectionalShadowRuntimeDiagnostics(
        int Enabled,
        float ConfiguredMaxDistance,
        float EffectiveNearDistance,
        float EffectiveFarDistance,
        float CascadeBlendFraction,
        float[] CascadeSplits,
        int StaticCacheActiveMask,
        int StaticCacheValidMask,
        int StaticCacheRefreshMask,
        int StaticCacheReuseMask,
        int[] StaticCandidateCounts,
        int[] StaticEmittedCounts,
        int[] StaticRejectedCounts,
        int[] StaticOverflowCounts,
        int[] DynamicCandidateCounts,
        int[] DynamicEmittedCounts,
        int[] DynamicRejectedCounts,
        int[] DynamicOverflowCounts,
        int ConservativeLodFallbackCount,
        DirectionalShadowReceiverCounters ReceiverCounters)
    {
        public static DirectionalShadowRuntimeDiagnostics Empty { get; } = new(
            Enabled: 0,
            ConfiguredMaxDistance: 0.0f,
            EffectiveNearDistance: 0.0f,
            EffectiveFarDistance: 0.0f,
            CascadeBlendFraction: 0.0f,
            CascadeSplits: Array.Empty<float>(),
            StaticCacheActiveMask: 0,
            StaticCacheValidMask: 0,
            StaticCacheRefreshMask: 0,
            StaticCacheReuseMask: 0,
            StaticCandidateCounts: Array.Empty<int>(),
            StaticEmittedCounts: Array.Empty<int>(),
            StaticRejectedCounts: Array.Empty<int>(),
            StaticOverflowCounts: Array.Empty<int>(),
            DynamicCandidateCounts: Array.Empty<int>(),
            DynamicEmittedCounts: Array.Empty<int>(),
            DynamicRejectedCounts: Array.Empty<int>(),
            DynamicOverflowCounts: Array.Empty<int>(),
            ConservativeLodFallbackCount: 0,
            ReceiverCounters: DirectionalShadowReceiverCounters.Empty);
    }
}
