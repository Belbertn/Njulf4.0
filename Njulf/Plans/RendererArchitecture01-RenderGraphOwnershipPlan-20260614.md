# Renderer Architecture 01 - Render Graph Ownership Plan - 2026-06-14

Target outcome: the render graph owns frame resources, pass dependencies, barriers, transient allocation, aliasing, resolution classes, pass timing, and memory accounting. Individual passes record rendering work, but they no longer manually own scene target lifetime, layout state, swapchain resize policy, or cross-pass synchronization.

This plan is performance-first and quality-preserving. No feature is considered complete if it relies on stubs, placeholder images, disabled validation, broad barriers used as a workaround, or duplicate old/new resource paths.

## Non-Negotiable Requirements

1. Every pass declares its inputs, outputs, read/write access, queue, resolution class, clear/load/store behavior, and whether resources persist across frames.
2. The graph compiles a complete dependency plan before command recording.
3. The graph generates Vulkan image and buffer barriers from declarations, not from ad hoc pass code.
4. Transient render targets are allocated by the graph and aliased when lifetimes do not overlap.
5. Persistent history resources are explicit and never accidentally aliased.
6. Swapchain resize and resolution-scale changes are handled by recompiling graph resources, not by each pass patching itself.
7. Render-target usage flags are minimal and derived from declared pass use.
8. Old manual layout ownership is removed as passes migrate. There must not be two authoritative layout trackers.

## Current Problems To Remove

1. Passes still call manual target transitions and know image layouts.
2. `RenderTargetManager` owns a fixed set of targets instead of graph-created resources.
3. Some descriptor sets are recreated by individual passes on swapchain resize because pass-owned image views change.
4. `VulkanRenderer.Clear` performs a standalone render operation outside the graph.
5. `RenderGraph` schedules passes, but does not yet fully own resource lifetimes, barriers, aliasing, or memory.
6. Dynamic resolution currently works by resizing renderer-owned targets, but the graph cannot yet choose per-pass resolution or validate target compatibility.

## Phase 0 - Baseline And Inventory

Implementation checklist:
- `Njulf.Rendering/Pipeline/ProductionRenderPipeline.cs`: centralize the current production pass order and validation.
- `Njulf.Rendering/Diagnostics/RenderGraphResourceInventorySnapshot.cs`: define immutable image and buffer inventory dump records.
- `Njulf.Rendering/Diagnostics/RenderGraphResourceInventoryBuilder.cs`: generate the current production frame inventory from `RenderSettings`, scene extent, depth format, and optional `SceneRenderingData`.
- `Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`: include the resource inventory in exported performance snapshots.
- `Njulf.Rendering/VulkanRenderer.cs`: update the latest inventory after frame rendering without changing pass execution.
- `Njulf.Tests`: assert the production pass order and inventory coverage remain stable during migration.

1. Add a generated resource inventory document or diagnostic dump for the current production frame.
2. List every image resource:
   - swapchain color.
   - scene color.
   - scene depth.
   - fogged scene color.
   - AO raw, blurred, scratch.
   - LDR scene color.
   - SMAA edges and blend weights.
   - motion vectors.
   - TAA history A and B.
   - bloom chain.
   - shadow maps.
   - environment and reflection images.
   - Hi-Z pyramid.
3. For each resource, record format, extent policy, usage flags, lifetime, producer passes, consumer passes, and persistence.
4. List every buffer resource used across passes, including scene object buffers, material buffers, meshlet draw buffers, light buffers, particle buffers, diagnostics buffers, and staging/upload buffers.
5. Add tests that assert the current production pass order remains stable until migration intentionally changes it.

Acceptance criteria:
- A developer can inspect one diagnostics dump and see every frame resource and its producers/consumers.
- No rendering behavior changes in this phase.

