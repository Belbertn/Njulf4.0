# Production Foliage Readiness With Ray Tracing

Goal: upgrade the current GPU-driven foliage path into a production-ready foliage system that works for large authored worlds and can participate in upcoming ray tracing features without forcing a rewrite later.

Current baseline:

- GPU-driven foliage, authored meshlet foliage, shadows, diagnostics, far card impostors, sample stress scenes, and performance snapshots exist.
- `SampleInputController` already reserves practical foliage controls:
  - `Ctrl+F8`: foliage indirect meshlet dispatch
  - `Ctrl+F9`: far impostors
  - `Ctrl+F10`: foliage debug view
- `FoliagePatch.DensityTexture` is still an untyped placeholder and GPU patch data currently uses an invalid density texture index.
- There is no production terrain placement, density-map import path, streaming cell system, foliage interaction system, variant/biome system, baked impostor atlas pipeline, or ray tracing acceleration-structure integration yet.

## Step 1: Lock The Baseline And Control Surface

1. Keep the existing foliage controls stable in `SampleInputController`.
2. Add a short keybinding registry comment or sample controls document before adding more foliage/ray tracing hotkeys.
3. Capture baseline snapshots for:
   - `DenseGrassField`
   - `ShrubFoliage`
   - `MixedTreeLineFoliage`
   - `MixedTreeLineFoliageNoShadows`
   - `ForestFoliage`
4. Store CPU build, CPU upload, GPU cull, depth, forward, shadow, memory, visible clusters, visible meshlet draws, and impostor counts.
5. Add smoke runs that can execute each foliage feature with its flag disabled and enabled.

Acceptance:

- Current foliage scenarios remain visually and behaviorally unchanged with all new production flags disabled.
- Baseline numbers exist before any density, terrain, streaming, or ray tracing work starts.

## Step 2: Add Production Feature Flags And Diagnostics First

1. Extend `FoliageSettings` with disabled-by-default feature flags for:
   - density maps
   - terrain placement
   - baked impostor atlases
   - streaming cells
   - interaction wind
   - alpha overdraw diagnostics
   - ray tracing representation
2. Add diagnostics for each flag:
   - enabled state
   - active object/cell/patch count
   - CPU update time
   - GPU memory
   - fallback reason
3. Add settings serialization tests before wiring behavior.
4. Add `SampleInputController` debug output to print the new flags only after the settings and diagnostics exist.

Acceptance:

- Every production feature can be disabled independently.
- A disabled feature has zero runtime cost beyond settings/diagnostic plumbing.

## Step 3: Replace Placeholder Density With Real Density Maps

1. Replace `FoliagePatch.DensityTexture` with a typed asset handle/reference.
2. Add density-map metadata:
   - source path or asset id
   - format
   - dimensions
   - wrap/clamp
   - UV transform
   - revision
3. Import `R8` and `R16` first; add `BC4` or KTX2 later when texture compression policy is settled.
4. Bind density maps through the bindless texture heap and populate `GPUFoliagePatch.DensityTextureIndex`.
5. Update `foliage_cull.comp` so density affects:
   - visible cluster acceptance
   - procedural grass blade count
   - authored instance probability
   - density fade
6. Use deterministic stochastic thinning from patch seed, cluster id, and density value.
7. Add diagnostics:
   - density-textured patch count
   - density-sampled cluster count
   - density-rejected cluster count
   - missing/invalid density texture count
   - density texture bytes

Acceptance:

- Painted density masks change placement without CPU per-blade work.
- Camera movement does not rebuild density-driven placement.
- Missing density maps fail safely and visibly.

## Step 4: Add Terrain-Aware Placement

1. Add a terrain query abstraction for height, normal, slope, altitude, material/biome id, and exclusion masks.
2. Add `FoliagePlacementRules`:
   - slope range
   - altitude range
   - biome mask
   - water exclusion
   - road/object exclusion
   - normal alignment strength
   - random yaw range
   - scale range
3. Generate cluster placement only when patch content, terrain data, density maps, or placement rules change.
4. Store per-cluster terrain height/normal data needed by GPU culling, raster expansion, and future ray tracing proxies.
5. Add debug views for slope rejection, biome rejection, density rejection, and terrain-normal alignment.
6. Add hillside, biome-strip, and road-exclusion sample scenes.

Acceptance:

- Foliage follows uneven terrain and respects placement rules.
- Terrain-aware placement does not add per-frame CPU rebuild cost.

## Step 5: Define Foliage Asset Contracts

1. Add a foliage prototype asset format containing:
   - mesh or procedural blade definition
   - material
   - density map
   - placement rules
   - LOD distances
   - wind parameters
   - lighting parameters
   - impostor reference
   - ray tracing policy
2. Validate importer requirements:
   - masked material required for dense foliage unless explicitly blended
   - alpha coverage mip policy required
   - monotonic LOD distances
   - density dimensions valid
   - impostor metadata valid when required
   - ray tracing policy compatible with geometry mode
