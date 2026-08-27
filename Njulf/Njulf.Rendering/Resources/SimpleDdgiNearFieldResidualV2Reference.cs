using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

public interface ISimpleDdgiNearFieldLinearDepthHierarchy
{
    int MaximumMipLevel { get; }

    /// <summary>Samples positive linear view depth in metres.</summary>
    bool TrySampleLinearDepth(Vector2 uv, int mipLevel, out float linearDepth);
}

public readonly record struct SimpleDdgiNearFieldViewTraceConfiguration(
    int MaximumTraceSteps,
    int MipZeroRefinementSteps,
    float NearPlaneMeters,
    float MaximumDistanceMeters,
    float ReceiverPixelFootprintMeters,
    float BiasFootprintScale,
    float ThicknessFootprintScale,
    float DepthDiscontinuityScale,
    Vector2 ViewportExtent)
{
    public float StartBiasMeters => MathF.Max(
        0.001f, ReceiverPixelFootprintMeters * BiasFootprintScale);

    public float ThicknessMeters => MathF.Max(
        0.02f, ReceiverPixelFootprintMeters * ThicknessFootprintScale);

    public void Validate()
    {
        if (MaximumTraceSteps is < 1 or > 256 ||
            MipZeroRefinementSteps is < 0 or > 16 ||
            !float.IsFinite(NearPlaneMeters) || NearPlaneMeters <= 0.0f ||
            !float.IsFinite(MaximumDistanceMeters) ||
            MaximumDistanceMeters is < 2.0f or > 16.0f ||
            !float.IsFinite(ReceiverPixelFootprintMeters) ||
            ReceiverPixelFootprintMeters < 0.0f ||
            !float.IsFinite(BiasFootprintScale) || BiasFootprintScale < 0.0f ||
            !float.IsFinite(ThicknessFootprintScale) ||
            ThicknessFootprintScale < 0.0f ||
            !float.IsFinite(DepthDiscontinuityScale) ||
            DepthDiscontinuityScale < 1.0f ||
            !float.IsFinite(ViewportExtent.X) ||
            !float.IsFinite(ViewportExtent.Y) ||
            ViewportExtent.X < 1.0f || ViewportExtent.Y < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTraceSteps));
        }
    }
}

