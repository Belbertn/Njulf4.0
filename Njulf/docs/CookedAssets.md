# Cooked asset pipeline

Njulf resolves versioned, hashed cooked packages before source models. Cooked startup bypasses SharpGLTF/Assimp import, source-image decoding, runtime downscaling, GPU vertex construction, and runtime meshlet generation. The `Development` build falls back to source import when a package is missing or stale; other configurations remain cooked-only by default.

## Cook assets

```powershell
dotnet run --project Njulf.AssetTool -- cook model NjulfHelloGame/Strut.glb --out NjulfHelloGame/Cooked
./tools/cook-bistro.ps1 -Configuration Development
dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out NjulfHelloGame/Cooked
dotnet run --project Njulf.AssetTool -- cook changed NjulfHelloGame --out NjulfHelloGame/Cooked
dotnet run --project Njulf.AssetTool -- clean-stale --out NjulfHelloGame/Cooked
```

The default meshlet build profile is `portable-48v-64t`. Controlled recooks can
select `portable-flex-48v-32-64t-cone025-split2`,
`portable-flex-48v-32-64t-cone050-split2`, or `connected-64v-126t` with
`--meshlet-build-profile <id>`. The selected ID and parameters participate in
cook identity and compatibility hashing, so profiles are never silently mixed
within one cooked mesh.

`Cooked/` is generated output and is intentionally not version-controlled. After
a fresh clone, run the folder cook before starting the normal cooked-only runtime
or producing a Release build or publish.

Amazon Bistro must use the shared `cook-bistro.ps1` workflow. It force-cooks both
the exterior and interior for `win-x64` with Assimp, the explicit
`AmazonBistro` material-texture convention, AutoBC texture selection, and full
offline mip chains. It then runs the Bistro cook contract/material tests and
rebuilds the game so every regenerated package and KTX2 is copied to the runtime
output. The generic folder-cook default is `Standard` and cannot infer Bistro's
glass and packed-map semantics. Source-path loads compare a stable
import-semantic contract and reject mismatched packages. An explicitly requested
`.njmodel` remains authoritative.

The cooker defaults to the host RID and writes `Cooked/<rid>/`. Override it with `--platform win-x64`, `linux-x64`, or another supported desktop RID. `cook changed` skips an asset only when its source, effective settings, dependencies, tool version, platform, and every recorded output hash are unchanged. Package and database writes are atomic. Pass `--force` to rebuild.

### Cook progress and bounded folder work

Cook commands emit newline-delimited progress records to `stderr`; the existing
per-asset summaries remain on `stdout`. This makes redirected CI and agent logs
as useful as an interactive terminal. The default is human-readable `plain`
records with material/texture detail:

```powershell
dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out NjulfHelloGame/Cooked --progress plain --progress-detail items
```

- `--progress plain|jsonl|off` selects plain text, one JSON object per line,
  or suppresses progress records. `plain` is the default.
- `--progress-detail stages|items` limits output to major stages or includes
  material/texture activity. `items` is the default.
- Progress always goes to `stderr`, is flushed per record, uses no cursor
  control, and emits a heartbeat after ten seconds without another event.
- `--jobs <count|auto>` enables worker-local folder cooks; `1` remains the
  default. `--max-inflight-bytes <bytes>` bounds source-byte admission, with a
  single larger source admitted exclusively instead of starving forever.
- Ctrl+C stops new work cooperatively, rolls back unpublished generation files,
  retains completed asset database checkpoints, and returns exit code `130`.

JSONL records include schema `1`, a process-local `runId`, monotonically
increasing `sequence`, stable event/stage/outcome names, and separate
`stageElapsedMs`, `itemElapsedMs`, `assetElapsedMs`, and `totalElapsedMs`
fields. Progress configuration is diagnostic only: it is not included in cook
identity, reports, or the asset database.

Model/Mesh 2.0 payloads contain renderer-ready streams plus deterministic
meshlet LOD0/1/2, absolute object-space simplification errors,
appearance-aware simplification, conservative normal cones, bottom-up
hierarchy nodes, and a conservative static ray-query proxy. Each mesh package
owns a content-addressed, independently authenticated 64 KiB page sidecar.
Model/Mesh 1.x is a hard recook boundary because its quality metadata cannot be
reinterpreted safely. Win-x64 and linux-x64 use meshoptimizer encoding; other
RIDs use zstd until a compatible native decoder is available. See
[the Meshlet System v2 contract](rendering/meshlet-system-v2.md) for the full
runtime and residency invariants.

Textures default to semantic BC formats in plain, non-supercompressed KTX2 containers: BC7 for color/data, BC5 for normal maps, BC4 for scalar maps, and BC6H for HDR. Full mip chains and color-correct filtering are generated offline. macOS currently selects RGBA8 pending an ASTC target profile. Use `--texture-format rgba8` when an uncompressed diagnostic output is useful.

## C1 opacity-micromap fixture

The all-on GI fixture uses a real alpha-masked grass asset and must not source-
fallback during qualification. Run `tools/cook-gi-all-on-c1.ps1` to force a
SharpGLTF RGBA8 cook with sampler anisotropy fixed to 1, the reviewed native
OMM bridge, and its provenance record. The script then calls
`Njulf.AssetTool advanced-gi verify-c1-model` and rejects a missing, rejected,
empty, or non-four-state optional section. Maximum sampler anisotropy is part
of the importer options and therefore the cook settings identity.

