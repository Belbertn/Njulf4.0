#ifndef NJULF_RAY_SCENE_ALPHA_GLSL
#define NJULF_RAY_SCENE_ALPHA_GLSL

#include "gi_material_transport.glsl"
#include "ddgi_alpha_coverage.glsl"

// Shared V2 ray-scene metadata and alpha-coverage decoder. Direct shadows and
// other first-hit consumers use this contract without depending on DDGI state,
// scheduling, probe identity, or transport history.
GPUDdgiRayQueryInstance ReadRaySceneQueryInstance(uint instanceIndex)
{
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE / 4);
    uint bufferIndex = uint(SIMPLE_DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX);
    uvec4 header0 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 0u);
    uvec4 header1 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 4u);
    uvec4 header2 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 8u);
    uvec4 header3 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 12u);
    uvec4 header4 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 16u);
    uvec4 header5 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 20u);
    GPUDdgiRayQueryInstance instance;
    instance.AbiVersion = header0.x;
    instance.GeometryClass = header0.y;
    instance.GeometryFlags = header0.z;
    instance.StableInstanceIdentity = header0.w;
    instance.VertexBufferIndex = header1.x;
    instance.VertexOffset = header1.y;
    instance.VertexStride = header1.z;
    instance.VertexFormat = header1.w;
    instance.PositionOffset = header2.x;
    instance.NormalOffset = header2.y;
    instance.TangentOffset = header2.z;
    instance.TexCoord0Offset = header2.w;
    instance.TexCoord1Offset = header3.x;
    instance.ColorOffset = header3.y;
    instance.IndexBufferIndex = header3.z;
    instance.IndexOffset = header3.w;
    instance.IndexType = header4.x;
    instance.MaterialIndex = header4.y;
    instance.MaterialRevision = header4.z;
    instance.PackedAlpha = header4.w;
    instance.PackedDecalLayerAndOrder = header5.x;
    instance.DecalDepthTolerance = uintBitsToFloat(header5.y);
    instance.DecalDepthBias = uintBitsToFloat(header5.z);
    instance.RepresentationGeneration = header5.w;
    instance.WorldMatrixInverseTranspose = ReadStorageAlignedMat4Uniform(
        bufferIndex, baseWord + 24u);
    return instance;
}

bool RaySceneQueryInstanceIsValid(GPUDdgiRayQueryInstance instance)
{
    return instance.AbiVersion == DDGI_RAY_QUERY_INSTANCE_ABI_V2 &&
        instance.GeometryClass != DDGI_RAY_GEOMETRY_INVALID &&
        instance.RepresentationGeneration != 0u &&
        instance.StableInstanceIdentity != 0u &&
        instance.MaterialRevision != 0u &&
        instance.IndexType == 0u &&
        (instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC ||
         instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_GPU_VERTEX ||
         instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_FOLIAGE_PROXY);
}

bool RaySceneGeometryHasFlag(
    GPUDdgiRayQueryInstance instance,
    uint flag)
{
    return (instance.GeometryFlags & flag) == flag;
}

bool RaySceneGeometryIsDecal(GPUDdgiRayQueryInstance instance)
{
    return instance.GeometryClass == DDGI_RAY_GEOMETRY_DECAL_OVERLAY ||
        RaySceneGeometryHasFlag(
            instance, DDGI_RAY_GEOMETRY_FLAG_DECAL_OVERLAY);
}

uint ReadRaySceneIndex(
    GPUDdgiRayQueryInstance instance,
    uint indexOffset)
{
    return ReadStorageWord(
        instance.IndexBufferIndex,
        instance.IndexOffset + indexOffset);
}

GPUVertex ReadRaySceneVertex(
    GPUDdgiRayQueryInstance instance,
    uint localVertexIndex)
{
    uint vertexIndex = instance.VertexOffset + localVertexIndex;
    return instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC
        ? ReadSplitVertex(vertexIndex)
        : ReadVertexFromBuffer(instance.VertexBufferIndex, vertexIndex);
}

vec2 TransformRaySceneUv(
    vec2 uv,
    vec4 offsetScale,
    float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float sine = sin(rotationRadians);
    float cosine = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * cosine - scaled.y * sine,
        scaled.x * sine + scaled.y * cosine);
}

