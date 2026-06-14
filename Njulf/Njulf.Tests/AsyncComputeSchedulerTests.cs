using System;
using System.Linq;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public class AsyncComputeSchedulerTests
{
    [Test]
    public void DeviceProfile_ClassifiesDedicatedComputeAndOwnershipTransfer()
    {
        AsyncComputeDeviceProfile profile = AsyncComputeDeviceProfile.FromQueueFamilies(
            graphicsQueueFamily: 0,
            computeQueueFamily: 1,
            transferQueueFamily: 2,
            computeQueueAvailable: true,
            timelineSemaphoresSupported: true,
            timestampComputeAndGraphicsSupported: true);

        Assert.That(profile.Availability, Is.EqualTo(AsyncComputeAvailability.DedicatedComputeQueue));
        Assert.That(profile.RequiresOwnershipTransfer, Is.True);
        Assert.That(profile.Explain(AsyncComputeMode.Conservative), Does.Contain("dedicated compute queue"));
    }

    [Test]
    public void Scheduler_FallsBackToGraphicsWhenComputeQueueIsShared()
    {
        RenderGraphDeclarationPlan graph = BuildGraph();
        AsyncComputeDeviceProfile profile = AsyncComputeDeviceProfile.FromQueueFamilies(0, 0, 0, true, true, true);

        AsyncSchedulePlan plan = AsyncComputeScheduler.Build(
            graph,
            profile,
            AsyncComputeMode.Aggressive,
            new[] { Hint("Culling", workload: 1000) });

        Assert.That(plan.Passes.Single(pass => pass.PassName == "Culling").Async, Is.False);
        Assert.That(plan.Passes.Single(pass => pass.PassName == "Culling").Queue, Is.EqualTo(RenderGraphQueueClass.Graphics));
        Assert.That(plan.Diagnostic, Does.Contain("shares the graphics queue"));
    }

    [Test]
    public void ConservativeScheduler_AvoidsTinyOrImmediateWaitWork()
    {
        RenderGraphDeclarationPlan graph = BuildGraph();
        AsyncComputeDeviceProfile profile = DedicatedProfile();

        AsyncSchedulePlan plan = AsyncComputeScheduler.Build(
            graph,
            profile,
            AsyncComputeMode.Conservative,
            new[]
            {
                Hint("Culling", workload: 20),
                Hint("PostCompute", workload: 1000, immediateGraphicsConsumer: true)
            });

        Assert.That(plan.Passes.Single(pass => pass.PassName == "Culling").Async, Is.False);
        Assert.That(plan.Passes.Single(pass => pass.PassName == "PostCompute").Async, Is.False);
    }

    [Test]
    public void AggressiveScheduler_ProducesCrossQueueSyncEdges()
    {
        RenderGraphDeclarationPlan graph = BuildGraph();
        AsyncComputeDeviceProfile profile = DedicatedProfile();

        AsyncSchedulePlan plan = AsyncComputeScheduler.Build(
            graph,
            profile,
            AsyncComputeMode.Aggressive,
            new[]
            {
                Hint("Culling", workload: 1000),
                Hint("PostCompute", workload: 1000)
            });

        Assert.That(plan.Passes.Single(pass => pass.PassName == "Culling").Queue, Is.EqualTo(RenderGraphQueueClass.Compute));
        Assert.That(plan.SyncEdges, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(plan.SyncEdges.Any(edge => edge.Producer == "Culling" && edge.Consumer == "Forward"), Is.True);
        Assert.That(plan.SyncEdges.All(edge => edge.RequiresOwnershipTransfer), Is.True);
    }

    [Test]
    public void Scheduler_UsesPassMetadataWhenHintIsNotProvided()
    {
        var registry = new RenderGraphResourceRegistry();
        RenderGraphResourceHandle buffer = registry.GetOrCreateBuffer(new RenderGraphBufferDesc(
            "Visibility",
            RenderGraphResourcePersistence.External)
        {
            ByteSize = 1024,
            Usage = BufferUsageFlags.StorageBufferBit
        });

        registry.AddPass(new RenderGraphPassDesc("SkinningPass", RenderGraphQueueClass.Compute)
        {
            AsyncEligible = true,
            PreferredQueue = RenderGraphQueueClass.Compute,
            ExpectedWorkloadScore = 500,
            HasExternalSideEffect = true,
            NeverCull = true
        }.SupportsQueue(RenderGraphQueueClass.Graphics)
            .Write(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
        RenderGraphDeclarationPlan graph = registry.Compile();

        AsyncSchedulePlan plan = AsyncComputeScheduler.Build(
            graph,
            DedicatedProfile(),
            AsyncComputeMode.Aggressive,
            Array.Empty<AsyncPassSchedulingHint>());

        ScheduledPass skinning = plan.Passes.Single(pass => pass.PassName == "SkinningPass");

        Assert.Multiple(() =>
        {
            Assert.That(skinning.Async, Is.True);
            Assert.That(skinning.Queue, Is.EqualTo(RenderGraphQueueClass.Compute));
            Assert.That(plan.QueueDiagnostics.AsyncScheduledPassCount, Is.EqualTo(1));
        });
    }

    private static AsyncPassSchedulingHint Hint(
        string passName,
        int workload,
        bool bandwidthHeavy = false,
        bool immediateGraphicsConsumer = false)
    {
        return new AsyncPassSchedulingHint(
            passName,
            AsyncEligible: true,
            RenderGraphQueueClass.Compute,
            workload,
            bandwidthHeavy,
            immediateGraphicsConsumer);
    }

    private static AsyncComputeDeviceProfile DedicatedProfile() =>
        AsyncComputeDeviceProfile.FromQueueFamilies(0, 1, 0, true, true, true);

    private static RenderGraphDeclarationPlan BuildGraph()
    {
        var registry = new RenderGraphResourceRegistry();
        RenderGraphResourceHandle buffer = registry.GetOrCreateBuffer(new RenderGraphBufferDesc("Visibility", RenderGraphResourcePersistence.External)
        {
            ByteSize = 1024,
            Usage = BufferUsageFlags.StorageBufferBit
        });

        registry.AddPass(new RenderGraphPassDesc("Depth", RenderGraphQueueClass.Graphics)
            .Write(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.MeshShaderBitExt));
        registry.AddPass(new RenderGraphPassDesc("Culling", RenderGraphQueueClass.Compute)
            .ReadWrite(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .After("Depth"));
        registry.AddPass(new RenderGraphPassDesc("Forward", RenderGraphQueueClass.Graphics)
            .Read(buffer, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.MeshShaderBitExt)
            .After("Culling"));
        registry.AddPass(new RenderGraphPassDesc("PostCompute", RenderGraphQueueClass.Compute) { NeverCull = true }
            .ReadWrite(buffer, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .After("Forward")
            .After("Culling"));

        return registry.Compile();
    }
}
