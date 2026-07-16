# DDGI Bounce Lighting: Instability, Arch Over-Brightness, Dark Wall Blobs — Diagnosis & Fix Plan

## Context

The simple-DDGI path (4 volumes: authored 1m slab Y[9,19] + 3 camera rings at 1/4/16m spacing, 31,890 probes, 2048 updates/frame round-robin) shows three symptoms in the Sponza plaza scene:
1. Bounce light pulses slowly (seconds-scale); sometimes dims, sometimes goes fully black and recovers.
2. Too bright under the arches.
3. Top-left wall covered in dark blotches.

The three performance captures quantify it: average sampled irradiance luminance swings 0.41→0.55→0.52 between captures; `DdgiBlackFrameSuspect=1` in all three (engine warning fired); trace hit-rate varies 54–72%; emissive hits ±40%/frame; 3 recenters but only 2 atlas-preserves; 212 sampled pixels have zero indirect AND zero IBL. User decisions: fix all three symptoms; use a budget-limited shadow ray for emissive occlusion.

## Root causes (all verified in code)

### 1. Slow pulsing
- **Globally-shared per-frame ray rotation.** One rotation quaternion per frame for all 31,890 probes (`SimpleDdgiVolumeManager.cs:620` → `BuildFrameRotation(_frameIndex)`; used at `ddgi_simple_trace.comp:105` with no probe-dependent term). Monte-Carlo error is spatially correlated across the whole field and shifts coherently frame-to-frame → field-wide brightness ripple instead of per-probe noise that would average out.
- **Adaptive hysteresis chases that noise.** `SimpleDdgiAdaptiveIrradianceHysteresis` (`ddgi_simple_blend.comp:76-93`) cuts hysteresis from 0.97 to 0.485…0.30 whenever a texel's relative luminance delta exceeds 0.25 — but with 96 rays/probe and small bright sources (23 emissive proxies, sun through openings), 25% texel deltas are routine MC noise, not lighting change. The probe snaps to noisy estimates, which feed back into other probes via the bounce term (`ddgi_simple_trace.comp:153` samples the same single-buffered atlas the blend writes) → under-damped, slow oscillation.
- **Maintenance/full ray flip-flop.** Stable probes drop to ~¼ rays (maintenance), whose noisier estimates push `luminanceChangeEma` over the 0.03 stability threshold (`SimpleDdgiVolumeManager.cs:2900-2908`), flipping them back to full rays — visible as `MaintenanceRayProbeUpdateCount` oscillating 179↔382 across captures.

### 2. Full blackouts
- Atlas clears (`ClearAtlasBuffersIfRequired`, `SimpleDdgiVolumeManager.cs:2345-2377`) zero both atlases; `AtlasClearCount=2` in the captures. Triggers include atlas buffer reallocation (`EnsureBuffer(..., invalidateAtlas:true)`, ~line 2046-2074). Refill takes the full round-robin period (P95 41 frames, max 54) → black-then-recover over ~1s.
- While `_atlasFresh`, hysteresis is forced to 0 **globally** (`SimpleDdgiVolumeManager.cs:603`), nuking temporal history even for probes that kept valid data (the per-probe FRESH flag already returns hysteresis 0 in `SimpleDdgiCadenceAdjustedHysteresis`, `ddgi_simple_blend.comp:111-112` — the global override is redundant and harmful).
- Recenter scroll marks whole ring faces FRESH/SCROLL_EXPOSED; `SimpleDdgiProbeSupportsGather` (`ddgi_simple_shared.glsl:659-669`) rejects them → ownership dips in waves. The fallback that fills the gap is `diffuseIbl × (1-ownership)`, and the scene sets `Environment.DiffuseIntensity = 0.10` (`SamplePlazaGlobalIllumination.cs:118`) — near-black. So every support dip reads as "GI turned off".

