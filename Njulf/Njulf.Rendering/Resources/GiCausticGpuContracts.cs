using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Versioned GPU ABI for the isolated C4 photon-cache workload.
///
/// <para>The cache is deliberately not a DDGI atlas, source cache, or transport
/// input.  Every record below belongs only to tagged specular/refractive paths
/// and is rejected when its generation or revision fingerprint differs from
/// the active C4 transaction.</para>
/// </summary>
public static class GiCausticGpuAbi
{
    public const uint Version = 0xC401_0005u;
    public const int TaskDispatchHeaderBytes = 64;
    public const int TaskRecordBytes = 128;
    public const int EmitterRecordBytes = 128;
    public const int HeroRecordBytes = 128;
    public const int ProposalPairRecordBytes = 32;
    public const int PhotonRecordBytes = 80;
    public const int CellEntryBytes = 32;
    public const int CacheHeaderBytes = 128;
    public const int ResolveRequestBytes = 64;
    public const int ResolveResultBytes = 48;
    public const int PushConstantsBytes = 128;
    public const uint DescriptorCount = 4;

    /// <summary>
    /// Fixed storage binding order.  The indices themselves are owned by the
    /// renderer-wide bindless table; this type makes the C4 dependency
    /// explicit and validates that it cannot drift into a DDGI slot.
    /// </summary>
    public static GiCausticGpuBindlessSlots BindlessSlots { get; } = new(
        BindlessIndex.GiCausticTaskBuffer,
        BindlessIndex.GiCausticPhotonBuffer,
        BindlessIndex.GiCausticCacheBuffer,
        BindlessIndex.GiCausticScratchBuffer);

    public static void VerifyManagedLayout()
    {
        Verify<GPUCausticTaskDispatchHeaderV1>(TaskDispatchHeaderBytes,
            (nameof(GPUCausticTaskDispatchHeaderV1.AbiVersion), 0),
            (nameof(GPUCausticTaskDispatchHeaderV1.CacheGeneration), 4),
            (nameof(GPUCausticTaskDispatchHeaderV1.TaskCount), 8),
            (nameof(GPUCausticTaskDispatchHeaderV1.RevisionFingerprintLow), 24));
        Verify<GPUCausticPhotonTaskV1>(TaskRecordBytes,
            (nameof(GPUCausticPhotonTaskV1.AbiVersion), 0),
            (nameof(GPUCausticPhotonTaskV1.HeroInstanceId), 16),
            (nameof(GPUCausticPhotonTaskV1.OriginAndSelectionPdf), 32),
            (nameof(GPUCausticPhotonTaskV1.DirectionAndPathPdf), 48),
            (nameof(GPUCausticPhotonTaskV1.EmittedContributionAndPositionPdf), 64),
            (nameof(GPUCausticPhotonTaskV1.InitialFluxAndDirectionPdf), 80),
            (nameof(GPUCausticPhotonTaskV1.HeroOptics), 96),
            (nameof(GPUCausticPhotonTaskV1.AbsorptionAndMaximumDistance), 112));
        Verify<GPUCausticEmitterV1>(EmitterRecordBytes,
            (nameof(GPUCausticEmitterV1.AbiVersion), 0),
            (nameof(GPUCausticEmitterV1.PositionAndRange), 16),
            (nameof(GPUCausticEmitterV1.RadiometricValueAndSelectionWeight), 48),
            (nameof(GPUCausticEmitterV1.NormalAndTargetingMix), 96),
            (nameof(GPUCausticEmitterV1.ContentRevisionLow), 112));
        Verify<GPUCausticHeroV1>(HeroRecordBytes,
            (nameof(GPUCausticHeroV1.AbiVersion), 0),
            (nameof(GPUCausticHeroV1.BoundsCenterAndRadius), 16),
            (nameof(GPUCausticHeroV1.HeroOptics), 64),
            (nameof(GPUCausticHeroV1.AbsorptionAndMaximumDistance), 80),
            (nameof(GPUCausticHeroV1.GeometryRevisionLow), 112));
        Verify<GPUCausticProposalPairV1>(ProposalPairRecordBytes,
            (nameof(GPUCausticProposalPairV1.EmitterIndex), 0),
            (nameof(GPUCausticProposalPairV1.EmitterPdf), 16),
            (nameof(GPUCausticProposalPairV1.CdfUpper), 24));
        Verify<GPUCausticPhotonCandidateV1>(PhotonRecordBytes,
            (nameof(GPUCausticPhotonCandidateV1.WorldPosition), 0),
            (nameof(GPUCausticPhotonCandidateV1.IncidentFlux), 16),
            (nameof(GPUCausticPhotonCandidateV1.PackedIncidentDirection), 32),
            (nameof(GPUCausticPhotonCandidateV1.TangentPlaneFootprint), 48),
            (nameof(GPUCausticPhotonCandidateV1.SourceId), 64),
            (nameof(GPUCausticPhotonCandidateV1.PathSignature), 68),
            (nameof(GPUCausticPhotonCandidateV1.CacheGeneration), 76));
        Verify<GPUCausticCellEntryV1>(CellEntryBytes,
            (nameof(GPUCausticCellEntryV1.CellX), 0),
            (nameof(GPUCausticCellEntryV1.Cascade), 12),
            (nameof(GPUCausticCellEntryV1.PhotonOffset), 16),
            (nameof(GPUCausticCellEntryV1.Flags), 28));
        Verify<GPUCausticCacheHeaderV1>(CacheHeaderBytes,
            (nameof(GPUCausticCacheHeaderV1.AbiVersion), 0),
            (nameof(GPUCausticCacheHeaderV1.CellTableCapacity), 28),
            (nameof(GPUCausticCacheHeaderV1.PublicationFlags), 52),
            (nameof(GPUCausticCacheHeaderV1.CellOriginAndSize), 64),
            (nameof(GPUCausticCacheHeaderV1.PhotonBankIndex), 80));
        Verify<GPUCausticResolveRequestV1>(ResolveRequestBytes,
            (nameof(GPUCausticResolveRequestV1.WorldPosition), 0),
            (nameof(GPUCausticResolveRequestV1.PackedReceiverNormal), 16),
            (nameof(GPUCausticResolveRequestV1.OutputIndex), 24),
            (nameof(GPUCausticResolveRequestV1.DiffuseBrdf), 32),
            (nameof(GPUCausticResolveRequestV1.StableReceiverId), 48),
            (nameof(GPUCausticResolveRequestV1.TransportRevision), 56));
        Verify<GPUCausticResolveResultV1>(ResolveResultBytes,
            (nameof(GPUCausticResolveResultV1.Radiance), 0),
            (nameof(GPUCausticResolveResultV1.Confidence), 12),
            (nameof(GPUCausticResolveResultV1.CacheGeneration), 16),
            (nameof(GPUCausticResolveResultV1.Flags), 20),
            (nameof(GPUCausticResolveResultV1.RejectedPhotonCount), 28),
            (nameof(GPUCausticResolveResultV1.LuminanceMoments), 32));
        Verify<GPUCausticPushConstantsV1>(PushConstantsBytes,
            (nameof(GPUCausticPushConstantsV1.AbiVersion), 0),
            (nameof(GPUCausticPushConstantsV1.TaskCount), 20),
            (nameof(GPUCausticPushConstantsV1.CandidateStagingWordOffset), 52),
            (nameof(GPUCausticPushConstantsV1.CacheBankHeaderWordOffset), 76),
            (nameof(GPUCausticPushConstantsV1.ResolveRequestCount), 100),
            (nameof(GPUCausticPushConstantsV1.CellOriginAndSize), 112));
        BindlessSlots.Validate();
    }

