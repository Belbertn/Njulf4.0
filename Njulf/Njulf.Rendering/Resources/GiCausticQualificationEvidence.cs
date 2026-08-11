using System;

namespace Njulf.Rendering.Resources;

/// <summary>Frozen policy revision for independently qualified C4 evidence.</summary>
public static class GiCausticQualificationEvidenceAbi
{
    public const uint Version = 0xC401_0103u;
    public const uint MinimumReferenceFrameCount = 120u;
    public const uint MinimumIndependentRunCount = 3u;
    public const double MinimumMaskedErrorReduction = 0.20;
    public const double MaximumRelativeEnergyError = 0.02;
}

/// <summary>
/// Runtime identity against which an archived C4 qualification is checked.
/// A driver/device key, content, source distributions, and current-pose TLAS
/// identity are all mandatory because any one can change path statistics.
/// </summary>
public readonly record struct GiCausticAdmissionContext(
    string DeviceQualificationKey,
    string CorpusId,
    ulong ContentRevision,
    ulong LightDistributionRevision,
    ulong EmissiveDistributionRevision,
    ulong HeroSourceRevision,
    ulong CurrentPoseTlasSignature,
    uint TransportAbiRevision = GiCausticGpuAbi.Version,
    uint ScreenResolveAbiRevision = GiCausticScreenGpuAbi.Version,
    string ShaderBundleHash = "")
{
    public bool TryValidate(out string failure)
    {
        if (!StableKey(DeviceQualificationKey) || !StableKey(CorpusId) ||
            !StableKey(ShaderBundleHash))
        {
            failure = "caustic-device-corpus-and-shader-qualification-identity-required";
            return false;
        }
        if (ContentRevision == 0UL || LightDistributionRevision == 0UL ||
            EmissiveDistributionRevision == 0UL || HeroSourceRevision == 0UL ||
            CurrentPoseTlasSignature == 0UL)
        {
            failure = "caustic-content-source-and-current-pose-revisions-required";
            return false;
        }
        if (TransportAbiRevision != GiCausticGpuAbi.Version)
        {
            failure = "caustic-transport-ABI-revision-mismatch";
            return false;
        }
        if (ScreenResolveAbiRevision != GiCausticScreenGpuAbi.Version)
        {
            failure = "caustic-screen-resolve-ABI-revision-mismatch";
            return false;
        }
        failure = "valid";
        return true;
    }

    internal static bool StableKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value!.Length <= 256 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !ContainsControl(value);

    private static bool ContainsControl(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
                return true;
        }
        return false;
    }
}

public readonly record struct GiCausticEvidenceBinding(
    GiCausticAdmissionContext Context,
    uint EvidenceAbiRevision,
    ulong ConfigurationFingerprint,
    ulong LayoutFingerprint,
    ulong LayoutTotalBytes)
{
    public ulong Fingerprint => GiCausticQualificationEvidenceEvaluator
        .ComputeBindingFingerprint(this);

    public bool TryValidate(out string failure)
    {
        if (!Context.TryValidate(out failure))
            return false;
        if (EvidenceAbiRevision != GiCausticQualificationEvidenceAbi.Version)
        {
            failure = "caustic-evidence-ABI-revision-mismatch";
            return false;
        }
        if (ConfigurationFingerprint == 0UL || LayoutFingerprint == 0UL ||
            LayoutTotalBytes == 0UL)
        {
            failure = "caustic-evidence-configuration-or-layout-binding-invalid";
            return false;
        }
        failure = "valid";
        return true;
    }

    public static GiCausticEvidenceBinding Create(
        in GiCausticAdmissionContext context,
        in GiTaggedCausticCacheConfiguration configuration,
        in GiCausticGpuResourceLayout layout) => new(
        context,
        GiCausticQualificationEvidenceAbi.Version,
        GiCausticQualificationEvidenceEvaluator.ComputeConfigurationFingerprint(
            configuration),
        GiCausticQualificationEvidenceEvaluator.ComputeLayoutFingerprint(layout),
        layout.TotalBytes);
}

