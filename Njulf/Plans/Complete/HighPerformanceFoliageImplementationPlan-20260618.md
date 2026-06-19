# High Performance Foliage Implementation Plan

Goal: add high quality foliage that scales to dense grass, bushes, and trees without turning camera movement into CPU draw-list work. Use meshlets wherever foliage is real mesh geometry, and use meshlet-shaped procedural clusters for grass and tiny plants.

## Current Renderer Baseline

The renderer already has the right foundation:

1. Vulkan mesh/task shaders are mandatory in `VulkanContext`.
2. `MeshManager` stores shared mesh data, meshlets, meshlet-local vertex indices, meshlet-local triangle indices, split vertex streams, and generated meshlet LOD ranges.
3. `SceneDataBuilder` builds `GPUMeshletDrawCommand` and `GPUPackedMeshletDrawCommand` lists, uploads them through bindless buffers, and records per-frame task constants.
4. `DepthPrePass`, `ForwardPlusPass`, motion vectors, and shadow passes already draw meshlet lists through task/mesh shaders.
5. `forward.task` already performs GPU frustum and Hi-Z culling for CPU-submitted candidate meshlets.
6. Static repeated content can already enter the scene through `StaticInstanceBatch`, but it still emits per-instance, per-meshlet CPU draw commands.
7. Diagnostics already track CPU build/upload costs, meshlet candidate counts, GPU task counters, pass timings, and buffer memory.

The foliage system should therefore avoid a separate old-style vertex-instancing renderer. It should become a specialized GPU-driven meshlet producer that feeds foliage-specific mesh/task shaders and, later, the general GPU-driven submission roadmap.

## Non-Goals For The First Shipping Slice

1. Do not make blended foliage the default. Leaves and grass should primarily be alpha-tested/masked so they write depth and cast predictable shadows.
2. Do not create one `RenderObject` or one `GPUObjectData` per grass blade or leaf card.
3. Do not add CPU camera-dependent foliage draw-list rebuilds as the main path.
4. Do not require authored impostors for the first version. Impostors are a later LOD extension for forests and far tree lines.
5. Do not rewrite the general material system before foliage works. Add narrowly-scoped foliage material flags/extensions.

## Content Classes

Support three foliage classes with one architecture:

1. Grass and tiny ground plants:
   - procedural or semi-procedural meshlet clusters.
   - one cluster contains approximately 16-64 blades/cards and emits at most the mesh shader output limits.
   - CPU stores patch definitions, density parameters, seeds, and optional density texture references.
   - GPU expands blades/cards in the mesh shader.

2. Bushes, shrubs, and leaf clumps:
   - authored meshes registered through `MeshManager`.
   - use normal `GPUMeshlet` data.
   - GPU foliage instance data supplies transforms, variation, wind, and LOD state.

3. Trees:
   - trunk/branch mesh can use the existing opaque meshlet path.
   - leaf canopy uses the foliage meshlet path with alpha-tested materials, wind, and foliage lighting.
   - optional far impostor/card LOD comes after the meshlet path is stable.

## Architecture

Add foliage as a renderer-owned subsystem, not as many normal scene objects.

Core scene API:

1. Add `FoliagePrototype` in `Njulf.Core/Scene` or a new `Njulf.Core/Foliage` namespace.
2. Add `FoliagePatch` or `FoliageLayer` to hold terrain/world area placement.
3. Add `Scene.FoliageLayers` or `Scene.FoliagePatches` beside `RenderObjects`, `StaticInstanceBatches`, and `ParticleEffects`.

Recommended CPU-facing model:

