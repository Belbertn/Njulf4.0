# Plan: Simple DDGI quality features to production standard

## Context

The v2 GI is correct for opaque surfaces in the forward pass, but the rest of the frame doesn't participate: fog samples the **legacy** probe path (`SampleDdgiAmbientIrradiance`, `fog.comp:104-107`) so it loses GI whenever the simple path is active; particles have no GI at all (`particle.frag` — no DDGI references); rough specular has no probe fallback (black rough reflections where SSR/probes miss); there is no sky-visibility/large-scale occlusion term for the environment fallback (and the gather hard-cuts to black outside all volumes — known bug); far sun shadows are a roadmap item with no substrate wired. Authoring quality is limited by not being able to see probes (a legacy probe overlay exists at `VulkanRenderer.cs:1754-1827` but isn't wired to simple volumes).

Verified integration points:
- Only `forward.frag` + `ddgi_simple_trace.comp` call `SampleSimpleDdgiIrradiance`; the gather lives in `ddgi_simple_shared.glsl` (include-ready).
- Fog already has the probe-ambient pattern (upward-biased normal, froxel position) — just wired to the legacy sampler.
- Specular composition point: `specularIbl` via `EvaluateGlobalReflectionSpecular`/`EvaluateReflectionSpecular` (`forward.frag:2607-2643`), final combine at `:3803`.
- Reflection-probe capture infra exists (`ReflectionProbeCapturesQueued/Completed`) — captures render through forward, so DDGI-lit captures are mostly a scheduling problem.
- Far-field clipmap (occupancy + DDA, oracle-tested) is the substrate for sky visibility and far sun shadows; the jump-flood distance field from the performance plan makes both cheap (DDA fallback works meanwhile).
- Debug-draw infra: `DrawDdgiProbeVolumeOverlay`, `DrawDdgiProbeSamples`, marker budgeting, `DebugDdgiProbeVolumesDrawn` counter — all legacy-path-only today.

Dependencies: the outside-all-volumes environment fallback fix (bug list) folds into Phase 2 here. Phase 3 wants the jump-flood distance field (perf plan Phase 4) but can ship on DDA. Phase 4b hooks the lighting-dirty signal (accuracy plan Phase 2) if present.

## Production standards (every phase)

Kill-switch setting per feature; mirror/contract tests in the same commit; every term observable (debug view + snapshot counter); golden A/B captures per phase on three scenes (Cornell, verticality, and a new "interior" scene with glass, smoke particles, and height fog); no phase regresses the perf baselines by > 5%.

## Phase 0 — Interior test scene + golden captures (½ day)

1. Add an interior scenario to `SampleStressSceneBuilder`: colored room + glass pane (transparent), smoke emitter (particles), height fog enabled, one point light + optional sun. This is the scene where every quality gap below is visible.
2. Capture golden screenshots + snapshots for all three scenes as the reference set.

## Phase 1 — Unified GI sampling: transparents, particles, fog (1–2 days)

1. **Audit transparents**: confirm whether `TransparentForwardPass`/weighted-OIT shade through `forward.frag`'s simple branch; if a separate shader path, port the gather call (bindless params/atlas indices are globally registered, so it's a call + descriptor availability check).
2. **Fog**: in `fog.comp`, read the `SimpleDdgiParams` selector (same authoritative buffer as `forward.frag:3360`) and route the ambient term to the simple gather when active, legacy otherwise; keep the existing upward-biased-normal froxel sampling. Height fog in a red room must tint red.
3. **Particles**: sample GI once per particle (center position, view-oriented wrap normal) in the vertex/instance stage — not per fragment — and modulate the particle's ambient term. Add `SimpleDdgiParticleSampleCount` diagnostic (legacy analog exists).
4. Tests: shader-build + contract pins; per-pass sample counters asserted > 0 in the interior scene snapshot.
5. Gate: interior scene — smoke and glass pick up bounce color; fog tinted; A/B kill-switch reverts each term independently.

## Phase 2 — Sky visibility + fallback continuity (1–2 days)

