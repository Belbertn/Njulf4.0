#ifndef NJULF_C4_RECEIVER_PAYLOAD_GLSL
#define NJULF_C4_RECEIVER_PAYLOAD_GLSL

const uint NJULF_C4_RECEIVER_VALID = 1u << 30u;
const uint NJULF_C4_RECEIVER_OPAQUE_OR_MASKED = 1u << 31u;

vec2 NjulfC4OctEncodeNormal(vec3 value)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 1.0e-12 || any(isnan(value)) || any(isinf(value)))
        return vec2(0.0);
    vec3 normal = value * inversesqrt(lengthSquared);
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return clamp(normal.xy, vec2(-1.0), vec2(1.0));
}

vec3 NjulfC4OctDecodeNormal(uint packed)
{
    vec2 encoded = unpackSnorm2x16(packed);
    vec3 normal = vec3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    float lengthSquared = dot(normal, normal);
    return lengthSquared > 1.0e-12
        ? normal * inversesqrt(lengthSquared)
        : vec3(0.0, 1.0, 0.0);
}

uint NjulfC4PackRgb9E5(vec3 value)
{
    vec3 bounded = clamp(value, vec3(0.0), vec3(65408.0));
    float maximumChannel = max(bounded.x, max(bounded.y, bounded.z));
    int sharedExponent = maximumChannel <= 0.0
        ? 0
        : clamp(max(-16, int(floor(log2(maximumChannel)))) + 16,
            0, 31);
    float sharedScale = exp2(float(sharedExponent - 24));
    if (maximumChannel > 0.0 &&
        floor(maximumChannel / sharedScale + 0.5) >= 512.0 &&
        sharedExponent < 31)
    {
        sharedExponent++;
        sharedScale *= 2.0;
    }
    uvec3 rgb9 = uvec3(clamp(
        floor(bounded / sharedScale + vec3(0.5)),
        vec3(0.0), vec3(511.0)));
    return rgb9.x | (rgb9.y << 9u) | (rgb9.z << 18u) |
        (uint(sharedExponent) << 27u);
}

vec3 NjulfC4UnpackRgb9E5(uint packed)
{
    uvec3 rgb9 = uvec3(
        packed & 0x1ffu,
        (packed >> 9u) & 0x1ffu,
        (packed >> 18u) & 0x1ffu);
    uint sharedExponent = packed >> 27u;
    return vec3(rgb9) * exp2(float(int(sharedExponent) - 24));
}

uint NjulfC4PackDielectricF0AndFlags(vec3 dielectricF0)
{
    uvec3 encoded = uvec3(round(clamp(dielectricF0, vec3(0.0), vec3(1.0)) *
        1023.0));
    return encoded.x | (encoded.y << 10u) | (encoded.z << 20u) |
        NJULF_C4_RECEIVER_VALID | NJULF_C4_RECEIVER_OPAQUE_OR_MASKED;
}

vec3 NjulfC4UnpackDielectricF0(uint packed)
{
    return vec3(
        packed & 0x3ffu,
        (packed >> 10u) & 0x3ffu,
        (packed >> 20u) & 0x3ffu) / 1023.0;
}

bool NjulfC4ReceiverPayloadValid(uvec4 payload)
{
    return (payload.w & (NJULF_C4_RECEIVER_VALID |
        NJULF_C4_RECEIVER_OPAQUE_OR_MASKED)) ==
        (NJULF_C4_RECEIVER_VALID | NJULF_C4_RECEIVER_OPAQUE_OR_MASKED);
}

#endif
