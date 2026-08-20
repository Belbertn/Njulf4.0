using System;
using Njulf.Rendering.Debug;

namespace Njulf.Rendering.Data;

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

    public bool IsAcceptedFor(
        in SimpleDdgiSubmittedFrameEvidence submitted) =>
        submitted.Valid &&
        submitted.TailCertificationEnabled &&
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
        Generations.SourceLighting == submitted.SourceLightingGeneration &&
        Generations.CanonicalField == submitted.TransportGeneration &&
        Generations.VolumeTable == submitted.TransportTopologyGeneration &&
        Generations.PhysicalOwnership == submitted.TransportTopologyGeneration &&
        Generations.SchedulerResources == submitted.SchedulerResourceGeneration;
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
    public int CachedSweepCount { get; init; }
    public bool TailCertificationEnabled { get; init; }
    public SimpleDdgiTailCertificateFrameEvidence TailCertificate { get; init; }

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
    /// Availability denotes a named query entry in the exact completed slot.
    /// A recorded sub-microsecond pass can legitimately have a zero duration;
    /// an absent entry means the pass was not recorded for this submission.
    /// </summary>
    public bool GpuTimingAvailable { get; init; }
    public bool GpuAcceleratedSolveTimingAvailable { get; init; }
    public bool GpuSchedulerTailAdmitTimingAvailable { get; init; }
    public bool GpuSchedulerEmitTimingAvailable { get; init; }
    public bool GpuSchedulerCommitTimingAvailable { get; init; }
    public bool GpuDdgiTotalTimingAvailable { get; init; }
    public long GpuAcceleratedSolveMicroseconds { get; init; }
    public long GpuSchedulerTailAdmitMicroseconds { get; init; }
    public long GpuSchedulerEmitMicroseconds { get; init; }
    public long GpuSchedulerCommitMicroseconds { get; init; }
    public long GpuDdgiTotalMicroseconds { get; init; }

    public bool SchedulerFeedbackAvailable { get; init; }
    public bool SchedulerFeedbackFrameAligned { get; init; }
    public bool SchedulerFeedbackGenerationAligned { get; init; }
    public ulong SchedulerFeedbackFrameSerial { get; init; }
    public uint SchedulerFeedbackVolumeResourceGeneration { get; init; }
    public uint SchedulerFeedbackSchedulerResourceGeneration { get; init; }
    public uint SchedulerFeedbackQueueTransactionGeneration { get; init; }
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
        ulong sourceCacheLayoutIdentity)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        if (sceneData.SimpleDdgiActive == 0)
            return default;

        SimpleDdgiTransportConvergenceTelemetry tail =
            sceneData.SimpleDdgiTransportConvergence;
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
            CachedSweepCount = Math.Max(0, sceneData.SimpleDdgiTransportCachedSweepCount),
            TailCertificationEnabled = sceneData.SimpleDdgiTransportTailCertificationEnabled,
            TailCertificate = new SimpleDdgiTailCertificateFrameEvidence
            {
                Phase = tail.TailPhase,
                Reason = tail.TailReason,
                Generations = tail.TailGenerations,
                SolveEpoch = tail.TailSolveEpoch,
                AuditEpoch = tail.TailAuditEpoch,
                ExpectedParticipantCount = tail.TailExpectedParticipantCount,
                AuditedParticipantCount = tail.TailAuditedParticipantCount,
                ExcludedInactiveCount = tail.TailExcludedInactiveCount,
                ExcludedNotVisibleCount = tail.TailExcludedNotVisibleCount,
                ExcludedStaleSourceCount = tail.TailExcludedStaleSourceCount,
                ExcludedInvalidCacheCount = tail.TailExcludedInvalidCacheCount,
                CacheIdentityFailureCount = tail.TailCacheIdentityFailureCount,
                CacheCardinalityFailureCount = tail.TailCacheCardinalityFailureCount,
                CacheSourceGenerationFailureCount = tail.TailCacheSourceGenerationFailureCount,
                CacheSourceEpochFailureCount = tail.TailCacheSourceEpochFailureCount,
                CachePhysicalGenerationFailureCount = tail.TailCachePhysicalGenerationFailureCount,
                ExpectedTexelCount = tail.TailExpectedTexelCount,
                AuditedTexelCount = tail.TailAuditedTexelCount,
                NonFiniteCount = tail.TailNonFiniteCount,
                CounterOverflowCount = tail.TailCounterOverflowCount,
                AuditComplete = tail.TailAuditComplete,
                CertificateCurrent = tail.TailCertificateCurrent
            },
            SourceCacheLayoutIdentity = sourceCacheLayoutIdentity,
            ScheduledPrimaryRayCount = sceneData.DdgiScheduledPrimaryRayCount,
            VisibilityRayCount = sceneData.DdgiVisibilityRayCount
        };
    }

    public static SimpleDdgiCompletedFrameEvidence Complete(
        in SimpleDdgiSubmittedFrameEvidence submitted,
        FrameTimingSnapshot timings,
        bool schedulerFeedbackAvailable,
        in GPUSimpleDdgiSchedulerFeedback feedback)
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
            feedback.SchedulerResourceGeneration == submitted.SchedulerResourceGeneration &&
            feedback.SourceLightingGeneration == submitted.SourceLightingGeneration &&
            feedback.TransportGeneration == submitted.TransportGeneration;
        bool gpuDdgiTotalTimingAvailable = submitted.GpuTimingRecorded &&
            HasAnySimpleDdgiGpuTiming(timings);
        bool acceleratedSolveTimingAvailable = submitted.GpuTimingRecorded &&
            HasRecordedGpuTiming(timings, "SimpleDdgiAcceleratedSolvePass");
        bool tailAdmitTimingAvailable = submitted.GpuTimingRecorded &&
            HasRecordedGpuTiming(timings, "SimpleDdgiSchedule.TailAdmit");
        bool emitTimingAvailable = submitted.GpuTimingRecorded &&
            HasRecordedGpuTiming(timings, "SimpleDdgiSchedule.Emit");
        bool commitTimingAvailable = submitted.GpuTimingRecorded &&
            HasRecordedGpuTiming(timings, "SimpleDdgiSchedulerCommitPass");
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
            GpuAcceleratedSolveTimingAvailable = acceleratedSolveTimingAvailable,
            GpuSchedulerTailAdmitTimingAvailable = tailAdmitTimingAvailable,
            GpuSchedulerEmitTimingAvailable = emitTimingAvailable,
            GpuSchedulerCommitTimingAvailable = commitTimingAvailable,
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
            GpuDdgiTotalMicroseconds = gpuDdgiTotalTimingAvailable
                ? CalculateGpuDdgiTotalMicroseconds(timings)
                : 0,
            SchedulerFeedbackAvailable = schedulerFeedbackAvailable,
            SchedulerFeedbackFrameAligned = frameAligned,
            SchedulerFeedbackGenerationAligned = generationAligned,
            SchedulerFeedbackFrameSerial = feedbackSerial,
            SchedulerFeedbackVolumeResourceGeneration = feedback.VolumeTableGeneration,
            SchedulerFeedbackSchedulerResourceGeneration = feedback.SchedulerResourceGeneration,
            SchedulerFeedbackQueueTransactionGeneration = feedback.QueueTransactionGeneration,
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

    internal static long CalculateGpuDdgiTotalMicroseconds(
        FrameTimingSnapshot timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        return checked(
            timings.GetGpuMicrosecondsOrZero("DdgiFoliageProxyGenerationPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageDemandPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageResidencyPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageFeedbackPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedulePass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiTracePass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiDirectionalRadiancePass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiAcceleratedSolvePass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiBlendPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportAuditPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiRelocateClassifyPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiPublishPass") +
            timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedulerCommitPass"));
    }

    private static bool HasAnySimpleDdgiGpuTiming(FrameTimingSnapshot timings)
        => HasRecordedGpuTiming(timings, "DdgiFoliageProxyGenerationPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiPageDemandPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiPageResidencyPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiPageFeedbackPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiSchedulePass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiTracePass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiDirectionalRadiancePass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiAcceleratedSolvePass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiTransportPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiBlendPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiTransportAuditPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiRelocateClassifyPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiPublishPass") ||
           HasRecordedGpuTiming(timings, "SimpleDdgiSchedulerCommitPass");

    private static bool HasRecordedGpuTiming(
        FrameTimingSnapshot timings,
        string passName) =>
        timings.TryGetPass(passName, out _);

    private static uint SaturatingAdd(uint left, uint right) =>
        uint.MaxValue - left < right ? uint.MaxValue : left + right;
}
