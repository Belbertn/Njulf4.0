using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Persisted intent for the receiver-feedback path.  The legacy mode exists
/// solely for A/B comparison while the exact compacted ABI is qualified.
/// </summary>
public enum SimpleDdgiReceiverFeedbackMode : uint
{
    Off = 0,
    LegacyPackedReference = 1,
    ExactCompacted = 2,
    /// <summary>
    /// Selects the exact compacted producer only when the complete B1
    /// qualification binding matches this build, device, settings, corpus,
    /// content profile, and scene asset. A mismatch retains canonical Off.
    /// </summary>
    AutoQualified = 3
}

public enum DdgiOpacityMicromapMode : uint
{
    Off = 0,
    ExtFourStateExperiment = 1,
    AutoQualified = 2
}

public enum SimpleDdgiDirectionalGuidingMode : uint
{
    Off = 0,
    CpuOracle = 1,
    PerProbeHistogramExperiment = 2,
    AutoQualified = 3
}

public enum GiCausticMode : uint
{
    Off = 0,
    PhotonReference = 1,
    WorldCacheExperiment = 2,
    AutoQualified = 3
}

public enum SimpleDdgiNearFieldResidualMode : uint
{
    Off = 0,
    Reference = 1,
    /// <summary>
    /// Original explicit C5 mode. Its numeric value is durable settings/API
    /// state and must retain the fixed admitted execution resolution.
    /// </summary>
    HiZHalfResolutionExperiment = 2,
    AutoQualified = 3,
    /// <summary>
    /// Bounded Hi-Z SSGI residual. Explicit selection starts at quarter
    /// resolution and falls back to eighth resolution when its independent
    /// memory envelope cannot admit the complete quarter-resolution profile.
    /// Half resolution is reserved for evidence-bound AutoQualified profiles.
    /// </summary>
    HiZAdaptive = 4
}

/// <summary>
/// Stable production quality policy for the C5 screen-space diffuse residual.
/// Numeric values are persisted by settings schema v14.
/// </summary>
public enum SimpleDdgiNearFieldResidualQualityPreset : uint
{
    Performance = 0,
    Balanced = 1,
    Quality = 2
}

/// <summary>
/// The five user-facing Advanced GI switches.  This is deliberately a small,
/// persistence-free command model: an editor host can carry it across a cold
/// renderer restart without manufacturing a qualification profile.
/// </summary>
public readonly record struct AdvancedGiFeatureSelection(
    bool ReceiverFeedbackEnabled,
    bool OpacityMicromapsEnabled,
    bool DirectionalGuidingEnabled,
    bool TaggedCausticsEnabled,
    bool NearFieldResidualEnabled)
{
    public static AdvancedGiFeatureSelection AllEnabled { get; } = new(
        ReceiverFeedbackEnabled: true,
        OpacityMicromapsEnabled: true,
        DirectionalGuidingEnabled: true,
        TaggedCausticsEnabled: true,
        NearFieldResidualEnabled: true);

    public bool AreAllEnabled =>
        ReceiverFeedbackEnabled &&
        OpacityMicromapsEnabled &&
        DirectionalGuidingEnabled &&
        TaggedCausticsEnabled &&
        NearFieldResidualEnabled;

    public static AdvancedGiFeatureSelection From(
        GlobalIlluminationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AdvancedGiFeatureSelection(
            settings.SimpleDdgiReceiverFeedbackMode !=
                SimpleDdgiReceiverFeedbackMode.Off,
            settings.DdgiOpacityMicromapMode != DdgiOpacityMicromapMode.Off,
            settings.SimpleDdgiDirectionalGuidingMode !=
                SimpleDdgiDirectionalGuidingMode.Off,
            settings.GiCausticMode != GiCausticMode.Off,
            settings.SimpleDdgiNearFieldResidualMode !=
                SimpleDdgiNearFieldResidualMode.Off);
    }

    /// <summary>
    /// Applies ordinary explicit modes.  AutoQualified remains available to
    /// automation through the existing settings/profile APIs, but is never
    /// selected implicitly by a UI checkbox.
    /// </summary>
    public void ApplyTo(GlobalIlluminationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SimpleDdgiReceiverFeedbackMode = ReceiverFeedbackEnabled
            ? SimpleDdgiReceiverFeedbackMode.ExactCompacted
            : SimpleDdgiReceiverFeedbackMode.Off;
        settings.DdgiOpacityMicromapMode = OpacityMicromapsEnabled
            ? DdgiOpacityMicromapMode.ExtFourStateExperiment
            : DdgiOpacityMicromapMode.Off;
        settings.SimpleDdgiDirectionalGuidingMode = DirectionalGuidingEnabled
            ? SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment
            : SimpleDdgiDirectionalGuidingMode.Off;
        settings.GiCausticMode = TaggedCausticsEnabled
            ? GiCausticMode.WorldCacheExperiment
            : GiCausticMode.Off;
        settings.SimpleDdgiNearFieldResidualMode = NearFieldResidualEnabled
            ? SimpleDdgiNearFieldResidualMode.HiZAdaptive
            : SimpleDdgiNearFieldResidualMode.Off;

        // A normal switch never inherits promotion credentials from a prior
        // AutoQualified run.
        settings.SimpleDdgiReceiverFeedbackQualificationId = string.Empty;
        settings.DdgiOpacityMicromapQualificationId = string.Empty;
        settings.SimpleDdgiDirectionalGuidingQualificationId = string.Empty;
        settings.GiCausticQualificationId = string.Empty;
        settings.SimpleDdgiNearFieldResidualQualificationId = string.Empty;
    }
}

/// <summary>
/// Separates explicit user intent from automatic promotion policy.  Explicit
/// modes still pass every hardware, Vulkan-limit, memory, ABI, allocation and
/// resource-completeness check; only AutoQualified consumes manifest gates.
/// </summary>
public static class AdvancedGiActivationPolicy
{
    public static bool RequiresQualification<TMode>(TMode mode)
        where TMode : struct, Enum =>
        ModeTraits<TMode>.HasAutoQualified &&
        EqualityComparer<TMode>.Default.Equals(
            mode,
            ModeTraits<TMode>.AutoQualified);

    public static bool PrerequisitesSatisfied<TMode>(
        TMode mode,
        in AdvancedGiPrerequisiteGateResult gate)
        where TMode : struct, Enum =>
        !RequiresQualification(mode) || gate.Passed;

    private static class ModeTraits<TMode>
        where TMode : struct, Enum
    {
        public static readonly bool HasAutoQualified;
        public static readonly TMode AutoQualified;

        static ModeTraits()
        {
            HasAutoQualified = Enum.TryParse(
                "AutoQualified",
                ignoreCase: false,
                out TMode mode);
            AutoQualified = mode;
        }
    }
}

