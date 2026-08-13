using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

[Flags]
public enum GiCausticPathTag : uint
{
    None = 0,
    SpecularToDiffuse = 1u << 0,
    RefractiveToDiffuse = 1u << 1
}

public readonly record struct GiTaggedCausticCacheConfiguration(
    bool Enabled,
    int HeroMaterialCount,
    int PhotonTaskCapacity,
    int MaximumWorldCells,
    int MaximumPhotonsPerCell,
    ulong MemoryBudgetBytes,
    int RecordStride = GiCausticCacheLayout.ReferencePhotonStride,
    int CacheBankCount = 2,
    float TargetLoadFactor = 0.5f,
    int MaximumEmitterCount = 64,
    int MaximumHeroCount = 16,
    int MaximumProposalPairCount = 1_024,
    ulong MaximumStorageBufferRange = ulong.MaxValue,
    GiCausticScreenResolveProfile ScreenResolveProfile = default,
    float WorldCellSize = 0.5f,
    float DirectionalEmissionDiskRadius = 1_000.0f,
    float TargetingMixtureProbability = 0.75f);

public readonly record struct GiTaggedCausticCacheQualification(
    bool SeparateOwnershipImplemented,
    bool DiffuseTransportFeedDisabled,
    bool ReferenceParityPassed,
    bool StabilityProofPassed,
    bool QualityPerMillisecondImproved);

public readonly record struct GiTaggedCausticCachePlan(
    bool Requested,
    bool Active,
    int WorldCellCapacity,
    int PhotonTaskCapacity,
    int MaximumPhotonsPerCell,
    ulong AllocatedBytes,
    GiCausticCacheLayout Layout,
    string Status,
    GiExperimentAdmission Admission)
{
    public GiCausticGpuResourceLayout GpuLayout { get; init; } =
        GiCausticGpuResourceLayout.Empty("disabled");
    public SimpleDdgiAdvancedExperimentMemoryPlan Memory { get; init; } =
        SimpleDdgiAdvancedExperimentMemoryPlan.Empty;
    public GiCausticEvidenceValidation EvidenceValidation { get; init; } =
        GiCausticEvidenceValidation.Missing("disabled");
}

public static class GiTaggedCausticCacheExperiment
{
    public static GiTaggedCausticCachePlan CreatePlan(
        in GiTaggedCausticCacheConfiguration configuration,
        in GiTaggedCausticCacheQualification qualification) =>
        CreatePlanCore(configuration, qualification, default, default, null);

    /// <summary>
    /// Compiles C4 only from a complete two-generation GPU layout and an
    /// immutable qualification artifact bound to this exact device/content/
    /// source/TLAS/layout identity. The legacy boolean overload is retained
    /// for compatibility but intentionally cannot activate resources.
    /// </summary>
    public static GiTaggedCausticCachePlan CreatePlan(
        in GiTaggedCausticCacheConfiguration configuration,
        in GiTaggedCausticCacheQualification qualification,
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext admissionContext)
        => CreatePlanCore(
            configuration,
            qualification,
            evidence,
            admissionContext,
            null);

    /// <summary>
    /// Compiles a bounded C4 measurement candidate without claiming promotion
    /// evidence. Only explicit WorldCacheExperiment may consume this plan.
    /// </summary>
    public static GiTaggedCausticCachePlan CreateCandidatePlan(
        in GiTaggedCausticCacheConfiguration configuration,
        in GiCausticAdmissionContext admissionContext,
        in AdvancedGiCandidateAuthorization authorization) =>
        CreatePlanCore(
            configuration,
            new GiTaggedCausticCacheQualification(
                SeparateOwnershipImplemented: true,
                DiffuseTransportFeedDisabled: true,
                ReferenceParityPassed: false,
                StabilityProofPassed: false,
                QualityPerMillisecondImproved: false),
            default,
            admissionContext,
            authorization);

