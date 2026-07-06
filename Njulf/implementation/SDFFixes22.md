# Global SDF Stability — Snapshot Analysis & Fix Plan

## Context

Five diagnostic snapshots (png + json) were captured: stationary, moving, and one per debug view for cascades 0–2. The SDF is more stable than before but still shows: speckled arc/ring artifacts on surfaces even when stationary, heavy concentric-ring + wavy distortion while moving, and instability when objects are close to the camera. Analysis of the snapshots against the code found two root-cause bugs and two amplifiers.

## Findings (root causes, ranked)

### 1. Non-conservative SDF distances → rays tunnel through geometry (stationary + close-to-camera artifacts)

`Njulf/Njulf.Shaders/global_sdf_update.comp`:
- Candidate gathering only looks **2 voxels** beyond the brick (`brickPadding`, line 166).
- Bricks with no candidates write `maxExtent` = the **whole cascade extent** (lines 260–262), which the encoder clamps to +32 voxels.
- Bricks *with* candidates store the distance to their own candidate set only — a mesh just outside the 2-voxel padded bounds is invisible, so stored values above ~2 voxels can wildly overestimate the true distance.

The sphere tracer (`global_sdf.glsl:346-352`) trusts these values and steps `d − 1.5·voxel`. A sample landing in an "empty" brick next to a wall reads up to 32 voxels and jumps straight through the wall. Whether a ray tunnels depends on exactly where its samples land → per-pixel hit/miss dither (the speckled arcs in the stationary screenshot) and frame-to-frame shimmer. Near the camera each voxel covers many pixels, so the effect is magnified — matching the "unstable when an object is close to the camera" symptom.

### 2. DDGI probe-scroll requests re-bake SDF bricks (movement churn)

`Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs:446-447` feeds `ddgiLayout.DirtyProbeRequests` into `MarkDirtyProbeRequest` → `MarkDirtyWorldBounds`, which dirties bricks in **all four** SDF cascades. But `DirtyProbeRequests` are probe-clipmap **scroll slabs** (`DdgiFrameLayout.BuildDirtyProbeRequests`, built from `clipmapUpdate.DirtyProbeCount`) — probes that need re-*tracing*. The SDF depends only on scene geometry; probe movement never changes it.

This explains the moving snapshot: `GlobalSdfDirtyBrickBacklog: 28549` while `GlobalSdfScrollDeltaCells: 0`, and 925 of 1024 brick writes being **empty** (90% of budget spent re-baking unchanged air). The churn also starves the bricks that genuinely need re-baking.

### 3. Stale scroll-band bricks are sampled before re-bake (movement rings/waves)

`PrepareUpdateJobs` order (`GlobalSdfManager.cs:67-69`): `UpdateCascadeClipmaps` (scroll + new ring offset) → `BuildCascadeMetadata` (publishes new offsets to GPU) → budget-limited bake. A 1-cell scroll invalidates a 24×24 = 576-brick slab per cascade, but the per-cascade budget share (of the 1024 floor) can't cover simultaneous slabs across cascades during continuous movement — so the GPU samples remapped physical bricks that still hold the previous logical bricks' distances. This produces the wavy floor / concentric-ring distortion in the moving screenshot; it settles when the camera stops (matches the stationary snapshot: backlog 0, `no-dirty-bricks`).

### 4. Minor: quantized immediate-hit path

`global_sdf.glsl:354-358`: when a sample reads `d ≤ 0` the trace returns the raw sample `t` (quantized by prior step lengths) with a central-difference normal, instead of refining to the zero crossing like the trilinear-cell path does. With fix #1 in place negative overshoots become rare, but this path still causes banding when it fires. (`GLOBAL_SDF_CUBIC_NEWTON_ITERATIONS = 1` also limits root precision at grazing angles.)

Non-issues verified along the way: the toroidal Repeat-wrap filtered sampling is correct (clamped to `[0.5, res−0.5]`, ring offset is whole-brick); the 512→1024 budget difference is just `BacklogBrickUpdateBudgetFloor` (`GlobalSdfManager.cs:121-129`); cascade 1/2 debug-view "X" patterns and all-green fields are expected encodings.

