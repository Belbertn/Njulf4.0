#ifndef NJULF_GI_MATERIAL_TRANSPORT_GLSL
#define NJULF_GI_MATERIAL_TRANSPORT_GLSL

#include "material_alpha.glsl"

// Keep these values synchronized with GiMaterialTransportFlags.  Transport
// validity is explicit: zero-valued diffuse/emission profiles are physically
// meaningful and must never be treated as "missing" sentinels.
const uint GI_MATERIAL_BASE_STATISTICS_VALID = 1u << 0u;
const uint GI_MATERIAL_DIFFUSE_PROFILE_VALID = 1u << 1u;
const uint GI_MATERIAL_EMISSION_PROFILE_VALID = 1u << 2u;
const uint GI_MATERIAL_ALPHA_PROFILE_VALID = 1u << 3u;
const uint GI_MATERIAL_NORMAL_PROFILE_VALID = 1u << 4u;
const uint GI_MATERIAL_UNLIT = 1u << 5u;
const uint GI_MATERIAL_DOUBLE_SIDED = 1u << 6u;
const uint GI_MATERIAL_TRANSMISSION_REMOVES_OPAQUE_DIFFUSE = 1u << 7u;
const uint GI_MATERIAL_EMITS_INTO_GI = 1u << 8u;
const uint GI_MATERIAL_RECEIVES_INDIRECT_DIFFUSE = 1u << 9u;
const uint GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE = 1u << 10u;
const uint GI_MATERIAL_HAS_BASE_COLOR_TEXTURE = 1u << 11u;
const uint GI_MATERIAL_HAS_METALLIC_ROUGHNESS_TEXTURE = 1u << 12u;
const uint GI_MATERIAL_HAS_OCCLUSION_TEXTURE = 1u << 13u;
const uint GI_MATERIAL_HAS_EMISSIVE_TEXTURE = 1u << 14u;
const uint GI_MATERIAL_LEGACY_V1_FALLBACK = 1u << 15u;
const uint GI_MATERIAL_UNSUPPORTED_TRANSMISSION = 1u << 16u;
const uint GI_MATERIAL_COMPACT_TEXTURE_FALLBACK = 1u << 17u;
const uint GI_MATERIAL_GEOMETRY_DECAL = 1u << 18u;
const uint GI_MATERIAL_THIN_SURFACE_TRANSMISSION = 1u << 19u;
const uint GI_MATERIAL_TRANSMISSION_PROFILE_VALID = 1u << 20u;
const uint GI_MATERIAL_HAS_TRANSMISSION_TEXTURE = 1u << 21u;
const uint GI_MATERIAL_VOLUME_TRANSMISSION = 1u << 22u;
const uint GI_MATERIAL_WATER_SURFACE_BOUNDARY = 1u << 23u;
const uint GI_MATERIAL_OPTICAL_POLICY_PAYLOAD = 1u << 28u;

const float GI_MATERIAL_PI = 3.14159265358979323846;
// Cosine-weighted hemispherical average of 1 - SchlickFresnel(F0, NdotL).
const float GI_MATERIAL_SCHLICK_COSINE_WEIGHTED_TRANSMISSION = 20.0 / 21.0;
const float GI_MATERIAL_MINIMUM_ROUGHNESS = 0.04;
const float GI_MATERIAL_MAXIMUM_FINITE_RADIANCE = 65504.0;

struct GiSurfaceSample
{
    // Authored sheet orientation is stable across front/back hits. The regular
    // geometric normal is face-oriented toward the current outgoing side.
    vec3 CanonicalGeometricNormal;
    vec3 GeometricNormal;
    vec3 ShadingNormal;
    // DirectionalDiffuseBase is the passive opaque base-layer share before
    // dielectric Fresnel. DiffuseReflectance is the documented
    // directionally-compressed hemispherical response at the sampled NdotV.
    vec3 DirectionalDiffuseBase;
    vec3 DielectricF0;
    // Complete normal-incidence specular color. Detailed materials include
    // metallic base color; compact profiles conservatively retain dielectric
    // F0 until a separately versioned metallic-color statistic is available.
    vec3 SpecularF0;
    vec3 DiffuseReflectance;
    vec3 TransmittedDiffuseReflectance;
    vec3 EmissiveRadiance;
    float MaterialOcclusion;
    float Opacity;
    float Metallic;
    float Roughness;
    uint Flags;
};

