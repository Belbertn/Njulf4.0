#version 460
layout(location=0) in vec2 uv;
layout(location=0) out vec4 color;
layout(set=0, binding=0) uniform sampler2D source;
layout(set=0, binding=1, std430) readonly buffer Data { uint value; } data;
void main() {
    // Preserve the scene outside a small corner panel.
    if (uv.x > 0.3 || uv.y > 0.3) discard;
    color = vec4(texture(source, uv / 0.3).rgb * 40.0 * float(data.value), 1);
}
