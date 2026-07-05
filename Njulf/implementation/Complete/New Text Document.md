# Global SDF at Simplified HEAD (19127df): what's fixed, what's still broken, and the two remaining root causes

## Context

Several rounds of fixes have landed on `Simplified` (latest `19127df` "SDAS"). The
diagnostic now proves the whole *scheduling* chain is healthy — this round of
analysis is about the two **remaining** defects, both introduced by the newest
shader work (`86bab72`), which explain the current screenshot's artifacts:
concentric ring banding across the floor, hit/miss speckle on thin props, and
grazing-angle misses.

## Status of previous fixes (verified against 19127df — do NOT redo these)

- **FIXED — brick scheduling (old Bug C):** the camera-stationary gate
  (`UpdateIdleRefreshCameraMotion`, 1mm exact-stillness check) was removed in
  `19127df`. Diagnostic confirms `sdfBricks=512 (budget=512)`; `sdfBricks=0`
  now only appears when the idle-refresh pending set is fully drained, which
  is by design.
- **FIXED — budget diagnostic:** `(budget=512)` prints next to `sdfBricks`.
- **FIXED — anisotropic per-axis scale (old Bug A):** `GPUMeshSdf` gained
  `WorldToLocalAxisScale`; C# `ComputeLocalToWorldAxisScales`
  (`Njulf.Rendering/Resources/MeshSdfManager.cs`) correctly computes
  1/‖column‖ of `worldToLocal` (valid for TRS, no shear), and
  `SampleMeshSdf` (`Njulf.Shaders/global_sdf_update.comp:102-123`) does a
  gradient-weighted anisotropic reconstruction (`ScaleLocalDistanceToWorld`).
  Verified correct end-to-end, including the out-of-bounds AABB branch.
