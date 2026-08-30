# Default Adaptive Physical Meshlet Page Residency Implementation Plan

Status: Proposed  
Date: 2026-08-29

## Goal

Implement physical residency as a renderer-wide, software-managed GPU page cache
rather than Vulkan sparse-memory binding. `GpuMeshletStreamingEnabled` remains
enabled by default and gains real adaptive behavior: eligible cooked static
meshes use paging only when it reduces committed VRAM; unsupported, small,
skinned, corrupt, or over-budget meshes retain the existing full-resident path.

Existing schema-1 `.pages` sidecars contain everything required, so no recook or
cooked-format migration is needed.

## Implementation Changes

### Global residency and GPU storage

1. Add one renderer-owned residency coordinator shared by all models. Refactor
   the existing residency policy into a global engine so page IDs, physical
   slots, I/O budgets, eviction, retries, and priorities operate across packages
   without slot collisions.
2. Use 64 KiB physical pages in lazily allocated 64 MiB device-local buffer
   banks. Reserve 16 stable bindless bank indices, supporting the current 1 GiB
   maximum; the default 4,096-page limit commits at most four banks/256 MiB.
3. Allocate banks through the existing mesh-buffer memory-budget path. Reject
   new paged registrations if allocation would exceed the configured capacity
   or Vulkan memory budget; keep already-active packages functioning.
4. Define a fixed GPU page format containing a header, packed meshlet records,
   local vertex indices, and local triangle indices. Repack decoded sidecar pages
   on workers, rebase global vertex offsets, and reject any page that cannot fit
   exactly within 64 KiB.
5. Maintain two fence-safe page-table and range-readiness copies, matching frames
   in flight. Never reuse an evicted slot until every frame that could reference
   its previous mapping has completed.
6. Use the high bit of a meshlet address to identify a virtual meshlet; retain
   existing direct addresses below that bit. An immutable virtual table maps
   virtual meshlets to global page IDs and page-local records. Keep hierarchy
   nodes permanently resident in the existing record buffer while rebasing
   their geometry references to virtual addresses.
7. Grow virtual tables transactionally, publish replacement descriptors only
   after upload completion, and defer destruction of old buffers through the
   existing fence deleter.

### Adaptive model activation

During both immediate and cooperative cooked-model uploads, preflight each
package before publishing any mesh handles. Activate paging only when all of the
following conditions hold:

1. Streaming is enabled for the new load.
2. The sidecar and manifest are valid and match the cooked mesh.
3. The submesh is static and has streamable pages.
4. All pinned coarse-LOD and hierarchy pages authenticate, decode, and repack
   successfully.
5. Global pinned pages plus the largest selectable fine-LOD group fit the
   configured cache.
6. Exact full-resident bytes avoided exceed incremental physical-bank commitment
   plus page-table, virtual-table, range-table, and resident-hierarchy overhead.

Apply the following lifecycle rules:

1. Evaluate eligible static submeshes as a package cohort so shared bank
   commitment is counted once. Page only candidates that contribute positive
   savings; mixed models retain skinned or ineligible submeshes as full resident.
2. Bootstrap pinned pages through the normal asynchronous frame upload path
   before publishing the model. Include these uploads in the configured
   per-frame budget and never wait for disk I/O on the render thread.
3. Do not upload duplicate full meshlet descriptors or local-index buffers for
   an activated paged submesh. Retain CPU meshlet data needed for culling,
   sorting, validation, and ray-proxy construction.
4. If preflight, allocation, pinned I/O, packing, or GPU publication fails, roll
   back all residency allocations and upload through the existing full-resident
   transaction. Later fine-page failures retain pinned coarse geometry, retry
   with backoff, and never invalidate the model.
5. Reference-count package sessions across model instances. On final unload,
   cancel reads, invalidate demand keys, clear future page tables, retire
   physical slots after fences, close the sidecar, and release empty trailing
   banks when safe.

### Demand generation, LOD correctness, and shaders

1. Add an immutable streaming-range table for each paged mesh. Flat LODs emit a
   deduplicated range-demand key; hierarchy traversal can emit singleton-page
   demands. CPU processing expands each demand into page requests without
   requiring large page-list loops per visible instance.
2. Provide persistent GPU demand stamps and a per-frame append buffer capped by
   `GpuMeshletStreamingMaximumRequestsPerFrame`. Copy only the count, overflow
   counters, and unique keys to host-visible readback memory.
3. Update opaque compaction, foliage culling, hierarchy traversal, transparent
   rendering, local shadows, and CPU validation paths to use the same residency
   resolver.
4. Treat a flat LOD as selectable only when its entire page range is resident in
   the current frame's table. Otherwise request it and render the complete
   pinned coarsest LOD; never mix partial fine geometry with its whole-mesh
   fallback.
5. Permit temporal LOD dithering only when both source and target ranges are
   complete.
6. Before replacing a hierarchy parent, verify that the entire child replacement
   set is resident. If not, request the missing pages and emit the resident
   parent.
