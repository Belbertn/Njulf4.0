using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Tests;

[TestFixture]
public sealed class BufferAllocationDiagnosticsTests
{
    [Test]
    public void AvailableBudget_ReportsRequestAndDeviceLocalHeapState()
    {
        var budget = new MemoryHeapBudgetSnapshot(
            true,
            new[]
            {
                new MemoryHeapBudgetEntry(
                    0,
                    true,
                    800,
                    1_000,
                    700,
                    900,
                    3,
                    2),
                new MemoryHeapBudgetEntry(
                    1,
                    false,
                    4_000,
                    8_000,
                    3_000,
                    5_000,
                    7,
                    5)
            });

        string message =
            BufferAllocationDiagnostics.BuildFailureMessage(
                1024UL * 1024UL,
                BufferUsageFlags.StorageBufferBit,
                MemoryUsage.AutoPreferDevice,
                default,
                "Meshlet Triangle Buffer",
                MemoryBudgetCategory.MeshBuffers,
                budget);

        Assert.Multiple(() =>
        {
            Assert.That(
                message,
                Does.Contain("Meshlet Triangle Buffer"));
            Assert.That(
                message,
                Does.Contain("1048576 bytes (1.00 MiB)"));
            Assert.That(
                message,
                Does.Contain("category=MeshBuffers"));
            Assert.That(
                message,
                Does.Contain("heapBudget=device-local heaps=1"));
            Assert.That(message, Does.Contain("usage=800 bytes"));
            Assert.That(message, Does.Contain("budget=1000 bytes"));
            Assert.That(
                message,
                Does.Contain("estimatedHeadroom=200 bytes"));
            Assert.That(
                message,
                Does.Contain("vmaAllocationCount=3"));
            Assert.That(
                message,
                Does.Contain("vmaBlockCount=2"));
            Assert.That(message, Does.Not.Contain("usage=4000 bytes"));
        });
    }

    [Test]
    public void UnavailableBudget_StillReportsTheAllocationRequest()
    {
        string message =
            BufferAllocationDiagnostics.BuildFailureMessage(
                64,
                BufferUsageFlags.TransferSrcBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit,
                null,
                MemoryBudgetCategory.StagingBuffers,
                MemoryHeapBudgetSnapshot.Unavailable);

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("'<unnamed>'"));
            Assert.That(message, Does.Contain("requested=64 bytes"));
            Assert.That(
                message,
                Does.Contain("category=StagingBuffers"));
            Assert.That(
                message,
                Does.Contain("heapBudget=unavailable"));
        });
    }
}