public readonly record struct GiCausticQualificationMeasurement(
    string CorpusId,
    ulong ContentRevision,
    double C4OffMaskedReferenceError,
    double C4MaskedReferenceError,
    double RelativeEmittedToResolvedEnergyError,
    double AddedGpuMilliseconds,
    double P95TotalGpuMilliseconds,
    double P99TotalGpuMilliseconds,
    double PeakLiveMemoryBytes);

/// <summary>
/// Complete immutable C4 qualification artifact. Witnesses describe archived
/// test outputs; admission still binds them to the exact current context and
/// byte layout rather than trusting live booleans.
/// </summary>
public readonly record struct GiCausticQualificationEvidence(
    string EvidenceId,
    GiCausticEvidenceBinding Binding,
    GiCausticQualificationMeasurement Measurement,
    uint ReferenceFrameCount,
    uint IndependentRunCount,
    bool CpuGpuPdfAndThroughputParity,
    bool MirrorAndDielectricEnergyConservation,
    bool DifferentialReferencePassed,
    bool BottomKUnbiasednessPassed,
    bool DarkReceiverReferencePassed,
    bool OwnershipIsolationPassed,
    bool PublicationAndMotionStabilityPassed,
    bool WholeFrameRegressionPassed,
    bool QualityPerMillisecondImproved,
    bool ZeroWorkFallbackPassed)
{
    public bool HasEvidenceId => GiCausticAdmissionContext.StableKey(EvidenceId);
}

public readonly record struct GiCausticEvidenceValidation(
    bool Accepted,
    GiExperimentFallbackReason FallbackReason,
    string Reason,
    string EvidenceId,
    ulong BindingFingerprint,
    double MaskedErrorReduction)
{
    public static GiCausticEvidenceValidation Missing(string reason) => new(
        false,
        GiExperimentFallbackReason.EvidenceMissing,
        reason,
        string.Empty,
        0UL,
        0.0);
}

