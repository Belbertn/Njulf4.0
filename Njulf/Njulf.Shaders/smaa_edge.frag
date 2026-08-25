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
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
        uv,
        0.0).rgb;
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 center = SampleColor(inUv);
    vec3 left = SampleColor(inUv - vec2(px.x, 0.0));
    vec3 top = SampleColor(inUv - vec2(0.0, px.y));

    vec2 delta = vec2(
        ColorDelta(center, left),
        ColorDelta(center, top));
    vec2 edges = step(vec2(max(pc.SmaaThreshold, 0.0001)), delta);
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
        ColorDelta(left, leftLeft),
        ColorDelta(top, topTop)));

    float finalDelta = max(maxDelta.x, maxDelta.y);
    edges *= step(vec2(finalDelta), 2.0 * delta);
    outEdges = edges;
}