```csharp
public sealed class FoliagePrototype
{
    public string Name { get; init; } = "FoliagePrototype";
    public object? Mesh { get; init; }
    public object? Material { get; init; }
    public FoliageGeometryMode GeometryMode { get; init; }
    public FoliageLodSettings Lod { get; init; } = new();
    public FoliageWindSettings Wind { get; init; } = new();
    public FoliageLightingSettings Lighting { get; init; } = new();
}

public sealed class FoliagePatch
{
    public string Name { get; init; } = "FoliagePatch";
    public FoliagePrototype Prototype { get; init; }
    public BoundingBox Bounds { get; init; }
    public float Density { get; init; }
    public uint Seed { get; init; }
    public object? DensityTexture { get; init; }
    public bool Visible { get; set; } = true;
}
```

GPU resource owner:

1. Add `FoliageManager` under `Njulf.Rendering/Resources`.
2. It owns prototype, patch, cluster, generated instance, visible draw, counter, and indirect argument buffers.
3. It registers those buffers in `BindlessIndexTable.cs` and `common.glsl`.
4. It follows the existing two-frame buffer pattern used by scene and GPU particle buffers.

Render graph passes:

1. `FoliageCullPass`: compute pass that builds visible foliage clusters and/or draw commands.
2. `FoliageDepthPass`: masked depth-only mesh/task pass, inserted into the depth stage before `HiZBuildPass`.
3. `FoliageForwardPass`: masked forward pass after opaque forward and before transparent forward.
4. `FoliageShadowPass` integration: draw foliage into directional shadows selectively, then spot/point shadows only when budgets allow.

## GPU Data Contract

Add compact, shader-friendly structs to `GPUStructs.cs` and `common.glsl`.

Suggested structs:

```csharp
public struct GPUFoliagePrototype
{
    public uint MeshMetadataIndex;
    public uint MeshletOffset;
    public uint MeshletCount;
    public uint MaterialIndex;
    public uint GeometryMode;
    public uint Flags;
    public float BladeHeight;
    public float BladeWidth;
    public Vector4 LodDistances;
    public Vector4 WindParams;
    public Vector4 ColorVariation;
}

public struct GPUFoliagePatch
{
    public Vector4 BoundsMinDensity;
    public Vector4 BoundsMaxSeed;
    public uint PrototypeIndex;
    public uint ClusterOffset;
    public uint ClusterCount;
    public uint Flags;
}

public struct GPUFoliageCluster
{
    public Vector4 WorldCenterRadius;
    public uint PatchIndex;
    public uint FirstInstance;
    public uint InstanceCount;
    public uint Flags;
}

public struct GPUFoliageInstance
{
    public Vector4 PositionScale;
    public Vector4 RotationSeed;
    public Vector4 ColorBend;
}

public struct GPUFoliageMeshletDrawCommand
{
    public uint MeshletIndex;
    public uint InstanceIndex;
    public uint PrototypeIndex;
    public uint MaterialIndex;
    public Vector4 WorldCenterRadius;
    public uint Flags;
    public uint LodLevel;
    public uint Padding0;
    public uint Padding1;
}
```

Keep foliage commands separate from `GPUMeshletDrawCommand` initially. The existing mesh shaders assume `InstanceId` indexes `GPUObjectData`; dense foliage needs `GPUFoliageInstance` instead.

## Meshlet Usage

Use meshlets in four places:

1. Authored foliage meshes:
   - bushes, shrubs, clumps, and tree canopies use `MeshManager` meshlets directly.
   - each visible foliage instance emits one draw command per visible source meshlet.
   - the foliage mesh shader reuses `GPUMeshlet`, meshlet vertex indices, triangle indices, and split vertex streams.

2. Procedural grass clusters:
   - each cluster is a meshlet-like work unit.
   - the mesh shader emits a fixed maximum number of vertices/primitives from seeds and density.
   - keep output under current shader limits: `max_vertices = 64`, `max_primitives = 126`, or create a separate grass mesh shader with device-limit validation.

3. Culling granularity:
   - patch bounds for cheap CPU/GPU rejection.
   - cluster bounds for GPU frustum and Hi-Z rejection.
   - source meshlet bounds for authored geometry culling where useful.

