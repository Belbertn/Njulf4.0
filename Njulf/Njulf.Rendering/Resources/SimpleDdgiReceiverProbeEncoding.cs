using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// CPU mirror of the compact Simple-DDGI receiver ABI. Runtime-valid records
/// are normally produced by the GPU after atlas publication; this mirror keeps
/// packing, validation, imports, and tests on the exact same numerical contract.
/// </summary>
public static class SimpleDdgiReceiverProbeEncoding
{
    public const float RelocationEncodingRangeInProbeSpacings = 0.5f;
    public const float MaximumUpdateRelocationInProbeSpacings = 0.45f;
    public const float ActiveWeightRejectionThreshold = 0.001f;
    public const uint InvalidAtlasProbeAddress = uint.MaxValue;

    public const uint PublishedCoherentFlag = 1u << 0;
    public const uint FreshFlag = 1u << 1;
    public const uint ScrollExposedFlag = 1u << 2;
    public const uint RelocationPendingFlag = 1u << 3;
    public const uint InactiveFlag = 1u << 4;
    public const uint InactiveClassificationFlag = 1u << 5;

    public const uint StateRejectionMask =
        FreshFlag |
        ScrollExposedFlag |
        RelocationPendingFlag |
        InactiveFlag |
        InactiveClassificationFlag;
    public const uint PublisherInputFlagMask = StateRejectionMask;

    /// <summary>
    /// A fill-friendly fail-closed record. Filling a newly allocated buffer with
    /// 0xffffffff creates this exact value: the address is invalid and every
    /// rejection flag is asserted even though the coherent bit is also set.
    /// </summary>
    public static GPUSimpleDdgiReceiverProbe Invalid => new()
    {
        PackedRelocationXY = uint.MaxValue,
        PackedRelocationZWeight = uint.MaxValue,
        Flags = uint.MaxValue,
        AtlasProbeAddress = InvalidAtlasProbeAddress
    };

    public static bool TryPack(
        Vector3 relocation,
        float probeSpacing,
        float activeWeight,
        uint receiverFlags,
        uint atlasProbeAddress,
        out GPUSimpleDdgiReceiverProbe packed)
    {
        packed = Invalid;
        if (!float.IsFinite(probeSpacing) || probeSpacing <= 0.0f ||
            !float.IsFinite(relocation.X) ||
            !float.IsFinite(relocation.Y) ||
            !float.IsFinite(relocation.Z) ||
            !float.IsFinite(activeWeight) ||
            activeWeight < 0.0f || activeWeight > 1.0f ||
            (receiverFlags & ~PublisherInputFlagMask) != 0u ||
            atlasProbeAddress == InvalidAtlasProbeAddress)
        {
            return false;
        }

        float relocationRange = probeSpacing * RelocationEncodingRangeInProbeSpacings;
        if (!float.IsFinite(relocationRange) || relocationRange <= 0.0f ||
            MathF.Abs(relocation.X) > relocationRange ||
            MathF.Abs(relocation.Y) > relocationRange ||
            MathF.Abs(relocation.Z) > relocationRange)
        {
            return false;
        }

        ushort x = PackSnorm16(relocation.X / relocationRange);
        ushort y = PackSnorm16(relocation.Y / relocationRange);
        ushort z = PackSnorm16(relocation.Z / relocationRange);
        ushort weight = PackActiveWeight(activeWeight);
        packed = new GPUSimpleDdgiReceiverProbe
        {
            PackedRelocationXY = x | ((uint)y << 16),
            PackedRelocationZWeight = z | ((uint)weight << 16),
            Flags = receiverFlags | PublishedCoherentFlag,
            AtlasProbeAddress = atlasProbeAddress
        };
        return true;
    }

    public static bool TryUnpack(
        in GPUSimpleDdgiReceiverProbe packed,
        float probeSpacing,
        out Vector3 relocation,
        out float activeWeight)
    {
        relocation = Vector3.Zero;
        activeWeight = 0.0f;
        if (!float.IsFinite(probeSpacing) || probeSpacing <= 0.0f ||
            (packed.Flags & PublishedCoherentFlag) == 0u ||
            packed.AtlasProbeAddress == InvalidAtlasProbeAddress)
        {
            return false;
        }

        float relocationRange = probeSpacing * RelocationEncodingRangeInProbeSpacings;
        relocation = new Vector3(
            UnpackSnorm16((ushort)packed.PackedRelocationXY),
            UnpackSnorm16((ushort)(packed.PackedRelocationXY >> 16)),
            UnpackSnorm16((ushort)packed.PackedRelocationZWeight)) * relocationRange;
        activeWeight = (packed.PackedRelocationZWeight >> 16) / 65535.0f;
        return true;
    }

    internal static ushort PackSnorm16(float value)
    {
        float clamped = Math.Clamp(value, -1.0f, 1.0f);
        int quantized = checked((int)MathF.Round(
            clamped * short.MaxValue,
            MidpointRounding.ToEven));
        return unchecked((ushort)(short)quantized);
    }

    internal static float UnpackSnorm16(ushort value)
    {
        short signed = unchecked((short)value);
        return MathF.Max(signed / (float)short.MaxValue, -1.0f);
    }

    internal static ushort PackActiveWeight(float activeWeight)
    {
        if (activeWeight <= ActiveWeightRejectionThreshold)
            return 0;

        int quantized = checked((int)MathF.Round(
            activeWeight * ushort.MaxValue,
            MidpointRounding.ToEven));
        // 66 is the first UNORM16 code whose decoded value is strictly above
        // 0.001, so an accepted source weight cannot quantize back to rejected.
        return checked((ushort)Math.Clamp(quantized, 66, ushort.MaxValue));
    }
}
