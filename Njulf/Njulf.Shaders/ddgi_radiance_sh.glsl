#ifndef NJULF_DDGI_RADIANCE_SH_GLSL
#define NJULF_DDGI_RADIANCE_SH_GLSL

#define SIMPLE_DDGI_RADIANCE_SH_L2_ABI 0x4C320001u
#define SIMPLE_DDGI_RADIANCE_SH_L2_RECORD_WORDS 16u
#define SIMPLE_DDGI_RADIANCE_SH_L2_REPRESENTATION_VERSION 1u
#define SIMPLE_DDGI_RADIANCE_SH_L1_ABI 0x4C310001u
#define SIMPLE_DDGI_RADIANCE_SH_L1_RECORD_WORDS 8u
#define SIMPLE_DDGI_RADIANCE_SH_L1_REPRESENTATION_VERSION 1u

const uint SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_OFF = 0u;
const uint SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L1_REFERENCE = 1u;
const uint SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L2 = 2u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_OFF = 0u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_RECEIVER_ONLY = 1u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_ONE_BOUNCE = 2u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_RECURSIVE_EXPERIMENTAL = 3u;

const uint SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_SHIFT = 4u;
const uint SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_MASK = 0x3u << 4u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_SHIFT = 6u;
const uint SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_MASK = 0x3u << 6u;
const uint SIMPLE_DDGI_ROUGH_SPECULAR_MINIMUM_SHIFT = 8u;
const uint SIMPLE_DDGI_ROUGH_SPECULAR_FULL_SHIFT = 16u;

const uint SIMPLE_DDGI_RADIANCE_SH_VALID_BIT = 1u << 0u;
const uint SIMPLE_DDGI_RADIANCE_SH_HISTORY_BIT = 1u << 1u;
const uint SIMPLE_DDGI_RADIANCE_SH_SAMPLE_COUNT_SHIFT = 2u;
const uint SIMPLE_DDGI_RADIANCE_SH_QUALITY_SHIFT = 10u;
const uint SIMPLE_DDGI_RADIANCE_SH_VERSION_SHIFT = 14u;
const uint SIMPLE_DDGI_RADIANCE_SH_VERSION_MASK = 0xffu << 14u;
const uint SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_SHIFT = 22u;
const uint SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_MASK = 0x3ffu << 22u;
const float SIMPLE_DDGI_RADIANCE_SH_MAXIMUM_FINITE_HALF = 65504.0;

uint SimpleDdgiDirectionalRadianceMode(uint residencyFlags)
{
    return (residencyFlags & SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_MASK) >>
        SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_SHIFT;
}

uint SimpleDdgiGlossyTransportMode(uint residencyFlags)
{
    return (residencyFlags & SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_MASK) >>
        SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_SHIFT;
}

float SimpleDdgiRoughSpecularMinimumRoughness(uint residencyFlags)
{
    return float((residencyFlags >>
        SIMPLE_DDGI_ROUGH_SPECULAR_MINIMUM_SHIFT) & 0xffu) / 255.0;
}

float SimpleDdgiRoughSpecularFullWeightRoughness(uint residencyFlags)
{
    return float((residencyFlags >>
        SIMPLE_DDGI_ROUGH_SPECULAR_FULL_SHIFT) & 0xffu) / 255.0;
}

float SimpleDdgiRoughSpecularWeight(
    uint residencyFlags,
    float perceptualRoughness)
{
    float minimum = SimpleDdgiRoughSpecularMinimumRoughness(residencyFlags);
    float full = max(
        SimpleDdgiRoughSpecularFullWeightRoughness(residencyFlags),
        minimum + 1.0 / 255.0);
    return smoothstep(minimum, full, clamp(perceptualRoughness, 0.0, 1.0));
}

uint SimpleDdgiRadianceShRecordWords(uint mode)
{
    return mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L2
        ? SIMPLE_DDGI_RADIANCE_SH_L2_RECORD_WORDS
        : mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L1_REFERENCE
            ? SIMPLE_DDGI_RADIANCE_SH_L1_RECORD_WORDS
            : 0u;
}

