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
}