/// <summary>
/// Stable, machine-readable reason for an advanced-GI feature to use its
/// canonical fallback.  The accompanying detail is diagnostic only and must
/// not be parsed for policy decisions.
/// </summary>
public enum GiExperimentFallbackReason : uint
{
    None = 0,
    InvalidRequestedMode = 1,
    UnsupportedCapability = 2,
    PrerequisiteMissing = 3,
    QualificationIdMissing = 4,
    QualificationNotPassed = 5,
    IndependentMemoryBudgetExceeded = 6,
    RendererMemoryHeadroomExceeded = 7,
    VulkanLimitExceeded = 8,
    ArithmeticOverflow = 9,
    ResourceIncomplete = 10,
    ResourceAllocationFailed = 11,
    LayoutRevisionMismatch = 12,
    GenerationMismatch = 13,
    FeedbackBankInvalid = 14,
    FeedbackBankOverflowed = 15,
    DeviceLost = 16,
    InvalidConfiguration = 17,
    FeedbackLayoutNotRepresentable = 18,
    EvidenceMissing = 19,
    EvidenceBindingMismatch = 20,
    EvidenceInvalid = 21
}

/// <summary>
/// Input to the common requested/supported/admitted/effective state resolver.
/// This is evaluated only on a safe resource transition; it does not mutate
/// the user-authored requested mode.
/// </summary>
public readonly record struct GiExperimentModeEvaluation(
    bool Supported,
    bool PrerequisitesSatisfied,
    bool MemoryAdmitted,
    bool ResourcesComplete,
    bool RequiresQualification,
    bool QualificationPassed,
    string? QualificationId,
    string? FailureDetail = null);

/// <summary>
/// Explicit state for a feature mode.  In particular, a requested mode is not
/// evidence that an allocation or a GPU path exists.
/// </summary>
public readonly record struct GiExperimentModeState<TMode>(
    TMode RequestedMode,
    TMode SupportedMode,
    TMode AdmittedMode,
    TMode EffectiveMode,
    GiExperimentFallbackReason FallbackReason,
    string FallbackDetail,
    string QualificationId)
    where TMode : struct, Enum
{
    public bool IsEffective =>
        !EqualityComparer<TMode>.Default.Equals(EffectiveMode, default);

    public bool IsAdmitted =>
        !EqualityComparer<TMode>.Default.Equals(AdmittedMode, default);

    public static GiExperimentModeState<TMode> Disabled(TMode offMode) => new(
        offMode,
        offMode,
        offMode,
        offMode,
        GiExperimentFallbackReason.None,
        "disabled-by-request",
        string.Empty);
}

/// <summary>
/// Centralized fail-closed mode resolution.  It deliberately has no renderer
/// side effects, making admission recomputable for resize, reload, restart,
/// and device-loss transitions.
/// </summary>
public static class GiExperimentModeResolver
{
    public static GiExperimentModeState<TMode> Resolve<TMode>(
        TMode requestedMode,
        TMode offMode,
        in GiExperimentModeEvaluation evaluation)
        where TMode : struct, Enum
    {
        string qualificationId = NormalizeQualificationId(evaluation.QualificationId);
        if (!Enum.IsDefined(typeof(TMode), requestedMode) ||
            !Enum.IsDefined(typeof(TMode), offMode))
        {
            return new GiExperimentModeState<TMode>(
                requestedMode,
                offMode,
                offMode,
                offMode,
                GiExperimentFallbackReason.InvalidRequestedMode,
                "requested-mode-is-not-defined-by-the-current-abi",
                qualificationId);
        }

        if (EqualityComparer<TMode>.Default.Equals(requestedMode, offMode))
            return GiExperimentModeState<TMode>.Disabled(offMode);

        if (!evaluation.Supported)
        {
            return Fallback(
                requestedMode,
                offMode,
                offMode,
                GiExperimentFallbackReason.UnsupportedCapability,
                "device-tool-or-content-capability-is-unavailable",
                qualificationId,
                evaluation.FailureDetail);
        }

        if (!evaluation.PrerequisitesSatisfied)
        {
            return Fallback(
                requestedMode,
                requestedMode,
                offMode,
                GiExperimentFallbackReason.PrerequisiteMissing,
                "prerequisite-contract-is-not-satisfied",
                qualificationId,
                evaluation.FailureDetail);
        }

        bool requiresQualification = evaluation.RequiresQualification ||
            AdvancedGiActivationPolicy.RequiresQualification(requestedMode);
        if (requiresQualification && qualificationId.Length == 0)
        {
            return Fallback(
                requestedMode,
                requestedMode,
                offMode,
                GiExperimentFallbackReason.QualificationIdMissing,
                "qualification-id-is-required-for-this-mode",
                qualificationId,
                evaluation.FailureDetail);
        }

        if (requiresQualification && !evaluation.QualificationPassed)
        {
            return Fallback(
                requestedMode,
                requestedMode,
                offMode,
                GiExperimentFallbackReason.QualificationNotPassed,
                "qualification-evidence-is-missing-or-rejected",
                qualificationId,
                evaluation.FailureDetail);
        }

        if (!evaluation.MemoryAdmitted)
        {
            return Fallback(
                requestedMode,
                requestedMode,
                offMode,
                GiExperimentFallbackReason.IndependentMemoryBudgetExceeded,
                "independent-feature-memory-budget-rejected",
                qualificationId,
                evaluation.FailureDetail);
        }

        if (!evaluation.ResourcesComplete)
        {
            return Fallback(
                requestedMode,
                requestedMode,
                requestedMode,
                GiExperimentFallbackReason.ResourceIncomplete,
                "admitted-resources-are-not-complete-for-this-frame",
                qualificationId,
                evaluation.FailureDetail);
        }

        return new GiExperimentModeState<TMode>(
            requestedMode,
            requestedMode,
            requestedMode,
            requestedMode,
            GiExperimentFallbackReason.None,
            "active",
            qualificationId);
    }

    private static GiExperimentModeState<TMode> Fallback<TMode>(
        TMode requestedMode,
        TMode supportedMode,
        TMode admittedMode,
        GiExperimentFallbackReason reason,
        string defaultDetail,
        string qualificationId,
        string? suppliedDetail)
        where TMode : struct, Enum => new(
            requestedMode,
            supportedMode,
            admittedMode,
            default,
            reason,
            string.IsNullOrWhiteSpace(suppliedDetail)
                ? defaultDetail
                : suppliedDetail.Trim(),
            qualificationId);

    private static string NormalizeQualificationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim();
        // This is an evidence hash/key, not a free-form report.  Keep the
        // runtime contract consistent with persisted settings and fail closed
        // rather than allowing an unbounded string into diagnostics/captures.
        return normalized.Length <= 256 ? normalized : string.Empty;
    }
}

