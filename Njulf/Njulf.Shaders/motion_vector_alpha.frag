#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"
#include "material_coverage.glsl"

layout(location = 0) noperspective in vec2 inCurrentUv;
layout(location = 1) noperspective in vec2 inPreviousUv;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in vec2 inTexCoord2;
layout(location = 4) in vec4 inVertexColor;
layout(location = 5) flat in uint inMaterialIndex;
layout(location = 6) flat in uint inReceiverSignature;
layout(location = 7) flat in uint inHistoryFrameAndFlags;
layout(location = 0) out vec2 outVelocity;
#ifdef MOTION_VECTOR_IDENTITY_ATTACHMENT
layout(location = 1) out uint outReceiverIdentity;
#endif

layout(push_constant) uniform MotionVectorPushConstantBlock
{
    GPUMotionVectorPushConstants Push;
} pc;

void main()
{
    GPUMaterialData material = ReadMaterial(inMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    MaterialAlphaCoverage coverage = EvaluateMaterialAlphaCoverage(
        material,
        inTexCoord,
        inTexCoord2,
        inVertexColor.a);
    if (!MaterialCoverageSurvivesForward(coverage))
        discard;

    outVelocity = clamp(inCurrentUv - inPreviousUv, vec2(-1.0), vec2(1.0));
#ifdef MOTION_VECTOR_DEPTH_WRITE
#ifdef MOTION_VECTOR_IDENTITY_ATTACHMENT
    outReceiverIdentity = inReceiverSignature == 0u ? 1u : inReceiverSignature;
#endif
#else
    if ((inHistoryFrameAndFlags & 0x100u) != 0u)
    {
        uint frameIndex = inHistoryFrameAndFlags & 0xffu;
        uvec2 dimensions = uvec2(max(textureSize(
            BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0),
            ivec2(1)));
        uvec2 pixel = uvec2(clamp(
            floor(gl_FragCoord.xy),
            vec2(0.0),
            vec2(dimensions - uvec2(1u))));
        uint pixelIndex = pixel.y * dimensions.x + pixel.x;
        uint identity = inReceiverSignature == 0u ? 1u : inReceiverSignature;
        WriteStorageWord(
            uint(DIRECTIONAL_SHADOW_SCRATCH_BUFFER_BASE_INDEX) + frameIndex,
            pixelIndex,
            identity);
    }
#endif
}
