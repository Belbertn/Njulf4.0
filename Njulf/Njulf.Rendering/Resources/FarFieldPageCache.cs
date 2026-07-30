using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Stable world-space address of one far-field page.  A key identifies
    /// content, while its physical page is deliberately a bounded cache slot.
    /// </summary>
    internal readonly record struct FarFieldPageKey(int Cascade, int X, int Y, int Z);

    internal enum FarFieldPageResidencyState : byte
    {
        Empty = 0,
        Pending = 1,
        Baking = 2,
        // Commands have been recorded and the GPU page-table publication is
        // ordered before consumers.  Keep this state distinct from Resident so
        // the CPU eviction policy cannot reuse the physical page until a
        // conservative frame delay has elapsed.
        Publishing = 3,
        Resident = 4
    }

    internal readonly record struct FarFieldPageBakeRequest(
        FarFieldPageKey Key,
        int PhysicalPageIndex,
        uint Generation,
        ulong SourceRevision,
        int GpuTableEntryIndex);

    /// <summary>
    /// CPU-side work item for one bounded page bake.  Geometry is filtered to
    /// the page bounds before commands are recorded, so a streamed world does
    /// not repeatedly voxelize every static instance for every page.
    /// </summary>
    internal readonly record struct FarFieldPageBakeWork(
        FarFieldPageBakeRequest Request,
        int[] InstanceIndices,
        int InstanceCount);

    /// <summary>
    /// Deterministic, bounded virtual-page cache shared by the far-field CPU
    /// scheduler and the GPU's open-addressed page table.  Missing, pending, and
    /// stale pages deliberately remain invalid: tracing can then use its stable
    /// environment fallback rather than sampling a reused physical page.
    /// </summary>
    internal sealed class FarFieldPageCache
    {
        public const uint CascadeMask = 0xffu;
        public const uint AllocatedFlag = 1u << 8;
        public const uint ValidFlag = 1u << 9;

        private readonly Dictionary<FarFieldPageKey, int> _slotByKey = new();
        private PageSlot[] _slots = Array.Empty<PageSlot>();
        private int[] _gpuTableEntryIndices = Array.Empty<int>();
        private ulong _frameSerial;
        private int _evictionCount;
        private int _stalePublicationRejectCount;
        private const ulong SafePublishDelayFrames = 3;

        public int Capacity => _slots.Length;
        public int EvictionCount => _evictionCount;
        public int StalePublicationRejectCount => _stalePublicationRejectCount;
        public int ResidentCount => Count(FarFieldPageResidencyState.Resident);
        public int PendingCount => Count(FarFieldPageResidencyState.Pending) +
            Count(FarFieldPageResidencyState.Baking) +
            Count(FarFieldPageResidencyState.Publishing);
        public int RequiredGpuTableCapacity => NextPowerOfTwo(Math.Max(2, Capacity * 2));

        public void Configure(int capacity)
        {
            capacity = Math.Max(1, capacity);
            if (capacity == _slots.Length)
                return;

            _slotByKey.Clear();
            _slots = new PageSlot[capacity];
            _gpuTableEntryIndices = new int[capacity];
            Array.Fill(_gpuTableEntryIndices, -1);
            _evictionCount = 0;
            _stalePublicationRejectCount = 0;
        }

        public void Clear()
        {
            if (_slots.Length == 0)
                return;

            _slotByKey.Clear();
            Array.Clear(_slots, 0, _slots.Length);
            Array.Fill(_gpuTableEntryIndices, -1);
            _evictionCount = 0;
            _stalePublicationRejectCount = 0;
        }

        public void BeginFrame(ulong frameSerial)
        {
            _frameSerial = Math.Max(frameSerial, _frameSerial + 1UL);
            for (int i = 0; i < _slots.Length; i++)
            {
                ref PageSlot slot = ref _slots[i];
                if (slot.State == FarFieldPageResidencyState.Publishing &&
                    _frameSerial - slot.LastPublishedFrame >= SafePublishDelayFrames)
                {
                    slot.State = FarFieldPageResidencyState.Resident;
                    slot.LastResidentFrame = _frameSerial;
                }
            }
        }

        public void Request(
            FarFieldPageKey key,
            ulong sourceRevision,
            int priority,
            ulong validationRevision = 0)
        {
            if (_slots.Length == 0)
                return;

            priority = Math.Max(priority, 0);
            if (_slotByKey.TryGetValue(key, out int existingSlot))
            {
                ref PageSlot slot = ref _slots[existingSlot];
                slot.LastRequestedFrame = _frameSerial;
                slot.ValidationRevision = validationRevision;
                // Priority is a current-frame demand signal, not a historical high
                // watermark.  Keeping the old maximum made a page requested once
                // near the camera effectively unevictable after the camera moved.
                slot.Priority = priority;
                if (slot.SourceRevision != sourceRevision)
                {
                    slot.SourceRevision = sourceRevision;
                    slot.Generation = AdvanceGeneration(slot.Generation);
                    // Do not cancel a recorded bake; its generation guard prevents
                    // publication and the slot is rescheduled immediately after it.
                    if (slot.State != FarFieldPageResidencyState.Baking)
                        slot.State = FarFieldPageResidencyState.Pending;
                }

                return;
            }

            int physicalPage = FindVictimSlot();
            if (physicalPage < 0)
                return; // Every page is currently baking; defer deterministically.

            ref PageSlot victim = ref _slots[physicalPage];
            if (victim.State != FarFieldPageResidencyState.Empty)
            {
                // A low-priority outer-cascade request must never displace a more
                // relevant resident page merely because the cache is full.  This
                // keeps a small page pool stable during camera movement.
                if (priority < victim.Priority ||
                    (priority == victim.Priority && victim.LastRequestedFrame >= _frameSerial))
                    return;

                _slotByKey.Remove(victim.Key);
                _evictionCount++;
            }

            victim = new PageSlot
            {
                Key = key,
                State = FarFieldPageResidencyState.Pending,
                Generation = AdvanceGeneration(victim.Generation),
                SourceRevision = sourceRevision,
                ValidationRevision = validationRevision,
                LastRequestedFrame = _frameSerial,
                Priority = priority
            };
            _slotByKey.Add(key, physicalPage);
        }

        public bool TryBeginBake(out FarFieldPageBakeRequest request)
        {
            int selected = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                ref PageSlot candidate = ref _slots[i];
                if (candidate.State != FarFieldPageResidencyState.Pending)
                    continue;

                if (selected < 0 || IsHigherBakePriority(candidate, _slots[selected], i, selected))
                    selected = i;
            }

            if (selected < 0)
            {
                request = default;
                return false;
            }

            ref PageSlot slot = ref _slots[selected];
            slot.State = FarFieldPageResidencyState.Baking;
            request = new FarFieldPageBakeRequest(
                slot.Key,
                selected,
                slot.Generation,
                slot.SourceRevision,
                selected < _gpuTableEntryIndices.Length ? _gpuTableEntryIndices[selected] : -1);
            return true;
        }

        public FarFieldPageBakeRequest WithGpuTableEntryIndex(FarFieldPageBakeRequest request)
        {
            if ((uint)request.PhysicalPageIndex >= (uint)_gpuTableEntryIndices.Length)
                return request;
            return request with { GpuTableEntryIndex = _gpuTableEntryIndices[request.PhysicalPageIndex] };
        }

        public void MarkBakePublished(FarFieldPageBakeRequest request)
        {
            if ((uint)request.PhysicalPageIndex >= (uint)_slots.Length)
                return;

            ref PageSlot slot = ref _slots[request.PhysicalPageIndex];
            if (slot.Key != request.Key || slot.State != FarFieldPageResidencyState.Baking)
            {
                _stalePublicationRejectCount++;
                return;
            }

            if (slot.Generation != request.Generation || slot.SourceRevision != request.SourceRevision)
            {
                _stalePublicationRejectCount++;
                slot.State = FarFieldPageResidencyState.Pending;
                return;
            }

            slot.State = FarFieldPageResidencyState.Publishing;
            slot.LastPublishedFrame = _frameSerial;
        }

        public void MarkBakeFailed(FarFieldPageBakeRequest request)
        {
            if ((uint)request.PhysicalPageIndex >= (uint)_slots.Length)
                return;

            ref PageSlot slot = ref _slots[request.PhysicalPageIndex];
            if (slot.Key == request.Key && slot.State == FarFieldPageResidencyState.Baking)
                slot.State = FarFieldPageResidencyState.Pending;
        }

        public bool IsResident(FarFieldPageKey key)
        {
            return _slotByKey.TryGetValue(key, out int slot) &&
                _slots[slot].State == FarFieldPageResidencyState.Resident;
        }

        public bool TryGetPhysicalPage(FarFieldPageKey key, out int physicalPage, out uint generation)
        {
            if (_slotByKey.TryGetValue(key, out physicalPage))
            {
                PageSlot slot = _slots[physicalPage];
                generation = slot.Generation;
                return slot.State != FarFieldPageResidencyState.Empty;
            }

            physicalPage = -1;
            generation = 0;
            return false;
        }

        public bool TryGetSourceRevision(FarFieldPageKey key, out ulong sourceRevision)
        {
            if (_slotByKey.TryGetValue(key, out int slot))
            {
                sourceRevision = _slots[slot].SourceRevision;
                return true;
            }

            sourceRevision = 0;
            return false;
        }

        public bool TryGetValidationRevision(FarFieldPageKey key, out ulong validationRevision)
        {
            if (_slotByKey.TryGetValue(key, out int slot))
            {
                validationRevision = _slots[slot].ValidationRevision;
                return true;
            }

            validationRevision = 0;
            return false;
        }

        public void BuildGpuTable(Span<GPUFarFieldPageTableEntry> destination)
        {
            destination.Clear();
            Array.Fill(_gpuTableEntryIndices, -1);
            if (destination.Length == 0 || (destination.Length & (destination.Length - 1)) != 0)
                throw new ArgumentException("The far-field page table must have a power-of-two capacity.", nameof(destination));

            int mask = destination.Length - 1;
            for (int physicalPage = 0; physicalPage < _slots.Length; physicalPage++)
            {
                PageSlot slot = _slots[physicalPage];
                if (slot.State == FarFieldPageResidencyState.Empty)
                    continue;

                int tableIndex = (int)(Hash(slot.Key) & (uint)mask);
                while ((destination[tableIndex].CascadeAndFlags & AllocatedFlag) != 0u)
                    tableIndex = (tableIndex + 1) & mask;

                uint flags = ((uint)slot.Key.Cascade & CascadeMask) | AllocatedFlag;
                // A publishing page is already visible to GPU consumers: the
                // bake pass writes its payload, issues the required barrier, and
                // then atomically marks this exact generation valid.  Leaving it
                // invalid here would overwrite that publication on the following
                // frame's table upload and cause a two-frame missing-page flicker.
                if (slot.State is FarFieldPageResidencyState.Resident or FarFieldPageResidencyState.Publishing)
                    flags |= ValidFlag;
                destination[tableIndex] = new GPUFarFieldPageTableEntry
                {
                    WorldPageX = slot.Key.X,
                    WorldPageY = slot.Key.Y,
                    WorldPageZ = slot.Key.Z,
                    CascadeAndFlags = flags,
                    PhysicalPageIndex = checked((uint)physicalPage),
                    Generation = slot.Generation,
                    Reserved0 = unchecked((uint)slot.SourceRevision),
                    Reserved1 = unchecked((uint)(slot.SourceRevision >> 32))
                };
                _gpuTableEntryIndices[physicalPage] = tableIndex;
            }
        }

        private int FindVictimSlot()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].State == FarFieldPageResidencyState.Empty)
                    return i;
            }

            int selected = -1;
            for (int i = 0; i < _slots.Length; i++)
            {
                PageSlot candidate = _slots[i];
                if (candidate.State == FarFieldPageResidencyState.Baking ||
                    candidate.State == FarFieldPageResidencyState.Publishing)
                    continue;
                if (selected < 0 || IsBetterEvictionCandidate(candidate, _slots[selected], i, selected))
                    selected = i;
            }

            return selected;
        }

        private static bool IsHigherBakePriority(PageSlot left, PageSlot right, int leftIndex, int rightIndex)
        {
            if (left.Priority != right.Priority)
                return left.Priority > right.Priority;
            if (left.LastRequestedFrame != right.LastRequestedFrame)
                return left.LastRequestedFrame > right.LastRequestedFrame;
            return leftIndex < rightIndex;
        }

        private static bool IsBetterEvictionCandidate(PageSlot left, PageSlot right, int leftIndex, int rightIndex)
        {
            if (left.Priority != right.Priority)
                return left.Priority < right.Priority;
            if (left.LastRequestedFrame != right.LastRequestedFrame)
                return left.LastRequestedFrame < right.LastRequestedFrame;
            if (left.LastResidentFrame != right.LastResidentFrame)
                return left.LastResidentFrame < right.LastResidentFrame;
            return leftIndex < rightIndex;
        }

        private int Count(FarFieldPageResidencyState state)
        {
            int count = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].State == state)
                    count++;
            }

            return count;
        }

        private static uint AdvanceGeneration(uint generation)
        {
            generation++;
            return generation == 0u ? 1u : generation;
        }

        private static uint Hash(FarFieldPageKey key)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = Mix(hash, (uint)key.Cascade);
                hash = Mix(hash, (uint)key.X);
                hash = Mix(hash, (uint)key.Y);
                hash = Mix(hash, (uint)key.Z);
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                return hash ^ (hash >> 16);
            }
        }

        private static uint Mix(uint hash, uint value) => (hash ^ value) * 16777619u;

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value && result < 1 << 30)
                result <<= 1;
            return result;
        }

        private struct PageSlot
        {
            public FarFieldPageKey Key;
            public FarFieldPageResidencyState State;
            public uint Generation;
            public ulong SourceRevision;
            public ulong ValidationRevision;
            public ulong LastRequestedFrame;
            public ulong LastResidentFrame;
            public ulong LastPublishedFrame;
            public int Priority;
        }
    }
}
