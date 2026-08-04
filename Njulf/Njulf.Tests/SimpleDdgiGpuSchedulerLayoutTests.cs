using System;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerLayoutTests
{
    [Test]
    public void Layout_IsAlignedNonOverlappingAndUsesActiveProbeOutputCapacity()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            activeProbeCount: 15368,
            requestCapacity: 2048,
            activeVolumeCount: 3,
            dirtyRegionCapacity: 32,
            validationEnabled: true);

        ulong previousEnd = 0;
        foreach (SimpleDdgiSchedulerArenaRegion region in layout.Regions)
        {
            Assert.That(region.Offset % SimpleDdgiGpuSchedulerLayout.ArenaAlignmentBytes, Is.EqualTo(0), region.Name);
            Assert.That(region.ByteSize % SimpleDdgiGpuSchedulerLayout.ArenaAlignmentBytes, Is.EqualTo(0), region.Name);
            Assert.That(region.Offset, Is.GreaterThanOrEqualTo(previousEnd), region.Name);
            Assert.That(region.End, Is.LessThanOrEqualTo(layout.TotalBytes), region.Name);
            previousEnd = region.End;
        }

        Assert.Multiple(() =>
        {
            Assert.That(layout.CandidateInput.ElementCount, Is.EqualTo(15368));
            Assert.That(layout.CandidateOutput.ElementCount, Is.EqualTo(15368));
            Assert.That(layout.UpdateRecords.ElementCount, Is.EqualTo(2048));
            Assert.That(layout.Outcomes.ElementCount, Is.EqualTo(2048));
            Assert.That(layout.ActiveLaneCount, Is.EqualTo(3 * 7 * 4 * 2));
            Assert.That(layout.ValidationReadbackBytes, Is.EqualTo(SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes));
            Assert.That(layout.FeedbackSummary.ByteSize, Is.EqualTo(4096));
        });
    }

    [Test]
    public void Layout_IndirectSlotsAreFixedAndAligned()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(64, 16, 1);

        for (SimpleDdgiSchedulerDispatchSlot slot = 0;
             slot < SimpleDdgiSchedulerDispatchSlot.Count;
             slot++)
        {
            SimpleDdgiSchedulerArenaRegion command = layout.GetIndirectCommand(slot);
            Assert.Multiple(() =>
            {
                Assert.That(command.Offset % 16, Is.EqualTo(0));
                Assert.That(command.ByteSize, Is.EqualTo(16));
                Assert.That(command.Offset, Is.EqualTo(
                    layout.IndirectCommands.Offset + (ulong)slot * 16UL));
            });
        }
    }

    [Test]
    public void Layout_RejectsStorageRangeOverflow()
    {
        Assert.That(
            () => SimpleDdgiGpuSchedulerLayout.Create(1024, 256, 2, maxStorageBufferRange: 1024),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Layout_GroupMathAndAbiSizesRemainPinned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(0), Is.EqualTo(0));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(1), Is.EqualTo(1));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(64), Is.EqualTo(1));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(65), Is.EqualTo(2));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerFrame>(), Is.EqualTo(160));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerVolumePolicy>(), Is.EqualTo(160));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerCandidate>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiUpdateOutcome>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerFeedback>(), Is.EqualTo(256));
        });
    }

    [Test]
    public void MaximumArenaFitsTheShippingSixMiBSchedulerBudget()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);

        Assert.That(layout.TotalBytes, Is.LessThanOrEqualTo(6UL * 1024UL * 1024UL));
        Assert.That(layout.CandidateInput.ElementStride, Is.EqualTo(28));
        Assert.That(layout.CandidateOutput.ElementStride, Is.EqualTo(sizeof(uint)));
    }
}
