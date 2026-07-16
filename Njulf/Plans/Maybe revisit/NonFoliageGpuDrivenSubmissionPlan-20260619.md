# Non-Foliage GPU-Driven Submission Plan

This plan replaces the still-relevant parts of `GpuDrivenSubmissionRoadmap.md`.
It is scoped to regular scene meshlets: opaque forward, depth prepass, and non-foliage shadows.

Foliage already has GPU culling buffers, visible-list output, counters, indirect mesh-task dispatch, and sample toggles. Do not use this plan for foliage hardening.

## Current Baseline

- `SceneDataBuilder` still builds CPU meshlet draw lists for opaque, depth, transparent, and shadow buckets.
- `ForwardPlusPass`, `DepthPrePass`, and non-foliage shadow passes still submit CPU-known meshlet counts.
- CPU-side LOD selection still happens while building scene draw lists.
- `SampleInputController` already exposes useful diagnostics controls:
  - `Ctrl+F2` exports a performance snapshot.
  - `Ctrl+F4` toggles GPU timing.
  - `Ctrl+F5` cycles quality presets.
  - `Ctrl+F6` cycles feature isolation.
  - `Ctrl+F7` toggles secondary command buffers.
  - `1`, `2`, and `3` switch between important sample scenarios/views.

## Out Of Scope

- Foliage GPU culling, foliage impostors, and foliage indirect dispatch.
- Transparent sorting.
- Geometry decals.
- CPU debug snapshots and material inspection.
- Skinned mesh special cases beyond preserving the current fallback.
- Particle rendering.

## Step 1: Add A Non-Foliage Submission Settings Surface

Goal: create a guarded prototype path without disturbing the existing renderer.

Implementation:

1. Add a `SceneSubmissionSettings` or equivalent section under `RenderSettings`.
2. Add these settings:
   - `GpuCompactionEnabled`
   - `IndirectMeshletDispatchEnabled`
   - `GpuLodSelectionEnabled`
   - `GpuShadowCompactionEnabled`
   - `ValidationCompareCpuGpuLists`
3. Default all new settings to `false`.
4. Add matching fields to `SceneRenderingData` and `RendererDiagnostics`.
5. Add sample-controller chords for the first two settings only:
   - one toggle for non-foliage GPU compaction
   - one toggle for non-foliage indirect dispatch
6. Print existing CPU counts next to new GPU counts when toggles change.

Acceptance criteria:

- Default rendering is unchanged.
- New settings appear in performance snapshots.
- Sample toggles can enable and disable prototype paths at runtime.

## Step 2: Split Stable Scene Inputs From Per-Frame Draw Lists

Goal: make the current CPU path easier to compare against the future GPU path.

Implementation:

1. Keep existing CPU-built draw-list generation intact.
2. Identify the stable inputs needed by a GPU compaction pass:
   - object records
   - mesh metadata
   - meshlet metadata
   - material indices and render-mode flags
   - transforms
   - static-instance data
3. Ensure these inputs are uploaded when scene content changes, not when only camera visibility changes.
4. Keep CPU draw-list buffers as the fallback source.
5. Add diagnostics for:
   - stable scene input bytes
   - CPU candidate-list bytes
   - whether camera movement rebuilt CPU draw lists

Acceptance criteria:

- With all new GPU submission settings off, output is unchanged.
- `ScenePayloadRebuilt` remains meaningful.
- Performance snapshots distinguish stable scene uploads from CPU candidate-list uploads.

## Step 3: Implement Opaque GPU Compaction

Goal: prove the model on regular opaque meshlets before touching depth, shadows, or LOD.

Implementation:

1. Add per-frame output buffers:
   - compacted opaque meshlet draw buffer
   - visible opaque counter buffer
   - overflow counter
2. Add a compute pass before forward opaque rendering.
3. The compute pass reads stable scene inputs and CPU opaque candidates.
4. The compute pass performs:
   - object or meshlet frustum tests
   - optional Hi-Z occlusion tests
   - material filtering for opaque-only work
5. The compute pass appends visible opaque meshlets into the compacted output buffer.
6. Add barriers from compute writes to task/mesh shader reads.
7. In `ForwardPlusPass`, consume the compacted buffer only when `GpuCompactionEnabled` is true.
8. Keep the existing CPU draw-list path as fallback.

Acceptance criteria:

- Opaque rendering matches the CPU path visually.
- GPU visible counts reconcile with candidate, frustum-rejected, Hi-Z-tested, Hi-Z-rejected, emitted, and overflow counters.
- Toggling the prototype path during runtime does not require scene reload.
- If buffers overflow or validation fails, the renderer falls back to the CPU path.

## Step 4: Add CPU/GPU List Validation Mode

Goal: make correctness testable before optimizing around the new path.

Implementation:

1. When `ValidationCompareCpuGpuLists` is true, run CPU and GPU opaque list generation in the same frame.
2. Read back a bounded debug sample of GPU-visible meshlet commands.
3. Compare:
   - emitted count
   - object index
   - mesh index
   - meshlet index
   - material path bucket
4. Report mismatch count and first mismatch in diagnostics.
5. Do not block normal rendering on validation readback unless a debug validation mode explicitly requests it.

