# SDF Round 4: Correct Thin-Feature Preservation

## Diagnosis

The thin-feature preservation from SDFFix99 fixed coarse-cascade dropout, but the first implementation was too broad. It flipped a global SDF voxel to a negative core whenever the center was near a surface and any half-voxel neighborhood sample was inside geometry. That is true for ordinary thick surfaces too, so the first voxel shell around floors and walls could become negative.

Because the trigger depends on voxel-grid phase, the artifacts appeared to breathe as the clipmap scrolled: smooth surfaces, membranes over openings, and coarse-cascade rubble could appear or disappear at the same world locations as the camera moved. The trace-side near-minimum fallback amplified the same problem by synthesizing positive-to-positive hits with no actual zero crossing.

Observed symptoms:

- Voxel-scale acne on flat floors and walls.
- Coarse-cascade membranes sealing openings such as the shed doorway.
- Floating dark blobs and pink rubble in cascade 1/2/3 territory.
- Elevated DDGI surface-cache fallback rates from phantom SDF hits.
- Higher global-SDF brick update cost from excessive neighborhood sampling.

## Implemented Fix

`Njulf.Shaders/global_sdf_update.comp` now only preserves a thin feature when the negative core would otherwise be lost at the current cascade resolution:

- The center gate is tightened to `0 < centerDistance < 0.5 * voxelSize`.
- The writer first samples the six face neighbors at a full voxel offset.
- If any full-voxel neighbor is negative, the feature is thick enough to already have a voxel-center negative core, so the original center distance is kept.
- Only when all six full-voxel neighbors are positive does the writer sample the six half-voxel face neighbors.
- If a half-voxel neighbor is negative, the writer stores `min(centerDistance, -0.35 * voxelSize)`.

`Njulf.Shaders/global_sdf.glsl` no longer has `TryIntersectTrilinearNearMinimum` or its call site. Near-band tracing returns hits from the trilinear zero-crossing solver, plus the existing inside-start fallback.

Kept from the previous work:

- `LocalToWorldAxisScale` naming.
- Sign-split single-cascade SDF debug colors.
- 600-frame global-SDF rolling timing diagnostics.
- Removal of the dead mip timing diagnostic.

## Verification

Required local checks:

- `dotnet build Njulf.sln`
- `dotnet test Njulf.Tests\Njulf.Tests.csproj --filter "FullyQualifiedName~GlobalSdfManagerTests|FullyQualifiedName~ShaderBuildTests"`

Runtime capture expectations:

- Flat floors and walls are smooth again in FullSlice and single-cascade views.
- The shed doorway stays open with no cascade-3 green membrane.
- The original coarse-cascade thin-wall dropout remains fixed.
- DDGI surface-cache fallback percent should return near the previous sub-1% range.
- `GpuGlobalSdfBrickMicrosecondsRollingMax` should return near the pre-overtrigger slab cost.

If the yellow/black checkerboard patch remains after this fix, treat it as a separate undefined-color or NaN diagnostic issue.
