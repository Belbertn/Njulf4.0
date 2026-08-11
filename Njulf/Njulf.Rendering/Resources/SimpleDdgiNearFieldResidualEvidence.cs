using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Versioned policy for the C5 measure-before-build artifact.  This is a
/// qualification contract, not a user preference: the evidence has to be
/// re-issued whenever one of the bound inputs changes.
/// </summary>
public static class SimpleDdgiNearFieldResidualEvidenceAbi
{
    // V4 additionally budgets the asynchronous per-frame telemetry readback
    // ring. Prior artifacts did not include that live allocation and cannot
    // be compared against the same independent memory envelope.
    public const uint Version = 0x4335_0104u;

    // These values deliberately live in the evidence ABI rather than in a
    // preset.  Changing a promotion floor invalidates prior evidence.
    public const uint MinimumReferenceSequenceCount = 3u;
    public const uint MinimumReferenceFrameCount = 120u;
    public const uint MinimumIndependentRunCount = 3u;
    public const double MaximumEqualCostRelativeDifference = 0.05;
}

/// <summary>
/// Current runtime identity used to validate an archived C5 evidence artifact.
/// Device identity includes the driver/toolchain qualification key; a marketing
/// GPU name alone is intentionally not sufficient for promotion.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualAdmissionContext(
    string DeviceQualificationKey,
    string CorpusId,
    ulong ContentRevision,
    string B3QualificationId,
    uint B3QualificationRevision,
    uint NearFieldResidualAbiRevision = SimpleDdgiNearFieldResidualGpuAbi.Version)
{
    public bool IsValid => TryValidate(out _);

    public bool TryValidate(out string failure)
    {
        if (!IsStableKey(DeviceQualificationKey, 256))
        {
            failure = "near-field-device-qualification-key-required";
            return false;
        }
        if (!IsStableKey(CorpusId, 256))
        {
            failure = "near-field-corpus-id-required";
            return false;
        }
        if (ContentRevision == 0UL)
        {
            failure = "near-field-content-revision-required";
            return false;
        }
        if (!IsStableKey(B3QualificationId, 256) || B3QualificationRevision == 0u)
        {
            failure = "near-field-B3-qualification-identity-required";
            return false;
        }
        if (NearFieldResidualAbiRevision != SimpleDdgiNearFieldResidualGpuAbi.Version)
        {
            failure = "near-field-residual-ABI-revision-mismatch";
            return false;
        }

        failure = "valid";
        return true;
    }

    internal static bool IsStableKey(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value!.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !ContainsControlCharacter(value);

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Immutable identity recorded next to a C5 result.  Direct field comparison
/// is authoritative; fingerprints make stale evidence easy to diagnose and
/// persist, but are never the only validation mechanism.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualEvidenceBinding(
    string DeviceQualificationKey,
    string CorpusId,
    ulong ContentRevision,
    string B3QualificationId,
    uint B3QualificationRevision,
    uint NearFieldResidualAbiRevision,
    uint EvidenceAbiRevision,
    SimpleDdgiNearFieldTraceSourceContract TraceSourceContract,
    ulong ProfileFingerprint,
    ulong LayoutFingerprint,
    int SourceWidth,
    int SourceHeight,
    int TraceWidth,
    int TraceHeight,
    ulong LayoutTotalBytes)
{
    public ulong Fingerprint =>
        SimpleDdgiNearFieldResidualEvidenceEvaluator.ComputeBindingFingerprint(this);

    public bool IsValid => TryValidate(out _);

    public bool TryValidate(out string failure)
    {
        var context = new SimpleDdgiNearFieldResidualAdmissionContext(
            DeviceQualificationKey,
            CorpusId,
            ContentRevision,
            B3QualificationId,
            B3QualificationRevision,
            NearFieldResidualAbiRevision);
        if (!context.TryValidate(out failure))
            return false;
        if (EvidenceAbiRevision != SimpleDdgiNearFieldResidualEvidenceAbi.Version)
        {
            failure = "near-field-evidence-ABI-revision-mismatch";
            return false;
        }
        if (!TraceSourceContract.TryValidate(out _))
        {
            failure = "near-field-evidence-trace-source-binding-invalid";
            return false;
        }
        if (TraceSourceContract.Extent.FullWidth != SourceWidth ||
            TraceSourceContract.Extent.FullHeight != SourceHeight ||
            TraceSourceContract.Extent.ScaledWidth != TraceWidth ||
            TraceSourceContract.Extent.ScaledHeight != TraceHeight)
        {
            failure = "near-field-evidence-trace-source-extent-binding-invalid";
            return false;
        }
        if (ProfileFingerprint == 0UL || LayoutFingerprint == 0UL ||
            SourceWidth <= 0 || SourceHeight <= 0 ||
            TraceWidth <= 0 || TraceHeight <= 0 || LayoutTotalBytes == 0UL)
        {
            failure = "near-field-evidence-profile-or-layout-binding-invalid";
            return false;
        }

        failure = "valid";
        return true;
    }

    public static SimpleDdgiNearFieldResidualEvidenceBinding Create(
        in SimpleDdgiNearFieldResidualAdmissionContext context,
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualLayout layout) => new(
            context.DeviceQualificationKey,
            context.CorpusId,
            context.ContentRevision,
            context.B3QualificationId,
            context.B3QualificationRevision,
            context.NearFieldResidualAbiRevision,
            SimpleDdgiNearFieldResidualEvidenceAbi.Version,
            configuration.SourceContract,
            SimpleDdgiNearFieldResidualEvidenceEvaluator.ComputeProfileFingerprint(
                configuration.Profile),
            SimpleDdgiNearFieldResidualEvidenceEvaluator.ComputeLayoutFingerprint(
                layout),
            layout.SourceWidth,
            layout.SourceHeight,
            layout.TraceWidth,
            layout.TraceHeight,
            layout.TotalBytes);
}

/// <summary>
/// Deterministic, scene-linear result from the mandatory post-B3 measurement.
/// This is deliberately data, not a boolean preference: a C5 request cannot
/// turn into GPU resources until an equal-cost comparison names a material
/// opportunity.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualMeasurement(
    string CorpusId,
    ulong ContentRevision,
    uint B3QualificationRevision,
    double PostB3NearFieldError,
    double C5OracleError,
    double EqualCostAdditionalB3Error,
    bool ErrorIsScreenLocal,
    bool ErrorIsObservableByShortDepthRay,
    bool RootCauseIsNotDdgiLivenessOrAlpha,
    bool UsesSceneLinearReference);

