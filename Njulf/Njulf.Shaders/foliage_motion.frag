#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) noperspective in vec2 inCurrentUv;
layout(location = 1) noperspective in vec2 inPreviousUv;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) flat in uint inMaterialIndex;
layout(location = 0) out vec2 outVelocity;

void main()
{
    GPUMaterialData material = ReadMaterial(inMaterialIndex);
    if (material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX)
    {
        float alpha = texture(BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)], inTexCoord).a;
        if (material.NormalScaleBias.y >= 0.5 && alpha < material.NormalScaleBias.z)
            discard;
    }

    outVelocity = clamp(inCurrentUv - inPreviousUv, vec2(-1.0), vec2(1.0));
}
