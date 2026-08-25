#ifndef NJULF_THICK_TRANSMISSION_TRANSPORT_GLSL
#define NJULF_THICK_TRANSMISSION_TRANSPORT_GLSL

#include "ray_query_surface.glsl"
#include "dielectric_transport.glsl"

const uint THICK_TRANSMISSION_SPECTRAL_CENTRAL = 0u;
const uint THICK_TRANSMISSION_SPECTRAL_RED = 1u;
const uint THICK_TRANSMISSION_SPECTRAL_GREEN = 2u;
const uint THICK_TRANSMISSION_SPECTRAL_BLUE = 3u;

struct ThickTransmissionPathResult
{
    uint Valid;
    uint Miss;
    uint FallbackReason;
    uint InterfaceCount;
    uint CandidateCount;
    uint PathSignature;
    float PathLength;
    float RemainingDistance;
    vec3 Direction;
    vec3 Throughput;
    RayQuerySurfaceHit TerminalHit;
};

uint ThickTransmissionHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

float ThickTransmissionRandom(uint seed, uint interfaceIndex, uint dimension)
{
    uint value = ThickTransmissionHash(
        seed ^ (interfaceIndex + 1u) * 0x9e3779b9u ^
        (dimension + 1u) * 0x85ebca6bu);
    return (float(value >> 8u) + 0.5) / 16777216.0;
}

uint ThickTransmissionPathHash(uint state, uint value)
{
    uint result = ThickTransmissionHash(state ^ value ^ 0x9e3779b9u);
    return result == 0u ? 1u : result;
}

float ThickTransmissionSpectralIor(
    float centralIor,
    float dispersion,
    bool dispersionEnabled,
    uint spectralChannel)
{
    if (!dispersionEnabled || spectralChannel ==
            THICK_TRANSMISSION_SPECTRAL_CENTRAL)
    {
        return centralIor;
    }
    vec3 rgbIors = DielectricRgbIors(centralIor, dispersion);
    return spectralChannel == THICK_TRANSMISSION_SPECTRAL_RED
        ? rgbIors.r
        : spectralChannel == THICK_TRANSMISSION_SPECTRAL_BLUE
            ? rgbIors.b
            : rgbIors.g;
}

bool ThickTransmissionResolveBoundary(
    uint stableBoundaryIdentity,
    GPUMaterialData material,
    bool dispersionEnabled,
    uint spectralChannel,
    out DielectricBoundary boundary,
    out GPUMaterialExtensionData extensionData,
    out float roughness)
{
    boundary.BoundaryIdentity = stableBoundaryIdentity;
    boundary.MaterialRevision = material.MaterialRevision;
    boundary.Ior = 1.5;
    boundary.AbsorptionCoefficient = vec3(0.0);
    boundary.BoundaryKind = OPTICAL_BOUNDARY_CLOSED_VOLUME;
    roughness = clamp(material.MetallicRoughnessAO.y, 0.0, 1.0);
    if (stableBoundaryIdentity == 0u || material.MaterialRevision == 0u ||
        material.ExtensionDataIndex < 0 ||
        !GiMaterialHasFlag(
            material.TransportFlags, GI_MATERIAL_VOLUME_TRANSMISSION))
    {
        return false;
    }

    extensionData = ReadMaterialExtension(uint(material.ExtensionDataIndex));
    if (!DielectricFinite(extensionData.Transmission) ||
        !DielectricFinite(extensionData.AttenuationColor) ||
        extensionData.Transmission.x <= 0.0 ||
        extensionData.Transmission.y < 1.0 ||
        extensionData.Transmission.y > 4.0)
    {
        return false;
    }
    boundary.Ior = ThickTransmissionSpectralIor(
        extensionData.Transmission.y,
        extensionData.Dispersion.x,
        dispersionEnabled,
        spectralChannel);
    boundary.AbsorptionCoefficient = DielectricAbsorptionCoefficient(
        clamp(extensionData.AttenuationColor.rgb, vec3(0.0), vec3(1.0)),
        extensionData.Transmission.w);
    boundary.BoundaryKind = OpticalMaterialBoundaryKind(extensionData);
    return DielectricBoundaryValid(boundary);
}

bool ThickTransmissionResolveHitBoundary(
    RayQuerySurfaceHit hit,
    bool dispersionEnabled,
    uint spectralChannel,
    out DielectricBoundary boundary,
    out GPUMaterialExtensionData extensionData,
    out float roughness)
{
    if (!ThickTransmissionResolveBoundary(
            hit.Instance.StableInstanceIdentity,
            hit.Material,
            dispersionEnabled,
            spectralChannel,
            boundary,
            extensionData,
            roughness))
    {
        return false;
    }
    roughness = RayQuerySurfaceSampleMetallicRoughness(hit).y;
    return true;
}

