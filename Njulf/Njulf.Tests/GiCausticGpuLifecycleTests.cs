using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticGpuLifecycleTests
{
    [Test]
    public void ManagedGpuAbi_HasExactDocumentedSizesAndOffsets()
    {
        GiCausticGpuAbi.VerifyManagedLayout();

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUCausticTaskDispatchHeaderV1>(),
                Is.EqualTo(GiCausticGpuAbi.TaskDispatchHeaderBytes));
            Assert.That(Marshal.SizeOf<GPUCausticPhotonTaskV1>(),
                Is.EqualTo(GiCausticGpuAbi.TaskRecordBytes));
            Assert.That(Marshal.SizeOf<GPUCausticPhotonCandidateV1>(),
                Is.EqualTo(GiCausticGpuAbi.PhotonRecordBytes));
            Assert.That(Marshal.SizeOf<GPUCausticCellEntryV1>(),
                Is.EqualTo(GiCausticGpuAbi.CellEntryBytes));
            Assert.That(Marshal.SizeOf<GPUCausticCacheHeaderV1>(),
                Is.EqualTo(GiCausticGpuAbi.CacheHeaderBytes));
            Assert.That(Marshal.SizeOf<GPUCausticPushConstantsV1>(),
                Is.EqualTo(GiCausticGpuAbi.PushConstantsBytes));
            Assert.That(Marshal.OffsetOf<GPUCausticPushConstantsV1>(
                    nameof(GPUCausticPushConstantsV1.CellOriginAndSize)).ToInt32(),
                Is.EqualTo(112));
            Assert.That(GiCausticGpuAbi.BindlessSlots.PhotonBufferIndex,
                Is.EqualTo(GiCausticGpuAbi.BindlessSlots.TaskBufferIndex + 1));
            Assert.That(GiCausticGpuAbi.BindlessSlots.CacheBufferIndex,
                Is.EqualTo(GiCausticGpuAbi.BindlessSlots.PhotonBufferIndex + 1));
            Assert.That(GiCausticGpuAbi.BindlessSlots.ScratchBufferIndex,
                Is.EqualTo(GiCausticGpuAbi.BindlessSlots.CacheBufferIndex + 1));
        });
    }

    [Test]
    public void StrictGpuPlan_RejectsLegacyOnePhotonBankInsteadOfOverwritingReadableData()
    {
        GiCausticCacheLayout legacy = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 16,
            maximumPhotonsPerCell: 4,
            maximumOccupiedCells: 4,
            recordStride: GiCausticGpuAbi.PhotonRecordBytes,
            writeBankCount: 1,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0UL,
            budgetBytes: 1_000_000UL);

        GiCausticGpuResourceLayout plan =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                legacy,
                IndependentMemoryBudgetBytes: 1_000_000UL,
                ScreenResolveProfile: new(64, 64)));

        Assert.Multiple(() =>
        {
            Assert.That(legacy.IsValid, Is.True);
            Assert.That(plan.IsValid, Is.False);
            Assert.That(plan.FailureReason,
                Is.EqualTo("caustic-gpu-requires-two-readable-photon-banks"));
        });
    }

    [Test]
    public void CentralMemory_KeepsPublishedCellTablesOutOfTransientScratch()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        GiCausticGpuMemoryRequirements memory =
            GiCausticGpuMemoryRequirements.FromLayout(
                layout,
                admitted: true,
                allocated: true,
                GiExperimentFallbackReason.None);

        Assert.Multiple(() =>
        {
            Assert.That(memory.CellTableAndSortScratch.RequiredBytes,
                Is.EqualTo(layout.ScratchBytes));
            Assert.That(memory.History.RequiredBytes,
                Is.EqualTo(layout.CacheTableBytes +
                    layout.CacheHistoryBytes +
                    layout.PublicationHeaderBytes +
                    layout.ScreenResolve.PersistentImageBytes +
                    layout.RuntimeMetadataBytes));
            Assert.That(memory.RequiredBytes, Is.EqualTo(layout.TotalBytes));
            Assert.That(
                SimpleDdgiAdvancedExperimentMemoryPlan.IsTransientCategory(
                    memory.CellTableAndSortScratch.Category),
                Is.True);
            Assert.That(
                SimpleDdgiAdvancedExperimentMemoryPlan.IsTransientCategory(
                    memory.History.Category),
                Is.False);
        });
    }

    [Test]
    public void BuildingNextGeneration_KeepsPublishedReadBankVisibleAndImmutable()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator();
        using var manager = new GiCausticGpuResourceManager();
        manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()),
            allocator);
        GiCausticCacheRevision revision = CreateRevision(71UL);
        GiCausticGpuBuildBeginResult first = manager.BeginBuild(
            revision, 4, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));
        Assert.That(manager.CompleteBuild(
            first.Token,
            true,
            CreateCompleteHeader(first.Token, layout)).Published,
            Is.True);
        Assert.That(manager.TryGetReadable(
            revision, out int publishedPhotonBank,
            out int publishedCacheBank, out _), Is.True);

        GiCausticGpuBuildBeginResult second = manager.BeginBuild(
            revision, 4, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));
        bool readableWhileBuilding = manager.TryGetReadable(
            revision, out int activePhotonBank,
            out int activeCacheBank, out GPUCausticCacheHeaderV1 header);
        GPUCausticPushConstantsV1 resolve =
            manager.CreateResolvePushConstants(
                revision,
                checked((uint)(layout.ScratchBytes / sizeof(uint))),
                0u,
                0u);

        Assert.Multiple(() =>
        {
            Assert.That(second.Started, Is.True, second.Reason);
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(GiCausticGpuResourceState.Building));
            Assert.That(manager.Snapshot.HasReadableCache, Is.True);
            Assert.That(readableWhileBuilding, Is.True);
            Assert.That(activePhotonBank, Is.EqualTo(publishedPhotonBank));
            Assert.That(activeCacheBank, Is.EqualTo(publishedCacheBank));
            Assert.That(second.Token.PhotonWriteBankIndex,
                Is.Not.EqualTo(publishedPhotonBank));
            Assert.That(second.Token.CacheWriteBankIndex,
                Is.Not.EqualTo(publishedCacheBank));
            Assert.That(resolve.CacheGeneration,
                Is.EqualTo(header.CacheGeneration));
            Assert.That(resolve.PhotonReadBankIndex,
                Is.EqualTo((uint)publishedPhotonBank));
        });
    }

    [Test]
    public void FirstBuild_ReadabilityProbeDoesNotCancelPendingPublication()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        using var manager = new GiCausticGpuResourceManager();
        manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()),
            new FakeAllocator());
        GiCausticCacheRevision revision = CreateRevision(73UL);
        GiCausticGpuBuildBeginResult begin = manager.BeginBuild(
            revision, 4, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));

        bool prematurelyReadable = manager.TryGetReadable(
            revision, out _, out _, out _);
        GiCausticGpuRuntimeSnapshot pending = manager.Snapshot;
        GiCausticGpuPublicationResult publication = manager.CompleteBuild(
            begin.Token,
            true,
            CreateCompleteHeader(begin.Token, layout));

        Assert.Multiple(() =>
        {
            Assert.That(begin.Started, Is.True, begin.Reason);
            Assert.That(prematurelyReadable, Is.False);
            Assert.That(pending.State,
                Is.EqualTo(GiCausticGpuResourceState.Building));
            Assert.That(pending.PendingGeneration,
                Is.EqualTo(begin.Token.CacheGeneration));
            Assert.That(publication.Published, Is.True, publication.Reason);
            Assert.That(manager.TryGetReadable(
                revision, out _, out _, out _), Is.True);
            Assert.That(manager.InvalidationCount, Is.Zero);
        });
    }

    [Test]
    public void StaleReadBankRejection_PreservesMatchingReplacementBuild()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        using var manager = new GiCausticGpuResourceManager();
        manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()),
            new FakeAllocator());
        GiCausticCacheRevision original = CreateRevision(81UL);
        GiCausticGpuBuildBeginResult first = manager.BeginBuild(
            original, 4, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));
        Assert.That(manager.CompleteBuild(
            first.Token,
            true,
            CreateCompleteHeader(first.Token, layout)).Published,
            Is.True);

        GiCausticCacheRevision replacement = CreateRevision(82UL);
        GiCausticGpuBuildBeginResult second = manager.BeginBuild(
            replacement, 4, new Vector4(0.0f, 0.0f, 0.0f, 0.5f));
        Assert.That(manager.TryGetReadable(
            replacement, out _, out _, out _), Is.False);
        GiCausticGpuPublicationResult publication = manager.CompleteBuild(
            second.Token,
            true,
            CreateCompleteHeader(second.Token, layout));

        Assert.Multiple(() =>
        {
            Assert.That(second.Started, Is.True, second.Reason);
            Assert.That(publication.Published, Is.True, publication.Reason);
            Assert.That(manager.TryGetReadable(
                replacement, out _, out _, out _), Is.True);
            Assert.That(manager.InvalidationCount, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void Lifecycle_PublishesOnlyValidatedHeaderAndInvalidatesOnRevisionChange()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator();
        using var manager = new GiCausticGpuResourceManager();
        GiCausticGpuRuntimeSnapshot allocated = manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()), allocator);
        GiCausticCacheRevision revision = CreateRevision(7UL);
        GiCausticGpuBuildBeginResult begin = manager.BeginBuild(
            revision, taskCount: 3, new Vector4(1.0f, 2.0f, 3.0f, 0.5f));
        GPUCausticCacheHeaderV1 header = CreateCompleteHeader(begin.Token, layout);

        GiCausticGpuPublicationResult publication = manager.CompleteBuild(
            begin.Token, gpuWorkCompleted: true, header);
        bool readable = manager.TryGetReadable(
            revision, out int photonBank, out int cacheBank, out GPUCausticCacheHeaderV1 readHeader);
        GPUCausticPushConstantsV1 resolve = manager.CreateResolvePushConstants(
            revision,
            scratchWordCapacity: checked((uint)(layout.ScratchBytes / sizeof(uint))),
            resolveRequestWordOffset: 0u,
            resolveRequestCount: 2u);
        GiCausticCacheRevision changed = CreateRevision(8UL);
        manager.Invalidate(changed, "hero-transform-revision-changed");

        Assert.Multiple(() =>
        {
            Assert.That(allocated.State, Is.EqualTo(GiCausticGpuResourceState.ReadyForBuild));
            Assert.That(allocated.AllocatedBytes, Is.EqualTo(layout.TotalBytes));
            Assert.That(begin.Started, Is.True, begin.Reason);
            Assert.That(publication.Published, Is.True, publication.Reason);
            Assert.That(readable, Is.True);
            Assert.That(photonBank, Is.EqualTo(begin.Token.PhotonWriteBankIndex));
            Assert.That(cacheBank, Is.EqualTo(begin.Token.CacheWriteBankIndex));
            Assert.That(readHeader.CacheGeneration,
                Is.EqualTo(begin.Token.CacheGeneration));
            Assert.That(resolve.PhotonReadBankIndex, Is.EqualTo((uint)photonBank));
            Assert.That(resolve.CacheReadBankIndex, Is.EqualTo((uint)cacheBank));
            Assert.That(resolve.CacheBankHeaderWordOffset,
                Is.Not.EqualTo(0u));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(GiCausticGpuResourceState.Invalidated));
            Assert.That(manager.TryGetReadable(changed, out _, out _, out _), Is.False);
            Assert.That(manager.InvalidationCount, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void OverflowedHeader_FailsClosedAndDoesNotPublishPartialPhotonBank()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator();
        using var manager = new GiCausticGpuResourceManager();
        manager.Reconcile(new GiCausticGpuRuntimeRequest(true, layout, FullySupported()), allocator);
        GiCausticCacheRevision revision = CreateRevision(9UL);
        GiCausticGpuBuildBeginResult begin = manager.BeginBuild(
            revision, taskCount: 2, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        GPUCausticCacheHeaderV1 overflow = CreateCompleteHeader(begin.Token, layout);
        overflow.OverflowCount = 1u;
        overflow.PublicationFlags |= GiCausticGpuCachePublicationFlags.CandidateOverflow;

        GiCausticGpuPublicationResult publication = manager.CompleteBuild(
            begin.Token, gpuWorkCompleted: true, overflow);

        Assert.Multiple(() =>
        {
            Assert.That(begin.Started, Is.True);
            Assert.That(publication.Published, Is.False);
            Assert.That(publication.Failure,
                Is.EqualTo(GiCausticGpuPublicationFailure.Overflow));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(GiCausticGpuResourceState.ReadyForBuild));
            Assert.That(manager.TryGetReadable(revision, out _, out _, out _), Is.False);
            Assert.That(manager.PublicationFailureCount, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void MissingTaggedTransportCapability_AllocatesNothingAndFallsBack()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator();
        using var manager = new GiCausticGpuResourceManager();
        GiCausticGpuFeatureSupport unsupported = FullySupported() with
        {
            TaggedTransportBackendIntegrated = false
        };

        GiCausticGpuRuntimeSnapshot snapshot = manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, unsupported), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(GiCausticGpuResourceState.Disabled));
            Assert.That(snapshot.AllocatedBytes, Is.Zero);
            Assert.That(snapshot.MemoryRequirements.AllocatedBytes, Is.Zero);
            Assert.That(allocator.AllocationCount, Is.Zero);
            Assert.That(snapshot.Reason,
                Is.EqualTo("caustic-tagged-first-diffuse-transport-backend-unavailable"));
        });
    }

    [Test]
    public void IncompleteNativeAllocation_IsRetiredAndNeverLeavesDescriptorsLive()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator { DescriptorCount = 3u };
        using var manager = new GiCausticGpuResourceManager();

        GiCausticGpuRuntimeSnapshot snapshot = manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.State, Is.EqualTo(GiCausticGpuResourceState.Disabled));
            Assert.That(snapshot.AllocatedBytes, Is.Zero);
            Assert.That(manager.AllocationFailureCount, Is.EqualTo(1UL));
            Assert.That(allocator.AllocationCount, Is.EqualTo(1));
            Assert.That(allocator.RetiredCount, Is.EqualTo(1));
            Assert.That(snapshot.Reason,
                Does.StartWith("caustic-gpu-allocation-rejected:"));
        });
    }

    private static GiCausticGpuResourceLayout CreateValidLayout()
    {
        GiCausticCacheLayout source = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 16,
            maximumPhotonsPerCell: 4,
            maximumOccupiedCells: 4,
            recordStride: GiCausticGpuAbi.PhotonRecordBytes,
            writeBankCount: 2,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0UL,
            budgetBytes: 1_000_000UL);
        GiCausticGpuResourceLayout layout =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                source,
                IndependentMemoryBudgetBytes: 1_000_000UL,
                ScreenResolveProfile: new(64, 64)));
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);
        return layout;
    }

    private static GiCausticGpuFeatureSupport FullySupported() => new(
        ComputeSupported: true,
        RayQuerySupported: true,
        CurrentPoseAccelerationStructuresAvailable: true,
        TaggedTransportBackendIntegrated: true,
        DeterministicParallelCacheBuildIntegrated: true,
        PublicationReadbackSupported: true,
        DedicatedBindlessSlotsAvailable: true,
        ScreenResolvePipelineIntegrated: true,
        ScreenResolveResourcesAvailable: true);

    private static GiCausticCacheRevision CreateRevision(ulong value) => new(
        TransportAbi: GiCausticGpuAbi.Version,
        HeroMaterialRevision: value,
        LightDistributionRevision: value + 1UL,
        CasterGeometryRevision: value + 2UL,
        CasterTransformRevision: value + 3UL,
        ReceiverGeometryRevision: value + 4UL,
        StableIdentityRevision: value + 5UL);

    private static GPUCausticCacheHeaderV1 CreateCompleteHeader(
        in GiCausticGpuBuildToken token,
        in GiCausticGpuResourceLayout layout) => new()
    {
        AbiVersion = GiCausticGpuAbi.Version,
        CacheGeneration = token.CacheGeneration,
        RevisionFingerprintLow = GiCausticGpuAbi.Low32(token.RevisionFingerprint),
        RevisionFingerprintHigh = GiCausticGpuAbi.High32(token.RevisionFingerprint),
        TaskCapacity = (uint)layout.TaskCapacity,
        PhotonCapacity = (uint)layout.PhotonCapacity,
        PhotonRecordStrideBytes = (uint)layout.PhotonRecordStride,
        CellTableCapacity = (uint)layout.CellTableCapacity,
        MaximumPhotonsPerCell = (uint)layout.MaximumPhotonsPerCell,
        CandidateCount = 0u,
        RetainedPhotonCount = 0u,
        OccupiedCellCount = 0u,
        OverflowCount = 0u,
        PublicationFlags = GiCausticGpuCachePublicationFlags.Initialized |
            GiCausticGpuCachePublicationFlags.BuildComplete,
        BuildSerial = token.CacheGeneration,
        CacheBankIndex = (uint)token.CacheWriteBankIndex,
        CellOriginAndSize = token.CellOriginAndSize,
        PhotonBankIndex = (uint)token.PhotonWriteBankIndex,
        CandidateInputCount = 0u,
        TransportAbiVersion = token.Revision.TransportAbi
    };

    private sealed class FakeAllocator : IGiCausticGpuResourceAllocator
    {
        private ulong _nextHandle = 1UL;

        public int AllocationCount { get; private set; }

        public int RetiredCount { get; private set; }

        public uint DescriptorCount { get; init; } = GiCausticGpuAbi.DescriptorCount;

        public GiCausticGpuAllocation Allocate(in GiCausticGpuResourceLayout layout)
        {
            AllocationCount++;
            return new GiCausticGpuAllocation(
                AllocationId: _nextHandle++,
                Tasks: Create(layout.TaskQueueBytes),
                Photons: Create(checked(layout.CandidateStagingBytes +
                    layout.PublishedPhotonBytes)),
                Cache: Create(layout.CacheBytes),
                Scratch: Create(layout.ScratchBytes),
                DescriptorCount: DescriptorCount);
        }

        public void Retire(GiCausticGpuAllocation allocation)
        {
            RetiredCount++;
        }

        private GiCausticGpuBuffer Create(ulong bytes) => new(_nextHandle++, bytes);
    }
}
