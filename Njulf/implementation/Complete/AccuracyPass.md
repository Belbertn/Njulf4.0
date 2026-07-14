# Plan: Simple DDGI accuracy features to production standard

## Context

The v2 GI (authored volumes + rings) is spatially correct but temporally and physically incomplete: fixed hysteresis 0.97 means any lighting change takes ~100+ frames to propagate; every probe traces 128 rays forever regardless of convergence; the TLAS holds only static geometry so dynamic objects neither block nor bounce light; and emissive coverage needs an audit. Goal: implement the accuracy roadmap (adaptive hysteresis/change detection, lighting-dirty scheduling, adaptive rays, dynamic rigid geometry, emissive completeness) to production standard.

Verified code facts shaping this plan:
- Blend already reads the previous texel value (`ddgi_simple_blend.comp:69-72`) — per-texel change detection slots in directly.
- `raysPerProbe` is a uniform param used for scratch stride + Fibonacci count in trace and blend (`ddgi_simple_trace.comp:59-75`, `blend:61-98`); the update-queue entry (`GPUSimpleDdgiProbeUpdate`: probeIndex, volumeIndex, flags) has room to carry a per-probe ray tier in flags.
- TLAS infra partially exists for dynamics: `AccelerationStructureGeometryDomain.Dynamic` (`AccelerationStructureManager.cs:1085`), refit-vs-rebuild action machinery with `AllowUpdate` flags (`:473-497, :536, :823`); `CollectStaticOpaqueInstances` currently excludes the dynamic domain.
- Emissive at hits is mostly done: hit material emissive incl. emissive-texture sampling (`ddgi_hit_shading.glsl:191-202`) + compact `DdgiAverageEmissive` fallback (`:59-63`). Suspicious: `EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit` reads only `ReadDdgiEmissiveSource(0u)` (`:353`) — single-source? `DdgiEmissiveSourceRevision` exists as a change-tracking pattern.
- The forward estimate counters (`SimpleDdgiAverageSampledIrradianceLuminance` etc.) are wired and populated — usable as automated accuracy oracles.

Dependencies: the relocation/classification correctness fixes (previously reported bug list) are assumed landed. Phase 3 shares a GPU→CPU probe-state readback with the performance plan's classification readback — build that ring once, whichever plan goes first.

## Production standards (every phase)

Kill-switch setting per feature (Effective* pattern); mirror/unit tests + source-contract pins in the same commit; new behavior observable in the perf snapshot; numeric oracle gates recorded per phase; screenshot A/B for visual changes.

## Phase 0 — Accuracy oracle harness (½ day)

1. **Furnace test scenario** in `SampleGlobalIlluminationValidation`: closed box, uniform albedo ρ, constant-radiance environment (or uniform emissive shell). Analytic steady state: sampled irradiance luminance → known constant. Automated check: run N frames headless (benchmark-suite pattern), read `SimpleDdgiAverageSampledIrradianceLuminance` from the snapshot, assert within ±5% of analytic. This catches energy drift from every later change.
2. **Light-toggle scenario**: Cornell with the point light switching every 120 frames; record response latency (frames until sampled luminance reaches 90% of new steady state) as the baseline metric (~expected 100+ frames today).
3. Record Cornell reference numbers (sampled luminance, visibility average) as the regression table.

## Phase 1 — Per-texel adaptive hysteresis / change detection (1 day)

1. `ddgi_simple_blend.comp`: after computing the new cosine-weighted estimate, compare its luminance against the stored texel: relative delta > `ChangeThreshold` (default 0.25) → scale hysteresis by 0.5; delta > `StepThreshold` (default 0.8) → drop hysteresis to 0.3; else keep configured 0.97. The estimate is already ray-averaged and firefly-clamped (luma ≤ 64), so single-ray fireflies are diluted before the comparison. Visibility moments get a gentler variant keyed on mean-distance delta (occluder moved) with lower aggressiveness.
2. Settings: `SimpleDdgiHysteresisChangeThreshold`, `SimpleDdgiHysteresisStepThreshold`, `SimpleDdgiAdaptiveHysteresisEnabled` (default on). Fresh-probe hysteresis-0 path unchanged and takes precedence.
3. Tests: mirror — step-change input converges ≤ 15 frames (vs ~100); sub-threshold noise keeps 0.97 (no pumping); thresholds continuous at boundaries (no oscillation between tiers — use smoothstep between tiers rather than hard steps); contract pins.
4. Gate: light-toggle scenario latency ≤ 0.25 s @60fps; furnace test unchanged; Cornell static screenshots identical (no noise pumping at rest).

## Phase 2 — Lighting-change dirty signal → scheduler boost (1 day)

1. CPU: per-frame hash of the light array (position/direction/color/intensity/enabled/shadow state) + `DdgiEmissiveSourceRevision`; on change set `_lightingDirtyFrames = 30` in `SimpleDdgiVolumeManager`.
2. Scheduler (`BuildUpdateQueue`): while dirty, raise the per-frame capacity (2× `SimpleDdgiProbeUpdatesPerFrame`, clamped to pool size and a hard cap) — do **not** mark probes fresh (adaptive hysteresis converges them; fresh would pop). Step 2b (optional, same phase if cheap): prioritize volumes intersecting the changed lights' influence AABBs.
3. Diagnostics: `SimpleDdgiLightingDirtyFrames`, boosted-capacity counter in the snapshot.
4. Tests: hash sensitivity (each field) / insensitivity (unrelated scene churn); boost bounded and decays; queue determinism.
5. Gate: moving-light scene — visible lag clearly below Phase 1 alone; budget returns to baseline ≤ 30 frames after the light stops.

