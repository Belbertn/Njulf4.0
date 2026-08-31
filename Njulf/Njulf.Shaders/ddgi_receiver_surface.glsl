#ifndef NJULF_DDGI_RECEIVER_SURFACE_GLSL
#define NJULF_DDGI_RECEIVER_SURFACE_GLSL

// Keep synchronized with SimpleDdgiReceiverSurfaceAbi. The parallel sidecar
// remains eight bytes and never steals precision from the 16-byte radiance ABI.
const uint SIMPLE_DDGI_RECEIVER_SURFACE_ABI_VERSION = 1u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_DEPTH_MASK = 0x00ffffffu;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_MAXIMUM_OFFSET = 15u;
const float SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_REVERSE_Z = 1.0 / 65536.0;
const float SIMPLE_DDGI_RECEIVER_SURFACE_MAXIMUM_RELATIVE_DEPTH = 0.035;
const float SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_NORMAL_DOT = 0.94;
const float SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_WORLD_TOLERANCE = 0.001;
const float SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_PLANE_TOLERANCE = 0.0015;
const float SIMPLE_DDGI_RECEIVER_SURFACE_CAMERA_DISTANCE_SCALE = 0.00001;
const float SIMPLE_DDGI_RECEIVER_SURFACE_PLANE_CAMERA_DISTANCE_SCALE = 0.00005;
const float SIMPLE_DDGI_RECEIVER_SURFACE_PLANE_FOOTPRINT_SCALE = 1.75;
const float SIMPLE_DDGI_RECEIVER_SURFACE_NORMAL_LENGTH_TOLERANCE = 0.01;

const uint SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED = 0u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID = 1u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE = 2u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_DEPTH = 3u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_POSITION = 4u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_PLANE = 5u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NORMAL = 6u;
const uint SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INSUFFICIENT_SUPPORT = 7u;

struct SimpleDdgiReceiverSurface
{
    vec3 GeometricNormal;
    float ReverseZ;
    uvec2 RepresentativeOffset;
};

bool SimpleDdgiReceiverSurfaceFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool SimpleDdgiReceiverSurfaceFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

float SimpleDdgiReceiverSurfaceSignNotZero(float value)
{
    return value >= 0.0 ? 1.0 : -1.0;
}

vec2 SimpleDdgiReceiverSurfaceEncodeOctahedral(vec3 normal)
{
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    vec2 encoded = normal.xy;
    if (normal.z < 0.0)
    {
        encoded = vec2(
            (1.0 - abs(encoded.y)) *
                SimpleDdgiReceiverSurfaceSignNotZero(encoded.x),
            (1.0 - abs(encoded.x)) *
                SimpleDdgiReceiverSurfaceSignNotZero(encoded.y));
    }
    return encoded;
}

vec3 SimpleDdgiReceiverSurfaceDecodeOctahedral(vec2 encoded)
{
    vec3 normal = vec3(
        encoded,
        1.0 - abs(encoded.x) - abs(encoded.y));
    float fold = max(-normal.z, 0.0);
    normal.x += normal.x >= 0.0 ? -fold : fold;
    normal.y += normal.y >= 0.0 ? -fold : fold;
    float lengthSquared = dot(normal, normal);
    if (!(lengthSquared > 0.0) ||
        !SimpleDdgiReceiverSurfaceFinite(lengthSquared))
    {
        return vec3(uintBitsToFloat(0x7fc00000u));
    }
    return normal * inversesqrt(lengthSquared);
}

uint SimpleDdgiReceiverSurfaceEncodeReverseZ(float reverseZ)
{
    if (!SimpleDdgiReceiverSurfaceFinite(reverseZ) || !(reverseZ > 0.0))
        return 0u;
    float normalized = clamp(
        (log2(max(
            reverseZ,
            SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_REVERSE_Z)) + 16.0) / 16.0,
        0.0,
        1.0);
    return 1u + uint(round(normalized * 16777214.0));
}

float SimpleDdgiReceiverSurfaceDecodeReverseZ(uint depthCode)
{
    depthCode &= SIMPLE_DDGI_RECEIVER_SURFACE_DEPTH_MASK;
    if (depthCode == 0u)
        return 0.0;
    float normalized = float(depthCode - 1u) / 16777214.0;
    return exp2(normalized * 16.0 - 16.0);
}

uvec2 SimpleDdgiReceiverSurfaceInvalid()
{
    return uvec2(0u);
}

