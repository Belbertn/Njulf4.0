using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Njulf.Assets.Cooked;

/// <summary>
/// The four states understood by a Vulkan EXT four-state opacity micromap.
/// <para>
/// Unknown states are deliberately not an approximation of the material alpha
/// test.  They retain the ordinary ray-query candidate path, where the frozen
/// DDGI alpha contract remains authoritative.
/// </para>
/// </summary>
public enum OpacityMicromapMicrotriangleState : byte
{
    Opaque = 0,
    Transparent = 1,
    UnknownOpaque = 2,
    UnknownTransparent = 3
}

public enum OpacityMicromapPayloadKind : byte
{
    None = 0,
    VulkanExtFourState = 1,

    // This is an identifier for diagnostics and persisted intent only.  There
    // is intentionally no KHR payload reader or runtime backend in this slice:
    // KHR uses a different object/build/shader contract and cannot reinterpret
    // an EXT payload.
    KhrReserved = 2
}

public enum OpacityMicromapFormat : byte
{
    FourState = 1
}

public enum OpacityMicromapMaterialAlphaMode : byte
{
    Opaque = 0,
    Mask = 1,
    Blend = 2,
    Unknown = byte.MaxValue
}

public enum OpacityMicromapAlphaComparison : byte
{
    GreaterThanOrEqual = 0,
    GreaterThan = 1,
    LessThan = 2,
    Unknown = byte.MaxValue
}

/// <summary>
/// Raw Vulkan sampler policy fields used by the baker and by the DDGI hit
/// shader.  The numeric values are persisted instead of a managed enum so a
/// sampler-policy revision cannot silently remap serialized values.
/// </summary>
public readonly record struct OpacityMicromapSamplerContract(
    uint MinFilter,
    uint MagFilter,
    uint MipFilter,
    uint AddressModeU,
    uint AddressModeV,
    uint AddressModeW,
    uint BorderColor,
    bool NormalizedCoordinates,
    bool MatchesDdgiPolicy,
    bool SdkQualified)
{
    public bool IsExactAndQualified =>
        NormalizedCoordinates && MatchesDdgiPolicy && SdkQualified;

    internal void AppendCanonicalBytes(IncrementalHash hash)
    {
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MinFilter);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MagFilter);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MipFilter);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, AddressModeU);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, AddressModeV);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, AddressModeW);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, BorderColor);
        OpacityMicromapCanonicalHash.AppendBoolean(hash, NormalizedCoordinates);
        OpacityMicromapCanonicalHash.AppendBoolean(hash, MatchesDdgiPolicy);
        OpacityMicromapCanonicalHash.AppendBoolean(hash, SdkQualified);
    }
}

/// <summary>
/// The full 3x3 UV transform expressed as IEEE-754 bit patterns.  The first
/// shipping C1 profile admits only <see cref="Identity"/>, but preserving all
/// bits in the contract prevents a later profile from losing transform identity
/// through float formatting or object hashing.
/// </summary>
public readonly record struct OpacityMicromapUvTransformBits(
    uint M00,
    uint M01,
    uint M02,
    uint M10,
    uint M11,
    uint M12,
    uint M20,
    uint M21,
    uint M22)
{
    public static OpacityMicromapUvTransformBits Identity { get; } = new(
        0x3f80_0000u, 0u, 0u,
        0u, 0x3f80_0000u, 0u,
        0u, 0u, 0x3f80_0000u);

    public bool IsIdentity => this == Identity;

    public bool IsFinite =>
        HasFiniteFloat(M00) && HasFiniteFloat(M01) && HasFiniteFloat(M02) &&
        HasFiniteFloat(M10) && HasFiniteFloat(M11) && HasFiniteFloat(M12) &&
        HasFiniteFloat(M20) && HasFiniteFloat(M21) && HasFiniteFloat(M22);

    internal void AppendCanonicalBytes(IncrementalHash hash)
    {
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M00);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M01);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M02);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M10);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M11);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M12);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M20);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M21);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, M22);
    }

    private static bool HasFiniteFloat(uint bits) =>
        float.IsFinite(BitConverter.Int32BitsToSingle(unchecked((int)bits)));
}

