using System;

namespace Njulf.Rendering.Data;

public enum SimpleDdgiNearVisibilityDisposition
{
    Disabled,
    IneligibleVolume,
    InsufficientConfidence,
    InvalidDepth,
    NoMomentDiscrepancy,
    ReceiverInFront,
    Applied
}

/// <summary>
/// One packed B4 sidecar texel. X is conservative hit distance and Y is the
/// directional hit confidence. It deliberately has the same four-byte stride
/// as the canonical RG16F visibility moments.
/// </summary>
public readonly record struct SimpleDdgiNearVisibilitySample(
    float ConservativeDepth,
    float Confidence)
{
    public const float MaximumHalf = 65_504.0f;

    public static SimpleDdgiNearVisibilitySample Empty { get; } =
        new(MaximumHalf, 0.0f);
}

/// <summary>One uniform visibility ray projected into an octahedral texel.</summary>
public readonly record struct SimpleDdgiNearVisibilityRay(
    float Cosine,
    float Distance,
    bool Hit);

public readonly record struct SimpleDdgiNearVisibilityQuery(
    float MomentMean,
    float MomentSecond,
    float ReceiverDistance,
    float ProbeSpacing,
    float ArchitecturalThickness,
    SimpleDdgiNearVisibilitySample Sidecar,
    SimpleDdgiVolumeKind VolumeKind,
    int SourceOrdinal,
    bool Enabled = true);

public readonly record struct SimpleDdgiNearVisibilityEvaluation(
    float MomentVisibility,
    float FinalVisibility,
    float ConservativeVisibility,
    float EvidenceTrust,
    float OccluderCoverage,
    SimpleDdgiNearVisibilityDisposition Disposition)
{
    public bool Applied =>
        Disposition == SimpleDdgiNearVisibilityDisposition.Applied;
}

public readonly record struct SimpleDdgiNearVisibilityBilinearEvaluation(
    float MomentVisibility,
    float FinalVisibility,
    float ConservativeVisibility,
    int AppliedTapCount)
{
    public bool Applied => FinalVisibility < MomentVisibility - 1.0e-6f;
}

/// <summary>
/// CPU reference for B4 admission, packing, temporal release, and receiver
/// evaluation. Constants and branch order intentionally mirror
/// ddgi_simple_shared.glsl/ddgi_simple_blend.comp so qualification fixtures can
/// distinguish a real thin-wall improvement from broad scene darkening.
/// </summary>
public static class SimpleDdgiNearVisibility
{
    public const int BytesPerTexel = sizeof(uint);
    public const float MinimumConfidence = 0.65f;
    public const float FullConfidence = 0.90f;
    public const float MinimumQualifyingNarrowWeight = 0.04f;

    public static float CoherentDepthBand(
        float probeSpacing,
        float architecturalThickness)
    {
        EnsureFinite(probeSpacing, nameof(probeSpacing));
        EnsureFinite(architecturalThickness, nameof(architecturalThickness));
        if (probeSpacing <= 0.0f || architecturalThickness <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(probeSpacing));
        return Math.Max(
            0.02f,
            Math.Min(probeSpacing * 0.10f, architecturalThickness * 0.50f));
    }