uvec2 SimpleDdgiReceiverSurfacePack(
    vec3 geometricNormal,
    float reverseZ,
    uvec2 representativeOffset)
{
    float normalLengthSquared = dot(geometricNormal, geometricNormal);
    if (!SimpleDdgiReceiverSurfaceFinite(geometricNormal) ||
        !SimpleDdgiReceiverSurfaceFinite(normalLengthSquared) ||
        abs(normalLengthSquared - 1.0) >
            SIMPLE_DDGI_RECEIVER_SURFACE_NORMAL_LENGTH_TOLERANCE ||
        !SimpleDdgiReceiverSurfaceFinite(reverseZ) || !(reverseZ > 0.0) ||
        any(greaterThan(
            representativeOffset,
            uvec2(SIMPLE_DDGI_RECEIVER_SURFACE_MAXIMUM_OFFSET))))
    {
        return SimpleDdgiReceiverSurfaceInvalid();
    }

    uint depthCode = SimpleDdgiReceiverSurfaceEncodeReverseZ(reverseZ);
    if (depthCode == 0u)
        return SimpleDdgiReceiverSurfaceInvalid();
    geometricNormal = normalize(geometricNormal);
    return uvec2(
        packSnorm2x16(
            SimpleDdgiReceiverSurfaceEncodeOctahedral(geometricNormal)),
        depthCode |
            (representativeOffset.x << 24u) |
            (representativeOffset.y << 28u));
}

bool SimpleDdgiReceiverSurfaceDecode(
    uvec2 packed,
    out SimpleDdgiReceiverSurface decoded)
{
    uint depthCode = packed.y & SIMPLE_DDGI_RECEIVER_SURFACE_DEPTH_MASK;
    if (depthCode == 0u)
        return false;

    decoded.GeometricNormal = SimpleDdgiReceiverSurfaceDecodeOctahedral(
        unpackSnorm2x16(packed.x));
    decoded.ReverseZ = SimpleDdgiReceiverSurfaceDecodeReverseZ(depthCode);
    decoded.RepresentativeOffset = uvec2(
        (packed.y >> 24u) & 0x0fu,
        (packed.y >> 28u) & 0x0fu);
    return SimpleDdgiReceiverSurfaceFinite(decoded.GeometricNormal) &&
        SimpleDdgiReceiverSurfaceFinite(decoded.ReverseZ) &&
        decoded.ReverseZ > 0.0;
}

bool SimpleDdgiReceiverSurfaceResolvePixel(
    uvec2 cacheCoordinate,
    uint scale,
    SimpleDdgiReceiverSurface surface,
    uvec2 screenExtent,
    out uvec2 pixel)
{
    pixel = uvec2(0u);
    if (scale == 0u ||
        any(greaterThanEqual(
            surface.RepresentativeOffset,
            uvec2(scale))))
    {
        return false;
    }
    pixel = cacheCoordinate * scale + surface.RepresentativeOffset;
    return all(lessThan(pixel, screenExtent));
}

bool SimpleDdgiReceiverSurfaceReconstructPosition(
    uvec2 pixel,
    float reverseZ,
    mat4 inverseViewProjection,
    uvec2 screenExtent,
    out vec3 worldPosition)
{
    worldPosition = vec3(0.0);
    if (any(equal(screenExtent, uvec2(0u))) ||
        any(greaterThanEqual(pixel, screenExtent)) ||
        !SimpleDdgiReceiverSurfaceFinite(reverseZ) || !(reverseZ > 0.0))
    {
        return false;
    }

    vec2 uv = (vec2(pixel) + vec2(0.5)) / vec2(screenExtent);
    vec4 world = MulRowMajor(
        vec4(uv * 2.0 - vec2(1.0), reverseZ, 1.0),
        inverseViewProjection);
    if (!SimpleDdgiReceiverSurfaceFinite(world.xyz) ||
        !SimpleDdgiReceiverSurfaceFinite(world.w) || abs(world.w) <= 0.000001)
    {
        return false;
    }
    worldPosition = world.xyz / world.w;
    return SimpleDdgiReceiverSurfaceFinite(worldPosition);
}

