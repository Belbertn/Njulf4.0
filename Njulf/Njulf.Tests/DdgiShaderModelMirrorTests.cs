using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiShaderModelMirrorTests
    {
        [Test]
        public void EvaluateDdgiVisibility_NonOccludingMomentsReturnFullTransport()
        {
            DdgiVisibility visibility = EvaluateDdgiVisibility(
                mean: 8.0f,
                meanSquared: 64.0f,
                probeDistance: 2.0f,
                viewBias: 0.05f,
                minProbeSpacing: 0.65f);

            Assert.Multiple(() =>
            {
                Assert.That(visibility.Transport, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(visibility.Mean, Is.EqualTo(8.0f).Within(0.0001f));
                Assert.That(visibility.Variance, Is.GreaterThanOrEqualTo(0.005f));
            });
        }

        [Test]
        public void CoverageSupportComputation_SeparatesSpatialSupportAndVisibleWeights()
        {
            DdgiCoverage unsupported = ComputeCoverage(
                expectedWeight: 1.0f,
                spatialWeight: 1.0f,
                supportedWeight: 0.0f,
                visibleWeight: 0.0f,
                edgeFade: 1.0f);
            DdgiCoverage supported = ComputeCoverage(
                expectedWeight: 1.0f,
                spatialWeight: 1.0f,
                supportedWeight: 0.75f,
                visibleWeight: 0.5f,
                edgeFade: 0.8f);

            Assert.Multiple(() =>
            {
                Assert.That(unsupported.Spatial, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(unsupported.Support, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(unsupported.Data, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(supported.Spatial, Is.EqualTo(0.8f).Within(0.0001f));
                Assert.That(supported.Support, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(supported.Data, Is.EqualTo(0.4f).Within(0.0001f));
            });
        }

        [Test]
        public void CandidateOwnership_UnsupportedCandidateDoesNotConsumeRemainingCoverage()
        {
            float remainingCoverage = 1.0f;
            float unsupportedBlend = AccumulateCandidateOwnership(
                supportCoverage: 0.0f,
                dataConfidence: 1.0f,
                ref remainingCoverage);
            float supportedBlend = AccumulateCandidateOwnership(
                supportCoverage: 0.8f,
                dataConfidence: 1.0f,
                ref remainingCoverage);

            Assert.Multiple(() =>
            {
                Assert.That(unsupportedBlend, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(supportedBlend, Is.EqualTo(0.8f).Within(0.0001f));
                Assert.That(remainingCoverage, Is.EqualTo(0.2f).Within(0.0001f));
            });
        }

        [Test]
        public void ResolveAccumulation_NormalizesSupportCoverageByOwnership()
        {
            float supportCoverage = 0.35f;
            float dataConfidence = 0.55f;
            float remainingCoverage = 1.0f;
            float blendWeight = AccumulateCandidateOwnership(supportCoverage, dataConfidence, ref remainingCoverage);
            float resolvedSupport = ResolveSupportCoverage(supportCoverage * blendWeight, blendWeight);

            Assert.That(resolvedSupport, Is.EqualTo(supportCoverage).Within(0.0001f));
        }

        [Test]
        public void CacheReadiness_MatchesShaderWarmupRamp()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CacheReadiness(cacheGeneration: 0, DdgiRuntimeWarmupState.ColdStart), Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.Disabled), Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.ColdStart), Is.EqualTo(0.35f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.LocalVolumeWarmup), Is.EqualTo(0.65f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.NearCascadeWarmup), Is.EqualTo(0.85f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.Recovery), Is.EqualTo(0.75f).Within(0.0001f));
                Assert.That(CacheReadiness(cacheGeneration: 1, DdgiRuntimeWarmupState.SteadyState), Is.EqualTo(1.0f).Within(0.0001f));
            });
        }

        [Test]
        public void FallbackWeighting_PreventsHighSpatialZeroSupportBlackout()
        {
            DdgiFallbackComposition composition = ComposeFallback(
                spatialCoverage: 1.0f,
                supportCoverage: 0.0f,
                dataConfidence: 0.0f,
                environmentFallbackIntensity: 0.65f);

            Assert.Multiple(() =>
            {
                Assert.That(composition.DdgiTrust, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(composition.EnvironmentFallbackWeight, Is.EqualTo(0.65f).Within(0.0001f));
                Assert.That(composition.IsBlackout, Is.False);
            });
        }

        private static DdgiVisibility EvaluateDdgiVisibility(
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
                return new DdgiVisibility(1.0f, safeMean, variance);

            float delta = probeDistance - safeMean;
            float transport = Math.Clamp(variance / (variance + delta * delta), 0.0f, 1.0f);
            return new DdgiVisibility(transport, safeMean, variance);
        }

        private static DdgiCoverage ComputeCoverage(
            float expectedWeight,
            float spatialWeight,
            float supportedWeight,
            float visibleWeight,
            float edgeFade)
        {
            float safeExpectedWeight = Math.Max(expectedWeight, 0.000001f);
            return new DdgiCoverage(
                Clamp01(spatialWeight / safeExpectedWeight) * edgeFade,
                Clamp01(supportedWeight / safeExpectedWeight) * edgeFade,
                Clamp01(visibleWeight / safeExpectedWeight) * edgeFade);
        }

        private static float AccumulateCandidateOwnership(
            float supportCoverage,
            float dataConfidence,
            ref float remainingCoverage)
        {
            float candidateOwnership = Clamp01(supportCoverage) * SmoothStep(0.02f, 0.25f, Clamp01(dataConfidence));
            if (candidateOwnership <= 0.000001f)
                return 0.0f;

            float blendWeight = Math.Clamp(candidateOwnership * remainingCoverage, 0.0f, remainingCoverage);
            remainingCoverage = Clamp01(remainingCoverage - blendWeight);
            return blendWeight;
        }

        private static float ResolveSupportCoverage(float blendedSupportCoverage, float totalOwnership)
        {
            if (totalOwnership <= 0.000001f)
                return 0.0f;

            float invOwnership = 1.0f / Math.Max(totalOwnership, 0.000001f);
            return Clamp01(blendedSupportCoverage * invOwnership);
        }

        private static float CacheReadiness(uint cacheGeneration, DdgiRuntimeWarmupState warmupState)
        {
            if (cacheGeneration == 0)
                return 0.0f;

            return warmupState switch
            {
                DdgiRuntimeWarmupState.Disabled => 0.0f,
                DdgiRuntimeWarmupState.ColdStart => 0.35f,
                DdgiRuntimeWarmupState.LocalVolumeWarmup => 0.65f,
                DdgiRuntimeWarmupState.NearCascadeWarmup => 0.85f,
                DdgiRuntimeWarmupState.Recovery => 0.75f,
                _ => 1.0f
            };
        }

        private static DdgiFallbackComposition ComposeFallback(
            float spatialCoverage,
            float supportCoverage,
            float dataConfidence,
            float environmentFallbackIntensity)
        {
            _ = Clamp01(spatialCoverage);
            float ddgiTrust = Clamp01(supportCoverage) * SmoothStep(0.02f, 0.25f, Clamp01(dataConfidence));
            float fallbackWeight = Math.Clamp((1.0f - ddgiTrust) * environmentFallbackIntensity, 0.0f, 4.0f);
            return new DdgiFallbackComposition(ddgiTrust, fallbackWeight, ddgiTrust <= 0.0f && fallbackWeight <= 0.0f);
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3.0f - 2.0f * t);
        }

        private static float Clamp01(float value) => Math.Clamp(value, 0.0f, 1.0f);

        private readonly record struct DdgiVisibility(float Transport, float Mean, float Variance);
        private readonly record struct DdgiCoverage(float Spatial, float Support, float Data);
        private readonly record struct DdgiFallbackComposition(float DdgiTrust, float EnvironmentFallbackWeight, bool IsBlackout);
    }
}
