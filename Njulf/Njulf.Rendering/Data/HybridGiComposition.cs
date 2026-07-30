using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Deterministic CPU oracle for the hybrid diffuse-GI composition contract.
/// DDGI and environment fallback form one baseline estimator; SSGI replaces a
/// confidence-bounded share of that same path space through a signed delta.
/// </summary>
public static class HybridGiComposition
{
    public static HybridGiCompositionResult Compose(
        Vector3 baseline,
        Vector3 ssgi,
        float ssgiSupport,
        float depthConfidence = 1f,
        float normalConfidence = 1f,
        float distanceConfidence = 1f,
        float temporalConfidence = 1f,
        float ddgiOwnership = 1f,
        float environmentFallbackShare = 0f)
    {
        ValidateFinite(baseline, nameof(baseline));
        ValidateFinite(ssgi, nameof(ssgi));

        Vector3 safeBaseline = ClampRadiance(baseline);
        Vector3 safeSsgi = ClampRadiance(ssgi);
        float baselineShare = Saturate(Saturate(ddgiOwnership) + Saturate(environmentFallbackShare));
        float weight = ResolveSsgiWeight(
            ssgiSupport,
            depthConfidence,
            normalConfidence,
            distanceConfidence,
            temporalConfidence,
            baselineShare);
        Vector3 delta = (safeSsgi - safeBaseline) * weight;
        Vector3 composed = safeBaseline + delta;

        return new HybridGiCompositionResult(
            safeBaseline,
            safeSsgi,
            composed,
            delta,
            weight,
            Saturate(ddgiOwnership),
            Saturate(environmentFallbackShare));
    }

    /// <summary>
    /// Each confidence is an independent upper bound. Multiplication keeps the
    /// resulting partition weight in [0,1] and makes any unsupported input
    /// select the baseline exactly.
    /// </summary>
    public static float ResolveSsgiWeight(
        float ssgiSupport,
        float depthConfidence,
        float normalConfidence,
        float distanceConfidence,
        float temporalConfidence,
        float baselineShare = 1f)
    {
        ValidateFinite(ssgiSupport, nameof(ssgiSupport));
        ValidateFinite(depthConfidence, nameof(depthConfidence));
        ValidateFinite(normalConfidence, nameof(normalConfidence));
        ValidateFinite(distanceConfidence, nameof(distanceConfidence));
        ValidateFinite(temporalConfidence, nameof(temporalConfidence));
        ValidateFinite(baselineShare, nameof(baselineShare));

        return Saturate(ssgiSupport) *
               Saturate(depthConfidence) *
               Saturate(normalConfidence) *
               Saturate(distanceConfidence) *
               Saturate(temporalConfidence) *
               Saturate(baselineShare);
    }

    public static bool IsComponentwiseBounded(in HybridGiCompositionResult result, float tolerance = 1e-6f)
    {
        Vector3 minimum = Vector3.Min(result.Baseline, result.Ssgi) - new Vector3(tolerance);
        Vector3 maximum = Vector3.Max(result.Baseline, result.Ssgi) + new Vector3(tolerance);
        Vector3 value = result.Composed;
        return value.X >= minimum.X && value.Y >= minimum.Y && value.Z >= minimum.Z &&
               value.X <= maximum.X && value.Y <= maximum.Y && value.Z <= maximum.Z;
    }

    private static Vector3 ClampRadiance(Vector3 value) => Vector3.Clamp(
        value,
        Vector3.Zero,
        new Vector3(GiMaterialReferenceEvaluator.MaximumFiniteRadiance));

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    private static void ValidateFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "Hybrid GI radiance must be finite.");
    }

    private static void ValidateFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Hybrid GI confidence must be finite.");
    }
}

public readonly record struct HybridGiCompositionResult(
    Vector3 Baseline,
    Vector3 Ssgi,
    Vector3 Composed,
    Vector3 SignedDelta,
    float SsgiWeight,
    float DdgiOwnership,
    float EnvironmentFallbackShare);