- **FIXED — encoding precision (old issue #8):** distances now stored
  voxel-relative with `EncodeSdfDistance`/`DecodeSdfDistance`
  (`common.glsl:227-241`, ±32-voxel clamp), consistent between
  `mesh_sdf_bake.comp` (encode), `global_sdf_update.comp` (encode/decode),
  and `global_sdf.glsl` (decode).
- **FIXED — hit epsilon dilation:** `hitEpsilon` is now `voxelSize * 0.15`
  (`global_sdf.glsl:150`). Correct for dilation, but it exposed Bug E below.
- **LANDED — mip acceleration:** cascades regenerate a full mip chain each
  frame (`GlobalSdfPasses.cs:166-170` → `VolumeTexture.GenerateMipChain`) and
  the trace does coarse-LOD empty-space skipping
  (`SelectGlobalSdfTraceLod` + skip branch, `global_sdf.glsl:127-167`).
  The concept is right; the implementation has Bug D below.

## Bug D — mip pyramid is wrong for how the trace uses it (primary remaining bug)

`VolumeTexture.GenerateMipChain` (`Njulf.Rendering/Resources/VolumeTexture.cs:132`)
builds mips with `CmdBlitImage` + `Filter.Linear` — an **average** downsample of
the **physical ring-buffered** texture. The trace's skip branch
(`global_sdf.glsl:160-167`) treats a coarse mip texel as a safe lower bound:
`if (coarse > mipVoxelSize*2) t += coarse - mipVoxelSize`. Two independent
failures:

1. **Average is not conservative.** A 2^L block containing one thin wall and
   mostly empty space averages to a large distance → the skip branch steps
   straight *through* the wall → intermittent misses on thin geometry. This
   is the hit/miss speckle on the small props in the slice screenshot, and it
   is a light-leak source for DDGI far-cascade rays.
2. **Ring seams poison mips ≥ 4.** Bricks are 8³, so mip blocks of 2/4/8
   voxels (LOD 1–3) stay inside one brick, but LOD ≥ 4 blocks span multiple
   physical bricks — and physically-adjacent bricks are logically far apart
   across the clipmap ring wrap. Those mip texels blend unrelated world
   regions. `SelectGlobalSdfTraceLod` currently allows LOD up to
   `MipCount-1` (LOD 7 at 256³), so far-field skip decisions read garbage.
   (The mip addressing in `FetchGlobalSdfCascadeEncodedDistance`,
   `global_sdf.glsl:50-61`, is likewise only brick-coherent for LOD ≤ 3.)

### Fix D

- Replace the blit in the global-SDF path with a small **min-filter compute
  downsample** (one trivial compute pass per mip level; follow the existing
  compute-pass pattern in `GlobalSdfPasses.cs`). Min over the 8 children is
  the conservative filter the skip logic assumes. Keep `GenerateMipChain`
  itself for any other users, or add a `GenerateMinMipChain` beside it.
- **Clamp `SelectGlobalSdfTraceLod` to LOD ≤ 3** (= log2(BrickSize)), so mips
  never cross brick boundaries and the physical-space downsample stays
  logically coherent. LOD 3 still gives 8× step acceleration — plenty.
- Keep the existing 1-mip-voxel back-off margin in the skip branch.
- Optional simplification if the compute pass is deemed too much work for
  now: keep linear-blit mips but clamp LOD ≤ 3 AND change the skip branch to
  only trust `coarse` when `coarse > mipVoxelSize * 4` (bigger safety
  margin). This reduces but does not eliminate thin-wall skips — the
  min-filter is the real fix; say so in the PR if the shortcut is taken.

## Bug E — grazing rays exhaust the step budget (the floor ring banding)

Sphere tracing slows geometrically at grazing incidence: for a ray at angle θ
above a plane, remaining distance shrinks by factor (1−sin θ) per step. With
`hitEpsilon` now 5× smaller (0.75 → 0.15 voxel) and `maxSteps` unchanged
(96 in the debug view, `forward.frag:2890`), grazing floor rays exhaust their
budget and return **miss**. In `GlobalSdfRaymarchDebugColor`
(`forward.frag:2867-2922`) a miss falls to the banded distance-slice path
(`cos(bestDistance * 24.0)`) — that IS the concentric pastel ring pattern on
the floor in the screenshot. For DDGI it means grazing probe rays over
floors/walls read as misses → sky/environment radiance leaks into
floor-adjacent probes, and burned step budgets inflate `gpuDdgiUs`
(diag shows 2.4–3.5 ms, `hybridPerfOk=False`).

### Fix E

In `TraceGlobalSdfCascadeSegment` (`global_sdf.glsl:137-187`):

- Make the hit threshold **distance-proportional (cone-style)**:
  `epsilon(t) = max(voxelSize * 0.15, t * epsilonSlope)`, with
  `epsilonSlope` passed by the caller: for DDGI probe rays a slope derived
  from ray solid angle (192 rays/probe → half-angle ≈ 0.16 rad → slope ≈
  0.05–0.16 works; make it a constant in `ddgi_update_shared.glsl` first,
  tune later); for the debug view a small slope (~0.002) is enough.
  This restores far/grazing hit registration without reintroducing uniform
  near-field dilation — dilation now scales with distance, which is the
  correct LOD behavior.
- Also use `epsilon(t)` as the **minimum step size** (replace the bare
  `hitEpsilon` floor in the `t +=` lines) so grazing marches accelerate as
  t grows instead of crawling at 0.15 voxel.
- Raise the debug view's `maxSteps` 96 → 128 (`forward.frag:2890`) as
  belt-and-braces; it should rarely be reached once epsilon scales.

## Order & files

1. Fix E first — it's ~10 lines in `Njulf.Shaders/global_sdf.glsl` (+ a
   constant in `ddgi_update_shared.glsl`, + one literal in
   `forward.frag`), no C# changes, and removes the most obvious artifact
   (floor rings) plus the DDGI grazing-miss bias.
2. Fix D — new min-downsample compute shader (e.g.
   `global_sdf_min_mip.comp`), pipeline creation + dispatch in
   `Njulf.Rendering/Pipeline/GlobalSdfPasses.cs` replacing the
   `GenerateMipChain` call at `:166-170`, LOD clamp in
   `SelectGlobalSdfTraceLod` (`global_sdf.glsl:127-135`) and in
   `FetchGlobalSdfCascadeEncodedDistance`'s callers.
3. Tests: `ShaderBuildTests.cs` string assertions for the new shader file
   and the LOD clamp; note again these don't compile shaders. No C# math
   changed, so `GlobalSdfManagerTests`/`GPUStructLayoutTests` stay as-is.

## Verification

1. Rebuild + run (user's machine/CI — no dotnet in this sandbox), capture the
   `GlobalSdfSlice` view standing still and while strafing:
   - floor should render as solid hit color to the horizon of each cascade,
     no concentric rings;
   - thin props should be solid silhouettes, no speckle;
   - `sdfSteps` avg in the diag should DROP (less step exhaustion) while
     hit rate (`ddgiTrace hit/miss`) rises.
2. Check `gpuDdgiUs` / `hybridPerfOk` — fixing E should recover a chunk of
   the trace cost.
3. Keep the normal-render vs slice comparison for the anisotropic fix (already
   landed) — column/frame bloat should stay gone.



   