bool SimpleDdgiReceiverSurfaceReconstructForwardPosition(
    uvec2 pixel,
    float reverseZ,
    mat4 inverseProjection,
    mat4 inverseView,
    uvec2 screenExtent,
    out vec3 worldPosition)
{
    worldPosition = vec3(0.0);
    if (any(equal(screenExtent, uvec2(0u))) ||
        any(greaterThanEqual(pixel, screenExtent)) ||
        !SimpleDdgiReceiverSurfaceFinite(reverseZ) || !(reverseZ > 0.0))
    {
        return false;
    }

    vec2 uv = (vec2(pixel) + vec2(0.5)) / vec2(screenExtent);
    vec4 view = MulRowMajor(
        vec4(uv * 2.0 - vec2(1.0), reverseZ, 1.0),
        inverseProjection);
    if (!SimpleDdgiReceiverSurfaceFinite(view.xyz) ||
        !SimpleDdgiReceiverSurfaceFinite(view.w) || abs(view.w) <= 0.000001)
    {
        return false;
    }
    vec4 world = MulRowMajor(vec4(view.xyz / view.w, 1.0), inverseView);
    if (!SimpleDdgiReceiverSurfaceFinite(world.xyz) ||
        !SimpleDdgiReceiverSurfaceFinite(world.w) || abs(world.w) <= 0.000001)
    {
        return false;
    }
    worldPosition = world.xyz / world.w;
    return SimpleDdgiReceiverSurfaceFinite(worldPosition);
}

float SimpleDdgiReceiverSurfacePixelFootprint(
    uvec2 pixel,
    float reverseZ,
    vec3 center,
    mat4 inverseViewProjection,
    uvec2 screenExtent)
{
    uvec2 neighborX = pixel;
    uvec2 neighborY = pixel;
    if (pixel.x + 1u < screenExtent.x)
        neighborX.x++;
    else if (pixel.x > 0u)
        neighborX.x--;
    if (pixel.y + 1u < screenExtent.y)
        neighborY.y++;
    else if (pixel.y > 0u)
        neighborY.y--;

    float footprint = 0.0;
    vec3 neighborPosition;
    if (any(notEqual(neighborX, pixel)) &&
        SimpleDdgiReceiverSurfaceReconstructPosition(
            neighborX,
            reverseZ,
            inverseViewProjection,
            screenExtent,
            neighborPosition))
    {
        footprint = max(footprint, distance(center, neighborPosition));
    }
    if (any(notEqual(neighborY, pixel)) &&
        SimpleDdgiReceiverSurfaceReconstructPosition(
            neighborY,
            reverseZ,
            inverseViewProjection,
            screenExtent,
            neighborPosition))
    {
        footprint = max(footprint, distance(center, neighborPosition));
    }
    return max(footprint, SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_WORLD_TOLERANCE);
}

float SimpleDdgiReceiverSurfaceForwardPixelFootprint(
    uvec2 pixel,
    float reverseZ,
    vec3 center,
    mat4 inverseProjection,
    mat4 inverseView,
    uvec2 screenExtent)
{
    uvec2 neighborX = pixel;
    uvec2 neighborY = pixel;
    if (pixel.x + 1u < screenExtent.x)
        neighborX.x++;
    else if (pixel.x > 0u)
        neighborX.x--;
    if (pixel.y + 1u < screenExtent.y)
        neighborY.y++;
    else if (pixel.y > 0u)
        neighborY.y--;

    float footprint = 0.0;
    vec3 neighborPosition;
    if (any(notEqual(neighborX, pixel)) &&
        SimpleDdgiReceiverSurfaceReconstructForwardPosition(
            neighborX,
            reverseZ,
            inverseProjection,
            inverseView,
            screenExtent,
            neighborPosition))
    {
        footprint = max(footprint, distance(center, neighborPosition));
    }
    if (any(notEqual(neighborY, pixel)) &&
        SimpleDdgiReceiverSurfaceReconstructForwardPosition(
            neighborY,
            reverseZ,
            inverseProjection,
            inverseView,
            screenExtent,
            neighborPosition))
    {
        footprint = max(footprint, distance(center, neighborPosition));
    }
    return max(footprint, SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_WORLD_TOLERANCE);
}