## Phase 1 - Introduce Graph Resource Descriptors

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphResourceHandle.cs`: add typed image and buffer handles with generation checks.
- `Njulf.Rendering/Pipeline/RenderGraphDescriptors.cs`: add image, buffer, pass, queue, persistence, resolution, access, load/store, and timing descriptors.
- `Njulf.Rendering/Pipeline/RenderGraphResourceRegistry.cs`: validate undeclared/stale handles, write-after-write without an explicit edge, transient read-before-write, transient cross-frame use, and missing history invalidation rules.
- `Njulf.Rendering/Pipeline/RenderPassBase.cs`: add an opt-in resource declaration hook.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: compile declarations during initialization without changing current pass execution.
- `Njulf.Rendering/Pipeline/DebugDrawPass.cs`: declare scene color, scene depth, and debug vertex buffer access as the first noncritical pass migration.
- `Njulf.Tests`: cover invalid declarations, missing producers, and derived usage flags.

1. Add `RenderGraphResourceHandle` as a typed handle with generation checks.
2. Add `RenderGraphImageDesc`:
   - name.
   - format.
   - resolution class.
   - explicit width/height override for fixed-size resources.
   - mip count.
   - array layers.
   - sample count.
   - clear value.
   - persistence mode: transient, imported, history, external.
   - allowed usages derived from pass declarations.
3. Add `RenderGraphBufferDesc`:
   - name.
   - stride.
   - count or byte size.
   - persistence mode.
   - usage flags.
   - CPU upload policy.
4. Add `RenderGraphPassDesc`:
   - pass name.
   - queue class.
   - reads.
   - writes.
   - read-write resources.
   - load/store operations.
   - clear operations.
   - required pipeline stages.
   - secondary command buffer support.
   - timing label.
5. Add validation:
   - no undeclared resource access.
   - no write-after-write without an edge.
   - no read-before-write for transient resources.
   - no transient resource used across frames.
   - no history resource missing history invalidation rules.

Acceptance criteria:
- At least one noncritical pass can declare resources through descriptors while old execution still runs.
- Unit tests cover invalid read/write declarations and missing producer errors.

## Phase 2 - Compile Pass Dependencies

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphDescriptors.cs`: add pass enablement and culling metadata.
- `Njulf.Rendering/Pipeline/RenderGraphResourceRegistry.cs`: compile declarations into diagnostics and reject incompatible duplicate image descriptors.
- `Njulf.Rendering/Pipeline/RenderGraphDeclarationCompiler.cs`: topologically sort declared passes, detect dependency cycles, cull disabled/unused passes, compute resource lifetimes, estimate resource bytes, and estimate barrier counts.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: retain compilation diagnostics on the graph declaration plan while preserving old execution order.
- `Njulf.Tests`: validate dependency sorting, cycles, culling, lifetimes, usage diagnostics, and descriptor mismatch errors.

1. Build a graph compiler that topologically sorts pass declarations.
2. Preserve explicit pass ordering constraints where rendering semantics require them, such as depth before Hi-Z and tone map before AA.
3. Generate resource lifetime intervals from first use to last use.
4. Detect illegal cycles and report pass/resource names in the error.
5. Detect resource extent or format mismatches when a pass reads a resource with incompatible expectations.
6. Add pass culling:
   - cull disabled passes.
   - cull passes whose outputs are unused.
   - never cull externally visible side effects such as presentation, query resolve, screenshots, or diagnostics.
7. Add graph compilation diagnostics with:
   - compiled pass order.
   - culled passes.
   - resource lifetimes.
   - estimated resource bytes.
   - barrier count.

Acceptance criteria:
- Existing production graph compiles into the same effective pass order.
- Disabled features remove their passes and transient targets without requiring placeholder target allocation.

## Phase 3 - Graph-Owned Image Allocation

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphImageAllocationPlan.cs`: derive allocation requests, minimal Vulkan image usage flags, allocation category, and byte estimates from compiled declarations.
- `Njulf.Rendering/Pipeline/RenderGraphImageAllocator.cs`: add a VMA-backed graph image allocator with image/view ownership, debug names, optional driver compression, and memory tracking.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: expose the latest graph image allocation plan after declaration compilation.
- `Njulf.Rendering/ServiceCollectionExtensions.cs`: register the graph image allocator for later production use.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track deferred runtime ownership switch-over items.
- `Njulf.Tests`: validate allocation categories, graph-owned filtering, minimal usage, byte estimates, and invalid allocatable descriptors without requiring a Vulkan device.

1. Add `RenderGraphImageAllocator` backed by VMA.
2. Allocate imported resources for swapchain images and externally owned persistent resources.
3. Allocate transient resources after graph compilation.
4. Derive image usage flags from pass use:
   - color attachment only when written as color.
   - depth attachment only when written/read as depth attachment.
   - sampled only when sampled.
   - storage only when used as storage.
   - transfer flags only when copied or blitted.
5. Add image compression control for eligible color attachments when supported.
6. Track allocation category for diagnostics:
   - transient render target.
   - persistent history.
   - imported swapchain.
   - shadow/environment/reflection external resources.
7. Remove fixed target allocation from `RenderTargetManager` after equivalent graph allocation exists.
8. Keep a temporary adapter only while migrating pass-by-pass, then delete it.

Acceptance criteria:
- Scene color, scene depth, AO targets, fog target, LDR target, SMAA targets, motion vectors, and bloom targets are graph-created.
- Disabled features do not allocate full-size targets.
- Resource usage flags are verified by tests and diagnostics.

## Phase 4 - Barrier Generation

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphDescriptors.cs`: add subresource range fields to declared resource uses.
- `Njulf.Rendering/Pipeline/RenderGraphBarrierPlanner.cs`: generate image and buffer barrier descriptors from declared pass access transitions.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: expose the latest generated barrier plan for diagnostics.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track deferred runtime replacement of manual barriers.
- `Njulf.Tests`: validate generated layout/access/stage transitions and per-mip image ranges.

