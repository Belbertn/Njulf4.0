using System.Runtime.InteropServices;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns mesh-slot generations, reference counts, and reusable indices.
/// Registration planning is non-mutating; prepared free-slot reservations can
/// be restored exactly if GPU publication fails.
/// </summary>
internal sealed class MeshSlotLifetimeTable
{
    private readonly List<MeshSlotState> _slots = new();
    private readonly Stack<int> _freeIndices = new();

    public int Count => _slots.Count;

    public int ActiveCount { get; private set; }

    public int FreeCount => _freeIndices.Count;

    public int[] CaptureAvailableFreeIndices() =>
        _freeIndices.ToArray();

    /// <summary>
    /// Resolves the generation for a reusable slot or a not-yet-published
    /// append position. Batch planning is intentionally non-mutating, so more
    /// than one contiguous future append may be prepared while
    /// <see cref="Count"/> is unchanged. <see cref="CommitSlot"/> remains the
    /// authority that enforces gap-free publication.
    /// </summary>
    public uint GetNextGeneration(int meshIndex)
    {
        if (meshIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(meshIndex));
        if (meshIndex >= _slots.Count)
            return 1;

        MeshSlotState slot = _slots[meshIndex];
        if (slot.ReferenceCount != 0)
        {
            throw new InvalidOperationException(
                $"Mesh slot {meshIndex} is still live and cannot be reused.");
        }
        if (slot.Generation == uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"Mesh slot {meshIndex} exhausted its generation space and cannot be reused safely.");
        }

