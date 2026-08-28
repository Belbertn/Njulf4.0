using System;
using System.Text.Json.Serialization;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

[Flags]
public enum SimpleDdgiGpuPassMask : uint
{
    None = 0u,
    FoliageProxyGeneration = 1u << 0,
    PageDemand = 1u << 1,
    PageResidency = 1u << 2,
    PageFeedback = 1u << 3,
    Schedule = 1u << 4,
    Trace = 1u << 5,
    DirectionalRadiance = 1u << 6,
    AcceleratedSolve = 1u << 7,
    Transport = 1u << 8,
    Blend = 1u << 9,
    TransportAudit = 1u << 10,
    RelocateClassify = 1u << 11,
    Publish = 1u << 12,
    SchedulerCommit = 1u << 13,
    ScheduleTailAdmit = 1u << 14,
    ScheduleEmit = 1u << 15,
    UrgentRelight = 1u << 16
}

internal static class SimpleDdgiGpuPassContract
{
    public const SimpleDdgiGpuPassMask TopLevelPasses =
        SimpleDdgiGpuPassMask.FoliageProxyGeneration |
        SimpleDdgiGpuPassMask.PageDemand |
        SimpleDdgiGpuPassMask.PageResidency |
        SimpleDdgiGpuPassMask.PageFeedback |
        SimpleDdgiGpuPassMask.Schedule |
        SimpleDdgiGpuPassMask.Trace |
        SimpleDdgiGpuPassMask.DirectionalRadiance |
        SimpleDdgiGpuPassMask.AcceleratedSolve |
        SimpleDdgiGpuPassMask.Transport |
        SimpleDdgiGpuPassMask.Blend |
        SimpleDdgiGpuPassMask.TransportAudit |
        SimpleDdgiGpuPassMask.RelocateClassify |
        SimpleDdgiGpuPassMask.Publish |
        SimpleDdgiGpuPassMask.SchedulerCommit |
        SimpleDdgiGpuPassMask.UrgentRelight;

    public const SimpleDdgiGpuPassMask SchedulerPasses =
        SimpleDdgiGpuPassMask.Schedule |
        SimpleDdgiGpuPassMask.SchedulerCommit |
        SimpleDdgiGpuPassMask.ScheduleTailAdmit |
        SimpleDdgiGpuPassMask.ScheduleEmit;

    public static SimpleDdgiGpuPassMask FromPassName(string passName) =>
        passName switch
        {
            "DdgiFoliageProxyGenerationPass" =>
                SimpleDdgiGpuPassMask.FoliageProxyGeneration,
            "SimpleDdgiPageDemandPass" => SimpleDdgiGpuPassMask.PageDemand,
            "SimpleDdgiPageResidencyPass" => SimpleDdgiGpuPassMask.PageResidency,
            "SimpleDdgiPageFeedbackPass" => SimpleDdgiGpuPassMask.PageFeedback,
            "SimpleDdgiSchedulePass" => SimpleDdgiGpuPassMask.Schedule,
            "SimpleDdgiTracePass" => SimpleDdgiGpuPassMask.Trace,
            "SimpleDdgiDirectionalRadiancePass" =>
                SimpleDdgiGpuPassMask.DirectionalRadiance,
            "SimpleDdgiAcceleratedSolvePass" =>
                SimpleDdgiGpuPassMask.AcceleratedSolve,
            "SimpleDdgiTransportPass" => SimpleDdgiGpuPassMask.Transport,
            "SimpleDdgiBlendPass" => SimpleDdgiGpuPassMask.Blend,
            "SimpleDdgiTransportAuditPass" =>
                SimpleDdgiGpuPassMask.TransportAudit,
            "SimpleDdgiRelocateClassifyPass" =>
                SimpleDdgiGpuPassMask.RelocateClassify,
            "SimpleDdgiPublishPass" => SimpleDdgiGpuPassMask.Publish,
            "SimpleDdgiSchedulerCommitPass" =>
                SimpleDdgiGpuPassMask.SchedulerCommit,
            "SimpleDdgiSchedule.TailAdmit" =>
                SimpleDdgiGpuPassMask.ScheduleTailAdmit,
            "SimpleDdgiSchedule.Emit" => SimpleDdgiGpuPassMask.ScheduleEmit,
            "SimpleDdgiUrgentRelightPass" =>
                SimpleDdgiGpuPassMask.UrgentRelight,
            _ => SimpleDdgiGpuPassMask.None
        };

    public static SimpleDdgiGpuPassMask CaptureAvailable(
        FrameTimingSnapshot timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        SimpleDdgiGpuPassMask result = SimpleDdgiGpuPassMask.None;
        AddIfRecorded(
            timings,
            "DdgiFoliageProxyGenerationPass",
            SimpleDdgiGpuPassMask.FoliageProxyGeneration,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiPageDemandPass",
            SimpleDdgiGpuPassMask.PageDemand,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiPageResidencyPass",
            SimpleDdgiGpuPassMask.PageResidency,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiPageFeedbackPass",
            SimpleDdgiGpuPassMask.PageFeedback,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiSchedulePass",
            SimpleDdgiGpuPassMask.Schedule,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiTracePass",
            SimpleDdgiGpuPassMask.Trace,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiDirectionalRadiancePass",
            SimpleDdgiGpuPassMask.DirectionalRadiance,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiAcceleratedSolvePass",
            SimpleDdgiGpuPassMask.AcceleratedSolve,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiTransportPass",
            SimpleDdgiGpuPassMask.Transport,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiBlendPass",
            SimpleDdgiGpuPassMask.Blend,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiTransportAuditPass",
            SimpleDdgiGpuPassMask.TransportAudit,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiRelocateClassifyPass",
            SimpleDdgiGpuPassMask.RelocateClassify,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiPublishPass",
            SimpleDdgiGpuPassMask.Publish,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiSchedulerCommitPass",
            SimpleDdgiGpuPassMask.SchedulerCommit,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiSchedule.TailAdmit",
            SimpleDdgiGpuPassMask.ScheduleTailAdmit,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiSchedule.Emit",
            SimpleDdgiGpuPassMask.ScheduleEmit,
            ref result);
        AddIfRecorded(
            timings,
            "SimpleDdgiUrgentRelightPass",
            SimpleDdgiGpuPassMask.UrgentRelight,
            ref result);
        return result;
    }