1. Add a graph layout/access state model per image subresource and buffer range.
2. Generate image barriers between pass writes and reads.
3. Generate buffer barriers for compute-to-graphics, transfer-to-shader, upload-to-shader, and diagnostics reads.
4. Support per-mip and per-array-layer barriers for Hi-Z and bloom.
5. Replace broad barriers with precise stage and access masks.
6. Validate that no pass calls manual `TransitionTo...` on graph-owned resources.
7. Add debug mode that dumps generated barriers per pass.
8. Add a strict mode that fails if a pass records access to a graph resource without declaration.

Acceptance criteria:
- All scene target transitions are generated by graph compilation.
- Manual layout tracking inside `RenderTarget` is removed for graph-owned targets.
- RenderDoc validation shows no layout hazards or unnecessary global barriers in normal frames.

## Phase 5 - Resource Aliasing

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphAliasPlanner.cs`: plan alias groups from allocation requests and compiled lifetime intervals.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: expose alias planning diagnostics.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track deferred VMA alias binding/runtime validation work.
- `Njulf.Tests`: validate non-overlap grouping, overlap rejection, history/import exclusion, disabled aliasing, and peak transient byte diagnostics.

1. Add alias compatibility checks:
   - same memory type requirements.
   - non-overlapping lifetimes.
   - compatible image dimensions and alignment.
   - no history or imported resources.
2. Add alias groups after lifetimes are known.
3. Prefer aliasing large full-resolution transient targets first:
   - AO scratch.
   - bloom temporary resources.
   - SMAA edges/blend after use.
   - fogged scene color when fog output is consumed before later post targets.
4. Add graph diagnostics for:
   - unaliased transient bytes.
   - aliased transient bytes.
   - peak transient bytes.
   - alias group members.
5. Add a debug setting to disable aliasing for diagnosis.
6. Validate aliasing with RenderDoc captures, image diff scenes, and resize stress.

Acceptance criteria:
- Peak transient render-target memory is lower with aliasing enabled.
- No alias group includes a resource whose lifetime overlaps another group member.
- Disabling aliasing produces identical image output apart from expected floating-point noise.

## Phase 6 - Resolution Classes

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphDescriptors.cs`: add per-resource custom scale metadata.
- `Njulf.Rendering/Pipeline/RenderGraphResolutionResolver.cs`: resolve swapchain, scene, half-scene, quarter-scene, fixed, custom-scale, and history-matched extents.
- `Njulf.Rendering/Pipeline/RenderGraphResolutionResolver.cs`: materialize compiled graph image descriptors from the live renderer resolution context before allocation/barrier/alias/descriptor planning.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: expose resolved image diagnostics and recompile derived plans for the current resolution profile.
- `Njulf.Rendering/VulkanRenderer.cs`: recompile graph resolution after graph initialization, render-target profile changes, and swapchain recreation.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track deferred settings-aware declaration recompilation and viewport/scissor replacement.
- `Njulf.Tests`: validate class sizing, odd-dimension rounding, fixed-size validation, and history invalidation on resolution changes.

Implementation status:
- Resolution classes, custom scales, fixed extents, history-matched extents, and odd-dimension rounding are implemented and covered by tests.
- The renderer now calls `RenderGraph.RecompileForResolution(...)` on initial graph setup, render-target profile changes, and swapchain recreation so allocation, barrier, alias, descriptor, and byte diagnostics use concrete current extents.
- `RenderGraphResolutionMaterializer` rebuilds diagnostics from materialized image descriptors and exposes `RenderGraph.ResolvedImages`.
- Remaining runtime work: settings-aware declaration recompilation when feature topology changes after initialization, graph-generated viewport/scissor state, and wiring `HistoryInvalidated` into live TAA/history resource reset.

