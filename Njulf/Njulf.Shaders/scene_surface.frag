#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

#ifndef FORWARD_SIMPLE_VERTEX_INPUT
#define FORWARD_SIMPLE_VERTEX_INPUT 0
#endif

#if FORWARD_SIMPLE_VERTEX_INPUT
layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) flat in uint fragMeshletIndex;
const vec4 fragWorldTangent = vec4(1.0, 0.0, 0.0, 1.0);
const vec2 fragTexCoord2 = vec2(0.0);
const vec4 fragVertexColor = vec4(1.0);
#else
layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) in vec4 fragWorldTangent;
layout(location = 6) flat in uint fragMeshletIndex;
layout(location = 7) in vec2 fragTexCoord2;
layout(location = 8) in vec4 fragVertexColor;
#endif

layout(location = 0) out vec4 outSceneNormal;
layout(location = 1) out vec4 outSceneMaterial;

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

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

bool IsIdentityTextureTransform(vec4 offsetScale, float rotationRadians)
{
    return abs(offsetScale.x) <= 0.0001 &&
           abs(offsetScale.y) <= 0.0001 &&
           abs(offsetScale.z - 1.0) <= 0.0001 &&
           abs(offsetScale.w - 1.0) <= 0.0001 &&
           abs(rotationRadians) <= 0.0001;
}

vec2 MaterialUv(float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = SelectUv(texCoordSet);
    return IsIdentityTextureTransform(offsetScale, rotationRadians)
        ? uv
        : ApplyTextureTransform(uv, offsetScale, rotationRadians);
}

vec3 ResolveNormal(GPUMaterialData material, vec3 interpolatedNormal, vec4 interpolatedTangent, vec2 uv)
{
    vec3 n = normalize(interpolatedNormal) * (gl_FrontFacing ? 1.0 : -1.0);

#if FORWARD_SIMPLE_VERTEX_INPUT
    return n;
#else
    vec3 t = normalize(interpolatedTangent.xyz - n * dot(n, interpolatedTangent.xyz));
    vec3 b = normalize(cross(n, t) * interpolatedTangent.w * (gl_FrontFacing ? 1.0 : -1.0));

    vec3 tangentNormal = SampleMaterialTexture(material.NormalTextureIndex, uv).xyz * 2.0 - 1.0;
    tangentNormal.xy *= material.NormalScaleBias.x;
    tangentNormal = normalize(tangentNormal);

    return normalize(mat3(t, b, n) * tangentNormal);
#endif
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);

    float alphaMode = material.NormalScaleBias.y;
    float alphaCutoff = material.NormalScaleBias.z;
    float outputAlpha = material.Albedo.a * albedoSample.a * fragVertexColor.a;
    if (alphaMode > 0.5 && alphaMode < 1.5 && outputAlpha <= alphaCutoff)
        discard;
    if (alphaMode > 1.5 && outputAlpha <= 0.001)
        discard;

    vec3 normal = normalize(fragNormal) * (gl_FrontFacing ? 1.0 : -1.0);
    bool useNormalTexture = material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
        material.NormalScaleBias.x > 0.001;
    if (useNormalTexture)
    {
        normal = ResolveNormal(
            material,
            fragNormal,
            fragWorldTangent,
            MaterialUv(
                material.TextureTexCoordSets.y,
                material.NormalOffsetScale,
                material.TextureRotations.y));
    }

    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.z,
                material.MetallicRoughnessOffsetScale,
                material.TextureRotations.z));
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    vec3 albedo = max(material.Albedo.rgb * albedoSample.rgb * fragVertexColor.rgb, vec3(0.0));

    outSceneNormal = vec4(normal, 1.0);
    outSceneMaterial = vec4(albedo, metallic);
}