The NVIDIA OMM 1.9.2 CPU API treats a nonzero mip `rowPitch` as a texel count,
not a byte count. The pinned bridge supplies the canonical zero sentinel for
tightly packed FP32 alpha data; using `width * sizeof(float)` would stride four
times too far and read outside the pinned texture after row 255 of a 1024-wide
image.

The output layout is:

```text
Cooked/
  win-x64/
    assetdb.njassetdb
    models/*.njmodel, *.njmesh, *.njmesh.meshlets-*.pages, *.njanim
    materials/*.njmat
    textures/*.ktx2, *.njtex
    reports/*.cook-report.json
```

## Explicit material classification

Material names are not cook-time semantics. Use glTF material extras to opt a
material into foliage shading:

```json
"extras": {
  "NJULF_foliage": true
}
```

`NJULF_foliage` is preserved in the cooked material data and controls foliage
shading only. Alpha-coverage-preserving mipmaps are controlled separately by
standard glTF `alphaMode: "MASK"`; opaque foliage such as a tree trunk uses
ordinary mipmaps.

## Offline foliage impostors

Capture equal-sized source views offline as albedo/opacity, normal, and
conservative depth/thickness RGBA images, then pack them deterministically:

```powershell
dotnet run --project Njulf.AssetTool -- foliage-impostor bake tree-views.json --out Assets/Foliage/Impostors --name oak
```

The manifest supplies `name`, `sourceBounds`, `pivot`, `scale`, and 1–64 views;
each view contains a non-zero `direction` plus `albedoOpacity`, `normal`, and
`depth` image paths. The command validates dimensions and bounds, packs all
three atlases into the same bounded grid, writes content-addressed PNGs
atomically, and emits `<name>.foliage-impostor.json` with normalized atlas
rectangles and a canonical SHA-256. That metadata matches the scene foliage
impostor contract and is authored onto the prototype. Runtime baking is never
performed; missing or invalid metadata falls back to the complete coarsest
authored LOD.

## Runtime policy

`ContentManager.Load<Model>` probes `Cooked/<current-rid>/models/<source-name>.njmodel`, then the legacy non-RID layout for migration compatibility. Validation remains strict in every configuration. `Development` permits source fallback by default so the editor remains usable when source assets are newer than their cooked packages; all other configurations default to cooked-only.

Individual model requests can set `ContentLoadOptions.RequireCooked`. This
overrides the Development fallback policy for synchronous, asynchronous, and
preload paths. A missing package raises `FileNotFoundException`; an invalid
package or import contract raises `InvalidDataException`. Both diagnostics retain
the resolver reason and provide an exact AssetTool recook command. The sample
Bistro exterior and deferred interior both use this per-request policy.

- `NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true|false` explicitly overrides the build default for source import.
- `NJULF_COOKED_ASSET_STRICT=false` relaxes whole-file/source validation for diagnosis only.
- `NJULF_COOKED_ASSET_REQUIRE_SIGNATURE=true` enables detached-signature enforcement for shipping deployments.
- `NJULF_COOKED_ASSET_PUBLIC_KEY=<path-or-PEM>` supplies the trusted ECDSA public key.

`ContentManager.CookedDiagnostics` reports the selected path, bytes read, load/upload timings, fallback reasons, and mismatch counts. `TextureManager.RuntimeDecodedTextureCount`, `CookedTextureLoadCount`, and `DownscaledTextureCount` distinguish cooked loads from source decoding.

For hosts that own a renderer upload queue, register an
`IContentUploadDispatcher` and resolve `IAsyncContentManager`. `LoadAsync<T>`
can then decode immutable cooked sidecars on CPU work while dispatching only
GPU upload/publication to the host-approved context. `PreloadAsync<T>` accepts
prioritized requests plus bounded concurrency and byte-admission options;
completed assets remain manager-owned if another request fails or is cancelled.
Without a dispatcher, `LoadAsync<T>` intentionally uses the synchronous path
instead of performing renderer work on a thread-pool thread.

Release sample publishing sets `CookedAssetsOnly=true`: source model/image files and source-import runtime assemblies are not copied. Debug builds retain the importer for the explicit fallback/editor path.

## Signing and migration

Generate a key pair once and keep the private key outside source control:

```powershell
dotnet run --project Njulf.AssetTool -- keygen --private keys/cooked-private.pem --public keys/cooked-public.pem
dotnet run --project Njulf.AssetTool -- cook folder NjulfHelloGame --out NjulfHelloGame/Cooked --signing-key keys/cooked-private.pem
```

Every binary package and KTX2 sidecar gets a detached ECDSA P-256/SHA-256 `.sig`. Runtime signature verification covers the complete file, while package references and section hashes protect the internal graph and decoded contents.

Upgrade older compatible package versions atomically:

```powershell
dotnet run --project Njulf.AssetTool -- migrate NjulfHelloGame/Cooked --out NjulfHelloGame/Cooked-Migrated --signing-key keys/cooked-private.pem
```

Omit `--out` for an in-place migration. Major-version incompatibilities require a source re-cook.

## Format and I/O guarantees

Every binary has a fixed 64-byte header and a 40-byte section table. Readers reject incompatible/future versions, wrong endianness, malformed ranges, unknown required sections, unsupported compression, corrupt XxHash3 section hashes, and invalid dependency hashes. Large packages use memory-mapped views; smaller payloads use direct `RandomAccess.Read` into typed arrays. Metadata uses zstd, with LZ4 available for latency-sensitive sections.
