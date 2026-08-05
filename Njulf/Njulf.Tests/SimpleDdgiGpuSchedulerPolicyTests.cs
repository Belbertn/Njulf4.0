using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerPolicyTests
{
    [Test]
    public void EveryLaneRoundTripsThroughThePackedIndex()
    {
        for (int lane = 0; lane < SimpleDdgiSchedulerAbi.MaxLaneCount; lane++)
        {
            SimpleDdgiSchedulerAbi.DecodeLaneIndex(
                lane,
                out int volume,
                out SimpleDdgiSchedulerWorkClass workClass,
                out SimpleDdgiSchedulerTransportCategory transport,
                out SimpleDdgiSchedulerRayTier rayTier);
            Assert.That(
                SimpleDdgiSchedulerAbi.GetLaneIndex(volume, workClass, transport, rayTier),
                Is.EqualTo(lane));
        }
    }

    [Test]
    public void CandidateAndLifecyclePackingRejectOutOfRangeValues()
    {
        uint packedClass = SimpleDdgiSchedulerAbi.PackCandidateWorkClassAndTransport(
            SimpleDdgiSchedulerWorkClass.VisibleDirty,
            SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation);
        SimpleDdgiSchedulerAbi.UnpackCandidateWorkClassAndTransport(
            packedClass,
            out SimpleDdgiSchedulerWorkClass workClass,
            out SimpleDdgiSchedulerTransportCategory transport);

        uint packedReason = SimpleDdgiSchedulerAbi.PackCandidateRayTierAndReasons(
            SimpleDdgiSchedulerRayTier.Maintenance,
            SimpleDdgiSchedulerCandidateReason.RegionalDirty |
            SimpleDdgiSchedulerCandidateReason.Visible);
        SimpleDdgiSchedulerAbi.UnpackCandidateRayTierAndReasons(
            packedReason,
            out SimpleDdgiSchedulerRayTier rayTier,
            out SimpleDdgiSchedulerCandidateReason reasons);

        Assert.Multiple(() =>
        {
            Assert.That(workClass, Is.EqualTo(SimpleDdgiSchedulerWorkClass.VisibleDirty));
            Assert.That(transport, Is.EqualTo(SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation));
            Assert.That(rayTier, Is.EqualTo(SimpleDdgiSchedulerRayTier.Maintenance));
            Assert.That(reasons, Is.EqualTo(SimpleDdgiSchedulerCandidateReason.RegionalDirty |
                SimpleDdgiSchedulerCandidateReason.Visible));
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(0, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackSchedulerProbeLifecycle(512, 0, 0, 0, 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(0x00ffffffu)]
    [TestCase(0x01000000u)]
    public void GenerationPackingKeepsNonZeroPhysicalGenerations(uint generation)
    {
        if (generation == 0 || generation > SimpleDdgiSchedulerAbi.PhysicalGenerationMask)
        {
            Assert.That(
                () => SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(generation, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            return;
        }

        uint packed = SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(generation, uint.MaxValue);
        Assert.That(packed & SimpleDdgiSchedulerAbi.PhysicalGenerationMask, Is.EqualTo(generation));
        Assert.That(packed >> 24, Is.EqualTo(255u));
    }

    [Test]
    public void CommitOutcomeRejectsPartialProducersFailuresAndGenerationMismatches()
    {
        const uint queueGeneration = 11u;
        const uint schedulerGeneration = 12u;
        const uint volumeGeneration = 13u;
        const uint sourceGeneration = 14u;
        const uint transportGeneration = 15u;
        const uint physicalGeneration = 16u;
        const uint requiredMask = 0xffu;

        GPUSimpleDdgiUpdateOutcome valid = new()
        {
            QueueTransactionGeneration = queueGeneration,
            SchedulerResourceGeneration = schedulerGeneration,
            VolumeTableGeneration = volumeGeneration,
            SourceLightingGeneration = sourceGeneration,
            TransportGeneration = transportGeneration,
            ExpectedPhysicalGeneration = physicalGeneration,
            RequiredCompletionMask = requiredMask,
            CompletionMask = requiredMask
        };

        Assert.That(
            SimpleDdgiSchedulerAbi.OutcomeCanCommit(
                valid,
                queueGeneration,
                schedulerGeneration,
                volumeGeneration,
                sourceGeneration,
                transportGeneration,
                physicalGeneration),
            Is.True);

        for (int bit = 0; bit < 8; bit++)
        {
            GPUSimpleDdgiUpdateOutcome partial = valid;
            partial.CompletionMask = requiredMask & ~(1u << bit);
            Assert.That(
                SimpleDdgiSchedulerAbi.OutcomeCanCommit(
                    partial,
                    queueGeneration,
                    schedulerGeneration,
                    volumeGeneration,
                    sourceGeneration,
                    transportGeneration,
                    physicalGeneration),
                Is.False,
                $"missing completion bit {bit}");
        }

        GPUSimpleDdgiUpdateOutcome failed = valid;
        failed.FailureReason = 1u;
        Assert.That(
            SimpleDdgiSchedulerAbi.OutcomeCanCommit(
                failed,
                queueGeneration,
                schedulerGeneration,
                volumeGeneration,
                sourceGeneration,
                transportGeneration,
                physicalGeneration),
            Is.False);

        uint[] currentGenerations =
        [
            queueGeneration + 1u,
            schedulerGeneration + 1u,
            volumeGeneration + 1u,
            sourceGeneration + 1u,
            transportGeneration + 1u,
            physicalGeneration + 1u
        ];
        for (int mismatch = 0; mismatch < currentGenerations.Length; mismatch++)
        {
            uint queue = mismatch == 0 ? currentGenerations[mismatch] : queueGeneration;
            uint scheduler = mismatch == 1 ? currentGenerations[mismatch] : schedulerGeneration;
            uint volume = mismatch == 2 ? currentGenerations[mismatch] : volumeGeneration;
            uint source = mismatch == 3 ? currentGenerations[mismatch] : sourceGeneration;
            uint transport = mismatch == 4 ? currentGenerations[mismatch] : transportGeneration;
            uint physical = mismatch == 5 ? currentGenerations[mismatch] : physicalGeneration;
            Assert.That(
                SimpleDdgiSchedulerAbi.OutcomeCanCommit(
                    valid,
                    queue,
                    scheduler,
                    volume,
                    source,
                    transport,
                    physical),
                Is.False,
                $"generation mismatch {mismatch}");
        }

        GPUSimpleDdgiUpdateOutcome zeroPhysical = valid;
        zeroPhysical.ExpectedPhysicalGeneration = 0u;
        Assert.That(
            SimpleDdgiSchedulerAbi.OutcomeCanCommit(
                zeroPhysical,
                queueGeneration,
                schedulerGeneration,
                volumeGeneration,
                sourceGeneration,
                transportGeneration,
                physicalGeneration),
            Is.False);
    }

    [Test]
    public void PersistentGpuDirtyRegionKeepsOneEventGeneration()
    {
        const ulong signature = 0x1020304050607080UL;
        uint firstEvent = SimpleDdgiVolumeManager.ResolveGpuDirtyRegionGeneration(
            currentGeneration: 1u,
            previousRegionsPresent: false,
            previousSignature: 0u,
            currentSignature: signature);
        uint persistentEvent = SimpleDdgiVolumeManager.ResolveGpuDirtyRegionGeneration(
            currentGeneration: firstEvent,
            previousRegionsPresent: true,
            previousSignature: signature,
            currentSignature: signature);

        Assert.That(firstEvent, Is.EqualTo(2u));
        Assert.That(persistentEvent, Is.EqualTo(firstEvent));
    }

    [Test]
    public void ChangedOrRepublishedGpuDirtyRegionStartsANewEventGeneration()
    {
        const ulong firstSignature = 0x1020304050607080UL;
        const ulong changedSignature = 0x1020304050607081UL;
        uint changedEvent = SimpleDdgiVolumeManager.ResolveGpuDirtyRegionGeneration(
            currentGeneration: 41u,
            previousRegionsPresent: true,
            previousSignature: firstSignature,
            currentSignature: changedSignature);
        uint republishedAfterGap = SimpleDdgiVolumeManager.ResolveGpuDirtyRegionGeneration(
            currentGeneration: changedEvent,
            previousRegionsPresent: false,
            previousSignature: changedSignature,
            currentSignature: changedSignature);

        Assert.That(changedEvent, Is.EqualTo(42u));
        Assert.That(republishedAfterGap, Is.EqualTo(43u));
    }
}
