using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

public readonly record struct SimpleDdgiRadianceSample(
    Vector3 DirectionFromProbe,
    Vector3 IncidentRadiance,
    bool Valid = true);

public readonly record struct SimpleDdgiRadianceProjection(
    Vector3[] Coefficients,
    int ValidSampleCount,
    float Coverage,
    float CoefficientEnergy)
{
    public static SimpleDdgiRadianceProjection Empty { get; } = new(
        new Vector3[SimpleDdgiRadianceShL2.CoefficientCount],
        0,
        0f,
        0f);
}

/// <summary>
/// Canonical real-L2 incident-radiance convention shared by CPU references and
/// production GLSL. Directions point from a probe toward the traced source.
/// </summary>
public static class SimpleDdgiRadianceShL2
{
    public const int CoefficientCount = 9;
    public const int ColorValueCount = CoefficientCount * 3;
    public const int PackedCoefficientWordCount = 14;
    public const int RecordSizeBytes = 64;
    public const uint RepresentationVersion = 1;

    private const uint ValidBit = 1u << 0;
    private const uint HistoryBit = 1u << 1;
    private const int SampleCountShift = 2;
    private const uint SampleCountMask = 0xffu << SampleCountShift;
    private const int QualityShift = 10;
    private const uint QualityMask = 0x0fu << QualityShift;
    private const int VersionShift = 14;
    private const uint VersionMask = 0xffu << VersionShift;
    private const int ChecksumShift = 22;
    private const uint ChecksumMask = 0x3ffu << ChecksumShift;
    private const float MaximumFiniteHalf = 65_504f;

    public static void EvaluateBasis(Vector3 direction, Span<float> destination)
    {
        if (destination.Length < CoefficientCount)
            throw new ArgumentException("Nine SH basis values are required.", nameof(destination));

        float lengthSquared = direction.LengthSquared();
        if (!(lengthSquared > 1e-20f) || !float.IsFinite(lengthSquared))
        {
            destination[..CoefficientCount].Clear();
            return;
        }

        Vector3 omega = direction / MathF.Sqrt(lengthSquared);
        float x = omega.X;
        float y = omega.Y;
        float z = omega.Z;
        destination[0] = 0.2820947918f;
        destination[1] = -0.4886025119f * y;
        destination[2] = 0.4886025119f * z;
        destination[3] = -0.4886025119f * x;
        destination[4] = 1.0925484306f * x * y;
        destination[5] = -1.0925484306f * y * z;
        destination[6] = 0.3153915653f * (3f * z * z - 1f);
        destination[7] = -1.0925484306f * x * z;
        destination[8] = 0.5462742153f * (x * x - y * y);
    }

    public static SimpleDdgiRadianceProjection Project(
        ReadOnlySpan<SimpleDdgiRadianceSample> samples)
    {
        if (samples.IsEmpty)
            return SimpleDdgiRadianceProjection.Empty;

        var coefficients = new Vector3[CoefficientCount];
        int validCount = 0;
        Span<float> basis = stackalloc float[CoefficientCount];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            ref readonly SimpleDdgiRadianceSample sample = ref samples[sampleIndex];
            if (!sample.Valid ||
                !IsFinite(sample.DirectionFromProbe) ||
                !IsFinite(sample.IncidentRadiance) ||
                sample.DirectionFromProbe.LengthSquared() <= 1e-20f)
            {
                continue;
            }

            EvaluateBasis(sample.DirectionFromProbe, basis);
            for (int coefficient = 0; coefficient < CoefficientCount; coefficient++)
                coefficients[coefficient] += sample.IncidentRadiance * basis[coefficient];
            validCount++;
        }

        if (validCount == 0)
            return SimpleDdgiRadianceProjection.Empty;

        float normalization = 4f * MathF.PI / validCount;
        float energy = 0f;
        for (int coefficient = 0; coefficient < CoefficientCount; coefficient++)
        {
            coefficients[coefficient] *= normalization;
            energy += coefficients[coefficient].LengthSquared();
        }

