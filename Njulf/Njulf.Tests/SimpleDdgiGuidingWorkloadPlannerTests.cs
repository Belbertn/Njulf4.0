using System;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingWorkloadPlannerTests
{
    [Test]
    public void BootstrapBuild_TrainsUniformRaysWithoutPublishingInvalidPayloads()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        var planner = new SimpleDdgiGuidingWorkloadPlanner(
            layout,
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        SimpleDdgiGuidingFrameProbe[] probes =
        [CreateProbe(physical: 2u, guide: default, queueOffset: 3u)];
        Buffers buffers = new(layout);

        SimpleDdgiGuidingWorkloadCompileResult result = Compile(
            planner,
            CreateToken(candidateGeneration: 7u, proposalEpoch: 1u),
            probes,
            buffers);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compiled, Is.True, result.Reason);
            Assert.That(result.Counts.GuidedProbeCount, Is.EqualTo(1));
            Assert.That(result.Counts.TrainingRecordCount, Is.EqualTo(32u));
            Assert.That(result.Counts.SampleRequestCount, Is.Zero,
                "A zero-filled bootstrap sidecar must stay hidden from trace.");
            Assert.That(buffers.Training[0].RecordOffset, Is.Zero);
            Assert.That(buffers.Training[0].RecordCount, Is.EqualTo(32u));
            Assert.That(buffers.Training[0].QueueOffset, Is.EqualTo(3u));
            Assert.That(buffers.Training[0].RayResultBaseIndex,
                Is.EqualTo(3u * 64u));
            Assert.That(buffers.Training[0].DirectionSlotsPerProbe,
                Is.EqualTo(64u));
            Assert.That(buffers.Training[0].SourceEpoch, Is.EqualTo(12u));
            Assert.That(buffers.Training[0].SourceLightingGeneration,
                Is.EqualTo(13u));
            Assert.That(buffers.Build[0].TargetDistributionGeneration,
                Is.EqualTo(7u));
            Assert.That(buffers.Build[0].TargetProposalEpoch, Is.EqualTo(1u));
        });
    }

    [Test]
    public void FirstReadableGuide_SamplesCompletePayloadSetTransactionally()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        var planner = new SimpleDdgiGuidingWorkloadPlanner(
            layout,
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        SimpleDdgiGuidingReadableProbeIdentity guide = new(
            1u, 101u, 8u, 4u, 1u, 0);
        SimpleDdgiGuidingFrameProbe probe = CreateProbe(1u, guide);
        Buffers buffers = new(layout);

        SimpleDdgiGuidingWorkloadCompileResult first = Compile(
            planner,
            CreateToken(5u, 1u),
            [probe],
            buffers);

        Assert.Multiple(() =>
        {
            Assert.That(first.Compiled, Is.True, first.Reason);
            Assert.That(first.Counts.SampleRequestCount, Is.EqualTo(64));
            Assert.That(first.Counts.BootstrapProbeCount, Is.EqualTo(1));
            Assert.That(first.Counts.SampleCommitCount, Is.EqualTo(1));
            Assert.That(buffers.Samples.Take(64).Select(x => x.SlotIndex),
                Is.EqualTo(Enumerable.Range(0, 64).Select(x => (uint)x)));
            Assert.That(buffers.Samples.Take(64).Select(x => x.TraceRayIndex),
                Is.EqualTo(Enumerable.Range(64, 64).Select(x => (uint)x)),
                "Compact trace payloads must be keyed by stable physical " +
                "probe, never by the frame-local queue position.");
            Assert.That(buffers.Samples.Take(64).Count(x =>
                    x.Technique == (uint)SimpleDdgiDirectionSamplingTechnique
                        .UniformMaintenance),
                Is.EqualTo(16));
        });

        Assert.That(planner.TryCommitSamples(
            buffers.Commits.AsSpan(0, 1),
            gpuWorkCompleted: true,
            validationCountersZero: false,
            out _), Is.False);
        Buffers retryBuffers = new(layout);
        SimpleDdgiGuidingWorkloadCompileResult retry = Compile(
            planner,
            CreateToken(6u, 1u),
            [probe],
            retryBuffers);
        Assert.That(retry.Counts.SampleRequestCount, Is.EqualTo(64),
            "An unvalidated sample dispatch must be retried exactly.");

        Assert.That(planner.TryCommitSamples(
            retryBuffers.Commits.AsSpan(0, 1),
            gpuWorkCompleted: true,
            validationCountersZero: true,
            out string commitReason), Is.True, commitReason);
        Buffers stableBuffers = new(layout);
        SimpleDdgiGuidingWorkloadCompileResult stable = Compile(
            planner,
            CreateToken(7u, 1u),
            [probe],
            stableBuffers);
        Assert.That(stable.Counts.SampleRequestCount, Is.Zero,
            "A stable proposal epoch must preserve cached directions.");
    }

    [Test]
    public void NewProposalEpoch_RotatesAtMostQuarterOfGuidedSlotsAndNoMaintenance()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        var planner = new SimpleDdgiGuidingWorkloadPlanner(
            layout,
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        Buffers bootstrapBuffers = new(layout);
        SimpleDdgiGuidingFrameProbe epoch1 = CreateProbe(
            0u,
            new(0u, 100u, 8u, 3u, 1u, 0));
        SimpleDdgiGuidingWorkloadCompileResult bootstrap = Compile(
            planner,
            CreateToken(4u, 1u),
            [epoch1],
            bootstrapBuffers);
        Assert.That(bootstrap.Compiled, Is.True, bootstrap.Reason);
        Assert.That(planner.TryCommitSamples(
            bootstrapBuffers.Commits.AsSpan(0, 1),
            true,
            true,
            out _), Is.True);

        Buffers rotatedBuffers = new(layout);
        SimpleDdgiGuidingFrameProbe epoch2 = epoch1 with
        {
            ReadableGuide = new(0u, 100u, 8u, 5u, 2u, 1)
        };
        SimpleDdgiGuidingWorkloadCompileResult rotated = Compile(
            planner,
            CreateToken(6u, 2u),
            [epoch2],
            rotatedBuffers);

        Assert.Multiple(() =>
        {
            Assert.That(rotated.Compiled, Is.True, rotated.Reason);
            // 64 total - 16 fixed maintenance = 48 guided; 25% = 12.
            Assert.That(rotated.Counts.SampleRequestCount, Is.EqualTo(12));
            Assert.That(rotated.Counts.RotatedGuidedSlotCount, Is.EqualTo(12));
            Assert.That(rotated.Counts.BootstrapProbeCount, Is.Zero);
            Assert.That(rotatedBuffers.Samples.Take(12).All(x =>
                    x.Technique ==
                    (uint)SimpleDdgiDirectionSamplingTechnique.Mixture),
                Is.True);
            Assert.That(rotatedBuffers.Samples.Take(12).Select(x => x.SlotIndex)
                    .Distinct().Count(),
                Is.EqualTo(12));
        });
    }

    [Test]
    public void PhysicalSlotOwnerChange_ForcesCompleteReplacementEvenAtSameEpoch()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        var planner = new SimpleDdgiGuidingWorkloadPlanner(
            layout,
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        Buffers firstBuffers = new(layout);
        SimpleDdgiGuidingFrameProbe first = CreateProbe(
            3u,
            new(3u, 103u, 8u, 9u, 4u, 0));
        _ = Compile(planner, CreateToken(10u, 4u), [first], firstBuffers);
        Assert.That(planner.TryCommitSamples(
            firstBuffers.Commits.AsSpan(0, 1), true, true, out _), Is.True);

        SimpleDdgiGuidingFrameProbe replacement = first with
        {
            VirtualProbeId = 903u,
            PageGeneration = 3u,
            StableProbeId = 0x0000_000b_0000_0387UL,
            ReadableGuide = new(3u, 903u, 3u, 10u, 4u, 1)
        };
        Buffers replacementBuffers = new(layout);
        SimpleDdgiGuidingWorkloadCompileResult result = Compile(
            planner,
            CreateToken(11u, 4u),
            [replacement],
            replacementBuffers);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compiled, Is.True, result.Reason);
            Assert.That(result.Counts.SampleRequestCount, Is.EqualTo(64));
            Assert.That(result.Counts.BootstrapProbeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReadableProposalRegression_IsRejectedInsteadOfSilentlyReusingStalePayloads()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        var planner = new SimpleDdgiGuidingWorkloadPlanner(
            layout,
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        SimpleDdgiGuidingFrameProbe current = CreateProbe(
            0u,
            new(0u, 100u, 8u, 4u, 2u, 0));
        Buffers first = new(layout);
        SimpleDdgiGuidingWorkloadCompileResult initial = Compile(
            planner,
            CreateToken(5u, 2u),
            [current],
            first);
        Assert.That(initial.Compiled, Is.True, initial.Reason);
        Assert.That(planner.TryCommitSamples(
            first.Commits.AsSpan(0, 1),
            gpuWorkCompleted: true,
            validationCountersZero: true,
            out _), Is.True);

        SimpleDdgiGuidingFrameProbe regressed = current with
        {
            ReadableGuide = new(0u, 100u, 8u, 3u, 1u, 1)
        };
        SimpleDdgiGuidingWorkloadCompileResult result = Compile(
            planner,
            CreateToken(6u, 2u),
            [regressed],
            new Buffers(layout));

        Assert.Multiple(() =>
        {
            Assert.That(result.Compiled, Is.False);
            Assert.That(result.Reason, Does.Contain("proposal-epoch-regressed"));
        });
    }

    [Test]
    public void ProposalEpochController_RequiresMaterialChangeAndMinimumAge()
    {
        var controller = new SimpleDdgiGuidingProposalEpochController(
            SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
        Assert.That(controller.TryPlan(100u, 1.0f,
            out SimpleDdgiGuidingProposalEpochPlan bootstrap, out _), Is.True);
        Assert.That(bootstrap.TargetEpoch, Is.EqualTo(1u));
        Assert.That(bootstrap.AdvancesEpoch, Is.False);
        Assert.That(controller.Commit(bootstrap, out _), Is.True);

        Assert.That(controller.TryPlan(123u, 1.0f,
            out SimpleDdgiGuidingProposalEpochPlan tooYoung, out _), Is.True);
        Assert.That(tooYoung.TargetEpoch, Is.EqualTo(1u));
        Assert.That(controller.Commit(tooYoung, out _), Is.True);

        Assert.That(controller.TryPlan(124u, 0.01f,
            out SimpleDdgiGuidingProposalEpochPlan unchanged, out _), Is.True);
        Assert.That(unchanged.TargetEpoch, Is.EqualTo(1u));
        Assert.That(controller.Commit(unchanged, out _), Is.True);

        Assert.That(controller.TryPlan(124u, 0.25f,
            out SimpleDdgiGuidingProposalEpochPlan advance, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(advance.AdvancesEpoch, Is.True);
            Assert.That(advance.TargetEpoch, Is.EqualTo(2u));
        });
        Assert.That(controller.Abort(advance), Is.True);
        Assert.That(controller.PublishedEpoch, Is.EqualTo(1u));
    }

    private static SimpleDdgiGuidingWorkloadCompileResult Compile(
        SimpleDdgiGuidingWorkloadPlanner planner,
        in SimpleDdgiGuidingBuildToken token,
        ReadOnlySpan<SimpleDdgiGuidingFrameProbe> probes,
        Buffers buffers) =>
        planner.TryCompile(
            token,
            probes,
            buffers.Selected,
            buffers.Training,
            buffers.Build,
            buffers.Headers,
            buffers.Samples,
            buffers.Commits);

    private static SimpleDdgiGuidingLayout CreateLayout()
    {
        const int probes = 4;
        const int slots = 64;
        ulong sidecarBytes = (ulong)probes * slots *
            SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount;
        return SimpleDdgiGuidingLayoutCompiler.Compile(
            new SimpleDdgiGuidingLayoutRequest(
                SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                probes,
                ScheduledGuidedProbeCapacity: probes,
                StorageAlignmentBytes: 16UL,
                AllocateValidationReferenceBank: false)
            {
                DirectionSlotsPerProbe = slots,
                DirectionPdfSidecarBudgetBytes = sidecarBytes
            });
    }

    private static SimpleDdgiGuidingBuildToken CreateToken(
        uint candidateGeneration,
        uint proposalEpoch) => new(
            AllocationEpoch: 3UL,
            ReadBankIndex: candidateGeneration == 1u ? -1 : 0,
            WriteBankIndex: 1,
            ExpectedReadBankGeneration: candidateGeneration - 1u,
            CandidateBankGeneration: candidateGeneration,
            TargetProposalEpoch: proposalEpoch,
            GuidingAbiVersion: SimpleDdgiGuidingGpuAbi.Version,
            LeafResolution: 8);

    private static SimpleDdgiGuidingFrameProbe CreateProbe(
        uint physical,
        SimpleDdgiGuidingReadableProbeIdentity guide,
        uint queueOffset = 0u) => new(
            QueueOffset: queueOffset,
            VirtualProbeId: 100u + physical,
            PhysicalProbeIndex: physical,
            PageGeneration: 8u,
            StableProbeId: ((ulong)(7u + physical) << 32) | (100u + physical),
            SourceEpoch: 12u,
            SourceLightingGeneration: 13u,
            ContentRevision: 14u,
            ActiveRayCount: 32u,
            IsFullSourceTrace: true,
            ReadableGuide: guide);

    private sealed class Buffers
    {
        public Buffers(SimpleDdgiGuidingLayout layout)
        {
            int probes = layout.ScheduledGuidedProbeCapacity;
            Selected = new SimpleDdgiGuidingFrameProbe[probes];
            Training = new GPUSimpleDdgiGuidingTrainingWorkItem[probes];
            Build = new GPUSimpleDdgiGuidingBuildWorkItem[probes];
            Headers = new SimpleDdgiGuidingExpectedProbeHeader[probes];
            Samples = new GPUSimpleDdgiGuidingSampleRequest[
                checked(probes * layout.DirectionSlotsPerProbe)];
            Commits = new SimpleDdgiGuidingSampleCommit[probes];
        }

        public SimpleDdgiGuidingFrameProbe[] Selected { get; }
        public GPUSimpleDdgiGuidingTrainingWorkItem[] Training { get; }
        public GPUSimpleDdgiGuidingBuildWorkItem[] Build { get; }
        public SimpleDdgiGuidingExpectedProbeHeader[] Headers { get; }
        public GPUSimpleDdgiGuidingSampleRequest[] Samples { get; }
        public SimpleDdgiGuidingSampleCommit[] Commits { get; }
    }
}
