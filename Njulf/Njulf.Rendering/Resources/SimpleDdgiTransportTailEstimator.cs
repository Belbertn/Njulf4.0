using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// CPU mirrors for the numerical rules used by the V2 transport operator and
/// audit.  These routines deliberately use infinity norms and do not average
/// over probes: a single bad active texel must keep the field pending.
/// </summary>
public static class SimpleDdgiTransportTailEstimator
{
    public const float AbsoluteTolerance = 0.0001f;
    public const float MaximumCertifiedContraction = 0.99f;

    public readonly record struct ThroughputNormalization(
        Vector3 Reflected,
        Vector3 Transmitted,
        float MaximumBeforeNormalization,
        float Scale,
        bool TransmissionEnabled,
        bool WasRenormalized,
        bool IsValid);

    public readonly record struct DirectionalSample(Vector3 Direction, Vector3 Radiance);

    public readonly record struct TailEstimate(
        float FixedPointDefect,
        float FieldMagnitude,
        float ConfiguredContractionBound,
        float ObservedContractionBound,
        float CertifiedContractionBound,
        float AbsoluteTailBound,
        float RelativeTailBound,
        float Tolerance,
        float CanonicalQuantizationFloor,
        bool IsFinite,
        bool HasValidContractionBound,
        bool QuantizationLimited,
        bool IsWithinTolerance)
    {
        public bool CanCertify =>
            IsFinite &&
            HasValidContractionBound &&
            !QuantizationLimited &&
            IsWithinTolerance;
    }

    /// <summary>
    /// Applies one common scale to reflected and transmitted lobes.  The
    /// component ratio between the lobes is unchanged by the recursive
    /// throughput ceiling.
    /// </summary>
    public static bool TryNormalizeRecursiveThroughput(
        Vector3 reflected,
        Vector3 transmitted,
        float contractionCeiling,
        bool transmissionEnabled,
        out ThroughputNormalization result)
    {
        result = default;
        if (!IsFiniteNonNegative(reflected) || !IsFiniteNonNegative(transmitted) ||
            !float.IsFinite(contractionCeiling) ||
            contractionCeiling < 0.0f ||
            contractionCeiling > MaximumCertifiedContraction)
        {
            return false;
        }

        if (!transmissionEnabled)
            transmitted = Vector3.Zero;

        // Keep the decoded lobe sum intact for the diagnostic and apply one
        // common scale to the pair. Per-lobe clamping would hide the actual
        // pre-normalization energy and could change the reflected/transmitted
        // ratio before the contraction ceiling is enforced.
        Vector3 combined = reflected + transmitted;
        float maximumBefore = MaxComponent(combined);
        float scale = maximumBefore > contractionCeiling && maximumBefore > 0.0f
            ? contractionCeiling / maximumBefore
            : 1.0f;

        result = new ThroughputNormalization(
            reflected * scale,
            transmitted * scale,
            maximumBefore,
            scale,
            transmissionEnabled,
            scale != 1.0f,
            true);
        return true;
    }

    /// <summary>
    /// Evaluates the positive irradiance estimator used for certified V2
    /// fields.  Invalid samples fail the entire estimate instead of silently
    /// dropping energy and allowing a false certificate.
    /// </summary>
    public static bool TryEvaluatePositiveIrradiance(
        ReadOnlySpan<DirectionalSample> samples,
        Vector3 normal,
        out Vector3 irradiance)
    {
        irradiance = Vector3.Zero;
        if (!IsFinite(normal))
            return false;

        float normalLengthSquared = normal.LengthSquared();
        if (!float.IsFinite(normalLengthSquared) || normalLengthSquared <= 1e-12f)
            return false;
        normal /= MathF.Sqrt(normalLengthSquared);

        Vector3 accumulated = Vector3.Zero;
        float weightSum = 0.0f;
        for (int i = 0; i < samples.Length; i++)
        {
            DirectionalSample sample = samples[i];
            if (!IsFinite(sample.Direction) || !IsFiniteNonNegative(sample.Radiance))
                return false;

            float directionLengthSquared = sample.Direction.LengthSquared();
            if (!float.IsFinite(directionLengthSquared) || directionLengthSquared <= 1e-12f)
                return false;

            Vector3 direction = sample.Direction / MathF.Sqrt(directionLengthSquared);
            float weight = MathF.Max(Vector3.Dot(normal, direction), 0.0f);
            accumulated += sample.Radiance * weight;
            weightSum += weight;
        }

        if (!float.IsFinite(weightSum) || !IsFinite(accumulated))
            return false;
        if (weightSum <= 1e-12f)
            return true;

        irradiance = accumulated * (MathF.PI / weightSum);
        return IsFiniteNonNegative(irradiance);
    }

