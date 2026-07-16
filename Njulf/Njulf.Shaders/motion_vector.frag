#version 460

layout(location = 0) noperspective in vec2 inCurrentUv;
layout(location = 1) noperspective in vec2 inPreviousUv;
layout(location = 0) out vec2 outVelocity;

void main()
{
    outVelocity = clamp(inCurrentUv - inPreviousUv, vec2(-1.0), vec2(1.0));
}