3. Add command-line validation suitable for CI.
4. Add runtime inspection for selected foliage prototype, patch bounds, density map, placement rules, active LOD, impostor asset, and ray tracing policy.

Acceptance:

- Bad foliage content fails early with actionable messages.
- CI can validate foliage assets without launching the renderer.

## Step 6: Build Real Impostor Atlas Support

1. Define `FoliageImpostorAsset` metadata:
   - atlas texture
   - normal texture
   - depth/thickness texture
   - view count
   - atlas rects
   - source bounds
   - pivot
   - scale
   - source prototype id
2. Build or integrate an offline impostor baker.
3. Render far foliage from atlas pages with view selection by camera direction.
4. Crossfade from meshlet LOD to impostor LOD.
5. Track atlas bytes, loaded pages, visible impostors, and transition count.

Acceptance:

- Far tree lines use baked impostors, not only procedural crossed cards.
- Meshlet-to-impostor transitions are stable during camera movement.

## Step 7: Plan Ray Tracing Representation Before Enabling Ray Tracing

1. Add a `FoliageRayTracingMode` setting:
   - `Disabled`
   - `ProxyOnly`
   - `AuthoredMeshlets`
   - `Hybrid`
2. Add per-prototype ray tracing policy:
   - excluded from rays
   - casts ray traced shadows only
   - visible in reflections
   - visible in GI
   - proxy geometry only
   - full authored geometry
3. Decide initial production defaults:
   - grass: excluded or proxy-only for shadows
   - shrubs: proxy or coarse authored mesh
   - tree trunks/large branches: full BLAS
   - tree leaves/canopies: proxy or alpha-tested authored mesh depending on budget
   - far impostors: ray tracing proxy, not atlas cards, unless explicitly supported
4. Add diagnostics:
   - foliage BLAS count
   - foliage TLAS instance count
   - foliage AS bytes
   - BLAS build/update time
   - TLAS update time
   - ray-visible foliage count by policy
   - fallback reason when ray tracing is unsupported

Acceptance:

- Raster foliage and ray tracing foliage have an explicit contract.
- Ray tracing can be disabled without changing raster results.

## Step 8: Implement Acceleration-Structure Inputs From Foliage Data

1. Reuse foliage prototype, patch, cluster, LOD, density, and terrain results as AS input.
2. Avoid building one BLAS per grass blade or leaf card.
3. Build BLAS per stable authored prototype or proxy mesh.
4. Use TLAS instances for placed clusters/cells where useful.
5. Support compaction and rebuild/update policies:
   - static prototypes: build once, compact
   - streamed cells: build on load, destroy on unload
   - wind/interaction: prefer shader deformation outside AS until a justified dynamic-AS path exists
6. Add alpha-test support for ray tracing only after masked-material data is available to hit shaders.
7. If using Vulkan ray tracing, gate device extensions and feature structs cleanly:
   - acceleration structure
   - ray tracing pipeline or ray query
   - deferred host operations if needed
   - buffer device address
8. Add CPU fallback and unsupported-device reporting.

Acceptance:

- Foliage AS work scales with prototypes/cells/clusters, not blades.
- Unsupported ray tracing hardware keeps the raster path intact.

## Step 9: Integrate Foliage With Ray Traced Effects

1. Start with one effect at a time:
   - ray traced shadows
   - ray traced reflections
   - ray traced ambient occlusion or GI
2. For each effect, define which foliage policies participate.
3. Add quality controls per effect:
   - ray visibility distance
   - foliage alpha mode
   - proxy-only distance
   - max foliage TLAS instances
   - max AS build bytes per frame
4. Add debug views:
   - raster-only foliage
   - ray-visible foliage
   - proxy foliage
   - alpha-tested ray hits
   - AS memory heat
5. Add `SampleInputController` toggles only after the settings exist; do not collide with current Ctrl+F8/F9/F10/F11/F12 controls.

Acceptance:

- Ray traced effects degrade gracefully when foliage is too expensive.
- Debug views explain why foliage is or is not visible to rays.

## Step 10: Add Streaming And World Partition

1. Introduce `FoliageCellKey`:
   - tile x/z
   - layer id
   - biome id
   - LOD ring
2. Split large patches into streamable cells.
3. Add a `FoliageStreamingManager`:
   - async load
   - async prepare
   - residency tracking
   - bounded GPU upload
   - bounded AS build/update budget
   - unload hysteresis
4. Use camera-centered rings:
   - near: full density and raster meshlets
   - mid: reduced density
   - far: impostors and ray proxies
5. Never block the render thread on IO, readback, or AS build completion.
6. Add diagnostics:
   - resident cells
   - pending load/unload
   - upload backlog bytes
   - AS backlog bytes
   - streaming misses
   - evicted cells

Acceptance:

- Large worlds stream foliage without frame spikes.
- Raster and ray tracing memory remain bounded by active budget profiles.

## Step 11: Add Interaction And Local Wind

1. Add lightweight interaction volumes:
   - sphere
   - capsule
   - trail
   - gust
2. Store influence in a low-resolution world grid, tile buffer, or texture.
3. Add a compute pass to write and decay influence.
4. Sample influence in raster foliage shaders.
5. Keep ray tracing conservative:
   - dynamic bending does not update AS by default
   - large interactive shrubs may use coarse proxy TLAS updates only if budget allows
6. Add diagnostics for active volumes, updated tiles, interaction buffer bytes, and GPU interaction time.

Acceptance:

- Interaction bends raster foliage with bounded cost.
- Ray tracing uses a documented approximation instead of silently diverging.

## Step 12: Add Alpha Overdraw And Ray Alpha Controls

1. Add foliage alpha diagnostics:
   - depth-only benefit
   - forward fragment pressure proxy
   - shadow alpha cost
   - ray alpha-hit cost where available
2. Add alpha quality modes:
   - hard alpha test
   - dithered alpha
   - alpha-to-coverage when MSAA is enabled
   - ray tracing alpha cutoff policy
3. Add budget warnings:
   - fragment-bound
   - shadow-bound
   - memory-bound
   - cull-bound
   - AS-build-bound
   - ray-hit-bound

Acceptance:

- Foliage bottlenecks distinguish mesh work, alpha overdraw, shadows, AS builds, and ray hit cost.

## Step 13: Add Biomes, Variants, And Seasons

1. Add prototype variant sets:
   - multiple meshes
   - multiple materials
   - weighted selection
   - per-variant scale/wind
   - per-variant ray tracing policy
2. Add biome rules:
   - species mix
   - density scale
   - color/season ramp
   - wind scale
   - ray visibility scale
3. Make selection deterministic from patch seed, cell key, biome id, and cluster id.
4. Keep variant selection batched; do not create one CPU render object per instance.

Acceptance:

- Large areas avoid obvious repetition.
- Variant and biome changes remain deterministic and compatible with ray tracing policies.

## Step 14: Quality Presets And Budgets

1. Extend quality presets to scale:
   - grass density
   - grass shadow density
   - draw distance
   - impostor distance
   - streaming ring distance
   - interaction cost
   - ray visibility distance
   - max foliage AS bytes
   - max AS build/update time per frame
2. Extend performance budgets to include:
   - CPU foliage build
   - CPU foliage upload
   - GPU foliage cull
   - GPU foliage depth
   - GPU foliage forward
   - GPU foliage shadow
   - foliage memory
   - streaming upload bytes
   - interaction GPU time
   - foliage BLAS/TLAS memory
   - foliage AS build/update time
   - ray traced foliage hit cost
3. Fail CI on deterministic budget regressions where hardware timing is stable; warn where hardware timing is not stable.

Acceptance:

- Quality profiles scale raster and ray tracing foliage together.
- Budget output identifies the limiting subsystem.

## Step 15: Production Sign-Off Matrix

1. Run matrix:
   - low, medium, high, ultra
   - shadows on/off
   - impostors on/off
   - density maps on/off
   - terrain placement on/off
   - streaming on/off
   - interaction on/off
   - ray tracing off/proxy/hybrid/full where supported
2. Add automated smoke scenarios:
   - dense painted grass
   - terrain slope rejection
   - streamed forest
   - baked impostor tree line
   - interaction stress
   - alpha overdraw stress
   - ray traced shadow foliage
   - ray traced reflection foliage
3. Document content limits:
   - max density per profile
   - max patch/cell size
   - required LOD distances
   - impostor requirements
   - density map requirements
   - alpha texture requirements
   - ray tracing policy defaults
   - AS memory limits

Acceptance:

- Foliage remains GPU-driven for raster rendering.
- Ray tracing participation is explicit, budgeted, and optional.
- Tests cover settings, serialization, scene revisions, density maps, placement rules, streaming cells, GPU struct layout, bindless constants, shader compilation, fallback behavior, AS policy, and budget evaluation.

## Final Definition Of Done

The foliage system is production-ready when:

1. Dense grass, shrubs, and tree canopies render through GPU-driven raster paths.
2. Foliage can be placed by authored patches, density maps, terrain rules, biome rules, and streamable cells.
3. Camera movement does not rebuild or upload per-instance CPU draw lists.
4. Far foliage uses baked impostor atlases with stable transitions.
5. Runtime diagnostics expose CPU, GPU, memory, streaming, interaction, alpha, shadow, and ray tracing costs.
6. Quality presets scale foliage without code changes.
7. Ray tracing uses explicit per-prototype policy and bounded AS memory/update budgets.
8. Unsupported ray tracing hardware cleanly falls back to the raster foliage path.
9. Full smoke/perf scenarios pass under raster-only and ray-tracing-enabled configurations.
