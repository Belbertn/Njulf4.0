using System;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingGpuIntegrationTests
{
    [Test]
    public void GpuPayloadAbi_HasFrozenByteSizesAndHeaderPacking()
    {
        Assert.DoesNotThrow(SimpleDdgiGuidingGpuAbi.VerifyManagedLayout);

        uint packed = SimpleDdgiGuidingGpuAbi.PackLeafResolutionAndFlags(
            8u,
            SimpleDdgiGuidingGpuDistributionFlags.BuildComplete |
            SimpleDdgiGuidingGpuDistributionFlags.UniformFallback);

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingDistributionHeader>(),
                Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingTrainingRecord>(),
                Is.EqualTo(40));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingTrainingWorkItem>(),
                Is.EqualTo(56));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingBuildWorkItem>(),
                Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingSampleRequest>(),
                Is.EqualTo(56));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingSamplePayload>(),
                Is.EqualTo(64));
            Assert.That(SimpleDdgiGuidingGpuAbi.TrainingWorkItemWordCount,
                Is.EqualTo(14u));
            Assert.That(SimpleDdgiGuidingGpuAbi.BuildWorkItemWordCount,
                Is.EqualTo(12u));
            Assert.That(SimpleDdgiGuidingGpuAbi.SampleRequestWordCount,
                Is.EqualTo(14u));
            Assert.That(SimpleDdgiGuidingGpuAbi.SamplePayloadWordCount,
                Is.EqualTo(16u));
            Assert.That(SimpleDdgiGuidingGpuAbi.PublicationRecordWordCount,
                Is.EqualTo(12u));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingPushConstants>(),
                Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiGuidingExtractPushConstants>(),
                Is.EqualTo(48));
            Assert.That(SimpleDdgiGuidingGpuAbi.GetLeafResolution(packed), Is.EqualTo(8u));
            Assert.That(SimpleDdgiGuidingGpuAbi.GetHierarchyWeightCount(8u),
                Is.EqualTo(85u));
            Assert.That(SimpleDdgiGuidingGpuAbi.GetPackedHierarchyWordCount(8u),
                Is.EqualTo(43u));
            Assert.That(SimpleDdgiGuidingGpuAbi.GetDistributionFlags(packed),
                Is.EqualTo(SimpleDdgiGuidingGpuDistributionFlags.BuildComplete |
                    SimpleDdgiGuidingGpuDistributionFlags.UniformFallback));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DistributionBank0,
                Is.EqualTo(200));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DistributionBank1,
                Is.EqualTo(201));
            Assert.That(SimpleDdgiGuidingBindlessSlots.TrainingScratch,
                Is.EqualTo(202));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                Is.EqualTo(203));
        });
        Assert.That(() => SimpleDdgiGuidingGpuAbi.PackLeafResolutionAndFlags(
            8u, (SimpleDdgiGuidingGpuDistributionFlags)1u),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void TraceOwnershipTag_IsDeterministicNonZeroAndBindsEveryInput()
    {
        const uint stableLow = 0x1020_3040u;
        const uint stableHigh = 0x5060_7080u;
        const uint physical = 17u;
        const uint virtualProbe = 271u;
        const uint page = 9u;
        const uint slot = 31u;
        const uint direction = 0xa5a5_5a5au;

        uint expected = SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(
            stableLow,
            stableHigh,
            physical,
            virtualProbe,
            page,
            slot,
            direction);
        uint[] mutations =
        {
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow + 1u,
                stableHigh, physical, virtualProbe, page, slot, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh + 1u, physical, virtualProbe, page, slot, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh, physical + 1u, virtualProbe, page, slot, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh, physical, virtualProbe + 1u, page, slot, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh, physical, virtualProbe, page + 1u, slot, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh, physical, virtualProbe, page, slot + 1u, direction),
            SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(stableLow,
                stableHigh, physical, virtualProbe, page, slot, direction + 1u)
        };

        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.Not.Zero);
            Assert.That(SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag(
                stableLow,
                stableHigh,
                physical,
                virtualProbe,
                page,
                slot,
                direction), Is.EqualTo(expected));
            Assert.That(mutations, Has.None.EqualTo(expected));
        });
    }

    [Test]
    public void DisabledEffectiveMode_AllocatesNoBuffersDescriptorsOrPasses()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        SimpleDdgiGuidingRuntimeSnapshot snapshot = manager.Reconcile(
            new SimpleDdgiGuidingRuntimeRequest(
                IsEffectivelyEnabled: false,
                CreateLayout()),
            allocator);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.AllocateCalls, Is.Zero);
            Assert.That(snapshot.State, Is.EqualTo(SimpleDdgiGuidingResourceState.Disabled));
            Assert.That(snapshot.AllocatedBytes, Is.Zero);
            Assert.That(snapshot.DescriptorCount, Is.Zero);
            Assert.That(snapshot.ProductionPassCount, Is.Zero);
            Assert.That(SimpleDdgiGuidingPasses.Create(snapshot,
                includeValidationPass: true), Is.Empty);
        });
    }

    [Test]
    public void Lifecycle_UsesTwoBanksAndPublishesOnlyValidatedCompletedHeaders()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        SimpleDdgiGuidingLayout layout = CreateLayout();
        SimpleDdgiGuidingRuntimeSnapshot initial = manager.Reconcile(
            new SimpleDdgiGuidingRuntimeRequest(true, layout), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.AllocateCalls, Is.EqualTo(1));
            Assert.That(initial.State, Is.EqualTo(SimpleDdgiGuidingResourceState.ReadyForBuild));
            Assert.That(initial.AllocatedBytes, Is.EqualTo(layout.ManagerOwnedBytes));
            Assert.That(initial.DescriptorCount, Is.EqualTo(3u));
            Assert.That(initial.ReadBankIndex, Is.EqualTo(-1));
            Assert.That(initial.ProductionPassCount, Is.Zero);
        });

        SimpleDdgiGuidingBuildBeginResult begin = manager.BeginBuild(9u);
        Assert.That(begin.Started, Is.True, begin.Reason);
        SimpleDdgiGuidingPassDeclaration[] buildingPasses =
            SimpleDdgiGuidingPasses.Create(manager.Snapshot,
                includeValidationPass: true).ToArray();
        Assert.That(buildingPasses.Select(pass => pass.Kind), Is.EqualTo(
            new[]
            {
                SimpleDdgiGuidingPassKind.Train,
                SimpleDdgiGuidingPassKind.Build,
                SimpleDdgiGuidingPassKind.Validate
            }));
        Assert.That(buildingPasses[1].DestinationBankIndex,
            Is.EqualTo(begin.Token.WriteBankIndex));

        var header = CreateCompletedHeader(
            virtualProbeId: 77u,
            pageGeneration: 4u,
            distributionGeneration: 12u,
            proposalEpoch: 9u);
        SimpleDdgiGuidingPublicationResult published = manager.CompleteBuild(
            begin.Token,
            gpuWorkCompleted: true,
            [new SimpleDdgiGuidingPublishedProbeHeader(3u, 77u, 4u, header)]);

        Assert.That(published.Published, Is.True, published.Reason);
        SimpleDdgiGuidingRuntimeSnapshot readable = manager.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(readable.State, Is.EqualTo(SimpleDdgiGuidingResourceState.Readable));
            Assert.That(readable.ReadBankIndex, Is.EqualTo(begin.Token.WriteBankIndex));
            Assert.That(readable.WriteBankIndex, Is.Not.EqualTo(readable.ReadBankIndex));
            Assert.That(readable.PublishedProbeCount, Is.EqualTo(1));
            Assert.That(readable.ProductionPassCount, Is.EqualTo(1));
            Assert.That(manager.TryGetReadableBank(3u, 77u, 4u, 12u, 9u,
                out int readBank), Is.True);
            Assert.That(readBank, Is.EqualTo(readable.ReadBankIndex));
            Assert.That(manager.TryGetReadableBank(3u, 77u, 4u, 11u, 9u,
                out _), Is.False);
        });

        SimpleDdgiGuidingPassDeclaration[] readablePasses =
            SimpleDdgiGuidingPasses.Create(readable,
                includeValidationPass: true).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(readablePasses, Has.Length.EqualTo(1));
            Assert.That(readablePasses[0].Kind,
                Is.EqualTo(SimpleDdgiGuidingPassKind.Sample));
            Assert.That(readablePasses[0].SourceBankIndex,
                Is.EqualTo(readable.ReadBankIndex));
            Assert.That(readablePasses[0].ResourceUses.Select(use => use.ResourceName),
                Does.Contain(SimpleDdgiGuidingResourceNames.DirectionPayloadSidecar));
        });
    }

    [Test]
    public void StaleOrPartialPublication_CannotFlipOrCancelTheCurrentBank()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        manager.Reconcile(new SimpleDdgiGuidingRuntimeRequest(true, CreateLayout()),
            allocator);

        SimpleDdgiGuidingBuildBeginResult first = manager.BeginBuild(3u);
        Assert.That(manager.CompleteBuild(first.Token, true,
            [new SimpleDdgiGuidingPublishedProbeHeader(0u, 1u, 1u,
                CreateCompletedHeader(1u, 1u, 100u, 3u))]).Published, Is.True);
        int firstReadBank = manager.Snapshot.ReadBankIndex;

        SimpleDdgiGuidingBuildBeginResult staleGeneration = manager.BeginBuild(3u);
        SimpleDdgiGuidingPublicationResult staleResult = manager.CompleteBuild(
            staleGeneration.Token,
            gpuWorkCompleted: true,
            [new SimpleDdgiGuidingPublishedProbeHeader(0u, 1u, 1u,
                CreateCompletedHeader(1u, 1u, 100u, 3u))]);
        Assert.Multiple(() =>
        {
            Assert.That(staleResult.Published, Is.False);
            Assert.That(staleResult.Failure,
                Is.EqualTo(SimpleDdgiGuidingPublicationFailure.CandidateGenerationNotNewer));
            Assert.That(manager.Snapshot.ReadBankIndex, Is.EqualTo(firstReadBank));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiGuidingResourceState.Readable));
        });

        SimpleDdgiGuidingBuildBeginResult current = manager.BeginBuild(4u);
        SimpleDdgiGuidingPublicationResult lateCompletion = manager.CompleteBuild(
            first.Token,
            gpuWorkCompleted: true,
            [new SimpleDdgiGuidingPublishedProbeHeader(0u, 1u, 1u,
                CreateCompletedHeader(1u, 1u, 101u, 4u))]);
        Assert.Multiple(() =>
        {
            Assert.That(lateCompletion.Failure,
                Is.EqualTo(SimpleDdgiGuidingPublicationFailure.TokenMismatch));
            Assert.That(manager.Snapshot.State,
                Is.EqualTo(SimpleDdgiGuidingResourceState.Building));
            Assert.That(manager.Snapshot.PendingBankGeneration,
                Is.EqualTo(current.Token.CandidateBankGeneration));
        });

        SimpleDdgiGuidingPublicationResult partial = manager.CompleteBuild(
            current.Token,
            gpuWorkCompleted: true,
            [
                new SimpleDdgiGuidingPublishedProbeHeader(0u, 1u, 1u,
                    CreateCompletedHeader(1u, 1u, 101u, 4u)),
                new SimpleDdgiGuidingPublishedProbeHeader(1u, 2u, 1u,
                    CreateIncompleteHeader(2u, 1u, 1u, 4u))
            ]);
        Assert.Multiple(() =>
        {
            Assert.That(partial.Published, Is.False);
            Assert.That(partial.Failure,
                Is.EqualTo(SimpleDdgiGuidingPublicationFailure.HeaderInvalid));
            Assert.That(manager.Snapshot.ReadBankIndex, Is.EqualTo(firstReadBank));
            Assert.That(manager.Snapshot.PublishedProbeCount, Is.EqualTo(1));
            Assert.That(manager.TryGetReadableBank(0u, 1u, 1u, 100u, 3u,
                out _), Is.True);
            Assert.That(manager.TryGetReadableBank(0u, 1u, 1u, 101u, 4u,
                out _), Is.False);
        });
    }

    [Test]
    public void CandidateBuild_KeepsPreviousValidatedBankSampleable()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        manager.Reconcile(new SimpleDdgiGuidingRuntimeRequest(true, CreateLayout()),
            allocator);

        SimpleDdgiGuidingBuildBeginResult initialBuild = manager.BeginBuild(3u);
        Assert.That(manager.CompleteBuild(
            initialBuild.Token,
            gpuWorkCompleted: true,
            [new SimpleDdgiGuidingPublishedProbeHeader(0u, 42u, 7u,
                CreateCompletedHeader(42u, 7u, 100u, 3u))]).Published, Is.True);
        int previousReadBank = manager.Snapshot.ReadBankIndex;

        SimpleDdgiGuidingBuildBeginResult candidate = manager.BeginBuild(4u);
        Assert.That(candidate.Started, Is.True, candidate.Reason);
        SimpleDdgiGuidingRuntimeSnapshot building = manager.Snapshot;
        SimpleDdgiGuidingPassDeclaration[] passes = SimpleDdgiGuidingPasses.Create(
            building,
            includeValidationPass: true).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(building.State, Is.EqualTo(SimpleDdgiGuidingResourceState.Building));
            Assert.That(building.HasReadableDistribution, Is.True);
            Assert.That(building.ReadBankIndex, Is.EqualTo(previousReadBank));
            Assert.That(building.WriteBankIndex, Is.Not.EqualTo(previousReadBank));
            Assert.That(manager.TryGetReadableBank(0u, 42u, 7u, 100u, 3u,
                out int sampledBank), Is.True);
            Assert.That(sampledBank, Is.EqualTo(previousReadBank));
            Assert.That(passes.Select(pass => pass.Kind), Is.EqualTo(
                new[]
                {
                    SimpleDdgiGuidingPassKind.Train,
                    SimpleDdgiGuidingPassKind.Build,
                    SimpleDdgiGuidingPassKind.Validate,
                    SimpleDdgiGuidingPassKind.Sample
                }));
            Assert.That(passes[^1].SourceBankIndex, Is.EqualTo(previousReadBank));
        });
    }

    [Test]
    public void DisablingAfterAllocation_RetiresAllC3ResourcesAndReturnsZeroState()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        manager.Reconcile(new SimpleDdgiGuidingRuntimeRequest(true, CreateLayout()),
            allocator);

        SimpleDdgiGuidingRuntimeSnapshot disabled = manager.Reconcile(
            new SimpleDdgiGuidingRuntimeRequest(false, default), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(allocator.RetireCalls, Is.EqualTo(1));
            Assert.That(disabled.IsEffectivelyEnabled, Is.False);
            Assert.That(disabled.AllocatedBytes, Is.Zero);
            Assert.That(disabled.DescriptorCount, Is.Zero);
            Assert.That(disabled.ProductionPassCount, Is.Zero);
            Assert.That(SimpleDdgiGuidingPasses.Create(disabled,
                includeValidationPass: false), Is.Empty);
        });
    }

    [Test]
    public void ValidationReference_IsAllocatedWithoutTakingTheSourceCacheSidecarSlot()
    {
        var allocator = new TrackingAllocator();
        using var manager = new SimpleDdgiGuidingManager();
        SimpleDdgiGuidingLayout validationLayout = CreateLayout(
            allocateValidationReferenceBank: true);

        SimpleDdgiGuidingRuntimeSnapshot snapshot = manager.Reconcile(
            new SimpleDdgiGuidingRuntimeRequest(true, validationLayout), allocator);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.DescriptorCount, Is.EqualTo(3u));
            Assert.That(allocator.LastAllocation.ValidationReference.IsAllocated,
                Is.True);
            Assert.That(allocator.LastAllocation.ValidationReference.Bytes,
                Is.EqualTo(validationLayout.ValidationReferenceBankBytes));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                Is.Not.EqualTo(SimpleDdgiGuidingBindlessSlots.TrainingScratch));
        });
    }

    private static SimpleDdgiGuidingLayout CreateLayout(
        bool allocateValidationReferenceBank = false) =>
        SimpleDdgiGuidingLayoutCompiler.Compile(
            new SimpleDdgiGuidingLayoutRequest(
                SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                PhysicalProbeCapacity: 8,
                ScheduledGuidedProbeCapacity: 2,
                StorageAlignmentBytes: 16UL,
                AllocateValidationReferenceBank: allocateValidationReferenceBank));

    private static GPUSimpleDdgiGuidingDistributionHeader CreateCompletedHeader(
        uint virtualProbeId,
        uint pageGeneration,
        uint distributionGeneration,
        uint proposalEpoch) => new()
        {
            AbiVersion = SimpleDdgiGuidingGpuAbi.Version,
            VirtualProbeId = virtualProbeId,
            PageGeneration = pageGeneration,
            DistributionGeneration = distributionGeneration,
            DirectionProposalEpoch = proposalEpoch,
            SampleCountAndAge = 4u,
            TotalIncidentEnergy = 1.0f,
            PackedLeafResolutionAndFlags =
                SimpleDdgiGuidingGpuAbi.PackLeafResolutionAndFlags(
                    8u,
                    SimpleDdgiGuidingGpuDistributionFlags.BuildComplete)
        };

    private static GPUSimpleDdgiGuidingDistributionHeader CreateIncompleteHeader(
        uint virtualProbeId,
        uint pageGeneration,
        uint distributionGeneration,
        uint proposalEpoch) => new()
        {
            AbiVersion = SimpleDdgiGuidingGpuAbi.Version,
            VirtualProbeId = virtualProbeId,
            PageGeneration = pageGeneration,
            DistributionGeneration = distributionGeneration,
            DirectionProposalEpoch = proposalEpoch,
            TotalIncidentEnergy = 1.0f,
            PackedLeafResolutionAndFlags =
                SimpleDdgiGuidingGpuAbi.PackLeafResolutionAndFlags(
                    8u,
                    SimpleDdgiGuidingGpuDistributionFlags.None)
        };

    private sealed class TrackingAllocator : ISimpleDdgiGuidingGpuResourceAllocator
    {
        private ulong _nextHandle = 1UL;

        public int AllocateCalls { get; private set; }
        public int RetireCalls { get; private set; }
        public SimpleDdgiGuidingGpuAllocation LastAllocation { get; private set; } =
            null!;

        public SimpleDdgiGuidingGpuAllocation Allocate(
            in SimpleDdgiGuidingLayout layout)
        {
            AllocateCalls++;
            ulong bankBytes = layout.PersistentDoubleBufferedBytes / 2UL;
            SimpleDdgiGuidingGpuAllocation allocation = new(
                AllocationId: _nextHandle++,
                DistributionBank0: new(_nextHandle++, bankBytes),
                DistributionBank1: new(_nextHandle++, bankBytes),
                TrainingScratch: new(_nextHandle++, layout.TrainingScratchBytes),
                ValidationReference: layout.ValidationReferenceAllocated
                    ? new(_nextHandle++, layout.ValidationReferenceBankBytes)
                    : default,
                DescriptorCount: 3u);
            LastAllocation = allocation;
            return allocation;
        }

        public void Retire(SimpleDdgiGuidingGpuAllocation allocation)
        {
            Assert.That(allocation.AllocationId, Is.Not.Zero);
            RetireCalls++;
        }
    }
}
