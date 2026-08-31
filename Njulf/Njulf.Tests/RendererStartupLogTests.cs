using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Core.Interfaces;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererStartupLogTests
{
    [Test]
    public void WritesStartedSucceededAndFailedSteps()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"startup-{Guid.NewGuid():N}.jsonl");

        using (var log = new RendererStartupLog(path, new[] { "--smoke-mode", "startup" }))
        {
            log.StepStarted("Game.CreateWindow");
            log.StepSucceeded("Game.CreateWindow");
            log.StepStarted("VulkanContext.CreateInstance");
            log.StepFailed("VulkanContext.CreateInstance", new InvalidOperationException("missing layer"));
        }

        string text = File.ReadAllText(path);
        Assert.That(text, Does.Contain("Game.CreateWindow"));
        Assert.That(text, Does.Contain("Started"));
        Assert.That(text, Does.Contain("Succeeded"));
        Assert.That(text, Does.Contain("Failed"));
        Assert.That(text, Does.Contain("missing layer"));
    }

    [Test]
    public void SnapshotsIncludeActivePipelineTelemetryAndAreThrottled()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"startup-snapshots-{Guid.NewGuid():N}.jsonl");

        using (var log = new RendererStartupLog(path))
        {
            var initial = new RendererStartupSnapshot(
                RendererStartupPhase.ProductionPreparing,
                ElapsedMicroseconds: 2_000_000,
                PhaseElapsedMicroseconds: 1_000_000,
                BootstrapPresented: true,
                ScenePresented: true,
                FullQualityPresented: false,
                PipelinesCompleted: 17,
                Detail: "preparing")
            {
                ActivePipelineCount = 2,
                OldestActivePipelineMicroseconds = 750_000,
                ActivePipelineSummary =
                    "ForwardPlusPass:ddgi_simple_receiver_cache_b1.comp.spv"
            };
            log.WriteSnapshot(initial);
            log.WriteSnapshot(initial with
            {
                ElapsedMicroseconds = 11_999_999,
                PipelinesCompleted = 18
            });
            log.WriteSnapshot(initial with
            {
                ElapsedMicroseconds = 12_000_000,
                PipelinesCompleted = 19
            });
            log.WriteSnapshot(initial with
            {
                Phase = RendererStartupPhase.FullQuality,
                ElapsedMicroseconds = 12_000_001,
                FullQualityPresented = true,
                ActivePipelineCount = 0,
                OldestActivePipelineMicroseconds = 0,
                ActivePipelineSummary = string.Empty
            });
        }

        JsonElement[] snapshots = File.ReadLines(path)
            .Select(line => JsonDocument.Parse(line))
            .Where(document =>
                document.RootElement.GetProperty("kind").GetString() ==
                "snapshot")
            .Select(document => document.RootElement.Clone())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(snapshots, Has.Length.EqualTo(3));
            Assert.That(
                snapshots[0].GetProperty("activePipelineCount").GetInt32(),
                Is.EqualTo(2));
            Assert.That(
                snapshots[0]
                    .GetProperty("oldestActivePipelineMicroseconds")
                    .GetInt64(),
                Is.EqualTo(750_000));
            Assert.That(
                snapshots[0].GetProperty("activePipelineSummary").GetString(),
                Does.Contain("ddgi_simple_receiver_cache_b1.comp.spv"));
            Assert.That(
                snapshots[1].GetProperty("pipelinesCompleted").GetUInt64(),
                Is.EqualTo(19));
            Assert.That(
                snapshots[2].GetProperty("phase").GetString(),
                Is.EqualTo(nameof(RendererStartupPhase.FullQuality)));
        });
    }
}