/// <summary>
/// A SHA-256 identity represented as four primitive values.  It has value
/// semantics and never exposes a mutable byte array.
/// </summary>
public readonly struct OpacityMicromapContentKey :
    IEquatable<OpacityMicromapContentKey>,
    IComparable<OpacityMicromapContentKey>
{
    public const int ByteLength = 32;

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly ulong _third;
    private readonly ulong _fourth;

    private OpacityMicromapContentKey(
        ulong first,
        ulong second,
        ulong third,
        ulong fourth)
    {
        _first = first;
        _second = second;
        _third = third;
        _fourth = fourth;
    }

    public static OpacityMicromapContentKey Zero => default;

    public bool IsZero =>
        _first == 0UL && _second == 0UL && _third == 0UL && _fourth == 0UL;

    public static OpacityMicromapContentKey FromSha256(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != ByteLength)
        {
            throw new ArgumentException(
                $"An opacity-micromap content key requires exactly {ByteLength} bytes.",
                nameof(digest));
        }

        return new OpacityMicromapContentKey(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[sizeof(ulong)..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[(2 * sizeof(ulong))..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[(3 * sizeof(ulong))..]));
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < ByteLength)
        {
            throw new ArgumentException(
                $"Destination must contain at least {ByteLength} bytes.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _first);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(ulong)..], _second);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[(2 * sizeof(ulong))..],
            _third);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[(3 * sizeof(ulong))..],
            _fourth);
    }

    public int CompareTo(OpacityMicromapContentKey other)
    {
        int comparison = _first.CompareTo(other._first);
        if (comparison != 0)
            return comparison;
        comparison = _second.CompareTo(other._second);
        if (comparison != 0)
            return comparison;
        comparison = _third.CompareTo(other._third);
        return comparison != 0 ? comparison : _fourth.CompareTo(other._fourth);
    }

    public bool Equals(OpacityMicromapContentKey other) =>
        _first == other._first &&
        _second == other._second &&
        _third == other._third &&
        _fourth == other._fourth;

    public override bool Equals(object? obj) =>
        obj is OpacityMicromapContentKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (int)(_first ^ (_first >> 32) ^ _second ^ (_second >> 32) ^
                         _third ^ (_third >> 32) ^ _fourth ^ (_fourth >> 32));
        }
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        CopyTo(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool operator ==(
        OpacityMicromapContentKey left,
        OpacityMicromapContentKey right) => left.Equals(right);

    public static bool operator !=(
        OpacityMicromapContentKey left,
        OpacityMicromapContentKey right) => !left.Equals(right);
}

