#version 460
#extension GL_EXT_nonuniform_qualifier : require

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(location = 0) in vec2 inUv;
layout(location = 1) in vec4 inColor;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform ImGuiPushConstants
{
    vec2 displayPosition;
    vec2 displaySize;
    uint textureIndex;
} pc;

void main()
{
    outColor = inColor * texture(textures[nonuniformEXT(pc.textureIndex)], inUv);
}
