using System.IO.Hashing;
using System.Runtime.InteropServices;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshletPhysicalResidencyTests
{
    [Test]
    public void VirtualAddress_PreservesDirectAndVirtualDomains()
    {
        uint encoded = MeshletVirtualAddress.Encode(1234);
        uint resolved = MeshletVirtualAddress.EncodeResolved(1234);
        Assert.Multiple(() =>
        {
            Assert.That(MeshletVirtualAddress.IsVirtual(1234), Is.False);
            Assert.That(MeshletVirtualAddress.IsVirtual(encoded), Is.True);
            Assert.That(MeshletVirtualAddress.IsResolved(encoded), Is.False);
            Assert.That(MeshletVirtualAddress.IsVirtual(resolved), Is.False);
            Assert.That(MeshletVirtualAddress.IsResolved(resolved), Is.True);
            Assert.That(MeshletVirtualAddress.Decode(encoded),
                Is.EqualTo(1234));
            Assert.That(MeshletVirtualAddress.DecodeResolved(resolved),
                Is.EqualTo(1234));
            Assert.That(
                () => MeshletVirtualAddress.Encode(
                    MeshletVirtualAddress.IndexMask + 1u),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => MeshletVirtualAddress.EncodeResolved(
                    MeshletVirtualAddress.IndexMask + 1u),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void ResolvedMapping_PacksPhysicalBankAndWordAddress()
    {
        uint packed = GPUMeshletResolvedMapping.PackAddress(
            bankIndex: 15,
            wordOffset: GPUMeshletResolvedMapping.WordMask);

        Assert.Multiple(() =>
        {
            Assert.That(packed >> 24, Is.EqualTo(15u));
            Assert.That(
                packed & GPUMeshletResolvedMapping.WordMask,
                Is.EqualTo(GPUMeshletResolvedMapping.WordMask));
            Assert.That(GPUMeshletResolvedMapping.Invalid.IsValid, Is.False);
            Assert.That(
                () => GPUMeshletResolvedMapping.PackAddress(16, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void ManagedCpuCache_ResolvesOwnedVirtualAddressesAndRejectsStaleState()
    {
        var cache = new ManagedCpuMeshletCache();
        MeshHandle handle = new(4, 7);
        MeshInfo meshInfo = CreateManagedMeshInfo(
            virtualBase: 20,
            rangeBase: 40);
        Meshlet[] meshlets =
        [
            CreateCpuMeshlet(101),
            CreateCpuMeshlet(202),
            CreateCpuMeshlet(303)
        ];

        cache.EnsureCapacity(1);
        cache.ValidatePrepared(handle, meshInfo, meshlets);
        cache.Commit(handle, meshInfo, meshlets);

        Assert.Multiple(() =>
        {
            Assert.That(
                cache.Get(handle, meshInfo,
                    MeshletVirtualAddress.Encode(20)).VertexOffset,
                Is.EqualTo(101));
            Assert.That(
                cache.Get(handle, meshInfo,
                    MeshletVirtualAddress.Encode(22)).VertexOffset,
                Is.EqualTo(303));
            Assert.That(
                () => cache.Get(
                    new MeshHandle(handle.Index, handle.Generation + 1),
                    meshInfo,
                    MeshletVirtualAddress.Encode(20)),
                Throws.InvalidOperationException);
            Assert.That(
                () => cache.Get(
                    handle,
                    meshInfo,
                    MeshletVirtualAddress.Encode(23)),
                Throws.InvalidOperationException);
        });

        cache.ValidateRelease(handle.Index, meshInfo);
        cache.Release(handle.Index);
        Assert.That(
            () => cache.Get(
                handle,
                meshInfo,
                MeshletVirtualAddress.Encode(20)),
            Throws.InvalidOperationException);

        MeshHandle replacement = new(handle.Index, handle.Generation + 1);
        cache.Commit(replacement, meshInfo, meshlets);
        Assert.That(
            cache.Get(
                replacement,
                meshInfo,
                MeshletVirtualAddress.Encode(21)).VertexOffset,
            Is.EqualTo(202));
        cache.RemovePreparedSlots([replacement.Index]);
        Assert.That(cache.Count, Is.Zero);
    }

    [Test]
    public void PhysicalUploader_RecordsAnImmutableExactFrameRangeSnapshot()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        uploader.SetRangeReady(37, ready: true);
        MeshletPhysicalFrameStateSnapshot recorded =
            uploader.CaptureFrameStateForRecording(0);

        uploader.SetRangeReady(37, ready: false);

        Assert.Multiple(() =>
        {
            Assert.That(recorded.RangeStateRevision, Is.Not.Zero);
            Assert.That(
                uploader.GetRecordedRangeStateRevision(0),
                Is.EqualTo(recorded.RangeStateRevision));
            Assert.That(
                uploader.IsRecordedRangeReady(37, 0),
                Is.True,
                "Worker mutations after command recording must not change CPU selection for that frame.");
            Assert.That(uploader.IsRangeReady(37, 0), Is.False);
        });
    }

    [Test]
    public async Task PhysicalUploader_FrameSnapshotKeepsMatchingPackedPageGeneration()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        MeshletPageUploadTicket first = await uploader.BeginUploadAsync(
            pageId: 0,
            physicalSlot: 0,
            CreateDecodedPage(vertexOffset: 2),
            submissionSerial: 0);
        uploader.PublishResident(first.PageId, first.PhysicalSlot);
        MeshletPhysicalFrameStateSnapshot recorded =
            uploader.CaptureFrameStateForRecording(0);
        byte[] recordedBytes = recorded.GetPackedPage(0, 0).ToArray();

        uploader.UnpublishResident(0, 0, retireAfterSerial: 1);
        MeshletPageUploadTicket replacement = await uploader.BeginUploadAsync(
            pageId: 1,
            physicalSlot: 0,
            CreateDecodedPage(vertexOffset: 19),
            submissionSerial: 1);
        uploader.PublishResident(replacement.PageId, replacement.PhysicalSlot);

        Assert.Multiple(() =>
        {
            Assert.That(
                recorded.GetPackedPage(0, 0).ToArray()
                    .SequenceEqual(recordedBytes),
                Is.True,
                "A recorded table must retain the packed bytes from its own generation.");
            Assert.That(
                uploader.GetPackedPage(0).ToArray().SequenceEqual(recordedBytes),
                Is.False,
                "The live slot should contain the replacement generation.");
            Assert.That(
                () => recorded.GetPackedPage(1, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>(),
                "A physical slot must not alias a different global page in the recorded snapshot.");
        });
    }

    [Test]
    public async Task Coordinator_BootstrapRequiresPinnedRangeReadyInBothFrameSlots()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(4));
        Assert.That(coordinator.TryRegisterPackage(
            "package-bootstrap-range-state",
            CreateSource(),
            out MeshletStreamingPackageHandle? handle,
            out _), Is.True);
        using (handle)
        {
            Assert.That(uploader.TryCaptureImmutableContracts(
                out _,
                out _,
                out ulong revision), Is.True);
            uploader.MarkImmutableContractsRecorded(revision, 0);
            await coordinator.TickAsync(0, 0);
            await coordinator.TickAsync(1, 1);

            MeshletStreamingSubMeshGpuBinding binding =
                handle!.GetSubMeshGpuBinding(0);
            int pinnedRangeIndex = checked((int)binding.Lod2RangeIndex);
            uploader.PrepareFrameSlot(1, 0);
            uploader.SetRangeReady(pinnedRangeIndex, ready: false);

            Assert.That(
                handle.IsPinnedBootstrapComplete,
                Is.False,
                "Pinned mappings alone must not publish a model without both frame-local range bits.");

            uploader.SetRangeReady(pinnedRangeIndex, ready: true);
            Assert.That(handle.IsPinnedBootstrapComplete, Is.True);
        }
    }

    [Test]
    public void GpuPagePacker_ProducesExactPageAndRebasesVertices()
    {
        byte[] decoded = CreateDecodedPage(vertexOffset: 2);
        MeshletGpuPagePackResult packed = MeshletGpuPagePacker.Pack(
            decoded,
            globalVertexOffset: 10);
        GPUMeshletPhysicalPageHeader header =
            MeshletGpuPagePacker.ReadHeader(packed.PageBytes);
        ReadOnlySpan<byte> recordBytes = packed.PageBytes.AsSpan(
            checked((int)header.MeshletWordOffset * sizeof(uint)),
            Marshal.SizeOf<GPUPackedMeshlet>());
        GPUPackedMeshlet meshlet =
            MemoryMarshal.Read<GPUPackedMeshlet>(recordBytes);

        Assert.Multiple(() =>
        {
            Assert.That(packed.PageBytes, Has.Length.EqualTo(64 * 1024));
            Assert.That(header.MeshletCount, Is.EqualTo(1));
            Assert.That(meshlet.VertexOffset, Is.EqualTo(12));
            Assert.That(meshlet.LocalVertexOffset, Is.Zero);
            Assert.That(meshlet.LocalTriangleOffset, Is.Zero);
            Assert.That(
                packed.PageBytes.AsSpan(packed.UsedBytes).ToArray(),
                Is.All.Zero);
        });
    }

    [Test]
    public void ActivationPlanner_RejectsOversubscribedCompleteWorkingSet()
    {
        CookedMeshPayload payload = CreateActivationPayload(
            streamablePageCount: 2,
            fullResidentBytes: 128 * 1024 * 1024);

        MeshletStreamingActivationPlan optimized =
            MeshletStreamingActivationPlanner.Evaluate(
                payload,
                streamingEnabled: true,
                configuredPhysicalPageCount: 4,
                alreadyRegisteredPageCount: 2,
                requireCompleteWorkingSet: true);
        MeshletStreamingActivationPlan baseline =
            MeshletStreamingActivationPlanner.Evaluate(
                payload,
                streamingEnabled: true,
                configuredPhysicalPageCount: 4,
                alreadyRegisteredPageCount: 0,
                requireCompleteWorkingSet: false);

        Assert.Multiple(() =>
        {
            Assert.That(optimized.Active, Is.False);
            Assert.That(
                optimized.FallbackReason,
                Is.EqualTo("complete-working-set-exceeds-cache"));
            Assert.That(baseline.Active, Is.True);
        });
    }

    [Test]
    public void Coordinator_CompleteWorkingSetAdmissionIsRaceSafe()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(4));
        Assert.That(coordinator.TryRegisterPackage(
            "complete-package-a",
            CreateSource(),
            out MeshletStreamingPackageHandle? first,
            out _,
            requireCompleteWorkingSet: true), Is.True);
        using (first)
        {
            Assert.That(coordinator.TryRegisterPackage(
                "complete-package-b",
                CreateSource(),
                out MeshletStreamingPackageHandle? second,
                out string reason,
                requireCompleteWorkingSet: true), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(second, Is.Null);
                Assert.That(
                    reason,
                    Is.EqualTo(
                        "complete-working-set-exceeds-global-cache"));
                Assert.That(
                    coordinator.CreateSnapshot().PackageCount,
                    Is.EqualTo(1));
            });
        }
    }

    [Test]
    public void ResolvedMappingTable_UpdatesChangedPagesAndRejectsFalseReadiness()
    {
        MeshletGpuPagePackResult packed = MeshletGpuPagePacker.Pack(
            CreateDecodedPage(vertexOffset: 2),
            globalVertexOffset: 10);
        GPUMeshletPhysicalPageHeader header =
            MeshletGpuPagePacker.ReadHeader(packed.PageBytes);
        var table = new MeshletResolvedMappingTable();
        table.SetContracts(
            [new GPUMeshletVirtualMapping(0, 0, 0, 10)],
            [new GPUMeshletStreamingRange(
                0, 1, 0, 1,
                MeshletStreamingRangeFlags.PinnedFallback,
                uint.MaxValue, 0, 0)]);
        GPUMeshletPageTableEntry[] resident =
        [
            new GPUMeshletPageTableEntry(
                0, 0, 1, MeshletGpuPageTableFlags.Resident)
        ];

        MeshletResolvedMappingUpdate first = table.Update(
            0,
            resident,
            [1u],
            (_, _) => packed.PageBytes);
        GPUMeshletResolvedMapping firstMapping = first.Mappings[0];
        uint firstPublishedRangeState = first.PublishedRangeStateWords[0];
        int firstDirtyRangeCount = first.DirtyRanges.Count;
        MeshletResolvedMappingUpdate unchanged = table.Update(
            0,
            resident,
            [1u],
            (_, _) => packed.PageBytes);
        MeshletResolvedMappingUpdate missing = table.Update(
            0,
            [GPUMeshletPageTableEntry.Unmapped],
            [1u],
            (_, _) => packed.PageBytes);

        Assert.Multiple(() =>
        {
            Assert.That(firstMapping.IsValid, Is.True);
            Assert.That(
                firstMapping.MeshletRecordAddress,
                Is.EqualTo(header.MeshletWordOffset));
            Assert.That(firstMapping.VertexOffset, Is.EqualTo(10u));
            Assert.That(firstPublishedRangeState & 1u,
                Is.EqualTo(1u));
            Assert.That(firstDirtyRangeCount, Is.EqualTo(1));
            Assert.That(unchanged.DirtyRanges, Is.Empty);
            Assert.That(missing.Mappings[0].IsValid, Is.False);
            Assert.That(missing.PublishedRangeStateWords[0] & 1u,
                Is.Zero);
            Assert.That(missing.InvalidReadyRanges,
                Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void PhysicalBanks_GrowLazilyAndRespectBudget()
    {
        var budget = new FixedBankBudget(1);
        using var banks = new MeshletPhysicalBankAllocator(
            MeshletPhysicalBankAllocator.PagesPerBank * 2,
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(banks.CreateSnapshot().CommittedBankCount, Is.Zero);
            Assert.That(banks.EnsureSlotAvailable(0, out _), Is.True);
            Assert.That(banks.CreateSnapshot().CommittedBankCount,
                Is.EqualTo(1));
            Assert.That(
                banks.EnsureSlotAvailable(
                    MeshletPhysicalBankAllocator.PagesPerBank,
                    out string reason),
                Is.False);
            Assert.That(reason, Does.Contain("synthetic-budget"));
        });
    }

    [Test]
    public async Task PhysicalUploader_KeepsFrameTablesFenceSafe()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(64);
        MeshletPageUploadTicket ticket = await uploader.BeginUploadAsync(
            pageId: 7,
            physicalSlot: 0,
            CreateDecodedPage(0),
            submissionSerial: 0);
        uploader.PublishResident(ticket.PageId, ticket.PhysicalSlot);
        Assert.That(uploader.TryResolve(7, 0, out _), Is.True);

        uploader.PrepareFrameSlot(writableFrameSlot: 1, sourceFrameSlot: 0);
        uploader.UnpublishResident(7, 0, retireAfterSerial: 3);

        Assert.Multiple(() =>
        {
            Assert.That(uploader.TryResolve(7, 0, out _), Is.True);
            Assert.That(uploader.TryResolve(7, 1, out _), Is.False);
            Assert.That(uploader.GetPackedPage(0).Length,
                Is.EqualTo(64 * 1024));
        });
    }

    [Test]
    public async Task Coordinator_UsesUniqueGlobalIdsAndWholeRangeFallback()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(4));
        Assert.That(coordinator.TryRegisterPackage(
            "package-a",
            CreateSource(),
            out MeshletStreamingPackageHandle? first,
            out _), Is.True);
        Assert.That(coordinator.TryRegisterPackage(
            "package-b",
            CreateSource(),
            out MeshletStreamingPackageHandle? second,
            out _), Is.True);
        using (first)
        using (second)
        {
            Assert.That(second!.GlobalPageBase,
                Is.EqualTo(first!.PageCount));
            await coordinator.TickAsync(0, 0);
            await coordinator.TickAsync(1, 1);

            MeshletStreamingRangeResolution fallback = first.RequestRange(
                0,
                MeshletStreamingPageFlags.Lod0,
                MeshletStreamingResidencyCoordinator.VisiblePriority,
                1);
            Assert.Multiple(() =>
            {
                Assert.That(fallback.IsComplete, Is.True);
                Assert.That(fallback.UsesFallback, Is.True);
                Assert.That(fallback.Pages, Has.Count.EqualTo(1));
                Assert.That(
                    fallback.Pages[0].ResolvedGlobalPageId,
                    Is.Not.EqualTo(second.GetGlobalPageId(2)));
            });
            await coordinator.TickAsync(1, 1);
            await coordinator.TickAsync(2, 2);
            MeshletStreamingRangeResolution fine = first.ResolveRange(
                0,
                MeshletStreamingPageFlags.Lod0);
            Assert.Multiple(() =>
            {
                Assert.That(fine.IsComplete, Is.True);
                Assert.That(fine.UsesFallback, Is.False);
                Assert.That(first.CanDitherBetweenRanges(
                    0,
                    MeshletStreamingPageFlags.Lod0,
                    MeshletStreamingPageFlags.Lod2), Is.True);
            });
        }
    }

    [Test]
    public async Task FrameResolver_RequestsFineRangeAndSelectsCompletePinnedFallback()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(4));
        Assert.That(coordinator.TryRegisterPackage(
            "package-cpu-frame-resolver",
            CreateSource(),
            out MeshletStreamingPackageHandle? handle,
            out _), Is.True);
        using (handle)
        {
            MeshletStreamingSubMeshGpuBinding binding =
                handle!.GetSubMeshGpuBinding(0);
            MeshInfo meshInfo = CreateManagedMeshInfo(
                binding.VirtualMeshletBase,
                binding.Lod0RangeIndex);
            var resolver = new MeshletFrameResidencyResolver(
                coordinator,
                uploader);
            var baselineResolver = new MeshletFrameResidencyResolver(
                coordinator,
                uploader,
                resolvedAddressingEnabled: false);
            _ = uploader.CaptureFrameStateForRecording(0);
            MeshletFrameRangeResolution unavailable = resolver.Resolve(
                meshInfo,
                requestedLod: 0,
                frameSlot: 0);
            Assert.That(
                unavailable.IsComplete,
                Is.False,
                "CPU submission must not read a virtual range before its exact frame snapshot is complete.");

            await coordinator.TickAsync(0, 0);
            await coordinator.TickAsync(1, 1);
            _ = uploader.CaptureFrameStateForRecording(0);

            MeshletFrameRangeResolution fallback = resolver.Resolve(
                meshInfo,
                requestedLod: 0,
                frameSlot: 0);
            int accepted = resolver.RequestRanges(
                [fallback.RequestedRangeIndex,
                 fallback.RequestedRangeIndex]);

            Assert.Multiple(() =>
            {
                Assert.That(fallback.IsComplete, Is.True);
                Assert.That(fallback.UsesFallback, Is.True);
                Assert.That(fallback.EffectiveLod, Is.EqualTo(2));
                Assert.That(
                    fallback.FirstMeshletAddress,
                    Is.EqualTo(MeshletVirtualAddress.EncodeResolved(
                        MeshletVirtualAddress.Decode(
                            meshInfo.MeshletLod2Offset))));
                Assert.That(
                    baselineResolver.Resolve(
                        meshInfo,
                        requestedLod: 0,
                        frameSlot: 0).FirstMeshletAddress,
                    Is.EqualTo(meshInfo.MeshletLod2Offset));
                Assert.That(accepted, Is.EqualTo(1));
                Assert.That(
                    coordinator.GetState(handle.PackageId, 0),
                    Is.EqualTo(MeshletPageResidencyState.Queued));
            });

            await coordinator.TickAsync(2, 2);
            await coordinator.TickAsync(3, 3);
            _ = uploader.CaptureFrameStateForRecording(0);
            MeshletFrameRangeResolution fine = resolver.Resolve(
                meshInfo,
                requestedLod: 0,
                frameSlot: 0);
            Assert.Multiple(() =>
            {
                Assert.That(fine.IsComplete, Is.True);
                Assert.That(fine.UsesFallback, Is.False);
                Assert.That(fine.EffectiveLod, Is.Zero);
                Assert.That(
                    fine.FirstMeshletAddress,
                    Is.EqualTo(MeshletVirtualAddress.EncodeResolved(
                        MeshletVirtualAddress.Decode(
                            meshInfo.MeshletOffset))));
            });
        }
    }

    [Test]
    public void Coordinator_ExpandsGpuRangeDemandUsingStableFourRangeAbi()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(4);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(4));
        Assert.That(coordinator.TryRegisterPackage(
            "package-range-demand",
            CreateSource(),
            out MeshletStreamingPackageHandle? handle,
            out _), Is.True);
        using (handle)
        {
            MeshletStreamingSubMeshGpuBinding binding =
                handle!.GetSubMeshGpuBinding(0);
            IReadOnlyList<GPUMeshletStreamingRange> ranges =
                handle.GetStreamingRanges();

            Assert.Multiple(() =>
            {
                Assert.That(ranges, Has.Count.EqualTo(4));
                Assert.That(binding.Lod1RangeIndex,
                    Is.EqualTo(binding.Lod0RangeIndex + 1u));
                Assert.That(binding.Lod2RangeIndex,
                    Is.EqualTo(binding.Lod0RangeIndex + 2u));
                Assert.That(binding.HierarchyRangeIndex,
                    Is.EqualTo(binding.Lod0RangeIndex + 3u));
                Assert.That(ranges[2].Flags &
                    MeshletStreamingRangeFlags.PinnedFallback,
                    Is.Not.Zero);
                Assert.That(ranges[3].PageCount, Is.Zero);
            });

            Assert.That(coordinator.RequestGlobalRange(
                binding.Lod0RangeIndex,
                MeshletStreamingResidencyCoordinator.VisiblePriority,
                serial: 1), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(coordinator.GetState(handle.PackageId, 0),
                    Is.EqualTo(MeshletPageResidencyState.Queued));
                Assert.That(coordinator.RequestGlobalRange(
                    uint.MaxValue,
                    MeshletStreamingResidencyCoordinator.VisiblePriority,
                    serial: 1), Is.False);
            });
        }
    }

    [Test]
    public async Task Coordinator_EvictsGloballyAndRetiresForTwoFrames()
    {
        using var uploader = new MeshletPhysicalPageCacheUploader(3);
        using var coordinator = new MeshletStreamingResidencyCoordinator(
            uploader,
            CreateOptions(3));
        Assert.That(coordinator.TryRegisterPackage(
            "package-a",
            CreateSource(),
            out MeshletStreamingPackageHandle? first,
            out _), Is.True);
        Assert.That(coordinator.TryRegisterPackage(
            "package-b",
            CreateSource(),
            out MeshletStreamingPackageHandle? second,
            out _), Is.True);
        using (first)
        using (second)
        {
            await coordinator.TickAsync(0, 0);
            await coordinator.TickAsync(1, 1);
            first!.RequestPage(
                0,
                MeshletStreamingResidencyCoordinator.VisiblePriority,
                1);
            await coordinator.TickAsync(1, 1);
            await coordinator.TickAsync(2, 2);

            second!.RequestPage(
                1,
                MeshletStreamingResidencyCoordinator.VisiblePriority + 1,
                3);
            await coordinator.TickAsync(3, 3);
            MeshletStreamingCoordinatorSnapshot evicting =
                coordinator.CreateSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(evicting.EvictionCount, Is.EqualTo(1));
                Assert.That(evicting.RetiredPhysicalPageCount,
                    Is.EqualTo(1));
                Assert.That(coordinator.GetState(
                    second.PackageId,
                    1), Is.EqualTo(MeshletPageResidencyState.Queued));
            });

            await coordinator.TickAsync(4, 4);
            Assert.That(coordinator.GetState(second.PackageId, 1),
                Is.EqualTo(MeshletPageResidencyState.Queued));
            await coordinator.TickAsync(5, 5);
            await coordinator.TickAsync(6, 6);
            Assert.That(coordinator.GetState(second.PackageId, 1),
                Is.EqualTo(MeshletPageResidencyState.Resident));
        }
    }

    private static MeshletStreamingResidencyOptions CreateOptions(
        int capacity) =>
        new(
            PhysicalPageCapacity: capacity,
            MaximumUploadBytesPerTick: 4 * 64 * 1024,
            MaximumAdmissionsPerTick: 4,
            MaximumConcurrentReads: 4,
            MaximumRequestsPerSerial: 16,
            EvictionGraceSerials: 1,
            DemandLifetimeSerials: 100,
            RetryBaseSerials: 1,
            RetryMaximumSerials: 2,
            FramesInFlight: 2);

    private static InMemorySource CreateSource()
    {
        byte[][] data =
        [
            CreateDecodedPage(0),
            CreateDecodedPage(0),
            CreateDecodedPage(0)
        ];
        MeshletStreamingPageFlags[] flags =
        [
            MeshletStreamingPageFlags.Streamable |
            MeshletStreamingPageFlags.Lod0,
            MeshletStreamingPageFlags.Streamable |
            MeshletStreamingPageFlags.Lod1,
            MeshletStreamingPageFlags.Pinned |
            MeshletStreamingPageFlags.Lod2
        ];
        var records = new MeshletStreamingPageRecord[3];
        for (int pageId = 0; pageId < records.Length; pageId++)
        {
            records[pageId] = new MeshletStreamingPageRecord(
                pageId,
                SubMeshIndex: 0,
                LogicalFirstMeshlet: pageId,
                MeshletCount: 1,
                flags[pageId],
                FallbackPageId: 2,
                DataOffset: 4096L * (pageId + 1),
                StoredBytes: data[pageId].Length,
                UncompressedBytes: data[pageId].Length,
                CookedCompression.None,
                Crc32.HashToUInt32(data[pageId]),
                XxHash64.HashToUInt64(data[pageId]));
        }
        var manifest = new MeshletStreamingManifest(
            MeshletStreamingManifest.CurrentSchemaVersion,
            MeshletStreamingManifest.ProductionPageSizeBytes,
            "memory.pages",
            records,
            records.Sum(static page => (long)page.StoredBytes),
            records.Sum(static page => (long)page.UncompressedBytes),
            PinnedPageCount: 1);
        manifest.Validate("memory");
        return new InMemorySource(manifest, data);
    }

    private static CookedMeshPayload CreateActivationPayload(
        int streamablePageCount,
        int fullResidentBytes)
    {
        if (streamablePageCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(streamablePageCount));
        int meshletCount = checked(streamablePageCount + 1);
        int packedMeshletBytes = checked(
            meshletCount * Marshal.SizeOf<GPUPackedMeshlet>());
        int meshletVertexCount = checked(
            (fullResidentBytes - packedMeshletBytes) / sizeof(uint));
        var bounds = new BoundingBox(Vector3.Zero, Vector3.One);
        var subMesh = new CookedSubMeshRecord(
            "Activation",
            MaterialSlot: 0,
            NodeIndex: -1,
            SkinIndex: -1,
            Matrix4x4.Identity,
            VertexOffset: 0,
            VertexCount: 3,
            IndexOffset: 0,
            IndexCount: 3,
            SkinningOffset: 0,
            SkinningCount: 0,
            MeshletOffset: 0,
            MeshletCount: streamablePageCount,
            MeshletVertexOffset: 0,
            MeshletVertexCount: meshletVertexCount,
            MeshletTriangleOffset: 0,
            MeshletTriangleCount: 0,
            LodRanges: Array.Empty<ProcessedMeshLodRange>(),
            DrawRanges: Array.Empty<ProcessedMeshDrawRange>(),
            bounds,
            BoundingSphere.FromBox(bounds),
            (uint)ProcessedVertexAttribute.Position)
        {
            MeshletLod2Offset = 0,
            MeshletLod2Count = 1
        };

        byte[] decoded = CreateDecodedPage(0);
        var pages = new MeshletStreamingPageRecord[meshletCount];
        for (int pageId = 0; pageId < pages.Length; pageId++)
        {
            bool pinned = pageId == streamablePageCount;
            pages[pageId] = new MeshletStreamingPageRecord(
                pageId,
                SubMeshIndex: 0,
                LogicalFirstMeshlet: pageId,
                MeshletCount: 1,
                pinned
                    ? MeshletStreamingPageFlags.Pinned |
                      MeshletStreamingPageFlags.Lod2
                    : MeshletStreamingPageFlags.Streamable |
                      MeshletStreamingPageFlags.Lod0,
                FallbackPageId: streamablePageCount,
                DataOffset: 4096L * (pageId + 256),
                StoredBytes: decoded.Length,
                UncompressedBytes: decoded.Length,
                CookedCompression.None,
                Crc32.HashToUInt32(decoded),
                XxHash64.HashToUInt64(decoded));
        }
        var manifest = new MeshletStreamingManifest(
            MeshletStreamingManifest.CurrentSchemaVersion,
            MeshletStreamingManifest.ProductionPageSizeBytes,
            "activation.pages",
            pages,
            pages.Sum(static page => (long)page.StoredBytes),
            pages.Sum(static page => (long)page.UncompressedBytes),
            PinnedPageCount: 1);
        manifest.Validate("activation");
        return new CookedMeshPayload(
            [subMesh],
            Array.Empty<CookedVertexPositionStream>(),
            Array.Empty<CookedVertexNormalTangentStream>(),
            Array.Empty<CookedVertexUvColorStream>(),
            Array.Empty<CookedVertexSkinningData>(),
            Array.Empty<uint>(),
            Array.Empty<Meshlet>(),
            Array.Empty<Meshlet>(),
            Array.Empty<Meshlet>(),
            Array.Empty<uint>(),
            Array.Empty<uint>())
        {
            StreamingManifest = manifest
        };
    }

    private static byte[] CreateDecodedPage(uint vertexOffset)
    {
        var meshlet = new Meshlet(
            Vector3.Zero,
            1f,
            vertexOffset,
            3,
            0,
            3,
            0,
            3,
            0,
            1);
        return MeshletStreamingPageCodec.Encode(
            new MeshletStreamingPagePayload(
                [meshlet],
                [0u, 1u, 2u],
                [0u, 1u, 2u]));
    }

    private static MeshInfo CreateManagedMeshInfo(
        uint virtualBase,
        uint rangeBase) =>
        new()
        {
            MeshletOffset = MeshletVirtualAddress.Encode(virtualBase),
            MeshletCount = 1,
            MeshletLod1Offset = MeshletVirtualAddress.Encode(
                checked(virtualBase + 1)),
            MeshletLod1Count = 1,
            MeshletLod2Offset = MeshletVirtualAddress.Encode(
                checked(virtualBase + 2)),
            MeshletLod2Count = 1,
            MeshletLodGeneratedCount = 3,
            UsesManagedPhysicalResidency = true,
            StreamingRangeIndex = rangeBase,
            ResidencyFlags =
                GpuMeshResidencyFlags.ManagedPhysicalResidency |
                GpuMeshResidencyFlags.HasPinnedFallback
        };

    private static Meshlet CreateCpuMeshlet(uint vertexOffset) =>
        new(
            Vector3.Zero,
            1f,
            vertexOffset,
            3,
            0,
            3,
            0,
            3,
            0,
            1);

    private sealed class InMemorySource : IMeshletStreamingPageSource
    {
        private readonly byte[][] _pages;

        public InMemorySource(
            MeshletStreamingManifest manifest,
            byte[][] pages)
        {
            Manifest = manifest;
            _pages = pages;
        }

        public MeshletStreamingManifest Manifest { get; }

        public ValueTask<byte[]> ReadPageAsync(
            int pageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((byte[])_pages[pageId].Clone());
        }
    }

    private sealed class FixedBankBudget : IMeshletPhysicalMemoryBudget
    {
        private int _remainingBanks;

        public FixedBankBudget(int bankCount)
        {
            _remainingBanks = bankCount;
        }

        public bool TryCommit(long bytes, out string rejectionReason)
        {
            Assert.That(bytes,
                Is.EqualTo(MeshletPhysicalBankAllocator.BankSizeBytes));
            if (_remainingBanks-- > 0)
            {
                rejectionReason = string.Empty;
                return true;
            }
            rejectionReason = "synthetic-budget-rejection";
            return false;
        }

        public void Release(long bytes)
        {
            Assert.That(bytes %
                MeshletPhysicalBankAllocator.BankSizeBytes, Is.Zero);
        }
    }
}
