# Renderer Architecture Completion Tracker - 2026-06-14

Purpose: track features and cleanup items that cannot be considered fully done until all render graph ownership phases are implemented and validated together.

This file is not a replacement for the phase plans. It records deferred completion checks and cross-phase work that must not be forgotten while individual phases are implemented incrementally.

Future-only work discovered from this point forward is tracked in `Plans/RendererArchitectureFutureChanges-20260614.md`. Keep this file as historical/current migration context; add new cross-plan follow-up items to the future tracker.

## Deferred Until Full Migration

### Production Graph Declarations

- [ ] Every production pass declares all image and buffer reads, writes, read-writes, queue class, load/store operations, clear behavior, timing label, and external side effects.
- [ ] All declarations use stable logical resource names shared with diagnostics and allocation.
- [ ] The complete production graph compiles into the intended effective order.
- [ ] Disabled features remove their passes from the compiled graph when outputs are unused.
- [ ] Declaration validation runs in strict/debug mode during development.

### Runtime Culling And Allocation

- [ ] Disabled AO, AA, bloom, fog, motion-vector, and TAA features stop allocating full-size placeholder targets.
- [ ] Pass culling feeds graph-owned allocation, not just compiler diagnostics.
- [ ] Transient resource lifetime intervals are used by the allocator.
- [ ] Persistent history resources are allocated separately and never aliased.
- [ ] Imported swapchain and external shadow/environment/reflection resources are represented as graph imports.

### Old Ownership Removal

- [ ] `RenderTargetManager` no longer owns fixed scene target allocation after graph allocation replaces it.
- [ ] Manual layout state on graph-owned render targets is removed.
- [ ] Pass-owned swapchain resize descriptor rebuilds are removed for graph targets.
- [ ] Renderer-level scene clear outside the graph is removed or represented as a graph pass.
- [ ] Duplicate old/new target registration paths are deleted.
- [ ] Broad compatibility usage flags kept only for old code are removed.

### Barrier Ownership

- [ ] Graph-generated image barriers replace manual `TransitionTo...` calls for graph-owned resources.
- [ ] Graph-generated buffer barriers cover compute-to-graphics, transfer-to-shader, upload-to-shader, and diagnostics readback.
- [ ] Per-mip and per-layer barriers are generated for Hi-Z, bloom, cubemap, and array resources.
- [ ] Strict mode fails when pass code accesses a graph resource without declaration.
- [ ] Barrier diagnostics can be dumped per pass.

### Aliasing And Memory

- [ ] Transient image aliasing is enabled for compatible non-overlapping lifetimes.
- [ ] Alias groups never include imported, external, or history resources.
- [ ] Large full-resolution transient targets are prioritized for aliasing.
- [ ] Diagnostics report unaliased bytes, aliased bytes, peak transient bytes, and alias group members.
- [ ] Aliasing can be disabled for debugging with identical expected output.

### Resolution And Resize

- [x] Dynamic resolution updates materialized graph scene extents through `RenderGraph.RecompileForResolution(...)`.
- [x] Swapchain recreation recompiles materialized graph resource descriptors and derived plans.
- [ ] TAA and other history resources invalidate live GPU images on resolution changes.
- [ ] Graph-generated viewport and scissor state replaces duplicated pass logic where practical.
- [ ] Scene depth, Hi-Z, AO, light culling, TAA, composite, particles, transparency, and post-process dimensions remain internally consistent.

### Descriptor Model

- [ ] Graph-owned image descriptors are centralized.
- [ ] Graph target descriptors rebuild after graph compilation, not in individual passes.
- [ ] Bindless indices for graph resources are stable within a frame.
- [ ] Per-pass descriptor sets remain only for immutable pipeline resources or fixed external inputs.
- [ ] Descriptor diagnostics list graph-managed views and bindless indices.

### Validation Before Deleting Old Paths

- [ ] Build passes.
- [ ] Full unit test suite passes.
- [ ] Resize and dynamic resolution integration tests pass.
- [ ] Feature toggles pass for AO, AA, bloom, fog, transparency, particles, debug draw, and shadows.
- [ ] RenderDoc validation shows no layout hazards.
- [ ] Memory snapshot confirms lower peak transient bytes after aliasing.
- [ ] Timestamp comparison shows no steady-state CPU/GPU regression from graph compilation or generated barriers.
- [ ] Visual output matches the previous renderer within expected floating-point tolerance.

## Current Known Deferrals From Phases 0-2

- [ ] Phase 2 pass culling currently exists at declaration/compiler diagnostics level only; runtime allocation still uses `RenderTargetManager`.
- [ ] All current `RenderPassBase` production passes now have resource declarations; strict production completeness still depends on replacing direct target/bindless access with graph handles and settings-aware graph recompilation.
- [ ] Estimated barrier count is diagnostic-only; real Vulkan barrier generation starts in Phase 4.
- [ ] Resource lifetimes now cover declared production passes, but remain adapter lifetimes until graph-owned resources replace direct runtime target access.
- [ ] Placeholder target removal is blocked until graph-owned image allocation is active.

## Current Known Deferrals From Phase 3

- [ ] VMA-backed graph image allocation exists but is not yet authoritative for production rendering.
- [ ] `RenderGraph.ImageAllocationPlan` reports graph-owned/imported/external image intent, but `RenderTargetManager` still allocates current scene targets.
- [ ] Graph-owned allocation is explicit through `RenderGraphImageAllocator.Recreate(...)`; production execution should call it only after pass migration replaces direct target references.
- [ ] Imported swapchain/external resources are represented in the allocation plan but do not yet carry imported Vulkan image/view handles.
- [ ] Disabled feature target deallocation remains blocked until all affected passes declare resources and consume graph handles.

