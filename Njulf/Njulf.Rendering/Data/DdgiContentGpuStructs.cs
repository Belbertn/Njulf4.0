using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

/// <summary>
/// Frozen 64-byte trailer after the canonical 1,024-light payload. It lets a
/// ray hit reject a stale hierarchy in O(1), including content-only edits that
/// preserve packed indices and stable light identities.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUDdgiLightBufferState
{
    public const uint MagicValue = 0x4444_474Cu; // "DDGL"

    public uint Magic;
    public uint LightBufferRevisionLow;
    public uint LightBufferRevisionHigh;
    public uint TopologyRevisionLow;
    public uint TopologyRevisionHigh;
    public uint ContentRevisionLow;
    public uint ContentRevisionHigh;
    public uint LightCount;
    public uint LocalLightCount;
    public uint ValidationChecksum;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;

    public static uint ComputeChecksum(
        ulong lightBufferRevision,
        ulong topologyRevision,
        ulong contentRevision,
        uint lightCount,
        uint localLightCount) =>
        MagicValue ^
        (uint)lightBufferRevision ^
        (uint)(lightBufferRevision >> 32) ^
        (uint)topologyRevision ^
        (uint)(topologyRevision >> 32) ^
        (uint)contentRevision ^
        (uint)(contentRevision >> 32) ^
        lightCount ^
        localLightCount;
}

