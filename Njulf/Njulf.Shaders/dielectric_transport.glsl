#ifndef NJULF_DIELECTRIC_TRANSPORT_GLSL
#define NJULF_DIELECTRIC_TRANSPORT_GLSL

#include "gi_material_transport.glsl"

const uint DIELECTRIC_MAX_MEDIA_DEPTH = 4u;
const uint DIELECTRIC_MAX_INTERFACES = 8u;
const uint DIELECTRIC_MAX_CANDIDATES_PER_INTERFACE = 64u;
const float DIELECTRIC_DELTA_ROUGHNESS_THRESHOLD = 0.02;

const uint DIELECTRIC_FALLBACK_NONE = 0u;
const uint DIELECTRIC_FALLBACK_INVALID_INPUT = 1u;
const uint DIELECTRIC_FALLBACK_STACK_OVERFLOW = 2u;
const uint DIELECTRIC_FALLBACK_STACK_UNDERFLOW = 3u;
const uint DIELECTRIC_FALLBACK_BOUNDARY_MISMATCH = 4u;
const uint DIELECTRIC_FALLBACK_INTERFACE_BUDGET = 5u;
const uint DIELECTRIC_FALLBACK_CANDIDATE_BUDGET = 6u;
const uint DIELECTRIC_FALLBACK_UNSUPPORTED_TOPOLOGY = 7u;
const uint DIELECTRIC_FALLBACK_PARTIAL_STACK = 8u;

const uint OPTICAL_BOUNDARY_CLOSED_VOLUME = 0u;
const uint OPTICAL_BOUNDARY_WATER_SURFACE = 1u;
const uint OPTICAL_BOUNDARY_KIND_MASK = 0x3u;
const uint OPTICAL_CAUSTIC_POLICY_SHIFT = 8u;
const uint OPTICAL_CAUSTIC_POLICY_MASK = 0x7u <<
    OPTICAL_CAUSTIC_POLICY_SHIFT;
const uint OPTICAL_VOLUME_TRANSMISSION_FLAG = 1u << 16u;
const uint OPTICAL_WATER_SURFACE_FLAG = 1u << 17u;

struct DielectricBoundary
{
    uint BoundaryIdentity;
    uint MaterialRevision;
    float Ior;
    vec3 AbsorptionCoefficient;
    uint BoundaryKind;
};

struct DielectricInterface
{
    float IncidentIor;
    float TransmittedIor;
    uint Entering;
    uint DepthBefore;
    uint DepthAfterTransmission;
};

struct DielectricMediaStack
{
    uint Count;
    uint InterfaceCount;
    uint FallbackReason;
    uint Padding;
    uvec4 BoundaryIdentities;
    uvec4 MaterialRevisions;
    vec4 Iors;
    uvec4 BoundaryKinds;
    vec4 EntryPathDistances;
    vec3 Absorption[DIELECTRIC_MAX_MEDIA_DEPTH];
};

bool DielectricFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool DielectricFinite(vec2 value)
{
    return DielectricFinite(value.x) && DielectricFinite(value.y);
}

bool DielectricFinite(vec3 value)
{
    return DielectricFinite(value.x) && DielectricFinite(value.y) &&
        DielectricFinite(value.z);
}

bool DielectricFinite(vec4 value)
{
    return DielectricFinite(value.xyz) && DielectricFinite(value.w);
}

void DielectricInitialize(out DielectricMediaStack stack)
{
    stack.Count = 0u;
    stack.InterfaceCount = 0u;
    stack.FallbackReason = DIELECTRIC_FALLBACK_NONE;
    stack.Padding = 0u;
    stack.BoundaryIdentities = uvec4(0u);
    stack.MaterialRevisions = uvec4(0u);
    stack.Iors = vec4(1.0);
    stack.BoundaryKinds = uvec4(OPTICAL_BOUNDARY_CLOSED_VOLUME);
    stack.EntryPathDistances = vec4(0.0);
    for (uint index = 0u; index < DIELECTRIC_MAX_MEDIA_DEPTH; ++index)
        stack.Absorption[index] = vec3(0.0);
}

float DielectricCurrentIor(DielectricMediaStack stack)
{
    return stack.Count == 0u ? 1.0 : stack.Iors[stack.Count - 1u];
}

vec3 DielectricCurrentAbsorption(DielectricMediaStack stack)
{
    return stack.Count == 0u
        ? vec3(0.0) : stack.Absorption[stack.Count - 1u];
}

