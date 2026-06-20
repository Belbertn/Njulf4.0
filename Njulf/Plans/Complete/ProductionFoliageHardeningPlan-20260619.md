# Production Foliage Hardening Plan

Goal: extend the completed GPU-driven foliage implementation into a broader production foliage system suitable for large, content-authored scenes. The existing foliage plan through Phase 9 is treated as the baseline: procedural grass, authored meshlet foliage, shadows, lighting, motion vectors for authored foliage, indirect submission, far card impostors, diagnostics, stress scenes, and budget checks are already in place.

## Phase 10: Contracts And Baseline

Tasks:

1. Freeze current Phase 9 behavior with repeatable baseline captures:
   - `DenseGrassField`
   - `ShrubFoliage`
   - `MixedTreeLineFoliage`
   - `MixedTreeLineFoliageNoShadows`
2. Export baseline performance snapshots for CPU build, CPU upload, GPU cull, depth, forward, shadows, memory, visible clusters, visible meshlet draws, and alpha-heavy scenes.
3. Add feature flags before implementing new production features:
   - density maps
   - terrain placement
   - impostor atlases
   - streaming
   - interaction
   - overdraw debug
4. Extend sample input/debug controls as each feature lands, keeping current controls stable:
   - Ctrl+F8 foliage indirect dispatch
   - Ctrl+F9 far impostors
   - Ctrl+F10 foliage debug view
5. Add smoke coverage that can run each new feature disabled and enabled.

Acceptance criteria:

1. All new production features can be disabled independently.
2. Existing Phase 9 sample scenarios remain visually and behaviorally unchanged when new flags are disabled.
3. Baseline snapshots make regressions measurable before new feature work begins.

## Phase 11: Real Density And Placement Maps

Tasks:

1. Replace the placeholder `FoliagePatch.DensityTexture` object path with a typed density map handle or asset reference.
2. Add density map import support:
   - single-channel `R8` or `R16`
   - optional compressed `BC4` or KTX2 path
   - stable mip generation
   - explicit wrap/clamp metadata
3. Bind density maps through the texture bindless heap and populate `GPUFoliagePatch.DensityTextureIndex`.
4. Add patch UV mapping:
   - world bounds to density-map UV
   - optional UV offset
   - optional UV scale
   - optional UV rotation
5. Update `foliage_cull.comp` to sample density maps during cluster acceptance.
6. Make density maps affect:
   - cluster visibility
   - procedural grass blade count
   - authored instance probability
   - LOD/density fade
7. Use deterministic stochastic thinning seeded by patch seed, cluster id, and density value.
8. Add diagnostics:
   - density-map sampled cluster count
   - density-rejected cluster count
   - density texture bytes
   - missing or invalid density texture count
9. Add tests:
   - patch revision changes when density map changes
   - bindless texture index reaches GPU patch data
   - density rejection is deterministic
   - missing density map falls back safely

Acceptance criteria:

1. Artists can paint a density mask and foliage placement changes without CPU per-blade work.
2. Camera movement does not rebuild density-driven placement.
3. Density-map cost and rejection rates are visible in diagnostics.

## Phase 12: Terrain-Aware Placement

Tasks:

1. Add a terrain query abstraction that can provide:
   - height
   - normal
   - slope
   - material or biome id
   - exclusion mask
2. Add `FoliagePlacementRules`:
   - min/max slope
   - min/max altitude
   - biome mask
   - water exclusion
   - road/object exclusion
   - normal alignment strength
   - random yaw range
   - scale range
3. Generate cluster placement from terrain data only when patch content, terrain data, or placement rules change.
4. Store per-cluster terrain height and normal data needed by GPU culling and mesh expansion.
5. Update procedural grass and authored foliage shaders so roots align to terrain normals where requested.
6. Add rejection categories to diagnostics:
   - slope rejected
   - altitude rejected
   - biome rejected
   - exclusion mask rejected
7. Add debug views:
   - slope rejection
   - biome rejection
   - density rejection
   - terrain-normal alignment
8. Add sample stress scenes:
   - hillside grass
   - biome strip
   - road or exclusion mask through grass
9. Add tests:
   - deterministic terrain placement
   - placement rule revision tracking
   - slope/altitude rejection
   - stable bounds after terrain alignment

Acceptance criteria:

1. Foliage follows uneven terrain.
2. Foliage respects slope, altitude, biome, and exclusion masks.
3. Terrain-aware placement does not add per-frame CPU rebuild cost.

## Phase 13: Real Impostor Atlas Pipeline

Tasks:

1. Define `FoliageImpostorAsset` metadata:
   - atlas texture
   - optional normal texture
   - optional depth or thickness texture
   - view count
   - atlas rects
   - source bounds
   - pivot
   - scale
   - source prototype id
