#version 460
#extension GL_GOOGLE_include_directive : require
#define SMAA_GLSL_4
#define SMAA_PRESET_HIGH
#define SMAA_RT_METRICS vec4(1.0 / 128.0, 1.0 / 96.0, 128.0, 96.0)
// Custom effects use DONT_CARE load: explicitly write the reference's
// zero-edge result instead of relying on a cleared attachment after discard.
#define discard return vec2(0.0)
#include "SMAA.hlsl"
#undef discard
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0,binding=0) uniform sampler2D SourceColor;
void main() { vec4 offsets[3]; SMAAEdgeDetectionVS(uv, offsets); color = vec4(SMAAColorEdgeDetectionPS(uv, offsets, SourceColor), 0.0, 0.0); }
