# Findings: unstable/noisy GI after full plan implementation (`ea7e065` + `3b515d2`)

## Context

User implemented all three plans (performance, accuracy, quality — ~4,400 insertions over 57 files) and reports: GI contribution noisy and unstable; dark areas never get completely dark; dark splotches appear and disappear randomly. Task: verify plan implementation, root-cause the instability, find other issues.

**Note: no new diagnostic snapshots were actually uploaded** — the newest files in the session are from before these commits (12:04/12:05 captures of `fe3ae89`). The analysis below is code-derived; a post-implementation snapshot (ideally two captures a few seconds apart in a dark corner) would confirm the diagnosis via the forward estimate counters.

## Hypotheses under review (three parallel code reviews)

1. **Adaptive-hysteresis feedback loop** (accuracy plan): if the change metric is relative (delta/luminance), dark texels (≈0) see huge relative deltas from pure noise → hysteresis permanently dropped → texel tracks the noisy per-frame estimate → splotches that never converge, dark floor from one-sided (≥0) noise.
2. **Adaptive-ray-tier interactions**: 32-ray maintenance estimates have 4× the variance; tier flapping (stability EMA computed from noisy estimates) and Fibonacci-set changes between tiers produce oscillating biased estimates; scratch stride/count mismatches would read stale/garbage rays.
3. **Async-compute race** (performance plan): missing/wrong semaphore or queue-family release/acquire pair → forward samples partially-written atlases non-deterministically = classic random appearing/disappearing splotches.
4. **Energy floor** (quality plan): rough-specular fallback / sky-visibility environment fallback / weight floors adding light where it should be black ("never completely dark").

## Snapshot analysis (Sponza-style "Strut" scene, 2 captures 29 s apart)

- 3 rings only (no authored volume), 20 736 probes, ring0 24×12×24 @1 m over the courtyard; never recentered.
- **Dirty signal pinned on**: `SimpleDdgiLightingDirtyFrames = 30` in both captures, `DirtyReasonFlags = 2`, `DdgiEmissiveSourceRevision = 2501` and climbing ⇒ emissive revision churns ~every frame ⇒ permanent boost (4096 probes/frame, 524 288 rays), `MaintenanceRayProbeUpdateCount = 0` (adaptive tiers never engage in this scene).
- **Emissive at hits**: 23 emissive sources; 65 k of 213 k hits (~30%) register emissive vs 2–5% direct-sun hits.
- **Rough specular fallback**: 5.26 M samples/frame, 98% nonzero ⇒ fires at every fragment incl. overdraw; forward opaque exploded to 36.4 ms (GPU frame 46.7 ms).
- **GI memory 524 MB** vs 100.7 budget while "DDGI total memory" = 112.7 MB ⇒ ~410 MB unaccounted under the GI budget category.
- Async compute: implemented (`Supported=1`) but **disabled by settings** ⇒ exonerated for the instability. Sky visibility / far-field / far sun shadows: zero samples ⇒ inactive in this scene (unverified, not guilty).
- Classification readback works (1 928 inactive probes, ~700 skipped/frame, `SavedRaysPerFrame` ≈ 82–90 k).

## Findings — temporal stability review (agent 1, complete)

**PRIMARY ROOT CAUSE (CONFIRMED): relative-delta explosion near zero in adaptive hysteresis.** `ddgi_simple_blend.comp:53-64` — `relativeDelta = |cur−prev| / max(max(prev,cur), 0.01)`; for dark texels any noise ≥0.01 luma gives delta≈1 ⇒ hysteresis collapses 0.97→0.30 ⇒ texel takes 70% of a fresh noisy estimate every frame, self-reinforcing (prev is itself noise). No absolute-delta floor. Explains both "never black" (tracks E[noise]>0, lifted by bounce feedback/emissive/env) and "random splotches" (temporal σ ≈ 0.65·σ_estimate). Fires at all ray tiers.

Secondary (all confirmed/structural):
1. **No tier-aware hysteresis floor** — 32-ray maintenance estimates (4× variance) get the same fast blend; design responds to its own noise as if it were change.
2. **Tier flapping**: stability EMA from texel 0 only (`blend:230-244`), same near-zero relative formula, tiny 0.03 threshold with hard reset and no promote/demote band (`SimpleDdgiVolumeManager.cs:1774-1780, 839-842`); 32-ray Fibonacci set is not a subset of the 128 set ⇒ tier flips shift the estimate; control loop has 3-4 frames latency (EMA read in relocate, before blend writes) ⇒ bang-bang oscillation.
3. **Visibility moments fast-blended too** (`blend:67-76,173`): pow(dot,16) ⇒ ~1-2 effective rays/texel at 32 rays ⇒ noisy Chebyshev ⇒ flickering weights near occluders.
4. **AO decoupled from GI**: forward changed to `ddgiDiffuse + diffuseIbl*indirectAo` — DDGI diffuse no longer multiplied by AO ⇒ direct "never fully dark" contributor (pinned by a mirror test as intended!).
5. Recursive bounce feedback loops the noisy floor back through the trace.
6. Emissive dirty-reason lags a frame; **emissive revision churn** (snapshot) keeps the dirty window pinned in scenes WITH emissive sources — needs an only-bump-on-change fix.
7. Classification criterion now backfaceRatio-based (earlier inversion fixed); all-miss (sky) probes = ACTIVE — correct.