7. Centralize shader access through `ReadMeshlet`,
   `ReadMeshletLocalVertexIndex`, and triangle-index helpers. Direct meshes
   retain current buffer access; paged meshes decode from the selected physical
   bank. Invalid mappings return an empty meshlet and increment a diagnostic
   counter instead of performing an unsafe read.
8. Leave vertex/index buffers, BLAS input, and coarse ray proxies unchanged.

### Frame lifecycle, settings, and diagnostics

1. At frame-slot fence completion, consume request readback, reclaim retired
   mappings and slots, advance retries, and launch bounded authenticated reads.
2. After beginning the next command buffer, drain decoded pages within the
   8 MiB budget, record staging copies, update only the fence-safe page-table and
   range-state copy, and insert transfer-to-compute/mesh barriers before culling.
3. After all request-producing passes, copy the compact feedback payload into
   that frame slot's readback buffer.
4. Keep current production defaults: enabled, 4,096 pages, 8 MiB uploaded per
   frame, 4,096 requests per frame, and four concurrent reads. Ensure constructor
   defaults, reset/default presets, quality presets, and missing persisted values
   all resolve to these settings.
5. Define `GpuMeshletStreamingEnabled=true` as adaptive activation; add no force
   mode and no settings-schema change.
6. Capture activation per model load. Enable/disable changes affect subsequent
   loads; already-loaded models retain their storage mode and report reload
   required. Cache topology and budget changes are deferred until scene or
   renderer reload rather than migrating active pages.
7. Extend renderer diagnostics with configured, available, active, degraded, and
   reload-required state; active/fallback package and submesh counts; fallback
   reasons; allocated, pinned, resident, queued, reading, uploading, failed, and
   retired pages; committed bytes; request overflow; hit/fallback rates; upload
   bytes; evictions; retries; invalid shader mappings; and the latest failure.
8. Update rendering documentation and settings descriptions to identify this as
   managed physical residency, explain adaptive fallback, and state that existing
   sidecars do not require recooking.

## Public Contracts

1. Preserve the existing cooked manifest and sidecar schemas and all current
   streaming setting names.
2. Keep `IMeshletStreamingPageUploader` source-compatible. Back its existing
   one-package residency manager with the refactored policy, while the renderer
   uses the same policy through the multi-package coordinator.
3. Add streaming state and counters through the existing renderer-diagnostics
   surface. Runtime model bindings, page keys, physical allocators, GPU range
   records, and virtual mappings remain internal implementation types.
4. Extend the internal GPU mesh metadata ABI with a streaming-range index and
   residency flags; validate its C#/GLSL size and offsets in ABI tests.

## Test and Acceptance Plan

### Automated tests

1. Unit-test page repacking, offset rebasing, exact 64 KiB bounds, high-bit
   addressing, direct/paged shader resolution, invalid mappings, and C#/GLSL ABI
   agreement.
2. Test multiple packages sharing globally unique slots, pinned admission,
   largest-range capacity checks, lazy bank growth, budget rejection, global LRU
   priority, retry/backoff, request deduplication and overflow, cancellation,
   unload, and two-frame retirement safety.
3. Test adaptive byte accounting, small/static/skinned/mixed models, missing or
   corrupt sidecars, oversized ranges, fine-page corruption, and transactional
   rollback in both upload paths. Assert that activated meshes have no duplicate
   full-resident GPU meshlet storage.
4. Test whole-range LOD fallback, suppressed unsafe dithering,
   hierarchy-parent fallback, foliage, transparent geometry, local shadows, and
   CPU validation.
5. Compile every affected shader permutation and run Vulkan validation with no
   descriptor, synchronization, out-of-bounds, device-loss, or stale-slot
   errors.

### Production qualification

Run the full existing build and test suite, then qualify representative large
cooked scenes for at least three 1,000-frame camera runs on the RTX 3060 Laptop
target. The implementation is accepted when:

1. physical cache allocation never exceeds 256 MiB at defaults;
2. uploads never exceed 8 MiB per frame;
3. warm-run residency hit rate is at least 90%;
4. full meshlet GPU allocation is measurably reduced;
5. visual output has no missing or duplicated geometry and remains within the
   existing reference tolerance;
6. CPU and GPU p95 frame times regress by no more than 2%; and
7. Vulkan validation reports no relevant errors or device loss.

Run AMD integrated-GPU correctness and fallback coverage as well. Performance
qualification remains required only on the production target.

## Assumptions and Defaults

1. "Physical sparse residency" means the selected portable managed page cache,
   not Vulkan sparse-resource binding or sparse queues.
2. Existing schema-1 sidecars are authoritative and require no recook. Assets
   without compatible sidecars remain full resident until next normally cooked.
3. Coarsest LOD and hierarchy fallback data are always pinned; skinned and
   runtime-imported meshes remain full resident.
4. Residency changes use a reload boundary; seamless migration of already-loaded
   meshes is intentionally out of scope.
