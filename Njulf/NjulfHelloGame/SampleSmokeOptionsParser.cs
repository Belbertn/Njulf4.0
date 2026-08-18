using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

public static class SampleSmokeOptionsParser
{
    private static readonly HashSet<string> KnownOptions = new(
        StringComparer.Ordinal)
    {
        "--smoke-frames",
        "--smoke-mode",
        "--scene-reloads",
        "--scene",
        "--performance-scenario",
        "--transparency-mode",
        "--health-report",
        "--baseline-snapshot-dir",
        "--sponza-gi-capture-dir",
        "--sponza-gi-capture-mode",
        "--bistro-quality-capture-dir",
        "--bistro-quality-variant",
        "--material-gi-capture-dir",
        "--material-gi-qualification-manifest",
        "--material-gi-qualification-candidate",
        "--advanced-gi-prerequisite-manifest",
        "--advanced-gi-qualification-manifest",
        "--advanced-gi-runtime-evidence-bundle",
        "--advanced-gi-startup-profile",
        "--simple-ddgi-receiver-feedback-mode",
        "--ddgi-opacity-micromap-mode",
        "--simple-ddgi-directional-guiding-mode",
        "--gi-caustic-mode",
        "--simple-ddgi-near-field-residual-mode",
        "--simple-ddgi-receiver-feedback-qualification-id",
        "--ddgi-opacity-micromap-qualification-id",
        "--simple-ddgi-directional-guiding-qualification-id",
        "--gi-caustic-qualification-id",
        "--simple-ddgi-near-field-residual-qualification-id",
        "--khronos-material-gi-render-manifest",
        "--khronos-material-gi-gate-report",
        "--khronos-material-gi-cooked-root",
        "--khronos-material-gi-render-capture",
        "--khronos-material-gi-render-report",
        "--quality-preset",
        "--long-run-report",
        "--long-run-warmup-frames",
        "--long-run-sample-interval",
        "--long-run-max-samples",
        "--long-run-memory-growth-tolerance-bytes",
        "--long-run-minutes",
        "--tail-ddgi-long-soak",
        "--benchmark",
        "--benchmark-report",
        "--benchmark-warmup-frames",
        "--benchmark-measure-frames",
        "--benchmark-max-settle-frames",
        "--benchmark-budget-profile",
        "--benchmark-pair-id",
        "--benchmark-variant",
        "--benchmark-require-production",
        "--benchmark-hdr-reference",
        "--benchmark-hdr-candidate",
        "--benchmark-hdr-max-relative-rmse",
        "--benchmark-shader-profile",
        "--benchmark-require-shader-profile",
        "--startup-log",
        "--validation",
        "--force-missing-assets",
        "--fail-on-validation-message",
        "--gpu-timing",
        "--gpu-meshlet-counters",
        "--ddgi-content-conformance",
        "--scene-gpu-compaction",
        "--scene-indirect-dispatch",
        "--scene-gpu-lod",
        "--scene-gpu-shadow-compaction",
        "--scene-submission-validation",
        "--async-compute",
        "--async-compute-mode",
        "--async-compute-path",
        "--simple-ddgi-scheduler-mode",
        "--simple-ddgi-residency-mode",
        "--simple-ddgi-sparse-page-budget",
        "--simple-ddgi-sparse-min-page-budget",
        "--simple-ddgi-sparse-retention-frames",
        "--simple-ddgi-sparse-max-admissions",
        "--simple-ddgi-sparse-max-feedback",
        "--simple-ddgi-sparse-inactive-retry-frames",
        "--simple-ddgi-storage-mode",
        "--simple-ddgi-mirror-coverage",
        "--far-field-clipmap",
        "--far-field-force-all"
    };