/// <summary>
/// Complete archived C5 qualification result.  It is intentionally immutable
/// and carries the exact source/profile/layout/device/content/B3 identity that
/// was measured.  A stale result is rejected instead of being "close enough"
/// for a new device, resize, source ABI, or B3 publication.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualQualificationEvidence(
    string EvidenceId,
    SimpleDdgiNearFieldResidualEvidenceBinding Binding,
    SimpleDdgiNearFieldResidualMeasurement Measurement,
    uint ReferenceSequenceCount,
    uint ReferenceFrameCount,
    uint IndependentRunCount,
    double C5AddedMilliseconds,
    double EqualCostAdditionalB3Milliseconds,
    bool B3ConvergenceVerified,
    bool CpuOrImageSpaceOracleVerified,
    bool TraceSourceIndependenceVerified,
    bool TemporalStabilityVerified,
    bool SignedResidualEnergyVerified,
    bool WholeFrameRegressionVerified)
{
    public bool HasEvidenceId =>
        SimpleDdgiNearFieldResidualAdmissionContext.IsStableKey(EvidenceId, 256);
}

public readonly record struct SimpleDdgiNearFieldResidualDecision(
    bool Proceed,
    string Reason,
    double C5ErrorReductionFraction,
    double B3ErrorReductionFraction)
{
    public static SimpleDdgiNearFieldResidualDecision No(string reason) =>
        new(false, reason, 0.0, 0.0);
}

/// <summary>
/// Admission-ready validation result.  The reason is stable machine-readable
/// text suitable for a capture/diagnostic; callers should use the enum for
/// fallback policy.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualEvidenceValidation(
    bool Accepted,
    GiExperimentFallbackReason FallbackReason,
    string Reason,
    string EvidenceId,
    ulong BindingFingerprint,
    SimpleDdgiNearFieldResidualDecision MeasurementDecision)
{
    public static SimpleDdgiNearFieldResidualEvidenceValidation Missing(
        string reason) => new(
            false,
            GiExperimentFallbackReason.EvidenceMissing,
            reason,
            string.Empty,
            0UL,
            SimpleDdgiNearFieldResidualDecision.No(reason));
}

