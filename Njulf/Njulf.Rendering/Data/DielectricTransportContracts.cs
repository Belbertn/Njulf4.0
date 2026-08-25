using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

public enum DielectricTransportFallbackReason : byte
{
    None = 0,
    InvalidInput,
    StackOverflow,
    StackUnderflow,
    BoundaryMismatch,
    InterfaceBudgetExceeded,
    CandidateBudgetExceeded,
    UnsupportedTopology,
    PartialMediaStack
}

public enum DielectricTransportBranch : byte
{
    Reflection = 0,
    Transmission = 1,
    TotalInternalReflection = 2
}

/// <summary>Stable reason why requested visible thick transport was demoted.</summary>
public enum ThickTransmissionFallbackReason : uint
{
    None = 0,
    Disabled = 1,
    RayQueryUnsupported = 2,
    AccelerationStructureUnsupported = 3,
    RaySceneIncomplete = 4,
    RayPipelineUnavailable = 5,
    TaskBudgetExceeded = 6,
    MemoryBudgetExceeded = 7,
    InvalidConfiguration = 8
}

public readonly record struct ThickTransmissionModeCapabilities(
    bool RayQuerySupported,
    bool AccelerationStructureSupported,
    bool RaySceneReady,
    bool RayPipelineAvailable);

public readonly record struct ThickTransmissionModeResolution(
    ThickTransmissionMode Requested,
    ThickTransmissionMode Effective,
    ThickTransmissionFallbackReason Reason,
    string Detail)
{
    public bool UsesRayQueries => Effective == ThickTransmissionMode.RayQuery;
}

/// <summary>
/// Resolves one deterministic visible-transport mode. Every unavailable or
/// incomplete ray path falls back to the bounded analytic approximation.
/// </summary>
public static class ThickTransmissionModeResolver
{
    // The inline implementation does not allocate a task queue today, but the
    // admission contract reserves the same bounded working-set envelope a
    // queued implementation requires (origin/direction, stack summary,
    // throughput, result, and alignment). This makes memory demotion stable
    // across the two execution forms.
    public const ulong EstimatedRayTaskBytes = 64UL;

    public static ThickTransmissionModeResolution Resolve(
        TransparencySettings settings,
        in ThickTransmissionModeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThickTransmissionMode requested = settings.Enabled
            ? settings.ThickTransmissionMode
            : ThickTransmissionMode.Off;
        if (requested == ThickTransmissionMode.Off)
        {
            return new ThickTransmissionModeResolution(
                requested,
                ThickTransmissionMode.Off,
                ThickTransmissionFallbackReason.Disabled,
                "Thick transmission is disabled.");
        }
        if (requested == ThickTransmissionMode.Approximation)
        {
            return new ThickTransmissionModeResolution(
                requested,
                requested,
                ThickTransmissionFallbackReason.None,
                string.Empty);
        }
        if (settings.ThickTransmissionRayTaskBudget <= 0)
        {
            return Fallback(
                ThickTransmissionFallbackReason.TaskBudgetExceeded,
                "The thick-transmission ray-task budget is zero.");
        }
        ulong requiredWorkingSet = checked(
            (ulong)settings.ThickTransmissionRayTaskBudget *
            EstimatedRayTaskBytes);
        if (requiredWorkingSet > settings.ThickTransmissionMemoryBudgetBytes)
        {
            return Fallback(
                ThickTransmissionFallbackReason.MemoryBudgetExceeded,
                $"The configured ray-task envelope requires " +
                $"{requiredWorkingSet} bytes, exceeding the " +
                $"{settings.ThickTransmissionMemoryBudgetBytes}-byte budget.");
        }
        if (!capabilities.RayQuerySupported)
            return Fallback(ThickTransmissionFallbackReason.RayQueryUnsupported,
                "Ray queries are unsupported.");
        if (!capabilities.AccelerationStructureSupported)
            return Fallback(
                ThickTransmissionFallbackReason.AccelerationStructureUnsupported,
                "Acceleration structures are unsupported.");
        if (!capabilities.RaySceneReady)
            return Fallback(ThickTransmissionFallbackReason.RaySceneIncomplete,
                "The shared ray scene does not contain complete optical-boundary coverage.");
        if (!capabilities.RayPipelineAvailable)
            return Fallback(ThickTransmissionFallbackReason.RayPipelineUnavailable,
                "The transparent ray-query pipeline is unavailable.");

        return new ThickTransmissionModeResolution(
            requested,
            requested,
            ThickTransmissionFallbackReason.None,
            string.Empty);

        ThickTransmissionModeResolution Fallback(
            ThickTransmissionFallbackReason reason,
            string detail) => new(
                requested,
                ThickTransmissionMode.Approximation,
                reason,
                detail);
    }
}

