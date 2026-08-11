using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public enum LightType : int
    {
        Point = 0,
        Directional = 1,
        Spot = 2
    }

    /// <summary>
    /// A stable, generation-checked reference to a light owned by <see cref="LightManager"/>.
    /// Packed GPU indices are intentionally not exposed through this type.
    /// </summary>
    public readonly struct LightHandle : IEquatable<LightHandle>
    {
        internal LightHandle(int slot, int generation)
        {
            Slot = slot;
            Generation = generation;
        }

        public int Slot { get; }
        public int Generation { get; }
        public bool IsValid => Slot >= 0 && Generation > 0;

        public bool Equals(LightHandle other) => Slot == other.Slot && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is LightHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Slot, Generation);
        public static bool operator ==(LightHandle left, LightHandle right) => left.Equals(right);
        public static bool operator !=(LightHandle left, LightHandle right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"Light({Slot}:{Generation})" : "Light(invalid)";
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Light
    {
        public Vector3 Position;
        public float Intensity;
        public Vector3 Color;
        public float Range;
        public Vector3 Direction;
        public float SpotAngle;
        public LightType Type;
        public bool CastsShadows;
        public float ShadowStrength;
        public uint ShadowMapSizeOverride;
        public float ShadowNearPlane;
        public float ShadowFarPlane;
        public int ShadowPriority;
    }

    public readonly struct LightFrameSnapshot
    {
        public LightFrameSnapshot(
            ReadOnlyMemory<Light> lights,
            int count,
            int directionalLightCount,
            int localLightCount,
            int firstShadowCastingDirectionalLightIndex,
            Light firstShadowCastingDirectionalLight,
            ulong revision,
            ulong topologyRevision = 0,
            ulong contentRevision = 0,
            ReadOnlyMemory<uint> stableIdentities = default)
        {
            Lights = lights;
            Count = count;
            DirectionalLightCount = directionalLightCount;
            LocalLightCount = localLightCount;
            FirstShadowCastingDirectionalLightIndex = firstShadowCastingDirectionalLightIndex;
            FirstShadowCastingDirectionalLight = firstShadowCastingDirectionalLight;
            Revision = revision;
            TopologyRevision = topologyRevision;
            ContentRevision = contentRevision;
            StableIdentities = stableIdentities;
        }

        public ReadOnlyMemory<Light> Lights { get; }
        public int Count { get; }
        public int DirectionalLightCount { get; }
        public int LocalLightCount { get; }
        public bool HasShadowCastingDirectionalLight => FirstShadowCastingDirectionalLightIndex >= 0;
        public int FirstShadowCastingDirectionalLightIndex { get; }
        public Light FirstShadowCastingDirectionalLight { get; }
        /// <summary>Revision of packed GPU-light data.</summary>
        public ulong Revision { get; }
        public ulong TopologyRevision { get; }
        public ulong ContentRevision { get; }
        public ReadOnlyMemory<uint> StableIdentities { get; }
    }

    /// <summary>Stable CPU-side light record for editor and scene-source bridges.</summary>
    public readonly record struct LightRecord(LightHandle Handle, Guid Id, string? Name, Light Light);

    public enum LightMutationKind
    {
        Added,
        Updated,
        Removed,
        Cleared
    }

    /// <summary>Producer-side light edit used by regional GI invalidation.</summary>
    public readonly record struct LightMutation(
        ulong Revision,
        LightMutationKind Kind,
        LightHandle Handle,
        Guid Id,
        Light? Previous,
        Light? Current);
    
    public sealed unsafe class LightManager : IDisposable, IDdgiLightMutationSource
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly object _lock = new object();
        
        private BufferHandle _lightBuffer;
        private Light[] _cpuLights;
        // Lights remain densely packed for the renderer. These tables provide stable editor-facing identity.
        private readonly int[] _slotToIndex = new int[MaxLights];
        private readonly int[] _indexToSlot = new int[MaxLights];
        private readonly int[] _slotGenerations = new int[MaxLights];
        private readonly string?[] _slotNames = new string?[MaxLights];
        private readonly Guid[] _slotIds = new Guid[MaxLights];
        private readonly Dictionary<Guid, int> _slotsById = new();
        private readonly Stack<int> _freeSlots = new();
        private Light[] _snapshotLights = Array.Empty<Light>();
        private uint[] _snapshotStableIdentities = Array.Empty<uint>();
        private GPULight[] _gpuLightScratch = Array.Empty<GPULight>();
        private LightFrameSnapshot _cachedSnapshot;
        private ulong _revision;
        private ulong _topologyRevision;
        private ulong _contentRevision;
        private ulong _snapshotRevision = ulong.MaxValue;
        private int _lightCount;
        private bool _needsUpload;
        private ulong _lastUploadBytes;
        private bool _disposed;

        public event Action<LightMutation>? Changed;
        
        public const int MaxLights = 1024;
        private static readonly ulong LightStride = (ulong)Marshal.SizeOf<GPULight>();
        public static readonly ulong LightBufferStateOffset =
            checked((ulong)MaxLights * LightStride);
        private static readonly ulong LightBufferSize = checked(
            LightBufferStateOffset +
            (ulong)Marshal.SizeOf<GPUDdgiLightBufferState>());
        
        public LightManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _cpuLights = new Light[MaxLights];
            Array.Fill(_slotToIndex, -1);
            Array.Fill(_indexToSlot, -1);
            for (int slot = MaxLights - 1; slot >= 0; slot--)
            {
                _slotGenerations[slot] = 1;
                _freeSlots.Push(slot);
            }
            _lightCount = 0;
            _needsUpload = false;
            
            _lightBuffer = _bufferManager.CreateDeviceBuffer(
                LightBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.LightBuffers,
                "Light Buffer");
            
            System.Diagnostics.Debug.WriteLine("Light manager created");
        }
        
        public int AddLight(Light light)
        {
            (int index, LightHandle handle) added;
            Guid id;
            ulong revision;
            lock (_lock)
            {
                added = AddLightUnsafe(light, name: null, id: null);
                id = _slotIds[added.handle.Slot];
                revision = _revision;
            }
            PublishMutation(new LightMutation(
                revision,
                LightMutationKind.Added,
                added.handle,
                id,
                null,
                light));
            return added.index;
        }

        /// <summary>Adds a light and returns a stable handle safe across packed-array swap-removals.</summary>
        public LightHandle AddLightHandle(in Light light, string? name = null, Guid? id = null)
        {
            (int index, LightHandle handle) added;
            Guid stableId;
            ulong revision;
            lock (_lock)
            {
                added = AddLightUnsafe(light, name, id);
                stableId = _slotIds[added.handle.Slot];
                revision = _revision;
            }
            PublishMutation(new LightMutation(
                revision,
                LightMutationKind.Added,
                added.handle,
                stableId,
                null,
                light));
            return added.handle;
        }
        
        public void RemoveLight(int index)
        {
            LightMutation? mutation = null;
            lock (_lock)
            {
                if (index < 0 || index >= _lightCount)
                    return;

                mutation = CreateRemovalMutationUnsafe(index);
                RemoveAtIndexUnsafe(index);
                mutation = mutation.Value with { Revision = _revision };
            }
            PublishMutation(mutation.Value);
        }
        
        public void UpdateLight(int index, Light light)
        {
            LightMutation? mutation = null;
            lock (_lock)
            {
                if (index < 0 || index >= _lightCount)
                    return;
                Light previous = _cpuLights[index];
                if (previous.Equals(light))
                    return;
                _cpuLights[index] = light;
                _needsUpload = true;
                _revision++;
                int slot = _indexToSlot[index];
                _contentRevision++;
                if (HasLocalTreeMembership(previous) != HasLocalTreeMembership(light))
                    _topologyRevision++;
                mutation = new LightMutation(
                    _revision,
                    LightMutationKind.Updated,
                    new LightHandle(slot, _slotGenerations[slot]),
                    _slotIds[slot],
                    previous,
                    light);
            }
            PublishMutation(mutation.Value);
        }

        public bool RemoveLight(LightHandle handle)
        {
            LightMutation? mutation = null;
            lock (_lock)
            {
                if (!TryResolveHandleUnsafe(handle, out int index))
                    return false;

                mutation = CreateRemovalMutationUnsafe(index);
                RemoveAtIndexUnsafe(index);
                mutation = mutation.Value with { Revision = _revision };
            }
            PublishMutation(mutation.Value);
            return true;
        }

        public bool UpdateLight(LightHandle handle, in Light light)
        {
            LightMutation? mutation = null;
            lock (_lock)
            {
                if (!TryResolveHandleUnsafe(handle, out int index))
                    return false;

                Light previous = _cpuLights[index];
                if (previous.Equals(light))
                    return true;
                _cpuLights[index] = light;
                _needsUpload = true;
                _revision++;
                _contentRevision++;
                if (HasLocalTreeMembership(previous) != HasLocalTreeMembership(light))
                    _topologyRevision++;
                mutation = new LightMutation(
                    _revision,
                    LightMutationKind.Updated,
                    handle,
                    _slotIds[handle.Slot],
                    previous,
                    light);
            }
            PublishMutation(mutation.Value);
            return true;
        }

        public bool TryGetLight(LightHandle handle, out Light light)
        {
            lock (_lock)
            {
                if (TryResolveHandleUnsafe(handle, out int index))
                {
                    light = _cpuLights[index];
                    return true;
                }
            }

            light = default;
            return false;
        }

        public bool TryGetLightHandle(int packedIndex, out LightHandle handle)
        {
            lock (_lock)
            {
                if (packedIndex >= 0 && packedIndex < _lightCount)
                {
                    int slot = _indexToSlot[packedIndex];
                    handle = new LightHandle(slot, _slotGenerations[slot]);
                    return true;
                }
            }

            handle = default;
            return false;
        }

        /// <summary>Resolves the stable scene identifier assigned when a light was created.</summary>
        public bool TryGetLightId(LightHandle handle, out Guid id)
        {
            lock (_lock)
            {
                if (TryResolveHandleUnsafe(handle, out _))
                {
                    id = _slotIds[handle.Slot];
                    return true;
                }
            }

            id = Guid.Empty;
            return false;
        }

        /// <summary>Finds a live light by its stable scene identifier without exposing packed indices.</summary>
        public bool TryGetLightHandle(Guid id, out LightHandle handle)
        {
            lock (_lock)
            {
                if (id != Guid.Empty && _slotsById.TryGetValue(id, out int slot) && _slotToIndex[slot] >= 0)
                {
                    handle = new LightHandle(slot, _slotGenerations[slot]);
                    return true;
                }
            }

            handle = default;
            return false;
        }

        public bool TryGetLightName(LightHandle handle, out string? name)
        {
            lock (_lock)
            {
                if (TryResolveHandleUnsafe(handle, out _))
                {
                    name = _slotNames[handle.Slot];
                    return true;
                }
            }

            name = null;
            return false;
        }

        public bool SetLightName(LightHandle handle, string? name)
        {
            lock (_lock)
            {
                if (!TryResolveHandleUnsafe(handle, out _))
                    return false;

                _slotNames[handle.Slot] = name;
                return true;
            }
        }
        
        public void ClearLights()
        {
            bool changed;
            ulong revision;
            lock (_lock)
            {
                changed = _lightCount != 0;
                if (!changed)
                    return;
                bool hadLocalTreeMembership = false;
                for (int index = 0; index < _lightCount; index++)
                {
                    hadLocalTreeMembership |= HasLocalTreeMembership(_cpuLights[index]);
                    int slot = _indexToSlot[index];
                    ReleaseSlotUnsafe(slot);
                    _indexToSlot[index] = -1;
                }
                _lightCount = 0;
                _needsUpload = true;
                _revision++;
                _contentRevision++;
                if (hadLocalTreeMembership)
                    _topologyRevision++;
                revision = _revision;
            }
            if (changed)
            {
                PublishMutation(new LightMutation(
                    revision,
                    LightMutationKind.Cleared,
                    default,
                    Guid.Empty,
                    null,
                    null));
            }
        }
        
        public BufferHandle LightBuffer => _lightBuffer;
        public ulong LightBufferAllocatedBytes => LightBufferSize;
        public int LightCount => _lightCount;
        public int MaxLightCount => MaxLights;
        public ulong LightBufferRevision
        {
            get
            {
                lock (_lock)
                    return _revision;
            }
        }
        public ulong LightTreeTopologyRevision
        {
            get
            {
                lock (_lock)
                    return _topologyRevision;
            }
        }
        public ulong LightTreeContentRevision
        {
            get
            {
                lock (_lock)
                    return _contentRevision;
            }
        }
        public ulong LastUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastUploadBytes;
            }
        }

        public int DirectionalLightCount => CountLights(LightType.Directional);
        public int LocalLightCount
        {
            get
            {
                lock (_lock)
                    return _lightCount - CountLightsUnsafe(LightType.Directional);
            }
        }

        private int CountLights(LightType type)
        {
            lock (_lock)
                return CountLightsUnsafe(type);
        }

        private int CountLightsUnsafe(LightType type)
        {
            int count = 0;
            for (int i = 0; i < _lightCount; i++)
            {
                if (_cpuLights[i].Type == type)
                    count++;
            }

            return count;
        }

        public bool TryGetFirstDirectionalLight(out int index, out Light light)
        {
            lock (_lock)
            {
                for (int i = 0; i < _lightCount; i++)
                {
                    if (_cpuLights[i].Type == LightType.Directional)
                    {
                        index = i;
                        light = _cpuLights[i];
                        return true;
                    }
                }
            }

            index = -1;
            light = default;
            return false;
        }

        public bool TryGetFirstShadowCastingDirectionalLight(out int index, out Light light)
        {
            lock (_lock)
            {
                for (int i = 0; i < _lightCount; i++)
                {
                    if (_cpuLights[i].Type == LightType.Directional && _cpuLights[i].CastsShadows)
                    {
                        index = i;
                        light = _cpuLights[i];
                        return true;
                    }
                }
            }

            index = -1;
            light = default;
            return false;
        }

        public Light[] GetLightSnapshot()
        {
            lock (_lock)
            {
                Light[] snapshot = new Light[_lightCount];
                Array.Copy(_cpuLights, snapshot, _lightCount);
                return snapshot;
            }
        }

        public LightFrameSnapshot GetFrameSnapshot()
        {
            lock (_lock)
            {
                if (_snapshotRevision == _revision)
                    return _cachedSnapshot;

                if (_snapshotLights.Length < _lightCount)
                    _snapshotLights = new Light[Math.Min(MaxLights, Math.Max(16, _lightCount * 2))];
                if (_snapshotStableIdentities.Length < _lightCount)
                {
                    _snapshotStableIdentities = new uint[
                        Math.Min(MaxLights, Math.Max(16, _lightCount * 2))];
                }

                int directionalLightCount = 0;
                int firstShadowCastingDirectionalIndex = -1;
                Light firstShadowCastingDirectionalLight = default;
                for (int i = 0; i < _lightCount; i++)
                {
                    Light light = _cpuLights[i];
                    _snapshotLights[i] = light;
                    int slot = _indexToSlot[i];
                    _snapshotStableIdentities[i] = PackStableIdentity(
                        slot,
                        _slotGenerations[slot]);
                    if (light.Type != LightType.Directional)
                        continue;

                    directionalLightCount++;
                    if (firstShadowCastingDirectionalIndex < 0 && light.CastsShadows)
                    {
                        firstShadowCastingDirectionalIndex = i;
                        firstShadowCastingDirectionalLight = light;
                    }
                }

                _cachedSnapshot = new LightFrameSnapshot(
                    _snapshotLights.AsMemory(0, _lightCount),
                    _lightCount,
                    directionalLightCount,
                    _lightCount - directionalLightCount,
                    firstShadowCastingDirectionalIndex,
                    firstShadowCastingDirectionalLight,
                    _revision,
                    _topologyRevision,
                    _contentRevision,
                    _snapshotStableIdentities.AsMemory(0, _lightCount));
                _snapshotRevision = _revision;
                return _cachedSnapshot;
            }
        }

        public void RegisterBuffer(BindlessHeap bindlessHeap, int bindlessIndex)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(_lightBuffer),
                0,
                Vk.WholeSize);
        }
        
        public void UploadToGPU(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for light upload.", nameof(commandBuffer));

            if (!_needsUpload)
            {
                lock (_lock)
                    _lastUploadBytes = 0;
                return;
            }
            
            lock (_lock)
            {
                _lastUploadBytes = 0;
                int localLightCount = 0;
                if (_lightCount > 0)
                {
                    if (_gpuLightScratch.Length < _lightCount)
                        Array.Resize(ref _gpuLightScratch, _lightCount);
                    for (int i = 0; i < _lightCount; i++)
                    {
                        GPULight gpuLight = ToGpuLight(_cpuLights[i]);
                        int slot = _indexToSlot[i];
                        gpuLight.StableIdentity = unchecked((int)PackStableIdentity(
                            slot,
                            _slotGenerations[slot]));
                        _gpuLightScratch[i] = gpuLight;
                        if (_cpuLights[i].Type != LightType.Directional)
                            localLightCount++;
                    }

                    _lastUploadBytes = GpuBufferUploader.UploadSpanToBuffer(
                        _context,
                        _bufferManager,
                        stagingRing,
                        commandBuffer,
                        _lightBuffer,
                        _gpuLightScratch.AsSpan(0, _lightCount),
                        barrierDescription: new UploadBarrierDescription(
                            PipelineStageFlags2.ComputeShaderBit |
                                PipelineStageFlags2.FragmentShaderBit,
                            AccessFlags2.ShaderStorageReadBit)).ByteCount;
                }

                var state = new GPUDdgiLightBufferState
                {
                    Magic = GPUDdgiLightBufferState.MagicValue,
                    LightBufferRevisionLow = (uint)_revision,
                    LightBufferRevisionHigh = (uint)(_revision >> 32),
                    TopologyRevisionLow = (uint)_topologyRevision,
                    TopologyRevisionHigh = (uint)(_topologyRevision >> 32),
                    ContentRevisionLow = (uint)_contentRevision,
                    ContentRevisionHigh = (uint)(_contentRevision >> 32),
                    LightCount = checked((uint)_lightCount),
                    LocalLightCount = checked((uint)localLightCount),
                    ValidationChecksum = GPUDdgiLightBufferState.ComputeChecksum(
                        _revision,
                        _topologyRevision,
                        _contentRevision,
                        checked((uint)_lightCount),
                        checked((uint)localLightCount))
                };
                _lastUploadBytes = checked(
                    _lastUploadBytes + GpuBufferUploader.UploadValueToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _lightBuffer,
                    state,
                    destinationOffset: LightBufferStateOffset,
                    barrierDescription: new UploadBarrierDescription(
                        PipelineStageFlags2.ComputeShaderBit |
                            PipelineStageFlags2.FragmentShaderBit,
                        AccessFlags2.ShaderStorageReadBit)).ByteCount);
                _needsUpload = false;
            }
        }

        internal static GPULight ToGpuLight(Light light)
        {
            float shadowStrength = light.CastsShadows
                ? ResolveShadowStrength(light.ShadowStrength)
                : 0f;
            return new GPULight
            {
                Position = new Njulf.Core.Math.Vector3(light.Position.X, light.Position.Y, light.Position.Z),
                Intensity = light.Intensity,
                Color = new Njulf.Core.Math.Vector3(light.Color.X, light.Color.Y, light.Color.Z),
                Range = light.Range,
                Direction = new Njulf.Core.Math.Vector3(light.Direction.X, light.Direction.Y, light.Direction.Z),
                SpotAngle = light.SpotAngle,
                Type = (int)light.Type,
                ShadowFlags = light.CastsShadows ? GPULight.CastsShadowsFlag : 0,
                ShadowStrength = shadowStrength
            };
        }

        private static float ResolveShadowStrength(float shadowStrength)
        {
            // Preserve the renderer's established authoring convention: zero
            // on a shadow-casting legacy Light means full-strength shadows.
            return Math.Clamp(shadowStrength <= 0f ? 1f : shadowStrength, 0f, 1f);
        }

        /// <summary>Copies live lights with stable IDs. Intended for infrequent tooling/save paths.</summary>
        public IReadOnlyList<LightRecord> GetLightRecords()
        {
            lock (_lock)
            {
                var records = new LightRecord[_lightCount];
                for (int index = 0; index < _lightCount; index++)
                {
                    int slot = _indexToSlot[index];
                    records[index] = new LightRecord(
                        new LightHandle(slot, _slotGenerations[slot]),
                        _slotIds[slot],
                        _slotNames[slot],
                        _cpuLights[index]);
                }
                return records;
            }
        }

        private (int index, LightHandle handle) AddLightUnsafe(in Light light, string? name, Guid? id)
        {
            if (_lightCount >= MaxLights || _freeSlots.Count == 0)
                throw new InvalidOperationException($"Forward+ supports at most {MaxLights} lights.");

            int index = _lightCount++;
            int slot = _freeSlots.Pop();
            Guid stableId = id.GetValueOrDefault(Guid.NewGuid());
            if (stableId == Guid.Empty)
                throw new ArgumentException("Light IDs must not be empty.", nameof(id));
            if (_slotsById.ContainsKey(stableId))
                throw new InvalidOperationException($"A light with ID '{stableId}' already exists.");
            _cpuLights[index] = light;
            _slotToIndex[slot] = index;
            _indexToSlot[index] = slot;
            _slotNames[slot] = name;
            _slotIds[slot] = stableId;
            _slotsById.Add(stableId, slot);
            _needsUpload = true;
            _revision++;
            _contentRevision++;
            if (HasLocalTreeMembership(light))
                _topologyRevision++;
            return (index, new LightHandle(slot, _slotGenerations[slot]));
        }

        private bool TryResolveHandleUnsafe(LightHandle handle, out int index)
        {
            if (!handle.IsValid || handle.Slot >= MaxLights || _slotGenerations[handle.Slot] != handle.Generation)
            {
                index = -1;
                return false;
            }

            index = _slotToIndex[handle.Slot];
            return index >= 0 && index < _lightCount;
        }

        private void RemoveAtIndexUnsafe(int index)
        {
            bool removedLocalTreeMember = HasLocalTreeMembership(_cpuLights[index]);
            int removedSlot = _indexToSlot[index];
            int lastIndex = --_lightCount;
            if (index != lastIndex)
            {
                _cpuLights[index] = _cpuLights[lastIndex];
                int movedSlot = _indexToSlot[lastIndex];
                _indexToSlot[index] = movedSlot;
                _slotToIndex[movedSlot] = index;
            }

            _cpuLights[lastIndex] = default;
            _indexToSlot[lastIndex] = -1;
            ReleaseSlotUnsafe(removedSlot);
            _needsUpload = true;
            _revision++;
            _contentRevision++;
            if (removedLocalTreeMember)
                _topologyRevision++;
        }

        private LightMutation CreateRemovalMutationUnsafe(int index)
        {
            int slot = _indexToSlot[index];
            return new LightMutation(
                _revision,
                LightMutationKind.Removed,
                new LightHandle(slot, _slotGenerations[slot]),
                _slotIds[slot],
                _cpuLights[index],
                null);
        }

        private void PublishMutation(in LightMutation mutation) =>
            Changed?.Invoke(mutation);

        private void ReleaseSlotUnsafe(int slot)
        {
            _slotToIndex[slot] = -1;
            _slotNames[slot] = null;
            if (_slotIds[slot] != Guid.Empty)
                _slotsById.Remove(_slotIds[slot]);
            _slotIds[slot] = Guid.Empty;
            _slotGenerations[slot] = _slotGenerations[slot] == int.MaxValue ? 1 : _slotGenerations[slot] + 1;
            _freeSlots.Push(slot);
        }

        internal static uint PackStableIdentity(int slot, int generation)
        {
            uint packedSlot = checked((uint)slot) & 0x3ffu;
            uint packedGeneration = unchecked((uint)generation) & 0x003f_ffffu;
            uint identity = (packedGeneration << 10) | packedSlot;
            return identity == 0 ? 1u : identity;
        }

        private static bool HasLocalTreeMembership(in Light light)
        {
            if (light.Type == LightType.Directional ||
                !float.IsFinite(light.Position.X) ||
                !float.IsFinite(light.Position.Y) ||
                !float.IsFinite(light.Position.Z) ||
                !float.IsFinite(light.Intensity) ||
                light.Intensity <= 0f ||
                !float.IsFinite(light.Color.X) ||
                !float.IsFinite(light.Color.Y) ||
                !float.IsFinite(light.Color.Z))
            {
                return false;
            }

            float luminance =
                MathF.Max(light.Color.X, 0f) * 0.2126f +
                MathF.Max(light.Color.Y, 0f) * 0.7152f +
                MathF.Max(light.Color.Z, 0f) * 0.0722f;
            float flux = luminance * MathF.Max(light.Intensity, 0f);
            return float.IsFinite(flux) && flux > 1e-20f;
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                if (_lightBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_lightBuffer);
            }
            
            System.Diagnostics.Debug.WriteLine("Light manager disposed.");
        }
    }
}
