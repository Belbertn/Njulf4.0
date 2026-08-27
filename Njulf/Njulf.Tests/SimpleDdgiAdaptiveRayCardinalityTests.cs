using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiAdaptiveRayCardinalityTests
{
    [TestCase(32u, 128u, new uint[] { 32u, 32u, 64u, 128u })]
    [TestCase(24u, 96u, new uint[] { 24u, 32u, 64u, 96u })]
    [TestCase(12u, 48u, new uint[] { 12u, 16u, 32u, 48u })]
    public void BuildTiers_ProducesBoundedProgressivePrefixes(
        uint maintenance,
        uint full,
        uint[] expected)
    {
        Span<uint> tiers = stackalloc uint[SimpleDdgiAdaptiveRayCardinality.TierCount];
        SimpleDdgiAdaptiveRayCardinality.BuildTiers(maintenance, full, tiers);

        Assert.That(tiers.ToArray(), Is.EqualTo(expected));
        Assert.That(tiers.ToArray(), Is.Ordered);
    }

    [Test]
    public void PromotionAndDemotion_MoveOneSupportedTierAtATime()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiAdaptiveRayCardinality.Promote(24u, 24u, 96u), Is.EqualTo(32u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.Promote(32u, 24u, 96u), Is.EqualTo(64u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.Promote(96u, 24u, 96u), Is.EqualTo(96u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.Demote(96u, 24u, 96u), Is.EqualTo(64u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.Demote(24u, 24u, 96u), Is.EqualTo(24u));
        });
    }

    [Test]
    public void Baseline_UsesFullCardinalityUntilVarianceIsMeasured()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiAdaptiveRayCardinality.ResolveBaseline(32u, 128u, 0), Is.EqualTo(128u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.ResolveBaseline(16u, 64u, 1), Is.EqualTo(64u));
            Assert.That(SimpleDdgiAdaptiveRayCardinality.ResolveBaseline(8u, 32u, 2), Is.EqualTo(32u));
        });
    }

    [Test]
    public void QuadratureWitness_IsExplicitFiniteAndFailClosed()
    {
        uint certified = SimpleDdgiAdaptiveRayCardinality
            .PackQuadratureWitness(0.02f);
        uint rejected = SimpleDdgiAdaptiveRayCardinality
            .PackQuadratureWitness(0.20f);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.TryUnpackQuadratureWitness(
                    certified,
                    out float decoded),
                Is.True);
            Assert.That(decoded, Is.EqualTo(0.02f));
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.CertifiesDemotion(certified),
                Is.True);
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.RequiresPromotion(certified),
                Is.False);
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.CertifiesDemotion(rejected),
                Is.False);
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.RequiresPromotion(rejected),
                Is.True);
            Assert.That(
                SimpleDdgiAdaptiveRayCardinality.RequiresPromotion(0u),
                Is.True);
        });
    }

    [Test]
    public void FeedbackEvidence_PreservesValidityAndUsesContentPartitionForTotal()
    {
        SimpleDdgiAdaptiveRayBucketEvidence valid =
            SimpleDdgiAdaptiveRayBucketEvidence.FromPacked(
                17u,
                SimpleDdgiAdaptiveRayCardinality.PackQuadratureWitness(0.04f));
        SimpleDdgiAdaptiveRayBucketEvidence missing =
            SimpleDdgiAdaptiveRayBucketEvidence.FromPacked(3u, 0u);
        var evidence = new SimpleDdgiAdaptiveRayEvidence(
            valid,
            missing,
            default,
            new(1u, 0.01f, true),
            new(2u, 0.02f, true),
            new(4u, 0.03f, true),
            new(8u, 0.04f, true));

        Assert.Multiple(() =>
        {
            Assert.That(valid.SavedRayCount, Is.EqualTo(17u));
            Assert.That(valid.QualityErrorValid, Is.True);
            Assert.That(valid.MaximumQuadratureError, Is.EqualTo(0.04f));
            Assert.That(missing.QualityErrorValid, Is.False);
            Assert.That(missing.MaximumQuadratureError, Is.Zero);
            Assert.That(evidence.TotalSavedRayCount, Is.EqualTo(15UL));
        });
    }
}
