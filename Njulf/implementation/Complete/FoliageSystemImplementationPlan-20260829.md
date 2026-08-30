# Production Foliage System Implementation Plan

Status: Proposed  
Date: 2026-08-29

Relationship: this plan supersedes the raster-foundation, placement, impostor,
and streaming portions of
`implementation/Complete/ProductionFoliageReadinessRayTracingPlan-20260620.md`.
That document's ray-tracing policy remains separate, deferred work.

## Goal

Deliver a production foliage system that can render dense grass, ground cover,
shrubs, and trees at good visual quality while keeping the runtime and content
model understandable. Build on the existing GPU-driven foliage renderer rather
than replacing it.

The system must:

1. scale without creating a CPU object or draw call per blade, leaf, shrub, or tree;
2. preserve stable alpha coverage, shadows, motion, and LOD transitions;
3. use the renderer's existing meshlet, bindless, indirect-dispatch, Hi-Z, and
   diagnostics infrastructure;
4. keep content deterministic and rebuild GPU records only when content changes;
5. run correctly on every supported Vulkan mesh-shader device and be performance
   qualified on both the existing RTX 3060 Laptop target and a discrete AMD RDNA target;
6. keep optional systems such as terrain placement, impostors, and streaming
   layered on top of one small production rendering path.

## Executive Decision

Use three representations, selected by content scale:

1. **Procedural mesh-shader grass** for blades and very small ground cover.
   Store patches and compact spatial clusters, not individual blades. One mesh
   workgroup expands one visible cluster.
2. **Authored meshlets plus GPU instances** for shrubs, tree canopies, and other
   recognizable vegetation. Use the existing cooked LOD meshlet ranges and exact
   GPU-compacted draw commands.
3. **Baked multi-view impostors** for distant shrubs and trees after the near and
   mid paths are qualified. Do not treat generated crossed cards as a finished
   tree-impostor solution.

All production representations follow the same submission rule:

> Compute selects and compacts the exact work first; taskless mesh pipelines
> consume indirect counts afterward.

Task shaders remain only as a validation/fallback path. Do not launch a
single-invocation task workgroup merely to pass one already-visible cluster or
meshlet to a mesh shader.

Keep the qualified 48-vertex/64-triangle authored meshlet profile for foliage.
Foliage is alpha-heavy and benefits from fine culling and LOD granularity. Add a
64-vertex/126-triangle candidate only to the general connected-opaque meshlet
qualification matrix. Do not implement a 128-vertex/256-triangle foliage ABI.

## Scope Boundaries

The first production release includes:

1. procedural grass;
2. authored shrub/tree instances;
3. camera and directional-shadow visibility streams;
4. exact taskless indirect dispatch;
5. deterministic density and placement;
6. shared depth/forward/shadow/motion coverage;
7. terrain placement through a narrow query/resource interface;
8. diagnostics, fallback behavior, tests, and hardware qualification.

The following are deferred until the raster system is qualified:

1. dynamic foliage ray-tracing geometry or per-blade acceleration structures;
2. local interaction fields, footprints, destruction, or gameplay bending;
3. virtual textures or Vulkan sparse residency;
4. GPU-generated infinite-world placement;
5. blended transparency for dense foliage;
6. a device-specific cooked foliage package per GPU vendor.

DDGI proxy and future ray-tracing work must continue to consume stable prototype,
patch, cell, and instance identities, but it does not own this plan's critical path.

### Simplicity constraints

1. Maintain one parameterized cull/expand implementation for all views instead
   of separate camera and shadow algorithms.
2. Generate placements on the CPU only when a patch/cell is dirty; do not build
   a second always-on GPU placement system.
3. Build compacted and compatibility artifacts from shared shader sources.
4. Keep one production cooked foliage profile across vendors.
5. Add a new quality tier or representation only after the previous tier has
   measured quality and performance evidence.
6. Prefer `Enabled` plus the existing renderer quality preset as the user-facing
   control surface. Keep individual foliage budgets as advanced overrides.

## Current Baseline

The repository already provides most of the foundation:

1. `FoliagePrototype`, `FoliagePatch`, three geometry modes, LOD, wind, lighting,
   scene serialization, and revision tracking;
