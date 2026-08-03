using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Diagnostics
{
    public sealed class RenderBudgetEvaluator
    {
        public const double WarningRatio = 0.85;
        public const double MaxDefaultSsgiResolutionScale = 0.5;
        public const int MaxDefaultSsgiRayCount = 8;
        public const string MaterialGiQualificationMetricName =
            "Material GI release qualification";
        public const string MaterialGiActiveV1FallbackMetricName =
            "Material GI active V1 fallbacks";
        public const string MaterialGiActiveInvalidProfileMetricName =
            "Material GI active invalid profiles";
        public const string MaterialGiCompileP95MetricName =
            "Material GI compile P95";
        public const string MaterialGiUploadP95MetricName =
            "Material GI upload P95";
        public const string MaterialGiPipelineP95MetricName =
            "Material GI compile/upload P95";
        public const string MaterialGiPrimitiveProfileMemoryMetricName =
            "Material GI primitive profile memory";
        public const string DdgiEmissiveTruncatedSourceMetricName =
            "DDGI emissive truncated sources";
        public const string DdgiEmissiveSkippedEnergyMetricName =
            "DDGI emissive skipped energy";
        public const string DdgiEmissiveUnsupportedSkinnedObjectMetricName =
            "DDGI emissive unsupported skinned objects";
        public const string DdgiEmissiveUnsupportedSkinnedImportanceMetricName =
            "DDGI emissive unsupported skinned importance";
        public const double ZeroGateNumericalTolerance = 1.0e-12;
        // "GPU memory" remains the compatibility metric consumed by renderer diagnostics:
        // driver heap usage when available, otherwise tracked allocations. When the driver
        // reports a heap budget, the tracked allocation budget is emitted separately.
        public const string EffectiveGpuMemoryMetricName = "GPU memory";
        public const string TrackedGpuMemoryMetricName = "Tracked GPU memory";

        public static RenderBudgetStatus Classify(double value, double failureThreshold)
        {
            if (double.IsNaN(value) || double.IsNaN(failureThreshold))
                return RenderBudgetStatus.Unavailable;
            if (double.IsPositiveInfinity(failureThreshold))
                return RenderBudgetStatus.WithinBudget;
            if (failureThreshold <= 0)
                return RenderBudgetStatus.Unavailable;
            if (value > failureThreshold)
                return RenderBudgetStatus.OverBudget;
            if (value > failureThreshold * WarningRatio)
                return RenderBudgetStatus.Warning;
            return RenderBudgetStatus.WithinBudget;
        }

        public static ulong ResolvePrimitiveProfileMemoryBudgetBytes(
            RenderQualityPreset qualityPreset) => qualityPreset switch
            {
                RenderQualityPreset.Low => 1UL * 1024UL * 1024UL,
                RenderQualityPreset.Medium => 4UL * 1024UL * 1024UL,
                RenderQualityPreset.High => 12UL * 1024UL * 1024UL,
                RenderQualityPreset.DdgiHigh => 16UL * 1024UL * 1024UL,
                RenderQualityPreset.Ultra => MaterialManager.MaximumPrimitiveProfileGpuBytes,
                _ => throw new ArgumentOutOfRangeException(nameof(qualityPreset))
            };

        public RenderBudgetSnapshot Evaluate(
            RenderBudgetProfile profile,
            RendererDiagnostics diagnostics,
            MemoryBudgetSnapshot memory,
            UploadBudgetSnapshot upload,
            RuntimeStallSnapshot stalls)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (memory == null)
                throw new ArgumentNullException(nameof(memory));
            if (upload == null)
                throw new ArgumentNullException(nameof(upload));
            if (stalls == null)
                throw new ArgumentNullException(nameof(stalls));

            bool hasActualGpuMemoryBudget = memory.HeapBudget.IsAvailable && memory.HeapBudget.PrimaryBudgetBytes > 0;
            ulong foliageMemoryBytes = diagnostics.FoliageInstanceBufferBytes +
                diagnostics.FoliageClusterBufferBytes +
                diagnostics.FoliageDrawBufferBytes +
                diagnostics.FoliageImpostorAtlasBytes;
            GiResidencySnapshot giResidency =
                GiResidencyReporter.Create(diagnostics, memory, profile);
            // The historical aggregate includes manager counters that can overlap tracked
            // allocations. Keep the legacy metric below for compatibility, but do not use it as
            // a release gate. Unique residency is reported from disjoint sources instead.
            ulong globalIlluminationMemoryBytes = giResidency.UniqueResidentBytes;
            ulong ddgiMemoryBytes = diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes;
            // Full DDGI stores atlases in images. Simple DDGI keeps its canonical
            // writer in SSBOs and may add a sampled-image mirror. V2 additionally
            // owns a private Jacobi target and persistent source-ray cache, all
            // charged against the same hard layout budget.
            ulong ddgiAtlasMemoryBytes = diagnostics.SimpleDdgiActive != 0
                ? SaturatingAdd(
                    SaturatingAdd(
                        diagnostics.SimpleDdgiAtlasBytes,
                        diagnostics.SimpleDdgiTransportIrradianceAtlasBytes),
                    diagnostics.SimpleDdgiTransportSourceCacheBytes)
                : diagnostics.DdgiTextureBytes;
            bool forwardGiRequired = diagnostics.GlobalIlluminationDdgiActive != 0 ||
                diagnostics.SimpleDdgiActive != 0;
            bool hasForwardGiIncrementalTiming = diagnostics.GpuForwardGiIncrementalAttribution is
                GiTimingAttribution.Exclusive or GiTimingAttribution.PairedEstimate;
            long globalIlluminationGpuMicroseconds = diagnostics.GpuSsgiTraceMicroseconds +
                diagnostics.GpuSsgiTemporalMicroseconds +
                diagnostics.GpuSsgiDenoiseMicroseconds +
                diagnostics.GpuDdgiUpdateMicroseconds +
                diagnostics.GpuGiCompositeMicroseconds +
                diagnostics.GpuFarFieldUpdateMicroseconds +
                diagnostics.GpuAccelerationStructureBlasMicroseconds +
                diagnostics.GpuAccelerationStructureTlasMicroseconds +
                (hasForwardGiIncrementalTiming ? diagnostics.GpuForwardGiIncrementalMicroseconds : 0);
            bool giGpuTimingComplete = diagnostics.GpuTimingValid != 0 &&
                (!forwardGiRequired || hasForwardGiIncrementalTiming);
            bool materialGiV2Active =
                diagnostics.MaterialGiV2ActiveFeatures != MaterialGiV2Feature.None;
            bool materialTransportV2Active =
                (diagnostics.MaterialGiV2ActiveFeatures &
                 MaterialGiV2Feature.MaterialTransport) != 0;
            bool emissiveMeshSamplingV2Active =
                (diagnostics.MaterialGiV2ActiveFeatures &
                 MaterialGiV2Feature.EmissiveMeshSampling) != 0;
            bool materialGiTimingApplicable =
                diagnostics.GlobalIlluminationEnabled != 0 &&
                materialTransportV2Active;
            RenderBudgetStatus? emissiveMeshSamplingGateStatus =
                diagnostics.GlobalIlluminationEnabled == 0 ||
                !emissiveMeshSamplingV2Active
                    ? RenderBudgetStatus.Unavailable
                    : null;
            double ddgiEmissiveTruncatedSourceCount = Math.Max(
                0.0,
                (double)diagnostics.DdgiEmissiveTriangleCandidateCount -
                Math.Max(diagnostics.DdgiEmissiveTriangleBudget, 0));
            int ddgiFullUpdateFailureThreshold = diagnostics.DdgiActiveProbeCount > 0
                ? Math.Max(0, diagnostics.DdgiActiveProbeCount - 1)
                : 0;
            BudgetMetric ddgiProbeMetric = diagnostics.SimpleDdgiActive != 0 &&
                diagnostics.DdgiMaxActiveProbeBudget > 0
                    ? CreateHardLimitMetric(
                        "DDGI probes",
                        diagnostics.DdgiProbeCount,
                        diagnostics.DdgiMaxActiveProbeBudget,
                        "count",
                        diagnostics.GlobalIlluminationEnabled == 0 ? RenderBudgetStatus.Unavailable : null)
                    : CreateMetric(
                        "DDGI probes",
                        diagnostics.DdgiProbeCount,
                        profile.DdgiProbeBudget,
                        "count",
                        diagnostics.GlobalIlluminationEnabled == 0 ? RenderBudgetStatus.Unavailable : null);
            var metrics = new List<BudgetMetric>(hasActualGpuMemoryBudget ? 40 : 39)
            {
                CreateMetric("CPU renderer", diagnostics.CpuTotalDrawSceneMicroseconds / 1000.0, profile.CpuFrameBudgetMilliseconds, "ms"),
                CreateMetric("GPU frame", diagnostics.GpuFrameMicroseconds / 1000.0, profile.GpuFrameBudgetMilliseconds, "ms",
                    diagnostics.GpuTimingValid == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateMetric(EffectiveGpuMemoryMetricName, memory.EffectiveMemoryBytes, memory.EffectiveBudgetBytes, "bytes"),
                CreateMetric("Upload", upload.TotalBytes, profile.UploadBudgetBytesPerFrame, "bytes"),
                CreateMetric("Objects", diagnostics.VisibleObjectCount, profile.ObjectBudget, "count"),
                CreateMetric("Meshlets", diagnostics.MeshletCountTotal + diagnostics.FoliageVisibleMeshletDrawCount, profile.MeshletBudget, "count"),
                CreateMetric("Foliage clusters", diagnostics.FoliageVisibleClusterCount, profile.FoliageClusterBudget, "count"),
                CreateMetric("Foliage meshlet draws", diagnostics.FoliageVisibleMeshletDrawCount, profile.FoliageMeshletDrawBudget, "count"),
                CreateMetric("Foliage grass blades", diagnostics.FoliageGrassBladeEstimate, profile.FoliageGrassBladeBudget, "count"),
                CreateMetric("Foliage memory", foliageMemoryBytes, profile.FoliageMemoryBudgetBytes, "bytes"),
                CreateMetric("Materials", diagnostics.MaterialCount, profile.MaterialBudget, "count"),
                CreateMetric(
                    MaterialGiPrimitiveProfileMemoryMetricName,
                    diagnostics.MaterialPrimitiveProfileGpuBytes,
                    ResolvePrimitiveProfileMemoryBudgetBytes(diagnostics.ActiveQualityPreset),
                    "bytes",
                    !materialTransportV2Active ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("Material GI non-finite values", diagnostics.MaterialNonFiniteValueCount, 0, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("Material GI clamped values", diagnostics.MaterialClampedValueCount, 0, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("Material alpha candidate limit", diagnostics.MaterialAlphaCandidateLimitReachedCount, 0, "rays",
                    diagnostics.GlobalIlluminationEnabled == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric(
                    MaterialGiQualificationMetricName,
                    diagnostics.MaterialGiReleaseQualificationFailureCount,
                    0,
                    "failures",
                    !materialGiV2Active ||
                    diagnostics.MaterialGiReleaseQualificationRequired == 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateHardLimitMetric(
                    MaterialGiActiveV1FallbackMetricName,
                    diagnostics.MaterialActiveLegacyV1FallbackCount,
                    0,
                    "materials",
                    !materialTransportV2Active
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateHardLimitMetric(
                    MaterialGiActiveInvalidProfileMetricName,
                    diagnostics.MaterialActiveInvalidProfileCount,
                    0,
                    "materials",
                    !materialTransportV2Active
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateZeroGateMetric(
                    DdgiEmissiveTruncatedSourceMetricName,
                    ddgiEmissiveTruncatedSourceCount,
                    "sources",
                    emissiveMeshSamplingGateStatus),
                CreateZeroGateMetric(
                    DdgiEmissiveSkippedEnergyMetricName,
                    diagnostics.DdgiEmissiveSkippedEnergyFraction,
                    "fraction",
                    emissiveMeshSamplingGateStatus),
                CreateZeroGateMetric(
                    DdgiEmissiveUnsupportedSkinnedObjectMetricName,
                    diagnostics.DdgiEmissiveSkippedSkinnedObjectCount,
                    "objects",
                    emissiveMeshSamplingGateStatus),
                CreateZeroGateMetric(
                    DdgiEmissiveUnsupportedSkinnedImportanceMetricName,
                    diagnostics.DdgiEmissiveSkippedSkinnedImportance,
                    "importance",
                    emissiveMeshSamplingGateStatus),
                CreateMetric(
                    MaterialGiCompileP95MetricName,
                    diagnostics.MaterialCompileP95Microseconds / 1000.0,
                    profile.GlobalIlluminationCpuBudgetMilliseconds,
                    "ms",
                    !materialGiTimingApplicable ||
                    diagnostics.MaterialCompileTimingSampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateMetric(
                    MaterialGiUploadP95MetricName,
                    diagnostics.MaterialUploadP95Microseconds / 1000.0,
                    profile.GlobalIlluminationCpuBudgetMilliseconds,
                    "ms",
                    !materialGiTimingApplicable ||
                    diagnostics.MaterialUploadTimingSampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateMetric(
                    MaterialGiPipelineP95MetricName,
                    (diagnostics.MaterialCompileP95Microseconds +
                     diagnostics.MaterialUploadP95Microseconds) / 1000.0,
                    profile.GlobalIlluminationCpuBudgetMilliseconds,
                    "ms",
                    !materialGiTimingApplicable ||
                    diagnostics.MaterialCompileTimingSampleCount <= 0 ||
                    diagnostics.MaterialUploadTimingSampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateMetric("Textures", diagnostics.TextureCount, profile.TextureBudget, "count"),
                CreateMetric("Lights", diagnostics.LightCount, profile.LightBudget, "count"),
                CreateMetric("Shadowed lights", diagnostics.SpotShadowSelectedCount + diagnostics.PointShadowSelectedCount + (diagnostics.ShadowedDirectionalLightIndex >= 0 ? 1 : 0), profile.ShadowedLightBudget, "count"),
                CreateMetric("Reflection probes", diagnostics.ReflectionProbeCount, profile.ReflectionProbeBudget, "count"),
                CreateMetric("GI GPU", globalIlluminationGpuMicroseconds / 1000.0, profile.GlobalIlluminationGpuBudgetMilliseconds, "ms",
                    diagnostics.GlobalIlluminationEnabled == 0 || !giGpuTimingComplete ? RenderBudgetStatus.Unavailable : null),
                // This is explicitly informational. A whole forward-draw timestamp contains
                // non-GI fragment work and must never be charged to the incremental GI budget.
                CreateMetric("GI forward gather (inclusive draw)", diagnostics.GpuForwardGiGatherMicroseconds / 1000.0, profile.GlobalIlluminationGpuBudgetMilliseconds, "ms",
                    RenderBudgetStatus.Unavailable),
                CreateMetric("GI forward gather incremental", diagnostics.GpuForwardGiIncrementalMicroseconds / 1000.0, profile.GlobalIlluminationGpuBudgetMilliseconds, "ms",
                    !forwardGiRequired || diagnostics.GpuTimingValid == 0 || !hasForwardGiIncrementalTiming ? RenderBudgetStatus.Unavailable : null),
                CreateMetric("GI CPU scheduling and upload", diagnostics.CpuGlobalIlluminationRecordP95Microseconds / 1000.0, profile.GlobalIlluminationCpuBudgetMilliseconds, "ms",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.GlobalIlluminationCpuTimingSampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateHardLimitMetric("DDGI dirty first-update latency", diagnostics.SimpleDdgiDirtyFirstUpdateLatencyP95Frames, 1, "frames",
                    diagnostics.SimpleDdgiActive == 0 || diagnostics.SimpleDdgiDirtyFirstUpdateLatencySampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateHardLimitMetric("DDGI dirty convergence latency", diagnostics.SimpleDdgiDirtyConvergenceLatencyP95Frames, 8, "frames",
                    diagnostics.SimpleDdgiActive == 0 || diagnostics.SimpleDdgiDirtyConvergenceLatencySampleCount <= 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                // GlobalIlluminationMemoryBudgetBytes predates component budgets and is not a
                // valid aggregate cap for cache, acceleration structures, and scratch together.
                CreateMetric("GI memory", globalIlluminationMemoryBytes, profile.GlobalIlluminationMemoryBudgetBytes, "bytes",
                    RenderBudgetStatus.Unavailable),
                CreateMetric("GI unique residency", giResidency.UniqueResidentBytes, giResidency.DeclaredComponentBudgetBytes, "bytes",
                    diagnostics.GlobalIlluminationEnabled == 0 ||
                    !giResidency.UniqueMeasurementAvailable ||
                    giResidency.DeclaredComponentBudgetBytes == 0
                        ? RenderBudgetStatus.Unavailable
                        : null),
                CreateHardLimitMetric("GI resident acceleration structures", diagnostics.AccelerationStructureResidentBytes, diagnostics.AccelerationStructureMemoryBudgetBytes, "bytes",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.AccelerationStructureMemoryBudgetBytes == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("Far-field page cache", diagnostics.FarFieldCacheBytes, diagnostics.FarFieldMemoryBudgetBytes, "bytes",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.FarFieldMemoryBudgetBytes == 0 ? RenderBudgetStatus.Unavailable : null),
                ddgiProbeMetric,
                CreateHardLimitMetric("DDGI active probe budget", diagnostics.DdgiActiveProbeCount, diagnostics.DdgiMaxActiveProbeBudget, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiMaxActiveProbeBudget <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("DDGI update request budget", diagnostics.DdgiProbesUpdated, diagnostics.DdgiProbeUpdateRequestBudget, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiProbeUpdateRequestBudget <= 0 || diagnostics.DdgiProbesUpdated <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("DDGI atlas memory", ddgiAtlasMemoryBytes, diagnostics.DdgiAtlasMemoryBudgetBytes, "bytes",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiAtlasMemoryBudgetBytes <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("DDGI total memory", ddgiMemoryBytes, diagnostics.DdgiAtlasMemoryBudgetBytes, "bytes",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiAtlasMemoryBudgetBytes <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("SSGI resolution scale", diagnostics.SsgiResolutionScale, MaxDefaultSsgiResolutionScale, "scale",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.SsgiRayCount <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("SSGI rays per pixel", diagnostics.SsgiRayCount, MaxDefaultSsgiRayCount, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.SsgiRayCount <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("DDGI probes updated", diagnostics.DdgiProbesUpdated, ddgiFullUpdateFailureThreshold, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiActiveProbeCount <= 0 || diagnostics.DdgiProbesUpdated <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateHardLimitMetric("DDGI resource reinitializations", diagnostics.DdgiResourceReinitializationCount, 0, "count",
                    ShouldEvaluateDdgiCameraMovementReinitialization(diagnostics) ? null : RenderBudgetStatus.Unavailable),
                CreateHardLimitMetric("DDGI gather fallback tiles", diagnostics.DdgiGatherFallbackTileCount, 0, "count",
                    diagnostics.GlobalIlluminationEnabled == 0 || diagnostics.DdgiGatherTileCount <= 0 ? RenderBudgetStatus.Unavailable : null),
                CreateMetric("Transparent objects", diagnostics.TransparentObjectCount, profile.TransparentObjectBudget, "count")
            };

            if (hasActualGpuMemoryBudget)
                metrics.Add(CreateMetric(TrackedGpuMemoryMetricName, memory.TotalTrackedBytes, profile.GpuMemoryBudgetBytes, "bytes"));

            RenderBudgetStatus overall = Combine(metrics);
            return new RenderBudgetSnapshot(profile, metrics, memory, upload, stalls, overall)
            {
                GiResidency = giResidency
            };
        }

        private static bool ShouldEvaluateDdgiCameraMovementReinitialization(RendererDiagnostics diagnostics)
        {
            return diagnostics.GlobalIlluminationEnabled != 0 &&
                   diagnostics.DdgiCascadeCount > 0 &&
                   (diagnostics.DdgiCameraMovementClass is DdgiCameraMovementClass.Normal or
                       DdgiCameraMovementClass.Fast or
                       DdgiCameraMovementClass.ViewResetOnly);
        }

        private static BudgetMetric CreateMetric(
            string name,
            double value,
            double failureThreshold,
            string unit,
            RenderBudgetStatus? forcedStatus = null)
        {
            return new BudgetMetric(
                name,
                value,
                failureThreshold * WarningRatio,
                failureThreshold,
                unit,
                forcedStatus ?? Classify(value, failureThreshold));
        }

        private static BudgetMetric CreateHardLimitMetric(
            string name,
            double value,
            double failureThreshold,
            string unit,
            RenderBudgetStatus? forcedStatus = null)
        {
            return new BudgetMetric(
                name,
                value,
                failureThreshold,
                failureThreshold,
                unit,
                forcedStatus ?? ClassifyHardLimit(value, failureThreshold));
        }

        private static BudgetMetric CreateZeroGateMetric(
            string name,
            double value,
            string unit,
            RenderBudgetStatus? forcedStatus = null)
        {
            double reportedValue = double.IsFinite(value) &&
                Math.Abs(value) <= ZeroGateNumericalTolerance
                    ? 0.0
                    : value;
            RenderBudgetStatus status = forcedStatus ??
                (double.IsFinite(reportedValue) && reportedValue == 0.0
                    ? RenderBudgetStatus.WithinBudget
                    : RenderBudgetStatus.OverBudget);
            return new BudgetMetric(name, reportedValue, 0.0, 0.0, unit, status);
        }

        private static RenderBudgetStatus ClassifyHardLimit(double value, double failureThreshold)
        {
            if (double.IsNaN(value) || double.IsNaN(failureThreshold))
                return RenderBudgetStatus.Unavailable;
            if (double.IsPositiveInfinity(failureThreshold))
                return RenderBudgetStatus.WithinBudget;
            if (failureThreshold < 0)
                return RenderBudgetStatus.Unavailable;
            return value > failureThreshold ? RenderBudgetStatus.OverBudget : RenderBudgetStatus.WithinBudget;
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private static RenderBudgetStatus Combine(IReadOnlyList<BudgetMetric> metrics)
        {
            bool sawWarning = false;
            bool sawAvailable = false;
            foreach (BudgetMetric metric in metrics)
            {
                if (metric.Status == RenderBudgetStatus.OverBudget)
                    return RenderBudgetStatus.OverBudget;
                if (metric.Status == RenderBudgetStatus.Warning)
                    sawWarning = true;
                if (metric.Status is RenderBudgetStatus.WithinBudget or RenderBudgetStatus.Warning)
                    sawAvailable = true;
            }

            if (sawWarning)
                return RenderBudgetStatus.Warning;
            return sawAvailable ? RenderBudgetStatus.WithinBudget : RenderBudgetStatus.Unavailable;
        }
    }
}
