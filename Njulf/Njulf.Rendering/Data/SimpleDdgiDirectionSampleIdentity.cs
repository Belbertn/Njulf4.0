using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>
/// Describes the estimator that generated a persisted DDGI direction.  This is
/// deliberately distinct from the old Fibonacci-codebook ordinal: a guiding
/// distribution is allowed to change after a ray has been traced, while the
/// generating PDF must remain immutable with that ray/source-cache record.
/// </summary>
public enum SimpleDdgiDirectionSamplingTechnique : byte
{
    /// <summary>
    /// Stable, stratified rays that exclusively own visibility, relocation, and
    /// classification in the first guiding promotion.
    /// </summary>
    UniformMaintenance = 0,

    /// <summary>
    /// A radiometric ray drawn from the uniform/guided mixture.
    /// </summary>
    Mixture = 1
}

/// <summary>Branch selected inside a mixture-transport slot.</summary>
public enum SimpleDdgiDirectionMixtureBranch : byte
{
    Uniform = 0,
    Guided = 1
}

public enum SimpleDdgiDirectionIdentityValidationFailure : byte
{
    None = 0,
    InvalidLeafCount = 1,
    ProposalEpochMissing = 2,
    DistributionGenerationMissing = 3,
    InvalidTechnique = 4,
    InvalidMixtureBranch = 5,
    MaintenanceCannotUseGuidedBranch = 6,
    LeafOutOfRange = 7,
    InvalidGenerationTimePdf = 8,
    InvalidPackedDirection = 9,
    DirectionDoesNotMatchLeafSample = 10
}

/// <summary>Result of validating a direction identity before cache reuse.</summary>
public readonly record struct SimpleDdgiDirectionIdentityValidation(
    bool IsValid,
    SimpleDdgiDirectionIdentityValidationFailure Failure,
    string Reason)
{
    public static SimpleDdgiDirectionIdentityValidation Valid { get; } =
        new(true, SimpleDdgiDirectionIdentityValidationFailure.None, "valid");
}

/// <summary>
/// Versioned, cache-stable identity for a variable-PDF DDGI direction.
///
/// <para>
    /// <see cref="GenerationTimePdfBits"/> is the authoritative binary32 mixture
    /// PDF at the sampled direction, including for uniform-maintenance samples.
    /// The technique selects the sample's own proposal PDF. Consumers must not
    /// rebuild the cross-technique mixture PDF from a newer guide. The packed direction