4. LOD:
   - near: full density and full meshlet set.
   - mid: reduced grass density and larger authored meshlet LOD range.
   - far: simplified clumps/cards.
   - very far: optional impostor atlas later.

## Render Flow

Initial high-performance flow:

1. CPU uploads stable foliage prototypes and patches only when scene content changes.
2. `FoliageManager` generates or updates cluster buffers when a patch changes, not every frame.
3. Per frame, `FoliageCullPass` reads camera, frustum planes, settings, and optional Hi-Z.
4. GPU culling appends visible cluster/draw records and writes counters.
5. Depth pass draws alpha-tested foliage depth for visible near and mid foliage.
6. Hi-Z builds from scene depth including foliage that participated in the depth pass.
7. Forward pass draws visible foliage with the same visibility buffers or a second Hi-Z-refined pass if needed.
8. Shadow passes draw a filtered subset:
   - trees and bushes in all relevant cascades.
   - grass only in nearest cascades or within a configurable shadow distance.

Important ordering:

1. For the first version, depth foliage culling should use frustum, distance, density LOD, and optional previous-frame Hi-Z only.
2. Forward foliage may use current-frame Hi-Z, but keep a conservative bias to avoid self-occlusion and alpha-mask popping.
3. Add a setting to disable foliage Hi-Z independently from normal opaque Hi-Z during validation.

## Pipeline And Shader Work

New shaders:

1. `foliage_cull.comp`
   - resets counters.
   - culls patches/clusters.
   - selects LOD and density.
   - writes visible cluster records and indirect mesh-task arguments where supported.

2. `foliage_grass.task` / `foliage_grass.mesh`
   - one task per visible procedural cluster.
   - mesh shader expands blades/cards procedurally.
   - outputs normal, tangent, UV, color variation, and material index.

3. `foliage_mesh.task` / `foliage_mesh.mesh`
   - draws authored meshlet foliage instances.
   - fetches `GPUFoliageInstance` instead of `GPUObjectData`.
   - applies wind before projection.

4. `foliage_depth.frag`
   - alpha cutoff only.
   - supports alpha coverage preserving textures and dither fade.

5. `foliage_forward.frag`
   - can share code from `forward.frag` through `common.glsl` helpers where practical.
   - adds foliage lighting terms without disturbing default PBR.

Pipeline objects:

1. Add foliage pipelines to `MeshPipeline` only if the shared layout remains clean.
2. If foliage push constants and shaders diverge too much, add `FoliagePipeline`.
3. Keep descriptor sets compatible with the bindless heap.
4. Add shader compile entries automatically through existing `*.task;*.mesh;*.frag;*.comp` glob.

## Quality Features

Material and texture quality:

1. Use masked alpha for most foliage.
2. Add alpha cutoff, alpha-to-coverage or stochastic alpha option, and distance dither fade.
3. Require alpha coverage preserving mip generation for leaf/grass textures.
4. Prefer KTX2/BC compressed textures once the asset path can preserve alpha quality.
5. Support per-instance hue, value, roughness, and normal strength variation.

Lighting:

1. Add a foliage material flag, for example `MATERIAL_FEATURE_FOLIAGE`.
2. Add wrap diffuse or subsurface-style backlighting controlled by material parameters.
3. Bend normals for grass cards so they shade like clumps instead of flat quads.
4. Support double-sided normal handling without forcing all materials to be double-sided.
5. Keep specular restrained for grass, configurable for waxy leaves.

Wind:

1. Compute wind procedurally from world position, time, prototype wind params, and instance seed.
2. Use low-frequency trunk/branch sway and high-frequency leaf flutter separately.
3. Apply wind consistently in depth, forward, shadow, and motion vector paths.
4. Add previous-frame wind support before enabling motion vectors for foliage.

Temporal stability:

1. Crossfade LOD and density changes through deterministic screen-door dithering.
2. Seed dithering by stable instance/cluster IDs, not by frame number.
3. Avoid per-frame random placement changes.
4. Add a debug view for LOD bands, density fade, and wind strength.

