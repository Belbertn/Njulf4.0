using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

/// <summary>
/// Eight-byte surface identity stored alongside a Simple-DDGI receiver-cache
/// radiance record. A zero depth code is the invalid sentinel.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverSurface
{
    public uint PackedNormal;
    public uint PackedDepthAndOffset;
}

/// <summary>Stable CPU/GLSL rejection taxonomy for cache qualification.</summary>
public enum SimpleDdgiReceiverSurfaceCompatibilityReason : uint
{
    Accepted = 0,
    InvalidSurface = 1,
    NonFinite = 2,
    Depth = 3,
    Position = 4,
    Plane = 5,
    Normal = 6,
    InsufficientSupport = 7
}

public readonly record struct SimpleDdgiReceiverSurfaceDecoded(
    Vector3 GeometricNormal,
    float ReverseZ,
    uint RepresentativeOffsetX,
    uint RepresentativeOffsetY);

/// <summary>
/// Authoritative packing and compatibility mirror for
/// <c>ddgi_receiver_surface.glsl</c>.
/// </summary>
public static class SimpleDdgiReceiverSurfaceAbi
{
    public const uint Version = 1;
    public const int StrideBytes = 8;
    public const uint DepthMask = 0x00ff_ffffu;
    public const uint MaximumDepthCode = DepthMask;
    public const uint MaximumRepresentativeOffset = 15;
    public const float MinimumReverseZ = 1.0f / 65_536.0f;
    public const float MaximumRelativeDepthDifference = 0.035f;
    public const float MinimumNormalDot = 0.94f;
    public const float MinimumWorldTolerance = 0.001f;
    public const float MinimumPlaneTolerance = 0.0015f;
    public const float CameraDistanceToleranceScale = 0.00001f;
    public const float PlaneCameraDistanceToleranceScale = 0.00005f;
    public const float PlanePixelFootprintScale = 1.75f;
    public const float NormalLengthTolerance = 0.01f;

    private const float MinimumHomogeneousW = 0.000001f;
    private const uint QuantizedDepthIntervalCount = MaximumDepthCode - 1u;

    public static bool TryPack(
        Vector3 geometricNormal,
        float reverseZ,
        uint representativeOffsetX,
        uint representativeOffsetY,
        out GPUSimpleDdgiReceiverSurface packed)
    {
        packed = default;
        float normalLengthSquared = geometricNormal.LengthSquared();
        if (!IsFinite(geometricNormal) ||
            !float.IsFinite(normalLengthSquared) ||
            MathF.Abs(normalLengthSquared - 1.0f) > NormalLengthTolerance ||
            !float.IsFinite(reverseZ) ||
            reverseZ <= 0.0f ||
            representativeOffsetX > MaximumRepresentativeOffset ||
            representativeOffsetY > MaximumRepresentativeOffset)
        {
            return false;
        }

        uint depthCode = EncodeReverseZ(reverseZ);
        if (depthCode == 0u)
            return false;

        geometricNormal = Vector3.Normalize(geometricNormal);
        Vector2 oct = EncodeOctahedral(geometricNormal);
        packed = new GPUSimpleDdgiReceiverSurface
        {
            PackedNormal = PackSnorm16x2(oct),
            PackedDepthAndOffset = depthCode |
                (representativeOffsetX << 24) |
                (representativeOffsetY << 28)
        };
        return true;
    }

    public static bool TryDecode(
        in GPUSimpleDdgiReceiverSurface packed,
        out SimpleDdgiReceiverSurfaceDecoded decoded)
    {
        decoded = default;
        uint depthCode = packed.PackedDepthAndOffset & DepthMask;
        if (depthCode == 0u)
            return false;

        float reverseZ = DecodeReverseZ(depthCode);
        Vector3 normal = DecodeOctahedral(UnpackSnorm16x2(packed.PackedNormal));
        if (!float.IsFinite(reverseZ) || reverseZ <= 0.0f ||
            !IsFinite(normal))
        {
            return false;
        }

        decoded = new SimpleDdgiReceiverSurfaceDecoded(
            normal,
            reverseZ,
            (packed.PackedDepthAndOffset >> 24) & 0x0fu,
            (packed.PackedDepthAndOffset >> 28) & 0x0fu);
        return true;
    }

    public static uint EncodeReverseZ(float reverseZ)
    {
        if (!float.IsFinite(reverseZ) || reverseZ <= 0.0f)
            return 0u;

        float normalized = Math.Clamp(
            (MathF.Log2(MathF.Max(reverseZ, MinimumReverseZ)) + 16.0f) /
                16.0f,
            0.0f,
            1.0f);
        return 1u + RoundPositiveToUInt(
            normalized * QuantizedDepthIntervalCount);
    }

