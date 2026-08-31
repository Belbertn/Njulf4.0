using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiWarningEvaluatorTests
{
    [Test]
    public void Evaluate_SingleZeroSampleDoesNotBecomeBlackFrameOrSupportHole()
    {
        var evaluator = new GiWarningEvaluator();

        GiWarningEvaluationResult result = evaluator.Evaluate(CreateSteadyDiagnostics() with
        {
            DdgiForwardSimplePathSampleCount = 1_000,
            DdgiForwardZeroFinalIndirectCount = 1,
            DdgiForwardOutOfGridSampleCount = 1
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.BlackFrame.IsAvailable, Is.True);
            Assert.That(result.BlackFrame.LargeAreaBlackout, Is.False);
            Assert.That(result.BlackFrame.SupportHole, Is.False);
            Assert.That(result.Warnings, Is.Empty);
        });
    }

    [Test]
    public void Evaluate_PersistentSupportHoleDoesNotBecomeBlackFrame()
    {
        var evaluator = new GiWarningEvaluator();
        RendererDiagnostics diagnostics = CreateSteadyDiagnostics() with
        {
            DdgiForwardSimplePathSampleCount = 100,
            DdgiForwardZeroFinalIndirectCount = 10,
            DdgiForwardOutOfGridSampleCount = 30
        };

        GiWarningEvaluationResult first = evaluator.Evaluate(diagnostics);
        GiWarningEvaluationResult second = evaluator.Evaluate(diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(first.BlackFrame.SupportHole, Is.False);
            Assert.That(second.BlackFrame.SupportHole, Is.True);
            Assert.That(second.BlackFrame.LargeAreaBlackout, Is.False);
            Assert.That(second.Warnings.Single().Code, Is.EqualTo(GiDiagnosticWarningCode.SupportHole));
            Assert.That(second.Warnings.Single().ObservedValue, Is.EqualTo(0.3).Within(0.0001));
            Assert.That(second.Warnings.Single().Threshold, Is.EqualTo(GiWarningEvaluator.SupportHoleFractionThreshold));
        });
    }

    [Test]
    public void Evaluate_LargePersistentCausalBlackoutRequiresConsecutiveFrames()
    {
        var evaluator = new GiWarningEvaluator();
        RendererDiagnostics diagnostics = CreateSteadyDiagnostics() with
        {
            DdgiForwardSimplePathSampleCount = 1_000,
            DdgiForwardZeroFinalIndirectCount = 900,
            DdgiForwardZeroDdgiAndZeroIblCount = 900,
            SimpleDdgiZeroIrradianceSampleCount = 900,
            SimpleDdgiGatherSampleCount = 1_000
        };

        GiWarningEvaluationResult first = evaluator.Evaluate(diagnostics);
        GiWarningEvaluationResult second = evaluator.Evaluate(diagnostics);
        GiWarningEvaluationResult third = evaluator.Evaluate(diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(first.BlackFrame.LargeAreaBlackout, Is.False);
            Assert.That(second.BlackFrame.LargeAreaBlackout, Is.False);
            Assert.That(third.BlackFrame.LargeAreaBlackout, Is.True);
            Assert.That(third.BlackFrame.ConsecutiveLargeAreaFrames, Is.EqualTo(3));
            Assert.That(third.Warnings.Single(warning => warning.Code == GiDiagnosticWarningCode.LargeAreaBlackout).Severity,
                Is.EqualTo(GiDiagnosticSeverity.Error));
        });
    }

    [Test]
    public void Evaluate_TransientRecenterResetsBlackoutPersistence()
    {
        var evaluator = new GiWarningEvaluator();
        RendererDiagnostics black = CreateSteadyDiagnostics() with
        {
            DdgiForwardSimplePathSampleCount = 1_000,
            DdgiForwardZeroFinalIndirectCount = 900,
            DdgiForwardZeroDdgiAndZeroIblCount = 900
        };

        evaluator.Evaluate(black);
        evaluator.Evaluate(black);
        GiWarningEvaluationResult transient = evaluator.Evaluate(black with { SimpleDdgiRecentered = 1 });
        GiWarningEvaluationResult resumed = evaluator.Evaluate(black);

        Assert.Multiple(() =>
        {
            Assert.That(transient.BlackFrame.TransientState, Is.True);
            Assert.That(transient.BlackFrame.TransientStateReason, Does.Contain("recentered"));
            Assert.That(transient.BlackFrame.LargeAreaBlackout, Is.False);
            Assert.That(resumed.BlackFrame.ConsecutiveLargeAreaFrames, Is.EqualTo(1));
            Assert.That(resumed.BlackFrame.LargeAreaBlackout, Is.False);
        });
    }

    [Test]
    public void Evaluate_CompatibleToroidalScrollRemainsObservableAsSteadyState()
    {
        var evaluator = new GiWarningEvaluator();
        RendererDiagnostics black = CreateSteadyDiagnostics() with
        {
            DdgiForwardSimplePathSampleCount = 1_000,
            DdgiForwardZeroFinalIndirectCount = 900,
            DdgiForwardZeroDdgiAndZeroIblCount = 900
        };

        evaluator.Evaluate(black);
        evaluator.Evaluate(black);
        GiWarningEvaluationResult scroll = evaluator.Evaluate(black with
        {
            SimpleDdgiRecentered = 1,
            SimpleDdgiAtlasPreservedOnRecenter = 1,
            SimpleDdgiScrollCommittedCascadeCount = 1
        });

        Assert.Multiple(() =>
        {
            Assert.That(scroll.BlackFrame.TransientState, Is.False);
            Assert.That(scroll.BlackFrame.ConsecutiveLargeAreaFrames, Is.EqualTo(3));
            Assert.That(scroll.BlackFrame.LargeAreaBlackout, Is.True);
        });
    }

    [Test]
    public void Evaluate_UnavailableCountersAreNotReportedAsHealthyZeros()
    {
        var evaluator = new GiWarningEvaluator();

        GiWarningEvaluationResult result = evaluator.Evaluate(RendererDiagnostics.Empty with
        {
            GlobalIlluminationDdgiActive = 1,
            DdgiDetailedCountersEnabled = 1,
            DdgiInvestigationCountersReadbackValid = 0
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.BlackFrame.IsAvailable, Is.False);
            Assert.That(result.BlackFrame.UnavailableReason, Does.Contain("unavailable"));
            Assert.That(result.Warnings.Single().Code, Is.EqualTo(GiDiagnosticWarningCode.InvestigationCountersUnavailable));
            Assert.That(result.Warnings.Single().Freshness, Is.EqualTo(GiMetricFreshness.Unavailable));
        });
    }

    private static RendererDiagnostics CreateSteadyDiagnostics() => RendererDiagnostics.Empty with
    {
        GlobalIlluminationDdgiActive = 1,
        DdgiDetailedCountersEnabled = 1,
        DdgiInvestigationCountersReadbackValid = 1,
        DdgiWarmupState = DdgiRuntimeWarmupState.SteadyState
    };
}