public readonly record struct DielectricMedium(
    uint BoundaryIdentity,
    uint MaterialRevision,
    float Ior,
    Vector3 AbsorptionCoefficient,
    OpticalBoundaryKind BoundaryKind,
    float EntryPathDistance)
{
    public static DielectricMedium Air { get; } = new(
        0u,
        0u,
        1f,
        Vector3.Zero,
        OpticalBoundaryKind.ClosedVolume,
        0f);

    public bool IsValid =>
        BoundaryIdentity != 0u && MaterialRevision != 0u &&
        float.IsFinite(Ior) && Ior >= 1f && Ior <= 4f &&
        DielectricTransportMath.IsFiniteNonNegative(AbsorptionCoefficient) &&
        Enum.IsDefined(BoundaryKind) &&
        float.IsFinite(EntryPathDistance) && EntryPathDistance >= 0f;
}

public readonly record struct DielectricBoundary(
    uint BoundaryIdentity,
    uint MaterialRevision,
    float Ior,
    Vector3 AbsorptionCoefficient,
    OpticalBoundaryKind BoundaryKind)
{
    public bool IsValid => new DielectricMedium(
        BoundaryIdentity,
        MaterialRevision,
        Ior,
        AbsorptionCoefficient,
        BoundaryKind,
        0f).IsValid;
}

public readonly record struct DielectricInterface(
    float IncidentIor,
    float TransmittedIor,
    bool Entering,
    int DepthBefore,
    int DepthAfterTransmission);

/// <summary>
/// Deterministic bounded media state used as the CPU oracle for visible,
/// DDGI, and caustic transport. A reflected or TIR branch never mutates the
/// stack; only a successfully committed transmission does.
/// </summary>
public sealed class BoundedDielectricMediaStack
{
    public const int MaximumDepth = 4;
    public const int MaximumInterfaces = 8;
    public const int MaximumCandidatesPerInterface = 64;

    private readonly DielectricMedium[] _media =
        new DielectricMedium[MaximumDepth];
    private int _interfaceCount;

    public int Count { get; private set; }
    public int InterfaceCount => _interfaceCount;
    public float CurrentIor => Count == 0 ? 1f : _media[Count - 1].Ior;
    public Vector3 CurrentAbsorptionCoefficient =>
        Count == 0 ? Vector3.Zero : _media[Count - 1].AbsorptionCoefficient;
    public DielectricTransportFallbackReason FallbackReason { get; private set; }
    public bool IsComplete => Count == 0 && FallbackReason ==
        DielectricTransportFallbackReason.None;

    public ReadOnlySpan<DielectricMedium> Media =>
        new(_media, 0, Count);

    public void Reset()
    {
        Array.Clear(_media);
        Count = 0;
        _interfaceCount = 0;
        FallbackReason = DielectricTransportFallbackReason.None;
    }

    public bool TryPrepareInterface(
        in DielectricBoundary boundary,
        bool frontFacing,
        out DielectricInterface dielectricInterface)
    {
        dielectricInterface = default;
        if (FallbackReason != DielectricTransportFallbackReason.None)
            return false;
        if (!boundary.IsValid)
            return Fail(DielectricTransportFallbackReason.InvalidInput);
        if (_interfaceCount >= MaximumInterfaces)
            return Fail(DielectricTransportFallbackReason.InterfaceBudgetExceeded);

        if (frontFacing)
        {
            if (Count >= MaximumDepth)
                return Fail(DielectricTransportFallbackReason.StackOverflow);
            if (Count > 0 &&
                _media[Count - 1].BoundaryIdentity == boundary.BoundaryIdentity)
            {
                return Fail(DielectricTransportFallbackReason.BoundaryMismatch);
            }

            dielectricInterface = new DielectricInterface(
                CurrentIor,
                boundary.Ior,
                true,
                Count,
                Count + 1);
            return true;
        }

        if (Count == 0)
            return Fail(DielectricTransportFallbackReason.StackUnderflow);
        DielectricMedium top = _media[Count - 1];
        if (top.BoundaryIdentity != boundary.BoundaryIdentity ||
            top.MaterialRevision != boundary.MaterialRevision ||
            top.BoundaryKind != boundary.BoundaryKind)
        {
            return Fail(DielectricTransportFallbackReason.BoundaryMismatch);
        }

        dielectricInterface = new DielectricInterface(
            top.Ior,
            Count > 1 ? _media[Count - 2].Ior : 1f,
            false,
            Count,
            Count - 1);
        return true;
    }

