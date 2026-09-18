#version 460
#extension GL_GOOGLE_include_directive : require
#include "forward_surface_shading.glsl"

void WriteForwardColor(vec4 color)
{
#if OPTICAL_FORWARD_ACTIVE
    if (!opticalExported) OpticalInvalidatePixel();
#endif
#if NJULF_C5_TRACE_RESOLUTION_SOURCE
    // The source-only program has no SceneColor attachment. Debug paths are
    // rejected by admission; retaining this no-op keeps shared material
    // control flow well-formed without publishing indirect lighting.
    color = color;
#elif FORWARD_WEIGHTED_OIT
    float alpha = clamp(color.a, 0.0, 1.0);
    if (alpha <= 0.001)
        discard;

    float depthWeight = clamp(pow(max(1.0 - gl_FragCoord.z * 0.95, 0.01), 3.0), 0.01, 1.0);
    float alphaWeight = max(alpha * 8.0 + 0.01, 0.01);
    float weight = clamp(alphaWeight * alphaWeight * alphaWeight * 64.0 * depthWeight, 0.01, 3000.0);
    vec3 premultipliedColor = max(color.rgb, vec3(0.0)) * alpha;
    outOitAccumulation = vec4(premultipliedColor * weight, alpha * weight);
    outOitRevealage = vec4(alpha);
#else
    outColor = color;
#endif
}

float forwardDebugOutputAlpha;

bool IsDdgiDebugView(uint view)
{
    return view >= GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE &&
           view <= GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE;
}

vec3 DdgiDebugCategoryColor(uint view)
{
    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
        return vec3(1.0, 0.55, 0.10);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE)
        return vec3(0.10, 0.85, 1.0);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE)
        return vec3(0.25, 0.45, 1.0);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP)
        return vec3(0.10, 1.0, 0.25);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET)
        return vec3(1.0, 0.10, 0.85);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE)
        return vec3(0.85, 0.85, 0.10);

    if (view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_OCCUPANCY_SLICE ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_TRACE_RESULT ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW)
        return vec3(0.20, 1.0, 0.55);

    return vec3(1.0, 1.0, 1.0);
}

vec3 ApplyDdgiDebugIdentity(vec3 color, uint view)
{
    if (!IsDdgiDebugView(view))
        return color;

    vec2 p = ForwardScreenPixel();
    vec2 screen = max(pc.Push.ScreenDimensions, vec2(1.0));
    vec3 category = DdgiDebugCategoryColor(view);

    bool border =
        p.x < 4.0 || p.y < 4.0 ||
        p.x >= screen.x - 4.0 ||
        p.y >= screen.y - 4.0;
    if (border)
        color = category;

    bool badge = p.x < 96.0 && p.y < 32.0;
    if (badge)
    {
        float checker = mod(floor(p.x / 8.0) + floor(p.y / 8.0), 2.0);
        color = mix(category * 0.35, category, checker);

        for (uint bit = 0u; bit < 6u; bit++)
        {
            float x0 = 8.0 + float(bit) * 12.0;
            bool inBar = p.x >= x0 && p.x < x0 + 8.0 && p.y >= 20.0 && p.y < 28.0;
            if (inBar)
            {
                bool one = ((view >> bit) & 1u) != 0u;
                color = one ? vec3(1.0) : vec3(0.0);
            }
        }
    }

    bool legend = p.x < 96.0 && p.y >= screen.y - 12.0;
    if (legend)
    {
        if (p.x < 32.0)
            color = vec3(1.0, 0.0, 0.0);
        else if (p.x < 64.0)
            color = vec3(0.0, 1.0, 0.0);
        else
            color = vec3(0.0, 0.0, 1.0);
    }

    return color;
}

void WriteDdgiDebugColor(uint view, vec3 color)
{
    WriteForwardColor(vec4(
        ApplyDdgiDebugIdentity(color, view),
        forwardDebugOutputAlpha));
}

void WriteMaterialTransportProvenance(uint sourcePath)
{
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    if (MaterialTransportProvenanceEnabled())
        outMaterialTransportProvenance =
            float(min(sourcePath, MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN)) / 255.0;
#endif
}

uint ResolveSimpleDdgiMaterialTransportProvenance(
    SimpleDdgiGatherResult gather,
    SimpleDdgiParams params)
{
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    if (SimpleDdgiRadiometricOwnership(gather) <= 0.000001)
        return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;

    uint sourceVolumeIndex = gather.selectedVolume;
    if (gather.secondaryVolume != SIMPLE_DDGI_INVALID_VOLUME_INDEX &&
        gather.secondaryContributionWeight > gather.primaryContributionWeight)
    {
        sourceVolumeIndex = gather.secondaryVolume;
    }
    if (sourceVolumeIndex >= params.volumeCount)
        return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;

    SimpleDdgiVolume sourceVolume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        sourceVolumeIndex);
    bool farFieldOnlyRing =
        (params.flags & SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED) != 0u &&
        sourceVolume.spacing >= 15.999;
    if (farFieldOnlyRing)
        return MATERIAL_TRANSPORT_PROVENANCE_FAR_FIELD;
    return sourceVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED
        ? MATERIAL_TRANSPORT_PROVENANCE_DETAILED_MESH
        : MATERIAL_TRANSPORT_PROVENANCE_COMPACT_PRIMITIVE;
#else
    return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;
#endif
}

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
float SimpleDdgiTransparentCompositeWeight(float outputAlpha)
{
    float alpha = clamp(outputAlpha, 0.0, 1.0);
#if FORWARD_WEIGHTED_OIT
    float depthWeight = clamp(
        pow(max(1.0 - gl_FragCoord.z * 0.95, 0.01), 3.0),
        0.01,
        1.0);
    float alphaWeight = max(alpha * 8.0 + 0.01, 0.01);
    float oitWeight = clamp(
        alphaWeight * alphaWeight * alphaWeight * 64.0 * depthWeight,
        0.01,
        3000.0);
    // This is the exact coefficient submitted to the weighted accumulation
    // target. The later normalization is shared by all fragments and cannot be
    // attributed without retaining a full per-pixel fragment list.
    return alpha * oitWeight;
#else
    // Sorted source-over uses opacity as this fragment's compositing
    // coefficient. Draw order supplies the accumulated destination
    // transmittance; feedback deliberately never substitutes fragment count.
    return alpha;
#endif
}

void EmitSimpleDdgiSurfaceReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float physicalSurfaceWeight,
    bool eligible,
    uint producer,
    bool tileNamespaceValid,
    uint tileNamespaceBase)
{
    EmitSimpleDdgiSurfaceReceiverFeedbackCore(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        physicalSurfaceWeight,
        eligible,
        producer,
        pc.Push.CurrentFrameIndex,
        pc.Push.ScreenDimensions,
        gl_FragCoord.xy,
        tileNamespaceValid,
        tileNamespaceBase,
        uvec3(fragObjectIndex, fragMaterialIndex, fragMeshletIndex));
}

void EmitSimpleDdgiTransparentReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float outputAlpha)
{
    EmitSimpleDdgiSurfaceReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        SimpleDdgiTransparentCompositeWeight(outputAlpha),
        true,
        2u,
        true,
        0u);
}

// Returns true when this cache-accepted masked fragment needs no inline exact
// gather. That can mean there is no open alpha producer, neither rotating B1
// stratum selects the pixel, or its exact surface payload was appended. A
// malformed producer policy or compact-list overflow returns false so the
// unchanged fragment gather and failure accounting execute immediately.
bool TryHandleSimpleDdgiMaskedFeedbackWithoutInlineGather(
    vec3 worldPosition,
    vec3 geometricNormal,
    float survivingCoverage)
{
    uint compactBufferIndex = SimpleDdgiMaskedFeedbackBufferIndex(
        pc.Push.CurrentFrameIndex);
    uint wordCount = uint(BindlessStorageBuffers[
        nonuniformEXT(compactBufferIndex)].Words.length());
    if (wordCount < SIMPLE_DDGI_MASKED_FEEDBACK_HEADER_WORDS)
        return false;

    uint state = SimpleDdgiMaskedFeedbackWord(
        compactBufferIndex,
        SIMPLE_DDGI_MASKED_FEEDBACK_STATE_WORD);
    if ((state & SIMPLE_DDGI_MASKED_FEEDBACK_INITIALIZED_BIT) == 0u)
        return false;
    if ((state & SIMPLE_DDGI_MASKED_FEEDBACK_ACTIVE_BIT) == 0u)
        return true;

    uint logicalCapacity;
    if (!SimpleDdgiMaskedFeedbackCompactionActive(
            compactBufferIndex,
            logicalCapacity))
    {
        return false;
    }

    uint controlOffsetWords;
    if (!SimpleDdgiReceiverFeedbackTryResolveFrameControlOffset(
            pc.Push.CurrentFrameIndex,
            controlOffsetWords))
    {
        return false;
    }

    uvec2 pixel;
    uint exactTileId;
    uvec2 tileBase;
    uvec2 coveredTileExtent;
    uint coveredTilePixelCount;
    if (!SimpleDdgiSurfaceFeedbackTryResolveTile(
            gl_FragCoord.xy,
            pc.Push.ScreenDimensions,
            true,
            0u,
            pixel,
            exactTileId,
            tileBase,
            coveredTileExtent,
            coveredTilePixelCount))
    {
        return false;
    }

    bool alphaPolicyUsable;
    bool refinementPolicyUsable;
    bool alphaSelected = SimpleDdgiSurfaceFeedbackCouldSelectProducer(
        controlOffsetWords,
        1u,
        pixel,
        tileBase,
        coveredTileExtent,
        coveredTilePixelCount,
        exactTileId,
        alphaPolicyUsable);
    bool refinementSelected =
        SimpleDdgiSurfaceFeedbackCouldSelectProducer(
            controlOffsetWords,
            6u,
            pixel,
            tileBase,
            coveredTileExtent,
            coveredTilePixelCount,
            exactTileId,
            refinementPolicyUsable);
    if (!alphaPolicyUsable || !refinementPolicyUsable)
        return false;
    if (!alphaSelected && !refinementSelected)
        return true;

    return SimpleDdgiMaskedFeedbackTryAppend(
        compactBufferIndex,
        logicalCapacity,
        worldPosition,
        geometricNormal,
        survivingCoverage,
        pixel,
        uvec3(fragObjectIndex, fragMaterialIndex, fragMeshletIndex));
}

void EmitSimpleDdgiAlphaMaskReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float survivingCoverage,
    bool alphaMask,
    float roughDdgiOwnership)
{
    // The fragment has already passed the shipping alpha expression. Its
    // projected raster-sample area is one pixel; sampled alpha supplies the
    // sub-pixel coverage estimate without counting rejected fragments.
    bool reflectionFeedback = ForwardReflectionCaptureEnabled() &&
        !ForwardAutomaticPlanarCaptureEnabled();
    uint tileNamespaceBase = 0u;
    bool tileNamespaceValid = !reflectionFeedback ||
        SimpleDdgiTryComputeCubemapTileNamespace(
            ForwardReflectionCaptureLayer(),
            pc.Push.ScreenDimensions,
            tileNamespaceBase);
    float physicalSurfaceWeight = reflectionFeedback
        ? SimpleDdgiCubemapTexelSolidAngle(
              ForwardScreenPixel(),
              pc.Push.ScreenDimensions) *
          clamp(roughDdgiOwnership, 0.0, 1.0)
        : clamp(survivingCoverage, 0.0, 1.0);
    EmitSimpleDdgiSurfaceReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        physicalSurfaceWeight,
        reflectionFeedback || alphaMask,
        reflectionFeedback ? 5u : 1u,
        tileNamespaceValid,
        tileNamespaceBase);
}
#endif

#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
const float C5_MAXIMUM_FINITE_FP16 = 65504.0;

vec2 C5OctEncodeNormal(vec3 value)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 1.0e-12 || any(isnan(value)) || any(isinf(value)))
        return vec2(0.0);
    vec3 normal = value * inversesqrt(lengthSquared);
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return clamp(normal.xy, vec2(-1.0), vec2(1.0));
}

bool C5CreateReceiverPayload(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 diffuseBase,
    vec3 dielectricF0,
    out uvec4 payload)
{
    payload = uvec4(0u);

    vec2 encodedGeometric = C5OctEncodeNormal(geometricNormal);
    vec2 encodedShading = C5OctEncodeNormal(shadingNormal);
    if (dot(encodedGeometric, encodedGeometric) == 0.0 &&
            abs(geometricNormal.z) < 0.5 ||
        dot(encodedShading, encodedShading) == 0.0 &&
            abs(shadingNormal.z) < 0.5)
    {
        return false;
    }

    // The object publication assigns this frame-local token while building
    // the matching frame-buffered C5 surface table. 0xffff is invalid and
    // 0xfffe is intentionally unassigned, leaving exactly 65,534 entries.
    uint surfaceToken = fragObjectIndex;
    if (surfaceToken >= 65534u ||
        any(isnan(diffuseBase)) || any(isinf(diffuseBase)) ||
        any(isnan(dielectricF0)) || any(isinf(dielectricF0)) ||
        any(lessThan(diffuseBase, vec3(0.0))) ||
        any(lessThan(dielectricF0, vec3(0.0))))
    {
        return false;
    }

    payload = uvec4(
        packSnorm2x16(encodedGeometric),
        packSnorm2x16(encodedShading),
        surfaceToken | (NjulfC5PackRgb565(dielectricF0) << 16u),
        NjulfC5PackRgb9E5(diffuseBase));
    return true;
}