    /// <summary>
    /// Computes a stable nonzero fingerprint for the exact revisions that own
    /// photon placement and energy.  This is a cache validation token, not a
    /// substitute for comparing the full revision on the CPU.
    /// </summary>
    public static ulong ComputeRevisionFingerprint(in GiCausticCacheRevision revision)
    {
        const ulong offsetBasis = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        ulong state = offsetBasis;
        state = Append(state, revision.TransportAbi, prime);
        state = Append(state, revision.HeroMaterialRevision, prime);
        state = Append(state, revision.LightDistributionRevision, prime);
        state = Append(state, revision.CasterGeometryRevision, prime);
        state = Append(state, revision.CasterTransformRevision, prime);
        state = Append(state, revision.ReceiverGeometryRevision, prime);
        state = Append(state, revision.StableIdentityRevision, prime);
        return state == 0UL ? 1UL : state;
    }

    public static uint Low32(ulong value) => unchecked((uint)value);

    public static uint High32(ulong value) => unchecked((uint)(value >> 32));

    private static ulong Append(ulong state, uint value, ulong prime)
    {
        state ^= value;
        return unchecked(state * prime);
    }

    private static ulong Append(ulong state, ulong value, ulong prime)
    {
        state = Append(state, unchecked((uint)value), prime);
        return Append(state, unchecked((uint)(value >> 32)), prime);
    }

    private static void Verify<T>(
        int expectedSize,
        params (string Field, int Offset)[] offsets)
        where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() != expectedSize)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} must be exactly {expectedSize} bytes; it is {Unsafe.SizeOf<T>()} bytes.");
        }

        foreach ((string field, int expectedOffset) in offsets)
        {
            int actualOffset = Marshal.OffsetOf<T>(field).ToInt32();
            if (actualOffset != expectedOffset)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{field} must be at offset {expectedOffset}; it is at {actualOffset}.");
            }
        }
    }
}

/// <summary>
/// Frozen phase encoding for the C4 task and deterministic cache-build
/// kernels.  Radix phases carry the least-significant-key index in bits 8..15
/// and byte index in bits 16..17.  Keeping the encoding in the managed ABI
/// prevents command-recorder/shader drift.
/// </summary>
public static class GiCausticGpuBuildPhases
{
    public const uint OperationMask = 0xffu;
    public const int RadixKeyShift = 8;
    public const int RadixByteShift = 16;

    public const uint TaskReset = 0u;
    public const uint TaskValidateMetadata = 1u;
    public const uint TaskGenerate = 2u;
    public const uint TaskValidate = 3u;

