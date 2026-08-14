#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "debug_ddgi_probe_shared.glsl"

layout(location = 0) in vec4 inLogicalPositionAndRadius;
layout(location = 1) in uvec4 inVolumeAndLogical;
layout(location = 2) in uvec4 inVirtualFrameAndVolumeGeneration;
layout(location = 3) in uvec4 inSchedulerResidencyAndFlags;

layout(location = 0) out vec4 outColor;

void main()
{
    DebugDdgiWriteCounterHeader();
    if (gl_InstanceIndex >= debugPc.SampledInstanceCount)
    {
        DebugDdgiEmitInvalid(gl_Position, outColor);
        return;
    }

    uint volumeIndex = inVolumeAndLogical.x;
    uvec3 logicalCoord = inVolumeAndLogical.yzw;
    uint virtualProbeIndex = inVirtualFrameAndVolumeGeneration.x;
    DebugDdgiProbeResolution probe;
    bool resolved = ResolveDebugDdgiProbe(
        volumeIndex,
        logicalCoord,
        virtualProbeIndex,
        inLogicalPositionAndRadius.xyz,
        inLogicalPositionAndRadius.w,
        inVirtualFrameAndVolumeGeneration.w,
        inSchedulerResidencyAndFlags.x,
        inSchedulerResidencyAndFlags.y,
        probe);

    vec4 color = DebugDdgiProbeColor(
        probe,
        virtualProbeIndex,
        inSchedulerResidencyAndFlags.z);
    bool filtered = debugPc.Mode == DEBUG_DDGI_MODE_NEWLY_EXPOSED &&
        (probe.stateFlags & SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED) == 0u;
    if (filtered)
        color.a = min(color.a, 0.07);
    DebugDdgiRecordResolvedMarker(probe, resolved, filtered);
    uint vertexIndex = uint(gl_VertexIndex);
    if (debugPc.Mode == DEBUG_DDGI_MODE_PROBE_RELOCATION)
    {
        if (vertexIndex < DEBUG_DDGI_SPHERE_VERTICES)
        {
            vec4 logicalColor = color;
            logicalColor.a = min(logicalColor.a, 0.22);
            DebugDdgiEmit(
                probe.logicalPosition + DebugDdgiSphereOffset(
                    vertexIndex,
                    probe.radius * 0.72),
                logicalColor,
                gl_Position,
                outColor);
            return;
        }
        if (vertexIndex < DEBUG_DDGI_SPHERE_VERTICES * 2u)
        {
            uint sphereVertex = vertexIndex - DEBUG_DDGI_SPHERE_VERTICES;
            vec3 relocationCenter = probe.logicalPosition + probe.relocation;
            DebugDdgiEmit(
                relocationCenter + DebugDdgiSphereOffset(
                    sphereVertex,
                    probe.radius),
                color,
                gl_Position,
                outColor);
            return;
        }

        vec3 endpoint = (vertexIndex & 1u) == 0u
            ? probe.logicalPosition
            : probe.logicalPosition + probe.relocation;
        DebugDdgiEmit(endpoint, color, gl_Position, outColor);
        return;
    }

    DebugDdgiEmit(
        probe.resolvedPosition + DebugDdgiSphereOffset(
            vertexIndex,
            probe.radius),
        color,
        gl_Position,
        outColor);
}
