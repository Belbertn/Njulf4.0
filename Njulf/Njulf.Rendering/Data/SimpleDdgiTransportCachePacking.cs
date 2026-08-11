using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>
/// CPU oracle for the versioned Simple-DDGI ray scratch and persistent source
/// cache ABIs. The implementation deliberately mirrors GLSL word-for-word so
/// layout tests can validate real encoded payloads rather than only struct sizes.
/// </summary>
public static class SimpleDdgiTransportCachePacking
{
    public const uint GenerationMask = 0x00ff_ffffu;
    public const int ClassificationShift = 24;
    public const uint ClassificationMask = 0x7u << ClassificationShift;
    public const int DirectionEpochShift = 27;
    public const uint DirectionEpochMask = 0x1fu << DirectionEpochShift;

    public const int RayMetadataHitKindShift = 16;
    public const uint RayMetadataHitKindMask = 0x7u << RayMetadataHitKindShift;
    public const int RayMetadataEpochShift = 19;
    public const uint RayMetadataEpochMask = 0x1fu << RayMetadataEpochShift;
    public const uint RayMetadataValidFlag = 1u << 24;
    public const uint RayMetadataReservedMask = 0xfe00_0000u;
    public const float MaximumFiniteHalf = 65_504.0f;
    public const float MaximumTransportLuminance = 64.0f;

    public readonly record struct Sample(
        Vector3 SourceRadiance,
        float Distance,
        Vector3 Direction,
        Vector3 Normal,
        Vector3 DiffuseReflectance,
        Vector3 TransmittedDiffuseReflectance,
        float MaterialOcclusion,
        int HitKind,
        uint ProbeGeneration,
        uint SourceLightingGeneration,
        uint SourceEpoch,
        uint SourceRayCount);

    public readonly record struct PackingError(
        bool RadianceClamped,
        float MaximumRadianceAbsoluteError,
        float DistanceAbsoluteError);

