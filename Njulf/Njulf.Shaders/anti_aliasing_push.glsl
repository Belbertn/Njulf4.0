#ifndef NJULF_ANTI_ALIASING_PUSH_GLSL
#define NJULF_ANTI_ALIASING_PUSH_GLSL

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
    uint SmaaQuality;
    uint SmaaDiagonalEnabled;
    uint SmaaCornerEnabled;
    float TaaFeedbackMin;
    float TaaFeedbackMax;
    float TaaVelocityRejectionScale;
    uint TaaHistoryValid;
    uint TaaJitterPadding;
    vec2 TaaCurrentJitterUv;
    vec2 TaaPreviousJitterUv;
} pc;

#endif // NJULF_ANTI_ALIASING_PUSH_GLSL
