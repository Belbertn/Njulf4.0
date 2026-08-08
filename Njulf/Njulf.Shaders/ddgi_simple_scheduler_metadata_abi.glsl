#ifndef NJULF_DDGI_SIMPLE_SCHEDULER_METADATA_ABI_GLSL
#define NJULF_DDGI_SIMPLE_SCHEDULER_METADATA_ABI_GLSL

// Persistent scheduler state is consumed by both scheduler kernels and sparse
// residency kernels. Keep its stride and metadata masks in one ABI header so
// page admission/feedback cannot silently index an older record layout.
const uint SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS = 11u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE = 1u << 16u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED = 1u << 17u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR = 1u << 30u;
const uint SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBILITY_MASK =
    SIMPLE_DDGI_SCHEDULER_PROBE_META_VISIBLE |
    SIMPLE_DDGI_SCHEDULER_PROBE_META_PUBLISHED;

#endif