    public static SimpleDdgiNearVisibilitySample BuildSample(
        ReadOnlySpan<SimpleDdgiNearVisibilityRay> rays,
        float probeSpacing,
        float architecturalThickness)
    {
        float nearest = SimpleDdgiNearVisibilitySample.MaximumHalf;
        double allNarrowWeight = 0.0;
        for (int index = 0; index < rays.Length; index++)
        {
            SimpleDdgiNearVisibilityRay ray = rays[index];
            EnsureFinite(ray.Cosine, nameof(rays));
            EnsureFinite(ray.Distance, nameof(rays));
            float broadWeight = MathF.Pow(
                Math.Clamp(ray.Cosine, 0.0f, 1.0f),
                16.0f);
            float narrowWeight = broadWeight * broadWeight;
            allNarrowWeight += narrowWeight;
            if (ray.Hit && narrowWeight >= MinimumQualifyingNarrowWeight)
                nearest = Math.Min(nearest, Math.Max(ray.Distance, 0.0f));
        }

        if (nearest >= SimpleDdgiNearVisibilitySample.MaximumHalf - 1.0f ||
            allNarrowWeight <= 1.0e-6)
        {
            return SimpleDdgiNearVisibilitySample.Empty;
        }

        float band = CoherentDepthBand(probeSpacing, architecturalThickness);
        double clusteredWeight = 0.0;
        double clusteredDepth = 0.0;
        for (int index = 0; index < rays.Length; index++)
        {
            SimpleDdgiNearVisibilityRay ray = rays[index];
            float depth = Math.Max(ray.Distance, 0.0f);
            if (!ray.Hit || Math.Abs(depth - nearest) > band)
                continue;

            float broadWeight = MathF.Pow(
                Math.Clamp(ray.Cosine, 0.0f, 1.0f),
                16.0f);
            float narrowWeight = broadWeight * broadWeight;
            clusteredWeight += narrowWeight;
            clusteredDepth += depth * narrowWeight;
        }

        if (clusteredWeight <= 1.0e-6)
            return SimpleDdgiNearVisibilitySample.Empty;
        return new SimpleDdgiNearVisibilitySample(
            (float)(clusteredDepth / clusteredWeight),
            Math.Clamp((float)(clusteredWeight / allNarrowWeight), 0.0f, 1.0f));
    }

    public static bool UsesSidecar(
        SimpleDdgiVolumeKind volumeKind,
        int sourceOrdinal) =>
        volumeKind == SimpleDdgiVolumeKind.RefinementBrick ||
        (volumeKind == SimpleDdgiVolumeKind.CameraRing &&
            sourceOrdinal == 10_000);

