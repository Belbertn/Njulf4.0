#ifndef NJULF_DDGI_CONTENT_STOCHASTIC_GLSL
#define NJULF_DDGI_CONTENT_STOCHASTIC_GLSL

// Must match DdgiStochasticIdentity.HashAbiVersion.
#define DDGI_STOCHASTIC_HASH_ABI_VERSION 1u
#define DDGI_STOCHASTIC_DOMAIN_LIGHT_TREE 0x11u
#define DDGI_STOCHASTIC_DOMAIN_ALPHA_COVERAGE 0x23u
#define DDGI_STOCHASTIC_DOMAIN_FOLIAGE_PROXY 0x37u
#define DDGI_STOCHASTIC_DOMAIN_TRANSPARENT_LAYER 0x41u
#define DDGI_STOCHASTIC_DOMAIN_DECAL_ORDER 0x53u
#define DDGI_STOCHASTIC_DOMAIN_AREA_LIGHT_SURFACE 0xA7u

uint DdgiStochasticAvalanche(uint value)
{
    value ^= value >> 16u;
    value *= 0x7FEB352Du;
    value ^= value >> 15u;
    value *= 0x846CA68Bu;
    value ^= value >> 16u;
    return value;
}

uint DdgiStochasticMix(uint state, uint value)
{
    uint x = state ^
        (value + 0x9E3779B9u + (state << 6u) + (state >> 2u));
    return DdgiStochasticAvalanche(x);
}

uint DdgiStableDecisionHash(
    uvec2 worldProbeStableKey,
    uint directionRayOrdinal,
    uint sourceLightingEpoch,
    uint samplingSequenceEpoch,
    uint decisionDomain,
    uint instanceIdentity,
    uint primitiveIdentity)
{
    uint state = 0xD1B54A35u ^ DDGI_STOCHASTIC_HASH_ABI_VERSION;
    state = DdgiStochasticMix(state, worldProbeStableKey.x);
    state = DdgiStochasticMix(state, worldProbeStableKey.y);
    state = DdgiStochasticMix(state, directionRayOrdinal);
    state = DdgiStochasticMix(state, sourceLightingEpoch);
    state = DdgiStochasticMix(state, samplingSequenceEpoch);
    state = DdgiStochasticMix(state, decisionDomain);
    state = DdgiStochasticMix(state, instanceIdentity);
    state = DdgiStochasticMix(state, primitiveIdentity);
    return DdgiStochasticAvalanche(state);
}

float DdgiStableDecisionUnitFloat(
    uvec2 worldProbeStableKey,
    uint directionRayOrdinal,
    uint sourceLightingEpoch,
    uint samplingSequenceEpoch,
    uint decisionDomain,
    uint instanceIdentity,
    uint primitiveIdentity)
{
    uint value = DdgiStableDecisionHash(
        worldProbeStableKey,
        directionRayOrdinal,
        sourceLightingEpoch,
        samplingSequenceEpoch,
        decisionDomain,
        instanceIdentity,
        primitiveIdentity);
    return (float(value >> 8u) + 0.5) * (1.0 / 16777216.0);
}

#endif
