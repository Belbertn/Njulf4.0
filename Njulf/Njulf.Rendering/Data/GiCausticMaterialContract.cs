using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>
/// Opt-in authoring contract for the deliberately small first caustic scope.
/// A material is never inferred to be a caustic caster from metallic or
/// transmission values alone.
/// </summary>
public enum GiCausticParticipationMode : byte
{
    None = 0,
    MirrorHero = 1,
    ClosedDielectricHero = 2,
    RoughSpecularReference = 3
}

public enum GiCausticHeroRejectionReason : byte
{
    None = 0,
    ParticipationDisabled,
    NonFiniteMaterial,
    AlphaOrThinSurface,
    AnimatedTopology,
    NotClosedManifold,
    InconsistentWinding,
    MissingGeometricNormals,
    InvalidIor,
    MissingThicknessSemantics,
    UnsupportedNestedMedium,
    UnsupportedRoughness,
    CurrentPoseAccelerationStructureUnavailable,
    RevisionUnavailable,
    TopologyEvidenceUnavailable,
    HeroCapacityExceeded
}

/// <summary>
/// The material values that own a tagged specular/refractive photon path.  The
/// values are scene-linear physical parameters; no display or diffuse-relative
/// energy cap belongs in this contract.
/// </summary>
public readonly record struct GiCausticMaterialContract(
    GiCausticParticipationMode Participation,
    float Roughness,
    float Ior,
    Vector3 AbsorptionCoefficient,
    bool IsAlphaBlendedOrMasked,
    bool UsesThinTransmission,
    bool HasExplicitThicknessSemantics)
{
    public GiCausticCasterPolicy CasterPolicy { get; init; } =
        GiCausticCasterPolicy.Default;
    public OpticalBoundaryKind BoundaryKind { get; init; } =
        OpticalBoundaryKind.ClosedVolume;
    public bool UsesVolumeTransmission { get; init; }

    public bool EffectiveUsesVolumeTransmission =>
        UsesVolumeTransmission ||
        Participation == GiCausticParticipationMode.ClosedDielectricHero;

    public GiCausticCasterPolicy EffectiveCasterPolicy
    {
        get
        {
            GiCausticCasterPolicy resolved =
                OpticalMaterialGpuContract.ResolveCasterPolicy(
                    CasterPolicy, Participation);
            if (resolved != GiCausticCasterPolicy.Default)
                return resolved;
            return EffectiveUsesVolumeTransmission ||
                   BoundaryKind == OpticalBoundaryKind.WaterSurface
                ? GiCausticCasterPolicy.DielectricPriority
                : GiCausticCasterPolicy.Disabled;
        }
    }

    public GiCausticParticipationMode EffectiveLegacyParticipation =>
        OpticalMaterialGpuContract.ToLegacyParticipation(
            EffectiveCasterPolicy,
            EffectiveUsesVolumeTransmission
                ? GiTransmissionPolicy.Volume : GiTransmissionPolicy.None);

    public bool IsFinite =>
        float.IsFinite(Roughness) &&
        float.IsFinite(Ior) &&
        float.IsFinite(AbsorptionCoefficient.X) &&
        float.IsFinite(AbsorptionCoefficient.Y) &&
        float.IsFinite(AbsorptionCoefficient.Z);

    public bool IsEnergyConservingMirror =>
        EffectiveCasterPolicy == GiCausticCasterPolicy.Mirror &&
        IsFinite &&
        Roughness >= 0.0f && Roughness <= 1.0f &&
        Ior > 0.0f &&
        AbsorptionCoefficient.X >= 0.0f &&
        AbsorptionCoefficient.Y >= 0.0f &&
        AbsorptionCoefficient.Z >= 0.0f;
}

/// <summary>
/// Topology/current-pose facts collected by asset validation and the AS
/// manager. They are explicit because a closed dielectric cannot be accepted
/// merely from material metadata.
/// </summary>
public readonly record struct GiCausticHeroGeometryFacts(
    bool IsRigidOrQualifiedCurrentPose,
    bool IsClosedManifold,
    bool HasConsistentWinding,
    bool HasValidGeometricNormals,
    bool HasUnsupportedNestedMedia,
    bool HasCurrentPoseAccelerationStructure,
    bool HasStableRevisions,
    bool HasAuthenticatedTopologyEvidence);

public readonly record struct GiCausticHeroValidation(
    bool IsEligible,
    GiCausticHeroRejectionReason RejectionReason,
    string Detail)
{
    public static GiCausticHeroValidation Accepted { get; } = new(
        true,
        GiCausticHeroRejectionReason.None,
        "eligible");
}

public static class GiCausticHeroContractValidator
{
    public const float MaximumMirrorRoughness = 0.04f;
    public const float MinimumRoughSpecularRoughness =
        DielectricTransportMath.DeltaRoughnessThreshold;

