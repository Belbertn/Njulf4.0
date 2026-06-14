# Renderer Architecture 04 - Offline Asset Pipeline, LOD, Foliage, And Impostors Plan - 2026-06-14

Target outcome: assets arrive in renderer-ready form. Meshes include authored LODs, generated fallback LODs, meshlets per LOD, bounds, material flags, foliage cluster data, and impostor data where applicable. Runtime rendering selects and draws prepared data instead of restructuring assets every frame.

Performance and quality are equal priorities. The asset pipeline must preserve author intent, provide deterministic fallback generation, and remove runtime work that belongs offline.

## Non-Negotiable Requirements

1. Authored LODs are imported and preserved.
2. Missing LODs get deterministic generated fallback LODs with quality metrics.
3. Meshlets are built per LOD offline or at import time, not during normal gameplay.
4. Foliage data supports batching, wind, alpha testing, LOD, and impostors.
5. Impostors are generated or imported with validated atlases and metadata.
6. Runtime has no placeholder impostors or fake LOD metadata.
7. Old runtime-only restructuring paths are removed once import data is complete.

## Phase 0 - Asset Format Contract

1. Define a renderer asset schema for meshes:
   - mesh asset ID.
   - submesh list.
   - LOD list.
   - meshlet ranges per LOD.
   - vertex/index buffers per LOD or shared packed buffers.
   - bounds per mesh, submesh, LOD, and meshlet.
   - material slots.
   - flags for alpha test, transparency, skinning, foliage, impostor support.
2. Define metadata versioning:
   - asset pipeline version.
   - meshlet builder version.
   - simplifier version.
   - material classification version.
3. Store source content hashes so stale processed assets are detected.
4. Define strict import errors for malformed LODs, missing material slots, invalid bounds, or non-finite data.

Acceptance criteria:
- Runtime can reject stale or invalid processed assets with a clear message.

## Phase 1 - Authored LOD Import

1. Support common authored LOD naming:
   - node suffixes such as `_LOD0`, `_LOD1`, `_LOD2`.
   - glTF extras when present.
   - explicit project metadata file.
2. Validate authored LODs:
   - same material slot compatibility or explicit remap.
   - decreasing or justified triangle counts.
   - valid bounds.
   - consistent skinning data for skinned meshes.
3. Preserve author-provided LOD switch distances when available.
4. Add import diagnostics:
   - LOD count.
   - triangle count per LOD.
   - vertex count per LOD.
   - material compatibility.
   - bounds changes.
5. Add tests using synthetic LOD assets.

Acceptance criteria:
- Authored LOD assets import without losing material or skinning semantics.
- Invalid LOD chains fail at import, not at runtime.

## Phase 2 - Generated Fallback LODs

1. Add deterministic mesh simplification for meshes without authored LODs.
2. Preserve:
   - boundary edges.
   - UV seams where needed.
   - hard normals.
   - material boundaries.
   - skin weights for skinned meshes.
3. Generate target LODs by ratio and screen-size policy.
4. Compute quality metrics:
   - triangle reduction.
   - vertex reduction.
   - geometric error.
   - UV error.
   - normal deviation.
   - material seam preservation.
5. Allow project settings to disable generated LODs per asset.
6. Store generated LOD provenance in processed asset metadata.

Acceptance criteria:
- Generated LODs are deterministic from the same source asset and settings.
- Quality metrics are visible and budget-testable.

## Phase 3 - Meshlet Build Per LOD

1. Build meshlets independently for every LOD.
2. Store per-meshlet:
   - bounding sphere.
   - bounding cone once available.
   - vertex/index ranges.
   - local triangle count.
   - local vertex count.
   - material/submesh reference.
3. Optimize meshlet locality:
   - vertex cache.
   - overdraw where practical.
   - spatial locality for culling.
4. Validate max vertices and triangles per meshlet.
5. Store meshlet quality stats per LOD.
6. Remove runtime LOD meshlet generation once processed assets carry required data.

Acceptance criteria:
- Runtime loads meshlet ranges directly from processed assets.
- Meshlet builder only runs in import tools or explicit development rebuild mode.

## Phase 4 - Runtime Asset Loading

1. Update runtime asset loading to consume processed mesh assets.
2. Upload all LOD mesh data into mesh buffers with stable ranges.
3. Expose `MeshInfo` with authored/generated LOD ranges.
4. Include fallback validation when older assets are loaded:
   - fail with migration instructions in development.
   - do not silently generate placeholder LODs in shipping path.
5. Add asset cache invalidation when schema or builder version changes.

Acceptance criteria:
- Runtime rendering can select any LOD without additional asset processing.

## Phase 5 - Foliage Asset Contract

