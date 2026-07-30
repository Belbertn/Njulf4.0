using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshLifetimeTests
{
    [Test]
    public void BatchPlanning_PreparesMultipleContiguousAppendGenerationsWithoutMutation()
    {
        var table = new MeshSlotLifetimeTable();
        uint[] generations =
        [
            table.GetNextGeneration(0),
            table.GetNextGeneration(1),
            table.GetNextGeneration(2)
        ];

        Assert.That(table.Count, Is.Zero);
        for (int index = 0; index < generations.Length; index++)
            table.CommitSlot(index, generations[index]);

        Assert.Multiple(() =>
        {
            Assert.That(generations, Is.All.EqualTo(1u));
            Assert.That(table.Count, Is.EqualTo(3));
            Assert.That(table.ActiveCount, Is.EqualTo(3));
            Assert.That(
                () => table.CommitSlot(4, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void FinalRelease_ReusesSlotWithNewGeneration_AndRejectsStaleHandle()
    {
        var table = new MeshSlotLifetimeTable();
        var original = new MeshHandle(
            0,
            table.GetNextGeneration(0));
        table.CommitSlot(
            original.Index,
            original.Generation);
        table.Retain(original);

        Assert.That(table.Release(original), Is.False);
        Assert.That(table.Release(original), Is.True);
        Assert.That(table.IsLive(original), Is.False);

        int[] free = table.CaptureAvailableFreeIndices();
        var reserved = new List<int>();
        table.ReservePreparedFreeIndices(
            free,
            1,
            reserved);
        var replacement = new MeshHandle(
            reserved[0],
            table.GetNextGeneration(reserved[0]));
        table.CommitSlot(
            replacement.Index,
            replacement.Generation);

        Assert.Multiple(() =>
        {
            Assert.That(replacement.Index, Is.EqualTo(original.Index));
            Assert.That(
                replacement.Generation,
                Is.EqualTo(original.Generation + 1));
            Assert.That(table.IsLive(replacement), Is.True);
            Assert.That(
                () => table.Retain(original),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void ReuseChurn_RemainsOneSlot_AndEveryPriorHandleStaysStale()
    {
        var table = new MeshSlotLifetimeTable();
        var stale = new List<MeshHandle>(10_000);

        for (int cycle = 0; cycle < 10_000; cycle++)
        {
            int index;
            int[] free = table.CaptureAvailableFreeIndices();
            if (free.Length == 0)
            {
                index = table.Count;
            }
            else
            {
                var reserved = new List<int>(1);
                table.ReservePreparedFreeIndices(
                    free,
                    1,
                    reserved);
                index = reserved[0];
            }

            var current = new MeshHandle(
                index,
                table.GetNextGeneration(index));
            table.CommitSlot(index, current.Generation);
            foreach (MeshHandle prior in stale)
                Assert.That(table.IsLive(prior), Is.False);
            stale.Add(current);
            Assert.That(table.Release(current), Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(table.ActiveCount, Is.Zero);
            Assert.That(table.FreeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void FailedPublication_RestoresSlotsAndFreeReservationExactly()
    {
        var table = new MeshSlotLifetimeTable();
        for (int index = 0; index < 3; index++)
            table.CommitSlot(index, 1);
        Assert.That(
            table.Release(new MeshHandle(1, 1)),
            Is.True);
        Assert.That(
            table.Release(new MeshHandle(2, 1)),
            Is.True);

        int[] available =
            table.CaptureAvailableFreeIndices();
        int[] pending = available.Take(2).ToArray();
        MeshSlotLifetimeTable.RegistrationSnapshot snapshot =
            table.CaptureRegistrationSnapshot(pending);
        var reserved = new List<int>(2);
        table.ReservePreparedFreeIndices(
            available,
            2,
            reserved);
        foreach (int index in reserved)
        {
            table.CommitSlot(
                index,
                table.GetNextGeneration(index));
        }

        table.RestoreRegistrationSnapshot(snapshot);
        table.RestoreReservedFreeIndices(reserved);

        Assert.Multiple(() =>
        {
            Assert.That(table.ActiveCount, Is.EqualTo(1));
            Assert.That(table.FreeCount, Is.EqualTo(2));
            Assert.That(
                table.CaptureAvailableFreeIndices(),
                Is.EqualTo(available));
            Assert.That(
                table.IsLive(new MeshHandle(0, 1)),
                Is.True);
        });
    }

    [Test]
    public void PersistentTailChurn_HitsExplicitDeadByteCapInsteadOfGrowingUnbounded()
    {
        const ulong churnAllocationBytes = 8;
        const ulong maximumDeadBytes = 32;
        ulong retainedBytes = churnAllocationBytes;
        int admittedCycles = 0;

        while (MeshRetentionBudget.CanRetainBelowTail(
                   retainedBytes,
                   maximumDeadBytes))
        {
            retainedBytes += churnAllocationBytes;
            // The prior tail is released after the new tail is appended, so
            // it becomes an interior hole.
            admittedCycles++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(admittedCycles, Is.EqualTo(4));
            Assert.That(
                MeshRetentionBudget.CalculateDeadBytes(
                    retainedBytes,
                    churnAllocationBytes),
                Is.EqualTo(maximumDeadBytes));
            Assert.That(
                MeshRetentionBudget.CanRetainBelowTail(
                    retainedBytes,
                    maximumDeadBytes),
                Is.False);
        });
    }

    [Test]
    public void OutOfOrderRelease_CascadesEveryStreamHighWaterBeforeSlotReuse()
    {
        var table = new MeshSlotLifetimeTable();
        var meshes = new List<MeshInfo>
        {
            CreateMeshInfo(0),
            CreateMeshInfo(10),
            CreateMeshInfo(20)
        };
        for (int index = 0; index < meshes.Count; index++)
            table.CommitSlot(index, 1);

        Assert.That(
            table.Release(new MeshHandle(1, 1)),
            Is.True);
        MeshStreamHighWater afterMiddle =
            MeshStreamLifetimeMetrics.CalculateHighWater(
                meshes,
                table);
        Assert.That(afterMiddle.VertexElements, Is.EqualTo(30));

        Assert.That(
            table.Release(new MeshHandle(2, 1)),
            Is.True);
        MeshStreamHighWater afterTail =
            MeshStreamLifetimeMetrics.CalculateHighWater(
                meshes,
                table);
        Assert.Multiple(() =>
        {
            Assert.That(
                afterTail.VertexElements,
                Is.EqualTo(10));
            Assert.That(
                afterTail.IndexElements,
                Is.EqualTo(10));
            Assert.That(
                afterTail.MeshletElements,
                Is.EqualTo(10));
            Assert.That(
                afterTail.MeshletVertexIndexElements,
                Is.EqualTo(10));
            Assert.That(
                afterTail.MeshletTriangleIndexElements,
                Is.EqualTo(10));
            Assert.That(
                afterTail.SkinningElements,
                Is.EqualTo(10));
        });

        int[] free = table.CaptureAvailableFreeIndices();
        var reserved = new List<int>();
        table.ReservePreparedFreeIndices(free, 1, reserved);
        int reusedIndex = reserved[0];
        meshes[reusedIndex] = CreateMeshInfo(
            checked((uint)afterTail.VertexElements));
        table.CommitSlot(
            reusedIndex,
            table.GetNextGeneration(reusedIndex));
        MeshStreamHighWater afterReuse =
            MeshStreamLifetimeMetrics.CalculateHighWater(
                meshes,
                table);

        Assert.Multiple(() =>
        {
            Assert.That(reusedIndex, Is.EqualTo(2));
            Assert.That(
                afterReuse.VertexElements,
                Is.EqualTo(20));
            Assert.That(
                afterReuse.MeshletElements,
                Is.EqualTo(20));
        });
    }

    [Test]
    public void DeadByteAccounting_RejectsDivergedLiveTotal()
    {
        Assert.That(
            () => MeshRetentionBudget.CalculateDeadBytes(
                retainedBytes: 7,
                liveBytes: 8),
            Throws.InvalidOperationException);
    }

    private static MeshInfo CreateMeshInfo(uint offset) =>
        new()
        {
            VertexOffset = offset,
            VertexCount = 10,
            IndexOffset = offset,
            IndexCount = 10,
            MeshletOffset = offset,
            MeshletLodGeneratedCount = 10,
            LocalVertexIndexOffset = offset,
            LocalVertexIndexCount = 10,
            LocalTriangleIndexOffset = offset,
            LocalTriangleIndexCount = 10,
            SkinningDataOffset = offset,
            SkinningDataCount = 10
        };
}