uint SimpleDdgiReceiverSurfaceEvaluateDecoded(
    float firstReverseZ,
    vec3 firstPosition,
    vec3 firstNormal,
    float secondReverseZ,
    vec3 secondPosition,
    vec3 secondNormal,
    float pixelFootprint,
    uint maximumScale,
    vec3 cameraPosition)
{
    if (!SimpleDdgiReceiverSurfaceFinite(firstReverseZ) ||
        !SimpleDdgiReceiverSurfaceFinite(secondReverseZ) ||
        !SimpleDdgiReceiverSurfaceFinite(firstPosition) ||
        !SimpleDdgiReceiverSurfaceFinite(secondPosition) ||
        !SimpleDdgiReceiverSurfaceFinite(firstNormal) ||
        !SimpleDdgiReceiverSurfaceFinite(secondNormal) ||
        !SimpleDdgiReceiverSurfaceFinite(pixelFootprint) ||
        !SimpleDdgiReceiverSurfaceFinite(cameraPosition))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE;
    }

    float relativeDepth = abs(firstReverseZ - secondReverseZ) /
        max(max(firstReverseZ, secondReverseZ),
            SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_REVERSE_Z);
    if (relativeDepth > SIMPLE_DDGI_RECEIVER_SURFACE_MAXIMUM_RELATIVE_DEPTH)
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_DEPTH;

    vec3 delta = secondPosition - firstPosition;
    float separation = length(delta);
    float cameraDistance = max(
        distance(firstPosition, cameraPosition),
        distance(secondPosition, cameraPosition));
    float safeFootprint = max(
        pixelFootprint,
        SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_WORLD_TOLERANCE);
    float worldTolerance = max(
        safeFootprint * (float(max(maximumScale, 1u)) * 2.0 + 2.0),
        max(
            cameraDistance *
                SIMPLE_DDGI_RECEIVER_SURFACE_CAMERA_DISTANCE_SCALE,
            SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_WORLD_TOLERANCE));
    if (!SimpleDdgiReceiverSurfaceFinite(separation) ||
        separation > worldTolerance)
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_POSITION;
    }

    float planeTolerance = max(
        safeFootprint * SIMPLE_DDGI_RECEIVER_SURFACE_PLANE_FOOTPRINT_SCALE,
        max(
            cameraDistance *
                SIMPLE_DDGI_RECEIVER_SURFACE_PLANE_CAMERA_DISTANCE_SCALE,
            SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_PLANE_TOLERANCE));
    if (abs(dot(delta, firstNormal)) > planeTolerance ||
        abs(dot(delta, secondNormal)) > planeTolerance)
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_PLANE;
    }
    if (dot(firstNormal, secondNormal) <
        SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_NORMAL_DOT)
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NORMAL;
    }
    return SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED;
}

uint SimpleDdgiReceiverSurfaceEvaluatePacked(
    uvec2 firstPacked,
    uvec2 firstCacheCoordinate,
    uint firstScale,
    uvec2 secondPacked,
    uvec2 secondCacheCoordinate,
    uint secondScale,
    mat4 inverseViewProjection,
    uvec2 screenExtent,
    vec3 cameraPosition)
{
    SimpleDdgiReceiverSurface first;
    SimpleDdgiReceiverSurface second;
    if (!SimpleDdgiReceiverSurfaceDecode(firstPacked, first) ||
        !SimpleDdgiReceiverSurfaceDecode(secondPacked, second))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;
    }

    uvec2 firstPixel;
    uvec2 secondPixel;
    if (!SimpleDdgiReceiverSurfaceResolvePixel(
            firstCacheCoordinate,
            firstScale,
            first,
            screenExtent,
            firstPixel) ||
        !SimpleDdgiReceiverSurfaceResolvePixel(
            secondCacheCoordinate,
            secondScale,
            second,
            screenExtent,
            secondPixel))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;
    }

    vec3 firstPosition;
    vec3 secondPosition;
    if (!SimpleDdgiReceiverSurfaceReconstructPosition(
            firstPixel,
            first.ReverseZ,
            inverseViewProjection,
            screenExtent,
            firstPosition) ||
        !SimpleDdgiReceiverSurfaceReconstructPosition(
            secondPixel,
            second.ReverseZ,
            inverseViewProjection,
            screenExtent,
            secondPosition))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE;
    }

    float footprint = max(
        SimpleDdgiReceiverSurfacePixelFootprint(
            firstPixel,
            first.ReverseZ,
            firstPosition,
            inverseViewProjection,
            screenExtent),
        SimpleDdgiReceiverSurfacePixelFootprint(
            secondPixel,
            second.ReverseZ,
            secondPosition,
            inverseViewProjection,
            screenExtent));
    return SimpleDdgiReceiverSurfaceEvaluateDecoded(
        first.ReverseZ,
        firstPosition,
        first.GeometricNormal,
        second.ReverseZ,
        secondPosition,
        second.GeometricNormal,
        footprint,
        max(firstScale, secondScale),
        cameraPosition);
}

