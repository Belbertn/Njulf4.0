using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiDirectionalGuidingPrerequisites(
    bool SpatialEmissiveSamplingReady,
    bool CachedRelightingReady,
    bool VariablePdfDirectionIdentityAvailable,
    bool MaintenanceSubsetPdfAudited,
    bool CacheCardinalityAndTailAuditUpdated,
    bool ReferenceParityPassed,
    bool QualityPerMillisecondImproved);

public static class SimpleDdgiDirectionalGuidingExperiment
{
    public const float UniformSpherePdf = 1.0f / (4.0f * MathF.PI);
    public const float MinimumUniformFraction = 0.10f;

    public static GiExperimentAdmission EvaluateAdmission(
        bool requested,
        in SimpleDdgiDirectionalGuidingPrerequisites prerequisites,
        ulong allocatedBytes = 0UL)
    {
        if (!requested)
            return GiExperimentAdmission.Disabled("C3");
        if (!prerequisites.SpatialEmissiveSamplingReady ||
            !prerequisites.CachedRelightingReady)
        {
            return GiExperimentAdmission.Missing(
                "C3",
                "spatial-emissive-sampling-and-cached-relighting-required");
        }
        if (!prerequisites.VariablePdfDirectionIdentityAvailable)
        {
            return GiExperimentAdmission.Missing(
                "C3",
                "variable-pdf-direction-identity-redesign-required",
                capabilitySupported: true);
        }
        if (!prerequisites.MaintenanceSubsetPdfAudited ||
            !prerequisites.CacheCardinalityAndTailAuditUpdated)
        {
            return GiExperimentAdmission.Missing(
                "C3",
                "maintenance-cache-and-tail-pdf-audit-required",
                capabilitySupported: true);
        }
        if (!prerequisites.ReferenceParityPassed ||
            !prerequisites.QualityPerMillisecondImproved)
        {
            return new GiExperimentAdmission(
                "C3",
                true,
                true,
                false,
                GiExperimentStage.QualificationFailed,
                0UL,
                !prerequisites.ReferenceParityPassed
                    ? "mis-reference-parity-failed"
                    : "quality-per-millisecond-win-not-demonstrated");
        }

        return new GiExperimentAdmission(
            "C3",
            true,
            true,
            true,
            GiExperimentStage.Active,
            allocatedBytes,
            "active-qualified-experiment");
    }

    /// <summary>
    /// Exact continuous mixture PDF. The uniform proposal is clamped to a
    /// nonzero floor, preserving support even when the learned density is zero.
    /// </summary>
    public static float MixturePdf(
        float guidedPdf,
        float uniformFraction)
    {
        if (!float.IsFinite(guidedPdf) || guidedPdf < 0.0f ||
            !float.IsFinite(uniformFraction))
            throw new ArgumentOutOfRangeException(nameof(guidedPdf));
        float uniform = Math.Clamp(
            uniformFraction,
            MinimumUniformFraction,
            1.0f);
        return uniform * UniformSpherePdf +
            (1.0f - uniform) * guidedPdf;
    }

    public static float BalanceWeight(
        int thisTechniqueSampleCount,
        float thisTechniquePdf,
        int otherTechniqueSampleCount,
        float otherTechniquePdf)
    {
        if (thisTechniqueSampleCount < 0 || otherTechniqueSampleCount < 0 ||
            !float.IsFinite(thisTechniquePdf) || thisTechniquePdf < 0.0f ||
            !float.IsFinite(otherTechniquePdf) || otherTechniquePdf < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(thisTechniquePdf));
        }
        float numerator = thisTechniqueSampleCount * thisTechniquePdf;
        float denominator = numerator +
            otherTechniqueSampleCount * otherTechniquePdf;
        return denominator > 0.0f ? numerator / denominator : 0.0f;
    }

    public static Vector3 EstimateIntegralContribution(
        Vector3 integrand,
        float mixturePdf)
    {
        if (!float.IsFinite(mixturePdf) || mixturePdf <= 0.0f ||
            !IsFinite(integrand))
            throw new ArgumentOutOfRangeException(nameof(mixturePdf));
        return integrand / mixturePdf;
    }

    /// <summary>
    /// Builds a discrete solid-angle-correct mixture used by CPU oracles. The
    /// output contains probability mass per bin, not density; it sums to one.
    /// </summary>
    public static void BuildHistogramMixture(
        ReadOnlySpan<float> incidentEnergy,
        ReadOnlySpan<float> solidAngles,
        float uniformFraction,
        Span<float> probabilityMass)
    {
        if (incidentEnergy.Length == 0 ||
            incidentEnergy.Length != solidAngles.Length ||
            probabilityMass.Length < incidentEnergy.Length)
        {
            throw new ArgumentException(
                "Histogram energy, solid angle, and output sizes must match.");
        }

        double energyIntegral = 0.0;
        double solidAngleSum = 0.0;
        for (int bin = 0; bin < incidentEnergy.Length; bin++)
        {
            float energy = incidentEnergy[bin];
            float solidAngle = solidAngles[bin];
            if (!float.IsFinite(energy) || energy < 0.0f ||
                !float.IsFinite(solidAngle) || solidAngle <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(incidentEnergy));
            energyIntegral += energy * solidAngle;
            solidAngleSum += solidAngle;
        }
        if (solidAngleSum <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(solidAngles));

        float uniform = Math.Clamp(
            uniformFraction,
            MinimumUniformFraction,
            1.0f);
        double normalizedSum = 0.0;
        for (int bin = 0; bin < incidentEnergy.Length; bin++)
        {
            double uniformMass = solidAngles[bin] / solidAngleSum;
            double guidedMass = energyIntegral > 0.0
                ? incidentEnergy[bin] * solidAngles[bin] / energyIntegral
                : uniformMass;
            float mass = checked((float)(
                uniform * uniformMass + (1.0f - uniform) * guidedMass));
            probabilityMass[bin] = mass;
            normalizedSum += mass;
        }

        // Remove accumulated float error without changing support ordering.
        float inverse = checked((float)(1.0 / normalizedSum));
        for (int bin = 0; bin < incidentEnergy.Length; bin++)
            probabilityMass[bin] *= inverse;
    }

    public static Vector3 EstimateHistogramIntegralContribution(
        Vector3 integrand,
        float solidAngle,
        float probabilityMass)
    {
        if (!IsFinite(integrand) ||
            !float.IsFinite(solidAngle) || solidAngle <= 0.0f ||
            !float.IsFinite(probabilityMass) || probabilityMass <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(probabilityMass));
        }
        return integrand * (solidAngle / probabilityMass);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
