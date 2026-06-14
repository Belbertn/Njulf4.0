# Renderer Architecture 03 - GPU-Driven Visibility And Meshlet Compaction Plan - 2026-06-14

Target outcome: visibility, LOD selection, occlusion testing, render-list categorization, and meshlet draw-list compaction run on the GPU. Depth, shadows, forward, transparents, and diagnostics consume compacted buffers generated from the persistent GPU scene.

This plan is the main architectural performance upgrade. The final state must remove CPU-authored per-pass meshlet lists from the production path. CPU comparison paths may exist only during migration and must be deleted after validation.

## Non-Negotiable Requirements

1. GPU culling produces compacted draw/dispatch lists for each consuming pass.
2. Culling supports frustum, LOD, occlusion, shadow cascades, material categories, and feature flags.
3. All counters are GPU-generated and reconciled in diagnostics.
4. Sorting and transparency handling are deterministic enough for stable output.
5. The CPU never loops over all meshlets each frame in the final production path.
6. The old CPU list-generation architecture is removed after GPU output is validated.

## Phase 0 - Establish Comparison Baseline

1. Add a debug-only comparison mode:
   - CPU builds current lists.
   - GPU builds new lists.
   - diagnostics compare counts, hashes, and sampled entries.
2. Hash each list:
   - depth solid.
   - depth masked.
   - directional shadow per cascade.
   - spot shadow per light.
   - point shadow per face.
   - opaque forward.
   - transparent forward.
3. Capture scenes:
   - small static scene.
   - many objects.
   - many meshlets.
   - alpha-tested foliage.
   - transparent objects.
   - local shadows.
   - occlusion stress.
4. Add clear tolerances:
   - frustum and LOD choices must match where algorithms are equivalent.
   - occlusion can differ only when using explicitly different GPU Hi-Z timing.

Acceptance criteria:
- Existing CPU path can be used as a temporary oracle during migration.
- Comparison mode reports actionable mismatches.

## Phase 1 - GPU Visibility Buffers

1. Add persistent and per-frame buffers:
   - object visibility input.
   - instance visibility input.
   - meshlet candidate ranges.
   - visibility flags output.
   - LOD selection output.
   - occlusion test output.
   - draw count counters.
   - compacted draw buffers per pass.
2. Add atomic counters for each output list.
3. Add counter reset pass before culling.
4. Add overflow detection:
   - if a compacted list exceeds capacity, set an overflow flag.
   - diagnostics report required capacity.
   - production path must grow buffers safely on the next frame.
5. Add buffer barriers through the render graph.

Acceptance criteria:
- GPU can produce empty lists, full lists, and overflow diagnostics without crashing.

## Phase 2 - Frustum And Category Culling

1. Write compute shader for object-level frustum culling.
2. Inputs:
   - GPU scene object buffer.
   - camera frustum planes.
   - render category masks.
   - material flags.
3. Outputs:
   - visible object IDs.
   - object category flags.
   - per-category counts.
4. Add support for:
   - opaque.
   - masked.
   - transparent.
   - geometry decals.
   - shadow casters.
   - particles/emitters if represented in scene.
5. Validate against CPU frustum tests.

Acceptance criteria:
- GPU-visible object counts match CPU counts in non-occlusion scenes.
- Category counts match material metadata.

## Phase 3 - GPU LOD Selection

1. Add LOD selection compute pass using:
   - camera position.
   - projected screen size.
   - material/mesh LOD policy.
   - per-object LOD bias.
   - hysteresis state.
2. Support authored LODs and generated fallback LODs.
3. Store selected LOD per object or instance.
4. Add hysteresis to avoid flicker.
5. Add per-pass LOD overrides:
   - shadows may use lower LOD bias.
   - reflections may use lower LOD bias.
   - impostors may replace far foliage.
6. Expose LOD decisions in diagnostics and debug overlays.

Acceptance criteria:
- LOD transitions are stable under camera jitter and TAA.
- Selected LODs match CPU reference implementation in deterministic tests.

## Phase 4 - Meshlet Expansion And Compaction

1. For each visible object, read selected meshlet LOD range.
2. Expand meshlet candidates in compute.
3. Frustum test meshlet bounds if object is partially inside the frustum.
4. Append surviving meshlets into compacted lists:
   - solid depth.
   - masked depth.
   - opaque forward.
   - transparent candidate.
   - shadow candidate lists.
5. Store draw command fields:
   - meshlet index.
   - object or instance ID.
   - material index.
   - mesh metadata index.
   - LOD level.
6. Use prefix-sum or atomic append initially only if contention is measured acceptable. If atomics bottleneck, implement prefix-sum compaction:
   - mark pass.
   - prefix pass.
   - scatter pass.
7. Add capacity growth policy based on high-water mark.

Acceptance criteria:
- GPU-generated compacted lists can directly drive mesh task dispatch.
- CPU no longer uploads meshlet draw lists in production mode.

## Phase 5 - Hi-Z Occlusion

1. Ensure depth prepass produces scene depth before Hi-Z.
2. Build Hi-Z from scene depth.
3. Add object-level occlusion testing using bounding sphere or box.
4. Add meshlet-level occlusion testing for expensive meshes.
5. Add occlusion bias settings per content type:
   - regular opaque.
   - foliage.
   - skinned.
   - large objects.
6. Use previous frame Hi-Z only when latency policy is explicit.
7. Add conservative fallback for newly visible or moving objects.
8. Track counters:
   - tested.
   - rejected.
   - accepted.
   - skipped due to invalid history.
9. Validate no visible popping in stress scenes.

