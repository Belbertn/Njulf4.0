using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// CPU oracle for the content-dependent ray-scene policies implemented by
/// <c>ddgi_hit_shading.glsl</c> and <c>ddgi_simple_trace.comp</c>. These methods
/// are intentionally allocation-free on their span-based hot paths so they can
/// also be used by capture validation and offline qualification tools.
/// </summary>
public static class DdgiGeometryParticipation
{
    public const int ProductionVisibilityLayerLimit = 8;
    public const int ProductionDecalCandidateLimit = 8;
    public const float ProductionVisibilityTermination = 0.01f;
    public const float ProductionDecalFacingCosine = 0.1f;

    public static float ComposeCoverageAlpha(
        float baseColorAlpha,
        float vertexAlpha,
        float sampledTextureAlpha)
    {
        ValidateUnitFinite(baseColorAlpha, nameof(baseColorAlpha));
        ValidateUnitFinite(vertexAlpha, nameof(vertexAlpha));
        ValidateUnitFinite(sampledTextureAlpha, nameof(sampledTextureAlpha));
        return Math.Clamp(
            baseColorAlpha * vertexAlpha * sampledTextureAlpha,
            0f,
            1f);
    }

    public static bool AcceptStableStochasticCoverage(
        float effectiveCoverageAlpha,
        DdgiStochasticIdentity identity)
    {
        ValidateUnitFinite(
            effectiveCoverageAlpha,
            nameof(effectiveCoverageAlpha));
        return identity.WithDomain(DdgiStochasticDecisionDomain.AlphaCoverage)
            .UnitFloat() < effectiveCoverageAlpha;
    }

