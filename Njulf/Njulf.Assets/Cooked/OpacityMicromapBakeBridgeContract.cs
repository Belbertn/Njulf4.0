using System.Security.Cryptography;
using System.Text;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Auditable identity for the native OMM baker.  The shipping renderer never
/// loads this bridge; only an offline asset tool may provide an implementation.
/// </summary>
public readonly record struct OpacityMicromapSdkProvenance(
    string SourceUri,
    string CommitOrRelease,
    string LicenseIdentifier,
    string BuildFlags,
    string CompilerIdentity,
    OpacityMicromapContentKey BinarySha256)
{
    public const int MaximumSourceUriCharacters = 2_048;
    public const int MaximumCommitCharacters = 128;
    public const int MaximumLicenseCharacters = 256;
    public const int MaximumBuildFlagsCharacters = 4_096;
    public const int MaximumCompilerIdentityCharacters = 1_024;

    public bool TryValidate(out string detail)
    {
        if (string.IsNullOrWhiteSpace(SourceUri) || SourceUri.Length > MaximumSourceUriCharacters ||
            !Uri.TryCreate(SourceUri, UriKind.Absolute, out Uri? source) ||
            source.Scheme is not ("https" or "http"))
        {
            detail = "sdk-provenance-source-uri-invalid";
            return false;
        }
        if (string.IsNullOrWhiteSpace(CommitOrRelease) ||
            CommitOrRelease.Length > MaximumCommitCharacters ||
            !CommitOrRelease.All(IsAsciiVisible))
        {
            detail = "sdk-provenance-commit-or-release-invalid";
            return false;
        }
        if (string.IsNullOrWhiteSpace(LicenseIdentifier) ||
            LicenseIdentifier.Length > MaximumLicenseCharacters ||
            !LicenseIdentifier.All(IsAsciiVisible))
        {
            detail = "sdk-provenance-license-invalid";
            return false;
        }
        if (BuildFlags is null || BuildFlags.Length > MaximumBuildFlagsCharacters ||
            CompilerIdentity is null || CompilerIdentity.Length > MaximumCompilerIdentityCharacters)
        {
            detail = "sdk-provenance-build-identity-invalid";
            return false;
        }
        if (BinarySha256.IsZero)
        {
            detail = "sdk-provenance-binary-hash-zero";
            return false;
        }

        detail = "sdk-provenance-valid";
        return true;
    }

    /// <summary>
    /// Stable provenance fingerprint recorded in every payload header.  Raw
    /// UTF-8 values are length-delimited; culture, object identity, and managed
    /// hash randomization cannot influence it.
    /// </summary>
    public OpacityMicromapContentKey ComputeFingerprint()
    {
        if (!TryValidate(out string detail))
            throw new InvalidOperationException($"Cannot fingerprint invalid OMM provenance: {detail}.");

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("njulf.opacity-micromap.sdk-provenance"u8);
        AppendUtf8(hash, SourceUri);
        AppendUtf8(hash, CommitOrRelease);
        AppendUtf8(hash, LicenseIdentifier);
        AppendUtf8(hash, BuildFlags);
        AppendUtf8(hash, CompilerIdentity);
        OpacityMicromapCanonicalHash.AppendContentKey(hash, BinarySha256);
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        OpacityMicromapCanonicalHash.AppendBlob(hash, bytes);
    }

    private static bool IsAsciiVisible(char value) => value is >= '!' and <= '~';
}

public readonly record struct OpacityMicromapBakeBridgeContract(
    uint BridgeAbi,
    OpacityMicromapSdkProvenance Provenance,
    int MaximumInputBytes,
    int MaximumOutputBytes,
    int MaximumPrimitiveCount)
{
    public bool TryValidate(out string detail)
    {
        if (BridgeAbi == 0)
        {
            detail = "omm-bridge-abi-zero";
            return false;
        }
        if (MaximumInputBytes <= 0 || MaximumOutputBytes <= 0 || MaximumPrimitiveCount <= 0)
        {
            detail = "omm-bridge-bounds-invalid";
            return false;
        }
        if (!Provenance.TryValidate(out detail))
            return false;

        detail = "omm-bridge-contract-valid";
        return true;
    }
}