1. **Fix the hard cut** (bug list item): outside all volumes — and in the outermost ring's edge-fade band — blend the gather toward the environment/sky diffuse term instead of returning black (`ddgi_simple_shared.glsl:533,546-548`).
2. **Sky-visibility term**: `EstimateFarFieldSkyVisibility(worldPos)` — 3 fixed cone directions (up + two 45° tilts) marched through the far-field clipmap (jump-flood sphere-march when available, DDA meanwhile, ~16 step cap). Attenuates the environment fallback so interiors/valleys beyond probe coverage aren't sky-lit. Evaluate per gather-fallback sample (cheap: only runs where no volume contains the point); cache per froxel for fog.
3. Debug view `FarFieldSkyVisibility`; settings (enable, cone count/length); snapshot counters (fallback samples, average sky visibility).
4. Tests: CPU oracle vs analytic roofed-box (interior ≈ 0, open field ≈ 1, doorway gradient monotonic); continuity mirror test across the last-ring boundary (no discontinuity in the blended result).
5. Gate: verticality scene — under-roof/valley darkening visible past ring coverage; no seam at the last-ring face; Cornell unchanged (always inside volumes).

## Phase 3 — Far sun shadows from the clipmap (2 days; roadmap item, same substrate)

1. Screen-space far-shadow term for pixels beyond the last directional cascade: march the clipmap toward the sun (distance-field sphere-march, ~16–32 steps, start offset to skip self), producing an attenuation factor combined where cascade sampling currently ends (`forward.frag` directional shadow resolve). Requires a sun in the verticality scene (Cornell keeps its point light, unaffected).
2. Half-resolution evaluation + bilateral upsample, reusing the AO half-res + blur pipeline pattern; full-res fallback setting for comparison.
3. Debug view `FarFieldSunShadow`; settings (enable, start distance = cascade far plane, max steps); GPU timing counter.
4. Tests: oracle vs analytic occluder column (shadow length/direction); contract pins.
5. Gate: mountains/towers shadow the terrain beyond cascade range; cost ≤ 0.3 ms at half-res; no shimmer under camera motion (stable world-space march).

## Phase 4 — Rough-specular GI fallback (1–2 days)

1. `forward.frag`: for roughness above a threshold band (smoothstep 0.6→0.85), evaluate the probe gather along the dominant reflection direction (`normal = R`) and energy-normalize as a specular ambient term; blend into `specularIbl` by the roughness weight (below the band: reflection probes/SSR as today). Respect metallic F0 — no double count with the diffuse term (which already carries `1−metallic`).
2. **DDGI-lit reflection probes**: verify captures run the simple gather (same forward shader + params buffer — likely already true); then schedule recaptures on GI warmup completion and on the lighting-dirty signal (accuracy plan) via the existing capture queue, giving infinite-bounce specular without new sampling code.
3. Tests: mirror for the roughness blend weights (continuity at band edges); furnace variant with a rough metal sphere (reflectance ≈ F·E within tolerance).
4. Gate: interior scene — rough metal/plastic shows colored reflections instead of black where SSR/probes miss; mirror-smooth surfaces unchanged.

## Phase 5 — Probe visualization & authoring tooling (1 day; can run in parallel any time — recommended early)

1. Extend `DrawDdgiProbeVolumeOverlay` (`VulkanRenderer.cs:1754`) to the simple path: volume AABB wireframes (authored vs ring colors, edge-fade band shading), per-probe markers colored by mode — sampled irradiance, classification (active/inactive), relocation offset vectors, freshness — reusing the existing marker budget (`remainingDetailedProbeMarkers`) and `DebugDdgiProbeVolumesDrawn` counter.
2. Keybind to cycle overlay modes; snapshot counter for markers drawn.
3. Tests: overlay off = zero debug-draw delta; marker budget respected; contract pin on mode enum.
4. Gate: an authored volume can be placed/adjusted in the sample using only the overlay for feedback (dogfood check while building the interior scene).

## Rollout order & rationale

0 → 1 → 5 → 2 → 3 → 4. Unified sampling first — broadest visible win, smallest risk. Tooling (5) early because it de-risks every later phase and the interior-scene authoring. Sky visibility (2) before far shadows (3): same substrate, and 2 also closes the known fallback-continuity bug. Specular (4) last — independent, and benefits from converged, dirty-signal-aware GI if the accuracy plan has landed.

## Verification (end-to-end)

1. `dotnet test Njulf/Njulf.sln` green per phase (new oracles: sky-visibility box, far-shadow occluder, specular furnace).
2. Golden A/B capture review per phase on all three scenes; perf baselines within 5%.
3. Final acceptance: interior scene with everything on — fog/smoke/glass all carry bounce color, rough reflections colored, sun shadows extend past cascade range in the verticality scene, all terms individually kill-switchable and observable in a Keypad0 snapshot.