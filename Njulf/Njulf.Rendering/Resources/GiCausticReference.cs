using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Reference GPU record for the first-diffuse endpoint of one tagged caustic
/// photon path.  It is intentionally FP32/auditable and is not the compact
/// production candidate representation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUCausticPhotonReferenceV1
{
    public Vector3 WorldPosition;
    public float SupportRadius;
    public Vector3 IncidentFlux;
    public float PathWeightDebug;
    public uint PackedIncidentDirection;
    public uint PackedReceiverNormal;
    public uint PathTagAndDepth;
    public uint StablePhotonId;
    /// <summary>AxisU, axisV, cosine, sine in receiver tangent space.</summary>
    public Vector4 TangentPlaneFootprint;
    public uint SourceId;
    public uint HeroInstanceId;
    public uint TransportRevision;
    public uint CacheGeneration;

    public readonly GPUCausticPhotonReferenceV1 WithFluxScale(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(scale));

        GPUCausticPhotonReferenceV1 result = this;
        result.IncidentFlux *= scale;
        return result;
    }

    public static void ValidateAbi()
    {
        if (Unsafe.SizeOf<GPUCausticPhotonReferenceV1>() != 80 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(WorldPosition)).ToInt32() != 0 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(SupportRadius)).ToInt32() != 12 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(IncidentFlux)).ToInt32() != 16 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(PathWeightDebug)).ToInt32() != 28 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(PackedIncidentDirection)).ToInt32() != 32 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(TangentPlaneFootprint)).ToInt32() != 48 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(SourceId)).ToInt32() != 64 ||
            Marshal.OffsetOf<GPUCausticPhotonReferenceV1>(nameof(CacheGeneration)).ToInt32() != 76)
        {
            throw new InvalidOperationException(
                "GPUCausticPhotonReferenceV1 ABI must be exactly 80 bytes with the documented offsets.");
        }
    }
}

