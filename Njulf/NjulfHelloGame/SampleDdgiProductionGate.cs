using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleDdgiProductionGateReport(
    bool Passed,
    IReadOnlyList<SampleDdgiProductionGateCriterion> Criteria)
{
    public IReadOnlyList<SampleDdgiProductionGateCriterion> Failures { get; } =
        Criteria.Where(criterion => !criterion.Passed).ToArray();
}

public sealed record SampleDdgiProductionGateCriterion(
    string Name,
    bool Passed,
    string Detail);

public static class SampleDdgiProductionGate
{
    public const double DdgiHighUpdateP95BudgetMilliseconds = 2.5;

    private static readonly HashSet<SamplePerformanceScenario> RequiredScenarios =
        SampleDdgiBenchmarkSuite.RequiredProductionGateScenes
            .Select(scene => scene.Scenario)
            .ToHashSet();

    public static SampleDdgiProductionGateReport Evaluate(SampleBenchmarkReport report)
    {
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        RendererDiagnostics diagnostics = report.LastDiagnostics ?? RendererDiagnostics.Empty;
        SampleBenchmarkTimingStats? ddgiUpdatePass = FindGpuPass(report, "DdgiUpdatePass");
        var criteria = new List<SampleDdgiProductionGateCriterion>
        {
            Criterion(
                "required-production-scene",
                RequiredScenarios.Contains(report.Scenario),
                $"scenario={report.Scenario}"),
            Criterion(
                "ddgi-high-profile",
                diagnostics.ActiveQualityPreset == RenderQualityPreset.DdgiHigh &&
                diagnostics.DdgiQualityTier == DdgiQualityTier.DdgiHigh,
                $"preset={diagnostics.ActiveQualityPreset}, tier={diagnostics.DdgiQualityTier}"),
            Criterion(
                "ddgi-only-ray-query-active",
                diagnostics.GlobalIlluminationEnabled != 0 &&
                diagnostics.GlobalIlluminationMode == GlobalIlluminationMode.Ddgi &&
                diagnostics.GlobalIlluminationDdgiActive != 0 &&
                diagnostics.GlobalIlluminationRayQueryActive != 0 &&
                diagnostics.GlobalIlluminationSsgiActive == 0,
                $"enabled={diagnostics.GlobalIlluminationEnabled}, mode={diagnostics.GlobalIlluminationMode}, ddgi={diagnostics.GlobalIlluminationDdgiActive}, ssgi={diagnostics.GlobalIlluminationSsgiActive}, rayQuery={diagnostics.GlobalIlluminationRayQueryActive}"),
            Criterion(
                "no-ssgi-resources",
                diagnostics.SsgiRenderTargetBytes == 0 &&
                diagnostics.SsgiWidth == 0 &&
                diagnostics.SsgiHeight == 0 &&
                diagnostics.SsgiRayCount == 0,
                $"ssgiBytes={diagnostics.SsgiRenderTargetBytes}, size={diagnostics.SsgiWidth}x{diagnostics.SsgiHeight}, rays={diagnostics.SsgiRayCount}"),
            Criterion(
                "no-ssgi-passes",
                !HasSsgiPass(report, diagnostics),
                "SSGI pass names are absent from benchmark, production pipeline, and render graph diagnostics"),
            Criterion(
                "no-full-ddgi-copy-without-updates",
                diagnostics.DdgiProbesUpdated > 0 ||
                (diagnostics.DdgiRecursiveCommitBytes == 0 &&
                 diagnostics.DdgiRecursiveCommitCopyCount == 0 &&
                 diagnostics.DdgiRecursiveCommitProbeCount == 0),
                $"updates={diagnostics.DdgiProbesUpdated}, recursiveCommitBytes={diagnostics.DdgiRecursiveCommitBytes}, copies={diagnostics.DdgiRecursiveCommitCopyCount}"),
            Criterion(
                "no-static-frame-full-as-rebuild",
                !IsStaticScene(report.Scenario) ||
                (diagnostics.AccelerationStructureBlasBuildCount == 0 &&
                 diagnostics.AccelerationStructureTlasBuildCount == 0),
                $"scenario={report.Scenario}, blasBuilds={diagnostics.AccelerationStructureBlasBuildCount}, tlasBuilds={diagnostics.AccelerationStructureTlasBuildCount}"),
            Criterion(
                "clipmaps-preserved-with-authored-volumes",
                diagnostics.DdgiProbeVolumeCount <= diagnostics.DdgiCascadeCount ||
                diagnostics.DdgiCascadeCount > 0,
                $"volumes={diagnostics.DdgiProbeVolumeCount}, cascades={diagnostics.DdgiCascadeCount}"),
            Criterion(
                "gpu-timing-valid",
                report.GpuTimingSupported != 0 &&
                report.GpuTimingValidSampleCount > 0 &&
                report.GpuTimingValidSampleCount >= Math.Max(1, report.MeasurementFrameCount),
                $"supported={report.GpuTimingSupported}, validSamples={report.GpuTimingValidSampleCount}, measured={report.MeasurementFrameCount}, reason={report.GpuTimingUnavailableReason}"),
            Criterion(
                "ddgi-update-p95-budget",
                IsDdgiUpdateWithinBudget(report, diagnostics),
                $"p95={ddgiUpdatePass?.P95Milliseconds ?? 0.0:F3}ms, budget={DdgiHighUpdateP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-memory-budget",
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0 &&
                diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes <= diagnostics.DdgiAtlasMemoryBudgetBytes,
                $"currentAtlas={diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes}, budget={diagnostics.DdgiAtlasMemoryBudgetBytes}"),
            Criterion(
                "budget-metrics-within-gate",
                report.BudgetMetrics.All(metric => metric.Status != RenderBudgetStatus.OverBudget),
                $"overBudget={string.Join(',', report.BudgetMetrics.Where(metric => metric.Status == RenderBudgetStatus.OverBudget).Select(metric => metric.Name))}"),
            Criterion(
                "foliage-ddgi-receiver-covered",
                report.Scenario != SamplePerformanceScenario.ForestFoliage ||
                diagnostics.FoliageVisibleClusterCount > 0 ||
                diagnostics.FoliageVisibleMeshletDrawCount > 0,
                $"scenario={report.Scenario}, foliageClusters={diagnostics.FoliageVisibleClusterCount}, foliageDraws={diagnostics.FoliageVisibleMeshletDrawCount}"),
            Criterion(
                "debug-views-expose-ddgi-gate-data",
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiProbeState) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiUpdateReasons) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiRayBudget),
                "DDGI coverage, probe state, update reason, and ray budget debug views are selectable")
        };

        return new SampleDdgiProductionGateReport(
            criteria.All(criterion => criterion.Passed),
            criteria);
    }

    private static SampleDdgiProductionGateCriterion Criterion(string name, bool passed, string detail) =>
        new(name, passed, detail);

    private static bool HasSsgiPass(SampleBenchmarkReport report, RendererDiagnostics diagnostics)
    {
        return report.GpuPasses.Any(pass => IsSsgiName(pass.Name)) ||
            diagnostics.ProductionPipelineDeclaredPasses.Any(IsSsgiName) ||
            diagnostics.ProductionPipelineActivePasses.Any(IsSsgiName) ||
            diagnostics.Graph.Passes.Any(pass => IsSsgiName(pass.Name)) ||
            diagnostics.Graph.Resources.Any(resource => IsSsgiName(resource.Id) || IsSsgiName(resource.DebugName));
    }

    private static bool IsSsgiName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name.IndexOf("Ssgi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsStaticScene(SamplePerformanceScenario scenario)
    {
        return scenario is not SamplePerformanceScenario.GiMovingPointLight
            and not SamplePerformanceScenario.GiMovingRigidObject
            and not SamplePerformanceScenario.ForestFoliage;
    }

    private static bool IsDdgiUpdateWithinBudget(SampleBenchmarkReport report, RendererDiagnostics diagnostics)
    {
        SampleBenchmarkTimingStats? ddgiUpdate = FindGpuPass(report, "DdgiUpdatePass");
        if (diagnostics.DdgiProbesUpdated <= 0 && ddgiUpdate == null)
            return true;

        return ddgiUpdate != null &&
            ddgiUpdate.P95Milliseconds <= DdgiHighUpdateP95BudgetMilliseconds;
    }

    private static SampleBenchmarkTimingStats? FindGpuPass(SampleBenchmarkReport report, string name)
    {
        return report.GpuPasses.FirstOrDefault(pass => pass.Name.Equals(name, StringComparison.Ordinal));
    }
}