## Performance Strategy

CPU:

1. CPU work should scale with changed patches, not visible blades.
2. Patch signatures should include prototype revision, patch bounds, density, seed, and placement map revision.
3. Avoid per-frame CPU generation of foliage instances except in a debug/fallback path.
4. Reuse `StagingRing`, `BufferManager`, `FenceBasedDeleter`, and bindless registration patterns.

GPU:

1. Cull at patch and cluster level before emitting meshlet work.
2. Use append counters with explicit overflow handling.
3. Use indirect mesh-task dispatch when `CmdDrawMeshTasksIndirectEXT` is available through Silk.NET.
4. Keep a direct dispatch fallback:
   - dispatch by visible capacity only for small scenes or validation.
   - otherwise use delayed readback counters for diagnostics, not for frame-critical draw counts.
5. Separate grass and authored foliage buckets by material class and LOD so shaders branch less.
6. Cap grass density based on quality preset and frame budget.
7. Shadow foliage density should be independently clampable.

Memory:

1. Use compact instance data: position/scale, rotation/seed, color/bend.
2. Store generated grass instances only if needed. Prefer deterministic generation from patch/cluster seed.
3. Do not duplicate meshlet geometry for foliage prototypes that already live in `MeshManager`.
4. Track foliage buffers under a new `MemoryBudgetCategory.Foliage` or include clear subfields in diagnostics.

## Settings

Add `FoliageSettings` to `RenderSettings.cs`.

Suggested fields:

```csharp
public sealed class FoliageSettings
{
    public bool Enabled { get; set; } = true;
    public bool GpuDrivenEnabled { get; set; } = true;
    public bool HiZCullingEnabled { get; set; } = true;
    public bool CastShadows { get; set; } = true;
    public float GrassShadowDistance { get; set; } = 25f;
    public float MaxDrawDistance { get; set; } = 250f;
    public float DensityScale { get; set; } = 1f;
    public int MaxVisibleClusters { get; set; } = 262144;
    public int MaxVisibleMeshletDraws { get; set; } = 524288;
    public FoliageDebugView DebugView { get; set; } = FoliageDebugView.None;
}
```

Quality presets should change density, draw distance, shadow distance, alpha mode, wind quality, and max visible capacities.

## Diagnostics

Add fields to `SceneRenderingData`, `RendererDiagnostics`, snapshots, and budget evaluation:

1. `FoliagePatchCount`
2. `FoliagePrototypeCount`
3. `FoliageClusterCount`
4. `FoliageVisibleClusterCount`
5. `FoliageCulledClusterCount`
6. `FoliageVisibleMeshletDrawCount`
7. `FoliageGrassBladeEstimate`
8. `FoliageLod0/1/2VisibleCount`
9. `FoliageHiZTestedCount`
10. `FoliageHiZRejectedCount`
11. `FoliageOverflowCount`
12. `FoliageInstanceBufferBytes`
13. `FoliageClusterBufferBytes`
14. `FoliageDrawBufferBytes`
15. `CpuFoliageBuildMicroseconds`
16. `CpuFoliageUploadMicroseconds`
17. `GpuFoliageCullMicroseconds`
18. `GpuFoliageDepthMicroseconds`
19. `GpuFoliageForwardMicroseconds`
20. `GpuFoliageShadowMicroseconds`

Add debug views:

1. clusters
2. LOD bands
3. density fade
4. wind strength
5. Hi-Z rejected clusters
6. shadow-casting foliage
7. material alpha cutoff

## Implementation Phases

### Phase 0: Baseline And Contracts

Tasks:

1. Capture current frame diagnostics on Sponza and a synthetic static instance foliage-like stress scene.
2. Record CPU scene build/upload, meshlet draw upload, task invocations, GPU timings, and memory.
3. Add `FoliageSettings`, `FoliageDebugView`, empty diagnostics fields, and tests for defaults/serialization.
4. Add bindless index reservations for foliage buffers, but do not use them yet.

