using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// The phase of the V2 transport state machine.  A field is only allowed to
/// become <see cref="Certified"/> after a complete, generation-frozen audit.
/// </summary>
public enum SimpleDdgiTransportPhase : byte
{
    SourceRepair = 0,
    AcceleratedSolve = 1,
    AuditFrozen = 2,
    Certified = 3,
    Tracking = 4
}

/// <summary>
/// Why the latest transport certificate is not currently publishable.
/// Keeping this separate from the phase makes fail-closed diagnostics useful
/// without making the scheduler infer policy from a floating-point value.
/// </summary>
public enum SimpleDdgiTransportCertificationReason : byte
{
    None = 0,
    SourceRepairRequired = 1,
    SolveEpochIncomplete = 2,
    AuditNotStarted = 3,
    AuditInProgress = 4,
    GenerationsChanged = 5,
    ParticipantCoverageIncomplete = 6,
    NonFiniteEvidence = 7,
    TailAboveTolerance = 8,
    QuantizationLimited = 9,
    InvalidContractionBound = 10,
    Certified = 11,
    Tracking = 12,
    CounterOverflow = 13
}

/// <summary>
/// Resource generations that must remain immutable from the start of a solve
/// epoch through the end of its audit.
/// </summary>
public readonly record struct SimpleDdgiTransportGenerations(
    uint VolumeTable,
    uint PhysicalOwnership,
    uint SourceLighting,
    uint SourceEpoch,
    uint TransportOperator,
    uint CanonicalField,
    uint Solve,
    uint Audit,
    uint Queue,
    uint SchedulerResources)
{
    public bool IsInitialized =>
        VolumeTable != 0u &&
        PhysicalOwnership != 0u &&
        SourceLighting != 0u &&
        SourceEpoch != 0u &&
        TransportOperator != 0u &&
        CanonicalField != 0u &&
        Solve != 0u &&
        Audit != 0u &&
        Queue != 0u &&
        SchedulerResources != 0u;
}