    public static float DecodeReverseZ(uint depthCode)
    {
        depthCode &= DepthMask;
        if (depthCode == 0u)
            return 0.0f;

        float normalized = (depthCode - 1u) /
            (float)QuantizedDepthIntervalCount;
        return MathF.Pow(2.0f, normalized * 16.0f - 16.0f);
    }

    public static ulong CalculateSidecarBytes(
        uint width,
        uint height,
        uint scale,
        uint frameCount = 1)
    {
        if (scale == 0u)
            throw new ArgumentOutOfRangeException(nameof(scale));

        ulong scaledWidth = ((ulong)width + scale - 1u) / scale;
        ulong scaledHeight = ((ulong)height + scale - 1u) / scale;
        return checked(
            scaledWidth * scaledHeight * StrideBytes * frameCount);
    }

    public static SimpleDdgiReceiverSurfaceCompatibilityReason
        EvaluatePackedCompatibility(
            in GPUSimpleDdgiReceiverSurface first,
            uint firstCacheX,
            uint firstCacheY,
            uint firstScale,
            in GPUSimpleDdgiReceiverSurface second,
            uint secondCacheX,
            uint secondCacheY,
            uint secondScale,
            in Matrix4x4 inverseViewProjection,
            uint screenWidth,
            uint screenHeight,
            Vector3 cameraPosition)
    {
        if (!TryDecode(first, out SimpleDdgiReceiverSurfaceDecoded firstDecoded) ||
            !TryDecode(second, out SimpleDdgiReceiverSurfaceDecoded secondDecoded) ||
            firstScale == 0u || secondScale == 0u)
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.InvalidSurface;
        }

