using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Immutable full-resolution C4 receiver profile.  The first production
/// implementation intentionally admits one exact profile: an 8x8 compact-tile
/// grid over the scene render extent.  Half-resolution reconstruction is a
/// separate qualification profile and is not silently inferred here.
/// </summary>
public readonly record struct GiCausticScreenResolveProfile(
    int Width,
    int Height,
    int TileSize = GiCausticScreenGpuAbi.TileSize,
    float MinimumReceiverNormalCosine = 0.5f)
{
    public bool TryValidate(out string reason)
    {
        if (Width <= 0 || Height <= 0)
        {
            reason = "caustic-screen-resolve-extent-missing";
            return false;
        }
        if (Width > GiCausticScreenGpuAbi.MaximumDimension ||
            Height > GiCausticScreenGpuAbi.MaximumDimension)
        {
            reason = "caustic-screen-resolve-extent-exceeds-profile-limit";
            return false;
        }
        if (TileSize != GiCausticScreenGpuAbi.TileSize)
        {
            reason = "caustic-screen-resolve-tile-size-ABI-mismatch";
            return false;
        }
        if (!float.IsFinite(MinimumReceiverNormalCosine) ||
            MinimumReceiverNormalCosine is < 0.0f or > 1.0f)
        {
            reason = "caustic-screen-resolve-normal-threshold-invalid";
            return false;
        }

        reason = "valid";
        return true;
    }
}

/// <summary>
/// Complete C4 screen allocation.  Receiver payload and radiance are separate
/// images; tile metadata aliases the deterministic cache-build scratch only
/// after publication barriers make the build lifetime end.
/// </summary>
public readonly record struct GiCausticScreenResolveLayout(
    int Width,
    int Height,
    int TileSize,
    int TileCountX,
    int TileCountY,
    int TileCapacity,
    float MinimumReceiverNormalCosine,
    ulong ReceiverPayloadBytes,
    ulong RadianceBytes,
    ulong MomentsBytes,
    ulong TileScratchBytes,
    bool IsValid,
    string FailureReason)
{
    public static GiCausticScreenResolveLayout Empty(string reason) => new(
        0, 0, 0, 0, 0, 0, 0.0f, 0UL, 0UL, 0UL, 0UL, false, reason);

    public ulong PersistentImageBytes => checked(
        ReceiverPayloadBytes + RadianceBytes + MomentsBytes);

    public ulong RequiredBytesWithoutAliasing => checked(
        PersistentImageBytes + TileScratchBytes);
}

public static class GiCausticScreenResolveLayoutCompiler
{
    public static GiCausticScreenResolveLayout Compile(
        in GiCausticScreenResolveProfile profile)
    {
        if (!profile.TryValidate(out string reason))
            return GiCausticScreenResolveLayout.Empty(reason);

        try
        {
            int tileCountX = checked(
                (profile.Width + profile.TileSize - 1) / profile.TileSize);
            int tileCountY = checked(
                (profile.Height + profile.TileSize - 1) / profile.TileSize);
            int tileCapacity = checked(tileCountX * tileCountY);
            ulong pixelCount = checked(
                (ulong)profile.Width * (ulong)profile.Height);
            ulong receiverPayloadBytes = checked(
                pixelCount * GiCausticScreenGpuAbi.ReceiverPayloadBytesPerPixel);
            ulong radianceBytes = checked(
                pixelCount * GiCausticScreenGpuAbi.RadianceBytesPerPixel);
            ulong momentsBytes = checked(
                pixelCount * GiCausticScreenGpuAbi.MomentsBytesPerPixel);
            ulong tileWords = checked(
                (ulong)GiCausticScreenGpuAbi.TileListWordOffset +
                (ulong)tileCapacity);
            ulong tileScratchBytes = AlignUp(
                checked(tileWords * sizeof(uint)),
                GiCausticScreenGpuAbi.ScratchAlignmentBytes);

            if (tileCapacity <= 0 || tileWords > uint.MaxValue ||
                tileScratchBytes / sizeof(uint) > uint.MaxValue)
            {
                return GiCausticScreenResolveLayout.Empty(
                    "caustic-screen-resolve-tile-capacity-overflow");
            }

            return new GiCausticScreenResolveLayout(
                profile.Width,
                profile.Height,
                profile.TileSize,
                tileCountX,
                tileCountY,
                tileCapacity,
                profile.MinimumReceiverNormalCosine,
                receiverPayloadBytes,
                radianceBytes,
                momentsBytes,
                tileScratchBytes,
                true,
                "valid");
        }
        catch (OverflowException)
        {
            return GiCausticScreenResolveLayout.Empty(
                "caustic-screen-resolve-layout-overflow");
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked(
        (value + alignment - 1UL) / alignment * alignment);
}

/// <summary>Frozen private-descriptor and push-constant ABI for C4 resolve.</summary>
public static class GiCausticScreenGpuAbi
{
    public const uint Version = 0xC402_0001u;
    public const int MaximumDimension = 16_384;
    public const int TileSize = 8;
    public const int ReceiverPayloadBytesPerPixel = 16;
    public const int RadianceBytesPerPixel = 8;
    public const int MomentsBytesPerPixel = 8;
    public const ulong ScratchAlignmentBytes = 256UL;
    public const uint ScratchHeaderWords = 16u;
    public const uint ActiveTileCountWordOffset = 0u;
    public const uint RejectedTileCountWordOffset = 1u;
    public const uint IndirectDispatchWordOffset = 4u;
    public const uint TileListWordOffset = ScratchHeaderWords;
    public const int PushConstantsBytes = 128;
    public const int FrameConstantsBytes = 192;
    public const uint DescriptorCount = 6u;

    public const Format ReceiverPayloadFormat = Format.R32G32B32A32Uint;
    public const Format RadianceFormat = Format.R16G16B16A16Sfloat;
    public const Format MomentsFormat = Format.R16G16B16A16Sfloat;

    public static void VerifyManagedLayout()
    {
        Verify<GPUCausticScreenPushConstantsV1>(PushConstantsBytes,
            (nameof(GPUCausticScreenPushConstantsV1.AbiVersion), 0),
            (nameof(GPUCausticScreenPushConstantsV1.TaskCount), 20),
            (nameof(GPUCausticScreenPushConstantsV1.CacheGeneration), 40),
            (nameof(GPUCausticScreenPushConstantsV1.CacheBankTableWordOffset), 80),
            (nameof(GPUCausticScreenPushConstantsV1.TransportAbiVersion), 104),
            (nameof(GPUCausticScreenPushConstantsV1.CellOriginAndSize), 112));
        Verify<GPUCausticScreenFrameConstantsV1>(FrameConstantsBytes,
            (nameof(GPUCausticScreenFrameConstantsV1.ViewProjection), 0),
            (nameof(GPUCausticScreenFrameConstantsV1.InverseViewProjection), 64),
            (nameof(GPUCausticScreenFrameConstantsV1.FullExtentAndInverse), 128),
            (nameof(GPUCausticScreenFrameConstantsV1.CameraPositionAndFlags), 144),
            (nameof(GPUCausticScreenFrameConstantsV1.ScreenParameters), 160),
            (nameof(GPUCausticScreenFrameConstantsV1.ResolveParameters), 176));
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
            int actual = Marshal.OffsetOf<T>(field).ToInt32();
            if (actual != expectedOffset)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{field} must be at offset {expectedOffset}; it is at {actual}.");
            }
        }
    }
}