float C5ResolveB3FootprintRadius()
{
    SimpleDdgiParams c5Params = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((c5Params.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u ||
        c5Params.probeCount == 0u || c5Params.volumeCount == 0u)
    {
        return 0.0;
    }
    uint selectedVolumeIndex;
    SimpleDdgiVolume selectedVolume;
    float selectedEdgeWeight;
    bool refinementOrBaseFallback;
    SelectSimpleDdgiVolume(
        c5Params,
        fragWorldPosition,
        selectedVolumeIndex,
        selectedVolume,
        selectedEdgeWeight,
        refinementOrBaseFallback);
    float spacing = selectedVolume.spacing;
    return !isnan(spacing) && !isinf(spacing) && spacing > 0.0
        ? spacing * 0.25
        : 0.0;
}

void C5WriteDirectDiffuseAndEmissiveSource(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 directionalDiffuseBase,
    vec3 dielectricF0,
    vec3 directDiffuseSource,
    vec3 emissive)
{
    bool payloadValid = C5CreateReceiverPayload(
        geometricNormal,
        shadingNormal,
        directionalDiffuseBase,
        dielectricF0,
        outNearFieldReceiverPayload);
    float b3FootprintRadius = payloadValid
        ? C5ResolveB3FootprintRadius()
        : 0.0;
    payloadValid = payloadValid && b3FootprintRadius > 0.0;
    if (!payloadValid)
        outNearFieldReceiverPayload = uvec4(0u);
    outDirectDiffuseAndEmissive = vec4(
        clamp(directDiffuseSource + emissive,
            vec3(0.0), vec3(C5_MAXIMUM_FINITE_FP16)),
        payloadValid ? b3FootprintRadius : 0.0);
}
#endif

#if NJULF_C4_RECEIVER_OUTPUT
bool C4CreateReceiverPayload(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 directionalDiffuseBase,
    vec3 dielectricF0,
    out uvec4 payload)
{
    payload = uvec4(0u);
    float geometricLengthSquared = dot(geometricNormal, geometricNormal);
    float shadingLengthSquared = dot(shadingNormal, shadingNormal);
    if (geometricLengthSquared <= 1.0e-12 ||
        shadingLengthSquared <= 1.0e-12 ||
        any(isnan(geometricNormal)) || any(isinf(geometricNormal)) ||
        any(isnan(shadingNormal)) || any(isinf(shadingNormal)) ||
        any(isnan(directionalDiffuseBase)) ||
        any(isinf(directionalDiffuseBase)) ||
        any(isnan(dielectricF0)) || any(isinf(dielectricF0)) ||
        any(lessThan(directionalDiffuseBase, vec3(0.0))) ||
        any(lessThan(dielectricF0, vec3(0.0))))
    {
        return false;
    }

    payload = uvec4(
        packSnorm2x16(NjulfC4OctEncodeNormal(geometricNormal)),
        packSnorm2x16(NjulfC4OctEncodeNormal(shadingNormal)),
        NjulfC4PackRgb9E5(directionalDiffuseBase),
        NjulfC4PackDielectricF0AndFlags(dielectricF0));
    return NjulfC4ReceiverPayloadValid(payload);
}
#endif

#if FORWARD_THIN_GLASS_ONLY
void main()
{
    EnforceAutomaticPlanarCaptureClip();
    GPUMaterialData material = ReadForwardMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    MaterialAlphaCoverage materialCoverage = ResolveMaterialAlphaCoverage(
        material,
        albedoSample,
        fragVertexColor.a);
    if (!MaterialCoverageSurvivesForward(materialCoverage) && !NJULF_VISIBILITY_COVERAGE_AUTHORITY)
        discard;

    vec3 geometricNormal = normalize(fragNormal) *
        (gl_FrontFacing ? 1.0 : -1.0);
    bool useNormalTexture =
        material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
        material.NormalScaleBias.x > 0.001;
    vec3 normal = useNormalTexture
        ? ResolveNormal(
            material,
            fragNormal,
            fragWorldTangent,
            MaterialUv(
                material.TextureTexCoordSets.y,
                material.NormalOffsetScale,
                material.TextureRotations.y))
        : geometricNormal;
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // AmazonBistroMaterialProfile is the explicit authority for window lobe
    // width. Do not multiply it by the FBX material's generic packed channel.
    float roughness = clamp(
        material.MetallicRoughnessAO.y,
        0.04,
        1.0);
    float reflectionSchedulingRoughness =
        EstimateReflectionSchedulingRoughness(
            roughness,
            roughness,
            normal);

    // ThinGlass is an explicit compiled material class, so only the dielectric
    // fields needed by this narrow shader are read from its extension record.
    // Profile defaults keep malformed content transmissive rather than turning
    // a missing extension into an opaque black pane.
    float transmissionFactor = 0.90;
    float ior = 1.50;
    float specularFactor = 1.0;
    vec3 thinTransmissionTint = vec3(1.0);
    bool hasMaterialExtension =
        material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
    if (hasMaterialExtension)
    {
        vec4 transmission;
        vec4 dispersion;
        ReadForwardThinGlassOptics(
            uint(material.ExtensionDataIndex),
            transmission,
            dispersion);
        transmissionFactor = clamp(transmission.x, 0.0, 1.0);
        ior = clamp(transmission.y, 1.0, 3.0);
        thinTransmissionTint = clamp(
            dispersion.yzw,
            vec3(0.0),
            vec3(1.0));
        if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR) != 0u)
        {
            GPUMaterialExtensionData optics = ReadForwardMaterialExtension(
                uint(material.ExtensionDataIndex), MATERIAL_FEATURE_SPECULAR);
            specularFactor = clamp(optics.SpecularColor.a, 0.0, 1.0);
        }
    }

    SimpleDdgiGatherResult gather = EmptySimpleDdgiGatherResult();
    vec3 ddgiDirectionalRadiance = vec3(0.0);
    float ddgiDirectionalConfidence = 0.0;
    bool gatherContributed = false;
    float radiometricOwnership = 0.0;
    float leakAttenuation = 0.0;
    if (specularFactor > 0.0 && ForwardGlobalIlluminationEnabled() != 0u)
    {
        SimpleDdgiParams params = ReadSimpleDdgiParams(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
        uint directionalMode = SimpleDdgiDirectionalRadianceMode(
            params.residencyFlags);
        uint glossyMode = SimpleDdgiGlossyTransportMode(
            params.residencyFlags);
        bool configured =
            (params.flags &
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
            params.probeCount > 0u &&
            directionalMode != SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_OFF &&
            glossyMode != SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_OFF;
        if (configured)
        {
            SetSimpleDdgiDirectionalRadianceQuery(
                reflect(-viewDirection, normal),
                roughness);
            SetSimpleDdgiDirectionalRadianceQueryEligibilityWeight(1.0);
            gather = SampleSimpleDdgiThinGlassDirectionalGather(
                params,
                fragWorldPosition,
                geometricNormal,
                viewDirection);
            radiometricOwnership = SimpleDdgiRadiometricOwnership(gather);
            leakAttenuation = SimpleDdgiLeakAttenuation(gather, params);
            gatherContributed = radiometricOwnership > 0.000001;
            ddgiDirectionalRadiance = gather.directionalRadiance *
                max(params.indirectIntensity, 0.0);
            // The compact L1 glass receiver owns low-frequency local scene
            // radiance. Its deliberately unresolved high-frequency share is
            // filled by the global environment, preserving crisp highlights
            // without falling back to manually placed probes.
            // L1 represents a larger fraction of a broad/frosted lobe than a
            // sharp pane. Reserve the unresolved sharp band for the global HDR
            // environment so windows retain readable reflections without a
            // local probe, while DDGI remains the authoritative local base.
            float representableFrequencyShare = mix(0.55, 0.85, roughness);
            ddgiDirectionalConfidence = clamp(
                gather.directionalRadianceSupport *
                    radiometricOwnership * representableFrequencyShare,
                0.0,
                1.0);
        }
    }

    GPUEnvironmentData environment = ReadEnvironmentData();
    vec3 reflectedSpecular = vec3(0.0);
    if (specularFactor > 0.0 && environment.Enabled != 0u)
    {
        vec3 reflectionDirection = reflect(-viewDirection, normal);
        float maxLod = max(
            float(environment.PrefilteredMipCount) - 1.0,
            0.0);
        float nDotV = max(dot(normal, viewDirection), 0.0);
        vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
            ior,
            1.0,
            vec3(1.0));
        vec3 fresnel = FresnelSchlickIndirectRoughness(
            nDotV,
            dielectricF0,
            roughness);
        vec2 brdf = texture(
            BindlessTextures[nonuniformEXT(environment.BrdfLutTextureIndex)],
            vec2(nDotV, roughness)).rg;
        bool reflectionDebugActive;
        vec3 reflectionDebugColor;
        reflectedSpecular = EvaluateTransparentReflectionSpecular(
            environment,
            fragWorldPosition,
            geometricNormal,
            reflectionDirection,
            roughness * maxLod,
            roughness,
            reflectionSchedulingRoughness,
            brdf,
            fresnel,
            1.0,
            ddgiDirectionalRadiance,
            ddgiDirectionalConfidence,
            false,
            ForwardMaterialSamplesSceneReflections(material, false),
            reflectionDebugActive,
            reflectionDebugColor);
        if (reflectionDebugActive)
        {
            WriteForwardColor(vec4(reflectionDebugColor, 1.0));
            return;
        }
    }

    float glassNdotV = clamp(
        abs(dot(normalize(normal), viewDirection)),
        0.0,
        1.0);
    float glassF0Ratio = (ior - 1.0) / max(ior + 1.0, 0.0001);
    float glassF0 = glassF0Ratio * glassF0Ratio;
    float glassFresnel = specularFactor * (glassF0 +
        (1.0 - glassF0) * pow(1.0 - glassNdotV, 5.0));
    float tintTransmission = dot(
        thinTransmissionTint,
        vec3(0.2126, 0.7152, 0.0722));
    float glassOpacity = clamp(
        1.0 - transmissionFactor * tintTransmission *
            (1.0 - glassFresnel),
        0.08,
        1.0);
    float outputAlpha = min(materialCoverage.Alpha, glassOpacity);
    vec3 color = specularFactor * max(reflectedSpecular, vec3(0.0)) / glassOpacity;

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    EmitSimpleDdgiTransparentReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        outputAlpha);
#endif
#if OPTICAL_FORWARD_ACTIVE
    opticalReflectionWeight *= specularFactor / glassOpacity;
    OpticalExport(material, geometricNormal, normal, roughness, outputAlpha);
#endif
    WriteForwardColor(vec4(color, outputAlpha));
}
#else
void main()
{
    EnforceAutomaticPlanarCaptureClip();
#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
    // Every non-discard path, including diagnostic early returns, has a
    // defined source attachment value. The C5-capability gate excludes debug
    // views; this initialization is the final shader-side safety net.
    outDirectDiffuseAndEmissive = vec4(0.0);
    outNearFieldReceiverPayload = uvec4(0u);
#endif
#if NJULF_C4_RECEIVER_OUTPUT
    outGiCausticReceiverPayload = uvec4(0u);
#endif
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    outHybridReflectionReceiverPayload = uvec4(0u);
#if !NJULF_HYBRID_REFLECTION_SPARSE_LOBE_OUTPUT
    outHybridReflectionLobeExtension = uvec2(0u);
#endif
#endif
    uint debugViewMode = ForwardDebugViewMode();
    uint ambientOcclusionDebugView = ForwardAmbientOcclusionDebugView();
    WriteMaterialTransportProvenance(MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN);
    GPUMaterialData material = ReadForwardMaterial(fragMaterialIndex);
    if (debugViewMode == MATERIAL_DEBUG_TRANSPORT_PROFILE ||
        debugViewMode == MATERIAL_DEBUG_MATERIAL_REVISIONS)
    {
        LoadForwardMaterialDiagnosticMetadata(
            fragMaterialIndex,
            material);
    }
#if FORWARD_THIN_GLASS_ONLY || \
    FORWARD_TRANSPARENT_ROLE_ORDINARY || \
    FORWARD_TRANSPARENT_ROLE_THICK
    const bool geometryDecal = false;
#elif FORWARD_TRANSPARENT_ROLE_DECAL
    const bool geometryDecal = true;
#else
    bool geometryDecal = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_GEOMETRY_DECAL);
#endif
    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_INVOCATION_COUNTER);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
    {
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_BACKFACE_KILLED_COUNTER);
        discard;
    }

    if (IsAnimationDebugView(debugViewMode))
    {
        GPUObjectData objectData = ReadInstanceData(pc.Push.CurrentFrameIndex, fragObjectIndex);
        if (objectData.SkinningEnabled != 0)
        {
            vec3 skinnedColor = debugViewMode == ANIMATION_DEBUG_SKINNED_OBJECTS
                ? vec3(1.0, 0.0, 0.85)
                : MeshletDebugColor(fragMeshletIndex);
            WriteForwardColor(vec4(skinnedColor, 1.0));
            return;
        }

        discard;
    }

#if FORWARD_SIMPLE_MATERIAL
    bool hasMaterialExtension = false;
#else
    bool hasMaterialExtension = material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
#endif
    GPUMaterialExtensionData materialExtension;
    if (hasMaterialExtension)
        materialExtension = ReadForwardMaterialExtension(
            uint(material.ExtensionDataIndex),
            material.FeatureFlags);
    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);

    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    // Coverage and visible albedo use the same transformed base-color sample.
    // Sampling twice was especially expensive for large alpha-blended decal
    // overlays and provided no semantic difference.
    MaterialAlphaCoverage materialCoverage = ResolveMaterialAlphaCoverage(
        material,
        albedoSample,
        fragVertexColor.a);
    float alphaMode = materialCoverage.AlphaMode;
    float alphaCutoff = materialCoverage.AlphaCutoff;
    float outputAlpha = materialCoverage.Alpha;
    forwardDebugOutputAlpha =
        alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha;

    if (!MaterialCoverageSurvivesForward(materialCoverage) && !NJULF_VISIBILITY_COVERAGE_AUTHORITY)
    {
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_COVERAGE_KILLED_COUNTER);
        discard;
    }

#if defined(FORWARD_SIMPLE_OPAQUE) && !defined(NJULF_VISIBILITY_COMPUTE) && FORWARD_SIMPLE_VERTEX_INPUT && \
    NJULF_COMMON_SURFACE_COVERAGE_DIAGNOSTICS
    RecordCommonSurfaceMaterialCoverage(material);
