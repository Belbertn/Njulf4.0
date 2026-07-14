using System;
using System.Reflection;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class GpuMemoryDiagnosticsFormattingTests
    {
        [Test]
        public void FormatGpuMemoryBudget_DistinguishesDriverHeapAndTrackedAllocationBudgets()
        {
            string result = Format(RendererDiagnostics.Empty with
            {
                GpuMemoryBudgetQueryAvailable = 1,
                GpuMemoryBudgetStatus = RenderBudgetStatus.WithinBudget,
                ActualGpuMemoryUsageBytes = 50,
                ActualGpuMemoryBudgetBytes = 100,
                TrackedGpuMemoryBytes = 101,
                GpuMemoryBudgetBytes = 100
            });

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("heapMemory=WithinBudget:"));
                Assert.That(result, Does.Contain("trackedMemory=OverBudget:"));
            });
        }

        [Test]
        public void FormatGpuMemoryBudget_UsesTrackedLabelWhenDriverHeapQueryIsUnavailable()
        {
            string result = Format(RendererDiagnostics.Empty with
            {
                GpuMemoryBudgetQueryAvailable = 0,
                GpuMemoryBudgetStatus = RenderBudgetStatus.Warning,
                TrackedGpuMemoryBytes = 86,
                GpuMemoryBudgetBytes = 100
            });

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.StartWith("trackedMemory=Warning:"));
                Assert.That(result, Does.Not.Contain("heapMemory="));
            });
        }

        private static string Format(RendererDiagnostics diagnostics)
        {
            Type type = typeof(SampleBenchmarkOptions).Assembly.GetType(
                "NjulfHelloGame.SampleDiagnosticsReporter",
                throwOnError: true)!;
            MethodInfo method = type.GetMethod("FormatGpuMemoryBudget", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(type.FullName, "FormatGpuMemoryBudget");

            return (string)method.Invoke(null, [diagnostics])!;
        }
    }
}