    public bool CommitTransmission(
        in DielectricBoundary boundary,
        in DielectricInterface dielectricInterface,
        float pathDistance)
    {
        if (FallbackReason != DielectricTransportFallbackReason.None)
            return false;
        if (!float.IsFinite(pathDistance) || pathDistance < 0f ||
            dielectricInterface.DepthBefore != Count)
        {
            return Fail(DielectricTransportFallbackReason.InvalidInput);
        }

        if (dielectricInterface.Entering)
        {
            if (Count >= MaximumDepth)
                return Fail(DielectricTransportFallbackReason.StackOverflow);
            _media[Count++] = new DielectricMedium(
                boundary.BoundaryIdentity,
                boundary.MaterialRevision,
                boundary.Ior,
                boundary.AbsorptionCoefficient,
                boundary.BoundaryKind,
                pathDistance);
        }
        else
        {
            if (Count == 0 ||
                _media[Count - 1].BoundaryIdentity != boundary.BoundaryIdentity)
            {
                return Fail(DielectricTransportFallbackReason.BoundaryMismatch);
            }
            _media[--Count] = default;
        }

        _interfaceCount++;
        return Count == dielectricInterface.DepthAfterTransmission;
    }

    public bool CommitReflection(in DielectricInterface dielectricInterface)
    {
        if (FallbackReason != DielectricTransportFallbackReason.None)
            return false;
        if (dielectricInterface.DepthBefore != Count)
            return Fail(DielectricTransportFallbackReason.InvalidInput);

        _interfaceCount++;
        return true;
    }

    public bool RegisterCandidateCount(int candidateCount)
    {
        if (candidateCount is >= 0 and <= MaximumCandidatesPerInterface)
            return true;
        return Fail(DielectricTransportFallbackReason.CandidateBudgetExceeded);
    }

    public bool FinalizePath()
    {
        if (FallbackReason != DielectricTransportFallbackReason.None)
            return false;
        return Count == 0 || Fail(
            DielectricTransportFallbackReason.PartialMediaStack);
    }

    private bool Fail(DielectricTransportFallbackReason reason)
    {
        FallbackReason = reason;
        return false;
    }
}

public static class DielectricTransportMath
{
    public const float MinimumIor = 1f;
    public const float MaximumIor = 4f;
    public const float DeltaRoughnessThreshold = 0.02f;
    public const float FraunhoferFNanometers = 486.13f;
    public const float FraunhoferDNanometers = 587.56f;
    public const float FraunhoferCNanometers = 656.27f;

    public static Vector3 AbsorptionCoefficient(
        Vector3 attenuationColor,
        float attenuationDistance)
    {
        if (!float.IsFinite(attenuationColor.X) ||
            !float.IsFinite(attenuationColor.Y) ||
            !float.IsFinite(attenuationColor.Z) ||
            attenuationColor.X < 0f || attenuationColor.X > 1f ||
            attenuationColor.Y < 0f || attenuationColor.Y > 1f ||
            attenuationColor.Z < 0f || attenuationColor.Z > 1f ||
            (!float.IsFinite(attenuationDistance) &&
             !float.IsPositiveInfinity(attenuationDistance)) ||
            attenuationDistance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(attenuationColor));
        }

        if (float.IsPositiveInfinity(attenuationDistance) ||
            attenuationDistance <= 0f)
        {
            return Vector3.Zero;
        }