bool GiMaterialHasFlag(uint flags, uint flag)
{
    return (flags & flag) == flag;
}

vec3 GiSafeNormal(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-12 ? value * inversesqrt(lengthSquared) : fallback;
}

vec3 CorrectGiShadingNormal(vec3 geometricNormal, vec3 shadingNormal)
{
    geometricNormal = GiSafeNormal(geometricNormal, vec3(0.0, 1.0, 0.0));
    shadingNormal = GiSafeNormal(shadingNormal, geometricNormal);
    float hemisphere = dot(geometricNormal, shadingNormal);
    if (hemisphere <= 0.0)
        return geometricNormal;

    // Limit grazing normal-map amplification while preserving the authored
    // direction over the valid geometric hemisphere.
    const float MinimumCosine = 0.1;
    if (hemisphere >= MinimumCosine)
        return shadingNormal;
    float blend = hemisphere / MinimumCosine;
    return GiSafeNormal(mix(geometricNormal, shadingNormal, blend), geometricNormal);
}

float EvaluateGiDielectricF0(float ior)
{
    ior = clamp(ior, 1.0, 3.0);
    float ratio = (ior - 1.0) / (ior + 1.0);
    return ratio * ratio;
}

vec3 EvaluateGiMaterialDielectricF0(
    float ior,
    float specularFactor,
    vec3 specularColor)
{
    specularFactor = clamp(specularFactor, 0.0, 1.0);
    specularColor = clamp(specularColor, vec3(0.0), vec3(1.0));
    return clamp(
        specularColor * (EvaluateGiDielectricF0(ior) * specularFactor),
        vec3(0.0),
        vec3(1.0));
}

vec3 EvaluateGiFresnelSchlick(vec3 f0, float cosine)
{
    float oneMinus = 1.0 - clamp(cosine, 0.0, 1.0);
    float factor = oneMinus * oneMinus;
    factor *= factor * oneMinus;
    return f0 + (vec3(1.0) - f0) * factor;
}

vec3 EvaluateGiDirectionalDiffuseBase(
    vec3 linearBaseColor,
    float metallic,
    float transmission,
    float clearcoat,
    vec3 sheenColor)
{
    linearBaseColor = clamp(linearBaseColor, vec3(0.0), vec3(1.0));
    metallic = clamp(metallic, 0.0, 1.0);
    transmission = clamp(transmission, 0.0, 1.0);
    clearcoat = clamp(clearcoat, 0.0, 1.0);

    float clearcoatEnergy = 1.0 - clearcoat * 0.04;
    vec3 sheenEnergy = vec3(1.0) - clamp(sheenColor, vec3(0.0), vec3(1.0));
    return clamp(
        linearBaseColor *
        (1.0 - metallic) *
        (1.0 - transmission) *
        clearcoatEnergy *
        sheenEnergy,
        vec3(0.0),
        vec3(1.0));
}

vec3 EvaluateGiHemisphericalDiffuseReflectance(
    vec3 linearBaseColor,
    float metallic,
    float ior,
    float specularFactor,
    vec3 specularColor,
    float transmission,
    float clearcoat,
    vec3 sheenColor,
    float nDotV)
{
    nDotV = clamp(nDotV, 0.0, 1.0);

    vec3 directionalDiffuseBase = EvaluateGiDirectionalDiffuseBase(
        linearBaseColor,
        metallic,
        transmission,
        clearcoat,
        sheenColor);
    vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
        ior,
        specularFactor,
        specularColor);
    vec3 outgoingEnergy =
        vec3(1.0) - EvaluateGiFresnelSchlick(dielectricF0, nDotV);
    vec3 incomingHemisphericalEnergy =
        (vec3(1.0) - dielectricF0) *
        GI_MATERIAL_SCHLICK_COSINE_WEIGHTED_TRANSMISSION;
    return clamp(
        directionalDiffuseBase *
        incomingHemisphericalEnergy *
        outgoingEnergy,
        vec3(0.0),
        vec3(1.0));
}

