#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 2) in vec3 fragWorldPosition;
layout(location = 3) in vec3 fragNormal;
layout(location = 4) flat in uint fragClusterIndex;
layout(location = 5) flat in uint fragLodBand;
layout(location = 6) flat in uint fragGeometryMode;
layout(location = 7) flat in uint fragDebugMeshletIndex;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform FoliageDrawPushConstantBlock
{
    GPUFoliageDrawPushConstants Push;
} pc;

vec3 DebugColor(uint value)
{
    uint hash = value * 747796405u + 2891336453u;
    hash = (hash >> ((hash >> 28u) + 4u)) ^ hash;
    hash *= 277803737u;
    hash = (hash >> 22u) ^ hash;
    return vec3(
        float(hash & 255u),
        float((hash >> 8u) & 255u),
        float((hash >> 16u) & 255u)) / 255.0;
}

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
    vec4 sampledAlbedo = vec4(1.0);
    if (material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX)
        sampledAlbedo = texture(BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)], fragTexCoord);

    if (fragGeometryMode == 0u)
    {
        float taper = mix(0.35, 0.08, clamp(fragTexCoord.y, 0.0, 1.0));
        if (abs(fragTexCoord.x - 0.5) > taper)
            discard;
    }
    else if (fragGeometryMode == 2u)
    {
        if (!IsInsideLeafCard(fragTexCoord) || sampledAlbedo.a < 0.05)
            discard;
    }
    else if (material.NormalScaleBias.y >= 0.5 && sampledAlbedo.a < material.NormalScaleBias.z)
    {
        discard;
    }

    if (pc.Push.DebugView == 1u)
    {
        uint debugId = fragGeometryMode == 1u ? fragDebugMeshletIndex : fragClusterIndex;
        outColor = vec4(DebugColor(debugId), 1.0);
        return;
    }

    if (pc.Push.DebugView == 2u)
    {
        vec3 lodColor = fragLodBand == 0u
            ? vec3(0.2, 0.95, 0.25)
            : (fragLodBand == 1u ? vec3(0.95, 0.85, 0.2) : vec3(0.95, 0.28, 0.18));
        outColor = vec4(lodColor, 1.0);
        return;
    }

    vec3 baseColor = material.Albedo.rgb * sampledAlbedo.rgb;
    if (length(baseColor) <= 0.001)
        baseColor = vec3(0.18, 0.48, 0.12);

    vec3 normal = normalize(fragNormal);
    vec3 lightDirection = normalize(vec3(-0.35, 0.85, 0.25));
    float wrap = 0.45;
    float diffuse = clamp((dot(normal, lightDirection) + wrap) / (1.0 + wrap), 0.0, 1.0);
    float heightShade = mix(0.72, 1.08, clamp(fragTexCoord.y, 0.0, 1.0));
    outColor = vec4(baseColor * (0.25 + diffuse * 0.85) * heightShade, 1.0);
}
