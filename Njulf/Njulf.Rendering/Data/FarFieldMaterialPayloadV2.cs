using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Versioned ABI and deterministic CPU oracle for the far-field material V2
/// resolve family. Payload version 3 retains the eight-word V2 layout and adds
/// an order-independent geometric-normal cone so an overlapping surface cannot
/// be hidden solely because the selected dominant surface faces another way.
/// Version 4 uses word 4's formerly reserved high half for material occlusion;
/// versions 2-3 decode that field as the neutral value one.
/// The GPU first selects a quantized dominant-surface key, then uses the complete
/// stable primitive key only to break physically equivalent ties before
/// publishing one payload.
/// </summary>
public static class FarFieldMaterialPayloadV2
{
    public const uint PayloadVersion = 4;
    public const uint MaterialOcclusionPayloadVersion = 4;
    public const uint VoxelStrideWords = 8;
    public const uint EmptyWinnerKey = uint.MaxValue;
    public const uint OccupiedBit = 1u << 31;
    public const uint StoredFlagMask = 0x7fffu;
    public const float MaximumFiniteHalf = 65_504f;

    /// <summary>
    /// Candidate payload plus transient voxel-local selection evidence.
    /// NormalCone is the conservative maximum geometric-normal deviation from
    /// GeometricNormal, normalized by PI: zero is one exact normal and one spans
    /// the full sphere. Individual triangle candidates supply their cooked
    /// material-local cone, which is commonly zero.
    /// MaterialOcclusion is the glTF-strength-adjusted indirect-diffuse factor.
    /// NormalizedSurfaceDistance and NormalFacing are used only for dominance
    /// resolution; the selected material payload does not serialize them.
    /// </summary>
    public readonly record struct Candidate(
        uint StablePrimitiveKey,
        float Coverage,
        Vector3 DiffuseReflectance,
        Vector3 EmissiveRadiance,
        Vector3 GeometricNormal,
        float NormalCone,
        uint MaterialFlags,
        uint MaterialRevision,
        uint TransportProfileRevision,
        float NormalizedSurfaceDistance = 0f,
        float NormalFacing = 1f,
        float MaterialOcclusion = 1f);

    /// <summary>
    /// ConflictCount is exactly the number of participating candidates rejected
    /// by the resolve, and is therefore bounded to [0, candidate count - 1].
    /// </summary>
    public readonly record struct ResolveResult(
        GPUFarFieldMaterialVoxelV2 Payload,
        int ConflictCount);

    /// <summary>
    /// Emulates the GPU dominance, stable-tie, and payload passes. Candidate
    /// order cannot affect the selected surface or its packed payload.
    /// Coverage dominates proximity, proximity dominates normal facing, and
    /// the stable primitive key is consulted only when all three quantized
    /// physical terms are equal.
    /// </summary>
    public static ResolveResult Resolve(ReadOnlySpan<Candidate> candidates)
    {
        uint winningSelectionKey = EmptyWinnerKey;
        int participatingCount = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = Validate(candidates[i]);
            if (candidate.Coverage <= 0f)
                continue;
            if (candidate.StablePrimitiveKey == EmptyWinnerKey)
                throw new ArgumentOutOfRangeException(
                    nameof(candidates),
                    "UInt32.MaxValue is reserved for an empty V2 voxel.");

            participatingCount++;
            winningSelectionKey = Math.Min(
                winningSelectionKey,
                ComputeSelectionKey(candidate));
        }

        if (winningSelectionKey == EmptyWinnerKey)
        {
            return new ResolveResult(
                new GPUFarFieldMaterialVoxelV2 { WinnerKey = EmptyWinnerKey },
                ConflictCount: 0);
        }

        uint winnerKey = EmptyWinnerKey;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = Validate(candidates[i]);
            if (candidate.Coverage <= 0f ||
                ComputeSelectionKey(candidate) != winningSelectionKey)
            {
                continue;
            }

