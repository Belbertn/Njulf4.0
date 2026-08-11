using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Fixed limits for an optional OMM chunk.  These are enforced before any
/// attacker-controlled count is converted to an allocation size.
/// </summary>
public readonly record struct OpacityMicromapPayloadLimits(
    int MaximumChunkBytes,
    int MaximumPrimitiveCount,
    int MaximumMaterialContracts,
    int MaximumUsageHistogramEntries,
    int MaximumOmmDataBytes,
    int MaximumIndexBytes,
    int MaximumDescriptorBytes)
{
    public static OpacityMicromapPayloadLimits Default { get; } = new(
        MaximumChunkBytes: 512 * 1024 * 1024,
        MaximumPrimitiveCount: 16_777_216,
        MaximumMaterialContracts: 65_536,
        MaximumUsageHistogramEntries: 4_096,
        MaximumOmmDataBytes: 384 * 1024 * 1024,
        MaximumIndexBytes: 96 * 1024 * 1024,
        MaximumDescriptorBytes: 64 * 1024 * 1024);

    internal OpacityMicromapPayloadLimits Normalize() => this == default ? Default : this;

    internal bool IsSane =>
        MaximumChunkBytes >= OpacityMicromapCookedPayloadCodec.HeaderBytes &&
        MaximumPrimitiveCount > 0 &&
        MaximumMaterialContracts > 0 &&
        MaximumUsageHistogramEntries > 0 &&
        MaximumOmmDataBytes > 0 &&
        MaximumIndexBytes > 0 &&
        MaximumDescriptorBytes > 0;
}

public readonly record struct OpacityMicromapUsage(
    OpacityMicromapFormat Format,
    uint SubdivisionLevel,
    ulong Count);

public readonly record struct OpacityMicromapClassificationStatistics(
    ulong Opaque,
    ulong Transparent,
    ulong UnknownOpaque,
    ulong UnknownTransparent)
{
    public bool TryGetTotal(out ulong total)
    {
        try
        {
            total = checked(checked(Opaque + Transparent) +
                checked(UnknownOpaque + UnknownTransparent));
            return true;
        }
        catch (OverflowException)
        {
            total = 0;
            return false;
        }
    }
}

/// <summary>
/// Immutable in-memory representation of the optional, backend-specific EXT
/// payload.  Byte spans are copied at creation so a caller cannot mutate a
/// payload after it has been validated and scheduled for upload.
/// </summary>
public sealed class OpacityMicromapCookedPayload
{
    private readonly OpacityMicromapMaterialContract[] _materialContracts;
    private readonly ReadOnlyCollection<OpacityMicromapMaterialContract>
        _materialContractView;
    private readonly OpacityMicromapUsage[] _usageHistogram;
    private readonly ReadOnlyCollection<OpacityMicromapUsage> _usageHistogramView;
    private readonly byte[] _ommData;
    private readonly byte[] _indexData;
    private readonly byte[] _descriptorData;

    private OpacityMicromapCookedPayload(
        uint cookAbi,
        OpacityMicromapContentKey sourceContentHash,
        OpacityMicromapContentKey sdkProvenanceHash,
        uint maximumSubdivisionLevel,
        uint primitiveCount,
        uint descriptorCount,
        OpacityMicromapMaterialContract[] materialContracts,
        OpacityMicromapUsage[] usageHistogram,
        byte[] ommData,
        byte[] indexData,
        byte[] descriptorData,
        OpacityMicromapClassificationStatistics? classificationStatistics)
    {
        CookAbi = cookAbi;
        SourceContentHash = sourceContentHash;
        SdkProvenanceHash = sdkProvenanceHash;
        MaximumSubdivisionLevel = maximumSubdivisionLevel;
        PrimitiveCount = primitiveCount;
        DescriptorCount = descriptorCount;
        _materialContracts = materialContracts;
        _materialContractView = Array.AsReadOnly(_materialContracts);
        _usageHistogram = usageHistogram;
        _usageHistogramView = Array.AsReadOnly(_usageHistogram);
        _ommData = ommData;
        _indexData = indexData;
        _descriptorData = descriptorData;
        ClassificationStatistics = classificationStatistics;
    }

    public uint CookAbi { get; }
    public OpacityMicromapContentKey SourceContentHash { get; }
    public OpacityMicromapContentKey SdkProvenanceHash { get; }
    public OpacityMicromapPayloadKind PayloadKind => OpacityMicromapPayloadKind.VulkanExtFourState;
    public OpacityMicromapFormat Format => OpacityMicromapFormat.FourState;
    public uint MaximumSubdivisionLevel { get; }
    public uint PrimitiveCount { get; }
    public uint DescriptorCount { get; }
    public IReadOnlyList<OpacityMicromapMaterialContract> MaterialContracts =>
        _materialContractView;
    public IReadOnlyList<OpacityMicromapUsage> UsageHistogram => _usageHistogramView;
    public ReadOnlyMemory<byte> OmmData => _ommData;
    public ReadOnlyMemory<byte> IndexData => _indexData;
    public ReadOnlyMemory<byte> DescriptorData => _descriptorData;
    public OpacityMicromapClassificationStatistics? ClassificationStatistics { get; }