2. `FoliageManager` with persistent prototype/patch/cluster buffers, per-frame
   instance, visible-cluster, meshlet-command, counter, and indirect buffers;
3. a 64-thread compute cull pass;
4. procedural grass/card mesh generation and authored meshlet rendering;
5. depth, forward, directional-shadow, local-shadow, motion-vector, DDGI, and
   diagnostic integration;
6. shared alpha-coverage logic between depth and forward foliage fragments;
7. stable content signatures and no steady-state CPU rebuild when content is unchanged;
8. foliage stress scenarios and detailed renderer diagnostics.

The important gaps found in the current path are:

1. procedural grass/card draws still launch one single-invocation task group per
   candidate cluster in depth, forward, and shadow passes;
2. authored forward/depth/shadow pipelines receive an exact indirect count but
   still contain a single-invocation pass-through task shader; only authored
   motion has a taskless compacted variant;
3. `foliage_cull.comp` receives an occlusion flag, but does not sample Hi-Z;
4. with multiple authored clusters, each authored work item scans the cluster
   array to rediscover its owner, producing work proportional to authored work
   items multiplied by cluster count;
5. the CPU contract places 64 procedural instances in a cluster while the mesh
   shader emits at most 16 blades, so counts and density do not describe the
   rasterized result;
6. `FoliagePatch.DensityTexture` is an untyped placeholder and no density texture
   index or UV transform reaches the GPU;
7. an authored patch currently creates only one instance, which is unsuitable
   for dense shrub or forest placement;
8. the authored meshlet stride is packed through the blade-width field and can
   skip arbitrary meshlets, creating holes instead of a valid geometric LOD;
9. procedural grass does not emit deformation-aware motion vectors;
10. LOD is recomputed independently in cull and mesh shaders and has no shared
    transition state or hysteresis;
11. camera-visible foliage is reused for shadow rendering, so off-camera casters
    cannot be handled as a first-class shadow visibility set;
12. the far authored representation is generated cards rather than a baked
    albedo/opacity/normal/depth impostor asset;
13. `GpuDrivenEnabled`, `HiZCullingEnabled`, and several debug views expose intent
    that is not yet fully reflected by runtime behavior.

## Target Runtime Architecture

### Representation rules

| Content | Near | Mid | Far |
|---|---|---|---|
| Grass and tiny ground cover | Procedural blades | Reduced procedural density with width compensation | Stable density fade, then omitted |
| Shrubs and leaf clumps | Authored meshlet LOD0 | Authored meshlet LOD1/LOD2 | Baked impostor |
| Trees | Opaque trunk in the general mesh renderer; canopy in foliage | Cooked trunk/canopy LODs | Baked impostor or forest HLOD |
| Decorative cards | Authored card meshlets | Reduced authored LOD | Optional omission or impostor |

Tree trunks and other connected opaque geometry should use the general opaque
meshlet renderer. This avoids paying foliage's two-sided alpha and coverage cost
for solid geometry.

### Frame flow

```text
Content/terrain revision
    -> deterministic patch/cell build (CPU, only when dirty)
    -> persistent prototype, patch, instance, and cluster buffers

Per view
    -> foliage view cull (distance + frustum + optional conservative Hi-Z)
       -> exact procedural cluster list + indirect mesh count
       -> exact visible authored-instance list + indirect compute count
    -> authored meshlet expansion (64-thread indirect compute)
       -> exact authored meshlet command list + indirect mesh count
    -> taskless mesh draws
       -> depth / forward / motion for the main camera
       -> shadow for each shadow view
```

The main-camera list is immutable after culling and is reused by depth, forward,
and motion. Directional cascades receive separate visibility lists derived from
their own matrices and wind-expanded bounds. Local-shadow and reflection-capture
lists use the same mechanism later, under explicit budgets.

### Per-view streams

Introduce a compact `FoliageViewStream` layout with fixed offsets in one buffer
allocation per in-flight frame. It contains:

1. procedural draw commands;
2. visible authored-instance commands;
3. expanded authored meshlet draw commands, bucketed into production 48v/64t
   and compatibility 64v/126t output classes;
