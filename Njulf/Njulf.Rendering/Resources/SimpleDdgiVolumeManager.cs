using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

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
        private const ulong RelocationClassificationStride = 48;
        private static readonly ulong AtlasTexelStride = 8;
        private const uint ProbeStateFreshFlag = 1u << 0;
        private const uint ProbeStateScrollExposedFlag = 1u << 1;
        private const uint ProbeStateInactiveFlag = 1u << 2;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly List<VolumeCandidate> _volumeCandidates = new(GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount + 3);
        private readonly GPUSimpleDdgiVolume[] _volumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiVolume[] _previousVolumeScratch = new GPUSimpleDdgiVolume[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly GPUSimpleDdgiProbeUpdate[] _updateQueueScratch = new GPUSimpleDdgiProbeUpdate[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private readonly GPUSimpleDdgiProbeState[] _probeStateScratch = new GPUSimpleDdgiProbeState[GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount];
        private byte[] _probeFresh = Array.Empty<byte>();
        private byte[] _probeInactive = Array.Empty<byte>();
        private uint[] _probeAges = Array.Empty<uint>();
        private int[] _volumeRoundRobinCursors = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
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
        private ulong _irradianceAtlasBytes;
        private ulong _visibilityAtlasBytes;
        private ulong _rayScratchBytes;
        private ulong _probeStateBytes;
        private ulong _probeUpdateQueueBytes;
        private ulong _relocationClassificationBytes;
        private ulong _copyTempBytes;
        private BindlessHeap? _registeredBindlessHeap;
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
        private int _ageRefreshProbeCount;
        private int _fullRefreshProbeCount;
        private int _scrollCopyCount;
        private string _lastBudgetWarning = string.Empty;
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
        public ulong BufferBytes => ParamsBufferSize +
            _irradianceAtlasBytes +
            _visibilityAtlasBytes +
            _rayScratchBytes +
            _probeStateBytes +
            _probeUpdateQueueBytes +
            _relocationClassificationBytes +
            _copyTempBytes;
        public ulong IrradianceAtlasBytes => _irradianceAtlasBytes;
        public ulong VisibilityAtlasBytes => _visibilityAtlasBytes;
        public ulong AtlasBytes => _irradianceAtlasBytes + _visibilityAtlasBytes;
        public ulong RayScratchBytes => _rayScratchBytes;
        public ulong ProbeStateBytes => _probeStateBytes;
        public ulong ProbeUpdateQueueBytes => _probeUpdateQueueBytes;
        public ulong RelocationClassificationBytes => _relocationClassificationBytes;
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
        public int AgeRefreshProbeCount => _ageRefreshProbeCount;
        public int FullRefreshProbeCount => _fullRefreshProbeCount;
        public int ScrollCopyCount => _scrollCopyCount;
        public GPUSimpleDdgiParams LastParams => _lastParams;
        public ReadOnlySpan<GPUSimpleDdgiVolume> LastVolumes => new(_volumeScratch, 0, _volumeCount);

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
                    RaysPerProbe: _raysPerProbe,
                    MaxProbeUpdatesPerFrame: _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame <= 0
                        ? probeCount
                        : Math.Min(probeCount, _settings.GlobalIllumination.SimpleDdgiProbeUpdatesPerFrame),
                    ScheduledProbeUpdates: scheduledUpdates,
                    ScheduledPrimaryRayCount: checked((ulong)Math.Max(0, scheduledUpdates) * (ulong)Math.Max(1, _raysPerProbe)),
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
        }

        public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

            ResetFrameCounters();

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            bool enabled = gi.EffectiveUseSimpleDdgi;
            if (!enabled)
            {
                _volumeCount = 0;
                _probeCount = 0;
                _probesToUpdate = 0;
                _hasGridOrigin = false;
                Array.Fill(_ringHasOrigins, false);
                _atlasClearRequired = true;
                _atlasFresh = true;
                Array.Clear(_volumeScratch);
                _lastParams = CreateDisabledParams(gi);
                UploadParams(stagingRing, commandBuffer);
                _frameIndex++;
                return;
            }

            BoundingBox sceneBounds = ExpandBounds(DdgiFrameLayoutBuilder.EstimateSceneProbeBounds(scene), gi.SimpleDdgiRingBaseSpacing * 1.5f);
            CapturePreviousVolumes();
            BuildVolumeTable(gi, sceneBounds, cameraPosition);
            EnsureCpuProbeStateCapacity(_probeCount);

            _raysPerProbe = Math.Clamp(gi.SimpleDdgiRaysPerProbe, 1, GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
            if (_recenteredThisFrame)
            {
                _totalRecenterCount++;
                _framesSinceLastRecenter = 0;
            }

            MarkFreshForNewOrScrolledProbes();
            int updateBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(_probeCount, gi.SimpleDdgiProbeUpdatesPerFrame);
            _probesToUpdate = BuildUpdateQueue(updateBudget);
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
            EnsureCapacity(_probeCount, _raysPerProbe, _probesToUpdate);
            PreserveScrolledAtlasData(commandBuffer);
            ClearAtlasBuffersIfRequired(commandBuffer);

            float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f;
            float hysteresis = _atlasFresh ? 0.0f : gi.SimpleDdgiHysteresis;
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
                HysteresisFrameAndFlags = new Vector4(hysteresis, _frameIndex, BuildFlags(gi, enabled), gi.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = new Vector4(0.0f, 0.0f, 0.0f, environmentIntensity),
                ProbeUpdateRange = new Vector4(_updateStartProbe, _probesToUpdate, _volumeCount, _probeCount),
                DebugAndBias = new Vector4((float)gi.DebugView, gi.DdgiSelfShadowBiasScale, gi.IndirectIntensity, gi.FarFieldMaxTraceSteps),
                RotationQuaternion = BuildFrameRotation(_frameIndex),
                BiasAndPadding = new Vector4(gi.SimpleDdgiNormalBias, gi.SimpleDdgiViewBias, 0.0f, 0.0f),
                Reserved0 = new Vector4(_volumeCount, _probeCount, GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount, 0.0f)
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

        public void MarkBlendExecuted()
        {
            if (_probesToUpdate > 0)
            {
                for (int i = 0; i < _probesToUpdate; i++)
                {
                    int probeIndex = checked((int)_updateQueueScratch[i].ProbeIndex);
                    if ((uint)probeIndex < (uint)_probeFresh.Length)
                    {
                        _probeFresh[probeIndex] = 0;
                        _probeAges[probeIndex] = 0;
                    }
                }

                _atlasFresh = false;
            }
        }

        private void ResetFrameCounters()
        {
            _recenteredThisFrame = false;
            _atlasPreservedOnRecenterThisFrame = false;
            _atlasClearedThisFrame = false;
            _newlyInvalidatedProbeCount = 0;
            _recenterRefreshProbeCount = 0;
            _ageRefreshProbeCount = 0;
            _fullRefreshProbeCount = 0;
            _scrollCopyCount = 0;
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
            uint[] previousAges = _probeAges;
            _probeFresh = new byte[Math.Max(0, probeCount)];
            _probeInactive = new byte[Math.Max(0, probeCount)];
            _probeAges = new uint[Math.Max(0, probeCount)];
            int copyCount = Math.Min(probeCount, previousFresh.Length);
            Array.Copy(previousFresh, _probeFresh, copyCount);
            Array.Copy(previousInactive, _probeInactive, copyCount);
            Array.Copy(previousAges, _probeAges, copyCount);
            if (probeCount > copyCount)
            {
                Array.Fill(_probeFresh, (byte)1, copyCount, probeCount - copyCount);
                _newlyInvalidatedProbeCount += probeCount - copyCount;
            }
        }

        private void MarkFreshForNewOrScrolledProbes()
        {
            if (_probeCount <= 0)
                return;

            if (_atlasFresh || _previousVolumeCount == 0)
            {
                Array.Fill(_probeFresh, (byte)1, 0, _probeCount);
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

                if (!TryResolveCellDelta(previous, current, out int deltaX, out int deltaY, out int deltaZ) ||
                    (deltaX == 0 && deltaY == 0 && deltaZ == 0))
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

                    MarkProbeFresh(FirstProbe(current) + x + y * countX + z * countX * countY, scrollExposed: true);
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

            int count = 0;
            for (int probeIndex = 0; probeIndex < _probeCount && count < capacity; probeIndex++)
            {
                if (_probeFresh[probeIndex] != 0)
                    AddProbeUpdate(ref count, capacity, probeIndex, ProbeStateFreshFlag);
            }

            int[] quotas = ResolveVolumeQuotas(Math.Max(0, capacity - count));
            for (int volumeIndex = 0; volumeIndex < _volumeCount && count < capacity; volumeIndex++)
            {
                int quota = quotas[volumeIndex];
                if (quota <= 0)
                    continue;

                GPUSimpleDdgiVolume volume = _volumeScratch[volumeIndex];
                int firstProbe = FirstProbe(volume);
                int probeCount = VolumeProbeCount(volume);
                int cursor = Math.Clamp(_volumeRoundRobinCursors[volumeIndex], 0, Math.Max(probeCount - 1, 0));
                int visited = 0;
                while (visited < probeCount && quota > 0 && count < capacity)
                {
                    int local = (cursor + visited) % probeCount;
                    int probeIndex = firstProbe + local;
                    visited++;
                    if ((uint)probeIndex >= (uint)_probeCount || _probeFresh[probeIndex] != 0)
                        continue;
                    if (_probeInactive[probeIndex] != 0 && _probeAges[probeIndex] < 240u)
                        continue;

                    AddProbeUpdate(ref count, capacity, probeIndex, _probeInactive[probeIndex] != 0 ? ProbeStateInactiveFlag : 0u);
                    quota--;
                }

                _volumeRoundRobinCursors[volumeIndex] = probeCount > 0 ? (cursor + visited) % probeCount : 0;
            }

            return count;
        }

        private int[] ResolveVolumeQuotas(int remainingBudget)
        {
            int[] quotas = new int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
            if (remainingBudget <= 0 || _volumeCount == 0)
                return quotas;

            int authoredCount = 0;
            int ring0 = -1;
            for (int i = 0; i < _volumeCount; i++)
            {
                uint kind = Kind(_volumeScratch[i]);
                if (kind == VolumeKindAuthored)
                    authoredCount++;
                else if (kind == VolumeKindRing && ring0 < 0)
                    ring0 = i;
            }

            int consumed = 0;
            if (authoredCount > 0)
            {
                int authoredBudget = Math.Max(1, remainingBudget / 2);
                for (int i = 0; i < _volumeCount; i++)
                {
                    if (Kind(_volumeScratch[i]) != VolumeKindAuthored)
                        continue;
                    quotas[i] = Math.Max(1, authoredBudget / authoredCount);
                    consumed += quotas[i];
                }
            }

            if (ring0 >= 0 && consumed < remainingBudget)
            {
                int ringBudget = Math.Max(1, (remainingBudget - consumed) / 2);
                quotas[ring0] += ringBudget;
                consumed += ringBudget;
            }

            int unassigned = 0;
            for (int i = 0; i < _volumeCount; i++)
            {
                if (quotas[i] == 0)
                    unassigned++;
            }

            int remainder = Math.Max(0, remainingBudget - consumed);
            for (int i = 0; i < _volumeCount && remainder > 0; i++)
            {
                if (quotas[i] != 0)
                    continue;
                int share = Math.Max(1, remainder / Math.Max(unassigned, 1));
                quotas[i] = share;
                remainder -= share;
                unassigned--;
            }

            if (remainder > 0)
                quotas[0] += remainder;
            return quotas;
        }

        private void AddProbeUpdate(ref int count, int capacity, int probeIndex, uint flags)
        {
            if (count >= capacity || (uint)probeIndex >= (uint)_probeCount)
                return;

            _updateQueueScratch[count++] = new GPUSimpleDdgiProbeUpdate
            {
                ProbeIndex = checked((uint)probeIndex),
                VolumeIndex = checked((uint)Math.Max(0, ResolveVolumeIndexForProbe(probeIndex))),
                Flags = flags | (_probeFresh[probeIndex] != 0 ? ProbeStateFreshFlag : 0u)
            };
        }

        private void MarkVolumeFresh(GPUSimpleDdgiVolume volume)
        {
            int firstProbe = FirstProbe(volume);
            int count = VolumeProbeCount(volume);
            for (int i = 0; i < count; i++)
                MarkProbeFresh(firstProbe + i, scrollExposed: false);
        }

        private void MarkProbeFresh(int probeIndex, bool scrollExposed)
        {
            if ((uint)probeIndex >= (uint)_probeFresh.Length)
                return;

            if (_probeFresh[probeIndex] == 0)
                _newlyInvalidatedProbeCount++;

            _probeFresh[probeIndex] = 1;
            _probeInactive[probeIndex] = 0;
            if (scrollExposed)
                _recenterRefreshProbeCount++;
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
            if ((uint)volumeIndex >= (uint)_previousVolumeCount)
                return false;

            previous = _previousVolumeScratch[volumeIndex];
            return Kind(previous) == Kind(current) &&
                CountX(previous) == CountX(current) &&
                CountY(previous) == CountY(current) &&
                CountZ(previous) == CountZ(current) &&
                FirstProbe(previous) == FirstProbe(current) &&
                NearlyEqual(Spacing(previous), Spacing(current), 0.0001f);
        }

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
                min,
                max,
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
            Vector3 origin = ResolveSceneClampedOrigin(
                sceneBounds.Min,
                sceneBounds.Max,
                latticeSize,
                spacing,
                cameraPosition,
                _ringOrigins[ringIndex],
                ref _ringHasOrigins[ringIndex],
                out bool recentered);
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
                uint flags = 0u;
                if (_probeFresh[i] != 0)
                    flags |= ProbeStateFreshFlag;
                if (_probeInactive[i] != 0)
                    flags |= ProbeStateInactiveFlag;

                _probeStateScratch[i] = new GPUSimpleDdgiProbeState
                {
                    RelocationAndActive = new Vector4(0.0f, 0.0f, 0.0f, _probeInactive[i] == 0 ? 1.0f : 0.0f),
                    Flags = flags,
                    Age = _probeAges[i],
                    Classification = _probeInactive[i] == 0 ? 0u : 1u,
                    Reserved0 = 0u
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

        private void EnsureCapacity(int probeCount, int raysPerProbe, int probesToUpdate)
        {
            ulong irradianceBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride));
            ulong visibilityBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * VisibilityTexelsPerProbe * VisibilityTexelsPerProbe * AtlasTexelStride));
            ulong rayBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probesToUpdate) * (ulong)Math.Max(1, raysPerProbe) * RayResultStride));
            ulong probeStateBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * ProbeStateStride));
            ulong updateQueueBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probesToUpdate) * ProbeUpdateStride));
            ulong relocationClassificationBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * RelocationClassificationStride));
            ulong copyTempBytes = Math.Max(irradianceBytes, visibilityBytes);

            EnsureBuffer(ref _irradianceAtlasBuffer, ref _irradianceAtlasBytes, irradianceBytes, "Simple DDGI Irradiance Atlas", invalidateAtlas: true);
            EnsureBuffer(ref _visibilityAtlasBuffer, ref _visibilityAtlasBytes, visibilityBytes, "Simple DDGI Visibility Atlas", invalidateAtlas: true);
            EnsureBuffer(ref _rayResultScratchBuffer, ref _rayScratchBytes, rayBytes, "Simple DDGI Ray Scratch", invalidateAtlas: false);
            if (EnsureBuffer(ref _probeStateBuffer, ref _probeStateBytes, probeStateBytes, "Simple DDGI Probe State", invalidateAtlas: false))
                _probeStateUploadRequired = true;
            EnsureBuffer(ref _probeUpdateQueueBuffer, ref _probeUpdateQueueBytes, updateQueueBytes, "Simple DDGI Probe Update Queue", invalidateAtlas: false);
            EnsureBuffer(ref _relocationClassificationBuffer, ref _relocationClassificationBytes, relocationClassificationBytes, "Simple DDGI Relocation Classification", invalidateAtlas: false);
            EnsureTransferBuffer(ref _copyTempBuffer, ref _copyTempBytes, copyTempBytes, "Simple DDGI Scroll Copy Temp");
        }

        private bool EnsureBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName, bool invalidateAtlas)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return false;

            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
            if (invalidateAtlas)
            {
                _atlasClearRequired = true;
                _atlasFresh = true;
            }

            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
            return true;
        }

        private void EnsureTransferBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return;

            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
        }

        private unsafe void PreserveScrolledAtlasData(CommandBuffer commandBuffer)
        {
            if (!_recenteredThisFrame || _atlasClearRequired || !_copyTempBuffer.IsValid)
                return;

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
            if (!atlasHandle.IsValid || !_copyTempBuffer.IsValid || atlasBytes == 0 || bytesPerProbe == 0)
                return false;

            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            List<(int OldLocal, int NewLocal, int Count)> runs = BuildScrollCopyRunsForTest(countX, countY, countZ, deltaX, deltaY, deltaZ);
            if (runs.Count == 0)
                return false;

            Silk.NET.Vulkan.Buffer atlas = _bufferManager.GetBuffer(atlasHandle);
            Silk.NET.Vulkan.Buffer temp = _bufferManager.GetBuffer(_copyTempBuffer);
            BufferCopy fullCopy = new() { SrcOffset = 0, DstOffset = 0, Size = atlasBytes };
            _context.Api.CmdCopyBuffer(commandBuffer, atlas, temp, 1, &fullCopy);
            InsertTransferBarrier(commandBuffer, temp, atlasBytes, AccessFlags2.TransferWriteBit, AccessFlags2.TransferReadBit);

            int firstProbe = FirstProbe(volume);
            bool copiedAny = false;
            foreach ((int oldLocal, int newLocal, int runCount) in runs)
            {
                ulong srcOffset = checked((ulong)(firstProbe + oldLocal) * bytesPerProbe);
                ulong dstOffset = checked((ulong)(firstProbe + newLocal) * bytesPerProbe);
                ulong size = checked((ulong)runCount * bytesPerProbe);
                if (srcOffset + size > atlasBytes || dstOffset + size > atlasBytes)
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

            if (ApproximatelyEqual(currentOrigin, desiredOrigin) ||
                !ShouldRecenter(cameraPosition, currentOrigin, latticeSize, sceneMin, sceneMax))
            {
                recentered = false;
                return currentOrigin;
            }

            recentered = !ApproximatelyEqual(currentOrigin, desiredOrigin);
            return desiredOrigin;
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

        private static bool ShouldRecenter(Vector3 cameraPosition, Vector3 currentOrigin, Vector3 latticeSize, Vector3 sceneMin, Vector3 sceneMax)
        {
            Vector3 quarter = latticeSize * 0.25f;
            Vector3 innerMin = currentOrigin + quarter;
            Vector3 innerMax = currentOrigin + latticeSize - quarter;
            return
                ShouldRecenterAxis(cameraPosition.X, innerMin.X, innerMax.X, latticeSize.X, sceneMin.X, sceneMax.X) ||
                ShouldRecenterAxis(cameraPosition.Y, innerMin.Y, innerMax.Y, latticeSize.Y, sceneMin.Y, sceneMax.Y) ||
                ShouldRecenterAxis(cameraPosition.Z, innerMin.Z, innerMax.Z, latticeSize.Z, sceneMin.Z, sceneMax.Z);
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

        private static uint Kind(GPUSimpleDdgiVolume volume) =>
            (uint)Math.Max(0, (int)MathF.Round(volume.WorldMaxAndKind.W));

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
                BiasAndPadding = new Vector4(settings.SimpleDdgiNormalBias, settings.SimpleDdgiViewBias, 0.0f, 0.0f),
                Reserved0 = Vector4.Zero
            };
        }

        private static uint BuildFlags(GlobalIlluminationSettings settings, bool enabled)
        {
            uint flags = enabled ? 1u : 0u;
            if (settings.FarFieldClipmapEnabled)
                flags |= 1u << 1;
            if (settings.FarFieldForceAll)
                flags |= 1u << 2;
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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
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
        }

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
                    RaysAndReserved = Vector4.Zero
                };
            }
        }
    }
}