    public const uint CacheClear = 0u;
    public const uint InitializeIndices = 1u;
    public const uint RadixHistogram = 2u;
    public const uint RadixPrefix = 3u;
    public const uint RadixScatter = 4u;
    public const uint CompactLocalScan = 5u;
    public const uint CompactGroupPrefix = 6u;
    public const uint CompactScatter = 7u;
    public const uint StageSortedCells = 8u;
    public const uint ClearCellTableForHash = 9u;
    public const uint HashAndFinalize = 10u;

    public static uint EncodeRadix(uint operation, uint keyIndex, uint byteIndex)
    {
        if (operation is not (RadixHistogram or RadixPrefix or RadixScatter))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (keyIndex >= GiCausticDeterministicBuildScratchLayout.RadixKeyCount)
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        if (byteIndex >= GiCausticDeterministicBuildScratchLayout.RadixBytesPerKey)
            throw new ArgumentOutOfRangeException(nameof(byteIndex));
        return operation |
            (keyIndex << RadixKeyShift) |
            (byteIndex << RadixByteShift);
    }

    public static uint DecodeOperation(uint encoded) => encoded & OperationMask;
    public static uint DecodeRadixKey(uint encoded) =>
        (encoded >> RadixKeyShift) & 0xffu;
    public static uint DecodeRadixByte(uint encoded) =>
        (encoded >> RadixByteShift) & 0x3u;
}

/// <summary>
/// Phase-local packing for task-source counts. These bits are interpreted only
/// by the task generator; cache-build and resolve phases use their own flags.
/// </summary>
public static class GiCausticGpuTaskGenerationFlags
{
    public const int MaximumEmitterCount = 255;
    public const int MaximumHeroCount = 255;
    public const int MaximumProposalPairCount = 65_535;
    private const int HeroShift = 8;
    private const int PairShift = 16;

    public static uint Encode(int emitterCount, int heroCount, int proposalPairCount)
    {
        if (emitterCount is <= 0 or > MaximumEmitterCount)
            throw new ArgumentOutOfRangeException(nameof(emitterCount));
        if (heroCount is <= 0 or > MaximumHeroCount)
            throw new ArgumentOutOfRangeException(nameof(heroCount));
        if (proposalPairCount is <= 0 or > MaximumProposalPairCount)
            throw new ArgumentOutOfRangeException(nameof(proposalPairCount));
        return (uint)emitterCount |
            ((uint)heroCount << HeroShift) |
            ((uint)proposalPairCount << PairShift);
    }

    public static int DecodeEmitterCount(uint flags) => (int)(flags & 0xffu);
    public static int DecodeHeroCount(uint flags) => (int)((flags >> HeroShift) & 0xffu);
    public static int DecodeProposalPairCount(uint flags) =>
        (int)((flags >> PairShift) & 0xffffu);
}

/// <summary>Fixed C4 descriptors, intentionally distinct from every DDGI descriptor.</summary>
public readonly record struct GiCausticGpuBindlessSlots(
    int TaskBufferIndex,
    int PhotonBufferIndex,
    int CacheBufferIndex,
    int ScratchBufferIndex)
{
    public void Validate()
    {
        if (TaskBufferIndex != BindlessIndex.GiCausticTaskBuffer ||
            PhotonBufferIndex != BindlessIndex.GiCausticPhotonBuffer ||
            CacheBufferIndex != BindlessIndex.GiCausticCacheBuffer ||
            ScratchBufferIndex != BindlessIndex.GiCausticScratchBuffer ||
            PhotonBufferIndex != TaskBufferIndex + 1 ||
            CacheBufferIndex != PhotonBufferIndex + 1 ||
            ScratchBufferIndex != CacheBufferIndex + 1 ||
            !BindlessIndex.IsStaticBufferIndex(TaskBufferIndex) ||
            !BindlessIndex.IsStaticBufferIndex(PhotonBufferIndex) ||
            !BindlessIndex.IsStaticBufferIndex(CacheBufferIndex) ||
            !BindlessIndex.IsStaticBufferIndex(ScratchBufferIndex))
        {
            throw new InvalidOperationException(
                "C4 requires four consecutive, static, dedicated bindless storage slots.");
        }
    }
}

public enum GiCausticGpuEmitterType : uint
{
    Point = 0,
    Spot = 1,
    DirectionalDisk = 2,
    AreaTriangle = 3,
    EmissiveTriangle = 4
}