    public static OpacityMicromapCookedPayload Create(
        uint cookAbi,
        OpacityMicromapContentKey sourceContentHash,
        OpacityMicromapContentKey sdkProvenanceHash,
        uint maximumSubdivisionLevel,
        uint primitiveCount,
        uint descriptorCount,
        ReadOnlySpan<OpacityMicromapMaterialContract> materialContracts,
        ReadOnlySpan<OpacityMicromapUsage> usageHistogram,
        ReadOnlySpan<byte> ommData,
        ReadOnlySpan<byte> indexData,
        ReadOnlySpan<byte> descriptorData,
        OpacityMicromapClassificationStatistics? classificationStatistics = null,
        OpacityMicromapPayloadLimits limits = default)
    {
        limits = limits.Normalize();
        ValidateCreateInputs(
            cookAbi,
            sourceContentHash,
            sdkProvenanceHash,
            maximumSubdivisionLevel,
            primitiveCount,
            descriptorCount,
            materialContracts,
            usageHistogram,
            ommData,
            indexData,
            descriptorData,
            classificationStatistics,
            limits);
        var payload = new OpacityMicromapCookedPayload(
            cookAbi,
            sourceContentHash,
            sdkProvenanceHash,
            maximumSubdivisionLevel,
            primitiveCount,
            descriptorCount,
            materialContracts.ToArray(),
            usageHistogram.ToArray(),
            ommData.ToArray(),
            indexData.ToArray(),
            descriptorData.ToArray(),
            classificationStatistics);
        OpacityMicromapCookedPayloadCodec.EnsureEncodable(payload, limits);
        return payload;
    }

    private static void ValidateCreateInputs(
        uint cookAbi,
        OpacityMicromapContentKey sourceContentHash,
        OpacityMicromapContentKey sdkProvenanceHash,
        uint maximumSubdivisionLevel,
        uint primitiveCount,
        uint descriptorCount,
        ReadOnlySpan<OpacityMicromapMaterialContract> materialContracts,
        ReadOnlySpan<OpacityMicromapUsage> usageHistogram,
        ReadOnlySpan<byte> ommData,
        ReadOnlySpan<byte> indexData,
        ReadOnlySpan<byte> descriptorData,
        OpacityMicromapClassificationStatistics? classificationStatistics,
        OpacityMicromapPayloadLimits limits)
    {
        if (!limits.IsSane || cookAbi == 0 || sourceContentHash.IsZero || sdkProvenanceHash.IsZero ||
            maximumSubdivisionLevel == 0 ||
            maximumSubdivisionLevel > OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel ||
            primitiveCount == 0 || primitiveCount > limits.MaximumPrimitiveCount ||
            descriptorCount == 0 ||
            materialContracts.IsEmpty || materialContracts.Length > limits.MaximumMaterialContracts ||
            usageHistogram.IsEmpty || usageHistogram.Length > limits.MaximumUsageHistogramEntries ||
            ommData.IsEmpty || ommData.Length > limits.MaximumOmmDataBytes ||
            indexData.IsEmpty || indexData.Length > limits.MaximumIndexBytes ||
            descriptorData.IsEmpty || descriptorData.Length > limits.MaximumDescriptorBytes ||
            descriptorCount > descriptorData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(materialContracts), "OMM payload creation inputs are out of bounds.");
        }
        if (classificationStatistics is { } statistics && !statistics.TryGetTotal(out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classificationStatistics),
                "OMM classification statistics overflow.");
        }

        try
        {
            ulong cursor = OpacityMicromapCookedPayloadCodec.HeaderBytes;
            cursor = AdvanceAligned(cursor, checked((ulong)materialContracts.Length *
                OpacityMicromapCookedPayloadCodec.MaterialContractBytes));
            cursor = AdvanceAligned(cursor, checked((ulong)ommData.Length));
            cursor = AdvanceAligned(cursor, checked((ulong)indexData.Length));
            cursor = AdvanceAligned(cursor, checked((ulong)descriptorData.Length));
            cursor = AdvanceAligned(cursor, checked((ulong)usageHistogram.Length *
                OpacityMicromapCookedPayloadCodec.UsageHistogramEntryBytes));
            cursor = AdvanceAligned(
                cursor,
                classificationStatistics.HasValue
                    ? OpacityMicromapCookedPayloadCodec.ClassificationStatisticsBytes
                    : 0UL);
            cursor = Align8(cursor);
            if (cursor > checked((ulong)limits.MaximumChunkBytes))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ommData),
                    "OMM payload creation inputs exceed the total chunk cap.");
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "OMM payload creation size overflowed.",
                exception);
        }
    }

    private static ulong AdvanceAligned(ulong cursor, ulong bytes) =>
        checked(Align8(cursor) + bytes);

    private static ulong Align8(ulong value) => checked((value + 7UL) & ~7UL);
}

public enum OpacityMicromapPayloadValidationFailure : byte
{
    None = 0,
    LimitsInvalid,
    ChunkTooSmall,
    ChunkTooLarge,
    MagicMismatch,
    SchemaVersionUnsupported,
    HeaderSizeMismatch,
    EndiannessMismatch,
    ReservedHeaderBitsSet,
    PayloadKindUnsupported,
    FormatUnsupported,
    CookAbiInvalid,
    ContentHashInvalid,
    SubdivisionInvalid,
    CountInvalid,
    SpanOutOfRange,
    SpanLayoutInvalid,
    SpanSizeInvalid,
    SpanChecksumMismatch,
    MaterialContractInvalid,
    UsageHistogramInvalid,
    ClassificationStatisticsInvalid,
    ModelAttachmentInvalid,
    TrailingOrMissingBytes
}

public readonly record struct OpacityMicromapPayloadReadResult(
    bool Success,
    OpacityMicromapCookedPayload? Payload,
    OpacityMicromapPayloadValidationFailure Failure,
    string Detail)
{
    public static OpacityMicromapPayloadReadResult Rejected(
        OpacityMicromapPayloadValidationFailure failure,
        string detail) => new(false, null, failure, detail);
}

