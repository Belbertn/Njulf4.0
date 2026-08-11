using Njulf.Assets.Cooked;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Fixed, schema-friendly histogram for the complete EXT four-state
/// subdivision domain. A fixed value type avoids mutable arrays in diagnostic
/// snapshots and keeps older captures deterministic.
/// </summary>
public readonly record struct OpacityMicromapSubdivisionHistogram(
    ulong Level0,
    ulong Level1,
    ulong Level2,
    ulong Level3,
    ulong Level4,
    ulong Level5,
    ulong Level6,
    ulong Level7,
    ulong Level8,
    ulong Level9,
    ulong Level10,
    ulong Level11,
    ulong Level12,
    ulong Level13,
    ulong Level14,
    ulong Level15)
{
    public static OpacityMicromapSubdivisionHistogram Empty { get; } = default;

    public static OpacityMicromapSubdivisionHistogram Create(
        ReadOnlySpan<ulong> counts)
    {
        if (counts.Length !=
            OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel +
            1)
        {
            throw new ArgumentException(
                "The OMM subdivision histogram must contain levels 0 through 15.",
                nameof(counts));
        }

        return new OpacityMicromapSubdivisionHistogram(
            counts[0], counts[1], counts[2], counts[3],
            counts[4], counts[5], counts[6], counts[7],
            counts[8], counts[9], counts[10], counts[11],
            counts[12], counts[13], counts[14], counts[15]);
    }

    public ulong GetCount(uint subdivisionLevel) => subdivisionLevel switch
    {
        0 => Level0,
        1 => Level1,
        2 => Level2,
        3 => Level3,
        4 => Level4,
        5 => Level5,
        6 => Level6,
        7 => Level7,
        8 => Level8,
        9 => Level9,
        10 => Level10,
        11 => Level11,
        12 => Level12,
        13 => Level13,
        14 => Level14,
        15 => Level15,
        _ => throw new ArgumentOutOfRangeException(nameof(subdivisionLevel))
    };

    public bool TryGetTotal(out ulong total)
    {
        try
        {
            total = checked(
                Level0 + Level1 + Level2 + Level3 +
                Level4 + Level5 + Level6 + Level7 +
                Level8 + Level9 + Level10 + Level11 +
                Level12 + Level13 + Level14 + Level15);
            return true;
        }
        catch (OverflowException)
        {
            total = 0UL;
            return false;
        }
    }
}

/// <summary>
/// Fence-independent, immutable C1 content evidence cached when the renderer
/// accepts a new registration generation. Counts are over unique immutable
/// variants, while <see cref="RegisteredMeshCount"/> records rigid mesh owners.
/// </summary>
public readonly record struct OpacityMicromapContentDiagnostics(
    bool Authoritative,
    int RegisteredMeshCount,
    int UniqueVariantCount,
    ulong RejectedRegistrationCount,
    int StaleMaterialRegistrationCount,
    int AmbiguousContentKeyCount,
    ulong PrimitiveCount,
    ulong MaterialContractCount,
    ulong OmmDataBytes,
    ulong IndexBytes,
    ulong DescriptorBytes,
    int ClassifiedPayloadCount,
    int UnclassifiedPayloadCount,
    ulong OpaqueMicrotriangleCount,
    ulong TransparentMicrotriangleCount,
    ulong UnknownOpaqueMicrotriangleCount,
    ulong UnknownTransparentMicrotriangleCount,
    uint MaximumSubdivisionLevel,
    OpacityMicromapSubdivisionHistogram SubdivisionHistogram,
    string Detail)
{
    public static OpacityMicromapContentDiagnostics Unavailable { get; } = new(
        Authoritative: false,
        RegisteredMeshCount: 0,
        UniqueVariantCount: 0,
        RejectedRegistrationCount: 0UL,
        StaleMaterialRegistrationCount: 0,
        AmbiguousContentKeyCount: 0,
        PrimitiveCount: 0UL,
        MaterialContractCount: 0UL,
        OmmDataBytes: 0UL,
        IndexBytes: 0UL,
        DescriptorBytes: 0UL,
        ClassifiedPayloadCount: 0,
        UnclassifiedPayloadCount: 0,
        OpaqueMicrotriangleCount: 0UL,
        TransparentMicrotriangleCount: 0UL,
        UnknownOpaqueMicrotriangleCount: 0UL,
        UnknownTransparentMicrotriangleCount: 0UL,
        MaximumSubdivisionLevel: 0U,
        SubdivisionHistogram: OpacityMicromapSubdivisionHistogram.Empty,
        Detail: "opacity-micromap-content-diagnostics-unavailable");

    public ulong PayloadBytes => checked(
        OmmDataBytes + IndexBytes + DescriptorBytes);

    public ulong KnownMicrotriangleCount => checked(
        OpaqueMicrotriangleCount + TransparentMicrotriangleCount);

    public ulong UnknownMicrotriangleCount => checked(
        UnknownOpaqueMicrotriangleCount +
        UnknownTransparentMicrotriangleCount);

    public ulong ClassifiedMicrotriangleCount => checked(
        KnownMicrotriangleCount + UnknownMicrotriangleCount);

    public double KnownCoverage => ClassifiedMicrotriangleCount == 0UL
        ? 0.0
        : (double)KnownMicrotriangleCount / ClassifiedMicrotriangleCount;

    public bool IsValid
    {
        get
        {
            if (RegisteredMeshCount < 0 || UniqueVariantCount < 0 ||
                UniqueVariantCount > RegisteredMeshCount ||
                StaleMaterialRegistrationCount < 0 ||
                AmbiguousContentKeyCount < 0 ||
                ClassifiedPayloadCount < 0 ||
                UnclassifiedPayloadCount < 0 ||
                ClassifiedPayloadCount + UnclassifiedPayloadCount !=
                    UniqueVariantCount ||
                MaximumSubdivisionLevel >
                    OpacityMicromapSubdivisionPolicy
                        .AbsoluteMaximumSubdivisionLevel ||
                string.IsNullOrWhiteSpace(Detail) ||
                !SubdivisionHistogram.TryGetTotal(out _))
            {
                return false;
            }

            try
            {
                _ = PayloadBytes;
                _ = KnownMicrotriangleCount;
                _ = UnknownMicrotriangleCount;
                _ = ClassifiedMicrotriangleCount;
                return Authoritative || this == Unavailable;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }

    public OpacityMicromapContentDiagnostics NormalizeForPersistence() =>
        IsValid
            ? this with { Detail = Detail.Trim() }
            : Unavailable;
}