/// <summary>Full signed world-cell identity. Hash collisions are never identity.</summary>
public readonly record struct GiCausticCellKey(int X, int Y, int Z, int Cascade)
    : IComparable<GiCausticCellKey>
{
    public int CompareTo(GiCausticCellKey other)
    {
        int result = Cascade.CompareTo(other.Cascade);
        if (result != 0) return result;
        result = X.CompareTo(other.X);
        if (result != 0) return result;
        result = Y.CompareTo(other.Y);
        return result != 0 ? result : Z.CompareTo(other.Z);
    }

    /// <summary>
    /// Stable mixing used only to choose an open-addressing start slot. Callers
    /// must compare the entire key after probing.
    /// </summary>
    public ulong StableHash64()
    {
        ulong state = 0x9E3779B97F4A7C15UL;
        state = Mix(state ^ unchecked((uint)X));
        state = Mix(state ^ unchecked((uint)Y));
        state = Mix(state ^ unchecked((uint)Z));
        return Mix(state ^ unchecked((uint)Cascade));
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

public readonly record struct GiCausticPhotonCandidate(
    GiCausticCellKey Cell,
    ulong StablePhotonHash,
    GPUCausticPhotonReferenceV1 Photon);

public readonly record struct GiCausticCacheRange(int Offset, int Count);

public readonly record struct GiCausticCellTableEntry(
    bool Occupied,
    GiCausticCellKey Key,
    GiCausticCacheRange Range);

public sealed class GiCausticCellTable
{
    private readonly GiCausticCellTableEntry[] _entries;

    internal GiCausticCellTable(GiCausticCellTableEntry[] entries, int insertedCount, int maximumProbeCount)
    {
        _entries = entries;
        InsertedCount = insertedCount;
        MaximumProbeCount = maximumProbeCount;
    }

    public int Capacity => _entries.Length;
    public int InsertedCount { get; }
    public int MaximumProbeCount { get; }
    public float LoadFactor => Capacity == 0 ? 0.0f : (float)InsertedCount / Capacity;
    public ReadOnlySpan<GiCausticCellTableEntry> Entries => _entries;

    public bool TryGetRange(GiCausticCellKey key, out GiCausticCacheRange range)
    {
        if (_entries.Length == 0)
        {
            range = default;
            return false;
        }

        int mask = _entries.Length - 1;
        int start = unchecked((int)key.StableHash64()) & mask;
        for (int probe = 0; probe < _entries.Length; probe++)
        {
            ref readonly GiCausticCellTableEntry entry = ref _entries[(start + probe) & mask];
            if (!entry.Occupied)
            {
                range = default;
                return false;
            }
            if (entry.Key.Equals(key))
            {
                range = entry.Range;
                return true;
            }
        }

        range = default;
        return false;
    }
}

public readonly record struct GiCausticCacheBuildConfiguration(
    int MaximumPhotonsPerCell,
    int MaximumOccupiedCells,
    float TargetLoadFactor,
    uint CacheGeneration)
{
    public static GiCausticCacheBuildConfiguration Default { get; } = new(
        MaximumPhotonsPerCell: 16,
        MaximumOccupiedCells: 4_096,
        TargetLoadFactor: 0.5f,
        CacheGeneration: 1);
}

public readonly record struct GiCausticCacheBuildDiagnostics(
    int InputPhotonCount,
    int OccupiedCellCount,
    int RetainedPhotonCount,
    int DroppedPhotonCount,
    int MaximumRunLength,
    int MaximumHashProbeCount,
    bool Valid,
    string FailureReason)
{
    public static GiCausticCacheBuildDiagnostics Empty(string reason) => new(
        0, 0, 0, 0, 0, 0, false, reason);
}

public sealed class GiCausticCacheBuildResult
{
    internal GiCausticCacheBuildResult(
        IReadOnlyList<GiCausticPhotonCandidate> photons,
        GiCausticCellTable? table,
        GiCausticCacheBuildDiagnostics diagnostics)
    {
        Photons = photons;
        Table = table;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<GiCausticPhotonCandidate> Photons { get; }
    public GiCausticCellTable? Table { get; }
    public GiCausticCacheBuildDiagnostics Diagnostics { get; }
    public bool IsValid => Diagnostics.Valid;

    public static GiCausticCacheBuildResult Invalid(string reason) => new(
        Array.Empty<GiCausticPhotonCandidate>(),
        null,
        GiCausticCacheBuildDiagnostics.Empty(reason));
}

/// <summary>
/// CPU reference for the cache's sort, deterministic bottom-K retention, and
/// full-key open-addressing table. It intentionally fails the complete build
/// rather than publishing a partial/spatially biased cache.
/// </summary>
public static class GiCausticPhotonCacheReference
{
    public static GiCausticCacheBuildResult Build(
        ReadOnlySpan<GiCausticPhotonCandidate> input,
        in GiCausticCacheBuildConfiguration configuration)
    {
        GPUCausticPhotonReferenceV1.ValidateAbi();
        if (configuration.MaximumPhotonsPerCell <= 0 ||
            configuration.MaximumOccupiedCells <= 0 ||
            !float.IsFinite(configuration.TargetLoadFactor) ||
            configuration.TargetLoadFactor <= 0.0f ||
            configuration.TargetLoadFactor > 0.5f)
        {
            return GiCausticCacheBuildResult.Invalid("invalid-cache-configuration");
        }

        if (input.Length == 0)
        {
            return new GiCausticCacheBuildResult(
                Array.Empty<GiCausticPhotonCandidate>(),
                new GiCausticCellTable(Array.Empty<GiCausticCellTableEntry>(), 0, 0),
                new GiCausticCacheBuildDiagnostics(0, 0, 0, 0, 0, 0, true, "empty"));
        }

        var sorted = new GiCausticPhotonCandidate[input.Length];
        input.CopyTo(sorted);
        Array.Sort(sorted, CompareCandidates);

        var retained = new List<GiCausticPhotonCandidate>(sorted.Length);
        var cells = new List<(GiCausticCellKey Key, GiCausticCacheRange Range)>();
        int maxRunLength = 0;
        int cursor = 0;
        while (cursor < sorted.Length)
        {
            int end = cursor + 1;
            GiCausticCellKey cell = sorted[cursor].Cell;
            while (end < sorted.Length && sorted[end].Cell.Equals(cell))
                end++;

            if (cells.Count >= configuration.MaximumOccupiedCells)
                return GiCausticCacheBuildResult.Invalid("occupied-cell-capacity-overflow");

            int runLength = end - cursor;
            maxRunLength = Math.Max(maxRunLength, runLength);
            int retainedCount = Math.Min(runLength, configuration.MaximumPhotonsPerCell);
            float inverseInclusionProbability = runLength > retainedCount
                ? (float)runLength / retainedCount
                : 1.0f;
            int offset = retained.Count;
            for (int i = 0; i < retainedCount; i++)
            {
                GiCausticPhotonCandidate candidate = sorted[cursor + i];
                GPUCausticPhotonReferenceV1 photon = candidate.Photon.WithFluxScale(
                    inverseInclusionProbability);
                photon.CacheGeneration = configuration.CacheGeneration;
                retained.Add(candidate with { Photon = photon });
            }
            cells.Add((cell, new GiCausticCacheRange(offset, retainedCount)));
            cursor = end;
        }

        if (!TryCreateTable(cells, configuration.TargetLoadFactor, out GiCausticCellTable? table,
                out int maximumProbeCount))
        {
            return GiCausticCacheBuildResult.Invalid("cell-table-insertion-failed");
        }

        return new GiCausticCacheBuildResult(
            retained,
            table,
            new GiCausticCacheBuildDiagnostics(
                input.Length,
                cells.Count,
                retained.Count,
                input.Length - retained.Count,
                maxRunLength,
                maximumProbeCount,
                true,
                "valid"));
    }

    private static int CompareCandidates(
        GiCausticPhotonCandidate left,
        GiCausticPhotonCandidate right)
    {
        int result = left.Cell.CompareTo(right.Cell);
        if (result != 0)
            return result;
        result = left.StablePhotonHash.CompareTo(right.StablePhotonHash);
        if (result != 0)
            return result;
        return left.Photon.StablePhotonId.CompareTo(right.Photon.StablePhotonId);
    }

    private static bool TryCreateTable(
        List<(GiCausticCellKey Key, GiCausticCacheRange Range)> cells,
        float targetLoadFactor,
        out GiCausticCellTable? table,
        out int maximumProbeCount)
    {
        table = null;
        maximumProbeCount = 0;
        int minimumCapacity;
        try
        {
            minimumCapacity = checked((int)Math.Ceiling(cells.Count / (double)targetLoadFactor));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!TryNextPowerOfTwo(Math.Max(2, minimumCapacity), out int capacity))
            return false;
        var entries = new GiCausticCellTableEntry[capacity];
        int mask = capacity - 1;
        foreach ((GiCausticCellKey key, GiCausticCacheRange range) in cells)
        {
            int start = unchecked((int)key.StableHash64()) & mask;
            bool inserted = false;
            for (int probe = 0; probe < capacity; probe++)
            {
                int index = (start + probe) & mask;
                if (!entries[index].Occupied)
                {
                    entries[index] = new GiCausticCellTableEntry(true, key, range);
                    maximumProbeCount = Math.Max(maximumProbeCount, probe + 1);
                    inserted = true;
                    break;
                }
            }
            if (!inserted)
                return false;
        }

        table = new GiCausticCellTable(entries, cells.Count, maximumProbeCount);
        return true;
    }

    internal static bool TryNextPowerOfTwo(int value, out int result)
    {
        if (value <= 0 || value > (1 << 30))
        {
            result = 0;
            return false;
        }

        uint bits = (uint)(value - 1);
        bits |= bits >> 1;
        bits |= bits >> 2;
        bits |= bits >> 4;
        bits |= bits >> 8;
        bits |= bits >> 16;
        result = checked((int)(bits + 1));
        return result > 0;
    }
}

public readonly record struct GiCausticRgbd(double R, double G, double B)
{
    public static GiCausticRgbd operator *(GiCausticRgbd value, double scale) =>
        new(value.R * scale, value.G * scale, value.B * scale);

    public static GiCausticRgbd operator /(GiCausticRgbd value, double scale) =>
        new(value.R / scale, value.G / scale, value.B / scale);

    public bool IsFinite =>
        double.IsFinite(R) && double.IsFinite(G) && double.IsFinite(B);

    public Vector3 ToVector3() => new((float)R, (float)G, (float)B);
}

/// <summary>All probabilities in the photon task's complete joint proposal.</summary>
public readonly record struct GiCausticPhotonTaskPdf(
    double Emitter,
    double CasterGivenEmitter,
    double Position,
    double Direction)
{
    public double JointPdf => Emitter * CasterGivenEmitter * Position * Direction;

    public void Validate()
    {
        if (!double.IsFinite(Emitter) || !double.IsFinite(CasterGivenEmitter) ||
            !double.IsFinite(Position) || !double.IsFinite(Direction) ||
            Emitter <= 0.0 || CasterGivenEmitter <= 0.0 || Position <= 0.0 || Direction <= 0.0 ||
            !double.IsFinite(JointPdf) || JointPdf <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(Emitter),
                "Photon task PDFs must be finite, positive, and have a finite positive joint product.");
        }
    }
}

public static class GiCausticPathReference
{
    public static GiCausticRgbd InitialFlux(
        GiCausticRgbd emittedContribution,
        int photonTaskCount,
        in GiCausticPhotonTaskPdf proposal)
    {
        if (!emittedContribution.IsFinite || emittedContribution.R < 0.0 ||
            emittedContribution.G < 0.0 || emittedContribution.B < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(emittedContribution));
        }
        if (photonTaskCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(photonTaskCount));
        proposal.Validate();

        double denominator = photonTaskCount * proposal.JointPdf;
        if (!double.IsFinite(denominator) || denominator <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(proposal));
        return emittedContribution / denominator;
    }

    public static GiCausticRgbd AdvanceNonDelta(
        GiCausticRgbd throughput,
        GiCausticRgbd scatteringValue,
        double absoluteCosTheta,
        double eventPdf)
    {
        ValidateSpectrum(throughput, nameof(throughput));
        ValidateSpectrum(scatteringValue, nameof(scatteringValue));
        if (!double.IsFinite(absoluteCosTheta) || absoluteCosTheta < 0.0 ||
            !double.IsFinite(eventPdf) || eventPdf <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventPdf));
        }

        double scale = absoluteCosTheta / eventPdf;
        return new GiCausticRgbd(
            throughput.R * scatteringValue.R * scale,
            throughput.G * scatteringValue.G * scale,
            throughput.B * scatteringValue.B * scale);
    }

    public static GiCausticRgbd AdvanceFresnelBranch(
        GiCausticRgbd throughput,
        double branchWeight,
        double selectedBranchProbability)
    {
        ValidateSpectrum(throughput, nameof(throughput));
        if (!double.IsFinite(branchWeight) || branchWeight < 0.0 ||
            !double.IsFinite(selectedBranchProbability) || selectedBranchProbability <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedBranchProbability));
        }

        return throughput * (branchWeight / selectedBranchProbability);
    }

    public static GiCausticRgbd ApplyBeerLambert(
        GiCausticRgbd throughput,
        GiCausticRgbd absorptionCoefficient,
        double distance)
    {
        ValidateSpectrum(throughput, nameof(throughput));
        ValidateSpectrum(absorptionCoefficient, nameof(absorptionCoefficient));
        if (!double.IsFinite(distance) || distance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(distance));

        return new GiCausticRgbd(
            throughput.R * Math.Exp(-absorptionCoefficient.R * distance),
            throughput.G * Math.Exp(-absorptionCoefficient.G * distance),
            throughput.B * Math.Exp(-absorptionCoefficient.B * distance));
    }

    public static double DielectricReflectance(
        double cosineIncident,
        double etaIncident,
        double etaTransmitted)
    {
        if (!double.IsFinite(cosineIncident) || !double.IsFinite(etaIncident) ||
            !double.IsFinite(etaTransmitted) || etaIncident <= 0.0 || etaTransmitted <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cosineIncident));
        }

        double cosI = Math.Clamp(Math.Abs(cosineIncident), 0.0, 1.0);
        double eta = etaIncident / etaTransmitted;
        double sinTransmittedSquared = eta * eta * Math.Max(0.0, 1.0 - cosI * cosI);
        if (sinTransmittedSquared >= 1.0)
            return 1.0;
        double cosT = Math.Sqrt(Math.Max(0.0, 1.0 - sinTransmittedSquared));
        double rs = ((etaTransmitted * cosI) - (etaIncident * cosT)) /
                    ((etaTransmitted * cosI) + (etaIncident * cosT));
        double rp = ((etaIncident * cosI) - (etaTransmitted * cosT)) /
                    ((etaIncident * cosI) + (etaTransmitted * cosT));
        return Math.Clamp(0.5 * (rs * rs + rp * rp), 0.0, 1.0);
    }

    private static void ValidateSpectrum(GiCausticRgbd value, string parameterName)
    {
        if (!value.IsFinite || value.R < 0.0 || value.G < 0.0 || value.B < 0.0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// A normalized anisotropic Epanechnikov footprint. The integral over its
/// tangent-plane ellipse is one, so support-radius clamps never silently clamp
/// photon flux.
/// </summary>
public readonly record struct GiCausticPhotonFootprint(
    float AxisU,
    float AxisV,
    float Cosine,
    float Sine)
{
    public GiCausticPhotonFootprint Normalize()
    {
        if (!float.IsFinite(AxisU) || !float.IsFinite(AxisV) ||
            !float.IsFinite(Cosine) || !float.IsFinite(Sine) ||
            AxisU <= 0.0f || AxisV <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(AxisU));
        }
        float length = MathF.Sqrt(Cosine * Cosine + Sine * Sine);
        if (length <= 1.0e-12f || !float.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(Cosine));
        return new GiCausticPhotonFootprint(AxisU, AxisV, Cosine / length, Sine / length);
    }

    public GiCausticPhotonFootprint ClampSupport(float minimumRadius, float maximumRadius)
    {
        if (!float.IsFinite(minimumRadius) || !float.IsFinite(maximumRadius) ||
            minimumRadius <= 0.0f || maximumRadius < minimumRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRadius));
        }

        GiCausticPhotonFootprint normalized = Normalize();
        return normalized with
        {
            AxisU = Math.Clamp(normalized.AxisU, minimumRadius, maximumRadius),
            AxisV = Math.Clamp(normalized.AxisV, minimumRadius, maximumRadius)
        };
    }
}

