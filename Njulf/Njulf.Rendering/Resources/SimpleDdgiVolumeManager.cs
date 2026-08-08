using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources
{
    public enum SimpleDdgiTrackingState
    {
        Bootstrapping,
        TrackingSourceCohort,
        TrackingPropagation,
        TrackingBounded,
        StaticConverging,
        StaticConverged,
        CapacityLimited
    }
    /// <summary>
    /// Deterministic priority buckets used by the bounded simple-DDGI update
    /// scheduler. Values are ordered intentionally; do not reorder them without
    /// updating the capture/oracle expectations that consume this telemetry.
    /// </summary>
    public enum SimpleDdgiSchedulerWorkClass : byte
    {
        VisibleZeroSupport = 0,
        FreshExposedVisible = 1,
        VisibleDirty = 2,
        VisibleRetry = 3,
        NearMaintenance = 4,
        MidMaintenance = 5,
        FarMaintenance = 6,
        Count = 7,
        None = byte.MaxValue
    }

    /// <summary>
    /// Explains why the scheduler did not admit all eligible work in the current
    /// frame. This is deliberately an enum rather than a formatted string so the
    /// render thread remains allocation-free and capture tools can localize text.
    /// </summary>
    public enum SimpleDdgiSchedulerPressureReason : byte
    {
        None = 0,
        RequestCap = 1,
        PrimaryRayCap = 2,
        FeedbackReducedBudget = 3,
        NoEligibleWork = 4
    }

    /// <summary>
    /// Frame-local, bounded scheduler telemetry. Each work-class field is a
    /// request count; no collection is allocated while a frame is being built.
    /// </summary>
    public readonly record struct SimpleDdgiSchedulerTelemetry(
        int ConfiguredRequestBudget,
        int EffectiveRequestBudget,
        int ScheduledVisibleZeroSupport,
        int ScheduledFreshExposedVisible,
        int ScheduledVisibleDirty,
        int ScheduledVisibleRetry,
        int ScheduledNearMaintenance,
        int ScheduledMidMaintenance,
        int ScheduledFarMaintenance,
        int ReservedVisibleZeroSupport,
        int ReservedFreshExposedVisible,
        int ReservedVisibleDirty,
        int ReservedVisibleRetry,
        int ReservedNearMaintenance,
        int ReservedMidMaintenance,
        int ReservedFarMaintenance,
        int PendingVisibleZeroSupport,
        int PendingFreshExposedVisible,
        int PendingVisibleDirty,
        int PendingVisibleRetry,
        int PendingNearMaintenance,
        int PendingMidMaintenance,
        int PendingFarMaintenance,
        int DeferredRequestCount,
        ulong RejectedPrimaryRayCount,
        SimpleDdgiSchedulerPressureReason PressureReason,
        ulong LastCompletedGpuMicroseconds,
        ulong TargetGpuMicroseconds,
        bool DeterministicFixedBudget);

    /// <summary>
    /// A renderer-owned timing sample for bounded next-frame scheduling feedback.
    /// Passing <see cref="DeterministicFixedBudget"/> keeps validation scheduling
    /// exactly at its authored request cap.
    /// </summary>
public readonly record struct SimpleDdgiSchedulingFeedback(
        ulong CompletedGpuMicroseconds,
        ulong TargetGpuMicroseconds,
    bool DeterministicFixedBudget);

/// <summary>
/// O(1) atmosphere-cohort feedback exported by the DDGI scheduler.  All counts are maintained
/// incrementally as probe state changes; a capture/admission caller never scans the probe pool.
/// </summary>
public readonly record struct SimpleDdgiAtmosphereCohortFeedback(
    uint VolumeResourceGeneration,
    uint SourceCohortGeneration,
    uint AdmittedSourceCohortGeneration,
    uint PropagationGeneration,
    uint PublishedPropagationGeneration,
    int ParticipatingProbeCount,
    int StaleParticipatingProbeCount,
    int VisiblePriorityParticipatingProbeCount,
    int VisiblePrioritySourceReadyProbeCount,
    int VisiblePriorityPublishedProbeCount,
    bool SourceCohortActive,
    bool VisiblePublicationBoundaryComplete,
    bool MinimumPropagationBoundaryComplete,
    bool QuietPeriodComplete,
    float AchievableSourceSweepSeconds,
    uint SourceCohortStartFrame = 0U,
    uint SourceCohortCompletionFrame = 0U,
    ulong SourceCohortStartCount = 0UL,
    ulong SourceCohortCompletionCount = 0UL,
    int TargetSourceProbeCount = 0,
    int AdmittedSourceProbeCount = 0,
    int ScheduledSourceProbeCount = 0,
    ulong TargetSourceRayCount = 0UL,
    ulong AdmittedSourceRayCount = 0UL,
    ulong ScheduledSourceRayCount = 0UL,
    int SourceCapacityShortfall = 0,
    ulong SourceRayCapacityShortfall = 0UL,
    uint StaticConvergedGeneration = 0U,
    bool StaticConvergencePending = false,
    ulong StaleReadbackRejectionCount = 0UL,
    ulong ResourceGenerationRejectionCount = 0UL,
    bool ResidencyFeedbackComplete = true,
    uint ResidencyEventSourceGeneration = 0U,
    uint ResidencyEventCohortGeneration = 0U,
    int ResidencyAdmissionProbeCount = 0,
    int ResidencyEvictionProbeCount = 0,
    int ResidencyOtherGenerationEvictionProbeCount = 0)
{
    public GiAtmosphereCohortFeedback ToAdmissionFeedback() => new(
        ConsumesSteppedAtmosphere: true,
        ParticipatingProbeCount,
        SourceCohortActive,
        StaleParticipatingProbeCount,
        VisiblePublicationBoundaryComplete,
        MinimumPropagationBoundaryComplete,
        AchievableSourceSweepSeconds,
        VolumeResourceGeneration,
        SourceCohortGeneration,
        AdmittedSourceCohortGeneration,
        PropagationGeneration,
        PublishedPropagationGeneration,
        VisiblePriorityParticipatingProbeCount,
        VisiblePrioritySourceReadyProbeCount,
        VisiblePriorityPublishedProbeCount,
        QuietPeriodComplete,
        CandidateStreamActive: false,
        SourceCohortStartFrame,
        SourceCohortCompletionFrame,
        SourceCohortStartCount,
        SourceCohortCompletionCount,
        TargetSourceProbeCount,
        AdmittedSourceProbeCount,
        ScheduledSourceProbeCount,
        TargetSourceRayCount,
        AdmittedSourceRayCount,
        ScheduledSourceRayCount,
        SourceCapacityShortfall,
        SourceRayCapacityShortfall,
        StaticConvergedGeneration,
        StaticConvergencePending,
        StaleReadbackRejectionCount,
        ResourceGenerationRejectionCount,
        ResidencyFeedbackComplete,
        ResidencyEventSourceGeneration,
        ResidencyEventCohortGeneration,
        ResidencyAdmissionProbeCount,
        ResidencyEvictionProbeCount,
        ResidencyOtherGenerationEvictionProbeCount);
}

    /// <summary>
    /// A contiguous update-queue range whose probes all use the same active ray
    /// count. Trace and transport dispatch this exact rectangle instead of the
    /// queue-wide maximum-ray rectangle.
    /// </summary>
    public readonly record struct SimpleDdgiRayDispatchBatch(
        int QueueOffset,
        int ProbeCount,
        int RaysPerProbe);

    internal readonly record struct SimpleDdgiCapacityKey(
        DdgiQualityTier QualityTier,
        ulong TopologyFingerprint,
        int ProbeCount,
        SimpleDdgiProbeResidencyMode ResidencyMode,
        int DensePayloadProbeCount,
        int SparseVirtualProbeCount,
        int SparseVirtualPageCount,
        int SparsePhysicalPageCapacity,
        int PhysicalProbeCapacity,
        ulong ResidencyArenaBytes,
        ulong ResidencyFeedbackReadbackBytes,
        int RayCapacity,
        int RequestCapacity,
        bool ReadbackRequired,
        bool SampledAtlasRequested,
        bool SampledAtlasProvisioningAvailable,
        ulong SampledAtlasBudgetBytes,
        bool TransportV2Enabled,
        SimpleDdgiSchedulerMode SchedulerMode,
        int SchedulerActiveLaneCount,
        ulong SchedulerArenaBytes,
        ulong SchedulerValidationReadbackBytes,
        bool FeatureEnabled);

    public sealed class SimpleDdgiVolumeManager : IDisposable
    {
        public const int IrradianceTexelsPerProbe = 8;
        public const int VisibilityTexelsPerProbe = 16;

        private const ulong MinBufferSize = 16;
        private const int VolumeKindLegacy = 0;
        private const int VolumeKindAuthored = 1;
        private const int VolumeKindRing = 2;
        private static readonly ulong ParamsSize = (ulong)Marshal.SizeOf<GPUSimpleDdgiParams>();
        private static readonly ulong VolumeStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiVolume>();
        private static readonly ulong VolumePagingStride =
            (ulong)Marshal.SizeOf<GPUSimpleDdgiVolumePaging>();
        private static readonly ulong ParamsBufferSize =
            ParamsSize +
            VolumeStride * GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount +
            VolumePagingStride *
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount;
        private static readonly ulong ProbeStateStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>();
        private static readonly ulong ReceiverProbeStride =
            (ulong)Marshal.SizeOf<GPUSimpleDdgiReceiverProbe>();
        private static readonly ulong RelocationClassificationStride =
            (ulong)Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>();
        private const uint ProbeStateFreshFlag = 1u << 0;
        private const uint ProbeStateScrollExposedFlag = 1u << 1;
        private const uint ProbeStateInactiveFlag = 1u << 2;
        private const uint ProbeStateRelocationPendingFlag = 1u << 3;
        // Set by the V2 trace/transport shaders when a solver-only lookup finds
        // a missing cache entry. The completed readback turns it into a normal
        // physical-slot invalidation and full source refresh.
        private const uint ProbeStateSourceCacheInvalidFlag = 1u << 4;
        // Visibility validity is probe-scoped. The RG16F atlas carries only
        // moments; publication sets this bit after the complete tile is visible.
        private const uint ProbeStateVisibilityValidFlag = 1u << 5;
        // Kept separate from scene light/emissive/geometry bits so a capture
        // can distinguish an authored lighting edit from a live transport
        // calibration change that deliberately restarted convergence.
        private const uint TransportCalibrationDirtyReasonFlag = 1u << 3;
        private const uint ProbeUpdateMaintenanceFlag = 1u << 12;
        // V2 source refreshes are explicit queue work. Solver-only reuse entries
        // retain their cached source radiance and therefore consume no primary
        // ray budget while still advancing the recursive transport field.
        private const uint ProbeUpdateSourceRefreshFlag = 1u << 13;
        // Captured when the queue item is built because publication may happen
        // after age/classification metadata has changed. Bits 14-15 are CPU-only;
        // the packed shader ray count starts at bit 16.
        private const uint ProbeUpdateRoutineSourceRefreshFlag = 1u << 15;
        private const byte RoutineMaintenanceNone = 0;
        private const byte RoutineMaintenanceConvergencePending = 1;
        private const byte RoutineSourceValidationPending = 2;
        // Packed into the simple-DDGI flag word so this artist-facing gather
        // quality control does not grow the hot params header or shift volumes.
        private const int SecondVolumeOwnershipEarlyOutThresholdShift = 12;
        private const uint SecondVolumeOwnershipEarlyOutThresholdMask = 0xffu << SecondVolumeOwnershipEarlyOutThresholdShift;
        private const uint ThinSurfaceTransmissionFlag = 1u << 20;
        private const uint ForceLegacyFarFieldFallbackEvaluationFlag = 1u << 21;
        private const uint DirectionCodebookFlag = 1u << 22;
        private const uint DirectionValidationFlag = 1u << 30;
        // Queue-local quality profile.  Probe-state generation uses a different
        // buffer, so bits 3..15 are available to make the trace shader consume
        // actual ring/cascade work limits instead of one global quality value.
        private const int ProbeUpdateMaterialTextureCascadeShift = 3;
        private const uint ProbeUpdateMaterialTextureCascadeMask = 0x7u << ProbeUpdateMaterialTextureCascadeShift;
        private const int ProbeUpdateMaxShadedLightsShift = 6;
        private const uint ProbeUpdateMaxShadedLightsMask = 0x3fu << ProbeUpdateMaxShadedLightsShift;
        // Bits 8..31 are mirrored by SIMPLE_DDGI_PROBE_FLAG_GENERATION_* in
        // ddgi_simple_shared.glsl.  Keep the generation independent of the
        // queue's packed ray-count flags: a probe-state generation protects every
        // asynchronous producer/consumer of the physical slot.
        private const int ProbeStateGenerationShift = 8;
        private const uint ProbeStateGenerationValueMask = 0x00ffffffu;
        private const int ProbeUpdateAgeShift = 24;
        private const uint ProbeUpdateAgeValueMask = 0xffu;
        // Confidently enclosed probes are not missing data. Retest them slowly
        // for streaming/geometry changes without promoting thousands of buried
        // slots into the highest-priority zero-support class every few frames.
        internal const uint InactiveProbeRetryFrames = 600u;
        // Once an exact field certificate is current, only excluded probes can
        // remain eligible without invalidating that certificate. Give those
        // reactivation/relocation candidates a deterministic bounded pulse
        // instead of executing speculative maintenance every rendered frame.
        internal const uint CertifiedMaintenancePulseFrames = 64u;
        private const uint ProbeLifecycleMinimumRecoveryFrames = 32u;
        // Keep synchronized with
        // SIMPLE_DDGI_RELOCATION_PENDING_MAX_RETRY_AGE in
        // ddgi_simple_shared.glsl.
        internal const uint RelocationPendingMaximumRetryAge = 32u;
        internal const uint SourceCacheRadianceDebugViewMode = 125u;
        private const int SchedulerWorkClassCount = (int)SimpleDdgiSchedulerWorkClass.Count;
        private const byte ProbeSchedulingScrollExposedFlag = 1 << 0;
        private const byte ProbeSchedulingRegionalDirtyFlag = 1 << 1;
        private const byte ProbeSchedulingVisibleFlag = 1 << 2;
        private const byte AtmosphereParticipantFlag = 1 << 0;
        private const byte AtmosphereVisibleFlag = 1 << 1;
        private const byte AtmosphereSourceReadyFlag = 1 << 2;
        private const byte AtmospherePublishedFlag = 1 << 3;
        private const byte SchedulerTransportParticipantFlag = 1 << 0;
        private const byte SchedulerTransportSourceRepairFlag = 1 << 1;
        private const byte SchedulerTransportPendingConvergenceFlag = 1 << 2;
        private const byte SchedulerInactiveDeferredFlag = 1 << 3;
        private const byte SchedulerTransportRoutineSourceRepairFlag = 1 << 4;
        private const byte SchedulerTransportRoutineMaintenanceFlag = 1 << 5;
        private const int TransportProbeStateReasonCount =
            (int)SimpleDdgiTransportProbeStateReason.Count;
        private const int TransportResidualDistributionBucketCount = 8;
        private const int TransportSourceEpochDistributionBucketCount = 4;
        private const int TransportSolverGenerationBucketCount = 6;
        private const int TransportSolverCompletionLatencyBucketCount = 512;
        private const byte SchedulerLifecycleFreshFlag = 1 << 0;
        private const byte SchedulerLifecycleScrollExposedFlag = 1 << 1;
        private const byte SchedulerLifecycleRelocationPendingFlag = 1 << 2;
        private const byte SchedulerLifecycleUnpublishedFlag = 1 << 3;
        private const byte SchedulerLifecycleVisibleUnsupportedFlag = 1 << 4;
        private const byte SchedulerLifecycleVisiblePendingFlag = 1 << 5;
        private const byte SchedulerLifecycleGenerationStaleFlag = 1 << 6;
        private const int SchedulerVisibleImportanceThreshold = 4;
        // Absolute fixed-point defect accepted only close to black. Combined
        // with the authored relative threshold, this bounds the residual without
        // subtracting real low-energy bounce from the convergence signal.
        internal const float TransportAbsoluteResidualTolerance = 0.0001f;
        private const float TransportResidualEnvelopeDecay = 0.25f;
        // A source refresh restarts convergence metadata. Give a field enough
        // iterations for long multi-hop paths before the corruption/noise
        // watchdog is allowed to resample it.
        private const int TransportSourceRefreshSolverWatchdogMultiplier = 16;
        private const int TransportGlobalSourceRefreshWatchdogFrameMultiplier = 2;
        private const float RoutineSourcePropagationResidualMultiplier = 2.0f;
        private const float GiSourceSweepNominalFramesPerSecond = 60.0f;
        private const float GiSourceSweepMinimumFramesPerSecond = 4.0f;
        private const float GiSourceSweepMaximumFramesPerSecond = 240.0f;
        private const float GiSourceSweepFrameRateSmoothing = 0.10f;
        private const int GiSourceAgeHistogramMaximumFrames =
            120 * (int)GiSourceSweepMaximumFramesPerSecond;
        // Exact buckets keep the published P50/P95 meaningful during a soak.
        // Size this source-age histogram for the maximum authored 120-second
        // target at the capped 240 Hz observation rate; overflow still fails
        // conservatively to the measured maximum without render-thread allocation.
        private const int DirtyLatencyBucketCount = 4_096;
        private const int SourceCohortQuietFrameCount = 4;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly Action<RuntimeStallReason, string, Action> _recordRuntimeStall;
        private readonly Func<ulong> _waitForBindlessDescriptorReaders;
        private readonly SimpleDdgiGpuScheduler _gpuScheduler;
        private readonly SimpleDdgiProbePageCache _probePageCache;
        private const int BufferRetirementCapacity = 512;
        private readonly GpuCompletionRetirementQueue _bufferRetirement =
            new(BufferRetirementCapacity);
        private readonly GpuRetirementRecord[] _bufferRetirementScratch =
            new GpuRetirementRecord[BufferRetirementCapacity];
        private ulong _lastSubmittedFrameFenceValue;
        private ulong _completedFrameFenceValue;
        private ulong _resourceLastUseFrameFenceValue;
        private bool _capacityTransitionDeferred;
        private string _capacityTransitionDeferredReason = string.Empty;
        private readonly List<VolumeCandidate> _volumeCandidates = new(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount + 3);
        private readonly GPUSimpleDdgiVolume[] _volumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiVolumePaging[] _volumePagingScratch =
            new GPUSimpleDdgiVolumePaging[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiVolume[] _previousVolumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly SimpleDdgiVolumePurpose[] _volumePurposes = new SimpleDdgiVolumePurpose[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumePriorities = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly SimpleDdgiTransportVolumeOrderKey[] _transportVolumeOrderKeys =
            new SimpleDdgiTransportVolumeOrderKey[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _transportVolumeOrder =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiSchedulerVolumePolicy[] _gpuVolumePolicyScratch =
            new GPUSimpleDdgiSchedulerVolumePolicy[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiSchedulerVolumePolicy[] _gpuPreviousVolumePolicyScratch =
            new GPUSimpleDdgiSchedulerVolumePolicy[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiSchedulerDirtyRegion[] _gpuDirtyRegionScratch =
            new GPUSimpleDdgiSchedulerDirtyRegion[SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity];
        private readonly GPUSimpleDdgiProbeUpdate[] _updateQueueScratch = new GPUSimpleDdgiProbeUpdate[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly GPUSimpleDdgiProbeUpdate[] _rayDispatchSortScratch = new GPUSimpleDdgiProbeUpdate[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly byte[] _rayDispatchWorkClassSortScratch = new byte[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly int[] _rayDispatchRayCountHistogram = new int[GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe + 1];
        private readonly int[] _rayDispatchRayCountOffsets = new int[GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe + 1];
        private readonly int[] _rayDispatchRayCountWriteOffsets = new int[GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe + 1];
        private readonly SimpleDdgiRayDispatchBatch[] _rayDispatchBatches = new SimpleDdgiRayDispatchBatch[GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe];
        private int _rayDispatchBatchCount;
        private readonly GPUSimpleDdgiProbeState[] _probeStateScratch = new GPUSimpleDdgiProbeState[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly List<int> _probeStateDirtySlots = new(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
        private readonly List<BufferUploadRun> _probeStateUploadRuns = new(256);
        private readonly GPUSimpleDdgiReceiverProbe[] _receiverProbeInvalidationScratch =
            new GPUSimpleDdgiReceiverProbe[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly List<int> _receiverProbeInvalidationSlots =
            new(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
        private readonly List<BufferUploadRun> _receiverProbeUploadRuns = new(256);
        private const int MaxSparseProbeStateUploadRuns = 256;
        // Diagnostics need an exact per-volume age percentile. Reusing one bounded
        // selection buffer avoids allocations and avoids sorting the whole probe pool.
        private readonly uint[] _probeAgePercentileScratch = new uint[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        // Scheduler scratch is retained for the renderer lifetime. Allocating five
        // small arrays every frame showed up as avoidable managed churn in long
        // travel/soak runs, particularly while authored-volume layouts change.
        private readonly int[] _volumeQuotaScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaUsageScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaMinimumScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaMaximumScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaCapacityScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaWeightScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        // The scheduler uses fixed-size class/volume matrices.  The volume limit is
        // deliberately tiny, but retaining these avoids per-frame List/array churn
        // in long travel and dirty-light soaks.
        private readonly int[] _volumeWorkClassPendingScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeWorkClassQuotaScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeWorkClassUsageScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeSourceRefreshPendingScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeSourceRefreshUsageScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeCachedSolverPendingScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _volumeCachedSolverUsageScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount];
        private readonly int[] _scheduledWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _reservedWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _pendingWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _rayRejectedWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly byte[] _queuedWorkClassScratch = new byte[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        // Probe membership is persistent. Queue counts are the authoritative
        // pending counters; a frame now walks only admitted work and state changes
        // instead of rediscovering every probe in every priority pass.
        private readonly SimpleDdgiPersistentProbeQueues _schedulerWorkQueues;
        private readonly SimpleDdgiPersistentProbeQueues _schedulerSourceRefreshQueues;
        private readonly SimpleDdgiPersistentProbeQueues _schedulerCachedSolverQueues;
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerWakeHeap = new();
        private int[] _probeSchedulerDirtyIndices = Array.Empty<int>();
        private byte[] _probeSchedulerDirty = Array.Empty<byte>();
        private byte[] _probeSchedulerTransportStates = Array.Empty<byte>();
        private byte[] _probeSchedulerVolumeIndices = Array.Empty<byte>();
        private byte[] _probeTransportTelemetryReasons = Array.Empty<byte>();
        private byte[] _probeTransportTelemetryResidualBuckets = Array.Empty<byte>();
        private byte[] _probeTransportTelemetrySourceEpochBuckets = Array.Empty<byte>();
        private byte[] _probeTransportTelemetryResidualQualifiedPending = Array.Empty<byte>();
        private byte[] _probeTransportTelemetryGenerationBuckets = Array.Empty<byte>();
        private uint[] _probeTransportSolverCompletionRecordedSourceGenerations =
            Array.Empty<uint>();
        private byte[] _probeTransportTelemetryVolumeIndices = Array.Empty<byte>();
        private readonly int[] _transportProbeStateReasonCounts =
            new int[TransportProbeStateReasonCount];
        private readonly int[] _volumeTransportProbeStateReasonCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                TransportProbeStateReasonCount];
        private readonly int[] _volumeTransportResidualDistributionCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                TransportResidualDistributionBucketCount];
        private readonly int[] _volumeTransportSourceEpochDistributionCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                TransportSourceEpochDistributionBucketCount];
        private readonly int[] _volumeTransportResidualQualifiedPendingCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeTransportSolverCompletionLatencyHistograms =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                TransportSolverCompletionLatencyBucketCount];
        private readonly int[] _volumeTransportSolverCompletionLatencySampleCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeTransportSolverCompletionLatencyMaxFrames =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeTransportSolverGenerationDistributionCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                TransportSolverGenerationBucketCount];
        private ulong _transportDispatchLaneCount;
        private ulong _transportUsefulDispatchLaneCount;
        private ulong _transportNoOpDispatchLaneCount;
        private readonly int[] _volumeScheduledTransportProbeCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly ulong[] _volumeScheduledTransportRayCounts =
            new ulong[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private byte[] _probeSchedulerLifecycleStates = Array.Empty<byte>();
        private byte[] _probeAtmosphereCohortFlags = Array.Empty<byte>();
        private uint[] _probeSchedulerTrackedLastUpdatedFrames = Array.Empty<uint>();
        private uint[] _probeSchedulerTrackedSourceRefreshFrames = Array.Empty<uint>();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerFreshAgeHeap = new();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerScrollExposedAgeHeap = new();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerRelocationPendingAgeHeap = new();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerUnpublishedAgeHeap = new();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerVisibleUnsupportedAgeHeap = new();
        private readonly SimpleDdgiSchedulerWakeHeap _schedulerGenerationStaleAgeHeap = new();
        private readonly SimpleDdgiIncrementalAgeHistogram _schedulerVisibleUnsupportedAgeHistogram =
            new(maximumExactAge: 600);
        private readonly SimpleDdgiIncrementalAgeHistogram _schedulerVisiblePendingAgeHistogram =
            new(maximumExactAge: 600);
        private readonly SimpleDdgiIncrementalAgeHistogram _schedulerGenerationStaleAgeHistogram =
            new(maximumExactAge: GiSourceAgeHistogramMaximumFrames);
        private int[] _probeVisibilityDirtyIndices = Array.Empty<int>();
        private byte[] _probeVisibilityDirty = Array.Empty<byte>();
        private byte[] _probeVisibleFreshCounted = Array.Empty<byte>();
        private readonly int[] _volumeVisibleFreshProbeCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private int _probeSchedulerDirtyCount;
        private int _probeVisibilityDirtyCount;
        private bool _schedulerRebuildRequired = true;
        private bool _schedulerVisibilityFullRefreshRequired = true;
        private bool _hasSchedulerVisibilityCamera;
        private Vector3 _schedulerVisibilityCameraPosition;
        private bool _schedulerGlobalStateValid;
        private SchedulerGlobalStateSnapshot _schedulerGlobalState;
        private int _schedulerParticipatingProbeCount;
        private int _schedulerSourceRepairProbeCount;
        // GPU-resident feedback is delayed and cannot safely overwrite the
        // CPU scheduler mirrors. These counts are the resident authority used
        // only for the tail solve/audit control plane.
        private int _transportResidentParticipantCount;
        private int _transportResidentSourceRepairProbeCount;
        private int _schedulerRoutineSourceRepairProbeCount;
        private int _schedulerRoutineMaintenancePendingProbeCount;
        private int _schedulerPendingConvergenceProbeCount;
        private int _schedulerAtmosphereVisibleParticipatingProbeCount;
        private int _schedulerAtmosphereVisibleSourceReadyProbeCount;
        private int _schedulerAtmosphereVisiblePublishedProbeCount;
        private readonly int[] _volumeAtmosphereParticipatingProbeCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeAtmosphereRayCounts =
            new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private int _schedulerRoutineWakeRefreshBudget = 1;
        private int _schedulerInactiveDeferredProbeCount;
        private ulong _schedulerInactiveDeferredSavedPrimaryRayCount;
        private byte[] _probeFresh = Array.Empty<byte>();
        private byte[] _probeInactive = Array.Empty<byte>();
        private byte[] _probeRelocationPending = Array.Empty<byte>();
        private byte[] _probeVisibilityValid = Array.Empty<byte>();
        private byte[] _probeQueued = Array.Empty<byte>();
        // Per-physical-slot metadata follows the same toroidal mapping as fresh,
        // relocation, and generation state. It lets scheduling distinguish a local
        // dirty event from ordinary maintenance without inspecting managed regions
        // after the upload path has returned.
        private byte[] _probeSchedulingFlags = Array.Empty<byte>();
        private byte[] _probeDirtyReasons = Array.Empty<byte>();
        private byte[] _probeRoutineMaintenancePending = Array.Empty<byte>();
        private byte[] _probeVisibilityImportance = Array.Empty<byte>();
        // Indexed by physical slot.  The generation travels with every update
        // request and rejects late trace/blend work after a toroidal remap.
        private uint[] _probeGenerations = Array.Empty<uint>();
        // A bounded per-frame invalidation marker lets independent dirty events
        // deduplicate while still advancing a generation when an already-fresh
        // physical slot is immediately reused by another scroll.
        private uint[] _probeInvalidationMarkers = Array.Empty<uint>();
        private uint _nextProbeInvalidationMarkerSerial;
        private uint _currentProbeInvalidationMarkerSerial;
        // GPU-resident regional invalidation uses an event generation, not the
        // CPU's per-frame deduplication serial. A dirty producer may publish the
        // same region for several frames; advancing this value every frame would
        // make already-committed probes look dirty again indefinitely and prevent
        // the complete source cohort from ever entering its solve epoch.
        private uint _gpuDirtyRegionGeneration = 1u;
        private bool _gpuDirtyRegionsPresentLastFrame;
        private ulong _gpuDirtyRegionSignature;
        // These mirrors make a full state upload safe during a scroll or regional
        // invalidation.  Without them, a fresh-slice upload would accidentally
        // erase relocation/classification for every preserved physical slot.
        private Vector3[] _probeRelocations = Array.Empty<Vector3>();
        private float[] _probeActiveWeights = Array.Empty<float>();
        private uint[] _probeClassifications = Array.Empty<uint>();
        private byte[] _probeStableUpdateCounts = Array.Empty<byte>();
        private float[] _probeLuminanceChangeEma = Array.Empty<float>();
        // Age is derived lazily from this timestamp. Incrementing 15k-32k age
        // cells every frame was one of the scheduler's dominant CPU costs.
        private uint[] _probeLastUpdatedFrames = Array.Empty<uint>();
        private readonly uint[] _probeAgeSnapshotScratch = new uint[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private uint _oldestVisibleUnsupportedProbeAge;
        private int _visibleUnsupportedProbeCountAboveLatencyTarget;
        private int _visibleZeroSupportRepairUpdateCount;
        private int _probeLifecycleLatencyTargetFrames;
        private uint _maximumFreshProbeAge;
        private uint _maximumScrollExposedProbeAge;
        private uint _maximumRelocationPendingProbeAge;
        private uint _maximumUnpublishedProbeAge;
        private int _probeLifecycleBoundExceededCount;
        // Source-cache lifetime follows the physical probe slot.  A lighting
        // generation changes globally; geometry/scroll invalidation clears only
        // the affected slot, preserving static source work elsewhere.
        private uint[] _probeSourceLightingGenerations = Array.Empty<uint>();
        private uint[] _probeLastSourceRefreshFrames = Array.Empty<uint>();
        // A per-slot serial separates convergence feedback from the physical
        // slot generation. Periodic source resampling intentionally preserves
        // the physical slot, but delayed residual readback from the old source
        // field must not be credited to the new one.
        private uint[] _probeSourceEpochs = Array.Empty<uint>();
        // The cache is keyed by the global maximum ray sequence, while each ring
        // traces a differently sized stratified source sequence.  Retain that
        // source count per physical probe so later maintenance work selects a
        // true subset of the traced directions rather than falling through to an
        // accidental primary trace at a different sequence index.
        private ushort[] _probeSourceRayCounts = Array.Empty<ushort>();
        private byte[] _probeTransportGenerationCounts = Array.Empty<byte>();
        // 0 = no outstanding regional-dirty latency sample, 1 = awaiting the
        // first completed blend, 2 = awaiting stable readback convergence.
        private byte[] _probeDirtyLatencyStates = Array.Empty<byte>();
        private uint[] _probeDirtyLatencyStartFrames = Array.Empty<uint>();
        private readonly GPUSimpleDdgiSchedulerProbeState[] _gpuResidentBootstrapStateScratch =
            new GPUSimpleDdgiSchedulerProbeState[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly uint[] _gpuResidentBootstrapLaneCursorScratch =
            new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
        private readonly uint[] _dirtyFirstUpdateLatencyBuckets = new uint[DirtyLatencyBucketCount];
        private readonly uint[] _dirtyConvergenceLatencyBuckets = new uint[DirtyLatencyBucketCount];
        private readonly uint[] _dirtyFirstScheduledLatencyBuckets = new uint[DirtyLatencyBucketCount];
        private uint _dirtyFirstScheduledLatencySampleCount;
        private uint _dirtyFirstUpdateLatencySampleCount;
        private uint _dirtyConvergenceLatencySampleCount;
        private uint _dirtyFirstScheduledLatencyCensoredCount;
        private uint _dirtyFirstUpdateLatencyCensoredCount;
        private uint _dirtyConvergenceLatencyCensoredCount;
        private uint _dirtyLatencyOutstandingEventCount;
        private uint _dirtyFirstScheduledLatencyMaxFrames;
        private uint _dirtyFirstUpdateLatencyMaxFrames;
        private uint _dirtyConvergenceLatencyMaxFrames;
        private int _previousVolumeCount;
        private readonly Vector3[] _ringOrigins = new Vector3[3];
        private readonly bool[] _ringHasOrigins = new bool[3];

        private BufferHandle _paramsBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _transportIrradianceAtlasBuffer;
        private BufferHandle _transportSourceCacheBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BufferHandle _rayResultScratchBuffer;
        private BufferHandle _probeStateBuffer;
        private BufferHandle _receiverProbeBuffer;
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _relocationClassificationBuffer;
        private readonly BufferHandle[] _probeStateReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly ulong[] _probeStateReadbackProvisionedBytes = new ulong[RenderingConstants.FramesInFlight];
        private readonly bool[] _probeStateReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly int[] _probeStateReadbackProbeCounts = new int[RenderingConstants.FramesInFlight];
        private readonly ulong[] _probeStateReadbackBytes = new ulong[RenderingConstants.FramesInFlight];
        private readonly ulong[] _probeClassificationReadbackBytes = new ulong[RenderingConstants.FramesInFlight];
        private readonly int[] _probeClassificationReadbackFirstProbes = new int[RenderingConstants.FramesInFlight];
        private int _probeClassificationReadbackCursor;
        private readonly uint[] _probeStateReadbackGenerations = new uint[RenderingConstants.FramesInFlight];
        // Markers identify exactly which physical slots were completed by the
        // readback frame.  Stability must never advance merely because an old
        // state buffer happened to be copied again.
        private readonly uint[][] _probeStateReadbackUpdateMarkers = new uint[RenderingConstants.FramesInFlight][];
        private readonly uint[][] _probeStateReadbackExpectedProbeGenerations = new uint[RenderingConstants.FramesInFlight][];
        private readonly byte[][] _probeStateReadbackExpectedTransportGenerations = new byte[RenderingConstants.FramesInFlight][];
        private readonly uint[][] _probeStateReadbackExpectedSourceEpochs = new uint[RenderingConstants.FramesInFlight][];
        // The GPU mutates probe state only for the submitted queue. Retain that
        // bounded index set with each fence-safe readback so consuming a completed
        // frame is O(updated probes), not O(the entire probe field).
        private readonly int[][] _probeStateReadbackUpdatedProbeIndices = new int[RenderingConstants.FramesInFlight][];
        private readonly int[] _probeStateReadbackUpdatedProbeCounts = new int[RenderingConstants.FramesInFlight];
        private readonly uint[] _probeStateReadbackUpdateMarkerSerials = new uint[RenderingConstants.FramesInFlight];
        private uint _nextProbeStateReadbackUpdateMarkerSerial;
        private ulong _irradianceAtlasBytes;
        private ulong _transportIrradianceAtlasBytes;
        private ulong _transportSourceCacheBytes;
        // Cache addressing uses params.raysPerProbe in the shader. Retain the
        // capacity used to populate the current entries so a live quality-tier
        // change that shrinks the allocation (and therefore does not force a
        // buffer reallocation) cannot reinterpret old probe-stride addresses.
        private int _transportSourceCacheRayCapacity;
        private ulong _visibilityAtlasBytes;
        private ulong _rayScratchBytes;
        private ulong _probeStateBytes;
        private ulong _receiverProbeBytes;
        private ulong _probeUpdateQueueBytes;
        private ulong _relocationClassificationBytes;
        private ulong _probeStateReadbackBufferBytes;
        private BindlessHeap? _registeredBindlessHeap;
        // The SSBO atlases remain the canonical writer and rollback path.  The
        // optional image mirror exists only for controlled sampled-atlas A/B
        // captures, so allocation or descriptor failures never disable DDGI.
        private SimpleDdgiSampledAtlas? _sampledAtlas;
        private SimpleDdgiStorageLayout _storageLayout =
            SimpleDdgiStorageLayout.Empty();
        private SimpleDdgiSampledAtlasLayout _sampledAtlasLayout =
            SimpleDdgiSampledAtlasLayout.Disabled();
        private ulong _sampledAtlasPublicationGeneration;
        private string _sampledAtlasFallbackReason = string.Empty;
        // Avoid retrying a known-unsatisfied image allocation every frame. A
        // topology change or explicit feature toggle clears this latch.
        private int _sampledAtlasFailedProbeCount = -1;
        private ulong _sampledAtlasFailureAllocationBudgetBytes;
        private long _lastSampledAtlasSynchronizationMicroseconds;
        private GPUSimpleDdgiParams _lastParams;
        private bool _controlHeaderInitialized;
        private bool _wasSimpleDdgiEnabled;
        private int _volumeCount;
        private int _probeCount;
        private int _probeCountX;
        private int _probeCountY;
        private int _probeCountZ;
        private int _raysPerProbe;
        private int _updateStartProbe;
        private int _probesToUpdate;
        private uint _frameIndex;
        private Vector3 _gridOrigin;
        private bool _hasGridOrigin;
        private bool _recenteredThisFrame;
        private bool _atlasPreservedOnRecenterThisFrame;
        private bool _atlasClearRequired = true;
        private bool _atlasClearedThisFrame;
        private bool _atlasFresh = true;
        private bool _probeStateUploadRequired = true;
        private bool _receiverProbeClearRequired = true;
        private ulong _receiverProbeInvalidationBytesThisFrame;
        private int _receiverProbeInvalidationRunCountThisFrame;
        private bool _receiverProbeFullClearThisFrame;
        private int _totalRecenterCount;
        private int _totalAtlasClearCount;
        private int _totalAtlasPreserveOnRecenterCount;
        private int _framesSinceLastClear = int.MaxValue;
        private int _framesSinceLastRecenter = int.MaxValue;
        private int _fullRefreshFrameCount;
        private int _partialRefreshFrameCount;
        private int _newlyInvalidatedProbeCount;
        private int _recenterRefreshProbeCount;
        private int _dirtyRefreshProbeCount;
        private int _ageRefreshProbeCount;
        private int _fullRefreshProbeCount;
        private int _scrollCopyCount;
        private bool _ringRecenteredThisFrame;
        private int _activeProbeCount;
        private int _probeRelocationCount;
        private int _classifiedInactiveProbeCountEstimate;
        private float _averageRelocationFractionEstimate;
        private float _relocationFractionSumEstimate;
        private float _averageBackfaceRatioEstimate;
        private float _averageCloseRatioEstimate;
        private float _averageHardInvalidProbeScoreEstimate;
        private int _probeStateReadbackValid;
        private int _probeConvergenceReadbackValid;
        private uint _volumeTableGeneration;
        // The volume table and the physical-slot ownership map usually change
        // together, but they are separate certificate dimensions. A compatible
        // table edit must not accidentally make an old slot-ownership witness
        // look current, and both values must remain non-zero across wrap.
        private uint _physicalOwnershipGeneration = 1u;
        private int _inactiveProbeSkipCount;
        private ulong _inactiveProbeSavedPrimaryRayCount;
        private int _lightingDirtyFrames;
        private int _lightingDirtyBoostedCapacity;
        private bool _hasLightingSignature;
        private ulong _lastLightingSignature;
        // Source and solver controls have different invalidation domains. A
        // trace-quality change must rebuild cached source radiance, while a
        // transport-only calibration can retain those expensive ray results and
        // simply restart the bounded Jacobi solve from the published field.
        private bool _hasTransportCalibrationSignatures;
        private ulong _lastTransportSourceCalibrationSignature;
        private ulong _lastTransportSolverCalibrationSignature;
        private uint _activeDirtyReasonFlags;
        private uint _regionalDirtyReasonFlags;
        private ulong _scheduledPrimaryRayCount;
        private ulong _scheduledTransportRayCount;
        private ulong _scheduledSourceRayCount;
        private int _sourceRefreshProbeCount;
        private int _sourceCacheReuseProbeCount;
        // Publication occurs after scene diagnostics are populated. Retain the
        // completed frame separately so captures never observe the just-reset
        // current-frame counters as a false zero-publication result.
        private int _transportPublishedProbeCount;
        private int _transportPublishRegionCount;
        private int _currentTransportPublishedProbeCount;
        private int _currentTransportPublishRegionCount;
        private int _receiverRecordsPublishedCount;
        private int _currentReceiverRecordsPublishedCount;
        private ulong _transportPublishedProbeTotal;
        private ulong _updateTransactionAbortCount;
        private ulong _sourceCacheInvalidationCount;
        // Fence-safe prior-frame counts. MarkBlendExecuted runs after scene
        // diagnostics are populated, so completion counters use the same
        // current/published handoff as transport publication telemetry.
        private int _sourceRefreshTransportInvalidationCount;
        private int _currentSourceRefreshTransportInvalidationCount;
        private int _completedSourceRefreshProbeCount;
        private int _currentCompletedSourceRefreshProbeCount;
        private uint _sourceLightingGeneration = 1u;
        // Global witness for changes to the set of per-probe source epochs.
        // This is intentionally distinct from both the lighting generation and
        // each probe's cache identity; frozen audits use it only to detect that
        // their epoch set changed while the GPU compares cache entries against
        // the owning probe's exact epoch.
        private uint _sourceEpochGeneration = 1u;
        private bool _sourceCohortTransitionActive;
        private uint _sourceCohortTransitionStartFrame;
        private ulong _sourceCohortTransitionCount;
        private ulong _sourceCohortCompletionCount;
        private uint _sourceCohortCompletedFrame;
        private int _sourceCohortQuietFrames = SourceCohortQuietFrameCount;
        private int _sourceRefreshTargetProbeCount;
        private int _sourceRefreshCapacityShortfall;
        private ulong _sourceRefreshTargetRayCount;
        private ulong _sourceRefreshRayCapacityShortfall;
        private float _sourceRefreshMinimumSweepSeconds;
        private int _sourceStepStaleProbeCount;
        private int _sourceStepAgeP95Frames;
        private int _sourceStepAgeMaximumFrames;
        private float _sourceSweepFramesPerSecond =
            GiSourceSweepNominalFramesPerSecond;
        private long _sourceSweepLastTimestamp;
        private uint _transportGeneration;
        // These are publication-boundary generations, not aliases for the
        // currently requested generations. A source generation advances as
        // soon as a new lighting signature is accepted by the DDGI scheduler;
        // the admitted value catches up only after the source cohort has no
        // stale participants. Propagation can likewise advance while a global
        // solve is pending, so its published value is latched at convergence.
        private uint _admittedSourceCohortGeneration;
        private uint _publishedPropagationGeneration;
        private ulong _staleReadbackRejectionCount;
        private ulong _resourceGenerationRejectionCount;
        // A local residual alone cannot prove that multi-bounce transport has
        // reached a probe whose neighbors are still warming. Global source
        // changes therefore retain every active probe until the whole field is
        // source-ready and simultaneously satisfies minimum-generation and
        // consecutive residual-stability evidence.
        private bool _transportGlobalConvergencePending = true;
        private bool _transportFieldConvergenceEvidenceResetPending = true;
        private bool _transportGlobalSourceRepairPhasePending = true;
        private uint _transportGlobalConvergenceSourceGeneration = 1u;
        private uint _transportGlobalConvergenceStartFrame;
        private bool _transportPeriodicSourceRefreshWavePending;
        private uint _transportPeriodicSourceRefreshWaveCutoffFrame;
        private uint _transportNextPeriodicSourceRefreshFrame;
        private bool _transportGlobalWatchdogRefreshWaveStarted;
        private ulong _transportCalibrationChangeCount;
        private bool _transportV2WasActive;
        // V2 retirement authority. The legacy per-probe EMA fields below are
        // retained for V1 compatibility and diagnostics, but cannot authorize a
        // certified V2 publication when tail certification is enabled.
        private readonly SimpleDdgiTransportSolveController _transportSolveController =
            new(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
        private SimpleDdgiTransportTailSummary _transportTailSummary =
            SimpleDdgiTransportTailSummary.Empty;
        private int _transportAuditProbeCursor;
        private uint _transportAuditChunkCount;
        private ulong _transportAuditFirstFrameSerial;
        private SimpleDdgiTransportGenerations _transportAuditGenerations;
        private int _transportAuditExpectedParticipantCount;
        private int _transportAuditExpectedTexelCount;
        private uint _transportAuditWitnessProbeIndex = uint.MaxValue;
        private uint _transportAuditWitnessTexelIndex = uint.MaxValue;
        private const int TransportAuditReadbackMarginFrames = 2;
        private ulong _transportAuditFinalSubmissionFrameSerial;
        // A complete resident visit reduction is delayed by frames in flight.
        // Quiesce new solve work and wait for one fence-complete epoch-zero
        // feedback packet before freezing canonical generations for audit.
        private bool _transportSolveDrainPending;
        private ulong _transportSolveDrainStartFeedbackSerial;
        private ulong _transportAuditReadbackTimeoutCount;
        private ulong _transportParticipantReconciliationFeedbackSerial;
        private int _transportSourceNoProgressFeedbackPeriods;
        private ulong _transportSourceNoProgressRecoveryCount;
        private ulong _transportConvergenceDeadlineRecoveryCount;
        private uint _transportTailProgressObservedFrame = uint.MaxValue;
        private bool _hasTransportTailProgressStamp;
        private TransportTailProgressStamp _transportTailProgressStamp;
        private int _effectiveMaxShadedLights;
        private ulong _adaptiveRaySavedPrimaryRayCount;
        private int _rayBudgetRejectedProbeCount;
        private ulong _rayBudgetRejectedPrimaryRayCount;
        // V2 source replacement and cached transport solve have deliberately
        // different work envelopes. Source requests execute primary ray
        // queries; cached solve requests only evaluate the immutable cache.
        // Keeping both values explicit prevents cheap convergence work from
        // inheriting the much tighter ray-query limit.
        private int _schedulerSourceRequestBudget;
        private int _schedulerConfiguredRequestBudget;
        private int _schedulerEffectiveRequestBudget;
        private int _schedulerDeferredRequestCount;
        private SimpleDdgiSchedulerPressureReason _schedulerPressureReason;
        private int _schedulerFeedbackRequestBudgetCap;
        private ulong _schedulerLastCompletedGpuMicroseconds;
        private ulong _schedulerTargetGpuMicroseconds;
        private bool _schedulerDeterministicFixedBudget;
        private Vector3 _schedulerCameraPosition;
        private int _fullRayProbeUpdateCount;
        private int _maintenanceRayProbeUpdateCount;
        private long _lastUploadMicroseconds;
        private SimpleDdgiUploadTiming _lastUploadTiming;
        private bool _capacityKeyValid;
        private SimpleDdgiCapacityKey _capacityKey;
        private SimpleDdgiMemoryPlan _capacityPlan;
        private bool _uploadCapacityStableKeyHit;
        private long _uploadCapacityCpuProbeStateMicroseconds;
        private long _uploadCapacityPlanCreationMicroseconds;
        private long _uploadCapacityPredicateMicroseconds;
        private long _uploadCapacityBufferSizeLookupMicroseconds;
        private long _uploadCapacityDeviceIdleWaitMicroseconds;
        private long _uploadCapacityBufferTransitionMicroseconds;
        private long _uploadCapacityReadbackReconciliationMicroseconds;
        private long _uploadCapacitySampledAtlasBudgetMicroseconds;
        private long _uploadCapacitySampledAtlasEnsureMicroseconds;
        private long _uploadCapacityDescriptorRegistrationMicroseconds;
        private long _uploadCapacityRetiredResourceDestructionMicroseconds;
        private int _uploadCapacityBufferSizeLookupCount;
        private int _uploadCapacityTransitionCount;
        private int _uploadCapacityDeviceIdleWaitCount;
        private int _uploadCapacityDescriptorRegistrationCount;
        private int _uploadCapacityRetiredResourceDestructionCount;
        private SimpleDdgiCapacityTransitionReason _uploadCapacityTransitionReason;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityIrradiance;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityVisibility;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityTransportIrradiance;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityTransportSourceCache;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityRayScratch;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityProbeState;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityReceiverProbes;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityUpdateQueue;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityRelocationClassification;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacityReadback;
        private SimpleDdgiCapacityResourceTelemetry _uploadCapacitySampledAtlas;
        private int _uploadReadbackProbeCount;
        private int _uploadSchedulerEntryRefreshCount;
        private int _uploadSchedulerWakeEntryRefreshCount;
        private int _uploadSchedulerWakeBudgetSaturated;
        private int _uploadSchedulerFullRebuildCount;
        private int _uploadVisibilityEntryRefreshCount;
        private int _uploadStateDirtySlotCount;
        private int _uploadStateUploadRunCount;
        private readonly int[] _ringFullRayProbeUpdateCounts = new int[3];
        private readonly int[] _ringMaintenanceRayProbeUpdateCounts = new int[3];
        private readonly ulong[] _ringScheduledPrimaryRayCounts = new ulong[3];
        private string _lastBudgetWarning = string.Empty;
        // Trace, relocation/classification, and blend are one transaction.  Keeping
        // this state on the manager prevents a skipped ray-query trace from allowing
        // later passes to consume scratch data recorded by an older frame.
        private bool _updateTransactionPending;
        private bool _traceTransactionExecuted;
        private bool _relocateClassifyTransactionExecuted;
        private bool _transportTransactionExecuted;
        private bool _blendTransactionExecuted;
        private uint _updateTransactionSerial;
        private ulong _frameSerial;
        private SimpleDdgiLayoutReport? _lastLayoutReport;
        // Ring origins may move every frame, but allocation admission only depends
        // on topology and tier inputs. Cache the immutable report so normal camera
        // travel does not allocate request records, decision lists, or hash sets.
        private SimpleDdgiLayoutReport? _cachedLayoutReport;
        private HashSet<int>? _cachedAcceptedSourceOrdinals;
        private ulong _cachedLayoutFingerprint;
        // SceneDataBuilder's content revision covers the object visibility,
        // mesh bounds, and transforms consumed by SimpleDdgiSceneBounds. Cache
        // that O(scene) reduction while the revision is stable; camera motion
        // still rebuilds the small volume table from the cached world bounds.
        private Scene? _sceneBoundsScene;
        private BoundingBox _sceneBoundsSnapshot;
        private ulong _sceneBoundsSceneContentRevision;
        private bool _hasSceneBoundsSnapshot;
        private bool _disposed;
        private SimpleDdgiSchedulerMode _schedulerMode = SimpleDdgiSchedulerMode.CpuReference;
        private bool _gpuResidentProbeStateBootstrapped;
        private GPUSimpleDdgiSchedulerFeedback _lastGpuSchedulerFeedback;
        private bool _gpuSchedulerFeedbackValid;
        private GPUSimpleDdgiResidencyFeedback _lastProbeResidencyFeedback;
        private bool _probeResidencyFeedbackValid;
        private ulong _probeResidencyFeedbackFrameSerial;
        private ulong _probeResidencyFeedbackGenerationRejectionCount;
        private uint _probeResidencyGeometryGeneration = 1u;
        private uint _lastProbeResidencyDemandEpochResetFrame = uint.MaxValue;
        private bool _probePageManagementCadenceInitialized;
        private Matrix4x4 _probePageManagementViewProjection;
        private Vector3 _probePageManagementCameraPosition;
        private ulong _probePageManagementSceneRevision;
        private ulong _probePageManagementCameraCutSerial;
        private uint _lastProbePageFullManagementFrame;
        private bool _probeResidencyBootstrapClassificationActive;
        private bool _probeResidencyMutationUnavailable;
        private string _probeResidencyMutationFailureReason = string.Empty;
        private ulong _gpuSchedulerFeedbackFrameSerial;
        private ulong _gpuSchedulerFeedbackGenerationRejectionCount;
        private bool _gpuSchedulerFallbackLatched;
        private bool _gpuSchedulerFallbackFreshResetPending;
        private bool _gpuSchedulerFallbackExportRequested;
        private bool _gpuSchedulerFallbackExportSubmitted;
        private SimpleDdgiSchedulerStateExportTag _gpuSchedulerFallbackExportTag;
        private int _gpuSchedulerReentryStableFrameCount;
        private ulong _gpuSchedulerStateExportSuccessCount;
        private ulong _gpuSchedulerStateExportFailureCount;
        private ulong _gpuSchedulerReentryCount;
        private ulong _gpuSchedulerFallbackCount;
        private int _gpuSchedulerGenerationMismatchStreak;
        private string _gpuSchedulerFallbackReason = string.Empty;
        private bool _gpuSchedulerFrameExecutionAvailable = true;
        private bool _sampledAtlasGpuPublicationAvailable;

        public SimpleDdgiVolumeManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings,
            Action<RuntimeStallReason, string, Action> recordRuntimeStall,
            Func<ulong> waitForBindlessDescriptorReaders)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _recordRuntimeStall = recordRuntimeStall ??
                throw new ArgumentNullException(nameof(recordRuntimeStall));
            _waitForBindlessDescriptorReaders =
                waitForBindlessDescriptorReaders ??
                throw new ArgumentNullException(
                    nameof(waitForBindlessDescriptorReaders));
            _gpuScheduler = new SimpleDdgiGpuScheduler(_context, _bufferManager);
            _probePageCache = new SimpleDdgiProbePageCache(
                _context,
                _bufferManager);

            int schedulerQueueCount =
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount;
            _schedulerWorkQueues = new SimpleDdgiPersistentProbeQueues(
                schedulerQueueCount,
                SchedulerWorkClassCount);
            _schedulerSourceRefreshQueues = new SimpleDdgiPersistentProbeQueues(
                schedulerQueueCount,
                SchedulerWorkClassCount);
            _schedulerCachedSolverQueues = new SimpleDdgiPersistentProbeQueues(
                schedulerQueueCount,
                SchedulerWorkClassCount);

            _paramsBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinBufferSize, ParamsBufferSize),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Simple DDGI Params");
            EnsureCapacity(0, 1, 1);
        }

        public int VolumeCount => _volumeCount;
        public int ProbeCount => _probeCount;
        public int ProbeCountX => _probeCountX;
        public int ProbeCountY => _probeCountY;
        public int ProbeCountZ => _probeCountZ;
        public int RaysPerProbe => _raysPerProbe;
        public int UpdateStartProbe => _updateStartProbe;
        public int ProbesToUpdate => _probesToUpdate;
        public SimpleDdgiSchedulerMode SchedulerMode => _schedulerMode;
        public ulong FrameSerial => _frameSerial;
        public uint FrameIndex => _frameIndex;
        public SimpleDdgiGpuScheduler GpuScheduler => _gpuScheduler;
        public SimpleDdgiProbePageCache ProbePageCache => _probePageCache;

        /// <summary>
        /// Selects the full sparse-page transaction for the current submitted
        /// frame. During sparse bootstrap every frame remains eager. Afterwards,
        /// exact camera/scene/cut changes wake it immediately and a bounded
        /// periodic audit prevents silent long-term drift. A transport source
        /// refresh can temporarily invalidate the tail certificate without
        /// invalidating page ownership, so it must not reopen full page
        /// management by itself.
        /// </summary>
        public bool PrepareProbePageManagement(
            Vector3 cameraPosition,
            Matrix4x4 viewProjection,
            ulong sceneContentRevision,
            ulong cameraCutSerial)
        {
            if (_probeResidencyBootstrapClassificationActive &&
                ShouldCompleteProbeResidencyBootstrapClassification(
                    _settings.GlobalIllumination
                        .SimpleDdgiTransportTailCertificationEnabled &&
                        TransportV2Active,
                    HasCurrentTransportTailCertificate,
                    _probeResidencyFeedbackValid,
                    _lastProbeResidencyFeedback.PublishedPageCount,
                    _lastProbeResidencyFeedback.InitializingPageCount,
                    _lastProbeResidencyFeedback.AdmissionCount))
            {
                _probeResidencyBootstrapClassificationActive = false;
            }

            bool viewChanged = !_probePageManagementCadenceInitialized ||
                cameraPosition != _probePageManagementCameraPosition ||
                !viewProjection.Equals(_probePageManagementViewProjection);
            bool sceneChanged = !_probePageManagementCadenceInitialized ||
                sceneContentRevision != _probePageManagementSceneRevision;
            bool cameraCut = !_probePageManagementCadenceInitialized ||
                cameraCutSerial != _probePageManagementCameraCutSerial;
            uint age = unchecked(_frameIndex - _lastProbePageFullManagementFrame);
            bool fullManagement = ShouldRunFullProbePageManagement(
                _probeResidencyBootstrapClassificationActive,
                _probeResidencyFeedbackValid,
                viewChanged,
                sceneChanged,
                cameraCut,
                age,
                CertifiedMaintenancePulseFrames);
            if (fullManagement)
            {
                _probePageManagementCadenceInitialized = true;
                _probePageManagementCameraPosition = cameraPosition;
                _probePageManagementViewProjection = viewProjection;
                _probePageManagementSceneRevision = sceneContentRevision;
                _probePageManagementCameraCutSerial = cameraCutSerial;
                _lastProbePageFullManagementFrame = _frameIndex;
            }
            return fullManagement;
        }

        internal static bool ShouldRunFullProbePageManagement(
            bool bootstrapClassificationActive,
            bool residencyFeedbackValid,
            bool viewChanged,
            bool sceneChanged,
            bool cameraCut,
            uint framesSinceFullManagement,
            uint auditIntervalFrames)
        {
            uint interval = Math.Max(auditIntervalFrames, 1u);
            return bootstrapClassificationActive ||
                !residencyFeedbackValid ||
                viewChanged ||
                sceneChanged ||
                cameraCut ||
                framesSinceFullManagement >= interval;
        }

        internal static bool ShouldCompleteProbeResidencyBootstrapClassification(
            bool tailCertificationRequested,
            bool certificateCurrent,
            bool residencyFeedbackValid,
            uint publishedPageCount,
            uint initializingPageCount,
            uint admissionCount)
        {
            if (tailCertificationRequested)
                return certificateCurrent;

            // Configurations without tail certification still need a bounded
            // end to their cold-start classification. A fence-complete quiet
            // summary with at least one coherent page is the strongest signal
            // available without enumerating page state on the CPU.
            return residencyFeedbackValid &&
                publishedPageCount != 0u &&
                initializingPageCount == 0u &&
                admissionCount == 0u;
        }
        public SimpleDdgiProbeResidencyMode ProbeResidencyMode =>
            _capacityPlan.ResidencyMode.Sanitize();
        public uint ProbeResidencyResourceGeneration =>
            _probePageCache.ResourceGeneration;
        public ulong ProbeResidencyArenaBytes => _probePageCache.ArenaBytes;
        public ulong ProbeResidencyFeedbackReadbackBytes =>
            _probePageCache.FeedbackReadbackBytes;
        public ulong ProbeResidencyRetiredBytes => _probePageCache.RetiredBytes;
        public bool ProbeResidencyMutationFrozen => _probePageCache.Frozen;
        public bool ProbeResidencyDevelopmentMutationFrozen =>
            _probePageCache.DevelopmentFrozen;
        public bool ProbeResidencyStateValid => _probePageCache.ResidencyValid;
        public string ProbeResidencyFailureReason => _probePageCache.FailureReason;
        public ulong ProbeResidencyDevelopmentControlCommandCount =>
            _probePageCache.DevelopmentControlCommandCount;
        public int ProbeResidencyLastDevelopmentControlledVirtualPage =>
            _probePageCache.LastDevelopmentControlledVirtualPage;
        public bool ProbeResidencyLastDevelopmentPinState =>
            _probePageCache.LastDevelopmentPinState;
        public GPUSimpleDdgiResidencyFeedback LastProbeResidencyFeedback =>
            _lastProbeResidencyFeedback;
        public bool ProbeResidencyFeedbackValid =>
            _capacityPlan.ResidencyMode.CollectsDemand() &&
            _probeResidencyFeedbackValid;
        public bool ProbeResidencyBootstrapClassificationActive =>
            _capacityPlan.ResidencyMode.UsesSparsePayloads() &&
            _probeResidencyBootstrapClassificationActive;
        public ulong ProbeResidencyFeedbackFrameSerial =>
            _probeResidencyFeedbackFrameSerial;
        public ulong ProbeResidencyFeedbackGenerationRejectionCount =>
            _probeResidencyFeedbackGenerationRejectionCount;
        public uint ProbeResidencyGeometryGeneration =>
            _probeResidencyGeometryGeneration;
        public int ProbeResidencyConfiguredPhysicalPageBudget =>
            _settings.GlobalIllumination.SimpleDdgiSparsePhysicalPageBudget;
        public int ProbeResidencyConfiguredMinimumPhysicalPageBudget =>
            _settings.GlobalIllumination.SimpleDdgiSparseMinimumPhysicalPageBudget;
        public int ProbeResidencyRetentionFrames =>
            _settings.GlobalIllumination.SimpleDdgiSparseRetentionFrames;
        public int ProbeResidencyMaximumAdmissionsPerFrame =>
            _settings.GlobalIllumination.SimpleDdgiSparseMaximumAdmissionsPerFrame;
        public int ProbeResidencyVisiblePublicationProbeBudget =>
            _capacityPlan.ResidencyMode.UsesSparsePayloads()
                ? Math.Max(0, _schedulerSourceRequestBudget)
                : 0;
        public int ProbeResidencyMaximumReceiverFeedbackRequests =>
            _settings.GlobalIllumination.SimpleDdgiSparseMaximumReceiverFeedbackRequests;
        public int ProbeResidencyInactiveRetryFrames =>
            _settings.GlobalIllumination.SimpleDdgiSparseInactiveRetryFrames;
        public BufferHandle GpuSchedulerArenaBuffer => _gpuScheduler.ArenaBuffer;
        public ulong GpuSchedulerArenaBytes => _gpuScheduler.ArenaBytes;
        public ulong GpuSchedulerFeedbackReadbackBytes => _gpuScheduler.FeedbackReadbackBytes;
        public ulong GpuSchedulerAuditReadbackBytes => _gpuScheduler.AuditReadbackBytes;
        public ulong GpuSchedulerRetiredBytes => _gpuScheduler.RetiredBytes;
        public GPUSimpleDdgiSchedulerFeedback LastGpuSchedulerFeedback => _lastGpuSchedulerFeedback;
        public bool GpuSchedulerFeedbackValid => _gpuSchedulerFeedbackValid;
        public ulong GpuSchedulerFeedbackFrameSerial => _gpuSchedulerFeedbackFrameSerial;
        public ulong GpuSchedulerFeedbackGenerationRejectionCount =>
            _gpuSchedulerFeedbackGenerationRejectionCount;
        public bool GpuSchedulerFallbackLatched => _gpuSchedulerFallbackLatched;
        public bool GpuSchedulerFallbackFreshResetPending =>
            _gpuSchedulerFallbackFreshResetPending;
        public ulong GpuSchedulerFallbackCount => _gpuSchedulerFallbackCount;
        public string GpuSchedulerFallbackReason => _gpuSchedulerFallbackReason;
        public bool GpuSchedulerFallbackExportPending =>
            _gpuSchedulerFallbackExportRequested ||
            _gpuSchedulerFallbackExportSubmitted;
        public int GpuSchedulerReentryStableFrameCount =>
            _gpuSchedulerReentryStableFrameCount;
        public ulong GpuSchedulerStateExportSuccessCount =>
            _gpuSchedulerStateExportSuccessCount;
        public ulong GpuSchedulerStateExportFailureCount =>
            _gpuSchedulerStateExportFailureCount;
        public ulong GpuSchedulerReentryCount => _gpuSchedulerReentryCount;
        public bool GpuSchedulerFrameExecutionAvailable =>
            _gpuSchedulerFrameExecutionAvailable;
        public ReadOnlySpan<SimpleDdgiRayDispatchBatch> RayDispatchBatches =>
            new(_rayDispatchBatches, 0, _rayDispatchBatchCount);
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public SimpleDdgiUploadTiming LastUploadTiming => _lastUploadTiming;
        /// <summary>CPU time for the post-blend incremental sampled-atlas mirror.</summary>
        public long LastSampledAtlasSynchronizationMicroseconds => _lastSampledAtlasSynchronizationMicroseconds;
        public ulong BufferBytes => ParamsBufferSize +
            _irradianceAtlasBytes +
            _transportIrradianceAtlasBytes +
            _transportSourceCacheBytes +
            _visibilityAtlasBytes +
            _rayScratchBytes +
            _probeStateBytes +
            _receiverProbeBytes +
            _probeUpdateQueueBytes +
            _relocationClassificationBytes +
            _probeStateReadbackBufferBytes +
            _gpuScheduler.ArenaBytes +
            _gpuScheduler.FeedbackReadbackBytes +
            _gpuScheduler.AuditReadbackBytes +
            _gpuScheduler.FallbackStateExportBytes +
            _probePageCache.ArenaBytes +
            _probePageCache.FeedbackReadbackBytes;
        public ulong IrradianceAtlasBytes => _irradianceAtlasBytes;
        /// <summary>Private V2 Jacobi target; never sampled by receivers.</summary>
        public ulong TransportIrradianceAtlasBytes => _transportIrradianceAtlasBytes;
        /// <summary>
        /// Offset, in uint words, of the resident scheduler's private
        /// visibility target packed after the private irradiance target.
        /// </summary>
        public uint GpuSchedulerPrivateVisibilityOffsetWords =>
            _schedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? checked((uint)((ulong)Math.Max(
                    0,
                    _capacityPlan.PhysicalProbeCapacity) *
                    SimpleDdgiMemoryPlan.IrradianceBytesPerProbe / sizeof(uint)))
                : 0u;
        /// <summary>Persistent V2 direct/sky/emissive source cache allocation.</summary>
        public ulong TransportSourceCacheBytes => _transportSourceCacheBytes;
        public ulong VisibilityAtlasBytes => _visibilityAtlasBytes;
        /// <summary>Canonical SSBO atlas allocation only.</summary>
        public ulong AtlasBufferBytes => _irradianceAtlasBytes + _visibilityAtlasBytes;
        /// <summary>Optional sampled-image mirror allocation.</summary>
        public ulong SampledAtlasImageBytes => _sampledAtlas?.EstimatedImageBytes ?? 0UL;
        public ulong SampledAtlasAllocatedImageBytes =>
            _sampledAtlas?.AllocatedImageBytes ?? 0UL;
        public int SampledAtlasGroupCount => _sampledAtlas?.GroupCount ?? 0;
        public int SampledAtlasLayersPerTexture => _sampledAtlas?.LayersPerTexture ?? 0;
        public ulong SampledAtlasAllocationGeneration => _sampledAtlasPublicationGeneration;
        /// <summary>Total atlas allocation across SSBO writer and optional images.</summary>
        public ulong AtlasBytes => checked(AtlasBufferBytes + SampledAtlasImageBytes);
        public ulong RayScratchBytes => _rayScratchBytes;
        public ulong ProbeStateBytes => _probeStateBytes;
        public ulong ReceiverProbeBytes => _receiverProbeBytes;
        /// <summary>
        /// Number of compact receiver records provisioned by the current
        /// allocation. This includes the single fail-closed placeholder used
        /// while Simple DDGI is inactive.
        /// </summary>
        public int ReceiverProbeCapacity => checked((int)(
            _receiverProbeBytes / SimpleDdgiMemoryPlan.ReceiverProbeBytesPerProbe));
        public ulong ReceiverProbeInvalidationBytesThisFrame =>
            _receiverProbeInvalidationBytesThisFrame;
        public int ReceiverProbeInvalidationRunCountThisFrame =>
            _receiverProbeInvalidationRunCountThisFrame;
        public bool ReceiverProbeFullClearThisFrame =>
            _receiverProbeFullClearThisFrame;
        public ulong ProbeUpdateQueueBytes => _probeUpdateQueueBytes;
        public SimpleDdgiStoragePackingMode StoragePackingMode =>
            _storageLayout.PackingMode;
        public ulong RelocationClassificationBytes => _relocationClassificationBytes;
        public ulong ProbeStateReadbackBytes => _probeStateReadbackBufferBytes;
        // Compatibility names retained by the public diagnostics schema. They
        // now cover every fence-retired DDGI resource, including compact-mirror
        // images, so ownership audits cannot miss image-generation backlog.
        public int RetiredBufferCount => checked(
            _bufferRetirement.ActiveCount +
            (_sampledAtlas?.RetiredImageCount ?? 0));
        public ulong RetiredBufferBytes => SaturatingAdd(
            _bufferRetirement.ActiveBytes,
            _sampledAtlas?.RetiredImageBytes ?? 0UL);
        public bool CapacityTransitionDeferred => _capacityTransitionDeferred;
        public string CapacityTransitionDeferredReason =>
            _capacityTransitionDeferredReason;
        /// <summary>All non-atlas buffer allocation; never subtract image bytes from buffers.</summary>
        public ulong NonAtlasBufferBytes => BufferBytes - AtlasBufferBytes;
        public bool SampledAtlasRequested => _settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled;
        public bool SampledAtlasActive => SampledAtlasRequested &&
            _registeredBindlessHeap != null &&
            _sampledAtlas?.IsReady == true &&
            string.IsNullOrEmpty(_sampledAtlasFallbackReason);
        public bool SampledAtlasGpuPublicationRequired =>
            SampledAtlasActive && _sampledAtlasGpuPublicationAvailable;
        public string SampledAtlasFallbackReason => SampledAtlasActive
            ? string.Empty
            : _sampledAtlasFallbackReason;

        public SimpleDdgiStorageDiagnostics CreateStorageDiagnostics()
        {
            int fp16DistanceVolumes = 0;
            int fp16DistanceProbes = 0;
            int fp32DistanceVolumes = 0;
            int fp32DistanceProbes = 0;
            foreach (SimpleDdgiTransportCacheRegion region in _storageLayout.Regions)
            {
                if (region.Format == SimpleDdgiTransportCacheFormat.Compact24)
                {
                    fp16DistanceVolumes++;
                    fp16DistanceProbes = checked(
                        fp16DistanceProbes + region.PhysicalProbeCount);
                }
                else if (region.Format == SimpleDdgiTransportCacheFormat.Compact28)
                {
                    fp32DistanceVolumes++;
                    fp32DistanceProbes = checked(
                        fp32DistanceProbes + region.PhysicalProbeCount);
                }
            }

            string mirrorFallback = !string.IsNullOrEmpty(_sampledAtlasFallbackReason)
                ? _sampledAtlasFallbackReason
                : _sampledAtlasLayout.FallbackReason;
            return new SimpleDdgiStorageDiagnostics(
                IsAvailable: _volumeCount > 0,
                PackingMode: _storageLayout.PackingMode,
                AbiVersion: _storageLayout.AbiVersion,
                DirectionCodebookVersion: _storageLayout.DirectionCodebookVersion,
                CanonicalIrradianceFormat: "RGBA16F",
                CanonicalVisibilityFormat: "RG16F",
                CanonicalIrradianceBytes: _irradianceAtlasBytes,
                CanonicalVisibilityBytes: _visibilityAtlasBytes,
                SourceCacheBytes: _transportSourceCacheBytes,
                SourceCacheLegacyBytes:
                    _capacityPlan.TransportSourceCacheLegacyBytes,
                SourceCacheCompact28Bytes:
                    _capacityPlan.TransportSourceCacheCompact28Bytes,
                SourceCacheCompact24Bytes:
                    _capacityPlan.TransportSourceCacheCompact24Bytes,
                SourceCacheAlignmentBytes:
                    _capacityPlan.TransportSourceCacheAlignmentBytes,
                SourceCacheLegacyRayCount:
                    _capacityPlan.TransportSourceCacheLegacyRayCount,
                SourceCacheCompact28RayCount:
                    _capacityPlan.TransportSourceCacheCompact28RayCount,
                SourceCacheCompact24RayCount:
                    _capacityPlan.TransportSourceCacheCompact24RayCount,
                Fp16DistanceEligibleVolumeCount: fp16DistanceVolumes,
                Fp16DistanceEligibleProbeCount: fp16DistanceProbes,
                Fp32DistanceVolumeCount: fp32DistanceVolumes,
                Fp32DistanceProbeCount: fp32DistanceProbes,
                RayScratchStrideBytes: _capacityPlan.RayResultStrideBytes,
                RayScratchBytes: _rayScratchBytes,
                MirrorCoverageMode: _sampledAtlasLayout.CoverageMode,
                MirrorRequestedProbeCount: _sampledAtlasLayout.RequestedProbeCount,
                MirrorEligibleProbeCount: _sampledAtlasLayout.EligibleProbeCount,
                MirrorAdmittedProbeCount: _sampledAtlasLayout.AdmittedProbeCount,
                MirrorProvisionedProbeCount: _sampledAtlasLayout.ProvisionedProbeCount,
                MirrorIrradianceBytes: _sampledAtlasLayout.IrradianceImageBytes,
                MirrorVisibilityBytes: _sampledAtlasLayout.VisibilityImageBytes,
                MirrorTotalBytes: _sampledAtlasLayout.TotalImageBytes,
                MirrorAllocatedBytes: SampledAtlasAllocatedImageBytes,
                MirrorExcludedIdentities: _sampledAtlasLayout.ExcludedIdentities,
                CacheRegions: _storageLayout.Regions,
                StorageLayoutFingerprint: _storageLayout.Fingerprint,
                MirrorLayoutFingerprint: _sampledAtlasLayout.Fingerprint,
                MirrorAllocationGeneration: SampledAtlasAllocationGeneration,
                MirrorFallbackReason: mirrorFallback);
        }

        /// <summary>
        /// The publish pass supplies the device-dependent image-pipeline result.
        /// The scheduler records sampled publication as required only when that
        /// producer is actually available; otherwise the outcome uses the
        /// explicit SampledPublishNotRequired bit.
        /// </summary>
        public void SetSampledAtlasGpuPublicationAvailable(bool available) =>
            _sampledAtlasGpuPublicationAvailable = available;
        // These handles are intentionally exposed as allocation-level contracts for render-graph
        // queue ownership tracking. Bindless indices identify descriptors, not Vulkan resources,
        // and therefore cannot safely stand in for a queue-family transfer target.
        public BufferHandle ParamsBuffer => _paramsBuffer;
        public BufferHandle IrradianceAtlasBuffer => _irradianceAtlasBuffer;
        public BufferHandle TransportIrradianceAtlasBuffer => _transportIrradianceAtlasBuffer;
        public BufferHandle TransportSourceCacheBuffer => _transportSourceCacheBuffer;
        public BufferHandle VisibilityAtlasBuffer => _visibilityAtlasBuffer;
        public BufferHandle RayResultScratchBuffer => _rayResultScratchBuffer;
        public BufferHandle ProbeStateBuffer => _probeStateBuffer;
        public BufferHandle ReceiverProbeBuffer => _receiverProbeBuffer;
        public BufferHandle ProbeUpdateQueueBuffer => _probeUpdateQueueBuffer;
        public Silk.NET.Vulkan.Buffer GetProbeUpdateQueueVkBuffer() =>
            _bufferManager.GetBuffer(_probeUpdateQueueBuffer);
        public BufferHandle RelocationClassificationBuffer => _relocationClassificationBuffer;

        public BufferHandle GetProbeStateReadbackBuffer(int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            return _probeStateReadbackBuffers[frameIndex];
        }
        public bool AtlasFresh => _atlasFresh;
        public bool RecenteredThisFrame => _recenteredThisFrame;
        public bool AtlasPreservedOnRecenterThisFrame => _atlasPreservedOnRecenterThisFrame;
        public bool AtlasClearedThisFrame => _atlasClearedThisFrame;
        public int TotalRecenterCount => _totalRecenterCount;
        public int TotalAtlasClearCount => _totalAtlasClearCount;
        public int TotalAtlasPreserveOnRecenterCount => _totalAtlasPreserveOnRecenterCount;
        public int FramesSinceLastClear => _framesSinceLastClear == int.MaxValue ? -1 : _framesSinceLastClear;
        public int FramesSinceLastRecenter => _framesSinceLastRecenter == int.MaxValue ? -1 : _framesSinceLastRecenter;
        public int FullRefreshFrameCount => _fullRefreshFrameCount;
        public int PartialRefreshFrameCount => _partialRefreshFrameCount;
        public int NewlyInvalidatedProbeCount => _newlyInvalidatedProbeCount;
        public int RecenterRefreshProbeCount => _recenterRefreshProbeCount;
        public int DirtyRefreshProbeCount => _dirtyRefreshProbeCount;
        public int AgeRefreshProbeCount => _ageRefreshProbeCount;
        public int FullRefreshProbeCount => _fullRefreshProbeCount;
        public int ScrollCopyCount => _scrollCopyCount;
        public int ActiveProbeCount => _probeStateReadbackValid != 0 ? _activeProbeCount : _probeCount;
        public int InactiveProbeCount => _probeStateReadbackValid != 0 ? Math.Max(0, _probeCount - _activeProbeCount) : 0;
        public int InactiveProbeSkipCount => _inactiveProbeSkipCount;
        public ulong InactiveProbeSavedPrimaryRayCount => _inactiveProbeSavedPrimaryRayCount;
        public int LightingDirtyFrames => _lightingDirtyFrames;
        public int LightingDirtyBoostedCapacity => _lightingDirtyBoostedCapacity;
        public uint DirtyReasonFlags => (_lightingDirtyFrames > 0 ? _activeDirtyReasonFlags : 0u) | _regionalDirtyReasonFlags;
        public ulong ScheduledPrimaryRayCount => _scheduledPrimaryRayCount;
        /// <summary>All scheduled rays whose cached source is evaluated by V2 transport.</summary>
        public ulong ScheduledTransportRayCount => _scheduledTransportRayCount;
        /// <summary>Scheduled rays that actually entered the primary ray-query source path.</summary>
        public ulong ScheduledSourceRayCount => _scheduledSourceRayCount;
        public int SourceRefreshProbeCount => _sourceRefreshProbeCount;
        private int AuthoredTransportSourceSweepFrames
        {
            get
            {
                return UsesSteppedAtmosphereSourcePolicy
                    ? ResolveGiTargetSourceSweepFrames(
                        _settings.Environment.GiTargetSourceSweepSeconds,
                        ResolveSourceSweepFramesPerSecond(
                            _schedulerDeterministicFixedBudget,
                            _sourceSweepFramesPerSecond))
                    : _settings.GlobalIllumination.SimpleDdgiTransportSourceRefreshFrames;
            }
        }

        public int EffectiveTransportSourceRefreshFrames
        {
            get
            {
                int authoredTarget = AuthoredTransportSourceSweepFrames;
                int sourceBudget = _schedulerSourceRequestBudget > 0
                    ? _schedulerSourceRequestBudget
                    : _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame;
                // The authored atmospheric cadence is a target, not permission
                // to start another cohort before the current field has had one
                // complete source/solve/audit opportunity. This also applies
                // the live V2 ray-query cap instead of the larger legacy probe
                // update setting when sizing that opportunity.
                return ResolveEffectiveTransportSourceRefreshFrames(
                    authoredTarget,
                    ResolveTransportRefreshParticipantCount(),
                    sourceBudget,
                    _probeCount,
                    TransportAuditProbeChunkSize);
            }
        }

        private int TransportSourceSweepOpportunityFrames
        {
            get
            {
                int sourceBudget = _schedulerSourceRequestBudget > 0
                    ? _schedulerSourceRequestBudget
                    : _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame;
                return ResolveTransportSourceSweepFrames(
                    AuthoredTransportSourceSweepFrames,
                    ResolveTransportRefreshParticipantCount(),
                    sourceBudget,
                    _probeCount);
            }
        }
        public int SourceRefreshTargetProbeCount => _sourceRefreshTargetProbeCount;
        public int SourceRefreshCapacityShortfall => _sourceRefreshCapacityShortfall;
        /// <summary>Exact mixed-tier source-ray target, not a probe-count proxy.</summary>
        public ulong SourceRefreshTargetRayCount => _sourceRefreshTargetRayCount;
        public ulong SourceRefreshRayCapacityShortfall => _sourceRefreshRayCapacityShortfall;
        public float SourceRefreshMinimumAchievableSweepSeconds =>
            _sourceRefreshMinimumSweepSeconds;
        public SimpleDdgiTrackingState TrackingState => ResolveTrackingState();
        public bool SourceCohortTransitionActive =>
            TransportV2Active && _sourceCohortTransitionActive;
        public ulong SourceCohortTransitionCount => _sourceCohortTransitionCount;
        public int SourceCohortTransitionElapsedFrames =>
            !SourceCohortTransitionActive
                ? 0
                : ClampUIntToInt(unchecked(
                    _frameIndex - _sourceCohortTransitionStartFrame));
        public int SourceStepStaleProbeCount => _sourceStepStaleProbeCount;
        public int SourceStepAgeP95Frames => _sourceStepAgeP95Frames;
        public int SourceStepAgeMaximumFrames => _sourceStepAgeMaximumFrames;
        public ulong SourceCohortCompletionCount => _sourceCohortCompletionCount;
        public uint SourceCohortCompletedFrame => _sourceCohortCompletedFrame;
        public int SourceCohortQuietFrames => _sourceCohortQuietFrames;
        public float SourceStepAgeP95Seconds =>
            _sourceStepAgeP95Frames / MathF.Max(_sourceSweepFramesPerSecond, 1.0f);
        public float SourceStepAgeMaximumSeconds =>
            _sourceStepAgeMaximumFrames / MathF.Max(_sourceSweepFramesPerSecond, 1.0f);
        private bool UsesSteppedAtmosphereSourcePolicy =>
            _settings.Environment.Enabled &&
            _settings.Environment.SourceKind == EnvironmentSourceKind.ProceduralSky;
        public int SourceCacheReuseProbeCount => _sourceCacheReuseProbeCount;
        public int TransportPublishedProbeCount => _transportPublishedProbeCount;
        /// <summary>
        /// Fence-complete compact receiver publications. CPU/GPU-mirror modes
        /// expose the preceding recorded transaction; resident mode uses the
        /// scheduler's delayed exact commit witness.
        /// </summary>
        public int ReceiverRecordsPublishedCount =>
            _receiverRecordsPublishedCount;
        public int TransportPublishRegionCount => _transportPublishRegionCount;
        public ulong TransportPublishedProbeTotal => _transportPublishedProbeTotal;
        public ulong TransportPublishRegionTotal => 0UL;
        public ulong UpdateTransactionAbortCount => _updateTransactionAbortCount;
        public ulong SourceCacheInvalidationCount => _sourceCacheInvalidationCount;
        public int SourceRefreshTransportInvalidationCount =>
            _sourceRefreshTransportInvalidationCount;
        public float SourceRefreshTransportInvalidationsPerRefresh =>
            _completedSourceRefreshProbeCount > 0
                ? _sourceRefreshTransportInvalidationCount /
                    (float)_completedSourceRefreshProbeCount
                : 0.0f;
        public uint SourceLightingGeneration => _sourceLightingGeneration;
        public uint AdmittedSourceCohortGeneration => _admittedSourceCohortGeneration;
        public uint TransportGeneration => _transportGeneration;
        public uint PublishedPropagationGeneration => _publishedPropagationGeneration;
        public uint VolumeTableGeneration => _volumeTableGeneration;
        public uint PhysicalOwnershipGeneration => _physicalOwnershipGeneration;
        public ulong StaleReadbackRejectionCount => _staleReadbackRejectionCount;
        public ulong ResourceGenerationRejectionCount => _resourceGenerationRejectionCount;

        /// <summary>
        /// Returns the scheduler's atmosphere contract without reading probe state back from the
        /// GPU. The method is intentionally a value snapshot so callers cannot retain mutable
        /// manager state across an admission transaction.
        /// </summary>
        public SimpleDdgiAtmosphereCohortFeedback CreateAtmosphereCohortFeedbackSnapshot()
        {
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                return CreateGpuResidentAtmosphereCohortFeedbackSnapshot();

            int participants = TransportV2Active ? _schedulerParticipatingProbeCount : 0;
            int stale = TransportV2Active ? _schedulerSourceRepairProbeCount : 0;
            int visibleParticipants = TransportV2Active
                ? _schedulerAtmosphereVisibleParticipatingProbeCount
                : 0;
            int visibleReady = TransportV2Active
                ? _schedulerAtmosphereVisibleSourceReadyProbeCount
                : 0;
            int visiblePublished = TransportV2Active
                ? _schedulerAtmosphereVisiblePublishedProbeCount
                : 0;
            bool visibleComplete = visibleReady >= visibleParticipants &&
                                   visiblePublished >= visibleParticipants;
            bool propagationComplete = !TransportGlobalConvergencePending &&
                                       _schedulerPendingConvergenceProbeCount == 0;
            bool quietComplete = _sourceCohortQuietFrames >= SourceCohortQuietFrameCount;
            bool staticConverged = propagationComplete &&
                                   quietComplete &&
                                   _sourceStepStaleProbeCount == 0 &&
                                   _admittedSourceCohortGeneration == _sourceLightingGeneration;
            int admittedSourceProbeCount = Math.Max(
                0,
                _sourceRefreshTargetProbeCount - _sourceRefreshCapacityShortfall);
            return new SimpleDdgiAtmosphereCohortFeedback(
                _volumeTableGeneration,
                _sourceLightingGeneration,
                _admittedSourceCohortGeneration,
                _transportGeneration,
                _publishedPropagationGeneration,
                participants,
                stale,
                visibleParticipants,
                visibleReady,
                visiblePublished,
                SourceCohortTransitionActive,
                visibleComplete,
                propagationComplete,
                quietComplete,
                _sourceRefreshMinimumSweepSeconds,
                _sourceCohortTransitionStartFrame,
                _sourceCohortCompletedFrame,
                _sourceCohortTransitionCount,
                _sourceCohortCompletionCount,
                _sourceRefreshTargetProbeCount,
                admittedSourceProbeCount,
                _sourceRefreshProbeCount,
                _sourceRefreshTargetRayCount,
                AdmittedSourceRayCount: (ulong)Math.Max(
                    _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget,
                    0),
                _scheduledSourceRayCount,
                _sourceRefreshCapacityShortfall,
                _sourceRefreshRayCapacityShortfall,
                staticConverged ? _sourceLightingGeneration : 0U,
                TransportGlobalConvergencePending,
                _staleReadbackRejectionCount,
                _resourceGenerationRejectionCount);
        }

        /// <summary>
        /// Accepts one fence-complete GPU summary. The summary is deliberately
        /// validated against the manager's current volume/source/transport
        /// generations before it can affect any CPU-visible policy. A missing
        /// or stale record remains a conservative hold rather than becoming a
        /// speculative completion signal.
        /// </summary>
        public bool TryConsumeGpuSchedulerFeedback(int frameIndex, ulong completedFrameSerial)
        {
            if (!_schedulerMode.IsGpuMode())
                return false;

            if (!_gpuScheduler.TryReadCompletedFeedback(
                    frameIndex,
                    completedFrameSerial,
                    out GPUSimpleDdgiSchedulerFeedback feedback))
            {
                return false;
            }

            bool transportGenerationMatches =
                feedback.TransportGeneration == _transportGeneration ||
                AdvanceSourceLightingGeneration(feedback.TransportGeneration) ==
                    _transportGeneration;
            if (feedback.VolumeTableGeneration != _volumeTableGeneration ||
                feedback.SourceLightingGeneration != _sourceLightingGeneration ||
                !transportGenerationMatches)
            {
                _gpuSchedulerFeedbackGenerationRejectionCount =
                    SaturatingAdd(_gpuSchedulerFeedbackGenerationRejectionCount, 1UL);
                _gpuSchedulerGenerationMismatchStreak =
                    Math.Min(int.MaxValue, _gpuSchedulerGenerationMismatchStreak + 1);
                _gpuSchedulerFeedbackValid = false;
                _transportResidentParticipantCount = 0;
                _transportResidentSourceRepairProbeCount = 0;
                if (_gpuSchedulerGenerationMismatchStreak >= 3)
                {
                    RequestGpuSchedulerFallback(
                        "repeated delayed scheduler feedback generation mismatch");
                }
                return false;
            }

            _gpuSchedulerGenerationMismatchStreak = 0;
            if (feedback.StatusFlags != 0u)
            {
                _gpuSchedulerFeedbackValid = false;
                _transportResidentParticipantCount = 0;
                _transportResidentSourceRepairProbeCount = 0;
                RequestGpuSchedulerFallback(
                    (feedback.StatusFlags & 1u) != 0u
                        ? "resident scheduler overflow"
                        : "resident scheduler invalid generation");
                return false;
            }

            _lastGpuSchedulerFeedback = feedback;
            _gpuSchedulerFeedbackFrameSerial =
                ((ulong)feedback.FrameSerialHigh << 32) | feedback.FrameSerialLow;
            _gpuSchedulerFeedbackValid = true;
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                _receiverRecordsPublishedCount = checked((int)Math.Min(
                    int.MaxValue,
                    feedback.PublishedCount));
            }

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                TransportV2Active &&
                ShouldAdvanceResidentCanonicalGeneration(
                    feedback.PublishedCount,
                    _gpuScheduler.LastActiveCanonicalMutationCount) &&
                feedback.TransportGeneration == _transportGeneration)
            {
                // The resident publish stage is GPU-owned, so advance the
                // canonical-field generation when its delayed commit witness
                // arrives. The next frame uploads this generation into both
                // params and scheduler frame word 18 before any audit can run.
                _transportGeneration = AdvanceSourceLightingGeneration(
                    _transportGeneration);
            }

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                ShouldAdvanceResidentSourceEpoch(
                    feedback.SourceProbeUsed,
                    _gpuScheduler.LastActiveSourceMutationCount))
            {
                // Resident probe epochs live in the scheduler arena. The
                // delayed summary is the host's conservative witness that the
                // epoch set may have changed; advancing once is sufficient to
                // invalidate a frozen global tuple without pretending that all
                // probes share one epoch value.
                _sourceEpochGeneration = AdvanceSourceEpoch(
                    _sourceEpochGeneration);
            }

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                TransportV2Active &&
                TailCertificationEnabled)
            {
                SynchronizeGpuResidentPeriodicSourceRefreshWave(feedback);
            }

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                _transportResidentParticipantCount = checked((int)Math.Min(
                    int.MaxValue,
                    feedback.SolveEpochParticipantCount));
                _transportResidentSourceRepairProbeCount = checked((int)Math.Min(
                    int.MaxValue,
                    ResolveBlockingTailSourceWorkCount(feedback)));
                SynchronizeGpuResidentSourceCohort(feedback);
                if (EnforceGpuResidentSourceProgress(feedback))
                {
                    // Recovery advances the source/transport generations. The
                    // feedback packet was valid for the generation that just
                    // failed to make progress, but must not be used to mark a
                    // solve epoch complete after that boundary.
                    _gpuSchedulerFeedbackValid = false;
                    _transportResidentParticipantCount = 0;
                    _transportResidentSourceRepairProbeCount = _probeCount;
                    return false;
                }

                // A delayed resident summary is the only host-visible witness
                // for a complete GPU solve epoch. A source repair observed
                // while an audit is frozen invalidates that audit immediately;
                // otherwise prepare the controller from the exact resident
                // counts and accept completion only when the epoch/stamp
                // reduction agrees with the frozen generations.
                if (TailCertificationEnabled &&
                    HasBlockingTailSourceWork(feedback) &&
                    _transportSolveController.Phase ==
                        SimpleDdgiTransportPhase.AuditFrozen)
                {
                    CancelTransportTailAudit(
                        SimpleDdgiTransportCertificationReason.SourceRepairRequired);
                }

                PrepareTailSolveController();
                SimpleDdgiTransportGenerations generations =
                    CreateTransportTailGenerations();
                if (TailCertificationEnabled &&
                    TryCompleteTransportSolveDrain(feedback))
                {
                    TryBeginTransportTailAudit();
                }
                else if (TailCertificationEnabled &&
                    !_transportSolveDrainPending &&
                    _transportSolveController.Phase ==
                        SimpleDdgiTransportPhase.AcceleratedSolve &&
                    feedback.SolveEpoch != 0u &&
                    feedback.SolveEpoch == _transportSolveController.SolveEpoch &&
                    feedback.SolveEpochVisitedCount ==
                        feedback.SolveEpochParticipantCount &&
                    !HasBlockingTailSourceWork(feedback) &&
                    _transportSolveController.MarkGpuEpochComplete(
                        feedback.SolveEpoch,
                        _transportResidentParticipantCount,
                        generations))
                {
                    BeginTransportSolveDrain();
                }
            }

            // These are delayed observations only. They are useful to the
            // control plane and diagnostics, but never rebuild the resident
            // queue or mutate per-probe CPU mirrors.
            _schedulerDeferredRequestCount = Math.Max(
                0,
                checked((int)Math.Min(
                    int.MaxValue,
                    (ulong)feedback.ConsideredCount -
                    Math.Min((ulong)feedback.ConsideredCount, feedback.AcceptedCount))));
            _sourceRefreshRayCapacityShortfall = feedback.SourceCapacityShortfall;
            return true;
        }

        internal static bool HasBlockingTailSourceWork(
            GPUSimpleDdgiSchedulerFeedback feedback) =>
            ResolveBlockingTailSourceWorkCount(feedback) != 0u;

        internal static bool ShouldAdvanceResidentSourceEpoch(
            uint admittedSourceProbeCount,
            uint activeParticipantSourceMutationCount) =>
            admittedSourceProbeCount != 0u &&
            activeParticipantSourceMutationCount != 0u;

        internal static bool ShouldAdvanceResidentCanonicalGeneration(
            uint publishedProbeCount,
            uint activeParticipantCanonicalMutationCount) =>
            publishedProbeCount != 0u &&
            activeParticipantCanonicalMutationCount != 0u;

        private void BeginTransportSolveDrain()
        {
            _transportSolveDrainPending = true;
            _transportSolveDrainStartFeedbackSerial =
                _gpuSchedulerFeedbackFrameSerial;
        }

        private void CancelTransportSolveDrain()
        {
            _transportSolveDrainPending = false;
            _transportSolveDrainStartFeedbackSerial = 0UL;
        }

        private bool TryCompleteTransportSolveDrain(
            GPUSimpleDdgiSchedulerFeedback feedback)
        {
            if (!CanCompleteTransportSolveDrain(
                    _transportSolveDrainPending,
                    _transportSolveDrainStartFeedbackSerial,
                    _gpuSchedulerFeedbackFrameSerial,
                    feedback.SolveEpoch,
                    _gpuScheduler.LastActiveCanonicalMutationCount,
                    _gpuScheduler.LastActiveSourceMutationCount,
                    HasBlockingTailSourceWork(feedback)))
            {
                return false;
            }

            CancelTransportSolveDrain();
            return true;
        }

        internal static bool CanCompleteTransportSolveDrain(
            bool drainPending,
            ulong drainStartFeedbackSerial,
            ulong feedbackSerial,
            uint feedbackSolveEpoch,
            uint activeCanonicalMutationCount,
            uint activeSourceMutationCount,
            bool blockingSourceWork) =>
            drainPending &&
            feedbackSerial > drainStartFeedbackSerial &&
            feedbackSolveEpoch == 0u &&
            activeCanonicalMutationCount == 0u &&
            activeSourceMutationCount == 0u &&
            !blockingSourceWork;

        internal static uint ResolveBlockingTailSourceWorkCount(
            GPUSimpleDdgiSchedulerFeedback feedback)
        {
            uint pendingCardinality =
                feedback.PackedPendingSourceInvalidAndCardinalityCounts >> 16;
            uint pendingGeneration =
                feedback.PackedPendingSourceRepairAndGenerationCounts >> 16;
            return Math.Max(
                Math.Max(
                    Math.Max(feedback.PendingSourceCount, feedback.PendingFreshCount),
                    Math.Max(feedback.PendingExposedCount, feedback.PendingRelocationCount)),
                Math.Max(pendingCardinality, pendingGeneration));
        }

        private void SynchronizeGpuResidentPeriodicSourceRefreshWave(
            GPUSimpleDdgiSchedulerFeedback feedback)
        {
            if (feedback.RoutineSourceProbeUsed != 0u)
            {
                if (!_transportPeriodicSourceRefreshWavePending)
                {
                    _transportPeriodicSourceRefreshWavePending = true;
                    // Feedback writes the scheduler frame index whenever the
                    // source cohort is nonempty. Freeze that exact membership
                    // boundary; later probes cannot drift into this solve.
                    _transportPeriodicSourceRefreshWaveCutoffFrame =
                        feedback.SourceCohortStartFrame != 0u
                            ? feedback.SourceCohortStartFrame
                            : _frameIndex;
                }
                return;
            }

            if (_transportPeriodicSourceRefreshWavePending &&
                !HasBlockingTailSourceWork(feedback) &&
                feedback.SourceProbeUsed == 0u)
            {
                // Classification and commit precede feedback in the same GPU
                // transaction. A zero pending/used witness therefore proves
                // every member of the frozen cutoff cohort is committed.
                _transportPeriodicSourceRefreshWavePending = false;
                _transportPeriodicSourceRefreshWaveCutoffFrame = 0u;
            }
        }

        private void SynchronizeGpuResidentSourceCohort(
            GPUSimpleDdgiSchedulerFeedback feedback)
        {
            uint blocking = ResolveBlockingTailSourceWorkCount(feedback);
            _sourceStepStaleProbeCount = checked((int)Math.Min(
                int.MaxValue,
                blocking));
            _sourceStepAgeMaximumFrames = checked((int)Math.Min(
                int.MaxValue,
                Math.Max(
                    Math.Max(feedback.MaximumFreshAge, feedback.MaximumExposedAge),
                    feedback.MaximumRelocationAge)));
            _sourceStepAgeP95Frames = 0;
            if (blocking != 0u ||
                feedback.SourceLightingGeneration != _sourceLightingGeneration)
            {
                _sourceCohortQuietFrames = 0;
                return;
            }

            _admittedSourceCohortGeneration = _sourceLightingGeneration;
            if (_sourceCohortTransitionActive)
            {
                _sourceCohortTransitionActive = false;
                _sourceCohortCompletionCount = SaturatingAdd(
                    _sourceCohortCompletionCount,
                    1UL);
                _sourceCohortCompletedFrame = _frameIndex;
                _sourceCohortQuietFrames = 0;
            }
            else
            {
                _sourceCohortQuietFrames = Math.Min(
                    SourceCohortQuietFrameCount,
                    _sourceCohortQuietFrames + 1);
            }
        }

        private bool EnforceGpuResidentSourceProgress(
            GPUSimpleDdgiSchedulerFeedback feedback)
        {
            bool requiresProgress = HasBlockingTailSourceWork(feedback) &&
                _sourceRefreshTargetProbeCount > 0 &&
                feedback.SourceCapacityShortfall == 0u;
            if (!requiresProgress || feedback.SourceProbeUsed != 0u ||
                feedback.SourceAchievedRays != 0u)
            {
                _transportSourceNoProgressFeedbackPeriods = 0;
                return false;
            }

            _transportSourceNoProgressFeedbackPeriods = Math.Min(
                int.MaxValue,
                _transportSourceNoProgressFeedbackPeriods + 1);
            if (_transportSourceNoProgressFeedbackPeriods < 2 ||
                _transportSolveController.Phase == SimpleDdgiTransportPhase.AuditFrozen)
            {
                return false;
            }

            _transportSourceNoProgressFeedbackPeriods = 0;
            _transportSourceNoProgressRecoveryCount = SaturatingAdd(
                _transportSourceNoProgressRecoveryCount,
                1UL);
            _transportSolveController.EnterSourceCohortRecovery(
                CreateTransportTailGenerations());
            _transportTailSummary = _transportSolveController.LastSummary;
            ApplyTransportTailRecovery(
                _transportSolveController.RecoveryAction,
                _transportSolveController.LastReason);
            return true;
        }

        /// <summary>
        /// Accepts only the fixed, fence-complete residency summary. It never
        /// rebuilds a CPU page list; generation or bijection failures freeze
        /// mutation and leave the dense coarser rings as the safe receiver path.
        /// </summary>
        public bool TryConsumeProbeResidencyFeedback(
            int frameIndex,
            ulong completedFrameSerial)
        {
            if (!_capacityPlan.ResidencyMode.CollectsDemand())
                return false;
            if (!_probePageCache.TryReadCompletedFeedback(
                    frameIndex,
                    completedFrameSerial,
                    out GPUSimpleDdgiResidencyFeedback feedback))
            {
                return false;
            }

            if (feedback.ResidencyResourceGeneration !=
                    _probePageCache.ResourceGeneration ||
                feedback.VirtualPageCount !=
                    (uint)Math.Max(0, _capacityPlan.SparseVirtualPageCount) ||
                feedback.SparsePhysicalPageCapacity !=
                    (uint)Math.Max(0, _capacityPlan.SparsePhysicalPageCapacity) ||
                feedback.PhysicalProbeCapacity !=
                    (uint)Math.Max(0, _capacityPlan.PhysicalProbeCapacity))
            {
                _probeResidencyFeedbackGenerationRejectionCount =
                    SaturatingAdd(
                        _probeResidencyFeedbackGenerationRejectionCount,
                        1UL);
                _probeResidencyFeedbackValid = false;
                return false;
            }

            _lastProbeResidencyFeedback = feedback;
            _probeResidencyFeedbackFrameSerial =
                ((ulong)feedback.FrameSerialHigh << 32) |
                feedback.FrameSerialLow;
            _probeResidencyFeedbackValid = true;
            bool invalid = feedback.PageTableReverseDisagreementCount != 0u ||
                feedback.DuplicateVirtualOwnerCount != 0u ||
                feedback.DuplicatePhysicalOwnerCount != 0u ||
                (feedback.Flags & ((1u << 1) | (1u << 2))) != 0u;
            if (invalid)
            {
                ReportProbeResidencyUnavailable(
                    "residency feedback reported an invalid mapping or generation-wrap transaction",
                    residencyStateValid: false,
                    requiresFreshTransaction: true);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Records an initialization-time scheduler failure without allowing a
        /// GPU-mode request to escape into a partially initialized graph. The
        /// CPU reference path remains usable; a fresh reset is only required if
        /// the resident path had already become authoritative.
        /// </summary>
        public void ReportGpuSchedulerUnavailable(string reason)
        {
            if (!_settings.GlobalIllumination.SimpleDdgiSchedulerMode.IsGpuMode())
                return;

            RequestGpuSchedulerFallback(
                string.IsNullOrWhiteSpace(reason)
                    ? "GPU scheduler pipeline unavailable"
                    : reason,
                requiresFreshReset: _schedulerMode == SimpleDdgiSchedulerMode.GpuResident);
        }

        public void ReportProbeResidencyUnavailable(
            string reason,
            bool residencyStateValid = true,
            bool requiresFreshTransaction = false)
        {
            if (!_capacityPlan.ResidencyMode.CollectsDemand() &&
                !_settings.GlobalIllumination.SimpleDdgiProbeResidencyMode
                    .CollectsDemand())
            {
                return;
            }

            string resolvedReason = string.IsNullOrWhiteSpace(reason)
                ? "Simple-DDGI residency pipeline unavailable"
                : reason;
            if (!requiresFreshTransaction)
            {
                // Pipeline construction failures cannot recover without a new
                // pass instance. Keep this latch across the first arena
                // bootstrap instead of accidentally unfreezing empty mappings.
                _probeResidencyMutationUnavailable = true;
                _probeResidencyMutationFailureReason = resolvedReason;
            }
            _probePageCache.FreezeForRuntimeFailure(
                residencyStateValid:
                    residencyStateValid && _probePageCache.ResidencyValid,
                reason: resolvedReason);
            _probeResidencyFeedbackValid = false;
            if (requiresFreshTransaction &&
                _capacityPlan.ResidencyMode.UsesSparsePayloads())
            {
                RequestGpuSchedulerFallback(
                    _probePageCache.FailureReason,
                    requiresFreshReset: false);
            }
        }

        /// <summary>
        /// Explicit development tooling control. Debug views never call this;
        /// callers must opt into residency mutation separately.
        /// </summary>
        public bool TrySetProbeResidencyDevelopmentPin(
            int virtualPageIndex,
            bool pinned) =>
            _probePageCache.TryQueueDevelopmentPagePin(
                virtualPageIndex,
                pinned);

        /// <summary>
        /// Freezes or releases page-table mutation for inspection without
        /// manufacturing a runtime failure or invalidating the frozen map.
        /// </summary>
        public void SetProbeResidencyDevelopmentFreeze(bool frozen) =>
            _probePageCache.SetDevelopmentMutationFrozen(frozen);

        private SimpleDdgiAtmosphereCohortFeedback CreateGpuResidentAtmosphereCohortFeedbackSnapshot()
        {
            bool sparseResidencyFeedbackMissing =
                _capacityPlan.ResidencyMode.UsesSparsePayloads() &&
                !_probeResidencyFeedbackValid;
            if (!_gpuSchedulerFeedbackValid || sparseResidencyFeedbackMissing)
            {
                // A resident scheduler without a completed matching summary
                // cannot authorize a new atmosphere cohort. Keep all current
                // participants stale until the normal delayed ring catches up.
                int gpuParticipants = TransportV2Active
                    ? (_capacityPlan.ResidencyMode.UsesSparsePayloads()
                        ? Math.Max(0, _capacityPlan.PhysicalProbeCapacity)
                        : _probeCount)
                    : 0;
                return new SimpleDdgiAtmosphereCohortFeedback(
                    _volumeTableGeneration,
                    _sourceLightingGeneration,
                    0u,
                    _transportGeneration,
                    0u,
                    gpuParticipants,
                    gpuParticipants,
                    gpuParticipants,
                    0,
                    0,
                    SourceCohortActive: gpuParticipants > 0,
                    VisiblePublicationBoundaryComplete: false,
                    MinimumPropagationBoundaryComplete: false,
                    QuietPeriodComplete: false,
                    AchievableSourceSweepSeconds: _sourceRefreshMinimumSweepSeconds,
                    TargetSourceProbeCount: _sourceRefreshTargetProbeCount,
                    TargetSourceRayCount: _sourceRefreshTargetRayCount,
                    SourceCapacityShortfall: _sourceRefreshCapacityShortfall,
                    SourceRayCapacityShortfall: _sourceRefreshRayCapacityShortfall,
                    StaticConvergencePending: true,
                    StaleReadbackRejectionCount: _staleReadbackRejectionCount +
                        _gpuScheduler.StaleFeedbackCount,
                    ResourceGenerationRejectionCount: _resourceGenerationRejectionCount +
                        _gpuSchedulerFeedbackGenerationRejectionCount,
                    ResidencyFeedbackComplete: !sparseResidencyFeedbackMissing);
            }

            GPUSimpleDdgiSchedulerFeedback feedback = _lastGpuSchedulerFeedback;
            int participants = TransportV2Active
                ? checked((int)Math.Min(
                    int.MaxValue,
                    feedback.SolveEpochParticipantCount))
                : 0;
            int visibleParticipants = checked((int)Math.Min(
                int.MaxValue,
                feedback.VisiblePriorityParticipatingProbeCount));
            int visibleReady = checked((int)Math.Min(
                int.MaxValue,
                feedback.VisiblePrioritySourceReadyProbeCount));
            int visiblePublished = checked((int)Math.Min(
                int.MaxValue,
                feedback.VisiblePriorityPublishedProbeCount));
            bool sourceCohortActive =
                feedback.StaticConvergencePending != 0u ||
                feedback.PendingSourceCount != 0u ||
                feedback.SourceCapacityShortfall != 0u ||
                feedback.SourceAchievedRays < feedback.SourceTargetRays ||
                feedback.StaticConvergedGeneration != feedback.SourceLightingGeneration ||
                (_capacityPlan.ResidencyMode.UsesSparsePayloads() &&
                    (_lastProbeResidencyFeedback.AdmissionProbeCount != 0u ||
                     _lastProbeResidencyFeedback
                        .OtherGenerationEvictionProbeCount != 0u));
            bool propagationComplete =
                feedback.StaticConvergencePending == 0u &&
                feedback.PublishedPropagationGeneration == feedback.PropagationGeneration;
            bool quietComplete = !sourceCohortActive &&
                feedback.SourceCohortCompletionFrame != 0u;
            bool staticConverged = !sourceCohortActive &&
                propagationComplete &&
                feedback.StaticConvergedGeneration == feedback.SourceLightingGeneration;

            return new SimpleDdgiAtmosphereCohortFeedback(
                feedback.VolumeTableGeneration,
                feedback.SourceLightingGeneration,
                staticConverged ? feedback.StaticConvergedGeneration : 0u,
                feedback.PropagationGeneration,
                feedback.PublishedPropagationGeneration,
                participants,
                checked((int)Math.Min(int.MaxValue, feedback.PendingSourceCount)),
                visibleParticipants,
                visibleReady,
                visiblePublished,
                sourceCohortActive,
                visibleReady >= visibleParticipants &&
                    visiblePublished >= visibleParticipants,
                propagationComplete,
                quietComplete,
                _sourceRefreshMinimumSweepSeconds,
                feedback.SourceCohortStartFrame,
                feedback.SourceCohortCompletionFrame,
                feedback.SourceCohortStartCount,
                feedback.SourceCohortCompletionCount,
                _sourceRefreshTargetProbeCount,
                checked((int)Math.Min(int.MaxValue, feedback.AcceptedCount)),
                checked((int)Math.Min(int.MaxValue, feedback.AcceptedCount)),
                _sourceRefreshTargetRayCount,
                feedback.SourceAchievedRays,
                feedback.SourceAchievedRays,
                _sourceRefreshCapacityShortfall,
                feedback.SourceCapacityShortfall,
                staticConverged ? feedback.StaticConvergedGeneration : 0u,
                !staticConverged,
                _staleReadbackRejectionCount,
                _resourceGenerationRejectionCount +
                    _gpuSchedulerFeedbackGenerationRejectionCount,
                ResidencyFeedbackComplete: true,
                ResidencyEventSourceGeneration:
                    _lastProbeResidencyFeedback.EventSourceGeneration,
                ResidencyEventCohortGeneration:
                    _lastProbeResidencyFeedback.EventCohortGeneration,
                ResidencyAdmissionProbeCount: checked((int)Math.Min(
                    int.MaxValue,
                    _lastProbeResidencyFeedback.AdmissionProbeCount)),
                ResidencyEvictionProbeCount: checked((int)Math.Min(
                    int.MaxValue,
                    _lastProbeResidencyFeedback.EvictionProbeCount)),
                ResidencyOtherGenerationEvictionProbeCount: checked((int)Math.Min(
                    int.MaxValue,
                    _lastProbeResidencyFeedback
                        .OtherGenerationEvictionProbeCount)));
        }

        public GiAtmosphereCohortFeedback CreateAtmosphereCohortFeedback() =>
            CreateAtmosphereCohortFeedbackSnapshot().ToAdmissionFeedback();
        public bool TransportV2Active => _settings.GlobalIllumination.SimpleDdgiTransportV2Enabled;
        /// <summary>
        /// True while a global source or layout change is still receiving its
        /// bounded field-wide multi-bounce solve. This distinguishes legitimate
        /// warmup from an unexpectedly dim, already-converged field in captures.
        /// </summary>
        public bool TransportGlobalConvergencePending =>
            TransportV2Active &&
            (_transportGlobalConvergencePending ||
             (TailCertificationEnabled && !HasCurrentTransportTailCertificate));
        private bool HasCurrentTransportTailCertificate =>
            _transportSolveController.IsCertified &&
            _transportTailSummary.IsCurrent(CreateTransportTailGenerations());
        public SimpleDdgiTailCertificationAvailability TailCertificationAvailability =>
            SimpleDdgiTransportSolveController.ResolveTailCertificationAvailability(
                _settings.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled,
                _schedulerMode,
                _gpuScheduler.IsReady,
                _gpuSchedulerFrameExecutionAvailable);
        public bool TailCertificationEnabled => TailCertificationAvailability.Enabled;
        public string TailCertificationFallbackReason
        {
            get
            {
                SimpleDdgiTailCertificationAvailability availability =
                    TailCertificationAvailability;
                return availability.Enabled ||
                       availability.Reason ==
                           SimpleDdgiTailCertificationFallbackReason.DisabledByConfiguration
                    ? string.Empty
                    : availability.Message;
            }
        }
        public bool TransportAccelerationEnabled =>
            _settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled;
        private bool _transportAccelerationRuntimeAvailable;
        public bool TransportAccelerationRuntimeAvailable =>
            _transportAccelerationRuntimeAvailable;
        public bool TransportAccelerationSolveActive =>
            TransportV2Active &&
            TailCertificationEnabled &&
            TransportAccelerationEnabled &&
            _transportAccelerationRuntimeAvailable &&
            _transportSolveController.Phase ==
                SimpleDdgiTransportPhase.AcceleratedSolve &&
            !_transportSolveController.IsCertified &&
            GpuSchedulerFrameExecutionAvailable;
        public int TransportAcceleratedSweepCount =>
            _settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled
                ? _settings.GlobalIllumination.SimpleDdgiTransportAcceleratedSweepCount
                : 1;
        public ReadOnlySpan<int> TransportSolveVolumeOrder =>
            _transportVolumeOrder.AsSpan(0, _volumeCount);

        public bool HasTransportWorkForVolume(int volumeIndex)
        {
            if ((uint)volumeIndex >= (uint)_volumeCount)
                return false;
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                return _gpuScheduler.IsReady && _probeCount > 0;
            return _probesToUpdate > 0 &&
                _volumeScheduledTransportProbeCounts[volumeIndex] > 0;
        }
        internal void SetTransportAccelerationRuntimeAvailable(bool available) =>
            _transportAccelerationRuntimeAvailable = available;
        public SimpleDdgiTransportPhase TransportTailPhase =>
            _transportSolveController.Phase;
        public SimpleDdgiTransportCertificationReason TransportTailCertificationReason =>
            _transportSolveController.LastReason;
        public SimpleDdgiTransportTailSummary TransportTailSummary => _transportTailSummary;
        public uint TransportAuditWitnessProbeIndex =>
            _transportAuditWitnessProbeIndex;
        public uint TransportAuditWitnessTexelIndex =>
            _transportAuditWitnessTexelIndex;
        public uint TransportTailAuditEpoch => _transportSolveController.AuditEpoch;
        public uint TransportTailSolveEpoch => _transportSolveController.SolveEpoch;
        public bool TransportTailSolveEpochComplete =>
            _transportSolveController.IsSolveEpochComplete;
        public ulong TransportTailSameTupleReauditAttemptCount =>
            _transportSolveController.SameTupleReauditAttemptCount;
        public ulong TransportTailRecoveryCount =>
            _transportSolveController.RecoveryCount;
        public int TransportTailNoProgressFrames =>
            _transportSolveController.NoProgressFrames;
        public ulong TransportTailAuditReadbackTimeoutCount =>
            _transportAuditReadbackTimeoutCount;
        public ulong TransportTailSourceNoProgressRecoveryCount =>
            _transportSourceNoProgressRecoveryCount;
        public ulong TransportTailConvergenceDeadlineRecoveryCount =>
            _transportConvergenceDeadlineRecoveryCount;
        public int TransportTailAuditReadbackDeadlineFrames =>
            SimpleDdgiTransportSolveController.ResolveAuditReadbackDeadlineFrames(
                Math.Max(0, _probeCount),
                TransportAuditProbeChunkSize,
                RenderingConstants.FramesInFlight,
                TransportAuditReadbackMarginFrames);
        public int TransportTailCompletedAuditReadbackAgeFrames =>
            _transportAuditFinalSubmissionFrameSerial == 0UL ||
            _frameSerial <= _transportAuditFinalSubmissionFrameSerial
                ? 0
                : checked((int)Math.Min(
                    int.MaxValue,
                    _frameSerial - _transportAuditFinalSubmissionFrameSerial));
        public int TransportTailConvergenceDeadlineFrames
        {
            get
            {
                int sourceBudget = _schedulerSourceRequestBudget > 0
                    ? _schedulerSourceRequestBudget
                    : _settings.GlobalIllumination
                        .SimpleDdgiProbeUpdatesPerFrame;
                int sourceSweepFrames = Math.Max(
                    1,
                    ResolveTransportSourceSweepFrames(
                        AuthoredTransportSourceSweepFrames,
                        // The source-repair set grows toward the frozen active
                        // cut while fresh probes publish. The physical field is
                        // the stable upper bound; a delayed partial feedback
                        // count must not shorten this wave's clock.
                        Math.Max(0, _probeCount),
                        sourceBudget,
                        _probeCount));
                int auditDeadlineFrames =
                    TransportTailAuditReadbackDeadlineFrames;
                // Source repair shares a deterministic resident admission
                // envelope with fresh-page publication, relocation, and the
                // delayed feedback that changes phases. One ideal arithmetic
                // sweep is therefore not an end-to-end scheduling guarantee.
                // Reserve one additional bounded sweep (or the audit window,
                // whichever is larger) exactly as the safe start-to-start
                // cadence does. The former frames-in-flight-only margin reset
                // a healthy Dense startup while its final fresh probes were
                // still publishing, making recovery invalidate all completed
                // source work and repeat forever.
                return ResolveTransportTailConvergenceDeadlineFrames(
                    sourceSweepFrames,
                    // Feedback reports only the currently source-ready
                    // participant cut. During startup that cut grows while
                    // fresh probes publish. Size the solve opportunity for
                    // the physical field so the deadline cannot shrink as
                    // eligibility advances underneath delayed feedback.
                    Math.Max(0, _probeCount),
                    Math.Max(1, _schedulerEffectiveRequestBudget),
                    Math.Max(1, TransportAcceleratedSweepCount),
                    auditDeadlineFrames,
                    RenderingConstants.FramesInFlight);
            }
        }

        internal static int ResolveTransportTailConvergenceDeadlineFrames(
            int sourceSweepFrames,
            int probeCount,
            int solveProbeBudgetPerFrame,
            int acceleratedSweepCount,
            int auditDeadlineFrames,
            int framesInFlight)
        {
            if (framesInFlight < 0)
                throw new ArgumentOutOfRangeException(nameof(framesInFlight));

            int schedulingMarginFrames = Math.Max(
                Math.Max(sourceSweepFrames, auditDeadlineFrames),
                Math.Max(2, framesInFlight * 2));
            return SimpleDdgiTransportSolveController
                .ResolveConvergenceDeadlineFrames(
                    sourceSweepFrames,
                    probeCount,
                    solveProbeBudgetPerFrame,
                    acceleratedSweepCount,
                    auditDeadlineFrames,
                    schedulingMarginFrames);
        }
        public bool TransportTailAuditPending =>
            TransportV2Active &&
            TailCertificationEnabled &&
            _transportSolveController.Phase == SimpleDdgiTransportPhase.AuditFrozen;

        /// <summary>
        /// Returns the current generation snapshot used by the tail audit. The
        /// snapshot intentionally excludes per-dispatch publication counters;
        /// a solve may publish several red/black batches before it is audited.
        /// </summary>
        public SimpleDdgiTransportGenerations GetTransportTailGenerations() =>
            CreateTransportTailGenerations();

        public SimpleDdgiTransportGenerations GetFrozenTransportTailGenerations() =>
            _transportAuditGenerations.IsInitialized
                ? _transportAuditGenerations
                : CreateTransportTailGenerations();

        /// <summary>
        /// Starts the frozen audit once the complete solve epoch has visited all
        /// participants. Render-graph code calls this immediately before the
        /// audit dispatch; a false result means the field is still incomplete.
        /// </summary>
        public bool TryBeginTransportTailAudit()
        {
            if (!TransportV2Active || !TailCertificationEnabled)
                return false;

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                _transportSolveDrainPending)
            {
                return false;
            }

            if (_transportSolveController.Phase == SimpleDdgiTransportPhase.AuditFrozen)
                return true;

            SimpleDdgiTransportGenerations generations = CreateTransportTailGenerations();
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                (!_gpuSchedulerFeedbackValid ||
                 HasBlockingTailSourceWork(_lastGpuSchedulerFeedback) ||
                 _sourceCohortTransitionActive))
            {
                if (_gpuSchedulerFeedbackValid &&
                    HasBlockingTailSourceWork(_lastGpuSchedulerFeedback))
                {
                    _transportSolveController.BeginSourceRepair(generations);
                    _transportTailSummary = _transportSolveController.LastSummary;
                }
                return false;
            }
            if (!_transportSolveController.TryBeginAudit(generations))
                return false;

            // TryBeginAudit advances the audit epoch and updates the frozen
            // tuple. Retain that post-increment tuple for every chunk and for
            // delayed readback acceptance; keeping the pre-increment tuple
            // would make a valid audit reject its own summary.
            _transportAuditGenerations = _transportSolveController.FrozenGenerations;
            _transportAuditProbeCursor = 0;
            _transportAuditChunkCount = checked((uint)Math.Max(
                1,
                (Math.Max(0, _probeCount) + TransportAuditProbeChunkSize - 1) /
                TransportAuditProbeChunkSize));
            _transportAuditFirstFrameSerial = _frameSerial;
            _transportAuditFinalSubmissionFrameSerial = 0UL;
            _transportAuditExpectedParticipantCount = Math.Max(
                0,
                _schedulerMode == SimpleDdgiSchedulerMode.GpuResident
                    ? _transportResidentParticipantCount
                    : _schedulerParticipatingProbeCount);
            _transportAuditExpectedTexelCount = checked(
                _transportAuditExpectedParticipantCount * IrradianceTexelsPerProbe * IrradianceTexelsPerProbe);
            _transportTailSummary = _transportSolveController.LastSummary with
            {
                AuditEpoch = _transportSolveController.AuditEpoch,
                Generations = _transportAuditGenerations,
                ExpectedParticipantCount = checked((uint)_transportAuditExpectedParticipantCount),
                ExpectedTexelCount = checked((uint)_transportAuditExpectedTexelCount),
                Reason = SimpleDdgiTransportCertificationReason.AuditInProgress
            };
            return true;
        }

        public const int TransportAuditProbeChunkSize =
            SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceProbeCapacity;

        public bool TryGetTransportTailAuditChunk(
            out SimpleDdgiTransportAuditChunkDispatch dispatch)
        {
            dispatch = default;
            if (!TransportTailAuditPending ||
                _transportAuditProbeCursor >= _probeCount ||
                _transportAuditChunkCount == 0u)
            {
                return false;
            }

            int probeCount = Math.Min(
                TransportAuditProbeChunkSize,
                _probeCount - _transportAuditProbeCursor);
            uint chunkIndex = checked((uint)(
                _transportAuditProbeCursor / TransportAuditProbeChunkSize));
            dispatch = new SimpleDdgiTransportAuditChunkDispatch(
                _transportSolveController.AuditEpoch,
                chunkIndex,
                _transportAuditChunkCount,
                _transportAuditProbeCursor,
                probeCount,
                _transportAuditExpectedParticipantCount,
                _transportAuditExpectedTexelCount,
                chunkIndex + 1u == _transportAuditChunkCount);
            return true;
        }

        public bool MarkTransportTailAuditChunkSubmitted(
            SimpleDdgiTransportAuditChunkDispatch dispatch)
        {
            if (!TransportTailAuditPending ||
                dispatch.AuditEpoch != _transportSolveController.AuditEpoch ||
                dispatch.ProbeOffset != _transportAuditProbeCursor ||
                dispatch.ChunkCount != _transportAuditChunkCount)
            {
                return false;
            }

            _transportAuditProbeCursor = checked(
                _transportAuditProbeCursor + dispatch.ProbeCount);
            if (dispatch.IsFinal)
                _transportAuditFinalSubmissionFrameSerial = _frameSerial;
            return true;
        }

        /// <summary>
        /// Converts the compact GPU reduction into a generation-frozen summary.
        /// The shader supplies only maxima/counters; all policy math and the
        /// completion decision remain on the CPU.
        /// </summary>
        public bool TryConsumeGpuTransportAudit(
            int frameIndex,
            ulong completedFrameSerial)
        {
            if (!TransportV2Active ||
                !TailCertificationEnabled ||
                _transportSolveController.Phase != SimpleDdgiTransportPhase.AuditFrozen)
            {
                return false;
            }

            if (!_gpuScheduler.TryReadCompletedTransportAudit(
                    frameIndex,
                    completedFrameSerial,
                    out SimpleDdgiTransportAuditReadback readback))
            {
                ExpireTransportTailAuditIfOverdue();
                return false;
            }

            GPUSimpleDdgiTransportAuditSummary gpu = readback.Summary;
            float defect = BitConverter.UInt32BitsToSingle(gpu.FixedPointDefectBits);
            float fieldMagnitude = BitConverter.UInt32BitsToSingle(gpu.FieldMagnitudeBits);
            float observedContraction = BitConverter.UInt32BitsToSingle(
                gpu.ObservedContractionBits);
            float canonicalQuantizationFloor = BitConverter.UInt32BitsToSingle(
                gpu.CanonicalQuantizationFloorBits);
            const uint auditWitnessAddressMask = (1u << 20) - 1u;
            uint auditWitnessAddress = gpu.MaximumDefectWitnessKey &
                auditWitnessAddressMask;
            uint maximumDefectWitnessProbeIndex = auditWitnessAddress >> 6;
            uint maximumDefectWitnessTexelIndex = auditWitnessAddress & 63u;
            _transportAuditWitnessProbeIndex = maximumDefectWitnessProbeIndex;
            _transportAuditWitnessTexelIndex = maximumDefectWitnessTexelIndex;
            bool detailedWitnessValid = gpu.DetailedWitnessValid == 1u &&
                gpu.DetailedWitnessProbeIndex < (uint)Math.Max(0, _probeCount) &&
                gpu.DetailedWitnessTexelIndex < (uint)(IrradianceTexelsPerProbe *
                    IrradianceTexelsPerProbe);
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            float q = gi.SimpleDdgiTransportAlbedoClamp;
            bool countersFinite =
                float.IsFinite(defect) && defect >= 0.0f &&
                float.IsFinite(fieldMagnitude) && fieldMagnitude >= 0.0f &&
                float.IsFinite(observedContraction) && observedContraction >= 0.0f &&
                observedContraction <= q &&
                float.IsFinite(canonicalQuantizationFloor) &&
                canonicalQuantizationFloor >= 0.0f;
            // The shader reduction is FP32. Move the observed maximum upward
            // by one representable value before using it as evidence, then
            // retain the configured ceiling as the fail-closed upper bound.
            float roundedObservedContraction = countersFinite
                ? MathF.Min(q, MathF.BitIncrement(observedContraction))
                : float.NaN;
            float certifiedQ = countersFinite
                ? MathF.Min(q, roundedObservedContraction)
                : float.NaN;
            float tail = countersFinite && certifiedQ < 1.0f
                ? defect / MathF.Max(1.0f - certifiedQ, 1e-6f)
                : float.NaN;
            float tolerance = countersFinite
                ? MathF.Max(
                    SimpleDdgiTransportTailEstimator.AbsoluteTolerance,
                    gi.SimpleDdgiTransportTailRelativeTolerance * fieldMagnitude)
                : float.NaN;
            bool completeChunk =
                _transportAuditChunkCount > 0u &&
                gpu.LastChunkIndex == _transportAuditChunkCount - 1u;
            uint expectedParticipants = gpu.ExpectedParticipantCount;
            uint expectedTexels = checked(expectedParticipants *
                (uint)(IrradianceTexelsPerProbe * IrradianceTexelsPerProbe));
            bool coverageCountersMatch =
                gpu.ExpectedTexelCount == expectedTexels &&
                gpu.AuditedParticipantCount == expectedParticipants &&
                gpu.AuditedTexelCount == expectedTexels &&
                gpu.ExcludedStaleSourceCount == 0u;
            bool finiteEvidence = countersFinite &&
                float.IsFinite(q) &&
                q >= 0.0f &&
                q <= SimpleDdgiTransportTailEstimator.MaximumCertifiedContraction &&
                float.IsFinite(roundedObservedContraction) &&
                roundedObservedContraction >= 0.0f &&
                roundedObservedContraction <= q &&
                float.IsFinite(tail) &&
                float.IsFinite(tolerance);
            bool quantizationLimited = finiteEvidence &&
                canonicalQuantizationFloor > tolerance;
            SimpleDdgiTransportCertificationReason reason =
                !completeChunk
                    ? SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete
                    : gpu.CounterOverflow != 0u
                        ? SimpleDdgiTransportCertificationReason.CounterOverflow
                        : gpu.InvalidCacheCount > 0u
                            ? SimpleDdgiTransportCertificationReason.InvalidCache
                        : !coverageCountersMatch
                            ? SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete
                        : !finiteEvidence || gpu.NonFiniteCount > 0u
                        ? SimpleDdgiTransportCertificationReason.NonFiniteEvidence
                        : quantizationLimited
                                ? SimpleDdgiTransportCertificationReason.QuantizationLimited
                                : tail <= tolerance
                                ? SimpleDdgiTransportCertificationReason.Certified
                                : SimpleDdgiTransportCertificationReason.TailAboveTolerance;

            SimpleDdgiTransportTailSummary summary = new()
            {
                AuditEpoch = readback.AuditEpoch,
                Generations = _transportAuditGenerations,
                ExpectedParticipantCount = expectedParticipants,
                AuditedParticipantCount = gpu.AuditedParticipantCount,
                ExpectedTexelCount = expectedTexels,
                AuditedTexelCount = gpu.AuditedTexelCount,
                ExcludedInactiveCount = gpu.ExcludedInactiveCount,
                ExcludedNotVisibleCount = gpu.ExcludedNotVisibleCount,
                ExcludedStaleSourceCount = gpu.ExcludedStaleSourceCount,
                NonFiniteCount = gpu.NonFiniteCount,
                ExcludedInvalidCacheCount = gpu.InvalidCacheCount,
                CacheIdentityFailureCount = gpu.CacheIdentityFailureCount,
                CacheCardinalityFailureCount = gpu.CacheCardinalityFailureCount,
                CacheSourceGenerationFailureCount =
                    gpu.CacheSourceGenerationFailureCount,
                CacheSourceEpochFailureCount = gpu.CacheSourceEpochFailureCount,
                CachePhysicalGenerationFailureCount =
                    gpu.CachePhysicalGenerationFailureCount,
                FirstNotResidentIdentity =
                    SimpleDdgiTransportMismatchIdentity.FromPacked(
                        gpu.FirstNotResidentIdentity),
                FirstStaleSourceIdentity =
                    SimpleDdgiTransportMismatchIdentity.FromPacked(
                        gpu.FirstStaleSourceIdentity),
                FirstInvalidCacheIdentity =
                    SimpleDdgiTransportMismatchIdentity.FromPacked(
                        gpu.FirstInvalidCacheIdentity),
                FirstNonFiniteIdentity =
                    SimpleDdgiTransportMismatchIdentity.FromPacked(
                        gpu.FirstNonFiniteIdentity),
                FixedPointDefect = defect,
                FieldMagnitude = fieldMagnitude,
                ConfiguredContractionBound = q,
                ObservedContractionBound = roundedObservedContraction,
                CertifiedContractionBound = certifiedQ,
                AbsoluteTailBound = tail,
                RelativeTailBound = countersFinite
                    ? tail / MathF.Max(fieldMagnitude, SimpleDdgiTransportTailEstimator.AbsoluteTolerance)
                    : float.NaN,
                Tolerance = tolerance,
                CanonicalQuantizationFloor = canonicalQuantizationFloor,
                MaximumDefectWitnessProbeIndex = maximumDefectWitnessProbeIndex,
                MaximumDefectWitnessTexelIndex = maximumDefectWitnessTexelIndex,
                DetailedWitnessValid = detailedWitnessValid,
                DetailedWitnessProbeIndex = gpu.DetailedWitnessProbeIndex,
                DetailedWitnessTexelIndex = gpu.DetailedWitnessTexelIndex,
                DetailedWitnessWeightSum = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessWeightSumBits),
                DetailedWitnessCandidateR = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCandidateRBits),
                DetailedWitnessCandidateG = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCandidateGBits),
                DetailedWitnessCandidateB = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCandidateBBits),
                DetailedWitnessCanonicalR = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCanonicalRBits),
                DetailedWitnessCanonicalG = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCanonicalGBits),
                DetailedWitnessCanonicalB = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessCanonicalBBits),
                DetailedWitnessProbeResidual = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessProbeResidualBits),
                DetailedWitnessSourceRayCount = gpu.DetailedWitnessSourceRayCount,
                DetailedWitnessPrivateR = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessPrivateRBits),
                DetailedWitnessPrivateG = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessPrivateGBits),
                DetailedWitnessPrivateB = BitConverter.UInt32BitsToSingle(
                    gpu.DetailedWitnessPrivateBBits),
                AuditMicroseconds = 0,
                FirstFrameSerial = _transportAuditFirstFrameSerial,
                FinalFrameSerial = readback.FrameSerial,
                ChunkCount = _transportAuditChunkCount,
                IsComplete = completeChunk,
                CounterOverflowCount = gpu.CounterOverflow,
                Reason = reason
            };
            return TryAcceptTransportTailSummary(summary);
        }

        /// <summary>
        /// Consumes a chunk-aggregated GPU audit summary. A summary from a stale
        /// field, incomplete participant set, invalid cache, or non-finite
        /// candidate is rejected and leaves V2 pending.
        /// </summary>
        public bool TryAcceptTransportTailSummary(SimpleDdgiTransportTailSummary summary)
        {
            if (!TransportV2Active || !TailCertificationEnabled)
                return false;

            SimpleDdgiTransportGenerations generations = CreateTransportTailGenerations();
            bool accepted = _transportSolveController.TryAcceptAudit(summary, generations);
            CancelTransportSolveDrain();
            _transportTailSummary = _transportSolveController.LastSummary;
            SimpleDdgiTransportRecoveryAction recoveryAction =
                _transportSolveController.RecoveryAction;
            SimpleDdgiTransportCertificationReason recoveryReason =
                _transportSolveController.LastReason;
            if (accepted)
            {
                _transportGlobalConvergencePending = false;
                _transportGlobalSourceRepairPhasePending = false;
                _publishedPropagationGeneration = _transportGeneration;
                _transportNextPeriodicSourceRefreshFrame =
                    ResolveNextPeriodicSourceRefreshFrame(
                        _frameIndex,
                        EffectiveTransportSourceRefreshFrames);
                RequirePersistentSchedulerRebuild();
            }
            else
            {
                _transportGlobalConvergencePending = true;
                ApplyTransportTailRecovery(recoveryAction, recoveryReason);
                RequirePersistentSchedulerRebuild();
            }
            return accepted;
        }

        public void CancelTransportTailAudit(SimpleDdgiTransportCertificationReason reason)
        {
            CancelTransportSolveDrain();
            _transportSolveController.CancelAudit(reason);
            _transportTailSummary = _transportSolveController.LastSummary;
            _transportAuditFinalSubmissionFrameSerial = 0UL;
            _transportGlobalConvergencePending = true;
            RequirePersistentSchedulerRebuild();
        }

        private void ExpireTransportTailAuditIfOverdue()
        {
            if (_transportSolveController.Phase != SimpleDdgiTransportPhase.AuditFrozen ||
                _transportAuditFirstFrameSerial == 0UL ||
                _frameSerial <= _transportAuditFirstFrameSerial)
            {
                return;
            }

            ulong age = _frameSerial - _transportAuditFirstFrameSerial;
            if (age < (ulong)TransportTailAuditReadbackDeadlineFrames)
                return;

            if (_transportSolveController.ExpireAudit(CreateTransportTailGenerations()))
            {
                _transportAuditReadbackTimeoutCount = SaturatingAdd(
                    _transportAuditReadbackTimeoutCount,
                    1UL);
                _transportTailSummary = _transportSolveController.LastSummary;
                _transportAuditFinalSubmissionFrameSerial = 0UL;
                _transportGlobalConvergencePending = true;
                RequirePersistentSchedulerRebuild();
            }
        }

        private void ApplyTransportTailRecovery(
            SimpleDdgiTransportRecoveryAction action,
            SimpleDdgiTransportCertificationReason reason)
        {
            switch (action)
            {
                case SimpleDdgiTransportRecoveryAction.None:
                case SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch:
                case SimpleDdgiTransportRecoveryAction.ReportUnsupportedTolerance:
                    return;
                case SimpleDdgiTransportRecoveryAction.ReconcileParticipants:
                    _transportParticipantReconciliationFeedbackSerial =
                        _gpuSchedulerFeedbackFrameSerial;
                    _transportResidentParticipantCount = 0;
                    _transportResidentSourceRepairProbeCount = 0;
                    return;
                case SimpleDdgiTransportRecoveryAction.RepairSourceCache:
                case SimpleDdgiTransportRecoveryAction.RebuildPrivateField:
                    // The audit is read-only, so it cannot mark one suspect
                    // cache entry for repair. Advance the bounded source
                    // generation once; resident classification then repairs
                    // every current participant while receivers continue to
                    // sample the last coherent canonical field.
                    _sourceLightingGeneration = AdvanceSourceLightingGeneration(
                        _sourceLightingGeneration);
                    _sourceEpochGeneration = AdvanceSourceEpoch(
                        _sourceEpochGeneration);
                    _transportGeneration = AdvanceSourceLightingGeneration(
                        _transportGeneration);
                    InvalidateTransportSourceCacheMetadata(
                        certificationReason: reason,
                        recoveryAction: action);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
        /// <summary>
        /// Number of internal DDGI frames spent in the current field-wide
        /// source/transport warmup. Zero means the local residual policy is
        /// active, not that the field was never solved.
        /// </summary>
        public int TransportGlobalConvergenceElapsedFrames
        {
            get
            {
                if (!TransportGlobalConvergencePending)
                    return 0;

                uint elapsed = unchecked(_frameIndex - _transportGlobalConvergenceStartFrame);
                return elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
            }
        }

        private void ResetUploadCapacityTelemetry()
        {
            _uploadCapacityStableKeyHit = false;
            _uploadCapacityCpuProbeStateMicroseconds = 0;
            _uploadCapacityPlanCreationMicroseconds = 0;
            _uploadCapacityPredicateMicroseconds = 0;
            _uploadCapacityBufferSizeLookupMicroseconds = 0;
            _uploadCapacityDeviceIdleWaitMicroseconds = 0;
            _uploadCapacityBufferTransitionMicroseconds = 0;
            _uploadCapacityReadbackReconciliationMicroseconds = 0;
            _uploadCapacitySampledAtlasBudgetMicroseconds = 0;
            _uploadCapacitySampledAtlasEnsureMicroseconds = 0;
            _uploadCapacityDescriptorRegistrationMicroseconds = 0;
            _uploadCapacityRetiredResourceDestructionMicroseconds = 0;
            _uploadCapacityBufferSizeLookupCount = 0;
            _uploadCapacityTransitionCount = 0;
            _uploadCapacityDeviceIdleWaitCount = 0;
            _uploadCapacityDescriptorRegistrationCount = 0;
            _uploadCapacityRetiredResourceDestructionCount = 0;
            _uploadCapacityTransitionReason = SimpleDdgiCapacityTransitionReason.None;
            _uploadCapacityIrradiance = default;
            _uploadCapacityVisibility = default;
            _uploadCapacityTransportIrradiance = default;
            _uploadCapacityTransportSourceCache = default;
            _uploadCapacityRayScratch = default;
            _uploadCapacityProbeState = default;
            _uploadCapacityReceiverProbes = default;
            _uploadCapacityUpdateQueue = default;
            _uploadCapacityRelocationClassification = default;
            _uploadCapacityReadback = default;
            _uploadCapacitySampledAtlas = default;
        }

        private SimpleDdgiCapacityTiming CreateUploadCapacityTiming() => new()
        {
            StableKeyHit = _uploadCapacityStableKeyHit,
            CpuProbeStateMicroseconds = _uploadCapacityCpuProbeStateMicroseconds,
            PlanCreationMicroseconds = _uploadCapacityPlanCreationMicroseconds,
            PredicateMicroseconds = _uploadCapacityPredicateMicroseconds,
            BufferSizeLookupMicroseconds = _uploadCapacityBufferSizeLookupMicroseconds,
            DeviceIdleWaitMicroseconds = _uploadCapacityDeviceIdleWaitMicroseconds,
            BufferTransitionMicroseconds = _uploadCapacityBufferTransitionMicroseconds,
            ReadbackReconciliationMicroseconds =
                _uploadCapacityReadbackReconciliationMicroseconds,
            SampledAtlasBudgetMicroseconds =
                _uploadCapacitySampledAtlasBudgetMicroseconds,
            SampledAtlasEnsureMicroseconds =
                _uploadCapacitySampledAtlasEnsureMicroseconds,
            DescriptorRegistrationMicroseconds =
                _uploadCapacityDescriptorRegistrationMicroseconds,
            RetiredResourceDestructionMicroseconds =
                _uploadCapacityRetiredResourceDestructionMicroseconds,
            BufferSizeLookupCount = _uploadCapacityBufferSizeLookupCount,
            TransitionCount = _uploadCapacityTransitionCount,
            DeviceIdleWaitCount = _uploadCapacityDeviceIdleWaitCount,
            DescriptorRegistrationCount = _uploadCapacityDescriptorRegistrationCount,
            RetiredResourceDestructionCount =
                _uploadCapacityRetiredResourceDestructionCount,
            RequiredLiveBytes = _capacityKeyValid ? _capacityPlan.LiveBytes : 0UL,
            TransitionReason = _uploadCapacityTransitionReason,
            IrradianceAtlas = _uploadCapacityIrradiance,
            VisibilityAtlas = _uploadCapacityVisibility,
            TransportIrradiance = _uploadCapacityTransportIrradiance,
            TransportSourceCache = _uploadCapacityTransportSourceCache,
            RayScratch = _uploadCapacityRayScratch,
            ProbeState = _uploadCapacityProbeState,
            ReceiverProbes = _uploadCapacityReceiverProbes,
            UpdateQueue = _uploadCapacityUpdateQueue,
            RelocationClassification = _uploadCapacityRelocationClassification,
            ReadbackBuffers = _uploadCapacityReadback,
            SampledAtlas = _uploadCapacitySampledAtlas
        };

        /// <summary>
        /// Monotonic count of live V2 source/solver calibration changes that
        /// restarted convergence. This is intentionally separate from physical
        /// cache invalidation count, which is measured in affected probe slots.
        /// </summary>
        public ulong TransportCalibrationChangeCount => _transportCalibrationChangeCount;

        /// <summary>
        /// Snapshot of V2 source/solver state from the scheduler's incremental
        /// counters; it neither reads GPU memory nor walks the probe pool.
        /// </summary>
        public void GetTransportProgress(
            out int sourceReadyProbeCount,
            out int sourceStaleProbeCount,
            out int convergedProbeCount,
            out int pendingSolverProbeCount)
        {
            sourceReadyProbeCount = 0;
            sourceStaleProbeCount = 0;
            convergedProbeCount = 0;
            pendingSolverProbeCount = 0;
            if (!TransportV2Active)
                return;

            sourceStaleProbeCount = _schedulerSourceRepairProbeCount;
            sourceReadyProbeCount = Math.Max(
                0,
                _schedulerParticipatingProbeCount - sourceStaleProbeCount);
            pendingSolverProbeCount = Math.Min(
                sourceReadyProbeCount,
                _schedulerPendingConvergenceProbeCount);
            convergedProbeCount = Math.Max(
                0,
                sourceReadyProbeCount - pendingSolverProbeCount);
        }

        /// <summary>
        /// Builds a capture snapshot from incrementally maintained counters. No
        /// probe-pool scan or GPU read occurs here; the bounded arrays are copied
        /// only when renderer diagnostics are materialized.
        /// Residual buckets are: &lt;=0.25T, &lt;=0.5T, &lt;=T, &lt;=2T,
        /// &lt;=4T, &lt;=8T, &gt;8T, invalid. Solver-generation buckets are
        /// 0, 1, 2, 3, 4-7, and 8+. Source-epoch buckets are current,
        /// one generation stale, two-or-more generations stale, and unknown.
        /// </summary>
        public SimpleDdgiTransportConvergenceTelemetry
            CreateTransportConvergenceTelemetry()
        {
            if (!TransportV2Active || _volumeCount <= 0)
                return SimpleDdgiTransportConvergenceTelemetry.Empty;

            var rings = new SimpleDdgiTransportRingConvergenceTelemetry[_volumeCount];
            int totalConverged = 0;
            int totalInactive = 0;
            int totalResidualQualifiedPending = 0;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                int[] reasons = CopyCounterSlice(
                    _volumeTransportProbeStateReasonCounts,
                    volumeIndex * TransportProbeStateReasonCount,
                    TransportProbeStateReasonCount);
                int[] residuals = CopyCounterSlice(
                    _volumeTransportResidualDistributionCounts,
                    volumeIndex * TransportResidualDistributionBucketCount,
                    TransportResidualDistributionBucketCount);
                int[] generations = CopyCounterSlice(
                    _volumeTransportSolverGenerationDistributionCounts,
                    volumeIndex * TransportSolverGenerationBucketCount,
                    TransportSolverGenerationBucketCount);
                int[] sourceEpochs = CopyCounterSlice(
                    _volumeTransportSourceEpochDistributionCounts,
                    volumeIndex * TransportSourceEpochDistributionBucketCount,
                    TransportSourceEpochDistributionBucketCount);
                int inactive = reasons[(int)SimpleDdgiTransportProbeStateReason.Inactive];
                int sourceStale = reasons[
                    (int)SimpleDdgiTransportProbeStateReason.SourceStale];
                int converged = reasons[
                    (int)SimpleDdgiTransportProbeStateReason.Converged];
                int probeCount = 0;
                for (int reason = 0; reason < reasons.Length; reason++)
                    probeCount = SaturatingAdd(probeCount, reasons[reason]);
                int sourceReady = Math.Max(0, probeCount - inactive - sourceStale);
                int pending = Math.Max(0, sourceReady - converged);
                totalConverged = SaturatingAdd(totalConverged, converged);
                totalInactive = SaturatingAdd(totalInactive, inactive);
                int residualQualifiedPending =
                    _volumeTransportResidualQualifiedPendingCounts[volumeIndex];
                totalResidualQualifiedPending = SaturatingAdd(
                    totalResidualQualifiedPending,
                    residualQualifiedPending);
                rings[volumeIndex] = new SimpleDdgiTransportRingConvergenceTelemetry(
                    volumeIndex,
                    _volumePurposes[volumeIndex],
                    probeCount,
                    sourceReady,
                    sourceStale,
                    pending,
                    converged,
                    Array.AsReadOnly(reasons),
                    Array.AsReadOnly(residuals),
                    Array.AsReadOnly(sourceEpochs),
                    Array.AsReadOnly(generations))
                {
                    ScheduledProbeCount =
                        _volumeScheduledTransportProbeCounts[volumeIndex],
                    ScheduledRayCount =
                        _volumeScheduledTransportRayCounts[volumeIndex],
                    ResidualQualifiedNotConvergedProbeCount =
                        residualQualifiedPending,
                    SolverCompletionLatencySampleCount =
                        _volumeTransportSolverCompletionLatencySampleCounts[volumeIndex],
                    SolverCompletionLatencyP50Frames =
                        ResolveHistogramPercentile(
                            _volumeTransportSolverCompletionLatencyHistograms,
                            volumeIndex * TransportSolverCompletionLatencyBucketCount,
                            TransportSolverCompletionLatencyBucketCount,
                            _volumeTransportSolverCompletionLatencySampleCounts[volumeIndex],
                            0.50),
                    SolverCompletionLatencyP95Frames =
                        ResolveHistogramPercentile(
                            _volumeTransportSolverCompletionLatencyHistograms,
                            volumeIndex * TransportSolverCompletionLatencyBucketCount,
                            TransportSolverCompletionLatencyBucketCount,
                            _volumeTransportSolverCompletionLatencySampleCounts[volumeIndex],
                            0.95),
                    SolverCompletionLatencyMaxFrames =
                        _volumeTransportSolverCompletionLatencyMaxFrames[volumeIndex]
                };
            }

            return new SimpleDdgiTransportConvergenceTelemetry(
                _probeConvergenceReadbackValid,
                _schedulerParticipatingProbeCount,
                _schedulerSourceRepairProbeCount,
                _schedulerPendingConvergenceProbeCount,
                totalConverged,
                totalInactive,
                _transportDispatchLaneCount,
                _transportUsefulDispatchLaneCount,
                _transportNoOpDispatchLaneCount,
                _settings.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance,
                Array.AsReadOnly(rings))
            {
                TailPhase = _transportSolveController.Phase,
                TailReason = _transportSolveController.LastReason,
                TailGenerations = _transportTailSummary.Generations,
                TailSolveEpoch = _transportSolveController.SolveEpoch,
                TailAuditEpoch = _transportSolveController.AuditEpoch,
                TailExpectedParticipantCount =
                    _transportTailSummary.ExpectedParticipantCount,
                TailAuditedParticipantCount =
                    _transportTailSummary.AuditedParticipantCount,
                TailExcludedInactiveCount =
                    _transportTailSummary.ExcludedInactiveCount,
                TailExcludedNotVisibleCount =
                    _transportTailSummary.ExcludedNotVisibleCount,
                TailExcludedStaleSourceCount =
                    _transportTailSummary.ExcludedStaleSourceCount,
                TailExcludedInvalidCacheCount =
                    _transportTailSummary.ExcludedInvalidCacheCount,
                TailCacheIdentityFailureCount =
                    _transportTailSummary.CacheIdentityFailureCount,
                TailCacheCardinalityFailureCount =
                    _transportTailSummary.CacheCardinalityFailureCount,
                TailCacheSourceGenerationFailureCount =
                    _transportTailSummary.CacheSourceGenerationFailureCount,
                TailCacheSourceEpochFailureCount =
                    _transportTailSummary.CacheSourceEpochFailureCount,
                TailCachePhysicalGenerationFailureCount =
                    _transportTailSummary.CachePhysicalGenerationFailureCount,
                TailNonFiniteCount =
                    _transportTailSummary.NonFiniteCount,
                TailCounterOverflowCount =
                    _transportTailSummary.CounterOverflowCount,
                TailFirstNotResidentIdentity =
                    _transportTailSummary.FirstNotResidentIdentity,
                TailFirstStaleSourceIdentity =
                    _transportTailSummary.FirstStaleSourceIdentity,
                TailFirstInvalidCacheIdentity =
                    _transportTailSummary.FirstInvalidCacheIdentity,
                TailFirstNonFiniteIdentity =
                    _transportTailSummary.FirstNonFiniteIdentity,
                TailExpectedTexelCount = _transportTailSummary.ExpectedTexelCount,
                TailAuditedTexelCount = _transportTailSummary.AuditedTexelCount,
                TailFixedPointDefect = _transportTailSummary.FixedPointDefect,
                TailFieldMagnitude = _transportTailSummary.FieldMagnitude,
                TailConfiguredContractionBound =
                    _transportTailSummary.ConfiguredContractionBound,
                TailObservedContractionBound =
                    _transportTailSummary.ObservedContractionBound,
                TailCertifiedContractionBound =
                    _transportTailSummary.CertifiedContractionBound,
                TailAbsoluteBound = _transportTailSummary.AbsoluteTailBound,
                TailRelativeBound = _transportTailSummary.RelativeTailBound,
                TailTolerance = _transportTailSummary.Tolerance,
                TailCanonicalQuantizationFloor =
                    _transportTailSummary.CanonicalQuantizationFloor,
                TailMaximumDefectWitnessProbeIndex =
                    _transportTailSummary.MaximumDefectWitnessProbeIndex,
                TailMaximumDefectWitnessTexelIndex =
                    _transportTailSummary.MaximumDefectWitnessTexelIndex,
                TailDetailedWitnessValid =
                    _transportTailSummary.DetailedWitnessValid,
                TailDetailedWitnessProbeIndex =
                    _transportTailSummary.DetailedWitnessProbeIndex,
                TailDetailedWitnessTexelIndex =
                    _transportTailSummary.DetailedWitnessTexelIndex,
                TailDetailedWitnessWeightSum =
                    _transportTailSummary.DetailedWitnessWeightSum,
                TailDetailedWitnessCandidateR =
                    _transportTailSummary.DetailedWitnessCandidateR,
                TailDetailedWitnessCandidateG =
                    _transportTailSummary.DetailedWitnessCandidateG,
                TailDetailedWitnessCandidateB =
                    _transportTailSummary.DetailedWitnessCandidateB,
                TailDetailedWitnessCanonicalR =
                    _transportTailSummary.DetailedWitnessCanonicalR,
                TailDetailedWitnessCanonicalG =
                    _transportTailSummary.DetailedWitnessCanonicalG,
                TailDetailedWitnessCanonicalB =
                    _transportTailSummary.DetailedWitnessCanonicalB,
                TailDetailedWitnessProbeResidual =
                    _transportTailSummary.DetailedWitnessProbeResidual,
                TailDetailedWitnessSourceRayCount =
                    _transportTailSummary.DetailedWitnessSourceRayCount,
                TailDetailedWitnessPrivateR =
                    _transportTailSummary.DetailedWitnessPrivateR,
                TailDetailedWitnessPrivateG =
                    _transportTailSummary.DetailedWitnessPrivateG,
                TailDetailedWitnessPrivateB =
                    _transportTailSummary.DetailedWitnessPrivateB,
                TailAuditMicroseconds = _transportTailSummary.AuditMicroseconds,
                TailAuditFirstFrameSerial =
                    _transportTailSummary.FirstFrameSerial,
                TailAuditFinalFrameSerial = _transportTailSummary.FinalFrameSerial,
                TailAuditChunkCount = _transportTailSummary.ChunkCount,
                TailAuditComplete = _transportTailSummary.IsComplete,
                TailCertificateCurrent = HasCurrentTransportTailCertificate,
                TailRecoveryAction = _transportSolveController.RecoveryAction,
                TailSameTupleReauditAttemptCount =
                    TransportTailSameTupleReauditAttemptCount,
                TailRecoveryCount = TransportTailRecoveryCount,
                TailNoProgressFrames = TransportTailNoProgressFrames,
                TailAuditReadbackDeadlineFrames =
                    TransportTailAuditReadbackDeadlineFrames,
                TailConvergenceDeadlineFrames =
                    TransportTailConvergenceDeadlineFrames,
                TailCompletedAuditReadbackAgeFrames =
                    TransportTailCompletedAuditReadbackAgeFrames,
                TailAuditReadbackTimeoutCount =
                    TransportTailAuditReadbackTimeoutCount,
                TailSourceNoProgressRecoveryCount =
                    TransportTailSourceNoProgressRecoveryCount,
                TailConvergenceDeadlineRecoveryCount =
                    TransportTailConvergenceDeadlineRecoveryCount,
                ResidualQualifiedNotConvergedProbeCount =
                    totalResidualQualifiedPending,
                RoutineSourceRepairProbeCount =
                    _schedulerRoutineSourceRepairProbeCount,
                RoutineMaintenancePendingProbeCount =
                    _schedulerRoutineMaintenancePendingProbeCount,
                DispatchBatchCount = _rayDispatchBatchCount
            };
        }

        private static int[] CopyCounterSlice(
            int[] source,
            int offset,
            int count)
        {
            var result = new int[count];
            Array.Copy(source, offset, result, 0, count);
            return result;
        }
        public int EffectiveMaxShadedLights => _effectiveMaxShadedLights;
        public ulong AdaptiveRaySavedPrimaryRayCount => _adaptiveRaySavedPrimaryRayCount;
        public int RayBudgetRejectedProbeCount => _rayBudgetRejectedProbeCount;
        public ulong RayBudgetRejectedPrimaryRayCount => _rayBudgetRejectedPrimaryRayCount;
        /// <summary>
        /// Bounded, allocation-free scheduler state for the most recently built
        /// queue. Capture/reporting code can translate the pressure enum into text
        /// off the render thread.
        /// </summary>
        public SimpleDdgiSchedulerTelemetry SchedulerTelemetry => new(
            ConfiguredRequestBudget: _schedulerConfiguredRequestBudget,
            EffectiveRequestBudget: _schedulerEffectiveRequestBudget,
            ScheduledVisibleZeroSupport: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport],
            ScheduledFreshExposedVisible: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible],
            ScheduledVisibleDirty: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty],
            ScheduledVisibleRetry: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry],
            ScheduledNearMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance],
            ScheduledMidMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance],
            ScheduledFarMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance],
            ReservedVisibleZeroSupport: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport],
            ReservedFreshExposedVisible: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible],
            ReservedVisibleDirty: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty],
            ReservedVisibleRetry: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry],
            ReservedNearMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance],
            ReservedMidMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance],
            ReservedFarMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance],
            PendingVisibleZeroSupport: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport],
            PendingFreshExposedVisible: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible],
            PendingVisibleDirty: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty],
            PendingVisibleRetry: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry],
            PendingNearMaintenance: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance],
            PendingMidMaintenance: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance],
            PendingFarMaintenance: _pendingWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance],
            DeferredRequestCount: _schedulerDeferredRequestCount,
            RejectedPrimaryRayCount: _rayBudgetRejectedPrimaryRayCount,
            PressureReason: _schedulerPressureReason,
            LastCompletedGpuMicroseconds: _schedulerLastCompletedGpuMicroseconds,
            TargetGpuMicroseconds: _schedulerTargetGpuMicroseconds,
            DeterministicFixedBudget: _schedulerDeterministicFixedBudget);
        public uint OldestVisibleUnsupportedProbeAge => _oldestVisibleUnsupportedProbeAge;
        public int VisibleUnsupportedProbeCountAboveLatencyTarget =>
            _visibleUnsupportedProbeCountAboveLatencyTarget;
        public int VisibleZeroSupportRepairUpdateCount => _visibleZeroSupportRepairUpdateCount;
        public int ProbeLifecycleLatencyTargetFrames => _probeLifecycleLatencyTargetFrames;
        public uint MaximumFreshProbeAge => _maximumFreshProbeAge;
        public uint MaximumScrollExposedProbeAge => _maximumScrollExposedProbeAge;
        public uint MaximumRelocationPendingProbeAge => _maximumRelocationPendingProbeAge;
        public uint MaximumUnpublishedProbeAge => _maximumUnpublishedProbeAge;
        public int ProbeLifecycleBoundExceededCount => _probeLifecycleBoundExceededCount;

        /// <summary>
        /// Returns the priority class assigned to a queue item. This is a debug and
        /// capture hook only; shader input stays ABI-compatible with the existing
        /// update queue.
        /// </summary>
        public SimpleDdgiSchedulerWorkClass GetScheduledWorkClass(int queueOffset)
        {
            if ((uint)queueOffset >= (uint)_probesToUpdate)
                return SimpleDdgiSchedulerWorkClass.None;
            return (SimpleDdgiSchedulerWorkClass)_queuedWorkClassScratch[queueOffset];
        }

        /// <summary>
        /// Returns the retained local dirty reason bits for a scheduled probe. The
        /// bit layout matches <see cref="DirtyReasonFlags"/> for light, emissive,
        /// and geometry/streaming causes; global lighting invalidation remains in
        /// <see cref="DirtyReasonFlags"/> because it has no single local region.
        /// </summary>
        public uint GetScheduledDirtyReasonFlags(int queueOffset)
        {
            if ((uint)queueOffset >= (uint)_probesToUpdate)
                return 0u;
            int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
            return (uint)probeIndex < (uint)_probeDirtyReasons.Length
                ? _probeDirtyReasons[probeIndex]
                : 0u;
        }

        /// <summary>Returns the 0..255 importance score used for visible-first admission.</summary>
        public byte GetScheduledProbeImportance(int queueOffset)
        {
            if ((uint)queueOffset >= (uint)_probesToUpdate)
                return 0;
            int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
            return (uint)probeIndex < (uint)_probeVisibilityImportance.Length
                ? _probeVisibilityImportance[probeIndex]
                : (byte)0;
        }

        /// <summary>
        /// Returns how many candidates in a work class were rejected by the hard
        /// primary-ray cap this frame. This makes a visible-response miss
        /// distinguishable from ordinary request-cap deferral without allocating a
        /// per-probe rejection list.
        /// </summary>
        public int GetPrimaryRayRejectedProbeCount(SimpleDdgiSchedulerWorkClass workClass)
        {
            int classIndex = (int)workClass;
            return (uint)classIndex < (uint)_rayRejectedWorkClassCounts.Length
                ? _rayRejectedWorkClassCounts[classIndex]
                : 0;
        }

        /// <summary>
        /// Supplies measured completed GPU work for the next queue. Adaptation is
        /// bounded and only recovers after an under-target completion; a missed
        /// time budget can never raise the following frame's request cap.
        /// Call on the render thread after the corresponding GPU timing resolves.
        /// </summary>
        public void ReportSchedulingFeedback(in SimpleDdgiSchedulingFeedback feedback)
        {
            _schedulerLastCompletedGpuMicroseconds = feedback.CompletedGpuMicroseconds;
            _schedulerTargetGpuMicroseconds = feedback.TargetGpuMicroseconds;
            _schedulerDeterministicFixedBudget = feedback.DeterministicFixedBudget;
            if (!UsesAdaptiveSchedulingFeedback(
                    feedback.DeterministicFixedBudget,
                    feedback.TargetGpuMicroseconds,
                    TransportV2Active))
            {
                _schedulerFeedbackRequestBudgetCap = 0;
                return;
            }

            _schedulerFeedbackRequestBudgetCap = ResolveAdaptiveSchedulingBudgetCap(
                _schedulerFeedbackRequestBudgetCap,
                _schedulerEffectiveRequestBudget,
                _schedulerConfiguredRequestBudget,
                feedback.CompletedGpuMicroseconds,
                feedback.TargetGpuMicroseconds);
        }

        /// <summary>
        /// V2 already converts its configured request cap into a fixed complete-
        /// ray-work envelope. Its scheduler also has a material fixed dispatch
        /// cost: treating that floor as request-proportional eventually drives
        /// the cap to one even though reducing admitted probes cannot remove the
        /// cost. Keep timing feedback for the legacy variable-ray path, while V2
        /// is governed by the explicit ray envelope and remains live enough to
        /// finish source sweeps and coherent page cohorts.
        /// </summary>
        internal static bool UsesAdaptiveSchedulingFeedback(
            bool deterministicFixedBudget,
            ulong targetGpuMicroseconds,
            bool fixedTransportV2RayEnvelope)
        {
            return !deterministicFixedBudget &&
                targetGpuMicroseconds != 0UL &&
                !fixedTransportV2RayEnvelope;
        }

        /// <summary>
        /// Resolves the next resident request cap from one fence-complete frame
        /// that actually dispatched the scheduler. Half of the declared DDGI
        /// budget is reserved for trace/solve/publication and page management;
        /// admission is therefore not allowed to train against the whole budget.
        /// A proportional over-budget correction removes a multi-millisecond
        /// burst in one observation, while under-budget recovery remains the
        /// deliberately slower one-eighth step.
        /// </summary>
        internal static int ResolveAdaptiveSchedulingBudgetCap(
            int feedbackCap,
            int lastEffectiveBudget,
            int configuredBudget,
            ulong completedGpuMicroseconds,
            ulong targetGpuMicroseconds)
        {
            int current = Math.Max(1,
                feedbackCap > 0 ? feedbackCap : lastEffectiveBudget);
            int ceiling = Math.Max(1, configuredBudget);
            current = Math.Min(current, ceiling);
            ulong schedulingTarget = Math.Max(1UL, targetGpuMicroseconds / 2UL);

            if (completedGpuMicroseconds > schedulingTarget)
            {
                // Keep ten percent measurement headroom. Double precision is
                // used only in this once-per-resolved-frame CPU controller;
                // the resulting integer cap remains deterministic for the
                // same completed timestamp and policy tuple.
                double scaled = current *
                    (schedulingTarget / (double)completedGpuMicroseconds) * 0.90;
                int proportional = Math.Max(1, (int)Math.Floor(scaled));
                return Math.Min(proportional, Math.Max(1, current - 1));
            }

            if (completedGpuMicroseconds <= schedulingTarget - schedulingTarget / 4UL)
            {
                int recovery = Math.Max(1, current / 8);
                return Math.Min(ceiling, current + recovery);
            }

            return current;
        }
        public int FullRayProbeUpdateCount => _fullRayProbeUpdateCount;
        public int MaintenanceRayProbeUpdateCount => _maintenanceRayProbeUpdateCount;
        public int NearFullRayProbeUpdateCount => _ringFullRayProbeUpdateCounts[0];
        public int MidFullRayProbeUpdateCount => _ringFullRayProbeUpdateCounts[1];
        public int FarFullRayProbeUpdateCount => _ringFullRayProbeUpdateCounts[2];
        public int NearMaintenanceRayProbeUpdateCount => _ringMaintenanceRayProbeUpdateCounts[0];
        public int MidMaintenanceRayProbeUpdateCount => _ringMaintenanceRayProbeUpdateCounts[1];
        public int FarMaintenanceRayProbeUpdateCount => _ringMaintenanceRayProbeUpdateCounts[2];
        public ulong NearScheduledPrimaryRayCount => _ringScheduledPrimaryRayCounts[0];
        public ulong MidScheduledPrimaryRayCount => _ringScheduledPrimaryRayCounts[1];
        public ulong FarScheduledPrimaryRayCount => _ringScheduledPrimaryRayCounts[2];
        /// <summary>Count of regional-dirty probes that reached a completed blend.</summary>
        public int DirtyFirstUpdateLatencySampleCount => ClampUIntToInt(_dirtyFirstUpdateLatencySampleCount);
        /// <summary>Count of regional-dirty probes observed stable through a valid readback.</summary>
        public int DirtyConvergenceLatencySampleCount => ClampUIntToInt(_dirtyConvergenceLatencySampleCount);
        /// <summary>Count of dirty probes that first entered the bounded GPU queue.</summary>
        public int DirtyFirstScheduledLatencySampleCount => ClampUIntToInt(_dirtyFirstScheduledLatencySampleCount);
        public int DirtyFirstScheduledLatencyP50Frames => CalculateLatencyPercentile(_dirtyFirstScheduledLatencyBuckets, _dirtyFirstScheduledLatencySampleCount, 0.50f);
        public int DirtyFirstScheduledLatencyP95Frames => CalculateLatencyPercentile(_dirtyFirstScheduledLatencyBuckets, _dirtyFirstScheduledLatencySampleCount, 0.95f);
        public int DirtyFirstUpdateLatencyP50Frames => CalculateLatencyPercentile(_dirtyFirstUpdateLatencyBuckets, _dirtyFirstUpdateLatencySampleCount, 0.50f);
        public int DirtyFirstUpdateLatencyP95Frames => CalculateLatencyPercentile(_dirtyFirstUpdateLatencyBuckets, _dirtyFirstUpdateLatencySampleCount, 0.95f);
        public int DirtyConvergenceLatencyP50Frames => CalculateLatencyPercentile(_dirtyConvergenceLatencyBuckets, _dirtyConvergenceLatencySampleCount, 0.50f);
        public int DirtyConvergenceLatencyP95Frames => CalculateLatencyPercentile(_dirtyConvergenceLatencyBuckets, _dirtyConvergenceLatencySampleCount, 0.95f);
        public int DirtyFirstUpdateLatencyMaxFrames => ClampUIntToInt(_dirtyFirstUpdateLatencyMaxFrames);
        public int DirtyConvergenceLatencyMaxFrames => ClampUIntToInt(_dirtyConvergenceLatencyMaxFrames);
        public int DirtyFirstScheduledLatencyMaxFrames => ClampUIntToInt(_dirtyFirstScheduledLatencyMaxFrames);
        public int DirtyFirstScheduledLatencyCensoredCount => ClampUIntToInt(_dirtyFirstScheduledLatencyCensoredCount);
        public int DirtyFirstUpdateLatencyCensoredCount => ClampUIntToInt(_dirtyFirstUpdateLatencyCensoredCount);
        public int DirtyConvergenceLatencyCensoredCount => ClampUIntToInt(_dirtyConvergenceLatencyCensoredCount);
        public int DirtyLatencyOutstandingEventCount => ClampUIntToInt(_dirtyLatencyOutstandingEventCount);
        public int ProbeRelocationCount => _probeStateReadbackValid != 0 ? _probeRelocationCount : 0;
        public int ClassifiedInactiveProbeCountEstimate => _probeStateReadbackValid != 0 ? _classifiedInactiveProbeCountEstimate : 0;
        public float AverageRelocationFractionEstimate => _probeStateReadbackValid != 0 ? _averageRelocationFractionEstimate : 0.0f;
        public float AverageBackfaceRatioEstimate => _probeStateReadbackValid != 0 ? _averageBackfaceRatioEstimate : 0.0f;
        public float AverageCloseRatioEstimate => _probeStateReadbackValid != 0 ? _averageCloseRatioEstimate : 0.0f;
        public float AverageHardInvalidProbeScoreEstimate => _probeStateReadbackValid != 0 ? _averageHardInvalidProbeScoreEstimate : 0.0f;
        public int ProbeStateReadbackValid => _probeStateReadbackValid;
        public GPUSimpleDdgiParams LastParams => _lastParams;
        public ReadOnlySpan<GPUSimpleDdgiVolume> LastVolumes => new(_volumeScratch, 0, _volumeCount);
        public ReadOnlySpan<GPUSimpleDdgiVolumePaging> LastVolumePaging =>
            new(_volumePagingScratch, 0, _volumeCount);
        /// <summary>
        /// The exact requested/accepted/rejected layout compiled before the
        /// current resource allocation.  A missing report means simple DDGI was
        /// disabled before a layout was required.
        /// </summary>
        public SimpleDdgiLayoutReport? LastLayoutReport => _lastLayoutReport;

        /// <summary>
        /// Returns one self-contained, frame-local scheduling record.  It deliberately
        /// distinguishes configured caps from work that was actually admitted so capture
        /// readers cannot mistake an under-filled queue for a smaller tier budget.
        /// </summary>
        public SimpleDdgiSchedulingTelemetry GetSchedulingTelemetry()
        {
            if (_volumeCount <= 0)
                return SimpleDdgiSchedulingTelemetry.Unavailable("Simple DDGI has no active resolved layout.");

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return new SimpleDdgiSchedulingTelemetry(
                IsAvailable: true,
                ConfiguredRequestBudget: _schedulerConfiguredRequestBudget,
                ConfiguredPrimaryRayBudget: Math.Max(0, gi.DdgiProbeUpdatePrimaryRayBudget),
                ScheduledRequestCount: _probesToUpdate,
                ScheduledPrimaryRayCount: _scheduledPrimaryRayCount,
                RejectedProbeCount: _rayBudgetRejectedProbeCount,
                RejectedPrimaryRayCount: _rayBudgetRejectedPrimaryRayCount,
                FirstScheduled: new SimpleDdgiLatencyTelemetry(
                    DirtyFirstScheduledLatencySampleCount,
                    DirtyFirstScheduledLatencyP50Frames,
                    DirtyFirstScheduledLatencyP95Frames,
                    DirtyFirstScheduledLatencyMaxFrames,
                    DirtyFirstScheduledLatencyCensoredCount),
                FirstCompleted: new SimpleDdgiLatencyTelemetry(
                    DirtyFirstUpdateLatencySampleCount,
                    DirtyFirstUpdateLatencyP50Frames,
                    DirtyFirstUpdateLatencyP95Frames,
                    DirtyFirstUpdateLatencyMaxFrames,
                    DirtyFirstUpdateLatencyCensoredCount),
                Convergence: new SimpleDdgiLatencyTelemetry(
                    DirtyConvergenceLatencySampleCount,
                    DirtyConvergenceLatencyP50Frames,
                    DirtyConvergenceLatencyP95Frames,
                    DirtyConvergenceLatencyMaxFrames,
                    DirtyConvergenceLatencyCensoredCount),
                OutstandingEventCount: DirtyLatencyOutstandingEventCount,
                CompletionSemantics: "First-completed is recorded when the probe blend publishes its result; receiver-visible effect is measured separately by locked ROI captures.");
        }
        /// <summary>
        /// Debug-only lookup against the bounded current-frame queue. Queue order is
        /// intentionally priority based, so it must not be inferred from a single
        /// contiguous probe-index range when visualizing multiple volumes.
        /// </summary>
        public bool IsProbeScheduledForUpdate(int probeIndex)
        {
            // BuildUpdateQueue clears this table once per frame and AddProbeUpdate
            // sets exactly the entries represented by the bounded GPU queue.  The
            // DDGI overlay can query hundreds of markers per frame, so scanning the
            // priority-ordered queue here would turn a constant-time debug query
            // into O(markers * scheduled probes).
            return (uint)probeIndex < (uint)_probeCount &&
                   _probeQueued[probeIndex] != 0;
        }
        public bool HasPendingUpdateTransaction => _updateTransactionPending;
        public bool CanExecuteTraceTransaction => _updateTransactionPending && !_traceTransactionExecuted;
        public bool CanExecuteRelocateClassifyTransaction => _updateTransactionPending && _traceTransactionExecuted && !_relocateClassifyTransactionExecuted;
        public bool CanExecuteTransportTransaction => TransportV2Active &&
            _updateTransactionPending && _traceTransactionExecuted && _relocateClassifyTransactionExecuted && !_transportTransactionExecuted;
        public bool CanExecuteBlendTransaction => _updateTransactionPending && _traceTransactionExecuted &&
            _relocateClassifyTransactionExecuted && (!TransportV2Active || _transportTransactionExecuted) &&
            !_blendTransactionExecuted;
        public bool CanExecutePublishTransaction => _updateTransactionPending && _blendTransactionExecuted;
        // Async planning asks all pass predicates before any command has been
        // recorded.  These predicates deliberately describe a transaction that is
        // safe to schedule, while CanExecute* above remains the stricter guard at
        // recording time.  This keeps the three passes together in a split queue
        // plan without permitting a consumer to read an older scratch result.
        public bool CanScheduleRelocateClassifyTransaction => _updateTransactionPending && !_relocateClassifyTransactionExecuted;
        public bool CanScheduleTransportTransaction => TransportV2Active && _updateTransactionPending && !_transportTransactionExecuted;
        public bool CanScheduleBlendTransaction => _updateTransactionPending;
        public bool CanSchedulePublishTransaction => _updateTransactionPending;
        public uint UpdateTransactionSerial => _updateTransactionSerial;

        public IReadOnlyList<DdgiVolumeDiagnosticsEntry> GetVolumeDiagnostics()
        {
            if (_volumeCount <= 0)
                return Array.Empty<DdgiVolumeDiagnosticsEntry>();

            var entries = new DdgiVolumeDiagnosticsEntry[_volumeCount];
            int activeProbeBudget = Math.Max(
                1,
                _lastLayoutReport?.Budget.ProbeBudget ?? GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                int probeCount = checked(countX * countY * countZ);
                float spacing = Spacing(volume);
                Vector3 origin = Origin(volume);
                Vector3 size = new(Math.Max(countX - 1, 1) * spacing, Math.Max(countY - 1, 1) * spacing, Math.Max(countZ - 1, 1) * spacing);
                int scheduledUpdates = Math.Max(0, (int)MathF.Round(volume.UpdateStartAndCount.Y));
                SimpleDdgiRingQuality quality = ResolveVolumeQuality(i);
                SimpleDdgiVolumeKind kind = Kind(volume) == VolumeKindAuthored
                    ? SimpleDdgiVolumeKind.Authored
                    : SimpleDdgiVolumeKind.CameraRing;
                int cascadeIndex = Kind(volume) == VolumeKindRing
                    ? Math.Clamp(SourceOrdinal(volume) - 10_000, 0, 2)
                    : 0;
                float volumeCubicMeters = Math.Max(size.X * size.Y * size.Z, 0.0001f);
                int firstProbe = FirstProbe(volume);
                bool cpuStateDiagnostics = !_schedulerMode.IsGpuMode();
                int ageCount = cpuStateDiagnostics &&
                    firstProbe >= 0 && firstProbe < _probeLastUpdatedFrames.Length
                    ? Math.Min(probeCount, _probeLastUpdatedFrames.Length - firstProbe)
                    : 0;
                FillProbeAgeSnapshot(
                    firstProbe,
                    _probeAgeSnapshotScratch.AsSpan(0, ageCount));
                uint estimatedAgeP95 = ageCount > 0
                    ? CalculateProbeAgePercentile(
                        _probeAgeSnapshotScratch.AsSpan(0, ageCount),
                        _probeAgePercentileScratch.AsSpan(0, ageCount),
                        0.95f)
                    : 0u;
                int inactiveProbeCount = cpuStateDiagnostics &&
                    _probeStateReadbackValid != 0
                    ? CountInactiveProbes(_probeInactive, firstProbe, probeCount)
                    : 0;
                int activeProbeCount = Math.Max(0, probeCount - inactiveProbeCount);
                SimpleDdgiLayoutVolumeDecision? layoutDecision = FindLayoutDecision(SourceOrdinal(volume));
                GPUSimpleDdgiVolumePaging paging = _volumePagingScratch[i];
                bool gpuRingResidencyValid = _schedulerMode.IsGpuMode() &&
                    _probeResidencyFeedbackValid &&
                    kind == SimpleDdgiVolumeKind.CameraRing;
                (uint ringVirtual, uint ringResident, uint ringActive,
                    uint ringInactive, uint ringDemanded, uint ringConverged) =
                    cascadeIndex switch
                    {
                        0 => (
                            _lastProbeResidencyFeedback.NearVirtualProbeCount,
                            _lastProbeResidencyFeedback.NearResidentProbeCount,
                            _lastProbeResidencyFeedback.NearActiveResidentProbeCount,
                            _lastProbeResidencyFeedback.NearInactiveResidentProbeCount,
                            _lastProbeResidencyFeedback.NearDemandedPageCount,
                            _lastProbeResidencyFeedback.NearConvergedResidentProbeCount),
                        1 => (
                            _lastProbeResidencyFeedback.MidVirtualProbeCount,
                            _lastProbeResidencyFeedback.MidResidentProbeCount,
                            _lastProbeResidencyFeedback.MidActiveResidentProbeCount,
                            _lastProbeResidencyFeedback.MidInactiveResidentProbeCount,
                            _lastProbeResidencyFeedback.MidDemandedPageCount,
                            _lastProbeResidencyFeedback.MidConvergedResidentProbeCount),
                        _ => (
                            _lastProbeResidencyFeedback.FarVirtualProbeCount,
                            _lastProbeResidencyFeedback.FarResidentProbeCount,
                            _lastProbeResidencyFeedback.FarActiveResidentProbeCount,
                            _lastProbeResidencyFeedback.FarInactiveResidentProbeCount,
                            _lastProbeResidencyFeedback.FarDemandedPageCount,
                            _lastProbeResidencyFeedback.FarConvergedResidentProbeCount)
                    };
                if (gpuRingResidencyValid)
                {
                    activeProbeCount = ClampUIntToInt(ringActive);
                    inactiveProbeCount = ClampUIntToInt(ringInactive);
                }

                entries[i] = new DdgiVolumeDiagnosticsEntry(
                    VolumeIndex: i,
                    Kind: kind,
                    CascadeIndex: cascadeIndex,
                    FirstProbeIndex: FirstProbe(volume),
                    ProbeCount: probeCount,
                    RaysPerProbe: quality.FullRays,
                    MaxProbeUpdatesPerFrame: _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame <= 0
                        ? probeCount
                        : Math.Min(probeCount, _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame),
                    ScheduledProbeUpdates: scheduledUpdates,
                    ScheduledPrimaryRayCount: CountScheduledPrimaryRays(i),
                    MaxRayDistance: Math.Max(spacing * Math.Max(Math.Max(countX, countY), countZ), spacing))
                {
                    PhysicalProbeCapacity = paging.ResidencyMode ==
                        (uint)SimpleDdgiProbeResidencyMode.SparseNearRing
                            ? checked(_capacityPlan.SparsePhysicalPageCapacity *
                                SimpleDdgiProbePageLayout.ProbesPerPage)
                            : probeCount,
                    ProbeResidencyMode = (SimpleDdgiProbeResidencyMode)
                        paging.ResidencyMode,
                    VirtualPageCount = checked((int)(paging.PageGridX *
                        paging.PageGridY * paging.PageGridZ)),
                    ResidentProbeCount = gpuRingResidencyValid
                        ? ClampUIntToInt(ringResident)
                        : probeCount,
                    ActiveResidentProbeCount = gpuRingResidencyValid
                        ? ClampUIntToInt(ringActive)
                        : activeProbeCount,
                    InactiveResidentProbeCount = gpuRingResidencyValid
                        ? ClampUIntToInt(ringInactive)
                        : inactiveProbeCount,
                    DemandedPageCount = gpuRingResidencyValid
                        ? ClampUIntToInt(ringDemanded)
                        : 0,
                    ConvergedResidentProbeCount = gpuRingResidencyValid
                        ? ClampUIntToInt(ringConverged)
                        : 0,
                    OriginX = origin.X,
                    OriginY = origin.Y,
                    OriginZ = origin.Z,
                    SizeX = size.X,
                    SizeY = size.Y,
                    SizeZ = size.Z,
                    ProbeSpacingX = spacing,
                    ProbeSpacingY = spacing,
                    ProbeSpacingZ = spacing,
                    MinProbeSpacing = spacing,
                    MaxProbeSpacing = spacing,
                    ProbeDensityPerCubicMeter = probeCount / volumeCubicMeters,
                    ActiveProbeBudgetFraction = Math.Clamp(probeCount / (float)activeProbeBudget, 0.0f, 1.0f),
                    ProbeStateCountsValid = gpuRingResidencyValid
                        ? 1
                        : cpuStateDiagnostics
                            ? _probeStateReadbackValid
                            : 0,
                    ActiveProbeCount = activeProbeCount,
                    InactiveProbeCount = inactiveProbeCount,
                    EstimatedAgeP95Frames = estimatedAgeP95,
                    PhysicalOffsetX = PhysicalOffsetX(volume),
                    PhysicalOffsetY = PhysicalOffsetY(volume),
                    PhysicalOffsetZ = PhysicalOffsetZ(volume),
                    IntendedPurpose = _volumePurposes[i],
                    AuthoredPriority = _volumePriorities[i],
                    LayoutDecision = layoutDecision?.Decision,
                    LayoutDecisionReason = layoutDecision?.Reason ?? "layout-report-unavailable",
                    LayoutRequestedProbeCount = layoutDecision?.Request.ProbeCount ?? probeCount,
                    LayoutAcceptedProbeCount = layoutDecision?.AcceptedProbeCount ?? probeCount,
                    LayoutRequestedPersistentBytes =
                        layoutDecision?.RequestedPersistentBytes ?? 0UL,
                    LayoutAcceptedPersistentBytes = layoutDecision?.EstimatedPersistentBytes ?? 0UL,
                    DesignPreset = Kind(volume) switch
                    {
                        VolumeKindAuthored => "simple-authored",
                        VolumeKindRing => "simple-ring",
                        _ => "simple-legacy"
                    },
                    BudgetWarning = !string.IsNullOrEmpty(_lastBudgetWarning)
                        ? _lastBudgetWarning
                        : probeCount > activeProbeBudget / 4 ? "simple-volume-uses-large-fraction-of-probe-budget" : string.Empty
                };
            }

            return entries;
        }

        private SimpleDdgiLayoutVolumeDecision? FindLayoutDecision(int sourceOrdinal)
        {
            SimpleDdgiLayoutReport? report = _lastLayoutReport;
            if (report == null)
                return null;

            IReadOnlyList<SimpleDdgiLayoutVolumeDecision> decisions = report.Volumes;
            for (int i = 0; i < decisions.Count; i++)
            {
                SimpleDdgiLayoutVolumeDecision decision = decisions[i];
                if (decision.Request.SourceOrdinal == sourceOrdinal)
                    return decision;
            }

            return null;
        }

        internal static uint CalculateProbeAgePercentile(
            ReadOnlySpan<uint> ages,
            Span<uint> scratch,
            float percentile)
        {
            if (ages.IsEmpty || scratch.Length < ages.Length)
                return 0u;

            ages.CopyTo(scratch);
            int rank = Math.Clamp((int)Math.Ceiling(Math.Clamp(percentile, 0.0f, 1.0f) * ages.Length) - 1, 0, ages.Length - 1);
            return SelectKthProbeAge(scratch.Slice(0, ages.Length), rank);
        }

        private static uint SelectKthProbeAge(Span<uint> values, int rank)
        {
            int left = 0;
            int right = values.Length - 1;
            while (left < right)
            {
                uint pivot = values[left + ((right - left) >> 1)];
                int lower = left;
                int upper = right;
                while (lower <= upper)
                {
                    while (values[lower] < pivot)
                        lower++;
                    while (values[upper] > pivot)
                        upper--;
                    if (lower > upper)
                        break;

                    (values[lower], values[upper]) = (values[upper], values[lower]);
                    lower++;
                    upper--;
                }

                if (rank <= upper)
                    right = upper;
                else if (rank >= lower)
                    left = lower;
                else
                    return values[rank];
            }

            return values[left];
        }

        internal static uint CalculateProbeAge(uint lastUpdatedFrame, uint currentFrame) =>
            unchecked(currentFrame - lastUpdatedFrame);

        private uint GetProbeAge(int probeIndex) =>
            (uint)probeIndex < (uint)_probeLastUpdatedFrames.Length
                ? CalculateProbeAge(_probeLastUpdatedFrames[probeIndex], _frameIndex)
                : 0u;

        private void FillProbeAgeSnapshot(int firstProbe, Span<uint> destination)
        {
            for (int local = 0; local < destination.Length; local++)
                destination[local] = GetProbeAge(firstProbe + local);
        }

        public void GetEstimatedProbeAgeFrames(out float p50, out float p95, out float maximum)
        {
            int ageCount = Math.Min(_probeCount, _probeLastUpdatedFrames.Length);
            FillProbeAgeSnapshot(
                firstProbe: 0,
                _probeAgeSnapshotScratch.AsSpan(0, ageCount));
            CalculateProbeAgeStatistics(
                _probeAgeSnapshotScratch.AsSpan(0, ageCount),
                _probeAgePercentileScratch.AsSpan(0, ageCount),
                out uint exactP50,
                out uint exactP95,
                out uint exactMaximum);
            p50 = exactP50;
            p95 = exactP95;
            maximum = exactMaximum;
        }

        internal static void CalculateProbeAgeStatistics(
            ReadOnlySpan<uint> ages,
            Span<uint> scratch,
            out uint p50,
            out uint p95,
            out uint maximum)
        {
            p50 = 0u;
            p95 = 0u;
            maximum = 0u;
            if (ages.IsEmpty || scratch.Length < ages.Length)
                return;

            p50 = CalculateProbeAgePercentile(ages, scratch, 0.50f);
            p95 = CalculateProbeAgePercentile(ages, scratch, 0.95f);
            for (int i = 0; i < ages.Length; i++)
            {
                if (ages[i] > maximum)
                    maximum = ages[i];
            }
        }

        private ulong CountScheduledPrimaryRays(int volumeIndex)
        {
            ulong rayCount = 0;
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                GPUSimpleDdgiProbeUpdate update = _updateQueueScratch[queueOffset];
                if (update.VolumeIndex != (uint)volumeIndex)
                    continue;
                if (TransportV2Active && (update.Flags & ProbeUpdateSourceRefreshFlag) == 0u)
                    continue;
                rayCount += Math.Max(1u, (update.Flags >> 16) & 0xffffu);
            }

            return rayCount;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bool heapChanged = !ReferenceEquals(_registeredBindlessHeap, bindlessHeap);
            _registeredBindlessHeap = bindlessHeap;
            SetGpuSchedulerMode(_settings.GlobalIllumination.SimpleDdgiSchedulerMode);
            _gpuScheduler.Register(bindlessHeap);
            _probePageCache.Register(
                bindlessHeap,
                _paramsBuffer,
                Math.Max(MinBufferSize, ParamsBufferSize));
            if (heapChanged)
                _capacityKeyValid = false;
            RegisterBuffers(bindlessHeap);
            UpdateSampledAtlasCapacity(
                _capacityPlan.SampledAtlasPhysicalProbeCapacity,
                priorGenerationComplete:
                    _resourceLastUseFrameFenceValue == 0UL ||
                    _completedFrameFenceValue >= _resourceLastUseFrameFenceValue,
                mirrorAllocationBudgetBytes:
                    ResolveSampledAtlasAllocationBudget(
                        _settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes,
                        _capacityPlan));
            _sampledAtlas?.Register(bindlessHeap);
        }

        private void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.SimpleDdgiParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsBufferSize));
            RegisterIfValid(BindlessIndex.SimpleDdgiIrradianceAtlasBuffer, _irradianceAtlasBuffer, _irradianceAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer, _transportIrradianceAtlasBuffer, _transportIrradianceAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer, _transportSourceCacheBuffer, _transportSourceCacheBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiVisibilityAtlasBuffer, _visibilityAtlasBuffer, _visibilityAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiRayResultScratchBuffer, _rayResultScratchBuffer, _rayScratchBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiProbeStateBuffer, _probeStateBuffer, _probeStateBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiReceiverProbeBuffer, _receiverProbeBuffer, _receiverProbeBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiRelocationClassificationBuffer, _relocationClassificationBuffer, _relocationClassificationBytes);
        }

        /// <summary>
        /// Supplies the renderer's exact graphics-fence progress. Resource
        /// destruction is driven only by these observed completion values;
        /// frame age is diagnostic and never authorizes reclamation.
        /// </summary>
        public void ObserveFrameFenceCompletion(
            ulong lastSubmittedFrameFenceValue,
            ulong completedFrameFenceValue)
        {
            if (lastSubmittedFrameFenceValue > _lastSubmittedFrameFenceValue)
                _lastSubmittedFrameFenceValue = lastSubmittedFrameFenceValue;
            if (completedFrameFenceValue > _completedFrameFenceValue)
                _completedFrameFenceValue = completedFrameFenceValue;

            long retirementStart = Stopwatch.GetTimestamp();
            int destroyed = DrainRetiredResources(force: false);
            _gpuScheduler.CollectRetired(_completedFrameFenceValue);
            _probePageCache.CollectRetired(_completedFrameFenceValue);
            _sampledAtlas?.CollectRetired(_completedFrameFenceValue);
            _uploadCapacityRetiredResourceDestructionMicroseconds +=
                ElapsedMicroseconds(retirementStart);
            _uploadCapacityRetiredResourceDestructionCount += destroyed;
        }

        public void Upload(
            Scene scene,
            Vector3 cameraPosition,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            int frameIndex,
            ulong lightingSignature,
            uint dirtyReasonFlags,
            bool structuredGatherAvailable,
            bool farFieldCoverageAvailable,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions = null,
            bool cohortLightingTransition = false,
            IReadOnlyList<GlobalIlluminationProbeVolume>? authoredSceneVolumes = null,
            ulong sceneContentRevision = 0UL)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
            RenderingConstants.ValidateFrameIndex(frameIndex);

            long uploadStart = Stopwatch.GetTimestamp();
            long layoutMicroseconds = 0;
            long readbackMicroseconds = 0;
            long capacityMicroseconds = 0;
            long invalidationMicroseconds = 0;
            long schedulerRefreshMicroseconds = 0;
            long importanceMicroseconds = 0;
            long queueBuildMicroseconds = 0;
            long lifecycleTelemetryMicroseconds = 0;
            long atlasMaintenanceMicroseconds = 0;
            long gpuUploadMicroseconds = 0;
            _uploadReadbackProbeCount = 0;
            _uploadSchedulerEntryRefreshCount = 0;
            _uploadSchedulerWakeEntryRefreshCount = 0;
            _uploadSchedulerWakeBudgetSaturated = 0;
            _uploadSchedulerFullRebuildCount = 0;
            _uploadVisibilityEntryRefreshCount = 0;
            _uploadStateDirtySlotCount = 0;
            _uploadStateUploadRunCount = 0;
            ResetUploadCapacityTelemetry();
            try
            {
                long phaseStart = Stopwatch.GetTimestamp();
                BeginFrameResourceRetirement();
                ResetFrameCounters();
                UpdateSourceSweepFrameRateEstimate();

                GlobalIlluminationSettings gi = _settings.GlobalIllumination;
                TryCompleteGpuSchedulerFallbackExport();
                UpdateGpuSchedulerReentry(
                    gi.SimpleDdgiSchedulerMode,
                    structuredGatherAvailable);
                SetGpuSchedulerMode(gi.SimpleDdgiSchedulerMode);
                // The manager receives the live per-frame capability result from
                // VulkanRenderer. A resident arena may remain allocated while a
                // frame lacks an active ray-query/structured-gather producer,
                // but no scheduler or indirect consumer may run against stale
                // commands in that case.
                _gpuSchedulerFrameExecutionAvailable =
                    !_schedulerMode.IsGpuMode() ||
                    (!_gpuSchedulerFallbackLatched &&
                        structuredGatherAvailable &&
                        (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident ||
                         _gpuResidentProbeStateBootstrapped));
                _gpuScheduler.CollectRetired(_completedFrameFenceValue);
                bool enabled = gi.EffectiveUseDdgi;
                layoutMicroseconds += ElapsedMicroseconds(phaseStart);
                if (!enabled)
                {
                    phaseStart = Stopwatch.GetTimestamp();
                    DisableCore(gi, stagingRing, commandBuffer);
                    gpuUploadMicroseconds += ElapsedMicroseconds(phaseStart);
                    return;
                }

                phaseStart = Stopwatch.GetTimestamp();
                BoundingBox sceneBounds = ExpandBounds(
                    ResolveSceneBounds(scene, sceneContentRevision),
                    gi.SimpleDdgiRingBaseSpacing * 1.5f);
                int previousProbeCount = _probeCount;
                int previousVolumeCount = _volumeCount;
                CapturePreviousVolumes();
                BuildVolumeTable(
                    gi,
                    sceneBounds,
                    cameraPosition,
                    structuredGatherAvailable,
                    authoredSceneVolumes);
                layoutMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                bool volumeTableRemapped = VolumeTableRemapped(previousProbeCount, previousVolumeCount);
                bool incompatibleTopologyStateReset = false;
                if (volumeTableRemapped)
                    AdvanceVolumeTableGenerationAndDropPendingReadbacks();
                invalidationMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                // A topology remap can be an ordinary cell-aligned toroidal
                // scroll. Compatible cell-aligned movement keeps private
                // scheduler state and persistent lane cursors. An incompatible
                // topology is a bootstrap boundary for the affected records.
                bool residentBootstrap = _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    !_gpuResidentProbeStateBootstrapped;
                bool residentCpuStateCapacityRefresh =
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    (residentBootstrap || volumeTableRemapped);
                if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident ||
                    residentCpuStateCapacityRefresh)
                    EnsureCpuProbeStateCapacity(_probeCount);
                if (volumeTableRemapped)
                    incompatibleTopologyStateReset =
                        ResetIncompatibleTopologyProbeState(previousVolumeCount);
                long cpuStateCapacityMicroseconds = ElapsedMicroseconds(phaseStart);
                capacityMicroseconds += cpuStateCapacityMicroseconds;
                _uploadCapacityCpuProbeStateMicroseconds += cpuStateCapacityMicroseconds;

                bool cpuFallbackFreshReset =
                    _schedulerMode == SimpleDdgiSchedulerMode.CpuReference &&
                    _gpuSchedulerFallbackFreshResetPending;
                if (cpuFallbackFreshReset)
                {
                    PrepareCpuFreshResetFallback();
                    _gpuSchedulerFallbackFreshResetPending = false;
                }

                phaseStart = Stopwatch.GetTimestamp();
                if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
                    ReadCompletedProbeStateReadback(frameIndex);
                readbackMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                bool hasRegionalDirtyWork = gi.SimpleDdgiRegionalInvalidationEnabled && dirtyRegions is { Count: > 0 };
                bool requiresGlobalInvalidation = RequiresGlobalInvalidation(dirtyRegions);
                // The dispatch allocation uses the largest selected ring profile;
                // each queue item packs its own active ray count so mid/far rings do
                // not perform near-ring work.
                _raysPerProbe = ResolveMaximumRingFullRays(gi);
                bool sourceCacheCapacityWillChange =
                    _transportSourceCacheRayCapacity != Math.Max(1, _raysPerProbe);
                UpdateLightingDirtyState(
                    gi,
                    lightingSignature,
                    dirtyReasonFlags,
                    suppressSignatureBoost: hasRegionalDirtyWork && !requiresGlobalInvalidation,
                    cohortLightingTransition);
                UpdateTransportV2ActivationState(sourceCacheCapacityWillChange);
                UpdateTransportCalibrationState(gi, sourceCacheCapacityWillChange);
                if (_recenteredThisFrame)
                {
                    _totalRecenterCount++;
                    _framesSinceLastRecenter = 0;
                }
                if (ProbeResidencyGeometryChanged(
                        dirtyReasonFlags,
                        dirtyRegions,
                        _recenteredThisFrame))
                {
                    _probeResidencyGeometryGeneration =
                        AdvancePackedGeometryGeneration(
                            _probeResidencyGeometryGeneration);
                }
                invalidationMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                int baseUpdateBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                    ? _probeCount
                    : Math.Min(_probeCount, gi.SimpleDdgiProbeUpdatesPerFrame);
                _schedulerRoutineWakeRefreshBudget =
                    ResolveRoutineSchedulerWakeRefreshBudget(baseUpdateBudget);
                // Lighting-dirty recovery has an explicit deterministic 2x
                // allowance. Publish that allowance as the hard configured cap even
                // on frames that do not consume it, so diagnostics never redefine a
                // tier from current queue output or reject intentional recovery work.
                int authoredConfiguredRequestBudget = ResolveConfiguredRequestBudget(
                    baseUpdateBudget,
                    _probeCount,
                    gi.SimpleDdgiLightingDirtyBoostEnabled);
                _schedulerSourceRequestBudget = ResolveTransportV2RequestBudget(
                    authoredConfiguredRequestBudget,
                    _raysPerProbe,
                    TransportV2Active);
                _schedulerConfiguredRequestBudget =
                    ResolveTransportV2SchedulerRequestCapacity(
                        authoredConfiguredRequestBudget,
                        _raysPerProbe,
                        TransportV2Active,
                        TailCertificationEnabled && TransportAccelerationEnabled);
                int dirtyBoostedBudget = ResolveLightingDirtyUpdateBudget(gi, baseUpdateBudget);
                // Atlas growth can invalidate every physical slot. Establish storage
                // first so MarkFreshForNewOrScrolledProbes observes that invalidation;
                // transient feedback throttling must not change persistent capacity.
                if (!EnsureCapacity(
                        _probeCount,
                        _raysPerProbe,
                        _schedulerConfiguredRequestBudget,
                        commandBuffer))
                {
                    // Keep the prior generation alive and publish a disabled
                    // control header for this submission. The next frame
                    // retries after normal fence progress; no undersized
                    // resource is exposed to the new topology.
                    DisableCore(gi, stagingRing, commandBuffer);
                    return;
                }
                if (_capacityPlan.ResidencyMode.CollectsDemand() &&
                    SimpleDdgiProbePageLayout
                        .DemandEpochRequiresResourceTransaction(_frameIndex) &&
                    _lastProbeResidencyDemandEpochResetFrame != _frameIndex)
                {
                    _probePageCache.RequireFreshTransactionForDemandEpochWrap();
                    _lastProbeResidencyDemandEpochResetFrame = _frameIndex;
                }
                int maximumPageAdmissions = Math.Min(
                    gi.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    _capacityPlan.SparsePhysicalPageCapacity);
                bool residencyArenaTransition =
                    _probePageCache.RequiresReplacement(
                        _capacityPlan.ResidencyMode,
                        _capacityPlan.SparseVirtualPageCount,
                        _capacityPlan.SparsePhysicalPageCapacity,
                        maximumPageAdmissions,
                        gi.SimpleDdgiSparseRetentionFrames,
                        gi.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                        gi.SimpleDdgiSparseInactiveRetryFrames);
                if (residencyArenaTransition &&
                    !EnsureBindlessDescriptorReadersComplete(
                        commandBuffer,
                        "Simple DDGI residency arena generation publication"))
                {
                    MarkResourcesUsedByPendingSubmission();
                    DisableCore(gi, stagingRing, commandBuffer);
                    return;
                }
                bool residencyArenaReplaced =
                    _probePageCache.EnsureCapacity(
                        _capacityPlan.ResidencyMode,
                        _capacityPlan.SparseVirtualPageCount,
                        _capacityPlan.SparsePhysicalPageCapacity,
                        maximumPageAdmissions,
                        gi.SimpleDdgiSparseRetentionFrames,
                        gi.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                        gi.SimpleDdgiSparseInactiveRetryFrames,
                        commandBuffer,
                        _resourceLastUseFrameFenceValue);
                if (residencyArenaReplaced)
                {
                    // Feedback belongs to one immutable arena generation and
                    // mode. In particular, Sparse -> Dense releases the arena
                    // without minting a replacement generation, so retaining
                    // the prior validity bit would expose stale sparse owners
                    // through Dense diagnostics until another sparse arena was
                    // created. A new transaction must earn a new fence-complete
                    // summary before any consumer can trust page counts again.
                    _probeResidencyFeedbackValid = false;
                    _lastProbeResidencyFeedback = default;
                    _probeResidencyFeedbackFrameSerial = 0UL;
                    _probeResidencyBootstrapClassificationActive =
                        _capacityPlan.ResidencyMode.UsesSparsePayloads();
                }
                if (_probePageCache.BootstrapRequired)
                {
                    GPUSimpleDdgiResidencyHeader residencyHeader =
                        CreateResidencyHeader(gi);
                    _probePageCache.UploadBootstrap(
                        stagingRing,
                        commandBuffer,
                        residencyHeader,
                        new ReadOnlySpan<GPUSimpleDdgiVolumePaging>(
                            _volumePagingScratch,
                            0,
                            _volumeCount));
                }
                _probePageCache.UploadPendingDevelopmentControl(
                    stagingRing,
                    commandBuffer);
                if (_probeResidencyMutationUnavailable &&
                    _capacityPlan.ResidencyMode.CollectsDemand())
                {
                    _probePageCache.FreezeForRuntimeFailure(
                        residencyStateValid: _probePageCache.ResidencyValid,
                        reason: _probeResidencyMutationFailureReason);
                }
                if (residencyArenaReplaced &&
                    _capacityPlan.ResidencyMode.UsesSparsePayloads())
                {
                    // The new compact payload transaction starts empty and
                    // fail-closed. Scheduler publication must bootstrap every
                    // admitted fine page; no old virtual freshness can certify
                    // the new physical owners.
                    residentBootstrap = true;
                    _gpuResidentProbeStateBootstrapped = false;
                    _gpuSchedulerFrameExecutionAvailable = false;
                    _gpuSchedulerFeedbackValid = false;
                }
                if (volumeTableRemapped)
                    QueueReceiverInvalidationsForVolumeRemap();
                if (_schedulerMode.IsGpuMode() && _probeCount > 0)
                {
                    bool schedulerArenaReplaced = _gpuScheduler.EnsureCapacity(
                        _probeCount,
                        Math.Clamp(_schedulerConfiguredRequestBudget, 0, _probeCount),
                        _volumeCount,
                        SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity,
                        _context.ValidationSettings.Mode != RendererValidationMode.Off,
                        _resourceLastUseFrameFenceValue);
                    if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                        schedulerArenaReplaced)
                    {
                        // A replacement arena contains undefined private
                        // scheduler state even when the public probe buffer was
                        // reused. Force the distinct bootstrap ABI before any
                        // resident dispatch can become graph-visible.
                        residentBootstrap = true;
                        _gpuResidentProbeStateBootstrapped = false;
                        _gpuSchedulerFrameExecutionAvailable = false;
                        _gpuSchedulerFeedbackValid = false;
                        _transportResidentParticipantCount = 0;
                        _transportResidentSourceRepairProbeCount = 0;
                        EnsureCpuProbeStateCapacity(_probeCount);
                    }
                }
                if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    (incompatibleTopologyStateReset || _probeStateUploadRequired))
                {
                    // The public probe ABI must receive new non-zero physical
                    // generations for incompatible slots before classification
                    // can admit them. Compatible toroidal scrolls remain GPU
                    // owned and do not enter this branch.
                    residentBootstrap = true;
                    _gpuResidentProbeStateBootstrapped = false;
                    _gpuSchedulerFrameExecutionAvailable = false;
                    _gpuSchedulerFeedbackValid = false;
                    _transportResidentParticipantCount = 0;
                    _transportResidentSourceRepairProbeCount = 0;
                }
                if (_gpuSchedulerFallbackExportRequested)
                    TryRecordGpuSchedulerFallbackExport(commandBuffer);
                MarkResourcesUsedByPendingSubmission();
                capacityMicroseconds += ElapsedMicroseconds(phaseStart);

                if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                {
                    phaseStart = Stopwatch.GetTimestamp();
                    UploadGpuResidentFrame(
                        gi,
                        cameraPosition,
                        dirtyRegions,
                        dirtyReasonFlags,
                        structuredGatherAvailable,
                        farFieldCoverageAvailable,
                        cohortLightingTransition,
                        stagingRing,
                        commandBuffer,
                        residentBootstrap,
                        volumeTableRemapped);
                    gpuUploadMicroseconds += ElapsedMicroseconds(phaseStart);
                    _frameIndex++;
                    return;
                }

                phaseStart = Stopwatch.GetTimestamp();
                _schedulerCameraPosition = cameraPosition;
                MarkFreshForNewOrScrolledProbes();
                if (hasRegionalDirtyWork)
                {
                    MarkRegionalDirtyProbes(dirtyRegions!);
                }
                if (ShouldBeginTransportGlobalConvergenceForInvalidation(
                        TransportV2Active,
                        _newlyInvalidatedProbeCount,
                        hasRegionalDirtyWork,
                        requiresGlobalInvalidation,
                        _atlasFresh,
                        _recenteredThisFrame))
                {
                    BeginTransportGlobalConvergence();
                }
                invalidationMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                if (TransportV2Active)
                    PrepareTransportGlobalConvergenceState();
                schedulerRefreshMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                int visibleFreshRecoveryBudget = RefreshProbeSchedulingImportance();
                importanceMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                // Readback and camera-visibility dirties target the same bounded
                // probe set. Refresh importance first, then rebuild each scheduler
                // entry once with its final visibility state. The previous order
                // refreshed scheduler entries before and after importance, doubling
                // heap/queue churn whenever visibility changed.
                RefreshPersistentSchedulerState();
                if (TransportV2Active)
                    EvaluateTransportGlobalConvergenceState();
                schedulerRefreshMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                int frameHardBudget = ResolveTransportV2FrameRequestBudget(
                    dirtyBoostedBudget,
                    _schedulerSourceRequestBudget,
                    _schedulerConfiguredRequestBudget,
                    TransportV2Active,
                    TransportAccelerationSolveActive);
                int updateBudget = ResolveFeedbackLimitedUpdateBudget(
                    frameHardBudget,
                    visibleFreshRecoveryBudget);
                _schedulerEffectiveRequestBudget = updateBudget;
                ResolveSourceRefreshThroughputTarget(updateBudget);
                RefreshSourceStepAgeTelemetry();
                _probesToUpdate = BuildUpdateQueue(updateBudget);
                BuildRayDispatchBatches();
                queueBuildMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                RefreshProbeLifecycleTelemetry(updateBudget);
                lifecycleTelemetryMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                BeginUpdateTransaction(_probesToUpdate > 0);
                _updateStartProbe = _probesToUpdate > 0 ? (int)_updateQueueScratch[0].ProbeIndex : 0;
                if (_probesToUpdate >= _probeCount)
                {
                    _fullRefreshFrameCount++;
                    _fullRefreshProbeCount = _probeCount;
                }
                else
                {
                    _partialRefreshFrameCount++;
                    // V2 records only completed periodic source resamples in
                    // AddProbeUpdate. Legacy mode has no separate source-cache
                    // lifetime, so retain its historical queue-wide classification.
                    if (!TransportV2Active)
                        _ageRefreshProbeCount = _probesToUpdate;
                }

                AnnotateVolumeUpdateRanges();
                queueBuildMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                PreserveToroidalAtlasData();
                ClearAtlasBuffersIfRequired(commandBuffer);
                SynchronizeSampledAtlasIfRequired(commandBuffer);
                UploadReceiverProbeInvalidations(stagingRing, commandBuffer);
                atlasMaintenanceMicroseconds += ElapsedMicroseconds(phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                // The push-constant fallback represents sky radiance seen by probe
                // transport when no environment cubemap is available.  Forward
                // diffuse IBL remains independently controlled by DiffuseIntensity.
                float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.SkyIntensity : 0.0f;
                // Fresh probes already force zero history in the blend shader. Do not
                // discard history for every other probe just because an atlas update
                // introduced a smaller set of fresh slots.
                // V2 treats the atlas as an explicit Jacobi field rather than a
                // slowly decaying temporal-history buffer.  The blend shader derives
                // its retention from this relaxation; V1 retains the legacy value.
                float hysteresis = gi.SimpleDdgiTransportV2Enabled
                    ? 1.0f - gi.SimpleDdgiTransportSolverRelaxation
                    : gi.SimpleDdgiHysteresis;
                GPUSimpleDdgiVolume firstVolume = _volumeCount > 0 ? _volumeScratch[0] : default;
                _lastParams = new GPUSimpleDdgiParams
                {
                    GridOriginAndSpacing = firstVolume.OriginAndSpacing,
                    GridCountsAndProbeCount = new Vector4(
                        firstVolume.GridCountsAndFirstProbe.X,
                        firstVolume.GridCountsAndFirstProbe.Y,
                        firstVolume.GridCountsAndFirstProbe.Z,
                        _probeCount),
                    AtlasTexelsAndRayCount = new Vector4(IrradianceTexelsPerProbe, VisibilityTexelsPerProbe, _raysPerProbe, gi.FarFieldClipmapResolution),
                    HysteresisFrameAndFlags = new Vector4(
                        hysteresis,
                        PackHeaderWord(_frameIndex),
                        PackHeaderWord(BuildFlags(
                            gi,
                            enabled,
                            structuredGatherAvailable,
                            farFieldCoverageAvailable)),
                        gi.FarFieldStartDistance),
                    EnvironmentRadianceAndIntensity = new Vector4(
                        _settings.Environment.TransportFallbackRadiance.X,
                        _settings.Environment.TransportFallbackRadiance.Y,
                        _settings.Environment.TransportFallbackRadiance.Z,
                        environmentIntensity),
                    // W scales only the environment complement for missing probe
                    // ownership (at receivers and bounce hit points). Valid probe
                    // transport, including trace misses, is intentionally unaffected.
                    ProbeUpdateRange = new Vector4(_updateStartProbe, _probesToUpdate, _volumeCount, gi.EnvironmentFallbackIntensity),
                    DebugAndBias = new Vector4(ResolveSimpleDdgiDebugViewMode(gi.DebugView), gi.DdgiSelfShadowBiasScale, gi.IndirectIntensity, gi.FarFieldMaxTraceSteps),
                    RotationQuaternion = BuildFrameRotation(_frameIndex),
                    BiasAndPadding = new Vector4(gi.SimpleDdgiNormalBias, gi.SimpleDdgiViewBias, gi.SimpleDdgiHysteresisChangeThreshold, gi.SimpleDdgiHysteresisStepThreshold),
                    // yzw describe the optional sampled-atlas mirror.  Keep the
                    // source SSBO atlas layout stable so the image path can fall back
                    // per sample at octahedral seams without changing writer shaders.
                    Reserved0 = new Vector4(
                        _volumeCount,
                        SampledAtlasActive ? _sampledAtlas!.LayersPerTexture : 0,
                        SampledAtlasActive ? _sampledAtlas!.GroupCount : 0,
                        SampledAtlasActive ? 1.0f : 0.0f),
                    BiasLimitsAndPadding = new Vector4(
                        gi.SimpleDdgiMaximumWorldBiasMeters,
                        gi.SimpleDdgiArchitecturalThicknessMeters,
                        gi.DdgiThinWallPolicyEnabled
                            ? gi.DdgiThinWallLeakClampStrength
                            : 0.0f,
                        SampledAtlasActive ? _sampledAtlas!.ProbeCapacity : 0),
                    TransportAndAtlasIndices = new Vector4(
                        PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                        PackHeaderWord(gi.SimpleDdgiTransportV2Enabled
                            ? (uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer
                            : (uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                        PackHeaderWord(gi.SimpleDdgiTransportV2Enabled
                            ? (uint)BindlessIndex.SimpleDdgiTransportSourceCacheBuffer
                            : 0u),
                        PackHeaderWord(_transportGeneration)),
                    TransportControls = new Vector4(
                        gi.SimpleDdgiTransportSolverRelaxation,
                        gi.SimpleDdgiTransportAlbedoClamp,
                        gi.SimpleDdgiTransportTailRelativeTolerance,
                        gi.SimpleDdgiTransportAcceleratedSweepCount),
                    ResidencyAndCounts = BuildResidencyAndCounts(),
                    ResidencyControls = BuildResidencyControls()
                };

                if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                {
                    // GPU admission owns the current transaction. The legacy
                    // params header must not retain a previous CPU queue range
                    // that a consumer could accidentally dispatch.
                    _lastParams.ProbeUpdateRange = new Vector4(
                        0.0f,
                        0.0f,
                        _volumeCount,
                        gi.EnvironmentFallbackIntensity);
                    for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                    {
                        GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                        volume.UpdateStartAndCount = new Vector4(
                            0.0f,
                            0.0f,
                            ResolveVolumeQuality(volumeIndex).FullRays,
                            0.0f);
                        _volumeScratch[volumeIndex] = volume;
                    }
                }

                UploadParams(stagingRing, commandBuffer);
                _controlHeaderInitialized = true;
                _wasSimpleDdgiEnabled = true;
                bool residentStateNeedsUpload = _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    (!_gpuResidentProbeStateBootstrapped || _probeStateUploadRequired || _probeStateDirtySlots.Count > 0);
                if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident || residentStateNeedsUpload)
                {
                    UploadProbeState(stagingRing, commandBuffer);
                }
                if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
                    UploadProbeUpdateQueue(stagingRing, commandBuffer);
                UploadGpuSchedulerFrame(
                    gi,
                    cameraPosition,
                    dirtyRegions,
                    dirtyReasonFlags,
                    structuredGatherAvailable,
                    farFieldCoverageAvailable,
                    cohortLightingTransition,
                    stagingRing,
                    commandBuffer);
                gpuUploadMicroseconds += ElapsedMicroseconds(phaseStart);
                if (_atlasClearedThisFrame)
                {
                    _totalAtlasClearCount++;
                    _framesSinceLastClear = 0;
                }
                else if (_framesSinceLastClear != int.MaxValue)
                {
                    _framesSinceLastClear++;
                }

                if (!_recenteredThisFrame && _framesSinceLastRecenter != int.MaxValue)
                    _framesSinceLastRecenter++;
                _frameIndex++;
            }
            finally
            {
                long totalMicroseconds = ElapsedMicroseconds(uploadStart);
                _lastUploadMicroseconds = totalMicroseconds;
                long classifiedMicroseconds =
                    layoutMicroseconds +
                    readbackMicroseconds +
                    capacityMicroseconds +
                    invalidationMicroseconds +
                    schedulerRefreshMicroseconds +
                    importanceMicroseconds +
                    queueBuildMicroseconds +
                    lifecycleTelemetryMicroseconds +
                    atlasMaintenanceMicroseconds +
                    gpuUploadMicroseconds;
                _lastUploadTiming = new SimpleDdgiUploadTiming
                {
                    TotalMicroseconds = totalMicroseconds,
                    LayoutMicroseconds = layoutMicroseconds,
                    ReadbackMicroseconds = readbackMicroseconds,
                    CapacityMicroseconds = capacityMicroseconds,
                    InvalidationMicroseconds = invalidationMicroseconds,
                    SchedulerRefreshMicroseconds = schedulerRefreshMicroseconds,
                    ImportanceMicroseconds = importanceMicroseconds,
                    QueueBuildMicroseconds = queueBuildMicroseconds,
                    LifecycleTelemetryMicroseconds = lifecycleTelemetryMicroseconds,
                    AtlasMaintenanceMicroseconds = atlasMaintenanceMicroseconds,
                    BufferUploadMicroseconds = gpuUploadMicroseconds,
                    OtherMicroseconds = Math.Max(0, totalMicroseconds - classifiedMicroseconds),
                    ReadbackProbeCount = _uploadReadbackProbeCount,
                    SchedulerEntryRefreshCount = _uploadSchedulerEntryRefreshCount,
                    SchedulerWakeEntryRefreshCount = _uploadSchedulerWakeEntryRefreshCount,
                    SchedulerWakeRefreshBudget = _schedulerRoutineWakeRefreshBudget,
                    SchedulerWakeBudgetSaturated = _uploadSchedulerWakeBudgetSaturated,
                    SchedulerFullRebuildCount = _uploadSchedulerFullRebuildCount,
                    VisibilityEntryRefreshCount = _uploadVisibilityEntryRefreshCount,
                    StateDirtySlotCount = _uploadStateDirtySlotCount,
                    StateUploadRunCount = _uploadStateUploadRunCount,
                    CapacityDetails = CreateUploadCapacityTiming()
                };
            }
        }

        /// <summary>
        /// Publishes a disabled simple-DDGI params header once at startup and
        /// whenever another GI implementation takes ownership. No probe layout,
        /// scheduler, trace, blend, or gather work is performed.
        /// </summary>
        public void EnsureDisabled(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
            if (_controlHeaderInitialized && !_wasSimpleDdgiEnabled)
                return;

            long uploadStart = Stopwatch.GetTimestamp();
            ResetUploadCapacityTelemetry();
            try
            {
                BeginFrameResourceRetirement();
                ResetFrameCounters();
                DisableCore(_settings.GlobalIllumination, stagingRing, commandBuffer);
            }
            finally
            {
                long totalMicroseconds = ElapsedMicroseconds(uploadStart);
                _lastUploadMicroseconds = totalMicroseconds;
                _lastUploadTiming = new SimpleDdgiUploadTiming
                {
                    TotalMicroseconds = totalMicroseconds,
                    BufferUploadMicroseconds = totalMicroseconds,
                    CapacityDetails = CreateUploadCapacityTiming()
                };
            }
        }

        private void DisableCore(
            GlobalIlluminationSettings settings,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            // Disabled DDGI owns no scheduler arena or feedback readback. Keep
            // the serialized mode intact; Upload will re-enter it on the first
            // enabled frame after the bounded bootstrap.
            SetGpuSchedulerMode(SimpleDdgiSchedulerMode.CpuReference);
            _gpuScheduler.CollectRetired(_completedFrameFenceValue);
            _volumeCount = 0;
            _probeCount = 0;
            _lastLayoutReport = null;
            _probesToUpdate = 0;
            _activeProbeCount = 0;
            _classifiedInactiveProbeCountEstimate = 0;
            _probeRelocationCount = 0;
            _relocationFractionSumEstimate = 0.0f;
            _averageRelocationFractionEstimate = 0.0f;
            _probeStateReadbackValid = 0;
            _probeConvergenceReadbackValid = 0;
            _hasGridOrigin = false;
            // A feature/backend toggle invalidates transport data, not the
            // world-space lattice phase. Re-seeding camera-ring origins from
            // the current scene bounds makes an unchanged camera sample a
            // different probe lattice after re-enable. Keep the three bounded
            // anchors; BuildVolumeTable will still recenter them by integral
            // cell deltas when the camera or scene coverage actually moved.
            _atlasClearRequired = true;
            _atlasFresh = true;
            AbortUpdateTransaction();
            for (int i = 0; i < _probeStateReadbackRecorded.Length; i++)
                DropProbeStateReadbackSlot(i);
            // Disabled features own only the graph-safe minimum buffers. This is
            // a deliberate generation transition, so stale DDGI, readback, and
            // sampled-atlas capacity cannot consume the global memory budget.
            EnsureCapacity(0, 1, 0, commandBuffer);
            ClearReceiverProbeBufferIfRequired(commandBuffer);
            Array.Clear(_volumeScratch);
            Array.Clear(_volumePurposes);
            Array.Clear(_volumePriorities);
            Array.Clear(_probeDirtyLatencyStates);
            Array.Clear(_probeDirtyLatencyStartFrames);
            Array.Clear(_probeSchedulingFlags);
            Array.Clear(_probeAtmosphereCohortFlags);
            Array.Clear(_probeDirtyReasons);
            Array.Clear(_probeRoutineMaintenancePending);
            Array.Clear(_probeVisibilityImportance);
            Array.Clear(_probeVisibilityValid);
            Array.Clear(_probeSourceLightingGenerations);
            Array.Clear(_probeLastSourceRefreshFrames);
            Array.Clear(_probeSourceEpochs);
            Array.Clear(_probeSourceRayCounts);
            Array.Clear(_probeTransportGenerationCounts);
            _schedulerWorkQueues.Clear();
            _schedulerSourceRefreshQueues.Clear();
            _schedulerCachedSolverQueues.Clear();
            _schedulerWakeHeap.Clear();
            _schedulerFreshAgeHeap.Clear();
            _schedulerScrollExposedAgeHeap.Clear();
            _schedulerRelocationPendingAgeHeap.Clear();
            _schedulerUnpublishedAgeHeap.Clear();
            _schedulerVisibleUnsupportedAgeHeap.Clear();
            _schedulerGenerationStaleAgeHeap.Clear();
            _schedulerVisibleUnsupportedAgeHistogram.Clear(_frameSerial);
            _schedulerVisiblePendingAgeHistogram.Clear(_frameSerial);
            _schedulerGenerationStaleAgeHistogram.Clear(_frameSerial);
            Array.Clear(_probeSchedulerTransportStates);
            Array.Clear(_probeAtmosphereCohortFlags);
            Array.Fill(_probeSchedulerVolumeIndices, byte.MaxValue);
            Array.Clear(_probeSchedulerLifecycleStates);
            Array.Clear(_probeSchedulerTrackedLastUpdatedFrames);
            Array.Clear(_probeSchedulerTrackedSourceRefreshFrames);
            _schedulerParticipatingProbeCount = 0;
            _transportResidentParticipantCount = 0;
            _transportResidentSourceRepairProbeCount = 0;
            _schedulerAtmosphereVisibleParticipatingProbeCount = 0;
            _schedulerAtmosphereVisibleSourceReadyProbeCount = 0;
            _schedulerAtmosphereVisiblePublishedProbeCount = 0;
            Array.Clear(_volumeAtmosphereParticipatingProbeCounts);
            Array.Clear(_volumeAtmosphereRayCounts);
            _schedulerSourceRepairProbeCount = 0;
            _schedulerRoutineSourceRepairProbeCount = 0;
            _schedulerRoutineMaintenancePendingProbeCount = 0;
            _schedulerPendingConvergenceProbeCount = 0;
            _schedulerInactiveDeferredProbeCount = 0;
            _schedulerInactiveDeferredSavedPrimaryRayCount = 0UL;
            _schedulerGlobalStateValid = false;
            _schedulerRebuildRequired = true;
            _schedulerVisibilityFullRefreshRequired = true;
            _hasSchedulerVisibilityCamera = false;
            _probeVisibilityDirtyCount = 0;
            Array.Clear(_probeVisibilityDirty);
            Array.Clear(_probeVisibleFreshCounted);
            Array.Clear(_volumeVisibleFreshProbeCounts);
            _hasTransportCalibrationSignatures = false;
            _transportV2WasActive = false;
            BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
            _dirtyLatencyOutstandingEventCount = 0;
            _lastParams = CreateDisabledParams(settings);
            UploadParams(stagingRing, commandBuffer);
            _controlHeaderInitialized = true;
            _wasSimpleDdgiEnabled = false;
            _frameIndex++;
        }

        public void MarkPublishExecuted()
        {
            if (!CanExecutePublishTransaction)
                return;

            // Every accepted CPU/GPU-mirror update reaches the compact record
            // publisher in both V1 and V2. Keep this receiver-specific counter
            // independent of the V2 transport-generation telemetry below.
            _currentReceiverRecordsPublishedCount = _probesToUpdate;

            if (TransportV2Active && _probesToUpdate > 0)
            {
                _currentTransportPublishedProbeCount = _probesToUpdate;
                // GPU publication has no CPU-built transfer regions.
                _currentTransportPublishRegionCount = 0;
                _transportPublishedProbeTotal = SaturatingAdd(
                    _transportPublishedProbeTotal,
                    (ulong)_probesToUpdate);
                _transportGeneration = AdvanceSourceLightingGeneration(_transportGeneration);
            }

            // A propagation generation becomes externally visible only after
            // its update transaction reaches the GPU publication point. Keep
            // an in-flight global solve out of the atmosphere admission view.
            if (TransportV2Active && !TransportGlobalConvergencePending)
                _publishedPropagationGeneration = _transportGeneration;

            if (_probesToUpdate > 0)
            {
                uint completedFrame = unchecked(_frameIndex - 1u);
                if (TransportV2Active &&
                    !TransportGlobalConvergencePending &&
                    !_transportPeriodicSourceRefreshWavePending)
                {
                    for (int i = 0; i < _probesToUpdate; i++)
                    {
                        GPUSimpleDdgiProbeUpdate candidate = _updateQueueScratch[i];
                        int candidateProbeIndex = checked((int)candidate.ProbeIndex);
                        if ((candidate.Flags & ProbeUpdateSourceRefreshFlag) != 0u &&
                            (uint)candidateProbeIndex < (uint)_probeCount &&
                            IsRoutinePeriodicTransportSourceRefresh(candidateProbeIndex))
                        {
                            _transportPeriodicSourceRefreshWavePending = true;
                            _transportPeriodicSourceRefreshWaveCutoffFrame = completedFrame;
                            break;
                        }
                    }
                }

                for (int i = 0; i < _probesToUpdate; i++)
                {
                    GPUSimpleDdgiProbeUpdate update = _updateQueueScratch[i];
                    int probeIndex = checked((int)update.ProbeIndex);
                    if ((uint)probeIndex < (uint)_probeFresh.Length)
                    {
                        bool relocationPublicationPending =
                            (uint)probeIndex < (uint)_probeRelocationPending.Length &&
                            _probeRelocationPending[probeIndex] != 0;
                        bool relocationTimedOut = ShouldRetireRelocationPendingOnCpu(
                            relocationPublicationPending,
                            ReadProbeUpdateAge(update.Reserved0));
                        if (relocationTimedOut)
                        {
                            // The relocation shader deterministically performs
                            // this same transition for the current physical
                            // generation. Mirror it at transaction completion so
                            // a continuously refreshed source epoch cannot keep
                            // every in-flight readback stale and leave the CPU
                            // scheduler believing the pending bit is permanent.
                            _probeRelocationPending[probeIndex] = 0;
                            if (_probeInactive[probeIndex] == 0)
                            {
                                _activeProbeCount = Math.Max(0, _activeProbeCount - 1);
                                _classifiedInactiveProbeCountEstimate = Math.Min(
                                    _probeCount,
                                    _classifiedInactiveProbeCountEstimate + 1);
                            }
                            _probeInactive[probeIndex] = 1;
                            _probeActiveWeights[probeIndex] = 0.0f;
                            _probeClassifications[probeIndex] = 1u;
                            relocationPublicationPending = false;
                        }
                        _probeFresh[probeIndex] =
                            relocationPublicationPending ? (byte)1 : (byte)0;
                        bool visibilityRefreshed =
                            !TransportV2Active ||
                            (update.Flags & (ProbeUpdateSourceRefreshFlag |
                                ProbeStateFreshFlag)) != 0u;
                        if ((uint)probeIndex < (uint)_probeVisibilityValid.Length)
                        {
                            if (relocationPublicationPending ||
                                _probeInactive[probeIndex] != 0)
                            {
                                _probeVisibilityValid[probeIndex] = 0;
                            }
                            else if (visibilityRefreshed)
                            {
                                _probeVisibilityValid[probeIndex] = 1;
                            }
                        }
                        if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length)
                        {
                            _probeSchedulingFlags[probeIndex] = relocationPublicationPending
                                ? (byte)(_probeSchedulingFlags[probeIndex] & ProbeSchedulingVisibleFlag)
                                : (byte)0;
                        }
                        if ((uint)probeIndex < (uint)_probeDirtyReasons.Length)
                            _probeDirtyReasons[probeIndex] = 0;
                        if (!relocationPublicationPending)
                            _probeLastUpdatedFrames[probeIndex] = completedFrame;
                        if (TransportV2Active)
                        {
                            bool sourceRefresh = (update.Flags & ProbeUpdateSourceRefreshFlag) != 0u;
                            if (sourceRefresh)
                            {
                                // Classify the refresh before publishing its new source age.
                                // Routine cache maintenance preserves the compatible warm-start
                                // field and must only wake the refreshed probe and its dependants;
                                // promoting every periodic refresh to a field-wide barrier makes
                                // a steady capture oscillate permanently between converged and
                                // globally pending states.
                                bool routineSourceRefresh =
                                    (update.Flags & ProbeUpdateRoutineSourceRefreshFlag) != 0u;
                                if (_currentCompletedSourceRefreshProbeCount < int.MaxValue)
                                    _currentCompletedSourceRefreshProbeCount++;
                                bool sourceGenerationBoundary =
                                    IsTransportSourceGenerationBoundary(
                                        true,
                                        _probeSourceLightingGenerations[probeIndex],
                                        update.SourceLightingGeneration,
                                        _sourceLightingGeneration,
                                        (update.Flags & ProbeStateFreshFlag) != 0u);
                                bool wakePropagationNeighborhood =
                                    ShouldWakeTransportPropagationNeighborhood(
                                        true,
                                        true,
                                        TransportGlobalConvergencePending);
                                if ((uint)probeIndex < (uint)_probeSourceEpochs.Length)
                                {
                                    uint committedSourceEpoch = update.SourceEpoch != 0u
                                        ? update.SourceEpoch
                                        : AdvanceSourceEpoch(_probeSourceEpochs[probeIndex]);
                                    SetProbeSourceEpoch(probeIndex, committedSourceEpoch);
                                }
                                _probeSourceLightingGenerations[probeIndex] = update.SourceLightingGeneration == 0u
                                    ? _sourceLightingGeneration
                                    : update.SourceLightingGeneration;
                                _probeLastSourceRefreshFrames[probeIndex] = completedFrame;
                                uint packedRayCount = (update.Flags >> 16) & 0xffffu;
                                int recordedSourceRayCount = update.SourceRayCount == 0u
                                    ? checked((int)Math.Max(packedRayCount, 1u))
                                    : checked((int)update.SourceRayCount);
                                _probeSourceRayCounts[probeIndex] = checked((ushort)Math.Clamp(
                                    recordedSourceRayCount,
                                    1,
                                    GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe));
                                if (sourceGenerationBoundary)
                                {
                                    _probeTransportGenerationCounts[probeIndex] = 1;
                                    // Only a genuine physical/source generation
                                    // boundary invalidates the prior Jacobi
                                    // convergence evidence. A periodic refresh
                                    // keeps the published solution as its initial
                                    // guess and relaxes from it below.
                                    if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                                        _probeStableUpdateCounts[probeIndex] = 0;
                                    if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                                        _probeLuminanceChangeEma[probeIndex] = 0.0f;
                                    if (_currentSourceRefreshTransportInvalidationCount < int.MaxValue)
                                        _currentSourceRefreshTransportInvalidationCount++;
                                }
                                else
                                {
                                    // Preserve the warm-start generation and EMA,
                                    // but require fresh stability samples. This
                                    // holds the propagation latch open long enough
                                    // for dependants to relax from their existing
                                    // solutions instead of retiring before the
                                    // refreshed residual reaches CPU readback.
                                    _probeTransportGenerationCounts[probeIndex] = (byte)Math.Min(
                                        byte.MaxValue,
                                        _probeTransportGenerationCounts[probeIndex] + 1);
                                    if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                                        _probeStableUpdateCounts[probeIndex] = 0;
                                }
                                if (routineSourceRefresh &&
                                    (uint)probeIndex <
                                        (uint)_probeRoutineMaintenancePending.Length)
                                {
                                    // A periodic refresh is a validation sample,
                                    // not proof that the cached field changed.
                                    // Keep the refreshed probe active until its
                                    // GPU residual returns; readback wakes the
                                    // trilinear neighbourhood only when that
                                    // residual is material. This prevents a
                                    // static source sweep from manufacturing
                                    // thousands of solver updates every frame.
                                    _probeRoutineMaintenancePending[probeIndex] =
                                        RoutineSourceValidationPending;
                                }
                                if (wakePropagationNeighborhood &&
                                    !routineSourceRefresh)
                                {
                                    MarkTransportPropagationNeighborhoodDirty(
                                        probeIndex);
                                }
                            }
                            else if ((uint)probeIndex < (uint)_probeTransportGenerationCounts.Length)
                            {
                                _probeTransportGenerationCounts[probeIndex] = (byte)Math.Min(
                                    byte.MaxValue,
                                    _probeTransportGenerationCounts[probeIndex] + 1);
                            }
                        }
                        RecordDirtyFirstCompletedUpdate(probeIndex, completedFrame);
                        MarkProbeSchedulerDirty(probeIndex);
                        MarkProbeVisibilityDirty(probeIndex);
                    }
                }

                _atlasFresh = false;
            }

            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
            _transportTransactionExecuted = false;
            _blendTransactionExecuted = false;
        }

        public void UpdateSampledAtlasGpuPublishDescriptors(DescriptorSet descriptorSet) =>
            _sampledAtlas?.UpdateGpuPublishDescriptors(descriptorSet);

        public void BeginSampledAtlasGpuPublication(CommandBuffer commandBuffer)
        {
            _lastSampledAtlasSynchronizationMicroseconds = 0;
            _sampledAtlas?.BeginGpuPublication(commandBuffer);
        }

        public void EndSampledAtlasGpuPublication(CommandBuffer commandBuffer) =>
            _sampledAtlas?.EndGpuPublication(commandBuffer);

        public void MarkTraceExecuted()
        {
            if (CanExecuteTraceTransaction)
                _traceTransactionExecuted = true;
        }

        public void MarkRelocateClassifyExecuted()
        {
            if (CanExecuteRelocateClassifyTransaction)
                _relocateClassifyTransactionExecuted = true;
        }

        public void MarkTransportExecuted()
        {
            if (CanExecuteTransportTransaction)
                _transportTransactionExecuted = true;
        }

        public void MarkBlendExecuted()
        {
            if (CanExecuteBlendTransaction)
                _blendTransactionExecuted = true;
        }

        public void AbortUpdateTransaction()
        {
            if (_updateTransactionPending)
            {
                _updateTransactionAbortCount = SaturatingAdd(
                    _updateTransactionAbortCount,
                    1UL);
            }
            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
            _transportTransactionExecuted = false;
            _blendTransactionExecuted = false;
        }

        private void BeginUpdateTransaction(bool hasWork)
        {
            _updateTransactionSerial++;
            if (_updateTransactionSerial == 0)
                _updateTransactionSerial = 1;
            _updateTransactionPending = hasWork;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
            _transportTransactionExecuted = false;
            _blendTransactionExecuted = false;
        }

        private void ResetFrameCounters()
        {
            // Clear only the bounded queue produced by the preceding frame.
            // Clearing the whole probe pool was another hidden O(total probes)
            // scheduler pass.
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
                if ((uint)probeIndex < (uint)_probeQueued.Length)
                    _probeQueued[probeIndex] = 0;
            }
            _probesToUpdate = 0;
            _rayDispatchBatchCount = 0;
            _transportPublishedProbeCount = _currentTransportPublishedProbeCount;
            _transportPublishRegionCount = _currentTransportPublishRegionCount;
            if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
            {
                _receiverRecordsPublishedCount =
                    _currentReceiverRecordsPublishedCount;
            }
            _sourceRefreshTransportInvalidationCount =
                _currentSourceRefreshTransportInvalidationCount;
            _completedSourceRefreshProbeCount =
                _currentCompletedSourceRefreshProbeCount;
            _currentTransportPublishedProbeCount = 0;
            _currentTransportPublishRegionCount = 0;
            _currentReceiverRecordsPublishedCount = 0;
            _currentSourceRefreshTransportInvalidationCount = 0;
            _currentCompletedSourceRefreshProbeCount = 0;
            _currentProbeInvalidationMarkerSerial = ++_nextProbeInvalidationMarkerSerial;
            if (_currentProbeInvalidationMarkerSerial == 0u)
            {
                // Generation/invalidation wrap must never make an old marker look
                // current.  This table is bounded by the selected probe budget.
                Array.Clear(_probeInvalidationMarkers);
                _currentProbeInvalidationMarkerSerial = ++_nextProbeInvalidationMarkerSerial;
            }
            _recenteredThisFrame = false;
            _atlasPreservedOnRecenterThisFrame = false;
            _atlasClearedThisFrame = false;
            _receiverProbeInvalidationBytesThisFrame = 0;
            _receiverProbeInvalidationRunCountThisFrame = 0;
            _receiverProbeFullClearThisFrame = false;
            _newlyInvalidatedProbeCount = 0;
            _recenterRefreshProbeCount = 0;
            _dirtyRefreshProbeCount = 0;
            _ageRefreshProbeCount = 0;
            _fullRefreshProbeCount = 0;
            _scrollCopyCount = 0;
            _ringRecenteredThisFrame = false;
            _inactiveProbeSkipCount = 0;
            _inactiveProbeSavedPrimaryRayCount = 0;
            _lightingDirtyBoostedCapacity = 0;
            _regionalDirtyReasonFlags = 0u;
            _scheduledPrimaryRayCount = 0;
            _scheduledTransportRayCount = 0;
            _scheduledSourceRayCount = 0;
            _sourceRefreshProbeCount = 0;
            _sourceCacheReuseProbeCount = 0;
            _effectiveMaxShadedLights = 0;
            _adaptiveRaySavedPrimaryRayCount = 0;
            _rayBudgetRejectedProbeCount = 0;
            _rayBudgetRejectedPrimaryRayCount = 0;
            _schedulerSourceRequestBudget = 0;
            _schedulerConfiguredRequestBudget = 0;
            _schedulerEffectiveRequestBudget = 0;
            _schedulerDeferredRequestCount = 0;
            _schedulerPressureReason = SimpleDdgiSchedulerPressureReason.None;
            Array.Clear(_scheduledWorkClassCounts);
            Array.Clear(_reservedWorkClassCounts);
            Array.Clear(_rayRejectedWorkClassCounts);
            Array.Clear(_volumeWorkClassQuotaScratch);
            Array.Clear(_volumeWorkClassUsageScratch);
            _fullRayProbeUpdateCount = 0;
            _maintenanceRayProbeUpdateCount = 0;
            Array.Clear(_ringFullRayProbeUpdateCounts);
            Array.Clear(_ringMaintenanceRayProbeUpdateCounts);
            Array.Clear(_ringScheduledPrimaryRayCounts);
        }

        private void CapturePreviousVolumes()
        {
            _previousVolumeCount = _volumeCount;
            Array.Copy(_volumeScratch, _previousVolumeScratch, _volumeScratch.Length);
        }

        private void EnsureCpuProbeStateCapacity(int probeCount)
        {
            if (_probeFresh.Length == probeCount)
                return;

            byte[] previousFresh = _probeFresh;
            byte[] previousInactive = _probeInactive;
            byte[] previousRelocationPending = _probeRelocationPending;
            byte[] previousVisibilityValid = _probeVisibilityValid;
            byte[] previousSchedulingFlags = _probeSchedulingFlags;
            byte[] previousDirtyReasons = _probeDirtyReasons;
            byte[] previousRoutineMaintenancePending =
                _probeRoutineMaintenancePending;
            byte[] previousVisibilityImportance = _probeVisibilityImportance;
            byte[] previousStableUpdateCounts = _probeStableUpdateCounts;
            float[] previousLuminanceChangeEma = _probeLuminanceChangeEma;
            uint[] previousLastUpdatedFrames = _probeLastUpdatedFrames;
            uint[] previousSourceLightingGenerations = _probeSourceLightingGenerations;
            uint[] previousSourceRefreshFrames = _probeLastSourceRefreshFrames;
            uint[] previousSourceEpochs = _probeSourceEpochs;
            ushort[] previousSourceRayCounts = _probeSourceRayCounts;
            byte[] previousTransportGenerationCounts = _probeTransportGenerationCounts;
            byte[] previousAtmosphereCohortFlags = _probeAtmosphereCohortFlags;
            uint[] previousGenerations = _probeGenerations;
            uint[] previousInvalidationMarkers = _probeInvalidationMarkers;
            Vector3[] previousRelocations = _probeRelocations;
            float[] previousActiveWeights = _probeActiveWeights;
            uint[] previousClassifications = _probeClassifications;
            byte[] previousDirtyLatencyStates = _probeDirtyLatencyStates;
            uint[] previousDirtyLatencyStartFrames = _probeDirtyLatencyStartFrames;
            _probeFresh = new byte[Math.Max(0, probeCount)];
            _probeInactive = new byte[Math.Max(0, probeCount)];
            _probeRelocationPending = new byte[Math.Max(0, probeCount)];
            _probeVisibilityValid = new byte[Math.Max(0, probeCount)];
            _probeQueued = new byte[Math.Max(0, probeCount)];
            _probeSchedulingFlags = new byte[Math.Max(0, probeCount)];
            _probeDirtyReasons = new byte[Math.Max(0, probeCount)];
            _probeRoutineMaintenancePending = new byte[Math.Max(0, probeCount)];
            _probeVisibilityImportance = new byte[Math.Max(0, probeCount)];
            _probeGenerations = new uint[Math.Max(0, probeCount)];
            _probeInvalidationMarkers = new uint[Math.Max(0, probeCount)];
            _probeRelocations = new Vector3[Math.Max(0, probeCount)];
            _probeActiveWeights = new float[Math.Max(0, probeCount)];
            _probeClassifications = new uint[Math.Max(0, probeCount)];
            _probeStableUpdateCounts = new byte[Math.Max(0, probeCount)];
            _probeLuminanceChangeEma = new float[Math.Max(0, probeCount)];
            _probeLastUpdatedFrames = new uint[Math.Max(0, probeCount)];
            _probeSourceLightingGenerations = new uint[Math.Max(0, probeCount)];
            _probeLastSourceRefreshFrames = new uint[Math.Max(0, probeCount)];
            _probeSourceEpochs = new uint[Math.Max(0, probeCount)];
            _probeSourceRayCounts = new ushort[Math.Max(0, probeCount)];
            _probeTransportGenerationCounts = new byte[Math.Max(0, probeCount)];
            _probeAtmosphereCohortFlags = new byte[Math.Max(0, probeCount)];
            _probeDirtyLatencyStates = new byte[Math.Max(0, probeCount)];
            _probeDirtyLatencyStartFrames = new uint[Math.Max(0, probeCount)];
            int copyCount = Math.Min(probeCount, previousFresh.Length);
            Array.Copy(previousFresh, _probeFresh, copyCount);
            Array.Copy(previousInactive, _probeInactive, copyCount);
            Array.Copy(
                previousRelocationPending,
                _probeRelocationPending,
                Math.Min(copyCount, previousRelocationPending.Length));
            Array.Copy(
                previousVisibilityValid,
                _probeVisibilityValid,
                Math.Min(copyCount, previousVisibilityValid.Length));
            Array.Copy(previousSchedulingFlags, _probeSchedulingFlags, Math.Min(copyCount, previousSchedulingFlags.Length));
            Array.Copy(previousDirtyReasons, _probeDirtyReasons, Math.Min(copyCount, previousDirtyReasons.Length));
            Array.Copy(
                previousRoutineMaintenancePending,
                _probeRoutineMaintenancePending,
                Math.Min(copyCount, previousRoutineMaintenancePending.Length));
            Array.Copy(previousVisibilityImportance, _probeVisibilityImportance, Math.Min(copyCount, previousVisibilityImportance.Length));
            Array.Copy(previousStableUpdateCounts, _probeStableUpdateCounts, Math.Min(copyCount, previousStableUpdateCounts.Length));
            Array.Copy(previousLuminanceChangeEma, _probeLuminanceChangeEma, Math.Min(copyCount, previousLuminanceChangeEma.Length));
            Array.Copy(previousLastUpdatedFrames, _probeLastUpdatedFrames, copyCount);
            Array.Copy(previousSourceLightingGenerations, _probeSourceLightingGenerations, Math.Min(copyCount, previousSourceLightingGenerations.Length));
            Array.Copy(previousSourceRefreshFrames, _probeLastSourceRefreshFrames, Math.Min(copyCount, previousSourceRefreshFrames.Length));
            Array.Copy(previousSourceEpochs, _probeSourceEpochs, Math.Min(copyCount, previousSourceEpochs.Length));
            Array.Copy(previousSourceRayCounts, _probeSourceRayCounts, Math.Min(copyCount, previousSourceRayCounts.Length));
            Array.Copy(previousTransportGenerationCounts, _probeTransportGenerationCounts, Math.Min(copyCount, previousTransportGenerationCounts.Length));
            Array.Copy(previousAtmosphereCohortFlags, _probeAtmosphereCohortFlags, Math.Min(copyCount, previousAtmosphereCohortFlags.Length));
            Array.Copy(previousGenerations, _probeGenerations, Math.Min(copyCount, previousGenerations.Length));
            Array.Copy(previousInvalidationMarkers, _probeInvalidationMarkers, Math.Min(copyCount, previousInvalidationMarkers.Length));
            Array.Copy(previousRelocations, _probeRelocations, Math.Min(copyCount, previousRelocations.Length));
            Array.Copy(previousActiveWeights, _probeActiveWeights, Math.Min(copyCount, previousActiveWeights.Length));
            Array.Copy(previousClassifications, _probeClassifications, Math.Min(copyCount, previousClassifications.Length));
            Array.Copy(previousDirtyLatencyStates, _probeDirtyLatencyStates, Math.Min(copyCount, previousDirtyLatencyStates.Length));
            Array.Copy(previousDirtyLatencyStartFrames, _probeDirtyLatencyStartFrames, Math.Min(copyCount, previousDirtyLatencyStartFrames.Length));
            if (probeCount > copyCount)
            {
                Array.Fill(_probeFresh, (byte)1, copyCount, probeCount - copyCount);
                Array.Fill(_probeGenerations, 1u, copyCount, probeCount - copyCount);
                Array.Fill(_probeActiveWeights, 1.0f, copyCount, probeCount - copyCount);
                Array.Fill(_probeSourceEpochs, 1u, copyCount, probeCount - copyCount);
                Array.Fill(
                    _probeLastUpdatedFrames,
                    unchecked(_frameIndex - 1u),
                    copyCount,
                    probeCount - copyCount);
                _newlyInvalidatedProbeCount += probeCount - copyCount;
            }

            for (int i = 0; i < Math.Min(copyCount, _probeGenerations.Length); i++)
            {
                if (_probeGenerations[i] == 0)
                    _probeGenerations[i] = 1;
                if (_probeActiveWeights[i] <= 0.0f && _probeInactive[i] == 0)
                    _probeActiveWeights[i] = 1.0f;
            }

            _activeProbeCount = Math.Max(0, probeCount - CountInactiveProbes(_probeInactive, probeCount));
            _classifiedInactiveProbeCountEstimate = probeCount - _activeProbeCount;
            _probeRelocationCount = 0;
            _relocationFractionSumEstimate = 0.0f;
            for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                float relocationFraction = CalculateProbeRelocationFraction(
                    probeIndex,
                    _probeRelocations[probeIndex]);
                if (relocationFraction <= 0.0f)
                    continue;
                _probeRelocationCount++;
                _relocationFractionSumEstimate += relocationFraction;
            }
            _averageRelocationFractionEstimate = _probeRelocationCount > 0
                ? _relocationFractionSumEstimate / _probeRelocationCount
                : 0.0f;
            RecomputeDirtyLatencyOutstandingCount();
            EnsurePersistentSchedulerCapacity(probeCount);
            RebuildAtmosphereCohortCounters();
            _probeStateUploadRequired = true;
        }

        private void EnsurePersistentSchedulerCapacity(int probeCount)
        {
            probeCount = Math.Max(0, probeCount);
            _schedulerWorkQueues.EnsureProbeCapacity(probeCount);
            _schedulerSourceRefreshQueues.EnsureProbeCapacity(probeCount);
            _schedulerCachedSolverQueues.EnsureProbeCapacity(probeCount);
            _schedulerWakeHeap.EnsureProbeCapacity(probeCount);
            _schedulerFreshAgeHeap.EnsureProbeCapacity(probeCount);
            _schedulerScrollExposedAgeHeap.EnsureProbeCapacity(probeCount);
            _schedulerRelocationPendingAgeHeap.EnsureProbeCapacity(probeCount);
            _schedulerUnpublishedAgeHeap.EnsureProbeCapacity(probeCount);
            _schedulerVisibleUnsupportedAgeHeap.EnsureProbeCapacity(probeCount);
            _schedulerGenerationStaleAgeHeap.EnsureProbeCapacity(probeCount);
            _probeSchedulerDirtyIndices = new int[probeCount];
            _probeSchedulerDirty = new byte[probeCount];
            _probeSchedulerTransportStates = new byte[probeCount];
            _probeSchedulerVolumeIndices = new byte[probeCount];
            Array.Fill(_probeSchedulerVolumeIndices, byte.MaxValue);
            _probeTransportTelemetryReasons = new byte[probeCount];
            _probeTransportTelemetryResidualBuckets = new byte[probeCount];
            _probeTransportTelemetrySourceEpochBuckets = new byte[probeCount];
            _probeTransportTelemetryResidualQualifiedPending = new byte[probeCount];
            _probeTransportTelemetryGenerationBuckets = new byte[probeCount];
            _probeTransportSolverCompletionRecordedSourceGenerations =
                new uint[probeCount];
            _probeTransportTelemetryVolumeIndices = new byte[probeCount];
            Array.Fill(_probeTransportTelemetryReasons, byte.MaxValue);
            Array.Fill(_probeTransportTelemetryResidualBuckets, byte.MaxValue);
            Array.Fill(_probeTransportTelemetrySourceEpochBuckets, byte.MaxValue);
            Array.Fill(_probeTransportTelemetryResidualQualifiedPending, byte.MaxValue);
            Array.Fill(_probeTransportTelemetryGenerationBuckets, byte.MaxValue);
            Array.Fill(_probeTransportTelemetryVolumeIndices, byte.MaxValue);
            Array.Clear(_transportProbeStateReasonCounts);
            Array.Clear(_volumeTransportProbeStateReasonCounts);
            Array.Clear(_volumeTransportResidualDistributionCounts);
            Array.Clear(_volumeTransportSourceEpochDistributionCounts);
            Array.Clear(_volumeTransportResidualQualifiedPendingCounts);
            Array.Clear(_volumeTransportSolverCompletionLatencyHistograms);
            Array.Clear(_volumeTransportSolverCompletionLatencySampleCounts);
            Array.Clear(_volumeTransportSolverCompletionLatencyMaxFrames);
            Array.Clear(_volumeTransportSolverGenerationDistributionCounts);
            _probeSchedulerLifecycleStates = new byte[probeCount];
            _probeSchedulerTrackedLastUpdatedFrames = new uint[probeCount];
            _probeSchedulerTrackedSourceRefreshFrames = new uint[probeCount];
            _probeVisibilityDirtyIndices = new int[probeCount];
            _probeVisibilityDirty = new byte[probeCount];
            _probeVisibleFreshCounted = new byte[probeCount];
            _probeSchedulerDirtyCount = 0;
            _probeVisibilityDirtyCount = 0;
            Array.Clear(_volumeVisibleFreshProbeCounts);
            _schedulerGlobalStateValid = false;
            _schedulerRebuildRequired = true;
            _schedulerVisibilityFullRefreshRequired = true;
            _hasSchedulerVisibilityCamera = false;
        }

        private void MarkProbeSchedulerDirty(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeSchedulerDirty.Length ||
                _probeSchedulerDirty[probeIndex] != 0)
            {
                return;
            }

            _probeSchedulerDirty[probeIndex] = 1;
            _probeSchedulerDirtyIndices[_probeSchedulerDirtyCount++] = probeIndex;
        }

        private void MarkProbeVisibilityDirty(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeVisibilityDirty.Length ||
                _probeVisibilityDirty[probeIndex] != 0)
            {
                return;
            }

            _probeVisibilityDirty[probeIndex] = 1;
            _probeVisibilityDirtyIndices[_probeVisibilityDirtyCount++] = probeIndex;
        }

        private void RequirePersistentSchedulerRebuild()
        {
            _schedulerRebuildRequired = true;
        }

        private SchedulerGlobalStateSnapshot CaptureSchedulerGlobalState()
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return new SchedulerGlobalStateSnapshot(
                _volumeTableGeneration,
                _probeCount,
                _lightingDirtyFrames > 0,
                TransportV2Active,
                TransportGlobalConvergencePending,
                _transportPeriodicSourceRefreshWavePending,
                _transportPeriodicSourceRefreshWaveCutoffFrame,
                _sourceLightingGeneration,
                gi.SimpleDdgiClassificationSchedulingEnabled,
                _probeConvergenceReadbackValid != 0,
                gi.SimpleDdgiStableMaintenanceUpdateCount,
                gi.SimpleDdgiTransportAcceleratedSweepCount,
                BitConverter.SingleToInt32Bits(gi.SimpleDdgiTransportTailRelativeTolerance));
        }

        private void RefreshPersistentSchedulerState()
        {
            SchedulerGlobalStateSnapshot currentGlobalState = CaptureSchedulerGlobalState();
            if (!_schedulerGlobalStateValid || currentGlobalState != _schedulerGlobalState)
            {
                if (!_schedulerGlobalStateValid ||
                    currentGlobalState.VolumeTableGeneration !=
                        _schedulerGlobalState.VolumeTableGeneration ||
                    currentGlobalState.LightingDirty !=
                        _schedulerGlobalState.LightingDirty ||
                    currentGlobalState.ConvergenceReadbackValid !=
                        _schedulerGlobalState.ConvergenceReadbackValid ||
                    currentGlobalState.StableMaintenanceUpdateCount !=
                        _schedulerGlobalState.StableMaintenanceUpdateCount)
                {
                    _schedulerVisibilityFullRefreshRequired = true;
                }
                _schedulerGlobalState = currentGlobalState;
                _schedulerGlobalStateValid = true;
                _schedulerRebuildRequired = true;
            }

            if (_schedulerRebuildRequired)
            {
                RebuildPersistentSchedulerState();
            }
            else
            {
                int wakeRefreshBudget = Math.Max(
                    1,
                    _schedulerRoutineWakeRefreshBudget);
                int wakeRefreshCount = 0;
                while (wakeRefreshCount < wakeRefreshBudget &&
                    _schedulerWakeHeap.TryPopDue(
                        _frameSerial,
                        out int dueProbeIndex))
                {
                    MarkProbeSchedulerDirty(dueProbeIndex);
                    wakeRefreshCount++;
                }
                _uploadSchedulerWakeEntryRefreshCount = wakeRefreshCount;
                _uploadSchedulerWakeBudgetSaturated =
                    wakeRefreshCount >= wakeRefreshBudget &&
                    _schedulerWakeHeap.HasDue(_frameSerial)
                        ? 1
                        : 0;

                int dirtyCount = _probeSchedulerDirtyCount;
                _probeSchedulerDirtyCount = 0;
                for (int dirtyOffset = 0; dirtyOffset < dirtyCount; dirtyOffset++)
                {
                    int probeIndex = _probeSchedulerDirtyIndices[dirtyOffset];
                    if ((uint)probeIndex >= (uint)_probeSchedulerDirty.Length)
                        continue;
                    _probeSchedulerDirty[probeIndex] = 0;
                    RefreshProbeSchedulerEntry(
                        probeIndex,
                        ResolveSchedulerVolumeIndex(probeIndex));
                }
            }

            _inactiveProbeSkipCount = _schedulerInactiveDeferredProbeCount;
            _inactiveProbeSavedPrimaryRayCount =
                _schedulerInactiveDeferredSavedPrimaryRayCount;
        }

        private void RebuildPersistentSchedulerState()
        {
            _uploadSchedulerFullRebuildCount++;
            _schedulerWorkQueues.Clear();
            _schedulerSourceRefreshQueues.Clear();
            _schedulerCachedSolverQueues.Clear();
            _schedulerWakeHeap.Clear();
            _schedulerFreshAgeHeap.Clear();
            _schedulerScrollExposedAgeHeap.Clear();
            _schedulerRelocationPendingAgeHeap.Clear();
            _schedulerUnpublishedAgeHeap.Clear();
            _schedulerVisibleUnsupportedAgeHeap.Clear();
            _schedulerGenerationStaleAgeHeap.Clear();
            _schedulerVisibleUnsupportedAgeHistogram.Clear(_frameSerial);
            _schedulerVisiblePendingAgeHistogram.Clear(_frameSerial);
            _schedulerGenerationStaleAgeHistogram.Clear(_frameSerial);
            Array.Clear(_probeSchedulerTransportStates);
            Array.Fill(_probeSchedulerVolumeIndices, byte.MaxValue);
            Array.Clear(_probeSchedulerLifecycleStates);
            Array.Clear(_probeSchedulerTrackedLastUpdatedFrames);
            Array.Clear(_probeSchedulerTrackedSourceRefreshFrames);
            Array.Clear(_pendingWorkClassCounts);
            Array.Clear(_probeAtmosphereCohortFlags);
            _schedulerParticipatingProbeCount = 0;
            _transportResidentParticipantCount = 0;
            _transportResidentSourceRepairProbeCount = 0;
            _schedulerAtmosphereVisibleParticipatingProbeCount = 0;
            _schedulerAtmosphereVisibleSourceReadyProbeCount = 0;
            _schedulerAtmosphereVisiblePublishedProbeCount = 0;
            Array.Clear(_volumeAtmosphereParticipatingProbeCounts);
            Array.Clear(_volumeAtmosphereRayCounts);
            _schedulerSourceRepairProbeCount = 0;
            _schedulerRoutineSourceRepairProbeCount = 0;
            _schedulerRoutineMaintenancePendingProbeCount = 0;
            _schedulerPendingConvergenceProbeCount = 0;
            _schedulerInactiveDeferredProbeCount = 0;
            _schedulerInactiveDeferredSavedPrimaryRayCount = 0UL;

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                int firstProbe = FirstProbe(volume);
                int probeCount = VolumeProbeCount(volume);
                int stride = ResolveProbeUpdateStride(probeCount);
                for (int sequenceIndex = 0; sequenceIndex < probeCount; sequenceIndex++)
                {
                    int local = (int)((long)sequenceIndex * stride % probeCount);
                    RefreshProbeSchedulerEntry(firstProbe + local, volumeIndex);
                }
            }

            Array.Clear(_probeSchedulerDirty);
            _probeSchedulerDirtyCount = 0;
            _schedulerRebuildRequired = false;
        }

        internal static int ResolveRoutineSchedulerWakeRefreshBudget(
            int baseUpdateBudget)
        {
            // Wake-heap entries are background inactive retries and periodic
            // source-cache maintenance. Promoting more entries than a frame can
            // reasonably dispatch only front-loads CPU queue churn; urgent
            // dirty/visible invalidations bypass this cap through the explicit
            // scheduler-dirty set.
            // The high-tier stationary set needs roughly 70 periodic/inactive
            // promotions per frame (eligible probes / refresh interval). A 128
            // entry cap provides nearly 2x maintenance headroom while keeping
            // indexed-heap/queue updates comfortably below the Upload gate.
            const int maximumWakeRefreshesPerFrame = 128;
            return Math.Clamp(
                baseUpdateBudget,
                1,
                maximumWakeRefreshesPerFrame);
        }

        private void RefreshProbeSchedulerEntry(int probeIndex, int volumeIndex)
        {
            _uploadSchedulerEntryRefreshCount++;
            if ((uint)probeIndex < (uint)_probeSchedulerVolumeIndices.Length)
            {
                _probeSchedulerVolumeIndices[probeIndex] =
                    (uint)volumeIndex < (uint)_volumeCount
                        ? checked((byte)volumeIndex)
                        : byte.MaxValue;
            }
            int queueIndex = SimpleDdgiPersistentProbeQueues.NoQueue;
            if ((uint)probeIndex < (uint)_probeCount &&
                (uint)volumeIndex < (uint)_volumeCount &&
                TryResolveProbeWorkClass(
                    probeIndex,
                    volumeIndex,
                    out SimpleDdgiSchedulerWorkClass workClass))
            {
                queueIndex = WorkClassOffset(volumeIndex) + (int)workClass;
            }

            _schedulerWorkQueues.MoveToQueue(probeIndex, queueIndex);
            bool sourceRefreshRequired = queueIndex !=
                    SimpleDdgiPersistentProbeQueues.NoQueue &&
                TransportV2Active &&
                NeedsSourceRefresh(probeIndex);
            bool cachedSolverPriority = queueIndex !=
                    SimpleDdgiPersistentProbeQueues.NoQueue &&
                !sourceRefreshRequired &&
                IsCachedTransportSolvePriorityCandidate(probeIndex);
            _schedulerSourceRefreshQueues.MoveToQueue(
                probeIndex,
                sourceRefreshRequired
                    ? queueIndex
                    : SimpleDdgiPersistentProbeQueues.NoQueue);
            _schedulerCachedSolverQueues.MoveToQueue(
                probeIndex,
                cachedSolverPriority
                    ? queueIndex
                    : SimpleDdgiPersistentProbeQueues.NoQueue);

            RefreshProbeSchedulerCounters(probeIndex, volumeIndex);
            RefreshProbeSchedulerLifecycleState(probeIndex);
            RefreshProbeSchedulerWake(probeIndex);
        }

        private int ResolveSchedulerVolumeIndex(int probeIndex)
        {
            if ((uint)probeIndex < (uint)_probeSchedulerVolumeIndices.Length)
            {
                int volumeIndex = _probeSchedulerVolumeIndices[probeIndex];
                if ((uint)volumeIndex < (uint)_volumeCount)
                    return volumeIndex;
            }

            return ResolveVolumeIndexForProbe(probeIndex);
        }

        private void RefreshProbeSchedulerCounters(int probeIndex, int volumeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeSchedulerTransportStates.Length)
                return;

            byte previousState = _probeSchedulerTransportStates[probeIndex];
            bool participant = TransportV2Active &&
                (uint)probeIndex < (uint)_probeInactive.Length &&
                ShouldParticipateInTransportConvergence(
                    _probeInactive[probeIndex] != 0);
            bool sourceRepair = participant && NeedsSourceRefresh(probeIndex);
            bool routineSourceRepair = sourceRepair &&
                IsRoutineTransportSourceRefresh(probeIndex);
            bool pendingConvergence = participant &&
                !sourceRepair &&
                !HasLocalTransportConvergenceEvidence(probeIndex);
            bool routineMaintenancePending = pendingConvergence &&
                (uint)probeIndex < (uint)_probeRoutineMaintenancePending.Length &&
                _probeRoutineMaintenancePending[probeIndex] != 0;
            bool inactiveDeferred = ShouldSkipInactiveProbe(probeIndex);
            byte nextState = 0;
            if (participant)
                nextState |= SchedulerTransportParticipantFlag;
            if (sourceRepair)
                nextState |= SchedulerTransportSourceRepairFlag;
            if (routineSourceRepair)
                nextState |= SchedulerTransportRoutineSourceRepairFlag;
            if (pendingConvergence)
                nextState |= SchedulerTransportPendingConvergenceFlag;
            if (routineMaintenancePending)
                nextState |= SchedulerTransportRoutineMaintenanceFlag;
            if (inactiveDeferred)
                nextState |= SchedulerInactiveDeferredFlag;
            RefreshTransportConvergenceTelemetry(probeIndex, volumeIndex);
            if (nextState == previousState)
            {
                RefreshAtmosphereCohortCounters(
                    probeIndex,
                    volumeIndex,
                    participant,
                    sourceRepair);
                return;
            }

            AdjustSchedulerCounter(
                previousState,
                nextState,
                SchedulerTransportParticipantFlag,
                ref _schedulerParticipatingProbeCount);
            AdjustSchedulerCounter(
                previousState,
                nextState,
                SchedulerTransportSourceRepairFlag,
                ref _schedulerSourceRepairProbeCount);
            AdjustSchedulerCounter(
                previousState,
                nextState,
                SchedulerTransportRoutineSourceRepairFlag,
                ref _schedulerRoutineSourceRepairProbeCount);
            AdjustSchedulerCounter(
                previousState,
                nextState,
                SchedulerTransportPendingConvergenceFlag,
                ref _schedulerPendingConvergenceProbeCount);
            AdjustSchedulerCounter(
                previousState,
                nextState,
                SchedulerTransportRoutineMaintenanceFlag,
                ref _schedulerRoutineMaintenancePendingProbeCount);

            bool wasInactiveDeferred =
                (previousState & SchedulerInactiveDeferredFlag) != 0;
            if (wasInactiveDeferred != inactiveDeferred)
            {
                ulong savedRays = (uint)volumeIndex < (uint)_volumeCount
                    ? (ulong)Math.Max(0, ResolveVolumeQuality(volumeIndex).FullRays)
                    : 0UL;
                if (inactiveDeferred)
                {
                    _schedulerInactiveDeferredProbeCount++;
                    _schedulerInactiveDeferredSavedPrimaryRayCount = SaturatingAdd(
                        _schedulerInactiveDeferredSavedPrimaryRayCount,
                        savedRays);
                }
                else
                {
                    _schedulerInactiveDeferredProbeCount = Math.Max(
                        0,
                        _schedulerInactiveDeferredProbeCount - 1);
                    _schedulerInactiveDeferredSavedPrimaryRayCount =
                        _schedulerInactiveDeferredSavedPrimaryRayCount >= savedRays
                            ? _schedulerInactiveDeferredSavedPrimaryRayCount - savedRays
                            : 0UL;
                }
            }

            _probeSchedulerTransportStates[probeIndex] = nextState;
            RefreshAtmosphereCohortCounters(
                probeIndex,
                volumeIndex,
                participant,
                sourceRepair);
        }

        private void RefreshAtmosphereCohortCounters(
            int probeIndex,
            int volumeIndex,
            bool participant,
            bool sourceRepair)
        {
            if ((uint)probeIndex >= (uint)_probeAtmosphereCohortFlags.Length)
                return;

            byte previous = _probeAtmosphereCohortFlags[probeIndex];
            bool visible = participant &&
                (uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                (_probeSchedulingFlags[probeIndex] & ProbeSchedulingVisibleFlag) != 0;
            bool sourceReady = participant && !sourceRepair;
            bool published = sourceReady &&
                (uint)probeIndex < (uint)_probeFresh.Length &&
                _probeFresh[probeIndex] == 0 &&
                (uint)probeIndex < (uint)_probeSourceLightingGenerations.Length &&
                _probeSourceLightingGenerations[probeIndex] == _sourceLightingGeneration &&
                (uint)probeIndex < (uint)_probeSourceRayCounts.Length &&
                _probeSourceRayCounts[probeIndex] > 0;
            byte next = 0;
            if (participant)
                next |= AtmosphereParticipantFlag;
            if (visible)
                next |= AtmosphereVisibleFlag;
            if (sourceReady)
                next |= AtmosphereSourceReadyFlag;
            if (published)
                next |= AtmospherePublishedFlag;

            AdjustAtmosphereCounter(
                previous,
                next,
                AtmosphereParticipantFlag,
                ref _schedulerAtmosphereVisibleParticipatingProbeCount,
                visibleOnly: true);
            AdjustAtmosphereCounter(
                previous,
                next,
                AtmosphereSourceReadyFlag,
                ref _schedulerAtmosphereVisibleSourceReadyProbeCount,
                visibleOnly: true,
                requiredFlag: AtmosphereVisibleFlag);
            AdjustAtmosphereCounter(
                previous,
                next,
                AtmospherePublishedFlag,
                ref _schedulerAtmosphereVisiblePublishedProbeCount,
                visibleOnly: true,
                requiredFlag: AtmosphereVisibleFlag);

            bool previousParticipant = (previous & AtmosphereParticipantFlag) != 0;
            bool nextParticipant = (next & AtmosphereParticipantFlag) != 0;
            if (previousParticipant != nextParticipant &&
                (uint)volumeIndex < (uint)_volumeCount)
            {
                _volumeAtmosphereParticipatingProbeCounts[volumeIndex] = Math.Max(
                    0,
                    _volumeAtmosphereParticipatingProbeCounts[volumeIndex] +
                        (nextParticipant ? 1 : -1));
                int rays = Math.Max(1, ResolveVolumeQuality(volumeIndex).FullRays);
                _volumeAtmosphereRayCounts[volumeIndex] = Math.Max(
                    0,
                    _volumeAtmosphereRayCounts[volumeIndex] +
                        (nextParticipant ? rays : -rays));
            }

            _probeAtmosphereCohortFlags[probeIndex] = next;
        }

        private static void AdjustAtmosphereCounter(
            byte previous,
            byte next,
            byte flag,
            ref int counter,
            bool visibleOnly,
            byte requiredFlag = 0)
        {
            bool wasSet = (previous & flag) != 0 &&
                          (!visibleOnly || (previous & AtmosphereVisibleFlag) != 0) &&
                          (requiredFlag == 0 || (previous & requiredFlag) != 0);
            bool isSet = (next & flag) != 0 &&
                         (!visibleOnly || (next & AtmosphereVisibleFlag) != 0) &&
                         (requiredFlag == 0 || (next & requiredFlag) != 0);
            if (wasSet == isSet)
                return;
            counter = Math.Max(0, counter + (isSet ? 1 : -1));
        }

        private void RebuildAtmosphereCohortCounters()
        {
            Array.Clear(_probeAtmosphereCohortFlags);
            _schedulerAtmosphereVisibleParticipatingProbeCount = 0;
            _schedulerAtmosphereVisibleSourceReadyProbeCount = 0;
            _schedulerAtmosphereVisiblePublishedProbeCount = 0;
            Array.Clear(_volumeAtmosphereParticipatingProbeCounts);
            Array.Clear(_volumeAtmosphereRayCounts);
            for (int probeIndex = 0; probeIndex < _probeCount; probeIndex++)
            {
                int volumeIndex = ResolveSchedulerVolumeIndex(probeIndex);
                bool participant = TransportV2Active &&
                    (uint)probeIndex < (uint)_probeInactive.Length &&
                    ShouldParticipateInTransportConvergence(_probeInactive[probeIndex] != 0);
                RefreshAtmosphereCohortCounters(
                    probeIndex,
                    volumeIndex,
                    participant,
                    participant && NeedsSourceRefresh(probeIndex));
            }
        }

        private void RefreshTransportConvergenceTelemetry(
            int probeIndex,
            int volumeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeTransportTelemetryReasons.Length ||
                (uint)volumeIndex >= (uint)_volumeCount)
            {
                return;
            }

            RecordTransportSolverCompletionLatency(probeIndex, volumeIndex);

            int reason = (int)ResolveTransportProbeStateReason(probeIndex);
            int residualBucket = ResolveTransportResidualDistributionBucket(probeIndex);
            int sourceEpochBucket = ResolveTransportSourceEpochDistributionBucket(probeIndex);
            int generationBucket = ResolveTransportSolverGenerationBucket(probeIndex);
            int residualQualifiedPending =
                residualBucket <= 2 &&
                reason != (int)SimpleDdgiTransportProbeStateReason.Converged &&
                reason != (int)SimpleDdgiTransportProbeStateReason.Inactive &&
                reason != (int)SimpleDdgiTransportProbeStateReason.SourceStale
                    ? 1
                    : 0;
            int previousVolume = _probeTransportTelemetryVolumeIndices[probeIndex];
            int previousReason = _probeTransportTelemetryReasons[probeIndex];
            int previousResidualBucket =
                _probeTransportTelemetryResidualBuckets[probeIndex];
            int previousSourceEpochBucket =
                _probeTransportTelemetrySourceEpochBuckets[probeIndex];
            int previousResidualQualifiedPending =
                _probeTransportTelemetryResidualQualifiedPending[probeIndex];
            int previousGenerationBucket =
                _probeTransportTelemetryGenerationBuckets[probeIndex];

            if ((uint)previousVolume <
                (uint)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            {
                if ((uint)previousReason < (uint)TransportProbeStateReasonCount)
                {
                    DecrementNonnegative(
                        ref _transportProbeStateReasonCounts[previousReason]);
                    DecrementNonnegative(ref _volumeTransportProbeStateReasonCounts[
                        previousVolume * TransportProbeStateReasonCount +
                        previousReason]);
                }
                if ((uint)previousResidualBucket <
                    (uint)TransportResidualDistributionBucketCount)
                {
                    DecrementNonnegative(ref _volumeTransportResidualDistributionCounts[
                        previousVolume * TransportResidualDistributionBucketCount +
                        previousResidualBucket]);
                }
                if ((uint)previousGenerationBucket <
                    (uint)TransportSolverGenerationBucketCount)
                {
                    DecrementNonnegative(ref _volumeTransportSolverGenerationDistributionCounts[
                        previousVolume * TransportSolverGenerationBucketCount +
                        previousGenerationBucket]);
                }
                if ((uint)previousSourceEpochBucket <
                    (uint)TransportSourceEpochDistributionBucketCount)
                {
                    DecrementNonnegative(ref _volumeTransportSourceEpochDistributionCounts[
                        previousVolume * TransportSourceEpochDistributionBucketCount +
                        previousSourceEpochBucket]);
                }
                if (previousResidualQualifiedPending == 1)
                {
                    DecrementNonnegative(
                        ref _volumeTransportResidualQualifiedPendingCounts[
                            previousVolume]);
                }
            }

            _transportProbeStateReasonCounts[reason]++;
            _volumeTransportProbeStateReasonCounts[
                volumeIndex * TransportProbeStateReasonCount + reason]++;
            _volumeTransportResidualDistributionCounts[
                volumeIndex * TransportResidualDistributionBucketCount +
                residualBucket]++;
            _volumeTransportSourceEpochDistributionCounts[
                volumeIndex * TransportSourceEpochDistributionBucketCount +
                sourceEpochBucket]++;
            if (residualQualifiedPending == 1)
                _volumeTransportResidualQualifiedPendingCounts[volumeIndex]++;
            _volumeTransportSolverGenerationDistributionCounts[
                volumeIndex * TransportSolverGenerationBucketCount +
                generationBucket]++;
            _probeTransportTelemetryReasons[probeIndex] = checked((byte)reason);
            _probeTransportTelemetryResidualBuckets[probeIndex] =
                checked((byte)residualBucket);
            _probeTransportTelemetrySourceEpochBuckets[probeIndex] =
                checked((byte)sourceEpochBucket);
            _probeTransportTelemetryResidualQualifiedPending[probeIndex] =
                checked((byte)residualQualifiedPending);
            _probeTransportTelemetryGenerationBuckets[probeIndex] =
                checked((byte)generationBucket);
            _probeTransportTelemetryVolumeIndices[probeIndex] =
                checked((byte)volumeIndex);
        }

        private SimpleDdgiTransportProbeStateReason ResolveTransportProbeStateReason(
            int probeIndex)
        {
            if (!TransportV2Active ||
                (uint)probeIndex >= (uint)_probeInactive.Length ||
                _probeInactive[probeIndex] != 0)
            {
                return SimpleDdgiTransportProbeStateReason.Inactive;
            }
            if (NeedsSourceRefresh(probeIndex))
                return SimpleDdgiTransportProbeStateReason.SourceStale;
            float residual = (uint)probeIndex < (uint)_probeLuminanceChangeEma.Length
                ? _probeLuminanceChangeEma[probeIndex]
                : float.NaN;
            if (!float.IsFinite(residual) || residual < 0.0f)
                return SimpleDdgiTransportProbeStateReason.InvalidResidual;

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            int generation = (uint)probeIndex <
                (uint)_probeTransportGenerationCounts.Length
                    ? _probeTransportGenerationCounts[probeIndex]
                    : 0;
            if (generation < Math.Max(
                    1,
                    gi.SimpleDdgiTransportAcceleratedSweepCount))
            {
                return SimpleDdgiTransportProbeStateReason.
                    MinimumSolverGenerationIncomplete;
            }
            if (residual > gi.SimpleDdgiTransportTailRelativeTolerance)
                return SimpleDdgiTransportProbeStateReason.ResidualAboveThreshold;

            int stableCount = (uint)probeIndex < (uint)_probeStableUpdateCounts.Length
                ? _probeStableUpdateCounts[probeIndex]
                : 0;
            if (stableCount < Math.Max(1, gi.SimpleDdgiStableMaintenanceUpdateCount))
                return SimpleDdgiTransportProbeStateReason.StableWindowIncomplete;
            return SimpleDdgiTransportProbeStateReason.Converged;
        }

        private int ResolveTransportResidualDistributionBucket(int probeIndex)
        {
            float residual = (uint)probeIndex < (uint)_probeLuminanceChangeEma.Length
                ? _probeLuminanceChangeEma[probeIndex]
                : float.NaN;
            if (!float.IsFinite(residual) || residual < 0.0f)
                return 7;
            float threshold = Math.Max(
                _settings.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance,
                0.000001f);
            if (residual <= threshold * 0.25f)
                return 0;
            if (residual <= threshold * 0.50f)
                return 1;
            if (residual <= threshold)
                return 2;
            if (residual <= threshold * 2.0f)
                return 3;
            if (residual <= threshold * 4.0f)
                return 4;
            if (residual <= threshold * 8.0f)
                return 5;
            return 6;
        }

        private int ResolveTransportSolverGenerationBucket(int probeIndex)
        {
            int generation = (uint)probeIndex <
                (uint)_probeTransportGenerationCounts.Length
                    ? _probeTransportGenerationCounts[probeIndex]
                    : 0;
            return generation switch
            {
                <= 0 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                <= 7 => 4,
                _ => 5
            };
        }

        private int ResolveTransportSourceEpochDistributionBucket(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeSourceLightingGenerations.Length)
                return 3;
            uint sourceGeneration = _probeSourceLightingGenerations[probeIndex];
            if (sourceGeneration == 0u)
                return 3;
            if (sourceGeneration == _sourceLightingGeneration)
                return 0;
            uint age = unchecked(_sourceLightingGeneration - sourceGeneration);
            return age == 1u ? 1 : 2;
        }

        private void RecordTransportSolverCompletionLatency(
            int probeIndex,
            int volumeIndex)
        {
            if ((uint)probeIndex >=
                    (uint)_probeTransportSolverCompletionRecordedSourceGenerations.Length ||
                (uint)probeIndex >= (uint)_probeSourceLightingGenerations.Length ||
                (uint)probeIndex >= (uint)_probeTransportGenerationCounts.Length ||
                (uint)probeIndex >= (uint)_probeLastSourceRefreshFrames.Length ||
                (uint)volumeIndex >=
                    (uint)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            {
                return;
            }

            uint sourceGeneration = _probeSourceLightingGenerations[probeIndex];
            if (sourceGeneration == 0u || sourceGeneration != _sourceLightingGeneration ||
                _probeTransportGenerationCounts[probeIndex] < Math.Max(
                    1,
                    _settings.GlobalIllumination.
                        SimpleDdgiTransportAcceleratedSweepCount) ||
                _probeTransportSolverCompletionRecordedSourceGenerations[probeIndex] ==
                    sourceGeneration)
            {
                return;
            }

            uint elapsed = unchecked(
                _frameIndex - _probeLastSourceRefreshFrames[probeIndex]);
            int elapsedFrames = checked((int)Math.Min(elapsed, int.MaxValue));
            int bucket = Math.Min(
                elapsedFrames,
                TransportSolverCompletionLatencyBucketCount - 1);
            int histogramIndex =
                volumeIndex * TransportSolverCompletionLatencyBucketCount + bucket;
            _volumeTransportSolverCompletionLatencyHistograms[histogramIndex] =
                SaturatingAdd(
                    _volumeTransportSolverCompletionLatencyHistograms[histogramIndex],
                    1);
            _volumeTransportSolverCompletionLatencySampleCounts[volumeIndex] =
                SaturatingAdd(
                    _volumeTransportSolverCompletionLatencySampleCounts[volumeIndex],
                    1);
            _volumeTransportSolverCompletionLatencyMaxFrames[volumeIndex] =
                Math.Max(
                    _volumeTransportSolverCompletionLatencyMaxFrames[volumeIndex],
                    elapsedFrames);
            _probeTransportSolverCompletionRecordedSourceGenerations[probeIndex] =
                sourceGeneration;
        }

        private static int ResolveHistogramPercentile(
            int[] histogram,
            int offset,
            int bucketCount,
            int sampleCount,
            double percentile)
        {
            if (sampleCount <= 0 || bucketCount <= 0)
                return 0;
            int target = Math.Max(
                1,
                checked((int)Math.Ceiling(sampleCount * percentile)));
            int cumulative = 0;
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                cumulative = SaturatingAdd(cumulative, histogram[offset + bucket]);
                if (cumulative >= target)
                    return bucket;
            }
            return bucketCount - 1;
        }

        private static void DecrementNonnegative(ref int value)
        {
            if (value > 0)
                value--;
        }

        private void RefreshProbeSchedulerLifecycleState(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeSchedulerLifecycleStates.Length ||
                (uint)probeIndex >= (uint)_probeLastUpdatedFrames.Length)
            {
                return;
            }

            byte previousState = _probeSchedulerLifecycleStates[probeIndex];
            uint previousLastUpdatedFrame =
                _probeSchedulerTrackedLastUpdatedFrames[probeIndex];
            uint currentLastUpdatedFrame = _probeLastUpdatedFrames[probeIndex];
            uint previousSourceRefreshFrame =
                _probeSchedulerTrackedSourceRefreshFrames[probeIndex];
            uint currentSourceRefreshFrame =
                (uint)probeIndex < (uint)_probeLastSourceRefreshFrames.Length
                    ? _probeLastSourceRefreshFrames[probeIndex]
                    : 0u;
            bool visible =
                (uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                (_probeSchedulingFlags[probeIndex] &
                    ProbeSchedulingVisibleFlag) != 0;
            bool fresh =
                (uint)probeIndex < (uint)_probeFresh.Length &&
                _probeFresh[probeIndex] != 0;
            bool scrollExposed =
                (uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                (_probeSchedulingFlags[probeIndex] &
                    ProbeSchedulingScrollExposedFlag) != 0;
            bool relocationPending =
                (uint)probeIndex < (uint)_probeRelocationPending.Length &&
                _probeRelocationPending[probeIndex] != 0;
            bool unpublished = fresh || relocationPending ||
                (TransportV2Active &&
                    ((uint)probeIndex >= (uint)_probeSourceRayCounts.Length ||
                        _probeSourceRayCounts[probeIndex] == 0));
            bool visibleUnsupported = visible &&
                IsProbeDataUnavailable(probeIndex);
            bool visiblePending = visible &&
                (fresh || scrollExposed || relocationPending || unpublished);
            bool generationStale = TransportV2Active &&
                (uint)probeIndex < (uint)_probeInactive.Length &&
                ShouldParticipateInTransportConvergence(
                    _probeInactive[probeIndex] != 0) &&
                ((uint)probeIndex >=
                        (uint)_probeSourceLightingGenerations.Length ||
                    _probeSourceLightingGenerations[probeIndex] !=
                        _sourceLightingGeneration);

            byte nextState = 0;
            if (fresh)
                nextState |= SchedulerLifecycleFreshFlag;
            if (scrollExposed)
                nextState |= SchedulerLifecycleScrollExposedFlag;
            if (relocationPending)
                nextState |= SchedulerLifecycleRelocationPendingFlag;
            if (unpublished)
                nextState |= SchedulerLifecycleUnpublishedFlag;
            if (visibleUnsupported)
                nextState |= SchedulerLifecycleVisibleUnsupportedFlag;
            if (visiblePending)
                nextState |= SchedulerLifecycleVisiblePendingFlag;
            if (generationStale)
                nextState |= SchedulerLifecycleGenerationStaleFlag;
            if (nextState == previousState &&
                currentLastUpdatedFrame == previousLastUpdatedFrame &&
                currentSourceRefreshFrame == previousSourceRefreshFrame)
            {
                return;
            }

            uint previousAge = CalculateProbeAge(
                previousLastUpdatedFrame,
                _frameIndex);
            uint currentAge = GetProbeAge(probeIndex);
            ulong lastUpdatedSerial = _frameSerial >= currentAge
                ? _frameSerial - currentAge
                : 0UL;
            uint previousSourceAge = CalculateProbeAge(
                previousSourceRefreshFrame,
                _frameIndex);
            uint currentSourceAge = CalculateProbeAge(
                currentSourceRefreshFrame,
                _frameIndex);
            ulong sourceRefreshSerial = _frameSerial >= currentSourceAge
                ? _frameSerial - currentSourceAge
                : 0UL;
            UpdateSchedulerAgeHeap(
                _schedulerFreshAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleFreshFlag,
                lastUpdatedSerial);
            UpdateSchedulerAgeHeap(
                _schedulerScrollExposedAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleScrollExposedFlag,
                lastUpdatedSerial);
            UpdateSchedulerAgeHeap(
                _schedulerRelocationPendingAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleRelocationPendingFlag,
                lastUpdatedSerial);
            UpdateSchedulerAgeHeap(
                _schedulerUnpublishedAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleUnpublishedFlag,
                lastUpdatedSerial);
            UpdateSchedulerAgeHeap(
                _schedulerVisibleUnsupportedAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleVisibleUnsupportedFlag,
                lastUpdatedSerial);
            UpdateSchedulerAgeHeap(
                _schedulerGenerationStaleAgeHeap,
                probeIndex,
                previousState,
                nextState,
                SchedulerLifecycleGenerationStaleFlag,
                sourceRefreshSerial);
            UpdateSchedulerAgeHistogram(
                _schedulerVisibleUnsupportedAgeHistogram,
                previousState,
                nextState,
                SchedulerLifecycleVisibleUnsupportedFlag,
                previousAge,
                currentAge);
            UpdateSchedulerAgeHistogram(
                _schedulerVisiblePendingAgeHistogram,
                previousState,
                nextState,
                SchedulerLifecycleVisiblePendingFlag,
                previousAge,
                currentAge);
            UpdateSchedulerAgeHistogram(
                _schedulerGenerationStaleAgeHistogram,
                previousState,
                nextState,
                SchedulerLifecycleGenerationStaleFlag,
                previousSourceAge,
                currentSourceAge);
            _probeSchedulerLifecycleStates[probeIndex] = nextState;
            _probeSchedulerTrackedLastUpdatedFrames[probeIndex] =
                currentLastUpdatedFrame;
            _probeSchedulerTrackedSourceRefreshFrames[probeIndex] =
                currentSourceRefreshFrame;
        }

        private static void UpdateSchedulerAgeHeap(
            SimpleDdgiSchedulerWakeHeap heap,
            int probeIndex,
            byte previousState,
            byte nextState,
            byte flag,
            ulong lastUpdatedSerial)
        {
            bool wasMember = (previousState & flag) != 0;
            bool isMember = (nextState & flag) != 0;
            if (!isMember)
            {
                if (wasMember)
                    heap.Remove(probeIndex);
                return;
            }

            heap.Schedule(probeIndex, lastUpdatedSerial);
        }

        private void UpdateSchedulerAgeHistogram(
            SimpleDdgiIncrementalAgeHistogram histogram,
            byte previousState,
            byte nextState,
            byte flag,
            uint previousAge,
            uint currentAge)
        {
            if ((previousState & flag) != 0)
                histogram.Remove(previousAge, _frameSerial);
            if ((nextState & flag) != 0)
                histogram.Add(currentAge, _frameSerial);
        }

        private static void AdjustSchedulerCounter(
            byte previousState,
            byte nextState,
            byte flag,
            ref int counter)
        {
            bool wasSet = (previousState & flag) != 0;
            bool isSet = (nextState & flag) != 0;
            if (wasSet == isSet)
                return;
            counter = isSet ? counter + 1 : Math.Max(0, counter - 1);
        }

        private void RefreshProbeSchedulerWake(int probeIndex)
        {
            _schedulerWakeHeap.Remove(probeIndex);
            if ((uint)probeIndex >= (uint)_probeCount)
                return;

            ulong nextWakeFrame = ulong.MaxValue;
            if (ShouldSkipInactiveProbe(probeIndex))
            {
                uint age = GetProbeAge(probeIndex);
                uint remaining = age < InactiveProbeRetryFrames
                    ? InactiveProbeRetryFrames - age
                    : 0u;
                nextWakeFrame = _frameSerial + Math.Max(1UL, remaining);
            }

            if (TransportV2Active &&
                (uint)probeIndex < (uint)_probeInactive.Length &&
                _probeInactive[probeIndex] == 0 &&
                (uint)probeIndex < (uint)_probeFresh.Length &&
                _probeFresh[probeIndex] == 0 &&
                (uint)probeIndex < (uint)_probeSourceLightingGenerations.Length &&
                _probeSourceLightingGenerations[probeIndex] == _sourceLightingGeneration &&
                (uint)probeIndex < (uint)_probeSourceRayCounts.Length &&
                _probeSourceRayCounts[probeIndex] > 0 &&
                (uint)probeIndex < (uint)_probeLastSourceRefreshFrames.Length)
            {
                uint elapsed = unchecked(
                    _frameIndex - _probeLastSourceRefreshFrames[probeIndex]);
                uint refreshFrames = checked((uint)Math.Max(
                    1,
                    EffectiveTransportSourceRefreshFrames));
                if (elapsed < refreshFrames)
                {
                    ulong periodicWake = _frameSerial + (refreshFrames - elapsed);
                    nextWakeFrame = Math.Min(nextWakeFrame, periodicWake);
                }
            }

            if (nextWakeFrame != ulong.MaxValue)
                _schedulerWakeHeap.Schedule(probeIndex, nextWakeFrame);
        }

        private void RecomputeDirtyLatencyOutstandingCount()
        {
            uint count = 0;
            for (int i = 0; i < _probeDirtyLatencyStates.Length; i++)
            {
                if (_probeDirtyLatencyStates[i] != 0 && count < uint.MaxValue)
                    count++;
            }

            _dirtyLatencyOutstandingEventCount = count;
        }

        private bool VolumeTableRemapped(int previousProbeCount, int previousVolumeCount)
        {
            if (previousProbeCount != _probeCount || previousVolumeCount != _volumeCount)
                return true;

            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume previous = _previousVolumeScratch[i];
                GPUSimpleDdgiVolume current = _volumeScratch[i];
                if (Kind(previous) != Kind(current) ||
                    SourceOrdinal(previous) != SourceOrdinal(current) ||
                    FirstProbe(previous) != FirstProbe(current) ||
                    CountX(previous) != CountX(current) ||
                    CountY(previous) != CountY(current) ||
                    CountZ(previous) != CountZ(current) ||
                    VolumeProbeCount(previous) != VolumeProbeCount(current) ||
                    !NearlyEqual(Spacing(previous), Spacing(current), 0.0001f) ||
                    !ApproximatelyEqual(Origin(previous), Origin(current)) ||
                    PhysicalOffsetX(previous) != PhysicalOffsetX(current) ||
                    PhysicalOffsetY(previous) != PhysicalOffsetY(current) ||
                    PhysicalOffsetZ(previous) != PhysicalOffsetZ(current) ||
                    !BitwiseEqual(previous.CacheLayout, current.CacheLayout))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BitwiseEqual(Vector4 left, Vector4 right) =>
            BitConverter.SingleToUInt32Bits(left.X) == BitConverter.SingleToUInt32Bits(right.X) &&
            BitConverter.SingleToUInt32Bits(left.Y) == BitConverter.SingleToUInt32Bits(right.Y) &&
            BitConverter.SingleToUInt32Bits(left.Z) == BitConverter.SingleToUInt32Bits(right.Z) &&
            BitConverter.SingleToUInt32Bits(left.W) == BitConverter.SingleToUInt32Bits(right.W);

        /// <summary>
        /// A changed volume table is not proof that an old physical-slot record
        /// can be copied into the new topology. Preserve compatible toroidal
        /// identities, but make every newly mapped/incompatible slot a fresh
        /// transaction with a new non-zero generation. This is also the source
        /// of the resident bootstrap records, so the private ABI cannot inherit
        /// an index-aligned CPU record from an unrelated volume.
        /// </summary>
        private bool ResetIncompatibleTopologyProbeState(int previousVolumeCount)
        {
            bool resetAnyProbe = false;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                bool compatible = false;
                for (int previousIndex = 0; previousIndex < previousVolumeCount; previousIndex++)
                {
                    GPUSimpleDdgiVolume previous = _previousVolumeScratch[previousIndex];
                    if (!MatchesVolumeIdentity(previous, current))
                        continue;

                    // A cell-aligned origin move is the supported toroidal
                    // remap. Any fractional movement changes the physical
                    // sample topology and must not reuse lifecycle metadata.
                    bool sameOrigin = ApproximatelyEqual(Origin(previous), Origin(current));
                    bool cellAligned = TryResolveCellDelta(
                        previous,
                        current,
                        out int deltaX,
                        out int deltaY,
                        out int deltaZ);
                    compatible = sameOrigin || (cellAligned &&
                        Math.Abs((long)deltaX) < CountX(current) &&
                        Math.Abs((long)deltaY) < CountY(current) &&
                        Math.Abs((long)deltaZ) < CountZ(current));
                    if (compatible)
                        break;
                }

                if (compatible)
                    continue;

                int firstProbe = FirstProbe(current);
                int probeCount = VolumeProbeCount(current);
                int end = Math.Min(_probeCount, checked(firstProbe + probeCount));
                for (int probeIndex = Math.Max(0, firstProbe); probeIndex < end; probeIndex++)
                {
                    resetAnyProbe = true;
                    _probeGenerations[probeIndex] = AdvanceProbeGeneration(
                        _probeGenerations[probeIndex]);
                    _probeFresh[probeIndex] = 1;
                    _probeInactive[probeIndex] = 0;
                    _probeRelocationPending[probeIndex] = 0;
                    _probeVisibilityValid[probeIndex] = 0;
                    _probeSchedulingFlags[probeIndex] = 0;
                    _probeInvalidationMarkers[probeIndex] = 0;
                    _probeDirtyReasons[probeIndex] =
                        (byte)SimpleDdgiSchedulerCandidateReason.Fresh;
                    _probeRoutineMaintenancePending[probeIndex] = 0;
                    _probeVisibilityImportance[probeIndex] = 0;
                    _probeRelocations[probeIndex] = Vector3.Zero;
                    _probeActiveWeights[probeIndex] = 1.0f;
                    _probeClassifications[probeIndex] = 0u;
                    _probeStableUpdateCounts[probeIndex] = 0;
                    _probeLuminanceChangeEma[probeIndex] = 0.0f;
                    _probeLastUpdatedFrames[probeIndex] = unchecked(_frameIndex - 1u);
                    _probeSourceLightingGenerations[probeIndex] = 0u;
                    _probeLastSourceRefreshFrames[probeIndex] = 0u;
                    AdvanceProbeSourceEpoch(probeIndex);
                    _probeSourceRayCounts[probeIndex] = 0;
                    _probeTransportGenerationCounts[probeIndex] = 0;
                    _probeAtmosphereCohortFlags[probeIndex] = 0;
                    _probeDirtyLatencyStates[probeIndex] = 1;
                    _probeDirtyLatencyStartFrames[probeIndex] = _frameIndex;
                    if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                        _probeStateDirtySlots.Add(probeIndex);
                }
            }

            if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
                _probeStateUploadRequired = true;
            _schedulerRebuildRequired = true;
            _schedulerVisibilityFullRefreshRequired = true;
            RecomputeDirtyLatencyOutstandingCount();
            RebuildAtmosphereCohortCounters();
            return resetAnyProbe;
        }

        private void AdvanceVolumeTableGenerationAndDropPendingReadbacks()
        {
            _volumeTableGeneration = AdvanceSourceLightingGeneration(_volumeTableGeneration);
            _physicalOwnershipGeneration = AdvanceSourceLightingGeneration(
                _physicalOwnershipGeneration);
            BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
            for (int i = 0; i < _probeStateReadbackRecorded.Length; i++)
            {
                if (_probeStateReadbackRecorded[i])
                    _resourceGenerationRejectionCount = SaturatingAdd(
                        _resourceGenerationRejectionCount,
                        1UL);
                DropProbeStateReadbackSlot(i);
            }
            Array.Clear(_probeDirtyLatencyStates);
            Array.Clear(_probeDirtyLatencyStartFrames);
            Array.Clear(_probeSchedulingFlags);
            Array.Clear(_probeDirtyReasons);
            Array.Clear(_probeRoutineMaintenancePending);
            Array.Clear(_probeVisibilityImportance);
            RequirePersistentSchedulerRebuild();
            _dirtyLatencyOutstandingEventCount = 0;
            _probeStateReadbackValid = 0;
            _probeConvergenceReadbackValid = 0;
        }

        private void UpdateLightingDirtyState(
            GlobalIlluminationSettings settings,
            ulong lightingSignature,
            uint dirtyReasonFlags,
            bool suppressSignatureBoost,
            bool cohortLightingTransition)
        {
            if (!_hasLightingSignature)
            {
                _lastLightingSignature = lightingSignature;
                _sourceLightingGeneration = 1u;
                BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
                _activeDirtyReasonFlags = 0u;
                _hasLightingSignature = true;
                return;
            }

            if (lightingSignature != _lastLightingSignature)
            {
                _lastLightingSignature = lightingSignature;
                _sourceLightingGeneration = AdvanceSourceLightingGeneration(_sourceLightingGeneration);
                _sourceCohortQuietFrames = 0;
                bool useCohortTransition =
                    cohortLightingTransition &&
                    TransportV2Active &&
                    UsesSteppedAtmosphereSourcePolicy;
                if (useCohortTransition)
                {
                    _sourceCohortTransitionActive = true;
                    _sourceCohortTransitionStartFrame = _frameIndex;
                    _sourceCohortTransitionCount = SaturatingAdd(
                        _sourceCohortTransitionCount,
                        1UL);
                    // A field already warming may retain residual evidence,
                    // but the end-to-end deadline belongs to the newest source
                    // generation. A late sky/reflection publication otherwise
                    // inherits the boot-time clock and enters recovery just as
                    // its source sweep completes.
                    _transportGlobalConvergenceStartFrame =
                        ResolveTransportConvergenceStartFrameAfterSourceChange(
                            _transportGlobalConvergenceStartFrame,
                            _frameIndex,
                            _transportGlobalConvergencePending);
                    if (_transportGlobalConvergencePending)
                        _transportGlobalWatchdogRefreshWaveStarted = false;
                    _transportGlobalConvergenceSourceGeneration =
                        _sourceLightingGeneration;
                }
                else
                {
                    _sourceCohortTransitionActive = false;
                    BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
                }
                // The physical cache remains allocated, but every slot must trace
                // its source once under the new light/environment signature.
                _sourceCacheInvalidationCount = SaturatingAdd(
                    _sourceCacheInvalidationCount,
                    (ulong)Math.Max(_probeCount, 0));
                _lightingDirtyFrames = !useCohortTransition &&
                    !suppressSignatureBoost &&
                    settings.SimpleDdgiLightingDirtyBoostEnabled
                    ? settings.SimpleDdgiLightingDirtyFrameCount
                    : 0;
                _activeDirtyReasonFlags = _lightingDirtyFrames > 0 ? dirtyReasonFlags : 0u;
            }
            else if (_lightingDirtyFrames > 0)
            {
                _lightingDirtyFrames--;
                if (_lightingDirtyFrames == 0)
                    _activeDirtyReasonFlags = 0u;
            }
        }

        private void UpdateTransportV2ActivationState(bool sourceCacheCapacityWillChange)
        {
            bool transportV2Active = TransportV2Active;
            if (transportV2Active && !_transportV2WasActive)
            {
                // V1 never maintains V2 cache contents. Require a full source
                // rebuild when V2 is enabled at runtime rather than trusting an
                // old allocation whose direct-light contract may have changed.
                InvalidateTransportSourceCacheMetadata(
                    recordInvalidation: !sourceCacheCapacityWillChange);
            }
            else if (!transportV2Active && _transportV2WasActive)
            {
                // Preserve the pending marker so a later re-enable performs a
                // bounded global solve even if no lighting signature changed.
                BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
            }

            _transportV2WasActive = transportV2Active;
        }

        private void UpdateTransportCalibrationState(
            GlobalIlluminationSettings settings,
            bool sourceCacheCapacityWillChange)
        {
            if (!TransportV2Active)
            {
                // A V1 interval never maintains cache/solver equivalence. Make
                // the next V2 activation establish a new baseline after its
                // explicit cache invalidation rather than comparing stale knobs.
                _hasTransportCalibrationSignatures = false;
                return;
            }

            ulong sourceSignature = CalculateTransportSourceCalibrationFingerprint(settings);
            ulong solverSignature = CalculateTransportSolverCalibrationFingerprint(settings);
            if (!_hasTransportCalibrationSignatures)
            {
                _lastTransportSourceCalibrationSignature = sourceSignature;
                _lastTransportSolverCalibrationSignature = solverSignature;
                _hasTransportCalibrationSignatures = true;
                return;
            }

            bool sourceChanged = sourceSignature != _lastTransportSourceCalibrationSignature;
            bool solverChanged = solverSignature != _lastTransportSolverCalibrationSignature;
            _lastTransportSourceCalibrationSignature = sourceSignature;
            _lastTransportSolverCalibrationSignature = solverSignature;
            if (!sourceChanged && !solverChanged)
                return;

            if (sourceChanged)
            {
                // Cached source terms are not interchangeable across a changed
                // ray/material/light-selection contract. Advance the source
                // generation as well as clearing CPU cache metadata so no
                // solver-only update can read the prior estimator.
                _sourceLightingGeneration = AdvanceSourceLightingGeneration(_sourceLightingGeneration);
                InvalidateTransportSourceCacheMetadata(
                    recordInvalidation: !sourceCacheCapacityWillChange);
            }
            else
            {
                // Relaxation, albedo limiting, and convergence policy do not
                // invalidate direct/sky/emissive source work. Preserve it and
                // restart only the recursive field, which makes live transport
                // calibration responsive without paying a full ray retrace.
                ResetTransportSolverConvergenceMetadata();
            }

            BeginTransportCalibrationBoost(settings);
            _transportCalibrationChangeCount = SaturatingAdd(
                _transportCalibrationChangeCount,
                1UL);
        }

        private void ResetTransportSolverConvergenceMetadata()
        {
            if (_probeTransportGenerationCounts.Length > 0)
                Array.Clear(_probeTransportGenerationCounts);
            if (_probeStableUpdateCounts.Length > 0)
                Array.Clear(_probeStableUpdateCounts);
            if (_probeLuminanceChangeEma.Length > 0)
                Array.Clear(_probeLuminanceChangeEma);
            BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
        }

        private void BeginTransportCalibrationBoost(GlobalIlluminationSettings settings)
        {
            if (!settings.SimpleDdgiLightingDirtyBoostEnabled || _probeCount <= 0)
                return;

            _lightingDirtyFrames = Math.Max(
                _lightingDirtyFrames,
                settings.SimpleDdgiLightingDirtyFrameCount);
            _activeDirtyReasonFlags |= TransportCalibrationDirtyReasonFlag;
        }

        private void BeginTransportGlobalConvergence(
            bool preservePeriodicSourceRefreshWave = false,
            bool forceFieldEvidenceReset = false,
            SimpleDdgiTransportCertificationReason certificationReason =
                SimpleDdgiTransportCertificationReason.SourceRepairRequired,
            SimpleDdgiTransportRecoveryAction recoveryAction =
                SimpleDdgiTransportRecoveryAction.None)
        {
            CancelTransportSolveDrain();
            bool convergenceWasPending = _transportGlobalConvergencePending;
            bool resetFieldEvidence = ShouldResetTransportFieldEvidence(
                forceFieldEvidenceReset);
            bool startConvergenceWave = ShouldStartTransportConvergenceWave(
                convergenceWasPending,
                resetFieldEvidence);
            if (startConvergenceWave &&
                _probeRoutineMaintenancePending.Length > 0)
            {
                Array.Clear(_probeRoutineMaintenancePending);
            }
            if (ShouldClearTransportPeriodicSourceRefreshWave(
                    preservePeriodicSourceRefreshWave,
                    convergenceWasPending,
                    resetFieldEvidence))
            {
                _transportPeriodicSourceRefreshWavePending = false;
                _transportPeriodicSourceRefreshWaveCutoffFrame = 0u;
                _transportNextPeriodicSourceRefreshFrame = 0u;
            }
            _transportGlobalConvergencePending = true;
            _transportGlobalSourceRepairPhasePending = true;
            if (startConvergenceWave)
            {
                // A repair that begins after a completed solve is a new bounded
                // propagation wave, but it is not a new lighting field. Give it
                // its own watchdog interval without discarding quiet probes'
                // fixed-point evidence. Re-arms while a wave is active leave
                // this clock untouched.
                _transportGlobalWatchdogRefreshWaveStarted = false;
                _transportGlobalConvergenceStartFrame = _frameIndex;
            }
            if (resetFieldEvidence)
            {
                _transportFieldConvergenceEvidenceResetPending = true;
            }
            _transportGlobalConvergenceSourceGeneration = _sourceLightingGeneration;
            if (TailCertificationEnabled)
            {
                _transportSolveController.BeginSourceRepair(
                    CreateTransportTailGenerations(),
                    certificationReason,
                    recoveryAction);
                _transportTailSummary = _transportSolveController.LastSummary;
            }
            if (resetFieldEvidence)
            {
                _probeConvergenceReadbackValid = 0;
                // Downstream probes retain their own source epoch when one source
                // changes. Drop every in-flight convergence snapshot so
                // pre-boundary residuals cannot repopulate stability after the
                // field reset.
                for (int i = 0; i < _probeStateReadbackRecorded.Length; i++)
                    DropProbeStateReadbackSlot(i);
            }
            RequirePersistentSchedulerRebuild();
        }

        internal static bool ShouldResetTransportFieldEvidence(
            bool forceFieldEvidenceReset) =>
            forceFieldEvidenceReset;

        internal static uint ResolveTransportConvergenceStartFrameAfterSourceChange(
            uint currentStartFrame,
            uint sourceChangeFrame,
            bool convergencePending) =>
            convergencePending ? sourceChangeFrame : currentStartFrame;

        internal static bool ShouldStartTransportConvergenceWave(
            bool globalConvergencePending,
            bool resetFieldEvidence) =>
            !globalConvergencePending || resetFieldEvidence;

        internal static bool ShouldClearTransportPeriodicSourceRefreshWave(
            bool preservePeriodicSourceRefreshWave,
            bool globalConvergencePending,
            bool resetFieldEvidence) =>
            !preservePeriodicSourceRefreshWave &&
            (resetFieldEvidence || !globalConvergencePending);

        private void PrepareTransportGlobalConvergenceState()
        {
            if (!TransportV2Active)
                return;

            if (_transportGlobalConvergenceSourceGeneration != _sourceLightingGeneration)
            {
                if (_sourceCohortTransitionActive)
                    _transportGlobalConvergenceSourceGeneration = _sourceLightingGeneration;
                else
                    BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
            }
            ApplyPendingTransportFieldConvergenceEvidenceReset();
        }

        private void EvaluateTransportGlobalConvergenceState()
        {
            if (!TransportV2Active)
                return;

            if (TailCertificationEnabled)
            {
                // V2 retirement is exclusively certificate-driven. The legacy
                // 95%/generation/EMA policy below remains available to V1 and
                // diagnostics, but it is never consulted for this path.
                PrepareTailSolveController();
                if (!_transportSolveController.IsCertified)
                {
                    // Freeze before pass predicates are evaluated so the
                    // publication/commit passes cannot mutate the canonical
                    // field in the same frame that the audit begins. This
                    // branch is resident-only: CPU/mirror fallback modes
                    // disable the resident certificate and use the explicit
                    // legacy convergence policy below.
                    if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                        _transportSolveController.IsSolveEpochComplete &&
                        !_transportSolveDrainPending)
                    {
                        TryBeginTransportTailAudit();
                    }
                    _transportGlobalConvergencePending = true;
                    return;
                }

                _transportGlobalConvergencePending = false;
                _transportGlobalSourceRepairPhasePending = false;
                _publishedPropagationGeneration = _transportGeneration;
                RequirePersistentSchedulerRebuild();
                return;
            }

            if (!_transportGlobalConvergencePending || _probeCount <= 0)
                return;

            uint globalSolveAge =
                unchecked(_frameIndex - _transportGlobalConvergenceStartFrame);
            if (ShouldStartTransportGlobalSourceRefreshWatchdogWave(
                    true,
                    _transportPeriodicSourceRefreshWavePending,
                    _transportGlobalWatchdogRefreshWaveStarted,
                    globalSolveAge,
                    EffectiveTransportSourceRefreshFrames))
            {
                _transportPeriodicSourceRefreshWavePending = true;
                _transportPeriodicSourceRefreshWaveCutoffFrame = _frameIndex;
                _transportGlobalWatchdogRefreshWaveStarted = true;
                _transportGlobalSourceRepairPhasePending = true;
                RefreshPersistentSchedulerState();
            }

            int participatingProbeCount = _schedulerParticipatingProbeCount;
            int sourceRepairProbeCount = _schedulerSourceRepairProbeCount;
            int pendingConvergenceProbeCount =
                _schedulerPendingConvergenceProbeCount;

            int sourceRepairAllowance =
                ResolveTransportGlobalConvergenceSourceRepairAllowance(
                    participatingProbeCount);
            if (sourceRepairProbeCount > sourceRepairAllowance)
            {
                _transportGlobalSourceRepairPhasePending = true;
                return;
            }

            if (ShouldStartTransportConvergenceEvidencePhase(
                    _transportGlobalSourceRepairPhasePending,
                    sourceRepairProbeCount))
            {
                // A genuine source/layout boundary already latched one field-wide
                // reset in BeginTransportGlobalConvergence. Repairs clear their
                // own per-probe evidence; repeating a global wipe here would make
                // a continuously repaired field incapable of converging.
                _transportGlobalSourceRepairPhasePending = false;
            }

            if (!CanCompleteTransportGlobalConvergence(
                    participatingProbeCount,
                    sourceRepairProbeCount,
                    pendingConvergenceProbeCount))
            {
                return;
            }

            // At least 95% of the source-ready receiver population has reached a
            // simultaneous stable state. The bounded tail remains in the cached
            // solver maintenance queues after the global barrier is released, so
            // it can converge without forcing already-stable probes to dispatch.
            _transportGlobalConvergencePending = false;
            _transportPeriodicSourceRefreshWavePending = false;
            _publishedPropagationGeneration = _transportGeneration;
            RequirePersistentSchedulerRebuild();
        }

        private SimpleDdgiTransportGenerations CreateTransportTailGenerations()
        {
            uint volume = NonZeroGeneration(_volumeTableGeneration);
            uint ownership = NonZeroGeneration(_physicalOwnershipGeneration);
            uint source = NonZeroGeneration(_sourceLightingGeneration);
            uint sourceEpoch = NonZeroGeneration(_sourceEpochGeneration);
            uint operatorGeneration = NonZeroGeneration(
                unchecked((uint)(_lastTransportSolverCalibrationSignature ^
                    (_lastTransportSolverCalibrationSignature >> 32))));
            uint canonical = NonZeroGeneration(_transportGeneration);
            uint solve = NonZeroGeneration(_transportSolveController.SolveEpoch);
            uint audit = NonZeroGeneration(_transportSolveController.AuditEpoch);
            // GPU-resident uploads are frame transactions, not transport
            // queue-generation changes. The resident scheduler owns the queue
            // and remains frozen while the audit is in flight; using the
            // per-frame serial here would cancel every audit on the next
            // upload before its delayed readback could be consumed.
            uint queue = _schedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? NonZeroGeneration(_gpuScheduler.ResourceGeneration)
                : NonZeroGeneration(_updateTransactionSerial);
            uint scheduler = NonZeroGeneration(_gpuScheduler.ResourceGeneration);
            return new SimpleDdgiTransportGenerations(
                volume,
                ownership,
                source,
                sourceEpoch,
                operatorGeneration,
                canonical,
                solve,
                audit,
                queue,
                scheduler);
        }

        private static uint NonZeroGeneration(uint value) => value == 0u ? 1u : value;

        private void PrepareTailSolveController()
        {
            if (!TransportV2Active || !TailCertificationEnabled)
                return;

            // Visit stamps are indexed by global physical probe index. Their
            // capacity is therefore the complete field capacity; the solve
            // epoch's required count remains the active participant count.
            _transportSolveController.EnsureParticipantCapacity(_probeCount);

            SimpleDdgiTransportGenerations generations = CreateTransportTailGenerations();
            if (_transportSolveController.Phase ==
                SimpleDdgiTransportPhase.UnsupportedTolerance)
            {
                return;
            }
            if (_transportSolveController.Phase == SimpleDdgiTransportPhase.AuditFrozen)
            {
                if (_transportSolveController.FrozenGenerations != generations)
                    CancelTransportTailAudit(SimpleDdgiTransportCertificationReason.GenerationsChanged);
                return;
            }

            if (_transportSolveController.Phase ==
                    SimpleDdgiTransportPhase.ParticipantReconciliation &&
                _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                (!_gpuSchedulerFeedbackValid ||
                 _gpuSchedulerFeedbackFrameSerial <=
                    _transportParticipantReconciliationFeedbackSerial))
            {
                return;
            }

            if (_transportSolveController.Phase ==
                SimpleDdgiTransportPhase.FailClosedRecovery)
            {
                _transportSolveController.BeginSourceRepair(generations);
                _transportTailSummary = _transportSolveController.LastSummary;
                return;
            }

            if (ShouldRestartTransportConvergenceForStaleCertificate(
                    _transportSolveController.Phase,
                    _transportSolveController.LastSummary.IsCurrent(generations)))
            {
                // The prior wave set the internal pending bit false when its
                // certificate was accepted. A later source/ownership boundary
                // must therefore start a new deadline clock as well as
                // invalidating the certificate. Merely setting the controller
                // back to SourceRepair leaves the old boot-time clock armed and
                // can immediately fail a healthy periodic recertification.
                BeginTransportGlobalConvergence(
                    preservePeriodicSourceRefreshWave:
                        _transportPeriodicSourceRefreshWavePending,
                    certificationReason:
                        SimpleDdgiTransportCertificationReason.GenerationsChanged);
                return;
            }

            if (_transportSolveController.Phase ==
                    SimpleDdgiTransportPhase.AcceleratedSolve &&
                !_transportSolveController.TryRefreshSolveGenerations(generations))
            {
                CancelTransportSolveDrain();
                _transportSolveController.Invalidate(
                    generations,
                    SimpleDdgiTransportCertificationReason.GenerationsChanged,
                    requireSourceRepair: true);
                _transportTailSummary = _transportSolveController.LastSummary;
                return;
            }

            // Resident participant/source-repair counts are authoritative only
            // after a generation-matched delayed feedback packet has arrived.
            // Treating the zero-initialized host mirror as a genuine empty
            // field would complete a zero-participant solve epoch, freeze an
            // audit before the first scheduler transaction, and permanently
            // prevent fresh probes from receiving their source rays.
            if (!CanPrepareTailSolveParticipantCounts(
                    _schedulerMode,
                    _gpuSchedulerFeedbackValid))
            {
                return;
            }

            // The old phase bit is intentionally not authoritative for V2; it
            // is also kept latched for legacy atmosphere diagnostics. Source
            // readiness is the concrete participant gate here.
            int participantCount = _schedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _transportResidentParticipantCount
                : _schedulerParticipatingProbeCount;
            int sourceRepairCount = _schedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _transportResidentSourceRepairProbeCount
                : _schedulerSourceRepairProbeCount;
            bool sourceRepairPending = sourceRepairCount > 0 ||
                (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                 _transportPeriodicSourceRefreshWavePending);
            if (sourceRepairPending)
            {
                CancelTransportSolveDrain();
                if (_transportSolveController.Phase != SimpleDdgiTransportPhase.SourceRepair)
                {
                    _transportSolveController.BeginSourceRepair(generations);
                    _transportTailSummary = _transportSolveController.LastSummary;
                }
                return;
            }

            if (_transportSolveController.Phase == SimpleDdgiTransportPhase.SourceRepair ||
                _transportSolveController.Phase == SimpleDdgiTransportPhase.Tracking ||
                _transportSolveController.Phase ==
                    SimpleDdgiTransportPhase.ParticipantReconciliation)
            {
                CancelTransportSolveDrain();
                _transportSolveController.BeginSolveEpoch(
                    generations,
                    Math.Max(0, participantCount));
                _transportTailSummary = _transportSolveController.LastSummary;
            }
        }

        internal static bool CanPrepareTailSolveParticipantCounts(
            SimpleDdgiSchedulerMode schedulerMode,
            bool gpuSchedulerFeedbackValid) =>
            schedulerMode != SimpleDdgiSchedulerMode.GpuResident ||
            gpuSchedulerFeedbackValid;

        internal static bool ShouldRestartTransportConvergenceForStaleCertificate(
            SimpleDdgiTransportPhase phase,
            bool certificateCurrent) =>
            phase == SimpleDdgiTransportPhase.Certified && !certificateCurrent;

        internal static uint ResolveNextPeriodicSourceRefreshFrame(
            uint certificateFrame,
            int refreshIntervalFrames) =>
            unchecked(certificateFrame +
                (uint)Math.Max(1, refreshIntervalFrames));

        private void TrackTransportTailProgress()
        {
            if (!TailCertificationEnabled ||
                _transportTailProgressObservedFrame == _frameIndex)
            {
                return;
            }

            _transportTailProgressObservedFrame = _frameIndex;
            var stamp = new TransportTailProgressStamp(
                _transportSolveController.Phase,
                _transportSolveController.LastReason,
                _transportSolveController.SolveEpoch,
                _transportSolveController.AuditEpoch,
                _transportSolveController.ExpectedParticipantCount,
                _transportSolveController.VisitedParticipantCount,
                _transportAuditProbeCursor,
                _sourceLightingGeneration,
                _sourceEpochGeneration,
                _transportGeneration,
                _transportResidentParticipantCount,
                _transportResidentSourceRepairProbeCount,
                _transportSolveController.RecoveryGeneration,
                _transportSolveDrainPending);
            bool madeProgress = !_hasTransportTailProgressStamp ||
                stamp != _transportTailProgressStamp;
            _transportTailProgressStamp = stamp;
            _hasTransportTailProgressStamp = true;
            _transportSolveController.ObserveProgressFrame(madeProgress);
            bool recoveryEligible =
                _transportSolveController.Phase is
                    SimpleDdgiTransportPhase.SourceRepair or
                    SimpleDdgiTransportPhase.AcceleratedSolve or
                     SimpleDdgiTransportPhase.ParticipantReconciliation or
                     SimpleDdgiTransportPhase.FailClosedRecovery;
            // A changing epoch/phase stamp is local progress, not proof that
            // the complete source -> solve -> certificate transaction is
            // converging. Enforce the separately computed end-to-end deadline
            // and start one fresh private rebuild wave when it expires.
            if (_transportGlobalConvergencePending && recoveryEligible &&
                TransportGlobalConvergenceElapsedFrames >=
                    TransportTailConvergenceDeadlineFrames)
            {
                _transportConvergenceDeadlineRecoveryCount = SaturatingAdd(
                    _transportConvergenceDeadlineRecoveryCount,
                    1UL);
                _transportSolveController.EnterConvergenceDeadlineRecovery(
                    CreateTransportTailGenerations());
                _transportTailSummary = _transportSolveController.LastSummary;
                ApplyTransportTailRecovery(
                    _transportSolveController.RecoveryAction,
                    _transportSolveController.LastReason);
                return;
            }
            if (!madeProgress && recoveryEligible &&
                _transportSolveController.NoProgressFrames >=
                    TransportTailConvergenceDeadlineFrames)
            {
                _transportSourceNoProgressRecoveryCount = SaturatingAdd(
                    _transportSourceNoProgressRecoveryCount,
                    1UL);
                _transportSolveController.EnterSourceCohortRecovery(
                    CreateTransportTailGenerations());
                _transportTailSummary = _transportSolveController.LastSummary;
                ApplyTransportTailRecovery(
                    _transportSolveController.RecoveryAction,
                    _transportSolveController.LastReason);
            }
        }

        private readonly record struct TransportTailProgressStamp(
            SimpleDdgiTransportPhase Phase,
            SimpleDdgiTransportCertificationReason Reason,
            uint SolveEpoch,
            uint AuditEpoch,
            int ExpectedParticipantCount,
            int VisitedParticipantCount,
            int AuditProbeCursor,
            uint SourceLightingGeneration,
            uint SourceEpochGeneration,
            uint CanonicalGeneration,
            int ResidentParticipantCount,
            int ResidentSourceRepairCount,
            uint RecoveryGeneration,
            bool SolveDrainPending);

        private void ApplyPendingTransportFieldConvergenceEvidenceReset()
        {
            if (!_transportFieldConvergenceEvidenceResetPending)
                return;

            if (_probeTransportGenerationCounts.Length > 0)
                Array.Clear(_probeTransportGenerationCounts);
            if (_probeStableUpdateCounts.Length > 0)
                Array.Clear(_probeStableUpdateCounts);
            if (_probeLuminanceChangeEma.Length > 0)
                Array.Clear(_probeLuminanceChangeEma);
            _transportFieldConvergenceEvidenceResetPending = false;
            RequirePersistentSchedulerRebuild();
        }

        internal static bool ShouldParticipateInTransportConvergence(bool inactive) =>
            !inactive;

        internal static bool ShouldStartTransportConvergenceEvidencePhase(
            bool sourceRepairPhasePending,
            int sourceRepairProbeCount) =>
            sourceRepairPhasePending &&
            Math.Max(0, sourceRepairProbeCount) == 0;

        internal static bool ShouldStartTransportGlobalSourceRefreshWatchdogWave(
            bool globalConvergencePending,
            bool periodicRefreshWavePending,
            bool watchdogWaveAlreadyStarted,
            uint globalSolveAgeFrames,
            int periodicRefreshFrames)
        {
            uint watchdogFrames = (uint)Math.Min(
                int.MaxValue,
                (long)Math.Max(1, periodicRefreshFrames) *
                    TransportGlobalSourceRefreshWatchdogFrameMultiplier);
            return globalConvergencePending &&
                !periodicRefreshWavePending &&
                !watchdogWaveAlreadyStarted &&
                globalSolveAgeFrames >= watchdogFrames;
        }

        internal static int ResolveTransportGlobalConvergenceSourceRepairAllowance(
            int participatingProbeCount)
        {
            int participants = Math.Max(0, participatingProbeCount);
            return Math.Max(1, participants / 1_024);
        }

        internal static bool CanCompleteTransportGlobalConvergence(
            int participatingProbeCount,
            int sourceRepairProbeCount,
            int pendingConvergenceProbeCount)
        {
            int participants = Math.Max(0, participatingProbeCount);
            int sourceRepair = Math.Clamp(sourceRepairProbeCount, 0, participants);
            int sourceReady = participants - sourceRepair;
            int pending = Math.Clamp(
                pendingConvergenceProbeCount,
                0,
                sourceReady);
            int converged = sourceReady - pending;
            bool minimumConvergedPopulationReached = sourceReady == 0 ||
                (long)converged * 100L >= (long)sourceReady * 95L;
            return minimumConvergedPopulationReached &&
                sourceRepair <= ResolveTransportGlobalConvergenceSourceRepairAllowance(participants);
        }

        private static bool RequiresGlobalInvalidation(IReadOnlyList<DdgiDirtyRegion>? dirtyRegions)
        {
            if (dirtyRegions == null)
                return false;

            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                DdgiDirtyReason reason = dirtyRegions[i].Reason;
                if (reason is DdgiDirtyReason.DirectionalLightChanged or
                    DdgiDirtyReason.StreamIn or
                    DdgiDirtyReason.StreamOut or
                    DdgiDirtyReason.Teleport)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool ShouldBeginTransportGlobalConvergenceForInvalidation(
            bool transportV2Active,
            int newlyInvalidatedProbeCount,
            bool hasRegionalDirtyWork,
            bool requiresGlobalInvalidation,
            bool atlasFresh,
            bool recenteredThisFrame) =>
            transportV2Active &&
            newlyInvalidatedProbeCount > 0 &&
            (atlasFresh ||
                recenteredThisFrame ||
                !hasRegionalDirtyWork ||
                requiresGlobalInvalidation);

        private void MarkRegionalDirtyProbes(
            IReadOnlyList<DdgiDirtyRegion> dirtyRegions)
        {
            for (int regionIndex = 0; regionIndex < dirtyRegions.Count; regionIndex++)
            {
                DdgiDirtyRegion dirty = dirtyRegions[regionIndex];
                BoundingBox bounds = dirty.InfluenceBounds;
                uint dirtyReasonFlag = ToSimpleDirtyReasonFlag(dirty.Reason);
                _regionalDirtyReasonFlags |= dirtyReasonFlag;

                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                {
                    GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                    int countX = CountX(volume);
                    int countY = CountY(volume);
                    int countZ = CountZ(volume);
                    float spacing = Spacing(volume);
                    Vector3 origin = Origin(volume);
                    Vector3 logicalMax = origin + new Vector3(
                        Math.Max(countX - 1, 0) * spacing,
                        Math.Max(countY - 1, 0) * spacing,
                        Math.Max(countZ - 1, 0) * spacing);
                    if (!BoundsIntersect(bounds, new BoundingBox(origin, logicalMax)))
                        continue;

                    // One extra cell represents the bounded transport influence of
                    // a surface/light change and avoids a hard update seam at the
                    // AABB boundary.  Overlapping dirty events deduplicate through
                    // MarkProbeFresh's already-fresh fast path.
                    int minX = ClampProbeRangeStart(bounds.Min.X, origin.X, spacing, countX);
                    int minY = ClampProbeRangeStart(bounds.Min.Y, origin.Y, spacing, countY);
                    int minZ = ClampProbeRangeStart(bounds.Min.Z, origin.Z, spacing, countZ);
                    int maxX = ClampProbeRangeEnd(bounds.Max.X, origin.X, spacing, countX);
                    int maxY = ClampProbeRangeEnd(bounds.Max.Y, origin.Y, spacing, countY);
                    int maxZ = ClampProbeRangeEnd(bounds.Max.Z, origin.Z, spacing, countZ);

                    for (int z = minZ; z <= maxZ; z++)
                        for (int y = minY; y <= maxY; y++)
                            for (int x = minX; x <= maxX; x++)
                            {
                                int physicalLocal = CalculatePhysicalProbeLocalIndex(volume, x, y, z);
                                int probeIndex = FirstProbe(volume) + physicalLocal;
                                MarkProbeFresh(
                                    probeIndex,
                                    scrollExposed: false,
                                    dirty: true,
                                    forceGenerationAdvance: true,
                                    dirtyReasonFlags: dirtyReasonFlag);
                            }
                }
            }
        }

        private static int ClampProbeRangeStart(float value, float origin, float spacing, int count) =>
            Math.Clamp((int)MathF.Floor((value - origin) / Math.Max(spacing, 0.001f)) - 1, 0, Math.Max(count - 1, 0));

        private static int ClampProbeRangeEnd(float value, float origin, float spacing, int count) =>
            Math.Clamp((int)MathF.Ceiling((value - origin) / Math.Max(spacing, 0.001f)) + 1, 0, Math.Max(count - 1, 0));

        private static bool BoundsIntersect(BoundingBox left, BoundingBox right) =>
            left.Min.X <= right.Max.X && left.Max.X >= right.Min.X &&
            left.Min.Y <= right.Max.Y && left.Max.Y >= right.Min.Y &&
            left.Min.Z <= right.Max.Z && left.Max.Z >= right.Min.Z;

        private static uint ToSimpleDirtyReasonFlag(DdgiDirtyReason reason) => reason switch
        {
            DdgiDirtyReason.LocalLightChanged or DdgiDirtyReason.DirectionalLightChanged => 1u << 0,
            DdgiDirtyReason.EmissiveChanged => 1u << 1,
            _ => 1u << 2
        };

        private int ResolveLightingDirtyUpdateBudget(GlobalIlluminationSettings settings, int baseUpdateBudget)
        {
            int capacity = Math.Clamp(baseUpdateBudget, 0, _probeCount);
            if (!settings.SimpleDdgiLightingDirtyBoostEnabled || _lightingDirtyFrames <= 0 || capacity <= 0)
                return capacity;

            int boosted = ResolveConfiguredRequestBudget(
                capacity,
                _probeCount,
                lightingDirtyBoostEnabled: true);
            _lightingDirtyBoostedCapacity = Math.Max(0, boosted - capacity);
            return boosted;
        }

        /// <summary>
        /// Resolves the immutable request-count ceiling for the current layout.
        /// The dirty-response allowance is a declared tier capability rather
        /// than scheduled output, so sparse queues cannot shrink the reported
        /// cap and bounded recovery cannot be misreported as an overrun.
        /// </summary>
        internal static int ResolveConfiguredRequestBudget(
            int baseUpdateBudget,
            int probeCount,
            bool lightingDirtyBoostEnabled)
        {
            int layoutCapacity = Math.Clamp(
                probeCount,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            int baseCapacity = Math.Clamp(baseUpdateBudget, 0, layoutCapacity);
            if (!lightingDirtyBoostEnabled || baseCapacity <= 0)
                return baseCapacity;

            long boostedCapacity = (long)baseCapacity * 2L;
            return (int)Math.Min(layoutCapacity, boostedCapacity);
        }

        /// <summary>
        /// V2 evaluates a complete immutable directional sequence for cached
        /// solver work as well as source replacement. The legacy request count
        /// was sized for maintenance-ray subsets and can therefore create a
        /// 250k-ray single-frame burst. Bound the resident transaction by a
        /// fixed ray-work envelope; lower-ray tiers retain proportionally more
        /// probes while every tier has the same worst-case scheduler/solve work.
        /// </summary>
        internal static int ResolveTransportV2RequestBudget(
            int configuredRequestBudget,
            int maximumFullRaysPerProbe,
            bool transportV2Active)
        {
            int configured = Math.Max(0, configuredRequestBudget);
            if (!transportV2Active || configured == 0)
                return configured;

            const int maximumRayEvaluationsPerFrame = 16_384;
            int raysPerProbe = Math.Max(1, maximumFullRaysPerProbe);
            int v2ProbeCap = Math.Max(1, maximumRayEvaluationsPerFrame / raysPerProbe);
            return Math.Min(configured, v2ProbeCap);
        }

        /// <summary>
        /// Provisions enough request/output storage for a bounded cached solve
        /// burst without increasing the primary-ray source envelope. Cached V2
        /// work evaluates resident cache entries and is substantially cheaper
        /// than tracing the same number of rays, but it still receives a fixed
        /// work cap so convergence cannot create an unbounded frame.
        /// </summary>
        internal static int ResolveTransportV2SchedulerRequestCapacity(
            int configuredRequestBudget,
            int maximumFullRaysPerProbe,
            bool transportV2Active,
            bool acceleratedTailSolveEnabled)
        {
            int configured = Math.Max(0, configuredRequestBudget);
            if (!transportV2Active || configured == 0)
                return configured;

            int sourceCapacity = ResolveTransportV2RequestBudget(
                configured,
                maximumFullRaysPerProbe,
                transportV2Active: true);
            if (!acceleratedTailSolveEnabled)
                return sourceCapacity;

            const int maximumCachedRayEvaluationsPerFrame = 65_536;
            int raysPerProbe = Math.Max(1, maximumFullRaysPerProbe);
            int cachedSolveCapacity = Math.Max(
                1,
                maximumCachedRayEvaluationsPerFrame / raysPerProbe);
            return Math.Min(configured, Math.Max(sourceCapacity, cachedSolveCapacity));
        }

        internal static int ResolveTransportV2FrameRequestBudget(
            int requestedBudget,
            int sourceRequestBudget,
            int schedulerRequestCapacity,
            bool transportV2Active,
            bool acceleratedSolveActive)
        {
            int capacity = Math.Max(0, schedulerRequestCapacity);
            int requested = Math.Clamp(requestedBudget, 0, capacity);
            if (!transportV2Active || acceleratedSolveActive)
                return requested;

            return Math.Min(requested, Math.Max(0, sourceRequestBudget));
        }

        private int ResolveFeedbackLimitedUpdateBudget(
            int requestedBudget,
            int minimumRecoveryBudget)
        {
            int hardBudget = Math.Clamp(
                requestedBudget,
                0,
                Math.Min(_probeCount, GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount));
            return ResolveFeedbackLimitedUpdateBudget(
                hardBudget,
                _schedulerFeedbackRequestBudgetCap,
                _schedulerDeterministicFixedBudget,
                minimumRecoveryBudget);
        }

        internal static int ResolveFeedbackLimitedUpdateBudget(
            int hardBudget,
            int feedbackRequestBudgetCap,
            bool deterministicFixedBudget)
        {
            return ResolveFeedbackLimitedUpdateBudget(
                hardBudget,
                feedbackRequestBudgetCap,
                deterministicFixedBudget,
                minimumRecoveryBudget: 0);
        }

        internal static int ResolveFeedbackLimitedUpdateBudget(
            int hardBudget,
            int feedbackRequestBudgetCap,
            bool deterministicFixedBudget,
            int minimumRecoveryBudget)
        {
            int clampedHardBudget = Math.Max(0, hardBudget);
            if (deterministicFixedBudget || feedbackRequestBudgetCap <= 0)
                return clampedHardBudget;

            int feedbackLimitedBudget = Math.Min(clampedHardBudget, feedbackRequestBudgetCap);
            int clampedRecoveryBudget = Math.Clamp(minimumRecoveryBudget, 0, clampedHardBudget);
            return Math.Max(feedbackLimitedBudget, clampedRecoveryBudget);
        }

        private int RefreshProbeSchedulingImportance()
        {
            if (_probeCount <= 0)
                return 0;

            if (!_hasSchedulerVisibilityCamera)
                _schedulerVisibilityFullRefreshRequired = true;
            else if ((_schedulerCameraPosition - _schedulerVisibilityCameraPosition).LengthSquared() > 0.000001f)
            {
                MarkCameraVisibilityCandidates(
                    _schedulerVisibilityCameraPosition,
                    _schedulerCameraPosition);
            }

            if (_schedulerVisibilityFullRefreshRequired)
            {
                Array.Clear(_volumeVisibleFreshProbeCounts);
                Array.Clear(_probeVisibleFreshCounted);
                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                {
                    GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                    int firstProbe = FirstProbe(volume);
                    int probeCount = VolumeProbeCount(volume);
                    for (int local = 0; local < probeCount; local++)
                    {
                        RefreshProbeSchedulingImportance(
                            firstProbe + local,
                            volumeIndex);
                    }
                }

                Array.Clear(_probeVisibilityDirty);
                _probeVisibilityDirtyCount = 0;
                _schedulerVisibilityFullRefreshRequired = false;
            }
            else
            {
                int dirtyCount = _probeVisibilityDirtyCount;
                _probeVisibilityDirtyCount = 0;
                for (int dirtyOffset = 0; dirtyOffset < dirtyCount; dirtyOffset++)
                {
                    int probeIndex = _probeVisibilityDirtyIndices[dirtyOffset];
                    if ((uint)probeIndex >= (uint)_probeVisibilityDirty.Length)
                        continue;
                    _probeVisibilityDirty[probeIndex] = 0;
                    RefreshProbeSchedulingImportance(
                        probeIndex,
                        ResolveSchedulerVolumeIndex(probeIndex));
                }
            }

            _schedulerVisibilityCameraPosition = _schedulerCameraPosition;
            _hasSchedulerVisibilityCamera = true;
            int visibleFreshRecoveryBudget = 0;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                // The per-ring minimum is also the bounded recovery guarantee for
                // visible cells invalidated by camera-relative scrolling. Adaptive
                // feedback may still reduce maintenance work after these cells are
                // populated, but it cannot leave a moving receiver sampling empty
                // atlas slots one probe at a time.
                int volumeMinimum = Math.Max(
                    0,
                    ResolveVolumeQuality(volumeIndex).MinimumUpdateQuota);
                visibleFreshRecoveryBudget = SaturatingAdd(
                    visibleFreshRecoveryBudget,
                    Math.Min(
                        _volumeVisibleFreshProbeCounts[volumeIndex],
                        volumeMinimum));
            }

            return visibleFreshRecoveryBudget;
        }

        private static bool ProbeResidencyGeometryChanged(
            uint dirtyReasonFlags,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            bool recentered)
        {
            if (recentered ||
                (dirtyReasonFlags & VulkanRenderer.SimpleDdgiDirtyReasonDynamicGeometry) != 0u)
            {
                return true;
            }

            if (dirtyRegions == null)
                return false;

            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                if (dirtyRegions[i].Reason is
                    DdgiDirtyReason.GeometryAdded or
                    DdgiDirtyReason.GeometryRemoved or
                    DdgiDirtyReason.TransformChanged or
                    DdgiDirtyReason.MaterialChanged or
                    DdgiDirtyReason.StreamIn or
                    DdgiDirtyReason.StreamOut or
                    DdgiDirtyReason.Teleport)
                {
                    return true;
                }
            }

            return false;
        }

        private static uint AdvancePackedGeometryGeneration(uint generation)
        {
            uint next = (generation + 1u) & 0xffffu;
            return next == 0u ? 1u : next;
        }

        private void RefreshProbeSchedulingImportance(
            int probeIndex,
            int volumeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeVisibilityImportance.Length ||
                (uint)probeIndex >= (uint)_probeSchedulingFlags.Length ||
                (uint)volumeIndex >= (uint)_volumeCount)
            {
                return;
            }

            _uploadVisibilityEntryRefreshCount++;

            bool wasVisible =
                (_probeSchedulingFlags[probeIndex] & ProbeSchedulingVisibleFlag) != 0;
            bool wasVisibleFresh =
                (uint)probeIndex < (uint)_probeVisibleFreshCounted.Length &&
                _probeVisibleFreshCounted[probeIndex] != 0;
            int requiredStableUpdates = Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiStableMaintenanceUpdateCount);
            bool needsVisibility = _lightingDirtyFrames > 0 ||
                _probeFresh[probeIndex] != 0 ||
                (_probeSchedulingFlags[probeIndex] &
                    (ProbeSchedulingScrollExposedFlag |
                        ProbeSchedulingRegionalDirtyFlag)) != 0 ||
                ((uint)probeIndex < (uint)_probeInactive.Length &&
                    _probeInactive[probeIndex] != 0) ||
                (_probeConvergenceReadbackValid != 0 &&
                    (uint)probeIndex < (uint)_probeStableUpdateCounts.Length &&
                    _probeStableUpdateCounts[probeIndex] < requiredStableUpdates);
            byte importance = needsVisibility
                ? CalculateProbeSchedulingImportance(probeIndex, volumeIndex)
                : (byte)0;
            _probeVisibilityImportance[probeIndex] = importance;
            if (importance >= SchedulerVisibleImportanceThreshold)
                _probeSchedulingFlags[probeIndex] |= ProbeSchedulingVisibleFlag;
            else
                _probeSchedulingFlags[probeIndex] &=
                    unchecked((byte)~ProbeSchedulingVisibleFlag);

            bool isVisible =
                (_probeSchedulingFlags[probeIndex] & ProbeSchedulingVisibleFlag) != 0;
            bool isVisibleFresh = isVisible &&
                (_probeFresh[probeIndex] != 0 ||
                    (_probeSchedulingFlags[probeIndex] &
                        ProbeSchedulingScrollExposedFlag) != 0);
            if (wasVisibleFresh != isVisibleFresh)
            {
                _probeVisibleFreshCounted[probeIndex] =
                    isVisibleFresh ? (byte)1 : (byte)0;
                _volumeVisibleFreshProbeCounts[volumeIndex] = Math.Max(
                    0,
                    _volumeVisibleFreshProbeCounts[volumeIndex] +
                        (isVisibleFresh ? 1 : -1));
            }

            if (wasVisible != isVisible)
                MarkProbeSchedulerDirty(probeIndex);
        }

        private void MarkCameraVisibilityCandidates(
            Vector3 previousCameraPosition,
            Vector3 currentCameraPosition)
        {
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume currentVolume = _volumeScratch[volumeIndex];
                MarkVolumeVisibilityCandidates(
                    currentVolume,
                    volumeIndex,
                    previousCameraPosition);
                MarkVolumeVisibilityCandidates(
                    currentVolume,
                    volumeIndex,
                    currentCameraPosition);
                if (_recenteredThisFrame &&
                    TryGetPreviousMatchingVolume(
                        volumeIndex,
                        currentVolume,
                        out GPUSimpleDdgiVolume previousVolume))
                {
                    MarkVolumeVisibilityCandidates(
                        previousVolume,
                        volumeIndex,
                        previousCameraPosition);
                }
            }
        }

        private void MarkVolumeVisibilityCandidates(
            GPUSimpleDdgiVolume volume,
            int volumeIndex,
            Vector3 cameraPosition)
        {
            int baseImportance = ResolveVolumeSchedulingBaseImportance(volumeIndex);
            // Camera proximity contributes three points. Always-visible volumes
            // and volumes that cannot reach the threshold need no spatial work.
            if (baseImportance >= SchedulerVisibleImportanceThreshold ||
                baseImportance + 4 < SchedulerVisibleImportanceThreshold)
            {
                return;
            }

            float spacing = Spacing(volume);
            Vector3 origin = Origin(volume);
            float radius = ResolveProbeSchedulingProximityRadius(
                volume,
                spacing);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int minimumX = Math.Clamp(
                (int)MathF.Floor((cameraPosition.X - radius - origin.X) / spacing),
                0,
                Math.Max(0, countX - 1));
            int maximumX = Math.Clamp(
                (int)MathF.Ceiling((cameraPosition.X + radius - origin.X) / spacing),
                0,
                Math.Max(0, countX - 1));
            int minimumY = Math.Clamp(
                (int)MathF.Floor((cameraPosition.Y - radius - origin.Y) / spacing),
                0,
                Math.Max(0, countY - 1));
            int maximumY = Math.Clamp(
                (int)MathF.Ceiling((cameraPosition.Y + radius - origin.Y) / spacing),
                0,
                Math.Max(0, countY - 1));
            int minimumZ = Math.Clamp(
                (int)MathF.Floor((cameraPosition.Z - radius - origin.Z) / spacing),
                0,
                Math.Max(0, countZ - 1));
            int maximumZ = Math.Clamp(
                (int)MathF.Ceiling((cameraPosition.Z + radius - origin.Z) / spacing),
                0,
                Math.Max(0, countZ - 1));
            int firstProbe = FirstProbe(volume);
            for (int z = minimumZ; z <= maximumZ; z++)
                for (int y = minimumY; y <= maximumY; y++)
                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        int physicalLocal = CalculatePhysicalProbeLocalIndex(
                            volume,
                            x,
                            y,
                            z);
                        MarkProbeVisibilityDirty(firstProbe + physicalLocal);
                    }
        }

        private int ResolveVolumeSchedulingBaseImportance(int volumeIndex)
        {
            if ((uint)volumeIndex >= (uint)_volumeCount)
                return 0;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            return Kind(volume) == VolumeKindAuthored
                ? _volumePurposes[volumeIndex] switch
                {
                    SimpleDdgiVolumePurpose.ReceiverHero => 6,
                    SimpleDdgiVolumePurpose.DynamicInfluence => 5,
                    SimpleDdgiVolumePurpose.NavigableInterior => 2,
                    _ => 1
                }
                : ResolveVolumeQuality(volumeIndex).RingIndex switch
                {
                    0 => 4,
                    1 => 1,
                    _ => 0
                };
        }

        private static float ResolveProbeSchedulingProximityRadius(
            GPUSimpleDdgiVolume volume,
            float spacing)
        {
            return Math.Max(
                spacing * 3.0f,
                Kind(volume) == VolumeKindRing
                    ? spacing * 4.0f
                    : Math.Max(
                        volume.WorldMinAndEdgeFade.W,
                        spacing * 2.0f));
        }

        private byte CalculateProbeSchedulingImportance(int probeIndex, int volumeIndex)
        {
            if ((uint)volumeIndex >= (uint)_volumeCount)
                return 0;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = FirstProbe(volume);
            int local = probeIndex - firstProbe;
            if (local < 0 || local >= VolumeProbeCount(volume))
                return 0;

            int importance = ResolveVolumeSchedulingBaseImportance(volumeIndex);

            (int x, int y, int z) = CalculateLogicalProbeCoordinate(volume, local);
            float spacing = Spacing(volume);
            Vector3 origin = Origin(volume);
            float dx = origin.X + x * spacing - _schedulerCameraPosition.X;
            float dy = origin.Y + y * spacing - _schedulerCameraPosition.Y;
            float dz = origin.Z + z * spacing - _schedulerCameraPosition.Z;
            float proximityRadius = ResolveProbeSchedulingProximityRadius(
                volume,
                spacing);
            if (dx * dx + dy * dy + dz * dz <= proximityRadius * proximityRadius)
                importance += 3;

            if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                (_probeSchedulingFlags[probeIndex] & ProbeSchedulingScrollExposedFlag) != 0)
            {
                importance++;
            }

            return checked((byte)Math.Clamp(importance, 0, byte.MaxValue));
        }

        private void MarkFreshForNewOrScrolledProbes()
        {
            if (_probeCount <= 0)
                return;

            if (!_settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled && _recenteredThisFrame)
            {
                // Non-toroidal recentering is always a clear and rebootstrap.
                // Atlas-only copying would desynchronize relocation,
                // classification, generation, EMA, and age from logical probes.
                _atlasClearRequired = true;
                _atlasFresh = true;
                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                    MarkVolumeFresh(_volumeScratch[volumeIndex]);
                return;
            }

            if (_atlasFresh || _previousVolumeCount == 0)
            {
                for (int probeIndex = 0; probeIndex < _probeCount; probeIndex++)
                    MarkProbeFresh(probeIndex, scrollExposed: false);
                _newlyInvalidatedProbeCount = _probeCount;
                return;
            }

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                if (!TryGetPreviousMatchingVolume(volumeIndex, current, out GPUSimpleDdgiVolume previous))
                {
                    MarkVolumeFresh(current);
                    continue;
                }

                if (!TryResolveCellDelta(previous, current, out int deltaX, out int deltaY, out int deltaZ))
                {
                    // A non-cell-aligned remap has no coherent overlap.  Preserve
                    // neither history nor readback-derived classification for it.
                    MarkVolumeFresh(current);
                    continue;
                }

                if (deltaX == 0 && deltaY == 0 && deltaZ == 0)
                {
                    continue;
                }

                int countX = CountX(current);
                int countY = CountY(current);
                int countZ = CountZ(current);
                for (int z = 0; z < countZ; z++)
                    for (int y = 0; y < countY; y++)
                        for (int x = 0; x < countX; x++)
                        {
                            int oldX = x - deltaX;
                            int oldY = y - deltaY;
                            int oldZ = z - deltaZ;
                            if (oldX >= 0 && oldX < countX && oldY >= 0 && oldY < countY && oldZ >= 0 && oldZ < countZ)
                                continue;

                            int physicalLocal = CalculatePhysicalProbeLocalIndex(current, x, y, z);
                            MarkProbeFresh(FirstProbe(current) + physicalLocal, scrollExposed: true, forceGenerationAdvance: true);
                        }
            }
        }

        /// <summary>
        /// Invalidates only physical slots whose world-cell ownership changed.
        /// This bounded walk runs on topology changes (principally toroidal
        /// scrolls), never on a stable frame. It is deliberately independent of
        /// scheduler mode so opaque receivers fail closed before a resident GPU
        /// scheduler can process the newly exposed cells later in the frame.
        /// </summary>
        private void QueueReceiverInvalidationsForVolumeRemap()
        {
            if (_probeCount <= 0 || _atlasClearRequired || _previousVolumeCount == 0)
            {
                _receiverProbeClearRequired = true;
                _receiverProbeInvalidationSlots.Clear();
                return;
            }

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                if (!TryGetPreviousMatchingVolume(
                        volumeIndex,
                        current,
                        out GPUSimpleDdgiVolume previous) ||
                    !TryResolveCellDelta(
                        previous,
                        current,
                        out int deltaX,
                        out int deltaY,
                        out int deltaZ))
                {
                    QueueReceiverVolumeInvalidation(current);
                    continue;
                }

                int countX = CountX(current);
                int countY = CountY(current);
                int countZ = CountZ(current);
                if (Math.Abs((long)deltaX) >= countX ||
                    Math.Abs((long)deltaY) >= countY ||
                    Math.Abs((long)deltaZ) >= countZ)
                {
                    QueueReceiverVolumeInvalidation(current);
                    continue;
                }

                if (deltaX == 0 && deltaY == 0 && deltaZ == 0)
                    continue;

                int firstProbe = FirstProbe(current);
                for (int z = 0; z < countZ; z++)
                for (int y = 0; y < countY; y++)
                for (int x = 0; x < countX; x++)
                {
                    int oldX = x - deltaX;
                    int oldY = y - deltaY;
                    int oldZ = z - deltaZ;
                    if (oldX >= 0 && oldX < countX &&
                        oldY >= 0 && oldY < countY &&
                        oldZ >= 0 && oldZ < countZ)
                    {
                        continue;
                    }

                    int physicalLocal = CalculatePhysicalProbeLocalIndex(
                        current,
                        x,
                        y,
                        z);
                    QueueReceiverProbeInvalidation(firstProbe + physicalLocal);
                }
            }
        }

        private void QueueReceiverVolumeInvalidation(GPUSimpleDdgiVolume volume)
        {
            int firstProbe = FirstProbe(volume);
            int end = Math.Min(_probeCount, checked(firstProbe + VolumeProbeCount(volume)));
            for (int probeIndex = Math.Max(0, firstProbe); probeIndex < end; probeIndex++)
                QueueReceiverProbeInvalidation(probeIndex);
        }

        private void QueueReceiverProbeInvalidation(int probeIndex)
        {
            if ((uint)probeIndex < (uint)_probeCount && !_receiverProbeClearRequired)
                _receiverProbeInvalidationSlots.Add(probeIndex);
        }

        private int BuildUpdateQueue(int updateBudget)
        {
            int capacity = Math.Clamp(updateBudget, 0, Math.Min(_probeCount, GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount));
            if (capacity == 0)
            {
                _schedulerPressureReason = _probeCount > 0
                    ? SimpleDdgiSchedulerPressureReason.RequestCap
                    : SimpleDdgiSchedulerPressureReason.NoEligibleWork;
                return 0;
            }

            int count = 0;
            int[] quotas = ResolveVolumeQuotas(capacity);
            int[] used = _volumeQuotaUsageScratch;
            Array.Clear(used, 0, _volumeCount);

            BuildWorkClassReservations(quotas);

            // The first pass honors bounded per-volume class reservations. This
            // prevents continuous scroll exposure from consuming the full near
            // allocation before visible dynamic dirty/retry work or maintenance
            // receives its declared minimum share.
            for (int workClassIndex = 0;
                 workClassIndex <= (int)SimpleDdgiSchedulerWorkClass.VisibleRetry && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    quotas,
                    used,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass: true);
            }
            QueueSourceRefreshThroughputCohort(
                ref count,
                capacity,
                quotas,
                used,
                Math.Min(_sourceRefreshTargetProbeCount, capacity));
            QueueSourceRefreshMaintenanceAcrossVolumes(
                ref count,
                capacity,
                quotas,
                used,
                reservedPass: true);
            QueueCachedSolverMaintenanceAcrossVolumes(
                ref count,
                capacity,
                quotas,
                used,
                reservedPass: true);
            for (int workClassIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                 workClassIndex < SchedulerWorkClassCount && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    quotas,
                    used,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass: true);
            }

            // Any reservation left idle by absent work is returned in the same
            // deterministic priority order. This keeps authored request caps hard
            // while never wasting available tracing work merely because a lower
            // class had no eligible probe this frame.
            for (int workClassIndex = 0;
                 workClassIndex <= (int)SimpleDdgiSchedulerWorkClass.VisibleRetry && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    quotas,
                    used,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass: false);
            }
            QueueSourceRefreshMaintenanceAcrossVolumes(
                ref count,
                capacity,
                quotas,
                used,
                reservedPass: false);
            QueueCachedSolverMaintenanceAcrossVolumes(
                ref count,
                capacity,
                quotas,
                used,
                reservedPass: false);
            for (int workClassIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                 workClassIndex < SchedulerWorkClassCount && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    quotas,
                    used,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass: false);
            }

            int eligiblePending = 0;
            for (int i = 0; i < SchedulerWorkClassCount; i++)
                eligiblePending = SaturatingAdd(eligiblePending, _pendingWorkClassCounts[i]);
            _schedulerDeferredRequestCount = Math.Max(0, eligiblePending - count);
            _schedulerPressureReason = ResolveSchedulerPressureReason(capacity, count, eligiblePending);

            return count;
        }

        private void BuildRayDispatchBatches()
        {
            _rayDispatchBatchCount = 0;
            _transportDispatchLaneCount = 0UL;
            _transportUsefulDispatchLaneCount = 0UL;
            _transportNoOpDispatchLaneCount = 0UL;
            Array.Clear(_volumeScheduledTransportProbeCounts);
            Array.Clear(_volumeScheduledTransportRayCounts);
            if (_probesToUpdate <= 0)
                return;

            int maximumRayCount = Math.Clamp(
                _raysPerProbe,
                1,
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
            Array.Clear(_rayDispatchRayCountHistogram);
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int rayCount = ResolveDispatchRayCount(
                    _updateQueueScratch[queueOffset],
                    maximumRayCount);
                _rayDispatchRayCountHistogram[rayCount]++;
                int volumeIndex = checked((int)_updateQueueScratch[queueOffset].VolumeIndex);
                if ((uint)volumeIndex < (uint)_volumeCount)
                {
                    _volumeScheduledTransportProbeCounts[volumeIndex]++;
                    _volumeScheduledTransportRayCounts[volumeIndex] = SaturatingAdd(
                        _volumeScheduledTransportRayCounts[volumeIndex],
                        (ulong)rayCount);
                }
            }

            int nextOffset = 0;
            for (int rayCount = 1; rayCount <= maximumRayCount; rayCount++)
            {
                _rayDispatchRayCountOffsets[rayCount] = nextOffset;
                _rayDispatchRayCountWriteOffsets[rayCount] = nextOffset;
                nextOffset += _rayDispatchRayCountHistogram[rayCount];
            }

            // Stable counting sort preserves scheduler priority within a ray tier
            // while making every tier a contiguous queue range for one dispatch.
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                GPUSimpleDdgiProbeUpdate update = _updateQueueScratch[queueOffset];
                int rayCount = ResolveDispatchRayCount(update, maximumRayCount);
                int destination = _rayDispatchRayCountWriteOffsets[rayCount]++;
                _rayDispatchSortScratch[destination] = update;
                _rayDispatchWorkClassSortScratch[destination] =
                    _queuedWorkClassScratch[queueOffset];
            }

            Array.Copy(
                _rayDispatchSortScratch,
                _updateQueueScratch,
                _probesToUpdate);
            Array.Copy(
                _rayDispatchWorkClassSortScratch,
                _queuedWorkClassScratch,
                _probesToUpdate);

            for (int rayCount = 1; rayCount <= maximumRayCount; rayCount++)
            {
                int probeCount = _rayDispatchRayCountHistogram[rayCount];
                if (probeCount <= 0)
                    continue;

                _rayDispatchBatches[_rayDispatchBatchCount++] =
                    new SimpleDdgiRayDispatchBatch(
                        _rayDispatchRayCountOffsets[rayCount],
                        probeCount,
                        rayCount);
                ulong usefulLanes = checked((ulong)probeCount * (ulong)rayCount);
                ulong dispatchedLanes = checked(
                    ((usefulLanes + 63UL) / 64UL) * 64UL);
                _transportUsefulDispatchLaneCount = SaturatingAdd(
                    _transportUsefulDispatchLaneCount,
                    usefulLanes);
                _transportDispatchLaneCount = SaturatingAdd(
                    _transportDispatchLaneCount,
                    dispatchedLanes);
                _transportNoOpDispatchLaneCount = SaturatingAdd(
                    _transportNoOpDispatchLaneCount,
                    dispatchedLanes - usefulLanes);
            }
        }

        internal static int ResolveDispatchRayCount(
            GPUSimpleDdgiProbeUpdate update,
            int fallbackRayCount)
        {
            int packedRayCount = checked((int)((update.Flags >> 16) & 0xffffu));
            int maximumRayCount = Math.Clamp(
                fallbackRayCount,
                1,
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
            return Math.Clamp(
                packedRayCount > 0 ? packedRayCount : fallbackRayCount,
                1,
                maximumRayCount);
        }

        private void ResolveSourceRefreshThroughputTarget(int updateBudget)
        {
            int participatingProbeCount = TransportV2Active
                ? _schedulerParticipatingProbeCount
                : 0;
            // Keep the authored sweep rate independent of the effective
            // start-to-start cadence. The latter deliberately includes time
            // for solve and audit; feeding it back into this target would slow
            // the source sweep again and make the cadence equation diverge.
            int targetFrames = Math.Max(1, AuthoredTransportSourceSweepFrames);
            _sourceRefreshTargetProbeCount = participatingProbeCount <= 0
                ? 0
                : (int)Math.Min(
                    int.MaxValue,
                    ((long)participatingProbeCount + targetFrames - 1L) /
                        targetFrames);
            _sourceRefreshCapacityShortfall = Math.Max(
                0,
                _sourceRefreshTargetProbeCount - Math.Max(updateBudget, 0));

            ulong admittedRayBudget = (ulong)Math.Max(
                _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget,
                0);
            Span<SimpleDdgiRayTier> tiers = stackalloc SimpleDdgiRayTier[
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
            int tierCount = 0;
            if (TransportV2Active)
            {
                for (int volumeIndex = 0;
                     volumeIndex < _volumeCount && tierCount < tiers.Length;
                     volumeIndex++)
                {
                    int volumeParticipants = _volumeAtmosphereParticipatingProbeCounts[volumeIndex];
                    if (volumeParticipants <= 0)
                        continue;
                    tiers[tierCount++] = new SimpleDdgiRayTier(
                        volumeParticipants,
                        Math.Max(1, ResolveVolumeQuality(volumeIndex).FullRays));
                }
            }

            // During bootstrap/rebuild the per-volume counters may not yet have been populated.
            // The fallback remains exact for a uniform cache and is removed as soon as the
            // incremental volume counters become authoritative.
            if (tierCount == 0 && participatingProbeCount > 0)
            {
                tiers[0] = new SimpleDdgiRayTier(
                    participatingProbeCount,
                    Math.Max(1, _transportSourceCacheRayCapacity));
                tierCount = 1;
            }

            SimpleDdgiRayCapacityResult result = SimpleDdgiRayCapacityPlanner.Evaluate(
                tiers[..tierCount],
                targetFrames,
                admittedRayBudget,
                ResolveSourceSweepFramesPerSecond(
                    _schedulerDeterministicFixedBudget,
                    _sourceSweepFramesPerSecond));
            _sourceRefreshTargetRayCount = result.TargetRaysPerFrame;
            _sourceRefreshRayCapacityShortfall = result.CapacityShortfall;
            _sourceRefreshMinimumSweepSeconds = result.MinimumAchievableSweepSeconds;
        }

        private void RefreshSourceStepAgeTelemetry()
        {
            int participantCount = _schedulerParticipatingProbeCount;
            int staleCount = _schedulerGenerationStaleAgeHistogram.Count;
            int maximumAge = ClampUIntToInt(
                ResolveMaximumTrackedProbeAge(
                    _schedulerGenerationStaleAgeHeap));
            _sourceStepStaleProbeCount = staleCount;
            _sourceStepAgeMaximumFrames = maximumAge;
            _sourceStepAgeP95Frames = 0;
            if (participantCount > 0)
            {
                int targetRank = (int)Math.Ceiling(participantCount * 0.95);
                int currentGenerationCount = Math.Max(
                    0,
                    participantCount - staleCount);
                if (targetRank > currentGenerationCount)
                {
                    int staleRank = targetRank - currentGenerationCount;
                    int age = _schedulerGenerationStaleAgeHistogram.SelectRank(
                        staleRank,
                        _frameSerial);
                    _sourceStepAgeP95Frames = age <=
                        GiSourceAgeHistogramMaximumFrames
                            ? age
                            : maximumAge;
                }
            }

            if (staleCount == 0)
            {
                _admittedSourceCohortGeneration = _sourceLightingGeneration;
                if (_sourceCohortTransitionActive)
                {
                    _sourceCohortTransitionActive = false;
                    _sourceCohortCompletionCount = SaturatingAdd(
                        _sourceCohortCompletionCount,
                        1UL);
                    _sourceCohortCompletedFrame = _frameIndex;
                    _sourceCohortQuietFrames = 0;
                }
                else
                {
                    _sourceCohortQuietFrames = Math.Min(
                        SourceCohortQuietFrameCount,
                        _sourceCohortQuietFrames + 1);
                }
            }
            else
            {
                _sourceCohortQuietFrames = 0;
            }
        }

        private SimpleDdgiTrackingState ResolveTrackingState()
        {
            if (_probeCount <= 0 || !_hasLightingSignature)
                return SimpleDdgiTrackingState.Bootstrapping;
            if (_sourceRefreshCapacityShortfall > 0 || SourceRefreshRayCapacityShortfall > 0UL)
                return SimpleDdgiTrackingState.CapacityLimited;
            if (_sourceCohortTransitionActive || _sourceStepStaleProbeCount > 0)
                return SimpleDdgiTrackingState.TrackingSourceCohort;
            if (TransportGlobalConvergencePending)
                return SimpleDdgiTrackingState.TrackingPropagation;
            if (_sourceCohortQuietFrames < SourceCohortQuietFrameCount)
                return SimpleDdgiTrackingState.TrackingBounded;
            return SimpleDdgiTrackingState.StaticConverged;
        }

        private void QueueSourceRefreshThroughputCohort(
            ref int count,
            int capacity,
            int[] volumeQuotas,
            int[] volumeUsage,
            int targetSourceRefreshCount)
        {
            if (!TransportV2Active ||
                targetSourceRefreshCount <= _sourceRefreshProbeCount ||
                !HasPendingPriorityWork(
                    _volumeSourceRefreshPendingScratch,
                    _volumeSourceRefreshUsageScratch))
            {
                return;
            }

            // Round-robin one source refresh per volume per sweep. This is a
            // throughput floor, not a new priority class: visible recovery ran
            // first, and the ordinary scheduler may consume any remaining work
            // after the target is met.
            bool madeProgress;
            do
            {
                madeProgress = false;
                for (int volumeIndex = 0;
                     volumeIndex < _volumeCount &&
                     count < capacity &&
                     _sourceRefreshProbeCount < targetSourceRefreshCount;
                     volumeIndex++)
                {
                    int quota = volumeIndex < volumeQuotas.Length
                        ? volumeQuotas[volumeIndex]
                        : 0;
                    if (quota <= 0 || volumeUsage[volumeIndex] >= quota)
                        continue;

                    for (int classIndex = 0;
                         classIndex < SchedulerWorkClassCount;
                         classIndex++)
                    {
                        int offset = WorkClassOffset(volumeIndex) + classIndex;
                        if (_volumeSourceRefreshPendingScratch[offset] <=
                            _volumeSourceRefreshUsageScratch[offset])
                        {
                            continue;
                        }

                        int before = count;
                        int oneItemLimit = Math.Min(
                            quota,
                            _volumeWorkClassUsageScratch[offset] + 1);
                        QueueWorkClassVolume(
                            ref count,
                            capacity,
                            volumeIndex,
                            quota,
                            ref volumeUsage[volumeIndex],
                            (SimpleDdgiSchedulerWorkClass)classIndex,
                            oneItemLimit,
                            ref _volumeWorkClassUsageScratch[offset],
                            sourceRefreshOnly: true,
                            cachedSolverOnly: false);
                        if (count <= before)
                            continue;
                        madeProgress = true;
                        break;
                    }
                }
            }
            while (madeProgress &&
                count < capacity &&
                _sourceRefreshProbeCount < targetSourceRefreshCount);
        }

        private void RefreshProbeLifecycleTelemetry(int updateBudget)
        {
            _probeLifecycleLatencyTargetFrames =
                ResolveProbeLifecycleLatencyTarget(_probeCount, updateBudget);
            _oldestVisibleUnsupportedProbeAge =
                ResolveMaximumTrackedProbeAge(
                    _schedulerVisibleUnsupportedAgeHeap);
            _maximumFreshProbeAge = ResolveMaximumTrackedProbeAge(
                _schedulerFreshAgeHeap);
            _maximumScrollExposedProbeAge = ResolveMaximumTrackedProbeAge(
                _schedulerScrollExposedAgeHeap);
            _maximumRelocationPendingProbeAge = ResolveMaximumTrackedProbeAge(
                _schedulerRelocationPendingAgeHeap);
            _maximumUnpublishedProbeAge = ResolveMaximumTrackedProbeAge(
                _schedulerUnpublishedAgeHeap);
            _visibleZeroSupportRepairUpdateCount =
                _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport];

            _visibleUnsupportedProbeCountAboveLatencyTarget =
                _schedulerVisibleUnsupportedAgeHistogram.CountAbove(
                    _probeLifecycleLatencyTargetFrames,
                    _frameSerial);
            _probeLifecycleBoundExceededCount =
                _schedulerVisiblePendingAgeHistogram.CountAbove(
                    _probeLifecycleLatencyTargetFrames,
                    _frameSerial);
        }

        private uint ResolveMaximumTrackedProbeAge(
            SimpleDdgiSchedulerWakeHeap ageHeap)
        {
            if (!ageHeap.TryPeek(out _, out ulong lastUpdatedSerial))
                return 0u;

            ulong age = _frameSerial >= lastUpdatedSerial
                ? _frameSerial - lastUpdatedSerial
                : 0UL;
            return age > uint.MaxValue ? uint.MaxValue : (uint)age;
        }

        internal static int ResolveProbeLifecycleLatencyTarget(
            int probeCount,
            int updateBudget)
        {
            int safeBudget = Math.Max(updateBudget, 1);
            int fullSweepFrames = (int)Math.Ceiling(
                Math.Max(probeCount, 0) / (double)safeBudget);
            // The finding bound cannot be shorter than a recovery transition it
            // intentionally permits. Two sweep intervals cover admission and
            // publication; the relocation timeout is the hard minimum.
            int minimumRecoveryFrames = checked((int)Math.Max(
                ProbeLifecycleMinimumRecoveryFrames,
                RelocationPendingMaximumRetryAge));
            return Math.Clamp(
                fullSweepFrames * 2,
                minimumRecoveryFrames,
                600);
        }

        private void BuildWorkClassReservations(int[] volumeQuotas)
        {
            Array.Clear(_volumeWorkClassQuotaScratch);
            Array.Clear(_volumeWorkClassUsageScratch);
            Array.Clear(_volumeSourceRefreshUsageScratch);
            Array.Clear(_volumeCachedSolverUsageScratch);
            Array.Clear(_reservedWorkClassCounts);

            for (int classIndex = 0; classIndex < SchedulerWorkClassCount; classIndex++)
            {
                _pendingWorkClassCounts[classIndex] =
                    _schedulerWorkQueues.GetWorkClassCount(classIndex);
            }

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                int workClassOffset = WorkClassOffset(volumeIndex);
                for (int classIndex = 0; classIndex < SchedulerWorkClassCount; classIndex++)
                {
                    int queueIndex = workClassOffset + classIndex;
                    _volumeWorkClassPendingScratch[queueIndex] =
                        _schedulerWorkQueues.GetQueueCount(queueIndex);
                    _volumeSourceRefreshPendingScratch[queueIndex] =
                        _schedulerSourceRefreshQueues.GetQueueCount(queueIndex);
                    _volumeCachedSolverPendingScratch[queueIndex] =
                        _schedulerCachedSolverQueues.GetQueueCount(queueIndex);
                }

                Span<int> pending = _volumeWorkClassPendingScratch.AsSpan(workClassOffset, SchedulerWorkClassCount);
                Span<int> reservations = _volumeWorkClassQuotaScratch.AsSpan(workClassOffset, SchedulerWorkClassCount);
                AllocateSchedulerClassQuotas(
                    volumeIndex < volumeQuotas.Length ? volumeQuotas[volumeIndex] : 0,
                    pending,
                    reservations);
                for (int classIndex = 0; classIndex < SchedulerWorkClassCount; classIndex++)
                {
                    _reservedWorkClassCounts[classIndex] = SaturatingAdd(
                        _reservedWorkClassCounts[classIndex],
                        reservations[classIndex]);
                }
            }
        }

        /// <summary>
        /// Resolves initial class ceilings for one volume. The lower-priority
        /// reservations are bounded fractions of that volume's existing ring or
        /// authored quota; a tiny budget keeps strict visible-first ordering while
        /// a normal budget protects dynamic dirty/retry and maintenance progress.
        /// </summary>
        internal static void AllocateSchedulerClassQuotas(
            int volumeQuota,
            ReadOnlySpan<int> pending,
            Span<int> reservations)
        {
            reservations.Clear();
            int classCount = Math.Min(pending.Length, reservations.Length);
            if (classCount < SchedulerWorkClassCount || volumeQuota <= 0)
                return;

            int budget = Math.Max(0, volumeQuota);
            int zeroSupport = Math.Max(
                0,
                pending[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport]);
            int fresh = Math.Max(0, pending[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible]);
            int dirty = Math.Max(0, pending[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty]);
            int retry = Math.Max(0, pending[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry]);
            int maintenanceClass = FindPendingMaintenanceClass(pending);
            int maintenance = maintenanceClass >= 0 ? Math.Max(0, pending[maintenanceClass]) : 0;

            int maintenanceReservation = maintenance > 0 && budget >= 4
                ? Math.Min(maintenance, Math.Max(1, budget / 16))
                : 0;
            int retryReservation = retry > 0 && budget >= 3
                ? Math.Min(retry, Math.Max(1, budget / 8))
                : 0;
            int dirtyReservation = dirty > 0 && budget >= 2
                ? Math.Min(dirty, Math.Max(1, budget / 4))
                : 0;

            // Keep at least one slot for the highest non-empty class whenever the
            // quota can represent it. Reservations are deliberately consumed from
            // the bottom up, so a capacity of one remains strictly visible-first.
            int lowerReservations = SaturatingAdd(maintenanceReservation, SaturatingAdd(retryReservation, dirtyReservation));
            if (lowerReservations >= budget &&
                (zeroSupport > 0 || fresh > 0 || dirty > 0 || retry > 0))
            {
                if (maintenanceReservation > 0)
                    maintenanceReservation--;
                else if (retryReservation > 0)
                    retryReservation--;
                else if (dirtyReservation > 0)
                    dirtyReservation--;
                lowerReservations--;
            }

            if (zeroSupport > 0)
            {
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport] =
                    Math.Max(0, budget - lowerReservations);
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty] = dirtyReservation;
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry] = retryReservation;
                if (maintenanceClass >= 0)
                    reservations[maintenanceClass] = maintenanceReservation;
                return;
            }

            if (fresh > 0)
            {
                reservations[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible] = Math.Max(0, budget - lowerReservations);
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty] = dirtyReservation;
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry] = retryReservation;
                if (maintenanceClass >= 0)
                    reservations[maintenanceClass] = maintenanceReservation;
                return;
            }

            if (dirty > 0)
            {
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty] = Math.Max(0, budget - maintenanceReservation - retryReservation);
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry] = retryReservation;
                if (maintenanceClass >= 0)
                    reservations[maintenanceClass] = maintenanceReservation;
                return;
            }

            if (retry > 0)
            {
                reservations[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry] = Math.Max(0, budget - maintenanceReservation);
                if (maintenanceClass >= 0)
                    reservations[maintenanceClass] = maintenanceReservation;
                return;
            }

            if (maintenanceClass >= 0)
                reservations[maintenanceClass] = Math.Min(maintenance, budget);
        }

        private static int FindPendingMaintenanceClass(ReadOnlySpan<int> pending)
        {
            for (int classIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                 classIndex <= (int)SimpleDdgiSchedulerWorkClass.FarMaintenance && classIndex < pending.Length;
                 classIndex++)
            {
                if (pending[classIndex] > 0)
                    return classIndex;
            }

            return -1;
        }

        private void QueueWorkClassAcrossVolumes(
            ref int count,
            int capacity,
            int[] volumeQuotas,
            int[] volumeUsage,
            SimpleDdgiSchedulerWorkClass workClass,
            bool reservedPass,
            bool sourceRefreshOnly = false,
            bool cachedSolverOnly = false)
        {
            int classIndex = (int)workClass;
            for (int orderIndex = 0;
                 orderIndex < _volumeCount && count < capacity;
                 orderIndex++)
            {
                int volumeIndex = _transportVolumeOrder[orderIndex];
                int quota = volumeIndex < volumeQuotas.Length ? volumeQuotas[volumeIndex] : 0;
                if (quota <= 0 || volumeUsage[volumeIndex] >= quota)
                    continue;

                int offset = WorkClassOffset(volumeIndex) + classIndex;
                if (sourceRefreshOnly &&
                    _volumeSourceRefreshPendingScratch[offset] <=
                        _volumeSourceRefreshUsageScratch[offset])
                {
                    continue;
                }
                if (cachedSolverOnly &&
                    _volumeCachedSolverPendingScratch[offset] <=
                        _volumeCachedSolverUsageScratch[offset])
                {
                    continue;
                }
                int classLimit = reservedPass
                    ? _volumeWorkClassQuotaScratch[offset]
                    : quota;
                if (classLimit <= 0 || _volumeWorkClassUsageScratch[offset] >= classLimit)
                    continue;

                QueueWorkClassVolume(
                    ref count,
                    capacity,
                    volumeIndex,
                    quota,
                    ref volumeUsage[volumeIndex],
                    workClass,
                    classLimit,
                    ref _volumeWorkClassUsageScratch[offset],
                    sourceRefreshOnly,
                    cachedSolverOnly);
            }
        }

        private void QueueSourceRefreshMaintenanceAcrossVolumes(
            ref int count,
            int capacity,
            int[] volumeQuotas,
            int[] volumeUsage,
            bool reservedPass)
        {
            if (!TransportV2Active ||
                !HasPendingPriorityWork(
                    _volumeSourceRefreshPendingScratch,
                    _volumeSourceRefreshUsageScratch))
            {
                return;
            }

            // Admit one due source refresh from every volume before allowing a
            // finer/earlier volume to consume the remaining maintenance share.
            // QueueWorkClassAcrossVolumes intentionally fills a volume at a
            // time, which is desirable for ordinary priority work but starved
            // outer cascades when only a handful of periodic refreshes fit.
            for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
            {
                int quota = volumeIndex < volumeQuotas.Length ? volumeQuotas[volumeIndex] : 0;
                if (quota <= 0 || volumeUsage[volumeIndex] >= quota)
                    continue;

                for (int workClassIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                     workClassIndex < SchedulerWorkClassCount && count < capacity;
                     workClassIndex++)
                {
                    int offset = WorkClassOffset(volumeIndex) + workClassIndex;
                    if (_volumeSourceRefreshPendingScratch[offset] <=
                        _volumeSourceRefreshUsageScratch[offset])
                    {
                        continue;
                    }

                    int before = count;
                    int oneItemClassLimit = Math.Min(
                        quota,
                        _volumeWorkClassUsageScratch[offset] + 1);
                    QueueWorkClassVolume(
                        ref count,
                        capacity,
                        volumeIndex,
                        quota,
                        ref volumeUsage[volumeIndex],
                        (SimpleDdgiSchedulerWorkClass)workClassIndex,
                        oneItemClassLimit,
                        ref _volumeWorkClassUsageScratch[offset],
                        sourceRefreshOnly: true,
                        cachedSolverOnly: false);
                    if (count > before)
                        break;
                }
            }

            for (int workClassIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                 workClassIndex < SchedulerWorkClassCount && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    volumeQuotas,
                    volumeUsage,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass,
                    sourceRefreshOnly: true);
            }
        }

        private void QueueCachedSolverMaintenanceAcrossVolumes(
            ref int count,
            int capacity,
            int[] volumeQuotas,
            int[] volumeUsage,
            bool reservedPass)
        {
            if (!TransportV2Active ||
                (TransportGlobalConvergencePending &&
                 (!TailCertificationEnabled ||
                  _transportSolveController.Phase != SimpleDdgiTransportPhase.AcceleratedSolve)) ||
                !HasPendingPriorityWork(
                    _volumeCachedSolverPendingScratch,
                    _volumeCachedSolverUsageScratch))
            {
                return;
            }

            for (int workClassIndex = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
                 workClassIndex < SchedulerWorkClassCount && count < capacity;
                 workClassIndex++)
            {
                QueueWorkClassAcrossVolumes(
                    ref count,
                    capacity,
                    volumeQuotas,
                    volumeUsage,
                    (SimpleDdgiSchedulerWorkClass)workClassIndex,
                    reservedPass,
                    cachedSolverOnly: true);
            }
        }

        private static bool HasPendingPriorityWork(
            ReadOnlySpan<int> pending,
            ReadOnlySpan<int> usage)
        {
            int count = Math.Min(pending.Length, usage.Length);
            for (int i = 0; i < count; i++)
            {
                if (pending[i] > usage[i])
                    return true;
            }

            return false;
        }

        private void QueueWorkClassVolume(
            ref int count,
            int capacity,
            int volumeIndex,
            int volumeQuota,
            ref int volumeUsed,
            SimpleDdgiSchedulerWorkClass workClass,
            int classLimit,
            ref int classUsed,
            bool sourceRefreshOnly,
            bool cachedSolverOnly)
        {
            if (volumeQuota <= volumeUsed || classLimit <= classUsed || (uint)volumeIndex >= (uint)_volumeCount)
                return;

            SimpleDdgiPersistentProbeQueues queues = sourceRefreshOnly
                ? _schedulerSourceRefreshQueues
                : cachedSolverOnly
                    ? _schedulerCachedSolverQueues
                    : _schedulerWorkQueues;
            int queueIndex = WorkClassOffset(volumeIndex) + (int)workClass;
            int visitLimit = queues.GetQueueCount(queueIndex);
            int visited = 0;
            while (visited < visitLimit &&
                volumeUsed < volumeQuota &&
                classUsed < classLimit &&
                count < capacity &&
                queues.TryRotateNext(queueIndex, out int probeIndex))
            {
                visited++;
                if ((uint)probeIndex >= (uint)_probeCount || _probeQueued[probeIndex] != 0)
                    continue;

                // The throughput cohort is a hard ceiling for background source
                // maintenance, not merely a floor. Urgent fresh, dirty, and
                // relocation repairs remain uncapped because they are not routine
                // refreshes. Without this check the generic maintenance pass used
                // every spare slot and destabilized thousands of probes at once.
                if (ShouldDeferRoutineSourceRefresh(
                        IsRoutineTransportSourceRefresh(probeIndex),
                        _sourceRefreshProbeCount,
                        _sourceRefreshTargetProbeCount))
                {
                    continue;
                }

                uint flags = _probeInactive[probeIndex] != 0 ? ProbeStateInactiveFlag : 0u;
                if (!AddProbeUpdate(
                        ref count,
                        capacity,
                        probeIndex,
                        volumeIndex,
                        flags,
                        workClass))
                    continue;

                volumeUsed++;
                classUsed++;
            }
        }

        private bool TryResolveProbeWorkClass(
            int probeIndex,
            int volumeIndex,
            out SimpleDdgiSchedulerWorkClass workClass)
        {
            workClass = SimpleDdgiSchedulerWorkClass.FarMaintenance;
            if ((uint)probeIndex >= (uint)_probeCount || (uint)volumeIndex >= (uint)_volumeCount ||
                ShouldSkipInactiveProbe(probeIndex))
            {
                return false;
            }

            bool visible = (uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                (_probeSchedulingFlags[probeIndex] & ProbeSchedulingVisibleFlag) != 0;
            bool freshOrExposed = _probeFresh[probeIndex] != 0 ||
                ((uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                    (_probeSchedulingFlags[probeIndex] & ProbeSchedulingScrollExposedFlag) != 0);
            bool dirty = _lightingDirtyFrames > 0 ||
                ((uint)probeIndex < (uint)_probeSchedulingFlags.Length &&
                    (_probeSchedulingFlags[probeIndex] & ProbeSchedulingRegionalDirtyFlag) != 0);
            // A converged cached source field does not need perpetual solver
            // maintenance. It returns to the queue immediately for a source
            // refresh, a physical invalidation, visible retry, or a meaningful
            // residual increase from readback.
            if (TransportV2Active && !NeedsSourceRefresh(probeIndex) &&
                IsTransportConverged(probeIndex) && !dirty)
            {
                return false;
            }
            bool retry = IsEligibleForVisibleRetry(probeIndex);
            int ringIndex = ResolveVolumeQuality(volumeIndex).RingIndex;
            bool zeroSupport = visible && IsProbeDataUnavailable(probeIndex);
            workClass = ResolveSchedulerWorkClass(
                zeroSupport,
                freshOrExposed,
                visible,
                dirty,
                retry,
                ringIndex);
            return true;
        }

        private bool IsProbeDataUnavailable(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeCount)
                return true;

            bool confidentlyInactive = IsConfidentlyInactiveProbe(probeIndex);
            return _probeFresh[probeIndex] != 0 ||
                ((uint)probeIndex < (uint)_probeRelocationPending.Length &&
                    _probeRelocationPending[probeIndex] != 0) ||
                (!confidentlyInactive &&
                    (uint)probeIndex < (uint)_probeActiveWeights.Length &&
                    _probeActiveWeights[probeIndex] <= 0.001f) ||
                (!confidentlyInactive && TransportV2Active &&
                    ((uint)probeIndex >= (uint)_probeSourceRayCounts.Length ||
                        _probeSourceRayCounts[probeIndex] == 0));
        }

        private bool IsCachedTransportSolvePriorityCandidate(int probeIndex)
        {
            if (!TransportV2Active || TransportGlobalConvergencePending)
                return false;

            bool sourceRefreshRequired = NeedsSourceRefresh(probeIndex);
            return ShouldPrioritizeCachedTransportSolve(
                true,
                false,
                sourceRefreshRequired,
                HasLocalTransportConvergenceEvidence(probeIndex));
        }

        private bool NeedsSourceRefresh(int probeIndex)
        {
            if (!TransportV2Active)
                return true;
            if ((uint)probeIndex >= (uint)_probeSourceLightingGenerations.Length ||
                (uint)probeIndex >= (uint)_probeLastSourceRefreshFrames.Length ||
                (uint)probeIndex >= (uint)_probeSourceRayCounts.Length ||
                (uint)probeIndex >= (uint)_probeFresh.Length)
            {
                return true;
            }

            // Inactive classification is authoritative geometric evidence, not
            // an absent source cache. Ignore ordinary sun/source generations
            // until the bounded classification retry is actually due. The due
            // retry performs a complete trace so reactivation remains safe.
            if (IsConfidentlyInactiveProbe(probeIndex) &&
                _probeFresh[probeIndex] == 0)
            {
                return !ShouldSkipInactiveProbe(probeIndex);
            }

            if (_probeFresh[probeIndex] != 0 ||
                _probeSourceLightingGenerations[probeIndex] != _sourceLightingGeneration ||
                _probeSourceRayCounts[probeIndex] == 0)
            {
                return true;
            }

            return IsRoutinePeriodicTransportSourceRefresh(probeIndex);
        }

        private bool IsRoutinePeriodicTransportSourceRefresh(int probeIndex)
        {
            if (!TransportV2Active ||
                (uint)probeIndex >= (uint)_probeSourceLightingGenerations.Length ||
                (uint)probeIndex >= (uint)_probeLastSourceRefreshFrames.Length ||
                (uint)probeIndex >= (uint)_probeSourceRayCounts.Length ||
                (uint)probeIndex >= (uint)_probeFresh.Length ||
                _probeFresh[probeIndex] != 0 ||
                _probeSourceLightingGenerations[probeIndex] != _sourceLightingGeneration ||
                _probeSourceRayCounts[probeIndex] == 0)
            {
                return false;
            }

            int periodicRefreshFrames = EffectiveTransportSourceRefreshFrames;
            uint elapsed = unchecked(_frameIndex - _probeLastSourceRefreshFrames[probeIndex]);
            bool periodicRefreshWaveMember =
                _transportPeriodicSourceRefreshWavePending &&
                IsTransportSourceRefreshDueAtCutoff(
                    _probeLastSourceRefreshFrames[probeIndex],
                    _transportPeriodicSourceRefreshWaveCutoffFrame,
                    periodicRefreshFrames);
            int completedSolverGenerations =
                (uint)probeIndex < (uint)_probeTransportGenerationCounts.Length
                    ? _probeTransportGenerationCounts[probeIndex]
                    : 0;
            if (TailCertificationEnabled)
            {
                // Source-age watchdogs remain active independently of solver
                // completion. A periodic source refresh is a genuine operator
                // boundary and will invalidate the current certificate.
                return elapsed >= (uint)Math.Max(1, periodicRefreshFrames);
            }

            int minimumSolverGenerations = Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiTransportAcceleratedSweepCount);
            return ShouldRefreshTransportSource(
                false,
                TransportGlobalConvergencePending,
                elapsed,
                periodicRefreshFrames,
                periodicRefreshWaveMember,
                HasLocalTransportConvergenceEvidence(probeIndex),
                completedSolverGenerations,
                minimumSolverGenerations);
        }

        private bool IsRoutineTransportSourceRefresh(int probeIndex)
        {
            if (IsRoutinePeriodicTransportSourceRefresh(probeIndex))
                return true;

            // A confidently inactive slot is retraced on a bounded cadence to
            // detect geometry streaming/reactivation. Its current source cache is
            // valid and its retry is local maintenance, not a field boundary.
            return TransportV2Active &&
                (uint)probeIndex < (uint)_probeFresh.Length &&
                (uint)probeIndex < (uint)_probeSourceLightingGenerations.Length &&
                (uint)probeIndex < (uint)_probeSourceRayCounts.Length &&
                _probeFresh[probeIndex] == 0 &&
                IsConfidentlyInactiveProbe(probeIndex) &&
                _probeSourceLightingGenerations[probeIndex] == _sourceLightingGeneration &&
                _probeSourceRayCounts[probeIndex] > 0;
        }

        private bool IsTransportConverged(int probeIndex)
        {
            if (TailCertificationEnabled)
            {
                // A complete-field certificate is the only V2 convergence
                // authority. Local EMA/generation data is deliberately ignored.
                return _transportSolveController.IsCertified &&
                    !NeedsSourceRefresh(probeIndex) &&
                    (uint)probeIndex < (uint)_probeInactive.Length &&
                    _probeInactive[probeIndex] == 0;
            }

            return !TransportGlobalConvergencePending &&
                HasLocalTransportConvergenceEvidence(probeIndex);
        }

        private bool HasLocalTransportConvergenceEvidence(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeTransportGenerationCounts.Length ||
                (uint)probeIndex >= (uint)_probeStableUpdateCounts.Length ||
                (uint)probeIndex >= (uint)_probeLuminanceChangeEma.Length)
            {
                return false;
            }

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return MeetsTransportConvergenceCriteria(
                _probeTransportGenerationCounts[probeIndex],
                gi.SimpleDdgiTransportAcceleratedSweepCount,
                _probeStableUpdateCounts[probeIndex],
                gi.SimpleDdgiStableMaintenanceUpdateCount,
                _probeLuminanceChangeEma[probeIndex],
                gi.SimpleDdgiTransportTailRelativeTolerance);
        }

        internal static bool MeetsTransportConvergenceCriteria(
            int completedSolverGenerations,
            int minimumSolverGenerations,
            int stableUpdateCount,
            int requiredStableUpdateCount,
            float residualEnvelope,
            float residualThreshold)
        {
            return completedSolverGenerations >= Math.Max(1, minimumSolverGenerations) &&
                stableUpdateCount >= Math.Max(1, requiredStableUpdateCount) &&
                float.IsFinite(residualEnvelope) &&
                residualEnvelope >= 0.0f &&
                float.IsFinite(residualThreshold) &&
                residualThreshold >= 0.0f &&
                residualEnvelope <= residualThreshold;
        }

        internal static bool ShouldRefreshTransportSource(
            bool hardRefreshRequired,
            bool globalConvergencePending,
            uint elapsedFrames,
            int periodicRefreshFrames,
            bool periodicRefreshWaveMember,
            bool hasLocalConvergenceEvidence,
            int completedSolverGenerations,
            int minimumSolverGenerations)
        {
            if (hardRefreshRequired)
                return true;

            bool periodicRefreshDue = elapsedFrames >= (uint)Math.Max(1, periodicRefreshFrames);
            if (!periodicRefreshDue)
                return false;

            if (globalConvergencePending)
            {
                // A per-probe watchdog lets fast/visible probes cycle their
                // sources while a slow maintenance tail is still draining.
                // Global recovery is admitted only through one latched,
                // fixed-cutoff cohort created by the field state machine.
                return periodicRefreshWaveMember;
            }

            int watchdogGeneration =
                ResolveTransportSourceRefreshWatchdogGeneration(minimumSolverGenerations);
            bool watchdogExpired =
                Math.Max(0, completedSolverGenerations) >= watchdogGeneration;
            return hasLocalConvergenceEvidence || watchdogExpired;
        }

        internal static bool IsTransportSourceRefreshDueAtCutoff(
            uint lastSourceRefreshFrame,
            uint cutoffFrame,
            int periodicRefreshFrames)
        {
            uint ageAtCutoff = unchecked(cutoffFrame - lastSourceRefreshFrame);
            return ageAtCutoff < 0x80000000u &&
                ageAtCutoff >= (uint)Math.Max(1, periodicRefreshFrames);
        }

        internal static int ResolveEffectiveTransportSourceRefreshFrames(
            int configuredRefreshFrames,
            int participantCount,
            int updateBudget,
            int probeCount,
            int auditChunkProbeCount)
        {
            int participants = Math.Max(0, participantCount);
            int probes = Math.Max(0, probeCount);
            int updates = updateBudget <= 0
                ? Math.Max(participants, 1)
                : Math.Max(1, updateBudget);
            int auditChunk = Math.Max(1, auditChunkProbeCount);

            long sourceSweepFrames = ResolveTransportSourceSweepFrames(
                configuredRefreshFrames,
                participants,
                updates,
                probes);
            // Cached solve requests are not paced by the authored source
            // sweep; they can consume the bounded scheduler request capacity.
            long solveEpochFrames = CeilingDivide(participants, updates);
            long auditFrames = CeilingDivide(probes, auditChunk);
            long schedulingMargin = Math.Max(sourceSweepFrames, auditFrames);
            long requiredOpportunity = checked(
                sourceSweepFrames +
                solveEpochFrames +
                auditFrames +
                schedulingMargin);
            long effective = Math.Max(
                Math.Max(1, configuredRefreshFrames),
                requiredOpportunity);
            return (int)Math.Min(effective, int.MaxValue);
        }

        internal static int ResolveTransportSourceSweepFrames(
            int configuredRefreshFrames,
            int participantCount,
            int updateBudget,
            int probeCount)
        {
            int participants = Math.Max(0, participantCount);
            if (participants == 0)
                return 0;

            int probes = Math.Max(0, probeCount);
            int updates = updateBudget <= 0
                ? Math.Max(participants, 1)
                : Math.Max(1, updateBudget);
            // Source work is intentionally spread over the authored sweep
            // window to avoid a ray-query burst. Account for that real
            // throughput instead of assuming every source frame consumes the
            // full scheduler request capacity. At 15,368 probes and a
            // 480-frame sweep the target is 33 probes/frame, not the
            // 128-request arena cap.
            int authoredSourceUpdates = probes <= 0
                ? updates
                : Math.Max(
                    1,
                    (int)Math.Min(
                        int.MaxValue,
                        CeilingDivide(
                            probes,
                            Math.Max(1, configuredRefreshFrames))));
            int sourceUpdates = Math.Min(updates, authoredSourceUpdates);
            return checked((int)Math.Min(
                int.MaxValue,
                CeilingDivide(participants, sourceUpdates)));
        }

        private int ResolveTransportRefreshParticipantCount()
        {
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                return _gpuSchedulerFeedbackValid
                    ? ResolveGpuResidentTransportRefreshParticipantCount(
                        _transportResidentParticipantCount,
                        _transportResidentSourceRepairProbeCount,
                        _probeCount)
                    : Math.Max(0, _probeCount);
            }

            return _probeStateReadbackValid != 0
                ? Math.Max(0, _schedulerParticipatingProbeCount)
                : Math.Max(0, _probeCount);
        }

        internal static int ResolveGpuResidentTransportRefreshParticipantCount(
            int sourceReadyParticipantCount,
            int blockingSourceWorkCount,
            int probeCount)
        {
            int capacity = Math.Max(0, probeCount);
            long conservativeActiveCohort =
                (long)Math.Max(0, sourceReadyParticipantCount) +
                Math.Max(0, blockingSourceWorkCount);
            // Blocking categories may overlap (for example a fresh probe can
            // also have a stale source generation), so clamp the conservative
            // union to the physical field. Overestimating during repair only
            // lengthens the next-source guard; underestimating can preempt the
            // cohort before Dense reaches audit.
            return (int)Math.Min(capacity, conservativeActiveCohort);
        }

        private static long CeilingDivide(int value, int divisor)
        {
            long nonNegativeValue = Math.Max(0, value);
            long positiveDivisor = Math.Max(1, divisor);
            return nonNegativeValue == 0L
                ? 0L
                : 1L + (nonNegativeValue - 1L) / positiveDivisor;
        }

        internal static int ResolveGiTargetSourceSweepFrames(float targetSeconds)
        {
            return ResolveGiTargetSourceSweepFrames(
                targetSeconds,
                GiSourceSweepNominalFramesPerSecond);
        }

        internal static int ResolveGiTargetSourceSweepFrames(
            float targetSeconds,
            float framesPerSecond)
        {
            float safeSeconds = float.IsFinite(targetSeconds)
                ? Math.Clamp(targetSeconds, 0.25f, 120.0f)
                : 8.0f;
            float safeFramesPerSecond = float.IsFinite(framesPerSecond)
                ? Math.Clamp(
                    framesPerSecond,
                    GiSourceSweepMinimumFramesPerSecond,
                    GiSourceSweepMaximumFramesPerSecond)
                : GiSourceSweepNominalFramesPerSecond;
            return Math.Max(
                1,
                (int)MathF.Ceiling(
                    safeSeconds * safeFramesPerSecond));
        }

        internal static float ResolveSourceSweepFramesPerSecond(
            bool deterministicFixedBudget,
            float observedFramesPerSecond) =>
            deterministicFixedBudget
                ? GiSourceSweepNominalFramesPerSecond
                : observedFramesPerSecond;

        private void UpdateSourceSweepFrameRateEstimate()
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (_sourceSweepLastTimestamp != 0)
            {
                double elapsedSeconds =
                    (timestamp - _sourceSweepLastTimestamp) /
                    (double)Stopwatch.Frequency;
                // Long debugger/suspend gaps are not render throughput and must
                // not collapse the authored sweep budget after resume.
                if (elapsedSeconds > 0.0 && elapsedSeconds <= 1.0)
                {
                    float observedFramesPerSecond = Math.Clamp(
                        (float)(1.0 / elapsedSeconds),
                        GiSourceSweepMinimumFramesPerSecond,
                        GiSourceSweepMaximumFramesPerSecond);
                    _sourceSweepFramesPerSecond +=
                        (observedFramesPerSecond - _sourceSweepFramesPerSecond) *
                        GiSourceSweepFrameRateSmoothing;
                }
            }

            _sourceSweepLastTimestamp = timestamp;
        }

        internal static bool ShouldWakeTransportPropagationNeighborhood(
            bool transportV2Active,
            bool sourceRefresh,
            bool globalConvergencePending) =>
            transportV2Active &&
            sourceRefresh &&
            !globalConvergencePending;

        internal static bool ShouldDeferRoutineSourceRefresh(
            bool routineSourceRefresh,
            int scheduledSourceRefreshCount,
            int targetSourceRefreshCount) =>
            routineSourceRefresh &&
            Math.Max(0, scheduledSourceRefreshCount) >=
                Math.Max(0, targetSourceRefreshCount);

        internal static bool ShouldPropagateRoutineSourceRefresh(
            bool residualEnvelopeValid,
            float residualEnvelope,
            float stableResidualThreshold)
        {
            if (!residualEnvelopeValid ||
                !float.IsFinite(residualEnvelope) ||
                residualEnvelope < 0.0f ||
                !float.IsFinite(stableResidualThreshold) ||
                stableResidualThreshold < 0.0f)
            {
                return true;
            }

            // Retirement still uses the authored convergence threshold. A
            // routine validation sample needs a wider wake threshold so tiny
            // solver/readback variation around that boundary cannot repeatedly
            // invalidate 27 otherwise stable neighbours. Explicit lighting,
            // geometry, relocation, and generation changes bypass this routine
            // path and remain fail-closed.
            float propagationThreshold =
                stableResidualThreshold *
                RoutineSourcePropagationResidualMultiplier;
            return !float.IsFinite(propagationThreshold) ||
                residualEnvelope > propagationThreshold;
        }

        internal static bool IsTransportSourceGenerationBoundary(
            bool sourceRefresh,
            uint cachedSourceLightingGeneration,
            uint requestedSourceLightingGeneration,
            uint currentSourceLightingGeneration,
            bool freshPhysicalProbe)
        {
            if (!sourceRefresh)
                return false;

            uint targetGeneration = requestedSourceLightingGeneration == 0u
                ? currentSourceLightingGeneration
                : requestedSourceLightingGeneration;
            return freshPhysicalProbe ||
                cachedSourceLightingGeneration == 0u ||
                cachedSourceLightingGeneration != targetGeneration;
        }

        internal static int ResolveTransportSourceRefreshWatchdogGeneration(
            int minimumSolverGenerations)
        {
            long minimum = Math.Max(1, minimumSolverGenerations);
            return (int)Math.Min(
                byte.MaxValue,
                minimum * TransportSourceRefreshSolverWatchdogMultiplier);
        }

        internal static bool ShouldPrioritizeCachedTransportSolve(
            bool transportV2Active,
            bool globalConvergencePending,
            bool sourceRefreshRequired,
            bool hasLocalConvergenceEvidence)
        {
            return transportV2Active &&
                !globalConvergencePending &&
                !sourceRefreshRequired &&
                !hasLocalConvergenceEvidence;
        }

        internal static float CalculateTransportConvergenceResidual(
            Vector3 current,
            Vector3 previous,
            float relativeThreshold)
        {
            if (!float.IsFinite(current.X) || !float.IsFinite(current.Y) || !float.IsFinite(current.Z) ||
                !float.IsFinite(previous.X) || !float.IsFinite(previous.Y) || !float.IsFinite(previous.Z) ||
                !float.IsFinite(relativeThreshold))
            {
                return 1.0f;
            }

            float threshold = Math.Clamp(relativeThreshold, 0.001f, 1.0f);
            float absoluteScale = TransportAbsoluteResidualTolerance / threshold;
            float x = CalculateTransportConvergenceResidualChannel(current.X, previous.X, absoluteScale);
            float y = CalculateTransportConvergenceResidualChannel(current.Y, previous.Y, absoluteScale);
            float z = CalculateTransportConvergenceResidualChannel(current.Z, previous.Z, absoluteScale);
            return Math.Clamp(Math.Max(x, Math.Max(y, z)), 0.0f, 1.0f);
        }

        private static float CalculateTransportConvergenceResidualChannel(
            float current,
            float previous,
            float absoluteScale)
        {
            float difference = Math.Abs(current - previous);
            float magnitude = Math.Max(Math.Abs(current), Math.Abs(previous));
            float residual = difference / Math.Max(magnitude, absoluteScale);
            return float.IsFinite(residual) ? residual : 1.0f;
        }

        internal static float UpdateTransportResidualEnvelope(
            float previousEnvelope,
            float currentResidual)
        {
            float safePrevious = float.IsFinite(previousEnvelope) && previousEnvelope >= 0.0f
                ? Math.Clamp(previousEnvelope, 0.0f, 1.0f)
                : 1.0f;
            float safeCurrent = float.IsFinite(currentResidual) && currentResidual >= 0.0f
                ? Math.Clamp(currentResidual, 0.0f, 1.0f)
                : 1.0f;
            return Math.Max(safeCurrent, safePrevious * TransportResidualEnvelopeDecay);
        }

        internal static float AggregateTransportConvergenceResiduals(ReadOnlySpan<float> residuals)
        {
            float aggregate = 0.0f;
            for (int i = 0; i < residuals.Length; i++)
            {
                float residual = residuals[i];
                if (!float.IsFinite(residual) || residual < 0.0f)
                    return 1.0f;
                aggregate = Math.Max(aggregate, Math.Clamp(residual, 0.0f, 1.0f));
            }

            return aggregate;
        }

        internal static SimpleDdgiSchedulerWorkClass ResolveSchedulerWorkClass(
            bool zeroSupport,
            bool freshOrExposed,
            bool visible,
            bool dirty,
            bool retry,
            int ringIndex)
        {
            if (zeroSupport && visible)
                return SimpleDdgiSchedulerWorkClass.VisibleZeroSupport;
            if (freshOrExposed && visible)
                return SimpleDdgiSchedulerWorkClass.FreshExposedVisible;
            if (dirty && visible)
                return SimpleDdgiSchedulerWorkClass.VisibleDirty;
            if (retry && visible)
                return SimpleDdgiSchedulerWorkClass.VisibleRetry;
            return Math.Clamp(ringIndex, 0, 2) switch
            {
                0 => SimpleDdgiSchedulerWorkClass.NearMaintenance,
                1 => SimpleDdgiSchedulerWorkClass.MidMaintenance,
                _ => SimpleDdgiSchedulerWorkClass.FarMaintenance
            };
        }

        internal static SimpleDdgiSchedulerWorkClass ResolveSchedulerWorkClass(
            bool freshOrExposed,
            bool visible,
            bool dirty,
            bool retry,
            int ringIndex) =>
            ResolveSchedulerWorkClass(
                zeroSupport: false,
                freshOrExposed,
                visible,
                dirty,
                retry,
                ringIndex);

        private bool IsEligibleForVisibleRetry(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeInactive.Length)
                return false;

            if (_probeInactive[probeIndex] != 0)
                return GetProbeAge(probeIndex) >= InactiveProbeRetryFrames;

            return _probeConvergenceReadbackValid != 0 &&
                (uint)probeIndex < (uint)_probeStableUpdateCounts.Length &&
                _probeStableUpdateCounts[probeIndex] < _settings.GlobalIllumination.SimpleDdgiStableMaintenanceUpdateCount;
        }

        private static int WorkClassOffset(int volumeIndex) =>
            Math.Max(0, volumeIndex) * SchedulerWorkClassCount;

        internal static int ResolveProbeUpdateStride(int probeCount)
        {
            if (probeCount <= 2)
                return 1;

            int candidate = probeCount / 2 + 1;
            while (candidate < probeCount && GreatestCommonDivisor(candidate, probeCount) != 1)
                candidate++;
            return candidate < probeCount ? candidate : 1;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int remainder = a % b;
                a = b;
                b = remainder;
            }

            return Math.Max(a, 1);
        }

        private bool ShouldSkipInactiveProbe(int probeIndex)
        {
            bool inRange = (uint)probeIndex < (uint)_probeInactive.Length &&
                (uint)probeIndex < (uint)_probeFresh.Length &&
                (uint)probeIndex < (uint)_probeLastUpdatedFrames.Length;
            return inRange && ShouldSkipInactiveProbeForScheduling(
                _settings.GlobalIllumination.SimpleDdgiClassificationSchedulingEnabled,
                _probeInactive[probeIndex] != 0,
                _probeFresh[probeIndex] != 0,
                GetProbeAge(probeIndex),
                InactiveProbeRetryFrames);
        }

        private bool IsConfidentlyInactiveProbe(int probeIndex) =>
            (uint)probeIndex < (uint)_probeInactive.Length &&
            _probeInactive[probeIndex] != 0 &&
            (uint)probeIndex < (uint)_probeClassifications.Length &&
            _probeClassifications[probeIndex] == 1u;

        internal static bool ShouldSkipInactiveProbeForScheduling(
            bool classificationSchedulingEnabled,
            bool inactive,
            bool freshOrRelocationPending,
            uint age,
            uint retryFrames)
        {
            // A pending relocation is represented as fresh on the CPU after
            // readback. It must bypass inactive throttling so the atlas can be
            // republished from the committed probe position immediately.
            return classificationSchedulingEnabled &&
                inactive &&
                !freshOrRelocationPending &&
                age < retryFrames;
        }

        private void BeginProbeDirtyLatency(int probeIndex, uint startFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                (uint)probeIndex >= (uint)_probeDirtyLatencyStartFrames.Length)
            {
                return;
            }

            if (_probeDirtyLatencyStates[probeIndex] == 0 && _dirtyLatencyOutstandingEventCount < uint.MaxValue)
                _dirtyLatencyOutstandingEventCount++;
            // 1 = waiting to enter a queue, 2 = scheduled but waiting for the
            // first completed blend, 3 = completed and waiting for convergence.
            _probeDirtyLatencyStates[probeIndex] = 1;
            _probeDirtyLatencyStartFrames[probeIndex] = startFrame;
        }

        private void ClearProbeDirtyLatency(int probeIndex)
        {
            if ((uint)probeIndex < (uint)_probeDirtyLatencyStates.Length)
            {
                if (_probeDirtyLatencyStates[probeIndex] != 0 && _dirtyLatencyOutstandingEventCount > 0)
                    _dirtyLatencyOutstandingEventCount--;
                _probeDirtyLatencyStates[probeIndex] = 0;
            }
            if ((uint)probeIndex < (uint)_probeDirtyLatencyStartFrames.Length)
                _probeDirtyLatencyStartFrames[probeIndex] = 0;
        }

        private void RecordDirtyFirstScheduledUpdate(int probeIndex, uint scheduledFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                _probeDirtyLatencyStates[probeIndex] != 1 ||
                (uint)probeIndex >= (uint)_probeDirtyLatencyStartFrames.Length)
            {
                return;
            }

            uint elapsedFrames = unchecked(scheduledFrame - _probeDirtyLatencyStartFrames[probeIndex]);
            RecordLatencySample(
                _dirtyFirstScheduledLatencyBuckets,
                ref _dirtyFirstScheduledLatencySampleCount,
                ref _dirtyFirstScheduledLatencyCensoredCount,
                ref _dirtyFirstScheduledLatencyMaxFrames,
                elapsedFrames);
            _probeDirtyLatencyStates[probeIndex] = 2;
        }

        private void RecordDirtyFirstCompletedUpdate(int probeIndex, uint completedFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                _probeDirtyLatencyStates[probeIndex] != 2 ||
                (uint)probeIndex >= (uint)_probeDirtyLatencyStartFrames.Length)
            {
                return;
            }

            uint elapsedFrames = unchecked(completedFrame - _probeDirtyLatencyStartFrames[probeIndex]);
            RecordLatencySample(
                _dirtyFirstUpdateLatencyBuckets,
                ref _dirtyFirstUpdateLatencySampleCount,
                ref _dirtyFirstUpdateLatencyCensoredCount,
                ref _dirtyFirstUpdateLatencyMaxFrames,
                elapsedFrames);
            _probeDirtyLatencyStates[probeIndex] = 3;
        }

        private void RecordDirtyConvergenceIfStable(int probeIndex, uint observedFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                _probeDirtyLatencyStates[probeIndex] != 3 ||
                (uint)probeIndex >= (uint)_probeDirtyLatencyStartFrames.Length ||
                (uint)probeIndex >= (uint)_probeStableUpdateCounts.Length)
            {
                return;
            }

            int requiredStableUpdates = Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiStableMaintenanceUpdateCount);
            if (_probeStableUpdateCounts[probeIndex] < requiredStableUpdates)
                return;

            uint elapsedFrames = unchecked(observedFrame - _probeDirtyLatencyStartFrames[probeIndex]);
            RecordLatencySample(
                _dirtyConvergenceLatencyBuckets,
                ref _dirtyConvergenceLatencySampleCount,
                ref _dirtyConvergenceLatencyCensoredCount,
                ref _dirtyConvergenceLatencyMaxFrames,
                elapsedFrames);
            ClearProbeDirtyLatency(probeIndex);
        }

        private static void RecordLatencySample(
            uint[] buckets,
            ref uint sampleCount,
            ref uint censoredCount,
            ref uint maximumFrames,
            uint elapsedFrames)
        {
            int bucket = elapsedFrames >= DirtyLatencyBucketCount - 1
                ? DirtyLatencyBucketCount - 1
                : (int)elapsedFrames;
            if ((uint)bucket < (uint)buckets.Length && buckets[bucket] < uint.MaxValue)
                buckets[bucket]++;
            if (elapsedFrames >= DirtyLatencyBucketCount - 1 && censoredCount < uint.MaxValue)
                censoredCount++;
            if (sampleCount < uint.MaxValue)
                sampleCount++;
            maximumFrames = Math.Max(maximumFrames, elapsedFrames);
        }

        internal static int CalculateLatencyPercentile(
            ReadOnlySpan<uint> buckets,
            uint sampleCount,
            float percentile)
        {
            if (sampleCount == 0 || buckets.IsEmpty)
                return 0;

            percentile = Math.Clamp(percentile, 0.0f, 1.0f);
            ulong targetRank = Math.Max(1UL, (ulong)Math.Ceiling(sampleCount * (double)percentile));
            ulong cumulative = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                cumulative += buckets[i];
                if (cumulative >= targetRank)
                    return i;
            }

            return buckets.Length - 1;
        }

        private static int ClampUIntToInt(uint value) =>
            value > int.MaxValue ? int.MaxValue : (int)value;

        private SimpleDdgiSchedulerPressureReason ResolveSchedulerPressureReason(
            int capacity,
            int scheduled,
            int eligiblePending)
        {
            if (_rayBudgetRejectedProbeCount > 0)
                return SimpleDdgiSchedulerPressureReason.PrimaryRayCap;
            if (eligiblePending <= 0)
                return SimpleDdgiSchedulerPressureReason.NoEligibleWork;
            if (scheduled >= eligiblePending)
                return SimpleDdgiSchedulerPressureReason.None;
            int baseBudget = _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(
                    _probeCount,
                    Math.Max(0, _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame));
            int authoredOrDirtyBudget = _lightingDirtyFrames > 0
                ? _schedulerConfiguredRequestBudget
                : baseBudget;
            if (!_schedulerDeterministicFixedBudget &&
                _schedulerFeedbackRequestBudgetCap > 0 &&
                capacity < authoredOrDirtyBudget)
            {
                return SimpleDdgiSchedulerPressureReason.FeedbackReducedBudget;
            }

            return SimpleDdgiSchedulerPressureReason.RequestCap;
        }

        private static int SaturatingAdd(int left, int right)
        {
            if (left <= 0)
                return Math.Max(right, 0);
            if (right <= 0)
                return left;
            return int.MaxValue - left < right ? int.MaxValue : left + right;
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private static long ElapsedMicroseconds(long startTimestamp) =>
            (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);

        private int[] ResolveVolumeQuotas(int updateBudget)
        {
            int[] quotas = _volumeQuotaScratch;
            Array.Clear(quotas, 0, _volumeCount);
            if (updateBudget <= 0 || _volumeCount == 0)
                return quotas;

            int[] minimums = _volumeQuotaMinimumScratch;
            int[] maximums = _volumeQuotaMaximumScratch;
            int[] capacities = _volumeQuotaCapacityScratch;
            int[] weights = _volumeQuotaWeightScratch;
            for (int i = 0; i < _volumeCount; i++)
            {
                SimpleDdgiRingQuality quality = ResolveVolumeQuality(i);
                int probeCount = VolumeProbeCount(_volumeScratch[i]);
                minimums[i] = Math.Min(quality.MinimumUpdateQuota, probeCount);
                maximums[i] = Math.Min(Math.Max(quality.MaximumUpdateQuota, minimums[i]), probeCount);
                capacities[i] = probeCount;
                weights[i] = ResolveVolumeSchedulingWeight(i, quality.RingIndex);
            }

            AllocateUpdateQuotas(
                quotas.AsSpan(0, _volumeCount),
                minimums.AsSpan(0, _volumeCount),
                maximums.AsSpan(0, _volumeCount),
                capacities.AsSpan(0, _volumeCount),
                weights.AsSpan(0, _volumeCount),
                updateBudget);

            return quotas;
        }

        private int ResolveVolumeSchedulingWeight(int volumeIndex, int ringIndex)
        {
            if ((uint)volumeIndex < (uint)_volumeCount &&
                Kind(_volumeScratch[volumeIndex]) == VolumeKindAuthored)
            {
                int purposeWeight = _volumePurposes[volumeIndex] switch
                {
                    SimpleDdgiVolumePurpose.ReceiverHero => 16,
                    SimpleDdgiVolumePurpose.DynamicInfluence => 14,
                    SimpleDdgiVolumePurpose.NavigableInterior => 10,
                    _ => 6
                };
                // A priority is an authoring declaration, not an unbounded way to
                // starve the maintenance rings. Clamp its influence while keeping
                // deterministic ordering for overlapping hero receivers.
                return Math.Clamp(purposeWeight + _volumePriorities[volumeIndex], 1, 32);
            }

            return ringIndex switch
            {
                0 => 8,
                1 => 3,
                _ => 1
            };
        }

        internal static void AllocateUpdateQuotas(
            Span<int> quotas,
            ReadOnlySpan<int> minimums,
            ReadOnlySpan<int> preferredMaximums,
            ReadOnlySpan<int> capacities,
            ReadOnlySpan<int> weights,
            int updateBudget)
        {
            int volumeCount = Math.Min(
                quotas.Length,
                Math.Min(minimums.Length, Math.Min(preferredMaximums.Length, Math.Min(capacities.Length, weights.Length))));
            quotas.Clear();
            int remaining = Math.Max(updateBudget, 0);

            // Preserve the tier's per-ring floors first. Under a deliberately
            // tiny global budget, deterministic round-robin still favors the
            // closest (first) volume by one request at most.
            bool assigned;
            do
            {
                assigned = false;
                for (int i = 0; i < volumeCount && remaining > 0; i++)
                {
                    int target = Math.Min(Math.Max(minimums[i], 0), Math.Max(capacities[i], 0));
                    if (quotas[i] >= target)
                        continue;
                    quotas[i]++;
                    remaining--;
                    assigned = true;
                }
            }
            while (assigned && remaining > 0);

            // The old maxima are preferred operating points, not a reason to
            // discard an explicitly configured global budget. Fill them first,
            // then distribute any remaining work with the same ring weights.
            AllocateWeightedQuotaRemainder(quotas, preferredMaximums, capacities, weights, volumeCount, ref remaining);
            AllocateWeightedQuotaRemainder(quotas, capacities, capacities, weights, volumeCount, ref remaining);
        }

        private static void AllocateWeightedQuotaRemainder(
            Span<int> quotas,
            ReadOnlySpan<int> limits,
            ReadOnlySpan<int> capacities,
            ReadOnlySpan<int> weights,
            int volumeCount,
            ref int remaining)
        {
            while (remaining > 0)
            {
                int selected = -1;
                int selectedScore = int.MaxValue;
                for (int i = 0; i < volumeCount; i++)
                {
                    int limit = Math.Min(Math.Max(limits[i], 0), Math.Max(capacities[i], 0));
                    if (quotas[i] >= limit)
                        continue;

                    int score = quotas[i] * 64 / Math.Max(weights[i], 1);
                    if (selected < 0 || score < selectedScore || (score == selectedScore && i < selected))
                    {
                        selected = i;
                        selectedScore = score;
                    }
                }

                if (selected < 0)
                    break;
                quotas[selected]++;
                remaining--;
            }
        }

        private bool AddProbeUpdate(
            ref int count,
            int capacity,
            int probeIndex,
            int volumeIndex,
            uint flags,
            SimpleDdgiSchedulerWorkClass workClass)
        {
            if (count >= capacity ||
                (uint)probeIndex >= (uint)_probeCount ||
                (uint)volumeIndex >= (uint)_volumeCount)
                return false;

            uint effectiveFlags = flags | (_probeFresh[probeIndex] != 0 ? ProbeStateFreshFlag : 0u);
            bool sourceRefresh = NeedsSourceRefresh(probeIndex);
            if (sourceRefresh)
            {
                effectiveFlags |= ProbeUpdateSourceRefreshFlag;
                if (IsRoutineTransportSourceRefresh(probeIndex))
                    effectiveFlags |= ProbeUpdateRoutineSourceRefreshFlag;
            }
            SimpleDdgiRingQuality quality = ResolveVolumeQuality(volumeIndex);
            int requestedRays = ResolveUpdateRayCount(probeIndex, quality, effectiveFlags);
            int sourceRayCount = sourceRefresh
                ? requestedRays
                : ((uint)probeIndex < (uint)_probeSourceRayCounts.Length && _probeSourceRayCounts[probeIndex] > 0
                    ? _probeSourceRayCounts[probeIndex]
                    : requestedRays);
            int primaryRayBudget = Math.Max(0, _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget);
            ulong primaryRayCost = sourceRefresh ? (ulong)requestedRays : 0UL;
            if (primaryRayCost > (ulong)primaryRayBudget ||
                _scheduledPrimaryRayCount > (ulong)primaryRayBudget - primaryRayCost)
            {
                if (_rayBudgetRejectedProbeCount < int.MaxValue)
                    _rayBudgetRejectedProbeCount++;
                ulong rejectedRays = primaryRayCost;
                _rayBudgetRejectedPrimaryRayCount = ulong.MaxValue - _rayBudgetRejectedPrimaryRayCount < rejectedRays
                    ? ulong.MaxValue
                    : _rayBudgetRejectedPrimaryRayCount + rejectedRays;
                int classIndex = (int)workClass;
                if ((uint)classIndex < (uint)_rayRejectedWorkClassCounts.Length)
                    _rayRejectedWorkClassCounts[classIndex] = SaturatingAdd(_rayRejectedWorkClassCounts[classIndex], 1);
                return false;
            }

            int queueOffset = count++;
            uint physicalProbeIndex = checked((uint)probeIndex);
            SimpleDdgiTransportCacheRegion cacheRegion =
                _storageLayout.FindVolume(volumeIndex) ??
                throw new InvalidOperationException(
                    $"Simple-DDGI storage layout is missing volume {volumeIndex} while scheduling probe {probeIndex}.");
            if (!SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
                    cacheRegion,
                    physicalProbeIndex,
                    out uint cacheProbeBaseWordPlusOne))
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI cache address is invalid for physical probe {physicalProbeIndex} in volume {volumeIndex}.");
            }
            _updateQueueScratch[queueOffset] = new GPUSimpleDdgiProbeUpdate
            {
                ProbeIndex = checked((uint)probeIndex),
                VolumeIndex = checked((uint)volumeIndex),
                Flags = PackUpdateFlags(probeIndex, volumeIndex, effectiveFlags, requestedRays, sourceRefresh),
                Reserved0 = PackProbeUpdateMetadata(_probeGenerations[probeIndex], GetProbeAge(probeIndex)),
                SourceRayCount = checked((uint)Math.Clamp(sourceRayCount, 1, GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe)),
                SourceLightingGeneration = sourceRefresh
                    ? _sourceLightingGeneration
                    : ((uint)probeIndex < (uint)_probeSourceLightingGenerations.Length
                        ? _probeSourceLightingGenerations[probeIndex]
                        : 0u),
                SourceEpoch = sourceRefresh
                    ? AdvanceSourceEpoch(
                        (uint)probeIndex < (uint)_probeSourceEpochs.Length
                            ? _probeSourceEpochs[probeIndex]
                            : 0u)
                    : ((uint)probeIndex < (uint)_probeSourceEpochs.Length
                        ? _probeSourceEpochs[probeIndex]
                        : 0u),
                PhysicalProbeIndex = physicalProbeIndex,
                PageMappingGeneration =
                    SimpleDdgiProbeAddress.DenseMappingGeneration,
                ResidencyResourceGeneration =
                    _capacityPlan.ResidencyMode.CollectsDemand()
                        ? _probePageCache.ResourceGeneration
                        : 0u,
                CacheProbeBaseWordPlusOne = cacheProbeBaseWordPlusOne
            };
            _queuedWorkClassScratch[queueOffset] = (byte)workClass;
            int scheduledClassIndex = (int)workClass;
            if ((uint)scheduledClassIndex < (uint)_scheduledWorkClassCounts.Length)
                _scheduledWorkClassCounts[scheduledClassIndex] = SaturatingAdd(_scheduledWorkClassCounts[scheduledClassIndex], 1);
            int schedulerQueueIndex = WorkClassOffset(volumeIndex) + scheduledClassIndex;
            if (sourceRefresh &&
                _schedulerSourceRefreshQueues.GetProbeQueue(probeIndex) == schedulerQueueIndex)
            {
                _volumeSourceRefreshUsageScratch[schedulerQueueIndex] = SaturatingAdd(
                    _volumeSourceRefreshUsageScratch[schedulerQueueIndex],
                    1);
            }
            else if (_schedulerCachedSolverQueues.GetProbeQueue(probeIndex) == schedulerQueueIndex)
            {
                _volumeCachedSolverUsageScratch[schedulerQueueIndex] = SaturatingAdd(
                    _volumeCachedSolverUsageScratch[schedulerQueueIndex],
                    1);
            }
            _probeQueued[probeIndex] = 1;
            if (TailCertificationEnabled &&
                !sourceRefresh &&
                _transportSolveController.Phase == SimpleDdgiTransportPhase.AcceleratedSolve &&
                (uint)probeIndex < (uint)_probeSourceLightingGenerations.Length &&
                (uint)probeIndex < (uint)_probeSourceRayCounts.Length &&
                _probeSourceLightingGenerations[probeIndex] == _sourceLightingGeneration &&
                _probeSourceRayCounts[probeIndex] > 0)
            {
                _transportSolveController.MarkParticipantVisited(
                    probeIndex,
                    CreateTransportTailGenerations());
            }
            RecordDirtyFirstScheduledUpdate(probeIndex, _frameIndex);
            return true;
        }

        private uint PackUpdateFlags(
            int probeIndex,
            int volumeIndex,
            uint flags,
            int rayCount,
            bool sourceRefresh)
        {
            SimpleDdgiRingQuality quality = ResolveVolumeQuality(volumeIndex);
            uint packedMaterialCascade = checked((uint)Math.Clamp(quality.MaterialTextureMaxCascade + 1, 0, 7));
            // Zero is the queue's "unset" sentinel. Bias the stored value so a
            // zeroed entry falls back to the pass default instead of disabling all
            // direct lighting.
            uint packedMaxLights = checked((uint)Math.Clamp(quality.MaxShadedLights, 0, 62) + 1);
            flags |= packedMaterialCascade << ProbeUpdateMaterialTextureCascadeShift;
            flags |= packedMaxLights << ProbeUpdateMaxShadedLightsShift;
            _effectiveMaxShadedLights = Math.Max(
                _effectiveMaxShadedLights,
                Math.Clamp(quality.MaxShadedLights, 0, 62));
            _scheduledTransportRayCount = SaturatingAdd(_scheduledTransportRayCount, (ulong)rayCount);
            if (sourceRefresh)
            {
                _scheduledPrimaryRayCount = SaturatingAdd(_scheduledPrimaryRayCount, (ulong)rayCount);
                _scheduledSourceRayCount = SaturatingAdd(_scheduledSourceRayCount, (ulong)rayCount);
                _ringScheduledPrimaryRayCounts[quality.RingIndex] = SaturatingAdd(
                    _ringScheduledPrimaryRayCounts[quality.RingIndex],
                    (ulong)rayCount);
                if (_sourceRefreshProbeCount < int.MaxValue)
                    _sourceRefreshProbeCount++;
                if (IsRoutinePeriodicTransportSourceRefresh(probeIndex) &&
                    _ageRefreshProbeCount < int.MaxValue)
                {
                    _ageRefreshProbeCount++;
                }
            }
            else if (_sourceCacheReuseProbeCount < int.MaxValue)
            {
                _sourceCacheReuseProbeCount++;
            }
            if (rayCount >= quality.FullRays)
            {
                _fullRayProbeUpdateCount++;
                _ringFullRayProbeUpdateCounts[quality.RingIndex]++;
            }
            else
            {
                flags |= ProbeUpdateMaintenanceFlag;
                _maintenanceRayProbeUpdateCount++;
                _ringMaintenanceRayProbeUpdateCounts[quality.RingIndex]++;
                _adaptiveRaySavedPrimaryRayCount += (ulong)Math.Max(0, quality.FullRays - rayCount);
            }

            return flags | ((uint)Math.Clamp(rayCount, 1, ushort.MaxValue) << 16);
        }

        internal static uint PackProbeUpdateMetadata(uint generation, uint age) =>
            NormalizeProbeGeneration(generation) |
            (Math.Min(age, ProbeUpdateAgeValueMask) << ProbeUpdateAgeShift);

        internal static uint ReadProbeUpdateGeneration(uint metadata) =>
            metadata & ProbeStateGenerationValueMask;

        internal static uint ReadProbeUpdateAge(uint metadata) =>
            (metadata >> ProbeUpdateAgeShift) & ProbeUpdateAgeValueMask;

        internal static bool ShouldRetireRelocationPendingOnCpu(
            bool relocationPending,
            uint updateAge) =>
            relocationPending &&
            updateAge >= RelocationPendingMaximumRetryAge;

        private int ResolveUpdateRayCount(int probeIndex, SimpleDdgiRingQuality quality, uint flags)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            // A field-wide source/layout change needs coherent low-frequency
            // transport, not a handful of maintenance directions that can make
            // a dark probe look locally stable before its bright neighbors have
            // propagated. Cached V2 solves have no primary ray-query cost, so
            // spend full ring directions for this bounded warmup phase.
            if (TransportGlobalConvergencePending)
                return quality.FullRays;
            if (!gi.SimpleDdgiAdaptiveRaysEnabled ||
                _lightingDirtyFrames > 0 ||
                (flags & ProbeStateFreshFlag) != 0 ||
                (flags & ProbeUpdateSourceRefreshFlag) != 0 ||
                _probeConvergenceReadbackValid == 0 ||
                (uint)probeIndex >= (uint)_probeStableUpdateCounts.Length)
            {
                return quality.FullRays;
            }

            if (_probeStableUpdateCounts[probeIndex] < gi.SimpleDdgiStableMaintenanceUpdateCount)
                return quality.FullRays;

            return quality.MaintenanceRays;
        }

        private void MarkVolumeFresh(GPUSimpleDdgiVolume volume)
        {
            int firstProbe = FirstProbe(volume);
            int count = VolumeProbeCount(volume);
            for (int i = 0; i < count; i++)
                MarkProbeFresh(firstProbe + i, scrollExposed: false, forceGenerationAdvance: true);
        }

        private void MarkProbeFresh(
            int probeIndex,
            bool scrollExposed,
            bool dirty = false,
            bool forceGenerationAdvance = false,
            uint dirtyReasonFlags = 0u)
        {
            if ((uint)probeIndex >= (uint)_probeFresh.Length)
                return;

            bool wasFresh = _probeFresh[probeIndex] != 0;
            bool shouldAdvanceGeneration = ShouldAdvanceProbeGenerationForInvalidation(
                wasFresh,
                _probeInvalidationMarkers[probeIndex],
                _currentProbeInvalidationMarkerSerial,
                forceGenerationAdvance);
            if (shouldAdvanceGeneration)
            {
                QueueReceiverProbeInvalidation(probeIndex);
                _probeInvalidationMarkers[probeIndex] = _currentProbeInvalidationMarkerSerial;
                _newlyInvalidatedProbeCount++;
                _probeGenerations[probeIndex] = AdvanceProbeGeneration(_probeGenerations[probeIndex]);
                float previousRelocationFraction = CalculateProbeRelocationFraction(
                    probeIndex,
                    _probeRelocations[probeIndex]);
                if (previousRelocationFraction > 0.0f)
                {
                    _probeRelocationCount = Math.Max(0, _probeRelocationCount - 1);
                    _relocationFractionSumEstimate = Math.Max(
                        0.0f,
                        _relocationFractionSumEstimate - previousRelocationFraction);
                    _averageRelocationFractionEstimate = _probeRelocationCount > 0
                        ? _relocationFractionSumEstimate / _probeRelocationCount
                        : 0.0f;
                }
                _probeRelocations[probeIndex] = Vector3.Zero;
                // Preserve the last classification until this fresh transaction
                // produces replacement evidence. Fresh work bypasses inactive
                // throttling, and the shader can reactivate in one update; this
                // avoids a camera scroll or regional dirty event publishing an
                // embedded probe as active during CPU/GPU readback latency.
                if ((uint)probeIndex < (uint)_probeRelocationPending.Length)
                    _probeRelocationPending[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                    _probeStableUpdateCounts[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                    _probeLuminanceChangeEma[probeIndex] = 0.0f;
                if ((uint)probeIndex <
                    (uint)_probeRoutineMaintenancePending.Length)
                {
                    _probeRoutineMaintenancePending[probeIndex] = 0;
                }
                if ((uint)probeIndex < (uint)_probeSourceLightingGenerations.Length)
                    _probeSourceLightingGenerations[probeIndex] = 0u;
                if ((uint)probeIndex < (uint)_probeLastSourceRefreshFrames.Length)
                    _probeLastSourceRefreshFrames[probeIndex] = 0u;
                if ((uint)probeIndex < (uint)_probeSourceEpochs.Length)
                    AdvanceProbeSourceEpoch(probeIndex);
                if ((uint)probeIndex < (uint)_probeSourceRayCounts.Length)
                    _probeSourceRayCounts[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeTransportGenerationCounts.Length)
                    _probeTransportGenerationCounts[probeIndex] = 0;
                if (_sourceCacheInvalidationCount < ulong.MaxValue)
                    _sourceCacheInvalidationCount++;
                ClearProbeDirtyLatency(probeIndex);
                if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length)
                    _probeSchedulingFlags[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeDirtyReasons.Length)
                    _probeDirtyReasons[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeLastUpdatedFrames.Length)
                    _probeLastUpdatedFrames[probeIndex] = unchecked(_frameIndex - 1u);
                _probeStateDirtySlots.Add(probeIndex);
            }
            _probeFresh[probeIndex] = 1;
            if ((uint)probeIndex < (uint)_probeVisibilityValid.Length)
                _probeVisibilityValid[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length)
            {
                if (scrollExposed)
                    _probeSchedulingFlags[probeIndex] |= ProbeSchedulingScrollExposedFlag;
                if (dirty)
                    _probeSchedulingFlags[probeIndex] |= ProbeSchedulingRegionalDirtyFlag;
            }
            if (dirty && (uint)probeIndex < (uint)_probeDirtyReasons.Length)
            {
                _probeDirtyReasons[probeIndex] |= checked((byte)(dirtyReasonFlags & byte.MaxValue));
            }
            if (dirty && shouldAdvanceGeneration)
                BeginProbeDirtyLatency(probeIndex, _frameIndex);
            if (scrollExposed && shouldAdvanceGeneration)
                _recenterRefreshProbeCount++;
            if (dirty && shouldAdvanceGeneration)
                _dirtyRefreshProbeCount++;
            MarkProbeSchedulerDirty(probeIndex);
            MarkProbeVisibilityDirty(probeIndex);
        }

        private void MarkProbeSourceCacheStale(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeCount)
                return;

            // Source-cache validity is independent of the physical-slot
            // generation. Preserve relocation, classification, and canonical
            // irradiance while forcing the next queue entry through its complete
            // traced source sequence.
            if ((uint)probeIndex < (uint)_probeSourceLightingGenerations.Length)
                _probeSourceLightingGenerations[probeIndex] = 0u;
            if ((uint)probeIndex < (uint)_probeLastSourceRefreshFrames.Length)
                _probeLastSourceRefreshFrames[probeIndex] = 0u;
            if ((uint)probeIndex < (uint)_probeSourceEpochs.Length)
                AdvanceProbeSourceEpoch(probeIndex);
            if ((uint)probeIndex < (uint)_probeSourceRayCounts.Length)
                _probeSourceRayCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeTransportGenerationCounts.Length)
                _probeTransportGenerationCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                _probeStableUpdateCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                _probeLuminanceChangeEma[probeIndex] = 0.0f;
            if ((uint)probeIndex < (uint)_probeRoutineMaintenancePending.Length)
                _probeRoutineMaintenancePending[probeIndex] = RoutineMaintenanceNone;
            // BuildProbeStateRecord omits GPU-only transient flags, including
            // SourceCacheInvalid. Queueing this sparse upload is what commits the
            // CPU invalidation and prevents the completed readback from re-arming
            // the same source repair indefinitely.
            _probeStateDirtySlots.Add(probeIndex);
            MarkProbeSchedulerDirty(probeIndex);
            MarkProbeVisibilityDirty(probeIndex);
            // A per-slot cache repair or classification reactivation is a local
            // source change, not a new field configuration. Wake its trilinear
            // transport neighbourhood while preserving convergence elsewhere.
            // Lighting, layout, and explicit global invalidations still use the
            // field-wide barrier paths above; ordinary maintenance residuals do
            // not recursively turn one repair into an unbounded wake wave.
            MarkTransportPropagationNeighborhoodDirty(probeIndex);
            _sourceCacheInvalidationCount = SaturatingAdd(_sourceCacheInvalidationCount, 1UL);
        }

        private void MarkTransportPropagationNeighborhoodDirty(
            int sourceProbeIndex,
            bool routineMaintenance = false)
        {
            int volumeIndex = ResolveVolumeIndexForProbe(sourceProbeIndex);
            if ((uint)volumeIndex >= (uint)_volumeCount)
                return;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = FirstProbe(volume);
            int localSource = sourceProbeIndex - firstProbe;
            if ((uint)localSource >= (uint)VolumeProbeCount(volume))
                return;

            (int sourceX, int sourceY, int sourceZ) =
                CalculateLogicalProbeCoordinate(volume, localSource);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int minimumX = Math.Max(0, sourceX - 1);
            int maximumX = Math.Min(countX - 1, sourceX + 1);
            int minimumY = Math.Max(0, sourceY - 1);
            int maximumY = Math.Min(countY - 1, sourceY + 1);
            int minimumZ = Math.Max(0, sourceZ - 1);
            int maximumZ = Math.Min(countZ - 1, sourceZ + 1);
            for (int z = minimumZ; z <= maximumZ; z++)
                for (int y = minimumY; y <= maximumY; y++)
                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        int local = CalculatePhysicalProbeLocalIndex(volume, x, y, z);
                        int probeIndex = firstProbe + local;
                        if ((uint)probeIndex >= (uint)_probeStableUpdateCounts.Length ||
                            ((uint)probeIndex < (uint)_probeInactive.Length &&
                                _probeInactive[probeIndex] != 0))
                        {
                            continue;
                        }

                        _probeStableUpdateCounts[probeIndex] = 0;
                        if ((uint)probeIndex <
                            (uint)_probeRoutineMaintenancePending.Length)
                        {
                            _probeRoutineMaintenancePending[probeIndex] =
                                routineMaintenance
                                    ? RoutineMaintenanceConvergencePending
                                    : RoutineMaintenanceNone;
                        }
                        MarkProbeSchedulerDirty(probeIndex);
                        MarkProbeVisibilityDirty(probeIndex);
                    }
        }

        internal static bool ShouldAdvanceProbeGenerationForInvalidation(
            bool wasFresh,
            uint previousInvalidationMarker,
            uint currentInvalidationMarker,
            bool forceGenerationAdvance)
        {
            return !wasFresh ||
                (forceGenerationAdvance && previousInvalidationMarker != currentInvalidationMarker);
        }

        private static int ResolveMaximumRingFullRays(GlobalIlluminationSettings gi)
        {
            return Math.Clamp(
                Math.Max(gi.SimpleDdgiNearFullRaysPerProbe,
                    Math.Max(gi.SimpleDdgiMidFullRaysPerProbe, gi.SimpleDdgiFarFullRaysPerProbe)),
                1,
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
        }

        private SimpleDdgiRingQuality ResolveVolumeQuality(int volumeIndex)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            int ringIndex = 0;
            if ((uint)volumeIndex < (uint)_volumeCount)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                if (Kind(volume) == VolumeKindRing)
                    ringIndex = Math.Clamp(SourceOrdinal(volume) - 10_000, 0, 2);
            }

            return ringIndex switch
            {
                1 => new SimpleDdgiRingQuality(
                    RingIndex: 1,
                    FullRays: gi.SimpleDdgiMidFullRaysPerProbe,
                    MaintenanceRays: gi.SimpleDdgiMidMaintenanceRaysPerProbe,
                    MinimumUpdateQuota: gi.SimpleDdgiMidMinimumUpdateQuota,
                    MaximumUpdateQuota: gi.SimpleDdgiMidMaximumUpdateQuota,
                    MaterialTextureMaxCascade: gi.SimpleDdgiMidMaterialTextureMaxCascade,
                    MaxShadedLights: gi.SimpleDdgiMidMaxShadedLights),
                2 => new SimpleDdgiRingQuality(
                    RingIndex: 2,
                    FullRays: gi.SimpleDdgiFarFullRaysPerProbe,
                    MaintenanceRays: gi.SimpleDdgiFarMaintenanceRaysPerProbe,
                    MinimumUpdateQuota: gi.SimpleDdgiFarMinimumUpdateQuota,
                    MaximumUpdateQuota: gi.SimpleDdgiFarMaximumUpdateQuota,
                    MaterialTextureMaxCascade: gi.SimpleDdgiFarMaterialTextureMaxCascade,
                    MaxShadedLights: gi.SimpleDdgiFarMaxShadedLights),
                _ => new SimpleDdgiRingQuality(
                    RingIndex: 0,
                    FullRays: gi.SimpleDdgiNearFullRaysPerProbe,
                    MaintenanceRays: gi.SimpleDdgiNearMaintenanceRaysPerProbe,
                    MinimumUpdateQuota: gi.SimpleDdgiNearMinimumUpdateQuota,
                    MaximumUpdateQuota: gi.SimpleDdgiNearMaximumUpdateQuota,
                    MaterialTextureMaxCascade: gi.SimpleDdgiNearMaterialTextureMaxCascade,
                    MaxShadedLights: gi.SimpleDdgiNearMaxShadedLights)
            };
        }

        private int ResolveVolumeIndexForProbe(int probeIndex)
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                int first = FirstProbe(volume);
                int count = VolumeProbeCount(volume);
                if (probeIndex >= first && probeIndex < first + count)
                    return i;
            }

            return -1;
        }

        private bool TryGetPreviousMatchingVolume(int volumeIndex, GPUSimpleDdgiVolume current, out GPUSimpleDdgiVolume previous)
        {
            previous = default;
            // Sorting can move a candidate when authored volumes are edited.  A
            // stable source key is the identity; index equality is merely a useful
            // fast path and must not be a correctness requirement.
            if ((uint)volumeIndex < (uint)_previousVolumeCount)
            {
                GPUSimpleDdgiVolume indexed = _previousVolumeScratch[volumeIndex];
                if (MatchesVolumeIdentity(indexed, current))
                {
                    previous = indexed;
                    return true;
                }
            }

            for (int i = 0; i < _previousVolumeCount; i++)
            {
                GPUSimpleDdgiVolume candidate = _previousVolumeScratch[i];
                if (!MatchesVolumeIdentity(candidate, current))
                    continue;
                previous = candidate;
                return true;
            }

            return false;
        }

        private static bool MatchesVolumeIdentity(GPUSimpleDdgiVolume previous, GPUSimpleDdgiVolume current) =>
            Kind(previous) == Kind(current) &&
            SourceOrdinal(previous) == SourceOrdinal(current) &&
            CountX(previous) == CountX(current) &&
            CountY(previous) == CountY(current) &&
            CountZ(previous) == CountZ(current) &&
            FirstProbe(previous) == FirstProbe(current) &&
            NearlyEqual(Spacing(previous), Spacing(current), 0.0001f);

        internal static bool TryResolveCellDelta(GPUSimpleDdgiVolume previous, GPUSimpleDdgiVolume current, out int deltaX, out int deltaY, out int deltaZ)
        {
            float spacing = Spacing(current);
            Vector3 delta = (Origin(previous) - Origin(current)) / spacing;
            deltaX = (int)MathF.Round(delta.X);
            deltaY = (int)MathF.Round(delta.Y);
            deltaZ = (int)MathF.Round(delta.Z);
            return NearlyEqual(delta.X, deltaX, 0.001f) &&
                NearlyEqual(delta.Y, deltaY, 0.001f) &&
                NearlyEqual(delta.Z, deltaZ, 0.001f);
        }

        private void BuildVolumeTable(
            GlobalIlluminationSettings gi,
            BoundingBox sceneBounds,
            Vector3 cameraPosition,
            bool structuredGatherAvailable,
            IReadOnlyList<GlobalIlluminationProbeVolume>? authoredSceneVolumes)
        {
            _volumeCandidates.Clear();
            Array.Clear(_volumeScratch);
            Array.Clear(_volumePurposes);
            Array.Clear(_volumePriorities);
            // V2 deliberately has no authored-volume dependency.  Its bounded
            // camera clipmaps are the whole field, with density concentrated in
            // the near/mid rings.  Retain the authored path only for the explicit
            // V1 fallback so existing captures can still be replayed.
            bool anyAuthored = false;
            if (!gi.SimpleDdgiTransportV2Enabled)
            {
                int authoredOrdinal = 0;
                foreach (SimpleDdgiAuthoredVolume authored in gi.SimpleDdgiAuthoredVolumes)
                {
                    if (authoredOrdinal >= GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
                        break;
                    authoredOrdinal++;
                    if (!TryCreateAuthoredVolume(authored, authoredOrdinal, out VolumeCandidate candidate))
                        continue;
                    anyAuthored = true;
                    _volumeCandidates.Add(candidate);
                }
                if (authoredSceneVolumes != null)
                {
                    for (int sceneOrdinal = 0;
                         sceneOrdinal < authoredSceneVolumes.Count &&
                         authoredOrdinal < GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount;
                         sceneOrdinal++)
                    {
                        GlobalIlluminationProbeVolume? authored = authoredSceneVolumes[sceneOrdinal];
                        authoredOrdinal++;
                        if (authored == null || !authored.Enabled ||
                            !TryCreateAuthoredVolume(
                                authored,
                                20_000 + sceneOrdinal,
                                out VolumeCandidate candidate))
                        {
                            continue;
                        }
                        anyAuthored = true;
                        _volumeCandidates.Add(candidate);
                    }
                }
            }

            int ringCount = gi.SimpleDdgiRingCount;
            if (!anyAuthored && ringCount == 0)
            {
                BoundingBox legacyBounds = ExpandBounds(sceneBounds, gi.SimpleDdgiProbeSpacing * 1.5f);
                _volumeCandidates.Add(CreateLegacyVolume(gi, legacyBounds, cameraPosition));
            }
            else
            {
                for (int ring = 0; ring < ringCount; ring++)
                    _volumeCandidates.Add(CreateRingVolume(gi, sceneBounds, cameraPosition, ring));
            }

            _volumeCandidates.Sort(static (left, right) =>
            {
                int kind = left.KindPriority.CompareTo(right.KindPriority);
                if (kind != 0)
                    return kind;

                // Authored receiver coverage is a scene ownership declaration,
                // not an incidental side effect of probe spacing.  Keep it ahead
                // of camera rings and honour its explicit priority deterministically.
                if (left.Kind == VolumeKindAuthored)
                {
                    int priority = right.Priority.CompareTo(left.Priority);
                    if (priority != 0)
                        return priority;
                    int purpose = left.PurposeRank.CompareTo(right.PurposeRank);
                    if (purpose != 0)
                        return purpose;
                }

                int spacing = left.Spacing.CompareTo(right.Spacing);
                return spacing != 0 ? spacing : left.SourceOrdinal.CompareTo(right.SourceOrdinal);
            });

            ulong layoutFingerprint = CalculateLayoutFingerprint(
                gi,
                _volumeCandidates,
                structuredGatherAvailable);
            if (_cachedLayoutReport == null || _cachedLayoutFingerprint != layoutFingerprint)
            {
                var layoutRequests = new SimpleDdgiLayoutVolumeRequest[_volumeCandidates.Count];
                for (int i = 0; i < _volumeCandidates.Count; i++)
                {
                    VolumeCandidate candidate = _volumeCandidates[i];
                    layoutRequests[i] = new SimpleDdgiLayoutVolumeRequest(
                        Id: candidate.Kind == VolumeKindAuthored
                            ? $"authored-{candidate.SourceOrdinal}"
                            : candidate.Kind == VolumeKindRing
                                ? $"ring-{candidate.SourceOrdinal - 10_000}"
                                : "legacy",
                        SourceOrdinal: candidate.SourceOrdinal,
                        IsAuthored: candidate.Kind == VolumeKindAuthored,
                        Purpose: candidate.Purpose,
                        Priority: candidate.Priority,
                        Spacing: candidate.Spacing,
                        ProbeCount: candidate.ProbeCount)
                    {
                        GridCountX = candidate.CountX,
                        GridCountY = candidate.CountY,
                        GridCountZ = candidate.CountZ,
                        // The first camera-relative ring is the only sparse
                        // authority in the topology-identical production
                        // slice. Authored, legacy, mid, and far volumes remain
                        // dense so a missing fine page always has a concrete
                        // coarser field to compose through.
                        SparseNearRingEligible =
                            candidate.Kind == VolumeKindRing &&
                            candidate.SourceOrdinal == 10_000,
                        DenseCoarserRingEligible =
                            candidate.Kind == VolumeKindRing &&
                            candidate.SourceOrdinal > 10_000,
                        RingIndex = candidate.Kind == VolumeKindRing
                            ? candidate.SourceOrdinal - 10_000
                            : -1,
                        ArchitecturalThickness =
                            gi.SimpleDdgiArchitecturalThicknessMeters,
                        MaximumTraceDistance = candidate.Spacing * Math.Max(
                            candidate.CountX,
                            Math.Max(candidate.CountY, candidate.CountZ))
                    };
                }

                SimpleDdgiProbeResidencyMode requestedResidencyMode =
                    ResolveProbeResidencyMode(
                        gi,
                        structuredGatherAvailable,
                        _volumeCandidates,
                        out string prerequisiteFallbackReason);

                bool residentPrivateTargets =
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident;
                int layoutReadbackBufferCount = !residentPrivateTargets &&
                    RequiresProbeStateReadback(
                        gi.SimpleDdgiClassificationReadbackEnabled,
                        gi.SimpleDdgiTransportV2Enabled)
                        ? RenderingConstants.FramesInFlight
                        : 0;
                _cachedLayoutReport = SimpleDdgiLayoutCompiler.Compile(
                    layoutRequests,
                    SimpleDdgiLayoutBudget.Resolve(gi),
                    gi.SimpleDdgiSampledAtlasEnabled,
                    gi.SimpleDdgiLayoutAdmissionMode,
                    // These allocations remain concrete across a live V1/V2
                    // switch so the immutable render graph remains valid.
                    transportV2Enabled: true,
                    transportRayCapacity: ResolveMaximumRingFullRays(gi),
                    configuredProbeUpdatesPerFrame:
                        gi.SimpleDdgiProbeUpdatesPerFrame,
                    lightingDirtyBoostEnabled:
                        gi.SimpleDdgiLightingDirtyBoostEnabled,
                    readbackBufferCount: layoutReadbackBufferCount,
                    residentPrivateTargets: residentPrivateTargets,
                    schedulerMode: _schedulerMode,
                    schedulerValidationEnabled:
                        _context.ValidationSettings.Mode !=
                            RendererValidationMode.Off,
                    residencyMode: requestedResidencyMode,
                    sparsePhysicalPageBudget:
                        gi.SimpleDdgiSparsePhysicalPageBudget,
                    sparseMinimumPhysicalPageBudget:
                        gi.SimpleDdgiSparseMinimumPhysicalPageBudget,
                    maximumPageAdmissionsPerFrame:
                        gi.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    storagePackingMode:
                        gi.SimpleDdgiStoragePackingMode,
                    sampledAtlasCoverageMode:
                        gi.SimpleDdgiSampledAtlasCoverageMode);

                if (!string.IsNullOrEmpty(prerequisiteFallbackReason) &&
                    string.IsNullOrEmpty(_cachedLayoutReport.ResidencyFallbackReason))
                {
                    _cachedLayoutReport = _cachedLayoutReport with
                    {
                        ResidencyFallbackReason = prerequisiteFallbackReason
                    };
                }

                _cachedAcceptedSourceOrdinals = _cachedLayoutReport.WasDegraded
                    ? new HashSet<int>(_cachedLayoutReport.AcceptedSourceOrdinals)
                    : null;
                _cachedLayoutFingerprint = layoutFingerprint;
            }

            _lastLayoutReport = _cachedLayoutReport;
            if (_lastLayoutReport.WasDegraded &&
                gi.SimpleDdgiLayoutAdmissionMode == SimpleDdgiLayoutAdmissionMode.Reject)
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI layout is invalid for {gi.DdgiQualityTier}: {_lastLayoutReport.Summary}.");
            }

            if (_lastLayoutReport.WasDegraded)
            {
                IReadOnlySet<int> acceptedSourceOrdinals = _cachedAcceptedSourceOrdinals ??
                    _lastLayoutReport.AcceptedSourceOrdinals;
                _volumeCandidates.RemoveAll(candidate => !acceptedSourceOrdinals.Contains(candidate.SourceOrdinal));
            }
            _lastBudgetWarning = _lastLayoutReport.WasDegraded
                ? $"simple-ddgi-layout-degraded:{_lastLayoutReport.Summary}"
                : string.Empty;
            int firstProbe = 0;
            _volumeCount = Math.Min(_volumeCandidates.Count, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
            for (int i = 0; i < _volumeCount; i++)
            {
                VolumeCandidate candidate = _volumeCandidates[i];
                candidate.FirstProbeIndex = firstProbe;
                if (gi.SimpleDdgiToroidalScrollingEnabled)
                {
                    GPUSimpleDdgiVolume provisional = candidate.ToGpuVolume();
                    if (TryGetPreviousMatchingVolume(i, provisional, out GPUSimpleDdgiVolume previous) &&
                        TryResolveCellDelta(previous, provisional, out int deltaX, out int deltaY, out int deltaZ))
                    {
                        // TryResolveCellDelta is old-origin minus new-origin.
                        // Advancing the logical origin by one cell therefore
                        // advances the physical offset by one slot as well.
                        candidate.PhysicalOffsetX = PositiveModulo(PhysicalOffsetX(previous) - deltaX, candidate.CountX);
                        candidate.PhysicalOffsetY = PositiveModulo(PhysicalOffsetY(previous) - deltaY, candidate.CountY);
                        candidate.PhysicalOffsetZ = PositiveModulo(PhysicalOffsetZ(previous) - deltaZ, candidate.CountZ);
                    }
                }
                firstProbe += candidate.ProbeCount;
                _volumeScratch[i] = candidate.ToGpuVolume();
                _volumePurposes[i] = candidate.Purpose;
                _volumePriorities[i] = candidate.Priority;
            }

            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                int outerPriority = Kind(volume) == VolumeKindRing
                    ? -Math.Max(0, ResolveVolumeQuality(i).RingIndex)
                    : 0;
                int fallbackPriority = Kind(volume) == VolumeKindAuthored
                    ? Math.Max(0, _volumePriorities[i])
                    : 0;
                _transportVolumeOrderKeys[i] = new SimpleDdgiTransportVolumeOrderKey(
                    i,
                    Math.Max(0.001f, Spacing(volume)),
                    fallbackPriority,
                    outerPriority);
            }
            SimpleDdgiTransportSolveController.OrderVolumes(
                _transportVolumeOrderKeys.AsSpan(0, _volumeCount),
                _transportVolumeOrder.AsSpan(0, _volumeCount));

            _probeCount = firstProbe;
            if (_volumeCount > 0)
            {
                GPUSimpleDdgiVolume first = _volumeScratch[0];
                _probeCountX = Math.Max(1, (int)MathF.Round(first.GridCountsAndFirstProbe.X));
                _probeCountY = Math.Max(1, (int)MathF.Round(first.GridCountsAndFirstProbe.Y));
                _probeCountZ = Math.Max(1, (int)MathF.Round(first.GridCountsAndFirstProbe.Z));
                _gridOrigin = new Vector3(first.OriginAndSpacing.X, first.OriginAndSpacing.Y, first.OriginAndSpacing.Z);
            }
            else
            {
                _probeCountX = 0;
                _probeCountY = 0;
                _probeCountZ = 0;
                _gridOrigin = default;
            }

            BuildVolumePagingTable();
            _storageLayout = _lastLayoutReport?.StorageLayout ??
                SimpleDdgiStorageLayout.Empty(
                    gi.SimpleDdgiStoragePackingMode);
            _sampledAtlasLayout = _lastLayoutReport?.SampledAtlasLayout ??
                SimpleDdgiSampledAtlasLayout.Disabled(
                    gi.SimpleDdgiSampledAtlasCoverageMode,
                    "layout-unavailable");
            ApplyCompiledStorageAndMirrorLayout();
        }

        private void BuildVolumePagingTable()
        {
            Array.Clear(_volumePagingScratch);
            SimpleDdgiMemoryPlan plan =
                _lastLayoutReport?.AcceptedMemoryPlan ??
                SimpleDdgiMemoryPlan.Empty;
            SimpleDdgiProbeResidencyMode globalMode =
                plan.VirtualProbeCount == _probeCount
                    ? plan.ResidencyMode.Sanitize()
                    : SimpleDdgiProbeResidencyMode.Dense;
            int pageTableCursor = 0;
            int densePhysicalCursor = 0;

            for (int volumeIndex = 0;
                volumeIndex < _volumeCount;
                volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                int virtualFirst = FirstProbe(volume);
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                int probeCount = VolumeProbeCount(volume);
                bool nearRingEligible =
                    Kind(volume) == VolumeKindRing &&
                    SourceOrdinal(volume) == 10_000;
                SimpleDdgiProbeResidencyMode volumeMode =
                    nearRingEligible && globalMode.CollectsDemand()
                        ? globalMode
                        : SimpleDdgiProbeResidencyMode.Dense;
                int pageGridX = SimpleDdgiProbePageLayout.CeilDivide(
                    countX,
                    SimpleDdgiProbePageLayout.PageDimensionX);
                int pageGridY = SimpleDdgiProbePageLayout.CeilDivide(
                    countY,
                    SimpleDdgiProbePageLayout.PageDimensionY);
                int pageGridZ = SimpleDdgiProbePageLayout.CeilDivide(
                    countZ,
                    SimpleDdgiProbePageLayout.PageDimensionZ);

                int densePhysicalFirst;
                if (globalMode.UsesSparsePayloads())
                {
                    densePhysicalFirst = nearRingEligible
                        ? 0
                        : densePhysicalCursor;
                    if (!nearRingEligible)
                        densePhysicalCursor = checked(
                            densePhysicalCursor + probeCount);
                }
                else
                {
                    // Dense and Shadow are the strict identity oracle.
                    densePhysicalFirst = virtualFirst;
                }

                _volumePagingScratch[volumeIndex] =
                    new GPUSimpleDdgiVolumePaging
                    {
                        VirtualFirstProbe = checked((uint)virtualFirst),
                        PageTableFirst = checked((uint)pageTableCursor),
                        DensePhysicalFirstProbe =
                            checked((uint)densePhysicalFirst),
                        ResidencyMode = checked((uint)volumeMode),
                        PageGridX = checked((uint)pageGridX),
                        PageGridY = checked((uint)pageGridY),
                        PageGridZ = checked((uint)pageGridZ),
                        SparsePoolFirstProbe = checked((uint)Math.Max(
                            0,
                            plan.DensePayloadProbeCount))
                    };

                if (nearRingEligible && globalMode.CollectsDemand())
                {
                    pageTableCursor = checked(
                        pageTableCursor +
                        pageGridX * pageGridY * pageGridZ);
                }
            }

            if (globalMode.CollectsDemand() &&
                pageTableCursor != plan.SparseVirtualPageCount)
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI volume paging table describes {pageTableCursor} pages; admitted layout requires {plan.SparseVirtualPageCount}.");
            }
            if (globalMode.UsesSparsePayloads() &&
                densePhysicalCursor != plan.DensePayloadProbeCount)
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI dense fallback spans {densePhysicalCursor} probes; admitted layout requires {plan.DensePayloadProbeCount}.");
            }
        }

        private void ApplyCompiledStorageAndMirrorLayout()
        {
            if (_storageLayout.Regions.Count != _volumeCount)
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI storage layout contains {_storageLayout.Regions.Count} regions for {_volumeCount} admitted volumes.");
            }

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                SimpleDdgiTransportCacheRegion region =
                    _storageLayout.FindVolume(volumeIndex) ??
                    throw new InvalidOperationException(
                        $"Simple-DDGI storage layout is missing volume {volumeIndex}.");
                GPUSimpleDdgiVolumePaging paging = _volumePagingScratch[volumeIndex];
                bool sparse = ((SimpleDdgiProbeResidencyMode)paging.ResidencyMode)
                    .Sanitize()
                    .UsesSparsePayloads();
                int expectedPhysicalFirst = checked((int)(sparse
                    ? paging.SparsePoolFirstProbe
                    : paging.DensePhysicalFirstProbe));
                if (region.PhysicalFirstProbe != expectedPhysicalFirst)
                {
                    throw new InvalidOperationException(
                        $"Simple-DDGI cache region {volumeIndex} begins at physical probe {region.PhysicalFirstProbe}; paging requires {expectedPhysicalFirst}.");
                }

                SimpleDdgiSampledAtlasRange? mirror =
                    _sampledAtlasLayout.FindVolume(volumeIndex);
                uint compactFirstLayerPlusOne = mirror is { } range
                    ? checked((uint)range.CompactFirstLayer + 1u)
                    : 0u;
                uint cacheFlags = SimpleDdgiStorageLayoutCompiler.PackVolumeFlags(
                    region.Format,
                    irradianceMirrorPresent: mirror.HasValue,
                    visibilityMirrorPresent: mirror.HasValue,
                    _storageLayout.AbiVersion,
                    _storageLayout.DirectionCodebookVersion);
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                volume.CacheLayout = new Vector4(
                    PackHeaderWord(checked((uint)region.BaseWord)),
                    PackHeaderWord(checked((uint)region.StrideWords)),
                    PackHeaderWord(compactFirstLayerPlusOne),
                    PackHeaderWord(cacheFlags));
                _volumeScratch[volumeIndex] = volume;
            }
        }

        private GPUSimpleDdgiResidencyHeader CreateResidencyHeader(
            GlobalIlluminationSettings settings)
        {
            SimpleDdgiMemoryPlan plan = _capacityPlan;
            uint flags = 0u;
            if (plan.ResidencyMode == SimpleDdgiProbeResidencyMode.Shadow)
                flags |= 1u << 0;
            if (plan.ResidencyMode.UsesSparsePayloads())
                flags |= 1u << 1;
            if (_context.ValidationSettings.Mode != RendererValidationMode.Off)
                flags |= 1u << 2;

            return new GPUSimpleDdgiResidencyHeader
            {
                FrameSerialLow = unchecked((uint)_frameSerial),
                FrameSerialHigh = unchecked((uint)(_frameSerial >> 32)),
                ResidencyResourceGeneration =
                    _probePageCache.ResourceGeneration,
                MappingGenerationCounter = 0u,
                ResidencyMode = checked((uint)plan.ResidencyMode),
                VirtualProbeCount = checked((uint)Math.Max(
                    0,
                    plan.VirtualProbeCount)),
                VirtualPageCount = checked((uint)Math.Max(
                    0,
                    plan.SparseVirtualPageCount)),
                DensePhysicalProbeCount = checked((uint)Math.Max(
                    0,
                    plan.DensePayloadProbeCount)),
                SparsePhysicalPageCapacity = checked((uint)Math.Max(
                    0,
                    plan.SparsePhysicalPageCapacity)),
                PhysicalProbeCapacity = checked((uint)Math.Max(
                    0,
                    plan.PhysicalProbeCapacity)),
                VolumeCount = checked((uint)Math.Max(0, _volumeCount)),
                RetentionFrames = checked((uint)
                    settings.SimpleDdgiSparseRetentionFrames),
                MaximumAdmissionsPerFrame = checked((uint)Math.Min(
                    settings.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    plan.SparsePhysicalPageCapacity)),
                MaximumReceiverFeedbackRequests = checked((uint)
                    settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests),
                InactiveRetryFrames = checked((uint)
                    settings.SimpleDdgiSparseInactiveRetryFrames),
                Flags = flags
            };
        }

        private SimpleDdgiProbeResidencyMode ResolveProbeResidencyMode(
            GlobalIlluminationSettings settings,
            bool structuredGatherAvailable,
            IReadOnlyList<VolumeCandidate> candidates,
            out string fallbackReason)
        {
            SimpleDdgiProbeResidencyMode requested =
                settings.SimpleDdgiProbeResidencyMode.Sanitize();
            fallbackReason = string.Empty;
            if (!requested.UsesSparsePayloads())
                return requested;

            if (!settings.SimpleDdgiStructuredGatherEnabled)
                fallbackReason = "structured-gather-disabled";
            else if (!structuredGatherAvailable)
                fallbackReason = "structured-gather-unavailable";
            else if (!settings.SimpleDdgiTransportV2Enabled)
                fallbackReason = "transport-v2-required";
            else if (!settings.SimpleDdgiToroidalScrollingEnabled)
                fallbackReason = "toroidal-addressing-required";
            else if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
            {
                bool frozenSparseTransaction =
                    settings.SimpleDdgiSchedulerMode.IsGpuMode() &&
                    _probePageCache.Frozen &&
                    (_capacityPlan.ResidencyMode.UsesSparsePayloads() ||
                     _probePageCache.Mode.UsesSparsePayloads());
                if (!frozenSparseTransaction)
                    fallbackReason = "gpu-resident-scheduler-required";
            }
            else
            {
                bool hasSparseNear = false;
                bool hasDenseCoarserRing = false;
                for (int index = 0; index < candidates.Count; index++)
                {
                    VolumeCandidate candidate = candidates[index];
                    if (candidate.Kind != VolumeKindRing)
                        continue;
                    hasSparseNear |= candidate.SourceOrdinal == 10_000;
                    hasDenseCoarserRing |= candidate.SourceOrdinal > 10_000;
                }

                if (!hasSparseNear || !hasDenseCoarserRing)
                    fallbackReason = "dense-coarser-ring-required";
            }

            return string.IsNullOrEmpty(fallbackReason)
                ? requested
                : SimpleDdgiProbeResidencyMode.Dense;
        }

        private static ulong CalculateLayoutFingerprint(
            GlobalIlluminationSettings settings,
            IReadOnlyList<VolumeCandidate> candidates,
            bool structuredGatherAvailable)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            hash = AddLayoutFingerprintValue(hash, (ulong)settings.DdgiQualityTier, prime);
            hash = AddLayoutFingerprintValue(hash, settings.DdgiAtlasMemoryBudgetBytes, prime);
            hash = AddLayoutFingerprintValue(hash, settings.SimpleDdgiSampledAtlasEnabled ? 1UL : 0UL, prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)settings.SimpleDdgiSampledAtlasCoverageMode.Sanitize(),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)settings.SimpleDdgiStoragePackingMode.Sanitize(),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                SimpleDdgiStorageLayoutCompiler.DirectionCodebookVersion,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                BitConverter.SingleToUInt32Bits(
                    settings.SimpleDdgiArchitecturalThicknessMeters),
                prime);
            hash = AddLayoutFingerprintValue(hash, settings.SimpleDdgiTransportV2Enabled ? 1UL : 0UL, prime);
            // Resident mode appends a private visibility target to the transport
            // atlas allocation. Keep the mode in the immutable layout key so a
            // CPU-to-resident transition cannot reuse a report sized for the
            // compute-only allocation.
            hash = AddLayoutFingerprintValue(hash, (ulong)settings.SimpleDdgiSchedulerMode.Sanitize(), prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)settings.SimpleDdgiProbeResidencyMode.Sanitize(),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparsePhysicalPageBudget,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparseMinimumPhysicalPageBudget,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparseRetentionFrames,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparseMaximumAdmissionsPerFrame,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparseMaximumReceiverFeedbackRequests,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiSparseInactiveRetryFrames,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiStructuredGatherEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                structuredGatherAvailable ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiToroidalScrollingEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiThinSurfaceTransmissionEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)ResolveMaximumRingFullRays(settings), prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiProbeUpdatesPerFrame,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiLightingDirtyBoostEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiClassificationReadbackEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)settings.SimpleDdgiLayoutAdmissionMode, prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)candidates.Count, prime);
            for (int i = 0; i < candidates.Count; i++)
            {
                VolumeCandidate candidate = candidates[i];
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.SourceOrdinal, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.Kind, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.Purpose, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.Priority, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.CountX, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.CountY, prime);
                hash = AddLayoutFingerprintValue(hash, (ulong)(uint)candidate.CountZ, prime);
                hash = AddLayoutFingerprintValue(
                    hash,
                    unchecked((uint)BitConverter.SingleToInt32Bits(candidate.Spacing)),
                    prime);
                hash = AddLayoutFingerprintVector(hash, candidate.Origin, prime);
                hash = AddLayoutFingerprintVector(hash, candidate.WorldMin, prime);
                hash = AddLayoutFingerprintVector(hash, candidate.WorldMax, prime);
                hash = AddLayoutFingerprintValue(
                    hash,
                    unchecked((uint)BitConverter.SingleToInt32Bits(candidate.EdgeFadeDistance)),
                    prime);
            }

            return hash;
        }

        /// <summary>
        /// Hashes only controls that alter the radiometric source estimator
        /// retained in the V2 cache. Layout changes are handled independently by
        /// the volume-table generation, and lighting/environment changes arrive
        /// through the renderer's scene signature.
        /// </summary>
        private static ulong CalculateTransportSourceCalibrationFingerprint(
            GlobalIlluminationSettings settings)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            hash = AddLayoutFingerprintValue(hash, (ulong)settings.DdgiQualityTier, prime);
            hash = AddLayoutFingerprintValue(hash, settings.DdgiAlphaMaskedTransportEnabled ? 1UL : 0UL, prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)(uint)settings.DdgiMaxShadedLights, prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)(uint)(settings.DdgiMaterialTextureMaxCascade + 1), prime);
            hash = AddLayoutFingerprintValue(hash, settings.FarFieldSunShadowEnabled ? 1UL : 0UL, prime);
            hash = AddLayoutFingerprintValue(hash, settings.FarFieldSkyVisibilityEnabled ? 1UL : 0UL, prime);

            hash = AddTransportRingSourceCalibrationValues(
                hash,
                settings.SimpleDdgiNearFullRaysPerProbe,
                settings.SimpleDdgiNearMaterialTextureMaxCascade,
                settings.SimpleDdgiNearMaxShadedLights,
                prime);
            hash = AddTransportRingSourceCalibrationValues(
                hash,
                settings.SimpleDdgiMidFullRaysPerProbe,
                settings.SimpleDdgiMidMaterialTextureMaxCascade,
                settings.SimpleDdgiMidMaxShadedLights,
                prime);
            return AddTransportRingSourceCalibrationValues(
                hash,
                settings.SimpleDdgiFarFullRaysPerProbe,
                settings.SimpleDdgiFarMaterialTextureMaxCascade,
                settings.SimpleDdgiFarMaxShadedLights,
                prime);
        }

        private static ulong AddTransportRingSourceCalibrationValues(
            ulong hash,
            int fullRays,
            int materialTextureMaxCascade,
            int maxShadedLights,
            ulong prime)
        {
            hash = AddLayoutFingerprintValue(hash, (ulong)(uint)fullRays, prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)(uint)(materialTextureMaxCascade + 1), prime);
            return AddLayoutFingerprintValue(hash, (ulong)(uint)maxShadedLights, prime);
        }

        /// <summary>
        /// Hashes knobs that change the V2 fixed-point operator or its retirement
        /// policy but leave direct/sky/emissive source rays valid. Keeping this
        /// separate from the source hash is what makes an albedo/relaxation
        /// adjustment cheap enough for production live tuning.
        /// </summary>
        private static ulong CalculateTransportSolverCalibrationFingerprint(
            GlobalIlluminationSettings settings)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiTransportSolverRelaxation)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiTransportAlbedoClamp)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiTransportTailRelativeTolerance)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiTransportAcceleratedSweepCount,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiStableMaintenanceUpdateCount,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiStableMaintenanceEmaThreshold)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiReducedBlendEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                settings.SimpleDdgiAdaptiveHysteresisEnabled ? 1UL : 0UL,
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiHysteresisChangeThreshold)),
                prime);
            return AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiHysteresisStepThreshold)),
                prime);
        }

        private static ulong AddLayoutFingerprintValue(ulong hash, ulong value, ulong prime) =>
            (hash ^ value) * prime;

        private static ulong AddLayoutFingerprintVector(ulong hash, Vector3 value, ulong prime)
        {
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(value.X)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(value.Y)),
                prime);
            return AddLayoutFingerprintValue(
                hash,
                unchecked((uint)BitConverter.SingleToInt32Bits(value.Z)),
                prime);
        }

        private static int EnforceProbeBudget(List<VolumeCandidate> volumes, int maxProbeCount)
        {
            int total = 0;
            for (int i = 0; i < volumes.Count; i++)
                total += volumes[i].ProbeCount;

            int droppedVolumeCount = 0;
            while (total > maxProbeCount)
            {
                int removeIndex = -1;
                float coarsestSpacing = float.MinValue;
                for (int i = 0; i < volumes.Count; i++)
                {
                    if (volumes[i].Kind != VolumeKindRing || volumes[i].Spacing < coarsestSpacing)
                        continue;
                    coarsestSpacing = volumes[i].Spacing;
                    removeIndex = i;
                }

                if (removeIndex < 0)
                {
                    int newestAuthoredOrdinal = int.MinValue;
                    for (int i = 0; i < volumes.Count; i++)
                    {
                        if (volumes[i].Kind != VolumeKindAuthored || volumes[i].SourceOrdinal < newestAuthoredOrdinal)
                            continue;
                        newestAuthoredOrdinal = volumes[i].SourceOrdinal;
                        removeIndex = i;
                    }
                }

                if (removeIndex < 0)
                    break;

                total -= volumes[removeIndex].ProbeCount;
                volumes.RemoveAt(removeIndex);
                droppedVolumeCount++;
            }

            return droppedVolumeCount;
        }

        private bool TryCreateAuthoredVolume(SimpleDdgiAuthoredVolume authored, int ordinal, out VolumeCandidate candidate)
        {
            Vector3 min = Min(authored.Min, authored.Max);
            Vector3 max = Max(authored.Min, authored.Max);
            float spacing = Math.Clamp(authored.Spacing, 0.25f, 8.0f);
            Vector3 size = max - min;
            if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
            {
                candidate = default;
                return false;
            }

            Vector3 origin = ResolveAuthoredLatticeOrigin(min, spacing, authored.LatticePhase);
            int countX = ResolveAuthoredLatticeAxisCount(max.X, origin.X, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
            int countY = ResolveAuthoredLatticeAxisCount(max.Y, origin.Y, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
            int countZ = ResolveAuthoredLatticeAxisCount(max.Z, origin.Z, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
            float edgeFadeDistance = Math.Max(spacing * 1.5f, 0.001f);
            (Vector3 influenceMin, Vector3 influenceMax) = ResolveInfluenceBounds(
                min,
                max,
                edgeFadeDistance);
            candidate = new VolumeCandidate(
                VolumeKindAuthored,
                ordinal,
                authored.Priority,
                authored.Purpose,
                origin,
                spacing,
                countX,
                countY,
                countZ,
                influenceMin,
                influenceMax,
                edgeFadeDistance);
            return true;
        }

        private VolumeCandidate CreateRingVolume(GlobalIlluminationSettings gi, BoundingBox sceneBounds, Vector3 cameraPosition, int ringIndex)
        {
            float spacing = ResolveRingSpacing(gi, ringIndex);
            (int countX, int countY, int countZ) = ResolveRingGrid(gi, ringIndex);
            Vector3 latticeSize = LatticeSize(countX, countY, countZ, spacing);
            bool hadRingOrigin = _ringHasOrigins[ringIndex];
            Vector3 placementCamera = cameraPosition;
            float verticalHysteresisFraction = gi.SimpleDdgiVerticalRingPolicy switch
            {
                SimpleDdgiVerticalRingPolicy.CameraRelative => 0.0f,
                SimpleDdgiVerticalRingPolicy.ReceiverAnchored => 0.49f,
                _ => gi.SimpleDdgiVerticalRecenterHysteresisFraction
            };
            if (gi.SimpleDdgiVerticalRingPolicy == SimpleDdgiVerticalRingPolicy.ReceiverAnchored)
                placementCamera.Y = gi.SimpleDdgiReceiverVerticalAnchor;
            Vector3 origin = ResolveSceneClampedOrigin(
                sceneBounds.Min,
                sceneBounds.Max,
                latticeSize,
                spacing,
                placementCamera,
                _ringOrigins[ringIndex],
                ref _ringHasOrigins[ringIndex],
                out bool recentered,
                verticalHysteresisFraction);
            if (recentered && _ringRecenteredThisFrame && hadRingOrigin)
            {
                origin = _ringOrigins[ringIndex];
                recentered = false;
            }
            else if (recentered)
            {
                _ringRecenteredThisFrame = true;
            }

            _ringOrigins[ringIndex] = origin;
            _recenteredThisFrame |= recentered;
            float edgeFadeDistance = Math.Max(spacing * 1.5f, 0.001f);
            (Vector3 influenceMin, Vector3 influenceMax) = ResolveInfluenceBounds(
                origin,
                origin + latticeSize,
                edgeFadeDistance);
            return new VolumeCandidate(
                VolumeKindRing,
                10_000 + ringIndex,
                priority: int.MinValue,
                purpose: SimpleDdgiVolumePurpose.TransitionSupport,
                origin,
                spacing,
                countX,
                countY,
                countZ,
                influenceMin,
                influenceMax,
                edgeFadeDistance);
        }

        internal static (int X, int Y, int Z) ResolveRingGrid(GlobalIlluminationSettings settings, int ringIndex)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            return ringIndex switch
            {
                1 => (
                    settings.SimpleDdgiMidRingGridSizeX,
                    settings.SimpleDdgiMidRingGridSizeY,
                    settings.SimpleDdgiMidRingGridSizeZ),
                2 => (
                    settings.SimpleDdgiFarRingGridSizeX,
                    settings.SimpleDdgiFarRingGridSizeY,
                    settings.SimpleDdgiFarRingGridSizeZ),
                _ => (
                    settings.SimpleDdgiNearRingGridSizeX,
                    settings.SimpleDdgiNearRingGridSizeY,
                    settings.SimpleDdgiNearRingGridSizeZ)
            };
        }

        internal static float ResolveRingSpacing(GlobalIlluminationSettings settings, int ringIndex)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (ringIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(ringIndex));

            float spacing = settings.SimpleDdgiRingBaseSpacing *
                MathF.Pow(settings.SimpleDdgiRingSpacingMultiplier, ringIndex);
            if (!settings.SimpleDdgiTransportV2Enabled || !settings.SimpleDdgiAutomaticProbeDensityEnabled)
                return spacing;

            // Keep the fixed probe count and all far-field coverage guarantees,
            // but spend the close rings on real architectural resolution rather
            // than asking scenes for handcrafted volumes. The mid ring eases back
            // toward its nominal spacing and the far ring retains full coverage.
            float nearDensity = settings.SimpleDdgiAutomaticProbeDensityScale;
            float ringDensity = ringIndex switch
            {
                0 => nearDensity,
                1 => MathF.Sqrt(nearDensity),
                _ => 1.0f
            };
            return Math.Max(0.25f, spacing * ringDensity);
        }

        private VolumeCandidate CreateLegacyVolume(GlobalIlluminationSettings gi, BoundingBox sceneBounds, Vector3 cameraPosition)
        {
            Vector3 size = sceneBounds.Max - sceneBounds.Min;
            float spacing = gi.SimpleDdgiProbeSpacing;
            int countX = Math.Clamp((int)MathF.Ceiling(size.X / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
            int countY = Math.Clamp((int)MathF.Ceiling(size.Y / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
            int countZ = Math.Clamp((int)MathF.Ceiling(size.Z / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
            Vector3 latticeSize = LatticeSize(countX, countY, countZ, spacing);
            _gridOrigin = ResolveSceneClampedOrigin(sceneBounds.Min, sceneBounds.Max, latticeSize, spacing, cameraPosition, _gridOrigin, ref _hasGridOrigin, out _recenteredThisFrame);
            float edgeFadeDistance = Math.Max(spacing * 1.5f, 0.001f);
            (Vector3 influenceMin, Vector3 influenceMax) = ResolveInfluenceBounds(
                _gridOrigin,
                _gridOrigin + latticeSize,
                edgeFadeDistance);
            return new VolumeCandidate(
                VolumeKindLegacy,
                0,
                priority: int.MinValue,
                purpose: SimpleDdgiVolumePurpose.TransitionSupport,
                _gridOrigin,
                spacing,
                countX,
                countY,
                countZ,
                influenceMin,
                influenceMax,
                edgeFadeDistance);
        }

        private void AnnotateVolumeUpdateRanges()
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                volume.UpdateStartAndCount = new Vector4(
                    0.0f,
                    0.0f,
                    ResolveVolumeQuality(i).FullRays,
                    0.0f);
                _volumeScratch[i] = volume;
            }

            if (_volumeCount == 0 || _probesToUpdate <= 0 || _probeCount <= 0)
                return;

            Span<int> firstQueueOffset = stackalloc int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
            Span<int> updatedCounts = stackalloc int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
            firstQueueOffset.Fill(-1);

            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int volumeIndex = checked((int)_updateQueueScratch[queueOffset].VolumeIndex);
                if ((uint)volumeIndex >= (uint)_volumeCount)
                    continue;

                if (firstQueueOffset[volumeIndex] < 0)
                    firstQueueOffset[volumeIndex] = queueOffset;
                updatedCounts[volumeIndex]++;
            }

            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                volume.UpdateStartAndCount = new Vector4(
                    Math.Max(firstQueueOffset[i], 0),
                    updatedCounts[i],
                    ResolveVolumeQuality(i).FullRays,
                    0.0f);
                _volumeScratch[i] = volume;
            }
        }

        private void UploadParams(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            UploadBarrierDescription barrier = new(
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit);
            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                _lastParams,
                new ReadOnlySpan<GPUSimpleDdgiVolume>(_volumeScratch, 0, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                barrierDescription: barrier);
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                new ReadOnlySpan<GPUSimpleDdgiVolumePaging>(
                    _volumePagingScratch,
                    0,
                    GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                ParamsSize +
                    VolumeStride *
                        GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount,
                barrier);
        }

        private void UploadGpuResidentFrame(
            GlobalIlluminationSettings gi,
            Vector3 cameraPosition,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            uint dirtyReasonFlags,
            bool structuredGatherAvailable,
            bool farFieldCoverageAvailable,
            bool cohortLightingTransition,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            bool residentBootstrap,
            bool volumeTableRemapped)
        {
            // A resident transition is a transaction boundary.  The CPU keeps
            // the current topology/policy state, but it never creates a queue
            // or walks the probe pool to decide what the GPU should do.
            if (_recenteredThisFrame && !gi.SimpleDdgiToroidalScrollingEnabled)
            {
                _atlasClearRequired = true;
                _atlasFresh = true;
            }

            PreserveToroidalAtlasData();
            ClearAtlasBuffersIfRequired(commandBuffer);
            SynchronizeSampledAtlasIfRequired(commandBuffer);
            UploadReceiverProbeInvalidations(stagingRing, commandBuffer);

            int configuredBudget = Math.Clamp(_schedulerConfiguredRequestBudget, 0, _probeCount);
            int effectiveBudget = ResolveTransportV2FrameRequestBudget(
                configuredBudget,
                _schedulerSourceRequestBudget,
                configuredBudget,
                TransportV2Active,
                TransportAccelerationSolveActive);
            if (!_schedulerDeterministicFixedBudget && _schedulerFeedbackRequestBudgetCap > 0)
                effectiveBudget = Math.Min(effectiveBudget, _schedulerFeedbackRequestBudgetCap);
            _schedulerEffectiveRequestBudget = effectiveBudget;
            _schedulerPressureReason = SimpleDdgiSchedulerPressureReason.None;
            ResolveGpuResidentSourceThroughputTarget();

            if (TailCertificationEnabled && TransportV2Active)
            {
                if (!_gpuSchedulerFeedbackValid)
                {
                    // Before the first delayed feedback packet arrives, use a
                    // conservative bootstrap witness. It may keep the solve
                    // in SourceRepair for an extra frame, but it can never
                    // certify an incomplete resident field.
                    _transportResidentParticipantCount = Math.Max(0, _probeCount);
                    _transportResidentSourceRepairProbeCount = Math.Max(0, _probeCount);
                }
                PrepareTailSolveController();
            }
            TrackTransportTailProgress();

            BeginUpdateTransaction(hasWork: false);
            _updateStartProbe = 0;
            _probesToUpdate = 0;
            _rayDispatchBatchCount = 0;

            float environmentIntensity = _settings.Environment.Enabled
                ? _settings.Environment.SkyIntensity
                : 0.0f;
            float hysteresis = gi.SimpleDdgiTransportV2Enabled
                ? 1.0f - gi.SimpleDdgiTransportSolverRelaxation
                : gi.SimpleDdgiHysteresis;
            GPUSimpleDdgiVolume firstVolume = _volumeCount > 0 ? _volumeScratch[0] : default;
            _lastParams = new GPUSimpleDdgiParams
            {
                GridOriginAndSpacing = firstVolume.OriginAndSpacing,
                GridCountsAndProbeCount = new Vector4(
                    firstVolume.GridCountsAndFirstProbe.X,
                    firstVolume.GridCountsAndFirstProbe.Y,
                    firstVolume.GridCountsAndFirstProbe.Z,
                    _probeCount),
                AtlasTexelsAndRayCount = new Vector4(
                    IrradianceTexelsPerProbe,
                    VisibilityTexelsPerProbe,
                    _raysPerProbe,
                    gi.FarFieldClipmapResolution),
                HysteresisFrameAndFlags = new Vector4(
                    hysteresis,
                    PackHeaderWord(_frameIndex),
                    PackHeaderWord(BuildFlags(
                        gi,
                        gi.EffectiveUseDdgi,
                        structuredGatherAvailable,
                        farFieldCoverageAvailable)),
                    gi.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = new Vector4(
                    _settings.Environment.TransportFallbackRadiance.X,
                    _settings.Environment.TransportFallbackRadiance.Y,
                    _settings.Environment.TransportFallbackRadiance.Z,
                    environmentIntensity),
                // A zero range is deliberate.  GPU consumers use the resident
                // queue/bucket ABI and must not observe a prior CPU range.
                ProbeUpdateRange = new Vector4(
                    0.0f,
                    0.0f,
                    _volumeCount,
                    gi.EnvironmentFallbackIntensity),
                DebugAndBias = new Vector4(
                    ResolveSimpleDdgiDebugViewMode(gi.DebugView),
                    gi.DdgiSelfShadowBiasScale,
                    gi.IndirectIntensity,
                    gi.FarFieldMaxTraceSteps),
                RotationQuaternion = BuildFrameRotation(_frameIndex),
                BiasAndPadding = new Vector4(
                    gi.SimpleDdgiNormalBias,
                    gi.SimpleDdgiViewBias,
                    gi.SimpleDdgiHysteresisChangeThreshold,
                    gi.SimpleDdgiHysteresisStepThreshold),
                Reserved0 = new Vector4(
                    _volumeCount,
                    SampledAtlasActive ? _sampledAtlas!.LayersPerTexture : 0,
                    SampledAtlasActive ? _sampledAtlas!.GroupCount : 0,
                    SampledAtlasActive ? 1.0f : 0.0f),
                BiasLimitsAndPadding = new Vector4(
                    gi.SimpleDdgiMaximumWorldBiasMeters,
                    gi.SimpleDdgiArchitecturalThicknessMeters,
                    gi.DdgiThinWallPolicyEnabled
                        ? gi.DdgiThinWallLeakClampStrength
                        : 0.0f,
                    SampledAtlasActive ? _sampledAtlas!.ProbeCapacity : 0),
                TransportAndAtlasIndices = new Vector4(
                    PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    PackHeaderWord(gi.SimpleDdgiTransportV2Enabled
                        ? (uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer
                        : (uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    PackHeaderWord(gi.SimpleDdgiTransportV2Enabled
                        ? (uint)BindlessIndex.SimpleDdgiTransportSourceCacheBuffer
                        : 0u),
                    PackHeaderWord(_transportGeneration)),
                TransportControls = new Vector4(
                    gi.SimpleDdgiTransportSolverRelaxation,
                    gi.SimpleDdgiTransportAlbedoClamp,
                    gi.SimpleDdgiTransportTailRelativeTolerance,
                    gi.SimpleDdgiTransportAcceleratedSweepCount),
                ResidencyAndCounts = BuildResidencyAndCounts(),
                ResidencyControls = BuildResidencyControls()
            };
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                volume.UpdateStartAndCount = new Vector4(
                    0.0f,
                    0.0f,
                    ResolveVolumeQuality(volumeIndex).FullRays,
                    0.0f);
                _volumeScratch[volumeIndex] = volume;
            }

            UploadParams(stagingRing, commandBuffer);
            _controlHeaderInitialized = true;
            _wasSimpleDdgiEnabled = true;
            // Resident execution keeps the public probe-state buffer GPU-owned
            // after activation. A toroidal remap can resize CPU mirrors or mark
            // affected slots fresh, but uploading those stale CPU mirrors would
            // overwrite committed GPU lifecycle state. Only the distinct
            // activation/arena bootstrap may seed public state from the CPU.
            if ((_probeStateUploadRequired &&
                    (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident || residentBootstrap)) ||
                (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    _probeStateDirtySlots.Count > 0))
            {
                UploadProbeState(stagingRing, commandBuffer);
            }

            if (residentBootstrap || !_gpuResidentProbeStateBootstrapped)
            {
                _gpuResidentProbeStateBootstrapped =
                    UploadGpuResidentSchedulerBootstrap(stagingRing, commandBuffer);
            }

            UploadGpuSchedulerFrame(
                gi,
                cameraPosition,
                dirtyRegions,
                dirtyReasonFlags,
                structuredGatherAvailable,
                farFieldCoverageAvailable,
                cohortLightingTransition,
                stagingRing,
                commandBuffer);

            // The render graph must not expose schedule/consumer passes until
            // the private scheduler-state transfer has actually been recorded.
            _gpuSchedulerFrameExecutionAvailable =
                !_gpuSchedulerFallbackLatched &&
                structuredGatherAvailable &&
                _gpuResidentProbeStateBootstrapped;

            if (_atlasClearedThisFrame)
            {
                _totalAtlasClearCount++;
                _framesSinceLastClear = 0;
            }
            else if (_framesSinceLastClear != int.MaxValue)
            {
                _framesSinceLastClear++;
            }
            if (!_recenteredThisFrame && _framesSinceLastRecenter != int.MaxValue)
                _framesSinceLastRecenter++;
        }

        private BoundingBox ResolveSceneBounds(
            Scene scene,
            ulong sceneContentRevision)
        {
            bool sameScene = ReferenceEquals(scene, _sceneBoundsScene);
            if (SimpleDdgiSceneBounds.ShouldRefreshSnapshot(
                    _hasSceneBoundsSnapshot,
                    sameScene,
                    _sceneBoundsSceneContentRevision,
                    sceneContentRevision))
            {
                _sceneBoundsSnapshot = SimpleDdgiSceneBounds.Estimate(scene);
                _sceneBoundsScene = scene;
                _sceneBoundsSceneContentRevision = sceneContentRevision;
                _hasSceneBoundsSnapshot = true;
            }

            return _sceneBoundsSnapshot;
        }

        private void ResolveGpuResidentSourceThroughputTarget()
        {
            if (!TransportV2Active || _probeCount <= 0)
            {
                _sourceRefreshTargetProbeCount = 0;
                _sourceRefreshCapacityShortfall = 0;
                _sourceRefreshTargetRayCount = 0;
                _sourceRefreshRayCapacityShortfall = 0;
                return;
            }

            // Use the authored source-sweep target, not the extended
            // start-to-start cadence that reserves solve/audit time. See
            // ResolveEffectiveTransportSourceRefreshFrames.
            int targetFrames = Math.Max(1, AuthoredTransportSourceSweepFrames);
            int participatingProbeCount = _probeCount;
            _sourceRefreshTargetProbeCount = (int)Math.Min(
                int.MaxValue,
                ((long)participatingProbeCount + targetFrames - 1L) / targetFrames);
            _sourceRefreshCapacityShortfall = Math.Max(
                0,
                _sourceRefreshTargetProbeCount - Math.Max(_schedulerEffectiveRequestBudget, 0));

            Span<SimpleDdgiRayTier> tiers = stackalloc SimpleDdgiRayTier[
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
            int tierCount = 0;
            for (int volumeIndex = 0;
                 volumeIndex < _volumeCount && tierCount < tiers.Length;
                 volumeIndex++)
            {
                int volumeProbeCount = VolumeProbeCount(_volumeScratch[volumeIndex]);
                if (volumeProbeCount <= 0)
                    continue;
                tiers[tierCount++] = new SimpleDdgiRayTier(
                    volumeProbeCount,
                    Math.Max(1, ResolveVolumeQuality(volumeIndex).FullRays));
            }

            ulong admittedRayBudget = (ulong)Math.Max(
                _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget,
                0);
            SimpleDdgiRayCapacityResult result = SimpleDdgiRayCapacityPlanner.Evaluate(
                tiers[..tierCount],
                targetFrames,
                admittedRayBudget,
                ResolveSourceSweepFramesPerSecond(
                    _schedulerDeterministicFixedBudget,
                    _sourceSweepFramesPerSecond));
            _sourceRefreshTargetRayCount = result.TargetRaysPerFrame;
            _sourceRefreshRayCapacityShortfall = result.CapacityShortfall;
            _sourceRefreshMinimumSweepSeconds = result.MinimumAchievableSweepSeconds;
        }

        private void UploadGpuSchedulerFrame(
            GlobalIlluminationSettings gi,
            Vector3 cameraPosition,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            uint dirtyReasonFlags,
            bool structuredGatherAvailable,
            bool farFieldCoverageAvailable,
            bool cohortLightingTransition,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (!_schedulerMode.IsGpuMode() || !_gpuScheduler.IsReady || _gpuScheduler.Layout == null)
                return;

            SimpleDdgiGpuSchedulerLayout layout = _gpuScheduler.Layout;
            int dirtyCount = BuildGpuDirtyRegions(dirtyRegions);
            bool dirtyOverflow = dirtyRegions != null && dirtyRegions.Count > dirtyCount;
            bool fallbackQuiesced =
                _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                _gpuSchedulerFallbackLatched;
            if (fallbackQuiesced)
            {
                dirtyCount = 0;
                dirtyOverflow = false;
            }
            uint featureFlags = 0u;
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureGpuResident;
            else
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureGpuMirror;
            if (TransportV2Active)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureTransportV2;
            if (gi.SimpleDdgiToroidalScrollingEnabled)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureToroidalScrolling;
            if (_atlasFresh)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureAtlasFresh;
            if (TransportGlobalConvergencePending)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureGlobalConvergence;
            if (dirtyOverflow)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureDirtyOverflow;
            if (gi.SimpleDdgiClassificationSchedulingEnabled)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureClassification;
            if (SampledAtlasGpuPublicationRequired)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureSampledPublication;
            if (TransportV2Active && TailCertificationEnabled)
                featureFlags |= SimpleDdgiSchedulerAbi.SchedulerFeatureTransportTailCertification;
            if (TransportV2Active &&
                TailCertificationEnabled &&
                _transportPeriodicSourceRefreshWavePending)
            {
                featureFlags |=
                    SimpleDdgiSchedulerAbi.SchedulerFeaturePeriodicSourceRefreshWave;
            }

            Span<uint> rayBuckets = stackalloc uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
            int rayBucketCount = 0;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                SimpleDdgiRingQuality quality = ResolveVolumeQuality(volumeIndex);
                rayBucketCount = AddGpuRayBucket(rayBuckets, rayBucketCount, quality.FullRays);
                rayBucketCount = AddGpuRayBucket(rayBuckets, rayBucketCount, quality.MaintenanceRays);
            }
            if (rayBucketCount == 0)
                rayBucketCount = AddGpuRayBucket(rayBuckets, rayBucketCount, Math.Max(1, _raysPerProbe));
            for (int bucket = rayBucketCount; bucket < rayBuckets.Length; bucket++)
                rayBuckets[bucket] = 0u;

            int requestedBudget = layout.RequestCapacity;
            uint configuredBudget = ClampToUint(_schedulerConfiguredRequestBudget);
            uint effectiveBudget = ClampToUint(Math.Clamp(_schedulerEffectiveRequestBudget, 0, requestedBudget));
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                // The CPU reference queue is not the resident authority. Keep
                // the authored cap as a hard ceiling, but preserve the delayed
                // timing-feedback cap computed for the resident scheduler. A
                // resident frame must never silently recover to the authored
                // cap just because no CPU queue was built.
                effectiveBudget = Math.Min(
                    effectiveBudget,
                    configuredBudget > (uint)requestedBudget
                        ? (uint)requestedBudget
                        : configuredBudget);
            }
            if (fallbackQuiesced)
            {
                configuredBudget = 0u;
                effectiveBudget = 0u;
            }

            uint sourceTargetRays = fallbackQuiesced
                ? 0u
                : ClampToUint(_sourceRefreshTargetRayCount);
            uint dirtyReasons = dirtyReasonFlags | _activeDirtyReasonFlags | _regionalDirtyReasonFlags;
            if (dirtyOverflow)
                dirtyReasons |= uint.MaxValue;
            if (ShouldQuiesceCertifiedResidentMaintenance(
                    HasCurrentTransportTailCertificate,
                    _transportPeriodicSourceRefreshWavePending,
                    _transportSolveDrainPending,
                    _recenteredThisFrame,
                    cohortLightingTransition,
                    dirtyReasons,
                    _frameIndex,
                    CertifiedMaintenancePulseFrames))
            {
                // Keep the authored/configured capacity visible in policy
                // diagnostics. Only this frame's resident admission envelope
                // is empty; the next invalidation or deterministic pulse opens
                // it immediately without reallocating scheduler resources.
                effectiveBudget = 0u;
                featureFlags |=
                    SimpleDdgiSchedulerAbi.SchedulerFeatureCertifiedQuiesced;
            }
            GPUSimpleDdgiSchedulerFrame frame = new()
            {
                ActiveProbeCount = fallbackQuiesced
                    ? 0u
                    : ClampToUint(_probeCount),
                ActiveVolumeCount = fallbackQuiesced
                    ? 0u
                    : ClampToUint(_volumeCount),
                CandidateCapacity = ClampToUint(layout.ActiveProbeCount),
                RequestCapacity = ClampToUint(requestedBudget),
                ConfiguredRequestBudget = configuredBudget,
                EffectiveRequestBudget = effectiveBudget,
                PrimaryRayBudget = fallbackQuiesced
                    ? 0u
                    : ClampToUint(Math.Max(0, gi.DdgiProbeUpdatePrimaryRayBudget)),
                SourceThroughputProbeTarget = fallbackQuiesced
                    ? 0u
                    : ClampToUint(_sourceRefreshTargetProbeCount),
                SourceThroughputRayTarget = sourceTargetRays,
                SourceThroughputRayCapacity = fallbackQuiesced
                    ? 0u
                    : ClampToUint(SaturatingAdd(
                        _sourceRefreshTargetRayCount,
                        _sourceRefreshRayCapacityShortfall)),
                FrameIndex = _frameIndex,
                PeriodicSourceRefreshControlFrame =
                    _transportPeriodicSourceRefreshWavePending
                        ? _transportPeriodicSourceRefreshWaveCutoffFrame
                        : _transportNextPeriodicSourceRefreshFrame,
                FrameSerialLow = ClampToUint(_frameSerial),
                FrameSerialHigh = ClampToUint(_frameSerial >> 32),
                VolumeTableGeneration = _volumeTableGeneration,
                SchedulerResourceGeneration = _gpuScheduler.ResourceGeneration,
                // A resident frame has no CPU-authored queue transaction. The
                // scheduler arena/resource generation is its queue epoch and
                // stays stable while a frozen audit spans multiple frames;
                // using the per-frame CPU serial here would make the audit's
                // immutable queue witness change even though no queue work is
                // allowed during AuditFrozen.
                QueueTransactionGeneration = _schedulerMode ==
                    SimpleDdgiSchedulerMode.GpuResident
                    ? _gpuScheduler.ResourceGeneration
                    : _updateTransactionSerial,
                SourceLightingGeneration = _sourceLightingGeneration,
                TransportGeneration = _transportGeneration,
                // Word 19 is the active resident solve-epoch witness. Keep it
                // zero in source-repair/certified/tracking phases so periodic
                // source replacement remains live there; while solving or
                // auditing, a nonzero value both stamps cached commits and
                // prevents the source-age watchdog from replacing their cache
                // before the complete visit reduction can be observed.
                GlobalConvergenceGeneration =
                    (!_transportSolveDrainPending &&
                     _transportSolveController.Phase ==
                        SimpleDdgiTransportPhase.AcceleratedSolve) ||
                    _transportSolveController.Phase == SimpleDdgiTransportPhase.AuditFrozen
                        ? _transportSolveController.SolveEpoch
                        : 0u,
                CameraPositionAndNearProximity = new Vector4(
                    cameraPosition.X,
                    cameraPosition.Y,
                    cameraPosition.Z,
                    Math.Max(0.0f, gi.SimpleDdgiRingBaseSpacing * 2.0f)),
                DirtyRegionCount = ClampToUint(dirtyCount),
                DirtyRegionCapacity = ClampToUint(layout.DirtyRegionCapacity),
                DirtyReasonFlags = dirtyReasons,
                FeatureFlags = featureFlags,
                ClassificationRetryFrames = InactiveProbeRetryFrames,
                SourceRefreshIntervalFrames = ClampToUint(EffectiveTransportSourceRefreshFrames),
                StableGenerationRequirement = ClampToUint(gi.SimpleDdgiStableMaintenanceUpdateCount),
                SourceEpoch = _sourceEpochGeneration,
                RayBucket0 = rayBuckets[0],
                RayBucket1 = rayBuckets[1],
                RayBucket2 = rayBuckets[2],
                RayBucket3 = rayBuckets[3],
                RayBucket4 = rayBuckets[4],
                RayBucket5 = rayBuckets[5],
                InvalidationMarkerGeneration = _currentProbeInvalidationMarkerSerial,
                Reserved0 = BitConverter.SingleToUInt32Bits(Math.Clamp(
                    gi.SimpleDdgiTransportTailRelativeTolerance,
                    0.0f,
                    1.0f))
            };

            BuildGpuVolumePolicies(_gpuVolumePolicyScratch, _gpuPreviousVolumePolicyScratch);
            _gpuScheduler.UploadFrame(
                stagingRing,
                commandBuffer,
                frame,
                new ReadOnlySpan<GPUSimpleDdgiSchedulerVolumePolicy>(
                    _gpuVolumePolicyScratch, 0, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                new ReadOnlySpan<GPUSimpleDdgiSchedulerVolumePolicy>(
                    _gpuPreviousVolumePolicyScratch, 0, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                new ReadOnlySpan<GPUSimpleDdgiSchedulerDirtyRegion>(
                    _gpuDirtyRegionScratch, 0, dirtyCount));
        }

        internal static bool ShouldQuiesceCertifiedResidentMaintenance(
            bool certificateCurrent,
            bool periodicSourceRefreshWavePending,
            bool solveDrainPending,
            bool recenteredThisFrame,
            bool cohortLightingTransition,
            uint dirtyReasonFlags,
            uint frameIndex,
            uint maintenancePulseFrames)
        {
            uint interval = Math.Max(maintenancePulseFrames, 1u);
            return certificateCurrent &&
                !periodicSourceRefreshWavePending &&
                !solveDrainPending &&
                !recenteredThisFrame &&
                !cohortLightingTransition &&
                dirtyReasonFlags == 0u &&
                frameIndex % interval != 0u;
        }

        private int BuildGpuDirtyRegions(IReadOnlyList<DdgiDirtyRegion>? dirtyRegions)
        {
            if (dirtyRegions == null || dirtyRegions.Count == 0)
            {
                // Clearing presence makes an identical region published after a
                // clean gap a distinct event, while retaining a nonzero generation.
                _gpuDirtyRegionsPresentLastFrame = false;
                return 0;
            }

            int count = Math.Min(dirtyRegions.Count, _gpuDirtyRegionScratch.Length);
            ulong signatureXor = 0u;
            ulong signatureSum = 0u;
            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                DdgiDirtyRegion dirty = dirtyRegions[i];
                BoundingBox bounds = dirty.InfluenceBounds;
                Vector3 minimum = bounds.Min;
                Vector3 maximum = bounds.Max;
                if (!IsFinite(minimum) || !IsFinite(maximum))
                {
                    bounds = dirty.Bounds;
                    minimum = bounds.Min;
                    maximum = bounds.Max;
                }
                Vector3 normalizedMinimum = new(
                    MathF.Min(minimum.X, maximum.X),
                    MathF.Min(minimum.Y, maximum.Y),
                    MathF.Min(minimum.Z, maximum.Z));
                Vector3 normalizedMaximum = new(
                    MathF.Max(minimum.X, maximum.X),
                    MathF.Max(minimum.Y, maximum.Y),
                    MathF.Max(minimum.Z, maximum.Z));
                minimum = normalizedMinimum;
                maximum = normalizedMaximum;
                uint reasonFlags = dirty.ReasonFlags == 0u
                    ? 1u << (int)dirty.Reason
                    : dirty.ReasonFlags;
                ulong regionSignature = HashGpuDirtyRegion(minimum, maximum, reasonFlags);
                signatureXor ^= regionSignature;
                signatureSum = unchecked(signatureSum +
                    regionSignature * 0x9e3779b185ebca87UL);

                if (i >= count)
                    continue;

                _gpuDirtyRegionScratch[i] = new GPUSimpleDdgiSchedulerDirtyRegion
                {
                    Minimum = new Vector4(
                        minimum.X,
                        minimum.Y,
                        minimum.Z,
                        0.0f),
                    Maximum = new Vector4(
                        maximum.X,
                        maximum.Y,
                        maximum.Z,
                        0.0f),
                    ReasonFlags = reasonFlags,
                    Reserved0 = 0u,
                    Reserved1 = 0u
                };
            }

            ulong signature = 1469598103934665603UL;
            signature = AddGpuDirtyRegionHash(signature, unchecked((uint)dirtyRegions.Count));
            signature = AddGpuDirtyRegionHash(
                signature,
                unchecked((uint)((ulong)dirtyRegions.Count >> 32)));
            signature = AddGpuDirtyRegionHash(signature, unchecked((uint)signatureXor));
            signature = AddGpuDirtyRegionHash(signature, unchecked((uint)(signatureXor >> 32)));
            signature = AddGpuDirtyRegionHash(signature, unchecked((uint)signatureSum));
            signature = AddGpuDirtyRegionHash(signature, unchecked((uint)(signatureSum >> 32)));
            _gpuDirtyRegionGeneration = ResolveGpuDirtyRegionGeneration(
                _gpuDirtyRegionGeneration,
                _gpuDirtyRegionsPresentLastFrame,
                _gpuDirtyRegionSignature,
                signature);
            _gpuDirtyRegionsPresentLastFrame = true;
            _gpuDirtyRegionSignature = signature;
            for (int i = 0; i < count; i++)
                _gpuDirtyRegionScratch[i].Generation = _gpuDirtyRegionGeneration;
            return count;
        }

        internal static uint ResolveGpuDirtyRegionGeneration(
            uint currentGeneration,
            bool previousRegionsPresent,
            ulong previousSignature,
            ulong currentSignature)
        {
            uint nonZeroGeneration = NonZeroGeneration(currentGeneration);
            return previousRegionsPresent && previousSignature == currentSignature
                ? nonZeroGeneration
                : AdvanceSourceLightingGeneration(nonZeroGeneration);
        }

        private static ulong HashGpuDirtyRegion(
            Vector3 minimum,
            Vector3 maximum,
            uint reasonFlags)
        {
            ulong hash = 1469598103934665603UL;
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(minimum.X));
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(minimum.Y));
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(minimum.Z));
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(maximum.X));
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(maximum.Y));
            hash = AddGpuDirtyRegionHash(hash, BitConverter.SingleToUInt32Bits(maximum.Z));
            return AddGpuDirtyRegionHash(hash, reasonFlags);
        }

        private static ulong AddGpuDirtyRegionHash(ulong hash, uint value) =>
            unchecked((hash ^ value) * 1099511628211UL);

        private void BuildGpuVolumePolicies(
            GPUSimpleDdgiSchedulerVolumePolicy[] currentPolicies,
            GPUSimpleDdgiSchedulerVolumePolicy[] previousPolicies)
        {
            Array.Clear(currentPolicies);
            Array.Clear(previousPolicies);
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                SimpleDdgiRingQuality quality = ResolveVolumeQuality(volumeIndex);
                bool hasPrevious = TryGetPreviousMatchingVolume(
                    volumeIndex,
                    current,
                    out GPUSimpleDdgiVolume previous);
                if (!hasPrevious)
                    previous = current;
                _gpuVolumePolicyScratch[volumeIndex] = CreateGpuVolumePolicy(
                    current,
                    previous,
                    quality,
                    hasPrevious,
                    volumeIndex);
                _gpuPreviousVolumePolicyScratch[volumeIndex] = CreateGpuVolumePolicy(
                    previous,
                    previous,
                    quality,
                    hasPrevious,
                    volumeIndex);
            }
        }

        private GPUSimpleDdgiSchedulerVolumePolicy CreateGpuVolumePolicy(
            GPUSimpleDdgiVolume current,
            GPUSimpleDdgiVolume previous,
            SimpleDdgiRingQuality quality,
            bool hasPrevious,
            int volumeIndex)
        {
            int countX = CountX(current);
            int countY = CountY(current);
            int countZ = CountZ(current);
            int previousCountX = CountX(previous);
            int previousCountY = CountY(previous);
            int previousCountZ = CountZ(previous);
            int deltaX = 0;
            int deltaY = 0;
            int deltaZ = 0;
            bool cellAligned = hasPrevious && TryResolveCellDelta(
                previous,
                current,
                out deltaX,
                out deltaY,
                out deltaZ);
            int probeCount = VolumeProbeCount(current);
            SimpleDdgiTransportCacheRegion cacheRegion =
                _storageLayout.FindVolume(volumeIndex) ??
                throw new InvalidOperationException(
                    $"Simple-DDGI storage layout is missing scheduler volume {volumeIndex}.");
            uint cachePhysicalFirst =
                checked((uint)cacheRegion.PhysicalFirstProbe);
            uint cachePhysicalCount =
                checked((uint)cacheRegion.PhysicalProbeCount);
            if (cachePhysicalFirst > ushort.MaxValue ||
                cachePhysicalCount > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI scheduler cache range {volumeIndex} exceeds its 16-bit physical ABI ({cachePhysicalFirst}+{cachePhysicalCount}).");
            }
            return new GPUSimpleDdgiSchedulerVolumePolicy
            {
                FirstProbe = ClampToUint(FirstProbe(current)),
                ProbeCount = ClampToUint(probeCount),
                VolumeKind = Kind(current),
                RingIndex = ClampToUint(quality.RingIndex),
                SourceOrdinal = ClampToUint(SourceOrdinal(current)),
                Purpose = ClampToUint((uint)_volumePurposes[volumeIndex]),
                LayoutGeneration = _volumeTableGeneration,
                PreviousLayoutGeneration = hasPrevious && _volumeTableGeneration > 0u
                    ? _volumeTableGeneration - 1u
                    : 0u,
                CurrentOriginAndSpacing = current.OriginAndSpacing,
                PreviousOriginAndSpacing = previous.OriginAndSpacing,
                CurrentCountX = ClampToUint(countX),
                CurrentCountY = ClampToUint(countY),
                CurrentCountZ = ClampToUint(countZ),
                PreviousCountX = ClampToUint(previousCountX),
                PreviousCountY = ClampToUint(previousCountY),
                PreviousCountZ = ClampToUint(previousCountZ),
                PhysicalOffsetX = ClampToUint(PhysicalOffsetX(current)),
                PhysicalOffsetY = ClampToUint(PhysicalOffsetY(current)),
                PhysicalOffsetZ = ClampToUint(PhysicalOffsetZ(current)),
                LayoutFlags = (hasPrevious ? 1u : 0u) | (cellAligned ? 2u : 0u),
                MinimumQuota = ClampToUint(Math.Min(quality.MinimumUpdateQuota, probeCount)),
                PreferredMaximumQuota = ClampToUint(Math.Min(
                    Math.Max(quality.MaximumUpdateQuota, quality.MinimumUpdateQuota), probeCount)),
                SchedulingWeight = ClampToUint(ResolveVolumeSchedulingWeight(volumeIndex, quality.RingIndex)),
                Priority = ClampToUint(Math.Max(0, _volumePriorities[volumeIndex])),
                FullRaysPerProbe = ClampToUint(quality.FullRays),
                MaintenanceRaysPerProbe = ClampToUint(quality.MaintenanceRays),
                MaterialTextureMaxCascade = quality.MaterialTextureMaxCascade < 0
                    ? uint.MaxValue
                    : ClampToUint(quality.MaterialTextureMaxCascade),
                MaxShadedLights = ClampToUint(quality.MaxShadedLights),
                SequenceStride = ClampToUint(ResolveProbeUpdateStride(probeCount)),
                CacheBaseWord = checked((uint)cacheRegion.BaseWord),
                CellDeltaX = deltaX,
                CellDeltaY = deltaY,
                CellDeltaZ = deltaZ,
                DirtyGeneration = _gpuDirtyRegionGeneration,
                ProximityRadiusPadding = current.WorldMinAndEdgeFade.W,
                CacheWordsPerProbe = checked((uint)(
                    cacheRegion.StrideWords * _raysPerProbe)),
                CachePhysicalFirstAndCount = cachePhysicalFirst |
                    (cachePhysicalCount << 16),
                CacheLayoutFlags =
                    BitConverter.SingleToUInt32Bits(current.CacheLayout.W)
            };
        }

        private static int AddGpuRayBucket(Span<uint> buckets, int count, int rayCount)
        {
            uint value = ClampToUint(Math.Clamp(rayCount, 1, GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe));
            for (int i = 0; i < count; i++)
            {
                if (buckets[i] == value)
                    return count;
            }
            if (count >= buckets.Length)
                return count;
            buckets[count] = value;
            return count + 1;
        }

        private static uint ClampToUint(ulong value) =>
            value > uint.MaxValue ? uint.MaxValue : (uint)value;

        private static uint ClampToUint(long value) =>
            value <= 0L ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)value;

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private bool UploadGpuResidentSchedulerBootstrap(
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (_probeCount <= 0 || !_gpuScheduler.IsReady ||
                _gpuScheduler.Layout == null)
            {
                return false;
            }

            EnsureCpuProbeStateCapacity(_probeCount);
            SimpleDdgiGpuSchedulerLayout layout = _gpuScheduler.Layout;
            int stateCount = Math.Min(_probeCount, layout.ActiveProbeCount);
            for (int probeIndex = 0; probeIndex < stateCount; probeIndex++)
                _gpuResidentBootstrapStateScratch[probeIndex] =
                    BuildGpuResidentSchedulerProbeState(probeIndex);

            // Cursors are persistent GPU state. Ordinary frames leave this
            // region untouched; when an arena is replaced, seed it from the
            // most recent fence-complete feedback so fairness survives the
            // resource transition. A first activation has no feedback and
            // therefore uses deterministic zero cursors.
            if (!_gpuScheduler.TryCopyLastFeedbackLaneCursors(
                    _gpuResidentBootstrapLaneCursorScratch))
            {
                Array.Clear(_gpuResidentBootstrapLaneCursorScratch);
            }
            return _gpuScheduler.UploadResidentBootstrap(
                stagingRing,
                commandBuffer,
                new ReadOnlySpan<GPUSimpleDdgiSchedulerProbeState>(
                    _gpuResidentBootstrapStateScratch,
                    0,
                    stateCount),
                _gpuResidentBootstrapLaneCursorScratch);
        }

        private GPUSimpleDdgiSchedulerProbeState BuildGpuResidentSchedulerProbeState(
            int probeIndex)
        {
            uint dirtyReasons = (uint)_probeDirtyReasons[probeIndex];
            if (_probeFresh[probeIndex] != 0)
                dirtyReasons |= SimpleDdgiSchedulerAbi.ReasonFresh;
            if (_probeInactive[probeIndex] == 0)
            {
                // The activation bootstrap has no completed GPU visibility
                // reduction yet. Treat every active slot as a conservative
                // receiver-visible participant; later classification may
                // exclude confidently inactive slots, never silently omit a
                // live slot from the certification denominator.
                dirtyReasons |= SimpleDdgiSchedulerAbi.ProbeMetadataVisible;
            }
            if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length)
            {
                byte schedulingFlags = _probeSchedulingFlags[probeIndex];
                if ((schedulingFlags & ProbeSchedulingScrollExposedFlag) != 0)
                    dirtyReasons |= SimpleDdgiSchedulerAbi.ReasonScrollExposed;
                if ((schedulingFlags & ProbeSchedulingRegionalDirtyFlag) != 0)
                    dirtyReasons |= SimpleDdgiSchedulerAbi.ReasonRegionalDirty;
                if ((schedulingFlags & ProbeSchedulingVisibleFlag) != 0)
                    dirtyReasons |= SimpleDdgiSchedulerAbi.ProbeMetadataVisible;
            }

            // A resident activation inherits the CPU-owned complete atlas. A
            // fresh/scroll/relocation slot has no receiver-visible publication
            // yet; all other slots may advertise their existing complete data.
            if (!_atlasFresh && _probeFresh[probeIndex] == 0 &&
                _probeRelocationPending[probeIndex] == 0)
            {
                dirtyReasons |= SimpleDdgiSchedulerAbi.ProbeMetadataPublished;
            }

            uint sourceRays = Math.Min(
                (uint)_probeSourceRayCounts[probeIndex],
                SimpleDdgiSchedulerAbi.SourceRayCountMask);
            uint transportGeneration = _probeTransportGenerationCounts[probeIndex];
            uint stableUpdates = _probeStableUpdateCounts[probeIndex];
            uint routineState = _probeRoutineMaintenancePending[probeIndex] != 0
                ? 1u
                : 0u;
            uint cacheProbeBaseWordPlusOne = 0u;
            int volumeIndex = ResolveSchedulerVolumeIndex(probeIndex);
            SimpleDdgiTransportCacheRegion? cacheRegion =
                _storageLayout.FindVolume(volumeIndex);
            if (cacheRegion is { } region)
            {
                SimpleDdgiStorageLayoutCompiler.TryResolveProbeCacheBaseWordPlusOne(
                    region,
                    checked((uint)probeIndex),
                    out cacheProbeBaseWordPlusOne);
            }

            return new GPUSimpleDdgiSchedulerProbeState
            {
                LastCommittedUpdateFrame = _probeLastUpdatedFrames[probeIndex],
                LastCommittedSourceRefreshFrame = _probeLastSourceRefreshFrames[probeIndex],
                CommittedSourceLightingGeneration = _probeSourceLightingGenerations[probeIndex],
                SourceEpoch = _probeSourceEpochs[probeIndex] == 0
                    ? 1u
                    : _probeSourceEpochs[probeIndex],
                OwningVolumeTableGeneration = _volumeTableGeneration,
                DirtyReasonFlags = dirtyReasons,
                DirtyStartFrame = _probeDirtyLatencyStartFrames[probeIndex],
                PackedTransportAndLifecycle = SimpleDdgiSchedulerAbi.PackSchedulerProbeLifecycle(
                    sourceRays,
                    transportGeneration,
                    stableUpdates,
                    routineState,
                    0u),
                AppliedInvalidationMarker = _probeInvalidationMarkers[probeIndex],
                CacheProbeBaseWordPlusOne = cacheProbeBaseWordPlusOne
            };
        }

        private void UploadReceiverProbeInvalidations(
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (!_receiverProbeBuffer.IsValid)
                return;
            if (_receiverProbeClearRequired)
            {
                ClearReceiverProbeBufferIfRequired(commandBuffer);
                return;
            }
            if (_receiverProbeInvalidationSlots.Count == 0 || _probeCount <= 0)
                return;

            _receiverProbeInvalidationSlots.Sort();
            _receiverProbeUploadRuns.Clear();
            int stagedCount = 0;
            int previousSlot = -2;
            GPUSimpleDdgiReceiverProbe invalid = SimpleDdgiReceiverProbeEncoding.Invalid;
            for (int dirtyIndex = 0;
                dirtyIndex < _receiverProbeInvalidationSlots.Count;
                dirtyIndex++)
            {
                int slot = _receiverProbeInvalidationSlots[dirtyIndex];
                if ((uint)slot >= (uint)_probeCount || slot == previousSlot)
                    continue;

                if (slot != previousSlot + 1)
                    _receiverProbeUploadRuns.Add(new BufferUploadRun(slot, 1));
                else
                {
                    BufferUploadRun prior = _receiverProbeUploadRuns[^1];
                    _receiverProbeUploadRuns[^1] = new BufferUploadRun(
                        prior.DestinationElementIndex,
                        prior.ElementCount + 1);
                }
                _receiverProbeInvalidationScratch[stagedCount++] = invalid;
                previousSlot = slot;
            }

            if (_receiverProbeUploadRuns.Count > MaxSparseProbeStateUploadRuns)
            {
                // A highly fragmented ownership change is safer and cheaper as
                // one fail-closed transfer fill than hundreds of tiny copies.
                // The scheduler will republish records only after matching atlas
                // transactions complete.
                _receiverProbeClearRequired = true;
                ClearReceiverProbeBufferIfRequired(commandBuffer);
                return;
            }

            if (stagedCount > 0)
            {
                var barrier = new UploadBarrierDescription(
                    ReceiverConsumerStages,
                    AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit);
                GpuBufferUploader.UploadRunsToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _receiverProbeBuffer,
                    new ReadOnlySpan<GPUSimpleDdgiReceiverProbe>(
                        _receiverProbeInvalidationScratch,
                        0,
                        stagedCount),
                    _receiverProbeUploadRuns,
                    barrier);
                _receiverProbeInvalidationBytesThisFrame = SaturatingAdd(
                    _receiverProbeInvalidationBytesThisFrame,
                    checked((ulong)stagedCount * ReceiverProbeStride));
                _receiverProbeInvalidationRunCountThisFrame = SaturatingAdd(
                    _receiverProbeInvalidationRunCountThisFrame,
                    _receiverProbeUploadRuns.Count);
            }

            _receiverProbeInvalidationSlots.Clear();
        }

        private const PipelineStageFlags2 ReceiverConsumerStages =
            PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.FragmentShaderBit;

        private unsafe void ClearReceiverProbeBufferIfRequired(
            CommandBuffer commandBuffer)
        {
            if (!_receiverProbeClearRequired ||
                !_receiverProbeBuffer.IsValid ||
                _receiverProbeBytes == 0)
            {
                return;
            }

            Silk.NET.Vulkan.Buffer buffer =
                _bufferManager.GetBuffer(_receiverProbeBuffer);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                buffer,
                0,
                _receiverProbeBytes,
                uint.MaxValue);
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                buffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                ReceiverConsumerStages,
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
                0,
                _receiverProbeBytes);
            ExecuteBufferBarrier(commandBuffer, barrier);
            _receiverProbeInvalidationBytesThisFrame = SaturatingAdd(
                _receiverProbeInvalidationBytesThisFrame,
                _receiverProbeBytes);
            _receiverProbeInvalidationRunCountThisFrame = SaturatingAdd(
                _receiverProbeInvalidationRunCountThisFrame,
                1);
            _receiverProbeFullClearThisFrame = true;
            _receiverProbeClearRequired = false;
            _receiverProbeInvalidationSlots.Clear();
        }

        private void UploadProbeState(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (_probeCount <= 0 || !_probeStateBuffer.IsValid ||
                (!_probeStateUploadRequired && _probeStateDirtySlots.Count == 0))
                return;

            var barrier = new UploadBarrierDescription(
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            if (_probeStateUploadRequired)
            {
                _uploadStateDirtySlotCount = _probeCount;
                _uploadStateUploadRunCount = _probeCount > 0 ? 1 : 0;
                for (int i = 0; i < _probeCount; i++)
                    _probeStateScratch[i] = BuildProbeStateRecord(i);

                GpuBufferUploader.UploadSpanToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _probeStateBuffer,
                    new ReadOnlySpan<GPUSimpleDdgiProbeState>(_probeStateScratch, 0, _probeCount),
                    barrierDescription: barrier);
            }
            else
            {
                _probeStateDirtySlots.Sort();
                _probeStateUploadRuns.Clear();
                int stagedCount = 0;
                int previousSlot = -2;
                for (int dirtyIndex = 0; dirtyIndex < _probeStateDirtySlots.Count; dirtyIndex++)
                {
                    int slot = _probeStateDirtySlots[dirtyIndex];
                    if ((uint)slot >= (uint)_probeCount || slot == previousSlot)
                        continue;

                    if (slot != previousSlot + 1)
                        _probeStateUploadRuns.Add(new BufferUploadRun(slot, 1));
                    else
                    {
                        BufferUploadRun prior = _probeStateUploadRuns[^1];
                        _probeStateUploadRuns[^1] = new BufferUploadRun(prior.DestinationElementIndex, prior.ElementCount + 1);
                    }
                    _probeStateScratch[stagedCount++] = BuildProbeStateRecord(slot);
                    previousSlot = slot;
                }

                if (_probeStateUploadRuns.Count > MaxSparseProbeStateUploadRuns)
                {
                    _uploadStateDirtySlotCount = _probeCount;
                    _uploadStateUploadRunCount = _probeCount > 0 ? 1 : 0;
                    for (int i = 0; i < _probeCount; i++)
                        _probeStateScratch[i] = BuildProbeStateRecord(i);
                    GpuBufferUploader.UploadSpanToBuffer(
                        _context, _bufferManager, stagingRing, commandBuffer, _probeStateBuffer,
                        new ReadOnlySpan<GPUSimpleDdgiProbeState>(_probeStateScratch, 0, _probeCount),
                        barrierDescription: barrier);
                }
                else if (stagedCount > 0)
                {
                    _uploadStateDirtySlotCount = stagedCount;
                    _uploadStateUploadRunCount = _probeStateUploadRuns.Count;
                    GpuBufferUploader.UploadRunsToBuffer(
                        _context, _bufferManager, stagingRing, commandBuffer, _probeStateBuffer,
                        new ReadOnlySpan<GPUSimpleDdgiProbeState>(_probeStateScratch, 0, stagedCount),
                        _probeStateUploadRuns,
                        barrier);
                }
            }

            _probeStateUploadRequired = false;
            _probeStateDirtySlots.Clear();
        }

        private GPUSimpleDdgiProbeState BuildProbeStateRecord(int probeIndex)
        {
            uint flags = PackProbeStateFlags(0u, _probeGenerations[probeIndex]);
            if (_probeFresh[probeIndex] != 0)
                flags |= ProbeStateFreshFlag;
            if (_probeInactive[probeIndex] != 0)
                flags |= ProbeStateInactiveFlag;
            if ((uint)probeIndex < (uint)_probeRelocationPending.Length &&
                _probeRelocationPending[probeIndex] != 0)
            {
                flags |= ProbeStateRelocationPendingFlag;
            }
            if ((uint)probeIndex < (uint)_probeVisibilityValid.Length &&
                _probeVisibilityValid[probeIndex] != 0 &&
                (flags & (ProbeStateFreshFlag | ProbeStateInactiveFlag |
                    ProbeStateRelocationPendingFlag)) == 0u)
            {
                flags |= ProbeStateVisibilityValidFlag;
            }

            Vector3 relocation = (uint)probeIndex < (uint)_probeRelocations.Length
                ? _probeRelocations[probeIndex]
                : Vector3.Zero;
            float activeWeight = (uint)probeIndex < (uint)_probeActiveWeights.Length
                ? _probeActiveWeights[probeIndex]
                : 1.0f;
            uint classification = (uint)probeIndex < (uint)_probeClassifications.Length
                ? _probeClassifications[probeIndex]
                : 0u;
            if (_probeInactive[probeIndex] != 0)
            {
                activeWeight = 0.0f;
                classification = 1u;
            }

            return new GPUSimpleDdgiProbeState
            {
                RelocationAndActive = new Vector4(relocation.X, relocation.Y, relocation.Z, Math.Clamp(activeWeight, 0.0f, 1.0f)),
                Flags = flags,
                Age = GetProbeAge(probeIndex),
                Classification = classification,
                Reserved0 = BitConverter.SingleToUInt32Bits((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length ? _probeLuminanceChangeEma[probeIndex] : 0.0f)
            };
        }

        private void UploadProbeUpdateQueue(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (_probesToUpdate <= 0 || !_probeUpdateQueueBuffer.IsValid)
                return;

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _probeUpdateQueueBuffer,
                new ReadOnlySpan<GPUSimpleDdgiProbeUpdate>(_updateQueueScratch, 0, _probesToUpdate),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private bool EnsureCapacity(int probeCount, int raysPerProbe, int probesToUpdate, CommandBuffer commandBuffer = default)
        {
            bool readbackRequired = _schedulerMode != SimpleDdgiSchedulerMode.GpuResident &&
                probeCount > 0 &&
                RequiresProbeStateReadback(
                    _settings.GlobalIllumination.SimpleDdgiClassificationReadbackEnabled,
                    _settings.GlobalIllumination.SimpleDdgiTransportV2Enabled);
            SimpleDdgiCapacityKey requiredKey = CreateCapacityKey(
                probeCount,
                raysPerProbe,
                probesToUpdate,
                readbackRequired);
            long predicateStart = Stopwatch.GetTimestamp();
            if (_capacityKeyValid && requiredKey == _capacityKey)
            {
                _uploadCapacityStableKeyHit = true;
                _uploadCapacityPredicateMicroseconds += ElapsedMicroseconds(predicateStart);
                if (_context.ValidationSettings.Mode != RendererValidationMode.Off)
                    ValidateCachedCapacityPlan(_capacityPlan, readbackRequired);
                return true;
            }

            _uploadCapacityTransitionReason |= ResolveCapacityTransitionReason(
                _capacityKeyValid,
                _capacityKey,
                requiredKey);
            _uploadCapacityPredicateMicroseconds += ElapsedMicroseconds(predicateStart);

            long planStart = Stopwatch.GetTimestamp();
            bool sampledAtlasCandidate =
                requiredKey.SampledAtlasRequested &&
                requiredKey.SampledAtlasProvisioningAvailable &&
                _sampledAtlasLayout.AdmittedProbeCount > 0;
            SimpleDdgiProbeResidencyMode residencyMode =
                requiredKey.ResidencyMode.Sanitize();
            SimpleDdgiMemoryPlan allocationPlan = SimpleDdgiMemoryPlan.Create(
                Math.Max(0, probeCount),
                Math.Clamp(probesToUpdate, 0, Math.Max(0, probeCount)),
                raysPerProbe,
                sampledAtlasRequested: sampledAtlasCandidate,
                // The immutable graph binds these in V1 as well as V2, but V1
                // only requires graph-safe placeholder descriptors rather than
                // probe-sized allocations it never reads.
                concreteTransportBuffers: requiredKey.TransportV2Enabled ||
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident,
                readbackBufferCount: readbackRequired
                    ? RenderingConstants.FramesInFlight
                    : 0,
                residentPrivateTargets:
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident,
                schedulerMode: _schedulerMode,
                schedulerActiveVolumeCount: _volumeCount,
                schedulerValidationEnabled:
                    _context.ValidationSettings.Mode != RendererValidationMode.Off,
                residencyMode: residencyMode,
                densePayloadProbeCount: requiredKey.DensePayloadProbeCount,
                sparseVirtualProbeCount: requiredKey.SparseVirtualProbeCount,
                sparseVirtualPageCount: requiredKey.SparseVirtualPageCount,
                sparsePhysicalPageCapacity:
                    requiredKey.SparsePhysicalPageCapacity,
                maximumPageAdmissionsPerFrame:
                    _settings.GlobalIllumination
                        .SimpleDdgiSparseMaximumAdmissionsPerFrame,
                storagePackingMode:
                    _settings.GlobalIllumination.SimpleDdgiStoragePackingMode,
                sampledAtlasCoverageMode:
                    _settings.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode,
                storageLayout: _storageLayout,
                sampledAtlasLayout: sampledAtlasCandidate
                    ? _sampledAtlasLayout
                    : null);
            long sampledBudgetStart = Stopwatch.GetTimestamp();
            bool sampledAtlasAdmitted = sampledAtlasCandidate &&
                (requiredKey.SampledAtlasBudgetBytes == 0UL ||
                    allocationPlan.LiveBytes <= requiredKey.SampledAtlasBudgetBytes);
            if (sampledAtlasCandidate && !sampledAtlasAdmitted)
            {
                allocationPlan = SimpleDdgiMemoryPlan.Create(
                    Math.Max(0, probeCount),
                    Math.Clamp(probesToUpdate, 0, Math.Max(0, probeCount)),
                    raysPerProbe,
                    sampledAtlasRequested: false,
                    concreteTransportBuffers: requiredKey.TransportV2Enabled ||
                        _schedulerMode == SimpleDdgiSchedulerMode.GpuResident,
                    readbackBufferCount: readbackRequired
                        ? RenderingConstants.FramesInFlight
                        : 0,
                    residentPrivateTargets:
                        _schedulerMode == SimpleDdgiSchedulerMode.GpuResident,
                    schedulerMode: _schedulerMode,
                    schedulerActiveVolumeCount: _volumeCount,
                    schedulerValidationEnabled:
                        _context.ValidationSettings.Mode != RendererValidationMode.Off,
                    residencyMode: residencyMode,
                    densePayloadProbeCount: requiredKey.DensePayloadProbeCount,
                    sparseVirtualProbeCount: requiredKey.SparseVirtualProbeCount,
                    sparseVirtualPageCount: requiredKey.SparseVirtualPageCount,
                    sparsePhysicalPageCapacity:
                        requiredKey.SparsePhysicalPageCapacity,
                    maximumPageAdmissionsPerFrame:
                        _settings.GlobalIllumination
                            .SimpleDdgiSparseMaximumAdmissionsPerFrame,
                    storagePackingMode:
                        _settings.GlobalIllumination.SimpleDdgiStoragePackingMode,
                    sampledAtlasCoverageMode:
                        _settings.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode,
                    storageLayout: _storageLayout,
                    sampledAtlasLayout: null);
            }
            _uploadCapacitySampledAtlasBudgetMicroseconds +=
                ElapsedMicroseconds(sampledBudgetStart);
            _uploadCapacityPlanCreationMicroseconds += ElapsedMicroseconds(planStart);

            bool storageContractChanged = _capacityKeyValid &&
                (_capacityPlan.StoragePackingMode != allocationPlan.StoragePackingMode ||
                 _capacityPlan.StorageAbiVersion != allocationPlan.StorageAbiVersion ||
                 _capacityPlan.DirectionCodebookVersion != allocationPlan.DirectionCodebookVersion ||
                 _capacityPlan.StorageLayoutFingerprint != allocationPlan.StorageLayoutFingerprint);
            bool sampledMappingChanged = _capacityKeyValid &&
                (_capacityPlan.SampledAtlasCoverageMode != allocationPlan.SampledAtlasCoverageMode ||
                 _capacityPlan.SampledAtlasLayoutFingerprint != allocationPlan.SampledAtlasLayoutFingerprint);

            predicateStart = Stopwatch.GetTimestamp();
            bool synchronizedCapacityTransition =
                storageContractChanged || sampledMappingChanged ||
                RequiresSynchronizedCapacityTransition(
                    allocationPlan,
                    readbackRequired);
            CaptureCapacityResourceTelemetry(allocationPlan, readbackRequired);
            _uploadCapacityPredicateMicroseconds += ElapsedMicroseconds(predicateStart);
            if (synchronizedCapacityTransition &&
                !EnsureBindlessDescriptorReadersComplete(
                    commandBuffer,
                    "Simple DDGI capacity generation publication"))
            {
                // The current fail-closed submission still binds the prior
                // generation. Extend its exact completion token before
                // returning so a later retry cannot retire it prematurely.
                MarkResourcesUsedByPendingSubmission();
                return false;
            }
            bool priorGenerationComplete =
                _resourceLastUseFrameFenceValue == 0UL ||
                _completedFrameFenceValue >= _resourceLastUseFrameFenceValue;
            bool destroyPreviousImmediately =
                synchronizedCapacityTransition && priorGenerationComplete;
            if (synchronizedCapacityTransition && !priorGenerationComplete)
            {
                CountRetiringBufferResources(
                    allocationPlan,
                    readbackRequired,
                    storageContractChanged,
                    out int retiringBufferCount,
                    out ulong retiringBufferBytes);
                if (retiringBufferCount > 0 &&
                    !_bufferRetirement.CanAdmit(
                        liveBytes: 0UL,
                        incomingBytes: retiringBufferBytes,
                        incomingRecordCount: retiringBufferCount,
                        out GpuRetirementAdmissionFailure failure))
                {
                    _capacityTransitionDeferred = true;
                    _capacityTransitionDeferredReason =
                        $"buffer-retirement-{failure.ToString().ToLowerInvariant()}";
                    return false;
                }

                bool sampledAtlasTransition =
                    _sampledAtlas?.IsReady == true &&
                    _sampledAtlas.EstimatedImageBytes !=
                        allocationPlan.SampledAtlasImageBytes;
                if (sampledAtlasTransition &&
                    !_sampledAtlas!.CanRetireCurrentAllocation(
                        _resourceLastUseFrameFenceValue,
                        _completedFrameFenceValue))
                {
                    _capacityTransitionDeferred = true;
                    _capacityTransitionDeferredReason =
                        "sampled-atlas-retirement-capacity";
                    return false;
                }
                // The DDGI component budget governs each stable generation,
                // not the short completion-token overlap required to switch
                // generations without DeviceWaitIdle. Gate that overlap against
                // the plan's total tracked-memory contract instead. Adding the
                // complete incoming plan is deliberately conservative because
                // unchanged allocations are already present in the snapshot.
                RenderBudgetProfile profile =
                    _settings.PerformanceBudgets.Profile;
                MemoryBudgetSnapshot tracked =
                    _bufferManager.AllocationTracker.CreateSnapshot(profile);
                ulong projectedTrackedBytes = SaturatingAdd(
                    tracked.TotalTrackedBytes,
                    allocationPlan.LiveBytes);
                ulong transitionMemoryLimit =
                    ResolveTransitionMemoryLimit(profile.GpuMemoryBudgetBytes);
                if (projectedTrackedBytes > transitionMemoryLimit)
                {
                    _capacityTransitionDeferred = true;
                    _capacityTransitionDeferredReason =
                        "completion-pending-global-memory-budget";
                    return false;
                }
            }
            _capacityTransitionDeferred = false;
            _capacityTransitionDeferredReason = string.Empty;
            ulong irradianceBytes = allocationPlan.IrradianceAtlasBytes;
            ulong visibilityBytes = allocationPlan.VisibilityAtlasBytes;
            ulong rayBytes = allocationPlan.RayScratchBytes;
            ulong probeStateBytes = allocationPlan.ProbeStateBytes;
            ulong receiverProbeBytes = allocationPlan.ReceiverProbeBytes;
            ulong updateQueueBytes = allocationPlan.UpdateQueueBytes;
            ulong relocationClassificationBytes =
                allocationPlan.RelocationClassificationBytes;

            // A stable capacity change deliberately invalidates canonical
            // history: preserving it would require old and new atlas allocations
            // to overlap outside the admitted hard budget.
            long bufferTransitionStart = Stopwatch.GetTimestamp();
            bool buffersChanged = false;
            buffersChanged |= EnsureBuffer(ref _irradianceAtlasBuffer, ref _irradianceAtlasBytes, irradianceBytes, "Simple DDGI Irradiance Atlas", invalidateAtlas: true, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately);
            buffersChanged |= EnsureBuffer(ref _visibilityAtlasBuffer, ref _visibilityAtlasBytes, visibilityBytes, "Simple DDGI Visibility Atlas", invalidateAtlas: true, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately);
            // Keep these allocations concrete even when V1 is selected so the
            // static render-graph declaration remains valid during a live V1/V2
            // toggle. V1 never dispatches the transport pass or touches them.
            ulong transportAtlasBytes =
                allocationPlan.TransportIrradianceBytes;
            int sourceCacheRayCapacity = allocationPlan.RayCapacity;
            bool sourceCacheRayCapacityChanged =
                _transportSourceCacheRayCapacity != sourceCacheRayCapacity;
            ulong sourceCacheBytes =
                allocationPlan.TransportSourceCacheBytes;
            buffersChanged |= EnsureBuffer(
                ref _transportIrradianceAtlasBuffer,
                ref _transportIrradianceAtlasBytes,
                transportAtlasBytes,
                "Simple DDGI Transport Irradiance Target",
                invalidateAtlas: false,
                commandBuffer: commandBuffer,
                preserveContents: false,
                destroyPreviousImmediately: destroyPreviousImmediately);
            bool sourceCacheReallocated = EnsureBuffer(
                ref _transportSourceCacheBuffer,
                ref _transportSourceCacheBytes,
                sourceCacheBytes,
                "Simple DDGI Transport Source Cache",
                invalidateAtlas: false,
                commandBuffer: commandBuffer,
                preserveContents: false,
                destroyPreviousImmediately: destroyPreviousImmediately,
                forceRecreate: storageContractChanged);
            if (sourceCacheReallocated || sourceCacheRayCapacityChanged)
            {
                InvalidateTransportSourceCacheMetadata();
            }
            _transportSourceCacheRayCapacity = sourceCacheRayCapacity;
            buffersChanged |= sourceCacheReallocated;
            buffersChanged |= EnsureBuffer(ref _rayResultScratchBuffer, ref _rayScratchBytes, rayBytes, "Simple DDGI Ray Scratch", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately, forceRecreate: storageContractChanged);
            if (EnsureBuffer(ref _probeStateBuffer, ref _probeStateBytes, probeStateBytes, "Simple DDGI Probe State", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately))
            {
                _probeStateUploadRequired = true;
                buffersChanged = true;
            }
            if (EnsureBuffer(
                    ref _receiverProbeBuffer,
                    ref _receiverProbeBytes,
                    receiverProbeBytes,
                    "Simple DDGI Receiver Probes",
                    invalidateAtlas: false,
                    commandBuffer: commandBuffer,
                    preserveContents: false,
                    destroyPreviousImmediately: destroyPreviousImmediately))
            {
                _receiverProbeClearRequired = true;
                _receiverProbeInvalidationSlots.Clear();
                buffersChanged = true;
            }
            buffersChanged |= EnsureBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBytes, updateQueueBytes, "Simple DDGI Probe Update Queue", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately);
            buffersChanged |= EnsureBuffer(ref _relocationClassificationBuffer, ref _relocationClassificationBytes, relocationClassificationBytes, "Simple DDGI Relocation Classification", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false, destroyPreviousImmediately: destroyPreviousImmediately);
            _uploadCapacityBufferTransitionMicroseconds +=
                ElapsedMicroseconds(bufferTransitionStart);

            long readbackStart = Stopwatch.GetTimestamp();
            ReconcileProbeStateReadbackBuffers(
                allocationPlan,
                readbackRequired,
                destroyImmediately: destroyPreviousImmediately);
            _uploadCapacityReadbackReconciliationMicroseconds +=
                ElapsedMicroseconds(readbackStart);

            if (buffersChanged && _registeredBindlessHeap != null)
            {
                long descriptorStart = Stopwatch.GetTimestamp();
                RegisterBuffers(_registeredBindlessHeap);
                _uploadCapacityDescriptorRegistrationMicroseconds +=
                    ElapsedMicroseconds(descriptorStart);
                _uploadCapacityDescriptorRegistrationCount++;
            }

            UpdateSampledAtlasCapacity(
                allocationPlan.SampledAtlasPhysicalProbeCapacity,
                priorGenerationComplete: priorGenerationComplete,
                sampledAtlasAdmitted: sampledAtlasAdmitted,
                mirrorAllocationBudgetBytes:
                    ResolveSampledAtlasAllocationBudget(
                        requiredKey.SampledAtlasBudgetBytes,
                        allocationPlan),
                forceRecreate: sampledMappingChanged);
            if (sampledMappingChanged)
                _sampledAtlas?.MarkFullSyncRequired();
            if (storageContractChanged || sampledMappingChanged)
            {
                // A mode/ABI/codebook or compact-mapping transition is a cold
                // generation. Never let old source bytes or atlas history
                // survive under new addressing, direction, or layer-owner
                // semantics.
                _atlasClearRequired = true;
                _atlasFresh = true;
                _sampledAtlas?.MarkFullSyncRequired();
            }
            _capacityPlan = allocationPlan;
            _capacityKey = requiredKey;
            _capacityKeyValid = true;
            if (_context.ValidationSettings.Mode != RendererValidationMode.Off)
                ValidateCachedCapacityPlan(allocationPlan, readbackRequired);
            return true;
        }

        private bool RequiresSynchronizedCapacityTransition(
            SimpleDdgiMemoryPlan required,
            bool readbackRequired)
        {
            if (RequiresBufferTransition(_irradianceAtlasBuffer, _irradianceAtlasBytes, required.IrradianceAtlasBytes) ||
                RequiresBufferTransition(_visibilityAtlasBuffer, _visibilityAtlasBytes, required.VisibilityAtlasBytes) ||
                RequiresBufferTransition(_transportIrradianceAtlasBuffer, _transportIrradianceAtlasBytes, required.TransportIrradianceBytes) ||
                RequiresBufferTransition(_transportSourceCacheBuffer, _transportSourceCacheBytes, required.TransportSourceCacheBytes) ||
                RequiresBufferTransition(_rayResultScratchBuffer, _rayScratchBytes, required.RayScratchBytes) ||
                RequiresBufferTransition(_probeStateBuffer, _probeStateBytes, required.ProbeStateBytes) ||
                RequiresBufferTransition(_receiverProbeBuffer, _receiverProbeBytes, required.ReceiverProbeBytes) ||
                RequiresBufferTransition(_probeUpdateQueueBuffer, _probeUpdateQueueBytes, required.UpdateQueueBytes) ||
                RequiresBufferTransition(_relocationClassificationBuffer, _relocationClassificationBytes, required.RelocationClassificationBytes))
            {
                return true;
            }

            ulong requiredReadbackBytes = readbackRequired
                ? required.ProbeStateReadbackBytesPerBuffer
                : 0UL;
            for (int frameIndex = 0;
                frameIndex < _probeStateReadbackBuffers.Length;
                frameIndex++)
            {
                BufferHandle handle = _probeStateReadbackBuffers[frameIndex];
                if (!handle.IsValid)
                    continue;

                if (!readbackRequired ||
                    RequiresStableCapacityReallocation(
                        _probeStateReadbackProvisionedBytes[frameIndex],
                        requiredReadbackBytes))
                {
                    return true;
                }
            }

            ulong sampledAtlasBytes =
                _sampledAtlas?.EstimatedImageBytes ?? 0UL;
            bool sampledAtlasFailureCached =
                required.SampledAtlasPhysicalProbeCapacity > 0 &&
                _sampledAtlasFailedProbeCount ==
                    required.SampledAtlasPhysicalProbeCapacity &&
                _sampledAtlasFailureAllocationBudgetBytes ==
                    ResolveSampledAtlasAllocationBudget(
                        _settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes,
                        required);
            if (_registeredBindlessHeap != null &&
                !sampledAtlasFailureCached &&
                sampledAtlasBytes != required.SampledAtlasImageBytes)
            {
                return true;
            }

            if (RequiresSchedulerArenaReplacement(required))
                return true;

            return false;
        }

        private bool RequiresSchedulerArenaReplacement(
            in SimpleDdgiMemoryPlan required)
        {
            SimpleDdgiGpuSchedulerLayout? current = _gpuScheduler.Layout;
            if (!required.SchedulerMode.IsGpuMode())
                return current != null;
            if (current == null)
                return _registeredBindlessHeap != null;

            return required.ProbeCount != current.ActiveProbeCount ||
                required.UpdateRequestCapacity != current.RequestCapacity ||
                required.SchedulerActiveLaneCount != current.ActiveLaneCount ||
                (required.SchedulerValidationReadbackBytes != 0UL) !=
                    current.ValidationEnabled;
        }

        private void CountRetiringBufferResources(
            SimpleDdgiMemoryPlan required,
            bool readbackRequired,
            bool forceStorageRecreation,
            out int count,
            out ulong bytes)
        {
            int localCount = 0;
            ulong localBytes = 0UL;

            Accumulate(_irradianceAtlasBuffer, _irradianceAtlasBytes, required.IrradianceAtlasBytes);
            Accumulate(_visibilityAtlasBuffer, _visibilityAtlasBytes, required.VisibilityAtlasBytes);
            Accumulate(_transportIrradianceAtlasBuffer, _transportIrradianceAtlasBytes, required.TransportIrradianceBytes);
            Accumulate(_transportSourceCacheBuffer, _transportSourceCacheBytes, required.TransportSourceCacheBytes, forceStorageRecreation);
            Accumulate(_rayResultScratchBuffer, _rayScratchBytes, required.RayScratchBytes, forceStorageRecreation);
            Accumulate(_probeStateBuffer, _probeStateBytes, required.ProbeStateBytes);
            Accumulate(_receiverProbeBuffer, _receiverProbeBytes, required.ReceiverProbeBytes);
            Accumulate(_probeUpdateQueueBuffer, _probeUpdateQueueBytes, required.UpdateQueueBytes);
            Accumulate(
                _relocationClassificationBuffer,
                _relocationClassificationBytes,
                required.RelocationClassificationBytes);

            ulong requiredReadbackBytes = readbackRequired
                ? required.ProbeStateReadbackBytesPerBuffer
                : 0UL;
            for (int frameIndex = 0;
                frameIndex < _probeStateReadbackBuffers.Length;
                frameIndex++)
            {
                Accumulate(
                    _probeStateReadbackBuffers[frameIndex],
                    _probeStateReadbackProvisionedBytes[frameIndex],
                    requiredReadbackBytes);
            }

            count = localCount;
            bytes = localBytes;

            void Accumulate(
                BufferHandle handle,
                ulong provisionedBytes,
                ulong requiredBytes,
                bool forceRecreation = false)
            {
                if (!handle.IsValid ||
                    (!forceRecreation && !RequiresStableCapacityReallocation(
                        provisionedBytes,
                        requiredBytes)))
                {
                    return;
                }

                localCount = checked(localCount + 1);
                localBytes = SaturatingAdd(localBytes, provisionedBytes);
            }
        }

        private static bool RequiresBufferTransition(
            BufferHandle handle,
            ulong provisionedBytes,
            ulong requiredBytes) =>
            handle.IsValid &&
            RequiresStableCapacityReallocation(provisionedBytes, requiredBytes);

        private SimpleDdgiCapacityKey CreateCapacityKey(
            int probeCount,
            int raysPerProbe,
            int probesToUpdate,
            bool readbackRequired)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            int probes = Math.Max(0, probeCount);
            SimpleDdgiMemoryPlan admittedLayout =
                _lastLayoutReport?.AcceptedMemoryPlan ??
                SimpleDdgiMemoryPlan.Empty;
            bool admittedLayoutMatches =
                admittedLayout.VirtualProbeCount == probes;
            SimpleDdgiProbeResidencyMode residencyMode =
                admittedLayoutMatches
                    ? admittedLayout.ResidencyMode.Sanitize()
                    : SimpleDdgiProbeResidencyMode.Dense;
            int densePayloadProbeCount = admittedLayoutMatches
                ? admittedLayout.DensePayloadProbeCount
                : probes;
            int sparseVirtualProbeCount = admittedLayoutMatches
                ? admittedLayout.SparseVirtualProbeCount
                : 0;
            int sparseVirtualPageCount = admittedLayoutMatches
                ? admittedLayout.SparseVirtualPageCount
                : 0;
            int sparsePhysicalPageCapacity = admittedLayoutMatches
                ? admittedLayout.SparsePhysicalPageCapacity
                : 0;
            int physicalProbeCapacity = admittedLayoutMatches
                ? admittedLayout.PhysicalProbeCapacity
                : probes;
            ulong residencyArenaBytes = admittedLayoutMatches
                ? admittedLayout.ResidencyArenaBytes
                : 0UL;
            ulong residencyFeedbackReadbackBytes = admittedLayoutMatches
                ? admittedLayout.ResidencyFeedbackReadbackBytes
                : 0UL;
            bool sampledRequested = probes > 0 && SampledAtlasRequested;
            SimpleDdgiSchedulerMode schedulerMode = _schedulerMode.Sanitize();
            int schedulerActiveLaneCount = 0;
            ulong schedulerArenaBytes = 0UL;
            ulong schedulerValidationReadbackBytes = 0UL;
            if (schedulerMode.IsGpuMode() && probes > 0)
            {
                SimpleDdgiGpuSchedulerLayout schedulerLayout =
                    SimpleDdgiGpuSchedulerLayout.Create(
                        probes,
                        Math.Clamp(probesToUpdate, 0, probes),
                        Math.Clamp(
                            _volumeCount,
                            1,
                            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                        SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity,
                        _context.ValidationSettings.Mode !=
                            RendererValidationMode.Off);
                schedulerActiveLaneCount = schedulerLayout.ActiveLaneCount;
                schedulerArenaBytes = schedulerLayout.TotalBytes;
                schedulerValidationReadbackBytes =
                    schedulerLayout.ValidationEnabled
                        ? checked(
                            (ulong)RenderingConstants.FramesInFlight *
                            SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes)
                        : 0UL;
            }
            return new SimpleDdgiCapacityKey(
                gi.DdgiQualityTier,
                ComputeCapacityTopologyFingerprint(probes),
                probes,
                residencyMode,
                densePayloadProbeCount,
                sparseVirtualProbeCount,
                sparseVirtualPageCount,
                sparsePhysicalPageCapacity,
                physicalProbeCapacity,
                residencyArenaBytes,
                residencyFeedbackReadbackBytes,
                Math.Clamp(
                    raysPerProbe,
                    1,
                    GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe),
                Math.Clamp(probesToUpdate, 0, probes),
                readbackRequired,
                sampledRequested,
                sampledRequested &&
                    _registeredBindlessHeap != null &&
                    _context.ShaderStorageImageArrayNonUniformIndexingSupported,
                gi.DdgiAtlasMemoryBudgetBytes,
                gi.SimpleDdgiTransportV2Enabled,
                schedulerMode,
                schedulerActiveLaneCount,
                schedulerArenaBytes,
                schedulerValidationReadbackBytes,
                probes > 0 && gi.EffectiveUseDdgi);
        }

        private ulong ComputeCapacityTopologyFingerprint(int probeCount)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;
            hash = (hash ^ (uint)Math.Max(0, probeCount)) * prime;
            hash = (hash ^ (uint)Math.Max(0, _volumeCount)) * prime;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                hash = (hash ^ (uint)Math.Max(0, CountX(volume))) * prime;
                hash = (hash ^ (uint)Math.Max(0, CountY(volume))) * prime;
                hash = (hash ^ (uint)Math.Max(0, CountZ(volume))) * prime;
                hash = (hash ^ BitConverter.SingleToUInt32Bits(volume.WorldMaxAndKind.W)) * prime;
                hash = (hash ^ BitConverter.SingleToUInt32Bits(volume.CacheLayout.X)) * prime;
                hash = (hash ^ BitConverter.SingleToUInt32Bits(volume.CacheLayout.Y)) * prime;
                hash = (hash ^ BitConverter.SingleToUInt32Bits(volume.CacheLayout.Z)) * prime;
                hash = (hash ^ BitConverter.SingleToUInt32Bits(volume.CacheLayout.W)) * prime;
            }

            hash = (hash ^ _storageLayout.Fingerprint) * prime;
            hash = (hash ^ _sampledAtlasLayout.Fingerprint) * prime;

            return hash;
        }

        internal static SimpleDdgiCapacityTransitionReason ResolveCapacityTransitionReason(
            bool previousValid,
            SimpleDdgiCapacityKey previous,
            SimpleDdgiCapacityKey required)
        {
            if (!previousValid)
                return SimpleDdgiCapacityTransitionReason.InitialAllocation;

            SimpleDdgiCapacityTransitionReason reason =
                SimpleDdgiCapacityTransitionReason.None;
            if (previous.QualityTier != required.QualityTier)
                reason |= SimpleDdgiCapacityTransitionReason.QualityTier;
            if (previous.TopologyFingerprint != required.TopologyFingerprint)
                reason |= SimpleDdgiCapacityTransitionReason.Topology;
            if (previous.ProbeCount != required.ProbeCount)
                reason |= SimpleDdgiCapacityTransitionReason.ProbeCapacity;
            if (previous.ResidencyMode != required.ResidencyMode)
                reason |= SimpleDdgiCapacityTransitionReason.ResidencyMode;
            if (previous.DensePayloadProbeCount != required.DensePayloadProbeCount ||
                previous.SparseVirtualProbeCount != required.SparseVirtualProbeCount ||
                previous.SparseVirtualPageCount != required.SparseVirtualPageCount ||
                previous.SparsePhysicalPageCapacity != required.SparsePhysicalPageCapacity ||
                previous.PhysicalProbeCapacity != required.PhysicalProbeCapacity ||
                previous.ResidencyArenaBytes != required.ResidencyArenaBytes ||
                previous.ResidencyFeedbackReadbackBytes !=
                    required.ResidencyFeedbackReadbackBytes)
            {
                reason |= SimpleDdgiCapacityTransitionReason.ResidencyCapacity;
            }
            if (previous.RayCapacity != required.RayCapacity)
                reason |= SimpleDdgiCapacityTransitionReason.RayCapacity;
            if (previous.RequestCapacity != required.RequestCapacity)
                reason |= SimpleDdgiCapacityTransitionReason.RequestCapacity;
            if (previous.ReadbackRequired != required.ReadbackRequired)
                reason |= SimpleDdgiCapacityTransitionReason.ReadbackMode;
            if (previous.SampledAtlasRequested != required.SampledAtlasRequested ||
                previous.SampledAtlasProvisioningAvailable !=
                    required.SampledAtlasProvisioningAvailable)
            {
                reason |= SimpleDdgiCapacityTransitionReason.SampledAtlasMode;
            }
            if (previous.SampledAtlasBudgetBytes != required.SampledAtlasBudgetBytes)
                reason |= SimpleDdgiCapacityTransitionReason.SampledAtlasBudget;
            if (previous.TransportV2Enabled != required.TransportV2Enabled)
                reason |= SimpleDdgiCapacityTransitionReason.TransportMode;
            if (previous.SchedulerMode != required.SchedulerMode)
                reason |= SimpleDdgiCapacityTransitionReason.SchedulerMode;
            if (previous.SchedulerActiveLaneCount !=
                    required.SchedulerActiveLaneCount ||
                previous.SchedulerArenaBytes != required.SchedulerArenaBytes)
            {
                reason |= SimpleDdgiCapacityTransitionReason.SchedulerCapacity;
            }
            if (previous.SchedulerValidationReadbackBytes !=
                required.SchedulerValidationReadbackBytes)
            {
                reason |= SimpleDdgiCapacityTransitionReason.SchedulerValidation;
            }
            if (previous.FeatureEnabled && !required.FeatureEnabled)
                reason |= SimpleDdgiCapacityTransitionReason.FeatureDisabled;
            return reason;
        }

        private void CaptureCapacityResourceTelemetry(
            SimpleDdgiMemoryPlan plan,
            bool readbackRequired)
        {
            _uploadCapacityIrradiance = CreateCapacityResourceTelemetry(
                _irradianceAtlasBuffer,
                _irradianceAtlasBytes,
                plan.IrradianceAtlasBytes);
            _uploadCapacityVisibility = CreateCapacityResourceTelemetry(
                _visibilityAtlasBuffer,
                _visibilityAtlasBytes,
                plan.VisibilityAtlasBytes);
            _uploadCapacityTransportIrradiance = CreateCapacityResourceTelemetry(
                _transportIrradianceAtlasBuffer,
                _transportIrradianceAtlasBytes,
                plan.TransportIrradianceBytes);
            _uploadCapacityTransportSourceCache = CreateCapacityResourceTelemetry(
                _transportSourceCacheBuffer,
                _transportSourceCacheBytes,
                plan.TransportSourceCacheBytes);
            _uploadCapacityRayScratch = CreateCapacityResourceTelemetry(
                _rayResultScratchBuffer,
                _rayScratchBytes,
                plan.RayScratchBytes);
            _uploadCapacityProbeState = CreateCapacityResourceTelemetry(
                _probeStateBuffer,
                _probeStateBytes,
                plan.ProbeStateBytes);
            _uploadCapacityReceiverProbes = CreateCapacityResourceTelemetry(
                _receiverProbeBuffer,
                _receiverProbeBytes,
                plan.ReceiverProbeBytes);
            _uploadCapacityUpdateQueue = CreateCapacityResourceTelemetry(
                _probeUpdateQueueBuffer,
                _probeUpdateQueueBytes,
                plan.UpdateQueueBytes);
            _uploadCapacityRelocationClassification = CreateCapacityResourceTelemetry(
                _relocationClassificationBuffer,
                _relocationClassificationBytes,
                plan.RelocationClassificationBytes);
            ulong requiredReadbackBytes = readbackRequired
                ? plan.ProbeStateReadbackBytes
                : 0UL;
            _uploadCapacityReadback = new SimpleDdgiCapacityResourceTelemetry(
                _probeStateReadbackBufferBytes,
                requiredReadbackBytes,
                _probeStateReadbackBufferBytes != requiredReadbackBytes);
            ulong sampledBytes = _sampledAtlas?.IsReady == true
                ? _sampledAtlas.EstimatedImageBytes
                : 0UL;
            _uploadCapacitySampledAtlas = new SimpleDdgiCapacityResourceTelemetry(
                sampledBytes,
                plan.SampledAtlasImageBytes,
                sampledBytes != plan.SampledAtlasImageBytes);
            if (_uploadCapacitySampledAtlas.Transitioned)
                _uploadCapacityTransitionCount++;
        }

        private static SimpleDdgiCapacityResourceTelemetry CreateCapacityResourceTelemetry(
            BufferHandle handle,
            ulong provisionedBytes,
            ulong requiredBytes) => new(
                handle.IsValid ? provisionedBytes : 0UL,
                requiredBytes,
                !handle.IsValid || provisionedBytes != requiredBytes);

        private void ValidateCachedCapacityPlan(
            SimpleDdgiMemoryPlan plan,
            bool readbackRequired)
        {
            ValidateBufferCapacity(
                "irradiance atlas",
                _irradianceAtlasBuffer,
                plan.IrradianceAtlasBytes);
            ValidateBufferCapacity(
                "visibility atlas",
                _visibilityAtlasBuffer,
                plan.VisibilityAtlasBytes);
            ValidateBufferCapacity(
                "transport irradiance",
                _transportIrradianceAtlasBuffer,
                plan.TransportIrradianceBytes);
            ValidateBufferCapacity(
                "transport source cache",
                _transportSourceCacheBuffer,
                plan.TransportSourceCacheBytes);
            ValidateBufferCapacity(
                "ray scratch",
                _rayResultScratchBuffer,
                plan.RayScratchBytes);
            ValidateBufferCapacity(
                "probe state",
                _probeStateBuffer,
                plan.ProbeStateBytes);
            ValidateBufferCapacity(
                "receiver probes",
                _receiverProbeBuffer,
                plan.ReceiverProbeBytes);
            ValidateBufferCapacity(
                "update queue",
                _probeUpdateQueueBuffer,
                plan.UpdateQueueBytes);
            ValidateBufferCapacity(
                "relocation/classification",
                _relocationClassificationBuffer,
                plan.RelocationClassificationBytes);

            ulong requiredReadbackBytes = readbackRequired
                ? plan.ProbeStateReadbackBytesPerBuffer
                : 0UL;
            for (int frameIndex = 0;
                frameIndex < _probeStateReadbackBuffers.Length;
                frameIndex++)
            {
                BufferHandle handle = _probeStateReadbackBuffers[frameIndex];
                if (!readbackRequired)
                {
                    if (handle.IsValid)
                    {
                        throw new InvalidOperationException(
                            "Simple DDGI retained a readback buffer while readback is disabled.");
                    }
                    continue;
                }

                ValidateBufferCapacity(
                    $"probe-state readback frame {frameIndex}",
                    handle,
                    requiredReadbackBytes);
            }
        }

        private void ValidateBufferCapacity(
            string name,
            BufferHandle handle,
            ulong requiredBytes)
        {
            if (!handle.IsValid)
            {
                throw new InvalidOperationException(
                    $"Simple DDGI capacity '{name}' is missing; required={requiredBytes} bytes.");
            }

            long lookupStart = Stopwatch.GetTimestamp();
            ulong liveBytes = _bufferManager.GetBufferSize(handle);
            _uploadCapacityBufferSizeLookupMicroseconds +=
                ElapsedMicroseconds(lookupStart);
            _uploadCapacityBufferSizeLookupCount++;
            if (liveBytes != requiredBytes)
            {
                _uploadCapacityTransitionReason |=
                    SimpleDdgiCapacityTransitionReason.LiveResourceMismatch;
                throw new InvalidOperationException(
                    $"Simple DDGI capacity '{name}' is {liveBytes} bytes; required={requiredBytes} bytes.");
            }
        }

        private unsafe bool EnsureBuffer(
            ref BufferHandle handle,
            ref ulong currentBytes,
            ulong requiredBytes,
            string debugName,
            bool invalidateAtlas,
            CommandBuffer commandBuffer,
            bool preserveContents,
            bool destroyPreviousImmediately,
            bool forceRecreate = false)
        {
            // Capacity is part of the resolved tier contract. A quality or
            // topology transition must converge to the newly admitted size in
            // both directions; otherwise an Ultra allocation retained by a Low
            // tier would immediately violate the live hard-budget metric.
            if (handle.IsValid &&
                !forceRecreate &&
                !RequiresStableCapacityReallocation(currentBytes, requiredBytes))
                return false;

            BufferHandle previousHandle = handle;
            ulong previousBytes = currentBytes;
            if (destroyPreviousImmediately && previousHandle.IsValid)
            {
                // The caller synchronized the device once for the whole stable
                // capacity transition. Keeping history would require old and new
                // allocations to overlap, defeating hard-budget admission.
                _bufferManager.DestroyBuffer(previousHandle);
                previousHandle = default;
                previousBytes = 0UL;
                handle = default;
                currentBytes = 0UL;
                preserveContents = false;
                if (invalidateAtlas)
                {
                    _atlasClearRequired = true;
                    _atlasFresh = true;
                    _sampledAtlas?.MarkFullSyncRequired();
                }
            }

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
            bool contentsPreserved = preserveContents && previousHandle.IsValid && previousBytes > 0 && commandBuffer.Handle != 0;
            if (contentsPreserved)
            {
                ulong copyBytes = Math.Min(previousBytes, requiredBytes);
                Silk.NET.Vulkan.Buffer source = _bufferManager.GetBuffer(previousHandle);
                Silk.NET.Vulkan.Buffer destination = _bufferManager.GetBuffer(handle);
                BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                    source,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
                    AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    0,
                    copyBytes);
                ExecuteBufferBarrier(commandBuffer, beforeCopy);
                BufferCopy copy = new() { SrcOffset = 0, DstOffset = 0, Size = copyBytes };
                _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);
                BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                    destination,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                    0,
                    copyBytes);
                ExecuteBufferBarrier(commandBuffer, afterCopy);
            }

            if (previousHandle.IsValid)
                RetireBufferResource(previousHandle, previousBytes);
            if (invalidateAtlas)
            {
                if (!contentsPreserved)
                {
                    _atlasClearRequired = true;
                    _atlasFresh = true;
                }
                _sampledAtlas?.MarkFullSyncRequired();
            }

            // Descriptor writes are deliberately batched by EnsureCapacity once
            // every handle belongs to the same capacity generation.
            _uploadCapacityTransitionCount++;
            return true;
        }

        private bool TryCreateAuthoredVolume(
            GlobalIlluminationProbeVolume authored,
            int ordinal,
            out VolumeCandidate candidate)
        {
            Vector3 minimum = authored.Origin;
            Vector3 maximum = authored.Origin + authored.Size;
            Vector3 min = Min(minimum, maximum);
            Vector3 max = Max(minimum, maximum);
            Vector3 spacingVector = authored.ProbeSpacing;
            float spacing = Math.Clamp(
                MathF.Min(spacingVector.X, MathF.Min(spacingVector.Y, spacingVector.Z)),
                0.25f,
                8.0f);
            Vector3 size = max - min;
            if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
            {
                candidate = default;
                return false;
            }

            Vector3 origin = ResolveAuthoredLatticeOrigin(min, spacing, Vector3.Zero);
            int countX = ResolveAuthoredLatticeAxisCount(max.X, origin.X, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
            int countY = ResolveAuthoredLatticeAxisCount(max.Y, origin.Y, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
            int countZ = ResolveAuthoredLatticeAxisCount(max.Z, origin.Z, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
            float edgeFadeDistance = Math.Max(spacing * 1.5f, 0.001f);
            (Vector3 influenceMin, Vector3 influenceMax) = ResolveInfluenceBounds(min, max, edgeFadeDistance);
            candidate = new VolumeCandidate(
                VolumeKindAuthored,
                ordinal,
                authored.Priority,
                SimpleDdgiVolumePurpose.ReceiverHero,
                origin,
                spacing,
                countX,
                countY,
                countZ,
                influenceMin,
                influenceMax,
                edgeFadeDistance);
            return true;
        }

        internal static bool RequiresStableCapacityReallocation(
            ulong provisionedBytes,
            ulong requiredBytes) =>
            provisionedBytes != requiredBytes;

        internal static ulong ResolveTransitionMemoryLimit(
            ulong gpuMemoryBudgetBytes)
        {
            if (gpuMemoryBudgetBytes == ulong.MaxValue)
                return ulong.MaxValue;
            ulong quotient = gpuMemoryBudgetBytes / 5UL;
            ulong remainder = gpuMemoryBudgetBytes % 5UL;
            return checked(
                quotient * 4UL +
                (remainder * 4UL) / 5UL);
        }

        private void ReconcileProbeStateReadbackBuffers(
            SimpleDdgiMemoryPlan allocationPlan,
            bool readbackRequired,
            bool destroyImmediately)
        {
            ulong requiredBytes = readbackRequired
                ? allocationPlan.ProbeStateReadbackBytesPerBuffer
                : 0UL;

            for (int frameIndex = 0;
                frameIndex < _probeStateReadbackBuffers.Length;
                frameIndex++)
            {
                BufferHandle handle = _probeStateReadbackBuffers[frameIndex];
                ulong provisionedBytes =
                    _probeStateReadbackProvisionedBytes[frameIndex];
                if (readbackRequired && handle.IsValid &&
                    !RequiresStableCapacityReallocation(provisionedBytes, requiredBytes))
                {
                    continue;
                }

                bool transitioned = false;
                if (handle.IsValid)
                {
                    ReleaseProbeStateReadbackBuffer(
                        frameIndex,
                        provisionedBytes,
                        destroyImmediately);
                    transitioned = true;
                }

                if (readbackRequired)
                {
                    _probeStateReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                        requiredBytes,
                        BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessRandomBit,
                        $"Simple DDGI Probe State Readback Frame {frameIndex}",
                        MemoryBudgetCategory.GlobalIllumination);
                    _probeStateReadbackProvisionedBytes[frameIndex] = requiredBytes;
                    _probeStateReadbackBufferBytes += requiredBytes;
                    transitioned = true;
                }

                if (transitioned)
                    _uploadCapacityTransitionCount++;
            }
        }

        private void UpdateSampledAtlasCapacity(
            int physicalProbeCapacity,
            bool priorGenerationComplete,
            bool sampledAtlasAdmitted = true,
            ulong mirrorAllocationBudgetBytes = ulong.MaxValue,
            bool forceRecreate = false)
        {
            long ensureStart = Stopwatch.GetTimestamp();
            if (!SampledAtlasRequested || physicalProbeCapacity <= 0)
            {
                bool hadPublishedAtlas = _sampledAtlas?.IsReady == true;
                bool released = _sampledAtlas?.Release(
                    _resourceLastUseFrameFenceValue,
                    _completedFrameFenceValue) ?? true;
                if (hadPublishedAtlas && released &&
                    _sampledAtlas?.IsReady != true)
                {
                    AdvanceSampledAtlasPublicationGeneration();
                }
                // A release can be deferred behind older retired generations.
                // Keep the image path disabled immediately so no later command
                // records barriers or publication against the old allocation
                // after the completion token captured by Release.
                _sampledAtlasFallbackReason =
                    ResolveSampledAtlasInactiveFallbackReason(
                        SampledAtlasRequested,
                        _sampledAtlasLayout);
                _sampledAtlasFailedProbeCount = -1;
                _sampledAtlasFailureAllocationBudgetBytes = 0;
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
                return;
            }

            if (_registeredBindlessHeap == null)
            {
                _sampledAtlasFallbackReason = "sampled-atlas-bindless-heap-unavailable";
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
                return;
            }

            if (!_context.ShaderStorageImageArrayNonUniformIndexingSupported)
            {
                bool hadPublishedAtlas = _sampledAtlas?.IsReady == true;
                bool released = _sampledAtlas?.Release(
                    _resourceLastUseFrameFenceValue,
                    _completedFrameFenceValue) ?? true;
                if (hadPublishedAtlas && released &&
                    _sampledAtlas?.IsReady != true)
                {
                    AdvanceSampledAtlasPublicationGeneration();
                }
                _sampledAtlasFallbackReason =
                    "sampled-atlas-storage-image-non-uniform-indexing-unavailable";
                _sampledAtlasFailedProbeCount = physicalProbeCapacity;
                _sampledAtlasFailureAllocationBudgetBytes =
                    mirrorAllocationBudgetBytes;
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
                return;
            }

            if (_sampledAtlasFailedProbeCount == physicalProbeCapacity &&
                _sampledAtlasFailureAllocationBudgetBytes ==
                    mirrorAllocationBudgetBytes)
            {
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
                return;
            }

            if (!sampledAtlasAdmitted)
            {
                bool hadPublishedAtlas = _sampledAtlas?.IsReady == true;
                bool released = _sampledAtlas?.Release(
                    _resourceLastUseFrameFenceValue,
                    _completedFrameFenceValue) ?? true;
                if (hadPublishedAtlas && released &&
                    _sampledAtlas?.IsReady != true)
                {
                    AdvanceSampledAtlasPublicationGeneration();
                }
                _sampledAtlasFallbackReason = "sampled-atlas-would-exceed-ddgi-memory-budget";
                _sampledAtlasFailedProbeCount = physicalProbeCapacity;
                _sampledAtlasFailureAllocationBudgetBytes =
                    mirrorAllocationBudgetBytes;
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
                return;
            }

            try
            {
                _sampledAtlas ??= new SimpleDdgiSampledAtlas(
                    _context,
                    _recordRuntimeStall);
                bool hadPublishedAtlas = _sampledAtlas.IsReady;
                ulong previousGeneration = _sampledAtlas.AllocationGeneration;
                if (_sampledAtlas.EnsureCapacity(
                        physicalProbeCapacity,
                        _resourceLastUseFrameFenceValue,
                        _completedFrameFenceValue,
                        priorGenerationComplete,
                        forceRecreate))
                {
                    if (_sampledAtlas.AllocatedImageBytes >
                        mirrorAllocationBudgetBytes)
                    {
                        // A newly-created generation has not been registered or
                        // submitted and can be destroyed immediately. A stable
                        // generation can reach this branch when only the budget
                        // changes; it must retain its real last-use fence.
                        ulong rejectionFenceValue =
                            ResolveSampledAtlasBudgetRejectionFence(
                                previousGeneration,
                                _sampledAtlas.AllocationGeneration,
                                _resourceLastUseFrameFenceValue);
                        bool released =
                            _sampledAtlas.Release(
                                lastUseFrameFenceValue: rejectionFenceValue,
                                completedFrameFenceValue: _completedFrameFenceValue);
                        if (released && !_sampledAtlas.IsReady)
                            AdvanceSampledAtlasPublicationGeneration();
                        _sampledAtlasFallbackReason =
                            "sampled-atlas-actual-allocation-exceeds-ddgi-memory-budget";
                        // This budget/device-size pair cannot become admissible
                        // merely by waiting. Latch it even while retirement is
                        // pending so an unrelated capacity-key change cannot
                        // cancel the deferred release through a stable ensure.
                        _sampledAtlasFailedProbeCount = physicalProbeCapacity;
                        _sampledAtlasFailureAllocationBudgetBytes =
                            mirrorAllocationBudgetBytes;
                        return;
                    }
                    if (_sampledAtlas.AllocationGeneration != previousGeneration)
                    {
                        _sampledAtlas.Register(_registeredBindlessHeap);
                        AdvanceSampledAtlasPublicationGeneration();
                    }
                    _sampledAtlasFallbackReason = string.Empty;
                    _sampledAtlasFailedProbeCount = -1;
                    _sampledAtlasFailureAllocationBudgetBytes = 0;
                    return;
                }

                _sampledAtlasFallbackReason = string.IsNullOrWhiteSpace(_sampledAtlas.LastFailureReason)
                    ? "sampled-atlas-allocation-unavailable"
                    : _sampledAtlas.LastFailureReason;
                bool releasePendingPublishedAtlas =
                    hadPublishedAtlas && _sampledAtlas.IsReady;
                bool releasedPublishedAtlas = !releasePendingPublishedAtlas ||
                    _sampledAtlas.Release(
                        _resourceLastUseFrameFenceValue,
                        _completedFrameFenceValue);
                if (hadPublishedAtlas && releasedPublishedAtlas &&
                    !_sampledAtlas.IsReady)
                {
                    AdvanceSampledAtlasPublicationGeneration();
                }
                bool retryAfterCompletion =
                    _sampledAtlasFallbackReason.Contains(
                        "retirement",
                        StringComparison.Ordinal) ||
                    !releasedPublishedAtlas;
                _sampledAtlasFailedProbeCount = retryAfterCompletion
                    ? -1
                    : physicalProbeCapacity;
                _sampledAtlasFailureAllocationBudgetBytes =
                    mirrorAllocationBudgetBytes;
            }
            catch (VulkanException exception)
            {
                bool hadPublishedAtlas = _sampledAtlas?.IsReady == true;
                bool released = _sampledAtlas?.Release(
                    _resourceLastUseFrameFenceValue,
                    _completedFrameFenceValue) ?? true;
                if (hadPublishedAtlas && released &&
                    _sampledAtlas?.IsReady != true)
                {
                    AdvanceSampledAtlasPublicationGeneration();
                }
                _sampledAtlasFallbackReason = $"sampled-atlas-vulkan-{exception.Result}";
                _sampledAtlasFailedProbeCount = released
                    ? physicalProbeCapacity
                    : -1;
                _sampledAtlasFailureAllocationBudgetBytes =
                    mirrorAllocationBudgetBytes;
            }
            finally
            {
                _uploadCapacitySampledAtlasEnsureMicroseconds +=
                    ElapsedMicroseconds(ensureStart);
            }
        }

        internal static ulong ResolveSampledAtlasAllocationBudget(
            ulong totalDdgiBudgetBytes,
            in SimpleDdgiMemoryPlan plan)
        {
            if (totalDdgiBudgetBytes == 0UL ||
                totalDdgiBudgetBytes == ulong.MaxValue)
            {
                return ulong.MaxValue;
            }

            if (plan.SampledAtlasImageBytes > plan.LiveBytes)
            {
                throw new InvalidOperationException(
                    "Simple-DDGI sampled image bytes exceed the complete memory plan.");
            }

            ulong nonMirrorBytes = plan.LiveBytes - plan.SampledAtlasImageBytes;
            return totalDdgiBudgetBytes > nonMirrorBytes
                ? totalDdgiBudgetBytes - nonMirrorBytes
                : 0UL;
        }

        internal static string ResolveSampledAtlasInactiveFallbackReason(
            bool sampledAtlasRequested,
            SimpleDdgiSampledAtlasLayout layout)
        {
            if (!sampledAtlasRequested)
                return string.Empty;
            if (layout != null &&
                !string.IsNullOrWhiteSpace(layout.FallbackReason))
            {
                return layout.FallbackReason;
            }
            return "sampled-atlas-no-admitted-ranges";
        }

        internal static ulong ResolveSampledAtlasBudgetRejectionFence(
            ulong previousAllocationGeneration,
            ulong currentAllocationGeneration,
            ulong currentLastUseFrameFenceValue) =>
            currentAllocationGeneration != previousAllocationGeneration
                ? 0UL
                : currentLastUseFrameFenceValue;

        private void AdvanceSampledAtlasPublicationGeneration()
        {
            _sampledAtlasPublicationGeneration++;
            if (_sampledAtlasPublicationGeneration == 0UL)
                _sampledAtlasPublicationGeneration = 1UL;
        }

        private unsafe bool WouldExceedSampledAtlasBudget(int probeCount, ulong configuredBudgetBytes)
        {
            if (configuredBudgetBytes == 0)
                return false;

            // Image capacity grows in fixed probe quanta, whereas the canonical
            // SSBOs grow to the exact requested probe count. Derive the actual
            // next image capacity here instead of relying on a tail estimate;
            // that keeps the configured DDGI limit a hard admission control at
            // every 256-probe boundary.
            int layersPerTexture;
            if (_sampledAtlas?.IsReady == true)
            {
                layersPerTexture = _sampledAtlas.LayersPerTexture;
            }
            else
            {
                PhysicalDeviceProperties properties = default;
                _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
                layersPerTexture = SimpleDdgiSampledAtlas.CalculateLayersPerTexture(
                    properties.Limits.MaxImageArrayLayers);
            }

            int provisionedProbeCapacity = SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(
                probeCount,
                layersPerTexture);
            ulong requiredImageBytes = SimpleDdgiSampledAtlas.CalculateEstimatedImageBytesForProbeCapacity(
                provisionedProbeCapacity);
            // EnsureCapacity performs an idle, destroy, then exact recreate when
            // the provisioned capacity changes. Charge the post-transition
            // capacity, not a stale high-water image that will not coexist with
            // the replacement.
            return checked(BufferBytes + requiredImageBytes) >
                configuredBudgetBytes;
        }

        private void SynchronizeSampledAtlasIfRequired(CommandBuffer commandBuffer)
        {
            if (!SampledAtlasActive || _sampledAtlas?.RequiresFullSync != true)
                return;

            _sampledAtlas.CopyRanges(
                commandBuffer,
                _bufferManager.GetBuffer(_irradianceAtlasBuffer),
                _irradianceAtlasBytes,
                _bufferManager.GetBuffer(_visibilityAtlasBuffer),
                _visibilityAtlasBytes,
                _sampledAtlasLayout);
        }

        // Toroidal scrolling preserves atlas and probe-state ownership by keeping
        // their physical slots fixed. There is no atlas copy: newly exposed cells
        // are invalidated in MarkFreshForNewOrScrolledProbes and rebootstrap in
        // place, while all overlap state stays GPU-owned.
        private void PreserveToroidalAtlasData()
        {
            if (!_settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled ||
                !_recenteredThisFrame || _atlasClearRequired || _atlasFresh)
            {
                return;
            }

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                if (!TryGetPreviousMatchingVolume(volumeIndex, current, out GPUSimpleDdgiVolume previous) ||
                    !TryResolveCellDelta(previous, current, out int deltaX, out int deltaY, out int deltaZ) ||
                    (deltaX == 0 && deltaY == 0 && deltaZ == 0) ||
                    Math.Abs((long)deltaX) >= CountX(current) ||
                    Math.Abs((long)deltaY) >= CountY(current) ||
                    Math.Abs((long)deltaZ) >= CountZ(current))
                {
                    continue;
                }

                _atlasPreservedOnRecenterThisFrame = true;
                _totalAtlasPreserveOnRecenterCount++;
                // Retain the published scroll telemetry name for capture
                // compatibility; it now counts zero-copy toroidal preserves.
                _scrollCopyCount++;
                return;
            }
        }

        private unsafe void ClearAtlasBuffersIfRequired(CommandBuffer commandBuffer)
        {
            if (!_atlasClearRequired)
                return;

            _receiverProbeClearRequired = true;
            _atlasClearedThisFrame = true;
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[4];
            uint barrierCount = 0;
            FillBufferAndAddBarrier(_irradianceAtlasBuffer, _irradianceAtlasBytes, barriers, ref barrierCount, commandBuffer);
            FillBufferAndAddBarrier(_transportIrradianceAtlasBuffer, _transportIrradianceAtlasBytes, barriers, ref barrierCount, commandBuffer);
            FillBufferAndAddBarrier(_visibilityAtlasBuffer, _visibilityAtlasBytes, barriers, ref barrierCount, commandBuffer);
            FillBufferAndAddBarrier(_transportSourceCacheBuffer, _transportSourceCacheBytes, barriers, ref barrierCount, commandBuffer);
            if (barrierCount > 0)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = barrierCount,
                    PBufferMemoryBarriers = barriers
                };
                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
            }

            _atlasClearRequired = false;
            _atlasFresh = true;
            InvalidateTransportSourceCacheMetadata();
            _sampledAtlas?.MarkFullSyncRequired();
            TriggerAtlasRecoveryBoost();
        }

        private void TriggerAtlasRecoveryBoost()
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (!gi.SimpleDdgiLightingDirtyBoostEnabled || _probeCount <= 0)
                return;

            _lightingDirtyFrames = Math.Max(_lightingDirtyFrames, gi.SimpleDdgiLightingDirtyFrameCount);
            _activeDirtyReasonFlags |= 1u << 2;
        }

        private void InvalidateTransportSourceCacheMetadata(
            bool recordInvalidation = true,
            SimpleDdgiTransportCertificationReason certificationReason =
                SimpleDdgiTransportCertificationReason.SourceRepairRequired,
            SimpleDdgiTransportRecoveryAction recoveryAction =
                SimpleDdgiTransportRecoveryAction.None)
        {
            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                // The resident arena is the source-age/epoch authority.  The
                // CPU mirrors are deliberately not cleared or walked here;
                // classify observes the new source generation and commit moves
                // the GPU state forward after a matching transaction.
                BeginTransportGlobalConvergence(
                    forceFieldEvidenceReset: true,
                    certificationReason: certificationReason,
                    recoveryAction: recoveryAction);
                if (recordInvalidation)
                {
                    _sourceCacheInvalidationCount = SaturatingAdd(
                        _sourceCacheInvalidationCount,
                        (ulong)Math.Max(_probeCount, 0));
                }
                return;
            }
            if (_probeSourceLightingGenerations.Length > 0)
                Array.Clear(_probeSourceLightingGenerations);
            if (_probeLastSourceRefreshFrames.Length > 0)
                Array.Clear(_probeLastSourceRefreshFrames);
            for (int probeIndex = 0; probeIndex < _probeSourceEpochs.Length; probeIndex++)
            {
                AdvanceProbeSourceEpoch(probeIndex);
            }
            if (_probeSourceRayCounts.Length > 0)
                Array.Clear(_probeSourceRayCounts);
            if (_probeTransportGenerationCounts.Length > 0)
                Array.Clear(_probeTransportGenerationCounts);
            BeginTransportGlobalConvergence(
                forceFieldEvidenceReset: true,
                certificationReason: certificationReason,
                recoveryAction: recoveryAction);
            if (recordInvalidation)
            {
                _sourceCacheInvalidationCount = SaturatingAdd(
                    _sourceCacheInvalidationCount,
                    (ulong)Math.Max(_probeCount, 0));
            }
        }

        private unsafe void FillBufferAndAddBarrier(BufferHandle handle, ulong size, BufferMemoryBarrier2* barriers, ref uint barrierCount, CommandBuffer commandBuffer)
        {
            if (!handle.IsValid || size == 0)
                return;

            Silk.NET.Vulkan.Buffer buffer = _bufferManager.GetBuffer(handle);
            _context.Api.CmdFillBuffer(commandBuffer, buffer, 0, size, 0u);
            barriers[barrierCount++] = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = size
            };
        }

        private void RegisterIfValid(int index, BufferHandle handle, ulong size)
        {
            if (_registeredBindlessHeap == null || !handle.IsValid)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(index, _bufferManager.GetBuffer(handle), 0, Math.Max(MinBufferSize, size));
        }

        private static BoundingBox ExpandBounds(BoundingBox bounds, float padding)
        {
            Vector3 p = new(Math.Max(padding, 0.0f));
            return new BoundingBox(bounds.Min - p, bounds.Max + p);
        }

        internal static Vector3 ResolveSceneClampedOrigin(
            Vector3 sceneMin,
            Vector3 sceneMax,
            Vector3 latticeSize,
            float spacing,
            Vector3 cameraPosition,
            Vector3 currentOrigin,
            ref bool hasCurrentOrigin,
            out bool recentered,
            float verticalHysteresisFraction = 0.25f)
        {
            Vector3 desiredOrigin = ResolveDesiredSceneClampedOrigin(sceneMin, sceneMax, latticeSize, spacing, cameraPosition);
            if (!hasCurrentOrigin)
            {
                hasCurrentOrigin = true;
                recentered = false;
                return desiredOrigin;
            }

            // The initial origin is the ring's lattice anchor.  Every subsequent
            // origin must remain an integer number of cells from that anchor or the
            // toroidal atlas/state history no longer describes the same world cells.
            // Resolve axes independently so movement on one axis cannot shift a
            // covered or still-centered axis as a side effect.
            Vector3 alignedOrigin = new(
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, latticeSize.X, spacing, cameraPosition.X, currentOrigin.X, 0.25f),
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.Y, sceneMax.Y, latticeSize.Y, spacing, cameraPosition.Y, currentOrigin.Y, verticalHysteresisFraction),
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.Z, sceneMax.Z, latticeSize.Z, spacing, cameraPosition.Z, currentOrigin.Z, 0.25f));
            recentered = !ApproximatelyEqual(currentOrigin, alignedOrigin);
            return recentered ? alignedOrigin : currentOrigin;
        }

        private static float ResolveAlignedSceneClampedAxisOrigin(
            float sceneMin,
            float sceneMax,
            float latticeExtent,
            float spacing,
            float cameraPosition,
            float currentOrigin,
            float recenterHysteresisFraction)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            bool latticeCoversScene = sceneExtent <= latticeExtent;
            float oppositeBoundaryOrigin = sceneMax - latticeExtent;
            float allowedOriginMin = Math.Min(sceneMin, oppositeBoundaryOrigin);
            float allowedOriginMax = Math.Max(sceneMin, oppositeBoundaryOrigin);
            bool originInsideAllowedInterval = currentOrigin >= allowedOriginMin - 0.0001f &&
                currentOrigin <= allowedOriginMax + 0.0001f;
            float quarterExtent = latticeExtent * Math.Clamp(recenterHysteresisFraction, 0.0f, 0.49f);
            bool cameraRequiresRecenter = !latticeCoversScene && ShouldRecenterAxis(
                    cameraPosition,
                    currentOrigin + quarterExtent,
                    currentOrigin + latticeExtent - quarterExtent,
                    latticeExtent,
                    sceneMin,
                    sceneMax);
            if (originInsideAllowedInterval && !cameraRequiresRecenter)
                return currentOrigin;

            // Initial placement may have an arbitrary scene-boundary phase.  Use a
            // raw target here and quantize exactly once relative to that established
            // lattice, avoiding alternating slab sizes from two independent snaps.
            float desiredOrigin = latticeCoversScene
                ? sceneMin - Math.Max(latticeExtent - sceneExtent, 0.0f) * 0.5f
                : Math.Clamp(cameraPosition - latticeExtent * 0.5f, sceneMin, oppositeBoundaryOrigin);
            float safeSpacing = Math.Max(spacing, 0.001f);
            float requestedCellDelta = MathF.Round(
                (desiredOrigin - currentOrigin) / safeSpacing,
                MidpointRounding.AwayFromZero);
            if (!float.IsFinite(requestedCellDelta))
                return currentOrigin;

            // Prefer an aligned origin fully inside the scene bounds.  If a dynamic
            // bound leaves no aligned cell in that interval, retain lattice
            // coherence and accept less than one cell of overscan.
            float minimumCellDelta = MathF.Ceiling((allowedOriginMin - currentOrigin) / safeSpacing - 0.0001f);
            float maximumCellDelta = MathF.Floor((allowedOriginMax - currentOrigin) / safeSpacing + 0.0001f);
            if (float.IsFinite(minimumCellDelta) &&
                float.IsFinite(maximumCellDelta) &&
                minimumCellDelta <= maximumCellDelta)
            {
                requestedCellDelta = Math.Clamp(requestedCellDelta, minimumCellDelta, maximumCellDelta);
            }

            float alignedOrigin = currentOrigin + requestedCellDelta * safeSpacing;
            return float.IsFinite(alignedOrigin) ? alignedOrigin : currentOrigin;
        }

        private static Vector3 ResolveDesiredSceneClampedOrigin(Vector3 sceneMin, Vector3 sceneMax, Vector3 latticeSize, float spacing, Vector3 cameraPosition)
        {
            return new Vector3(
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, latticeSize.X, spacing, cameraPosition.X),
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.Y, sceneMax.Y, latticeSize.Y, spacing, cameraPosition.Y),
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.Z, sceneMax.Z, latticeSize.Z, spacing, cameraPosition.Z));
        }

        private static float ResolveDesiredSceneClampedAxisOrigin(float sceneMin, float sceneMax, float latticeExtent, float spacing, float cameraPosition)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            if (sceneExtent <= latticeExtent)
                return sceneMin - Math.Max(latticeExtent - sceneExtent, 0.0f) * 0.5f;

            float maxOrigin = sceneMax - latticeExtent;
            if (maxOrigin < sceneMin)
                return sceneMin - Math.Max(latticeExtent - sceneExtent, 0.0f) * 0.5f;

            float target = SnapScalar(cameraPosition - latticeExtent * 0.5f, spacing);
            return Math.Clamp(target, sceneMin, maxOrigin);
        }

        private static bool ShouldRecenterAxis(float cameraPosition, float innerMin, float innerMax, float latticeExtent, float sceneMin, float sceneMax)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            return sceneExtent > latticeExtent && (cameraPosition < innerMin || cameraPosition > innerMax);
        }

        private static float SnapScalar(float value, float spacing)
        {
            float s = Math.Max(spacing, 0.001f);
            return MathF.Floor(value / s) * s;
        }

        internal static Vector3 ResolveAuthoredLatticeOrigin(Vector3 minimum, float spacing, Vector3 latticePhase)
        {
            float safeSpacing = Math.Max(spacing, 0.001f);
            Vector3 phase = new(
                NormalizeLatticePhase(latticePhase.X),
                NormalizeLatticePhase(latticePhase.Y),
                NormalizeLatticePhase(latticePhase.Z));
            Vector3 phaseOffset = phase * safeSpacing;
            return SnapVector(minimum - phaseOffset, safeSpacing) + phaseOffset;
        }

        /// <summary>
        /// Converts the probe lattice's guaranteed-coverage box into its selection
        /// domain. The transition band lives outside the core box, so receivers
        /// authored on a wall/floor boundary retain full ownership instead of
        /// fading the very volume intended to light them.
        /// </summary>
        internal static (Vector3 Min, Vector3 Max) ResolveInfluenceBounds(
            Vector3 coreMinimum,
            Vector3 coreMaximum,
            float edgeFadeDistance)
        {
            Vector3 minimum = Min(coreMinimum, coreMaximum);
            Vector3 maximum = Max(coreMinimum, coreMaximum);
            float expansion = float.IsFinite(edgeFadeDistance)
                ? Math.Max(edgeFadeDistance, 0.0f)
                : 0.0f;
            Vector3 margin = new(expansion);
            return (minimum - margin, maximum + margin);
        }

        private static int ResolveAuthoredLatticeAxisCount(float maximum, float origin, float spacing, int maximumCount)
        {
            float safeSpacing = Math.Max(spacing, 0.001f);
            float extent = Math.Max(maximum - origin, 0.0f);
            return Math.Clamp((int)MathF.Ceiling(extent / safeSpacing) + 1, 2, maximumCount);
        }

        private static float NormalizeLatticePhase(float value)
        {
            if (!float.IsFinite(value))
                return 0.0f;

            return value - MathF.Floor(value);
        }

        private static Vector3 SnapVector(Vector3 value, float spacing) =>
            new(SnapScalar(value.X, spacing), SnapScalar(value.Y, spacing), SnapScalar(value.Z, spacing));

        private static Vector3 LatticeSize(int countX, int countY, int countZ, float spacing) =>
            new(Math.Max(countX - 1, 1) * spacing, Math.Max(countY - 1, 1) * spacing, Math.Max(countZ - 1, 1) * spacing);

        private static Vector3 Origin(GPUSimpleDdgiVolume volume) =>
            new(volume.OriginAndSpacing.X, volume.OriginAndSpacing.Y, volume.OriginAndSpacing.Z);

        private static float Spacing(GPUSimpleDdgiVolume volume) =>
            Math.Max(volume.OriginAndSpacing.W, 0.001f);

        private static int CountX(GPUSimpleDdgiVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.X));

        private static int CountY(GPUSimpleDdgiVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Y));

        private static int CountZ(GPUSimpleDdgiVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Z));

        private static int FirstProbe(GPUSimpleDdgiVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.GridCountsAndFirstProbe.W));

        private static int VolumeProbeCount(GPUSimpleDdgiVolume volume) =>
            checked(CountX(volume) * CountY(volume) * CountZ(volume));

        internal static int CalculatePhysicalProbeLocalIndex(GPUSimpleDdgiVolume volume, int logicalX, int logicalY, int logicalZ)
        {
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int physicalX = PositiveModulo(logicalX + PhysicalOffsetX(volume), countX);
            int physicalY = PositiveModulo(logicalY + PhysicalOffsetY(volume), countY);
            int physicalZ = PositiveModulo(logicalZ + PhysicalOffsetZ(volume), countZ);
            return physicalX + physicalY * countX + physicalZ * countX * countY;
        }

        internal static (int X, int Y, int Z) CalculateLogicalProbeCoordinate(GPUSimpleDdgiVolume volume, int physicalLocalIndex)
        {
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int plane = checked(countX * countY);
            int physicalZ = Math.Clamp(physicalLocalIndex / plane, 0, countZ - 1);
            int remaining = Math.Max(0, physicalLocalIndex - physicalZ * plane);
            int physicalY = Math.Clamp(remaining / countX, 0, countY - 1);
            int physicalX = Math.Clamp(remaining - physicalY * countX, 0, countX - 1);
            return (
                PositiveModulo(physicalX - PhysicalOffsetX(volume), countX),
                PositiveModulo(physicalY - PhysicalOffsetY(volume), countY),
                PositiveModulo(physicalZ - PhysicalOffsetZ(volume), countZ));
        }

        private static int PhysicalOffsetX(GPUSimpleDdgiVolume volume) =>
            PositiveModulo((int)MathF.Round(volume.RaysAndReserved.Y), CountX(volume));

        private static int PhysicalOffsetY(GPUSimpleDdgiVolume volume) =>
            PositiveModulo((int)MathF.Round(volume.RaysAndReserved.Z), CountY(volume));

        private static int PhysicalOffsetZ(GPUSimpleDdgiVolume volume) =>
            PositiveModulo((int)MathF.Round(volume.RaysAndReserved.W), CountZ(volume));

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 1)
                return 0;
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static uint NormalizeProbeGeneration(uint generation)
        {
            uint normalized = generation & ProbeStateGenerationValueMask;
            return normalized == 0u ? 1u : normalized;
        }

        private static uint AdvanceProbeGeneration(uint generation)
        {
            uint next = (NormalizeProbeGeneration(generation) + 1u) & ProbeStateGenerationValueMask;
            return next == 0u ? 1u : next;
        }

        private static uint AdvanceSourceLightingGeneration(uint generation)
        {
            uint next = generation + 1u;
            return next == 0u ? 1u : next;
        }

        private static uint AdvanceSourceEpoch(uint epoch)
        {
            uint next = epoch + 1u;
            return next == 0u ? 1u : next;
        }

        private void SetProbeSourceEpoch(int probeIndex, uint sourceEpoch)
        {
            if ((uint)probeIndex >= (uint)_probeSourceEpochs.Length)
                return;

            uint normalized = sourceEpoch == 0u ? 1u : sourceEpoch;
            if (_probeSourceEpochs[probeIndex] == normalized)
                return;

            _probeSourceEpochs[probeIndex] = normalized;
            _sourceEpochGeneration = AdvanceSourceEpoch(_sourceEpochGeneration);
        }

        private void AdvanceProbeSourceEpoch(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeSourceEpochs.Length)
                return;

            SetProbeSourceEpoch(
                probeIndex,
                AdvanceSourceEpoch(_probeSourceEpochs[probeIndex]));
        }

        private static uint PackProbeStateFlags(uint flags, uint generation) =>
            (flags & ~((uint)ProbeStateGenerationValueMask << ProbeStateGenerationShift)) |
            (NormalizeProbeGeneration(generation) << ProbeStateGenerationShift);

        private static uint ReadProbeStateGeneration(uint flags) =>
            NormalizeProbeGeneration((flags >> ProbeStateGenerationShift) & ProbeStateGenerationValueMask);

        private static uint Kind(GPUSimpleDdgiVolume volume) =>
            (uint)Math.Max(0, (int)MathF.Round(volume.WorldMaxAndKind.W));

        private static int SourceOrdinal(GPUSimpleDdgiVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.RaysAndReserved.X));

        private static int CountInactiveProbes(byte[] inactive, int probeCount)
        {
            return CountInactiveProbes(inactive, 0, probeCount);
        }

        private static int CountInactiveProbes(byte[] inactive, int firstProbe, int probeCount)
        {
            if (firstProbe < 0 || probeCount <= 0)
                return 0;

            int count = 0;
            int start = Math.Clamp(firstProbe, 0, inactive.Length);
            int end = Math.Min(inactive.Length, start + Math.Max(probeCount, 0));
            for (int i = start; i < end; i++)
            {
                if (inactive[i] != 0)
                    count++;
            }

            return count;
        }

        private static Vector3 Min(Vector3 left, Vector3 right) =>
            new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

        private static Vector3 Max(Vector3 left, Vector3 right) =>
            new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));

        private static bool ApproximatelyEqual(Vector3 left, Vector3 right)
        {
            const float epsilon = 0.0001f;
            return NearlyEqual(left.X, right.X, epsilon) &&
                NearlyEqual(left.Y, right.Y, epsilon) &&
                NearlyEqual(left.Z, right.Z, epsilon);
        }

        private static bool NearlyEqual(float left, float right, float epsilon) =>
            MathF.Abs(left - right) <= epsilon;

        internal static uint ResolveSimpleDdgiDebugViewMode(GlobalIlluminationDebugView debugView)
        {
            GlobalIlluminationDebugView effectiveView =
                RendererBuildFeatures.ResolveGlobalIlluminationDebugView(debugView);
            return effectiveView == GlobalIlluminationDebugView.DdgiSourceCacheRadiance
                ? SourceCacheRadianceDebugViewMode
                : (uint)effectiveView;
        }

        private GPUSimpleDdgiParams CreateDisabledParams(GlobalIlluminationSettings settings)
        {
            return new GPUSimpleDdgiParams
            {
                GridOriginAndSpacing = new Vector4(0.0f, 0.0f, 0.0f, Math.Max(settings.SimpleDdgiProbeSpacing, 0.001f)),
                GridCountsAndProbeCount = Vector4.Zero,
                AtlasTexelsAndRayCount = new Vector4(IrradianceTexelsPerProbe, VisibilityTexelsPerProbe, Math.Max(settings.SimpleDdgiRaysPerProbe, 1), settings.FarFieldClipmapResolution),
                HysteresisFrameAndFlags = new Vector4(
                    0.0f,
                    PackHeaderWord(_frameIndex),
                    PackHeaderWord(0u),
                    settings.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = Vector4.Zero,
                ProbeUpdateRange = Vector4.Zero,
                DebugAndBias = new Vector4(ResolveSimpleDdgiDebugViewMode(settings.DebugView), settings.DdgiSelfShadowBiasScale, settings.IndirectIntensity, settings.FarFieldMaxTraceSteps),
                RotationQuaternion = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                BiasAndPadding = new Vector4(settings.SimpleDdgiNormalBias, settings.SimpleDdgiViewBias, settings.SimpleDdgiHysteresisChangeThreshold, settings.SimpleDdgiHysteresisStepThreshold),
                Reserved0 = Vector4.Zero,
                BiasLimitsAndPadding = new Vector4(
                    settings.SimpleDdgiMaximumWorldBiasMeters,
                    settings.SimpleDdgiArchitecturalThicknessMeters,
                    settings.DdgiThinWallPolicyEnabled
                        ? settings.DdgiThinWallLeakClampStrength
                        : 0.0f,
                    0.0f),
                TransportAndAtlasIndices = new Vector4(
                    PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    0.0f,
                    PackHeaderWord(_transportGeneration)),
                TransportControls = new Vector4(
                    settings.SimpleDdgiTransportSolverRelaxation,
                    settings.SimpleDdgiTransportAlbedoClamp,
                    settings.SimpleDdgiTransportTailRelativeTolerance,
                    settings.SimpleDdgiTransportAcceleratedSweepCount),
                ResidencyAndCounts = BuildResidencyAndCounts(),
                ResidencyControls = BuildResidencyControls()
            };
        }

        private Vector4 BuildResidencyAndCounts()
        {
            SimpleDdgiMemoryPlan plan = _capacityPlan;
            uint generation = plan.ResidencyMode.CollectsDemand() &&
                _probePageCache.IsReady
                    ? _probePageCache.ResourceGeneration
                    : 0u;
            return new Vector4(
                PackHeaderWord((uint)
                    BindlessIndex.SimpleDdgiResidencyArenaBuffer),
                PackHeaderWord(checked((uint)Math.Max(
                    0,
                    plan.SparseVirtualPageCount))),
                PackHeaderWord(checked((uint)Math.Max(
                    0,
                    plan.PhysicalProbeCapacity))),
                PackHeaderWord(generation));
        }

        private Vector4 BuildResidencyControls()
        {
            SimpleDdgiMemoryPlan plan = _capacityPlan;
            uint flags = 0u;
            if (_probePageCache.IsReady &&
                !_probePageCache.BootstrapRequired)
            {
                flags |= 1u << 0;
            }
            if (_probePageCache.Frozen)
                flags |= 1u << 1;
            if (_probePageCache.ResidencyValid)
                flags |= 1u << 2;
            if (_context.ValidationSettings.Mode != RendererValidationMode.Off)
                flags |= 1u << 3;
            return new Vector4(
                PackHeaderWord(checked((uint)plan.ResidencyMode)),
                PackHeaderWord(checked((uint)Math.Max(
                    0,
                    plan.DensePayloadProbeCount))),
                PackHeaderWord(checked((uint)Math.Max(
                    0,
                    plan.SparsePhysicalPageCapacity))),
                PackHeaderWord(flags));
        }

        internal static float PackHeaderWord(uint value) => BitConverter.UInt32BitsToSingle(value);

        private uint BuildFlags(
            GlobalIlluminationSettings settings,
            bool enabled,
            bool structuredGatherAvailable,
            bool farFieldCoverageAvailable)
        {
            uint flags = enabled ? 1u : 0u;
            if (farFieldCoverageAvailable)
                flags |= 1u << 1;
            if (farFieldCoverageAvailable && settings.FarFieldForceAll)
                flags |= 1u << 2;
            if (settings.SimpleDdgiFogEnabled)
                flags |= 1u << 3;
            if (settings.SimpleDdgiParticlesEnabled)
                flags |= 1u << 4;
            if (settings.SimpleDdgiAdaptiveHysteresisEnabled)
                flags |= 1u << 5;
            if (_lightingDirtyFrames > 0)
                flags |= 1u << 10;
            if (settings.SimpleDdgiTransportV2Enabled)
                flags |= 1u << 11;
            SimpleDdgiStoragePackingMode storageMode =
                settings.SimpleDdgiStoragePackingMode.Sanitize();
            if (storageMode != SimpleDdgiStoragePackingMode.Legacy)
                flags |= DirectionCodebookFlag;
            if (storageMode == SimpleDdgiStoragePackingMode.Validate)
                flags |= DirectionValidationFlag;
            if (settings.SimpleDdgiThinSurfaceTransmissionEnabled)
                flags |= ThinSurfaceTransmissionFlag;
            if (settings.SimpleDdgiForceLegacyFarFieldFallbackEvaluation)
                flags |= ForceLegacyFarFieldFallbackEvaluationFlag;
            if (farFieldCoverageAvailable && settings.FarFieldSkyVisibilityEnabled)
                flags |= 1u << 6;
            if (farFieldCoverageAvailable && settings.FarFieldSunShadowEnabled)
                flags |= 1u << 7;
            // A previously populated atlas is not authoritative once the ray-query
            // backend is unavailable.  Leave the volume table intact so recovery is
            // cheap, but withhold the structured-gather bit: forward shading then
            // takes its explicit environment/IBL fallback instead of sampling data
            // whose generation can no longer be advanced or validated.
            if (settings.SimpleDdgiStructuredGatherEnabled && structuredGatherAvailable)
                flags |= 1u << 8;
            // Detailed ray/atlas atomics are intentionally opt-in.  The normal
            // production path retains the lightweight CPU/GPU timing telemetry,
            // while investigation counters are enabled only for an explicit
            // diagnostics capture or an active GI debug view.
            if (RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
                (_settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                    RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                        settings.DebugView) != GlobalIlluminationDebugView.None))
            {
                flags |= 1u << 9;
            }
            uint earlyOutThreshold = checked((uint)Math.Clamp(
                (int)MathF.Round(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold * 255.0f),
                0,
                255));
            flags = (flags & ~SecondVolumeOwnershipEarlyOutThresholdMask) |
                (earlyOutThreshold << SecondVolumeOwnershipEarlyOutThresholdShift);
            return flags;
        }

        private static Vector4 BuildFrameRotation(uint frameIndex)
        {
            float u1 = HashToUnitFloat(frameIndex, 0x9e3779b9u);
            float u2 = HashToUnitFloat(frameIndex, 0x7f4a7c15u);
            float u3 = HashToUnitFloat(frameIndex, 0x94d049bbu);
            float r1 = MathF.Sqrt(Math.Max(0.0f, 1.0f - u1));
            float r2 = MathF.Sqrt(Math.Max(0.0f, u1));
            float theta1 = 2.0f * MathF.PI * u2;
            float theta2 = 2.0f * MathF.PI * u3;
            return new Vector4(r1 * MathF.Sin(theta1), r1 * MathF.Cos(theta1), r2 * MathF.Sin(theta2), r2 * MathF.Cos(theta2));
        }

        private static float HashToUnitFloat(uint frameIndex, uint salt)
        {
            uint x = frameIndex ^ salt;
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x >> 8) * (1.0f / 16777216.0f);
        }

        public unsafe void RecordProbeStateReadback(CommandBuffer commandBuffer, int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            DropProbeStateReadbackSlot(frameIndex);
            bool classificationFeedbackEnabled =
                _settings.GlobalIllumination.SimpleDdgiClassificationReadbackEnabled;
            if (!RequiresProbeStateReadback(
                    classificationFeedbackEnabled,
                    TransportV2Active) ||
                commandBuffer.Handle == 0 ||
                !_probeStateBuffer.IsValid ||
                _probeCount <= 0)
                return;

            ulong stateCopyBytes = checked((ulong)_probeCount * ProbeStateStride);
            int classificationCapacity = classificationFeedbackEnabled &&
                _relocationClassificationBuffer.IsValid
                    ? Math.Min(
                        _probeCount,
                        SimpleDdgiMemoryPlan.ClassificationReadbackProbeCapacity)
                    : 0;
            int classificationFirstProbe = classificationCapacity > 0
                ? Math.Clamp(_probeClassificationReadbackCursor, 0, _probeCount - 1)
                : 0;
            int classificationProbeCount = classificationCapacity > 0
                ? Math.Min(classificationCapacity, _probeCount - classificationFirstProbe)
                : 0;
            ulong classificationCopyBytes = checked(
                (ulong)classificationProbeCount * RelocationClassificationStride);
            ulong copyBytes = checked(stateCopyBytes + classificationCopyBytes);
            // Capacity is the admitted bounded plan even when classification is
            // temporarily disabled. Keeping the allocation stable avoids a
            // device-idle resize when diagnostics are toggled at runtime.
            ulong capacityBytes =
                SimpleDdgiMemoryPlan.ResolveProbeStateReadbackBufferBytes(
                    _probeCount);
            EnsureProbeStateReadbackBuffer(
                frameIndex,
                Math.Max(MinBufferSize, capacityBytes));
            if (!_probeStateReadbackBuffers[frameIndex].IsValid)
                return;

            Silk.NET.Vulkan.Buffer source = _bufferManager.GetBuffer(_probeStateBuffer);
            Silk.NET.Vulkan.Buffer destination = _bufferManager.GetBuffer(_probeStateReadbackBuffers[frameIndex]);

            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                stateCopyBytes);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = stateCopyBytes
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

            if (classificationCopyBytes > 0)
            {
                Silk.NET.Vulkan.Buffer classificationSource =
                    _bufferManager.GetBuffer(_relocationClassificationBuffer);
                BufferMemoryBarrier2 classificationBeforeCopy = BarrierBuilder.BufferBarrier(
                    classificationSource,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
                    AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    0,
                    classificationCopyBytes);
                ExecuteBufferBarrier(commandBuffer, classificationBeforeCopy);

                BufferCopy classificationCopy = new()
                {
                    SrcOffset = checked(
                        (ulong)classificationFirstProbe *
                        RelocationClassificationStride),
                    DstOffset = stateCopyBytes,
                    Size = classificationCopyBytes
                };
                _context.Api.CmdCopyBuffer(
                    commandBuffer,
                    classificationSource,
                    destination,
                    1,
                    &classificationCopy);
            }

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                copyBytes);
            ExecuteBufferBarrier(commandBuffer, afterCopy);

            _probeStateReadbackRecorded[frameIndex] = true;
            _probeStateReadbackProbeCounts[frameIndex] = _probeCount;
            _probeStateReadbackBytes[frameIndex] = copyBytes;
            _probeClassificationReadbackBytes[frameIndex] = classificationCopyBytes;
            _probeClassificationReadbackFirstProbes[frameIndex] =
                classificationFirstProbe;
            if (classificationProbeCount > 0)
            {
                _probeClassificationReadbackCursor =
                    (classificationFirstProbe + classificationProbeCount) %
                    _probeCount;
            }
            _probeStateReadbackGenerations[frameIndex] = _volumeTableGeneration;
            RecordProbeStateReadbackUpdatedSlots(frameIndex);
        }

        private void RecordProbeStateReadbackUpdatedSlots(int frameIndex)
        {
            if (_probeCount <= 0)
                return;

            uint[] markers = _probeStateReadbackUpdateMarkers[frameIndex] ?? Array.Empty<uint>();
            uint[] expectedGenerations = _probeStateReadbackExpectedProbeGenerations[frameIndex] ?? Array.Empty<uint>();
            byte[] expectedTransportGenerations =
                _probeStateReadbackExpectedTransportGenerations[frameIndex] ?? Array.Empty<byte>();
            uint[] expectedSourceEpochs =
                _probeStateReadbackExpectedSourceEpochs[frameIndex] ?? Array.Empty<uint>();
            int[] updatedProbeIndices =
                _probeStateReadbackUpdatedProbeIndices[frameIndex] ?? Array.Empty<int>();
            if (markers.Length < _probeCount ||
                expectedGenerations.Length < _probeCount ||
                expectedTransportGenerations.Length < _probeCount ||
                expectedSourceEpochs.Length < _probeCount)
            {
                markers = new uint[_probeCount];
                expectedGenerations = new uint[_probeCount];
                expectedTransportGenerations = new byte[_probeCount];
                expectedSourceEpochs = new uint[_probeCount];
                _probeStateReadbackUpdateMarkers[frameIndex] = markers;
                _probeStateReadbackExpectedProbeGenerations[frameIndex] = expectedGenerations;
                _probeStateReadbackExpectedTransportGenerations[frameIndex] =
                    expectedTransportGenerations;
                _probeStateReadbackExpectedSourceEpochs[frameIndex] =
                    expectedSourceEpochs;
            }
            if (updatedProbeIndices.Length < _probesToUpdate)
            {
                updatedProbeIndices = new int[_probesToUpdate];
                _probeStateReadbackUpdatedProbeIndices[frameIndex] =
                    updatedProbeIndices;
            }

            uint serial = ++_nextProbeStateReadbackUpdateMarkerSerial;
            if (serial == 0u)
            {
                // Serial wrap is exceptionally rare, but clearing the bounded
                // marker tables is deterministic and prevents a false match.
                for (int i = 0; i < _probeStateReadbackUpdateMarkers.Length; i++)
                {
                    uint[]? table = _probeStateReadbackUpdateMarkers[i];
                    if (table != null)
                        Array.Clear(table);
                }
                serial = ++_nextProbeStateReadbackUpdateMarkerSerial;
            }

            int updatedProbeCount = 0;
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
                if ((uint)probeIndex >= (uint)_probeCount)
                    continue;
                GPUSimpleDdgiProbeUpdate update = _updateQueueScratch[queueOffset];
                if (markers[probeIndex] != serial)
                    updatedProbeIndices[updatedProbeCount++] = probeIndex;
                markers[probeIndex] = serial;
                expectedGenerations[probeIndex] =
                    ReadProbeUpdateGeneration(update.Reserved0);
                byte priorTransportGeneration =
                    (uint)probeIndex < (uint)_probeTransportGenerationCounts.Length
                        ? _probeTransportGenerationCounts[probeIndex]
                        : (byte)0;
                bool sourceRefresh =
                    (update.Flags & ProbeUpdateSourceRefreshFlag) != 0u;
                bool sourceGenerationBoundary =
                    IsTransportSourceGenerationBoundary(
                        sourceRefresh,
                        _probeSourceLightingGenerations[probeIndex],
                        update.SourceLightingGeneration,
                        _sourceLightingGeneration,
                        (update.Flags & ProbeStateFreshFlag) != 0u);
                expectedTransportGenerations[probeIndex] =
                    sourceGenerationBoundary
                        ? (byte)1
                        : (byte)Math.Min(byte.MaxValue, priorTransportGeneration + 1);
                uint priorSourceEpoch =
                    (uint)probeIndex < (uint)_probeSourceEpochs.Length
                        ? _probeSourceEpochs[probeIndex]
                        : 0u;
                expectedSourceEpochs[probeIndex] =
                    sourceRefresh
                        ? AdvanceSourceEpoch(priorSourceEpoch)
                        : priorSourceEpoch;
            }

            _probeStateReadbackUpdateMarkerSerials[frameIndex] = serial;
            _probeStateReadbackUpdatedProbeCounts[frameIndex] = updatedProbeCount;
        }

        private void DropProbeStateReadbackSlot(int frameIndex)
        {
            _probeStateReadbackRecorded[frameIndex] = false;
            _probeStateReadbackProbeCounts[frameIndex] = 0;
            _probeStateReadbackBytes[frameIndex] = 0;
            _probeClassificationReadbackBytes[frameIndex] = 0;
            _probeClassificationReadbackFirstProbes[frameIndex] = 0;
            _probeStateReadbackGenerations[frameIndex] = 0u;
            _probeStateReadbackUpdateMarkerSerials[frameIndex] = 0u;
            _probeStateReadbackUpdatedProbeCounts[frameIndex] = 0;
        }

        internal static bool RequiresProbeStateReadback(
            bool classificationFeedbackEnabled,
            bool transportV2Active) =>
            classificationFeedbackEnabled || transportV2Active;

        internal static bool ShouldUseCanonicalOnlyResidentLayout(
            bool residentPrivateTargets,
            bool sampledAtlasRequested,
            SimpleDdgiLayoutReport requestedReport,
            SimpleDdgiLayoutReport canonicalOnlyReport)
        {
            if (!residentPrivateTargets || !sampledAtlasRequested ||
                requestedReport == null || canonicalOnlyReport == null)
            {
                return false;
            }

            return requestedReport.WasDegraded &&
                !canonicalOnlyReport.WasDegraded &&
                canonicalOnlyReport.AcceptedProbeCount ==
                    requestedReport.RequestedProbeCount;
        }

        internal static bool ShouldPreserveProbeStateReadbackEvidence(
            bool readbackRequired,
            bool readbackRecorded) =>
            readbackRequired && !readbackRecorded;

        internal static bool IsTransportSourceEpochCurrent(
            uint expectedSourceEpoch,
            uint currentSourceEpoch) =>
            expectedSourceEpoch == currentSourceEpoch;

        internal static bool IsProbeStateReadbackCurrent(
            uint readbackProbeGeneration,
            uint currentProbeGeneration,
            uint expectedSourceEpoch,
            uint currentSourceEpoch) =>
            NormalizeProbeGeneration(readbackProbeGeneration) ==
                NormalizeProbeGeneration(currentProbeGeneration) &&
            IsTransportSourceEpochCurrent(expectedSourceEpoch, currentSourceEpoch);

        internal static bool IsTransportProbeReactivated(
            bool classificationFeedbackEnabled,
            bool wasInactive,
            bool isInactive) =>
            classificationFeedbackEnabled &&
            wasInactive &&
            !isInactive;

        private unsafe void ReadCompletedProbeStateReadback(int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            bool classificationFeedbackEnabled =
                _settings.GlobalIllumination.SimpleDdgiClassificationReadbackEnabled;
            bool readbackRequired = RequiresProbeStateReadback(
                classificationFeedbackEnabled,
                TransportV2Active);
            bool readbackRecorded = _probeStateReadbackRecorded[frameIndex];
            if (ShouldPreserveProbeStateReadbackEvidence(
                    readbackRequired,
                    readbackRecorded))
            {
                // Publication is intentionally skipped on frames with no probe
                // updates. The most recently completed state remains the current
                // convergence evidence; treating an idle frame as a failed
                // readback makes the scheduler's global snapshot oscillate and
                // forces an O(total probes) rebuild on the following frame.
                DropProbeStateReadbackSlot(frameIndex);
                return;
            }

            if (!readbackRequired ||
                !_probeStateReadbackBuffers[frameIndex].IsValid)
            {
                DropProbeStateReadbackSlot(frameIndex);
                _probeStateReadbackValid = 0;
                _probeConvergenceReadbackValid = 0;
                return;
            }

            if (_probeStateReadbackGenerations[frameIndex] != _volumeTableGeneration)
            {
                _resourceGenerationRejectionCount = SaturatingAdd(
                    _resourceGenerationRejectionCount,
                    1UL);
                DropProbeStateReadbackSlot(frameIndex);
                _probeStateReadbackValid = 0;
                _probeConvergenceReadbackValid = 0;
                return;
            }

            int probeCount = Math.Min(_probeStateReadbackProbeCounts[frameIndex], _probeCount);
            ulong stateBytes = checked((ulong)Math.Max(probeCount, 0) * ProbeStateStride);
            ulong classificationBytes = classificationFeedbackEnabled
                ? Math.Min(
                    _probeClassificationReadbackBytes[frameIndex],
                    checked(
                        (ulong)Math.Min(
                            Math.Max(probeCount, 0),
                            SimpleDdgiMemoryPlan.ClassificationReadbackProbeCapacity) *
                        RelocationClassificationStride))
                : 0UL;
            ulong requiredReadBytes = checked(stateBytes + classificationBytes);
            ulong readBytes = Math.Min(_probeStateReadbackBytes[frameIndex], requiredReadBytes);
            if (probeCount <= 0 || readBytes < stateBytes)
            {
                DropProbeStateReadbackSlot(frameIndex);
                _probeStateReadbackValid = 0;
                _probeConvergenceReadbackValid = 0;
                return;
            }

            _bufferManager.InvalidateBuffer(_probeStateReadbackBuffers[frameIndex], 0, readBytes);
            GPUSimpleDdgiProbeState* states = (GPUSimpleDdgiProbeState*)_bufferManager.GetMappedPointer(_probeStateReadbackBuffers[frameIndex]);
            int classificationFirstProbe = Math.Clamp(
                _probeClassificationReadbackFirstProbes[frameIndex],
                0,
                Math.Max(probeCount - 1, 0));
            int classificationProbeCount = checked((int)(
                classificationBytes / RelocationClassificationStride));
            GPUSimpleDdgiRelocationClassification* classifications =
                classificationProbeCount > 0
                    ? (GPUSimpleDdgiRelocationClassification*)((byte*)states + stateBytes)
                    : null;
            float backfaceRatioSum = 0.0f;
            float closeRatioSum = 0.0f;
            float hardInvalidScoreSum = 0.0f;
            int classificationStatisticsSampleCount = 0;
            if (classificationFeedbackEnabled && classifications != null)
            {
                // Classification telemetry has its own bounded rotating window.
                // Preserve that representative sample without coupling it to the
                // much smaller intersection with this frame's update queue.
                for (int localIndex = 0; localIndex < classificationProbeCount; localIndex++)
                {
                    int probeIndex = classificationFirstProbe + localIndex;
                    if ((uint)probeIndex >= (uint)probeCount ||
                        NormalizeProbeGeneration(ReadProbeStateGeneration(states[probeIndex].Flags)) !=
                            NormalizeProbeGeneration(_probeGenerations[probeIndex]))
                    {
                        continue;
                    }

                    GPUSimpleDdgiRelocationClassification classification =
                        classifications[localIndex];
                    float classifiedRayRatio =
                        classification.Statistics.Y + classification.Statistics.W;
                    if (float.IsFinite(classifiedRayRatio) && classifiedRayRatio > 0.5f &&
                        float.IsFinite(classification.Classification.Z) &&
                        float.IsFinite(classification.Classification.W) &&
                        float.IsFinite(classification.Statistics.Z))
                    {
                        closeRatioSum += Math.Clamp(classification.Classification.Z, 0.0f, 1.0f);
                        backfaceRatioSum += Math.Clamp(classification.Classification.W, 0.0f, 1.0f);
                        hardInvalidScoreSum += Math.Clamp(classification.Statistics.Z, 0.0f, 1.0f);
                        classificationStatisticsSampleCount++;
                    }
                }
            }
            uint[] markers = _probeStateReadbackUpdateMarkers[frameIndex] ?? Array.Empty<uint>();
            uint[] expectedGenerations = _probeStateReadbackExpectedProbeGenerations[frameIndex] ?? Array.Empty<uint>();
            byte[] expectedTransportGenerations =
                _probeStateReadbackExpectedTransportGenerations[frameIndex] ?? Array.Empty<byte>();
            uint[] expectedSourceEpochs =
                _probeStateReadbackExpectedSourceEpochs[frameIndex] ?? Array.Empty<uint>();
            int[] updatedProbeIndices =
                _probeStateReadbackUpdatedProbeIndices[frameIndex] ?? Array.Empty<int>();
            int updatedProbeCount = Math.Min(
                _probeStateReadbackUpdatedProbeCounts[frameIndex],
                updatedProbeIndices.Length);
            uint completedMarkerSerial = _probeStateReadbackUpdateMarkerSerials[frameIndex];

            for (int updatedOffset = 0; updatedOffset < updatedProbeCount; updatedOffset++)
            {
                int probeIndex = updatedProbeIndices[updatedOffset];
                if ((uint)probeIndex >= (uint)probeCount)
                    continue;
                GPUSimpleDdgiProbeState state = states[probeIndex];
                // A readback can be in flight while a physical slot is reused by
                // toroidal scrolling, dirty-region invalidation, or source-cache
                // resampling. Never let old classification or convergence state
                // overwrite the new physical/source epoch.
                bool readbackGenerationCurrent =
                    (uint)probeIndex < (uint)expectedSourceEpochs.Length &&
                    (uint)probeIndex < (uint)_probeSourceEpochs.Length &&
                    IsProbeStateReadbackCurrent(
                        ReadProbeStateGeneration(state.Flags),
                        _probeGenerations[probeIndex],
                        expectedSourceEpochs[probeIndex],
                        _probeSourceEpochs[probeIndex]);
                if (!readbackGenerationCurrent)
                {
                    _staleReadbackRejectionCount = SaturatingAdd(
                        _staleReadbackRejectionCount,
                        1UL);
                    continue;
                }

                _uploadReadbackProbeCount++;

                Vector3 previousRelocation = _probeRelocations[probeIndex];
                bool wasInactive = _probeInactive[probeIndex] != 0;
                bool wasRelocationPending =
                    (uint)probeIndex < (uint)_probeRelocationPending.Length &&
                    _probeRelocationPending[probeIndex] != 0;
                bool wasFresh =
                    (uint)probeIndex < (uint)_probeFresh.Length &&
                    _probeFresh[probeIndex] != 0;
                byte previousStableUpdateCount =
                    (uint)probeIndex < (uint)_probeStableUpdateCounts.Length
                        ? _probeStableUpdateCounts[probeIndex]
                        : (byte)0;
                float previousLuminanceChangeEma =
                    (uint)probeIndex < (uint)_probeLuminanceChangeEma.Length
                        ? _probeLuminanceChangeEma[probeIndex]
                        : 0.0f;
                float previousActiveWeight =
                    (uint)probeIndex < (uint)_probeActiveWeights.Length
                        ? _probeActiveWeights[probeIndex]
                        : 1.0f;
                uint previousClassification =
                    (uint)probeIndex < (uint)_probeClassifications.Length
                        ? _probeClassifications[probeIndex]
                        : 0u;
                Vector3 currentRelocation = new(
                    state.RelocationAndActive.X,
                    state.RelocationAndActive.Y,
                    state.RelocationAndActive.Z);
                if (!float.IsFinite(currentRelocation.X) ||
                    !float.IsFinite(currentRelocation.Y) ||
                    !float.IsFinite(currentRelocation.Z))
                {
                    currentRelocation = previousRelocation;
                }
                float currentActiveWeight = float.IsFinite(state.RelocationAndActive.W)
                    ? Math.Clamp(state.RelocationAndActive.W, 0.0f, 1.0f)
                    : 0.0f;
                bool inactive = classificationFeedbackEnabled
                    ? state.Classification == 1u || currentActiveWeight <= 0.001f
                    : wasInactive;
                if (classificationFeedbackEnabled)
                {
                    if (wasInactive != inactive)
                        _activeProbeCount += inactive ? -1 : 1;

                    float previousRelocationFraction = CalculateProbeRelocationFraction(
                        probeIndex,
                        previousRelocation);
                    if (previousRelocationFraction > 0.0f)
                    {
                        _probeRelocationCount--;
                        _relocationFractionSumEstimate -= previousRelocationFraction;
                    }

                    float currentRelocationFraction = CalculateProbeRelocationFraction(
                        probeIndex,
                        currentRelocation);
                    if (currentRelocationFraction > 0.0f)
                    {
                        _probeRelocationCount++;
                        _relocationFractionSumEstimate += currentRelocationFraction;
                    }

                    _probeInactive[probeIndex] = inactive ? (byte)1 : (byte)0;
                    _probeRelocations[probeIndex] = currentRelocation;
                    _probeActiveWeights[probeIndex] = currentActiveWeight;
                    _probeClassifications[probeIndex] = state.Classification;
                }
                bool reactivated = IsTransportProbeReactivated(
                    classificationFeedbackEnabled,
                    wasInactive,
                    inactive);
                if ((uint)probeIndex < (uint)_probeVisibilityValid.Length)
                {
                    _probeVisibilityValid[probeIndex] =
                        (state.Flags & ProbeStateVisibilityValidFlag) != 0u &&
                        (state.Flags & (ProbeStateFreshFlag |
                            ProbeStateInactiveFlag |
                            ProbeStateRelocationPendingFlag)) == 0u
                            ? (byte)1
                            : (byte)0;
                }
                if (reactivated)
                {
                    // A newly contributing probe is a local transport source for
                    // its neighbors. Force a complete source refresh; its queue
                    // reason keeps this repair out of the global barrier path.
                    _probeFresh[probeIndex] = 1;
                    if ((uint)probeIndex < (uint)_probeVisibilityValid.Length)
                        _probeVisibilityValid[probeIndex] = 0;
                    MarkProbeSourceCacheStale(probeIndex);
                }
                float luminanceChangeEma = BitConverter.UInt32BitsToSingle(state.Reserved0);
                bool residualEnvelopeValid =
                    float.IsFinite(luminanceChangeEma) &&
                    luminanceChangeEma >= 0.0f;
                // Corrupt convergence state must fail closed. Zero means
                // perfectly converged; mapping NaN/Inf there could retire a
                // broken probe indefinitely.
                if (!residualEnvelopeValid)
                    luminanceChangeEma = float.PositiveInfinity;
                _probeLuminanceChangeEma[probeIndex] = luminanceChangeEma;

                bool completedThisReadback =
                    (uint)probeIndex < (uint)markers.Length &&
                    (uint)probeIndex < (uint)expectedGenerations.Length &&
                    markers[probeIndex] == completedMarkerSerial &&
                    expectedGenerations[probeIndex] == NormalizeProbeGeneration(_probeGenerations[probeIndex]);
                float relocationDelta = (previousRelocation - currentRelocation).Length();
                bool materiallyRelocated =
                    classificationFeedbackEnabled &&
                    relocationDelta > ResolveProbeSpacing(probeIndex) * 0.05f;
                bool relocationRetracePending =
                    (state.Flags & ProbeStateRelocationPendingFlag) != 0u;
                if ((uint)probeIndex < (uint)_probeRelocationPending.Length)
                    _probeRelocationPending[probeIndex] =
                        relocationRetracePending ? (byte)1 : (byte)0;
                bool sourceCacheInvalid =
                    (state.Flags & ProbeStateSourceCacheInvalidFlag) != 0u;

                // A relocation pass can commit a new probe origin only after the
                // trace for that transaction has already run. Mirror the GPU's
                // pending bit into the CPU scheduler so the committed position is
                // retraced with fresh/full-ray priority. Do not upload CPU state:
                // the GPU record is already authoritative for this generation.
                if (completedThisReadback &&
                    relocationRetracePending &&
                    (uint)probeIndex < (uint)_probeFresh.Length)
                {
                    _probeFresh[probeIndex] = 1;
                    if ((uint)probeIndex < (uint)_probeVisibilityValid.Length)
                        _probeVisibilityValid[probeIndex] = 0;
                    MarkTransportPropagationNeighborhoodDirty(probeIndex);
                }

                GlobalIlluminationSettings gi = _settings.GlobalIllumination;
                float stableResidualThreshold = Math.Min(
                    gi.SimpleDdgiStableMaintenanceEmaThreshold,
                    gi.SimpleDdgiTransportTailRelativeTolerance);
                bool sourceReady =
                    (uint)probeIndex < (uint)_probeSourceLightingGenerations.Length &&
                    (uint)probeIndex < (uint)_probeSourceRayCounts.Length &&
                    _probeSourceLightingGenerations[probeIndex] == _sourceLightingGeneration &&
                    _probeSourceRayCounts[probeIndex] > 0;
                bool minimumSolverWorkComplete =
                    (uint)probeIndex < (uint)expectedTransportGenerations.Length &&
                    expectedTransportGenerations[probeIndex] >=
                        Math.Max(1, gi.SimpleDdgiTransportAcceleratedSweepCount);
                bool disqualifyingState =
                    (classificationFeedbackEnabled && inactive) ||
                    reactivated ||
                    relocationRetracePending ||
                    sourceCacheInvalid ||
                    materiallyRelocated ||
                    !residualEnvelopeValid;
                bool validStableSample = TransportV2Active
                    ? sourceReady &&
                        minimumSolverWorkComplete &&
                        luminanceChangeEma <= stableResidualThreshold
                    : luminanceChangeEma <=
                        gi.SimpleDdgiStableMaintenanceEmaThreshold;

                if (disqualifyingState)
                {
                    _probeStableUpdateCounts[probeIndex] = 0;
                }
                else if (completedThisReadback && !validStableSample)
                {
                    _probeStableUpdateCounts[probeIndex] = 0;
                }
                else if (completedThisReadback &&
                    _probeStableUpdateCounts[probeIndex] < byte.MaxValue)
                {
                    _probeStableUpdateCounts[probeIndex]++;
                }
                bool routineSourceValidationCompleted =
                    completedThisReadback &&
                    (uint)probeIndex <
                        (uint)_probeRoutineMaintenancePending.Length &&
                    _probeRoutineMaintenancePending[probeIndex] ==
                        RoutineSourceValidationPending;
                if (routineSourceValidationCompleted)
                {
                    _probeRoutineMaintenancePending[probeIndex] =
                        RoutineMaintenanceConvergencePending;
                    if (ShouldPropagateRoutineSourceRefresh(
                            residualEnvelopeValid,
                            luminanceChangeEma,
                            stableResidualThreshold))
                    {
                        // The validation sample found a material source change.
                        // Only now invalidate the local Jacobi neighbourhood;
                        // unchanged periodic samples retire after their bounded
                        // stability window without perturbing adjacent probes.
                        MarkTransportPropagationNeighborhoodDirty(
                            probeIndex,
                            routineMaintenance: true);
                    }
                }
                bool routineMaintenanceCompleted =
                    completedThisReadback &&
                    (uint)probeIndex <
                        (uint)_probeRoutineMaintenancePending.Length &&
                    _probeRoutineMaintenancePending[probeIndex] ==
                        RoutineMaintenanceConvergencePending &&
                    HasLocalTransportConvergenceEvidence(probeIndex);
                if (routineMaintenanceCompleted)
                    _probeRoutineMaintenancePending[probeIndex] =
                        RoutineMaintenanceNone;
                if (completedThisReadback)
                    RecordDirtyConvergenceIfStable(probeIndex, _frameIndex);
                if (completedThisReadback && sourceCacheInvalid && !reactivated)
                {
                    // The GPU kept this transaction safe by falling back to
                    // source tracing, but a partial solver queue is not a valid
                    // long-term source cache. Requeue a complete source refresh
                    // without discarding a valid physical relocation or published
                    // irradiance generation.
                    MarkProbeSourceCacheStale(probeIndex);
                }
                bool schedulerStateChanged =
                    wasInactive != inactive ||
                    wasRelocationPending != relocationRetracePending ||
                    wasFresh != (_probeFresh[probeIndex] != 0) ||
                    previousStableUpdateCount != _probeStableUpdateCounts[probeIndex] ||
                    routineMaintenanceCompleted ||
                    BitConverter.SingleToInt32Bits(previousLuminanceChangeEma) !=
                        BitConverter.SingleToInt32Bits(_probeLuminanceChangeEma[probeIndex]) ||
                    (classificationFeedbackEnabled &&
                        (previousClassification != _probeClassifications[probeIndex] ||
                            (previousActiveWeight <= 0.001f) !=
                            (_probeActiveWeights[probeIndex] <= 0.001f)));
                if (schedulerStateChanged)
                {
                    MarkProbeSchedulerDirty(probeIndex);
                    MarkProbeVisibilityDirty(probeIndex);
                }
            }

            if (classificationFeedbackEnabled)
            {
                _activeProbeCount = Math.Clamp(_activeProbeCount, 0, probeCount);
                _classifiedInactiveProbeCountEstimate = probeCount - _activeProbeCount;
                _probeRelocationCount = Math.Clamp(_probeRelocationCount, 0, probeCount);
                _relocationFractionSumEstimate = Math.Max(0.0f, _relocationFractionSumEstimate);
                _averageRelocationFractionEstimate = _probeRelocationCount > 0
                    ? _relocationFractionSumEstimate / _probeRelocationCount
                    : 0.0f;
                if (classificationStatisticsSampleCount > 0)
                {
                    _averageBackfaceRatioEstimate =
                        backfaceRatioSum / classificationStatisticsSampleCount;
                    _averageCloseRatioEstimate =
                        closeRatioSum / classificationStatisticsSampleCount;
                    _averageHardInvalidProbeScoreEstimate =
                        hardInvalidScoreSum / classificationStatisticsSampleCount;
                }
            }
            _probeStateReadbackValid = classificationFeedbackEnabled ? 1 : 0;
            _probeConvergenceReadbackValid = 1;
            DropProbeStateReadbackSlot(frameIndex);
        }

        private float ResolveProbeSpacing(int probeIndex)
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                int first = FirstProbe(volume);
                int count = VolumeProbeCount(volume);
                if (probeIndex >= first && probeIndex < first + count)
                    return Spacing(volume);
            }

            return Math.Max(_settings.GlobalIllumination.SimpleDdgiProbeSpacing, 0.001f);
        }

        private float CalculateProbeRelocationFraction(
            int probeIndex,
            Vector3 relocation)
        {
            float relocationLength = relocation.Length();
            if (!float.IsFinite(relocationLength) || relocationLength <= 0.001f)
                return 0.0f;

            float spacing = ResolveProbeSpacing(probeIndex);
            return Math.Clamp(
                relocationLength / Math.Max(spacing * 0.45f, 0.001f),
                0.0f,
                1.0f);
        }

        private void EnsureProbeStateReadbackBuffer(int frameIndex, ulong requiredBytes)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            if (_probeStateReadbackBuffers[frameIndex].IsValid &&
                !RequiresStableCapacityReallocation(
                    _probeStateReadbackProvisionedBytes[frameIndex],
                    requiredBytes))
            {
                return;
            }

            if (_probeStateReadbackBuffers[frameIndex].IsValid)
            {
                _capacityKeyValid = false;
                throw new InvalidOperationException(
                    "Simple DDGI readback capacity diverged from its generation-owned plan.");
            }

            _probeStateReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                requiredBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Simple DDGI Probe State Readback Frame {frameIndex}",
                MemoryBudgetCategory.GlobalIllumination);
            _probeStateReadbackProvisionedBytes[frameIndex] = requiredBytes;
            _probeStateReadbackBufferBytes += requiredBytes;
        }

        private void ReleaseProbeStateReadbackBuffer(
            int frameIndex,
            ulong provisionedBytes,
            bool destroyImmediately)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            BufferHandle handle = _probeStateReadbackBuffers[frameIndex];
            if (!handle.IsValid)
                return;

            _probeStateReadbackBufferBytes = provisionedBytes >=
                _probeStateReadbackBufferBytes
                    ? 0UL
                    : _probeStateReadbackBufferBytes - provisionedBytes;
            if (destroyImmediately)
                _bufferManager.DestroyBuffer(handle);
            else
                RetireBufferResource(handle, provisionedBytes);
            _probeStateReadbackBuffers[frameIndex] = default;
            _probeStateReadbackProvisionedBytes[frameIndex] = 0UL;
            DropProbeStateReadbackSlot(frameIndex);
        }

        private unsafe void ExecuteBufferBarrier(CommandBuffer commandBuffer, BufferMemoryBarrier2 barrier)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void BeginFrameResourceRetirement()
        {
            _frameSerial++;
            long retirementStart = Stopwatch.GetTimestamp();
            int destroyed = DrainRetiredResources(force: false);
            _gpuScheduler.CollectRetired(_completedFrameFenceValue);
            _sampledAtlas?.CollectRetired(_completedFrameFenceValue);
            _uploadCapacityRetiredResourceDestructionMicroseconds +=
                ElapsedMicroseconds(retirementStart);
            _uploadCapacityRetiredResourceDestructionCount += destroyed;
        }

        private void MarkResourcesUsedByPendingSubmission()
        {
            _resourceLastUseFrameFenceValue =
                _lastSubmittedFrameFenceValue == ulong.MaxValue
                    ? ulong.MaxValue
                    : _lastSubmittedFrameFenceValue + 1UL;
        }

        /// <summary>
        /// A single update-after-bind set is shared by every frame in flight.
        /// Vulkan therefore requires all submitted readers of an element to
        /// complete before that element is rewritten. Complete only those
        /// frame fences and verify the renderer returned sufficient progress;
        /// never substitute frame age or a device-wide idle.
        /// </summary>
        private bool EnsureBindlessDescriptorReadersComplete(
            CommandBuffer commandBuffer,
            string transitionDescription)
        {
            if (_registeredBindlessHeap == null ||
                commandBuffer.Handle == 0 ||
                _resourceLastUseFrameFenceValue == 0UL ||
                _completedFrameFenceValue >= _resourceLastUseFrameFenceValue)
            {
                return true;
            }

            ulong completedFenceValue = _completedFrameFenceValue;
            _recordRuntimeStall(
                RuntimeStallReason.ResourceGenerationFenceWait,
                transitionDescription,
                () => completedFenceValue =
                    _waitForBindlessDescriptorReaders());
            ObserveFrameFenceCompletion(
                _lastSubmittedFrameFenceValue,
                completedFenceValue);

            if (_completedFrameFenceValue >=
                _resourceLastUseFrameFenceValue)
            {
                _capacityTransitionDeferred = false;
                _capacityTransitionDeferredReason = string.Empty;
                return true;
            }

            _capacityTransitionDeferred = true;
            _capacityTransitionDeferredReason =
                "bindless-descriptor-readers-pending";
            return false;
        }

        private void TryRecordGpuSchedulerFallbackExport(
            CommandBuffer commandBuffer)
        {
            if (!_gpuSchedulerFallbackExportRequested)
                return;

            if (_schedulerMode != SimpleDdgiSchedulerMode.GpuResident ||
                !_gpuScheduler.IsReady ||
                !_gpuResidentProbeStateBootstrapped ||
                _probeStateUploadRequired ||
                !_probeStateBuffer.IsValid ||
                _probeCount <= 0)
            {
                FailGpuSchedulerFallbackExport();
                return;
            }

            ulong requiredExportBytes =
                SimpleDdgiGpuScheduler.ResolveFallbackStateExportBytes(
                    _probeCount);
            ulong incrementalExportBytes = requiredExportBytes >
                    _gpuScheduler.FallbackStateExportBytes
                ? requiredExportBytes -
                    _gpuScheduler.FallbackStateExportBytes
                : 0UL;
            ulong fallbackPeakBytes = SaturatingAdd(
                BufferBytes,
                SaturatingAdd(
                    SampledAtlasImageBytes,
                    SaturatingAdd(
                        RetiredBufferBytes,
                        SaturatingAdd(
                            _gpuScheduler.RetiredBytes,
                            incrementalExportBytes))));
            ulong configuredBudget =
                _settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes;
            if (requiredExportBytes == 0UL ||
                configuredBudget != 0UL && fallbackPeakBytes > configuredBudget)
            {
                // The complete export is optional recovery evidence. If its
                // temporary overlap cannot honor the hard DDGI budget, take
                // the already-defined complete fresh-reset CPU fallback.
                FailGpuSchedulerFallbackExport();
                return;
            }

            ulong pendingFenceValue =
                _lastSubmittedFrameFenceValue == ulong.MaxValue
                    ? ulong.MaxValue
                    : _lastSubmittedFrameFenceValue + 1UL;
            SimpleDdgiSchedulerStateExportTag tag = new(
                _probeCount,
                _gpuScheduler.ResourceGeneration,
                NonZeroGeneration(_volumeTableGeneration),
                NonZeroGeneration(_physicalOwnershipGeneration),
                NonZeroGeneration(_sourceLightingGeneration),
                NonZeroGeneration(_sourceEpochGeneration),
                NonZeroGeneration(_transportGeneration),
                _frameSerial);
            bool recorded;
            try
            {
                recorded = _gpuScheduler.RecordFallbackStateExport(
                    commandBuffer,
                    _probeStateBuffer,
                    tag,
                    pendingFenceValue);
            }
            catch (Exception exception) when (
                exception is VulkanException or
                InvalidOperationException or
                OverflowException)
            {
                recorded = false;
            }
            if (!recorded)
            {
                FailGpuSchedulerFallbackExport();
                return;
            }

            _gpuSchedulerFallbackExportTag = tag;
            _gpuSchedulerFallbackExportRequested = false;
            _gpuSchedulerFallbackExportSubmitted = true;
        }

        private void TryCompleteGpuSchedulerFallbackExport()
        {
            if (!_gpuSchedulerFallbackExportSubmitted)
                return;

            SimpleDdgiSchedulerStateExportReadStatus status =
                _gpuScheduler.TryReadFallbackStateExport(
                    _completedFrameFenceValue,
                    _gpuResidentBootstrapStateScratch,
                    _probeStateScratch,
                    out SimpleDdgiSchedulerStateExportTag tag);
            if (status == SimpleDdgiSchedulerStateExportReadStatus.Pending)
                return;
            if (status != SimpleDdgiSchedulerStateExportReadStatus.Complete ||
                tag != _gpuSchedulerFallbackExportTag ||
                !IsCurrentGpuSchedulerFallbackExport(tag) ||
                !TryImportGpuSchedulerFallbackState(tag))
            {
                FailGpuSchedulerFallbackExport();
                return;
            }

            _gpuSchedulerFallbackExportSubmitted = false;
            _gpuSchedulerFallbackFreshResetPending = false;
            _gpuSchedulerStateExportSuccessCount = SaturatingAdd(
                _gpuSchedulerStateExportSuccessCount,
                1UL);
            _gpuSchedulerReentryStableFrameCount = 0;
        }

        private bool IsCurrentGpuSchedulerFallbackExport(
            in SimpleDdgiSchedulerStateExportTag tag) =>
            tag.IsInitialized &&
            tag.ProbeCount == _probeCount &&
            tag.SchedulerResourceGeneration == _gpuScheduler.ResourceGeneration &&
            tag.VolumeTableGeneration == NonZeroGeneration(_volumeTableGeneration) &&
            tag.PhysicalOwnershipGeneration ==
                NonZeroGeneration(_physicalOwnershipGeneration) &&
            tag.SourceLightingGeneration ==
                NonZeroGeneration(_sourceLightingGeneration) &&
            tag.SourceEpochGeneration == NonZeroGeneration(_sourceEpochGeneration) &&
            tag.TransportGeneration == NonZeroGeneration(_transportGeneration);

        private bool TryImportGpuSchedulerFallbackState(
            in SimpleDdgiSchedulerStateExportTag tag)
        {
            int probeCount = tag.ProbeCount;
            for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                if (!IsValidGpuSchedulerFallbackRecord(
                        _gpuResidentBootstrapStateScratch[probeIndex],
                        _probeStateScratch[probeIndex],
                        tag.VolumeTableGeneration))
                {
                    return false;
                }
            }

            EnsureCpuProbeStateCapacity(probeCount);
            _activeProbeCount = 0;
            _classifiedInactiveProbeCountEstimate = 0;
            _probeRelocationCount = 0;
            _relocationFractionSumEstimate = 0.0f;
            for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                GPUSimpleDdgiSchedulerProbeState scheduler =
                    _gpuResidentBootstrapStateScratch[probeIndex];
                GPUSimpleDdgiProbeState state = _probeStateScratch[probeIndex];
                uint physicalGeneration =
                    (state.Flags >> ProbeStateGenerationShift) &
                    ProbeStateGenerationValueMask;
                _probeGenerations[probeIndex] = physicalGeneration;
                _probeRelocations[probeIndex] = new Vector3(
                    state.RelocationAndActive.X,
                    state.RelocationAndActive.Y,
                    state.RelocationAndActive.Z);
                _probeActiveWeights[probeIndex] =
                    Math.Clamp(state.RelocationAndActive.W, 0.0f, 1.0f);
                _probeClassifications[probeIndex] = state.Classification;
                _probeFresh[probeIndex] =
                    (state.Flags & ProbeStateFreshFlag) != 0u ? (byte)1 : (byte)0;
                _probeInactive[probeIndex] =
                    (state.Flags & ProbeStateInactiveFlag) != 0u ? (byte)1 : (byte)0;
                _probeRelocationPending[probeIndex] =
                    (state.Flags & ProbeStateRelocationPendingFlag) != 0u
                        ? (byte)1
                        : (byte)0;
                _probeVisibilityValid[probeIndex] =
                    (state.Flags & ProbeStateVisibilityValidFlag) != 0u &&
                    (state.Flags & (ProbeStateFreshFlag |
                        ProbeStateInactiveFlag |
                        ProbeStateRelocationPendingFlag)) == 0u
                        ? (byte)1
                        : (byte)0;

                uint schedulerReasons = scheduler.DirtyReasonFlags;
                _probeDirtyReasons[probeIndex] =
                    unchecked((byte)(schedulerReasons & byte.MaxValue));
                byte schedulingFlags = 0;
                if ((state.Flags & ProbeStateScrollExposedFlag) != 0u ||
                    (schedulerReasons & SimpleDdgiSchedulerAbi.ReasonScrollExposed) != 0u)
                {
                    schedulingFlags |= ProbeSchedulingScrollExposedFlag;
                }
                if ((schedulerReasons & SimpleDdgiSchedulerAbi.ReasonRegionalDirty) != 0u)
                    schedulingFlags |= ProbeSchedulingRegionalDirtyFlag;
                if ((schedulerReasons & SimpleDdgiSchedulerAbi.ProbeMetadataVisible) != 0u)
                {
                    schedulingFlags |= ProbeSchedulingVisibleFlag;
                    _probeVisibilityImportance[probeIndex] =
                        SchedulerVisibleImportanceThreshold;
                }
                else
                {
                    _probeVisibilityImportance[probeIndex] = 0;
                }
                _probeSchedulingFlags[probeIndex] = schedulingFlags;

                SimpleDdgiSchedulerAbi.UnpackSchedulerProbeLifecycle(
                    scheduler.PackedTransportAndLifecycle,
                    out uint sourceRayCount,
                    out uint transportGeneration,
                    out uint stableUpdateCount,
                    out uint routineMaintenanceState,
                    out _);
                bool sourceCacheInvalid =
                    (state.Flags & ProbeStateSourceCacheInvalidFlag) != 0u;
                _probeSourceLightingGenerations[probeIndex] = sourceCacheInvalid
                    ? 0u
                    : scheduler.CommittedSourceLightingGeneration;
                _probeSourceEpochs[probeIndex] = scheduler.SourceEpoch;
                _probeSourceRayCounts[probeIndex] = sourceCacheInvalid
                    ? (ushort)0
                    : checked((ushort)sourceRayCount);
                _probeTransportGenerationCounts[probeIndex] =
                    checked((byte)transportGeneration);
                _probeStableUpdateCounts[probeIndex] =
                    checked((byte)stableUpdateCount);
                _probeRoutineMaintenancePending[probeIndex] =
                    routineMaintenanceState != 0u
                        ? RoutineMaintenanceConvergencePending
                        : RoutineMaintenanceNone;
                _probeLastUpdatedFrames[probeIndex] =
                    scheduler.LastCommittedUpdateFrame;
                _probeLastSourceRefreshFrames[probeIndex] =
                    scheduler.LastCommittedSourceRefreshFrame;
                _probeInvalidationMarkers[probeIndex] =
                    scheduler.AppliedInvalidationMarker;
                _probeDirtyLatencyStartFrames[probeIndex] =
                    scheduler.DirtyStartFrame;
                _probeDirtyLatencyStates[probeIndex] =
                    scheduler.DirtyStartFrame != 0u ? (byte)1 : (byte)0;
                float residual = BitConverter.UInt32BitsToSingle(state.Reserved0);
                _probeLuminanceChangeEma[probeIndex] =
                    float.IsFinite(residual) ? Math.Max(residual, 0.0f) : 0.0f;

                bool inactive = _probeInactive[probeIndex] != 0 ||
                    state.Classification == 1u ||
                    state.RelocationAndActive.W <= 0.001f;
                if (inactive)
                    _classifiedInactiveProbeCountEstimate++;
                else
                    _activeProbeCount++;
                float relocationFraction = _probeRelocations[probeIndex].Length();
                if (relocationFraction > 0.0001f)
                {
                    _probeRelocationCount++;
                    _relocationFractionSumEstimate += relocationFraction;
                }
            }

            _averageRelocationFractionEstimate = _probeRelocationCount > 0
                ? _relocationFractionSumEstimate / _probeRelocationCount
                : 0.0f;
            Array.Clear(_probeQueued);
            Array.Clear(_probeSchedulerDirty);
            _probeSchedulerDirtyCount = 0;
            _probeStateReadbackValid = 0;
            _probeConvergenceReadbackValid = 0;
            _probeStateUploadRequired = true;
            _schedulerGlobalStateValid = false;
            _schedulerVisibilityFullRefreshRequired = true;
            RequirePersistentSchedulerRebuild();
            RecomputeDirtyLatencyOutstandingCount();
            RebuildAtmosphereCohortCounters();
            BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
            return true;
        }

        internal static bool IsValidGpuSchedulerFallbackRecord(
            in GPUSimpleDdgiSchedulerProbeState scheduler,
            in GPUSimpleDdgiProbeState state,
            uint expectedVolumeTableGeneration)
        {
            uint physicalGeneration =
                (state.Flags >> ProbeStateGenerationShift) &
                ProbeStateGenerationValueMask;
            SimpleDdgiSchedulerAbi.UnpackSchedulerProbeLifecycle(
                scheduler.PackedTransportAndLifecycle,
                out uint sourceRayCount,
                out _,
                out _,
                out _,
                out uint transactionStatus);
            bool finiteState =
                float.IsFinite(state.RelocationAndActive.X) &&
                float.IsFinite(state.RelocationAndActive.Y) &&
                float.IsFinite(state.RelocationAndActive.Z) &&
                float.IsFinite(state.RelocationAndActive.W);
            return expectedVolumeTableGeneration != 0u &&
                scheduler.OwningVolumeTableGeneration ==
                    expectedVolumeTableGeneration &&
                scheduler.SourceEpoch != 0u &&
                transactionStatus == 0u &&
                sourceRayCount <=
                    GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe &&
                (sourceRayCount == 0u ||
                    (scheduler.CommittedSourceLightingGeneration != 0u &&
                        scheduler.CacheProbeBaseWordPlusOne != 0u)) &&
                physicalGeneration != 0u &&
                finiteState &&
                state.RelocationAndActive.W >= 0.0f &&
                state.RelocationAndActive.W <= 1.0f &&
                state.Classification <= 1u;
        }

        private void FailGpuSchedulerFallbackExport()
        {
            _gpuSchedulerFallbackExportRequested = false;
            _gpuSchedulerFallbackExportSubmitted = false;
            _gpuSchedulerFallbackFreshResetPending = true;
            _gpuSchedulerStateExportFailureCount = SaturatingAdd(
                _gpuSchedulerStateExportFailureCount,
                1UL);
        }

        private void UpdateGpuSchedulerReentry(
            SimpleDdgiSchedulerMode requestedMode,
            bool structuredGatherAvailable)
        {
            bool frozenSparseTransaction =
                _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                _capacityPlan.ResidencyMode.UsesSparsePayloads() &&
                _probePageCache.Frozen;
            if (!_gpuSchedulerFallbackLatched ||
                !requestedMode.IsGpuMode() ||
                (_schedulerMode != SimpleDdgiSchedulerMode.CpuReference &&
                 !frozenSparseTransaction) ||
                _gpuSchedulerFallbackFreshResetPending ||
                _gpuSchedulerFallbackExportRequested ||
                _gpuSchedulerFallbackExportSubmitted)
            {
                _gpuSchedulerReentryStableFrameCount = 0;
                return;
            }

            bool stable =
                structuredGatherAvailable &&
                _context.RayQuerySupported &&
                _context.KhrAccelerationStructure != null &&
                _gpuScheduler.IsReady &&
                _probeCount > 0 &&
                _wasSimpleDdgiEnabled &&
                !_atlasFresh &&
                !_capacityTransitionDeferred &&
                !_probeStateUploadRequired &&
                _gpuSchedulerGenerationMismatchStreak == 0;
            if (!stable)
            {
                _gpuSchedulerReentryStableFrameCount = 0;
                return;
            }

            _gpuSchedulerReentryStableFrameCount = Math.Min(
                int.MaxValue,
                _gpuSchedulerReentryStableFrameCount + 1);
            int requiredFrames =
                _settings.GlobalIllumination
                    .SimpleDdgiSchedulerReentryStableFrameCount;
            if (_gpuSchedulerReentryStableFrameCount < requiredFrames)
                return;

            if (frozenSparseTransaction)
            {
                // Never resume writes against the frozen mapping. A forced
                // replacement advances the full residency resource identity;
                // the normal bootstrap path then seeds an empty fail-closed map.
                _probePageCache.RequireFreshTransactionForReentry();
            }
            _gpuSchedulerFallbackLatched = false;
            _gpuSchedulerFallbackReason = string.Empty;
            _gpuSchedulerReentryStableFrameCount = 0;
            _gpuSchedulerReentryCount = SaturatingAdd(
                _gpuSchedulerReentryCount,
                1UL);
        }

        private void PrepareCpuFreshResetFallback()
        {
            // The resident path intentionally keeps the CPU lifecycle mirror
            // stale.  If a delayed summary proves that GPU ownership is no
            // longer trustworthy, importing that mirror would mix generations.
            // Preserve the last complete atlas and re-enter through a clean CPU
            // transaction instead.  This is bounded fallback work, never part
            // of a stable resident frame.
            int probeCount = Math.Min(_probeCount, _probeFresh.Length);
            for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                _probeFresh[probeIndex] = 1;
                _probeInactive[probeIndex] = 0;
                _probeRelocationPending[probeIndex] = 0;
                _probeVisibilityValid[probeIndex] = 0;
                _probeSchedulingFlags[probeIndex] = 0;
                _probeDirtyReasons[probeIndex] = 0;
                _probeRoutineMaintenancePending[probeIndex] = 0;
                _probeVisibilityImportance[probeIndex] = 0;
                _probeGenerations[probeIndex] = AdvanceProbeGeneration(
                    _probeGenerations[probeIndex]);
                _probeInvalidationMarkers[probeIndex] = 0;
                _probeRelocations[probeIndex] = Vector3.Zero;
                _probeActiveWeights[probeIndex] = 1.0f;
                _probeClassifications[probeIndex] = 0u;
                _probeStableUpdateCounts[probeIndex] = 0;
                _probeLuminanceChangeEma[probeIndex] = 0.0f;
                _probeLastUpdatedFrames[probeIndex] = unchecked(_frameIndex - 1u);
                _probeSourceLightingGenerations[probeIndex] = 0u;
                _probeLastSourceRefreshFrames[probeIndex] = 0u;
                AdvanceProbeSourceEpoch(probeIndex);
                _probeSourceRayCounts[probeIndex] = 0;
                _probeTransportGenerationCounts[probeIndex] = 0;
                _probeAtmosphereCohortFlags[probeIndex] = 0;
                _probeDirtyLatencyStates[probeIndex] = 0;
                _probeDirtyLatencyStartFrames[probeIndex] = 0u;
            }

            Array.Clear(_probeQueued);
            Array.Clear(_probeSchedulerDirty);
            _probeSchedulerDirtyCount = 0;
            _probeStateReadbackValid = 0;
            _probeConvergenceReadbackValid = 0;
            _probeStateUploadRequired = true;
            _atlasFresh = true;
            _newlyInvalidatedProbeCount = probeCount;
            _schedulerRebuildRequired = true;
            _schedulerVisibilityFullRefreshRequired = true;
            _schedulerGlobalStateValid = false;
            BeginTransportGlobalConvergence(forceFieldEvidenceReset: true);
        }

        private void RequestGpuSchedulerFallback(
            string reason,
            bool requiresFreshReset = true)
        {
            if (_gpuSchedulerFallbackLatched)
                return;

            string resolvedReason = string.IsNullOrWhiteSpace(reason)
                ? "resident scheduler validation failure"
                : reason;
            bool freezeSparseTransaction =
                _capacityPlan.ResidencyMode.UsesSparsePayloads() &&
                _probePageCache.IsReady;
            if (freezeSparseTransaction)
            {
                _probePageCache.FreezeForRuntimeFailure(
                    residencyStateValid: _probePageCache.ResidencyValid,
                    reason: resolvedReason);
                // A CPU mirror cannot own compact sparse payload addresses.
                // Keep the last complete GPU transaction and retry only by
                // replacing both its resource identity and page mappings.
                requiresFreshReset = false;
            }
            _gpuSchedulerFallbackLatched = true;
            bool canExportResidentState =
                !freezeSparseTransaction && requiresFreshReset &&
                _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                _gpuScheduler.IsReady &&
                _gpuResidentProbeStateBootstrapped &&
                _probeStateBuffer.IsValid &&
                _probeCount > 0;
            _gpuSchedulerFallbackExportRequested = canExportResidentState;
            _gpuSchedulerFallbackExportSubmitted = false;
            _gpuSchedulerFallbackFreshResetPending =
                requiresFreshReset && !canExportResidentState;
            _gpuSchedulerReentryStableFrameCount = 0;
            _gpuSchedulerFallbackCount = SaturatingAdd(
                _gpuSchedulerFallbackCount,
                1UL);
            _gpuSchedulerFallbackReason = resolvedReason;
        }

        private void SetGpuSchedulerMode(SimpleDdgiSchedulerMode requestedMode)
        {
            SimpleDdgiSchedulerMode nextMode = requestedMode.Sanitize();
            if (!nextMode.IsGpuMode())
            {
                // An explicit CPU selection is the operator's acknowledgement
                // of a resident fallback and permits a later explicit GPU
                // re-entry. A resident setting remains held at CPU until that
                // acknowledgement, so the renderer cannot oscillate modes.
                _gpuSchedulerFallbackLatched = false;
                _gpuSchedulerFallbackReason = string.Empty;
                _gpuSchedulerFallbackExportRequested = false;
                _gpuSchedulerFallbackExportSubmitted = false;
                _gpuSchedulerReentryStableFrameCount = 0;
                if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident)
                    _gpuSchedulerFallbackFreshResetPending = true;
            }
            else if (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null)
            {
                // GPU-resident scheduling cannot make progress without the
                // ray-query producer used by trace and transport. Resolve the
                // authored GPU default to the CPU path before allocating an
                // arena, and keep the reason visible for diagnostics.
                RequestGpuSchedulerFallback(
                    "GPU scheduler unavailable: ray-query acceleration-structure support is missing",
                    requiresFreshReset: _schedulerMode == SimpleDdgiSchedulerMode.GpuResident);
                _gpuSchedulerFallbackExportRequested = false;
                _gpuSchedulerFallbackExportSubmitted = false;
                _gpuSchedulerFallbackFreshResetPending =
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident;
                nextMode = SimpleDdgiSchedulerMode.CpuReference;
            }
            else if (_gpuSchedulerFallbackLatched)
            {
                bool frozenSparseTransaction =
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    _capacityPlan.ResidencyMode.UsesSparsePayloads() &&
                    _probePageCache.Frozen;
                bool exportInFlight =
                    _schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                    (_gpuSchedulerFallbackExportRequested ||
                     _gpuSchedulerFallbackExportSubmitted);
                nextMode = frozenSparseTransaction || exportInFlight
                    ? SimpleDdgiSchedulerMode.GpuResident
                    : SimpleDdgiSchedulerMode.CpuReference;
            }
            if (nextMode == _schedulerMode)
                return;

            if (_schedulerMode == SimpleDdgiSchedulerMode.GpuResident ||
                nextMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                // A resident transition invalidates CPU readback evidence. The
                // GPU scheduler owns lifecycle state in this mode, and stale
                // CPU records must not be allowed to overwrite it later.
                for (int frameIndex = 0; frameIndex < _probeStateReadbackRecorded.Length; frameIndex++)
                    DropProbeStateReadbackSlot(frameIndex);
                AbortUpdateTransaction();
                _probeStateUploadRequired = true;
                _gpuResidentProbeStateBootstrapped = false;
            }

            _schedulerMode = nextMode;
            _gpuScheduler.SetMode(nextMode, _resourceLastUseFrameFenceValue);
            // A GPU transition is fail-closed until the current frame records
            // its public upload and, for resident mode, the distinct private
            // scheduler bootstrap.  UploadGpuSchedulerFrame/UploadGpuResidentFrame
            // raises this only after those writes are in the command stream.
            _gpuSchedulerFrameExecutionAvailable = !nextMode.IsGpuMode();
            _gpuSchedulerFeedbackValid = false;
            _transportResidentParticipantCount = 0;
            _transportResidentSourceRepairProbeCount = 0;
            _lastGpuSchedulerFeedback = default;
            _gpuSchedulerFeedbackFrameSerial = 0;
            _receiverRecordsPublishedCount = 0;
            _currentReceiverRecordsPublishedCount = 0;
        }

        private void RetireBufferResource(BufferHandle buffer, ulong bytes)
        {
            if (!buffer.IsValid)
                return;

            if (_resourceLastUseFrameFenceValue == 0UL ||
                _completedFrameFenceValue >= _resourceLastUseFrameFenceValue)
            {
                _bufferManager.DestroyBuffer(buffer);
                return;
            }

            ulong packedHandle = PackBufferHandle(buffer);
            GpuRetirementRecord record = new(
                ResourceGeneration: buffer.Generation,
                ByteCharge: bytes,
                EnqueuedFrame: _lastSubmittedFrameFenceValue,
                Completion: GpuCompletionToken.ForFrameFence(
                    _resourceLastUseFrameFenceValue),
                Resource: new GpuRetirementResource(
                    GpuRetirementResourceKind.Buffer,
                    packedHandle));
            if (!_bufferRetirement.TryEnqueue(
                    record,
                    liveBytes: 0UL,
                    out GpuRetirementAdmissionFailure failure))
            {
                throw new InvalidOperationException(
                    $"Simple-DDGI buffer retirement admission failed: {failure}.");
            }
        }

        private int DrainRetiredResources(bool force)
        {
            int count = force
                ? _bufferRetirement.DrainAfterExternalDeviceIdle(
                    _bufferRetirementScratch)
                : _bufferRetirement.Poll(
                    new GpuCompletionProgress(
                        _completedFrameFenceValue,
                        0UL,
                        0UL),
                    _bufferRetirementScratch,
                    _lastSubmittedFrameFenceValue);
            for (int index = 0; index < count; index++)
            {
                GpuRetirementRecord record = _bufferRetirementScratch[index];
                if (record.Resource.Kind != GpuRetirementResourceKind.Buffer)
                    throw new InvalidOperationException(
                        "Simple-DDGI retirement queue contained a non-buffer record.");
                BufferHandle buffer = UnpackBufferHandle(record.Resource.Handle);
                if (buffer.IsValid)
                    _bufferManager.DestroyBuffer(buffer);
                _bufferRetirementScratch[index] = default;
            }
            return count;
        }

        private static ulong PackBufferHandle(BufferHandle buffer) =>
            ((ulong)buffer.Generation << 32) | unchecked((uint)buffer.Index);

        private static BufferHandle UnpackBufferHandle(ulong packed) =>
            new(unchecked((int)(uint)packed), (uint)(packed >> 32));

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _sampledAtlas?.Dispose();
            _sampledAtlas = null;
            if (_paramsBuffer.IsValid)
                _bufferManager.DestroyBuffer(_paramsBuffer);
            if (_irradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_irradianceAtlasBuffer);
            if (_transportIrradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_transportIrradianceAtlasBuffer);
            if (_transportSourceCacheBuffer.IsValid)
                _bufferManager.DestroyBuffer(_transportSourceCacheBuffer);
            if (_visibilityAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_visibilityAtlasBuffer);
            if (_rayResultScratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_rayResultScratchBuffer);
            if (_probeStateBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeStateBuffer);
            if (_receiverProbeBuffer.IsValid)
                _bufferManager.DestroyBuffer(_receiverProbeBuffer);
            if (_probeUpdateQueueBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeUpdateQueueBuffer);
            if (_relocationClassificationBuffer.IsValid)
                _bufferManager.DestroyBuffer(_relocationClassificationBuffer);
            _gpuScheduler.Dispose();
            _probePageCache.Dispose();
            for (int i = 0; i < _probeStateReadbackBuffers.Length; i++)
            {
                if (_probeStateReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_probeStateReadbackBuffers[i]);
            }
            _ = DrainRetiredResources(force: true);
        }

        private readonly record struct SchedulerGlobalStateSnapshot(
            uint VolumeTableGeneration,
            int ProbeCount,
            bool LightingDirty,
            bool TransportV2Active,
            bool TransportGlobalConvergencePending,
            bool PeriodicSourceRefreshWavePending,
            uint PeriodicSourceRefreshWaveCutoffFrame,
            uint SourceLightingGeneration,
            bool ClassificationSchedulingEnabled,
            bool ConvergenceReadbackValid,
            int StableMaintenanceUpdateCount,
            int AcceleratedSweepCount,
            int TailToleranceBits);

        private readonly record struct SimpleDdgiRingQuality(
            int RingIndex,
            int FullRays,
            int MaintenanceRays,
            int MinimumUpdateQuota,
            int MaximumUpdateQuota,
            int MaterialTextureMaxCascade,
            int MaxShadedLights);

        private struct VolumeCandidate
        {
            public VolumeCandidate(
                int kind,
                int sourceOrdinal,
                int priority,
                SimpleDdgiVolumePurpose purpose,
                Vector3 origin,
                float spacing,
                int countX,
                int countY,
                int countZ,
                Vector3 worldMin,
                Vector3 worldMax,
                float edgeFadeDistance)
            {
                Kind = kind;
                SourceOrdinal = sourceOrdinal;
                Priority = priority;
                Purpose = purpose;
                Origin = origin;
                Spacing = spacing;
                CountX = countX;
                CountY = countY;
                CountZ = countZ;
                WorldMin = worldMin;
                WorldMax = worldMax;
                EdgeFadeDistance = edgeFadeDistance;
                FirstProbeIndex = 0;
                PhysicalOffsetX = 0;
                PhysicalOffsetY = 0;
                PhysicalOffsetZ = 0;
            }

            public int Kind;
            public int SourceOrdinal;
            public int Priority;
            public SimpleDdgiVolumePurpose Purpose;
            public Vector3 Origin;
            public float Spacing;
            public int CountX;
            public int CountY;
            public int CountZ;
            public Vector3 WorldMin;
            public Vector3 WorldMax;
            public float EdgeFadeDistance;
            public int FirstProbeIndex;
            public int PhysicalOffsetX;
            public int PhysicalOffsetY;
            public int PhysicalOffsetZ;
            public int ProbeCount => checked(CountX * CountY * CountZ);
            public int KindPriority => Kind == VolumeKindAuthored ? 0 : Kind == VolumeKindLegacy ? 1 : 2;
            public int PurposeRank => Purpose switch
            {
                SimpleDdgiVolumePurpose.ReceiverHero => 0,
                SimpleDdgiVolumePurpose.NavigableInterior => 1,
                SimpleDdgiVolumePurpose.DynamicInfluence => 2,
                _ => 3
            };

            public GPUSimpleDdgiVolume ToGpuVolume()
            {
                return new GPUSimpleDdgiVolume
                {
                    OriginAndSpacing = new Vector4(Origin.X, Origin.Y, Origin.Z, Spacing),
                    GridCountsAndFirstProbe = new Vector4(CountX, CountY, CountZ, FirstProbeIndex),
                    WorldMinAndEdgeFade = new Vector4(WorldMin.X, WorldMin.Y, WorldMin.Z, EdgeFadeDistance),
                    WorldMaxAndKind = new Vector4(WorldMax.X, WorldMax.Y, WorldMax.Z, Kind),
                    UpdateStartAndCount = Vector4.Zero,
                    // x remains the stable volume key; yzw are the shared toroidal
                    // physical offset used by state, irradiance, and visibility.
                    RaysAndReserved = new Vector4(SourceOrdinal, PhysicalOffsetX, PhysicalOffsetY, PhysicalOffsetZ)
                };
            }
        }
    }
}
