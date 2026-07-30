# Why GI bounce is still low — diagnosis and fix plan

## Context

Sponza scene on the Simple DDGI path (Transport V2 Jacobi solver), 3 rings / 15,368 probes,
sun Intensity=14, sky 1.0. The user reports indirect bounce is still too dim (dark corridors
vs. bright sun patch) and asked: is it light intensity, or wrong math? Analysis of the four
performance captures + codebase answers this. No edits in this session; this is the plan.

## Answer: it is neither the light intensity nor the shading math

**The math is correct and self-consistent** (verified end to end):
- Trace hit shading: `L = E·albedo/π` — `ddgi_hit_shading.glsl:446`
- Blend irradiance estimator: `E = π·Σ(w·L)/Σw` (correct MC estimator) — `ddgi_simple_blend.comp:236-238`
- Transport bounce: sampled `E·albedo/π` — `ddgi_simple_transport.comp:119-125`
- Forward receiver: `E·albedo·(1-metallic)/π`, deliberately not multiplied by SSAO — `forward.frag:4123`
- No missing/double π anywhere; lights uploaded unscaled; no hidden energy multipliers.

**Sun Intensity=14 is plenty.** Unshadowed direct at probe hits averages 0.18-0.24 luminance.

## Root cause: the Jacobi multi-bounce solver is starved and falsely converged

Two coupled defects give each probe ~1 bounce iteration per 240+ frames instead of 8 per cycle:

1. **False convergence (primary).** The blend shader's residual
   `SimpleDdgiStableRelativeDelta(cur, prev, 0.02)` (`ddgi_simple_blend.comp:69-77`) subtracts
   an **absolute noise floor of 0.02** — the same order as the entire bounce signal in this
   scene (~0.03-0.09 luminance). Per-sweep multi-bounce increments are classified as noise,
   the probe-state EMA decays to ~0, and `IsTransportConverged`
   (`SimpleDdgiVolumeManager.cs:2741-2758`, threshold 0.025) retires probes while energy is
   still building. Captures: 15,331 of 15,368 probes "converged".
2. **Hard scheduler exclusion.** `TryResolveProbeWorkClass`
   (`SimpleDdgiVolumeManager.cs:2698-2702`) removes converged probes from ALL work classes;
   re-entry is only the 240-frame source refresh. Solver-only iterations — which cost **zero
   primary rays** (`AddProbeUpdate` :3189-3190) — almost never run
   (`SourceCacheReuseProbeCount` ≈ 1-2/frame). Result: ~20-26 probes updated/frame out of
   15,368 while the GPU sits at 422µs of a 2250µs target and 2.6k of 262k rays. The captures'
   own warning says it: "DDGI active probe count is too large for the current scheduled
   update rate." Multi-hop propagation (sunlit courtyard floor → walls → corridors) needs one
   full sweep per hop; at this rate convergence takes minutes and is reset by atlas
   clears/recenters (captures were taken 327-2288 frames after a clear).

**Secondary, real but smaller:** the scene's first-bounce source term is genuinely tiny —
86-89% of probe-ray hits return zero radiance, only ~3-4% see direct sun after shadow rays,
probe rays almost never miss so the sky (~0.7 luminance/miss) contributes ~nothing, emissive
is negligible, and effective receiver albedo is ~0.19. Small absolute increments per sweep
are exactly what the 0.02 noise floor eats.

Supporting detail: capture staleness percentiles (`DdgiFramesSinceProbeUpdatedP50/P95`) are
modelled, not measured (:1060-1082); `DdgiRefreshReasonAgeProbeCount` is a labeling artifact
(:1207-1211). A late finding to confirm at runtime: `MarkProbeSourceCacheStale` (:3386-3413)
re-arms `BeginTransportGlobalConvergence()` on every GPU cache invalidation (~2.5/frame in
captures), and while global convergence is pending, `NeedsSourceRefresh` (:2738) suppresses
all periodic refreshes — possible additional cadence suppressor.

---

## Plan

### Stage 0 — Verify at runtime, no behavior change

Capture a 60s steady-state Sponza baseline (Ctrl+R console diagnostics + performance
snapshot) and record:
- `SimpleDdgiTransportSourceRefreshProbeCount` vs `SourceCacheReuseProbeCount` — reuse ≈ 0
  is the smoking gun that solver generations never run.
- SchedulerTelemetry (`SimpleDdgiVolumeManager.cs:625-651` → `GiDiagnosticsContracts.cs:408-441`):
  `PressureReason` (`NoEligibleWork` ⇒ convergence retirement; `FeedbackReducedBudget` ⇒
  the GPU-feedback cap is a co-conspirator), per-class Pending/Reserved/Scheduled,
  `EffectiveRequestBudget` (expect 2048).
- `SimpleDdgiTransportBounceLuminanceAverage` / `SourceLuminanceAverage`,
  `DdgiForwardEstimateFinalDiffuseLuminance` (~0.044 today), `CpuSimpleDdgiRecordMicroseconds`
  (24-30ms — baseline it; later stages must not regress it).
- Debug views: Ctrl+G `DdgiIrradiance`; Ctrl+Keypad9 overlays `DdgiUpdatedProbes`,
  `DdgiProbeAge` (directly visualizes the ~24-probes/frame trickle).

Settings-only experiments (`RenderSettings.cs`; the solver-calibration fingerprint
:3792-3839 makes these live-recalibrating, no restart):
- **E1**: `SimpleDdgiTransportSourceRefreshFrames` 240 → 60. Expect ~4× faster bounce buildup.
- **E2**: `SimpleDdgiStableMaintenanceUpdateCount` 3 → 100 +
  `SimpleDdgiStableMaintenanceEmaThreshold` 0.03 → 1.0 → retirement can never fire.
  Expect `SourceCacheReuseProbeCount` to jump and bounce luminance to climb 2-4× in ~10s.
