using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiSchedulerPerfAssertionTests
{
    [Test]
    public void Evaluate_GpuModeWithoutGpuTimingWarnsButDoesNotFail()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            DdgiSchedulerMode = DdgiSchedulerMode.Gpu,
            CpuDdgiSchedulerMicroseconds = 250,
            DdgiSchedulerP95OverBudget = 0,
            GpuTimingValid = 0
        };

        DdgiSchedulerPerfAssertionResult result = DdgiSchedulerPerfAssertion.Evaluate(diagnostics, warmedUp: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True);
            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Warnings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Evaluate_GpuModeAfterWarmupFailsCpuSchedulerAndP95BudgetViolations()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            DdgiSchedulerMode = DdgiSchedulerMode.Gpu,
            CpuDdgiSchedulerMicroseconds = 2_001,
            DdgiSchedulerP95OverBudget = 1,
            GpuTimingValid = 1
        };

        DdgiSchedulerPerfAssertionResult result = DdgiSchedulerPerfAssertion.Evaluate(diagnostics, warmedUp: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.Failures, Has.Count.EqualTo(2));
            Assert.That(result.Warnings, Is.Empty);
        });
    }

    [Test]
    public void Evaluate_CpuReferenceModeIgnoresGpuSchedulerGates()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            DdgiSchedulerMode = DdgiSchedulerMode.CpuReference,
            CpuDdgiSchedulerMicroseconds = 90_000,
            DdgiSchedulerP95OverBudget = 1,
            GpuTimingValid = 0
        };

        DdgiSchedulerPerfAssertionResult result = DdgiSchedulerPerfAssertion.Evaluate(diagnostics, warmedUp: true);

        Assert.That(result.Passed, Is.True);
    }
}
