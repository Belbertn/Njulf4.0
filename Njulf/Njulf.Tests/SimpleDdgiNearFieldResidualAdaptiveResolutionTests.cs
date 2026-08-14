using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualAdaptiveResolutionTests
{
    [Test]
    public void SustainedP95Pressure_DemotesQuarterToEighth()
    {
        var governor = new SimpleDdgiNearFieldResidualAdaptiveResolution(0.25F);

        bool changed = false;
        for (int sample = 0;
             sample < SimpleDdgiNearFieldResidualAdaptiveResolution.SampleWindowSize;
             sample++)
        {
            changed |= governor.ObserveAuthoritativeGpuTime(800UL);
        }

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(governor.ActiveScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Eighth));
            Assert.That(governor.LastP95Microseconds, Is.EqualTo(800UL));
            Assert.That(governor.Revision, Is.EqualTo(2U));
            Assert.That(governor.AuthoritativeTimingSampleCount,
                Is.EqualTo((ulong)SimpleDdgiNearFieldResidualAdaptiveResolution
                    .SampleWindowSize));
            Assert.That(governor.WindowSampleCount, Is.Zero);
            Assert.That(governor.DemotionCount, Is.EqualTo(1U));
            Assert.That(governor.PromotionCount, Is.Zero);
        });
    }

    [Test]
    public void SustainedHeadroom_PromotesOnlyToAdmittedMaximum()
    {
        var governor = new SimpleDdgiNearFieldResidualAdaptiveResolution(0.25F);
        for (int sample = 0;
             sample < SimpleDdgiNearFieldResidualAdaptiveResolution.SampleWindowSize;
             sample++)
        {
            governor.ObserveAuthoritativeGpuTime(900UL);
        }

        bool changed = false;
        int replacementSamples =
            SimpleDdgiNearFieldResidualAdaptiveResolution.SampleWindowSize +
            (SimpleDdgiNearFieldResidualAdaptiveResolution.PromotionWindowCount - 1) *
            SimpleDdgiNearFieldResidualAdaptiveResolution.EvaluationCadence;
        for (int sample = 0; sample < replacementSamples; sample++)
            changed |= governor.ObserveAuthoritativeGpuTime(400UL);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(governor.ActiveScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));
            Assert.That(governor.CreateExtent(1_921, 1_081), Is.EqualTo(
                new SimpleDdgiNearFieldResidualExecutionExtent(
                    481,
                    271,
                    SimpleDdgiNearFieldResidualExecutionScale.Quarter,
                    governor.Revision)));
        });

        for (int sample = 0; sample < 600; sample++)
            Assert.That(governor.ObserveAuthoritativeGpuTime(100UL), Is.False);
        Assert.That(governor.ActiveScale,
            Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));
    }

    [Test]
    public void HalfAdmission_StartsQuarterAndRequiresSustainedHeadroom()
    {
        var governor = new SimpleDdgiNearFieldResidualAdaptiveResolution(0.5F);

        Assert.That(governor.ActiveScale,
            Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));

        int promotionSamples =
            SimpleDdgiNearFieldResidualAdaptiveResolution.SampleWindowSize +
            (SimpleDdgiNearFieldResidualAdaptiveResolution.PromotionWindowCount - 1) *
            SimpleDdgiNearFieldResidualAdaptiveResolution.EvaluationCadence;
        bool changed = false;
        for (int sample = 0; sample < promotionSamples; sample++)
            changed |= governor.ObserveAuthoritativeGpuTime(400UL);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(governor.ActiveScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Half));
            Assert.That(governor.MaximumScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Half));
            Assert.That(governor.Revision, Is.EqualTo(2U));
            Assert.That(governor.PromotionCount, Is.EqualTo(1U));
            Assert.That(governor.DemotionCount, Is.Zero);
        });
    }

    [Test]
    public void DisabledGovernor_StartsAndRemainsAtAdmittedResolution()
    {
        var governor = new SimpleDdgiNearFieldResidualAdaptiveResolution(
            0.5F,
            enabled: false);

        bool changed = false;
        for (int sample = 0; sample < 600; sample++)
            changed |= governor.ObserveAuthoritativeGpuTime(2_000UL);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(governor.ActiveScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Half));
            Assert.That(governor.MaximumScale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Half));
            Assert.That(governor.Revision, Is.EqualTo(1U));
            Assert.That(governor.AuthoritativeTimingSampleCount, Is.Zero);
            Assert.That(governor.PromotionCount, Is.Zero);
            Assert.That(governor.DemotionCount, Is.Zero);
        });
    }

    [Test]
    public void ShortSpike_DoesNotChangeResolution()
    {
        var governor = new SimpleDdgiNearFieldResidualAdaptiveResolution(0.25F);
        for (int sample = 0; sample < 119; sample++)
            Assert.That(governor.ObserveAuthoritativeGpuTime(400UL), Is.False);

        Assert.That(governor.ObserveAuthoritativeGpuTime(2_000UL), Is.False);
        Assert.That(governor.ActiveScale,
            Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));
    }
}