1. Add graph resolution classes:
   - swapchain.
   - scene.
   - half scene.
   - quarter scene.
   - fixed.
   - per-resource custom scale.
   - history-matched scene.
2. Make dynamic resolution update the scene class.
3. Recompute dependent graph resources when the scene class changes.
4. Add history invalidation rules when resolution changes:
   - TAA history.
   - motion vectors.
   - SSR history once implemented.
   - denoiser histories once implemented.
5. Ensure depth, Hi-Z, light culling, decals, particles, transparency, AA, and post consume the intended resolution class.
6. Add per-pass viewport/scissor generation from the graph output extent.
7. Remove manually duplicated viewport/scissor logic where possible.

Acceptance criteria:
- Resolution scale changes do not require individual pass resize code.
- Scene-depth, Hi-Z, AO, light culling, TAA, and composite dimensions remain internally consistent.

## Phase 7 - Descriptor Binding Model

Implementation checklist:
- `Njulf.Rendering/Pipeline/RenderGraphDescriptorPlan.cs`: centralize graph image descriptor-to-bindless-index planning using existing `BindlessIndex` constants.
- `Njulf.Rendering/Pipeline/RenderGraph.cs`: expose descriptor plan diagnostics after graph declaration compilation.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track deferred live descriptor writes and per-pass resize callback cleanup.
- `Njulf.Tests`: validate static graph target mappings, bloom mip mappings, dynamic fallback indices, expected sampled layouts, and duplicate static index validation.

1. Add graph-managed image view descriptors for graph resources.
2. Bind graph image descriptors by stable logical names or generated bindless indices.
3. Rebuild descriptor mappings after graph compilation, not from individual pass resize callbacks.
4. Keep pass-specific descriptor sets only for immutable pipeline resources or fixed external inputs.
5. Remove per-pass descriptor recreation that exists only because image views changed.
6. Ensure bindless indices are stable within a frame and validated before pass execution.

Acceptance criteria:
- AO, AO blur, fog, bloom, Hi-Z, AA, and composite no longer own resize-only descriptor rebuild paths for graph targets.
- Descriptor updates for graph targets are centralized and visible in graph diagnostics.

## Phase 8 - Migrate Passes

Implementation checklist:
- `Njulf.Rendering/Pipeline/DepthPrePass.cs`: declare scene depth clear/write and depth meshlet draw-buffer reads while preserving existing command recording.
- `Njulf.Rendering/Pipeline/ProductionRenderGraphResources.cs`: centralize production graph logical resource names and descriptor shapes during pass migration.
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`: declare scene depth sampling and Hi-Z pyramid storage/readback intent.
- `Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs`: declare scene depth sampling and AO raw storage output.
- `Njulf.Rendering/Pipeline/AmbientOcclusionBlurPass.cs`: declare AO raw, AO scratch, AO blurred, and scene depth blur dependencies.
- `Njulf.Rendering/Pipeline/MotionVectorPass.cs`: declare motion-vector color output, scene-depth read, and opaque meshlet draw-buffer reads.
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`: declare directional shadow map writes and directional shadow meshlet draw-buffer reads.
- `Njulf.Rendering/Pipeline/SpotShadowPass.cs`: declare spot shadow atlas writes and local shadow meshlet draw-buffer reads.
- `Njulf.Rendering/Pipeline/PointShadowPass.cs`: declare point shadow cubemap-array writes and local shadow meshlet draw-buffer reads.
- `Njulf.Rendering/Pipeline/TiledLightCullingPass.cs`: declare scene-depth sampling, light-buffer reads, and tiled-light storage writes.
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`: declare HDR scene color output, scene depth, Hi-Z, AO, meshlet, light, and tile-list reads.
- `Njulf.Rendering/Pipeline/SkyboxPass.cs`: declare HDR scene color write/load, scene-depth read, and environment cubemap sampling.
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`: declare HDR scene color write/load, scene-depth/Hi-Z reads, transparent meshlet draws, and light/tile reads.
- `Njulf.Rendering/Pipeline/ParticlePass.cs`: declare HDR scene color write/load, scene-depth sampling, particle instance, and particle batch buffer reads.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: track which passes have declarations only versus full graph-owned execution.
- `Njulf.Tests`: keep graph planning tests passing as migrated declarations are added to production initialization, and cover catalog descriptor consistency.

