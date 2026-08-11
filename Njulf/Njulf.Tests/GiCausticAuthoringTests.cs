using System.Numerics;
using Njulf.Assets;
using Njulf.Assets.Validation;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticAuthoringTests
{
    private static readonly Vector3[] TetrahedronVertices =
    [
        new(0f, 0f, 0f),
        new(1f, 0f, 0f),
        new(0f, 1f, 0f),
        new(0f, 0f, 1f)
    ];

    private static readonly uint[] OutwardTetrahedronIndices =
    [
        0u, 2u, 1u,
        0u, 1u, 3u,
        0u, 3u, 2u,
        1u, 2u, 3u
    ];

    [Test]
    public void ClosedDielectric_RequiresClosedStableVolumeAndPhysicalInputs()
    {
        var facts = new ModelGiCausticHeroGeometryFacts(
            IsStaticOrCurrentPoseQualified: true,
            IsClosedManifold: true,
            HasConsistentWinding: true,
            HasGeometricNormals: true,
            HasUnsupportedNestedMedium: false);

        ModelGiCausticHeroValidation eligible = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.ClosedDielectricHero,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.Volume,
            roughness: 0.01f,
            ior: 1.5f,
            thicknessFactor: 0.1f,
            attenuationDistance: float.PositiveInfinity,
            attenuationColor: Vector4.One,
            facts);
        ModelGiCausticHeroValidation open = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.ClosedDielectricHero,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.Volume,
            roughness: 0.01f,
            ior: 1.5f,
            thicknessFactor: 0.1f,
            attenuationDistance: float.PositiveInfinity,
            attenuationColor: Vector4.One,
            facts with { IsClosedManifold = false });

        Assert.Multiple(() =>
        {
            Assert.That(eligible.IsEligible, Is.True);
            Assert.That(open.IsEligible, Is.False);
            Assert.That(open.Reason,
                Is.EqualTo(ModelGiCausticHeroValidationReason.NotClosedManifold));
        });
    }

    [Test]
    public void MirrorAndRoughReference_HaveDeliberatelyDisjointRoughnessScopes()
    {
        var facts = new ModelGiCausticHeroGeometryFacts(true, false, false, false, false);

        ModelGiCausticHeroValidation mirror = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.MirrorHero,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.None,
            0.02f, 1.0f, 0.0f, float.PositiveInfinity, Vector4.One, facts);
        ModelGiCausticHeroValidation rough = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.RoughSpecularReference,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.None,
            0.2f, 1.0f, 0.0f, float.PositiveInfinity, Vector4.One, facts);
        ModelGiCausticHeroValidation invalidMirror = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.MirrorHero,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.None,
            0.2f, 1.0f, 0.0f, float.PositiveInfinity, Vector4.One, facts);

        Assert.Multiple(() =>
        {
            Assert.That(mirror.IsEligible, Is.True);
            Assert.That(rough.IsEligible, Is.True);
            Assert.That(invalidMirror.Reason,
                Is.EqualTo(ModelGiCausticHeroValidationReason.UnsupportedRoughness));
        });
    }

    [Test]
    public void RenderingContract_RejectsAlphaAndCurrentPoseFailuresBeforePhotonWork()
    {
        var material = new GiCausticMaterialContract(
            GiCausticParticipationMode.ClosedDielectricHero,
            Roughness: 0.01f,
            Ior: 1.5f,
            AbsorptionCoefficient: Vector3.Zero,
            IsAlphaBlendedOrMasked: true,
            UsesThinTransmission: false,
            HasExplicitThicknessSemantics: true);
        var facts = new GiCausticHeroGeometryFacts(
            IsRigidOrQualifiedCurrentPose: true,
            IsClosedManifold: true,
            HasConsistentWinding: true,
            HasValidGeometricNormals: true,
            HasUnsupportedNestedMedia: false,
            HasCurrentPoseAccelerationStructure: false,
            HasStableRevisions: true,
            HasAuthenticatedTopologyEvidence: true);

        GiCausticHeroValidation validation = GiCausticHeroContractValidator.Validate(
            material, facts);

        Assert.That(validation.RejectionReason,
            Is.EqualTo(GiCausticHeroRejectionReason.AlphaOrThinSurface));
    }

    [Test]
    public void TopologyAnalyzer_AuthenticatesClosedOutwardVolumeAndExactSeams()
    {
        Vector3[] seamVertices = [.. TetrahedronVertices, TetrahedronVertices[0]];
        uint[] seamIndices = (uint[])OutwardTetrahedronIndices.Clone();
        seamIndices[0] = 4u;

        bool analyzed = ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
            seamVertices,
            seamIndices,
            isSkinned: false,
            out ModelGiCausticHeroTopologyEvidence evidence,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(analyzed, Is.True, reason);
            Assert.That(evidence.IsStructurallyValid, Is.True);
            Assert.That(evidence.SourceVertexCount, Is.EqualTo(5));
            Assert.That(evidence.CanonicalVertexCount, Is.EqualTo(4));
            Assert.That(evidence.BoundaryEdgeCount, Is.Zero);
            Assert.That(evidence.NonManifoldEdgeCount, Is.Zero);
            Assert.That(evidence.InconsistentWindingEdgeCount, Is.Zero);
            Assert.That(evidence.ConnectedComponentCount, Is.EqualTo(1));
            Assert.That(evidence.HasPositiveOrientation, Is.True);
            Assert.That(evidence.SignedVolume, Is.EqualTo(1.0 / 6.0).Within(1e-12));
            Assert.That(evidence.Facts.IsClosedManifold, Is.True);
            Assert.That(evidence.Facts.HasConsistentWinding, Is.True);
            Assert.That(evidence.Facts.HasGeometricNormals, Is.True);
        });
    }

    [Test]
    public void TopologyAnalyzer_RejectsOpenInvertedAndTamperedEvidence()
    {
        uint[] open = OutwardTetrahedronIndices[..9];
        Assert.That(ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
            TetrahedronVertices, open, false,
            out ModelGiCausticHeroTopologyEvidence openEvidence,
            out string openReason), Is.True, openReason);

        uint[] inverted = new uint[OutwardTetrahedronIndices.Length];
        for (int index = 0; index < inverted.Length; index += 3)
        {
            inverted[index] = OutwardTetrahedronIndices[index];
            inverted[index + 1] = OutwardTetrahedronIndices[index + 2];
            inverted[index + 2] = OutwardTetrahedronIndices[index + 1];
        }
        Assert.That(ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
            TetrahedronVertices, inverted, false,
            out ModelGiCausticHeroTopologyEvidence invertedEvidence,
            out string invertedReason), Is.True, invertedReason);

        Assert.That(ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
            TetrahedronVertices, OutwardTetrahedronIndices, false,
            out ModelGiCausticHeroTopologyEvidence valid,
            out string validReason), Is.True, validReason);
        bool tamperedMatches = ModelGiCausticHeroTopologyAnalyzer.Matches(
            TetrahedronVertices,
            OutwardTetrahedronIndices,
            false,
            valid with { TopologyHash = valid.TopologyHash + 1UL },
            out string tamperedReason);

        Assert.Multiple(() =>
        {
            Assert.That(openEvidence.Facts.IsClosedManifold, Is.False);
            Assert.That(openEvidence.BoundaryEdgeCount, Is.GreaterThan(0));
            Assert.That(invertedEvidence.Facts.IsClosedManifold, Is.True);
            Assert.That(invertedEvidence.HasPositiveOrientation, Is.False);
            Assert.That(invertedEvidence.Facts.HasConsistentWinding, Is.False);
            Assert.That(tamperedMatches, Is.False);
            Assert.That(tamperedReason,
                Is.EqualTo("caustic-topology-evidence-does-not-match-mesh"));
        });
    }

    [Test]
    public void TopologyEvidenceOverload_FailsClosedForLegacyBooleanOnlyClaims()
    {
        ModelGiCausticHeroValidation missing = ModelGiCausticHeroValidator.Validate(
            ModelGiCausticParticipationMode.ClosedDielectricHero,
            ModelAlphaMode.Opaque,
            ModelGiTransmissionPolicy.Volume,
            0.01f,
            1.5f,
            0.1f,
            float.PositiveInfinity,
            Vector4.One,
            default(ModelGiCausticHeroTopologyEvidence));

        Assert.Multiple(() =>
        {
            Assert.That(missing.IsEligible, Is.False);
            Assert.That(missing.Reason,
                Is.EqualTo(ModelGiCausticHeroValidationReason.MissingTopologyEvidence));
        });
    }
}