/// <summary>
/// All material-side inputs whose exact bits participate in an OMM payload.
/// Instance transforms are deliberately absent: rigid transform-only instances
/// share the same cooked payload and BLAS variant.
/// </summary>
public readonly record struct OpacityMicromapMaterialContract(
    uint MaterialSlot,
    uint FirstPrimitive,
    uint PrimitiveCount,
    int TexCoordSet,
    OpacityMicromapUvTransformBits UvTransform,
    OpacityMicromapContentKey TextureContentHash,
    OpacityMicromapContentKey TextureFormatAndMipHash,
    OpacityMicromapSamplerContract Sampler,
    uint MaterialAlphaBits,
    uint UniformVertexAlphaBits,
    uint AlphaCutoffBits,
    uint FixedLodBits,
    uint AlphaContractRevision,
    uint ShaderAbiRevision)
{
    public bool HasFiniteAlphaInputs =>
        IsFinite(MaterialAlphaBits) &&
        IsFinite(UniformVertexAlphaBits) &&
        IsFinite(AlphaCutoffBits) &&
        IsFinite(FixedLodBits);

    /// <summary>
    /// The schema-level subset that the first shipping C1 profile can ever
    /// attach to an EXT four-state micromap.  This intentionally does not
    /// infer eligibility from a material object: the asset tool must prove the
    /// rest of the static-texture/UV/animation facts separately.  It does keep
    /// malformed persisted contracts from being mistaken for a supported C1
    /// contract after a checksum-valid load.
    /// </summary>
    public bool IsExactStaticMaskContract =>
        PrimitiveCount != 0 &&
        TexCoordSet >= 0 &&
        UvTransform.IsFinite &&
        UvTransform.IsIdentity &&
        !TextureContentHash.IsZero &&
        !TextureFormatAndMipHash.IsZero &&
        Sampler.IsExactAndQualified &&
        IsUnitFinite(MaterialAlphaBits) &&
        IsUnitFinite(UniformVertexAlphaBits) &&
        IsFinite(AlphaCutoffBits) &&
        IsNonNegativeFinite(FixedLodBits) &&
        AlphaContractRevision != 0 &&
        ShaderAbiRevision != 0;

    internal void AppendCanonicalBytes(IncrementalHash hash)
    {
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MaterialSlot);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, FirstPrimitive);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, PrimitiveCount);
        OpacityMicromapCanonicalHash.AppendInt32(hash, TexCoordSet);
        UvTransform.AppendCanonicalBytes(hash);
        OpacityMicromapCanonicalHash.AppendContentKey(hash, TextureContentHash);
        OpacityMicromapCanonicalHash.AppendContentKey(hash, TextureFormatAndMipHash);
        Sampler.AppendCanonicalBytes(hash);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MaterialAlphaBits);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, UniformVertexAlphaBits);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, AlphaCutoffBits);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, FixedLodBits);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, AlphaContractRevision);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, ShaderAbiRevision);
    }

    private static bool IsFinite(uint bits) =>
        float.IsFinite(BitConverter.Int32BitsToSingle(unchecked((int)bits)));

    private static bool IsUnitFinite(uint bits)
    {
        float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return float.IsFinite(value) && value is >= 0.0f and <= 1.0f;
    }

    private static bool IsNonNegativeFinite(uint bits)
    {
        float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        return float.IsFinite(value) && value >= 0.0f;
    }
}

public readonly record struct OpacityMicromapSubdivisionPolicy(
    uint RequestedSubdivisionLevel,
    uint MaximumSubdivisionLevel,
    uint PolicyRevision)
{
    public const uint AbsoluteMaximumSubdivisionLevel = 15;

    public bool IsValid =>
        MaximumSubdivisionLevel > 0 &&
        MaximumSubdivisionLevel <= AbsoluteMaximumSubdivisionLevel &&
        RequestedSubdivisionLevel <= MaximumSubdivisionLevel;

    internal void AppendCanonicalBytes(IncrementalHash hash)
    {
        OpacityMicromapCanonicalHash.AppendUInt32(hash, RequestedSubdivisionLevel);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, MaximumSubdivisionLevel);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, PolicyRevision);
    }
}

/// <summary>
/// The raw canonical content inputs for a payload key.  Byte sequences are
/// hashed immediately by <see cref="OpacityMicromapContentKeyBuilder"/>; this
/// type itself does not retain an ownership claim on caller memory.
/// </summary>
public readonly record struct OpacityMicromapContentKeyInput(
    ReadOnlyMemory<byte> MeshTopologyBytes,
    ReadOnlyMemory<byte> IndexBytes,
    ReadOnlyMemory<byte> UvBytes,
    OpacityMicromapMaterialContract Material,
    ulong TextureResidencyRevision,
    uint CookAbi,
    uint PayloadSchemaVersion,
    OpacityMicromapPayloadKind PayloadKind,
    OpacityMicromapSubdivisionPolicy SubdivisionPolicy)
{
    public const int MaximumCanonicalBlobBytes = 512 * 1024 * 1024;
    public const int MaximumTotalCanonicalBlobBytes =
        MaximumCanonicalBlobBytes;

    public bool IsWithinBounds
    {
        get
        {
            long totalBytes = (long)MeshTopologyBytes.Length +
                              IndexBytes.Length +
                              UvBytes.Length;
            return MeshTopologyBytes.Length <= MaximumCanonicalBlobBytes &&
                   IndexBytes.Length <= MaximumCanonicalBlobBytes &&
                   UvBytes.Length <= MaximumCanonicalBlobBytes &&
                   totalBytes <= MaximumTotalCanonicalBlobBytes;
        }
    }
}

