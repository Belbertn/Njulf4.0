#ifndef NJULF_DDGI_GUIDING_PAYLOAD_IDENTITY_GLSL
#define NJULF_DDGI_GUIDING_PAYLOAD_IDENTITY_GLSL

// Compact ownership tag for the trace hot path. The complete transport
// consumers still validate every payload field; trace needs only a trustworthy
// direction owned by the current sparse probe slot. Keep this byte-for-byte in
// sync with SimpleDdgiGuidingGpuAbi.ComputeTraceOwnershipTag.
uint SimpleDdgiGuidingTraceOwnershipTag(
    uvec2 stableProbeId,
    uint physicalProbeIndex,
    uint virtualProbeId,
    uint pageGeneration,
    uint slotIndex,
    uint packedDirectionOct32)
{
    uint tag = 0x43335447u; // "C3TG"
    tag = (tag ^ stableProbeId.x) * 16777619u;
    tag = (tag ^ stableProbeId.y) * 16777619u;
    tag = (tag ^ physicalProbeIndex) * 16777619u;
    tag = (tag ^ virtualProbeId) * 16777619u;
    tag = (tag ^ pageGeneration) * 16777619u;
    tag = (tag ^ slotIndex) * 16777619u;
    tag = (tag ^ packedDirectionOct32) * 16777619u;
    tag ^= tag >> 16u;
    return tag == 0u ? 1u : tag;
}

#endif
