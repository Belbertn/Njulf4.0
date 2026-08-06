using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>CPU mirror of the canonical one-word RG16F visibility payload.</summary>
public static class SimpleDdgiVisibilityPacking
{
    public const uint VisibilityValidProbeFlag = 1u << 5;
    public const int BytesPerTexel = sizeof(uint);

    public static uint ResolveWordAddress(
        uint probeIndex,
        uint texelIndex,
        uint texelsPerProbe)
    {
        if (texelsPerProbe == 0 || texelIndex >= checked(texelsPerProbe * texelsPerProbe))
            throw new ArgumentOutOfRangeException(nameof(texelIndex));
        return checked(probeIndex * texelsPerProbe * texelsPerProbe + texelIndex);
    }

    public static uint PackMoments(Vector2 moments)
    {
        if (!float.IsFinite(moments.X) || !float.IsFinite(moments.Y))
            throw new ArgumentOutOfRangeException(nameof(moments));
        ushort mean = BitConverter.HalfToUInt16Bits(
            (Half)Math.Clamp(moments.X, 0.0f, 65_504.0f));
        ushort secondMoment = BitConverter.HalfToUInt16Bits(
            (Half)Math.Clamp(moments.Y, 0.0f, 65_504.0f));
        return mean | ((uint)secondMoment << 16);
    }

    public static Vector2 UnpackMoments(uint packed) => new(
        (float)BitConverter.UInt16BitsToHalf(checked((ushort)(packed & 0xffffu))),
        (float)BitConverter.UInt16BitsToHalf(checked((ushort)(packed >> 16))));

    public static bool IsVisibilityValid(uint probeFlags) =>
        (probeFlags & VisibilityValidProbeFlag) != 0u;
}
