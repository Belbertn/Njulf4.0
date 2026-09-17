#version 460
#extension GL_GOOGLE_include_directive : require
#define SMAA_GLSL_4
#define SMAA_PRESET_HIGH
#define SMAA_RT_METRICS vec4(1.0 / 128.0, 1.0 / 96.0, 128.0, 96.0)
#include "SMAA.hlsl"
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0,binding=0) uniform sampler2D SourceColor;
layout(set=0,binding=1) uniform sampler2D Blend;
void main() { vec4 offset; SMAANeighborhoodBlendingVS(uv, offset); color = SMAANeighborhoodBlendingPS(uv, offset, SourceColor, Blend); }