## Phase 3 — Adaptive rays per probe (1–2 days)

1. Two tiers: FULL (=`SimpleDdgiRaysPerProbe`, 128) for fresh/dirty/unstable probes, MAINTENANCE (default 32) for stable ones. Tier packed into the update-entry flags word; scratch stride stays max-rays (scratch is update-budget-sized, waste bounded); trace uses the entry's count for the Fibonacci set and fill, blend loops the same count (count read from the entry, not the global param).
2. Stability signal: blend accumulates a per-probe luminance-change EMA into the probe-state buffer; the CPU reads it via the shared probe-state readback ring (classification readback from the perf plan — build here if not landed; generation-tagged, 2-frame latency, dropped on scroll/resize). Scheduler assigns tiers: fresh/dirty/high-EMA → FULL; stable N consecutive updates → MAINTENANCE; lighting-dirty (Phase 2) promotes everything to FULL for the dirty window.
3. Tests: mirror — Fibonacci at 32 rays stays uniform (existing distribution test parameterized); constant-field blend energy invariant 32 vs 128 within tolerance; mixed-tier scratch addressing; tier state machine unit test (promote/demote hysteresis so tiers don't flap).
4. Gate: converged Cornell — ≥ 60% probes on MAINTENANCE, sampled luminance within ±2% of Phase-0 reference, rays/frame down ≥ 40%; light-toggle — probes promote to FULL within readback latency and the Phase-1 latency gate still holds.

## Phase 4 — Emissive completeness audit (½–1 day)

1. Confirm/fix `EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit` iterating **all** emissive sources (code reads only source 0 at `ddgi_hit_shading.glsl:353`) — loop over `pc.EmissiveSourceCount` with a small cap, distance-weighted.
2. Verify `sampleMaterialTextures` is enabled for the simple trace path (emissive-texture LOD heuristic at `:191-202`) and pin the no-double-counting contract: `surfaceEmissive` = hit surface's own emission, proxy term = light *received from other* emitters — distinct terms.
3. Wire emissive changes into the Phase-2 dirty hash (revision already tracked).
4. Test + gate: emissive-panel Cornell variant — sampled irradiance vs analytic view-factor estimate within tolerance; toggling the panel meets the Phase-1 latency gate; multi-emitter scene shows both sources contributing.

## Phase 5 — Dynamic rigid geometry in the trace (2–4 days, biggest item, last)

1. **Instance collection**: include dynamic-domain rigid instances (today filtered out by `CollectStaticOpaqueInstances`) in the ray-query instance set with per-frame world transforms; ensure one-time BLAS per dynamic mesh (flags already `PreferFastTrace | AllowUpdate`); upload their `GPUDdgiRayQueryInstance` metadata each frame so hit shading resolves vertices/materials correctly.
2. **TLAS update policy**: transforms-only change → refit (`Update` action, machinery at `:473-497, :823`); instance count/topology change → full rebuild; per-frame budget + diagnostic counters (refit/rebuild counts already partially exist: `AccelerationStructureTlasUpdateCount`).
3. **Probe hygiene under motion**: no fresh resets — moved instances raise the Phase-2 dirty signal scoped to their old+new AABBs (region-priority scheduling), and Phase-1 adaptive hysteresis converges the visibility moments. This is why dynamics goes last: the responsiveness features hide its latency.
4. Tests: oracle scene with an analytically-placed moving box — occlusion follows within M frames, no residual light/shadow at the old position after convergence; TLAS action decision unit tests; dynamic instance metadata round-trip test.
5. Gate: large moving occluder scene — GI shadowing follows ≤ ~0.3 s, no ghosting after convergence, trace µs increase < 20% with 10 dynamic instances, sync-validation clean (TLAS rebuild hazards).
6. Explicitly deferred follow-up (not this plan): skinned meshes (skinned-BLAS refit from the existing GPU-skinned vertex buffer, or capsule proxies).

## Rollout order & rationale

0 → 1 → 2 → 3 → 4 → 5. Change detection first (every later phase's latency gates depend on it), dirty scheduling second (cheap, multiplies Phase 1), adaptive rays third (needs the readback + benefits from stability signals), emissive audit fourth (small, mostly verification), dynamic geometry last (largest, and its motion artifacts are only acceptable once the temporal-response features exist).

## Verification (end-to-end)

1. `dotnet test Njulf/Njulf.sln` green per phase (new: furnace/latency oracles run in the benchmark harness, headless).
2. Phase-0 regression table re-recorded per phase; furnace ±5% and Cornell ±2% luminance invariants never violated.
3. Final acceptance: light-toggle latency ≤ 0.25 s, ≥ 40% ray reduction at rest, moving-occluder scene artifact-free, all with kill-switches off reproducing today's behavior byte-identically.