Acceptance criteria:

- Validation can prove equality with Hi-Z disabled.
- With Hi-Z enabled, diagnostics clearly separate expected occlusion differences from invalid mismatches.
- Performance snapshot captures validation status.

## Step 5: Add Opaque Indirect Mesh-Task Dispatch

Goal: remove CPU-known visible opaque meshlet counts from the draw path.

Implementation:

1. Add a GPU-written indirect mesh-task dispatch argument buffer for opaque forward.
2. Write the dispatch argument from the compaction pass.
3. Use `CmdDrawMeshTasksIndirect` when:
   - `GpuCompactionEnabled` is true
   - `IndirectMeshletDispatchEnabled` is true
   - the device path supports it
   - validation has not forced fallback
4. Keep direct dispatch using GPU buffer capacity as a debug fallback.
5. Add diagnostics:
   - indirect enabled
   - generated task count
   - max visible capacity
   - overflow count
   - fallback reason

Acceptance criteria:

- Forward opaque rendering works without CPU-readable visible count.
- Direct CPU path, direct GPU-capacity path, and indirect path are all selectable.
- Unsupported devices automatically stay on the CPU path.

## Step 6: Move Non-Foliage LOD Selection To GPU

Goal: stop camera-dependent LOD changes from requiring CPU draw-list rebuilds.

Implementation:

1. Extend GPU mesh metadata with LOD ranges already used by CPU selection.
2. Upload per-object LOD parameters needed for distance or screen-size decisions.
3. Implement LOD selection inside the compaction pass.
4. Preserve hysteresis or add a GPU-friendly replacement.
5. Track diagnostics:
   - LOD0/LOD1/LOD2 emitted counts
   - missing LOD fallback count
   - LOD transition count
   - clamp or overflow count
6. Keep CPU LOD selection as fallback when `GpuLodSelectionEnabled` is false.

Acceptance criteria:

- Camera movement changes LOD without CPU candidate-list rebuilds.
- LOD transitions are stable enough for normal camera movement.
- CPU and GPU LOD diagnostics reconcile in validation mode.

## Step 7: Extend Compaction To Depth Prepass

Goal: reuse GPU-visible opaque lists for depth without duplicating the full architecture.

Implementation:

1. Decide whether depth can consume the same opaque compacted list or needs a separate solid/masked split.
2. Add separate compacted buffers only if material or shader path differences require them.
3. Update `DepthPrePass` to consume GPU compacted lists behind settings.
4. Preserve the current solid and masked CPU buffers as fallback.
5. Add diagnostics for depth compacted count and fallback reason.

Acceptance criteria:

- Depth output matches current behavior.
- Hi-Z build input remains valid.
- Forward opaque and depth prepass can be toggled independently during debugging if needed.

## Step 8: Extend Compaction To Non-Foliage Directional Shadows

Goal: move regular meshlet shadow visibility/list generation after opaque forward is stable.

Implementation:

1. Start with directional shadows only.
2. Keep CPU cascade setup and light selection.
3. Generate one visible meshlet list per cascade.
4. Use separate counters and capacities per cascade.
5. Add indirect dispatch only after direct GPU compacted shadow lists validate.
6. Leave foliage shadow code untouched.

Acceptance criteria:

- Shadow maps visually match the current CPU-built list path.
- Diagnostics report candidate, emitted, rejected, and overflow counts per cascade.
- Camera and light-frustum changes avoid broad CPU draw-list rebuilds where possible.

## Step 9: Extend To Local Shadows Only If Diagnostics Justify It

Goal: avoid adding complexity where budgets do not require it.

Implementation:

1. Use performance snapshots to confirm spot or point shadow list construction is a real CPU cost.
2. If justified, add GPU compacted lists for selected spot lights first.
3. Handle point shadows after spot lights because cube-face bucketing adds more cases.
4. Preserve CPU light selection and budgeting.

Acceptance criteria:

- Local shadow GPU compaction only ships if it reduces measured CPU cost.
- Per-light or per-face overflow is reported clearly.
- CPU fallback remains functional.

## Step 10: Retire Or Reframe Old Diagnostics

Goal: remove ambiguity once the GPU path is reliable.

Implementation:

1. Rename diagnostics that say `SubmittedCpu` if they can include GPU-submitted work.
2. Keep explicit CPU-only diagnostics for fallback and validation.
3. Add final performance snapshot fields for:
   - active submission mode
   - CPU candidate count
   - GPU emitted count
   - indirect task count
   - fallback reason
4. Update sample-controller print output to use the final names.

Acceptance criteria:

- Performance snapshots make it obvious which submission path rendered the frame.
- Old CPU-only names are not reused for mixed CPU/GPU values.
- Debug output stays short enough to read in the sample console.

## Done Criteria

- Regular opaque scene meshlets can render from GPU-compacted visible lists.
- Opaque forward can use GPU-written indirect mesh-task dispatch.
- GPU LOD selection can be enabled without camera-driven CPU draw-list rebuilds.
- Directional shadow meshlet lists can be GPU-compacted or fall back cleanly.
- Foliage behavior remains unchanged.
- Transparent, decal, debug snapshot, skinned edge cases, and particle paths remain on explicit fallback or separate future plans.
