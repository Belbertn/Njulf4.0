# Njulf Cooked Asset Pipeline Plan (Part 1 of 2)

This is the first of two plans split out of the original `IndustryStandardCookedAssetPipelinePlan-20260621.md`:

- **Part 1 (this plan): the asset cooker** — offline cooking of meshes, textures, materials, and animation into Njulf-owned binary formats, plus the runtime loading path. Goal: eliminate the ~64s `Content.LoadInitialScene` freeze.
- **Part 2 (separate follow-up plan): editor mode** — JSON scene format, stable entity identity, Hexa.NET.ImGui integration, in-game editor overlay with save-to-disk.

The split point: Part 1 keeps the sample scene *code-driven* (as today) but loads cooked assets instead of parsing glTF. The scene file format moves entirely to Part 2, because its primary author is the editor. Consequently the old plan's `.njscene` phase is not in Part 1; cooked binary scenes return in Part 2 / hardening once the JSON source format exists.

What Part 1 does to accommodate the editor (nothing else needed):

1. Keeps the source-import fallback alive (`NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true`) so editor sessions can load un-cooked assets.
2. Gives every cooked package a stable asset id + content hash in its manifest, which Part 2's scene files will reference.
3. Keeps cooking usable as a library (not just CLI) so a later cook-on-save hook is possible.

## Current Baseline (verified against the code)

1. **Nothing of the cooker exists yet**: no `Njulf.Assets.Cooked`, no binary reader/writer, no header/magic/version code, no content hashing utility, no asset DB. The only binary parsing in the repo is `Ktx2Texture.cs` (BinaryPrimitives) and GLB chunk reading in `ModelImporter.ReadGlb`.
2. Import: `ModelImporter` (`Njulf.Assets/ModelImporter.cs`, ~2700 lines) routes `.gltf/.glb` → SharpGLTF (`Gltf/SharpGltfModelMeshConverter.cs`), everything else → Assimp. Produces `ModelMesh` → `ModelSubMesh[]` + `ModelMaterial[]` + `Skeletons/Skins/AnimationClips` + texture types (`ModelTextureSource` with `CacheIdentity`, `ContainerKind`; `ModelTextureSlot` with sampler/color-space/UV transform).
3. Preprocessed mesh representation exists in-memory: `ProcessedMeshAsset` / `ProcessedSubMeshAsset` / `ProcessedMeshAssetBuilder` (`Njulf.Assets/ProcessedMeshAsset.cs`) — vertex streams, indices, meshlets (`Meshlet[]`, `MeshletVertices`, `MeshletTriangles`), draw ranges, bounds, single LOD (`Level 0` only). Consumed by `MeshManager.RegisterProcessedMeshes(...)` (`MeshManager.cs:621`).
4. **Two meshlet builders exist**: asset-side `MeshletBuilder` (`Njulf.Assets/MeshletBuilder.cs`, 64 verts/126 tris, single LOD) and the runtime `MeshManager.BuildMeshletLods` (~line 1460, generates LOD0/1/2 at registration, LOD0 caps 48/64). The sample path (`ModelRenderUploadService.UploadModel` → `RegisterMeshes(generateMeshlets: true)`) uses the **runtime** builder — that is the work to move offline.
5. Textures: `TextureManager` (`Njulf.Rendering/Resources/TextureManager.cs`) decodes PNG/JPEG via StbImageSharp, downscales at runtime (`MaxLoadedTextureDimension` 2048, `DownscaledTextureCount`), loads KTX2 via `Ktx2Texture.Parse` — which **rejects supercompressed/Basis/UASTC** (no transcoder) and 3D/array/cubemap, but uploads BC-format mip chains fine. Bindless via `BindlessHeap`; two-level cache keyed on `CacheIdentity`.
6. Materials: `MaterialManager.RegisterMaterial(GPUMaterialData, extensionData, metadata, textureHandles)` — content-addressed, ref-counted. `GPUMaterialData` contains **runtime bindless texture indices**, so cooked materials must store texture *references*, not final indices.
7. Content: `ContentManager` (`Njulf.Assets/ContentManager.cs`) — in-memory `Dictionary<string,object>` cache only; `Load<T>` supports `ModelMesh`, `MeshletMesh`, `Model` (via `IModelRenderUploadService`), `ProcessedMeshAsset`.
8. Tooling: `Njulf.AssetTool/Program.cs` has `validate` / `import` / `report` / `--child-import` with `--backend/--policy/--timeout-ms` options over `AssetValidator` (`Njulf.Assets/AssetValidation.cs`), which already computes metrics and classifications (Opaque/Masked/Blended/FoliageCandidate/…).
9. Gate & timing: `SampleAssetValidationGate` requires a validation report before loading; `Game.RunStartupStep("Content.LoadInitialScene", ...)` (`Njulf.Core/Game.cs:200`) + `RendererStartupLog` give per-step timings; `SampleBenchmarkRunner` writes JSON reports; baselines live under `Plans/Baselines/`.

