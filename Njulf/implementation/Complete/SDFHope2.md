# Fix global SDF distortion: unsafe distance conversion under non-uniform instance scale

## Context

After the dirty-region padding fix landed (commit 724b583, from `SDFHope.md`), the SDF still doesn't conform to the meshes. The new snapshot pair (normal render vs `GlobalSdfFullSlice` at the same camera pose) shows *structural* errors, not staleness: melted phantom blobs where the raster has empty floor, walls the cascade-0 trace tunnels through (rendered instead by coarser cascades' dilated hits), "carved"-looking pillars, and a deep-negative floor. All bake/validity diagnostics read clean, and nothing in this scene moves — so the bake *content* itself is wrong.

### Scene reality that changes the analysis

The running scene is the `GiSdfCascadeField` validation scenario (`SampleStressSceneBuilder.cs:960-1042`). Every object — rooms, walls, pillars, occluders, boundaries, foundation — is **one shared unit-cube mesh** (`GetValidationBoxMesh()`, the single baked 65³ mesh SDF; `MeshSdfTotalBakedMeshCount: 1`, `GlobalSdfMeshSdfCount: 40`) instanced with **extreme non-uniform scales**: foundation `34×0.16×92`, boundaries `0.32×4×86`, walls `~9×4×0.3`, pillars `0.65×2.9×0.65`.

### Root cause

`SampleMeshSdf` (`Njulf/Njulf.Shaders/global_sdf_update.comp:118-136`) converts an in-bounds local SDF sample to world meters via a gradient-directional scale (`EstimateMeshSdfLocalGradient` + `ScaleLocalDistanceToWorld`). Under non-uniform scale this is **unboundedly wrong**, because the nearest surface in local space is not the nearest surface in world space. Concrete failure (foundation, scale 34×0.16×92): an interior point near the slab's X-end is 0.05 local units from the X face and 0.3 from the Y face → the local SDF reports the X face, gradient +X, directional scale 34 → stored ≈ **−1.7m**, while the true nearest surface (Y face) is **0.048m** away — a 35× overshoot; along Z the factor reaches ~575×. Both signs are affected (interior → giant phantom "inside" volumes = the melted blobs and deep-red floor; near-surface positive overshoot → sphere-trace tunneling = eroded/dusty walls and pillars).

Movement never caused the corruption — re-baking (scroll, dirty regions) just reshuffles which bricks expose it, which is why it *looked* movement-correlated.

Note: session 1's plan "ruled out" this path, but only checked it under uniform scale + rotation (where it is exact). With this scene's scales it is the primary defect.

### Ruled out / already fixed

- Dirty-region padding gap: real, fixed in 724b583, keep it.
- Brick logical/physical indexing, validity signatures, world→local transform convention, scroll accounting: verified consistent; all diagnostics corroborate.
- The white walls in the FullSlice view are not "different material": the debug view (`forward.frag:2907 GlobalSdfRaymarchDebugColor`) returns the **first cascade whose trace hits**, tinted per cascade — white surfaces are coarser-cascade hits showing through holes in cascade 0's eroded walls.

## Changes

### 1. Conservative distance conversion — `Njulf/Njulf.Shaders/global_sdf_update.comp`

In `SampleMeshSdf` (lines 118-136), replace the gradient-directional conversion for in-bounds samples with **minimum-axis-scale** conversion, both signs:

```glsl
float minAxisScale = min(localToWorldScale.x, min(localToWorldScale.y, localToWorldScale.z));
return DecodeSdfDistance(normalizedDistance, meshSdf.LocalBoundsMinAndVoxelSize.w) * minAxisScale;
```

This is provably conservative: for any surface point r, |world(p−r)| ≥ minScale·|p−r| ≥ minScale·d_local, so the stored magnitude never exceeds the true distance (for both positive and negative values) — sphere tracing can never tunnel, and phantom interiors are bounded by the instance's real thickness. The out-of-bounds path (`length(outside * localToWorldScale)`, line 128-129) is already exact per-axis — keep it.

Remove the now-unused `EstimateMeshSdfLocalGradient` / `ScaleLocalDistanceToWorld` (or leave them unreferenced if other shaders share them — check `global_sdf.glsl` includes first).

Cost note: min-scale makes tracing crawl only *inside an instance's local bounds* (a thin shell for these boxes; identical to today for uniform-scale content like real models, since min = directional there).

### 2. Regression guard — `Njulf/Njulf.Tests/ShaderBuildTests.cs`

Shaders must still compile; extend the existing shader-source contract pattern (this file already asserts shader constants) to pin the min-axis-scale conversion (e.g. source contains `minAxisScale` in `SampleMeshSdf`) so it isn't accidentally reverted to the gradient path.

### 3. Nothing else changes

The padding fix (724b583) stays. No C# changes required — `LocalToWorldAxisScale.xyz` is already uploaded per instance.

## Verification

1. `dotnet build` + `dotnet test` (ShaderBuildTests compiles all shaders).
2. User-side, same scene (`DdgiSdfCacheTest` / GiSdfCascadeField), same camera pose as the snapshot pair:
   - `GlobalSdfFullSlice`: walls/pillars should render as crisp boxes from cascade 0 (one consistent tint up close), the near-camera phantom blob gone, floor "inside" band only ~0.16m deep instead of meters.
   - Use the existing per-cascade views (`GlobalSdfCascade0..3`, debug views 124-127) to confirm cascade 0/1 conform tightly. Cascades 2/3 will *still* look inflated around 0.16-0.3m geometry — that is inherent voxel-size dilation (sub-voxel features at 0.5-1m voxels), not this bug.
   - `GlobalSdfAverageTraceSteps` should drop (fewer near-band crawl zones from bogus values); `DdgiSurfaceCacheFallbackPercent` (10.2%) should improve.

## Known follow-up (out of scope, flag to user)

With `SdfBackendFirstCascade = 2` (0.5m voxels), DDGI consumes cascades where all geometry in this scene is sub-voxel thin and necessarily dilated — worth a separate evaluation (lower the backend first cascade near-field, or accept the conservative dilation).