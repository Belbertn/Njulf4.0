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
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class DdgiProbeVolumeManager : IDisposable
    {
        public const int AbsoluteMaxVolumeCount = 16;
        public const int AbsoluteMaxProbeCount = 65_536;

        private static readonly ulong VolumeMetadataBufferSize =
            GlobalIlluminationProbeVolumeData.HeaderSize +
            GlobalIlluminationProbeVolumeData.VolumeStride * AbsoluteMaxVolumeCount;
        private static readonly ulong MinProbeStateBufferSize = GlobalIlluminationProbeVolumeData.ProbeStateStride;
        private static readonly ulong SchedulerConstantsBufferSize = (ulong)Marshal.SizeOf<GPUDdgiSchedulerConstants>();
        private static readonly ulong SchedulerCounterBufferSize = (ulong)Marshal.SizeOf<GPUDdgiSchedulerCounters>();
        private static readonly ulong TraceIndirectDispatchBufferSize = (ulong)Marshal.SizeOf<GPUDdgiTraceIndirectDispatch>();
        private static readonly ulong SchedulerQueueReadbackBufferSize =
            GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride * AbsoluteMaxProbeCount;
        private const ulong MinResourceBufferSize = 16;
        private const uint ResourceSignatureLayoutVersion = 2;
        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private const int SchedulerWorkgroupSize = 64;
        private const int SchedulerReadbackRingSize = 2;
        private const int GpuScheduleOverBudgetReductionFrameThreshold = 3;
        private const float GpuScheduleOverBudgetRequestBudgetScale = 0.75f;
        private const float WarmupCompletionTarget = 0.80f;
        private const int WarmupMaxAgeFrames = 60;
        private const int MinimumVisibleNearFieldUpdates = 256;
        private const int MinimumLocalVolumeUpdates = 128;
        private const int MinimumNewCellUpdates = 128;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly GPUDdgiProbeVolume[] _volumeScratch = new GPUDdgiProbeVolume[AbsoluteMaxVolumeCount];
        private readonly GPUDdgiProbeUpdateRequest[] _probeUpdateRequestScratch = new GPUDdgiProbeUpdateRequest[AbsoluteMaxProbeCount];
        private readonly byte[] _probeUpdateRequestMarks = new byte[AbsoluteMaxProbeCount];
        private readonly DdgiProbeSchedulerFeedback[] _probeSchedulerFeedback = new DdgiProbeSchedulerFeedback[AbsoluteMaxProbeCount];
        private readonly DdgiProbeUpdateScheduler.DdgiProbeUpdateSchedulerScratch _schedulerScratch = new();
        private readonly DdgiCpuSchedulerInstrumentation _schedulerInstrumentation = new();
        private GPUDdgiDirtyRegion[] _gpuDirtyRegionScratch = Array.Empty<GPUDdgiDirtyRegion>();
        private readonly PerformanceSampleWindow _schedulerTimingWindow = new(120);
        private readonly PerformanceSampleWindow _gpuScheduleTimingWindow = new(120);
        private readonly bool[] _schedulerCounterReadbackRecorded = new bool[SchedulerReadbackRingSize];
        private readonly bool[] _schedulerQueueReadbackRecorded = new bool[SchedulerReadbackRingSize];
        private readonly int[] _schedulerCounterReadbackRequestBudgets = new int[SchedulerReadbackRingSize];
        private readonly int[] _schedulerCounterReadbackPrimaryRayBudgets = new int[SchedulerReadbackRingSize];
        private readonly int[] _schedulerCounterReadbackQueueCapacities = new int[SchedulerReadbackRingSize];
        private readonly ValidationExpectedFrame[] _schedulerValidationExpectedFrames =
        [
            ValidationExpectedFrame.Invalid,
            ValidationExpectedFrame.Invalid
        ];
        private readonly GPUDdgiProbeUpdateRequest[][] _schedulerValidationExpectedRequests =
        [
            new GPUDdgiProbeUpdateRequest[AbsoluteMaxProbeCount],
            new GPUDdgiProbeUpdateRequest[AbsoluteMaxProbeCount]
        ];
        private readonly byte[] _schedulerValidationSeenProbes = new byte[AbsoluteMaxProbeCount];
        private readonly int[] _lastScheduledProbeUpdatesByVolume = new int[AbsoluteMaxVolumeCount];
        private readonly ulong[] _lastScheduledPrimaryRaysByVolume = new ulong[AbsoluteMaxVolumeCount];
        private readonly ulong[] _lastLocalSlotSignatures = new ulong[AbsoluteMaxVolumeCount];
        private readonly int[] _lastLocalSlotGenerations = new int[AbsoluteMaxVolumeCount];
        private readonly ulong[] _currentLocalSlotSignatures = new ulong[AbsoluteMaxVolumeCount];
        private readonly int[] _currentLocalSlotGenerations = new int[AbsoluteMaxVolumeCount];
        private readonly int[] _currentLocalSlotProbeCapacities = new int[AbsoluteMaxVolumeCount];
        private readonly List<RetiredBufferResource> _retiredBuffers = new();

        private BufferHandle _volumeMetadataBuffer;
        private BufferHandle _probeStateBuffer;
        private BufferHandle _probeUpdateQueueBuffer;
        private BufferHandle _probeRelocationClassificationBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BufferHandle _rayResultScratchBuffer;
        private BufferHandle _schedulerConstantsBuffer;
        private BufferHandle _dirtyRegionBuffer;
        private BufferHandle _probeCandidateBuffer;
        private BufferHandle _schedulerGroupCountBuffer;
        private BufferHandle _schedulerPrefixBuffer;
        private BufferHandle _schedulerCounterBuffer;
        private BufferHandle _traceIndirectDispatchBuffer;
        private readonly BufferHandle[] _schedulerCounterReadbackBuffers = new BufferHandle[SchedulerReadbackRingSize];
        private readonly BufferHandle[] _schedulerQueueReadbackBuffers = new BufferHandle[SchedulerReadbackRingSize];
        private BindlessHeap? _registeredBindlessHeap;
        private ulong _probeStateBufferSize;
        private ulong _probeUpdateQueueBufferSize;
        private ulong _probeRelocationClassificationBufferSize;
        private ulong _irradianceAtlasBufferSize;
        private ulong _visibilityAtlasBufferSize;
        private ulong _rayResultScratchBufferSize;
        private ulong _schedulerConstantsBufferSize;
        private ulong _dirtyRegionBufferSize;
        private ulong _probeCandidateBufferSize;
        private ulong _schedulerGroupCountBufferSize;
        private ulong _schedulerPrefixBufferSize;
        private ulong _schedulerCounterBufferSize;
        private ulong _schedulerCounterReadbackBufferSize;
        private ulong _schedulerQueueReadbackBufferSize;
        private ulong _traceIndirectDispatchBufferSize;
        private int _dirtyRegionCapacity;
        private int _probeCandidateCapacity;
        private int _schedulerGroupCountCapacity;
        private int _schedulerPrefixCapacity;
        private int _lastGpuSchedulerDirtyRegionCount;
        private int _lastGpuSchedulerDirtyRegionOverflowCount;
        private int _lastGpuSchedulerResourceReinitializationCount;
        private int _totalGpuSchedulerResourceReinitializationCount;
        private ulong _lastGpuSchedulerUploadBytes;
        private GPUDdgiSchedulerCounters _lastCompletedGpuSchedulerCounters;
        private int _lastCompletedGpuSchedulerCountersValid;
        private int _lastCompletedGpuSchedulerReadbackLatencyFrames;
        private int _lastCompletedGpuSchedulerRequestBudget;
        private int _lastCompletedGpuSchedulerPrimaryRayBudget;
        private int _lastCompletedGpuSchedulerQueueCapacity;
        private DdgiGpuSchedulerValidationSnapshot _lastCompletedGpuSchedulerValidation = DdgiGpuSchedulerValidationSnapshot.Invalid;
        private int _volumeCount;
        private int _probeCount;
        private int _activeProbeCount;
        private int _raysPerProbe;
        private int _maxProbeUpdatesPerFrame;
        private int _lastProbeUpdateRequestBudget;
        private int _lastProbeUpdatePrimaryRayBudget;
        private float _adaptiveBudgetScale = 1.0f;
        private int _lastAdaptiveBudgetReduced;
        private int _lastEmergencyDegradeActive;
        private int _lastEffectiveMaxShadedLights;
        private string _lastAdaptiveBudgetReason = "none";
        private int _updateCursor;
        private int _scheduledUpdateStartProbeIndex;
        private int _scheduledProbeUpdateCount;
        private int _lastNewProbeUpdateCount;
        private int _lastFrustumProbeUpdateCount;
        private int _lastOutsideFrustumProbeUpdateCount;
        private int _lastDirtyBoundsProbeUpdateCount;
        private int _lastAgeRefreshProbeUpdateCount;
        private int _lastHighVarianceProbeUpdateCount;
        private int _lastLowConfidenceProbeUpdateCount;
        private int _lastStableProbeUpdateCount;
        private float _lastAverageProbeVariability;
        private float _lastAverageProbeConfidence;
        private DdgiRuntimeWarmupState _warmupState = DdgiRuntimeWarmupState.Disabled;
        private float _lastWarmedVisibleProbeFraction;
        private float _lastWarmedLocalProbeFraction;
        private float _lastWarmedCascade0ProbeFraction;
        private bool _lastUploadRequestedWarmupRecovery;
        private ulong _lastScheduledPrimaryRayCount;
        private ulong _lastRayScratchBytes;
        private ulong _lastUpdatedAtlasBytes;
        private int _lastPublishedCacheLatencyFrames;
        private uint _publishedCacheGeneration;
        private ulong _publishedCacheLastUpdatedFrameSerial;
        private int _lastResourceReinitializationCount;
        private int _totalResourceReinitializationCount;
        private ulong _textureBytes;
        private long _lastUploadMicroseconds;
        private long _lastSchedulerMicroseconds;
        private long _schedulerP95Microseconds;
        private int _schedulerTimingSampleCount;
        private long _gpuScheduleP95Microseconds;
        private int _gpuScheduleTimingSampleCount;
        private int _gpuScheduleOverBudget;
        private int _gpuScheduleConsecutiveOverBudgetFrames;
        private long _lastGpuUpdateMicroseconds;
        private bool _lastGpuUpdateEstimated;
        private ulong _lastResourceSignature;
        private ulong _lastAuthoredLayoutSignature;
        private ulong _lastLocalAllocationSignature;
        private bool _hasResourceSignature;
        private bool _hasAuthoredLayoutSignature;
        private bool _hasLocalAllocationSignature;
        private bool _wasDdgiEnabled;
        private bool _disposed;
        private int _visibilityInitializationStartProbe;
        private int _visibilityInitializationProbeCount;
        private ulong _frameSerial;
        private int _lastActiveLocalSlotCount;
        private int _lastLocalSlotGeneration;
        private ulong _lastLocalSlotInitBytes;
        private string _lastLocalVolumeEvictionReason = "none";
        private string _lastCacheClearReason = "none";

        public DdgiProbeVolumeManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _volumeMetadataBuffer = _bufferManager.CreateDeviceBuffer(
                VolumeMetadataBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Probe Volume Metadata Buffer");
            EnsureProbeStateCapacity(0);
            EnsureProbeUpdateQueueCapacity(0);
            EnsureProbeRelocationClassificationCapacity(0);
            EnsureAtlasCapacity(
                ref _irradianceAtlasBuffer,
                ref _irradianceAtlasBufferSize,
                0,
                BindlessIndex.DdgiIrradianceAtlasBuffer,
                "DDGI Irradiance Atlas Buffer");
            EnsureAtlasCapacity(
                ref _visibilityAtlasBuffer,
                ref _visibilityAtlasBufferSize,
                0,
                BindlessIndex.DdgiVisibilityAtlasBuffer,
                "DDGI Visibility Atlas Buffer");
            EnsureRayResultScratchCapacity(0, 0);
            _schedulerConstantsBuffer = CreateSchedulerDeviceBuffer(
                SchedulerConstantsBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                "DDGI Scheduler Constants Buffer");
            _schedulerConstantsBufferSize = SchedulerConstantsBufferSize;
            _schedulerCounterBuffer = CreateSchedulerDeviceBuffer(
                SchedulerCounterBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                "DDGI Scheduler Counter Buffer");
            _schedulerCounterBufferSize = SchedulerCounterBufferSize;
            _traceIndirectDispatchBuffer = CreateSchedulerDeviceBuffer(
                TraceIndirectDispatchBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndirectBufferBit,
                "DDGI Trace Indirect Dispatch Buffer");
            _traceIndirectDispatchBufferSize = Math.Max(MinResourceBufferSize, TraceIndirectDispatchBufferSize);
            for (int i = 0; i < _schedulerCounterReadbackBuffers.Length; i++)
                _schedulerCounterReadbackBuffers[i] = CreateSchedulerReadbackBuffer(i);
            _schedulerCounterReadbackBufferSize = checked(SchedulerCounterBufferSize * (ulong)_schedulerCounterReadbackBuffers.Length);
            _schedulerQueueReadbackBufferSize = 0UL;
            EnsureGpuSchedulerCapacity(
                0,
                settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions,
                settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount);
        }

        public int VolumeCount => _volumeCount;
        public int ProbeCount => _probeCount;
        public int ActiveProbeCount => _activeProbeCount;
        public int RaysPerProbe => _raysPerProbe;
        public int MaxProbeUpdatesPerFrame => _maxProbeUpdatesPerFrame;
        public int LastProbeUpdateRequestBudget => _lastProbeUpdateRequestBudget;
        public int LastProbeUpdatePrimaryRayBudget => _lastProbeUpdatePrimaryRayBudget;
        public float LastAdaptiveBudgetScale => _adaptiveBudgetScale;
        public int LastAdaptiveBudgetReduced => _lastAdaptiveBudgetReduced;
        public int LastEmergencyDegradeActive => _lastEmergencyDegradeActive;
        public int LastEffectiveMaxShadedLights => _lastEffectiveMaxShadedLights;
        public string LastAdaptiveBudgetReason => _lastAdaptiveBudgetReason;
        public int ScheduledUpdateStartProbeIndex => _scheduledUpdateStartProbeIndex;
        public int ScheduledProbeUpdateCount => _scheduledProbeUpdateCount;
        public int LastNewProbeUpdateCount => _lastNewProbeUpdateCount;
        public int LastFrustumProbeUpdateCount => _lastFrustumProbeUpdateCount;
        public int LastOutsideFrustumProbeUpdateCount => _lastOutsideFrustumProbeUpdateCount;
        public int LastDirtyBoundsProbeUpdateCount => _lastDirtyBoundsProbeUpdateCount;
        public int LastAgeRefreshProbeUpdateCount => _lastAgeRefreshProbeUpdateCount;
        public int LastHighVarianceProbeUpdateCount => _lastHighVarianceProbeUpdateCount;
        public int LastLowConfidenceProbeUpdateCount => _lastLowConfidenceProbeUpdateCount;
        public int LastStableProbeUpdateCount => _lastStableProbeUpdateCount;
        public float LastAverageProbeVariability => _lastAverageProbeVariability;
        public float LastAverageProbeConfidence => _lastAverageProbeConfidence;
        public DdgiRuntimeWarmupState WarmupState => _warmupState;
        public float LastWarmedVisibleProbeFraction => _lastWarmedVisibleProbeFraction;
        public float LastWarmedLocalProbeFraction => _lastWarmedLocalProbeFraction;
        public float LastWarmedCascade0ProbeFraction => _lastWarmedCascade0ProbeFraction;
        public ulong LastScheduledPrimaryRayCount => _lastScheduledPrimaryRayCount;
        public ulong LastRayScratchBytes => _lastRayScratchBytes;
        public ulong LastUpdatedAtlasBytes => _lastUpdatedAtlasBytes;
        public int LastPublishedCacheLatencyFrames => _lastPublishedCacheLatencyFrames;
        public uint PublishedCacheGeneration => _publishedCacheGeneration;
        public ulong PublishedCacheLastUpdatedFrameSerial => _publishedCacheLastUpdatedFrameSerial;
        public int LastResourceReinitializationCount => _lastResourceReinitializationCount;
        public int TotalResourceReinitializationCount => _totalResourceReinitializationCount;
        public int LastActiveLocalSlotCount => _lastActiveLocalSlotCount;
        public int LastLocalSlotGeneration => _lastLocalSlotGeneration;
        public ulong LastLocalSlotInitBytes => _lastLocalSlotInitBytes;
        public string LastLocalVolumeEvictionReason => _lastLocalVolumeEvictionReason;
        public string LastCacheClearReason => _lastCacheClearReason;
        public ulong TextureBytes => _textureBytes;
        public ulong ProbeVolumeBufferBytes => VolumeMetadataBufferSize;
        public ulong ProbeStateBufferBytes => _probeStateBufferSize;
        public ulong ProbeUpdateQueueBytes => _probeUpdateQueueBufferSize;
        public ulong ProbeRelocationClassificationBytes => _probeRelocationClassificationBufferSize;
        public ulong CurrentIrradianceAtlasBytes => _irradianceAtlasBufferSize;
        public ulong CurrentVisibilityAtlasBytes => _visibilityAtlasBufferSize;
        public ulong LocalSlotReservedPoolBytes => EstimateLocalSlotReservedPoolBytes();
        public ulong BufferBytes => VolumeMetadataBufferSize +
            _probeStateBufferSize +
            _probeUpdateQueueBufferSize +
            _probeRelocationClassificationBufferSize +
            _rayResultScratchBufferSize +
            GpuSchedulerBufferBytes;
        public ulong GpuSchedulerBufferBytes => _schedulerConstantsBufferSize +
            _dirtyRegionBufferSize +
            _probeCandidateBufferSize +
            _schedulerGroupCountBufferSize +
            _schedulerPrefixBufferSize +
            _schedulerCounterBufferSize +
            _schedulerCounterReadbackBufferSize +
            _schedulerQueueReadbackBufferSize +
            _traceIndirectDispatchBufferSize;
        public int GpuSchedulerDirtyRegionCapacity => _dirtyRegionCapacity;
        public int GpuSchedulerCandidateCapacity => _probeCandidateCapacity;
        public int GpuSchedulerGroupCountCapacity => _schedulerGroupCountCapacity;
        public int GpuSchedulerPrefixCapacity => _schedulerPrefixCapacity;
        public bool HasGpuSchedulerTraceIndirectDispatchBuffer =>
            _traceIndirectDispatchBuffer.IsValid &&
            _traceIndirectDispatchBufferSize >= TraceIndirectDispatchBufferSize;
        public int LastGpuSchedulerDirtyRegionCount => _lastGpuSchedulerDirtyRegionCount;
        public int LastGpuSchedulerDirtyRegionOverflowCount => _lastGpuSchedulerDirtyRegionOverflowCount;
        public int LastGpuSchedulerResourceReinitializationCount => _lastGpuSchedulerResourceReinitializationCount;
        public int TotalGpuSchedulerResourceReinitializationCount => _totalGpuSchedulerResourceReinitializationCount;
        public ulong LastGpuSchedulerUploadBytes => _lastGpuSchedulerUploadBytes;
        public int LastCompletedGpuSchedulerCountersValid => _lastCompletedGpuSchedulerCountersValid;
        public int LastCompletedGpuSchedulerReadbackLatencyFrames => _lastCompletedGpuSchedulerReadbackLatencyFrames;
        public int LastCompletedGpuSchedulerRequestBudget => _lastCompletedGpuSchedulerRequestBudget;
        public int LastCompletedGpuSchedulerPrimaryRayBudget => _lastCompletedGpuSchedulerPrimaryRayBudget;
        public int LastCompletedGpuSchedulerQueueCapacity => _lastCompletedGpuSchedulerQueueCapacity;
        public GPUDdgiSchedulerCounters LastCompletedGpuSchedulerCounters => _lastCompletedGpuSchedulerCounters;
        public DdgiGpuSchedulerValidationSnapshot LastCompletedGpuSchedulerValidation => _lastCompletedGpuSchedulerValidation;
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public long LastSchedulerMicroseconds => _lastSchedulerMicroseconds;
        public long SchedulerP95Microseconds => _schedulerP95Microseconds;
        public int SchedulerTimingSampleCount => _schedulerTimingSampleCount;
        public long GpuScheduleP95Microseconds => _gpuScheduleP95Microseconds;
        public int GpuScheduleTimingSampleCount => _gpuScheduleTimingSampleCount;
        public int GpuScheduleOverBudget => _gpuScheduleOverBudget;
        public long CpuSchedulerPhaseClipmapDirtyMicroseconds => _schedulerInstrumentation.PhaseClipmapDirtyMicroseconds;
        public long CpuSchedulerPhaseDirtyRegionsMicroseconds => _schedulerInstrumentation.PhaseDirtyRegionsMicroseconds;
        public long CpuSchedulerPhaseUninitializedMicroseconds => _schedulerInstrumentation.PhaseUninitializedMicroseconds;
        public long CpuSchedulerPhaseFrustumMicroseconds => _schedulerInstrumentation.PhaseFrustumMicroseconds;
        public long CpuSchedulerPhaseSafetyMicroseconds => _schedulerInstrumentation.PhaseSafetyMicroseconds;
        public long CpuSchedulerPhaseRoundRobinMicroseconds => _schedulerInstrumentation.PhaseRoundRobinMicroseconds;
        public int CpuSchedulerCandidateInsertCount => _schedulerInstrumentation.CandidateInsertCount;
        public int CpuSchedulerCandidateMaxShiftCount => _schedulerInstrumentation.CandidateMaxShiftCount;

        public void ReportCompletedGpuUpdateMicroseconds(long gpuUpdateMicroseconds)
        {
            ReportCompletedGpuUpdateMicroseconds(gpuUpdateMicroseconds, gpuUpdateMicroseconds > 0, 0, false);
        }

        public void ReportCompletedGpuUpdateMicroseconds(long gpuUpdateMicroseconds, bool gpuTimingAvailable)
        {
            ReportCompletedGpuUpdateMicroseconds(gpuUpdateMicroseconds, gpuTimingAvailable, 0, false);
        }

        public void ReportCompletedGpuUpdateMicroseconds(
            long gpuUpdateMicroseconds,
            bool gpuTimingAvailable,
            long gpuScheduleMicroseconds,
            bool gpuScheduleTimingAvailable)
        {
            if (gpuUpdateMicroseconds > 0)
            {
                _lastGpuUpdateMicroseconds = gpuUpdateMicroseconds;
                _lastGpuUpdateEstimated = false;
            }
            else if (gpuTimingAvailable)
            {
                _lastGpuUpdateMicroseconds = 0;
                _lastGpuUpdateEstimated = false;
            }
            else
            {
                long estimatedGpuUpdateMicroseconds = EstimateScheduledGpuUpdateMicroseconds();
                if (estimatedGpuUpdateMicroseconds > 0)
                {
                    _lastGpuUpdateMicroseconds = estimatedGpuUpdateMicroseconds;
                    _lastGpuUpdateEstimated = true;
                }
            }

            if (!gpuScheduleTimingAvailable)
                return;

            _gpuScheduleTimingWindow.Add(Math.Max(0, gpuScheduleMicroseconds));
            PerformanceSampleStats scheduleStats = _gpuScheduleTimingWindow.GetStats();
            _gpuScheduleP95Microseconds = (long)Math.Round(scheduleStats.P95);
            _gpuScheduleTimingSampleCount = scheduleStats.Count;
            long scheduleBudgetMicroseconds = (long)MathF.Round(
                _settings.GlobalIllumination.DdgiGpuScheduleTimeBudgetMilliseconds * 1000.0f);
            _gpuScheduleOverBudget = scheduleBudgetMicroseconds > 0 &&
                _gpuScheduleP95Microseconds > scheduleBudgetMicroseconds
                    ? 1
                    : 0;
            _gpuScheduleConsecutiveOverBudgetFrames = _gpuScheduleOverBudget != 0
                ? _gpuScheduleConsecutiveOverBudgetFrames + 1
                : 0;
        }

        internal const ulong RayResultStride = 80UL;

        public bool IsProbeScheduledForUpdate(int probeIndex)
        {
            return (uint)probeIndex < (uint)_activeProbeCount &&
                _probeUpdateRequestMarks[probeIndex] != 0;
        }

        public bool TryGetScheduledProbeUpdateFlags(int probeIndex, out uint flags, out uint priority)
        {
            if ((uint)probeIndex >= (uint)_activeProbeCount || _scheduledProbeUpdateCount <= 0)
            {
                flags = 0u;
                priority = 0u;
                return false;
            }

            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                if (request.ProbeIndex != (uint)probeIndex)
                    continue;

                flags = request.Flags;
                priority = request.Priority;
                return true;
            }

            flags = 0u;
            priority = 0u;
            return false;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiProbeVolumeBuffer,
                _bufferManager.GetBuffer(_volumeMetadataBuffer),
                0,
                VolumeMetadataBufferSize);
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiProbeStateBuffer,
                _bufferManager.GetBuffer(_probeStateBuffer),
                0,
                _probeStateBufferSize);
            RegisterIfValid(BindlessIndex.DdgiProbeUpdateQueueBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBufferSize);
            RegisterIfValid(
                BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                _probeRelocationClassificationBuffer,
                _probeRelocationClassificationBufferSize);
            RegisterIfValid(BindlessIndex.DdgiIrradianceAtlasBuffer, _irradianceAtlasBuffer, _irradianceAtlasBufferSize);
            RegisterIfValid(BindlessIndex.DdgiVisibilityAtlasBuffer, _visibilityAtlasBuffer, _visibilityAtlasBufferSize);
            RegisterIfValid(BindlessIndex.DdgiRayResultScratchBuffer, _rayResultScratchBuffer, _rayResultScratchBufferSize);
            RegisterGpuSchedulerBuffers();
        }

        public void Upload(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            Upload(authoredVolumes, null, 0, 0UL, 0, 0, "none", stagingRing, commandBuffer);
        }

        public void Upload(
            DdgiFrameLayout layout,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            Upload(
                layout.Volumes,
                layout.VolumeMetadata,
                layout.TotalPhysicalProbeCount,
                layout.LocalAllocationSignature,
                layout.ActiveLocalSlotCount,
                layout.LocalSlotGeneration,
                layout.LocalVolumeEvictionReason,
                stagingRing,
                commandBuffer);
        }

        private void Upload(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata>? runtimeMetadata,
            int reservedPhysicalProbeCount,
            ulong localAllocationSignature,
            int activeLocalSlotCount,
            int localSlotGeneration,
            string localVolumeEvictionReason,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (authoredVolumes == null)
                throw new ArgumentNullException(nameof(authoredVolumes));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI probe upload.", nameof(commandBuffer));

            BeginFrameResourceRetirement();
            long uploadStart = Stopwatch.GetTimestamp();
            _lastResourceReinitializationCount = 0;
            _lastGpuSchedulerResourceReinitializationCount = 0;
            _lastUploadRequestedWarmupRecovery = false;
            _lastLocalSlotInitBytes = 0UL;
            _lastCacheClearReason = "none";
            _lastActiveLocalSlotCount = Math.Max(0, activeLocalSlotCount);
            _lastLocalSlotGeneration = Math.Max(0, localSlotGeneration);
            _lastLocalVolumeEvictionReason = string.IsNullOrWhiteSpace(localVolumeEvictionReason)
                ? "none"
                : localVolumeEvictionReason;
            int previousActiveProbeCount = _activeProbeCount;
            _volumeCount = GlobalIlluminationProbeVolumeData.BuildVolumes(
                authoredVolumes,
                _settings.GlobalIllumination,
                _volumeScratch.AsSpan(0, AbsoluteMaxVolumeCount),
                out _probeCount,
                out _activeProbeCount,
                out _raysPerProbe,
                out _maxProbeUpdatesPerFrame,
                runtimeMetadata,
                reservedPhysicalProbeCount);

            if (_probeCount > AbsoluteMaxProbeCount)
            {
                _probeCount = AbsoluteMaxProbeCount;
                _activeProbeCount = Math.Min(_activeProbeCount, AbsoluteMaxProbeCount);
            }

            bool resourcesRecreated = EnsureProbeStateCapacity(_probeCount);
            ulong atlasBytes = GlobalIlluminationProbeVolumeData.EstimateTextureBytes(_activeProbeCount);
            _textureBytes = checked(atlasBytes * 2UL);
            resourcesRecreated |= EnsureProbeUpdateQueueCapacity(_probeCount);
            resourcesRecreated |= EnsureProbeRelocationClassificationCapacity(_probeCount);
            resourcesRecreated |= EnsureAtlasCapacity(ref _irradianceAtlasBuffer, ref _irradianceAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateIrradianceAtlasBytes(_activeProbeCount), BindlessIndex.DdgiIrradianceAtlasBuffer, "DDGI Irradiance Atlas Buffer");
            resourcesRecreated |= EnsureAtlasCapacity(ref _visibilityAtlasBuffer, ref _visibilityAtlasBufferSize, GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount), BindlessIndex.DdgiVisibilityAtlasBuffer, "DDGI Visibility Atlas Buffer");
            EnsureGpuSchedulerCapacity(
                _activeProbeCount,
                _settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions,
                _settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount);

            bool ddgiEnabled = _settings.GlobalIllumination.EffectiveUseDdgi && _activeProbeCount > 0;
            ulong resourceSignature = CreateCacheCompatibilitySignature(
                _volumeScratch.AsSpan(0, _volumeCount),
                CreateProbeUpdateModeSignature(_settings.GlobalIllumination));
            bool hadResourceSignature = _hasResourceSignature;
            bool resourceSignatureChanged = hadResourceSignature && resourceSignature != _lastResourceSignature;
            int authoredFirstProbeIndex = FindFirstProbeIndex(_volumeScratch.AsSpan(0, _volumeCount), DdgiProbeVolumeKind.Authored);
            int authoredProbeCount = CalculateProbeCountForKind(_volumeScratch.AsSpan(0, _volumeCount), DdgiProbeVolumeKind.Authored);
            ulong authoredLayoutSignature = CreateAuthoredLayoutSignature(_volumeScratch.AsSpan(0, _volumeCount));
            int localSlotRangeCount = BuildCurrentLocalSlotSignatures(
                runtimeMetadata,
                _volumeScratch.AsSpan(0, _volumeCount));
            bool localAllocationChanged = localAllocationSignature != 0UL &&
                (!_hasLocalAllocationSignature || localAllocationSignature != _lastLocalAllocationSignature);
            bool shouldInitializeResources = ddgiEnabled &&
                (resourcesRecreated ||
                 !_wasDdgiEnabled ||
                 !_hasResourceSignature ||
                 resourceSignature != _lastResourceSignature ||
                 localAllocationChanged);

            if (shouldInitializeResources)
            {
                InitializePersistentResources(stagingRing, commandBuffer, resourceSignature);
                CommitCurrentLocalSlotSignatures();
                _lastLocalAllocationSignature = localAllocationSignature;
                _hasLocalAllocationSignature = localAllocationSignature != 0UL;
                _lastCacheClearReason = ResolveCacheClearReason(
                    resourcesRecreated,
                    _wasDdgiEnabled,
                    hadResourceSignature,
                    resourceSignatureChanged,
                    localAllocationChanged);
                _lastUploadRequestedWarmupRecovery = _wasDdgiEnabled &&
                    (resourcesRecreated ||
                     !hadResourceSignature ||
                     resourceSignatureChanged ||
                     localAllocationChanged);
                _lastResourceReinitializationCount = 1;
                _totalResourceReinitializationCount++;
                _updateCursor = 0;
            }
            else if (ddgiEnabled)
            {
                bool authoredLayoutChanged = !_hasAuthoredLayoutSignature ||
                    authoredLayoutSignature != _lastAuthoredLayoutSignature;
                if (localSlotRangeCount > 0)
                {
                    InitializeChangedLocalSlotRanges(stagingRing, commandBuffer);
                    CommitCurrentLocalSlotSignatures();
                    _lastLocalAllocationSignature = localAllocationSignature;
                    _hasLocalAllocationSignature = localAllocationSignature != 0UL;
                }
                else if (authoredLayoutChanged && authoredFirstProbeIndex >= 0 && authoredProbeCount > 0)
                {
                    InitializeProbeRange(
                        stagingRing,
                        commandBuffer,
                        authoredFirstProbeIndex,
                        authoredProbeCount);
                }
                else if (_activeProbeCount > previousActiveProbeCount)
                {
                    InitializeProbeRange(
                        stagingRing,
                        commandBuffer,
                        previousActiveProbeCount,
                        _activeProbeCount - previousActiveProbeCount);
                }
            }

            GPUDdgiProbeVolumeHeader header = GlobalIlluminationProbeVolumeData.BuildHeader(
                _volumeCount,
                _probeCount,
                _activeProbeCount,
                _raysPerProbe,
                _maxProbeUpdatesPerFrame,
                _settings.GlobalIllumination,
                BindlessIndex.DdgiProbeStateBuffer,
                _publishedCacheGeneration,
                unchecked((uint)_publishedCacheLastUpdatedFrameSerial),
                _warmupState);

            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _volumeMetadataBuffer,
                header,
                _volumeScratch.AsSpan(0, _volumeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _wasDdgiEnabled = ddgiEnabled;
            if (!ddgiEnabled)
            {
                _hasResourceSignature = false;
                _hasAuthoredLayoutSignature = false;
                _hasLocalAllocationSignature = false;
                Array.Clear(_lastLocalSlotSignatures, 0, _lastLocalSlotSignatures.Length);
                Array.Clear(_lastLocalSlotGenerations, 0, _lastLocalSlotGenerations.Length);
            }
            else
            {
                _lastAuthoredLayoutSignature = authoredLayoutSignature;
                _hasAuthoredLayoutSignature = true;
            }
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
        }

        public int ScheduleProbeUpdates(bool enabled) => ScheduleProbeUpdates(enabled, (IReadOnlyList<BoundingBox>?)null);

        public int ScheduleProbeUpdates(bool enabled, IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            return ScheduleProbeUpdates(enabled, null, dirtyBounds);
        }

        public int ScheduleProbeUpdates(bool enabled, DdgiFrameLayout layout)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            return ScheduleProbeUpdates(enabled, layout, layout.DirtyBounds);
        }

        public int ScheduleProbeUpdates(bool enabled, DdgiFrameLayout layout, ulong frameSerial)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            int scheduled = ScheduleProbeUpdates(enabled, layout, layout.DirtyBounds);
            if (enabled && scheduled > 0)
                MarkScheduledClipmapCellsUpdated(layout, frameSerial);
            return scheduled;
        }

        private int ScheduleProbeUpdates(
            bool enabled,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            if (!enabled || _activeProbeCount <= 0 || _maxProbeUpdatesPerFrame <= 0)
            {
                _scheduledUpdateStartProbeIndex = 0;
                _scheduledProbeUpdateCount = 0;
                _lastProbeUpdateRequestBudget = 0;
                _lastProbeUpdatePrimaryRayBudget = 0;
                ResetAdaptiveBudgetDiagnostics();
                ResetScheduleDiagnostics();
                ResetSchedulerTimingDiagnostics();
                ResetGpuScheduleTimingDiagnostics();
                _schedulerInstrumentation.Reset();
                ResetPublishedCacheDiagnostics(resetCacheIdentity: true);
                ResetWarmupDiagnostics();
                _lastGpuUpdateMicroseconds = 0;
                _lastGpuUpdateEstimated = false;
                return 0;
            }

            if (_updateCursor >= _activeProbeCount)
                _updateCursor = 0;

            UpdateWarmupStateAndDiagnostics(layout, ShouldEnterWarmupRecovery(layout));

            DdgiAdaptiveBudgetSelection budgetSelection = DdgiProbeUpdateScheduler.CalculateAdaptiveBudgets(
                _maxProbeUpdatesPerFrame,
                _activeProbeCount,
                _settings.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame,
                _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget,
                _settings.GlobalIllumination.DdgiColdStartPrimaryRayBudget,
                _settings.GlobalIllumination.DdgiMinimumProbeRefreshFrames,
                _settings.GlobalIllumination.DdgiMaxShadedLights,
                _settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled,
                _settings.GlobalIllumination.EffectiveDdgiProbeUpdateTimeBudgetMilliseconds,
                _settings.GlobalIllumination.DdgiAdaptiveBudgetHysteresisFraction,
                _settings.GlobalIllumination.DdgiEmergencyDegradeGpuTimeMultiplier,
                _lastGpuUpdateMicroseconds,
                _adaptiveBudgetScale,
                _lastGpuUpdateEstimated);
            int requestBudget = ApplyNonStarvingRequestBudgetFloor(budgetSelection.RequestBudget, layout);
            int primaryRayBudget = ApplyNonStarvingPrimaryRayBudgetFloor(budgetSelection.PrimaryRayBudget, requestBudget);
            _adaptiveBudgetScale = budgetSelection.BudgetScale;
            _lastAdaptiveBudgetReduced = budgetSelection.BudgetReduced ? 1 : 0;
            _lastEmergencyDegradeActive = budgetSelection.EmergencyDegradeActive ? 1 : 0;
            _lastEffectiveMaxShadedLights = budgetSelection.EffectiveMaxShadedLights;
            _lastAdaptiveBudgetReason = budgetSelection.Reason;
            _lastProbeUpdateRequestBudget = requestBudget;
            _lastProbeUpdatePrimaryRayBudget = primaryRayBudget;
            long schedulerStart = Stopwatch.GetTimestamp();
            AgeProbeSchedulerFeedback(_activeProbeCount);
            _schedulerInstrumentation.Reset();
            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                _volumeScratch.AsSpan(0, _volumeCount),
                layout,
                dirtyBounds,
                _activeProbeCount,
                requestBudget,
                primaryRayBudget,
                _updateCursor,
                _settings.GlobalIllumination,
                _probeUpdateRequestScratch.AsSpan(0, Math.Min(requestBudget, _probeUpdateRequestScratch.Length)),
                _probeUpdateRequestMarks,
                _schedulerScratch,
                _probeSchedulerFeedback.AsSpan(0, _activeProbeCount),
                new DdgiWarmupSchedulingContext(_warmupState, WarmupMaxAgeFrames),
                _schedulerInstrumentation);
            _lastSchedulerMicroseconds = ElapsedMicroseconds(schedulerStart);
            _schedulerTimingWindow.Add(_lastSchedulerMicroseconds);
            PerformanceSampleStats schedulerStats = _schedulerTimingWindow.GetStats();
            _schedulerP95Microseconds = (long)Math.Round(schedulerStats.P95);
            _schedulerTimingSampleCount = schedulerStats.Count;

            _scheduledProbeUpdateCount = result.RequestCount;
            _scheduledUpdateStartProbeIndex = result.CompatibilityStartProbeIndex;
            _updateCursor = result.NextUpdateCursor;
            UpdateProbeSchedulerFeedbackFromScheduledRequests();
            BuildScheduleDiagnostics();
            ResetPublishedCacheTransientDiagnostics();
            return _scheduledProbeUpdateCount;
        }

        private void ResetAdaptiveBudgetDiagnostics()
        {
            _adaptiveBudgetScale = 1.0f;
            _lastAdaptiveBudgetReduced = 0;
            _lastEmergencyDegradeActive = 0;
            _lastEffectiveMaxShadedLights = 0;
            _lastAdaptiveBudgetReason = "none";
        }

        private void ResetScheduleDiagnostics()
        {
            _lastNewProbeUpdateCount = 0;
            _lastFrustumProbeUpdateCount = 0;
            _lastOutsideFrustumProbeUpdateCount = 0;
            _lastDirtyBoundsProbeUpdateCount = 0;
            _lastAgeRefreshProbeUpdateCount = 0;
            _lastHighVarianceProbeUpdateCount = 0;
            _lastLowConfidenceProbeUpdateCount = 0;
            _lastStableProbeUpdateCount = 0;
            _lastAverageProbeVariability = 0.0f;
            _lastAverageProbeConfidence = 0.0f;
            _lastScheduledPrimaryRayCount = 0;
            Array.Clear(_lastScheduledProbeUpdatesByVolume, 0, _lastScheduledProbeUpdatesByVolume.Length);
            Array.Clear(_lastScheduledPrimaryRaysByVolume, 0, _lastScheduledPrimaryRaysByVolume.Length);
        }

        private void ResetSchedulerTimingDiagnostics()
        {
            _lastSchedulerMicroseconds = 0;
            _schedulerP95Microseconds = 0;
            _schedulerTimingSampleCount = 0;
        }

        private void ResetGpuScheduleTimingDiagnostics()
        {
            _gpuScheduleP95Microseconds = 0;
            _gpuScheduleTimingSampleCount = 0;
            _gpuScheduleOverBudget = 0;
            _gpuScheduleConsecutiveOverBudgetFrames = 0;
        }

        private void ResetPublishedCacheDiagnostics(bool resetCacheIdentity)
        {
            ResetPublishedCacheTransientDiagnostics();
            if (resetCacheIdentity)
            {
                _publishedCacheGeneration = 0;
                _publishedCacheLastUpdatedFrameSerial = 0;
            }
        }

        private void ResetPublishedCacheTransientDiagnostics()
        {
            _lastRayScratchBytes = 0;
            _lastUpdatedAtlasBytes = 0;
            _lastPublishedCacheLatencyFrames = _publishedCacheGeneration > 0 ? 1 : 0;
        }

        private void ResetWarmupDiagnostics()
        {
            _warmupState = DdgiRuntimeWarmupState.Disabled;
            _lastWarmedVisibleProbeFraction = 0.0f;
            _lastWarmedLocalProbeFraction = 0.0f;
            _lastWarmedCascade0ProbeFraction = 0.0f;
            _lastUploadRequestedWarmupRecovery = false;
        }

        private bool ShouldEnterWarmupRecovery(DdgiFrameLayout? layout)
        {
            if (_lastUploadRequestedWarmupRecovery)
                return true;
            if (layout == null)
                return false;
            if (layout.ViewPriorityHistoryReset)
                return true;

            if (layout.MovementClass is
                DdgiCameraMovementClass.LayoutChanged or
                DdgiCameraMovementClass.Teleport or
                DdgiCameraMovementClass.ViewResetOnly)
            {
                return true;
            }

            for (int i = 0; i < layout.DirtyProbeRequests.Count; i++)
            {
                DdgiClipmapDirtyReason reason = layout.DirtyProbeRequests[i].Reason;
                if (reason is DdgiClipmapDirtyReason.Teleport or DdgiClipmapDirtyReason.LayoutChange)
                    return true;
            }

            return false;
        }

        private int ApplyNonStarvingRequestBudgetFloor(int requestBudget, DdgiFrameLayout? layout)
        {
            int hardMax = Math.Min(_maxProbeUpdatesPerFrame, _activeProbeCount);
            if (hardMax <= 0)
                return 0;

            int protectedMinimum = 0;
            bool viewPriorityActive = layout?.ViewPriority.Enabled == true;
            if (viewPriorityActive)
                protectedMinimum = Math.Max(protectedMinimum, Math.Min(MinimumVisibleNearFieldUpdates, hardMax));

            bool warmupActive = _warmupState is
                DdgiRuntimeWarmupState.ColdStart or
                DdgiRuntimeWarmupState.LocalVolumeWarmup or
                DdgiRuntimeWarmupState.NearCascadeWarmup or
                DdgiRuntimeWarmupState.Recovery;
            if ((viewPriorityActive || warmupActive) && HasAuthoredLocalProbes())
                protectedMinimum = Math.Max(protectedMinimum, Math.Min(MinimumLocalVolumeUpdates, hardMax));

            if (warmupActive || HasNewCellWork(layout))
                protectedMinimum = Math.Max(protectedMinimum, Math.Min(MinimumNewCellUpdates, hardMax));

            return Math.Clamp(Math.Max(requestBudget, protectedMinimum), 0, hardMax);
        }

        private int ApplyNonStarvingPrimaryRayBudgetFloor(int primaryRayBudget, int requestBudget)
        {
            if (requestBudget <= 0)
                return Math.Max(0, primaryRayBudget);

            long minimumRays = (long)requestBudget * Math.Max(1, _raysPerProbe);
            return (int)Math.Clamp(
                Math.Max((long)primaryRayBudget, minimumRays),
                0L,
                GlobalIlluminationSettings.MaxDdgiProbeUpdatePrimaryRayBudget);
        }

        private void UpdateWarmupStateAndDiagnostics(DdgiFrameLayout? layout, bool recoveryRequested)
        {
            if (_activeProbeCount <= 0)
            {
                ResetWarmupDiagnostics();
                return;
            }

            bool useGpuWarmupCounters =
                _settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.Gpu &&
                _lastCompletedGpuSchedulerCountersValid != 0;
            DdgiWarmupFractions fractions = useGpuWarmupCounters
                ? new DdgiWarmupFractions(_lastWarmedVisibleProbeFraction, _lastWarmedLocalProbeFraction, _lastWarmedCascade0ProbeFraction)
                : CalculateWarmupFractions(layout);
            if (!useGpuWarmupCounters)
            {
                _lastWarmedVisibleProbeFraction = fractions.Visible;
                _lastWarmedLocalProbeFraction = fractions.Local;
                _lastWarmedCascade0ProbeFraction = fractions.Cascade0;
            }

            if (_warmupState == DdgiRuntimeWarmupState.Disabled ||
                _warmupState == DdgiRuntimeWarmupState.ColdStart)
            {
                _warmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup;
            }

            if (recoveryRequested)
                _warmupState = DdgiRuntimeWarmupState.Recovery;

            if (_warmupState == DdgiRuntimeWarmupState.Recovery)
            {
                if (fractions.Local >= WarmupCompletionTarget && fractions.Cascade0 >= WarmupCompletionTarget)
                    _warmupState = DdgiRuntimeWarmupState.SteadyState;
                return;
            }

            if (_warmupState == DdgiRuntimeWarmupState.LocalVolumeWarmup &&
                fractions.Local >= WarmupCompletionTarget)
            {
                _warmupState = DdgiRuntimeWarmupState.NearCascadeWarmup;
            }

            if (_warmupState == DdgiRuntimeWarmupState.NearCascadeWarmup &&
                fractions.Cascade0 >= WarmupCompletionTarget)
            {
                _warmupState = DdgiRuntimeWarmupState.SteadyState;
            }
        }

        private DdgiWarmupFractions CalculateWarmupFractions(DdgiFrameLayout? layout)
        {
            DdgiViewPriorityContext viewPriority = layout?.ViewPriority ?? default;
            if (!viewPriority.Enabled || _volumeCount <= 0 || _activeProbeCount <= 0)
            {
                return new DdgiWarmupFractions(
                    Visible: 0.0f,
                    Local: HasAuthoredLocalProbes() ? 0.0f : 1.0f,
                    Cascade0: HasCascade0Probes() ? 0.0f : 1.0f);
            }

            int visibleCount = 0;
            int warmedVisibleCount = 0;
            int localCount = 0;
            int warmedLocalCount = 0;
            int cascade0Count = 0;
            int warmedCascade0Count = 0;
            for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = _volumeScratch[volumeIndex];
                bool cameraRelative = IsCameraRelative(volume);
                bool cascade0 = cameraRelative && CascadeIndex(volume) == 0;
                bool localAuthored = !cameraRelative;
                if (!cascade0 && !localAuthored)
                    continue;

                int firstProbe = FirstProbeIndex(volume);
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                Vector3 origin = Origin(volume);
                Vector3 spacing = Spacing(volume);
                DdgiClipmapCell gridMin = GridMinCell(volume);
                DdgiClipmapCell ringOffset = RingOffsetCell(volume);
                int probeCount = checked(countX * countY * countZ);
                int endProbe = Math.Min(_activeProbeCount, firstProbe + probeCount);
                for (int probeIndex = firstProbe; probeIndex < endProbe; probeIndex++)
                {
                    int local = probeIndex - firstProbe;
                    int wrappedX = local % countX;
                    int wrappedY = (local / countX) % countY;
                    int wrappedZ = local / (countX * countY);
                    Vector3 probePosition;
                    if (cameraRelative)
                    {
                        DdgiClipmapCell logicalCell = DdgiClipmapAddressing.DecodeLogicalCellFromPhysicalProbeIndex(
                            probeIndex,
                            gridMin,
                            ringOffset,
                            countX,
                            countY,
                            countZ,
                            firstProbe);
                        probePosition = new Vector3(logicalCell.X * spacing.X, logicalCell.Y * spacing.Y, logicalCell.Z * spacing.Z);
                    }
                    else
                    {
                        probePosition = origin + new Vector3(wrappedX * spacing.X, wrappedY * spacing.Y, wrappedZ * spacing.Z);
                    }

                    if (!ProbeInWarmupView(probePosition, viewPriority))
                        continue;

                    bool warmed = probeIndex < _probeSchedulerFeedback.Length &&
                        _probeSchedulerFeedback[probeIndex].IsWarmed(WarmupMaxAgeFrames);
                    visibleCount++;
                    if (warmed)
                        warmedVisibleCount++;

                    if (localAuthored)
                    {
                        localCount++;
                        if (warmed)
                            warmedLocalCount++;
                    }
                    else if (cascade0)
                    {
                        cascade0Count++;
                        if (warmed)
                            warmedCascade0Count++;
                    }
                }
            }

            return new DdgiWarmupFractions(
                Visible: Fraction(warmedVisibleCount, visibleCount, emptyValue: 0.0f),
                Local: Fraction(warmedLocalCount, localCount, emptyValue: 1.0f),
                Cascade0: Fraction(warmedCascade0Count, cascade0Count, emptyValue: 1.0f));
        }

        private bool HasAuthoredLocalProbes()
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                if (!IsCameraRelative(_volumeScratch[i]))
                    return true;
            }

            return false;
        }

        private bool HasCascade0Probes()
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUDdgiProbeVolume volume = _volumeScratch[i];
                if (IsCameraRelative(volume) && CascadeIndex(volume) == 0)
                    return true;
            }

            return false;
        }

        private static bool ProbeInWarmupView(Vector3 probePosition, DdgiViewPriorityContext viewPriority)
        {
            Vector3 toProbe = probePosition - viewPriority.CameraPosition;
            float forwardDistance = Vector3.Dot(toProbe, viewPriority.Forward);
            if (forwardDistance < viewPriority.NearPlane || forwardDistance > viewPriority.FarPlane)
                return false;

            float rightDistance = MathF.Abs(Vector3.Dot(toProbe, viewPriority.Right));
            float upDistance = MathF.Abs(Vector3.Dot(toProbe, viewPriority.Up));
            return rightDistance <= forwardDistance * viewPriority.TanHalfFovX &&
                upDistance <= forwardDistance * viewPriority.TanHalfFovY;
        }

        private static bool HasNewCellWork(DdgiFrameLayout? layout)
        {
            if (layout == null)
                return false;

            for (int i = 0; i < layout.DirtyProbeRequests.Count; i++)
            {
                if (layout.DirtyProbeRequests[i].Reason is not DdgiClipmapDirtyReason.DirtyBounds)
                    return true;
            }

            return false;
        }

        private static float Fraction(int numerator, int denominator, float emptyValue)
        {
            if (denominator <= 0)
                return Math.Clamp(emptyValue, 0.0f, 1.0f);
            return Math.Clamp(numerator / (float)denominator, 0.0f, 1.0f);
        }

        private void BuildScheduleDiagnostics()
        {
            ResetScheduleDiagnostics();
            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                uint flags = request.Flags;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0)
                    _lastNewProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag) != 0)
                    _lastFrustumProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag) != 0)
                    _lastOutsideFrustumProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag) != 0)
                    _lastDirtyBoundsProbeUpdateCount++;
                if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag) != 0)
                    _lastAgeRefreshProbeUpdateCount++;

                if (request.ProbeIndex < (uint)AbsoluteMaxProbeCount)
                {
                    DdgiProbeSchedulerFeedback feedback = _probeSchedulerFeedback[request.ProbeIndex];
                    if (feedback.VariabilityScore >= 0.35f)
                        _lastHighVarianceProbeUpdateCount++;
                    if (feedback.IsLowConfidence)
                        _lastLowConfidenceProbeUpdateCount++;
                    if (feedback.IsStable)
                        _lastStableProbeUpdateCount++;

                    _lastAverageProbeVariability += feedback.VariabilityScore;
                    _lastAverageProbeConfidence += feedback.CombinedConfidence;
                }

                if (request.VolumeIndex < (uint)_volumeCount)
                {
                    int volumeIndex = checked((int)request.VolumeIndex);
                    ulong rays = (ulong)Math.Max(0, ResolveVolumeRaysPerProbe(_volumeScratch[volumeIndex]));
                    _lastScheduledProbeUpdatesByVolume[volumeIndex]++;
                    _lastScheduledPrimaryRaysByVolume[volumeIndex] += rays;
                    _lastScheduledPrimaryRayCount += rays;
                }
            }

            if (_scheduledProbeUpdateCount > 0)
            {
                _lastAverageProbeVariability /= _scheduledProbeUpdateCount;
                _lastAverageProbeConfidence /= _scheduledProbeUpdateCount;
            }
        }

        private long EstimateScheduledGpuUpdateMicroseconds()
        {
            if (_scheduledProbeUpdateCount <= 0 || _lastScheduledPrimaryRayCount == 0UL)
                return 0;

            float budgetMilliseconds = _settings.GlobalIllumination.EffectiveDdgiAdaptiveBudgetTimeMilliseconds;
            if (budgetMilliseconds <= 0.0f)
                return 0;

            int steadyPrimaryRayBudget = Math.Max(1, _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget);
            ulong estimatedRayQueries = _lastScheduledPrimaryRayCount;
            if (_settings.GlobalIllumination.DdgiMaxShadedLights > 0)
                estimatedRayQueries += _lastScheduledPrimaryRayCount;

            long budgetMicroseconds = Math.Max(1L, (long)MathF.Round(budgetMilliseconds * 1000.0f));
            double scheduledLoad = estimatedRayQueries / (double)steadyPrimaryRayBudget;
            return Math.Max(1L, (long)Math.Round(budgetMicroseconds * Math.Max(0.1, scheduledLoad)));
        }

        private void AgeProbeSchedulerFeedback(int activeProbeCount)
        {
            int clampedActiveCount = Math.Clamp(activeProbeCount, 0, AbsoluteMaxProbeCount);
            for (int i = 0; i < clampedActiveCount; i++)
            {
                ref DdgiProbeSchedulerFeedback feedback = ref _probeSchedulerFeedback[i];
                if (!feedback.HasSample)
                    continue;

                feedback.AgeFrames = feedback.AgeFrames == uint.MaxValue ? uint.MaxValue : feedback.AgeFrames + 1u;
                feedback.LuminanceChange = MathF.Max(0.0f, feedback.LuminanceChange * 0.92f - 0.002f);
                feedback.IrradianceConfidence = Math.Clamp(feedback.IrradianceConfidence + 0.006f, 0.0f, 1.0f);
                feedback.VisibilityConfidence = Math.Clamp(feedback.VisibilityConfidence + 0.004f, 0.0f, 1.0f);
                if (feedback.LuminanceChange < 0.025f)
                    feedback.LastDirtyReasonFlags = 0u;
            }
        }

        private void UpdateProbeSchedulerFeedbackFromScheduledRequests()
        {
            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                int probeIndex = checked((int)request.ProbeIndex);
                if ((uint)probeIndex >= (uint)_activeProbeCount)
                    continue;

                ulong raysPerProbe = ResolveRequestPrimaryRayCount(request);
                float rayConfidence = Math.Clamp((float)raysPerProbe / Math.Max(1, _settings.GlobalIllumination.DdgiMaxRaysPerProbe), 0.1f, 1.0f);
                float reasonImpulse = ResolveSchedulerFeedbackReasonImpulse(request.Flags);

                ref DdgiProbeSchedulerFeedback feedback = ref _probeSchedulerFeedback[probeIndex];
                float previousMean = feedback.HasSample ? feedback.LuminanceMean : 0.0f;
                float targetMean = EstimateProbeFeedbackLuminanceMean(request.Flags, rayConfidence, previousMean);
                float measuredChange = MathF.Abs(targetMean - previousMean) / MathF.Max(MathF.Max(targetMean, previousMean), 0.05f);

                feedback.LuminanceMean = feedback.HasSample
                    ? Math.Clamp(previousMean * 0.85f + targetMean * 0.15f, 0.0f, 64.0f)
                    : targetMean;
                feedback.LuminanceChange = Math.Clamp(MathF.Max(feedback.LuminanceChange, measuredChange) + reasonImpulse, 0.0f, 1.0f);
                feedback.AgeFrames = 0u;
                feedback.IrradianceConfidence = Math.Clamp(rayConfidence * (reasonImpulse > 0.2f ? 0.72f : 0.9f), 0.0f, 1.0f);
                feedback.VisibilityConfidence = Math.Clamp(rayConfidence * 0.85f, 0.0f, 1.0f);
                feedback.LastDirtyReasonFlags = request.Flags;
                feedback.Initialized = 1;
            }
        }

        private ulong ResolveRequestPrimaryRayCount(in GPUDdgiProbeUpdateRequest request)
        {
            if (request.VolumeIndex >= (uint)_volumeCount)
                return 0UL;

            int volumeIndex = checked((int)request.VolumeIndex);
            return (ulong)Math.Max(0, ResolveVolumeRaysPerProbe(_volumeScratch[volumeIndex]));
        }

        private static float ResolveSchedulerFeedbackReasonImpulse(uint flags)
        {
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0)
                return 0.75f;
            if ((flags & (GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryAddedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryRemovedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonEmissiveChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonLocalLightChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirectionalLightChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamInFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamOutFlag)) != 0)
            {
                return 0.55f;
            }
            if ((flags & (GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTransformChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonMaterialChangedFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag)) != 0)
            {
                return 0.35f;
            }
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag) != 0)
                return 0.04f;

            return 0.08f;
        }

        private static float EstimateProbeFeedbackLuminanceMean(uint flags, float rayConfidence, float previousMean)
        {
            float baseline = MathF.Max(previousMean, 0.18f);
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonEmissiveChangedFlag) != 0)
                baseline = MathF.Max(baseline, 1.2f);
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirectionalLightChangedFlag) != 0)
                baseline = MathF.Max(baseline, 0.85f);
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonLocalLightChangedFlag) != 0)
                baseline = MathF.Max(baseline, 0.65f);
            if ((flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0)
                baseline = MathF.Max(baseline, 0.45f);

            return Math.Clamp(baseline * (0.8f + rayConfidence * 0.4f), 0.0f, 64.0f);
        }

        public IReadOnlyList<DdgiVolumeDiagnosticsEntry> GetVolumeDiagnostics()
        {
            if (_volumeCount <= 0)
                return Array.Empty<DdgiVolumeDiagnosticsEntry>();

            var entries = new DdgiVolumeDiagnosticsEntry[_volumeCount];
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUDdgiProbeVolume volume = _volumeScratch[i];
                int probeCount = checked(
                    Math.Max(0, (int)volume.SizeAndProbeCountX.W) *
                    Math.Max(0, (int)volume.ProbeSpacingAndProbeCountY.W) *
                    Math.Max(0, (int)volume.BiasAndProbeCountZ.W));
                var kind = (DdgiProbeVolumeKind)Math.Max(0, (int)volume.ClipmapGridMinAndKind.W);
                int localSlotIndex = kind == DdgiProbeVolumeKind.Authored
                    ? (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.X)
                    : -1;
                int localSlotGeneration = kind == DdgiProbeVolumeKind.Authored
                    ? (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Y)
                    : 0;
                int streamingCellId = kind == DdgiProbeVolumeKind.Authored
                    ? (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Z)
                    : 0;
                entries[i] = new DdgiVolumeDiagnosticsEntry(
                    VolumeIndex: i,
                    Kind: kind,
                    CascadeIndex: (int)volume.ClipmapRingOffsetAndCascade.W,
                    FirstProbeIndex: (int)volume.OriginAndFirstProbeIndex.W,
                    ProbeCount: probeCount,
                    RaysPerProbe: ResolveVolumeRaysPerProbe(volume),
                    MaxProbeUpdatesPerFrame: ResolveVolumeMaxProbeUpdatesPerFrame(volume),
                    ScheduledProbeUpdates: _lastScheduledProbeUpdatesByVolume[i],
                    ScheduledPrimaryRayCount: _lastScheduledPrimaryRaysByVolume[i],
                    MaxRayDistance: volume.BiasAndProbeCountZ.Z)
                {
                    LocalSlotIndex = localSlotIndex,
                    LocalSlotGeneration = localSlotGeneration,
                    StreamingCellId = streamingCellId,
                    QualityClass = 0,
                    PhysicalProbeCapacity = localSlotIndex >= 0 && (uint)localSlotIndex < (uint)_currentLocalSlotProbeCapacities.Length
                        ? _currentLocalSlotProbeCapacities[localSlotIndex]
                        : probeCount
                };
            }

            return entries;
        }

        private void MarkScheduledClipmapCellsUpdated(DdgiFrameLayout layout, ulong frameSerial)
        {
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> metadata = layout.VolumeMetadata;
            IReadOnlyList<DdgiClipmapCascadeState> cascades = layout.CameraRelativeCascades;
            if (metadata.Count == 0 || cascades.Count == 0)
                return;

            for (int i = 0; i < _scheduledProbeUpdateCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = _probeUpdateRequestScratch[i];
                if ((request.Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag) == 0)
                    continue;
                if (request.VolumeIndex >= metadata.Count)
                    continue;

                DdgiProbeVolumeRuntimeMetadata volumeMetadata = metadata[checked((int)request.VolumeIndex)];
                if (volumeMetadata.Kind != DdgiProbeVolumeKind.CameraClipmap)
                    continue;

                for (int cascadeIndex = 0; cascadeIndex < cascades.Count; cascadeIndex++)
                {
                    DdgiClipmapCascadeState cascade = cascades[cascadeIndex];
                    if (cascade.CascadeIndex != volumeMetadata.CascadeIndex)
                        continue;

                    cascade.MarkLogicalCellUpdated(
                        new DdgiClipmapCell(request.LogicalCellX, request.LogicalCellY, request.LogicalCellZ),
                        frameSerial);
                    break;
                }
            }
        }

        public void UploadScheduledProbeUpdateQueue(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            EnsureRayResultScratchCapacity(_scheduledProbeUpdateCount, _raysPerProbe);
            UpdatePublishedCacheDiagnostics();
            if (_scheduledProbeUpdateCount <= 0)
                return;
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI probe update queue upload.", nameof(commandBuffer));

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _probeUpdateQueueBuffer,
                _probeUpdateRequestScratch.AsSpan(0, _scheduledProbeUpdateCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        }

        public int PrepareGpuScheduleInputs(
            DdgiFrameLayout layout,
            SceneRenderingData sceneData,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            bool preserveCpuSchedulerDiagnostics = false)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for DDGI GPU scheduler input upload.", nameof(commandBuffer));

            int scheduledProbeUpdateUpperBound = PrepareGpuScheduleBudgetState(enabled: true, layout, preserveCpuSchedulerDiagnostics);

            EnsureGpuSchedulerCapacity(
                _activeProbeCount,
                _settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions,
                _settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount);

            int configuredDirtyRegionLimit = Math.Clamp(
                _settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions,
                0,
                GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget);
            DdgiGpuSchedulerDirtyRegionUploadPlan dirtyRegionUpload = CalculateGpuSchedulerDirtyRegionUpload(
                layout.DirtyRegions.Count,
                configuredDirtyRegionLimit,
                _dirtyRegionCapacity);
            int uploadDirtyRegionCount = dirtyRegionUpload.UploadCount;
            _lastGpuSchedulerDirtyRegionCount = dirtyRegionUpload.UploadCount;
            _lastGpuSchedulerDirtyRegionOverflowCount = dirtyRegionUpload.OverflowCount;

            EnsureGpuDirtyRegionScratchCapacity(_dirtyRegionCapacity);
            for (int i = 0; i < uploadDirtyRegionCount; i++)
                _gpuDirtyRegionScratch[i] = ConvertDirtyRegion(layout.DirtyRegions[i]);
            if (uploadDirtyRegionCount < _dirtyRegionCapacity)
                Array.Clear(_gpuDirtyRegionScratch, uploadDirtyRegionCount, _dirtyRegionCapacity - uploadDirtyRegionCount);

            GPUDdgiSchedulerConstants constants = BuildGpuSchedulerConstants(layout, sceneData, uploadDirtyRegionCount);
            GPUDdgiSchedulerCounters counters = default;
            GPUDdgiTraceIndirectDispatch dispatch = default;
            ulong uploadedBytes = 0UL;
            uploadedBytes += GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _schedulerConstantsBuffer,
                constants,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit)).ByteCount;
            uploadedBytes += GpuBufferUploader.UploadPaddedSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _dirtyRegionBuffer,
                _gpuDirtyRegionScratch.AsSpan(0, uploadDirtyRegionCount),
                _dirtyRegionCapacity,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit)).ByteCount;
            uploadedBytes += GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _schedulerCounterBuffer,
                counters,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit)).ByteCount;
            uploadedBytes += GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _traceIndirectDispatchBuffer,
                dispatch,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.IndirectCommandReadBit)).ByteCount;
            _lastGpuSchedulerUploadBytes = uploadedBytes;
            return scheduledProbeUpdateUpperBound;
        }

        public void CaptureGpuSchedulerValidationExpectedFrame(int frameIndex, int cpuRequestCount)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            int readbackIndex = frameIndex % SchedulerReadbackRingSize;
            int clampedRequestCount = Math.Clamp(cpuRequestCount, 0, AbsoluteMaxProbeCount);
            if (clampedRequestCount > 0)
            {
                Array.Copy(
                    _probeUpdateRequestScratch,
                    0,
                    _schedulerValidationExpectedRequests[readbackIndex],
                    0,
                    clampedRequestCount);
            }

            ulong primaryRayCount = 0UL;
            for (int i = 0; i < clampedRequestCount; i++)
                primaryRayCount += ResolveRequestPrimaryRayCount(_probeUpdateRequestScratch[i]);

            _schedulerValidationExpectedFrames[readbackIndex] = new ValidationExpectedFrame(
                true,
                clampedRequestCount,
                primaryRayCount,
                Math.Max(0, _lastProbeUpdateRequestBudget),
                Math.Max(0, _lastProbeUpdatePrimaryRayBudget),
                Math.Clamp(_activeProbeCount, 0, AbsoluteMaxProbeCount),
                Math.Clamp(_volumeCount, 0, AbsoluteMaxVolumeCount));
        }

        public void ClearGpuSchedulerValidationExpectedFrame(int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            _schedulerValidationExpectedFrames[frameIndex % SchedulerReadbackRingSize] = ValidationExpectedFrame.Invalid;
        }

        public void ReadCompletedGpuSchedulerCounters(int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            int readbackIndex = frameIndex % SchedulerReadbackRingSize;

            if (!_schedulerCounterReadbackRecorded[readbackIndex] || !_schedulerCounterReadbackBuffers[readbackIndex].IsValid)
            {
                _lastCompletedGpuSchedulerCounters = default;
                _lastCompletedGpuSchedulerCountersValid = 0;
                _lastCompletedGpuSchedulerReadbackLatencyFrames = 0;
                _lastCompletedGpuSchedulerRequestBudget = 0;
                _lastCompletedGpuSchedulerPrimaryRayBudget = 0;
                _lastCompletedGpuSchedulerQueueCapacity = 0;
                _lastCompletedGpuSchedulerValidation = DdgiGpuSchedulerValidationSnapshot.Invalid;
                return;
            }

            _bufferManager.InvalidateBuffer(_schedulerCounterReadbackBuffers[readbackIndex], 0, SchedulerCounterBufferSize);
            GPUDdgiSchedulerCounters* counters = (GPUDdgiSchedulerCounters*)_bufferManager.GetMappedPointer(_schedulerCounterReadbackBuffers[readbackIndex]);
            _lastCompletedGpuSchedulerCounters = *counters;
            _lastCompletedGpuSchedulerCountersValid = 1;
            _lastCompletedGpuSchedulerReadbackLatencyFrames = RenderingConstants.FramesInFlight;
            _lastCompletedGpuSchedulerRequestBudget = _schedulerCounterReadbackRequestBudgets[readbackIndex];
            _lastCompletedGpuSchedulerPrimaryRayBudget = _schedulerCounterReadbackPrimaryRayBudgets[readbackIndex];
            _lastCompletedGpuSchedulerQueueCapacity = _schedulerCounterReadbackQueueCapacities[readbackIndex];
            ApplyGpuWarmupCounterFractions(*counters);
            ReadCompletedGpuSchedulerValidation(readbackIndex, *counters);
            _schedulerCounterReadbackRecorded[readbackIndex] = false;
        }

        private void ApplyGpuWarmupCounterFractions(GPUDdgiSchedulerCounters counters)
        {
            if (counters.WarmupVisibleProbeCount > 0)
            {
                _lastWarmedVisibleProbeFraction = Math.Clamp(
                    counters.WarmupWarmedVisibleProbeCount / (float)counters.WarmupVisibleProbeCount,
                    0.0f,
                    1.0f);
            }
            if (counters.WarmupLocalProbeCount > 0)
            {
                _lastWarmedLocalProbeFraction = Math.Clamp(
                    counters.WarmupWarmedLocalProbeCount / (float)counters.WarmupLocalProbeCount,
                    0.0f,
                    1.0f);
            }
            if (counters.WarmupCascade0ProbeCount > 0)
            {
                _lastWarmedCascade0ProbeFraction = Math.Clamp(
                    counters.WarmupWarmedCascade0ProbeCount / (float)counters.WarmupCascade0ProbeCount,
                    0.0f,
                    1.0f);
            }

            if (_warmupState == DdgiRuntimeWarmupState.LocalVolumeWarmup &&
                _lastWarmedLocalProbeFraction >= WarmupCompletionTarget)
            {
                _warmupState = DdgiRuntimeWarmupState.NearCascadeWarmup;
            }
            if ((_warmupState == DdgiRuntimeWarmupState.NearCascadeWarmup ||
                _warmupState == DdgiRuntimeWarmupState.Recovery) &&
                _lastWarmedLocalProbeFraction >= WarmupCompletionTarget &&
                _lastWarmedCascade0ProbeFraction >= WarmupCompletionTarget)
            {
                _warmupState = DdgiRuntimeWarmupState.SteadyState;
            }
        }

        private void ReadCompletedGpuSchedulerValidation(int readbackIndex, GPUDdgiSchedulerCounters counters)
        {
            ValidationExpectedFrame expectedFrame = _schedulerValidationExpectedFrames[readbackIndex];
            if (!expectedFrame.Valid)
            {
                _lastCompletedGpuSchedulerValidation = DdgiGpuSchedulerValidationSnapshot.Invalid;
                _schedulerQueueReadbackRecorded[readbackIndex] = false;
                return;
            }

            if (!_schedulerQueueReadbackRecorded[readbackIndex] || !_schedulerQueueReadbackBuffers[readbackIndex].IsValid)
            {
                _lastCompletedGpuSchedulerValidation = new DdgiGpuSchedulerValidationSnapshot(
                    0,
                    "missing-readback",
                    expectedFrame.CpuRequestCount,
                    counters.RequestCount,
                    0,
                    0,
                    AbsoluteMaxProbeCount,
                    "GPU scheduler queue readback was not recorded.");
                return;
            }

            uint clampedGpuRequestCount = Math.Min(counters.RequestCount, (uint)AbsoluteMaxProbeCount);
            ulong readBytes = Math.Max(
                GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride,
                clampedGpuRequestCount * GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride);
            _bufferManager.InvalidateBuffer(_schedulerQueueReadbackBuffers[readbackIndex], 0, readBytes);
            GPUDdgiProbeUpdateRequest* gpuRequests =
                (GPUDdgiProbeUpdateRequest*)_bufferManager.GetMappedPointer(_schedulerQueueReadbackBuffers[readbackIndex]);

            _lastCompletedGpuSchedulerValidation = ValidateCompletedGpuSchedulerFrame(
                expectedFrame,
                counters,
                gpuRequests,
                (int)clampedGpuRequestCount);
            _schedulerQueueReadbackRecorded[readbackIndex] = false;
        }

        private DdgiGpuSchedulerValidationSnapshot ValidateCompletedGpuSchedulerFrame(
            ValidationExpectedFrame expectedFrame,
            GPUDdgiSchedulerCounters counters,
            GPUDdgiProbeUpdateRequest* gpuRequests,
            int gpuRequestCount)
        {
            int mismatchCount = 0;
            string firstMismatch = string.Empty;

            void Report(string message)
            {
                mismatchCount++;
                if (firstMismatch.Length == 0)
                    firstMismatch = message;
            }

            if (counters.RequestCount > (uint)expectedFrame.RequestBudget)
                Report($"request count exceeds budget: gpu={counters.RequestCount} budget={expectedFrame.RequestBudget}");
            if (counters.PrimaryRayCount > (uint)expectedFrame.PrimaryRayBudget)
                Report($"primary rays exceed budget: gpu={counters.PrimaryRayCount} budget={expectedFrame.PrimaryRayBudget}");
            if (counters.RequestCount > (uint)AbsoluteMaxProbeCount)
                Report($"request count exceeds readback capacity: gpu={counters.RequestCount} capacity={AbsoluteMaxProbeCount}");

            int countTolerance = Math.Max(1, (int)Math.Ceiling(expectedFrame.CpuRequestCount * 0.10));
            if (Math.Abs(gpuRequestCount - expectedFrame.CpuRequestCount) > countTolerance)
                Report($"request count drift exceeds 10%: cpu={expectedFrame.CpuRequestCount} gpu={gpuRequestCount}");

            ulong cpuPrimaryRayCount = expectedFrame.CpuPrimaryRayCount;
            ulong primaryTolerance = Math.Max(1UL, (ulong)Math.Ceiling(cpuPrimaryRayCount * 0.10));
            bool budgetsUnsaturated = cpuPrimaryRayCount < (ulong)Math.Max(0, expectedFrame.PrimaryRayBudget) &&
                counters.PrimaryRayCount < (uint)Math.Max(0, expectedFrame.PrimaryRayBudget);
            if (budgetsUnsaturated &&
                AbsoluteDifference((ulong)counters.PrimaryRayCount, cpuPrimaryRayCount) > primaryTolerance)
            {
                Report($"primary ray drift exceeds 10%: cpu={cpuPrimaryRayCount} gpu={counters.PrimaryRayCount}");
            }

            Array.Clear(_schedulerValidationSeenProbes, 0, Math.Max(0, expectedFrame.ActiveProbeCount));
            int[] perVolumeCounts = new int[AbsoluteMaxVolumeCount];
            ulong computedPrimaryRayCount = 0UL;
            for (int i = 0; i < gpuRequestCount; i++)
            {
                GPUDdgiProbeUpdateRequest request = gpuRequests[i];
                if (request.ProbeIndex >= (uint)expectedFrame.ActiveProbeCount)
                {
                    Report($"inactive probe request at {i}: probe={request.ProbeIndex} active={expectedFrame.ActiveProbeCount}");
                    continue;
                }

                int probeIndex = checked((int)request.ProbeIndex);
                if (_schedulerValidationSeenProbes[probeIndex] != 0)
                    Report($"duplicate probe request at {i}: probe={request.ProbeIndex}");
                _schedulerValidationSeenProbes[probeIndex] = 1;

                if (request.VolumeIndex >= (uint)expectedFrame.VolumeCount)
                {
                    Report($"invalid volume at {i}: volume={request.VolumeIndex} volumeCount={expectedFrame.VolumeCount}");
                    continue;
                }

                int volumeIndex = checked((int)request.VolumeIndex);
                perVolumeCounts[volumeIndex]++;
                ulong rays = ResolveRequestPrimaryRayCount(request);
                if (rays == 0UL)
                    Report($"zero-ray request at {i}: probe={request.ProbeIndex} volume={request.VolumeIndex}");
                computedPrimaryRayCount += rays;
                ValidateRequestVolumeMapping(i, request, Report);
            }

            int perVolumeTotal = 0;
            for (int i = 0; i < expectedFrame.VolumeCount; i++)
                perVolumeTotal += perVolumeCounts[i];
            if (perVolumeTotal != gpuRequestCount)
                Report($"per-volume request counts do not sum to total: sum={perVolumeTotal} gpu={gpuRequestCount}");
            if (computedPrimaryRayCount != counters.PrimaryRayCount)
                Report($"primary ray counter mismatch: computed={computedPrimaryRayCount} counter={counters.PrimaryRayCount}");

            string status = mismatchCount == 0 ? "ok" : "mismatch";
            return new DdgiGpuSchedulerValidationSnapshot(
                1,
                status,
                expectedFrame.CpuRequestCount,
                counters.RequestCount,
                Math.Min(expectedFrame.CpuRequestCount, gpuRequestCount),
                mismatchCount,
                AbsoluteMaxProbeCount,
                firstMismatch);
        }

        private void ValidateRequestVolumeMapping(
            int requestIndex,
            in GPUDdgiProbeUpdateRequest request,
            Action<string> report)
        {
            int volumeIndex = checked((int)request.VolumeIndex);
            GPUDdgiProbeVolume volume = _volumeScratch[volumeIndex];
            int firstProbe = Math.Max(0, (int)MathF.Round(volume.OriginAndFirstProbeIndex.W));
            int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
            int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
            int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
            int volumeProbeCount = countX * countY * countZ;
            if (request.ProbeIndex < (uint)firstProbe || request.ProbeIndex >= (uint)(firstProbe + volumeProbeCount))
            {
                report($"probe does not belong to volume at {requestIndex}: probe={request.ProbeIndex} volume={request.VolumeIndex}");
                return;
            }

            int kind = Math.Max(0, (int)MathF.Round(volume.ClipmapGridMinAndKind.W));
            if (kind == (int)DdgiProbeVolumeKind.CameraClipmap)
                return;

            int localIndex = checked((int)request.ProbeIndex) - firstProbe;
            int expectedX = localIndex % countX;
            int expectedY = (localIndex / countX) % countY;
            int expectedZ = localIndex / (countX * countY);
            if (request.LogicalCellX != expectedX ||
                request.LogicalCellY != expectedY ||
                request.LogicalCellZ != expectedZ)
            {
                report(
                    $"invalid logical cell at {requestIndex}: probe={request.ProbeIndex} logical=({request.LogicalCellX},{request.LogicalCellY},{request.LogicalCellZ}) expected=({expectedX},{expectedY},{expectedZ})");
            }
        }

        private static ulong AbsoluteDifference(ulong left, ulong right)
        {
            return left >= right ? left - right : right - left;
        }

        public void RecordGpuSchedulerCounterReadback(CommandBuffer commandBuffer, int frameIndex)
        {
            RenderingConstants.ValidateFrameIndex(frameIndex);
            if (commandBuffer.Handle == 0 || !_schedulerCounterBuffer.IsValid)
                return;

            int readbackIndex = frameIndex % SchedulerReadbackRingSize;
            if (!_schedulerCounterReadbackBuffers[readbackIndex].IsValid)
                _schedulerCounterReadbackBuffers[readbackIndex] = CreateSchedulerReadbackBuffer(readbackIndex);

            VkBuffer source = _bufferManager.GetBuffer(_schedulerCounterBuffer);
            VkBuffer destination = _bufferManager.GetBuffer(_schedulerCounterReadbackBuffers[readbackIndex]);

            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                SchedulerCounterBufferSize);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = SchedulerCounterBufferSize
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

            if (ShouldRecordGpuSchedulerValidationQueue())
                RecordGpuSchedulerQueueReadback(commandBuffer, frameIndex);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                SchedulerCounterBufferSize);
            ExecuteBufferBarrier(commandBuffer, afterCopy);

            _schedulerCounterReadbackRequestBudgets[readbackIndex] = Math.Max(0, _lastProbeUpdateRequestBudget);
            _schedulerCounterReadbackPrimaryRayBudgets[readbackIndex] = Math.Max(0, _lastProbeUpdatePrimaryRayBudget);
            _schedulerCounterReadbackQueueCapacities[readbackIndex] = Math.Max(0, _maxProbeUpdatesPerFrame);
            _schedulerCounterReadbackRecorded[readbackIndex] = true;
        }

        public void RecordGpuSchedulerTraceIndirectDispatch(CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0 || !HasGpuSchedulerTraceIndirectDispatchBuffer)
                return;

            VkBuffer indirectBuffer = _bufferManager.GetBuffer(_traceIndirectDispatchBuffer);
            _context.Api.CmdDispatchIndirect(commandBuffer, indirectBuffer, 0);
        }

        private bool ShouldRecordGpuSchedulerValidationQueue()
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return gi.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare &&
                gi.DdgiGpuSchedulerReadbackValidationEnabled &&
                gi.DdgiCompareModeUseGpuQueueForRendering;
        }

        private void RecordGpuSchedulerQueueReadback(CommandBuffer commandBuffer, int frameIndex)
        {
            int readbackIndex = frameIndex % SchedulerReadbackRingSize;
            if (!_probeUpdateQueueBuffer.IsValid)
                return;
            if (!_schedulerQueueReadbackBuffers[readbackIndex].IsValid)
            {
                _schedulerQueueReadbackBuffers[readbackIndex] = CreateSchedulerQueueReadbackBuffer(readbackIndex);
                _schedulerQueueReadbackBufferSize = checked(SchedulerQueueReadbackBufferSize * (ulong)_schedulerQueueReadbackBuffers.Length);
            }

            VkBuffer source = _bufferManager.GetBuffer(_probeUpdateQueueBuffer);
            VkBuffer destination = _bufferManager.GetBuffer(_schedulerQueueReadbackBuffers[readbackIndex]);
            ulong copyBytes = Math.Min(_probeUpdateQueueBufferSize, SchedulerQueueReadbackBufferSize);
            if (copyBytes == 0UL)
                return;

            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
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
            _schedulerQueueReadbackRecorded[readbackIndex] = true;
        }

        private int PrepareGpuScheduleBudgetState(bool enabled, DdgiFrameLayout? layout = null, bool preserveCpuSchedulerDiagnostics = false)
        {
            if (!enabled || _activeProbeCount <= 0 || _maxProbeUpdatesPerFrame <= 0)
            {
                _scheduledUpdateStartProbeIndex = 0;
                _scheduledProbeUpdateCount = 0;
                _lastProbeUpdateRequestBudget = 0;
                _lastProbeUpdatePrimaryRayBudget = 0;
                ResetAdaptiveBudgetDiagnostics();
                ResetScheduleDiagnostics();
                ResetSchedulerTimingDiagnostics();
                ResetGpuScheduleTimingDiagnostics();
                _schedulerInstrumentation.Reset();
                ResetPublishedCacheDiagnostics(resetCacheIdentity: true);
                ResetWarmupDiagnostics();
                _lastGpuUpdateMicroseconds = 0;
                _lastGpuUpdateEstimated = false;
                return 0;
            }

            UpdateWarmupStateAndDiagnostics(layout, ShouldEnterWarmupRecovery(layout));

            DdgiAdaptiveBudgetSelection budgetSelection = DdgiProbeUpdateScheduler.CalculateAdaptiveBudgets(
                _maxProbeUpdatesPerFrame,
                _activeProbeCount,
                _settings.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame,
                _settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget,
                _settings.GlobalIllumination.DdgiColdStartPrimaryRayBudget,
                _settings.GlobalIllumination.DdgiMinimumProbeRefreshFrames,
                _settings.GlobalIllumination.DdgiMaxShadedLights,
                _settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled,
                _settings.GlobalIllumination.EffectiveDdgiAdaptiveBudgetTimeMilliseconds,
                _settings.GlobalIllumination.DdgiAdaptiveBudgetHysteresisFraction,
                _settings.GlobalIllumination.DdgiEmergencyDegradeGpuTimeMultiplier,
                _lastGpuUpdateMicroseconds,
                _adaptiveBudgetScale,
                _lastGpuUpdateEstimated);
            _adaptiveBudgetScale = budgetSelection.BudgetScale;
            _lastAdaptiveBudgetReduced = budgetSelection.BudgetReduced ? 1 : 0;
            _lastEmergencyDegradeActive = budgetSelection.EmergencyDegradeActive ? 1 : 0;
            _lastEffectiveMaxShadedLights = budgetSelection.EffectiveMaxShadedLights;
            _lastAdaptiveBudgetReason = budgetSelection.Reason;
            _lastProbeUpdateRequestBudget = ApplyNonStarvingRequestBudgetFloor(budgetSelection.RequestBudget, layout);
            _lastProbeUpdatePrimaryRayBudget = ApplyNonStarvingPrimaryRayBudgetFloor(
                budgetSelection.PrimaryRayBudget,
                _lastProbeUpdateRequestBudget);
            if (_gpuScheduleConsecutiveOverBudgetFrames >= GpuScheduleOverBudgetReductionFrameThreshold &&
                _lastProbeUpdateRequestBudget > 1)
            {
                _lastProbeUpdateRequestBudget = Math.Max(
                    1,
                    (int)MathF.Floor(_lastProbeUpdateRequestBudget * GpuScheduleOverBudgetRequestBudgetScale));
                _lastProbeUpdateRequestBudget = ApplyNonStarvingRequestBudgetFloor(_lastProbeUpdateRequestBudget, layout);
                _lastProbeUpdatePrimaryRayBudget = ApplyNonStarvingPrimaryRayBudgetFloor(
                    _lastProbeUpdatePrimaryRayBudget,
                    _lastProbeUpdateRequestBudget);
                _lastAdaptiveBudgetReduced = 1;
                _lastAdaptiveBudgetReason = _lastAdaptiveBudgetReason.Length == 0 || _lastAdaptiveBudgetReason == "within-budget"
                    ? "gpu-schedule-over-budget"
                    : _lastAdaptiveBudgetReason + "+gpu-schedule-over-budget";
            }
            _scheduledUpdateStartProbeIndex = 0;
            _scheduledProbeUpdateCount = Math.Min(_activeProbeCount, _lastProbeUpdateRequestBudget);
            ResetScheduleDiagnostics();
            if (!preserveCpuSchedulerDiagnostics)
            {
                ResetSchedulerTimingDiagnostics();
                _schedulerInstrumentation.Reset();
            }
            _lastScheduledPrimaryRayCount = Math.Min(
                (ulong)Math.Max(0, _lastProbeUpdatePrimaryRayBudget),
                (ulong)Math.Max(0, _scheduledProbeUpdateCount) * (ulong)Math.Max(1, _raysPerProbe));
            EnsureRayResultScratchCapacity(_scheduledProbeUpdateCount, _raysPerProbe);
            UpdatePublishedCacheDiagnostics();
            return _scheduledProbeUpdateCount;
        }

        public void PublishCompletedUpdates(SceneRenderingData sceneData)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));

            UpdatePublishedCacheDiagnostics();
            if (_scheduledProbeUpdateCount > 0 && _raysPerProbe > 0)
            {
                _publishedCacheGeneration = unchecked(_publishedCacheGeneration + 1u);
                if (_publishedCacheGeneration == 0u)
                    _publishedCacheGeneration = 1u;
                _publishedCacheLastUpdatedFrameSerial = sceneData.DdgiFrameSerial;
            }
            sceneData.DdgiRayScratchBytes = _lastRayScratchBytes;
            sceneData.DdgiUpdatedAtlasBytes = _lastUpdatedAtlasBytes;
            sceneData.DdgiPublishedCacheLatencyFrames = _lastPublishedCacheLatencyFrames;
            sceneData.DdgiCacheGeneration = _publishedCacheGeneration;
            sceneData.DdgiLastUpdatedFrameSerial = _publishedCacheLastUpdatedFrameSerial;
            sceneData.DdgiCacheWarmupState = _warmupState;
        }

        private void UpdatePublishedCacheDiagnostics()
        {
            if (_scheduledProbeUpdateCount <= 0 || _raysPerProbe <= 0)
            {
                ResetPublishedCacheTransientDiagnostics();
                return;
            }

            _lastRayScratchBytes = CalculateRayScratchBytes(_scheduledProbeUpdateCount, _raysPerProbe);
            _lastUpdatedAtlasBytes = checked((ulong)_scheduledProbeUpdateCount *
                (GlobalIlluminationProbeVolumeData.IrradianceBytesPerProbe +
                 GlobalIlluminationProbeVolumeData.VisibilityBytesPerProbe));
            _lastPublishedCacheLatencyFrames = 1;
        }

        private bool TryScheduleDirtyProbeUpdates(IReadOnlyList<BoundingBox>? dirtyBounds, int updateCount)
        {
            if (dirtyBounds == null || dirtyBounds.Count == 0 || _volumeCount <= 0 || updateCount <= 0)
                return false;

            int bestProbeIndex = -1;
            float bestScore = float.MaxValue;

            for (int dirtyIndex = 0; dirtyIndex < dirtyBounds.Count; dirtyIndex++)
            {
                BoundingBox dirtyBoundsItem = dirtyBounds[dirtyIndex];
                Vector3 dirtyCenter = dirtyBoundsItem.Center;

                for (int volumeIndex = 0; volumeIndex < _volumeCount; volumeIndex++)
                {
                    GPUDdgiProbeVolume volume = _volumeScratch[volumeIndex];
                    Vector3 origin = ReadVector3(volume.OriginAndFirstProbeIndex);
                    Vector3 size = ReadVector3(volume.SizeAndProbeCountX);
                    Vector3 spacing = ReadVector3(volume.ProbeSpacingAndProbeCountY);
                    int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
                    int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
                    int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
                    int firstProbeIndex = Math.Clamp((int)MathF.Round(volume.OriginAndFirstProbeIndex.W), 0, _activeProbeCount - 1);

                    BoundingBox volumeBounds = new(origin, origin + size);
                    if (!dirtyBoundsItem.Intersects(volumeBounds) && !volumeBounds.Contains(dirtyCenter))
                        continue;

                    int x = spacing.X > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.X - origin.X) / spacing.X), 0, countX - 1) : 0;
                    int y = spacing.Y > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Y - origin.Y) / spacing.Y), 0, countY - 1) : 0;
                    int z = spacing.Z > 0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Z - origin.Z) / spacing.Z), 0, countZ - 1) : 0;
                    int localProbeIndex = x + y * countX + z * countX * countY;
                    int probeIndex = Math.Clamp(firstProbeIndex + localProbeIndex, 0, _activeProbeCount - 1);

                    Vector3 probePosition = origin + new Vector3(spacing.X * x, spacing.Y * y, spacing.Z * z);
                    float score = Vector3.DistanceSquared(probePosition, dirtyCenter);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestProbeIndex = probeIndex;
                    }
                }
            }

            if (bestProbeIndex < 0)
                return false;

            _scheduledUpdateStartProbeIndex = CalculateProbeUpdateStartForDirtyProbe(
                _activeProbeCount,
                updateCount,
                bestProbeIndex);
            _updateCursor = (_scheduledUpdateStartProbeIndex + updateCount) % _activeProbeCount;
            return true;
        }

        internal static int CalculateProbeUpdateStartForDirtyProbe(int activeProbeCount, int updateCount, int dirtyProbeIndex)
        {
            if (activeProbeCount <= 0 || updateCount <= 0)
                return 0;

            int clampedUpdateCount = Math.Min(updateCount, activeProbeCount);
            int clampedProbeIndex = Math.Clamp(dirtyProbeIndex, 0, activeProbeCount - 1);
            int maxStart = Math.Max(0, activeProbeCount - clampedUpdateCount);
            return Math.Clamp(clampedProbeIndex - clampedUpdateCount / 2, 0, maxStart);
        }

        private static Vector3 ReadVector3(Vector4 value) => new(value.X, value.Y, value.Z);

        internal static DdgiGpuSchedulerResourceLayout CalculateGpuSchedulerResourceLayout(
            int activeProbeCount,
            int maxDirtyRegions,
            int priorityBucketCount)
        {
            int clampedActiveProbeCount = Math.Clamp(activeProbeCount, 0, AbsoluteMaxProbeCount);
            int dirtyRegionCapacity = Math.Max(
                1,
                Math.Clamp(maxDirtyRegions, 0, GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget));
            int bucketCount = Math.Clamp(priorityBucketCount, 1, 256);
            int candidateCapacity = Math.Max(1, checked(clampedActiveProbeCount * 2));
            int workgroupCount = Math.Max(1, (clampedActiveProbeCount + SchedulerWorkgroupSize - 1) / SchedulerWorkgroupSize);
            int groupCountCapacity = checked(workgroupCount * bucketCount);
            int prefixCapacity = checked(groupCountCapacity + bucketCount + 1);

            return new DdgiGpuSchedulerResourceLayout(
                dirtyRegionCapacity,
                candidateCapacity,
                groupCountCapacity,
                prefixCapacity,
                workgroupCount,
                bucketCount,
                checked((ulong)dirtyRegionCapacity * (ulong)Marshal.SizeOf<GPUDdgiDirtyRegion>()),
                checked((ulong)candidateCapacity * (ulong)Marshal.SizeOf<GPUDdgiProbeCandidate>()),
                checked((ulong)groupCountCapacity * sizeof(uint)),
                checked((ulong)prefixCapacity * sizeof(uint)));
        }

        internal static DdgiGpuSchedulerDirtyRegionUploadPlan CalculateGpuSchedulerDirtyRegionUpload(
            int sourceDirtyRegionCount,
            int configuredDirtyRegionLimit,
            int allocatedDirtyRegionCapacity)
        {
            int sourceCount = Math.Max(0, sourceDirtyRegionCount);
            int configuredLimit = Math.Clamp(
                configuredDirtyRegionLimit,
                0,
                GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget);
            int allocatedCapacity = Math.Max(0, allocatedDirtyRegionCapacity);
            int uploadCount = Math.Min(sourceCount, Math.Min(configuredLimit, allocatedCapacity));
            int overflowCount = Math.Max(0, sourceCount - uploadCount);
            return new DdgiGpuSchedulerDirtyRegionUploadPlan(uploadCount, overflowCount);
        }

        private GPUDdgiSchedulerConstants BuildGpuSchedulerConstants(
            DdgiFrameLayout layout,
            SceneRenderingData sceneData,
            int dirtyRegionCount)
        {
            DdgiViewPriorityContext viewPriority = layout.ViewPriority;
            uint flags = 0u;
            if (viewPriority.Enabled)
                flags |= 1u;
            if (_settings.GlobalIllumination.DdgiGpuSchedulerReadbackValidationEnabled)
                flags |= 1u << 1;
            if (_settings.GlobalIllumination.DdgiGpuSchedulerFallbackOnValidationFailure)
                flags |= 1u << 2;
            DdgiWarmupBudgetSplit warmupBudget = CalculateWarmupBudgetSplit(_lastProbeUpdateRequestBudget);

            return new GPUDdgiSchedulerConstants
            {
                ActiveProbeCount = (uint)Math.Clamp(_activeProbeCount, 0, AbsoluteMaxProbeCount),
                VolumeCount = (uint)Math.Clamp(_volumeCount, 0, AbsoluteMaxVolumeCount),
                RequestBudget = (uint)Math.Max(0, _lastProbeUpdateRequestBudget),
                PrimaryRayBudget = (uint)Math.Max(0, _lastProbeUpdatePrimaryRayBudget),
                DirtyRegionCount = (uint)Math.Clamp(dirtyRegionCount, 0, _dirtyRegionCapacity),
                PriorityBucketCount = (uint)Math.Clamp(
                    _settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount,
                    1,
                    256),
                FrameSerial = sceneData.DdgiFrameSerialLow32,
                Flags = flags,
                CameraPositionNearPlane = viewPriority.Enabled
                    ? new Vector4(viewPriority.CameraPosition.X, viewPriority.CameraPosition.Y, viewPriority.CameraPosition.Z, viewPriority.NearPlane)
                    : Vector4.Zero,
                ForwardFarPlane = viewPriority.Enabled
                    ? new Vector4(viewPriority.Forward.X, viewPriority.Forward.Y, viewPriority.Forward.Z, viewPriority.FarPlane)
                    : Vector4.Zero,
                RightTanHalfFovX = viewPriority.Enabled
                    ? new Vector4(viewPriority.Right.X, viewPriority.Right.Y, viewPriority.Right.Z, viewPriority.TanHalfFovX)
                    : Vector4.Zero,
                UpTanHalfFovY = viewPriority.Enabled
                    ? new Vector4(viewPriority.Up.X, viewPriority.Up.Y, viewPriority.Up.Z, viewPriority.TanHalfFovY)
                    : Vector4.Zero,
                CameraVelocitySafetyRadius = viewPriority.Enabled
                    ? new Vector4(viewPriority.CameraVelocity.X, viewPriority.CameraVelocity.Y, viewPriority.CameraVelocity.Z, viewPriority.SafetyRadius)
                    : Vector4.Zero,
                FrustumPriorityWeight = _settings.GlobalIllumination.DdgiFrustumPriorityWeight,
                NewProbeUpdateBoost = _settings.GlobalIllumination.DdgiNewProbeUpdateBoost,
                OutOfFrustumMinimumUpdateFraction = _settings.GlobalIllumination.DdgiOutOfFrustumMinimumUpdateFraction,
                MinimumProbeRefreshFrames = (uint)Math.Max(1, _settings.GlobalIllumination.DdgiMinimumProbeRefreshFrames),
                WarmupState = (uint)_warmupState,
                WarmupLocalBudget = (uint)Math.Max(0, warmupBudget.Local),
                WarmupCascade0Budget = (uint)Math.Max(0, warmupBudget.Cascade0),
                WarmupNewCellBudget = (uint)Math.Max(0, warmupBudget.NewCell),
                WarmupSafetyBudget = (uint)Math.Max(0, warmupBudget.Safety)
            };
        }

        private static DdgiWarmupBudgetSplit CalculateWarmupBudgetSplit(int requestBudget)
        {
            int budget = Math.Max(0, requestBudget);
            int local = Math.Clamp((int)MathF.Ceiling(budget * 0.50f), 0, budget);
            int cascade0 = Math.Clamp((int)MathF.Ceiling(budget * 0.35f), 0, Math.Max(0, budget - local));
            int newCell = Math.Clamp((int)MathF.Ceiling(budget * 0.10f), 0, Math.Max(0, budget - local - cascade0));
            int safety = Math.Max(0, budget - local - cascade0 - newCell);
            return new DdgiWarmupBudgetSplit(local, cascade0, newCell, safety);
        }

        private static GPUDdgiDirtyRegion ConvertDirtyRegion(DdgiDirtyRegion region)
        {
            return new GPUDdgiDirtyRegion
            {
                MinReason = new Vector4(
                    region.Bounds.Min.X,
                    region.Bounds.Min.Y,
                    region.Bounds.Min.Z,
                    (float)(uint)region.Reason),
                MaxPadding = new Vector4(
                    region.Bounds.Max.X,
                    region.Bounds.Max.Y,
                    region.Bounds.Max.Z,
                    0.0f)
            };
        }

        private void EnsureGpuDirtyRegionScratchCapacity(int capacity)
        {
            if (_gpuDirtyRegionScratch.Length >= capacity)
                return;

            Array.Resize(ref _gpuDirtyRegionScratch, capacity);
        }

        private bool EnsureProbeStateCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinProbeStateBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeStateStride));

            return EnsureStorageBuffer(ref _probeStateBuffer, ref _probeStateBufferSize, requiredSize, BindlessIndex.DdgiProbeStateBuffer, "DDGI Probe State Buffer");
        }

        private bool EnsureRayResultScratchCapacity(int scheduledProbeCount, int rayCapacityPerProbe)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                CalculateRayScratchBytes(scheduledProbeCount, rayCapacityPerProbe));

            return EnsureStorageBuffer(
                ref _rayResultScratchBuffer,
                ref _rayResultScratchBufferSize,
                requiredSize,
                BindlessIndex.DdgiRayResultScratchBuffer,
                "DDGI Ray Result Scratch Buffer");
        }

        internal static ulong CalculateRayScratchBytes(int scheduledProbeCount, int rayCapacityPerProbe)
        {
            int probeCount = Math.Clamp(scheduledProbeCount, 0, AbsoluteMaxProbeCount);
            int raysPerProbe = Math.Clamp(rayCapacityPerProbe, 0, GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe);
            if (probeCount == 0 || raysPerProbe == 0)
                return 0UL;

            return checked((ulong)probeCount * (ulong)raysPerProbe * RayResultStride);
        }

        private bool EnsureProbeUpdateQueueCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride));

            return EnsureStorageBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBufferSize, requiredSize, BindlessIndex.DdgiProbeUpdateQueueBuffer, "DDGI Probe Update Queue Buffer");
        }

        private bool EnsureProbeRelocationClassificationCapacity(int probeCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Clamp(probeCount, 0, AbsoluteMaxProbeCount) * GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride));

            return EnsureStorageBuffer(
                ref _probeRelocationClassificationBuffer,
                ref _probeRelocationClassificationBufferSize,
                requiredSize,
                BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                "DDGI Probe Relocation Classification Buffer");
        }

        private void EnsureGpuSchedulerCapacity(int activeProbeCount, int maxDirtyRegions, int priorityBucketCount)
        {
            DdgiGpuSchedulerResourceLayout layout = CalculateGpuSchedulerResourceLayout(
                activeProbeCount,
                maxDirtyRegions,
                priorityBucketCount);

            bool schedulerResourcesRecreated = EnsureSchedulerStorageBuffer(
                ref _dirtyRegionBuffer,
                ref _dirtyRegionBufferSize,
                layout.DirtyRegionBufferSize,
                BindlessIndex.DdgiDirtyRegionBuffer,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                "DDGI Dirty Region Buffer");
            schedulerResourcesRecreated |= EnsureSchedulerStorageBuffer(
                ref _probeCandidateBuffer,
                ref _probeCandidateBufferSize,
                layout.CandidateBufferSize,
                BindlessIndex.DdgiProbeCandidateBuffer,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                "DDGI Probe Candidate Buffer");
            schedulerResourcesRecreated |= EnsureSchedulerStorageBuffer(
                ref _schedulerGroupCountBuffer,
                ref _schedulerGroupCountBufferSize,
                layout.GroupCountBufferSize,
                BindlessIndex.DdgiSchedulerGroupCountBuffer,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                "DDGI Scheduler Group Count Buffer");
            schedulerResourcesRecreated |= EnsureSchedulerStorageBuffer(
                ref _schedulerPrefixBuffer,
                ref _schedulerPrefixBufferSize,
                layout.PrefixBufferSize,
                BindlessIndex.DdgiSchedulerPrefixBuffer,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                "DDGI Scheduler Prefix Buffer");

            if (schedulerResourcesRecreated)
            {
                _lastGpuSchedulerResourceReinitializationCount++;
                _totalGpuSchedulerResourceReinitializationCount++;
            }

            _dirtyRegionCapacity = CalculateBufferElementCapacity(_dirtyRegionBufferSize, Marshal.SizeOf<GPUDdgiDirtyRegion>());
            _probeCandidateCapacity = CalculateBufferElementCapacity(_probeCandidateBufferSize, Marshal.SizeOf<GPUDdgiProbeCandidate>());
            _schedulerGroupCountCapacity = CalculateBufferElementCapacity(_schedulerGroupCountBufferSize, sizeof(uint));
            _schedulerPrefixCapacity = CalculateBufferElementCapacity(_schedulerPrefixBufferSize, sizeof(uint));
        }

        private bool EnsureAtlasCapacity(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            return EnsureStorageBuffer(
                ref handle,
                ref currentSize,
                Math.Max(MinResourceBufferSize, requiredSize),
                bindlessIndex,
                debugName);
        }

        private bool EnsureStorageBuffer(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            string debugName)
        {
            if (handle.IsValid && currentSize >= requiredSize)
                return false;

            if (handle.IsValid)
                RetireBufferResource(handle);

            currentSize = requiredSize;
            handle = _bufferManager.CreateDeviceBuffer(
                currentSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                debugName);
            _registeredBindlessHeap?.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(handle),
                0,
                currentSize);
            return true;
        }

        private bool EnsureSchedulerStorageBuffer(
            ref BufferHandle handle,
            ref ulong currentSize,
            ulong requiredSize,
            int bindlessIndex,
            BufferUsageFlags usage,
            string debugName)
        {
            ulong normalizedRequiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (handle.IsValid && currentSize >= normalizedRequiredSize)
                return false;

            bool recreated = handle.IsValid;
            if (handle.IsValid)
                RetireBufferResource(handle);

            currentSize = normalizedRequiredSize;
            handle = CreateSchedulerDeviceBuffer(currentSize, usage, debugName);
            _registeredBindlessHeap?.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(handle),
                0,
                currentSize);
            return recreated;
        }

        private BufferHandle CreateSchedulerDeviceBuffer(ulong size, BufferUsageFlags usage, string debugName)
        {
            return _bufferManager.CreateDeviceBuffer(
                Math.Max(MinResourceBufferSize, size),
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                debugName);
        }

        private static int CalculateBufferElementCapacity(ulong bufferSize, int elementSize)
        {
            if (elementSize <= 0)
                return 0;

            return (int)Math.Min(int.MaxValue, bufferSize / (ulong)elementSize);
        }

        private BufferHandle CreateSchedulerReadbackBuffer(int index)
        {
            return _bufferManager.CreateBuffer(
                SchedulerCounterBufferSize,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"DDGI Scheduler Counter Readback Buffer {index}",
                MemoryBudgetCategory.GlobalIllumination);
        }

        private BufferHandle CreateSchedulerQueueReadbackBuffer(int index)
        {
            return _bufferManager.CreateBuffer(
                SchedulerQueueReadbackBufferSize,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"DDGI Scheduler Queue Validation Readback Buffer {index}",
                MemoryBudgetCategory.GlobalIllumination);
        }

        private void ExecuteBufferBarrier(CommandBuffer commandBuffer, BufferMemoryBarrier2 barrier)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void RegisterGpuSchedulerBuffers()
        {
            RegisterIfValid(BindlessIndex.DdgiSchedulerConstantsBuffer, _schedulerConstantsBuffer, _schedulerConstantsBufferSize);
            RegisterIfValid(BindlessIndex.DdgiDirtyRegionBuffer, _dirtyRegionBuffer, _dirtyRegionBufferSize);
            RegisterIfValid(BindlessIndex.DdgiProbeCandidateBuffer, _probeCandidateBuffer, _probeCandidateBufferSize);
            RegisterIfValid(BindlessIndex.DdgiSchedulerGroupCountBuffer, _schedulerGroupCountBuffer, _schedulerGroupCountBufferSize);
            RegisterIfValid(BindlessIndex.DdgiSchedulerPrefixBuffer, _schedulerPrefixBuffer, _schedulerPrefixBufferSize);
            RegisterIfValid(BindlessIndex.DdgiSchedulerCounterBuffer, _schedulerCounterBuffer, _schedulerCounterBufferSize);
            RegisterIfValid(BindlessIndex.DdgiTraceIndirectDispatchBuffer, _traceIndirectDispatchBuffer, _traceIndirectDispatchBufferSize);
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

        private void InitializePersistentResources(StagingRing stagingRing, CommandBuffer commandBuffer, ulong resourceSignature)
        {
            ResetPublishedCacheDiagnostics(resetCacheIdentity: true);
            ClearStorageBuffer(commandBuffer, _probeStateBuffer, _probeStateBufferSize);
            ClearStorageBuffer(commandBuffer, _probeUpdateQueueBuffer, _probeUpdateQueueBufferSize);
            ClearStorageBuffer(commandBuffer, _probeRelocationClassificationBuffer, _probeRelocationClassificationBufferSize);
            ClearStorageBuffer(commandBuffer, _irradianceAtlasBuffer, _irradianceAtlasBufferSize);
            ClearStorageBuffer(commandBuffer, _rayResultScratchBuffer, _rayResultScratchBufferSize);
            UploadInitializedVisibilityAtlas(stagingRing, commandBuffer, _visibilityAtlasBuffer);
            ClearProbeSchedulerFeedbackRange(0, _activeProbeCount);

            _lastResourceSignature = resourceSignature;
            _hasResourceSignature = true;
        }

        private void InitializeProbeRange(
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            int startProbeIndex,
            int probeCount)
        {
            if (probeCount <= 0 || startProbeIndex < 0 || startProbeIndex >= _activeProbeCount)
                return;

            int clampedCount = Math.Min(probeCount, _activeProbeCount - startProbeIndex);
            ClearStorageBufferRange(
                commandBuffer,
                _probeStateBuffer,
                checked((ulong)startProbeIndex * GlobalIlluminationProbeVolumeData.ProbeStateStride),
                checked((ulong)clampedCount * GlobalIlluminationProbeVolumeData.ProbeStateStride));
            ClearStorageBufferRange(
                commandBuffer,
                _probeRelocationClassificationBuffer,
                checked((ulong)startProbeIndex * GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride),
                checked((ulong)clampedCount * GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride));
            ClearStorageBufferRange(
                commandBuffer,
                _irradianceAtlasBuffer,
                checked((ulong)startProbeIndex * GlobalIlluminationProbeVolumeData.IrradianceBytesPerProbe),
                checked((ulong)clampedCount * GlobalIlluminationProbeVolumeData.IrradianceBytesPerProbe));
            UploadInitializedVisibilityAtlasRange(stagingRing, commandBuffer, _visibilityAtlasBuffer, startProbeIndex, clampedCount);
            ClearProbeSchedulerFeedbackRange(startProbeIndex, clampedCount);
        }

        private void ClearProbeSchedulerFeedbackRange(int startProbeIndex, int probeCount)
        {
            if (probeCount <= 0)
                return;

            int start = Math.Clamp(startProbeIndex, 0, AbsoluteMaxProbeCount);
            int end = Math.Clamp(start + probeCount, start, AbsoluteMaxProbeCount);
            Array.Clear(_probeSchedulerFeedback, start, end - start);
        }

        private int BuildCurrentLocalSlotSignatures(
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata>? runtimeMetadata,
            ReadOnlySpan<GPUDdgiProbeVolume> volumes)
        {
            Array.Clear(_currentLocalSlotSignatures, 0, _currentLocalSlotSignatures.Length);
            Array.Clear(_currentLocalSlotGenerations, 0, _currentLocalSlotGenerations.Length);
            Array.Clear(_currentLocalSlotProbeCapacities, 0, _currentLocalSlotProbeCapacities.Length);
            if (runtimeMetadata == null || runtimeMetadata.Count == 0 || volumes.IsEmpty)
                return 0;

            int count = 0;
            for (int i = 0; i < volumes.Length && i < runtimeMetadata.Count; i++)
            {
                DdgiProbeVolumeRuntimeMetadata metadata = runtimeMetadata[i];
                if (metadata.Kind != DdgiProbeVolumeKind.Authored || metadata.LocalSlotIndex < 0)
                    continue;
                if ((uint)metadata.LocalSlotIndex >= (uint)_currentLocalSlotSignatures.Length)
                    continue;

                _currentLocalSlotSignatures[metadata.LocalSlotIndex] = CreateLocalSlotAssignmentSignature(volumes[i], metadata);
                _currentLocalSlotGenerations[metadata.LocalSlotIndex] = metadata.LocalSlotGeneration;
                _currentLocalSlotProbeCapacities[metadata.LocalSlotIndex] = Math.Max(
                    metadata.PhysicalProbeCapacity,
                    CalculateVolumeProbeCount(volumes[i], int.MaxValue));
                count++;
            }

            return count;
        }

        private void InitializeChangedLocalSlotRanges(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            for (int i = 0; i < _currentLocalSlotSignatures.Length; i++)
            {
                ulong signature = _currentLocalSlotSignatures[i];
                if (signature == 0UL)
                    continue;
                if (_lastLocalSlotSignatures[i] == signature &&
                    _lastLocalSlotGenerations[i] == _currentLocalSlotGenerations[i])
                {
                    continue;
                }

                if (!TryFindLocalSlotRange(i, out int firstProbeIndex, out int probeCount))
                    continue;

                InitializeProbeRange(stagingRing, commandBuffer, firstProbeIndex, probeCount);
                _lastLocalSlotInitBytes = checked(_lastLocalSlotInitBytes + EstimateProbeRangeInitializationBytes(probeCount));
            }
        }

        private void CommitCurrentLocalSlotSignatures()
        {
            Array.Copy(_currentLocalSlotSignatures, _lastLocalSlotSignatures, _currentLocalSlotSignatures.Length);
            Array.Copy(_currentLocalSlotGenerations, _lastLocalSlotGenerations, _currentLocalSlotGenerations.Length);
        }

        private bool TryFindLocalSlotRange(int slotIndex, out int firstProbeIndex, out int probeCount)
        {
            for (int i = 0; i < _volumeCount; i++)
            {
                GPUDdgiProbeVolume volume = _volumeScratch[i];
                var kind = (DdgiProbeVolumeKind)Math.Max(0, (int)MathF.Round(volume.ClipmapGridMinAndKind.W));
                if (kind != DdgiProbeVolumeKind.Authored)
                    continue;

                int candidateSlot = (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.X);
                if (candidateSlot != slotIndex)
                    continue;

                firstProbeIndex = Math.Max(0, (int)MathF.Round(volume.OriginAndFirstProbeIndex.W));
                int slotCapacity = (uint)slotIndex < (uint)_currentLocalSlotProbeCapacities.Length
                    ? _currentLocalSlotProbeCapacities[slotIndex]
                    : 0;
                probeCount = Math.Max(slotCapacity, CalculateVolumeProbeCount(volume, _activeProbeCount - firstProbeIndex));
                probeCount = Math.Min(probeCount, Math.Max(0, _activeProbeCount - firstProbeIndex));
                return probeCount > 0;
            }

            firstProbeIndex = 0;
            probeCount = 0;
            return false;
        }

        internal static ulong EstimateProbeRangeInitializationBytes(int probeCount)
        {
            if (probeCount <= 0)
                return 0UL;

            return checked((ulong)probeCount *
                (GlobalIlluminationProbeVolumeData.ProbeStateStride +
                 GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride +
                 GlobalIlluminationProbeVolumeData.IrradianceBytesPerProbe +
                 GlobalIlluminationProbeVolumeData.VisibilityBytesPerProbe));
        }

        private ulong EstimateLocalSlotReservedPoolBytes()
        {
            int reservedProbeCount = 0;
            for (int i = 0; i < _currentLocalSlotProbeCapacities.Length; i++)
                reservedProbeCount = checked(reservedProbeCount + Math.Max(0, _currentLocalSlotProbeCapacities[i]));

            return EstimateProbeRangeInitializationBytes(reservedProbeCount);
        }

        private static ulong CreateLocalSlotAssignmentSignature(
            GPUDdgiProbeVolume volume,
            DdgiProbeVolumeRuntimeMetadata metadata)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, (uint)Math.Max(0, metadata.LocalSlotIndex));
            hash = HashAdd(hash, (uint)Math.Max(0, metadata.StreamingCellId));
            hash = HashAdd(hash, (uint)Math.Max(0, metadata.QualityClass));
            hash = HashAdd(hash, metadata.Priority);
            hash = HashAdd(hash, metadata.BlendDistance);
            hash = HashAdd(hash, metadata.UpdatePriority);
            hash = HashAdd(hash, volume.OriginAndFirstProbeIndex);
            hash = HashAdd(hash, volume.SizeAndProbeCountX);
            hash = HashAdd(hash, volume.ProbeSpacingAndProbeCountY);
            hash = HashAdd(hash, volume.BiasAndProbeCountZ);
            hash = HashAdd(hash, volume.RayAndUpdateParams.X);
            hash = HashAdd(hash, volume.RayAndUpdateParams.Z);
            hash = HashAdd(hash, volume.RayAndUpdateParams.W);
            return hash == 0UL ? 1UL : hash;
        }

        private string ResolveCacheClearReason(
            bool resourcesRecreated,
            bool wasDdgiEnabled,
            bool hadResourceSignature,
            bool resourceSignatureChanged,
            bool localAllocationChanged)
        {
            if (resourcesRecreated)
                return "resource-resize";
            if (!wasDdgiEnabled)
                return "ddgi-enable";
            if (!hadResourceSignature || resourceSignatureChanged)
                return "cache-compatibility";
            if (localAllocationChanged)
                return "local-allocation-change";
            return "none";
        }

        private void ClearStorageBuffer(CommandBuffer commandBuffer, BufferHandle handle, ulong size)
        {
            if (!handle.IsValid || size == 0)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _context.Api.CmdFillBuffer(commandBuffer, buffer, 0, size, 0u);
            InsertTransferToShaderBarrier(commandBuffer, buffer, size);
        }

        private void ClearStorageBufferRange(CommandBuffer commandBuffer, BufferHandle handle, ulong offset, ulong size)
        {
            if (!handle.IsValid || size == 0)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _context.Api.CmdFillBuffer(commandBuffer, buffer, offset, size, 0u);
            InsertTransferToShaderBarrier(commandBuffer, buffer, offset, size);
        }

        private void UploadInitializedVisibilityAtlas(StagingRing stagingRing, CommandBuffer commandBuffer, BufferHandle destination)
        {
            ulong byteCount = GlobalIlluminationProbeVolumeData.EstimateVisibilityAtlasBytes(_activeProbeCount);
            if (byteCount == 0 || !destination.IsValid)
                return;

            GpuBufferUploader.UploadBytesToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                destination,
                byteCount,
                WriteVisibilityAtlasInitializationPayload,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit));
        }

        private void UploadInitializedVisibilityAtlasRange(
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle destination,
            int startProbeIndex,
            int probeCount)
        {
            if (probeCount <= 0 || !destination.IsValid)
                return;

            _visibilityInitializationStartProbe = startProbeIndex;
            _visibilityInitializationProbeCount = probeCount;
            ulong byteCount = checked((ulong)probeCount * GlobalIlluminationProbeVolumeData.VisibilityBytesPerProbe);
            ulong destinationOffset = checked((ulong)startProbeIndex * GlobalIlluminationProbeVolumeData.VisibilityBytesPerProbe);
            GpuBufferUploader.UploadBytesToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                destination,
                byteCount,
                WriteVisibilityAtlasRangeInitializationPayload,
                destinationOffset,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                    destinationOffset,
                    byteCount));
        }

        private void WriteVisibilityAtlasInitializationPayload(void* destination, ulong byteCount)
        {
            Span<byte> bytes = new(destination, checked((int)byteCount));
            CreateVisibilityAtlasInitializationPayload(
                _volumeScratch.AsSpan(0, _volumeCount),
                _activeProbeCount,
                GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe,
                bytes);
        }

        private void WriteVisibilityAtlasRangeInitializationPayload(void* destination, ulong byteCount)
        {
            Span<byte> bytes = new(destination, checked((int)byteCount));
            CreateVisibilityAtlasRangeInitializationPayload(
                _volumeScratch.AsSpan(0, _volumeCount),
                _activeProbeCount,
                _visibilityInitializationStartProbe,
                _visibilityInitializationProbeCount,
                GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe,
                bytes);
        }

        private void InsertTransferToShaderBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong size)
        {
            InsertTransferToShaderBarrier(commandBuffer, buffer, 0, size);
        }

        private void InsertTransferToShaderBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong offset, ulong size)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = offset,
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

        private void InsertShaderToTransferReadBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong offset, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                offset,
                size,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);
        }

        private void InsertShaderReadToTransferWriteBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong offset, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                offset,
                size,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit);
        }

        private void InsertTransferReadToShaderBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong offset, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                offset,
                size,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }

        private void InsertTransferWriteToShaderReadBarrier(CommandBuffer commandBuffer, VkBuffer buffer, ulong offset, ulong size)
        {
            InsertBufferBarrier(
                commandBuffer,
                buffer,
                offset,
                size,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit);
        }

        private void InsertBufferBarrier(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            ulong offset,
            ulong size,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = sourceStage,
                SrcAccessMask = sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = offset,
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

        internal static void CreateVisibilityAtlasInitializationPayload(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            uint visibilityTexelsPerProbe,
            Span<byte> destination)
        {
            if (activeProbeCount <= 0 || visibilityTexelsPerProbe == 0)
                return;

            int texelCount = checked((int)(visibilityTexelsPerProbe * visibilityTexelsPerProbe));
            int expectedBytes = checked(activeProbeCount * texelCount * sizeof(uint));
            if (destination.Length < expectedBytes)
                throw new ArgumentException("Destination is too small for the DDGI visibility atlas initialization payload.", nameof(destination));

            Span<uint> words = MemoryMarshal.Cast<byte, uint>(destination[..expectedBytes]);
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                int firstProbe = Math.Clamp((int)MathF.Round(volume.OriginAndFirstProbeIndex.W), 0, activeProbeCount);
                int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
                int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
                int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
                int probeCount = Math.Min(checked(countX * countY * countZ), activeProbeCount - firstProbe);
                if (probeCount <= 0)
                    continue;

                float maxDistance = MathF.Max(volume.BiasAndProbeCountZ.Z > 0.0f ? volume.BiasAndProbeCountZ.Z : 16.0f, 0.1f);
                uint packedMoments = PackHalf2(maxDistance, maxDistance * maxDistance);
                int probeEnd = firstProbe + probeCount;
                for (int probeIndex = firstProbe; probeIndex < probeEnd; probeIndex++)
                {
                    int baseWord = probeIndex * texelCount;
                    words.Slice(baseWord, texelCount).Fill(packedMoments);
                }
            }
        }

        internal static void CreateVisibilityAtlasRangeInitializationPayload(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            int startProbeIndex,
            int probeCount,
            uint visibilityTexelsPerProbe,
            Span<byte> destination)
        {
            if (activeProbeCount <= 0 || probeCount <= 0 || visibilityTexelsPerProbe == 0)
                return;
            if (startProbeIndex < 0 || startProbeIndex >= activeProbeCount)
                throw new ArgumentOutOfRangeException(nameof(startProbeIndex));

            int clampedProbeCount = Math.Min(probeCount, activeProbeCount - startProbeIndex);
            int texelCount = checked((int)(visibilityTexelsPerProbe * visibilityTexelsPerProbe));
            int expectedBytes = checked(clampedProbeCount * texelCount * sizeof(uint));
            if (destination.Length < expectedBytes)
                throw new ArgumentException("Destination is too small for the DDGI visibility atlas range initialization payload.", nameof(destination));

            Span<uint> words = MemoryMarshal.Cast<byte, uint>(destination[..expectedBytes]);
            for (int localProbe = 0; localProbe < clampedProbeCount; localProbe++)
            {
                int probeIndex = startProbeIndex + localProbe;
                float maxDistance = ResolveMaxRayDistanceForProbe(volumes, activeProbeCount, probeIndex);
                uint packedMoments = PackHalf2(maxDistance, maxDistance * maxDistance);
                words.Slice(localProbe * texelCount, texelCount).Fill(packedMoments);
            }
        }

        internal static ulong CreateResourceSignature(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int probeCount,
            int activeProbeCount,
            int raysPerProbe,
            int maxProbeUpdatesPerFrame,
            uint probeUpdateModeFlags = 0u)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, ResourceSignatureLayoutVersion);
            hash = HashAdd(hash, volumes.Length);
            hash = HashAdd(hash, probeCount);
            hash = HashAdd(hash, activeProbeCount);
            hash = HashAdd(hash, raysPerProbe);
            hash = HashAdd(hash, probeUpdateModeFlags);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.IrradianceTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rgba16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rg16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeStateStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride);
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                hash = HashAdd(hash, i);
                hash = HashAdd(hash, volume.OriginAndFirstProbeIndex.W);
                hash = HashAdd(hash, volume.ClipmapGridMinAndKind.W);
                hash = HashAdd(hash, volume.ClipmapRingOffsetAndCascade.W);
                hash = HashAdd(hash, volume.SizeAndProbeCountX.W);
                hash = HashAdd(hash, volume.ProbeSpacingAndProbeCountY);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.X);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Y);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Z);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.W);
                hash = HashAdd(hash, volume.RayAndUpdateParams.X);
                hash = HashAdd(hash, volume.RayAndUpdateParams.Z);
                hash = HashAdd(hash, volume.RayAndUpdateParams.W);
                hash = HashAdd(hash, volume.DebugColorAndFlags.W);
            }

            return hash;
        }

        internal static ulong CreateCacheCompatibilitySignature(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            uint probeUpdateModeFlags = 0u)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, ResourceSignatureLayoutVersion);
            hash = HashAdd(hash, probeUpdateModeFlags);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.IrradianceTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rgba16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.Rg16FloatBytesPerTexel);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeStateStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeUpdateRequestStride);
            hash = HashAdd(hash, GlobalIlluminationProbeVolumeData.ProbeRelocationClassificationStride);

            int cameraVolumeCount = 0;
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                var kind = (DdgiProbeVolumeKind)Math.Max(0, (int)volume.ClipmapGridMinAndKind.W);
                if (kind != DdgiProbeVolumeKind.CameraClipmap)
                    continue;

                cameraVolumeCount++;
                hash = HashAdd(hash, kind.GetHashCode());
                hash = HashAdd(hash, volume.ClipmapRingOffsetAndCascade.W);
                hash = HashAdd(hash, volume.SizeAndProbeCountX.W);
                hash = HashAdd(hash, volume.ProbeSpacingAndProbeCountY);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.X);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Y);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.Z);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ.W);
                hash = HashAdd(hash, volume.RayAndUpdateParams.X);
                hash = HashAdd(hash, volume.RayAndUpdateParams.Z);
                hash = HashAdd(hash, volume.RayAndUpdateParams.W);
            }

            return HashAdd(hash, cameraVolumeCount);
        }

        internal static ulong CreateAuthoredLayoutSignature(ReadOnlySpan<GPUDdgiProbeVolume> volumes)
        {
            ulong hash = HashStart;
            int authoredVolumeCount = 0;
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                var kind = (DdgiProbeVolumeKind)Math.Max(0, (int)volume.ClipmapGridMinAndKind.W);
                if (kind != DdgiProbeVolumeKind.Authored)
                    continue;

                authoredVolumeCount++;
                hash = HashAdd(hash, i);
                hash = HashAdd(hash, volume.OriginAndFirstProbeIndex);
                hash = HashAdd(hash, volume.SizeAndProbeCountX);
                hash = HashAdd(hash, volume.ProbeSpacingAndProbeCountY);
                hash = HashAdd(hash, volume.BiasAndProbeCountZ);
                hash = HashAdd(hash, volume.RayAndUpdateParams.X);
                hash = HashAdd(hash, volume.RayAndUpdateParams.Z);
                hash = HashAdd(hash, volume.RayAndUpdateParams.W);
            }

            return HashAdd(hash, authoredVolumeCount);
        }

        private static int FindFirstProbeIndex(ReadOnlySpan<GPUDdgiProbeVolume> volumes, DdgiProbeVolumeKind kind)
        {
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                if ((DdgiProbeVolumeKind)Math.Max(0, (int)volume.ClipmapGridMinAndKind.W) != kind)
                    continue;

                return Math.Max(0, (int)MathF.Round(volume.OriginAndFirstProbeIndex.W));
            }

            return -1;
        }

        private static int CalculateProbeCountForKind(ReadOnlySpan<GPUDdgiProbeVolume> volumes, DdgiProbeVolumeKind kind)
        {
            int probeCount = 0;
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                if ((DdgiProbeVolumeKind)Math.Max(0, (int)volume.ClipmapGridMinAndKind.W) != kind)
                    continue;

                probeCount = checked(probeCount + CalculateVolumeProbeCount(volume, int.MaxValue));
            }

            return probeCount;
        }

        private static float ResolveMaxRayDistanceForProbe(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            int probeIndex)
        {
            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume volume = volumes[i];
                int firstProbe = Math.Clamp((int)MathF.Round(volume.OriginAndFirstProbeIndex.W), 0, activeProbeCount);
                int probeCount = CalculateVolumeProbeCount(volume, activeProbeCount - firstProbe);
                if (probeIndex < firstProbe || probeIndex >= firstProbe + probeCount)
                    continue;

                return MathF.Max(volume.BiasAndProbeCountZ.Z > 0.0f ? volume.BiasAndProbeCountZ.Z : 16.0f, 0.1f);
            }

            return 16.0f;
        }

        private static int CalculateVolumeProbeCount(GPUDdgiProbeVolume volume, int maxCount)
        {
            int countX = Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));
            int countY = Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));
            int countZ = Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));
            int probeCount = checked(countX * countY * countZ);
            return Math.Min(probeCount, Math.Max(0, maxCount));
        }

        private static bool IsCameraRelative(in GPUDdgiProbeVolume volume) =>
            (uint)MathF.Round(volume.ClipmapGridMinAndKind.W) == (uint)DdgiProbeVolumeKind.CameraClipmap;

        private static Vector3 Origin(in GPUDdgiProbeVolume volume) =>
            new(volume.OriginAndFirstProbeIndex.X, volume.OriginAndFirstProbeIndex.Y, volume.OriginAndFirstProbeIndex.Z);

        private static Vector3 Spacing(in GPUDdgiProbeVolume volume) =>
            new(volume.ProbeSpacingAndProbeCountY.X, volume.ProbeSpacingAndProbeCountY.Y, volume.ProbeSpacingAndProbeCountY.Z);

        private static int FirstProbeIndex(in GPUDdgiProbeVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.OriginAndFirstProbeIndex.W));

        private static int CountX(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));

        private static int CountY(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));

        private static int CountZ(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));

        private static DdgiClipmapCell GridMinCell(in GPUDdgiProbeVolume volume) =>
            new(
                (int)MathF.Round(volume.ClipmapGridMinAndKind.X),
                (int)MathF.Round(volume.ClipmapGridMinAndKind.Y),
                (int)MathF.Round(volume.ClipmapGridMinAndKind.Z));

        private static DdgiClipmapCell RingOffsetCell(in GPUDdgiProbeVolume volume) =>
            new(
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.X),
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Y),
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Z));

        private static int CascadeIndex(in GPUDdgiProbeVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.W));

        private static uint CreateProbeUpdateModeSignature(GlobalIlluminationSettings settings)
        {
            uint flags = 0u;
            if (settings.DdgiProbeRelocationEnabled)
                flags |= GlobalIlluminationProbeVolumeData.ProbeRelocationEnabledFlag;
            if (settings.DdgiProbeClassificationEnabled)
                flags |= GlobalIlluminationProbeVolumeData.ProbeClassificationEnabledFlag;
            return flags;
        }

        private static uint PackHalf2(float x, float y)
        {
            uint hx = BitConverter.HalfToUInt16Bits((Half)x);
            uint hy = BitConverter.HalfToUInt16Bits((Half)y);
            return hx | (hy << 16);
        }

        private static int ResolveVolumeRaysPerProbe(GPUDdgiProbeVolume volume)
        {
            return Math.Clamp(
                (int)MathF.Round(volume.RayAndUpdateParams.X),
                0,
                GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe);
        }

        private static int ResolveVolumeMaxProbeUpdatesPerFrame(GPUDdgiProbeVolume volume)
        {
            return Math.Max(0, (int)MathF.Round(volume.RayAndUpdateParams.Y));
        }

        private static ulong HashAdd(ulong hash, Vector4 value)
        {
            hash = HashAdd(hash, value.X);
            hash = HashAdd(hash, value.Y);
            hash = HashAdd(hash, value.Z);
            return HashAdd(hash, value.W);
        }

        private static ulong HashAdd(ulong hash, int value) => HashAdd(hash, unchecked((uint)value));

        private static ulong HashAdd(ulong hash, ulong value)
        {
            hash = HashAdd(hash, unchecked((uint)value));
            return HashAdd(hash, unchecked((uint)(value >> 32)));
        }

        private static ulong HashAdd(ulong hash, float value) => HashAdd(hash, BitConverter.SingleToUInt32Bits(value));

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 8) & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 16) & 0xff;
            hash *= HashPrime;
            hash ^= (value >> 24) & 0xff;
            hash *= HashPrime;
            return hash;
        }

        private void RegisterIfValid(int bindlessIndex, BufferHandle handle, ulong size)
        {
            if (!handle.IsValid || _registeredBindlessHeap == null)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(handle),
                0,
                size);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            for (int i = 0; i < _schedulerCounterReadbackBuffers.Length; i++)
            {
                if (_schedulerCounterReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_schedulerCounterReadbackBuffers[i]);
                if (_schedulerQueueReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_schedulerQueueReadbackBuffers[i]);
            }
            if (_traceIndirectDispatchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_traceIndirectDispatchBuffer);
            if (_schedulerCounterBuffer.IsValid)
                _bufferManager.DestroyBuffer(_schedulerCounterBuffer);
            if (_schedulerPrefixBuffer.IsValid)
                _bufferManager.DestroyBuffer(_schedulerPrefixBuffer);
            if (_schedulerGroupCountBuffer.IsValid)
                _bufferManager.DestroyBuffer(_schedulerGroupCountBuffer);
            if (_probeCandidateBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeCandidateBuffer);
            if (_dirtyRegionBuffer.IsValid)
                _bufferManager.DestroyBuffer(_dirtyRegionBuffer);
            if (_schedulerConstantsBuffer.IsValid)
                _bufferManager.DestroyBuffer(_schedulerConstantsBuffer);
            if (_rayResultScratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_rayResultScratchBuffer);
            if (_visibilityAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_visibilityAtlasBuffer);
            if (_irradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_irradianceAtlasBuffer);
            if (_probeRelocationClassificationBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeRelocationClassificationBuffer);
            if (_probeUpdateQueueBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeUpdateQueueBuffer);
            if (_probeStateBuffer.IsValid)
                _bufferManager.DestroyBuffer(_probeStateBuffer);
            if (_volumeMetadataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_volumeMetadataBuffer);
            DrainRetiredResources(force: true);
        }

        private readonly record struct RetiredBufferResource(
            BufferHandle Buffer,
            ulong RetireAfterFrameSerial);
    }

    internal readonly record struct DdgiGpuSchedulerResourceLayout(
        int DirtyRegionCapacity,
        int CandidateCapacity,
        int GroupCountCapacity,
        int PrefixCapacity,
        int WorkgroupCount,
        int PriorityBucketCount,
        ulong DirtyRegionBufferSize,
        ulong CandidateBufferSize,
        ulong GroupCountBufferSize,
        ulong PrefixBufferSize);

    internal readonly record struct DdgiGpuSchedulerDirtyRegionUploadPlan(
        int UploadCount,
        int OverflowCount);

    internal readonly record struct DdgiWarmupFractions(
        float Visible,
        float Local,
        float Cascade0);

    internal readonly record struct DdgiWarmupBudgetSplit(
        int Local,
        int Cascade0,
        int NewCell,
        int Safety);

    public readonly record struct DdgiGpuSchedulerValidationSnapshot(
        int Valid,
        string Status,
        int CpuRequestCount,
        uint GpuRequestCount,
        int ComparedRequestCount,
        int MismatchCount,
        int SampleLimit,
        string FirstMismatch)
    {
        public static DdgiGpuSchedulerValidationSnapshot Invalid { get; } = new(
            0,
            string.Empty,
            0,
            0u,
            0,
            0,
            0,
            string.Empty);
    }

    internal readonly record struct ValidationExpectedFrame(
        bool Valid,
        int CpuRequestCount,
        ulong CpuPrimaryRayCount,
        int RequestBudget,
        int PrimaryRayBudget,
        int ActiveProbeCount,
        int VolumeCount)
    {
        public static ValidationExpectedFrame Invalid { get; } = new(false, 0, 0UL, 0, 0, 0, 0);
    }
}
