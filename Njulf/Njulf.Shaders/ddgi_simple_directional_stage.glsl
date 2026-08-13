#ifndef NJULF_DDGI_SIMPLE_DIRECTIONAL_STAGE_GLSL
#define NJULF_DDGI_SIMPLE_DIRECTIONAL_STAGE_GLSL

// C3's directional projection runs after every ordinary ray-scratch consumer.
// Its first dispatch may therefore replace each dead ray with this compact,
// layout-independent projection input without allocating another buffer.
// Radiance is already multiplied by the exact balance-estimator weight.
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_DIRECTION_WORD = 3u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_METADATA_WORD = 4u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_SIGNATURE = 0x44534600u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_SIGNATURE_MASK = 0xfffffe00u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_GUIDED_BIT = 1u << 8u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_NO_WORK = 0x44534400u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_FAILED = 0x44534500u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_HIT_KIND_MASK = 0x7u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_SHIFT = 3u;
const uint SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_MASK = 0xf8u;

uint PackSimpleDdgiDirectionalStageMetadata(
    float hitKind,
    uint sourceEpoch,
    bool guided)
{
    return SIMPLE_DDGI_DIRECTIONAL_STAGE_SIGNATURE |
        (guided ? SIMPLE_DDGI_DIRECTIONAL_STAGE_GUIDED_BIT : 0u) |
        (uint(hitKind) & SIMPLE_DDGI_DIRECTIONAL_STAGE_HIT_KIND_MASK) |
        ((SimpleDdgiDirectionEpoch(sourceEpoch) <<
            SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_SHIFT) &
            SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_MASK);
}

bool SimpleDdgiDirectionalStageMetadataIsGuided(uint metadata)
{
    return (metadata & SIMPLE_DDGI_DIRECTIONAL_STAGE_GUIDED_BIT) != 0u;
}

bool SimpleDdgiDirectionalStageMetadataIsValid(
    uint metadata,
    uint expectedSourceEpoch)
{
    return (metadata & SIMPLE_DDGI_DIRECTIONAL_STAGE_SIGNATURE_MASK) ==
            SIMPLE_DDGI_DIRECTIONAL_STAGE_SIGNATURE &&
        ((metadata & SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_MASK) >>
            SIMPLE_DDGI_DIRECTIONAL_STAGE_EPOCH_SHIFT) ==
                SimpleDdgiDirectionEpoch(expectedSourceEpoch) &&
        (metadata & SIMPLE_DDGI_DIRECTIONAL_STAGE_HIT_KIND_MASK) <=
            uint(SIMPLE_DDGI_RAY_HIT_KIND_FAR_FIELD_BACK_FACE);
}

#endif