4. one indirect compute command for authored expansion;
5. one indirect mesh command for procedural draws;
6. one indirect mesh command per authored output class;
7. counters and overflow flags.

Required initial streams:

1. main camera;
2. one stream per active directional-shadow cascade.

Add local-shadow and reflection-capture streams only when their passes request
them. If foliage local shadows remain enabled, selected local lights must receive
budgeted per-light streams before Phase 2 is accepted; otherwise disable that
feature with an explicit diagnostic fallback. Reflection captures must use an
on-demand stream or a conservative uncullable fallback, never the main camera's
visibility list. Do not reserve worst-case storage for every possible view
unconditionally.

## CPU And Content Contracts

### Foliage prototype

Keep `FoliageGeometryMode`, but give every field one meaning:

1. mesh and material handles;
2. procedural blade height and width;
3. authored meshlet LOD ranges obtained from `MeshManager`;
4. LOD/fade distances;
5. wind and lighting settings;
6. placement defaults;
7. optional impostor asset;
8. shadow and temporal policies.

Remove `AuthoredMeshletStride` after a serialization migration. Never obtain an
LOD by rendering every Nth meshlet. Select a complete cooked LOD range instead.
Split its temporary GPU storage from `BladeWidth` before changing behavior so
the ABI remains reviewable.

Validate prototypes at registration/cook time:

1. procedural grass requires finite positive dimensions and a valid masked material;
2. authored meshlets require a valid mesh and at least one non-empty LOD range;
3. LOD distances must be finite and monotonic;
4. two-sided alpha is permitted for leaves but not silently forced onto opaque trunks;
5. an enabled impostor must reference complete atlas metadata;
6. all wind-expanded bounds must remain finite and conservative.

### Foliage patch and placement

Add an explicit placement mode:

1. `SingleInstance` for an authored tree/shrub placed at a known transform;
2. `DeterministicScatter` for authored instances generated inside patch bounds;
3. `ProceduralSurface` for grass clusters generated from density and terrain data.

Add `FoliagePlacementSettings` with:

1. density;
2. minimum spacing;
3. scale range;
4. yaw range and optional normal alignment;
5. altitude and slope ranges;
6. biome/material mask;
7. water, road, and exclusion-mask policy;
8. deterministic seed;
9. cell size.

Use a deterministic stratified-jitter placement builder first. It is simpler
than a full Poisson-disc system, provides even coverage, and is reproducible
from patch id, cell id, prototype id, and seed. Placement output changes only
when the patch, density map, terrain revision, exclusions, or prototype changes.

Authored scatter initially emits one conservative GPU cull candidate per
instance. Spatial grouping can be added only if measurements show candidate
bandwidth is a problem. This keeps authored expansion simple and removes the
current global work-item-to-cluster scan.

### Density maps

Replace the untyped runtime placeholder with an asset-level density-map
reference that resolves to a `TextureHandle` in the renderer. Keep source path,
content hash, dimensions, format, sampler policy, and revision in serialized
metadata.

Support `R8_UNorm` first. Add `R16_UNorm` only if authored tests demonstrate a
visible need. Density maps use clamp sampling and explicit world-to-density UV
scale/offset stored in `GPUFoliagePatch`.

Density behavior:

1. scalar patch density defines the maximum cluster allocation;
2. density-map samples control deterministic cluster/blade or authored-instance
   acceptance;
3. the stable hash threshold uses patch seed and candidate identity, never frame time;
4. a missing or invalid texture fails closed to a documented scalar-density
   fallback and reports a diagnostic;
5. low-density regions must not force a CPU rebuild each frame.

### Terrain boundary

Consume terrain through two small contracts rather than coupling foliage to a
particular terrain renderer:

1. a CPU `ITerrainQuery`-style interface for deterministic authored placement,
   returning height, normal, slope, biome/material id, water, and exclusions;
2. an optional GPU terrain-surface descriptor for per-blade height and normal
   sampling by procedural grass.

Until the active terrain plan supplies the GPU descriptor, procedural patches
use their authored base plane and authored scatter uses the CPU query when
available. Terrain revision participates in the foliage content signature.

## GPU Contracts

### Static records

