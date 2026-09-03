// Included inside forward main after material evaluation. Keep this block in
// one source so cache-required variants can preserve their diagnostic ordering
// while exact-only variants delay the structured result until after lighting.
SimpleDdgiGatherResult precomputedSimpleDdgiGather =
    EmptySimpleDdgiGatherResult();
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
bool exactFeedbackGatherContributed = false;
float exactFeedbackRadiometricOwnership = 0.0;
float exactFeedbackLeakAttenuation = 0.0;
float exactFeedbackRoughDdgiOwnership = 0.0;
bool exactFeedbackMaskedHandledByCompaction = false;
#endif
vec3 ddgiDirectionalRadiance = vec3(0.0);
float ddgiDirectionalConfidence = 0.0;
float indirectSpecularVisibility = 1.0;
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
ForwardDdgiReceiverCacheSample cachedGather;
cachedGather.Packed = uvec4(0u);
#endif
#if !FORWARD_GLOBAL_ILLUMINATION_DISABLED && \
    !FORWARD_DDGI_RECEIVER_CACHE_LEGACY && \
    !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
if (!NjulfReceiverCacheAcceptedLane())
{
bool directionalGlobalIlluminationEnabled = geometryDecal
    ? ForwardDecalGlobalIlluminationEnabled()
    : ForwardGlobalIlluminationEnabled() != 0u;
if (directionalGlobalIlluminationEnabled)
{
    SimpleDdgiParams directionalParams = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    uint directionalMode = SimpleDdgiDirectionalRadianceMode(
        directionalParams.residencyFlags);
    uint glossyMode = SimpleDdgiGlossyTransportMode(
        directionalParams.residencyFlags);
    float roughnessWeight = SimpleDdgiRoughSpecularWeight(
        directionalParams.residencyFlags,
        roughness);
    bool directionalConfigured =
        (directionalParams.flags &
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
        directionalParams.probeCount > 0u &&
        directionalMode !=
            SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_OFF &&
        glossyMode != SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_OFF &&
        (roughnessWeight > 0.0 || thinGlass);
    bool diffuseGatherRequired =
        (directionalParams.flags &
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
        directionalParams.probeCount > 0u;
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    bool receiverCompactDirectionalResolved = !directionalConfigured;
#if FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED
    if (NjulfPerformanceOptimizationEnabled(
            NJULF_PERFORMANCE_HYBRID_PROJECTION_ELISION))
    {
    // Accepted opaque receivers have no forward directional-specular owner in
    // this artifact. Mark the directional requirement resolved without
    // touching the compact L2 record; rejected/exception paths still fall
    // through to the authoritative exact gather below.
        receiverCompactDirectionalResolved =
            receiverCompactDirectionalResolved || receiverCacheAccepted;
    }
    else
#endif
    {
        if (receiverCacheAccepted && directionalConfigured)
        {
            vec3 compactDirectionalRadiance;
            float compactDirectionalConfidence;
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
            IncrementSimpleDdgiReceiverCacheDiagnostic(
                pc.Push.CurrentFrameIndex,
                SIMPLE_DDGI_RECEIVER_CACHE_DIRECTIONAL_EVALUATION_COUNTER);
#endif
            receiverCompactDirectionalResolved =
                SampleForwardDdgiCompactDirectionalRadiance(
                    ForwardScreenPixel(),
                    gl_FragCoord.z,
                    fragWorldPosition,
                    ddgiNormal,
                    reflect(-viewDirection, normal),
                    roughness,
                    directionalMode,
                    directionalParams.frameIndex,
                    pc.Push,
                    compactDirectionalRadiance,
                    compactDirectionalConfidence);
            if (receiverCompactDirectionalResolved)
            {
                ddgiDirectionalRadiance = compactDirectionalRadiance *
                    max(directionalParams.indirectIntensity, 0.0);
                ddgiDirectionalConfidence = compactDirectionalConfidence;
            }
        }
    }
    bool exactGatherRequired =
        !receiverCacheAccepted ||
        !receiverCompactDirectionalResolved ||
        (ForwardAmbientOcclusionBentNormalMode() == 2u &&
         bentNormalValid);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    // Cache-required opaque artifacts also own B1 attribution. Only a
    // surviving alpha-mask fragment that cannot enter the lossless compact
    // transaction pays for the exact structured gather here. Rejected cache
    // admissions and any overflow retain this authoritative path.
    bool alphaMaskFeedbackRequired =
        alphaMode > 0.5 && alphaMode < 1.5;
    if (alphaMaskFeedbackRequired && receiverCacheAccepted &&
        !exactGatherRequired && diffuseGatherRequired &&
        NjulfPerformanceOptimizationEnabled(
            NJULF_PERFORMANCE_COMPACT_MASKED_FEEDBACK))
    {
        exactFeedbackMaskedHandledByCompaction =
            TryHandleSimpleDdgiMaskedFeedbackWithoutInlineGather(
                fragWorldPosition,
                ddgiNormal,
                materialCoverage.Alpha);
    }
    exactGatherRequired = exactGatherRequired ||
        (alphaMaskFeedbackRequired &&
         !exactFeedbackMaskedHandledByCompaction);
#endif
    if (exactGatherRequired && diffuseGatherRequired)
#else
    if (diffuseGatherRequired)
#endif
    {
        if (directionalConfigured)
        {
            SetSimpleDdgiDirectionalRadianceQuery(
                reflect(-viewDirection, normal),
                roughness);
            if (thinGlass)
            {
                // Transparent sheets have no deferred SSR target. Their
                // explicit ThinGlass classification therefore admits the
                // directional DDGI field as the default reflected scene at
                // any roughness, with the environment only filling genuinely
                // unsupported DDGI weight.
                SetSimpleDdgiDirectionalRadianceQueryEligibilityWeight(1.0);
            }
        }
        precomputedSimpleDdgiGather = SampleSimpleDdgiGather(
            directionalParams,
            fragWorldPosition,
            ddgiNormal,
            viewDirection);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
        // Cache-accepted alpha-mask receivers skip the exact diffuse
        // composition block below, so publish B1 ownership directly from this
        // authoritative gather. ThinGlass uses the same path and explicitly
        // owns its reflection lobe at every roughness.
        exactFeedbackGatherContributed = true;
        exactFeedbackRadiometricOwnership =
            SimpleDdgiRadiometricOwnership(precomputedSimpleDdgiGather);
        exactFeedbackLeakAttenuation = SimpleDdgiLeakAttenuation(
            precomputedSimpleDdgiGather,
            directionalParams);
#if FORWARD_THIN_GLASS_ONLY
        exactFeedbackRoughDdgiOwnership = 1.0;
#else
        exactFeedbackRoughDdgiOwnership =
            SimpleDdgiRoughSpecularWeight(
                directionalParams.residencyFlags,
                roughness);
#endif
#endif
        indirectSpecularVisibility =
            SimpleDdgiRoughIndirectSpecularVisibility(
                precomputedSimpleDdgiGather,
                directionalParams,
                roughness);
        if (directionalConfigured)
        {
            ddgiDirectionalRadiance =
                precomputedSimpleDdgiGather.directionalRadiance *
                max(directionalParams.indirectIntensity, 0.0);
            ddgiDirectionalConfidence = clamp(
                precomputedSimpleDdgiGather.directionalRadianceSupport *
                SimpleDdgiRadiometricOwnership(
                    precomputedSimpleDdgiGather),
                0.0,
                1.0);
        }
    }
}
}
#endif
