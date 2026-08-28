using System;
using System.Text.Json.Serialization;

namespace Njulf.Rendering.Data;

/// <summary>
/// A non-negative RGB infinity-bound tuple used by the transport certificate.
/// Keeping channels separate lets the proof use the actual recursive gain of
/// each colour channel instead of pessimistically pairing the largest defect
/// with the largest gain observed in a different channel.
/// </summary>
public readonly record struct SimpleDdgiTransportRgbBounds(
    [property: JsonRequired] float Red,
    [property: JsonRequired] float Green,
    [property: JsonRequired] float Blue)
{
    [JsonIgnore]
    public float Maximum => MathF.Max(Red, MathF.Max(Green, Blue));

    [JsonIgnore]
    public bool IsFiniteNonNegative =>
        float.IsFinite(Red) && Red >= 0.0f &&
        float.IsFinite(Green) && Green >= 0.0f &&
        float.IsFinite(Blue) && Blue >= 0.0f;

    public bool IsAtMost(float ceiling) =>
        IsFiniteNonNegative &&
        float.IsFinite(ceiling) &&
        Red <= ceiling && Green <= ceiling && Blue <= ceiling;

    public static SimpleDdgiTransportRgbBounds Broadcast(float value) =>
        new(value, value, value);

    public static SimpleDdgiTransportRgbBounds Max(
        SimpleDdgiTransportRgbBounds left,
        SimpleDdgiTransportRgbBounds right) =>
        new(
            MathF.Max(left.Red, right.Red),
            MathF.Max(left.Green, right.Green),
            MathF.Max(left.Blue, right.Blue));
}

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
    Tracking = 4,
    /// <summary>
    /// The scheduler and audit observed different participant snapshots.  No
    /// canonical data is mutated in this phase; the next fence-complete
    /// scheduler summary must establish a new participant witness first.
    /// </summary>
    ParticipantReconciliation = 5,
    /// <summary>
    /// Invalid numerical evidence discarded the transaction-private solve
    /// state.  The last coherent canonical field remains receiver-visible
    /// while a bounded source/solve generation is rebuilt.
    /// </summary>
    FailClosedRecovery = 6,
    /// <summary>
    /// The requested tolerance is below the representable canonical storage
    /// floor.  This is terminal for the current configuration and deliberately
    /// does not dispatch another identical audit.
    /// </summary>
    UnsupportedTolerance = 7
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
    CounterOverflow = 13,
    InvalidCache = 14,
    AuditReadbackTimeout = 15,
    SameTupleReauditBlocked = 16,
    CompletedAuditUnconsumed = 17,
    SourceCohortNoProgress = 18,
    FailClosedRecovery = 19,
    ConvergenceDeadlineExceeded = 20
}

/// <summary>
/// Concrete control-plane work requested by the most recent audit decision.
/// Keeping the action separate from the diagnostic reason prevents manager
/// integration from inferring destructive recovery policy from a float or a
/// broad phase name.
/// </summary>
public enum SimpleDdgiTransportRecoveryAction : byte
{
    None = 0,
    ReconcileParticipants = 1,
    RepairSourceCache = 2,
    AdvanceSolveEpoch = 3,
    RebuildPrivateField = 4,
    ReportUnsupportedTolerance = 5
}

/// <summary>One bounded virtual/physical identity captured for an audit mismatch.</summary>
public readonly record struct SimpleDdgiTransportMismatchIdentity(
    [property: JsonRequired] uint VirtualProbeIndex,
    [property: JsonRequired] uint PhysicalProbeIndex)
{
    public static SimpleDdgiTransportMismatchIdentity None { get; } = new(
        uint.MaxValue,
        uint.MaxValue);

    [JsonIgnore]
    public bool IsValid => VirtualProbeIndex != uint.MaxValue;

    public static SimpleDdgiTransportMismatchIdentity FromPacked(uint packed)
    {
        uint virtualPlusOne = packed & 0xffffu;
        uint physicalPlusOne = packed >> 16;
        return virtualPlusOne == 0u
            ? None
            : new SimpleDdgiTransportMismatchIdentity(
                virtualPlusOne - 1u,
                physicalPlusOne == 0u ? uint.MaxValue : physicalPlusOne - 1u);
    }
}