#endif

    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_SURVIVING_COUNTER);

    if (debugViewMode == DEBUG_VIEW_MESHLETS)
    {
        WriteForwardColor(vec4(MeshletDebugColor(fragMeshletIndex), 1.0));
        return;
    }

    uint transparencyDebugView = ForwardTransparencyDebugView();
    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_MODE)
    {
        vec3 modeColor = alphaMode < 0.5 ? vec3(0.1, 0.8, 0.2) :
            alphaMode < 1.5 ? vec3(0.95, 0.85, 0.1) :
            vec3(0.2, 0.55, 1.0);
        WriteForwardColor(vec4(modeColor, 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_VALUE)
    {
        WriteForwardColor(vec4(vec3(outputAlpha), 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_CUTOFF)
    {
        WriteForwardColor(vec4(vec3(alphaCutoff), 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_SORT_ORDER)
    {
        WriteForwardColor(vec4(MeshletDebugColor(fragMeshletIndex), alphaMode > 1.5 ? max(outputAlpha, 0.25) : 1.0));
        return;
    }

    vec3 geometricNormal = normalize(fragNormal) * (gl_FrontFacing ? 1.0 : -1.0);
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
#if FORWARD_DDGI_RECEIVER_CACHE_LEGACY
    ForwardDdgiReceiverCacheAdmission receiverCacheAdmission;
    receiverCacheAdmission.EntryIndex = ForwardDdgiReceiverCacheEntryIndex(
        ForwardScreenPixel(),
        pc.Push.ScreenDimensions);
    receiverCacheAdmission.Reason = SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED;
    bool receiverCacheAccepted = true;
    RecordLegacyForwardDdgiReceiverCacheAdmission(
        pc.Push.CurrentFrameIndex);
#else
    // Resolve the complementary split before normal and material-extension
    // shading. Coverage and sidedness have already matched the depth-prepass
    // survivor, so discarding here cannot create a coverage disagreement.
    ForwardDdgiReceiverCacheAdmission receiverCacheAdmission =
        EvaluateForwardDdgiReceiverCacheAdmission(
            ForwardScreenPixel(),
            gl_FragCoord.z,
            fragWorldPosition,
            geometricNormal,
            pc.Push);
    bool receiverCacheAccepted =
        ForwardDdgiReceiverCacheAdmissionAccepted(receiverCacheAdmission);
    RecordForwardDdgiReceiverCacheAdmission(
        pc.Push.CurrentFrameIndex,
        receiverCacheAdmission.Reason);
#if FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
    if (!receiverCacheAccepted)
        discard;
#elif FORWARD_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_ONLY
    if (receiverCacheAccepted)
        discard;
#else
    if (NjulfReceiverCacheAcceptedLane() && !receiverCacheAccepted)
        discard;
    if (NjulfReceiverCacheExactFallbackLane() && receiverCacheAccepted)
        discard;
#endif
#endif
#if NJULF_DDGI_RECEIVER_CACHE_DEBUG_VIEW
    WriteForwardColor(vec4(
        ForwardDdgiReceiverCacheAdmissionDebugColor(
            receiverCacheAdmission.Reason),
        1.0));
    return;
#endif
#endif
    vec3 shadowNormal = geometricNormal;
    bool useNormalTexture = material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
        material.NormalScaleBias.x > 0.001;
    vec3 normal = useNormalTexture
        ? ResolveNormal(
            material,
            fragNormal,
            fragWorldTangent,
            MaterialUv(
                material.TextureTexCoordSets.y,
                material.NormalOffsetScale,
                material.TextureRotations.y))
        : geometricNormal;
    vec3 diffuseIndirectNormal = normal;
    vec3 ddgiNormal = geometricNormal;
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // glTF metallic-roughness contract: G = roughness and B = metallic.
    // Occlusion is an independent binding even when it aliases the same image.
    vec2 metallicRoughnessUv = MaterialUv(
        material.TextureTexCoordSets.z,
        material.MetallicRoughnessOffsetScale,
        material.TextureRotations.z);
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            metallicRoughnessUv);
    // The upload contract binds DefaultWhiteTexture for a missing emissive texture.
    // Sample independently of the factor: material.Emissive is the authoritative black
    // default, while a texture-only material remains valid when its factor is non-zero.
    vec4 emissiveSample = material.EmissiveTextureIndex ==
            DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(
            material.EmissiveTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.w,
                material.EmissiveOffsetScale,
                material.TextureRotations.w));

    float authoredRoughness = clamp(
        material.MetallicRoughnessAO.y * armSample.g,
        0.04,
        1.0);
    float reflectionFootprintRoughness = authoredRoughness;
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    if (material.MetallicRoughnessTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        // A derivative-only variance estimate covers one fragment quad, but a
        // dark roughness island can span several quads after texture
        // minification. Such an island scheduled an isolated SSR/ray-query
        // source inside otherwise broad Sponza stone and cloth. A single
        // coarser mip-footprint sample provides the conservative lobe width
        // used by the deferred reflection receiver. Continuous polished
        // regions remain polished because both footprints agree.
        float footprintRoughness = clamp(
            material.MetallicRoughnessAO.y *
                SampleMaterialTextureFootprint(
                    material.MetallicRoughnessTextureIndex,
                    metallicRoughnessUv,
                    4.0).g,
            0.04,
            1.0);
        reflectionFootprintRoughness = max(
            reflectionFootprintRoughness,
            footprintRoughness);
    }
#endif
    float roughness = authoredRoughness;
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    // Preserve the isotropic lobe width for reflection scheduling. The
    // anisotropic BRDF adjustment below sharpens one axis, but a brushed lobe
    // remains broad overall and does not warrant isotropic full-rate tracing.
    float reflectionSchedulingRoughness = roughness;
    float sampledOcclusion = material.OcclusionTextureIndex == DEFAULT_WHITE_TEXTURE
        ? 1.0
        : SampleMaterialTexture(
            material.OcclusionTextureIndex,
            MaterialUv(
                material.OcclusionBinding.y,
                material.OcclusionOffsetScale,
                material.OcclusionBinding.x)).r;
    float ambientOcclusion = EvaluateGiMaterialOcclusion(
        material.MetallicRoughnessAO.z,
        sampledOcclusion);
    float screenSpaceAo = SampleScreenSpaceAo();
    float indirectAo = clamp(ambientOcclusion * screenSpaceAo, 0.0, 1.0);
    // Probe visibility owns broad transport occlusion, but cannot represent
    // sub-probe contacts. Retain half of screen-space AO for that missing local
    // band instead of either double-darkening DDGI or leaving it uniformly flat.
    float ddgiIndirectAo = mix(1.0, screenSpaceAo, 0.5);
    vec3 albedo = max(material.Albedo.rgb * albedoSample.rgb * fragVertexColor.rgb, vec3(0.0));
    vec3 emissive = max(material.Emissive.rgb * emissiveSample.rgb, vec3(0.0));

    float clearcoatFactor = 0.0;
    float clearcoatRoughness = 0.04;
    vec3 sheenColor = vec3(0.0);
    float sheenRoughness = 0.0;
    float anisotropyStrength = 0.0;
    float transmissionFactor = 0.0;
    float ior = 1.5;
    float transmissionThickness = 0.0;
    float attenuationDistance = 0.0;
    vec3 attenuationColor = vec3(1.0);
    vec3 subsurfaceColor = vec3(1.0);
    float subsurfaceStrength = 0.0;
    float specularFactor = 1.0;
    vec3 specularColor = vec3(1.0);
    float iridescenceFactor = 0.0;
    float iridescenceThickness = 0.0;
    float dispersion = 0.0;
    vec3 thinTransmissionTint = vec3(1.0);
    vec3 clearcoatNormal = normal;
    // ThinSurface is normally a GI-only transport contract (for example,
    // opaque curtains). ThinGlass is the explicit visible dielectric opt-in;
    // keeping these independent prevents cloth from becoming accidental glass.
#if FORWARD_THIN_GLASS_ONLY
    const bool thinGiTransport = true;
    const bool thinGlass = true;
    const bool volumeGiTransport = false;
#elif FORWARD_TRANSPARENT_ROLE_DECAL
    const bool thinGiTransport = false;
    const bool thinGlass = false;
    const bool volumeGiTransport = false;
#elif FORWARD_TRANSPARENT_ROLE_THICK
    const bool thinGiTransport = false;
    const bool thinGlass = false;
    const bool volumeGiTransport = true;
#elif FORWARD_TRANSPARENT_ROLE_ORDINARY
    bool thinGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    bool thinGlass = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_GLASS);
    const bool volumeGiTransport = false;
#else
    bool thinGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    bool thinGlass = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_GLASS);
    bool volumeGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_VOLUME_TRANSMISSION);
#endif
    bool rasterTransmissionEnabled =
#if FORWARD_TRANSPARENT_ROLE_DECAL
        false;
#else
        (material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u &&
        (!thinGiTransport || thinGlass);
#endif

    if (hasMaterialExtension)
    {
        if ((material.FeatureFlags & MATERIAL_FEATURE_EMISSIVE_STRENGTH) != 0u)
            emissive *= materialExtension.Clearcoat.w;

        if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT) != 0u)
        {
            clearcoatFactor = clamp(materialExtension.Clearcoat.x, 0.0, 1.0);
            clearcoatRoughness = clamp(materialExtension.Clearcoat.y, 0.04, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT_TEXTURE) != 0u)
                clearcoatFactor *= SampleMaterialTexture(materialExtension.ClearcoatTextureIndex, ExtensionUv(materialExtension.ClearcoatOffsetScale, materialExtension.ExtensionTextureRotations0.x, materialExtension.ExtensionTextureTexCoordSets0.x)).r;
            if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT_ROUGHNESS_TEXTURE) != 0u)
                clearcoatRoughness = clamp(clearcoatRoughness * SampleMaterialTexture(materialExtension.ClearcoatRoughnessTextureIndex, ExtensionUv(materialExtension.ClearcoatRoughnessOffsetScale, materialExtension.ExtensionTextureRotations0.y, materialExtension.ExtensionTextureTexCoordSets0.y)).g, 0.04, 1.0);
            if ((material.FeatureFlags &
                    MATERIAL_FEATURE_CLEARCOAT_NORMAL_TEXTURE) != 0u)
            {
                vec2 clearcoatUv = ExtensionUv(
                    materialExtension.ClearcoatNormalOffsetScale,
                    materialExtension.ExtensionTextureRotations0.z,
                    materialExtension.ExtensionTextureTexCoordSets0.z);
                vec3 clearcoatTangentNormal =
                    SampleMaterialTextureFootprint(
                        materialExtension.ClearcoatNormalTextureIndex,
                        clearcoatUv,
                        4.0).xyz * 2.0 - 1.0;
                clearcoatTangentNormal.xy *=
                    materialExtension.Clearcoat.z;
                clearcoatTangentNormal.z = sqrt(max(
                    0.0,
                    1.0 - dot(
                        clearcoatTangentNormal.xy,
                        clearcoatTangentNormal.xy)));
                float facingSign = gl_FrontFacing ? 1.0 : -1.0;
                clearcoatNormal = normalize(
                    BuildOrthonormalTbn(
                        fragNormal,
                        fragWorldTangent,
                        facingSign) *
                    normalize(clearcoatTangentNormal));
            }
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN) != 0u)
        {
            sheenColor = max(materialExtension.SheenColor.rgb, vec3(0.0));
            sheenRoughness = clamp(materialExtension.SheenColor.a, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE) != 0u)
                sheenColor *= SampleMaterialTexture(materialExtension.SheenColorTextureIndex, ExtensionUv(materialExtension.SheenColorOffsetScale, materialExtension.ExtensionTextureRotations0.w, materialExtension.ExtensionTextureTexCoordSets0.w)).rgb;
            if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN_ROUGHNESS_TEXTURE) != 0u)
                sheenRoughness = clamp(sheenRoughness * SampleMaterialTexture(materialExtension.SheenRoughnessTextureIndex, ExtensionUv(materialExtension.SheenRoughnessOffsetScale, materialExtension.ExtensionTextureRotations1.x, materialExtension.ExtensionTextureTexCoordSets1.x)).a, 0.0, 1.0);
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_ANISOTROPY) != 0u)
        {
            anisotropyStrength = clamp(materialExtension.Anisotropy.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_ANISOTROPY_TEXTURE) != 0u)
                anisotropyStrength *= SampleMaterialTexture(materialExtension.AnisotropyTextureIndex, ExtensionUv(materialExtension.AnisotropyOffsetScale, materialExtension.ExtensionTextureRotations1.y, materialExtension.ExtensionTextureTexCoordSets1.y)).b;
            roughness = clamp(mix(roughness, roughness * 0.65, anisotropyStrength), 0.04, 1.0);
        }

        if (rasterTransmissionEnabled ||
            (material.FeatureFlags & MATERIAL_FEATURE_IOR) != 0u)
        {
            ior = clamp(materialExtension.Transmission.y, 1.0, 3.0);
        }

        if (rasterTransmissionEnabled)
        {
            transmissionFactor = clamp(materialExtension.Transmission.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION_TEXTURE) != 0u)
                transmissionFactor *= SampleMaterialTexture(materialExtension.TransmissionTextureIndex, ExtensionUv(materialExtension.TransmissionOffsetScale, materialExtension.ExtensionTextureRotations1.z, materialExtension.ExtensionTextureTexCoordSets1.z)).r;
            transmissionThickness = max(materialExtension.Transmission.z, 0.0);
            attenuationDistance = max(materialExtension.Transmission.w, 0.0);
            attenuationColor = max(materialExtension.AttenuationColor.rgb, vec3(0.0));
            if ((material.FeatureFlags & MATERIAL_FEATURE_VOLUME_APPROXIMATION) != 0u)
                transmissionThickness *= SampleMaterialTexture(materialExtension.ThicknessTextureIndex, ExtensionUv(materialExtension.ThicknessOffsetScale, materialExtension.ExtensionTextureRotations1.w, materialExtension.ExtensionTextureTexCoordSets1.w)).g;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE) != 0u)
        {
            subsurfaceColor = max(materialExtension.Subsurface.rgb, vec3(0.0));
            subsurfaceStrength = clamp(materialExtension.Subsurface.a, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE_TEXTURE) != 0u)
                subsurfaceColor *= SampleMaterialTexture(materialExtension.SubsurfaceTextureIndex, ExtensionUv(materialExtension.SubsurfaceOffsetScale, materialExtension.ExtensionTextureRotations3.x, materialExtension.ExtensionTextureTexCoordSets3.x)).rgb;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR) != 0u)
        {
            specularFactor = clamp(materialExtension.SpecularColor.a, 0.0, 1.0);
            specularColor = max(materialExtension.SpecularColor.rgb, vec3(0.0));
            if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_TEXTURE) != 0u)
                specularFactor *= SampleMaterialTexture(materialExtension.SpecularTextureIndex, ExtensionUv(materialExtension.SpecularOffsetScale, materialExtension.ExtensionTextureRotations2.x, materialExtension.ExtensionTextureTexCoordSets2.x)).a;
            if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE) != 0u)
                specularColor *= SampleMaterialTexture(materialExtension.SpecularColorTextureIndex, ExtensionUv(materialExtension.SpecularColorOffsetScale, materialExtension.ExtensionTextureRotations2.y, materialExtension.ExtensionTextureTexCoordSets2.y)).rgb;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE) != 0u)
        {
            iridescenceFactor = clamp(materialExtension.Iridescence.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE_TEXTURE) != 0u)
                iridescenceFactor *= SampleMaterialTexture(materialExtension.IridescenceTextureIndex, ExtensionUv(materialExtension.IridescenceOffsetScale, materialExtension.ExtensionTextureRotations2.z, materialExtension.ExtensionTextureTexCoordSets2.z)).r;
            float minThickness = min(materialExtension.Iridescence.z, materialExtension.Iridescence.w);
            float maxThickness = max(materialExtension.Iridescence.z, materialExtension.Iridescence.w);
            float thicknessSample = (material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE_THICKNESS_TEXTURE) != 0u
                ? SampleMaterialTexture(materialExtension.IridescenceThicknessTextureIndex, ExtensionUv(materialExtension.IridescenceThicknessOffsetScale, materialExtension.ExtensionTextureRotations2.w, materialExtension.ExtensionTextureTexCoordSets2.w)).g
                : 1.0;
            iridescenceThickness = mix(minThickness, maxThickness, clamp(thicknessSample, 0.0, 1.0));
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_DISPERSION) != 0u)
        {
            dispersion = clamp(materialExtension.Dispersion.x, 0.0, 1.0);
        }
        thinTransmissionTint = clamp(
            materialExtension.Dispersion.yzw,
            vec3(0.0),
            vec3(1.0));
    }

    roughness = ApplyGeometricSpecularAntialiasing(
        roughness,
        normal);
    clearcoatRoughness = ApplyGeometricSpecularAntialiasing(
        clearcoatRoughness,
        clearcoatNormal);
    reflectionSchedulingRoughness = max(
        roughness,
        EstimateReflectionSchedulingRoughness(
            roughness,
            reflectionFootprintRoughness,
            normal));

