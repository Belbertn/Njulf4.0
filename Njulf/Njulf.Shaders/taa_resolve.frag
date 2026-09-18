#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "anti_aliasing_push.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outHistory;

vec3 RgbToYCoCg(vec3 color)
{
    return vec3(
        dot(color, vec3(0.25, 0.5, 0.25)),
        color.r * 0.5 - color.b * 0.5,
        -color.r * 0.25 + color.g * 0.5 - color.b * 0.25);
}

vec3 YCoCgToRgb(vec3 color)
{
    return vec3(
        color.x + color.y - color.z,
        color.x + color.z,
        color.x - color.y - color.z);
}

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

vec3 SampleCurrent(vec2 uv)
{
    return textureLod(
        BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
        uv,
        0.0).rgb;
}

// Evaluates the 4x4 Catmull-Rom kernel with five bilinear fetches
// (Prowl SampleHistoryCatmullRom). The negative lobes are clamped away so
// repeated resampling cannot accumulate negative color.
vec3 SampleHistoryCatmullRom(vec2 uv)
{
    vec2 historyDimensions = vec2(textureSize(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        0));
    vec2 samplePosition = uv * historyDimensions;
    vec2 texelCenter = floor(samplePosition - 0.5) + 0.5;
    vec2 fraction = samplePosition - texelCenter;

    vec2 w0 = fraction * (-0.5 + fraction * (1.0 - 0.5 * fraction));
    vec2 w1 = 1.0 + fraction * fraction * (-2.5 + 1.5 * fraction);
    vec2 w2 = fraction * (0.5 + fraction * (2.0 - 1.5 * fraction));
    vec2 w3 = fraction * fraction * (-0.5 + 0.5 * fraction);

    vec2 w12 = w1 + w2;
    vec2 offset12 = w2 / max(w12, vec2(1e-4));

    vec2 tc0 = (texelCenter - 1.0) / historyDimensions;
    vec2 tc3 = (texelCenter + 2.0) / historyDimensions;
    vec2 tc12 = (texelCenter + offset12) / historyDimensions;

    vec3 historyColor = textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        vec2(tc12.x, tc0.y),
        0.0).rgb * (w12.x * w0.y);
    historyColor += textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        vec2(tc0.x, tc12.y),
        0.0).rgb * (w0.x * w12.y);
    historyColor += textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        vec2(tc12.x, tc12.y),
        0.0).rgb * (w12.x * w12.y);
    historyColor += textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        vec2(tc3.x, tc12.y),
        0.0).rgb * (w3.x * w12.y);
    historyColor += textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        vec2(tc12.x, tc3.y),
        0.0).rgb * (w12.x * w3.y);
    return max(historyColor, vec3(0.0));
}

// Clips the history onto the neighborhood bounding box surface
// (Flax Temporal.hlsl ClipToAABB).
vec3 ClipToAabb(vec3 history, vec3 aabbMin, vec3 aabbMax)
{
    vec3 center = 0.5 * (aabbMin + aabbMax);
    vec3 extents = 0.5 * (aabbMax - aabbMin);
    vec3 direction = history - center;
    vec3 intersectionScale = abs(extents) / max(abs(direction), vec3(1e-4));
    float scale = min(min(intersectionScale.x, intersectionScale.y), intersectionScale.z);
    return center + direction * min(scale, 1.0);
}

