#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outHistory;

layout(push_constant) uniform AntiAliasingPushBlock
{
    vec2 SourceDimensions;
    vec2 InvSourceDimensions;
    uint InputTextureIndex;
    uint SmaaEdgesTextureIndex;
    uint SmaaBlendWeightsTextureIndex;
    uint SmaaAreaTextureIndex;
    uint SmaaSearchTextureIndex;
    float FxaaContrastThreshold;
    float FxaaRelativeThreshold;
    float FxaaSubpixelBlending;
    float SmaaThreshold;
    uint SmaaMaxSearchSteps;
    uint SmaaMaxSearchStepsDiagonal;
    float SmaaCornerRounding;
    uint DebugView;
    uint OutputToSrgb;
    uint SmaaSampleCount;
    uint SmaaMode;
    float TaaFeedbackMin;
    float TaaFeedbackMax;
    float TaaVelocityRejectionScale;
    uint TaaHistoryValid;
} pc;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
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

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 current = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv).rgb;
    vec3 history = texture(BindlessTextures[nonuniformEXT(TAA_HISTORY_TEXTURE_INDEX)], inUv).rgb;

    vec3 minColor = current;
    vec3 maxColor = current;
    vec3 localSum = vec3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            vec3 sampleColor = texture(
                BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))],
                inUv + vec2(float(x), float(y)) * px).rgb;
            minColor = min(minColor, sampleColor);
            maxColor = max(maxColor, sampleColor);
            localSum += sampleColor;
        }
    }

    history = clamp(history, minColor, maxColor);
    float contrast = max(maxColor.r - minColor.r, max(maxColor.g - minColor.g, maxColor.b - minColor.b));
    float feedback = mix(pc.TaaFeedbackMax, pc.TaaFeedbackMin, smoothstep(0.02, 0.25, contrast));
    float historyDelta = abs(Luma(history) - Luma(current));
    feedback = mix(feedback, pc.TaaFeedbackMin, smoothstep(0.02, 0.16, historyDelta));
    vec3 resolved = pc.TaaHistoryValid != 0u
        ? mix(current, history, clamp(feedback, 0.0, 0.99))
        : current;
    vec3 localAverage = localSum * (1.0 / 9.0);
    resolved = clamp(resolved + (current - localAverage) * 0.16, minColor, maxColor);

    if (pc.DebugView == 7u)
    {
        vec3 debugHistory = pc.TaaHistoryValid != 0u ? history : current;
        outColor = vec4(EncodeOutput(debugHistory), 1.0);
        outHistory = vec4(current, 1.0);
        return;
    }

    if (pc.DebugView == 6u)
    {
        bool checker = (fract(gl_FragCoord.x * 0.125) < 0.5) == (fract(gl_FragCoord.y * 0.125) < 0.5);
        float pattern = checker ? 0.35 : 0.75;
        outColor = vec4(vec3(pattern), 1.0);
        outHistory = vec4(resolved, 1.0);
        return;
    }

    outColor = vec4(EncodeOutput(resolved), 1.0);
    outHistory = vec4(resolved, 1.0);
}
