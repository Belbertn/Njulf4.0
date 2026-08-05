using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiRayCapacityPlannerTests
{
    [Test]
    public void Evaluate_UsesExactMixedTierRayCount()
    {
        SimpleDdgiRayTier[] tiers =
        [
            new SimpleDdgiRayTier(2, 4),
            new SimpleDdgiRayTier(3, 2)
        ];

        SimpleDdgiRayCapacityResult result = SimpleDdgiRayCapacityPlanner.Evaluate(
            tiers,
            targetFrames: 4,
            admittedRaysPerFrame: 3UL,
            framesPerSecond: 60.0f);

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalRequiredRays, Is.EqualTo(14UL));
            Assert.That(result.TargetRaysPerFrame, Is.EqualTo(4UL));
            Assert.That(result.AdmittedRaysPerFrame, Is.EqualTo(3UL));
            Assert.That(result.CapacityShortfall, Is.EqualTo(1UL));
            Assert.That(result.TargetIsFeasible, Is.False);
            Assert.That(result.MinimumAchievableSweepSeconds, Is.EqualTo(14.0f / 180.0f).Within(1e-6f));
        });
    }

    [Test]
    public void Evaluate_EmptyOrZeroCapacityHasStableFiniteContract()
    {
        SimpleDdgiRayCapacityResult empty = SimpleDdgiRayCapacityPlanner.Evaluate(
            ReadOnlySpan<SimpleDdgiRayTier>.Empty,
            targetFrames: 0,
            admittedRaysPerFrame: 0UL,
            framesPerSecond: 0.0f);
        SimpleDdgiRayCapacityResult blocked = SimpleDdgiRayCapacityPlanner.Evaluate(
            new[] { new SimpleDdgiRayTier(1, 8) },
            targetFrames: 4,
            admittedRaysPerFrame: 0UL,
            framesPerSecond: 60.0f);

        Assert.Multiple(() =>
        {
            Assert.That(empty.TotalRequiredRays, Is.Zero);
            Assert.That(empty.MinimumAchievableSweepSeconds, Is.Zero);
            Assert.That(empty.TargetIsFeasible, Is.True);
            Assert.That(blocked.CapacityShortfall, Is.EqualTo(2UL));
            Assert.That(float.IsPositiveInfinity(blocked.MinimumAchievableSweepSeconds), Is.True);
        });
    }

    [Test]
    public void TierCadence_MixedRingsReceiveExactNonStarvingSweepAllotments()
    {
        const int frames = 600;
        int nearTotal = 0;
        int midTotal = 0;
        int farTotal = 0;
        for (uint frame = 0u; frame < frames; frame++)
        {
            int near = SimpleDdgiRayCapacityPlanner.ResolveTierProbeTarget(
                10_976,
                frames,
                frame);
            int mid = SimpleDdgiRayCapacityPlanner.ResolveTierProbeTarget(
                3_240,
                frames,
                frame);
            int far = SimpleDdgiRayCapacityPlanner.ResolveTierProbeTarget(
                1_152,
                frames,
                frame);

            Assert.Multiple(() =>
            {
                Assert.That(near, Is.InRange(18, 19));
                Assert.That(mid, Is.InRange(5, 6));
                Assert.That(far, Is.InRange(1, 2));
            });
            nearTotal += near;
            midTotal += mid;
            farTotal += far;
        }

        Assert.Multiple(() =>
        {
            Assert.That(nearTotal, Is.EqualTo(10_976));
            Assert.That(midTotal, Is.EqualTo(3_240));
            Assert.That(farTotal, Is.EqualTo(1_152));
        });
    }
}