/// <summary>
/// Independently rejectable advanced-GI allocations.  The names intentionally
/// match the published memory-plan schema.
/// </summary>
public enum SimpleDdgiAdvancedMemoryCategory : uint
{
    ReceiverFeedbackRecordBanks = 0,
    ReceiverFeedbackSortScratch = 1,
    ReceiverFeedbackProbeSummaries = 2,
    OpacityMicromapResidentData = 3,
    OpacityMicromapBuildScratch = 4,
    OpacityMicromapCompactionHeadroom = 5,
    DirectionalGuidingHistoryBanks = 6,
    DirectionalGuidingBuildScratch = 7,
    CausticPhotonRecords = 8,
    CausticCellTableAndSortScratch = 9,
    CausticHistory = 10,
    NearFieldTraceTargets = 11,
    NearFieldHistoryAndMoments = 12,
    NearFieldFilterScratch = 13
}

/// <summary>
/// Byte accounting for one independently rejectable category.  A disabled or
/// rejected feature must report zero in every byte field; the fallback reason
/// explains why it owns no resources.
/// </summary>
public readonly record struct SimpleDdgiAdvancedMemoryUsage(
    SimpleDdgiAdvancedMemoryCategory Category,
    ulong RequestedBytes,
    ulong RequiredBytes,
    ulong AdmittedBytes,
    ulong AllocatedBytes,
    ulong PeakLiveBytes,
    ulong RetiredButLiveBytes,
    ulong FallbackBytes,
    GiExperimentFallbackReason FallbackReason)
{
    public bool IsZero => RequestedBytes == 0UL &&
        RequiredBytes == 0UL &&
        AdmittedBytes == 0UL &&
        AllocatedBytes == 0UL &&
        PeakLiveBytes == 0UL &&
        RetiredButLiveBytes == 0UL &&
        FallbackBytes == 0UL;

    public bool IsValidFor(
        SimpleDdgiAdvancedMemoryCategory expectedCategory)
    {
        if (Category != expectedCategory ||
            !Enum.IsDefined(Category) ||
            !Enum.IsDefined(FallbackReason))
        {
            return false;
        }

        // A category may retain bytes after its active allocation has been
        // retired, but it may never allocate more than was admitted and its
        // observed peak may never be smaller than its current allocation.
        return RequiredBytes <= RequestedBytes &&
            AdmittedBytes <= RequiredBytes &&
            AllocatedBytes <= AdmittedBytes &&
            PeakLiveBytes >= AllocatedBytes &&
            FallbackBytes <= RequiredBytes &&
            (FallbackReason == GiExperimentFallbackReason.None || IsZero);
    }

    public static SimpleDdgiAdvancedMemoryUsage Zero(
        SimpleDdgiAdvancedMemoryCategory category,
        GiExperimentFallbackReason reason = GiExperimentFallbackReason.None) =>
        new(category, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, reason);

    public static SimpleDdgiAdvancedMemoryUsage Admitted(
        SimpleDdgiAdvancedMemoryCategory category,
        ulong requiredBytes,
        ulong allocatedBytes,
        ulong peakLiveBytes,
        ulong retiredButLiveBytes = 0UL) => new(
            category,
            requiredBytes,
            requiredBytes,
            requiredBytes,
            allocatedBytes,
            peakLiveBytes,
            retiredButLiveBytes,
            0UL,
            GiExperimentFallbackReason.None);
}

