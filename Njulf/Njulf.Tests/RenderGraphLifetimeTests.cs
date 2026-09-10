using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderGraphLifetimeTests
{
    [Test]
    public void CleanupIsIdempotentAcrossExplicitCleanupAndDispose()
    {
        var graph = new RenderGraph();
        var pass = new CountingPipelinePass("CountingPass");
        graph.AddPass(pass);

        graph.Cleanup();
        graph.Cleanup();
        graph.Dispose();

        Assert.That(pass.Cleanups, Is.EqualTo(1));
    }

}
