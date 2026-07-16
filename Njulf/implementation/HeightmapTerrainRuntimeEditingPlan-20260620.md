# Heightmap Terrain Runtime Editing Plan

Goal: add a production-ready heightmap terrain system that renders efficiently in the existing Vulkan mesh/task-shader renderer, supports runtime editing, and exposes terrain queries for foliage, gameplay, camera, and future editor tools.

## Research Summary

Recommended starting point:

1. Use a heightmap-as-texture design, not a giant mutable vertex mesh.
2. Render reusable regular grid patches and sample height in the shader.
3. Use CPU quadtree/CDLOD-style node selection first, with min/max height bounds for culling.
4. Upload runtime edits as dirty image rectangles, then regenerate only affected normal/min-max data.
5. Keep a CPU authoritative tile cache for gameplay queries, persistence, collision, and deterministic edit behavior.

Research basis:

1. GPU geometry clipmaps store terrain elevation in single-channel textures, keep vertex/index footprints mostly constant, and update shifted texture regions incrementally.
2. Geometry clipmaps are strongest for very large or infinite landscapes, especially viewer-centered streaming.
3. CDLOD uses a constant-depth quadtree over a heightmap, stores min/max height per node, selects nodes per frame, and morphs between LOD levels in the vertex shader.
4. Vulkan device-local resources should be updated through staging uploads when CPU data changes.
5. Vulkan buffer/image copies are transfer operations and need correct transfer-to-shader synchronization.
6. Vulkan storage images are usable for compute edits and normal generation, but require exact format/layout/barrier handling.
7. This renderer already requires mesh/task shaders, so terrain should use mesh-shader expansion instead of adding a legacy vertex-input path.

Primary references:

1. NVIDIA GPU Gems 2, "Terrain Rendering Using GPU-Based Geometry Clipmaps": https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry
2. Losasso and Hoppe, "Geometry Clipmaps: Terrain Rendering Using Nested Regular Grids": https://hhoppe.com/geomclipmap.pdf
3. Filip Strugar, "Continuous Distance-Dependent Level of Detail for Rendering Heightmaps": https://aggrobird.com/files/cdlod_latest.pdf
4. Vulkan staging buffer documentation: https://docs.vulkan.org/tutorial/latest/04_Vertex_buffers/02_Staging_buffer.html
5. Vulkan copy commands: https://docs.vulkan.org/spec/latest/chapters/copies.html
6. Vulkan storage images and texel buffers: https://docs.vulkan.org/guide/latest/storage_image_and_texel_buffers.html
7. Vulkan sparse resources: https://docs.vulkan.org/guide/latest/sparse_resources.html
8. VK_EXT_mesh_shader proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_mesh_shader.html

## Current Njulf Baseline

The renderer already has the right low-level pieces:

1. `VulkanRenderer` owns a `RenderGraph` with shadow, depth, Hi-Z, light culling, forward, transparent, particle, debug, fog, bloom, tonemap, and AA passes.
2. `MeshManager` stores mesh data in device buffers and uses meshlet-oriented storage buffers instead of fixed vertex-input binding.
3. `FoliageManager` is a good model for a renderer-owned subsystem that builds CPU records, owns GPU buffers, registers bindless resources, tracks content signatures, and exposes diagnostics.
4. `StagingRing` already supports frame-local and overflow staging uploads.
5. `TextureManager` already creates sampled images and registers bindless texture views, but terrain will need writable/updateable height/normal images beyond normal content textures.
6. `RenderSettings`, `SceneRenderingData`, `RendererDiagnostics`, `SampleDiagnosticsReporter`, and `SampleInputController` already have a pattern for runtime feature toggles and debug views.
7. Existing foliage plans already expect terrain queries for height, normal, slope, biome, and exclusion masks.

Implication: add terrain as a first-class renderer subsystem, not as many imported meshes or `RenderObject`s.

## Chosen Architecture

Use finite-world tiled CDLOD first. Keep geometry clipmaps as a later streaming mode.

Reasons:

1. Runtime editing is naturally local. A quadtree with dirty node updates is simpler than clipmap window management.
2. Njulf already has finite sample scenes and foliage placement needs world-space terrain queries.
3. CPU selection over a min/max quadtree is easy to validate, debug, and test.
4. Mesh shaders let us generate regular-grid patch geometry without storing large per-vertex terrain buffers.
5. If large/infinite terrain becomes necessary, the same height/normal/material tile cache can feed a geometry clipmap renderer later.

