using System;
using Njulf.Rendering.Debug;

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
    ScheduleEmit = 1u << 15
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
        SimpleDdgiGpuPassMask.SchedulerCommit;

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
    public SimpleDdgiTransportPhase Phase { get; init; }
    public SimpleDdgiTransportCertificationReason Reason { get; init; }
    public SimpleDdgiTransportGenerations Generations { get; init; }
    public uint SolveEpoch { get; init; }
    public uint AuditEpoch { get; init; }
    public uint ExpectedParticipantCount { get; init; }
    public uint AuditedParticipantCount { get; init; }
    public uint ExcludedInactiveCount { get; init; }
    public uint ExcludedNotVisibleCount { get; init; }
    public uint ExcludedStaleSourceCount { get; init; }
    public uint ExcludedInvalidCacheCount { get; init; }
    public uint CacheIdentityFailureCount { get; init; }
    public uint CacheCardinalityFailureCount { get; init; }
    public uint CacheSourceGenerationFailureCount { get; init; }
    public uint CacheSourceEpochFailureCount { get; init; }
    public uint CachePhysicalGenerationFailureCount { get; init; }
    public uint ExpectedTexelCount { get; init; }
    public uint AuditedTexelCount { get; init; }
    public uint NonFiniteCount { get; init; }
    public uint CounterOverflowCount { get; init; }
    public bool AuditComplete { get; init; }
    public bool CertificateCurrent { get; init; }
    public ulong AuditFirstSubmissionFrameSerial { get; init; }
    public ulong AuditFinalSubmissionFrameSerial { get; init; }
    public uint AuditPlannedChunkCount { get; init; }
    public uint AuditSubmittedChunkCount { get; init; }
    public bool AuditDispatchComplete { get; init; }
    /// <summary>
    /// Bounded, reference-free audit result captured from the transport owner
    /// at the end of command recording. Keeping the numerical proof here makes
    /// a later report independently auditable instead of trusting a boolean
    /// certificate pulse.
    /// </summary>
    public SimpleDdgiTransportTailSummary Summary { get; init; }
    public ulong SummaryDigest { get; init; }

    public bool HasCompleteIdentity =>
        Generations.IsInitialized &&
        SolveEpoch == Generations.Solve &&
        AuditEpoch == Generations.Audit &&
        Summary.Generations == Generations &&
        Summary.AuditEpoch == AuditEpoch;

    public bool HasDurableSummary =>
        SummaryDigest != 0UL &&
        SummaryDigest == SimpleDdgiTailSummaryDigest.Compute(Summary);

    public bool IsAcceptedFor(
        in SimpleDdgiSubmittedFrameEvidence submitted) =>
        submitted.Valid &&
        submitted.TailCertificationEnabled &&
        Phase == SimpleDdgiTransportPhase.Certified &&
        Reason == SimpleDdgiTransportCertificationReason.Certified &&
        HasCompleteIdentity &&
        HasDurableSummary &&
        CertificateCurrent &&
        AuditComplete &&
        ExpectedParticipantCount > 0u &&
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
        Summary.ChunkCount > 0u &&
        AuditDispatchComplete &&
        AuditPlannedChunkCount == Summary.ChunkCount &&
        AuditSubmittedChunkCount == Summary.ChunkCount &&
        AuditFirstSubmissionFrameSerial == Summary.FirstFrameSerial &&
        AuditFinalSubmissionFrameSerial == Summary.FinalFrameSerial &&
        Summary.FirstFrameSerial != ulong.MaxValue &&
        Summary.FinalFrameSerial != ulong.MaxValue &&
        Summary.FinalFrameSerial >= Summary.FirstFrameSerial &&
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
    public bool Valid { get; init; }
    public int FrameSlot { get; init; }
    public ulong FrameSerial { get; init; }
    public bool GpuTimingRecorded { get; init; }
    public SimpleDdgiSchedulerMode SchedulerMode { get; init; }
    public int ActiveProbeCount { get; init; }
    public uint VolumeResourceGeneration { get; init; }
    public uint TransportTopologyGeneration { get; init; }
    public uint SourceLightingGeneration { get; init; }
    public uint AdmittedSourceCohortGeneration { get; init; }
    public uint TransportGeneration { get; init; }
    public uint PublishedPropagationGeneration { get; init; }
    public uint LivePropagationSourceGeneration { get; init; }
    public uint SchedulerResourceGeneration { get; init; }
    public uint QueueTransactionGeneration { get; init; }
    public int CachedSweepCount { get; init; }
    public bool TailCertificationEnabled { get; init; }
    public SimpleDdgiTailCertificateFrameEvidence TailCertificate { get; init; }
    /// <summary>
    /// Exact DDGI timing scopes whose commands were recorded for this
    /// submission. The recorder retains this mask even when timestamps are
    /// disabled or its fixed query capacity is exhausted.
    /// </summary>
    public SimpleDdgiGpuPassMask IntendedGpuPasses { get; init; }
    /// <summary>
    /// Intended scopes that actually acquired a timestamp-query pair. This is
    /// distinct from both command intent and fence-complete query results.
    /// </summary>
    public SimpleDdgiGpuPassMask AdmittedGpuTimingPasses { get; init; }

    // Existing workload attribution retained in the exact submitted value.
    public ulong SourceCacheLayoutIdentity { get; init; }
    public ulong ScheduledPrimaryRayCount { get; init; }
    public ulong VisibilityRayCount { get; init; }
}