bool DielectricBoundaryValid(DielectricBoundary boundary)
{
    return boundary.BoundaryIdentity != 0u &&
        boundary.MaterialRevision != 0u && DielectricFinite(boundary.Ior) &&
        boundary.Ior >= 1.0 && boundary.Ior <= 4.0 &&
        DielectricFinite(boundary.AbsorptionCoefficient) &&
        all(greaterThanEqual(boundary.AbsorptionCoefficient, vec3(0.0))) &&
        boundary.BoundaryKind <= OPTICAL_BOUNDARY_WATER_SURFACE;
}

bool DielectricFail(inout DielectricMediaStack stack, uint reason)
{
    stack.FallbackReason = reason;
    return false;
}

bool DielectricPrepareInterface(
    inout DielectricMediaStack stack,
    DielectricBoundary boundary,
    bool frontFacing,
    uint maximumInterfaces,
    uint maximumMediaDepth,
    out DielectricInterface interfaceData)
{
    interfaceData.IncidentIor = 1.0;
    interfaceData.TransmittedIor = 1.0;
    interfaceData.Entering = 0u;
    interfaceData.DepthBefore = stack.Count;
    interfaceData.DepthAfterTransmission = stack.Count;
    if (stack.FallbackReason != DIELECTRIC_FALLBACK_NONE)
        return false;
    if (!DielectricBoundaryValid(boundary))
        return DielectricFail(stack, DIELECTRIC_FALLBACK_INVALID_INPUT);
    uint interfaceLimit = clamp(
        maximumInterfaces, 1u, DIELECTRIC_MAX_INTERFACES);
    uint mediaDepthLimit = clamp(
        maximumMediaDepth, 1u, DIELECTRIC_MAX_MEDIA_DEPTH);
    if (stack.InterfaceCount >= interfaceLimit)
        return DielectricFail(stack, DIELECTRIC_FALLBACK_INTERFACE_BUDGET);

    if (frontFacing)
    {
        if (stack.Count >= mediaDepthLimit)
            return DielectricFail(stack, DIELECTRIC_FALLBACK_STACK_OVERFLOW);
        if (stack.Count > 0u &&
            stack.BoundaryIdentities[stack.Count - 1u] ==
                boundary.BoundaryIdentity)
        {
            return DielectricFail(stack,
                DIELECTRIC_FALLBACK_BOUNDARY_MISMATCH);
        }
        interfaceData.IncidentIor = DielectricCurrentIor(stack);
        interfaceData.TransmittedIor = boundary.Ior;
        interfaceData.Entering = 1u;
        interfaceData.DepthAfterTransmission = stack.Count + 1u;
        return true;
    }

    if (stack.Count == 0u)
        return DielectricFail(stack, DIELECTRIC_FALLBACK_STACK_UNDERFLOW);
    uint top = stack.Count - 1u;
    if (stack.BoundaryIdentities[top] != boundary.BoundaryIdentity ||
        stack.MaterialRevisions[top] != boundary.MaterialRevision ||
        stack.BoundaryKinds[top] != boundary.BoundaryKind)
    {
        return DielectricFail(stack, DIELECTRIC_FALLBACK_BOUNDARY_MISMATCH);
    }
    interfaceData.IncidentIor = stack.Iors[top];
    interfaceData.TransmittedIor = top > 0u ? stack.Iors[top - 1u] : 1.0;
    interfaceData.DepthAfterTransmission = top;
    return true;
}

bool DielectricCommitTransmission(
    inout DielectricMediaStack stack,
    DielectricBoundary boundary,
    DielectricInterface interfaceData,
    float pathDistance)
{
    if (stack.FallbackReason != DIELECTRIC_FALLBACK_NONE)
        return false;
    if (!DielectricFinite(pathDistance) || pathDistance < 0.0 ||
        interfaceData.DepthBefore != stack.Count)
    {
        return DielectricFail(stack, DIELECTRIC_FALLBACK_INVALID_INPUT);
    }

    if (interfaceData.Entering != 0u)
    {
        if (stack.Count >= DIELECTRIC_MAX_MEDIA_DEPTH)
            return DielectricFail(stack, DIELECTRIC_FALLBACK_STACK_OVERFLOW);
        uint index = stack.Count++;
        stack.BoundaryIdentities[index] = boundary.BoundaryIdentity;
        stack.MaterialRevisions[index] = boundary.MaterialRevision;
        stack.Iors[index] = boundary.Ior;
        stack.BoundaryKinds[index] = boundary.BoundaryKind;
        stack.EntryPathDistances[index] = pathDistance;
        stack.Absorption[index] = boundary.AbsorptionCoefficient;
    }
    else
    {
        if (stack.Count == 0u ||
            stack.BoundaryIdentities[stack.Count - 1u] !=
                boundary.BoundaryIdentity)
        {
            return DielectricFail(stack,
                DIELECTRIC_FALLBACK_BOUNDARY_MISMATCH);
        }
        uint index = --stack.Count;
        stack.BoundaryIdentities[index] = 0u;
        stack.MaterialRevisions[index] = 0u;
        stack.Iors[index] = 1.0;
        stack.BoundaryKinds[index] = OPTICAL_BOUNDARY_CLOSED_VOLUME;
        stack.EntryPathDistances[index] = 0.0;
        stack.Absorption[index] = vec3(0.0);
    }
    ++stack.InterfaceCount;
    return stack.Count == interfaceData.DepthAfterTransmission;
}