Update the shared C#/GLSL ABI together and add layout tests.

`GPUFoliagePrototype` should expose separate fields for:

1. geometry mode and flags;
2. mesh metadata and complete LOD ranges;
3. material;
4. blade dimensions;
5. LOD/fade distances;
6. wind and lighting;
7. impostor metadata index.

`GPUFoliagePatch` should expose:

1. bounds and prototype;
2. scalar density and seed;
3. density texture index;
4. density UV scale/offset;
5. optional terrain descriptor index;
6. stable object/material identities and revisions.

`GPUFoliageCluster` remains a conservative spatial input record. Align
procedural cluster capacity with raster output: one cluster represents at most
16 candidate blades while the production shader declares 64 vertices and 32
primitives. This makes estimates, budgets, culling, and visible density agree.

### Dynamic commands

Add `GPUFoliageProceduralDrawCommand` containing:

1. cluster index;
2. selected LOD band;
3. stable candidate/active density information;
4. transition fraction and width compensation;
5. pass/view flags.

Add `GPUFoliageAuthoredInstanceCommand` containing:

1. instance and cluster index;
2. selected complete LOD range;
3. conservative wind-expanded world bounds;
4. transition/impostor state.

Keep `GPUFoliageMeshletDrawCommand`, but produce it only in authored expansion.
Every raster pass reads the selected LOD from the command instead of recomputing
distance independently.

### Vulkan mesh-shader properties

Query and record `VkPhysicalDeviceMeshShaderPropertiesEXT`, including preferred
task/mesh invocation counts, local-invocation output preferences, compact-output
preferences, and output granularities. Use them for diagnostics and selecting
precompiled shader variants, not for silently choosing a different cooked asset.

Required production variants:

1. procedural compacted: 64 threads, 64 vertices, 32 primitives;
2. authored compacted: 64 threads, 48 vertices, 64 primitives;
3. authored compatibility: 128 threads, 64 vertices, 126 primitives.

The authored expansion stage writes the output-class bit explicitly and appends
to the matching stream. A 64v/126t compatibility meshlet must never reach a
48v/64t production pipeline.

Keep output declarations equal to the data contract. Call `SetMeshOutputsEXT`
immediately after loading the command/counts and before expensive vertex work.
Have invocation `i` write output `i` wherever practical, using workgroup-sized
strides for remaining outputs. Avoid large shared arrays.

## Phased Implementation

### Phase 0: Freeze And Measure The Existing Baseline

1. Capture release-build snapshots for:
   - `DenseGrassField`;
   - `ShrubFoliage`;
   - `MixedTreeLineFoliage`;
   - `MixedTreeLineFoliageNoShadows`;
   - `ForestFoliage`.
2. Record three independent runs of 1000 measured frames for the existing RTX
   3060 Laptop target.
3. Add one discrete AMD RDNA2 or RDNA3 device to the performance matrix.
4. Store p50/p95 CPU frame, GPU frame, foliage cull, depth, forward, motion,
   shadow, task/mesh workgroups, fragment invocations if available, and buffer bytes.
5. Capture reference images with static wind, animated wind, LOD traversal,
   shadow cascades, and TAA history.
6. Confirm all current settings and debug views either affect runtime behavior
   or are explicitly marked unimplemented.

Acceptance:

1. baseline artifacts identify commit, build configuration, device, driver,
   settings, resolution, camera, and asset hashes;
2. all overflow counters are zero;
3. the current visual result is reproducible before structural changes begin.

### Phase 1: Correct The Static Model And ABI

1. Separate authored stride from blade width, then remove stride-based meshlet skipping.
2. Add placement mode/settings and serialization migration.
3. Align procedural clusters to 16 candidate blades.
4. Add dynamic procedural and authored-instance draw command structs.
5. Add density/terrain/impostor fields with invalid sentinel values, without
   enabling those features yet.
6. Add mesh-shader property discovery and diagnostics.
7. Make `GpuDrivenEnabled` a documented debug fallback switch or remove it through
   settings migration; do not leave a shipping control with no behavior.
8. Add C#/GLSL size, offset, packing, finite-value, and sentinel tests.

Acceptance:

1. old scenes migrate deterministically;
2. unchanged scenes preserve their current appearance;
3. blade estimates match the maximum procedural blades the mesh shader can emit;
4. authored LODs never omit arbitrary meshlets;
5. all ABI tests and shader compilation tests pass.

### Phase 2: Build Exact Taskless Production Submission

1. Replace the mixed visible-cluster list with per-view procedural and authored lists.
2. Refactor `foliage_cull.comp` into the view-cull stage:
   - one lane per procedural cluster or authored instance;
   - distance and frustum rejection first;
   - conservative previous-frame Hi-Z only when the shared Hi-Z policy says it is valid;
   - LOD selected once and stored in the output command;
   - subgroup/workgroup allocation where it reduces atomic pressure;
   - exact indirect counts written for downstream work.
3. Add `foliage_authored_expand.comp`:
   - one 64-thread workgroup per visible authored instance;
   - lanes traverse the selected meshlet range with a 64-wide stride;
   - test wind-expanded meshlet spheres against the target view;
   - apply normal-cone culling only to eligible single-sided content;
   - append exact authored meshlet commands;
   - write the authored indirect mesh count.
4. Add taskless compacted variants of procedural and authored mesh shaders for
   forward, depth, shadow, and motion-compatible consumers.
5. Reuse one main-view list for depth, forward, and motion.
6. Build separate directional-cascade lists so off-camera casters remain correct.
7. Build budgeted per-view streams for every enabled local foliage shadow, or
   disable local foliage shadows with a reported fallback until those streams exist.
8. Give reflection captures an on-demand view stream; do not reuse main-camera visibility.
9. Retain current task shaders behind a validation setting that compares emitted
   command identities and rendered references.
10. Record explicit compute-to-indirect and compute-to-mesh storage barriers for
   every stream.

Acceptance:

1. the normal production path records zero foliage task-shader workgroups;
2. every mesh workgroup corresponds to one valid compacted command;
3. the multi-authored-instance path has no scan over unrelated clusters;
4. depth, forward, and motion consume identical main-view command identities;
5. each shadow cascade consumes its own conservative visibility stream;
6. Hi-Z is disabled after camera cuts, invalid history, fast motion, and scene
   revisions according to the shared policy;
7. validation and production outputs match, with zero overflow or invalid-command counters;
8. unchanged scenarios regress by no more than 2% p95 CPU or GPU frame time.

### Phase 3: Implement Density And Scalable Placement

1. Add the density-map asset/import path and resolve it through `TextureManager`.
2. Populate density texture and UV data in `GPUFoliagePatch`.
3. Sample density deterministically in view culling and procedural generation.
4. Generate authored scatter instances with the stratified-jitter builder.
5. Emit one stable authored instance/cull record per accepted placement.
6. Rebuild only dirty patches/cells and upload only changed ranges where practical.
7. Add density rejection, missing texture, generated instance, dirty cell, build
   time, and upload byte diagnostics.
8. Add density edge, checkerboard, sparse island, and invalid-texture sample scenes.

Acceptance:

1. painted density changes visible placement without per-frame CPU generation;
2. the same inputs produce byte-identical placement records across runs;
3. authored scatter can produce a forest from one patch rather than one scene
   entity per tree;
4. density changes preserve stable identities outside dirty regions;
5. missing density assets fall back predictably and report the reason;
6. steady camera motion does not change the content signature or upload static data.

### Phase 4: Finish Temporal And Visual Quality

1. Replace whole-band procedural density jumps with a continuous density fraction:
   - use stable candidate hashes for fractional acceptance;
   - mildly widen retained blades as density falls, with a conservative cap;
   - never reseed from frame number.
2. Add an LOD transition window and stable coverage dither. Store the chosen band
   and transition in the draw command so every pass agrees. During an authored
   transition, expand both complete source and target LOD ranges and tag them
   with complementary coverage phases; never fade between two incomplete
   meshlet subsets.
3. Add a compact procedural motion-vector mesh shader that evaluates the same
   deterministic blade root and wind function at current and previous time.
4. Keep authored and procedural wind analytic and root-anchored. Expand all cull
   and shadow bounds by the proven maximum displacement.
