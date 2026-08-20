using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

public sealed record SampleTailDdgiScenarioReportPaths(
    string Scroll,
    string Teleport,
    string SourceChange,
    string Relocation,
    string HighAlbedo,
    string ThinWall);

public sealed record SampleTailDdgiQualificationManifest(
    int SchemaVersion,
    IReadOnlyList<string> TailJacobiReports,
    IReadOnlyList<string> TailAcceleratedReports,
    SampleTailDdgiScenarioReportPaths Scenarios,
    string LongSoakReport);

public sealed record SampleTailDdgiQualificationCriterion(
    string Name,
    bool Passed,
    string Detail);

public sealed record SampleTailDdgiQualificationRun(
    string Role,
    string Path,
    SamplePerformanceScenario Scenario,
    DateTimeOffset CapturedAtUtc,
    string Variant,
    string PairId,
    string IdentityHash,
    string FullIdentityHash,
    string BuildConfiguration,
    bool CaptureComparable,
    bool ProductionTiming,
    bool ProductionGatePassed,
    SampleBenchmarkHdrDifference HdrComparison,
    SampleTailDdgiRuntimeEvidence Evidence);

public sealed record SampleTailDdgiLongSoakEvidence(
    string Path,
    string Status,
    string? Failure,
    string BuildConfiguration,
    double ElapsedSeconds,
    int RequestedFrameCount,
    double RequestedMinutes,
    long ExpectedSampleCount,
    long TotalSamples,
    bool ManagedMemoryStable,
    bool GpuMemoryStable,
    int BudgetViolationFrameCount,
    int TelemetryCoverageFailureFrameCount,
    long TextureExhaustionSampleCount,
    long SamplerExhaustionSampleCount,
    MaterialGiProducerIdentity ProducerIdentity)
{
    public string QualificationProfile { get; init; } = string.Empty;
    public string GiGpuMetricSource { get; init; } = string.Empty;
    public uint CaptureRenderWidth { get; init; }
    public uint CaptureRenderHeight { get; init; }
    public bool TimingGatesPassed { get; init; }
}

public sealed record SampleTailDdgiQualificationReport(
    int SchemaVersion,
    string Kind,
    DateTimeOffset EvaluatedAtUtc,
    bool Passed,
    SampleTailDdgiMathQualificationReport Mathematics,
    double RuntimeSolveEpochReduction,
    double RuntimeConvergenceFrameReduction,
    IReadOnlyList<SampleTailDdgiQualificationRun> TailJacobiRuns,
    IReadOnlyList<SampleTailDdgiQualificationRun> TailAcceleratedRuns,
    IReadOnlyList<SampleTailDdgiQualificationRun> ScenarioRuns,
    SampleTailDdgiLongSoakEvidence LongSoak,
    IReadOnlyList<SampleTailDdgiQualificationCriterion> Criteria)
{
    public IReadOnlyList<SampleTailDdgiQualificationCriterion> Failures { get; } =
        Criteria.Where(static criterion => !criterion.Passed).ToArray();
}

public sealed record SampleTailDdgiQualificationReportArtifact(
    string Role,
    string Path,
    SampleBenchmarkReport Report);

public sealed record SampleTailDdgiQualificationInput(
    IReadOnlyList<SampleTailDdgiQualificationReportArtifact> TailJacobi,
    IReadOnlyList<SampleTailDdgiQualificationReportArtifact> TailAccelerated,
    IReadOnlyList<SampleTailDdgiQualificationReportArtifact> Scenarios,
    SampleTailDdgiLongSoakEvidence LongSoak);

public static class SampleTailDdgiQualificationEvaluator
{
    public const int RequiredRepetitionCount = 3;
    public const double RequiredAccelerationReduction = 0.30;
    public const double MaximumTrackedMemoryBudgetFraction = 0.80;

    private static readonly IReadOnlyDictionary<string, SamplePerformanceScenario>
        RequiredScenarios = new Dictionary<string, SamplePerformanceScenario>(
            StringComparer.Ordinal)
        {
            ["scroll"] = SamplePerformanceScenario.GiLocalVolumeStreaming,
            ["teleport"] = SamplePerformanceScenario.GiFastTraversalTeleport,
            ["source-change"] = SamplePerformanceScenario.GiMovingPointLight,
            ["relocation"] = SamplePerformanceScenario.GiMovingRigidObject,
            ["high-albedo"] = SamplePerformanceScenario.GiSimpleDdgiFurnace,
            ["thin-wall"] = SamplePerformanceScenario.GiThinWallLeakTest
        };