vec3 ThickTransmissionWaterNormal(
    RayQuerySurfaceHit hit,
    GPUMaterialExtensionData extensionData,
    vec3 orientedGeometricNormal,
    float timeSeconds)
{
    if (OpticalMaterialBoundaryKind(extensionData) !=
            OPTICAL_BOUNDARY_WATER_SURFACE ||
        hit.Material.NormalTextureIndex < FIRST_TEXTURE_INDEX ||
        hit.Material.NormalTextureIndex >= FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        return orientedGeometricNormal;
    }

    vec3 reference = abs(orientedGeometricNormal.z) < 0.9
        ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(reference, orientedGeometricNormal));
    vec3 bitangent = cross(orientedGeometricNormal, tangent);
    vec2 baseUv = int(round(hit.Material.TextureTexCoordSets.y)) == 1
        ? hit.Uv1 : hit.Uv0;
    baseUv = GiCausticTextureTransform(
        baseUv,
        hit.Material.NormalOffsetScale,
        hit.Material.TextureRotations.y);
    vec2 scales = max(
        OpticalMaterialWaterUvScales(extensionData), vec2(0.001));
    vec2 uv0 = baseUv * scales.x +
        OpticalMaterialWaterVelocity0(extensionData) * timeSeconds;
    vec2 uv1 = baseUv * scales.y +
        OpticalMaterialWaterVelocity1(extensionData) * timeSeconds;
    vec2 wave0 = textureLod(
        BindlessTextures[nonuniformEXT(hit.Material.NormalTextureIndex)],
        uv0, 0.0).xy * 2.0 - 1.0;
    vec2 wave1 = textureLod(
        BindlessTextures[nonuniformEXT(hit.Material.NormalTextureIndex)],
        uv1, 0.0).xy * 2.0 - 1.0;
    vec2 wave = 0.5 * (wave0 + wave1) *
        max(hit.Material.NormalScaleBias.x, 0.0);
    vec3 normal = normalize(orientedGeometricNormal +
        tangent * wave.x + bitangent * wave.y);
    return dot(normal, orientedGeometricNormal) > 0.0
        ? normal : orientedGeometricNormal;
}

bool ThickTransmissionMediaCanTerminate(DielectricMediaStack stack)
{
    return stack.Count == 0u ||
        (stack.Count == 1u &&
         stack.BoundaryKinds[0] == OPTICAL_BOUNDARY_WATER_SURFACE);
}

bool ThickTransmissionScatterInterface(
    inout DielectricMediaStack stack,
    DielectricBoundary boundary,
    bool frontFacing,
    uint maximumInterfaces,
    uint maximumMediaDepth,
    vec3 incidentDirection,
    vec3 scatterNormal,
    float roughness,
    float pathDistance,
    uint randomSeed,
    out vec3 outgoingDirection,
    out float interfaceWeight)
{
    DielectricInterface interfaceData;
    if (!DielectricPrepareInterface(
            stack,
            boundary,
            frontFacing,
            maximumInterfaces,
            maximumMediaDepth,
            interfaceData))
    {
        return false;
    }
    bool transmitted;
    bool totalInternalReflection;
    uint interfaceIndex = stack.InterfaceCount;
    if (!DielectricSampleInterface(
            incidentDirection,
            scatterNormal,
            roughness,
            interfaceData.IncidentIor,
            interfaceData.TransmittedIor,
            ThickTransmissionRandom(randomSeed, interfaceIndex, 0u),
            ThickTransmissionRandom(randomSeed, interfaceIndex, 1u),
            ThickTransmissionRandom(randomSeed, interfaceIndex, 2u),
            outgoingDirection,
            interfaceWeight,
            transmitted,
            totalInternalReflection))
    {
        return false;
    }
    bool committed = transmitted
        ? DielectricCommitTransmission(
            stack, boundary, interfaceData, pathDistance)
        : DielectricCommitReflection(stack, interfaceData);
    if (!committed)
    {
        return false;
    }
    return true;
}