Acceptance criteria:

1. Existing scenes render unchanged.
2. `dotnet test Njulf.sln` passes.
3. Diagnostics expose zero foliage counts when no foliage exists.

### Phase 1: Foliage Scene Model And CPU Fallback

Tasks:

1. Add `FoliagePrototype` and `FoliagePatch` scene objects.
2. Add `FoliageManager` with CPU-side patch/prototype registration and revision tracking.
3. Implement a debug fallback that converts a small foliage patch into `StaticInstanceBatch`-like object data for validation only.
4. Add a sample scene with grass and one bush/tree prototype.

Acceptance criteria:

1. Small foliage patches render through the fallback.
2. The fallback is explicitly capped and cannot be mistaken for the production dense path.
3. Scene add/remove and revision behavior is covered by tests.

### Phase 2: GPU Buffers And Cluster Generation

Tasks:

1. Add `GPUFoliagePrototype`, `GPUFoliagePatch`, `GPUFoliageCluster`, and `GPUFoliageInstance`.
2. Generate patch clusters on CPU when patch data changes.
3. Upload prototype, patch, and cluster buffers through `FoliageManager`.
4. Register buffers in bindless heap.
5. Add GPU struct layout tests and bindless index tests.

Acceptance criteria:

1. Buffer sizes and offsets match `common.glsl`.
2. Cluster generation is deterministic from patch seed.
3. CPU upload bytes stay zero on camera-only movement.

### Phase 3: Procedural Grass Meshlets

Tasks:

1. Add `foliage_cull.comp` for frustum, distance, LOD, and density culling.
2. Add visible cluster and counter buffers.
3. Add `FoliageCullPass`.
4. Add `foliage_grass.task`, `foliage_grass.mesh`, `foliage_depth.frag`, and first depth/forward pipelines.
5. Draw procedural grass clusters as meshlet-shaped workgroups.

Acceptance criteria:

1. Dense grass renders without per-blade CPU objects.
2. Camera movement does not rebuild scene payloads.
3. Grass depth participates in Hi-Z and opaque occlusion.
4. Debug views can show clusters and LOD bands.

### Phase 4: Authored Meshlet Foliage

Status: Complete as of 2026-06-19. Authored tree canopies and grass clumps are registered as foliage prototypes, culled into `GPUFoliageMeshletDrawCommand` records by `foliage_cull.comp`, and rendered through `foliage_mesh.task` / `foliage_mesh.mesh` in both depth and forward. The default path uses mesh-task indirect dispatch, with an explicit direct mesh-task fallback controlled by `FoliageSettings.IndirectMeshletDispatchEnabled`.

Tasks:

1. Register bush/tree/leaf meshes through `MeshManager` as usual.
2. Add `foliage_mesh.task` and `foliage_mesh.mesh` that read `GPUMeshlet` plus `GPUFoliageInstance`.
3. Emit `GPUFoliageMeshletDrawCommand` records from `foliage_cull.comp`.
4. Bucket commands by prototype/material/LOD where practical.
5. Add direct dispatch fallback and indirect mesh-task dispatch when available.

Acceptance criteria:

1. Bushes and tree canopies render through source meshlets.
2. Meshlet debug coloring works for authored foliage.
3. Draw count scales with visible foliage meshlets, not total patch density.

### Phase 5: Shadows And Motion

Status: Complete as of 2026-06-19. Directional foliage shadows render into the working cascade map through foliage grass/authored meshlet shadow pipelines, using the same foliage mesh shaders as depth/forward so wind and alpha cutoff stay consistent. Grass shadows are distance-clamped by `FoliageSettings.GrassShadowDistance` and density-clamped by `FoliageSettings.GrassShadowDensityScale`. Optional spot/point foliage shadows are available through `FoliageSettings.LocalShadowsEnabled` and are capped by local light, cluster, and meshlet draw budgets. Authored foliage motion vectors use previous view-projection, previous instance buffers, and previous wind time when `FoliageSettings.MotionVectorsEnabled` is enabled; procedural grass remains excluded from motion vectors.

