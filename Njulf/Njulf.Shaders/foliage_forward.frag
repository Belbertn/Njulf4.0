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
layout(location = 8) flat in vec4 fragColorVariation;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outSsgiTraceSource;

layout(push_constant) uniform FoliageDrawPushConstantBlock
{
    GPUFoliageDrawPushConstants Push;
} pc;

void WriteFoliageForwardColor(vec4 color)
{
    outColor = color;
}

void WriteFoliageSsgiTraceSource(vec4 color)
{
    outSsgiTraceSource = color;
}

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

float Hash01(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return float(value & 0x00ffffffu) / float(0x01000000u);
}

float StableDither(vec2 pixel, uint stableId)
{
    uvec2 p = uvec2(pixel);
    return Hash01(stableId ^ (p.x * 1973u) ^ (p.y * 9277u));
}

float ComputeLodCoverage(uint lodBand)
{
    if (lodBand == 0u)
        return 1.0;
    if (lodBand == 1u)
        return 0.88;
    return 0.72;
}

vec3 SafeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 0.000001)
        return fallback;
    return value * inversesqrt(lengthSquared);
}

vec3 ComputeBentNormal(vec3 rawNormal, vec3 viewDirection, GPUFoliageCluster cluster, GPUFoliagePrototype prototype)
{
    vec3 normal = SafeNormalize(rawNormal, vec3(0.0, 1.0, 0.0));
    float normalBend = clamp(prototype.LightingParams.z, 0.0, 1.0);
    vec3 clusterVector = fragWorldPosition - cluster.WorldCenterRadius.xyz;
    vec3 clumpNormal = SafeNormalize(
        vec3(clusterVector.x, max(cluster.WorldCenterRadius.w * 0.35, 0.1), clusterVector.z),
        vec3(0.0, 1.0, 0.0));

    float bendStrength = fragGeometryMode == 0u ? normalBend : normalBend * 0.55;
    normal = SafeNormalize(mix(normal, clumpNormal, bendStrength), vec3(0.0, 1.0, 0.0));
    if (dot(normal, viewDirection) < 0.0)
        normal = -normal;
    return normal;
}

vec3 ApplyFoliageLighting(vec3 baseColor, vec3 normal, vec3 viewDirection, GPUFoliagePrototype prototype)
{
    vec3 lightDirection = normalize(vec3(-0.35, 0.85, 0.25));
    float wrap = mix(0.08, 0.72, clamp(prototype.LightingParams.x, 0.0, 1.0));
    float backlightStrength = clamp(prototype.LightingParams.y, 0.0, 1.0);
    float frontDiffuse = clamp((dot(normal, lightDirection) + wrap) / (1.0 + wrap), 0.0, 1.0);
    float backDiffuse = clamp((dot(-normal, lightDirection) + wrap) / (1.0 + wrap), 0.0, 1.0);
    float viewBacklight = pow(clamp(dot(viewDirection, -lightDirection) * 0.5 + 0.5, 0.0, 1.0), 2.0);
    float diffuse = frontDiffuse + backDiffuse * backlightStrength * viewBacklight * 0.65;
    float heightShade = mix(0.74, 1.08, clamp(fragTexCoord.y, 0.0, 1.0));
    return baseColor * (0.18 + diffuse * 0.92) * heightShade;
}

void main()
{
    WriteFoliageSsgiTraceSource(vec4(0.0, 0.0, 0.0, 1.0));
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
        WriteFoliageForwardColor(vec4(DebugColor(debugId), 1.0));
        return;
    }

    if (pc.Push.DebugView == 2u)
    {
        vec3 lodColor = fragLodBand == 0u
            ? vec3(0.2, 0.95, 0.25)
            : (fragLodBand == 1u ? vec3(0.95, 0.85, 0.2) : vec3(0.95, 0.28, 0.18));
        WriteFoliageForwardColor(vec4(lodColor, 1.0));
        return;
    }

    GPUFoliageCluster cluster = ReadFoliageCluster(fragClusterIndex);
    GPUFoliagePatch foliagePatch = ReadFoliagePatch(cluster.PatchIndex);
    GPUFoliagePrototype prototype = ReadFoliagePrototype(foliagePatch.PrototypeIndex);
    float lodCoverage = ComputeLodCoverage(fragLodBand);
    if (StableDither(gl_FragCoord.xy, fragClusterIndex ^ (fragLodBand * 0x9e3779b9u)) > lodCoverage)
        discard;

    vec3 baseColor = material.Albedo.rgb * sampledAlbedo.rgb;
    baseColor *= mix(vec3(1.0), max(fragColorVariation.rgb, vec3(0.0)), clamp(fragColorVariation.a, 0.0, 1.0));
    if (length(baseColor) <= 0.001)
        baseColor = vec3(0.18, 0.48, 0.12);

    vec3 viewDirection = SafeNormalize(pc.Push.CameraPositionTime.xyz - fragWorldPosition, vec3(0.0, 0.0, 1.0));
    vec3 normal = ComputeBentNormal(fragNormal, viewDirection, cluster, prototype);
    vec3 foliageLighting = ApplyFoliageLighting(baseColor, normal, viewDirection, prototype);
    WriteFoliageForwardColor(vec4(foliageLighting, 1.0));
    WriteFoliageSsgiTraceSource(vec4(clamp(foliageLighting, vec3(0.0), vec3(64.0)), 1.0));
}