/// <summary>
/// CPU oracle for V13's perspective-correct view-space Hi-Z DDA. Projection
/// storage convention is irrelevant because hierarchy samples are already
/// positive linear view depths.
/// </summary>
public static class SimpleDdgiNearFieldViewSpaceTraceReference
{
    public static SimpleDdgiNearFieldTraceResult Trace(
        ISimpleDdgiNearFieldLinearDepthHierarchy hierarchy,
        Vector3 receiverViewPosition,
        Vector3 viewDirection,
        Matrix4x4 projection,
        in SimpleDdgiNearFieldViewTraceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        configuration.Validate();
        if (!Finite(receiverViewPosition) || !Finite(viewDirection) ||
            !Finite(projection) || viewDirection.LengthSquared() <= 1.0e-12f)
        {
            return SimpleDdgiNearFieldTraceResult.Miss(0, 0, "non-finite-ray");
        }

        Vector3 direction = Vector3.Normalize(viewDirection);
        Vector3 start = receiverViewPosition +
            direction * configuration.StartBiasMeters;
        Vector3 end = receiverViewPosition +
            direction * configuration.MaximumDistanceMeters;
        if (!ClipToNearPlane(ref start, ref end, configuration.NearPlaneMeters))
            return SimpleDdgiNearFieldTraceResult.Miss(0, 0, "near-plane-clipped");

        Vector4 startClip = Vector4.Transform(new Vector4(start, 1.0f), projection);
        Vector4 endClip = Vector4.Transform(new Vector4(end, 1.0f), projection);
        if (!Finite(startClip) || !Finite(endClip) ||
            MathF.Abs(startClip.W) <= 1.0e-8f ||
            MathF.Abs(endClip.W) <= 1.0e-8f)
        {
            return SimpleDdgiNearFieldTraceResult.Miss(0, 0, "invalid-projection");
        }

        Vector2 startUv = NdcToUv(startClip);
        Vector2 endUv = NdcToUv(endClip);
        float enter = 0.0f;
        float exit = 1.0f;
        if (!ClipToViewport(startUv, endUv, ref enter, ref exit))
        {
            return SimpleDdgiNearFieldTraceResult.Miss(
                0, 0, "screen-exit");
        }

        Vector2 startPixel = startUv * configuration.ViewportExtent;
        Vector2 pixelDelta = (endUv - startUv) *
            configuration.ViewportExtent;
        float projectedPixels = MathF.Max(
            MathF.Abs(pixelDelta.X), MathF.Abs(pixelDelta.Y));
        float clippedPixels = projectedPixels * MathF.Max(exit - enter, 0.0f);
        if (!float.IsFinite(projectedPixels) || projectedPixels <= 1.0e-5f ||
            clippedPixels <= 1.0e-5f)
        {
            return SimpleDdgiNearFieldTraceResult.Miss(
                0, 0, "degenerate-projection");
        }

        int mip = Math.Clamp(
            (int)MathF.Floor(MathF.Log2(MathF.Max(
                clippedPixels / configuration.MaximumTraceSteps, 1.0f))),
            0,
            Math.Max(hierarchy.MaximumMipLevel, 0));
        float t = MathF.Min(
            exit,
            enter + MathF.Max(1.0f / projectedPixels, 1.0e-5f));
        float previousT = enter;
        int depthTests = 0;
        int refinements = 0;

        while (t <= exit && depthTests < configuration.MaximumTraceSteps)
        {
            Vector2 uv = Vector2.Lerp(startUv, endUv, t);
            if (!InsideViewport(uv))
                return SimpleDdgiNearFieldTraceResult.Miss(
                    depthTests, depthTests, "screen-exit");

            depthTests++;
            if (!hierarchy.TrySampleLinearDepth(uv, mip, out float sceneDepth) ||
                !float.IsFinite(sceneDepth) || sceneDepth <= 0.0f)
            {
                previousT = t;
                t = NextCellBoundaryParameter(
                    startPixel, pixelDelta, t, mip);
                continue;
            }

            Vector3 rayPosition = PerspectiveInterpolate(
                start, end, startClip.W, endClip.W, t);
            float rayDepth = -rayPosition.Z;
            float thickness = configuration.ThicknessMeters;
            if (rayDepth < sceneDepth - thickness)
            {
                previousT = t;
                if (mip < hierarchy.MaximumMipLevel)
                {
                    mip++;
                }
                t = NextCellBoundaryParameter(
                    startPixel, pixelDelta, t, mip);
                continue;
            }

            // A coarse minimum already behind the ray can still belong to a
            // foreground texel adjacent to a gap or depth step. Descend before
            // applying the upper thickness bound so the exact mip-zero texel
            // decides whether this is a valid crossing.
            if (mip > 0)
            {
                mip--;
                continue;
            }

            if (rayDepth > sceneDepth +
                thickness * configuration.DepthDiscontinuityScale)
            {
                previousT = t;
                t = NextCellBoundaryParameter(
                    startPixel, pixelDelta, t, mip: 0);
                continue;
            }

            float low = previousT;
            float high = t;
            float refinedSceneDepth = sceneDepth;
            int attemptedRefinements = 0;
            for (int refinement = 0;
                 refinement < configuration.MipZeroRefinementSteps &&
                 depthTests < configuration.MaximumTraceSteps;
                 refinement++)
            {
                attemptedRefinements++;
                refinements++;
                float mid = (low + high) * 0.5f;
                Vector2 midUv = Vector2.Lerp(startUv, endUv, mid);
                depthTests++;
                if (!InsideViewport(midUv) ||
                    !hierarchy.TrySampleLinearDepth(
                        midUv, 0, out float midSceneDepth) ||
                    !float.IsFinite(midSceneDepth) || midSceneDepth <= 0.0f)
                {
                    low = mid;
                    continue;
                }
                Vector3 midPosition = PerspectiveInterpolate(
                    start, end, startClip.W, endClip.W, mid);
                float midRayDepth = -midPosition.Z;
                if (midRayDepth >= midSceneDepth - thickness)
                {
                    high = mid;
                    refinedSceneDepth = midSceneDepth;
                }
                else
                {
                    low = mid;
                }
            }

            if (depthTests >= configuration.MaximumTraceSteps &&
                attemptedRefinements < configuration.MipZeroRefinementSteps)
            {
                return SimpleDdgiNearFieldTraceResult.Miss(
                    depthTests, depthTests, "step-limit");
            }

            Vector2 hitUv = Vector2.Lerp(startUv, endUv, high);
            Vector3 hitPosition = PerspectiveInterpolate(
                start, end, startClip.W, endClip.W, high);
            float hitDepth = -hitPosition.Z;
            if (!InsideViewport(hitUv) || hitDepth < refinedSceneDepth - thickness ||
                hitDepth > refinedSceneDepth +
                    thickness * configuration.DepthDiscontinuityScale)
            {
                previousT = t;
                continue;
            }
            return new SimpleDdgiNearFieldTraceResult(
                true, hitUv, hitDepth, refinedSceneDepth, depthTests,
                depthTests, refinements, "hit");
        }

        return SimpleDdgiNearFieldTraceResult.Miss(
            depthTests, depthTests,
            depthTests >= configuration.MaximumTraceSteps
                ? "step-limit"
                : "screen-segment-exhausted");
    }

