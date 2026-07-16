#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform CompositePushBlock
{
    uint SceneColorTextureIndex;
    uint BloomTextureIndex;
    uint BloomDebugTextureIndex;
    uint BloomEnabled;
    float Exposure;
    float BloomIntensity;
    uint ToneMapper;
    uint DebugViewMode;
    uint OutputToSrgb;
    uint EnvironmentDebugView;
    uint EnvironmentDebugMipLevel;
    uint AmbientOcclusionDebugTextureIndex;
    uint AutoExposureEnabled;
    uint AutoExposureStateBufferIndex;
    uint Padding0;
    uint Padding1;
} pc;

const uint TONE_MAPPER_NONE = 0u;
const uint TONE_MAPPER_REINHARD = 1u;
const uint TONE_MAPPER_ACES_FITTED = 2u;
const uint DEBUG_VIEW_RAW_HDR = 1u;
const uint DEBUG_VIEW_BLOOM_EXTRACT_MASK = 2u;
const uint DEBUG_VIEW_BLOOM_DOWNSAMPLE_MIP = 3u;
const uint DEBUG_VIEW_BLOOM_UPSAMPLE_RESULT = 4u;
const uint DEBUG_VIEW_BLOOM_ONLY = 5u;
const uint AO_DEBUG_RAW = 1u;
const uint AO_DEBUG_BLURRED = 2u;
const uint ENVIRONMENT_DEBUG_IRRADIANCE_CUBEMAP = 2u;
const uint ENVIRONMENT_DEBUG_PREFILTERED_ENVIRONMENT_MIP = 3u;
const uint ENVIRONMENT_DEBUG_BRDF_LUT = 4u;

vec3 AcesFitted(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

vec3 LinearToSrgb(vec3 color)
{
    bvec3 cutoff = lessThanEqual(color, vec3(0.0031308));
    vec3 lower = color * 12.92;
    vec3 higher = 1.055 * pow(max(color, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
    return mix(higher, lower, cutoff);
}

void main()
{
    vec3 hdr = max(texture(BindlessTextures[nonuniformEXT(int(pc.SceneColorTextureIndex))], inUv).rgb, vec3(0.0));
    float exposure = max(pc.Exposure, 0.0);
    if (pc.AutoExposureEnabled != 0u)
        exposure = max(ReadStorageFloat(pc.AutoExposureStateBufferIndex, 0u), 0.0);

    if (pc.AmbientOcclusionDebugTextureIndex == uint(AMBIENT_OCCLUSION_RAW_TEXTURE_INDEX) ||
        pc.AmbientOcclusionDebugTextureIndex == uint(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX))
    {
        float ao = texture(BindlessTextures[nonuniformEXT(int(pc.AmbientOcclusionDebugTextureIndex))], inUv).r;
        vec3 debugColor = vec3(clamp(ao, 0.0, 1.0));
        if (pc.OutputToSrgb != 0u)
            debugColor = LinearToSrgb(debugColor);
        outColor = vec4(debugColor, 1.0);
        return;
    }

    if (pc.EnvironmentDebugView == ENVIRONMENT_DEBUG_BRDF_LUT)
    {
        vec3 brdf = texture(BindlessTextures[nonuniformEXT(BRDF_LUT_TEXTURE_INDEX)], inUv).rrg;
        vec3 debugColor = clamp(brdf, 0.0, 1.0);
        if (pc.OutputToSrgb != 0u)
            debugColor = LinearToSrgb(debugColor);
        outColor = vec4(debugColor, 1.0);
        return;
    }

    if (pc.EnvironmentDebugView == ENVIRONMENT_DEBUG_IRRADIANCE_CUBEMAP ||
        pc.EnvironmentDebugView == ENVIRONMENT_DEBUG_PREFILTERED_ENVIRONMENT_MIP)
    {
        vec2 xy = inUv * 2.0 - vec2(1.0);
        vec3 direction = normalize(vec3(xy, 1.0));
        float lod = pc.EnvironmentDebugView == ENVIRONMENT_DEBUG_PREFILTERED_ENVIRONMENT_MIP
            ? float(pc.EnvironmentDebugMipLevel)
            : 0.0;
        int textureIndex = pc.EnvironmentDebugView == ENVIRONMENT_DEBUG_PREFILTERED_ENVIRONMENT_MIP
            ? PREFILTERED_ENVIRONMENT_TEXTURE_INDEX
            : IRRADIANCE_CUBEMAP_TEXTURE_INDEX;
        vec3 debugColor = textureLod(BindlessCubeTextures[nonuniformEXT(textureIndex)], direction, lod).rgb;
        debugColor = clamp(debugColor * exposure, 0.0, 1.0);
        if (pc.OutputToSrgb != 0u)
            debugColor = LinearToSrgb(debugColor);
        outColor = vec4(debugColor, 1.0);
        return;
    }

    if (pc.DebugViewMode == DEBUG_VIEW_BLOOM_EXTRACT_MASK ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_DOWNSAMPLE_MIP ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_UPSAMPLE_RESULT ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_ONLY)
    {
        vec3 debugBloom = max(texture(BindlessTextures[nonuniformEXT(int(pc.BloomDebugTextureIndex))], inUv).rgb, vec3(0.0));
        vec3 debugColor = clamp(debugBloom * max(pc.BloomIntensity, 0.0), 0.0, 1.0);
        if (pc.OutputToSrgb != 0u)
            debugColor = LinearToSrgb(debugColor);
        outColor = vec4(debugColor, 1.0);
        return;
    }

    if (pc.BloomEnabled != 0u && pc.DebugViewMode != DEBUG_VIEW_RAW_HDR)
    {
        vec3 bloom = max(texture(BindlessTextures[nonuniformEXT(int(pc.BloomTextureIndex))], inUv).rgb, vec3(0.0));
        hdr += bloom * max(pc.BloomIntensity, 0.0);
    }

    vec3 color = hdr * exposure;

    if (pc.DebugViewMode == DEBUG_VIEW_RAW_HDR || pc.ToneMapper == TONE_MAPPER_NONE)
    {
        color = clamp(color, 0.0, 1.0);
    }
    else if (pc.ToneMapper == TONE_MAPPER_REINHARD)
    {
        color = color / (vec3(1.0) + color);
    }
    else
    {
        color = AcesFitted(color);
    }

    if (pc.OutputToSrgb != 0u)
        color = LinearToSrgb(color);

    outColor = vec4(color, 1.0);
}
