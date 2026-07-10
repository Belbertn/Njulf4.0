# Fix global SDF staleness after object rotation/movement (dirty-region padding gap)

## Context

The global SDF still shows eroded/speckled surfaces and "dust" after the model rotates and the camera moves, even after the latest push (commit `d9be6cd` — brick-validity signatures). The new snapshot is the key: **all validity/scroll diagnostics read clean** (`GlobalSdfDirtyBrickSampleCount: 0`, `ScrollChangedBrickValidationFailureCount: 0`, dirty backlog 0) while the slice view still shows the artifacts. So the bad data lives in bricks the system considers valid and fully baked — their *content* is stale, and nothing knows to re-bake them.

### Root cause

There is a padding mismatch between what the GPU bake reads and what the CPU marks dirty on movement:

- **GPU bake influence region** (`Njulf/Njulf.Shaders/global_sdf_update.comp:273-284`): a mesh contributes to a brick if `meshRecordAabb ± 1 voxel` intersects `brickAabb ± 4 voxels`. The record AABB itself is additionally inflated by one *mesh-SDF* voxel (`MeshSdfManager.cs:397-400`). So bricks up to **~5 cascade voxels (+ 1 mesh-SDF voxel) outside** an object's AABB store distance values derived from that object (each voxel writes `min(composedDistance, safeBound)` where `safeBound` reaches up to distance-to-brick-face + 4 voxels).
- **CPU dirty marking on transform change** (`VulkanRenderer.cs:6585` emits `Union(oldBounds, newBounds)` → `GlobalSdfManager.cs:1060 MarkDirtyWorldBounds` → `GlobalSdfManager.cs:1581 MarkWorldBoundsDirty`): the bounds are converted to brick cells with **zero padding**.

Result: when an object rotates/moves (the sample model rotates via `Program.cs:248-251 ApplyModelRotation`, firing `DdgiDirtyReason.TransformChanged` per frame), the shell of bricks just outside the old/new AABBs is never re-baked:

- around the **old** pose, stale too-*small* distances remain → dust/speckle clouds in the slice;
- around the **new** pose, stale too-*large* distances remain (baked before the object arrived, capped only by `safeBound`) → sphere-tracing rays overstep through the near-surface band → **surface erosion speckles**, exactly what the screenshot shows on the rotated pipe/bracket instances.

This also explains why the d9be6cd validity-signature system can't catch it: those bricks were legitimately baked at their current logical positions — the signature only detects *unbaked/scrolled* bricks, not stale content.

### Ruled out during investigation

- Logical vs physical brick indexing (CPU `_dirtyBricks` and shader `brickLinear` are both physical, mappings are consistent inverses).
- World→local transform convention in `TransformWorldToMeshSdfLocal` (correct for the engine's row-vector `S·R·T` matrices), and `ComputeLocalToWorldAxisScales` (column norms of `worldToLocal` are correct for `S·R·T`).
- Outside-box mesh-SDF distance and the conservative voxel composition — both conservative within a single bake.
- New `...ByMovement` diagnostics: the 7 buckets are `DdgiCameraMovementClass` (bucket 3 = `Normal`); all rejects landing there just says rejections happen during ordinary camera motion — not a separate lead.

## Changes

### 1. Pad dirty bounds to the candidate-gather influence radius — `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs`

In the cascade runtime (`MarkWorldBoundsDirty`, line ~1581), inflate the incoming bounds by a cascade-specific padding before converting to cells:

```
paddingMeters = (CandidateGatherPaddingVoxels + MeshCullInflationVoxels) * VoxelSize  // 4 + 1
              + maxMeshSdfWorldVoxelSize;                                             // record inflation
```

- Add named constants `CandidateGatherPaddingVoxels = 4` and `MeshCullInflationVoxels = 1` with a comment stating they must mirror `global_sdf_update.comp` (the `4.0` at line 273 and the `±1 voxel` mesh cull at lines 283-284).
- Apply the padding **before** the `cascade.Intersects(bounds)` gate in `MarkDirtyWorldBounds` (line ~1060-1068) too — otherwise a bounds hugging a cascade edge can skip a cascade whose edge bricks are influenced. Simplest: pass raw bounds down and have each cascade inflate first, testing `Intersects` on the inflated bounds inside the loop.

This single choke point covers all callers: DDGI `TransformChanged`/`GeometryAdded`/`GeometryRemoved` regions and `MeshSdfManager.MarkNewlyBakedInstanceBoundsDirty` (line 167).

### 2. Track the mesh-record inflation term — `Njulf/Njulf.Rendering/Resources/MeshSdfManager.cs`

The record AABB the shader culls against is render bounds + `meshSdfWorldVoxelSize` (`bakedRecord.LocalBoundsMinAndVoxelSize.W * maxAxisScale`, lines 397-400), but DDGI dirty regions use raw render bounds. Track the max of `meshSdfWorldVoxelSize` across instance records (update where records are created in `TryCreateInstanceGpuRecord` callers / `PrepareInstanceRecords`), expose it (e.g. `MaxInstanceWorldVoxelSize` property), and feed it to `GlobalSdfManager` each frame (or on change) so the padding in change 1 stays strictly conservative.

### 3. Regression tests — `Njulf/Njulf.Tests/GlobalSdfManagerTests.cs`

Follow the existing test patterns in this file (it already constructs cascades and asserts dirty-brick state):

- Bounds that end just outside a brick but within `5 * voxelSize` → that neighboring brick must become dirty.
- Bounds farther away than the padding → brick stays clean.
- A bounds near the cascade boundary → cascade is still marked (Intersects uses padded bounds).

## Verification

1. `dotnet test` for `Njulf.Tests` (at minimum `GlobalSdfManagerTests`); `dotnet build` the solution. (GPU repro isn't possible in this environment.)
2. User-side repro check: run the sample, let the model rotate, fly the camera, stop, capture a diagnostic snapshot with the `GlobalSdfFullSlice` debug view. Expected:
   - speckles/dust around the rotated instances clear once the dirty backlog drains;
   - `GlobalSdfAverageTraceSteps` (36.5 in the snapshot) drops;
   - `DdgiSurfaceCacheRejectDepthUv/NoCandidatePassed` counts and `DdgiSurfaceCacheFallbackPercent` (6.2%) decrease, since SDF hit positions feed the surface-cache projection checks;
   - `GlobalSdfDirtyBrickSampleCount` stays 0.

Cost note: the padding adds at most a one-brick shell to each moved region's re-bake (5 voxels < 8-voxel brick), well within the 512-brick/frame budget.