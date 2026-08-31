# Post-`6d55143` Performance Regression and Recovery

## Summary

The slowdown is primarily a shader-path regression introduced in `4d43937b`, re-enabled by `67dada86`, and substantially amplified by `460c3ef7`. Commit `76037503` did not cause it—it only moved two Markdown files.

A full-content Bistro comparison on the same RTX 3060 Laptop GPU shows:

| Metric | Pre-big `c0857e5d` | Post-range | Change |
|---|---:|---:|---:|
| GPU frame p95 | 41.179 ms | 190.462 ms | 4.63× slower |
| `ForwardPlusPass` p95 | 14.451 ms | 170.952 ms | 11.83× slower |
| Transparent p95 | 18.051 ms | 8.557 ms | 52.6% faster |
| Receiver cache p95 | 2.517 ms | 8.208 ms | 3.26× slower |
| AO + blur p95 | 0.694 ms | 2.598 ms | 3.74× slower |
| CPU frame p95 | 7.070 ms | 14.363 ms | 2.03× slower |
| Tracked GPU memory | 1.728 GB | 2.440 GB | +679.7 MiB |

The comparison uses the same scene asset hash, 3,653 visible objects and 83,475 opaque meshlets. The source evidence is in the pre-big `artifacts/approved-plan-baseline-20260827/bistro-perf/reference/reference.json` capture and the post-range `.perf-loop-runs/campaign-8f84ae8a/references/Release/bistro-stationary/reference.json` capture in the quality-locked performance worktree.

The causal proof is the existing experimental receiver split: separating cache-accepted and exact-fallback fragments into distinct native shaders reduced Forward p95 from 156.264 to 31.159 ms and frame GPU p95 from 178.427 to 52.610 ms. The complete positive candidate stack reached 42.164 ms. This demonstrates that the main loss is combined shader size, register pressure, divergence and occupancy—not synchronization or general Vulkan overhead. The detailed evidence is recorded in `implementation/QualityLocked1080p60CandidateLedger-20260830.md` in the quality-locked performance worktree.

## Commit Attribution

| Commit | Attribution |
|---|---|
| `5d065045` | Added/fixed C5 near-field residual work, including a finalize pass. Minor update/relight cost; not the stationary bottleneck. |
| `f094f4b6` | Intentionally enlarged Bistro by loading `BistroInterior` alongside the exterior. This explains part of the loss relative to the exact `6d55143` scene, but `c0857e5d` still rendered the enlarged scene at 41.179 ms. |
| `c0857e5d` | Texture decoding/cooking fixes only. Serves as the last measured good full-Bistro anchor. |
| `4d43937b` | First major regression wave: TemporalAdaptive receiver cache for DdgiHigh, high GTAO at 6×8 samples, local C5 scheduling, surface-aware cache shaders and the combined cache/exact Forward graph. |
| `3e084abf` | Secondary regression: screen-space-error LOD, sided raster partitions, specular AA and VRS plumbing. Forward indirect draws increased from 1 to 6; compacted opaque meshlets rose 39%. |
| `6006e18f` | Mostly loading, upload and cache restructuring. Relevant to startup and allocations, with no evidence that it caused the steady-state GPU collapse. |
| `2569b275` | Recovery commit: restored exact receiver gathering and disabled transport acceleration after correctness failures. |
| `c071dd2e` | Prepared both receiver pipeline families during background loading. Primarily startup behavior; no major steady-state regression. |
| `abb5eaf4` | Expanded current-pose dynamic geometry, BLAS and DDGI refinement. Adds motion/update work and memory, but does not explain stationary Forward time. |
| `67dada86` | Primary activation commit: reversed the `2569b275` rollback, made TemporalAdaptive the default, enabled transport acceleration and production GI features, and enabled hierarchical LOD plus physical meshlet streaming. |
| `460c3ef7` | Main amplifier: `Auto` reflections now select Adaptive, Forward writes the larger v2 reflection/lobe payload, recursive glossy transport and CSM temporal are enabled, and automatic planar reflections plus physical-residency coordination were added. |
| `8bbd41d1` | Predominantly progressive startup and pipeline-binary work. Some visibility-compaction plumbing, but no demonstrated dominant steady-state cost. |
| `adba46e7` | Corrected sided-stream capacities. Correctness/memory change rather than a shader throughput root cause. |
| `59cbcc63` | Cooked-content enforcement and tooling. Measured textures, materials and scene workload remain equivalent to the pre-big capture. |
| `76037503` | Runtime-neutral: two documentation-only renames. |