    public static SampleTailDdgiQualificationReport Evaluate(
        SampleTailDdgiQualificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var criteria = new List<SampleTailDdgiQualificationCriterion>();
        SampleTailDdgiMathQualificationReport mathematics =
            SampleTailDdgiMathQualification.Run();
        Add(
            criteria,
            "mathematical-error-bounds",
            mathematics.AccuracyPassed,
            $"cases={mathematics.Cases.Count}, " +
            $"failed={mathematics.Cases.Count(static result => !result.Passed)}");
        Add(
            criteria,
            "mathematical-acceleration",
            mathematics.AccelerationPassed,
            $"jacobiEpochs={mathematics.JacobiSolveEpochs}, " +
            $"acceleratedEpochs={mathematics.AcceleratedSolveEpochs}, " +
            $"reduction={mathematics.SolveEpochReduction:P2}, " +
            $"required={SampleTailDdgiMathQualification.RequiredAccelerationReduction:P0}");

        Add(
            criteria,
            "three-tail-jacobi-repetitions",
            input.TailJacobi?.Count == RequiredRepetitionCount,
            $"observed={input.TailJacobi?.Count ?? 0}, required={RequiredRepetitionCount}");
        Add(
            criteria,
            "three-tail-accelerated-repetitions",
            input.TailAccelerated?.Count == RequiredRepetitionCount,
            $"observed={input.TailAccelerated?.Count ?? 0}, required={RequiredRepetitionCount}");

        SampleTailDdgiQualificationReportArtifact[] jacobi =
            input.TailJacobi?.ToArray() ?? Array.Empty<SampleTailDdgiQualificationReportArtifact>();
        SampleTailDdgiQualificationReportArtifact[] accelerated =
            input.TailAccelerated?.ToArray() ?? Array.Empty<SampleTailDdgiQualificationReportArtifact>();
        SampleTailDdgiQualificationReportArtifact[] scenarios =
            input.Scenarios?.ToArray() ?? Array.Empty<SampleTailDdgiQualificationReportArtifact>();

        for (int index = 0; index < jacobi.Length; index++)
        {
            EvaluateRuntimeReport(
                criteria,
                jacobi[index],
                $"tail-jacobi-{index + 1}",
                SampleBenchmarkCaptureVariant.TailJacobi,
                expectedAcceleration: false,
                expectedScenario: null);
        }
        for (int index = 0; index < accelerated.Length; index++)
        {
            EvaluateRuntimeReport(
                criteria,
                accelerated[index],
                $"tail-accelerated-{index + 1}",
                SampleBenchmarkCaptureVariant.TailAccelerated,
                expectedAcceleration: true,
                expectedScenario: null);
        }

        EvaluateRepeatability(criteria, jacobi, "tail-jacobi");
        EvaluateRepeatability(criteria, accelerated, "tail-accelerated");

        int pairCount = Math.Min(jacobi.Length, accelerated.Length);
        for (int index = 0; index < pairCount; index++)
        {
            EvaluateCrossModeIdentity(criteria, jacobi[index], accelerated[index], index);
            EvaluateRayBudget(criteria, jacobi[index], accelerated[index], index);
        }

        Add(
            criteria,
            "required-runtime-scenarios",
            scenarios.Length == RequiredScenarios.Count &&
            RequiredScenarios.Keys.All(role => scenarios.Count(
                artifact => string.Equals(
                    artifact.Role,
                    role,
                    StringComparison.Ordinal)) == 1),
            $"observed={string.Join(",", scenarios.Select(static item => item.Role).OrderBy(static role => role, StringComparer.Ordinal))}; " +
            $"required={string.Join(",", RequiredScenarios.Keys.OrderBy(static role => role, StringComparer.Ordinal))}");
        foreach (SampleTailDdgiQualificationReportArtifact scenario in scenarios)
        {
            RequiredScenarios.TryGetValue(
                scenario.Role,
                out SamplePerformanceScenario expectedScenario);
            EvaluateRuntimeReport(
                criteria,
                scenario,
                $"scenario-{scenario.Role}",
                SampleBenchmarkCaptureVariant.TailAccelerated,
                expectedAcceleration: true,
                RequiredScenarios.ContainsKey(scenario.Role)
                    ? expectedScenario
                    : null);
            if (accelerated.Length > 0)
            {
                EvaluateScenarioEnvironmentIdentity(
                    criteria,
                    accelerated[0],
                    scenario);
            }
        }

        double epochReduction = CalculateReduction(
            jacobi.Select(static run => run.Report.TailDdgiEvidence.SolveEpochCount),
            accelerated.Select(static run => run.Report.TailDdgiEvidence.SolveEpochCount));
        double frameReduction = CalculateReduction(
            jacobi.Select(static run => run.Report.TailDdgiEvidence.ConvergenceFrameCount),
            accelerated.Select(static run => run.Report.TailDdgiEvidence.ConvergenceFrameCount));
        bool runtimeSamplesComplete =
            jacobi.Length == RequiredRepetitionCount &&
            accelerated.Length == RequiredRepetitionCount &&
            jacobi.All(static run =>
                run.Report.TailDdgiEvidence.SolveEpochCount > 0 &&
                run.Report.TailDdgiEvidence.ConvergenceFrameCount > 0) &&
            accelerated.All(static run =>
                run.Report.TailDdgiEvidence.SolveEpochCount > 0 &&
                run.Report.TailDdgiEvidence.ConvergenceFrameCount > 0);
        Add(
            criteria,
            "runtime-acceleration-at-least-30-percent",
            runtimeSamplesComplete &&
            (epochReduction >= RequiredAccelerationReduction ||
             frameReduction >= RequiredAccelerationReduction),
            $"complete={runtimeSamplesComplete}, epochReduction={epochReduction:P2}, " +
            $"frameReduction={frameReduction:P2}, required={RequiredAccelerationReduction:P0}");

        EvaluateLongSoak(criteria, input.LongSoak, accelerated.FirstOrDefault()?.Report);

        SampleTailDdgiQualificationRun[] jacobiRuns = jacobi
            .Select(ToRun)
            .ToArray();
        SampleTailDdgiQualificationRun[] acceleratedRuns = accelerated
            .Select(ToRun)
            .ToArray();
        SampleTailDdgiQualificationRun[] scenarioRuns = scenarios
            .Select(ToRun)
            .ToArray();
        SampleTailDdgiQualificationCriterion[] immutableCriteria = criteria.ToArray();
        return new SampleTailDdgiQualificationReport(
            SchemaVersion: 1,
            Kind: "tail-certified-ddgi-qualification",
            EvaluatedAtUtc: DateTimeOffset.UtcNow,
            Passed: immutableCriteria.All(static criterion => criterion.Passed),
            Mathematics: mathematics,
            RuntimeSolveEpochReduction: epochReduction,
            RuntimeConvergenceFrameReduction: frameReduction,
            TailJacobiRuns: Array.AsReadOnly(jacobiRuns),
            TailAcceleratedRuns: Array.AsReadOnly(acceleratedRuns),
            ScenarioRuns: Array.AsReadOnly(scenarioRuns),
            LongSoak: input.LongSoak,
            Criteria: Array.AsReadOnly(immutableCriteria));
    }

