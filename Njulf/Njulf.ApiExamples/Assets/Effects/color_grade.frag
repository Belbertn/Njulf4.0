#version 460
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0, binding=0) uniform sampler2D SourceColor;
layout(push_constant) uniform Parameters {
    layout(offset=0) float Saturation;
    layout(offset=16) float Contrast;
    layout(offset=32) vec3 Tint;
} p;
void main() {
    vec4 source = texture(SourceColor, uv);
    vec3 rgb = source.rgb * p.Tint;
    float luma = dot(rgb, vec3(0.2126, 0.7152, 0.0722));
    rgb = mix(vec3(luma), rgb, p.Saturation);
    color = vec4(clamp((rgb - 0.5) * p.Contrast + 0.5, 0.0, 1.0), source.a);
}