## Non-Goals For The First Slice

1. Do not implement sparse residency or virtual texturing in the first slice.
2. Do not build a full terrain editor UI. Add runtime-edit APIs and sample hotkeys only.
3. Do not support overhangs, caves, or arbitrary voxel terrain.
4. Do not rewrite the material system before terrain works.
5. Do not require GPU-side brush editing initially. CPU-authoritative edits are simpler, deterministic, and easier to save.
6. Do not make terrain participate in ray tracing yet. Leave resource ownership and bounds suitable for later BLAS/proxy work.

## Core Data Model

New core files:

1. `Njulf.Core/Terrain/HeightmapTerrain.cs`
2. `Njulf.Core/Terrain/TerrainTile.cs`
3. `Njulf.Core/Terrain/TerrainLayer.cs`
4. `Njulf.Core/Terrain/TerrainMaterialLayer.cs`
5. `Njulf.Core/Terrain/TerrainEditStroke.cs`
6. `Njulf.Core/Terrain/TerrainBrush.cs`
7. `Njulf.Core/Terrain/TerrainEditMode.cs`
8. `Njulf.Core/Terrain/TerrainQueryResult.cs`
9. `Njulf.Core/Terrain/ITerrainQuery.cs`
10. `Njulf.Core/Terrain/TerrainChangedEventArgs.cs`

Add to `Scene`:

1. `IReadOnlyList<HeightmapTerrain> Terrains`
2. `Add(HeightmapTerrain terrain)`
3. `Remove(HeightmapTerrain terrain)`
4. terrain disposal ownership matching existing scene-owned disposable behavior

Recommended CPU-facing model:

```csharp
public sealed class HeightmapTerrain : IDisposable
{
    public string Name { get; init; } = "Terrain";
    public TerrainGridDescriptor Grid { get; init; }
    public IReadOnlyList<TerrainMaterialLayer> MaterialLayers { get; }
    public ITerrainQuery Query { get; }
    public ulong Revision { get; private set; }

    public TerrainEditResult ApplyStroke(TerrainEditStroke stroke);
}
```

Terrain grid descriptor:

1. world origin
2. sample spacing
3. width and height in samples
4. height scale and height offset
5. tile size, recommended `129x129` samples for `128x128` quads with shared edges
6. storage format: `R16_UNorm` for normal shipped terrain, `R32_SFloat` allowed for high precision/debug/edit-heavy terrain
7. material map resolution and layer count

## Runtime Resource Model

Add `TerrainManager` under `Njulf.Rendering/Resources`.

Responsibilities:

1. Register terrains from the scene.
2. Maintain a CPU-side terrain content signature and per-terrain revision.
3. Own GPU height, normal, material-weight, and min/max resources.
4. Maintain selected terrain draw records per frame.
5. Upload dirty rectangles through `StagingRing`.
6. Dispatch compute work to update normal maps and min/max hierarchy for dirty regions.
7. Register terrain buffers/images into the bindless heaps.
8. Populate `SceneRenderingData` terrain fields.
9. Expose terrain diagnostics and debug draw data.

Do not overload `TextureManager` with terrain mutability at first. Add a small `TerrainTextureResources` helper that can create images with:

1. `SampledBit`
2. `TransferDstBit`
3. `TransferSrcBit` for debug/readback/export
4. `StorageBit` when compute writes are enabled

Height resource options:

1. First slice: one image per terrain, or one array image for terrains that share dimensions and format.
2. Later: tile atlas or sparse image for very large worlds.

Normal resource:

1. `R16G16B16A16_SNorm` or `R8G8B8A8_SNorm` depending on quality/performance.
2. Regenerate from height in compute for affected dirty rectangles plus a one-sample border.

Material resource:

1. Start with one `R8G8B8A8_UNorm` splat/weight map for up to four layers.
2. Add multiple pages or indexed layers later.

Min/max resource:

1. CPU quadtree stores min/max height per node for selection, culling, and gameplay.
2. Optional GPU min/max buffer mirrors selected nodes for task-shader culling/debug.
3. Recompute only dirty ancestor nodes after edits.

## GPU Data Contract

Add compact structs to `GPUStructs.cs` and matching GLSL declarations in `common.glsl`:

