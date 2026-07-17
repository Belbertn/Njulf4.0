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
    /// <summary>
    /// Deterministic priority buckets used by the bounded simple-DDGI update
    /// scheduler. Values are ordered intentionally; do not reorder them without
    /// updating the capture/oracle expectations that consume this telemetry.
    /// </summary>
    public enum SimpleDdgiSchedulerWorkClass : byte
    {
        FreshExposedVisible = 0,
        VisibleDirty = 1,
        VisibleRetry = 2,
        NearMaintenance = 3,
        MidMaintenance = 4,
        FarMaintenance = 5,
        Count = 6,
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
        int ScheduledFreshExposedVisible,
        int ScheduledVisibleDirty,
        int ScheduledVisibleRetry,
        int ScheduledNearMaintenance,
        int ScheduledMidMaintenance,
        int ScheduledFarMaintenance,
        int ReservedFreshExposedVisible,
        int ReservedVisibleDirty,
        int ReservedVisibleRetry,
        int ReservedNearMaintenance,
        int ReservedMidMaintenance,
        int ReservedFarMaintenance,
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
        private static readonly ulong ParamsBufferSize = ParamsSize + VolumeStride * GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount;
        private static readonly ulong RayResultStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiRayResult>();
        private static readonly ulong TransportRayCacheStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiTransportRayCache>();
        private static readonly ulong ProbeStateStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>();
        private static readonly ulong ProbeUpdateStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>();
        private static readonly ulong RelocationClassificationStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>();
        private static readonly ulong AtlasTexelStride = 8;
        private const uint ProbeStateFreshFlag = 1u << 0;
        private const uint ProbeStateScrollExposedFlag = 1u << 1;
        private const uint ProbeStateInactiveFlag = 1u << 2;
        private const uint ProbeStateRelocationPendingFlag = 1u << 3;
        // Set by the V2 trace/transport shaders when a solver-only lookup finds
        // a missing cache entry. The completed readback turns it into a normal
        // physical-slot invalidation and full source refresh.
        private const uint ProbeStateSourceCacheInvalidFlag = 1u << 4;
        // Kept separate from scene light/emissive/geometry bits so a capture
        // can distinguish an authored lighting edit from a live transport
        // calibration change that deliberately restarted convergence.
        private const uint TransportCalibrationDirtyReasonFlag = 1u << 3;
        private const uint ProbeUpdateMaintenanceFlag = 1u << 12;
        // V2 source refreshes are explicit queue work. Solver-only reuse entries
        // retain their cached source radiance and therefore consume no primary
        // ray budget while still advancing the recursive transport field.
        private const uint ProbeUpdateSourceRefreshFlag = 1u << 13;
        // Packed into the simple-DDGI flag word so this artist-facing gather
        // quality control does not grow the hot params header or shift volumes.
        private const int SecondVolumeOwnershipEarlyOutThresholdShift = 12;
        private const uint SecondVolumeOwnershipEarlyOutThresholdMask = 0xffu << SecondVolumeOwnershipEarlyOutThresholdShift;
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
        private const uint InactiveProbeRetryFrames = 8u;
        private const int SchedulerWorkClassCount = (int)SimpleDdgiSchedulerWorkClass.Count;
        private const byte ProbeSchedulingScrollExposedFlag = 1 << 0;
        private const byte ProbeSchedulingRegionalDirtyFlag = 1 << 1;
        private const byte ProbeSchedulingVisibleFlag = 1 << 2;
        private const int SchedulerVisibleImportanceThreshold = 4;
        // Exact buckets keep the published P50/P95 meaningful during a soak.
        // The former 15+ bucket made a 773-frame convergence tail appear as a
        // harmless 15-frame P95.  4K buckets cost 32 KiB for both histograms and
        // cover well beyond the documented response window without allocations.
        private const int DirtyLatencyBucketCount = 4_096;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly List<RetiredBufferResource> _retiredBuffers = new();
        private readonly List<VolumeCandidate> _volumeCandidates = new(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount + 3);
        private readonly GPUSimpleDdgiVolume[] _volumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiVolume[] _previousVolumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly SimpleDdgiVolumePurpose[] _volumePurposes = new SimpleDdgiVolumePurpose[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumePriorities = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiProbeUpdate[] _updateQueueScratch = new GPUSimpleDdgiProbeUpdate[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly GPUSimpleDdgiProbeState[] _probeStateScratch = new GPUSimpleDdgiProbeState[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly List<int> _probeStateDirtySlots = new(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
        private readonly List<BufferUploadRun> _probeStateUploadRuns = new(256);
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
        private readonly int[] _scheduledWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _reservedWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _pendingWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly int[] _rayRejectedWorkClassCounts = new int[SchedulerWorkClassCount];
        private readonly byte[] _queuedWorkClassScratch = new byte[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private byte[] _probeFresh = Array.Empty<byte>();
        private byte[] _probeInactive = Array.Empty<byte>();
        private byte[] _probeQueued = Array.Empty<byte>();
        // Per-physical-slot metadata follows the same toroidal mapping as fresh,
        // relocation, and generation state. It lets scheduling distinguish a local
        // dirty event from ordinary maintenance without inspecting managed regions
        // after the upload path has returned.
        private byte[] _probeSchedulingFlags = Array.Empty<byte>();
        private byte[] _probeDirtyReasons = Array.Empty<byte>();
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
        // These mirrors make a full state upload safe during a scroll or regional
        // invalidation.  Without them, a fresh-slice upload would accidentally
        // erase relocation/classification for every preserved physical slot.
        private Vector3[] _probeRelocations = Array.Empty<Vector3>();
        private float[] _probeActiveWeights = Array.Empty<float>();
        private uint[] _probeClassifications = Array.Empty<uint>();
        private byte[] _probeStableUpdateCounts = Array.Empty<byte>();
        private float[] _probeLuminanceChangeEma = Array.Empty<float>();
        private uint[] _probeAges = Array.Empty<uint>();
        // Source-cache lifetime follows the physical probe slot.  A lighting
        // generation changes globally; geometry/scroll invalidation clears only
        // the affected slot, preserving static source work elsewhere.
        private uint[] _probeSourceLightingGenerations = Array.Empty<uint>();
        private uint[] _probeLastSourceRefreshFrames = Array.Empty<uint>();
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
        private readonly Dictionary<
            (int VolumeKind, int SourceOrdinal, SimpleDdgiSchedulerWorkClass WorkClass),
            int> _volumeWorkClassRoundRobinCursors = new(
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * SchedulerWorkClassCount);
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
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _relocationClassificationBuffer;
        private readonly BufferHandle[] _probeStateReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly bool[] _probeStateReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly int[] _probeStateReadbackProbeCounts = new int[RenderingConstants.FramesInFlight];
        private readonly ulong[] _probeStateReadbackBytes = new ulong[RenderingConstants.FramesInFlight];
        private readonly uint[] _probeStateReadbackGenerations = new uint[RenderingConstants.FramesInFlight];
        // Markers identify exactly which physical slots were completed by the
        // readback frame.  Stability must never advance merely because an old
        // state buffer happened to be copied again.
        private readonly uint[][] _probeStateReadbackUpdateMarkers = new uint[RenderingConstants.FramesInFlight][];
        private readonly uint[][] _probeStateReadbackExpectedProbeGenerations = new uint[RenderingConstants.FramesInFlight][];
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
        private ulong _probeUpdateQueueBytes;
        private ulong _relocationClassificationBytes;
        private ulong _probeStateReadbackBufferBytes;
        private BindlessHeap? _registeredBindlessHeap;
        // The SSBO atlases remain the canonical writer and rollback path.  The
        // optional image mirror exists only for controlled sampled-atlas A/B
        // captures, so allocation or descriptor failures never disable DDGI.
        private SimpleDdgiSampledAtlas? _sampledAtlas;
        private string _sampledAtlasFallbackReason = string.Empty;
        // Avoid retrying a known-unsatisfied image allocation every frame. A
        // topology change or explicit feature toggle clears this latch.
        private int _sampledAtlasFailedProbeCount = -1;
        private ulong _sampledAtlasFailureBudgetBytes;
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
        private int _probeStateReadbackValid;
        private uint _volumeTableGeneration;
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
        private int _transportPublishedProbeCount;
        private int _transportPublishRegionCount;
        private ulong _sourceCacheInvalidationCount;
        private uint _sourceLightingGeneration = 1u;
        private uint _transportGeneration;
        // A local residual alone cannot prove that multi-bounce transport has
        // reached a probe whose neighbors are still warming. Global source
        // changes therefore enter a bounded field-wide solve phase: every
        // physical probe receives the configured minimum number of Jacobi
        // iterations before any local residual is allowed to retire it.
        private bool _transportGlobalConvergencePending = true;
        private uint _transportGlobalConvergenceSourceGeneration = 1u;
        private uint _transportGlobalConvergenceStartFrame;
        private ulong _transportCalibrationChangeCount;
        private bool _transportV2WasActive;
        private int _effectiveMaxShadedLights;
        private ulong _adaptiveRaySavedPrimaryRayCount;
        private int _rayBudgetRejectedProbeCount;
        private ulong _rayBudgetRejectedPrimaryRayCount;
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
        private uint _updateTransactionSerial;
        private ulong _frameSerial;
        private SimpleDdgiLayoutReport? _lastLayoutReport;
        // Ring origins may move every frame, but allocation admission only depends
        // on topology and tier inputs. Cache the immutable report so normal camera
        // travel does not allocate request records, decision lists, or hash sets.
        private SimpleDdgiLayoutReport? _cachedLayoutReport;
        private HashSet<int>? _cachedAcceptedSourceOrdinals;
        private ulong _cachedLayoutFingerprint;
        // Publication coalesces contiguous physical probe ranges before issuing
        // copies.  Both retained arrays make this render-thread path allocation
        // free even at the largest accepted queue size.
        private readonly int[] _transportPublishProbeIndices = new int[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly BufferCopy[] _transportPublishCopies = new BufferCopy[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private bool _disposed;

        public SimpleDdgiVolumeManager(VulkanContext context, BufferManager bufferManager, RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

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
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        /// <summary>CPU time for the post-blend incremental sampled-atlas mirror.</summary>
        public long LastSampledAtlasSynchronizationMicroseconds => _lastSampledAtlasSynchronizationMicroseconds;
        public ulong BufferBytes => ParamsBufferSize +
            _irradianceAtlasBytes +
            _transportIrradianceAtlasBytes +
            _transportSourceCacheBytes +
            _visibilityAtlasBytes +
            _rayScratchBytes +
            _probeStateBytes +
            _probeUpdateQueueBytes +
            _relocationClassificationBytes +
            _probeStateReadbackBufferBytes;
        public ulong IrradianceAtlasBytes => _irradianceAtlasBytes;
        /// <summary>Private V2 Jacobi target; never sampled by receivers.</summary>
        public ulong TransportIrradianceAtlasBytes => _transportIrradianceAtlasBytes;
        /// <summary>Persistent V2 direct/sky/emissive source cache allocation.</summary>
        public ulong TransportSourceCacheBytes => _transportSourceCacheBytes;
        public ulong VisibilityAtlasBytes => _visibilityAtlasBytes;
        /// <summary>Canonical SSBO atlas allocation only.</summary>
        public ulong AtlasBufferBytes => _irradianceAtlasBytes + _visibilityAtlasBytes;
        /// <summary>Optional sampled-image mirror allocation.</summary>
        public ulong SampledAtlasImageBytes => _sampledAtlas?.EstimatedImageBytes ?? 0UL;
        public int SampledAtlasGroupCount => _sampledAtlas?.GroupCount ?? 0;
        public int SampledAtlasLayersPerTexture => _sampledAtlas?.LayersPerTexture ?? 0;
        /// <summary>Total atlas allocation across SSBO writer and optional images.</summary>
        public ulong AtlasBytes => checked(AtlasBufferBytes + SampledAtlasImageBytes);
        public ulong RayScratchBytes => _rayScratchBytes;
        public ulong ProbeStateBytes => _probeStateBytes;
        public ulong ProbeUpdateQueueBytes => _probeUpdateQueueBytes;
        public ulong RelocationClassificationBytes => _relocationClassificationBytes;
        public ulong ProbeStateReadbackBytes => _probeStateReadbackBufferBytes;
        /// <summary>All non-atlas buffer allocation; never subtract image bytes from buffers.</summary>
        public ulong NonAtlasBufferBytes => BufferBytes - AtlasBufferBytes;
        public bool SampledAtlasRequested => _settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled;
        public bool SampledAtlasActive => SampledAtlasRequested &&
            _registeredBindlessHeap != null &&
            _sampledAtlas?.IsReady == true;
        public string SampledAtlasFallbackReason => SampledAtlasActive
            ? string.Empty
            : _sampledAtlasFallbackReason;
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
        public BufferHandle ProbeUpdateQueueBuffer => _probeUpdateQueueBuffer;
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
        public int SourceCacheReuseProbeCount => _sourceCacheReuseProbeCount;
        public int TransportPublishedProbeCount => _transportPublishedProbeCount;
        public int TransportPublishRegionCount => _transportPublishRegionCount;
        public ulong SourceCacheInvalidationCount => _sourceCacheInvalidationCount;
        public uint SourceLightingGeneration => _sourceLightingGeneration;
        public uint TransportGeneration => _transportGeneration;
        public bool TransportV2Active => _settings.GlobalIllumination.SimpleDdgiTransportV2Enabled;
        /// <summary>
        /// True while a global source or layout change is still receiving its
        /// bounded field-wide multi-bounce solve. This distinguishes legitimate
        /// warmup from an unexpectedly dim, already-converged field in captures.
        /// </summary>
        public bool TransportGlobalConvergencePending =>
            TransportV2Active && _transportGlobalConvergencePending;
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

        /// <summary>
        /// Monotonic count of live V2 source/solver calibration changes that
        /// restarted convergence. This is intentionally separate from physical
        /// cache invalidation count, which is measured in affected probe slots.
        /// </summary>
        public ulong TransportCalibrationChangeCount => _transportCalibrationChangeCount;

        /// <summary>
        /// Snapshot of V2 source/solver state for diagnostics. The scan is over
        /// bounded CPU metadata only; it neither reads GPU memory nor changes
        /// scheduling state.
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

            for (int probeIndex = 0; probeIndex < _probeCount; probeIndex++)
            {
                // Inactive probes contribute no receiver-visible interpolation
                // mass. They retain the bounded classification retry path, but
                // an embedded/relocating tail must not hold every active probe in
                // field-wide full-ray warmup indefinitely.
                if (!ShouldParticipateInTransportConvergence(
                        (uint)probeIndex < (uint)_probeInactive.Length &&
                        _probeInactive[probeIndex] != 0))
                {
                    continue;
                }

                if (NeedsSourceRefresh(probeIndex))
                {
                    sourceStaleProbeCount++;
                    continue;
                }

                sourceReadyProbeCount++;
                if (IsTransportConverged(probeIndex))
                    convergedProbeCount++;
                else
                    pendingSolverProbeCount++;
            }
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
            ScheduledFreshExposedVisible: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible],
            ScheduledVisibleDirty: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty],
            ScheduledVisibleRetry: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry],
            ScheduledNearMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance],
            ScheduledMidMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance],
            ScheduledFarMaintenance: _scheduledWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance],
            ReservedFreshExposedVisible: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible],
            ReservedVisibleDirty: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty],
            ReservedVisibleRetry: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry],
            ReservedNearMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance],
            ReservedMidMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance],
            ReservedFarMaintenance: _reservedWorkClassCounts[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance],
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
            if (feedback.DeterministicFixedBudget || feedback.TargetGpuMicroseconds == 0)
            {
                _schedulerFeedbackRequestBudgetCap = 0;
                return;
            }

            int lastEffectiveBudget = Math.Max(1, _schedulerEffectiveRequestBudget);
            if (_schedulerFeedbackRequestBudgetCap <= 0)
                _schedulerFeedbackRequestBudgetCap = lastEffectiveBudget;

            if (feedback.CompletedGpuMicroseconds > feedback.TargetGpuMicroseconds)
            {
                // At most a quarter reduction per resolved sample prevents timer
                // noise from collapsing a healthy queue, while still responding
                // quickly to sustained over-budget completed work.
                int reduction = Math.Max(1, _schedulerFeedbackRequestBudgetCap / 4);
                _schedulerFeedbackRequestBudgetCap = Math.Max(1, _schedulerFeedbackRequestBudgetCap - reduction);
            }
            else if (feedback.CompletedGpuMicroseconds <=
                feedback.TargetGpuMicroseconds - feedback.TargetGpuMicroseconds / 4UL)
            {
                // Recovery is deliberately slower than pressure reduction and is
                // based only on an under-target completed sample.
                int recovery = Math.Max(1, _schedulerFeedbackRequestBudgetCap / 8);
                _schedulerFeedbackRequestBudgetCap = Math.Min(
                    Math.Max(1, _schedulerConfiguredRequestBudget),
                    _schedulerFeedbackRequestBudgetCap + recovery);
            }
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
        public int ProbeStateReadbackValid => _probeStateReadbackValid;
        public GPUSimpleDdgiParams LastParams => _lastParams;
        public ReadOnlySpan<GPUSimpleDdgiVolume> LastVolumes => new(_volumeScratch, 0, _volumeCount);
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
            int configuredRequestBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(_probeCount, Math.Max(0, gi.SimpleDdgiProbeUpdatesPerFrame));
            return new SimpleDdgiSchedulingTelemetry(
                IsAvailable: true,
                ConfiguredRequestBudget: configuredRequestBudget,
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
            _relocateClassifyTransactionExecuted && (!TransportV2Active || _transportTransactionExecuted);
        // Async planning asks all pass predicates before any command has been
        // recorded.  These predicates deliberately describe a transaction that is
        // safe to schedule, while CanExecute* above remains the stricter guard at
        // recording time.  This keeps the three passes together in a split queue
        // plan without permitting a consumer to read an older scratch result.
        public bool CanScheduleRelocateClassifyTransaction => _updateTransactionPending && !_relocateClassifyTransactionExecuted;
        public bool CanScheduleTransportTransaction => TransportV2Active && _updateTransactionPending && !_transportTransactionExecuted;
        public bool CanScheduleBlendTransaction => _updateTransactionPending;
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
                DdgiProbeVolumeKind kind = Kind(volume) == VolumeKindAuthored
                    ? DdgiProbeVolumeKind.Authored
                    : DdgiProbeVolumeKind.CameraClipmap;
                int cascadeIndex = Kind(volume) == VolumeKindRing
                    ? Math.Clamp(SourceOrdinal(volume) - 10_000, 0, 2)
                    : 0;
                float volumeCubicMeters = Math.Max(size.X * size.Y * size.Z, 0.0001f);
                int firstProbe = FirstProbe(volume);
                int ageCount = firstProbe >= 0 && firstProbe < _probeAges.Length
                    ? Math.Min(probeCount, _probeAges.Length - firstProbe)
                    : 0;
                uint estimatedAgeP95 = ageCount > 0
                    ? CalculateProbeAgePercentile(
                        _probeAges.AsSpan(firstProbe, ageCount),
                        _probeAgePercentileScratch.AsSpan(0, ageCount),
                        0.95f)
                    : 0u;
                int inactiveProbeCount = _probeStateReadbackValid != 0
                    ? CountInactiveProbes(_probeInactive, firstProbe, probeCount)
                    : 0;
                int activeProbeCount = Math.Max(0, probeCount - inactiveProbeCount);
                SimpleDdgiLayoutVolumeDecision? layoutDecision = FindLayoutDecision(SourceOrdinal(volume));

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
                    PhysicalProbeCapacity = probeCount,
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
                    ProbeStateCountsValid = _probeStateReadbackValid,
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
                    LayoutRequestedPersistentBytes = layoutDecision == null
                        ? 0UL
                        : SimpleDdgiLayoutCompiler.EstimatePersistentBytes(
                            layoutDecision.Request.ProbeCount,
                            _settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled),
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

        public void GetEstimatedProbeAgeFrames(out float p50, out float p95, out float maximum)
        {
            maximum = 0.0f;
            long totalProbeCount = 0;
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[i];
                int probeCount = VolumeProbeCount(volume);
                int scheduled = Math.Max(0, (int)MathF.Round(volume.UpdateStartAndCount.Y));
                if (probeCount <= 0 || scheduled <= 0)
                    continue;
                maximum = Math.Max(maximum, probeCount / (float)scheduled);
                totalProbeCount += probeCount;
            }

            p50 = EstimateProbeAgePercentile(0.50f, maximum, totalProbeCount);
            p95 = EstimateProbeAgePercentile(0.95f, maximum, totalProbeCount);
        }

        private float EstimateProbeAgePercentile(float percentile, float maximum, long totalProbeCount)
        {
            if (maximum <= 0.0f || totalProbeCount <= 0)
                return 0.0f;

            float low = 0.0f;
            float high = maximum;
            for (int iteration = 0; iteration < 24; iteration++)
            {
                float candidate = (low + high) * 0.5f;
                double coveredProbeCount = 0.0;
                for (int i = 0; i < _volumeCount; i++)
                {
                    GPUSimpleDdgiVolume volume = _volumeScratch[i];
                    int probeCount = VolumeProbeCount(volume);
                    int scheduled = Math.Max(0, (int)MathF.Round(volume.UpdateStartAndCount.Y));
                    if (probeCount <= 0 || scheduled <= 0)
                        continue;
                    float sweepFrames = probeCount / (float)scheduled;
                    coveredProbeCount += probeCount * Math.Clamp(candidate / sweepFrames, 0.0f, 1.0f);
                }

                if (coveredProbeCount / totalProbeCount >= percentile)
                    high = candidate;
                else
                    low = candidate;
            }

            return high;
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

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.SimpleDdgiParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsBufferSize));
            RegisterIfValid(BindlessIndex.SimpleDdgiIrradianceAtlasBuffer, _irradianceAtlasBuffer, _irradianceAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer, _transportIrradianceAtlasBuffer, _transportIrradianceAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer, _transportSourceCacheBuffer, _transportSourceCacheBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiVisibilityAtlasBuffer, _visibilityAtlasBuffer, _visibilityAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiRayResultScratchBuffer, _rayResultScratchBuffer, _rayScratchBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiProbeStateBuffer, _probeStateBuffer, _probeStateBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiRelocationClassificationBuffer, _relocationClassificationBuffer, _relocationClassificationBytes);
            UpdateSampledAtlasCapacity(_probeCount);
            _sampledAtlas?.Register(bindlessHeap);
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
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions = null)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
            RenderingConstants.ValidateFrameIndex(frameIndex);

            long uploadStart = Stopwatch.GetTimestamp();
            try
            {
            BeginFrameResourceRetirement();
            ResetFrameCounters();

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            bool enabled = gi.EffectiveUseSimpleDdgi;
            if (!enabled)
            {
                DisableCore(gi, stagingRing, commandBuffer);
                return;
            }

            BoundingBox sceneBounds = ExpandBounds(DdgiFrameLayoutBuilder.EstimateSceneProbeBounds(scene), gi.SimpleDdgiRingBaseSpacing * 1.5f);
            int previousProbeCount = _probeCount;
            int previousVolumeCount = _volumeCount;
            CapturePreviousVolumes();
            BuildVolumeTable(gi, sceneBounds, cameraPosition);
            EnsureCpuProbeStateCapacity(_probeCount);
            if (VolumeTableRemapped(previousProbeCount, previousVolumeCount))
                AdvanceVolumeTableGenerationAndDropPendingReadbacks();
            ReadCompletedProbeStateReadback(frameIndex);
            bool hasRegionalDirtyWork = gi.SimpleDdgiRegionalInvalidationEnabled && dirtyRegions is { Count: > 0 };
            bool requiresGlobalInvalidation = RequiresGlobalInvalidation(dirtyRegions);
            // The dispatch allocation uses the largest selected ring profile;
            // each queue item packs its own active ray count so mid/far rings do
            // not perform near-ring work.
            _raysPerProbe = ResolveMaximumRingFullRays(gi);
            bool sourceCacheCapacityWillChange =
                _transportSourceCacheRayCapacity != Math.Max(1, _raysPerProbe);
            UpdateLightingDirtyState(gi, lightingSignature, dirtyReasonFlags, suppressSignatureBoost: hasRegionalDirtyWork && !requiresGlobalInvalidation);
            UpdateTransportV2ActivationState(sourceCacheCapacityWillChange);
            UpdateTransportCalibrationState(gi, sourceCacheCapacityWillChange);
            if (_recenteredThisFrame)
            {
                _totalRecenterCount++;
                _framesSinceLastRecenter = 0;
            }

            int baseUpdateBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(_probeCount, gi.SimpleDdgiProbeUpdatesPerFrame);
            _schedulerConfiguredRequestBudget = baseUpdateBudget;
            int dirtyBoostedBudget = ResolveLightingDirtyUpdateBudget(gi, baseUpdateBudget);
            // Atlas growth can invalidate every physical slot. Establish storage
            // first so MarkFreshForNewOrScrolledProbes observes that invalidation;
            // transient feedback throttling must not change persistent capacity.
            EnsureCapacity(_probeCount, _raysPerProbe, dirtyBoostedBudget, commandBuffer);
            _schedulerCameraPosition = cameraPosition;
            MarkFreshForNewOrScrolledProbes();
            if (hasRegionalDirtyWork)
                MarkRegionalDirtyProbes(dirtyRegions!);
            UpdateTransportGlobalConvergenceState();
            int visibleFreshRecoveryBudget = RefreshProbeSchedulingImportance();

            int updateBudget = ResolveFeedbackLimitedUpdateBudget(
                dirtyBoostedBudget,
                visibleFreshRecoveryBudget);
            _schedulerEffectiveRequestBudget = updateBudget;
            _probesToUpdate = BuildUpdateQueue(updateBudget);
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
                _ageRefreshProbeCount = _probesToUpdate;
            }

            AnnotateVolumeUpdateRanges();
            PreserveToroidalAtlasData();
            ClearAtlasBuffersIfRequired(commandBuffer);
            SynchronizeSampledAtlasIfRequired(commandBuffer);

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
                EnvironmentRadianceAndIntensity = new Vector4(0.0f, 0.0f, 0.0f, environmentIntensity),
                // W scales only the environment complement for missing probe
                // ownership (at receivers and bounce hit points). Valid probe
                // transport, including trace misses, is intentionally unaffected.
                ProbeUpdateRange = new Vector4(_updateStartProbe, _probesToUpdate, _volumeCount, gi.EnvironmentFallbackIntensity),
                DebugAndBias = new Vector4((float)gi.DebugView, gi.DdgiSelfShadowBiasScale, gi.IndirectIntensity, gi.FarFieldMaxTraceSteps),
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
                    0.0f,
                    0.0f),
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
                    gi.SimpleDdgiTransportResidualThreshold,
                    gi.SimpleDdgiTransportMaximumSolverGenerations)
            };

            UploadParams(stagingRing, commandBuffer);
            _controlHeaderInitialized = true;
            _wasSimpleDdgiEnabled = true;
            UploadProbeState(stagingRing, commandBuffer);
            UploadProbeUpdateQueue(stagingRing, commandBuffer);
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
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
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
            try
            {
                BeginFrameResourceRetirement();
                ResetFrameCounters();
                DisableCore(_settings.GlobalIllumination, stagingRing, commandBuffer);
            }
            finally
            {
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
            }
        }

        private void DisableCore(
            GlobalIlluminationSettings settings,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            _volumeCount = 0;
            _probeCount = 0;
            _lastLayoutReport = null;
            _probesToUpdate = 0;
            _activeProbeCount = 0;
            _probeStateReadbackValid = 0;
            _hasGridOrigin = false;
            Array.Fill(_ringHasOrigins, false);
            _atlasClearRequired = true;
            _atlasFresh = true;
            AbortUpdateTransaction();
            UpdateSampledAtlasCapacity(0);
            Array.Clear(_volumeScratch);
            Array.Clear(_volumePurposes);
            Array.Clear(_volumePriorities);
            Array.Clear(_probeDirtyLatencyStates);
            Array.Clear(_probeDirtyLatencyStartFrames);
            Array.Clear(_probeSchedulingFlags);
            Array.Clear(_probeDirtyReasons);
            Array.Clear(_probeVisibilityImportance);
            Array.Clear(_probeSourceLightingGenerations);
            Array.Clear(_probeLastSourceRefreshFrames);
            Array.Clear(_probeSourceRayCounts);
            Array.Clear(_probeTransportGenerationCounts);
            _hasTransportCalibrationSignatures = false;
            _transportV2WasActive = false;
            BeginTransportGlobalConvergence();
            _dirtyLatencyOutstandingEventCount = 0;
            _lastParams = CreateDisabledParams(settings);
            UploadParams(stagingRing, commandBuffer);
            _controlHeaderInitialized = true;
            _wasSimpleDdgiEnabled = false;
            _frameIndex++;
        }

        public void MarkBlendExecuted()
        {
            if (!CanExecuteBlendTransaction)
                return;

            if (_probesToUpdate > 0)
            {
                uint completedFrame = unchecked(_frameIndex - 1u);
                for (int i = 0; i < _probesToUpdate; i++)
                {
                    GPUSimpleDdgiProbeUpdate update = _updateQueueScratch[i];
                    int probeIndex = checked((int)update.ProbeIndex);
                    if ((uint)probeIndex < (uint)_probeFresh.Length)
                    {
                        _probeFresh[probeIndex] = 0;
                        if ((uint)probeIndex < (uint)_probeSchedulingFlags.Length)
                            _probeSchedulingFlags[probeIndex] = 0;
                        if ((uint)probeIndex < (uint)_probeDirtyReasons.Length)
                            _probeDirtyReasons[probeIndex] = 0;
                        _probeAges[probeIndex] = 0;
                        if (TransportV2Active)
                        {
                            bool sourceRefresh = (update.Flags & ProbeUpdateSourceRefreshFlag) != 0u;
                            if (sourceRefresh)
                            {
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
                                _probeTransportGenerationCounts[probeIndex] = 1;
                                // Convergence evidence belongs to the prior
                                // source field. Reset it when direct/sky/
                                // emissive input changes so a previously quiet
                                // probe cannot be declared solved solely from
                                // stale readback residuals.
                                if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                                    _probeStableUpdateCounts[probeIndex] = 0;
                                if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                                    _probeLuminanceChangeEma[probeIndex] = 0.0f;
                            }
                            else if ((uint)probeIndex < (uint)_probeTransportGenerationCounts.Length)
                            {
                                _probeTransportGenerationCounts[probeIndex] = (byte)Math.Min(
                                    byte.MaxValue,
                                    _probeTransportGenerationCounts[probeIndex] + 1);
                            }
                        }
                        RecordDirtyFirstCompletedUpdate(probeIndex, completedFrame);
                    }
                }

                _atlasFresh = false;
            }

            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
            _transportTransactionExecuted = false;
        }

        /// <summary>
        /// Mirrors just-completed probe layers after the canonical blend writes.
        /// This records on the graphics queue while sampled images are not yet a
        /// first-class render-graph resource, preserving queue ownership safety.
        /// </summary>
        public void SynchronizeSampledAtlasesAfterBlend(CommandBuffer commandBuffer)
        {
            _lastSampledAtlasSynchronizationMicroseconds = 0;
            if (!SampledAtlasActive || _probesToUpdate <= 0 || _sampledAtlas == null)
                return;

            long syncStart = Stopwatch.GetTimestamp();
            try
            {
                _sampledAtlas.CopyUpdated(
                    commandBuffer,
                    _bufferManager.GetBuffer(_irradianceAtlasBuffer),
                    _irradianceAtlasBytes,
                    _bufferManager.GetBuffer(_visibilityAtlasBuffer),
                    _visibilityAtlasBytes,
                    new ReadOnlySpan<GPUSimpleDdgiProbeUpdate>(_updateQueueScratch, 0, _probesToUpdate));
            }
            finally
            {
                _lastSampledAtlasSynchronizationMicroseconds = ElapsedMicroseconds(syncStart);
            }
        }

        /// <summary>
        /// Publishes the completed V2 Jacobi target into the canonical atlas.
        /// The blend pass reads only the previously published canonical field and
        /// writes only the private target; this range copy is the sole visibility
        /// boundary for receiver sampling.  Incomplete transport work therefore
        /// cannot leak into a rendered frame.
        /// </summary>
        public unsafe void PublishTransportAtlasAfterBlend(CommandBuffer commandBuffer)
        {
            _transportPublishedProbeCount = 0;
            _transportPublishRegionCount = 0;
            if (!TransportV2Active || _probesToUpdate <= 0 ||
                !_transportIrradianceAtlasBuffer.IsValid || !_irradianceAtlasBuffer.IsValid ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            int selectedCount = 0;
            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
                if ((uint)probeIndex >= (uint)_probeCount)
                    continue;
                _transportPublishProbeIndices[selectedCount++] = probeIndex;
            }

            if (selectedCount == 0)
                return;

            Array.Sort(_transportPublishProbeIndices, 0, selectedCount);
            ulong probeBytes = checked((ulong)IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride);
            int copyCount = 0;
            int uniqueCount = 0;
            int runStart = _transportPublishProbeIndices[0];
            int previous = runStart;
            uniqueCount = 1;
            for (int index = 1; index < selectedCount; index++)
            {
                int probeIndex = _transportPublishProbeIndices[index];
                if (probeIndex == previous)
                    continue;

                uniqueCount++;
                if (probeIndex == previous + 1)
                {
                    previous = probeIndex;
                    continue;
                }

                _transportPublishCopies[copyCount++] = CreateTransportPublishCopy(runStart, previous, probeBytes);
                runStart = previous = probeIndex;
            }
            _transportPublishCopies[copyCount++] = CreateTransportPublishCopy(runStart, previous, probeBytes);

            Silk.NET.Vulkan.Buffer target = _bufferManager.GetBuffer(_transportIrradianceAtlasBuffer);
            Silk.NET.Vulkan.Buffer canonical = _bufferManager.GetBuffer(_irradianceAtlasBuffer);
            ulong targetBytes = Math.Min(_transportIrradianceAtlasBytes, _irradianceAtlasBytes);
            BufferMemoryBarrier2* before = stackalloc BufferMemoryBarrier2[2];
            before[0] = BarrierBuilder.BufferBarrier(
                target,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                targetBytes);
            before[1] = BarrierBuilder.BufferBarrier(
                canonical,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                0,
                _irradianceAtlasBytes);
            var beforeDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = before
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &beforeDependency);

            fixed (BufferCopy* copies = _transportPublishCopies)
                _context.Api.CmdCopyBuffer(commandBuffer, target, canonical, checked((uint)copyCount), copies);

            BufferMemoryBarrier2* after = stackalloc BufferMemoryBarrier2[2];
            after[0] = BarrierBuilder.BufferBarrier(
                canonical,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                // A sampled-atlas mirror may immediately issue a transfer read
                // after this publication. Include that consumer here as well as
                // the normal compute/fragment field readers; otherwise its
                // generic compute-write -> transfer barrier would not cover this
                // preceding transfer write.
                PipelineStageFlags2.TransferBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.TransferReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                _irradianceAtlasBytes);
            after[1] = BarrierBuilder.BufferBarrier(
                target,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                0,
                targetBytes);
            var afterDependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = after
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &afterDependency);

            _transportPublishedProbeCount = uniqueCount;
            _transportPublishRegionCount = copyCount;
            _transportGeneration = AdvanceSourceLightingGeneration(_transportGeneration);
        }

        private static BufferCopy CreateTransportPublishCopy(int firstProbe, int lastProbe, ulong probeBytes)
        {
            ulong offset = checked((ulong)Math.Max(firstProbe, 0) * probeBytes);
            ulong probeCount = checked((ulong)Math.Max(lastProbe - firstProbe + 1, 0));
            return new BufferCopy
            {
                SrcOffset = offset,
                DstOffset = offset,
                Size = checked(probeCount * probeBytes)
            };
        }

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

        public void AbortUpdateTransaction()
        {
            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
            _transportTransactionExecuted = false;
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
        }

        private void ResetFrameCounters()
        {
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
            _transportPublishedProbeCount = 0;
            _transportPublishRegionCount = 0;
            _effectiveMaxShadedLights = 0;
            _adaptiveRaySavedPrimaryRayCount = 0;
            _rayBudgetRejectedProbeCount = 0;
            _rayBudgetRejectedPrimaryRayCount = 0;
            _schedulerConfiguredRequestBudget = 0;
            _schedulerEffectiveRequestBudget = 0;
            _schedulerDeferredRequestCount = 0;
            _schedulerPressureReason = SimpleDdgiSchedulerPressureReason.None;
            Array.Clear(_scheduledWorkClassCounts);
            Array.Clear(_reservedWorkClassCounts);
            Array.Clear(_pendingWorkClassCounts);
            Array.Clear(_rayRejectedWorkClassCounts);
            Array.Clear(_volumeWorkClassPendingScratch);
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
            byte[] previousSchedulingFlags = _probeSchedulingFlags;
            byte[] previousDirtyReasons = _probeDirtyReasons;
            byte[] previousVisibilityImportance = _probeVisibilityImportance;
            byte[] previousStableUpdateCounts = _probeStableUpdateCounts;
            float[] previousLuminanceChangeEma = _probeLuminanceChangeEma;
            uint[] previousAges = _probeAges;
            uint[] previousSourceLightingGenerations = _probeSourceLightingGenerations;
            uint[] previousSourceRefreshFrames = _probeLastSourceRefreshFrames;
            ushort[] previousSourceRayCounts = _probeSourceRayCounts;
            byte[] previousTransportGenerationCounts = _probeTransportGenerationCounts;
            uint[] previousGenerations = _probeGenerations;
            Vector3[] previousRelocations = _probeRelocations;
            float[] previousActiveWeights = _probeActiveWeights;
            uint[] previousClassifications = _probeClassifications;
            byte[] previousDirtyLatencyStates = _probeDirtyLatencyStates;
            uint[] previousDirtyLatencyStartFrames = _probeDirtyLatencyStartFrames;
            _probeFresh = new byte[Math.Max(0, probeCount)];
            _probeInactive = new byte[Math.Max(0, probeCount)];
            _probeQueued = new byte[Math.Max(0, probeCount)];
            _probeSchedulingFlags = new byte[Math.Max(0, probeCount)];
            _probeDirtyReasons = new byte[Math.Max(0, probeCount)];
            _probeVisibilityImportance = new byte[Math.Max(0, probeCount)];
            _probeGenerations = new uint[Math.Max(0, probeCount)];
            _probeInvalidationMarkers = new uint[Math.Max(0, probeCount)];
            _probeRelocations = new Vector3[Math.Max(0, probeCount)];
            _probeActiveWeights = new float[Math.Max(0, probeCount)];
            _probeClassifications = new uint[Math.Max(0, probeCount)];
            _probeStableUpdateCounts = new byte[Math.Max(0, probeCount)];
            _probeLuminanceChangeEma = new float[Math.Max(0, probeCount)];
            _probeAges = new uint[Math.Max(0, probeCount)];
            _probeSourceLightingGenerations = new uint[Math.Max(0, probeCount)];
            _probeLastSourceRefreshFrames = new uint[Math.Max(0, probeCount)];
            _probeSourceRayCounts = new ushort[Math.Max(0, probeCount)];
            _probeTransportGenerationCounts = new byte[Math.Max(0, probeCount)];
            _probeDirtyLatencyStates = new byte[Math.Max(0, probeCount)];
            _probeDirtyLatencyStartFrames = new uint[Math.Max(0, probeCount)];
            int copyCount = Math.Min(probeCount, previousFresh.Length);
            Array.Copy(previousFresh, _probeFresh, copyCount);
            Array.Copy(previousInactive, _probeInactive, copyCount);
            Array.Copy(previousSchedulingFlags, _probeSchedulingFlags, Math.Min(copyCount, previousSchedulingFlags.Length));
            Array.Copy(previousDirtyReasons, _probeDirtyReasons, Math.Min(copyCount, previousDirtyReasons.Length));
            Array.Copy(previousVisibilityImportance, _probeVisibilityImportance, Math.Min(copyCount, previousVisibilityImportance.Length));
            Array.Copy(previousStableUpdateCounts, _probeStableUpdateCounts, Math.Min(copyCount, previousStableUpdateCounts.Length));
            Array.Copy(previousLuminanceChangeEma, _probeLuminanceChangeEma, Math.Min(copyCount, previousLuminanceChangeEma.Length));
            Array.Copy(previousAges, _probeAges, copyCount);
            Array.Copy(previousSourceLightingGenerations, _probeSourceLightingGenerations, Math.Min(copyCount, previousSourceLightingGenerations.Length));
            Array.Copy(previousSourceRefreshFrames, _probeLastSourceRefreshFrames, Math.Min(copyCount, previousSourceRefreshFrames.Length));
            Array.Copy(previousSourceRayCounts, _probeSourceRayCounts, Math.Min(copyCount, previousSourceRayCounts.Length));
            Array.Copy(previousTransportGenerationCounts, _probeTransportGenerationCounts, Math.Min(copyCount, previousTransportGenerationCounts.Length));
            Array.Copy(previousGenerations, _probeGenerations, Math.Min(copyCount, previousGenerations.Length));
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
            RecomputeDirtyLatencyOutstandingCount();
            _probeStateUploadRequired = true;
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
                    VolumeProbeCount(previous) != VolumeProbeCount(current) ||
                    !NearlyEqual(Spacing(previous), Spacing(current), 0.0001f))
                {
                    return true;
                }
            }

            return false;
        }

        private void AdvanceVolumeTableGenerationAndDropPendingReadbacks()
        {
            _volumeTableGeneration++;
            BeginTransportGlobalConvergence();
            for (int i = 0; i < _probeStateReadbackRecorded.Length; i++)
                _probeStateReadbackRecorded[i] = false;
            Array.Clear(_probeDirtyLatencyStates);
            Array.Clear(_probeDirtyLatencyStartFrames);
            Array.Clear(_probeSchedulingFlags);
            Array.Clear(_probeDirtyReasons);
            Array.Clear(_probeVisibilityImportance);
            _volumeWorkClassRoundRobinCursors.Clear();
            _dirtyLatencyOutstandingEventCount = 0;
            _probeStateReadbackValid = 0;
        }

        private void UpdateLightingDirtyState(
            GlobalIlluminationSettings settings,
            ulong lightingSignature,
            uint dirtyReasonFlags,
            bool suppressSignatureBoost)
        {
            if (!_hasLightingSignature)
            {
                _lastLightingSignature = lightingSignature;
                _sourceLightingGeneration = 1u;
                BeginTransportGlobalConvergence();
                _activeDirtyReasonFlags = 0u;
                _hasLightingSignature = true;
                return;
            }

            if (lightingSignature != _lastLightingSignature)
            {
                _lastLightingSignature = lightingSignature;
                _sourceLightingGeneration = AdvanceSourceLightingGeneration(_sourceLightingGeneration);
                BeginTransportGlobalConvergence();
                // The physical cache remains allocated, but every slot must trace
                // its source once under the new light/environment signature.
                _sourceCacheInvalidationCount = SaturatingAdd(
                    _sourceCacheInvalidationCount,
                    (ulong)Math.Max(_probeCount, 0));
                _lightingDirtyFrames = !suppressSignatureBoost && settings.SimpleDdgiLightingDirtyBoostEnabled
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
                BeginTransportGlobalConvergence();
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
            BeginTransportGlobalConvergence();
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

        private void BeginTransportGlobalConvergence()
        {
            _transportGlobalConvergencePending = true;
            _transportGlobalConvergenceSourceGeneration = _sourceLightingGeneration;
            _transportGlobalConvergenceStartFrame = _frameIndex;
        }

        private void UpdateTransportGlobalConvergenceState()
        {
            if (!TransportV2Active)
                return;

            if (_transportGlobalConvergenceSourceGeneration != _sourceLightingGeneration)
                BeginTransportGlobalConvergence();
            if (!_transportGlobalConvergencePending || _probeCount <= 0)
                return;

            int minimumSolverGenerations = Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiTransportMaximumSolverGenerations);
            int participatingProbeCount = 0;
            int sourceRepairProbeCount = 0;
            int pendingSolverProbeCount = 0;
            for (int probeIndex = 0; probeIndex < _probeCount; probeIndex++)
            {
                bool inactive = (uint)probeIndex < (uint)_probeInactive.Length &&
                    _probeInactive[probeIndex] != 0;
                if (!ShouldParticipateInTransportConvergence(inactive))
                    continue;

                participatingProbeCount++;
                if (NeedsSourceRefresh(probeIndex))
                {
                    sourceRepairProbeCount++;
                    continue;
                }

                int completedSolverGenerations = (uint)probeIndex < (uint)_probeTransportGenerationCounts.Length
                    ? _probeTransportGenerationCounts[probeIndex]
                    : 0;
                if (completedSolverGenerations < minimumSolverGenerations)
                    pendingSolverProbeCount++;
            }

            if (!CanCompleteTransportGlobalConvergence(
                    participatingProbeCount,
                    sourceRepairProbeCount,
                    pendingSolverProbeCount))
            {
                return;
            }

            // All receiver-participating source terms are current and have
            // contributed the requested number of Jacobi generations, apart from
            // a bounded local source-repair tail. Inactive probes remain on their
            // reactivation cadence and source-repair probes stay queued, while
            // local residual/stability criteria can now retire quiet active probes.
            _transportGlobalConvergencePending = false;
        }

        internal static bool ShouldParticipateInTransportConvergence(bool inactive) =>
            !inactive;

        internal static int ResolveTransportGlobalConvergenceSourceRepairAllowance(
            int participatingProbeCount)
        {
            // A field with fewer than 1,000 active probes must be completely
            // source-ready. Larger clipmaps may leave at most 0.1% (and never
            // more than 32 slots) on the local repair path. This prevents a
            // handful of continuously relocating probes from pinning the other
            // 99.9% in expensive full-ray warmup without hiding a broad outage.
            return Math.Min(32, Math.Max(0, participatingProbeCount) / 1_000);
        }

        internal static bool CanCompleteTransportGlobalConvergence(
            int participatingProbeCount,
            int sourceRepairProbeCount,
            int pendingSolverProbeCount)
        {
            int participants = Math.Max(0, participatingProbeCount);
            int sourceRepair = Math.Clamp(sourceRepairProbeCount, 0, participants);
            return Math.Max(0, pendingSolverProbeCount) == 0 &&
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

        private void MarkRegionalDirtyProbes(IReadOnlyList<DdgiDirtyRegion> dirtyRegions)
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
                        MarkProbeFresh(
                            FirstProbe(volume) + physicalLocal,
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

            int boosted = Math.Min(_probeCount, Math.Min(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount, checked(capacity * 2)));
            _lightingDirtyBoostedCapacity = Math.Max(0, boosted - capacity);
            return boosted;
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

            bool globalDirty = _lightingDirtyFrames > 0;
            int visibleFreshRecoveryBudget = 0;
            int requiredStableUpdates = Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiStableMaintenanceUpdateCount);
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                int firstProbe = FirstProbe(volume);
                int probeCount = VolumeProbeCount(volume);
                int visibleFreshCount = 0;
                for (int local = 0; local < probeCount; local++)
                {
                    int probeIndex = firstProbe + local;
                    if ((uint)probeIndex >= (uint)_probeVisibilityImportance.Length ||
                        (uint)probeIndex >= (uint)_probeSchedulingFlags.Length)
                    {
                        continue;
                    }

                    bool needsVisibility = globalDirty || _probeFresh[probeIndex] != 0 ||
                        (_probeSchedulingFlags[probeIndex] & (ProbeSchedulingScrollExposedFlag | ProbeSchedulingRegionalDirtyFlag)) != 0 ||
                        ((uint)probeIndex < (uint)_probeInactive.Length && _probeInactive[probeIndex] != 0) ||
                        (_probeStateReadbackValid != 0 &&
                            (uint)probeIndex < (uint)_probeStableUpdateCounts.Length &&
                            _probeStableUpdateCounts[probeIndex] < requiredStableUpdates);
                    if (!needsVisibility)
                    {
                        _probeVisibilityImportance[probeIndex] = 0;
                        _probeSchedulingFlags[probeIndex] &= unchecked((byte)~ProbeSchedulingVisibleFlag);
                        continue;
                    }

                    byte importance = CalculateProbeSchedulingImportance(probeIndex, volumeIndex);
                    _probeVisibilityImportance[probeIndex] = importance;
                    if (importance >= SchedulerVisibleImportanceThreshold)
                    {
                        _probeSchedulingFlags[probeIndex] |= ProbeSchedulingVisibleFlag;
                        bool freshOrExposed = _probeFresh[probeIndex] != 0 ||
                            (_probeSchedulingFlags[probeIndex] & ProbeSchedulingScrollExposedFlag) != 0;
                        if (freshOrExposed)
                            visibleFreshCount++;
                    }
                    else
                        _probeSchedulingFlags[probeIndex] &= unchecked((byte)~ProbeSchedulingVisibleFlag);
                }

                // The per-ring minimum is also the bounded recovery guarantee for
                // visible cells invalidated by camera-relative scrolling. Adaptive
                // feedback may still reduce maintenance work after these cells are
                // populated, but it cannot leave a moving receiver sampling empty
                // atlas slots one probe at a time.
                int volumeMinimum = Math.Max(0, ResolveVolumeQuality(volumeIndex).MinimumUpdateQuota);
                visibleFreshRecoveryBudget = SaturatingAdd(
                    visibleFreshRecoveryBudget,
                    Math.Min(visibleFreshCount, volumeMinimum));
            }

            return visibleFreshRecoveryBudget;
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

            int importance = Kind(volume) == VolumeKindAuthored
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

            (int x, int y, int z) = CalculateLogicalProbeCoordinate(volume, local);
            float spacing = Spacing(volume);
            Vector3 origin = Origin(volume);
            float dx = origin.X + x * spacing - _schedulerCameraPosition.X;
            float dy = origin.Y + y * spacing - _schedulerCameraPosition.Y;
            float dz = origin.Z + z * spacing - _schedulerCameraPosition.Z;
            float proximityRadius = Math.Max(
                spacing * 3.0f,
                Kind(volume) == VolumeKindRing
                    ? spacing * 4.0f
                    : Math.Max(_volumeScratch[volumeIndex].WorldMinAndEdgeFade.W, spacing * 2.0f));
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

            for (int i = 0; i < _probeCount; i++)
                _probeAges[i] = _probeAges[i] == uint.MaxValue ? uint.MaxValue : _probeAges[i] + 1u;
            Array.Clear(_probeQueued, 0, _probeCount);

            int count = 0;
            int[] quotas = ResolveVolumeQuotas(capacity);
            int[] used = _volumeQuotaUsageScratch;
            Array.Clear(used, 0, _volumeCount);

            BuildWorkClassReservations(quotas);

            // The first pass honors bounded per-volume class reservations. This
            // prevents continuous scroll exposure from consuming the full near
            // allocation before visible dynamic dirty/retry work or maintenance
            // receives its declared minimum share.
            for (int workClassIndex = 0; workClassIndex < SchedulerWorkClassCount && count < capacity; workClassIndex++)
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
            for (int workClassIndex = 0; workClassIndex < SchedulerWorkClassCount && count < capacity; workClassIndex++)
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

        private void BuildWorkClassReservations(int[] volumeQuotas)
        {
            Array.Clear(_volumeWorkClassPendingScratch);
            Array.Clear(_volumeWorkClassQuotaScratch);
            Array.Clear(_volumeWorkClassUsageScratch);
            Array.Clear(_pendingWorkClassCounts);
            Array.Clear(_reservedWorkClassCounts);

            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                int firstProbe = FirstProbe(volume);
                int probeCount = VolumeProbeCount(volume);
                int workClassOffset = WorkClassOffset(volumeIndex);
                for (int local = 0; local < probeCount; local++)
                {
                    int probeIndex = firstProbe + local;
                    if (!TryResolveProbeWorkClass(probeIndex, volumeIndex, out SimpleDdgiSchedulerWorkClass workClass))
                        continue;

                    int classIndex = (int)workClass;
                    _volumeWorkClassPendingScratch[workClassOffset + classIndex] = SaturatingAdd(
                        _volumeWorkClassPendingScratch[workClassOffset + classIndex],
                        1);
                    _pendingWorkClassCounts[classIndex] = SaturatingAdd(_pendingWorkClassCounts[classIndex], 1);
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
            if (lowerReservations >= budget && (fresh > 0 || dirty > 0 || retry > 0))
            {
                if (maintenanceReservation > 0)
                    maintenanceReservation--;
                else if (retryReservation > 0)
                    retryReservation--;
                else if (dirtyReservation > 0)
                    dirtyReservation--;
                lowerReservations--;
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
            bool reservedPass)
        {
            int classIndex = (int)workClass;
            for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
            {
                int quota = volumeIndex < volumeQuotas.Length ? volumeQuotas[volumeIndex] : 0;
                if (quota <= 0 || volumeUsage[volumeIndex] >= quota)
                    continue;

                int offset = WorkClassOffset(volumeIndex) + classIndex;
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
                    ref _volumeWorkClassUsageScratch[offset]);
            }
        }

        private void QueueWorkClassVolume(
            ref int count,
            int capacity,
            int volumeIndex,
            int volumeQuota,
            ref int volumeUsed,
            SimpleDdgiSchedulerWorkClass workClass,
            int classLimit,
            ref int classUsed)
        {
            if (volumeQuota <= volumeUsed || classLimit <= classUsed || (uint)volumeIndex >= (uint)_volumeCount)
                return;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = FirstProbe(volume);
            int probeCount = VolumeProbeCount(volume);
            var cursorKey = VolumeWorkClassCursorKey(volume, workClass);
            _volumeWorkClassRoundRobinCursors.TryGetValue(cursorKey, out int storedCursor);
            int cursor = Math.Clamp(storedCursor, 0, Math.Max(probeCount - 1, 0));
            int stride = ResolveProbeUpdateStride(probeCount);

            int visited = 0;
            while (visited < probeCount && volumeUsed < volumeQuota && classUsed < classLimit && count < capacity)
            {
                int local = (int)((cursor + (long)visited * stride) % probeCount);
                int probeIndex = firstProbe + local;
                visited++;
                if ((uint)probeIndex >= (uint)_probeCount || _probeQueued[probeIndex] != 0)
                    continue;

                if (ShouldSkipInactiveProbe(probeIndex))
                {
                    RecordInactiveProbeSkip(probeIndex);
                    continue;
                }

                if (!TryResolveProbeWorkClass(probeIndex, volumeIndex, out SimpleDdgiSchedulerWorkClass resolvedClass) ||
                    resolvedClass != workClass)
                {
                    continue;
                }

                uint flags = _probeInactive[probeIndex] != 0 ? ProbeStateInactiveFlag : 0u;
                if (!AddProbeUpdate(ref count, capacity, probeIndex, flags, workClass))
                    continue;

                volumeUsed++;
                classUsed++;
            }

            // Every saturated priority class must advance independently. Starting
            // visible retry/dirty scans at local probe zero each frame repeatedly
            // admitted the same prefix and left the tail stale indefinitely.
            _volumeWorkClassRoundRobinCursors[cursorKey] = probeCount > 0
                ? (int)((cursor + (long)visited * stride) % probeCount)
                : 0;
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
            workClass = ResolveSchedulerWorkClass(freshOrExposed, visible, dirty, retry, ringIndex);
            return true;
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

            if (_probeFresh[probeIndex] != 0 ||
                _probeSourceLightingGenerations[probeIndex] != _sourceLightingGeneration ||
                _probeSourceRayCounts[probeIndex] == 0)
            {
                return true;
            }

            uint elapsed = unchecked(_frameIndex - _probeLastSourceRefreshFrames[probeIndex]);
            bool periodicRefreshDue = elapsed >= (uint)Math.Max(
                1,
                _settings.GlobalIllumination.SimpleDdgiTransportSourceRefreshFrames);
            // A field-wide warmup owns a coherent source generation until every
            // physical slot has received the requested minimum bounce work. If
            // early probes refreshed again on a short cadence, their generation
            // count would continually reset before a large/low-budget field had
            // a chance to converge. Explicit source invalidation above still
            // wins immediately; only routine resampling is deferred.
            return periodicRefreshDue && !TransportGlobalConvergencePending;
        }

        private bool IsTransportConverged(int probeIndex)
        {
            if (TransportGlobalConvergencePending)
                return false;

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if ((uint)probeIndex >= (uint)_probeTransportGenerationCounts.Length ||
                (uint)probeIndex >= (uint)_probeStableUpdateCounts.Length ||
                (uint)probeIndex >= (uint)_probeLuminanceChangeEma.Length)
            {
                return false;
            }

            return _probeTransportGenerationCounts[probeIndex] >=
                    gi.SimpleDdgiTransportMaximumSolverGenerations &&
                _probeStableUpdateCounts[probeIndex] >= gi.SimpleDdgiStableMaintenanceUpdateCount &&
                _probeLuminanceChangeEma[probeIndex] <= gi.SimpleDdgiTransportResidualThreshold;
        }

        internal static SimpleDdgiSchedulerWorkClass ResolveSchedulerWorkClass(
            bool freshOrExposed,
            bool visible,
            bool dirty,
            bool retry,
            int ringIndex)
        {
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

        private bool IsEligibleForVisibleRetry(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probeInactive.Length)
                return false;

            if (_probeInactive[probeIndex] != 0)
                return _probeAges[probeIndex] >= InactiveProbeRetryFrames;

            return _probeStateReadbackValid != 0 &&
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
                (uint)probeIndex < (uint)_probeAges.Length;
            return inRange && ShouldSkipInactiveProbeForScheduling(
                _settings.GlobalIllumination.SimpleDdgiClassificationSchedulingEnabled,
                _probeInactive[probeIndex] != 0,
                _probeFresh[probeIndex] != 0,
                _probeAges[probeIndex],
                InactiveProbeRetryFrames);
        }

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

        private void RecordInactiveProbeSkip(int probeIndex)
        {
            _inactiveProbeSkipCount++;
            int volumeIndex = ResolveVolumeIndexForProbe(probeIndex);
            _inactiveProbeSavedPrimaryRayCount += (ulong)ResolveVolumeQuality(volumeIndex).FullRays;
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
            int authoredOrDirtyBudget = _schedulerConfiguredRequestBudget;
            if (_settings.GlobalIllumination.SimpleDdgiLightingDirtyBoostEnabled && _lightingDirtyFrames > 0)
            {
                authoredOrDirtyBudget = Math.Min(
                    _probeCount,
                    Math.Min(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount, checked(authoredOrDirtyBudget * 2)));
            }
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
            uint flags,
            SimpleDdgiSchedulerWorkClass workClass)
        {
            if (count >= capacity || (uint)probeIndex >= (uint)_probeCount)
                return false;

            int volumeIndex = Math.Max(0, ResolveVolumeIndexForProbe(probeIndex));
            uint effectiveFlags = flags | (_probeFresh[probeIndex] != 0 ? ProbeStateFreshFlag : 0u);
            bool sourceRefresh = NeedsSourceRefresh(probeIndex);
            if (sourceRefresh)
                effectiveFlags |= ProbeUpdateSourceRefreshFlag;
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
            _updateQueueScratch[queueOffset] = new GPUSimpleDdgiProbeUpdate
            {
                ProbeIndex = checked((uint)probeIndex),
                VolumeIndex = checked((uint)volumeIndex),
                Flags = PackUpdateFlags(probeIndex, volumeIndex, effectiveFlags, requestedRays, sourceRefresh),
                Reserved0 = PackProbeUpdateMetadata(_probeGenerations[probeIndex], _probeAges[probeIndex]),
                SourceRayCount = checked((uint)Math.Clamp(sourceRayCount, 1, GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe)),
                SourceLightingGeneration = sourceRefresh ? _sourceLightingGeneration : 0u
            };
            _queuedWorkClassScratch[queueOffset] = (byte)workClass;
            int scheduledClassIndex = (int)workClass;
            if ((uint)scheduledClassIndex < (uint)_scheduledWorkClassCounts.Length)
                _scheduledWorkClassCounts[scheduledClassIndex] = SaturatingAdd(_scheduledWorkClassCounts[scheduledClassIndex], 1);
            _probeQueued[probeIndex] = 1;
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
                _probeStateReadbackValid == 0 ||
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
                _probeInvalidationMarkers[probeIndex] = _currentProbeInvalidationMarkerSerial;
                _newlyInvalidatedProbeCount++;
                _probeGenerations[probeIndex] = AdvanceProbeGeneration(_probeGenerations[probeIndex]);
                _probeRelocations[probeIndex] = Vector3.Zero;
                _probeActiveWeights[probeIndex] = 1.0f;
                _probeClassifications[probeIndex] = 0u;
                if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                    _probeStableUpdateCounts[probeIndex] = 0;
                if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                    _probeLuminanceChangeEma[probeIndex] = 0.0f;
                if ((uint)probeIndex < (uint)_probeSourceLightingGenerations.Length)
                    _probeSourceLightingGenerations[probeIndex] = 0u;
                if ((uint)probeIndex < (uint)_probeLastSourceRefreshFrames.Length)
                    _probeLastSourceRefreshFrames[probeIndex] = 0u;
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
                if ((uint)probeIndex < (uint)_probeAges.Length)
                    _probeAges[probeIndex] = 0u;
                _probeStateDirtySlots.Add(probeIndex);
            }
            _probeFresh[probeIndex] = 1;
            _probeInactive[probeIndex] = 0;
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
            if ((uint)probeIndex < (uint)_probeSourceRayCounts.Length)
                _probeSourceRayCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeTransportGenerationCounts.Length)
                _probeTransportGenerationCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeStableUpdateCounts.Length)
                _probeStableUpdateCounts[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeLuminanceChangeEma.Length)
                _probeLuminanceChangeEma[probeIndex] = 0.0f;
            // A per-slot cache repair is exceptional (normally a live resource
            // transition or corruption guard). Treat it as a new transport
            // boundary so neighboring bounce paths cannot remain retired while
            // this source term is rebuilt.
            BeginTransportGlobalConvergence();
            _sourceCacheInvalidationCount = SaturatingAdd(_sourceCacheInvalidationCount, 1UL);
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

        private void BuildVolumeTable(GlobalIlluminationSettings gi, BoundingBox sceneBounds, Vector3 cameraPosition)
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

            ulong layoutFingerprint = CalculateLayoutFingerprint(gi, _volumeCandidates);
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
                        ProbeCount: candidate.ProbeCount);
                }

                _cachedLayoutReport = SimpleDdgiLayoutCompiler.Compile(
                    layoutRequests,
                    SimpleDdgiLayoutBudget.Resolve(gi),
                    gi.SimpleDdgiSampledAtlasEnabled,
                    gi.SimpleDdgiLayoutAdmissionMode,
                    transportV2Enabled: true,
                    transportRayCapacity: ResolveMaximumRingFullRays(gi));
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
        }

        private static ulong CalculateLayoutFingerprint(
            GlobalIlluminationSettings settings,
            IReadOnlyList<VolumeCandidate> candidates)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            hash = AddLayoutFingerprintValue(hash, (ulong)settings.DdgiQualityTier, prime);
            hash = AddLayoutFingerprintValue(hash, settings.DdgiAtlasMemoryBudgetBytes, prime);
            hash = AddLayoutFingerprintValue(hash, settings.SimpleDdgiSampledAtlasEnabled ? 1UL : 0UL, prime);
            hash = AddLayoutFingerprintValue(hash, settings.SimpleDdgiTransportV2Enabled ? 1UL : 0UL, prime);
            hash = AddLayoutFingerprintValue(hash, (ulong)ResolveMaximumRingFullRays(settings), prime);
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
                unchecked((uint)BitConverter.SingleToInt32Bits(settings.SimpleDdgiTransportResidualThreshold)),
                prime);
            hash = AddLayoutFingerprintValue(
                hash,
                (ulong)(uint)settings.SimpleDdgiTransportMaximumSolverGenerations,
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
                volume.UpdateStartAndCount = Vector4.Zero;
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
                volume.UpdateStartAndCount = new Vector4(Math.Max(firstQueueOffset[i], 0), updatedCounts[i], 0.0f, 0.0f);
                _volumeScratch[i] = volume;
            }
        }

        private void UploadParams(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                _lastParams,
                new ReadOnlySpan<GPUSimpleDdgiVolume>(_volumeScratch, 0, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount),
                barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit));
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
                    for (int i = 0; i < _probeCount; i++)
                        _probeStateScratch[i] = BuildProbeStateRecord(i);
                    GpuBufferUploader.UploadSpanToBuffer(
                        _context, _bufferManager, stagingRing, commandBuffer, _probeStateBuffer,
                        new ReadOnlySpan<GPUSimpleDdgiProbeState>(_probeStateScratch, 0, _probeCount),
                        barrierDescription: barrier);
                }
                else if (stagedCount > 0)
                {
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
                Age = _probeAges[probeIndex],
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

        private void EnsureCapacity(int probeCount, int raysPerProbe, int probesToUpdate, CommandBuffer commandBuffer = default)
        {
            ulong irradianceBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride));
            ulong visibilityBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * VisibilityTexelsPerProbe * VisibilityTexelsPerProbe * AtlasTexelStride));
            ulong rayBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probesToUpdate) * (ulong)Math.Max(1, raysPerProbe) * RayResultStride));
            ulong probeStateBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * ProbeStateStride));
            ulong updateQueueBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probesToUpdate) * ProbeUpdateStride));
            ulong relocationClassificationBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * RelocationClassificationStride));

            // A growth can retain atlas history when every existing physical slot
            // still describes the same volume. This also covers appending a new
            // volume after unchanged existing volumes; its new slots are marked
            // fresh below while the copied prefix remains valid.
            bool preserveAtlasContents = CanPreserveAtlasContentsOnGrowth();
            EnsureBuffer(ref _irradianceAtlasBuffer, ref _irradianceAtlasBytes, irradianceBytes, "Simple DDGI Irradiance Atlas", invalidateAtlas: true, commandBuffer: commandBuffer, preserveContents: preserveAtlasContents);
            EnsureBuffer(ref _visibilityAtlasBuffer, ref _visibilityAtlasBytes, visibilityBytes, "Simple DDGI Visibility Atlas", invalidateAtlas: true, commandBuffer: commandBuffer, preserveContents: preserveAtlasContents);
            // Keep these allocations concrete even when V1 is selected so the
            // static render-graph declaration remains valid during a live V1/V2
            // toggle. V1 never dispatches the transport pass or touches them.
            ulong transportAtlasBytes = checked(Math.Max(
                MinBufferSize,
                (ulong)Math.Max(1, probeCount) * IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride));
            int sourceCacheRayCapacity = Math.Max(1, raysPerProbe);
            bool sourceCacheRayCapacityChanged =
                _transportSourceCacheRayCapacity != sourceCacheRayCapacity;
            ulong sourceCacheBytes = checked(Math.Max(
                MinBufferSize,
                (ulong)Math.Max(1, probeCount) * (ulong)sourceCacheRayCapacity * TransportRayCacheStride));
            EnsureBuffer(
                ref _transportIrradianceAtlasBuffer,
                ref _transportIrradianceAtlasBytes,
                transportAtlasBytes,
                "Simple DDGI Transport Irradiance Target",
                invalidateAtlas: false,
                commandBuffer: commandBuffer,
                preserveContents: false);
            bool sourceCacheReallocated = EnsureBuffer(
                ref _transportSourceCacheBuffer,
                ref _transportSourceCacheBytes,
                sourceCacheBytes,
                "Simple DDGI Transport Source Cache",
                invalidateAtlas: false,
                commandBuffer: commandBuffer,
                preserveContents: false);
            if (sourceCacheReallocated || sourceCacheRayCapacityChanged)
            {
                InvalidateTransportSourceCacheMetadata();
            }
            _transportSourceCacheRayCapacity = sourceCacheRayCapacity;
            EnsureBuffer(ref _rayResultScratchBuffer, ref _rayScratchBytes, rayBytes, "Simple DDGI Ray Scratch", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false);
            if (EnsureBuffer(ref _probeStateBuffer, ref _probeStateBytes, probeStateBytes, "Simple DDGI Probe State", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false))
                _probeStateUploadRequired = true;
            EnsureBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBytes, updateQueueBytes, "Simple DDGI Probe Update Queue", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false);
            EnsureBuffer(ref _relocationClassificationBuffer, ref _relocationClassificationBytes, relocationClassificationBytes, "Simple DDGI Relocation Classification", invalidateAtlas: false, commandBuffer: commandBuffer, preserveContents: false);
            UpdateSampledAtlasCapacity(probeCount);
        }

        private bool CanPreserveAtlasContentsOnGrowth()
        {
            if (_atlasFresh || _atlasClearRequired || _previousVolumeCount == 0)
                return false;

            for (int previousIndex = 0; previousIndex < _previousVolumeCount; previousIndex++)
            {
                GPUSimpleDdgiVolume previous = _previousVolumeScratch[previousIndex];
                bool foundCompatibleSlotRange = false;
                for (int currentIndex = 0; currentIndex < _volumeCount; currentIndex++)
                {
                    GPUSimpleDdgiVolume current = _volumeScratch[currentIndex];
                    if (Kind(previous) == Kind(current) &&
                        SourceOrdinal(previous) == SourceOrdinal(current) &&
                        FirstProbe(previous) == FirstProbe(current) &&
                        VolumeProbeCount(previous) == VolumeProbeCount(current) &&
                        NearlyEqual(Spacing(previous), Spacing(current), 0.0001f))
                    {
                        foundCompatibleSlotRange = true;
                        break;
                    }
                }

                if (!foundCompatibleSlotRange)
                    return false;
            }

            return true;
        }

        private unsafe bool EnsureBuffer(
            ref BufferHandle handle,
            ref ulong currentBytes,
            ulong requiredBytes,
            string debugName,
            bool invalidateAtlas,
            CommandBuffer commandBuffer,
            bool preserveContents)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return false;

            BufferHandle previousHandle = handle;
            ulong previousBytes = currentBytes;

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
                RetireBufferResource(previousHandle);
            if (invalidateAtlas)
            {
                if (!contentsPreserved)
                {
                    _atlasClearRequired = true;
                    _atlasFresh = true;
                }
                _sampledAtlas?.MarkFullSyncRequired();
            }

            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
            return true;
        }

        private void UpdateSampledAtlasCapacity(int probeCount)
        {
            if (!SampledAtlasRequested || probeCount <= 0)
            {
                _sampledAtlas?.Dispose();
                _sampledAtlas = null;
                _sampledAtlasFallbackReason = string.Empty;
                _sampledAtlasFailedProbeCount = -1;
                _sampledAtlasFailureBudgetBytes = 0;
                return;
            }

            if (_registeredBindlessHeap == null)
            {
                _sampledAtlasFallbackReason = "sampled-atlas-bindless-heap-unavailable";
                return;
            }

            ulong configuredBudgetBytes = _settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes;
            if (_sampledAtlasFailedProbeCount == probeCount &&
                _sampledAtlasFailureBudgetBytes == configuredBudgetBytes)
                return;

            if (WouldExceedSampledAtlasBudget(probeCount, configuredBudgetBytes))
            {
                _sampledAtlas?.Release();
                _sampledAtlasFallbackReason = "sampled-atlas-would-exceed-ddgi-memory-budget";
                _sampledAtlasFailedProbeCount = probeCount;
                _sampledAtlasFailureBudgetBytes = configuredBudgetBytes;
                return;
            }

            try
            {
                _sampledAtlas ??= new SimpleDdgiSampledAtlas(_context);
                if (_sampledAtlas.EnsureCapacity(probeCount, _registeredBindlessHeap))
                {
                    _sampledAtlasFallbackReason = string.Empty;
                    _sampledAtlasFailedProbeCount = -1;
                    _sampledAtlasFailureBudgetBytes = 0;
                    return;
                }

                _sampledAtlasFallbackReason = string.IsNullOrWhiteSpace(_sampledAtlas.LastFailureReason)
                    ? "sampled-atlas-allocation-unavailable"
                    : _sampledAtlas.LastFailureReason;
                _sampledAtlasFailedProbeCount = probeCount;
                _sampledAtlasFailureBudgetBytes = configuredBudgetBytes;
            }
            catch (VulkanException exception)
            {
                _sampledAtlas?.Dispose();
                _sampledAtlas = null;
                _sampledAtlasFallbackReason = $"sampled-atlas-vulkan-{exception.Result}";
                _sampledAtlasFailedProbeCount = probeCount;
                _sampledAtlasFailureBudgetBytes = configuredBudgetBytes;
            }
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
            ulong projectedImageBytes = Math.Max(SampledAtlasImageBytes, requiredImageBytes);
            return checked(BufferBytes + projectedImageBytes) > configuredBudgetBytes;
        }

        private void SynchronizeSampledAtlasIfRequired(CommandBuffer commandBuffer)
        {
            if (!SampledAtlasActive || _sampledAtlas?.RequiresFullSync != true)
                return;

            _sampledAtlas.CopyAll(
                commandBuffer,
                _bufferManager.GetBuffer(_irradianceAtlasBuffer),
                _irradianceAtlasBytes,
                _bufferManager.GetBuffer(_visibilityAtlasBuffer),
                _visibilityAtlasBytes,
                _probeCount);
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

        private void InvalidateTransportSourceCacheMetadata(bool recordInvalidation = true)
        {
            if (_probeSourceLightingGenerations.Length > 0)
                Array.Clear(_probeSourceLightingGenerations);
            if (_probeLastSourceRefreshFrames.Length > 0)
                Array.Clear(_probeLastSourceRefreshFrames);
            if (_probeSourceRayCounts.Length > 0)
                Array.Clear(_probeSourceRayCounts);
            if (_probeTransportGenerationCounts.Length > 0)
                Array.Clear(_probeTransportGenerationCounts);
            BeginTransportGlobalConvergence();
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

        private static uint PackProbeStateFlags(uint flags, uint generation) =>
            (flags & ~((uint)ProbeStateGenerationValueMask << ProbeStateGenerationShift)) |
            (NormalizeProbeGeneration(generation) << ProbeStateGenerationShift);

        private static uint ReadProbeStateGeneration(uint flags) =>
            NormalizeProbeGeneration((flags >> ProbeStateGenerationShift) & ProbeStateGenerationValueMask);

        private static uint Kind(GPUSimpleDdgiVolume volume) =>
            (uint)Math.Max(0, (int)MathF.Round(volume.WorldMaxAndKind.W));

        private static int SourceOrdinal(GPUSimpleDdgiVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.RaysAndReserved.X));

        private static (int VolumeKind, int SourceOrdinal, SimpleDdgiSchedulerWorkClass WorkClass) VolumeWorkClassCursorKey(
            GPUSimpleDdgiVolume volume,
            SimpleDdgiSchedulerWorkClass workClass) =>
            ((int)Kind(volume), SourceOrdinal(volume), workClass);

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
                DebugAndBias = new Vector4((float)settings.DebugView, settings.DdgiSelfShadowBiasScale, settings.IndirectIntensity, settings.FarFieldMaxTraceSteps),
                RotationQuaternion = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                BiasAndPadding = new Vector4(settings.SimpleDdgiNormalBias, settings.SimpleDdgiViewBias, settings.SimpleDdgiHysteresisChangeThreshold, settings.SimpleDdgiHysteresisStepThreshold),
                Reserved0 = Vector4.Zero,
                BiasLimitsAndPadding = new Vector4(
                    settings.SimpleDdgiMaximumWorldBiasMeters,
                    settings.SimpleDdgiArchitecturalThicknessMeters,
                    0.0f,
                    0.0f),
                TransportAndAtlasIndices = new Vector4(
                    PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    PackHeaderWord((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                    0.0f,
                    PackHeaderWord(_transportGeneration)),
                TransportControls = new Vector4(
                    settings.SimpleDdgiTransportSolverRelaxation,
                    settings.SimpleDdgiTransportAlbedoClamp,
                    settings.SimpleDdgiTransportResidualThreshold,
                    settings.SimpleDdgiTransportMaximumSolverGenerations)
            };
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
            if (_settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                settings.DebugView != GlobalIlluminationDebugView.None)
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
            if (!_settings.GlobalIllumination.SimpleDdgiClassificationReadbackEnabled ||
                commandBuffer.Handle == 0 ||
                !_probeStateBuffer.IsValid ||
                _probeCount <= 0)
                return;

            ulong copyBytes = checked((ulong)_probeCount * ProbeStateStride);
            EnsureProbeStateReadbackBuffer(frameIndex, Math.Max(MinBufferSize, copyBytes));
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
                copyBytes);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = copyBytes
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

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
            _probeStateReadbackGenerations[frameIndex] = _volumeTableGeneration;
            RecordProbeStateReadbackUpdatedSlots(frameIndex);
        }

        private void RecordProbeStateReadbackUpdatedSlots(int frameIndex)
        {
            if (_probeCount <= 0)
                return;

            uint[] markers = _probeStateReadbackUpdateMarkers[frameIndex] ?? Array.Empty<uint>();
            uint[] expectedGenerations = _probeStateReadbackExpectedProbeGenerations[frameIndex] ?? Array.Empty<uint>();
            if (markers.Length < _probeCount || expectedGenerations.Length < _probeCount)
            {
                markers = new uint[_probeCount];
                expectedGenerations = new uint[_probeCount];
                _probeStateReadbackUpdateMarkers[frameIndex] = markers;
                _probeStateReadbackExpectedProbeGenerations[frameIndex] = expectedGenerations;
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

            for (int queueOffset = 0; queueOffset < _probesToUpdate; queueOffset++)
            {
                int probeIndex = checked((int)_updateQueueScratch[queueOffset].ProbeIndex);
                if ((uint)probeIndex >= (uint)_probeCount)
                    continue;
                markers[probeIndex] = serial;
                expectedGenerations[probeIndex] = ReadProbeUpdateGeneration(_updateQueueScratch[queueOffset].Reserved0);
            }

            _probeStateReadbackUpdateMarkerSerials[frameIndex] = serial;
        }

        private unsafe void ReadCompletedProbeStateReadback(int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            if (!_settings.GlobalIllumination.SimpleDdgiClassificationReadbackEnabled ||
                !_probeStateReadbackRecorded[frameIndex] ||
                !_probeStateReadbackBuffers[frameIndex].IsValid)
            {
                _probeStateReadbackValid = 0;
                return;
            }

            if (_probeStateReadbackGenerations[frameIndex] != _volumeTableGeneration)
            {
                _probeStateReadbackRecorded[frameIndex] = false;
                _probeStateReadbackValid = 0;
                return;
            }

            int probeCount = Math.Min(_probeStateReadbackProbeCounts[frameIndex], _probeCount);
            ulong readBytes = Math.Min(_probeStateReadbackBytes[frameIndex], checked((ulong)Math.Max(probeCount, 0) * ProbeStateStride));
            if (probeCount <= 0 || readBytes < ProbeStateStride)
            {
                _probeStateReadbackRecorded[frameIndex] = false;
                _probeStateReadbackValid = 0;
                return;
            }

            _bufferManager.InvalidateBuffer(_probeStateReadbackBuffers[frameIndex], 0, readBytes);
            GPUSimpleDdgiProbeState* states = (GPUSimpleDdgiProbeState*)_bufferManager.GetMappedPointer(_probeStateReadbackBuffers[frameIndex]);
            int inactiveCount = 0;
            int activeCount = 0;
            int relocatedCount = 0;
            float relocationFractionSum = 0.0f;
            uint[] markers = _probeStateReadbackUpdateMarkers[frameIndex] ?? Array.Empty<uint>();
            uint[] expectedGenerations = _probeStateReadbackExpectedProbeGenerations[frameIndex] ?? Array.Empty<uint>();
            uint completedMarkerSerial = _probeStateReadbackUpdateMarkerSerials[frameIndex];

            for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                GPUSimpleDdgiProbeState state = states[probeIndex];
                // A readback can be in flight while a physical slot is reused by
                // toroidal scrolling or a dirty-region invalidation.  Never let
                // old relocation/classification history overwrite the new slot.
                if (ReadProbeStateGeneration(state.Flags) != NormalizeProbeGeneration(_probeGenerations[probeIndex]))
                {
                    if (_probeInactive[probeIndex] != 0)
                        inactiveCount++;
                    else
                        activeCount++;
                    continue;
                }

                Vector3 previousRelocation = _probeRelocations[probeIndex];
                Vector3 currentRelocation = new(
                    state.RelocationAndActive.X,
                    state.RelocationAndActive.Y,
                    state.RelocationAndActive.Z);
                bool inactive = state.Classification == 1u || state.RelocationAndActive.W <= 0.001f;
                _probeInactive[probeIndex] = inactive ? (byte)1 : (byte)0;
                _probeRelocations[probeIndex] = currentRelocation;
                _probeActiveWeights[probeIndex] = Math.Clamp(state.RelocationAndActive.W, 0.0f, 1.0f);
                _probeClassifications[probeIndex] = state.Classification;
                float luminanceChangeEma = Math.Max(BitConverter.UInt32BitsToSingle(state.Reserved0), 0.0f);
                if (!float.IsFinite(luminanceChangeEma))
                    luminanceChangeEma = 0.0f;
                _probeLuminanceChangeEma[probeIndex] = luminanceChangeEma;

                bool completedThisReadback =
                    (uint)probeIndex < (uint)markers.Length &&
                    (uint)probeIndex < (uint)expectedGenerations.Length &&
                    markers[probeIndex] == completedMarkerSerial &&
                    expectedGenerations[probeIndex] == NormalizeProbeGeneration(_probeGenerations[probeIndex]);
                float relocationDelta = (previousRelocation - currentRelocation).Length();
                bool materiallyRelocated = relocationDelta > ResolveProbeSpacing(probeIndex) * 0.05f;
                bool relocationRetracePending =
                    (state.Flags & ProbeStateRelocationPendingFlag) != 0u;
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
                    _probeFresh[probeIndex] = 1;

                if (inactive ||
                    relocationRetracePending ||
                    sourceCacheInvalid ||
                    materiallyRelocated ||
                    luminanceChangeEma > _settings.GlobalIllumination.SimpleDdgiStableMaintenanceEmaThreshold)
                {
                    _probeStableUpdateCounts[probeIndex] = 0;
                }
                else if (completedThisReadback && _probeStableUpdateCounts[probeIndex] < byte.MaxValue)
                {
                    _probeStableUpdateCounts[probeIndex]++;
                }
                if (completedThisReadback)
                    RecordDirtyConvergenceIfStable(probeIndex, _frameIndex);
                if (completedThisReadback && sourceCacheInvalid)
                {
                    // The GPU kept this transaction safe by falling back to
                    // source tracing, but a partial solver queue is not a valid
                    // long-term source cache. Requeue a complete source refresh
                    // without discarding a valid physical relocation or published
                    // irradiance generation.
                    MarkProbeSourceCacheStale(probeIndex);
                }
                if (inactive)
                    inactiveCount++;
                else
                    activeCount++;

                float relocationLength = new Vector3(
                    state.RelocationAndActive.X,
                    state.RelocationAndActive.Y,
                    state.RelocationAndActive.Z).Length();
                if (relocationLength > 0.001f)
                {
                    relocatedCount++;
                    float spacing = ResolveProbeSpacing(probeIndex);
                    relocationFractionSum += Math.Clamp(relocationLength / Math.Max(spacing * 0.45f, 0.001f), 0.0f, 1.0f);
                }
            }

            _activeProbeCount = activeCount;
            _classifiedInactiveProbeCountEstimate = inactiveCount;
            _probeRelocationCount = relocatedCount;
            _averageRelocationFractionEstimate = relocatedCount > 0 ? relocationFractionSum / relocatedCount : 0.0f;
            _probeStateReadbackValid = 1;
            _probeStateReadbackRecorded[frameIndex] = false;
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

        private void EnsureProbeStateReadbackBuffer(int frameIndex, ulong requiredBytes)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            if (_probeStateReadbackBuffers[frameIndex].IsValid &&
                _bufferManager.GetBufferSize(_probeStateReadbackBuffers[frameIndex]) >= requiredBytes)
            {
                return;
            }

            if (_probeStateReadbackBuffers[frameIndex].IsValid)
            {
                _probeStateReadbackBufferBytes -= _bufferManager.GetBufferSize(_probeStateReadbackBuffers[frameIndex]);
                RetireBufferResource(_probeStateReadbackBuffers[frameIndex]);
            }

            _probeStateReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                requiredBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Simple DDGI Probe State Readback Frame {frameIndex}",
                MemoryBudgetCategory.GlobalIllumination);
            _probeStateReadbackBufferBytes += requiredBytes;
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
            DrainRetiredResources(force: false);
        }

        private void RetireBufferResource(BufferHandle buffer)
        {
            if (!buffer.IsValid)
                return;

            _retiredBuffers.Add(new RetiredBufferResource(
                buffer,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
        }

        private void DrainRetiredResources(bool force)
        {
            for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            {
                RetiredBufferResource retired = _retiredBuffers[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBuffers.RemoveAt(i);
            }
        }

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
            if (_probeUpdateQueueBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeUpdateQueueBuffer);
            if (_relocationClassificationBuffer.IsValid)
                _bufferManager.DestroyBuffer(_relocationClassificationBuffer);
            for (int i = 0; i < _probeStateReadbackBuffers.Length; i++)
            {
                if (_probeStateReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_probeStateReadbackBuffers[i]);
            }
            DrainRetiredResources(force: true);
        }

        private readonly record struct RetiredBufferResource(
            BufferHandle Buffer,
            ulong RetireAfterFrameSerial);

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