vec3 EvaluateGiHemisphericalDiffuseTransmittance(
    vec3 linearBaseColor,
    float metallic,
    float ior,
    float specularFactor,
    vec3 specularColor,
    float transmission,
    vec3 transmissionTint,
    float clearcoat,
    vec3 sheenColor,
    float nDotV)
{
    transmission = clamp(transmission, 0.0, 1.0);
    if (transmission <= 0.0)
        return vec3(0.0);
    vec3 available = EvaluateGiHemisphericalDiffuseReflectance(
        linearBaseColor,
        metallic,
        ior,
        specularFactor,
        specularColor,
        0.0,
        clearcoat,
        sheenColor,
        nDotV);
    return clamp(
        available * transmission * clamp(transmissionTint, vec3(0.0), vec3(1.0)),
        vec3(0.0),
        vec3(1.0));
}

vec3 EvaluateGiDiffuseBrdf(
    vec3 directionalDiffuseBase,
    vec3 dielectricF0,
    float nDotL,
    float nDotV)
{
    if (nDotL <= 0.0 || nDotV <= 0.0)
        return vec3(0.0);

    directionalDiffuseBase =
        clamp(directionalDiffuseBase, vec3(0.0), vec3(1.0));
    dielectricF0 = clamp(dielectricF0, vec3(0.0), vec3(1.0));
    vec3 incomingEnergy =
        vec3(1.0) - EvaluateGiFresnelSchlick(dielectricF0, nDotL);
    vec3 outgoingEnergy =
        vec3(1.0) - EvaluateGiFresnelSchlick(dielectricF0, nDotV);
    return clamp(
        directionalDiffuseBase * incomingEnergy * outgoingEnergy,
        vec3(0.0),
        vec3(1.0)) / GI_MATERIAL_PI;
}

vec3 EvaluateGiDiffuseFromIrradiance(vec3 irradiance, vec3 diffuseReflectance)
{
    return max(irradiance, vec3(0.0)) *
        clamp(diffuseReflectance, vec3(0.0), vec3(1.0)) / GI_MATERIAL_PI;
}

float EvaluateGiMaterialOcclusion(float strength, float sampleRed)
{
    strength = clamp(strength, 0.0, 1.0);
    sampleRed = clamp(sampleRed, 0.0, 1.0);
    return 1.0 + strength * (sampleRed - 1.0);
}

vec3 ApplyGiMaterialOcclusion(vec3 incomingIndirect, float materialOcclusion)
{
    return max(incomingIndirect, vec3(0.0)) * clamp(materialOcclusion, 0.0, 1.0);
}

