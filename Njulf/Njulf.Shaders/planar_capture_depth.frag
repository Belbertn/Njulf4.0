#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable
#define FORWARD_OPAQUE 1

#include "common.glsl"
#include "material_coverage.glsl"
#include "automatic_planar_reflection.glsl"

// Same full mesh producer and push ABI as the capture color pass. In
// particular, world position and alpha include the same skinning, UV sets,
// material transforms and vertex color. Discards must precede depth writes.
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 7) in vec2 fragTexCoord2;
layout(location = 8) in vec4 fragVertexColor;
layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

void main()
{
    uint layer = (pc.Push.DiagnosticFlags >> 16u) & 0x1fffu;
    uint slot = layer & (AUTOMATIC_PLANAR_CAPTURE_LAYER_FLAG - 1u);
    if ((layer & AUTOMATIC_PLANAR_CAPTURE_LAYER_FLAG) != 0u && AutomaticPlanarShouldDiscardCaptureFragment(
            pc.Push.CurrentFrameIndex, slot, fragObjectIndex,
            fragWorldPosition, pc.Push.CameraPosition))
        discard;

    GPUMaterialData material = ReadForwardMaterial(fragMaterialIndex);
    if (material.NormalScaleBias.w < 0.5 && !gl_FrontFacing)
        discard;
    MaterialAlphaCoverage coverage = EvaluateMaterialAlphaCoverage(
        material, fragTexCoord, fragTexCoord2, fragVertexColor.a);
    if (!MaterialCoverageSurvivesForward(coverage))
        discard;
}