    /// <summary>
    /// Validates the authored contract without allocating or querying device
    /// state. Callers retain the ordinary path for every rejection.
    /// </summary>
    public static GiCausticHeroValidation Validate(
        in GiCausticMaterialContract material,
        in GiCausticHeroGeometryFacts geometry)
    {
        GiCausticCasterPolicy casterPolicy = material.EffectiveCasterPolicy;
        if (casterPolicy == GiCausticCasterPolicy.Disabled)
            return Reject(GiCausticHeroRejectionReason.ParticipationDisabled);
        if (!material.IsFinite || material.AbsorptionCoefficient.X < 0.0f ||
            material.AbsorptionCoefficient.Y < 0.0f ||
            material.AbsorptionCoefficient.Z < 0.0f)
        {
            return Reject(GiCausticHeroRejectionReason.NonFiniteMaterial);
        }
        if (material.IsAlphaBlendedOrMasked || material.UsesThinTransmission)
            return Reject(GiCausticHeroRejectionReason.AlphaOrThinSurface);
        if (!geometry.IsRigidOrQualifiedCurrentPose)
            return Reject(GiCausticHeroRejectionReason.AnimatedTopology);
        if (!geometry.HasAuthenticatedTopologyEvidence)
            return Reject(GiCausticHeroRejectionReason.TopologyEvidenceUnavailable);
        if (!geometry.HasCurrentPoseAccelerationStructure)
        {
            return Reject(
                GiCausticHeroRejectionReason.CurrentPoseAccelerationStructureUnavailable);
        }
        if (!geometry.HasStableRevisions)
            return Reject(GiCausticHeroRejectionReason.RevisionUnavailable);

        switch (casterPolicy)
        {
            case GiCausticCasterPolicy.Mirror:
                if (material.Roughness < 0.0f ||
                    material.Roughness > MaximumMirrorRoughness ||
                    material.Ior <= 0.0f)
                {
                    return Reject(GiCausticHeroRejectionReason.UnsupportedRoughness);
                }

                return GiCausticHeroValidation.Accepted;

            case GiCausticCasterPolicy.DielectricPriority:
                bool water = material.BoundaryKind ==
                    OpticalBoundaryKind.WaterSurface;
                if (!water && !geometry.IsClosedManifold)
                    return Reject(GiCausticHeroRejectionReason.NotClosedManifold);
                if (!water && !geometry.HasConsistentWinding)
                    return Reject(GiCausticHeroRejectionReason.InconsistentWinding);
                if (!geometry.HasValidGeometricNormals)
                    return Reject(GiCausticHeroRejectionReason.MissingGeometricNormals);
                if (!material.EffectiveUsesVolumeTransmission ||
                    !material.HasExplicitThicknessSemantics)
                {
                    return Reject(
                        GiCausticHeroRejectionReason.MissingThicknessSemantics);
                }
                if (material.Ior <= 1.0f || material.Ior > 4.0f)
                    return Reject(GiCausticHeroRejectionReason.InvalidIor);
                if (material.Roughness < 0.0f || material.Roughness > 1.0f)
                    return Reject(GiCausticHeroRejectionReason.UnsupportedRoughness);

                return GiCausticHeroValidation.Accepted;

            case GiCausticCasterPolicy.RoughSpecular:
                if (material.Roughness <= MinimumRoughSpecularRoughness ||
                    material.Roughness > 1.0f)
                    return Reject(GiCausticHeroRejectionReason.UnsupportedRoughness);

                return GiCausticHeroValidation.Accepted;

            default:
                return Reject(GiCausticHeroRejectionReason.ParticipationDisabled);
        }
    }

    private static GiCausticHeroValidation Reject(
        GiCausticHeroRejectionReason reason) => new(false, reason, ToDetail(reason));

    private static string ToDetail(GiCausticHeroRejectionReason reason) =>
        reason switch
        {
            GiCausticHeroRejectionReason.ParticipationDisabled => "participation-disabled",
            GiCausticHeroRejectionReason.NonFiniteMaterial => "material-values-must-be-finite-and-non-negative",
            GiCausticHeroRejectionReason.AlphaOrThinSurface => "alpha-and-thin-surfaces-are-not-caustic-hero-boundaries",
            GiCausticHeroRejectionReason.AnimatedTopology => "hero-requires-rigid-or-qualified-current-pose-geometry",
            GiCausticHeroRejectionReason.NotClosedManifold => "closed-dielectric-requires-a-closed-manifold",
            GiCausticHeroRejectionReason.InconsistentWinding => "closed-dielectric-requires-consistent-winding",
            GiCausticHeroRejectionReason.MissingGeometricNormals => "closed-dielectric-requires-geometric-normals",
            GiCausticHeroRejectionReason.InvalidIor => "closed-dielectric-ior-must-be-finite-and-in-supported-range",
            GiCausticHeroRejectionReason.MissingThicknessSemantics => "closed-dielectric-requires-thickness-semantics",
            GiCausticHeroRejectionReason.UnsupportedNestedMedium => "nested-media-are-not-supported-by-the-caustic-hero-contract",
            GiCausticHeroRejectionReason.UnsupportedRoughness => "roughness-is-outside-the-selected-caustic-mode-scope",
            GiCausticHeroRejectionReason.CurrentPoseAccelerationStructureUnavailable => "current-pose-acceleration-structure-is-unavailable",
            GiCausticHeroRejectionReason.RevisionUnavailable => "hero-revisions-are-unavailable",
            GiCausticHeroRejectionReason.TopologyEvidenceUnavailable => "authenticated-cooker-topology-evidence-is-unavailable",
            GiCausticHeroRejectionReason.HeroCapacityExceeded => "admitted-caustic-hero-capacity-exceeded",
            _ => "rejected"
        };
}
