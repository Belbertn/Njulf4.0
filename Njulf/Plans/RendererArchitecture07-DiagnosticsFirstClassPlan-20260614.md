# Renderer Architecture 07 - First-Class Diagnostics And Performance Telemetry Plan - 2026-06-14

Target outcome: diagnostics are part of the renderer architecture. Every major CPU cost, GPU pass cost, memory category, upload path, visibility decision, resource lifetime, and debug overlay has structured data, budget evaluation, and a clear rendering/debug path.

Diagnostics should help maximize performance without becoming a production cost. Shipping builds can compile or configure expensive overlays out, but the data model must remain coherent and testable.

## Non-Negotiable Requirements

1. Diagnostics data is structured, versioned, and exportable.
2. GPU timings are asynchronous and never block the frame loop.
3. Memory diagnostics include actual allocation data where possible, not only estimates.
4. Visual overlays are real rendering passes using real data, not placeholder text.
5. Every performance feature added by the architecture plans exposes counters proving its value.
6. Old duplicated or misleading diagnostics are renamed or removed.
7. Budget failures are actionable and tied to categories.

## Phase 0 - Diagnostics Schema

1. Version `RendererDiagnostics`.
2. Split diagnostics into categories:
   - frame timing.
   - CPU pass timing.
   - GPU pass timing.
   - GPU memory.
   - upload/staging.
   - render graph.
   - GPU scene.
   - visibility and culling.
   - LOD/impostors/foliage.
   - lighting.
   - shadows.
   - particles.
   - post and resolution.
   - debug overlays.
3. Add schema version to performance snapshots.
4. Add compatibility handling for old snapshot files.
5. Remove or rename ambiguous fields.

Acceptance criteria:
- A snapshot consumer can identify schema version and parse categories safely.

## Phase 1 - CPU Timing

1. Track CPU timing for:
   - frame begin.
   - acquire.
   - scene update.
   - dirty upload preparation.
   - graph compilation.
   - graph execution/recording.
   - per-pass recording.
   - submit.
   - present.
   - fence waits.
2. Distinguish:
   - active CPU work.
   - waiting on GPU.
   - waiting on presentation.
   - graph recompilation.
3. Add rolling averages and latest-frame values.
4. Add budget status per timing category.

Acceptance criteria:
- CPU bottlenecks can be classified without external profiler for first-pass triage.

## Phase 2 - GPU Timing

1. Keep timestamp queries asynchronous.
2. Track:
   - supported.
   - enabled.
   - pending.
   - valid.
   - disjoint/reset state if applicable.
3. Report per-pass GPU time.
4. Add queue-specific timing after async compute:
   - graphics queue.
   - compute queue.
   - transfer queue.
5. Track overlap and waits where timeline semaphore data allows it.
6. Add tests for timing aggregation math.

Acceptance criteria:
- GPU timings never force same-frame query waits.
- Missing timings include an explicit reason.

## Phase 3 - GPU Memory Diagnostics

1. Integrate VMA allocation tracking by category:
   - mesh buffers.
   - material buffers.
   - GPU scene buffers.
   - visibility/compaction buffers.
   - textures.
   - render graph transient images.
   - persistent history images.
   - shadows.
   - environment.
   - reflection probes.
   - particles.
   - staging/upload.
   - debug tooling.
2. Add `VK_EXT_memory_budget`/VMA heap budget reporting where available.
3. Preserve estimates for unsupported platforms but label them as estimates.
4. Track high-water marks.
5. Track transient aliasing savings.
6. Add budget evaluation per heap and category.

Acceptance criteria:
- Memory pressure can be tied to specific systems.
- Actual heap budget is reported when supported.

## Phase 4 - Upload And Streaming Diagnostics

1. Track per-frame upload bytes by source:
   - transforms.
   - materials.
   - meshes.
   - textures.
   - particles.
   - visibility buffers.
   - diagnostics/debug.
2. Track staging ring:
   - capacity.
   - used bytes.
   - high-water mark.
   - wrap count.
   - overflow attempts.
3. Track asset streaming:
   - queued uploads.
   - completed uploads.
   - evictions.
   - failed budget requests.
4. Add warnings when uploads exceed target budgets.

Acceptance criteria:
- Upload spikes are visible and attributable.

## Phase 5 - Render Graph Diagnostics

1. Report compiled pass order.
2. Report culled passes and reasons.
3. Report resource lifetimes.
4. Report transient memory peak.
5. Report alias groups.
6. Report generated barriers per pass.
7. Report resource resolution classes and actual dimensions.
8. Report graph compile time.
9. Add debug export:
   - JSON.
   - optional DOT graph.