/// <summary>
/// Fixed-shape central memory plan.  A fixed shape avoids missing a category
/// in a diagnostic snapshot when a feature is disabled.
/// </summary>
public readonly record struct SimpleDdgiAdvancedExperimentMemoryPlan(
    SimpleDdgiAdvancedMemoryUsage ReceiverFeedbackRecordBanks,
    SimpleDdgiAdvancedMemoryUsage ReceiverFeedbackSortScratch,
    SimpleDdgiAdvancedMemoryUsage ReceiverFeedbackProbeSummaries,
    SimpleDdgiAdvancedMemoryUsage OpacityMicromapResidentData,
    SimpleDdgiAdvancedMemoryUsage OpacityMicromapBuildScratch,
    SimpleDdgiAdvancedMemoryUsage OpacityMicromapCompactionHeadroom,
    SimpleDdgiAdvancedMemoryUsage DirectionalGuidingHistoryBanks,
    SimpleDdgiAdvancedMemoryUsage DirectionalGuidingBuildScratch,
    SimpleDdgiAdvancedMemoryUsage CausticPhotonRecords,
    SimpleDdgiAdvancedMemoryUsage CausticCellTableAndSortScratch,
    SimpleDdgiAdvancedMemoryUsage CausticHistory,
    SimpleDdgiAdvancedMemoryUsage NearFieldTraceTargets,
    SimpleDdgiAdvancedMemoryUsage NearFieldHistoryAndMoments,
    SimpleDdgiAdvancedMemoryUsage NearFieldFilterScratch)
{
    public static SimpleDdgiAdvancedExperimentMemoryPlan Empty { get; } =
        CreateZero(GiExperimentFallbackReason.None);

    public ulong AllocatedBytes => Sum(static usage => usage.AllocatedBytes);

    public ulong PeakLiveBytes => Sum(static usage => usage.PeakLiveBytes);

    public ulong RetiredButLiveBytes => Sum(static usage => usage.RetiredButLiveBytes);

    /// <summary>Persistent resources are never candidates for aliasing.</summary>
    public ulong PersistentAllocatedBytes => checked(
        ReceiverFeedbackRecordBanks.AllocatedBytes +
        ReceiverFeedbackProbeSummaries.AllocatedBytes +
        OpacityMicromapResidentData.AllocatedBytes +
        DirectionalGuidingHistoryBanks.AllocatedBytes +
        CausticPhotonRecords.AllocatedBytes +
        CausticHistory.AllocatedBytes +
        NearFieldHistoryAndMoments.AllocatedBytes);

    /// <summary>
    /// Conservative transient peak before the render graph proves legal alias
    /// intervals.  Integrators may lower it only via <see
    /// cref="GiExperimentScratchAliasing"/> with declared lifetimes.
    /// </summary>
    public ulong ConservativeTransientPeakLiveBytes => checked(
        ReceiverFeedbackSortScratch.PeakLiveBytes +
        OpacityMicromapBuildScratch.PeakLiveBytes +
        OpacityMicromapCompactionHeadroom.PeakLiveBytes +
        DirectionalGuidingBuildScratch.PeakLiveBytes +
        CausticCellTableAndSortScratch.PeakLiveBytes +
        NearFieldTraceTargets.PeakLiveBytes +
        NearFieldFilterScratch.PeakLiveBytes);

    public bool AllCategoriesZero =>
        ReceiverFeedbackRecordBanks.IsZero &&
        ReceiverFeedbackSortScratch.IsZero &&
        ReceiverFeedbackProbeSummaries.IsZero &&
        OpacityMicromapResidentData.IsZero &&
        OpacityMicromapBuildScratch.IsZero &&
        OpacityMicromapCompactionHeadroom.IsZero &&
        DirectionalGuidingHistoryBanks.IsZero &&
        DirectionalGuidingBuildScratch.IsZero &&
        CausticPhotonRecords.IsZero &&
        CausticCellTableAndSortScratch.IsZero &&
        CausticHistory.IsZero &&
        NearFieldTraceTargets.IsZero &&
        NearFieldHistoryAndMoments.IsZero &&
        NearFieldFilterScratch.IsZero;

    /// <summary>
    /// Guards attachment of a C5 sub-plan to the central content plan.  The
    /// C5 compiler is not allowed to claim another experiment's allocation.
    /// </summary>
    public bool HasOnlyNearFieldResidualCategories =>
        ReceiverFeedbackRecordBanks.IsZero &&
        ReceiverFeedbackSortScratch.IsZero &&
        ReceiverFeedbackProbeSummaries.IsZero &&
        OpacityMicromapResidentData.IsZero &&
        OpacityMicromapBuildScratch.IsZero &&
        OpacityMicromapCompactionHeadroom.IsZero &&
        DirectionalGuidingHistoryBanks.IsZero &&
        DirectionalGuidingBuildScratch.IsZero &&
        CausticPhotonRecords.IsZero &&
        CausticCellTableAndSortScratch.IsZero &&
        CausticHistory.IsZero;

    public bool HasOnlyOpacityMicromapCategories =>
        ReceiverFeedbackRecordBanks.IsZero &&
        ReceiverFeedbackSortScratch.IsZero &&
        ReceiverFeedbackProbeSummaries.IsZero &&
        DirectionalGuidingHistoryBanks.IsZero &&
        DirectionalGuidingBuildScratch.IsZero &&
        CausticPhotonRecords.IsZero &&
        CausticCellTableAndSortScratch.IsZero &&
        CausticHistory.IsZero &&
        NearFieldTraceTargets.IsZero &&
        NearFieldHistoryAndMoments.IsZero &&
        NearFieldFilterScratch.IsZero;

    public bool IsValid =>
        ReceiverFeedbackRecordBanks.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks) &&
        ReceiverFeedbackSortScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch) &&
        ReceiverFeedbackProbeSummaries.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries) &&
        OpacityMicromapResidentData.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.OpacityMicromapResidentData) &&
        OpacityMicromapBuildScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch) &&
        OpacityMicromapCompactionHeadroom.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom) &&
        DirectionalGuidingHistoryBanks.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks) &&
        DirectionalGuidingBuildScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch) &&
        CausticPhotonRecords.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords) &&
        CausticCellTableAndSortScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch) &&
        CausticHistory.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticHistory) &&
        NearFieldTraceTargets.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets) &&
        NearFieldHistoryAndMoments.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments) &&
        NearFieldFilterScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch);

    public SimpleDdgiAdvancedExperimentMemoryPlan NormalizeForPersistence()
    {
        SimpleDdgiAdvancedExperimentMemoryPlan normalized =
            NormalizeUninitialized(this);
        return normalized.IsValid ? normalized : Empty;
    }

    /// <summary>
    /// Only these categories may be placed in a render-graph aliasing interval.
    /// Every other category is persistent by definition and must retain its
    /// own allocation until the owning generation is safely retired.
    /// </summary>
    public static bool IsTransientCategory(
        SimpleDdgiAdvancedMemoryCategory category) => category switch
    {
        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch => true,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch => true,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom => true,
        SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch => true,
        SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch => true,
        SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets => true,
        SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch => true,
        _ => false
    };

    public SimpleDdgiAdvancedMemoryUsage Get(
        SimpleDdgiAdvancedMemoryCategory category) => category switch
    {
        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks =>
            ReceiverFeedbackRecordBanks,
        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch =>
            ReceiverFeedbackSortScratch,
        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries =>
            ReceiverFeedbackProbeSummaries,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapResidentData =>
            OpacityMicromapResidentData,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch =>
            OpacityMicromapBuildScratch,
        SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom =>
            OpacityMicromapCompactionHeadroom,
        SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks =>
            DirectionalGuidingHistoryBanks,
        SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch =>
            DirectionalGuidingBuildScratch,
        SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords =>
            CausticPhotonRecords,
        SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch =>
            CausticCellTableAndSortScratch,
        SimpleDdgiAdvancedMemoryCategory.CausticHistory => CausticHistory,
        SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets =>
            NearFieldTraceTargets,
        SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments =>
            NearFieldHistoryAndMoments,
        SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch =>
            NearFieldFilterScratch,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    /// <summary>
    /// Combines plans that own disjoint category sets.  A category represents
    /// one physical ownership domain, so accepting two nonzero entries would
    /// double-count a resource and can hide a renderer-headroom failure.
    /// Feature planners should return zero for every category they do not own.
    /// </summary>
    public static SimpleDdgiAdvancedExperimentMemoryPlan CombineDisjoint(
        in SimpleDdgiAdvancedExperimentMemoryPlan left,
        in SimpleDdgiAdvancedExperimentMemoryPlan right)
    {
        // record structs can be introduced through default(T), bypassing the
        // category-tag initializers. Treat that all-zero transport value as
        // the documented empty plan instead of comparing its accidental
        // ReceiverFeedbackRecordBanks tags against every category.
        SimpleDdgiAdvancedExperimentMemoryPlan normalizedLeft =
            NormalizeUninitialized(left);
        SimpleDdgiAdvancedExperimentMemoryPlan normalizedRight =
            NormalizeUninitialized(right);
        return new SimpleDdgiAdvancedExperimentMemoryPlan(
            CombineUsage(normalizedLeft.ReceiverFeedbackRecordBanks,
                normalizedRight.ReceiverFeedbackRecordBanks),
            CombineUsage(normalizedLeft.ReceiverFeedbackSortScratch,
                normalizedRight.ReceiverFeedbackSortScratch),
            CombineUsage(normalizedLeft.ReceiverFeedbackProbeSummaries,
                normalizedRight.ReceiverFeedbackProbeSummaries),
            CombineUsage(normalizedLeft.OpacityMicromapResidentData,
                normalizedRight.OpacityMicromapResidentData),
            CombineUsage(normalizedLeft.OpacityMicromapBuildScratch,
                normalizedRight.OpacityMicromapBuildScratch),
            CombineUsage(normalizedLeft.OpacityMicromapCompactionHeadroom,
                normalizedRight.OpacityMicromapCompactionHeadroom),
            CombineUsage(normalizedLeft.DirectionalGuidingHistoryBanks,
                normalizedRight.DirectionalGuidingHistoryBanks),
            CombineUsage(normalizedLeft.DirectionalGuidingBuildScratch,
                normalizedRight.DirectionalGuidingBuildScratch),
            CombineUsage(normalizedLeft.CausticPhotonRecords,
                normalizedRight.CausticPhotonRecords),
            CombineUsage(normalizedLeft.CausticCellTableAndSortScratch,
                normalizedRight.CausticCellTableAndSortScratch),
            CombineUsage(normalizedLeft.CausticHistory,
                normalizedRight.CausticHistory),
            CombineUsage(normalizedLeft.NearFieldTraceTargets,
                normalizedRight.NearFieldTraceTargets),
            CombineUsage(normalizedLeft.NearFieldHistoryAndMoments,
                normalizedRight.NearFieldHistoryAndMoments),
            CombineUsage(normalizedLeft.NearFieldFilterScratch,
                normalizedRight.NearFieldFilterScratch));
    }

    public static SimpleDdgiAdvancedExperimentMemoryPlan CreateZero(
        GiExperimentFallbackReason reason) => new(
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapResidentData,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticHistory,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments,
                reason),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch,
                reason));

    /// <summary>
    /// Produces a fixed-shape B1 rejection without assigning B1's fallback to
    /// categories owned by C1/C3/C4/C5. This matters when independent plans
    /// are combined into one persisted ownership record.
    /// </summary>
    public static SimpleDdgiAdvancedExperimentMemoryPlan
        CreateReceiverFeedbackRejected(
            GiExperimentFallbackReason reason) => Empty with
        {
            ReceiverFeedbackRecordBanks = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
                reason),
            ReceiverFeedbackSortScratch = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                reason),
            ReceiverFeedbackProbeSummaries = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries,
                reason)
        };

    /// <summary>
    /// Attaches C3's persistent distribution banks, validation mirror, and
    /// source-cache direction/PDF sidecar to the history category, while the
    /// complete serialized train/sample workspace remains independently
    /// aliasable build scratch. No sidecar or staging byte may be charged to
    /// the guiding manager twice.
    /// </summary>
    public static SimpleDdgiAdvancedExperimentMemoryPlan
        CreateDirectionalGuiding(
            in SimpleDdgiGuidingLayout layout,
            ulong allocatedHistoryBytes,
            ulong allocatedScratchBytes,
            ulong retiredHistoryBytes = 0UL,
            ulong retiredScratchBytes = 0UL)
    {
        ulong requiredHistory = checked(
            layout.PersistentDoubleBufferedBytes +
            layout.ValidationReferenceBankBytes +
            layout.DirectionPdfSidecarBytes);
        ulong requiredScratch = layout.TransientWorkspace.TotalBytes;
        if (!layout.HasAllocation || !layout.HasTransportSidecar ||
            !layout.TransientWorkspace.IsComplete || requiredHistory == 0UL ||
            requiredScratch == 0UL ||
            allocatedHistoryBytes > requiredHistory ||
            allocatedScratchBytes > requiredScratch)
        {
            throw new ArgumentException(
                "C3 memory requires a complete transport layout and bounded live allocations.",
                nameof(layout));
        }

        return Empty with
        {
            DirectionalGuidingHistoryBanks =
                SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingHistoryBanks,
                    requiredHistory,
                    allocatedHistoryBytes,
                    checked(allocatedHistoryBytes + retiredHistoryBytes),
                    retiredHistoryBytes),
            DirectionalGuidingBuildScratch =
                SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingBuildScratch,
                    requiredScratch,
                    allocatedScratchBytes,
                    checked(allocatedScratchBytes + retiredScratchBytes),
                    retiredScratchBytes)
        };
    }

    public static SimpleDdgiAdvancedExperimentMemoryPlan
        CreateDirectionalGuidingRejected(
            GiExperimentFallbackReason reason) => Empty with
        {
            DirectionalGuidingHistoryBanks =
                SimpleDdgiAdvancedMemoryUsage.Zero(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingHistoryBanks,
                    reason),
            DirectionalGuidingBuildScratch =
                SimpleDdgiAdvancedMemoryUsage.Zero(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingBuildScratch,
                    reason)
        };

    public static SimpleDdgiAdvancedExperimentMemoryPlan CreateCaustic(
        in GiCausticGpuMemoryRequirements requirements)
    {
        if (!requirements.PhotonRecords.IsValidFor(
                SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords) ||
            !requirements.CellTableAndSortScratch.IsValidFor(
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch) ||
            !requirements.History.IsValidFor(
                SimpleDdgiAdvancedMemoryCategory.CausticHistory) ||
            requirements.RequiredBytes == 0UL ||
            requirements.AllocatedBytes != requirements.RequiredBytes)
        {
            throw new ArgumentException(
                "C4 memory requirements must be complete, allocated, and category-correct.",
                nameof(requirements));
        }
        return Empty with
        {
            CausticPhotonRecords = requirements.PhotonRecords,
            CausticCellTableAndSortScratch =
                requirements.CellTableAndSortScratch,
            CausticHistory = requirements.History
        };
    }

    public static SimpleDdgiAdvancedExperimentMemoryPlan CreateCausticRejected(
        GiExperimentFallbackReason reason) => Empty with
        {
            CausticPhotonRecords = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
                reason),
            CausticCellTableAndSortScratch = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                reason),
            CausticHistory = SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticHistory,
                reason)
        };

    /// <summary>
    /// Compiles the three C5 ownership categories from a complete selected
    /// layout.  The layout is the sole byte authority: source, prepared
    /// receivers, surface identity, activity buffers, temporal history, and filter ping-pong
    /// must all be represented.  The method rejects rather than reporting a
    /// deceptively smaller partial C5 allocation.
    /// </summary>
    public static bool TryCreateNearFieldResidual(
        in SimpleDdgiNearFieldResidualLayout layout,
        out SimpleDdgiAdvancedExperimentMemoryPlan plan,
        out GiExperimentFallbackReason fallbackReason,
        out string failure)
    {
        if (!layout.IsValid)
        {
            fallbackReason = GiExperimentFallbackReason.InvalidConfiguration;
            failure = "near-field-layout-is-not-valid";
            plan = CreateNearFieldResidualRejected(fallbackReason);
            return false;
        }

        if (layout.TraceSourceBytes == 0UL ||
            layout.ReceiverPayloadBytes == 0UL ||
            (layout.SourceProducerMode ==
                 SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster &&
             layout.TraceRasterDepthBytes == 0UL) ||
            (layout.SourceProducerMode ==
                 SimpleDdgiNearFieldSourceProducerMode.ForwardMrt &&
             layout.TraceRasterDepthBytes != 0UL) ||
            layout.TraceFrameConstantsBytes == 0UL ||
            layout.PreparedDepthFootprintBytes == 0UL ||
            layout.PreparedReceiverPayloadBytes == 0UL ||
            layout.PreparedMotionBytes == 0UL ||
            layout.SourceLuminanceBytes == 0UL ||
            layout.RawCandidateBytes == 0UL ||
            layout.HitMetadataBytes != 0UL ||
            layout.HistoryRadianceBytes == 0UL ||
            layout.MomentBytes == 0UL ||
            layout.HistoryValidityBytes == 0UL ||
            layout.HistoryMetadataBytes == 0UL ||
            layout.HistoryNormalBytes == 0UL ||
            layout.SurfaceTableBytes == 0UL ||
            layout.ActiveTileAndIndirectBytes == 0UL ||
            layout.TileBuffersBytes == 0UL ||
            layout.TelemetryReadbackBytes == 0UL ||
            (layout.FilterIterationCount == 0 && layout.FilterScratchBytes != 0UL) ||
            (layout.FilterIterationCount > 0 && layout.FilterScratchBytes == 0UL))
        {
            fallbackReason = GiExperimentFallbackReason.InvalidConfiguration;
            failure = "near-field-layout-is-missing-a-required-resource-category";
            plan = CreateNearFieldResidualRejected(fallbackReason);
            return false;
        }

        try
        {
            ulong traceTargets = checked(
                layout.TraceSourceBytes +
                layout.ReceiverPayloadBytes +
                layout.TraceRasterDepthBytes +
                layout.TraceFrameConstantsBytes +
                layout.PreparedDepthFootprintBytes +
                layout.PreparedReceiverPayloadBytes +
                layout.PreparedMotionBytes +
                layout.SourceLuminanceBytes +
                layout.RawCandidateBytes +
                layout.SurfaceTableBytes +
                layout.ActiveTileAndIndirectBytes +
                layout.SchedulerHistoryBytes +
                layout.TileBuffersBytes +
                layout.TelemetryReadbackBytes);
            ulong historyAndMoments = checked(
                layout.HistoryRadianceBytes +
                layout.MomentBytes +
                layout.HistoryValidityBytes +
                layout.HistoryMetadataBytes +
                layout.HistoryNormalBytes);
            ulong compiledTotal = checked(
                traceTargets + historyAndMoments + layout.FilterScratchBytes);
            if (compiledTotal != layout.TotalBytes)
            {
                fallbackReason = GiExperimentFallbackReason.InvalidConfiguration;
                failure = "near-field-layout-total-does-not-match-resource-categories";
                plan = CreateNearFieldResidualRejected(fallbackReason);
                return false;
            }

            plan = Empty with
            {
                NearFieldTraceTargets = SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets,
                    traceTargets,
                    traceTargets,
                    traceTargets),
                NearFieldHistoryAndMoments = SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments,
                    historyAndMoments,
                    historyAndMoments,
                    historyAndMoments),
                NearFieldFilterScratch = SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch,
                    layout.FilterScratchBytes,
                    layout.FilterScratchBytes,
                    layout.FilterScratchBytes)
            };
            fallbackReason = GiExperimentFallbackReason.None;
            failure = "valid";
            return true;
        }
        catch (OverflowException)
        {
            fallbackReason = GiExperimentFallbackReason.ArithmeticOverflow;
            failure = "near-field-memory-category-overflow";
            plan = CreateNearFieldResidualRejected(fallbackReason);
            return false;
        }
    }

    /// <summary>
    /// Produces a fixed-shape C5-disabled memory record.  Only C5 categories
    /// carry the failure reason; every byte field remains exactly zero.
    /// </summary>
    public static SimpleDdgiAdvancedExperimentMemoryPlan CreateNearFieldResidualRejected(
        GiExperimentFallbackReason reason) => Empty with
    {
        NearFieldTraceTargets = SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets,
            reason),
        NearFieldHistoryAndMoments = SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments,
            reason),
        NearFieldFilterScratch = SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch,
            reason)
    };

    private ulong Sum(Func<SimpleDdgiAdvancedMemoryUsage, ulong> selector)
    {
        ulong total = 0UL;
        foreach (SimpleDdgiAdvancedMemoryCategory category in
                 Enum.GetValues<SimpleDdgiAdvancedMemoryCategory>())
        {
            total = checked(total + selector(Get(category)));
        }
        return total;
    }

    private static SimpleDdgiAdvancedMemoryUsage CombineUsage(
        in SimpleDdgiAdvancedMemoryUsage left,
        in SimpleDdgiAdvancedMemoryUsage right)
    {
        if (left.Category != right.Category)
        {
            throw new ArgumentException(
                "Advanced-GI memory usage was attached to the wrong category field.");
        }
        if (!left.IsZero && !right.IsZero)
        {
            throw new InvalidOperationException(
                $"Advanced-GI memory category {left.Category} has more than one owner.");
        }
        if (!left.IsZero)
            return left;
        if (!right.IsZero)
            return right;

        GiExperimentFallbackReason reason = left.FallbackReason !=
            GiExperimentFallbackReason.None
            ? left.FallbackReason
            : right.FallbackReason;
        return SimpleDdgiAdvancedMemoryUsage.Zero(left.Category, reason);
    }

    private static SimpleDdgiAdvancedExperimentMemoryPlan NormalizeUninitialized(
        in SimpleDdgiAdvancedExperimentMemoryPlan plan) =>
        plan.Equals(default(SimpleDdgiAdvancedExperimentMemoryPlan))
            ? Empty
            : plan;
}