            winnerKey = Math.Min(winnerKey, candidate.StablePrimitiveKey);
        }

        if (winnerKey == EmptyWinnerKey)
            throw new InvalidOperationException("The selected far-field V2 dominance key had no stable tie-break candidate.");

        Candidate winner = default;
        bool foundWinner = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = candidates[i];
            if (candidate.Coverage <= 0f || candidate.StablePrimitiveKey != winnerKey)
                continue;

            if (foundWinner && candidate != winner)
            {
                throw new InvalidOperationException(
                    "A far-field V2 stable primitive key identified two different payloads.");
            }

            winner = candidate;
            foundWinner = true;
        }

        if (!foundWinner)
            throw new InvalidOperationException("The selected far-field V2 winner was not found in the payload pass.");

        Candidate resolvedWinner = Validate(winner) with
        {
            NormalCone = ResolveConservativeNormalCone(winner, candidates)
        };
        return new ResolveResult(
            Pack(resolvedWinner),
            ConflictCount: Math.Max(participatingCount - 1, 0));
    }

    /// <summary>
    /// Returns the atomic-min key used by the GPU dominance pass. The inverse
    /// rank leaves UInt32.MaxValue reserved for an empty voxel.
    /// </summary>
    public static uint ComputeSelectionKey(in Candidate candidate)
    {
        Candidate value = Validate(candidate);
        if (value.Coverage <= 0f)
            return EmptyWinnerKey;

        uint coverage = PackSelectionUnorm8(Math.Max(value.Coverage, 1f / 255f));
        uint proximity = PackSelectionUnorm16(1f - value.NormalizedSurfaceDistance);
        uint facing = PackSelectionUnorm8(value.NormalFacing);
        uint dominanceRank = (coverage << 24) | (proximity << 8) | facing;
        return ~dominanceRank;
    }

    public static GPUFarFieldMaterialVoxelV2 Pack(in Candidate candidate)
    {
        Candidate value = Validate(candidate);
        if (value.StablePrimitiveKey == EmptyWinnerKey)
            throw new ArgumentOutOfRangeException(nameof(candidate), "The empty winner key cannot identify a material payload.");

        uint coverage = PackUnorm8(value.Coverage);
        // A decoded cone must never be narrower than the represented geometry,
        // otherwise an exact boundary ray can recreate the overlap false miss.
        uint cone = PackConservativeUnorm8(value.NormalCone);
        uint metadata = OccupiedBit |
                        ((value.MaterialFlags & StoredFlagMask) << 16) |
                        (cone << 8) |
                        coverage;

        Vector2 oct = EncodeOctahedral(value.GeometricNormal);
        return new GPUFarFieldMaterialVoxelV2
        {
            WinnerKey = value.StablePrimitiveKey,
            CoverageConeAndFlags = metadata,
            DiffuseRgb10 = PackRgb10(value.DiffuseReflectance),
            EmissionRg16 = PackHalf2(value.EmissiveRadiance.X, value.EmissiveRadiance.Y),
            EmissionBAndOcclusion16 = PackHalf2(
                value.EmissiveRadiance.Z,
                value.MaterialOcclusion),
            GeometricNormalOct16 = PackSnorm2x16(oct),
            MaterialRevision = value.MaterialRevision,
            TransportProfileRevision = value.TransportProfileRevision
        };
    }

    public static Candidate Unpack(
        in GPUFarFieldMaterialVoxelV2 payload,
        uint payloadVersion = PayloadVersion)
    {
        if (payload.WinnerKey == EmptyWinnerKey ||
            (payload.CoverageConeAndFlags & OccupiedBit) == 0u)
        {
            throw new ArgumentException("The supplied far-field V2 payload is empty.", nameof(payload));
        }

        Vector2 oct = UnpackSnorm2x16(payload.GeometricNormalOct16);
        return new Candidate(
            payload.WinnerKey,
            (payload.CoverageConeAndFlags & 0xffu) / 255f,
            UnpackRgb10(payload.DiffuseRgb10),
            new Vector3(
                UnpackHalfLow(payload.EmissionRg16),
                UnpackHalfHigh(payload.EmissionRg16),
                UnpackHalfLow(payload.EmissionBAndOcclusion16)),
            DecodeOctahedral(oct),
            ((payload.CoverageConeAndFlags >> 8) & 0xffu) / 255f,
            (payload.CoverageConeAndFlags >> 16) & StoredFlagMask,
            payload.MaterialRevision,
            payload.TransportProfileRevision,
            MaterialOcclusion: payloadVersion >= MaterialOcclusionPayloadVersion
                ? Math.Clamp(
                    UnpackHalfHigh(payload.EmissionBAndOcclusion16),
                    0f,
                    1f)
                : 1f);
    }

    /// <summary>glTF MASK equality is inclusive by contract.</summary>
    public static bool SurvivesAlpha(float alpha, MaterialAlphaMode alphaMode, float alphaCutoff)
    {
        return MaterialAlphaCoverageContract.OccupiesOpaqueTransport(
            Math.Clamp(alpha, 0f, 1f),
            alphaMode,
            alphaCutoff);
    }

    public static bool SurvivesSidedness(bool doubleSided, bool frontFacing) =>
        doubleSided || frontFacing;

    /// <summary>
    /// Mirrors payload-version-3 trace-facing resolution. The selected normal is
    /// retained for an ordinary front-face hit and flipped only when a
    /// conservative overlapping-normal cone is what made the hit valid.
    /// </summary>
    public static bool TryResolveFacing(
        in Candidate candidate,
        Vector3 rayDirection,
        out Vector3 resolvedGeometricNormal)
    {
        Candidate value = Validate(candidate);
        EnsureFinite(rayDirection, nameof(rayDirection));
        float directionLengthSquared = rayDirection.LengthSquared();
        if (directionLengthSquared <= 1e-12f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rayDirection),
                "A far-field material facing query requires a non-zero ray direction.");
        }

        Vector3 direction = rayDirection / MathF.Sqrt(directionLengthSquared);
        resolvedGeometricNormal = value.GeometricNormal;
        float facing = Vector3.Dot(resolvedGeometricNormal, direction);
        bool doubleSided =
            (value.MaterialFlags & (uint)GiMaterialTransportFlags.DoubleSided) != 0u;
        if (doubleSided)
        {
            if (facing > 0f)
                resolvedGeometricNormal = -resolvedGeometricNormal;
            return true;
        }

        float rayAngleNormalized =
            MathF.Acos(Math.Clamp(facing, -1f, 1f)) / MathF.PI;
        if (rayAngleNormalized + value.NormalCone <= 0.5f)
            return false;

        if (facing >= 0f)
            resolvedGeometricNormal = -resolvedGeometricNormal;
        return true;
    }

    private static float ResolveConservativeNormalCone(
        in Candidate winner,
        ReadOnlySpan<Candidate> candidates)
    {
        Candidate selected = Validate(winner);
        float resolvedCone = selected.NormalCone;
        if ((selected.MaterialFlags & (uint)GiMaterialTransportFlags.DoubleSided) != 0u)
            return resolvedCone;

        for (int i = 0; i < candidates.Length; i++)
        {
            Candidate candidate = Validate(candidates[i]);
            if (candidate.Coverage <= 0f)
                continue;

            if ((candidate.MaterialFlags & (uint)GiMaterialTransportFlags.DoubleSided) != 0u)
                return 1f;

            float normalDot = Math.Clamp(
                Vector3.Dot(selected.GeometricNormal, candidate.GeometricNormal),
                -1f,
                1f);
            float axisDeviation = MathF.Acos(normalDot) / MathF.PI;
            resolvedCone = Math.Max(
                resolvedCone,
                Math.Min(axisDeviation + candidate.NormalCone, 1f));
        }

        return resolvedCone;
    }

    private static Candidate Validate(in Candidate candidate)
    {
        EnsureFinite(candidate.Coverage, nameof(candidate.Coverage));
        EnsureFinite(candidate.NormalCone, nameof(candidate.NormalCone));
        EnsureFinite(candidate.DiffuseReflectance, nameof(candidate.DiffuseReflectance));
        EnsureFinite(candidate.EmissiveRadiance, nameof(candidate.EmissiveRadiance));
        EnsureFinite(candidate.GeometricNormal, nameof(candidate.GeometricNormal));
        EnsureFinite(candidate.NormalizedSurfaceDistance, nameof(candidate.NormalizedSurfaceDistance));
        EnsureFinite(candidate.NormalFacing, nameof(candidate.NormalFacing));
        EnsureFinite(candidate.MaterialOcclusion, nameof(candidate.MaterialOcclusion));

        Vector3 normal = SafeNormal(candidate.GeometricNormal);
        return candidate with
        {
            Coverage = Math.Clamp(candidate.Coverage, 0f, 1f),
            DiffuseReflectance = Vector3.Clamp(candidate.DiffuseReflectance, Vector3.Zero, Vector3.One),
            EmissiveRadiance = Vector3.Clamp(
                candidate.EmissiveRadiance,
                Vector3.Zero,
                new Vector3(MaximumFiniteHalf)),
            GeometricNormal = normal,
            NormalCone = Math.Clamp(candidate.NormalCone, 0f, 1f),
            NormalizedSurfaceDistance = Math.Clamp(candidate.NormalizedSurfaceDistance, 0f, 1f),
            NormalFacing = Math.Clamp(candidate.NormalFacing, 0f, 1f),
            MaterialOcclusion = Math.Clamp(candidate.MaterialOcclusion, 0f, 1f)
        };
    }

    private static uint PackRgb10(Vector3 value)
    {
        uint r = (uint)MathF.Round(Math.Clamp(value.X, 0f, 1f) * 1023f);
        uint g = (uint)MathF.Round(Math.Clamp(value.Y, 0f, 1f) * 1023f);
        uint b = (uint)MathF.Round(Math.Clamp(value.Z, 0f, 1f) * 1023f);
        return r | (g << 10) | (b << 20);
    }

    private static Vector3 UnpackRgb10(uint value) => new(
        (value & 0x3ffu) / 1023f,
        ((value >> 10) & 0x3ffu) / 1023f,
        ((value >> 20) & 0x3ffu) / 1023f);

    private static uint PackUnorm8(float value) =>
        (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private static uint PackConservativeUnorm8(float value) =>
        (uint)MathF.Ceiling(Math.Clamp(value, 0f, 1f) * 255f);

    // GLSL round permits implementation-dependent half-way handling. Selection
    // keys instead use an explicit half-up rule on both CPU and GPU so exact
    // quantization boundaries cannot produce different winners.
    private static uint PackSelectionUnorm8(float value) =>
        (uint)MathF.Floor(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);

    private static uint PackSelectionUnorm16(float value) =>
        (uint)MathF.Floor(Math.Clamp(value, 0f, 1f) * 65_535f + 0.5f);

    private static uint PackHalf2(float low, float high)
    {
        ushort lowBits = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(low, 0f, MaximumFiniteHalf));
        ushort highBits = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(high, 0f, MaximumFiniteHalf));
        return lowBits | ((uint)highBits << 16);
    }

    private static float UnpackHalfLow(uint value) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(value & 0xffffu));

    private static float UnpackHalfHigh(uint value) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(value >> 16));

    private static Vector2 EncodeOctahedral(Vector3 normal)
    {
        normal = SafeNormal(normal);
        float inverseL1 = 1f / (Math.Abs(normal.X) + Math.Abs(normal.Y) + Math.Abs(normal.Z));
        Vector2 projected = new(normal.X * inverseL1, normal.Y * inverseL1);
        if (normal.Z >= 0f)
            return projected;

        return new Vector2(
            (1f - Math.Abs(projected.Y)) * SignNotZero(projected.X),
            (1f - Math.Abs(projected.X)) * SignNotZero(projected.Y));
    }

    private static Vector3 DecodeOctahedral(Vector2 encoded)
    {
        Vector3 normal = new(
            encoded.X,
            encoded.Y,
            1f - Math.Abs(encoded.X) - Math.Abs(encoded.Y));
        if (normal.Z < 0f)
        {
            float x = (1f - Math.Abs(normal.Y)) * SignNotZero(normal.X);
            float y = (1f - Math.Abs(normal.X)) * SignNotZero(normal.Y);
            normal.X = x;
            normal.Y = y;
        }

        return SafeNormal(normal);
    }

    private static uint PackSnorm2x16(Vector2 value)
    {
        short x = (short)MathF.Round(Math.Clamp(value.X, -1f, 1f) * 32767f);
        short y = (short)MathF.Round(Math.Clamp(value.Y, -1f, 1f) * 32767f);
        return unchecked((ushort)x) | ((uint)unchecked((ushort)y) << 16);
    }

    private static Vector2 UnpackSnorm2x16(uint value) => new(
        Math.Max(unchecked((short)(value & 0xffffu)) / 32767f, -1f),
        Math.Max(unchecked((short)(value >> 16)) / 32767f, -1f));

    private static Vector3 SafeNormal(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : Vector3.UnitY;
    }

    private static float SignNotZero(float value) => value >= 0f ? 1f : -1f;

    private static void EnsureFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Far-field material payload values must be finite.");
    }

    private static void EnsureFinite(Vector3 value, string name)
    {
        EnsureFinite(value.X, name);
        EnsureFinite(value.Y, name);
        EnsureFinite(value.Z, name);
    }
}

public readonly record struct FarFieldMaterialV2Counters(
    uint ConflictCount,
    uint StalePublicationRejectCount)
{
    public static FarFieldMaterialV2Counters Empty { get; } = default;
}
