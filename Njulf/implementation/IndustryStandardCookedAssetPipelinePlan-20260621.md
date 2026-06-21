# Industry Standard Cooked Asset Pipeline Plan

Goal: replace runtime asset importing with an offline cooked asset pipeline that uses industry-standard source formats and GPU-ready runtime packages. The runtime should not parse large glTF files, decode/downscale PNG/JPEG textures, build meshlets, classify materials, or validate source assets during startup.

This plan intentionally separates two concepts:

1. **Source/interchange formats**: artist-facing and industry-standard formats such as glTF 2.0/GLB, USD/USDZ, FBX, OBJ, PNG/TGA/EXR, and HDR.
2. **Runtime cooked formats**: Njulf-owned binary packages optimized for fast loading, stable renderer contracts, versioning, streaming, and diagnostics.

The runtime format does not need to be an industry interchange format. Modern engines normally use engine-specific cooked assets at runtime while accepting industry-standard source formats in the asset processor.

## Current Baseline

The project already has useful foundations:

1. `Njulf.AssetTool` validates/imports/reports source assets.
2. `ModelImporter` routes glTF/GLB through SharpGLTF and non-glTF formats through Assimp.
3. `ProcessedMeshAsset` and `ProcessedMeshAssetBuilder` already define part of a preprocessed mesh representation.
4. `TextureManager` supports KTX2 metadata/upload paths, texture budget diagnostics, mip/downscale accounting, and bindless texture registration.
5. `MeshManager` builds GPU mesh buffers and meshlets at runtime.
6. `SampleAssetValidationGate` already enforces a validation report before the sample scene loads assets.
7. The benchmark path shows the main freeze is `Content.LoadInitialScene`, not `SampleInputController`.

The current problem:

1. Runtime still imports glTF/GLB source files.
2. Runtime still decodes/downscales many PNG/JPEG textures.
3. Runtime still converts imported model data into renderer data.
4. Runtime still builds or uploads large meshlet/candidate data during first scene load.
5. The first sample scene load is around 64-67 seconds on the current machine.

## Target Architecture

### Source Asset Layer

Supported source formats:

1. Primary: `.gltf`, `.glb`.
2. Secondary: `.fbx`, `.obj`.
3. Future: `.usd`, `.usda`, `.usdc`, `.usdz`.
4. Texture sources: `.png`, `.jpg`, `.tga`, `.exr`, `.hdr`, `.ktx2`.

Source assets are never loaded directly by shipping runtime scenes unless an explicit development fallback flag is enabled.

### Cooked Asset Layer

Add these runtime formats:

1. `.njmodel`: model package manifest plus mesh/material/animation references.
2. `.njmesh`: GPU-ready mesh payload with vertex streams, index data, meshlets, bounds, LODs, and draw ranges.
3. `.njmat`: renderer-native material metadata and texture bindings.
4. `.njtex`: texture package metadata, normally pointing to or embedding `.ktx2`.
5. `.njanim`: skeleton, skin, and animation clip payloads.
6. `.njscene`: optional baked scene layout, object records, light/probe references, and streaming cells.

Each cooked asset starts with:

1. Magic.
2. Format version.
3. Endianness marker.
4. Build tool version.
5. Source asset hash.
6. Import settings hash.
7. Dependency list hash.
8. Payload table.
9. Compression flags.
10. Diagnostics block offset.

### Runtime Ownership

Runtime loading should become:

1. Read cooked header.
2. Check version/hash compatibility.
3. Map or read binary payload sections.
4. Upload vertex/index/meshlet buffers directly.
5. Register precomputed materials and bindless texture handles.
6. Create `Model`, `RenderObject`, `SkinnedRenderObject`, `FoliagePrototype`, and scene objects without source parsing.

## Format Policy

### Mesh

Cooked mesh data must include:

1. Packed vertex streams in renderer-friendly layout.
2. 16-bit or 32-bit indices, chosen offline.
3. Submesh draw ranges.
4. Material slot indices.
5. Bounds per model, submesh, meshlet, and LOD.
6. Meshlet records, meshlet vertex remap data, and meshlet triangle data.
7. Optional meshopt-compressed payloads for disk size.
8. Optional simplified LODs when the source provides them or the processor generates them.
9. Skinning streams for skinned meshes.

Acceptance:

1. Loading `.njmesh` does not call `ModelImporter`.
2. Loading `.njmesh` does not call `MeshletBuilder`.
3. Runtime mesh registration performs no triangle topology analysis.

### Textures

Cooked texture policy:

1. Convert runtime textures to KTX2.
2. Use BC7 for color/albedo where available.
3. Use BC5 for normal maps.
4. Use BC4/BC5 for masks and scalar maps.
5. Use BC6H for HDR where appropriate.
6. Generate mipmaps offline.
7. Apply max-size/downscale policy offline.
8. Preserve color space and sampler metadata.
9. Keep original source path and source hash in diagnostics.

Acceptance:

1. Runtime sample scene load does not decode PNG/JPEG for cooked assets.
2. `TextureManager` loads cooked textures without runtime downscale.
3. Texture diagnostics still report source path, original size, cooked size, format, mip count, bytes, and compression.

### Materials

Cooked material data must preserve:

1. Alpha mode and cutoff.
2. Double-sided flag.
3. Base color, normal, metallic-roughness, occlusion, emissive.
4. Clearcoat, sheen, anisotropy, transmission, IOR, volume, specular, iridescence, dispersion where supported.
5. Texture transform and UV set.
6. Foliage/decals/transparent material classifications.
7. Renderer pipeline class: opaque, masked, blended, decal, unlit, foliage.

Acceptance:

1. Runtime material registration does not inspect glTF material channels.
2. Material fallback decisions are made offline and serialized.
3. Unsupported source features become cook-time errors or warnings, not runtime surprises.

### Animation

Cooked animation data must include:

1. Skeleton hierarchy.
2. Inverse bind matrices.
3. Skin-to-mesh bindings.
4. Animation clips.
5. Channel target type.
6. Interpolation type.
7. Pre-normalized time/key arrays.
8. Optional quantized tracks for disk size.

Acceptance:

1. Runtime animation load does not require SharpGLTF runtime types.
2. Existing `Animator` behavior remains unchanged for loaded cooked assets.

### Scene And Streaming

Cooked scene data should include:

1. Object records.
2. Static instance batches.
3. Lights.
4. Reflection probes.
5. Foliage patches and prototypes.
6. Optional streaming cell metadata.
7. Dependency list for model/material/texture packages.

Acceptance:

1. Sample scene can load from `.njscene` without hardcoded source model paths.
2. Scene reload can reuse cooked dependencies without reparsing source files.

## Implementation Phases

## Phase 0: Lock Baselines And Scope

1. Keep the current benchmark runner.
2. Capture startup logs for:
   - source glTF sample load
   - one-frame benchmark
   - forest foliage scenario when feasible
3. Add a baseline summary under `Plans/Baselines/CookedAssetPipeline-YYYYMMDD/`.
4. Record:
   - `Content.LoadInitialScene`
   - first-frame CPU draw
   - texture count and bytes
   - meshlet count
   - uploaded bytes
   - staging overflow count

Acceptance:

1. Baselines exist before cooked asset code lands.
2. Regressions can be measured against source-runtime loading.

## Phase 1: Define Cooked Asset Contracts

1. Add `Njulf.Assets.Cooked` namespace.
2. Define immutable records for:
   - `CookedAssetHeader`
   - `CookedAssetManifest`
   - `CookedMeshAsset`
   - `CookedMaterialAsset`
   - `CookedTextureAsset`
   - `CookedAnimationAsset`
   - `CookedSceneAsset`
3. Add binary section identifiers.
4. Add version constants.
5. Add compatibility rules:
   - exact major version match
   - minor version can read older minor versions
   - source hash mismatch is warning in development, failure in strict mode
6. Add tests for header parsing, version rejection, and corrupted section tables.

Acceptance:

1. Contracts compile without renderer dependency cycles.
2. Invalid cooked files fail with clear managed exceptions.

## Phase 2: Binary Serialization Infrastructure

1. Add `CookedAssetWriter`.
2. Add `CookedAssetReader`.
3. Use little-endian binary layout.
4. Store strings in a string table.
5. Store arrays in aligned sections.
6. Add optional per-section compression support, initially `None` only.
7. Add CRC or hash per section.
8. Add round-trip tests for every primitive payload type.

Acceptance:

