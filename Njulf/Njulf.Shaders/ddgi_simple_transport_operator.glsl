#ifndef NJULF_DDGI_SIMPLE_TRANSPORT_OPERATOR_GLSL
#define NJULF_DDGI_SIMPLE_TRANSPORT_OPERATOR_GLSL

// The recursive operator is deliberately shared by the transport and audit
// kernels.  Keeping lobe admission in one function prevents the regular solve
// and the certificate from proving different operators.
const float SIMPLE_DDGI_TRANSPORT_MAX_CERTIFIED_Q = 0.99;
const uint SIMPLE_DDGI_TRANSPORT_CONTRACTION_ROUNDING_MARGIN_ULPS = 8u;

bool SimpleDdgiTransportFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool SimpleDdgiTransportTryNormalizeLobes(
    SimpleDdgiParams params,
    vec3 reflectedInput,
    vec3 transmittedInput,
    vec3 glossyInput,
    vec3 pathThroughput,
    bool transmissionEnabled,
    out vec3 reflected,
    out vec3 transmitted,
    out vec3 glossy,
    out float maximumBeforeNormalization,
    out vec3 scale,
    out vec3 enforcedThroughput)
{
    reflected = vec3(0.0);
    transmitted = vec3(0.0);
    glossy = vec3(0.0);
    maximumBeforeNormalization = 0.0;
    scale = vec3(1.0);
    enforcedThroughput = vec3(0.0);

    float q = params.transportAlbedoClamp;
    if (!SimpleDdgiTransportFinite(reflectedInput) ||
        !SimpleDdgiTransportFinite(transmittedInput) ||
        !SimpleDdgiTransportFinite(glossyInput) ||
        !SimpleDdgiTransportFinite(pathThroughput) ||
        any(lessThan(reflectedInput, vec3(0.0))) ||
        any(lessThan(transmittedInput, vec3(0.0))) ||
        any(lessThan(glossyInput, vec3(0.0))) ||
        any(lessThan(pathThroughput, vec3(0.0))) ||
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
    glossy = glossyInput;
    vec3 total = (reflected + transmitted + glossy) * pathThroughput;
    maximumBeforeNormalization = max(
        total.r,
        max(total.g, total.b));
    uint qBits = floatBitsToUint(q);
    float safeQ = uintBitsToFloat(
        qBits > SIMPLE_DDGI_TRANSPORT_CONTRACTION_ROUNDING_MARGIN_ULPS
            ? qBits - SIMPLE_DDGI_TRANSPORT_CONTRACTION_ROUNDING_MARGIN_ULPS
            : 0u);
    scale = min(vec3(1.0), vec3(safeQ) / max(total, vec3(0.000001)));
    reflected *= scale;
    transmitted *= scale;
    glossy *= scale;

    // Division, three lobe products, their additions, and the path product
    // are independently rounded on the GPU. Re-evaluate the actual diagonal
    // gain and apply one bounded correction with an eight-ULP safety margin;
    // this prevents an exact q-boundary material from becoming q+1 ULP and
    // permanently failing the tail certificate.
    enforcedThroughput =
        (reflected + transmitted + glossy) * pathThroughput;
    vec3 correction = min(
        vec3(1.0),
        vec3(safeQ) / max(enforcedThroughput, vec3(0.000001)));
    reflected *= correction;
    transmitted *= correction;
    glossy *= correction;
    scale *= correction;
    enforcedThroughput =
        (reflected + transmitted + glossy) * pathThroughput;
    return SimpleDdgiTransportFinite(reflected) &&
        SimpleDdgiTransportFinite(transmitted) &&
        SimpleDdgiTransportFinite(glossy) &&
        SimpleDdgiTransportFinite(enforcedThroughput) &&
        all(lessThanEqual(enforcedThroughput, vec3(q)));
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
    out vec3 enforcedThroughput)
{
    reflectedBounceRadiance = vec3(0.0);
    transmittedBounceRadiance = vec3(0.0);
    solverGatherCount = 0u;
    solverOwnershipSum = 0.0;
    solverFallbackWeightSum = 0.0;
    invalid = false;
    enforcedThroughput = vec3(0.0);

    // Intermediate red/black colors are published to the canonical SSBO but
    // the sampled-image mirror is intentionally updated only after the final
    // color.  Disable that optional fast path for every recursive gather so a
    // solver sweep cannot observe a stale image generation.
    params.sampledAtlasEnabled = 0u;

    if (!SimpleDdgiTransportFinite(source.sourceRadiance) ||
        any(lessThan(source.sourceRadiance, vec3(0.0))) ||
        !SimpleDdgiTransportFinite(source.direction) ||
        !SimpleDdgiTransportFinite(source.normal) ||
        !SimpleDdgiTransportFinite(source.endpointOffset) ||
        !SimpleDdgiTransportFinite(source.pathThroughput) ||
        any(lessThan(source.pathThroughput, vec3(0.0))) ||
        (source.volumePathFlags &
            ~SIMPLE_DDGI_VOLUME_PATH_KNOWN_FLAG_MASK) != 0u ||
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
    bool recursiveGlossy =
        SimpleDdgiGlossyTransportMode(params.residencyFlags) ==
            SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_RECURSIVE_CERTIFIED;
    vec3 glossyInput = vec3(0.0);
    float roughnessWeight = 0.0;
    float nDotV = 0.0;
    if (recursiveGlossy)
    {
        if (!SimpleDdgiTransportFinite(source.specularF0) ||
            any(lessThan(source.specularF0, vec3(0.0))) ||
            isnan(source.roughness) || isinf(source.roughness) ||
            source.roughness < 0.0 || source.roughness > 1.0)
        {
            invalid = true;
            return vec3(0.0);
        }

        roughnessWeight = SimpleDdgiRoughSpecularWeight(
            params.residencyFlags,
            source.roughness);
        nDotV = max(dot(normal, -normalize(source.direction)), 0.0);
        if (roughnessWeight > 0.0 && nDotV > 0.0 &&
            max(source.specularF0.r,
                max(source.specularF0.g, source.specularF0.b)) > 0.0)
        {
            GPUEnvironmentData environment = ReadGiEnvironmentData();
            if (environment.BrdfLutTextureIndex < 0)
            {
                invalid = true;
                return vec3(0.0);
            }
            vec2 brdf = texture(
                BindlessTextures[nonuniformEXT(
                    environment.BrdfLutTextureIndex)],
                vec2(nDotV, source.roughness)).rg;
            vec3 f0 = clamp(source.specularF0, vec3(0.0), vec3(1.0));
            vec3 fresnel = f0 +
                (max(vec3(1.0 - source.roughness), f0) - f0) *
                pow(clamp(1.0 - nDotV, 0.0, 1.0), 5.0);
            glossyInput = max(
                fresnel * brdf.x + vec3(brdf.y),
                vec3(0.0)) *
                max(environment.SpecularIntensity, 0.0) *
                roughnessWeight *
                clamp(source.materialOcclusion, 0.0, 1.0);
        }
    }
    vec3 reflected;
    vec3 transmitted;
    vec3 glossy;
    float ignoredMaximum;
    vec3 ignoredScale;
    if (!SimpleDdgiTransportTryNormalizeLobes(
            params,
            source.diffuseReflectance,
            source.transmittedDiffuseReflectance,
            glossyInput,
            source.pathThroughput,
            transmissionEnabled,
            reflected,
            transmitted,
            glossy,
            ignoredMaximum,
            ignoredScale,
            enforcedThroughput))
    {
        invalid = true;
        return vec3(0.0);
    }
    // Preserve the normalized diagonal RGB gain. The legacy scalar
    // contraction is its maximum component, while audit certification pairs
    // each channel's defect with the gain that actually affects it.

    vec3 probePosition = SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) +
        probeState.relocation;
    vec3 hitPosition = probePosition + source.endpointOffset;
    float surfaceOffset = max(0.03, volume.spacing * 0.02);
    if (recursiveGlossy &&
        max(glossy.r, max(glossy.g, glossy.b)) > 0.0)
    {
        vec3 reflectionDirection = reflect(
            source.direction,
            normal);
        SetSimpleDdgiDirectionalRadianceQuery(
            reflectionDirection,
            source.roughness);
        SetSimpleDdgiDirectionalRadianceQueryBuffer(
            uint(SIMPLE_DDGI_DIRECTIONAL_RADIANCE_PARITY_BUFFER_INDEX));
    }
    else
    {
        SetSimpleDdgiDirectionalRadianceQueryBuffer(0u);
    }

    if (max(reflected.r, max(reflected.g, reflected.b)) > 0.0 ||
        max(glossy.r, max(glossy.g, glossy.b)) > 0.0)
    {
        float ownership;
        float fallbackWeight;
        SimpleDdgiGatherResult reflectedGather;
        vec3 bouncedIrradiance =
            SampleSimpleDdgiSolverBounceIrradianceDetailed(
            params,
            hitPosition + normal * surfaceOffset,
            normal,
            -source.direction,
            ownership,
            fallbackWeight,
            reflectedGather);
        solverGatherCount++;
        solverOwnershipSum += ownership;
        solverFallbackWeightSum += fallbackWeight;
        reflectedBounceRadiance = EvaluateGiDiffuseFromIrradiance(
            bouncedIrradiance,
            reflected);
        if (max(glossy.r, max(glossy.g, glossy.b)) > 0.0)
        {
            float confidence = clamp(
                reflectedGather.directionalRadianceSupport * ownership *
                SimpleDdgiLeakAttenuation(reflectedGather, params),
                0.0,
                1.0);
            reflectedBounceRadiance += max(
                reflectedGather.directionalRadiance,
                vec3(0.0)) * glossy * confidence;
        }
    }

    if (max(transmitted.r, max(transmitted.g, transmitted.b)) > 0.0)
    {
        SetSimpleDdgiDirectionalRadianceQueryBuffer(0u);
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

    reflectedBounceRadiance *= source.pathThroughput;
    transmittedBounceRadiance *= source.pathThroughput;
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