## Fix Plan

### Fix A — make stored distances conservative (`global_sdf_update.comp`)

1. Raise `brickPadding` from 2 to **4 voxels** (line 166) so gathered candidates stay valid over a wider band. Candidate count is small (40 mesh SDFs); cost is negligible.
2. After the candidate min-loop, clamp every voxel's positive distance to the provable bound:
   `safeBound = distanceFromVoxelToBrickAabbSurface + paddingMeters` — meshes outside the padded bounds are at least that far away.
   `distanceMeters = min(distanceMeters, safeBound)` (negatives/interior unaffected by min with a positive bound).
3. Empty bricks (`candidateCount == 0`) write `safeBound` instead of `maxExtent`.

Result: the sphere-trace lower-bound invariant holds; tunneling stops. Trace steps through empty bricks shrink (~4–8 voxels instead of ~30), so `GlobalSdfAverageTraceSteps` will rise somewhat — acceptable, and cross-cascade fallback already covers the far field. Padding is the tunable if step counts climb too much.

### Fix B — stop probe scrolls from dirtying the SDF (`GlobalSdfManager.cs`)

Delete the `DirtyProbeRequests` loop in `ApplyDdgiEvents` (lines 446-447) and the now-unused `MarkDirtyProbeRequest` (lines 453-472). Keep the `DirtyRegions` loop (line 449-450) — those represent real geometry changes (e.g. newly baked mesh SDFs via `MeshSdfManager.MarkNewlyBakedInstanceBoundsDirty`). Keep the `MovementClass` full-invalidation (Teleport/LayoutChanged/FirstActivation) — those are rare, legitimate resets.

### Fix C — guarantee scroll bands re-bake promptly (`GlobalSdfManager.cs`)

In `CalculateEffectiveBrickUpdateBudget` (or at its call site in `PrepareUpdateJobs:102`), raise the effective budget to also cover the current frame's **priority** (scroll-band) bricks: `max(requested, BacklogBrickUpdateBudgetFloor, totalPriorityDirtyBricks)`. Scroll slabs are ≤ ~576 bricks per cascade step and cascades scroll at different rates, so the spike is bounded and occasional. Priority bricks are already selected first per cascade (`SelectDirtyBrickJobs:500-511`); this just ensures the budget split can't leave part of a slab stale for multiple frames. Update the existing unit tests for `CalculateEffectiveBrickUpdateBudget` accordingly (it's `internal` and test-covered).

### Fix D — refine the immediate-hit path (`global_sdf.glsl`)

When `d ≤ 0` at line 354, instead of returning the raw sample `t`: run the same trilinear-cell intersection for the current cell (the machinery at lines 360-377 already exists — reorder so the cell test runs for any in-band sample, with the `d ≤ 0` return as fallback when the cell solve fails). Also bump `GLOBAL_SDF_CUBIC_NEWTON_ITERATIONS` from 1 to 2 for grazing-angle precision.

## Files to modify

- `Njulf/Njulf.Shaders/global_sdf_update.comp` — Fix A
- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` — Fixes B, C
- `Njulf/Njulf.Shaders/global_sdf.glsl` — Fix D
- Whichever test file covers `CalculateEffectiveBrickUpdateBudget` / shader constants (e.g. `ShaderBuildTests.cs`) — adjust expectations

## Verification

1. `dotnet build` + run the existing rendering/shader test suites (`ShaderBuildTests`, GlobalSdfManager unit tests).
2. Re-capture the same five snapshot scenarios and compare json + png:
   - Moving: `GlobalSdfDirtyBrickBacklog` should drop from ~28k to roughly the active scroll slabs (hundreds), `GlobalSdfBricksWrittenEmptyCount` share should collapse, and scroll counters should be the dominant dirty source.
   - Stationary: speckled arcs on walls gone; `DdgiSdfStepExhaustedCount` stays low while `GlobalSdfAverageTraceSteps` rises moderately (watch it doesn't explode — tune padding if so).
   - Cascade0 view close to a wall: zero-crossing band should be stable frame-to-frame.
3. Fly the camera continuously through the scene: floor/wall ring artifacts while moving should be gone or reduced to a brief settle.