## Cooked Formats

| Extension | Contents |
|---|---|
| `.njmodel` | Package manifest: asset id (GUID), source path + hashes, references to mesh/material/anim payloads, sub-object table (names, node/skin indices, per-sub-object material slots) |
| `.njmesh` | GPU-ready mesh payload: vertex streams, indices, meshlet LOD0/1/2 data, draw ranges, bounds |
| `.njmat` | Material table: parameters, feature flags, pipeline classification, texture references |
| `.njtex` | Texture metadata (color space, sampler, source identity) + path of sidecar `.ktx2` |
| `.njanim` | Skeletons, skins (inverse binds), animation clips |
| `.njassetdb` | Cook database (JSON) for incremental cooking |

Every binary file starts with a fixed 64-byte header followed by a section table:

- Header: magic (`u32`, e.g. `NJCA`), asset kind (`u16`), format major/minor (`u16`/`u16`), endianness marker (`u32 0x01020304`), build tool version (`u32`), flags (`u32`), source hash (`u64`), import settings hash (`u64`), dependency list hash (`u64`), section count (`u32`), section table offset (`u64`).
- Section table entry: section id (`u32` FourCC), flags (`u32`: required/optional, compression), offset (`u64`), compressed size (`u64`), uncompressed size (`u64`), content hash (`u64` XxHash3).
- Hashing: add the `System.IO.Hashing` NuGet package (XxHash3/XxHash64) — nothing suitable exists in the repo.
- Compatibility: exact major match required; a reader at minor N loads files ≤ N; unknown *optional* sections are skipped, unknown *required* sections fail; source-hash mismatch is a warning in development, an error in strict mode.

## Implementation Phases

### Phase 1: Cooked Asset Contracts

New folder `Njulf.Assets/Cooked/` (namespace `Njulf.Assets.Cooked` — lives in `Njulf.Assets` so both the tool and the runtime can use it without touching `Njulf.Rendering`).

1. `CookedAssetHeader` (record struct mirroring the header layout above), `CookedSectionEntry`, enums `CookedAssetKind`, `CookedSectionFlags`, `CookedCompression { None, LZ4, Zstd }` (only `None` implemented now).
2. `CookedFormatVersions` static class: one `(Major, Minor)` constant per asset kind, bumped independently.
3. Payload records (plain data, no methods): `CookedModelManifest`, `CookedMeshPayload`, `CookedMaterialTable`, `CookedTextureMeta`, `CookedAnimationPayload`. Model them closely on the existing `ProcessedMeshAsset`/`ModelMaterial` shapes so conversion is mechanical.
4. Exceptions: `CookedAssetFormatException` (bad magic/version/section), `CookedAssetHashException` (section hash or source hash mismatch in strict mode).
5. Tests (`Njulf.Tests/Cooked/`): header round-trip, wrong magic, future major version rejected, older minor accepted, corrupted section table entry rejected, endianness marker checked.

Acceptance: contracts compile with zero references to `Njulf.Rendering`; every invalid-file case throws a typed exception with the file path and reason in the message.

### Phase 2: Binary Serialization Infrastructure

Same folder. Built on `BinaryPrimitives`/`Span<T>` (pattern already used in `Ktx2Texture.cs`).

