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
        /// <summary>The stepped-sun source cohort cannot meet, or has exceeded, its declared sweep budget.</summary>
        SourceSweepBudgetExceeded,
        /// <summary>A requested or live GI component exceeded its declared hard budget.</summary>
        GiBudgetOverrun
    }

    public enum GiMeasurementMode
    {
        Production,
        NormalTelemetry,
        DetailedInvestigation
    }

    /// <summary>
    /// Build-time boundary shared by CPU flags and shader compilation. Release,
    /// ShippingPerformance, and ProfileSymbols cannot enable DDGI investigation
    /// atomics at runtime; Debug and DetailedInvestigation retain the explicit
    /// diagnostic path.
    /// </summary>
    public static class RendererBuildFeatures
    {
#if DEBUG || NJULF_DEVELOPMENT || NJULF_DETAILED_INVESTIGATION
        /// <summary>
        /// Receiver-side GI visualization branches are present. Development
        /// retains these read-only views without compiling the expensive
        /// investigation counter and atomic paths.
        /// </summary>
        public const bool DdgiVisualDebugViewsCompiled = true;
#else
        public const bool DdgiVisualDebugViewsCompiled = false;
#endif

#if DEBUG || NJULF_DETAILED_INVESTIGATION
        public const bool DetailedDdgiDiagnosticsCompiled = true;
#else
        public const bool DetailedDdgiDiagnosticsCompiled = false;
#endif

        /// <summary>
        /// Raw transport-cache projection is intentionally absent from receiver
        /// fragment shaders. NVIDIA's Vulkan backend cannot reliably lower that
        /// storage-access graph; a future implementation must project it in
        /// compute and expose only a compact receiver resource.
        /// </summary>
        public const bool SourceCacheRadianceReceiverDiagnosticCompiled = false;

        /// <summary>
        /// The compact Simple-DDGI receiver payload does not carry the
        /// scheduler's update-reason byte or the classifier's continuous
        /// invalidity score. Advertising those branches produced valid black
        /// images that looked like a disabled DDGI field.
        /// </summary>
        public const bool ExtendedProbeMetadataReceiverDiagnosticsCompiled = false;

        private static bool RequiresExtendedProbeMetadata(
            GlobalIlluminationDebugView view) =>
            view is GlobalIlluminationDebugView.DdgiUpdateReasons or
                GlobalIlluminationDebugView.DdgiClassificationInvalidScore;

        /// <summary>
        /// Returns true for receiver views whose inputs and branch fan-out are
        /// deliberately absent from production Simple-DDGI shaders.  General GI
        /// views (final indirect, far-field inspection, and material provenance)
        /// remain usable in production artifacts.
        /// </summary>
        public static bool RequiresDdgiVisualDebugShaders(
            GlobalIlluminationDebugView view)
        {
            return view is GlobalIlluminationDebugView.DdgiIrradiance
                or GlobalIlluminationDebugView.DdgiVisibility
                or GlobalIlluminationDebugView.DdgiProbeIndex
                or GlobalIlluminationDebugView.DdgiProbeState
                or GlobalIlluminationDebugView.DdgiProbeRelocation
                or GlobalIlluminationDebugView.DdgiLeakClamp
                or GlobalIlluminationDebugView.DdgiCoverage
                or GlobalIlluminationDebugView.DdgiCascadeSelection
                or GlobalIlluminationDebugView.DdgiCascadeBlendWeight
                or GlobalIlluminationDebugView.DdgiUpdateReasons
                or GlobalIlluminationDebugView.DdgiRayBudget
                or GlobalIlluminationDebugView.DdgiGatherLocalVolume
                or GlobalIlluminationDebugView.DdgiGatherClipmap
                or GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight
                or GlobalIlluminationDebugView.DdgiGatherFallback
                or GlobalIlluminationDebugView.DdgiRawDiffuse
                or GlobalIlluminationDebugView.DdgiSuppressionMask
                or GlobalIlluminationDebugView.DdgiEffectiveWeight
                or GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight
                or GlobalIlluminationDebugView.DdgiClassificationInvalidScore
                or GlobalIlluminationDebugView.DdgiVisibilityMoments
                or GlobalIlluminationDebugView.DdgiSpatialCoverage
                or GlobalIlluminationDebugView.DdgiSupportCoverage
                or GlobalIlluminationDebugView.DdgiDataConfidence
                or GlobalIlluminationDebugView.DdgiVisibilityConfidence
                or GlobalIlluminationDebugView.DdgiConfidenceChain
                or GlobalIlluminationDebugView.DdgiProbeLogicalPosition
                or GlobalIlluminationDebugView.DdgiProbeRelocatedPosition
                or GlobalIlluminationDebugView.DdgiProbeRelocationDirection
                or GlobalIlluminationDebugView.DdgiGatherBlendWeight
                or GlobalIlluminationDebugView.DdgiSampledIrradiance
                or GlobalIlluminationDebugView.DdgiFinalDiffuse
                or GlobalIlluminationDebugView.DdgiConfidenceBypass
                or GlobalIlluminationDebugView.DdgiDirectionalSupport
                or GlobalIlluminationDebugView.DdgiSourceCacheRadiance
                or GlobalIlluminationDebugView.DdgiProbeResidency
                or GlobalIlluminationDebugView.DdgiResidencyFallback
                or GlobalIlluminationDebugView.DdgiPageAge
                or GlobalIlluminationDebugView.DdgiPhysicalPage;
        }

        /// <summary>
        /// Compatibility name retained for tooling that predates the split
        /// between read-only visual views and detailed counter instrumentation.
        /// </summary>
        public static bool RequiresDetailedDdgiReceiverDiagnostics(
            GlobalIlluminationDebugView view) =>
            RequiresDdgiVisualDebugShaders(view);

        public static bool IsGlobalIlluminationDebugViewAvailable(
            GlobalIlluminationDebugView view) =>
            (SourceCacheRadianceReceiverDiagnosticCompiled ||
             view != GlobalIlluminationDebugView.DdgiSourceCacheRadiance) &&
            (ExtendedProbeMetadataReceiverDiagnosticsCompiled ||
             !RequiresExtendedProbeMetadata(view)) &&
            (DdgiVisualDebugViewsCompiled ||
             !RequiresDdgiVisualDebugShaders(view));

        /// <summary>
        /// Resolves an authored request to a branch that exists in the running
        /// shader bundle.  The authored setting is retained separately in
        /// diagnostics, so an unavailable request is never reported as accepted.
        /// </summary>
        public static GlobalIlluminationDebugView ResolveGlobalIlluminationDebugView(
            GlobalIlluminationDebugView view) =>
            IsGlobalIlluminationDebugViewAvailable(view)
                ? view
                : GlobalIlluminationDebugView.None;

        public static string GetGlobalIlluminationDebugViewAvailabilityReason(
            GlobalIlluminationDebugView view)
        {
            if (IsGlobalIlluminationDebugViewAvailable(view))
                return string.Empty;
            if (view == GlobalIlluminationDebugView.DdgiSourceCacheRadiance)
            {
                return "Raw DDGI source-cache inspection is not compiled into receiver shaders; " +
                    "it requires a compute-projected diagnostic resource.";
            }
            if (RequiresExtendedProbeMetadata(view))
            {
                return "The compact Simple-DDGI receiver payload does not publish this value. " +
                    "Use Probe State / Probe Relocation for classifier evidence, or the " +
                    "DDGI Updated Probes / Update Reasons overlay for scheduler evidence.";
            }

            return "The requested DDGI receiver view requires a Development, Debug, or " +
                "DetailedInvestigation shader artifact.";
        }
    }

    public enum GiMetricFreshness
    {
        CurrentFrame,
        DelayedReadback,
        Aggregated,
        Unavailable
    }

    /// <summary>
    /// Meaning of an exported numeric metric. Capture consumers must not compare
    /// a capacity or configured budget as if it were executed work.
    /// </summary>
    public enum PerformanceMetricSemantic
    {
        Unavailable,
        Exact,
        SampledEstimate,
        Capacity,
        ConfiguredBudget,
        EmittedWork
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
        string Reason)
    {
        public SimpleDdgiLayoutAdmissionClass AdmissionClass { get; init; } =
            SimpleDdgiLayoutAdmissionClass.Required;
    }

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
        public SimpleDdgiProbeResidencyMode ResidencyMode { get; init; } =
            SimpleDdgiProbeResidencyMode.Dense;
        public int DensePayloadProbeCount { get; init; }
        public int SparseVirtualProbeCount { get; init; }
        public int SparseVirtualPageCount { get; init; }
        public int SparsePhysicalPageCapacity { get; init; }
        public int PhysicalProbeCapacity { get; init; }
        public int SampledAtlasPhysicalProbeCapacity { get; init; }
        public int SparsePagePaddingProbeCount { get; init; }
        public int SampledAtlasPaddingProbeCount { get; init; }
        public ulong SampledAtlasPaddingBytes { get; init; }
        public ulong DenseEquivalentBytes { get; init; }
        public ulong AllocatedSparseBytes { get; init; }
        public ulong AvoidedBytes { get; init; }
        public ulong ResidencyArenaBytes { get; init; }
        public bool PhysicalPageBudgetWasReduced { get; init; }
        public string PhysicalPageBudgetDecision { get; init; } = string.Empty;
        public string ResidencyFallbackReason { get; init; } = string.Empty;
        public int RequiredRejectedVolumeCount { get; init; }
        public int OptionalRejectedVolumeCount { get; init; }

        /// <summary>
        /// Rejections captured before admission classes were serialized are treated as
        /// required. This preserves the conservative meaning of older captures while newer
        /// captures can distinguish an optional refinement fallback from a broken base field.
        /// </summary>
        public int UnclassifiedRejectedVolumeCount => Math.Max(
            0,
            RejectedVolumeCount -
            RequiredRejectedVolumeCount -
            OptionalRejectedVolumeCount);

        public bool HasRequiredDegradation =>
            PhysicalPageBudgetWasReduced ||
            RequiredRejectedVolumeCount > 0 ||
            UnclassifiedRejectedVolumeCount > 0;

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
                // Admission owns the exact cumulative capacity model, including
                // update/readback caps and sampled-atlas growth quanta. Persist
                // its incremental evidence directly instead of reconstructing a
                // potentially different standalone plan here.
                ulong requestedPersistentBytes = decision.RequestedPersistentBytes;
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
                    decision.Reason)
                {
                    AdmissionClass = request.AdmissionClass
                });
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
                volumes)
            {
                ResidencyMode = report.AcceptedMemoryPlan.ResidencyMode,
                DensePayloadProbeCount = report.DensePayloadProbeCount,
                SparseVirtualProbeCount = report.SparseVirtualProbeCount,
                SparseVirtualPageCount = report.SparseVirtualPageCount,
                SparsePhysicalPageCapacity = report.SparsePhysicalPageCapacity,
                PhysicalProbeCapacity = report.PhysicalProbeCapacity,
                SampledAtlasPhysicalProbeCapacity =
                    report.SampledAtlasPhysicalProbeCapacity,
                SparsePagePaddingProbeCount =
                    report.SparsePagePaddingProbeCount,
                SampledAtlasPaddingProbeCount =
                    report.SampledAtlasPaddingProbeCount,
                SampledAtlasPaddingBytes = report.SampledAtlasPaddingBytes,
                DenseEquivalentBytes = report.DenseEquivalentBytes,
                AllocatedSparseBytes = report.AllocatedSparseBytes,
                AvoidedBytes = report.AvoidedBytes,
                ResidencyArenaBytes = report.AcceptedMemoryPlan.ResidencyArenaBytes,
                PhysicalPageBudgetWasReduced = report.PhysicalPageBudgetWasReduced,
                PhysicalPageBudgetDecision = report.PhysicalPageBudgetDecision,
                ResidencyFallbackReason = report.ResidencyFallbackReason,
                RequiredRejectedVolumeCount = report.RequiredRejectionCount,
                OptionalRejectedVolumeCount = report.OptionalRejectionCount
            };
        }
    }

    public sealed record SimpleDdgiResidencyRingTelemetry(
        int RingIndex,
        int VirtualProbeCount,
        int ResidentProbeCount,
        int ActiveResidentProbeCount,
        int InactiveResidentProbeCount,
        int DemandedPageCount,
        int ConvergedResidentProbeCount);

    /// <summary>
    /// Fixed-size, fence-complete sparse-page summary plus exact allocation-plan
    /// bytes. It never contains a page list and therefore remains O(1) to export.
    /// </summary>
    public sealed record SimpleDdgiProbeResidencyTelemetry(
        bool IsAvailable,
        SimpleDdgiProbeResidencyMode Mode,
        bool SparseAuthoritative,
        string FallbackReason)
    {
        public uint CurrentResourceGeneration { get; init; }
        /// <summary>
        /// True when runtime validation stopped all page-table mutation. A
        /// valid frozen map may remain readable, but the feature is degraded
        /// until a fresh resource generation is bootstrapped.
        /// </summary>
        public bool MutationFrozen { get; init; }
        public bool DevelopmentMutationFrozen { get; init; }
        public bool ResidencyStateValid { get; init; }
        public bool FeedbackValid { get; init; }
        public ulong FeedbackFrameSerial { get; init; }
        public uint FeedbackResourceGeneration { get; init; }
        public uint MappingGeneration { get; init; }
        public uint DemandEpoch { get; init; }
        public int VirtualProbeCount { get; init; }
        public int VirtualPageCount { get; init; }
        public int DensePhysicalProbeCount { get; init; }
        public int SparsePhysicalPageCapacity { get; init; }
        public int PhysicalProbeCapacity { get; init; }
        public int ResidentPageCount { get; init; }
        public int FreePageCount { get; init; }
        public int InitializingPageCount { get; init; }
        public int PublishedPageCount { get; init; }
        public int SuppressedPageCount { get; init; }
        public int ResidentProbeCount { get; init; }
        public int NonResidentVirtualProbeCount { get; init; }
        public int ActiveResidentProbeCount { get; init; }
        public int InactiveResidentProbeCount { get; init; }
        public int ConvergedResidentProbeCount { get; init; }
        public int VisibleDemandPageCount { get; init; }
        public int ReceiverDemandPageCount { get; init; }
        public int ReceiverRequestCount { get; init; }
        public int ReceiverRequestOverflowCount { get; init; }
        public bool PredictorComparisonValid { get; init; }
        public int PredictorActualPageCount { get; init; }
        public int PredictorTruePositivePageCount { get; init; }
        public int PredictorFalseNegativePageCount { get; init; }
        public int PredictorFalsePositivePageCount { get; init; }
        public double PredictorFalseNegativeRate { get; init; }
        public double PredictorInflationRatio { get; init; }
        public int RetainedPageCount { get; init; }
        public int RetainedAge0To15PageCount { get; init; }
        public int RetainedAge16To63PageCount { get; init; }
        public int RetainedAge64To255PageCount { get; init; }
        public int RetainedAge256PlusPageCount { get; init; }
        public int VisibleDemandResidentHitPageCount { get; init; }
        public int VisibleDemandMissingPageCount { get; init; }
        public int AdmissionCount { get; init; }
        public int EvictionCount { get; init; }
        public int FailedAdmissionCount { get; init; }
        public int PoolPressureFrameCount { get; init; }
        public int ConsecutivePressureFrames { get; init; }
        public int MaximumConsecutivePressureFrames { get; init; }
        public int PageTableReverseDisagreementCount { get; init; }
        public int DuplicateVirtualOwnerCount { get; init; }
        public int DuplicatePhysicalOwnerCount { get; init; }
        public int StaleVirtualRequestCount { get; init; }
        public int StaleMappingRequestCount { get; init; }
        public int StaleResourceRequestCount { get; init; }
        public int OutOfRangeRequestCount { get; init; }
        public int NonResidentGatherRejectionCount { get; init; }
        public int CoarserFallbackCount { get; init; }
        public int SuppressionCount { get; init; }
        public int RetryCount { get; init; }
        public int DevelopmentPinnedPageCount { get; init; }
        public ulong DevelopmentControlCommandCount { get; init; }
        public int LastDevelopmentControlledVirtualPage { get; init; } = -1;
        public bool LastDevelopmentPinState { get; init; }
        public int AllocationToScheduleP50Frames { get; init; }
        public int AllocationToScheduleP95Frames { get; init; }
        public int AllocationToScheduleMaximumFrames { get; init; }
        public int AllocationToPublicationP50Frames { get; init; }
        public int AllocationToPublicationP95Frames { get; init; }
        public int AllocationToPublicationMaximumFrames { get; init; }
        public int OrdinaryAllocationToPublicationP50Frames { get; init; }
        public int OrdinaryAllocationToPublicationP95Frames { get; init; }
        public int OrdinaryAllocationToPublicationMaximumFrames { get; init; }
        public int CutAllocationToPublicationP50Frames { get; init; }
        public int CutAllocationToPublicationP95Frames { get; init; }
        public int CutAllocationToPublicationMaximumFrames { get; init; }
        public uint EventSourceGeneration { get; init; }
        public uint EventCohortGeneration { get; init; }
        public int AdmissionProbeCount { get; init; }
        public int EvictionProbeCount { get; init; }
        public int OtherGenerationEvictionProbeCount { get; init; }
        public ulong PhysicalPayloadBytes { get; init; }
        public ulong PageArenaBytes { get; init; }
        public ulong FeedbackReadbackBytes { get; init; }
        public ulong RetiredBytes { get; init; }
        public ulong DenseEquivalentBytes { get; init; }
        public ulong AllocatedCapacityBytes { get; init; }
        public ulong AvoidedBytes { get; init; }
        public ulong PayloadBytesAvoidedThisFrame { get; init; }
        public ulong PrimaryRaysAvoidedThisFrame { get; init; }
        public IReadOnlyList<SimpleDdgiResidencyRingTelemetry> Rings { get; init; } =
            Array.Empty<SimpleDdgiResidencyRingTelemetry>();
        public int ConfiguredPhysicalPageBudget { get; init; }
        public int ConfiguredMinimumPhysicalPageBudget { get; init; }
        public int RetentionFrames { get; init; }
        public int MaximumAdmissionsPerFrame { get; init; }
        public int MaximumReceiverFeedbackRequests { get; init; }
        public int InactiveRetryFrames { get; init; }

        public static SimpleDdgiProbeResidencyTelemetry Unavailable(
            string reason) => new(
                false,
                SimpleDdgiProbeResidencyMode.Dense,
                false,
                reason);
    }

    public static class SimpleDdgiProbeResidencyTelemetryFactory
    {
        public static SimpleDdgiProbeResidencyTelemetry Create(
            SimpleDdgiVolumeManager? manager)
        {
            if (manager?.LastLayoutReport is not { } report)
            {
                return SimpleDdgiProbeResidencyTelemetry.Unavailable(
                    "Simple DDGI did not produce a resolved residency layout.");
            }

            GPUSimpleDdgiResidencyFeedback feedback =
                manager.LastProbeResidencyFeedback;
            SimpleDdgiProbeResidencyMode mode =
                manager.ProbeResidencyMode;
            bool valid = IsFeedbackValidForMode(
                mode,
                manager.ProbeResidencyFeedbackValid);
            SimpleDdgiMemoryPlan plan = report.AcceptedMemoryPlan;
            ulong sampledBytesPerProbe =
                plan.SampledAtlasPhysicalProbeCapacity > 0
                    ? plan.SampledAtlasImageBytes /
                        (ulong)plan.SampledAtlasPhysicalProbeCapacity
                    : 0UL;
            ulong payloadBytesPerProbe = checked(
                SimpleDdgiMemoryPlan.IrradianceBytesPerProbe +
                SimpleDdgiMemoryPlan.VisibilityBytesPerProbe +
                SimpleDdgiMemoryPlan.IrradianceBytesPerProbe +
                (ulong)Math.Max(0, plan.RayCapacity) *
                    SimpleDdgiMemoryPlan.TransportRayCacheBytes +
                sampledBytesPerProbe);
            ulong nonResident = valid
                ? feedback.NonResidentVirtualProbeCount
                : (ulong)Math.Max(0, plan.NonResidentVirtualProbeCapacity);
            var rings = new[]
            {
                Ring(0, feedback.NearVirtualProbeCount,
                    feedback.NearResidentProbeCount,
                    feedback.NearActiveResidentProbeCount,
                    feedback.NearInactiveResidentProbeCount,
                    feedback.NearDemandedPageCount,
                    feedback.NearConvergedResidentProbeCount),
                Ring(1, feedback.MidVirtualProbeCount,
                    feedback.MidResidentProbeCount,
                    feedback.MidActiveResidentProbeCount,
                    feedback.MidInactiveResidentProbeCount,
                    feedback.MidDemandedPageCount,
                    feedback.MidConvergedResidentProbeCount),
                Ring(2, feedback.FarVirtualProbeCount,
                    feedback.FarResidentProbeCount,
                    feedback.FarActiveResidentProbeCount,
                    feedback.FarInactiveResidentProbeCount,
                    feedback.FarDemandedPageCount,
                    feedback.FarConvergedResidentProbeCount)
            };
            bool mutationFrozen = manager.ProbeResidencyMutationFrozen;
            bool residencyStateValid = manager.ProbeResidencyStateValid;
            string fallbackReason = !string.IsNullOrWhiteSpace(
                    report.ResidencyFallbackReason)
                ? report.ResidencyFallbackReason
                : manager.ProbeResidencyFailureReason;
            if (mutationFrozen && string.IsNullOrWhiteSpace(fallbackReason))
                fallbackReason = "Sparse residency mutation is frozen.";
            bool sparseAuthoritative =
                mode.UsesSparsePayloads() &&
                (!mutationFrozen || residencyStateValid);
            bool predictorComparisonValid = valid &&
                mode ==
                    SimpleDdgiProbeResidencyMode.Shadow;
            uint predictorActualPages = predictorComparisonValid
                ? feedback.OpaqueGatherDemandPageCount
                : 0u;
            uint predictorPages = predictorComparisonValid
                ? feedback.VisibleDemandPageCount
                : 0u;

            return new SimpleDdgiProbeResidencyTelemetry(
                true,
                mode,
                sparseAuthoritative,
                fallbackReason)
            {
                CurrentResourceGeneration = manager.ProbeResidencyResourceGeneration,
                MutationFrozen = mutationFrozen,
                DevelopmentMutationFrozen =
                    manager.ProbeResidencyDevelopmentMutationFrozen,
                ResidencyStateValid = residencyStateValid,
                FeedbackValid = valid,
                FeedbackFrameSerial = manager.ProbeResidencyFeedbackFrameSerial,
                FeedbackResourceGeneration = valid
                    ? feedback.ResidencyResourceGeneration
                    : 0u,
                MappingGeneration = valid ? feedback.MappingGenerationCounter : 0u,
                DemandEpoch = valid ? feedback.DemandEpoch : 0u,
                VirtualProbeCount = valid
                    ? ToInt(feedback.VirtualProbeCount)
                    : plan.VirtualProbeCount,
                VirtualPageCount = valid
                    ? ToInt(feedback.VirtualPageCount)
                    : plan.SparseVirtualPageCount,
                DensePhysicalProbeCount = valid
                    ? ToInt(feedback.DensePhysicalProbeCount)
                    : plan.DensePayloadProbeCount,
                SparsePhysicalPageCapacity = valid
                    ? ToInt(feedback.SparsePhysicalPageCapacity)
                    : plan.SparsePhysicalPageCapacity,
                PhysicalProbeCapacity = valid
                    ? ToInt(feedback.PhysicalProbeCapacity)
                    : plan.PhysicalProbeCapacity,
                ResidentPageCount = valid ? ToInt(feedback.ResidentPageCount) : 0,
                FreePageCount = valid
                    ? ToInt(feedback.FreePageCount)
                    : plan.SparsePhysicalPageCapacity,
                InitializingPageCount = valid ? ToInt(feedback.InitializingPageCount) : 0,
                PublishedPageCount = valid ? ToInt(feedback.PublishedPageCount) : 0,
                SuppressedPageCount = valid ? ToInt(feedback.SuppressedPageCount) : 0,
                ResidentProbeCount = valid ? ToInt(feedback.ResidentProbeCount) : 0,
                NonResidentVirtualProbeCount = ToInt(nonResident),
                ActiveResidentProbeCount = valid ? ToInt(feedback.ActiveResidentProbeCount) : 0,
                InactiveResidentProbeCount = valid ? ToInt(feedback.InactiveResidentProbeCount) : 0,
                ConvergedResidentProbeCount = valid ? ToInt(feedback.ConvergedResidentProbeCount) : 0,
                VisibleDemandPageCount = valid ? ToInt(feedback.VisibleDemandPageCount) : 0,
                ReceiverDemandPageCount = valid ? ToInt(feedback.ReceiverDemandPageCount) : 0,
                ReceiverRequestCount = valid ? ToInt(feedback.ReceiverRequestCount) : 0,
                ReceiverRequestOverflowCount = valid ? ToInt(feedback.ReceiverRequestOverflowCount) : 0,
                PredictorComparisonValid = predictorComparisonValid,
                PredictorActualPageCount = predictorComparisonValid
                    ? ToInt(feedback.OpaqueGatherDemandPageCount)
                    : 0,
                PredictorTruePositivePageCount = predictorComparisonValid
                    ? ToInt(feedback.PredictorTruePositivePageCount)
                    : 0,
                PredictorFalseNegativePageCount = predictorComparisonValid
                    ? ToInt(feedback.PredictorFalseNegativePageCount)
                    : 0,
                PredictorFalsePositivePageCount = predictorComparisonValid
                    ? ToInt(feedback.PredictorFalsePositivePageCount)
                    : 0,
                PredictorFalseNegativeRate = predictorComparisonValid
                    ? (double)feedback.PredictorFalseNegativePageCount /
                        Math.Max(1u, predictorActualPages)
                    : 0.0,
                PredictorInflationRatio = predictorComparisonValid
                    ? (double)predictorPages / Math.Max(1u, predictorActualPages)
                    : 0.0,
                RetainedPageCount = valid ? ToInt(feedback.RetainedPageCount) : 0,
                RetainedAge0To15PageCount = valid ? ToInt(feedback.RetainedAge0To15PageCount) : 0,
                RetainedAge16To63PageCount = valid ? ToInt(feedback.RetainedAge16To63PageCount) : 0,
                RetainedAge64To255PageCount = valid ? ToInt(feedback.RetainedAge64To255PageCount) : 0,
                RetainedAge256PlusPageCount = valid ? ToInt(feedback.RetainedAge256PlusPageCount) : 0,
                VisibleDemandResidentHitPageCount = valid ? ToInt(feedback.VisibleDemandResidentHitPageCount) : 0,
                VisibleDemandMissingPageCount = valid ? ToInt(feedback.VisibleDemandMissingPageCount) : 0,
                AdmissionCount = valid ? ToInt(feedback.AdmissionCount) : 0,
                EvictionCount = valid ? ToInt(feedback.EvictionCount) : 0,
                FailedAdmissionCount = valid ? ToInt(feedback.FailedAdmissionCount) : 0,
                PoolPressureFrameCount = valid ? ToInt(feedback.PoolPressureFrameCount) : 0,
                ConsecutivePressureFrames = valid ? ToInt(feedback.ConsecutivePressureFrames) : 0,
                MaximumConsecutivePressureFrames = valid ? ToInt(feedback.MaximumConsecutivePressureFrames) : 0,
                PageTableReverseDisagreementCount = valid ? ToInt(feedback.PageTableReverseDisagreementCount) : 0,
                DuplicateVirtualOwnerCount = valid ? ToInt(feedback.DuplicateVirtualOwnerCount) : 0,
                DuplicatePhysicalOwnerCount = valid ? ToInt(feedback.DuplicatePhysicalOwnerCount) : 0,
                StaleVirtualRequestCount = valid ? ToInt(feedback.StaleVirtualRequestCount) : 0,
                StaleMappingRequestCount = valid ? ToInt(feedback.StaleMappingRequestCount) : 0,
                StaleResourceRequestCount = valid ? ToInt(feedback.StaleResourceRequestCount) : 0,
                OutOfRangeRequestCount = valid ? ToInt(feedback.OutOfRangeRequestCount) : 0,
                NonResidentGatherRejectionCount = valid ? ToInt(feedback.NonResidentGatherRejectionCount) : 0,
                CoarserFallbackCount = valid ? ToInt(feedback.CoarserFallbackCount) : 0,
                SuppressionCount = valid ? ToInt(feedback.SuppressionCount) : 0,
                RetryCount = valid ? ToInt(feedback.RetryCount) : 0,
                DevelopmentPinnedPageCount = valid
                    ? ToInt(feedback.DevelopmentPinnedPageCount)
                    : 0,
                DevelopmentControlCommandCount =
                    manager.ProbeResidencyDevelopmentControlCommandCount,
                LastDevelopmentControlledVirtualPage =
                    manager.ProbeResidencyLastDevelopmentControlledVirtualPage,
                LastDevelopmentPinState =
                    manager.ProbeResidencyLastDevelopmentPinState,
                AllocationToScheduleP50Frames = valid ? ToInt(feedback.AllocationToScheduleP50) : 0,
                AllocationToScheduleP95Frames = valid ? ToInt(feedback.AllocationToScheduleP95) : 0,
                AllocationToScheduleMaximumFrames = valid ? ToInt(feedback.AllocationToScheduleMax) : 0,
                AllocationToPublicationP50Frames = valid ? ToInt(feedback.AllocationToPublicationP50) : 0,
                AllocationToPublicationP95Frames = valid ? ToInt(feedback.AllocationToPublicationP95) : 0,
                AllocationToPublicationMaximumFrames = valid ? ToInt(feedback.AllocationToPublicationMax) : 0,
                OrdinaryAllocationToPublicationP50Frames = valid
                    ? ToInt(feedback.OrdinaryAllocationToPublicationP50)
                    : 0,
                OrdinaryAllocationToPublicationP95Frames = valid
                    ? ToInt(feedback.OrdinaryAllocationToPublicationP95)
                    : 0,
                OrdinaryAllocationToPublicationMaximumFrames = valid
                    ? ToInt(feedback.OrdinaryAllocationToPublicationMax)
                    : 0,
                CutAllocationToPublicationP50Frames = valid
                    ? ToInt(feedback.CutAllocationToPublicationP50)
                    : 0,
                CutAllocationToPublicationP95Frames = valid
                    ? ToInt(feedback.CutAllocationToPublicationP95)
                    : 0,
                CutAllocationToPublicationMaximumFrames = valid
                    ? ToInt(feedback.CutAllocationToPublicationMax)
                    : 0,
                EventSourceGeneration = valid ? feedback.EventSourceGeneration : 0u,
                EventCohortGeneration = valid ? feedback.EventCohortGeneration : 0u,
                AdmissionProbeCount = valid ? ToInt(feedback.AdmissionProbeCount) : 0,
                EvictionProbeCount = valid ? ToInt(feedback.EvictionProbeCount) : 0,
                OtherGenerationEvictionProbeCount = valid
                    ? ToInt(feedback.OtherGenerationEvictionProbeCount)
                    : 0,
                PhysicalPayloadBytes = plan.PhysicalPayloadBytes,
                PageArenaBytes = manager.ProbeResidencyArenaBytes,
                FeedbackReadbackBytes = manager.ProbeResidencyFeedbackReadbackBytes,
                RetiredBytes = manager.ProbeResidencyRetiredBytes,
                DenseEquivalentBytes = report.DenseEquivalentBytes,
                AllocatedCapacityBytes = report.AllocatedSparseBytes,
                AvoidedBytes = report.AvoidedBytes,
                PayloadBytesAvoidedThisFrame = SaturatingMultiply(
                    nonResident,
                    payloadBytesPerProbe),
                PrimaryRaysAvoidedThisFrame = SaturatingMultiply(
                    nonResident,
                    (ulong)Math.Max(0, plan.RayCapacity)),
                Rings = rings,
                ConfiguredPhysicalPageBudget =
                    manager.ProbeResidencyConfiguredPhysicalPageBudget,
                ConfiguredMinimumPhysicalPageBudget =
                    manager.ProbeResidencyConfiguredMinimumPhysicalPageBudget,
                RetentionFrames = manager.ProbeResidencyRetentionFrames,
                MaximumAdmissionsPerFrame =
                    manager.ProbeResidencyMaximumAdmissionsPerFrame,
                MaximumReceiverFeedbackRequests =
                    manager.ProbeResidencyMaximumReceiverFeedbackRequests,
                InactiveRetryFrames = manager.ProbeResidencyInactiveRetryFrames
            };
        }

        internal static bool IsFeedbackValidForMode(
            SimpleDdgiProbeResidencyMode mode,
            bool feedbackValid) =>
            mode.CollectsDemand() && feedbackValid;

        private static SimpleDdgiResidencyRingTelemetry Ring(
            int index,
            uint virtualCount,
            uint residentCount,
            uint activeCount,
            uint inactiveCount,
            uint demandedCount,
            uint convergedCount) => new(
                index,
                ToInt(virtualCount),
                ToInt(residentCount),
                ToInt(activeCount),
                ToInt(inactiveCount),
                ToInt(demandedCount),
                ToInt(convergedCount));

        private static int ToInt(uint value) =>
            value > int.MaxValue ? int.MaxValue : (int)value;

        private static int ToInt(ulong value) =>
            value > int.MaxValue ? int.MaxValue : (int)value;

        private static ulong SaturatingMultiply(ulong left, ulong right) =>
            left == 0UL || right == 0UL
                ? 0UL
                : left > ulong.MaxValue / right
                    ? ulong.MaxValue
                    : left * right;
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
        int ScheduledVisibleZeroSupport,
        int ScheduledFreshExposedVisible,
        int ScheduledVisibleDirty,
        int ScheduledVisibleRetry,
        int ScheduledNearMaintenance,
        int ScheduledMidMaintenance,
        int ScheduledFarMaintenance,
        int ReservedVisibleZeroSupport,
        int ReservedFreshExposedVisible,
        int ReservedVisibleDirty,
        int ReservedVisibleRetry,
        int ReservedNearMaintenance,
        int ReservedMidMaintenance,
        int ReservedFarMaintenance,
        int PendingVisibleZeroSupport,
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
                telemetry.ScheduledVisibleZeroSupport,
                telemetry.ScheduledFreshExposedVisible,
                telemetry.ScheduledVisibleDirty,
                telemetry.ScheduledVisibleRetry,
                telemetry.ScheduledNearMaintenance,
                telemetry.ScheduledMidMaintenance,
                telemetry.ScheduledFarMaintenance,
                telemetry.ReservedVisibleZeroSupport,
                telemetry.ReservedFreshExposedVisible,
                telemetry.ReservedVisibleDirty,
                telemetry.ReservedVisibleRetry,
                telemetry.ReservedNearMaintenance,
                telemetry.ReservedMidMaintenance,
                telemetry.ReservedFarMaintenance,
                telemetry.PendingVisibleZeroSupport,
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

        public string ExecutableHash { get; init; } = "unknown-executable";
        public string DirtyWorktreeState { get; init; } = "unknown-dirty-state";
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

        public uint DdgiCacheGeneration { get; init; }
        public uint SimpleDdgiTransportGeneration { get; init; }
        public bool TransportConvergencePending { get; init; }
        public int TransportConvergedProbeCount { get; init; }
        public int TransportPendingProbeCount { get; init; }
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

        public int DiagnosticSampleStrideX { get; init; }
        public int DiagnosticSampleStrideY { get; init; }
        public int DiagnosticSampleWeight { get; init; }
        public PerformanceMetricSemantic SkyVisibilityCountSemantic { get; init; } =
            PerformanceMetricSemantic.Unavailable;
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

            var states = new List<GiFeatureState>(12)
            {
                CreateGlobalIlluminationState(diagnostics),
                CreateEmergencyGiFallbackState(diagnostics),
                CreateDdgiState(diagnostics),
                CreateSimpleDdgiState(diagnostics),
                CreateSimpleDdgiProbeResidencyState(diagnostics),
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
                if (diagnostics.SimpleDdgiTransportSourceCohortTransitionActive != 0)
                    progress += $" A stepped-sun source cohort is draining ({diagnostics.SimpleDdgiTransportSourceStepStaleProbeCount} stale probes, P95 age {diagnostics.SimpleDdgiTransportSourceStepAgeP95Seconds:F2}s).";
                if (diagnostics.SimpleDdgiTransportSourceRefreshCapacityShortfall > 0)
                    progress += $" The current update cap is {diagnostics.SimpleDdgiTransportSourceRefreshCapacityShortfall} source probes/frame below the declared sweep target.";
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

        private static GiFeatureState CreateSimpleDdgiProbeResidencyState(
            RendererDiagnostics diagnostics)
        {
            SimpleDdgiProbeResidencyTelemetry residency =
                diagnostics.SimpleDdgiProbeResidency;
            bool requested = residency.Mode != SimpleDdgiProbeResidencyMode.Dense ||
                !string.IsNullOrWhiteSpace(residency.FallbackReason);
            if (!requested)
            {
                return new GiFeatureState(
                    "simple-ddgi-probe-residency",
                    true,
                    true,
                    false,
                    false,
                    GiFeatureStateStatus.Disabled,
                    "Dense Simple-DDGI payload addressing is selected.");
            }
            if (!residency.IsAvailable ||
                (residency.Mode == SimpleDdgiProbeResidencyMode.Dense &&
                 !string.IsNullOrWhiteSpace(residency.FallbackReason)))
            {
                return new GiFeatureState(
                    "simple-ddgi-probe-residency",
                    true,
                    true,
                    true,
                    false,
                    GiFeatureStateStatus.Fallback,
                    NonEmptyOr(
                        residency.FallbackReason,
                        "Sparse probe residency was requested but its layout is unavailable."));
            }
            if (residency.MutationFrozen)
            {
                string defaultReason = residency.ResidencyStateValid
                    ? "Sparse residency mutation is frozen; the last validated mapping remains readable until a fresh transaction is bootstrapped."
                    : "Sparse residency mutation is frozen and the mapping is invalid; gathers fail closed to the dense coarser ring.";
                return new GiFeatureState(
                    "simple-ddgi-probe-residency",
                    true,
                    true,
                    true,
                    false,
                    GiFeatureStateStatus.Fallback,
                    NonEmptyOr(residency.FallbackReason, defaultReason));
            }

            string reason = residency.Mode == SimpleDdgiProbeResidencyMode.Shadow
                ? "Shadow demand collection is active; dense payload addressing remains authoritative."
                : residency.FeedbackValid
                    ? $"Sparse near-ring paging is authoritative with {residency.ResidentPageCount}/{residency.SparsePhysicalPageCapacity} physical pages resident."
                    : "Sparse near-ring paging is authoritative; the first fence-complete residency summary is pending.";
            return new GiFeatureState(
                "simple-ddgi-probe-residency",
                true,
                true,
                true,
                true,
                GiFeatureStateStatus.Active,
                reason);
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
            reason = string.Empty;
            return false;
        }

        private static uint SaturatingAdd(uint left, uint right) => uint.MaxValue - left < right ? uint.MaxValue : left + right;
        private static double ClampFraction(double value) => Math.Clamp(value, 0.0, 1.0);
        private static string BuildCameraState(RendererDiagnostics diagnostics) =>
            diagnostics.CaptureCamera.CameraCutSerial == 0
                ? "stable"
                : "cut=" + diagnostics.CaptureCamera.CameraCutSerial.ToString(CultureInfo.InvariantCulture);
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

            if (layout.HasRequiredDegradation)
            {
                GiDiagnosticSeverity severity = layout.AdmissionMode == SimpleDdgiLayoutAdmissionMode.Reject
                    ? GiDiagnosticSeverity.Error
                    : GiDiagnosticSeverity.Warning;
                warnings.Add(new GiDiagnosticWarning(
                    GiDiagnosticWarningCode.SimpleDdgiLayoutDegraded,
                    severity,
                    $"Simple DDGI required layout was degraded before allocation: " +
                    $"{layout.RequiredRejectedVolumeCount + layout.UnclassifiedRejectedVolumeCount} required volume(s) were rejected " +
                    $"and physical-page-budget-reduced={layout.PhysicalPageBudgetWasReduced} ({layout.Summary}).",
                    "simple-ddgi-layout",
                    layout.RequiredRejectedVolumeCount +
                        layout.UnclassifiedRejectedVolumeCount,
                    0,
                    "volumes",
                    GiMetricFreshness.CurrentFrame,
                    diagnostics.ActiveBudgetProfileName,
                    diagnostics.CaptureRun.Scenario,
                    BuildCameraState(diagnostics),
                    diagnostics.CaptureFrame.FrameSerial,
                    diagnostics.CaptureCamera.CameraCutSerial,
                    "Inspect the per-volume layout decisions, preserve receiver-hero priority, and either select a supported tier or explicitly approve the documented degraded layout."));
            }

            if (layout.HasRequiredDegradation &&
                layout.RequestedProbeCount > layout.ProbeBudget)
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
            if (layout.HasRequiredDegradation &&
                layout.RequestedPersistentBytes > layout.PersistentMemoryBudgetBytes)
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
            if (layout.HasRequiredDegradation &&
                layout.RequestedVolumeCount > layout.VolumeBudget)
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
                    BuildCameraState(diagnostics),
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

            if (diagnostics.SimpleDdgiTransportSourceCohortTransitionActive != 0)
            {
                int targetPerFrame = Math.Max(
                    diagnostics.SimpleDdgiTransportSourceRefreshTargetProbeCount,
                    0);
                int capacityShortfall = Math.Max(
                    diagnostics.SimpleDdgiTransportSourceRefreshCapacityShortfall,
                    0);
                if (capacityShortfall > 0)
                {
                    warnings.Add(new GiDiagnosticWarning(
                        GiDiagnosticWarningCode.SourceSweepBudgetExceeded,
                        GiDiagnosticSeverity.Warning,
                        "The configured Simple DDGI update cap cannot deliver the declared stepped-sun source sweep.",
                        "simple-ddgi-source-sweep-capacity",
                        targetPerFrame,
                        Math.Max(targetPerFrame - capacityShortfall, 0),
                        "source probes/frame",
                        GiMetricFreshness.CurrentFrame,
                        diagnostics.ActiveBudgetProfileName,
                        diagnostics.CaptureRun.Scenario,
                        BuildCameraState(diagnostics),
                        diagnostics.CaptureFrame.FrameSerial,
                        diagnostics.CaptureCamera.CameraCutSerial,
                        "Increase the Simple DDGI update/ray budget, reclaim inactive probes, or explicitly lengthen the authored GI source-sweep target."));
                }

                int targetFrames = Math.Max(
                    diagnostics.SimpleDdgiTransportSourceRefreshFrames,
                    0);
                int p95AgeFrames = Math.Max(
                    diagnostics.SimpleDdgiTransportSourceStepAgeP95Frames,
                    0);
                if (targetFrames > 0 && p95AgeFrames > targetFrames)
                {
                    warnings.Add(new GiDiagnosticWarning(
                        GiDiagnosticWarningCode.SourceSweepBudgetExceeded,
                        GiDiagnosticSeverity.Warning,
                        "The P95 stepped-sun source age exceeds the declared Simple DDGI sweep budget.",
                        "simple-ddgi-source-sweep-lag",
                        p95AgeFrames,
                        targetFrames,
                        "frames",
                        GiMetricFreshness.DelayedReadback,
                        diagnostics.ActiveBudgetProfileName,
                        diagnostics.CaptureRun.Scenario,
                        BuildCameraState(diagnostics),
                        diagnostics.CaptureFrame.FrameSerial,
                        diagnostics.CaptureCamera.CameraCutSerial,
                        "Capture the source-cohort telemetry, then increase refresh throughput or reduce the sun-step rate before qualifying dynamic time of day."));
                }
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
                BuildCameraState(diagnostics),
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
                BuildCameraState(diagnostics),
                diagnostics.CaptureFrame.FrameSerial,
                diagnostics.CaptureCamera.CameraCutSerial,
                action);
        }

        private static string BuildCameraState(RendererDiagnostics diagnostics) =>
            diagnostics.CaptureCamera.CameraCutSerial == 0
                ? "stable"
                : "cut=" + diagnostics.CaptureCamera.CameraCutSerial.ToString(CultureInfo.InvariantCulture);
    }

    public static class GiResidencyReporter
    {
        public static GiResidencySnapshot Create(
            RendererDiagnostics diagnostics,
            MemoryBudgetSnapshot memory,
            RenderBudgetProfile profile)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (memory == null)
                throw new ArgumentNullException(nameof(memory));
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            ulong trackedGiBytes = GetCategoryBytes(memory, MemoryBudgetCategory.GlobalIllumination);
            bool hasTrackedGiEntry = HasCategory(memory, MemoryBudgetCategory.GlobalIllumination);
            ulong uniqueResidentBytes = trackedGiBytes;
            ulong accelerationStructureTransientBytes = ResolveAccelerationStructureTransientBytes(diagnostics);
            ulong accelerationStructureTransientBudgetBytes = diagnostics.AccelerationStructureMemoryBudgetBytes == 0
                ? 0
                : AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(
                    diagnostics.AccelerationStructureMemoryBudgetBytes);
            bool farFieldConfigured = diagnostics.FarFieldPagedFeatureEnabled != 0 ||
                diagnostics.FarFieldCacheBytes != 0 ||
                diagnostics.FarFieldInstanceBufferBytes != 0;
            var components = new List<GiResidencyComponent>(6)
            {
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
                hasTrackedGiEntry,
                true,
                "Allocation-tracker GlobalIllumination category; manager-facing component values are not summed.",
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
            AddSetting(settings, "gi.requestedDebugViewAvailable", diagnostics.GlobalIlluminationRequestedDebugViewAvailable);
            AddSetting(settings, "gi.debugViewAvailabilityReason", diagnostics.GlobalIlluminationDebugViewAvailabilityReason);
            AddSetting(settings, "gi.active", diagnostics.GlobalIlluminationEnabled);
            AddSetting(settings, "gi.emergencyFallback", diagnostics.GlobalIlluminationEmergencyFallbackEnabled);
            AddSetting(settings, "gi.fallbackReason", diagnostics.GlobalIlluminationFallbackReason);
            AddSetting(settings, "gi.indirectIntensity", diagnostics.GlobalIlluminationIndirectIntensity);
            AddSetting(settings, "gi.environmentFallbackIntensity", diagnostics.GlobalIlluminationEnvironmentFallbackIntensity);
            AddSetting(settings, "gi.ddgi.requested", diagnostics.GlobalIlluminationDdgiRequested);
            AddSetting(settings, "gi.ddgi.active", diagnostics.GlobalIlluminationDdgiActive);
            AddSetting(settings, "gi.simpleDdgi.requested", diagnostics.SimpleDdgiRequested);
            AddSetting(settings, "gi.simpleDdgi.active", diagnostics.SimpleDdgiActive);
            AddSetting(settings, "gi.simpleDdgi.transportV2.active", diagnostics.SimpleDdgiTransportV2Active);
            AddSetting(settings, "gi.simpleDdgi.automaticProbeDensity.active", diagnostics.SimpleDdgiAutomaticProbeDensityActive);
            AddSetting(settings, "gi.simpleDdgi.transport.relaxation", diagnostics.SimpleDdgiTransportSolverRelaxation);
            AddSetting(settings, "gi.simpleDdgi.transport.albedoClamp", diagnostics.SimpleDdgiTransportAlbedoClamp);
            AddSetting(settings, "gi.simpleDdgi.transport.tailRelativeTolerance", diagnostics.SimpleDdgiTransportTailRelativeTolerance);
            AddSetting(settings, "gi.simpleDdgi.transport.acceleratedSweepCount", diagnostics.SimpleDdgiTransportAcceleratedSweepCount);
            AddSetting(settings, "gi.simpleDdgi.transport.accelerationEnabled", diagnostics.SimpleDdgiTransportAccelerationEnabled ? 1 : 0);
            AddSetting(settings, "gi.simpleDdgi.transport.tailCertificationEnabled", diagnostics.SimpleDdgiTransportTailCertificationEnabled ? 1 : 0);
            AddSetting(settings, "gi.simpleDdgi.transport.legacy.residualThreshold", diagnostics.SimpleDdgiTransportResidualThreshold);
            AddSetting(settings, "gi.simpleDdgi.transport.legacy.maximumSolverGenerations", diagnostics.SimpleDdgiTransportMaximumSolverGenerations);
            AddSetting(settings, "gi.simpleDdgi.transport.sourceRefreshFrames.configured", diagnostics.SimpleDdgiTransportConfiguredSourceRefreshFrames);
            AddSetting(settings, "gi.rayQuery.requested", diagnostics.GlobalIlluminationRayQueryRequested);
            AddSetting(settings, "gi.rayQuery.supported", diagnostics.GlobalIlluminationRayQuerySupported);
            AddSetting(settings, "gi.rayQuery.active", diagnostics.GlobalIlluminationRayQueryActive);

            AddSetting(settings, "ddgi.quality", diagnostics.DdgiQualityTier.ToString());
            AddSetting(settings, "ddgi.maxActiveProbes", diagnostics.DdgiMaxActiveProbeBudget);
            AddSetting(settings, "ddgi.maxUpdatesPerFrame", diagnostics.DdgiMaxProbeUpdatesPerFrame);
            AddSetting(settings, "ddgi.requestBudget", diagnostics.DdgiProbeUpdateRequestBudget);
            AddSetting(settings, "ddgi.primaryRayBudget", diagnostics.DdgiProbeUpdatePrimaryRayBudget);
            AddSetting(settings, "ddgi.raysPerProbe", diagnostics.DdgiRaysPerProbe);
            AddSetting(settings, "ddgi.atlasMemoryBudgetBytes", diagnostics.DdgiAtlasMemoryBudgetBytes);

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
            AddProbeResidencySettings(
                settings,
                diagnostics.SimpleDdgiProbeResidency);
            AddSchedulerSettings(settings, diagnostics.SimpleDdgiScheduling, diagnostics.SimpleDdgiSchedulerPolicy);
            AddGpuSchedulerSettings(settings, diagnostics);
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
            // Exposure is runtime state while auto exposure is active. Recording
            // that adapting value as configuration makes an otherwise locked
            // benchmark appear to change its render settings every frame.
            if (diagnostics.AutoExposureEnabled != 0)
                AddSetting(settings, "lighting.exposure", "automatic");
            else
                AddSetting(settings, "lighting.exposure", diagnostics.Exposure);
            AddSetting(settings, "lighting.autoExposure", diagnostics.AutoExposureEnabled);
            AddSetting(settings, "lighting.toneMapper", diagnostics.ToneMapper.ToString());
            AddSetting(settings, "lighting.directionalShadows", diagnostics.DirectionalShadowsEnabled);
            AddSetting(settings, "lighting.directionalShadowMapSize", diagnostics.DirectionalShadowMapSize);
            AddSetting(settings, "lighting.directionalShadowCascades", diagnostics.DirectionalShadowCascadeCount);
            AddSetting(settings, "lighting.directionalShadowMaxDistance", diagnostics.DirectionalShadowRuntime.ConfiguredMaxDistance);
            AddSetting(settings, "lighting.directionalShadowCascadeBlendFraction", diagnostics.DirectionalShadowRuntime.CascadeBlendFraction);
            AddSetting(settings, "lighting.directionalShadow.requestedMode", diagnostics.DirectionalShadowRuntime.RequestedMode.ToString());
            AddSetting(settings, "lighting.directionalShadow.effectiveMode", diagnostics.DirectionalShadowRuntime.EffectiveMode.ToString());
            AddSetting(settings, "lighting.directionalShadow.fallbackReason", diagnostics.DirectionalShadowRuntime.FallbackReason.ToString());
            AddSetting(settings, "lighting.directionalShadow.fallbackDetail", diagnostics.DirectionalShadowRuntime.FallbackDetail);
            AddSetting(settings, "lighting.directionalShadow.qualification", diagnostics.DirectionalShadowRuntime.QualificationLevel.ToString());
            AddSetting(settings, "lighting.directionalShadow.qualificationId", diagnostics.DirectionalShadowRuntime.QualificationId);
            AddSetting(settings, "lighting.directionalShadow.qualificationDetail", diagnostics.DirectionalShadowRuntime.QualificationDetail);
            AddSetting(settings, "lighting.directionalShadow.qualificationDeviceRule", diagnostics.DirectionalShadowRuntime.QualificationDeviceRuleId);
            AddSetting(settings, "lighting.directionalShadow.qualificationTrack", diagnostics.DirectionalShadowRuntime.QualificationTrackId);
            AddSetting(settings, "lighting.directionalShadow.qualifiedGpuBudgetUs", diagnostics.DirectionalShadowRuntime.QualifiedGpuBudgetMicroseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AddSetting(settings, "lighting.directionalShadow.qualifiedMemoryBudgetBytes", diagnostics.DirectionalShadowRuntime.QualifiedMemoryBudgetBytes);
            AddSetting(settings, "lighting.directionalShadow.rayMaskBytes", diagnostics.DirectionalShadowRuntime.RayMaskBytes);
            AddSetting(settings, "lighting.directionalShadow.historyBytes", diagnostics.DirectionalShadowRuntime.HistoryBytes);
            AddSetting(settings, "lighting.directionalShadow.raySceneGeneration", diagnostics.DirectionalShadowRuntime.RaySceneResourceGeneration);
            AddSetting(settings, "lighting.directionalShadow.raySceneEpoch", diagnostics.DirectionalShadowRuntime.RaySceneContentEpoch);
            AddSetting(settings, "lighting.directionalShadow.raysIssued", diagnostics.DirectionalShadowRuntime.RayCounters.OpaqueRaysIssued);
            AddSetting(settings, "lighting.directionalShadow.transparentRaysIssued", diagnostics.DirectionalShadowRuntime.RayCounters.TransparentRaysIssued);
            AddSetting(settings, "lighting.directionalShadow.candidateCapHits", diagnostics.DirectionalShadowRuntime.RayCounters.OpaqueCandidateCapHits + diagnostics.DirectionalShadowRuntime.RayCounters.TransparentCandidateCapHits);
            AddSetting(settings, "lighting.shadowNormalBias", diagnostics.ShadowNormalBias);
            AddSetting(settings, "lighting.shadowSlopeBias", diagnostics.ShadowSlopeScaledDepthBias);
            AddSetting(settings, "lighting.directionalShadowPcfRadius", diagnostics.DirectionalShadowPcfRadius);
            AddSetting(settings, "lighting.ambientOcclusion", diagnostics.AmbientOcclusionEnabled);
            AddSetting(settings, "lighting.ambientOcclusionIntensity", diagnostics.AmbientOcclusionIntensity);
            AddSetting(settings, "lighting.reflections", diagnostics.ReflectionsEnabled);
            AddSetting(settings, "lighting.reflectionMode", diagnostics.ReflectionMode.ToString());
            AddSetting(settings, "lighting.reflection.requestedMode",
                diagnostics.RequestedReflectionMode.ToString());
            AddSetting(settings, "lighting.reflection.effectiveMode",
                diagnostics.EffectiveReflectionMode.ToString());
            AddSetting(settings, "lighting.reflection.fallbackReason",
                diagnostics.ReflectionFallbackReason.ToString());
            AddSetting(settings, "lighting.reflection.fallbackDetail",
                diagnostics.ReflectionFallbackDetail);
            AddSetting(settings, "lighting.reflection.rayCapacity",
                diagnostics.HybridReflectionRayQueryCapacity);
            AddSetting(settings, "lighting.reflection.ssrHits",
                diagnostics.HybridReflectionSsrHitCount);
            AddSetting(settings, "lighting.reflection.rayQueries",
                diagnostics.HybridReflectionRayQueryCount);
            AddSetting(settings, "lighting.reflection.rayOverflows",
                diagnostics.HybridReflectionRayQueryOverflowCount);
            AddSetting(settings, "lighting.reflection.probeFallbacks",
                diagnostics.HybridReflectionProbeFallbackCount);
            AddSetting(settings, "lighting.reflection.environmentFallbacks",
                diagnostics.HybridReflectionEnvironmentFallbackCount);
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
            AddSetting(settings, "layout.requiredDegraded", layout.HasRequiredDegradation ? 1 : 0);
            AddSetting(settings, "layout.requiredRejectedVolumes", layout.RequiredRejectedVolumeCount);
            AddSetting(settings, "layout.optionalRejectedVolumes", layout.OptionalRejectedVolumeCount);
            AddSetting(settings, "layout.unclassifiedRejectedVolumes", layout.UnclassifiedRejectedVolumeCount);
            AddSetting(settings, "layout.residency.mode", layout.ResidencyMode.ToString());
            AddSetting(settings, "layout.residency.densePayloadProbes", layout.DensePayloadProbeCount);
            AddSetting(settings, "layout.residency.sparseVirtualProbes", layout.SparseVirtualProbeCount);
            AddSetting(settings, "layout.residency.sparseVirtualPages", layout.SparseVirtualPageCount);
            AddSetting(settings, "layout.residency.sparsePhysicalPages", layout.SparsePhysicalPageCapacity);
            AddSetting(settings, "layout.residency.physicalProbeCapacity", layout.PhysicalProbeCapacity);
            AddSetting(settings, "layout.residency.sampledProbeCapacity", layout.SampledAtlasPhysicalProbeCapacity);
            AddSetting(settings, "layout.residency.pagePaddingProbes", layout.SparsePagePaddingProbeCount);
            AddSetting(settings, "layout.residency.sampledPaddingProbes", layout.SampledAtlasPaddingProbeCount);
            AddSetting(settings, "layout.residency.sampledPaddingBytes", layout.SampledAtlasPaddingBytes);
            AddSetting(settings, "layout.residency.denseEquivalentBytes", layout.DenseEquivalentBytes);
            AddSetting(settings, "layout.residency.allocatedBytes", layout.AllocatedSparseBytes);
            AddSetting(settings, "layout.residency.avoidedBytes", layout.AvoidedBytes);
            AddSetting(settings, "layout.residency.arenaBytes", layout.ResidencyArenaBytes);
            AddSetting(settings, "layout.residency.pageBudgetReduced", layout.PhysicalPageBudgetWasReduced ? 1 : 0);
            AddSetting(settings, "layout.residency.pageBudgetDecision", layout.PhysicalPageBudgetDecision);
            AddSetting(settings, "layout.residency.fallbackReason", layout.ResidencyFallbackReason);

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
                        volume.AdmissionClass.ToString(),
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
            AddSetting(settings, "scheduler.policy.targetGpuMicroseconds", policy.TargetGpuMicroseconds);
            AddSetting(settings, "scheduler.policy.deterministicFixedBudget", policy.DeterministicFixedBudget ? 1 : 0);
            AddSetting(settings, "scheduler.policy.unavailableReason", policy.UnavailableReason);
        }

        private static void AddProbeResidencySettings(
            List<string> settings,
            SimpleDdgiProbeResidencyTelemetry residency)
        {
            AddSetting(settings, "residency.available", residency.IsAvailable ? 1 : 0);
            AddSetting(settings, "residency.mode", residency.Mode.ToString());
            AddSetting(settings, "residency.sparseAuthoritative", residency.SparseAuthoritative ? 1 : 0);
            AddSetting(settings, "residency.mutationFrozen", residency.MutationFrozen ? 1 : 0);
            AddSetting(settings, "residency.developmentMutationFrozen", residency.DevelopmentMutationFrozen ? 1 : 0);
            AddSetting(settings, "residency.stateValid", residency.ResidencyStateValid ? 1 : 0);
            AddSetting(settings, "residency.developmentPinnedPageCount", residency.DevelopmentPinnedPageCount);
            AddSetting(settings, "residency.developmentControlCommandCount", residency.DevelopmentControlCommandCount);
            AddSetting(settings, "residency.lastDevelopmentControlledVirtualPage", residency.LastDevelopmentControlledVirtualPage);
            AddSetting(settings, "residency.lastDevelopmentPinState", residency.LastDevelopmentPinState ? 1 : 0);
            AddSetting(settings, "residency.configuredPageBudget", residency.ConfiguredPhysicalPageBudget);
            AddSetting(settings, "residency.minimumPageBudget", residency.ConfiguredMinimumPhysicalPageBudget);
            AddSetting(settings, "residency.retentionFrames", residency.RetentionFrames);
            AddSetting(settings, "residency.maximumAdmissionsPerFrame", residency.MaximumAdmissionsPerFrame);
            AddSetting(settings, "residency.maximumReceiverFeedbackRequests", residency.MaximumReceiverFeedbackRequests);
            AddSetting(settings, "residency.inactiveRetryFrames", residency.InactiveRetryFrames);
            AddSetting(settings, "residency.fallbackReason", residency.FallbackReason);
        }

        private static void AddGpuSchedulerSettings(
            List<string> settings,
            RendererDiagnostics diagnostics)
        {
            AddSetting(settings, "scheduler.gpu.mode", diagnostics.SimpleDdgiSchedulerMode.ToString());
            AddSetting(settings, "scheduler.gpu.ready", diagnostics.SimpleDdgiSchedulerReady);
            AddSetting(settings, "scheduler.gpu.resourceGeneration", diagnostics.SimpleDdgiSchedulerResourceGeneration);
            AddSetting(settings, "scheduler.gpu.arenaBytes", diagnostics.SimpleDdgiSchedulerArenaBytes);
            AddSetting(settings, "scheduler.gpu.feedbackReadbackBytes", diagnostics.SimpleDdgiSchedulerFeedbackReadbackBytes);
            AddSetting(settings, "scheduler.gpu.retiredBytes", diagnostics.SimpleDdgiSchedulerRetiredBytes);
            AddSetting(settings, "scheduler.gpu.staleFeedbackCount", diagnostics.SimpleDdgiSchedulerStaleFeedbackCount);
            AddSetting(settings, "scheduler.gpu.feedbackGenerationRejections", diagnostics.SimpleDdgiSchedulerFeedbackGenerationRejectionCount);
            AddSetting(settings, "scheduler.gpu.fallbackLatched", diagnostics.SimpleDdgiSchedulerFallbackLatched);
            AddSetting(settings, "scheduler.gpu.fallbackFreshResetPending", diagnostics.SimpleDdgiSchedulerFallbackFreshResetPending);
            AddSetting(settings, "scheduler.gpu.fallbackCount", diagnostics.SimpleDdgiSchedulerFallbackCount);
            AddSetting(settings, "scheduler.gpu.fallbackReason", diagnostics.SimpleDdgiSchedulerFallbackReason);
            AddSetting(settings, "scheduler.gpu.fallbackExportPending", diagnostics.SimpleDdgiSchedulerFallbackExportPending);
            AddSetting(settings, "scheduler.gpu.fallbackExportBytes", diagnostics.SimpleDdgiSchedulerFallbackExportBytes);
            AddSetting(settings, "scheduler.gpu.stateExportSuccessCount", diagnostics.SimpleDdgiSchedulerStateExportSuccessCount);
            AddSetting(settings, "scheduler.gpu.stateExportFailureCount", diagnostics.SimpleDdgiSchedulerStateExportFailureCount);
            AddSetting(settings, "scheduler.gpu.reentryStableFrameCount", diagnostics.SimpleDdgiSchedulerReentryStableFrameCount);
            AddSetting(settings, "scheduler.gpu.reentryCount", diagnostics.SimpleDdgiSchedulerReentryCount);
            AddSetting(
                settings,
                "transport.tailCertification.fallbackReason",
                diagnostics.SimpleDdgiTransportTailCertificationFallbackReason);
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
                        state.Status.ToString()
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