void main()
{
    vec2 sampleUv = inUv + pc.TaaCurrentJitterUv;
    vec3 current = SampleCurrent(sampleUv);

    ivec2 sourceExtent = ivec2(pc.SourceDimensions);
    ivec2 sourceTexel = clamp(
        ivec2(sampleUv * pc.SourceDimensions),
        ivec2(0),
        sourceExtent - ivec2(1));

    // One 3x3 pass over the current frame collects the sharpen reference,
    // the YCoCg neighborhood statistics, and the closest depth. Depth is
    // reverse-Z, so the closest sample is the largest value.
    vec3 currentSum = vec3(0.0);
    vec3 neighborhoodMinimum = vec3(65504.0);
    vec3 neighborhoodMaximum = vec3(-65504.0);
    vec3 firstMoment = vec3(0.0);
    vec3 secondMoment = vec3(0.0);
    float closestDepth = -1.0;
    ivec2 closestOffset = ivec2(0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 neighborOffset = ivec2(x, y);
            ivec2 neighborTexel = clamp(
                sourceTexel + neighborOffset,
                ivec2(0),
                sourceExtent - ivec2(1));
            vec3 neighborColor = texelFetch(
                BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
                neighborTexel,
                0).rgb;
            currentSum += neighborColor;
            vec3 neighborYCoCg = RgbToYCoCg(neighborColor);
            neighborhoodMinimum = min(neighborhoodMinimum, neighborYCoCg);
            neighborhoodMaximum = max(neighborhoodMaximum, neighborYCoCg);
            firstMoment += neighborYCoCg;
            secondMoment += neighborYCoCg * neighborYCoCg;
            float neighborDepth = texelFetch(
                BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)],
                neighborTexel,
                0).r;
            if (neighborDepth > closestDepth)
            {
                closestDepth = neighborDepth;
                closestOffset = neighborOffset;
            }
        }
    }

    vec3 neighborhoodAverage = currentSum * (1.0 / 9.0);
    firstMoment *= 1.0 / 9.0;
    secondMoment *= 1.0 / 9.0;
    vec3 standardDeviation = sqrt(max(
        secondMoment - firstMoment * firstMoment,
        vec3(0.0)));

    // Sharpen the current sample against its own neighborhood (Flax).
    current += (current - neighborhoodAverage) * pc.TaaSharpness;
    current = max(current, vec3(0.0));

    // Velocity dilated over the closest-depth neighbor so silhouette pixels
    // reproject with the surface that owns their color. Motion vectors are
    // jitter-free, so this vector is purely geometric.
    vec2 velocity = texelFetch(
        BindlessTextures[nonuniformEXT(MOTION_VECTOR_TEXTURE_INDEX)],
        clamp(sourceTexel + closestOffset, ivec2(0), sourceExtent - ivec2(1)),
        0).rg;
    bool velocityFinite = !any(isnan(velocity)) && !any(isinf(velocity));
    if (!velocityFinite)
        velocity = vec2(0.0);

    vec2 historyUv = inUv - velocity;
    bool historyUvValid = all(greaterThanEqual(historyUv, vec2(0.0))) &&
        all(lessThanEqual(historyUv, vec2(1.0)));
    vec2 clampedHistoryUv = clamp(historyUv, vec2(0.0), vec2(1.0));
    vec3 historyColor = SampleHistoryCatmullRom(clampedHistoryUv);
    // The history length counter lives in alpha; Catmull-Rom's negative
    // lobes would corrupt a counter, so it is read with a plain tap.
    float historyAlpha = textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        clampedHistoryUv,
        0.0).a;

    float velocityPixels = length(velocity * pc.SourceDimensions);
    float rejectionEnd = max(0.5, pc.TaaVelocityRejectionScale);
    float motion = smoothstep(0.25, rejectionEnd, velocityPixels);

    // Variance clipping bound, tightened under motion (Prowl).
    float gamma = mix(1.25, 0.75, motion);
    vec3 aabbMin = max(neighborhoodMinimum, firstMoment - standardDeviation * gamma);
    vec3 aabbMax = min(neighborhoodMaximum, firstMoment + standardDeviation * gamma);
    vec3 clippedHistory = YCoCgToRgb(ClipToAabb(
        RgbToYCoCg(historyColor),
        aabbMin,
        aabbMax));

    // Scale-free disocclusion test: reverse-Z depth is ~near/z, so a relative
    // difference is already a view-space relative difference. There is no
    // previous depth buffer, so the current depth buffer is read at the
    // reprojected position and point-sampled, never bilinearly filtered.
    ivec2 historyDepthTexel = clamp(
        ivec2(clampedHistoryUv * pc.SourceDimensions),
        ivec2(0),
        sourceExtent - ivec2(1));
    float historyDepth = texelFetch(
        BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)],
        historyDepthTexel,
        0).r;
    float depthRelative = abs(closestDepth - historyDepth) /
        max(max(closestDepth, historyDepth), 1e-4);
    float disocclusion = smoothstep(0.02, 0.08, depthRelative);

    // History length accumulates in alpha. A rejected or freshly revealed
    // pixel ramps its feedback back up over a few frames instead of popping
    // to the raw current frame.
    bool historyValid = pc.TaaHistoryValid != 0u &&
        historyUvValid &&
        velocityFinite;
    float previousLength = historyValid ? clamp(historyAlpha, 1.0, 32.0) : 0.0;
    float currentLength = min(previousLength * (1.0 - disocclusion) + 1.0, 32.0);

    float feedback = mix(pc.TaaFeedbackMax, pc.TaaFeedbackMin, motion);
    feedback = min(feedback, 1.0 - 1.0 / currentLength);

    vec3 resolved = mix(current, clippedHistory, clamp(feedback, 0.0, 0.99));

    outHistory = vec4(resolved, currentLength);

    if (pc.DebugView == 5u)
    {
        vec2 encodedVelocity = clamp(
            velocity * pc.SourceDimensions * 0.125 + vec2(0.5),
            vec2(0.0),
            vec2(1.0));
        outColor = vec4(encodedVelocity, historyValid ? 1.0 : 0.0, 1.0);
        return;
    }

    if (pc.DebugView == 7u)
    {
        vec3 debugHistory = historyValid ? clippedHistory : current;
        outColor = vec4(EncodeOutput(debugHistory), 1.0);
        return;
    }

    if (pc.DebugView == 6u)
    {
        vec2 jitterPixels = pc.TaaCurrentJitterUv * pc.SourceDimensions;
        vec2 encodedJitter = clamp(jitterPixels + vec2(0.5), vec2(0.0), vec2(1.0));
        outColor = vec4(encodedJitter, 0.0, 1.0);
        return;
    }

    if (pc.DebugView == 8u)
    {
        // Convergence view: uniform white once a still frame has settled,
        // red where the history length resets through disocclusion.
        vec3 lengthColor = vec3(currentLength / 32.0);
        outColor = vec4(
            mix(lengthColor, vec3(disocclusion, 0.0, 0.0), disocclusion),
            1.0);
        return;
    }

    outColor = vec4(EncodeOutput(resolved), 1.0);
}