Tasks:

1. Add foliage depth into directional cascades with per-cascade distance and density limits.
2. Add optional spot/point shadow foliage with strict budget caps.
3. Apply the same wind function in shadow and forward shaders.
4. Add previous-frame wind/transform support before foliage writes motion vectors.
5. Exclude grass motion vectors initially if they are too noisy; add a setting.

Acceptance criteria:

1. Trees and shrubs cast stable shadows.
2. Grass shadows can be disabled, distance-clamped, or density-reduced.
3. Wind does not cause depth/forward/shadow mismatch.

### Phase 6: Foliage Lighting Quality

Status: Complete as of 2026-06-19. `MATERIAL_FEATURE_FOLIAGE` is defined in C#, importer feature bits, and GLSL, and it is treated as a non-extension feature so foliage materials do not require unused `GPUMaterialExtensionData`. Foliage forward lighting now uses prototype wrap diffuse/backlight/normal-bend controls, two-sided normal handling, clump-style bent normals for grass/cards, deterministic per-cluster/per-instance color variation, and stable screen-door LOD coverage. Masked/foliage materials are identified for alpha coverage preserving mip policy through upload validation helpers.

Tasks:

1. Add `MATERIAL_FEATURE_FOLIAGE`.
2. Add foliage material parameters in `GPUMaterialExtensionData` only if existing fields cannot represent the needed controls.
3. Implement wrap diffuse/backlighting, double-sided normal handling, bent normals, and per-instance color variation.
4. Add alpha dither fade for LOD/density transitions.
5. Add texture guidance and validation for alpha coverage preserving mips.

Acceptance criteria:

1. Leaves look lit from front and back without becoming emissive.
2. Grass cards do not read as flat planes in normal lighting.
3. LOD/density changes are temporally stable.

### Phase 7: GPU-Driven Indirect Submission

Status: Complete as of 2026-06-19. Foliage culling writes `GPUFoliageDispatchArgs`/`DrawMeshTasksIndirectCommandEXT`-compatible dispatch arguments into the per-frame indirect dispatch buffer, and depth, forward, shadow, and authored motion-vector passes use `CmdDrawMeshTasksIndirect` when `FoliageSettings.IndirectMeshletDispatchEnabled` is enabled. The direct mesh-task fallback remains available and can be toggled at runtime in the sample with Ctrl+F8 for validation. Foliage counters now report emitted meshlet draws and GPU meshlet-draw buffer overflow separately, with total foliage overflow including both CPU generation drops and GPU draw-capacity drops.

Tasks:

1. Add `GPUFoliageDispatchArgs` buffers written by `foliage_cull.comp`.
2. Use mesh-task indirect dispatch when supported.
3. Keep the direct dispatch fallback and report which path is active.
4. Add overflow counters and capacity diagnostics.
5. Add a validation mode that compares GPU counters with CPU fallback on small scenes.

Acceptance criteria:

1. CPU does not need a same-frame visible count readback.
2. GPU counters reconcile candidates, rejected clusters, emitted clusters, and emitted meshlet draws.
3. Overflow degrades by dropping distant/lowest-priority foliage first.

### Phase 8: Far Foliage LOD And Impostors

Status: Complete as of 2026-06-19. Authored foliage prototypes can opt into far impostors through `FoliagePrototype.FarImpostorEnabled`, controlled globally by `FoliageSettings.FarImpostorsEnabled`. GPU foliage culling routes opted-in authored LOD2 clusters away from meshlet command emission and into the existing alpha-tested/depth-writing card pass, which renders a small deterministic crossed-card canopy cloud with the same stable LOD dithering used by foliage lighting. The forest sample tree canopy opts in, diagnostics expose visible far-impostor cluster count and optional impostor atlas bytes, and Ctrl+F9 toggles far impostors at runtime.

