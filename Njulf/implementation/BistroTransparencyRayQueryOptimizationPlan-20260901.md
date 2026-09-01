# Bistro Transparent Ray-Traversal Optimization

## Summary

- Start only after planar capture and opaque-forward optimizations are accepted; recapture the resulting baseline so the reported 3.90 ms is not compared across different builds.
- Confirm the hotspot maps to `ForwardTraceTransparentReflectionNearest` in `forward_transparent_ordinary_ray.frag.spv`.
- Optimize traversal by excluding geometry that transparent reflections already reject after traversal, without reducing rays, resolution, samples, or visual quality.

## Key Changes

- Reserve TLAS instance-mask bit `0x04` for transparent reflections; expand the internal shared-mask union while keeping the existing opaque and directional-shadow bits unchanged.
- Build each instance mask from independent eligibility rules:
  - Always include the existing general ray-scene bit.
  - Preserve current directional-shadow eligibility exactly.
  - Include the reflection bit for opaque, alpha-mask, thin-only, dynamic, skinned, foliage, and conservative geometry.
  - Exclude alpha-blend, decals, volume transmission, and water by either geometry class or flag. Thin-plus-blend remains excluded.
- Mirror the reflection-mask constant in the GLSL ray-scene contract and replace only the reflection query's `0xff` cull mask.
- Retain `gl_RayFlagsNoneEXT`, nearest-hit behavior, the 64-candidate safety cap, alpha/front-face candidate checks, ray distance, SSR ordering, and all existing reflection budgets.
- Do not change the `-Od` workaround, other ray-query loops, descriptor layouts, push constants, specialization constants, pipeline permutations, BLAS ABI, or public APIs.

## Validation and Tests

- Extend acceleration-structure tests to prove mask bits are unique and verify the full eligibility table, including class/flag mismatches and exclusion precedence.
- Add a shader contract test locking the C#/GLSL mask value and confirming the ordinary transparent reflection query uses it while preserving its candidate loop.
- Re-run transparent partitioning tests to ensure Bistro still selects `forward_transparent_ordinary_ray.frag.spv` without fallback or additional pipeline variants.
- Compile normally—not from pre-existing artifacts—and validate every affected forward ray SPIR-V module for Vulkan 1.3, unchanged entry point, descriptors, 256-byte push-constant ABI, and specialization IDs.
- Run alpha-visibility hardware conformance plus Bistro glass/foliage and Sponza thin-curtain checks. Require relative RMSE <= 0.005, FLIP p95 <= 0.02, ROI mean <= 0.02, ROI p95 <= 0.03, and temporal repeatability.

## Performance Acceptance

- Establish an identical-build A/A noise envelope, then run three ABBA cycles at 1920x1080 using the pinned Bistro workload: Release for attribution and ShippingPerformance for final acceptance.
- Require the target artifact and ordinary-ray partition to be active, zero render-time pipeline creation after warm-up, and stable reflection-ray request/admission/hit counts.
- Measure the exact `TransparentForwardPass`; use aggregate `TransparentPasses` only when the weighted transparency timings are confirmed zero.
- Keep the change only if p95 improves by at least 5% and 0.05 ms with a positive paired-bootstrap 95% lower bound—approximately 0.20 ms against a 3.90 ms baseline—with no greater than 1% CPU/GPU tail regression, TLAS cost shift, memory-gate failure, or quality failure.
- Re-profile the exact module hash to confirm reduced traversal/candidate activity. If the candidate misses these gates, classify it as rejected or inconclusive and remove it rather than lowering reflection quality.

## Assumptions

- The 3.90 ms observation is provisional attribution evidence; the authoritative baseline is captured after the two higher-priority optimizations.
- Existing user-owned shader, build, and Bistro material-profile changes remain part of the baseline.
- Profiling uses the same GPU, driver, scene, camera, settings, and shader bundle for A/B comparisons; raw profiler evidence remains external if the protected campaign schema cannot represent this transparent stage.