/// <summary>
/// Frozen 64-byte local-light hierarchy node. A complete hierarchy is written
/// to inactive storage and becomes visible only through its published state.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUDdgiLightTreeNode
{
    // xyz = conservative influence minimum, w = aggregate emitted flux.
    public Vector4 BoundsMinimumAndFlux;
    // xyz = conservative influence maximum, w = maximum represented range.
    public Vector4 BoundsMaximumAndRange;
    // xyz = aggregate spot-cone axis, w = conservative minimum cone cosine.
    public Vector4 ConeAxisAndCosine;
    // Internal: left/right node. Leaf: first leaf/count.
    public uint LeftOrFirstLeaf;
    public uint RightOrLeafCount;
    public uint DescendantLeafCount;
    // Low 16 bits are flags; high 16 bits are the validation checksum.
    public uint FlagsAndChecksum;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct GPUDdgiLightTreeLeaf
{
    public uint PackedLightIndex;
    public uint StableLightIdentity;
    public uint LightBufferRevisionLow;
    public uint LightBufferRevisionHigh;
    // xyz = center, w = finite influence range. Negative w means the
    // conservative malformed/infinite-range class.
    public Vector4 CenterAndRange;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUDdgiLightTreeState
{
    public const uint ValidFlag = 1u << 0;

    public uint RootNodeIndex;
    public uint NodeCount;
    public uint LeafCount;
    public uint MaximumDepth;
    public uint ActiveStorageIndex;
    public uint PublicationGeneration;
    public uint LightBufferRevisionLow;
    public uint LightBufferRevisionHigh;
    public uint TopologyRevisionLow;
    public uint TopologyRevisionHigh;
    public uint ContentRevisionLow;
    public uint ContentRevisionHigh;
    public uint ValidationChecksum;
    public uint Flags;
    public uint RebuildReason;
    public uint Reserved0;

    public static uint ComputeValidationChecksum(
        uint publicationGeneration,
        uint leafCount,
        uint nodeCount,
        ulong lightBufferRevision,
        ulong topologyRevision,
        ulong contentRevision) =>
        publicationGeneration ^
        leafCount ^
        nodeCount ^
        (uint)lightBufferRevision ^
        (uint)(lightBufferRevision >> 32) ^
        (uint)topologyRevision ^
        (uint)(topologyRevision >> 32) ^
        (uint)contentRevision ^
        (uint)(contentRevision >> 32);
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct GPUDdgiLightTreePushConstants
{
    public uint LightCount;
    public uint LeafCapacity;
    public uint PaddedLeafCount;
    public uint NodeCount;
    public uint TargetStorageIndex;
    public uint PublishedStorageIndex;
    public uint BuildAction;
    public uint MaximumDepth;
    public float UniformMixtureProbability;
    public uint ExactLightThreshold;
    public uint NodeBankStrideWords;
    public uint LeafBankStrideWords;
    public uint ScratchRecordOffsetWords;
    public uint ScratchSortedIndexOffsetWords;
    public uint ScratchIndirectOffsetWords;
    public uint PublicationGeneration;
    public uint LightBufferRevisionLow;
    public uint LightBufferRevisionHigh;
    public uint TopologyRevisionLow;
    public uint TopologyRevisionHigh;
    public uint ContentRevisionLow;
    public uint ContentRevisionHigh;
    public uint Flags;
    public uint Reserved0;
}

/// <summary>
/// Frozen 64-byte RGB real-L2 SH record. Words 0..13 contain 27 binary16
/// coefficients plus one reserved half. Word 14 is the physical-slot
/// generation; word 15 is validity/sample/version/checksum metadata.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUSimpleDdgiRadianceShL2
{
    public uint Word0;
    public uint Word1;
    public uint Word2;
    public uint Word3;
    public uint Word4;
    public uint Word5;
    public uint Word6;
    public uint Word7;
    public uint Word8;
    public uint Word9;
    public uint Word10;
    public uint Word11;
    public uint Word12;
    public uint Word13;
    public uint SlotGeneration;
    public uint Metadata;
}

/// <summary>
/// Frozen 80-byte work record for one camera-independent procedural foliage
/// patch. The GPU expands the admitted card range directly into interleaved
/// <see cref="GPUVertex"/> and uint32 index streams consumed by a dynamic BLAS.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
public struct GPUDdgiFoliageProxyPatch
{
    // xyz = patch minimum, w = conservative crossed-card width.
    public Vector4 BoundsMinimumAndClusterWidth;
    // xyz = patch maximum, w = nominal card height.
    public Vector4 BoundsMaximumAndCardHeight;
    // xyz = wind strength/frequency/flutter, w = represented coverage.
    public Vector4 WindAndCoverage;
    public uint StablePatchKeyLow;
    public uint StablePatchKeyHigh;
    public uint CardOffset;
    public uint CardCount;
    public uint GridColumns;
    public uint GridRows;
    public uint RepresentedInstancesPerCard;
    public uint Flags;
}

/// <summary>Frozen 32-byte push ABI for foliage-proxy generation.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct GPUDdgiFoliageProxyGenerationPushConstants
{
    public uint PatchBufferIndex;
    public uint VertexBufferIndex;
    public uint IndexBufferIndex;
    public uint PatchCount;
    public uint CardCount;
    public uint CurrentFrameIndex;
    public float WindTimeSeconds;
    public uint CadenceGenerationLow;
}

public enum DdgiRayGeometryClass : uint
{
    Invalid = 0,
    StaticOpaque = 1,
    RigidOpaque = 2,
    SkinnedCurrentPose = 3,
    AlphaMask = 4,
    AlphaBlend = 5,
    ThinTransmission = 6,
    DecalOverlay = 7,
    AuthoredFoliage = 8,
    ProceduralFoliageProxy = 9,
    ConservativeProxy = 10
}

public enum DdgiRayVertexFormat : uint
{
    Invalid = 0,
    SplitStatic = 1,
    InterleavedGpuVertex = 2,
    InterleavedFoliageProxy = 3
}

[Flags]
public enum DdgiRayGeometryFlags : uint
{
    None = 0,
    AlphaMask = 1u << 0,
    AlphaBlend = 1u << 1,
    ThinTransmission = 1u << 2,
    TwoSided = 1u << 3,
    DecalOverlay = 1u << 4,
    Foliage = 1u << 5,
    DynamicVertexSource = 1u << 6,
    ConservativeProxy = 1u << 7,
    PremultipliedAlpha = 1u << 8,
    UnsupportedMaterialProxy = 1u << 9
}

/// <summary>
/// Frozen constants for <see cref="GPUDdgiRayQueryInstance"/>. Version and
/// record size are checked by CPU layout tests and shader mirror tests; a
/// mismatched record is never interpreted as the legacy 80-byte ABI.
/// </summary>
public static class DdgiRayQueryInstanceAbi
{
    public const uint Version2 = 0x4452_0002u; // "DR" + ABI 2.
    public const int SizeInBytes = 160;
    public const uint Uint32IndexType = 0;

    public static uint PackAlpha(MaterialBlendMode blendMode, float cutoff)
    {
        if (!float.IsFinite(cutoff) || cutoff < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(cutoff));

        uint mode = blendMode switch
        {
            MaterialBlendMode.Opaque => 0u,
            MaterialBlendMode.Mask => 1u,
            MaterialBlendMode.AlphaBlend => 2u,
            MaterialBlendMode.PremultipliedAlpha => 3u,
            MaterialBlendMode.Additive => 4u,
            MaterialBlendMode.Multiply => 5u,
            _ => throw new ArgumentOutOfRangeException(nameof(blendMode))
        };
        Half packedCutoff = (Half)Math.Min(cutoff, (float)Half.MaxValue);
        return mode | ((uint)BitConverter.HalfToUInt16Bits(packedCutoff) << 16);
    }

    public static uint PackDecalLayerAndOrder(int layer, uint stableOrder) =>
        unchecked((uint)(ushort)Math.Clamp(layer, short.MinValue, short.MaxValue)) |
        ((stableOrder & 0xffffu) << 16);
}
