#ifndef NJULF_GI_CAUSTIC_RAY_QUERY_GLSL
#define NJULF_GI_CAUSTIC_RAY_QUERY_GLSL

#include "gi_material_transport.glsl"
#include "ddgi_alpha_coverage.glsl"

const float GI_CAUSTIC_RAY_EPSILON = 0.002;
const uint GI_CAUSTIC_MAX_PATH_VERTICES = 8u;

struct GiCausticRayHit
{
    uint InstanceIndex;
    uint PrimitiveIndex;
    vec2 Barycentrics;
    float Distance;
    vec3 Position;
    vec3 CanonicalGeometricNormal;
    vec2 Uv0;
    vec2 Uv1;
    vec4 VertexColor;
    bool FrontFacing;
    GPUDdgiRayQueryInstance Instance;
    GPUMaterialData Material;
};

GPUDdgiRayQueryInstance GiCausticReadRayQueryInstance(uint instanceIndex)
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

bool GiCausticRayInstanceValid(GPUDdgiRayQueryInstance instance)
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

bool GiCausticRayGeometryIsDecal(GPUDdgiRayQueryInstance instance)
{
    return instance.GeometryClass == DDGI_RAY_GEOMETRY_DECAL_OVERLAY ||
        (instance.GeometryFlags & DDGI_RAY_GEOMETRY_FLAG_DECAL_OVERLAY) != 0u;
}

uint GiCausticReadRayIndex(
    GPUDdgiRayQueryInstance instance,
    uint indexOffset)
{
    return ReadStorageWord(
        instance.IndexBufferIndex,
        instance.IndexOffset + indexOffset);
}

GPUVertex GiCausticReadRayVertex(
    GPUDdgiRayQueryInstance instance,
    uint localVertexIndex)
{
    uint vertexIndex = instance.VertexOffset + localVertexIndex;
    return instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC
        ? ReadSplitVertex(vertexIndex)
        : ReadVertexFromBuffer(instance.VertexBufferIndex, vertexIndex);
}

vec2 GiCausticTextureTransform(
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

bool GiCausticCandidatePassesOpacity(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing)
{
    GPUDdgiRayQueryInstance instance =
        GiCausticReadRayQueryInstance(instanceIndex);
    if (!GiCausticRayInstanceValid(instance))
        return true; // malformed metadata blocks conservatively
    if (GiCausticRayGeometryIsDecal(instance))
        return false;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    bool doubleSided = GiMaterialHasFlag(
            material.TransportFlags, GI_MATERIAL_DOUBLE_SIDED) ||
        (instance.GeometryFlags & DDGI_RAY_GEOMETRY_FLAG_TWO_SIDED) != 0u;
    if (!doubleSided && !frontFacing)
        return false;

    int alphaMode = DecodeMaterialAlphaMode(material.NormalScaleBias.y);
    if (alphaMode == MATERIAL_ALPHA_MODE_BLEND)
        return false;
    if (alphaMode != MATERIAL_ALPHA_MODE_MASK)
        return true;

    uint triangleBase = primitiveIndex * 3u;
    GPUVertex v0 = GiCausticReadRayVertex(
        instance, GiCausticReadRayIndex(instance, triangleBase + 0u));
    GPUVertex v1 = GiCausticReadRayVertex(
        instance, GiCausticReadRayIndex(instance, triangleBase + 1u));
    GPUVertex v2 = GiCausticReadRayVertex(
        instance, GiCausticReadRayIndex(instance, triangleBase + 2u));
    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);
    vec2 uv0 = v0.TexCoord * bary.x + v1.TexCoord * bary.y +
        v2.TexCoord * bary.z;
    vec2 uv1 = v0.TexCoord2 * bary.x + v1.TexCoord2 * bary.y +
        v2.TexCoord2 * bary.z;
    float vertexAlpha = clamp(
        v0.Color.a * bary.x + v1.Color.a * bary.y + v2.Color.a * bary.z,
        0.0, 1.0);
    float textureAlpha = 1.0;
    if (GiMaterialHasFlag(
            material.TransportFlags, GI_MATERIAL_HAS_BASE_COLOR_TEXTURE) &&
        material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX &&
        material.AlbedoTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        vec2 uv = int(round(material.TextureTexCoordSets.x)) == 1 ? uv1 : uv0;
        uv = GiCausticTextureTransform(
            uv, material.BaseColorOffsetScale, material.TextureRotations.x);
        textureAlpha = textureLod(
            BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)],
            uv,
            max(material.DdgiMaterialPolicy.y, 0.0)).a;
    }
    return DdgiAlphaCandidateOccupiesOpaqueTransport(
        material.Albedo.a,
        vertexAlpha,
        textureAlpha,
        material.NormalScaleBias.y,
        material.NormalScaleBias.z);
}