/// <summary>
/// Computes the immutable OMM content identity from length-delimited, raw
/// canonical bytes.  It intentionally has no overload accepting an object or
/// managed <c>GetHashCode()</c> result.
/// </summary>
public static class OpacityMicromapContentKeyBuilder
{
    public const uint CurrentKeyAbi = 1;

    public static OpacityMicromapContentKey Compute(
        in OpacityMicromapContentKeyInput input)
    {
        ValidateInput(input);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("njulf.opacity-micromap.content-key"u8);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, CurrentKeyAbi);
        OpacityMicromapCanonicalHash.AppendBlob(hash, input.MeshTopologyBytes.Span);
        OpacityMicromapCanonicalHash.AppendBlob(hash, input.IndexBytes.Span);
        OpacityMicromapCanonicalHash.AppendBlob(hash, input.UvBytes.Span);
        input.Material.AppendCanonicalBytes(hash);
        OpacityMicromapCanonicalHash.AppendUInt64(hash, input.TextureResidencyRevision);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, input.CookAbi);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, input.PayloadSchemaVersion);
        OpacityMicromapCanonicalHash.AppendByte(hash, (byte)input.PayloadKind);
        input.SubdivisionPolicy.AppendCanonicalBytes(hash);
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static void ValidateInput(in OpacityMicromapContentKeyInput input)
    {
        if (!input.IsWithinBounds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"Canonical OMM input blobs may not exceed " +
                $"{OpacityMicromapContentKeyInput.MaximumCanonicalBlobBytes} bytes each " +
                $"or {OpacityMicromapContentKeyInput.MaximumTotalCanonicalBlobBytes} bytes in total.");
        }
        if (input.MeshTopologyBytes.IsEmpty || input.IndexBytes.IsEmpty || input.UvBytes.IsEmpty)
        {
            throw new ArgumentException(
                "Mesh topology, index, and UV bytes are required for an OMM content key.",
                nameof(input));
        }
        if (input.CookAbi == 0 || input.PayloadSchemaVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(input), "OMM ABI revisions must be non-zero.");
        if (input.PayloadKind != OpacityMicromapPayloadKind.VulkanExtFourState)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "Only the explicitly versioned Vulkan EXT four-state payload can be keyed.");
        }
        if (!input.SubdivisionPolicy.IsValid)
            throw new ArgumentOutOfRangeException(nameof(input), "OMM subdivision policy is invalid.");
        if (input.Material.PrimitiveCount == 0 || input.Material.TexCoordSet < 0)
            throw new ArgumentOutOfRangeException(nameof(input), "OMM material primitive range is invalid.");
        if (!input.Material.HasFiniteAlphaInputs || !input.Material.UvTransform.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(input), "OMM material contains non-finite values.");
    }
}

public enum OpacityMicromapEligibilityFailure : byte
{
    None = 0,
    GeometryNotStatic,
    UvStreamNotStable,
    AlphaModeNotMask,
    AlphaExpressionNotCanonical,
    MaterialAlphaNotFinite,
    MaterialAlphaOutOfRange,
    MaterialAlphaMutable,
    VertexAlphaNotUniform,
    VertexAlphaNotFinite,
    VertexAlphaOutOfRange,
    VertexAlphaMutable,
    TextureNotImmutable,
    TextureDecodeDoesNotMatchRuntime,
    TextureFormatUnsupported,
    TexCoordSetInvalid,
    UvTransformNotFinite,
    UvTransformNotIdentity,
    SamplerPolicyMismatch,
    FixedLodUnavailable,
    MipChainNotFullyResident,
    AlphaCutoffNotFinite,
    AlphaCutoffMutable,
    AlphaComparisonMismatch,
    ThinTransmissionPresent,
    AnimatedMaskPresent,
    ProceduralAlphaPresent,
    PerRayAlphaOverridePresent,
    GeometryOrUvDeforms,
    FourStateFormatUnsupported,
    SubdivisionUnsupported
}