    public static long CalculateTopLevelMicroseconds(
        FrameTimingSnapshot timings,
        SimpleDdgiGpuPassMask activePasses)
    {
        ArgumentNullException.ThrowIfNull(timings);
        long total = 0;
        AddIfActive(
            timings,
            activePasses,
            SimpleDdgiGpuPassMask.FoliageProxyGeneration,
            "DdgiFoliageProxyGenerationPass",
            ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.PageDemand,
            "SimpleDdgiPageDemandPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.PageResidency,
            "SimpleDdgiPageResidencyPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.PageFeedback,
            "SimpleDdgiPageFeedbackPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.Schedule,
            "SimpleDdgiSchedulePass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.Trace,
            "SimpleDdgiTracePass", ref total);
        AddIfActive(
            timings,
            activePasses,
            SimpleDdgiGpuPassMask.DirectionalRadiance,
            "SimpleDdgiDirectionalRadiancePass",
            ref total);
        AddIfActive(
            timings,
            activePasses,
            SimpleDdgiGpuPassMask.AcceleratedSolve,
            "SimpleDdgiAcceleratedSolvePass",
            ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.Transport,
            "SimpleDdgiTransportPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.Blend,
            "SimpleDdgiBlendPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.TransportAudit,
            "SimpleDdgiTransportAuditPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.RelocateClassify,
            "SimpleDdgiRelocateClassifyPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.Publish,
            "SimpleDdgiPublishPass", ref total);
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.SchedulerCommit,
            "SimpleDdgiSchedulerCommitPass", ref total);
        // Urgent relight is one parent timing scope around an additional set of
        // cache-only child commands. Those child Execute overloads do not
        // receive the timestamp recorder, so adding the parent once neither
        // omits its work nor double-counts the ordinary post-forward passes.
        AddIfActive(timings, activePasses, SimpleDdgiGpuPassMask.UrgentRelight,
            "SimpleDdgiUrgentRelightPass", ref total);
        return total;
    }

    private static void AddIfRecorded(
        FrameTimingSnapshot timings,
        string passName,
        SimpleDdgiGpuPassMask pass,
        ref SimpleDdgiGpuPassMask result)
    {
        if (timings.TryGetPass(passName, out _))
            result |= pass;
    }

    private static void AddIfActive(
        FrameTimingSnapshot timings,
        SimpleDdgiGpuPassMask activePasses,
        SimpleDdgiGpuPassMask pass,
        string passName,
        ref long total)
    {
        if ((activePasses & pass) != 0)
            total = checked(total + timings.GetGpuMicrosecondsOrZero(passName));
    }
}