Acceptance criteria:
- A graph snapshot explains why every pass ran and which resources it used.

## Phase 6 - Visibility Diagnostics

1. Track:
   - total objects.
   - visible objects.
   - frustum culled.
   - occlusion tested.
   - occlusion rejected.
   - LOD distribution.
   - impostor count.
   - foliage clusters tested/visible.
   - meshlet candidates.
   - meshlets emitted by pass.
   - transparent sort count.
   - compacted buffer overflow.
2. Reconcile counters so emitted/rejected/tested totals make sense.
3. Add sanity checks for impossible combinations.
4. Add debug overlays:
   - object bounds.
   - meshlet bounds.
   - selected LOD.
   - occluded bounds.
   - impostor selections.
   - foliage clusters.

Acceptance criteria:
- Culling and LOD effectiveness are measurable and visible.

## Phase 7 - Lighting, Shadow, And Tile Overlays

1. Add light tile data output suitable for visualization.
2. Add overlay modes:
   - local light count per tile.
   - max light pressure.
   - shadowed light influence.
   - empty tiles.
3. Add shadow diagnostics:
   - selected lights.
   - cascade bounds.
   - casters per cascade.
   - local shadow atlas occupancy.
   - point shadow face masks.
4. Add meshlet line overlays for shadow casters.
5. Ensure overlays use real buffers/counters.

Acceptance criteria:
- Lighting and shadow pressure can be inspected visually in engine.

## Phase 8 - Pass Timing And Memory Visual Overlays

1. Add in-engine overlay renderer for:
   - pass timing bars.
   - GPU memory categories.
   - transient graph memory.
   - upload pressure.
   - resolution scale.
   - queue overlap.
2. Use compact GPU/CPU data uploaded to a debug overlay buffer.
3. Avoid text-only placeholders where a visual overlay is requested.
4. Add capture/export support so overlay data can be saved with snapshots.
5. Ensure debug overlays are disabled or low-cost in shipping profiles.

Acceptance criteria:
- Pass timing and memory can be viewed visually without external tools.

## Phase 9 - Budget Profiles

1. Define target platform profiles:
   - low.
   - medium.
   - high.
   - ultra.
   - development stress.
2. Budgets include:
   - CPU frame time.
   - GPU frame time.
   - pass time.
   - GPU memory.
   - transient memory.
   - upload bytes.
   - draw/meshlet counts.
   - light tile pressure.
   - particle count.
   - shadow count.
3. Add budget evaluator.
4. Add status levels:
   - ok.
   - warning.
   - over budget.
   - invalid/unmeasured.
5. Add tests for budget math.

Acceptance criteria:
- Snapshots show whether performance is inside target profile budgets.

## Phase 10 - Automated Stress Scenes

1. Add deterministic stress scenes:
   - many static objects.
   - many skinned objects.
   - dense foliage.
   - impostor transition field.
   - heavy local lights.
   - shadow-heavy scene.
   - transparent/OIT scene.
   - particle-heavy scene.
   - post-processing/dynamic-resolution scene.
2. Each scene exports a performance snapshot.
3. Add optional CI thresholds for machine-independent counters.
4. Keep GPU time thresholds manual unless CI hardware is fixed.

Acceptance criteria:
- Major systems have reproducible performance workloads.

## Phase 11 - Remove Old Diagnostics

Delete or replace:

1. Ambiguous counters whose meaning changed.
2. Duplicate CPU/GPU timing fields.
3. Estimate-only memory fields once actual allocation fields are available, unless clearly labeled.
4. Debug modes that no longer correspond to real renderer data.
5. Snapshot fields for removed CPU render-list architecture.
6. Any overlay that renders placeholder data.

Acceptance criteria:
- Diagnostics describe the current architecture, not historical implementation details.

## Validation

1. Unit tests:
   - snapshot serialization.
   - budget evaluation.
   - counter reconciliation.
   - memory category aggregation.
   - timing aggregation.
2. Integration tests:
   - all debug overlay modes.
   - snapshot export.
   - GPU timing unsupported path.
   - memory-budget unsupported path.
3. Manual validation:
   - RenderDoc capture labels match pass diagnostics.
   - vendor profiler pass times are reasonably aligned with engine timestamps.
   - overlays match underlying counters.

## Definition Of Done

1. Diagnostics are structured, versioned, and exported.
2. CPU/GPU timing, memory, upload, graph, and visibility data are complete.
3. Visual overlays use real renderer data.
4. Budget profiles classify performance health.
5. Old misleading diagnostics and placeholder overlays are removed.