```csharp
public struct GPUTerrainInfo
{
    public Vector4 OriginSpacingHeightScale; // xyz origin, w spacing
    public Vector4 SizeHeightOffset;         // xy samples, z height offset, w max height
    public uint HeightTextureIndex;
    public uint NormalTextureIndex;
    public uint MaterialTextureIndex;
    public uint Flags;
}

public struct GPUTerrainPatch
{
    public uint TerrainIndex;
    public uint LodLevel;
    public uint PatchX;
    public uint PatchY;
    public Vector4 BoundsMinMaxHeight;       // xz origin/range or packed bounds data
    public Vector4 MorphAndUvScaleOffset;
}
```

Add bindless indices:

1. terrain info buffer
2. terrain selected patch buffer frame 0/1
3. terrain counter/debug buffer frame 0/1
4. optional terrain min/max buffer

Keep selected patch buffers double-buffered like foliage and particles.

## Rendering

Add terrain-specific pipelines:

1. `TerrainPipeline`
2. `terrain_depth.task`
3. `terrain_depth.mesh`
4. `terrain_forward.task`
5. `terrain_forward.mesh`
6. `terrain_forward.frag`
7. `terrain_shadow_depth.task`
8. `terrain_shadow_depth.mesh`
9. `terrain_normal_update.comp`
10. `terrain_minmax_update.comp` only if GPU min/max becomes necessary

Pass order:

1. Terrain edit upload/update compute before any pass that samples terrain.
2. Terrain directional shadow before or inside directional shadow pass.
3. Terrain spot/point shadows only after budget controls exist.
4. Terrain depth before `HiZBuildPass`, so terrain contributes to occlusion.
5. Terrain motion vectors only if TAA/motion blur needs terrain movement/edit history.
6. Terrain forward in the opaque stage before skybox and transparent passes.
7. Terrain debug overlay/debug draw after normal debug draw setup.

Practical first render graph changes:

1. Add `TerrainUpdatePass` before shadow/depth rendering.
2. Add `TerrainDepthPass` after `DepthPrePass` or before it; both must happen before `HiZBuildPass`.
3. Add `TerrainForwardPass` near `ForwardPlusPass`.
4. Add terrain drawing into directional shadows once the camera-visible path is stable.

Mesh shader patching:

1. Task shader reads selected `GPUTerrainPatch` records.
2. Mesh shader emits a regular local grid and samples height/normal/material maps.
3. Patch resolution should start at `8x8` quads if device mesh-output limits support 81 vertices and 128 primitives.
4. Add device-limit validation and fallback to a smaller grid if needed.
5. Use CDLOD morphing near LOD range boundaries to hide popping.
6. Use edge morphing or skirts to avoid cracks between neighboring LOD levels.

## LOD And Selection

Initial algorithm:

1. Build a constant-depth quadtree from the heightmap.
2. Store min/max height per node.
3. Every frame, traverse from root using camera position, projected error, and distance bands.
4. Reject nodes against the camera frustum using bounds extruded by min/max height.
5. Emit selected patches into a frame-local CPU list.
6. Upload selected patch records to a device buffer.
7. Render one mesh task per selected patch.

LOD target:

1. Keep near terrain around 1-2 pixels per height sample.
2. Expose quality settings through `RenderSettings.Terrain`.
3. Low: shorter draw distance, coarser near LOD, fewer material layers.
4. Medium/High/Ultra: scale draw distance and patch budget similarly to foliage settings.

Diagnostics:

1. selected patch count
2. rejected patch count
3. LOD0/LOD1/LOD2/etc. selected counts
4. terrain upload bytes
5. dirty rect count and area
6. CPU selection microseconds
7. CPU edit microseconds
8. GPU terrain depth/forward/shadow microseconds
9. normal/minmax update dispatch count
10. terrain texture memory bytes

## Runtime Editing

Editing should be CPU-authoritative in the first implementation.

Flow:

1. Caller creates `TerrainEditStroke`.
2. `HeightmapTerrain.ApplyStroke` modifies CPU tile samples.
3. The terrain records dirty sample rectangles and increments `Revision`.
4. `TerrainManager.PrepareFrame` merges dirty rectangles per terrain/tile.
5. Staging upload copies dirty height rectangles into the terrain height image.
6. A compute pass regenerates affected normal-map rectangles with a one-sample border.
7. CPU updates quadtree min/max for dirty leaves and ancestors.
8. Dependent systems get a terrain-changed event with affected bounds.

Brush modes:

1. Raise/lower
2. Flatten to sampled or explicit height
3. Smooth
4. Noise/roughen
5. Paint material weights
6. Set biome/exclusion mask later

