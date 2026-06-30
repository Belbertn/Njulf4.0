using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiVisibilityModelTests
    {
        [Test]
        public void EvaluateVisibility_UsesProbeSpacingScaledVarianceFloor()
        {
            DdgiVisibilityResult tightSpacing = EvaluateVisibility(
                mean: 1.0f,
                meanSquared: 1.0f,
                probeDistance: 1.5f,
                viewBias: 0.0f,
                minProbeSpacing: 0.5f);
            DdgiVisibilityResult wideSpacing = EvaluateVisibility(
                mean: 1.0f,
                meanSquared: 1.0f,
                probeDistance: 1.5f,
                viewBias: 0.0f,
                minProbeSpacing: 4.0f);

            Assert.Multiple(() =>
            {
                Assert.That(tightSpacing.Variance, Is.EqualTo(0.005f).Within(0.0001f));
                Assert.That(wideSpacing.Variance, Is.EqualTo(0.04f).Within(0.0001f));
                Assert.That(wideSpacing.Transport, Is.GreaterThan(tightSpacing.Transport));
            });
        }

        [Test]
        public void VisibilityConfidence_IsSeparateFromTransportAndHasDeadZone()
        {
            Assert.Multiple(() =>
            {
                Assert.That(VisibilityConfidence(0.0f), Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(VisibilityConfidence(0.02f), Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(VisibilityConfidence(0.40f), Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(VisibilityConfidence(1.0f), Is.EqualTo(1.0f).Within(0.0001f));
            });
        }

        private static DdgiVisibilityResult EvaluateVisibility(
            float mean,
            float meanSquared,
            float probeDistance,
            float viewBias,
            float minProbeSpacing)
        {
            float safeMean = Math.Max(mean, 0.0001f);
            float safeMeanSquared = Math.Max(meanSquared, safeMean * safeMean);
            float minVariance = Math.Max(0.005f, minProbeSpacing * minProbeSpacing * 0.0025f);
            float variance = Math.Max(safeMeanSquared - safeMean * safeMean, minVariance);
            if (probeDistance <= safeMean + Math.Max(viewBias, 0.02f))
                return new DdgiVisibilityResult(1.0f, safeMean, variance);

            float delta = probeDistance - safeMean;
            float transport = Math.Clamp(variance / (variance + delta * delta), 0.0f, 1.0f);
            return new DdgiVisibilityResult(transport, safeMean, variance);
        }

        private static float VisibilityConfidence(float visibilityTransport)
        {
            float t = Math.Clamp((Math.Clamp(visibilityTransport, 0.0f, 1.0f) - 0.02f) / 0.38f, 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        private readonly record struct DdgiVisibilityResult(float Transport, float Mean, float Variance);
    }
}