2. Build an offline or build-time impostor baker:
   - render source mesh from multiple azimuth angles
   - optionally include elevation bands
   - output albedo-alpha
   - optionally output normal
   - optionally output depth or thickness
   - write metadata next to generated textures
3. Pack impostors into atlas pages.
4. Import atlas pages as compressed textures where supported.
5. Add `FoliagePrototype.ImpostorAsset`.
6. Extend GPU prototype data with impostor atlas metadata.
7. Update far foliage rendering:
   - select atlas view by camera direction
   - sample baked albedo-alpha
   - use normal/depth data when present
   - crossfade from meshlet LOD to impostor LOD
8. Track memory and work:
   - atlas bytes
   - loaded atlas page count
   - visible impostor count
   - meshlet-to-impostor transition count
9. Add visual validation samples:
   - camera orbit around tree line
   - near meshlet to far impostor transition
   - impostor memory pressure scene

Acceptance criteria:

1. Far tree lines use real baked impostors rather than only procedural crossed cards.
2. Impostor memory is visible in diagnostics and budgets.
3. Meshlet-to-impostor transitions are stable during camera movement.

## Phase 14: Authoring And Validation Workflow

Tasks:

1. Add a foliage prototype asset format containing:
   - mesh
   - material
   - density map
   - placement rules
   - LOD distances
   - wind parameters
   - lighting parameters
   - impostor reference
2. Add importer validation:
   - masked material required unless explicitly blended
   - alpha coverage mip policy required for foliage textures
   - invalid density map dimensions rejected
   - missing impostor metadata reported
   - LOD distances must be monotonic
   - density and draw distance must fit active budget profile
3. Add command-line validation output for CI.
4. Add runtime inspection in the sample:
   - selected foliage prototype
   - patch bounds
   - density map name
   - placement rule summary
   - active LOD
   - active impostor asset
5. Add authoring-driven budget warnings:
   - too many clusters
   - too high density
   - excessive alpha coverage risk
   - shadow budget pressure
   - missing far LOD or impostor for large tree lines

Acceptance criteria:

1. Bad foliage content fails early with actionable errors.
2. Runtime debugging can identify which authored prototype or patch is causing cost.
3. CI can validate foliage assets without launching the renderer.

## Phase 15: Streaming And World Partition

Tasks:

1. Introduce `FoliageCellKey`:
   - world tile x/z
   - layer id
   - biome id
   - LOD ring
2. Split large foliage patches into streamable cells.
3. Add `FoliageStreamingManager`:
   - async load
   - async prepare
   - residency tracking
   - GPU upload staging budget
   - unload hysteresis
4. Use camera-centered rings:
   - near cells use full density
   - mid cells use reduced density
   - far cells use impostors only
5. Avoid render-thread stalls:
   - no blocking IO on render thread
   - no same-frame GPU readback
   - bounded upload bytes per frame
   - predictable fallback when cells are not resident
6. Add diagnostics:
   - resident cells
   - pending load cells
   - pending unload cells
   - upload backlog bytes
   - streaming misses
   - evicted cell count
7. Add stress scene:
   - camera path through a large grid of foliage cells
8. Add tests:
   - deterministic cell keys
   - stable residency decisions
   - unload hysteresis
   - upload budget limiting

Acceptance criteria:

1. Large worlds can move through foliage without frame spikes.
2. GPU memory remains bounded by the active budget profile.
3. Streaming misses are diagnosable and degrade gracefully.

## Phase 16: Interaction And Local Wind

Tasks:

1. Add lightweight interaction volumes:
   - sphere
   - capsule
   - trail
   - wind gust
2. Store interaction influence in a low-resolution world grid, tile buffer, or texture.
3. Add a compute pass to write and decay interaction influence.
4. Sample interaction influence in foliage shaders:
   - bend grass away from capsules
   - bend shrubs locally
   - apply delayed spring return
   - apply gust direction and strength
5. Keep interaction budgeted:
   - max active volumes
   - max updated tiles per frame
   - fixed influence resolution per cell
   - bounded GPU pass time
6. Add sample controls:
   - toggle interaction debug
   - spawn gust
   - show influence grid
7. Add diagnostics:
   - active interaction volumes
   - updated influence tiles
   - interaction buffer bytes
   - GPU interaction pass time
8. Add tests:
   - volume packing
   - influence decay
   - budget cap behavior

Acceptance criteria:

1. Player or vehicle movement can bend grass and shrubs without CPU foliage regeneration.
2. Interaction cost is bounded and visible in diagnostics.
3. Interaction can be disabled cleanly for perf comparison.