        return slot.Generation + 1;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _slots.EnsureCapacity(capacity);
    }

    public RegistrationSnapshot CaptureRegistrationSnapshot(
        IReadOnlyList<int> pendingIndices)
    {
        ArgumentNullException.ThrowIfNull(pendingIndices);
        var reusedSlots = new List<MeshSlotSnapshot>(
            Math.Min(pendingIndices.Count, _slots.Count));
        var seen = new HashSet<int>();
        foreach (int index in pendingIndices)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(pendingIndices));
            if (!seen.Add(index))
            {
                throw new InvalidOperationException(
                    $"Mesh slot {index} was prepared more than once.");
            }
            if (index < _slots.Count)
            {
                reusedSlots.Add(
                    new MeshSlotSnapshot(index, _slots[index]));
            }
        }

        return new RegistrationSnapshot(
            _slots.Count,
            ActiveCount,
            reusedSlots.ToArray());
    }

    public void ReservePreparedFreeIndices(
        IReadOnlyList<int> availableFreeIndices,
        int requestedCount,
        ICollection<int> reservedFreeIndices)
    {
        ArgumentNullException.ThrowIfNull(availableFreeIndices);
        ArgumentNullException.ThrowIfNull(reservedFreeIndices);
        if (requestedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedCount));

        int reservationCount = Math.Min(
            availableFreeIndices.Count,
            requestedCount);
        for (int i = 0; i < reservationCount; i++)
        {
            if (_freeIndices.Count == 0)
            {
                throw new InvalidOperationException(
                    "A prepared free mesh-slot reservation disappeared before publication.");
            }

            int actual = _freeIndices.Pop();
            reservedFreeIndices.Add(actual);
            if (actual != availableFreeIndices[i])
            {
                throw new InvalidOperationException(
                    $"Free mesh-slot reservation changed before publication. " +
                    $"expected={availableFreeIndices[i]}, actual={actual}.");
            }
        }
    }

    public void CommitSlot(int meshIndex, uint generation)
    {
        if (meshIndex < 0 || meshIndex > _slots.Count)
            throw new ArgumentOutOfRangeException(nameof(meshIndex));
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        if (meshIndex == _slots.Count)
        {
            if (generation != 1)
            {
                throw new InvalidOperationException(
                    "A newly appended mesh slot must start at generation 1.");
            }

            _slots.Add(new MeshSlotState(generation, 1));
        }
        else
        {
            MeshSlotState previous = _slots[meshIndex];
            if (previous.ReferenceCount != 0)
            {
                throw new InvalidOperationException(
                    $"Mesh slot {meshIndex} is still live and cannot be committed.");
            }
            uint expectedGeneration = GetNextGeneration(meshIndex);
            if (generation != expectedGeneration)
            {
                throw new InvalidOperationException(
                    $"Mesh slot {meshIndex} generation changed before publication. " +
                    $"expected={expectedGeneration}, actual={generation}.");
            }

            _slots[meshIndex] =
                new MeshSlotState(generation, 1);
        }

        ActiveCount = checked(ActiveCount + 1);
    }

    public void Retain(MeshHandle handle)
    {
        int index = ValidateLiveHandle(handle);
        MeshSlotState slot = _slots[index];
        int referenceCount = checked(slot.ReferenceCount + 1);
        _slots[index] = slot with
        {
            ReferenceCount = referenceCount
        };
    }

    /// <returns>
    /// <see langword="true"/> when the final reference was released and the
    /// caller must clear slot-owned mesh state.
    /// </returns>
    public bool Release(MeshHandle handle)
    {
        int index = ValidateLiveHandle(handle);
        MeshSlotState slot = _slots[index];
        if (slot.ReferenceCount > 1)
        {
            _slots[index] = slot with
            {
                ReferenceCount = slot.ReferenceCount - 1
            };
            return false;
        }

        bool reusable = slot.Generation != uint.MaxValue;
        if (reusable)
        {
            _freeIndices.EnsureCapacity(
                checked(_freeIndices.Count + 1));
        }

        _slots[index] = slot with { ReferenceCount = 0 };
        ActiveCount--;
        if (reusable)
            _freeIndices.Push(index);
        return true;
    }

    public int GetReferenceCount(MeshHandle handle)
    {
        int index = ValidateLiveHandle(handle);
        return _slots[index].ReferenceCount;
    }

    public bool IsLive(MeshHandle handle)
    {
        return handle.IsValid &&
               handle.Index < _slots.Count &&
               _slots[handle.Index].Generation == handle.Generation &&
               _slots[handle.Index].ReferenceCount > 0;
    }

    public bool IsSlotLive(int index)
    {
        return index >= 0 &&
               index < _slots.Count &&
               _slots[index].ReferenceCount > 0;
    }

    public int FindHighestLiveSlot()
    {
        for (int index = _slots.Count - 1; index >= 0; index--)
        {
            if (_slots[index].ReferenceCount > 0)
                return index;
        }

        return -1;
    }

    public int FindHighestLiveSlotExcluding(int excludedIndex)
    {
        if (excludedIndex < 0 || excludedIndex >= _slots.Count)
            throw new ArgumentOutOfRangeException(nameof(excludedIndex));

        for (int index = _slots.Count - 1; index >= 0; index--)
        {
            if (index != excludedIndex &&
                _slots[index].ReferenceCount > 0)
            {
                return index;
            }
        }

        return -1;
    }

    public void RestoreRegistrationSnapshot(
        RegistrationSnapshot snapshot)
    {
        if (snapshot.SlotCount < 0 ||
            snapshot.SlotCount > _slots.Count)
        {
            throw new InvalidOperationException(
                "Mesh lifetime snapshot has an invalid slot count.");
        }

        CollectionsMarshal.SetCount(
            _slots,
            snapshot.SlotCount);
        foreach (MeshSlotSnapshot slot in snapshot.ReusedSlots)
            _slots[slot.Index] = slot.State;
        ActiveCount = snapshot.ActiveCount;
    }

    public void RestoreReservedFreeIndices(
        IList<int> reservedFreeIndices)
    {
        ArgumentNullException.ThrowIfNull(reservedFreeIndices);
        for (int i = reservedFreeIndices.Count - 1; i >= 0; i--)
            _freeIndices.Push(reservedFreeIndices[i]);
        reservedFreeIndices.Clear();
    }

    public void Clear()
    {
        _slots.Clear();
        _freeIndices.Clear();
        ActiveCount = 0;
    }

    private int ValidateLiveHandle(MeshHandle handle)
    {
        if (!handle.IsValid || handle.Index >= _slots.Count)
            throw new InvalidOperationException("Invalid mesh handle.");

        MeshSlotState slot = _slots[handle.Index];
        if (slot.Generation != handle.Generation)
            throw new InvalidOperationException("Mesh handle generation mismatch.");
        if (slot.ReferenceCount <= 0)
            throw new InvalidOperationException("Mesh handle has already been released.");
        return handle.Index;
    }

    internal readonly record struct MeshSlotState(
        uint Generation,
        int ReferenceCount);

    internal readonly record struct MeshSlotSnapshot(
        int Index,
        MeshSlotState State);

    internal readonly record struct RegistrationSnapshot(
        int SlotCount,
        int ActiveCount,
        MeshSlotSnapshot[] ReusedSlots);
}
