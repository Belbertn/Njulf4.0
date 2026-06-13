#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) out vec2 outUv;
layout(location = 1) out vec4 outColor;
layout(location = 2) out vec4 outParams;
layout(location = 3) flat out uint outTextureIndex;
layout(location = 4) flat out uint outBlendMode;
layout(location = 5) flat out uint outDebugId;

layout(push_constant) uniform ParticlePushBlock
{
    GPUParticlePushConstants Push;
} pc;

const vec2 QuadCorners[6] = vec2[](
    vec2(-0.5, -0.5),
    vec2( 0.5, -0.5),
    vec2( 0.5,  0.5),
    vec2(-0.5, -0.5),
    vec2( 0.5,  0.5),
    vec2(-0.5,  0.5)
);

const vec2 QuadUv[6] = vec2[](
    vec2(0.0, 1.0),
    vec2(1.0, 1.0),
    vec2(1.0, 0.0),
    vec2(0.0, 1.0),
    vec2(1.0, 0.0),
    vec2(0.0, 0.0)
);

void main()
{
    uint instanceIndex = pc.Push.InstanceOffset + uint(gl_InstanceIndex);
    GPUParticleInstance particle = ReadParticleInstance(pc.Push.CurrentFrameIndex, instanceIndex);

    vec3 center = particle.PositionSize.xyz;
    float size = particle.PositionSize.w;
    vec3 cameraRight = normalize(vec3(
        pc.Push.InverseViewMatrix[0][0],
        pc.Push.InverseViewMatrix[0][1],
        pc.Push.InverseViewMatrix[0][2]));
    vec3 cameraUp = normalize(vec3(
        pc.Push.InverseViewMatrix[1][0],
        pc.Push.InverseViewMatrix[1][1],
        pc.Push.InverseViewMatrix[1][2]));

    vec2 corner = QuadCorners[gl_VertexIndex];
    float rotation = particle.VelocityRotation.w;
    float c = cos(rotation);
    float s = sin(rotation);
    vec2 rotated = vec2(
        corner.x * c - corner.y * s,
        corner.x * s + corner.y * c);

    vec3 velocity = particle.VelocityRotation.xyz;
    if (particle.BillboardMode == 1u || particle.BillboardMode == 4u)
    {
        vec3 velocityDir = length(velocity) > 0.0001 ? normalize(velocity) : cameraUp;
        vec3 side = normalize(cross(velocityDir, normalize(pc.Push.CameraPosition - center)));
        if (length(side) <= 0.0001)
            side = cameraRight;
        cameraUp = velocityDir;
        cameraRight = side;
        if (particle.BillboardMode == 4u)
            rotated.y *= clamp(length(velocity) * 0.08, 1.0, 4.0);
    }
    else if (particle.BillboardMode == 5u)
    {
        vec3 axis = velocity;
        vec3 axisDir = length(axis) > 0.0001 ? normalize(axis) : cameraUp;
        vec3 side = normalize(cross(axisDir, normalize(pc.Push.CameraPosition - center)));
        if (length(side) <= 0.0001)
            side = cameraRight;
        cameraRight = side;
        cameraUp = axis;
        rotated = corner;
        size = max(size, 0.0001);
    }
    else if (particle.BillboardMode == 2u)
    {
        cameraRight = vec3(1.0, 0.0, 0.0);
        cameraUp = vec3(0.0, 0.0, 1.0);
    }

    vec3 worldPosition = center + (cameraRight * rotated.x + cameraUp * rotated.y) * size;
    gl_Position = MulRowMajor(vec4(worldPosition, 1.0), pc.Push.ViewProjectionMatrix);

    uint columns = max(particle.FlipbookColumns, 1u);
    uint rows = max(particle.FlipbookRows, 1u);
    uint frame = min(particle.FlipbookFrame, columns * rows - 1u);
    vec2 uvSize = vec2(1.0 / float(columns), 1.0 / float(rows));
    vec2 uvOffset = vec2(float(frame % columns), float(frame / columns)) * uvSize;

    outUv = uvOffset + QuadUv[gl_VertexIndex] * uvSize;
    outColor = particle.Color;
    outParams = particle.EmissiveLifetimeSoftClip;
    outTextureIndex = particle.TextureIndex;
    outBlendMode = particle.BlendMode;
    outDebugId = particle.DebugId;
}