## Phase 17: Alpha Overdraw And Quality Controls

Tasks:

1. Add dedicated foliage alpha-overdraw diagnostics:
   - depth-only benefit
   - forward alpha fragment pressure proxy
   - optional alpha-kill count where feasible
   - foliage shadow alpha cost
2. Add debug views:
   - alpha cutoff
   - density fade
   - overdraw heat
   - shadow-only foliage cost
3. Add foliage alpha quality modes:
   - hard alpha test
   - dithered alpha
   - alpha-to-coverage when MSAA is enabled
   - stochastic alpha if temporal stability is acceptable
4. Add per-quality preset controls:
   - grass density
   - grass shadow density
   - draw distance
   - impostor distance
   - overdraw clamp
5. Add budget feedback:
   - fragment-bound warning
   - shadow-bound warning
   - memory-bound warning
   - cull-bound warning
6. Add tests:
   - quality preset maps to expected foliage settings
   - alpha mode serialization
   - diagnostic counters default to zero

Acceptance criteria:

1. Foliage bottlenecks distinguish mesh work from alpha/fragment pressure.
2. Quality presets scale foliage without changing content.
3. Alpha quality modes remain stable under camera movement.

## Phase 18: Content Variation And Biomes

Tasks:

1. Add prototype variant sets:
   - multiple meshes
   - multiple materials
   - weighted selection
   - per-variant scale range
   - per-variant wind scale
2. Add biome rules:
   - grass species mix
   - shrub species mix
   - tree species mix
   - density scale
   - color/season ramp
   - wind scale
3. Make variant selection deterministic:
   - patch seed
   - cell key
   - cluster id
4. Add GPU and CPU data so variants do not create one CPU object per instance.
5. Add seasonal controls:
   - global season parameter
   - per-biome color ramp
   - optional dead/dry density scale
6. Add sample scenes:
   - meadow
   - forest edge
   - dry season
   - green season
7. Add tests:
   - deterministic weighted selection
   - biome mask behavior
   - season parameter serialization

Acceptance criteria:

1. Large foliage areas avoid obvious repetition.
2. Variant selection remains deterministic across runs.
3. Biome and seasonal changes do not break GPU-driven batching.

## Phase 19: Production Sign-Off

Tasks:

1. Run a full performance matrix:
   - low, medium, high, ultra
   - shadows on/off
   - impostors on/off
   - streaming on/off
   - density maps on/off
   - interaction on/off
2. Add automated smoke scenarios:
   - dense painted grass
   - terrain slope rejection
   - streamed forest
   - baked impostor tree line
   - interaction stress
   - alpha overdraw stress
3. Define hard budgets:
   - CPU foliage build
   - CPU foliage upload
   - GPU foliage cull
   - GPU foliage depth
   - GPU foliage forward
   - GPU foliage shadow
   - foliage memory
   - streaming upload bytes
   - interaction GPU time
4. Fail CI on deterministic budget regressions.
5. Document content authoring limits:
   - max density per profile
   - max patch size
   - required LOD distances
   - impostor requirements
   - density map format requirements
   - alpha texture requirements
6. Create final production readiness checklist for foliage content and renderer settings.

Acceptance criteria:

1. Painted, terrain-aware, streamable foliage works with baked impostors and interaction.
2. Diagnostics identify CPU build, CPU upload, GPU cull, mesh shader work, alpha overdraw, shadows, memory, streaming, and interaction bottlenecks.
3. Quality presets and budget profiles can scale foliage without code changes.
4. Foliage remains GPU-driven and does not scale CPU draw-list work with visible blades.
5. Full test suite and focused foliage smoke/perf scenarios pass.

## Final Definition Of Done

The production foliage system is complete when:

1. Dense grass, shrubs, and tree canopies render through GPU-driven foliage paths.
2. Foliage can be placed by authored patches, density maps, terrain rules, biome rules, and streamable world cells.
3. Camera movement does not rebuild or upload per-instance CPU draw lists.
4. Authored foliage meshes use meshlets.
5. Procedural grass uses meshlet-shaped task/mesh workgroups.
6. Far tree lines use baked impostor atlases with stable meshlet-to-impostor transitions.
7. Depth, forward, shadow, wind, impostor, and interaction paths remain visually consistent.
8. Alpha overdraw and shadow cost are directly diagnosable.
9. Runtime diagnostics expose candidate, visible, culled, overflow, CPU, GPU, streaming, interaction, and memory numbers.
10. Quality presets scale density, distance, shadows, impostors, streaming, and interaction without code changes.
11. Tests cover settings, serialization, scene revisions, density maps, placement rules, streaming cells, GPU struct layout, bindless constants, shader compilation, fallback behavior, and budget evaluation.