/// <summary>
/// Versioned binary codec for the optional OMM chunk.  The base model does not
/// depend on this data; every parse failure is intentionally bounded and lets
/// the renderer select its ordinary alpha-candidate BLAS immediately.
/// </summary>
public static class OpacityMicromapCookedPayloadCodec
{
    public const uint CurrentSchemaVersion = 1;
    public const uint LittleEndianMarker = 0x0102_0304u;
    public const int HeaderBytes = 416;
    public const int MaterialContractBytes = 172;
    public const int UsageHistogramEntryBytes = 16;
    public const int ClassificationStatisticsBytes = 32;

    private const int MagicBytes = 8;
    private const int SpanEntryBytes = 48;
    private const int SpanDirectoryOffset = 128;
    private const int SpanCount = 6;
    private const int HeaderMagicOffset = 0;
    private const int HeaderSchemaOffset = 8;
    private const int HeaderSizeOffset = 12;
    private const int HeaderEndiannessOffset = 16;
    private const int HeaderPayloadKindOffset = 20;
    private const int HeaderFormatOffset = 21;
    private const int HeaderReservedOffset = 22;
    private const int HeaderCookAbiOffset = 24;
    private const int HeaderSourceHashOffset = 28;
    private const int HeaderProvenanceHashOffset = 60;
    private const int HeaderMaxSubdivisionOffset = 92;
    private const int HeaderPrimitiveCountOffset = 96;
    private const int HeaderMaterialCountOffset = 100;
    private const int HeaderDescriptorCountOffset = 104;
    private const int HeaderUsageCountOffset = 108;
    private const int HeaderFlagsOffset = 112;
    private const int HeaderTotalBytesOffset = 116;

    private static ReadOnlySpan<byte> Magic => "NJOMM001"u8;

    private enum SpanKind
    {
        Materials = 0,
        OmmData = 1,
        IndexData = 2,
        DescriptorData = 3,
        UsageHistogram = 4,
        ClassificationStatistics = 5
    }

    private readonly record struct EncodedSpan(
        ulong Offset,
        ulong Length,
        OpacityMicromapContentKey Checksum);

    public static byte[] Write(
        OpacityMicromapCookedPayload payload,
        OpacityMicromapPayloadLimits limits = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        limits = limits.Normalize();
        EnsureEncodable(payload, limits);

        byte[] materials = EncodeMaterials(payload.MaterialContracts);
        byte[] usage = EncodeUsageHistogram(payload.UsageHistogram);
        byte[] statistics = EncodeClassificationStatistics(payload.ClassificationStatistics);

        ulong cursor = HeaderBytes;
        EncodedSpan materialSpan = CreateSpan(ref cursor, materials);
        EncodedSpan ommSpan = CreateSpan(ref cursor, payload.OmmData.Span);
        EncodedSpan indexSpan = CreateSpan(ref cursor, payload.IndexData.Span);
        EncodedSpan descriptorSpan = CreateSpan(ref cursor, payload.DescriptorData.Span);
        EncodedSpan usageSpan = CreateSpan(ref cursor, usage);
        EncodedSpan statisticsSpan = CreateSpan(ref cursor, statistics);
        cursor = Align8(cursor);
        if (cursor > checked((ulong)limits.MaximumChunkBytes) || cursor > int.MaxValue)
        {
            throw new InvalidOperationException("OMM encoded chunk exceeds its bounded schema limit.");
        }

        byte[] bytes = new byte[checked((int)cursor)];
        WriteHeader(
            bytes,
            payload,
            materialSpan,
            ommSpan,
            indexSpan,
            descriptorSpan,
            usageSpan,
            statisticsSpan);
        materials.CopyTo(bytes.AsSpan(checked((int)materialSpan.Offset)));
        payload.OmmData.Span.CopyTo(bytes.AsSpan(checked((int)ommSpan.Offset)));
        payload.IndexData.Span.CopyTo(bytes.AsSpan(checked((int)indexSpan.Offset)));
        payload.DescriptorData.Span.CopyTo(bytes.AsSpan(checked((int)descriptorSpan.Offset)));
        usage.CopyTo(bytes.AsSpan(checked((int)usageSpan.Offset)));
        statistics.CopyTo(bytes.AsSpan(checked((int)statisticsSpan.Offset)));
        return bytes;
    }