/// <summary>
/// A pass interval supplied by the render graph for a transient category.
/// Persistent categories never use this form and are therefore never aliased.
/// </summary>
public readonly record struct GiExperimentScratchInterval(
    int FirstPassInclusive,
    int LastPassInclusive)
{
    public void Validate()
    {
        if (FirstPassInclusive < 0 || LastPassInclusive < FirstPassInclusive)
            throw new ArgumentOutOfRangeException(nameof(FirstPassInclusive));
    }

    public bool CanAlias(in GiExperimentScratchInterval other)
    {
        Validate();
        other.Validate();
        return LastPassInclusive < other.FirstPassInclusive ||
            other.LastPassInclusive < FirstPassInclusive;
    }
}

public readonly record struct GiExperimentScratchAllocation(
    SimpleDdgiAdvancedMemoryCategory Category,
    ulong Bytes,
    GiExperimentScratchInterval Interval,
    ulong Alignment = 1UL);

/// <summary>
/// One immutable subrange of the physical advanced-GI transient arena.  Two
/// slices may overlap in bytes only when their inclusive pass intervals do not
/// overlap.  Offsets and sizes are 64-bit because Vulkan buffer ranges are not
/// constrained to the host process' native integer width.
/// </summary>
public readonly record struct GiExperimentScratchSlice(
    SimpleDdgiAdvancedMemoryCategory Category,
    ulong Offset,
    ulong Bytes,
    ulong Alignment,
    GiExperimentScratchInterval Interval)
{
    public ulong EndExclusive => checked(Offset + Bytes);

    public bool ByteRangeOverlaps(in GiExperimentScratchSlice other) =>
        Offset < other.EndExclusive && other.Offset < EndExclusive;
}

