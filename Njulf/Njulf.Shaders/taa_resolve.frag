#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "anti_aliasing_push.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outHistory;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

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

void main()
{
    vec2 sampleUv = inUv + pc.TaaCurrentJitterUv;
    vec3 current = SampleCurrent(sampleUv);

    ivec2 sourceExtent = ivec2(pc.SourceDimensions);
    ivec2 sourceTexel = clamp(
        ivec2(sampleUv * pc.SourceDimensions),
        ivec2(0),
        sourceExtent - ivec2(1));
    vec2 rawVelocity = texelFetch(
        BindlessTextures[nonuniformEXT(MOTION_VECTOR_TEXTURE_INDEX)],
        sourceTexel,
        0).rg;
    bool velocityFinite = !any(isnan(rawVelocity)) && !any(isinf(rawVelocity));
    if (!velocityFinite)
        rawVelocity = vec2(0.0);

    vec2 jitterVelocity = pc.TaaCurrentJitterUv - pc.TaaPreviousJitterUv;
    vec2 physicalVelocity = rawVelocity - jitterVelocity;
    vec2 historyUv = inUv - physicalVelocity;
    bool historyUvValid = all(greaterThanEqual(historyUv, vec2(0.0))) &&
        all(lessThanEqual(historyUv, vec2(1.0)));
    vec3 historyColor = textureLod(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        clamp(historyUv, vec2(0.0), vec2(1.0)),
        0.0).rgb;
    ivec2 historyExtent = textureSize(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        0);
    ivec2 historyTexel = clamp(
        ivec2(historyUv * vec2(historyExtent)),
        ivec2(0),
        historyExtent - ivec2(1));
    float previousDepth = texelFetch(
        BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)],
        historyTexel,
        0).a;

    vec3 neighborhoodMinimum = vec3(65504.0);
    vec3 neighborhoodMaximum = vec3(-65504.0);
    vec3 firstMoment = vec3(0.0);
    vec3 secondMoment = vec3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 neighborTexel = clamp(
                sourceTexel + ivec2(x, y),
                ivec2(0),
                sourceExtent - ivec2(1));
            vec3 sampleYCoCg = RgbToYCoCg(texelFetch(
                BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
                neighborTexel,
                0).rgb);
            neighborhoodMinimum = min(neighborhoodMinimum, sampleYCoCg);
            neighborhoodMaximum = max(neighborhoodMaximum, sampleYCoCg);
            firstMoment += sampleYCoCg;
            secondMoment += sampleYCoCg * sampleYCoCg;
        }
    }

    firstMoment *= 1.0 / 9.0;
    secondMoment *= 1.0 / 9.0;
    vec3 standardDeviation = sqrt(max(
        secondMoment - firstMoment * firstMoment,
        vec3(0.0)));
    vec3 varianceMinimum = max(
        neighborhoodMinimum,
        firstMoment - standardDeviation * 1.25);
    vec3 varianceMaximum = min(
        neighborhoodMaximum,
        firstMoment + standardDeviation * 1.25);
    vec3 historyYCoCg = RgbToYCoCg(historyColor);
    vec3 clippedHistoryYCoCg = clamp(
        historyYCoCg,
        varianceMinimum,
        varianceMaximum);
    vec3 clippedHistory = YCoCgToRgb(clippedHistoryYCoCg);

    float currentDepth = texelFetch(
        BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)],
        sourceTexel,
        0).r;
    float depthGradient = abs(dFdx(currentDepth)) + abs(dFdy(currentDepth));
    float depthTolerance = max(
        0.00002,
        max(abs(currentDepth), abs(previousDepth)) * 0.002 + depthGradient * 2.0);
    bool depthConsistent = abs(currentDepth - previousDepth) <= depthTolerance;

    float velocityPixels = length(physicalVelocity * pc.SourceDimensions);
    bool historyInsideClip =
        all(greaterThanEqual(historyYCoCg, varianceMinimum)) &&
        all(lessThanEqual(historyYCoCg, varianceMaximum));
    // Jitter can flip which surface owns a silhouette texel between frames;
    // that reads as a depth mismatch even though the scene is unchanged. A
    // static pixel whose history already fits the current neighborhood is a
    // coverage flip, not a disocclusion. Mismatch under motion stays rejected.
    bool coverageFlip = !depthConsistent &&
        historyInsideClip &&
        velocityPixels < 0.5;

    float rejectionEnd = max(0.5, pc.TaaVelocityRejectionScale);
    float motionRejection = smoothstep(0.25, rejectionEnd, velocityPixels);
    float feedback = mix(pc.TaaFeedbackMax, pc.TaaFeedbackMin, motionRejection);
    // Frame-stable operands: the current anchor carries jitter-phase noise
    // that must not collapse the feedback on high-contrast edges. Stale
    // history still gets clamped hard, so a large clip distance still drops
    // the feedback here.
    float historyDelta = abs(Luma(clippedHistory) - Luma(historyColor));
    feedback = mix(
        feedback,
        pc.TaaFeedbackMin,
        smoothstep(0.04, 0.24, historyDelta));

    bool historyValid = pc.TaaHistoryValid != 0u &&
        historyUvValid &&
        velocityFinite &&
        (depthConsistent || coverageFlip);
    vec3 resolved = historyValid
        ? mix(current, clippedHistory, clamp(feedback, 0.0, 0.99))
        : current;

    outHistory = vec4(resolved, currentDepth);

    if (pc.DebugView == 5u)
    {
        vec2 encodedVelocity = clamp(
            physicalVelocity * pc.SourceDimensions * 0.125 + vec2(0.5),
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

    outColor = vec4(EncodeOutput(resolved), 1.0);
}