Tasks:

1. Add far card/cloud LOD for tree canopies.
2. Add optional impostor atlas assets for tree lines.
3. Crossfade meshlet foliage to impostors with deterministic dithering.
4. Keep impostors alpha-tested and depth-writing where possible.

Acceptance criteria:

1. Forest-scale scenes stay within meshlet and fragment budgets.
2. Distant trees remain visually stable during camera movement.
3. Impostor memory is visible in diagnostics.

### Phase 9: Tooling, Tests, And Budgets

Status: Complete as of 2026-06-19. Foliage coverage now includes settings/defaults/serialization, scene revision and cluster determinism, GPU struct layout, bindless constants, fallback caps, and shader build tests for the foliage cull/depth/forward/motion/task/mesh shaders. Performance scenarios include a dense procedural grass field, authored shrub foliage, mixed tree line foliage, and a matching no-shadow tree line variant, all reachable from smoke arguments and scenario cycling. Performance snapshots now include a compact foliage summary with CPU/GPU timings, visible work counts, memory, overflow, and a likely bottleneck label, and render budget profiles carry explicit foliage cluster, meshlet draw, grass blade, and memory thresholds. Sample controls expose Ctrl+F8 foliage indirect dispatch, Ctrl+F9 far impostors, and Ctrl+F10 foliage debug view diagnostics.

Tasks:

1. Add unit tests for settings, scene revisions, cluster generation, GPU struct sizes, bindless constants, and fallback caps.
2. Add shader build tests for all foliage shaders.
3. Add sample stress scenes:
   - grass field
   - shrubs
   - mixed tree line
   - shadows on/off
4. Extend performance snapshots with foliage counters.
5. Add render budget thresholds for foliage.

Acceptance criteria:

1. A dense foliage sample can be profiled repeatably.
2. Diagnostics identify whether the bottleneck is CPU build, GPU cull, mesh shader, fragment alpha overdraw, shadows, or memory.
3. Existing non-foliage samples remain unchanged.

## Recommended First PR Sequence

1. Settings, diagnostics placeholders, bindless reservations, and tests.
2. Scene model plus small CPU fallback sample.
3. `FoliageManager` buffers and cluster generation.
4. Procedural grass cull/depth/forward pipeline.
5. Authored meshlet foliage pipeline.
6. Shadows and foliage lighting quality.
7. Indirect dispatch and far LOD.

This order keeps the first visible milestone small while preserving the high-performance destination.

## Key Risks

1. Alpha-tested foliage can become fragment-bound before meshlet culling matters. Track overdraw and depth prepass benefit early.
2. Hi-Z self-occlusion can cause popping. Keep foliage Hi-Z independently switchable and conservative.
3. Wind mismatch between passes causes shimmer. Share wind functions and pass previous-frame state deliberately.
4. Indirect mesh-task support can vary by driver. Keep fallback paths explicit.
5. Buffer capacities can hide failures. Every append buffer needs overflow counters and deterministic priority rules.
6. Foliage can dominate shadow costs. Shadow density and distance must be independent quality controls.

## Definition Of Done

The foliage feature is production-ready when:

1. Dense grass, shrubs, and tree canopies render through GPU foliage paths.
2. Camera movement does not rebuild or upload per-instance CPU draw lists.
3. Authored foliage meshes use meshlets.
4. Procedural grass uses meshlet-shaped task/mesh workgroups.
5. Depth, forward, and shadow passes use consistent wind and alpha cutoff.
6. Diagnostics expose candidate, visible, culled, overflow, CPU, GPU, and memory numbers.
7. Quality presets scale density, distance, shadows, and wind without changing code.
8. Tests cover settings, scene revisions, GPU struct layout, bindless constants, shader compilation, and fallback behavior.