/// <summary>
/// Exact eligibility facts supplied by the asset tool.  Defaults fail closed.
/// A later transformed-UV profile must update the canonical shared UV stream
/// and this contract revision before it can admit non-identity transforms.
/// </summary>
public readonly record struct OpacityMicromapEligibilityInput
{
    public bool StaticTriangleTopology { get; init; }
    public bool StableDdgiUvStream { get; init; }
    public OpacityMicromapMaterialAlphaMode AlphaMode { get; init; }
    public bool UsesCanonicalSingleSampledAlphaExpression { get; init; }
    public uint MaterialAlphaBits { get; init; }
    public bool MaterialAlphaFrozen { get; init; }
    public bool VertexAlphaIsUniform { get; init; }
    public uint UniformVertexAlphaBits { get; init; }
    public bool VertexAlphaFrozen { get; init; }
    public bool ImmutableTextureContent { get; init; }
    public bool ExactRuntimeDecodedAlphaValues { get; init; }
    public bool RuntimeTextureFormatSupported { get; init; }
    public int TexCoordSet { get; init; }
    public OpacityMicromapUvTransformBits UvTransform { get; init; }
    public OpacityMicromapSamplerContract Sampler { get; init; }
    public bool FixedDdgiLod { get; init; }
    public bool CompleteResidentMipData { get; init; }
    public uint AlphaCutoffBits { get; init; }
    public bool AlphaCutoffFrozen { get; init; }
    public OpacityMicromapAlphaComparison AlphaComparison { get; init; }
    public bool ThinTransmissionAbsent { get; init; }
    public bool AnimatedMaskAbsent { get; init; }
    public bool ProceduralAlphaAbsent { get; init; }
    public bool PerRayAlphaOverrideAbsent { get; init; }
    public bool GeometryAndUvsDoNotDeform { get; init; }
    public bool FourStateFormatSupported { get; init; }
    public uint RequestedSubdivisionLevel { get; init; }
    public uint MaximumFourStateSubdivisionLevel { get; init; }

    public static OpacityMicromapEligibilityInput ExactStaticMask { get; } = new()
    {
        StaticTriangleTopology = true,
        StableDdgiUvStream = true,
        AlphaMode = OpacityMicromapMaterialAlphaMode.Mask,
        UsesCanonicalSingleSampledAlphaExpression = true,
        MaterialAlphaBits = 0x3f80_0000u,
        MaterialAlphaFrozen = true,
        VertexAlphaIsUniform = true,
        UniformVertexAlphaBits = 0x3f80_0000u,
        VertexAlphaFrozen = true,
        ImmutableTextureContent = true,
        ExactRuntimeDecodedAlphaValues = true,
        RuntimeTextureFormatSupported = true,
        TexCoordSet = 0,
        UvTransform = OpacityMicromapUvTransformBits.Identity,
        Sampler = new OpacityMicromapSamplerContract(
            MinFilter: 1,
            MagFilter: 1,
            MipFilter: 1,
            AddressModeU: 0,
            AddressModeV: 0,
            AddressModeW: 0,
            BorderColor: 0,
            NormalizedCoordinates: true,
            MatchesDdgiPolicy: true,
            SdkQualified: true),
        FixedDdgiLod = true,
        CompleteResidentMipData = true,
        AlphaCutoffBits = 0x3f00_0000u,
        AlphaCutoffFrozen = true,
        AlphaComparison = OpacityMicromapAlphaComparison.GreaterThanOrEqual,
        ThinTransmissionAbsent = true,
        AnimatedMaskAbsent = true,
        ProceduralAlphaAbsent = true,
        PerRayAlphaOverrideAbsent = true,
        GeometryAndUvsDoNotDeform = true,
        FourStateFormatSupported = true,
        RequestedSubdivisionLevel = 1,
        MaximumFourStateSubdivisionLevel = 1
    };
}

