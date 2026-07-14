#ifndef NJULF_DDGI_HIT_SHADING_GLSL
#define NJULF_DDGI_HIT_SHADING_GLSL

#ifndef DDGI_HIT_USE_SELECTED_LIGHTS
#define DDGI_HIT_USE_SELECTED_LIGHTS 1
#endif

#ifndef DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER
#define DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER 1
#endif

#ifndef DDGI_HIT_DIRECT_LIGHT_CAP
#define DDGI_HIT_DIRECT_LIGHT_CAP pc.MaxShadedLights
#endif

// The Simple DDGI update queue carries a quality profile per probe update.  The
// hit shader is also shared by other GI paths, so leave the push-constant
// defaults intact unless the caller explicitly supplies per-update values.
#ifndef DDGI_HIT_MAX_SHADED_LIGHTS
#define DDGI_HIT_MAX_SHADED_LIGHTS pc.MaxShadedLights
#endif

#ifndef DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE
#define DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE pc.MaterialTextureMaxCascade
#endif

vec4 SampleDdgiMaterialTexture(int textureIndex, vec2 uv, float lod, vec4 fallback)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    if (!valid)
        return fallback;

    return textureLod(BindlessTextures[nonuniformEXT(textureIndex)], uv, lod);
}

bool ShouldSampleDdgiMaterialTextures(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return true;

    return DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE < DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE &&
        volumeCascadeIndex <= DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE;
}

bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)
{
    return !ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
}

float DdgiMaterialTextureLod(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return 0.0;

    return min(float(volumeCascadeIndex) * 1.5, 4.0);
}

float ResolveDdgiMaterialTextureLod(GPUMaterialData material, uint volumeCascadeIndex)
{
    return max(DdgiMaterialTextureLod(volumeCascadeIndex), max(material.DdgiMaterialPolicy.y, 0.0));
}

vec3 ResolveCompactDdgiAlbedo(GPUMaterialData material)
{
    return dot(material.DdgiAverageAlbedo.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageAlbedo.rgb
        : material.Albedo.rgb;
}

vec3 ResolveCompactDdgiEmissive(GPUMaterialData material)
{
    return dot(material.DdgiAverageEmissive.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageEmissive.rgb
        : material.Emissive.rgb;
}

vec2 ApplyDdgiTextureTransform(vec2 uv, vec4 offsetScale, float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

bool IsIdentityDdgiTextureTransform(vec4 offsetScale, float rotationRadians)
{
    return abs(offsetScale.x) <= 0.0001 &&
           abs(offsetScale.y) <= 0.0001 &&
           abs(offsetScale.z - 1.0) <= 0.0001 &&
           abs(offsetScale.w - 1.0) <= 0.0001 &&
           abs(rotationRadians) <= 0.0001;
}

vec2 SelectDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet)
{
    return int(round(texCoordSet)) == 1 ? uv1 : uv0;
}

vec2 MaterialDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = SelectDdgiHitUv(uv0, uv1, texCoordSet);
    return IsIdentityDdgiTextureTransform(offsetScale, rotationRadians)
        ? uv
        : ApplyDdgiTextureTransform(uv, offsetScale, rotationRadians);
}

GPUDdgiRayQueryInstance ReadDdgiRayQueryInstance(uint instanceIndex)
{
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE / 4);
    GPUDdgiRayQueryInstance instance;
    instance.VertexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 0u);
    instance.IndexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 1u);
    instance.MaterialIndex = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 2u);
    instance.Padding0 = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 3u);
    instance.WorldMatrixInverseTranspose = ReadStorageMat4(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 4u);
    return instance;
}

