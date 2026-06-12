#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) in vec4 fragWorldTangent;
layout(location = 6) flat in uint fragMeshletIndex;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

const float PI = 3.14159265359;
const uint DEBUG_VIEW_NONE = 0u;
const uint DEBUG_VIEW_MESHLETS = 1u;
const uint DEBUG_VIEW_SHADOW_CASCADE_OVERLAY = 2u;
const uint DEBUG_VIEW_SHADOW_MAP_PREVIEW = 3u;
const uint DEBUG_VIEW_SHADOW_RECEIVER_FACTOR = 4u;
const uint DEBUG_VIEW_SPOT_ATLAS_PREVIEW = 5u;
const uint DEBUG_VIEW_POINT_CUBEMAP_FACE_PREVIEW = 6u;
const uint DEBUG_VIEW_LOCAL_SHADOW_SELECTION = 7u;

uint HashUint(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

vec3 MeshletDebugColor(uint meshletIndex)
{
    uint hash = HashUint(meshletIndex + 1u);
    return vec3(
        float(hash & 0xffu),
        float((hash >> 8u) & 0xffu),
        float((hash >> 16u) & 0xffu)) / 255.0;
}

vec4 SampleMaterialTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
}

uint SelectShadowCascade(float cameraDistance, vec4 splits, uint cascadeCount)
{
    for (uint cascade = 0u; cascade < cascadeCount; cascade++)
    {
        if (cameraDistance <= splits[cascade])
            return cascade;
    }

    return max(cascadeCount, 1u) - 1u;
}

float SampleShadowCascade(uint textureIndex, vec2 uv, float receiverDepth, float bias)
{
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || receiverDepth < 0.0 || receiverDepth > 1.0)
        return 1.0;

    float sampledDepth = texture(BindlessTextures[nonuniformEXT(int(textureIndex))], uv).r;
    return receiverDepth >= sampledDepth - bias ? 1.0 : 0.0;
}

float EvaluateDirectionalShadow(uint lightIndex, vec3 worldPosition, vec3 normal, out uint selectedCascade)
{
    selectedCascade = 0u;
    vec4 shadowIndices = ReadShadowIndices();
    if (shadowIndices.x < 0.5 ||
        pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX) ||
        int(round(shadowIndices.w)) != int(lightIndex))
        return 1.0;

    vec4 shadowSettings = ReadShadowSettings();
    uint cascadeCount = clamp(uint(round(shadowIndices.y)), 1u, uint(MAX_DIRECTIONAL_SHADOW_TEXTURES));
    vec4 splits = ReadShadowCascadeSplits();
    float cameraDistance = length(pc.Push.CameraPosition - worldPosition);
    selectedCascade = SelectShadowCascade(cameraDistance, splits, cascadeCount);

    vec3 biasedPosition = worldPosition + normal * shadowSettings.y;
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), ReadShadowMatrix(selectedCascade));
    vec3 shadowCoord = lightClip.xyz / max(lightClip.w, 0.00001);
    vec2 uv = shadowCoord.xy * 0.5 + vec2(0.5);
    float receiverDepth = shadowCoord.z;

    float mapSize = max(shadowSettings.z, 1.0);
    int radius = int(clamp(round(shadowSettings.w), 0.0, 3.0));
    vec2 texelSize = vec2(1.0 / mapSize);
    uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + selectedCascade;

    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            lit += SampleShadowCascade(textureIndex, uv + vec2(x, y) * texelSize, receiverDepth, 0.0005);
            taps += 1.0;
        }
    }

    return taps > 0.0 ? lit / taps : 1.0;
}

float CompareReverseZDepth(float receiverDepth, float sampledDepth, float bias)
{
    if (receiverDepth < 0.0 || receiverDepth > 1.0)
        return 1.0;
    return receiverDepth >= sampledDepth - bias ? 1.0 : 0.0;
}