public readonly record struct OpacityMicromapEligibility(
    bool Eligible,
    OpacityMicromapEligibilityFailure Failure,
    string Detail)
{
    public static OpacityMicromapEligibility Rejected(
        OpacityMicromapEligibilityFailure failure,
        string detail) => new(false, failure, detail);

    public static OpacityMicromapEligibility Accepted { get; } =
        new(true, OpacityMicromapEligibilityFailure.None, "eligible-exact-static-mask");
}

public static class OpacityMicromapEligibilityEvaluator
{
    public static OpacityMicromapEligibility Evaluate(
        in OpacityMicromapEligibilityInput input)
    {
        if (!input.StaticTriangleTopology)
            return Reject(OpacityMicromapEligibilityFailure.GeometryNotStatic, "geometry-not-static");
        if (!input.GeometryAndUvsDoNotDeform)
            return Reject(OpacityMicromapEligibilityFailure.GeometryOrUvDeforms, "geometry-or-uv-deforms");
        if (!input.StableDdgiUvStream)
            return Reject(OpacityMicromapEligibilityFailure.UvStreamNotStable, "ddgi-uv-stream-not-stable");
        if (input.AlphaMode != OpacityMicromapMaterialAlphaMode.Mask)
            return Reject(OpacityMicromapEligibilityFailure.AlphaModeNotMask, "material-alpha-mode-not-mask");
        if (!input.UsesCanonicalSingleSampledAlphaExpression)
            return Reject(OpacityMicromapEligibilityFailure.AlphaExpressionNotCanonical, "alpha-expression-not-canonical");

        if (!TryUnitFinite(input.MaterialAlphaBits, out _))
        {
            return Reject(
                float.IsFinite(ToFloat(input.MaterialAlphaBits))
                    ? OpacityMicromapEligibilityFailure.MaterialAlphaOutOfRange
                    : OpacityMicromapEligibilityFailure.MaterialAlphaNotFinite,
                "material-alpha-not-finite-or-unit-range");
        }
        if (!input.MaterialAlphaFrozen)
            return Reject(OpacityMicromapEligibilityFailure.MaterialAlphaMutable, "material-alpha-runtime-mutable");
        if (!input.VertexAlphaIsUniform)
            return Reject(OpacityMicromapEligibilityFailure.VertexAlphaNotUniform, "vertex-alpha-varies");
        if (!TryUnitFinite(input.UniformVertexAlphaBits, out _))
        {
            return Reject(
                float.IsFinite(ToFloat(input.UniformVertexAlphaBits))
                    ? OpacityMicromapEligibilityFailure.VertexAlphaOutOfRange
                    : OpacityMicromapEligibilityFailure.VertexAlphaNotFinite,
                "vertex-alpha-not-finite-or-unit-range");
        }
        if (!input.VertexAlphaFrozen)
            return Reject(OpacityMicromapEligibilityFailure.VertexAlphaMutable, "vertex-alpha-runtime-mutable");
        if (!input.ImmutableTextureContent)
            return Reject(OpacityMicromapEligibilityFailure.TextureNotImmutable, "alpha-texture-not-immutable");
        if (!input.ExactRuntimeDecodedAlphaValues)
            return Reject(OpacityMicromapEligibilityFailure.TextureDecodeDoesNotMatchRuntime, "alpha-texture-decode-mismatch");
        if (!input.RuntimeTextureFormatSupported)
            return Reject(OpacityMicromapEligibilityFailure.TextureFormatUnsupported, "alpha-texture-format-unsupported");
        if (input.TexCoordSet < 0)
            return Reject(OpacityMicromapEligibilityFailure.TexCoordSetInvalid, "texcoord-set-invalid");
        if (!input.UvTransform.IsFinite)
            return Reject(OpacityMicromapEligibilityFailure.UvTransformNotFinite, "uv-transform-not-finite");
        if (!input.UvTransform.IsIdentity)
            return Reject(OpacityMicromapEligibilityFailure.UvTransformNotIdentity, "uv-transform-not-identity");
        if (!input.Sampler.IsExactAndQualified)
            return Reject(OpacityMicromapEligibilityFailure.SamplerPolicyMismatch, "sampler-policy-mismatch");
        if (!input.FixedDdgiLod)
            return Reject(OpacityMicromapEligibilityFailure.FixedLodUnavailable, "ddgi-fixed-lod-unavailable");
        if (!input.CompleteResidentMipData)
            return Reject(OpacityMicromapEligibilityFailure.MipChainNotFullyResident, "alpha-mips-not-fully-resident");

        float cutoff = ToFloat(input.AlphaCutoffBits);
        if (!float.IsFinite(cutoff))
            return Reject(OpacityMicromapEligibilityFailure.AlphaCutoffNotFinite, "alpha-cutoff-not-finite");
        if (!input.AlphaCutoffFrozen)
            return Reject(OpacityMicromapEligibilityFailure.AlphaCutoffMutable, "alpha-cutoff-runtime-mutable");
        if (input.AlphaComparison != OpacityMicromapAlphaComparison.GreaterThanOrEqual)
            return Reject(OpacityMicromapEligibilityFailure.AlphaComparisonMismatch, "alpha-comparison-must-be-greater-than-or-equal");
        if (!input.ThinTransmissionAbsent)
            return Reject(OpacityMicromapEligibilityFailure.ThinTransmissionPresent, "thin-transmission-present");
        if (!input.AnimatedMaskAbsent)
            return Reject(OpacityMicromapEligibilityFailure.AnimatedMaskPresent, "animated-alpha-mask-present");
        if (!input.ProceduralAlphaAbsent)
            return Reject(OpacityMicromapEligibilityFailure.ProceduralAlphaPresent, "procedural-alpha-present");
        if (!input.PerRayAlphaOverrideAbsent)
            return Reject(OpacityMicromapEligibilityFailure.PerRayAlphaOverridePresent, "per-ray-alpha-override-present");
        if (!input.FourStateFormatSupported)
            return Reject(OpacityMicromapEligibilityFailure.FourStateFormatUnsupported, "four-state-format-unsupported");
        if (input.RequestedSubdivisionLevel > input.MaximumFourStateSubdivisionLevel ||
            input.MaximumFourStateSubdivisionLevel == 0 ||
            input.MaximumFourStateSubdivisionLevel >
                OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel)
        {
            return Reject(OpacityMicromapEligibilityFailure.SubdivisionUnsupported, "four-state-subdivision-unsupported");
        }

        return OpacityMicromapEligibility.Accepted;
    }

