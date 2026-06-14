# Renderer Architecture Future Changes - 2026-06-14

Purpose: track only future implementation, validation, migration, and cleanup work discovered while executing RendererArchitecture plans 01-07.

Rules:
- Use this file for deferred or cross-plan work from every renderer architecture plan.
- Do not record completed implementation status here.
- Move an item out of this file, or delete it, only when the replacement is implemented, verified, and any old path is safely removed.
- Keep plan documents as the phase source of truth; keep this file as the future-work queue.

## Plan 01 - Render Graph Ownership

- [ ] Delete the remaining graph-target adapter layout calls (`TransitionGraphTarget` / `SetKnownLayout`) after every intra-pass substep is represented as graph-declared work or pass-local subresource barriers.
- [ ] Add strict runtime validation for undeclared graph resource access during development.
- [ ] Delete remaining old compatibility/adaptor APIs after RenderDoc layout validation confirms graph-generated barriers and graph-owned allocation cover all production targets.
- [ ] Validate graph ownership with resize and dynamic-resolution tests, AO/AA/bloom/fog/shadow toggles, RenderDoc layout inspection, memory snapshots, timestamp comparison, and visual parity.

## Plan 02 - GPU Scene

- [ ] Make GPU scene invalid mesh/material data fail hard in production once all current sample/import paths provide valid renderer handles; the initial mirror matches the existing `SceneDataBuilder` skip/default behavior to avoid deleting the verified renderer path prematurely.
- [ ] Add in-flight range hazard assertions backed by real frame fences for GPU scene buffer updates; current uploads go through staging/copy, but explicit range/fence validation remains to be wired.
- [ ] Add RenderDoc inspection and performance validation proving GPU scene upload bytes scale with changed objects in the sample scene.

## Plan 03 - GPU Driven Visibility

- [ ] Add broader visual/performance validation across stress scenes for Hi-Z occlusion, shadow specialization, transparent weighted OIT, and visibility buffer growth.

## Plan 04 - Offline Asset Pipeline

- [ ] Integrate `ProcessedMeshAsset` loading into `ModelImporter`, content cache invalidation, and `ModelRenderUploadService` so runtime consumes processed LOD/meshlet ranges instead of building them during normal load.
- [ ] Implement authored LOD discovery from glTF node names/extras and project metadata, including material remap and skinning validation.
- [ ] Implement deterministic generated fallback LOD simplification with geometric/UV/normal/material-seam quality metrics.
- [ ] Build meshlets per processed LOD offline or in explicit development rebuild mode, then remove production runtime meshlet generation where processed assets are present.
- [ ] Implement foliage batching runtime backed by GPU scene buffers and cluster/instance metadata.
- [ ] Implement real impostor atlas generation/import, impostor material/shader rendering, transitions, and validation captures.

## Plan 05 - Visibility First Frame

- [ ] Wire `VisibilityFirstFramePlanner` audit output into renderer diagnostics/performance snapshots.
- [ ] Move GPU scene dirty upload, GPU skinning, object visibility, LOD selection, depth, Hi-Z, occlusion refinement, meshlet compaction, and light culling into the target frame shape after Plan 03 compute passes exist.
- [ ] Make optional zero-work pass skipping consume GPU-generated counts instead of old CPU scene-data counts.
- [ ] Remove duplicate CPU culling/order workarounds once the visibility-first graph is authoritative.

## Plan 06 - Async Compute

- [ ] Expose actual compute queue family selection in `VulkanContext`; current context selects graphics and transfer queues but not a dedicated compute queue.
- [ ] Extend render graph pass declarations with async eligibility/workload hints and feed them into `AsyncComputeScheduler`.
- [ ] Implement per-queue command pools/buffers, timeline semaphore or binary fallback submission, and frame fences covering all submitted queues.
- [ ] Generate live queue-family ownership release/acquire barriers from `QueueSyncEdge` and resource usage plans.
- [ ] Move candidate passes to async execution only after profiling proves benefit, then remove any manual or duplicate queue scheduling paths.

## Plan 07 - Diagnostics First Class

- [ ] Feed `VisibilityFirstFramePlanner` audit output and async schedule decisions into `RendererDiagnosticsSchema` once those systems are live production paths.
- [ ] Add queue-specific GPU timing categories for graphics, compute, and transfer queues after async compute submission is implemented.
- [ ] Replace estimate-only memory metrics with VMA allocation categories for GPU scene buffers, visibility/compaction buffers, graph transient images, persistent history images, shadows, particles, and debug tooling where actual allocation data is available.
- [ ] Export render graph culled-pass reasons, resource lifetime intervals, alias groups, generated barrier counts, and DOT graph output from compiled production graph snapshots.
- [ ] Implement real visual overlay render passes for pass timing bars, memory categories, upload pressure, queue overlap, light tiles, shadows, bounds, selected LODs, foliage clusters, and impostor selections.
- [ ] Add deterministic stress scenes that export snapshots for static objects, skinned objects, foliage, impostors, lights, shadows, transparency/OIT, particles, and post-processing/dynamic resolution.
- [ ] Remove old flat or ambiguous diagnostics fields only after structured category consumers and overlays are verified against real renderer data.
