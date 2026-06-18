#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 6) flat in uint fragGeometryMode;

bool IsInsideLeafCard(vec2 uv)
{
    float y = clamp(uv.y, 0.0, 1.0);
    vec2 centered = vec2(uv.x * 2.0 - 1.0, y * 2.0 - 1.0);
    float halfWidth = mix(0.10, 0.62, sin(y * 3.14159265359));
    return abs(centered.x) <= halfWidth && abs(centered.y) <= 0.98;
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    if (fragGeometryMode == 0u)
    {
        float taper = mix(0.35, 0.08, clamp(fragTexCoord.y, 0.0, 1.0));
        if (abs(fragTexCoord.x - 0.5) > taper)
            discard;
        return;
    }

    if (fragGeometryMode == 2u && !IsInsideLeafCard(fragTexCoord))
        discard;

    if (material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX)
    {
        float alpha = texture(BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)], fragTexCoord).a;
        if (fragGeometryMode == 2u && alpha < 0.05)
            discard;
        if (material.NormalScaleBias.y >= 0.5 && alpha < material.NormalScaleBias.z)
            discard;
    }
}
