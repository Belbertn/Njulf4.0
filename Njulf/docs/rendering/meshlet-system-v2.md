# Meshlet System v2

This document is the shipping contract for the meshlet quality and performance
work introduced at the Model/Mesh 2.0 hard-recook boundary.

## Implemented behavior

| Area | Production contract |
|---|---|
| Cooking | Absolute object-space simplification errors, appearance-aware simplification, locked-border preservation, tight conservative spheres, and conservative normal cones. |
| Profiles | The portable 48-vertex/64-triangle baseline is the production default. Two explicit 32–64-triangle cone-clustering candidates and one connected 64-vertex/126-triangle candidate remain selectable without vendor checks. |
| GPU ABI | `GPUPackedMeshlet` ABI v2 is 36 bytes. Cooked data stays lossless; runtime upload validates and conservatively quantizes cones. |
| Submission | Exact compute-compacted streams dispatch taskless mesh shaders for opaque, transparent, motion, foliage, depth, and shadow work. Task shaders remain an explicit compatibility/validation mode only. |
| LOD | Screen-space absolute error, 15% hysteresis, and deterministic eight-frame temporal dithering. Directional shadows use matching source/target cuts; transparent draws rebuild and sort from the current camera. |
| Hierarchy | Bottom-up clusters use leaf groups of 16, fanout 8, maximum depth 12, target ratio 0.5, and stuck threshold 0.85. Runtime traversal is a bounded DFS with a proven 96-entry shared stack and a flat-LOD fallback. |
| Streaming | Renderer-wide software-managed physical residency uses authenticated 64 KiB pages, lazily committed 64 MiB GPU banks, complete coarse cuts, bounded parallel reads, 8 MiB/frame uploads, a 4096-page default budget, retry backoff, global priority/LRU selection, and frame-safe retirement. It does not require Vulkan sparse binding. |
| Animation | Eligible static meshes may page LOD0/LOD1 while LOD2 and hierarchy fallback data remain pinned. Skinned, runtime-imported, small, corrupt, or non-beneficial meshes remain fully resident. |
| Ray queries | Static BLAS input may use the conservative LOD2 triangle proxy. Skinned geometry remains resident and exact. Proxy use is explicitly tagged in ray-scene diagnostics. |
| Failure behavior | A missing, corrupt, incompatible, or over-budget page sidecar fails closed to the ordinary full-resident cooked payload. A page is never published before its transfer completion serial. |

## Cooked package contract

Every v2 mesh package contains the required `MSPM` manifest and an immutable
sidecar named from the exact stored page bytes:

```text
asset.njmesh
asset.njmesh.meshlets-<128-bit-content-id>.pages
```

The sidecar contains a 64-byte header, a fixed 64-byte authenticated index
record per page, and 4 KiB-aligned page payloads. The main package manifest
pins each page's decoded size, CRC32, xxHash64, compression, semantic LOD,
submesh, logical meshlet range, and complete coarse fallback page group.

Writing publishes the content-addressed sidecar before atomically replacing the
main package. If main-package publication fails, the previous generation still
references its unchanged sidecar. Tree and single-file migration both preserve
and revalidate the sidecar. Model and Mesh 1.x content is not migrated because
its error and hierarchy semantics cannot be reconstructed; it must be recooked.

The main package deliberately retains the full-resident streams. They are the
correctness fallback and allow older rendering backends to consume v2 content.
The renderer preflights eligible static submeshes as a package cohort, admits a
paged session only when pinned/coarse data fits and exact committed bytes are
lower than the full-resident upload, then shares physical slots and I/O budgets
through one global coordinator. Activated submeshes do not also upload duplicate
full meshlet/local-index streams. Any preflight or bootstrap failure rolls the
cohort back transactionally to the ordinary full-resident upload.

Paging is adaptive, not a hardware-qualified feature switch. Existing schema-1
`.pages` sidecars are sufficient and do not require recooking. Missing, corrupt,
skinned, over-budget, or savings-negative content reports its concrete fallback
reason and remains fully resident.

## Residency invariants

- Global pinned pages plus the largest selectable fine range must fit the
  configured physical cache before a session is admitted.
- A static fine page is usable only when it is resident. Otherwise the entire
  pinned coarsest LOD page group must be resident; partial coarse geometry is
  never returned as a valid resolution.
- Page reads are authenticated and structurally decoded before upload begins.
- GPU page-table publication occurs only after the upload ticket completes.
- Eviction first unpublishes the mapping. Its slot is not reusable until the
  retirement serial has completed for every in-flight frame.
- Pinned pages cannot be evicted. A higher-priority request may replace only an
  older lower-priority streamable page, preventing same-frame oscillation.
- New demand is capped at 4096 unique pages per serial. Excess demand resolves
  through the coarse group and is counted instead of allocating unbounded work.

Default controls live under `RenderSettings.SceneSubmission`:

- `GpuMeshletStreamingEnabled = true`
- `GpuMeshletStreamingPhysicalPageCount = 4096` (256 MiB)
- `GpuMeshletStreamingUploadBudgetMiB = 8`
- `GpuMeshletStreamingMaximumRequestsPerFrame = 4096`
- `GpuMeshletStreamingConcurrentReads = 4`

`RenderSettings.Raster.MeshShaderTuningMode = Auto` selects taskless
48v/64p/64-thread shaders by default and widens only when loaded content needs
the 64v/126p contract. All four taskless size/workgroup permutations and the
task-stage compatibility path remain explicit test modes on every device whose
reported Vulkan limits satisfy the selected contract.

Settings schema 26 persists these controls and the combined renderer activation
contracts. Applying any quality preset
restores the complete production meshlet feature set before its tier-specific
pixel-error budget is selected. Pre-24 files promote hierarchy, streaming
policy, and eight-frame dithering; pre-23 files also promote screen-space error
LOD because Model/Mesh 1.x content is no longer accepted by the v2 runtime.

## Validation and profiling

Taskless submission and adaptive physical residency are enabled without a
vendor qualification manifest. Runtime Vulkan limits, allocation limits,
resource validity, and content correctness remain authoritative fallbacks.
Optional clean Release captures can still compare the selectable clustering
and workgroup candidates; changing the production cooking profile should retain
the existing 3% p95 improvement and 2% regression gates.

Automated tests validate serialization, hierarchy coverage and boundedness,
packed ABI layout, shader permutations, multipage coarse fallback completeness,
sidecar corruption handling, retry/eviction/retirement, settings migration,
global-bank admission, adaptive byte accounting, and full-resident rollback.
