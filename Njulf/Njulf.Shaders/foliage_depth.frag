#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "foliage_coverage.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 4) flat in uint fragClusterIndex;
layout(location = 5) flat in uint fragLodBand;
layout(location = 6) flat in uint fragGeometryMode;

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec4 sampledAlbedo;
    if (!FoliageCoverageSurvives(
            material,
            fragTexCoord,
            fragGeometryMode,
            fragClusterIndex,
            fragLodBand,
            gl_FragCoord.xy,
            sampledAlbedo))
        discard;
}
