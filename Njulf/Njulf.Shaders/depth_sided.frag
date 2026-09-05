#version 460
#extension GL_GOOGLE_include_directive : require

#ifdef NJULF_OPAQUE_VISIBILITY
#extension GL_EXT_mesh_shader : require
layout(location = 12) perprimitiveEXT flat in uvec2 fragVisibility;
layout(location = 0) out uvec2 outVisibility;
#endif

#include "common.glsl"

layout(location = 0) flat in uint fragMaterialIndex;

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;
#ifdef NJULF_OPAQUE_VISIBILITY
    outVisibility = fragVisibility | uvec2(0u, gl_FrontFacing ? 0x80000000u : 0u);
#endif
}
