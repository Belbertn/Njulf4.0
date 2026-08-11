using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiPrerequisiteGateTests
{
    [Test]
    public void MissingFrozenEvidence_FailsClosedBeforeAnyFeatureAdmission()
    {
        var manifest = new AdvancedGiPrerequisiteManifest
        {
            FeatureIsolatedReferenceCorpusAvailable = true,
            SpatialEmissiveAndCachedRelightingQualified = true,
            RefinementBricksQualified = true,
            AlphaConformancePassed = true
        };

        AdvancedGiPrerequisiteGateResult result = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.TaggedCaustics);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.QualificationId, Is.Empty);
            Assert.That(result.FailureDetail, Does.Contain("missing-or-unverified-frozen-contract"));
        });
    }

    [Test]
    public void FeatureSpecificPrerequisites_RemainIndependentlyFailClosed()
    {
        AdvancedGiPrerequisiteManifest manifest = CompleteManifest(
            alphaConformance: false,
            spatialEmissive: false,
            refinement: false);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.Evaluate(AdvancedGiPrerequisiteFeature.ReceiverFeedback).Passed,
                Is.True);
            Assert.That(manifest.Evaluate(AdvancedGiPrerequisiteFeature.OpacityMicromaps)
                .FailureDetail, Is.EqualTo("alpha-conformance-gate-not-passed"));
            Assert.That(manifest.Evaluate(AdvancedGiPrerequisiteFeature.DirectionalGuiding)
                .FailureDetail,
                Is.EqualTo("spatial-emissive-and-cached-relighting-gate-not-passed"));
            Assert.That(manifest.Evaluate(AdvancedGiPrerequisiteFeature.NearFieldResidual)
                .FailureDetail, Is.EqualTo("b3-refinement-brick-gate-not-passed"));
        });
    }

    [Test]
    public void CompleteManifest_ProducesStableFeatureScopedQualificationIds()
    {
        AdvancedGiPrerequisiteManifest first = CompleteManifest();
        AdvancedGiPrerequisiteManifest second = CompleteManifest();

        AdvancedGiPrerequisiteGateResult caustic = first.Evaluate(
            AdvancedGiPrerequisiteFeature.TaggedCaustics);
        AdvancedGiPrerequisiteGateResult residual = first.Evaluate(
            AdvancedGiPrerequisiteFeature.NearFieldResidual);

        Assert.Multiple(() =>
        {
            Assert.That(caustic.Passed, Is.True);
            Assert.That(caustic.QualificationId, Has.Length.EqualTo(64));
            Assert.That(caustic.QualificationId,
                Is.EqualTo(second.Evaluate(AdvancedGiPrerequisiteFeature.TaggedCaustics)
                    .QualificationId));
            Assert.That(caustic.QualificationId, Is.Not.EqualTo(residual.QualificationId));
        });
    }

    private static AdvancedGiPrerequisiteManifest CompleteManifest(
        bool alphaConformance = true,
        bool spatialEmissive = true,
        bool refinement = true)
    {
        var manifest = new AdvancedGiPrerequisiteManifest
        {
            FeatureIsolatedReferenceCorpusAvailable = true,
            SpatialEmissiveAndCachedRelightingQualified = spatialEmissive,
            RefinementBricksQualified = refinement,
            AlphaConformancePassed = alphaConformance
        };
        foreach (AdvancedGiPrerequisiteContract contract in
                 System.Enum.GetValues<AdvancedGiPrerequisiteContract>())
        {
            manifest.Add(new AdvancedGiFrozenContractEvidence(
                contract,
                AbiRevision: 1u + (uint)contract,
                ArtifactSha256: new string((char)('a' + (byte)contract % 6), 64),
                Verified: true,
                Detail: contract.ToString()));
        }
        return manifest;
    }
}
