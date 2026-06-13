#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 2) in vec2 fragTexCoord2;

vec4 SampleMaterialTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
}

vec2 SelectUv(float texCoordSet)
{
    return int(round(texCoordSet)) == 1 ? fragTexCoord2 : fragTexCoord;
}

vec2 ApplyTextureTransform(vec2 uv, vec4 offsetScale, float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec2 uv = ApplyTextureTransform(
        SelectUv(material.TextureTexCoordSets.x),
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    float alpha = material.Albedo.a * SampleMaterialTexture(material.AlbedoTextureIndex, uv).a;
    float alphaCutoff = clamp(material.NormalScaleBias.z, 0.0, 1.0);

    if (alpha <= alphaCutoff)
        discard;
}