uint SimpleDdgiRadianceShCoefficientCount(uint mode)
{
    return mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L2
        ? 9u
        : mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L1_REFERENCE
            ? 4u
            : 0u;
}

uint SimpleDdgiRadianceShRepresentationVersion(uint mode)
{
    return mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L2
        ? SIMPLE_DDGI_RADIANCE_SH_L2_REPRESENTATION_VERSION
        : mode == SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_L1_REFERENCE
            ? SIMPLE_DDGI_RADIANCE_SH_L1_REPRESENTATION_VERSION
            : 0u;
}

float SimpleDdgiRadianceShCoefficientValue(
    vec3 coefficients[9],
    uint valueIndex)
{
    vec3 coefficient = coefficients[valueIndex / 3u];
    uint channel = valueIndex % 3u;
    return channel == 0u
        ? coefficient.x
        : channel == 1u
            ? coefficient.y
            : coefficient.z;
}

uint SimpleDdgiRadianceShHashAdd(uint hash, uint value)
{
    return (hash ^ value) * 16777619u;
}

uint SimpleDdgiRadianceShFinishHash(uint hash)
{
    return hash ^ (hash >> 16u);
}

void InvalidateSimpleDdgiRadianceShRecord(
    uint bufferIndex,
    uint probeIndex,
    uint mode)
{
    uint recordWords = SimpleDdgiRadianceShRecordWords(mode);
    if (recordWords == 0u)
        return;
    uint baseWord = probeIndex * recordWords;
    uint metadataWord = recordWords - 1u;
    WriteStorageWordUniform(bufferIndex, baseWord + metadataWord, 0u);
    memoryBarrierBuffer();
    for (uint word = 0u; word < metadataWord; word++)
        WriteStorageWordUniform(bufferIndex, baseWord + word, 0u);
}

bool WriteSimpleDdgiRadianceShRecord(
    uint bufferIndex,
    uint probeIndex,
    uint mode,
    vec3 coefficients[9],
    uint slotGeneration,
    uint validSampleCount,
    uint qualityLevel,
    bool hasHistory)
{
    uint recordWords = SimpleDdgiRadianceShRecordWords(mode);
    uint coefficientCount = SimpleDdgiRadianceShCoefficientCount(mode);
    if (recordWords == 0u || coefficientCount == 0u ||
        slotGeneration == 0u || slotGeneration > 0x00ffffffu)
    {
        return false;
    }

    uint coefficientWords = (coefficientCount * 3u + 1u) / 2u;
    uint packedWords[14];
    for (uint word = 0u; word < 14u; word++)
        packedWords[word] = 0u;

    uint coefficientValueCount = coefficientCount * 3u;
    for (uint valueIndex = 0u;
         valueIndex < coefficientValueCount;
         valueIndex++)
    {
        float value = SimpleDdgiRadianceShCoefficientValue(
            coefficients,
            valueIndex);
        if (isnan(value) || isinf(value) ||
            abs(value) > SIMPLE_DDGI_RADIANCE_SH_MAXIMUM_FINITE_HALF)
        {
            InvalidateSimpleDdgiRadianceShRecord(
                bufferIndex,
                probeIndex,
                mode);
            return false;
        }
    }

    for (uint word = 0u; word < coefficientWords; word++)
    {
        uint firstIndex = word * 2u;
        float first = SimpleDdgiRadianceShCoefficientValue(
            coefficients,
            firstIndex);
        float second = firstIndex + 1u < coefficientValueCount
            ? SimpleDdgiRadianceShCoefficientValue(
                coefficients,
                firstIndex + 1u)
            : 0.0;
        packedWords[word] = packHalf2x16(vec2(first, second));
    }

    uint metadataWithoutChecksum = SIMPLE_DDGI_RADIANCE_SH_VALID_BIT |
        (hasHistory ? SIMPLE_DDGI_RADIANCE_SH_HISTORY_BIT : 0u) |
        ((min(validSampleCount, 255u) & 0xffu) <<
            SIMPLE_DDGI_RADIANCE_SH_SAMPLE_COUNT_SHIFT) |
        ((min(qualityLevel, 15u) & 0x0fu) <<
            SIMPLE_DDGI_RADIANCE_SH_QUALITY_SHIFT) |
        (SimpleDdgiRadianceShRepresentationVersion(mode) <<
            SIMPLE_DDGI_RADIANCE_SH_VERSION_SHIFT);
    uint hash = 2166136261u;
    for (uint word = 0u; word < coefficientWords; word++)
        hash = SimpleDdgiRadianceShHashAdd(hash, packedWords[word]);
    hash = SimpleDdgiRadianceShHashAdd(hash, slotGeneration);
    hash = SimpleDdgiRadianceShHashAdd(hash, metadataWithoutChecksum);
    uint checksum = SimpleDdgiRadianceShFinishHash(hash) & 0x3ffu;
    uint metadata = metadataWithoutChecksum |
        (checksum << SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_SHIFT);

    uint baseWord = probeIndex * recordWords;
    uint generationWord = recordWords - 2u;
    uint metadataWord = recordWords - 1u;
    // Validity is the final scalar publication store. A receiver that observes
    // either side of a transition therefore rejects the record.
    WriteStorageWordUniform(bufferIndex, baseWord + metadataWord, 0u);
    memoryBarrierBuffer();
    for (uint word = 0u; word < coefficientWords; word++)
        WriteStorageWordUniform(bufferIndex, baseWord + word, packedWords[word]);
    WriteStorageWordUniform(bufferIndex, baseWord + generationWord, slotGeneration);
    memoryBarrierBuffer();
    WriteStorageWordUniform(bufferIndex, baseWord + metadataWord, metadata);
    return true;
}