    /// <summary>
    /// Composes ordinary alpha and physical thin layers deterministically in
    /// front-to-back traversal order. Hitting the hard layer cap is fail-closed
    /// and returns zero throughput, matching the shader emergency policy.
    /// </summary>
    public static DdgiVisibilityComposition ComposeVisibility(
        ReadOnlySpan<DdgiVisibilityLayer> layers,
        int layerLimit = ProductionVisibilityLayerLimit,
        float terminationThreshold = ProductionVisibilityTermination)
    {
        if (layerLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(layerLimit));
        if (!float.IsFinite(terminationThreshold) ||
            terminationThreshold < 0f || terminationThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationThreshold));
        }

        Vector3 throughput = Vector3.One;
        int composed = 0;
        for (int index = 0; index < layers.Length; index++)
        {
            DdgiVisibilityLayer layer = layers[index];
            ValidateUnitFinite(layer.CoverageAlpha, nameof(layer.CoverageAlpha));
            ValidateUnitFinite(layer.ThinTransmission.X, nameof(layer.ThinTransmission));
            ValidateUnitFinite(layer.ThinTransmission.Y, nameof(layer.ThinTransmission));
            ValidateUnitFinite(layer.ThinTransmission.Z, nameof(layer.ThinTransmission));

            Vector3 layerThroughput = layer.Kind switch
            {
                DdgiVisibilityLayerKind.OrdinaryAlphaBlend =>
                    new Vector3(1f - layer.CoverageAlpha),
                DdgiVisibilityLayerKind.ThinTransmission => Vector3.Lerp(
                    Vector3.One,
                    layer.ThinTransmission,
                    layer.CoverageAlpha),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(layers),
                    $"Unsupported visibility layer kind {layer.Kind}.")
            };
            throughput *= layerThroughput;
            composed++;

            if (composed >= layerLimit && index + 1 < layers.Length)
            {
                return new DdgiVisibilityComposition(
                    Vector3.Zero,
                    composed,
                    ReachedLayerLimit: true,
                    TerminatedForLowThroughput: false);
            }

            if (MaxComponent(throughput) < terminationThreshold)
            {
                return new DdgiVisibilityComposition(
                    throughput,
                    composed,
                    ReachedLayerLimit: false,
                    TerminatedForLowThroughput: true);
            }
        }

        return new DdgiVisibilityComposition(
            throughput,
            composed,
            ReachedLayerLimit: false,
            TerminatedForLowThroughput: false);
    }

    /// <summary>
    /// Retains the nearest bounded candidate set, associates it with the base
    /// hit, imposes stable layer/object ordering, and composites every accepted
    /// material overlay. The input is never mutated.
    /// </summary>
    public static DdgiDecalComposition ComposeDecals(
        DdgiReferenceSurface baseSurface,
        float baseHitDistance,
        ReadOnlySpan<DdgiDecalCandidate> candidates,
        int candidateLimit = ProductionDecalCandidateLimit,
        float facingCosine = ProductionDecalFacingCosine) =>
        ComposeDecals(
            baseSurface,
            baseHitDistance,
            SafeNormal(
                baseSurface.Sanitized().CanonicalGeometricNormal,
                Vector3.UnitY),
            candidates,
            candidateLimit,
            facingCosine);

    /// <summary>
    /// Ray-aware decal association. Authored decal bias and tolerance are
    /// normal-space distances, so ray-hit separation is projected onto the
    /// candidate's canonical geometric normal before comparison.
    /// </summary>
    public static DdgiDecalComposition ComposeDecals(
        DdgiReferenceSurface baseSurface,
        float baseHitDistance,
        Vector3 rayDirection,
        ReadOnlySpan<DdgiDecalCandidate> candidates,
        int candidateLimit = ProductionDecalCandidateLimit,
        float facingCosine = ProductionDecalFacingCosine)
    {
        if (!float.IsFinite(baseHitDistance) || baseHitDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(baseHitDistance));
        if (!float.IsFinite(rayDirection.X) ||
            !float.IsFinite(rayDirection.Y) ||
            !float.IsFinite(rayDirection.Z) ||
            rayDirection.LengthSquared() <= 1e-12f)
        {
            throw new ArgumentOutOfRangeException(nameof(rayDirection));
        }
        rayDirection = rayDirection.Normalized();
        if (candidateLimit < 0 || candidateLimit > 64)
            throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (!float.IsFinite(facingCosine) || facingCosine < -1f || facingCosine > 1f)
            throw new ArgumentOutOfRangeException(nameof(facingCosine));

        if (candidateLimit == 0 || candidates.IsEmpty)
        {
            return new DdgiDecalComposition(
                baseSurface,
                RetainedCount: 0,
                AssociatedCount: 0,
                DepthRejectedCount: 0,
                FacingRejectedCount: 0,
                OverflowCount: candidates.Length);
        }

        var retained = new List<DdgiDecalCandidate>(
            Math.Min(candidateLimit, candidates.Length));
        int overflow = 0;
        for (int index = 0; index < candidates.Length; index++)
        {
            DdgiDecalCandidate candidate = ValidateCandidate(candidates[index]);
            if (retained.Count < candidateLimit)
            {
                retained.Add(candidate);
                continue;
            }

            overflow++;
            int farthest = 0;
            for (int retainedIndex = 1;
                 retainedIndex < retained.Count;
                 retainedIndex++)
            {
                if (CompareNearest(
                        retained[retainedIndex],
                        retained[farthest]) > 0)
                {
                    farthest = retainedIndex;
                }
            }
            if (CompareNearest(candidate, retained[farthest]) < 0)
                retained[farthest] = candidate;
        }

        retained.Sort(CompareOverlayOrder);
        DdgiReferenceSurface composed = baseSurface.Sanitized();
        Vector3 baseNormal = SafeNormal(
            composed.CanonicalGeometricNormal,
            Vector3.UnitY);
        int associated = 0;
        int depthRejected = 0;
        int facingRejected = 0;
        for (int index = 0; index < retained.Count; index++)
        {
            DdgiDecalCandidate candidate = retained[index];
            Vector3 decalNormal = SafeNormal(
                candidate.Surface.CanonicalGeometricNormal,
                baseNormal);
            float projectedDepthSeparation =
                (baseHitDistance - candidate.HitDistance) *
                MathF.Abs(Vector3.Dot(rayDirection, decalNormal));
            if (MathF.Abs(
                    projectedDepthSeparation - Math.Max(candidate.DepthBias, 0f)) >
                candidate.DepthTolerance)
            {
                depthRejected++;
                continue;
            }

            if (Vector3.Dot(baseNormal, decalNormal) <= facingCosine)
            {
                facingRejected++;
                continue;
            }

            composed = ApplyDecalOverlay(
                composed,
                candidate.Surface,
                candidate.PremultipliedAlpha);
            associated++;
        }

        return new DdgiDecalComposition(
            composed,
            retained.Count,
            associated,
            depthRejected,
            facingRejected,
            overflow);
    }

    public static BoundingBox CreateSweptInfluenceBounds(
        BoundingBox previous,
        BoundingBox current,
        float influenceRadius)
    {
        if (!float.IsFinite(influenceRadius) || influenceRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(influenceRadius));
        Vector3 expansion = new(influenceRadius);
        return new BoundingBox(
            Vector3.Min(previous.Min, current.Min) - expansion,
            Vector3.Max(previous.Max, current.Max) + expansion);
    }

    private static DdgiReferenceSurface ApplyDecalOverlay(
        DdgiReferenceSurface baseSurface,
        DdgiReferenceSurface decalSurface,
        bool premultipliedAlpha)
    {
        baseSurface = baseSurface.Sanitized();
        decalSurface = decalSurface.Sanitized();
        float alpha = decalSurface.Opacity;
        if (alpha <= 0f)
            return baseSurface;

        // The material evaluator exposes unassociated lobe values in both
        // blend modes. Premultiplication is retained in the ABI for raster
        // parity and future texture payloads, while this oracle performs the
        // bounded over operation on those resolved values.
        _ = premultipliedAlpha;
        return baseSurface with
        {
            DiffuseReflectance = Clamp01(Vector3.Lerp(
                baseSurface.DiffuseReflectance,
                decalSurface.DiffuseReflectance,
                alpha)),
            DirectionalDiffuseBase = Clamp01(Vector3.Lerp(
                baseSurface.DirectionalDiffuseBase,
                decalSurface.DirectionalDiffuseBase,
                alpha)),
            DielectricF0 = Clamp01(Vector3.Lerp(
                baseSurface.DielectricF0,
                decalSurface.DielectricF0,
                alpha)),
            SpecularF0 = Clamp01(Vector3.Lerp(
                baseSurface.SpecularF0,
                decalSurface.SpecularF0,
                alpha)),
            TransmittedDiffuseReflectance = Clamp01(Vector3.Lerp(
                baseSurface.TransmittedDiffuseReflectance,
                decalSurface.TransmittedDiffuseReflectance,
                alpha)),
            EmissiveRadiance = ClampRadiance(Vector3.Lerp(
                baseSurface.EmissiveRadiance,
                decalSurface.EmissiveRadiance,
                alpha)),
            ShadingNormal = CorrectShadingNormal(
                baseSurface.GeometricNormal,
                Vector3.Lerp(
                    baseSurface.ShadingNormal,
                    decalSurface.ShadingNormal,
                    alpha)),
            MaterialOcclusion = Lerp(
                baseSurface.MaterialOcclusion,
                decalSurface.MaterialOcclusion,
                alpha),
            Metallic = Lerp(
                baseSurface.Metallic,
                decalSurface.Metallic,
                alpha),
            Roughness = Math.Clamp(
                Lerp(baseSurface.Roughness, decalSurface.Roughness, alpha),
                0.04f,
                1f)
        };
    }

    private static DdgiDecalCandidate ValidateCandidate(
        DdgiDecalCandidate candidate)
    {
        if (!float.IsFinite(candidate.HitDistance) || candidate.HitDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(candidate.HitDistance));
        if (!float.IsFinite(candidate.DepthTolerance) || candidate.DepthTolerance < 0f)
            throw new ArgumentOutOfRangeException(nameof(candidate.DepthTolerance));
        if (!float.IsFinite(candidate.DepthBias))
            throw new ArgumentOutOfRangeException(nameof(candidate.DepthBias));
        return candidate with { Surface = candidate.Surface.Sanitized() };
    }

    private static int CompareNearest(
        DdgiDecalCandidate left,
        DdgiDecalCandidate right)
    {
        int comparison = left.HitDistance.CompareTo(right.HitDistance);
        if (comparison != 0)
            return comparison;
        comparison = left.StableInstanceIdentity.CompareTo(
            right.StableInstanceIdentity);
        return comparison != 0
            ? comparison
            : left.PrimitiveIdentity.CompareTo(right.PrimitiveIdentity);
    }

    private static int CompareOverlayOrder(
        DdgiDecalCandidate left,
        DdgiDecalCandidate right)
    {
        int comparison = left.Layer.CompareTo(right.Layer);
        if (comparison != 0)
            return comparison;
        comparison = left.StableOrder.CompareTo(right.StableOrder);
        if (comparison != 0)
            return comparison;
        comparison = left.StableInstanceIdentity.CompareTo(
            right.StableInstanceIdentity);
        return comparison != 0
            ? comparison
            : left.PrimitiveIdentity.CompareTo(right.PrimitiveIdentity);
    }

    private static Vector3 CorrectShadingNormal(
        Vector3 geometricNormal,
        Vector3 shadingNormal)
    {
        geometricNormal = SafeNormal(geometricNormal, Vector3.UnitY);
        shadingNormal = SafeNormal(shadingNormal, geometricNormal);
        float hemisphere = Vector3.Dot(geometricNormal, shadingNormal);
        if (hemisphere <= 0f)
            return geometricNormal;
        if (hemisphere >= 0.1f)
            return shadingNormal;
        return SafeNormal(
            Vector3.Lerp(geometricNormal, shadingNormal, hemisphere / 0.1f),
            geometricNormal);
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-12f ? value.Normalized() : fallback;

    private static Vector3 Clamp01(Vector3 value) => Vector3.Clamp(
        value,
        Vector3.Zero,
        Vector3.One);

    private static Vector3 ClampRadiance(Vector3 value) => Vector3.Clamp(
        value,
        Vector3.Zero,
        new Vector3(65_504f));

    private static float MaxComponent(Vector3 value) =>
        Math.Max(value.X, Math.Max(value.Y, value.Z));

    private static float Lerp(float left, float right, float amount) =>
        left + (right - left) * amount;

    private static void ValidateUnitFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public enum DdgiVisibilityLayerKind
{
    OrdinaryAlphaBlend = 0,
    ThinTransmission = 1
}

public readonly record struct DdgiVisibilityLayer(
    DdgiVisibilityLayerKind Kind,
    float CoverageAlpha,
    Vector3 ThinTransmission)
{
    public static DdgiVisibilityLayer Alpha(float coverageAlpha) =>
        new(
            DdgiVisibilityLayerKind.OrdinaryAlphaBlend,
            coverageAlpha,
            Vector3.One);

    public static DdgiVisibilityLayer Thin(
        float coverageAlpha,
        Vector3 transmission) =>
        new(
            DdgiVisibilityLayerKind.ThinTransmission,
            coverageAlpha,
            transmission);
}

public readonly record struct DdgiVisibilityComposition(
    Vector3 Throughput,
    int ComposedLayerCount,
    bool ReachedLayerLimit,
    bool TerminatedForLowThroughput);

public readonly record struct DdgiReferenceSurface(
    Vector3 CanonicalGeometricNormal,
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    Vector3 DirectionalDiffuseBase,
    Vector3 DielectricF0,
    Vector3 SpecularF0,
    Vector3 DiffuseReflectance,
    Vector3 TransmittedDiffuseReflectance,
    Vector3 EmissiveRadiance,
    float MaterialOcclusion,
    float Opacity,
    float Metallic,
    float Roughness)
{
    public DdgiReferenceSurface Sanitized()
    {
        static Vector3 FiniteOrZero(Vector3 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z)
                ? value
                : Vector3.Zero;
        Vector3 geometric = FiniteOrZero(GeometricNormal);
        geometric = geometric.LengthSquared() > 1e-12f
            ? geometric.Normalized()
            : Vector3.UnitY;
        Vector3 canonical = FiniteOrZero(CanonicalGeometricNormal);
        canonical = canonical.LengthSquared() > 1e-12f
            ? canonical.Normalized()
            : geometric;
        Vector3 shading = FiniteOrZero(ShadingNormal);
        shading = shading.LengthSquared() > 1e-12f
            ? shading.Normalized()
            : geometric;
        return this with
        {
            CanonicalGeometricNormal = canonical,
            GeometricNormal = geometric,
            ShadingNormal = shading,
            DirectionalDiffuseBase = Vector3.Clamp(
                FiniteOrZero(DirectionalDiffuseBase),
                Vector3.Zero,
                Vector3.One),
            DielectricF0 = Vector3.Clamp(
                FiniteOrZero(DielectricF0),
                Vector3.Zero,
                Vector3.One),
            SpecularF0 = Vector3.Clamp(
                FiniteOrZero(SpecularF0),
                Vector3.Zero,
                Vector3.One),
            DiffuseReflectance = Vector3.Clamp(
                FiniteOrZero(DiffuseReflectance),
                Vector3.Zero,
                Vector3.One),
            TransmittedDiffuseReflectance = Vector3.Clamp(
                FiniteOrZero(TransmittedDiffuseReflectance),
                Vector3.Zero,
                Vector3.One),
            EmissiveRadiance = Vector3.Clamp(
                FiniteOrZero(EmissiveRadiance),
                Vector3.Zero,
                new Vector3(65_504f)),
            MaterialOcclusion = float.IsFinite(MaterialOcclusion)
                ? Math.Clamp(MaterialOcclusion, 0f, 1f)
                : 1f,
            Opacity = float.IsFinite(Opacity)
                ? Math.Clamp(Opacity, 0f, 1f)
                : 1f,
            Metallic = float.IsFinite(Metallic)
                ? Math.Clamp(Metallic, 0f, 1f)
                : 0f,
            Roughness = float.IsFinite(Roughness)
                ? Math.Clamp(Roughness, 0.04f, 1f)
                : 1f
        };
    }
}

public readonly record struct DdgiDecalCandidate(
    float HitDistance,
    float DepthTolerance,
    float DepthBias,
    int Layer,
    uint StableOrder,
    uint StableInstanceIdentity,
    uint PrimitiveIdentity,
    bool PremultipliedAlpha,
    DdgiReferenceSurface Surface);

public readonly record struct DdgiDecalComposition(
    DdgiReferenceSurface Surface,
    int RetainedCount,
    int AssociatedCount,
    int DepthRejectedCount,
    int FacingRejectedCount,
    int OverflowCount);