    private static GiTaggedCausticCachePlan CreatePlanCore(
        in GiTaggedCausticCacheConfiguration configuration,
        in GiTaggedCausticCacheQualification qualification,
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext admissionContext,
        AdvancedGiCandidateAuthorization? candidateAuthorization)
    {
        if (!configuration.Enabled)
        {
            return Empty(
                requested: false,
                GiExperimentAdmission.Disabled("C4"),
                "disabled",
                GiExperimentFallbackReason.None,
                GiCausticEvidenceValidation.Missing("disabled"));
        }
        if (configuration.HeroMaterialCount <= 0)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C4", "no-authored-hero-caustic-materials"),
                "no-authored-hero-caustic-materials",
                GiExperimentFallbackReason.PrerequisiteMissing,
                GiCausticEvidenceValidation.Missing(
                    "no-authored-hero-caustic-materials"));
        }
        if (!qualification.SeparateOwnershipImplemented ||
            !qualification.DiffuseTransportFeedDisabled)
        {
            string reason = !qualification.SeparateOwnershipImplemented
                ? "separate-caustic-ownership-required"
                : "diffuse-ddgi-feedback-forbidden";
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C4", reason, capabilitySupported: true),
                reason,
                GiExperimentFallbackReason.PrerequisiteMissing,
                GiCausticEvidenceValidation.Missing(reason));
        }

        int cells = configuration.MaximumWorldCells;
        int taskCapacity = configuration.PhotonTaskCapacity;
        int photonsPerCell = configuration.MaximumPhotonsPerCell;
        if (cells is < 1 or > 65_536 ||
            taskCapacity is < 1 or > 1_048_576 ||
            photonsPerCell is < 1 or > 256 ||
            configuration.HeroMaterialCount > configuration.MaximumHeroCount ||
            configuration.RecordStride != GiCausticGpuAbi.PhotonRecordBytes ||
            configuration.CacheBankCount !=
                GiCausticGpuResourceLayoutCompiler.RequiredCacheBankCount ||
            !float.IsFinite(configuration.TargetLoadFactor) ||
            configuration.TargetLoadFactor is <= 0.0f or > 0.5f ||
            !float.IsFinite(configuration.WorldCellSize) ||
            configuration.WorldCellSize <= 0.0f ||
            !float.IsFinite(configuration.DirectionalEmissionDiskRadius) ||
            configuration.DirectionalEmissionDiskRadius <= 0.0f ||
            !float.IsFinite(configuration.TargetingMixtureProbability) ||
            configuration.TargetingMixtureProbability is < 0.0f or > 0.95f ||
            !configuration.ScreenResolveProfile.TryValidate(out _))
        {
            const string reason = "invalid-bounded-caustic-cache-configuration";
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C4", reason, true),
                reason,
                GiExperimentFallbackReason.InvalidConfiguration,
                GiCausticEvidenceValidation.Missing(reason));
        }
        GiCausticCacheLayout layout = GiCausticCacheLayoutCompiler.Compile(
            taskCapacity,
            photonsPerCell,
            cells,
            configuration.RecordStride,
            writeBankCount:
                GiCausticGpuResourceLayoutCompiler.RequiredPhotonBankCount,
            cacheBankCount: configuration.CacheBankCount,
            targetLoadFactor: configuration.TargetLoadFactor,
            historyBytes: 0UL,
            budgetBytes: configuration.MemoryBudgetBytes);
        if (!layout.IsValid)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C4", layout.FailureReason, true),
                layout.FailureReason,
                MapC4LayoutFailure(layout.FailureReason),
                GiCausticEvidenceValidation.Missing(layout.FailureReason));
        }

        GiCausticGpuResourceLayout gpuLayout =
            GiCausticGpuResourceLayoutCompiler.Compile(
                new GiCausticGpuResourceLayoutRequest(
                    layout,
                    configuration.MemoryBudgetBytes,
                    configuration.MaximumStorageBufferRange,
                    configuration.MaximumEmitterCount,
                    configuration.MaximumHeroCount,
                    configuration.MaximumProposalPairCount,
                    configuration.ScreenResolveProfile));
        if (!gpuLayout.IsValid)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C4", gpuLayout.FailureReason, true),
                gpuLayout.FailureReason,
                MapC4LayoutFailure(gpuLayout.FailureReason),
                GiCausticEvidenceValidation.Missing(gpuLayout.FailureReason));
        }

        bool candidate = candidateAuthorization.HasValue;
        GiCausticEvidenceValidation evidenceValidation;
        if (candidate)
        {
            AdvancedGiCandidateAuthorization authorization =
                candidateAuthorization!.Value;
            bool contextValid = admissionContext.TryValidate(
                out string contextReason);
            if (!authorization.IsWellFormed || !contextValid ||
                !AdvancedGiCandidateAuthorization.HashEquals(
                    admissionContext.CorpusId,
                    authorization.ContentBinding.CorpusSha256))
            {
                string reason = !authorization.IsWellFormed
                    ? "caustic-candidate-authorization-invalid"
                    : !contextValid
                        ? contextReason
                        : "caustic-candidate-corpus-binding-mismatch";
                return Empty(
                    requested: true,
                    GiExperimentAdmission.Missing("C4", reason, true),
                    reason,
                    GiExperimentFallbackReason.EvidenceBindingMismatch,
                    GiCausticEvidenceValidation.Missing(reason));
            }
            GiCausticEvidenceBinding binding =
                GiCausticEvidenceBinding.Create(
                    admissionContext,
                    configuration,
                    gpuLayout);
            evidenceValidation = new GiCausticEvidenceValidation(
                Accepted: true,
                GiExperimentFallbackReason.None,
                "active-candidate-experiment",
                authorization.AuthorizationId,
                binding.Fingerprint,
                MaskedErrorReduction: 0.0);
        }
        else
        {
            evidenceValidation =
                GiCausticQualificationEvidenceEvaluator.ValidateForAdmission(
                    evidence,
                    admissionContext,
                    configuration,
                    gpuLayout);
        }
        if (!evidenceValidation.Accepted)
        {
            GiExperimentStage stage = evidenceValidation.FallbackReason ==
                GiExperimentFallbackReason.EvidenceMissing
                ? GiExperimentStage.PrerequisiteMissing
                : GiExperimentStage.QualificationFailed;
            return Empty(
                requested: true,
                new GiExperimentAdmission(
                    "C4", true, true, false, stage, 0UL,
                    evidenceValidation.Reason),
                evidenceValidation.Reason,
                evidenceValidation.FallbackReason,
                evidenceValidation);
        }

        GiCausticGpuMemoryRequirements requirements =
            gpuLayout.CreateMemoryRequirements(
                admitted: true,
                allocated: true);
        SimpleDdgiAdvancedExperimentMemoryPlan memory =
            SimpleDdgiAdvancedExperimentMemoryPlan.CreateCaustic(requirements);

        var admission = new GiExperimentAdmission(
            "C4",
            true,
            true,
            true,
            GiExperimentStage.Active,
            memory.AllocatedBytes,
            candidate
                ? "active-candidate-experiment"
                : "active-qualified-experiment");
        return new GiTaggedCausticCachePlan(
            true,
            true,
            cells,
            taskCapacity,
            photonsPerCell,
            memory.AllocatedBytes,
            layout,
            admission.Status,
            admission)
        {
            GpuLayout = gpuLayout,
            Memory = memory,
            EvidenceValidation = evidenceValidation
        };
    }

    public static Vector3 CompositeTaggedContribution(
        Vector3 diffuseBaseline,
        Vector3 taggedCausticRadiance,
        GiCausticPathTag tag)
    {
        if (!IsFinite(diffuseBaseline) || !IsFinite(taggedCausticRadiance))
            throw new ArgumentOutOfRangeException(nameof(taggedCausticRadiance));
        if ((tag & (GiCausticPathTag.SpecularToDiffuse |
                    GiCausticPathTag.RefractiveToDiffuse)) == 0)
            return Vector3.Max(diffuseBaseline, Vector3.Zero);

        Vector3 baseline = Vector3.Max(diffuseBaseline, Vector3.Zero);
        Vector3 caustic = Vector3.Max(taggedCausticRadiance, Vector3.Zero);
        // Photon flux/PDF/path throughput and the normalized receiver kernel
        // own energy. A valid bright caustic on a dark receiver must not be
        // clamped relative to the unrelated diffuse baseline.
        return baseline + caustic;
    }

    private static GiTaggedCausticCachePlan Empty(
        bool requested,
        GiExperimentAdmission admission,
        string status,
        GiExperimentFallbackReason fallbackReason,
        in GiCausticEvidenceValidation evidenceValidation) => new(
            requested,
            false,
            0,
            0,
            0,
            0UL,
            GiCausticCacheLayout.Empty(status),
            status,
            admission)
        {
            GpuLayout = GiCausticGpuResourceLayout.Empty(status),
            Memory = SimpleDdgiAdvancedExperimentMemoryPlan
                .CreateCausticRejected(fallbackReason),
            EvidenceValidation = evidenceValidation
        };

    private static GiExperimentFallbackReason MapC4LayoutFailure(
        string reason) => reason switch
        {
            "independent-caustic-memory-budget" or
            "caustic-gpu-independent-memory-budget-exceeded" =>
                GiExperimentFallbackReason.IndependentMemoryBudgetExceeded,
            "caustic-cache-layout-overflow" or
            "caustic-deterministic-build-scratch-overflow" or
            "caustic-gpu-layout-overflow" =>
                GiExperimentFallbackReason.ArithmeticOverflow,
            "caustic-gpu-storage-buffer-range-exceeded" =>
                GiExperimentFallbackReason.VulkanLimitExceeded,
            _ => GiExperimentFallbackReason.InvalidConfiguration
        };

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public readonly record struct SimpleDdgiNearFieldResidualPrerequisites(
    bool RefinementBricksActive,
    bool RefinementQualityGatePassed,
    bool RemainingContactScaleErrorMeasured,
    bool SourceOwnershipImplemented,
    bool DisocclusionRejectionImplemented,
    bool CameraAndScreenEdgeStabilityPassed,
    bool ReferenceErrorPerMillisecondImproved,
    bool NoDoubleCountingOrFalseDarkening);

