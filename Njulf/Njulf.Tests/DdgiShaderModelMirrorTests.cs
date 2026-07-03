using System;
using System.IO;
using System.Numerics;
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
                Assert.That(sampleVolume, Does.Contain("float atlasDataTrust = confidenceBypass ? 1.0 : DdgiSparseDataTrust(irradianceConfidence);"));
                Assert.That(sampleVolume, Does.Contain("float radianceTransportTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(rayHitConfidence, 0.35);"));
                Assert.That(sampleVolume, Does.Contain("float stateIrradianceTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(max(stateIrradianceConfidence, irradianceConfidence), 0.45);"));
                Assert.That(sampleVolume, Does.Contain("float qualityConfidence = clamp(radianceTransportTrust * stateIrradianceTrust, 0.0, 1.0);"));
                Assert.That(sampleVolume, Does.Not.Contain("float transportConfidence = clamp(rayHitConfidence + visibilityConfidence, 0.0, 1.0);"));
                Assert.That(sampleVolume, Does.Not.Contain("float qualityConfidence = clamp(radianceTransportConfidence * max(stateIrradianceConfidence, irradianceConfidence), 0.0, 1.0);"));
                Assert.That(sampleVolume, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * atlasDataTrust;"));
                Assert.That(sampleVolume, Does.Contain("float radianceWeight = supportWeight * qualityConfidence;"));
                Assert.That(sampleVolume, Does.Contain("float visibilityWeight = max(visibilityAttenuation, 0.03);"));
                Assert.That(sampleVolume, Does.Contain("float visibleRadianceWeight = radianceWeight * visibilityWeight;"));
                Assert.That(sampleVolume, Does.Contain("accumulated += clamp(probeIrradiance, vec3(0.0), vec3(64.0)) * visibleRadianceWeight;"));
                Assert.That(sampleVolume, Does.Contain("totalWeight += visibleRadianceWeight;"));
                Assert.That(sampleVolume, Does.Contain("dataWeightSum += visibleRadianceWeight;"));
                Assert.That(sampleVolume, Does.Not.Contain("float visibleRadianceWeight = radianceWeight * visibilityAttenuation;"));
                Assert.That(sampleVolume, Does.Not.Contain("totalWeight += radianceWeight;"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateSupport = clamp(candidate.supportCoverage, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateData = clamp(candidate.weight, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("vec3 probeSamplePosition = DdgiSurfaceProbeSamplePosition(info, worldPosition, normal);"));
                Assert.That(accumulateCandidate, Does.Contain("if (ReadDdgiVolumeSampleInfo(volumeIndex, probeSamplePosition, biasedInfo))"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateBlendWeight,"));
                Assert.That(accumulateCandidate, Does.Contain("candidateBlendWeight = clamp(candidateBlendWeight, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData) * candidateBlendWeight;"));
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
                Assert.That(compose, Does.Contain("bool confidenceBypass = DdgiDebugBypassConfidenceSuppression(debugViewMode);"));
                Assert.That(compose, Does.Contain("float dataTrust = confidenceBypass && dataConfidence > 0.000001"));
                Assert.That(compose, Does.Contain(": DdgiSparseDataTrust(dataConfidence);"));
                Assert.That(compose, Does.Contain("float ddgiTrust = clamp(supportTrust * leakAttenuation, 0.0, 1.0);"));
                Assert.That(compose, Does.Contain("float environmentTrust = clamp(1.0 - supportTrust, 0.0, 1.0);"));
                Assert.That(compose, Does.Not.Contain("float environmentTrust = clamp(1.0 - ddgiTrust, 0.0, 1.0);"));
                Assert.That(compose, Does.Contain("? (1.0 - cacheReadiness) * (1.0 - supportTrust)"));
                Assert.That(compose, Does.Not.Contain("? (1.0 - cacheReadiness) * (1.0 - dataTrust)"));
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
        public void ForwardShader_ConfidenceBypassDoesNotBypassVisibilityOrLeakAttenuation()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string sampleVolume = ExtractFunction(shader, "DdgiSampleResult SampleDdgiVolumeIrradiance(");
            string compose = ExtractFunction(shader, "HybridDiffuseGiResult ComposeHybridDiffuseGi(");
            string sparseTrust = ExtractFunction(shader, "float DdgiSparseDataTrust(");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS = 119u"));
                Assert.That(shader, Does.Contain("bool DdgiDebugBypassConfidenceSuppression(uint debugViewMode)"));
                Assert.That(shader, Does.Contain("return debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS;"));
                Assert.That(sparseTrust, Does.Contain("if (confidence <= 0.000001)"));
                Assert.That(sparseTrust, Does.Contain("return DdgiSoftConfidenceTrust(confidence, 0.35);"));
                Assert.That(sampleVolume, Does.Contain("float atlasDataTrust = confidenceBypass ? 1.0 : DdgiSparseDataTrust(irradianceConfidence);"));
                Assert.That(sampleVolume, Does.Contain("float radianceTransportTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(rayHitConfidence, 0.35);"));
                Assert.That(sampleVolume, Does.Contain("float stateIrradianceTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(max(stateIrradianceConfidence, irradianceConfidence), 0.45);"));
                Assert.That(sampleVolume, Does.Contain("float visibilityTrust = DdgiVisibilityMomentTrust(visibilityConfidence);"));
                Assert.That(sampleVolume, Does.Contain("float visibilityWeight = max(visibilityAttenuation, 0.03);"));
                Assert.That(sampleVolume, Does.Contain("float visibleRadianceWeight = radianceWeight * visibilityWeight;"));
                Assert.That(compose, Does.Contain("float leakAttenuation = clamp(mix(1.0, visibilityTransport, leakStrength), 0.05, 1.0);"));
                Assert.That(compose, Does.Contain("float ddgiTrust = clamp(supportTrust * leakAttenuation, 0.0, 1.0);"));
                Assert.That(compose, Does.Contain("result.nearContactSuppression = 1.0 - leakAttenuation;"));
                Assert.That(compose, Does.Not.Contain("leakAttenuation = 1.0"));
                Assert.That(compose, Does.Not.Contain("visibilityAttenuation = 1.0"));
            });
        }

        [Test]
        public void DdgiShaders_LockProductionRadianceIrradianceConvention()
        {
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string update = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
            string sampleDiffuse = ExtractFunction(forward, "vec3 SampleDdgiDiffuse(");
            string sampleVolume = ExtractFunction(forward, "DdgiSampleResult SampleDdgiVolumeIrradiance(");
            string traceEnergy = ExtractFunction(update, "void RecordDdgiTraceEnergyDiagnostics(");
            string directLight = ExtractFunction(update, "vec3 EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(");
            string stableDiffuse = ExtractFunction(update, "vec3 EvaluateStableDdgiDiffuseRadianceAtHit(");

            Assert.Multiple(() =>
            {
                Assert.That(sampleDiffuse, Does.Contain("return ddgi.irradiance * (albedo / PI) * diffuseWeight;"));
                Assert.That(sampleVolume, Does.Contain("float finalIntensity = globalIntensity * info.volumeIntensity;"));
                Assert.That(sampleVolume, Does.Not.Contain("albedo / PI"));
                Assert.That(update, Does.Contain("vec3 probeRayRadiance = radiance;"));
                Assert.That(update, Does.Not.Contain("vec3 sampleIrradiance = DdgiRawAtlasRadianceConventionEnabled()"));
                Assert.That(update, Does.Not.Contain("atlasRadianceScale"));
                Assert.That(update, Does.Not.Contain("rawIrradiance / globalIntensity"));
                Assert.That(update, Does.Contain("return clamp(sampledIrradiance, vec3(0.0), vec3(64.0));"));
                Assert.That(directLight, Does.Contain("noShadowDiffuse = incomingRadiance * nDotL * (albedo / PI);"));
                Assert.That(stableDiffuse, Does.Contain("return stableIrradiance * (albedo / PI);"));
                Assert.That(traceEnergy, Does.Not.Contain("albedo / PI"));
            });
        }

        [Test]
        public void DdgiUpdateShader_UsesVarianceAwareHistoryAndHalfSafeAtlasWrites()
        {
            string update = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
            string schedule = ReadRepoText("Njulf.Shaders", "ddgi_schedule_score.comp");
            string writeIrradiance = ExtractFunction(update, "void WriteProbeIrradianceAtlasTexel(");
            string history = ExtractFunction(update, "vec4 ResolveDdgiIrradianceHistory(");
            string firefly = ExtractFunction(update, "vec3 ApplyDdgiIrradianceFireflySuppression(");

            Assert.Multiple(() =>
            {
                Assert.That(update, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_MAX = 256.0;"));
                Assert.That(update, Does.Contain("const float DDGI_HALF_FLOAT_MAX = 65504.0;"));
                Assert.That(update, Does.Contain("float ResolveDdgiIrradianceBlendAlpha(float baseBlendAlpha, uint flags, float inconsistency)"));
                Assert.That(update, Does.Contain("float ResolveDdgiVisibilityBlendAlpha(float baseBlendAlpha, uint flags)"));
                Assert.That(update, Does.Contain("float catchUpResponse = mix(0.0, 0.35, smoothstep(0.20, 0.60, inconsistency));"));
                Assert.That(update, Does.Not.Contain("float catchUpResponse = mix(0.0, 0.55, smoothstep(0.10, 0.60, inconsistency));"));
                Assert.That(history, Does.Contain("float longResponse = historyValid > 0.5 ? 0.04 : 1.0;"));
                Assert.That(history, Does.Contain("float shortResponse = historyValid > 0.5 ? 0.35 : 1.0;"));
                Assert.That(history, Does.Contain("float meanDelta = abs(shortMean - longMean) / max(max(shortMean, longMean), 0.05);"));
                Assert.That(history, Does.Contain("float instantaneousDelta = abs(currentLuminance - previousShortMean) / max(max(currentLuminance, previousShortMean), 0.05);"));
                Assert.That(history, Does.Contain("? max(meanDelta, previousInconsistency * 0.5)"));
                Assert.That(history, Does.Not.Contain("? max(max(meanDelta, instantaneousDelta), previousInconsistency * 0.65)"));
                Assert.That(history, Does.Contain("return vec4(longMean, shortMean, clamp(inconsistency, 0.0, 1.0), historyValid > 0.5 ? instantaneousDelta : 0.0);"));
                Assert.That(update, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 17u, irradianceHistory.x);"));
                Assert.That(update, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 18u, irradianceHistory.y);"));
                Assert.That(update, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 19u, luminanceInconsistency);"));
                Assert.That(writeIrradiance, Does.Contain("SanitizeDdgiIrradianceAtlasSample(irradianceSample);"));
                Assert.That(writeIrradiance, Does.Contain("ApplyDdgiIrradianceFireflySuppression(safePrevious.rgb, safeCurrent.rgb, historyValid, suppressed);"));
                Assert.That(writeIrradiance, Does.Contain("DDGI_BLEND_ENERGY_NONFINITE_IRRADIANCE_COUNTER"));
                Assert.That(writeIrradiance, Does.Contain("DDGI_BLEND_ENERGY_FIREFLY_SUPPRESSED_COUNTER"));
                Assert.That(firefly, Does.Contain("float luminanceLimit = max(previousLuminance * 8.0, 16.0);"));
                Assert.That(schedule, Does.Contain("float luminanceChange = clamp(stateHistory.z, 0.0, 1.0);"));
                Assert.That(schedule, Does.Contain("float luminanceInconsistency = max(luminanceChange, storedInconsistency);"));
                Assert.That(schedule, Does.Contain("uint probeAge = constants.FrameSerial - lastUpdateFrame;"));
                Assert.That(schedule, Does.Contain("bool highVarianceProbe = !newProbe && visibleProbe && probeAge >= 2u && luminanceInconsistency > 0.35;"));
                Assert.That(schedule, Does.Contain("float varianceBoost = mix(1.25, 1.5, clamp((luminanceInconsistency - 0.35) / 0.65, 0.0, 1.0));"));
                Assert.That(schedule, Does.Not.Contain("bool highVarianceProbe = !newProbe && visibleProbe && luminanceInconsistency > 0.25;"));
                Assert.That(schedule, Does.Not.Contain("float varianceBoost = mix(1.25, 1.75, clamp((luminanceInconsistency - 0.25) / 0.75, 0.0, 1.0));"));
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

        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        public void DdgiSphericalFibonacciDirections_AreUnitLengthAndCosineNormalizeToPi(int sampleCount)
        {
            const double tolerance = Math.PI * 0.05;
            double weightedCosineSum = 0.0;
            double solidAngle = 4.0 * Math.PI / sampleCount;

            for (int i = 0; i < sampleCount; i++)
            {
                Vector3 direction = DdgiSphericalFibonacci(i, sampleCount);
                Assert.That(direction.Length(), Is.EqualTo(1.0f).Within(1.0e-5f), $"Direction {i} for N={sampleCount}");
                weightedCosineSum += Math.Max(direction.Z, 0.0f) * solidAngle;
            }

            Assert.That(weightedCosineSum, Is.EqualTo(Math.PI).Within(tolerance));
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

        private static Vector3 DdgiSphericalFibonacci(int index, int count)
        {
            double sampleCount = Math.Max(count, 1);
            double sampleIndex = Math.Min(index, sampleCount - 1.0);
            double z = 1.0 - 2.0 * ((sampleIndex + 0.5) / sampleCount);
            double radius = Math.Sqrt(Math.Max(1.0 - z * z, 0.0));
            double phi = sampleIndex * 2.39996322972865332;
            return new Vector3(
                (float)(Math.Cos(phi) * radius),
                (float)(Math.Sin(phi) * radius),
                (float)z);
        }
    }
}