#ifdef NJULF_VISIBILITY_COMPUTE
    // Helper lanes supply the same primitive's material derivatives only.
    if (!VisibilityCovered)
        return;
#endif
    bool reflectsIndirectDiffuse = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE);
    vec3 directionalDiffuseBase = reflectsIndirectDiffuse
        ? EvaluateGiDirectionalDiffuseBase(
            albedo,
            metallic,
            transmissionFactor,
            clearcoatFactor,
            sheenColor)
        : vec3(0.0);
    vec3 canonicalDiffuseReflectance = reflectsIndirectDiffuse
        ? EvaluateGiHemisphericalDiffuseReflectance(
            albedo,
            metallic,
            ior,
            specularFactor,
            specularColor,
            transmissionFactor,
            clearcoatFactor,
            sheenColor,
            max(dot(normal, viewDirection), 0.0))
        : vec3(0.0);
    if (thinGlass)
    {
        // The Bistro glass base color is a transmission tint, not a Lambertian
        // paint layer. Keep the material in GI transport so it can transmit
        // energy, but remove diffuse raster lighting from the visible sheet.
        directionalDiffuseBase = vec3(0.0);
        canonicalDiffuseReflectance = vec3(0.0);
    }

    subsurfaceColor = clamp(
        subsurfaceColor,
        vec3(0.0),
        vec3(1.0));
    subsurfaceStrength = clamp(subsurfaceStrength, 0.0, 1.0);
    vec3 subsurfaceDirectionalDiffuseBase =
        EvaluateGiSubsurfaceDiffuseBudget(
            directionalDiffuseBase,
            subsurfaceColor);
    vec3 subsurfaceDiffuseReflectance =
        EvaluateGiSubsurfaceDiffuseBudget(
            canonicalDiffuseReflectance,
            subsurfaceColor);
    bool subsurfaceBacklightingActive =
        subsurfaceStrength > 0.000001 &&
        any(greaterThan(
            subsurfaceDirectionalDiffuseBase,
            vec3(0.000001)));

    if (IsMaterialDebugView(debugViewMode))
    {
        if (debugViewMode == MATERIAL_DEBUG_FEATURE_FLAGS)
        {
            WriteForwardColor(vec4(MaterialFeatureFlagsDebugColor(material.FeatureFlags), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_BASE_COLOR)
        {
            WriteForwardColor(vec4(albedo, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_METALLIC)
        {
            WriteForwardColor(vec4(vec3(metallic), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(roughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_NORMAL_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(clamp(material.NormalScaleBias.x, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_WORLD_NORMAL)
        {
            WriteForwardColor(vec4(normal * 0.5 + vec3(0.5), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_EMISSIVE_INTENSITY)
        {
            float emissiveIntensity = clamp(log2(1.0 + MaxComponent(emissive)) / 6.0, 0.0, 1.0);
            WriteForwardColor(vec4(vec3(emissiveIntensity), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CLEARCOAT_FACTOR)
        {
            WriteForwardColor(vec4(vec3(clearcoatFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CLEARCOAT_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(clearcoatRoughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHEEN_COLOR)
        {
            WriteForwardColor(vec4(sheenColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHEEN_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(sheenRoughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ANISOTROPY_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(anisotropyStrength), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ANISOTROPY_DIRECTION)
        {
            float anisotropyRotation = hasMaterialExtension ? materialExtension.Anisotropy.y : 0.0;
            vec2 direction = vec2(cos(anisotropyRotation), sin(anisotropyRotation)) * anisotropyStrength;
            WriteForwardColor(vec4(direction * 0.5 + vec2(0.5), anisotropyStrength, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_TRANSMISSION)
        {
            WriteForwardColor(vec4(vec3(transmissionFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IOR)
        {
            WriteForwardColor(vec4(vec3(clamp((ior - 1.0) * 0.5, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_VOLUME_THICKNESS)
        {
            WriteForwardColor(vec4(vec3(clamp(transmissionThickness, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ATTENUATION_COLOR)
        {
            WriteForwardColor(vec4(attenuationColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SUBSURFACE_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(subsurfaceStrength), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SPECULAR_FACTOR)
        {
            WriteForwardColor(vec4(vec3(specularFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SPECULAR_COLOR)
        {
            WriteForwardColor(vec4(specularColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IRIDESCENCE_FACTOR)
        {
            WriteForwardColor(vec4(vec3(iridescenceFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IRIDESCENCE_THICKNESS)
        {
            WriteForwardColor(vec4(vec3(clamp(iridescenceThickness / 1200.0, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_DISPERSION)
        {
            WriteForwardColor(vec4(vec3(dispersion), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_MATERIAL_OCCLUSION)
        {
            WriteForwardColor(vec4(vec3(ambientOcclusion), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CANONICAL_DIFFUSE_REFLECTANCE)
        {
            WriteForwardColor(vec4(canonicalDiffuseReflectance, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_COMPILED_EMISSION)
        {
            vec3 displayEmission = emissive / (vec3(1.0) + emissive);
            WriteForwardColor(vec4(displayEmission, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_GEOMETRIC_NORMAL)
        {
            WriteForwardColor(vec4(geometricNormal * 0.5 + vec3(0.5), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_OPACITY)
        {
            WriteForwardColor(vec4(vec3(outputAlpha), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SIDEDNESS)
        {
            vec3 sidedness = doubleSided
                ? (gl_FrontFacing ? vec3(0.1, 0.8, 1.0) : vec3(1.0, 0.45, 0.1))
                : vec3(0.2, 0.85, 0.25);
            WriteForwardColor(vec4(sidedness, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHADING_MODEL)
        {
            vec3 modelColor = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_UNLIT)
                ? vec3(1.0, 0.65, 0.1)
                : (material.FeatureFlags & MATERIAL_FEATURE_FOLIAGE) != 0u
                    ? vec3(0.15, 0.85, 0.25)
                    : (material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE) != 0u
                        ? vec3(1.0, 0.25, 0.55)
                        : vec3(0.2, 0.55, 1.0);
            WriteForwardColor(vec4(modelColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_TRANSPORT_PROFILE)
        {
            vec3 validity = vec3(
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_DIFFUSE_PROFILE_VALID) ? 1.0 : 0.0,
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_EMISSION_PROFILE_VALID) ? 1.0 : 0.0,
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_ALPHA_PROFILE_VALID) ? 1.0 : 0.0);
            float quality = clamp(float(material.TransportProfileQuality) / 3.0, 0.0, 1.0);
            if (GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_COMPACT_TEXTURE_FALLBACK))
                validity = mix(validity, vec3(1.0, 0.0, 1.0), 0.5);
            WriteForwardColor(vec4(validity * mix(0.3, 1.0, quality), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_MATERIAL_REVISIONS)
        {
            // Three deliberately incommensurate multipliers keep independently
            // changing revisions visually distinct: red=material publication,
            // green=texture-content publication, blue=transport profile.
            float materialRevision = fract(float(material.MaterialRevision) * 0.61803398875);
            float textureRevision = fract(float(material.TextureContentRevision) * 0.56984029099);
            float profileRevision = fract(float(material.TransportProfileRevision) * 0.75487766625);
            WriteForwardColor(vec4(materialRevision, textureRevision, profileRevision, 1.0));
            return;
        }
    }

    bool unlit = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_UNLIT);
    if (unlit)
    {
        // KHR_materials_unlit is base-color only. The trace source was cleared
        // before material evaluation, so unlit surfaces neither receive nor
        // reflect diffuse GI unless a future named transport override opts in.
        if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE ||
            debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR)
        {
            WriteForwardColor(vec4(0.0, 0.0, 0.0, 1.0));
            return;
        }
        WriteForwardColor(vec4(albedo, outputAlpha));
        return;
    }

    vec3 diffuseReflectance = canonicalDiffuseReflectance;

#if NJULF_GTAO_BENT_NORMAL_LIGHTING
    // Resolve only after material-debug and unlit early-outs. The sample is
    // shared by environment diffuse and, at Ultra, the exact DDGI lookup.
    bool bentNormalValid = TryResolveIndirectDiffuseNormal(
        normal,
        geometricNormal,
        diffuseIndirectNormal);
    if (ForwardAmbientOcclusionBentNormalMode() == 2u && bentNormalValid)
        ddgiNormal = diffuseIndirectNormal;
#endif

    vec3 diffuseIbl = vec3(0.0);
    vec3 specularIbl = vec3(0.0);
    bool reflectionDebugActive = false;
    vec3 reflectionDebugColor = vec3(0.0);
    vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
        ior,
        specularFactor,
        specularColor);

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    // Cache-required programs retain the established ordering because their
    // functional attribution atomics are part of the pinned shader contract.
#include "forward_ddgi_receiver_gather.glsl"
#endif

    GPUEnvironmentData environment = ReadEnvironmentData();
    vec3 directLighting = vec3(0.0);
    vec3 directDiffuseSource = vec3(0.0);
    vec3 directBackDiffuseSource = vec3(0.0);
    float lastShadowFactor = 1.0;
    uint lastShadowCascade = 0u;
    vec3 lastShadowEvaluationNormal = shadowNormal;

#if !FORWARD_INCOMPATIBLE_DEBUG_VIEWS_STATIC_NONE
    if (environment.DebugView == ENVIRONMENT_DEBUG_AMBIENT_OCCLUSION)
    {
        WriteForwardColor(vec4(vec3(indirectAo), 1.0));
        return;
    }
#endif

    if (ambientOcclusionDebugView == AO_DEBUG_FINAL)
    {
        WriteForwardColor(vec4(vec3(indirectAo), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_RECONSTRUCTED_NORMAL)
    {
        vec2 uv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        WriteForwardColor(vec4(ReconstructNormalFromDepth(uv) * 0.5 + vec3(0.5), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_LINEAR_DEPTH)
    {
        ivec2 depthSize = textureSize(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0);
        ivec2 pixel = ivec2(clamp(ForwardScreenPixel(), vec2(0.0), vec2(depthSize - ivec2(1))));
        vec2 screenUv = (vec2(pixel) + vec2(0.5)) / vec2(depthSize);
        float depth = FetchDepthAtPixel(pixel, depthSize);
        vec3 viewPosition = ReconstructViewPositionFromDepth(screenUv, depth);
        vec3 farPosition = ReconstructViewPositionFromDepth(vec2(0.5), 0.0);
        float farDepth = max(abs(farPosition.z), 0.0001);
        float linearDepth = clamp(abs(viewPosition.z) / farDepth, 0.0, 1.0);
        float visibleDepth = sqrt(linearDepth);
        WriteForwardColor(vec4(vec3(visibleDepth), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_MAP_PREVIEW)
    {
        vec2 previewUv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        uint cascadeCount = max(uint(ReadShadowIndices().y + 0.5), 1u);
        uint previewCascade = min(ForwardDirectionalShadowPreviewCascade(), cascadeCount - 1u);
        uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + previewCascade;
        float depth = texture(BindlessTextures[nonuniformEXT(textureIndex)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SPOT_ATLAS_PREVIEW)
    {
        vec2 previewUv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    uint directionalLightCount = ForwardDirectionalLightCount(pc.Push);
    float directionalShadowFactor = 1.0;
    uint directionalShadowCascade = 0u;
    vec3 directionalShadowEvaluationNormal = shadowNormal;
    int configuredDirectionalShadowLightIndex =
        int(round(ReadShadowIndices().w));
    if (directionalLightCount > 0u)
    {
        uint directionalLightIndex =
            ForwardDirectionalLightIndex(pc.Push, 0u);
        AccumulateLight(
            directionalLightIndex,
            albedo,
            metallic,
            directionalDiffuseBase,
            subsurfaceDirectionalDiffuseBase,
            subsurfaceBacklightingActive,
            roughness,
            dielectricF0,
            clearcoatFactor,
            clearcoatRoughness,
            clearcoatNormal,
            sheenColor,
            sheenRoughness,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            geometryDecal,
            lastShadowFactor,
            lastShadowCascade,
            lastShadowEvaluationNormal,
            directLighting,
            directDiffuseSource,
            directBackDiffuseSource);
        if (int(directionalLightIndex) ==
                configuredDirectionalShadowLightIndex ||
            lastShadowFactor < 1.0 || lastShadowCascade != 0u)
        {
            directionalShadowFactor = lastShadowFactor;
            directionalShadowCascade = lastShadowCascade;
            directionalShadowEvaluationNormal =
                lastShadowEvaluationNormal;
        }
    }
    if (directionalLightCount > 1u)
    {
        uint directionalLightIndex =
            ForwardDirectionalLightIndex(pc.Push, 1u);
        AccumulateLight(
            directionalLightIndex,
            albedo,
            metallic,
            directionalDiffuseBase,
            subsurfaceDirectionalDiffuseBase,
            subsurfaceBacklightingActive,
            roughness,
            dielectricF0,
            clearcoatFactor,
            clearcoatRoughness,
            clearcoatNormal,
            sheenColor,
            sheenRoughness,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            geometryDecal,
            lastShadowFactor,
            lastShadowCascade,
            lastShadowEvaluationNormal,
            directLighting,
            directDiffuseSource,
            directBackDiffuseSource);
        // Prefer the configured shadow owner even when its exact result is the
        // fully-lit cascade-zero sentinel. Retain the non-default fallback for
        // diagnostic configurations that do not publish an owner index.
        if (int(directionalLightIndex) ==
                configuredDirectionalShadowLightIndex ||
            lastShadowFactor < 1.0 || lastShadowCascade != 0u)
        {
            directionalShadowFactor = lastShadowFactor;
            directionalShadowCascade = lastShadowCascade;
            directionalShadowEvaluationNormal =
                lastShadowEvaluationNormal;
        }
    }
    vec3 directionalShadowDebugColor;
    if (TryEvaluateDirectionalShadowDebug(
            debugViewMode,
            fragWorldPosition,
            directionalShadowEvaluationNormal,
            geometryDecal,
            directionalShadowFactor,
            directionalShadowDebugColor))
    {
        WriteForwardColor(vec4(directionalShadowDebugColor, 1.0));
        return;
    }

    if (pc.Push.LocalLightCount == 0u)
    {
        // Directional lights were handled above; there are no tiled local lights.
    }
    else
    {
        vec2 safeScreenSize = max(pc.Push.ScreenDimensions, vec2(1.0));
        uvec2 pixel = uvec2(clamp(
            ForwardScreenPixel(),
            vec2(0.0),
            safeScreenSize - vec2(1.0)));
        uvec2 tile = pixel / uvec2(
            FORWARD_CLUSTER_TILE_SIZE,
            FORWARD_CLUSTER_TILE_SIZE);
        uint tileCountX = uint(ceil(
            safeScreenSize.x / float(FORWARD_CLUSTER_TILE_SIZE)));
        uint tileCountY = uint(ceil(
            safeScreenSize.y / float(FORWARD_CLUSTER_TILE_SIZE)));
        float viewDepth = clamp(
            CameraForwardDistance(fragWorldPosition),
            FORWARD_CLUSTER_NEAR_PLANE,
            FORWARD_CLUSTER_FAR_PLANE);
        float normalizedClusterDepth =
            log(viewDepth / FORWARD_CLUSTER_NEAR_PLANE) /
            log(FORWARD_CLUSTER_FAR_PLANE /
                FORWARD_CLUSTER_NEAR_PLANE);
        uint depthSlice = min(
            uint(clamp(
                floor(normalizedClusterDepth *
                    float(FORWARD_CLUSTER_DEPTH_SLICE_COUNT)),
                0.0,
                float(FORWARD_CLUSTER_DEPTH_SLICE_COUNT - 1u))),
            FORWARD_CLUSTER_DEPTH_SLICE_COUNT - 1u);
        uint clusterIndex =
            (depthSlice * tileCountY + tile.y) * tileCountX + tile.x;
        GPUTiledLightHeader tileHeader =
            ReadTiledLightHeader(clusterIndex);

        for (uint i = 0u; i < tileHeader.LightCount; i++)
        {
            AccumulateLight(
                ReadTiledLightIndex(tileHeader.LightOffset + i),
                albedo,
                metallic,
                directionalDiffuseBase,
                subsurfaceDirectionalDiffuseBase,
                subsurfaceBacklightingActive,
                roughness,
                dielectricF0,
                clearcoatFactor,
                clearcoatRoughness,
                clearcoatNormal,
                sheenColor,
                sheenRoughness,
                normal,
                shadowNormal,
                viewDirection,
                fragWorldPosition,
                geometryDecal,
                lastShadowFactor,
                lastShadowCascade,
                lastShadowEvaluationNormal,
                directLighting,
                directDiffuseSource,
                directBackDiffuseSource);
        }
    }

    if (subsurfaceStrength > 0.0)
    {
        vec3 originalDirectDiffuseSource = directDiffuseSource;
        directDiffuseSource = ApplyGiSubsurfaceDiffuseSplit(
            originalDirectDiffuseSource,
            directBackDiffuseSource,
            subsurfaceStrength);
        directLighting +=
            directDiffuseSource - originalDirectDiffuseSource;
    }

#if !FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    // Exact-only receiver programs delay their structured result until the
    // direct-light loop has released its material and shadow intermediates.
#include "forward_ddgi_receiver_gather.glsl"
#endif

#if NJULF_C5_TRACE_RESOLUTION_SOURCE
    C5WriteDirectDiffuseAndEmissiveSource(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        directDiffuseSource,
        emissive);
    return;
#endif

    if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE)
    {
        WriteForwardColor(vec4(
            clamp(
                max(directDiffuseSource, vec3(0.0)),
                vec3(0.0),
                vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE)),
            1.0));
        return;
    }

    if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR)
    {
        // Both terms came from the same light loop and shadow samples, avoiding
        // a second BRDF implementation or persistent MRT.
        vec3 directSpecular = max(directLighting - directDiffuseSource, vec3(0.0));
        WriteForwardColor(vec4(
            clamp(
                directSpecular,
                vec3(0.0),
                vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE)),
            1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_RECEIVER_FACTOR)
    {
        WriteForwardColor(vec4(vec3(directionalShadowFactor), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_CASCADE_OVERLAY)
    {
        vec3 cascadeColor = directionalShadowCascade == 0u ? vec3(0.9, 0.15, 0.1) :
            directionalShadowCascade == 1u ? vec3(0.1, 0.75, 0.2) :
            directionalShadowCascade == 2u ? vec3(0.1, 0.35, 0.95) :
            vec3(0.9, 0.8, 0.1);
        directLighting = mix(directLighting, cascadeColor, 0.35);
    }

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_ONLY
    if (receiverCacheAccepted)
    {
        // Keep the radiance record out of the direct-light loop. Rejected
        // fragments never issue this sixteen-byte load.
        cachedGather = LoadForwardDdgiReceiverCache(
            receiverCacheAdmission.EntryIndex);
        indirectSpecularVisibility =
            SampleForwardDdgiReceiverCacheRoughSpecularVisibility(
                cachedGather,
                roughness);
    }
#endif

    EvaluateIbl(
        albedo,
        metallic,
        diffuseReflectance,
        roughness,
        reflectionSchedulingRoughness,
        dielectricF0,
        normal,
        diffuseIndirectNormal,
        geometricNormal,
        viewDirection,
        indirectAo,
        indirectSpecularVisibility,
        ddgiDirectionalRadiance,
        ddgiDirectionalConfidence,
        ForwardMaterialSamplesSceneReflections(material, geometryDecal),
        diffuseIbl,
        specularIbl,
        reflectionDebugActive,
        reflectionDebugColor);

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
    if ((!receiverCacheAccepted ||
         (ForwardAmbientOcclusionBentNormalMode() != 0u &&
          bentNormalValid)) &&
        environment.Enabled != 0u)
    {
        // EvaluateIbl deliberately skips diffuse environment work in the
        // accepted cache path. A rejected fragment restores the exact
        // environment owner from the same normal/material inputs.
        vec3 exactEnvironmentIrradiance =
            EvaluateEnvironmentDiffuseIrradiance(
                environment,
                diffuseIndirectNormal);
        diffuseIbl = EvaluateGiDiffuseFromIrradiance(
            exactEnvironmentIrradiance,
            diffuseReflectance);
    }
#endif

#if !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    if (reflectionDebugActive)
    {
        WriteForwardColor(vec4(reflectionDebugColor, forwardDebugOutputAlpha));
        return;
    }
#endif

#if !FORWARD_INCOMPATIBLE_DEBUG_VIEWS_STATIC_NONE
    if (environment.DebugView == ENVIRONMENT_DEBUG_DIFFUSE_IBL_ONLY)
    {
        WriteForwardColor(vec4(diffuseIbl, 1.0));
        return;
    }

    if (environment.DebugView == ENVIRONMENT_DEBUG_SPECULAR_IBL_ONLY)
    {
        WriteForwardColor(vec4(specularIbl, 1.0));
        return;
    }
#endif

    vec3 subsurfaceBackDiffuseIndirect = vec3(0.0);
    if (subsurfaceStrength > 0.0 &&
        environment.Enabled != 0u &&
        any(greaterThan(
            subsurfaceDiffuseReflectance,
            vec3(0.000001))))
    {
        vec3 subsurfaceBackEnvironmentIrradiance =
            EvaluateEnvironmentDiffuseIrradiance(
                environment,
                -normal);
        subsurfaceBackDiffuseIndirect =
            EvaluateGiDiffuseFromIrradiance(
                subsurfaceBackEnvironmentIrradiance,
                subsurfaceDiffuseReflectance) * indirectAo;
    }

    vec3 finalDiffuseIndirect = vec3(0.0);
#if FORWARD_THIN_GLASS_ONLY
    // ThinGlass participates in GI transport as a transmitting surface but
    // exposes no Lambertian raster lobe. Directional DDGI was already consumed
    // by EvaluateIbl as the default reflected-radiance source.
#elif FORWARD_GLOBAL_ILLUMINATION_DISABLED
    // Benchmark control artifact. This is a separate native program so the
    // A/B delta measures only the incremental cache consumer work and does not
    // retain the sparse-gather graph as dead control flow.
    finalDiffuseIndirect = diffuseIbl * indirectAo;
#else
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_ONLY
    if (receiverCacheAccepted)
    {
        // The admitted sidecar proves that this resolved record belongs to
        // the fragment's local receiver surface. The producer already applied
        // intensity, ownership, leak attenuation, fallback visibility and the
        // Lambert factor; material/AO composition remains fragment exact.
        vec3 cachedDdgiDiffuse =
            ForwardDdgiReceiverCacheDdgiIrradiance(cachedGather) *
            ambientOcclusion * ddgiIndirectAo * diffuseReflectance;
        vec3 cachedEnvironmentDiffuse =
            ForwardDdgiReceiverCacheEnvironmentIrradiance(cachedGather) *
            indirectAo * diffuseReflectance;
        if (ForwardAmbientOcclusionBentNormalMode() != 0u &&
            bentNormalValid)
        {
            // EnvironmentOnly and EnvironmentAndDdgi evaluate the authored
            // bent direction at the fragment. The cache continues to own the
            // DDGI estimate (EnvironmentOnly), admission, and rough-specular
            // visibility without reusing normal-dependent sky irradiance.
            cachedEnvironmentDiffuse = diffuseIbl * indirectAo;
        }

        finalDiffuseIndirect = cachedDdgiDiffuse + cachedEnvironmentDiffuse;
#if !FORWARD_DDGI_RECEIVER_CACHE_LEGACY && \
    !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
        if (!NjulfReceiverCacheAcceptedLane() &&
            ForwardAmbientOcclusionBentNormalMode() == 2u && bentNormalValid)
        {
            // Ultra's DDGI lobe is also bent-normal dependent. Re-evaluate
            // only that lobe from the canonical gather while retaining the
            // cache for receiver admission, environment replacement, and
            // rough-specular visibility. This is the safe visible fallback
            // when no compact diffuse-directional record is available.
            SimpleDdgiParams bentParams = ReadSimpleDdgiParams(
                uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
            SimpleDdgiGatherResult bentGather = precomputedSimpleDdgiGather;
            float bentRadiometricOwnership =
                SimpleDdgiRadiometricOwnership(bentGather);
            float bentLeakAttenuation = SimpleDdgiLeakAttenuation(
                bentGather,
                bentParams);
            float bentOwnership =
                bentRadiometricOwnership * bentLeakAttenuation;
            float bentEnvironmentFallback =
                (1.0 - bentRadiometricOwnership) *
                bentParams.environmentFallbackIntensity;
            vec3 bentDdgiDiffuse = ApplyGiMaterialOcclusion(
                EvaluateGiDiffuseFromIrradiance(
                    bentGather.irradiance * bentParams.indirectIntensity,
                    diffuseReflectance),
                ambientOcclusion * ddgiIndirectAo) * bentOwnership;
            vec3 bentEnvironmentDiffuse = diffuseIbl;
            if (bentEnvironmentFallback >
                    SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT &&
                (bentParams.flags &
                    SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
            {
                bentEnvironmentDiffuse *= EstimateFarFieldSkyVisibility(
                    fragWorldPosition,
                    ddgiNormal,
                    bentParams,
                    DdgiSparseDiagnosticSampleWeight());
            }
            finalDiffuseIndirect = bentDdgiDiffuse +
                bentEnvironmentDiffuse * bentEnvironmentFallback * indirectAo;
        }
#endif
    }
#if !FORWARD_DDGI_RECEIVER_CACHE_LEGACY && \
    !FORWARD_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_ONLY && \
    !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
    if (!NjulfReceiverCacheAcceptedLane() && !receiverCacheAccepted)
    {
#endif
#endif
#if !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
    bool globalIlluminationEnabled = geometryDecal
        ? ForwardDecalGlobalIlluminationEnabled()
        : ForwardGlobalIlluminationEnabled() != 0u;
    SimpleDdgiParams simpleDdgiParams = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    bool simpleDdgiConfigured = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;
    bool simpleDdgiActive = simpleDdgiConfigured &&
        (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) != 0u;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
    DdgiSampleResult ddgiSample = EmptyDdgiSampleResult();
    vec3 simpleDdgiContributingVolumeColor = vec3(0.0);
    vec3 simpleDdgiSourceCacheIrradiance = vec3(0.0);
    uint simpleDdgiPrimaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    uint simpleDdgiSecondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    float simpleDdgiSecondVolumeUsed = 0.0;
    float simpleDdgiPrimaryContributionWeight = 0.0;
    float simpleDdgiSecondaryContributionWeight = 0.0;
    uint simpleDdgiCombinedRejectionMask = 0u;
    uint simpleDdgiFirstRejectionReason = SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
    uint simpleDdgiNonResidentProbeCount = 0u;
    uint simpleDdgiResidencyTableFlags = 0u;
    uint simpleDdgiResidencyHistoryFlags = 0u;
    uint simpleDdgiResidencyDemandMask = 0u;
    uint simpleDdgiPhysicalPageIndex = 0xffffffffu;
    uint simpleDdgiPageMappingGeneration = 0u;
    float simpleDdgiPageAgeNormalized = 0.0;
    float fallbackWeight = 0.0;
    float nearContactSuppression = 0.0;
    vec3 hybridDebugDiffuse = vec3(0.0);
    vec3 hybridSuppressionMask = vec3(0.0);
    float hybridEffectiveDdgiWeight = 0.0;
#endif
    vec3 ddgiDiffuse = vec3(0.0);
    vec3 finalDdgiDiffuse = vec3(0.0);
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    uint materialTransportProvenance =
        MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;
#endif

    if (!globalIlluminationEnabled)
    {
        // Preserve inexpensive environment diffuse for transparent materials
        // while avoiding DDGI/legacy probe work that the pass explicitly opted
        // out of.  This also gives feature-isolated rendering a stable fallback.
        finalDiffuseIndirect = diffuseIbl * indirectAo;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        fallbackWeight = 1.0;
        // This view intentionally removes Simple-DDGI ownership/support and
        // environment substitution so it cannot alias FinalIndirect.
        hybridDebugDiffuse = ddgiDiffuse;
        hybridSuppressionMask = vec3(0.0);
#endif
    }
    else if (simpleDdgiActive)
    {
        // A support-aware result distinguishes an unavailable probe field from
        // legitimate zero irradiance.  Fresh, exposed, and invalid slots
        // are excluded before this reaches lighting composition.
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_DDGI_GATHER_COUNTER);
        SimpleDdgiGatherResult simpleGather = EmptySimpleDdgiGatherResult();
        float simpleSupport;
        float simpleDirectionalSupport;
        float simpleRadiometricOwnership;
        float simpleLeakAttenuation;
        vec3 simpleIrradiance;
        // Reflection captures, detailed/provenance artifacts, and any frame
        // where cache creation or dispatch is unavailable bind this exact
        // fallback artifact.
        // Non-cache native programs perform exactly one structured gather per
        // fragment. It feeds both directional specular and diffuse ownership;
        // retaining a second syntactic call site duplicates the optimized
        // residency atomics even when the branches are mutually exclusive.
        simpleGather = precomputedSimpleDdgiGather;
        simpleSupport = clamp(simpleGather.validSupport, 0.0, 1.0);
        simpleDirectionalSupport = clamp(
            simpleGather.directionalSupport,
            0.0,
            1.0);
        simpleRadiometricOwnership =
            SimpleDdgiRadiometricOwnership(simpleGather);
        simpleLeakAttenuation = SimpleDdgiLeakAttenuation(
            simpleGather,
            simpleDdgiParams);
        simpleIrradiance = simpleGather.irradiance;
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
        exactFeedbackGatherContributed = true;
        exactFeedbackRadiometricOwnership = simpleRadiometricOwnership;
        exactFeedbackLeakAttenuation = simpleLeakAttenuation;
        exactFeedbackRoughDdgiOwnership = SimpleDdgiRoughSpecularWeight(
            simpleDdgiParams.residencyFlags,
            roughness);
#endif
        float simpleOwnership = simpleRadiometricOwnership * simpleLeakAttenuation;
        // Leak attenuation represents blocked transport, not missing field
        // coverage, so it must not be refilled with the environment complement.
        float simpleFallback = (1.0 - simpleRadiometricOwnership) * simpleDdgiParams.environmentFallbackIntensity;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        simpleDdgiContributingVolumeColor = simpleGather.contributingVolumeColor;
#if NJULF_DDGI_DETAILED_COUNTERS
        simpleDdgiSourceCacheIrradiance = simpleGather.sourceCacheIrradiance;
#endif
        simpleDdgiPrimaryVolume = simpleGather.selectedVolume;
        simpleDdgiSecondaryVolume = simpleGather.secondaryVolume;
        simpleDdgiSecondVolumeUsed = simpleGather.secondVolumeUsed;
        simpleDdgiPrimaryContributionWeight = simpleGather.primaryContributionWeight;
        simpleDdgiSecondaryContributionWeight = simpleGather.secondaryContributionWeight;
#if NJULF_DDGI_DETAILED_COUNTERS
        simpleDdgiCombinedRejectionMask = simpleGather.combinedRejectionMask;
        simpleDdgiFirstRejectionReason = simpleGather.firstRejectionReason;
#endif
        simpleDdgiNonResidentProbeCount = simpleGather.nonResidentProbeCount;
        ddgiSample.irradiance = simpleIrradiance;
        ddgiSample.coverage = simpleGather.spatialCoverage;
        ddgiSample.spatialCoverage = simpleGather.spatialCoverage;
        ddgiSample.supportCoverage = simpleSupport;
        // Data confidence is availability. Directional support is geometric
        // estimator authority and has its own debug view/chain channel.
        ddgiSample.weight = simpleSupport;
        ddgiSample.ownershipConsumed = simpleOwnership;
        ddgiSample.visibility = simpleGather.transportVisibility;
        ddgiSample.visibilityConfidence = simpleGather.transportVisibility;
        ddgiSample.activeProbe = simpleSupport;
        ddgiSample.cascadeIndex = float(simpleGather.selectedVolume);
        // Keep the geometric edge transition separate from the actual sampled
        // contribution.  Unsupported probes can force a fallback even when the
        // fragment is not in an authored-volume transition band.
        ddgiSample.cascadeBlendWeight = clamp(1.0 - simpleGather.transitionWeight, 0.0, 1.0);
        ddgiSample.minProbeSpacing = simpleGather.selectedSpacing;
        ddgiSample.rayBudget = float(simpleDdgiParams.raysPerProbe) / 256.0;
        ddgiSample.leakClamp = simpleGather.transportVisibility;
        ddgiSample.irradianceAtlasConfidence = simpleSupport;
        ddgiSample.qualityConfidence = simpleDirectionalSupport;
#endif

        // Diagnostic sampling is intentionally opt-in.  It rereads probe state and
        // atlases, so doing it per shaded fragment made normal production frames
        // pay the cost of a second gather.
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
#if NJULF_DDGI_DETAILED_COUNTERS
        float simpleDiagnosticVisibility = simpleGather.transportVisibility;
        float simpleDiagnosticVisibilityMean = 0.0;
        bool sampleSimpleDdgiDebug = IsDdgiDebugView(debugViewMode) ||
            DdgiForwardEstimateDiagnosticPixel();
#else
        bool sampleSimpleDdgiDebug = IsDdgiDebugView(debugViewMode);
#endif
        if (sampleSimpleDdgiDebug)
        {
            SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(
                simpleDdgiParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection);
            ddgiSample.probeIndex = simpleDebug.probeIndex;
            ddgiSample.logicalProbePosition = simpleDebug.logicalProbePosition;
            ddgiSample.relocatedProbePosition = simpleDebug.relocatedProbePosition;
            ddgiSample.relocation = simpleDebug.relocation;
            // Nearest-probe state is diagnostic only; the authoritative
            // receiver estimate above remains the structured eight-corner
            // gather. This makes relocation/state views observable without
            // changing lighting composition or normal-frame buffer traffic.
            ddgiSample.activeProbe = simpleDebug.activeWeight;
            ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;
            ddgiSample.visibilityMomentVariance = simpleDebug.visibilityMomentVariance;
            ddgiSample.visibilityProbeDistance = simpleDebug.visibilityProbeDistance;
            ddgiSample.visibilityMaxRayDistance = simpleDebug.visibilityMaxRayDistance;
#if NJULF_DDGI_DETAILED_COUNTERS
            simpleDiagnosticVisibility = simpleDebug.visibility;
            simpleDiagnosticVisibilityMean = simpleDebug.visibilityMomentMean;
#endif
            simpleDdgiResidencyTableFlags = simpleDebug.residencyTableFlags;
            simpleDdgiResidencyHistoryFlags = simpleDebug.residencyHistoryFlags;
            simpleDdgiResidencyDemandMask = simpleDebug.residencyDemandMask;
            simpleDdgiPhysicalPageIndex = simpleDebug.physicalPageIndex;
            simpleDdgiPageMappingGeneration = simpleDebug.pageMappingGeneration;
            simpleDdgiPageAgeNormalized = simpleDebug.pageAgeNormalized;
        }
#if NJULF_DDGI_DETAILED_COUNTERS
        AccumulateDdgiVisibilityMomentDiagnostics(
            ddgiSample.visibilityMomentMean,
            ddgiSample.visibilityMomentVariance,
            ddgiSample.visibilityProbeDistance,
            ddgiSample.visibilityMaxRayDistance,
            simpleDiagnosticVisibility,
            ddgiSample.irradianceAtlasConfidence);
#endif
#endif

        // Once valid probe data produces a normalized estimate, DDGI owns the
        // spatially covered share. Probe-validity mass selects that estimate but
        // must not premultiply it, or inactive probes next to geometry become a
        // visible dark lattice. Screen-space AO is reserved for the environment
        // fallback because probe visibility already occludes DDGI bounce lighting.
        ddgiDiffuse = ApplyGiMaterialOcclusion(
            EvaluateGiDiffuseFromIrradiance(
                simpleIrradiance * simpleDdgiParams.indirectIntensity,
                diffuseReflectance),
            ambientOcclusion * ddgiIndirectAo);
        finalDdgiDiffuse = ddgiDiffuse * simpleOwnership;
        vec3 simpleEnvironmentFallback = diffuseIbl;
        bool evaluateFarFieldFallback =
            simpleFallback > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT ||
            (simpleDdgiParams.flags &
                SIMPLE_DDGI_FLAG_FORCE_LEGACY_FAR_FIELD_FALLBACK) != 0u;
        if (evaluateFarFieldFallback &&
            (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
        {
            simpleEnvironmentFallback *= EstimateFarFieldSkyVisibility(
                fragWorldPosition,
                ddgiNormal,
                simpleDdgiParams,
                DdgiSparseDiagnosticSampleWeight());
        }
        finalDiffuseIndirect = finalDdgiDiffuse + simpleEnvironmentFallback * simpleFallback * indirectAo;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        fallbackWeight = simpleFallback;
        nearContactSuppression = 1.0 - simpleLeakAttenuation;
        hybridDebugDiffuse = finalDiffuseIndirect;
        hybridSuppressionMask = vec3(simpleSupport, simpleLeakAttenuation, simpleDirectionalSupport);
        hybridEffectiveDdgiWeight = simpleOwnership;
#endif
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
        materialTransportProvenance =
            ResolveSimpleDdgiMaterialTransportProvenance(
                simpleGather,
                simpleDdgiParams);
#endif

#if NJULF_DDGI_DETAILED_COUNTERS
        vec3 diagnosticFinalDiffuseIndirect =
            ApplyGiSubsurfaceDiffuseSplit(
                finalDiffuseIndirect,
                subsurfaceBackDiffuseIndirect,
                subsurfaceStrength);
        HybridDiffuseGiResult simpleHybridDiagnostics;
        simpleHybridDiagnostics.diffuse = diagnosticFinalDiffuseIndirect;
        simpleHybridDiagnostics.ddgiCoverage = simpleGather.spatialCoverage;
        simpleHybridDiagnostics.environmentFallbackWeight = simpleFallback;
        simpleHybridDiagnostics.nearContactSuppression = 1.0 - simpleLeakAttenuation;
        simpleHybridDiagnostics.effectiveDdgiWeight = simpleOwnership;
        simpleHybridDiagnostics.suppressionMask = hybridSuppressionMask;
        AccumulateDdgiForwardEstimateDiagnostics(
            simpleHybridDiagnostics,
            ddgiSample,
            ddgiDiffuse,
            diffuseReflectance,
            geometryDecal);
        AccumulateDdgiInvestigationForwardDiagnostics(
            true,
            simpleDdgiParams,
            fragWorldPosition,
            ddgiNormal,
            viewDirection,
            simpleIrradiance,
            simpleDiagnosticVisibility,
            simpleDiagnosticVisibilityMean,
            finalDdgiDiffuse,
            diffuseIbl,
            diagnosticFinalDiffuseIndirect);
#endif
    }
    else
    {
        // Simple DDGI is the only dynamic-GI backend. During startup, recovery,
        // or unsupported ray-query frames, use its configured environment
        // fallback instead of sampling a second probe implementation.
        float simpleDisabledFallbackWeight = simpleDdgiConfigured
            ? simpleDdgiParams.environmentFallbackIntensity
            : 1.0;
        finalDiffuseIndirect = diffuseIbl * simpleDisabledFallbackWeight * indirectAo;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        fallbackWeight = simpleDisabledFallbackWeight;
        hybridDebugDiffuse = finalDiffuseIndirect;
        hybridSuppressionMask = vec3(0.0);
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
        vec3 diagnosticFinalDiffuseIndirect =
            ApplyGiSubsurfaceDiffuseSplit(
                finalDiffuseIndirect,
                subsurfaceBackDiffuseIndirect,
                subsurfaceStrength);
        HybridDiffuseGiResult simpleFallbackDiagnostics;
        simpleFallbackDiagnostics.diffuse = diagnosticFinalDiffuseIndirect;
        simpleFallbackDiagnostics.ddgiCoverage = 0.0;
        simpleFallbackDiagnostics.environmentFallbackWeight = fallbackWeight;
        simpleFallbackDiagnostics.nearContactSuppression = 0.0;
        simpleFallbackDiagnostics.effectiveDdgiWeight = 0.0;
        simpleFallbackDiagnostics.suppressionMask = vec3(0.0);
        AccumulateDdgiForwardEstimateDiagnostics(
            simpleFallbackDiagnostics,
            ddgiSample,
            vec3(0.0),
            diffuseReflectance,
            geometryDecal);
        if (simpleDdgiConfigured)
        {
            AccumulateDdgiInvestigationForwardDiagnostics(
                true,
                simpleDdgiParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection,
                vec3(0.0),
                0.0,
                0.0,
                vec3(0.0),
                diffuseIbl,
                diagnosticFinalDiffuseIndirect);
        }
#endif
    }

#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    WriteMaterialTransportProvenance(materialTransportProvenance);
#endif
#endif
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_LEGACY && \
    !FORWARD_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_ONLY && \
    !FORWARD_DDGI_RECEIVER_CACHE_ACCEPTED_ONLY
    }
#endif
#endif // FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE

    if (subsurfaceStrength > 0.0)
    {
        finalDiffuseIndirect = ApplyGiSubsurfaceDiffuseSplit(
            finalDiffuseIndirect,
            subsurfaceBackDiffuseIndirect,
            subsurfaceStrength);
    }

#if !FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_INCOMPATIBLE_DEBUG_VIEWS_STATIC_NONE
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)
    {
        WriteForwardColor(vec4(finalDiffuseIndirect, forwardDebugOutputAlpha));
        return;
    }

    vec2 giDebugUv = clamp(ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0)), vec2(0.0), vec2(1.0));
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_OCCUPANCY_SLICE)
    {
        FarFieldClipmapParams farField = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
        uint packed;
        bool missing;
        ReadFarFieldDebugVoxel(farField, giDebugUv, packed, missing);
        vec3 rgb = vec3(
            float((packed >> 0u) & 0xffu),
            float((packed >> 8u) & 0xffu),
            float((packed >> 16u) & 0xffu)) / 255.0;
        float occupied = (packed & 0x80000000u) != 0u ? 1.0 : 0.0;
        vec3 base = missing ? vec3(0.08, 0.015, 0.12) : vec3(0.02);
        WriteForwardColor(vec4(mix(base, rgb, occupied), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_TRACE_RESULT)
    {
        vec3 traceDir = normalize(fragWorldPosition - pc.Push.CameraPosition);
        float hitT;
        vec3 farNormal;
        vec3 farAlbedo;
        bool hitFar = TraceFarFieldClipmap(pc.Push.CameraPosition, traceDir, 0.0, 512.0, hitT, farNormal, farAlbedo);
        vec3 traceColor = hitFar ? farAlbedo * (abs(farNormal) * 0.35 + vec3(0.65)) : vec3(0.0, 0.02, 0.05);
        WriteForwardColor(vec4(traceColor, 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY)
    {
        float visibility = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u
            ? EstimateFarFieldSkyVisibility(
                fragWorldPosition,
                geometricNormal,
                simpleDdgiParams,
                DdgiSparseDiagnosticSampleWeight())
            : 1.0;
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY, vec3(visibility));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW)
    {
        float farShadow = 1.0;
        int shadowLightIndex = int(round(ReadShadowIndices().w));
        if (shadowLightIndex >= 0 &&
            shadowLightIndex < int(ForwardTotalLightCount(pc.Push)))
        {
            uint lightIndex = uint(shadowLightIndex);
            GPULight light = ReadLight(lightIndex);
            farShadow = EstimateFarFieldSunShadow(fragWorldPosition, normal, normalize(-light.Direction));
        }
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW, vec3(farShadow));
        return;
    }

#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE)
    {
        // A logarithmic presentation keeps exact zero black while retaining
        // headroom for the sun-lit tail. Eight linear units map to white; one
        // unit remains mid-bright instead of saturating the whole scene. The
        // raw linear value remains available through DdgiSampledIrradiance.
        vec3 safeIrradiance = max(ddgiSample.irradiance, vec3(0.0));
        float irradianceLuminance = DdgiDiagnosticLuminance(safeIrradiance);
        vec3 presentedIrradiance = vec3(0.0);
        if (irradianceLuminance > 0.00000001)
        {
            const float logScale = 64.0;
            const float referenceWhite = 8.0;
            float presentedLuminance = log2(1.0 + irradianceLuminance * logScale) /
                log2(1.0 + referenceWhite * logScale);
            presentedIrradiance = clamp(
                safeIrradiance * (presentedLuminance / irradianceLuminance),
                vec3(0.0),
                vec3(1.0));
        }
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE,
            presentedIrradiance);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE)
    {
        // Use the same normalized logarithmic presentation as DdgiIrradiance.
        // The hue remains radiometric, so a green direct/emissive source is
        // immediately distinguishable from a grey transport result.
        vec3 safeSource = max(simpleDdgiSourceCacheIrradiance, vec3(0.0));
        float sourceLuminance = DdgiDiagnosticLuminance(safeSource);
        vec3 presentedSource = vec3(0.0);
        if (sourceLuminance > 0.00000001)
        {
            const float logScale = 64.0;
            const float referenceWhite = 8.0;
            float presentedLuminance = log2(1.0 + sourceLuminance * logScale) /
                log2(1.0 + referenceWhite * logScale);
            presentedSource = clamp(
                safeSource * (presentedLuminance / sourceLuminance),
                vec3(0.0),
                vec3(1.0));
        }
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE,
            presentedSource);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY)
    {
        bool suppressed =
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY) != 0u ||
            (simpleDdgiResidencyHistoryFlags &
                SIMPLE_DDGI_PAGE_HISTORY_SUPPRESSED) != 0u;
        bool resident = simpleDdgiPhysicalPageIndex != 0xffffffffu &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_VALID) != 0u;
        bool published = resident &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_PUBLISHED) != 0u &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_INITIALIZING) == 0u;
        bool demandedMissing = simpleDdgiResidencyDemandMask != 0u &&
            !published;
        vec3 residencyColor = suppressed
            ? vec3(0.85, 0.10, 0.85)
            : (demandedMissing
                ? vec3(1.0, 0.08, 0.02)
                : (!resident
                    ? vec3(0.18)
                    : (!published
                        ? vec3(1.0, 0.72, 0.05)
                        : (simpleDdgiResidencyDemandMask == 0u &&
                           simpleDdgiPageAgeNormalized > 0.0
                            ? vec3(0.05, 0.45, 1.0)
                            : vec3(0.05, 0.95, 0.20)))));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY,
            residencyColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK)
    {
        float missingShare = clamp(
            float(simpleDdgiNonResidentProbeCount) /
                float(SIMPLE_DDGI_PROBES_PER_PAGE),
            0.0,
            1.0);
        bool suppliedByCoarser = missingShare > 0.0 &&
            simpleDdgiSecondVolumeUsed > 0.5 &&
            simpleDdgiSecondaryContributionWeight > 0.000001;
        vec3 supplierColor = suppliedByCoarser
            ? MeshletDebugColor(simpleDdgiSecondaryVolume + 1u)
            : vec3(0.0);
        vec3 residencyFallbackColor = missingShare <= 0.0
            ? vec3(0.04, 0.65, 0.10)
            : (suppliedByCoarser
                ? mix(supplierColor, vec3(1.0, 0.05, 0.02),
                    0.35 + 0.35 * missingShare)
                : vec3(1.0, 0.0, 0.0));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK,
            residencyFallbackColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE)
    {
        bool resident = simpleDdgiPhysicalPageIndex != 0xffffffffu &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_VALID) != 0u;
        bool suppressed =
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY) != 0u ||
            (simpleDdgiResidencyHistoryFlags &
                SIMPLE_DDGI_PAGE_HISTORY_SUPPRESSED) != 0u;
        vec3 ageColor = !resident
            ? (suppressed ? vec3(0.85, 0.10, 0.85) : vec3(0.08))
            : mix(
                vec3(0.0, 0.85, 1.0),
                vec3(1.0, 0.08, 0.0),
                simpleDdgiPageAgeNormalized);
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE,
            ageColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE)
    {
        vec3 pageColor = simpleDdgiPhysicalPageIndex == 0xffffffffu
            ? vec3(0.12)
            : MeshletDebugColor(
                simpleDdgiPhysicalPageIndex ^
                (simpleDdgiPageMappingGeneration * 0x9e3779b9u));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE,
            pageColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE, clamp(ddgiDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE, clamp(ddgiSample.irradiance, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE, clamp(finalDdgiDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS, clamp(hybridDebugDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK, clamp(hybridSuppressionMask, vec3(0.0), vec3(1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT, vec3(clamp(hybridEffectiveDdgiWeight, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE, vec3(clamp(ddgiSample.spatialCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE, vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE)
    {
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE,
            vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT)
    {
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT,
            vec3(clamp(ddgiSample.qualityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE, vec3(clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN, vec3(
            clamp(ddgiSample.supportCoverage, 0.0, 1.0),
            clamp(ddgiSample.qualityConfidence, 0.0, 1.0),
            clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT, vec3(clamp(fallbackWeight / 4.0, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY, vec3(ddgiSample.visibility));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS)
    {
        float visibilityMaxDistance = max(ddgiSample.visibilityMaxRayDistance, 0.0001);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS, vec3(
            clamp(ddgiSample.visibilityMomentMean / visibilityMaxDistance, 0.0, 1.0),
            clamp(sqrt(max(ddgiSample.visibilityMomentVariance, 0.0)) / visibilityMaxDistance, 0.0, 1.0),
            clamp(ddgiSample.visibilityProbeDistance / visibilityMaxDistance, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX, MeshletDebugColor(ddgiSample.probeIndex));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE)
    {
        if (simpleDdgiActive && globalIlluminationEnabled &&
            simpleDdgiCombinedRejectionMask != 0u &&
            ddgiSample.supportCoverage <= 0.000001)
        {
            // R = first failing reason, G/B = low/high portions of the combined
            // nine-bit mask. A structured gather normally rejects some of its
            // corner candidates while still producing a valid estimate; showing
            // those routine rejections made healthy receivers look failed. Only
            // expose the rejection payload when the gather has no usable support.
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE,
                vec3(
                    float(min(simpleDdgiFirstRejectionReason, SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)) /
                        float(SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT),
                    float(simpleDdgiCombinedRejectionMask & 0xffu) / 255.0,
                    float((simpleDdgiCombinedRejectionMask >> 8u) & 0x1u)));
        }
        else
        {
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE,
                vec3(ddgiSample.activeProbe, ddgiSample.supportCoverage, ddgiSample.weight));
        }
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION, abs(ddgiSample.relocation));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED)
    {
        float relocationAmount = length(ddgiSample.relocation) / max(ddgiSample.minProbeSpacing * 0.4, 0.001);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED, vec3(clamp(relocationAmount, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION, fract(abs(ddgiSample.logicalProbePosition) * 0.05));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION, fract(abs(ddgiSample.relocatedProbePosition) * 0.05));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION)
    {
        float relocationLength = length(ddgiSample.relocation);
        vec3 relocationDirection = relocationLength > 0.000001
            ? normalize(ddgiSample.relocation) * 0.5 + vec3(0.5)
            : vec3(0.5);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION, relocationDirection);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE, vec3(clamp(ddgiSample.classificationInvalidScore, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP, vec3(clamp(ddgiSample.leakClamp * (1.0 - nearContactSuppression), 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE, vec3(clamp(ddgiSample.spatialCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION)
    {
        vec3 cascadeContributorColor = simpleDdgiActive && globalIlluminationEnabled
            ? simpleDdgiContributingVolumeColor
            : MeshletDebugColor(uint(max(ddgiSample.cascadeIndex, 0.0)) + 1u);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION, cascadeContributorColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT, vec3(clamp(ddgiSample.cascadeBlendWeight, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS)
    {
        uint updateReason = uint(clamp(ddgiSample.updateReason * 255.0, 0.0, 255.0));
        vec3 updateReasonColor = updateReason != 0u
            ? MeshletDebugColor(updateReason)
            : vec3(0.0);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS, updateReasonColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET, vec3(ddgiSample.rayBudget, ddgiSample.supportCoverage, ddgiSample.weight));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT)
    {
        float blendWeight = simpleDdgiActive && globalIlluminationEnabled
            ? clamp(simpleDdgiSecondaryContributionWeight, 0.0, 1.0)
            : 0.0;
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT,
            vec3(blendWeight));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK)
    {
        if (!(simpleDdgiActive && globalIlluminationEnabled))
        {
            WriteDdgiDebugColor(debugViewMode, vec3(0.0));
            return;
        }

        bool primaryValid = simpleDdgiPrimaryContributionWeight > 0.000001 &&
            simpleDdgiPrimaryVolume < simpleDdgiParams.volumeCount;
        bool secondaryValid = simpleDdgiSecondaryContributionWeight > 0.000001 &&
            simpleDdgiSecondVolumeUsed > 0.5 &&
            simpleDdgiSecondaryVolume < simpleDdgiParams.volumeCount;
        SimpleDdgiVolume primaryVolume = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            primaryValid ? simpleDdgiPrimaryVolume : 0u);
        SimpleDdgiVolume secondaryVolume = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            secondaryValid ? simpleDdgiSecondaryVolume : 0u);

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME)
        {
            bool primaryAuthored = primaryValid && primaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED;
            bool secondaryAuthored = secondaryValid && secondaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED;
            uint authoredIndex = primaryAuthored
                ? simpleDdgiPrimaryVolume
                : (secondaryAuthored ? simpleDdgiSecondaryVolume : SIMPLE_DDGI_INVALID_VOLUME_INDEX);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME,
                authoredIndex != SIMPLE_DDGI_INVALID_VOLUME_INDEX
                    ? MeshletDebugColor(authoredIndex + 1u)
                    : vec3(0.0));
            return;
        }

        bool primaryRing = primaryValid && primaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_RING;
        bool secondaryRing = secondaryValid && secondaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_RING;
        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP)
        {
            uint ringIndex = primaryRing
                ? simpleDdgiPrimaryVolume
                : (secondaryRing ? simpleDdgiSecondaryVolume : SIMPLE_DDGI_INVALID_VOLUME_INDEX);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP,
                ringIndex != SIMPLE_DDGI_INVALID_VOLUME_INDEX
                    ? MeshletDebugColor(ringIndex + 1u)
                    : vec3(0.0));
            return;
        }

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT)
        {
            float ringWeight =
                (primaryRing ? simpleDdgiPrimaryContributionWeight : 0.0) +
                (secondaryRing ? simpleDdgiSecondaryContributionWeight : 0.0);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT,
                vec3(clamp(ringWeight, 0.0, 1.0)));
            return;
        }

        float fallback = clamp(simpleDdgiSecondVolumeUsed, 0.0, 1.0);
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK,
            vec3(fallback, 1.0 - fallback, 0.0));
        return;
    }
#endif
#endif // GI debug views are dynamically available for this artifact

#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
    // This must remain independent of final scene colour and every indirect
    // owner. AccumulateLight has already applied the exact shadow factor to
    // directDiffuseSource, while emissive follows the frozen material
    // photometric convention.
    C5WriteDirectDiffuseAndEmissiveSource(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        directDiffuseSource,
        emissive);
#endif

#if NJULF_C4_RECEIVER_OUTPUT
    // C4 stores incident photon flux independently. This MRT publishes only
    // current receiver normals and BRDF parameters; the resolve applies them
    // once for each photon direction and the composite adds separate C4
    // radiance exactly once.
    C4CreateReceiverPayload(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        outGiCausticReceiverPayload);
#endif

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    // The deferred reflection pass owns only base indirect specular. Direct
    // light, diffuse GI, emissive, and material extensions remain in SceneColor.
    float layerNdotV = max(dot(normal, viewDirection), 0.0);
    float clearcoatViewFresnel = clearcoatFactor *
        FresnelSchlick(layerNdotV, vec3(0.04)).x;
    float sheenViewEnergy = MaxComponent(clamp(
        sheenColor,
        vec3(0.0),
        vec3(1.0))) * SheenDirectionalAlbedo(
            layerNdotV,
            sheenRoughness);
    float baseLayerSpecularScale = clamp(
        (1.0 - clearcoatViewFresnel) *
        (1.0 - sheenViewEnergy),
        0.0,
        1.0);
    float hybridSpecularOcclusion = clamp(
        pow(indirectAo, 1.0 + roughness) * indirectSpecularVisibility,
        0.0,
        1.0) * baseLayerSpecularScale;
    uint hybridReflectionLobeFlags = 0u;
    if (transmissionFactor >= 0.05)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_TRANSMISSIVE;
    }
    if (anisotropyStrength >= 0.35 &&
        reflectionSchedulingRoughness >= 0.20)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_BROAD_ANISOTROPIC;
    }
    if (clearcoatFactor > 0.0)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_CLEARCOAT;
    }
    vec3 hybridTangent = fragWorldTangent.xyz - normal *
        dot(fragWorldTangent.xyz, normal);
    if (dot(hybridTangent, hybridTangent) <= 1.0e-12)
        hybridTangent = NjulfHybridReflectionCanonicalTangentBasisX(normal);
    else
        hybridTangent = normalize(hybridTangent);
    vec3 hybridBitangent = normalize(cross(normal, hybridTangent) *
        (fragWorldTangent.w < 0.0 ? -1.0 : 1.0));
    float hybridAnisotropyRotation = hasMaterialExtension
        ? materialExtension.Anisotropy.y
        : 0.0;
    hybridTangent = normalize(
        hybridTangent * cos(hybridAnisotropyRotation) +
        hybridBitangent * sin(hybridAnisotropyRotation));
    bool hybridReflectionPayloadValid =
        NjulfHybridReflectionCreatePayload(
        geometricNormal,
        normal,
        mix(dielectricF0, albedo, metallic),
        roughness,
        reflectionSchedulingRoughness,
        hybridSpecularOcclusion,
        hybridReflectionLobeFlags,
        // Meshlet IDs are rasterization details and change across otherwise
        // continuous surfaces. History identity must remain stable across them.
        uvec3(
            fragObjectIndex,
            fragMaterialIndex,
            material.MaterialRevision),
        outHybridReflectionReceiverPayload);
    uvec2 hybridReflectionLobeExtension =
        NjulfHybridReflectionCreateLobeExtension(
            clearcoatNormal,
            clearcoatFactor,
            clearcoatRoughness,
            anisotropyStrength,
            normal,
            hybridTangent);
#if NJULF_HYBRID_REFLECTION_SPARSE_LOBE_OUTPUT
    // Clearcoat and broad anisotropy are explicitly flagged. Preserve exact
    // sub-threshold anisotropy as well: it still changes the base-lobe sample
    // even though it intentionally does not change scheduling priority.
    bool hybridLobeExtensionRequired =
        (hybridReflectionLobeFlags &
            (NJULF_HYBRID_REFLECTION_LOBE_ANISOTROPIC |
             NJULF_HYBRID_REFLECTION_LOBE_CLEARCOAT)) != 0u ||
        NjulfHybridReflectionPackUnorm8(anisotropyStrength) != 0u;
    if (hybridReflectionPayloadValid && hybridLobeExtensionRequired)
    {
        uvec2 hybridExtent = uvec2(max(
            floor(pc.Push.ScreenDimensions), vec2(1.0)));
        uvec2 hybridPixel = min(
            uvec2(max(floor(gl_FragCoord.xy), vec2(0.0))),
            hybridExtent - uvec2(1u));
        NjulfHybridSparseLobeStore(
            pc.Push.CurrentFrameIndex,
            hybridExtent,
            hybridPixel,
            hybridReflectionLobeExtension);
    }
#else
    outHybridReflectionLobeExtension = hybridReflectionLobeExtension;
#endif
#endif

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    vec3 color = finalDiffuseIndirect + directLighting + emissive;
#else
    float layerNdotV = max(dot(normal, viewDirection), 0.0);
    float clearcoatViewFresnel = clearcoatFactor *
        FresnelSchlick(layerNdotV, vec3(0.04)).x;
    float sheenViewEnergy = MaxComponent(clamp(
        sheenColor,
        vec3(0.0),
        vec3(1.0))) * SheenDirectionalAlbedo(
            layerNdotV,
            sheenRoughness);
    float baseLayerSpecularScale = clamp(
        (1.0 - clearcoatViewFresnel) *
        (1.0 - sheenViewEnergy),
        0.0,
        1.0);
    vec3 color = finalDiffuseIndirect +
        specularIbl * baseLayerSpecularScale +
        directLighting + emissive;
#endif

    opticalReflectionWeight *= baseLayerSpecularScale;
    if (hasMaterialExtension)
    {
        float nDotV = max(dot(normal, viewDirection), 0.0);
        GPUEnvironmentData extensionEnvironment = environment;
#if !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
        if (clearcoatFactor > 0.0 && extensionEnvironment.Enabled != 0u)
        {
            float clearcoatNdotV = max(
                dot(clearcoatNormal, viewDirection),
                0.0);
            vec3 clearcoatReflection = reflect(
                -viewDirection,
                clearcoatNormal);
            float clearcoatMaxLod = max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 clearcoatPrefiltered = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                clearcoatReflection,
                clearcoatRoughness * clearcoatMaxLod);
            vec3 clearcoatFresnel = FresnelSchlickRoughness(
                clearcoatNdotV,
                vec3(0.04),
                clearcoatRoughness);
            vec2 clearcoatBrdf = texture(
                BindlessTextures[nonuniformEXT(
                    extensionEnvironment.BrdfLutTextureIndex)],
                vec2(clearcoatNdotV, clearcoatRoughness)).rg;
            color += clearcoatPrefiltered *
                (clearcoatFresnel * clearcoatBrdf.x +
                    clearcoatBrdf.y) *
                clearcoatFactor *
                extensionEnvironment.SpecularIntensity * indirectAo;
        }
#endif

        if (MaxComponent(sheenColor) > 0.0 &&
            extensionEnvironment.Enabled != 0u)
        {
            vec3 sheenReflection = reflect(-viewDirection, normal);
            float sheenMaxLod = max(
                float(extensionEnvironment.PrefilteredMipCount) - 1.0,
                0.0);
            vec3 sheenRadiance = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                sheenReflection,
                max(sheenRoughness, 0.07) * sheenMaxLod);
            color += sheenRadiance * sheenColor *
                SheenDirectionalAlbedo(nDotV, sheenRoughness) *
                extensionEnvironment.SpecularIntensity * indirectAo;
        }

        if (iridescenceFactor > 0.0 && metallic < 0.5)
        {
            float nDotVFilm = clamp(dot(normal, viewDirection), 0.0, 1.0);
            float phase = iridescenceThickness * 0.018 + (1.0 - nDotVFilm) * 6.2831853;
            vec3 filmTint = 0.5 + 0.5 * cos(vec3(phase, phase + 2.0943951, phase + 4.1887902));
            float filmFresnel = pow(1.0 - nDotVFilm, 3.0);
            color += filmTint * filmFresnel * iridescenceFactor * specularFactor * indirectAo;
        }

        if (transmissionFactor > 0.0)
        {
            if (thinGlass)
            {
                // A zero-thickness sheet exits parallel to the incident ray.
                // Let fixed-function source-over blending retain the already
                // rendered opaque scene behind the window, while this fragment
                // contributes only its Fresnel reflection/lighting. Dividing by
                // opacity converts that reflected radiance to the pipeline's
                // non-premultiplied blend convention instead of attenuating it
                // a second time.
                float glassNdotV = clamp(
                    abs(dot(normalize(normal), viewDirection)),
                    0.0,
                    1.0);
                float glassF0Ratio = (ior - 1.0) / max(ior + 1.0, 0.0001);
                float glassF0 = glassF0Ratio * glassF0Ratio;
                float glassFresnel = specularFactor * (glassF0 +
                    (1.0 - glassF0) * pow(1.0 - glassNdotV, 5.0));
                float tintTransmission = dot(
                    thinTransmissionTint,
                    vec3(0.2126, 0.7152, 0.0722));
                float glassOpacity = clamp(
                    1.0 - transmissionFactor *
                        tintTransmission * (1.0 - glassFresnel),
                    0.08,
                    1.0);
                // Zero specular explicitly disables sheet reflections,
                // including the grazing-angle Fresnel tail in the universal path.
                color = specularFactor > 0.0
                    ? max(color, vec3(0.0)) / glassOpacity
                    : vec3(0.0);
                opticalReflectionWeight *= specularFactor > 0.0 ? 1.0 / glassOpacity : 0.0;
                outputAlpha = min(outputAlpha, glassOpacity);
            }
            else if (extensionEnvironment.Enabled != 0u)
            {
            vec3 incidentDirection = normalize(-viewDirection);
            vec3 orientedNormal = normalize(normal);
            vec3 transmitted = vec3(0.0);
            bool resolvedPhysicalPath = false;
            bool opticalPhysicalRequested = volumeGiTransport && ForwardThickTransmissionRayQueryEnabled();
            opticalTransmissionObserved = opticalPhysicalRequested ? 0.0 : 1.0;
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY && \
    !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
            if (opticalPhysicalRequested && OpticalDeferredShading())
            {
                opticalTraceFlags |= 2u;
                opticalScatterNormal = ForwardInitialWaterScatterNormal(material, materialExtension, orientedNormal);
            }
            if (volumeGiTransport &&
                ForwardThickTransmissionRayQueryEnabled() &&
                !OpticalDeferredShading() &&
                ForwardTryReserveThickTransmissionTask())
            {
                GPUObjectData objectData = ReadInstanceData(
                    pc.Push.CurrentFrameIndex,
                    fragObjectIndex);
                uint stableObjectIdentity =
                    objectData.NearFieldStableObjectId;
                vec3 scatterNormal =
                    ForwardInitialWaterScatterNormal(
                        material,
                        materialExtension,
                        orientedNormal);
                opticalScatterNormal = scatterNormal;
                uint randomSeed = ForwardThickTransmissionSeed(
                    stableObjectIdentity,
                    material.MaterialRevision);
                ThickTransmissionPathResult centralPath;
                vec3 centralRadiance;
                resolvedPhysicalPath =
                    ForwardTraceThickTransmissionChannel(
                        material,
                        materialExtension,
                        stableObjectIdentity,
                        incidentDirection,
                        scatterNormal,
                        roughness,
                        randomSeed,
                        THICK_TRANSMISSION_SPECTRAL_CENTRAL,
                        extensionEnvironment,
                        centralRadiance,
                        centralPath);
                transmitted = centralRadiance;
                opticalTransmissionObserved = resolvedPhysicalPath ? 1.0 : 0.0;
                opticalTransmissionDistance = resolvedPhysicalPath ? centralPath.PathLength : 0.0;
                if (resolvedPhysicalPath &&
                    ForwardThickTransmissionDispersionEnabled() &&
                    dispersion > 0.0)
                {
                    ThickTransmissionPathResult redPath;
                    ThickTransmissionPathResult bluePath;
                    vec3 redRadiance;
                    vec3 blueRadiance;
                    bool redValid = ForwardTraceThickTransmissionChannel(
                        material, materialExtension, stableObjectIdentity,
                        incidentDirection, scatterNormal, roughness,
                        randomSeed, THICK_TRANSMISSION_SPECTRAL_RED,
                        extensionEnvironment, redRadiance, redPath);
                    bool blueValid = ForwardTraceThickTransmissionChannel(
                        material, materialExtension, stableObjectIdentity,
                        incidentDirection, scatterNormal, roughness,
                        randomSeed, THICK_TRANSMISSION_SPECTRAL_BLUE,
                        extensionEnvironment, blueRadiance, bluePath);
                    opticalTransmissionObserved = redValid && blueValid ? 1.0 : 0.0;
                    // The central IOR is exactly the green-channel IOR in the
                    // Khronos RGB approximation, so the already-traced central
                    // path is the deterministic green sample.
                    if (redValid && blueValid)
                    {
                        transmitted = vec3(
                            redRadiance.r,
                            centralRadiance.g,
                            blueRadiance.b);
                    }
                }
            }
#endif
            if (!resolvedPhysicalPath)
            {
                float centralReflectance;
                vec3 transmittedDirection;
                bool refracted = DielectricTryRefract(
                    incidentDirection,
                    orientedNormal,
                    1.0,
                    ior,
                    transmittedDirection,
                    centralReflectance);
                if (!refracted)
                    transmittedDirection = normalize(reflect(
                        incidentDirection, orientedNormal));
                float lod = roughness * max(
                    float(extensionEnvironment.PrefilteredMipCount) - 1.0,
                    0.0);
                vec3 transmittedSample =
                    SampleEnvironmentPrefilteredRadiance(
                        extensionEnvironment,
                        transmittedDirection,
                        lod);
                if (ForwardThickTransmissionDispersionEnabled() &&
                    dispersion > 0.0)
                {
                    vec3 rgbIors = DielectricRgbIors(ior, dispersion);
                    vec3 redDirection;
                    vec3 blueDirection;
                    float ignoredReflectance;
                    bool redRefracted = DielectricTryRefract(
                        incidentDirection, orientedNormal, 1.0,
                        rgbIors.r, redDirection, ignoredReflectance);
                    bool blueRefracted = DielectricTryRefract(
                        incidentDirection, orientedNormal, 1.0,
                        rgbIors.b, blueDirection, ignoredReflectance);
                    if (redRefracted)
                    {
                        transmittedSample.r =
                            SampleEnvironmentPrefilteredRadiance(
                                extensionEnvironment,
                                redDirection,
                                lod).r;
                    }
                    if (blueRefracted)
                    {
                        transmittedSample.b =
                            SampleEnvironmentPrefilteredRadiance(
                                extensionEnvironment,
                                blueDirection,
                                lod).b;
                    }
                }
                transmitted = transmittedSample;
                if (attenuationDistance > 0.0 &&
                    transmissionThickness > 0.0)
                {
                    transmitted *= DielectricBeerLambert(
                        DielectricAbsorptionCoefficient(
                            attenuationColor,
                            attenuationDistance),
                        transmissionThickness);
                }
            }

            opticalTransmission = transmitted;
            opticalTransmissionWeight = albedo * transmissionFactor;
            opticalTransmissionSource = opticalPhysicalRequested ? 5u : 1u;
            opticalReflectionWeight *= 1.0 - transmissionFactor;
            transmitted *= albedo;
            color = mix(color, transmitted, transmissionFactor);
            outputAlpha = min(outputAlpha, mix(1.0, 0.35, transmissionFactor));
            }
        }
    }

    float finalOutputAlpha =
        alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha;
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#if defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE)
    EmitSimpleDdgiAlphaMaskReceiverFeedback(
        precomputedSimpleDdgiGather,
        exactFeedbackGatherContributed,
        exactFeedbackRadiometricOwnership,
        exactFeedbackLeakAttenuation,
        materialCoverage.Alpha,
        alphaMode > 0.5 && alphaMode < 1.5 &&
            !exactFeedbackMaskedHandledByCompaction,
        exactFeedbackRoughDdgiOwnership);
#else
    EmitSimpleDdgiTransparentReceiverFeedback(
        precomputedSimpleDdgiGather,
        exactFeedbackGatherContributed,
        exactFeedbackRadiometricOwnership,
        exactFeedbackLeakAttenuation,
        finalOutputAlpha);
#endif
#endif
#if OPTICAL_FORWARD_ACTIVE
    if (geometryDecal) { opticalExported = true; OpticalInvalidatePixel(); }
    else OpticalExport(material, geometricNormal, normal, roughness, finalOutputAlpha);
#endif
    WriteForwardColor(vec4(color, finalOutputAlpha));
}
#endif

#ifdef NJULF_VISIBILITY_COMPUTE
#undef main
#undef discard
#include "opaque_shade.glsl"
#endif