public readonly record struct SimpleDdgiNearFieldResidualConfiguration(
    bool Enabled,
    int Width,
    int Height,
    ulong MemoryBudgetBytes,
    SimpleDdgiNearFieldResidualProfile Profile,
    SimpleDdgiNearFieldTraceSourceContract SourceContract);

public readonly record struct SimpleDdgiNearFieldResidualPlan(
    bool Requested,
    bool Active,
    int Width,
    int Height,
    ulong TraceBytes,
    ulong HistoryBytes,
    ulong AllocatedBytes,
    SimpleDdgiNearFieldResidualLayout Layout,
    string Status,
    GiExperimentAdmission Admission)
{
    /// <summary>
    /// Fixed-shape central ownership plan.  A rejected C5 request has exact
    /// zeroes in all three C5 categories, never deferred/lazy byte estimates.
    /// </summary>
    public SimpleDdgiAdvancedExperimentMemoryPlan Memory { get; init; } =
        SimpleDdgiAdvancedExperimentMemoryPlan.Empty;

    /// <summary>Exact transient filter ping-pong allocation for this plan.</summary>
    public ulong FilterScratchBytes => Memory.NearFieldFilterScratch.AllocatedBytes;

    /// <summary>
    /// Immutable evidence result used for this admission.  It records why an
    /// otherwise valid set of boolean implementation prerequisites did not
    /// become an active C5 allocation.
    /// </summary>
    public SimpleDdgiNearFieldResidualEvidenceValidation EvidenceValidation { get; init; }

    public string EvidenceId => EvidenceValidation.EvidenceId;

    public ulong EvidenceBindingFingerprint => EvidenceValidation.BindingFingerprint;
}

