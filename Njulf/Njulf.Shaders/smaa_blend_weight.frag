#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "anti_aliasing_push.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outWeights;

const vec2 SmaaAreaTextureSize = vec2(160.0, 560.0);
const vec2 SmaaSearchTextureSize = vec2(66.0, 33.0);
const vec2 SmaaPackedSearchTextureSize = vec2(64.0, 16.0);

vec2 SampleEdges(vec2 uv)
{
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.SmaaEdgesTextureIndex))],
        uv,
        0.0).rg;
}

vec2 SampleEdgesOffset(vec2 uv, ivec2 offset)
{
    return SampleEdges(uv + vec2(offset) * pc.InvSourceDimensions);
}

float SearchLength(vec2 edges, float horizontalOffset)
{
    vec2 scale = SmaaSearchTextureSize * vec2(0.5, -1.0);
    vec2 bias = SmaaSearchTextureSize * vec2(horizontalOffset, 1.0);
    scale += vec2(-1.0, 1.0);
    bias += vec2(0.5, -0.5);
    scale /= SmaaPackedSearchTextureSize;
    bias /= SmaaPackedSearchTextureSize;
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.SmaaSearchTextureIndex))],
        scale * edges + bias,
        0.0).r;
}

float SearchXLeft(vec2 uv, float end)
{
    vec2 edges = vec2(0.0, 1.0);
    int maxSteps = int(clamp(pc.SmaaMaxSearchSteps, 1u, 32u));
    for (int i = 0; i < 32; i++)
    {
        if (i >= maxSteps || uv.x <= end || edges.g <= 0.8281 || edges.r != 0.0)
            break;
        edges = SampleEdges(uv);
        uv.x -= 2.0 * pc.InvSourceDimensions.x;
    }
    float offset = 3.25 - (255.0 / 127.0) * SearchLength(edges, 0.0);
    return uv.x + pc.InvSourceDimensions.x * offset;
}

float SearchXRight(vec2 uv, float end)
{
    vec2 edges = vec2(0.0, 1.0);
    int maxSteps = int(clamp(pc.SmaaMaxSearchSteps, 1u, 32u));
    for (int i = 0; i < 32; i++)
    {
        if (i >= maxSteps || uv.x >= end || edges.g <= 0.8281 || edges.r != 0.0)
            break;
        edges = SampleEdges(uv);
        uv.x += 2.0 * pc.InvSourceDimensions.x;
    }
    float offset = 3.25 - (255.0 / 127.0) * SearchLength(edges, 0.5);
    return uv.x - pc.InvSourceDimensions.x * offset;
}

float SearchYUp(vec2 uv, float end)
{
    vec2 edges = vec2(1.0, 0.0);
    int maxSteps = int(clamp(pc.SmaaMaxSearchSteps, 1u, 32u));
    for (int i = 0; i < 32; i++)
    {
        if (i >= maxSteps || uv.y <= end || edges.r <= 0.8281 || edges.g != 0.0)
            break;
        edges = SampleEdges(uv);
        uv.y -= 2.0 * pc.InvSourceDimensions.y;
    }
    float offset = 3.25 - (255.0 / 127.0) * SearchLength(edges.gr, 0.0);
    return uv.y + pc.InvSourceDimensions.y * offset;
}

float SearchYDown(vec2 uv, float end)
{
    vec2 edges = vec2(1.0, 0.0);
    int maxSteps = int(clamp(pc.SmaaMaxSearchSteps, 1u, 32u));
    for (int i = 0; i < 32; i++)
    {
        if (i >= maxSteps || uv.y >= end || edges.r <= 0.8281 || edges.g != 0.0)
            break;
        edges = SampleEdges(uv);
        uv.y += 2.0 * pc.InvSourceDimensions.y;
    }
    float offset = 3.25 - (255.0 / 127.0) * SearchLength(edges.gr, 0.5);
    return uv.y - pc.InvSourceDimensions.y * offset;
}

vec2 Area(vec2 distanceValue, float crossing1, float crossing2)
{
    vec2 texel = 16.0 * round(4.0 * vec2(crossing1, crossing2)) + distanceValue;
    vec2 uv = (texel + vec2(0.5)) / SmaaAreaTextureSize;
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.SmaaAreaTextureIndex))],
        uv,
        0.0).rg;
}

vec2 AreaDiagonal(vec2 distanceValue, vec2 crossingEdges)
{
    vec2 texel = 20.0 * crossingEdges + distanceValue;
    vec2 uv = (texel + vec2(0.5)) / SmaaAreaTextureSize;
    uv.x += 0.5;
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.SmaaAreaTextureIndex))],
        uv,
        0.0).rg;
}

