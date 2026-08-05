using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
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
}