Explicitly NOT the bug: scratch/ray-count aliasing (trace/blend/relocate all use the entry ray count consistently); dirty-signal hashing is camera-independent and quantized (quiesces in Cornell — but see emissive churn above).

Tests: new mirror tests pin the buggy behavior (hysteresis test only uses previousLuma=1.0; constant-field-only tier test; latency oracles reward fast blend). No convergence-at-rest / dark-region temporal-variance oracle exists — the exact failing scenario is untested.

## Findings — async/perf review (agent 2, complete)

- **Async compute exonerated for today's symptom** (off by default) and the probe-atlas hand-off is actually correct: matching QFOT release/acquire pairs for the 7 DDGI buffers, monotonic timeline semaphore chain, forward serialized behind this frame's blend, per-frame pools fenced. Blend shared-memory restructure verified clean (sizing/barrier/bounds). Readback ring verified (generation tags dropped on remap/scroll before reads).
- **CONFIRMED latent hole**: when async IS enabled, compute reads TLAS/material/light/far-field EXCLUSIVE buffers with **no queue-family acquire** — undefined contents per spec (driver-dependent corruption). Only the 7 hand-picked buffers are transferred (`SimpleDdgiVolumeManager.cs:147-155`). The contract-string tests would pass with this hole intact.
- imageAvailable semaphore waited on the setup submit, not the late submit that writes swapchain — spec-questionable, would show as tearing not GI noise.
- **Always-on instability contributors** (independent of async): (a) 32-ray maintenance frames can leave irradiance texels with weightSum≈0 ⇒ blend writes vec3(0) ⇒ texel goes dark then recovers (visibility keeps-previous, irradiance does NOT); (b) classification skip freezes probes up to 240 frames on noisy 2-frame-latent classification ⇒ stale/dark cycles.
- **No master revert switch**: async off ≠ pre-plan behavior; adaptive rays/hysteresis/classification/blend-cache all have independent flags defaulting true.
- Jump flood: algorithmically correct (seed/ping-pong/conservative march), off by default; plain JFA without refinement (minor over-estimates ⇒ small leaks, brighter not darker).
- Perf plan delivery: blend restructure ✔, readback ✔, async partial/unsound (QFOT gap), jump flood ✔ (off), **tile cache SKIPPED** — and Sponza's 36 ms forward now demonstrates it's needed.

## Consolidated root-cause model

**Splotches appear/disappear (temporal):** (1) adaptive-hysteresis relative-delta explosion near zero — PRIMARY, all tiers; (2) irradiance texels written to zero on weightSum≈0 maintenance frames; (3) classification-skip stale/dark cycles; (4) tier flapping (texel-0 EMA, no band, different Fibonacci sets, 3-4 frame loop latency); (5) visibility-moment fast blend ⇒ Chebyshev flicker; (6) rough-specular per-pixel gather amplifies probe noise on screen.

**Dark areas never black (energy):** (1) AO no longer multiplies DDGI diffuse (`forward.frag:3538`); (2) rough-specular fallback missing ÷π + `max()` additive floor; (3) E[one-sided noise] floor from fast blending; (4) bounce feedback recirculates the floor; (5) pre-existing 1e-5 gather weight floor; (6) Sponza-specific: 23 emissive sources inject 30% of hit radiance (legitimate content but destabilized by 1-3).

**Sponza dirty-pin + perf:** emissive revision churns every frame (revision 2501) ⇒ dirty window pinned ⇒ 2× budget (524 k rays), all-FULL tiers, GI 4.1-4.8 ms (over budget); rough-specular = second full gather per fragment (5.26 M samples) ⇒ forward 36.4 ms; **GI memory 524 MB with only 113 MB accounted — prime suspect: jump-flood/far-field buffer sizing (e.g., distance ping-pong at wrong dims), needs verification.**

## Final fix plan (priority order)

**Phase 1 — Temporal stability (1 day):** absolute luminance floor gating all three change metrics (blend:60, :72, :153), optionally scaled 1/√rayCount; pass activeRayCount into adaptive hysteresis — at 32 rays never drop below base; irradiance texels with weightSum≈0 keep previous value (like visibility); verify `SimpleDdgiAdaptiveHysteresisEnabled=false` fully restores fixed 0.97. Gate: static Sponza/Cornell dark corners — temporal σ under threshold, splotches gone.

**Phase 2 — Energy floor (½-1 day):** restore AO on GI diffuse (`(ddgiDiffuse + diffuseIbl) * indirectAo`, or make the convention an explicit setting); specular fallback ÷π, replace `max()` with a proper blend, move out from behind the env-enabled gate (gate on probeCount>0), early-skip the gather below the roughness band. Gate: lights-off scene renders pitch black; furnace unchanged.