### 3. Arches too bright
- Emissive proxies are added at ray hit points with **zero occlusion** (`ddgi_hit_shading.glsl:359-388`): every hit within a source's radius gets `radiance · nDotL · (1-d/r)² · albedo/π` straight through vaults/columns. (Direct sun DOES trace shadow rays, `:355`.) With 23 sources this both over-brightens arcade interiors and is the largest per-frame variance source (emissive hit counter ±40%).
- Secondary: the DDGI diffuse term deliberately gets no screen AO (`forward.frag:3563-3565` — only the env fallback is AO'd), so leaked probe light under arches isn't attenuated.

### 4. Dark wall blobs
- **Ring-volume probes can never deactivate**: `activeFloor = 0.35` for non-authored volumes (`ddgi_simple_relocate_classify.comp:133-134`) while classification INACTIVE requires `activeWeight ≤ 0.05` (`:175-177`) — unreachable. A probe buried in masonry stays "supported".
- **Numerator-only visibility normalization**: the gather accumulates `irradiance · directionalWeight · chebyshevVisibility` but normalizes by `directionalMass` (no visibility) (`ddgi_simple_shared.glsl:727-731, 750-752`). A buried probe's Chebyshev ≈ 0 kills its energy but its weight stays in the denominator → dark blob stamped around its grid position; ownership stays ~1 so the env fallback never fills it.
- In the authored slab (activeFloor=0) probes inside walls DO classify inactive; where all 8 cell corners are inactive, ownership=0 and the pixel falls back to the near-black 0.10-intensity IBL → the flat dark region (matches the 212 zero-DDGI-zero-IBL samples).

## Fix plan

### Fix 1 — Per-probe ray rotation (pulsing)
In `ddgi_simple_trace.comp:105`, compose the global frame rotation with a deterministic per-probe rotation (hash of the probe's global index → quaternion or golden-angle azimuth/inclination offsets). Add the helper in `ddgi_simple_shared.glsl` next to `SimpleDdgiFibonacciDirection` (line 439). No blend/classify changes needed — they consume stored ray directions from the ray buffer. Keep the C# mirror in sync: `SimpleDdgiFibonacciDirection` is mirrored and tested in `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs` (mirror lives in Njulf.Rendering/Resources; add the same helper + a distribution test).

### Fix 2 — Stop adaptive hysteresis chasing noise (pulsing)
`ddgi_simple_blend.comp:76-93` + params plumbing in `SimpleDdgiVolumeManager.cs` (~line 605-630, `BuildFlags` ~2684):
- Gate the hysteresis reduction on the CPU's real lighting-change signal: only allow reduced hysteresis while `_lightingDirtyFrames > 0` (signal already exists, `SimpleDdgiVolumeManager.cs:888-891`); pass it as a params flag.
- Defense-in-depth retune: raise `SimpleDdgiHysteresisChangeThreshold` 0.25→0.5 and floor `stepHysteresis` 0.30→0.60 (`RenderSettings.cs:1483-1484`, blend lines 90-91), so even when active the snap is bounded.

### Fix 3 — Blackout handling (blackouts)
`SimpleDdgiVolumeManager.cs`:
- Remove the global hysteresis-0 override at line 603 (`hysteresis = _atlasFresh ? 0 : …`) — per-probe FRESH already handles it; probes with surviving data keep their history.
- After any atlas clear (`_atlasClearRequired` path, `:2345-2377`), trigger the existing lighting-dirty boosted budget (`ResolveLightingDirtyUpdateBudget`, `:988-997`) so refill converges in a handful of frames instead of 54+.
- On atlas buffer growth (`EnsureBuffer(..., invalidateAtlas:true)`, `:2046-2074`), copy the old buffer contents into the new allocation instead of clearing, when the probe layout is compatible.

### Fix 4 — Dark blobs (gather + classification)
- `ddgi_simple_shared.glsl` gather (`SampleSimpleDdgiVolumeGather`, 671-754): treat visibility-dead contributions as unsupported — if `transportVisibility < ~0.05`, skip the probe entirely (exclude from `validMass`/`directionalMass`/`visibleMass`), so the remaining visible probes own the cell. Preserve current behavior when all 8 fail (empty result → env fallback). Mirror any logic that `SimpleDdgiShaderMirrorTests.cs` covers.
- `ddgi_simple_relocate_classify.comp:133-134`: let saturated-invalid ring probes deactivate: `activeFloor = (kind == AUTHORED || hardInvalidProbeScore >= 0.95) ? 0.0 : 0.35`. Buried ring probes then classify INACTIVE and are excluded by `SimpleDdgiProbeSupportsGather` like authored ones.
- Tuning note (flag to user, don't silently change): `Environment.DiffuseIntensity = 0.10` makes any remaining ownership gap nearly black. Consider a modest bump (e.g. 0.2-0.25) or enabling `FarFieldSkyVisibilityEnabled` so the fallback can be sky-occluded and raised safely.

### Fix 5 — Emissive proxy shadow ray (arches)
`ddgi_hit_shading.glsl` (`EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit`, 359-388): accumulate per-source contributions, track the dominant (highest-luminance) source, and trace **one** visibility ray to it (reuse `TraceLightVisibility`, `:217-241`). Apply that visibility to the dominant source's contribution; leave the remainder unshadowed (bounded cost: +1 ray per hit, ~+0.2-0.4ms worst case within the 2.5ms GI budget). Skip the ray when total emissive contribution is below the existing 1e-4 luminance threshold pattern.

## Files to modify
- `Njulf/Njulf.Shaders/ddgi_simple_trace.comp` (Fix 1)
- `Njulf/Njulf.Shaders/ddgi_simple_shared.glsl` (Fix 1 helper, Fix 4 gather)
- `Njulf/Njulf.Shaders/ddgi_simple_blend.comp` (Fix 2)
- `Njulf/Njulf.Shaders/ddgi_simple_relocate_classify.comp` (Fix 4)
- `Njulf/Njulf.Shaders/ddgi_hit_shading.glsl` (Fix 5)
- `Njulf/Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs` (Fix 2 flag plumbing, Fix 3)
- `Njulf/Njulf.Rendering/Data/RenderSettings.cs` (Fix 2 threshold defaults)
- `Njulf/Njulf.Rendering/Data/GPUStructs.cs` if a new params flag bit is needed (Fix 2)
- C# shader mirrors + `Njulf/Njulf.Tests/SimpleDdgiShaderMirrorTests.cs` (Fixes 1, 4)

## Verification
1. `dotnet build Njulf/Njulf.sln` — compiles all shaders via glslangValidator (csproj targets), catching GLSL errors.
2. `dotnet test` — `SimpleDdgiShaderMirrorTests`, `SimpleDdgiVolumeManagerTests`, `DdgiValidationPlanTests` etc.; update mirrors so distribution/round-trip tests pass with per-probe rotation.
3. Runtime (user's GPU machine — container has no GPU):
   - Pulsing: `SimpleDdgiAverageSampledIrradianceLuminance` swing across captures drops from ~35% to <10%; `DdgiBlackFrameSuspect=0` in steady state; `MaintenanceRayProbeUpdateCount` stops oscillating.
   - Blackouts: force an atlas clear (resize a ring) and confirm recovery in <10 frames with no full-black frame.
   - Dark blobs: debug views 7 (DdgiIrradiance) and 32 (DdgiDataConfidence) on the top-left wall — blobs/dark region gone; `DdgiForwardZeroDdgiAndZeroIblCount` → ~0.
   - Arches: compare view 7 under the arcades before/after; lantern light no longer passes through vaults; final render arch brightness plausible.
   - Perf: `GpuSimpleDdgiTraceMicroseconds` stays within the 2.5ms GI budget (expect +0.2-0.4ms from emissive shadow rays).