bool ResolveCommittedHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    vec3 rayDirection,
    uint volumeCascadeIndex,
    bool sampleMaterialTextures,
    float materialTextureLod,
    out vec3 normal,
    out vec3 albedo,
    out vec3 emissive)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
    uint i0 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
    uint i1 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
    uint i2 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
    uint v0 = instance.VertexOffset + i0;
    uint v1 = instance.VertexOffset + i1;
    uint v2 = instance.VertexOffset + i2;

    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);

    vec3 p0 = ReadSplitVertexPosition(v0);
    vec3 p1 = ReadSplitVertexPosition(v1);
    vec3 p2 = ReadSplitVertexPosition(v2);
    vec3 fallbackLocalNormal = cross(p1 - p0, p2 - p0);
    fallbackLocalNormal = dot(fallbackLocalNormal, fallbackLocalNormal) > 0.000001
        ? normalize(fallbackLocalNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 localNormal =
        ReadSplitVertexNormal(v0) * bary.x +
        ReadSplitVertexNormal(v1) * bary.y +
        ReadSplitVertexNormal(v2) * bary.z;
    if (dot(localNormal, localNormal) <= 0.000001)
        localNormal = fallbackLocalNormal;

    normal = normalize(MulRowMajor(vec4(normalize(localNormal), 0.0), instance.WorldMatrixInverseTranspose).xyz);
    if (dot(normal, normal) <= 0.000001)
        normal = normalize(-rayDirection);
    if (dot(normal, rayDirection) > 0.0)
        normal = -normal;

    vec2 uv0 =
        ReadSplitVertexTexCoord(v0) * bary.x +
        ReadSplitVertexTexCoord(v1) * bary.y +
        ReadSplitVertexTexCoord(v2) * bary.z;
    vec2 uv1 =
        ReadSplitVertexTexCoord2(v0) * bary.x +
        ReadSplitVertexTexCoord2(v1) * bary.y +
        ReadSplitVertexTexCoord2(v2) * bary.z;
    vec3 vertexColor =
        ReadSplitVertexColor(v0).rgb * bary.x +
        ReadSplitVertexColor(v1).rgb * bary.y +
        ReadSplitVertexColor(v2).rgb * bary.z;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    if (ShouldUseCompactDdgiMaterial(volumeCascadeIndex))
    {
        albedo = max(ResolveCompactDdgiAlbedo(material) * vertexColor, vec3(0.0));
        emissive = max(ResolveCompactDdgiEmissive(material), vec3(0.0));
        return true;
    }

    materialTextureLod = ResolveDdgiMaterialTextureLod(material, volumeCascadeIndex);
    vec4 albedoSample = vec4(1.0);
    if (sampleMaterialTextures && material.AlbedoTextureIndex != DEFAULT_WHITE_TEXTURE)
    {
        vec2 albedoUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        albedoSample = SampleDdgiMaterialTexture(material.AlbedoTextureIndex, albedoUv, materialTextureLod, vec4(1.0));
    }
    albedo = max(ResolveCompactDdgiAlbedo(material) * albedoSample.rgb * vertexColor, vec3(0.0));

    vec4 emissiveSample = vec4(1.0);
    if (sampleMaterialTextures && material.EmissiveTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        vec2 emissiveUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w);
        emissiveSample = SampleDdgiMaterialTexture(material.EmissiveTextureIndex, emissiveUv, materialTextureLod, vec4(0.0));
    }
    emissive = max(ResolveCompactDdgiEmissive(material) * emissiveSample.rgb, vec3(0.0));
    return true;
}

float TraceLightVisibility(vec3 worldPosition, vec3 normal, vec3 lightDirection, float maxDistance)
{
    float normalOffset = DDGI_PROBE_TRACE_EPSILON * 4.0;
    float rayTMin = DDGI_PROBE_TRACE_EPSILON * 2.0;
    float rayDistance = max(maxDistance - normalOffset, rayTMin);
    vec3 origin = worldPosition + normal * normalOffset;

    rayQueryEXT shadowQuery;
    rayQueryInitializeEXT(
        shadowQuery,
        SceneTlas,
        gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT,
        0xff,
        origin,
        rayTMin,
        lightDirection,
        rayDistance);

    while (rayQueryProceedEXT(shadowQuery))
    {
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(shadowQuery, true);
    return hitType == gl_RayQueryCommittedIntersectionNoneEXT ? 1.0 : 0.0;
}

vec3 RotateDdgiEnvironmentDirection(vec3 direction, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return normalize(vec3(
        direction.x * c - direction.z * s,
        direction.y,
        direction.x * s + direction.z * c));
}

vec3 SampleDdgiEnvironmentMissRadianceWithFallback(vec3 direction, vec3 fallbackRadianceBase)
{
    float skyWeight = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 fallbackRadiance = fallbackRadianceBase * skyWeight;
    GPUEnvironmentData environment = ReadEnvironmentData();
    if (environment.Enabled == 0u || environment.EnvironmentTextureIndex < 0)
        return fallbackRadiance;

    vec3 environmentDirection = RotateDdgiEnvironmentDirection(direction, environment.RotationRadians);
    vec3 environmentRadiance = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.EnvironmentTextureIndex)],
        environmentDirection,
        0.0).rgb;
    return max(environmentRadiance, vec3(0.0)) * max(environment.DiffuseIntensity, 0.0);
}

#if DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER
vec3 SampleDdgiEnvironmentMissRadiance(vec3 direction)
{
    return SampleDdgiEnvironmentMissRadianceWithFallback(direction, pc.EnvironmentRadianceAndIntensity.rgb);
}
#endif

#if DDGI_HIT_USE_SELECTED_LIGHTS
bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)
{
    if (pc.DirectionalLightCount == 0u ||
        pc.PrimaryDirectionalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.PrimaryDirectionalLightIndex >= pc.LightCount)
        return false;

    selectedLight = ReadLight(pc.PrimaryDirectionalLightIndex);
    return selectedLight.Type == 1;
}

bool TryBuildSelectedDdgiLocalLightContribution(
    vec3 worldPosition,
    vec3 normal,
    out GPULight light,
    out vec3 lightDirection,
    out float distanceToLight,
    out float attenuation)
{
    if (pc.LocalLightCount == 0u ||
        pc.SelectedLocalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.SelectedLocalLightIndex >= pc.LightCount)
        return false;

    light = ReadLight(pc.SelectedLocalLightIndex);
    if (light.Type == 1)
        return false;

    vec3 toLight = light.Position - worldPosition;
    distanceToLight = length(toLight);
    if (distanceToLight >= light.Range || light.Range <= 0.0)
        return false;

    lightDirection = toLight / max(distanceToLight, 0.0001);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return false;

    float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
    attenuation = rangeFactor * rangeFactor;
    if (light.Type == 2)
    {
        float coneCos = cos(light.SpotAngle);
        float spotCos = dot(normalize(light.Direction), -lightDirection);
        float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
        attenuation *= spotFactor;
    }

    attenuation *= max(pc.SelectedLocalLightEnergyScale, 0.0);
    return attenuation > 0.0;
}
#endif

