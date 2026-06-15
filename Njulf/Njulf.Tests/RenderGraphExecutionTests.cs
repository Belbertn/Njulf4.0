using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderGraphExecutionTests
{
    [Test]
    public void Execute_InvokesCompiledBarriersForSkippedPass()
    {
        using var graph = new RenderGraph();
        graph.AddPass(new SkippedPass("SkippedPass", RenderGraphQueueClass.Graphics));
        graph.InitializeDeclarations();
        var phases = new List<RenderGraphBarrierExecutionPhase>();

        graph.Execute(
            default,
            frameIndex: 0,
            new SceneRenderingData(),
            executeCompiledBarriers: (_, passName, queue, phase) =>
            {
                Assert.That(passName, Is.EqualTo("SkippedPass"));
                Assert.That(queue, Is.EqualTo(RenderGraphQueueClass.Graphics));
                phases.Add(phase);
            });

        Assert.That(phases, Is.EqualTo(new[]
        {
            RenderGraphBarrierExecutionPhase.BeforePass,
            RenderGraphBarrierExecutionPhase.AfterPass
        }));
    }

    [Test]
    public void ExecuteQueue_InvokesCompiledBarriersForSkippedPass()
    {
        using var graph = new RenderGraph();
        graph.AddPass(new SkippedPass("SkippedPass", RenderGraphQueueClass.Compute));
        graph.InitializeDeclarations();
        var phases = new List<RenderGraphBarrierExecutionPhase>();

        graph.ExecuteQueue(
            default,
            RenderGraphQueueClass.Compute,
            frameIndex: 0,
            new SceneRenderingData(),
            executeCompiledBarriers: (_, passName, queue, phase) =>
            {
                Assert.That(passName, Is.EqualTo("SkippedPass"));
                Assert.That(queue, Is.EqualTo(RenderGraphQueueClass.Compute));
                phases.Add(phase);
            });

        Assert.That(phases, Is.EqualTo(new[]
        {
            RenderGraphBarrierExecutionPhase.BeforePass,
            RenderGraphBarrierExecutionPhase.AfterPass
        }));
    }

    private sealed class SkippedPass : RenderPassBase
    {
        private readonly RenderGraphQueueClass _queue;

        public SkippedPass(string name, RenderGraphQueueClass queue)
            : base(name, Stub<VulkanContext>(), Stub<SwapchainManager>(), Stub<BindlessHeap>())
        {
            _queue = queue;
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) => false;

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            resources.AddPass(new RenderGraphPassDesc(Name, _queue)
            {
                HasExternalSideEffect = true
            });
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            throw new InvalidOperationException("Skipped test pass should not execute.");
        }

        private static T Stub<T>() where T : class
        {
            return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        }
    }
}
