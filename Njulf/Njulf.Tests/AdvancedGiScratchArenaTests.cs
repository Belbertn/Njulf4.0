using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiScratchArenaTests
{
    private static readonly SimpleDdgiAdvancedMemoryCategory[] TransientCategories =
    [
        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom,
        SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
        SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
        SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets,
        SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch
    ];

    [Test]
    public void EmptyPlan_HasNoAllocationOrSyntheticSavings()
    {
        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            [], out GiExperimentScratchArenaPlan plan, out string failure), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Empty);
            Assert.That(plan.Slices, Is.Empty);
            Assert.That(plan.RequiredBytes, Is.Zero);
            Assert.That(plan.PeakLiveBytes, Is.Zero);
            Assert.That(plan.UnaliasedBytes, Is.Zero);
            Assert.That(plan.AliasedBytesSaved, Is.Zero);
            Assert.That(plan.PlacementOverheadBytes, Is.Zero);
            Assert.That(plan.LayoutFingerprint, Is.Not.Zero);
        });
    }

    [Test]
    public void DisjointLifetimes_ReuseTheSamePhysicalRange()
    {
        GiExperimentScratchAllocation[] requests =
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                4096UL, new GiExperimentScratchInterval(2, 5), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                3072UL, new GiExperimentScratchInterval(6, 9), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                2048UL, new GiExperimentScratchInterval(10, 12), 256UL)
        ];

        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            requests, out GiExperimentScratchArenaPlan plan, out string failure), Is.True,
            failure);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Slices.Select(static slice => slice.Offset),
                Is.All.Zero);
            Assert.That(plan.RequiredBytes, Is.EqualTo(4096UL));
            Assert.That(plan.PeakLiveBytes, Is.EqualTo(4096UL));
            Assert.That(plan.UnaliasedBytes, Is.EqualTo(9216UL));
            Assert.That(plan.AliasedBytesSaved, Is.EqualTo(5120UL));
            Assert.That(plan.PlacementOverheadBytes, Is.Zero);
        });
    }

    [Test]
    public void OverlappingLifetimes_GetDisjointAlignedRanges()
    {
        GiExperimentScratchAllocation[] requests =
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                1000UL, new GiExperimentScratchInterval(0, 4), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                700UL, new GiExperimentScratchInterval(3, 8), 512UL),
            new(SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                600UL, new GiExperimentScratchInterval(9, 12), 128UL)
        ];

        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            requests, out GiExperimentScratchArenaPlan plan, out string failure), Is.True,
            failure);

        GiExperimentScratchSlice b1 = Get(
            plan, SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch);
        GiExperimentScratchSlice c3 = Get(
            plan, SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch);
        GiExperimentScratchSlice c4 = Get(
            plan, SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch);
        Assert.Multiple(() =>
        {
            Assert.That(b1.Offset % b1.Alignment, Is.Zero);
            Assert.That(c3.Offset % c3.Alignment, Is.Zero);
            Assert.That(c4.Offset % c4.Alignment, Is.Zero);
            Assert.That(b1.ByteRangeOverlaps(c3), Is.False);
            Assert.That(c4.Offset, Is.Zero,
                "C4 starts after both live intervals and must reuse their base range.");
            Assert.That(plan.PeakLiveBytes, Is.EqualTo(1700UL));
            Assert.That(plan.RequiredBytes, Is.EqualTo(1724UL));
            Assert.That(plan.PlacementOverheadBytes, Is.EqualTo(24UL));
        });
    }

    [Test]
    public void Placement_IsIndependentOfCallerEnumerationOrder()
    {
        GiExperimentScratchAllocation[] forward =
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                4096UL, new GiExperimentScratchInterval(5, 8), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch,
                8192UL, new GiExperimentScratchInterval(0, 1), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                2048UL, new GiExperimentScratchInterval(9, 11), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                6144UL, new GiExperimentScratchInterval(12, 16), 256UL)
        ];
        GiExperimentScratchAllocation[] reverse = forward.Reverse().ToArray();

        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            forward, out GiExperimentScratchArenaPlan first, out string firstFailure),
            Is.True, firstFailure);
        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            reverse, out GiExperimentScratchArenaPlan second, out string secondFailure),
            Is.True, secondFailure);

        Assert.Multiple(() =>
        {
            Assert.That(second.RequiredBytes, Is.EqualTo(first.RequiredBytes));
            Assert.That(second.LayoutFingerprint, Is.EqualTo(first.LayoutFingerprint));
            Assert.That(second.Slices, Is.EqualTo(first.Slices));
        });
    }

    [Test]
    public void InvalidRequests_FailClosedWithNoPartialPlan()
    {
        GiExperimentScratchAllocation persistent = new(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
            64UL,
            new GiExperimentScratchInterval(0, 1));
        GiExperimentScratchAllocation invalidAlignment = new(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            64UL,
            new GiExperimentScratchInterval(0, 1),
            3UL);
        GiExperimentScratchAllocation invalidInterval = new(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            64UL,
            new GiExperimentScratchInterval(2, 1));
        GiExperimentScratchAllocation overflow = new(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            ulong.MaxValue,
            new GiExperimentScratchInterval(0, 2),
            1UL);
        GiExperimentScratchAllocation overlap = new(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
            1UL,
            new GiExperimentScratchInterval(0, 2),
            2UL);

        AssertFailure([persistent], "advanced-gi-scratch-category-is-not-transient");
        AssertFailure([invalidAlignment], "advanced-gi-scratch-alignment-is-invalid");
        AssertFailure([invalidInterval], "advanced-gi-scratch-interval-is-invalid");
        AssertFailure([overflow, overlap], "advanced-gi-scratch-size-overflow");
        AssertFailure(
            [
                invalidAlignment with { Alignment = 4UL },
                invalidAlignment with { Alignment = 4UL }
            ],
            "advanced-gi-scratch-category-is-duplicated");
    }

    [Test]
    public void RandomizedPlacements_NeverOverlapWhileLive()
    {
        var random = new Random(0x5A17C4);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int count = random.Next(1, TransientCategories.Length + 1);
            SimpleDdgiAdvancedMemoryCategory[] categories = TransientCategories
                .OrderBy(_ => random.Next())
                .Take(count)
                .ToArray();
            var requests = new GiExperimentScratchAllocation[count];
            for (int index = 0; index < count; index++)
            {
                int first = random.Next(0, 32);
                int last = random.Next(first, 36);
                ulong alignment = 1UL << random.Next(0, 11);
                ulong bytes = checked((ulong)random.Next(1, 16_385));
                requests[index] = new GiExperimentScratchAllocation(
                    categories[index], bytes,
                    new GiExperimentScratchInterval(first, last), alignment);
            }

            Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
                requests, out GiExperimentScratchArenaPlan plan, out string failure),
                Is.True, $"iteration {iteration}: {failure}");
            Assert.That(plan.PeakLiveBytes,
                Is.EqualTo(BruteForcePeak(requests)), $"iteration {iteration}");

            foreach (GiExperimentScratchSlice slice in plan.Slices)
            {
                Assert.That(slice.Offset % slice.Alignment, Is.Zero,
                    $"iteration {iteration}, {slice.Category}");
                Assert.That(slice.EndExclusive, Is.LessThanOrEqualTo(plan.RequiredBytes));
            }
            for (int left = 0; left < plan.Slices.Count; left++)
            {
                for (int right = left + 1; right < plan.Slices.Count; right++)
                {
                    GiExperimentScratchSlice a = plan.Slices[left];
                    GiExperimentScratchSlice b = plan.Slices[right];
                    if (!a.Interval.CanAlias(b.Interval))
                    {
                        Assert.That(a.ByteRangeOverlaps(b), Is.False,
                            $"iteration {iteration}: {a.Category}/{b.Category}");
                    }
                }
            }
        }
    }

    [Test]
    public void AdvancedGraph_DeclaresOnlyScratchRangesTransient()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            DdgiOpacityMicromapMode.ExtFourStateExperiment,
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment,
            GiCausticMode.WorldCacheExperiment,
            SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        IReadOnlyDictionary<RenderGraphResourceId, RenderGraphResourceDescriptor> resources =
            ProductionRenderPipelineDeclaration.Instance.CreateResourceDescriptors(
                    Format.D32Sfloat, Format.B8G8R8A8Srgb, modes)
                .ToDictionary(static descriptor => descriptor.Id);

        RenderGraphResourceId[] transientBuffers =
        [
            RenderGraphResourceId.OpacityMicromapBuildScratch,
            RenderGraphResourceId.OpacityMicromapCompactionHeadroom,
            RenderGraphResourceId.SimpleDdgiGuidingScratch,
            RenderGraphResourceId.GiCausticScratch,
            RenderGraphResourceId.NearFieldResidualHitMetadata,
            RenderGraphResourceId.NearFieldResidualTileBuffers
        ];
        foreach (RenderGraphResourceId id in transientBuffers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(resources[id].Kind, Is.EqualTo(RenderGraphResourceKind.BufferSet),
                    id.ToString());
                Assert.That(resources[id].Lifetime,
                    Is.EqualTo(RenderGraphResourceLifetime.Transient), id.ToString());
                Assert.That(resources[id].Persistent, Is.False, id.ToString());
            });
        }

        RenderGraphResourceId[] persistentBuffers =
        [
            RenderGraphResourceId.OpacityMicromapResources,
            RenderGraphResourceId.SimpleDdgiGuidingDistributions,
            RenderGraphResourceId.GiCausticPhotons,
            RenderGraphResourceId.GiCausticCache,
            RenderGraphResourceId.NearFieldResidualHistoryMetadata
        ];
        foreach (RenderGraphResourceId id in persistentBuffers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(resources[id].Lifetime,
                    Is.Not.EqualTo(RenderGraphResourceLifetime.Transient), id.ToString());
                Assert.That(resources[id].Persistent, Is.True, id.ToString());
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(resources.ContainsKey(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackRecords),
                Is.False);
            Assert.That(resources.ContainsKey(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackSortScratch),
                Is.False);
            Assert.That(resources.ContainsKey(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackSummaries),
                Is.False);
        });

        RenderGraphPassResourceDeclaration ommBuild =
            ProductionRenderPipelineDeclaration.Instance
                .CreateExternallyRecordedPassResourceDeclarations(modes)
                .Single(static pass => pass.PassName == "OpacityMicromapBuildPass");
        Assert.That(ommBuild.Usages.Select(static usage => usage.Resource),
            Does.Contain(RenderGraphResourceId.OpacityMicromapBuildScratch));
        Assert.That(ommBuild.Usages.Select(static usage => usage.Resource),
            Does.Contain(RenderGraphResourceId.OpacityMicromapCompactionHeadroom));
    }

    [Test]
    public void VulkanArena_ReconcilesTransactionallyAndPublishesExactSlices()
    {
        GiExperimentScratchArenaPlan first = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                4096UL, new GiExperimentScratchInterval(0, 2), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                2048UL, new GiExperimentScratchInterval(3, 5), 256UL)
        ]);
        GiExperimentScratchArenaPlan second = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                8192UL, new GiExperimentScratchInterval(0, 2), 256UL),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                2048UL, new GiExperimentScratchInterval(3, 5), 256UL)
        ]);
        var backend = new FakeArenaBackend();
        int waitCount = 0;
        using var arena = new AdvancedGiTransientBufferArena(
            backend, () => waitCount++);

        Assert.That(arena.TryReconcile(
            first, 64UL * 1024UL, 64UL * 1024UL, out string firstFailure),
            Is.True, firstFailure);
        Assert.That(arena.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            4096UL, 256UL, out AdvancedGiTransientBufferSlice b1,
            out string b1Failure), Is.True, b1Failure);
        Assert.That(arena.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
            2048UL, 256UL, out AdvancedGiTransientBufferSlice c3,
            out string c3Failure), Is.True, c3Failure);

        Assert.Multiple(() =>
        {
            Assert.That(b1.Buffer, Is.EqualTo(c3.Buffer));
            Assert.That(b1.NativeBufferHandle, Is.EqualTo(c3.NativeBufferHandle));
            Assert.That(b1.Offset, Is.Zero);
            Assert.That(c3.Offset, Is.Zero);
            Assert.That(b1.ArenaGeneration, Is.EqualTo(1UL));
            Assert.That(backend.AllocationCount, Is.EqualTo(1));
            Assert.That(backend.RetirementCount, Is.Zero);
            Assert.That(waitCount, Is.Zero);
            Assert.That(arena.Diagnostics.AliasedBytesSaved, Is.EqualTo(2048UL));
        });

        // Idempotent reconciliation must not allocate, wait, or invalidate
        // descriptor ranges.
        Assert.That(arena.TryReconcile(
            first, 64UL * 1024UL, 64UL * 1024UL, out string sameFailure),
            Is.True, sameFailure);
        Assert.That(backend.AllocationCount, Is.EqualTo(1));
        Assert.That(waitCount, Is.Zero);

        Assert.That(arena.TryReconcile(
            second, 64UL * 1024UL, 64UL * 1024UL, out string secondFailure),
            Is.True, secondFailure);
        Assert.That(arena.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            8192UL, 256UL, out AdvancedGiTransientBufferSlice replacement,
            out string replacementFailure), Is.True, replacementFailure);
        Assert.Multiple(() =>
        {
            Assert.That(replacement.Buffer, Is.Not.EqualTo(b1.Buffer));
            Assert.That(replacement.ArenaGeneration, Is.EqualTo(2UL));
            Assert.That(backend.AllocationCount, Is.EqualTo(2));
            Assert.That(backend.RetirementCount, Is.EqualTo(1));
            Assert.That(waitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void VulkanArena_FailedReplacementKeepsPriorGenerationReadable()
    {
        GiExperimentScratchArenaPlan first = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                1024UL, new GiExperimentScratchInterval(0, 1), 256UL)
        ]);
        GiExperimentScratchArenaPlan second = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                2048UL, new GiExperimentScratchInterval(0, 1), 256UL)
        ]);
        var backend = new FakeArenaBackend();
        using var arena = new AdvancedGiTransientBufferArena(backend, static () => { });
        Assert.That(arena.TryReconcile(
            first, 4096UL, 4096UL, out string initialFailure), Is.True,
            initialFailure);
        Assert.That(arena.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            1024UL, 256UL, out AdvancedGiTransientBufferSlice prior,
            out string priorFailure), Is.True, priorFailure);

        backend.FailAllocation = true;
        Assert.That(arena.TryReconcile(
            second, 4096UL, 4096UL, out string failure), Is.False);
        Assert.That(failure, Does.StartWith(
            "advanced-gi-transient-arena-allocation-failed:"));
        Assert.That(arena.TryGetSlice(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
            1024UL, 256UL, out AdvancedGiTransientBufferSlice stillPrior,
            out string stillPriorFailure), Is.True, stillPriorFailure);
        Assert.Multiple(() =>
        {
            Assert.That(stillPrior, Is.EqualTo(prior));
            Assert.That(backend.LiveCount, Is.EqualTo(1));
            Assert.That(arena.Diagnostics.AllocatedBytes, Is.EqualTo(1024UL));
        });
    }

    [Test]
    public void VulkanArena_RejectsIncompatibleOrOverBudgetPlansBeforeAllocation()
    {
        GiExperimentScratchArenaPlan imagePlan = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch,
                1024UL, new GiExperimentScratchInterval(0, 1), 256UL)
        ]);
        GiExperimentScratchArenaPlan bufferPlan = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                2048UL, new GiExperimentScratchInterval(0, 1), 256UL)
        ]);
        var backend = new FakeArenaBackend();
        using var arena = new AdvancedGiTransientBufferArena(backend, static () => { });

        Assert.That(arena.TryReconcile(
            imagePlan, 4096UL, 4096UL, out string imageFailure), Is.False);
        Assert.That(imageFailure, Is.EqualTo(
            "advanced-gi-transient-arena-category-is-not-buffer-compatible"));
        Assert.That(arena.TryReconcile(
            bufferPlan, 1024UL, 4096UL, out string rangeFailure), Is.False);
        Assert.That(rangeFailure, Is.EqualTo(
            "advanced-gi-transient-arena-maximum-buffer-range-exceeded"));
        Assert.That(arena.TryReconcile(
            bufferPlan, 4096UL, 1024UL, out string budgetFailure), Is.False);
        Assert.That(budgetFailure, Is.EqualTo(
            "advanced-gi-transient-arena-memory-headroom-exceeded"));
        Assert.That(backend.AllocationCount, Is.Zero);
    }

    [Test]
    public void VulkanArena_DisableWaitsThenReturnsToExactZeroOwnership()
    {
        GiExperimentScratchArenaPlan plan = Compile(
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                1024UL, new GiExperimentScratchInterval(0, 1), 256UL)
        ]);
        var backend = new FakeArenaBackend();
        int waits = 0;
        using var arena = new AdvancedGiTransientBufferArena(backend, () => waits++);
        Assert.That(arena.TryReconcile(
            plan, 4096UL, 4096UL, out string allocationFailure), Is.True,
            allocationFailure);

        Assert.That(arena.TryReconcile(
            GiExperimentScratchArenaPlan.Empty,
            4096UL,
            0UL,
            out string disableFailure), Is.True, disableFailure);
        Assert.Multiple(() =>
        {
            Assert.That(waits, Is.EqualTo(1));
            Assert.That(backend.LiveCount, Is.Zero);
            Assert.That(arena.Diagnostics,
                Is.EqualTo(AdvancedGiTransientBufferArenaDiagnostics.Disabled));
        });
    }

    private static GiExperimentScratchSlice Get(
        GiExperimentScratchArenaPlan plan,
        SimpleDdgiAdvancedMemoryCategory category)
    {
        Assert.That(plan.TryGetSlice(category, out GiExperimentScratchSlice slice), Is.True);
        return slice;
    }

    private static GiExperimentScratchArenaPlan Compile(
        GiExperimentScratchAllocation[] requests)
    {
        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            requests, out GiExperimentScratchArenaPlan plan, out string failure),
            Is.True, failure);
        return plan;
    }

    private static void AssertFailure(
        GiExperimentScratchAllocation[] requests,
        string expected)
    {
        Assert.That(GiExperimentScratchAliasing.TryCompileArenaPlan(
            requests, out GiExperimentScratchArenaPlan plan, out string failure), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.EqualTo(expected));
            Assert.That(plan.Slices, Is.Empty);
            Assert.That(plan.RequiredBytes, Is.Zero);
        });
    }

    private static ulong BruteForcePeak(
        IReadOnlyList<GiExperimentScratchAllocation> requests)
    {
        int lastPass = requests.Max(static request => request.Interval.LastPassInclusive);
        ulong peak = 0UL;
        for (int pass = 0; pass <= lastPass; pass++)
        {
            ulong live = 0UL;
            foreach (GiExperimentScratchAllocation request in requests)
            {
                if (request.Interval.FirstPassInclusive <= pass &&
                    pass <= request.Interval.LastPassInclusive)
                {
                    live = checked(live + request.Bytes);
                }
            }
            peak = Math.Max(peak, live);
        }
        return peak;
    }

    private sealed class FakeArenaBackend : IAdvancedGiTransientBufferArenaBackend
    {
        private readonly Dictionary<BufferHandle, ulong> _buffers = new();
        private int _nextIndex;

        public bool FailAllocation { get; set; }

        public int AllocationCount { get; private set; }

        public int RetirementCount { get; private set; }

        public int LiveCount => _buffers.Count;

        public BufferHandle Allocate(ulong bytes, bool requireDeviceAddress)
        {
            if (FailAllocation)
                throw new InvalidOperationException("injected-allocation-failure");
            var handle = new BufferHandle(_nextIndex++, 1u);
            _buffers.Add(handle, bytes);
            AllocationCount++;
            return handle;
        }

        public void Retire(BufferHandle buffer)
        {
            if (!_buffers.Remove(buffer))
                throw new InvalidOperationException("retiring-unknown-buffer");
            RetirementCount++;
        }

        public ulong GetNativeHandle(BufferHandle buffer) =>
            _buffers.ContainsKey(buffer) ? checked((ulong)buffer.Index + 1UL) : 0UL;

        public ulong GetSize(BufferHandle buffer) =>
            _buffers.TryGetValue(buffer, out ulong bytes) ? bytes : 0UL;
    }
}