        if (!TryResolveRepresentativePixel(
                firstCacheX,
                firstCacheY,
                firstScale,
                firstDecoded,
                screenWidth,
                screenHeight,
                out uint firstPixelX,
                out uint firstPixelY) ||
            !TryResolveRepresentativePixel(
                secondCacheX,
                secondCacheY,
                secondScale,
                secondDecoded,
                screenWidth,
                screenHeight,
                out uint secondPixelX,
                out uint secondPixelY))
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.InvalidSurface;
        }

        if (!TryReconstructWorldPosition(
                firstPixelX,
                firstPixelY,
                firstDecoded.ReverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 firstPosition) ||
            !TryReconstructWorldPosition(
                secondPixelX,
                secondPixelY,
                secondDecoded.ReverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 secondPosition))
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.NonFinite;
        }

        float firstFootprint = EstimatePixelFootprint(
            firstPixelX,
            firstPixelY,
            firstDecoded.ReverseZ,
            firstPosition,
            inverseViewProjection,
            screenWidth,
            screenHeight);
        float secondFootprint = EstimatePixelFootprint(
            secondPixelX,
            secondPixelY,
            secondDecoded.ReverseZ,
            secondPosition,
            inverseViewProjection,
            screenWidth,
            screenHeight);
        return EvaluateDecodedCompatibility(
            firstDecoded.ReverseZ,
            firstPosition,
            firstDecoded.GeometricNormal,
            secondDecoded.ReverseZ,
            secondPosition,
            secondDecoded.GeometricNormal,
            MathF.Max(firstFootprint, secondFootprint),
            Math.Max(firstScale, secondScale),
            cameraPosition);
    }

    public static SimpleDdgiReceiverSurfaceCompatibilityReason
        EvaluateFragmentCompatibility(
            in GPUSimpleDdgiReceiverSurface cached,
            uint cacheX,
            uint cacheY,
            uint cacheScale,
            in Matrix4x4 inverseViewProjection,
            uint screenWidth,
            uint screenHeight,
            Vector3 cameraPosition,
            uint fragmentPixelX,
            uint fragmentPixelY,
            float fragmentReverseZ,
            Vector3 fragmentWorldPosition,
            Vector3 fragmentGeometricNormal)
    {
        if (!TryDecode(cached, out SimpleDdgiReceiverSurfaceDecoded decoded) ||
            cacheScale == 0u)
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.InvalidSurface;
        }

        float normalLengthSquared = fragmentGeometricNormal.LengthSquared();
        if (!float.IsFinite(fragmentReverseZ) || fragmentReverseZ <= 0.0f ||
            !IsFinite(fragmentWorldPosition) ||
            !IsFinite(fragmentGeometricNormal) ||
            !float.IsFinite(normalLengthSquared) ||
            normalLengthSquared <= 0.0f)
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.NonFinite;
        }

        if (!TryResolveRepresentativePixel(
                cacheX,
                cacheY,
                cacheScale,
                decoded,
                screenWidth,
                screenHeight,
                out uint cachedPixelX,
                out uint cachedPixelY) ||
            fragmentPixelX >= screenWidth || fragmentPixelY >= screenHeight)
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.InvalidSurface;
        }

        if (!TryReconstructWorldPosition(
                cachedPixelX,
                cachedPixelY,
                decoded.ReverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 cachedPosition) ||
            !TryReconstructWorldPosition(
                fragmentPixelX,
                fragmentPixelY,
                fragmentReverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 reconstructedFragmentPosition))
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.NonFinite;
        }

        float cachedFootprint = EstimatePixelFootprint(
            cachedPixelX,
            cachedPixelY,
            decoded.ReverseZ,
            cachedPosition,
            inverseViewProjection,
            screenWidth,
            screenHeight);
        float fragmentFootprint = EstimatePixelFootprint(
            fragmentPixelX,
            fragmentPixelY,
            fragmentReverseZ,
            reconstructedFragmentPosition,
            inverseViewProjection,
            screenWidth,
            screenHeight);
        return EvaluateDecodedCompatibility(
            decoded.ReverseZ,
            cachedPosition,
            decoded.GeometricNormal,
            fragmentReverseZ,
            fragmentWorldPosition,
            Vector3.Normalize(fragmentGeometricNormal),
            MathF.Max(cachedFootprint, fragmentFootprint),
            cacheScale,
            cameraPosition);
    }

    public static bool TryReconstructWorldPosition(
        uint pixelX,
        uint pixelY,
        float reverseZ,
        in Matrix4x4 inverseViewProjection,
        uint screenWidth,
        uint screenHeight,
        out Vector3 worldPosition)
    {
        worldPosition = default;
        if (screenWidth == 0u || screenHeight == 0u ||
            pixelX >= screenWidth || pixelY >= screenHeight ||
            !float.IsFinite(reverseZ) || reverseZ <= 0.0f ||
            !IsFinite(inverseViewProjection))
        {
            return false;
        }

        float u = (pixelX + 0.5f) / screenWidth;
        float v = (pixelY + 0.5f) / screenHeight;
        Vector4 homogeneous = Vector4.Transform(
            new Vector4(u * 2.0f - 1.0f, v * 2.0f - 1.0f, reverseZ, 1.0f),
            inverseViewProjection);
        if (!IsFinite(homogeneous) || MathF.Abs(homogeneous.W) <= MinimumHomogeneousW)
            return false;

        worldPosition = new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) /
            homogeneous.W;
        return IsFinite(worldPosition);
    }

    private static SimpleDdgiReceiverSurfaceCompatibilityReason
        EvaluateDecodedCompatibility(
            float firstReverseZ,
            Vector3 firstPosition,
            Vector3 firstNormal,
            float secondReverseZ,
            Vector3 secondPosition,
            Vector3 secondNormal,
            float pixelFootprint,
            uint maximumScale,
            Vector3 cameraPosition)
    {
        if (!float.IsFinite(firstReverseZ) || !float.IsFinite(secondReverseZ) ||
            !float.IsFinite(pixelFootprint) || !IsFinite(firstPosition) ||
            !IsFinite(secondPosition) || !IsFinite(firstNormal) ||
            !IsFinite(secondNormal) || !IsFinite(cameraPosition))
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.NonFinite;
        }

        float relativeDepth = MathF.Abs(firstReverseZ - secondReverseZ) /
            MathF.Max(MathF.Max(firstReverseZ, secondReverseZ), MinimumReverseZ);
        if (relativeDepth > MaximumRelativeDepthDifference)
            return SimpleDdgiReceiverSurfaceCompatibilityReason.Depth;

        Vector3 delta = secondPosition - firstPosition;
        float separation = delta.Length();
        float cameraDistance = MathF.Max(
            Vector3.Distance(firstPosition, cameraPosition),
            Vector3.Distance(secondPosition, cameraPosition));
        float safeFootprint = MathF.Max(pixelFootprint, MinimumWorldTolerance);
        float worldTolerance = MathF.Max(
            safeFootprint * (Math.Max(maximumScale, 1u) * 2.0f + 2.0f),
            MathF.Max(
                cameraDistance * CameraDistanceToleranceScale,
                MinimumWorldTolerance));
        if (!float.IsFinite(separation) || separation > worldTolerance)
            return SimpleDdgiReceiverSurfaceCompatibilityReason.Position;

        float planeTolerance = MathF.Max(
            safeFootprint * PlanePixelFootprintScale,
            MathF.Max(
                cameraDistance * PlaneCameraDistanceToleranceScale,
                MinimumPlaneTolerance));
        if (MathF.Abs(Vector3.Dot(delta, firstNormal)) > planeTolerance ||
            MathF.Abs(Vector3.Dot(delta, secondNormal)) > planeTolerance)
        {
            return SimpleDdgiReceiverSurfaceCompatibilityReason.Plane;
        }

        if (Vector3.Dot(firstNormal, secondNormal) < MinimumNormalDot)
            return SimpleDdgiReceiverSurfaceCompatibilityReason.Normal;

        return SimpleDdgiReceiverSurfaceCompatibilityReason.Accepted;
    }

    private static float EstimatePixelFootprint(
        uint pixelX,
        uint pixelY,
        float reverseZ,
        Vector3 center,
        in Matrix4x4 inverseViewProjection,
        uint screenWidth,
        uint screenHeight)
    {
        float footprint = 0.0f;
        uint neighborX = pixelX + 1u < screenWidth
            ? pixelX + 1u
            : pixelX > 0u ? pixelX - 1u : pixelX;
        uint neighborY = pixelY + 1u < screenHeight
            ? pixelY + 1u
            : pixelY > 0u ? pixelY - 1u : pixelY;
        if (neighborX != pixelX && TryReconstructWorldPosition(
                neighborX,
                pixelY,
                reverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 horizontal))
        {
            footprint = MathF.Max(footprint, Vector3.Distance(center, horizontal));
        }
        if (neighborY != pixelY && TryReconstructWorldPosition(
                pixelX,
                neighborY,
                reverseZ,
                inverseViewProjection,
                screenWidth,
                screenHeight,
                out Vector3 vertical))
        {
            footprint = MathF.Max(footprint, Vector3.Distance(center, vertical));
        }
        return MathF.Max(footprint, MinimumWorldTolerance);
    }

    private static bool TryResolveRepresentativePixel(
        uint cacheX,
        uint cacheY,
        uint scale,
        in SimpleDdgiReceiverSurfaceDecoded decoded,
        uint screenWidth,
        uint screenHeight,
        out uint pixelX,
        out uint pixelY)
    {
        pixelX = 0u;
        pixelY = 0u;
        if (scale == 0u ||
            decoded.RepresentativeOffsetX >= scale ||
            decoded.RepresentativeOffsetY >= scale)
        {
            return false;
        }

        ulong resolvedX = (ulong)cacheX * scale +
            decoded.RepresentativeOffsetX;
        ulong resolvedY = (ulong)cacheY * scale +
            decoded.RepresentativeOffsetY;
        if (resolvedX >= screenWidth || resolvedY >= screenHeight)
            return false;
        pixelX = (uint)resolvedX;
        pixelY = (uint)resolvedY;
        return true;
    }

    private static Vector2 EncodeOctahedral(Vector3 normal)
    {
        normal /= MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z);
        Vector2 encoded = new(normal.X, normal.Y);
        if (normal.Z < 0.0f)
        {
            encoded = new Vector2(
                (1.0f - MathF.Abs(encoded.Y)) * SignNotZero(encoded.X),
                (1.0f - MathF.Abs(encoded.X)) * SignNotZero(encoded.Y));
        }
        return encoded;
    }

    private static Vector3 DecodeOctahedral(Vector2 encoded)
    {
        Vector3 normal = new(
            encoded.X,
            encoded.Y,
            1.0f - MathF.Abs(encoded.X) - MathF.Abs(encoded.Y));
        float fold = MathF.Max(-normal.Z, 0.0f);
        normal.X += normal.X >= 0.0f ? -fold : fold;
        normal.Y += normal.Y >= 0.0f ? -fold : fold;
        float lengthSquared = normal.LengthSquared();
        return lengthSquared > 0.0f && float.IsFinite(lengthSquared)
            ? normal / MathF.Sqrt(lengthSquared)
            : new Vector3(float.NaN);
    }

    private static uint PackSnorm16x2(Vector2 value)
    {
        ushort x = unchecked((ushort)(short)QuantizeSnorm16(value.X));
        ushort y = unchecked((ushort)(short)QuantizeSnorm16(value.Y));
        return x | ((uint)y << 16);
    }

    private static Vector2 UnpackSnorm16x2(uint packed) => new(
        Math.Clamp(unchecked((short)(packed & 0xffffu)) / 32767.0f, -1.0f, 1.0f),
        Math.Clamp(unchecked((short)(packed >> 16)) / 32767.0f, -1.0f, 1.0f));

    private static int QuantizeSnorm16(float value)
    {
        float scaled = Math.Clamp(value, -1.0f, 1.0f) * 32767.0f;
        return scaled >= 0.0f
            ? (int)MathF.Floor(scaled + 0.5f)
            : (int)MathF.Ceiling(scaled - 0.5f);
    }

    private static uint RoundPositiveToUInt(float value) =>
        (uint)MathF.Floor(MathF.Max(value, 0.0f) + 0.5f);

    private static float SignNotZero(float value) => value >= 0.0f ? 1.0f : -1.0f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