- **E3**: `SimpleDdgiTransportMaximumSolverGenerations` 8 → 32. Isolates solver-iteration
  deficit from source-term deficit.

Gate: if E2/E3 do not raise bounce materially, stop and re-investigate before Stage 1.

### Stage 1 — Fix the residual metric + retirement criteria (smallest risk)

- `ddgi_simple_blend.comp` residual return paths (:254-256, :394-397): replace the absolute
  0.02 floor with one derived from the already-plumbed `params.transportResidualThreshold`
  (e.g. `max(0.25·threshold, 0.002)`). Leave the adaptive-hysteresis use at :93 alone (it
  serves temporal filtering, not convergence).
- Track absolute AND relative deltas: pack two fp16 EMAs into the existing
  `state.luminanceChangeEma` word (`packHalf2x16`) — touch `ddgi_simple_shared.glsl`
  state pack/unpack, EMA update `ddgi_simple_blend.comp:500-517`,
  `ddgi_simple_relocate_classify.comp:226`, CPU unpack `SimpleDdgiVolumeManager.cs:5207-5210`.
  No buffer-size change.
- `IsTransportConverged` (:2741-2758): generations ≥ max AND stable ≥ count AND
  (relativeEma ≤ threshold OR absoluteEma ≤ ~0.001). The absolute escape hatch prevents
  genuinely black probes from busy-looping the scheduler. Apply the same to the
  stable-count reset at :5234-5245.
- Lower default `SimpleDdgiTransportResidualThreshold` 0.025 → 0.01 (`RenderSettings.cs:4323`).
- Tests: extend `Njulf.Tests/SimpleDdgiVolumeManagerTests.cs` with convergence-predicate
  cases (small-signal growth must not retire; black probe must retire).

### Stage 2 — Guarantee solver-only follow-up generations

- Add `SimpleDdgiSolverOnlyUpdatesPerFrame` (default 512-1024) admitted additionally in
  `BuildUpdateQueue` (:2387-2446) for probes with `!NeedsSourceRefresh && gen < max` —
  ray-free, transport+blend compute only. Keep the GPU-feedback cap
  (`ResolveFeedbackLimitedUpdateBudget` :2164-2204) for ray-costed source refreshes.
- Priority: route pending-solver probes ahead of Far maintenance (targeted branch in
  `TryResolveProbeWorkClass`/`ResolveSchedulerWorkClass` :2674-2779, or raise the
  maintenance reservation above budget/16 in `AllocateSchedulerClassQuotas` :2496-2567).
- CPU risk gate: `CpuSimpleDdgiRecordMicroseconds` is already 24-30ms; extra queue entries
  grow `MarkBlendExecuted` (:1384-1450) and publish (:1487-1560). Cap admissions, total
  ≤ 2048/frame, gate on CPU ≤ baseline +20%; GPU headroom validated by E2.
- Stage 2.5: make `DdgiFramesSinceProbeUpdatedP50/P95/Max` measured from `_probeAges`
  percentiles instead of modelled (`VulkanRenderer.cs:8311-8313`).

### Stage 3 — Background maintenance instead of hard exclusion

Soften :2698-2702: converged probes re-enter their ring maintenance class once
`_probeAges > ~SourceRefreshFrames/4`, but only via the non-reserved second queue pass
(:2428-2437) so they consume strictly leftover budget. Keeps the field warm across
clears/recenters at near-zero ray cost. Land after Stages 1-2 so effects are separable.

### Stage 4 — Secondary energy follow-ups

- **Sky visibility audit**: 0-11 misses per ~2,500 rays is suspicious for an open-courtyard
  Sponza — check with Ctrl+V investigation views whether the cooked model's roof actually
  closes the courtyard (asset issue, not renderer) and that upward rays from courtyard
  probes credit sky on miss.
- **Effective albedo ~0.19**: already covered by
  `implementation/MaterialGiProductionReadinessPlan-20260728.md` (sRGB decode, single
  base-color multiply, mean-energy conformance) — reference, don't duplicate.
- Runtime-confirm the `MarkProbeSourceCacheStale` → global-convergence-pending → periodic
  refresh suppression loop; if real, stop re-arming global convergence for single-probe
  repairs (:3411).

## Success metrics (existing counters)

- `SimpleDdgiTransportSourceCacheReuseProbeCount` ≫ `SourceRefreshProbeCount` in steady state.
- `SimpleDdgiTransportBounceLuminanceAverage` ≥ 3× baseline within ~10s of load;
  `DdgiForwardEstimateFinalDiffuseLuminance` ≥ 0.015 (SampleDdgiProductionGate.cs:33),
  target 0.04-0.07.
- After a lighting change, `TransportConvergedProbeCount` drops to ~0 and recovers
  monotonically — no premature re-retirement.
- `LastCompletedGpuMicroseconds` ≤ 2250µs; `CpuSimpleDdgiRecordMicroseconds` ≤ baseline +20%;
  measured probe-age P95 drops from O(240) to O(30) frames.

## Critical files

- `Njulf/Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs` — scheduler, convergence predicate, budgets, readback
- `Njulf/Njulf.Shaders/ddgi_simple_blend.comp` — residual metric, EMA write
- `Njulf/Njulf.Shaders/ddgi_simple_shared.glsl` — probe state / params ABI
- `Njulf/Njulf.Rendering/Data/RenderSettings.cs` — defaults, new solver-only budget setting
- `Njulf/Njulf.Tests/SimpleDdgiVolumeManagerTests.cs` — scheduler/convergence unit coverage