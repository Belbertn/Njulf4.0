using System;
using System.Collections.Generic;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Diagnostics
{
    public sealed class GpuAllocationTracker
    {
        private readonly object _lock = new();
        private readonly Dictionary<BufferHandle, AllocationRecord> _buffers = new();
        private readonly Dictionary<ulong, AllocationRecord> _images = new();

        public void RegisterBuffer(BufferHandle handle, ulong size, BufferUsageFlags usage, MemoryBudgetCategory category, string name)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
                _buffers[handle] = new AllocationRecord(size, category, string.IsNullOrWhiteSpace(name) ? usage.ToString() : name);
        }

        public void RegisterImage(
            ulong nativeHandle,
            ulong estimatedBytes,
            Format format,
            Extent3D extent,
            uint mipLevels,
            uint arrayLayers,
            MemoryBudgetCategory category,
            string name)
        {
            if (nativeHandle == 0)
                return;

            string description = string.IsNullOrWhiteSpace(name)
                ? $"{format} {extent.Width}x{extent.Height}x{extent.Depth} mips={mipLevels} layers={arrayLayers}"
                : name;

            lock (_lock)
                _images[nativeHandle] = new AllocationRecord(estimatedBytes, category, description);
        }

        public void RetireBuffer(BufferHandle handle)
        {
            lock (_lock)
                _buffers.Remove(handle);
        }

        public void RetireImage(ulong nativeHandle)
        {
            lock (_lock)
                _images.Remove(nativeHandle);
        }

        public MemoryBudgetSnapshot CreateSnapshot(RenderBudgetProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var totals = new Dictionary<MemoryBudgetCategory, CategoryTotal>();
            lock (_lock)
            {
                foreach (AllocationRecord record in _buffers.Values)
                    Add(totals, record);
                foreach (AllocationRecord record in _images.Values)
                    Add(totals, record);
            }

            var entries = new List<MemoryBudgetEntry>(totals.Count);
            ulong totalBytes = 0;
            foreach (KeyValuePair<MemoryBudgetCategory, CategoryTotal> pair in totals)
            {
                totalBytes = checked(totalBytes + pair.Value.Bytes);
                entries.Add(new MemoryBudgetEntry(pair.Key, pair.Value.Bytes, pair.Value.Count, pair.Value.Description));
            }

            entries.Sort((left, right) => left.Category.CompareTo(right.Category));
            return new MemoryBudgetSnapshot(totalBytes, profile.GpuMemoryBudgetBytes, entries);
        }

        private static void Add(Dictionary<MemoryBudgetCategory, CategoryTotal> totals, AllocationRecord record)
        {
            if (!totals.TryGetValue(record.Category, out CategoryTotal total))
                total = new CategoryTotal();

            total.Bytes = checked(total.Bytes + record.Bytes);
            total.Count++;
            if (string.IsNullOrEmpty(total.Description))
                total.Description = record.Description;
            totals[record.Category] = total;
        }

        private readonly record struct AllocationRecord(ulong Bytes, MemoryBudgetCategory Category, string Description);

        private struct CategoryTotal
        {
            public ulong Bytes;
            public int Count;
            public string Description;
        }
    }
}