        return new Vector3(
            -MathF.Log(MathF.Max(attenuationColor.X, 1e-6f)) /
            attenuationDistance,
            -MathF.Log(MathF.Max(attenuationColor.Y, 1e-6f)) /
            attenuationDistance,
            -MathF.Log(MathF.Max(attenuationColor.Z, 1e-6f)) /
            attenuationDistance);
    }

    public static Vector3 BeerLambert(Vector3 absorption, float distance)
    {
        if (!IsFiniteNonNegative(absorption) ||
            !float.IsFinite(distance) || distance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        return new Vector3(
            MathF.Exp(-absorption.X * distance),
            MathF.Exp(-absorption.Y * distance),
            MathF.Exp(-absorption.Z * distance));
    }

    public static float ExactUnpolarizedFresnel(
        float cosineIncident,
        float incidentIor,
        float transmittedIor,
        out bool totalInternalReflection)
    {
        ValidateInterface(cosineIncident, incidentIor, transmittedIor);
        float cosI = Math.Clamp(MathF.Abs(cosineIncident), 0f, 1f);
        float eta = incidentIor / transmittedIor;
        float sinTSquared = eta * eta * MathF.Max(0f, 1f - cosI * cosI);
        totalInternalReflection = sinTSquared >= 1f;
        if (totalInternalReflection)
            return 1f;

        float cosT = MathF.Sqrt(MathF.Max(0f, 1f - sinTSquared));
        float rsDenominator = transmittedIor * cosI + incidentIor * cosT;
        float rpDenominator = incidentIor * cosI + transmittedIor * cosT;
        if (rsDenominator <= 1e-12f || rpDenominator <= 1e-12f)
        {
            totalInternalReflection = true;
            return 1f;
        }

        float rs = (transmittedIor * cosI - incidentIor * cosT) /
                   rsDenominator;
        float rp = (incidentIor * cosI - transmittedIor * cosT) /
                   rpDenominator;
        return Math.Clamp(0.5f * (rs * rs + rp * rp), 0f, 1f);
    }

    public static bool TryRefract(
        Vector3 incidentDirection,
        Vector3 orientedGeometricNormal,
        float incidentIor,
        float transmittedIor,
        out Vector3 transmittedDirection,
        out float reflectance)
    {
        Vector3 incident = SafeNormalize(incidentDirection);
        Vector3 normal = SafeNormalize(orientedGeometricNormal);
        float cosI = Math.Clamp(Vector3.Dot(-incident, normal), 0f, 1f);
        reflectance = ExactUnpolarizedFresnel(
            cosI,
            incidentIor,
            transmittedIor,
            out bool totalInternalReflection);
        if (totalInternalReflection)
        {
            transmittedDirection = Vector3.Zero;
            return false;
        }

        float eta = incidentIor / transmittedIor;
        float k = 1f - eta * eta * (1f - cosI * cosI);
        if (k <= 0f)
        {
            transmittedDirection = Vector3.Zero;
            reflectance = 1f;
            return false;
        }
        transmittedDirection = SafeNormalize(
            eta * incident + (eta * cosI - MathF.Sqrt(k)) * normal);
        return transmittedDirection.LengthSquared() > 0.999f;
    }

    /// <summary>
    /// Returns red, green, and blue IORs using the KHR_materials_dispersion
    /// real-time RGB-triplet approximation. The authored value is 20 / Vd.
    /// </summary>
    public static Vector3 RgbIors(float centralIor, float dispersion)
    {
        if (!float.IsFinite(centralIor) || centralIor < MinimumIor ||
            centralIor > MaximumIor || !float.IsFinite(dispersion) ||
            dispersion < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(centralIor));
        }

        float halfSpread = (centralIor - 1f) * 0.025f * dispersion;
        return new Vector3(
            MathF.Max(1f, centralIor - halfSpread),
            centralIor,
            MathF.Min(MaximumIor, centralIor + halfSpread));
    }

    internal static bool IsFiniteNonNegative(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && value.X >= 0f && value.Y >= 0f &&
        value.Z >= 0f;

    private static void ValidateInterface(
        float cosineIncident,
        float incidentIor,
        float transmittedIor)
    {
        if (!float.IsFinite(cosineIncident) ||
            !float.IsFinite(incidentIor) ||
            !float.IsFinite(transmittedIor) ||
            incidentIor < MinimumIor || incidentIor > MaximumIor ||
            transmittedIor < MinimumIor || transmittedIor > MaximumIor)
        {
            throw new ArgumentOutOfRangeException(nameof(incidentIor));
        }
    }

    private static Vector3 SafeNormalize(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || value.LengthSquared() <= 1e-12f)
        {
            return Vector3.Zero;
        }
        return value.Normalized();
    }
}