bool ThickTransmissionTracePath(
    vec3 initialPosition,
    vec3 incidentDirection,
    vec3 initialScatterNormal,
    DielectricBoundary initialBoundary,
    bool initialFrontFacing,
    float initialRoughness,
    uint maximumInterfaces,
    uint maximumMediaDepth,
    uint maximumCandidatesPerInterface,
    float maximumDistance,
    uint randomSeed,
    float timeSeconds,
    bool dispersionEnabled,
    uint spectralChannel,
    out ThickTransmissionPathResult result)
{
    result.Valid = 0u;
    result.Miss = 0u;
    result.FallbackReason = DIELECTRIC_FALLBACK_NONE;
    result.InterfaceCount = 0u;
    result.CandidateCount = 0u;
    result.PathSignature = ThickTransmissionHash(randomSeed);
    result.PathLength = 0.0;
    result.RemainingDistance = maximumDistance;
    result.Direction = normalize(incidentDirection);
    result.Throughput = vec3(1.0);
    if (!DielectricFinite(initialPosition) ||
        !DielectricFinite(result.Direction) ||
        !DielectricFinite(initialScatterNormal) ||
        !DielectricFinite(maximumDistance) || maximumDistance <= 0.0 ||
        !DielectricBoundaryValid(initialBoundary))
    {
        result.FallbackReason = DIELECTRIC_FALLBACK_INVALID_INPUT;
        return false;
    }

    DielectricMediaStack stack;
    DielectricInitialize(stack);
    float interfaceWeight;
    if (!ThickTransmissionScatterInterface(
            stack,
            initialBoundary,
            initialFrontFacing,
            maximumInterfaces,
            maximumMediaDepth,
            result.Direction,
            normalize(initialScatterNormal),
            initialRoughness,
            0.0,
            randomSeed,
            result.Direction,
            interfaceWeight))
    {
        result.FallbackReason = stack.FallbackReason;
        return false;
    }
    result.Throughput *= interfaceWeight;
    result.PathSignature = ThickTransmissionPathHash(
        result.PathSignature,
        initialBoundary.BoundaryIdentity ^
        initialBoundary.MaterialRevision ^
        (initialFrontFacing ? 0x51ed270bu : 0x7f4a7c15u));
    vec3 origin = initialPosition + result.Direction *
        GI_CAUSTIC_RAY_EPSILON * 2.0;

    for (uint vertexIndex = 0u;
         vertexIndex < DIELECTRIC_MAX_INTERFACES;
         ++vertexIndex)
    {
        RayQuerySurfaceHit hit;
        uint candidateCount;
        bool candidateBudgetExceeded;
        bool found = RayQuerySurfaceTraceNearestBounded(
            origin,
            result.Direction,
            result.RemainingDistance,
            clamp(
                maximumCandidatesPerInterface,
                1u,
                DIELECTRIC_MAX_CANDIDATES_PER_INTERFACE),
            candidateCount,
            candidateBudgetExceeded,
            hit);
        result.CandidateCount += candidateCount;
        if (candidateBudgetExceeded)
        {
            result.FallbackReason = DIELECTRIC_FALLBACK_CANDIDATE_BUDGET;
            return false;
        }
        if (!found)
        {
            if (!ThickTransmissionMediaCanTerminate(stack))
            {
                result.FallbackReason = DIELECTRIC_FALLBACK_PARTIAL_STACK;
                return false;
            }
            result.Throughput *= DielectricBeerLambert(
                DielectricCurrentAbsorption(stack),
                result.RemainingDistance);
            result.PathLength += result.RemainingDistance;
            result.RemainingDistance = 0.0;
            result.InterfaceCount = stack.InterfaceCount;
            result.Miss = 1u;
            result.Valid = 1u;
            return DielectricFinite(result.Throughput);
        }

        result.Throughput *= DielectricBeerLambert(
            DielectricCurrentAbsorption(stack), hit.Distance);
        result.PathLength += hit.Distance;
        result.RemainingDistance -= hit.Distance;
        if (!DielectricFinite(result.Throughput) ||
            any(lessThan(result.Throughput, vec3(0.0))) ||
            result.RemainingDistance <= GI_CAUSTIC_RAY_EPSILON)
        {
            result.FallbackReason = DIELECTRIC_FALLBACK_INTERFACE_BUDGET;
            return false;
        }

        DielectricBoundary boundary;
        GPUMaterialExtensionData extensionData;
        float roughness;
        bool opticalBoundary = ThickTransmissionResolveHitBoundary(
            hit,
            dispersionEnabled,
            spectralChannel,
            boundary,
            extensionData,
            roughness);
        if (!opticalBoundary)
        {
            // An opaque surface can physically terminate the path while it is
            // inside a medium. The segment attenuation above is complete;
            // only an environment miss requires a closed stack.
            result.TerminalHit = hit;
            result.InterfaceCount = stack.InterfaceCount;
            result.Valid = 1u;
            return true;
        }

        vec3 orientedNormal = RayQuerySurfaceOrientedNormal(hit);
        vec3 scatterNormal = boundary.BoundaryKind ==
                OPTICAL_BOUNDARY_WATER_SURFACE
            ? ThickTransmissionWaterNormal(
                hit, extensionData, orientedNormal, timeSeconds)
            : orientedNormal;
        if (!ThickTransmissionScatterInterface(
                stack,
                boundary,
                hit.FrontFacing,
                maximumInterfaces,
                maximumMediaDepth,
                result.Direction,
                scatterNormal,
                roughness,
                result.PathLength,
                randomSeed,
                result.Direction,
                interfaceWeight))
        {
            result.FallbackReason = stack.FallbackReason;
            return false;
        }
        result.Throughput *= interfaceWeight;
        result.PathSignature = ThickTransmissionPathHash(
            result.PathSignature,
            boundary.BoundaryIdentity ^ boundary.MaterialRevision ^
            (hit.FrontFacing ? 0x51ed270bu : 0x7f4a7c15u));
        origin = hit.Position + result.Direction *
            GI_CAUSTIC_RAY_EPSILON * 2.0;
    }

    result.FallbackReason = DIELECTRIC_FALLBACK_INTERFACE_BUDGET;
    return false;
}

#endif // NJULF_THICK_TRANSMISSION_TRANSPORT_GLSL