    private static void EvaluateRuntimeReport(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        SampleTailDdgiQualificationReportArtifact artifact,
        string criterionPrefix,
        string expectedVariant,
        bool expectedAcceleration,
        SamplePerformanceScenario? expectedScenario)
    {
        SampleBenchmarkReport report = artifact.Report;
        SampleTailDdgiRuntimeEvidence evidence = report.TailDdgiEvidence;
        bool scenarioMatches = !expectedScenario.HasValue ||
            report.Scenario == expectedScenario.Value;
        Add(
            criteria,
            criterionPrefix + "-production-capture",
            report.CaptureContract.Comparable &&
            report.CaptureContract.ProductionTiming &&
            IsShippingPerformance(report.LastDiagnostics.CaptureRun.BuildConfiguration) &&
            report.MeasurementFrameCount >= 120 &&
            report.GpuTimingValidSampleCount == report.MeasurementFrameCount &&
            !report.SettlingWaitTimedOut &&
            scenarioMatches,
            $"comparable={report.CaptureContract.Comparable}, production={report.CaptureContract.ProductionTiming}, " +
            $"build='{report.LastDiagnostics.CaptureRun.BuildConfiguration}', measured={report.MeasurementFrameCount}, " +
            $"gpuValid={report.GpuTimingValidSampleCount}, settlingTimedOut={report.SettlingWaitTimedOut}, " +
            $"scenario={report.Scenario}, expectedScenario={expectedScenario?.ToString() ?? "paired-main"}");
        Add(
            criteria,
            criterionPrefix + "-variant",
            string.Equals(report.CaptureContract.Variant, expectedVariant, StringComparison.Ordinal) &&
            string.Equals(evidence.Variant, expectedVariant, StringComparison.Ordinal) &&
            evidence.AccelerationEnabled == expectedAcceleration,
            $"capture='{report.CaptureContract.Variant}', evidence='{evidence.Variant}', " +
            $"acceleration={evidence.AccelerationEnabled}, expected={expectedAcceleration}");
        Add(
            criteria,
            criterionPrefix + "-production-gate",
            report.DdgiProductionGate is { Passed: true },
            report.DdgiProductionGate == null
                ? "DDGI production gate evidence is missing."
                : $"passed={report.DdgiProductionGate.Passed}, failures={report.DdgiProductionGate.Failures.Count}");
        Add(
            criteria,
            criterionPrefix + "-hdr",
            report.HdrDifference is { Available: true, Passed: true },
            $"available={report.HdrDifference.Available}, passed={report.HdrDifference.Passed}, " +
            $"relativeRmse={report.HdrDifference.RelativeRmse:R}, reason='{report.HdrDifference.FailureReason}'");

        bool exactCoverage = evidence.Available &&
            evidence.ExpectedParticipantCount > 0u &&
            evidence.ExpectedParticipantCount == evidence.AuditedParticipantCount &&
            evidence.ExpectedTexelCount > 0u &&
            evidence.ExpectedTexelCount == evidence.AuditedTexelCount;
        bool cleanAudit = evidence.ExcludedStaleSourceCount == 0u &&
            evidence.InvalidCacheCount == 0u &&
            evidence.CacheIdentityFailureCount == 0u &&
            evidence.CacheCardinalityFailureCount == 0u &&
            evidence.CacheSourceGenerationFailureCount == 0u &&
            evidence.CacheSourceEpochFailureCount == 0u &&
            evidence.CachePhysicalGenerationFailureCount == 0u &&
            evidence.NonFiniteCount == 0u &&
            evidence.CounterOverflowCount == 0u;
        bool currentCertificate = evidence.FinalAuditComplete &&
            evidence.FinalCertificateCurrent &&
            float.IsFinite(evidence.FinalTailBound) &&
            float.IsFinite(evidence.FinalTailTolerance) &&
            evidence.FinalTailBound <= evidence.FinalTailTolerance;
        bool trackingValid =
            evidence.StaticConvergedWithoutCurrentCertificateCount == 0 &&
            (evidence.FinalTrackingState != SimpleDdgiTrackingState.StaticConverged ||
             evidence.FinalCertificateCurrent);
        Add(
            criteria,
            criterionPrefix + "-certificate-correctness",
            exactCoverage && cleanAudit && currentCertificate && trackingValid,
            $"participants={evidence.AuditedParticipantCount}/{evidence.ExpectedParticipantCount}, " +
            $"texels={evidence.AuditedTexelCount}/{evidence.ExpectedTexelCount}, invalidCache={evidence.InvalidCacheCount}, " +
            $"staleSource={evidence.ExcludedStaleSourceCount}, cacheIdentity={evidence.CacheIdentityFailureCount}, " +
            $"cacheCardinality={evidence.CacheCardinalityFailureCount}, " +
            $"cacheSourceGeneration={evidence.CacheSourceGenerationFailureCount}, " +
            $"cacheSourceEpoch={evidence.CacheSourceEpochFailureCount}, " +
            $"cachePhysicalGeneration={evidence.CachePhysicalGenerationFailureCount}, " +
            $"nonFinite={evidence.NonFiniteCount}, overflow={evidence.CounterOverflowCount}, " +
            $"excludedNonResidentOrUnpublished={evidence.ExcludedNotVisibleCount}, auditComplete={evidence.FinalAuditComplete}, " +
            $"certificateCurrent={evidence.FinalCertificateCurrent}, bound={evidence.FinalTailBound:R}, " +
            $"tolerance={evidence.FinalTailTolerance:R}, tracking={evidence.FinalTrackingState}, " +
            $"staticWithoutCertificate={evidence.StaticConvergedWithoutCurrentCertificateCount}");
        Add(
            criteria,
            criterionPrefix + "-resident-tail-mode",
            evidence.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
            evidence.TailCertificationEnabled &&
            string.IsNullOrWhiteSpace(evidence.TailCertificationFallbackReason),
            $"scheduler={evidence.SchedulerMode}, tail={evidence.TailCertificationEnabled}, " +
            $"fallback='{evidence.TailCertificationFallbackReason}'");
        Add(
            criteria,
            criterionPrefix + "-timing-percentiles",
            HasCompleteTiming(evidence.GiGpuMilliseconds, report.MeasurementFrameCount) &&
            HasCompleteTiming(evidence.AcceleratedSolveGpuMilliseconds, report.MeasurementFrameCount) &&
            HasCompleteTiming(evidence.AuditGpuMilliseconds, report.MeasurementFrameCount),
            $"GI={DescribeTiming(evidence.GiGpuMilliseconds)}, " +
            $"solve={DescribeTiming(evidence.AcceleratedSolveGpuMilliseconds)}, " +
            $"audit={DescribeTiming(evidence.AuditGpuMilliseconds)}");
        Add(
            criteria,
            criterionPrefix + "-work-evidence",
            evidence.RunPrimaryProbeCount > 0UL &&
            evidence.RunPrimaryRayCount > 0UL &&
            evidence.RunRayQueryCount == evidence.RunPrimaryRayCount &&
            evidence.RunCachedSolverIterationCount > 0UL &&
            evidence.RunCachedTransportRayEvaluationCount > 0UL &&
            evidence.RunAuditChunkCount > 0UL,
            $"primaryProbes={evidence.RunPrimaryProbeCount}, primaryRays={evidence.RunPrimaryRayCount}, " +
            $"rayQueries={evidence.RunRayQueryCount}, shadowRays={evidence.RunShadowRayCount}, " +
            $"cachedSweeps={evidence.RunCachedSolverIterationCount}, " +
            $"cachedEvaluations={evidence.RunCachedTransportRayEvaluationCount}, " +
            $"auditChunks={evidence.RunAuditChunkCount}");
        bool memoryValid = evidence.GpuMemoryBudgetBytes > 0UL &&
            evidence.TrackedGpuMemoryBytes <=
                (decimal)evidence.GpuMemoryBudgetBytes *
                (decimal)MaximumTrackedMemoryBudgetFraction &&
            evidence.DdgiBufferBytes > 0UL &&
            evidence.ReceiverProbeBytes > 0UL &&
            evidence.SchedulerArenaBytes > 0UL &&
            evidence.SchedulerAuditReadbackBytes > 0UL;
        Add(
            criteria,
            criterionPrefix + "-memory",
            memoryValid,
            $"tracked={evidence.TrackedGpuMemoryBytes}, budget={evidence.GpuMemoryBudgetBytes}, " +
            $"ddgiTextures={evidence.DdgiTextureBytes}, ddgiBuffers={evidence.DdgiBufferBytes}, " +
            $"receiver={evidence.ReceiverProbeBytes}, scheduler={evidence.SchedulerArenaBytes}, " +
            $"feedbackReadback={evidence.SchedulerFeedbackReadbackBytes}, " +
            $"auditReadback={evidence.SchedulerAuditReadbackBytes}");
    }

