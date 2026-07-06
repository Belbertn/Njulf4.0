# SDF content is broken at the mesh-SDF bake layer — diagnosis + fix plan

## Context

Debug plumbing, cascade-isolation views, and counters are all in (`34fc623`) and working. They ruled out the clipmap machinery: `skippedInstances=0`, no candidate overflow, scroll/regen addressing verified consistent end-to-end in earlier audit. The isolation views show the decisive pattern: **box edges/corners read ≈0 (green rims), large flat faces read garbage (floors saturated positive/blue, ceiling undersides deep negative/red, ghost door frames offset from real geometry), and small props read fine.** Error magnitude scales with mesh size and is camera-independent. That is the signature of bad **per-mesh SDF source data**, faithfully unioned into the global cascades.

## Root causes found (in the code, high confidence)

### 1. Every authored mesh bakes as *unsigned fallback* — the weld test uses vertex indices
`MeshSdfBakePlanner.CreateBakeFlags` (`MeshSdfBakePlanner.cs:85-121`) counts edge use keyed by **vertex index**. `GetValidationBoxMesh` and `GetGroundPlaneMesh` (SampleStressSceneBuilder) use 4 unique vertices per face (24 per box, required for per-face normals), so no edge is ever shared by index → `count != 2` → `MESH_SDF_FLAG_UNSIGNED_FALLBACK` for every box, wall, floor, ceiling, and the ground slab. Consequences: no signed interiors, and surfaces reconstruct at ±half a bake voxel instead of zero (unsigned |d| has a V-shape at the surface that trilinear can't bring to 0). The existing `meshSdfUnsigned=` counter (already printed in another diag line, `SampleDiagnosticsReporter.cs:450`) should confirm: predicted ≈ all meshes.

**Fix:** key the edge map by **quantized vertex position** (weld tolerance ~1e-4 of max extent) instead of raw index. The 24-vert box then registers closed → signed bake with correct interiors. The pseudonormal sign path (`mesh_sdf_bake.comp:241`) is fine for welded convex faces.

### 2. Sheet compensation is applied twice
The bake already writes `unsignedDistance - halfVoxel` for fallback meshes (`mesh_sdf_bake.comp:246-248`), and `SampleMeshSdf` (`global_sdf_update.comp:120-128`) subtracts `sheetHalfThickness` **again** in its out-of-bounds branch. Keep exactly one (recommend: keep the bake-side subtraction, delete the sample-side one).

### 3. Global SDF bricks are never invalidated when a mesh SDF finishes baking
Bakes complete at `MeshSdfBakeBudget=2`/frame while the global SDF's initial full build (~55k bricks at 512/frame) runs concurrently. Bricks built before a mesh's record became active are missing that mesh **forever** — nothing re-marks them (`MarkDirtyWorldBounds` is only driven by DDGI layout events; there is no bake→invalidate link in `MeshSdfManager`/`MeshSdfBakePass`/`GlobalSdfPasses`). This produces permanent regional holes exactly like the "far positive on faces whose own mesh is absent" symptom.

**Fix:** when a bake job completes (in `MeshSdfManager`, where the baked record becomes available — surfaces as `LastFrameBakedMeshCount` at `MeshSdfManager.cs:126`), collect the world bounds of every *instance* using that mesh and call `GlobalSdfManager.MarkDirtyWorldBounds(bounds)` for each (plumb through `GlobalSdfPasses.ExecuteInternal`, which already holds both managers).

### 4. Bake voxel is proportional to mesh extent — big meshes get meter-scale voxels
`targetVoxelSize = maxExtent / 64`, resolution clamped to ≤128 (`MeshSdfBakePlanner.cs:25,32,127`). The 30 m ground slab bakes at ~0.47 m voxels while its slab is 0.22 m thick — sub-voxel geometry smears; the finest global cascade (0.125 m) faithfully unions that blur. **Fix:** cap the target voxel absolutely, e.g. `targetVoxelSize = max(min(maxExtent/64, 0.25f), 0.0001f)` so a 30 m mesh lands at res 128 ≈ 0.24 m/voxel, and thin authored features stay ≥ ~1 voxel. (Memory worst case 128³ R16 = 4 MB/mesh — fine at current mesh counts.)

## Implementation order

1. Fix 1 (weld by position) + unit test: feed a 24-vert per-face-normal cube into `CreateBakeFlags`, assert flags == 0; feed an actual open plane, assert fallback.
2. Fix 3 (bake-completion invalidation) + make sure `emptyPrevCandidateBricks`/`bricksWrittenEmpty` counters stay wired to observe it working.
3. Fix 2 (single compensation) — one-line shader change, extend `ShaderBuildTests` string assertions.
4. Fix 4 (voxel cap) — planner constant + test asserting the 30 m-mesh descriptor voxel ≤ 0.25.
5. Bump/trigger rebake: baked records must be regenerated for fixes 1/4 to take effect — verify the bake path re-runs when descriptors change (or bump a bake version so `_recordsByMesh` invalidates).

## Files to touch

- `Njulf/Njulf.Rendering/Resources/MeshSdfBakePlanner.cs` — position-welded edge test, voxel cap.
- `Njulf/Njulf.Shaders/global_sdf_update.comp` — remove double sheet compensation.
- `Njulf/Njulf.Rendering/Resources/MeshSdfManager.cs` + `Pipeline/GlobalSdfPasses.cs` — bake-completion → `MarkDirtyWorldBounds` plumbing.
- `Njulf/Njulf.Tests/` — planner weld/voxel tests, ShaderBuildTests updates.


## Verification

- `dotnet test` (new planner tests + shader build tests).
- User-side: `meshSdfUnsigned=` should drop to ~0; cascade-isolation views should show green at *all* surfaces (floors and ceiling undersides included) at spawn and after moving; ghost frames gone. The `bricksWrittenEmpty` counter should show activity during startup and after moves, then settle.
- DDGI consumers: `DdgiRayBackendHeatmap` and `GlobalSdfSlice` (backend view) should now show a usable field for cascades ≥ 2.

## Not the problem (verified, don't refactor)

Clipmap scroll/ring addressing (CPU↔compute↔sampler consistent, Repeat sampler correct), push-constant plumbing, instance world-to-local packing (row-vector convention verified component-wise), candidate pruning min-union logic.