public static class SimpleDdgiNearFieldResidualEvidenceEvaluator
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Enforces the plan's measure-before-build rule. The 20% threshold is
    /// intentionally a baseline promotion floor, while C5 must additionally
    /// beat spending the same time/memory on B3.
    /// </summary>
    public static SimpleDdgiNearFieldResidualDecision Evaluate(
        in SimpleDdgiNearFieldResidualMeasurement measurement,
        double minimumRequiredReduction = 0.20)
    {
        if (string.IsNullOrWhiteSpace(measurement.CorpusId) ||
            measurement.ContentRevision == 0 || measurement.B3QualificationRevision == 0 ||
            !double.IsFinite(measurement.PostB3NearFieldError) ||
            !double.IsFinite(measurement.C5OracleError) ||
            !double.IsFinite(measurement.EqualCostAdditionalB3Error) ||
            measurement.PostB3NearFieldError <= 0.0 || measurement.C5OracleError < 0.0 ||
            measurement.EqualCostAdditionalB3Error < 0.0 ||
            !double.IsFinite(minimumRequiredReduction) || minimumRequiredReduction <= 0.0 ||
            minimumRequiredReduction >= 1.0)
        {
            return SimpleDdgiNearFieldResidualDecision.No("invalid-post-B3-measurement");
        }
        if (!measurement.UsesSceneLinearReference)
            return SimpleDdgiNearFieldResidualDecision.No("scene-linear-reference-required");
        if (!measurement.ErrorIsScreenLocal || !measurement.ErrorIsObservableByShortDepthRay)
            return SimpleDdgiNearFieldResidualDecision.No("residual-is-not-observable-screen-local-detail");
        if (!measurement.RootCauseIsNotDdgiLivenessOrAlpha)
        {
            return SimpleDdgiNearFieldResidualDecision.No(
                "resolve-ddgi-liveness-alpha-or-source-root-cause-before-C5");
        }

        double c5Reduction = 1.0 - measurement.C5OracleError / measurement.PostB3NearFieldError;
        double b3Reduction = 1.0 - measurement.EqualCostAdditionalB3Error /
            measurement.PostB3NearFieldError;
        if (c5Reduction < minimumRequiredReduction)
        {
            return new SimpleDdgiNearFieldResidualDecision(
                false,
                "c5-post-B3-error-reduction-below-promotion-floor",
                c5Reduction,
                b3Reduction);
        }
        if (c5Reduction <= b3Reduction)
        {
            return new SimpleDdgiNearFieldResidualDecision(
                false,
                "equal-cost-B3-is-at-least-as-effective",
                c5Reduction,
                b3Reduction);
        }

        return new SimpleDdgiNearFieldResidualDecision(
            true,
            "material-post-B3-screen-local-opportunity",
            c5Reduction,
            b3Reduction);
    }

    /// <summary>
    /// Validates a qualification artifact against the exact admission inputs.
    /// This deliberately does not accept a bare collection of prerequisite
    /// booleans: evidence must be both materially positive and current.
    /// </summary>
    public static SimpleDdgiNearFieldResidualEvidenceValidation ValidateForAdmission(
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualAdmissionContext context,
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualLayout layout,
        double minimumRequiredReduction = 0.20)
    {
        if (!context.TryValidate(out string contextFailure))
            return SimpleDdgiNearFieldResidualEvidenceValidation.Missing(contextFailure);
        if (!evidence.HasEvidenceId)
            return SimpleDdgiNearFieldResidualEvidenceValidation.Missing(
                "near-field-qualification-evidence-id-required");
        if (!evidence.Binding.TryValidate(out string bindingFailure))
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceInvalid,
                bindingFailure,
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(bindingFailure));
        }
        if (!configuration.SourceContract.TryValidateForLayout(
                layout,
                out string sourceContractFailure))
        {
            return Reject(
                GiExperimentFallbackReason.InvalidConfiguration,
                sourceContractFailure,
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    sourceContractFailure));
        }
        if (!BindingMatchesContext(evidence.Binding, context))
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceBindingMismatch,
                "near-field-evidence-device-content-or-B3-binding-mismatch",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-device-content-or-B3-binding-mismatch"));
        }
        if (!BindingMatchesConfiguration(evidence.Binding, configuration, layout))
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceBindingMismatch,
                "near-field-evidence-source-profile-or-layout-binding-mismatch",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-source-profile-or-layout-binding-mismatch"));
        }
        if (!MeasurementMatchesBinding(evidence.Measurement, evidence.Binding))
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceInvalid,
                "near-field-evidence-measurement-binding-mismatch",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-measurement-binding-mismatch"));
        }
        if (evidence.ReferenceSequenceCount <
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumReferenceSequenceCount ||
            evidence.ReferenceFrameCount <
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumReferenceFrameCount ||
            evidence.IndependentRunCount <
                SimpleDdgiNearFieldResidualEvidenceAbi.MinimumIndependentRunCount)
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceInvalid,
                "near-field-evidence-insufficient-reference-sequences-or-runs",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-insufficient-reference-sequences-or-runs"));
        }
        if (!HasEqualCostComparison(evidence.C5AddedMilliseconds,
                evidence.EqualCostAdditionalB3Milliseconds))
        {
            return Reject(
                GiExperimentFallbackReason.EvidenceInvalid,
                "near-field-evidence-equal-cost-comparison-invalid",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-equal-cost-comparison-invalid"));
        }
        if (!evidence.B3ConvergenceVerified ||
            !evidence.CpuOrImageSpaceOracleVerified ||
            !evidence.TraceSourceIndependenceVerified ||
            !evidence.TemporalStabilityVerified ||
            !evidence.SignedResidualEnergyVerified ||
            !evidence.WholeFrameRegressionVerified)
        {
            return Reject(
                GiExperimentFallbackReason.QualificationNotPassed,
                "near-field-evidence-required-quality-witness-missing",
                evidence,
                SimpleDdgiNearFieldResidualDecision.No(
                    "near-field-evidence-required-quality-witness-missing"));
        }

        SimpleDdgiNearFieldResidualDecision decision = Evaluate(
            evidence.Measurement, minimumRequiredReduction);
        if (!decision.Proceed)
        {
            return Reject(
                GiExperimentFallbackReason.QualificationNotPassed,
                decision.Reason,
                evidence,
                decision);
        }

        return new SimpleDdgiNearFieldResidualEvidenceValidation(
            true,
            GiExperimentFallbackReason.None,
            decision.Reason,
            evidence.EvidenceId,
            evidence.Binding.Fingerprint,
            decision);
    }

    public static ulong ComputeProfileFingerprint(
        in SimpleDdgiNearFieldResidualProfile profile)
    {
        ulong hash = FnvOffsetBasis;
        hash = Add(hash, (ulong)(uint)SimpleDdgiNearFieldResidualEvidenceAbi.Version);
        hash = Add(hash, (ulong)(uint)profile.SourceFormat);
        hash = Add(hash, BitConverter.SingleToUInt32Bits(profile.ResolutionScale));
        hash = Add(hash, (ulong)(uint)profile.MaximumTraceSteps);
        hash = Add(hash, (ulong)(uint)profile.MaximumMipVisits);
        hash = Add(hash, (ulong)(uint)profile.BinaryRefinementSteps);
        hash = Add(hash, (ulong)(uint)profile.FilterIterationCount);
        hash = Add(hash, profile.ImageRowAlignment);
        return Add(hash, profile.ImageAllocationGranularity);
    }

    public static ulong ComputeLayoutFingerprint(
        in SimpleDdgiNearFieldResidualLayout layout)
    {
        ulong hash = FnvOffsetBasis;
        hash = Add(hash, (ulong)(uint)SimpleDdgiNearFieldResidualEvidenceAbi.Version);
        hash = Add(hash, (ulong)(uint)layout.SourceWidth);
        hash = Add(hash, (ulong)(uint)layout.SourceHeight);
        hash = Add(hash, (ulong)(uint)layout.SourceFormat);
        hash = Add(hash, BitConverter.SingleToUInt32Bits(layout.TraceResolutionScale));
        hash = Add(hash, (ulong)(uint)layout.TraceWidth);
        hash = Add(hash, (ulong)(uint)layout.TraceHeight);
        hash = Add(hash, (ulong)(uint)layout.FilterIterationCount);
        hash = Add(hash, layout.TraceSourceBytes);
        hash = Add(hash, layout.ReceiverPayloadBytes);
        hash = Add(hash, layout.TraceFrameConstantsBytes);
        hash = Add(hash, layout.RawCandidateBytes);
        hash = Add(hash, layout.HitMetadataBytes);
        hash = Add(hash, layout.HistoryRadianceBytes);
        hash = Add(hash, layout.MomentBytes);
        hash = Add(hash, layout.HistoryValidityBytes);
        hash = Add(hash, layout.HistoryMetadataBytes);
        hash = Add(hash, layout.HistoryNormalBytes);
        hash = Add(hash, layout.FilterScratchBytes);
        hash = Add(hash, layout.TileBuffersBytes);
        hash = Add(hash, layout.TelemetryReadbackBytes);
        hash = Add(hash, layout.TotalBytes);
        return Add(hash, layout.IsValid ? 1UL : 0UL);
    }

    public static ulong ComputeBindingFingerprint(
        in SimpleDdgiNearFieldResidualEvidenceBinding binding)
    {
        ulong hash = FnvOffsetBasis;
        hash = Add(hash, (ulong)(uint)SimpleDdgiNearFieldResidualEvidenceAbi.Version);
        hash = AddString(hash, binding.DeviceQualificationKey);
        hash = AddString(hash, binding.CorpusId);
        hash = Add(hash, binding.ContentRevision);
        hash = AddString(hash, binding.B3QualificationId);
        hash = Add(hash, binding.B3QualificationRevision);
        hash = Add(hash, binding.NearFieldResidualAbiRevision);
        hash = Add(hash, binding.EvidenceAbiRevision);
        hash = AddTraceSourceContract(hash, binding.TraceSourceContract);
        hash = Add(hash, binding.ProfileFingerprint);
        hash = Add(hash, binding.LayoutFingerprint);
        hash = Add(hash, (ulong)(uint)binding.SourceWidth);
        hash = Add(hash, (ulong)(uint)binding.SourceHeight);
        hash = Add(hash, (ulong)(uint)binding.TraceWidth);
        hash = Add(hash, (ulong)(uint)binding.TraceHeight);
        return Add(hash, binding.LayoutTotalBytes);
    }

    private static bool BindingMatchesContext(
        in SimpleDdgiNearFieldResidualEvidenceBinding binding,
        in SimpleDdgiNearFieldResidualAdmissionContext context) =>
        string.Equals(binding.DeviceQualificationKey, context.DeviceQualificationKey,
            StringComparison.Ordinal) &&
        string.Equals(binding.CorpusId, context.CorpusId, StringComparison.Ordinal) &&
        binding.ContentRevision == context.ContentRevision &&
        string.Equals(binding.B3QualificationId, context.B3QualificationId,
            StringComparison.Ordinal) &&
        binding.B3QualificationRevision == context.B3QualificationRevision &&
        binding.NearFieldResidualAbiRevision == context.NearFieldResidualAbiRevision;

    private static bool BindingMatchesConfiguration(
        in SimpleDdgiNearFieldResidualEvidenceBinding binding,
        in SimpleDdgiNearFieldResidualConfiguration configuration,
        in SimpleDdgiNearFieldResidualLayout layout) =>
        binding.TraceSourceContract == configuration.SourceContract &&
        binding.TraceSourceContract.TryValidateForLayout(layout, out _) &&
        binding.ProfileFingerprint == ComputeProfileFingerprint(configuration.Profile) &&
        binding.LayoutFingerprint == ComputeLayoutFingerprint(layout) &&
        binding.SourceWidth == layout.SourceWidth &&
        binding.SourceHeight == layout.SourceHeight &&
        binding.TraceWidth == layout.TraceWidth &&
        binding.TraceHeight == layout.TraceHeight &&
        binding.LayoutTotalBytes == layout.TotalBytes;

    private static bool MeasurementMatchesBinding(
        in SimpleDdgiNearFieldResidualMeasurement measurement,
        in SimpleDdgiNearFieldResidualEvidenceBinding binding) =>
        string.Equals(measurement.CorpusId, binding.CorpusId,
            StringComparison.Ordinal) &&
        measurement.ContentRevision == binding.ContentRevision &&
        measurement.B3QualificationRevision == binding.B3QualificationRevision;

    private static bool HasEqualCostComparison(
        double c5AddedMilliseconds,
        double b3AddedMilliseconds)
    {
        if (!double.IsFinite(c5AddedMilliseconds) ||
            !double.IsFinite(b3AddedMilliseconds) ||
            c5AddedMilliseconds <= 0.0 || b3AddedMilliseconds <= 0.0)
        {
            return false;
        }

        double largest = Math.Max(c5AddedMilliseconds, b3AddedMilliseconds);
        return Math.Abs(c5AddedMilliseconds - b3AddedMilliseconds) / largest <=
            SimpleDdgiNearFieldResidualEvidenceAbi.MaximumEqualCostRelativeDifference;
    }

    private static SimpleDdgiNearFieldResidualEvidenceValidation Reject(
        GiExperimentFallbackReason fallbackReason,
        string reason,
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualDecision decision) => new(
            false,
            fallbackReason,
            reason,
            evidence.HasEvidenceId ? evidence.EvidenceId : string.Empty,
            evidence.Binding.Fingerprint,
            decision);

    private static ulong Add(ulong hash, ulong value)
    {
        for (int index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)(value & 0xFFUL);
            hash *= FnvPrime;
            value >>= 8;
        }

        return hash;
    }

    private static ulong AddTraceSourceContract(
        ulong hash,
        in SimpleDdgiNearFieldTraceSourceContract contract)
    {
        hash = Add(hash, (ulong)(uint)contract.Terms);
        hash = Add(hash, contract.AbiRevision);
        hash = Add(hash, (ulong)(uint)contract.Format);
        hash = Add(hash, (ulong)(uint)contract.Extent.FullWidth);
        hash = Add(hash, (ulong)(uint)contract.Extent.FullHeight);
        hash = Add(hash, (ulong)(uint)contract.Extent.ScaledWidth);
        hash = Add(hash, (ulong)(uint)contract.Extent.ScaledHeight);
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            contract.Extent.ResolutionScale));
        hash = Add(hash, (ulong)contract.ColorSpace);
        hash = Add(hash, (ulong)contract.Producer);
        hash = Add(hash, (ulong)contract.AlphaCoverage);
        hash = Add(hash, contract.LayoutRevision);
        return Add(hash, contract.SourceRevision);
    }

    private static ulong AddString(ulong hash, string? value)
    {
        if (value == null)
            return Add(hash, ulong.MaxValue);

        foreach (char character in value)
        {
            hash ^= (byte)(character & 0xFF);
            hash *= FnvPrime;
            hash ^= (byte)(character >> 8);
            hash *= FnvPrime;
        }

        return Add(hash, (ulong)value.Length);
    }
}

