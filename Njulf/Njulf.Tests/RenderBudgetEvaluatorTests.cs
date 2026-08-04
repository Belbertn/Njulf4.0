using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderBudgetEvaluatorTests
{
    [Test]
    public void Classification_UsesWarningAndFailureThresholds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RenderBudgetEvaluator.Classify(84, 100), Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(RenderBudgetEvaluator.Classify(86, 100), Is.EqualTo(RenderBudgetStatus.Warning));
            Assert.That(RenderBudgetEvaluator.Classify(101, 100), Is.EqualTo(RenderBudgetStatus.OverBudget));
        });
    }

    [Test]
    public void Evaluation_AcceptsSimpleDdgiDiagnostics()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 32,
            SimpleDdgiAtlasBytes = 1024
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        Assert.That(snapshot.Profile.Kind, Is.EqualTo(profile.Kind));
    }
}