1. Binary files round-trip deterministically.
2. Reader can skip unknown optional sections.
3. Reader rejects required unknown sections.

## Phase 3: Cook Meshes Offline

1. Extend `Njulf.AssetTool` with:
   - `cook model <source> --out <folder>`
   - `cook folder <source-folder> --out <folder>`
2. Reuse `ModelImporter` for source import.
3. Reuse `ProcessedMeshAssetBuilder` for meshlet generation.
4. Serialize processed meshes to `.njmesh`.
5. Serialize model package metadata to `.njmodel`.
6. Add source dependency hashes.
7. Add a runtime `ContentManager.Load<CookedMeshAsset>`.
8. Add a `ModelRenderUploadService.UploadCookedModel`.

Acceptance:

1. A `.gltf` source can be cooked to `.njmodel` and `.njmesh`.
2. The cooked model loads without invoking `ModelImporter`.
3. Meshlet counts match source runtime import.

## Phase 4: Cook Textures To KTX2

1. Add a texture cooking interface:
   - `ITextureCooker`
   - `TextureCookOptions`
   - `CookedTextureReport`
2. First implementation can write uncompressed KTX2 if BC/Basis tooling is not ready.
3. Add later integration for Basis Universal or another BC/KTX2 encoder.
4. Generate mipmaps offline.
5. Apply max dimension policy offline.
6. Update cooked material texture references to point at cooked texture ids.
7. Add `TextureManager.LoadCookedTexture`.

Acceptance:

1. Cooked sample loads no PNG/JPEG textures at runtime.
2. Downscaled texture count is zero for cooked sample assets.
3. Runtime texture memory matches or beats source-runtime path.

## Phase 5: Cook Materials

1. Convert `ModelMaterial` to `CookedMaterialAsset`.
2. Store renderer classification offline.
3. Store texture binding ids.
4. Store default fallback decisions.
5. Store material extension payloads.
6. Add `MaterialManager.RegisterCookedMaterial`.
7. Add tests comparing source-imported material data to cooked-loaded material data.

Acceptance:

1. Current Sponza and foliage materials produce equivalent GPU material data.
2. Missing texture fallback decisions are identical to source load.

## Phase 6: Cook Animation And Skinning

1. Serialize skeletons, skins, and animation clips to `.njanim`.
2. Preserve skinned mesh relationships in `.njmodel`.
3. Add runtime reconstruction helpers.
4. Add tests using `Strut.glb`.

Acceptance:

1. The animated character sample plays from cooked assets.
2. Joint count, clip count, and playing animator count match source load.

## Phase 7: Cook Scene Layout

1. Add `.njscene` writer.
2. Add scene object records for:
   - render objects
   - skinned render objects
   - static instance batches
   - lights
   - reflection probes
   - foliage patches
   - particle effect references
3. Convert `SampleAssetManifest` to prefer cooked scene path.
4. Keep source manifest fallback behind `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true`.

Acceptance:

1. `NjulfHelloGame` can load the sample from `.njscene`.
2. Source runtime load remains available for development fallback.

## Phase 8: Runtime Integration And Cache

1. Extend `ContentManager` with cooked asset routing.
2. Add cache keys based on cooked asset hash/version.
3. Add renderer upload methods for cooked payloads.
4. Avoid double-copy where possible by reading directly into upload-ready arrays.
5. Add diagnostics:
   - cooked asset count
   - cooked bytes read
   - source fallback count
   - cooked load time
   - cooked upload time
   - version mismatch count

Acceptance:

1. `Content.Load<Model>()` can resolve a cooked model when available.
2. Diagnostics make it obvious whether source or cooked path was used.

## Phase 9: Asset Database And Incremental Cooking

1. Add `.njassetdb` under the output cook folder.
2. Track source path, source hash, import settings hash, dependencies, output files, and last cook status.
3. Re-cook only changed dependencies.
4. Add `Njulf.AssetTool cook changed`.
5. Add `Njulf.AssetTool clean-stale`.

Acceptance:

1. Re-running cook on unchanged sample assets is fast.
2. Changing one source texture cooks only affected cooked assets.

## Phase 10: CI And Editor Workflow

1. Add CI command:
   - `dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out <temp>`
2. Add schema/version tests.
3. Add golden cooked asset tests for tiny fixtures.
4. Add sample smoke using cooked assets.
5. Add failure tests for:
   - old version
   - corrupt section hash
   - missing dependency
   - unsupported source feature
   - source fallback disabled