bool DielectricCommitReflection(
    inout DielectricMediaStack stack,
    DielectricInterface interfaceData)
{
    if (stack.FallbackReason != DIELECTRIC_FALLBACK_NONE)
        return false;
    if (interfaceData.DepthBefore != stack.Count)
        return DielectricFail(stack, DIELECTRIC_FALLBACK_INVALID_INPUT);
    ++stack.InterfaceCount;
    return true;
}

vec3 DielectricAbsorptionCoefficient(
    vec3 attenuationColor,
    float attenuationDistance)
{
    if (!DielectricFinite(attenuationColor) ||
        !DielectricFinite(attenuationDistance) || attenuationDistance <= 0.0)
    {
        return vec3(0.0);
    }
    return -log(clamp(attenuationColor, vec3(1.0e-6), vec3(1.0))) /
        attenuationDistance;
}

vec3 DielectricBeerLambert(vec3 absorptionCoefficient, float distance)
{
    return exp(-max(absorptionCoefficient, vec3(0.0)) * max(distance, 0.0));
}

float DielectricExactFresnel(
    float cosineIncident,
    float incidentIor,
    float transmittedIor,
    out bool totalInternalReflection)
{
    float cosI = clamp(abs(cosineIncident), 0.0, 1.0);
    float eta = incidentIor / transmittedIor;
    float sinTransmittedSquared = eta * eta * max(0.0, 1.0 - cosI * cosI);
    totalInternalReflection = sinTransmittedSquared >= 1.0;
    if (totalInternalReflection)
        return 1.0;
    float cosT = sqrt(max(0.0, 1.0 - sinTransmittedSquared));
    float rsDenominator = transmittedIor * cosI + incidentIor * cosT;
    float rpDenominator = incidentIor * cosI + transmittedIor * cosT;
    if (rsDenominator <= 1.0e-12 || rpDenominator <= 1.0e-12)
    {
        totalInternalReflection = true;
        return 1.0;
    }
    float rs = (transmittedIor * cosI - incidentIor * cosT) /
        rsDenominator;
    float rp = (incidentIor * cosI - transmittedIor * cosT) /
        rpDenominator;
    return clamp(0.5 * (rs * rs + rp * rp), 0.0, 1.0);
}

bool DielectricTryRefract(
    vec3 incidentDirection,
    vec3 orientedGeometricNormal,
    float incidentIor,
    float transmittedIor,
    out vec3 transmittedDirection,
    out float reflectance)
{
    vec3 incident = normalize(incidentDirection);
    vec3 normal = normalize(orientedGeometricNormal);
    float cosineIncident = clamp(dot(-incident, normal), 0.0, 1.0);
    bool totalInternalReflection;
    reflectance = DielectricExactFresnel(
        cosineIncident, incidentIor, transmittedIor,
        totalInternalReflection);
    if (totalInternalReflection)
    {
        transmittedDirection = vec3(0.0);
        return false;
    }
    transmittedDirection = refract(
        incident, normal, incidentIor / transmittedIor);
    float lengthSquared = dot(transmittedDirection, transmittedDirection);
    if (lengthSquared <= 1.0e-12)
    {
        transmittedDirection = vec3(0.0);
        reflectance = 1.0;
        return false;
    }
    transmittedDirection *= inversesqrt(lengthSquared);
    return true;
}

vec3 DielectricRgbIors(float centralIor, float dispersion)
{
    // KHR_materials_dispersion stores 20 / Vd. This is its recommended
    // bounded real-time RGB approximation (red, green, blue).
    float halfSpread = (centralIor - 1.0) * 0.025 * max(dispersion, 0.0);
    return vec3(
        max(1.0, centralIor - halfSpread),
        centralIor,
        min(4.0, centralIor + halfSpread));
}

