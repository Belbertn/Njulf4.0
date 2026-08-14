using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>
/// Canonical direction reconstruction shared with <c>ddgi_simple_shared.glsl</c>.
/// Quaternion components are checked-in IEEE-754 payloads; runtime code never
/// regenerates the table with platform-dependent trigonometry.
/// </summary>
public static class SimpleDdgiDirectionCodebook
{
    public const uint Version = 3;
    public const int RotationCount = 32;
    public const uint EpochMask = RotationCount - 1;

    // Generated once with the documented integer hash/Shoemake construction:
    // salts 0x243f6a88, 0x85a308d3, 0x13198a2e; normalized as binary32.
    private static ReadOnlySpan<uint> RotationBits =>
    [
        0xbf5bd755, 0xbebab075, 0x3ca7887c, 0x3eb80372,
        0x3e6cc936, 0xbf4054f9, 0x3f161892, 0x3e4874c2,
        0x3eac774a, 0xbf4c3fd2, 0x3edfc806, 0xbe78992f,
        0x3e8e3aa2, 0xbe75df8f, 0x3f1f4b91, 0xbf30fd68,
        0xbeb0bf0c, 0xbe3a07e6, 0x3f338432, 0x3f18c3cc,
        0xbebc6ba9, 0xbe69227c, 0x3eba2b0e, 0x3f532f2b,
        0xbed7e49a, 0x3f01d379, 0x3e9871c4, 0xbf30b056,
        0x3f0f09f9, 0xbe6572e7, 0x3e28c577, 0x3f480354,
        0x3ea53a7b, 0xbf5c3e38, 0xbebe24a5, 0xbe088ba0,
        0xbf0abee3, 0x3e42f1d1, 0xbf093229, 0x3f1e641b,
        0x3f4eee35, 0x3f148c02, 0xbdc995a8, 0x3c74e75b,
        0x3f2fb909, 0xbe9f1263, 0xbe9a9b07, 0x3f1584c6,
        0xbda8356e, 0xbf1498a0, 0x3f4d1e76, 0xbdf52ebd,
        0xbed91c5a, 0xbf1f9b16, 0xbf00b171, 0x3ed87aa3,
        0xbba366ac, 0xbf370fa7, 0x3ea02148, 0x3f200a13,
        0xbc8c26bb, 0xbee85062, 0xbf640302, 0x3cbd5cc1,
        0x3f026b2a, 0xbe84e2e1, 0xbf4535b3, 0xbe9081f2,
        0xbe041713, 0xbe06180f, 0xbf74c7c8, 0x3e69623c,
        0x3e307ec5, 0xbef062b7, 0x3f394a01, 0x3ef36619,
        0xbcd7d778, 0xbf0aebde, 0x3e9b4dcd, 0xbf486747,
        0x3f2314c5, 0xbf344390, 0x3e81d358, 0x3e3cf7b2,
        0x3f5193d1, 0x3c92ceb4, 0xbf123561, 0xbd6aca1d,
        0xbeff1a77, 0x3f072cd2, 0xbf1bfc4f, 0xbea3406c,
        0x3f34a557, 0x3e3d736a, 0xbf053cb7, 0x3ee33966,
        0x3f1f64cc, 0xbea0bbd6, 0xbf03a6b4, 0xbeffa577,
        0x3e7d6bca, 0x3e92524c, 0x3ed236b9, 0x3f546b76,
        0xbec9e9a1, 0x3f314df4, 0xbe8ce5b0, 0x3f09a310,
        0x3eb479e5, 0xbf31a3e3, 0xbe9581c3, 0xbf0e4c85,
        0x3ebf3f88, 0x3e34d2f2, 0x3ee4f4cf, 0xbf4b1591,
        0x3f3c12d0, 0x3d8ae5fc, 0xbe99b488, 0x3f1ac77a,
        0xbf321385, 0xbf02c48d, 0xbee07239, 0xbe808a35,
        0xbea78718, 0xbef28400, 0xbf50f78b, 0x3d434881
    ];