Acceptance:

1. CI proves cooked assets can be generated and loaded.
2. Runtime source fallback cannot hide a broken cooked pipeline.

## Phase 11: Performance Gate

1. Add benchmark profile:
   - source runtime load
   - cooked runtime load
2. Measure:
   - `Content.LoadInitialScene`
   - first-frame CPU draw
   - first valid GPU frame
   - uploaded bytes
   - texture decode/downscale count
   - staging overflow count
3. Add optional threshold checks:
   - cooked `Content.LoadInitialScene` must be at least 50% faster than source load initially.
   - later target: 80-90% faster than source load.

Acceptance:

1. The current ~64s sample source load has a measured cooked replacement.
2. A regression in cooked load time fails a benchmark gate or emits a clear warning.

## Phase 12: Production Hardening

1. Add compression:
   - meshopt for mesh payloads
   - zstd or LZ4 for metadata sections
   - KTX2/BasisU or BCn for textures
2. Add memory-mapped loading for large packages.
3. Add streaming-cell support for `.njscene`.
4. Add platform-specific texture format selection.
5. Add cooked package signing/hash validation for shipping builds.
6. Add asset migration tooling for older cooked versions.

Acceptance:

1. Cooked assets can be shipped without source assets.
2. Platform-specific output folders can be produced deterministically.
3. Runtime load path is stable and does not depend on source import libraries.

## Migration Strategy

1. Keep source runtime loading until cooked parity is proven.
2. Cook Sponza first, without animation.
3. Cook `Strut.glb` second to validate animation/skinning.
4. Cook grass/foliage third to validate masked materials, foliage prototypes, and texture policies.
5. Add `.njscene` only after model/material/texture/animation assets work independently.
6. Flip `NjulfHelloGame` default to cooked assets only after benchmark evidence.

## Suggested File Layout

Cook output:

```text
Cooked/
  assetdb.njassetdb
  models/
    NewSponza_Main.njmodel
    NewSponza_Main.meshes.njmesh
    Strut.njmodel
    Strut.anim.njanim
  materials/
    NewSponza_Main.materials.njmat
  textures/
    stone_wall_bc7.ktx2
    normal_bc5.ktx2
  scenes/
    SampleScene.njscene
  reports/
    SampleScene.cook-report.json
```

Source output mapping:

```text
NewSponza_Main_glTF_003.gltf -> Cooked/models/NewSponza_Main.njmodel
NewSponza_Curtains_glTF.gltf -> Cooked/models/NewSponza_Curtains.njmodel
Strut.glb -> Cooked/models/Strut.njmodel + Cooked/models/Strut.anim.njanim
textures/*.png -> Cooked/textures/*.ktx2
```

## Open Decisions

1. Whether `.njmodel` embeds mesh/material/animation payloads or references sidecar files.
2. Whether the first texture cooker writes uncompressed KTX2 or integrates a BC/Basis encoder immediately.
3. Whether cooked mesh payloads use current `GPUVertex` layout or a versioned packed vertex layout.
4. Whether scene cooking is sample-specific first or generic from day one.
5. Whether USD support is a near-term source importer or a later DCC interchange phase.

## Recommended First PR

Scope the first implementation to mesh-only cooked loading:

1. Add cooked header/reader/writer.
2. Add `.njmesh` writer for `ProcessedMeshAsset`.
3. Add `.njmesh` reader tests.
4. Add `Njulf.AssetTool cook model`.
5. Add runtime cooked mesh upload path.
6. Benchmark Sponza source mesh conversion vs cooked mesh load.

Do not start with full `.njscene`; it hides too many moving parts. Prove mesh and material parity first, then move textures and scenes.

## Definition Of Done

The cooked asset pipeline is complete when:

1. Sample startup defaults to cooked assets.
2. Source glTF/GLB files are no longer parsed during normal sample startup.
3. PNG/JPEG textures are no longer decoded/downscaled during normal sample startup.
4. Meshlets are not generated during normal sample startup.
5. Startup benchmark shows a large and repeatable improvement.
6. Source fallback remains available for development only.
7. Cook reports are deterministic and CI-validated.
8. Runtime diagnostics clearly distinguish cooked path from source fallback path.
