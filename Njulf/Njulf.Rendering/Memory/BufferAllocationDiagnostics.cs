using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Memory;

internal static class BufferAllocationDiagnostics
{
    private const double BytesPerMebibyte = 1024.0 * 1024.0;

    internal static string BuildFailureMessage(
        ulong size,
        BufferUsageFlags usage,
        MemoryUsage memoryUsage,
        AllocationCreateFlags allocationFlags,
        string? debugName,
        MemoryBudgetCategory category,
        MemoryHeapBudgetSnapshot heapBudget)
    {
        string name = string.IsNullOrWhiteSpace(debugName)
            ? "<unnamed>"
            : debugName;
        string heapSummary = FormatHeapBudget(heapBudget);
        return FormattableString.Invariant(
            $"Failed to create buffer '{name}' (requested={FormatBytes(size)}; usage={usage}; memoryUsage={memoryUsage}; allocationFlags={allocationFlags}; category={category}; {heapSummary})");
    }

    private static string FormatHeapBudget(
        MemoryHeapBudgetSnapshot heapBudget)
    {
        if (!heapBudget.IsAvailable || heapBudget.Entries.Count == 0)
            return "heapBudget=unavailable";

        bool deviceLocalOnly = false;
        foreach (MemoryHeapBudgetEntry entry in heapBudget.Entries)
        {
            if (entry.IsDeviceLocal && entry.BudgetBytes > 0)
            {
                deviceLocalOnly = true;
                break;
            }
        }

        ulong usageBytes = 0;
        ulong budgetBytes = 0;
        ulong allocationBytes = 0;
        ulong blockBytes = 0;
        ulong allocationCount = 0;
        ulong blockCount = 0;
        int heapCount = 0;
        foreach (MemoryHeapBudgetEntry entry in heapBudget.Entries)
        {
            if (deviceLocalOnly && !entry.IsDeviceLocal)
                continue;

            usageBytes = SaturatingAdd(usageBytes, entry.UsageBytes);
            budgetBytes = SaturatingAdd(budgetBytes, entry.BudgetBytes);
            allocationBytes = SaturatingAdd(
                allocationBytes,
                entry.AllocationBytes);
            blockBytes = SaturatingAdd(blockBytes, entry.BlockBytes);
            allocationCount = SaturatingAdd(
                allocationCount,
                entry.AllocationCount);
            blockCount = SaturatingAdd(blockCount, entry.BlockCount);
            heapCount++;
        }

        ulong headroomBytes = budgetBytes > usageBytes
            ? budgetBytes - usageBytes
            : 0;
        string scope = deviceLocalOnly ? "device-local" : "all";
        return FormattableString.Invariant(
            $"heapBudget={scope} heaps={heapCount}, usage={FormatBytes(usageBytes)}, budget={FormatBytes(budgetBytes)}, estimatedHeadroom={FormatBytes(headroomBytes)}, vmaAllocationBytes={FormatBytes(allocationBytes)}, vmaBlockBytes={FormatBytes(blockBytes)}, vmaAllocationCount={allocationCount}, vmaBlockCount={blockCount}");
    }

    private static string FormatBytes(ulong bytes) =>
        FormattableString.Invariant(
            $"{bytes} bytes ({bytes / BytesPerMebibyte:F2} MiB)");

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right
            ? ulong.MaxValue
            : left + right;
}

internal sealed class BufferAllocationException : VulkanException
{
    internal BufferAllocationException(string message, Result result)
        : base(message, result)
    {
    }
}