/// <summary>
/// Allocation-free copy of the complete-field certificate state owned by one
/// submitted Simple-DDGI frame. The renderer never retains the convergence
/// telemetry's reference-backed ring collections in its frame-slot ring.
/// </summary>
public readonly record struct SimpleDdgiTailCertificateFrameEvidence
{
    [JsonRequired]
    public SimpleDdgiTransportPhase Phase { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportCertificationReason Reason { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportGenerations Generations { get; init; }
    [JsonRequired]
    public uint SolveEpoch { get; init; }
    [JsonRequired]
    public uint AuditEpoch { get; init; }
    [JsonRequired]
    public uint ExpectedParticipantCount { get; init; }
    [JsonRequired]
    public uint AuditedParticipantCount { get; init; }
    [JsonRequired]
    public uint ExcludedInactiveCount { get; init; }
    [JsonRequired]
    public uint ExcludedNotVisibleCount { get; init; }
    [JsonRequired]
    public uint ExcludedStaleSourceCount { get; init; }
    [JsonRequired]
    public uint ExcludedInvalidCacheCount { get; init; }
    [JsonRequired]
    public uint CacheIdentityFailureCount { get; init; }
    [JsonRequired]
    public uint CacheCardinalityFailureCount { get; init; }
    [JsonRequired]
    public uint CacheSourceGenerationFailureCount { get; init; }
    [JsonRequired]
    public uint CacheSourceEpochFailureCount { get; init; }
    [JsonRequired]
    public uint CachePhysicalGenerationFailureCount { get; init; }
    [JsonRequired]
    public uint ExpectedTexelCount { get; init; }
    [JsonRequired]
    public uint AuditedTexelCount { get; init; }
    [JsonRequired]
    public uint NonFiniteCount { get; init; }
    [JsonRequired]
    public uint CounterOverflowCount { get; init; }
    [JsonRequired]
    public bool AuditComplete { get; init; }
    [JsonRequired]
    public bool CertificateCurrent { get; init; }
    [JsonRequired]
    public ulong AuditSolveFeedbackFrameSerial { get; init; }
    [JsonRequired]
    public ulong AuditTriggerFeedbackFrameSerial { get; init; }
    [JsonRequired]
    public ulong AuditFirstSubmissionFrameSerial { get; init; }
    [JsonRequired]
    public ulong AuditFinalSubmissionFrameSerial { get; init; }
    [JsonRequired]
    public uint AuditPlannedChunkCount { get; init; }
    [JsonRequired]
    public uint AuditSubmittedChunkCount { get; init; }
    [JsonRequired]
    public bool AuditDispatchComplete { get; init; }
    /// <summary>
    /// Bounded, reference-free audit result captured from the transport owner
    /// at the end of command recording. Keeping the numerical proof here makes
    /// a later report independently auditable instead of trusting a boolean
    /// certificate pulse.
    /// </summary>
    [JsonRequired]
    public SimpleDdgiTransportTailSummary Summary { get; init; }
    [JsonRequired]
    public ulong SummaryDigest { get; init; }

    [JsonIgnore]
    public bool HasCompleteIdentity =>
        Generations.IsInitialized &&
        SolveEpoch == Generations.Solve &&
        AuditEpoch == Generations.Audit &&
        Summary.Generations == Generations &&
        Summary.AuditEpoch == AuditEpoch;

    [JsonIgnore]
    public bool HasDurableSummary =>
        SummaryDigest != 0UL &&
        SummaryDigest == SimpleDdgiTailSummaryDigest.Compute(Summary);

    [JsonIgnore]
    public bool HasCompleteAuditFeedbackLifecycle =>
        AuditSolveFeedbackFrameSerial ==
            Summary.AuditSolveFeedbackFrameSerial &&
        AuditTriggerFeedbackFrameSerial ==
            Summary.AuditTriggerFeedbackFrameSerial &&
        SimpleDdgiAuditFeedbackLifecycleContract.IsValid(
            AuditSolveFeedbackFrameSerial,
            AuditTriggerFeedbackFrameSerial,
            AuditFirstSubmissionFrameSerial);

    public bool IsAcceptedFor(
        in SimpleDdgiSubmittedFrameEvidence submitted) =>
        submitted.Valid &&
        submitted.FrameSerialsValid &&
        submitted.TailCertificationEnabled &&
        Phase == SimpleDdgiTransportPhase.Certified &&
        Reason == SimpleDdgiTransportCertificationReason.Certified &&
        HasCompleteIdentity &&
        HasDurableSummary &&
        HasCompleteAuditFeedbackLifecycle &&
        CertificateCurrent &&
        AuditComplete &&
        ExpectedParticipantCount > 0u &&
        SimpleDdgiAuditCardinalityContract.TryResolve(
            submitted.ActiveProbeCount,
            submitted.AuditPhysicalProbeCount,
            ExpectedParticipantCount,
            out uint expectedChunkCount,
            out uint recomputedExpectedTexelCount,
            out ulong expectedDispatchFrameSpan) &&
        SimpleDdgiAuditCardinalityContract.HasExactCertifiedPopulation(
            submitted.AuditPhysicalProbeCount,
            ExpectedParticipantCount,
            ExcludedInactiveCount,
            ExcludedNotVisibleCount) &&
        AuditPlannedChunkCount == expectedChunkCount &&
        ExpectedTexelCount == recomputedExpectedTexelCount &&
        AuditedParticipantCount == ExpectedParticipantCount &&
        ExpectedTexelCount > 0u &&
        AuditedTexelCount == ExpectedTexelCount &&
        ExcludedStaleSourceCount == 0u &&
        ExcludedInvalidCacheCount == 0u &&
        CacheIdentityFailureCount == 0u &&
        CacheCardinalityFailureCount == 0u &&
        CacheSourceGenerationFailureCount == 0u &&
        CacheSourceEpochFailureCount == 0u &&
        CachePhysicalGenerationFailureCount == 0u &&
        NonFiniteCount == 0u &&
        CounterOverflowCount == 0u &&
        Summary.IsCertified &&
        Summary.IsComplete == AuditComplete &&
        Summary.ExpectedParticipantCount == ExpectedParticipantCount &&
        Summary.AuditedParticipantCount == AuditedParticipantCount &&
        Summary.ExcludedInactiveCount == ExcludedInactiveCount &&
        Summary.ExcludedNotVisibleCount == ExcludedNotVisibleCount &&
        Summary.ExpectedTexelCount == ExpectedTexelCount &&
        Summary.AuditedTexelCount == AuditedTexelCount &&
        Summary.ExcludedStaleSourceCount == ExcludedStaleSourceCount &&
        Summary.ExcludedInvalidCacheCount == ExcludedInvalidCacheCount &&
        Summary.CacheIdentityFailureCount == CacheIdentityFailureCount &&
        Summary.CacheCardinalityFailureCount == CacheCardinalityFailureCount &&
        Summary.CacheSourceGenerationFailureCount ==
            CacheSourceGenerationFailureCount &&
        Summary.CacheSourceEpochFailureCount == CacheSourceEpochFailureCount &&
        Summary.CachePhysicalGenerationFailureCount ==
            CachePhysicalGenerationFailureCount &&
        Summary.NonFiniteCount == NonFiniteCount &&
        Summary.CounterOverflowCount == CounterOverflowCount &&
        Summary.HasPerChannelEvidence &&
        Summary.ChannelEvidenceVersion ==
            SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion &&
        Summary.ChunkCount > 0u &&
        AuditDispatchComplete &&
        AuditPlannedChunkCount == Summary.ChunkCount &&
        AuditSubmittedChunkCount == Summary.ChunkCount &&
        AuditFirstSubmissionFrameSerial == Summary.FirstFrameSerial &&
        AuditFinalSubmissionFrameSerial == Summary.FinalFrameSerial &&
        Summary.FirstFrameSerial != 0UL &&
        Summary.FinalFrameSerial != 0UL &&
        Summary.FirstFrameSerial != ulong.MaxValue &&
        Summary.FinalFrameSerial != ulong.MaxValue &&
        Summary.FinalFrameSerial >= Summary.FirstFrameSerial &&
        Summary.FinalFrameSerial - Summary.FirstFrameSerial + 1UL ==
            expectedDispatchFrameSpan &&
        Summary.FinalFrameSerial < submitted.SchedulerFrameSerial &&
        submitted.AdmittedSourceCohortGeneration ==
            submitted.SourceLightingGeneration &&
        submitted.LivePropagationSourceGeneration ==
            submitted.SourceLightingGeneration &&
        submitted.PublishedPropagationGeneration ==
            submitted.TransportGeneration &&
        submitted.QueueTransactionGeneration ==
            submitted.SchedulerResourceGeneration &&
        Generations.SourceLighting == submitted.SourceLightingGeneration &&
        Generations.CanonicalField == submitted.TransportGeneration &&
        Generations.VolumeTable == submitted.TransportTopologyGeneration &&
        Generations.PhysicalOwnership == submitted.TransportTopologyGeneration &&
        Generations.Queue == submitted.QueueTransactionGeneration &&
        Generations.SchedulerResources == submitted.SchedulerResourceGeneration &&
        Generations.Queue == Generations.SchedulerResources;
}

/// <summary>
/// Simple-DDGI state copied at the end of command recording and admitted to
/// the fixed frame-slot ring only after the terminal graphics submit succeeds.
/// </summary>
public readonly record struct SimpleDdgiSubmittedFrameEvidence
{
    [JsonRequired]
    public bool Valid { get; init; }
    [JsonRequired]
    public int FrameSlot { get; init; }
    [JsonRequired]
    public ulong FrameSerial { get; init; }
    /// <summary>
    /// Serial authored by SimpleDdgiVolumeManager after its per-frame begin.
    /// Scheduler feedback uses this domain; <see cref="FrameSerial"/> remains
    /// the renderer/route identity used for frame-slot and measurement joins.
    /// </summary>
    [JsonRequired]
    public ulong SchedulerFrameSerial { get; init; }
    [JsonIgnore]
    public bool FrameSerialsValid =>
        SimpleDdgiFrameSerialContract.AreValid(
            FrameSerial,
            SchedulerFrameSerial);
    [JsonRequired]
    public bool GpuTimingRecorded { get; init; }
    [JsonRequired]
    public SimpleDdgiSchedulerMode SchedulerMode { get; init; }
    [JsonRequired]
    public int ActiveProbeCount { get; init; }
    /// <summary>
    /// Exact physical field extent traversed by the frozen audit. This is
    /// intentionally distinct from <see cref="ActiveProbeCount"/>, which can
    /// be the smaller probe-state-readback scheduler workload.
    /// </summary>
    [JsonRequired]
    public int AuditPhysicalProbeCount { get; init; }
    [JsonRequired]
    public uint VolumeResourceGeneration { get; init; }
    [JsonRequired]
    public uint TransportTopologyGeneration { get; init; }
    [JsonRequired]
    public uint SourceLightingGeneration { get; init; }
    [JsonRequired]
    public uint AdmittedSourceCohortGeneration { get; init; }
    [JsonRequired]
    public uint TransportGeneration { get; init; }
    [JsonRequired]
    public uint PublishedPropagationGeneration { get; init; }
    [JsonRequired]
    public uint LivePropagationSourceGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerResourceGeneration { get; init; }
    [JsonRequired]
    public uint QueueTransactionGeneration { get; init; }
    [JsonRequired]
    public int CachedSweepCount { get; init; }
    [JsonRequired]
    public bool TailCertificationEnabled { get; init; }
    [JsonRequired]
    public SimpleDdgiTailCertificateFrameEvidence TailCertificate { get; init; }
    /// <summary>
    /// Exact DDGI timing scopes whose commands were recorded for this
    /// submission. The recorder retains this mask even when timestamps are
    /// disabled or its fixed query capacity is exhausted.
    /// </summary>
    [JsonRequired]
    public SimpleDdgiGpuPassMask IntendedGpuPasses { get; init; }
    /// <summary>
    /// Intended scopes that actually acquired a timestamp-query pair. This is
    /// distinct from both command intent and fence-complete query results.
    /// </summary>
    [JsonRequired]
    public SimpleDdgiGpuPassMask AdmittedGpuTimingPasses { get; init; }

    // Existing workload attribution retained in the exact submitted value.
    [JsonRequired]
    public ulong SourceCacheLayoutIdentity { get; init; }
    [JsonRequired]
    public ulong ScheduledPrimaryRayCount { get; init; }
    [JsonRequired]
    public ulong VisibilityRayCount { get; init; }
}

/// <summary>
/// Fence-complete GPU timings and scheduler feedback paired with the exact
/// submitted frame-slot value that produced them.
/// </summary>
public readonly record struct SimpleDdgiCompletedFrameEvidence
{
    [JsonRequired]
    public bool Valid { get; init; }
    [JsonRequired]
    public SimpleDdgiSubmittedFrameEvidence Submitted { get; init; }
    /// <summary>
    /// Compatibility alias for an exact causal DDGI total in the completed
    /// slot. Per-pass presence lives in <see cref="CompletedGpuTimingPasses"/>;
    /// a recorded sub-microsecond pass can legitimately have a zero duration.
    /// </summary>
    [JsonRequired]
    public bool GpuTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuTimingPassSetAligned { get; init; }
    [JsonRequired]
    public SimpleDdgiGpuPassMask CompletedGpuTimingPasses { get; init; }
    [JsonRequired]
    public bool GpuScheduleTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuAcceleratedSolveTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuSchedulerTailAdmitTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuSchedulerEmitTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuSchedulerCommitTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuTransportAuditTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuUrgentRelightTimingAvailable { get; init; }
    [JsonRequired]
    public bool GpuDdgiTotalTimingAvailable { get; init; }
    [JsonRequired]
    public long GpuAcceleratedSolveMicroseconds { get; init; }
    [JsonRequired]
    public long GpuSchedulerTailAdmitMicroseconds { get; init; }
    [JsonRequired]
    public long GpuSchedulerEmitMicroseconds { get; init; }
    [JsonRequired]
    public long GpuSchedulerCommitMicroseconds { get; init; }
    [JsonRequired]
    public long GpuTransportAuditMicroseconds { get; init; }
    [JsonRequired]
    public long GpuUrgentRelightMicroseconds { get; init; }
    [JsonRequired]
    public long GpuDdgiTotalMicroseconds { get; init; }

    [JsonRequired]
    public bool SchedulerFeedbackAvailable { get; init; }
    [JsonRequired]
    public bool SchedulerFeedbackFrameAligned { get; init; }
    [JsonRequired]
    public bool SchedulerFeedbackGenerationAligned { get; init; }
    [JsonRequired]
    public ulong SchedulerFeedbackFrameSerial { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackVolumeResourceGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackSchedulerResourceGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackQueueTransactionGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackTransportTopologyGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackSourceLightingGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackTransportGeneration { get; init; }
    [JsonRequired]
    public uint SchedulerFeedbackStatusFlags { get; init; }
    [JsonRequired]
    public uint SchedulerConsideredCandidateCount { get; init; }
    /// <summary>Exact COUNTER_COMPACTED total exposed as EligibleCount.</summary>
    [JsonRequired]
    public uint SchedulerCompactedCandidateCount { get; init; }
    [JsonRequired]
    public uint SchedulerAcceptedWorkCount { get; init; }
    [JsonRequired]
    public uint SchedulerCommittedWorkCount { get; init; }
    [JsonRequired]
    public uint SchedulerPublishedWorkCount { get; init; }
    [JsonRequired]
    public uint SchedulerActiveWorkCount { get; init; }
    [JsonRequired]
    public uint SchedulerSourceParticipantCount { get; init; }
    [JsonRequired]
    public uint SchedulerHardSourceParticipantCount { get; init; }
    [JsonRequired]
    public uint SchedulerRoutineSourceParticipantCount { get; init; }
    [JsonRequired]
    public uint SchedulerCachedParticipantCount { get; init; }
    [JsonRequired]
    public uint SchedulerSolveParticipantCount { get; init; }
    [JsonRequired]
    public uint SchedulerSolveVisitedCount { get; init; }
    [JsonRequired]
    public uint SchedulerSolveEpoch { get; init; }
    [JsonRequired]
    public uint SchedulerActiveCanonicalMutationCount { get; init; }
    [JsonRequired]
    public uint SchedulerActiveSourceMutationCount { get; init; }
    [JsonRequired]
    public uint SchedulerBlockingTailSourceWorkCount { get; init; }
    [JsonRequired]
    public uint SchedulerPrimaryRayCount { get; init; }
    [JsonRequired]
    public uint SchedulerSourceRayCount { get; init; }
    [JsonRequired]
    public uint SchedulerTransportRayCount { get; init; }
    [JsonRequired]
    public uint SchedulerCachedRayCount { get; init; }
}

/// <summary>Fixed, allocation-free submitted-work ring keyed by renderer slot.</summary>
internal sealed class SimpleDdgiSubmittedFrameRing
{
    private readonly SimpleDdgiSubmittedFrameEvidence[] _frames =
        new SimpleDdgiSubmittedFrameEvidence[RenderingConstants.FramesInFlight];
    private readonly bool[] _pending = new bool[RenderingConstants.FramesInFlight];

    public int FrameSlotCount => _frames.Length;

    public void MarkSubmitted(
        int frameSlot,
        in SimpleDdgiSubmittedFrameEvidence frame)
    {
        ValidateFrameSlot(frameSlot);
        if (!frame.Valid)
            throw new ArgumentException("Submitted DDGI evidence must be valid.", nameof(frame));
        if (frame.FrameSlot != frameSlot)
        {
            throw new ArgumentException(
                $"DDGI frame identity names slot {frame.FrameSlot}, but was submitted to slot {frameSlot}.",
                nameof(frame));
        }
        if (_pending[frameSlot])
        {
            throw new InvalidOperationException(
                $"DDGI frame slot {frameSlot} was reused before its submitted evidence was consumed.");
        }

        _frames[frameSlot] = frame;
        _pending[frameSlot] = true;
    }

    public bool TryPeek(
        int frameSlot,
        out SimpleDdgiSubmittedFrameEvidence frame)
    {
        ValidateFrameSlot(frameSlot);
        frame = _frames[frameSlot];
        return _pending[frameSlot];
    }

    public bool TryConsume(
        int frameSlot,
        out SimpleDdgiSubmittedFrameEvidence frame)
    {
        if (!TryPeek(frameSlot, out frame))
            return false;

        _frames[frameSlot] = default;
        _pending[frameSlot] = false;
        return true;
    }

    private void ValidateFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
    }
}

internal static class SimpleDdgiFrameEvidenceFactory
{
    public static SimpleDdgiSubmittedFrameEvidence CaptureSubmitted(
        int frameSlot,
        SceneRenderingData sceneData,
        bool gpuTimingRecorded,
        ulong schedulerFrameSerial,
        int auditPhysicalProbeCount,
        SimpleDdgiGpuPassMask intendedGpuPasses,
        SimpleDdgiGpuPassMask admittedGpuTimingPasses,
        uint queueTransactionGeneration,
        in SimpleDdgiTailCertificateFrameEvidence tailCertificate,
        ulong sourceCacheLayoutIdentity)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        if (sceneData.SimpleDdgiActive == 0)
            return default;

        return new SimpleDdgiSubmittedFrameEvidence
        {
            Valid = true,
            FrameSlot = frameSlot,
            FrameSerial = sceneData.DdgiFrameSerial,
            SchedulerFrameSerial = schedulerFrameSerial,
            GpuTimingRecorded = gpuTimingRecorded,
            SchedulerMode = sceneData.SimpleDdgiSchedulerMode,
            ActiveProbeCount = Math.Max(0, sceneData.DdgiActiveProbeCount),
            AuditPhysicalProbeCount = Math.Max(0, auditPhysicalProbeCount),
            VolumeResourceGeneration = sceneData.SimpleDdgiVolumeResourceGeneration,
            TransportTopologyGeneration = sceneData.SimpleDdgiTransportTopologyGeneration,
            SourceLightingGeneration = sceneData.SimpleDdgiSourceLightingGeneration,
            AdmittedSourceCohortGeneration = sceneData.SimpleDdgiAdmittedSourceCohortGeneration,
            TransportGeneration = sceneData.SimpleDdgiTransportGeneration,
            PublishedPropagationGeneration = sceneData.SimpleDdgiPublishedPropagationGeneration,
            LivePropagationSourceGeneration = sceneData.SimpleDdgiLivePropagationSourceGeneration,
            SchedulerResourceGeneration = sceneData.SimpleDdgiSchedulerResourceGeneration,
            QueueTransactionGeneration = queueTransactionGeneration,
            CachedSweepCount = Math.Max(0, sceneData.SimpleDdgiTransportCachedSweepCount),
            TailCertificationEnabled = sceneData.SimpleDdgiTransportTailCertificationEnabled,
            TailCertificate = tailCertificate,
            IntendedGpuPasses = intendedGpuPasses,
            AdmittedGpuTimingPasses = admittedGpuTimingPasses,
            SourceCacheLayoutIdentity = sourceCacheLayoutIdentity,
            ScheduledPrimaryRayCount = sceneData.DdgiScheduledPrimaryRayCount,
            VisibilityRayCount = sceneData.DdgiVisibilityRayCount
        };
    }

    public static SimpleDdgiCompletedFrameEvidence Complete(
        in SimpleDdgiSubmittedFrameEvidence submitted,
        FrameTimingSnapshot timings,
        bool schedulerFeedbackAvailable,
        in GPUSimpleDdgiSchedulerFeedback feedback,
        uint schedulerFeedbackTransportTopologyGeneration,
        uint schedulerActiveCanonicalMutationCount = 0u,
        uint schedulerActiveSourceMutationCount = 0u)
    {
        ArgumentNullException.ThrowIfNull(timings);
        if (!submitted.Valid)
            return default;

        ulong feedbackSerial =
            ((ulong)feedback.FrameSerialHigh << 32) | feedback.FrameSerialLow;
        bool frameAligned = schedulerFeedbackAvailable &&
            submitted.FrameSerialsValid &&
            feedbackSerial == submitted.SchedulerFrameSerial;
        bool generationAligned = frameAligned &&
            feedback.VolumeTableGeneration == submitted.VolumeResourceGeneration &&
            schedulerFeedbackTransportTopologyGeneration ==
                submitted.TransportTopologyGeneration &&
            feedback.SchedulerResourceGeneration == submitted.SchedulerResourceGeneration &&
            feedback.QueueTransactionGeneration ==
                submitted.QueueTransactionGeneration &&
            feedback.SourceLightingGeneration == submitted.SourceLightingGeneration;
        SimpleDdgiGpuPassMask completedGpuTimingPasses =
            SimpleDdgiGpuPassContract.CaptureAvailable(timings);
        bool timingPassSetAligned = submitted.GpuTimingRecorded &&
            submitted.AdmittedGpuTimingPasses == submitted.IntendedGpuPasses &&
            completedGpuTimingPasses == submitted.AdmittedGpuTimingPasses;
        bool gpuDdgiTotalTimingAvailable = timingPassSetAligned &&
            (submitted.IntendedGpuPasses &
                (SimpleDdgiGpuPassMask.Schedule |
                 SimpleDdgiGpuPassMask.TransportAudit)) != 0;
        bool scheduleTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.Schedule) != 0;
        bool acceleratedSolveTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.AcceleratedSolve) != 0;
        bool tailAdmitTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.ScheduleTailAdmit) != 0;
        bool emitTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.ScheduleEmit) != 0;
        bool commitTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.SchedulerCommit) != 0;
        bool transportAuditTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.TransportAudit) != 0;
        bool urgentRelightTimingAvailable =
            (completedGpuTimingPasses & SimpleDdgiGpuPassMask.UrgentRelight) != 0;
        uint activeWork = SaturatingAdd(
            feedback.SourceProbeUsed,
            feedback.CachedSolverProbeUsed);
        uint cachedRays = feedback.TransportRayUsed > feedback.SourceAchievedRays
            ? feedback.TransportRayUsed - feedback.SourceAchievedRays
            : 0u;

        return new SimpleDdgiCompletedFrameEvidence
        {
            Valid = true,
            Submitted = submitted,
            GpuTimingAvailable = gpuDdgiTotalTimingAvailable,
            GpuTimingPassSetAligned = timingPassSetAligned,
            CompletedGpuTimingPasses = completedGpuTimingPasses,
            GpuScheduleTimingAvailable = scheduleTimingAvailable,
            GpuAcceleratedSolveTimingAvailable = acceleratedSolveTimingAvailable,
            GpuSchedulerTailAdmitTimingAvailable = tailAdmitTimingAvailable,
            GpuSchedulerEmitTimingAvailable = emitTimingAvailable,
            GpuSchedulerCommitTimingAvailable = commitTimingAvailable,
            GpuTransportAuditTimingAvailable = transportAuditTimingAvailable,
            GpuUrgentRelightTimingAvailable = urgentRelightTimingAvailable,
            GpuDdgiTotalTimingAvailable = gpuDdgiTotalTimingAvailable,
            GpuAcceleratedSolveMicroseconds =
                acceleratedSolveTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiAcceleratedSolvePass")
                    : 0,
            GpuSchedulerTailAdmitMicroseconds =
                tailAdmitTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.TailAdmit")
                    : 0,
            GpuSchedulerEmitMicroseconds =
                emitTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Emit")
                    : 0,
            GpuSchedulerCommitMicroseconds =
                commitTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedulerCommitPass")
                    : 0,
            GpuTransportAuditMicroseconds =
                transportAuditTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportAuditPass")
                    : 0,
            GpuUrgentRelightMicroseconds =
                urgentRelightTimingAvailable
                    ? timings.GetGpuMicrosecondsOrZero("SimpleDdgiUrgentRelightPass")
                    : 0,
            GpuDdgiTotalMicroseconds = gpuDdgiTotalTimingAvailable
                ? SimpleDdgiGpuPassContract.CalculateTopLevelMicroseconds(
                    timings,
                    submitted.IntendedGpuPasses)
                : 0,
            SchedulerFeedbackAvailable = schedulerFeedbackAvailable,
            SchedulerFeedbackFrameAligned = frameAligned,
            SchedulerFeedbackGenerationAligned = generationAligned,
            SchedulerFeedbackFrameSerial = feedbackSerial,
            SchedulerFeedbackVolumeResourceGeneration = feedback.VolumeTableGeneration,
            SchedulerFeedbackSchedulerResourceGeneration = feedback.SchedulerResourceGeneration,
            SchedulerFeedbackQueueTransactionGeneration = feedback.QueueTransactionGeneration,
            SchedulerFeedbackTransportTopologyGeneration =
                schedulerFeedbackTransportTopologyGeneration,
            SchedulerFeedbackSourceLightingGeneration = feedback.SourceLightingGeneration,
            SchedulerFeedbackTransportGeneration = feedback.TransportGeneration,
            SchedulerFeedbackStatusFlags = feedback.StatusFlags,
            SchedulerConsideredCandidateCount = feedback.ConsideredCount,
            SchedulerCompactedCandidateCount = feedback.EligibleCount,
            SchedulerAcceptedWorkCount = feedback.AcceptedCount,
            SchedulerCommittedWorkCount = feedback.CommittedCount,
            SchedulerPublishedWorkCount = feedback.PublishedCount,
            SchedulerActiveWorkCount = activeWork,
            SchedulerSourceParticipantCount = feedback.SourceProbeUsed,
            SchedulerHardSourceParticipantCount = feedback.HardSourceProbeUsed,
            SchedulerRoutineSourceParticipantCount = feedback.RoutineSourceProbeUsed,
            SchedulerCachedParticipantCount = feedback.CachedSolverProbeUsed,
            SchedulerSolveParticipantCount = feedback.SolveEpochParticipantCount,
            SchedulerSolveVisitedCount = feedback.SolveEpochVisitedCount,
            SchedulerSolveEpoch = feedback.SolveEpoch,
            SchedulerActiveCanonicalMutationCount =
                schedulerActiveCanonicalMutationCount,
            SchedulerActiveSourceMutationCount =
                schedulerActiveSourceMutationCount,
            SchedulerBlockingTailSourceWorkCount =
                SimpleDdgiVolumeManager.ResolveBlockingTailSourceWorkCount(
                    feedback),
            SchedulerPrimaryRayCount = feedback.PrimaryRayUsed,
            SchedulerSourceRayCount = feedback.SourceAchievedRays,
            SchedulerTransportRayCount = feedback.TransportRayUsed,
            SchedulerCachedRayCount = cachedRays
        };
    }

    private static uint SaturatingAdd(uint left, uint right) =>
        uint.MaxValue - left < right ? uint.MaxValue : left + right;
}