Brush parameters:

1. world position
2. radius
3. strength
4. falloff curve
5. delta time scaling
6. target height/material layer
7. optional mask

Dirty rectangle rules:

1. Merge small adjacent edits within the same frame.
2. Clamp uploads to tile bounds.
3. Add a one-sample border for normal generation.
4. Add a quadtree leaf range for min/max updates.
5. Track upload byte budget and defer excessive edits across frames.

GPU-side editing extension:

1. Later, brush compute shaders can write directly to storage images for very large brushes.
2. If GPU editing is added, maintain CPU consistency through readback, command replay, or CPU-side mirror application.
3. Do not make GPU-only edits the default until persistence and gameplay queries are solved.

## Queries, Collision, And Foliage Integration

`ITerrainQuery` must provide:

1. bilinear height sample
2. normal sample
3. slope
4. material/biome id or material weights
5. world-to-sample conversion
6. raycast against heightfield
7. affected-bounds query for edits

Use cases:

1. camera grounding or editor orbit pivot
2. object placement
3. foliage terrain-aware placement
4. particle collisions later
5. road/water exclusion masks later

Foliage integration:

1. Add terrain placement only after `ITerrainQuery` is stable.
2. `FoliageManager` should cache placement against terrain revision and patch rules.
3. Foliage rebuilds only when terrain edits affect the foliage patch bounds or placement masks.

## Materials

First material slice:

1. Terrain-specific PBR shader path reusing existing material textures where possible.
2. Up to four splat layers from one material weight texture.
3. Triplanar or UV-scaled tiling per layer.
4. Normal blending from detail normals plus geometric terrain normal.
5. Optional slope/height-based auto material only after painted splats work.

Avoid making each terrain layer a normal `RenderObject` material. Terrain needs compact layer arrays and sampling logic.

## Persistence And Asset Pipeline

Add a terrain asset contract:

1. `.njterrain.json` metadata
2. raw or compressed height tiles
3. normal cache optional, regeneratable
4. material weight maps
5. layer material references
6. biome/exclusion masks later
7. edit revision and source provenance

Import/export:

1. import `R16` PNG/TIFF or raw height data first
2. export height and splat maps for debugging
3. save runtime edits as tile deltas or full tile rewrites
4. support autosave only after explicit save/load is deterministic

## Sample Controls

Extend `SampleInputController` only after terrain settings and diagnostics exist.

Suggested controls:

1. toggle terrain debug view
2. cycle edit mode
3. brush radius up/down
4. brush strength up/down
5. apply edit at mouse raycast hit
6. print sampled terrain point
7. reset sample terrain

Do not overload existing foliage/shadow/debug hotkeys without documenting the final key map.

## Debug Views

Add `TerrainDebugView`:

1. none
2. height
3. normal
4. slope
5. LOD level
6. patch bounds
7. quadtree min/max
8. dirty regions
9. material weights
10. terrain query hit point

Integrate with:

1. `RenderSettings`
2. `SceneRenderingData`
3. `RendererDiagnostics`
4. `SampleDiagnosticsReporter`
5. `SampleInputController`
6. performance snapshots

## Implementation Steps

### Step 1: CPU Terrain Contract

1. Add core terrain classes and `Scene.Terrains`.
2. Add CPU tile storage and bilinear sampling.
3. Add edit stroke application for raise/lower, flatten, smooth.
4. Add dirty rectangle tracking and terrain revision.
5. Add tests for sampling, bounds, dirty rect merge, edit modes, and scene add/remove.

Acceptance:

1. A terrain can be created, sampled, edited, and queried without the renderer.
2. Edits produce deterministic sample values and dirty bounds.

### Step 2: Renderer Resource Ownership

1. Add `TerrainManager`.
2. Create height, normal, material, and selected-patch GPU resources.
3. Add bindless registration and lifetime management.
4. Upload full terrain once, then dirty rectangles after edits.
5. Add diagnostics for memory, uploads, and terrain counts.

Acceptance:

1. Terrain resources are created and destroyed validation-clean.
2. Dirty edits upload only affected rectangles.
3. Diagnostics show terrain memory and upload bytes.

### Step 3: First Visible Terrain

1. Add `TerrainPipeline`.
2. Add terrain depth and forward task/mesh/fragment shaders.
3. Render a single LOD fixed grid from the height texture.
4. Add a sample terrain scene.
5. Add screenshot/manual RenderDoc validation notes.