/// <summary>
/// Fence-complete GPU timings and scheduler feedback paired with the exact
/// submitted frame-slot value that produced them.
/// </summary>
public readonly record struct SimpleDdgiCompletedFrameEvidence
{
    public bool Valid { get; init; }
    public SimpleDdgiSubmittedFrameEvidence Submitted { get; init; }
    /// <summary>
    /// Compatibility alias for an exact causal DDGI total in the completed
    /// slot. Per-pass presence lives in <see cref="CompletedGpuTimingPasses"/>;
    /// a recorded sub-microsecond pass can legitimately have a zero duration.
    /// </summary>
    public bool GpuTimingAvailable { get; init; }
    public bool GpuTimingPassSetAligned { get; init; }
    public SimpleDdgiGpuPassMask CompletedGpuTimingPasses { get; init; }
    public bool GpuScheduleTimingAvailable { get; init; }
    public bool GpuAcceleratedSolveTimingAvailable { get; init; }
    public bool GpuSchedulerTailAdmitTimingAvailable { get; init; }
    public bool GpuSchedulerEmitTimingAvailable { get; init; }
    public bool GpuSchedulerCommitTimingAvailable { get; init; }
    public bool GpuTransportAuditTimingAvailable { get; init; }
    public bool GpuDdgiTotalTimingAvailable { get; init; }
    public long GpuAcceleratedSolveMicroseconds { get; init; }
    public long GpuSchedulerTailAdmitMicroseconds { get; init; }
    public long GpuSchedulerEmitMicroseconds { get; init; }
    public long GpuSchedulerCommitMicroseconds { get; init; }
    public long GpuTransportAuditMicroseconds { get; init; }
    public long GpuDdgiTotalMicroseconds { get; init; }

    public bool SchedulerFeedbackAvailable { get; init; }
    public bool SchedulerFeedbackFrameAligned { get; init; }
    public bool SchedulerFeedbackGenerationAligned { get; init; }
    public ulong SchedulerFeedbackFrameSerial { get; init; }
    public uint SchedulerFeedbackVolumeResourceGeneration { get; init; }
    public uint SchedulerFeedbackSchedulerResourceGeneration { get; init; }
    public uint SchedulerFeedbackQueueTransactionGeneration { get; init; }
    public uint SchedulerFeedbackTransportTopologyGeneration { get; init; }
    public uint SchedulerFeedbackSourceLightingGeneration { get; init; }
    public uint SchedulerFeedbackTransportGeneration { get; init; }
    public uint SchedulerFeedbackStatusFlags { get; init; }
    public uint SchedulerConsideredCandidateCount { get; init; }
    /// <summary>Exact COUNTER_COMPACTED total exposed as EligibleCount.</summary>
    public uint SchedulerCompactedCandidateCount { get; init; }
    public uint SchedulerAcceptedWorkCount { get; init; }
    public uint SchedulerCommittedWorkCount { get; init; }
    public uint SchedulerPublishedWorkCount { get; init; }
    public uint SchedulerActiveWorkCount { get; init; }
    public uint SchedulerSourceParticipantCount { get; init; }
    public uint SchedulerHardSourceParticipantCount { get; init; }
    public uint SchedulerRoutineSourceParticipantCount { get; init; }
    public uint SchedulerCachedParticipantCount { get; init; }
    public uint SchedulerSolveParticipantCount { get; init; }
    public uint SchedulerSolveVisitedCount { get; init; }
    public uint SchedulerSolveEpoch { get; init; }
    public uint SchedulerPrimaryRayCount { get; init; }
    public uint SchedulerSourceRayCount { get; init; }
    public uint SchedulerTransportRayCount { get; init; }
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
            GpuTimingRecorded = gpuTimingRecorded,
            SchedulerMode = sceneData.SimpleDdgiSchedulerMode,
            ActiveProbeCount = Math.Max(0, sceneData.DdgiActiveProbeCount),
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
        uint schedulerFeedbackTransportTopologyGeneration)
    {
        ArgumentNullException.ThrowIfNull(timings);
        if (!submitted.Valid)
            return default;

        ulong feedbackSerial =
            ((ulong)feedback.FrameSerialHigh << 32) | feedback.FrameSerialLow;
        bool frameAligned = schedulerFeedbackAvailable &&
            feedbackSerial == submitted.FrameSerial;
        bool generationAligned = frameAligned &&
            feedback.VolumeTableGeneration == submitted.VolumeResourceGeneration &&
            schedulerFeedbackTransportTopologyGeneration ==
                submitted.TransportTopologyGeneration &&
            feedback.SchedulerResourceGeneration == submitted.SchedulerResourceGeneration &&
            feedback.QueueTransactionGeneration ==
                submitted.QueueTransactionGeneration &&
            feedback.SourceLightingGeneration == submitted.SourceLightingGeneration &&
            feedback.TransportGeneration == submitted.TransportGeneration;
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
            SchedulerPrimaryRayCount = feedback.PrimaryRayUsed,
            SchedulerSourceRayCount = feedback.SourceAchievedRays,
            SchedulerTransportRayCount = feedback.TransportRayUsed,
            SchedulerCachedRayCount = cachedRays
        };
    }

    private static uint SaturatingAdd(uint left, uint right) =>
        uint.MaxValue - left < right ? uint.MaxValue : left + right;
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
