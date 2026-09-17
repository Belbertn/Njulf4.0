#version 460
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0,binding=0) uniform sampler2D SourceColor;
layout(set=0,binding=1) uniform sampler2D Pattern;
void main() { color = vec4(texture(Pattern, uv).rgb, texture(SourceColor, uv).a); }