[Flags]
public enum GiCausticGpuEmitterFlags : uint
{
    None = 0,
    Valid = 1u << 0,
    TwoSided = 1u << 1,
    DeltaPosition = 1u << 2,
    DeltaDirection = 1u << 3,
    SceneLinearRadiometry = 1u << 4,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuTaskFlags : uint
{
    None = 0,
    AuthoredHero = 1u << 0,
    MirrorHero = 1u << 1,
    ClosedDielectricHero = 1u << 2,
    RoughSpecularReference = 1u << 3,
    ValidatedByTaskPass = 1u << 4,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuPhotonFlags : uint
{
    None = 0,
    SpecularToDiffuse = 1u << 0,
    RefractiveToDiffuse = 1u << 1,
    FirstDiffuseEndpoint = 1u << 2,
    Valid = 1u << 3,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuCellFlags : uint
{
    None = 0,
    Occupied = 1u << 0,
    BuildComplete = 1u << 1,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuCachePublicationFlags : uint
{
    None = 0,
    Initialized = 1u << 0,
    BuildComplete = 1u << 1,
    Invalidated = 1u << 2,
    CandidateOverflow = 1u << 3,
    CellTableOverflow = 1u << 4,
    TaskOverflow = 1u << 5,
    DeterministicBuildBackendUnavailable = 1u << 6,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuResolveRequestFlags : uint
{
    None = 0,
    Valid = 1u << 0,
    OpaqueReceiver = 1u << 1,
    EnergyConservingDiffuseBrdf = 1u << 2,
    Invalid = 1u << 31
}

[Flags]
public enum GiCausticGpuResolveResultFlags : uint
{
    None = 0,
    CacheReadable = 1u << 0,
    CellFound = 1u << 1,
    ContributionValid = 1u << 2,
    NormalRejected = 1u << 3,
    RevisionRejected = 1u << 4,
    RequestRejected = 1u << 5,
    CacheRejected = 1u << 31
}

/// <summary>First 64 bytes of the task queue, written before every transaction.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.TaskDispatchHeaderBytes)]
public struct GPUCausticTaskDispatchHeaderV1
{
    public uint AbiVersion;
    public uint CacheGeneration;
    public uint TaskCount;
    public uint TaskCapacity;
    public uint PhotonWriteBankIndex;
    public uint CacheWriteBankIndex;
    public uint RevisionFingerprintLow;
    public uint RevisionFingerprintHigh;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;
    public uint Reserved6;
}

/// <summary>
/// Frozen, scene-linear emitter metadata consumed by the GPU task generator.
/// Delta emitters use unit PDFs in their discrete position/direction measure;
/// triangle emitters use world-area and solid-angle densities.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.EmitterRecordBytes)]
public struct GPUCausticEmitterV1
{
    public uint AbiVersion;
    public uint StableSourceId;
    public GiCausticGpuEmitterType Type;
    public GiCausticGpuEmitterFlags Flags;
    /// <summary>xyz origin/vertex 0; w finite source range or directional disk radius.</summary>
    public Vector4 PositionAndRange;
    /// <summary>xyz normalized emission direction; w outer cone cosine.</summary>
    public Vector4 DirectionAndCosOuter;
    /// <summary>xyz radiant intensity/irradiance/radiance; w positive proposal weight.</summary>
    public Vector4 RadiometricValueAndSelectionWeight;
    /// <summary>xyz triangle edge 1; w exact world-space triangle area.</summary>
    public Vector4 Edge1AndArea;
    /// <summary>xyz triangle edge 2; w inner cone cosine.</summary>
    public Vector4 Edge2AndCosInner;
    /// <summary>xyz normalized triangle normal; w canonical/target mixture probability.</summary>
    public Vector4 NormalAndTargetingMix;
    public uint ContentRevisionLow;
    public uint ContentRevisionHigh;
    public uint Reserved0;
    public uint Reserved1;

    public readonly bool IsValid
    {
        get
        {
            const GiCausticGpuEmitterFlags required =
                GiCausticGpuEmitterFlags.Valid |
                GiCausticGpuEmitterFlags.SceneLinearRadiometry;
            if (AbiVersion != GiCausticGpuAbi.Version || StableSourceId == 0u ||
                !Enum.IsDefined(Type) || (Flags & required) != required ||
                (Flags & GiCausticGpuEmitterFlags.Invalid) != 0 ||
                ContentRevisionLow == 0u && ContentRevisionHigh == 0u ||
                Reserved0 != 0u || Reserved1 != 0u ||
                !Finite(PositionAndRange) || !Finite(DirectionAndCosOuter) ||
                !Finite(RadiometricValueAndSelectionWeight) ||
                !Finite(Edge1AndArea) || !Finite(Edge2AndCosInner) ||
                !Finite(NormalAndTargetingMix) ||
                PositionAndRange.W <= 0.0f ||
                RadiometricValueAndSelectionWeight.X < 0.0f ||
                RadiometricValueAndSelectionWeight.Y < 0.0f ||
                RadiometricValueAndSelectionWeight.Z < 0.0f ||
                RadiometricValueAndSelectionWeight.X +
                    RadiometricValueAndSelectionWeight.Y +
                    RadiometricValueAndSelectionWeight.Z <= 0.0f ||
                RadiometricValueAndSelectionWeight.W <= 0.0f ||
                NormalAndTargetingMix.W < 0.0f ||
                NormalAndTargetingMix.W > 0.95f)
            {
                return false;
            }

            return Type switch
            {
                GiCausticGpuEmitterType.Point =>
                    (Flags & GiCausticGpuEmitterFlags.DeltaPosition) != 0,
                GiCausticGpuEmitterType.Spot =>
                    (Flags & GiCausticGpuEmitterFlags.DeltaPosition) != 0 &&
                    UnitLength(DirectionAndCosOuter) &&
                    DirectionAndCosOuter.W is >= -1.0f and < 1.0f &&
                    Edge2AndCosInner.W >= DirectionAndCosOuter.W &&
                    Edge2AndCosInner.W <= 1.0f,
                GiCausticGpuEmitterType.DirectionalDisk =>
                    (Flags & GiCausticGpuEmitterFlags.DeltaDirection) != 0 &&
                    UnitLength(DirectionAndCosOuter),
                GiCausticGpuEmitterType.AreaTriangle or
                    GiCausticGpuEmitterType.EmissiveTriangle =>
                    Edge1AndArea.W > 0.0f && UnitLength(NormalAndTargetingMix),
                _ => false
            };
        }
    }

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool UnitLength(Vector4 value)
    {
        float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
        return lengthSquared is > 0.999f and < 1.001f;
    }
}

/// <summary>One author-validated current-pose tagged hero used by task generation.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.HeroRecordBytes)]
public struct GPUCausticHeroV1
{
    public uint AbiVersion;
    public uint StableHeroId;
    public uint MaterialRevision;
    public GiCausticGpuTaskFlags Flags;
    public Vector4 BoundsCenterAndRadius;
    public Vector4 BoundsMinimumAndConeRadius;
    public Vector4 BoundsMaximumAndConeSpread;
    /// <summary>x IOR, y roughness, z reserved, w reserved.</summary>
    public Vector4 HeroOptics;
    /// <summary>xyz absorption coefficient; w maximum tagged path distance.</summary>
    public Vector4 AbsorptionAndMaximumDistance;
    /// <summary>x proposal relationship multiplier; remaining components reserved.</summary>
    public Vector4 ProposalWeightAndReserved;
    public uint GeometryRevisionLow;
    public uint GeometryRevisionHigh;
    public uint TransformRevisionLow;
    public uint TransformRevisionHigh;

    public readonly bool IsValid
    {
        get
        {
            GiCausticGpuTaskFlags mode = Flags &
                (GiCausticGpuTaskFlags.MirrorHero |
                 GiCausticGpuTaskFlags.ClosedDielectricHero |
                 GiCausticGpuTaskFlags.RoughSpecularReference);
            return AbiVersion == GiCausticGpuAbi.Version &&
                StableHeroId != 0u && MaterialRevision != 0u &&
                (Flags & GiCausticGpuTaskFlags.AuthoredHero) != 0 &&
                (Flags & GiCausticGpuTaskFlags.Invalid) == 0 &&
                mode != GiCausticGpuTaskFlags.None &&
                (mode & (GiCausticGpuTaskFlags)((uint)mode - 1u)) == 0 &&
                Finite(BoundsCenterAndRadius) && BoundsCenterAndRadius.W > 0.0f &&
                Finite(BoundsMinimumAndConeRadius) &&
                BoundsMinimumAndConeRadius.W > 0.0f &&
                Finite(BoundsMaximumAndConeSpread) &&
                BoundsMaximumAndConeSpread.W >= 0.0f &&
                BoundsMinimumAndConeRadius.X <= BoundsMaximumAndConeSpread.X &&
                BoundsMinimumAndConeRadius.Y <= BoundsMaximumAndConeSpread.Y &&
                BoundsMinimumAndConeRadius.Z <= BoundsMaximumAndConeSpread.Z &&
                Finite(HeroOptics) && HeroOptics.X > 0.0f &&
                HeroOptics.X <= 4.0f && HeroOptics.Y >= 0.0f &&
                HeroOptics.Y <= 1.0f &&
                Finite(AbsorptionAndMaximumDistance) &&
                AbsorptionAndMaximumDistance.X >= 0.0f &&
                AbsorptionAndMaximumDistance.Y >= 0.0f &&
                AbsorptionAndMaximumDistance.Z >= 0.0f &&
                AbsorptionAndMaximumDistance.W > 0.0f &&
                Finite(ProposalWeightAndReserved) &&
                ProposalWeightAndReserved.X > 0.0f &&
                ProposalWeightAndReserved.Y == 0.0f &&
                ProposalWeightAndReserved.Z == 0.0f &&
                ProposalWeightAndReserved.W == 0.0f &&
                (GeometryRevisionLow != 0u || GeometryRevisionHigh != 0u) &&
                (TransformRevisionLow != 0u || TransformRevisionHigh != 0u);
        }
    }

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>
/// One exact two-level proposal entry. Entries are ordered by upper CDF and
/// retain both factors so the generated task can audit the complete joint PDF.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.ProposalPairRecordBytes)]
public struct GPUCausticProposalPairV1
{
    public uint EmitterIndex;
    public uint HeroIndex;
    public uint StablePairId;
    public uint Reserved;
    public float EmitterPdf;
    public float CasterGivenEmitterPdf;
    public float CdfUpper;
    public float TargetingMixtureProbability;

    public readonly bool IsValidFor(int emitterCount, int heroCount) =>
        EmitterIndex < (uint)emitterCount && HeroIndex < (uint)heroCount &&
        StablePairId != 0u && Reserved == 0u &&
        float.IsFinite(EmitterPdf) && EmitterPdf > 0.0f && EmitterPdf <= 1.0f &&
        float.IsFinite(CasterGivenEmitterPdf) &&
        CasterGivenEmitterPdf > 0.0f && CasterGivenEmitterPdf <= 1.0f &&
        float.IsFinite(CdfUpper) && CdfUpper > 0.0f && CdfUpper <= 1.0f &&
        float.IsFinite(TargetingMixtureProbability) &&
        TargetingMixtureProbability >= 0.0f &&
        TargetingMixtureProbability <= 0.95f;
}

/// <summary>
/// One bounded, authored photon path request.  Task generation only validates
/// and stamps these inputs; it never invents a hero material or light path.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.TaskRecordBytes)]
public struct GPUCausticPhotonTaskV1
{
    public uint AbiVersion;
    public uint CacheGeneration;
    public uint StableTaskIdLow;
    public uint StableTaskIdHigh;
    public uint HeroInstanceId;
    public uint HeroMaterialRevisionLow;
    public uint SourceId;
    public GiCausticGpuTaskFlags Flags;
    /// <summary>xyz source point; w source-selection PDF.</summary>
    public Vector4 OriginAndSelectionPdf;
    /// <summary>xyz initial direction; w conditional/path PDF.</summary>
    public Vector4 DirectionAndPathPdf;
    /// <summary>xyz emitted radiometric contribution; w emitter-position PDF.</summary>
    public Vector4 EmittedContributionAndPositionPdf;
    /// <summary>xyz audited initial flux; w full direction-mixture PDF.</summary>
    public Vector4 InitialFluxAndDirectionPdf;
    /// <summary>x IOR, y roughness, z initial cone radius, w cone spread.</summary>
    public Vector4 HeroOptics;
    /// <summary>xyz Beer-Lambert absorption coefficient; w maximum path distance.</summary>
    public Vector4 AbsorptionAndMaximumDistance;

    public readonly bool IsInputValid(uint expectedGeneration, uint taskCount)
    {
        GiCausticGpuTaskFlags heroFlags = Flags &
            (GiCausticGpuTaskFlags.MirrorHero |
             GiCausticGpuTaskFlags.ClosedDielectricHero |
             GiCausticGpuTaskFlags.RoughSpecularReference);
        Vector3 direction = new(
            DirectionAndPathPdf.X,
            DirectionAndPathPdf.Y,
            DirectionAndPathPdf.Z);
        return AbiVersion == GiCausticGpuAbi.Version &&
            CacheGeneration == expectedGeneration &&
            (StableTaskIdLow != 0u || StableTaskIdHigh != 0u) &&
            HeroInstanceId != 0u &&
            HeroMaterialRevisionLow != 0u &&
            SourceId != 0u &&
            taskCount > 0u &&
            heroFlags != GiCausticGpuTaskFlags.None &&
            (heroFlags & (GiCausticGpuTaskFlags)((uint)heroFlags - 1u)) == 0 &&
            (Flags & GiCausticGpuTaskFlags.AuthoredHero) != 0 &&
            (Flags & GiCausticGpuTaskFlags.Invalid) == 0 &&
            IsFinitePositive(OriginAndSelectionPdf.W) &&
            IsFinitePositive(DirectionAndPathPdf.W) &&
            IsFinitePositive(EmittedContributionAndPositionPdf.W) &&
            IsFinitePositive(InitialFluxAndDirectionPdf.W) &&
            IsFinite(OriginAndSelectionPdf.X) &&
            IsFinite(OriginAndSelectionPdf.Y) &&
            IsFinite(OriginAndSelectionPdf.Z) &&
            IsFinite(direction.X) && IsFinite(direction.Y) && IsFinite(direction.Z) &&
            direction.LengthSquared() > 0.999f && direction.LengthSquared() < 1.001f &&
            IsFiniteNonNegative(EmittedContributionAndPositionPdf.X) &&
            IsFiniteNonNegative(EmittedContributionAndPositionPdf.Y) &&
            IsFiniteNonNegative(EmittedContributionAndPositionPdf.Z) &&
            IsFiniteNonNegative(InitialFluxAndDirectionPdf.X) &&
            IsFiniteNonNegative(InitialFluxAndDirectionPdf.Y) &&
            IsFiniteNonNegative(InitialFluxAndDirectionPdf.Z) &&
            IsFinitePositive(HeroOptics.X) && HeroOptics.X <= 4.0f &&
            IsFiniteNonNegative(HeroOptics.Y) && HeroOptics.Y <= 1.0f &&
            IsFinitePositive(HeroOptics.Z) &&
            IsFiniteNonNegative(HeroOptics.W) &&
            IsFiniteNonNegative(AbsorptionAndMaximumDistance.X) &&
            IsFiniteNonNegative(AbsorptionAndMaximumDistance.Y) &&
            IsFiniteNonNegative(AbsorptionAndMaximumDistance.Z) &&
            IsFinitePositive(AbsorptionAndMaximumDistance.W) &&
            HeroModeIsValid(heroFlags) &&
            InitialFluxMatchesJointPdf(taskCount);
    }

    private readonly bool HeroModeIsValid(GiCausticGpuTaskFlags heroFlags) =>
        heroFlags switch
        {
            GiCausticGpuTaskFlags.MirrorHero => HeroOptics.Y <= 0.04f,
            GiCausticGpuTaskFlags.ClosedDielectricHero =>
                HeroOptics.Y <= 0.04f && HeroOptics.X > 1.0f,
            GiCausticGpuTaskFlags.RoughSpecularReference => HeroOptics.Y > 0.04f,
            _ => false
        };

    private readonly bool InitialFluxMatchesJointPdf(uint taskCount)
    {
        double jointPdf = (double)OriginAndSelectionPdf.W *
            DirectionAndPathPdf.W * EmittedContributionAndPositionPdf.W *
            InitialFluxAndDirectionPdf.W;
        double denominator = taskCount * jointPdf;
        if (!double.IsFinite(denominator) || denominator <= 0.0)
            return false;
        return Matches(InitialFluxAndDirectionPdf.X,
                   EmittedContributionAndPositionPdf.X / denominator) &&
            Matches(InitialFluxAndDirectionPdf.Y,
                EmittedContributionAndPositionPdf.Y / denominator) &&
            Matches(InitialFluxAndDirectionPdf.Z,
                EmittedContributionAndPositionPdf.Z / denominator);
    }

    private static bool Matches(float actual, double expected)
    {
        if (!double.IsFinite(expected) || expected < 0.0 || expected > float.MaxValue)
            return false;
        double tolerance = Math.Max(1.0e-6, Math.Abs(expected) * 2.0e-4);
        return Math.Abs(actual - expected) <= tolerance;
    }

    private static bool IsFinite(float value) => float.IsFinite(value);

    private static bool IsFinitePositive(float value) =>
        float.IsFinite(value) && value > 0.0f;

    private static bool IsFiniteNonNegative(float value) =>
        float.IsFinite(value) && value >= 0.0f;
}

/// <summary>
/// FP32 first-diffuse endpoint emitted by the tagged transport backend.  It
/// mirrors the audited 80-byte reference payload while remaining in a
/// production-only candidate bank.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.PhotonRecordBytes)]
public struct GPUCausticPhotonCandidateV1
{
    public Vector3 WorldPosition;
    public float SupportRadius;
    public Vector3 IncidentFlux;
    public float PathWeightDebug;
    public uint PackedIncidentDirection;
    public uint PackedReceiverNormal;
    public GiCausticGpuPhotonFlags PathTagAndDepth;
    public uint StablePhotonId;
    /// <summary>axis U, axis V, cosine, sine in the receiver tangent plane.</summary>
    public Vector4 TangentPlaneFootprint;
    public uint SourceId;
    /// <summary>Nonzero hash of every admitted specular/interface boundary.</summary>
    public uint PathSignature;
    public uint TransportRevision;
    public uint CacheGeneration;

    public readonly bool IsValidFor(uint expectedGeneration, float maximumSupportRadius)
    {
        return CacheGeneration == expectedGeneration &&
            (PathTagAndDepth & GiCausticGpuPhotonFlags.Valid) != 0 &&
            (PathTagAndDepth & GiCausticGpuPhotonFlags.FirstDiffuseEndpoint) != 0 &&
            (PathTagAndDepth & GiCausticGpuPhotonFlags.Invalid) == 0 &&
            StablePhotonId != 0u && SourceId != 0u && PathSignature != 0u &&
            IsFinite(WorldPosition) && IsFinite(IncidentFlux) &&
            float.IsFinite(SupportRadius) && SupportRadius > 0.0f &&
            SupportRadius <= maximumSupportRadius &&
            float.IsFinite(TangentPlaneFootprint.X) &&
            float.IsFinite(TangentPlaneFootprint.Y) &&
            float.IsFinite(TangentPlaneFootprint.Z) &&
            float.IsFinite(TangentPlaneFootprint.W) &&
            TangentPlaneFootprint.X > 0.0f && TangentPlaneFootprint.Y > 0.0f;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Full signed world-cell key plus a range into one immutable photon bank.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.CellEntryBytes)]
public struct GPUCausticCellEntryV1
{
    public int CellX;
    public int CellY;
    public int CellZ;
    public int Cascade;
    public uint PhotonOffset;
    public uint PhotonCount;
    public uint CacheGeneration;
    public GiCausticGpuCellFlags Flags;
}

/// <summary>
/// Per-cache-bank publication header.  The GPU writes <see cref="PublicationFlags"/>
/// with <see cref="GiCausticGpuCachePublicationFlags.BuildComplete"/> last;
/// the CPU still validates this readback before flipping the readable bank.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.CacheHeaderBytes)]
public struct GPUCausticCacheHeaderV1
{
    public uint AbiVersion;
    public uint CacheGeneration;
    public uint RevisionFingerprintLow;
    public uint RevisionFingerprintHigh;
    public uint TaskCapacity;
    public uint PhotonCapacity;
    public uint PhotonRecordStrideBytes;
    public uint CellTableCapacity;
    public uint MaximumPhotonsPerCell;
    public uint CandidateCount;
    public uint RetainedPhotonCount;
    public uint OccupiedCellCount;
    public uint OverflowCount;
    public GiCausticGpuCachePublicationFlags PublicationFlags;
    public uint BuildSerial;
    public uint CacheBankIndex;
    /// <summary>xyz world-cell origin; w strictly positive cell size.</summary>
    public Vector4 CellOriginAndSize;
    public uint PhotonBankIndex;
    public uint CandidateInputCount;
    public uint TransportAbiVersion;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;
    public uint Reserved6;
    public uint Reserved7;
    public uint Reserved8;

    public readonly ulong RevisionFingerprint =>
        ((ulong)RevisionFingerprintHigh << 32) | RevisionFingerprintLow;

    public readonly bool IsBuildComplete =>
        (PublicationFlags & GiCausticGpuCachePublicationFlags.BuildComplete) != 0;

    public readonly bool IsOverflowed => OverflowCount != 0u ||
        (PublicationFlags & (GiCausticGpuCachePublicationFlags.CandidateOverflow |
                             GiCausticGpuCachePublicationFlags.CellTableOverflow |
                             GiCausticGpuCachePublicationFlags.TaskOverflow)) != 0;
}

/// <summary>Input record for the isolated C4 resolve kernel.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.ResolveRequestBytes)]
public struct GPUCausticResolveRequestV1
{
    public Vector3 WorldPosition;
    public float MaximumDistance;
    public uint PackedReceiverNormal;
    public uint ExpectedCacheGeneration;
    public uint OutputIndex;
    public GiCausticGpuResolveRequestFlags Flags;
    /// <summary>
    /// Current scene-linear, energy-conserving diffuse BRDF. Photon records
    /// intentionally retain incident flux rather than baked receiver color.
    /// </summary>
    public Vector3 DiffuseBrdf;
    public float MinimumNormalCosine;
    public uint StableReceiverId;
    public uint MaterialRevision;
    public uint TransportRevision;
    public uint Reserved;

    public readonly bool IsValidFor(
        uint expectedCacheGeneration,
        uint expectedTransportRevision,
        uint maximumOutputCount)
    {
        const GiCausticGpuResolveRequestFlags required =
            GiCausticGpuResolveRequestFlags.Valid |
            GiCausticGpuResolveRequestFlags.OpaqueReceiver |
            GiCausticGpuResolveRequestFlags.EnergyConservingDiffuseBrdf;
        return float.IsFinite(WorldPosition.X) &&
            float.IsFinite(WorldPosition.Y) &&
            float.IsFinite(WorldPosition.Z) &&
            float.IsFinite(MaximumDistance) && MaximumDistance > 0.0f &&
            ExpectedCacheGeneration == expectedCacheGeneration &&
            OutputIndex < maximumOutputCount &&
            (Flags & required) == required &&
            (Flags & GiCausticGpuResolveRequestFlags.Invalid) == 0 &&
            float.IsFinite(DiffuseBrdf.X) && DiffuseBrdf.X >= 0.0f &&
            float.IsFinite(DiffuseBrdf.Y) && DiffuseBrdf.Y >= 0.0f &&
            float.IsFinite(DiffuseBrdf.Z) && DiffuseBrdf.Z >= 0.0f &&
            float.IsFinite(MinimumNormalCosine) &&
            MinimumNormalCosine >= 0.0f && MinimumNormalCosine <= 1.0f &&
            StableReceiverId != 0u && MaterialRevision != 0u &&
            TransportRevision == expectedTransportRevision &&
            Reserved == 0u;
    }
}

/// <summary>
/// Isolated C4 resolve result.  A presentation/composite adapter may add this
/// tagged result later, but the workload never writes DDGI source data.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.ResolveResultBytes)]
public struct GPUCausticResolveResultV1
{
    public Vector3 Radiance;
    public float Confidence;
    public uint CacheGeneration;
    public GiCausticGpuResolveResultFlags Flags;
    public uint PhotonCount;
    public uint RejectedPhotonCount;
    /// <summary>First and second luminance moments of accepted contributions.</summary>
    public Vector2 LuminanceMoments;
    public float MaximumKernelWeight;
    public uint Reserved;
}

/// <summary>Shared 128-byte push block for all C4 compute kernels.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = GiCausticGpuAbi.PushConstantsBytes)]
public struct GPUCausticPushConstantsV1
{
    public uint AbiVersion;
    public uint TaskBufferIndex;
    public uint PhotonBufferIndex;
    public uint CacheBufferIndex;
    public uint ScratchBufferIndex;
    public uint TaskCount;
    public uint PhotonCapacity;
    public uint PhotonRecordStrideWords;
    public uint CellTableCapacity;
    public uint MaximumPhotonsPerCell;
    public uint CacheGeneration;
    public uint RevisionFingerprintLow;
    public uint RevisionFingerprintHigh;
    public uint CandidateStagingWordOffset;
    public uint CachePhotonBankBaseWord;
    public uint PhotonReadBankIndex;
    public uint PhotonWriteBankIndex;
    public uint CacheReadBankIndex;
    public uint CacheWriteBankIndex;
    public uint CacheBankHeaderWordOffset;
    public uint CacheBankTableWordOffset;
    public uint ScratchWordCapacity;
    public uint Flags;
    public uint BuildPhase;
    public uint ResolveRequestWordOffset;
    public uint ResolveRequestCount;
    // std430 aligns the trailing vec4 to 16 bytes. Keep these explicit so the
    // managed push block has the same 112-byte vector offset as GLSL.
    public uint TransportAbiVersion;
    public uint MaximumOccupiedCells;
    public Vector4 CellOriginAndSize;
}
