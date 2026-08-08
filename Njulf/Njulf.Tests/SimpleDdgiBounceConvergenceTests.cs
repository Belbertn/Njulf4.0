using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiBounceConvergenceTests
{
    private const float ResidualThreshold = 0.025f;

    [TestCase(0.2f)]
    [TestCase(0.5f)]
    [TestCase(0.8f)]
    public void WhiteEnclosureOracle_ConvergesToAnalyticBounceRatio(float albedo)
    {
        Vector3 sourceRadiance = Vector3.One;
        Vector3 reflectance = new(albedo);
        Vector3 totalRadiance = sourceRadiance;

        // Mirrors blend's radiance-to-irradiance PI and the canonical material
        // evaluator's single irradiance-to-diffuse 1/PI conversion.
        for (int generation = 0; generation < 128; generation++)
        {
            Vector3 incidentIrradiance = totalRadiance * MathF.PI;
            Vector3 reflectedBounce =
                GiMaterialReferenceEvaluator.EvaluateDiffuseFromIrradiance(
                    incidentIrradiance,
                    reflectance);
            totalRadiance = sourceRadiance + reflectedBounce;
        }

        float measuredBounceToSource = totalRadiance.X - 1.0f;
        float expected =
            SampleGlobalIlluminationValidation.ExpectedWhiteEnclosureBounceToSourceRatio(albedo);
        Assert.That(measuredBounceToSource, Is.EqualTo(expected).Within(expected * 0.05f + 1e-5f));
    }

    [Test]
    public void FixedPointResidual_PreservesDimAndChromaticTransportChanges()
    {
        float dimGrowth = SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
            new Vector3(0.035f),
            new Vector3(0.030f),
            ResidualThreshold);
        float chromaticChange = SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
            new Vector3(0.0f, 0.2972f, 0.0f),
            new Vector3(1.0f, 0.0f, 0.0f),
            ResidualThreshold);

        Assert.Multiple(() =>
        {
            Assert.That(dimGrowth, Is.GreaterThan(ResidualThreshold));
            Assert.That(chromaticChange, Is.EqualTo(1.0f).Within(1e-6f));
        });
    }

    [Test]
    public void FixedPointResidual_UsesStrictAbsoluteToleranceOnlyNearBlack()
    {
        float belowAbsoluteTolerance =
            SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
                new Vector3(0.00005f),
                Vector3.Zero,
                ResidualThreshold);
        float aboveAbsoluteTolerance =
            SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
                new Vector3(0.00020f),
                Vector3.Zero,
                ResidualThreshold);
        float brightRelativeChange =
            SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
                new Vector3(1.02f),
                Vector3.One,
                ResidualThreshold);

        Assert.Multiple(() =>
        {
            Assert.That(belowAbsoluteTolerance, Is.LessThan(ResidualThreshold));
            Assert.That(aboveAbsoluteTolerance, Is.GreaterThan(ResidualThreshold));
            Assert.That(brightRelativeChange, Is.LessThan(ResidualThreshold));
        });
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void FixedPointResidual_NonFiniteInputFailsClosed(float invalid)
    {
        float residual = SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
            new Vector3(invalid, 0.0f, 0.0f),
            Vector3.Zero,
            ResidualThreshold);

        Assert.That(residual, Is.EqualTo(1.0f));
    }

    [Test]
    public void ResidualEnvelope_HasImmediateAttackAndBoundedRelease()
    {
        float attacked = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(0.0f, 0.05f);
        float release1 = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(1.0f, 0.0f);
        float release2 = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(release1, 0.0f);
        float release3 = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(release2, 0.0f);
        float repaired = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(float.NaN, 0.0f);

        Assert.Multiple(() =>
        {
            Assert.That(attacked, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(release1, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(release2, Is.EqualTo(0.0625f).Within(1e-6f));
            Assert.That(release3, Is.EqualTo(0.015625f).Within(1e-6f));
            Assert.That(repaired, Is.EqualTo(0.25f).Within(1e-6f));
        });
    }

    [Test]
    public void ResidualAggregation_DoesNotDiluteSparseDirectionalChange()
    {
        float[] residuals = new float[64];
        residuals[37] = 0.50f;

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.AggregateTransportConvergenceResiduals(residuals),
                Is.EqualTo(0.50f));
            residuals[12] = float.NaN;
            Assert.That(
                SimpleDdgiVolumeManager.AggregateTransportConvergenceResiduals(residuals),
                Is.EqualTo(1.0f));
        });
    }

    [TestCase(7, 8, 3, 3, 0.0f, false)]
    [TestCase(8, 8, 2, 3, 0.0f, false)]
    [TestCase(8, 8, 3, 3, 0.024f, true)]
    [TestCase(8, 8, 3, 3, 0.026f, false)]
    [TestCase(12, 8, 3, 3, 0.0f, true)]
    public void ConvergenceCriteria_RequireAllIndependentGates(
        int generations,
        int minimumGenerations,
        int stableUpdates,
        int requiredStableUpdates,
        float residual,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.MeetsTransportConvergenceCriteria(
                generations,
                minimumGenerations,
                stableUpdates,
                requiredStableUpdates,
                residual,
                ResidualThreshold),
            Is.EqualTo(expected));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void ConvergenceCriteria_NonFiniteStateCannotRetireAProbe(float invalid)
    {
        Assert.That(
            SimpleDdgiVolumeManager.MeetsTransportConvergenceCriteria(
                8,
                8,
                3,
                3,
                invalid,
                ResidualThreshold),
            Is.False);
    }

    [TestCase(true, true, 0u, 240, false, false, 0, 8, true)]
    [TestCase(false, true, 240u, 240, false, true, 8, 8, false)]
    [TestCase(false, true, 240u, 240, true, false, 8, 8, true)]
    [TestCase(false, true, 240u, 240, false, false, 127, 8, false)]
    [TestCase(false, true, 240u, 240, false, false, 128, 8, false)]
    [TestCase(false, false, 239u, 240, false, true, 8, 8, false)]
    [TestCase(false, false, 240u, 240, false, false, 127, 8, false)]
    [TestCase(false, false, 240u, 240, false, false, 128, 8, true)]
    [TestCase(false, false, 240u, 240, false, true, 8, 8, true)]
    public void PeriodicSourceRefresh_DefersForSolveButHasABoundedWatchdog(
        bool hardRefresh,
        bool globalPending,
        uint elapsed,
        int refreshFrames,
        bool periodicRefreshWaveMember,
        bool locallyConverged,
        int completedSolverGenerations,
        int minimumSolverGenerations,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldRefreshTransportSource(
                hardRefresh,
                globalPending,
                elapsed,
                refreshFrames,
                periodicRefreshWaveMember,
                locallyConverged,
                completedSolverGenerations,
                minimumSolverGenerations),
            Is.EqualTo(expected));
    }

    [TestCase(1, 16)]
    [TestCase(8, 128)]
    [TestCase(64, 255)]
    public void PeriodicSourceRefresh_WatchdogFitsTransportGenerationStorage(
        int minimumSolverGenerations,
        int expectedWatchdogGeneration)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportSourceRefreshWatchdogGeneration(
                minimumSolverGenerations),
            Is.EqualTo(expectedWatchdogGeneration));
    }

    [TestCase(true, false, false, 479u, 240, false)]
    [TestCase(true, false, false, 480u, 240, true)]
    [TestCase(true, false, true, 480u, 240, false)]
    [TestCase(true, true, false, 480u, 240, false)]
    [TestCase(false, false, false, 480u, 240, false)]
    public void GlobalSourceRefreshWatchdog_StartsAtMostOneCohortPerSolve(
        bool globalConvergencePending,
        bool periodicRefreshWavePending,
        bool watchdogWaveAlreadyStarted,
        uint globalSolveAgeFrames,
        int periodicRefreshFrames,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldStartTransportGlobalSourceRefreshWatchdogWave(
                globalConvergencePending,
                periodicRefreshWavePending,
                watchdogWaveAlreadyStarted,
                globalSolveAgeFrames,
                periodicRefreshFrames),
            Is.EqualTo(expected));
    }

    [TestCase(0u, 240u, 240, true)]
    [TestCase(1u, 240u, 240, false)]
    [TestCase(241u, 240u, 240, false)]
    [TestCase(uint.MaxValue - 8u, 8u, 16, true)]
    public void PeriodicSourceRefresh_WaveMembershipUsesAFixedWrapSafeCutoff(
        uint lastSourceRefreshFrame,
        uint cutoffFrame,
        int refreshFrames,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.IsTransportSourceRefreshDueAtCutoff(
                lastSourceRefreshFrame,
                cutoffFrame,
                refreshFrames),
            Is.EqualTo(expected));
    }

    [TestCase(true, 1, false)]
    [TestCase(true, 0, true)]
    [TestCase(false, 0, false)]
    public void GlobalConvergence_StartsEvidenceOnlyAfterSourceCohortDrains(
        bool sourceRepairPhasePending,
        int pendingSourceRepairs,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldStartTransportConvergenceEvidencePhase(
                sourceRepairPhasePending,
                pendingSourceRepairs),
            Is.EqualTo(expected));
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void GlobalConvergence_OnlyGenuineFieldBoundariesResetEvidence(
        bool forceFieldEvidenceReset,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldResetTransportFieldEvidence(
                forceFieldEvidenceReset),
            Is.EqualTo(expected));
    }

    [TestCase(false, false, true)]
    [TestCase(true, false, false)]
    [TestCase(true, true, true)]
    public void GlobalConvergence_StartsClockOncePerPropagationWave(
        bool globalConvergencePending,
        bool resetFieldEvidence,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldStartTransportConvergenceWave(
                globalConvergencePending,
                resetFieldEvidence),
            Is.EqualTo(expected));
    }

    [TestCase(true, true, true, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, true, true, true)]
    [TestCase(false, false, true, true)]
    public void PeriodicSourceRefresh_LocalRepairsPreserveAnActiveCohort(
        bool explicitlyPreserve,
        bool globalConvergencePending,
        bool resetFieldEvidence,
        bool expectedClear)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldClearTransportPeriodicSourceRefreshWave(
                explicitlyPreserve,
                globalConvergencePending,
                resetFieldEvidence),
            Is.EqualTo(expectedClear));
    }

    [Test]
    public void PeriodicSourceRefresh_MultiBatchCohortProducesOneEvidencePhase()
    {
        const int probeCount = 16;
        const int queueCapacity = 4;
        const uint cutoffFrame = 240u;
        const int refreshFrames = 240;
        uint[] lastRefreshFrames = new uint[probeCount];
        int batchCount = 0;

        while (true)
        {
            int[] due = Enumerable.Range(0, probeCount)
                .Where(probe => SimpleDdgiVolumeManager.IsTransportSourceRefreshDueAtCutoff(
                    lastRefreshFrames[probe],
                    cutoffFrame,
                    refreshFrames))
                .Take(queueCapacity)
                .ToArray();
            if (due.Length == 0)
                break;

            batchCount++;
            foreach (int probe in due)
                lastRefreshFrames[probe] = cutoffFrame + (uint)batchCount;

            Assert.That(
                SimpleDdgiVolumeManager.ShouldStartTransportConvergenceEvidencePhase(
                    true,
                    lastRefreshFrames.Count(frame =>
                        SimpleDdgiVolumeManager.IsTransportSourceRefreshDueAtCutoff(
                            frame,
                            cutoffFrame,
                            refreshFrames))),
                Is.EqualTo(batchCount == probeCount / queueCapacity));
        }

        Assert.Multiple(() =>
        {
            Assert.That(batchCount, Is.EqualTo(probeCount / queueCapacity));
            Assert.That(lastRefreshFrames, Has.All.GreaterThan(cutoffFrame));
        });
    }

    [TestCase(240, 15_368, 2_048, 15_368, 256, 543)]
    [TestCase(600, 15_368, 2_048, 15_368, 256, 1_253)]
    [TestCase(900, 15_368, 2_048, 15_368, 256, 1_777)]
    [TestCase(120, 1_024, 0, 1_024, 256, 233)]
    [TestCase(1, 1_024, 128, 1_024, 256, 28)]
    public void PeriodicSourceRefresh_ProvidesCompleteTailSolverOpportunity(
        int configuredFrames,
        int participantCount,
        int updateBudget,
        int probeCount,
        int auditChunkProbeCount,
        int expectedFrames)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveEffectiveTransportSourceRefreshFrames(
                configuredFrames,
                participantCount,
                updateBudget,
                probeCount,
                auditChunkProbeCount),
            Is.EqualTo(expectedFrames));
    }

    [Test]
    public void PeriodicSourceRefresh_V2IgnoresLegacyGenerationSettings()
    {
        static int Resolve() =>
            SimpleDdgiVolumeManager.ResolveEffectiveTransportSourceRefreshFrames(
                configuredRefreshFrames: 1,
                participantCount: 1_024,
                updateBudget: 128,
                probeCount: 1_024,
                auditChunkProbeCount: 256);

        int maximumSolverGenerations = 1;
        int stableMaintenanceUpdates = 1;
        int first = Resolve();
        maximumSolverGenerations = 64;
        stableMaintenanceUpdates = 16;
        int second = Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(28));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(maximumSolverGenerations, Is.EqualTo(64));
            Assert.That(stableMaintenanceUpdates, Is.EqualTo(16));
        });
    }

    [Test]
    public void PeriodicSourceRefresh_V2RespondsToOpportunityInputs()
    {
        static int Resolve(
            int configured = 1,
            int participants = 1_024,
            int budget = 128,
            int probes = 1_024,
            int auditChunk = 256) =>
            SimpleDdgiVolumeManager.ResolveEffectiveTransportSourceRefreshFrames(
                configured,
                participants,
                budget,
                probes,
                auditChunk);

        int baseline = Resolve();
        Assert.Multiple(() =>
        {
            Assert.That(baseline, Is.EqualTo(28));
            Assert.That(Resolve(participants: 2_048), Is.EqualTo(52));
            Assert.That(Resolve(budget: 256), Is.EqualTo(16));
            Assert.That(Resolve(auditChunk: 128), Is.EqualTo(32));
            Assert.That(Resolve(configured: 60), Is.EqualTo(126));
        });
    }

    [Test]
    public void PeriodicSourceRefresh_DenseCadenceIncludesAuthoredSweepAndTailOpportunity()
    {
        int sparse =
            SimpleDdgiVolumeManager.ResolveEffectiveTransportSourceRefreshFrames(
                configuredRefreshFrames: 480,
                participantCount: 5_822,
                updateBudget: 128,
                probeCount: 15_368,
                auditChunkProbeCount: 256);
        int dense =
            SimpleDdgiVolumeManager.ResolveEffectiveTransportSourceRefreshFrames(
                configuredRefreshFrames: 480,
                participantCount: 15_368,
                updateBudget: 128,
                probeCount: 15_368,
                auditChunkProbeCount: 256);

        Assert.Multiple(() =>
        {
            Assert.That(sparse, Is.EqualTo(480));
            Assert.That(dense, Is.EqualTo(1_114));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveTransportSourceSweepFrames(
                    configuredRefreshFrames: 480,
                    participantCount: 15_368,
                    updateBudget: 128,
                    probeCount: 15_368),
                Is.EqualTo(466));
        });
    }

    [Test]
    public void PeriodicSourceRefresh_UsesReadyPlusBlockingCohortDuringRepair()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager
                    .ResolveGpuResidentTransportRefreshParticipantCount(
                        sourceReadyParticipantCount: 4_006,
                        blockingSourceWorkCount: 9_240,
                        probeCount: 15_368),
                Is.EqualTo(13_246));
            Assert.That(
                SimpleDdgiVolumeManager
                    .ResolveGpuResidentTransportRefreshParticipantCount(
                        sourceReadyParticipantCount: 13_000,
                        blockingSourceWorkCount: 9_000,
                        probeCount: 15_368),
                Is.EqualTo(15_368));
        });
    }

    [Test]
    public void SourceGenerationChange_RebasesOnlyAnActiveConvergenceDeadline()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager
                    .ResolveTransportConvergenceStartFrameAfterSourceChange(
                        currentStartFrame: 49u,
                        sourceChangeFrame: 547u,
                        convergencePending: true),
                Is.EqualTo(547u));
            Assert.That(
                SimpleDdgiVolumeManager
                    .ResolveTransportConvergenceStartFrameAfterSourceChange(
                        currentStartFrame: 49u,
                        sourceChangeFrame: 547u,
                        convergencePending: false),
                Is.EqualTo(49u));
        });
    }

    [Test]
    public void ConvergenceDeadline_DenseStartupIncludesBoundedAdmissionMargin()
    {
        Assert.That(
            SimpleDdgiVolumeManager
                .ResolveTransportTailConvergenceDeadlineFrames(
                    sourceSweepFrames: 466,
                    probeCount: 15_368,
                    solveProbeBudgetPerFrame: 128,
                    acceleratedSweepCount: 2,
                    auditDeadlineFrames: 65,
                    framesInFlight: 3),
            Is.EqualTo(1_239));
    }

    [TestCase(true, 24.0f, 60.0f)]
    [TestCase(true, 144.0f, 60.0f)]
    [TestCase(false, 24.0f, 24.0f)]
    [TestCase(false, 144.0f, 144.0f)]
    public void SourceSweepFrameRate_IsNominalOnlyForDeterministicScheduling(
        bool deterministicFixedBudget,
        float observedFramesPerSecond,
        float expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveSourceSweepFramesPerSecond(
                deterministicFixedBudget,
                observedFramesPerSecond),
            Is.EqualTo(expected));
    }

    [TestCase(true, true, false, true)]
    [TestCase(true, true, true, false)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    public void SourceRefresh_WakesOnlyItsNeighborhoodAfterGlobalSettlement(
        bool transportV2,
        bool sourceRefresh,
        bool globalConvergencePending,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldWakeTransportPropagationNeighborhood(
                transportV2,
                sourceRefresh,
                globalConvergencePending),
            Is.EqualTo(expected));
    }

    [TestCase(true, 40, 40, true)]
    [TestCase(true, 41, 40, true)]
    [TestCase(true, 39, 40, false)]
    [TestCase(false, 2_048, 40, false)]
    [TestCase(true, -1, 0, true)]
    public void RoutineSourceRefresh_UsesThroughputTargetAsHardBackgroundCeiling(
        bool routineSourceRefresh,
        int scheduledSourceRefreshCount,
        int targetSourceRefreshCount,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldDeferRoutineSourceRefresh(
                routineSourceRefresh,
                scheduledSourceRefreshCount,
                targetSourceRefreshCount),
            Is.EqualTo(expected));
    }

    [TestCase(true, 0.0f, 0.025f, false)]
    [TestCase(true, 0.025f, 0.025f, false)]
    [TestCase(true, 0.0499f, 0.025f, false)]
    [TestCase(true, 0.0501f, 0.025f, true)]
    [TestCase(false, 0.0f, 0.025f, true)]
    [TestCase(true, float.PositiveInfinity, 0.025f, true)]
    [TestCase(true, -1.0f, 0.025f, true)]
    [TestCase(true, 0.0f, float.NaN, true)]
    public void RoutineSourceRefresh_PropagatesOnlyForMaterialOrInvalidResidual(
        bool residualEnvelopeValid,
        float residualEnvelope,
        float stableResidualThreshold,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldPropagateRoutineSourceRefresh(
                residualEnvelopeValid,
                residualEnvelope,
                stableResidualThreshold),
            Is.EqualTo(expected));
    }

    [TestCase(true, 54u, 54u, 54u, false, false)]
    [TestCase(true, 54u, 0u, 54u, false, false)]
    [TestCase(true, 0u, 54u, 54u, false, true)]
    [TestCase(true, 53u, 54u, 54u, false, true)]
    [TestCase(true, 54u, 54u, 54u, true, true)]
    [TestCase(false, 0u, 54u, 54u, true, false)]
    public void SourceRefresh_ResetsSolverOnlyAtSourceGenerationBoundary(
        bool sourceRefresh,
        uint cachedGeneration,
        uint requestedGeneration,
        uint currentGeneration,
        bool freshPhysicalProbe,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.IsTransportSourceGenerationBoundary(
                sourceRefresh,
                cachedGeneration,
                requestedGeneration,
                currentGeneration,
                freshPhysicalProbe),
            Is.EqualTo(expected));
    }

    [TestCase(true, false, false, false, true)]
    [TestCase(true, true, false, false, false)]
    [TestCase(true, false, true, false, false)]
    [TestCase(true, false, false, true, false)]
    [TestCase(false, false, false, false, false)]
    public void CachedSolverPriority_OnlySelectsReadyPostWarmupUnconvergedWork(
        bool transportV2,
        bool globalPending,
        bool sourceRefreshRequired,
        bool locallyConverged,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldPrioritizeCachedTransportSolve(
                transportV2,
                globalPending,
                sourceRefreshRequired,
                locallyConverged),
            Is.EqualTo(expected));
    }

    [Test]
    public void ExactProbeAgeStatistics_ReportObservedDistribution()
    {
        uint[] ages =
        [
            0u, 0u, 1u, 2u, 3u,
            5u, 8u, 13u, 21u, uint.MaxValue
        ];
        uint[] scratch = new uint[ages.Length];

        SimpleDdgiVolumeManager.CalculateProbeAgeStatistics(
            ages,
            scratch,
            out uint p50,
            out uint p95,
            out uint maximum);

        Assert.Multiple(() =>
        {
            Assert.That(p50, Is.EqualTo(3u));
            Assert.That(p95, Is.EqualTo(uint.MaxValue));
            Assert.That(maximum, Is.EqualTo(uint.MaxValue));
        });
    }

    [Test]
    public void ExactProbeAgeStatistics_EmptyOrUndersizedInputFailsClosed()
    {
        SimpleDdgiVolumeManager.CalculateProbeAgeStatistics(
            ReadOnlySpan<uint>.Empty,
            Span<uint>.Empty,
            out uint emptyP50,
            out uint emptyP95,
            out uint emptyMaximum);
        SimpleDdgiVolumeManager.CalculateProbeAgeStatistics(
            [1u, 2u],
            new uint[1],
            out uint shortP50,
            out uint shortP95,
            out uint shortMaximum);

        Assert.Multiple(() =>
        {
            Assert.That((emptyP50, emptyP95, emptyMaximum), Is.EqualTo((0u, 0u, 0u)));
            Assert.That((shortP50, shortP95, shortMaximum), Is.EqualTo((0u, 0u, 0u)));
        });
    }

    [Test]
    public void JacobiChain_FieldWideStabilityPreventsDistantBlackRetirement()
    {
        const int probeCount = 20;
        const float reflectance = 0.80f;
        const float relaxation = 0.70f;
        float[] field = new float[probeCount];
        float[] next = new float[probeCount];
        float[] envelopes = Enumerable.Repeat(1.0f, probeCount).ToArray();
        int[] stableCounts = new int[probeCount];
        float farResidualAtMinimumGeneration = 0.0f;
        bool farProbeWouldHaveRetiredLocallyBeforeBounceArrived = false;
        int convergedIteration = -1;

        for (int iteration = 1; iteration <= 1_024; iteration++)
        {
            for (int probe = 0; probe < probeCount; probe++)
            {
                float target = probe == 0 ? 1.0f : field[probe - 1] * reflectance;
                float residual = SimpleDdgiVolumeManager.CalculateTransportConvergenceResidual(
                    new Vector3(target),
                    new Vector3(field[probe]),
                    ResidualThreshold);
                envelopes[probe] = SimpleDdgiVolumeManager.UpdateTransportResidualEnvelope(
                    envelopes[probe],
                    residual);
                next[probe] = field[probe] + (target - field[probe]) * relaxation;
            }

            if (iteration == 8)
                farResidualAtMinimumGeneration = envelopes[^1];

            for (int probe = 0; probe < probeCount; probe++)
            {
                bool postWarmupStable =
                    iteration >= 8 &&
                    envelopes[probe] <= ResidualThreshold;
                stableCounts[probe] = postWarmupStable
                    ? stableCounts[probe] + 1
                    : 0;
            }

            (field, next) = (next, field);
            bool[] localConvergence = Enumerable.Range(0, probeCount).Select(probe =>
                SimpleDdgiVolumeManager.MeetsTransportConvergenceCriteria(
                    iteration,
                    8,
                    stableCounts[probe],
                    3,
                    envelopes[probe],
                    ResidualThreshold)).ToArray();
            if (iteration < probeCount &&
                localConvergence[^1] &&
                !localConvergence.All(static converged => converged))
            {
                farProbeWouldHaveRetiredLocallyBeforeBounceArrived = true;
            }

            bool allConverged = localConvergence.All(static converged => converged);
            if (allConverged)
            {
                convergedIteration = iteration;
                break;
            }
        }

        float analyticFarValue = MathF.Pow(reflectance, probeCount - 1);
        Assert.Multiple(() =>
        {
            Assert.That(farResidualAtMinimumGeneration, Is.LessThanOrEqualTo(ResidualThreshold));
            Assert.That(farProbeWouldHaveRetiredLocallyBeforeBounceArrived, Is.True);
            Assert.That(convergedIteration, Is.GreaterThan(probeCount));
            Assert.That(
                field[^1],
                Is.EqualTo(analyticFarValue).Within(analyticFarValue * 0.02f));
        });
    }

    [Test]
    public void ThinSheetJacobiChain_MatchesAnalyticReflectedAndTransmittedFixedPoint()
    {
        const int cellCount = 20;
        const int sheetCell = 7;
        const float ordinaryReflectance = 0.78f;
        const float sheetReflectance = 0.24f;
        const float sheetTransmittance = 0.46f;
        const float relaxation = 0.70f;
        float[] field = new float[cellCount];
        float[] next = new float[cellCount];

        for (int iteration = 0; iteration < 1_024; iteration++)
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                float target;
                if (cell == 0)
                    target = 1f;
                else if (cell == sheetCell)
                    target = field[cell - 1] * (sheetReflectance + sheetTransmittance);
                else
                    target = field[cell - 1] * ordinaryReflectance;
                next[cell] = field[cell] + (target - field[cell]) * relaxation;
            }
            (field, next) = (next, field);
        }

        float expected =
            MathF.Pow(ordinaryReflectance, cellCount - 2) *
            (sheetReflectance + sheetTransmittance);
        Assert.That(field[^1], Is.EqualTo(expected).Within(expected * 0.01f));
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public void ProbeStateReadback_RemainsAvailableForV2Convergence(
        bool classificationFeedbackEnabled,
        bool transportV2Active,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.RequiresProbeStateReadback(
                classificationFeedbackEnabled,
                transportV2Active),
            Is.EqualTo(expected));
    }

    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    public void ProbeStateReadback_IdleFramePreservesLatestCompletedEvidence(
        bool readbackRequired,
        bool readbackRecorded,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldPreserveProbeStateReadbackEvidence(
                readbackRequired,
                readbackRecorded),
            Is.EqualTo(expected));
    }

    [Test]
    public void ProbeStateReadback_RejectsResidualFromPriorSourceEpoch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.IsTransportSourceEpochCurrent(7u, 7u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsTransportSourceEpochCurrent(7u, 8u),
                Is.False);
        });
    }

    [TestCase(true, true, false, true)]
    [TestCase(true, true, true, false)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    public void ProbeReactivation_ReopensTransportOnlyForRealFeedbackTransition(
        bool classificationFeedbackEnabled,
        bool wasInactive,
        bool isInactive,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.IsTransportProbeReactivated(
                classificationFeedbackEnabled,
                wasInactive,
                isInactive),
            Is.EqualTo(expected));
    }

    [Test]
    public void BlendShader_UsesTheBehavioralResidualContract()
    {
        string shader = File.ReadAllText(FindSourceFile(
            "Njulf.Shaders",
            "ddgi_simple_blend.comp")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain(
                "const float SIMPLE_DDGI_TRANSPORT_ABSOLUTE_RESIDUAL_TOLERANCE = 0.0001;"));
            Assert.That(shader, Does.Contain("float SimpleDdgiTransportConvergenceResidual("));
            Assert.That(
                shader.Split(
                    "SimpleDdgiTransportConvergenceResidual(params, irradiance, previous.rgb)",
                    StringSplitOptions.None).Length - 1,
                Is.EqualTo(2));
            Assert.That(shader, Does.Contain(
                "SimpleDdgiStableRelativeDelta(currentLuma, previousLuma, 0.02);"));
            Assert.That(shader, Does.Contain(
                "return (params.flags & SIMPLE_DDGI_FLAG_TRANSPORT_V2) != 0u"));
            Assert.That(shader, Does.Contain("aggregateIrradianceDelta = max("));
            Assert.That(shader, Does.Contain("SharedSimpleIrradianceDelta[texel]);"));
            Assert.That(shader, Does.Contain("if (!transportV2Active)"));
            Assert.That(shader, Does.Contain(
                "SIMPLE_DDGI_TRANSPORT_RESIDUAL_ENVELOPE_DECAY"));
        });
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        string? sourceDirectory = Path.GetDirectoryName(GetThisSourceFilePath());
        string? repositoryRoot = sourceDirectory == null
            ? null
            : Directory.GetParent(sourceDirectory)?.FullName;
        if (repositoryRoot != null)
        {
            string sourceTreeCandidate =
                Path.Combine(repositoryRoot, Path.Combine(relativeParts));
            if (File.Exists(sourceTreeCandidate))
                return sourceTreeCandidate;
        }

        string currentDirectoryCandidate =
            Path.Combine(Directory.GetCurrentDirectory(), Path.Combine(relativeParts));
        if (File.Exists(currentDirectoryCandidate))
            return currentDirectoryCandidate;

        string directory = TestContext.CurrentContext.TestDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(directory, Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate source file.",
            Path.Combine(relativeParts));
    }

    private static string GetThisSourceFilePath(
        [CallerFilePath] string sourceFilePath = "") =>
        sourceFilePath;
}