Acceptance criteria:
- Occlusion rejection reduces emitted meshlets in occlusion scenes.
- No object disappears incorrectly in validation camera paths.

## Phase 6 - Shadow Visibility

1. Add directional cascade culling compute.
2. Add spot light culling compute.
3. Add point light face culling compute.
4. Reuse GPU scene bounds and selected LOD policy.
5. Support shadow caster flags and material alpha-test flags.
6. Produce compacted shadow meshlet lists per cascade/light/face.
7. Add list capacity planning for worst-case local shadows.
8. Add diagnostics per shadow list.

Acceptance criteria:
- Shadow pass consumes GPU-generated compacted lists.
- CPU no longer rebuilds shadow meshlet lists in production.

## Phase 7 - Transparent Sorting And OIT Integration

1. Generate transparent meshlet candidates on GPU.
2. Compute sort key:
   - depth or distance.
   - material layer.
   - material index.
   - object ID.
   - meshlet index.
3. Implement GPU sort:
   - radix sort for large lists.
   - bitonic sort only for small fixed-size debug cases.
4. Provide path for weighted OIT once implemented:
   - no full sort required for weighted OIT.
   - still sort alpha-blended fallback path.
5. Validate stable output with moving camera and moving transparent objects.

Acceptance criteria:
- Transparent rendering remains stable.
- Sorting cost is measurable and can be bypassed for OIT modes that do not need it.

## Phase 8 - Indirect Dispatch Or Direct Mesh Task Dispatch

1. Select execution model:
   - if mesh shader indirect dispatch is supported, generate indirect dispatch commands.
   - otherwise dispatch by compacted count read through safe CPU-visible delayed counters or bounded dispatch with shader-side exit.
2. Avoid same-frame GPU-to-CPU readbacks.
3. Store draw counts in GPU counters used by indirect commands.
4. Add graph barriers from compaction to draw/dispatch.
5. Validate vendor support and fallback policy at device initialization.

Acceptance criteria:
- Main render passes execute without CPU knowing exact visible meshlet counts for the current frame.
- No CPU readback is required in the frame loop.

## Phase 9 - Diagnostics And Debugging

1. Add GPU visibility diagnostics:
   - input objects.
   - frustum culled objects.
   - LOD distribution.
   - meshlet candidates.
   - meshlets emitted per pass.
   - occlusion tested/rejected.
   - shadow list counts.
   - transparent sort count and time.
   - overflow events.
2. Add debug overlays:
   - object visibility state.
   - selected LOD.
   - occlusion rejected bounds.
   - meshlet bounds for emitted meshlets.
   - shadow caster coverage.
3. Add performance snapshots before and after CPU path removal.

Acceptance criteria:
- A performance snapshot explains where meshlets were removed and which pass consumed them.

## Phase 10 - Remove Old Architecture

Delete after GPU path validates:

1. CPU per-frame meshlet draw command build for production.
2. CPU opaque/solid/masked/transparent list upload buffers used only by old path.
3. CPU shadow meshlet command build for production.
4. Redundant CPU occlusion/LOD fields no longer used by rendering.
5. Temporary comparison mode after a fixed burn-in period, or keep only behind a non-shipping test flag.

Acceptance criteria:
- The production renderer cannot accidentally fall back to CPU draw-list generation.
- Old upload buffers are removed from diagnostics and budget accounting.

## Validation

1. Unit tests:
   - sort key packing.
   - LOD selection math.
   - capacity growth math.
   - counter reconciliation.
2. GPU tests/manual captures:
   - list buffer content inspection.
   - overflow simulation.
   - occlusion stress.
   - shadow correctness.
   - transparency stability.
3. Performance validation:
   - CPU scene build time drops with high object counts.
   - upload bytes drop because draw lists are no longer uploaded.
   - GPU culling time is less than saved rendering work in target scenes.
   - no same-frame readback stalls.

## Definition Of Done

1. GPU builds compacted visibility lists for production passes.
2. Depth, shadows, forward, and transparency consume GPU-generated lists.
3. CPU list generation and its buffers are removed from production.
4. Diagnostics prove the work reduction and expose all overflows or fallbacks.

## Implementation Notes - 2026-06-14

- Phase 0 comparison primitives are implemented through `GpuVisibilityListSignature` and `GpuVisibilityComparisonResult`, covering counts and deterministic list hashes.
- Phase 1 host contracts are implemented through `GPUVisibilityCounters`, `GPUVisibilityPushConstants`, `GPUVisibilitySortKey`, and `GpuVisibilityCapacityPlanner`.
- Phase 7 sort-key packing is implemented and tested through `GpuVisibilitySortKeyPacker`.
- Production GPU visibility now includes Hi-Z object occlusion, meshlet expansion/compaction, per-list counters, indirect mesh-task dispatch arguments, per-cascade/per-light shadow list specialization, transparent sort bypass for weighted OIT, delayed counter readback, frame diagnostics, resource-inventory ownership, and capacity growth.
- Weighted blended OIT now has graph-owned accumulation/revealage targets, a two-attachment transparent pipeline, a fullscreen composite pass into HDR scene color, fixed bindless texture indices, diagnostics, and shader/layout tests.
- The production renderer cannot fall back to CPU meshlet-list generation: `SceneDataBuilder` has CPU meshlet draw-list generation disabled, production bindless draw-list indices are owned by `GpuVisibilityBufferSet`, and depth/shadow/forward/transparent passes consume those GPU-generated buffers.
- Remaining work is broader visual/performance validation across stress scenes, not missing Plan 3 implementation.