vec2 DecodeDiagBilinearAccess(vec2 edges)
{
    edges.r *= abs(5.0 * edges.r - 5.0 * 0.75);
    return round(edges);
}

vec4 DecodeDiagBilinearAccess(vec4 edges)
{
    edges.rb *= abs(5.0 * edges.rb - 5.0 * 0.75);
    return round(edges);
}

vec2 SearchDiagonal1(vec2 uv, vec2 direction, out vec2 endEdges)
{
    endEdges = vec2(0.0);
    float distanceValue = -1.0;
    float continuation = 1.0;
    int maxSteps = int(clamp(pc.SmaaMaxSearchStepsDiagonal, 1u, 16u));
    for (int i = 0; i < 16; i++)
    {
        if (i >= maxSteps || distanceValue >= float(maxSteps - 1) || continuation <= 0.9)
            break;
        uv += direction * pc.InvSourceDimensions;
        distanceValue += 1.0;
        endEdges = SampleEdges(uv);
        continuation = dot(endEdges, vec2(0.5));
    }
    return vec2(distanceValue, continuation);
}

vec2 SearchDiagonal2(vec2 uv, vec2 direction, out vec2 endEdges)
{
    endEdges = vec2(0.0);
    uv.x += 0.25 * pc.InvSourceDimensions.x;
    float distanceValue = -1.0;
    float continuation = 1.0;
    int maxSteps = int(clamp(pc.SmaaMaxSearchStepsDiagonal, 1u, 16u));
    for (int i = 0; i < 16; i++)
    {
        if (i >= maxSteps || distanceValue >= float(maxSteps - 1) || continuation <= 0.9)
            break;
        uv += direction * pc.InvSourceDimensions;
        distanceValue += 1.0;
        endEdges = DecodeDiagBilinearAccess(SampleEdges(uv));
        continuation = dot(endEdges, vec2(0.5));
    }
    return vec2(distanceValue, continuation);
}

vec2 CalculateDiagonalWeights(vec2 edges)
{
    if (pc.SmaaDiagonalEnabled == 0u || pc.SmaaMaxSearchStepsDiagonal == 0u)
        return vec2(0.0);

    vec2 endEdges;
    vec2 left = vec2(0.0);
    if (edges.r > 0.0)
    {
        left = SearchDiagonal1(inUv, vec2(-1.0, 1.0), endEdges);
        left.x += float(endEdges.y > 0.9);
    }
    vec2 right = SearchDiagonal1(inUv, vec2(1.0, -1.0), endEdges);
    vec2 weights = vec2(0.0);
    if (left.x + right.x > 2.0)
    {
        vec4 coordinates = inUv.xyxy +
            vec4(-left.x + 0.25, left.x, right.x, -right.x - 0.25) *
            pc.InvSourceDimensions.xyxy;
        vec4 crossingEdges;
        crossingEdges.xy = SampleEdgesOffset(coordinates.xy, ivec2(-1, 0));
        crossingEdges.zw = SampleEdgesOffset(coordinates.zw, ivec2(1, 0));
        crossingEdges.yxwz = DecodeDiagBilinearAccess(crossingEdges);
        vec2 crossing = 2.0 * crossingEdges.xz + crossingEdges.yw;
        crossing = mix(crossing, vec2(0.0),
            step(vec2(0.9), vec2(left.y, right.y)));
        weights += AreaDiagonal(max(vec2(left.x, right.x), vec2(0.0)), crossing);
    }

    vec2 upper = SearchDiagonal2(inUv, vec2(-1.0, -1.0), endEdges);
    vec2 lower = vec2(0.0);
    if (SampleEdgesOffset(inUv, ivec2(1, 0)).r > 0.0)
    {
        lower = SearchDiagonal2(inUv, vec2(1.0, 1.0), endEdges);
        lower.x += float(endEdges.y > 0.9);
    }
    if (upper.x + lower.x > 2.0)
    {
        vec4 coordinates = inUv.xyxy +
            vec4(-upper.x, -upper.x, lower.x, lower.x) *
            pc.InvSourceDimensions.xyxy;
        vec4 crossingEdges;
        crossingEdges.x = SampleEdgesOffset(coordinates.xy, ivec2(-1, 0)).g;
        crossingEdges.y = SampleEdgesOffset(coordinates.xy, ivec2(0, -1)).r;
        crossingEdges.zw = SampleEdgesOffset(coordinates.zw, ivec2(1, 0)).gr;
        vec2 crossing = 2.0 * crossingEdges.xz + crossingEdges.yw;
        crossing = mix(crossing, vec2(0.0),
            step(vec2(0.9), vec2(upper.y, lower.y)));
        weights += AreaDiagonal(max(vec2(upper.x, lower.x), vec2(0.0)), crossing).gr;
    }
    return weights;
}

