#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "anti_aliasing_push.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec2 outEdges;

float ColorDelta(vec3 a, vec3 b)
{
    vec3 delta = abs(a - b);
    return max(delta.r, max(delta.g, delta.b));
}

vec3 SampleColor(vec2 uv)
{
    // The shared LDR target stays linear for post effects and blending.
    // SMAA color thresholds are defined in perceptual (sRGB) color.
    vec3 color = clamp(textureLod(
        BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
        uv,
        0.0).rgb, vec3(0.0), vec3(1.0));
    return mix(1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055,
        color * 12.92, lessThanEqual(color, vec3(0.0031308)));
}

float SampleDepth(vec2 uv)
{
    return textureLod(
        BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)],
        uv,
        0.0).r;
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 center = SampleColor(inUv);
    vec3 left = SampleColor(inUv - vec2(px.x, 0.0));
    vec3 top = SampleColor(inUv - vec2(0.0, px.y));

    vec2 threshold = vec2(max(pc.SmaaThreshold, 0.0001));
    vec2 predicationEdges = vec2(0.0);
    if (pc.SmaaPredicationEnabled != 0u)
    {
        float centerDepth = SampleDepth(inUv);
        vec2 predicationDelta = abs(vec2(
            centerDepth - SampleDepth(inUv - vec2(px.x, 0.0)),
            centerDepth - SampleDepth(inUv - vec2(0.0, px.y))));
        predicationEdges = step(vec2(0.01), predicationDelta);
        threshold *= 2.0 * (vec2(1.0) - 0.4 * predicationEdges);
    }

    vec2 delta = vec2(
        ColorDelta(center, left),
        ColorDelta(center, top));
    vec2 edges = step(threshold, delta);
    if (dot(edges, vec2(1.0)) == 0.0)
        discard;

    vec3 right = SampleColor(inUv + vec2(px.x, 0.0));
    vec3 bottom = SampleColor(inUv + vec2(0.0, px.y));
    vec2 maxDelta = max(delta, vec2(
        ColorDelta(center, right),
        ColorDelta(center, bottom)));

    vec3 leftLeft = SampleColor(inUv - vec2(2.0 * px.x, 0.0));
    vec3 topTop = SampleColor(inUv - vec2(0.0, 2.0 * px.y));
    maxDelta = max(maxDelta, vec2(
        ColorDelta(center, leftLeft),
        ColorDelta(center, topTop)));

    float finalDelta = max(maxDelta.x, maxDelta.y);
    edges *= step(vec2(finalDelta), 2.0 * delta);
    outEdges = edges;
}
