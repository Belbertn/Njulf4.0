#version 460
#extension GL_GOOGLE_include_directive : require
#define SMAA_GLSL_4
#define SMAA_PRESET_HIGH
#define SMAA_RT_METRICS vec4(1.0 / 128.0, 1.0 / 96.0, 128.0, 96.0)
#include "SMAA.hlsl"
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0,binding=0) uniform sampler2D Edges;
layout(set=0,binding=1) uniform sampler2D Area;
layout(set=0,binding=2) uniform sampler2D Search;
void main() {
    vec4 offsets[3]; vec2 pixel;
    SMAABlendingWeightCalculationVS(uv, pixel, offsets);
    color = SMAABlendingWeightCalculationPS(uv, pixel, offsets, Edges, Area, Search, vec4(0.0));
    // Match the RGBA8 UNORM weight attachment used by the production pipeline.
    // Custom render targets are RGBA16F, so apply that storage precision here.
    color = round(clamp(color, 0.0, 1.0) * 255.0) / 255.0;
}
