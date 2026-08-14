using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverFeedbackGpuResourcesTests
{
    [Test]
    public void CapturedBank_IsOnlyVisibleToTheFollowingFrame()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();

        Assert.That(manager.Configure(plan, allocator).State,
            Is.EqualTo(SimpleDdgiReceiverFeedbackGpuResourceState.Ready));
        Assert.That(manager.TryBeginCapture(7u, 100UL, out var token, out _), Is.True);
        Assert.That(manager.AcquireForScheduling(7u, 100UL).UseFeedback, Is.False);

        SimpleDdgiReceiverFeedbackBankValidation completion = manager.CompleteCapture(
            token,
            ValidHeader(token, plan.Layout));
        SimpleDdgiReceiverFeedbackScheduleBinding next =
            manager.AcquireForScheduling(7u, 101UL);

        Assert.Multiple(() =>
        {
            Assert.That(completion.UseFeedback, Is.True);
            Assert.That(next.UseFeedback, Is.True);
            Assert.That(next.SummaryBankIndex, Is.EqualTo(token.WriteBankIndex));
            Assert.That(manager.AcquireForScheduling(7u, 102UL).UseFeedback, Is.False);
        });
    }

    [Test]
    public void TwoBanks_MayRemainInFlightAndOnlyTheirOwnReuseIsBlocked()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();
        manager.Configure(plan, allocator);

        Assert.That(manager.TryBeginCapture(
            7u, 100UL, out var first, out _), Is.True);
        Assert.That(manager.TryBeginCapture(
            7u, 101UL, out var second, out _), Is.True);
        Assert.That(second.WriteBankIndex, Is.Not.EqualTo(first.WriteBankIndex));
        Assert.That(manager.TryBeginCapture(
            7u, 102UL, out _, out string fullReason), Is.False);
        Assert.That(fullReason, Does.Contain("write-bank-still-in-flight"));

        Assert.That(manager.CompleteGpuCapture(
            first, ValidGpuHeader(first, plan.Layout)).UseFeedback, Is.True);
        Assert.That(manager.TryBeginCapture(
            7u, 102UL, out var third, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(third.WriteBankIndex, Is.EqualTo(first.WriteBankIndex));
            Assert.That(third.FeedbackGeneration,
                Is.GreaterThan(second.FeedbackGeneration));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuResourceState.Capturing));
        });
    }

    [Test]
    public void OverflowedOrMismatchedBank_FallsBackAsAWhole()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();
        manager.Configure(plan, allocator);
        Assert.That(manager.TryBeginCapture(7u, 100UL, out var token, out _), Is.True);
        var bad = ValidHeader(token, plan.Layout) with
        {
            DroppedCount = 1u,
            Flags = SimpleDdgiReceiverFeedbackBankFlags.Validated |
                SimpleDdgiReceiverFeedbackBankFlags.AppendOverflow
        };

        SimpleDdgiReceiverFeedbackBankValidation completion = manager.CompleteCapture(token, bad);
        SimpleDdgiReceiverFeedbackScheduleBinding scheduled =
            manager.AcquireForScheduling(7u, 101UL);

        Assert.Multiple(() =>
        {
            Assert.That(completion.UseFeedback, Is.False);
            Assert.That(completion.Reason, Is.EqualTo(GiExperimentFallbackReason.FeedbackBankOverflowed));
            Assert.That(scheduled.UseFeedback, Is.False);
        });
    }

    [Test]
    public void FailedReplacement_DoesNotPublishOldLayout()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();
        manager.Configure(plan, allocator);
        Assert.That(manager.TryBeginCapture(7u, 100UL, out var token, out _), Is.True);
        manager.CompleteCapture(token, ValidHeader(token, plan.Layout));

        allocator.ThrowOnAllocate = true;
        SimpleDdgiReceiverFeedbackGpuResourceSnapshot snapshot = manager.Configure(plan, allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.AllocatedBytes, Is.GreaterThan(0UL));
            Assert.That(snapshot.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired));
            Assert.That(snapshot.PublishedBankIndex, Is.EqualTo(-1));
            Assert.That(manager.AcquireForScheduling(7u, 101UL).UseFeedback, Is.False);
            Assert.That(manager.TryBeginCapture(7u, 101UL, out _, out _), Is.False);
        });
    }

    [Test]
    public void InvalidAllocationShape_IsRejectedWithoutDescriptors()
    {
        var allocator = new TestAllocator { InvalidShape = true };
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackGpuResourceSnapshot snapshot =
            manager.Configure(CreateEffectivePlan(), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(SimpleDdgiReceiverFeedbackGpuResourceState.Disabled));
            Assert.That(snapshot.AllocatedBytes, Is.Zero);
            Assert.That(snapshot.DescriptorCount, Is.Zero);
        });
    }

    [Test]
    public void LegacyPerRecordScratchLayout_IsRejectedBeforeTheAllocatorCanRun()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan exactPlan = CreateEffectivePlan();
        SimpleDdgiReceiverFeedbackPlan legacySizedPlan = exactPlan with
        {
            Layout = exactPlan.Layout with
            {
                SortScratchBytes = exactPlan.Layout.RecordCapacity * 16UL,
                TotalBytes = exactPlan.Layout.RecordBanksBytes +
                    exactPlan.Layout.RecordCapacity * 16UL +
                    exactPlan.Layout.SummaryBytes +
                    exactPlan.Layout.CaptureSource.RequiredBytes
            }
        };

        SimpleDdgiReceiverFeedbackGpuResourceSnapshot snapshot = manager.Configure(
            legacySizedPlan,
            allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuResourceState.Disabled));
            Assert.That(snapshot.Reason,
                Does.Contain("layout-bytes-do-not-match-abi"));
            Assert.That(allocator.AllocationAttempts, Is.Zero);
        });
    }

    [Test]
    public void NativeGpuHeader_IsCheckedAgainstTheAdmittedPartitionBeforePublishing()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();
        manager.Configure(plan, allocator);
        Assert.That(manager.TryBeginCapture(7u, 100UL, out var token, out _), Is.True);

        SimpleDdgiReceiverFeedbackBankValidation completion = manager.CompleteGpuCapture(
            token,
            ValidGpuHeader(token, plan.Layout));

        Assert.Multiple(() =>
        {
            Assert.That(completion.UseFeedback, Is.True);
            Assert.That(manager.AcquireForScheduling(7u, 101UL).UseFeedback, Is.True);
        });
    }

    [Test]
    public void ManagedHeader_WithForeignRecordCapacityRejectsTheWholeWriteBank()
    {
        var allocator = new TestAllocator();
        using var manager = new SimpleDdgiReceiverFeedbackGpuResourceManager();
        SimpleDdgiReceiverFeedbackPlan plan = CreateEffectivePlan();
        manager.Configure(plan, allocator);
        Assert.That(manager.TryBeginCapture(7u, 100UL, out var token, out _), Is.True);

        SimpleDdgiReceiverFeedbackBankValidation completion = manager.CompleteCapture(
            token,
            ValidHeader(token, plan.Layout) with { RecordCapacity = 65u });

        Assert.Multiple(() =>
        {
            Assert.That(completion.UseFeedback, Is.False);
            Assert.That(completion.Reason,
                Is.EqualTo(GiExperimentFallbackReason.FeedbackBankInvalid));
            Assert.That(completion.Detail,
                Does.Contain("record-capacity-does-not-match-admitted-layout"));
            Assert.That(manager.AcquireForScheduling(7u, 101UL).UseFeedback, Is.False);
        });
    }

    private static SimpleDdgiReceiverFeedbackPlan CreateEffectivePlan()
    {
        Assert.That(
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.TryCreateLayout(
                64u,
                new SimpleDdgiReceiverFeedbackProducerCapacities(
                    OpaqueForward: 8u,
                    AlphaMaskOrFoliage: 0u,
                    TransparentWeightedOit: 0u,
                    Particles: 0u,
                    Fog: 0u,
                    ReflectionCapture: 0u,
                    RefinementOrBaseFallback: 0u),
                out SimpleDdgiReceiverFeedbackCaptureSourceLayout captureSource,
                out string captureReason),
            Is.True,
            captureReason);
        var layout = new SimpleDdgiReceiverFeedbackLayout(
            SampledScreenTileCount: 8UL,
            ScreenRecordCount: 8UL,
            OtherProducerRecordCount: 0UL,
            SafetyMarginRecordCount: 0UL,
            RecordCapacity: 64UL,
            RecordBankBytes: 2_048UL,
            RecordBanksBytes: 4_096UL,
            SortScratchBytes: 6_144UL,
            SummaryBytes: 7_328UL,
            TotalBytes: 17_568UL + captureSource.RequiredBytes,
            GpuSortAbiVersion: SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
            GpuSortSummaryCapacity: 64u,
            GpuSortFallbackCapacity: 64u,
            CaptureSource: captureSource,
            SourceScreenTileCount: 8UL,
            MaximumUniqueGatherOwnersPerTile: 1u);
        return new SimpleDdgiReceiverFeedbackPlan(
            new GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                GiExperimentFallbackReason.None,
                "active",
                string.Empty),
            layout,
            SimpleDdgiAdvancedExperimentMemoryPlan.Empty);
    }

    private static SimpleDdgiReceiverFeedbackBankHeader ValidHeader(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackLayout layout) => new(
        SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
        token.FeedbackGeneration,
        token.ViewportGeneration,
        token.FrameSerial,
        AppendCount: 0u,
        DroppedCount: 0u,
        ProducerOverflowMask: 0u,
        RecordCapacity: checked((uint)layout.RecordCapacity),
        SimpleDdgiReceiverFeedbackBankFlags.Validated);

    private static GPUSimpleDdgiReceiverFeedbackBankHeaderV2 ValidGpuHeader(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackLayout layout) => new()
    {
        LayoutRevision = SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
        EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel,
        FeedbackGeneration = token.FeedbackGeneration,
        ViewportGeneration = token.ViewportGeneration,
        FrameSerialLow = unchecked((uint)token.FrameSerial),
        FrameSerialHigh = checked((uint)(token.FrameSerial >> 32)),
        AppendCount = 0u,
        DroppedCount = 0u,
        ProducerOverflowMask = 0u,
        RecordCapacity = checked((uint)layout.RecordCapacity),
        ProbePartialCount = 0u,
        FallbackPartialCount = 0u,
        SummaryCount = 0u,
        FallbackSummaryCount = 0u,
        InvalidRecordCount = 0u,
        Flags = SimpleDdgiReceiverFeedbackGpuBankFlags.Validated
    };

    private sealed class TestAllocator : ISimpleDdgiReceiverFeedbackGpuResourceAllocator
    {
        public bool ThrowOnAllocate { get; set; }
        public bool InvalidShape { get; set; }
        public int AllocationAttempts { get; private set; }
        private ulong _next = 1UL;

        public SimpleDdgiReceiverFeedbackGpuAllocation Allocate(
            in SimpleDdgiReceiverFeedbackLayout layout)
        {
            AllocationAttempts++;
            if (ThrowOnAllocate)
                throw new InvalidOperationException("test allocation failure");
            ulong id = _next++;
            return new SimpleDdgiReceiverFeedbackGpuAllocation(
                id,
                new SimpleDdgiReceiverFeedbackGpuBuffer(id, InvalidShape ? 1UL : layout.RecordBanksBytes),
                new SimpleDdgiReceiverFeedbackGpuBuffer(_next++, layout.SortScratchBytes),
                new SimpleDdgiReceiverFeedbackGpuBuffer(_next++, layout.SummaryBytes),
                new SimpleDdgiReceiverFeedbackGpuBuffer(
                    _next++,
                    layout.CaptureSource.RequiredBytes),
                InvalidShape ? 3u : 4u);
        }

        public void Retire(SimpleDdgiReceiverFeedbackGpuAllocation allocation)
        {
        }
    }
}
