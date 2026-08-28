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
/// One packed B4 sidecar texel containing the two nearest independently
/// coherent hit layers. Each depth/confidence pair occupies one RG16F word;
/// the second layer prevents a foreground sliver from erasing reliable wall
/// evidence behind it without ever averaging the two depths.
/// </summary>
public readonly record struct SimpleDdgiNearVisibilitySample(
    float ConservativeDepth,
    float Confidence)
{
    public const float MaximumHalf = 65_504.0f;

    public float SecondaryDepth { get; init; } = MaximumHalf;
    public float SecondaryConfidence { get; init; }

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
    public const int LegacyBytesPerTexel = sizeof(uint);
    public const int BytesPerTexel = 2 * sizeof(uint);
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
        float primarySeed = SimpleDdgiNearVisibilitySample.MaximumHalf;
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
            {
                primarySeed = Math.Min(
                    primarySeed,
                    Math.Max(ray.Distance, 0.0f));
            }
        }

        if (primarySeed >=
                SimpleDdgiNearVisibilitySample.MaximumHalf - 1.0f ||
            allNarrowWeight <= 1.0e-6)
        {
            return SimpleDdgiNearVisibilitySample.Empty;
        }

        float band = CoherentDepthBand(probeSpacing, architecturalThickness);
        float secondarySeed = SimpleDdgiNearVisibilitySample.MaximumHalf;
        double primaryWeight = 0.0;
        double primaryDepth = 0.0;
        for (int index = 0; index < rays.Length; index++)
        {
            SimpleDdgiNearVisibilityRay ray = rays[index];
            float depth = Math.Max(ray.Distance, 0.0f);
            if (!ray.Hit)
                continue;

            float broadWeight = MathF.Pow(
                Math.Clamp(ray.Cosine, 0.0f, 1.0f),
                16.0f);
            float narrowWeight = broadWeight * broadWeight;
            if (Math.Abs(depth - primarySeed) <= band)
            {
                primaryWeight += narrowWeight;
                primaryDepth += depth * narrowWeight;
            }
            else if (depth > primarySeed + band &&
                narrowWeight >= MinimumQualifyingNarrowWeight)
            {
                secondarySeed = Math.Min(secondarySeed, depth);
            }
        }

        if (primaryWeight <= 1.0e-6)
            return SimpleDdgiNearVisibilitySample.Empty;

        double secondaryWeight = 0.0;
        double secondaryDepth = 0.0;
        if (secondarySeed <
            SimpleDdgiNearVisibilitySample.MaximumHalf - 1.0f)
        {
            for (int index = 0; index < rays.Length; index++)
            {
                SimpleDdgiNearVisibilityRay ray = rays[index];
                float depth = Math.Max(ray.Distance, 0.0f);
                if (!ray.Hit ||
                    Math.Abs(depth - primarySeed) <= band ||
                    Math.Abs(depth - secondarySeed) > band)
                {
                    continue;
                }

                float broadWeight = MathF.Pow(
                    Math.Clamp(ray.Cosine, 0.0f, 1.0f),
                    16.0f);
                float narrowWeight = broadWeight * broadWeight;
                secondaryWeight += narrowWeight;
                secondaryDepth += depth * narrowWeight;
            }
        }

        return new SimpleDdgiNearVisibilitySample(
            (float)(primaryDepth / primaryWeight),
            Math.Clamp(
                (float)(primaryWeight / allNarrowWeight),
                0.0f,
                1.0f))
        {
            SecondaryDepth = secondaryWeight > 1.0e-6
                ? (float)(secondaryDepth / secondaryWeight)
                : SimpleDdgiNearVisibilitySample.MaximumHalf,
            SecondaryConfidence = secondaryWeight > 1.0e-6
                ? Math.Clamp(
                    (float)(secondaryWeight / allNarrowWeight),
                    0.0f,
                    1.0f)
                : 0.0f
        };
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

    public static ulong PackV2(SimpleDdgiNearVisibilitySample sample)
    {
        EnsureFinite(sample.SecondaryDepth, nameof(sample));
        EnsureFinite(sample.SecondaryConfidence, nameof(sample));
        uint primary = Pack(sample);
        uint secondary = Pack(new SimpleDdgiNearVisibilitySample(
            sample.SecondaryDepth,
            sample.SecondaryConfidence));
        return primary | ((ulong)secondary << 32);
    }

    public static SimpleDdgiNearVisibilitySample UnpackV2(ulong packed)
    {
        SimpleDdgiNearVisibilitySample primary = Unpack(
            unchecked((uint)packed));
        SimpleDdgiNearVisibilitySample secondary = Unpack(
            unchecked((uint)(packed >> 32)));
        return primary with
        {
            SecondaryDepth = secondary.ConservativeDepth,
            SecondaryConfidence = secondary.Confidence
        };
    }

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
        EnsureFinite(previous.SecondaryDepth, nameof(previous));
        EnsureFinite(previous.SecondaryConfidence, nameof(previous));
        EnsureFinite(current.ConservativeDepth, nameof(current));
        EnsureFinite(current.Confidence, nameof(current));
        EnsureFinite(current.SecondaryDepth, nameof(current));
        EnsureFinite(current.SecondaryConfidence, nameof(current));
        EnsureFinite(texelHysteresis, nameof(texelHysteresis));
        EnsureFinite(probeSpacing, nameof(probeSpacing));
        EnsureFinite(architecturalThickness, nameof(architecturalThickness));

        NearLayer previousPrimary = NormalizeLayer(
            previous.ConservativeDepth,
            previous.Confidence);
        NearLayer previousSecondary = NormalizeLayer(
            previous.SecondaryDepth,
            previous.SecondaryConfidence);
        SortActiveLayers(ref previousPrimary, ref previousSecondary);

        NearLayer currentPrimary = NormalizeLayer(
            current.ConservativeDepth,
            current.Confidence);
        NearLayer currentSecondary = NormalizeLayer(
            current.SecondaryDepth,
            current.SecondaryConfidence);
        SortActiveLayers(ref currentPrimary, ref currentSecondary);

        // A no-hit refresh releases confidence immediately, but retaining the
        // previous finite depth avoids half-float infinity entering arithmetic
        // and preserves the original one-layer ABI behavior.
        if (currentPrimary.Confidence <= 0.0f &&
            previousPrimary.Confidence > 0.0f)
        {
            currentPrimary = currentPrimary with
            {
                Depth = previousPrimary.Depth
            };
        }
        if (currentSecondary.Confidence <= 0.0f &&
            previousSecondary.Confidence > 0.0f)
        {
            currentSecondary = currentSecondary with
            {
                Depth = previousSecondary.Depth
            };
        }

        float baseHysteresis = historyValid && !freshUpdate
            ? Math.Min(Math.Clamp(texelHysteresis, 0.0f, 1.0f), 0.85f)
            : 0.0f;
        float depthBand = CoherentDepthBand(
            Math.Max(probeSpacing, 0.001f),
            Math.Max(architecturalThickness, 0.008f));

        bool previousPrimaryUsed = false;
        bool previousSecondaryUsed = false;
        int primaryMatch = ResolveNearestPreviousLayer(
            currentPrimary,
            previousPrimary,
            previousSecondary,
            previousPrimaryUsed,
            previousSecondaryUsed,
            depthBand);
        if (primaryMatch == 0)
            previousPrimaryUsed = true;
        else if (primaryMatch == 1)
            previousSecondaryUsed = true;
        NearLayer blendedPrimary = BlendLayer(
            currentPrimary,
            primaryMatch == 0
                ? previousPrimary
                : primaryMatch == 1
                    ? previousSecondary
                    : default,
            primaryMatch >= 0 ? baseHysteresis : 0.0f,
            depthBand);

        int secondaryMatch = ResolveNearestPreviousLayer(
            currentSecondary,
            previousPrimary,
            previousSecondary,
            previousPrimaryUsed,
            previousSecondaryUsed,
            depthBand);
        NearLayer blendedSecondary = BlendLayer(
            currentSecondary,
            secondaryMatch == 0
                ? previousPrimary
                : secondaryMatch == 1
                    ? previousSecondary
                    : default,
            secondaryMatch >= 0 ? baseHysteresis : 0.0f,
            depthBand);
        SortActiveLayers(ref blendedPrimary, ref blendedSecondary);

        return new SimpleDdgiNearVisibilitySample(
            blendedPrimary.Depth,
            blendedPrimary.Confidence)
        {
            SecondaryDepth = blendedSecondary.Depth,
            SecondaryConfidence = blendedSecondary.Confidence
        };
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

        float discrepancyMargin = Math.Max(
            0.02f,
            Math.Min(spacing * 0.12f, thickness * 0.75f));
        float receiverMargin = Math.Max(
            0.01f,
            Math.Min(spacing * 0.04f, thickness * 0.35f));
        float transitionWidth = Math.Max(
            0.03f,
            Math.Min(spacing * 0.15f, Math.Max(thickness, 0.02f) * 1.5f));

        NearLayerEvaluation primary = EvaluateLayer(
            query.Sidecar.ConservativeDepth,
            query.Sidecar.Confidence,
            query.MomentMean,
            query.ReceiverDistance,
            discrepancyMargin,
            receiverMargin,
            transitionWidth);
        bool secondaryPresent = query.Sidecar.SecondaryConfidence > 0.0f;
        NearLayerEvaluation secondary = secondaryPresent
            ? EvaluateLayer(
                query.Sidecar.SecondaryDepth,
                query.Sidecar.SecondaryConfidence,
                query.MomentMean,
                query.ReceiverDistance,
                discrepancyMargin,
                receiverMargin,
                transitionWidth)
            : default;
        if (!primary.Applied && !secondary.Applied)
        {
            return Unchanged(
                secondaryPresent &&
                primary.Disposition is
                    SimpleDdgiNearVisibilityDisposition.InsufficientConfidence or
                    SimpleDdgiNearVisibilityDisposition.InvalidDepth
                    ? secondary.Disposition
                    : primary.Disposition);
        }

        NearLayerEvaluation selected = !primary.Applied
            ? secondary
            : !secondary.Applied ||
                primary.ConservativeVisibility <=
                    secondary.ConservativeVisibility
                ? primary
                : secondary;
        float conservativeVisibility = selected.ConservativeVisibility;
        return new SimpleDdgiNearVisibilityEvaluation(
            momentVisibility,
            Math.Min(momentVisibility, conservativeVisibility),
            conservativeVisibility,
            selected.Trust,
            selected.Coverage,
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

    private static NearLayerEvaluation EvaluateLayer(
        float depth,
        float confidence,
        float momentMean,
        float receiverDistance,
        float discrepancyMargin,
        float receiverMargin,
        float transitionWidth)
    {
        confidence = Math.Clamp(confidence, 0.0f, 1.0f);
        if (confidence < MinimumConfidence)
        {
            return new NearLayerEvaluation(
                1.0f,
                0.0f,
                0.0f,
                SimpleDdgiNearVisibilityDisposition.InsufficientConfidence,
                false);
        }
        if (depth <= 0.0f ||
            depth >= SimpleDdgiNearVisibilitySample.MaximumHalf - 1.0f)
        {
            return new NearLayerEvaluation(
                1.0f,
                0.0f,
                0.0f,
                SimpleDdgiNearVisibilityDisposition.InvalidDepth,
                false);
        }
        if (momentMean <= depth + discrepancyMargin)
        {
            return new NearLayerEvaluation(
                1.0f,
                0.0f,
                0.0f,
                SimpleDdgiNearVisibilityDisposition.NoMomentDiscrepancy,
                false);
        }

        float receiverDepthDelta = receiverDistance - depth;
        if (receiverDepthDelta <= receiverMargin)
        {
            return new NearLayerEvaluation(
                1.0f,
                0.0f,
                0.0f,
                SimpleDdgiNearVisibilityDisposition.ReceiverInFront,
                false);
        }

        float coverage = SmoothStep(
            receiverMargin,
            receiverMargin + transitionWidth,
            receiverDepthDelta);
        float trust = SmoothStep(
            MinimumConfidence,
            FullConfidence,
            confidence);
        return new NearLayerEvaluation(
            Lerp(1.0f, 0.02f, coverage * trust),
            trust,
            coverage,
            SimpleDdgiNearVisibilityDisposition.Applied,
            true);
    }

    private static NearLayer NormalizeLayer(float depth, float confidence) =>
        new(
            Math.Clamp(
                depth,
                0.0f,
                SimpleDdgiNearVisibilitySample.MaximumHalf),
            Math.Clamp(confidence, 0.0f, 1.0f));

    private static void SortActiveLayers(
        ref NearLayer primary,
        ref NearLayer secondary)
    {
        if (secondary.Confidence <= 0.0f ||
            (primary.Confidence > 0.0f && primary.Depth <= secondary.Depth))
        {
            return;
        }

        (primary, secondary) = (secondary, primary);
    }

    private static int ResolveNearestPreviousLayer(
        NearLayer current,
        NearLayer previousPrimary,
        NearLayer previousSecondary,
        bool previousPrimaryUsed,
        bool previousSecondaryUsed,
        float depthBand)
    {
        if (current.Confidence < MinimumConfidence)
            return -1;

        float primaryDistance = !previousPrimaryUsed &&
            previousPrimary.Confidence >= MinimumConfidence
                ? Math.Abs(current.Depth - previousPrimary.Depth)
                : float.PositiveInfinity;
        float secondaryDistance = !previousSecondaryUsed &&
            previousSecondary.Confidence >= MinimumConfidence
                ? Math.Abs(current.Depth - previousSecondary.Depth)
                : float.PositiveInfinity;
        float nearestDistance = Math.Min(primaryDistance, secondaryDistance);
        if (nearestDistance > depthBand)
            return -1;
        return primaryDistance <= secondaryDistance ? 0 : 1;
    }

    private static NearLayer BlendLayer(
        NearLayer current,
        NearLayer previous,
        float hysteresis,
        float depthBand)
    {
        if (current.Confidence < MinimumConfidence ||
            previous.Confidence < MinimumConfidence ||
            Math.Abs(current.Depth - previous.Depth) > depthBand)
        {
            hysteresis = 0.0f;
        }

        return new NearLayer(
            Lerp(current.Depth, previous.Depth, hysteresis),
            Math.Clamp(
                Lerp(current.Confidence, previous.Confidence, hysteresis),
                0.0f,
                1.0f));
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
        EnsureFinite(query.Sidecar.SecondaryDepth, nameof(query));
        EnsureFinite(query.Sidecar.SecondaryConfidence, nameof(query));
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

    private readonly record struct NearLayer(float Depth, float Confidence);

    private readonly record struct NearLayerEvaluation(
        float ConservativeVisibility,
        float Trust,
        float Coverage,
        SimpleDdgiNearVisibilityDisposition Disposition,
        bool Applied);
}
