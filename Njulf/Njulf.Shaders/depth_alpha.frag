#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#ifdef NJULF_OPAQUE_VISIBILITY
#extension GL_EXT_mesh_shader : require
layout(location = 12) perprimitiveEXT flat in uvec2 fragVisibility;
layout(location = 0) out uvec2 outVisibility;
#endif

#include "common.glsl"
#include "material_coverage.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 2) in vec2 fragTexCoord2;
layout(location = 3) in vec4 fragVertexColor;

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    MaterialAlphaCoverage coverage = EvaluateMaterialAlphaCoverage(
        material,
        fragTexCoord,
        fragTexCoord2,
        fragVertexColor.a);
    if (!MaterialCoverageSurvivesForward(coverage))
        discard;
#ifdef NJULF_OPAQUE_VISIBILITY
    outVisibility = fragVisibility | uvec2(0u, gl_FrontFacing ? 0x80000000u : 0u);
#endif
}