// Geometry decals are material overlays, not transport surfaces. Composite
// every supported lobe into the base sample and leave base geometric
// orientation/opacity/participation ownership intact so lighting is evaluated
// exactly once after the final ordered overlay.
void ApplyGiDecalOverlay(
    inout GiSurfaceSample baseSurface,
    GiSurfaceSample decalSurface,
    bool premultipliedAlpha)
{
    float opacity = clamp(decalSurface.Opacity, 0.0, 1.0);
    if (opacity <= 0.0)
        return;

    vec3 decalDiffuse = clamp(
        decalSurface.DiffuseReflectance,
        vec3(0.0),
        vec3(1.0));
    vec3 decalDirectionalBase = clamp(
        decalSurface.DirectionalDiffuseBase,
        vec3(0.0),
        vec3(1.0));
    vec3 decalEmission = max(decalSurface.EmissiveRadiance, vec3(0.0));
    if (premultipliedAlpha)
    {
        baseSurface.DiffuseReflectance =
            baseSurface.DiffuseReflectance * (1.0 - opacity) +
            decalDiffuse * opacity;
        baseSurface.DirectionalDiffuseBase =
            baseSurface.DirectionalDiffuseBase * (1.0 - opacity) +
            decalDirectionalBase * opacity;
        baseSurface.EmissiveRadiance =
            baseSurface.EmissiveRadiance * (1.0 - opacity) +
            decalEmission * opacity;
    }
    else
    {
        baseSurface.DiffuseReflectance = mix(
            baseSurface.DiffuseReflectance,
            decalDiffuse,
            opacity);
        baseSurface.DirectionalDiffuseBase = mix(
            baseSurface.DirectionalDiffuseBase,
            decalDirectionalBase,
            opacity);
        baseSurface.EmissiveRadiance = mix(
            baseSurface.EmissiveRadiance,
            decalEmission,
            opacity);
    }

    baseSurface.DielectricF0 = mix(
        baseSurface.DielectricF0,
        clamp(decalSurface.DielectricF0, vec3(0.0), vec3(1.0)),
        opacity);
    baseSurface.SpecularF0 = mix(
        baseSurface.SpecularF0,
        clamp(decalSurface.SpecularF0, vec3(0.0), vec3(1.0)),
        opacity);
    baseSurface.TransmittedDiffuseReflectance = mix(
        baseSurface.TransmittedDiffuseReflectance,
        clamp(decalSurface.TransmittedDiffuseReflectance, vec3(0.0), vec3(1.0)),
        opacity);
    baseSurface.ShadingNormal = CorrectGiShadingNormal(
        baseSurface.GeometricNormal,
        GiSafeNormal(
            mix(baseSurface.ShadingNormal, decalSurface.ShadingNormal, opacity),
            baseSurface.ShadingNormal));
    baseSurface.MaterialOcclusion = mix(
        baseSurface.MaterialOcclusion,
        clamp(decalSurface.MaterialOcclusion, 0.0, 1.0),
        opacity);
    baseSurface.Metallic = mix(
        baseSurface.Metallic,
        clamp(decalSurface.Metallic, 0.0, 1.0),
        opacity);
    baseSurface.Roughness = clamp(
        mix(baseSurface.Roughness, decalSurface.Roughness, opacity),
        GI_MATERIAL_MINIMUM_ROUGHNESS,
        1.0);

    baseSurface.DiffuseReflectance = clamp(
        baseSurface.DiffuseReflectance,
        vec3(0.0),
        vec3(1.0));
    baseSurface.DirectionalDiffuseBase = clamp(
        baseSurface.DirectionalDiffuseBase,
        vec3(0.0),
        vec3(1.0));
    baseSurface.EmissiveRadiance = clamp(
        baseSurface.EmissiveRadiance,
        vec3(0.0),
        vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE));
}

bool EvaluateGiOpacity(float alpha, float alphaMode, float alphaCutoff)
{
    return MaterialAlphaSurvivesRasterCoverage(alpha, alphaMode, alphaCutoff);
}

bool EvaluateGiSidedness(bool doubleSided, bool frontFacing)
{
    return doubleSided || frontFacing;
}

GiSurfaceSample EmptyGiSurfaceSample(vec3 geometricNormal, vec3 shadingNormal, uint flags)
{
    GiSurfaceSample surface;
    surface.CanonicalGeometricNormal = GiSafeNormal(geometricNormal, vec3(0.0, 1.0, 0.0));
    surface.GeometricNormal = GiSafeNormal(geometricNormal, vec3(0.0, 1.0, 0.0));
    surface.ShadingNormal = CorrectGiShadingNormal(surface.GeometricNormal, shadingNormal);
    surface.DirectionalDiffuseBase = vec3(0.0);
    surface.DielectricF0 = vec3(0.0);
    surface.SpecularF0 = vec3(0.0);
    surface.DiffuseReflectance = vec3(0.0);
    surface.TransmittedDiffuseReflectance = vec3(0.0);
    surface.EmissiveRadiance = vec3(0.0);
    surface.MaterialOcclusion = 1.0;
    surface.Opacity = 1.0;
    surface.Metallic = 0.0;
    surface.Roughness = 1.0;
    surface.Flags = flags;
    return surface;
}