/// is retained as an independent ABI-drift check and is intentionally not
/// reconstructed from a current distribution either.
/// </para>
/// </summary>
public readonly record struct SimpleDdgiDirectionSampleIdentity(
    ulong StableProbeId,
    uint ProposalEpoch,
    uint SlotIndex,
    SimpleDdgiDirectionSamplingTechnique Technique,
    SimpleDdgiDirectionMixtureBranch MixtureBranch,
    uint LeafIndex,
    uint IntraLeafSampleBits,
    uint PackedDirectionOct32,
    uint GenerationTimePdfBits,
    uint DistributionGeneration)
{
    /// <summary>
    /// Increment when field meaning, bit packing, or hash inputs change. This
    /// value belongs in persisted/source-cache ABI validation, not settings.
    /// </summary>
    public const uint AbiVersion = 0x4333_0007u;

    public const int IntraLeafComponentBitCount = 16;
    public const uint IntraLeafComponentMask = 0xffffu;

    /// <summary>The exact binary32 PDF supplied by the trace producer.</summary>
    public float GenerationTimePdf =>
        BitConverter.UInt32BitsToSingle(GenerationTimePdfBits);

    /// <summary>Decodes the authoritative persisted octahedral direction.</summary>
    public Vector3 DecodePackedDirection() =>
        SimpleDdgiTransportCachePacking.UnpackOctahedralSnorm16(
            PackedDirectionOct32);

    /// <summary>
    /// Creates an identity while preserving the producer PDF bit pattern rather
    /// than re-evaluating a potentially newer distribution.
    /// </summary>
    public static SimpleDdgiDirectionSampleIdentity Create(
        ulong stableProbeId,
        uint proposalEpoch,
        uint slotIndex,
        SimpleDdgiDirectionSamplingTechnique technique,
        SimpleDdgiDirectionMixtureBranch mixtureBranch,
        uint leafIndex,
        uint intraLeafSampleBits,
        Vector3 direction,
        float generationTimePdf,
        uint distributionGeneration)
    {
        if (!float.IsFinite(generationTimePdf) || generationTimePdf <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(generationTimePdf));
        if (proposalEpoch == 0u)
            throw new ArgumentOutOfRangeException(nameof(proposalEpoch));
        if (distributionGeneration == 0u)
            throw new ArgumentOutOfRangeException(nameof(distributionGeneration));

        return new SimpleDdgiDirectionSampleIdentity(
            stableProbeId,
            proposalEpoch,
            slotIndex,
            technique,
            mixtureBranch,
            leafIndex,
            intraLeafSampleBits,
            SimpleDdgiTransportCachePacking.PackOctahedralSnorm16(direction),
            BitConverter.SingleToUInt32Bits(generationTimePdf),
            distributionGeneration);
    }

    /// <summary>
    /// Packs two canonical 16-bit intra-leaf sample coordinates. Values are
    /// represented by cell centres on decode, so the result is always strictly
    /// inside the leaf and never crosses the azimuth seam by rounding.
    /// </summary>
    public static uint PackIntraLeafSample(double u, double v)
    {
        ushort packedU = QuantizeUnitOpen(u, nameof(u));
        ushort packedV = QuantizeUnitOpen(v, nameof(v));
        return packedU | ((uint)packedV << IntraLeafComponentBitCount);
    }

    /// <summary>Unpacks canonical, strictly interior intra-leaf coordinates.</summary>
    public static (double U, double V) UnpackIntraLeafSample(uint packed)
    {
        double u = ((packed & IntraLeafComponentMask) + 0.5d) / 65_536.0d;
        double v = ((packed >> IntraLeafComponentBitCount) + 0.5d) / 65_536.0d;
        return (u, v);
    }

    /// <summary>
    /// Validates all fields that can be checked without consulting the current
    /// guide. In particular, the PDF is checked as a binary32 value but is not
    /// recomputed from a hierarchy.
    /// </summary>
    public SimpleDdgiDirectionIdentityValidation Validate(int leafCount)
    {
        if (leafCount <= 0)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.InvalidLeafCount,
                "leaf-count-must-be-positive");
        }
        if (ProposalEpoch == 0u)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.ProposalEpochMissing,
                "proposal-epoch-missing");
        }
        if (DistributionGeneration == 0u)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.DistributionGenerationMissing,
                "distribution-generation-missing");
        }
        if (Technique is not SimpleDdgiDirectionSamplingTechnique.UniformMaintenance and
            not SimpleDdgiDirectionSamplingTechnique.Mixture)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.InvalidTechnique,
                "unknown-sampling-technique");
        }
        if (MixtureBranch is not SimpleDdgiDirectionMixtureBranch.Uniform and
            not SimpleDdgiDirectionMixtureBranch.Guided)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.InvalidMixtureBranch,
                "unknown-mixture-branch");
        }
        if (Technique == SimpleDdgiDirectionSamplingTechnique.UniformMaintenance &&
            MixtureBranch != SimpleDdgiDirectionMixtureBranch.Uniform)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.MaintenanceCannotUseGuidedBranch,
                "maintenance-rays-must-remain-uniform");
        }
        if (LeafIndex >= (uint)leafCount)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.LeafOutOfRange,
                "leaf-index-out-of-range");
        }
        if (!float.IsFinite(GenerationTimePdf) || GenerationTimePdf <= 0.0f)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.InvalidGenerationTimePdf,
                "generation-time-pdf-must-be-finite-and-positive");
        }

        Vector3 direction = DecodePackedDirection();
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            !float.IsFinite(direction.Z) || direction.LengthSquared() < 0.999f ||
            direction.LengthSquared() > 1.001f)
        {
            return Invalid(
                SimpleDdgiDirectionIdentityValidationFailure.InvalidPackedDirection,
                "packed-direction-is-not-a-finite-unit-vector");
        }

        return SimpleDdgiDirectionIdentityValidation.Valid;
    }

    /// <summary>
    /// Stable hash for cache keys and test vectors. It includes the ABI version
    /// and every identity field, including the generation-time PDF bits.
    /// </summary>
    public ulong ComputeHash64()
    {
        const ulong offset = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;

        ulong hash = offset;
        hash = Append(hash, AbiVersion, prime);
        hash = Append(hash, (uint)StableProbeId, prime);
        hash = Append(hash, (uint)(StableProbeId >> 32), prime);
        hash = Append(hash, ProposalEpoch, prime);
        hash = Append(hash, SlotIndex, prime);
        hash = Append(hash, (uint)Technique, prime);
        hash = Append(hash, (uint)MixtureBranch, prime);
        hash = Append(hash, LeafIndex, prime);
        hash = Append(hash, IntraLeafSampleBits, prime);
        hash = Append(hash, PackedDirectionOct32, prime);
        hash = Append(hash, GenerationTimePdfBits, prime);
        return Append(hash, DistributionGeneration, prime);
    }

    private static SimpleDdgiDirectionIdentityValidation Invalid(
        SimpleDdgiDirectionIdentityValidationFailure failure,
        string reason) => new(false, failure, reason);

    private static ushort QuantizeUnitOpen(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0d || value >= 1.0d)
            throw new ArgumentOutOfRangeException(parameterName);
        return checked((ushort)Math.Min(65_535, (int)Math.Floor(value * 65_536.0d)));
    }

    private static ulong Append(ulong hash, uint value, ulong prime)
    {
        hash ^= value;
        return unchecked(hash * prime);
    }
}