float DielectricGgxDistribution(float nDotM, float alpha)
{
    float alphaSquared = alpha * alpha;
    float denominator = nDotM * nDotM * (alphaSquared - 1.0) + 1.0;
    return alphaSquared /
        max(GI_MATERIAL_PI * denominator * denominator, 1.0e-12);
}

float DielectricSmithG1(float nDotDirection, float alpha)
{
    float nDot = max(abs(nDotDirection), 1.0e-6);
    float tangentSquared = max(0.0, 1.0 - nDot * nDot) /
        (nDot * nDot);
    return 2.0 / (1.0 + sqrt(1.0 + alpha * alpha * tangentSquared));
}

vec3 DielectricSampleGgxVisibleNormal(
    vec3 localView,
    float alpha,
    float u1,
    float u2)
{
    vec3 stretchedView = normalize(vec3(
        alpha * localView.x,
        alpha * localView.y,
        localView.z));
    float lensq = dot(stretchedView.xy, stretchedView.xy);
    vec3 tangent1 = lensq > 1.0e-12
        ? vec3(-stretchedView.y, stretchedView.x, 0.0) * inversesqrt(lensq)
        : vec3(1.0, 0.0, 0.0);
    vec3 tangent2 = cross(stretchedView, tangent1);
    float radius = sqrt(clamp(u1, 0.0, 1.0));
    float phi = 2.0 * GI_MATERIAL_PI * u2;
    float t1 = radius * cos(phi);
    float t2 = radius * sin(phi);
    float blend = 0.5 * (1.0 + stretchedView.z);
    t2 = mix(sqrt(max(0.0, 1.0 - t1 * t1)), t2, blend);
    float normalZ = sqrt(max(0.0, 1.0 - t1 * t1 - t2 * t2));
    vec3 stretchedNormal = t1 * tangent1 + t2 * tangent2 +
        normalZ * stretchedView;
    return normalize(vec3(
        alpha * stretchedNormal.x,
        alpha * stretchedNormal.y,
        max(stretchedNormal.z, 0.0)));
}