GiSurfaceSample EvaluateGiCompactSurface(
    GPUMaterialData material,
    vec3 geometricNormal,
    vec3 shadingNormal)
{
    uint flags = material.TransportFlags;
    GiSurfaceSample surface = EmptyGiSurfaceSample(geometricNormal, shadingNormal, flags);
    bool reflectsDiffuse =
        GiMaterialHasFlag(flags, GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_DIFFUSE_PROFILE_VALID);
    bool emits =
        GiMaterialHasFlag(flags, GI_MATERIAL_EMITS_INTO_GI) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_EMISSION_PROFILE_VALID);

    surface.DiffuseReflectance = reflectsDiffuse
        ? clamp(material.DdgiAverageAlbedo.rgb, vec3(0.0), vec3(1.0))
        : vec3(0.0);
    surface.TransmittedDiffuseReflectance =
        GiMaterialHasFlag(flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION) &&
        GiMaterialHasFlag(flags, GI_MATERIAL_TRANSMISSION_PROFILE_VALID)
            ? clamp(material.DdgiAverageTransmission.rgb, vec3(0.0), vec3(1.0))
            : vec3(0.0);
    if (reflectsDiffuse)
    {
        if (GiMaterialHasFlag(flags, GI_MATERIAL_LEGACY_V1_FALLBACK))
        {
            // V1 profiles stored only a Lambertian response. Preserve that
            // explicitly tagged compatibility behavior without pretending an
            // unavailable dielectric-F0 profile is valid.
            surface.DirectionalDiffuseBase = surface.DiffuseReflectance;
            surface.DielectricF0 = vec3(0.0);
        }
        else
        {
            vec2 diffuseBaseRg =
                unpackHalf2x16(material.PackedMeanGiDirectionalDiffuseBaseRg);
            vec2 diffuseBaseBAndF0R =
                unpackHalf2x16(material.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
            vec2 dielectricF0Gb =
                unpackHalf2x16(material.PackedMeanGiDielectricF0Gb);
            surface.DirectionalDiffuseBase = clamp(
                vec3(diffuseBaseRg, diffuseBaseBAndF0R.x),
                vec3(0.0),
                vec3(1.0));
            surface.DielectricF0 = clamp(
                vec3(diffuseBaseBAndF0R.y, dielectricF0Gb),
                vec3(0.0),
                vec3(1.0));
            surface.SpecularF0 = surface.DielectricF0;
        }
    }
    surface.EmissiveRadiance = emits
        ? clamp(material.DdgiAverageEmissive.rgb, vec3(0.0), vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE))
        : vec3(0.0);
    surface.MaterialOcclusion = GiMaterialHasFlag(flags, GI_MATERIAL_BASE_STATISTICS_VALID)
        ? clamp(material.DdgiAverageAlbedo.a, 0.0, 1.0)
        : 1.0;
    surface.Opacity = GiMaterialHasFlag(flags, GI_MATERIAL_ALPHA_PROFILE_VALID)
        ? clamp(material.DdgiMaterialPolicy.z, 0.0, 1.0)
        : 1.0;
    if (!GiMaterialHasFlag(flags, GI_MATERIAL_LEGACY_V1_FALLBACK))
    {
        vec2 meanMetallicRoughness = unpackHalf2x16(material.PackedMeanMetallicRoughness);
        surface.Metallic = clamp(meanMetallicRoughness.x, 0.0, 1.0);
        surface.Roughness = clamp(meanMetallicRoughness.y, GI_MATERIAL_MINIMUM_ROUGHNESS, 1.0);
    }
    else
    {
        surface.Metallic = clamp(material.MetallicRoughnessAO.x, 0.0, 1.0);
        surface.Roughness = clamp(material.MetallicRoughnessAO.y, GI_MATERIAL_MINIMUM_ROUGHNESS, 1.0);
    }
    return surface;
}

