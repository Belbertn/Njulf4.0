#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;

vec4 SampleMaterialTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec2 uv = fragTexCoord * material.TexCoordOffsetScale.zw + material.TexCoordOffsetScale.xy;
    float alpha = material.Albedo.a * SampleMaterialTexture(material.AlbedoTextureIndex, uv).a;
    float alphaCutoff = clamp(material.NormalScaleBias.z, 0.0, 1.0);

    if (alpha <= alphaCutoff)
        discard;
}
