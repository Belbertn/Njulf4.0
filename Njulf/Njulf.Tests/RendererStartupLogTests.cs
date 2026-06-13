using System;
using System.IO;
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
}