GiSurfaceSample EvaluateGiTexturedSurface(
    GPUMaterialData material,
    GPUMaterialExtensionData extensionData,
    bool hasExtensionData,
    vec4 baseColorSample,
    vec4 metallicRoughnessSample,
    float occlusionSample,
    vec3 emissiveSample,
    vec4 vertexColor,
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 viewDirection,
    bool retainRawEmissionForDiagnostics)
{
    uint flags = material.TransportFlags;
    GiSurfaceSample surface = EmptyGiSurfaceSample(geometricNormal, shadingNormal, flags);
    vec4 baseColor = max(material.Albedo * baseColorSample * vertexColor, vec4(0.0));
    surface.Opacity = clamp(baseColor.a, 0.0, 1.0);
    surface.Metallic = clamp(
        material.MetallicRoughnessAO.x * metallicRoughnessSample.b,
        0.0,
        1.0);
    surface.Roughness = clamp(
        material.MetallicRoughnessAO.y * metallicRoughnessSample.g,
        GI_MATERIAL_MINIMUM_ROUGHNESS,
        1.0);
    surface.MaterialOcclusion = EvaluateGiMaterialOcclusion(
        material.MetallicRoughnessAO.z,
        occlusionSample);

    float ior = 1.5;
    float specularFactor = 1.0;
    vec3 specularColor = vec3(1.0);
    float transmission = 0.0;
    float clearcoat = 0.0;
    vec3 sheenColor = vec3(0.0);
    float emissiveStrength = 1.0;
    vec3 thinTransmissionTint = vec3(1.0);
    if (hasExtensionData)
    {
        if ((material.FeatureFlags & MATERIAL_FEATURE_EMISSIVE_STRENGTH) != 0u)
            emissiveStrength = extensionData.Clearcoat.w;
        if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT) != 0u)
            clearcoat = clamp(extensionData.Clearcoat.x, 0.0, 1.0);
        if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN) != 0u)
            sheenColor = clamp(extensionData.SheenColor.rgb, vec3(0.0), vec3(1.0));
        if ((material.FeatureFlags &
             (MATERIAL_FEATURE_TRANSMISSION | MATERIAL_FEATURE_IOR)) != 0u)
        {
            ior = clamp(extensionData.Transmission.y, 1.0, 3.0);
        }
        if ((material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u)
        {
            transmission = clamp(extensionData.Transmission.x, 0.0, 1.0);
            thinTransmissionTint = clamp(extensionData.Dispersion.yzw, vec3(0.0), vec3(1.0));
        }
        if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR) != 0u)
        {
            specularFactor = clamp(extensionData.SpecularColor.a, 0.0, 1.0);
            specularColor = clamp(extensionData.SpecularColor.rgb, vec3(0.0), vec3(1.0));
        }
    }

    if (GiMaterialHasFlag(flags, GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE))
    {
        // Unsupported/volume transmission has no GI transmitted lobe yet.
        // Preserve an opaque diffuse fallback instead of removing energy on
        // both sides of the surface.
        float transportTransmission =
            GiMaterialHasFlag(flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION)
                ? transmission
                : 0.0;
        float nDotV = max(dot(surface.ShadingNormal, GiSafeNormal(viewDirection, surface.ShadingNormal)), 0.0);
        surface.DirectionalDiffuseBase = EvaluateGiDirectionalDiffuseBase(
            baseColor.rgb,
            surface.Metallic,
            transportTransmission,
            clearcoat,
            sheenColor);
        surface.DielectricF0 = EvaluateGiMaterialDielectricF0(
            ior,
            specularFactor,
            specularColor);
        surface.SpecularF0 = mix(
            surface.DielectricF0,
            clamp(baseColor.rgb, vec3(0.0), vec3(1.0)),
            surface.Metallic);
        surface.DiffuseReflectance = EvaluateGiHemisphericalDiffuseReflectance(
            baseColor.rgb,
            surface.Metallic,
            ior,
            specularFactor,
            specularColor,
            transportTransmission,
            clearcoat,
            sheenColor,
            nDotV);
        if (GiMaterialHasFlag(flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION))
        {
            surface.TransmittedDiffuseReflectance = EvaluateGiHemisphericalDiffuseTransmittance(
                baseColor.rgb,
                surface.Metallic,
                ior,
                specularFactor,
                specularColor,
                transmission,
                thinTransmissionTint,
                clearcoat,
                sheenColor,
                nDotV);
        }
    }

    if (GiMaterialHasFlag(flags, GI_MATERIAL_EMITS_INTO_GI))
    {
        if (retainRawEmissionForDiagnostics)
        {
            // DDGI retains this raw product until its diagnostic counter has
            // seen finite overflow and NaN/Inf.
            vec3 rawEmissiveRadiance =
                material.Emissive.rgb *
                emissiveSample *
                emissiveStrength;
            surface.EmissiveRadiance = rawEmissiveRadiance;
        }
        else
        {
            // Consumers without the DDGI counter request the explicit
            // FP16-storage-safe form.
            surface.EmissiveRadiance = clamp(
                max(material.Emissive.rgb, vec3(0.0)) *
                max(emissiveSample, vec3(0.0)) *
                max(emissiveStrength, 0.0),
                vec3(0.0),
                vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE));
        }
    }
    return surface;
}

#endif // NJULF_GI_MATERIAL_TRANSPORT_GLSL