void DetectHorizontalCornerPattern(inout vec2 weights, vec4 coordinates, vec2 distances)
{
    if (pc.SmaaCornerEnabled == 0u)
        return;
    vec2 leftRight = step(distances.xy, distances.yx);
    vec2 rounding = (1.0 - clamp(pc.SmaaCornerRounding / 100.0, 0.0, 1.0)) * leftRight;
    rounding /= max(leftRight.x + leftRight.y, 1.0);
    vec2 factor = vec2(1.0);
    factor.x -= rounding.x * SampleEdgesOffset(coordinates.xy, ivec2(0, 1)).r;
    factor.x -= rounding.y * SampleEdgesOffset(coordinates.zw, ivec2(1, 1)).r;
    factor.y -= rounding.x * SampleEdgesOffset(coordinates.xy, ivec2(0, -2)).r;
    factor.y -= rounding.y * SampleEdgesOffset(coordinates.zw, ivec2(1, -2)).r;
    weights *= clamp(factor, vec2(0.0), vec2(1.0));
}

void DetectVerticalCornerPattern(inout vec2 weights, vec4 coordinates, vec2 distances)
{
    if (pc.SmaaCornerEnabled == 0u)
        return;
    vec2 leftRight = step(distances.xy, distances.yx);
    vec2 rounding = (1.0 - clamp(pc.SmaaCornerRounding / 100.0, 0.0, 1.0)) * leftRight;
    rounding /= max(leftRight.x + leftRight.y, 1.0);
    vec2 factor = vec2(1.0);
    factor.x -= rounding.x * SampleEdgesOffset(coordinates.xy, ivec2(1, 0)).g;
    factor.x -= rounding.y * SampleEdgesOffset(coordinates.zw, ivec2(1, 1)).g;
    factor.y -= rounding.x * SampleEdgesOffset(coordinates.xy, ivec2(-2, 0)).g;
    factor.y -= rounding.y * SampleEdgesOffset(coordinates.zw, ivec2(-2, 1)).g;
    weights *= clamp(factor, vec2(0.0), vec2(1.0));
}

void main()
{
    vec2 metrics = pc.InvSourceDimensions;
    vec4 offset0 = inUv.xyxy + metrics.xyxy * vec4(-0.25, -0.125, 1.25, -0.125);
    vec4 offset1 = inUv.xyxy + metrics.xyxy * vec4(-0.125, -0.25, -0.125, 1.25);
    vec4 offset2 = vec4(offset0.x, offset0.z, offset1.y, offset1.w) +
        metrics.xxyy * vec4(-2.0, 2.0, -2.0, 2.0) * float(pc.SmaaMaxSearchSteps);
    vec2 pixelCoordinate = inUv * pc.SourceDimensions;
    vec4 weights = vec4(0.0);
    vec2 edges = SampleEdges(inUv);

    if (edges.g > 0.0)
    {
        weights.rg = CalculateDiagonalWeights(edges);
        if (dot(weights.rg, vec2(1.0)) <= 0.0)
        {
            vec3 coordinates;
            coordinates.x = SearchXLeft(offset0.xy, offset2.x);
            coordinates.y = offset1.y;
            float crossing1 = SampleEdges(coordinates.xy).r;
            coordinates.z = SearchXRight(offset0.zw, offset2.y);
            vec2 distances = abs(round(pc.SourceDimensions.xx * coordinates.xz - pixelCoordinate.xx));
            float crossing2 = SampleEdgesOffset(coordinates.zy, ivec2(1, 0)).r;
            weights.rg = Area(sqrt(distances), crossing1, crossing2);
            coordinates.y = inUv.y;
            DetectHorizontalCornerPattern(
                weights.rg,
                vec4(coordinates.xy, coordinates.zy),
                distances);
        }
        else
        {
            edges.r = 0.0;
        }
    }

    if (edges.r > 0.0)
    {
        vec3 coordinates;
        coordinates.y = SearchYUp(offset1.xy, offset2.z);
        coordinates.x = offset0.x;
        float crossing1 = SampleEdges(coordinates.xy).g;
        coordinates.z = SearchYDown(offset1.zw, offset2.w);
        vec2 distances = abs(round(pc.SourceDimensions.yy * coordinates.yz - pixelCoordinate.yy));
        float crossing2 = SampleEdgesOffset(coordinates.xz, ivec2(0, 1)).g;
        weights.ba = Area(sqrt(distances), crossing1, crossing2);
        coordinates.x = inUv.x;
        DetectVerticalCornerPattern(
            weights.ba,
            vec4(coordinates.xy, coordinates.xz),
            distances);
    }

    outWeights = weights;
}
