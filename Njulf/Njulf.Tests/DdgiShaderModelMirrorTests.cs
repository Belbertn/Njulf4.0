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
        public void DdgiRoundedBoxEdgeFade_PreservesPerAxisCoverageWithMildCornerRounding()
        {
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string common = ReadRepoText("Njulf.Shaders", "common.glsl");
            string update = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

            static float RoundedBoxEdgeFade(Vector3 edgeDistance, Vector3 blendDistance)
            {
                Vector3 safeBlendDistance = Vector3.Max(blendDistance, new Vector3(0.0001f));
                Vector3 axisFade = Vector3.Clamp(edgeDistance / safeBlendDistance, Vector3.Zero, Vector3.One);
                float perAxisFade = Math.Min(axisFade.X, Math.Min(axisFade.Y, axisFade.Z));
                float cornerPressure = Math.Clamp((Vector3.One - axisFade).Length() * 0.70710678f, 0.0f, 1.0f);
                float roundedBoxFade = perAxisFade * (1.0f + ((1.0f - cornerPressure * 0.25f) - 1.0f) * perAxisFade);
                return Math.Clamp(roundedBoxFade, 0.0f, 1.0f);
            }

            float boundary = RoundedBoxEdgeFade(Vector3.Zero, Vector3.One);
            float interior = RoundedBoxEdgeFade(Vector3.One, Vector3.One);
            float singleAxisHalf = RoundedBoxEdgeFade(new Vector3(0.5f, 1.0f, 1.0f), Vector3.One);
            float cornerHalf = RoundedBoxEdgeFade(new Vector3(0.5f, 0.5f, 1.0f), Vector3.One);

            Assert.Multiple(() =>
            {
                Assert.That(forward, Does.Contain("float ResolveDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
                Assert.That(common, Does.Contain("float ResolveDdgiAmbientRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
                Assert.That(update, Does.Contain("float ResolveStableDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
                Assert.That(boundary, Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(interior, Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(singleAxisHalf, Is.GreaterThan(0.45f));
                Assert.That(cornerHalf, Is.LessThan(singleAxisHalf));
                Assert.That(cornerHalf, Is.GreaterThan(0.40f));
            });
        }

        [Test]
        public void ForwardShader_DdgiVisibilityUsesProbeSpacingVarianceAndSeparateConfidenceRamp()
        {
            string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
            string visibility = ExtractFunction(shader, "float EvaluateDdgiVisibility(");
            string confidence = ExtractFunction(shader, "float DdgiVisibilityConfidence(");
            string momentTrust = ExtractFunction(shader, "float DdgiVisibilityMomentTrust(");

            Assert.Multiple(() =>
            {
                Assert.That(visibility, Does.Contain("mean = max(moments.x, 0.0);"));
                Assert.That(visibility, Does.Not.Contain("mean = max(moments.x, 0.0001);"));
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
                Assert.That(sampleVolume, Does.Contain("float cellWeight = clamp(trilinear.x * trilinear.y * trilinear.z * 2.0, 0.0, 1.0);"));
                Assert.That(sampleVolume, Does.Contain("float normalWeight = max(DdgiSquare(clamp(alignment * 0.5 + 0.5, 0.0, 1.0)), 0.1);"));
                Assert.That(sampleVolume, Does.Contain("float visibilityLeakFloor = mix(0.005, 0.05, probeVisibilityConfidence);"));
                Assert.That(sampleVolume, Does.Contain("float visibilityWeight = max(visibilityAttenuation * visibilityAttenuation * visibilityAttenuation, visibilityLeakFloor);"));
                Assert.That(sampleVolume, Does.Contain("float visibleRadianceWeight = ShapeDdgiGatherWeight(radianceWeight * visibilityWeight);"));
                Assert.That(sampleVolume, Does.Contain("float visibleSupportWeight = supportWeight * mix(0.05, 1.0, probeVisibilityConfidence);"));
                Assert.That(sampleVolume, Does.Contain("accumulated += clamp(probeIrradiance, vec3(0.0), vec3(64.0)) * visibleRadianceWeight;"));
                Assert.That(sampleVolume, Does.Contain("totalWeight += visibleRadianceWeight;"));
                Assert.That(sampleVolume, Does.Contain("dataWeightSum += visibleSupportWeight * qualityConfidence;"));
                Assert.That(sampleVolume, Does.Contain("visibilityWeightedSupport += visibleSupportWeight * visibilityAttenuation;"));
                Assert.That(sampleVolume, Does.Not.Contain("float visibleRadianceWeight = radianceWeight * visibilityAttenuation;"));
                Assert.That(sampleVolume, Does.Not.Contain("totalWeight += radianceWeight;"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateSupport = clamp(candidate.supportCoverage, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateData = clamp(candidate.weight, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("vec3 probeSamplePosition = DdgiSurfaceProbeSamplePosition(info, worldPosition, normal);"));
                Assert.That(accumulateCandidate, Does.Contain("if (ReadDdgiVolumeSampleInfo(volumeIndex, probeSamplePosition, biasedInfo))"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateBlendWeight,"));
                Assert.That(accumulateCandidate, Does.Contain("candidateBlendWeight = clamp(candidateBlendWeight, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateVisibility = clamp(candidate.leakClamp, 0.0, 1.0);"));
                Assert.That(accumulateCandidate, Does.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData) * mix(0.10, 1.0, candidateVisibility) * candidateBlendWeight;"));
                Assert.That(accumulateCandidate, Does.Not.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData) * candidateBlendWeight;"));
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
                Assert.That(sampleVolume, Does.Contain("float visibilityLeakFloor = mix(0.005, 0.05, probeVisibilityConfidence);"));
                Assert.That(sampleVolume, Does.Contain("float visibilityWeight = max(visibilityAttenuation * visibilityAttenuation * visibilityAttenuation, visibilityLeakFloor);"));
                Assert.That(sampleVolume, Does.Contain("float visibleRadianceWeight = ShapeDdgiGatherWeight(radianceWeight * visibilityWeight);"));
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
            string common = ReadRepoText("Njulf.Shaders", "common.glsl");
            string schedule = ReadRepoText("Njulf.Shaders", "ddgi_schedule_score.comp");
            string writeIrradiance = ExtractFunction(update, "void WriteProbeIrradianceAtlasTexel(");
            string history = ExtractFunction(update, "vec4 ResolveDdgiIrradianceHistory(");
            string firefly = ExtractFunction(update, "vec3 ApplyDdgiIrradianceFireflySuppression(");
            string asymmetricBlend = ExtractFunction(update, "float ResolveDdgiAsymmetricIrradianceBlendAlpha(");

            Assert.Multiple(() =>
            {
                Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_MAX = 64.0;"));
                Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_GAMMA = 5.0;"));
                Assert.That(update, Does.Contain("const float DDGI_HALF_FLOAT_MAX = 65504.0;"));
                Assert.That(update, Does.Contain("float ResolveDdgiIrradianceBlendAlpha(float baseBlendAlpha, uint flags, float inconsistency)"));
                Assert.That(update, Does.Contain("float ResolveDdgiIrradianceReasonBlendFloor(uint flags)"));
                Assert.That(update, Does.Contain("float ResolveDdgiVisibilityBlendAlpha(float baseBlendAlpha, uint flags)"));
                Assert.That(update, Does.Contain("float catchUpResponse = mix(0.0, 0.35, smoothstep(0.20, 0.60, inconsistency));"));
                Assert.That(update, Does.Contain("response = max(response, ResolveDdgiIrradianceReasonBlendFloor(flags));"));
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
                Assert.That(writeIrradiance, Does.Contain("SanitizeDdgiEncodedIrradianceAtlasSample(previous);"));
                Assert.That(writeIrradiance, Does.Contain("vec4 safePreviousLinear = ResolveDdgiIrradianceAtlasSqrtBlend(DecodeDdgiIrradianceAtlasSqrtSample(safePrevious));"));
                Assert.That(writeIrradiance, Does.Contain("ApplyDdgiIrradianceFireflySuppression(safePreviousLinear.rgb, safeCurrent.rgb, historyValid, suppressed);"));
                Assert.That(writeIrradiance, Does.Contain("vec4 encodedCurrent = vec4(EncodeDdgiIrradianceAtlasRgb(safeCurrent.rgb), safeCurrent.w);"));
                Assert.That(writeIrradiance, Does.Contain("float asymmetricBlendAlpha = ResolveDdgiAsymmetricIrradianceBlendAlpha("));
                Assert.That(writeIrradiance, Does.Contain("WritePackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u, mix(safePrevious, encodedCurrent, asymmetricBlendAlpha));"));
                Assert.That(asymmetricBlend, Does.Contain("float changeAttention = smoothstep(0.02, 0.35, relativeDelta);"));
                Assert.That(asymmetricBlend, Does.Contain("float reasonFloor = ResolveDdgiIrradianceReasonBlendFloor(flags);"));
                Assert.That(asymmetricBlend, Does.Contain("max(response, 1.0 / 1024.0)"));
                Assert.That(asymmetricBlend, Does.Contain("float brighteningDamping = mix(1.0, 0.5, changeAttention);"));
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
                Assert.That(schedule, Does.Contain("float historicalHitRatio = ReadDdgiScheduleHistoricalHitRatio(probeIndex);"));
                Assert.That(schedule, Does.Contain("bool geometryProximateProbe = historicalHitRatio > 0.02;"));
                Assert.That(schedule, Does.Contain("uint geometryVisibleReserveDivisor = ResolveDdgiGeometryProximateLaneDivisor(visibleReserveDivisor, historicalHitRatio);"));
                Assert.That(schedule, Does.Contain("bool visibleHotProbe = visibleProbe && (localAuthoredProbe || lowConfidenceProbe || highVarianceProbe || (cascade0Probe && geometryProximateProbe));"));
            });
        }

        [Test]
        public void DdgiUpdateShader_ClassificationKeepsSoftInvalidProbesActiveAndFloorsClipmaps()
        {
            string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain("Triangle winding is not reliable probe-validity evidence for production scenes"));
                Assert.That(shader, Does.Contain("float softInvalidProbeScore = smoothstep(0.25, 0.45, closeRatio);"));
                Assert.That(shader, Does.Contain("smoothstep(0.25, 0.45, closeRatio)"));
                Assert.That(shader, Does.Contain("float hardInvalidProbeScore = smoothstep(0.70, 0.90, closeRatio);"));
                Assert.That(shader, Does.Contain("smoothstep(0.70, 0.90, closeRatio)"));
                Assert.That(shader, Does.Contain("float hardInvalid = smoothstep(0.75, 0.95, hardInvalidProbeScore);"));
                Assert.That(shader, Does.Contain("float clipmapActiveFloor = volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE ? 0.0 : 0.35;"));
                Assert.That(shader, Does.Contain("float targetActiveProbe = classificationEnabled ? max(1.0 - hardInvalid, clipmapActiveFloor) : 1.0;"));
                Assert.That(shader, Does.Contain("float activeBlendAlpha = targetActiveProbe > previousActiveProbe"));
                Assert.That(shader, Does.Contain("? max(stateBlendAlpha, 0.35)"));
                Assert.That(shader, Does.Contain("float activeProbe = mix(previousActiveProbe, targetActiveProbe, activeBlendAlpha);"));
                Assert.That(shader, Does.Contain("float confidencePenalty = classificationEnabled ? 1.0 - softInvalid * 0.75 : 1.0;"));
                Assert.That(shader, Does.Contain("float traceSampleConfidence = clamp(hitRatio + missRatio * 0.35, 0.0, 1.0);"));
                Assert.That(shader, Does.Contain("float rayHitConfidence = clamp(mix(0.35, 1.0, traceSampleConfidence) * confidencePenalty, 0.0, 1.0);"));
                Assert.That(shader, Does.Not.Contain("float rayHitConfidence = clamp(mix(0.35, 1.0, traceSampleConfidence) * (1.0 - backfaceRatio) * confidencePenalty, 0.0, 1.0);"));
                Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * luminanceConfidence, 0.0, 1.0);"));
                Assert.That(shader, Does.Not.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
                Assert.That(shader, Does.Contain("float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5) * confidencePenalty, 0.0, 1.0);"));
                Assert.That(shader, Does.Contain("uint ResolveDdgiInactiveProbeFallback("));
                Assert.That(shader, Does.Contain("uint fallbackProbeIndex = ResolveDdgiInactiveProbeFallback(volumeIndex, request.LogicalCell, probeIndex, activeProbe);"));
                Assert.That(shader, Does.Contain("PackDdgiFallbackProbeIndex(fallbackProbeIndex)"));
            });
        }

        [Test]
        public void DdgiScheduleShader_HonorsCpuAgeRefreshCandidateHints()
        {
            string schedule = ReadRepoText("Njulf.Shaders", "ddgi_schedule_score.comp");

            Assert.Multiple(() =>
            {
                Assert.That(schedule, Does.Contain("bool hintedAgeProbe = (inputReasonFlags & DDGI_SCHEDULE_REASON_AGE_REFRESH) != 0u;"));
                Assert.That(schedule, Does.Contain("bool ageProbeSelected = hintedAgeProbe ||"));
                Assert.That(schedule, Does.Contain("reasonFlags |= DDGI_SCHEDULE_REASON_AGE_REFRESH;"));
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

        [Test]
        public void DdgiPerceptualIrradianceAtlasBlend_EncodesHistoryBeforeEwmaAndDecodesAfterInterpolation()
        {
            string update = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
            string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
            string common = ReadRepoText("Njulf.Shaders", "common.glsl");

            const double previous = 1.0;
            const double current = 16.0;
            const double blendAlpha = 0.25;
            double encodedBlend = Lerp(EncodeDdgiIrradiance(previous), EncodeDdgiIrradiance(current), blendAlpha);
            double decodedPerceptual = DecodeDdgiIrradiance(encodedBlend);
            double linearBlend = Lerp(previous, current, blendAlpha);

            Assert.Multiple(() =>
            {
                Assert.That(update, Does.Contain("WritePackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u, mix(safePrevious, encodedCurrent, asymmetricBlendAlpha));"));
                Assert.That(update, Does.Contain("return ResolveDdgiIrradianceAtlasSqrtBlend(mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y));"));
                Assert.That(forward, Does.Contain("DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX)"));
                Assert.That(forward, Does.Contain("return ResolveDdgiIrradianceAtlasSqrtBlend(mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y));"));
                Assert.That(common, Does.Contain("irradiance = ResolveDdgiIrradianceAtlasSqrtBlend(DecodeDdgiIrradianceAtlasSqrtSample(irradiance));"));
                Assert.That(decodedPerceptual, Is.EqualTo(2.3393550318978145).Within(1.0e-12));
                Assert.That(decodedPerceptual, Is.LessThan(linearBlend));
            });
        }

        [TestCase(0.0f)]
        [TestCase(0.25f)]
        [TestCase(0.5f)]
        [TestCase(0.9f)]
        [TestCase(1.0f)]
        public void DdgiVisibilityGatherWeight_MatchesCosinePower50(float cosTheta)
        {
            double expected = Math.Pow(Math.Max(cosTheta, 0.0f), 50.0);
            double actual = DdgiVisibilityGatherWeight(cosTheta);

            Assert.That(actual, Is.EqualTo(expected).Within(1.0e-12));
        }

        [TestCase(0.05, 0.003125)]
        [TestCase(0.10, 0.025)]
        [TestCase(0.20, 0.20)]
        [TestCase(0.75, 0.75)]
        public void DdgiGatherWeightShaping_CrushesOnlyLowWeights(double input, double expected)
        {
            Assert.That(ShapeDdgiGatherWeight(input), Is.EqualTo(expected).Within(1.0e-12));
        }

        [TestCase(0.0, 0.1)]
        [TestCase(0.1, 0.181)]
        [TestCase(0.25, 0.278125)]
        [TestCase(0.5, 0.5)]
        [TestCase(2.0, 2.0)]
        public void DdgiGatherNormalization_SoftensSparseDenominators(double weightSum, double expected)
        {
            Assert.That(DdgiGatherNormalizationWeight(weightSum), Is.EqualTo(expected).Within(1.0e-12));
        }

        [Test]
        public void DdgiAsymmetricIrradianceBlend_DarkensFasterAndDampsBrighteningWithoutBreakingReasonFloors()
        {
            const uint materialChanged = 1u << 11;

            Assert.Multiple(() =>
            {
                Assert.That(ResolveAsymmetricIrradianceBlendAlpha(0.10, 0u, true, 1.0, 0.5), Is.EqualTo(0.30).Within(1.0e-12));
                Assert.That(ResolveAsymmetricIrradianceBlendAlpha(0.10, 0u, true, 1.0, 2.0), Is.EqualTo(0.05).Within(1.0e-12));
                Assert.That(ResolveAsymmetricIrradianceBlendAlpha(0.10, materialChanged, true, 1.0, 2.0), Is.EqualTo(0.30).Within(1.0e-12));
                Assert.That(ResolveAsymmetricIrradianceBlendAlpha(0.10, 0u, false, 1.0, 2.0), Is.EqualTo(0.10).Within(1.0e-12));
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

        private static double DdgiVisibilityGatherWeight(float cosTheta)
        {
            double x = Math.Max(cosTheta, 0.0f);
            double x2 = x * x;
            double x4 = x2 * x2;
            double x8 = x4 * x4;
            double x16 = x8 * x8;
            double x32 = x16 * x16;
            return x32 * x16 * x2;
        }

        private static double ShapeDdgiGatherWeight(double weight)
        {
            if (weight < 0.2)
                weight *= (weight * weight) / 0.04;
            return weight;
        }

        private static double DdgiGatherNormalizationWeight(double weightSum)
        {
            return Lerp(1.0, weightSum, Math.Clamp(weightSum * weightSum + 0.9, 0.0, 1.0));
        }

        private static double ResolveAsymmetricIrradianceBlendAlpha(
            double blendAlpha,
            uint flags,
            bool historyValid,
            double previousLuminance,
            double currentLuminance)
        {
            if (!historyValid)
                return Math.Clamp(blendAlpha, 0.0, 1.0);

            double relativeDelta = Math.Abs(currentLuminance - previousLuminance) / Math.Max(Math.Max(currentLuminance, previousLuminance), 0.05);
            double changeAttention = SmoothStep(0.02, 0.35, relativeDelta);
            double reasonFloor = ResolveIrradianceReasonBlendFloor(flags);
            double response = Math.Clamp(blendAlpha, 0.0, 1.0);

            if (currentLuminance < previousLuminance)
            {
                double darkeningResponse = Math.Max(response, 1.0 / 1024.0);
                darkeningResponse = Math.Max(darkeningResponse, Lerp(response, Math.Min(response + 0.20, 0.65), changeAttention));
                response = darkeningResponse;
            }
            else if (currentLuminance > previousLuminance)
            {
                double brighteningDamping = Lerp(1.0, 0.5, changeAttention);
                response = Math.Max(response * brighteningDamping, reasonFloor);
            }

            return Math.Clamp(response, 0.0, 1.0);
        }

        private static double ResolveIrradianceReasonBlendFloor(uint flags)
        {
            double response = 0.0;
            if ((flags & ((1u << 12) | (1u << 13))) != 0u)
                response = Math.Max(response, 0.35);
            if ((flags & (1u << 14)) != 0u)
                response = Math.Max(response, 0.25);
            if ((flags & (1u << 11)) != 0u)
                response = Math.Max(response, 0.30);
            return response;
        }

        private static double SmoothStep(double edge0, double edge1, double value)
        {
            double t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private static double EncodeDdgiIrradiance(double value)
        {
            return Math.Pow(Math.Clamp(value, 0.0, 64.0), 1.0 / 5.0);
        }

        private static double DecodeDdgiIrradiance(double encodedValue)
        {
            double sqrtIrradiance = Math.Pow(Math.Max(encodedValue, 0.0), 5.0 * 0.5);
            return Math.Clamp(sqrtIrradiance * sqrtIrradiance, 0.0, 64.0);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }
    }
}
