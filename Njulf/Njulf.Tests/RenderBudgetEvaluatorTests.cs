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

    [Test]
    public void Evaluation_SumsExplicitPackedResourcesAndActualMirrorAllocation()
    {
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        SimpleDdgiStorageDiagnostics storage =
            SimpleDdgiStorageDiagnostics.Unavailable with
            {
                IsAvailable = true,
                CanonicalIrradianceBytes = 100UL,
                CanonicalVisibilityBytes = 200UL,
                SourceCacheBytes = 400UL,
                MirrorTotalBytes = 500UL,
                MirrorAllocatedBytes = 550UL
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            DdgiProbeCount = 32,
            DdgiAtlasMemoryBudgetBytes = 10_000UL,
            // Deliberately misleading compatibility aggregates: the explicit
            // storage schema must be the sole authority when it is available.
            SimpleDdgiAtlasBytes = 99_999UL,
            SimpleDdgiTransportIrradianceAtlasBytes = 300UL,
            SimpleDdgiTransportSourceCacheBytes = 88_888UL,
            SimpleDdgiStorage = storage
        };

        RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0,
                [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));
        BudgetMetric metric = snapshot.Metrics.Single(entry =>
            entry.Name == "DDGI atlas memory");

        Assert.That(metric.Value, Is.EqualTo(1_550.0));
    }
}
