#ifndef NJULF_DDGI_SIMPLE_TRANSPORT_OPERATOR_GLSL
#define NJULF_DDGI_SIMPLE_TRANSPORT_OPERATOR_GLSL

// The recursive operator is deliberately shared by the transport and audit
// kernels.  Keeping lobe admission in one function prevents the regular solve
// and the certificate from proving different operators.
const float SIMPLE_DDGI_TRANSPORT_MAX_CERTIFIED_Q = 0.99;

bool SimpleDdgiTransportFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool SimpleDdgiTransportTryNormalizeLobes(
    SimpleDdgiParams params,
    vec3 reflectedInput,
    vec3 transmittedInput,
    bool transmissionEnabled,
    out vec3 reflected,
    out vec3 transmitted,
    out float maximumBeforeNormalization,
    out float scale)
{
    reflected = vec3(0.0);
    transmitted = vec3(0.0);
    maximumBeforeNormalization = 0.0;
    scale = 1.0;

    float q = params.transportAlbedoClamp;
    if (!SimpleDdgiTransportFinite(reflectedInput) ||
        !SimpleDdgiTransportFinite(transmittedInput) ||
        any(lessThan(reflectedInput, vec3(0.0))) ||
        any(lessThan(transmittedInput, vec3(0.0))) ||
        isnan(q) || isinf(q) || q < 0.0 || q >= 1.0 ||
        q > SIMPLE_DDGI_TRANSPORT_MAX_CERTIFIED_Q)
    {
        return false;
    }

    // Preserve the decoded sum for diagnostics and apply one common scale to
    // both lobes. Clamping each lobe first would hide excess decoded energy
    // and could change the reflected/transmitted ratio.
    reflected = reflectedInput;
    transmitted = transmissionEnabled ? transmittedInput : vec3(0.0);
    maximumBeforeNormalization = max(
        reflected.r + transmitted.r,
        max(reflected.g + transmitted.g, reflected.b + transmitted.b));
    if (maximumBeforeNormalization > q && maximumBeforeNormalization > 0.0)
    {
        scale = q / maximumBeforeNormalization;
        reflected *= scale;
        transmitted *= scale;
    }
    return SimpleDdgiTransportFinite(reflected) && SimpleDdgiTransportFinite(transmitted);
}

// Evaluates F(x)'s cached recursive bounce for one source-cache entry. The
// source radiance itself is intentionally not included in this return value.
vec3 EvaluateSimpleDdgiCachedRecursiveBounce(
    SimpleDdgiParams params,
    SimpleDdgiVolume volume,
    uint localProbeIndex,
    SimpleDdgiProbeState probeState,
    SimpleDdgiTransportRayCache source,
    out vec3 reflectedBounceRadiance,
    out vec3 transmittedBounceRadiance,
    out uint solverGatherCount,
    out float solverOwnershipSum,
    out float solverFallbackWeightSum,
    out bool invalid,
    out float enforcedThroughput)
{
    reflectedBounceRadiance = vec3(0.0);
    transmittedBounceRadiance = vec3(0.0);
    solverGatherCount = 0u;
    solverOwnershipSum = 0.0;
    solverFallbackWeightSum = 0.0;
    invalid = false;
    enforcedThroughput = 0.0;

    // Intermediate red/black colors are published to the canonical SSBO but
    // the sampled-image mirror is intentionally updated only after the final
    // color.  Disable that optional fast path for every recursive gather so a
    // solver sweep cannot observe a stale image generation.
    params.sampledAtlasEnabled = 0u;

    if (!SimpleDdgiTransportFinite(source.sourceRadiance) ||
        any(lessThan(source.sourceRadiance, vec3(0.0))) ||
        !SimpleDdgiTransportFinite(source.direction) ||
        !SimpleDdgiTransportFinite(source.normal) ||
        isnan(source.distance) || isinf(source.distance) ||
        source.distance < 0.0 ||
        dot(source.direction, source.direction) <= 0.0000001)
    {
        invalid = true;
        return vec3(0.0);
    }

    float hitKind = SimpleDdgiTransportRayCacheHitKind(source);
    if (hitKind < 0.5 || SimpleDdgiRayHitKindIsOneSidedBackFace(hitKind))
        return vec3(0.0);

    vec3 canonicalNormal = length(source.normal) > 0.00001
        ? normalize(source.normal)
        : vec3(0.0, 1.0, 0.0);
    vec3 normal = hitKind > 1.5 ? -canonicalNormal : canonicalNormal;
    bool transmissionEnabled =
        (params.flags & SIMPLE_DDGI_FLAG_THIN_SURFACE_TRANSMISSION) != 0u;
    vec3 reflected;
    vec3 transmitted;
    float ignoredMaximum;
    float ignoredScale;
    if (!SimpleDdgiTransportTryNormalizeLobes(
            params,
            source.diffuseReflectance,
            source.transmittedDiffuseReflectance,
            transmissionEnabled,
            reflected,
            transmitted,
            ignoredMaximum,
            ignoredScale))
    {
        invalid = true;
        return vec3(0.0);
    }
    enforcedThroughput = max(
        reflected.r + transmitted.r,
        max(reflected.g + transmitted.g, reflected.b + transmitted.b));

    vec3 probePosition = SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) +
        probeState.relocation;
    vec3 hitPosition = probePosition + source.direction * source.distance;
    float surfaceOffset = max(0.03, volume.spacing * 0.02);
    if (max(reflected.r, max(reflected.g, reflected.b)) > 0.0)
    {
        float ownership;
        float fallbackWeight;
        vec3 bouncedIrradiance = SampleSimpleDdgiSolverBounceIrradiance(
            params,
            hitPosition + normal * surfaceOffset,
            normal,
            -source.direction,
            ownership,
            fallbackWeight);
        solverGatherCount++;
        solverOwnershipSum += ownership;
        solverFallbackWeightSum += fallbackWeight;
        reflectedBounceRadiance = EvaluateGiDiffuseFromIrradiance(
            bouncedIrradiance,
            reflected);
    }

    if (max(transmitted.r, max(transmitted.g, transmitted.b)) > 0.0)
    {
        float ownership;
        float fallbackWeight;
        vec3 transmittedIrradiance = SampleSimpleDdgiSolverBounceIrradiance(
            params,
            hitPosition - normal * surfaceOffset,
            -normal,
            source.direction,
            ownership,
            fallbackWeight);
        solverGatherCount++;
        solverOwnershipSum += ownership;
        solverFallbackWeightSum += fallbackWeight;
        transmittedBounceRadiance = EvaluateGiDiffuseFromIrradiance(
            transmittedIrradiance,
            transmitted);
    }

    vec3 totalBounce = reflectedBounceRadiance + transmittedBounceRadiance;
    if (!SimpleDdgiTransportFinite(totalBounce) ||
        any(lessThan(totalBounce, vec3(0.0))))
    {
        invalid = true;
        return vec3(0.0);
    }
    return totalBounce;
}

#endif
