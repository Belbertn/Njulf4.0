# Revised Renderer Architecture Implementation Plan

This plan supersedes the older abandoned-renderer checklist for the current simplified renderer branch. The current code already contains many systems the older plan treated as missing: renderer diagnostics, performance snapshots, GPU particles, foliage controls, scene GPU compaction, indirect meshlet dispatch, and a basic render graph. The remaining work should focus on hardening those systems and adding missing architecture in staged, testable slices.

`NjulfHelloGame/SampleInputController.cs` should be used as the runtime validation harness. It already exposes toggles for GPU timing, performance snapshots, quality presets, feature isolation, scene GPU compaction, indirect dispatch, foliage impostors, particles, and debug overlays.

## 1. Lock Current Baseline

Capture the current renderer behavior before changing ownership, pass scheduling, or scene submission architecture.

Implementation steps:

1. Run the existing renderer, diagnostics, scene submission, foliage, particle, and smoke tests.
2. Use the sample input controller's performance snapshot export path to capture a current representative frame.
3. Capture baseline numbers for CPU pass record times, GPU pass times, render target memory, scene submission mode, meshlet counts, particle counts, foliage counts, and foliage impostor counts.
4. Save at least one baseline snapshot for the normal Sponza/interior scene and one for the forest foliage scenario.
5. Treat the baseline as a regression reference while migrating graph ownership and scene submission.

Validation:

- Existing tests pass before the migration starts.
- Performance snapshots export successfully.
- Baseline snapshots include enough diagnostics to compare pass timings, scene submission mode, and memory estimates after each later step.

## 2. Add Render Graph Resource Ownership

Move render target and transient resource ownership into graph-declared resources while keeping the current pass order manual.

Implementation steps:

1. Add graph resource identifiers for scene color, LDR scene color, depth, motion vectors, bloom chain, ambient occlusion, fog output, shadow maps, Hi-Z, particle buffers, and transient intermediates.
2. Add per-pass declarations for resources read, resources written, required format, size policy, lifetime scope, and persistence.
3. Keep pass execution order identical to the current renderer.
4. Keep existing manually-created resources available during migration, but route newly migrated resources through the graph registry.
5. Add validation that fails when a pass reads or writes an undeclared graph resource.
6. Migrate resources in small groups, starting with post-process targets before moving depth, shadows, and scene submission resources.

Validation:

- The graph can report a complete resource inventory.
- No migrated pass reads undeclared resources.
- No migrated render target is double-owned.
- Swapchain resize still recreates all size-dependent graph resources.
- Render output matches the baseline snapshots.

## 3. Add Barrier And Layout Planning

Replace per-pass manual barriers with graph-planned transitions where resource usage is known.

Implementation steps:

1. Extend pass resource declarations with access type, pipeline stage, image layout, and queue family intent.
2. Add a barrier planner that derives image layout transitions and buffer barriers from adjacent pass usage.
3. Keep explicit manual barriers as an escape hatch for resources that have not yet been migrated.
4. Add diagnostics for planned barriers per pass and per frame.
5. Migrate one pass group at a time, starting with linear post-process passes.
6. Remove manual barriers only after graph-planned barriers are validated for that pass group.

Validation:

- Vulkan validation remains clean.
- Feature isolation from `SampleInputController.cs` still skips the expected passes.
- Render output remains visually equivalent to the baseline.
- Barrier diagnostics explain every graph-planned transition.

## 4. Add Graph Diagnostics And Inventory Export

Make graph behavior visible before adding more scheduling complexity.

Implementation steps:

1. Add diagnostics for graph resource inventory, resource memory estimate, pass read/write lists, barrier count, transient resource count, persistent resource count, and aliasable resource count.
2. Include graph diagnostics in performance snapshots.
3. Add tests for invalid pass dependencies, missing resource declarations, incompatible formats, and invalid lifetime declarations.
4. Add a console dump or debug overlay hook if it is useful during renderer bring-up.
5. Ensure diagnostics remain useful when feature isolation disables individual passes.

Validation:

- Exported performance snapshots include graph inventory and barrier information.
- Tests catch undeclared resources and invalid pass dependencies.
- Graph diagnostics remain stable across swapchain recreation.

## 5. Add Processed Mesh Asset Pipeline

Introduce render-ready mesh assets before expanding GPU-driven submission further.

Implementation steps:

1. Add `ProcessedMeshAsset` metadata for vertex layout, index layout, meshlets, bounds, material slots, LOD metadata, and optional draw ranges.
2. Add a builder that converts imported model data into processed mesh assets.
3. Keep runtime importer compatibility while allowing processed assets to bypass repeated layout work.
4. Add serialization only after the in-memory builder contract is stable.
5. Add asset pipeline tests using representative glTF content from the current sample scene.
6. Wire processed assets into `MeshManager` behind a narrow loader path.

Validation:

- Processed assets produce the same meshlet counts as the runtime path.
- Processed assets render identically in the sample scene.
- Importer tests cover bounds, material slots, LOD metadata, and meshlet layout.
- Runtime fallback to the existing importer remains available.

## 6. Harden Current GPU Scene Submission

Build on the existing scene submission path instead of copying abandoned GPU scene code back wholesale.

Implementation steps:

1. Audit the existing `SceneSubmissionSettings` path for GPU compaction, indirect meshlet dispatch, GPU LOD selection, GPU shadow compaction, and CPU/GPU validation comparison.
2. Improve fallback reasons when GPU submission is disabled, unsupported, capacity-limited, or validation-failed.
3. Add focused tests for CPU/GPU list comparison and mismatch reporting.
4. Add diagnostics for capacity pressure, overflow, missing LOD fallback, shadow compaction status, and indirect task counts.
5. Use `Ctrl+F11` and `Ctrl+F12` from `SampleInputController.cs` to validate scene GPU compaction and indirect dispatch at runtime.
6. Avoid introducing a new broad `GpuSceneManager` until current scene submission diagnostics show a concrete need.

Validation:

- Scene GPU compaction can be toggled at runtime.
- Indirect meshlet dispatch can be toggled at runtime.
- Diagnostics clearly report active mode, fallback mode, candidate counts, emitted counts, overflow counts, and validation mismatches.
- CPU and GPU submission paths render equivalent visible geometry.

## 7. Move To Production Pipeline Declaration

After graph ownership is stable, move pass registration and pass-resource declarations out of `VulkanRenderer` orchestration.

Implementation steps:

1. Add a production pipeline declaration object that registers passes, dependencies, resources, feature conditions, and debug names.
2. Keep `VulkanRenderer` responsible for device, swapchain, lifetime, and frame orchestration.
3. Move pass ordering into the declaration object while preserving the current pass order.
4. Move feature-conditioned pass inclusion into declarative metadata where practical.
5. Add startup diagnostics that report the declared production pipeline.
6. Add tests that verify the declared pass list and dependency order.

Validation:

- The declared pass list matches the current renderer order.
- Feature isolation still skips the same passes as before.
- Startup diagnostics show the active production pipeline.
- `VulkanRenderer` loses pass registration responsibility without changing frame behavior.

## 8. Optional: Weighted OIT Transparency

Implement this only if transparency quality becomes a near-term goal.

Implementation steps:

1. Add weighted OIT accumulation and revealage graph resources.
2. Add a weighted transparent pass and a weighted composite pass.
3. Wire `TransparencySettings.Mode` so the renderer can switch between sorted alpha and weighted OIT.
4. Add debug views for accumulation and revealage.
5. Keep sorted alpha as the fallback mode.
6. Add performance and memory diagnostics for OIT targets.

Validation:

- Transparent curtains, glass-like materials, and particles render acceptably.
- Sorted alpha fallback remains available.
- OIT memory cost appears in diagnostics and performance snapshots.

## 9. Optional: Async Compute Scheduling

Defer async compute until graph resource ownership, barrier planning, and diagnostics are reliable.

Implementation steps:

1. Add async eligibility metadata to compute passes.
2. Start with low-risk candidates such as Hi-Z, bloom, ambient occlusion blur, fog, and particles.
3. Add queue ownership transitions through the graph planner.
4. Keep async compute disabled by default while validating synchronization.
5. Add a runtime setting and diagnostics for async-enabled passes, overlap candidates, and queue ownership transitions.
6. Enable async per pass only after GPU timing shows a measurable benefit.

Validation:

- Vulkan validation remains clean.
- GPU timing shows actual overlap or a measurable frame-time benefit.
- Disabling async compute returns to baseline behavior.
- Feature isolation and performance snapshots still work with async disabled and enabled.

## 10. Optional: Hi-Z First-Frame And Adaptive Visibility Policy

Revisit this after GPU visibility and graph planning are stable.

Implementation steps:

1. Add a first-frame visibility planner for scene loads, camera cuts, and Hi-Z invalidation.
2. Prevent stale or empty Hi-Z data from incorrectly culling visible geometry.
3. Add adaptive Hi-Z cost/benefit tracking.
4. Report whether Hi-Z is skipped, warming up, active, or disabled by policy.
5. Connect policy diagnostics to performance snapshots.

Validation:

- Camera cuts do not incorrectly cull visible geometry.
- Scene reloads do not produce first-frame occlusion artifacts.
- Diagnostics explain when Hi-Z is skipped, warmed up, active, or disabled.

## Recommended Near-Term Order

1. Lock current baseline snapshots and tests.
2. Add render graph resource ownership.
3. Add barrier and layout planning.
4. Add graph diagnostics and inventory export.
5. Add the processed mesh asset pipeline.
6. Harden current GPU scene submission.
7. Move to production pipeline declaration.

Weighted OIT, async compute, and advanced Hi-Z policy should remain conditional work driven by profiling data or visual requirements.
