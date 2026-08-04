using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GpuCompletionRetirementQueueTests
{
    [Test]
    public void Poll_ReclaimsOnlyRecordsWhosePrimitiveIsSignaled()
    {
        var queue = new GpuCompletionRetirementQueue(4);
        GpuRetirementRecord fenceRecord = Record(
            10UL,
            128UL,
            3UL,
            GpuCompletionToken.ForFrameFence(7UL),
            GpuRetirementResourceKind.Image,
            101UL);
        GpuRetirementRecord timelineRecord = Record(
            10UL,
            64UL,
            4UL,
            GpuCompletionToken.ForTimeline(42UL, 9UL),
            GpuRetirementResourceKind.ImageView,
            102UL);

        Assert.That(queue.TryEnqueue(fenceRecord, 1000UL, out _), Is.True);
        Assert.That(queue.TryEnqueue(timelineRecord, 1000UL, out _), Is.True);

        GpuRetirementRecord[] retired = new GpuRetirementRecord[2];
        Assert.That(queue.Poll(
            new GpuCompletionProgress(CompletedFrameFenceValue: 6UL, 42UL, 9UL),
            retired,
            currentFrame: 8UL), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(retired[0].Resource.Handle, Is.EqualTo(102UL));
            Assert.That(queue.ActiveCount, Is.EqualTo(1));
            Assert.That(queue.ActiveBytes, Is.EqualTo(128UL));
        });

        Assert.That(queue.Poll(
            new GpuCompletionProgress(CompletedFrameFenceValue: 7UL, 42UL, 9UL),
            retired,
            currentFrame: 9UL), Is.EqualTo(1));
        Assert.That(queue.IsEmpty, Is.True);
    }

    [Test]
    public void Admission_ReportsCapacityBudgetAndInvalidTokenWithoutMutatingState()
    {
        var queue = new GpuCompletionRetirementQueue(1, memoryBudgetBytes: 150UL);
        GpuRetirementRecord valid = Record(
            1UL,
            100UL,
            0UL,
            GpuCompletionToken.ForFrameFence(1UL),
            GpuRetirementResourceKind.Buffer,
            201UL);
        Assert.That(queue.TryEnqueue(valid, 0UL, out _), Is.True);

        Assert.That(queue.TryEnqueue(valid with { Resource = valid.Resource with { Handle = 202UL } }, 0UL,
            out GpuRetirementAdmissionFailure capacity), Is.False);
        Assert.That(capacity, Is.EqualTo(GpuRetirementAdmissionFailure.Capacity));

        var budgetQueue = new GpuCompletionRetirementQueue(2, memoryBudgetBytes: 150UL);
        Assert.That(budgetQueue.TryEnqueue(valid, 0UL, out _), Is.True);
        Assert.That(budgetQueue.TryEnqueue(valid with { ByteCharge = 51UL }, 0UL,
            out GpuRetirementAdmissionFailure budget), Is.False);
        Assert.That(budget, Is.EqualTo(GpuRetirementAdmissionFailure.MemoryBudget));

        GpuRetirementRecord invalid = valid with
        {
            Completion = default,
            Resource = valid.Resource with { Handle = 203UL }
        };
        Assert.That(budgetQueue.TryEnqueue(invalid, 0UL,
            out GpuRetirementAdmissionFailure token), Is.False);
        Assert.That(token, Is.EqualTo(GpuRetirementAdmissionFailure.InvalidCompletionToken));
        Assert.That(budgetQueue.ActiveCount, Is.EqualTo(1));
    }

    [Test]
    public void BatchAdmission_IsAtomicWhenCapacityOrBudgetRejects()
    {
        var queue = new GpuCompletionRetirementQueue(2, memoryBudgetBytes: 256UL);
        GpuRetirementRecord first = Record(
            1UL,
            100UL,
            0UL,
            GpuCompletionToken.ForFrameFence(3UL),
            GpuRetirementResourceKind.ImageView,
            301UL);
        GpuRetirementRecord second = first with
        {
            Resource = first.Resource with { Handle = 302UL }
        };
        Assert.That(queue.TryEnqueueBatch(new[] { first, second }, 0UL, out _), Is.True);

        GpuRetirementRecord[] rejected =
        [
            first with { Resource = first.Resource with { Handle = 303UL } },
            first with { Resource = first.Resource with { Handle = 304UL } }
        ];
        Assert.That(queue.TryEnqueueBatch(rejected, 0UL, out GpuRetirementAdmissionFailure failure), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.EqualTo(GpuRetirementAdmissionFailure.Capacity));
            Assert.That(queue.ActiveCount, Is.EqualTo(2));
            Assert.That(queue.ActiveBytes, Is.EqualTo(200UL));
        });

        var budgetQueue = new GpuCompletionRetirementQueue(4, memoryBudgetBytes: 256UL);
        Assert.That(budgetQueue.TryEnqueueBatch(new[] { first, second }, 0UL, out _), Is.True);
        Assert.That(budgetQueue.TryEnqueueBatch(rejected, 0UL,
            out GpuRetirementAdmissionFailure budgetFailure), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(budgetFailure, Is.EqualTo(GpuRetirementAdmissionFailure.MemoryBudget));
            Assert.That(budgetQueue.ActiveCount, Is.EqualTo(2));
            Assert.That(budgetQueue.ActiveBytes, Is.EqualTo(200UL));
        });
    }

    [Test]
    public void DrainAfterExternalDeviceIdle_ReturnsAllRemainingRecords()
    {
        var queue = new GpuCompletionRetirementQueue(3);
        for (ulong handle = 1; handle <= 3; handle++)
        {
            Assert.That(queue.TryEnqueue(
                Record(handle, handle * 10UL, handle, GpuCompletionToken.ForFrameFence(100UL),
                    GpuRetirementResourceKind.Allocation, handle),
                0UL,
                out _), Is.True);
        }

        GpuRetirementRecord[] drained = new GpuRetirementRecord[3];
        Assert.That(queue.DrainAfterExternalDeviceIdle(drained), Is.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(queue.ActiveCount, Is.Zero);
            Assert.That(queue.ActiveBytes, Is.Zero);
            Assert.That(queue.GetSnapshot(200UL).RetiredCount, Is.EqualTo(3UL));
        });
    }

    private static GpuRetirementRecord Record(
        ulong generation,
        ulong bytes,
        ulong frame,
        GpuCompletionToken completion,
        GpuRetirementResourceKind kind,
        ulong handle) => new(
        generation,
        bytes,
        frame,
        completion,
        new GpuRetirementResource(kind, handle));
}
