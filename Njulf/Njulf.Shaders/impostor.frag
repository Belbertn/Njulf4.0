#version 460

layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 out_color;

layout(set = 0, binding = 0) uniform sampler2D u_albedoAtlas;

layout(push_constant) uniform ImpostorConstants
{
    vec4 atlasRect;
    float alphaCutoff;
    float fade;
    uint flags;
    uint padding0;
} pc;

void main()
{
    vec2 uv = pc.atlasRect.xy + v_uv * pc.atlasRect.zw;
    vec4 albedo = texture(u_albedoAtlas, uv);
    if (albedo.a < pc.alphaCutoff)
        discard;

    out_color = vec4(albedo.rgb, albedo.a * clamp(pc.fade, 0.0, 1.0));
}
