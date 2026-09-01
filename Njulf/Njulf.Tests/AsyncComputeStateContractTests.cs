using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Tests;

[TestFixture]
public sealed class AsyncComputeStateContractTests
{
    [Test]
    public void Projection_DiscardRestoresCommittedStateAndCommitPublishesAtomically()
    {
        var bindings = new RenderGraphResourceBindings();
        RenderGraphConcreteResourceBinding binding = RenderGraphConcreteResourceBinding.ForBuffer(
            RenderGraphResourceId.SceneSubmissionBuffers,
            "projection buffer",
            new Buffer { Handle = 501 },
            byteSize: 1024,
            permittedQueueFamilies: new uint[] { 0, 1 },
            initialOwnerQueueFamily: 0,
            allocationGeneration: 501);
        bindings.Replace(new[] { binding });

        var projection = new AsyncComputeResourceStateProjection(4);
        Assert.That(projection.Begin(bindings), Is.True);
        Assert.That(projection.TryTransition(
            binding.AllocationIdentity,
            bindings.Generation,
            AsyncComputeQueue.Compute,
            ownerQueueFamily: 1,
            ImageLayout.Undefined,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            out _), Is.True);
        Assert.That(projection.TryGet(binding.AllocationIdentity, committed: true, out _), Is.False);

        projection.Discard();
        Assert.Multiple(() =>
        {
            Assert.That(projection.TryGet(binding.AllocationIdentity, committed: false, out _), Is.False);
            Assert.That(projection.CommittedPlanGeneration, Is.Zero);
        });

        Assert.That(projection.Begin(bindings), Is.True);
        Assert.That(projection.TryTransition(
            binding.AllocationIdentity,
            bindings.Generation,
            AsyncComputeQueue.Compute,
            ownerQueueFamily: 1,
            ImageLayout.Undefined,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            out _), Is.True);
        Assert.That(projection.Commit(bindings.Generation), Is.True);
        Assert.That(projection.TryGet(binding.AllocationIdentity, committed: true,
            out AsyncComputeProjectedResourceState committed), Is.True);
        Assert.That(committed.LastQueue, Is.EqualTo(AsyncComputeQueue.Compute));
    }

    [Test]
    public void Projection_NewPlanDiscardDoesNotErasePriorCommittedBindings()
    {
        var bindings = new RenderGraphResourceBindings();
        RenderGraphConcreteResourceBinding first = CreateBuffer(601);
        bindings.Replace(new[] { first });
        var projection = new AsyncComputeResourceStateProjection(4);
        Assert.That(projection.Begin(bindings), Is.True);
        Assert.That(projection.Commit(bindings.Generation), Is.True);

        RenderGraphConcreteResourceBinding second = CreateBuffer(602);
        bindings.Replace(new[] { second });
        Assert.That(projection.Begin(bindings), Is.True);
        projection.Discard();

        Assert.Multiple(() =>
        {
            Assert.That(projection.TryGet(first.AllocationIdentity, committed: false, out _), Is.True);
            Assert.That(projection.TryGet(second.AllocationIdentity, committed: false, out _), Is.False);
            Assert.That(projection.CommittedPlanGeneration, Is.Not.EqualTo(bindings.Generation));
        });
    }

    [Test]
    public void Projection_GrowsAtPlanGenerationBoundaryAndReusesExactAliases()
    {
        var bindings = new RenderGraphResourceBindings();
        RenderGraphConcreteResourceBinding first = CreateBuffer(701);
        RenderGraphConcreteResourceBinding alias = first with
        {
            Resource = RenderGraphResourceId.MaterialBuffers,
            Name = "alias-701"
        };
        RenderGraphConcreteResourceBinding second = CreateBuffer(702);
        RenderGraphConcreteResourceBinding third = CreateBuffer(703);
        bindings.Replace(new[] { first, alias, second, third });

        var projection = new AsyncComputeResourceStateProjection(1);
        Assert.That(projection.Begin(bindings), Is.True);
        int grownCapacity = projection.Capacity;

        Assert.Multiple(() =>
        {
            Assert.That(grownCapacity, Is.GreaterThanOrEqualTo(4));
            Assert.That(projection.Count, Is.EqualTo(3));
            Assert.That(projection.Begin(bindings), Is.True);
            Assert.That(projection.Capacity, Is.EqualTo(grownCapacity));
            Assert.That(projection.Count, Is.EqualTo(3));
        });
    }

