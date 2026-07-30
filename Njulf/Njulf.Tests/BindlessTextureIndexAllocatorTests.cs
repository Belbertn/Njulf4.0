using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BindlessTextureIndexAllocatorTests
{
    [Test]
    public void DescriptorPublicationFailure_DoesNotConsumeCandidate()
    {
        var allocator = new BindlessTextureIndexAllocator(10, 13);

        int firstAttempt = allocator.GetAllocationCandidate();
        int retry = allocator.GetAllocationCandidate();

        Assert.Multiple(() =>
        {
            Assert.That(firstAttempt, Is.EqualTo(10));
            Assert.That(retry, Is.EqualTo(firstAttempt));
            Assert.That(allocator.Used, Is.Zero);
            Assert.That(allocator.HighWater, Is.Zero);
        });
    }

    [Test]
    public void AllocationAndReuse_TrackAuthoritativeOccupancyAndHighWater()
    {
        var allocator = new BindlessTextureIndexAllocator(10, 14);

        Allocate(allocator, 10);
        Allocate(allocator, 11);
        Allocate(allocator, 12);
        allocator.Free(11);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.Used, Is.EqualTo(2));
            Assert.That(allocator.HighWater, Is.EqualTo(3));
            Assert.That(allocator.GetAllocationCandidate(), Is.EqualTo(11));
        });

        Allocate(allocator, 11);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.Used, Is.EqualTo(3));
            Assert.That(allocator.HighWater, Is.EqualTo(3));
            Assert.That(allocator.GetAllocationCandidate(), Is.EqualTo(13));
        });
    }

    [Test]
    public void Free_RejectsStaticForeignAndDuplicateIndicesWithoutCorruption()
    {
        var allocator = new BindlessTextureIndexAllocator(10, 13);
        Allocate(allocator, 10);
        allocator.Free(10);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => allocator.Free(9),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => allocator.Free(11),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                () => allocator.Free(10),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(allocator.Used, Is.Zero);
            Assert.That(allocator.GetAllocationCandidate(), Is.EqualTo(10));
        });
    }

    [Test]
    public void Capacity_IsExactAndExhaustionDoesNotMutateState()
    {
        var allocator = new BindlessTextureIndexAllocator(10, 12);
        Allocate(allocator, 10);
        Allocate(allocator, 11);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.Capacity, Is.EqualTo(2));
            Assert.That(allocator.Used, Is.EqualTo(2));
            Assert.That(allocator.HighWater, Is.EqualTo(2));
            Assert.That(
                allocator.GetAllocationCandidate,
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(allocator.Used, Is.EqualTo(2));
        });
    }

    [Test]
    public void Commit_RejectsAnythingExceptCurrentCandidate()
    {
        var allocator = new BindlessTextureIndexAllocator(10, 13);

        Assert.That(
            () => allocator.CommitAllocation(11),
            Throws.TypeOf<InvalidOperationException>());
        Assert.Multiple(() =>
        {
            Assert.That(allocator.Used, Is.Zero);
            Assert.That(allocator.GetAllocationCandidate(), Is.EqualTo(10));
        });
    }

    private static void Allocate(
        BindlessTextureIndexAllocator allocator,
        int expected)
    {
        int candidate = allocator.GetAllocationCandidate();
        Assert.That(candidate, Is.EqualTo(expected));
        allocator.CommitAllocation(candidate);
    }
}