5. Ensure depth, forward, shadow, and motion use one shared coverage function,
   including alpha cutoff and LOD/density dither.
6. Add alpha-coverage-preserving texture mips to foliage asset cooking.
7. Use the same stable subset for reduced grass shadow density; do not choose a
   new random subset per cascade or frame.
8. Validate two-sided normal handling, normal bending, wrap diffuse, backlighting,
   material normal maps, and mirrored authored instances.
9. Keep the four-vertex/two-triangle blade as the default. Qualify a segmented
   hero-grass variant only as a separate quality experiment after the default
   path meets density and temporal targets.

Acceptance:

1. slow camera movement through every LOD boundary has no whole-cluster pop;
2. static grass is stable under TAA and moving grass writes physically consistent velocity;
3. depth/forward coverage mismatch tests report zero mismatched pixels;
4. wind cannot move geometry outside culling or shadow bounds;
5. reduced-density shadows remain temporally stable;
6. leaf and blade mip transitions do not visibly lose alpha coverage.

### Phase 5: Add Terrain, Biome, And Exclusion Placement

1. Introduce the CPU terrain-query boundary and add terrain revision to patch signatures.
2. Apply altitude, slope, biome/material, water, road, and exclusion rules during
   authored placement generation.
3. Add optional GPU terrain height/normal sampling for procedural blade roots.
4. Expand cluster bounds using terrain min/max height plus blade and wind extent.
5. Rebuild only cells affected by terrain edits or exclusion-mask dirty rectangles.
6. Add debug views for density, slope, biome, exclusion, terrain normals, and
   conservative bounds.
7. Add hillside, road-cut, shoreline, biome boundary, and runtime terrain-edit scenarios.

Acceptance:

1. grass roots follow terrain without floating or systematic burial;
2. authored instances respect slope, altitude, biome, water, and exclusions;
3. a local terrain edit cannot rebuild or re-upload unrelated foliage cells;
4. terrain edits retain stable placement identities for unchanged candidates;
5. conservative bounds remain valid for every accepted placement and wind phase.

### Phase 6: Add Real Impostors And World Cells

1. Define `FoliageImpostorAsset` containing:
   - albedo/opacity atlas;
   - normal atlas;
   - conservative depth or thickness atlas;
   - view directions and atlas rectangles;
   - source bounds, pivot, scale, and content hash.
2. Add an offline deterministic impostor baker; do not bake during gameplay.
3. Select the nearest atlas view and blend adjacent views only when reference
   captures prove it necessary.
4. Cross-fade complete authored LOD2 geometry to the impostor using stable coverage.
5. Partition large foliage patches into `FoliageCellKey` world cells.
6. Add bounded async cell loading, upload budgets, residency hysteresis, and
   frame-safe retirement using existing renderer patterns.
7. Keep procedural cluster generation deterministic from cell identity so cells
   need not serialize every grass blade.
8. Use near/mid/far rings for full authored geometry, reduced density/LOD, and impostors.

Acceptance:

1. distant tree lines use baked source-derived impostors, not generic crossed cards;
2. geometry-to-impostor transitions preserve silhouette, pivot, color, and shadow stability;
3. cell streaming has no unbounded upload, allocation, or acceleration-structure work;
4. unload/reload reproduces identical placement;
5. a missing/corrupt impostor falls back to the coarsest valid authored LOD;
6. all residency and overflow limits have diagnostics and zero-error qualification runs.

### Phase 7: AMD And Cross-Vendor Qualification

1. Capture Radeon GPU Profiler traces for dense grass and forest scenarios.
2. Check mesh export allocation, wave size, VGPR/LDS pressure, rasterizer/export
   stalls, task-stage activity, and pixel-shader dominance.
3. Compare the production variants:
   - procedural 64-thread 64v/32p;
   - authored 64-thread 48v/64p;
   - authored compatibility 128-thread 64v/126p.
4. Add `64v/126t` only as a general connected-opaque cooker candidate. Do not
   apply it to masked foliage unless foliage-specific captures clear the normal gate.
5. Preserve a single production cooked foliage profile unless a measured,
   reviewed device rule justifies more complexity.