1. `CookedAssetWriter`: opens a stream, reserves the header, then `WriteSection(id, flags, ReadOnlySpan<byte>)` / `WriteSection<T>(id, flags, ReadOnlySpan<T>) where T : unmanaged`; finalizes by writing the section table + header with all hashes. Sections are 16-byte aligned. Deterministic output: fixed section order per asset kind, zeroed padding.
2. `CookedAssetReader`: parses/validates header + section table, verifies each accessed section's XxHash3, exposes `TryGetSection(id, out ReadOnlyMemory<byte>)` and `ReadSection<T>(id) -> T[]` (single copy from file into the target array; use `RandomAccess.Read` on a `SafeFileHandle` so payloads land directly in upload-ready arrays).
3. String table helper: one `STRT` section per file — offsets + UTF-8 bytes; all other sections reference strings by index.
4. Tests: round-trip every primitive array type (float3 streams, u16/u32 indices, `Meshlet` structs, matrices, strings), skip-unknown-optional-section, reject-unknown-required-section, single-bit corruption in a payload detected by section hash, determinism (cook twice → byte-identical files).

Acceptance: deterministic round-trips proven by tests; reader never reads a section without verifying its hash (unless a `SkipHashValidation` dev flag is set).

### Phase 3: Cook Meshes Offline

The core phase. Two halves: make the runtime's mesh preparation runnable offline, then serialize it.

1. **Extract the runtime meshlet-LOD build.** Move the logic of `MeshManager.BuildMeshletLods`/`GenerateMeshlets` (LOD0/1/2 generation, quality stats, range validation) into a shared class (e.g. `Njulf.Assets/RendererMeshletLodBuilder.cs` or a new `Njulf.Geometry` home) that both `MeshManager` and the cooker call, so cooked meshlets are bit-identical to what the runtime would have built. Extend `ProcessedSubMeshAsset`/`ProcessedMeshLodRange` to carry all three LOD ranges instead of the current single `Level 0`.
2. **Cook the GPU vertex layout offline.** `ModelRenderUploadService.UploadModel` currently builds `GPUVertex[]` at load; the cooker instead writes the final split streams `MeshManager` consumes (`GPUVertexPositionStream`, `GPUVertexNormalTangentStream`, `GPUVertexUvColorStream`, `GPUVertexSkinningData`) as separate sections, plus u16/u32 indices chosen offline (`Uses32BitIndices` rule already exists in `ProcessedIndexLayout`).
3. **`.njmesh` sections** (per model, all submeshes concatenated with a submesh table): `SUBM` (submesh records: offsets/counts, material slot, node/skin index, bind transform, bounds), `VPOS`, `VNRM`, `VUVC`, `VSKN` (optional), `INDX`, `MLT0/MLT1/MLT2` (meshlet records per LOD), `MLVX` (meshlet vertex remap), `MLTR` (meshlet triangles), `DRWR` (draw ranges), `BNDS`.
4. **`.njmodel`** (also binary, small): asset GUID, source path + XxHash3 of source file(s) (a `.gltf` hashes its `.bin` and referenced images too), import settings hash (serialize the effective `ImporterOptions`), sub-object table, references to the `.njmesh` / `.njmat` / `.njanim` files with their content hashes.
5. **AssetTool commands** (extend the existing `args[0]` dispatch in `Njulf.AssetTool/Program.cs`): `cook model <source> --out <folder>`, `cook folder <source-folder> --out <folder>`, reusing `--backend/--policy/--timeout-ms` and the existing child-process isolation for Assimp sources. Cooking flow: `ModelImporter.ImportDetailed` → `ProcessedMeshAssetBuilder.Build` → meshlet LOD build → writer. Emit a per-model `*.cook-report.json` (counts, warnings, timings) using the `AssetValidationJson` style.
6. **Runtime load path**: `ContentManager.Load<Model>` learns cooked routing (details in Phase 7); new `ModelRenderUploadService.UploadCookedModel(CookedModel...)` that feeds `MeshManager` via a new `RegisterCookedMeshes` overload taking the pre-built streams + meshlets directly (bypassing `BuildGpuVertices` and meshlet generation entirely).

