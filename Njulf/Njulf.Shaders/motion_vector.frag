#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) noperspective in vec2 inCurrentUv;
layout(location = 1) noperspective in vec2 inPreviousUv;
layout(location = 2) flat in uint inReceiverSignature;
layout(location = 3) flat in uint inHistoryFrameAndFlags;
layout(location = 4) flat in uint inMaterialIndex;
layout(location = 0) out vec2 outVelocity;

void main()
{
    GPUMaterialData material = ReadMaterial(inMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    outVelocity = clamp(inCurrentUv - inPreviousUv, vec2(-1.0), vec2(1.0));
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
        WriteStorageWord(
            uint(DIRECTIONAL_SHADOW_SCRATCH_BUFFER_BASE_INDEX) + frameIndex,
            pixel.y * dimensions.x + pixel.x,
            inReceiverSignature == 0u ? 1u : inReceiverSignature);
    }
}