bool ReadSimpleDdgiRadianceShRecord(
    uint bufferIndex,
    uint probeIndex,
    uint mode,
    uint expectedSlotGeneration,
    out vec3 coefficients[9],
    out uint validSampleCount,
    out uint qualityLevel,
    out bool hasHistory)
{
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
        coefficients[coefficient] = vec3(0.0);
    validSampleCount = 0u;
    qualityLevel = 0u;
    hasHistory = false;

    uint recordWords = SimpleDdgiRadianceShRecordWords(mode);
    uint coefficientCount = SimpleDdgiRadianceShCoefficientCount(mode);
    if (recordWords == 0u || coefficientCount == 0u ||
        expectedSlotGeneration == 0u)
    {
        return false;
    }

    uint coefficientWords = (coefficientCount * 3u + 1u) / 2u;
    uint baseWord = probeIndex * recordWords;
    uint generationWord = recordWords - 2u;
    uint metadataWord = recordWords - 1u;
    uint metadata = ReadStorageWordUniform(
        bufferIndex,
        baseWord + metadataWord);
    uint metadataWithoutChecksum =
        metadata & ~SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_MASK;
    if ((metadata & SIMPLE_DDGI_RADIANCE_SH_VALID_BIT) == 0u ||
        ((metadata & SIMPLE_DDGI_RADIANCE_SH_VERSION_MASK) >>
            SIMPLE_DDGI_RADIANCE_SH_VERSION_SHIFT) !=
                SimpleDdgiRadianceShRepresentationVersion(mode))
    {
        return false;
    }

    uint packedWords[14];
    uint hash = 2166136261u;
    for (uint word = 0u; word < coefficientWords; word++)
    {
        packedWords[word] = ReadStorageWordUniform(
            bufferIndex,
            baseWord + word);
        hash = SimpleDdgiRadianceShHashAdd(hash, packedWords[word]);
    }
    uint slotGeneration = ReadStorageWordUniform(
        bufferIndex,
        baseWord + generationWord);
    hash = SimpleDdgiRadianceShHashAdd(hash, slotGeneration);
    hash = SimpleDdgiRadianceShHashAdd(hash, metadataWithoutChecksum);
    uint checksum = SimpleDdgiRadianceShFinishHash(hash) & 0x3ffu;
    uint metadataAfter = ReadStorageWordUniform(
        bufferIndex,
        baseWord + metadataWord);
    if (metadataAfter != metadata ||
        slotGeneration != expectedSlotGeneration ||
        checksum != ((metadata & SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_MASK) >>
            SIMPLE_DDGI_RADIANCE_SH_CHECKSUM_SHIFT))
    {
        return false;
    }

    uint coefficientValueCount = coefficientCount * 3u;
    for (uint valueIndex = 0u;
         valueIndex < coefficientValueCount;
         valueIndex++)
    {
        vec2 values = unpackHalf2x16(packedWords[valueIndex / 2u]);
        float value = (valueIndex & 1u) == 0u ? values.x : values.y;
        if (isnan(value) || isinf(value))
            return false;
        uint coefficientIndex = valueIndex / 3u;
        uint channel = valueIndex % 3u;
        if (channel == 0u)
            coefficients[coefficientIndex].x = value;
        else if (channel == 1u)
            coefficients[coefficientIndex].y = value;
        else
            coefficients[coefficientIndex].z = value;
    }

    validSampleCount = (metadata >>
        SIMPLE_DDGI_RADIANCE_SH_SAMPLE_COUNT_SHIFT) & 0xffu;
    qualityLevel = (metadata >> SIMPLE_DDGI_RADIANCE_SH_QUALITY_SHIFT) & 0x0fu;
    hasHistory = (metadata & SIMPLE_DDGI_RADIANCE_SH_HISTORY_BIT) != 0u;
    return true;
}