internal static class SimpleDdgiFrameSerialContract
{
    /// <summary>
    /// Renderer and scheduler serials are independent domains. Renderer zero is
    /// a valid first route identity; manager zero and MaxValue are lifecycle
    /// sentinels. Sequence validation belongs to the measured route rather than
    /// an arithmetic offset, because disabled or aborted prehistory can advance
    /// the two producers differently.
    /// </summary>
    public static bool AreValid(
        ulong rendererFrameSerial,
        ulong schedulerFrameSerial) =>
        rendererFrameSerial != ulong.MaxValue &&
        schedulerFrameSerial != 0UL &&
        schedulerFrameSerial != ulong.MaxValue;

    public static uint LowWord(ulong frameSerial) =>
        unchecked((uint)frameSerial);

    public static uint HighWord(ulong frameSerial) =>
        unchecked((uint)(frameSerial >> 32));

    public static ulong FromWords(uint lowWord, uint highWord) =>
        ((ulong)highWord << 32) | lowWord;
}

public static class SimpleDdgiAuditCardinalityContract
{
    public const uint MaximumChunksPerSubmittedFrame = 2u;

    /// <summary>
    /// Recomputes the production audit geometry without trusting copied tail
    /// fields. The dispatch walks every physical probe slot in fixed 256
    /// probe chunks, while each frozen participant contributes the complete
    /// 8x8 irradiance interior to the numerical certificate.
    /// </summary>
    public static bool TryResolve(
        int activeProbeCount,
        int auditPhysicalProbeCount,
        uint expectedParticipantCount,
        out uint expectedChunkCount,
        out uint expectedTexelCount,
        out ulong expectedDispatchFrameSpan)
    {
        expectedChunkCount = 0u;
        expectedTexelCount = 0u;
        expectedDispatchFrameSpan = 0UL;
        if (activeProbeCount <= 0 ||
            auditPhysicalProbeCount <= 0 ||
            activeProbeCount > auditPhysicalProbeCount ||
            expectedParticipantCount == 0u ||
            expectedParticipantCount > (uint)activeProbeCount ||
            expectedParticipantCount > (uint)auditPhysicalProbeCount)
        {
            return false;
        }

        const uint chunkCapacity =
            SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceProbeCapacity;
        const uint irradianceTexelsPerProbe =
            SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
            SimpleDdgiVolumeManager.IrradianceTexelsPerProbe;
        expectedChunkCount = checked(
            ((uint)auditPhysicalProbeCount + chunkCapacity - 1u) /
            chunkCapacity);
        ulong texelCount =
            (ulong)expectedParticipantCount * irradianceTexelsPerProbe;
        if (texelCount > uint.MaxValue)
        {
            expectedChunkCount = 0u;
            return false;
        }

        expectedTexelCount = (uint)texelCount;
        expectedDispatchFrameSpan =
            (expectedChunkCount + MaximumChunksPerSubmittedFrame - 1u) /
            MaximumChunksPerSubmittedFrame;
        return true;
    }

