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
        private static readonly ulong ProbeStateStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>();
        private static readonly ulong ProbeUpdateStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>();
        private static readonly ulong RelocationClassificationStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>();
        private static readonly ulong AtlasTexelStride = 8;
        private const uint ProbeStateFreshFlag = 1u << 0;
        private const uint ProbeStateScrollExposedFlag = 1u << 1;
        private const uint ProbeStateInactiveFlag = 1u << 2;
        private const uint ProbeUpdateMaintenanceFlag = 1u << 12;
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
        // Latency buckets 0..14 represent exact frame counts; bucket 15 is
        // saturated (15+). This keeps dirty-response telemetry bounded and
        // allocation-free during long-running streaming/soak sessions.
        private const int DirtyLatencyBucketCount = 16;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly List<RetiredBufferResource> _retiredBuffers = new();
        private readonly List<VolumeCandidate> _volumeCandidates = new(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount + 3);
        private readonly GPUSimpleDdgiVolume[] _volumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiVolume[] _previousVolumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiProbeUpdate[] _updateQueueScratch = new GPUSimpleDdgiProbeUpdate[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly GPUSimpleDdgiProbeState[] _probeStateScratch = new GPUSimpleDdgiProbeState[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        // Scheduler scratch is retained for the renderer lifetime. Allocating five
        // small arrays every frame showed up as avoidable managed churn in long
        // travel/soak runs, particularly while authored-volume layouts change.
        private readonly int[] _volumeQuotaScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaUsageScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaMinimumScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaMaximumScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaCapacityScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly int[] _volumeQuotaWeightScratch = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private byte[] _probeFresh = Array.Empty<byte>();
        private byte[] _probeInactive = Array.Empty<byte>();
        private byte[] _probeQueued = Array.Empty<byte>();
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
        // 0 = no outstanding regional-dirty latency sample, 1 = awaiting the
        // first completed blend, 2 = awaiting stable readback convergence.
        private byte[] _probeDirtyLatencyStates = Array.Empty<byte>();
        private uint[] _probeDirtyLatencyStartFrames = Array.Empty<uint>();
        private readonly uint[] _dirtyFirstUpdateLatencyBuckets = new uint[DirtyLatencyBucketCount];
        private readonly uint[] _dirtyConvergenceLatencyBuckets = new uint[DirtyLatencyBucketCount];
        private uint _dirtyFirstUpdateLatencySampleCount;
        private uint _dirtyConvergenceLatencySampleCount;
        private uint _dirtyFirstUpdateLatencyMaxFrames;
        private uint _dirtyConvergenceLatencyMaxFrames;
        private readonly Dictionary<int, int> _volumeRoundRobinCursors = new();
        private int _previousVolumeCount;
        private readonly Vector3[] _ringOrigins = new Vector3[3];
        private readonly bool[] _ringHasOrigins = new bool[3];

        private BufferHandle _paramsBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BufferHandle _rayResultScratchBuffer;
        private BufferHandle _probeStateBuffer;
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _relocationClassificationBuffer;
        private BufferHandle _copyTempBuffer;
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
        private ulong _visibilityAtlasBytes;
        private ulong _rayScratchBytes;
        private ulong _probeStateBytes;
        private ulong _probeUpdateQueueBytes;
        private ulong _relocationClassificationBytes;
        private ulong _copyTempBytes;
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
        private uint _activeDirtyReasonFlags;
        private uint _regionalDirtyReasonFlags;
        private ulong _scheduledPrimaryRayCount;
        private ulong _adaptiveRaySavedPrimaryRayCount;
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
        private uint _updateTransactionSerial;
        private ulong _frameSerial;
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
            _visibilityAtlasBytes +
            _rayScratchBytes +
            _probeStateBytes +
            _probeUpdateQueueBytes +
            _relocationClassificationBytes +
            _copyTempBytes +
            _probeStateReadbackBufferBytes;
        public ulong IrradianceAtlasBytes => _irradianceAtlasBytes;
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
        public ulong CopyTempBytes => _copyTempBytes;
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
        public ulong AdaptiveRaySavedPrimaryRayCount => _adaptiveRaySavedPrimaryRayCount;
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
        public int DirtyFirstUpdateLatencyP50Frames => CalculateLatencyPercentile(_dirtyFirstUpdateLatencyBuckets, _dirtyFirstUpdateLatencySampleCount, 0.50f);
        public int DirtyFirstUpdateLatencyP95Frames => CalculateLatencyPercentile(_dirtyFirstUpdateLatencyBuckets, _dirtyFirstUpdateLatencySampleCount, 0.95f);
        public int DirtyConvergenceLatencyP50Frames => CalculateLatencyPercentile(_dirtyConvergenceLatencyBuckets, _dirtyConvergenceLatencySampleCount, 0.50f);
        public int DirtyConvergenceLatencyP95Frames => CalculateLatencyPercentile(_dirtyConvergenceLatencyBuckets, _dirtyConvergenceLatencySampleCount, 0.95f);
        public int DirtyFirstUpdateLatencyMaxFrames => ClampUIntToInt(_dirtyFirstUpdateLatencyMaxFrames);
        public int DirtyConvergenceLatencyMaxFrames => ClampUIntToInt(_dirtyConvergenceLatencyMaxFrames);
        public int ProbeRelocationCount => _probeStateReadbackValid != 0 ? _probeRelocationCount : 0;
        public int ClassifiedInactiveProbeCountEstimate => _probeStateReadbackValid != 0 ? _classifiedInactiveProbeCountEstimate : 0;
        public float AverageRelocationFractionEstimate => _probeStateReadbackValid != 0 ? _averageRelocationFractionEstimate : 0.0f;
        public int ProbeStateReadbackValid => _probeStateReadbackValid;
        public GPUSimpleDdgiParams LastParams => _lastParams;
        public ReadOnlySpan<GPUSimpleDdgiVolume> LastVolumes => new(_volumeScratch, 0, _volumeCount);
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
        public bool CanExecuteBlendTransaction => _updateTransactionPending && _traceTransactionExecuted && _relocateClassifyTransactionExecuted;
        // Async planning asks all pass predicates before any command has been
        // recorded.  These predicates deliberately describe a transaction that is
        // safe to schedule, while CanExecute* above remains the stricter guard at
        // recording time.  This keeps the three passes together in a split queue
        // plan without permitting a consumer to read an older scratch result.
        public bool CanScheduleRelocateClassifyTransaction => _updateTransactionPending && !_relocateClassifyTransactionExecuted;
        public bool CanScheduleBlendTransaction => _updateTransactionPending;
        public uint UpdateTransactionSerial => _updateTransactionSerial;

        public IReadOnlyList<DdgiVolumeDiagnosticsEntry> GetVolumeDiagnostics()
        {
            if (_volumeCount <= 0)
                return Array.Empty<DdgiVolumeDiagnosticsEntry>();

            var entries = new DdgiVolumeDiagnosticsEntry[_volumeCount];
            int activeProbeBudget = Math.Max(1, GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
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
                int cascadeIndex = Kind(volume) == VolumeKindRing ? Math.Max(0, i) : 0;
                float volumeCubicMeters = Math.Max(size.X * size.Y * size.Z, 0.0001f);

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
                    DesignPreset = kind == DdgiProbeVolumeKind.Authored ? "simple-authored" : "simple-ring",
                    BudgetWarning = !string.IsNullOrEmpty(_lastBudgetWarning)
                        ? _lastBudgetWarning
                        : probeCount > activeProbeBudget / 4 ? "simple-volume-uses-large-fraction-of-probe-budget" : string.Empty
                };
            }

            return entries;
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
                _volumeCount = 0;
                _probeCount = 0;
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
                Array.Clear(_probeDirtyLatencyStates);
                Array.Clear(_probeDirtyLatencyStartFrames);
                _lastParams = CreateDisabledParams(gi);
                UploadParams(stagingRing, commandBuffer);
                _frameIndex++;
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
            UpdateLightingDirtyState(gi, lightingSignature, dirtyReasonFlags, suppressSignatureBoost: hasRegionalDirtyWork && !requiresGlobalInvalidation);

            // The dispatch allocation uses the largest selected ring profile;
            // each queue item packs its own active ray count so mid/far rings do
            // not perform near-ring work.
            _raysPerProbe = ResolveMaximumRingFullRays(gi);
            if (_recenteredThisFrame)
            {
                _totalRecenterCount++;
                _framesSinceLastRecenter = 0;
            }

            int baseUpdateBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(_probeCount, gi.SimpleDdgiProbeUpdatesPerFrame);
            int updateBudget = ResolveLightingDirtyUpdateBudget(gi, baseUpdateBudget);
            EnsureCapacity(_probeCount, _raysPerProbe, updateBudget, commandBuffer);
            MarkFreshForNewOrScrolledProbes();
            if (hasRegionalDirtyWork)
                MarkRegionalDirtyProbes(dirtyRegions!);
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
            PreserveScrolledAtlasData(commandBuffer);
            ClearAtlasBuffersIfRequired(commandBuffer);
            SynchronizeSampledAtlasIfRequired(commandBuffer);

            float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f;
            // Fresh probes already force zero history in the blend shader. Do not
            // discard history for every other probe just because an atlas update
            // introduced a smaller set of fresh slots.
            float hysteresis = gi.SimpleDdgiHysteresis;
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
                HysteresisFrameAndFlags = new Vector4(hysteresis, _frameIndex, BuildFlags(gi, enabled, structuredGatherAvailable), gi.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = new Vector4(0.0f, 0.0f, 0.0f, environmentIntensity),
                // W is the forward-only environment fallback scale.  Valid probe
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
                    SampledAtlasActive ? 1.0f : 0.0f)
            };

            UploadParams(stagingRing, commandBuffer);
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

        public void MarkBlendExecuted()
        {
            if (!CanExecuteBlendTransaction)
                return;

            if (_probesToUpdate > 0)
            {
                uint completedFrame = unchecked(_frameIndex - 1u);
                for (int i = 0; i < _probesToUpdate; i++)
                {
                    int probeIndex = checked((int)_updateQueueScratch[i].ProbeIndex);
                    if ((uint)probeIndex < (uint)_probeFresh.Length)
                    {
                        _probeFresh[probeIndex] = 0;
                        _probeAges[probeIndex] = 0;
                        RecordDirtyFirstCompletedUpdate(probeIndex, completedFrame);
                    }
                }

                _atlasFresh = false;
            }

            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
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

        public void AbortUpdateTransaction()
        {
            _updateTransactionPending = false;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
        }

        private void BeginUpdateTransaction(bool hasWork)
        {
            _updateTransactionSerial++;
            if (_updateTransactionSerial == 0)
                _updateTransactionSerial = 1;
            _updateTransactionPending = hasWork;
            _traceTransactionExecuted = false;
            _relocateClassifyTransactionExecuted = false;
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
            _adaptiveRaySavedPrimaryRayCount = 0;
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
            byte[] previousStableUpdateCounts = _probeStableUpdateCounts;
            float[] previousLuminanceChangeEma = _probeLuminanceChangeEma;
            uint[] previousAges = _probeAges;
            uint[] previousGenerations = _probeGenerations;
            Vector3[] previousRelocations = _probeRelocations;
            float[] previousActiveWeights = _probeActiveWeights;
            uint[] previousClassifications = _probeClassifications;
            byte[] previousDirtyLatencyStates = _probeDirtyLatencyStates;
            uint[] previousDirtyLatencyStartFrames = _probeDirtyLatencyStartFrames;
            _probeFresh = new byte[Math.Max(0, probeCount)];
            _probeInactive = new byte[Math.Max(0, probeCount)];
            _probeQueued = new byte[Math.Max(0, probeCount)];
            _probeGenerations = new uint[Math.Max(0, probeCount)];
            _probeInvalidationMarkers = new uint[Math.Max(0, probeCount)];
            _probeRelocations = new Vector3[Math.Max(0, probeCount)];
            _probeActiveWeights = new float[Math.Max(0, probeCount)];
            _probeClassifications = new uint[Math.Max(0, probeCount)];
            _probeStableUpdateCounts = new byte[Math.Max(0, probeCount)];
            _probeLuminanceChangeEma = new float[Math.Max(0, probeCount)];
            _probeAges = new uint[Math.Max(0, probeCount)];
            _probeDirtyLatencyStates = new byte[Math.Max(0, probeCount)];
            _probeDirtyLatencyStartFrames = new uint[Math.Max(0, probeCount)];
            int copyCount = Math.Min(probeCount, previousFresh.Length);
            Array.Copy(previousFresh, _probeFresh, copyCount);
            Array.Copy(previousInactive, _probeInactive, copyCount);
            Array.Copy(previousStableUpdateCounts, _probeStableUpdateCounts, Math.Min(copyCount, previousStableUpdateCounts.Length));
            Array.Copy(previousLuminanceChangeEma, _probeLuminanceChangeEma, Math.Min(copyCount, previousLuminanceChangeEma.Length));
            Array.Copy(previousAges, _probeAges, copyCount);
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
            _probeStateUploadRequired = true;
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
            for (int i = 0; i < _probeStateReadbackRecorded.Length; i++)
                _probeStateReadbackRecorded[i] = false;
            Array.Clear(_probeDirtyLatencyStates);
            Array.Clear(_probeDirtyLatencyStartFrames);
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
                _activeDirtyReasonFlags = 0u;
                _hasLightingSignature = true;
                return;
            }

            if (lightingSignature != _lastLightingSignature)
            {
                _lastLightingSignature = lightingSignature;
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
                _regionalDirtyReasonFlags |= ToSimpleDirtyReasonFlag(dirty.Reason);

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
                            forceGenerationAdvance: true);
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

        private void MarkFreshForNewOrScrolledProbes()
        {
            if (_probeCount <= 0)
                return;

            if (!_settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled && _recenteredThisFrame)
            {
                // The compatibility switch intentionally does not retain the old
                // atlas-only copy behavior: relocation, classification, generation,
                // EMA, and age would then describe different logical probes than
                // their copied texels.  A bounded clear/rebootstrap is slower than
                // toroidal scrolling but is always coherent and therefore safe for
                // A/B captures or an emergency rollback.
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
                return 0;

            for (int i = 0; i < _probeCount; i++)
                _probeAges[i] = _probeAges[i] == uint.MaxValue ? uint.MaxValue : _probeAges[i] + 1u;
            Array.Clear(_probeQueued, 0, _probeCount);

            int count = 0;
            int[] quotas = ResolveVolumeQuotas(capacity);
            int[] used = _volumeQuotaUsageScratch;
            Array.Clear(used, 0, _volumeCount);

            // First fill each bounded ring allocation with exposed/dirty probes.
            // This preserves one-frame response while reserving maintenance work
            // for rings that do not happen to be moving this frame.
            for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
                QueueFreshVolumeProbes(ref count, capacity, volumeIndex, quotas[volumeIndex], ref used[volumeIndex]);

            for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
                QueueRoundRobinVolumeProbes(ref count, capacity, volumeIndex, quotas[volumeIndex], ref used[volumeIndex]);

            if (count < capacity)
                FillUnusedUpdateBudget(ref count, capacity, used);

            return count;
        }

        private void QueueFreshVolumeProbes(ref int count, int capacity, int volumeIndex, int quota, ref int used)
        {
            if (quota <= 0 || (uint)volumeIndex >= (uint)_volumeCount)
                return;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = FirstProbe(volume);
            int probeCount = VolumeProbeCount(volume);
            for (int local = 0; local < probeCount && used < quota && count < capacity; local++)
            {
                int probeIndex = firstProbe + local;
                if ((uint)probeIndex >= (uint)_probeCount || _probeQueued[probeIndex] != 0 || _probeFresh[probeIndex] == 0)
                    continue;
                AddProbeUpdate(ref count, capacity, probeIndex, ProbeStateFreshFlag);
                used++;
            }
        }

        private void QueueRoundRobinVolumeProbes(ref int count, int capacity, int volumeIndex, int quota, ref int used)
        {
            if (quota <= used || (uint)volumeIndex >= (uint)_volumeCount)
                return;

            GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = FirstProbe(volume);
            int probeCount = VolumeProbeCount(volume);
            int cursorKey = VolumeCursorKey(volume);
            _volumeRoundRobinCursors.TryGetValue(cursorKey, out int storedCursor);
            int cursor = Math.Clamp(storedCursor, 0, Math.Max(probeCount - 1, 0));
            int stride = ResolveProbeUpdateStride(probeCount);
            int visited = 0;
            while (visited < probeCount && used < quota && count < capacity)
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

                AddProbeUpdate(ref count, capacity, probeIndex, _probeInactive[probeIndex] != 0 ? ProbeStateInactiveFlag : 0u);
                used++;
            }

            _volumeRoundRobinCursors[cursorKey] = probeCount > 0
                ? (int)((cursor + (long)visited * stride) % probeCount)
                : 0;
        }

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

        private void FillUnusedUpdateBudget(ref int count, int capacity, int[] used)
        {
            bool madeProgress;
            do
            {
                madeProgress = false;
                for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
                {
                    int probeCount = VolumeProbeCount(_volumeScratch[volumeIndex]);
                    if (used[volumeIndex] >= probeCount)
                        continue;

                    int before = count;
                    QueueRoundRobinVolumeProbes(ref count, capacity, volumeIndex, probeCount, ref used[volumeIndex]);
                    madeProgress |= count > before;
                }
            }
            while (madeProgress && count < capacity);
        }

        private bool ShouldSkipInactiveProbe(int probeIndex)
        {
            return _settings.GlobalIllumination.SimpleDdgiClassificationSchedulingEnabled &&
                (uint)probeIndex < (uint)_probeInactive.Length &&
                _probeInactive[probeIndex] != 0 &&
                // Re-probe promptly: classification is asynchronous and a long
                // frozen interval can leave a moved or newly-lit probe stale.
                _probeAges[probeIndex] < InactiveProbeRetryFrames;
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

            _probeDirtyLatencyStates[probeIndex] = 1;
            _probeDirtyLatencyStartFrames[probeIndex] = startFrame;
        }

        private void ClearProbeDirtyLatency(int probeIndex)
        {
            if ((uint)probeIndex < (uint)_probeDirtyLatencyStates.Length)
                _probeDirtyLatencyStates[probeIndex] = 0;
            if ((uint)probeIndex < (uint)_probeDirtyLatencyStartFrames.Length)
                _probeDirtyLatencyStartFrames[probeIndex] = 0;
        }

        private void RecordDirtyFirstCompletedUpdate(int probeIndex, uint completedFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                _probeDirtyLatencyStates[probeIndex] != 1 ||
                (uint)probeIndex >= (uint)_probeDirtyLatencyStartFrames.Length)
            {
                return;
            }

            uint elapsedFrames = unchecked(completedFrame - _probeDirtyLatencyStartFrames[probeIndex]);
            RecordLatencySample(
                _dirtyFirstUpdateLatencyBuckets,
                ref _dirtyFirstUpdateLatencySampleCount,
                ref _dirtyFirstUpdateLatencyMaxFrames,
                elapsedFrames);
            _probeDirtyLatencyStates[probeIndex] = 2;
        }

        private void RecordDirtyConvergenceIfStable(int probeIndex, uint observedFrame)
        {
            if ((uint)probeIndex >= (uint)_probeDirtyLatencyStates.Length ||
                _probeDirtyLatencyStates[probeIndex] != 2 ||
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
                ref _dirtyConvergenceLatencyMaxFrames,
                elapsedFrames);
            ClearProbeDirtyLatency(probeIndex);
        }

        private static void RecordLatencySample(
            uint[] buckets,
            ref uint sampleCount,
            ref uint maximumFrames,
            uint elapsedFrames)
        {
            int bucket = elapsedFrames >= DirtyLatencyBucketCount - 1
                ? DirtyLatencyBucketCount - 1
                : (int)elapsedFrames;
            if ((uint)bucket < (uint)buckets.Length && buckets[bucket] < uint.MaxValue)
                buckets[bucket]++;
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
                weights[i] = quality.RingIndex switch
                {
                    0 => 6,
                    1 => 3,
                    _ => 1
                };
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

        private void AddProbeUpdate(ref int count, int capacity, int probeIndex, uint flags)
        {
            if (count >= capacity || (uint)probeIndex >= (uint)_probeCount)
                return;

            int volumeIndex = Math.Max(0, ResolveVolumeIndexForProbe(probeIndex));
            _updateQueueScratch[count++] = new GPUSimpleDdgiProbeUpdate
            {
                ProbeIndex = checked((uint)probeIndex),
                VolumeIndex = checked((uint)volumeIndex),
                Flags = PackUpdateFlags(probeIndex, volumeIndex, flags | (_probeFresh[probeIndex] != 0 ? ProbeStateFreshFlag : 0u)),
                Reserved0 = PackProbeUpdateMetadata(_probeGenerations[probeIndex], _probeAges[probeIndex])
            };
            _probeQueued[probeIndex] = 1;
        }

        private uint PackUpdateFlags(int probeIndex, int volumeIndex, uint flags)
        {
            SimpleDdgiRingQuality quality = ResolveVolumeQuality(volumeIndex);
            int rayCount = ResolveUpdateRayCount(probeIndex, quality, flags);
            uint packedMaterialCascade = checked((uint)Math.Clamp(quality.MaterialTextureMaxCascade + 1, 0, 7));
            uint packedMaxLights = checked((uint)Math.Clamp(quality.MaxShadedLights, 0, 63));
            flags |= packedMaterialCascade << ProbeUpdateMaterialTextureCascadeShift;
            flags |= packedMaxLights << ProbeUpdateMaxShadedLightsShift;
            _scheduledPrimaryRayCount += (ulong)rayCount;
            _ringScheduledPrimaryRayCounts[quality.RingIndex] += (ulong)rayCount;
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
            if (!gi.SimpleDdgiAdaptiveRaysEnabled ||
                _lightingDirtyFrames > 0 ||
                (flags & ProbeStateFreshFlag) != 0 ||
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

        private void MarkProbeFresh(int probeIndex, bool scrollExposed, bool dirty = false, bool forceGenerationAdvance = false)
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
                ClearProbeDirtyLatency(probeIndex);
                if ((uint)probeIndex < (uint)_probeAges.Length)
                    _probeAges[probeIndex] = 0u;
                _probeStateUploadRequired = true;
            }
            _probeFresh[probeIndex] = 1;
            _probeInactive[probeIndex] = 0;
            if (dirty && shouldAdvanceGeneration)
                BeginProbeDirtyLatency(probeIndex, _frameIndex);
            if (scrollExposed && shouldAdvanceGeneration)
                _recenterRefreshProbeCount++;
            if (dirty && shouldAdvanceGeneration)
                _dirtyRefreshProbeCount++;
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

        internal static List<(int OldLocal, int NewLocal, int Count)> BuildScrollCopyRunsForTest(int countX, int countY, int countZ, int deltaX, int deltaY, int deltaZ)
        {
            var runs = new List<(int OldLocal, int NewLocal, int Count)>();
            if (countX <= 0 || countY <= 0 || countZ <= 0 ||
                Math.Abs(deltaX) >= countX || Math.Abs(deltaY) >= countY || Math.Abs(deltaZ) >= countZ)
            {
                return runs;
            }

            int runX = countX - Math.Abs(deltaX);
            int oldXStart = Math.Max(0, -deltaX);
            int newXStart = Math.Max(0, deltaX);
            for (int z = 0; z < countZ; z++)
            {
                int oldZ = z - deltaZ;
                if ((uint)oldZ >= (uint)countZ)
                    continue;

                for (int y = 0; y < countY; y++)
                {
                    int oldY = y - deltaY;
                    if ((uint)oldY >= (uint)countY)
                        continue;

                    int oldLocal = oldXStart + oldY * countX + oldZ * countX * countY;
                    int newLocal = newXStart + y * countX + z * countX * countY;
                    runs.Add((oldLocal, newLocal, runX));
                }
            }

            return runs;
        }

        private void BuildVolumeTable(GlobalIlluminationSettings gi, BoundingBox sceneBounds, Vector3 cameraPosition)
        {
            _volumeCandidates.Clear();
            Array.Clear(_volumeScratch);
            bool anyAuthored = false;
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
                int spacing = left.Spacing.CompareTo(right.Spacing);
                if (spacing != 0)
                    return spacing;
                int kind = left.KindPriority.CompareTo(right.KindPriority);
                return kind != 0 ? kind : left.SourceOrdinal.CompareTo(right.SourceOrdinal);
            });

            int droppedVolumeCount = EnforceProbeBudget(_volumeCandidates, GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            _lastBudgetWarning = droppedVolumeCount > 0
                ? $"simple-ddgi-probe-budget-dropped-{droppedVolumeCount}-volume{(droppedVolumeCount == 1 ? string.Empty : "s")}"
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

            int countX = Math.Clamp((int)MathF.Ceiling(size.X / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
            int countY = Math.Clamp((int)MathF.Ceiling(size.Y / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
            int countZ = Math.Clamp((int)MathF.Ceiling(size.Z / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
            Vector3 origin = SnapVector(min, spacing);
            Vector3 latticeSize = LatticeSize(countX, countY, countZ, spacing);
            candidate = new VolumeCandidate(
                VolumeKindAuthored,
                ordinal,
                origin,
                spacing,
                countX,
                countY,
                countZ,
                origin,
                origin + latticeSize,
                Math.Max(spacing * 1.5f, 0.001f));
            return true;
        }

        private VolumeCandidate CreateRingVolume(GlobalIlluminationSettings gi, BoundingBox sceneBounds, Vector3 cameraPosition, int ringIndex)
        {
            float spacing = gi.SimpleDdgiRingBaseSpacing * MathF.Pow(gi.SimpleDdgiRingSpacingMultiplier, ringIndex);
            int countX = gi.SimpleDdgiRingGridSizeX;
            int countY = gi.SimpleDdgiRingGridSizeY;
            int countZ = gi.SimpleDdgiRingGridSizeZ;
            Vector3 latticeSize = LatticeSize(countX, countY, countZ, spacing);
            bool hadRingOrigin = _ringHasOrigins[ringIndex];
            Vector3 origin = ResolveSceneClampedOrigin(
                sceneBounds.Min,
                sceneBounds.Max,
                latticeSize,
                spacing,
                cameraPosition,
                _ringOrigins[ringIndex],
                ref _ringHasOrigins[ringIndex],
                out bool recentered);
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
            return new VolumeCandidate(
                VolumeKindRing,
                10_000 + ringIndex,
                origin,
                spacing,
                countX,
                countY,
                countZ,
                origin,
                origin + latticeSize,
                Math.Max(spacing * 1.5f, 0.001f));
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
            return new VolumeCandidate(
                VolumeKindLegacy,
                0,
                _gridOrigin,
                spacing,
                countX,
                countY,
                countZ,
                _gridOrigin,
                _gridOrigin + latticeSize,
                Math.Max(spacing * 1.5f, 0.001f));
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
            if (_probeCount <= 0 || !_probeStateBuffer.IsValid || !_probeStateUploadRequired)
                return;

            for (int i = 0; i < _probeCount; i++)
            {
                uint flags = PackProbeStateFlags(0u, _probeGenerations[i]);
                if (_probeFresh[i] != 0)
                    flags |= ProbeStateFreshFlag;
                if (_probeInactive[i] != 0)
                    flags |= ProbeStateInactiveFlag;

                Vector3 relocation = (uint)i < (uint)_probeRelocations.Length
                    ? _probeRelocations[i]
                    : Vector3.Zero;
                float activeWeight = (uint)i < (uint)_probeActiveWeights.Length
                    ? _probeActiveWeights[i]
                    : 1.0f;
                uint classification = (uint)i < (uint)_probeClassifications.Length
                    ? _probeClassifications[i]
                    : 0u;
                if (_probeInactive[i] != 0)
                {
                    activeWeight = 0.0f;
                    classification = 1u;
                }

                _probeStateScratch[i] = new GPUSimpleDdgiProbeState
                {
                    RelocationAndActive = new Vector4(relocation.X, relocation.Y, relocation.Z, Math.Clamp(activeWeight, 0.0f, 1.0f)),
                    Flags = flags,
                    Age = _probeAges[i],
                    Classification = classification,
                    Reserved0 = BitConverter.SingleToUInt32Bits((uint)i < (uint)_probeLuminanceChangeEma.Length ? _probeLuminanceChangeEma[i] : 0.0f)
                };
            }

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _probeStateBuffer,
                new ReadOnlySpan<GPUSimpleDdgiProbeState>(_probeStateScratch, 0, _probeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
            _probeStateUploadRequired = false;
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

        private void EnsureTransferBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return;

            if (handle.IsValid)
                RetireBufferResource(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
        }

        private unsafe void PreserveScrolledAtlasData(CommandBuffer commandBuffer)
        {
            if (!_recenteredThisFrame || _atlasClearRequired || _atlasFresh)
                return;

            if (_settings.GlobalIllumination.SimpleDdgiToroidalScrollingEnabled)
            {
                // Logical coordinates now resolve through the per-volume physical
                // offset.  Irradiance, visibility, relocation, classification,
                // generation, EMA, and age all remain in the same physical slot,
                // so copying only the two atlases would be both expensive and
                // incoherent.  Keep the legacy copier behind the feature flag for
                // capture A/B comparisons.
                bool preservedAny = false;
                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                {
                    GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                    if (!TryGetPreviousMatchingVolume(volumeIndex, current, out GPUSimpleDdgiVolume previous) ||
                        !TryResolveCellDelta(previous, current, out int deltaX, out int deltaY, out int deltaZ) ||
                        (deltaX == 0 && deltaY == 0 && deltaZ == 0))
                    {
                        continue;
                    }

                    preservedAny = Math.Abs((long)deltaX) < CountX(current) &&
                        Math.Abs((long)deltaY) < CountY(current) &&
                        Math.Abs((long)deltaZ) < CountZ(current);
                    if (preservedAny)
                        break;
                }

                if (preservedAny)
                {
                    _atlasPreservedOnRecenterThisFrame = true;
                    _totalAtlasPreserveOnRecenterCount++;
                }
                return;
            }

            bool copiedAny = false;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUSimpleDdgiVolume current = _volumeScratch[volumeIndex];
                if (!TryGetPreviousMatchingVolume(volumeIndex, current, out GPUSimpleDdgiVolume previous) ||
                    !TryResolveCellDelta(previous, current, out int deltaX, out int deltaY, out int deltaZ) ||
                    (deltaX == 0 && deltaY == 0 && deltaZ == 0))
                {
                    continue;
                }

                copiedAny |= CopyScrolledAtlasRuns(commandBuffer, _irradianceAtlasBuffer, _irradianceAtlasBytes, (ulong)IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride, current, deltaX, deltaY, deltaZ);
                copiedAny |= CopyScrolledAtlasRuns(commandBuffer, _visibilityAtlasBuffer, _visibilityAtlasBytes, (ulong)VisibilityTexelsPerProbe * VisibilityTexelsPerProbe * AtlasTexelStride, current, deltaX, deltaY, deltaZ);
            }

            if (copiedAny)
            {
                _scrollCopyCount++;
                _atlasPreservedOnRecenterThisFrame = true;
                _totalAtlasPreserveOnRecenterCount++;
                _sampledAtlas?.MarkFullSyncRequired();
            }
        }

        private unsafe bool CopyScrolledAtlasRuns(
            CommandBuffer commandBuffer,
            BufferHandle atlasHandle,
            ulong atlasBytes,
            ulong bytesPerProbe,
            GPUSimpleDdgiVolume volume,
            int deltaX,
            int deltaY,
            int deltaZ)
        {
            if (!atlasHandle.IsValid || atlasBytes == 0 || bytesPerProbe == 0)
                return false;

            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            List<(int OldLocal, int NewLocal, int Count)> runs = BuildScrollCopyRunsForTest(countX, countY, countZ, deltaX, deltaY, deltaZ);
            if (runs.Count == 0)
                return false;

            ulong volumeSliceBytes = checked((ulong)VolumeProbeCount(volume) * bytesPerProbe);
            EnsureTransferBuffer(ref _copyTempBuffer, ref _copyTempBytes, Math.Max(MinBufferSize, volumeSliceBytes), "Simple DDGI Scroll Copy Temp");
            if (!_copyTempBuffer.IsValid || volumeSliceBytes == 0)
                return false;

            Silk.NET.Vulkan.Buffer atlas = _bufferManager.GetBuffer(atlasHandle);
            Silk.NET.Vulkan.Buffer temp = _bufferManager.GetBuffer(_copyTempBuffer);
            int firstProbe = FirstProbe(volume);
            ulong volumeSrcOffset = checked((ulong)firstProbe * bytesPerProbe);
            if (volumeSrcOffset + volumeSliceBytes > atlasBytes)
                return false;

            BufferCopy fullCopy = new() { SrcOffset = volumeSrcOffset, DstOffset = 0, Size = volumeSliceBytes };
            _context.Api.CmdCopyBuffer(commandBuffer, atlas, temp, 1, &fullCopy);
            InsertTransferBarrier(commandBuffer, temp, volumeSliceBytes, AccessFlags2.TransferWriteBit, AccessFlags2.TransferReadBit);

            bool copiedAny = false;
            foreach ((int oldLocal, int newLocal, int runCount) in runs)
            {
                ulong srcOffset = checked((ulong)oldLocal * bytesPerProbe);
                ulong dstOffset = checked((ulong)(firstProbe + newLocal) * bytesPerProbe);
                ulong size = checked((ulong)runCount * bytesPerProbe);
                if (srcOffset + size > volumeSliceBytes || dstOffset + size > atlasBytes)
                    continue;

                BufferCopy copy = new() { SrcOffset = srcOffset, DstOffset = dstOffset, Size = size };
                _context.Api.CmdCopyBuffer(commandBuffer, temp, atlas, 1, &copy);
                copiedAny = true;
            }

            if (copiedAny)
                InsertTransferBarrier(commandBuffer, atlas, atlasBytes, AccessFlags2.TransferWriteBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            return copiedAny;
        }

        private unsafe void InsertTransferBarrier(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer buffer, ulong size, AccessFlags2 srcAccess, AccessFlags2 dstAccess)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = srcAccess,
                DstStageMask = dstAccess == AccessFlags2.TransferReadBit ? PipelineStageFlags2.TransferBit : PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = dstAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = 0,
                Size = size
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private unsafe void ClearAtlasBuffersIfRequired(CommandBuffer commandBuffer)
        {
            if (!_atlasClearRequired)
                return;

            _atlasClearedThisFrame = true;
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            uint barrierCount = 0;
            FillBufferAndAddBarrier(_irradianceAtlasBuffer, _irradianceAtlasBytes, barriers, ref barrierCount, commandBuffer);
            FillBufferAndAddBarrier(_visibilityAtlasBuffer, _visibilityAtlasBytes, barriers, ref barrierCount, commandBuffer);
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
            out bool recentered)
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
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, latticeSize.X, spacing, cameraPosition.X, currentOrigin.X),
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.Y, sceneMax.Y, latticeSize.Y, spacing, cameraPosition.Y, currentOrigin.Y),
                ResolveAlignedSceneClampedAxisOrigin(sceneMin.Z, sceneMax.Z, latticeSize.Z, spacing, cameraPosition.Z, currentOrigin.Z));
            recentered = !ApproximatelyEqual(currentOrigin, alignedOrigin);
            return recentered ? alignedOrigin : currentOrigin;
        }

        private static float ResolveAlignedSceneClampedAxisOrigin(
            float sceneMin,
            float sceneMax,
            float latticeExtent,
            float spacing,
            float cameraPosition,
            float currentOrigin)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            bool latticeCoversScene = sceneExtent <= latticeExtent;
            float oppositeBoundaryOrigin = sceneMax - latticeExtent;
            float allowedOriginMin = Math.Min(sceneMin, oppositeBoundaryOrigin);
            float allowedOriginMax = Math.Max(sceneMin, oppositeBoundaryOrigin);
            bool originInsideAllowedInterval = currentOrigin >= allowedOriginMin - 0.0001f &&
                currentOrigin <= allowedOriginMax + 0.0001f;
            float quarterExtent = latticeExtent * 0.25f;
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

        private static uint PackProbeStateFlags(uint flags, uint generation) =>
            (flags & ~((uint)ProbeStateGenerationValueMask << ProbeStateGenerationShift)) |
            (NormalizeProbeGeneration(generation) << ProbeStateGenerationShift);

        private static uint ReadProbeStateGeneration(uint flags) =>
            NormalizeProbeGeneration((flags >> ProbeStateGenerationShift) & ProbeStateGenerationValueMask);

        private static uint Kind(GPUSimpleDdgiVolume volume) =>
            (uint)Math.Max(0, (int)MathF.Round(volume.WorldMaxAndKind.W));

        private static int SourceOrdinal(GPUSimpleDdgiVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.RaysAndReserved.X));

        private static int VolumeCursorKey(GPUSimpleDdgiVolume volume) =>
            HashCode.Combine((int)Kind(volume), SourceOrdinal(volume));

        private static int CountInactiveProbes(byte[] inactive, int probeCount)
        {
            int count = 0;
            int length = Math.Min(Math.Max(probeCount, 0), inactive.Length);
            for (int i = 0; i < length; i++)
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
                HysteresisFrameAndFlags = new Vector4(0.0f, _frameIndex, 0.0f, settings.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = Vector4.Zero,
                ProbeUpdateRange = Vector4.Zero,
                DebugAndBias = new Vector4((float)settings.DebugView, settings.DdgiSelfShadowBiasScale, settings.IndirectIntensity, settings.FarFieldMaxTraceSteps),
                RotationQuaternion = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                BiasAndPadding = new Vector4(settings.SimpleDdgiNormalBias, settings.SimpleDdgiViewBias, settings.SimpleDdgiHysteresisChangeThreshold, settings.SimpleDdgiHysteresisStepThreshold),
                Reserved0 = Vector4.Zero
            };
        }

        private uint BuildFlags(GlobalIlluminationSettings settings, bool enabled, bool structuredGatherAvailable)
        {
            uint flags = enabled ? 1u : 0u;
            if (settings.FarFieldClipmapEnabled)
                flags |= 1u << 1;
            if (settings.FarFieldForceAll)
                flags |= 1u << 2;
            if (settings.SimpleDdgiFogEnabled)
                flags |= 1u << 3;
            if (settings.SimpleDdgiParticlesEnabled)
                flags |= 1u << 4;
            if (settings.SimpleDdgiAdaptiveHysteresisEnabled)
                flags |= 1u << 5;
            if (_lightingDirtyFrames > 0)
                flags |= 1u << 10;
            if (settings.FarFieldClipmapEnabled && settings.FarFieldSkyVisibilityEnabled)
                flags |= 1u << 6;
            if (settings.FarFieldClipmapEnabled && settings.FarFieldSunShadowEnabled)
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

                if (inactive ||
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
            if (_copyTempBuffer.IsValid)
                _bufferManager.DestroyBuffer(_copyTempBuffer);
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