/// <summary>
/// Deterministic placement result for one physical transient buffer arena.
/// The constructor is private so callers cannot manufacture a plan whose
/// slices overlap while live.
/// </summary>
public sealed class GiExperimentScratchArenaPlan
{
    private readonly GiExperimentScratchSlice[] _slices;
    private readonly IReadOnlyList<GiExperimentScratchSlice> _readOnlySlices;

    internal GiExperimentScratchArenaPlan(
        GiExperimentScratchSlice[] slices,
        ulong requiredBytes,
        ulong peakLiveBytes,
        ulong unaliasedBytes,
        ulong layoutFingerprint)
    {
        _slices = slices ?? throw new ArgumentNullException(nameof(slices));
        _readOnlySlices = Array.AsReadOnly(_slices);
        RequiredBytes = requiredBytes;
        PeakLiveBytes = peakLiveBytes;
        UnaliasedBytes = unaliasedBytes;
        LayoutFingerprint = layoutFingerprint;
    }

    public static GiExperimentScratchArenaPlan Empty { get; } = new(
        [], 0UL, 0UL, 0UL, GiExperimentScratchAliasing.EmptyLayoutFingerprint);

    public IReadOnlyList<GiExperimentScratchSlice> Slices => _readOnlySlices;

    /// <summary>Exact bytes required by the compiled physical placement.</summary>
    public ulong RequiredBytes { get; }

