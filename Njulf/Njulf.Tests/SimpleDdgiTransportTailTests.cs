using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTransportTailTests
{
    private static SimpleDdgiTransportGenerations Generations => new(
        VolumeTable: 1u,
        PhysicalOwnership: 2u,
        SourceLighting: 3u,
        SourceEpoch: 4u,
        TransportOperator: 5u,
        CanonicalField: 6u,
        Solve: 7u,
        Audit: 8u,
        Queue: 9u,
        SchedulerResources: 10u);

    [Test]
    public void AuditPass_UsesTwoSequentialChunksWithoutGrowingTheWorkspaceAbi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiTransportAuditPass.MaximumChunksPerFrame,
                Is.EqualTo(2));
            Assert.That(
                SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceProbeCapacity,
                Is.EqualTo(256));
        });
    }

    [Test]
    public void BlockingSourceWork_IncludesEveryResidentTransientAndPackedCause()
    {
        GPUSimpleDdgiSchedulerFeedback[] feedbackCases =
        [
            new() { PendingSourceCount = 1u },
            new() { PendingFreshCount = 2u },
            new() { PendingExposedCount = 3u },
            new() { PendingRelocationCount = 4u },
            new() { PackedPendingSourceInvalidAndCardinalityCounts = 5u << 16 },
            new() { PackedPendingSourceRepairAndGenerationCounts = 6u << 16 }
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveBlockingTailSourceWorkCount(default),
                Is.Zero);
            for (int i = 0; i < feedbackCases.Length; i++)
            {
                Assert.That(
                    SimpleDdgiVolumeManager.HasBlockingTailSourceWork(feedbackCases[i]),
                    Is.True,
                    $"source-work cause {i} must block audit");
            }
            Assert.That(
                SimpleDdgiVolumeManager.ResolveBlockingTailSourceWorkCount(
                    new GPUSimpleDdgiSchedulerFeedback
                    {
                        PendingSourceCount = 2u,
                        PendingFreshCount = 7u,
                        PendingRelocationCount = 3u
                    }),
                Is.EqualTo(7u));
        });
    }

    [Test]
    public void LocalSourceRepair_ReopensTheSameSolveEpoch()
    {
        var controller = new SimpleDdgiTransportSolveController(2);
        controller.BeginSourceRepair(Generations);
        Assert.That(controller.BeginSolveEpoch(Generations, 2), Is.True);
        uint solveEpoch = controller.SolveEpoch;
        SimpleDdgiTransportGenerations solveGenerations =
            controller.FrozenGenerations;
        Assert.That(
            controller.MarkGpuEpochComplete(solveEpoch, 2, solveGenerations),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(
                controller.PauseSolveForLocalSourceRepair(solveGenerations),
                Is.True);
            Assert.That(controller.SolveEpoch, Is.EqualTo(solveEpoch));
            Assert.That(controller.Phase,
                Is.EqualTo(SimpleDdgiTransportPhase.AcceleratedSolve));
            Assert.That(controller.ExpectedParticipantCount, Is.EqualTo(2));
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
            Assert.That(controller.IsSolveEpochComplete, Is.False);
            Assert.That(controller.LastReason,
                Is.EqualTo(
                    SimpleDdgiTransportCertificationReason.SourceRepairRequired));
        });

        Assert.That(
            controller.MarkGpuEpochComplete(solveEpoch, 2, solveGenerations),
            Is.True,
            "a complete GPU reduction may close the retained epoch after repaired probes reacquire their stamps");
    }

    [Test]
    public void ResidentGenerationWitness_IgnoresExcludedInactiveRetryMutation()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ShouldAdvanceResidentSourceEpoch(
                    admittedSourceProbeCount: 23u,
                    activeParticipantSourceMutationCount: 0u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldAdvanceResidentCanonicalGeneration(
                    publishedProbeCount: 23u,
                    activeParticipantCanonicalMutationCount: 0u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldAdvanceResidentSourceEpoch(23u, 1u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldAdvanceResidentCanonicalGeneration(23u, 1u),
                Is.True);
        });
    }

    [Test]
    public void ResidentAtlasFresh_RetiresOnlyAfterAResidentCommit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRetireResidentAtlasFresh(
                    SimpleDdgiSchedulerMode.GpuResident,
                    atlasFresh: true,
                    committedProbeCount: 1u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRetireResidentAtlasFresh(
                    SimpleDdgiSchedulerMode.GpuResident,
                    atlasFresh: true,
                    committedProbeCount: 0u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRetireResidentAtlasFresh(
                    SimpleDdgiSchedulerMode.GpuMirror,
                    atlasFresh: true,
                    committedProbeCount: 1u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRetireResidentAtlasFresh(
                    SimpleDdgiSchedulerMode.GpuResident,
                    atlasFresh: false,
                    committedProbeCount: 1u),
                Is.False);
        });
    }

    [Test]
    public void SolveDrain_RequiresANewerQuiescedFenceCompleteFeedback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 101UL, 0u, 0u, 0u, false),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 100UL, 0u, 0u, 0u, false),
                Is.False,
                "the completion packet must postdate the quiesce request");
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 101UL, 7u, 0u, 0u, false),
                Is.False,
                "a nonzero epoch still permits cached solve mutation");
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 101UL, 0u, 1u, 0u, false),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 101UL, 0u, 0u, 1u, false),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.CanCompleteTransportSolveDrain(
                    true, 100UL, 101UL, 0u, 0u, 0u, true),
                Is.False);
        });
    }

    [Test]
    public void AuditMismatchIdentity_DecodesBoundedVirtualAndPhysicalWitness()
    {
        uint packed = (321u + 1u) | ((654u + 1u) << 16);
        SimpleDdgiTransportMismatchIdentity identity =
            SimpleDdgiTransportMismatchIdentity.FromPacked(packed);

        Assert.Multiple(() =>
        {
            Assert.That(identity.IsValid, Is.True);
            Assert.That(identity.VirtualProbeIndex, Is.EqualTo(321u));
            Assert.That(identity.PhysicalProbeIndex, Is.EqualTo(654u));
            Assert.That(
                SimpleDdgiTransportMismatchIdentity.FromPacked(0u).IsValid,
                Is.False);
        });
    }

    [Test]
    public void ConvergenceDeadline_IncludesSourceSolveAuditAndSchedulingWindows()
    {
        Assert.That(
            SimpleDdgiTransportSolveController.ResolveConvergenceDeadlineFrames(
                sourceSweepFrames: 120,
                participantCount: 5_787,
                solveProbeBudgetPerFrame: 256,
                acceleratedSweepCount: 2,
                auditDeadlineFrames: 27,
                schedulingMarginFrames: 4),
            Is.EqualTo(197));
    }

    [Test]
    public void CompleteFiniteTailAudit_RebasesTheNextEpochDeadline()
    {
        var controller = CreateAuditingController(participantCount: 2);
        SimpleDdgiTransportTailSummary progress = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 2u,
            auditedParticipants: 2u,
            reason: SimpleDdgiTransportCertificationReason.TailAboveTolerance);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldRebaseTransportConvergenceDeadline(progress),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldRebaseTransportConvergenceDeadline(
                        progress with { IsComplete = false }),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldRebaseTransportConvergenceDeadline(
                        progress with { NonFiniteCount = 1u }),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldRebaseTransportConvergenceDeadline(
                        progress with
                        {
                            Reason = SimpleDdgiTransportCertificationReason
                                .ParticipantCoverageIncomplete
                        }),
                Is.False);
        });
    }

    [TestCase(SimpleDdgiTransportPhase.Certified, false, true)]
    [TestCase(SimpleDdgiTransportPhase.Certified, true, false)]
    [TestCase(SimpleDdgiTransportPhase.AcceleratedSolve, false, false)]
    [TestCase(SimpleDdgiTransportPhase.SourceRepair, false, false)]
    public void StaleCertificate_StartsANewConvergenceDeadlineWave(
        SimpleDdgiTransportPhase phase,
        bool certificateCurrent,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager
                .ShouldRestartTransportConvergenceForStaleCertificate(
                    phase,
                    certificateCurrent),
            Is.EqualTo(expected));
    }

    [Test]
    public void AcceptedCertificate_RearmsPeriodicSourceRefreshAfterFullInterval()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveNextPeriodicSourceRefreshFrame(
                    certificateFrame: 1_000u,
                    refreshIntervalFrames: 480),
                Is.EqualTo(1_480u));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveNextPeriodicSourceRefreshFrame(
                    certificateFrame: uint.MaxValue - 2u,
                    refreshIntervalFrames: 4),
                Is.EqualTo(1u),
                "the GPU frame comparison is wrap-safe, so the CPU control frame must wrap identically");
            Assert.That(
                SimpleDdgiVolumeManager.ResolveNextPeriodicSourceRefreshFrame(
                    certificateFrame: 77u,
                    refreshIntervalFrames: 0),
                Is.EqualTo(78u));
        });
    }

    [Test]
    public void CertifiedMaintenance_QuiescesBetweenPulsesButNeverMasksRealWork()
    {
        const uint interval = 64u;
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, false, false, false, 0u, 65u, interval),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, false, false, false, 0u, 128u, interval),
                Is.False,
                "the deterministic maintenance pulse must remain live");
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    false, false, false, false, false, 0u, 65u, interval),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, true, false, false, false, 0u, 65u, interval),
                Is.False,
                "periodic source replacement has priority");
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, true, false, false, 0u, 65u, interval),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, false, true, false, 0u, 65u, interval),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, false, false, true, 0u, 65u, interval),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldQuiesceCertifiedResidentMaintenance(
                    true, false, false, false, false, 1u, 65u, interval),
                Is.False,
                "dirty work must open admission immediately");
        });
    }

    [Test]
    public void PostBootstrapPageManagement_WakesForIdentityChangesAndPeriodicAudit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, true, false, false, false, 1u, 64u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, true, false, false, false, 64u, 64u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    true, true, false, false, false, 1u, 64u),
                Is.True,
                "sparse bootstrap remains eager until its authoritative boundary");
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, false, false, false, false, 1u, 64u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, true, true, false, false, 1u, 64u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, true, false, true, false, 1u, 64u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ShouldRunFullProbePageManagement(
                    false, true, false, false, true, 1u, 64u),
                Is.True);
        });
    }

    [Test]
    public void ResidencyBootstrapClassification_EndsOnlyAtAnAuthoritativeBoundary()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        true, false, true, 200u, 0u, 0u),
                Is.False,
                "a quiet page summary cannot replace a requested transport certificate");
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        true, true, false, 0u, 17u, 8u),
                Is.True,
                "the current certificate is the authoritative tail-enabled boundary");
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        false, false, true, 200u, 0u, 0u),
                Is.True,
                "tail-disabled configurations use a fence-complete quiet page summary");
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        false, false, true, 200u, 1u, 0u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        false, false, true, 200u, 0u, 1u),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldCompleteProbeResidencyBootstrapClassification(
                        false, false, false, 200u, 0u, 0u),
                Is.False);
        });
    }

    [Test]
    public void ParticipantCoverage_IncludesOffscreenActiveProbe()
    {
        (bool Visible, bool Inactive)[] probes =
        [
            (Visible: true, Inactive: false),
            (Visible: false, Inactive: false),
            (Visible: false, Inactive: true)
        ];

        uint expectedParticipants = 0u;
        uint auditedParticipants = 0u;
        uint excludedInactive = 0u;
        uint excludedNotVisible = 0u;
        foreach ((bool visible, bool inactive) in probes)
        {
            if (inactive)
            {
                excludedInactive++;
                continue;
            }

            bool participating = SimpleDdgiSchedulerAbi.IsTailCertificationParticipant(
                inactive,
                sourceReady: true,
                fresh: false,
                scrollExposed: false,
                relocationPending: false,
                sourceCacheInvalid: false);
            if (participating)
            {
                expectedParticipants++;
                auditedParticipants++;
            }
            else if (!visible)
            {
                excludedNotVisible++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(expectedParticipants, Is.EqualTo(2u));
            Assert.That(auditedParticipants, Is.EqualTo(2u));
            Assert.That(excludedInactive, Is.EqualTo(1u));
            Assert.That(excludedNotVisible, Is.Zero);
        });
    }

    [TestCase(128u, true)]
    [TestCase(127u, false)]
    [TestCase(64u, false)]
    [TestCase(0u, false)]
    public void SourceCacheCardinality_RequiresCompleteCurrentSequence(
        uint storedSourceRayCount,
        bool expectedValid)
    {
        bool valid = SimpleDdgiTransportTailEstimator.IsCompleteCurrentSourceCacheEntry(
            storedSourceRayCount,
            requiredSourceRayCount: 128u,
            physicalGeneration: 17u,
            expectedPhysicalGeneration: 17u,
            sourceLightingGeneration: 23u,
            expectedSourceLightingGeneration: 23u,
            sourceEpoch: 29u,
            expectedSourceEpoch: 29u);

        Assert.That(valid, Is.EqualTo(expectedValid));
    }

    [Test]
    public void ResidentFrameWork_UsesGenerationValidatedGpuFeedback()
    {
        var feedback = new GPUSimpleDdgiSchedulerFeedback
        {
            AcceptedCount = 17u,
            SourceProbeUsed = 5u,
            PrimaryRayUsed = 640u,
            SourceAchievedRays = 640u,
            TransportRayUsed = 1_408u,
            PublishedCount = 16u
        };

        VulkanRenderer.SimpleDdgiFrameWork work =
            VulkanRenderer.ResolveSimpleDdgiFrameWork(
                rayUpdateActive: true,
                SimpleDdgiSchedulerMode.GpuResident,
                gpuFeedbackValid: true,
                feedback,
                cpuScheduledProbeCount: 0,
                cpuSourceRefreshProbeCount: 0,
                cpuPrimaryRayCount: 0UL,
                cpuSourceRayCount: 0UL,
                cpuTransportRayCount: 0UL,
                cpuPublishedProbeCount: 0);

        Assert.That(work, Is.EqualTo(new VulkanRenderer.SimpleDdgiFrameWork(
            ScheduledProbeCount: 17,
            SourceRefreshProbeCount: 5,
            PrimaryRayCount: 640UL,
            SourceRayCount: 640UL,
            TransportRayCount: 1_408UL,
            PublishedProbeCount: 16)));
    }

    [TestCase(SimpleDdgiSchedulerMode.CpuReference, false)]
    [TestCase(SimpleDdgiSchedulerMode.GpuMirror, true)]
    [TestCase(SimpleDdgiSchedulerMode.GpuResident, false)]
    public void NonAuthoritativeResidentFeedback_DoesNotReplaceCpuWork(
        SimpleDdgiSchedulerMode schedulerMode,
        bool gpuFeedbackValid)
    {
        var feedback = new GPUSimpleDdgiSchedulerFeedback
        {
            AcceptedCount = 99u,
            SourceProbeUsed = 99u,
            PrimaryRayUsed = 99u,
            SourceAchievedRays = 99u,
            TransportRayUsed = 99u,
            PublishedCount = 99u
        };

        VulkanRenderer.SimpleDdgiFrameWork work =
            VulkanRenderer.ResolveSimpleDdgiFrameWork(
                rayUpdateActive: true,
                schedulerMode,
                gpuFeedbackValid,
                feedback,
                cpuScheduledProbeCount: 7,
                cpuSourceRefreshProbeCount: 3,
                cpuPrimaryRayCount: 384UL,
                cpuSourceRayCount: 384UL,
                cpuTransportRayCount: 896UL,
                cpuPublishedProbeCount: 6);

        Assert.That(work, Is.EqualTo(new VulkanRenderer.SimpleDdgiFrameWork(
            ScheduledProbeCount: 7,
            SourceRefreshProbeCount: 3,
            PrimaryRayCount: 384UL,
            SourceRayCount: 384UL,
            TransportRayCount: 896UL,
            PublishedProbeCount: 6)));
    }

    [Test]
    public void InactiveRayProducer_ReportsNoFrameWork()
    {
        VulkanRenderer.SimpleDdgiFrameWork work =
            VulkanRenderer.ResolveSimpleDdgiFrameWork(
                rayUpdateActive: false,
                SimpleDdgiSchedulerMode.GpuResident,
                gpuFeedbackValid: true,
                new GPUSimpleDdgiSchedulerFeedback
                {
                    AcceptedCount = 1u,
                    SourceProbeUsed = 1u,
                    PrimaryRayUsed = 128u,
                    SourceAchievedRays = 128u,
                    TransportRayUsed = 128u,
                    PublishedCount = 1u
                },
                cpuScheduledProbeCount: 1,
                cpuSourceRefreshProbeCount: 1,
                cpuPrimaryRayCount: 128UL,
                cpuSourceRayCount: 128UL,
                cpuTransportRayCount: 128UL,
                cpuPublishedProbeCount: 1);

        Assert.That(work, Is.EqualTo(default(VulkanRenderer.SimpleDdgiFrameWork)));
    }

    [Test]
    public void SourceCacheIdentity_RequiresEveryGenerationToMatch()
    {
        static bool Validate(uint physical, uint source, uint epoch) =>
            SimpleDdgiTransportTailEstimator.IsCompleteCurrentSourceCacheEntry(
                storedSourceRayCount: 128u,
                requiredSourceRayCount: 128u,
                physicalGeneration: physical,
                expectedPhysicalGeneration: 17u,
                sourceLightingGeneration: source,
                expectedSourceLightingGeneration: 23u,
                sourceEpoch: epoch,
                expectedSourceEpoch: 29u);

        Assert.Multiple(() =>
        {
            Assert.That(Validate(17u, 23u, 29u), Is.True);
            Assert.That(Validate(16u, 23u, 29u), Is.False);
            Assert.That(Validate(17u, 22u, 29u), Is.False);
            Assert.That(Validate(17u, 23u, 28u), Is.False);
        });
    }

    [Test]
    public void BlendSweepPolicy_SourceRefreshWritesVisibilityAndLifecycleOnlyInSweepZero()
    {
        int visibilityWrites = 0;
        int lifecycleCompletions = 0;
        int irradianceWrites = 0;
        for (int sweep = 0; sweep < 3; sweep++)
        {
            SimpleDdgiBlendSweepWork work =
                SimpleDdgiTransportSolveController.ResolveBlendSweepWork(
                    sweep,
                    isFirstColor: true,
                    transportV2Active: true,
                    requiresSourceRefresh: true,
                    freshUpdate: sweep == 0);
            visibilityWrites += work.WritesVisibility ? 1 : 0;
            lifecycleCompletions += work.AdvancesOneUpdateLifecycle ? 1 : 0;
            irradianceWrites += work.WritesIrradiance ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(visibilityWrites, Is.EqualTo(1));
            Assert.That(lifecycleCompletions, Is.EqualTo(1));
            Assert.That(irradianceWrites, Is.EqualTo(3));
        });
    }

    [Test]
    public void Controller_GlobalProbeVisitIndicesUseTotalProbeCapacity()
    {
        var controller = new SimpleDdgiTransportSolveController();
        controller.EnsureParticipantCapacity(101);
        Assert.That(controller.BeginSolveEpoch(Generations, expectedParticipantCount: 2), Is.True);
        SimpleDdgiTransportGenerations frozen = controller.FrozenGenerations;

        Assert.Multiple(() =>
        {
            Assert.That(controller.ParticipantVisitCapacity, Is.EqualTo(101));
            Assert.That(controller.ExpectedParticipantCount, Is.EqualTo(2));
            Assert.That(controller.MarkParticipantVisited(0, frozen), Is.True);
            Assert.That(controller.MarkParticipantVisited(100, frozen), Is.True);
            Assert.That(controller.VisitedParticipantCount, Is.EqualTo(2));
            Assert.That(controller.IsSolveEpochComplete, Is.True);
        });
    }

    [TestCase(SimpleDdgiSchedulerMode.GpuResident, false, false)]
    [TestCase(SimpleDdgiSchedulerMode.GpuResident, true, true)]
    [TestCase(SimpleDdgiSchedulerMode.GpuMirror, false, true)]
    [TestCase(SimpleDdgiSchedulerMode.CpuReference, false, true)]
    public void ResidentTailCounts_RequireGenerationMatchedFeedback(
        SimpleDdgiSchedulerMode schedulerMode,
        bool feedbackValid,
        bool expectedAuthority)
    {
        Assert.That(
            SimpleDdgiVolumeManager.CanPrepareTailSolveParticipantCounts(
                schedulerMode,
                feedbackValid),
            Is.EqualTo(expectedAuthority));
    }

    [TestCase(true, 17u, 17u, 17u, true)]
    [TestCase(false, 17u, 17u, 17u, false)]
    [TestCase(true, 16u, 17u, 17u, false)]
    [TestCase(true, 17u, 17u, 16u, false)]
    [TestCase(true, 0u, 0u, 0u, false)]
    public void ResidentPublication_PromotesOnlyCurrentHostLiveBoundary(
        bool hasCurrentLiveSourceBoundary,
        uint feedbackPropagationGeneration,
        uint currentTransportGeneration,
        uint hostPublishedPropagationGeneration,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.CanPromoteHostLivePropagationPublication(
                hasCurrentLiveSourceBoundary,
                feedbackPropagationGeneration,
                currentTransportGeneration,
                hostPublishedPropagationGeneration),
            Is.EqualTo(expected));
    }

    [TestCase(SimpleDdgiSchedulerMode.CpuReference, false,
        SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler)]
    [TestCase(SimpleDdgiSchedulerMode.GpuMirror, false,
        SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler)]
    [TestCase(SimpleDdgiSchedulerMode.GpuResident, true,
        SimpleDdgiTailCertificationFallbackReason.None)]
    public void TailCertificationPolicy_RequiresGpuResident(
        SimpleDdgiSchedulerMode schedulerMode,
        bool expectedEnabled,
        SimpleDdgiTailCertificationFallbackReason expectedReason)
    {
        SimpleDdgiTailCertificationAvailability availability =
            SimpleDdgiTransportSolveController.ResolveTailCertificationAvailability(
                requested: true,
                schedulerMode,
                gpuSchedulerReady: true,
                gpuSchedulerFrameExecutionAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Enabled, Is.EqualTo(expectedEnabled));
            Assert.That(availability.Reason, Is.EqualTo(expectedReason));
            Assert.That(
                availability.Enabled || availability.Message.Length > 0,
                Is.True);
        });
    }

    [Test]
    public void TailCertificationPolicy_FailsClosedWithoutGuidedAudit()
    {
        SimpleDdgiTailCertificationAvailability availability =
            SimpleDdgiTransportSolveController.ResolveTailCertificationAvailability(
                requested: true,
                SimpleDdgiSchedulerMode.GpuResident,
                gpuSchedulerReady: true,
                gpuSchedulerFrameExecutionAvailable: true,
                guidedTransportActive: true,
                guidedAuditAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(availability.Enabled, Is.False);
            Assert.That(availability.Reason, Is.EqualTo(
                SimpleDdgiTailCertificationFallbackReason
                    .GuidedOperatorUnsupported));
            Assert.That(availability.Message, Does.Contain("guided audit"));
        });
    }

    [Test]
    public void EvaluateTail_UsesInfinityNormAndAbsoluteFloor()
    {
        Vector3[] candidate =
        [
            new(1.0f, 0.0f, 0.0f),
            new(0.0f, 0.0f, 0.003f)
        ];
        Vector3[] canonical =
        [
            new(0.99f, 0.0f, 0.0f),
            new(0.0f, 0.0f, 0.0f)
        ];

        SimpleDdgiTransportTailEstimator.TailEstimate estimate =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                candidate,
                canonical,
                configuredContractionBound: 0.9f,
                relativeTolerance: 0.025f);

        Assert.Multiple(() =>
        {
            Assert.That(estimate.FixedPointDefect, Is.EqualTo(0.01f).Within(1e-6f));
            Assert.That(estimate.FieldMagnitude, Is.EqualTo(0.99f).Within(1e-6f));
            Assert.That(estimate.AbsoluteTailBound, Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(estimate.Tolerance, Is.EqualTo(0.02475f).Within(1e-6f));
            Assert.That(estimate.IsWithinTolerance, Is.False);
            Assert.That(estimate.CanCertify, Is.False);
        });
    }

    [Test]
    public void EvaluateTailPerChannel_DoesNotPairUnrelatedDefectAndGainMaxima()
    {
        SimpleDdgiTransportTailEstimator.TailEstimate estimate =
            SimpleDdgiTransportTailEstimator.EvaluateTailPerChannel(
                [new Vector3(1.001f, 1.0f, 1.01f)],
                [Vector3.One],
                configuredContractionBound: 0.9f,
                relativeTolerance: 0.02f,
                observedContractionChannels:
                    new SimpleDdgiTransportRgbBounds(0.9f, 0.2f, 0.1f),
                canonicalQuantizationFloorChannels: default);

        Assert.Multiple(() =>
        {
            Assert.That(estimate.ChannelEvidenceVersion, Is.EqualTo(
                SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion));
            Assert.That(estimate.FixedPointDefect, Is.EqualTo(0.01f).Within(1e-5f));
            Assert.That(estimate.CertifiedContractionBound, Is.EqualTo(0.9f));
            Assert.That(estimate.AbsoluteTailBound, Is.EqualTo(
                0.01f / 0.9f).Within(1e-5f));
            Assert.That(estimate.AbsoluteTailBound, Is.LessThan(0.02f));
            Assert.That(estimate.CanCertify, Is.True);
        });
    }

    [Test]
    public void EvaluateTailPerChannel_RejectsAnyChannelAbovePhysicalCeiling()
    {
        SimpleDdgiTransportTailEstimator.TailEstimate estimate =
            SimpleDdgiTransportTailEstimator.EvaluateTailPerChannel(
                [Vector3.Zero],
                [Vector3.Zero],
                configuredContractionBound: 0.99f,
                relativeTolerance: 0.02f,
                observedContractionChannels:
                    new SimpleDdgiTransportRgbBounds(0.2f, 0.991f, 0.2f),
                canonicalQuantizationFloorChannels: default);

        Assert.That(estimate.HasValidContractionBound, Is.False);
        Assert.That(estimate.CanCertify, Is.False);
    }

    [Test]
    public void EvaluateTail_RejectsNonFiniteAndInvalidContraction()
    {
        SimpleDdgiTransportTailEstimator.TailEstimate nonFinite =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                [new(float.NaN, 0.0f, 0.0f)],
                [Vector3.Zero],
                0.9f,
                0.025f);
        SimpleDdgiTransportTailEstimator.TailEstimate invalidQ =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                [Vector3.Zero],
                [Vector3.Zero],
                1.0f,
                0.025f);

        Assert.That(nonFinite.IsFinite, Is.False);
        Assert.That(invalidQ.HasValidContractionBound, Is.False);
        Assert.That(invalidQ.CanCertify, Is.False);

        SimpleDdgiTransportTailEstimator.TailEstimate tooLooseQ =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                [Vector3.Zero],
                [Vector3.Zero],
                configuredContractionBound: 0.995f,
                relativeTolerance: 0.025f);
        Assert.That(tooLooseQ.HasValidContractionBound, Is.False);
    }

    [Test]
    public void EvaluateTail_QuantizationFloorRemainsPending()
    {
        SimpleDdgiTransportTailEstimator.TailEstimate estimate =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                [new(1.0f, 1.0f, 1.0f)],
                [new(1.0f, 1.0f, 1.0f)],
                configuredContractionBound: 0.5f,
                relativeTolerance: 0.10f,
                canonicalQuantizationFloor: 0.5f);

        Assert.That(estimate.QuantizationLimited, Is.True);
        Assert.That(estimate.IsWithinTolerance, Is.True);
        Assert.That(estimate.CanCertify, Is.False);

        SimpleDdgiTransportTailEstimator.TailEstimate exactBlack =
            SimpleDdgiTransportTailEstimator.EvaluateTail(
                [Vector3.Zero],
                [Vector3.Zero],
                configuredContractionBound: 0.5f,
                relativeTolerance: 0.10f,
                canonicalQuantizationFloor: 0.00005f);
        Assert.That(exactBlack.QuantizationLimited, Is.False);
        Assert.That(exactBlack.CanCertify, Is.True);
    }

    [Test]
    public void NormalizeThroughput_ScalesCombinedLobesWithoutChangingRatio()
    {
        bool success = SimpleDdgiTransportTailEstimator.TryNormalizeRecursiveThroughput(
            reflected: new(0.8f, 0.4f, 0.0f),
            transmitted: new(0.8f, 0.0f, 0.0f),
            contractionCeiling: 0.99f,
            transmissionEnabled: true,
            out SimpleDdgiTransportTailEstimator.ThroughputNormalization result);

        Assert.That(success, Is.True);
        Assert.That(result.WasRenormalized, Is.True);
        Assert.That(result.Reflected.X / result.Transmitted.X, Is.EqualTo(1.0f).Within(1e-6f));
        Assert.That(result.Reflected.Y / result.Reflected.X, Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(
            SimpleDdgiTransportTailEstimator.MaxComponent(result.Reflected + result.Transmitted),
            Is.EqualTo(0.99f).Within(1e-6f));
    }

    [Test]
    public void NormalizeThroughput_RejectsContractionAboveCertifiedCeiling()
    {
        bool success = SimpleDdgiTransportTailEstimator.TryNormalizeRecursiveThroughput(
            reflected: new(0.2f, 0.2f, 0.2f),
            transmitted: Vector3.Zero,
            contractionCeiling: 0.995f,
            transmissionEnabled: false,
            out _);

        Assert.That(success, Is.False);
    }

    [Test]
    public void PositiveEstimator_NormalizesCosineMass()
    {
        bool success = SimpleDdgiTransportTailEstimator.TryEvaluatePositiveIrradiance(
            [new SimpleDdgiTransportTailEstimator.DirectionalSample(
                Vector3.UnitY,
                new(2.0f, 3.0f, 4.0f))],
            Vector3.UnitY,
            out Vector3 irradiance);

        Assert.That(success, Is.True);
        Assert.That(irradiance.X, Is.EqualTo(2.0f * MathF.PI).Within(1e-5f));
        Assert.That(irradiance.Y, Is.EqualTo(3.0f * MathF.PI).Within(1e-5f));
        Assert.That(irradiance.Z, Is.EqualTo(4.0f * MathF.PI).Within(1e-5f));
    }

    [Test]
    public void Controller_RequiresCompleteFrozenAuditBeforeCertification()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 2), Is.True);
        SimpleDdgiTransportGenerations solveGenerations = controller.FrozenGenerations;
        Assert.That(controller.MarkParticipantVisited(0, solveGenerations), Is.True);
        Assert.That(controller.TryBeginAudit(solveGenerations), Is.False);
        Assert.That(controller.MarkParticipantVisited(1, solveGenerations), Is.True);
        Assert.That(controller.IsSolveEpochComplete, Is.True);
        Assert.That(controller.TryBeginAudit(solveGenerations), Is.True);
        Assert.That(controller.FrozenGenerations.Audit, Is.EqualTo(controller.AuditEpoch));
        Assert.That(controller.LastSummary.Generations, Is.EqualTo(controller.FrozenGenerations));

        SimpleDdgiTransportTailSummary summary = new()
        {
            AuditEpoch = controller.AuditEpoch,
            Generations = controller.FrozenGenerations,
            ExpectedParticipantCount = 2,
            AuditedParticipantCount = 2,
            ExpectedTexelCount = 4,
            AuditedTexelCount = 4,
            FixedPointDefect = 0.00001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.9f,
            ObservedContractionBound = 0.9f,
            CertifiedContractionBound = 0.9f,
            AbsoluteTailBound = 0.0001f,
            RelativeTailBound = 0.0001f,
            Tolerance = 0.025f,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.Certified
        };

        Assert.That(controller.TryAcceptAudit(summary, controller.FrozenGenerations), Is.True);
        Assert.That(controller.IsCertified, Is.True);
        Assert.That(controller.Phase, Is.EqualTo(SimpleDdgiTransportPhase.Certified));
        Assert.That(controller.TryBeginAudit(controller.FrozenGenerations), Is.False);
        Assert.That(
            controller.LastReason,
            Is.EqualTo(SimpleDdgiTransportCertificationReason.Certified),
            "An idle render-pass poll must not replace a current certificate's reason.");
    }

    [Test]
    public void Controller_GpuEpochWitnessRequiresExactParticipantCoverage()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 2), Is.True);
        SimpleDdgiTransportGenerations frozen = controller.FrozenGenerations;

        Assert.That(
            controller.MarkGpuEpochComplete(
                controller.SolveEpoch,
                participantCount: 1,
                generations: frozen),
            Is.False);
        Assert.That(controller.IsSolveEpochComplete, Is.False);

        Assert.That(
            controller.MarkGpuEpochComplete(
                controller.SolveEpoch,
                participantCount: 2,
                generations: frozen),
            Is.True);
        Assert.That(controller.IsSolveEpochComplete, Is.True);
        Assert.That(
            controller.MarkGpuEpochComplete(
                controller.SolveEpoch,
                participantCount: 2,
                generations: frozen with { CanonicalField = 99u }),
            Is.False);
        Assert.That(
            controller.LastReason,
            Is.EqualTo(SimpleDdgiTransportCertificationReason.GenerationsChanged));
    }

    [Test]
    public void Controller_FirstCurrentGpuReductionBindsProvisionalParticipantCount()
    {
        var controller = new SimpleDdgiTransportSolveController();
        controller.EnsureParticipantCapacity(16);
        Assert.That(
            controller.BeginSolveEpoch(Generations, expectedParticipantCount: 0),
            Is.True);
        SimpleDdgiTransportGenerations frozen = controller.FrozenGenerations;

        Assert.Multiple(() =>
        {
            Assert.That(
                controller.TryBindGpuEpochParticipantCount(
                    controller.SolveEpoch,
                    participantCount: 12,
                    generations: frozen),
                Is.True);
            Assert.That(controller.ExpectedParticipantCount, Is.EqualTo(12));
            Assert.That(
                controller.MarkGpuEpochComplete(
                    controller.SolveEpoch,
                    participantCount: 12,
                    generations: frozen),
                Is.True);
            Assert.That(controller.IsSolveEpochComplete, Is.True);
        });
    }

    [Test]
    public void Controller_DoesNotRebindGpuParticipantCountAfterVisitWitness()
    {
        var controller = new SimpleDdgiTransportSolveController();
        controller.EnsureParticipantCapacity(16);
        Assert.That(controller.BeginSolveEpoch(Generations, 12), Is.True);
        SimpleDdgiTransportGenerations frozen = controller.FrozenGenerations;
        Assert.That(
            controller.MarkGpuEpochComplete(
                controller.SolveEpoch,
                participantCount: 12,
                generations: frozen),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(
                controller.TryBindGpuEpochParticipantCount(
                    controller.SolveEpoch,
                    participantCount: 11,
                    generations: frozen),
                Is.False);
            Assert.That(controller.ExpectedParticipantCount, Is.EqualTo(12));
        });
    }

    [Test]
    public void Controller_CancelsAuditWhenGenerationsChange()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 1), Is.True);
        SimpleDdgiTransportGenerations solveGenerations = controller.FrozenGenerations;
        Assert.That(controller.MarkParticipantVisited(0, solveGenerations), Is.True);
        Assert.That(controller.TryBeginAudit(solveGenerations), Is.True);

        SimpleDdgiTransportGenerations changed = Generations with { CanonicalField = 99u };
        Assert.That(controller.TryAcceptAudit(
            new SimpleDdgiTransportTailSummary
            {
                AuditEpoch = controller.AuditEpoch,
                Generations = controller.FrozenGenerations,
                ExpectedParticipantCount = 1,
                AuditedParticipantCount = 1,
                ExpectedTexelCount = 1,
                AuditedTexelCount = 1,
                IsComplete = true,
                Reason = SimpleDdgiTransportCertificationReason.Certified
            },
            changed), Is.False);
        Assert.That(controller.IsCertified, Is.False);
        Assert.That(controller.LastReason, Is.EqualTo(SimpleDdgiTransportCertificationReason.GenerationsChanged));
    }

    [Test]
    public void AuditReadback_RejectsMutationOfEveryFrozenGeneration()
    {
        for (int field = 0; field < 10; field++)
        {
            SimpleDdgiTransportSolveController controller =
                CreateAuditingController(participantCount: 1);
            SimpleDdgiTransportTailSummary summary = CreateFiniteAuditSummary(
                controller,
                expectedParticipants: 1,
                auditedParticipants: 1,
                reason: SimpleDdgiTransportCertificationReason.Certified);
            SimpleDdgiTransportGenerations current = controller.FrozenGenerations;
            SimpleDdgiTransportGenerations changed = field switch
            {
                0 => current with { VolumeTable = current.VolumeTable + 1u },
                1 => current with { PhysicalOwnership = current.PhysicalOwnership + 1u },
                2 => current with { SourceLighting = current.SourceLighting + 1u },
                3 => current with { SourceEpoch = current.SourceEpoch + 1u },
                4 => current with { TransportOperator = current.TransportOperator + 1u },
                5 => current with { CanonicalField = current.CanonicalField + 1u },
                6 => current with { Solve = current.Solve + 1u },
                7 => current with { Audit = current.Audit + 1u },
                8 => current with { Queue = current.Queue + 1u },
                _ => current with
                {
                    SchedulerResources = current.SchedulerResources + 1u
                }
            };

            Assert.Multiple(() =>
            {
                Assert.That(controller.TryAcceptAudit(summary, changed), Is.False,
                    $"generation field {field}");
                Assert.That(controller.Phase,
                    Is.Not.EqualTo(SimpleDdgiTransportPhase.AuditFrozen),
                    $"generation field {field}");
                Assert.That(controller.LastReason,
                    Is.EqualTo(SimpleDdgiTransportCertificationReason.GenerationsChanged),
                    $"generation field {field}");
            });
        }
    }

    [Test]
    public void Controller_TailAboveToleranceStartsANewFullyVisitedSolveEpoch()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 2), Is.True);
        SimpleDdgiTransportGenerations firstSolve = controller.FrozenGenerations;
        Assert.That(controller.MarkParticipantVisited(0, firstSolve), Is.True);
        Assert.That(controller.MarkParticipantVisited(1, firstSolve), Is.True);
        Assert.That(controller.TryBeginAudit(firstSolve), Is.True);
        uint rejectedSolveEpoch = controller.SolveEpoch;
        SimpleDdgiTransportGenerations audited = controller.FrozenGenerations;

        SimpleDdgiTransportTailSummary rejected = new()
        {
            AuditEpoch = controller.AuditEpoch,
            Generations = audited,
            ExpectedParticipantCount = 2u,
            AuditedParticipantCount = 2u,
            ExpectedTexelCount = 128u,
            AuditedTexelCount = 128u,
            FixedPointDefect = 0.1f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.9f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.2f,
            RelativeTailBound = 0.2f,
            Tolerance = 0.025f,
            CanonicalQuantizationFloor = 0.001f,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.TailAboveTolerance
        };

        Assert.That(controller.TryAcceptAudit(rejected, audited), Is.False);
        SimpleDdgiTransportGenerations secondSolve = controller.FrozenGenerations;
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase,
                Is.EqualTo(SimpleDdgiTransportPhase.AcceleratedSolve));
            Assert.That(controller.LastReason,
                Is.EqualTo(SimpleDdgiTransportCertificationReason.TailAboveTolerance));
            Assert.That(controller.LastSummary, Is.EqualTo(rejected));
            Assert.That(controller.SolveEpoch, Is.Not.EqualTo(rejectedSolveEpoch));
            Assert.That(controller.ExpectedParticipantCount, Is.EqualTo(2));
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
            Assert.That(controller.IsSolveEpochComplete, Is.False);
        });

        Assert.That(controller.MarkParticipantVisited(0, secondSolve), Is.True);
        Assert.That(controller.MarkParticipantVisited(1, secondSolve), Is.True);
        Assert.That(controller.IsSolveEpochComplete, Is.True);
    }

    [Test]
    public void CoverageFailure_ClearsWitnessAndCannotReauditSameTuple()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 2), Is.True);
        SimpleDdgiTransportGenerations solve = controller.FrozenGenerations;
        Assert.That(controller.MarkParticipantVisited(0, solve), Is.True);
        Assert.That(controller.MarkParticipantVisited(1, solve), Is.True);
        Assert.That(controller.TryBeginAudit(solve), Is.True);

        SimpleDdgiTransportTailSummary incomplete = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 2u,
            auditedParticipants: 1u,
            reason: SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete);

        Assert.That(
            controller.TryAcceptAudit(incomplete, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(
                SimpleDdgiTransportPhase.ParticipantReconciliation));
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.ReconcileParticipants));
            Assert.That(controller.ExpectedParticipantCount, Is.Zero);
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
            Assert.That(controller.IsSolveEpochComplete, Is.False);
            Assert.That(controller.TryBeginAudit(controller.FrozenGenerations), Is.False);
            Assert.That(controller.RecoveryCount, Is.EqualTo(1u));
        });
    }

    [Test]
    public void InvalidCacheFailure_EntersSourceRepairAndClearsWitness()
    {
        var controller = CreateAuditingController(participantCount: 1);
        SimpleDdgiTransportTailSummary invalidCache = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 1u,
            auditedParticipants: 0u,
            reason: SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete) with
        {
            ExcludedInvalidCacheCount = 1u,
            CacheIdentityFailureCount = 1u
        };

        Assert.That(
            controller.TryAcceptAudit(invalidCache, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(SimpleDdgiTransportPhase.SourceRepair));
            Assert.That(controller.LastReason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.InvalidCache));
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.RepairSourceCache));
            Assert.That(controller.ExpectedParticipantCount, Is.Zero);
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
        });
    }

    [Test]
    public void AboveTolerance_AdvancesSolveEpochBeforeNextAudit()
    {
        var controller = CreateAuditingController(participantCount: 1);
        uint rejectedSolveEpoch = controller.SolveEpoch;
        SimpleDdgiTransportTailSummary aboveTolerance = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 1u,
            auditedParticipants: 1u,
            reason: SimpleDdgiTransportCertificationReason.TailAboveTolerance) with
        {
            FixedPointDefect = 0.1f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.2f,
            RelativeTailBound = 0.2f
        };

        Assert.That(
            controller.TryAcceptAudit(aboveTolerance, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.SolveEpoch, Is.Not.EqualTo(rejectedSolveEpoch));
            Assert.That(controller.Phase, Is.EqualTo(SimpleDdgiTransportPhase.AcceleratedSolve));
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch));
        });
    }

    [Test]
    public void NonFiniteAudit_EntersFailClosedRecovery()
    {
        var controller = CreateAuditingController(participantCount: 1);
        SimpleDdgiTransportTailSummary nonFinite = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 1u,
            auditedParticipants: 1u,
            reason: SimpleDdgiTransportCertificationReason.NonFiniteEvidence) with
        {
            FixedPointDefect = float.NaN,
            NonFiniteCount = 1u
        };

        Assert.That(
            controller.TryAcceptAudit(nonFinite, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(
                SimpleDdgiTransportPhase.FailClosedRecovery));
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.RebuildPrivateField));
            Assert.That(controller.RecoveryGeneration, Is.Not.Zero);
            Assert.That(controller.ExpectedParticipantCount, Is.Zero);
        });
    }

    [Test]
    public void ConvergenceDeadline_EntersFreshPrivateFieldRecovery()
    {
        var controller = CreateAuditingController(participantCount: 2);
        uint recoveryGeneration = controller.RecoveryGeneration;

        controller.EnterConvergenceDeadlineRecovery(controller.FrozenGenerations);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(
                SimpleDdgiTransportPhase.FailClosedRecovery));
            Assert.That(controller.LastReason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.ConvergenceDeadlineExceeded));
            Assert.That(controller.LastSummary.Reason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.ConvergenceDeadlineExceeded));
            Assert.That(controller.LastSummary.IsComplete, Is.False);
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.RebuildPrivateField));
            Assert.That(controller.RecoveryGeneration, Is.Not.EqualTo(recoveryGeneration));
            Assert.That(controller.ExpectedParticipantCount, Is.Zero);
            Assert.That(controller.VisitedParticipantCount, Is.Zero);
            Assert.That(controller.CompletedAuditPending, Is.False);
        });
    }

    [Test]
    public void QuantizationLimited_ReportsUnsupportedFloorWithoutSpinning()
    {
        var controller = CreateAuditingController(participantCount: 1);
        SimpleDdgiTransportTailSummary limited = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 1u,
            auditedParticipants: 1u,
            reason: SimpleDdgiTransportCertificationReason.QuantizationLimited) with
        {
            CanonicalQuantizationFloor = 0.05f,
            Tolerance = 0.025f
        };

        Assert.That(
            controller.TryAcceptAudit(limited, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(
                SimpleDdgiTransportPhase.UnsupportedTolerance));
            Assert.That(controller.RecoveryAction, Is.EqualTo(
                SimpleDdgiTransportRecoveryAction.ReportUnsupportedTolerance));
            Assert.That(controller.TryBeginAudit(controller.FrozenGenerations), Is.False);
        });
    }

    [Test]
    public void CompleteAudit_LeavesFrozenPhaseWithinReadbackDeadline()
    {
        var controller = CreateAuditingController(participantCount: 1);
        int deadline = SimpleDdgiTransportSolveController.ResolveAuditReadbackDeadlineFrames(
            probeCount: 5_787,
            chunkSize: 256,
            framesInFlight: RenderingConstants.FramesInFlight,
            readbackMargin: 2);
        Assert.That(deadline, Is.EqualTo(
            23 + RenderingConstants.FramesInFlight + 2));

        for (int age = 0; age < deadline; age++)
            controller.ObserveProgressFrame(madeProgress: age == 0);
        Assert.That(controller.Phase, Is.EqualTo(SimpleDdgiTransportPhase.AuditFrozen));

        Assert.That(controller.ExpireAudit(controller.FrozenGenerations), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Phase, Is.EqualTo(SimpleDdgiTransportPhase.AcceleratedSolve));
            Assert.That(controller.LastReason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.AuditReadbackTimeout));
            Assert.That(controller.IsSolveEpochComplete, Is.False);
            Assert.That(controller.NoProgressFrames, Is.EqualTo(deadline - 1));
        });
    }

    [Test]
    public void CompletedAuditSummary_MustBeConsumedBeforeAnyOtherTransition()
    {
        var controller = CreateAuditingController(participantCount: 1);
        SimpleDdgiTransportTailSummary summary = CreateFiniteAuditSummary(
            controller,
            expectedParticipants: 1u,
            auditedParticipants: 1u,
            reason: SimpleDdgiTransportCertificationReason.Certified);

        Assert.That(
            controller.TryStageCompletedAudit(summary, controller.FrozenGenerations),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(controller.CompletedAuditPending, Is.True);
            Assert.That(controller.TryBeginAudit(controller.FrozenGenerations), Is.False);
            Assert.That(controller.LastReason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.CompletedAuditUnconsumed));
        });
        Assert.That(controller.TryConsumeCompletedAudit(out bool accepted), Is.True);
        Assert.That(accepted, Is.True);
        Assert.That(controller.CompletedAuditPending, Is.False);
    }

    [Test]
    public void StateMachineModel_OneThousandFramesCannotRemainInTerminalFrozenLoop()
    {
        var controller = new SimpleDdgiTransportSolveController();
        SimpleDdgiTransportGenerations generations = Generations;
        int frozenAge = 0;
        int deadline = SimpleDdgiTransportSolveController.ResolveAuditReadbackDeadlineFrames(
            probeCount: 1,
            chunkSize: 256,
            framesInFlight: 3,
            readbackMargin: 2);

        for (int frame = 0; frame < 1_000; frame++)
        {
            if (controller.Phase is SimpleDdgiTransportPhase.SourceRepair or
                SimpleDdgiTransportPhase.ParticipantReconciliation or
                SimpleDdgiTransportPhase.FailClosedRecovery)
            {
                Assert.That(controller.BeginSolveEpoch(generations, 1), Is.True);
            }
            if (controller.Phase == SimpleDdgiTransportPhase.AcceleratedSolve)
            {
                SimpleDdgiTransportGenerations solve = controller.FrozenGenerations;
                if (!controller.IsSolveEpochComplete)
                    Assert.That(controller.MarkParticipantVisited(0, solve), Is.True);
                Assert.That(controller.TryBeginAudit(solve), Is.True);
                frozenAge = 0;
            }
            else if (controller.Phase == SimpleDdgiTransportPhase.AuditFrozen)
            {
                frozenAge++;
                if (frozenAge > deadline)
                {
                    Assert.That(controller.ExpireAudit(controller.FrozenGenerations), Is.True);
                    frozenAge = 0;
                }
            }
        }

        Assert.That(frozenAge, Is.LessThanOrEqualTo(deadline));
        Assert.That(controller.RecoveryCount, Is.GreaterThan(0u));
    }

    [Test]
    public void Controller_PreservesExplicitAuditFailureReason()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, 1), Is.True);
        SimpleDdgiTransportGenerations solve = controller.FrozenGenerations;
        Assert.That(controller.MarkParticipantVisited(0, solve), Is.True);
        Assert.That(controller.TryBeginAudit(solve), Is.True);

        SimpleDdgiTransportTailSummary rejected = new()
        {
            AuditEpoch = controller.AuditEpoch,
            Generations = controller.FrozenGenerations,
            ExpectedParticipantCount = 1u,
            AuditedParticipantCount = 1u,
            ExpectedTexelCount = 64u,
            AuditedTexelCount = 64u,
            FixedPointDefect = 0.001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.9f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.002f,
            RelativeTailBound = 0.002f,
            Tolerance = 0.025f,
            CanonicalQuantizationFloor = 0.001f,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete
        };

        Assert.That(
            controller.TryAcceptAudit(rejected, controller.FrozenGenerations),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(controller.LastReason, Is.EqualTo(
                SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete));
            Assert.That(controller.LastSummary, Is.EqualTo(rejected));
            Assert.That(controller.Phase, Is.EqualTo(
                SimpleDdgiTransportPhase.ParticipantReconciliation));
        });
    }

    [Test]
    public void Controller_AllowsExplicitEmptyFieldCertificate()
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, expectedParticipantCount: 0), Is.True);
        SimpleDdgiTransportGenerations solveGenerations = controller.FrozenGenerations;

        Assert.That(controller.TryBeginAudit(solveGenerations), Is.True);

        SimpleDdgiTransportTailSummary summary = new()
        {
            AuditEpoch = controller.AuditEpoch,
            Generations = controller.FrozenGenerations,
            ExpectedParticipantCount = 0u,
            AuditedParticipantCount = 0u,
            ExpectedTexelCount = 0u,
            AuditedTexelCount = 0u,
            FixedPointDefect = 0.0f,
            FieldMagnitude = 0.0f,
            ConfiguredContractionBound = 0.5f,
            ObservedContractionBound = 0.0f,
            CertifiedContractionBound = 0.0f,
            AbsoluteTailBound = 0.0f,
            RelativeTailBound = 0.0f,
            Tolerance = 0.0001f,
            CanonicalQuantizationFloor = 0.0f,
            IsComplete = true,
            Reason = SimpleDdgiTransportCertificationReason.Certified
        };

        Assert.That(controller.TryAcceptAudit(summary, controller.FrozenGenerations), Is.True);
        Assert.That(controller.IsCertified, Is.True);
    }

    [Test]
    public void AuditAccumulator_RequiresOrderedCompleteChunksAndRecomputesTail()
    {
        var accumulator = new SimpleDdgiTransportAuditAccumulator(
            auditEpoch: 3u,
            generations: Generations,
            expectedParticipantCount: 2u,
            expectedTexelCount: 8u,
            configuredContractionBound: 0.9f,
            relativeTolerance: 0.025f,
            canonicalQuantizationFloor: 0.0f,
            firstFrameSerial: 100u,
            expectedChunkCount: 2u);

        SimpleDdgiTransportAuditChunk secondChunk = new()
        {
            AuditEpoch = 3u,
            Generations = Generations,
            ChunkIndex = 1u,
            ExpectedChunkCount = 2u,
            ExpectedParticipantCount = 2u,
            ExpectedTexelCount = 8u
        };
        Assert.That(accumulator.TryAddChunk(secondChunk), Is.False);

        Assert.That(accumulator.TryAddChunk(new SimpleDdgiTransportAuditChunk
        {
            AuditEpoch = 3u,
            Generations = Generations,
            ChunkIndex = 0u,
            ExpectedChunkCount = 2u,
            ExpectedParticipantCount = 2u,
            ExpectedTexelCount = 8u,
            AuditedParticipantCount = 1u,
            AuditedTexelCount = 4u,
            FixedPointDefect = 0.00001f,
            FieldMagnitude = 1.0f,
            ObservedContractionBound = 0.9f,
            AuditMilliseconds = 1u,
            FinalFrameSerial = 100u
        }), Is.True);
        Assert.That(accumulator.TryAddChunk(new SimpleDdgiTransportAuditChunk
        {
            AuditEpoch = 3u,
            Generations = Generations,
            ChunkIndex = 1u,
            ExpectedChunkCount = 2u,
            ExpectedParticipantCount = 2u,
            ExpectedTexelCount = 8u,
            AuditedParticipantCount = 1u,
            AuditedTexelCount = 4u,
            FixedPointDefect = 0.00002f,
            FieldMagnitude = 1.0f,
            ObservedContractionBound = 0.9f,
            AuditMilliseconds = 2u,
            FinalFrameSerial = 101u
        }), Is.True);

        Assert.That(accumulator.TryFinalize(out SimpleDdgiTransportTailSummary summary), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Reason, Is.EqualTo(SimpleDdgiTransportCertificationReason.Certified));
            Assert.That(summary.FixedPointDefect, Is.EqualTo(0.00002f).Within(1e-8f));
            Assert.That(summary.AbsoluteTailBound, Is.EqualTo(0.0002f).Within(1e-7f));
            Assert.That(summary.AuditMicroseconds, Is.EqualTo(3000u));
            Assert.That(summary.ChunkCount, Is.EqualTo(2u));
        });
    }

    [Test]
    public void AuditAccumulator_PreservesPerChannelCertificateEvidence()
    {
        var accumulator = new SimpleDdgiTransportAuditAccumulator(
            auditEpoch: 5u,
            generations: Generations,
            expectedParticipantCount: 1u,
            expectedTexelCount: 1u,
            configuredContractionBound: 0.9f,
            relativeTolerance: 0.02f,
            canonicalQuantizationFloor: 0.0f,
            firstFrameSerial: 10u,
            expectedChunkCount: 1u);

        Assert.That(accumulator.TryAddChunk(new SimpleDdgiTransportAuditChunk
        {
            AuditEpoch = 5u,
            Generations = Generations,
            ChunkIndex = 0u,
            ExpectedChunkCount = 1u,
            ExpectedParticipantCount = 1u,
            ExpectedTexelCount = 1u,
            AuditedParticipantCount = 1u,
            AuditedTexelCount = 1u,
            FixedPointDefect = 0.01f,
            FieldMagnitude = 1.0f,
            ObservedContractionBound = 0.9f,
            ChannelEvidenceVersion =
                SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion,
            FixedPointDefectChannels =
                new SimpleDdgiTransportRgbBounds(0.001f, 0.0f, 0.01f),
            FieldMagnitudeChannels =
                new SimpleDdgiTransportRgbBounds(1.0f, 1.0f, 1.0f),
            ObservedContractionChannels =
                new SimpleDdgiTransportRgbBounds(0.9f, 0.2f, 0.1f),
            FinalFrameSerial = 10u
        }), Is.True);

        Assert.That(accumulator.TryFinalize(
            out SimpleDdgiTransportTailSummary summary), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(summary.HasPerChannelEvidence, Is.True);
            Assert.That(summary.AbsoluteTailBound, Is.EqualTo(
                0.01f / 0.9f).Within(1e-6f));
            Assert.That(summary.IsCertified, Is.True);
        });
    }

    [Test]
    public void AuditAccumulator_RejectsStaleChunkAndInvalidCoverage()
    {
        var accumulator = new SimpleDdgiTransportAuditAccumulator(
            auditEpoch: 1u,
            generations: Generations,
            expectedParticipantCount: 1u,
            expectedTexelCount: 4u,
            configuredContractionBound: 0.5f,
            relativeTolerance: 0.025f,
            canonicalQuantizationFloor: 0.0f,
            firstFrameSerial: 1u,
            expectedChunkCount: 1u);

        Assert.That(accumulator.TryAddChunk(new SimpleDdgiTransportAuditChunk
        {
            AuditEpoch = 2u,
            Generations = Generations,
            ChunkIndex = 0u,
            ExpectedChunkCount = 1u,
            ExpectedParticipantCount = 1u,
            ExpectedTexelCount = 4u
        }), Is.False);

        Assert.That(accumulator.TryAddChunk(new SimpleDdgiTransportAuditChunk
        {
            AuditEpoch = 1u,
            Generations = Generations,
            ChunkIndex = 0u,
            ExpectedChunkCount = 1u,
            ExpectedParticipantCount = 1u,
            ExpectedTexelCount = 4u,
            AuditedParticipantCount = 0u,
            AuditedTexelCount = 3u,
            FixedPointDefect = 0.0f,
            FieldMagnitude = 0.0f,
            ObservedContractionBound = 0.5f
        }), Is.True);

        Assert.That(accumulator.TryFinalize(out SimpleDdgiTransportTailSummary summary), Is.False);
        Assert.That(summary.Reason, Is.EqualTo(SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete));
    }

    [Test]
    public void AuditAccumulator_FailsClosedOnCounterOverflow()
    {
        var accumulator = new SimpleDdgiTransportAuditAccumulator(
            auditEpoch: 1u,
            generations: Generations,
            expectedParticipantCount: uint.MaxValue,
            expectedTexelCount: 0u,
            configuredContractionBound: 0.5f,
            relativeTolerance: 0.025f,
            canonicalQuantizationFloor: 0.0f,
            firstFrameSerial: 1u,
            expectedChunkCount: 2u);

        SimpleDdgiTransportAuditChunk chunk = new()
        {
            AuditEpoch = 1u,
            Generations = Generations,
            ChunkIndex = 0u,
            ExpectedChunkCount = 2u,
            ExpectedParticipantCount = uint.MaxValue,
            ExpectedTexelCount = 0u,
            AuditedParticipantCount = uint.MaxValue,
            FixedPointDefect = 0.0f,
            FieldMagnitude = 0.0f,
            ObservedContractionBound = 0.5f
        };

        Assert.That(accumulator.TryAddChunk(chunk), Is.True);
        Assert.That(accumulator.TryAddChunk(chunk with
        {
            ChunkIndex = 1u,
            AuditedParticipantCount = 1u
        }), Is.False);
        Assert.That(accumulator.TryFinalize(out SimpleDdgiTransportTailSummary summary), Is.False);
        Assert.That(summary.CounterOverflowCount, Is.EqualTo(1u));
        Assert.That(summary.Reason, Is.EqualTo(SimpleDdgiTransportCertificationReason.CounterOverflow));
    }

    [Test]
    public void LogicalParity_UsesToroidalCoordinate()
    {
        int physicalParity = (0 + 0 + 0) & 1;
        int logicalParity = SimpleDdgiTransportSolveController.ResolveLogicalParity(
            localProbeIndex: 0,
            gridCountX: 4,
            gridCountY: 4,
            gridCountZ: 4,
            physicalOffsetX: 1,
            physicalOffsetY: 0,
            physicalOffsetZ: 0);

        Assert.That(physicalParity, Is.EqualTo(0));
        Assert.That(logicalParity, Is.EqualTo(1));
    }

    [Test]
    public void VolumeOrder_IsCoarseFirstWithStableTieBreakers()
    {
        SimpleDdgiTransportVolumeOrderKey[] keys =
        [
            new(VolumeIndex: 2, Spacing: 1.0f, FallbackPriority: 1),
            new(VolumeIndex: 0, Spacing: 3.0f, FallbackPriority: 2),
            new(VolumeIndex: 1, Spacing: 3.0f, FallbackPriority: 0)
        ];
        int[] ordered = new int[keys.Length];

        SimpleDdgiTransportSolveController.OrderVolumes(keys, ordered);

        Assert.That(ordered, Is.EqualTo(new[] { 1, 0, 2 }));
    }

    private static SimpleDdgiTransportSolveController CreateAuditingController(
        int participantCount)
    {
        var controller = new SimpleDdgiTransportSolveController();
        Assert.That(controller.BeginSolveEpoch(Generations, participantCount), Is.True);
        SimpleDdgiTransportGenerations solve = controller.FrozenGenerations;
        for (int participant = 0; participant < participantCount; participant++)
            Assert.That(controller.MarkParticipantVisited(participant, solve), Is.True);
        Assert.That(controller.TryBeginAudit(solve), Is.True);
        return controller;
    }

    private static SimpleDdgiTransportTailSummary CreateFiniteAuditSummary(
        SimpleDdgiTransportSolveController controller,
        uint expectedParticipants,
        uint auditedParticipants,
        SimpleDdgiTransportCertificationReason reason)
    {
        uint expectedTexels = checked(expectedParticipants * 64u);
        uint auditedTexels = checked(auditedParticipants * 64u);
        return new SimpleDdgiTransportTailSummary
        {
            AuditEpoch = controller.AuditEpoch,
            Generations = controller.FrozenGenerations,
            ExpectedParticipantCount = expectedParticipants,
            AuditedParticipantCount = auditedParticipants,
            ExpectedTexelCount = expectedTexels,
            AuditedTexelCount = auditedTexels,
            FixedPointDefect = 0.00001f,
            FieldMagnitude = 1.0f,
            ConfiguredContractionBound = 0.9f,
            ObservedContractionBound = 0.5f,
            CertifiedContractionBound = 0.5f,
            AbsoluteTailBound = 0.00002f,
            RelativeTailBound = 0.00002f,
            Tolerance = 0.025f,
            CanonicalQuantizationFloor = 0.001f,
            IsComplete = true,
            Reason = reason
        };
    }
}