/// <summary>
/// Resource generations that must remain immutable from the start of a solve
/// epoch through the end of its audit.
/// </summary>
public readonly record struct SimpleDdgiTransportGenerations(
    [property: JsonRequired] uint VolumeTable,
    [property: JsonRequired] uint PhysicalOwnership,
    [property: JsonRequired] uint SourceLighting,
    [property: JsonRequired] uint SourceEpoch,
    [property: JsonRequired] uint TransportOperator,
    [property: JsonRequired] uint CanonicalField,
    [property: JsonRequired] uint Solve,
    [property: JsonRequired] uint Audit,
    [property: JsonRequired] uint Queue,
    [property: JsonRequired] uint SchedulerResources)
{
    /// <summary>
    /// Folded ray-scene content epoch. Kept as an additive property so legacy
    /// evidence can deserialize fail-closed without changing the positional
    /// constructor ABI.
    /// </summary>
    [JsonRequired]
    public uint DynamicGeometryEpoch { get; init; } = 1U;

    [JsonIgnore]
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
        SchedulerResources != 0u &&
        DynamicGeometryEpoch != 0u;
}

/// <summary>
/// A compact CPU representation of the complete-field audit result.  The GPU
/// audit readback can be copied into this type without retaining the frozen
/// field itself.
/// </summary>
public readonly record struct SimpleDdgiTransportTailSummary
{
    public const float MaximumCertifiedContraction = 0.99f;
    public const uint PerChannelEvidenceVersion = 1u;

    [JsonRequired]
    public uint AuditEpoch { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportGenerations Generations { get; init; }
    [JsonRequired]
    public uint ExpectedParticipantCount { get; init; }
    [JsonRequired]
    public uint AuditedParticipantCount { get; init; }
    [JsonRequired]
    public uint ExcludedInactiveCount { get; init; }
    /// <summary>
    /// Virtual probes that were not resident and published when the exact
    /// participant snapshot was frozen. This is expected sparse-domain
    /// exclusion evidence, not incomplete coverage of that snapshot.
    /// </summary>
    [JsonRequired]
    public uint ExcludedNotVisibleCount { get; init; }
    [JsonRequired]
    public uint ExcludedStaleSourceCount { get; init; }
    [JsonRequired]
    public uint ExcludedInvalidCacheCount { get; init; }
    [JsonRequired]
    public uint CacheIdentityFailureCount { get; init; }
    [JsonRequired]
    public uint CacheCardinalityFailureCount { get; init; }
    [JsonRequired]
    public uint CacheSourceGenerationFailureCount { get; init; }
    [JsonRequired]
    public uint CacheSourceEpochFailureCount { get; init; }
    [JsonRequired]
    public uint CachePhysicalGenerationFailureCount { get; init; }
    [JsonRequired]
    public uint NonFiniteCount { get; init; }
    [JsonRequired]
    public uint CounterOverflowCount { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportMismatchIdentity FirstNotResidentIdentity { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportMismatchIdentity FirstStaleSourceIdentity { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportMismatchIdentity FirstInvalidCacheIdentity { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportMismatchIdentity FirstNonFiniteIdentity { get; init; }
    [JsonRequired]
    public uint AuditedTexelCount { get; init; }
    [JsonRequired]
    public uint ExpectedTexelCount { get; init; }
    [JsonRequired]
    public float FixedPointDefect { get; init; }
    [JsonRequired]
    public float FieldMagnitude { get; init; }
    [JsonRequired]
    public float ConfiguredContractionBound { get; init; }
    [JsonRequired]
    public float ObservedContractionBound { get; init; }
    [JsonRequired]
    public float CertifiedContractionBound { get; init; }
    [JsonRequired]
    public float AbsoluteTailBound { get; init; }
    [JsonRequired]
    public float RelativeTailBound { get; init; }
    [JsonRequired]
    public float Tolerance { get; init; }
    [JsonRequired]
    public float CanonicalQuantizationFloor { get; init; }
    /// <summary>
    /// Zero denotes the conservative legacy scalar proof. Version one carries
    /// independent RGB reductions and is required for new GPU audit readback.
    /// The scalar fields remain the maxima for stable diagnostics and ABI
    /// consumers.
    /// </summary>
    [JsonRequired]
    public uint ChannelEvidenceVersion { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds FixedPointDefectChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds FieldMagnitudeChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds ObservedContractionChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds CertifiedContractionChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds AbsoluteTailBoundChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds RelativeTailBoundChannels { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportRgbBounds CanonicalQuantizationFloorChannels { get; init; }
    /// <summary>
    /// Probe selected from the highest compact defect bucket. This locates a
    /// representative maximum-defect site without weakening or replacing the
    /// exact <see cref="FixedPointDefect"/> reduction.
    /// </summary>
    [JsonRequired]
    public uint MaximumDefectWitnessProbeIndex { get; init; }
    [JsonRequired]
    public uint MaximumDefectWitnessTexelIndex { get; init; }
    [JsonRequired]
    public bool DetailedWitnessValid { get; init; }
    [JsonRequired]
    public uint DetailedWitnessProbeIndex { get; init; }
    [JsonRequired]
    public uint DetailedWitnessTexelIndex { get; init; }
    [JsonRequired]
    public float DetailedWitnessWeightSum { get; init; }
    [JsonRequired]
    public float DetailedWitnessCandidateR { get; init; }
    [JsonRequired]
    public float DetailedWitnessCandidateG { get; init; }
    [JsonRequired]
    public float DetailedWitnessCandidateB { get; init; }
    [JsonRequired]
    public float DetailedWitnessCanonicalR { get; init; }
    [JsonRequired]
    public float DetailedWitnessCanonicalG { get; init; }
    [JsonRequired]
    public float DetailedWitnessCanonicalB { get; init; }
    [JsonRequired]
    public float DetailedWitnessProbeResidual { get; init; }
    [JsonRequired]
    public uint DetailedWitnessSourceRayCount { get; init; }
    [JsonRequired]
    public float DetailedWitnessPrivateR { get; init; }
    [JsonRequired]
    public float DetailedWitnessPrivateG { get; init; }
    [JsonRequired]
    public float DetailedWitnessPrivateB { get; init; }
    [JsonRequired]
    public ulong AuditMicroseconds { get; init; }
    /// <summary>
    /// Scheduler-feedback serial of the complete, nonzero solve-epoch
    /// reduction that armed the drain preceding this audit.
    /// </summary>
    [JsonRequired]
    public ulong AuditSolveFeedbackFrameSerial { get; init; }
    /// <summary>
    /// Scheduler-feedback serial of the later epoch-zero/quiescent reduction
    /// that completed the drain and allowed this audit to freeze.
    /// </summary>
    [JsonRequired]
    public ulong AuditTriggerFeedbackFrameSerial { get; init; }
    [JsonRequired]
    public ulong FirstFrameSerial { get; init; }
    [JsonRequired]
    public ulong FinalFrameSerial { get; init; }
    [JsonRequired]
    public uint ChunkCount { get; init; }
    [JsonRequired]
    public bool IsComplete { get; init; }
    [JsonRequired]
    public SimpleDdgiTransportCertificationReason Reason { get; init; }

    public static SimpleDdgiTransportTailSummary Empty => new()
    {
        Reason = SimpleDdgiTransportCertificationReason.AuditNotStarted,
        FirstNotResidentIdentity = SimpleDdgiTransportMismatchIdentity.None,
        FirstStaleSourceIdentity = SimpleDdgiTransportMismatchIdentity.None,
        FirstInvalidCacheIdentity = SimpleDdgiTransportMismatchIdentity.None,
        FirstNonFiniteIdentity = SimpleDdgiTransportMismatchIdentity.None
    };

    [JsonIgnore]
    public bool HasExactParticipantCoverage =>
        IsComplete &&
        AuditedParticipantCount == ExpectedParticipantCount &&
        ExcludedStaleSourceCount == 0u &&
        ExcludedInvalidCacheCount == 0u;

    [JsonIgnore]
    public bool HasExactTexelCoverage =>
        (ExpectedTexelCount == 0u && AuditedTexelCount == 0u) ||
        (ExpectedTexelCount > 0u && AuditedTexelCount == ExpectedTexelCount);

    [JsonIgnore]
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
        ConfiguredContractionBound <= MaximumCertifiedContraction &&
        HasFiniteChannelEvidence;

    [JsonIgnore]
    public bool HasPerChannelEvidence =>
        ChannelEvidenceVersion == PerChannelEvidenceVersion;

    private bool HasFiniteChannelEvidence =>
        ChannelEvidenceVersion == 0u ||
        (HasPerChannelEvidence &&
         FixedPointDefectChannels.IsFiniteNonNegative &&
         FieldMagnitudeChannels.IsFiniteNonNegative &&
         ObservedContractionChannels.IsAtMost(ConfiguredContractionBound) &&
         CertifiedContractionChannels.IsAtMost(ConfiguredContractionBound) &&
         ChannelsMatchCertifiedContraction() &&
         AbsoluteTailBoundChannels.IsFiniteNonNegative &&
         RelativeTailBoundChannels.IsFiniteNonNegative &&
         CanonicalQuantizationFloorChannels.IsFiniteNonNegative &&
         ScalarsMatchChannelMaxima());

    [JsonIgnore]
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
        if (HasPerChannelEvidence)
        {
            SimpleDdgiTransportRgbBounds recomputed = new(
                FixedPointDefectChannels.Red /
                    MathF.Max(1.0f - CertifiedContractionChannels.Red, 1e-6f),
                FixedPointDefectChannels.Green /
                    MathF.Max(1.0f - CertifiedContractionChannels.Green, 1e-6f),
                FixedPointDefectChannels.Blue /
                    MathF.Max(1.0f - CertifiedContractionChannels.Blue, 1e-6f));
            SimpleDdgiTransportRgbBounds recomputedRelative = new(
                recomputed.Red / MathF.Max(FieldMagnitudeChannels.Red, 0.0001f),
                recomputed.Green / MathF.Max(FieldMagnitudeChannels.Green, 0.0001f),
                recomputed.Blue / MathF.Max(FieldMagnitudeChannels.Blue, 0.0001f));
            return NearlyEqual(recomputed.Red, AbsoluteTailBoundChannels.Red) &&
                NearlyEqual(recomputed.Green, AbsoluteTailBoundChannels.Green) &&
                NearlyEqual(recomputed.Blue, AbsoluteTailBoundChannels.Blue) &&
                NearlyEqual(recomputed.Maximum, AbsoluteTailBound) &&
                NearlyEqual(
                    recomputedRelative.Red,
                    RelativeTailBoundChannels.Red) &&
                NearlyEqual(
                    recomputedRelative.Green,
                    RelativeTailBoundChannels.Green) &&
                NearlyEqual(
                    recomputedRelative.Blue,
                    RelativeTailBoundChannels.Blue) &&
                NearlyEqual(recomputedRelative.Maximum, RelativeTailBound);
        }

        float denominator = MathF.Max(1.0f - CertifiedContractionBound, 1e-6f);
        float recomputedTail = FixedPointDefect / denominator;
        return NearlyEqual(recomputedTail, AbsoluteTailBound);
    }

    private bool ScalarsMatchChannelMaxima() =>
        NearlyEqual(FixedPointDefectChannels.Maximum, FixedPointDefect) &&
        NearlyEqual(FieldMagnitudeChannels.Maximum, FieldMagnitude) &&
        NearlyEqual(ObservedContractionChannels.Maximum, ObservedContractionBound) &&
        NearlyEqual(CertifiedContractionChannels.Maximum, CertifiedContractionBound) &&
        NearlyEqual(AbsoluteTailBoundChannels.Maximum, AbsoluteTailBound) &&
        NearlyEqual(RelativeTailBoundChannels.Maximum, RelativeTailBound) &&
        NearlyEqual(
            CanonicalQuantizationFloorChannels.Maximum,
            CanonicalQuantizationFloor);

    private bool ChannelsMatchCertifiedContraction() =>
        NearlyEqual(
            MathF.Min(ConfiguredContractionBound,
                ObservedContractionChannels.Red),
            CertifiedContractionChannels.Red) &&
        NearlyEqual(
            MathF.Min(ConfiguredContractionBound,
                ObservedContractionChannels.Green),
            CertifiedContractionChannels.Green) &&
        NearlyEqual(
            MathF.Min(ConfiguredContractionBound,
                ObservedContractionChannels.Blue),
            CertifiedContractionChannels.Blue);

    private static bool NearlyEqual(float expected, float actual)
    {
        float allowedError = MathF.Max(0.00001f, MathF.Abs(expected) * 0.0001f);
        return MathF.Abs(expected - actual) <= allowedError;
    }
}