    /// <summary>
    /// The theoretical live-byte lower bound before alignment and placement
    /// fragmentation.  <see cref="RequiredBytes"/> is never smaller.
    /// </summary>
    public ulong PeakLiveBytes { get; }

    /// <summary>Sum of all slice sizes if every category had a private buffer.</summary>
    public ulong UnaliasedBytes { get; }

    public ulong AliasedBytesSaved => UnaliasedBytes > RequiredBytes
        ? UnaliasedBytes - RequiredBytes
        : 0UL;

    public ulong PlacementOverheadBytes => checked(RequiredBytes - PeakLiveBytes);

    /// <summary>
    /// Stable FNV-1a identity over the ordered category/offset/size/alignment/
    /// lifetime tuples.  It is an in-process ABI key, not a security hash.
    /// </summary>
    public ulong LayoutFingerprint { get; }

    public bool TryGetSlice(
        SimpleDdgiAdvancedMemoryCategory category,
        out GiExperimentScratchSlice slice)
    {
        for (int index = 0; index < _slices.Length; index++)
        {
            if (_slices[index].Category == category)
            {
                slice = _slices[index];
                return true;
            }
        }

        slice = default;
        return false;
    }
}

public static class GiExperimentScratchAliasing
{
    internal const ulong EmptyLayoutFingerprint = 14695981039346656037UL;
    public const ulong MaximumAlignment = 1UL << 30;

