# SDF Thin-Feature Preservation Fix

## Diagnosis

The residual turn artifact is representational, not stale state. The problematic scene is mostly one baked mesh instanced with heavy non-uniform scale. Several wall and frame instances are only about 0.1-0.3 m thick in world space, while the global SDF coarse cascades store 0.5 m and 1.0 m voxels.

At those resolutions, voxel centers can land on both sides of a thin wall and still sample positive distances. `IntersectTrilinearCell` can only find an SDF hit when the cell contains a zero crossing, so a coarse cascade may contain "near zero" values in the debug view while still having no negative core for the tracer to cross. This explains the teal miss band after the 180 degree turn: the camera is facing geometry that is outside fine cascade coverage, and the coarse cascades do not represent the thin walls as hittable surfaces.

The doorway and right-wall gap are the seed because they are built from the most downscaled instances. Cascade 0 can still show them, but they are already melted by resolution and trilinear filtering. Cascade 1 is marginal. Cascades 2 and 3 can lose them completely. DDGI is affected too because `SdfBackendFirstCascade` is 2, so GI rays primarily use the cascades where these walls used to disappear.

Checked and cleared during diagnosis:

- Clipmap scrolling and invalidation counters are sane.
- Ring addressing is consistent between writer and sampler.
- Empty-brick `safeBound` writes are conservative.
- Instance scale math is correct. The GPU field was misnamed and has been renamed from `WorldToLocalAxisScale` to `LocalToWorldAxisScale`.

## Implemented Fix

### Global SDF Composition

`Njulf.Shaders/global_sdf_update.comp` now preserves sub-voxel thin features while composing each global SDF voxel.

For a candidate mesh SDF sample:

- If the center sample is already negative, it is kept.
- If the center sample is positive but outside the near-surface preservation band, it is kept.
- If the center sample is positive and within `0.75 * globalVoxelSize`, the writer probes the voxel-sized neighborhood around the center.
- If any neighborhood probe finds negative mesh SDF, the stored center distance is clamped to a conservative negative core of `-0.35 * globalVoxelSize`.

This intentionally thickens detected sub-voxel surfaces to about one global voxel in the cascades where they would otherwise have no sign change. It is scoped to mesh candidates that are already near the surface, so empty space and normal far-field distances are left alone.

### Trace-Side Backstop

`Njulf.Shaders/global_sdf.glsl` now has a near-band fallback after `IntersectTrilinearCell` fails. If the DDA segment through a cell has no cubic zero root but samples a positive local minimum below `0.45 * voxelSize`, it synthesizes a hit at that minimum.

This catches residual positive-to-positive tunneling at grazing angles without replacing the normal trilinear root path.

### Diagnostics And Debugging

- `GlobalSdfSingleCascadeDebugColor` now splits near-zero sign: positive-near samples render cyan, negative-near samples render amber. This makes "near zero but no negative core" directly visible in the single-cascade views.
- The GPU global-SDF rolling max window is now 600 frames instead of 120, so late snapshots are less likely to miss the last real brick update.
- `GpuGlobalSdfMipMicroseconds` was removed from live diagnostics because there is no mip pass and global SDF tracing samples lod 0.
- `GPUMeshSdf.WorldToLocalAxisScale` was renamed to `LocalToWorldAxisScale` in C# and GLSL.

## Verification Plan

1. Build the solution.
2. Run `GlobalSdfManagerTests` and `ShaderBuildTests`.
3. Reproduce the 180 degree turn capture:
   - Expected: the teal miss band is gone or substantially reduced because coarse cascades now contain a negative core for thin walls.
   - Expected: the doorway/right-wall gap may still look melted in cascade 0. That is a separate resolution issue.
   - Expected: DDGI leak-through at these thin walls should reduce because cascade 2+ now preserves the surfaces.

## Future Work

The remaining cosmetic issue is per-instance bake resolution. Heavily downscaled instances can still look melted even when they are hittable in the global SDF. A future pass should raise the effective mesh SDF bake resolution or minimum represented thickness for instances that are crushed along one axis.
