#ifndef NJULF_AUTOMATIC_PLANAR_REFLECTION_GLSL
#define NJULF_AUTOMATIC_PLANAR_REFLECTION_GLSL

const uint AUTOMATIC_PLANAR_METADATA_MAGIC = 0x31524c50u;
const uint AUTOMATIC_PLANAR_METADATA_VERSION = 3u;
const uint AUTOMATIC_PLANAR_BANK_WORD_COUNT = 1024u;
const uint AUTOMATIC_PLANAR_HEADER_WORD_COUNT = 16u;
const uint AUTOMATIC_PLANAR_RECORD_WORD_COUNT = 96u;
const uint AUTOMATIC_PLANAR_MAXIMUM_CAPTURES = 2u;
const uint AUTOMATIC_PLANAR_CAPTURE_LAYER_FLAG = 0x1000u;
const uint AUTOMATIC_PLANAR_RECEIVER_IDENTITY_MASK = 0x003fffffu;
const uint AUTOMATIC_PLANAR_EXCLUSION_BITSET_FLAG = 0x80000000u;
const uint AUTOMATIC_PLANAR_EXCLUSION_COUNT_MASK = 0x7fffffffu;

uint AutomaticPlanarHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

uint AutomaticPlanarHashCombine(uint seed, uint value)
{
    return AutomaticPlanarHash(
        seed ^ (value + 0x9e3779b9u +
            (seed << 6u) + (seed >> 2u)));
}

uint AutomaticPlanarReceiverIdentity(
    uint objectIndex,
    uint materialIndex,
    uint materialRevision)
{
    uint identity = AutomaticPlanarHash(objectIndex);
    identity = AutomaticPlanarHashCombine(identity, materialIndex);
    identity = AutomaticPlanarHashCombine(identity, materialRevision);
    return identity & AUTOMATIC_PLANAR_RECEIVER_IDENTITY_MASK;
}

uint AutomaticPlanarBankBase(uint frameIndex)
{
    return (frameIndex & 1u) * AUTOMATIC_PLANAR_BANK_WORD_COUNT;
}

uint AutomaticPlanarRead(uint frameIndex, uint wordOffset)
{
    return ReadStorageWord(
        uint(AUTOMATIC_PLANAR_REFLECTION_BUFFER_INDEX),
        AutomaticPlanarBankBase(frameIndex) + wordOffset);
}

bool AutomaticPlanarMetadataAvailable(uint frameIndex)
{
    return AutomaticPlanarRead(frameIndex, 0u) ==
               AUTOMATIC_PLANAR_METADATA_MAGIC &&
           AutomaticPlanarRead(frameIndex, 1u) ==
               AUTOMATIC_PLANAR_METADATA_VERSION;
}

uint AutomaticPlanarCaptureCount(uint frameIndex)
{
    if (!AutomaticPlanarMetadataAvailable(frameIndex))
        return 0u;
    return min(
        AutomaticPlanarRead(frameIndex, 2u),
        AUTOMATIC_PLANAR_MAXIMUM_CAPTURES);
}

uint AutomaticPlanarRecordBase(uint slot)
{
    return AUTOMATIC_PLANAR_HEADER_WORD_COUNT +
        slot * AUTOMATIC_PLANAR_RECORD_WORD_COUNT;
}

float AutomaticPlanarReadFloat(uint frameIndex, uint offset)
{
    return uintBitsToFloat(AutomaticPlanarRead(frameIndex, offset));
}

vec3 AutomaticPlanarReadVec3(uint frameIndex, uint offset)
{
    return vec3(
        AutomaticPlanarReadFloat(frameIndex, offset + 0u),
        AutomaticPlanarReadFloat(frameIndex, offset + 1u),
        AutomaticPlanarReadFloat(frameIndex, offset + 2u));
}

vec4 AutomaticPlanarReadVec4(uint frameIndex, uint offset)
{
    return vec4(
        AutomaticPlanarReadFloat(frameIndex, offset + 0u),
        AutomaticPlanarReadFloat(frameIndex, offset + 1u),
        AutomaticPlanarReadFloat(frameIndex, offset + 2u),
        AutomaticPlanarReadFloat(frameIndex, offset + 3u));
}

