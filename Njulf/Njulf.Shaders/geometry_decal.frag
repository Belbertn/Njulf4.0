#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

// Geometry decals sit on an opaque, fully lit depth owner. For color-only
// grime, inheriting that destination's direct light, DDGI, reflections, and
// shadowing is both more faithful and much cheaper than shading the same
// receiver again. The pipeline uses destination-color modulation:
//   dst * (1 - alpha + alpha * tint)
layout(early_fragment_tests) in;

#include "common.glsl"
#include "material_coverage.glsl"
#include "gi_material_transport.glsl"

layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) in vec4 fragWorldTangent;
layout(location = 6) flat in uint fragMeshletIndex;
layout(location = 7) in vec2 fragTexCoord2;
layout(location = 8) in vec4 fragVertexColor;

layout(location = 0) out vec4 outColor;

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    if (!GiMaterialHasFlag(
            material.TransportFlags,
            GI_MATERIAL_GEOMETRY_DECAL))
    {
        discard;
    }

    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    vec2 uv = MaterialCoverageUv(
        fragTexCoord,
        fragTexCoord2,
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    vec4 baseColorSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialCoverageTexture(material.AlbedoTextureIndex, uv);
    MaterialAlphaCoverage coverage = ResolveMaterialAlphaCoverage(
        material,
        baseColorSample,
        fragVertexColor.a);
    if (!MaterialCoverageSurvivesForward(coverage))
        discard;

    float opacity = clamp(coverage.Alpha, 0.0, 1.0);
    vec3 tint = clamp(
        material.Albedo.rgb * baseColorSample.rgb * fragVertexColor.rgb,
        vec3(0.0),
        vec3(1.0));
    outColor = vec4(tint * opacity, opacity);
}
