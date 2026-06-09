#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) in vec4 fragWorldTangent;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

const float PI = 3.14159265359;

vec4 SampleMaterialTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
}

vec3 ResolveNormal(GPUMaterialData material, vec3 interpolatedNormal, vec4 interpolatedTangent, vec2 uv)
{
    vec3 n = normalize(interpolatedNormal);
    vec3 t = normalize(interpolatedTangent.xyz - n * dot(n, interpolatedTangent.xyz));
    vec3 b = normalize(cross(n, t) * interpolatedTangent.w);

    vec3 tangentNormal = SampleMaterialTexture(material.NormalTextureIndex, uv).xyz * 2.0 - 1.0;
    tangentNormal.xy *= material.NormalScaleBias.x;
    tangentNormal = normalize(tangentNormal);

    mat3 tbn = mat3(t, b, n);
    return normalize(tbn * tangentNormal);
}

float DistributionGGX(float nDotH, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSq = alpha * alpha;
    float denom = nDotH * nDotH * (alphaSq - 1.0) + 1.0;
    return alphaSq / max(PI * denom * denom, 0.000001);
}

float GeometrySchlickGGX(float nDotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) * 0.125;
    return nDotV / max(nDotV * (1.0 - k) + k, 0.000001);
}

float GeometrySmith(float nDotV, float nDotL, float roughness)
{
    return GeometrySchlickGGX(nDotV, roughness) * GeometrySchlickGGX(nDotL, roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance)
{
    vec3 halfVector = normalize(viewDirection + lightDirection);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotH = max(dot(normal, halfVector), 0.0);
    float hDotV = max(dot(halfVector, viewDirection), 0.0);

    if (nDotL <= 0.0 || nDotV <= 0.0)
        return vec3(0.0);

    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = FresnelSchlick(hDotV, f0);
    float distribution = DistributionGGX(nDotH, roughness);
    float geometry = GeometrySmith(nDotV, nDotL, roughness);

    vec3 specular = (distribution * geometry * fresnel) / max(4.0 * nDotV * nDotL, 0.000001);
    vec3 diffuseWeight = (vec3(1.0) - fresnel) * (1.0 - metallic);
    vec3 diffuse = diffuseWeight * albedo / PI;

    return (diffuse + specular) * radiance * nDotL;
}

void main()
{
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec2 uv = fragTexCoord * material.TexCoordOffsetScale.zw + material.TexCoordOffsetScale.xy;

    vec2 safeScreenSize = max(pc.Push.ScreenDimensions, vec2(1.0));
    uvec2 pixel = uvec2(clamp(gl_FragCoord.xy, vec2(0.0), safeScreenSize - vec2(1.0)));
    uvec2 tile = pixel / uvec2(16u, 16u);
    uint tileCountX = uint(ceil(safeScreenSize.x / 16.0));
    uint tileIndex = tile.y * tileCountX + tile.x;
    GPUTiledLightHeader tileHeader = ReadTiledLightHeader(tileIndex);

    vec3 normal = ResolveNormal(material, fragNormal, fragWorldTangent, uv);
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    vec4 albedoSample = SampleMaterialTexture(material.AlbedoTextureIndex, uv);
    // glTF metallic-roughness contract: G = roughness, B = metallic.
    // R is occlusion only when the material upload marks this as a shared ORM texture.
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(material.MetallicRoughnessTextureIndex, uv);
    vec4 emissiveSample = SampleMaterialTexture(material.EmissiveTextureIndex, uv);

    float roughness = clamp(material.MetallicRoughnessAO.y * armSample.g, 0.04, 1.0);
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    float sampledOcclusion = material.MetallicRoughnessAO.w > 0.5 ? armSample.r : 1.0;
    float ambientOcclusion = clamp(material.MetallicRoughnessAO.z * sampledOcclusion, 0.0, 1.0);
    vec3 albedo = max(material.Albedo.rgb * albedoSample.rgb, vec3(0.0));
    vec3 emissive = max(material.Emissive.rgb * emissiveSample.rgb, vec3(0.0));

    vec3 ambient = albedo * 0.08 * ambientOcclusion;
    vec3 directLighting = vec3(0.0);

    for (uint i = 0u; i < tileHeader.LightCount; i++)
    {
        uint lightIndex = ReadTiledLightIndex(tileHeader.LightOffset + i);
        GPULight light = ReadLight(lightIndex);

        vec3 lightDirection;
        float attenuation = 1.0;

        if (light.Type == 1)
        {
            lightDirection = normalize(-light.Direction);
        }
        else
        {
            vec3 toLight = light.Position - fragWorldPosition;
            float distanceToLight = length(toLight);
            if (distanceToLight >= light.Range || light.Range <= 0.0)
                continue;

            lightDirection = toLight / max(distanceToLight, 0.0001);
            float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
            attenuation = rangeFactor * rangeFactor;

            if (light.Type == 2)
            {
                float coneCos = cos(light.SpotAngle);
                float spotCos = dot(normalize(light.Direction), -lightDirection);
                float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
                attenuation *= spotFactor;
            }
        }

        vec3 radiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
        directLighting += EvaluatePbrLight(
            albedo,
            metallic,
            roughness,
            normal,
            viewDirection,
            lightDirection,
            radiance);
    }

    vec3 color = ambient + directLighting + emissive;

    outColor = vec4(color, material.Albedo.a * albedoSample.a);
}
