#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) flat in uint fragMaterialIndex;

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;
}
