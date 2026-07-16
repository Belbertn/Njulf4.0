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
        CountingPass pass = CreateUninitializedPass();
        graph.AddPass(pass);

        graph.Cleanup();
        graph.Cleanup();
        graph.Dispose();

        Assert.That(pass.CleanupCount, Is.EqualTo(1));
    }

    private static CountingPass CreateUninitializedPass()
    {
        var pass = (CountingPass)RuntimeHelpers.GetUninitializedObject(typeof(CountingPass));
        FieldInfo nameField = typeof(RenderPassBase).GetField(
            "<Name>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderPassBase.Name backing field was not found.");
        nameField.SetValue(pass, "CountingPass");
        return pass;
    }

    private sealed class CountingPass : RenderPassBase
    {
        private CountingPass()
            : base("unused", null!, null!, null!)
        {
        }

        public int CleanupCount { get; private set; }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
        }

        public override void Cleanup()
        {
            CleanupCount++;
        }
    }
}
