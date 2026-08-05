using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiProbePageLayoutTests
{
    [Test]
    public void CurrentHighNearRing_UsesExactPageGeometryAndBoundedArena()
    {
        int pages = SimpleDdgiProbePageLayout.ResolveVirtualPageCount(28, 14, 28);
        SimpleDdgiProbePageLayout layout = SimpleDdgiProbePageLayout.Create(
            pages,
            sparsePhysicalPageCapacity: 960,
            maximumAdmissionsPerFrame: 64);

        Assert.Multiple(() =>
        {
            Assert.That(pages, Is.EqualTo(1_372));
            Assert.That(layout.TotalBytes,
                Is.LessThanOrEqualTo(SimpleDdgiProbePageLayout.CurrentProfileOverheadGateBytes));
            Assert.That(layout.TotalBytes % 16UL, Is.Zero);
            Assert.That(layout.PageTableOffset % 16UL, Is.Zero);
            Assert.That(layout.PhysicalMetadataOffset % 16UL, Is.Zero);
            Assert.That(layout.FeedbackOffset % 16UL, Is.Zero);
        });
    }

    [Test]
    public void ToroidalAddress_RoundTripsNegativeMultiAxisOffsets()
    {
        var volume = new SimpleDdgiVolumePageLayout(
            VirtualFirstProbe: 100,
            PageTableFirst: 20,
            DensePhysicalFirstProbe: 100,
            SparsePoolFirstProbe: 4_000,
            GridCountX: 7,
            GridCountY: 5,
            GridCountZ: 9,
            PhysicalOffsetX: -9,
            PhysicalOffsetY: 12,
            PhysicalOffsetZ: -20,
            ResidencyMode: SimpleDdgiProbeResidencyMode.SparseNearRing);

        for (int z = 0; z < volume.GridCountZ; z++)
        for (int y = 0; y < volume.GridCountY; y++)
        for (int x = 0; x < volume.GridCountX; x++)
        {
            SimpleDdgiVirtualPageAddress address =
                SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(volume, x, y, z);
            int localPage = address.VirtualPageIndex - volume.PageTableFirst;
            bool valid = SimpleDdgiProbePageLayout.TryResolveVirtualProbeFromPage(
                volume,
                localPage,
                address.PageLocalProbeIndex,
                out int roundTrip);
            Assert.That(valid, Is.True);
            Assert.That(roundTrip, Is.EqualTo(address.VirtualProbeIndex));
        }
    }

    [Test]
    public void OneCellScroll_PreservesEveryOldWorldCellAndReusesOnlyTheExposedSlice()
    {
        var before = new SimpleDdgiVolumePageLayout(
            VirtualFirstProbe: 0,
            PageTableFirst: 0,
            DensePhysicalFirstProbe: 0,
            SparsePoolFirstProbe: 1_000,
            GridCountX: 6,
            GridCountY: 4,
            GridCountZ: 6,
            PhysicalOffsetX: -3,
            PhysicalOffsetY: 1,
            PhysicalOffsetZ: 8,
            ResidencyMode: SimpleDdgiProbeResidencyMode.SparseNearRing);
        SimpleDdgiVolumePageLayout after = before with
        {
            PhysicalOffsetX = before.PhysicalOffsetX + 1
        };

        for (int z = 0; z < before.GridCountZ; z++)
        for (int y = 0; y < before.GridCountY; y++)
        for (int oldX = 1; oldX < before.GridCountX; oldX++)
        {
            SimpleDdgiVirtualPageAddress oldAddress =
                SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(
                    before,
                    oldX,
                    y,
                    z);
            SimpleDdgiVirtualPageAddress preservedAddress =
                SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(
                    after,
                    oldX - 1,
                    y,
                    z);
            Assert.That(preservedAddress, Is.EqualTo(oldAddress));
        }

        for (int z = 0; z < before.GridCountZ; z++)
        for (int y = 0; y < before.GridCountY; y++)
        {
            SimpleDdgiVirtualPageAddress droppedAddress =
                SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(
                    before,
                    0,
                    y,
                    z);
            SimpleDdgiVirtualPageAddress exposedAddress =
                SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(
                    after,
                    after.GridCountX - 1,
                    y,
                    z);
            Assert.That(exposedAddress.VirtualProbeIndex,
                Is.EqualTo(droppedAddress.VirtualProbeIndex));
            Assert.That(exposedAddress.VirtualPageIndex,
                Is.EqualTo(droppedAddress.VirtualPageIndex));
            Assert.That(exposedAddress.PageLocalProbeIndex,
                Is.EqualTo(droppedAddress.PageLocalProbeIndex));
        }
    }

    [Test]
    public void OddGrid_PaddedPageSlotsFailClosed()
    {
        var volume = new SimpleDdgiVolumePageLayout(
            0, 0, 0, 0,
            GridCountX: 3,
            GridCountY: 3,
            GridCountZ: 3,
            0, 0, 0,
            SimpleDdgiProbeResidencyMode.SparseNearRing);

        Assert.That(volume.VirtualPageCount, Is.EqualTo(8));
        Assert.That(
            SimpleDdgiProbePageLayout.TryResolveVirtualProbeFromPage(
                volume,
                volumeLocalPageIndex: 7,
                pageLocalProbeIndex: 7,
                out _),
            Is.False);
    }

    [Test]
    public void SparseAddress_RequiresTableReverseAndResourceGenerationAgreement()
    {
        var volume = new SimpleDdgiVolumePageLayout(
            0, 0, 0, 500,
            4, 4, 4,
            0, 0, 0,
            SimpleDdgiProbeResidencyMode.SparseNearRing);
        SimpleDdgiVirtualPageAddress virtualAddress =
            SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(volume, 3, 1, 2);
        var entry = new SimpleDdgiPageTableEntry(
            PhysicalPagePlusOne: 4,
            MappingGeneration: 19,
            Flags: SimpleDdgiPageTableEntry.ValidFlag,
            Reserved: 0);
        var owner = new SimpleDdgiPhysicalPageOwner(
            virtualAddress.VirtualPageIndex,
            19,
            7,
            0,
            0,
            0);

        SimpleDdgiProbeAddress valid = SimpleDdgiProbePageLayout.ResolveProbeAddress(
            volume,
            virtualAddress,
            entry,
            owner,
            residencyResourceGeneration: 7);
        SimpleDdgiProbeAddress stale = SimpleDdgiProbePageLayout.ResolveProbeAddress(
            volume,
            virtualAddress,
            entry,
            owner with { MappingGeneration = 18 },
            residencyResourceGeneration: 7);

        Assert.Multiple(() =>
        {
            Assert.That(valid.Resident, Is.True);
            Assert.That(valid.PhysicalProbeIndex,
                Is.EqualTo((uint)(500 + 3 * 8 + virtualAddress.PageLocalProbeIndex)));
            Assert.That(stale.Resident, Is.False);
            Assert.That(stale.PhysicalProbeIndex,
                Is.EqualTo(SimpleDdgiProbeAddress.InvalidPhysicalProbeIndex));
        });
    }

    [Test]
    public void DenseAddress_IsIdentityAndDoesNotReadPageOwnership()
    {
        var volume = new SimpleDdgiVolumePageLayout(
            40, 0, 40, 0,
            4, 4, 4,
            0, 0, 0,
            SimpleDdgiProbeResidencyMode.Dense);
        SimpleDdgiVirtualPageAddress virtualAddress =
            SimpleDdgiProbePageLayout.ResolveVirtualPageAddress(volume, 2, 1, 3);
        SimpleDdgiProbeAddress address = SimpleDdgiProbePageLayout.ResolveProbeAddress(
            volume,
            virtualAddress,
            default,
            default,
            0);

        Assert.That(address.Resident, Is.True);
        Assert.That(address.PhysicalProbeIndex, Is.EqualTo(address.VirtualProbeIndex));
        Assert.That(address.PageMappingGeneration,
            Is.EqualTo(SimpleDdgiProbeAddress.DenseMappingGeneration));
    }

    [Test]
    public void LayoutRejectsOverflowAndInvalidCapacityBeforeAllocation()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SimpleDdgiProbePageLayout.Create(10, 11, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SimpleDdgiProbePageLayout.Create(10, 5, 6));
            Assert.Throws<InvalidOperationException>(() =>
                SimpleDdgiProbePageLayout.Create(10, 5, 2, maxStorageBufferRange: 16));
        });
    }

    [Test]
    public void HardVirtualLimitResidencyArenaRemainsWithinOneMiB()
    {
        int virtualPages =
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount /
            SimpleDdgiProbePageLayout.ProbesPerPage;
        SimpleDdgiProbePageLayout layout =
            SimpleDdgiProbePageLayout.Create(
                virtualPages,
                virtualPages,
                virtualPages);

        Assert.Multiple(() =>
        {
            Assert.That(layout.TotalBytes,
                Is.LessThanOrEqualTo(
                    SimpleDdgiProbePageLayout.HardLimitOverheadGateBytes));
            Assert.That(layout.HeaderOffset % 16UL, Is.Zero);
            Assert.That(layout.PageTableOffset % 16UL, Is.Zero);
            Assert.That(layout.PhysicalMetadataOffset % 16UL, Is.Zero);
            Assert.That(layout.FeedbackOffset % 16UL, Is.Zero);
        });
    }

    [Test]
    public void TransactionPolicy_IsPartOfImmutableArenaIdentity()
    {
        var baseline = new SimpleDdgiProbePageTransactionPolicy(
            RetentionFrames: 120,
            MaximumAdmissionsPerFrame: 64,
            MaximumReceiverFeedbackRequests: 2_048,
            InactiveRetryFrames: 300).Validate(960);

        Assert.Multiple(() =>
        {
            Assert.That(baseline, Is.Not.EqualTo(
                baseline with { RetentionFrames = 121 }));
            Assert.That(baseline, Is.Not.EqualTo(
                baseline with { MaximumAdmissionsPerFrame = 63 }));
            Assert.That(baseline, Is.Not.EqualTo(
                baseline with { MaximumReceiverFeedbackRequests = 2_047 }));
            Assert.That(baseline, Is.Not.EqualTo(
                baseline with { InactiveRetryFrames = 301 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                (baseline with { RetentionFrames = 0 }).Validate(960));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                (baseline with { MaximumAdmissionsPerFrame = 961 })
                    .Validate(960));
        });
    }

    [Test]
    public void GenerationAndDemandEpochWraps_NeverPublishZeroOrReuseAnUnclearedEpoch()
    {
        uint wrapFrame = SimpleDdgiProbePageLayout.DemandEpochMask - 1u;

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiProbePageLayout.AdvanceNonZeroGeneration(0u),
                Is.EqualTo(1u));
            Assert.That(
                SimpleDdgiProbePageLayout.AdvanceNonZeroGeneration(uint.MaxValue),
                Is.EqualTo(1u));
            Assert.That(
                SimpleDdgiProbePageLayout.DemandEpochRequiresResourceTransaction(
                    wrapFrame),
                Is.True);
            Assert.That(
                SimpleDdgiProbePageLayout.DemandEpochForFrame(wrapFrame),
                Is.EqualTo(1u));
            Assert.That(
                SimpleDdgiProbePageLayout.DemandEpochRequiresResourceTransaction(
                    wrapFrame - 1u),
                Is.False);
        });
    }
}
