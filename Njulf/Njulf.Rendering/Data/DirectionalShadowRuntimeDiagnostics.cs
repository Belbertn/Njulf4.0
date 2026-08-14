using System;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Logical state of a static directional-shadow cache layer.  A clear-only
    /// refresh is still <see cref="Valid"/> on a later frame; emptiness is not
    /// a validity signal.
    /// </summary>
    public enum DirectionalShadowCacheLayerState
    {
        Invalid = 0,
        RefreshRecorded = 1,
        Valid = 2
    }

    /// <summary>
    /// Per-cascade provenance for the working directional shadow map.  This is
    /// capture evidence only; it never feeds delayed diagnostic counts back
    /// into same-frame shadow sampling.
    /// </summary>
    public readonly record struct DirectionalShadowCacheLayerProvenance(
        int CascadeIndex,
        int Active,
        ulong CacheSignature,
        uint ResourceGeneration,
        DirectionalShadowCacheLayerState CacheState,
        int CopiedFromCache,
        int RefreshedThisFrame,
        int ExplicitlyCleared,
        int DynamicWorkAppended,
        int FoliageWorkAppended,
        int FinalWorkingLayerValid,
        ulong SubmissionSerial)
    {
        public static DirectionalShadowCacheLayerProvenance Invalid(int cascadeIndex) => new(
            CascadeIndex: cascadeIndex,
            Active: 0,
            CacheSignature: 0UL,
            ResourceGeneration: 0u,
            CacheState: DirectionalShadowCacheLayerState.Invalid,
            CopiedFromCache: 0,
            RefreshedThisFrame: 0,
            ExplicitlyCleared: 0,
            DynamicWorkAppended: 0,
            FoliageWorkAppended: 0,
            FinalWorkingLayerValid: 0,
            SubmissionSerial: 0UL);
    }

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
        uint[] PrimaryResolvedCounts,
        uint[] ClearDepthFootprintCounts,
        uint[] PrimaryFullyLitCounts,
        uint[] PrimaryPartiallyShadowedCounts,
        uint[] PrimaryFullyShadowedCounts,
        uint[] FinalFullyLitCounts,
        uint[] FinalPartiallyShadowedCounts,
        uint[] FinalFullyShadowedCounts,
        float[] AverageReceiverDepths,
        float[] AverageMinimumSampledDepths,
        float[] AverageMaximumSampledDepths,
        uint UnresolvedCount)
    {
        public static DirectionalShadowReceiverCounters Empty { get; } = new(
            ReadbackValid: 0,
            PrimarySelectionCounts: Array.Empty<uint>(),
            ProjectionRejectedCounts: Array.Empty<uint>(),
            UvDepthRejectedCounts: Array.Empty<uint>(),
            FallbackCounts: Array.Empty<uint>(),
            TransitionBlendCounts: Array.Empty<uint>(),
            PrimaryResolvedCounts: Array.Empty<uint>(),
            ClearDepthFootprintCounts: Array.Empty<uint>(),
            PrimaryFullyLitCounts: Array.Empty<uint>(),
            PrimaryPartiallyShadowedCounts: Array.Empty<uint>(),
            PrimaryFullyShadowedCounts: Array.Empty<uint>(),
            FinalFullyLitCounts: Array.Empty<uint>(),
            FinalPartiallyShadowedCounts: Array.Empty<uint>(),
            FinalFullyShadowedCounts: Array.Empty<uint>(),
            AverageReceiverDepths: Array.Empty<float>(),
            AverageMinimumSampledDepths: Array.Empty<float>(),
            AverageMaximumSampledDepths: Array.Empty<float>(),
            UnresolvedCount: 0);
    }

    /// <summary>Fence-complete traversal and denoiser counters for one frame slot.</summary>
    public readonly record struct DirectionalShadowRayCounters(
        int ReadbackValid,
        uint OpaqueRaysIssued,
        uint OpaqueRaysSkipped,
        uint OpaqueHits,
        uint OpaqueMisses,
        uint OpaqueCandidateCount,
        uint OpaqueAlphaSampleCount,
        uint OpaqueCandidateCapHits,
        uint InvalidReceiverCount,
        uint BoundsRejectionCount,
        uint TemporalAcceptedCount,
        uint TemporalRejectedCount,
        uint SpatialFilteredPixelCount,
        uint TransparentRaysIssued,
        uint TransparentHits,
        uint TransparentMisses,
        uint TransparentCandidateCount,
        uint TransparentAlphaSampleCount,
        uint TransparentCandidateCapHits,
        uint TransparentBoundsRejectionCount)
    {
        public static DirectionalShadowRayCounters Empty { get; } = default;

        public float OpaqueHitRate => OpaqueRaysIssued == 0u
            ? 0f
            : (float)OpaqueHits / OpaqueRaysIssued;
        public float TransparentHitRate => TransparentRaysIssued == 0u
            ? 0f
            : (float)TransparentHits / TransparentRaysIssued;
        public float AverageOpaqueCandidates => OpaqueRaysIssued == 0u
            ? 0f
            : (float)OpaqueCandidateCount / OpaqueRaysIssued;
        public float AverageTransparentCandidates => TransparentRaysIssued == 0u
            ? 0f
            : (float)TransparentCandidateCount / TransparentRaysIssued;
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

        /// <summary>Layer-by-layer source evidence for the final working map.</summary>
        public IReadOnlyList<DirectionalShadowCacheLayerProvenance> CacheLayerProvenance { get; init; } =
            Array.Empty<DirectionalShadowCacheLayerProvenance>();

        /// <summary>Bounded GPU caster attribution readback for this completed frame.</summary>
        public DirectionalShadowCasterDiagnostics CasterDiagnostics { get; init; } =
            DirectionalShadowCasterDiagnostics.Empty;

        /// <summary>Same-frame deterministic cascade fitter state.</summary>
        public IReadOnlyList<DirectionalShadowCascadeFitDiagnostics> CascadeFitDiagnostics { get; init; } =
            Array.Empty<DirectionalShadowCascadeFitDiagnostics>();

        public DirectionalShadowMode RequestedMode { get; init; } =
            DirectionalShadowMode.Cascaded;
        public DirectionalShadowMode EffectiveMode { get; init; } =
            DirectionalShadowMode.Cascaded;
        public DirectionalShadowFallbackReason FallbackReason { get; init; } =
            DirectionalShadowFallbackReason.None;
        public string FallbackDetail { get; init; } = string.Empty;
        public int RayMaskEnabled { get; init; }
        public int CascadedReceiverFallbackRequired { get; init; }
        public string RayMaskFormat { get; init; } = string.Empty;
        public uint RayMaskWidth { get; init; }
        public uint RayMaskHeight { get; init; }
        public ulong RayMaskBytes { get; init; }
        public uint RayMaskResourceGeneration { get; init; }
        public uint RaySceneResourceGeneration { get; init; }
        public ulong RaySceneContentEpoch { get; init; }
        public DirectionalShadowQualificationLevel QualificationLevel { get; init; }
        public string QualificationId { get; init; } = string.Empty;
        public string QualificationDetail { get; init; } = string.Empty;
        public string QualificationDeviceRuleId { get; init; } = string.Empty;
        public string QualificationTrackId { get; init; } = string.Empty;
        public double QualifiedGpuBudgetMicroseconds { get; init; }
        public ulong QualifiedMemoryBudgetBytes { get; init; }
        public DirectionalShadowReceiverPolicy OpaqueReceiverPolicy { get; init; }
        public DirectionalShadowReceiverPolicy TransparentReceiverPolicy { get; init; }
        public DirectionalShadowReceiverPolicy DecalReceiverPolicy { get; init; }
        public int CsmTemporalEnabled { get; init; }
        public int SoftTemporalEnabled { get; init; }
        public int SoftSpatialEnabled { get; init; }
        public int HistoryValid { get; init; }
        public DirectionalShadowHistoryResetReason HistoryResetReason { get; init; }
        public ulong HistoryBytes { get; init; }
        public long GpuCsmMicroseconds { get; init; }
        public long GpuRayTraceMicroseconds { get; init; }
        public long GpuTemporalMicroseconds { get; init; }
        public long GpuSpatialMicroseconds { get; init; }
        public RaySceneGeometryCategory RaySceneExactCategories { get; init; }
        public RaySceneGeometryCategory RaySceneProxyCategories { get; init; }
        public RaySceneGeometryCategory RaySceneCompleteCategories { get; init; }
        public Njulf.Core.Math.Vector3 RaySceneCoverageMinimum { get; init; }
        public Njulf.Core.Math.Vector3 RaySceneCoverageMaximum { get; init; }
        public DirectionalShadowRayCounters RayCounters { get; init; } =
            DirectionalShadowRayCounters.Empty;
    }
}