    public static Quaternion GetRotation(uint sourceEpoch)
    {
        int word = checked((int)((sourceEpoch & EpochMask) * 4u));
        ReadOnlySpan<uint> bits = RotationBits;
        return new Quaternion(
            BitConverter.UInt32BitsToSingle(bits[word]),
            BitConverter.UInt32BitsToSingle(bits[word + 1]),
            BitConverter.UInt32BitsToSingle(bits[word + 2]),
            BitConverter.UInt32BitsToSingle(bits[word + 3]));
    }

    public static uint GetComponentBits(int epoch, int component)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(epoch, RotationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(component);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(component, 4);
        return RotationBits[epoch * 4 + component];
    }

    public static uint ResolveDirectionRayIndex(
        uint localRayOrdinal,
        uint activeRayCount,
        uint sourceRayCount,
        uint maximumRayCount)
    {
        if (activeRayCount == 0 || sourceRayCount == 0 ||
            maximumRayCount == 0 || localRayOrdinal >= activeRayCount ||
            activeRayCount > sourceRayCount ||
            sourceRayCount > maximumRayCount)
        {
            throw new ArgumentOutOfRangeException(nameof(activeRayCount));
        }

        // Version 3 embeds every supported source cardinality into one
        // maximum-cardinality Fibonacci lattice. Promotion fills unused slots
        // without reinterpreting cached directions, and maintenance selects a
        // deterministic nested subset with the same low quadrature error.
        uint sourceOrdinal = (uint)Math.Min(
            (ulong)localRayOrdinal * sourceRayCount / activeRayCount,
            sourceRayCount - 1UL);
        return (uint)((ulong)sourceOrdinal * maximumRayCount / sourceRayCount);
    }

    public static Vector3 ReconstructDirection(
        uint probeIndex,
        uint directionRayIndex,
        uint maximumRayCount,
        uint sourceEpoch)
    {
        if (maximumRayCount == 0 || directionRayIndex >= maximumRayCount)
            throw new ArgumentOutOfRangeException(nameof(directionRayIndex));

        Quaternion rotation = Quaternion.Normalize(
            Quaternion.Multiply(GetRotation(sourceEpoch), ProbeRotation(probeIndex)));
        float i = directionRayIndex;
        float count = maximumRayCount;
        float z = 1.0f - 2.0f * (i + 0.5f) / count;
        float radius = MathF.Sqrt(MathF.Max(0.0f, 1.0f - z * z));
        float angle = 2.399963229728653f * i;
        Vector3 unrotated = new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, z);
        Vector3 direction = Vector3.Normalize(Vector3.Transform(unrotated, rotation));
        return SimpleDdgiTransportCachePacking.UnpackOctahedralSnorm16(
            SimpleDdgiTransportCachePacking.PackOctahedralSnorm16(direction));
    }

    private static Quaternion ProbeRotation(uint probeIndex)
    {
        float u1 = HashToUnitFloat(probeIndex, 0x9e3779b9);
        float u2 = HashToUnitFloat(probeIndex, 0x7f4a7c15);
        float u3 = HashToUnitFloat(probeIndex, 0x94d049bb);
        float r1 = MathF.Sqrt(MathF.Max(0.0f, 1.0f - u1));
        float r2 = MathF.Sqrt(MathF.Max(0.0f, u1));
        float theta1 = 2.0f * MathF.PI * u2;
        float theta2 = 2.0f * MathF.PI * u3;
        return Quaternion.Normalize(new Quaternion(
            r1 * MathF.Sin(theta1),
            r1 * MathF.Cos(theta1),
            r2 * MathF.Sin(theta2),
            r2 * MathF.Cos(theta2)));
    }

    private static float HashToUnitFloat(uint value, uint salt) =>
        (Hash(value ^ salt) >> 8) * (1.0f / 16_777_216.0f);

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return value;
        }
    }

}