        return new SimpleDdgiRadianceProjection(
            coefficients,
            validCount,
            (float)validCount / samples.Length,
            energy);
    }

    public static Vector3 Reconstruct(
        ReadOnlySpan<Vector3> coefficients,
        Vector3 direction,
        Vector3? bandScales = null)
    {
        if (coefficients.Length < CoefficientCount)
            throw new ArgumentException("Nine RGB SH coefficients are required.", nameof(coefficients));

        Span<float> basis = stackalloc float[CoefficientCount];
        EvaluateBasis(direction, basis);
        Vector3 scales = bandScales ?? Vector3.One;
        Vector3 value = Vector3.Zero;
        for (int coefficient = 0; coefficient < CoefficientCount; coefficient++)
        {
            int band = coefficient == 0 ? 0 : coefficient <= 3 ? 1 : 2;
            value += coefficients[coefficient] * basis[coefficient] * scales[band];
        }
        return value;
    }

    public static Vector3[] TemporalBlend(
        ReadOnlySpan<Vector3> history,
        ReadOnlySpan<Vector3> current,
        float historyWeight)
    {
        if (history.Length < CoefficientCount || current.Length < CoefficientCount)
            throw new ArgumentException("Both SH records must contain nine coefficients.");

        float weight = Math.Clamp(historyWeight, 0f, 0.995f);
        var result = new Vector3[CoefficientCount];
        for (int index = 0; index < CoefficientCount; index++)
            result[index] = Vector3.Lerp(current[index], history[index], weight);
        return result;
    }

    public static bool TryPack(
        ReadOnlySpan<Vector3> coefficients,
        uint slotGeneration,
        int validSampleCount,
        int qualityLevel,
        bool hasHistory,
        out GPUSimpleDdgiRadianceShL2 packed)
    {
        packed = default;
        if (coefficients.Length < CoefficientCount || slotGeneration == 0)
            return false;

        Span<uint> words = MemoryMarshal.CreateSpan(ref packed.Word0, 16);
        int valueIndex = 0;
        for (int coefficient = 0; coefficient < CoefficientCount; coefficient++)
        {
            Vector3 value = coefficients[coefficient];
            if (!CanPack(value))
            {
                packed = default;
                return false;
            }

            PackHalf(words, valueIndex++, value.X);
            PackHalf(words, valueIndex++, value.Y);
            PackHalf(words, valueIndex++, value.Z);
        }

        // Half 27 is reserved and remains zero.
        words[14] = slotGeneration;
        uint metadata = ValidBit |
            (hasHistory ? HistoryBit : 0u) |
            ((uint)Math.Clamp(validSampleCount, 0, 255) << SampleCountShift) |
            ((uint)Math.Clamp(qualityLevel, 0, 15) << QualityShift) |
            (RepresentationVersion << VersionShift);
        uint checksum = ComputeChecksum(words[..15], metadata) & 0x3ffu;
        words[15] = metadata | (checksum << ChecksumShift);
        return true;
    }

    public static bool TryUnpack(
        in GPUSimpleDdgiRadianceShL2 packed,
        uint expectedSlotGeneration,
        out Vector3[] coefficients,
        out int validSampleCount,
        out int qualityLevel,
        out bool hasHistory)
    {
        coefficients = Array.Empty<Vector3>();
        validSampleCount = 0;
        qualityLevel = 0;
        hasHistory = false;
        ReadOnlySpan<uint> words = MemoryMarshal.CreateReadOnlySpan(
            in packed.Word0,
            16);
        uint metadata = words[15];
        uint metadataWithoutChecksum = metadata & ~ChecksumMask;
        if ((metadata & ValidBit) == 0 ||
            ((metadata & VersionMask) >> VersionShift) != RepresentationVersion ||
            words[14] != expectedSlotGeneration ||
            ((metadata & ChecksumMask) >> ChecksumShift) !=
                (ComputeChecksum(words[..15], metadataWithoutChecksum) & 0x3ffu))
        {
            return false;
        }

        var unpacked = new Vector3[CoefficientCount];
        int valueIndex = 0;
        for (int coefficient = 0; coefficient < CoefficientCount; coefficient++)
        {
            Vector3 value = new(
                UnpackHalf(words, valueIndex++),
                UnpackHalf(words, valueIndex++),
                UnpackHalf(words, valueIndex++));
            if (!IsFinite(value))
                return false;
            unpacked[coefficient] = value;
        }

        coefficients = unpacked;
        validSampleCount = (int)((metadata & SampleCountMask) >> SampleCountShift);
        qualityLevel = (int)((metadata & QualityMask) >> QualityShift);
        hasHistory = (metadata & HistoryBit) != 0;
        return true;
    }

    /// <summary>
    /// Mirrors the checked GLSL receiver path. A zero-sample record is a valid
    /// publication witness but deliberately has no directional authority.
    /// </summary>
    public static bool TryEvaluateRecord(
        in GPUSimpleDdgiRadianceShL2 packed,
        uint expectedSlotGeneration,
        Vector3 direction,
        float perceptualRoughness,
        out Vector3 radiance,
        out Vector3 negativeReconstruction)
    {
        radiance = Vector3.Zero;
        negativeReconstruction = Vector3.Zero;
        float directionLengthSquared = direction.LengthSquared();
        if (!(directionLengthSquared > 1e-12f) ||
            !float.IsFinite(directionLengthSquared) ||
            !float.IsFinite(perceptualRoughness) ||
            !TryUnpack(
                packed,
                expectedSlotGeneration,
                out Vector3[] coefficients,
                out int validSampleCount,
                out _,
                out _) ||
            validSampleCount == 0)
        {
            return false;
        }

        Vector3 reconstructed = Reconstruct(
            coefficients,
            direction / MathF.Sqrt(directionLengthSquared),
            SimpleDdgiGgxBandScaleTable.Evaluate(perceptualRoughness));
        if (!IsFinite(reconstructed))
            return false;

        negativeReconstruction = Vector3.Max(-reconstructed, Vector3.Zero);
        radiance = Vector3.Max(reconstructed, Vector3.Zero);
        return true;
    }

    private static void PackHalf(Span<uint> words, int valueIndex, float value)
    {
        int wordIndex = valueIndex >> 1;
        int halfShift = (valueIndex & 1) * 16;
        uint bits = BitConverter.HalfToUInt16Bits((Half)value);
        words[wordIndex] |= bits << halfShift;
    }

    private static float UnpackHalf(ReadOnlySpan<uint> words, int valueIndex)
    {
        int wordIndex = valueIndex >> 1;
        int halfShift = (valueIndex & 1) * 16;
        ushort bits = (ushort)(words[wordIndex] >> halfShift);
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static uint ComputeChecksum(ReadOnlySpan<uint> words, uint metadata)
    {
        uint hash = 2166136261u;
        for (int index = 0; index < words.Length; index++)
        {
            hash ^= words[index];
            hash *= 16777619u;
        }
        hash ^= metadata;
        hash *= 16777619u;
        hash ^= hash >> 16;
        return hash;
    }

    private static bool CanPack(Vector3 value) =>
        IsFinite(value) &&
        MathF.Abs(value.X) <= MaximumFiniteHalf &&
        MathF.Abs(value.Y) <= MaximumFiniteHalf &&
        MathF.Abs(value.Z) <= MaximumFiniteHalf;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Checked-in GGX spherical-band multipliers generated from the renderer's
/// normalized <c>D_GGX(mu) * mu</c> prefilter kernel. DC is exactly preserved.
/// </summary>
public static class SimpleDdgiGgxBandScaleTable
{
    public const int EntryCount = 17;
    public const uint TableVersion = 1;

    private static readonly Vector4[] EntryData =
    [
        new(0.000000f, 1.000000f, 1.000000f, 1.000000f),
        new(0.062500f, 1.000000f, 0.999920f, 0.999768f),
        new(0.125000f, 1.000000f, 0.999059f, 0.997319f),
        new(0.187500f, 1.000000f, 0.996234f, 0.989412f),
        new(0.250000f, 1.000000f, 0.990308f, 0.973136f),
        new(0.312500f, 1.000000f, 0.980439f, 0.946599f),
        new(0.375000f, 1.000000f, 0.966179f, 0.909141f),
        new(0.437500f, 1.000000f, 0.947472f, 0.861241f),
        new(0.500000f, 1.000000f, 0.924593f, 0.804257f),
        new(0.562500f, 1.000000f, 0.898061f, 0.740092f),
        new(0.625000f, 1.000000f, 0.868541f, 0.670879f),
        new(0.687500f, 1.000000f, 0.836758f, 0.598731f),
        new(0.750000f, 1.000000f, 0.803430f, 0.525559f),
        new(0.812500f, 1.000000f, 0.769222f, 0.452980f),
        new(0.875000f, 1.000000f, 0.734716f, 0.382274f),
        new(0.937500f, 1.000000f, 0.700400f, 0.314395f),
        new(1.000000f, 1.000000f, 0.666667f, 0.250000f)
    ];

    private static ReadOnlySpan<Vector4> Entries => EntryData;

    public static Vector3 Evaluate(float perceptualRoughness)
    {
        float roughness = Math.Clamp(perceptualRoughness, 0f, 1f);
        float coordinate = roughness * (EntryCount - 1);
        int lower = Math.Min(EntryCount - 1, (int)coordinate);
        int upper = Math.Min(EntryCount - 1, lower + 1);
        float fraction = coordinate - lower;
        Vector4 a = Entries[lower];
        Vector4 b = Entries[upper];
        return Vector3.Lerp(
            new Vector3(a.Y, a.Z, a.W),
            new Vector3(b.Y, b.Z, b.W),
            fraction);
    }

    public static uint Checksum
    {
        get
        {
            uint hash = 2166136261u;
            foreach (Vector4 entry in Entries)
            {
                Add(BitConverter.SingleToUInt32Bits(entry.X));
                Add(BitConverter.SingleToUInt32Bits(entry.Y));
                Add(BitConverter.SingleToUInt32Bits(entry.Z));
                Add(BitConverter.SingleToUInt32Bits(entry.W));
            }
            return hash;

            void Add(uint value)
            {
                hash ^= value;
                hash *= 16777619u;
            }
        }
    }
}

public readonly record struct DdgiIndirectSpecularOwnership(
    float ScreenOrGeometricWeight,
    float LocalReflectionProbeWeight,
    float DdgiDirectionalRadianceWeight,
    float EnvironmentWeight)
{
    public float Sum => ScreenOrGeometricWeight +
        LocalReflectionProbeWeight +
        DdgiDirectionalRadianceWeight +
        EnvironmentWeight;
}

/// <summary>Normalized priority selector; sources are never independently added.</summary>
public static class DdgiIndirectSpecularSelector
{
    public static DdgiIndirectSpecularOwnership Select(
        float screenOrGeometricConfidence,
        float localReflectionProbeConfidence,
        float ddgiConfidence,
        float perceptualRoughness,
        float ddgiMinimumRoughness,
        float ddgiFullWeightRoughness)
    {
        float screen = SaturateFinite(screenOrGeometricConfidence);
        float local = SaturateFinite(localReflectionProbeConfidence);
        float ddgi = SaturateFinite(ddgiConfidence);
        float minimum = Math.Clamp(ddgiMinimumRoughness, 0f, 1f);
        float full = Math.Clamp(ddgiFullWeightRoughness, minimum + 1e-4f, 1f);
        float roughnessWeight = SmoothStep(
            minimum,
            full,
            Math.Clamp(perceptualRoughness, 0f, 1f));

        float remaining = 1f;
        float screenWeight = remaining * screen;
        remaining -= screenWeight;
        float localWeight = remaining * local;
        remaining -= localWeight;
        float ddgiWeight = remaining * ddgi * roughnessWeight;
        remaining -= ddgiWeight;
        float environmentWeight = MathF.Max(0f, remaining);

        // Assign the tiny floating-point residual to the canonical fallback so
        // the weights sum to one before a single split-sum BRDF application.
        float sum = screenWeight + localWeight + ddgiWeight + environmentWeight;
        environmentWeight += 1f - sum;
        return new DdgiIndirectSpecularOwnership(
            screenWeight,
            localWeight,
            ddgiWeight,
            environmentWeight);
    }

    private static float SaturateFinite(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
