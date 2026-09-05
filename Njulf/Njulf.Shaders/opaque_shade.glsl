#include "opaque_visibility_work.glsl"

layout(local_size_x = 64) in;

void VisibilityReconstruct(uvec4 job, uint lane)
{
    GPUMeshletDrawCommand draw = ReadMeshletDrawCommandFromBase(
        job.y & 0xffffu, pc.Push.CurrentFrameIndex, job.x);
    GPUMeshlet meshlet = ReadMeshlet(draw.MeshletIndex, pc.Push.CurrentFrameIndex);
    GPUObjectData objectData = ReadInstanceData(pc.Push.CurrentFrameIndex, draw.InstanceId);
    uint instanceBuffer = uint(INSTANCE_BUFFER_BASE_INDEX) + pc.Push.CurrentFrameIndex;
    uint objectOffset = draw.InstanceId * uint(SIZEOF_GPU_OBJECT_DATA / 4);
    uint triangle = (job.y >> 16u) & 127u;
    vec4 clip[3];
    vec3 positions[3];
    vec3 normals[3];
    vec4 tangents[3];
    GPUVertex vertices[3];
    float handedness = ReadRowMajorLinearDeterminant(instanceBuffer, objectOffset) < 0.0 ? -1.0 : 1.0;
    for (uint i = 0u; i < 3u; ++i)
    {
        uint vertexSlot = ReadMeshletLocalTriangleIndex(meshlet, triangle * 3u + i);
        uint vertexIndex = ReadMeshletLocalVertexIndex(meshlet, vertexSlot);
        vertices[i] = FetchRenderableVertex(meshlet, vertexIndex, objectData, pc.Push.CurrentFrameIndex);
        vec4 world = TransformRowMajorPoint(vertices[i].Position, instanceBuffer, objectOffset);
        positions[i] = world.xyz;
        normals[i] = normalize(TransformRowMajorVector(vertices[i].Normal, instanceBuffer, objectOffset + 16u));
        tangents[i] = vec4(normalize(TransformRowMajorVector(vertices[i].Tangent.xyz, instanceBuffer, objectOffset)),
            vertices[i].Tangent.w * handedness);
        clip[i] = MulRowMajor(world, pc.Push.ViewProjectionMatrix);
    }
    uint width = uint(pc.Push.ScreenDimensions.x);
    uvec2 origin = uvec2(job.z % width, job.z / width);
    vec2 pixel = vec2(origin + uvec2(lane & 1u, lane >> 1u)) + 0.5;
    vec2 ndc = pixel / pc.Push.ScreenDimensions * 2.0 - 1.0;
    // Homogeneous barycentrics also handle triangles crossing the near plane.
    vec3 rowX = vec3(clip[0].x, clip[1].x, clip[2].x) - ndc.x * vec3(clip[0].w, clip[1].w, clip[2].w);
    vec3 rowY = vec3(clip[0].y, clip[1].y, clip[2].y) - ndc.y * vec3(clip[0].w, clip[1].w, clip[2].w);
    vec3 weights = cross(rowX, rowY);
    weights /= dot(weights, vec3(1.0));
    float reciprocalW = 1.0 / dot(weights, vec3(clip[0].w, clip[1].w, clip[2].w));
    float depth = dot(weights, vec3(clip[0].z, clip[1].z, clip[2].z)) * reciprocalW;
    fragNormal = normals[0] * weights.x + normals[1] * weights.y + normals[2] * weights.z;
    fragWorldPosition = positions[0] * weights.x + positions[1] * weights.y + positions[2] * weights.z;
    fragTexCoord = vertices[0].TexCoord * weights.x + vertices[1].TexCoord * weights.y + vertices[2].TexCoord * weights.z;
#if FORWARD_SIMPLE_VERTEX_INPUT
    fragWorldTangent = vec4(1.0, 0.0, 0.0, 1.0);
    fragTexCoord2 = vec2(0.0);
    fragVertexColor = vec4(1.0);
#else
    fragWorldTangent = tangents[0] * weights.x + tangents[1] * weights.y + tangents[2] * weights.z;
    fragTexCoord2 = vertices[0].TexCoord2 * weights.x + vertices[1].TexCoord2 * weights.y + vertices[2].TexCoord2 * weights.z;
    fragVertexColor = vertices[0].Color * weights.x + vertices[1].Color * weights.y + vertices[2].Color * weights.z;
#endif
    fragMaterialIndex = draw.MaterialIndex;
    fragObjectIndex = draw.InstanceId;
    fragMeshletIndex = draw.MeshletIndex;
    VisibilityFragCoord = vec4(pixel, depth, reciprocalW);
    VisibilityFrontFacing = (job.y & 0x80000000u) != 0u;
    VisibilityCovered = (job.w & (1u << lane)) != 0u;
    VisibilityWorldDx = dFdx(fragWorldPosition);
    VisibilityWorldDy = dFdy(fragWorldPosition);
    VisibilityDepthGradient = abs(dFdx(depth)) + abs(dFdy(depth));
}

void main()
{
    uint thread = gl_SubgroupID * gl_SubgroupSize + gl_SubgroupInvocationID;
    uint jobNumber = (gl_WorkGroupID.y * gl_NumWorkGroups.x + gl_WorkGroupID.x) * 16u + thread / 4u;
    if (jobNumber >= VisibilityControl[VisibilityCountWord + NJULF_VISIBILITY_FAMILY] || VisibilityControl[1] != 0u)
        return;
    uint index = VisibilityIndices[VisibilityControl[VisibilityOffsetWord + NJULF_VISIBILITY_FAMILY] + jobNumber];
    VisibilityReconstruct(VisibilityJobs[index], gl_SubgroupInvocationID & 3u);
    ShadeForwardSurface();
    if (!VisibilityCovered)
        return;
    ivec2 pixel = ivec2(VisibilityFragCoord.xy);
    imageStore(OpaqueColor, pixel, outColor);
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    imageStore(OpaqueHybridReceiver, pixel, outHybridReflectionReceiverPayload);
#if !NJULF_HYBRID_REFLECTION_SPARSE_LOBE_OUTPUT
    imageStore(OpaqueHybridLobe, pixel, uvec4(outHybridReflectionLobeExtension, 0u, 0u));
#endif
#endif
}