bool GiCausticResolveCommittedHit(
    rayQueryEXT query,
    vec3 rayOrigin,
    vec3 rayDirection,
    out GiCausticRayHit hit)
{
    hit.InstanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(query, true);
    hit.PrimitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(query, true);
    hit.Barycentrics = rayQueryGetIntersectionBarycentricsEXT(query, true);
    hit.Distance = rayQueryGetIntersectionTEXT(query, true);
    hit.FrontFacing = rayQueryGetIntersectionFrontFaceEXT(query, true);
    hit.Position = rayOrigin + rayDirection * hit.Distance;
    hit.Instance = GiCausticReadRayQueryInstance(hit.InstanceIndex);
    if (!GiCausticRayInstanceValid(hit.Instance) ||
        !GiCausticFinite(hit.Distance) || hit.Distance <= 0.0 ||
        !GiCausticFinite(hit.Position))
    {
        return false;
    }

    uint triangleBase = hit.PrimitiveIndex * 3u;
    GPUVertex v0 = GiCausticReadRayVertex(
        hit.Instance, GiCausticReadRayIndex(hit.Instance, triangleBase + 0u));
    GPUVertex v1 = GiCausticReadRayVertex(
        hit.Instance, GiCausticReadRayIndex(hit.Instance, triangleBase + 1u));
    GPUVertex v2 = GiCausticReadRayVertex(
        hit.Instance, GiCausticReadRayIndex(hit.Instance, triangleBase + 2u));
    vec3 localNormal = cross(v1.Position - v0.Position, v2.Position - v0.Position);
    if (!GiCausticFinite(localNormal) || dot(localNormal, localNormal) <= 1.0e-12)
        return false;
    mat4 worldMatrix = transpose(inverse(hit.Instance.WorldMatrixInverseTranspose));
    float determinantSign = determinant(mat3(worldMatrix)) < 0.0 ? -1.0 : 1.0;
    hit.CanonicalGeometricNormal = normalize(MulRowMajor(
        vec4(normalize(localNormal) * determinantSign, 0.0),
        hit.Instance.WorldMatrixInverseTranspose).xyz);
    if (!GiCausticFinite(hit.CanonicalGeometricNormal) ||
        dot(hit.CanonicalGeometricNormal, hit.CanonicalGeometricNormal) < 0.99)
    {
        return false;
    }
    vec3 bary = vec3(
        1.0 - hit.Barycentrics.x - hit.Barycentrics.y,
        hit.Barycentrics.x,
        hit.Barycentrics.y);
    hit.Uv0 = v0.TexCoord * bary.x + v1.TexCoord * bary.y +
        v2.TexCoord * bary.z;
    hit.Uv1 = v0.TexCoord2 * bary.x + v1.TexCoord2 * bary.y +
        v2.TexCoord2 * bary.z;
    hit.VertexColor = v0.Color * bary.x + v1.Color * bary.y +
        v2.Color * bary.z;
    hit.Material = ReadMaterial(hit.Instance.MaterialIndex);
    return hit.Material.MaterialRevision == hit.Instance.MaterialRevision;
}

vec2 GiCausticMaterialUv(
    GiCausticRayHit hit,
    float texCoordSet,
    vec4 offsetScale,
    float rotation)
{
    vec2 uv = int(round(texCoordSet)) == 1 ? hit.Uv1 : hit.Uv0;
    return GiCausticTextureTransform(uv, offsetScale, rotation);
}

vec4 GiCausticSampleBaseColor(GiCausticRayHit hit)
{
    vec4 sampleValue = vec4(1.0);
    if (GiMaterialHasFlag(
            hit.Material.TransportFlags, GI_MATERIAL_HAS_BASE_COLOR_TEXTURE) &&
        hit.Material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX &&
        hit.Material.AlbedoTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        sampleValue = textureLod(
            BindlessTextures[nonuniformEXT(hit.Material.AlbedoTextureIndex)],
            GiCausticMaterialUv(hit,
                hit.Material.TextureTexCoordSets.x,
                hit.Material.BaseColorOffsetScale,
                hit.Material.TextureRotations.x),
            max(hit.Material.DdgiMaterialPolicy.y, 0.0));
    }
    return max(hit.Material.Albedo * hit.VertexColor * sampleValue, vec4(0.0));
}

