#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_quad : require

vec4 VisibilityFragCoord;
bool VisibilityFrontFacing;
bool VisibilityCovered;
vec3 VisibilityWorldDx;
vec3 VisibilityWorldDy;
float VisibilityDepthGradient;

// Jobs keep all four lanes on one primitive through material evaluation.
// Using subgroup quad operations requires no compute-derivative extension.
#define VISIBILITY_DERIVATIVE(T) \
T VisibilityDx(T value) { T neighbor = subgroupQuadSwapHorizontal(value); return (gl_SubgroupInvocationID & 1u) == 0u ? neighbor - value : value - neighbor; } \
T VisibilityDy(T value) { T neighbor = subgroupQuadSwapVertical(value); return (gl_SubgroupInvocationID & 2u) == 0u ? neighbor - value : value - neighbor; }
VISIBILITY_DERIVATIVE(float)
VISIBILITY_DERIVATIVE(vec2)
VISIBILITY_DERIVATIVE(vec3)
VISIBILITY_DERIVATIVE(vec4)
#undef VISIBILITY_DERIVATIVE
#define dFdx VisibilityDx
#define dFdy VisibilityDy
#define gl_FragCoord VisibilityFragCoord
#define gl_FrontFacing VisibilityFrontFacing