public static class GiCausticQualificationEvidenceEvaluator
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static GiCausticEvidenceValidation ValidateForAdmission(
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext context,
        in GiTaggedCausticCacheConfiguration configuration,
        in GiCausticGpuResourceLayout layout)
    {
        if (!context.TryValidate(out string contextFailure))
            return GiCausticEvidenceValidation.Missing(contextFailure);
        if (!evidence.HasEvidenceId)
        {
            return GiCausticEvidenceValidation.Missing(
                "caustic-qualification-evidence-id-required");
        }
        if (!evidence.Binding.TryValidate(out string bindingFailure))
            return Reject(evidence, GiExperimentFallbackReason.EvidenceInvalid,
                bindingFailure);
        GiCausticEvidenceBinding expected = GiCausticEvidenceBinding.Create(
            context, configuration, layout);
        if (evidence.Binding != expected)
        {
            return Reject(evidence,
                GiExperimentFallbackReason.EvidenceBindingMismatch,
                "caustic-evidence-device-content-source-or-layout-binding-mismatch");
        }

        GiCausticQualificationMeasurement measurement = evidence.Measurement;
        if (!string.Equals(measurement.CorpusId, context.CorpusId,
                StringComparison.Ordinal) ||
            measurement.ContentRevision != context.ContentRevision ||
            !FinitePositive(measurement.C4OffMaskedReferenceError) ||
            !double.IsFinite(measurement.C4MaskedReferenceError) ||
            measurement.C4MaskedReferenceError < 0.0 ||
            !double.IsFinite(measurement.RelativeEmittedToResolvedEnergyError) ||
            measurement.RelativeEmittedToResolvedEnergyError < 0.0 ||
            !FinitePositive(measurement.AddedGpuMilliseconds) ||
            !FinitePositive(measurement.P95TotalGpuMilliseconds) ||
            !FinitePositive(measurement.P99TotalGpuMilliseconds) ||
            measurement.P99TotalGpuMilliseconds < measurement.P95TotalGpuMilliseconds ||
            !FinitePositive(measurement.PeakLiveMemoryBytes) ||
            measurement.PeakLiveMemoryBytes > layout.TotalBytes)
        {
            return Reject(evidence, GiExperimentFallbackReason.EvidenceInvalid,
                "caustic-qualification-measurement-invalid");
        }
        if (evidence.ReferenceFrameCount <
                GiCausticQualificationEvidenceAbi.MinimumReferenceFrameCount ||
            evidence.IndependentRunCount <
                GiCausticQualificationEvidenceAbi.MinimumIndependentRunCount)
        {
            return Reject(evidence, GiExperimentFallbackReason.EvidenceInvalid,
                "caustic-qualification-sample-count-insufficient");
        }

        double reduction = 1.0 - measurement.C4MaskedReferenceError /
            measurement.C4OffMaskedReferenceError;
        if (!double.IsFinite(reduction) || reduction <
                GiCausticQualificationEvidenceAbi.MinimumMaskedErrorReduction)
        {
            return Reject(evidence,
                GiExperimentFallbackReason.QualificationNotPassed,
                "caustic-masked-error-reduction-below-promotion-floor",
                reduction);
        }
        if (measurement.RelativeEmittedToResolvedEnergyError >
            GiCausticQualificationEvidenceAbi.MaximumRelativeEnergyError)
        {
            return Reject(evidence,
                GiExperimentFallbackReason.QualificationNotPassed,
                "caustic-reference-energy-error-exceeds-limit",
                reduction);
        }
        if (!evidence.CpuGpuPdfAndThroughputParity ||
            !evidence.MirrorAndDielectricEnergyConservation ||
            !evidence.DifferentialReferencePassed ||
            !evidence.BottomKUnbiasednessPassed ||
            !evidence.DarkReceiverReferencePassed ||
            !evidence.OwnershipIsolationPassed ||
            !evidence.PublicationAndMotionStabilityPassed ||
            !evidence.WholeFrameRegressionPassed ||
            !evidence.QualityPerMillisecondImproved ||
            !evidence.ZeroWorkFallbackPassed)
        {
            return Reject(evidence,
                GiExperimentFallbackReason.QualificationNotPassed,
                "caustic-required-quality-witness-missing",
                reduction);
        }

        return new GiCausticEvidenceValidation(
            true,
            GiExperimentFallbackReason.None,
            "active-qualified-experiment",
            evidence.EvidenceId,
            evidence.Binding.Fingerprint,
            reduction);
    }

    public static ulong ComputeConfigurationFingerprint(
        in GiTaggedCausticCacheConfiguration configuration)
    {
        ulong hash = Add(FnvOffset, GiCausticQualificationEvidenceAbi.Version);
        hash = Add(hash, configuration.Enabled ? 1UL : 0UL);
        hash = Add(hash, unchecked((uint)configuration.HeroMaterialCount));
        hash = Add(hash, unchecked((uint)configuration.PhotonTaskCapacity));
        hash = Add(hash, unchecked((uint)configuration.MaximumWorldCells));
        hash = Add(hash, unchecked((uint)configuration.MaximumPhotonsPerCell));
        hash = Add(hash, configuration.MemoryBudgetBytes);
        hash = Add(hash, unchecked((uint)configuration.RecordStride));
        hash = Add(hash, unchecked((uint)configuration.CacheBankCount));
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            configuration.TargetLoadFactor));
        hash = Add(hash, unchecked((uint)configuration.MaximumEmitterCount));
        hash = Add(hash, unchecked((uint)configuration.MaximumHeroCount));
        hash = Add(hash, unchecked((uint)configuration.MaximumProposalPairCount));
        hash = Add(hash, configuration.MaximumStorageBufferRange);
        hash = Add(hash, unchecked((uint)configuration.ScreenResolveProfile.Width));
        hash = Add(hash, unchecked((uint)configuration.ScreenResolveProfile.Height));
        hash = Add(hash, unchecked((uint)configuration.ScreenResolveProfile.TileSize));
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            configuration.ScreenResolveProfile.MinimumReceiverNormalCosine));
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            configuration.WorldCellSize));
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            configuration.DirectionalEmissionDiskRadius));
        return Add(hash, BitConverter.SingleToUInt32Bits(
            configuration.TargetingMixtureProbability));
    }

    public static ulong ComputeLayoutFingerprint(
        in GiCausticGpuResourceLayout layout)
    {
        ulong hash = Add(FnvOffset, GiCausticQualificationEvidenceAbi.Version);
        hash = Add(hash, GiCausticGpuAbi.Version);
        hash = Add(hash, unchecked((uint)layout.TaskCapacity));
        hash = Add(hash, unchecked((uint)layout.CellTableCapacity));
        hash = Add(hash, unchecked((uint)layout.MaximumPhotonsPerCell));
        hash = Add(hash, unchecked((uint)layout.EmitterCapacity));
        hash = Add(hash, unchecked((uint)layout.HeroCapacity));
        hash = Add(hash, unchecked((uint)layout.ProposalPairCapacity));
        hash = Add(hash, layout.TaskQueueBytes);
        hash = Add(hash, layout.CandidateStagingBytes);
        hash = Add(hash, layout.PublishedPhotonBytes);
        hash = Add(hash, layout.CacheBytes);
        hash = Add(hash, layout.ScratchBytes);
        hash = Add(hash, GiCausticScreenGpuAbi.Version);
        hash = Add(hash, unchecked((uint)layout.ScreenResolve.Width));
        hash = Add(hash, unchecked((uint)layout.ScreenResolve.Height));
        hash = Add(hash, unchecked((uint)layout.ScreenResolve.TileSize));
        hash = Add(hash, unchecked((uint)layout.ScreenResolve.TileCapacity));
        hash = Add(hash, BitConverter.SingleToUInt32Bits(
            layout.ScreenResolve.MinimumReceiverNormalCosine));
        hash = Add(hash, layout.ScreenResolve.ReceiverPayloadBytes);
        hash = Add(hash, layout.ScreenResolve.RadianceBytes);
        hash = Add(hash, layout.ScreenResolve.MomentsBytes);
        hash = Add(hash, layout.ScreenResolve.TileScratchBytes);
        hash = Add(hash, layout.RuntimeMetadataBytes);
        return Add(hash, layout.TotalBytes);
    }

    public static ulong ComputeBindingFingerprint(in GiCausticEvidenceBinding binding)
    {
        ulong hash = Add(FnvOffset, binding.EvidenceAbiRevision);
        hash = AddString(hash, binding.Context.DeviceQualificationKey);
        hash = AddString(hash, binding.Context.CorpusId);
        hash = Add(hash, binding.Context.ContentRevision);
        hash = Add(hash, binding.Context.LightDistributionRevision);
        hash = Add(hash, binding.Context.EmissiveDistributionRevision);
        hash = Add(hash, binding.Context.HeroSourceRevision);
        hash = Add(hash, binding.Context.CurrentPoseTlasSignature);
        hash = Add(hash, binding.Context.TransportAbiRevision);
        hash = Add(hash, binding.Context.ScreenResolveAbiRevision);
        hash = AddString(hash, binding.Context.ShaderBundleHash);
        hash = Add(hash, binding.ConfigurationFingerprint);
        hash = Add(hash, binding.LayoutFingerprint);
        return Add(hash, binding.LayoutTotalBytes);
    }

    private static GiCausticEvidenceValidation Reject(
        in GiCausticQualificationEvidence evidence,
        GiExperimentFallbackReason fallback,
        string reason,
        double reduction = 0.0) => new(
        false,
        fallback,
        reason,
        evidence.EvidenceId ?? string.Empty,
        evidence.Binding.Fingerprint,
        reduction);

    private static bool FinitePositive(double value) =>
        double.IsFinite(value) && value > 0.0;

    private static ulong Add(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * FnvPrime;
        return hash;
    }

    private static ulong AddString(ulong hash, string value)
    {
        foreach (char character in value)
        {
            hash = (hash ^ (byte)character) * FnvPrime;
            hash = (hash ^ (byte)(character >> 8)) * FnvPrime;
        }
        return Add(hash, unchecked((uint)value.Length));
    }
}
