using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Versioned four-word surface-history ABI shared by conservative temporal
/// consumers. The upper nibble of word 3 is the validity mask for the four
/// bilinear taps in floor/floor, ceil/floor, floor/ceil, ceil/ceil order.
/// </summary>
public static class TemporalSurfaceValidityCodec
{
    public const uint AbiVersion = 1u;
    public const int WordsPerPixel = 4;
    public const ulong BytesPerPixel = WordsPerPixel * sizeof(uint);
    public const uint NormalPayloadMask = 0x0fffffffu;
    public const int TapMaskShift = 28;

    public static uint PackNormal(Vector3 normal, uint tapMask = 0u)
    {
        Vector3 unit = SafeNormal(normal);
        float inverseL1 = 1f /
            (MathF.Abs(unit.X) + MathF.Abs(unit.Y) + MathF.Abs(unit.Z));
        float x = unit.X * inverseL1;
        float y = unit.Y * inverseL1;
        if (unit.Z < 0f)
        {
            float foldedX = (1f - MathF.Abs(y)) * SignNotZero(x);
            float foldedY = (1f - MathF.Abs(x)) * SignNotZero(y);
            x = foldedX;
            y = foldedY;
        }

        uint encodedX = (uint)MathF.Round(
            Math.Clamp(x * 0.5f + 0.5f, 0f, 1f) * 16383f);
        uint encodedY = (uint)MathF.Round(
            Math.Clamp(y * 0.5f + 0.5f, 0f, 1f) * 16383f);
        return (encodedX & 0x3fffu) |
               ((encodedY & 0x3fffu) << 14) |
               ((tapMask & 0xfu) << TapMaskShift);
    }

    public static Vector3 UnpackNormal(uint packed)
    {
        float x = ((packed & 0x3fffu) / 16383f) * 2f - 1f;
        float y = (((packed >> 14) & 0x3fffu) / 16383f) * 2f - 1f;
        Vector3 normal = new(x, y, 1f - MathF.Abs(x) - MathF.Abs(y));
        if (normal.Z < 0f)
        {
            float unfoldedX = (1f - MathF.Abs(normal.Y)) * SignNotZero(normal.X);
            float unfoldedY = (1f - MathF.Abs(normal.X)) * SignNotZero(normal.Y);
            normal.X = unfoldedX;
            normal.Y = unfoldedY;
        }
        return SafeNormal(normal);
    }

    public static uint UnpackTapMask(uint packed) => packed >> TapMaskShift;

    /// <summary>
    /// Maps the directional-shadow nearest texel to the shared four-tap mask.
    /// <paramref name="previousPixelCenter"/> is previousUv * dimensions.
    /// </summary>
    public static uint ResolveNearestTapBit(Vector2 previousPixelCenter)
    {
        int baseX = (int)MathF.Floor(previousPixelCenter.X - 0.5f);
        int baseY = (int)MathF.Floor(previousPixelCenter.Y - 0.5f);
        int nearestX = (int)MathF.Floor(previousPixelCenter.X);
        int nearestY = (int)MathF.Floor(previousPixelCenter.Y);
        int offsetX = Math.Clamp(nearestX - baseX, 0, 1);
        int offsetY = Math.Clamp(nearestY - baseY, 0, 1);
        return 1u << (offsetX + offsetY * 2);
    }

    public static bool RequiresProducer(SurfaceHistoryConsumer consumers) =>
        (consumers & (SurfaceHistoryConsumer.DirectionalCsmTemporal |
                      SurfaceHistoryConsumer.DirectionalRaySoft)) != 0;

    private static Vector3 SafeNormal(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
            ? value / MathF.Sqrt(lengthSquared)
            : new Vector3(0f, 0f, 1f);
    }

    private static float SignNotZero(float value) => value < 0f ? -1f : 1f;
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential,
    Pack = 4)]
public struct GPUTemporalSurfaceValidityPushConstants
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint CurrentBufferIndex;
    public uint PreviousBufferIndex;
    public uint HistoryValid;
    public float RelativeDepthThreshold;
    public float NormalThreshold;
    public uint AbiVersion;
}
