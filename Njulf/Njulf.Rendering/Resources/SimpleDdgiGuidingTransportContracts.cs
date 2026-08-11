using System;
using System.Numerics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Bounded request for the source-cache-owned C3 direction/PDF sidecar. The
/// distribution and sidecar domains intentionally admit the same prefix of
/// physical probe slots; probes outside that domain retain ordinary uniform
/// sampling rather than using a modulo/hash alias.
/// </summary>
public readonly record struct SimpleDdgiGuidingSourceCacheLayoutRequest(
    bool Enabled,
    int TotalPhysicalProbeCapacity,
    int RequestedGuidedPhysicalProbeCapacity,
    int DirectionSlotsPerProbe,
    ulong MemoryBudgetBytes);

/// <summary>Exact source-cache sidecar admission and addressing contract.</summary>
public readonly record struct SimpleDdgiGuidingSourceCacheLayout(
    bool IsAdmitted,
    int RequestedGuidedPhysicalProbeCapacity,
    int AdmittedGuidedPhysicalProbeCapacity,
    int DirectionSlotsPerProbe,
    uint PayloadCapacity,
    ulong PayloadStrideBytes,
    ulong RequestedBytes,
    ulong AllocatedBytes,
    string Reason)
{
    public static SimpleDdgiGuidingSourceCacheLayout Disabled { get; } = new(
        IsAdmitted: false,
        RequestedGuidedPhysicalProbeCapacity: 0,
        AdmittedGuidedPhysicalProbeCapacity: 0,
        DirectionSlotsPerProbe: 0,
        PayloadCapacity: 0u,
        PayloadStrideBytes: SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount,
        RequestedBytes: 0UL,
        AllocatedBytes: 0UL,
        Reason: "directional-guiding-disabled");

    public bool TryGetPayloadIndex(
        int physicalProbeIndex,
        int directionSlot,
        out uint payloadIndex)
    {
        payloadIndex = 0u;
        if (!IsAdmitted || physicalProbeIndex < 0 || directionSlot < 0 ||
            physicalProbeIndex >= AdmittedGuidedPhysicalProbeCapacity ||
            directionSlot >= DirectionSlotsPerProbe)
        {
            return false;
        }

        ulong index = checked(
            (ulong)physicalProbeIndex * (ulong)DirectionSlotsPerProbe +
            (ulong)directionSlot);
        if (index >= PayloadCapacity)
            return false;
        payloadIndex = checked((uint)index);
        return true;
    }

    public bool TryGetPayloadByteOffset(
        int physicalProbeIndex,
        int directionSlot,
        out ulong byteOffset)
    {
        byteOffset = 0UL;
        if (!TryGetPayloadIndex(
                physicalProbeIndex,
                directionSlot,
                out uint payloadIndex))
        {
            return false;
        }
        byteOffset = checked((ulong)payloadIndex * PayloadStrideBytes);
        return true;
    }
}

/// <summary>
/// Pure admission compiler. Memory pressure reduces the number of guided
/// physical slots before allocation; it never reduces the fixed maintenance
/// ray cardinality or creates an incompletely backed probe record.
/// </summary>
public static class SimpleDdgiGuidingSourceCacheLayoutCompiler
{
    public const int MaximumDirectionSlotsPerProbe = 256;