Secondary confirmed defects are:

- The new LOD policy changed from 46,816 decimated opaque meshlets to zero, shifting most emitted geometry toward LOD0.
- GTAO, receiver-cache compute, hybrid DDGI base and CSM temporal add several milliseconds, but together are far smaller than the approximately 156 ms Forward regression.
- Meshlet physical residency oversubscribed Sponza’s 4,096-page cache with 4,545 pages, producing 425,988,243 invalid mappings, 1,073 evictions and a scene-payload rebuild every frame. Correct admission reduced Sponza CPU p95 from 8.162 to 3.350 ms.
- DDGI refinement used a changing CPU pool slot as GPU ABI identity, causing 216–532 ms motion stalls and cold descriptor/resource recreation.
- Automatic planar reflections add repeatable motion-tail spikes, though they are inactive in the stationary Bistro capture.
- `WaitForFrameFence` rising from 41.11 to 183.59 ms is GPU backpressure, not the initiating CPU problem. `ForwardGiGatherPass` is also an inclusive alias of `ForwardPlusPass` and must not be counted twice.

## Recovery Changes

- Preserve DdgiHigh quality and public settings. Do not solve this by disabling GI, GTAO, reflections, shadows, textures or the Bistro interior.
- Port the proven positive changes from the performance branch in dependency order:
  - Remove redundant directional projection and its maximum 56 discarded coefficient reads from hybrid cache receivers.
  - Reuse resolve-validated cache geometry instead of reconstructing it per fragment.
  - Split cache-accepted and exact-fallback receivers into complementary compile-time shader variants.
  - Compile incompatible diagnostic/material branches and inactive bent-normal logic out of those production variants.
  - Spatially schedule cache gathers and stage each 6×6 resolve neighborhood once per 8×8 tile.
  - Correct full-footprint meshlet-residency admission and stable DDGI refinement-lane identity.
- Do not retain rejected candidates `MR-004`, `MR-007`, `MR-010`, `MR-013` or `MR-014`; they were neutral, sub-threshold or visually incorrect.
- Revisit the screen-space LOD policy after the dominant shader fix, restoring useful decimation without changing geometric error limits.
- Keep public APIs and serialized quality settings unchanged. Changes are internal to Forward shader variants, mesh-pipeline selection, cache scheduling and residency/refinement identity.

## Test Plan

- Work from a clean branch based on `76037503`; the current workspace’s 91 tracked and 19 untracked WIP changes remain excluded.
- Run three-cycle ABBA captures in Release and ShippingPerformance at native 1920×1080 DdgiHigh for Bistro stationary, motion and relight, plus Sponza low/high stationary and horizontal/vertical motion.
- Require the existing quality contract: all 423 shaders validate, full focused/unit suites pass, FLIP p95 ≤ 0.02, luminance shift within the locked limits, no secondary-pass regression above 1%, and complete DDGI/Sponza capture contracts.
- Initial recovery acceptance is reproduction of the demonstrated cumulative result: GPU p95 ≤ 42.218 ms and Forward p95 ≤ 22.522 ms on the same hardware and workload. The separate 1080p60 target remains GPU p95 ≤ 10 ms, CPU p95 ≤ 6 ms and frame p99 ≤ 16.67 ms.
- Verify zero Sponza invalid physical mappings/payload rebuilds and zero DDGI capacity transitions or descriptor registrations during the 960-frame vertical route.

## Assumptions

- Scope is committed code from `6d55143` through `76037503`; current uncommitted work is not attributed.
- The exact `6d55143` Bistro contained only the exterior. `c0857e5d` is used to isolate renderer regressions after the intended interior-content increase.
- Visual quality and feature coverage are fixed; startup performance is reported separately from steady-state and motion performance.
