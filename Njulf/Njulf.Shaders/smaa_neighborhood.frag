#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "anti_aliasing_push.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

vec3 EncodeOutput(vec3 color)
{
    color = clamp(color, vec3(0.0), vec3(1.0));
    if (pc.OutputToSrgb != 0u)
    {
        bvec3 cutoff = lessThanEqual(color, vec3(0.0031308));
        vec3 lower = color * 12.92;
        vec3 higher = 1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055;
        color = mix(higher, lower, cutoff);
    }
    return color;
}

vec4 SampleBlend(vec2 uv)
{
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.SmaaBlendWeightsTextureIndex))],
        uv,
        0.0);
}

vec3 SampleColor(vec2 uv)
{
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
        uv,
        0.0).rgb;
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 center = SampleColor(inUv);
    if (pc.DebugView == 1u)
    {
        outColor = vec4(EncodeOutput(center), 1.0);
        return;
    }

    vec4 weights;
    weights.x = SampleBlend(inUv + vec2(px.x, 0.0)).a;
    weights.y = SampleBlend(inUv + vec2(0.0, px.y)).g;
    weights.wz = SampleBlend(inUv).xz;

    vec3 result = center;
    if (dot(weights, vec4(1.0)) >= 0.00001)
    {
        bool horizontal = max(weights.x, weights.z) > max(weights.y, weights.w);
        vec4 blendingOffset = horizontal
            ? vec4(weights.x, 0.0, weights.z, 0.0)
            : vec4(0.0, weights.y, 0.0, weights.w);
        vec2 blendingWeight = horizontal ? weights.xz : weights.yw;
        blendingWeight /= max(dot(blendingWeight, vec2(1.0)), 0.00001);
        vec4 blendingCoordinate = inUv.xyxy +
            blendingOffset * vec4(px, -px);
        result = blendingWeight.x * SampleColor(blendingCoordinate.xy) +
            blendingWeight.y * SampleColor(blendingCoordinate.zw);
    }

    outColor = vec4(EncodeOutput(result), 1.0);
}