    /// <summary>
    /// A successful reduce audit visits every physical probe exactly once.
    /// Once stale, invalid-cache, and non-finite outcomes have been rejected,
    /// every probe must be either a certified participant, inactive, or not
    /// visible. Sum in 64 bits so a forged overflowing counter tuple fails
    /// rather than wrapping into the physical extent.
    /// </summary>
    public static bool HasExactCertifiedPopulation(
        int auditPhysicalProbeCount,
        uint expectedParticipantCount,
        uint excludedInactiveCount,
        uint excludedNotVisibleCount) =>
        auditPhysicalProbeCount > 0 &&
        (ulong)expectedParticipantCount + excludedInactiveCount +
            excludedNotVisibleCount == (ulong)auditPhysicalProbeCount;
}

internal static class SimpleDdgiAuditFeedbackLifecycleContract
{
    /// <summary>
    /// The complete solve reduction precedes the epoch-zero drain reduction,
    /// and both must precede the first command submission of the frozen audit.
    /// Zero and MaxValue are lifecycle sentinels in the scheduler domain.
    /// </summary>
    public static bool IsValid(
        ulong solveFeedbackFrameSerial,
        ulong triggerFeedbackFrameSerial,
        ulong firstAuditSubmissionFrameSerial) =>
        solveFeedbackFrameSerial is not 0UL and not ulong.MaxValue &&
        triggerFeedbackFrameSerial is not 0UL and not ulong.MaxValue &&
        firstAuditSubmissionFrameSerial is not 0UL and not ulong.MaxValue &&
        solveFeedbackFrameSerial < triggerFeedbackFrameSerial &&
        triggerFeedbackFrameSerial < firstAuditSubmissionFrameSerial;
}