    private static OpacityMicromapEligibility Reject(
        OpacityMicromapEligibilityFailure failure,
        string detail) => OpacityMicromapEligibility.Rejected(failure, detail);

    internal static float ToFloat(uint bits) =>
        BitConverter.Int32BitsToSingle(unchecked((int)bits));

    private static bool TryUnitFinite(uint bits, out float value)
    {
        value = ToFloat(bits);
        return float.IsFinite(value) && value is >= 0.0f and <= 1.0f;
    }
}

/// <summary>
/// Bounds over the final composed alpha expression (material factor times the
/// uniform vertex factor times sampled alpha), not bounds over the source
/// texture alone.  Values are supplied as bits to make equality-at-cutoff
/// auditable.
/// </summary>
public readonly record struct OpacityMicromapAlphaRange(
    uint MinimumAlphaBits,
    uint MaximumAlphaBits,
    uint RepresentativeAlphaBits)
{
    public bool TryGetValues(out float minimum, out float maximum, out float representative)
    {
        minimum = OpacityMicromapEligibilityEvaluator.ToFloat(MinimumAlphaBits);
        maximum = OpacityMicromapEligibilityEvaluator.ToFloat(MaximumAlphaBits);
        representative = OpacityMicromapEligibilityEvaluator.ToFloat(RepresentativeAlphaBits);
        return float.IsFinite(minimum) &&
               float.IsFinite(maximum) &&
               float.IsFinite(representative) &&
               minimum is >= 0.0f and <= 1.0f &&
               maximum is >= 0.0f and <= 1.0f &&
               representative is >= 0.0f and <= 1.0f &&
               minimum <= maximum &&
               representative >= minimum &&
               representative <= maximum;
    }
}

