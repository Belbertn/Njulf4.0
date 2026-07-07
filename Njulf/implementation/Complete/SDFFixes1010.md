# SDF Round 3 — Fix the Thin-Feature Fix: Surface Acne + Sealed Openings

## Context

Commit `6cdff10` implemented the SDFFix99 thin-feature preservation. It *did* cure the coarse-cascade dropout (the round-1 teal regions now trace hits), but it over-triggers: every flat surface now shows voxel-scale "acne", openings get sealed by phantom membranes (the shed doorway is covered by a bright cascade-3 green surface), DDGI surface-cache fallbacks jumped from <1% to 6–28%, and scroll-frame brick GPU cost rose ~10× (rolling max 2.6–4.6 ms vs ~0.4 ms baseline).

## Root cause of the acne (exact)

`SampleMeshSdfPreservingThinFeatures` (`global_sdf_update.comp`) flips a voxel to a negative core when:
`0 < centerDistance < 0.75×voxel` **AND any of 14 samples within ±0.5 voxel is negative.**

That second condition is true for **every voxel adjacent to any surface, thick or thin** — being within ~half a voxel of geometry means some half-voxel probe lands inside it. So the entire first voxel shell above every floor/wall flips negative, moving the traced zero-crossing outward by an amount that depends on where the surface sits relative to the voxel grid → quantized ±1-voxel relief = the acne (visible at every cascade, including cascade 0). The same blanket inflation closes 1–2-voxel openings in coarse cascades → the green membrane over the shed doorway, and the dark garbage blobs in the previously-teal region.

The intended predicate (SDFFix99 wording) was "no sign change would survive quantization" — i.e. flip **only when the negative core is about to be lost** (wall thinner than a voxel, no voxel center inside it). The implementation replaced that with "near any surface".

Compounding it, the trace-side backstop `TryIntersectTrilinearNearMinimum` (`global_sdf.glsl`) synthesizes hits whenever a ray's in-cell minimum dips below **0.45×voxel with no sign change, unconditionally** — phantom grazing hits on every edge/corner, contributing to the lumpy silhouettes and the surface-cache fallback spike (rays hit phantom surfaces where no card projects: `RejectDepthUv` 168/841/430).

## The fix (all in `global_sdf_update.comp` + `global_sdf.glsl`)

1. **Correct the writer predicate** — flip to a negative core only when the surface would otherwise vanish at this cascade's resolution:
   - Gate: `0 < centerDistance < 0.5×voxel` (tighter than 0.75).
   - Thinness test: sample the 6 face neighbors at ±1.0 **full** voxel. If **any** of them is negative, the feature is thick enough to keep a negative core at a voxel center → **do not modify** (this is what kills the acne: for a thick floor, the sample one voxel into the floor is negative → untouched).
   - If **all six** full-voxel samples are positive AND the ±half-voxel neighborhood minimum is negative (a crossing genuinely exists nearby), write `min(centerDistance, -0.35×voxel)`.
   - This is also cheaper: 6 full-voxel + up to 6 half-voxel samples, only inside the tighter gate — brick cost should drop back near baseline (verify via `GpuGlobalSdfBrickMicrosecondsRollingMax`).
2. **Remove `TryIntersectTrilinearNearMinimum`** (or gate it to ≤0.15×voxel if a residual grazing artifact reappears). With correct writer-side preservation it is redundant and it is the second inflation source.
3. Keep everything else from `6cdff10` (the `LocalToWorldAxisScale` rename, diagnostics).

Files: `Njulf/Njulf.Shaders/global_sdf_update.comp` (`SampleMeshSdfPreservingThinFeatures`), `Njulf/Njulf.Shaders/global_sdf.glsl` (delete the near-minimum helper + its call site), `Njulf/Njulf.Tests/ShaderBuildTests.cs` (drop/adjust the tests added for the removed helper). Write the round doc as `Njulf/implementation/SDFFix110.md` (SDFFix99.md moves to Complete).

## Expected results / verification

1. Build + `ShaderBuildTests`, `GlobalSdfManagerTests`.
2. FullSlice captures: flat floors/walls smooth again (no relief) at all cascades; shed doorway open (no green membrane); round-1 teal regions still resolved (thin walls keep hitting in c2/c3); melted doorframe unchanged (separate under-resolution issue, future work: minimum bake resolution for heavily downscaled instances).
3. Numbers: `DdgiSurfaceCacheFallbackPercent` back under ~1%; `GpuGlobalSdfBrickMicrosecondsRollingMax` back near ~0.5 ms per slab.
4. Also check the yellow/black checkerboard patch in the border (screenshot 2, top-left) after the fix — likely NaN/undefined color from the phantom-hit path; if it persists, investigate separately.

Push to the working branch (`Simplified`).