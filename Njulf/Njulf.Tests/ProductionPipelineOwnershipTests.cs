using System.Runtime.CompilerServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProductionPipelineOwnershipTests
{
    [TestCase(false), TestCase(true)]
    public void InitializationFailure_DoesNotPublishReadinessOrOverwriteItsResources(bool failGraph)
    {
        var owner = new ProductionPipelineOwner();
        var failure = new InvalidOperationException("initialization");
        int constructions = 0, graphInitializations = 0;
        void Create() { constructions++; if (!failGraph) throw failure; }
        void InitializeGraph() { graphInitializations++; Assert.That(owner.IsReady, Is.False); throw failure; }
        Assert.That(Assert.Throws<InvalidOperationException>(() => owner.InitializeStages(Create, InitializeGraph)), Is.SameAs(failure));
        Assert.That(owner.IsReady, Is.False);
        Assert.That(Assert.Throws<InvalidOperationException>(() => owner.InitializeStages(Create, InitializeGraph)), Is.SameAs(failure));
        Assert.That((constructions, graphInitializations), Is.EqualTo((1, failGraph ? 1 : 0)));
        owner.CleanupGraph(new RenderGraph());
        Assert.That(() => owner.InitializeStages(Create, InitializeGraph), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void SuccessfulInitialization_PublishesOnlyAfterBothStages_AndRunsOnce()
    {
        var owner = new ProductionPipelineOwner();
        var order = new List<string>();
        owner.InitializeStages(() => { Assert.That(owner.IsReady, Is.False); order.Add("create"); },
            () => { Assert.That(owner.IsReady, Is.False); order.Add("initialize"); });
        owner.InitializeStages(() => Assert.Fail("repeated construction"), () => Assert.Fail("repeated initialization"));
        Assert.That(owner.IsReady, Is.True);
        Assert.That(order, Is.EqualTo(new[] { "create", "initialize" }));
        owner.CleanupGraph(new RenderGraph());
        Assert.That(owner.IsReady, Is.False);
    }

    [Test]
    public void PartialRegistration_LeavesEachPassWithExactlyOneCleanupOwner()
    {
        var construction = new ProductionPassOwnership();
        var graph = new RenderGraph();
        var declaration = ProductionRenderPipelineDeclaration.Instance;
        AdvancedGiRenderGraphModes modes = default;
        string firstName = declaration.CreatePassOrder(modes)[0];
        var registered = construction.Track(new CountingPipelinePass(firstName));
        var pending = construction.Track(new CountingPipelinePass("pending"));
        // Fail after the first handoff because the next declared pass is missing.
        Assert.Throws<InvalidOperationException>(() => declaration.RegisterPasses(graph,
            new Dictionary<string, RenderPassBase> { [firstName] = registered }, modes, construction.Register));
        construction.CleanupUnregistered();
        construction.CleanupUnregistered();
        Assert.That((registered.Cleanups, pending.Cleanups), Is.EqualTo((0, 1)));
        graph.Cleanup(); graph.Dispose();
        Assert.That((registered.Cleanups, pending.Cleanups), Is.EqualTo((1, 1)));
    }

    [Test]
    public void UnregisteredCleanup_RetriesFailureAndKeepsSuccessfulReleasesComplete()
    {
        var construction = new ProductionPassOwnership();
        var first = construction.Track(new CountingPipelinePass("first") { FailCleanup = true });
        var second = construction.Track(new CountingPipelinePass("second"));
        Assert.Throws<AggregateException>(construction.CleanupUnregistered);
        Assert.That((first.Cleanups, second.Cleanups), Is.EqualTo((0, 1)));
        first.FailCleanup = false;
        construction.CleanupUnregistered(); construction.CleanupUnregistered();
        Assert.That((first.Cleanups, second.Cleanups), Is.EqualTo((1, 1)));
    }

    [Test]
    public void GraphInitializationFailure_CleansBothInitializedAndPartiallyInitializedPasses()
    {
        var graph = new RenderGraph();
        var first = new CountingPipelinePass("first");
        var failing = new CountingPipelinePass("failing") { FailInitialize = true };
        graph.AddPass(first); graph.AddPass(failing);
        graph.DeclarePassResources("first"); graph.DeclarePassResources("failing");
        var failure = Assert.Throws<InvalidOperationException>(() => graph.Initialize());
        Assert.That(failure, Is.SameAs(failing.InitializationFailure));
        graph.Cleanup(); graph.Dispose();
        Assert.That((first.Initializations, failing.Initializations, first.Cleanups, failing.Cleanups), Is.EqualTo((1, 1, 1, 1)));
    }

    [Test]
    public void GraphCleanupFailure_DoesNotSkipFollowingPasses_AndDisposeRetries()
    {
        var graph = new RenderGraph();
        var first = new CountingPipelinePass("first") { FailCleanup = true };
        var second = new CountingPipelinePass("second");
        graph.AddPass(first); graph.AddPass(second);
        Assert.Throws<AggregateException>(graph.Dispose);
        Assert.That((first.Cleanups, second.Cleanups), Is.EqualTo((0, 1)));
        first.FailCleanup = false;
        graph.Dispose(); graph.Dispose(); graph.Cleanup();
        Assert.That((first.Cleanups, second.Cleanups), Is.EqualTo((1, 1)));
    }

    [Test]
    public void StandalonePassDispose_RetriesFailedCleanup()
    {
        var pass = new CountingPipelinePass("standalone") { FailCleanup = true };
        Assert.Throws<InvalidOperationException>(pass.Dispose);
        pass.FailCleanup = false;
        pass.Dispose(); pass.Dispose();
        Assert.That(pass.Cleanups, Is.EqualTo(1));
    }
}

// These stand-ins satisfy the borrowed constructor contracts; this pass never calls a native service.
// No private fields or source shapes are used to observe ownership or initialization.
internal sealed class CountingPipelinePass(string name) : RenderPassBase(name,
    (VulkanContext)RuntimeHelpers.GetUninitializedObject(typeof(VulkanContext)),
    (SwapchainManager)RuntimeHelpers.GetUninitializedObject(typeof(SwapchainManager)),
    (BindlessHeap)RuntimeHelpers.GetUninitializedObject(typeof(BindlessHeap)))
{
    internal int Cleanups, Initializations;
    internal bool FailCleanup, FailInitialize;
    internal readonly Exception InitializationFailure = new InvalidOperationException("pass initialization");
    public override void Initialize() { Initializations++; if (FailInitialize) throw InitializationFailure; }
    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData) { }
    public override void Cleanup() { if (FailCleanup) throw new InvalidOperationException("pass cleanup"); Cleanups++; }
}