    public static uint Pack(SimpleDdgiNearVisibilitySample sample)
    {
        EnsureFinite(sample.ConservativeDepth, nameof(sample));
        EnsureFinite(sample.Confidence, nameof(sample));
        ushort depth = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(
            sample.ConservativeDepth,
            0.0f,
            SimpleDdgiNearVisibilitySample.MaximumHalf));
        ushort confidence = BitConverter.HalfToUInt16Bits((Half)Math.Clamp(
            sample.Confidence,
            0.0f,
            1.0f));
        return depth | ((uint)confidence << 16);
    }

    public static SimpleDdgiNearVisibilitySample Unpack(uint packed) => new(
        (float)BitConverter.UInt16BitsToHalf(
            checked((ushort)(packed & 0xffffu))),
        (float)BitConverter.UInt16BitsToHalf(
            checked((ushort)(packed >> 16))));

    public static SimpleDdgiNearVisibilitySample BlendEvidence(
        SimpleDdgiNearVisibilitySample previous,
        SimpleDdgiNearVisibilitySample current,
        float texelHysteresis,
        bool historyValid,
        bool freshUpdate,
        float probeSpacing,
        float architecturalThickness = 0.08f)
    {
        EnsureFinite(previous.ConservativeDepth, nameof(previous));
        EnsureFinite(previous.Confidence, nameof(previous));
        EnsureFinite(current.ConservativeDepth, nameof(current));
        EnsureFinite(current.Confidence, nameof(current));
        EnsureFinite(texelHysteresis, nameof(texelHysteresis));
        EnsureFinite(probeSpacing, nameof(probeSpacing));
        EnsureFinite(architecturalThickness, nameof(architecturalThickness));

        float previousDepth = Math.Clamp(
            previous.ConservativeDepth,
            0.0f,
            SimpleDdgiNearVisibilitySample.MaximumHalf);
        float previousConfidence = Math.Clamp(previous.Confidence, 0.0f, 1.0f);
        float currentConfidence = Math.Clamp(current.Confidence, 0.0f, 1.0f);
        float currentDepth = currentConfidence > 0.0f
            ? Math.Clamp(
                current.ConservativeDepth,
                0.0f,
                SimpleDdgiNearVisibilitySample.MaximumHalf)
            : (previousConfidence > 0.0f
                ? previousDepth
                : SimpleDdgiNearVisibilitySample.MaximumHalf);

        float hysteresis = historyValid && !freshUpdate
            ? Math.Min(Math.Clamp(texelHysteresis, 0.0f, 1.0f), 0.85f)
            : 0.0f;
        float depthBand = CoherentDepthBand(
            Math.Max(probeSpacing, 0.001f),
            Math.Max(architecturalThickness, 0.008f));
        if (currentConfidence < MinimumConfidence ||
            previousConfidence < MinimumConfidence ||
            Math.Abs(currentDepth - previousDepth) > depthBand)
        {
            hysteresis = 0.0f;
        }

        return new SimpleDdgiNearVisibilitySample(
            Lerp(currentDepth, previousDepth, hysteresis),
            Math.Clamp(
                Lerp(currentConfidence, previousConfidence, hysteresis),
                0.0f,
                1.0f));
    }

    public static SimpleDdgiNearVisibilityBilinearEvaluation EvaluateBilinear(
        float momentVisibility,
        ReadOnlySpan<SimpleDdgiNearVisibilityQuery> taps,
        ReadOnlySpan<float> weights)
    {
        EnsureFinite(momentVisibility, nameof(momentVisibility));
        if (taps.Length != 4 || weights.Length != 4)
            throw new ArgumentException("Bilinear near visibility requires four taps.");

        float conservativeVisibility = 0.0f;
        float weightSum = 0.0f;
        int appliedTapCount = 0;
        for (int index = 0; index < 4; index++)
        {
            EnsureFinite(weights[index], nameof(weights));
            float weight = Math.Max(weights[index], 0.0f);
            SimpleDdgiNearVisibilityEvaluation tap = Evaluate(taps[index]);
            float tapFactor = tap.Applied ? tap.ConservativeVisibility : 1.0f;
            conservativeVisibility += weight * tapFactor;
            weightSum += weight;
            if (tap.Applied && weight > 0.0f)
                appliedTapCount++;
        }

        conservativeVisibility = weightSum > 1.0e-6f
            ? conservativeVisibility / weightSum
            : 1.0f;
        float clampedMomentVisibility = Math.Clamp(momentVisibility, 0.0f, 1.0f);
        return new SimpleDdgiNearVisibilityBilinearEvaluation(
            clampedMomentVisibility,
            Math.Min(clampedMomentVisibility, conservativeVisibility),
            conservativeVisibility,
            appliedTapCount);
    }

    public static SimpleDdgiNearVisibilityEvaluation Evaluate(
        in SimpleDdgiNearVisibilityQuery query)
    {
        Validate(query);
        float spacing = Math.Max(query.ProbeSpacing, 0.001f);
        float thickness = Math.Clamp(
            query.ArchitecturalThickness,
            0.008f,
            4.0f);
        float momentVisibility = Chebyshev(
            query.MomentMean,
            query.MomentSecond,
            query.ReceiverDistance,
            spacing);

        SimpleDdgiNearVisibilityEvaluation Unchanged(
            SimpleDdgiNearVisibilityDisposition disposition) => new(
                momentVisibility,
                momentVisibility,
                1.0f,
                0.0f,
                0.0f,
                disposition);

        if (!query.Enabled)
            return Unchanged(SimpleDdgiNearVisibilityDisposition.Disabled);
        if (!UsesSidecar(query.VolumeKind, query.SourceOrdinal))
        {
            return Unchanged(
                SimpleDdgiNearVisibilityDisposition.IneligibleVolume);
        }

        float depth = query.Sidecar.ConservativeDepth;
        float confidence = Math.Clamp(query.Sidecar.Confidence, 0.0f, 1.0f);
        if (confidence < MinimumConfidence)
        {
            return Unchanged(
                SimpleDdgiNearVisibilityDisposition.InsufficientConfidence);
        }
        if (depth <= 0.0f ||
            depth >= SimpleDdgiNearVisibilitySample.MaximumHalf - 1.0f)
            return Unchanged(SimpleDdgiNearVisibilityDisposition.InvalidDepth);

        float discrepancyMargin = Math.Max(
            0.02f,
            Math.Min(spacing * 0.12f, thickness * 0.75f));
        if (query.MomentMean <= depth + discrepancyMargin)
        {
            return Unchanged(
                SimpleDdgiNearVisibilityDisposition.NoMomentDiscrepancy);
        }

        float receiverMargin = Math.Max(
            0.01f,
            Math.Min(spacing * 0.04f, thickness * 0.35f));
        float receiverDepthDelta = query.ReceiverDistance - depth;
        if (receiverDepthDelta <= receiverMargin)
        {
            return Unchanged(
                SimpleDdgiNearVisibilityDisposition.ReceiverInFront);
        }

        float transitionWidth = Math.Max(
            0.03f,
            Math.Min(spacing * 0.15f, Math.Max(thickness, 0.02f) * 1.5f));
        float coverage = SmoothStep(
            receiverMargin,
            receiverMargin + transitionWidth,
            receiverDepthDelta);
        float trust = SmoothStep(MinimumConfidence, FullConfidence, confidence);
        float conservativeVisibility = Lerp(
            1.0f,
            0.02f,
            coverage * trust);
        return new SimpleDdgiNearVisibilityEvaluation(
            momentVisibility,
            Math.Min(momentVisibility, conservativeVisibility),
            conservativeVisibility,
            trust,
            coverage,
            SimpleDdgiNearVisibilityDisposition.Applied);
    }

    public static float Chebyshev(
        float mean,
        float meanSecond,
        float receiverDistance,
        float probeSpacing)
    {
        if (receiverDistance <= mean)
            return 1.0f;
        float measuredVariance = Math.Max(meanSecond - mean * mean, 0.0f);
        float varianceSpacing = Math.Min(Math.Max(probeSpacing, 0.0f), 4.0f);
        float spacingFloor = Math.Max(
            0.0005f,
            varianceSpacing * varianceSpacing * 0.005f);
        float meanBound = Math.Max(0.0005f, mean * mean * 0.0625f);
        float variance = Math.Max(
            measuredVariance,
            Math.Min(spacingFloor, meanBound));
        float delta = receiverDistance - mean;
        return Math.Clamp(
            variance / (variance + delta * delta),
            0.0f,
            1.0f);
    }

    private static void Validate(in SimpleDdgiNearVisibilityQuery query)
    {
        EnsureFinite(query.MomentMean, nameof(query));
        EnsureFinite(query.MomentSecond, nameof(query));
        EnsureFinite(query.ReceiverDistance, nameof(query));
        EnsureFinite(query.ProbeSpacing, nameof(query));
        EnsureFinite(query.ArchitecturalThickness, nameof(query));
        EnsureFinite(query.Sidecar.ConservativeDepth, nameof(query));
        EnsureFinite(query.Sidecar.Confidence, nameof(query));
        if (query.MomentMean < 0.0f || query.MomentSecond < 0.0f ||
            query.ReceiverDistance < 0.0f || query.ProbeSpacing <= 0.0f ||
            query.ArchitecturalThickness <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(query));
    }

    private static void EnsureFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static float Lerp(float first, float second, float amount) =>
        first + (second - first) * amount;

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float t = Math.Clamp(
            (value - minimum) / Math.Max(maximum - minimum, float.Epsilon),
            0.0f,
            1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