/// <summary>
/// Tracks residual temporal state ownership across safe mode/resize/revision
/// transitions. The GPU history resource mirrors these identities in headers.
/// </summary>
public sealed class SimpleDdgiNearFieldResidualHistoryManager
{
    private uint _generation = 1;
    private bool _hasHistory;
    private SimpleDdgiNearFieldHistoryIdentity _previous;

    public uint Generation => _generation;
    public bool HasHistory => _hasHistory;
    public uint ClearCount { get; private set; }

    public SimpleDdgiNearFieldHistoryValidation BeginFrame(
        in SimpleDdgiNearFieldHistoryIdentity current,
        float depthTolerance,
        float minimumNormalDot)
    {
        if (!_hasHistory)
            return SimpleDdgiNearFieldHistoryValidation.Reject(
                SimpleDdgiNearFieldHistoryRejectionReason.InvalidCurrentCandidate);

        return SimpleDdgiNearFieldResidualReference.ValidateHistory(
            current, _previous, depthTolerance, minimumNormalDot);
    }

    /// <summary>
    /// Stores only a validated current residual. Invalid/miss candidates clear
    /// the reusable state rather than preserving energy across a disocclusion.
    /// </summary>
    public void EndFrame(in SimpleDdgiNearFieldHistoryIdentity current)
    {
        if (!current.CurrentCandidateValid || current.CameraCut)
        {
            Clear();
            return;
        }

        _previous = current;
        _hasHistory = true;
    }

    public void Clear()
    {
        _hasHistory = false;
        _previous = default;
        _generation++;
        if (_generation == 0)
            _generation = 1;
        ClearCount++;
    }
}