    public static SimpleDdgiGuidingSourceCacheLayout Compile(
        in SimpleDdgiGuidingSourceCacheLayoutRequest request)
    {
        if (!request.Enabled)
            return SimpleDdgiGuidingSourceCacheLayout.Disabled;
        if (request.TotalPhysicalProbeCapacity <= 0)
            return Rejected("guiding-source-cache-has-no-physical-probes");
        if (request.RequestedGuidedPhysicalProbeCapacity <= 0 ||
            request.RequestedGuidedPhysicalProbeCapacity >
                request.TotalPhysicalProbeCapacity)
        {
            return Rejected("guiding-source-cache-guided-probe-capacity-invalid");
        }
        if (request.DirectionSlotsPerProbe is <= 0 or >
            MaximumDirectionSlotsPerProbe)
        {
            return Rejected("guiding-source-cache-direction-slot-count-invalid");
        }
        if (request.MemoryBudgetBytes == 0UL)
            return Rejected("guiding-source-cache-memory-budget-missing");

        try
        {
            ulong bytesPerProbe = checked(
                (ulong)request.DirectionSlotsPerProbe *
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
            ulong requestedBytes = checked(
                (ulong)request.RequestedGuidedPhysicalProbeCapacity *
                bytesPerProbe);
            ulong budgetProbeCapacity = request.MemoryBudgetBytes /
                bytesPerProbe;
            ulong uintAddressProbeCapacity = uint.MaxValue /
                (uint)request.DirectionSlotsPerProbe;
            ulong admitted64 = Math.Min(
                (ulong)request.RequestedGuidedPhysicalProbeCapacity,
                Math.Min(budgetProbeCapacity, uintAddressProbeCapacity));
            if (admitted64 == 0UL)
                return Rejected("guiding-source-cache-budget-admits-no-complete-probe");

            int admitted = checked((int)admitted64);
            uint payloadCapacity = checked((uint)(
                admitted64 * (ulong)request.DirectionSlotsPerProbe));
            ulong allocatedBytes = checked(
                (ulong)payloadCapacity *
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
            string reason = admitted ==
                request.RequestedGuidedPhysicalProbeCapacity
                    ? "admitted"
                    : "admitted-prefix-reduced-by-direction-sidecar-budget";
            return new SimpleDdgiGuidingSourceCacheLayout(
                IsAdmitted: true,
                request.RequestedGuidedPhysicalProbeCapacity,
                admitted,
                request.DirectionSlotsPerProbe,
                payloadCapacity,
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount,
                requestedBytes,
                allocatedBytes,
                reason);
        }
        catch (OverflowException)
        {
            return Rejected("guiding-source-cache-layout-overflow");
        }
    }

    private static SimpleDdgiGuidingSourceCacheLayout Rejected(string reason) =>
        SimpleDdgiGuidingSourceCacheLayout.Disabled with { Reason = reason };
}

/// <summary>One finite C3 radiometric observation before atlas projection.</summary>
public readonly record struct SimpleDdgiGuidingProjectionSample(
    Vector3 IncidentRadiance,
    Vector3 Direction,
    SimpleDdgiDirectionSamplingTechnique Technique,
    float GenerationTimeMixturePdf,
    bool IsPublishable = true);

/// <summary>Double-precision projection result used by CPU/GPU parity gates.</summary>
public readonly record struct SimpleDdgiGuidingProjectionResult(
    bool IsValid,
    Vector3 Irradiance,
    int UniformMaintenanceSampleCount,
    int MixtureSampleCount,
    double MinimumPdfDenominator,
    double MaximumInversePdfWeight,
    double EffectiveSampleSize,
    string Reason)
{
    public static SimpleDdgiGuidingProjectionResult Invalid(string reason) =>
        new(false, Vector3.Zero, 0, 0, 0.0d, 0.0d, 0.0d, reason);
}

/// <summary>
/// Frozen C3 transport policy and estimator. The result is the Monte-Carlo
/// irradiance integral directly; no equal-ray cosine normalization is applied
/// after inverse-PDF weighting.
/// </summary>
public static class SimpleDdgiGuidingTransportEstimator
{
    public const double UniformSpherePdf = 1.0d / (4.0d * Math.PI);
    public const double MaintenanceFraction = 0.25d;
    public const int MinimumMaintenanceRays = 8;

    public static int ResolveMaintenanceRayCount(int totalRayCount)
    {
        if (totalRayCount <= 0 || totalRayCount >
            SimpleDdgiGuidingSourceCacheLayoutCompiler.MaximumDirectionSlotsPerProbe)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRayCount));
        }
        int fractional = checked((int)Math.Ceiling(
            totalRayCount * MaintenanceFraction));
        return Math.Min(
            totalRayCount,
            Math.Max(MinimumMaintenanceRays, fractional));
    }

    /// <summary>
    /// Returns the fixed stratified maintenance subset. This mirrors the
    /// scheduler mapping floor(k * total / maintenance), so a maintenance-only
    /// dispatch addresses every and only uniform-maintenance payload.
    /// </summary>
    public static bool IsMaintenanceSlot(int slotIndex, int totalRayCount)
    {
        int maintenanceCount = ResolveMaintenanceRayCount(totalRayCount);
        if (slotIndex < 0 || slotIndex >= totalRayCount)
            return false;
        int rank = checked((slotIndex * maintenanceCount + totalRayCount - 1) /
            totalRayCount);
        return slotIndex == checked(rank * totalRayCount / maintenanceCount);
    }

    /// <summary>
    /// Returns the proposal density used by unbiased guide training. A
    /// maintenance ray is sampled from pUniform even though its payload stores
    /// pMix(direction) for the radiometric balance denominator.
    /// </summary>
    public static double ResolveTrainingPdf(
        SimpleDdgiDirectionSamplingTechnique technique,
        double generationTimeMixturePdf) => technique switch
        {
            SimpleDdgiDirectionSamplingTechnique.UniformMaintenance =>
                UniformSpherePdf,
            SimpleDdgiDirectionSamplingTechnique.Mixture
                when double.IsFinite(generationTimeMixturePdf) &&
                     generationTimeMixturePdf > 0.0d =>
                generationTimeMixturePdf,
            _ => throw new ArgumentOutOfRangeException(nameof(technique))
        };

    public static double EvaluateBalanceDenominator(
        int uniformMaintenanceSampleCount,
        int mixtureSampleCount,
        double generationTimeMixturePdf)
    {
        if (uniformMaintenanceSampleCount < 0 || mixtureSampleCount < 0 ||
            uniformMaintenanceSampleCount + mixtureSampleCount == 0 ||
            !double.IsFinite(generationTimeMixturePdf) ||
            generationTimeMixturePdf <= 0.0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generationTimeMixturePdf));
        }
        return uniformMaintenanceSampleCount * UniformSpherePdf +
            mixtureSampleCount * generationTimeMixturePdf;
    }

    public static bool OwnsVisibility(
        SimpleDdgiDirectionSamplingTechnique technique) =>
        technique == SimpleDdgiDirectionSamplingTechnique.UniformMaintenance;

    public static SimpleDdgiGuidingProjectionResult ProjectIrradiance(
        Vector3 receiverDirection,
        ReadOnlySpan<SimpleDdgiGuidingProjectionSample> samples)
    {
        if (!IsFinite(receiverDirection) ||
            receiverDirection.LengthSquared() <= 1.0e-12f)
        {
            return SimpleDdgiGuidingProjectionResult.Invalid(
                "guiding-projection-receiver-direction-invalid");
        }
        if (samples.IsEmpty)
        {
            return SimpleDdgiGuidingProjectionResult.Invalid(
                "guiding-projection-has-no-samples");
        }

        int uniformCount = 0;
        int mixtureCount = 0;
        foreach (ref readonly SimpleDdgiGuidingProjectionSample sample in samples)
        {
            switch (sample.Technique)
            {
                case SimpleDdgiDirectionSamplingTechnique.UniformMaintenance:
                    uniformCount++;
                    break;
                case SimpleDdgiDirectionSamplingTechnique.Mixture:
                    mixtureCount++;
                    break;
                default:
                    return SimpleDdgiGuidingProjectionResult.Invalid(
                        "guiding-projection-technique-invalid");
            }
        }

        Vector3 normal = Vector3.Normalize(receiverDirection);
        double accumulatedR = 0.0d;
        double accumulatedG = 0.0d;
        double accumulatedB = 0.0d;
        double minimumDenominator = double.PositiveInfinity;
        double maximumInverseWeight = 0.0d;
        double sumWeights = 0.0d;
        double sumSquaredWeights = 0.0d;
        foreach (ref readonly SimpleDdgiGuidingProjectionSample sample in samples)
        {
            if (!sample.IsPublishable || !IsFinite(sample.IncidentRadiance) ||
                sample.IncidentRadiance.X < 0.0f ||
                sample.IncidentRadiance.Y < 0.0f ||
                sample.IncidentRadiance.Z < 0.0f ||
                !IsFinite(sample.Direction) ||
                sample.Direction.LengthSquared() <= 1.0e-12f ||
                !float.IsFinite(sample.GenerationTimeMixturePdf) ||
                sample.GenerationTimeMixturePdf <= 0.0f)
            {
                return SimpleDdgiGuidingProjectionResult.Invalid(
                    "guiding-projection-sample-invalid");
            }

            double denominator = EvaluateBalanceDenominator(
                uniformCount,
                mixtureCount,
                sample.GenerationTimeMixturePdf);
            if (!double.IsFinite(denominator) || denominator <= 0.0d)
            {
                return SimpleDdgiGuidingProjectionResult.Invalid(
                    "guiding-projection-pdf-denominator-invalid");
            }
            Vector3 direction = Vector3.Normalize(sample.Direction);
            double cosine = Math.Max(Vector3.Dot(normal, direction), 0.0f);
            double weight = cosine / denominator;
            if (!double.IsFinite(weight))
            {
                return SimpleDdgiGuidingProjectionResult.Invalid(
                    "guiding-projection-weight-nonfinite");
            }
            accumulatedR += sample.IncidentRadiance.X * weight;
            accumulatedG += sample.IncidentRadiance.Y * weight;
            accumulatedB += sample.IncidentRadiance.Z * weight;
            minimumDenominator = Math.Min(minimumDenominator, denominator);
            maximumInverseWeight = Math.Max(
                maximumInverseWeight,
                1.0d / denominator);
            sumWeights += weight;
            sumSquaredWeights += weight * weight;
        }

        if (!double.IsFinite(accumulatedR) || !double.IsFinite(accumulatedG) ||
            !double.IsFinite(accumulatedB) || accumulatedR < 0.0d ||
            accumulatedG < 0.0d || accumulatedB < 0.0d ||
            accumulatedR > float.MaxValue || accumulatedG > float.MaxValue ||
            accumulatedB > float.MaxValue)
        {
            return SimpleDdgiGuidingProjectionResult.Invalid(
                "guiding-projection-result-invalid");
        }
        double effectiveSampleSize = sumSquaredWeights > 0.0d
            ? sumWeights * sumWeights / sumSquaredWeights
            : 0.0d;
        return new SimpleDdgiGuidingProjectionResult(
            IsValid: true,
            new Vector3(
                (float)accumulatedR,
                (float)accumulatedG,
                (float)accumulatedB),
            uniformCount,
            mixtureCount,
            minimumDenominator,
            maximumInverseWeight,
            effectiveSampleSize,
            "valid");
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
