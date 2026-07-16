using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record MemoryBudgetSnapshot(
        ulong TotalTrackedBytes,
        ulong BudgetBytes,
        IReadOnlyList<MemoryBudgetEntry> Entries,
        MemoryHeapBudgetSnapshot HeapBudget)
    {
        public ulong EffectiveMemoryBytes => HeapBudget.IsAvailable && HeapBudget.PrimaryBudgetBytes > 0
            ? HeapBudget.PrimaryUsageBytes
            : TotalTrackedBytes;

        public ulong EffectiveBudgetBytes => HeapBudget.IsAvailable && HeapBudget.PrimaryBudgetBytes > 0
            ? HeapBudget.PrimaryBudgetBytes
            : BudgetBytes;

        public static MemoryBudgetSnapshot Empty { get; } = new(
            0,
            0,
            Array.Empty<MemoryBudgetEntry>(),
            MemoryHeapBudgetSnapshot.Unavailable);
    }

    public sealed record MemoryBudgetEntry(
        MemoryBudgetCategory Category,
        ulong Bytes,
        int AllocationCount,
        string Description);

    public sealed record MemoryHeapBudgetSnapshot(
        bool IsAvailable,
        IReadOnlyList<MemoryHeapBudgetEntry> Entries)
    {
        public static MemoryHeapBudgetSnapshot Unavailable { get; } = new(false, Array.Empty<MemoryHeapBudgetEntry>());

        public ulong TotalUsageBytes => Sum(entry => entry.UsageBytes);
        public ulong TotalBudgetBytes => Sum(entry => entry.BudgetBytes);
        public ulong TotalAllocationBytes => Sum(entry => entry.AllocationBytes);
        public ulong TotalBlockBytes => Sum(entry => entry.BlockBytes);
        public ulong DeviceLocalUsageBytes => Sum(entry => entry.UsageBytes, deviceLocalOnly: true);
        public ulong DeviceLocalBudgetBytes => Sum(entry => entry.BudgetBytes, deviceLocalOnly: true);
        public ulong DeviceLocalAllocationBytes => Sum(entry => entry.AllocationBytes, deviceLocalOnly: true);
        public ulong DeviceLocalBlockBytes => Sum(entry => entry.BlockBytes, deviceLocalOnly: true);
        public ulong PrimaryUsageBytes => DeviceLocalBudgetBytes > 0 ? DeviceLocalUsageBytes : TotalUsageBytes;
        public ulong PrimaryBudgetBytes => DeviceLocalBudgetBytes > 0 ? DeviceLocalBudgetBytes : TotalBudgetBytes;
        public ulong PrimaryAllocationBytes => DeviceLocalBudgetBytes > 0 ? DeviceLocalAllocationBytes : TotalAllocationBytes;
        public ulong PrimaryBlockBytes => DeviceLocalBudgetBytes > 0 ? DeviceLocalBlockBytes : TotalBlockBytes;

        private ulong Sum(Func<MemoryHeapBudgetEntry, ulong> selector, bool deviceLocalOnly = false)
        {
            ulong total = 0;
            foreach (MemoryHeapBudgetEntry entry in Entries)
            {
                if (deviceLocalOnly && !entry.IsDeviceLocal)
                    continue;

                total = checked(total + selector(entry));
            }

            return total;
        }
    }

    public sealed record MemoryHeapBudgetEntry(
        uint HeapIndex,
        bool IsDeviceLocal,
        ulong UsageBytes,
        ulong BudgetBytes,
        ulong AllocationBytes,
        ulong BlockBytes,
        uint AllocationCount,
        uint BlockCount);
}