vec4 AutomaticPlanarProject(
    uint frameIndex,
    uint matrixOffset,
    vec3 worldPosition)
{
    vec4 position = vec4(worldPosition, 1.0);
    // CPU matrices are row-major and world positions multiply from the left.
    return vec4(
        position.x * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 0u) +
        position.y * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 4u) +
        position.z * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 8u) +
        position.w * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 12u),
        position.x * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 1u) +
        position.y * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 5u) +
        position.z * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 9u) +
        position.w * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 13u),
        position.x * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 2u) +
        position.y * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 6u) +
        position.z * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 10u) +
        position.w * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 14u),
        position.x * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 3u) +
        position.y * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 7u) +
        position.z * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 11u) +
        position.w * AutomaticPlanarReadFloat(frameIndex, matrixOffset + 15u));
}

bool AutomaticPlanarListContains(
    uint frameIndex,
    uint offset,
    uint count,
    uint value)
{
    uint boundedCount = min(count, 256u);
    for (uint index = 0u; index < boundedCount; index++)
    {
        if (AutomaticPlanarRead(frameIndex, offset + index) == value)
            return true;
    }
    return false;
}

bool AutomaticPlanarExactListContains(
    uint frameIndex,
    uint offset,
    uint count,
    uint value)
{
    for (uint index = 0u; index < count; index++)
    {
        if (AutomaticPlanarRead(frameIndex, offset + index) == value)
            return true;
    }
    return false;
}

bool AutomaticPlanarExcludedObjectContains(
    uint frameIndex,
    uint record,
    uint objectIndex)
{
    uint descriptor = AutomaticPlanarRead(frameIndex, record + 90u);
    uint payloadWordCount =
        descriptor & AUTOMATIC_PLANAR_EXCLUSION_COUNT_MASK;
    uint payloadOffset = AutomaticPlanarRead(frameIndex, record + 91u);
    if ((descriptor & AUTOMATIC_PLANAR_EXCLUSION_BITSET_FLAG) != 0u)
    {
        uint wordIndex = objectIndex >> 5u;
        if (wordIndex >= payloadWordCount)
            return false;
        uint word = AutomaticPlanarRead(
            frameIndex,
            payloadOffset + wordIndex);
        return (word & (1u << (objectIndex & 31u))) != 0u;
    }

    return AutomaticPlanarExactListContains(
        frameIndex,
        payloadOffset,
        payloadWordCount,
        objectIndex);
}

bool AutomaticPlanarShouldDiscardCaptureFragment(
    uint frameIndex,
    uint slot,
    uint objectIndex,
    vec3 worldPosition,
    vec3 reflectedCameraPosition)
{
    if (slot >= AutomaticPlanarCaptureCount(frameIndex))
        return true;
    uint record = AutomaticPlanarRecordBase(slot);
    if (AutomaticPlanarExcludedObjectContains(
            frameIndex,
            record,
            objectIndex))
    {
        return true;
    }

    vec4 plane = AutomaticPlanarReadVec4(frameIndex, record + 0u);
    float virtualCameraDistance = dot(
        plane,
        vec4(reflectedCameraPosition, 1.0));
    // The retained half-space is the side containing the real camera, which
    // is opposite the reflected camera. This shader-space clip is the exact
    // reverse-Z oblique-plane ownership rule without perturbing depth values.
    vec4 retainedPlane = virtualCameraDistance < 0.0 ? plane : -plane;
    float diagonal = max(
        AutomaticPlanarReadFloat(frameIndex, record + 18u),
        0.0);
    float tolerance = max(0.0005, diagonal * 0.0001);
    return dot(retainedPlane, vec4(worldPosition, 1.0)) < -tolerance;
}

