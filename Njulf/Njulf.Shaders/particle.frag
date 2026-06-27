#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec4 inParams;
layout(location = 3) flat in uint inTextureIndex;
layout(location = 4) flat in uint inBlendMode;
layout(location = 5) flat in uint inDebugId;
layout(location = 6) in vec2 inNextUv;
layout(location = 7) flat in float inFlipbookBlend;
layout(location = 8) in vec3 inWorldPosition;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform ParticlePushBlock
{
    GPUParticlePushConstants Push;
} pc;

const uint PARTICLE_DEBUG_NONE = 0u;
const uint PARTICLE_DEBUG_SOFT_FADE = 3u;
const uint PARTICLE_DEBUG_FLIPBOOK_FRAME = 4u;
const uint PARTICLE_DEBUG_LIFETIME = 6u;
const uint PARTICLE_DEBUG_EMITTER_ID = 8u;
const uint PARTICLE_BLEND_ALPHA_CLIP = 4u;

vec3 DebugColor(uint id)
{
    id ^= id >> 16u;
    id *= 0x7feb352du;
    id ^= id >> 15u;
    id *= 0x846ca68bu;
    id ^= id >> 16u;
    return vec3(
        float(id & 0xffu),
        float((id >> 8u) & 0xffu),
        float((id >> 16u) & 0xffu)) / 255.0;
}

vec3 ReconstructViewPosition(vec2 uv, float depth, GPUParticleFrameData frame)
{
    vec4 clip = vec4(uv * 2.0 - vec2(1.0), depth, 1.0);
    vec4 view = MulRowMajor(clip, frame.InverseProjectionMatrix);
    return view.xyz / max(abs(view.w), 0.00001);
}

float SoftParticleFade()
{
    float softDistance = inParams.z;
    if (pc.Push.SoftParticlesEnabled == 0u || softDistance <= 0.0001)
        return 1.0;

    GPUParticleFrameData frame = ReadParticleFrameData(
        pc.Push.CurrentFrameIndex,
        pc.Push.ParticleFrameDataBufferBaseIndex);
    vec2 screenUv = gl_FragCoord.xy / max(frame.ScreenDimensions, vec2(1.0));
    float sceneDepth = texture(BindlessTextures[nonuniformEXT(int(pc.Push.DepthTextureIndex))], screenUv).r;
    if (sceneDepth <= 0.000001)
        return 1.0;

    float particleDepth = abs(ReconstructViewPosition(screenUv, gl_FragCoord.z, frame).z);
    float geometryDepth = abs(ReconstructViewPosition(screenUv, sceneDepth, frame).z);
    return clamp(abs(geometryDepth - particleDepth) / softDistance, 0.0, 1.0);
}

void main()
{
    vec4 sampleColor = texture(BindlessTextures[nonuniformEXT(int(inTextureIndex))], inUv);
    if (inFlipbookBlend > 0.0001)
    {
        vec4 nextSampleColor = texture(BindlessTextures[nonuniformEXT(int(inTextureIndex))], inNextUv);
        sampleColor = mix(sampleColor, nextSampleColor, clamp(inFlipbookBlend, 0.0, 1.0));
    }

    vec4 color = sampleColor * inColor;
    float softFade = SoftParticleFade();
    color.a *= softFade;

    if (inBlendMode == PARTICLE_BLEND_ALPHA_CLIP && color.a <= inParams.w)
        discard;
    if (color.a <= 0.001)
        discard;

    if (pc.Push.DebugView == PARTICLE_DEBUG_SOFT_FADE)
    {
        outColor = vec4(vec3(softFade), 1.0);
        return;
    }

    if (pc.Push.DebugView == PARTICLE_DEBUG_LIFETIME)
    {
        outColor = vec4(inParams.y, 1.0 - inParams.y, 0.0, max(color.a, 0.35));
        return;
    }

    if (pc.Push.DebugView == PARTICLE_DEBUG_EMITTER_ID)
    {
        outColor = vec4(DebugColor(inDebugId), max(color.a, 0.35));
        return;
    }

    GPUParticleFrameData frame = ReadParticleFrameData(
        pc.Push.CurrentFrameIndex,
        pc.Push.ParticleFrameDataBufferBaseIndex);
    vec3 viewVector = frame.CameraPosition - inWorldPosition;
    float viewVectorLength = length(viewVector);
    vec3 viewFacingNormal = viewVectorLength > 0.0001 ? viewVector / viewVectorLength : vec3(0.0, 1.0, 0.0);
    if (viewVectorLength <= 0.0001)
        viewFacingNormal = vec3(0.0, 1.0, 0.0);

    float emissiveStrength = max(inParams.x, 0.0);
    vec3 hdr = color.rgb * emissiveStrength;
    float nonEmissiveWeight = clamp(1.0 - max(emissiveStrength - 1.0, 0.0), 0.0, 1.0);
    vec3 ddgiAmbient = SampleDdgiAmbientDiffuse(inWorldPosition, viewFacingNormal, color.rgb, 0.75, 6u);
    hdr += ddgiAmbient * nonEmissiveWeight;
    outColor = vec4(hdr, color.a);
}