float EvaluateSpotShadow(uint lightIndex, vec3 worldPosition, vec3 normal)
{
    int shadowIndex = ReadLocalSpotShadowIndex(lightIndex);
    if (shadowIndex < 0 || pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX))
        return 1.0;

    GPUSpotShadow shadow = ReadSpotShadow(uint(shadowIndex));
    if (shadow.Enabled == 0 || shadow.LightIndex != int(lightIndex))
        return 1.0;

    vec3 biasedPosition = worldPosition + normal * shadow.BiasStrengthTexelSize.x;
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), shadow.LightViewProjection);
    vec3 shadowCoord = lightClip.xyz / max(lightClip.w, 0.00001);
    vec2 localUv = shadowCoord.xy * 0.5 + vec2(0.5);
    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return 1.0;

    vec2 atlasUv = localUv * shadow.AtlasScaleOffset.xy + shadow.AtlasScaleOffset.zw;
    vec2 minUv = shadow.AtlasScaleOffset.zw;
    vec2 maxUv = shadow.AtlasScaleOffset.zw + shadow.AtlasScaleOffset.xy;
    int radius = int(clamp(shadow.PcfRadius, 0, 3));
    vec2 texelSize = vec2(shadow.BiasStrengthTexelSize.w);

    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            vec2 sampleUv = clamp(atlasUv + vec2(x, y) * texelSize, minUv, maxUv);
            float sampledDepth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], sampleUv).r;
            lit += CompareReverseZDepth(shadowCoord.z, sampledDepth, shadow.BiasStrengthTexelSize.y);
            taps += 1.0;
        }
    }

    float visibility = taps > 0.0 ? lit / taps : 1.0;
    return mix(1.0, visibility, shadow.BiasStrengthTexelSize.z);
}

uint SelectPointShadowFace(vec3 direction)
{
    vec3 absDir = abs(direction);
    if (absDir.x >= absDir.y && absDir.x >= absDir.z)
        return direction.x >= 0.0 ? 0u : 1u;
    if (absDir.y >= absDir.x && absDir.y >= absDir.z)
        return direction.y >= 0.0 ? 2u : 3u;
    return direction.z >= 0.0 ? 4u : 5u;
}

float EvaluatePointShadow(uint lightIndex, vec3 worldPosition, vec3 normal)
{
    int shadowIndex = ReadLocalPointShadowIndex(lightIndex);
    if (shadowIndex < 0 || pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX))
        return 1.0;

    GPUPointShadow shadow = ReadPointShadow(uint(shadowIndex));
    if (shadow.Enabled == 0 || shadow.LightIndex != int(lightIndex))
        return 1.0;

    vec3 lightPosition = shadow.PositionRange.xyz;
    vec3 toReceiver = worldPosition - lightPosition;
    float range = max(shadow.PositionRange.w, 0.001);
    if (length(toReceiver) > range)
        return 1.0;

    vec3 sampleDirection = normalize(toReceiver);
    uint faceIndex = SelectPointShadowFace(sampleDirection);
    mat4 faceMatrix = ReadPointShadowFaceMatrix(uint(shadowIndex), faceIndex);
    vec3 biasedPosition = worldPosition + normal * shadow.BiasStrengthTexelSize.x;
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), faceMatrix);
    vec3 shadowCoord = lightClip.xyz / max(lightClip.w, 0.00001);
    vec2 faceUv = shadowCoord.xy * 0.5 + vec2(0.5);
    if (faceUv.x < 0.0 || faceUv.x > 1.0 || faceUv.y < 0.0 || faceUv.y > 1.0)
        return 1.0;

    int radius = int(clamp(shadow.PcfRadius, 0, 2));
    vec2 texelSize = vec2(shadow.BiasStrengthTexelSize.w);
    float layer = float(shadow.CubemapIndex * 6 + int(faceIndex));
    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            vec2 sampleUv = clamp(faceUv + vec2(x, y) * texelSize, vec2(0.0), vec2(1.0));
            float sampledDepth = texture(BindlessArrayTextures[nonuniformEXT(POINT_SHADOW_CUBEMAP_ARRAY_TEXTURE_INDEX)], vec3(sampleUv, layer)).r;
            lit += CompareReverseZDepth(shadowCoord.z, sampledDepth, shadow.BiasStrengthTexelSize.y);
            taps += 1.0;
        }
    }

    float visibility = taps > 0.0 ? lit / taps : 1.0;
    return mix(1.0, visibility, shadow.BiasStrengthTexelSize.z);
}