bool AutomaticPlanarTrySample(
    uint frameIndex,
    uint receiverIdentity,
    vec3 worldPosition,
    vec3 worldNormal,
    float roughness,
    out vec3 radiance,
    out float confidence)
{
    radiance = vec3(0.0);
    confidence = 0.0;
    uint captureCount = AutomaticPlanarCaptureCount(frameIndex);
    for (uint slot = 0u; slot < captureCount; slot++)
    {
        uint record = AutomaticPlanarRecordBase(slot);
        uint identityCount = AutomaticPlanarRead(frameIndex, record + 88u);
        uint identityOffset = AutomaticPlanarRead(frameIndex, record + 89u);
        if (!AutomaticPlanarListContains(
                frameIndex,
                identityOffset,
                identityCount,
                receiverIdentity))
        {
            continue;
        }

        vec4 plane = AutomaticPlanarReadVec4(frameIndex, record + 0u);
        vec3 planeNormal = normalize(plane.xyz);
        float diagonal = max(
            AutomaticPlanarReadFloat(frameIndex, record + 18u),
            0.0);
        float planeTolerance = max(0.0005, diagonal * 0.001);
        if (abs(dot(plane, vec4(worldPosition, 1.0))) > planeTolerance ||
            abs(dot(normalize(worldNormal), planeNormal)) < 0.9995)
        {
            continue;
        }

        vec3 origin = AutomaticPlanarReadVec3(frameIndex, record + 4u);
        vec3 tangent = AutomaticPlanarReadVec3(frameIndex, record + 8u);
        vec3 bitangent = AutomaticPlanarReadVec3(frameIndex, record + 12u);
        vec2 boundsMinimum = vec2(
            AutomaticPlanarReadFloat(frameIndex, record + 11u),
            AutomaticPlanarReadFloat(frameIndex, record + 15u));
        vec2 boundsMaximum = vec2(
            AutomaticPlanarReadFloat(frameIndex, record + 16u),
            AutomaticPlanarReadFloat(frameIndex, record + 17u));
        vec3 relative = worldPosition - origin;
        vec2 planePosition = vec2(
            dot(relative, tangent),
            dot(relative, bitangent));
        vec2 boundTolerance = max(
            (boundsMaximum - boundsMinimum) * 0.001,
            vec2(0.0005));
        if (any(lessThan(planePosition, boundsMinimum - boundTolerance)) ||
            any(greaterThan(planePosition, boundsMaximum + boundTolerance)))
        {
            continue;
        }

        vec4 clip = AutomaticPlanarProject(
            frameIndex,
            record + 24u,
            worldPosition);
        if (clip.w <= 0.000001 || any(isnan(clip)) || any(isinf(clip)))
            continue;
        vec2 uv = clip.xy / clip.w * 0.5 + vec2(0.5);
        if (any(lessThanEqual(uv, vec2(0.0))) ||
            any(greaterThanEqual(uv, vec2(1.0))))
        {
            continue;
        }

        uvec2 dimensions = uvec2(
            max(AutomaticPlanarRead(frameIndex, record + 21u), 1u),
            max(AutomaticPlanarRead(frameIndex, record + 22u), 1u));
        vec2 edgePixels = min(uv, vec2(1.0) - uv) * vec2(dimensions);
        float edgeFade = smoothstep(
            0.0,
            2.0,
            min(edgePixels.x, edgePixels.y));
        uint mipCount = max(
            AutomaticPlanarRead(frameIndex, record + 23u),
            1u);
        float mip = clamp(roughness, 0.0, 1.0) *
            float(mipCount - 1u);
        uint mip0 = min(uint(floor(mip)), mipCount - 1u);
        uint mip1 = min(mip0 + 1u, mipCount - 1u);
        float mipBlend = fract(mip);
        uint textureOffset = AutomaticPlanarRead(
            frameIndex,
            record + 92u);
        uint textureIndex0 = AutomaticPlanarRead(
            frameIndex,
            textureOffset + mip0);
        uint textureIndex1 = AutomaticPlanarRead(
            frameIndex,
            textureOffset + mip1);
        vec2 texel = 0.5 / vec2(max(dimensions >> mip0, uvec2(1u)));
        vec2 sampleUv = clamp(uv, texel, vec2(1.0) - texel);
        vec4 sample0 = texture(
            BindlessTextures[nonuniformEXT(textureIndex0)],
            sampleUv);
        vec4 sample1 = texture(
            BindlessTextures[nonuniformEXT(textureIndex1)],
            sampleUv);
        vec4 filteredSample = mix(sample0, sample1, mipBlend);
        vec3 value = filteredSample.rgb;
        if (any(isnan(value)) || any(isinf(value)) ||
            any(lessThan(value, vec3(0.0))))
        {
            continue;
        }
        float captureConfidence = clamp(
            AutomaticPlanarReadFloat(frameIndex, record + 7u),
            0.0,
            1.0);
        radiance = value;
        confidence = captureConfidence * edgeFade *
            clamp(filteredSample.a, 0.0, 1.0);
        return confidence > 0.0;
    }
    return false;
}

#endif