/// <summary>
/// A compact CPU representation of the complete-field audit result.  The GPU
/// audit readback can be copied into this type without retaining the frozen
/// field itself.
/// </summary>
public readonly record struct SimpleDdgiTransportTailSummary
{
    public const float MaximumCertifiedContraction = 0.99f;

    public uint AuditEpoch { get; init; }
    public SimpleDdgiTransportGenerations Generations { get; init; }
    public uint ExpectedParticipantCount { get; init; }
    public uint AuditedParticipantCount { get; init; }
    public uint ExcludedInactiveCount { get; init; }
    public uint ExcludedNotVisibleCount { get; init; }
    public uint ExcludedStaleSourceCount { get; init; }
    public uint ExcludedInvalidCacheCount { get; init; }
    public uint CacheIdentityFailureCount { get; init; }
    public uint CacheCardinalityFailureCount { get; init; }
    public uint CacheSourceGenerationFailureCount { get; init; }
    public uint CacheSourceEpochFailureCount { get; init; }
    public uint CachePhysicalGenerationFailureCount { get; init; }
    public uint NonFiniteCount { get; init; }
    public uint CounterOverflowCount { get; init; }
    public uint AuditedTexelCount { get; init; }
    public uint ExpectedTexelCount { get; init; }
    public float FixedPointDefect { get; init; }
    public float FieldMagnitude { get; init; }
    public float ConfiguredContractionBound { get; init; }
    public float ObservedContractionBound { get; init; }
    public float CertifiedContractionBound { get; init; }
    public float AbsoluteTailBound { get; init; }
    public float RelativeTailBound { get; init; }
    public float Tolerance { get; init; }
    public float CanonicalQuantizationFloor { get; init; }
    /// <summary>
    /// Probe selected from the highest compact defect bucket. This locates a
    /// representative maximum-defect site without weakening or replacing the
    /// exact <see cref="FixedPointDefect"/> reduction.
    /// </summary>
    public uint MaximumDefectWitnessProbeIndex { get; init; }
    public uint MaximumDefectWitnessTexelIndex { get; init; }
    public bool DetailedWitnessValid { get; init; }
    public uint DetailedWitnessProbeIndex { get; init; }
    public uint DetailedWitnessTexelIndex { get; init; }
    public float DetailedWitnessWeightSum { get; init; }
    public float DetailedWitnessCandidateR { get; init; }
    public float DetailedWitnessCandidateG { get; init; }
    public float DetailedWitnessCandidateB { get; init; }
    public float DetailedWitnessCanonicalR { get; init; }
    public float DetailedWitnessCanonicalG { get; init; }
    public float DetailedWitnessCanonicalB { get; init; }
    public float DetailedWitnessProbeResidual { get; init; }
    public uint DetailedWitnessSourceRayCount { get; init; }
    public float DetailedWitnessPrivateR { get; init; }
    public float DetailedWitnessPrivateG { get; init; }
    public float DetailedWitnessPrivateB { get; init; }
    public ulong AuditMicroseconds { get; init; }
    public ulong FirstFrameSerial { get; init; }
    public ulong FinalFrameSerial { get; init; }
    public uint ChunkCount { get; init; }
    public bool IsComplete { get; init; }
    public SimpleDdgiTransportCertificationReason Reason { get; init; }

    public static SimpleDdgiTransportTailSummary Empty => new()
    {
        Reason = SimpleDdgiTransportCertificationReason.AuditNotStarted
    };

    public bool HasExactParticipantCoverage =>
        IsComplete &&
        AuditedParticipantCount == ExpectedParticipantCount &&
        ExcludedStaleSourceCount == 0u &&
        ExcludedInvalidCacheCount == 0u;

    public bool HasExactTexelCoverage =>
        (ExpectedTexelCount == 0u && AuditedTexelCount == 0u) ||
        (ExpectedTexelCount > 0u && AuditedTexelCount == ExpectedTexelCount);

    public bool HasFiniteEvidence =>
        NonFiniteCount == 0u &&
        CounterOverflowCount == 0u &&
        float.IsFinite(FixedPointDefect) &&
        float.IsFinite(FieldMagnitude) &&
        float.IsFinite(ConfiguredContractionBound) &&
        float.IsFinite(ObservedContractionBound) &&
        float.IsFinite(CertifiedContractionBound) &&
        float.IsFinite(AbsoluteTailBound) &&
        float.IsFinite(RelativeTailBound) &&
        float.IsFinite(Tolerance) &&
        FixedPointDefect >= 0.0f &&
        FieldMagnitude >= 0.0f &&
        ConfiguredContractionBound >= 0.0f &&
        ConfiguredContractionBound < 1.0f &&
        ObservedContractionBound >= 0.0f &&
        ObservedContractionBound <= ConfiguredContractionBound &&
        CertifiedContractionBound >= 0.0f &&
        CertifiedContractionBound < 1.0f &&
        CertifiedContractionBound <= ConfiguredContractionBound &&
        AbsoluteTailBound >= 0.0f &&
        RelativeTailBound >= 0.0f &&
        Tolerance >= 0.0001f &&
        float.IsFinite(CanonicalQuantizationFloor) &&
        CanonicalQuantizationFloor >= 0.0f &&
        ConfiguredContractionBound <= MaximumCertifiedContraction;

    public bool IsCertified =>
        Reason == SimpleDdgiTransportCertificationReason.Certified &&
        HasExactParticipantCoverage &&
        HasExactTexelCoverage &&
        HasFiniteEvidence &&
        CanonicalQuantizationFloor <= Tolerance &&
        AbsoluteTailBound <= Tolerance &&
        IsRecomputedTailConsistent();

    public bool IsCurrent(SimpleDdgiTransportGenerations generations) =>
        Generations == generations;

    private bool IsRecomputedTailConsistent()
    {
        float denominator = MathF.Max(1.0f - CertifiedContractionBound, 1e-6f);
        float recomputedTail = FixedPointDefect / denominator;
        float allowedError = MathF.Max(0.00001f, recomputedTail * 0.0001f);
        return MathF.Abs(recomputedTail - AbsoluteTailBound) <= allowedError;
    }
}
