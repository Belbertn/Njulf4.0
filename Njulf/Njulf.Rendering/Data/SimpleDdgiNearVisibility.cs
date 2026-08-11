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
        float probeSpacing)
    {
        EnsureFinite(previous.ConservativeDepth, nameof(previous));
        EnsureFinite(previous.Confidence, nameof(previous));
        EnsureFinite(current.ConservativeDepth, nameof(current));
        EnsureFinite(current.Confidence, nameof(current));
        EnsureFinite(texelHysteresis, nameof(texelHysteresis));
        EnsureFinite(probeSpacing, nameof(probeSpacing));

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
        float spacing = Math.Max(probeSpacing, 0.0f);
        if (currentConfidence < MinimumConfidence)
            hysteresis = Math.Min(hysteresis, 0.20f);
        else if (previousConfidence >= MinimumConfidence &&
            currentDepth > previousDepth + spacing * 0.10f)
            hysteresis = Math.Min(hysteresis, 0.25f);
        else if (previousConfidence >= MinimumConfidence &&
            currentDepth < previousDepth - spacing * 0.10f)
            hysteresis = Math.Min(hysteresis, 0.50f);

        return new SimpleDdgiNearVisibilitySample(
            Lerp(currentDepth, previousDepth, hysteresis),
            Math.Clamp(
                Lerp(currentConfidence, previousConfidence, hysteresis),
                0.0f,
                1.0f));
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