/// Samples either the delta interface or a GGX VNDF rough dielectric. The
/// returned scalar weight already includes the selected branch probability,
/// direction PDF, cosine, and radiance eta conversion.
bool DielectricSampleInterface(
    vec3 incidentDirection,
    vec3 orientedNormal,
    float roughness,
    float incidentIor,
    float transmittedIor,
    float randomMicrofacet0,
    float randomMicrofacet1,
    float randomBranch,
    out vec3 outgoingDirection,
    out float pathWeight,
    out bool transmitted,
    out bool totalInternalReflection)
{
    outgoingDirection = vec3(0.0);
    pathWeight = 0.0;
    transmitted = false;
    totalInternalReflection = false;
    vec3 normal = normalize(orientedNormal);
    vec3 view = normalize(-incidentDirection);
    float nDotV = dot(normal, view);
    if (!DielectricFinite(view) || !DielectricFinite(normal) ||
        nDotV <= 1.0e-5 || incidentIor < 1.0 || transmittedIor < 1.0)
    {
        return false;
    }

    if (roughness < DIELECTRIC_DELTA_ROUGHNESS_THRESHOLD)
    {
        float fresnel = DielectricExactFresnel(
            nDotV, incidentIor, transmittedIor,
            totalInternalReflection);
        bool reflection = totalInternalReflection || randomBranch < fresnel;
        if (reflection)
        {
            float probability = totalInternalReflection ? 1.0 : fresnel;
            outgoingDirection = normalize(reflect(incidentDirection, normal));
            pathWeight = fresnel / max(probability, 1.0e-6);
            return DielectricFinite(outgoingDirection) &&
                DielectricFinite(pathWeight);
        }

        float probability = max(1.0 - fresnel, 1.0e-6);
        if (!DielectricTryRefract(
                incidentDirection, normal, incidentIor, transmittedIor,
                outgoingDirection, fresnel))
        {
            totalInternalReflection = true;
            outgoingDirection = normalize(reflect(incidentDirection, normal));
            pathWeight = 1.0;
            return true;
        }
        pathWeight = ((1.0 - fresnel) / probability) *
            ((incidentIor * incidentIor) /
             (transmittedIor * transmittedIor));
        transmitted = true;
        return DielectricFinite(pathWeight);
    }

    float alpha = max(roughness * roughness, 0.0004);
    vec3 reference = abs(normal.z) < 0.9
        ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(reference, normal));
    vec3 bitangent = cross(normal, tangent);
    vec3 localView = vec3(
        dot(view, tangent), dot(view, bitangent), nDotV);
    vec3 localMicroNormal = DielectricSampleGgxVisibleNormal(
        localView,
        alpha,
        clamp(randomMicrofacet0, 1.0e-6, 1.0 - 1.0e-6),
        randomMicrofacet1);
    vec3 microNormal = normalize(
        tangent * localMicroNormal.x +
        bitangent * localMicroNormal.y +
        normal * localMicroNormal.z);
    float vDotM = max(dot(view, microNormal), 1.0e-6);
    float nDotM = max(dot(normal, microNormal), 1.0e-6);
    float distribution = DielectricGgxDistribution(nDotM, alpha);
    float visibleNormalPdf = distribution *
        DielectricSmithG1(nDotV, alpha) * vDotM / nDotV;
    float fresnel = DielectricExactFresnel(
        vDotM, incidentIor, transmittedIor,
        totalInternalReflection);
    bool reflection = totalInternalReflection || randomBranch < fresnel;

    if (reflection)
    {
        outgoingDirection = normalize(reflect(incidentDirection, microNormal));
        float nDotO = dot(normal, outgoingDirection);
        if (nDotO <= 1.0e-5)
            return false;
        float directionPdf = visibleNormalPdf /
            max(4.0 * vDotM, 1.0e-12);
        float branchProbability = totalInternalReflection ? 1.0 : fresnel;
        directionPdf *= branchProbability;
        float geometry = DielectricSmithG1(nDotV, alpha) *
            DielectricSmithG1(nDotO, alpha);
        float brdf = fresnel * distribution * geometry /
            max(4.0 * nDotV * nDotO, 1.0e-12);
        pathWeight = brdf * nDotO / max(directionPdf, 1.0e-12);
        return DielectricFinite(pathWeight) && pathWeight >= 0.0;
    }

    outgoingDirection = refract(
        incidentDirection, microNormal, incidentIor / transmittedIor);
    float outgoingLengthSquared = dot(outgoingDirection, outgoingDirection);
    if (outgoingLengthSquared <= 1.0e-12)
        return false;
    outgoingDirection *= inversesqrt(outgoingLengthSquared);
    float nDotO = abs(dot(normal, outgoingDirection));
    float oDotM = dot(outgoingDirection, microNormal);
    if (nDotO <= 1.0e-5 || oDotM >= -1.0e-6)
        return false;
    float denominator = incidentIor * vDotM + transmittedIor * oDotM;
    float denominatorSquared = denominator * denominator;
    if (denominatorSquared <= 1.0e-12)
        return false;
    float jacobian = transmittedIor * transmittedIor * abs(oDotM) /
        denominatorSquared;
    float directionPdf = visibleNormalPdf * jacobian *
        max(1.0 - fresnel, 1.0e-6);
    float geometry = DielectricSmithG1(nDotV, alpha) *
        DielectricSmithG1(nDotO, alpha);
    float btdf = (1.0 - fresnel) * distribution * geometry *
        transmittedIor * transmittedIor * vDotM * abs(oDotM) /
        max(nDotV * nDotO * denominatorSquared, 1.0e-12);
    pathWeight = btdf * nDotO / max(directionPdf, 1.0e-12);
    transmitted = true;
    return DielectricFinite(pathWeight) && pathWeight >= 0.0;
}

uint OpticalMaterialPackedFlags(GPUMaterialExtensionData extensionData)
{
    return uint(extensionData.Padding0);
}

uint OpticalMaterialBoundaryKind(GPUMaterialExtensionData extensionData)
{
    return OpticalMaterialPackedFlags(extensionData) &
        OPTICAL_BOUNDARY_KIND_MASK;
}

uint OpticalMaterialCausticPolicy(GPUMaterialExtensionData extensionData)
{
    return (OpticalMaterialPackedFlags(extensionData) &
        OPTICAL_CAUSTIC_POLICY_MASK) >> OPTICAL_CAUSTIC_POLICY_SHIFT;
}

vec2 OpticalMaterialWaterVelocity0(GPUMaterialExtensionData extensionData)
{
    return unpackHalf2x16(uint(extensionData.Padding1));
}

vec2 OpticalMaterialWaterVelocity1(GPUMaterialExtensionData extensionData)
{
    return unpackHalf2x16(uint(extensionData.Padding2));
}

vec2 OpticalMaterialWaterUvScales(GPUMaterialExtensionData extensionData)
{
    return unpackHalf2x16(uint(extensionData.Padding3));
}

#endif // NJULF_DIELECTRIC_TRANSPORT_GLSL