public static class GiCausticPhotonKernel
{
    public static float EvaluateNormalized(
        in GiCausticPhotonFootprint footprint,
        float tangentOffsetU,
        float tangentOffsetV)
    {
        if (!float.IsFinite(tangentOffsetU) || !float.IsFinite(tangentOffsetV))
            return 0.0f;
        GiCausticPhotonFootprint normalized = footprint.Normalize();
        float rotatedU = normalized.Cosine * tangentOffsetU + normalized.Sine * tangentOffsetV;
        float rotatedV = -normalized.Sine * tangentOffsetU + normalized.Cosine * tangentOffsetV;
        float radiusSquared = (rotatedU * rotatedU) / (normalized.AxisU * normalized.AxisU) +
                              (rotatedV * rotatedV) / (normalized.AxisV * normalized.AxisV);
        if (!float.IsFinite(radiusSquared) || radiusSquared >= 1.0f)
            return 0.0f;

        return 2.0f * (1.0f - radiusSquared) /
               (MathF.PI * normalized.AxisU * normalized.AxisV);
    }

    public static Vector3 EvaluateFluxDensity(
        Vector3 incidentFlux,
        in GiCausticPhotonFootprint footprint,
        float tangentOffsetU,
        float tangentOffsetV)
    {
        if (!float.IsFinite(incidentFlux.X) || !float.IsFinite(incidentFlux.Y) ||
            !float.IsFinite(incidentFlux.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(incidentFlux));
        }
        return Vector3.Max(incidentFlux, Vector3.Zero) *
               EvaluateNormalized(footprint, tangentOffsetU, tangentOffsetV);
    }
}
