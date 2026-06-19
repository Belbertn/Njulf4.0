# GPU-Driven Submission Roadmap

This roadmap captures the larger architectural next step for the renderer: moving from CPU-built per-pass candidate lists toward GPU-generated visible lists and indirect mesh-task submission.

The renderer is already partially GPU-driven. Task shaders perform meshlet frustum and HiZ rejection for several paths, but the CPU still builds and uploads candidate meshlet draw lists, and render passes still submit CPU-known candidate counts with direct mesh-task dispatch. A fully GPU-driven path would make camera movement and visibility changes mostly GPU-side.

## Why This Matters

GPU-driven submission becomes valuable when diagnostics show that CPU-side scene build, upload, culling-list construction, or command recording scales poorly with scene size.

Strong signals:

- `ScenePayloadRebuilt` frequently flips during camera movement.
- CPU scene build/upload time is high.
- meshlet candidate counts are large while emitted visible counts are much lower.
- command recording or pass submission overhead grows with scene size.
- shadow, transparent, or debug list generation starts dominating CPU frame time.

This is a high-potential optimization, but it is a bigger architectural change than the immediate optimization plan. It should come after stall attribution, render-target rebuild stabilization, staging overflow fixes, and low-risk CPU cleanup.

## Target Architecture

1. CPU uploads stable scene data when scene content changes:
   - object records
   - mesh metadata
   - meshlet metadata
   - material indices
   - transforms
   - visibility flags
   - static/dynamic classification

2. GPU compute or task shaders evaluate per-frame visibility:
   - camera frustum culling
   - HiZ occlusion culling
   - optional LOD selection
   - optional per-pass visibility masks

3. GPU writes compacted outputs:
   - visible meshlet draw lists
   - visible object lists
   - per-bucket counts
   - indirect dispatch arguments
   - diagnostics counters

4. Render passes consume GPU-generated data:
   - opaque forward
   - depth prepass
   - motion vectors
   - shadows
   - eventually transparent/decal paths where practical

## Phase 1: Opaque-Only GPU Compaction Prototype

Goal: prove the model on the simplest high-value path without disturbing transparency, shadows, or debug tooling.

Implementation:

- Keep the existing CPU-built static candidate meshlet list.
- Add a GPU-visible output buffer for compacted opaque meshlet commands.
- Add a counter buffer for visible opaque meshlet count.
- Add a compute pass or task/compute preparation pass that:
  - reads static opaque candidates
  - performs frustum and optional HiZ tests
  - appends visible candidates to the compacted output buffer
  - writes visible count
- Add barriers so the forward pass can consume the compacted list.
- Keep a setting to switch between the existing path and the prototype path.

Acceptance criteria:

- opaque output matches the existing path visually.
- GPU counters reconcile candidate, culled, and emitted counts.
- CPU scene payload rebuilds are not required for camera movement in the prototype path.
- fallback to the existing path is trivial when validation fails or device support is insufficient.

## Phase 2: Indirect Mesh-Task Dispatch

Goal: remove CPU-known visible counts from the draw path.

Implementation:

- Add an indirect dispatch argument buffer written by the GPU compaction pass.
- Use device-supported mesh-task indirect dispatch if available.
- Preserve direct dispatch fallback for unsupported devices or validation.
- Add diagnostics:
  - indirect path enabled
  - generated task count
  - max visible capacity
  - overflow count

Acceptance criteria:

- forward opaque rendering consumes GPU-written dispatch arguments.
- CPU no longer needs to know the final visible opaque meshlet count before drawing.
- fallback path remains functional.

## Phase 3: GPU LOD Selection

Goal: move camera-dependent meshlet LOD choice out of CPU scene payload rebuilds.

Implementation:

- Extend mesh metadata so GPU shaders can select the correct meshlet LOD range.
- Upload per-object LOD settings and previous/current LOD state where needed.
- Implement screen-size or distance-based LOD selection in the GPU culling pass.
- Preserve hysteresis or temporal stability to avoid visible popping.
- Report diagnostics:
  - LOD0/LOD1/LOD2 emitted counts
  - LOD transitions
  - LOD clamp/overflow events

Acceptance criteria:

- camera movement changes LOD selection without CPU payload rebuilds.
- visual transitions remain stable.
- diagnostics match or improve on current CPU LOD reporting.

## Phase 4: Extend To Shadows

Goal: apply GPU-driven list generation to shadow passes once opaque forward is stable.

Implementation:

- Generate per-cascade directional shadow visible lists.
- Generate local shadow visible lists for selected spot/point lights.
- Keep light selection on CPU initially; move only meshlet visibility/list generation to GPU.
- Use separate counters and capacities per shadow bucket.

Acceptance criteria:

- shadow maps match the existing CPU-built list path.
- camera movement and light-frustum changes avoid broad CPU draw-list rebuilds where possible.
- shadow diagnostics show candidate, culled, emitted, and overflow counts per bucket.

## Phase 5: Handle Complex Paths Carefully

Goal: avoid forcing unsuitable workloads into the first GPU-driven version.

Defer until opaque and shadows are stable:

- transparent sorting
- geometry decals
- debug CPU snapshots
- material inspection paths
- skinned mesh edge cases
- particle/decal interactions

These paths may need hybrid handling. Transparent sorting in particular may remain CPU-assisted or use a specialized GPU sort later.

## Required Infrastructure

- GPU append/consume buffers with explicit capacity and overflow handling.
- Counter reset and readback strategy.
- Per-frame buffer ownership and barriers.
- Indirect dispatch argument layout and validation tests.
- Capability detection for mesh-task indirect support.
- Debug tooling to compare CPU-built and GPU-built visible lists during development.
- Diagnostics that expose both candidate and emitted counts.

## Risks

- Added synchronization complexity can erase gains if barriers are too conservative.
- GPU compaction can create memory pressure if capacities are oversized.
- Indirect dispatch support may vary by device and driver.
- Transparent and debug tooling paths may still need CPU-readable state.
- Validation and reproducibility become harder when visibility is GPU-generated.

## Recommended Position In The Optimization Plan

This should be treated as a strategic Phase 6 after:

1. global stall attribution,
2. render-target rebuild stabilization,
3. staging overflow lifetime fixes,
4. cached per-frame matrices and secondary command-pool cleanup,
5. renderer-owned feature isolation.

The first concrete milestone should be an opaque-only GPU compaction prototype with a reliable fallback to the existing CPU-built candidate-list path.