// Invalid metadata blocks conservatively. Decals, ordinary alpha blend, and
// thin transmission do not become accidental binary sun occluders. Alpha-mask
// candidates use the same authored UV transform, deterministic LOD, vertex
// alpha, cutoff, and material alpha composition as the shared ray scene.
bool RaySceneCandidateBlocksDirectionalShadow(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing,
    mat4x3 objectToWorld,
    float primaryRayFootprintWorld,
    out bool sampledAlphaTexture)
{
    sampledAlphaTexture = false;
    GPUDdgiRayQueryInstance instance =
        ReadRaySceneQueryInstance(instanceIndex);
    if (!RaySceneQueryInstanceIsValid(instance))
        return true;
    if (RaySceneGeometryIsDecal(instance))
        return false;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    if (material.MaterialRevision != instance.MaterialRevision)
        return true;

    bool doubleSided =
        GiMaterialHasFlag(
            material.TransportFlags, GI_MATERIAL_DOUBLE_SIDED) ||
        RaySceneGeometryHasFlag(
            instance, DDGI_RAY_GEOMETRY_FLAG_TWO_SIDED);
    if (!doubleSided && !frontFacing)
        return false;

    int alphaMode = DecodeMaterialAlphaMode(material.NormalScaleBias.y);
    bool thin = RaySceneGeometryHasFlag(
        instance, DDGI_RAY_GEOMETRY_FLAG_THIN_TRANSMISSION);
    if (thin || alphaMode == MATERIAL_ALPHA_MODE_BLEND)
        return false;
    if (alphaMode != MATERIAL_ALPHA_MODE_MASK)
        return true;

    uint triangleBase = primitiveIndex * 3u;
    GPUVertex vertex0 = ReadRaySceneVertex(
        instance, ReadRaySceneIndex(instance, triangleBase + 0u));
    GPUVertex vertex1 = ReadRaySceneVertex(
        instance, ReadRaySceneIndex(instance, triangleBase + 1u));
    GPUVertex vertex2 = ReadRaySceneVertex(
        instance, ReadRaySceneIndex(instance, triangleBase + 2u));
    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);
    vec2 uv0 = vertex0.TexCoord * bary.x +
        vertex1.TexCoord * bary.y + vertex2.TexCoord * bary.z;
    vec2 uv1 = vertex0.TexCoord2 * bary.x +
        vertex1.TexCoord2 * bary.y + vertex2.TexCoord2 * bary.z;
    float vertexAlpha = clamp(
        vertex0.Color.a * bary.x + vertex1.Color.a * bary.y +
            vertex2.Color.a * bary.z,
        0.0,
        1.0);
    float sampledTextureAlpha = 1.0;
    if (GiMaterialHasFlag(
            material.TransportFlags,
            GI_MATERIAL_HAS_BASE_COLOR_TEXTURE) &&
        material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX &&
        material.AlbedoTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        vec2 uv = int(round(material.TextureTexCoordSets.x)) == 1 ? uv1 : uv0;
        uv = TransformRaySceneUv(
            uv,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);

        vec3 world0 = objectToWorld * vec4(vertex0.Position, 1.0);
        vec3 world1 = objectToWorld * vec4(vertex1.Position, 1.0);
        vec3 world2 = objectToWorld * vec4(vertex2.Position, 1.0);
        vec2 authoredUv0 = int(round(material.TextureTexCoordSets.x)) == 1
            ? vertex0.TexCoord2
            : vertex0.TexCoord;
        vec2 authoredUv1 = int(round(material.TextureTexCoordSets.x)) == 1
            ? vertex1.TexCoord2
            : vertex1.TexCoord;
        vec2 authoredUv2 = int(round(material.TextureTexCoordSets.x)) == 1
            ? vertex2.TexCoord2
            : vertex2.TexCoord;
        authoredUv0 = TransformRaySceneUv(
            authoredUv0,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        authoredUv1 = TransformRaySceneUv(
            authoredUv1,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        authoredUv2 = TransformRaySceneUv(
            authoredUv2,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        float worldEdge = max(
            max(length(world1 - world0), length(world2 - world0)),
            length(world2 - world1));
        float uvEdge = max(
            max(length(authoredUv1 - authoredUv0), length(authoredUv2 - authoredUv0)),
            length(authoredUv2 - authoredUv1));
        if (isnan(worldEdge) || isinf(worldEdge) || worldEdge <= 1.0e-7 ||
            isnan(uvEdge) || isinf(uvEdge) ||
            isnan(primaryRayFootprintWorld) || isinf(primaryRayFootprintWorld) ||
            primaryRayFootprintWorld <= 0.0)
        {
            // A malformed footprint cannot be interpreted as transparent.
            return true;
        }
        ivec2 baseDimensions = textureSize(
            BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)],
            0);
        float texelFootprint = max(primaryRayFootprintWorld, 1.0e-6) *
            (uvEdge / worldEdge) *
            float(max(max(baseDimensions.x, baseDimensions.y), 1));
        float primaryLod = clamp(
            log2(max(texelFootprint, 1.0)),
            0.0,
            float(max(textureQueryLevels(
                BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)]) - 1, 0)));
        sampledTextureAlpha = textureLod(
            BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)],
            uv,
            primaryLod).a;
        sampledAlphaTexture = true;
    }

    return DdgiAlphaCandidateOccupiesOpaqueTransport(
        material.Albedo.a,
        vertexAlpha,
        sampledTextureAlpha,
        material.NormalScaleBias.y,
        material.NormalScaleBias.z);
}

#endif // NJULF_RAY_SCENE_ALPHA_GLSL