    public static int Pack(
        SimpleDdgiTransportCacheFormat format,
        in Sample sample,
        Span<uint> destination,
        out PackingError error)
    {
        int wordCount = format.WordCount();
        if (wordCount == 0)
            throw new ArgumentOutOfRangeException(nameof(format));
        if (destination.Length < wordCount)
            throw new ArgumentException($"The {format} cache ABI requires {wordCount} words.", nameof(destination));
        if (!IsFiniteNonNegative(sample.SourceRadiance) ||
            !float.IsFinite(sample.Distance) || sample.Distance < 0.0f ||
            !IsFiniteDirection(sample.Direction) ||
            !IsFiniteDirection(sample.Normal) ||
            !IsFiniteNonNegative(sample.DiffuseReflectance) ||
            !IsFiniteNonNegative(sample.TransmittedDiffuseReflectance) ||
            !float.IsFinite(sample.MaterialOcclusion) ||
            sample.SourceRayCount is < 1u or > 256u ||
            sample.HitKind is < 0 or > 4 ||
            (sample.ProbeGeneration & GenerationMask) == 0u ||
            sample.SourceLightingGeneration == 0u ||
            sample.SourceEpoch == 0u)
        {
            destination[..wordCount].Clear();
            error = default;
            return 0;
        }

        destination[..wordCount].Clear();
        Vector3 boundedRadiance = ClampTransportRadiance(sample.SourceRadiance);
        if (format == SimpleDdgiTransportCacheFormat.Legacy36)
        {
            destination[0] = BitConverter.SingleToUInt32Bits(boundedRadiance.X);
            destination[1] = BitConverter.SingleToUInt32Bits(boundedRadiance.Y);
            destination[2] = BitConverter.SingleToUInt32Bits(boundedRadiance.Z);
            destination[3] = BitConverter.SingleToUInt32Bits(sample.Distance);
        }
        else
        {
            destination[0] = PackHalf2(boundedRadiance.X, boundedRadiance.Y);
        }
        float decodedDistance;
        if (format == SimpleDdgiTransportCacheFormat.Compact28)
        {
            destination[1] = PackHalf2(boundedRadiance.Z, 0.0f);
            destination[2] = BitConverter.SingleToUInt32Bits(sample.Distance);
            decodedDistance = sample.Distance;
        }
        else if (format == SimpleDdgiTransportCacheFormat.Compact24)
        {
            float boundedDistance = Math.Clamp(sample.Distance, 0.0f, MaximumFiniteHalf);
            destination[1] = PackHalf2(boundedRadiance.Z, boundedDistance);
            decodedDistance = UnpackHalf2(destination[1]).Y;
        }
        else
        {
            decodedDistance = sample.Distance;
        }

        int normalWord = format switch
        {
            SimpleDdgiTransportCacheFormat.Legacy36 => 5,
            SimpleDdgiTransportCacheFormat.Compact28 => 3,
            SimpleDdgiTransportCacheFormat.Compact24 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        if (format == SimpleDdgiTransportCacheFormat.Legacy36)
            destination[4] = PackOctahedralSnorm16(sample.Direction);
        destination[normalWord] = PackOctahedralSnorm16(sample.Normal);
        destination[normalWord + 1] = PackUnorm4x8(
            sample.DiffuseReflectance.X,
            sample.DiffuseReflectance.Y,
            sample.DiffuseReflectance.Z,
            sample.MaterialOcclusion);
        uint transmission = PackUnorm4x8(
            sample.TransmittedDiffuseReflectance.X,
            sample.TransmittedDiffuseReflectance.Y,
            sample.TransmittedDiffuseReflectance.Z,
            0.0f);
        if (format == SimpleDdgiTransportCacheFormat.Legacy36)
        {
            uint encodedRayCount = sample.SourceRayCount == 256u
                ? 0u
                : sample.SourceRayCount;
            transmission = (transmission & 0x00ff_ffffu) |
                (encodedRayCount << 24);
        }
        destination[normalWord + 2] = transmission;

        uint generationAndFlags =
            (sample.ProbeGeneration & GenerationMask) |
            (checked((uint)sample.HitKind + 1u) << ClassificationShift) |
            ((sample.SourceEpoch & 0x1fu) << DirectionEpochShift);
        int generationWord = wordCount - 1;
        destination[generationWord] = generationAndFlags;

        Vector3 decodedRadiance = format == SimpleDdgiTransportCacheFormat.Legacy36
            ? new Vector3(
                BitConverter.UInt32BitsToSingle(destination[0]),
                BitConverter.UInt32BitsToSingle(destination[1]),
                BitConverter.UInt32BitsToSingle(destination[2]))
            : new Vector3(
                UnpackHalf2(destination[0]),
                UnpackHalf2(destination[1]).X);
        error = new PackingError(
            RadianceClamped: boundedRadiance != sample.SourceRadiance,
            MaximumRadianceAbsoluteError: MaxAbs(decodedRadiance - sample.SourceRadiance),
            DistanceAbsoluteError: MathF.Abs(decodedDistance - sample.Distance));
        return wordCount;
    }

    public static bool TryUnpack(
        SimpleDdgiTransportCacheFormat format,
        ReadOnlySpan<uint> source,
        uint directionProbeIndex,
        uint directionRayIndex,
        uint maximumRayCount,
        uint expectedProbeGeneration,
        uint expectedSourceLightingGeneration,
        uint expectedSourceEpoch,
        uint expectedSourceRayCount,
        out Sample sample)
    {
        sample = default;
        int wordCount = format.WordCount();
        if (wordCount == 0 || source.Length < wordCount || maximumRayCount == 0 ||
            directionRayIndex >= maximumRayCount ||
            expectedProbeGeneration == 0u ||
            expectedSourceLightingGeneration == 0u ||
            expectedSourceEpoch == 0u ||
            expectedSourceRayCount is < 1u or > 256u ||
            expectedSourceRayCount > maximumRayCount)
        {
            return false;
        }

        int generationWord = wordCount - 1;
        uint generationAndFlags = source[generationWord];
        uint classificationCode = (generationAndFlags & ClassificationMask) >>
            ClassificationShift;
        if (classificationCode is < 1u or > 5u ||
            (generationAndFlags & GenerationMask) !=
                (expectedProbeGeneration & GenerationMask))
            return false;

        Vector2 rg = format == SimpleDdgiTransportCacheFormat.Legacy36
            ? default
            : UnpackHalf2(source[0]);
        Vector2 bAndDistance = format == SimpleDdgiTransportCacheFormat.Legacy36
            ? default
            : UnpackHalf2(source[1]);
        float distance = format switch
        {
            SimpleDdgiTransportCacheFormat.Legacy36 =>
                BitConverter.UInt32BitsToSingle(source[3]),
            SimpleDdgiTransportCacheFormat.Compact28 =>
                BitConverter.UInt32BitsToSingle(source[2]),
            _ => bAndDistance.Y
        };
        Vector3 radiance = format == SimpleDdgiTransportCacheFormat.Legacy36
            ? new Vector3(
                BitConverter.UInt32BitsToSingle(source[0]),
                BitConverter.UInt32BitsToSingle(source[1]),
                BitConverter.UInt32BitsToSingle(source[2]))
            : new Vector3(rg, bAndDistance.X);
        if (!IsFiniteNonNegative(radiance) || !float.IsFinite(distance) || distance < 0.0f ||
            (format == SimpleDdgiTransportCacheFormat.Compact28 &&
                (source[1] & 0xffff_0000u) != 0u))
        {
            return false;
        }

        int normalWord = format switch
        {
            SimpleDdgiTransportCacheFormat.Legacy36 => 5,
            SimpleDdgiTransportCacheFormat.Compact28 => 3,
            SimpleDdgiTransportCacheFormat.Compact24 => 2,
            _ => 0
        };
        Vector4 surface = UnpackUnorm4x8(source[normalWord + 1]);
        uint transmissionWord = source[normalWord + 2];
        Vector4 transmission = UnpackUnorm4x8(transmissionWord);
        uint sourceLightingGeneration;
        uint sourceEpoch;
        uint sourceRayCount;
        Vector3 direction;
        if (format == SimpleDdgiTransportCacheFormat.Legacy36)
        {
            uint encodedRayCount = transmissionWord >> 24;
            sourceRayCount = encodedRayCount == 0u ? 256u : encodedRayCount;
            uint cachedEpoch = (generationAndFlags & DirectionEpochMask) >>
                DirectionEpochShift;
            if (cachedEpoch != (expectedSourceEpoch & 0x1fu))
            {
                return false;
            }
            sourceLightingGeneration = expectedSourceLightingGeneration;
            sourceEpoch = expectedSourceEpoch;
            direction = UnpackOctahedralSnorm16(source[4]);
        }
        else
        {
            if ((transmissionWord & 0xff00_0000u) != 0u)
                return false;
            uint cachedEpoch = (generationAndFlags & DirectionEpochMask) >>
                DirectionEpochShift;
            if (cachedEpoch != (expectedSourceEpoch & 0x1fu))
                return false;
            sourceLightingGeneration = expectedSourceLightingGeneration;
            sourceEpoch = expectedSourceEpoch;
            sourceRayCount = expectedSourceRayCount;
            direction = SimpleDdgiDirectionCodebook.ReconstructDirection(
                directionProbeIndex,
                directionRayIndex,
                maximumRayCount,
                sourceEpoch);
        }

        if (sourceRayCount != expectedSourceRayCount ||
            sourceLightingGeneration != expectedSourceLightingGeneration ||
            sourceEpoch != expectedSourceEpoch)
        {
            return false;
        }

        int hitKind = checked((int)classificationCode - 1);
        sample = new Sample(
            radiance,
            distance,
            direction,
            UnpackOctahedralSnorm16(source[normalWord]),
            surface.Xyz(),
            transmission.Xyz(),
            surface.W,
            hitKind,
            generationAndFlags & GenerationMask,
            sourceLightingGeneration,
            sourceEpoch,
            sourceRayCount);
        return true;
    }

    /// <summary>
    /// CPU oracle for the shader's radiometric-only cache update. The method
    /// validates geometric provenance first and mutates only RGB source
    /// radiance; distance and every conditional hit/provenance word are
    /// preserved exactly.
    /// </summary>
    public static bool TryUpdateRadiance(
        SimpleDdgiTransportCacheFormat format,
        Span<uint> record,
        uint expectedProbeGeneration,
        uint expectedSourceEpoch,
        Vector3 sourceRadiance)
    {
        int wordCount = format.WordCount();
        if (wordCount == 0 || record.Length < wordCount ||
            expectedProbeGeneration == 0u || expectedSourceEpoch == 0u ||
            !IsFiniteNonNegative(sourceRadiance))
        {
            return false;
        }

        uint generationAndFlags = record[wordCount - 1];
        uint classificationCode =
            (generationAndFlags & ClassificationMask) >> ClassificationShift;
        uint directionEpoch =
            (generationAndFlags & DirectionEpochMask) >> DirectionEpochShift;
        if (classificationCode is < 1u or > 5u ||
            (generationAndFlags & GenerationMask) !=
                (expectedProbeGeneration & GenerationMask) ||
            directionEpoch != (expectedSourceEpoch & 0x1fu))
        {
            return false;
        }

        Vector3 bounded = ClampTransportRadiance(sourceRadiance);
        if (format == SimpleDdgiTransportCacheFormat.Legacy36)
        {
            record[0] = BitConverter.SingleToUInt32Bits(bounded.X);
            record[1] = BitConverter.SingleToUInt32Bits(bounded.Y);
            record[2] = BitConverter.SingleToUInt32Bits(bounded.Z);
            return true;
        }

        record[0] = PackHalf2(bounded.X, bounded.Y);
        uint preservedDistance =
            format == SimpleDdgiTransportCacheFormat.Compact24
                ? record[1] & 0xffff_0000u
                : 0u;
        record[1] =
            (PackHalf2(bounded.Z, 0.0f) & 0x0000_ffffu) |
            preservedDistance;
        return true;
    }

    public static uint PackRayMetadata(
        float visibilityDistance,
        int hitKind,
        uint sourceEpoch)
    {
        if (!float.IsFinite(visibilityDistance) || visibilityDistance < 0.0f ||
            hitKind is < 0 or > 4 || sourceEpoch == 0u)
        {
            return 0u;
        }
        ushort distance = BitConverter.HalfToUInt16Bits(
            (Half)Math.Clamp(visibilityDistance, 0.0f, MaximumFiniteHalf));
        uint exactHitKind = checked((uint)Math.Clamp(hitKind, 0, 4));
        return distance |
            (exactHitKind << RayMetadataHitKindShift) |
            ((sourceEpoch & 0x1fu) << RayMetadataEpochShift) |
            RayMetadataValidFlag;
    }

    public static bool TryUnpackRayMetadata(
        uint metadata,
        out float visibilityDistance,
        out int hitKind,
        out uint directionEpoch)
    {
        visibilityDistance = (float)BitConverter.UInt16BitsToHalf(
            checked((ushort)(metadata & 0xffffu)));
        hitKind = checked((int)((metadata & RayMetadataHitKindMask) >>
            RayMetadataHitKindShift));
        directionEpoch = (metadata & RayMetadataEpochMask) >>
            RayMetadataEpochShift;
        return (metadata & RayMetadataValidFlag) != 0u &&
            (metadata & RayMetadataReservedMask) == 0u &&
            float.IsFinite(visibilityDistance) &&
            visibilityDistance >= 0.0f &&
            hitKind <= 4;
    }

    public static Vector3 ClampTransportRadiance(Vector3 value)
    {
        if (!IsFiniteNonNegative(value))
            return Vector3.Zero;
        float peak = MathF.Max(value.X, MathF.Max(value.Y, value.Z));
        if (peak <= 0.0f)
            return Vector3.Zero;

        // Normalize before the dot product so every finite binary32 input,
        // including float.MaxValue, remains finite. Computing luminance first
        // can overflow and turn a valid bright ray into black through an
        // infinity-derived zero scale.
        Vector3 normalized = value / peak;
        float normalizedLuminance = Vector3.Dot(
            normalized,
            new Vector3(0.2126f, 0.7152f, 0.0722f));
        float scale = normalizedLuminance > 0.0f &&
            peak > MaximumTransportLuminance / normalizedLuminance
                ? (MaximumTransportLuminance / peak) / normalizedLuminance
                : 1.0f;
        return Vector3.Clamp(value * scale, Vector3.Zero,
            new Vector3(MaximumFiniteHalf));
    }

    public static uint PackOctahedralSnorm16(Vector3 direction)
    {
        if (!IsFiniteDirection(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        direction = Vector3.Normalize(direction);
        float invL1 = 1.0f /
            (MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z));
        Vector3 n = direction * invL1;
        Vector2 encoded = n.Z >= 0.0f
            ? new Vector2(n.X, n.Y)
            : new Vector2(
                (1.0f - MathF.Abs(n.Y)) * CopySignOne(n.X),
                (1.0f - MathF.Abs(n.X)) * CopySignOne(n.Y));
        short x = PackSnorm16(encoded.X);
        short y = PackSnorm16(encoded.Y);
        return (ushort)x | ((uint)(ushort)y << 16);
    }

    public static Vector3 UnpackOctahedralSnorm16(uint packed)
    {
        float x = Math.Max(unchecked((short)(packed & 0xffffu)) / 32767.0f, -1.0f);
        float y = Math.Max(unchecked((short)(packed >> 16)) / 32767.0f, -1.0f);
        Vector3 decoded = new(x, y, 1.0f - MathF.Abs(x) - MathF.Abs(y));
        float t = Math.Clamp(-decoded.Z, 0.0f, 1.0f);
        decoded.X += decoded.X >= 0.0f ? -t : t;
        decoded.Y += decoded.Y >= 0.0f ? -t : t;
        return Vector3.Normalize(decoded);
    }

    private static uint PackHalf2(float x, float y) =>
        BitConverter.HalfToUInt16Bits((Half)x) |
        ((uint)BitConverter.HalfToUInt16Bits((Half)y) << 16);

    private static Vector2 UnpackHalf2(uint packed) => new(
        (float)BitConverter.UInt16BitsToHalf(checked((ushort)(packed & 0xffffu))),
        (float)BitConverter.UInt16BitsToHalf(checked((ushort)(packed >> 16))));

    private static uint PackUnorm4x8(float x, float y, float z, float w) =>
        PackUnorm8(x) |
        ((uint)PackUnorm8(y) << 8) |
        ((uint)PackUnorm8(z) << 16) |
        ((uint)PackUnorm8(w) << 24);

    private static Vector4 UnpackUnorm4x8(uint packed) => new(
        (packed & 0xffu) / 255.0f,
        ((packed >> 8) & 0xffu) / 255.0f,
        ((packed >> 16) & 0xffu) / 255.0f,
        (packed >> 24) / 255.0f);

    private static byte PackUnorm8(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f),
        0,
        255));

    private static short PackSnorm16(float value) => checked((short)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, -1.0f, 1.0f) * 32767.0f),
        -32767,
        32767));

    private static bool IsFiniteNonNegative(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        value.X >= 0.0f && value.Y >= 0.0f && value.Z >= 0.0f;

    private static bool IsFiniteDirection(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        value.LengthSquared() > 1.0e-12f;

    private static float MaxAbs(Vector3 value) => MathF.Max(
        MathF.Abs(value.X),
        MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    private static float CopySignOne(float value) => value >= 0.0f ? 1.0f : -1.0f;

    private static Vector3 Xyz(this Vector4 value) => new(value.X, value.Y, value.Z);
}