1. Define foliage asset metadata:
   - source mesh.
   - material slots.
   - alpha-test settings.
   - wind parameters.
   - bend pivot.
   - density categories.
   - placement constraints.
   - LOD/impostor policy.
2. Define foliage cluster data:
   - cluster bounds.
   - instance count.
   - instance transforms.
   - per-instance variation.
   - wind phase/randomization.
3. Add import validation for foliage materials:
   - alpha test threshold.
   - two-sided settings.
   - normal map availability.
   - mip bias policy.
4. Add content budget metrics:
   - instances per cluster.
   - triangles per cluster per LOD.
   - overdraw risk.

Acceptance criteria:
- Foliage assets carry enough metadata for batching, culling, LOD, wind, and impostors.

## Phase 6 - Foliage Batching Runtime

1. Add `FoliageBatchManager`.
2. Store foliage instances in GPU scene buffers.
3. Batch by:
   - mesh asset.
   - material.
   - wind profile.
   - LOD/impostor policy.
   - cell/cluster.
4. Cull at cluster level first, then instance or meshlet level when needed.
5. Support GPU-driven LOD and impostor selection.
6. Keep per-instance variation in compact GPU data.
7. Avoid per-instance CPU draw calls.
8. Remove old individual-object foliage rendering after batching is validated.

Acceptance criteria:
- Large foliage fields submit as clustered GPU-driven work, not thousands of CPU objects.

## Phase 7 - Impostor Generation

1. Add impostor generator tool:
   - renders source mesh from configured directions.
   - outputs albedo, normal, depth, roughness/metalness as needed.
   - supports alpha coverage preservation.
   - writes atlas metadata.
2. Support impostor types:
   - billboard cross for simple vegetation.
   - octahedral impostor for complex objects.
   - card cloud only if content requires it.
3. Store:
   - atlas texture references.
   - view direction layout.
   - object bounds.
   - pivot.
   - depth reconstruction parameters.
   - normal encoding parameters.
4. Validate atlas resolution and alpha coverage.
5. Add visual comparison mode between mesh LOD and impostor.

Acceptance criteria:
- Impostors are real generated assets with material data, not placeholder quads.

## Phase 8 - Runtime Impostor Rendering

1. Add impostor material/shader path.
2. Integrate impostor selection into GPU LOD pass.
3. Render impostors through forward/depth/shadow paths as appropriate.
4. Handle depth and normal correctness enough for lighting and fog.
5. Add fade or dither transitions to avoid popping.
6. Add shadow policy:
   - use lower mesh LOD for near shadows.
   - use impostor shadow for far foliage only after validation.
7. Add diagnostics for impostor counts and saved meshlets.

Acceptance criteria:
- Far foliage/objects can switch to impostors without obvious popping or lighting breaks.

## Phase 9 - Content Budgets And Gates

1. Add import-time budgets:
   - max triangles per LOD.
   - max meshlets per asset.
   - max foliage instances per cluster.
   - max impostor atlas size.
   - max material slots.
2. Add warnings and failures:
   - warning for soft budget exceed.
   - error for hard runtime-incompatible content.
3. Store budget status in processed asset metadata.
4. Expose budget data in performance snapshots.

Acceptance criteria:
- Expensive content is caught before runtime performance debugging.

## Phase 10 - Remove Old Architecture

Delete after migration:

1. Runtime-only generated LOD paths used in production.
2. Meshlet generation during normal runtime load when processed data exists.
3. Per-object foliage rendering paths.
4. Placeholder LOD/impostor metadata.
5. Duplicate mesh metadata structures that conflict with processed asset metadata.
6. Silent fallback behavior that hides invalid content.

Acceptance criteria:
- Runtime consumes validated processed assets and does not silently invent missing production data.

## Validation

1. Import tests:
   - authored LODs.
   - generated LODs.
   - skinned LODs.
   - invalid material remap.
   - invalid bounds.
   - foliage clusters.
   - impostor atlas metadata.
2. Runtime tests:
   - LOD selection.
   - foliage cluster culling.
   - impostor transition.
   - asset cache invalidation.
3. Visual validation:
   - side-by-side LOD comparisons.
   - impostor lighting/fog.
   - foliage wind and shadows.
4. Performance validation:
   - reduced meshlet count at distance.
   - fewer CPU objects for foliage.
   - lower shadow and forward cost in large scenes.

## Definition Of Done

1. Authored and generated LODs are fully imported.
2. Meshlets are available per LOD from processed assets.
3. Foliage batching and impostors are production rendering paths.
4. Invalid content fails early with actionable diagnostics.
5. Old runtime restructuring and placeholder paths are removed.
