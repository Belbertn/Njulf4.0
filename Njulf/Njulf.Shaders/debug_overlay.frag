#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform DebugOverlayPushBlock
{
    uint TileCountX;
    uint TileCountY;
    uint MaxLightsPerTile;
    uint HeaderBufferIndex;
    uint ScreenWidth;
    uint ScreenHeight;
    uint LocalLightCount;
    uint Padding0;
} pc;

vec3 OccupancyColor(float occupancy)
{
    float t = clamp(occupancy, 0.0, 1.0);
    if (t < 0.5)
        return mix(vec3(0.04, 0.18, 1.0), vec3(0.04, 0.90, 0.28), t * 2.0);
    return mix(vec3(1.0, 0.88, 0.05), vec3(1.0, 0.06, 0.02), (t - 0.5) * 2.0);
}

void main()
{
    uvec2 pixel = uvec2(gl_FragCoord.xy);
    uvec2 tile = pixel / uvec2(16u);
    if (tile.x >= pc.TileCountX || tile.y >= pc.TileCountY ||
        pixel.x >= pc.ScreenWidth || pixel.y >= pc.ScreenHeight)
    {
        discard;
    }

    uint tileIndex = tile.y * pc.TileCountX + tile.x;
    uint baseWord = tileIndex * uint(SIZEOF_GPU_TILED_LIGHT_HEADER / 4);
    uvec4 packed = ReadStorageAlignedUVec4Uniform(pc.HeaderBufferIndex, baseWord);
    uint count = packed.x;
    uint overflow = packed.z;
    bool saturated = overflow != 0u || count >= pc.MaxLightsPerTile;
    bool grid = (pixel.x & 15u) == 0u || (pixel.y & 15u) == 0u;

    if (count == 0u && !grid)
        discard;

    vec3 color = saturated
        ? vec3(1.0, 0.0, 1.0)
        : OccupancyColor(float(count) / float(max(pc.MaxLightsPerTile, 1u)));
    float alpha = saturated ? 0.82 : (count == 0u ? 0.0 : 0.62);
    if (grid)
    {
        color = mix(color, vec3(0.72), count == 0u ? 1.0 : 0.25);
        alpha = max(alpha, 0.12);
    }
    outColor = vec4(color, alpha);
}