    /// <summary>
    /// Computes the full-field infinity-norm tail certificate.  The candidate
    /// and canonical spans must cover exactly the same active field; no
    /// percentile or local-generation shortcut is used.
    /// </summary>
    public static TailEstimate EvaluateTail(
        ReadOnlySpan<Vector3> candidate,
        ReadOnlySpan<Vector3> canonical,
        float configuredContractionBound,
        float relativeTolerance,
        float observedContractionBound = float.NaN,
        float canonicalQuantizationFloor = 0.0f)
    {
        float defect = 0.0f;
        float fieldMagnitude = 0.0f;
        bool finite = candidate.Length == canonical.Length &&
                      float.IsFinite(configuredContractionBound) &&
                      configuredContractionBound >= 0.0f &&
                      configuredContractionBound <= MaximumCertifiedContraction &&
                      float.IsFinite(relativeTolerance) &&
                      relativeTolerance >= 0.0f &&
                      float.IsFinite(canonicalQuantizationFloor) &&
                      canonicalQuantizationFloor >= 0.0f;

        for (int i = 0; finite && i < candidate.Length; i++)
        {
            Vector3 next = candidate[i];
            Vector3 previous = canonical[i];
            if (!IsFiniteNonNegative(next) || !IsFiniteNonNegative(previous))
            {
                finite = false;
                break;
            }

            defect = MathF.Max(defect, MaxComponent(Abs(next - previous)));
            fieldMagnitude = MathF.Max(fieldMagnitude, MaxComponent(previous));
        }

        bool observedValid = float.IsNaN(observedContractionBound) ||
                             (float.IsFinite(observedContractionBound) &&
                              observedContractionBound >= 0.0f &&
                              observedContractionBound <= configuredContractionBound);
        float observed = float.IsNaN(observedContractionBound)
            ? configuredContractionBound
            : observedContractionBound;
        finite &= observedValid;

        float certifiedQ = finite ? MathF.Min(configuredContractionBound, observed) : float.NaN;
        float absoluteTail = finite ? defect / MathF.Max(1.0f - certifiedQ, 1e-6f) : float.NaN;
        float relativeTail = finite ? absoluteTail / MathF.Max(fieldMagnitude, AbsoluteTolerance) : float.NaN;
        float tolerance = finite
            ? MathF.Max(AbsoluteTolerance, relativeTolerance * fieldMagnitude)
            : float.NaN;
        // A quantization floor only blocks certification when it is larger
        // than the authored tail tolerance. A zero defect is a valid exact
        // fixed point (including an all-black field), so using `defect <=
        // floor` would incorrectly make every representable field pending.
        bool quantizationLimited = finite &&
                                   canonicalQuantizationFloor > tolerance;
        bool withinTolerance = finite && absoluteTail <= tolerance;

        return new TailEstimate(
            defect,
            fieldMagnitude,
            configuredContractionBound,
            observed,
            certifiedQ,
            absoluteTail,
            relativeTail,
            tolerance,
            canonicalQuantizationFloor,
            finite,
            finite,
            quantizationLimited,
            withinTolerance);
    }

    /// <summary>
    /// Analytic sanity oracle for a scalar positive fixed point.  It is useful
    /// in tests because the measured defect and the geometric tail are both
    /// known exactly up to the requested floating-point precision.
    /// </summary>
    public static float GeometricTail(float sourceMagnitude, float contraction, int completedIterations)
    {
        if (!float.IsFinite(sourceMagnitude) || sourceMagnitude < 0.0f ||
            !float.IsFinite(contraction) || contraction < 0.0f || contraction >= 1.0f ||
            completedIterations < 0)
        {
            return float.NaN;
        }

        return sourceMagnitude * MathF.Pow(contraction, completedIterations) /
               MathF.Max(1.0f - contraction, 1e-6f);
    }

    /// <summary>
    /// Mirrors the audit's fail-closed source-cache identity check. A cache
    /// entry belongs to the frozen source field only when it carries the full
    /// volume sequence and all three ownership generations match exactly.
    /// </summary>
    public static bool IsCompleteCurrentSourceCacheEntry(
        uint storedSourceRayCount,
        uint requiredSourceRayCount,
        uint physicalGeneration,
        uint expectedPhysicalGeneration,
        uint sourceLightingGeneration,
        uint expectedSourceLightingGeneration,
        uint sourceEpoch,
        uint expectedSourceEpoch)
    {
        bool generationsValid =
            expectedPhysicalGeneration is > 0u and <= SimpleDdgiSchedulerAbi.PhysicalGenerationMask &&
            expectedSourceLightingGeneration != 0u &&
            expectedSourceEpoch != 0u;
        return requiredSourceRayCount != 0u &&
               storedSourceRayCount == requiredSourceRayCount &&
               generationsValid &&
               physicalGeneration == expectedPhysicalGeneration &&
               sourceLightingGeneration == expectedSourceLightingGeneration &&
               sourceEpoch == expectedSourceEpoch;
    }

    public static float MaxComponent(Vector3 value) =>
        MathF.Max(value.X, MathF.Max(value.Y, value.Z));

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool IsFiniteNonNegative(Vector3 value) =>
        IsFinite(value) && value.X >= 0.0f && value.Y >= 0.0f && value.Z >= 0.0f;

    private static Vector3 Abs(Vector3 value) => new(
        MathF.Abs(value.X),
        MathF.Abs(value.Y),
        MathF.Abs(value.Z));
}