6. Run the complete correctness and performance matrix on NVIDIA and AMD.

Adoption gates:

1. no correctness, coverage, shadow, motion, or deterministic-placement failures;
2. zero buffer, command, or residency overflows in qualified scenes;
3. no candidate may regress p95 CPU or GPU frame time by more than 2%;
4. a meshlet-size candidate must improve the target workload by at least 3% p95
   before replacing the checked-in baseline;
5. quality presets must reduce cost monotonically without changing deterministic identities;
6. performance evidence must include three independent 1000-frame runs and
   profiler captures, not unit-test inference.

## Diagnostics And Debugging Contract

Expose per-frame and per-view values for:

1. patch, cell, procedural-cluster, and authored-instance candidates;
2. distance-, frustum-, Hi-Z-, density-, terrain-, biome-, and exclusion-rejected counts;
3. visible procedural commands, visible authored instances, and expanded meshlet commands;
4. LOD0/LOD1/LOD2/impostor counts and transition counts;
5. task and mesh workgroups by path, proving the production task count is zero;
6. output capacity, high-water mark, overflow, and invalid-command counts;
7. static and per-frame buffer bytes;
8. CPU dirty-build/upload time and bytes;
9. GPU cull, authored expansion, depth, forward, motion, and shadow times;
10. density/impostor texture bytes and cell residency;
11. current shader variant and queried mesh-shader preferences;
12. fallback state and a human-readable reason.

Complete the existing foliage debug views and add only missing views:

1. clusters/candidates;
2. LOD and transition fraction;
3. density acceptance;
4. wind displacement and expanded bounds;
5. Hi-Z rejection;
6. shadow stream membership;
7. alpha cutoff/coverage;
8. terrain/biome/exclusion rejection;
9. impostor view and transition;
10. overdraw heat map if a reusable renderer facility exists.

## Test Plan

### Unit and serialization tests

1. deterministic placement and stable candidate identities;
2. patch/prototype/settings revision propagation;
3. scene migration from the current foliage schema;
4. monotonic LOD validation and complete-range fallback;
5. density UV mapping, missing texture fallback, and dirty-region invalidation;
6. terrain slope/altitude/biome/exclusion rules;
7. conservative static and wind-expanded bounds;
8. GPU struct sizes, offsets, sentinels, and indirect-command offsets;
9. capacity math and overflow accounting;
10. cell streaming retry, hysteresis, and frame-safe retirement.

### Shader and pipeline tests

1. every required compacted shader artifact is built;
2. compacted pipelines contain no task stage;
3. production output declarations exactly match 64v/32p or 48v/64p;
4. compatibility declarations remain 64v/126p;
5. `SetMeshOutputsEXT` executes before vertex expansion;
6. all raster consumers include the shared foliage coverage contract;
7. procedural motion uses the same seed, root, LOD, and wind equations as forward;
8. view cull and authored expansion expose required barriers and indirect usage;
9. task validation and taskless production emit equivalent command identities.

### Visual and runtime tests

1. stationary and moving camera through all LOD transitions;
2. static and animated wind with TAA;
3. depth/forward/shadow silhouette comparison;
4. off-camera directional-shadow casters;
5. camera cuts, rapid turns, teleports, and invalid Hi-Z history;
6. dense alpha overdraw and backlit leaf canopies;
7. density-map hard/soft boundaries;
8. sloped terrain, shoreline, road exclusion, and terrain edits;
9. missing LOD, density, terrain, and impostor resources;
10. streaming cell load/unload while frames are in flight.

## Expected File-Level Work

Primary existing files:

