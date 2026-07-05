# SDF / Surface-Cache: Why It's Still Bad, and the Fix Plan

## Context

Latest diagnostics show the surface cache still fails 9–30% of hit-shading
lookups (gate is <2%), and the SDF-traced views show terraced/rippled
surfaces. The user asked for root-cause analysis, a comparison against how
Lumen / RTXGI / Godot SDFGI solve the same problem, and a plan.

## Root cause — the diagnostics already isolate it

Key numbers from the captured frames:

- `fallbackBackend sdf/ray = 935/0, 461/1, 467/2, 910/0, 776/1` —
  **effectively 100% of fallbacks are SDF-backend hits** (DDGI cascades ≥ 2,
  `backendFirstCascade=2`). Ray-query hits almost never fail → the card
  lookup machinery itself is fine; **the SDF hit positions are what the
  lookup rejects**.
- Reject split: `depthUv` ≈ 85–87% of fallbacks, `grid` ≈ 12%,
  `noCandidatePassed` ≈ 1%, `normal`/`alpha` = 0.
- Fallback% spikes to ~28–30% when the camera moves (trace count spikes),
  settles ~9–10% when still.

### Mechanism 1 (dominant): depth window normalized by object thickness

`TryProjectDdgiSurfaceCard` (`Njulf.Shaders/ddgi_update_shared.glsl:1853`):

```glsl
float depthTolerance = 0.05 + clamp(0.08 * hitDistance / depthRange, 0.0, 0.25);
```

