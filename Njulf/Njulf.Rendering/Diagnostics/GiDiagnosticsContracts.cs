using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Diagnostics
{
    /// <summary>
    /// States how a timing value may be used. Inclusive values are diagnostic attribution only
    /// and must never be summed into an incremental GI budget.
    /// </summary>
    public enum GiTimingAttribution
    {
        Unavailable,
        Exclusive,
        Inclusive,
        PairedEstimate
    }

    public enum GiFeatureStateStatus
    {
        Disabled,
        Unsupported,
        Requested,
        Active,
        Fallback,
        Unavailable
    }

    public enum GiDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public enum GiDiagnosticWarningCode
    {
        InvestigationCountersUnavailable,
        SupportHole,
        LargeAreaBlackout,
        AsyncComputeFallback,
        PagedFarFieldInactive,
        SampledAtlasFallback,
        /// <summary>The explicit live rollback control is suppressing dynamic GI.</summary>
        EmergencyGiFallbackActive,
        /// <summary>A requested Simple-DDGI layout was deterministically degraded before allocation.</summary>
        SimpleDdgiLayoutDegraded,
        /// <summary>The resolved detailed ray-query resident set could not be represented completely.</summary>
        AccelerationStructureIncomplete,
        /// <summary>A requested or live GI component exceeded its declared hard budget.</summary>
        GiBudgetOverrun
    }

    public enum GiMeasurementMode
    {
        Production,
        NormalTelemetry,
        DetailedInvestigation
    }

    public enum GiMetricFreshness
    {
        CurrentFrame,
        DelayedReadback,
        Aggregated,
        Unavailable
    }

    /// <summary>
    /// Explicit feature lifecycle. <see cref="Status"/> is the current outcome while the
    /// individual booleans preserve the distinction between capability, request, and activity.
    /// </summary>
    public sealed record GiFeatureState(
        string Name,
        bool Compiled,
        bool Supported,
        bool Requested,
        bool Active,
        GiFeatureStateStatus Status,
        string Reason);

    public sealed record GiDiagnosticWarning(
        GiDiagnosticWarningCode Code,
        GiDiagnosticSeverity Severity,
        string Message,
        string Feature,
        double ObservedValue,
        double Threshold,
        string Unit,
        GiMetricFreshness Freshness,
        string Tier,
        string Scenario,
        string CameraState,
        ulong FrameSerial,
        ulong CameraCutSerial,
        string RecommendedAction);

    /// <summary>
    /// Quantified evidence used by the black-frame evaluator. A black-frame warning is only
    /// emitted after a sufficiently large, persistent, causally-supported failure; a single
    /// zero-valued shaded sample is intentionally not enough.
    /// </summary>
    public sealed record GiBlackFrameMetrics(
        bool IsAvailable,
        uint SampleCount,
        uint ZeroFinalIndirectSampleCount,
        uint ZeroDdgiAndIblSampleCount,
        uint ZeroIrradianceSampleCount,
        uint OutOfGridSampleCount,
        double ZeroFinalIndirectFraction,
        double ZeroDdgiAndIblFraction,
        double ZeroIrradianceFraction,
        double OutOfGridFraction,
        bool ExpectedMaterialBlackKnown,
        uint ExpectedMaterialBlackSampleCount,
        bool TransientState,
        string TransientStateReason,
        int ConsecutiveLargeAreaFrames,
        int ConsecutiveSupportHoleFrames,
        bool LargeAreaBlackout,
        bool SupportHole,
        string UnavailableReason)
    {
        public static GiBlackFrameMetrics Unavailable(string reason) => new(
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            false,
            string.Empty,
            0,
            0,
            false,
            false,
            reason);
    }

    public sealed record GiWarningEvaluationResult(
        GiBlackFrameMetrics BlackFrame,
        IReadOnlyList<GiDiagnosticWarning> Warnings)
    {
        public bool LegacyBlackFrameSuspect => BlackFrame.LargeAreaBlackout;
    }

    public sealed record GiTimingAttributionSnapshot(
        long ForwardOpaqueInclusiveMicroseconds,
        long ForwardGiGatherInclusiveMicroseconds,
        GiTimingAttribution ForwardGiGatherInclusiveAttribution,
        long ForwardGiGatherIncrementalMicroseconds,
        GiTimingAttribution ForwardGiGatherIncrementalAttribution,
        string ForwardGiGatherIncrementalReason)
    {
        public static GiTimingAttributionSnapshot Unavailable { get; } = new(
            0,
            0,
            GiTimingAttribution.Unavailable,
            0,
            GiTimingAttribution.Unavailable,
            "No isolated or paired forward-GI timing is available.");
    }

    /// <summary>
    /// One latency distribution with the censored tail reported separately from
    /// completed observations.  This prevents a bounded histogram from silently
    /// making a long-running dirty event look healthy.
    /// </summary>
    public sealed record SimpleDdgiLatencyTelemetry(
        int SampleCount,
        int P50Frames,
        int P95Frames,
        int MaximumFrames,
        int CensoredCount);

    /// <summary>
    /// Frame-local Simple-DDGI scheduler evidence. Configured limits and admitted
    /// work are intentionally separate: a sparse queue must not redefine its tier.
    /// </summary>
    public sealed record SimpleDdgiSchedulingTelemetry(
        bool IsAvailable,
        int ConfiguredRequestBudget,
        int ConfiguredPrimaryRayBudget,
        int ScheduledRequestCount,
        ulong ScheduledPrimaryRayCount,
        int RejectedProbeCount,
        ulong RejectedPrimaryRayCount,
        SimpleDdgiLatencyTelemetry FirstScheduled,
        SimpleDdgiLatencyTelemetry FirstCompleted,
        SimpleDdgiLatencyTelemetry Convergence,
        int OutstandingEventCount,
        string CompletionSemantics)
    {
        public static SimpleDdgiSchedulingTelemetry Unavailable(string reason) => new(
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            new SimpleDdgiLatencyTelemetry(0, 0, 0, 0, 0),
            new SimpleDdgiLatencyTelemetry(0, 0, 0, 0, 0),
            new SimpleDdgiLatencyTelemetry(0, 0, 0, 0, 0),
            0,
            reason);
    }

    /// <summary>
    /// Serializable pre-allocation admission evidence.  This deliberately includes rejected
    /// requests, which cannot be reconstructed from the active GPU volume table alone.
    /// </summary>
    public sealed record SimpleDdgiLayoutVolumeTelemetry(
        string Id,
        int SourceOrdinal,
        bool IsAuthored,
        SimpleDdgiVolumePurpose IntendedPurpose,
        int Priority,
        float ProbeSpacing,
        int RequestedProbeCount,
        int AcceptedProbeCount,
        ulong RequestedPersistentBytes,
        SimpleDdgiLayoutDecision Decision,
        string Reason);

    /// <summary>
    /// Tier-resolved Simple-DDGI layout and budget.  Origin and physical offsets remain in
    /// <see cref="DdgiVolumeDiagnosticsEntry"/> because they are camera-relative frame state,
    /// whereas this record is stable configuration/admission evidence.
    /// </summary>
    public sealed record SimpleDdgiLayoutTelemetry(
        bool IsAvailable,
        DdgiQualityTier Tier,
        SimpleDdgiLayoutAdmissionMode AdmissionMode,
        int ProbeBudget,
        ulong PersistentMemoryBudgetBytes,
        int VolumeBudget,
        int RequestedProbeCount,
        int AcceptedProbeCount,
        ulong RequestedPersistentBytes,
        ulong AcceptedPersistentBytes,
        int RequestedVolumeCount,
        int AcceptedVolumeCount,
        int RejectedVolumeCount,
        bool WasDegraded,
        string Summary,
        IReadOnlyList<SimpleDdgiLayoutVolumeTelemetry> Volumes)
    {
        public static SimpleDdgiLayoutTelemetry Unavailable(string reason) => new(
            false,
            DdgiQualityTier.DdgiHigh,
            SimpleDdgiLayoutAdmissionMode.Degrade,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            reason,
            Array.Empty<SimpleDdgiLayoutVolumeTelemetry>());
    }

    public static class SimpleDdgiLayoutTelemetryFactory
    {
        public static SimpleDdgiLayoutTelemetry Create(
            SimpleDdgiLayoutReport? report,
            bool sampledAtlasRequested = false,
            bool transportV2Enabled = false,
            int transportRayCapacity = 0)
        {
            if (report == null)
                return SimpleDdgiLayoutTelemetry.Unavailable("Simple DDGI did not produce a resolved layout report.");

            var volumes = new List<SimpleDdgiLayoutVolumeTelemetry>(report.Volumes.Count);
            int acceptedVolumeCount = 0;
            int rejectedVolumeCount = 0;
            foreach (SimpleDdgiLayoutVolumeDecision decision in report.Volumes)
            {
                if (decision.Decision == SimpleDdgiLayoutDecision.Accepted)
                    acceptedVolumeCount++;
                else
                    rejectedVolumeCount++;

                SimpleDdgiLayoutVolumeRequest request = decision.Request;
                // The allocator stores accepted bytes on its decision. Rejected entries retain
                // their request but intentionally allocate zero bytes, so recompute the requested
                // cost here to keep the persisted pre-allocation evidence exact for both paths.
                ulong requestedPersistentBytes = SimpleDdgiLayoutCompiler.EstimatePersistentBytes(
                    request.ProbeCount,
                    sampledAtlasRequested,
                    transportV2Enabled,
                    transportRayCapacity);
                volumes.Add(new SimpleDdgiLayoutVolumeTelemetry(
                    request.Id,
                    request.SourceOrdinal,
                    request.IsAuthored,
                    request.Purpose,
                    request.Priority,
                    request.Spacing,
                    request.ProbeCount,
                    decision.AcceptedProbeCount,
                    requestedPersistentBytes,
                    decision.Decision,
                    decision.Reason));
            }

            return new SimpleDdgiLayoutTelemetry(
                true,
                report.Budget.Tier,
                report.AdmissionMode,
                report.Budget.ProbeBudget,
                report.Budget.PersistentMemoryBudgetBytes,
                report.Budget.VolumeBudget,
                report.RequestedProbeCount,
                report.AcceptedProbeCount,
                report.RequestedPersistentBytes,
                report.AcceptedPersistentBytes,
                report.Volumes.Count,
                acceptedVolumeCount,
                rejectedVolumeCount,
                report.WasDegraded,
                report.Summary,
                volumes);
        }
    }

    /// <summary>
    /// Compact, capture-safe scheduler class evidence.  The manager keeps its own allocation-free
    /// struct; this contract copies it at diagnostics capture time so persisted snapshots do not
    /// depend on renderer-internal storage or enum ABI.
    /// </summary>
    public sealed record SimpleDdgiSchedulerPolicyTelemetry(
        bool IsAvailable,
        int ConfiguredRequestBudget,
        int EffectiveRequestBudget,
        int ScheduledFreshExposedVisible,
        int ScheduledVisibleDirty,
        int ScheduledVisibleRetry,
        int ScheduledNearMaintenance,
        int ScheduledMidMaintenance,
        int ScheduledFarMaintenance,
        int ReservedFreshExposedVisible,
        int ReservedVisibleDirty,
        int ReservedVisibleRetry,
        int ReservedNearMaintenance,
        int ReservedMidMaintenance,
        int ReservedFarMaintenance,
        int PendingFreshExposedVisible,
        int PendingVisibleDirty,
        int PendingVisibleRetry,
        int PendingNearMaintenance,
        int PendingMidMaintenance,
        int PendingFarMaintenance,
        int DeferredRequestCount,
        ulong RejectedPrimaryRayCount,
        string PressureReason,
        ulong LastCompletedGpuMicroseconds,
        ulong TargetGpuMicroseconds,
        bool DeterministicFixedBudget,
        string UnavailableReason)
    {
        public static SimpleDdgiSchedulerPolicyTelemetry Unavailable(string reason) => new(
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "Unavailable",
            0,
            0,
            false,
            reason);
    }

    public static class SimpleDdgiSchedulerPolicyTelemetryFactory
    {
        public static SimpleDdgiSchedulerPolicyTelemetry Create(SimpleDdgiSchedulerTelemetry telemetry)
        {
            return new SimpleDdgiSchedulerPolicyTelemetry(
                true,
                telemetry.ConfiguredRequestBudget,
                telemetry.EffectiveRequestBudget,
                telemetry.ScheduledFreshExposedVisible,
                telemetry.ScheduledVisibleDirty,
                telemetry.ScheduledVisibleRetry,
                telemetry.ScheduledNearMaintenance,
                telemetry.ScheduledMidMaintenance,
                telemetry.ScheduledFarMaintenance,
                telemetry.ReservedFreshExposedVisible,
                telemetry.ReservedVisibleDirty,
                telemetry.ReservedVisibleRetry,
                telemetry.ReservedNearMaintenance,
                telemetry.ReservedMidMaintenance,
                telemetry.ReservedFarMaintenance,
                telemetry.PendingFreshExposedVisible,
                telemetry.PendingVisibleDirty,
                telemetry.PendingVisibleRetry,
                telemetry.PendingNearMaintenance,
                telemetry.PendingMidMaintenance,
                telemetry.PendingFarMaintenance,
                telemetry.DeferredRequestCount,
                telemetry.RejectedPrimaryRayCount,
                telemetry.PressureReason.ToString(),
                telemetry.LastCompletedGpuMicroseconds,
                telemetry.TargetGpuMicroseconds,
                telemetry.DeterministicFixedBudget,
                string.Empty);
        }
    }

    public sealed record GiResidencyComponent(
        string Name,
        ulong Bytes,
        ulong BudgetBytes,
        RenderBudgetStatus Status,
        bool MayOverlapOtherComponents,
        string Source,
        bool CountsTowardCombinedBudget = true);

    /// <summary>
    /// Component diagnostics may overlap because manager-facing counters describe logical
    /// resources or children of one shared cap. <see cref="UniqueResidentBytes"/> is reported
    /// once from disjoint render-target and allocation-tracker sources, and is the only combined
    /// residency value intended for comparison or trend analysis.
    /// </summary>
    public sealed record GiResidencySnapshot(
        ulong UniqueResidentBytes,
        ulong DeclaredComponentBudgetBytes,
        bool UniqueMeasurementAvailable,
        bool ComponentTotalsMayOverlap,
        string UniqueMeasurementSource,
        IReadOnlyList<GiResidencyComponent> Components)
    {
        public static GiResidencySnapshot Unavailable { get; } = new(
            0,
            0,
            false,
            true,
            "unavailable",
            Array.Empty<GiResidencyComponent>());
    }

    public sealed record PerformanceCaptureRunMetadata(
        string SceneKind,
        string Scenario,
        string BuildConfiguration,
        string ApplicationVersion,
        string Commit,
        string ShaderBundleHash,
        int SettingsSchemaVersion)
    {
        public static PerformanceCaptureRunMetadata Unknown { get; } = new(
            "unknown-scene",
            "unknown-scenario",
            "unknown-build",
            "unknown-version",
            "unknown-commit",
            "unknown-shader-bundle",
            0);
    }

    public sealed record PerformanceCaptureCameraMetadata(
        float PositionX,
        float PositionY,
        float PositionZ,
        float YawRadians,
        float PitchRadians,
        float FieldOfViewRadians,
        float NearPlane,
        float FarPlane,
        string ViewHash,
        string ProjectionHash,
        ulong CameraCutSerial)
    {
        public static PerformanceCaptureCameraMetadata Unknown { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "unknown-view",
            "unknown-projection",
            0);
    }

    public sealed record PerformanceCaptureFrameMetadata(
        ulong FrameSerial,
        ulong FramesSinceSceneLoad,
        DdgiRuntimeWarmupState WarmupState,
        int FramesSinceLastRingRecenter,
        int FramesSinceLastAtlasClear)
    {
        public static PerformanceCaptureFrameMetadata Unknown { get; } = new(
            0,
            0,
            DdgiRuntimeWarmupState.Disabled,
            0,
            0);
    }

    public sealed record ResolvedGiSettingsMetadata(
        string StableHash,
        string Summary,
        IReadOnlyList<string> EffectiveSettings)
    {
        public static ResolvedGiSettingsMetadata Unknown { get; } = new(
            "unknown",
            "unknown",
            Array.Empty<string>());
    }

    public sealed record GiMeasurementMetadata(
        GiMeasurementMode Mode,
        int DiagnosticSamplingRate,
        string EstimatedOverhead,
        bool DetailedCountersEnabled,
        bool DetailedCountersReadbackValid)
    {
        public static GiMeasurementMetadata Unknown { get; } = new(
            GiMeasurementMode.NormalTelemetry,
            0,
            "not measured",
            false,
            false);
    }

    /// <summary>
    /// Resolves a compact, serializable feature state table from live renderer diagnostics.
    /// It intentionally treats a requested feature without a viable runtime allocation as a
    /// fallback rather than claiming it is active.
    /// </summary>
    public static class GiFeatureStateFactory
    {
        public static IReadOnlyList<GiFeatureState> Create(RendererDiagnostics diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            var states = new List<GiFeatureState>(11)
            {
                CreateGlobalIlluminationState(diagnostics),
                CreateEmergencyGiFallbackState(diagnostics),
                CreateSsgiState(diagnostics),
                CreateDdgiState(diagnostics),
                CreateSimpleDdgiState(diagnostics),
                CreateSimpleDdgiTransportState(diagnostics),
                CreateRayQueryGiState(diagnostics),
                CreatePagedFarFieldState(diagnostics),
                CreateAsyncComputeState(diagnostics),
                CreateSampledAtlasState(diagnostics),
                CreateAccelerationStructureStreamingState(diagnostics),
                CreateDetailedCountersState(diagnostics)
            };
            return states;
        }

        private static GiFeatureState CreateGlobalIlluminationState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.GlobalIlluminationEnabled != 0;
            bool requested = diagnostics.GlobalIlluminationRequested != 0 ||
                active ||
                diagnostics.GlobalIlluminationSsgiActive != 0 ||
                diagnostics.GlobalIlluminationDdgiActive != 0;
            if (!requested)
            {
                return new GiFeatureState(
                    "global-illumination",
                    true,
                    true,
                    false,
                    false,
                    GiFeatureStateStatus.Disabled,
                    "Not requested by the resolved GI settings.");
            }
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
            {
                return new GiFeatureState(
                    "global-illumination",
                    true,
                    true,
                    true,
                    false,
                    GiFeatureStateStatus.Fallback,
                    "Emergency GI fallback is active; dynamic GI is suppressed while environment and reflection lighting remain available.");
            }
            if (active)
            {
                return new GiFeatureState(
                    "global-illumination",
                    true,
                    true,
                    true,
                    true,
                    GiFeatureStateStatus.Active,
                    "The resolved global-illumination mode is active.");
            }

            string reason = NonEmptyOr(
                diagnostics.GlobalIlluminationFallbackReason,
                "Global illumination was requested but no dynamic GI path is active.");
            return new GiFeatureState(
                "global-illumination",
                true,
                true,
                true,
                false,
                GiFeatureStateStatus.Fallback,
                reason);
        }

        private static GiFeatureState CreateEmergencyGiFallbackState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0;
            return active
                ? new GiFeatureState(
                    "emergency-gi-fallback",
                    true,
                    true,
                    true,
                    true,
                    GiFeatureStateStatus.Active,
                    "Emergency GI fallback is active; dynamic GI is disabled without requiring a restart.")
                : new GiFeatureState(
                    "emergency-gi-fallback",
                    true,
                    true,
                    false,
                    false,
                    GiFeatureStateStatus.Disabled,
                    "The emergency GI fallback control is not active.");
        }

        private static GiFeatureState CreateSsgiState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.GlobalIlluminationSsgiActive != 0;
            bool requested = diagnostics.GlobalIlluminationSsgiRequested != 0 || active;
            return CreateDynamicGiPathState(
                "ssgi",
                requested,
                active,
                diagnostics,
                "Screen-space GI is active.",
                "SSGI was requested but is not active.");
        }

        private static GiFeatureState CreateDdgiState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.GlobalIlluminationDdgiActive != 0;
            bool requested = diagnostics.GlobalIlluminationDdgiRequested != 0 || active;
            return CreateDynamicGiPathState(
                "ddgi",
                requested,
                active,
                diagnostics,
                "The resolved DDGI path is active.",
                "DDGI was requested but is not active.");
        }

        private static GiFeatureState CreateSimpleDdgiState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.SimpleDdgiActive != 0;
            bool requested = diagnostics.SimpleDdgiRequested != 0 || active;
            return CreateDynamicGiPathState(
                "simple-ddgi",
                requested,
                active,
                diagnostics,
                "The resolved Simple-DDGI path is active.",
                "Simple DDGI was requested but no resolved Simple-DDGI layout is active.");
        }

        private static GiFeatureState CreateSimpleDdgiTransportState(RendererDiagnostics diagnostics)
        {
            bool simpleDdgiActive = diagnostics.SimpleDdgiActive != 0;
            bool active = diagnostics.SimpleDdgiTransportV2Active != 0;
            bool requested = simpleDdgiActive || active;
            if (!requested)
                return new GiFeatureState("simple-ddgi-transport-v2", true, true, false, false, GiFeatureStateStatus.Disabled, "Simple DDGI is inactive.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                return new GiFeatureState("simple-ddgi-transport-v2", true, true, true, false, GiFeatureStateStatus.Fallback, "Emergency GI fallback is active; V2 transport is intentionally suppressed.");
            if (active)
            {
                string progress = diagnostics.SimpleDdgiTransportSourceReadyProbeCount > 0
                    ? $"V2 source/solve transport is active; {diagnostics.SimpleDdgiTransportConvergedProbeCount}/{diagnostics.SimpleDdgiTransportSourceReadyProbeCount} source-ready probes are converged."
                    : "V2 source/solve transport is active; source cache warmup is pending.";
                if (diagnostics.SimpleDdgiTransportGlobalConvergencePending != 0)
                    progress += $" Field-wide minimum-bounce convergence is still in progress ({diagnostics.SimpleDdgiTransportGlobalConvergenceElapsedFrames} DDGI frames).";
                return new GiFeatureState("simple-ddgi-transport-v2", true, true, true, true, GiFeatureStateStatus.Active, progress);
            }

            return new GiFeatureState(
                "simple-ddgi-transport-v2",
                true,
                true,
                true,
                false,
                GiFeatureStateStatus.Fallback,
                "Simple DDGI is using the explicit V1 compatibility transport path.");
        }

        private static GiFeatureState CreateRayQueryGiState(RendererDiagnostics diagnostics)
        {
            bool active = diagnostics.GlobalIlluminationRayQueryActive != 0;
            bool requested = diagnostics.GlobalIlluminationRayQueryRequested != 0 || active;
            bool supported = diagnostics.GlobalIlluminationRayQuerySupported != 0;
            if (!requested)
                return new GiFeatureState("ray-query-gi", true, supported, false, false, GiFeatureStateStatus.Disabled, "Not requested by the resolved GI settings.");
            if (!supported)
                return new GiFeatureState("ray-query-gi", true, false, true, false, GiFeatureStateStatus.Unsupported, "The selected device does not support the requested GI ray-query path.");
            return CreateDynamicGiPathState(
                "ray-query-gi",
                true,
                active,
                diagnostics,
                "The GI ray-query backend is active.",
                "The GI ray-query backend was requested but is not active.");
        }

        private static GiFeatureState CreateDynamicGiPathState(
            string name,
            bool requested,
            bool active,
            RendererDiagnostics diagnostics,
            string activeReason,
            string inactiveReason)
        {
            if (!requested)
                return new GiFeatureState(name, true, true, false, false, GiFeatureStateStatus.Disabled, "Not requested by the resolved GI settings.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
            {
                return new GiFeatureState(
                    name,
                    true,
                    true,
                    true,
                    false,
                    GiFeatureStateStatus.Fallback,
                    "Emergency GI fallback is active; the requested path is intentionally suppressed.");
            }
            if (active)
                return new GiFeatureState(name, true, true, true, true, GiFeatureStateStatus.Active, activeReason);

            string reason = string.IsNullOrWhiteSpace(diagnostics.GlobalIlluminationFallbackReason)
                ? inactiveReason
                : diagnostics.GlobalIlluminationFallbackReason;
            return new GiFeatureState(name, true, true, true, false, GiFeatureStateStatus.Fallback, reason);
        }

        private static GiFeatureState CreatePagedFarFieldState(RendererDiagnostics diagnostics)
        {
            bool requested = diagnostics.FarFieldPagedFeatureEnabled != 0;
            bool allocated = diagnostics.FarFieldPagedMode != 0 && diagnostics.FarFieldPagePoolCapacity > 0;
            bool active = allocated &&
                diagnostics.FarFieldResidentPageCount > 0 &&
                diagnostics.FarFieldPendingPageCount == 0;
            if (!requested)
                return new GiFeatureState("paged-far-field", true, true, false, false, GiFeatureStateStatus.Disabled, "Not requested by the resolved GI settings.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                return new GiFeatureState("paged-far-field", true, true, true, false, GiFeatureStateStatus.Fallback, "Emergency GI fallback is active; paged far-field work is intentionally suppressed.");
            if (diagnostics.SimpleDdgiActive == 0)
                return new GiFeatureState("paged-far-field", true, true, true, false, GiFeatureStateStatus.Fallback, "Simple DDGI is inactive, so the paged far-field path cannot run.");
            if (!active)
            {
                string reason = diagnostics.FarFieldPagePoolCapacity <= 0
                    ? "No far-field page pool was allocated."
                    : diagnostics.FarFieldPagedMode == 0
                        ? "The far-field manager did not enter paged mode."
                        : diagnostics.FarFieldResidentPageCount <= 0
                            ? "The far-field page pool is allocated but no page has been published."
                            : $"The far-field cache is warming ({diagnostics.FarFieldPendingPageCount} page(s) pending).";
                return new GiFeatureState("paged-far-field", true, true, true, false, GiFeatureStateStatus.Fallback, reason);
            }

            return new GiFeatureState("paged-far-field", true, true, true, true, GiFeatureStateStatus.Active, "Paged mode is active and every resident page is published.");
        }

        private static GiFeatureState CreateAsyncComputeState(RendererDiagnostics diagnostics)
        {
            bool requested = diagnostics.AsyncComputeRequested != 0;
            bool supported = diagnostics.AsyncComputeSupported != 0;
            bool active = diagnostics.AsyncComputeEnabled != 0;
            if (!requested)
                return new GiFeatureState("async-compute", true, supported, false, false, GiFeatureStateStatus.Disabled, "Not requested by the frame plan.");
            if (!supported)
                return new GiFeatureState("async-compute", true, false, true, false, GiFeatureStateStatus.Unsupported, NonEmptyOr(diagnostics.AsyncComputeLastFallbackReason, "The selected device or queue topology does not support this plan."));
            if (active)
                return new GiFeatureState("async-compute", true, true, true, true, GiFeatureStateStatus.Active, "The validated asynchronous submission plan is active.");
            if (!string.IsNullOrWhiteSpace(diagnostics.AsyncComputeLastFallbackReason))
                return new GiFeatureState("async-compute", true, true, true, false, GiFeatureStateStatus.Fallback, diagnostics.AsyncComputeLastFallbackReason);
            return new GiFeatureState("async-compute", true, true, true, false, GiFeatureStateStatus.Requested, NonEmptyOr(diagnostics.AsyncComputeStatus, "Requested but not selected for this frame."));
        }

        private static GiFeatureState CreateSampledAtlasState(RendererDiagnostics diagnostics)
        {
            bool requested = diagnostics.SimpleDdgiSampledAtlasRequested != 0;
            bool active = diagnostics.SimpleDdgiSampledAtlasActive != 0;
            if (!requested)
                return new GiFeatureState("sampled-simple-ddgi-atlas", true, true, false, false, GiFeatureStateStatus.Disabled, "Not requested by the resolved GI settings.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                return new GiFeatureState("sampled-simple-ddgi-atlas", true, true, true, false, GiFeatureStateStatus.Fallback, "Emergency GI fallback is active; the sampled atlas mirror is intentionally suppressed.");
            if (active)
                return new GiFeatureState("sampled-simple-ddgi-atlas", true, true, true, true, GiFeatureStateStatus.Active, "Sampled atlas mirror is active.");
            return new GiFeatureState(
                "sampled-simple-ddgi-atlas",
                true,
                true,
                true,
                false,
                string.IsNullOrWhiteSpace(diagnostics.SimpleDdgiSampledAtlasFallbackReason)
                    ? GiFeatureStateStatus.Requested
                    : GiFeatureStateStatus.Fallback,
                NonEmptyOr(diagnostics.SimpleDdgiSampledAtlasFallbackReason, "Requested but not active yet."));
        }

        private static GiFeatureState CreateAccelerationStructureStreamingState(RendererDiagnostics diagnostics)
        {
            bool requested = diagnostics.StreamedGiAccelerationStructuresFeatureEnabled != 0;
            bool active = diagnostics.AccelerationStructureStreamingEnabled != 0;
            if (!requested)
                return new GiFeatureState("gi-acceleration-structure-streaming", true, true, false, false, GiFeatureStateStatus.Disabled, "Not requested by the resolved GI settings.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                return new GiFeatureState("gi-acceleration-structure-streaming", true, true, true, false, GiFeatureStateStatus.Fallback, "Emergency GI fallback is active; GI acceleration-structure streaming is intentionally suppressed.");
            if (diagnostics.AccelerationStructureBlasBudgetRejectedCount > 0)
                return new GiFeatureState(
                    "gi-acceleration-structure-streaming",
                    true,
                    true,
                    true,
                    false,
                    GiFeatureStateStatus.Fallback,
                    NonEmptyOr(
                        diagnostics.AccelerationStructureFallbackReason,
                        "The resolved resident set was rejected rather than publishing an incomplete TLAS."));
            if (active)
                return new GiFeatureState("gi-acceleration-structure-streaming", true, true, true, true, GiFeatureStateStatus.Active, "Streaming residency management is active.");
            return new GiFeatureState(
                "gi-acceleration-structure-streaming",
                true,
                true,
                true,
                false,
                string.IsNullOrWhiteSpace(diagnostics.AccelerationStructureFallbackReason)
                    ? GiFeatureStateStatus.Requested
                    : GiFeatureStateStatus.Fallback,
                NonEmptyOr(diagnostics.AccelerationStructureFallbackReason, "Requested but not active yet."));
        }

        private static GiFeatureState CreateDetailedCountersState(RendererDiagnostics diagnostics)
        {
            bool requested = diagnostics.DdgiDetailedCountersRequested != 0 || diagnostics.DdgiDetailedCountersEnabled != 0;
            bool active = diagnostics.DdgiDetailedCountersEnabled != 0 && diagnostics.DdgiInvestigationCountersReadbackValid != 0;
            if (!requested)
                return new GiFeatureState("detailed-gi-counters", true, true, false, false, GiFeatureStateStatus.Disabled, "Detailed investigation counters are disabled.");
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                return new GiFeatureState("detailed-gi-counters", true, true, true, false, GiFeatureStateStatus.Fallback, "Emergency GI fallback is active; detailed dynamic-GI counter collection is suppressed.");
            if (active)
                return new GiFeatureState("detailed-gi-counters", true, true, true, true, GiFeatureStateStatus.Active, "Completed GPU counter readback is available.");
            return new GiFeatureState("detailed-gi-counters", true, true, true, false, GiFeatureStateStatus.Fallback, "Counter readback is unavailable for the completed frame.");
        }

        private static string NonEmptyOr(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Stateful, calibrated evaluator for GI support and blackout evidence. It is deliberately
    /// separate from GPU counter ingestion so delayed readback, camera transitions, and noisy
    /// individual samples cannot become false release warnings.
    /// </summary>
    public sealed class GiWarningEvaluator
    {
        public const uint MinimumSampleCount = 64;
        public const double SupportHoleFractionThreshold = 0.05;
        public const double LargeAreaZeroFractionThreshold = 0.75;
        public const double LargeAreaCausalFractionThreshold = 0.20;
        public const int RequiredConsecutiveSupportHoleFrames = 2;
        public const int RequiredConsecutiveLargeAreaFrames = 3;

        private int _consecutiveSupportHoleFrames;
        private int _consecutiveLargeAreaFrames;

        public void Reset()
        {
            _consecutiveSupportHoleFrames = 0;
            _consecutiveLargeAreaFrames = 0;
        }

        public GiWarningEvaluationResult Evaluate(RendererDiagnostics diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            if (diagnostics.GlobalIlluminationDdgiActive == 0)
            {
                Reset();
                return new GiWarningEvaluationResult(
                    GiBlackFrameMetrics.Unavailable("DDGI is inactive."),
                    Array.Empty<GiDiagnosticWarning>());
            }

            if (diagnostics.DdgiDetailedCountersEnabled == 0 || diagnostics.DdgiInvestigationCountersReadbackValid == 0)
            {
                Reset();
                string reason = diagnostics.DdgiDetailedCountersEnabled == 0
                    ? "Detailed GI counters are disabled."
                    : "Completed detailed GI counter readback is unavailable.";
                var warning = new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.InvestigationCountersUnavailable,
                    GiDiagnosticSeverity.Info,
                    reason + " Counter-based support and blackout diagnostics are unavailable.",
                    "detailed-gi-counters",
                    0,
                    0,
                    "samples",
                    GiMetricFreshness.Unavailable,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    BuildCameraState(diagnostics),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Enable the explicit investigation capture mode and wait for a completed GPU readback.");
                return new GiWarningEvaluationResult(GiBlackFrameMetrics.Unavailable(reason), new[] { warning });
            }

            uint sampleCount = SaturatingAdd(
                diagnostics.DdgiForwardSimplePathSampleCount,
                diagnostics.DdgiForwardLegacyPathSampleCount);
            if (sampleCount == 0)
                sampleCount = diagnostics.SimpleDdgiGatherSampleCount;

            if (sampleCount < MinimumSampleCount)
            {
                Reset();
                return new GiWarningEvaluationResult(
                    GiBlackFrameMetrics.Unavailable($"Only {sampleCount} samples were read; at least {MinimumSampleCount} are required."),
                    Array.Empty<GiDiagnosticWarning>());
            }

            uint zeroFinal = diagnostics.DdgiForwardZeroFinalIndirectCount;
            uint zeroDdgiAndIbl = diagnostics.DdgiForwardZeroDdgiAndZeroIblCount;
            uint zeroIrradiance = diagnostics.SimpleDdgiZeroIrradianceSampleCount;
            uint outOfGrid = diagnostics.DdgiForwardOutOfGridSampleCount;
            double inverseSampleCount = 1.0 / sampleCount;
            double zeroFinalFraction = ClampFraction(zeroFinal * inverseSampleCount);
            double zeroDdgiAndIblFraction = ClampFraction(zeroDdgiAndIbl * inverseSampleCount);
            double outOfGridFraction = ClampFraction(outOfGrid * inverseSampleCount);
            uint irradianceDenominator = diagnostics.SimpleDdgiGatherSampleCount > 0
                ? diagnostics.SimpleDdgiGatherSampleCount
                : sampleCount;
            double zeroIrradianceFraction = ClampFraction(zeroIrradiance / (double)irradianceDenominator);

            bool transient = IsTransient(diagnostics, out string transientReason);
            bool supportHoleCandidate = outOfGridFraction >= SupportHoleFractionThreshold;
            bool causalBlackout = outOfGridFraction >= LargeAreaCausalFractionThreshold ||
                zeroIrradianceFraction >= LargeAreaCausalFractionThreshold ||
                zeroDdgiAndIblFraction >= LargeAreaCausalFractionThreshold;
            bool blackoutCandidate = zeroFinalFraction >= LargeAreaZeroFractionThreshold && causalBlackout;

            if (transient)
            {
                Reset();
            }
            else
            {
                _consecutiveSupportHoleFrames = supportHoleCandidate
                    ? _consecutiveSupportHoleFrames + 1
                    : 0;
                _consecutiveLargeAreaFrames = blackoutCandidate
                    ? _consecutiveLargeAreaFrames + 1
                    : 0;
            }

            bool supportHole = !transient && _consecutiveSupportHoleFrames >= RequiredConsecutiveSupportHoleFrames;
            bool largeAreaBlackout = !transient && _consecutiveLargeAreaFrames >= RequiredConsecutiveLargeAreaFrames;
            var metrics = new GiBlackFrameMetrics(
                true,
                sampleCount,
                zeroFinal,
                zeroDdgiAndIbl,
                zeroIrradiance,
                outOfGrid,
                zeroFinalFraction,
                zeroDdgiAndIblFraction,
                zeroIrradianceFraction,
                outOfGridFraction,
                false,
                0,
                transient,
                transientReason,
                _consecutiveLargeAreaFrames,
                _consecutiveSupportHoleFrames,
                largeAreaBlackout,
                supportHole,
                string.Empty);

            var warnings = new List<GiDiagnosticWarning>(2);
            if (supportHole)
            {
                warnings.Add(new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.SupportHole,
                    GiDiagnosticSeverity.Warning,
                    "DDGI support holes persist across completed readbacks.",
                    "ddgi-support",
                    outOfGridFraction,
                    SupportHoleFractionThreshold,
                    "fraction",
                    GiMetricFreshness.DelayedReadback,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    BuildCameraState(diagnostics),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Inspect receiver-volume coverage and use the support/fallback debug views before tuning lighting."));
            }
            if (largeAreaBlackout)
            {
                warnings.Add(new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.LargeAreaBlackout,
                    GiDiagnosticSeverity.Error,
                    "A large, persistent indirect-blackout pattern has causal DDGI support evidence.",
                    "ddgi-final-indirect",
                    zeroFinalFraction,
                    LargeAreaZeroFractionThreshold,
                    "fraction",
                    GiMetricFreshness.DelayedReadback,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    BuildCameraState(diagnostics),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Capture FinalIndirect, sampled irradiance, support, visibility, ownership, and fallback views from the same camera state."));
            }

            return new GiWarningEvaluationResult(metrics, warnings);
        }

        private static bool IsTransient(RendererDiagnostics diagnostics, out string reason)
        {
            if (diagnostics.SimpleDdgiRecentered != 0)
            {
                reason = "DDGI recentered this frame.";
                return true;
            }
            if (diagnostics.SimpleDdgiAtlasCleared != 0)
            {
                reason = "The DDGI atlas was cleared this frame.";
                return true;
            }
            if (diagnostics.SimpleDdgiAtlasFresh != 0)
            {
                reason = "The DDGI atlas is still fresh.";
                return true;
            }
            if (diagnostics.DdgiWarmupState != DdgiRuntimeWarmupState.SteadyState)
            {
                reason = "DDGI is not in steady state: " + diagnostics.DdgiWarmupState + ".";
                return true;
            }
            if (diagnostics.DdgiCameraMovementClass is DdgiCameraMovementClass.Teleport or DdgiCameraMovementClass.ViewResetOnly)
            {
                reason = "Camera movement invalidated stable DDGI evidence: " + diagnostics.DdgiCameraMovementClass + ".";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private static uint SaturatingAdd(uint left, uint right) => uint.MaxValue - left < right ? uint.MaxValue : left + right;
        private static double ClampFraction(double value) => Math.Clamp(value, 0.0, 1.0);
        private static string BuildCameraState(RendererDiagnostics diagnostics) =>
            diagnostics.CaptureCamera.CameraCutSerial == 0
                ? diagnostics.DdgiCameraMovementClass.ToString()
                : diagnostics.DdgiCameraMovementClass + "; cut=" + diagnostics.CaptureCamera.CameraCutSerial.ToString(CultureInfo.InvariantCulture);
    }

    public static class GiDiagnosticWarningFactory
    {
        public static IReadOnlyList<GiDiagnosticWarning> Create(
            RendererDiagnostics diagnostics,
            GiWarningEvaluationResult evaluation,
            IReadOnlyList<GiFeatureState> featureStates)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (evaluation == null)
                throw new ArgumentNullException(nameof(evaluation));
            if (featureStates == null)
                throw new ArgumentNullException(nameof(featureStates));

            var warnings = new List<GiDiagnosticWarning>(evaluation.Warnings.Count + 8);
            warnings.AddRange(evaluation.Warnings);
            foreach (GiFeatureState state in featureStates)
            {
                if (state.Name == "async-compute" && state.Status is GiFeatureStateStatus.Fallback or GiFeatureStateStatus.Unsupported)
                {
                    warnings.Add(CreateFeatureWarning(
                        GiDiagnosticWarningCode.AsyncComputeFallback,
                        GiDiagnosticSeverity.Warning,
                        "Async compute did not run for this capture: " + state.Reason,
                        state,
                        diagnostics,
                        "Compare graphics-only and async outputs; only enable async after the validated path improves P95/P99."));
                }
                else if (state.Name == "paged-far-field" && state.Requested && !state.Active)
                {
                    warnings.Add(CreateFeatureWarning(
                        GiDiagnosticWarningCode.PagedFarFieldInactive,
                        GiDiagnosticSeverity.Warning,
                        "Paged far-field was requested but is not active: " + state.Reason,
                        state,
                        diagnostics,
                        "Inspect the page-pool allocation and feature fallback reason; do not report paged far-field as active."));
                }
                else if (state.Name == "sampled-simple-ddgi-atlas" && state.Status == GiFeatureStateStatus.Fallback)
                {
                    warnings.Add(CreateFeatureWarning(
                        GiDiagnosticWarningCode.SampledAtlasFallback,
                        GiDiagnosticSeverity.Info,
                        "Sampled Simple DDGI atlas fell back: " + state.Reason,
                        state,
                        diagnostics,
                        "Use the canonical SSBO path for comparison or resolve the sampled-mirror allocation constraint."));
                }
                else if (state.Name == "emergency-gi-fallback" && state.Active)
                {
                    warnings.Add(CreateFeatureWarning(
                        GiDiagnosticWarningCode.EmergencyGiFallbackActive,
                        GiDiagnosticSeverity.Warning,
                        "Emergency GI fallback is active: dynamic GI is disabled and stable environment/reflection lighting is being used.",
                        state,
                        diagnostics,
                        "Treat this capture as rollback evidence, not a GI qualification run. Clear the kill switch only after the triggering issue is understood."));
                }
            }

            AddLayoutWarnings(warnings, diagnostics);
            AddRuntimeBudgetWarnings(warnings, diagnostics);

            return warnings;
        }

        private static void AddLayoutWarnings(List<GiDiagnosticWarning> warnings, RendererDiagnostics diagnostics)
        {
            SimpleDdgiLayoutTelemetry layout = diagnostics.SimpleDdgiLayout;
            if (!layout.IsAvailable)
                return;

            if (layout.WasDegraded)
            {
                GiDiagnosticSeverity severity = layout.AdmissionMode == SimpleDdgiLayoutAdmissionMode.Reject
                    ? GiDiagnosticSeverity.Error
                    : GiDiagnosticSeverity.Warning;
                warnings.Add(new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.SimpleDdgiLayoutDegraded,
                    severity,
                    $"Simple DDGI layout was degraded before allocation: {layout.RejectedVolumeCount} of {layout.RequestedVolumeCount} requested volumes were rejected ({layout.Summary}).",
                    "simple-ddgi-layout",
                    layout.RejectedVolumeCount,
                    0,
                    "volumes",
                    GiMetricFreshness.CurrentFrame,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    diagnostics.DdgiCameraMovementClass.ToString(),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Inspect the per-volume layout decisions, preserve receiver-hero priority, and either select a supported tier or explicitly approve the documented degraded layout."));
            }

            if (layout.RequestedProbeCount > layout.ProbeBudget)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "simple-ddgi-layout-probes",
                    "Requested Simple DDGI probes exceed the resolved tier layout budget.",
                    (ulong)Math.Max(0, layout.RequestedProbeCount),
                    (ulong)Math.Max(0, layout.ProbeBudget),
                    "probes",
                    "Reduce or reprioritize requested volume density, or select a tier that can represent the receiver layout before allocation.");
            }
            if (layout.RequestedPersistentBytes > layout.PersistentMemoryBudgetBytes)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "simple-ddgi-layout-memory",
                    "Requested Simple DDGI persistent storage exceeds the resolved tier layout budget.",
                    layout.RequestedPersistentBytes,
                    layout.PersistentMemoryBudgetBytes,
                    "bytes",
                    "Reduce layout memory, disable an optional mirror deliberately, or select a tier with sufficient persistent DDGI storage before allocation.");
            }
            if (layout.RequestedVolumeCount > layout.VolumeBudget)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "simple-ddgi-layout-volume-count",
                    "Requested Simple DDGI volume count exceeds the resolved layout limit.",
                    (ulong)Math.Max(0, layout.RequestedVolumeCount),
                    (ulong)Math.Max(0, layout.VolumeBudget),
                    "volumes",
                    "Merge or reprioritize authored and transition volumes before allocation; do not silently discard receiver coverage.");
            }
        }

        private static void AddRuntimeBudgetWarnings(List<GiDiagnosticWarning> warnings, RendererDiagnostics diagnostics)
        {
            if (diagnostics.AccelerationStructureBlasBudgetRejectedCount > 0)
            {
                warnings.Add(new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.AccelerationStructureIncomplete,
                    GiDiagnosticSeverity.Error,
                    "The complete detailed GI ray-query resident set was rejected; DDGI must remain on its safe fallback until residency succeeds.",
                    "gi-acceleration-structures",
                    (ulong)diagnostics.AccelerationStructureBlasBudgetRejectedCount,
                    0,
                    "meshes",
                    GiMetricFreshness.CurrentFrame,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    diagnostics.DdgiCameraMovementClass.ToString(),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Increase the resolved AS tier budget or reduce the coherent resident radius; never accept a TLAS with holes among admitted geometry."));
            }

            if (diagnostics.GlobalIlluminationDdgiActive != 0 &&
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0UL)
            {
                ulong ddgiBytes = SaturatingAdd(diagnostics.DdgiTextureBytes, diagnostics.DdgiBufferBytes);
                if (ddgiBytes > diagnostics.DdgiAtlasMemoryBudgetBytes)
                {
                    AddBudgetWarning(
                        warnings,
                        diagnostics,
                        "ddgi-storage",
                        "Live DDGI storage exceeds its configured hard tier budget.",
                        ddgiBytes,
                        diagnostics.DdgiAtlasMemoryBudgetBytes,
                        "bytes",
                        "Inspect canonical atlas, optional mirror, state, queue, and scratch accounting; reduce the resolved layout rather than raising an unrelated profile cap.");
                }
            }

            if (diagnostics.StreamedGiAccelerationStructuresFeatureEnabled != 0 &&
                diagnostics.AccelerationStructureMemoryBudgetBytes > 0UL &&
                diagnostics.AccelerationStructureResidentBytes > diagnostics.AccelerationStructureMemoryBudgetBytes)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "gi-acceleration-structures",
                    "Resident GI acceleration structures exceed their configured hard budget.",
                    diagnostics.AccelerationStructureResidentBytes,
                    diagnostics.AccelerationStructureMemoryBudgetBytes,
                    "bytes",
                    "Reduce the resident working set, choose a lower AS LOD for remote geometry, or use a formally resolved platform tier budget.");
            }

            if (diagnostics.FarFieldPagedFeatureEnabled != 0 &&
                diagnostics.FarFieldMemoryBudgetBytes > 0UL &&
                diagnostics.FarFieldCacheBytes > diagnostics.FarFieldMemoryBudgetBytes)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "paged-far-field",
                    "Far-field cache exceeds its configured hard budget.",
                    diagnostics.FarFieldCacheBytes,
                    diagnostics.FarFieldMemoryBudgetBytes,
                    "bytes",
                    "Reduce page-pool/cache pressure or lower the resolved far-field tier; preserve deterministic missing-page fallback.");
            }

            SimpleDdgiSchedulingTelemetry scheduling = diagnostics.SimpleDdgiScheduling;
            if (scheduling.IsAvailable && scheduling.ScheduledRequestCount > scheduling.ConfiguredRequestBudget)
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "simple-ddgi-scheduler-requests",
                    "Simple DDGI scheduler admitted more requests than its configured hard cap.",
                    (ulong)Math.Max(0, scheduling.ScheduledRequestCount),
                    (ulong)Math.Max(0, scheduling.ConfiguredRequestBudget),
                    "requests",
                    "Treat this as a scheduler correctness failure; clamp admission before command recording and retain the configured cap in capture metadata.");
            }
            if (scheduling.IsAvailable && scheduling.ScheduledPrimaryRayCount > (ulong)Math.Max(0, scheduling.ConfiguredPrimaryRayBudget))
            {
                AddBudgetWarning(
                    warnings,
                    diagnostics,
                    "simple-ddgi-scheduler-primary-rays",
                    "Simple DDGI scheduler admitted more primary rays than its configured hard cap.",
                    scheduling.ScheduledPrimaryRayCount,
                    (ulong)Math.Max(0, scheduling.ConfiguredPrimaryRayBudget),
                    "rays",
                    "Treat this as a scheduler correctness failure; reject the work before trace dispatch rather than redefining the tier from current output.");
            }
        }

        private static void AddBudgetWarning(
            List<GiDiagnosticWarning> warnings,
            RendererDiagnostics diagnostics,
            string feature,
            string message,
            ulong observed,
            ulong threshold,
            string unit,
            string action)
        {
            warnings.Add(new GiDiagnosticWarning(
                GiDiagnosticWarningCode.GiBudgetOverrun,
                GiDiagnosticSeverity.Error,
                message,
                feature,
                observed,
                threshold,
                unit,
                GiMetricFreshness.CurrentFrame,
                diagnostics.ActiveBudgetProfileName,
                diagnostics.CaptureRun.Scenario,
                diagnostics.DdgiCameraMovementClass.ToString(),
                diagnostics.CaptureFrame.FrameSerial,
                diagnostics.CaptureCamera.CameraCutSerial,
                action));
        }

        private static ulong SaturatingAdd(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private static GiDiagnosticWarning CreateFeatureWarning(
            GiDiagnosticWarningCode code,
            GiDiagnosticSeverity severity,
            string message,
            GiFeatureState state,
            RendererDiagnostics diagnostics,
            string action)
        {
            return new GiDiagnosticWarning(
                code,
                severity,
                message,
                state.Name,
                state.Active ? 1 : 0,
                1,
                "active",
                GiMetricFreshness.CurrentFrame,
                diagnostics.ActiveBudgetProfileName,
                diagnostics.CaptureRun.Scenario,
                diagnostics.DdgiCameraMovementClass.ToString(),
                diagnostics.CaptureFrame.FrameSerial,
                diagnostics.CaptureCamera.CameraCutSerial,
                action);
        }
    }

    public static class GiResidencyReporter
    {
        public static GiResidencySnapshot Create(RendererDiagnostics diagnostics, MemoryBudgetSnapshot memory)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (memory == null)
                throw new ArgumentNullException(nameof(memory));

            ulong trackedGiBytes = GetCategoryBytes(memory, MemoryBudgetCategory.GlobalIllumination);
            bool hasTrackedGiEntry = HasCategory(memory, MemoryBudgetCategory.GlobalIllumination);
            ulong uniqueResidentBytes = SaturatingAdd(diagnostics.GlobalIlluminationRenderTargetBytes, trackedGiBytes);
            ulong accelerationStructureTransientBytes = ResolveAccelerationStructureTransientBytes(diagnostics);
            ulong accelerationStructureTransientBudgetBytes = diagnostics.AccelerationStructureMemoryBudgetBytes == 0
                ? 0
                : AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(
                    diagnostics.AccelerationStructureMemoryBudgetBytes);
            bool farFieldConfigured = diagnostics.FarFieldPagedFeatureEnabled != 0 ||
                diagnostics.FarFieldCacheBytes != 0 ||
                diagnostics.FarFieldInstanceBufferBytes != 0;
            var components = new List<GiResidencyComponent>(7)
            {
                CreateComponent(
                    "GI render targets",
                    diagnostics.GlobalIlluminationRenderTargetBytes,
                    0,
                    false,
                    "Renderer render-target accounting; disjoint from allocation-tracker GI resources. No GI-specific render-target cap is declared.",
                    countsTowardCombinedBudget: false),
                CreateComponent(
                    "DDGI cache",
                    ResolveDdgiCacheBytes(diagnostics),
                    diagnostics.DdgiAtlasMemoryBudgetBytes,
                    false,
                    diagnostics.SimpleDdgiActive != 0
                        ? "Simple DDGI receiver-visible atlas, optional sampled mirror, and V2 private transport/source-cache storage."
                        : "DDGI irradiance and visibility atlas accounting."),
                CreateComponent(
                    "DDGI state and update queues",
                    ResolveDdgiStateAndQueueBytes(diagnostics),
                    diagnostics.DdgiAtlasMemoryBudgetBytes,
                    true,
                    "Probe state, update queue, and relocation/classification storage share the DDGI storage cap.",
                    countsTowardCombinedBudget: false),
                CreateComponent(
                    "Far-field cache",
                    ResolveFarFieldBytes(diagnostics),
                    diagnostics.FarFieldMemoryBudgetBytes,
                    false,
                    "Far-field page pool, table/cache, and bounded static page-bake input. The table byte counter is a subset of the cache total.",
                    countsTowardCombinedBudget: farFieldConfigured),
                CreateComponent(
                    "GI acceleration structures",
                    diagnostics.AccelerationStructureResidentBytes,
                    diagnostics.AccelerationStructureMemoryBudgetBytes,
                    false,
                    "Resident GI BLAS/TLAS allocations."),
                CreateComponent(
                    "GI acceleration-structure scratch and transient",
                    accelerationStructureTransientBytes,
                    accelerationStructureTransientBudgetBytes,
                    false,
                    "Retired BLAS/TLAS, scratch, TLAS instance input, and ray-query metadata are bounded separately from the resident BLAS/TLAS working set."),
                CreateComponent(
                    "DDGI scratch and transient",
                    diagnostics.DdgiRayScratchBytes,
                    diagnostics.DdgiAtlasMemoryBudgetBytes,
                    true,
                    "Ray scratch is bounded by the scheduled request/ray caps and shares the DDGI storage cap.",
                    countsTowardCombinedBudget: false)
            };

            ulong componentBudgets = 0;
            bool allActiveComponentBudgetsKnown = diagnostics.GlobalIlluminationEnabled != 0;
            foreach (GiResidencyComponent component in components)
            {
                if (component.CountsTowardCombinedBudget && component.BudgetBytes != 0)
                    componentBudgets = SaturatingAdd(componentBudgets, component.BudgetBytes);

                // An unused feature is not an unknown allocation. Conversely, an
                // allocated component with no declared cap must make the aggregate
                // metric unavailable rather than comparing it against a partial sum.
                if (component.Bytes != 0 && component.BudgetBytes == 0)
                {
                    allActiveComponentBudgetsKnown = false;
                }
            }

            return new GiResidencySnapshot(
                uniqueResidentBytes,
                allActiveComponentBudgetsKnown ? componentBudgets : 0,
                hasTrackedGiEntry || diagnostics.GlobalIlluminationRenderTargetBytes > 0,
                true,
                "GI render-target bytes plus the allocation tracker GlobalIllumination category; manager-facing component values are not summed.",
                components);
        }

        private static GiResidencyComponent CreateComponent(
            string name,
            ulong bytes,
            ulong budgetBytes,
            bool mayOverlap,
            string source,
            bool countsTowardCombinedBudget = true)
        {
            RenderBudgetStatus status = budgetBytes == 0
                ? RenderBudgetStatus.Unavailable
                : RenderBudgetEvaluator.Classify(bytes, budgetBytes);
            return new GiResidencyComponent(
                name,
                bytes,
                budgetBytes,
                status,
                mayOverlap,
                source,
                countsTowardCombinedBudget);
        }

        private static ulong ResolveDdgiCacheBytes(RendererDiagnostics diagnostics)
        {
            if (diagnostics.SimpleDdgiActive != 0)
            {
                // V2 deliberately keeps its Jacobi target private until a
                // completed generation is published, and stores source rays in
                // a separate persistent buffer. Both allocations are governed
                // by the same simple-DDGI layout cap; excluding them made a V2
                // capture appear substantially cheaper than it really was.
                return SaturatingAdd(
                    SaturatingAdd(
                        diagnostics.SimpleDdgiAtlasBytes,
                        diagnostics.SimpleDdgiTransportIrradianceAtlasBytes),
                    diagnostics.SimpleDdgiTransportSourceCacheBytes);
            }

            ulong atlasBytes = SaturatingAdd(
                diagnostics.DdgiCurrentIrradianceAtlasBytes,
                diagnostics.DdgiCurrentVisibilityAtlasBytes);
            return atlasBytes != 0 ? atlasBytes : diagnostics.DdgiTextureBytes;
        }

        private static ulong ResolveDdgiStateAndQueueBytes(RendererDiagnostics diagnostics)
        {
            return SaturatingAdd(
                SaturatingAdd(diagnostics.DdgiProbeStateBufferBytes, diagnostics.DdgiProbeUpdateQueueBytes),
                diagnostics.DdgiProbeRelocationClassificationBytes);
        }

        private static ulong ResolveFarFieldBytes(RendererDiagnostics diagnostics) =>
            SaturatingAdd(diagnostics.FarFieldCacheBytes, diagnostics.FarFieldInstanceBufferBytes);

        private static ulong ResolveAccelerationStructureTransientBytes(RendererDiagnostics diagnostics)
        {
            return SaturatingAdd(
                SaturatingAdd(diagnostics.AccelerationStructureRetiredBytes, diagnostics.AccelerationStructureScratchBytes),
                SaturatingAdd(
                    diagnostics.AccelerationStructureInstanceBufferBytes,
                    diagnostics.AccelerationStructureRayQueryMetadataBytes));
        }

        private static bool HasCategory(MemoryBudgetSnapshot snapshot, MemoryBudgetCategory category)
        {
            foreach (MemoryBudgetEntry entry in snapshot.Entries)
            {
                if (entry.Category == category)
                    return true;
            }
            return false;
        }

        private static ulong GetCategoryBytes(MemoryBudgetSnapshot snapshot, MemoryBudgetCategory category)
        {
            foreach (MemoryBudgetEntry entry in snapshot.Entries)
            {
                if (entry.Category == category)
                    return entry.Bytes;
            }
            return 0;
        }

        private static ulong SaturatingAdd(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }

    public static class ResolvedGiSettingsMetadataFactory
    {
        public static ResolvedGiSettingsMetadata Create(RendererDiagnostics diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            // This is a configuration fingerprint, not a per-frame world-state hash.  Dynamic
            // ring origins and physical offsets are persisted in DdgiVolumes for exact replay,
            // but deliberately excluded here so a camera-relative recenter does not make two
            // otherwise identical resolved configurations incomparable.
            var settings = new List<string>(96 + diagnostics.DdgiVolumes.Count + diagnostics.SimpleDdgiLayout.Volumes.Count);
            AddSetting(settings, "quality", diagnostics.ActiveQualityPreset.ToString());
            AddSetting(settings, "gi.requested", diagnostics.GlobalIlluminationRequested);
            AddSetting(settings, "gi.requestedMode", diagnostics.GlobalIlluminationRequestedMode.ToString());
            AddSetting(settings, "gi.effectiveMode", diagnostics.GlobalIlluminationMode.ToString());
            AddSetting(settings, "gi.requestedDebugView", diagnostics.GlobalIlluminationRequestedDebugView.ToString());
            AddSetting(settings, "gi.effectiveDebugView", diagnostics.GlobalIlluminationDebugView.ToString());
            AddSetting(settings, "gi.active", diagnostics.GlobalIlluminationEnabled);
            AddSetting(settings, "gi.emergencyFallback", diagnostics.GlobalIlluminationEmergencyFallbackEnabled);
            AddSetting(settings, "gi.fallbackReason", diagnostics.GlobalIlluminationFallbackReason);
            AddSetting(settings, "gi.indirectIntensity", diagnostics.GlobalIlluminationIndirectIntensity);
            AddSetting(settings, "gi.environmentFallbackIntensity", diagnostics.GlobalIlluminationEnvironmentFallbackIntensity);
            AddSetting(settings, "gi.ssgi.requested", diagnostics.GlobalIlluminationSsgiRequested);
            AddSetting(settings, "gi.ssgi.active", diagnostics.GlobalIlluminationSsgiActive);
            AddSetting(settings, "gi.ssgi.scale", diagnostics.SsgiResolutionScale);
            AddSetting(settings, "gi.ssgi.width", diagnostics.SsgiWidth);
            AddSetting(settings, "gi.ssgi.height", diagnostics.SsgiHeight);
            AddSetting(settings, "gi.ssgi.rays", diagnostics.SsgiRayCount);
            AddSetting(settings, "gi.ddgi.requested", diagnostics.GlobalIlluminationDdgiRequested);
            AddSetting(settings, "gi.ddgi.active", diagnostics.GlobalIlluminationDdgiActive);
            AddSetting(settings, "gi.simpleDdgi.requested", diagnostics.SimpleDdgiRequested);
            AddSetting(settings, "gi.simpleDdgi.active", diagnostics.SimpleDdgiActive);
            AddSetting(settings, "gi.simpleDdgi.transportV2.active", diagnostics.SimpleDdgiTransportV2Active);
            AddSetting(settings, "gi.simpleDdgi.automaticProbeDensity.active", diagnostics.SimpleDdgiAutomaticProbeDensityActive);
            AddSetting(settings, "gi.simpleDdgi.transport.relaxation", diagnostics.SimpleDdgiTransportSolverRelaxation);
            AddSetting(settings, "gi.simpleDdgi.transport.albedoClamp", diagnostics.SimpleDdgiTransportAlbedoClamp);
            AddSetting(settings, "gi.simpleDdgi.transport.residualThreshold", diagnostics.SimpleDdgiTransportResidualThreshold);
            AddSetting(settings, "gi.simpleDdgi.transport.maximumSolverGenerations", diagnostics.SimpleDdgiTransportMaximumSolverGenerations);
            AddSetting(settings, "gi.simpleDdgi.transport.sourceRefreshFrames", diagnostics.SimpleDdgiTransportSourceRefreshFrames);
            AddSetting(settings, "gi.simpleDdgi.transport.globalConvergenceElapsedFrames", diagnostics.SimpleDdgiTransportGlobalConvergenceElapsedFrames);
            AddSetting(settings, "gi.simpleDdgi.transport.calibrationChangeCount", diagnostics.SimpleDdgiTransportCalibrationChangeCount);
            AddSetting(settings, "gi.rayQuery.requested", diagnostics.GlobalIlluminationRayQueryRequested);
            AddSetting(settings, "gi.rayQuery.supported", diagnostics.GlobalIlluminationRayQuerySupported);
            AddSetting(settings, "gi.rayQuery.active", diagnostics.GlobalIlluminationRayQueryActive);

            AddSetting(settings, "ddgi.quality", diagnostics.DdgiQualityTier.ToString());
            AddSetting(settings, "ddgi.scheduler", diagnostics.DdgiSchedulerMode.ToString());
            AddSetting(settings, "ddgi.maxActiveProbes", diagnostics.DdgiMaxActiveProbeBudget);
            AddSetting(settings, "ddgi.maxUpdatesPerFrame", diagnostics.DdgiMaxProbeUpdatesPerFrame);
            AddSetting(settings, "ddgi.requestBudget", diagnostics.DdgiProbeUpdateRequestBudget);
            AddSetting(settings, "ddgi.primaryRayBudget", diagnostics.DdgiProbeUpdatePrimaryRayBudget);
            AddSetting(settings, "ddgi.scheduledRequestBudget", diagnostics.DdgiScheduledRequestBudget);
            AddSetting(settings, "ddgi.scheduledPrimaryRayBudget", diagnostics.DdgiScheduledPrimaryRayBudget);
            AddSetting(settings, "ddgi.raysPerProbe", diagnostics.DdgiRaysPerProbe);
            AddSetting(settings, "ddgi.atlasMemoryBudgetBytes", diagnostics.DdgiAtlasMemoryBudgetBytes);
            AddSetting(settings, "ddgi.adaptiveBudgetScale", diagnostics.DdgiAdaptiveBudgetScale);
            AddSetting(settings, "ddgi.adaptiveBudgetReduced", diagnostics.DdgiAdaptiveBudgetReduced);
            AddSetting(settings, "ddgi.emergencyDegrade", diagnostics.DdgiEmergencyDegradeActive);
            AddSetting(settings, "ddgi.adaptiveBudgetReason", diagnostics.DdgiAdaptiveBudgetReason);
            AddSetting(settings, "ddgi.effectiveMaxShadedLights", diagnostics.DdgiEffectiveMaxShadedLights);
            AddSetting(settings, "ddgi.lightSelectionMode", diagnostics.DdgiLightSelectionMode);
            AddSetting(settings, "ddgi.emissiveSourceRevision", diagnostics.DdgiEmissiveSourceRevision);
            AddSetting(settings, "ddgi.gpuSchedulerFallback", diagnostics.DdgiGpuSchedulerFallbackActive);
            AddSetting(settings, "ddgi.gpuSchedulerFallbackReason", diagnostics.DdgiGpuSchedulerFallbackReason);

            AddSetting(settings, "sampledAtlas.requested", diagnostics.SimpleDdgiSampledAtlasRequested);
            AddSetting(settings, "sampledAtlas.active", diagnostics.SimpleDdgiSampledAtlasActive);
            AddSetting(settings, "sampledAtlas.groups", diagnostics.SimpleDdgiSampledAtlasGroupCount);
            AddSetting(settings, "sampledAtlas.layersPerTexture", diagnostics.SimpleDdgiSampledAtlasLayersPerTexture);
            AddSetting(settings, "sampledAtlas.fallbackReason", diagnostics.SimpleDdgiSampledAtlasFallbackReason);
            AddSetting(settings, "farField.requested", diagnostics.FarFieldPagedFeatureEnabled);
            AddSetting(settings, "farField.active", diagnostics.FarFieldPagedMode);
            AddSetting(settings, "farField.pagePoolCapacity", diagnostics.FarFieldPagePoolCapacity);
            AddSetting(settings, "farField.memoryBudgetBytes", diagnostics.FarFieldMemoryBudgetBytes);
            AddSetting(settings, "asStreaming.requested", diagnostics.StreamedGiAccelerationStructuresFeatureEnabled);
            AddSetting(settings, "asStreaming.active", diagnostics.AccelerationStructureStreamingEnabled);
            AddSetting(settings, "asStreaming.memoryBudgetBytes", diagnostics.AccelerationStructureMemoryBudgetBytes);
            AddSetting(settings, "asStreaming.fallbackReason", diagnostics.AccelerationStructureFallbackReason);

            AddLightingSettings(settings, diagnostics);
            AddLayoutSettings(settings, diagnostics.SimpleDdgiLayout);
            AddSchedulerSettings(settings, diagnostics.SimpleDdgiScheduling, diagnostics.SimpleDdgiSchedulerPolicy);
            AddFeatureStateSettings(settings, diagnostics);
            AddVolumeSettings(settings, diagnostics.DdgiVolumes);

            string canonical = string.Join("\n", settings);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            string stableHash = Convert.ToHexString(hash).ToLowerInvariant();
            return new ResolvedGiSettingsMetadata(
                stableHash,
                string.Join("; ", settings),
                settings);
        }

        private static void AddLightingSettings(List<string> settings, RendererDiagnostics diagnostics)
        {
            AddSetting(settings, "lighting.environment.enabled", diagnostics.EnvironmentEnabled);
            AddSetting(settings, "lighting.environment.sourceKind", diagnostics.EnvironmentSourceKind.ToString());
            AddSetting(settings, "lighting.environment.sourcePath", diagnostics.EnvironmentSourcePath);
            AddSetting(settings, "lighting.environment.usesFallback", diagnostics.EnvironmentUsesFallback);
            AddSetting(settings, "lighting.environment.skyIntensity", diagnostics.SkyIntensity);
            AddSetting(settings, "lighting.environment.diffuseIblIntensity", diagnostics.DiffuseIblIntensity);
            AddSetting(settings, "lighting.environment.specularIblIntensity", diagnostics.SpecularIblIntensity);
            AddSetting(settings, "lighting.exposure", diagnostics.Exposure);
            AddSetting(settings, "lighting.autoExposure", diagnostics.AutoExposureEnabled);
            AddSetting(settings, "lighting.toneMapper", diagnostics.ToneMapper.ToString());
            AddSetting(settings, "lighting.directionalShadows", diagnostics.DirectionalShadowsEnabled);
            AddSetting(settings, "lighting.directionalShadowMapSize", diagnostics.DirectionalShadowMapSize);
            AddSetting(settings, "lighting.directionalShadowCascades", diagnostics.DirectionalShadowCascadeCount);
            AddSetting(settings, "lighting.directionalShadowMaxDistance", diagnostics.DirectionalShadowRuntime.ConfiguredMaxDistance);
            AddSetting(settings, "lighting.directionalShadowCascadeBlendFraction", diagnostics.DirectionalShadowRuntime.CascadeBlendFraction);
            AddSetting(settings, "lighting.shadowNormalBias", diagnostics.ShadowNormalBias);
            AddSetting(settings, "lighting.shadowSlopeBias", diagnostics.ShadowSlopeScaledDepthBias);
            AddSetting(settings, "lighting.directionalShadowPcfRadius", diagnostics.DirectionalShadowPcfRadius);
            AddSetting(settings, "lighting.ambientOcclusion", diagnostics.AmbientOcclusionEnabled);
            AddSetting(settings, "lighting.ambientOcclusionIntensity", diagnostics.AmbientOcclusionIntensity);
            AddSetting(settings, "lighting.reflections", diagnostics.ReflectionsEnabled);
            AddSetting(settings, "lighting.reflectionMode", diagnostics.ReflectionMode.ToString());
        }

        private static void AddLayoutSettings(List<string> settings, SimpleDdgiLayoutTelemetry layout)
        {
            AddSetting(settings, "layout.available", layout.IsAvailable ? 1 : 0);
            if (!layout.IsAvailable)
            {
                AddSetting(settings, "layout.unavailableReason", layout.Summary);
                return;
            }

            AddSetting(settings, "layout.tier", layout.Tier.ToString());
            AddSetting(settings, "layout.admission", layout.AdmissionMode.ToString());
            AddSetting(settings, "layout.probeBudget", layout.ProbeBudget);
            AddSetting(settings, "layout.persistentBudgetBytes", layout.PersistentMemoryBudgetBytes);
            AddSetting(settings, "layout.volumeBudget", layout.VolumeBudget);
            AddSetting(settings, "layout.requestedProbes", layout.RequestedProbeCount);
            AddSetting(settings, "layout.acceptedProbes", layout.AcceptedProbeCount);
            AddSetting(settings, "layout.requestedPersistentBytes", layout.RequestedPersistentBytes);
            AddSetting(settings, "layout.acceptedPersistentBytes", layout.AcceptedPersistentBytes);
            AddSetting(settings, "layout.requestedVolumes", layout.RequestedVolumeCount);
            AddSetting(settings, "layout.acceptedVolumes", layout.AcceptedVolumeCount);
            AddSetting(settings, "layout.rejectedVolumes", layout.RejectedVolumeCount);
            AddSetting(settings, "layout.degraded", layout.WasDegraded ? 1 : 0);

            var volumes = new List<SimpleDdgiLayoutVolumeTelemetry>(layout.Volumes);
            volumes.Sort(static (left, right) =>
            {
                int ordinal = left.SourceOrdinal.CompareTo(right.SourceOrdinal);
                return ordinal != 0 ? ordinal : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            for (int index = 0; index < volumes.Count; index++)
            {
                SimpleDdgiLayoutVolumeTelemetry volume = volumes[index];
                AddSetting(settings, "layout.request[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    string.Join("|", new[]
                    {
                        volume.Id,
                        volume.SourceOrdinal.ToString(CultureInfo.InvariantCulture),
                        volume.IsAuthored ? "authored" : "ring",
                        volume.IntendedPurpose.ToString(),
                        volume.Priority.ToString(CultureInfo.InvariantCulture),
                        volume.ProbeSpacing.ToString("R", CultureInfo.InvariantCulture),
                        volume.RequestedProbeCount.ToString(CultureInfo.InvariantCulture),
                        volume.AcceptedProbeCount.ToString(CultureInfo.InvariantCulture),
                        volume.RequestedPersistentBytes.ToString(CultureInfo.InvariantCulture),
                        volume.Decision.ToString(),
                        volume.Reason
                    }));
            }
        }

        private static void AddSchedulerSettings(
            List<string> settings,
            SimpleDdgiSchedulingTelemetry scheduling,
            SimpleDdgiSchedulerPolicyTelemetry policy)
        {
            AddSetting(settings, "scheduler.available", scheduling.IsAvailable ? 1 : 0);
            AddSetting(settings, "scheduler.requestBudget", scheduling.ConfiguredRequestBudget);
            AddSetting(settings, "scheduler.primaryRayBudget", scheduling.ConfiguredPrimaryRayBudget);
            AddSetting(settings, "scheduler.completionSemantics", scheduling.CompletionSemantics);
            AddSetting(settings, "scheduler.policy.available", policy.IsAvailable ? 1 : 0);
            AddSetting(settings, "scheduler.policy.configuredRequestBudget", policy.ConfiguredRequestBudget);
            AddSetting(settings, "scheduler.policy.effectiveRequestBudget", policy.EffectiveRequestBudget);
            AddSetting(settings, "scheduler.policy.targetGpuMicroseconds", policy.TargetGpuMicroseconds);
            AddSetting(settings, "scheduler.policy.deterministicFixedBudget", policy.DeterministicFixedBudget ? 1 : 0);
            AddSetting(settings, "scheduler.policy.pressureReason", policy.PressureReason);
            AddSetting(settings, "scheduler.policy.unavailableReason", policy.UnavailableReason);
        }

        private static void AddFeatureStateSettings(List<string> settings, RendererDiagnostics diagnostics)
        {
            IReadOnlyList<GiFeatureState> source = diagnostics.GiFeatureStates.Count > 0
                ? diagnostics.GiFeatureStates
                : GiFeatureStateFactory.Create(diagnostics);
            var states = new List<GiFeatureState>(source);
            states.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            for (int index = 0; index < states.Count; index++)
            {
                GiFeatureState state = states[index];
                AddSetting(settings, "feature[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    string.Join("|", new[]
                    {
                        state.Name,
                        state.Compiled ? "1" : "0",
                        state.Supported ? "1" : "0",
                        state.Requested ? "1" : "0",
                        state.Active ? "1" : "0",
                        state.Status.ToString(),
                        state.Reason
                    }));
            }
        }

        private static void AddVolumeSettings(List<string> settings, IReadOnlyList<DdgiVolumeDiagnosticsEntry> source)
        {
            var volumes = new List<DdgiVolumeDiagnosticsEntry>(source);
            volumes.Sort(static (left, right) =>
            {
                int index = left.VolumeIndex.CompareTo(right.VolumeIndex);
                if (index != 0)
                    return index;
                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0 ? kind : left.CascadeIndex.CompareTo(right.CascadeIndex);
            });
            for (int index = 0; index < volumes.Count; index++)
            {
                DdgiVolumeDiagnosticsEntry volume = volumes[index];
                AddSetting(settings, "volume[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    string.Join("|", new[]
                    {
                        volume.Kind.ToString(),
                        volume.CascadeIndex.ToString(CultureInfo.InvariantCulture),
                        volume.ProbeCount.ToString(CultureInfo.InvariantCulture),
                        volume.RaysPerProbe.ToString(CultureInfo.InvariantCulture),
                        volume.MaxProbeUpdatesPerFrame.ToString(CultureInfo.InvariantCulture),
                        volume.SizeX.ToString("R", CultureInfo.InvariantCulture),
                        volume.SizeY.ToString("R", CultureInfo.InvariantCulture),
                        volume.SizeZ.ToString("R", CultureInfo.InvariantCulture),
                        volume.ProbeSpacingX.ToString("R", CultureInfo.InvariantCulture),
                        volume.ProbeSpacingY.ToString("R", CultureInfo.InvariantCulture),
                        volume.ProbeSpacingZ.ToString("R", CultureInfo.InvariantCulture),
                        volume.IntendedPurpose.ToString(),
                        volume.AuthoredPriority.ToString(CultureInfo.InvariantCulture),
                        volume.LayoutDecision?.ToString() ?? "unavailable",
                        volume.LayoutDecisionReason,
                        volume.LayoutRequestedProbeCount.ToString(CultureInfo.InvariantCulture),
                        volume.LayoutAcceptedProbeCount.ToString(CultureInfo.InvariantCulture),
                        volume.LayoutRequestedPersistentBytes.ToString(CultureInfo.InvariantCulture),
                        volume.LayoutAcceptedPersistentBytes.ToString(CultureInfo.InvariantCulture)
                    }));
            }
        }

        private static void AddSetting(List<string> settings, string key, string? value) =>
            settings.Add(key + "=" + Escape(value));

        private static void AddSetting(List<string> settings, string key, int value) =>
            AddSetting(settings, key, value.ToString(CultureInfo.InvariantCulture));

        private static void AddSetting(List<string> settings, string key, uint value) =>
            AddSetting(settings, key, value.ToString(CultureInfo.InvariantCulture));

        private static void AddSetting(List<string> settings, string key, ulong value) =>
            AddSetting(settings, key, value.ToString(CultureInfo.InvariantCulture));

        private static void AddSetting(List<string> settings, string key, float value) =>
            AddSetting(settings, key, value.ToString("R", CultureInfo.InvariantCulture));

        private static string Escape(string? value) => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }
}
