using Njulf.Core.Geometry;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Retains the CPU-only geometry records required by submission and debug
/// consumers for meshes whose GPU records live in the managed page cache.
/// Entries are owned by a mesh slot generation so slot reuse cannot expose
/// stale virtual-address data.
/// </summary>
internal sealed class ManagedCpuMeshletCache
{
    private readonly Dictionary<int, Entry> _entries = [];

    internal int Count => _entries.Count;

    internal void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries.EnsureCapacity(capacity);
    }

    internal void ValidatePrepared(
        MeshHandle handle,
        in MeshInfo meshInfo,
        IReadOnlyList<Meshlet> meshlets)
    {
        ArgumentNullException.ThrowIfNull(meshlets);
        if (!handle.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(handle));
        if (!meshInfo.UsesManagedPhysicalResidency)
        {
            if (meshlets.Count != 0)
            {
                throw new InvalidOperationException(
                    "Only managed-residency meshes can retain virtual CPU meshlets.");
            }
            return;
        }
        if (!MeshletVirtualAddress.IsVirtual(meshInfo.MeshletOffset))
        {
            throw new InvalidOperationException(
                "Managed-residency mesh metadata does not contain a virtual base address.");
        }
        if (meshlets.Count != checked((int)meshInfo.MeshletLodGeneratedCount))
        {
            throw new InvalidOperationException(
                "Managed CPU meshlet count does not match the virtual geometry range.");
        }
        if (_entries.ContainsKey(handle.Index))
        {
            throw new InvalidOperationException(
                $"Mesh slot {handle.Index} already owns managed CPU meshlets.");
        }

        uint virtualBase = MeshletVirtualAddress.Decode(meshInfo.MeshletOffset);
        ulong virtualEnd = checked((ulong)virtualBase + (uint)meshlets.Count);
        if (virtualEnd > (ulong)MeshletVirtualAddress.IndexMask + 1UL)
        {
            throw new InvalidOperationException(
                "Managed CPU meshlets exceed the virtual address space.");
        }

        ValidateLodOffset(
            meshInfo.MeshletLod1Offset,
            checked(virtualBase + meshInfo.MeshletCount),
            nameof(meshInfo.MeshletLod1Offset));
        ValidateLodOffset(
            meshInfo.MeshletLod2Offset,
            checked(virtualBase + meshInfo.MeshletCount +
                meshInfo.MeshletLod1Count),
            nameof(meshInfo.MeshletLod2Offset));
    }

    internal void Commit(
        MeshHandle handle,
        in MeshInfo meshInfo,
        Meshlet[] meshlets)
    {
        ArgumentNullException.ThrowIfNull(meshlets);
        ValidatePrepared(handle, meshInfo, meshlets);
        _entries.Add(
            handle.Index,
            new Entry(
                handle.Generation,
                MeshletVirtualAddress.Decode(meshInfo.MeshletOffset),
                meshlets));
    }

    internal Meshlet Get(
        MeshHandle handle,
        in MeshInfo meshInfo,
        uint meshletAddress)
    {
        if (!MeshletVirtualAddress.IsVirtual(meshletAddress))
        {
            throw new ArgumentException(
                "The managed CPU meshlet cache requires a virtual address.",
                nameof(meshletAddress));
        }
        if (!_entries.TryGetValue(handle.Index, out Entry entry) ||
            entry.Generation != handle.Generation)
        {
            throw new InvalidOperationException(
                $"Mesh handle {handle.Index}:{handle.Generation} does not own retained managed CPU meshlets.");
        }
        if (!meshInfo.UsesManagedPhysicalResidency ||
            entry.Meshlets.Length != checked((int)meshInfo.MeshletLodGeneratedCount))
        {
            throw new InvalidOperationException(
                "Managed CPU meshlet state diverged from authoritative mesh metadata.");
        }

        uint virtualIndex = MeshletVirtualAddress.Decode(meshletAddress);
        if (virtualIndex < entry.VirtualBase)
        {
            throw CreateInvalidAddress(meshletAddress, handle, entry);
        }
        uint localIndex = virtualIndex - entry.VirtualBase;
        if (localIndex >= entry.Meshlets.Length)
        {
            throw CreateInvalidAddress(meshletAddress, handle, entry);
        }
        return entry.Meshlets[(int)localIndex];
    }

    internal void ValidateRelease(
        int meshIndex,
        in MeshInfo meshInfo)
    {
        bool hasEntry = _entries.TryGetValue(meshIndex, out Entry entry);
        if (!meshInfo.UsesManagedPhysicalResidency)
        {
            if (hasEntry)
            {
                throw new InvalidOperationException(
                    "A direct mesh unexpectedly owns managed CPU meshlets.");
            }
            return;
        }
        if (!hasEntry ||
            entry.Meshlets.Length != checked((int)meshInfo.MeshletLodGeneratedCount) ||
            entry.VirtualBase != MeshletVirtualAddress.Decode(meshInfo.MeshletOffset))
        {
            throw new InvalidOperationException(
                "Released managed mesh state diverged from its retained CPU meshlets.");
        }
    }

    internal void Release(int meshIndex) => _entries.Remove(meshIndex);

    internal void RemovePreparedSlots(ReadOnlySpan<int> meshIndices)
    {
        foreach (int meshIndex in meshIndices)
            _entries.Remove(meshIndex);
    }

    internal void Clear() => _entries.Clear();

    private static void ValidateLodOffset(
        uint encodedOffset,
        uint expectedVirtualIndex,
        string name)
    {
        if (!MeshletVirtualAddress.IsVirtual(encodedOffset) ||
            MeshletVirtualAddress.Decode(encodedOffset) != expectedVirtualIndex)
        {
            throw new InvalidOperationException(
                $"Managed CPU meshlet {name} is not contiguous with the virtual base range.");
        }
    }

    private static InvalidOperationException CreateInvalidAddress(
        uint address,
        MeshHandle handle,
        in Entry entry) =>
        new(
            $"Virtual meshlet address 0x{address:x8} is outside mesh " +
            $"{handle.Index}:{handle.Generation} CPU range " +
            $"[{entry.VirtualBase}, {entry.VirtualBase + (uint)entry.Meshlets.Length}).");

    private readonly record struct Entry(
        uint Generation,
        uint VirtualBase,
        Meshlet[] Meshlets);
}