1. `Njulf.Core/Foliage/FoliagePrototype.cs`
2. `Njulf.Core/Foliage/FoliagePatch.cs`
3. `Njulf.Core/Foliage/FoliageLodSettings.cs`
4. `Njulf.Assets/Scenes/SceneDocument.cs`
5. `Njulf.Assets/Scenes/SceneDocumentLoader.cs`
6. `Njulf.Assets/Scenes/SceneDocumentWriter.cs`
7. `Njulf.Rendering/Data/GPUStructs.cs`
8. `Njulf.Rendering/Data/RenderSettings.cs`
9. `Njulf.Rendering/Data/SceneRenderingData.cs`
10. `Njulf.Rendering/Data/RendererDiagnostics.cs`
11. `Njulf.Rendering/Core/VulkanContext.cs`
12. `Njulf.Rendering/Resources/FoliageManager.cs`
13. `Njulf.Rendering/Pipeline/FoliageCullPass.cs`
14. `Njulf.Rendering/Pipeline/PipelineObjects/FoliagePipeline.cs`
15. `Njulf.Rendering/Pipeline/DepthPrePass.cs`
16. `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
17. `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`
18. `Njulf.Rendering/Pipeline/MotionVectorPass.cs`
19. `Njulf.Shaders/common.glsl`
20. `Njulf.Shaders/foliage_cull.comp`
21. `Njulf.Shaders/foliage_grass.mesh`
22. `Njulf.Shaders/foliage_mesh.mesh`
23. `Njulf.Shaders/foliage_motion.mesh`
24. `Njulf.Shaders/foliage_coverage.glsl`
25. `Njulf.Shaders/Njulf.Shaders.csproj`

Likely new files:

1. `Njulf.Core/Foliage/FoliagePlacementMode.cs`
2. `Njulf.Core/Foliage/FoliagePlacementSettings.cs`
3. `Njulf.Core/Foliage/FoliageImpostorAsset.cs`
4. `Njulf.Rendering/Resources/FoliagePlacementBuilder.cs`
5. `Njulf.Rendering/Resources/FoliageCellKey.cs`
6. `Njulf.Rendering/Resources/FoliageStreamingManager.cs`
7. `Njulf.Rendering/Pipeline/FoliageAuthoredExpandPass.cs`
8. `Njulf.Shaders/foliage_authored_expand.comp`
9. `Njulf.Shaders/foliage_grass_motion.mesh`

Prefer compile-time defines that produce compacted and compatibility artifacts
from shared mesh shader sources over duplicating complete shaders.

## Delivery Order

The minimum useful production slice is Phases 0 through 4. Do not begin impostor
or streaming work while any of the following remain true:

1. production foliage still launches pass-through task shaders;
2. density/visible counts do not match emitted geometry;
3. authored expansion scans unrelated clusters;
4. procedural motion is missing while temporal history is enabled;
5. camera and shadow visibility are conflated;
6. overflow counters are non-zero in baseline scenes.

Terrain placement may proceed in parallel with Phase 4 once the CPU/GPU boundary
is frozen. Impostors and streaming begin only after the near/mid representation
passes the full correctness and performance gate.

## Definition Of Done

The foliage system is production-ready when:

1. grass, shrubs, and forest scenes are authored through patches/cells rather
   than per-blade or per-tree scene objects;
2. unchanged foliage has zero steady-state CPU rebuild and upload work;
3. the production path uses exact taskless indirect mesh dispatch for every
   supported foliage raster pass;
4. procedural and authored visibility, LOD, coverage, motion, and shadows are
   temporally stable and share one command/coverage contract;
5. density and terrain placement are deterministic and locally invalidated;
6. off-camera shadow casters and wind-expanded bounds are correct;
7. far trees use a validated impostor or a complete authored fallback;
8. all counters, overflows, fallbacks, buffer usage, and shader variants are observable;
9. unit, shader, serialization, visual, smoke, and hardware qualification suites pass;
10. RTX and Radeon captures meet the declared p95 gates with zero correctness counters.

## Primary Technical References

1. AMD GPUOpen, mesh shader optimization and best practices:  
   https://gpuopen.com/learn/mesh_shaders/mesh_shaders-optimization_and_best_practices/
2. AMD GPUOpen, procedural grass rendering with mesh shaders:  
   https://gpuopen.com/learn/mesh_shaders/mesh_shaders-procedural_grass_rendering/
3. Vulkan `VK_EXT_mesh_shader` proposal and implementation preferences:  
   https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_mesh_shader.html
4. Njulf meshlet system v2 production contract:  
   `docs/rendering/meshlet-system-v2.md`
5. Njulf terrain runtime editing plan and foliage query boundary:  
   `implementation/HeightmapTerrainRuntimeEditingPlan-20260620.md`
