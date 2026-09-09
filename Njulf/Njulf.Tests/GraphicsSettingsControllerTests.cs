using Njulf.Graphics;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GraphicsSettingsControllerTests
{
    [Test]
    public async Task PresetAdmissionRejectionPreventsEveryOverride()
    {
        var settings = new RenderSettings();
        float exposure = settings.Exposure;
        var controller = new VulkanGraphicsSettingsController(settings, () => { })
        { ValidatePreset = _ => "Existing profiles exceed the preset budget." };
        var result = await controller.ApplyAsync(new() { QualityPreset = settings.QualityPreset, Exposure = 9 });
        Assert.That(result.Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rejected));
        Assert.That(result.Fields.Any(f => f.Impact == GraphicsSettingsImpact.Rejected), Is.True);
        Assert.That(settings.Exposure, Is.EqualTo(exposure));
    }
    [Test]
    public async Task PreviewIsPureAndApplyWaitsForResourcePreparation()
    {
        var settings = new RenderSettings();
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        string original = settings.ComputePersistenceSha256();
        var change = new GraphicsSettingsChange { Exposure = 2.75f, ResolutionScale = .75f };
        var preview = controller.Preview(change);
        Assert.That(settings.ComputePersistenceSha256(), Is.EqualTo(original));
        Assert.That(preview.Fields.Select(f => f.Impact), Does.Contain(GraphicsSettingsImpact.ResourceRebuild));
        var task = controller.ApplyAsync(change);
        Assert.That(task.IsCompleted, Is.False);
        controller.BeginFrame();
        Assert.That(settings.Exposure, Is.EqualTo(2.75f));
        Assert.That(task.IsCompleted, Is.False);
        controller.CompleteFrame();
        Assert.That((await task).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rebuilt));
        Assert.That(controller.IsPending, Is.False);
    }

    [Test]
    public void PresetRunsBeforeOverridesAndRestartDoesNotMutateLiveSettings()
    {
        var settings = new RenderSettings();
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        string original = settings.ComputePersistenceSha256();
        var change = new GraphicsSettingsChange { QualityPreset = RenderQualityPreset.Low, Exposure = 3.5f, ResolutionScale = .75f };
        var result = controller.ApplyAsync(change).GetAwaiter().GetResult();
        Assert.That(result.Outcome, Is.EqualTo(GraphicsSettingsOutcome.RestartRequired));
        Assert.That(result.Requested.QualityPreset, Is.EqualTo(RenderQualityPreset.Low));
        Assert.That(result.Requested.Exposure, Is.EqualTo(3.5f));
        Assert.That(result.Requested.ResolutionScale, Is.EqualTo(.75f));
        Assert.That(settings.ComputePersistenceSha256(), Is.EqualTo(original));
    }

    [Test]
    public void InvalidRequestRejectsAllFields()
    {
        var settings = new RenderSettings();
        float exposure = settings.Exposure;
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        var result = controller.Preview(new() { Exposure = 3, AntiAliasingMode = (AntiAliasingMode)999 });
        Assert.That(result.Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rejected));
        Assert.That(settings.Exposure, Is.EqualTo(exposure));
        Assert.That(controller.Preview(new() { ResolutionScale = float.NaN }).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rejected));
    }

    [Test]
    public async Task BusyCancellationAndShutdownSettleEveryRequest()
    {
        var settings = new RenderSettings();
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        using var cancellation = new CancellationTokenSource();
        var first = controller.ApplyAsync(new() { Exposure = 7 }, cancellation.Token);
        Assert.That((await controller.ApplyAsync(new() { Exposure = 8 })).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rejected));
        cancellation.Cancel();
        Assert.That(first.IsCanceled, Is.True, "Cancellation must settle even while no frames are being processed.");
        controller.BeginFrame();
        Assert.That(first.IsCanceled, Is.True);
        Assert.That(settings.Exposure, Is.Not.EqualTo(7));
        var second = controller.ApplyAsync(new() { Exposure = 9 });
        controller.Shutdown();
        Assert.That((await second).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Failed));
        Assert.That(controller.IsPending, Is.False);
    }

    [Test]
    public async Task CancellationAfterProcessingDoesNotUndoAnAppliedRequest()
    {
        var settings = new RenderSettings();
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        using var cancellation = new CancellationTokenSource();
        var task = controller.ApplyAsync(new() { Exposure = 7 }, cancellation.Token);
        controller.BeginFrame();
        cancellation.Cancel();
        controller.CompleteFrame();
        Assert.That((await task).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Applied));
        Assert.That(settings.Exposure, Is.EqualTo(7));
    }

    [Test]
    public async Task MixedShadowFlagsCanAllBeDisabledAndLegacyWritesAreVisible()
    {
        var settings = new RenderSettings();
        settings.Shadows.PointShadowsEnabled = false;
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        var task = controller.ApplyAsync(new() { ShadowsEnabled = false });
        controller.BeginFrame(); controller.CompleteFrame();
        Assert.That((await task).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rebuilt));
        Assert.That(settings.Shadows.DirectionalShadowsEnabled, Is.False);
        Assert.That(settings.Shadows.SpotShadowsEnabled, Is.False);
        settings.Exposure = 12;
        Assert.That(controller.Current.Exposure, Is.EqualTo(12));
    }

    [Test]
    public async Task DiagnosticsOnlyChangesAreReceiptedWithoutChangingPersistenceSchema()
    {
        var settings = new RenderSettings();
        var controller = new VulkanGraphicsSettingsController(settings, () => { });
        var task = controller.ApplyAsync(new() { GpuTimingEnabled = true, CpuDiagnosticSnapshotsEnabled = true });
        controller.BeginFrame(); controller.CompleteFrame();
        Assert.That((await task).Outcome, Is.EqualTo(GraphicsSettingsOutcome.Applied));
        Assert.That(controller.Current.CpuDiagnosticSnapshotsEnabled, Is.True);
        Assert.That(controller.Preview(new() { GpuTimingEnabled = true }).Outcome, Is.EqualTo(GraphicsSettingsOutcome.NoChange));
        Assert.That(settings.CreateSnapshot().ComputePersistenceSha256(), Is.EqualTo(settings.ComputePersistenceSha256()));
    }
}
