#ifndef NJULF_OPAQUE_VISIBILITY_WORK_GLSL
#define NJULF_OPAQUE_VISIBILITY_WORK_GLSL

// One job covers the surviving pixels of one primitive in a 2x2 quad.
// At most one job per pixel; prefix/scatter partitions the same bounded arena.
const uint VisibilityFamilyCount = 4u;
const uint VisibilityCountWord = 4u;
const uint VisibilityCursorWord = 8u;
const uint VisibilityOffsetWord = 12u;
const uint VisibilityIndirectWord = 16u;
layout(set = 2, binding = 0) uniform usampler2D OpaqueVisibility;
layout(set = 2, binding = 1, rgba16f) uniform writeonly image2D OpaqueColor;
layout(set = 2, binding = 2, rgba32ui) uniform writeonly uimage2D OpaqueHybridReceiver;
layout(set = 2, binding = 3, rg32ui) uniform writeonly uimage2D OpaqueHybridLobe;
layout(set = 2, binding = 4, std430) buffer VisibilityJobBuffer { uvec4 VisibilityJobs[]; };
layout(set = 2, binding = 5, std430) buffer VisibilityIndexBuffer { uint VisibilityIndices[]; };
layout(set = 2, binding = 6, std430) buffer VisibilityControlBuffer { uint VisibilityControl[]; };

#endif