Acceptance:

1. `Njulf.AssetTool cook model NewSponza_Main_glTF_003.gltf --out Cooked/` produces `.njmodel` + `.njmesh`.
2. Loading the cooked model invokes neither `ModelImporter` nor any meshlet builder (assert via startup-log step names / debug counters).
3. Meshlet counts, LOD ranges, vertex/index counts, and bounds match the source-import path exactly (test compares both paths on a small fixture; Sponza compared by counts).

### Phase 4: Cook Textures To KTX2

1. New `Njulf.Assets/Cooked/TextureCooker.cs` implementing `ITextureCooker` with `TextureCookOptions` (max dimension, color space, mip filter, target format policy) and `CookedTextureReport` (source path, original/cooked size, format, mip count, bytes).
2. **Stage 1 (this phase): uncompressed KTX2.** Decode sources with StbImageSharp *offline* (same library the runtime uses today), generate the full mip chain offline (box filter; sRGB-aware averaging), apply the max-dimension downscale offline, and write non-supercompressed KTX2 (`R8G8B8A8Srgb/Unorm`) — which `Ktx2Texture.Parse` already loads. Stage 2 (Phase 10) swaps in a BC encoder; **do not** emit BasisU/UASTC supercompression, since the runtime parser rejects it.
3. Texture identity: key cooked textures by the existing `ModelTextureSource.CacheIdentity` so embedded GLB textures, data URIs, and file textures all dedupe; output name = `<sanitized-name>_<hash8>.ktx2` + sidecar `.njtex` (color space, sampler description from `ModelTextureSlot`, source identity/hash, original dimensions).
4. Pass-through: sources that are already `.ktx2` (`ContainerKind.Ktx2`) are copied/validated, not re-encoded.
5. Runtime: `TextureManager.LoadCookedTexture(CookedTextureMeta)` — thin wrapper over the existing `LoadKtx2Texture` path that also records "cooked" provenance in `TextureAssetMemoryEntry` diagnostics; no decode, no runtime downscale.
6. `cook model`/`cook folder` cook all referenced textures and rewrite material texture references to cooked texture ids.

Acceptance:

1. Cooked sample load performs zero StbImageSharp decodes (add a counter/assert) and `DownscaledTextureCount == 0`.
2. Mip counts and dimensions match the runtime-computed values from the baseline (same policy, applied offline).
3. Texture diagnostics still report source path, original size, cooked size, format, mips, bytes.

### Phase 5: Cook Materials

1. Convert `ModelMaterial` → `CookedMaterialTable` entries in `.njmat`: all PBR params + extensions (clearcoat, sheen, anisotropy, transmission/IOR/volume, specular, iridescence, dispersion), alpha mode/cutoff, double-sided, unlit, decal fields, `FeatureFlags`, UV transforms/sets, and **texture references by cooked texture id** (bindless indices stay runtime-assigned).
2. Bake the renderer pipeline classification offline (opaque/masked/blended/decal/unlit/foliage) — reuse the classification logic already in `AssetValidation.Classify()` and whatever `ModelRenderUploadService` currently derives, so the decision is serialized, not recomputed.
3. Serialize fallback decisions (missing texture → default white/normal/black, the same defaults `TextureManager.InitializeDefaultTextures` provides) as explicit flags so runtime doesn't re-infer them.
4. Runtime: `MaterialManager.RegisterCookedMaterial(entry, resolvedTextureHandles)` — builds `GPUMaterialData` from the cooked params + freshly resolved bindless indices, then reuses the existing content-addressed `RegisterMaterial` internals.
5. Tests: import a fixture both ways (source vs cooked) and compare the resulting `GPUMaterialData` (masking out bindless indices), extension data, and metadata field-by-field; do the same as an integration check for Sponza and a foliage material.

Acceptance: Sponza and foliage materials produce equivalent GPU material data; unsupported source features fail at cook time with a clear report entry, never at runtime.

### Phase 6: Cook Animation And Skinning

