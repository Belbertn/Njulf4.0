# Hybrid Forward DDGI Diffuse/Visibility Permutation

## Goal

In hybrid opaque Forward variants, gather diffuse DDGI and scalar rough-specular visibility only. The hybrid-reflection DDGI base remains the owner of directional radiance/confidence.

## Implementation

- Derive a compile-time directional-DDGI flag from the existing `NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT` define; add no artifact name, runtime setting, or selector.
- Compile out directional mode/configuration, SH query setup, reconstruction, and radiance/confidence extraction in hybrid opaque variants.
- Preserve exact/cache diffuse gathering and `SimpleDdgiRoughIndirectSpecularVisibility`, including the hybrid payload's specular-occlusion field.
- Apply the change to every active hybrid opaque sibling, including the specialized families from plan 1, cache, MRT, and sparse-lobe variants.
- Leave non-hybrid Forward, transparent, ThinGlass, and foliage paths unchanged. Do not change descriptors, push constants, attachments, or payload ABI.

## Verification

- Add one shader-contract test proving hybrid variants omit directional gathering while retaining diffuse gathering and scalar visibility.
- Fresh-build the affected artifacts, run `spirv-val`, and inspect stripped SPIR-V to confirm the directional accesses and SH evaluation are absent.
- Exercise one hybrid pipeline under Vulkan validation and compare one representative frame.
- Take one matched warmed Bistro baseline/candidate pair. Record registers, spills/local memory, occupancy, `ForwardPlusPass`, and whole-frame time; keep only a clear useful improvement without regression.