internal static class SimpleDdgiAuditLifecycleContract
{
    /// <summary>
    /// Stamps only a successfully submitted audit chunk. Audit freeze may
    /// begin while polling feedback in renderer BeginFrame, before the manager
    /// advances to the new scheduler frame; therefore freeze itself must leave
    /// both lifecycle serials unset.
    /// </summary>
    public static SimpleDdgiTransportTailSummary StampSuccessfulChunk(
        ulong schedulerFrameSerial,
        uint chunkIndex,
        ref ulong firstSubmissionFrameSerial,
        ref ulong finalSubmissionFrameSerial,
        in SimpleDdgiTransportTailSummary summary)
    {
        if (schedulerFrameSerial is 0UL or ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schedulerFrameSerial));
        }
        if (chunkIndex == 0u)
        {
            if (firstSubmissionFrameSerial != 0UL ||
                finalSubmissionFrameSerial != 0UL ||
                summary.ChunkCount != 0u)
            {
                throw new InvalidOperationException(
                    "The first audit chunk cannot overwrite an existing lifecycle.");
            }
            firstSubmissionFrameSerial = schedulerFrameSerial;
        }
        else if (firstSubmissionFrameSerial == 0UL ||
                 finalSubmissionFrameSerial == 0UL ||
                 summary.ChunkCount != chunkIndex ||
                 schedulerFrameSerial < finalSubmissionFrameSerial)
        {
            throw new InvalidOperationException(
                "Later audit chunks require an exact, nondecreasing prior lifecycle.");
        }

        finalSubmissionFrameSerial = schedulerFrameSerial;
        return summary with
        {
            FirstFrameSerial = firstSubmissionFrameSerial,
            FinalFrameSerial = finalSubmissionFrameSerial,
            ChunkCount = checked(chunkIndex + 1u),
            IsComplete = false,
            Reason = SimpleDdgiTransportCertificationReason.AuditInProgress
        };
    }
}