Acceptance:

1. Terrain renders in the camera view.
2. Terrain writes depth before Hi-Z.
3. Material and normal output are stable under camera movement.

### Step 4: CDLOD Selection

1. Build quadtree min/max from CPU height data.
2. Add per-frame selection and frustum culling.
3. Add LOD morphing.
4. Add crack prevention through edge morphing or skirts.
5. Add LOD debug visualization.

Acceptance:

1. Camera movement changes selected patches without popping or cracks.
2. Patch counts stay bounded by quality settings.
3. CPU selection time is tracked and reasonable.

### Step 5: Runtime Editing In Rendered Scene

1. Wire sample input brush controls.
2. Apply edits at terrain raycast hits.
3. Upload dirty height rectangles.
4. Regenerate dirty normal regions.
5. Update quadtree min/max after edits.
6. Visualize dirty regions and updated normals.

Acceptance:

1. Brush edits are visible at runtime without recreating terrain resources.
2. Height queries match rendered terrain after edits.
3. Bounds/culling update after large edits.

### Step 6: Terrain Materials

1. Add terrain material layer descriptors.
2. Add four-layer splat map sampling.
3. Add material weight painting.
4. Add layer diagnostics and debug views.

Acceptance:

1. Terrain supports at least four painted material layers.
2. Runtime material painting updates visible splats without full terrain upload.

### Step 7: Foliage And Gameplay Hooks

1. Implement `ITerrainQuery` adapter used by foliage placement.
2. Add terrain revision checks to foliage placement cache.
3. Rebuild affected foliage patches only when terrain edits overlap them.
4. Add slope/altitude/material placement rules.

Acceptance:

1. Foliage follows edited terrain after affected patch rebuilds.
2. Unaffected foliage does not rebuild.

### Step 8: Persistence

1. Add terrain asset metadata format.
2. Save/load height tiles and material weights.
3. Add import/export for heightmaps.
4. Add regression tests with small known terrain assets.

Acceptance:

1. Runtime edits can be saved, reloaded, and produce matching samples.
2. Missing/corrupt terrain assets fail with useful diagnostics.

## Testing Plan

CPU tests:

1. terrain coordinate conversion
2. bilinear sampling
3. normal calculation
4. brush falloff and edit modes
5. dirty rect merging
6. quadtree min/max propagation
7. CDLOD node selection for fixed camera positions
8. save/load roundtrip

Renderer-side tests where practical:

1. struct layout tests for terrain GPU records
2. render settings serialization tests
3. diagnostics mapping tests
4. shader build tests for terrain shaders
5. resource leak auditor checks for terrain resources

Manual validation:

1. flat terrain renders as flat plane
2. imported heightmap renders with expected scale
3. edit brush updates height and normal
4. LOD debug colors transition smoothly
5. terrain contributes to shadows and Hi-Z
6. RenderDoc capture has expected terrain image layouts and barriers

## Performance Targets

Initial targets on a mid/high desktop GPU:

1. CPU terrain selection under 0.5 ms for sample-scale terrain.
2. Runtime brush edit CPU work under 1 ms for normal interactive brush sizes.
3. Dirty upload area proportional to brush area, not whole terrain.
4. Terrain depth plus forward under 2 ms for sample scene at high quality.
5. No per-frame terrain vertex buffer rebuild.
6. No full normal-map regeneration for small edits.

## Risks

1. Mesh shader output limits vary. Validate terrain patch resolution from device properties and provide fallback.
2. Storage image format support varies. Check format features before enabling compute writes.
3. Terrain cracks are easy to introduce. Add LOD debug views and explicit crack tests.
4. CPU and GPU terrain can diverge if GPU-only edits are added too early. Keep CPU authoritative first.
5. Large terrain memory can grow quickly. Add memory diagnostics before streaming/sparse resources.
6. Shadow rendering can multiply terrain cost. Add directional shadows first and gate local shadows behind budgets.

## First Vertical Slice

Build this first:

1. `HeightmapTerrain` CPU model with edit strokes and queries.
2. One sample terrain added to `NjulfHelloGame`.
3. `TerrainManager` uploads one height texture and one normal texture.
4. One fixed-LOD terrain depth/forward path.
5. Runtime raise/lower brush through `SampleInputController`.
6. Diagnostics for terrain count, upload bytes, dirty rects, selected patches, and pass timings.

This creates a working editable terrain without committing to streaming, sparse residency, or a full editor UI.