    private static float NextCellBoundaryParameter(
        Vector2 startPixel,
        Vector2 pixelDelta,
        float currentParameter,
        int mip)
    {
        float cellSize = MathF.Pow(2.0f, mip);
        Vector2 currentPixel = startPixel + pixelDelta * currentParameter;
        float next = 2.0f;
        next = NextAxisBoundary(
            currentPixel.X, pixelDelta.X, cellSize, currentParameter, next);
        next = NextAxisBoundary(
            currentPixel.Y, pixelDelta.Y, cellSize, currentParameter, next);
        float traversalEpsilon = MathF.Max(
            1.0e-6f,
            1.0e-4f / MathF.Max(
                MathF.Max(MathF.Abs(pixelDelta.X), MathF.Abs(pixelDelta.Y)),
                1.0f));
        return next + traversalEpsilon;
    }

    private static float NextAxisBoundary(
        float currentPixel,
        float direction,
        float cellSize,
        float currentParameter,
        float currentMinimum)
    {
        if (MathF.Abs(direction) <= 1.0e-7f)
            return currentMinimum;
        float cell = MathF.Floor(currentPixel / cellSize);
        float boundary = direction > 0.0f
            ? (cell + 1.0f) * cellSize
            : cell * cellSize;
        float candidate = currentParameter +
            (boundary - currentPixel) / direction;
        if (candidate <= currentParameter + 1.0e-7f)
        {
            boundary += MathF.CopySign(cellSize, direction);
            candidate = currentParameter +
                (boundary - currentPixel) / direction;
        }
        return candidate > currentParameter
            ? MathF.Min(currentMinimum, candidate)
            : currentMinimum;
    }

    private static bool ClipToViewport(
        Vector2 start,
        Vector2 end,
        ref float enter,
        ref float exit)
    {
        Vector2 delta = end - start;
        return ClipAxis(start.X, delta.X, ref enter, ref exit) &&
            ClipAxis(start.Y, delta.Y, ref enter, ref exit) &&
            enter <= exit;
    }

    private static bool ClipAxis(
        float start,
        float delta,
        ref float enter,
        ref float exit)
    {
        if (MathF.Abs(delta) <= 1.0e-8f)
            return start >= 0.0f && start <= 1.0f;
        float first = (0.0f - start) / delta;
        float second = (1.0f - start) / delta;
        if (first > second)
            (first, second) = (second, first);
        enter = MathF.Max(enter, first);
        exit = MathF.Min(exit, second);
        return enter <= exit && exit >= 0.0f && enter <= 1.0f;
    }

    private static bool ClipToNearPlane(
        ref Vector3 start,
        ref Vector3 end,
        float nearPlane)
    {
        float startDepth = -start.Z;
        float endDepth = -end.Z;
        if (startDepth < nearPlane && endDepth < nearPlane)
            return false;
        if (startDepth < nearPlane)
        {
            float t = (nearPlane - startDepth) / (endDepth - startDepth);
            start = Vector3.Lerp(start, end, Math.Clamp(t, 0.0f, 1.0f));
        }
        else if (endDepth < nearPlane)
        {
            float t = (nearPlane - startDepth) / (endDepth - startDepth);
            end = Vector3.Lerp(start, end, Math.Clamp(t, 0.0f, 1.0f));
        }
        return -start.Z >= nearPlane && -end.Z >= nearPlane;
    }

    private static Vector3 PerspectiveInterpolate(
        Vector3 start,
        Vector3 end,
        float startW,
        float endW,
        float t)
    {
        float startReciprocalW = 1.0f / startW;
        float endReciprocalW = 1.0f / endW;
        Vector3 numerator = Vector3.Lerp(
            start * startReciprocalW,
            end * endReciprocalW,
            t);
        float reciprocalW = startReciprocalW +
            (endReciprocalW - startReciprocalW) * t;
        return numerator / reciprocalW;
    }

    private static Vector2 NdcToUv(Vector4 clip) =>
        new(clip.X / clip.W * 0.5f + 0.5f,
            clip.Y / clip.W * 0.5f + 0.5f);

    private static bool InsideViewport(Vector2 uv) =>
        uv.X >= 0.0f && uv.X < 1.0f && uv.Y >= 0.0f && uv.Y < 1.0f;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

public readonly record struct SimpleDdgiNearFieldSampleKey(
    uint SequenceIndex,
    uint RayOrdinal,
    uint StableSurfaceIdentity,
    uint PixelX,
    uint PixelY);

public static class SimpleDdgiNearFieldSamplingReference
{
    // Fixed 8x8 rank permutation. It is spatial only; neither wall-clock time
    // nor TAA jitter is part of the sample identity.
    private static ReadOnlySpan<byte> BlueNoiseRanks =>
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21
    ];