void SimpleDdgiEvaluateRadianceShL2Basis(vec3 direction, out float basis[9])
{
    vec3 omega = normalize(direction);
    float x = omega.x;
    float y = omega.y;
    float z = omega.z;
    basis[0] = 0.2820947918;
    basis[1] = -0.4886025119 * y;
    basis[2] = 0.4886025119 * z;
    basis[3] = -0.4886025119 * x;
    basis[4] = 1.0925484306 * x * y;
    basis[5] = -1.0925484306 * y * z;
    basis[6] = 0.3153915653 * (3.0 * z * z - 1.0);
    basis[7] = -1.0925484306 * x * z;
    basis[8] = 0.5462742153 * (x * x - y * y);
}

// Projection kernels keep the ray dimension distributed across the workgroup
// and reduce one coefficient at a time. Accepting an already-normalized
// direction avoids nine normalizations and, unlike a per-lane float[9]
// accumulator, keeps register pressure bounded on native Vulkan compilers.
float SimpleDdgiEvaluateRadianceShL2BasisCoefficientNormalized(
    vec3 omega,
    uint coefficient)
{
    float x = omega.x;
    float y = omega.y;
    float z = omega.z;
    if (coefficient == 0u)
        return 0.2820947918;
    if (coefficient == 1u)
        return -0.4886025119 * y;
    if (coefficient == 2u)
        return 0.4886025119 * z;
    if (coefficient == 3u)
        return -0.4886025119 * x;
    if (coefficient == 4u)
        return 1.0925484306 * x * y;
    if (coefficient == 5u)
        return -1.0925484306 * y * z;
    if (coefficient == 6u)
        return 0.3153915653 * (3.0 * z * z - 1.0);
    if (coefficient == 7u)
        return -1.0925484306 * x * z;
    return coefficient == 8u
        ? 0.5462742153 * (x * x - y * y)
        : 0.0;
}