uint SimpleDdgiReceiverSurfaceEvaluateFragment(
    uvec2 cachedPacked,
    uvec2 cacheCoordinate,
    uint cacheScale,
    uvec2 fragmentPixel,
    float fragmentReverseZ,
    vec3 fragmentWorldPosition,
    vec3 fragmentGeometricNormal,
    mat4 inverseProjection,
    mat4 inverseView,
    uvec2 screenExtent,
    vec3 cameraPosition)
{
    SimpleDdgiReceiverSurface cached;
    if (!SimpleDdgiReceiverSurfaceDecode(cachedPacked, cached))
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;

    float fragmentNormalLengthSquared = dot(
        fragmentGeometricNormal,
        fragmentGeometricNormal);
    if (!SimpleDdgiReceiverSurfaceFinite(fragmentReverseZ) ||
        !(fragmentReverseZ > 0.0) ||
        !SimpleDdgiReceiverSurfaceFinite(fragmentWorldPosition) ||
        !SimpleDdgiReceiverSurfaceFinite(fragmentGeometricNormal) ||
        !(fragmentNormalLengthSquared > 0.0) ||
        any(greaterThanEqual(fragmentPixel, screenExtent)))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE;
    }

    uvec2 cachedPixel;
    if (!SimpleDdgiReceiverSurfaceResolvePixel(
            cacheCoordinate,
            cacheScale,
            cached,
            screenExtent,
            cachedPixel))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;
    }

    vec3 cachedPosition;
    if (!SimpleDdgiReceiverSurfaceReconstructForwardPosition(
            cachedPixel,
            cached.ReverseZ,
            inverseProjection,
            inverseView,
            screenExtent,
            cachedPosition))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE;
    }

    // fragmentWorldPosition is the authoritative rasterized receiver. Avoid
    // reconstructing it and two synthetic neighbors a second time. Using only
    // the cached representative's footprint can tighten acceptance, but can
    // never turn a prior rejection into a cache hit; the exact fallback
    // therefore preserves correctness at discontinuities.
    float footprint = SimpleDdgiReceiverSurfaceForwardPixelFootprint(
        cachedPixel,
        cached.ReverseZ,
        cachedPosition,
        inverseProjection,
        inverseView,
        screenExtent);
    return SimpleDdgiReceiverSurfaceEvaluateDecoded(
        cached.ReverseZ,
        cachedPosition,
        cached.GeometricNormal,
        fragmentReverseZ,
        fragmentWorldPosition,
        normalize(fragmentGeometricNormal),
        footprint,
        cacheScale,
        cameraPosition);
}

// The half-resolution cache representative is constrained to the fragment's
// 2x2 block. Resolve already proved world position, plane, and footprint
// continuity before publication, so consumption needs only screen-local depth
// and normal discontinuity checks.
uint SimpleDdgiReceiverSurfaceEvaluateFragmentScreenLocal(
    uvec2 cachedPacked,
    uvec2 cacheCoordinate,
    uint cacheScale,
    uvec2 fragmentPixel,
    float fragmentReverseZ,
    vec3 fragmentWorldPosition,
    vec3 fragmentGeometricNormal,
    uvec2 screenExtent)
{
    SimpleDdgiReceiverSurface cached;
    if (!SimpleDdgiReceiverSurfaceDecode(cachedPacked, cached))
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;

    uvec2 cachedPixel;
    if (!SimpleDdgiReceiverSurfaceResolvePixel(
            cacheCoordinate,
            cacheScale,
            cached,
            screenExtent,
            cachedPixel))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID;
    }

    float fragmentNormalLengthSquared = dot(
        fragmentGeometricNormal,
        fragmentGeometricNormal);
    if (!SimpleDdgiReceiverSurfaceFinite(fragmentReverseZ) ||
        !(fragmentReverseZ > 0.0) ||
        !SimpleDdgiReceiverSurfaceFinite(fragmentWorldPosition) ||
        !SimpleDdgiReceiverSurfaceFinite(fragmentGeometricNormal) ||
        !SimpleDdgiReceiverSurfaceFinite(fragmentNormalLengthSquared) ||
        !(fragmentNormalLengthSquared > 0.0) ||
        any(greaterThanEqual(fragmentPixel, screenExtent)))
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE;
    }

    float relativeDepth = abs(cached.ReverseZ - fragmentReverseZ) /
        max(max(cached.ReverseZ, fragmentReverseZ),
            SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_REVERSE_Z);
    if (!SimpleDdgiReceiverSurfaceFinite(relativeDepth) ||
        relativeDepth > SIMPLE_DDGI_RECEIVER_SURFACE_MAXIMUM_RELATIVE_DEPTH)
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_DEPTH;
    }

    vec3 fragmentNormal = fragmentGeometricNormal *
        inversesqrt(fragmentNormalLengthSquared);
    if (dot(cached.GeometricNormal, fragmentNormal) <
        SIMPLE_DDGI_RECEIVER_SURFACE_MINIMUM_NORMAL_DOT)
    {
        return SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NORMAL;
    }
    return SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED;
}

#endif