public readonly record struct SimpleDdgiNearFieldResidualValidation(
    float DepthConfidence,
    float NormalConfidence,
    float MotionConfidence,
    float MaterialRevisionConfidence,
    float ScreenEdgeConfidence)
{
    public float CombinedConfidence => Math.Clamp(
        Math.Min(
            Math.Min(DepthConfidence, NormalConfidence),
            Math.Min(
                MotionConfidence,
                Math.Min(MaterialRevisionConfidence, ScreenEdgeConfidence))),
        0.0f,
        1.0f);
}

public static class SimpleDdgiNearFieldResidualExperiment
{
    /// <summary>
    /// Converts a formerly active plan into the exact zero-byte fallback after
    /// a runtime identity transition (for example, a resize invalidating the
    /// measured extent). Requested intent and evidence identity remain
    /// diagnostic, but no stale layout can remain admitted.
    /// </summary>
    public static SimpleDdgiNearFieldResidualPlan InvalidateRuntimePlan(
        in SimpleDdgiNearFieldResidualPlan plan,
        string reason,
        GiExperimentFallbackReason fallbackReason =
            GiExperimentFallbackReason.ResourceIncomplete)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "near-field-runtime-plan-invalidated"
            : reason;
        SimpleDdgiNearFieldResidualEvidenceValidation validation =
            plan.EvidenceValidation with
            {
                Accepted = false,
                FallbackReason = fallbackReason,
                Reason = normalizedReason,
                MeasurementDecision =
                    SimpleDdgiNearFieldResidualDecision.No(normalizedReason)
            };
        return Empty(
            plan.Requested,
            new GiExperimentAdmission(
                "C5",
                plan.Requested,
                true,
                false,
                GiExperimentStage.PrerequisiteMissing,
                0UL,
                normalizedReason),
            normalizedReason,
            fallbackReason,
            validation);
    }

    /// <summary>
    /// Compatibility overload for callers that only have legacy prerequisite
    /// booleans.  It is intentionally fail-closed once those prerequisites are
    /// met: boolean state alone cannot allocate C5 resources.
    /// </summary>
    public static SimpleDdgiNearFieldResidualPlan CreatePlan(
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualPrerequisites prerequisites) =>
        CreatePlanCore(configuration, prerequisites, default, default, null);

    /// <summary>
    /// Creates an admitted C5 resource plan only after technical prerequisites,
    /// a complete layout, and current immutable post-B3 evidence all agree.
    /// </summary>
    public static SimpleDdgiNearFieldResidualPlan CreatePlan(
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualPrerequisites prerequisites,
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualAdmissionContext admissionContext)
        => CreatePlanCore(
            configuration,
            prerequisites,
            evidence,
            admissionContext,
            null);

    /// <summary>
    /// Compiles a bounded C5 measurement candidate after implementation and
    /// B3 ownership prerequisites, without treating candidate authorization as
    /// quality evidence. AutoQualified cannot consume this result.
    /// </summary>
    public static SimpleDdgiNearFieldResidualPlan CreateCandidatePlan(
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualPrerequisites prerequisites,
        in SimpleDdgiNearFieldResidualAdmissionContext admissionContext,
        in AdvancedGiCandidateAuthorization authorization) =>
        CreatePlanCore(
            configuration,
            prerequisites,
            default,
            admissionContext,
            authorization);

    private static SimpleDdgiNearFieldResidualPlan CreatePlanCore(
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualPrerequisites prerequisites,
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualAdmissionContext admissionContext,
        AdvancedGiCandidateAuthorization? candidateAuthorization)
    {
        bool candidate = candidateAuthorization.HasValue;
        if (!configuration.Enabled)
        {
            return Empty(
                requested: false,
                GiExperimentAdmission.Disabled("C5"),
                "disabled",
                GiExperimentFallbackReason.None,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing("disabled"));
        }
        if (!prerequisites.RefinementBricksActive ||
            !prerequisites.RefinementQualityGatePassed)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", "B3-refinement-qualification-required"),
                "B3-refinement-qualification-required",
                GiExperimentFallbackReason.PrerequisiteMissing,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    "B3-refinement-qualification-required"));
        }
        if (!candidate && !prerequisites.RemainingContactScaleErrorMeasured)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", "no-material-post-B3-residual-error", true),
                "no-material-post-B3-residual-error",
                GiExperimentFallbackReason.PrerequisiteMissing,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    "no-material-post-B3-residual-error"));
        }
        if (!configuration.SourceContract.IsValid)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", configuration.SourceContract.FailureReason, true),
                configuration.SourceContract.FailureReason,
                GiExperimentFallbackReason.InvalidConfiguration,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    configuration.SourceContract.FailureReason));
        }
        if (!prerequisites.SourceOwnershipImplemented)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", "DDGI-free-trace-source-ownership-required", true),
                "DDGI-free-trace-source-ownership-required",
                GiExperimentFallbackReason.PrerequisiteMissing,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    "DDGI-free-trace-source-ownership-required"));
        }
        if (!prerequisites.DisocclusionRejectionImplemented)
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", "depth-normal-motion-material-rejection-required", true),
                "depth-normal-motion-material-rejection-required",
                GiExperimentFallbackReason.PrerequisiteMissing,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    "depth-normal-motion-material-rejection-required"));
        }
        bool qualityQualified =
            prerequisites.CameraAndScreenEdgeStabilityPassed &&
            prerequisites.ReferenceErrorPerMillisecondImproved &&
            prerequisites.NoDoubleCountingOrFalseDarkening;
        if (!candidate && !qualityQualified)
        {
            string reason = !prerequisites.CameraAndScreenEdgeStabilityPassed
                ? "camera-or-screen-edge-stability-failed"
                : !prerequisites.ReferenceErrorPerMillisecondImproved
                    ? "reference-error-per-millisecond-win-not-demonstrated"
                    : "double-counting-or-false-darkening-gate-failed";
            return Empty(
                requested: true,
                new GiExperimentAdmission(
                    "C5",
                    true,
                    true,
                    false,
                    GiExperimentStage.QualificationFailed,
                    0UL,
                    reason),
                reason,
                GiExperimentFallbackReason.QualificationNotPassed,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(reason));
        }

        if (configuration.Width is < 1 or > 16_384 ||
            configuration.Height is < 1 or > 16_384)
        {
            const string reason = "near-field-source-dimensions-out-of-range";
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing("C5", reason, true),
                reason,
                GiExperimentFallbackReason.InvalidConfiguration,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(reason));
        }

        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                configuration.Width,
                configuration.Height,
                configuration.Profile,
                configuration.MemoryBudgetBytes);
        if (!layout.IsValid)
        {
            GiExperimentFallbackReason layoutFailure = MapLayoutFailure(layout.FailureReason);
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing(
                    "C5", layout.FailureReason, true),
                layout.FailureReason,
                layoutFailure,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    layout.FailureReason));
        }
        if (!configuration.SourceContract.TryValidateForLayout(
                layout,
                out string sourceContractFailure))
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing("C5", sourceContractFailure, true),
                sourceContractFailure,
                GiExperimentFallbackReason.InvalidConfiguration,
                SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                    sourceContractFailure));
        }

        SimpleDdgiNearFieldResidualEvidenceValidation evidenceValidation;
        if (candidate)
        {
            AdvancedGiCandidateAuthorization authorization =
                candidateAuthorization!.Value;
            bool contextValid = admissionContext.TryValidate(
                out string contextReason);
            if (!authorization.IsWellFormed || !contextValid ||
                !AdvancedGiCandidateAuthorization.HashEquals(
                    admissionContext.CorpusId,
                    authorization.ContentBinding.CorpusSha256))
            {
                string reason = !authorization.IsWellFormed
                    ? "near-field-candidate-authorization-invalid"
                    : !contextValid
                        ? contextReason
                        : "near-field-candidate-corpus-binding-mismatch";
                return Empty(
                    requested: true,
                    GiExperimentAdmission.Missing("C5", reason, true),
                    reason,
                    GiExperimentFallbackReason.EvidenceBindingMismatch,
                    SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                        reason));
            }
            SimpleDdgiNearFieldResidualEvidenceBinding binding =
                SimpleDdgiNearFieldResidualEvidenceBinding.Create(
                    admissionContext,
                    configuration,
                    layout);
            evidenceValidation = new(
                Accepted: true,
                GiExperimentFallbackReason.None,
                "active-candidate-experiment",
                authorization.AuthorizationId,
                binding.Fingerprint,
                SimpleDdgiNearFieldResidualDecision.No(
                    "candidate-measurement-pending"));
        }
        else
        {
            evidenceValidation =
                SimpleDdgiNearFieldResidualEvidenceEvaluator
                    .ValidateForAdmission(
                        evidence,
                        admissionContext,
                        configuration,
                        layout);
        }
        if (!evidenceValidation.Accepted)
        {
            return Empty(
                requested: true,
                CreateEvidenceRejectedAdmission(evidenceValidation),
                evidenceValidation.Reason,
                evidenceValidation.FallbackReason,
                evidenceValidation);
        }

        if (!SimpleDdgiAdvancedExperimentMemoryPlan.TryCreateNearFieldResidual(
                layout,
                out SimpleDdgiAdvancedExperimentMemoryPlan memory,
                out GiExperimentFallbackReason memoryFailure,
                out string memoryFailureDetail))
        {
            return Empty(
                requested: true,
                GiExperimentAdmission.Missing("C5", memoryFailureDetail, true),
                memoryFailureDetail,
                memoryFailure,
                evidenceValidation);
        }

        var admission = new GiExperimentAdmission(
            "C5",
            true,
            true,
            true,
            GiExperimentStage.Active,
            memory.AllocatedBytes,
            candidate
                ? "active-candidate-experiment"
                : "active-qualified-experiment");
        return new SimpleDdgiNearFieldResidualPlan(
            true,
            true,
            layout.TraceWidth,
            layout.TraceHeight,
            memory.NearFieldTraceTargets.AllocatedBytes,
            memory.NearFieldHistoryAndMoments.AllocatedBytes,
            memory.AllocatedBytes,
            layout,
            admission.Status,
            admission)
        {
            Memory = memory,
            EvidenceValidation = evidenceValidation
        };
    }

    public static Vector3 EvaluateHighFrequencyResidual(
        Vector3 directDiffuseAndEmissive,
        Vector3 lowFrequencyEstimate,
        in SimpleDdgiNearFieldResidualValidation validation)
    {
        if (!IsFinite(directDiffuseAndEmissive) ||
            !IsFinite(lowFrequencyEstimate))
            throw new ArgumentOutOfRangeException(nameof(directDiffuseAndEmissive));
        return SimpleDdgiNearFieldResidualReference.EvaluateBandResidual(
            directDiffuseAndEmissive,
            lowFrequencyEstimate,
            validation.CombinedConfidence,
            nearEstimateValid: true,
            lowEstimateValid: validation.CombinedConfidence > 0.0f);
    }

    private static SimpleDdgiNearFieldResidualPlan Empty(
        bool requested,
        GiExperimentAdmission admission,
        string status,
        GiExperimentFallbackReason fallbackReason,
        in SimpleDdgiNearFieldResidualEvidenceValidation evidenceValidation) =>
        new(
            requested,
            false,
            0,
            0,
            0UL,
            0UL,
            0UL,
            SimpleDdgiNearFieldResidualLayout.Empty(status),
            status,
            admission)
        {
            Memory = SimpleDdgiAdvancedExperimentMemoryPlan
                .CreateNearFieldResidualRejected(fallbackReason),
            EvidenceValidation = evidenceValidation
        };

    private static GiExperimentAdmission CreateEvidenceRejectedAdmission(
        in SimpleDdgiNearFieldResidualEvidenceValidation validation)
    {
        GiExperimentStage stage = validation.FallbackReason is
            GiExperimentFallbackReason.EvidenceMissing or
            GiExperimentFallbackReason.PrerequisiteMissing
            ? GiExperimentStage.PrerequisiteMissing
            : GiExperimentStage.QualificationFailed;
        return new GiExperimentAdmission(
            "C5",
            true,
            true,
            false,
            stage,
            0UL,
            validation.Reason);
    }

    private static GiExperimentFallbackReason MapLayoutFailure(string failure) =>
        failure switch
        {
            "independent-near-field-memory-budget" =>
                GiExperimentFallbackReason.IndependentMemoryBudgetExceeded,
            "near-field-layout-overflow" => GiExperimentFallbackReason.ArithmeticOverflow,
            _ => GiExperimentFallbackReason.InvalidConfiguration
        };

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