    public static Vector2 OwenSobol2D(in SimpleDdgiNearFieldSampleKey key)
    {
        uint rank = BlueNoiseRanks[
            (int)((key.PixelY & 7u) * 8u + (key.PixelX & 7u))];
        uint index = key.SequenceIndex * 4u + key.RayOrdinal + rank;
        uint seed = Hash(key.StableSurfaceIdentity ^
            (key.PixelX * 0x9e37_79b9u) ^
            (key.PixelY * 0x85eb_ca6bu) ^
            (key.RayOrdinal * 0xc2b2_ae35u));
        uint x = OwenScramble(ReverseBits(index), seed);
        uint y = OwenScramble(SobolDimensionOne(index), Hash(seed ^ 0x68bc_21ebu));
        const float inverseUint = 1.0f / 4_294_967_296.0f;
        return new Vector2((x + 0.5f) * inverseUint,
            (y + 0.5f) * inverseUint);
    }

    public static Vector3 CosineHemisphere(Vector2 sample)
    {
        float radius = MathF.Sqrt(Math.Clamp(sample.X, 0.0f, 1.0f));
        float phi = 2.0f * MathF.PI * Math.Clamp(sample.Y, 0.0f, 1.0f);
        return new Vector3(radius * MathF.Cos(phi), radius * MathF.Sin(phi),
            MathF.Sqrt(MathF.Max(0.0f, 1.0f - sample.X)));
    }

    public static float GuidedTexelToSolidAnglePdf(
        float texelProbability,
        float targetDistanceSquared,
        float targetCosine,
        float projectedPixelArea)
    {
        if (!float.IsFinite(texelProbability) || texelProbability <= 0.0f ||
            !float.IsFinite(targetDistanceSquared) || targetDistanceSquared <= 0.0f ||
            !float.IsFinite(targetCosine) || targetCosine <= 0.0f ||
            !float.IsFinite(projectedPixelArea) || projectedPixelArea <= 0.0f)
        {
            return 0.0f;
        }
        return texelProbability * targetDistanceSquared /
            (targetCosine * projectedPixelArea);
    }

    public static float MixturePdf(float cosinePdf, float guidedPdf,
        float guidedWeight)
    {
        if (!float.IsFinite(cosinePdf) || !float.IsFinite(guidedPdf) ||
            !float.IsFinite(guidedWeight))
            return 0.0f;
        float weight = Math.Clamp(guidedWeight, 0.0f, 1.0f);
        return MathF.Max((1.0f - weight) * MathF.Max(cosinePdf, 0.0f) +
            weight * MathF.Max(guidedPdf, 0.0f), 0.0f);
    }

    public static (Vector3 Mean, float Coverage) AggregateLaunchedRays(
        ReadOnlySpan<Vector3> contributions,
        ReadOnlySpan<bool> validHits)
    {
        if (contributions.Length == 0 || contributions.Length != validHits.Length)
            return (Vector3.Zero, 0.0f);
        Vector3 sum = Vector3.Zero;
        int valid = 0;
        for (int i = 0; i < contributions.Length; i++)
        {
            if (!validHits[i])
                continue;
            if (!float.IsFinite(contributions[i].X) ||
                !float.IsFinite(contributions[i].Y) ||
                !float.IsFinite(contributions[i].Z))
                continue;
            sum += contributions[i];
            valid++;
        }
        // Misses remain zero by dividing by every launched ray.
        return (sum / contributions.Length,
            valid / (float)contributions.Length);
    }

    private static uint SobolDimensionOne(uint index)
    {
        uint result = 0u;
        uint direction = 1u << 31;
        for (uint value = index; value != 0u; value >>= 1)
        {
            if ((value & 1u) != 0u)
                result ^= direction;
            direction ^= direction >> 1;
        }
        return result;
    }

    private static uint OwenScramble(uint value, uint seed)
    {
        value ^= value * 0x3d20_adeau;
        value += seed;
        value *= (seed >> 16) | 1u;
        value ^= value * 0x0552_6c56u;
        value ^= value * 0x53a2_2864u;
        return value;
    }

    private static uint ReverseBits(uint value)
    {
        value = (value >> 16) | (value << 16);
        value = ((value & 0x00ff_00ffu) << 8) |
            ((value & 0xff00_ff00u) >> 8);
        value = ((value & 0x0f0f_0f0fu) << 4) |
            ((value & 0xf0f0_f0f0u) >> 4);
        value = ((value & 0x3333_3333u) << 2) |
            ((value & 0xcccc_ccccu) >> 2);
        return ((value & 0x5555_5555u) << 1) |
            ((value & 0xaaaa_aaaau) >> 1);
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb_352du;
        value ^= value >> 15;
        value *= 0x846c_a68bu;
        return value ^ (value >> 16);
    }
}
