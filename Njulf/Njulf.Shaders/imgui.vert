#version 460

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 outUv;
layout(location = 1) out vec4 outColor;

layout(push_constant) uniform ImGuiPushConstants
{
    vec2 displayPosition;
    vec2 displaySize;
    uint textureIndex;
} pc;

void main()
{
    vec2 position = (inPosition - pc.displayPosition) / pc.displaySize * 2.0 - 1.0;
    gl_Position = vec4(position.x, -position.y, 0.0, 1.0);
    outUv = inUv;
    outColor = inColor;
}
