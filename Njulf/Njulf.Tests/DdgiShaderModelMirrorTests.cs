using System;
using System.IO;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiShaderModelMirrorTests
    {
        [Test]
        public void ForwardShader_DdgiVisibilityUsesProbeSpacingVarianceAndSeparateConfidenceRamp()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string visibility = ExtractFunction(shader, "float EvaluateDdgiVisibility(");
            string confidence = ExtractFunction(shader, "float DdgiVisibilityConfidence(");
            string momentTrust = ExtractFunction(shader, "float DdgiVisibilityMomentTrust(");

            Assert.Multiple(() =>
            {
                Assert.That(visibility, Does.Contain("float minVariance = max(0.005, minProbeSpacing * minProbeSpacing * 0.0025);"));
                Assert.That(visibility, Does.Contain("if (probeDistance <= mean + max(viewBias, 0.02))"));
                Assert.That(visibility, Does.Contain("return clamp(variance / (variance + delta * delta), 0.0, 1.0);"));
                Assert.That(confidence, Does.Contain("smoothstep(0.02, 0.40, clamp(visibilityTransport, 0.0, 1.0))"));
                Assert.That(momentTrust, Does.Contain("smoothstep(0.05, 0.20, clamp(visibilityConfidence, 0.0, 1.0))"));
            });
        }

        [Test]
        public void ForwardShader_CandidateOwnershipDoesNotSpendCoverageOnUnsupportedCandidates()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string sampleVolume = ExtractFunction(shader, "DdgiSampleResult SampleDdgiVolumeIrradiance(");
            string accumulateCandidate = ExtractFunction(shader, "float AccumulateDdgiCandidate(");
            string resolveAccumulation = ExtractFunction(shader, "DdgiSampleResult ResolveDdgiAccumulation(");

            Assert.Multiple(() =>
            {
                Assert.That(sampleVolume, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence;"));
                Assert.That(sampleVolume, Does.Contain("float radianceWeight = supportWeight * qualityConfidence;"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateSupport = clamp(candidate.supportCoverage, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateData = clamp(candidate.weight, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData);"));
                Assert.That(accumulateCandidate, Does.Not.Contain("float candidateOwnership = candidateSupport * smoothstep(0.02, 0.25, candidateData);"));
                Assert.That(accumulateCandidate, Does.Contain("if (candidateOwnership <= 0.000001)"));
                Assert.That(accumulateCandidate, Does.Contain("return -1.0;"));
                Assert.That(accumulateCandidate, Does.Contain("remainingOwnership = clamp(remainingOwnership - blendWeight, 0.0, 1.0);"));
                Assert.That(resolveAccumulation, Does.Contain("result.supportCoverage = clamp(blendedSupportCoverage * invOwnership, 0.0, 1.0);"));
                Assert.That(resolveAccumulation, Does.Contain("result.ownershipConsumed = clamp(totalOwnership, 0.0, 1.0);"));
            });
        }

        [Test]
        public void ForwardShader_CompositionUsesThinWallLeakControlsAndKeepsFallbackAvailable()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string compose = ExtractFunction(shader, "HybridDiffuseGiResult ComposeHybridDiffuseGi(");

            Assert.Multiple(() =>
            {
                Assert.That(compose, Does.Contain("float thinWallLeakClampStrength = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 14u), 0.0, 1.0);"));
                Assert.That(compose, Does.Contain("float thinWallProxyThickness = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 15u), 0.0, 1.0);"));
                Assert.That(compose, Does.Contain("float leakStrength = clamp(thinWallLeakClampStrength * mix(0.35, 0.85, clamp(thinWallProxyThickness * 8.0, 0.0, 1.0)), 0.0, 0.85);"));
                Assert.That(compose, Does.Contain("float leakAttenuation = clamp(mix(1.0, visibilityTransport, leakStrength), 0.05, 1.0);"));
                Assert.That(compose, Does.Contain("float dataTrust = DdgiSparseDataTrust(dataConfidence);"));
                Assert.That(compose, Does.Contain("float effectiveEnvironmentFallbackIntensity = max(environmentFallbackIntensity, warmupFallbackFloor);"));
                Assert.That(compose, Does.Contain("float environmentFallbackWeight = clamp(environmentTrust * effectiveEnvironmentFallbackIntensity, 0.0, 4.0);"));
                Assert.That(compose, Does.Not.Contain("float environmentFallbackWeight = clamp(environmentTrust * environmentFallbackIntensity, 0.0, 4.0);"));
                Assert.That(compose, Does.Contain("result.diffuse = SafeRadiance(environmentFallbackField * indirectAoWeight);"));
                Assert.That(compose, Does.Contain("result.diffuse = SafeRadiance(ddgiLowFrequencyField + (environmentFallbackField + nearField) * indirectAoWeight);"));
                Assert.That(compose, Does.Not.Contain("environmentFallbackWeight = clamp((1.0 - ddgiLowFrequencyCoverage) * indirectAo"));
                Assert.That(compose, Does.Not.Contain("environmentFallbackWeight = clamp((1.0 - effectiveDdgiWeight) * indirectAo"));
            });
        }

        [Test]
        public void DdgiUpdateShader_ClassificationKeepsSoftInvalidProbesActiveAndFloorsClipmaps()
        {
            string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain("float softInvalidProbeScore = max("));
                Assert.That(shader, Does.Contain("smoothstep(0.25, 0.45, closeRatio)"));
                Assert.That(shader, Does.Contain("float hardInvalidProbeScore = max("));
                Assert.That(shader, Does.Contain("smoothstep(0.70, 0.90, closeRatio)"));
                Assert.That(shader, Does.Contain("float hardInvalid = smoothstep(0.75, 0.95, hardInvalidProbeScore);"));
                Assert.That(shader, Does.Contain("float clipmapActiveFloor = volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE ? 0.0 : 0.35;"));
                Assert.That(shader, Does.Contain("float targetActiveProbe = classificationEnabled ? max(1.0 - hardInvalid, clipmapActiveFloor) : 1.0;"));
                Assert.That(shader, Does.Contain("float activeBlendAlpha = targetActiveProbe > previousActiveProbe"));
                Assert.That(shader, Does.Contain("? max(stateBlendAlpha, 0.35)"));
                Assert.That(shader, Does.Contain("float activeProbe = mix(previousActiveProbe, targetActiveProbe, activeBlendAlpha);"));
                Assert.That(shader, Does.Contain("float confidencePenalty = classificationEnabled ? 1.0 - softInvalid * 0.75 : 1.0;"));
                Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * luminanceConfidence, 0.0, 1.0);"));
                Assert.That(shader, Does.Not.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
                Assert.That(shader, Does.Contain("float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5) * confidencePenalty, 0.0, 1.0);"));
            });
        }

        private static string ReadRepoText(params string[] pathParts)
        {
            string? directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException("Could not locate repository file.", Path.Combine(pathParts));
        }

        private static string ExtractFunction(string source, string signature)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex < 0)
                throw new InvalidOperationException($"Could not find '{signature}'.");

            int bodyStart = source.IndexOf('{', signatureIndex);
            if (bodyStart < 0)
                throw new InvalidOperationException($"Could not find body for '{signature}'.");

            int depth = 0;
            for (int i = bodyStart; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source[signatureIndex..(i + 1)];
                }
            }

            throw new InvalidOperationException($"Could not find end of body for '{signature}'.");
        }
    }
}
