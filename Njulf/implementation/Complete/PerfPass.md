# Plan: Simple DDGI performance features to production standard

## Context

The v2 GI (authored volumes + camera rings, `fe3ae89`) is functionally healthy in Cornell. The performance roadmap items to implement: blend restructure, classification-driven scheduling, a real async-compute queue, far-field trace tiering with a distance field, and (conditionally) per-tile volume selection. Current costs (Cornell, 1724 probe updates/frame): trace 410 µs, blend 1027 µs, relocate 93 µs (GI total 1.53 ms of 2.5 ms budget), forward opaque 3.17 ms, GPU frame 5.6 ms.

Verified constraints from code:
- Async compute today is **diagnostic-only**: `BuildAsyncComputePlan` hardcodes `supported=false/enabled=false`; no dedicated compute queue exists (`VulkanRenderer.cs:4497-4537`). The graph already models queue-ownership transitions (8) and candidate passes. `IsAsyncComputePassAllowedBySettings` has no arm for the Simple passes (falls through `_ => true`).
- Blend is O(texels × rays): 64-thread workgroup per probe, each of 320 texels iterates all ≤256 rays from storage (`ddgi_simple_blend.comp`).
- Classification results are GPU-only; `_probeInactive` never populated (readback missing); legacy has the readback pattern to copy (`DdgiProbeSchedulerFeedback`, 2-frame latency like `DdgiGpuSchedulerReadbackLatencyFrames`).
- Far-field clipmap (occupancy + DDA + oracle tests) exists and is disabled; `FarFieldStartDistance` is already seamed into `ddgi_simple_trace.comp`.
- Legacy `DdgiGatherTileManager` exists as the model for tile classification.

Prerequisite (separate, already reported): the relocation/classification correctness fixes (backface flag before normal flip, inverted classification criterion, escape magnitude) must land before Phase 2, since scheduling will act on classification results.

Out of scope here: CPU Hi-Z record/barrier cost (~14 ms) — non-GI, its own work item.

## Production standards applied to every phase

- Kill-switch setting per feature (pattern: existing `Effective*` flags), defaulting on once verified, flippable at runtime for A/B.
- Mirror/unit tests + source-contract pins updated in the same commit; no screenshot-only verification.
- New diagnostics surfaced in the perf snapshot so regressions are observable (the recenter investigation showed unobservable subsystems hide bugs).
- Numeric gate vs the Phase-0 baseline recorded per phase; GPU-behavioral changes additionally require a clean Vulkan sync-validation run.


## Phase 1 — Blend restructure: shared-memory ray cache (1 day, ~0.7 ms win)

1. `ddgi_simple_blend.comp`: cooperative-load the probe's ray results into shared memory once per workgroup (≤256 rays × 32 B = 8 KB, fits comfortably), then run the existing irradiance (64 texels) and visibility (256 texels) loops against shared memory. Keep per-texel accumulation order deterministic.
2. Keep math semantics identical; update the source-contract pins; add a mirror equivalence test (accumulation reassociation within epsilon).
3. Gate: Cornell blend ≤ 0.4 ms; screenshot A/B identical; `dotnet test` green.

## Phase 2 — Classification readback + budget recovery (1–2 days; big win in open scenes)

1. Compact GPU→CPU readback of per-probe classification: 1 bit/probe bitset written by the relocate/classify pass (21 k probes ≈ 2.7 KB/frame), ring-buffered with 2-frame latency (model: legacy scheduler feedback). Tag each readback with a volume-table generation; drop stale readbacks on resize/scroll/recenter frames so remapped probe indices never misapply.
2. Populate `_probeInactive` from the readback → the existing scheduler skip (`SimpleDdgiVolumeManager.cs:474-477`) goes live; keep the age-based re-probe (240 frames) so darkness isn't permanent.
3. Fix quota redistribution while in the scheduler: unused per-volume quota (authored 700 of 1024 today) flows to the next volume in priority order, so the 2048 budget is fully used.
4. Diagnostics: `SimpleDdgiInactiveProbeCount`, rays-saved/frame; wire `DdgiProbeRelocationCount`/`ClassifiedInactiveProbeCountEstimate`/`DdgiScrollCount` for the simple path (readback + `ScrollCopyCount` already exists).
5. Tests: scheduler unit tests (inactive skipped, re-probe cadence, generation-mismatch drop, quota redistribution); diagnostics-populated assertion.
6. Gate: verticality scene ≥ 40% of scheduled rays recovered with zero visual diff; Cornell unchanged (closed box ⇒ all probes active).

