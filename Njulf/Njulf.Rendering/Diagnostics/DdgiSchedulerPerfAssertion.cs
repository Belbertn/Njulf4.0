using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record DdgiSchedulerPerfAssertionResult(
        bool Passed,
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Warnings);

    public static class DdgiSchedulerPerfAssertion
    {
        public const long DefaultGpuModeCpuSchedulerBudgetMicroseconds = 2_000;

        public static DdgiSchedulerPerfAssertionResult Evaluate(
            RendererDiagnostics diagnostics,
            bool warmedUp,
            long gpuModeCpuSchedulerBudgetMicroseconds = DefaultGpuModeCpuSchedulerBudgetMicroseconds)
        {
            if (diagnostics == null)
                throw new System.ArgumentNullException(nameof(diagnostics));

            var failures = new List<string>();
            var warnings = new List<string>();
            bool gpuSchedulerMode = diagnostics.DdgiSchedulerMode is DdgiSchedulerMode.Gpu or DdgiSchedulerMode.CpuGpuCompare;

            if (gpuSchedulerMode && warmedUp && diagnostics.CpuDdgiSchedulerMicroseconds > gpuModeCpuSchedulerBudgetMicroseconds)
            {
                failures.Add(
                    $"CpuDdgiSchedulerMicroseconds {diagnostics.CpuDdgiSchedulerMicroseconds} exceeds {gpuModeCpuSchedulerBudgetMicroseconds} in GPU scheduler mode.");
            }

            if (gpuSchedulerMode && diagnostics.GpuTimingValid == 0)
                warnings.Add("GpuTimingValid is 0; GPU scheduler timestamp assertions are unavailable.");

            if (gpuSchedulerMode && warmedUp && diagnostics.DdgiSchedulerP95OverBudget != 0)
                failures.Add("DdgiSchedulerP95OverBudget is set after GPU scheduler warmup.");

            return new DdgiSchedulerPerfAssertionResult(failures.Count == 0, failures, warnings);
        }
    }
}