vec2 GiCausticSampleMetallicRoughness(GiCausticRayHit hit)
{
    vec4 sampleValue = vec4(1.0);
    if (GiMaterialHasFlag(hit.Material.TransportFlags,
            GI_MATERIAL_HAS_METALLIC_ROUGHNESS_TEXTURE) &&
        hit.Material.MetallicRoughnessTextureIndex >= FIRST_TEXTURE_INDEX &&
        hit.Material.MetallicRoughnessTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        sampleValue = textureLod(
            BindlessTextures[nonuniformEXT(
                hit.Material.MetallicRoughnessTextureIndex)],
            GiCausticMaterialUv(hit,
                hit.Material.TextureTexCoordSets.z,
                hit.Material.MetallicRoughnessOffsetScale,
                hit.Material.TextureRotations.z),
            max(hit.Material.DdgiMaterialPolicy.y, 0.0));
    }
    return vec2(
        clamp(hit.Material.MetallicRoughnessAO.x * sampleValue.b, 0.0, 1.0),
        clamp(hit.Material.MetallicRoughnessAO.y * sampleValue.g, 0.0, 1.0));
}

bool GiCausticTraceNearest(
    vec3 origin,
    vec3 direction,
    float maximumDistance,
    out GiCausticRayHit hit)
{
    if (!GiCausticFinite(origin) || !GiCausticFinite(direction) ||
        !GiCausticFinite(maximumDistance) || maximumDistance <= GI_CAUSTIC_RAY_EPSILON)
    {
        return false;
    }
    rayQueryEXT query;
    rayQueryInitializeEXT(
        query,
        SceneTlas,
        gl_RayFlagsNoneEXT,
        0xff,
        origin,
        GI_CAUSTIC_RAY_EPSILON,
        direction,
        maximumDistance);
    uint candidateCount = 0u;
    while (rayQueryProceedEXT(query))
    {
        if (rayQueryGetIntersectionTypeEXT(query, false) !=
            gl_RayQueryCandidateIntersectionTriangleEXT)
        {
            continue;
        }
        ++candidateCount;
        if (candidateCount > 64u)
        {
            rayQueryConfirmIntersectionEXT(query);
            rayQueryTerminateEXT(query);
            break;
        }
        uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(
            query, false);
        uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(query, false);
        vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(query, false);
        bool frontFacing = rayQueryGetIntersectionFrontFaceEXT(query, false);
        if (GiCausticCandidatePassesOpacity(
                instanceIndex, primitiveIndex, barycentrics, frontFacing))
        {
            rayQueryConfirmIntersectionEXT(query);
        }
    }
    if (rayQueryGetIntersectionTypeEXT(query, true) ==
        gl_RayQueryCommittedIntersectionNoneEXT)
    {
        return false;
    }
    return GiCausticResolveCommittedHit(query, origin, direction, hit);
}

vec3 GiCausticOrientedNormal(GiCausticRayHit hit)
{
    return hit.FrontFacing
        ? hit.CanonicalGeometricNormal
        : -hit.CanonicalGeometricNormal;
}

bool GiCausticHitIsDiffuseReceiver(GiCausticRayHit hit)
{
    uint flags = hit.Material.TransportFlags;
    return !GiCausticRayGeometryIsDecal(hit.Instance) &&
        !GiMaterialHasFlag(flags, GI_MATERIAL_UNLIT) &&
        !GiMaterialHasFlag(flags, GI_MATERIAL_TRANSMISSION_REMOVES_OPAQUE_DIFFUSE) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_RECEIVES_INDIRECT_DIFFUSE) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_DIFFUSE_PROFILE_VALID);
}

uint GiCausticPackOctahedral(vec3 direction)
{
    direction = normalize(direction);
    direction /= max(abs(direction.x) + abs(direction.y) + abs(direction.z),
        1.0e-12);
    vec2 encoded = direction.xy;
    if (direction.z < 0.0)
    {
        encoded = (1.0 - abs(encoded.yx)) * vec2(
            encoded.x >= 0.0 ? 1.0 : -1.0,
            encoded.y >= 0.0 ? 1.0 : -1.0);
    }
    ivec2 quantized = ivec2(round(clamp(encoded, vec2(-1.0), vec2(1.0)) *
        32767.0));
    return (uint(quantized.x) & 0xffffu) |
        ((uint(quantized.y) & 0xffffu) << 16u);
}

#endif // NJULF_GI_CAUSTIC_RAY_QUERY_GLSL