1. `.njanim` sections: skeleton hierarchy (parent indices + names via string table + local bind TRS), skins (joint→skeleton mapping, inverse bind matrices), clips (per-channel: target node, target type (T/R/S/weights), interpolation, time array, value array — pre-normalized, keeping the exact shapes `Njulf.Core`'s `Animator`/`AnimationClip` consume).
2. `.njmodel` records skinned-submesh ↔ skin bindings (`SkinIndex`, `SkinningBindTransform` already exist on `ProcessedSubMeshAsset`) and the `VSKN` stream from Phase 3 carries joints/weights.
3. Runtime reconstruction helpers build `Skeleton`/`Skin`/`AnimationClip` instances from the payload without SharpGLTF/Assimp types.
4. Optional (flagged, off by default): quantized rotation tracks — defer implementation, reserve the section flag now.
5. Tests: cook `Strut.glb`; compare joint count, clip count/durations/channel counts vs source import; play one clip and compare a sampled pose matrix set within epsilon.

Acceptance: the animated character sample plays from cooked assets with identical joint/clip/animator counts and matching sampled poses.

### Phase 7: Runtime Integration And Cache

1. **Cooked routing in `ContentManager`**: a `CookedContentResolver` maps a requested source path to a cooked package: probe `<contentRoot>/Cooked/models/<name>.njmodel`, verify header + (in dev) source hash. Resolution order: cooked if present and valid → source import if `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true` → error otherwise. Cache key extends the existing `CreateCacheKey` with cooked content hash + format version.
2. `Load<Model>` on a cooked hit: read `.njmodel` manifest → load `.njmesh`/`.njmat`/`.njanim` → `UploadCookedModel` → resolve cooked textures via `LoadCookedTexture`. No `ModelImporter` involvement.
3. **Zero-copy discipline**: mesh sections read directly into the arrays passed to `MeshManager` staging uploads (Phase 2's `ReadSection<T>`); no intermediate managed object graph for bulk payloads.
4. **Diagnostics** (surfaced in `RendererStartupLog` steps + a new block in `RendererDiagnostics`): cooked asset count, cooked bytes read, cooked load ms / upload ms, source-fallback count (with per-asset reasons), version/hash mismatch count.
5. **Sample gate**: extend `SampleAssetValidationGate` so that when cooked assets are used, the gate checks the cook reports (status + hashes) instead of the source validation report; keep the existing behavior for the fallback path.

Acceptance:

1. `Content.Load<Model>()` transparently resolves cooked packages for the unchanged sample code.
2. The startup log makes it unambiguous which path loaded every asset.
3. With fallback disabled and a cooked file deleted, startup fails with a clear error naming the missing package.

### Phase 8: Asset Database And Incremental Cooking

1. `Cooked/assetdb.njassetdb` — JSON (System.Text.Json, `AssetValidationJson` style): per source asset → source hash, import settings hash, tool version, dependency hashes (textures, .bin buffers), output files + their content hashes, last cook status/time.
2. `cook changed` — re-cooks only entries whose source/settings/dependency/tool-version hashes differ; `clean-stale` — deletes outputs no longer referenced by the DB.
3. `cook folder` populates/updates the DB atomically (write-temp + rename) so an aborted cook can't corrupt it.
4. Tests: unchanged re-cook touches nothing (mtimes/hashes stable); touching one texture re-cooks exactly that texture + the materials referencing it; bumping a format version constant forces a full re-cook of that asset kind.

Acceptance: re-running `cook folder` on an unchanged sample completes in seconds; single-texture change cooks only affected outputs.

### Phase 9: CI And Performance Gate

1. CI job: `dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out <temp>` followed by the cooked-asset sample smoke (headless/one-frame benchmark mode).
2. Golden-file tests: tiny fixtures (one triangle mesh, 4×4 texture, one material, one 2-frame clip) with committed cooked outputs; byte-compare on re-cook (determinism gate).
3. Failure-path tests: old major version, corrupt section hash, missing dependency file, unsupported required glTF extension at cook time, fallback disabled + missing cooked file.
4. Benchmark profile comparing source vs cooked: `Content.LoadInitialScene`, first-frame CPU draw, first valid GPU frame, uploaded bytes, decode/downscale counts, staging overflows — reusing `SampleBenchmarkRunner` + `RendererStartupLog`.
5. Threshold gate: cooked `Content.LoadInitialScene` ≥50% faster than the Phase 0 baseline initially; ratchet toward 80–90% once Phase 10 compression lands. Emit a warning/failure through the benchmark analyzer.

Acceptance: CI proves cook + load end-to-end; the ~64s baseline has a measured cooked replacement; a load-time regression fails a gate.

### Phase 10: Production Hardening

1. Compression: meshopt encoding for vertex/index/meshlet sections, LZ4/zstd for metadata sections (the Phase 1/2 flags and section layout already reserve this); BC7/BC5/BC4/BC6H encoding for textures (bc7enc-style encoder or basisu CLI invoked by the cooker), still written as plain (non-supercompressed) KTX2 unless `Ktx2Texture` gains a transcoder.
2. Memory-mapped loading for large packages (swap `RandomAccess` reads for `MemoryMappedFile` views where profiling justifies it).
3. Platform-specific output folders (`Cooked/win-x64/`, …) with per-platform texture format selection.
4. Package signing / whole-file hash validation for shipping builds; strict mode default outside development.
5. Asset migration tooling (`Njulf.AssetTool migrate`) for older cooked versions.
6. Cooked binary scene (`.njscene`) lands here **after** Part 2 ships the JSON scene source format.

Acceptance: cooked assets ship without source assets; deterministic per-platform outputs; runtime has no source-import library dependency in shipping configuration.

## Migration Strategy

1. Keep source runtime loading until cooked parity is proven at each phase.
2. Cook order: Sponza (meshes/textures/materials, no animation) → `Strut.glb` (animation/skinning) → grass/foliage (masked materials, foliage prototypes, texture policy).
3. Flip `NjulfHelloGame` to cooked-by-default only after the Phase 9 benchmark evidence; keep `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD` for development and for Part 2's editor.

## Suggested File Layout

```text
NjulfHelloGame/Cooked/
  assetdb.njassetdb
  models/    NewSponza_Main.njmodel, NewSponza_Main.meshes.njmesh,
             NewSponza_Curtains.njmodel, Strut.njmodel, Strut.anim.njanim
  materials/ NewSponza_Main.materials.njmat
  textures/  stone_wall_a1b2c3d4.ktx2 (+ .njtex), normal_e5f6a7b8.ktx2 (+ .njtex)
  reports/   NewSponza_Main.cook-report.json
```

## Open Decisions

1. `.njmodel` embedding mesh/anim payloads vs sidecar files (plan assumes sidecars, matching the layout above).
2. Where the shared meshlet-LOD builder lives (`Njulf.Assets` forces `Njulf.Rendering` → `Njulf.Assets` reference; alternatively a small new `Njulf.Geometry` project both reference).
3. Whether cooked vertex streams freeze the current `GPUVertex`-derived split-stream layout with a version constant, or introduce a packed/quantized layout now (plan assumes: freeze current layout, version it, pack later).
4. BC encoder choice in Phase 10 (managed bc7enc port vs invoking `basisu`/`toktx` as an external tool).


## Definition Of Done (Part 1)

1. Sample startup defaults to cooked assets; source glTF/GLB parsing, PNG/JPEG decoding, runtime downscaling, and meshlet generation no longer occur during normal startup.
2. Startup benchmark shows a large, repeatable improvement over the Phase 0 baseline, enforced by a gate.
3. Source fallback remains available for development (and Part 2's editor) only.
4. Cook reports and cooked outputs are deterministic and CI-validated.
5. Runtime diagnostics clearly distinguish cooked loads from source fallbacks.

---

**Part 2 preview (separate plan, written after Part 1 is approved):** JSON scene format (`.njscene.json`) + stable entity GUIDs + TRS on `RenderObject` + stable light handles; Hexa.NET.ImGui Vulkan pass + input capture; editor overlay v1 (toggle shortcut, hierarchy, inspector, add/remove, mouse picking via a new `CameraBase` screen-ray + existing `Ray.Intersects`, save-to-disk).