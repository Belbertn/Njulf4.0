#version 460
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0, binding=0) uniform sampler2D SourceColor;
layout(set=0, binding=1) uniform sampler2D ExtraColor;
layout(push_constant) uniform Parameters {
    layout(offset=0) float Gain;
    layout(offset=16) vec3 Bias;
} p;
void main() {
    vec4 source = texture(SourceColor, uv);
    color = vec4(source.rgb * p.Gain + p.Bias + texelFetch(ExtraColor, ivec2(0), 0).rgb, source.a);
}
