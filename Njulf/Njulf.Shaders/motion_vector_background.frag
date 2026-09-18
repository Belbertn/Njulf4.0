#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec2 outVelocity;

layout(push_constant) uniform MotionVectorPushConstantBlock
{
    GPUMotionVectorPushConstants Push;
} pc;

vec2 ClipToUv(vec4 clip, vec2 jitterNdc)
{
    vec2 ndc = clip.xy / max(abs(clip.w), 0.000001);
    return (ndc - jitterNdc) * 0.5 + vec2(0.5);
}

void main()
{
    vec2 velocity = vec2(0.0);
    if ((pc.Push.PreviousFrameValid & 1u) != 0u)
    {
        // Reproject the far plane (reversed-Z clip depth 0) covering this
        // pixel so uncovered background carries camera motion instead of the
        // attachment clear-value zeros the TAA resolve would otherwise treat
        // as valid geometry motion. Both clip positions drop the NDC jitter
        // so the velocity is purely geometric, matching the mesh paths.
        vec4 currentClip = vec4(
            inUv * 2.0 - vec2(1.0) + pc.Push.TemporalJitterNdc.xy,
            0.0,
            1.0);
        vec4 world = MulRowMajor(
            currentClip,
            inverse(pc.Push.ViewProjectionMatrix));
        if (abs(world.w) > 0.000001)
        {
            vec4 previousClip = MulRowMajor(
                vec4(world.xyz / world.w, 1.0),
                pc.Push.PreviousViewProjectionMatrix);
            velocity = inUv - ClipToUv(previousClip, pc.Push.TemporalJitterNdc.zw);
        }
    }
    outVelocity = clamp(velocity, vec2(-1.0), vec2(1.0));
}