// Checked-in table generated from normalized D_GGX(mu) * mu. Rows are
// perceptual roughness, l0, l1, l2 and mirror the CPU checksum table.
const vec4 SimpleDdgiGgxBandScaleTable[17] = vec4[](
    vec4(0.000000, 1.000000, 1.000000, 1.000000),
    vec4(0.062500, 1.000000, 0.999920, 0.999768),
    vec4(0.125000, 1.000000, 0.999059, 0.997319),
    vec4(0.187500, 1.000000, 0.996234, 0.989412),
    vec4(0.250000, 1.000000, 0.990308, 0.973136),
    vec4(0.312500, 1.000000, 0.980439, 0.946599),
    vec4(0.375000, 1.000000, 0.966179, 0.909141),
    vec4(0.437500, 1.000000, 0.947472, 0.861241),
    vec4(0.500000, 1.000000, 0.924593, 0.804257),
    vec4(0.562500, 1.000000, 0.898061, 0.740092),
    vec4(0.625000, 1.000000, 0.868541, 0.670879),
    vec4(0.687500, 1.000000, 0.836758, 0.598731),
    vec4(0.750000, 1.000000, 0.803430, 0.525559),
    vec4(0.812500, 1.000000, 0.769222, 0.452980),
    vec4(0.875000, 1.000000, 0.734716, 0.382274),
    vec4(0.937500, 1.000000, 0.700400, 0.314395),
    vec4(1.000000, 1.000000, 0.666667, 0.250000));

vec3 SimpleDdgiGgxBandScales(float perceptualRoughness)
{
    float coordinate = clamp(perceptualRoughness, 0.0, 1.0) * 16.0;
    uint lower = min(uint(coordinate), 16u);
    uint upper = min(lower + 1u, 16u);
    return mix(
        SimpleDdgiGgxBandScaleTable[lower].yzw,
        SimpleDdgiGgxBandScaleTable[upper].yzw,
        coordinate - float(lower));
}

bool EvaluateSimpleDdgiRadianceShRecord(
    uint bufferIndex,
    uint probeIndex,
    uint mode,
    uint expectedSlotGeneration,
    vec3 direction,
    float perceptualRoughness,
    out vec3 radiance,
    out vec3 negativeReconstruction)
{
    radiance = vec3(0.0);
    negativeReconstruction = vec3(0.0);
    float directionLengthSquared = dot(direction, direction);
    if (!(directionLengthSquared > 1.0e-12) ||
        isnan(directionLengthSquared) || isinf(directionLengthSquared))
    {
        return false;
    }

    vec3 coefficients[9];
    uint validSampleCount;
    uint qualityLevel;
    bool hasHistory;
    if (!ReadSimpleDdgiRadianceShRecord(
            bufferIndex,
            probeIndex,
            mode,
            expectedSlotGeneration,
            coefficients,
            validSampleCount,
            qualityLevel,
            hasHistory))
    {
        return false;
    }
    // Zero-sample records are transactionally complete so their matching
    // diffuse probe generation may publish, but contain no directional
    // evidence. Reject them as a reflection source and preserve the explicit
    // local-probe/environment ownership fallback.
    if (validSampleCount == 0u)
        return false;

    float basis[9];
    SimpleDdgiEvaluateRadianceShL2Basis(
        direction * inversesqrt(directionLengthSquared),
        basis);
    vec3 bandScales = SimpleDdgiGgxBandScales(perceptualRoughness);
    uint coefficientCount = SimpleDdgiRadianceShCoefficientCount(mode);
    vec3 reconstructed = vec3(0.0);
    for (uint coefficient = 0u;
         coefficient < coefficientCount;
         coefficient++)
    {
        uint band = coefficient == 0u ? 0u : coefficient <= 3u ? 1u : 2u;
        reconstructed += coefficients[coefficient] *
            (basis[coefficient] * bandScales[band]);
    }
    if (any(isnan(reconstructed)) || any(isinf(reconstructed)))
        return false;

    // Ringing is measured before, and clamped only at, the final radiance
    // evaluation. Coefficient storage and interpolation remain signed/linear.
    negativeReconstruction = max(-reconstructed, vec3(0.0));
    radiance = max(reconstructed, vec3(0.0));
    return true;
}

#endif
