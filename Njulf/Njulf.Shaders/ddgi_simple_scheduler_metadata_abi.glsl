#ifndef NJULF_DDGI_SIMPLE_SCHEDULER_METADATA_ABI_GLSL
#define NJULF_DDGI_SIMPLE_SCHEDULER_METADATA_ABI_GLSL

// Persistent scheduler metadata is consumed by both scheduler kernels and
// residency feedback. Keep the masks in one ABI header so feedback cannot
// accidentally treat visibility/publication state as a repair request.
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE = 1u << 16u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED = 1u << 17u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR = 1u << 30u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBILITY_MASK =
    SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE |
    SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED;

#endif