float DdgiHitLuminance(vec3 value)
{
    return dot(max(value, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

vec3 EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    vec3 normal,
    vec3 albedo,
    GPULight light,
    vec3 lightDirection,
    float visibilityDistance,
    float attenuation,
    out vec3 noShadowDiffuse)
{
    noShadowDiffuse = vec3(0.0);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    vec3 incomingRadiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    noShadowDiffuse = incomingRadiance * nDotL * (albedo / PI);
    if (DdgiHitLuminance(noShadowDiffuse) <= 0.0001)
        return vec3(0.0);

    float visibility = TraceLightVisibility(worldPosition, normal, lightDirection, visibilityDistance);
    return noShadowDiffuse * visibility;
}

vec3 EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    if (pc.EmissiveSourceCount == 0u)
        return vec3(0.0);

    const uint MaxEvaluatedEmissiveSources = 64u;
    uint sourceCount = min(pc.EmissiveSourceCount, MaxEvaluatedEmissiveSources);
    vec3 diffuseRadiance = vec3(0.0);
    for (uint sourceIndex = 0u; sourceIndex < sourceCount; sourceIndex++)
    {
        GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(sourceIndex);
        vec3 toSource = source.CenterRadius.xyz - worldPosition;
        float distanceToSource = length(toSource);
        float radius = max(source.CenterRadius.w, 0.001);
        if (distanceToSource >= radius)
            continue;

        vec3 lightDirection = toSource / max(distanceToSource, 0.0001);
        float nDotL = max(dot(normal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;

        float radiusAttenuation = 1.0 - distanceToSource / radius;
        radiusAttenuation *= radiusAttenuation;
        vec3 sourceRadiance = max(source.RadianceImportance.rgb, vec3(0.0));
        diffuseRadiance += sourceRadiance * nDotL * radiusAttenuation * (albedo / PI);
    }

    return diffuseRadiance;
}

vec3 EvaluateDirectDiffuseRadianceAtHit(vec3 worldPosition, vec3 normal, vec3 albedo, out vec3 directNoShadowDiffuse)
{
    vec3 directDiffuseRadiance = vec3(0.0);
    directNoShadowDiffuse = vec3(0.0);

#if DDGI_HIT_USE_SELECTED_LIGHTS
    uint selectedLightCapacity = min(DDGI_HIT_MAX_SHADED_LIGHTS, DDGI_MAX_SELECTED_HIT_LIGHTS);
    uint selectedLightCount = 0u;
    if (selectedLightCapacity == 0u || pc.LightCount == 0u)
        return directDiffuseRadiance;

    GPULight directionalLight;
    if (TryReadSelectedDdgiDirectionalLight(directionalLight))
    {
        vec3 lightDirection = normalize(-directionalLight.Direction);
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            normal,
            albedo,
            directionalLight,
            lightDirection,
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
        selectedLightCount++;
    }

    if (selectedLightCount >= selectedLightCapacity)
        return directDiffuseRadiance;

    GPULight localLight;
    vec3 localLightDirection;
    float localLightDistance;
    float localLightAttenuation;
    if (TryBuildSelectedDdgiLocalLightContribution(
        worldPosition,
        normal,
        localLight,
        localLightDirection,
        localLightDistance,
        localLightAttenuation))
    {
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            normal,
            albedo,
            localLight,
            localLightDirection,
            localLightDistance,
            localLightAttenuation,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
#else
    uint selectedLightCapacity = min(pc.LightCount, DDGI_HIT_DIRECT_LIGHT_CAP);
    for (uint i = 0u; i < selectedLightCapacity; i++)
    {
        GPULight light = ReadLight(i);
        vec3 lightDirection;
        float visibilityDistance = DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE;
        float attenuation = 1.0;
        if (light.Type == 1)
        {
            lightDirection = normalize(-light.Direction);
        }
        else
        {
            vec3 toLight = light.Position - worldPosition;
            float distanceToLight = length(toLight);
            if (distanceToLight >= light.Range || light.Range <= 0.0)
                continue;
            lightDirection = toLight / max(distanceToLight, 0.0001);
            visibilityDistance = distanceToLight;
            float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
            attenuation = rangeFactor * rangeFactor;
            if (light.Type == 2)
            {
                float coneCos = cos(light.SpotAngle);
                float spotCos = dot(normalize(light.Direction), -lightDirection);
                attenuation *= smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
            }
        }

        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            normal,
            albedo,
            light,
            lightDirection,
            visibilityDistance,
            attenuation,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
#endif

    return directDiffuseRadiance;
}

#endif