The tolerance is in **card-depth-normalized units, clamped at 0.25**. World
tolerance = `0.05*depthRange + min(0.08*hitT, 0.25*depthRange)`. For thin
geometry (this scene's walls), `0.25*depthRange` is a few centimetres.

But the SDF hit error is *independent of the object's thickness* — it is set
by the trace epsilon and the SDF voxel error at the hit cascade:

- Cascade voxels are `[0.125, 0.25, 0.5, 1.0]` m (`GlobalSdfManager.cs:18`,
  resolution 192 → extents 24/48/96/192 m).
- Trace epsilon: `max(voxelSize*0.15, t*0.08)`
  (`DDGI_GLOBAL_SDF_TRACE_EPSILON_SLOPE = 0.08`,
  `global_sdf.glsl:165`) → the ray stops **0.4–1.6 m short** of the surface
  at t = 5–20 m.
- Hit refinement is only 2 sphere-trace iterations
  (`ddgi_update_shared.glsl:2252-2260`) stepping by the *Euclidean* SDF
  distance **along the ray** — for grazing incidence (far floors/walls,
  exactly the far-cascade regime) per-step progress is `dist/sin(angle)`;
  2 iterations leave 0.1–0.5 m of residual error.

Residual error (0.1–0.5 m) ≫ allowed window (~5 cm on thin walls) → depthUv
reject → analytic fallback. The 0.25 clamp defeats the distance-scaled
tolerance exactly where it is needed. The UV window (fixed ±0.02) has the
same flaw for lateral error, and shares the same counter.

### Mechanism 2: lookup-grid padding smaller than the hit error

Grid AABB = card-AABB union padded by a fixed **1.0 m**
(`SurfaceCacheFarCascadeVoxelPadding`, `SurfaceCacheManager.cs:29,468,554`).
Hit error before refinement reaches ~1.6 m → hits float outside the grid →
the ~12% `grid` rejects.

### Mechanism 3: epsilon slope 0.08 is an outlier, and causes the terraces

The DDGI trace uses slope 0.08 while the debug view uses 0.015
(`forward.frag:58`) — the probes see a *worse* SDF than the debug view. An
epsilon proportional to t produces the terrace/onion-ring contours in
screenshot 1 (iso-t shells on oblique surfaces, plus concentric LOD-switch
rings from `SelectGlobalSdfTraceLod`). Lumen expands the hit test by
~a voxel, not 8% of ray distance.

### Why the image still looks blotchy at "only" 10% fallback

- The fallback path returns *analytic* radiance whose energy differs from
  the cache (the cache includes probe-fed indirect,
  `surface_cache_update.comp:866-873`). Probe rays flip between the two
  shading models frame to frame → temporal splotches in the probe grid.
- The stale-brick tearing from `implementation/RejectConterfix.md` Task 2
  (clipmap scroll invalidation gap) was never confirmed/fixed — diagnostics
  say `bricks=0, backlog=0` (scheduler believes clean) while the SDF view
  shows layered stale shells after movement. This tracks the
  moving-vs-still fallback% split too.

## Comparison with other engines — where our technique diverges

**Lumen (UE5)** — same architecture (cards + global SDF), different details:
1. Sampling tolerances are **world-space, tied to SDF voxel size**, and the
   depth test is against a **captured per-texel card depth buffer** with a
   bias — never against the object's bounding-box thickness.
2. SDF hits are refined aggressively (more steps / gradient projection onto
   the isosurface) before cache lookup; the trace's "surface expand" is
   voxel-proportional, not distance-proportional.
3. Cards capture a full gbuffer (albedo/normal/depth) **once by raster**,
   and relight cheaply from the stored gbuffer. Njulf re-traces a ray query
   and re-resolves the material **per texel per relight frame**
   (`surface_cache_update.comp:875-913,960-984`) — that is why
   `gpuUs cache=874` vs `sdf=253`.
4. Atlas has dilation gutters; a near-miss samples a valid texel instead of
   rejecting. Lumen's "fallback" is skylight/radiosity through the same
   cache — there is no second shading model to disagree with.

**Godot SDFGI**: stores albedo/light **in voxels at the same resolution as
the SDF** — the shading representation's precision matches the trace
representation's error by construction. That is the deep statement of our
bug: *cm-precision card windows are being fed by a 0.5–1 m-precision trace*.

**RTXGI DDGI**: no cache at all — analytic direct + recursive probe gather;
one consistent shading path, so no fallback dichotomy.

Conclusion: the architecture is Lumen-shaped and sound; the failures are
(a) tolerance in the wrong units + wrong clamp, (b) under-refined hits,
(c) oversized epsilon slope, (d) under-padded grid, plus the still-open
stale-brick invalidation suspicion.

## Fix plan (ranked; A+B+C are one small PR)

### A. World-space lookup tolerance (kills the depthUv dominance)
- `TryProjectDdgiSurfaceCard` (`ddgi_update_shared.glsl:1833-1875`): replace
  the normalized `depthTolerance` with a **`hitErrorMeters` parameter**.
  Test `dot(relative, axisN)` (world metres) against
  `[-hitErrorMeters, depthRange + hitErrorMeters]`; widen the UV window by
  `hitErrorMeters / (2*halfU|halfV)` as well (lateral error is real).
- Plumb `hitErrorMeters` through `TrySampleDdgiSurfaceCacheRadiance` /
  `ConsiderDdgiSurfaceCacheCard` (replacing the `hitDistance` argument that
  exists only for tolerance). Call sites:
  - SDF backend (`:2277`): pass the **voxel size of the finest cascade
    containing the refined hit** (SampleDdgiGlobalSdf already finds it —
    return/derive the cascade voxel size), e.g. `voxelSize * 1.0`.
  - Ray-query backend (`:2366`): pass a small constant (~0.01).

### B. Converging hit refinement + saner epsilon
- Replace the 2-iteration along-ray refinement (`:2252-2260`) with
  **gradient projection**: 2–3 iterations of
  `p -= EstimateDdgiGlobalSdfNormal(p) * SampleDdgiGlobalSdf(p).DistanceMeters`
  (converges for grazing rays, unlike stepping along the ray), then
  recompute `hitT`-equivalent for the visibility moment from
  `distance(origin, p)`.
- Lower `DDGI_GLOBAL_SDF_TRACE_EPSILON_SLOPE` 0.08 → 0.02, and raise the
  fixed floor in `TraceGlobalSdfCascadeSegment` from `voxelSize*0.15` to
  `voxelSize*0.5` (Lumen-style voxel-proportional expand). This shrinks the
  hit bias at range *and* the terrace artifacts, without reopening
  inside-start leaks (floor term still covers near field; watch
  `insideStarts`/`stepExhausted` stay ~0).

### C. Grid padding derived from SDF error
- `SurfaceCacheManager.cs`: derive the pad from the coarsest DDGI-used SDF
  cascade voxel (1.0 m) × safety 2 → pad **2.0 m** for both the grid AABB
  (`CalculateGridBounds`) and per-card bounds (`CalculateCardBounds`)
  instead of the fixed 1.0. (Plumb as a constant; keep the existing name.)

### D. Stale-brick confirmation (carried over, still open)
- Add the debug trigger that calls `MarkAllCascadesDirty()` once (hotkey or
  smoke option). Repro: move 10–20 m, stop, capture SDF slice, trigger,
  compare after backlog drains. If it heals → audit
  `InvalidateMovedAxisSlab`/ring-wrap consumption in `GlobalSdfManager.cs`
  and add the multi-axis scroll unit test per
  `implementation/RejectConterfix.md` Task 2. If it does not heal, report —
  artifact is in trace/content, not invalidation.

### Follow-up (separate PR, not this one)
- Lumen-style capture: store albedo+depth in the capture atlas, relight
  from stored gbuffer without re-tracing (cuts the 874 µs relight, enables
  real card-depth testing later); add the dilation gutter pass. Only after
  fallback% is at gate: delete the analytic per-hit path (M4).

## Files touched (A–D)

- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl` — projection tolerance,
  refinement, epsilon constant, plumbing.
- `Njulf/Njulf.Shaders/global_sdf.glsl` — epsilon floor in
  `TraceGlobalSdfCascadeSegment`.
- `Njulf/Njulf.Rendering/Resources/SurfaceCacheManager.cs` — padding.
- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` +
  `NjulfHelloGame/*` — MarkAllCascadesDirty debug trigger (D).
- Tests: `ShaderBuildTests` string assertions, `RendererDiagnosticsTests`
  if counter args change, `GlobalSdfManagerTests` scroll test (D, if fix).

## Verification

1. Run the sample on the same two viewpoints; capture still + moving
   windows of `Frame diagnostics SDF`.
   - Expect `depthUv` rejects to collapse; `cacheFallback% < 5` steady,
     `< 10` moving; `grid` rejects near 0 after C.
   - Watch `insideStarts`, `backfaceSynthesized`, `stepExhausted` stay ~0
     after the epsilon change; `gpuUs sdf` should not regress materially
     (smaller epsilon → more steps; `avgSteps` may rise from ~6 to ~10,
     acceptable).
2. Visual: SDF slice/debug view — terracing and LOD rings reduced; normal
   render — floor/wall splotches reduced (fewer per-frame shading-model
   flips).
3. Task D repro decides whether the invalidation audit proceeds.
4. `dotnet test` for the touched test suites.