    /// <summary>
    /// Computes the exact peak of declared transient lifetimes.  This is a
    /// planning-time oracle, so an O(n^2) sweep keeps the implementation simple
    /// and allocation-free for the small fixed feature set.
    /// </summary>
    public static ulong ComputePeakLiveBytes(
        ReadOnlySpan<GiExperimentScratchAllocation> allocations)
    {
        ulong peak = 0UL;
        for (int candidate = 0; candidate < allocations.Length; candidate++)
        {
            GiExperimentScratchAllocation allocation = allocations[candidate];
            if (!SimpleDdgiAdvancedExperimentMemoryPlan.IsTransientCategory(
                    allocation.Category))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(allocations),
                    "Persistent advanced-GI allocations must not alias through render-graph scratch intervals.");
            }
            allocation.Interval.Validate();
            ulong live = 0UL;
            int pass = allocation.Interval.FirstPassInclusive;
            for (int index = 0; index < allocations.Length; index++)
            {
                GiExperimentScratchAllocation other = allocations[index];
                if (!SimpleDdgiAdvancedExperimentMemoryPlan.IsTransientCategory(
                        other.Category))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(allocations),
                        "Persistent advanced-GI allocations must not alias through render-graph scratch intervals.");
                }
                other.Interval.Validate();
                if (other.Interval.FirstPassInclusive <= pass &&
                    pass <= other.Interval.LastPassInclusive)
                {
                    live = checked(live + other.Bytes);
                }
            }
            peak = Math.Max(peak, live);
        }
        return peak;
    }

    /// <summary>
    /// Compiles a deterministic first-fit interval placement.  Requests are
    /// ordered by first use, then largest-first for equal starts, so the result
    /// is stable across caller enumeration order and avoids common alignment
    /// fragmentation.  The fixed advanced-GI category set is intentionally
    /// small; an allocation-free quadratic conflict scan is faster and easier
    /// to audit than a general-purpose heap allocator here.
    /// </summary>
    public static bool TryCompileArenaPlan(
        ReadOnlySpan<GiExperimentScratchAllocation> allocations,
        out GiExperimentScratchArenaPlan plan,
        out string failure)
    {
        if (allocations.Length == 0)
        {
            plan = GiExperimentScratchArenaPlan.Empty;
            failure = string.Empty;
            return true;
        }

        int categoryCount = Enum.GetValues<SimpleDdgiAdvancedMemoryCategory>().Length;
        if (allocations.Length > categoryCount)
        {
            plan = GiExperimentScratchArenaPlan.Empty;
            failure = "advanced-gi-scratch-request-count-exceeds-category-count";
            return false;
        }

        var ordered = allocations.ToArray();
        var seen = new HashSet<SimpleDdgiAdvancedMemoryCategory>();
        try
        {
            for (int index = 0; index < ordered.Length; index++)
            {
                GiExperimentScratchAllocation allocation = ordered[index];
                if (!Enum.IsDefined(allocation.Category) ||
                    !SimpleDdgiAdvancedExperimentMemoryPlan.IsTransientCategory(
                        allocation.Category))
                {
                    plan = GiExperimentScratchArenaPlan.Empty;
                    failure = "advanced-gi-scratch-category-is-not-transient";
                    return false;
                }
                if (!seen.Add(allocation.Category))
                {
                    plan = GiExperimentScratchArenaPlan.Empty;
                    failure = "advanced-gi-scratch-category-is-duplicated";
                    return false;
                }
                if (allocation.Bytes == 0UL)
                {
                    plan = GiExperimentScratchArenaPlan.Empty;
                    failure = "advanced-gi-scratch-request-is-empty";
                    return false;
                }
                if (!IsPowerOfTwo(allocation.Alignment) ||
                    allocation.Alignment > MaximumAlignment)
                {
                    plan = GiExperimentScratchArenaPlan.Empty;
                    failure = "advanced-gi-scratch-alignment-is-invalid";
                    return false;
                }

                allocation.Interval.Validate();
            }

            Array.Sort(ordered, static (left, right) =>
            {
                int comparison = left.Interval.FirstPassInclusive.CompareTo(
                    right.Interval.FirstPassInclusive);
                if (comparison != 0)
                    return comparison;
                comparison = right.Bytes.CompareTo(left.Bytes);
                if (comparison != 0)
                    return comparison;
                comparison = left.Interval.LastPassInclusive.CompareTo(
                    right.Interval.LastPassInclusive);
                return comparison != 0
                    ? comparison
                    : left.Category.CompareTo(right.Category);
            });

            var placed = new GiExperimentScratchSlice[ordered.Length];
            ulong arenaBytes = 0UL;
            ulong unaliasedBytes = 0UL;
            for (int requestIndex = 0; requestIndex < ordered.Length; requestIndex++)
            {
                GiExperimentScratchAllocation request = ordered[requestIndex];
                var blockers = new List<GiExperimentScratchSlice>(requestIndex);
                for (int placedIndex = 0; placedIndex < requestIndex; placedIndex++)
                {
                    GiExperimentScratchSlice candidate = placed[placedIndex];
                    if (!request.Interval.CanAlias(candidate.Interval))
                        blockers.Add(candidate);
                }
                blockers.Sort(static (left, right) =>
                {
                    int comparison = left.Offset.CompareTo(right.Offset);
                    return comparison != 0
                        ? comparison
                        : left.EndExclusive.CompareTo(right.EndExclusive);
                });

                ulong cursor = 0UL;
                for (int blockerIndex = 0; blockerIndex < blockers.Count; blockerIndex++)
                {
                    GiExperimentScratchSlice blocker = blockers[blockerIndex];
                    ulong aligned = AlignUp(cursor, request.Alignment);
                    ulong end = checked(aligned + request.Bytes);
                    if (end <= blocker.Offset)
                    {
                        cursor = aligned;
                        break;
                    }

                    if (cursor < blocker.EndExclusive)
                        cursor = blocker.EndExclusive;
                }

                cursor = AlignUp(cursor, request.Alignment);
                var slice = new GiExperimentScratchSlice(
                    request.Category,
                    cursor,
                    request.Bytes,
                    request.Alignment,
                    request.Interval);
                placed[requestIndex] = slice;
                arenaBytes = Math.Max(arenaBytes, slice.EndExclusive);
                unaliasedBytes = checked(unaliasedBytes + request.Bytes);
            }

            // Present slices in category order so descriptor publication and
            // captures do not inherit the planner's packing iteration order.
            Array.Sort(placed, static (left, right) =>
                left.Category.CompareTo(right.Category));
            ulong peakLiveBytes = ComputePeakLiveBytes(ordered);
            if (arenaBytes < peakLiveBytes)
            {
                plan = GiExperimentScratchArenaPlan.Empty;
                failure = "advanced-gi-scratch-placement-accounting-is-invalid";
                return false;
            }

            for (int leftIndex = 0; leftIndex < placed.Length; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1;
                     rightIndex < placed.Length;
                     rightIndex++)
                {
                    GiExperimentScratchSlice left = placed[leftIndex];
                    GiExperimentScratchSlice right = placed[rightIndex];
                    if (!left.Interval.CanAlias(right.Interval) &&
                        left.ByteRangeOverlaps(right))
                    {
                        plan = GiExperimentScratchArenaPlan.Empty;
                        failure = "advanced-gi-scratch-live-slices-overlap";
                        return false;
                    }
                }
            }

            plan = new GiExperimentScratchArenaPlan(
                placed,
                arenaBytes,
                peakLiveBytes,
                unaliasedBytes,
                ComputeLayoutFingerprint(placed));
            failure = string.Empty;
            return true;
        }
        catch (OverflowException)
        {
            plan = GiExperimentScratchArenaPlan.Empty;
            failure = "advanced-gi-scratch-size-overflow";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            plan = GiExperimentScratchArenaPlan.Empty;
            failure = "advanced-gi-scratch-interval-is-invalid";
            return false;
        }
    }

    private static bool IsPowerOfTwo(ulong value) =>
        value != 0UL && (value & (value - 1UL)) == 0UL;

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        ulong mask = alignment - 1UL;
        return checked((value + mask) & ~mask);
    }

    private static ulong ComputeLayoutFingerprint(
        ReadOnlySpan<GiExperimentScratchSlice> slices)
    {
        ulong hash = EmptyLayoutFingerprint;
        for (int index = 0; index < slices.Length; index++)
        {
            GiExperimentScratchSlice slice = slices[index];
            Append(ref hash, (ulong)slice.Category);
            Append(ref hash, slice.Offset);
            Append(ref hash, slice.Bytes);
            Append(ref hash, slice.Alignment);
            Append(ref hash, checked((ulong)slice.Interval.FirstPassInclusive));
            Append(ref hash, checked((ulong)slice.Interval.LastPassInclusive));
        }
        return hash;
    }

    private static void Append(ref ulong hash, ulong value)
    {
        const ulong prime = 1099511628211UL;
        for (int byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
        {
            hash ^= (byte)(value >> (byteIndex * 8));
            hash = unchecked(hash * prime);
        }
    }
}

/// <summary>Mode-state payload added alongside legacy roadmap admissions.</summary>
public readonly record struct GiRoadmapExperimentModeDiagnostics(
    GiExperimentModeState<SimpleDdgiReceiverFeedbackMode> ReceiverFeedback,
    GiExperimentModeState<DdgiOpacityMicromapMode> OpacityMicromap,
    GiExperimentModeState<SimpleDdgiDirectionalGuidingMode> DirectionalGuiding,
    GiExperimentModeState<GiCausticMode> Caustic,
    GiExperimentModeState<SimpleDdgiNearFieldResidualMode> NearFieldResidual)
{
    public static GiRoadmapExperimentModeDiagnostics Disabled { get; } = new(
        GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>.Disabled(
            SimpleDdgiReceiverFeedbackMode.Off),
        GiExperimentModeState<DdgiOpacityMicromapMode>.Disabled(
            DdgiOpacityMicromapMode.Off),
        GiExperimentModeState<SimpleDdgiDirectionalGuidingMode>.Disabled(
            SimpleDdgiDirectionalGuidingMode.Off),
        GiExperimentModeState<GiCausticMode>.Disabled(GiCausticMode.Off),
        GiExperimentModeState<SimpleDdgiNearFieldResidualMode>.Disabled(
            SimpleDdgiNearFieldResidualMode.Off));
}
