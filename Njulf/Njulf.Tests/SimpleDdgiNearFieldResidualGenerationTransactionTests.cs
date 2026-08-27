using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualGenerationTransactionTests
{
    [Test]
    public void FrameBoundarySwap_RetiresAgainstGreatestReferencingFence()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        var backend = new FakeBackend();
        using var transaction = Create(backend);

        Assert.That(transaction.TryInitialize(first, out string initialize),
            Is.True, initialize);
        Assert.That(transaction.RecordActiveReference(5UL), Is.True);
        Assert.That(transaction.RecordActiveReference(9UL), Is.True);
        SimpleDdgiNearFieldResidualGenerationRequestResult request =
            transaction.RequestReplacement(second);
        Assert.Multiple(() =>
        {
            Assert.That(request.Accepted, Is.True);
            Assert.That(request.ReplacementReady, Is.True);
            Assert.That(request.CanonicalFallbackRequired, Is.True);
            Assert.That(transaction.Snapshot.PendingGeneration, Is.EqualTo(2UL));
        });

        Assert.That(transaction.TryCommitAtFrameBoundary(
            greatestReferencingFrameFenceValue: 7UL,
            currentFrame: 10UL,
            out string commit), Is.True, commit);
        SimpleDdgiNearFieldResidualGenerationSnapshot committed =
            transaction.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(committed.ActiveGeneration, Is.EqualTo(2UL));
            Assert.That(committed.RetiredGeneration, Is.EqualTo(1UL));
            Assert.That(committed.PendingGeneration, Is.Zero);
            Assert.That(committed.LayoutEpoch, Is.EqualTo(2U));
            Assert.That(committed.HistoryEpoch, Is.EqualTo(2U));
            Assert.That(committed.CanonicalFallbackRequired, Is.False);
            Assert.That(committed.LiveBytes,
                Is.EqualTo(first.TotalBytes + second.TotalBytes));
        });

        Assert.That(transaction.PollCompleted(
            new GpuCompletionProgress(8UL, 0UL, 0UL), 11UL), Is.Zero);
        Assert.That(backend.DestroyedGenerations, Is.Empty);
        Assert.That(transaction.PollCompleted(
            new GpuCompletionProgress(9UL, 0UL, 0UL), 12UL), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(backend.DestroyedGenerations, Is.EqualTo(new[] { 1UL }));
            Assert.That(transaction.Snapshot.RetiredGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.LiveBytes,
                Is.EqualTo(second.TotalBytes));
        });
    }

    [Test]
    public void RepeatedRequests_CoalesceAnUnreferencedPendingGeneration()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        SimpleDdgiNearFieldResidualLayout newest = Layout(800, 450);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);

        Assert.That(transaction.RequestReplacement(second).ReplacementReady,
            Is.True);
        Assert.That(transaction.RequestReplacement(newest).ReplacementReady,
            Is.True);

        SimpleDdgiNearFieldResidualGenerationSnapshot snapshot =
            transaction.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.PendingGeneration, Is.EqualTo(3UL));
            Assert.That(snapshot.RequestedSourceWidth, Is.EqualTo(800));
            Assert.That(snapshot.RequestedSourceHeight, Is.EqualTo(450));
            Assert.That(snapshot.CoalescedRequestCount, Is.EqualTo(1UL));
            Assert.That(backend.DestroyedGenerations, Is.EqualTo(new[] { 2UL }));
            Assert.That(snapshot.LiveBytes,
                Is.EqualTo(first.TotalBytes + newest.TotalBytes));
        });
    }

    [Test]
    public void RetiredGeneration_DefersAndCoalescesToNewestRequestedExtent()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        SimpleDdgiNearFieldResidualLayout skipped = Layout(800, 450);
        SimpleDdgiNearFieldResidualLayout newest = Layout(960, 540);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);
        Assert.That(transaction.RequestReplacement(second).ReplacementReady,
            Is.True);
        Assert.That(transaction.RecordActiveReference(11UL), Is.True);
        Assert.That(transaction.TryCommitAtFrameBoundary(11UL, 12UL, out _),
            Is.True);

        Assert.That(transaction.RequestReplacement(skipped).ReplacementReady,
            Is.False);
        Assert.That(transaction.RequestReplacement(newest).ReplacementReady,
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(transaction.Snapshot.HasQueuedRequest, Is.True);
            Assert.That(transaction.Snapshot.CanonicalFallbackRequired, Is.True);
            Assert.That(transaction.Snapshot.PendingGeneration, Is.Zero);
        });

        Assert.That(transaction.PollCompleted(
            new GpuCompletionProgress(11UL, 0UL, 0UL), 13UL), Is.EqualTo(1));
        SimpleDdgiNearFieldResidualGenerationSnapshot prepared =
            transaction.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(prepared.PendingGeneration, Is.EqualTo(3UL));
            Assert.That(prepared.RequestedSourceWidth, Is.EqualTo(960));
            Assert.That(backend.AllocatedWidths,
                Is.EqualTo(new[] { 320, 640, 960 }));
            Assert.That(backend.AllocatedWidths, Does.Not.Contain(800));
        });
    }

    [Test]
    public void AllocationFailure_PreservesActiveGenerationAndCanonicalFallback()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);
        backend.FailNextAllocation = true;

        SimpleDdgiNearFieldResidualGenerationRequestResult request =
            transaction.RequestReplacement(second);
        SimpleDdgiNearFieldResidualGenerationSnapshot failed =
            transaction.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(request.Accepted, Is.True);
            Assert.That(request.ReplacementReady, Is.False);
            Assert.That(request.Reason,
                Does.StartWith("near-field-generation-allocation-failed:"));
            Assert.That(failed.ActiveGeneration, Is.EqualTo(1UL));
            Assert.That(failed.PendingGeneration, Is.Zero);
            Assert.That(failed.HasQueuedRequest, Is.True);
            Assert.That(failed.AllocationFailureCount, Is.EqualTo(1UL));
            Assert.That(failed.CanonicalFallbackRequired, Is.True);
        });

        _ = transaction.PollCompleted(default, currentFrame: 1UL);
        Assert.That(transaction.Snapshot.PendingGeneration, Is.EqualTo(2UL));
    }

    [Test]
    public void SteadyAndOverlapBudgets_FailClosedBeforePublishing()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        ulong steady = Math.Max(first.TotalBytes, second.TotalBytes);
        var backend = new FakeBackend();
        using var transaction = new
            SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>(
                backend,
                steadyBudgetBytes: steady,
                peakBudgetBytes: steady);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);

        SimpleDdgiNearFieldResidualGenerationRequestResult overlap =
            transaction.RequestReplacement(second);
        SimpleDdgiNearFieldResidualLayout oversized = second with
        {
            TotalBytes = steady + 1UL
        };
        SimpleDdgiNearFieldResidualGenerationRequestResult overSteady =
            transaction.RequestReplacement(oversized);

        Assert.Multiple(() =>
        {
            Assert.That(overlap.Accepted, Is.True);
            Assert.That(overlap.ReplacementReady, Is.False);
            Assert.That(overlap.Reason,
                Is.EqualTo("near-field-generation-peak-budget-exceeded"));
            Assert.That(overSteady.Accepted, Is.False);
            Assert.That(overSteady.Reason,
                Is.EqualTo("near-field-generation-steady-budget-exceeded"));
            Assert.That(transaction.Snapshot.ActiveGeneration, Is.EqualTo(1UL));
            Assert.That(transaction.Snapshot.PeakLiveBytes,
                Is.LessThanOrEqualTo(steady));
        });
    }

    [Test]
    public void EvidenceEnvelopeMismatch_DoesNotAllocateAReplacement()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout second = Layout(640, 360);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);

        SimpleDdgiNearFieldResidualGenerationRequestResult result =
            transaction.RequestReplacement(
                second,
                SimpleDdgiNearFieldResidualExtentEnvelope.Exact(first));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(
                "near-field-generation-evidence-extent-envelope-mismatch"));
            Assert.That(backend.AllocatedWidths, Is.EqualTo(new[] { 320 }));
            Assert.That(transaction.Snapshot.ActiveGeneration, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void RecoveryRebuild_WithUnchangedLayout_PublishesFreshGeneration()
    {
        SimpleDdgiNearFieldResidualLayout layout = Layout(320, 180);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(layout, out _), Is.True);

        SimpleDdgiNearFieldResidualGenerationRequestResult request =
            transaction.RequestRebuild(
                layout,
                SimpleDdgiNearFieldResidualExtentEnvelope.Exact(layout));

        Assert.Multiple(() =>
        {
            Assert.That(request.Accepted, Is.True);
            Assert.That(request.ReplacementReady, Is.True);
            Assert.That(transaction.Snapshot.ActiveGeneration, Is.EqualTo(1UL));
            Assert.That(transaction.Snapshot.PendingGeneration, Is.EqualTo(2UL));
            Assert.That(transaction.Snapshot.LiveBytes,
                Is.EqualTo(layout.TotalBytes * 2UL));
        });

        Assert.That(transaction.TryCommitAtFrameBoundary(
            greatestReferencingFrameFenceValue: 0UL,
            currentFrame: 2UL,
            out string failure), Is.True, failure);
        Assert.Multiple(() =>
        {
            Assert.That(transaction.Snapshot.ActiveGeneration, Is.EqualTo(2UL));
            Assert.That(transaction.Snapshot.PendingGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.RetiredGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.LiveBytes,
                Is.EqualTo(layout.TotalBytes));
            Assert.That(backend.AllocatedWidths,
                Is.EqualTo(new[] { 320, 320 }));
            Assert.That(backend.DestroyedGenerations,
                Is.EqualTo(new[] { 1UL }));
        });
    }

    [Test]
    public void TerminalRetirement_DropsPendingAndReleasesActiveAfterFence()
    {
        SimpleDdgiNearFieldResidualLayout first = Layout(320, 180);
        SimpleDdgiNearFieldResidualLayout pending = Layout(640, 360);
        var backend = new FakeBackend();
        using var transaction = Create(backend);
        Assert.That(transaction.TryInitialize(first, out _), Is.True);
        Assert.That(transaction.RecordActiveReference(17UL), Is.True);
        Assert.That(transaction.RequestReplacement(pending).ReplacementReady,
            Is.True);

        Assert.That(transaction.TryBeginTerminalRetirement(
            currentFrame: 15UL,
            out string failure), Is.True, failure);
        Assert.Multiple(() =>
        {
            Assert.That(transaction.Snapshot.ActiveGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.PendingGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.RetiredGeneration, Is.EqualTo(1UL));
            Assert.That(transaction.Snapshot.CanonicalFallbackRequired, Is.True);
            Assert.That(transaction.Snapshot.LiveBytes,
                Is.EqualTo(first.TotalBytes));
            Assert.That(backend.DestroyedGenerations,
                Is.EqualTo(new[] { 2UL }));
        });

        Assert.That(transaction.PollCompleted(
            new GpuCompletionProgress(16UL, 0UL, 0UL), 16UL), Is.Zero);
        Assert.That(transaction.Snapshot.LiveBytes, Is.EqualTo(first.TotalBytes));
        Assert.That(transaction.PollCompleted(
            new GpuCompletionProgress(17UL, 0UL, 0UL), 17UL), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(transaction.Snapshot.RetiredGeneration, Is.Zero);
            Assert.That(transaction.Snapshot.LiveBytes, Is.Zero);
            Assert.That(transaction.Snapshot.State,
                Is.EqualTo("terminal-retirement-complete"));
            Assert.That(backend.DestroyedGenerations,
                Is.EqualTo(new[] { 2UL, 1UL }));
        });
    }

    [Test]
    public void ProductionTransaction_HasNinetySixAndOneNinetyTwoMiBLimitsAndNoWaitApi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>
                    .DefaultSteadyBudgetBytes,
                Is.EqualTo(96UL * 1024UL * 1024UL));
            Assert.That(
                SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>
                    .DefaultPeakBudgetBytes,
                Is.EqualTo(192UL * 1024UL * 1024UL));
            Assert.That(typeof(
                    SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>)
                .GetMethods()
                .Select(static method => method.Name),
                Has.None.Contains("WaitIdle"));
            Assert.That(typeof(
                    ISimpleDdgiNearFieldResidualGenerationBackend<FakeResources>)
                .GetMethods()
                .Select(static method => method.Name),
                Is.EquivalentTo(new[] { "Allocate", "Destroy" }));
        });
    }

    private static SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>
        Create(FakeBackend backend) => new(backend);

    private static SimpleDdgiNearFieldResidualLayout Layout(int width, int height)
    {
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                width,
                height,
                SimpleDdgiNearFieldResidualProfile.Performance,
                SimpleDdgiNearFieldResidualGenerationTransaction<FakeResources>
                    .DefaultSteadyBudgetBytes);
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);
        return layout;
    }

    private sealed record FakeResources(ulong Generation);

    private sealed class FakeBackend :
        ISimpleDdgiNearFieldResidualGenerationBackend<FakeResources>
    {
        public bool FailNextAllocation { get; set; }
        public List<ulong> DestroyedGenerations { get; } = [];
        public List<int> AllocatedWidths { get; } = [];

        public SimpleDdgiNearFieldResidualGenerationAllocation<FakeResources>
            Allocate(
                ulong generation,
                in SimpleDdgiNearFieldResidualLayout layout)
        {
            if (FailNextAllocation)
            {
                FailNextAllocation = false;
                throw new OutOfMemoryException("Synthetic C5 allocation failure.");
            }
            AllocatedWidths.Add(layout.SourceWidth);
            return new(
                generation,
                layout,
                layout.TotalBytes,
                new FakeResources(generation));
        }

        public void Destroy(
            SimpleDdgiNearFieldResidualGenerationAllocation<FakeResources>
                allocation) =>
            DestroyedGenerations.Add(allocation.Generation);
    }
}