**Phase 3 — Un-pin the dirty signal (½ day):** bump emissive revision only on actual content change (hash-compare), fix the read-before-increment ordering; test: static multi-emitter scene decays `LightingDirtyFrames` to 0. Gate: Sponza rays/frame drops ~4×, maintenance tier engages, GI GPU back ≤ 2.5 ms.

**Phase 4 — Tier/classification stabilization (1 day):** whole-probe EMA (not texel 0); promote/demote hysteresis band, no hard reset; make the 32-ray set a stratified subset of the 128 set; cap classification-skip window (~60 frames), skip only after N consistent inactive readbacks, staggered re-probe. Gate: converged Cornell ≥60% maintenance with zero tier-flap oscillation; no stale-dark cycles.

**Phase 5 — Perf regressions (1-2 days):** specular fallback reuses the diffuse gather's volume selection/probe reads (skip second selection + Chebyshev); hunt the ~410 MB GI-category allocation (verify jump-flood distance buffer dims + copy-temp size); then implement the deferred tile-selection cache (now justified by 36 ms forward). Gate: Sponza forward back to scene-appropriate cost; GI memory < 100 MB.

**Phase 6 — Oracles that would have caught this (1 day):** dark-region temporal-variance oracle (static scene, N dark pixels over M frames, adaptive features ON); lights-off→black oracle measuring FINAL composed luminance (current metric is upstream of both energy bugs); extend hysteresis mirror tests to previousLuma≈0; unpin the tests that enshrine the buggy behavior; implement or remove the reflection-recapture scaffold.

**Phase 7 — Async hardening (before enabling, 1-2 days):** extend the QFOT set (or CONCURRENT-create) to every compute-read buffer (TLAS/material/light/far-field); move the imageAvailable wait to the late submit; replace contract-string tests with a sync-validation CI smoke. Async stays off until then.

## Findings — quality/energy review (agent 3, complete)

Energy paths that keep dark areas lit (ranked; first two fire in Cornell and Sponza):
1. **CONFIRMED — AO removed from the DDGI diffuse term** (`forward.frag:3538`): `(ddgiDiffuse + diffuseIbl) * indirectAo` → `ddgiDiffuse + diffuseIbl * indirectAo`. Corners/contact regions lose their occlusion on the GI term (≈3× brighter where AO≈0.3). Matches the hybrid path's convention, so possibly deliberate — but it is the mechanism that removed corner darkening.
2. **CONFIRMED — rough-specular fallback over-bright and additive-only** (`forward.frag:2715-2723`): uses hemispherical irradiance as lobe radiance (missing ÷π ⇒ ~3.14× hot) and `mix(specularIbl, max(specularIbl, fallback), w)` can only raise — a hard glow floor on every rough surface (Cornell walls roughness 0.82-0.92 ⇒ weight 1.0). Correctly BRDF-weighted and no env-fallback double-add; the defects are the missing /π and the max().
3. Env fallback in the ring fade band adds an indoor floor wherever an env cubemap is bound (by design for the hard-cut fix; zero in env-off scenes).
4. Pre-existing: gather weight floor (`trilinear*1e-5`) leaks mean probe irradiance into fully-occluded points (untouched by these commits).
5. Emissive multi-source loop is correct but raises the GI baseline in multi-emitter scenes (Sponza: 23 sources, 30% of hits) — legitimate energy, but it's what the unstable blend fails to settle on.

Other:
- **Rough-specular fallback is dead when the environment is disabled** (sits behind `EvaluateIbl`'s env-enabled early-return, `forward.frag:2672`) — feature/gate mismatch.
- **Sky visibility is a no-op in every scene without a bound env cubemap** (attenuates a fallback that's 0; `environmentRadiance` hardcoded to 0 in params, `SimpleDdgiVolumeManager.cs:418`); costs 3 cone traces/pixel when active.
- **Reflection-probe "recapture" is a scaffold**: `DrainCaptureQueue` decrements counters, renders nothing (`ReflectionProbeManager.cs:141-149`).
- **The accuracy oracles are blind to both regressions**: the luminance metric is packed from the raw probe gather (`forward.frag:1957`), upstream of the AO composition and specular fallback; furnace has no dark regions; the benchmark unit test feeds synthetic diagnostics. No lights-off→black or dark-percentile oracle exists.

Quality-plan delivery: fog ✔ (simple-path routed, intensity once), particles ✔, transparents ✔ (shared forward path — inherits findings 1-2), sky visibility ~ (delivered but inert without env cubemap + far-field on), far sun shadows ✔ (correctly gated, directional-only, beyond last cascade), rough specular ✔ but buggy (÷π, max, env gate), probe overlay ✔ (debug views 122/123), emissive audit ✔, interior scene ~ (depends on the recapture scaffold + env default).