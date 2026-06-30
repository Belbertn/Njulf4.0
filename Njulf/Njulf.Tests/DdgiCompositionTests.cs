using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DdgiCompositionTests
    {
        [Test]
        public void FullCoverageConfidentVisibleProbe_UsesFullDdgi()
        {
            DdgiCompositionResult result = Compose(spatialCoverage: 1.0f, supportCoverage: 1.0f, dataConfidence: 1.0f, visibilityConfidence: 1.0f, indirectAo: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.EqualTo(DdgiDiffuse).Within(0.0001f));
            });
        }

        [Test]
        public void CoveredConfidentLowVisibilityProbe_FallsBackWithoutGoingBlack()
        {
            DdgiCompositionResult result = Compose(spatialCoverage: 1.0f, supportCoverage: 1.0f, dataConfidence: 1.0f, visibilityConfidence: 0.0f, indirectAo: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.GreaterThan(0.0f));
                Assert.That(result.Indirect, Is.GreaterThan(EnvironmentDiffuse * 0.99f));
                Assert.That(result.Indirect, Is.LessThan(DdgiDiffuse));
            });
        }

        [Test]
        public void CoveredVisibleNoData_UsesEnvironmentFallback()
        {
            DdgiCompositionResult result = Compose(spatialCoverage: 1.0f, supportCoverage: 1.0f, dataConfidence: 0.0f, visibilityConfidence: 1.0f, indirectAo: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.EqualTo(EnvironmentDiffuse).Within(0.0001f));
            });
        }

        [Test]
        public void SpatialCoverageWithoutSupport_UsesEnvironmentFallback()
        {
            DdgiCompositionResult result = Compose(spatialCoverage: 1.0f, supportCoverage: 0.0f, dataConfidence: 0.0f, visibilityConfidence: 1.0f, indirectAo: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.EqualTo(EnvironmentDiffuse).Within(0.0001f));
            });
        }

        [Test]
        public void NoCoverage_UsesEnvironmentFallback()
        {
            DdgiCompositionResult result = Compose(spatialCoverage: 0.0f, supportCoverage: 0.0f, dataConfidence: 0.0f, visibilityConfidence: 0.0f, indirectAo: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.EqualTo(EnvironmentDiffuse).Within(0.0001f));
            });
        }

        [Test]
        public void PartialVisibilityWithAo_DoesNotDoubleKillLowFrequencyDdgi()
        {
            DdgiCompositionResult unoccluded = Compose(spatialCoverage: 1.0f, supportCoverage: 1.0f, dataConfidence: 1.0f, visibilityConfidence: 0.2f, indirectAo: 1.0f);
            DdgiCompositionResult occluded = Compose(spatialCoverage: 1.0f, supportCoverage: 1.0f, dataConfidence: 1.0f, visibilityConfidence: 0.2f, indirectAo: 0.5f);

            Assert.Multiple(() =>
            {
                Assert.That(occluded.DdgiTrust, Is.EqualTo(unoccluded.DdgiTrust).Within(0.0001f));
                Assert.That(occluded.EnvironmentFallbackWeight, Is.EqualTo(unoccluded.EnvironmentFallbackWeight).Within(0.0001f));
                Assert.That(occluded.Indirect, Is.EqualTo(unoccluded.Indirect).Within(0.0001f));
            });
        }

        [Test]
        public void PartialSupportWithAo_AppliesAoOnlyToEnvironmentFallback()
        {
            DdgiCompositionResult unoccluded = Compose(spatialCoverage: 1.0f, supportCoverage: 0.5f, dataConfidence: 1.0f, visibilityConfidence: 1.0f, indirectAo: 1.0f);
            DdgiCompositionResult occluded = Compose(spatialCoverage: 1.0f, supportCoverage: 0.5f, dataConfidence: 1.0f, visibilityConfidence: 1.0f, indirectAo: 0.5f);

            float expectedOccluded = DdgiDiffuse * occluded.DdgiTrust + EnvironmentDiffuse * occluded.EnvironmentFallbackWeight * 0.5f;

            Assert.Multiple(() =>
            {
                Assert.That(occluded.DdgiTrust, Is.EqualTo(unoccluded.DdgiTrust).Within(0.0001f));
                Assert.That(occluded.EnvironmentFallbackWeight, Is.EqualTo(unoccluded.EnvironmentFallbackWeight).Within(0.0001f));
                Assert.That(occluded.Indirect, Is.EqualTo(expectedOccluded).Within(0.0001f));
                Assert.That(occluded.Indirect, Is.GreaterThan(unoccluded.Indirect * 0.5f));
            });
        }

        [Test]
        public void LowVisibilityConfidenceHighTransport_UsesTransportForLeakAttenuation()
        {
            DdgiCompositionResult result = Compose(
                spatialCoverage: 1.0f,
                supportCoverage: 1.0f,
                dataConfidence: 1.0f,
                visibilityConfidence: 0.0f,
                indirectAo: 1.0f,
                visibilityTransport: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(result.DdgiTrust, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(result.EnvironmentFallbackWeight, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(result.Indirect, Is.EqualTo(DdgiDiffuse).Within(0.0001f));
            });
        }

        private const float DdgiDiffuse = 2.0f;
        private const float EnvironmentDiffuse = 0.25f;
        private const float EnvironmentFallbackIntensity = 1.0f;
        private const float LeakStrength = 0.85f;

        private static DdgiCompositionResult Compose(
            float spatialCoverage,
            float supportCoverage,
            float dataConfidence,
            float visibilityConfidence,
            float indirectAo,
            float? visibilityTransport = null)
        {
            _ = Clamp01(spatialCoverage);
            float safeSupport = Clamp01(supportCoverage);
            float safeDataConfidence = Clamp01(dataConfidence);
            float safeVisibilityTransport = Clamp01(visibilityTransport ?? Clamp01(visibilityConfidence));
            float safeAo = Clamp01(indirectAo);
            float leakAttenuation = Math.Clamp(Lerp(1.0f, safeVisibilityTransport, LeakStrength), 0.05f, 1.0f);
            float effectiveDdgiWeight = safeSupport * SmoothStep(0.02f, 0.20f, safeDataConfidence);
            float ddgiTrust = Clamp01(effectiveDdgiWeight);
            float environmentTrust = Clamp01(1.0f - ddgiTrust);
            float environmentFallbackWeight = Math.Clamp(environmentTrust * EnvironmentFallbackIntensity, 0.0f, 4.0f);
            float indirect = DdgiDiffuse * ddgiTrust * leakAttenuation + EnvironmentDiffuse * environmentFallbackWeight * safeAo;
            return new DdgiCompositionResult(ddgiTrust, environmentFallbackWeight, indirect);
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3.0f - 2.0f * t);
        }

        private static float Clamp01(float value) => Math.Clamp(value, 0.0f, 1.0f);

        private static float Lerp(float left, float right, float amount) => left + (right - left) * amount;

        private readonly record struct DdgiCompositionResult(
            float DdgiTrust,
            float EnvironmentFallbackWeight,
            float Indirect);
    }
}