Implementation route update:
- The production graph contains `DirectionalShadowPass`, `SpotShadowPass`, and `PointShadowPass` before the original migration-order list, so Phase 8 includes declaration migration for those passes as external graph resources.
- Until graph recompilation is settings-aware, declarations for mode-dependent outputs such as tone map/composite and anti-aliasing intentionally over-approximate possible outputs while the old runtime path still chooses the active target.
- Current Phase 8 migration is declaration coverage over adapter/imported resources. Direct command recording still uses `RenderTargetManager`, external shadow resources, bindless indices, and manual transitions until graph-owned runtime handles are verified.
- Screenshot and RenderDoc capture paths are request/diagnostics services in the current codebase, not command-recorded render graph passes. They remain tracked until a real swapchain readback/capture hook exists.

Migration order:

1. Clear and scene depth initialization.
2. Depth prepass.
3. Hi-Z build.
4. AO and AO blur.
5. Motion vectors.
6. Tiled light culling.
7. Forward plus.
8. Skybox.
9. Transparent forward.
10. Particles.
11. Debug draw.
12. Fog.
13. Auto exposure.
14. Bloom.
15. Tone map/composite.
16. Anti-aliasing.
17. Screenshot and capture paths.

For each pass:
1. Add resource declarations.
2. Remove manual target transitions.
3. Remove pass-owned image layout assumptions.
4. Replace direct target references with graph resource handles.
5. Move clear/load/store behavior into pass declarations.
6. Add tests for declarations where feasible.
7. Validate image output against the previous path before deleting the old code.

Acceptance criteria:
- No migrated pass can touch an undeclared graph resource.
- Each migrated pass has old direct target wiring removed after validation.

## Phase 9 - Remove Old Architecture

Implementation checklist:
- `Njulf.Rendering/Resources/RenderTargetManager.cs`: delete fixed scene target allocation only after graph-created images are used by production command recording.
- `Njulf.Rendering/Resources/RenderTarget.cs`: remove manual layout tracking only after graph-generated barriers are submitted for graph-owned resources.
- `Njulf.Rendering/Pipeline/*Pass.cs`: delete per-pass transition and resize-only descriptor paths after each pass records against graph handles.
- `Njulf.Rendering/VulkanRenderer.cs`: remove standalone `Clear`/swapchain layout ownership after clear/present/capture are graph-declared and verified.
- `Plans/RendererArchitectureCompletionTracker-20260614.md`: keep every old-path deletion blocked until build, full tests, resize/toggle validation, RenderDoc validation, and visual parity are complete.

Implementation status:
- Deferred. Phase 8 currently provides full production declaration coverage and Phase 6 now materializes that graph for the current resolution profile, but live command recording still uses old runtime-owned targets and manual transitions. Deleting old architecture now would break rendering, so removal waits for graph-owned runtime handles/barrier submission to be verified.

Delete or reduce:

1. Fixed scene-target ownership from `RenderTargetManager`.
2. Manual image layout tracking for graph-owned resources.
3. Per-pass resize callbacks that only update graph target descriptors.
4. Renderer-level scene clear outside the graph.
5. Placeholder full-size resources for disabled passes.
6. Any duplicate target registration path where both graph and renderer register the same image.
7. Broad compatibility usage flags kept only for old code.
8. Dead `OnSwapchainRecreated` code after graph recompilation owns resize.

Acceptance criteria:
- There is one authoritative owner for each resource.
- The old architecture is deleted, not left as an unused fallback.

## Validation

1. Unit tests:
   - graph dependency validation.
   - illegal resource access.
   - resource lifetime calculation.
   - alias compatibility.
   - resolution class sizing.
   - barrier generation for representative pass chains.
2. Integration tests:
   - resize.
   - dynamic resolution changes.
   - AO on/off.
   - AA modes.
   - bloom on/off.
   - fog on/off.
   - transparent/particle/debug scenes.
3. Manual GPU validation:
   - RenderDoc capture with validation enabled.
   - memory snapshot before/after aliasing.
   - timestamp comparison before/after graph migration.
   - stress scene with frequent resolution changes.
4. Performance acceptance:
   - no CPU regression from graph compilation in steady state.
   - graph compiles only when settings or dimensions change.
   - fewer transient bytes after aliasing.
   - no added GPU barriers in steady state compared with manual path.

## Definition Of Done

1. All production passes use graph-declared resources.
2. Graph-owned target allocation replaces fixed scene target allocation.
3. Barriers are generated by the graph.
4. Dynamic resolution and swapchain recreation are graph recompilation events.
5. Disabled passes allocate no full-size transient resources.
6. Old manual resource ownership and unused compatibility paths are removed.
7. Build, tests, RenderDoc validation, and performance snapshots pass.
