# Meshlet System v2

This document is the shipping contract for the meshlet quality and performance
work introduced at the Model/Mesh 2.0 hard-recook boundary.

## Implemented behavior

| Area | Production contract |
|---|---|
| Cooking | Absolute object-space simplification errors, appearance-aware simplification, locked-border preservation, tight conservative spheres, and conservative normal cones. |
| Profiles | The qualified 48-vertex/64-triangle baseline remains the production default. Two explicit 32–64-triangle flexible cone-clustering candidates remain behind the measured 3% p95 adoption gate and within the packed GPU ABI. |
| GPU ABI | `GPUPackedMeshlet` ABI v2 is 36 bytes. Cooked data stays lossless; runtime upload validates and conservatively quantizes cones. |
| Submission | A 16-byte instance candidate is expanded on GPU into bucketed meshlet work. Opaque, motion, foliage, depth, and shadow paths share the same culling contracts. |
| LOD | Screen-space absolute error, 15% hysteresis, and deterministic eight-frame temporal dithering. Directional shadows use matching source/target cuts; transparent draws rebuild and sort from the current camera. |
| Hierarchy | Bottom-up clusters use leaf groups of 16, fanout 8, maximum depth 12, target ratio 0.5, and stuck threshold 0.85. Runtime traversal is a bounded DFS with a proven 96-entry shared stack and a flat-LOD fallback. |
| Streaming | Independently authenticated 64 KiB pages, 4 KiB disk alignment, per-page Zstd fallback, content-addressed sidecars, complete multipage coarse cuts, bounded parallel reads, 8 MiB/frame upload admission, 4096-page physical budget, retry backoff, priority/LRU selection, and frame-safe retirement. |
| Animation | Every skinned page is pinned. Static LOD0/LOD1 pages are streamable; static LOD2 and hierarchy geometry are pinned. |
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
Backends that support paging open a `MeshletStreamingResidencySession`, provide
an `IMeshletStreamingPageUploader`, and drive `TickAsync` with submission and
completed GPU serials. Failure to open the session is a reported fallback, not
a model-load failure.

## Residency invariants

- Pinned page count must fit the configured physical cache before a session is
  admitted.
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

Settings schema 24 persists these controls. Applying any quality preset
restores the complete production meshlet feature set before its tier-specific
pixel-error budget is selected. Pre-24 files promote hierarchy, streaming
policy, and eight-frame dithering; pre-23 files also promote screen-space error
LOD because Model/Mesh 1.x content is no longer accepted by the v2 runtime.

## Qualification gate

`MeshletSystemQualificationContract` is the release gate, not a benchmark
suggestion. Evidence must come from a clean release build, pin a commit and
artifact SHA-256, include at least three independent runs of 1000 measured
frames, and prove all correctness counters are zero. Visual/reference, shadow,
transparent-order, dither, skinned-residency, ray-proxy, and full-resident
fallback checks are mandatory.

The performance-qualified target is **NVIDIA GeForce RTX 3060 Laptop GPU**.
Its candidate p95 CPU and GPU frame times may regress by no more than 2%, warm
page-cache hit rate must be at least 90%, peak physical pages must remain within
256 MiB, and page uploads must remain within 8 MiB per frame.

AMD integrated GPUs run the identical correctness gate but return
`CorrectnessOnly`; they cannot produce a production-performance qualification.
Other devices are outside this frozen qualification matrix until a separately
reviewed device rule is added.

Automated tests validate serialization, hierarchy coverage and boundedness,
packed ABI layout, shader permutations, multipage coarse fallback completeness,
sidecar corruption handling, retry/eviction/retirement, settings migration,
and qualification classification. Hardware performance status must still be
backed by captured device evidence; a passing unit suite does not manufacture
that evidence.