## Current Known Deferrals From Phase 4

- [ ] Barrier planning is generated from declarations, but production command recording still executes existing manual barriers and `RenderTarget.TransitionTo...` calls.
- [ ] Generated barrier descriptors are not yet converted to live `DependencyInfo` submissions in production.
- [ ] Strict undeclared-access enforcement exists at declaration validation level only; runtime command recording is not instrumented yet.
- [ ] Per-mip/per-layer barrier planning exists in descriptors, but production Hi-Z and bloom passes still perform their own manual transitions.

## Current Known Deferrals From Phase 5

- [ ] Alias groups are planned from lifetimes, but graph image allocation does not yet bind multiple images to shared memory.
- [ ] Compatibility is conservative at descriptor level; final VMA memory requirement/alignment checks still need live Vulkan queries.
- [ ] History, imported, and external images are excluded from aliasing as required.
- [ ] Aliasing diagnostics exist in `RenderGraph.AliasPlan`, but no RenderDoc/image-diff validation is possible until graph-owned targets are active.

## Current Known Deferrals From Phase 6

- [x] Resolution class resolver exists and swapchain/dynamic-resolution changes now rematerialize production graph descriptors and derived plans.
- [x] Allocation descriptors are materialized from resolved extents before graph-owned images become authoritative.
- [ ] Settings-aware graph declaration recompilation is still needed when topology/counts change after initialization, such as bloom mip count or feature-mode changes that alter declared resources.
- [ ] History invalidation decisions are computed by the resolver but not yet wired to TAA resource reset.
- [ ] Viewport/scissor generation from graph output extents is not yet replacing duplicated pass code.

## Current Known Deferrals From Phase 7

- [ ] Descriptor planning maps graph images to stable bindless indices, but live descriptor writes still happen in renderer/pass resize paths.
- [ ] Graph-managed image views are not yet registered centrally because production graph-owned image allocation is not authoritative.
- [ ] Per-pass descriptor sets that only exist for resize-driven graph target views still need removal during pass migration.
- [ ] Bindless index validation is planning-only until migrated passes consume graph descriptors.

## Phase 8 Migration Status

- [ ] Shared production graph resource catalog: `ProductionRenderGraphResources` centralizes names and adapter descriptors for migrated declarations; resources remain imported/external until graph-owned allocation becomes authoritative.
- [ ] Clear and scene depth initialization: `DepthPrePass` declares scene-depth clear/write intent; standalone `VulkanRenderer.Clear` is still outside the graph.
- [ ] Depth prepass: declarations added; command recording still uses `RenderTargetManager` and manual transitions.
- [ ] Hi-Z build: declarations added for scene-depth sampling and Hi-Z pyramid read/write; command recording still uses `HiZDepthPyramid` and manual per-mip barriers.
- [ ] AO and AO blur: declarations added for scene-depth sampling, AO raw, AO scratch, and AO blurred; command recording still owns descriptor sets and manual image transitions.
- [ ] Motion vectors: declarations added for motion-vector output, scene-depth read, and opaque meshlet draw-buffer read; command recording still uses `RenderTargetManager` and manual transitions.
- [ ] Tiled light culling: declarations added for scene-depth sampling, light-buffer reads, and tile-list buffer writes; command recording still owns manual depth/image and buffer barriers.
- [ ] Forward plus: declarations added for HDR scene color, scene depth, Hi-Z, AO, opaque draw, light, and tile-list reads; command recording still uses `RenderTargetManager` and bindless descriptors directly.
- [ ] Skybox: declarations added for HDR scene color load/store, scene-depth read, and environment cubemap sampling; command recording still owns direct target references.
- [ ] Transparent forward: declarations added for HDR scene color load/store, scene-depth/Hi-Z reads, transparent draw buffer, light, and tile-list reads; command recording still owns direct target references.
- [ ] Particles: declarations added for HDR scene color load/store, scene-depth read/sampling, particle instance, and particle batch buffers; command recording still owns direct target references.
- [ ] Debug draw: declarations added and ordered after `ParticlePass`; command recording still uses `RenderTargetManager` and manual transitions.
- [ ] Fog: declarations added for HDR scene color, scene depth, environment cubemap, and fogged HDR storage output; command recording still owns descriptor sets and manual transitions.
- [ ] Auto exposure: declarations added for HDR/fogged scene color reads and histogram/state storage writes; internal transfer clear remains manually barriered.
- [ ] Bloom: declarations added for HDR/fogged scene color reads and bloom mip sampled/storage use; command recording still owns per-mip descriptor sets and manual transitions.
- [ ] Tone map/composite: declarations added for HDR/fogged scene color, bloom, AO, environment, auto-exposure state, LDR output, and swapchain output; command recording still owns direct target/swapchain references.
- [ ] Anti-aliasing: declarations added for LDR input, SMAA intermediates/lookups, motion vectors, old-owned imported TAA histories, and swapchain output; command recording still owns direct target/swapchain references and TAA history ping-pong.
- [ ] Screenshot and capture paths: screenshot requests are queued for diagnostics but no swapchain/graph readback path is currently dequeued or recorded; RenderDoc capture service is metadata-only and does not currently wrap frame recording.
- [ ] Shadow passes: declarations added for directional shadow map array, spot shadow atlas, point shadow cubemap array, and shadow meshlet draw buffers; command recording still owns external shadow resources and manual layout transitions.
- [ ] Production declaration coverage: all current `RenderPassBase` production subclasses override `DeclareResources`; declarations are still adapter declarations over old runtime-owned resources.