    [Test]
    public void VariantCache_RejectsUnacceptedOrStalePlansAndEvictsLeastRecentlyUsed()
    {
        var cache = new AsyncComputePlanVariantCache(2);
        AsyncComputePlanVariantKey first = Key(1UL, 11UL);
        AsyncComputePlanVariantKey second = Key(2UL, 12UL);
        AsyncComputePlanVariantKey third = Key(3UL, 13UL);
        cache.Add(first, Plan(accepted: true, resourceGeneration: 1UL));
        cache.Add(second, Plan(accepted: true, resourceGeneration: 2UL));
        Assert.That(cache.TryGet(first, out _), Is.True);
        cache.Add(third, Plan(accepted: true, resourceGeneration: 3UL));

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet(first, out _), Is.True);
            Assert.That(cache.TryGet(second, out _), Is.False);
            Assert.That(cache.EvictionCount, Is.EqualTo(1UL));
        });

        AsyncComputePlanVariantKey staleKey = Key(4UL, 14UL);
        cache.Add(staleKey, Plan(accepted: false, resourceGeneration: 4UL));
        Assert.That(cache.TryGet(staleKey, out _), Is.False);
    }

    [Test]
    public void ValidationLedger_AttributesExactSegmentsAndQuarantinesUnknownErrors()
    {
        var ledger = new AsyncComputeValidationLedger(segmentCapacity: 4, eventCapacity: 4);
        ledger.BeginFrame(17UL);
        Assert.That(ledger.RegisterSegment(3, AsyncComputePath.HiZBuild, 700UL), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(ledger.RecordError(3, 701UL, 4, out AsyncComputePath exact),
                Is.EqualTo(AsyncComputeValidationAttribution.Segment));
            Assert.That(exact, Is.EqualTo(AsyncComputePath.HiZBuild));
            Assert.That(ledger.IsAutoTimingAllowed(AsyncComputePath.HiZBuild), Is.True);
            Assert.That(ledger.RecordError(99, 702UL, 5, out _),
                Is.EqualTo(AsyncComputeValidationAttribution.Unknown));
            Assert.That(ledger.IsAutoTimingAllowed(AsyncComputePath.HiZBuild), Is.False);
            Assert.That(ledger.Events.Length, Is.EqualTo(2));
            Assert.That(ledger.Events[1].Quarantined, Is.True);
        });
    }

    private static RenderGraphConcreteResourceBinding CreateBuffer(ulong handle) =>
        RenderGraphConcreteResourceBinding.ForBuffer(
            RenderGraphResourceId.SceneSubmissionBuffers,
            $"buffer-{handle}",
            new Buffer { Handle = handle },
            1024,
            new uint[] { 0, 1 },
            0,
            SharingMode.Exclusive,
            allocationGeneration: handle);

    private static AsyncComputePlanVariantKey Key(ulong generation, ulong signature) =>
        new(generation, signature, 1UL, 2UL, 3UL);

    private static AsyncComputeSubmissionPlan Plan(bool accepted, ulong resourceGeneration) =>
        new(
            accepted,
            accepted ? string.Empty : "rejected",
            resourceGeneration,
            Array.Empty<AsyncComputeSubmissionSegment>(),
            Array.Empty<QueueOwnershipTransfer>(),
            Array.Empty<AsyncComputePathRuntimeStatus>());
}
