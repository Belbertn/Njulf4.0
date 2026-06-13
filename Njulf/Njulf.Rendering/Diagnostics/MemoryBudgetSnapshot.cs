using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record MemoryBudgetSnapshot(
        ulong TotalTrackedBytes,
        ulong BudgetBytes,
        IReadOnlyList<MemoryBudgetEntry> Entries)
    {
        public static MemoryBudgetSnapshot Empty { get; } = new(0, 0, Array.Empty<MemoryBudgetEntry>());
    }

    public sealed record MemoryBudgetEntry(
        MemoryBudgetCategory Category,
        ulong Bytes,
        int AllocationCount,
        string Description);
}