    private static void EvaluateRepeatability(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        IReadOnlyList<SampleTailDdgiQualificationReportArtifact> reports,
        string role)
    {
        if (reports.Count != RequiredRepetitionCount)
            return;
        SampleBenchmarkReport baseline = reports[0].Report;
        for (int index = 1; index < reports.Count; index++)
        {
            SampleBenchmarkPairComparison comparison =
                SampleBenchmarkPairComparer.Compare(
                    baseline,
                    reports[index].Report,
                    requireRepeatability: false);
            SampleBenchmarkCaptureContract left = baseline.CaptureContract;
            SampleBenchmarkCaptureContract right =
                reports[index].Report.CaptureContract;
            bool exactIdentity = comparison.Comparable &&
                string.Equals(left.Variant, right.Variant, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(left.FullIdentityHash) &&
                !string.Equals(
                    left.FullIdentityHash,
                    "unavailable",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    left.FullIdentityHash,
                    right.FullIdentityHash,
                    StringComparison.Ordinal);
            double maximumObservedP95Difference = comparison.Metrics.Count == 0
                ? 0.0
                : comparison.Metrics.Max(static metric => metric.RelativeDifference);
            Add(
                criteria,
                $"{role}-repeat-{index + 1}",
                exactIdentity,
                exactIdentity
                    ? $"identity='{left.FullIdentityHash}', recordedMaxP95Difference={maximumObservedP95Difference:P2}; " +
                      "each repetition is gated independently against its production budget"
                    : comparison.Failures.Count == 0
                        ? "Repeated capture variant or exact rendered-state identity differs."
                        : string.Join("; ", comparison.Failures));
        }
    }

    private static void EvaluateCrossModeIdentity(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        SampleTailDdgiQualificationReportArtifact jacobi,
        SampleTailDdgiQualificationReportArtifact accelerated,
        int index)
    {
        SampleBenchmarkReport left = jacobi.Report;
        SampleBenchmarkReport right = accelerated.Report;
        RendererDiagnostics a = left.LastDiagnostics;
        RendererDiagnostics b = right.LastDiagnostics;
        bool baseIdentity =
            left.Scenario == right.Scenario &&
            string.Equals(left.CaptureContract.PairId, right.CaptureContract.PairId, StringComparison.Ordinal) &&
            string.Equals(a.CaptureGpuDeviceName, b.CaptureGpuDeviceName, StringComparison.Ordinal) &&
            string.Equals(a.CaptureGpuDriverVersion, b.CaptureGpuDriverVersion, StringComparison.Ordinal) &&
            a.CaptureRenderWidth == b.CaptureRenderWidth &&
            a.CaptureRenderHeight == b.CaptureRenderHeight &&
            a.ActiveQualityPreset == b.ActiveQualityPreset &&
            string.Equals(a.CaptureSceneAssetHash, b.CaptureSceneAssetHash, StringComparison.Ordinal) &&
            a.CaptureSceneContentRevision == b.CaptureSceneContentRevision &&
            string.Equals(a.CaptureSceneStateHash, b.CaptureSceneStateHash, StringComparison.Ordinal) &&
            a.CaptureCamera == b.CaptureCamera &&
            a.CaptureRun == b.CaptureRun &&
            a.ActiveFeatureIsolation == b.ActiveFeatureIsolation &&
            a.GlobalIlluminationDebugView == b.GlobalIlluminationDebugView &&
            a.CaptureFrame.DdgiCacheGeneration == b.CaptureFrame.DdgiCacheGeneration;
        bool settingsLocked = SettingsDifferOnlyByAcceleration(
            a.ResolvedGiSettings.EffectiveSettings,
            b.ResolvedGiSettings.EffectiveSettings,
            out string settingsDetail);
        Add(
            criteria,
            $"paired-run-{index + 1}-identity-lock",
            baseIdentity && settingsLocked,
            $"baseIdentity={baseIdentity}, settings={settingsDetail}");
        Add(
            criteria,
            $"paired-run-{index + 1}-tail-tolerance-locked",
            a.SimpleDdgiTransportTailRelativeTolerance > 0.0f &&
            a.SimpleDdgiTransportTailRelativeTolerance ==
                b.SimpleDdgiTransportTailRelativeTolerance,
            $"jacobiRelative={a.SimpleDdgiTransportTailRelativeTolerance:R}, " +
            $"acceleratedRelative={b.SimpleDdgiTransportTailRelativeTolerance:R}, " +
            $"jacobiAcceptedAbsolute={left.TailDdgiEvidence.FinalTailTolerance:R}, " +
            $"acceleratedAcceptedAbsolute={right.TailDdgiEvidence.FinalTailTolerance:R}");
    }

    private static void EvaluateRayBudget(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        SampleTailDdgiQualificationReportArtifact jacobi,
        SampleTailDdgiQualificationReportArtifact accelerated,
        int index)
    {
        SampleTailDdgiRuntimeEvidence a = jacobi.Report.TailDdgiEvidence;
        SampleTailDdgiRuntimeEvidence b = accelerated.Report.TailDdgiEvidence;
        bool equal = a.RunPrimaryProbeCount > 0UL &&
            a.RunPrimaryRayCount > 0UL &&
            a.RunPrimaryProbeCount == b.RunPrimaryProbeCount &&
            a.RunPrimaryRayCount == b.RunPrimaryRayCount &&
            a.RunRayQueryCount == b.RunRayQueryCount &&
            a.RunShadowRayCount == b.RunShadowRayCount &&
            a.RunEstimatedShadowRayUpperBound ==
                b.RunEstimatedShadowRayUpperBound;
        Add(
            criteria,
            $"paired-run-{index + 1}-ray-budget",
            equal,
            $"primaryProbes={a.RunPrimaryProbeCount}/{b.RunPrimaryProbeCount}, " +
            $"primaryRays={a.RunPrimaryRayCount}/{b.RunPrimaryRayCount}, " +
            $"rayQueries={a.RunRayQueryCount}/{b.RunRayQueryCount}, " +
            $"shadowRays={a.RunShadowRayCount}/{b.RunShadowRayCount}, " +
            $"shadowUpperBound={a.RunEstimatedShadowRayUpperBound}/{b.RunEstimatedShadowRayUpperBound}, " +
            $"cachedSweeps={a.RunCachedSolverIterationCount}/{b.RunCachedSolverIterationCount}");
    }

    private static void EvaluateScenarioEnvironmentIdentity(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        SampleTailDdgiQualificationReportArtifact reference,
        SampleTailDdgiQualificationReportArtifact scenario)
    {
        RendererDiagnostics a = reference.Report.LastDiagnostics;
        RendererDiagnostics b = scenario.Report.LastDiagnostics;
        bool locked =
            string.Equals(
                reference.Report.CaptureContract.PairId,
                scenario.Report.CaptureContract.PairId,
                StringComparison.Ordinal) &&
            string.Equals(a.CaptureGpuDeviceName, b.CaptureGpuDeviceName, StringComparison.Ordinal) &&
            string.Equals(a.CaptureGpuDriverVersion, b.CaptureGpuDriverVersion, StringComparison.Ordinal) &&
            a.CaptureRenderWidth == b.CaptureRenderWidth &&
            a.CaptureRenderHeight == b.CaptureRenderHeight &&
            a.ActiveQualityPreset == b.ActiveQualityPreset &&
            string.Equals(a.CaptureRun.BuildConfiguration, b.CaptureRun.BuildConfiguration, StringComparison.Ordinal) &&
            string.Equals(a.CaptureRun.ApplicationVersion, b.CaptureRun.ApplicationVersion, StringComparison.Ordinal) &&
            string.Equals(a.CaptureRun.Commit, b.CaptureRun.Commit, StringComparison.Ordinal) &&
            string.Equals(a.CaptureRun.ShaderBundleHash, b.CaptureRun.ShaderBundleHash, StringComparison.Ordinal) &&
            a.CaptureRun.SettingsSchemaVersion == b.CaptureRun.SettingsSchemaVersion &&
            string.Equals(a.CaptureRun.ExecutableHash, b.CaptureRun.ExecutableHash, StringComparison.Ordinal) &&
            string.Equals(a.CaptureRun.DirtyWorktreeState, b.CaptureRun.DirtyWorktreeState, StringComparison.Ordinal) &&
            a.ActiveBudgetProfile == b.ActiveBudgetProfile &&
            a.ActiveFeatureIsolation == b.ActiveFeatureIsolation &&
            a.GlobalIlluminationDebugView == b.GlobalIlluminationDebugView &&
            a.SimpleDdgiSchedulerMode == b.SimpleDdgiSchedulerMode &&
            a.SimpleDdgiTransportV2Active == b.SimpleDdgiTransportV2Active &&
            a.SimpleDdgiTransportTailCertificationEnabled ==
                b.SimpleDdgiTransportTailCertificationEnabled &&
            a.SimpleDdgiTransportAccelerationEnabled ==
                b.SimpleDdgiTransportAccelerationEnabled &&
            a.SimpleDdgiTransportTailRelativeTolerance ==
                b.SimpleDdgiTransportTailRelativeTolerance;
        Add(
            criteria,
            $"scenario-{scenario.Role}-environment-identity",
            locked,
            $"locked={locked}, pair='{scenario.Report.CaptureContract.PairId}', " +
            $"gpu='{b.CaptureGpuDeviceName}', driver='{b.CaptureGpuDriverVersion}', " +
            $"build='{b.CaptureRun.BuildConfiguration}', commit='{b.CaptureRun.Commit}', " +
            $"scenarioSettings='{b.ResolvedGiSettings.StableHash}', " +
            $"tailTolerance={b.SimpleDdgiTransportTailRelativeTolerance:R}");
    }

    private static void EvaluateLongSoak(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        SampleTailDdgiLongSoakEvidence soak,
        SampleBenchmarkReport? acceleratedReference)
    {
        bool runPassed = soak != null &&
            string.Equals(soak.Status, "passed", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(soak.Failure) &&
            IsShippingPerformance(soak.BuildConfiguration) &&
            (soak.RequestedMinutes > 0.0 || soak.RequestedFrameCount >= 3_600) &&
            soak.ExpectedSampleCount >= 2 &&
            soak.TotalSamples == soak.ExpectedSampleCount &&
            soak.ManagedMemoryStable &&
            soak.GpuMemoryStable &&
            soak.BudgetViolationFrameCount == 0 &&
            soak.TelemetryCoverageFailureFrameCount == 0 &&
            soak.TextureExhaustionSampleCount == 0 &&
            soak.SamplerExhaustionSampleCount == 0;
        Add(
            criteria,
            "long-soak-stability",
            runPassed,
            soak == null
                ? "Long-soak evidence is missing."
                : $"status={soak.Status}, build='{soak.BuildConfiguration}', seconds={soak.ElapsedSeconds:R}, " +
                  $"frames={soak.RequestedFrameCount}, minutes={soak.RequestedMinutes:R}, " +
                  $"samples={soak.TotalSamples}/{soak.ExpectedSampleCount}, " +
                  $"managedStable={soak.ManagedMemoryStable}, gpuStable={soak.GpuMemoryStable}, " +
                  $"budgetViolations={soak.BudgetViolationFrameCount}, " +
                  $"telemetryFailures={soak.TelemetryCoverageFailureFrameCount}");

        bool qualificationProfileLocked = soak != null &&
            string.Equals(
                soak.QualificationProfile,
                SampleTailDdgiLongSoakProfile.Name,
                StringComparison.Ordinal) &&
            string.Equals(
                soak.GiGpuMetricSource,
                SampleTailDdgiLongSoakProfile.GiGpuMetricSource,
                StringComparison.Ordinal) &&
            soak.TimingGatesPassed;
        Add(
            criteria,
            "long-soak-tail-profile",
            qualificationProfileLocked,
            soak == null
                ? "Long-soak evidence is missing."
                : $"profile='{soak.QualificationProfile}', " +
                  $"giGpuSource='{soak.GiGpuMetricSource}', " +
                  $"timingGatesPassed={soak.TimingGatesPassed}");

        MaterialGiProducerIdentity? reference = acceleratedReference?.ProducerIdentity;
        bool identityLocked = soak != null &&
            acceleratedReference != null &&
            reference != null &&
            soak.ProducerIdentity != null &&
            string.Equals(soak.ProducerIdentity.BuildCommit, reference.BuildCommit, StringComparison.Ordinal) &&
            string.Equals(soak.ProducerIdentity.ShaderFingerprint, reference.ShaderFingerprint, StringComparison.Ordinal) &&
            string.Equals(soak.ProducerIdentity.SettingsFingerprint, reference.SettingsFingerprint, StringComparison.Ordinal) &&
            string.Equals(soak.ProducerIdentity.GpuName, reference.GpuName, StringComparison.Ordinal) &&
            string.Equals(soak.ProducerIdentity.DriverVersion, reference.DriverVersion, StringComparison.Ordinal) &&
            string.Equals(soak.ProducerIdentity.QualityTier, reference.QualityTier, StringComparison.Ordinal) &&
            soak.CaptureRenderWidth ==
                acceleratedReference.LastDiagnostics.CaptureRenderWidth &&
            soak.CaptureRenderHeight ==
                acceleratedReference.LastDiagnostics.CaptureRenderHeight;
        Add(
            criteria,
            "long-soak-identity-lock",
            identityLocked,
            identityLocked
                ? $"settings='{reference!.SettingsFingerprint}', gpu='{reference.GpuName}', " +
                  $"render={soak!.CaptureRenderWidth}x{soak.CaptureRenderHeight}"
                : "Long-soak producer identity does not match the accelerated ShippingPerformance captures.");
    }

    private static SampleTailDdgiQualificationRun ToRun(
        SampleTailDdgiQualificationReportArtifact artifact)
    {
        SampleBenchmarkReport report = artifact.Report;
        return new SampleTailDdgiQualificationRun(
            artifact.Role,
            artifact.Path,
            report.Scenario,
            report.CapturedAtUtc,
            report.CaptureContract.Variant,
            report.CaptureContract.PairId,
            report.CaptureContract.IdentityHash,
            report.CaptureContract.FullIdentityHash,
            report.LastDiagnostics.CaptureRun.BuildConfiguration,
            report.CaptureContract.Comparable,
            report.CaptureContract.ProductionTiming,
            report.DdgiProductionGate is { Passed: true },
            report.HdrDifference,
            report.TailDdgiEvidence);
    }

    private static bool SettingsDifferOnlyByAcceleration(
        IReadOnlyList<string> jacobiSettings,
        IReadOnlyList<string> acceleratedSettings,
        out string detail)
    {
        const string accelerationKey =
            "gi.simpleDdgi.transport.accelerationEnabled";
        Dictionary<string, string> left = IndexSettings(jacobiSettings);
        Dictionary<string, string> right = IndexSettings(acceleratedSettings);
        string[] differing = left.Keys
            .Concat(right.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(key =>
                !left.TryGetValue(key, out string? leftValue) ||
                !right.TryGetValue(key, out string? rightValue) ||
                !string.Equals(leftValue, rightValue, StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        bool valid = differing.Length == 1 &&
            string.Equals(differing[0], accelerationKey, StringComparison.Ordinal) &&
            left.TryGetValue(accelerationKey, out string? jacobiValue) &&
            right.TryGetValue(accelerationKey, out string? acceleratedValue) &&
            string.Equals(jacobiValue, "0", StringComparison.Ordinal) &&
            string.Equals(acceleratedValue, "1", StringComparison.Ordinal);
        detail = differing.Length == 0
            ? "no resolved setting changed"
            : string.Join(",", differing.Select(key =>
                $"{key}:{left.GetValueOrDefault(key, "<missing>")}->{right.GetValueOrDefault(key, "<missing>")}"));
        return valid;
    }

    private static Dictionary<string, string> IndexSettings(
        IReadOnlyList<string> settings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string setting in settings ?? Array.Empty<string>())
        {
            int separator = setting.IndexOf('=');
            string key = separator >= 0 ? setting[..separator] : setting;
            string value = separator >= 0 ? setting[(separator + 1)..] : string.Empty;
            result[key] = value;
        }
        return result;
    }

    private static double CalculateReduction(
        IEnumerable<int> baseline,
        IEnumerable<int> accelerated)
    {
        long baselineTotal = baseline.Aggregate(
            0L,
            static (total, value) => checked(total + Math.Max(0, value)));
        long acceleratedTotal = accelerated.Aggregate(
            0L,
            static (total, value) => checked(total + Math.Max(0, value)));
        return baselineTotal > 0
            ? 1.0 - acceleratedTotal / (double)baselineTotal
            : 0.0;
    }

    private static bool HasCompleteTiming(
        SampleBenchmarkTimingStats stats,
        int expectedCount) =>
        stats.Count == expectedCount &&
        double.IsFinite(stats.P50Milliseconds) &&
        double.IsFinite(stats.P95Milliseconds) &&
        double.IsFinite(stats.P99Milliseconds) &&
        stats.P50Milliseconds >= 0.0 &&
        stats.P50Milliseconds <= stats.P95Milliseconds &&
        stats.P95Milliseconds <= stats.P99Milliseconds;

    private static string DescribeTiming(SampleBenchmarkTimingStats stats) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"count={stats.Count},p50={stats.P50Milliseconds:F3},p95={stats.P95Milliseconds:F3},p99={stats.P99Milliseconds:F3}ms");

    private static bool IsShippingPerformance(string? configuration) =>
        string.Equals(
            configuration?.Split(';', 2)[0].Trim(),
            "ShippingPerformance",
            StringComparison.OrdinalIgnoreCase);

    private static void Add(
        ICollection<SampleTailDdgiQualificationCriterion> criteria,
        string name,
        bool passed,
        string detail) =>
        criteria.Add(new SampleTailDdgiQualificationCriterion(name, passed, detail));
}

/// <summary>
/// One strict command for the complete qualification bundle. It never treats
/// compilation, unit tests, missing HDR, or absent production captures as a
/// successful production qualification.
/// </summary>
public static class SampleTailDdgiQualificationCli
{
    public const string QualificationOption = "--tail-ddgi-qualification";
    public const string ReportOption = "--tail-ddgi-qualification-report";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 128,
            WriteIndented = true
        };
        // Long-run reports deliberately serialize enum values by name for
        // human-auditable evidence. The qualification reader also consumes
        // benchmark reports that may contain numeric enums, both of which are
        // accepted by the standard converter.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        exitCode = 0;
        int optionIndex = Array.FindIndex(
            args,
            static argument => string.Equals(
                argument,
                QualificationOption,
                StringComparison.Ordinal));
        if (optionIndex < 0)
            return false;

        try
        {
            if (Array.FindLastIndex(
                    args,
                    static argument => string.Equals(
                        argument,
                        QualificationOption,
                        StringComparison.Ordinal)) != optionIndex ||
                optionIndex + 1 >= args.Length)
            {
                throw new ArgumentException(
                    $"{QualificationOption} must appear once and requires <manifest.json>.");
            }

            string manifestPath = RequireValue(
                args[optionIndex + 1],
                "qualification manifest");
            string? reportPath = null;
            var consumed = new HashSet<int> { optionIndex, optionIndex + 1 };
            for (int index = 0; index < args.Length; index++)
            {
                if (consumed.Contains(index))
                    continue;
                string argument = args[index];
                if (argument.StartsWith(ReportOption + "=", StringComparison.Ordinal))
                {
                    if (reportPath != null)
                        throw new ArgumentException($"{ReportOption} may appear only once.");
                    reportPath = RequireValue(
                        argument[(ReportOption.Length + 1)..],
                        "qualification report");
                    consumed.Add(index);
                    continue;
                }
                if (string.Equals(argument, ReportOption, StringComparison.Ordinal))
                {
                    if (reportPath != null || index + 1 >= args.Length)
                        throw new ArgumentException($"{ReportOption} requires one path.");
                    reportPath = RequireValue(args[index + 1], "qualification report");
                    consumed.Add(index);
                    consumed.Add(index + 1);
                    index++;
                    continue;
                }
                throw new ArgumentException(
                    $"{QualificationOption} is standalone and cannot be combined with '{argument}'.");
            }

            string fullManifestPath = Path.GetFullPath(manifestPath);
            SampleTailDdgiQualificationManifest manifest =
                Load<SampleTailDdgiQualificationManifest>(
                    fullManifestPath,
                    "tail-DDGI qualification manifest");
            ValidateManifest(manifest);
            string baseDirectory = Path.GetDirectoryName(fullManifestPath) ??
                Directory.GetCurrentDirectory();
            SampleTailDdgiQualificationInput input = LoadInput(
                manifest,
                baseDirectory);
            SampleTailDdgiQualificationReport report =
                SampleTailDdgiQualificationEvaluator.Evaluate(input);
            string targetPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(reportPath)
                    ? Path.Combine(
                        baseDirectory,
                        "tail-ddgi-qualification-report.json")
                    : reportPath);
            SampleEvidenceFileIo.WriteAtomic(
                targetPath,
                JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Tail-DDGI qualification report");

            if (report.Passed)
            {
                output.WriteLine(
                    $"Tail-certified DDGI qualification passed: report='{targetPath}', " +
                    $"mathReduction={report.Mathematics.SolveEpochReduction:P2}, " +
                    $"runtimeEpochReduction={report.RuntimeSolveEpochReduction:P2}, " +
                    $"runtimeFrameReduction={report.RuntimeConvergenceFrameReduction:P2}.");
                exitCode = 0;
            }
            else
            {
                error.WriteLine(
                    $"Tail-certified DDGI qualification failed: report='{targetPath}'. " +
                    string.Join("; ", report.Failures.Select(static failure =>
                        $"{failure.Name}: {failure.Detail}")));
                exitCode = 2;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                InvalidDataException or
                JsonException or
                UnauthorizedAccessException or
                OverflowException)
        {
            error.WriteLine(
                $"Tail-certified DDGI qualification command failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 64;
            return true;
        }
    }

    private static SampleTailDdgiQualificationInput LoadInput(
        SampleTailDdgiQualificationManifest manifest,
        string baseDirectory)
    {
        SampleTailDdgiQualificationReportArtifact[] jacobi = manifest.TailJacobiReports
            .Select((path, index) => LoadBenchmark(
                $"tail-jacobi-{index + 1}",
                ResolvePath(baseDirectory, path)))
            .ToArray();
        SampleTailDdgiQualificationReportArtifact[] accelerated =
            manifest.TailAcceleratedReports
                .Select((path, index) => LoadBenchmark(
                    $"tail-accelerated-{index + 1}",
                    ResolvePath(baseDirectory, path)))
                .ToArray();
        SampleTailDdgiQualificationReportArtifact[] scenarios =
        [
            LoadBenchmark("scroll", ResolvePath(baseDirectory, manifest.Scenarios.Scroll)),
            LoadBenchmark("teleport", ResolvePath(baseDirectory, manifest.Scenarios.Teleport)),
            LoadBenchmark("source-change", ResolvePath(baseDirectory, manifest.Scenarios.SourceChange)),
            LoadBenchmark("relocation", ResolvePath(baseDirectory, manifest.Scenarios.Relocation)),
            LoadBenchmark("high-albedo", ResolvePath(baseDirectory, manifest.Scenarios.HighAlbedo)),
            LoadBenchmark("thin-wall", ResolvePath(baseDirectory, manifest.Scenarios.ThinWall))
        ];
        string soakPath = ResolvePath(baseDirectory, manifest.LongSoakReport);
        SampleLongRunReport soak = Load<SampleLongRunReport>(
            soakPath,
            "tail-DDGI long-soak report");
        if (!string.Equals(
                soak.Kind,
                MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Long-soak report '{soakPath}' has unexpected kind '{soak.Kind}'.");
        }
        var soakEvidence = new SampleTailDdgiLongSoakEvidence(
            soakPath,
            soak.Status,
            soak.Failure,
            soak.BuildConfiguration,
            soak.ElapsedSeconds,
            soak.RequestedFrameCount,
            soak.RequestedMinutes,
            soak.ExpectedSampleCount,
            soak.TotalSamples,
            !soak.ManagedMemoryTrend.HasPositiveTrend,
            !soak.GpuMemoryTrend.HasPositiveTrend,
            soak.PostWarmupBudgetViolationFrameCount,
            soak.PostWarmupTelemetryCoverageFailureFrameCount,
            soak.DescriptorPressure.TextureExhaustionSampleCount,
            soak.DescriptorPressure.SamplerExhaustionSampleCount,
            soak.ProducerIdentity)
        {
            QualificationProfile = soak.QualificationProfile,
            GiGpuMetricSource = soak.GiGpuMetricSource,
            CaptureRenderWidth = soak.CaptureRenderWidth,
            CaptureRenderHeight = soak.CaptureRenderHeight,
            TimingGatesPassed =
                soak.TailTimingGates.Count ==
                    SampleTailDdgiLongSoakProfile
                        .RequiredPercentileTimingMetrics.Count &&
                soak.TailTimingGates.All(static gate => gate.Passed)
        };
        return new SampleTailDdgiQualificationInput(
            Array.AsReadOnly(jacobi),
            Array.AsReadOnly(accelerated),
            Array.AsReadOnly(scenarios),
            soakEvidence);
    }

    private static SampleTailDdgiQualificationReportArtifact LoadBenchmark(
        string role,
        string path)
    {
        SampleBenchmarkReport report = Load<SampleBenchmarkReport>(
            path,
            $"{role} benchmark report");
        if (!string.Equals(report.Kind, "njulf-renderer-benchmark", StringComparison.Ordinal) ||
            !string.Equals(
                report.Schema,
                MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema,
                StringComparison.Ordinal) ||
            report.MeasurementFrameCount <= 0)
        {
            throw new InvalidDataException(
                $"{role} report '{path}' is not a complete Njulf benchmark report.");
        }
        return new SampleTailDdgiQualificationReportArtifact(role, path, report);
    }

    private static T Load<T>(string path, string role)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            role);
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            JsonOptions.MaxDepth,
            role);
        return JsonSerializer.Deserialize<T>(evidence.Bytes, JsonOptions) ??
            throw new InvalidDataException($"{role} deserialized to null.");
    }

    private static void ValidateManifest(
        SampleTailDdgiQualificationManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Tail-DDGI qualification manifest schema {manifest.SchemaVersion} is unsupported; expected 1.");
        if (manifest.TailJacobiReports == null ||
            manifest.TailJacobiReports.Count !=
                SampleTailDdgiQualificationEvaluator.RequiredRepetitionCount)
        {
            throw new InvalidDataException("Exactly three TailJacobi report paths are required.");
        }
        if (manifest.TailAcceleratedReports == null ||
            manifest.TailAcceleratedReports.Count !=
                SampleTailDdgiQualificationEvaluator.RequiredRepetitionCount)
        {
            throw new InvalidDataException("Exactly three TailAccelerated report paths are required.");
        }
        if (manifest.Scenarios == null)
            throw new InvalidDataException("All required runtime scenario report paths are required.");

        string[] paths = manifest.TailJacobiReports
            .Concat(manifest.TailAcceleratedReports)
            .Concat(
            [
                manifest.Scenarios.Scroll,
                manifest.Scenarios.Teleport,
                manifest.Scenarios.SourceChange,
                manifest.Scenarios.Relocation,
                manifest.Scenarios.HighAlbedo,
                manifest.Scenarios.ThinWall,
                manifest.LongSoakReport
            ])
            .ToArray();
        if (paths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Qualification report paths cannot be empty.");
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Every qualification report path must be unique.");
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(baseDirectory, path));

    private static string RequireValue(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"A non-option {role} path is required.");
        }
        return value;
    }
}
