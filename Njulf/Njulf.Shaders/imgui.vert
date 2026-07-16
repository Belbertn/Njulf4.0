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
    // ImGui and this pass both use a top-left framebuffer origin. With the
    // pass's positive-height Vulkan viewport, NDC -1 maps to the framebuffer
    // top, so flipping Y here would vertically mirror the complete overlay.
    gl_Position = vec4(position, 0.0, 1.0);
    outUv = inUv;
    outColor = inColor;
}