vec3 ResolveNormal(GPUMaterialData material, vec3 interpolatedNormal, vec4 interpolatedTangent, vec2 uv)
{
    vec3 n = normalize(interpolatedNormal);
    vec3 t = normalize(interpolatedTangent.xyz - n * dot(n, interpolatedTangent.xyz));
    vec3 b = normalize(cross(n, t) * interpolatedTangent.w);

    vec3 tangentNormal = SampleMaterialTexture(material.NormalTextureIndex, uv).xyz * 2.0 - 1.0;
    tangentNormal.xy *= material.NormalScaleBias.x;
    tangentNormal = normalize(tangentNormal);

    mat3 tbn = mat3(t, b, n);
    return normalize(tbn * tangentNormal);
}

float DistributionGGX(float nDotH, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSq = alpha * alpha;
    float denom = nDotH * nDotH * (alphaSq - 1.0) + 1.0;
    return alphaSq / max(PI * denom * denom, 0.000001);
}

float GeometrySchlickGGX(float nDotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) * 0.125;
    return nDotV / max(nDotV * (1.0 - k) + k, 0.000001);
}

float GeometrySmith(float nDotV, float nDotL, float roughness)
{
    return GeometrySchlickGGX(nDotV, roughness) * GeometrySchlickGGX(nDotL, roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance)
{
    vec3 halfVector = normalize(viewDirection + lightDirection);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotH = max(dot(normal, halfVector), 0.0);
    float hDotV = max(dot(halfVector, viewDirection), 0.0);

    if (nDotL <= 0.0 || nDotV <= 0.0)
        return vec3(0.0);

    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = FresnelSchlick(hDotV, f0);
    float distribution = DistributionGGX(nDotH, roughness);
    float geometry = GeometrySmith(nDotV, nDotL, roughness);

    vec3 specular = (distribution * geometry * fresnel) / max(4.0 * nDotV * nDotL, 0.000001);
    vec3 diffuseWeight = (vec3(1.0) - fresnel) * (1.0 - metallic);
    vec3 diffuse = diffuseWeight * albedo / PI;

    return (diffuse + specular) * radiance * nDotL;
}

void AccumulateLight(
    uint lightIndex,
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 normal,
    vec3 viewDirection,
    vec3 worldPosition,
    out float shadowFactor,
    out uint shadowCascade,
    inout vec3 directLighting)
{
    GPULight light = ReadLight(lightIndex);
    shadowFactor = 1.0;
    shadowCascade = 0u;

    vec3 lightDirection;
    float attenuation = 1.0;

    if (light.Type == 1)
    {
        lightDirection = normalize(-light.Direction);
        shadowFactor = EvaluateDirectionalShadow(lightIndex, worldPosition, normal, shadowCascade);
    }
    else
    {
        vec3 toLight = light.Position - worldPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.Range || light.Range <= 0.0)
            return;

        lightDirection = toLight / max(distanceToLight, 0.0001);
        float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
        attenuation = rangeFactor * rangeFactor;

        if (light.Type == 2)
        {
            float coneCos = cos(light.SpotAngle);
            float spotCos = dot(normalize(light.Direction), -lightDirection);
            float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
            attenuation *= spotFactor;
            shadowFactor = EvaluateSpotShadow(lightIndex, worldPosition, normal);
        }
        else
        {
            shadowFactor = EvaluatePointShadow(lightIndex, worldPosition, normal);
        }
    }

    vec3 radiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    directLighting += EvaluatePbrLight(
        albedo,
        metallic,
        roughness,
        normal,
        viewDirection,
        lightDirection,
        radiance) * shadowFactor;
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec2 uv = fragTexCoord * material.TexCoordOffsetScale.zw + material.TexCoordOffsetScale.xy;

    vec4 albedoSample = SampleMaterialTexture(material.AlbedoTextureIndex, uv);
    float alphaMode = material.NormalScaleBias.y;
    float alphaCutoff = material.NormalScaleBias.z;
    float outputAlpha = material.Albedo.a * albedoSample.a;

    if (alphaMode > 0.5 && alphaMode < 1.5 && outputAlpha <= alphaCutoff)
        discard;
    if (alphaMode > 1.5 && outputAlpha <= 0.001)
        discard;

    if (pc.Push.DebugViewMode == DEBUG_VIEW_MESHLETS)
    {
        outColor = vec4(MeshletDebugColor(fragMeshletIndex), 1.0);
        return;
    }

    vec3 normal = ResolveNormal(material, fragNormal, fragWorldTangent, uv);
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // glTF metallic-roughness contract: G = roughness, B = metallic.
    // R is occlusion only when the material upload marks this as a shared ORM texture.
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(material.MetallicRoughnessTextureIndex, uv);
    vec4 emissiveSample = SampleMaterialTexture(material.EmissiveTextureIndex, uv);

    float roughness = clamp(material.MetallicRoughnessAO.y * armSample.g, 0.04, 1.0);
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    float sampledOcclusion = material.MetallicRoughnessAO.w > 0.5 ? armSample.r : 1.0;
    float ambientOcclusion = clamp(material.MetallicRoughnessAO.z * sampledOcclusion, 0.0, 1.0);
    vec3 albedo = max(material.Albedo.rgb * albedoSample.rgb, vec3(0.0));
    vec3 emissive = max(material.Emissive.rgb * emissiveSample.rgb, vec3(0.0));

    vec3 ambient = albedo * 0.08 * ambientOcclusion;
    vec3 directLighting = vec3(0.0);
    float lastShadowFactor = 1.0;
    uint lastShadowCascade = 0u;

    if (pc.Push.DebugViewMode == DEBUG_VIEW_SHADOW_MAP_PREVIEW)
    {
        vec2 previewUv = gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(DIRECTIONAL_SHADOW_TEXTURE_BASE)], previewUv).r;
        outColor = vec4(vec3(depth), 1.0);
        return;
    }

    if (pc.Push.DebugViewMode == DEBUG_VIEW_SPOT_ATLAS_PREVIEW)
    {
        vec2 previewUv = gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], previewUv).r;
        outColor = vec4(vec3(depth), 1.0);
        return;
    }

    if (pc.Push.LocalLightCount == 0u)
    {
        for (uint i = 0u; i < pc.Push.LightCount; i++)
        {
            AccumulateLight(
                i,
                albedo,
                metallic,
                roughness,
                normal,
                viewDirection,
                fragWorldPosition,
                lastShadowFactor,
                lastShadowCascade,
                directLighting);
        }
    }
    else
    {
        vec2 safeScreenSize = max(pc.Push.ScreenDimensions, vec2(1.0));
        uvec2 pixel = uvec2(clamp(gl_FragCoord.xy, vec2(0.0), safeScreenSize - vec2(1.0)));
        uvec2 tile = pixel / uvec2(16u, 16u);
        uint tileCountX = uint(ceil(safeScreenSize.x / 16.0));
        uint tileIndex = tile.y * tileCountX + tile.x;
        GPUTiledLightHeader tileHeader = ReadTiledLightHeader(tileIndex);

        for (uint i = 0u; i < tileHeader.LightCount; i++)
        {
            AccumulateLight(
                ReadTiledLightIndex(tileHeader.LightOffset + i),
                albedo,
                metallic,
                roughness,
                normal,
                viewDirection,
                fragWorldPosition,
                lastShadowFactor,
                lastShadowCascade,
                directLighting);
        }
    }

    if (pc.Push.DebugViewMode == DEBUG_VIEW_SHADOW_RECEIVER_FACTOR)
    {
        outColor = vec4(vec3(lastShadowFactor), 1.0);
        return;
    }

    if (pc.Push.DebugViewMode == DEBUG_VIEW_SHADOW_CASCADE_OVERLAY)
    {
        vec3 cascadeColor = lastShadowCascade == 0u ? vec3(0.9, 0.15, 0.1) :
            lastShadowCascade == 1u ? vec3(0.1, 0.75, 0.2) :
            lastShadowCascade == 2u ? vec3(0.1, 0.35, 0.95) :
            vec3(0.9, 0.8, 0.1);
        directLighting = mix(directLighting, cascadeColor, 0.35);
    }

    vec3 color = ambient + directLighting + emissive;

    outColor = vec4(color, alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha);
}
