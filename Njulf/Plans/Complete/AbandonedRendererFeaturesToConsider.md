# Abandoned Renderer Features To Consider

This branch is the simplified/pre-refactor renderer. The abandoned renderer on `main` contained several larger architectural systems that are worth reconsidering here, but they should be reintroduced selectively rather than copied wholesale.

## 1. Render Graph Resource Ownership

The most important abandoned feature was the deeper render graph system: resource registry, declaration compiler, barrier planner, alias planner, image allocator, descriptor planner, diagnostics exporter, and production graph resources.

Why it matters:

- Centralizes image lifetime, layout transitions, barriers, and pass dependencies.
- Reduces manual ownership bugs such as missed render target disposal.
- Makes dynamic resolution, optional passes, and transient render targets easier to reason about.
- Provides the foundation for async compute and memory aliasing.

Recommendation: bring this back in stages. Start with resource ownership and explicit pass declarations before reintroducing full barrier planning or aliasing.

## 2. GPU Scene And GPU-Driven Visibility

The abandoned renderer had `GpuSceneManager`, `GpuSceneBufferSet`, dirty range tracking, visibility buffers, GPU visibility shaders, meshlet expansion, object visibility, prefix/finalize passes, and shadow list generation.

Why it matters:

- Moves culling and draw-list construction away from CPU-side per-frame loops.
- Scales better with large scenes.
- Enables GPU-generated visibility for depth, forward, and shadow passes.
- Reduces CPU bottlenecks as scene complexity grows.

Recommendation: high value, but only after the simplified renderer has stable render graph ownership and diagnostics. This is a major architectural feature.

## 3. Offline Processed Mesh Asset Pipeline

The abandoned renderer had `ProcessedMeshAsset`, `ProcessedMeshAssetBuilder`, and related tests. That points toward render-ready assets generated before runtime instead of doing too much import and layout work during execution.

Why it matters:

- Faster loading.
- More predictable GPU buffer layout.
- Cleaner support for meshlets and GPU-driven rendering.
- Less runtime importer complexity.

Recommendation: worth revisiting before the full GPU scene work, because it supports that direction and can be staged independently.

## 4. Production Render Pipeline Declaration

Files such as `ProductionRenderPipeline`, `ProductionRenderGraphResources`, and graph resource inventory diagnostics suggest the abandoned renderer was moving toward a declarative production pipeline.

Why it matters:

- Separates pass declaration from `VulkanRenderer` orchestration.
- Makes feature toggles cleaner.
- Makes the render pipeline easier to inspect and test.
- Reduces god-object pressure on `VulkanRenderer`.

Recommendation: reintroduce after basic render graph resource ownership exists.

## 5. Async Compute Scheduling

The abandoned renderer had an `AsyncComputeScheduler`.

Why it matters:

- Allows compute-heavy passes to overlap with graphics when legal.
- Relevant for Hi-Z, ambient occlusion, bloom, fog, exposure, particles, and GPU visibility.
- Encourages correct queue ownership and synchronization discipline.

Recommendation: defer until render graph barriers and ownership are solid. Adding async compute too early will increase validation and synchronization risk.

## 6. First-Class Renderer Diagnostics

The abandoned renderer included a renderer diagnostics schema, diagnostics overlay snapshots, graph inventory, diagnostic exporters, and tests.

Why it matters:

- Makes complex renderer behavior observable.
- Helps detect missing resources, bad pass dependencies, memory growth, and stale descriptors.
- Reduces risk before reintroducing heavier architecture.

Recommendation: bring this back early. Diagnostics should come before GPU-driven rendering and async compute.

## 7. GPU Particle Simulation

The abandoned renderer had `ParticleSimulationPass` and `particle_simulation.comp`.

Why it matters:

- Moves particle simulation to the GPU.
- Scales better for VFX-heavy scenes.
- Pairs naturally with async compute.

Recommendation: medium priority. Useful, but less foundational than graph ownership, diagnostics, and GPU scene work.

## 8. Weighted OIT Transparency

The abandoned renderer had `WeightedOitCompositePass` and `weighted_oit_composite.frag`.

Why it matters:

- Improves transparent rendering compared with simple sorted alpha.
- Helps particles, glass, and other VFX-heavy materials.

Recommendation: bring this back if transparency quality becomes a near-term goal. Otherwise defer.

## 9. Impostors And Foliage Batching

The abandoned renderer had `ImpostorGenerator`, `FoliageBatchManager`, and tests.

Why it matters:

- Helps large outdoor scenes.
- Reduces distant geometry cost.
- Works well with GPU visibility systems.

Recommendation: prioritize only if the target game needs large foliage or open environments.

## 10. Adaptive Hi-Z And Visibility First-Frame Planning

The abandoned renderer had `AdaptiveHiZPolicy` and `VisibilityFirstFramePlanner`.

Why it matters:

- Avoids brittle first-frame occlusion behavior.
- Helps tune Hi-Z cost versus benefit.
- Makes occlusion culling less artifact-prone.

Recommendation: revisit after Hi-Z and GPU visibility are stable.

## Suggested Reintroduction Order

1. Renderer diagnostics, schema, and resource inventory.
2. Render graph resource ownership and lifetime.
3. Barrier and layout planning.
4. Processed mesh asset pipeline.
5. GPU scene buffers and dirty range tracking.
6. GPU-driven visibility.
7. Production render pipeline declaration.
8. Async compute.
9. Weighted OIT, GPU particles, and impostors based on game needs.

## Summary

The defining abandoned direction was render graph ownership plus GPU scene rendering. If this simplified branch becomes the long-term base, the best path is to reintroduce those ideas through small vertical slices with validation tests, not by copying the abandoned renderer back wholesale.