public static class GiCausticScreenGpuBindings
{
    public const uint SceneDepth = 0u;
    public const uint ReceiverPayload = 1u;
    public const uint CausticRadiance = 2u;
    public const uint CausticMoments = 3u;
    public const uint SceneColor = 4u;
    public const uint FrameConstants = 5u;
}

public static class GiCausticScreenGpuDescriptorSets
{
    public const uint BindlessStorageBuffers = 0u;
    public const uint BindlessTextures = 1u;
    public const uint ScreenResources = 2u;
}

[Flags]
public enum GiCausticScreenGpuFlags : uint
{
    None = 0,
    ReversedZ = 1u << 0,
    ReceiverPayloadValidated = 1u << 1,
    SceneColorCompositeEnabled = 1u << 2
}

[StructLayout(LayoutKind.Sequential, Pack = 4,
    Size = GiCausticScreenGpuAbi.PushConstantsBytes)]
public struct GPUCausticScreenPushConstantsV1
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
    public uint TransportAbiVersion;
    public uint MaximumOccupiedCells;
    public Vector4 CellOriginAndSize;

    public static GPUCausticScreenPushConstantsV1 FromPublishedCache(
        in GPUCausticPushConstantsV1 source,
        GiCausticScreenGpuFlags screenFlags)
    {
        return new GPUCausticScreenPushConstantsV1
        {
            AbiVersion = GiCausticScreenGpuAbi.Version,
            TaskBufferIndex = source.TaskBufferIndex,
            PhotonBufferIndex = source.PhotonBufferIndex,
            CacheBufferIndex = source.CacheBufferIndex,
            ScratchBufferIndex = source.ScratchBufferIndex,
            TaskCount = source.TaskCount,
            PhotonCapacity = source.PhotonCapacity,
            PhotonRecordStrideWords = source.PhotonRecordStrideWords,
            CellTableCapacity = source.CellTableCapacity,
            MaximumPhotonsPerCell = source.MaximumPhotonsPerCell,
            CacheGeneration = source.CacheGeneration,
            RevisionFingerprintLow = source.RevisionFingerprintLow,
            RevisionFingerprintHigh = source.RevisionFingerprintHigh,
            CandidateStagingWordOffset = source.CandidateStagingWordOffset,
            CachePhotonBankBaseWord = source.CachePhotonBankBaseWord,
            PhotonReadBankIndex = source.PhotonReadBankIndex,
            PhotonWriteBankIndex = source.PhotonWriteBankIndex,
            CacheReadBankIndex = source.CacheReadBankIndex,
            CacheWriteBankIndex = source.CacheWriteBankIndex,
            CacheBankHeaderWordOffset = source.CacheBankHeaderWordOffset,
            CacheBankTableWordOffset = source.CacheBankTableWordOffset,
            ScratchWordCapacity = source.ScratchWordCapacity,
            Flags = (uint)screenFlags,
            BuildPhase = 0u,
            ResolveRequestWordOffset = 0u,
            ResolveRequestCount = 0u,
            TransportAbiVersion = source.TransportAbiVersion,
            MaximumOccupiedCells = source.MaximumOccupiedCells,
            CellOriginAndSize = source.CellOriginAndSize
        };
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct GPUCausticUInt4
{
    public uint X;
    public uint Y;
    public uint Z;
    public uint W;
}

[StructLayout(LayoutKind.Sequential, Pack = 4,
    Size = GiCausticScreenGpuAbi.FrameConstantsBytes)]
public struct GPUCausticScreenFrameConstantsV1
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Vector4 FullExtentAndInverse;
    public Vector4 CameraPositionAndFlags;
    /// <summary>TileCountX, TileCountY, TileCapacity, GiCausticScreenGpuFlags.</summary>
    public GPUCausticUInt4 ScreenParameters;
    /// <summary>Minimum normal cosine, maximum search distance, reserved.</summary>
    public Vector4 ResolveParameters;
}