public readonly record struct OpacityMicromapClassification(
    OpacityMicromapMicrotriangleState State,
    bool RequiresShaderConfirmation,
    string Detail)
{
    public bool IsKnown => !RequiresShaderConfirmation;
}

/// <summary>
/// Classifies only proofs.  There is no epsilon: the frozen DDGI comparison is
/// <c>alpha &gt;= cutoff</c>, so alpha exactly equal to cutoff is known opaque.
/// Mixed cells choose one of the two unknown states using only a deterministic
/// representative; both unknown states take the unchanged candidate shader and
/// therefore cannot alter visibility.
/// </summary>
public static class OpacityMicromapFourStateClassifier
{
    public static OpacityMicromapClassification Classify(
        in OpacityMicromapAlphaRange range,
        uint alphaCutoffBits,
        in OpacityMicromapEligibility eligibility)
    {
        if (!range.TryGetValues(out float minimum, out float maximum, out float representative) ||
            !float.IsFinite(OpacityMicromapEligibilityEvaluator.ToFloat(alphaCutoffBits)))
        {
            return UnknownOpaque("alpha-range-or-cutoff-invalid");
        }

        if (!eligibility.Eligible)
        {
            return SelectUnknown(
                representative,
                OpacityMicromapEligibilityEvaluator.ToFloat(alphaCutoffBits),
                $"ineligible-{eligibility.Detail}");
        }

        float cutoff = OpacityMicromapEligibilityEvaluator.ToFloat(alphaCutoffBits);
        if (maximum < cutoff)
        {
            return new OpacityMicromapClassification(
                OpacityMicromapMicrotriangleState.Transparent,
                RequiresShaderConfirmation: false,
                "proved-below-cutoff");
        }
        if (minimum >= cutoff)
        {
            return new OpacityMicromapClassification(
                OpacityMicromapMicrotriangleState.Opaque,
                RequiresShaderConfirmation: false,
                "proved-at-or-above-cutoff");
        }

        return SelectUnknown(representative, cutoff, "cutoff-boundary-unknown");
    }

    private static OpacityMicromapClassification SelectUnknown(
        float representative,
        float cutoff,
        string detail) => representative >= cutoff
        ? UnknownOpaque(detail)
        : new OpacityMicromapClassification(
            OpacityMicromapMicrotriangleState.UnknownTransparent,
            RequiresShaderConfirmation: true,
            detail);

    private static OpacityMicromapClassification UnknownOpaque(string detail) => new(
        OpacityMicromapMicrotriangleState.UnknownOpaque,
        RequiresShaderConfirmation: true,
        detail);
}

internal static class OpacityMicromapCanonicalHash
{
    public static void AppendBlob(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendUInt64(hash, checked((ulong)value.Length));
        hash.AppendData(value);
    }

    public static void AppendContentKey(IncrementalHash hash, OpacityMicromapContentKey key)
    {
        Span<byte> bytes = stackalloc byte[OpacityMicromapContentKey.ByteLength];
        key.CopyTo(bytes);
        hash.AppendData(bytes);
    }

    public static void AppendBoolean(IncrementalHash hash, bool value) =>
        AppendByte(hash, value ? (byte)1 : (byte)0);

    public static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }

    public static void AppendInt32(IncrementalHash hash, int value) =>
        AppendUInt32(hash, unchecked((uint)value));

    public static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    public static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