## Phase 3 — Real async compute queue (3–5 days; riskiest, ~1–1.5 ms frame win)

1. **Queue infra** (`VulkanContext`): select a dedicated compute-capable family at device init; create queue, per-frame command pools/buffers, and a timeline semaphore. Feature flag `Settings.AsyncCompute.Enabled` + device-support check; clean fallback to today's single-queue path (`AsyncComputePlan.Supported` finally becomes real).
2. **Ownership transfers**: implement the queue-family release/acquire barriers the graph already plans diagnostically (EXCLUSIVE sharing; the transition count/summary diagnostics become live). The probe atlases/scratch/params are the transferred resources.
3. **Scheduling**: submit Simple DDGI trace → relocate → blend on the async queue, kicked at frame start to overlap shadow/depth/HiZ raster; graphics queue acquires the atlases before `ForwardPlusPass`. Since the gather already consumes last-frame probe data (passes currently run after forward), keeping 1-frame latency makes the overlap dependency-free — do that, don't invent same-frame consumption.
4. Add the missing `IsAsyncComputePassAllowedBySettings` arm for `SimpleDdgiTracePass/SimpleDdgiRelocateClassifyPass/SimpleDdgiBlendPass` (today they fall through to `true` unintentionally). HiZBuild/AO-blur remain candidates behind their existing settings — enable after DDGI proves the path.
5. Diagnostics: per-queue timestamp pools (`GpuSimpleDdgi*` timings must remain valid on the compute queue), `AsyncComputeEnabledPassCount`, measured overlap estimate.
6. Tests: ownership-transfer contract test (every async-written resource acquired before graphics reads), runtime A/B flip test, and the acceptance bar: **Vulkan sync-validation clean** on both scenes.
7. Gate: GPU frame drops by ≈ GI GPU cost on both scenes; zero visual diff; setting-off path byte-identical to today.

## Phase 4 — Far-field tiering + jump-flood distance field (2–3 days; scalability)

1. Validate the existing occupancy path: enable `FarFieldClipmapEnabled` + `FarFieldForceAll` A/B vs TLAS-only in the verticality scene (the original plan's Phase-2 gate; oracle tests exist). Fix anything the A/B surfaces before optimizing.
2. Jump-flood distance field: `farfield_jumpflood.comp` (7 passes @128³, R16, +4 MB) seeded from occupancy after bake; sphere-march replaces the DDA inner loop (~256 steps → ~32). CPU oracle vs the analytic box SDF (extend `FarFieldClipmapOracleTests`).
3. Production split: default `FarFieldStartDistance` ≈ ring0 outer radius; per-ring override so the 16 m ring can trace far-field-only. Existing FAR_FIELD ray/hit/step counters cover observability; add step-count histogram bucket.
4. Gate: instanced-city stress variant (×10 TLAS instances) — trace µs stays flat vs TLAS-only growth; GI parity within tolerance on the A/B captures; no step-exhausted spike.

## Phase 5 — Per-tile volume selection cache (conditional, 1–2 days)

1. **Measure first**: timestamp the forward-gather delta between `RingCount=1` and `RingCount=3` + authored. If the volume loop costs < ~0.15 ms at 1080p, skip this phase — record the measurement and close it.
2. If justified: compute pass (model: `DdgiGatherTileManager`) classifying 16×16 tiles → 2 volume indices + blend weights per tile; forward reads the tile record instead of looping volumes. The 2-volume record preserves the edge-fade blend so tile quantization can't create seams.
3. Gate: measured forward-opaque reduction; seam inspection with the selected-volume debug view.

## Rollout order & rationale

0 → 1 → 2 → 3 → 4 → 5. Blend first (self-contained, immediate), classification second (needs the correctness fixes anyway and pays for the verticality testing), async third (riskiest — after the GPU workload is at its final shape so overlap tuning isn't redone), far-field fourth (needed before any big-world content lands), tile cache last and only if measured.

## Verification (end-to-end)

1. `dotnet test Njulf/Njulf.sln` green at every phase.
2. Benchmark table (Phase 0) re-run per phase; numbers recorded; no phase regresses another metric by > 5%.
3. Sync-validation clean for Phases 3–4.
4. Final acceptance: verticality scene walk with all features on — GI GPU ≤ 1 ms, GPU frame ≥ 1 ms below baseline, zero visual regressions in the A/B captures, all new diagnostics populated in a Keypad0 snapshot.