/// <summary>
/// Native bridge inputs expressed without a C++ ABI.  The actual bridge owns
/// its allocator/cancellation translation; this managed contract verifies all
/// byte counts before an implementation is invoked.
/// </summary>
public readonly record struct OpacityMicromapBakeRequest(
    OpacityMicromapContentKey ContentKey,
    OpacityMicromapEligibility Eligibility,
    OpacityMicromapMaterialContract MaterialContract,
    uint PrimitiveCount,
    uint RequestedSubdivisionLevel,
    ReadOnlyMemory<byte> IndexBytes,
    ReadOnlyMemory<byte> UvBytes,
    ReadOnlyMemory<byte> AlphaTextureBytes)
{
    /// <summary>Number of tightly packed UV32 vertices in <see cref="UvBytes"/>.</summary>
    public uint VertexCount { get; init; }
    public uint TextureWidth { get; init; }
    public uint TextureHeight { get; init; }
    public uint TextureMipCount { get; init; } = 1;
    public uint TextureVulkanFormat { get; init; }
    public uint AlphaChannel { get; init; } = 3;
    public ulong MaximumWorkloadSize { get; init; } = 1UL << 28;
    public uint MaximumArrayDataBytes { get; init; } = 256U * 1024U * 1024U;

    public bool TryValidate(
        in OpacityMicromapBakeBridgeContract bridge,
        out OpacityMicromapBakeFailure failure,
        out string detail)
    {
        if (!bridge.TryValidate(out detail))
        {
            failure = OpacityMicromapBakeFailure.BridgeContractInvalid;
            return false;
        }
        if (!Eligibility.Eligible)
        {
            failure = OpacityMicromapBakeFailure.ContentIneligible;
            detail = $"omm-content-ineligible-{Eligibility.Detail}";
            return false;
        }
        if (ContentKey.IsZero || PrimitiveCount == 0 ||
            PrimitiveCount > bridge.MaximumPrimitiveCount ||
            RequestedSubdivisionLevel >
                OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel ||
            MaterialContract.PrimitiveCount == 0 ||
            !MaterialContract.HasFiniteAlphaInputs ||
            !MaterialContract.UvTransform.IsFinite)
        {
            failure = OpacityMicromapBakeFailure.RequestInvalid;
            detail = "omm-bake-request-fields-invalid";
            return false;
        }
        if (VertexCount == 0 || TextureWidth == 0 || TextureHeight == 0 ||
            TextureMipCount != 1 || AlphaChannel != 3 ||
            MaximumWorkloadSize == 0 || MaximumArrayDataBytes == 0)
        {
            failure = OpacityMicromapBakeFailure.RequestInvalid;
            detail = "omm-bake-request-texture-or-workload-invalid";
            return false;
        }
        if (IndexBytes.IsEmpty || UvBytes.IsEmpty || AlphaTextureBytes.IsEmpty)
        {
            failure = OpacityMicromapBakeFailure.RequestInvalid;
            detail = "omm-bake-request-input-empty";
            return false;
        }

        long expectedIndexBytes;
        long expectedUvBytes;
        long expectedAlphaBytes;
        try
        {
            expectedIndexBytes = checked((long)PrimitiveCount * 3L * sizeof(uint));
            expectedUvBytes = checked((long)VertexCount * 2L * sizeof(float));
            expectedAlphaBytes = checked(
                (long)TextureWidth * TextureHeight * sizeof(float));
        }
        catch (OverflowException)
        {
            failure = OpacityMicromapBakeFailure.RequestInvalid;
            detail = "omm-bake-request-byte-count-overflow";
            return false;
        }
        if (IndexBytes.Length != expectedIndexBytes ||
            UvBytes.Length != expectedUvBytes ||
            AlphaTextureBytes.Length != expectedAlphaBytes)
        {
            failure = OpacityMicromapBakeFailure.RequestInvalid;
            detail = "omm-bake-request-packed-input-length-mismatch";
            return false;
        }

        long totalBytes = (long)IndexBytes.Length + UvBytes.Length + AlphaTextureBytes.Length;
        if (totalBytes > bridge.MaximumInputBytes)
        {
            failure = OpacityMicromapBakeFailure.InputTooLarge;
            detail = "omm-bake-request-input-exceeds-bridge-cap";
            return false;
        }

        failure = OpacityMicromapBakeFailure.None;
        detail = "omm-bake-request-valid";
        return true;
    }
}

public enum OpacityMicromapBakeFailure : byte
{
    None = 0,
    BridgeUnavailable,
    BridgeContractInvalid,
    ContentIneligible,
    RequestInvalid,
    InputTooLarge,
    Cancelled,
    NativeFailure,
    OutputRejected
}

public readonly record struct OpacityMicromapBakeResult(
    bool Succeeded,
    OpacityMicromapCookedPayload? Payload,
    OpacityMicromapBakeFailure Failure,
    string Detail)
{
    public static OpacityMicromapBakeResult Rejected(
        OpacityMicromapBakeFailure failure,
        string detail) => new(false, null, failure, detail);
}

/// <summary>
/// Versioned C-ABI bridge seam.  This repository intentionally ships only the
/// fail-closed implementation until a pinned SDK binary is supplied by the
/// asset-tool distribution; no external OMM SDK package is referenced here.
/// </summary>
public interface IOpacityMicromapBakeBridge
{
    OpacityMicromapBakeBridgeContract Contract { get; }

    ValueTask<OpacityMicromapBakeResult> BakeAsync(
        OpacityMicromapBakeRequest request,
        CancellationToken cancellationToken);
}

public sealed class FailClosedOpacityMicromapBakeBridge : IOpacityMicromapBakeBridge
{
    public static FailClosedOpacityMicromapBakeBridge Instance { get; } = new();

    private FailClosedOpacityMicromapBakeBridge()
    {
    }

    public OpacityMicromapBakeBridgeContract Contract => default;

    public ValueTask<OpacityMicromapBakeResult> BakeAsync(
        OpacityMicromapBakeRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(OpacityMicromapBakeResult.Rejected(
                OpacityMicromapBakeFailure.Cancelled,
                "omm-baker-cancelled-before-bridge-invocation"));
        }

        return ValueTask.FromResult(OpacityMicromapBakeResult.Rejected(
            OpacityMicromapBakeFailure.BridgeUnavailable,
            "omm-native-bridge-not-installed; optional-payload-not-produced"));
    }
}