/// <summary>
/// Stable FNV-1a digest of every field that can affect tail certification and
/// its audit lifecycle. It is intentionally independent of runtime record
/// hashing and struct padding.
/// </summary>
internal static class SimpleDdgiTailSummaryDigest
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(in SimpleDdgiTransportTailSummary summary)
    {
        ulong hash = OffsetBasis;
        Add(ref hash, summary.AuditEpoch);
        Add(ref hash, summary.Generations);
        Add(ref hash, summary.ExpectedParticipantCount);
        Add(ref hash, summary.AuditedParticipantCount);
        Add(ref hash, summary.ExcludedInactiveCount);
        Add(ref hash, summary.ExcludedNotVisibleCount);
        Add(ref hash, summary.ExcludedStaleSourceCount);
        Add(ref hash, summary.ExcludedInvalidCacheCount);
        Add(ref hash, summary.CacheIdentityFailureCount);
        Add(ref hash, summary.CacheCardinalityFailureCount);
        Add(ref hash, summary.CacheSourceGenerationFailureCount);
        Add(ref hash, summary.CacheSourceEpochFailureCount);
        Add(ref hash, summary.CachePhysicalGenerationFailureCount);
        Add(ref hash, summary.NonFiniteCount);
        Add(ref hash, summary.CounterOverflowCount);
        Add(ref hash, summary.FirstNotResidentIdentity);
        Add(ref hash, summary.FirstStaleSourceIdentity);
        Add(ref hash, summary.FirstInvalidCacheIdentity);
        Add(ref hash, summary.FirstNonFiniteIdentity);
        Add(ref hash, summary.AuditedTexelCount);
        Add(ref hash, summary.ExpectedTexelCount);
        Add(ref hash, summary.FixedPointDefect);
        Add(ref hash, summary.FieldMagnitude);
        Add(ref hash, summary.ConfiguredContractionBound);
        Add(ref hash, summary.ObservedContractionBound);
        Add(ref hash, summary.CertifiedContractionBound);
        Add(ref hash, summary.AbsoluteTailBound);
        Add(ref hash, summary.RelativeTailBound);
        Add(ref hash, summary.Tolerance);
        Add(ref hash, summary.CanonicalQuantizationFloor);
        Add(ref hash, summary.ChannelEvidenceVersion);
        Add(ref hash, summary.FixedPointDefectChannels);
        Add(ref hash, summary.FieldMagnitudeChannels);
        Add(ref hash, summary.ObservedContractionChannels);
        Add(ref hash, summary.CertifiedContractionChannels);
        Add(ref hash, summary.AbsoluteTailBoundChannels);
        Add(ref hash, summary.RelativeTailBoundChannels);
        Add(ref hash, summary.CanonicalQuantizationFloorChannels);
        Add(ref hash, summary.MaximumDefectWitnessProbeIndex);
        Add(ref hash, summary.MaximumDefectWitnessTexelIndex);
        Add(ref hash, summary.DetailedWitnessValid);
        Add(ref hash, summary.DetailedWitnessProbeIndex);
        Add(ref hash, summary.DetailedWitnessTexelIndex);
        Add(ref hash, summary.DetailedWitnessWeightSum);
        Add(ref hash, summary.DetailedWitnessCandidateR);
        Add(ref hash, summary.DetailedWitnessCandidateG);
        Add(ref hash, summary.DetailedWitnessCandidateB);
        Add(ref hash, summary.DetailedWitnessCanonicalR);
        Add(ref hash, summary.DetailedWitnessCanonicalG);
        Add(ref hash, summary.DetailedWitnessCanonicalB);
        Add(ref hash, summary.DetailedWitnessProbeResidual);
        Add(ref hash, summary.DetailedWitnessSourceRayCount);
        Add(ref hash, summary.DetailedWitnessPrivateR);
        Add(ref hash, summary.DetailedWitnessPrivateG);
        Add(ref hash, summary.DetailedWitnessPrivateB);
        Add(ref hash, summary.AuditMicroseconds);
        Add(ref hash, summary.AuditSolveFeedbackFrameSerial);
        Add(ref hash, summary.AuditTriggerFeedbackFrameSerial);
        Add(ref hash, summary.FirstFrameSerial);
        Add(ref hash, summary.FinalFrameSerial);
        Add(ref hash, summary.ChunkCount);
        Add(ref hash, summary.IsComplete);
        Add(ref hash, (uint)summary.Reason);
        return hash;
    }

    private static void Add(
        ref ulong hash,
        in SimpleDdgiTransportGenerations generations)
    {
        Add(ref hash, generations.VolumeTable);
        Add(ref hash, generations.PhysicalOwnership);
        Add(ref hash, generations.SourceLighting);
        Add(ref hash, generations.SourceEpoch);
        Add(ref hash, generations.TransportOperator);
        Add(ref hash, generations.CanonicalField);
        Add(ref hash, generations.Solve);
        Add(ref hash, generations.Audit);
        Add(ref hash, generations.Queue);
        Add(ref hash, generations.SchedulerResources);
        Add(ref hash, generations.DynamicGeometryEpoch);
    }

    private static void Add(
        ref ulong hash,
        in SimpleDdgiTransportMismatchIdentity identity)
    {
        Add(ref hash, identity.VirtualProbeIndex);
        Add(ref hash, identity.PhysicalProbeIndex);
    }

    private static void Add(
        ref ulong hash,
        in SimpleDdgiTransportRgbBounds bounds)
    {
        Add(ref hash, bounds.Red);
        Add(ref hash, bounds.Green);
        Add(ref hash, bounds.Blue);
    }

    private static void Add(ref ulong hash, bool value) =>
        Add(ref hash, value ? 1u : 0u);

    private static void Add(ref ulong hash, float value) =>
        Add(ref hash, BitConverter.SingleToUInt32Bits(value));

    private static void Add(ref ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
    }

    private static void Add(ref ulong hash, ulong value)
    {
        Add(ref hash, unchecked((uint)value));
        Add(ref hash, unchecked((uint)(value >> 32)));
    }
}