    public static SampleSmokeOptions Parse(string[] args)
    {
        if (args == null)
            throw new ArgumentNullException(nameof(args));

        string? smokeModeEnvironment = Environment.GetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE");
        bool smokeModeSpecified = !string.IsNullOrWhiteSpace(smokeModeEnvironment);
        bool sceneSpecified = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE"));
        SampleSmokeMode mode = ParseMode(smokeModeEnvironment, SampleSmokeMode.None);
        int frameCount = ParsePositiveInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES"), 0, "NJULF_RENDERER_SMOKE_FRAMES");
        int sceneReloadCount = ParsePositiveInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_RELOAD_COUNT"), 1, "NJULF_RENDERER_SCENE_RELOAD_COUNT");
        SampleSceneKind sceneKind = ParseSceneKind(Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE"));
        SamplePerformanceScenario performanceScenario = ParsePerformanceScenario(Environment.GetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO"));
        TransparencyMode transparencyMode = ParseTransparencyMode(Environment.GetEnvironmentVariable("NJULF_RENDERER_TRANSPARENCY_MODE"));
        string? startupLogPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_STARTUP_LOG"));
        string? healthReportPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_HEALTH_REPORT"));
        string? baselineSnapshotDirectory = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR"));
        string? sponzaGiCaptureDirectory = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_SPONZA_GI_CAPTURE_DIR"));
        SampleSponzaGiCaptureMode sponzaGiCaptureMode =
            SampleSponzaGiCaptureMode.DetailedDiagnostics;
        bool sponzaGiCaptureModeSpecified = false;
        string? bistroQualityCaptureDirectory =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_BISTRO_QUALITY_CAPTURE_DIR"));
        SampleBistroQualityCaptureVariant bistroQualityCaptureVariant =
            ParseBistroQualityCaptureVariant(
                Environment.GetEnvironmentVariable(
                    "NJULF_BISTRO_QUALITY_VARIANT"));
        bool bistroQualityVariantSpecified = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                "NJULF_BISTRO_QUALITY_VARIANT"));
        string? materialGiCaptureDirectory = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_MATERIAL_GI_CAPTURE_DIR"));
        string? materialGiQualificationManifestPath = RendererValidationSettings.NormalizeOptionalPath(
            Environment.GetEnvironmentVariable("NJULF_MATERIAL_GI_QUALIFICATION_MANIFEST"));
        string? advancedGiPrerequisiteManifestPath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_PREREQUISITE_MANIFEST"));
        string? advancedGiQualificationManifestPath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_QUALIFICATION_MANIFEST"));
        string? advancedGiRuntimeEvidenceBundlePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_RUNTIME_EVIDENCE_BUNDLE"));
        string? advancedGiStartupProfilePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_ADVANCED_GI_STARTUP_PROFILE"));
        SimpleDdgiReceiverFeedbackMode? receiverFeedbackModeOverride =
            ParseOptionalEnum<SimpleDdgiReceiverFeedbackMode>(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_SIMPLE_DDGI_RECEIVER_FEEDBACK_MODE"),
                "NJULF_RENDERER_SIMPLE_DDGI_RECEIVER_FEEDBACK_MODE");
        DdgiOpacityMicromapMode? opacityMicromapModeOverride =
            ParseOptionalEnum<DdgiOpacityMicromapMode>(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_DDGI_OPACITY_MICROMAP_MODE"),
                "NJULF_RENDERER_DDGI_OPACITY_MICROMAP_MODE");
        SimpleDdgiDirectionalGuidingMode? directionalGuidingModeOverride =
            ParseOptionalEnum<SimpleDdgiDirectionalGuidingMode>(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_SIMPLE_DDGI_DIRECTIONAL_GUIDING_MODE"),
                "NJULF_RENDERER_SIMPLE_DDGI_DIRECTIONAL_GUIDING_MODE");
        GiCausticMode? giCausticModeOverride =
            ParseOptionalEnum<GiCausticMode>(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_GI_CAUSTIC_MODE"),
                "NJULF_RENDERER_GI_CAUSTIC_MODE");
        SimpleDdgiNearFieldResidualMode? nearFieldResidualModeOverride =
            ParseOptionalEnum<SimpleDdgiNearFieldResidualMode>(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_MODE"),
                "NJULF_RENDERER_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_MODE");
        string? receiverFeedbackQualificationId = ParseOptionalStableToken(
            Environment.GetEnvironmentVariable(
                "NJULF_SIMPLE_DDGI_RECEIVER_FEEDBACK_QUALIFICATION_ID"),
            "NJULF_SIMPLE_DDGI_RECEIVER_FEEDBACK_QUALIFICATION_ID");
        string? opacityMicromapQualificationId = ParseOptionalStableToken(
            Environment.GetEnvironmentVariable(
                "NJULF_DDGI_OPACITY_MICROMAP_QUALIFICATION_ID"),
            "NJULF_DDGI_OPACITY_MICROMAP_QUALIFICATION_ID");
        string? directionalGuidingQualificationId = ParseOptionalStableToken(
            Environment.GetEnvironmentVariable(
                "NJULF_SIMPLE_DDGI_DIRECTIONAL_GUIDING_QUALIFICATION_ID"),
            "NJULF_SIMPLE_DDGI_DIRECTIONAL_GUIDING_QUALIFICATION_ID");
        string? giCausticQualificationId = ParseOptionalStableToken(
            Environment.GetEnvironmentVariable(
                "NJULF_GI_CAUSTIC_QUALIFICATION_ID"),
            "NJULF_GI_CAUSTIC_QUALIFICATION_ID");
        string? nearFieldResidualQualificationId = ParseOptionalStableToken(
            Environment.GetEnvironmentVariable(
                "NJULF_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_QUALIFICATION_ID"),
            "NJULF_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_QUALIFICATION_ID");
        bool materialGiQualificationCandidate = ParseBool(
            Environment.GetEnvironmentVariable(
                "NJULF_MATERIAL_GI_QUALIFICATION_CANDIDATE"),
            "NJULF_MATERIAL_GI_QUALIFICATION_CANDIDATE");
        string? benchmarkReportPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REPORT"));
        string? longRunReportPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_REPORT"));
        string? khronosRenderManifestPath = null;
        string? khronosRenderGateReportPath = null;
        string? khronosRenderCookedRoot = null;
        string? khronosRenderCapturePath = null;
        string? khronosRenderReportPath = null;
        RenderQualityPreset? qualityPresetOverride = ParseQualityPreset(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_QUALITY_PRESET"));
        int longRunWarmupFrames = ParseNonNegativeInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_WARMUP_FRAMES"),
            120,
            "NJULF_RENDERER_LONG_RUN_WARMUP_FRAMES");
        bool longRunWarmupSpecified = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_LONG_RUN_WARMUP_FRAMES"));
        int longRunSampleInterval = ParsePositiveInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_SAMPLE_INTERVAL"),
            15,
            "NJULF_RENDERER_LONG_RUN_SAMPLE_INTERVAL");
        int longRunMaxRetainedSamples = ParsePositiveInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MAX_SAMPLES"),
            256,
            "NJULF_RENDERER_LONG_RUN_MAX_SAMPLES");
        ulong longRunMemoryGrowthToleranceBytes = ParseNonNegativeUlong(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MEMORY_GROWTH_TOLERANCE_BYTES"),
            1_048_576,
            "NJULF_RENDERER_LONG_RUN_MEMORY_GROWTH_TOLERANCE_BYTES");
        double longRunMinutes = ParseNonNegativeDouble(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MINUTES"),
            0.0,
            "NJULF_RENDERER_LONG_RUN_MINUTES");
        bool tailDdgiLongSoak = ParseBool(
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_TAIL_DDGI_LONG_SOAK"),
            "NJULF_RENDERER_TAIL_DDGI_LONG_SOAK");
        bool longRunOptionsSpecified =
            tailDdgiLongSoak ||
            !string.IsNullOrWhiteSpace(longRunReportPath) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_WARMUP_FRAMES")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_SAMPLE_INTERVAL")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MAX_SAMPLES")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MEMORY_GROWTH_TOLERANCE_BYTES")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MINUTES"));
        bool forceMissingAssets = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_FORCE_MISSING_ASSETS"),
            "NJULF_RENDERER_FORCE_MISSING_ASSETS");
        bool failOnValidationMessage = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE"),
            "NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE");
        bool enableGpuTiming = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING"),
            "NJULF_RENDERER_GPU_TIMING");
        bool gpuTimingSpecified = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING"));
        bool enableGpuMeshletCounters = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_GPU_MESHLET_COUNTERS"),
            "NJULF_RENDERER_GPU_MESHLET_COUNTERS");
        bool enableDdgiContentConformance = ParseBool(
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_DDGI_CONTENT_CONFORMANCE"),
            "NJULF_RENDERER_DDGI_CONTENT_CONFORMANCE");
        bool enableSceneGpuCompaction = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_COMPACTION"),
            "NJULF_RENDERER_SCENE_GPU_COMPACTION");
        bool enableSceneIndirectDispatch = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_INDIRECT_DISPATCH"),
            "NJULF_RENDERER_SCENE_INDIRECT_DISPATCH");
        bool enableSceneGpuLodSelection = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_LOD"),
            "NJULF_RENDERER_SCENE_GPU_LOD");
        bool enableSceneGpuShadowCompaction = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_SHADOW_COMPACTION"),
            "NJULF_RENDERER_SCENE_GPU_SHADOW_COMPACTION");
        bool enableSceneSubmissionValidation = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION"),
            "NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION");
        bool enableAsyncCompute = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE"),
            "NJULF_RENDERER_ASYNC_COMPUTE");
        AsyncComputeMode? asyncComputeModeOverride = ParseAsyncComputeMode(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE_MODE"));
        AsyncComputePath? asyncComputeValidationPath = ParseAsyncComputePath(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE_PATH"));
        SimpleDdgiSchedulerMode? simpleDdgiSchedulerModeOverride = ParseSimpleDdgiSchedulerMode(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SCHEDULER_MODE"));
        SimpleDdgiProbeResidencyMode? simpleDdgiProbeResidencyModeOverride =
            ParseSimpleDdgiProbeResidencyMode(Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_SIMPLE_DDGI_RESIDENCY_MODE"));
        int? simpleDdgiSparsePhysicalPageBudgetOverride = ParseOptionalNonNegativeInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_PAGE_BUDGET"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_PAGE_BUDGET");
        int? simpleDdgiSparseMinimumPhysicalPageBudgetOverride = ParseOptionalNonNegativeInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MIN_PAGE_BUDGET"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MIN_PAGE_BUDGET");
        int? simpleDdgiSparseRetentionFramesOverride = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_RETENTION_FRAMES"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_RETENTION_FRAMES");
        int? simpleDdgiSparseMaximumAdmissionsOverride = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_ADMISSIONS"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_ADMISSIONS");
        int? simpleDdgiSparseMaximumReceiverFeedbackOverride = ParseOptionalNonNegativeInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_FEEDBACK"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_FEEDBACK");
        int? simpleDdgiSparseInactiveRetryFramesOverride = ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SPARSE_INACTIVE_RETRY_FRAMES"),
            "NJULF_RENDERER_SIMPLE_DDGI_SPARSE_INACTIVE_RETRY_FRAMES");
        SimpleDdgiStoragePackingMode? simpleDdgiStoragePackingModeOverride =
            ParseSimpleDdgiStoragePackingMode(Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_SIMPLE_DDGI_STORAGE_MODE"));
        SimpleDdgiSampledAtlasCoverageMode? simpleDdgiSampledAtlasCoverageModeOverride =
            ParseSimpleDdgiSampledAtlasCoverageMode(Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_SIMPLE_DDGI_MIRROR_COVERAGE"));
        if (asyncComputeModeOverride.HasValue)
            enableAsyncCompute =
                asyncComputeModeOverride == AsyncComputeMode.ForceEnabledForValidation;
        else if (enableAsyncCompute)
            asyncComputeModeOverride = AsyncComputeMode.ForceEnabledForValidation;
        bool enableFarFieldClipmap = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_FAR_FIELD_CLIPMAP"),
            "NJULF_RENDERER_FAR_FIELD_CLIPMAP");
        bool enableFarFieldForceAll = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_FAR_FIELD_FORCE_ALL"),
            "NJULF_RENDERER_FAR_FIELD_FORCE_ALL");
        enableFarFieldClipmap |= enableFarFieldForceAll;
        bool enableBenchmark = ParseBool(
                Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK"),
                "NJULF_RENDERER_BENCHMARK") ||
            !string.IsNullOrWhiteSpace(benchmarkReportPath);
        int benchmarkWarmupFrames = ParseNonNegativeInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES"), 30, "NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES");
        int benchmarkMeasureFrames = ParsePositiveInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES"), 120, "NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES");
        int benchmarkMaximumSettlingFrames = ParsePositiveInt(
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_BENCHMARK_MAX_SETTLE_FRAMES"),
            SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount,
            "NJULF_RENDERER_BENCHMARK_MAX_SETTLE_FRAMES");
        string benchmarkPairId =
            Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_PAIR_ID")?.Trim() ??
            string.Empty;
        string benchmarkVariant =
            Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_VARIANT")?.Trim() ??
            "baseline";
        bool benchmarkRequireProduction = ParseBool(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REQUIRE_PRODUCTION"),
            "NJULF_RENDERER_BENCHMARK_REQUIRE_PRODUCTION");
        string benchmarkHdrReferencePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_BENCHMARK_HDR_REFERENCE")) ?? string.Empty;
        string benchmarkHdrCandidatePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_BENCHMARK_HDR_CANDIDATE")) ?? string.Empty;
        string? benchmarkHdrMaximumRelativeRmseEnvironment =
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_BENCHMARK_HDR_MAX_RELATIVE_RMSE");
        double benchmarkHdrMaximumRelativeRmse = ParseNonNegativeDouble(
            benchmarkHdrMaximumRelativeRmseEnvironment,
            SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse,
            "NJULF_RENDERER_BENCHMARK_HDR_MAX_RELATIVE_RMSE");
        string benchmarkShaderProfilePath =
            RendererValidationSettings.NormalizeOptionalPath(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_BENCHMARK_SHADER_PROFILE")) ?? string.Empty;
        bool benchmarkRequireShaderProfile = ParseBool(
            Environment.GetEnvironmentVariable(
                "NJULF_RENDERER_BENCHMARK_REQUIRE_SHADER_PROFILE"),
            "NJULF_RENDERER_BENCHMARK_REQUIRE_SHADER_PROFILE");
        RenderBudgetProfileKind? benchmarkBudgetProfile =
            ParseBenchmarkBudgetProfile(
                Environment.GetEnvironmentVariable(
                    "NJULF_RENDERER_BENCHMARK_BUDGET_PROFILE"));
        enableBenchmark |= benchmarkBudgetProfile.HasValue;
        enableBenchmark |= !string.IsNullOrWhiteSpace(benchmarkPairId);
        enableBenchmark |= !string.Equals(
            benchmarkVariant,
            SampleBenchmarkCaptureVariant.Baseline,
            StringComparison.OrdinalIgnoreCase);
        enableBenchmark |= benchmarkRequireProduction;
        enableBenchmark |= !string.IsNullOrWhiteSpace(benchmarkHdrReferencePath);
        enableBenchmark |= !string.IsNullOrWhiteSpace(benchmarkHdrCandidatePath);
        enableBenchmark |= !string.IsNullOrWhiteSpace(
            benchmarkHdrMaximumRelativeRmseEnvironment);
        enableBenchmark |= !string.IsNullOrWhiteSpace(benchmarkShaderProfilePath);
        enableBenchmark |= benchmarkRequireShaderProfile;

        string? validationEnvironment =
            Environment.GetEnvironmentVariable("NJULF_RENDERER_VALIDATION");
        bool validationSpecified = !string.IsNullOrWhiteSpace(validationEnvironment);
        if (!RendererValidationSettings.TryParseMode(
                validationEnvironment,
                out RendererValidationMode validationMode,
                out string? validationError))
        {
            throw new ArgumentException(validationError);
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string optionName = GetOptionName(arg);
            if (!KnownOptions.Contains(optionName))
            {
                throw new ArgumentException(
                    $"Unknown renderer option '{optionName}'.",
                    nameof(args));
            }

            string value = ReadValue(args, ref i);
            switch (optionName)
            {
                case "--smoke-frames":
                    frameCount = ParsePositiveInt(value, 0, "--smoke-frames");
                    break;
                case "--smoke-mode":
                    mode = ParseMode(value, SampleSmokeMode.None);
                    smokeModeSpecified = true;
                    break;
                case "--scene-reloads":
                    sceneReloadCount = ParsePositiveInt(value, 1, "--scene-reloads");
                    break;
                case "--scene":
                    sceneKind = ParseSceneKind(value);
                    sceneSpecified = true;
                    break;
                case "--performance-scenario":
                    performanceScenario = ParsePerformanceScenario(value);
                    break;
                case "--transparency-mode":
                    transparencyMode = ParseTransparencyMode(value);
                    break;
                case "--health-report":
                    healthReportPath = RequirePath(value, "--health-report");
                    break;
                case "--baseline-snapshot-dir":
                    baselineSnapshotDirectory = RequirePath(value, "--baseline-snapshot-dir");
                    break;
                case "--sponza-gi-capture-dir":
                    sponzaGiCaptureDirectory = RequirePath(value, "--sponza-gi-capture-dir");
                    break;
                case "--sponza-gi-capture-mode":
                    sponzaGiCaptureMode = ParseSponzaGiCaptureMode(value);
                    sponzaGiCaptureModeSpecified = true;
                    break;
                case "--bistro-quality-capture-dir":
                    bistroQualityCaptureDirectory =
                        RequirePath(value, optionName);
                    break;
                case "--bistro-quality-variant":
                    bistroQualityCaptureVariant =
                        ParseBistroQualityCaptureVariant(value);
                    bistroQualityVariantSpecified = true;
                    break;
                case "--material-gi-capture-dir":
                    materialGiCaptureDirectory = RequirePath(value, "--material-gi-capture-dir");
                    break;
                case "--material-gi-qualification-manifest":
                    materialGiQualificationManifestPath =
                        RequirePath(value, "--material-gi-qualification-manifest");
                    break;
                case "--material-gi-qualification-candidate":
                    materialGiQualificationCandidate =
                        ParseBool(value, optionName);
                    break;
                case "--advanced-gi-prerequisite-manifest":
                    advancedGiPrerequisiteManifestPath =
                        RequirePath(value, optionName);
                    break;
                case "--advanced-gi-qualification-manifest":
                    advancedGiQualificationManifestPath =
                        RequirePath(value, optionName);
                    break;
                case "--advanced-gi-runtime-evidence-bundle":
                    advancedGiRuntimeEvidenceBundlePath =
                        RequirePath(value, optionName);
                    break;
                case "--advanced-gi-startup-profile":
                    advancedGiStartupProfilePath =
                        RequirePath(value, optionName);
                    break;
                case "--simple-ddgi-receiver-feedback-mode":
                    receiverFeedbackModeOverride =
                        ParseRequiredEnum<SimpleDdgiReceiverFeedbackMode>(
                            value, optionName);
                    break;
                case "--ddgi-opacity-micromap-mode":
                    opacityMicromapModeOverride =
                        ParseRequiredEnum<DdgiOpacityMicromapMode>(
                            value, optionName);
                    break;
                case "--simple-ddgi-directional-guiding-mode":
                    directionalGuidingModeOverride =
                        ParseRequiredEnum<SimpleDdgiDirectionalGuidingMode>(
                            value, optionName);
                    break;
                case "--gi-caustic-mode":
                    giCausticModeOverride =
                        ParseRequiredEnum<GiCausticMode>(value, optionName);
                    break;
                case "--simple-ddgi-near-field-residual-mode":
                    nearFieldResidualModeOverride =
                        ParseRequiredEnum<SimpleDdgiNearFieldResidualMode>(
                            value, optionName);
                    break;
                case "--simple-ddgi-receiver-feedback-qualification-id":
                    receiverFeedbackQualificationId =
                        RequireStableToken(value, optionName);
                    break;
                case "--ddgi-opacity-micromap-qualification-id":
                    opacityMicromapQualificationId =
                        RequireStableToken(value, optionName);
                    break;
                case "--simple-ddgi-directional-guiding-qualification-id":
                    directionalGuidingQualificationId =
                        RequireStableToken(value, optionName);
                    break;
                case "--gi-caustic-qualification-id":
                    giCausticQualificationId =
                        RequireStableToken(value, optionName);
                    break;
                case "--simple-ddgi-near-field-residual-qualification-id":
                    nearFieldResidualQualificationId =
                        RequireStableToken(value, optionName);
                    break;
                case "--khronos-material-gi-render-manifest":
                    khronosRenderManifestPath =
                        RequirePath(value, "--khronos-material-gi-render-manifest");
                    break;
                case "--khronos-material-gi-gate-report":
                    khronosRenderGateReportPath =
                        RequirePath(value, "--khronos-material-gi-gate-report");
                    break;
                case "--khronos-material-gi-cooked-root":
                    khronosRenderCookedRoot =
                        RequirePath(value, "--khronos-material-gi-cooked-root");
                    break;
                case "--khronos-material-gi-render-capture":
                    khronosRenderCapturePath =
                        RequirePath(value, "--khronos-material-gi-render-capture");
                    break;
                case "--khronos-material-gi-render-report":
                    khronosRenderReportPath =
                        RequirePath(value, "--khronos-material-gi-render-report");
                    break;
                case "--quality-preset":
                    qualityPresetOverride = ParseQualityPreset(value) ??
                        throw new ArgumentException("--quality-preset requires low, medium, high, ultra, or ddgi-high.");
                    break;
                case "--long-run-report":
                    longRunReportPath = RequirePath(value, "--long-run-report");
                    longRunOptionsSpecified = true;
                    break;
                case "--long-run-warmup-frames":
                    longRunWarmupFrames = ParseNonNegativeInt(value, 120, "--long-run-warmup-frames");
                    longRunWarmupSpecified = true;
                    longRunOptionsSpecified = true;
                    break;
                case "--long-run-sample-interval":
                    longRunSampleInterval = ParsePositiveInt(value, 15, "--long-run-sample-interval");
                    longRunOptionsSpecified = true;
                    break;
                case "--long-run-max-samples":
                    longRunMaxRetainedSamples = ParsePositiveInt(value, 256, "--long-run-max-samples");
                    longRunOptionsSpecified = true;
                    break;
                case "--long-run-memory-growth-tolerance-bytes":
                    longRunMemoryGrowthToleranceBytes = ParseNonNegativeUlong(
                        value,
                        1_048_576,
                        "--long-run-memory-growth-tolerance-bytes");
                    longRunOptionsSpecified = true;
                    break;
                case "--long-run-minutes":
                    longRunMinutes = ParsePositiveDouble(value, 0.0, "--long-run-minutes");
                    longRunOptionsSpecified = true;
                    break;
                case "--tail-ddgi-long-soak":
                    tailDdgiLongSoak = ParseBool(value, optionName);
                    longRunOptionsSpecified |= tailDdgiLongSoak;
                    break;
                case "--benchmark":
                    enableBenchmark = ParseBool(value, optionName);
                    break;
                case "--benchmark-report":
                    benchmarkReportPath = RequirePath(value, "--benchmark-report");
                    enableBenchmark = true;
                    break;
                case "--benchmark-warmup-frames":
                    benchmarkWarmupFrames = ParseNonNegativeInt(value, 30, "--benchmark-warmup-frames");
                    break;
                case "--benchmark-measure-frames":
                    benchmarkMeasureFrames = ParsePositiveInt(value, 120, "--benchmark-measure-frames");
                    break;
                case "--benchmark-max-settle-frames":
                    benchmarkMaximumSettlingFrames = ParsePositiveInt(
                        value,
                        SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount,
                        "--benchmark-max-settle-frames");
                    break;
                case "--benchmark-budget-profile":
                    benchmarkBudgetProfile =
                        ParseBenchmarkBudgetProfile(value) ??
                        throw new ArgumentException(
                            "--benchmark-budget-profile requires low, medium, high, ultra, or stress.");
                    enableBenchmark = true;
                    break;
                case "--benchmark-pair-id":
                    benchmarkPairId = RequireNonEmptyValue(value, "--benchmark-pair-id");
                    enableBenchmark = true;
                    break;
                case "--benchmark-variant":
                    benchmarkVariant = RequireNonEmptyValue(value, "--benchmark-variant");
                    enableBenchmark = true;
                    break;
                case "--benchmark-require-production":
                    benchmarkRequireProduction = ParseBool(value, optionName);
                    enableBenchmark = true;
                    break;
                case "--benchmark-hdr-reference":
                    benchmarkHdrReferencePath = RequirePath(value, optionName);
                    enableBenchmark = true;
                    break;
                case "--benchmark-hdr-candidate":
                    benchmarkHdrCandidatePath = RequirePath(value, optionName);
                    enableBenchmark = true;
                    break;
                case "--benchmark-hdr-max-relative-rmse":
                    benchmarkHdrMaximumRelativeRmse = ParseNonNegativeDouble(
                        value,
                        SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse,
                        optionName);
                    enableBenchmark = true;
                    break;
                case "--benchmark-shader-profile":
                    benchmarkShaderProfilePath = RequirePath(value, optionName);
                    enableBenchmark = true;
                    break;
                case "--benchmark-require-shader-profile":
                    benchmarkRequireShaderProfile = ParseBool(value, optionName);
                    enableBenchmark = true;
                    break;
                case "--startup-log":
                    startupLogPath = RequirePath(value, "--startup-log");
                    break;
                case "--validation":
                    if (!RendererValidationSettings.TryParseMode(value, out validationMode, out validationError))
                        throw new ArgumentException(validationError);
                    validationSpecified = true;
                    break;
                case "--force-missing-assets":
                    forceMissingAssets = ParseBool(value, optionName);
                    break;
                case "--fail-on-validation-message":
                    failOnValidationMessage = ParseBool(value, optionName);
                    break;
                case "--gpu-timing":
                    enableGpuTiming = ParseBool(value, optionName);
                    gpuTimingSpecified = true;
                    break;
                case "--gpu-meshlet-counters":
                    enableGpuMeshletCounters = ParseBool(value, optionName);
                    break;
                case "--ddgi-content-conformance":
                    enableDdgiContentConformance = ParseBool(value, optionName);
                    break;
                case "--scene-gpu-compaction":
                    enableSceneGpuCompaction = ParseBool(value, optionName);
                    break;
                case "--scene-indirect-dispatch":
                    enableSceneIndirectDispatch = ParseBool(value, optionName);
                    break;
                case "--scene-gpu-lod":
                    enableSceneGpuLodSelection = ParseBool(value, optionName);
                    break;
                case "--scene-gpu-shadow-compaction":
                    enableSceneGpuShadowCompaction = ParseBool(value, optionName);
                    break;
                case "--scene-submission-validation":
                    enableSceneSubmissionValidation = ParseBool(value, optionName);
                    break;
                case "--async-compute":
                    enableAsyncCompute = ParseBool(value, optionName);
                    asyncComputeModeOverride = enableAsyncCompute
                        ? AsyncComputeMode.ForceEnabledForValidation
                        : null;
                    break;
                case "--async-compute-mode":
                    asyncComputeModeOverride = ParseAsyncComputeMode(value) ??
                        throw new ArgumentException("--async-compute-mode requires auto, disabled, or forced.");
                    enableAsyncCompute =
                        asyncComputeModeOverride == AsyncComputeMode.ForceEnabledForValidation;
                    break;
                case "--async-compute-path":
                    asyncComputeValidationPath = ParseAsyncComputePath(value) ??
                        throw new ArgumentException("--async-compute-path requires a known atomic async path.");
                    break;
                case "--simple-ddgi-scheduler-mode":
                    simpleDdgiSchedulerModeOverride = ParseSimpleDdgiSchedulerMode(value) ??
                        throw new ArgumentException(
                            "--simple-ddgi-scheduler-mode requires cpu-reference, gpu-mirror, or gpu-resident.");
                    break;
                case "--simple-ddgi-residency-mode":
                    simpleDdgiProbeResidencyModeOverride =
                        ParseSimpleDdgiProbeResidencyMode(value) ??
                        throw new ArgumentException(
                            "--simple-ddgi-residency-mode requires dense, shadow, or sparse-near-ring.");
                    break;
                case "--simple-ddgi-sparse-page-budget":
                    simpleDdgiSparsePhysicalPageBudgetOverride =
                        ParseNonNegativeInt(value, 0, optionName);
                    break;
                case "--simple-ddgi-sparse-min-page-budget":
                    simpleDdgiSparseMinimumPhysicalPageBudgetOverride =
                        ParseNonNegativeInt(value, 0, optionName);
                    break;
                case "--simple-ddgi-sparse-retention-frames":
                    simpleDdgiSparseRetentionFramesOverride =
                        ParsePositiveInt(value, 1, optionName);
                    break;
                case "--simple-ddgi-sparse-max-admissions":
                    simpleDdgiSparseMaximumAdmissionsOverride =
                        ParsePositiveInt(value, 1, optionName);
                    break;
                case "--simple-ddgi-sparse-max-feedback":
                    simpleDdgiSparseMaximumReceiverFeedbackOverride =
                        ParseNonNegativeInt(value, 0, optionName);
                    break;
                case "--simple-ddgi-sparse-inactive-retry-frames":
                    simpleDdgiSparseInactiveRetryFramesOverride =
                        ParsePositiveInt(value, 1, optionName);
                    break;
                case "--simple-ddgi-storage-mode":
                    simpleDdgiStoragePackingModeOverride =
                        ParseSimpleDdgiStoragePackingMode(value) ??
                        throw new ArgumentException(
                            "--simple-ddgi-storage-mode requires legacy, validate, or packed.");
                    break;
                case "--simple-ddgi-mirror-coverage":
                    simpleDdgiSampledAtlasCoverageModeOverride =
                        ParseSimpleDdgiSampledAtlasCoverageMode(value) ??
                        throw new ArgumentException(
                            "--simple-ddgi-mirror-coverage requires disabled, full-canonical, or receiver-relevant.");
                    break;
                case "--far-field-clipmap":
                    enableFarFieldClipmap = ParseBool(value, optionName);
                    break;
                case "--far-field-force-all":
                    enableFarFieldForceAll = ParseBool(value, optionName);
                    enableFarFieldClipmap |= enableFarFieldForceAll;
                    break;
            }
        }

        string?[] khronosRenderValues =
        [
            khronosRenderManifestPath,
            khronosRenderGateReportPath,
            khronosRenderCookedRoot,
            khronosRenderCapturePath,
            khronosRenderReportPath
        ];
        int khronosRenderValueCount =
            khronosRenderValues.Count(static value => !string.IsNullOrWhiteSpace(value));
        if (khronosRenderValueCount is > 0 and < 5)
        {
            throw new ArgumentException(
                "The Khronos material/GI rendered gate requires all five options: " +
                "--khronos-material-gi-render-manifest, --khronos-material-gi-gate-report, " +
                "--khronos-material-gi-cooked-root, --khronos-material-gi-render-capture, and " +
                "--khronos-material-gi-render-report.");
        }
        SampleKhronosMaterialGiRenderedGateOptions? khronosRenderedGate =
            khronosRenderValueCount == 5
                ? SampleKhronosMaterialGiRenderedGateOptions.Create(
                    khronosRenderManifestPath!,
                    khronosRenderGateReportPath!,
                    khronosRenderCookedRoot!,
                    khronosRenderCapturePath!,
                    khronosRenderReportPath!)
                : null;

        if (!string.IsNullOrWhiteSpace(materialGiQualificationManifestPath) &&
            (khronosRenderedGate is not null ||
             !string.IsNullOrWhiteSpace(materialGiCaptureDirectory)))
        {
            throw new ArgumentException(
                "A qualified shipping rollout cannot be combined with the non-shipping " +
                "material-GI conformance capture or Khronos rendered gate.");
        }

        if (materialGiQualificationCandidate &&
            !string.IsNullOrWhiteSpace(materialGiQualificationManifestPath))
        {
            throw new ArgumentException(
                "A non-shipping material-GI qualification candidate cannot be combined " +
                "with an already approved qualification manifest.");
        }
        if (materialGiQualificationCandidate)
        {
            if (!enableBenchmark)
            {
                throw new ArgumentException(
                    "--material-gi-qualification-candidate requires benchmark mode.");
            }
            if (!benchmarkBudgetProfile.HasValue)
            {
                throw new ArgumentException(
                    "--material-gi-qualification-candidate requires an explicit " +
                    "--benchmark-budget-profile tier.");
            }
            if (string.IsNullOrWhiteSpace(benchmarkReportPath))
            {
                throw new ArgumentException(
                    "--material-gi-qualification-candidate requires an explicit " +
                    "--benchmark-report path for durable evidence.");
            }
            if (benchmarkWarmupFrames != 30 || benchmarkMeasureFrames != 120)
            {
                throw new ArgumentException(
                    "Material-GI qualification-candidate evidence requires the locked " +
                    "30-frame warmup and 120-frame measurement interval.");
            }
            if (performanceScenario is not (
                    SamplePerformanceScenario.Normal or
                    SamplePerformanceScenario.GiSimpleDdgiFurnace))
            {
                throw new ArgumentException(
                    "Material-GI qualification-candidate evidence requires the " +
                    "GiSimpleDdgiFurnace performance scenario.");
            }
            if (qualityPresetOverride.HasValue &&
                qualityPresetOverride != RenderQualityPreset.DdgiHigh)
            {
                throw new ArgumentException(
                    "Material-GI qualification-candidate evidence requires the " +
                    "DDGI-high quality preset.");
            }

            performanceScenario =
                SamplePerformanceScenario.GiSimpleDdgiFurnace;
            qualityPresetOverride = RenderQualityPreset.DdgiHigh;
        }

        if (tailDdgiLongSoak)
        {
            if (enableBenchmark)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak owns the long-run frame sequence and cannot be combined with benchmark mode.");
            }
            if (mode is not (SampleSmokeMode.None or SampleSmokeMode.LongRun))
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires --smoke-mode long-run when a smoke mode is specified.");
            }
            if (sceneSpecified &&
                sceneKind != SampleSceneKind.GlobalIlluminationTest)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak owns the GlobalIlluminationTest scene.");
            }
            if (performanceScenario is not (
                    SamplePerformanceScenario.Normal or
                    SamplePerformanceScenario.GiSimpleDdgiFurnace))
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires the GiSimpleDdgiFurnace performance scenario.");
            }
            if (qualityPresetOverride.HasValue &&
                qualityPresetOverride != RenderQualityPreset.DdgiHigh)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires the DDGI-high quality preset.");
            }
            if (validationSpecified &&
                validationMode != RendererValidationMode.Off)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires --validation off for ShippingPerformance timing evidence.");
            }
            if (gpuTimingSpecified && !enableGpuTiming)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires GPU timing and cannot be combined with --gpu-timing=false.");
            }
            if (asyncComputeModeOverride is not null and
                not AsyncComputeMode.Disabled ||
                enableAsyncCompute ||
                asyncComputeValidationPath.HasValue)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires the canonical disabled async-compute mode.");
            }
            if (simpleDdgiSchedulerModeOverride.HasValue &&
                simpleDdgiSchedulerModeOverride !=
                    SimpleDdgiSchedulerMode.GpuResident)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires the gpu-resident Simple-DDGI scheduler.");
            }
            if (forceMissingAssets ||
                transparencyMode != TransparencyMode.SortedAlphaBlend ||
                enableGpuMeshletCounters ||
                enableSceneGpuCompaction ||
                enableSceneIndirectDispatch ||
                enableSceneGpuLodSelection ||
                enableSceneGpuShadowCompaction ||
                enableSceneSubmissionValidation ||
                enableFarFieldClipmap ||
                enableFarFieldForceAll)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak cannot be combined with renderer behavior overrides.");
            }
            if (!string.IsNullOrWhiteSpace(baselineSnapshotDirectory) ||
                !string.IsNullOrWhiteSpace(sponzaGiCaptureDirectory) ||
                !string.IsNullOrWhiteSpace(materialGiCaptureDirectory) ||
                !string.IsNullOrWhiteSpace(materialGiQualificationManifestPath) ||
                khronosRenderValueCount != 0)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak cannot be combined with another capture or qualification mode.");
            }
            if (string.IsNullOrWhiteSpace(longRunReportPath))
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak requires --long-run-report for durable evidence.");
            }
            if (frameCount > 0 &&
                frameCount < SampleTailDdgiLongSoakProfile.RequiredFrameCount)
            {
                throw new ArgumentException(
                    $"--tail-ddgi-long-soak requires at least {SampleTailDdgiLongSoakProfile.RequiredFrameCount} frames, or a duration-owned run with no frame cap.");
            }
            if (frameCount > 0 && longRunMinutes > 0.0)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak accepts either a frame count or --long-run-minutes, not both.");
            }
            if (longRunWarmupSpecified &&
                longRunWarmupFrames <
                    SampleTailDdgiLongSoakProfile.MinimumWarmupFrameCount)
            {
                throw new ArgumentException(
                    $"--tail-ddgi-long-soak requires at least {SampleTailDdgiLongSoakProfile.MinimumWarmupFrameCount} warmup frames so late driver residency is outside the measured memory window.");
            }
            if (longRunMemoryGrowthToleranceBytes >
                SampleTailDdgiLongSoakProfile.MaximumMemoryGrowthToleranceBytes)
            {
                throw new ArgumentException(
                    "--tail-ddgi-long-soak cannot loosen the one-MiB memory-growth tolerance.");
            }

            mode = SampleSmokeMode.LongRun;
            if (frameCount == 0 && longRunMinutes <= 0.0)
                frameCount = SampleTailDdgiLongSoakProfile.RequiredFrameCount;
            if (!longRunWarmupSpecified)
            {
                longRunWarmupFrames =
                    SampleTailDdgiLongSoakProfile.MinimumWarmupFrameCount;
            }
            sceneKind = SampleSceneKind.GlobalIlluminationTest;
            performanceScenario =
                SamplePerformanceScenario.GiSimpleDdgiFurnace;
            qualityPresetOverride = RenderQualityPreset.DdgiHigh;
            validationMode = RendererValidationMode.Off;
            enableGpuTiming = true;
            enableAsyncCompute = false;
            asyncComputeModeOverride = AsyncComputeMode.Disabled;
            simpleDdgiSchedulerModeOverride =
                SimpleDdgiSchedulerMode.GpuResident;
            longRunOptionsSpecified = true;
        }

        int standaloneCaptureModeCount =
            (!string.IsNullOrWhiteSpace(materialGiCaptureDirectory) ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(sponzaGiCaptureDirectory) ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(bistroQualityCaptureDirectory) ? 1 : 0);
        if (standaloneCaptureModeCount > 1)
        {
            throw new ArgumentException(
                "Material, Sponza, and Bistro quality capture directories are independent standalone modes and cannot be combined.");
        }

        if (sponzaGiCaptureModeSpecified &&
            string.IsNullOrWhiteSpace(sponzaGiCaptureDirectory))
        {
            throw new ArgumentException(
                "--sponza-gi-capture-mode requires --sponza-gi-capture-dir.");
        }

        if (bistroQualityVariantSpecified &&
            string.IsNullOrWhiteSpace(bistroQualityCaptureDirectory) &&
            performanceScenario !=
                SamplePerformanceScenario.BistroQualityMotionRelight)
        {
            throw new ArgumentException(
                "--bistro-quality-variant requires --bistro-quality-capture-dir or the BistroQualityMotionRelight performance scenario.");
        }

        if (khronosRenderedGate is not null)
        {
            if (!string.IsNullOrWhiteSpace(materialGiCaptureDirectory) ||
                !string.IsNullOrWhiteSpace(sponzaGiCaptureDirectory) ||
                !string.IsNullOrWhiteSpace(baselineSnapshotDirectory))
            {
                throw new ArgumentException(
                    "The Khronos material/GI rendered gate cannot be combined with another capture mode.");
            }
            if (sceneSpecified && sceneKind != SampleSceneKind.MaterialShowcase)
            {
                throw new ArgumentException(
                    "The Khronos material/GI rendered gate owns the MaterialShowcase scene.");
            }
            if (performanceScenario != SamplePerformanceScenario.Normal ||
                mode != SampleSmokeMode.None ||
                frameCount > 0 ||
                enableBenchmark ||
                qualityPresetOverride.HasValue ||
                longRunOptionsSpecified)
            {
                throw new ArgumentException(
                    "The Khronos material/GI rendered gate owns its scene, quality, and deterministic frame sequence.");
            }
            if (forceMissingAssets ||
                transparencyMode != TransparencyMode.SortedAlphaBlend ||
                asyncComputeModeOverride.HasValue ||
                enableGpuMeshletCounters ||
                enableFarFieldClipmap ||
                enableFarFieldForceAll ||
                enableSceneGpuCompaction ||
                enableSceneIndirectDispatch ||
                enableSceneGpuLodSelection ||
                enableSceneGpuShadowCompaction ||
                enableSceneSubmissionValidation)
            {
                throw new ArgumentException(
                    "The Khronos material/GI rendered gate cannot be combined with renderer behavior overrides.");
            }
            if (validationSpecified && validationMode == RendererValidationMode.Off)
            {
                throw new ArgumentException(
                    "The Khronos material/GI rendered gate requires Vulkan validation; validation cannot be off.");
            }

            sceneKind = SampleSceneKind.MaterialShowcase;
            performanceScenario = SamplePerformanceScenario.Normal;
            validationMode = validationMode == RendererValidationMode.Off
                ? RendererValidationMode.Standard
                : validationMode;
            // The gate owns validation failure handling so it can atomically
            // replace its InProgress report before requesting shutdown.
            failOnValidationMessage = false;
            enableGpuTiming = true;
            enableAsyncCompute = false;
            asyncComputeModeOverride = AsyncComputeMode.Disabled;
        }
        else if (!string.IsNullOrWhiteSpace(materialGiCaptureDirectory))
        {
            if (!string.IsNullOrWhiteSpace(baselineSnapshotDirectory))
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir emits its own evidence manifest and cannot be combined with --baseline-snapshot-dir.");
            }
            if (sceneSpecified && sceneKind != SampleSceneKind.MaterialShowcase)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir requires the MaterialShowcase scene; do not combine it with another --scene value.");
            }
            if (performanceScenario != SamplePerformanceScenario.Normal)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir owns the material conformance scene and cannot be combined with --performance-scenario.");
            }
            if (mode != SampleSmokeMode.None || frameCount > 0)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir owns its deterministic frame sequence and cannot be combined with smoke mode or --smoke-frames.");
            }
            if (enableBenchmark)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir cannot be combined with benchmark mode.");
            }
            if (qualityPresetOverride.HasValue)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir owns its render settings and cannot be combined with --quality-preset.");
            }
            if (longRunOptionsSpecified)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir owns its frame sequence and cannot be combined with long-run options.");
            }
            if (asyncComputeModeOverride == AsyncComputeMode.Auto)
            {
                throw new ArgumentException(
                    "--material-gi-capture-dir requires an explicit --async-compute-mode disabled or forced value; auto is not reproducible evidence.");
            }

            sceneKind = SampleSceneKind.MaterialShowcase;
            performanceScenario = SamplePerformanceScenario.Normal;
            enableGpuTiming = true;
        }
        else if (!string.IsNullOrWhiteSpace(sponzaGiCaptureDirectory))
        {
            if (!string.IsNullOrWhiteSpace(baselineSnapshotDirectory))
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir already emits endpoint snapshots and cannot be combined with --baseline-snapshot-dir.");
            }
            if (qualityPresetOverride.HasValue)
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir owns its render settings and cannot be combined with --quality-preset.");
            }
            if (longRunOptionsSpecified)
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir owns its frame sequence and cannot be combined with long-run options.");
            }
            if (sceneSpecified && sceneKind != SampleSceneKind.SponzaPlaza)
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir requires the Sponza plaza scene; do not combine it with another --scene value.");
            }
            if (performanceScenario is not (SamplePerformanceScenario.Normal or SamplePerformanceScenario.GiSponzaRightWallStationary))
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir requires the stationary Sponza GI scenario; do not combine it with another --performance-scenario value.");
            }
            if (mode != SampleSmokeMode.None || frameCount > 0)
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir owns its deterministic frame sequence and cannot be combined with smoke mode or --smoke-frames.");
            }
            if (enableBenchmark)
            {
                throw new ArgumentException(
                    "--sponza-gi-capture-dir owns its deterministic frame sequence and cannot be combined with benchmark mode.");
            }

            sceneKind = SampleSceneKind.SponzaPlaza;
            performanceScenario = SamplePerformanceScenario.GiSponzaRightWallStationary;
            enableGpuTiming = true;
        }
        else if (!string.IsNullOrWhiteSpace(bistroQualityCaptureDirectory))
        {
            if (!string.IsNullOrWhiteSpace(baselineSnapshotDirectory))
            {
                throw new ArgumentException(
                    "--bistro-quality-capture-dir emits its own evidence and cannot be combined with --baseline-snapshot-dir.");
            }
            if (sceneSpecified && sceneKind != SampleSceneKind.Bistro)
            {
                throw new ArgumentException(
                    "--bistro-quality-capture-dir requires the Bistro scene.");
            }
            if (performanceScenario is not (
                    SamplePerformanceScenario.Normal or
                    SamplePerformanceScenario.BistroQualityMotionRelight))
            {
                throw new ArgumentException(
                    "--bistro-quality-capture-dir owns the BistroQualityMotionRelight scenario.");
            }
            if (mode != SampleSmokeMode.None || frameCount > 0 ||
                enableBenchmark || longRunOptionsSpecified)
            {
                throw new ArgumentException(
                    "--bistro-quality-capture-dir owns its deterministic frame sequence and cannot be combined with smoke, benchmark, or long-run modes.");
            }

            sceneKind = SampleSceneKind.Bistro;
            performanceScenario =
                SamplePerformanceScenario.BistroQualityMotionRelight;
            enableGpuTiming = true;
        }
        else
        {
            if (performanceScenario == SamplePerformanceScenario.GiSponzaRightWallStationary ||
                SampleSponzaAtmosphereScenario.IsScenario(performanceScenario))
            {
                if (sceneSpecified && sceneKind != SampleSceneKind.SponzaPlaza)
                {
                    throw new ArgumentException(
                        "Sponza atmosphere/reflection scenario requires the Sponza plaza scene.");
                }

                sceneKind = SampleSceneKind.SponzaPlaza;
            }

            if (performanceScenario ==
                SamplePerformanceScenario.BistroQualityMotionRelight)
            {
                if (sceneSpecified && sceneKind != SampleSceneKind.Bistro)
                {
                    throw new ArgumentException(
                        "BistroQualityMotionRelight requires the Bistro scene.");
                }
                sceneKind = SampleSceneKind.Bistro;
            }

            if (enableBenchmark)
            {
                if (mode != SampleSmokeMode.None || frameCount > 0 || longRunOptionsSpecified)
                {
                    throw new ArgumentException(
                        "Benchmark mode owns its warmup and measurement frame sequence and cannot be combined with smoke or long-run frames.");
                }

                // Scene, quality, scheduler, and async overrides describe the
                // benchmark workload. They must not implicitly arm the
                // three-frame startup runner, which would terminate before the
                // benchmark can publish a report.
                mode = SampleSmokeMode.None;
                frameCount = 0;
                if (!validationSpecified)
                    validationMode = RendererValidationMode.Off;
                // Async auto-promotion is an adaptive experiment, not a locked
                // workload: its profitability history can choose a different
                // queue plan in otherwise identical processes. Phase 8 keeps it
                // out of the canonical benchmark unless the caller explicitly
                // names an async mode for a controlled A/B capture.
                if (!asyncComputeModeOverride.HasValue)
                {
                    asyncComputeModeOverride = AsyncComputeMode.Disabled;
                    enableAsyncCompute = false;
                }
                if (benchmarkRequireProduction &&
                    validationMode != RendererValidationMode.Off)
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires --validation off.");
                }
                if (benchmarkRequireProduction && enableDdgiContentConformance)
                {
                    throw new ArgumentException(
                        "--benchmark-require-production cannot use the non-shipping --ddgi-content-conformance rollout authorization.");
                }
                if (benchmarkRequireProduction &&
                    benchmarkMaximumSettlingFrames <
                    SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount)
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires " +
                        $"--benchmark-max-settle-frames >= " +
                        $"{SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount}.");
                }
                if (benchmarkRequireProduction &&
                    RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled)
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires a Release, ShippingPerformance, or ProfileSymbols build whose DDGI diagnostic atomics are compiled out.");
                }
                if (benchmarkRequireProduction &&
                    string.IsNullOrWhiteSpace(benchmarkHdrReferencePath))
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires --benchmark-hdr-reference so the post-measurement HDR image gate cannot be omitted.");
                }
                if (benchmarkRequireProduction &&
                    string.IsNullOrWhiteSpace(benchmarkPairId))
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires --benchmark-pair-id for controlled repeats and A/B captures.");
                }
                if (benchmarkRequireProduction && benchmarkMeasureFrames < 120)
                {
                    throw new ArgumentException(
                        "--benchmark-require-production requires at least 120 measurement frames.");
                }
                if (benchmarkRequireShaderProfile &&
                    string.IsNullOrWhiteSpace(benchmarkShaderProfilePath))
                {
                    throw new ArgumentException(
                        "--benchmark-require-shader-profile requires --benchmark-shader-profile.");
                }
            }
            else
            {
                if (mode == SampleSmokeMode.None && !string.IsNullOrWhiteSpace(baselineSnapshotDirectory) && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && sceneSpecified && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && performanceScenario != SamplePerformanceScenario.Normal && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && enableGpuMeshletCounters && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && enableDdgiContentConformance && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None &&
                    (receiverFeedbackModeOverride.HasValue ||
                     opacityMicromapModeOverride.HasValue ||
                     directionalGuidingModeOverride.HasValue ||
                     giCausticModeOverride.HasValue ||
                     nearFieldResidualModeOverride.HasValue ||
                     !string.IsNullOrWhiteSpace(advancedGiPrerequisiteManifestPath) ||
                     !string.IsNullOrWhiteSpace(advancedGiQualificationManifestPath) ||
                     !string.IsNullOrWhiteSpace(advancedGiRuntimeEvidenceBundlePath) ||
                     !string.IsNullOrWhiteSpace(advancedGiStartupProfilePath)) &&
                    !smokeModeSpecified)
                {
                    mode = SampleSmokeMode.Startup;
                }
                if (mode == SampleSmokeMode.None && transparencyMode != TransparencyMode.SortedAlphaBlend && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && asyncComputeModeOverride.HasValue && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && simpleDdgiSchedulerModeOverride.HasValue && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None &&
                    (simpleDdgiStoragePackingModeOverride.HasValue ||
                     simpleDdgiSampledAtlasCoverageModeOverride.HasValue) &&
                    !smokeModeSpecified)
                {
                    mode = SampleSmokeMode.Startup;
                }
                if (mode == SampleSmokeMode.None &&
                    (simpleDdgiProbeResidencyModeOverride.HasValue ||
                     simpleDdgiSparsePhysicalPageBudgetOverride.HasValue ||
                     simpleDdgiSparseMinimumPhysicalPageBudgetOverride.HasValue ||
                     simpleDdgiSparseRetentionFramesOverride.HasValue ||
                     simpleDdgiSparseMaximumAdmissionsOverride.HasValue ||
                     simpleDdgiSparseMaximumReceiverFeedbackOverride.HasValue ||
                     simpleDdgiSparseInactiveRetryFramesOverride.HasValue) &&
                    !smokeModeSpecified)
                {
                    mode = SampleSmokeMode.Startup;
                }
                if (mode == SampleSmokeMode.None && (enableFarFieldClipmap || enableFarFieldForceAll) && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && qualityPresetOverride.HasValue && !smokeModeSpecified)
                    mode = SampleSmokeMode.Startup;
                if (mode == SampleSmokeMode.None && longRunOptionsSpecified && !smokeModeSpecified)
                    mode = SampleSmokeMode.LongRun;
                if (mode == SampleSmokeMode.None && frameCount > 0)
                    mode = SampleSmokeMode.Resize;
            }
        }
        if (longRunOptionsSpecified && mode != SampleSmokeMode.LongRun)
        {
            throw new ArgumentException(
                "Long-run report, sampling, and duration options require --smoke-mode long-run.");
        }
        if (longRunMaxRetainedSamples is < 2 or
            > SampleLongRunMonitor.MaximumRetainedSampleCapacity)
        {
            throw new ArgumentException(
                "--long-run-max-samples requires an integer value of at least " +
                $"two and at most {SampleLongRunMonitor.MaximumRetainedSampleCapacity}.");
        }
        if (mode != SampleSmokeMode.None && frameCount <= 0 &&
            !(mode == SampleSmokeMode.LongRun && longRunMinutes > 0.0))
        {
            frameCount = mode switch
            {
                SampleSmokeMode.LongRun => 1000,
                // Lifecycle mutations are issued after a completed frame. Leave
                // one subsequent frame after the final resize/restore so the
                // gate observes the rebuilt swapchain rather than exiting at
                // the mutation boundary.
                SampleSmokeMode.Resize => 5,
                SampleSmokeMode.Minimize => 4,
                SampleSmokeMode.All => Math.Max(
                    7,
                    sceneReloadCount >= (int.MaxValue - 6) / 2
                        ? int.MaxValue
                        : (sceneReloadCount * 2) + 6),
                SampleSmokeMode.QualitySwitch => 7,
                SampleSmokeMode.DdgiResidencySwitch => 12,
                SampleSmokeMode.TextureHotReload => 4,
                _ => 3
            };
        }
        if (enableBenchmark)
            enableGpuTiming = true;

        var benchmark = new SampleBenchmarkOptions(
            enableBenchmark,
            benchmarkWarmupFrames,
            benchmarkMeasureFrames,
            benchmarkReportPath,
            DisableVSync: true,
            BudgetProfileOverride: benchmarkBudgetProfile,
            MaterialGiQualificationCandidate:
                materialGiQualificationCandidate)
        {
            CapturePairId = benchmarkPairId,
            CaptureVariant = string.IsNullOrWhiteSpace(benchmarkVariant)
                ? "baseline"
                : benchmarkVariant,
            RequireProductionTiming = benchmarkRequireProduction,
            HdrReferencePath = benchmarkHdrReferencePath,
            HdrCandidatePath = benchmarkHdrCandidatePath,
            HdrMaximumRelativeRmse = benchmarkHdrMaximumRelativeRmse,
            ShaderProfileArtifactPath = benchmarkShaderProfilePath,
            RequireShaderProfileEvidence = benchmarkRequireShaderProfile,
            MaximumAdditionalSettlingFrameCount = benchmarkMaximumSettlingFrames
        };
        return new SampleSmokeOptions(
            mode,
            frameCount,
            sceneReloadCount,
            startupLogPath,
            healthReportPath,
            validationMode,
            failOnValidationMessage,
            forceMissingAssets,
            performanceScenario,
            enableGpuTiming,
            enableSceneGpuCompaction,
            enableSceneIndirectDispatch,
            enableSceneGpuLodSelection,
            enableSceneGpuShadowCompaction,
            enableSceneSubmissionValidation,
            enableAsyncCompute,
            enableFarFieldClipmap,
            enableFarFieldForceAll,
            baselineSnapshotDirectory,
            sceneKind,
            transparencyMode,
            benchmark,
            sponzaGiCaptureDirectory,
            sponzaGiCaptureMode,
            asyncComputeModeOverride,
            materialGiCaptureDirectory,
            qualityPresetOverride,
            longRunReportPath,
            longRunWarmupFrames,
            longRunSampleInterval,
            longRunMaxRetainedSamples,
            longRunMemoryGrowthToleranceBytes,
            longRunMinutes,
            khronosRenderedGate,
            materialGiQualificationManifestPath,
            asyncComputeValidationPath,
            simpleDdgiSchedulerModeOverride,
            tailDdgiLongSoak,
            simpleDdgiProbeResidencyModeOverride,
            simpleDdgiSparsePhysicalPageBudgetOverride,
            simpleDdgiSparseMinimumPhysicalPageBudgetOverride,
            simpleDdgiSparseRetentionFramesOverride,
            simpleDdgiSparseMaximumAdmissionsOverride,
            simpleDdgiSparseMaximumReceiverFeedbackOverride,
            simpleDdgiSparseInactiveRetryFramesOverride,
            simpleDdgiStoragePackingModeOverride,
            simpleDdgiSampledAtlasCoverageModeOverride,
            enableGpuMeshletCounters,
            enableDdgiContentConformance,
            advancedGiPrerequisiteManifestPath,
            advancedGiQualificationManifestPath,
            receiverFeedbackModeOverride,
            opacityMicromapModeOverride,
            directionalGuidingModeOverride,
            giCausticModeOverride,
            nearFieldResidualModeOverride,
            receiverFeedbackQualificationId,
            opacityMicromapQualificationId,
            directionalGuidingQualificationId,
            giCausticQualificationId,
            nearFieldResidualQualificationId,
            advancedGiRuntimeEvidenceBundlePath,
            advancedGiStartupProfilePath,
            bistroQualityCaptureDirectory,
            bistroQualityCaptureVariant);
    }

    private static AsyncComputePath? ParseAsyncComputePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (AsyncComputePath path in Enum.GetValues<AsyncComputePath>())
            if (string.Equals(path.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                return path;
        throw new ArgumentException($"Unknown async compute path '{value}'.");
    }

    private static SampleSponzaGiCaptureMode ParseSponzaGiCaptureMode(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Sponza GI capture mode requires a value. Valid values: detailed, production, presentation.");
        }

        return value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "detailed" or "diagnostics" or "detaileddiagnostics" =>
                SampleSponzaGiCaptureMode.DetailedDiagnostics,
            "production" or "timing" or "productiontiming" =>
                SampleSponzaGiCaptureMode.ProductionTiming,
            "presentation" or "review" or "presentationreview" =>
                SampleSponzaGiCaptureMode.PresentationReview,
            _ => throw new ArgumentException(
                $"Invalid Sponza GI capture mode '{value}'. Valid values: detailed, production, presentation.")
        };
    }

    private static SampleBistroQualityCaptureVariant
        ParseBistroQualityCaptureVariant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SampleBistroQualityCaptureVariant.SunScaleStep;

        string normalized = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "presentation" or "beauty" =>
                SampleBistroQualityCaptureVariant.Presentation,
            "steadymotion" or "steady" or "motion" =>
                SampleBistroQualityCaptureVariant.SteadyMotion,
            "sunscalestep" or "scalestep" or "radiancestep" =>
                SampleBistroQualityCaptureVariant.SunScaleStep,
            "sundirectionstep" or "directionstep" =>
                SampleBistroQualityCaptureVariant.SunDirectionStep,
            "reflectionsourceab" or "reflection" =>
                SampleBistroQualityCaptureVariant.ReflectionSourceAb,
            _ => throw new ArgumentException(
                $"Invalid Bistro quality variant '{value}'. Valid values: presentation, steady-motion, sun-scale-step, sun-direction-step, reflection-source-ab.")
        };
    }

    private static SimpleDdgiSchedulerMode? ParseSimpleDdgiSchedulerMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "cpureference" or "cpu" => SimpleDdgiSchedulerMode.CpuReference,
            "gpumirror" or "mirror" => SimpleDdgiSchedulerMode.GpuMirror,
            "gpuresident" or "resident" => SimpleDdgiSchedulerMode.GpuResident,
            _ => throw new ArgumentException(
                $"Invalid Simple DDGI scheduler mode '{value}'. Valid values: cpu-reference, gpu-mirror, gpu-resident.")
        };
    }

    private static SimpleDdgiProbeResidencyMode? ParseSimpleDdgiProbeResidencyMode(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "dense" => SimpleDdgiProbeResidencyMode.Dense,
            "shadow" => SimpleDdgiProbeResidencyMode.Shadow,
            "sparsenearring" or "sparse" =>
                SimpleDdgiProbeResidencyMode.SparseNearRing,
            _ => throw new ArgumentException(
                $"Invalid Simple DDGI residency mode '{value}'. Valid values: dense, shadow, sparse-near-ring.")
        };
    }

    private static SimpleDdgiStoragePackingMode? ParseSimpleDdgiStoragePackingMode(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "legacy" => SimpleDdgiStoragePackingMode.Legacy,
            "validate" or "validation" => SimpleDdgiStoragePackingMode.Validate,
            "packed" => SimpleDdgiStoragePackingMode.Packed,
            _ => throw new ArgumentException(
                $"Invalid Simple DDGI storage mode '{value}'. Valid values: legacy, validate, packed.")
        };
    }

    private static SimpleDdgiSampledAtlasCoverageMode?
        ParseSimpleDdgiSampledAtlasCoverageMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "disabled" or "off" => SimpleDdgiSampledAtlasCoverageMode.Disabled,
            "fullcanonical" or "full" =>
                SimpleDdgiSampledAtlasCoverageMode.FullCanonical,
            "receiverrelevant" or "partial" =>
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
            _ => throw new ArgumentException(
                $"Invalid Simple DDGI mirror coverage '{value}'. Valid values: disabled, full-canonical, receiver-relevant.")
        };
    }

    private static int? ParseOptionalPositiveInt(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : ParsePositiveInt(value, 1, name);

    private static int? ParseOptionalNonNegativeInt(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : ParseNonNegativeInt(value, 0, name);

    private static string ReadValue(string[] args, ref int index)
    {
        string arg = args[index];
        int equals = arg.IndexOf('=');
        if (equals >= 0)
            return arg[(equals + 1)..];

        if (arg is "--force-missing-assets" or
            "--fail-on-validation-message" or
            "--benchmark" or
            "--benchmark-require-production" or
            "--benchmark-require-shader-profile" or
            "--material-gi-qualification-candidate" or
            "--tail-ddgi-long-soak" or
            "--gpu-timing" or
            "--gpu-meshlet-counters" or
            "--ddgi-content-conformance" or
            "--scene-gpu-compaction" or
            "--scene-indirect-dispatch" or
            "--scene-gpu-lod" or
            "--scene-gpu-shadow-compaction" or
            "--scene-submission-validation" or
            "--async-compute" or
            "--far-field-clipmap" or
            "--far-field-force-all")
            return "true";

        if (index + 1 >= args.Length)
            throw new ArgumentException($"{arg} requires a value.");
        if (args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{arg} requires a value; found option '{args[index + 1]}' instead.");
        }

        index++;
        return args[index];
    }

    private static string GetOptionName(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) ||
            !arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unexpected positional renderer argument '{arg}'.");
        }

        return arg.Split('=', 2)[0];
    }

    private static SampleSmokeMode ParseMode(string? value, SampleSmokeMode defaultMode)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultMode;

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => SampleSmokeMode.None,
            "startup" => SampleSmokeMode.Startup,
            "resize" => SampleSmokeMode.Resize,
            "fullscreen" => SampleSmokeMode.Fullscreen,
            "minimize" => SampleSmokeMode.Minimize,
            "scene-reload" or "scene_reload" or "scenereload" => SampleSmokeMode.SceneReload,
            "missing-assets" or "missing_assets" or "missingassets" => SampleSmokeMode.MissingAssets,
            "long-run" or "long_run" or "longrun" => SampleSmokeMode.LongRun,
            "quality-switch" or "quality_switch" or "qualityswitch" => SampleSmokeMode.QualitySwitch,
            "ddgi-residency-switch" or "ddgi_residency_switch" or
                "ddgiresidencyswitch" => SampleSmokeMode.DdgiResidencySwitch,
            "texture-hot-reload" or "texture_hot_reload" or "texturehotreload" => SampleSmokeMode.TextureHotReload,
            "all" => SampleSmokeMode.All,
            _ => throw new ArgumentException($"Invalid smoke mode '{value}'. Valid values: none, startup, resize, fullscreen, minimize, scene-reload, missing-assets, long-run, quality-switch, ddgi-residency-switch, texture-hot-reload, all.")
        };
    }

    private static RenderQualityPreset? ParseQualityPreset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "low" => RenderQualityPreset.Low,
            "medium" => RenderQualityPreset.Medium,
            "high" => RenderQualityPreset.High,
            "ultra" => RenderQualityPreset.Ultra,
            "ddgihigh" => RenderQualityPreset.DdgiHigh,
            _ => throw new ArgumentException(
                $"Invalid quality preset '{value}'. Valid values: low, medium, high, ultra, ddgi-high.")
        };
    }

    private static RenderBudgetProfileKind? ParseBenchmarkBudgetProfile(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim()
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "low" or "lowspec1080p30" =>
                RenderBudgetProfileKind.LowSpec1080p30,
            "medium" or "mid" or "midspec1080p60" =>
                RenderBudgetProfileKind.MidSpec1080p60,
            "high" or "highspec1440p60" =>
                RenderBudgetProfileKind.HighSpec1440p60,
            "ultra" or "ultra4k60" =>
                RenderBudgetProfileKind.Ultra4k60,
            "stress" or "stressunlimited" =>
                RenderBudgetProfileKind.StressUnlimited,
            _ => throw new ArgumentException(
                $"Invalid benchmark budget profile '{value}'. Valid values: low, medium, high, ultra, stress.")
        };
    }

    private static SamplePerformanceScenario ParsePerformanceScenario(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SamplePerformanceScenario.Normal;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        foreach (SamplePerformanceScenario scenario in Enum.GetValues<SamplePerformanceScenario>())
        {
            string scenarioName = scenario.ToString().Replace("-", string.Empty).Replace("_", string.Empty);
            if (scenarioName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return scenario;
        }

        throw new ArgumentException($"Invalid performance scenario '{value}'. Valid values: {string.Join(", ", Enum.GetNames<SamplePerformanceScenario>())}.");
    }


    private static AsyncComputeMode? ParseAsyncComputeMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "auto" => AsyncComputeMode.Auto,
            "disabled" or "disable" or "off" => AsyncComputeMode.Disabled,
            "forced" or "force" or "forceenabled" or "forceenabledforvalidation" or "validation" =>
                AsyncComputeMode.ForceEnabledForValidation,
            _ => throw new ArgumentException(
                $"Invalid async compute mode '{value}'. Valid values: auto, disabled, forced.")
        };
    }

    private static SampleSceneKind ParseSceneKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SampleSceneKind.GlobalIlluminationTest;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        foreach (SampleSceneKind sceneKind in Enum.GetValues<SampleSceneKind>())
        {
            string sceneName = sceneKind.ToString().Replace("-", string.Empty).Replace("_", string.Empty);
            if (sceneName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return sceneKind;
        }

        throw new ArgumentException($"Invalid scene '{value}'. Valid values: {string.Join(", ", Enum.GetNames<SampleSceneKind>())}.");
    }

    private static TransparencyMode ParseTransparencyMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TransparencyMode.SortedAlphaBlend;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized.ToLowerInvariant() switch
        {
            "sorted" or "sortedalpha" or "sortedalphablend" => TransparencyMode.SortedAlphaBlend,
            "weighted" or "weightedoit" or "weightedblendedoit" => TransparencyMode.WeightedBlendedOit,
            _ => throw new ArgumentException($"Invalid transparency mode '{value}'. Valid values: sorted-alpha-blend, weighted-blended-oit.")
        };
    }

    private static int ParsePositiveInt(string? value, int defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{name} requires a positive integer value.");
        return parsed;
    }

    private static int ParseNonNegativeInt(string? value, int defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(value, out int parsed) || parsed < 0)
            throw new ArgumentException($"{name} requires a non-negative integer value.");
        return parsed;
    }

    private static ulong ParseNonNegativeUlong(string? value, ulong defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
            throw new ArgumentException($"{name} requires a non-negative integer value.");
        return parsed;
    }

    private static double ParsePositiveDouble(string? value, double defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) ||
            parsed <= 0.0)
        {
            throw new ArgumentException($"{name} requires a positive finite numeric value.");
        }

        return parsed;
    }

    private static double ParseNonNegativeDouble(string? value, double defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0.0)
        {
            throw new ArgumentException($"{name} requires a non-negative finite numeric value.");
        }

        return parsed;
    }

    private static bool ParseBool(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ArgumentException(
                $"{name} requires a boolean value: true, false, 1, 0, yes, no, on, or off.")
        };
    }

    private static string RequirePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} requires a non-empty path.");

        return System.IO.Path.GetFullPath(value);
    }

    private static string RequireNonEmptyValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} requires a non-empty value.");
        return value.Trim();
    }

    private static TEnum? ParseOptionalEnum<TEnum>(
        string? value,
        string name)
        where TEnum : struct, Enum =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : ParseRequiredEnum<TEnum>(value, name);

    private static TEnum ParseRequiredEnum<TEnum>(string value, string name)
        where TEnum : struct, Enum
    {
        string normalized = NormalizeEnumToken(value);
        foreach (string candidateName in Enum.GetNames<TEnum>())
        {
            if (string.Equals(
                    NormalizeEnumToken(candidateName),
                    normalized,
                    StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse(candidateName, ignoreCase: false, out TEnum candidate))
            {
                return candidate;
            }
        }

        throw new ArgumentException(
            $"{name} has invalid value '{value}'. Valid values: " +
            string.Join(", ", Enum.GetNames<TEnum>()));
    }

    private static string NormalizeEnumToken(string? value) =>
        (value ?? string.Empty).Trim()
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace("_", string.Empty, StringComparison.Ordinal);

    private static string? ParseOptionalStableToken(
        string? value,
        string name) => string.IsNullOrWhiteSpace(value)
        ? null
        : RequireStableToken(value, name);

    private static string RequireStableToken(string value, string name)
    {
        string token = RequireNonEmptyValue(value, name);
        if (token.Length > 256 || token.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} requires a control-free token of at most 256 characters.");
        }
        return token;
    }
}