    public static OpacityMicromapPayloadReadResult TryRead(
        ReadOnlySpan<byte> bytes,
        OpacityMicromapPayloadLimits limits = default)
    {
        limits = limits.Normalize();
        if (!limits.IsSane)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.LimitsInvalid, "payload-limits-invalid");
        }
        if (bytes.Length < HeaderBytes)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.ChunkTooSmall, "payload-header-truncated");
        }
        if (bytes.Length > limits.MaximumChunkBytes)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.ChunkTooLarge, "payload-exceeds-bounded-chunk-size");
        }
        if (!bytes.Slice(HeaderMagicOffset, MagicBytes).SequenceEqual(Magic))
            return Reject(OpacityMicromapPayloadValidationFailure.MagicMismatch, "payload-magic-mismatch");
        if (ReadUInt32(bytes, HeaderSchemaOffset) != CurrentSchemaVersion)
        {
            return Reject(
                OpacityMicromapPayloadValidationFailure.SchemaVersionUnsupported,
                "payload-schema-unsupported");
        }
        if (ReadUInt32(bytes, HeaderSizeOffset) != HeaderBytes)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.HeaderSizeMismatch, "payload-header-size-mismatch");
        }
        if (ReadUInt32(bytes, HeaderEndiannessOffset) != LittleEndianMarker)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.EndiannessMismatch, "payload-endianness-mismatch");
        }
        if (ReadUInt16(bytes, HeaderReservedOffset) != 0 ||
            ReadUInt32(bytes, HeaderFlagsOffset) != 0 ||
            !bytes.Slice(124, 4).SequenceEqual(stackalloc byte[4]))
        {
            return Reject(OpacityMicromapPayloadValidationFailure.ReservedHeaderBitsSet, "payload-reserved-header-bits-set");
        }
        if ((OpacityMicromapPayloadKind)bytes[HeaderPayloadKindOffset] !=
            OpacityMicromapPayloadKind.VulkanExtFourState)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.PayloadKindUnsupported, "payload-kind-not-vulkan-ext-four-state");
        }
        if ((OpacityMicromapFormat)bytes[HeaderFormatOffset] != OpacityMicromapFormat.FourState)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.FormatUnsupported, "payload-format-not-four-state");
        }

        uint cookAbi = ReadUInt32(bytes, HeaderCookAbiOffset);
        uint maximumSubdivision = ReadUInt32(bytes, HeaderMaxSubdivisionOffset);
        uint primitiveCount = ReadUInt32(bytes, HeaderPrimitiveCountOffset);
        uint materialCount = ReadUInt32(bytes, HeaderMaterialCountOffset);
        uint descriptorCount = ReadUInt32(bytes, HeaderDescriptorCountOffset);
        uint usageCount = ReadUInt32(bytes, HeaderUsageCountOffset);
        ulong totalBytes = ReadUInt64(bytes, HeaderTotalBytesOffset);
        if (cookAbi == 0)
            return Reject(OpacityMicromapPayloadValidationFailure.CookAbiInvalid, "payload-cook-abi-zero");
        if (maximumSubdivision == 0 || maximumSubdivision > OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.SubdivisionInvalid, "payload-subdivision-invalid");
        }
        if (primitiveCount == 0 || primitiveCount > limits.MaximumPrimitiveCount ||
            descriptorCount == 0 ||
            materialCount == 0 || materialCount > limits.MaximumMaterialContracts ||
            usageCount == 0 || usageCount > limits.MaximumUsageHistogramEntries)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.CountInvalid, "payload-count-out-of-bounds");
        }
        if (totalBytes != checked((ulong)bytes.Length))
        {
            return Reject(OpacityMicromapPayloadValidationFailure.TrailingOrMissingBytes, "payload-total-byte-count-mismatch");
        }

        OpacityMicromapContentKey sourceHash = ReadContentKey(bytes, HeaderSourceHashOffset);
        OpacityMicromapContentKey provenanceHash = ReadContentKey(bytes, HeaderProvenanceHashOffset);
        if (sourceHash.IsZero || provenanceHash.IsZero)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.ContentHashInvalid, "payload-source-or-provenance-hash-zero");
        }

        if (!TryReadSpans(
                bytes,
                limits,
                materialCount,
                descriptorCount,
                usageCount,
                out EncodedSpan[] spans,
                out OpacityMicromapPayloadReadResult failure))
            return failure;

        ReadOnlySpan<byte> materialBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.Materials].Offset),
            checked((int)spans[(int)SpanKind.Materials].Length));
        ReadOnlySpan<byte> ommBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.OmmData].Offset),
            checked((int)spans[(int)SpanKind.OmmData].Length));
        ReadOnlySpan<byte> indexBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.IndexData].Offset),
            checked((int)spans[(int)SpanKind.IndexData].Length));
        ReadOnlySpan<byte> descriptorBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.DescriptorData].Offset),
            checked((int)spans[(int)SpanKind.DescriptorData].Length));
        ReadOnlySpan<byte> usageBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.UsageHistogram].Offset),
            checked((int)spans[(int)SpanKind.UsageHistogram].Length));
        ReadOnlySpan<byte> statisticsBytes = bytes.Slice(
            checked((int)spans[(int)SpanKind.ClassificationStatistics].Offset),
            checked((int)spans[(int)SpanKind.ClassificationStatistics].Length));

        if (!ChecksumMatches(materialBytes, spans[(int)SpanKind.Materials].Checksum) ||
            !ChecksumMatches(ommBytes, spans[(int)SpanKind.OmmData].Checksum) ||
            !ChecksumMatches(indexBytes, spans[(int)SpanKind.IndexData].Checksum) ||
            !ChecksumMatches(descriptorBytes, spans[(int)SpanKind.DescriptorData].Checksum) ||
            !ChecksumMatches(usageBytes, spans[(int)SpanKind.UsageHistogram].Checksum) ||
            !ChecksumMatches(statisticsBytes, spans[(int)SpanKind.ClassificationStatistics].Checksum))
        {
            return Reject(OpacityMicromapPayloadValidationFailure.SpanChecksumMismatch, "payload-span-checksum-mismatch");
        }

        if (!TryDecodeMaterials(
                materialBytes,
                materialCount,
                primitiveCount,
                out OpacityMicromapMaterialContract[] materials))
        {
            return Reject(OpacityMicromapPayloadValidationFailure.MaterialContractInvalid, "payload-material-contract-invalid");
        }
        if (!TryDecodeUsageHistogram(
                usageBytes,
                usageCount,
                maximumSubdivision,
                checked((ulong)limits.MaximumPrimitiveCount),
                out OpacityMicromapUsage[] usage))
        {
            return Reject(OpacityMicromapPayloadValidationFailure.UsageHistogramInvalid, "payload-usage-histogram-invalid");
        }
        if (!TryDecodeClassificationStatistics(
                statisticsBytes,
                out OpacityMicromapClassificationStatistics? statistics))
        {
            return Reject(
                OpacityMicromapPayloadValidationFailure.ClassificationStatisticsInvalid,
                "payload-classification-statistics-invalid");
        }

        try
        {
            OpacityMicromapCookedPayload payload = OpacityMicromapCookedPayload.Create(
                cookAbi,
                sourceHash,
                provenanceHash,
                maximumSubdivision,
                primitiveCount,
                descriptorCount,
                materials,
                usage,
                ommBytes,
                indexBytes,
                descriptorBytes,
                statistics,
                limits);
            return new OpacityMicromapPayloadReadResult(true, payload, default, "payload-valid");
        }
        catch (ArgumentException)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.MaterialContractInvalid, "payload-contract-creation-rejected");
        }
        catch (InvalidOperationException)
        {
            return Reject(OpacityMicromapPayloadValidationFailure.SpanSizeInvalid, "payload-bounds-rejected");
        }
    }

    internal static void EnsureEncodable(
        OpacityMicromapCookedPayload payload,
        OpacityMicromapPayloadLimits limits)
    {
        if (!limits.IsSane)
            throw new ArgumentOutOfRangeException(nameof(limits), "OMM payload limits are invalid.");
        if (payload.CookAbi == 0 || payload.SourceContentHash.IsZero || payload.SdkProvenanceHash.IsZero)
            throw new ArgumentException("OMM payload identity fields are invalid.", nameof(payload));
        if (payload.MaximumSubdivisionLevel == 0 ||
            payload.MaximumSubdivisionLevel > OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "OMM payload subdivision is invalid.");
        }
        if (payload.PrimitiveCount == 0 || payload.PrimitiveCount > limits.MaximumPrimitiveCount ||
            payload.DescriptorCount == 0 ||
            payload.MaterialContracts.Count == 0 ||
            payload.MaterialContracts.Count > limits.MaximumMaterialContracts ||
            payload.UsageHistogram.Count == 0 ||
            payload.UsageHistogram.Count > limits.MaximumUsageHistogramEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "OMM payload counts are invalid.");
        }
        if (payload.OmmData.IsEmpty || payload.OmmData.Length > limits.MaximumOmmDataBytes ||
            payload.IndexData.IsEmpty || payload.IndexData.Length > limits.MaximumIndexBytes ||
            payload.DescriptorData.IsEmpty ||
            payload.DescriptorData.Length > limits.MaximumDescriptorBytes ||
            payload.DescriptorCount > payload.DescriptorData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "OMM payload span bytes are invalid.");
        }
        foreach (OpacityMicromapMaterialContract material in payload.MaterialContracts)
        {
            if (material.PrimitiveCount == 0 ||
                checked((ulong)material.FirstPrimitive + material.PrimitiveCount) > payload.PrimitiveCount ||
                !material.IsExactStaticMaskContract)
            {
                throw new ArgumentException(
                    "OMM material contract is not eligible for the exact static C1 profile.",
                    nameof(payload));
            }
        }
        var seenUsage = new HashSet<(OpacityMicromapFormat Format, uint Subdivision)>();
        foreach (OpacityMicromapUsage usage in payload.UsageHistogram)
        {
            if (usage.Format != OpacityMicromapFormat.FourState || usage.Count == 0 ||
                usage.Count > checked((ulong)limits.MaximumPrimitiveCount) ||
                usage.SubdivisionLevel > payload.MaximumSubdivisionLevel ||
                !seenUsage.Add((usage.Format, usage.SubdivisionLevel)))
            {
                throw new ArgumentException("OMM usage histogram is invalid.", nameof(payload));
            }
        }
        if (payload.ClassificationStatistics is { } statistics && !statistics.TryGetTotal(out _))
        {
            throw new ArgumentException("OMM classification statistics overflow.", nameof(payload));
        }
    }

    private static bool TryReadSpans(
        ReadOnlySpan<byte> bytes,
        OpacityMicromapPayloadLimits limits,
        uint materialCount,
        uint descriptorCount,
        uint usageCount,
        out EncodedSpan[] spans,
        out OpacityMicromapPayloadReadResult failure)
    {
        spans = new EncodedSpan[SpanCount];
        failure = default;
        ulong expectedOffset = HeaderBytes;
        for (int index = 0; index < SpanCount; index++)
        {
            int offset = SpanDirectoryOffset + index * SpanEntryBytes;
            ulong spanOffset = ReadUInt64(bytes, offset);
            ulong spanLength = ReadUInt64(bytes, offset + 8);
            OpacityMicromapContentKey checksum = ReadContentKey(bytes, offset + 16);
            if (spanOffset != expectedOffset || spanOffset > checked((ulong)bytes.Length) ||
                spanLength > checked((ulong)bytes.Length) - spanOffset)
            {
                failure = Reject(OpacityMicromapPayloadValidationFailure.SpanLayoutInvalid, "payload-span-layout-invalid");
                return false;
            }
            spans[index] = new EncodedSpan(spanOffset, spanLength, checksum);
            try
            {
                ulong spanEnd = checked(spanOffset + spanLength);
                expectedOffset = Align8(spanEnd);
                if (expectedOffset > checked((ulong)bytes.Length) ||
                    !PaddingIsZero(bytes.Slice(
                        checked((int)spanEnd),
                        checked((int)(expectedOffset - spanEnd)))))
                {
                    failure = Reject(
                        OpacityMicromapPayloadValidationFailure.SpanLayoutInvalid,
                        "payload-span-padding-is-not-zero");
                    return false;
                }
            }
            catch (OverflowException)
            {
                failure = Reject(OpacityMicromapPayloadValidationFailure.SpanOutOfRange, "payload-span-offset-overflow");
                return false;
            }
        }
        if (expectedOffset != checked((ulong)bytes.Length))
        {
            failure = Reject(OpacityMicromapPayloadValidationFailure.TrailingOrMissingBytes, "payload-span-end-does-not-match-file-size");
            return false;
        }
        if (spans[(int)SpanKind.Materials].Length !=
            checked((ulong)materialCount * MaterialContractBytes) ||
            spans[(int)SpanKind.UsageHistogram].Length !=
            checked((ulong)usageCount * UsageHistogramEntryBytes) ||
            spans[(int)SpanKind.OmmData].Length == 0 ||
            spans[(int)SpanKind.OmmData].Length > checked((ulong)limits.MaximumOmmDataBytes) ||
            spans[(int)SpanKind.IndexData].Length == 0 ||
            spans[(int)SpanKind.IndexData].Length > checked((ulong)limits.MaximumIndexBytes) ||
            spans[(int)SpanKind.DescriptorData].Length == 0 ||
            spans[(int)SpanKind.DescriptorData].Length > checked((ulong)limits.MaximumDescriptorBytes) ||
            descriptorCount > spans[(int)SpanKind.DescriptorData].Length ||
            spans[(int)SpanKind.ClassificationStatistics].Length is not 0 and
                not ClassificationStatisticsBytes)
        {
            failure = Reject(OpacityMicromapPayloadValidationFailure.SpanSizeInvalid, "payload-span-size-invalid");
            return false;
        }
        return true;
    }

    private static EncodedSpan CreateSpan(ref ulong cursor, ReadOnlySpan<byte> bytes)
    {
        cursor = Align8(cursor);
        ulong offset = cursor;
        cursor = checked(cursor + checked((ulong)bytes.Length));
        return new EncodedSpan(offset, checked((ulong)bytes.Length), ComputeChecksum(bytes));
    }

    private static void WriteHeader(
        Span<byte> bytes,
        OpacityMicromapCookedPayload payload,
        EncodedSpan materialSpan,
        EncodedSpan ommSpan,
        EncodedSpan indexSpan,
        EncodedSpan descriptorSpan,
        EncodedSpan usageSpan,
        EncodedSpan statisticsSpan)
    {
        Magic.CopyTo(bytes);
        WriteUInt32(bytes, HeaderSchemaOffset, CurrentSchemaVersion);
        WriteUInt32(bytes, HeaderSizeOffset, HeaderBytes);
        WriteUInt32(bytes, HeaderEndiannessOffset, LittleEndianMarker);
        bytes[HeaderPayloadKindOffset] = (byte)OpacityMicromapPayloadKind.VulkanExtFourState;
        bytes[HeaderFormatOffset] = (byte)OpacityMicromapFormat.FourState;
        WriteUInt32(bytes, HeaderCookAbiOffset, payload.CookAbi);
        WriteContentKey(bytes, HeaderSourceHashOffset, payload.SourceContentHash);
        WriteContentKey(bytes, HeaderProvenanceHashOffset, payload.SdkProvenanceHash);
        WriteUInt32(bytes, HeaderMaxSubdivisionOffset, payload.MaximumSubdivisionLevel);
        WriteUInt32(bytes, HeaderPrimitiveCountOffset, payload.PrimitiveCount);
        WriteUInt32(bytes, HeaderMaterialCountOffset, checked((uint)payload.MaterialContracts.Count));
        WriteUInt32(bytes, HeaderDescriptorCountOffset, payload.DescriptorCount);
        WriteUInt32(bytes, HeaderUsageCountOffset, checked((uint)payload.UsageHistogram.Count));
        WriteUInt64(bytes, HeaderTotalBytesOffset, checked((ulong)bytes.Length));

        WriteSpan(bytes, SpanKind.Materials, materialSpan);
        WriteSpan(bytes, SpanKind.OmmData, ommSpan);
        WriteSpan(bytes, SpanKind.IndexData, indexSpan);
        WriteSpan(bytes, SpanKind.DescriptorData, descriptorSpan);
        WriteSpan(bytes, SpanKind.UsageHistogram, usageSpan);
        WriteSpan(bytes, SpanKind.ClassificationStatistics, statisticsSpan);
    }

    private static void WriteSpan(Span<byte> bytes, SpanKind kind, EncodedSpan span)
    {
        int offset = SpanDirectoryOffset + (int)kind * SpanEntryBytes;
        WriteUInt64(bytes, offset, span.Offset);
        WriteUInt64(bytes, offset + 8, span.Length);
        WriteContentKey(bytes, offset + 16, span.Checksum);
    }

    private static byte[] EncodeMaterials(IReadOnlyList<OpacityMicromapMaterialContract> materials)
    {
        byte[] result = new byte[checked(materials.Count * MaterialContractBytes)];
        for (int index = 0; index < materials.Count; index++)
            WriteMaterial(result.AsSpan(index * MaterialContractBytes, MaterialContractBytes), materials[index]);
        return result;
    }

    private static void WriteMaterial(Span<byte> bytes, OpacityMicromapMaterialContract material)
    {
        int offset = 0;
        WriteUInt32(bytes, ref offset, material.MaterialSlot);
        WriteUInt32(bytes, ref offset, material.FirstPrimitive);
        WriteUInt32(bytes, ref offset, material.PrimitiveCount);
        WriteInt32(bytes, ref offset, material.TexCoordSet);
        WriteUvTransform(bytes, ref offset, material.UvTransform);
        WriteContentKey(bytes, ref offset, material.TextureContentHash);
        WriteContentKey(bytes, ref offset, material.TextureFormatAndMipHash);
        WriteSampler(bytes, ref offset, material.Sampler);
        WriteUInt32(bytes, ref offset, material.MaterialAlphaBits);
        WriteUInt32(bytes, ref offset, material.UniformVertexAlphaBits);
        WriteUInt32(bytes, ref offset, material.AlphaCutoffBits);
        WriteUInt32(bytes, ref offset, material.FixedLodBits);
        WriteUInt32(bytes, ref offset, material.AlphaContractRevision);
        WriteUInt32(bytes, ref offset, material.ShaderAbiRevision);
        bytes[offset++] = 0; // reserved; keeps the fixed record 32-bit aligned.
        if (offset != MaterialContractBytes)
            throw new InvalidOperationException("OMM material binary layout changed without a schema update.");
    }

    private static bool TryDecodeMaterials(
        ReadOnlySpan<byte> bytes,
        uint count,
        uint primitiveCount,
        out OpacityMicromapMaterialContract[] materials)
    {
        try
        {
            materials = new OpacityMicromapMaterialContract[checked((int)count)];
            for (int index = 0; index < materials.Length; index++)
            {
                ReadOnlySpan<byte> materialBytes = bytes.Slice(index * MaterialContractBytes, MaterialContractBytes);
                int offset = 0;
                var material = new OpacityMicromapMaterialContract(
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadInt32(materialBytes, ref offset),
                    ReadUvTransform(materialBytes, ref offset),
                    ReadContentKey(materialBytes, ref offset),
                    ReadContentKey(materialBytes, ref offset),
                    ReadSampler(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset),
                    ReadUInt32(materialBytes, ref offset));
                bool reservedByteClear = materialBytes[offset++] == 0;
                if (offset != MaterialContractBytes || !reservedByteClear || material.PrimitiveCount == 0 ||
                    checked((ulong)material.FirstPrimitive + material.PrimitiveCount) > primitiveCount ||
                    !material.IsExactStaticMaskContract)
                {
                    materials = Array.Empty<OpacityMicromapMaterialContract>();
                    return false;
                }
                materials[index] = material;
            }
            return true;
        }
        catch (ArgumentException)
        {
            materials = Array.Empty<OpacityMicromapMaterialContract>();
            return false;
        }
        catch (InvalidDataException)
        {
            materials = Array.Empty<OpacityMicromapMaterialContract>();
            return false;
        }
    }

    private static byte[] EncodeUsageHistogram(IReadOnlyList<OpacityMicromapUsage> usage)
    {
        byte[] result = new byte[checked(usage.Count * UsageHistogramEntryBytes)];
        for (int index = 0; index < usage.Count; index++)
        {
            int offset = index * UsageHistogramEntryBytes;
            WriteUInt32(result, offset, (uint)usage[index].Format);
            WriteUInt32(result, offset + 4, usage[index].SubdivisionLevel);
            WriteUInt64(result, offset + 8, usage[index].Count);
        }
        return result;
    }

    private static bool TryDecodeUsageHistogram(
        ReadOnlySpan<byte> bytes,
        uint count,
        uint maximumSubdivision,
        ulong maximumUsageCount,
        out OpacityMicromapUsage[] usage)
    {
        usage = new OpacityMicromapUsage[checked((int)count)];
        var seen = new HashSet<(OpacityMicromapFormat Format, uint Subdivision)>();
        for (int index = 0; index < usage.Length; index++)
        {
            int offset = index * UsageHistogramEntryBytes;
            var entry = new OpacityMicromapUsage(
                (OpacityMicromapFormat)ReadUInt32(bytes, offset),
                ReadUInt32(bytes, offset + 4),
                ReadUInt64(bytes, offset + 8));
            if (entry.Format != OpacityMicromapFormat.FourState || entry.Count == 0 ||
                entry.Count > maximumUsageCount ||
                entry.SubdivisionLevel > maximumSubdivision ||
                !seen.Add((entry.Format, entry.SubdivisionLevel)))
            {
                usage = Array.Empty<OpacityMicromapUsage>();
                return false;
            }
            usage[index] = entry;
        }
        return true;
    }

    private static byte[] EncodeClassificationStatistics(
        OpacityMicromapClassificationStatistics? statistics)
    {
        if (statistics is not { } value)
            return Array.Empty<byte>();
        byte[] result = new byte[ClassificationStatisticsBytes];
        WriteUInt64(result, 0, value.Opaque);
        WriteUInt64(result, 8, value.Transparent);
        WriteUInt64(result, 16, value.UnknownOpaque);
        WriteUInt64(result, 24, value.UnknownTransparent);
        return result;
    }

    private static bool TryDecodeClassificationStatistics(
        ReadOnlySpan<byte> bytes,
        out OpacityMicromapClassificationStatistics? statistics)
    {
        if (bytes.IsEmpty)
        {
            statistics = null;
            return true;
        }
        if (bytes.Length != ClassificationStatisticsBytes)
        {
            statistics = null;
            return false;
        }
        var value = new OpacityMicromapClassificationStatistics(
            ReadUInt64(bytes, 0),
            ReadUInt64(bytes, 8),
            ReadUInt64(bytes, 16),
            ReadUInt64(bytes, 24));
        if (!value.TryGetTotal(out _))
        {
            statistics = null;
            return false;
        }
        statistics = value;
        return true;
    }

    private static void WriteUvTransform(
        Span<byte> bytes,
        ref int offset,
        OpacityMicromapUvTransformBits transform)
    {
        WriteUInt32(bytes, ref offset, transform.M00);
        WriteUInt32(bytes, ref offset, transform.M01);
        WriteUInt32(bytes, ref offset, transform.M02);
        WriteUInt32(bytes, ref offset, transform.M10);
        WriteUInt32(bytes, ref offset, transform.M11);
        WriteUInt32(bytes, ref offset, transform.M12);
        WriteUInt32(bytes, ref offset, transform.M20);
        WriteUInt32(bytes, ref offset, transform.M21);
        WriteUInt32(bytes, ref offset, transform.M22);
    }

    private static OpacityMicromapUvTransformBits ReadUvTransform(
        ReadOnlySpan<byte> bytes,
        ref int offset) => new(
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset),
        ReadUInt32(bytes, ref offset));

    private static void WriteSampler(
        Span<byte> bytes,
        ref int offset,
        OpacityMicromapSamplerContract sampler)
    {
        WriteUInt32(bytes, ref offset, sampler.MinFilter);
        WriteUInt32(bytes, ref offset, sampler.MagFilter);
        WriteUInt32(bytes, ref offset, sampler.MipFilter);
        WriteUInt32(bytes, ref offset, sampler.AddressModeU);
        WriteUInt32(bytes, ref offset, sampler.AddressModeV);
        WriteUInt32(bytes, ref offset, sampler.AddressModeW);
        WriteUInt32(bytes, ref offset, sampler.BorderColor);
        bytes[offset++] = sampler.NormalizedCoordinates ? (byte)1 : (byte)0;
        bytes[offset++] = sampler.MatchesDdgiPolicy ? (byte)1 : (byte)0;
        bytes[offset++] = sampler.SdkQualified ? (byte)1 : (byte)0;
    }

    private static OpacityMicromapSamplerContract ReadSampler(
        ReadOnlySpan<byte> bytes,
        ref int offset)
    {
        uint minFilter = ReadUInt32(bytes, ref offset);
        uint magFilter = ReadUInt32(bytes, ref offset);
        uint mipFilter = ReadUInt32(bytes, ref offset);
        uint addressU = ReadUInt32(bytes, ref offset);
        uint addressV = ReadUInt32(bytes, ref offset);
        uint addressW = ReadUInt32(bytes, ref offset);
        uint border = ReadUInt32(bytes, ref offset);
        byte normalized = bytes[offset++];
        byte ddgi = bytes[offset++];
        byte qualified = bytes[offset++];
        if (normalized > 1 || ddgi > 1 || qualified > 1)
            throw new InvalidDataException("OMM sampler booleans are malformed.");
        return new OpacityMicromapSamplerContract(
            minFilter,
            magFilter,
            mipFilter,
            addressU,
            addressV,
            addressW,
            border,
            normalized != 0,
            ddgi != 0,
            qualified != 0);
    }

    private static bool ChecksumMatches(ReadOnlySpan<byte> bytes, OpacityMicromapContentKey expected) =>
        ComputeChecksum(bytes) == expected;

    private static OpacityMicromapContentKey ComputeChecksum(ReadOnlySpan<byte> bytes) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData(bytes));

    private static ulong Align8(ulong value) => checked((value + 7UL) & ~7UL);

    private static bool PaddingIsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
                return false;
        }
        return true;
    }

    private static OpacityMicromapPayloadReadResult Reject(
        OpacityMicromapPayloadValidationFailure failure,
        string detail) => OpacityMicromapPayloadReadResult.Rejected(failure, detail);

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, sizeof(ushort)));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        uint value = ReadUInt32(bytes, offset);
        offset += sizeof(uint);
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset) =>
        unchecked((int)ReadUInt32(bytes, ref offset));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));

    private static OpacityMicromapContentKey ReadContentKey(ReadOnlySpan<byte> bytes, int offset) =>
        OpacityMicromapContentKey.FromSha256(
            bytes.Slice(offset, OpacityMicromapContentKey.ByteLength));

    private static OpacityMicromapContentKey ReadContentKey(ReadOnlySpan<byte> bytes, ref int offset)
    {
        OpacityMicromapContentKey key = ReadContentKey(bytes, offset);
        offset += OpacityMicromapContentKey.ByteLength;
        return key;
    }

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)), value);

    private static void WriteUInt32(Span<byte> bytes, ref int offset, uint value)
    {
        WriteUInt32(bytes, offset, value);
        offset += sizeof(uint);
    }

    private static void WriteInt32(Span<byte> bytes, ref int offset, int value) =>
        WriteUInt32(bytes, ref offset, unchecked((uint)value));

    private static void WriteUInt64(Span<byte> bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)), value);

    private static void WriteContentKey(
        Span<byte> bytes,
        int offset,
        OpacityMicromapContentKey key) =>
        key.CopyTo(bytes.Slice(offset, OpacityMicromapContentKey.ByteLength));

    private static void WriteContentKey(
        Span<byte> bytes,
        ref int offset,
        OpacityMicromapContentKey key)
    {
        WriteContentKey(bytes, offset, key);
        offset += OpacityMicromapContentKey.ByteLength;
    }
}
