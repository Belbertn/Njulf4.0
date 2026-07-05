# Surface cache fallback plan (cacheFallback% 27–45% → <2% gate)

## Context

The DDGI hybrid shades probe-ray hits from a Lumen-style surface cache
(per-object cards in a 4096² atlas, relit every frame). The design gate
(`Njulf/implementation/DDGI-SDF.md` M4) requires <2% fallback to analytic
hit-shading; we run 27–45% (`cacheFallback%`). Exploration of
`origin/Simplified` (`294db3f`) established the facts below — the plan is
built on them, do not re-derive.

**Facts (verified, with sources):**
- `atlasOccupancy=0.7%` is a *sizing artifact*, not failure: ~240 cards
  (40 objects × 6 axes) at 8–32 px tiles in a 4096² atlas. No fix needed.
- `cacheTiles=128` pinned every frame is *by design*: `BuildCaptureList`
  (`Njulf.Rendering/Resources/SurfaceCacheManager.cs:199-224`) re-lists all
  cards each frame, sorts New>Dirty>oldest, truncates to budget → round-robin
  relight. 240 cards / 128 budget = every card relit every ~2 frames (doc
  allows ≤4). No thrash, no fix needed.
- `ddgiShaderFallback=709/709/0` and `forwardFallback` are the **forward
  probe-gather** fallback — a different subsystem. Do not conflate with the
  surface cache in code or in the report output.
- The real problem is **hit→card lookup rejection**. All of these branches
  increment ONE counter (`DDGI_SURFACE_CACHE_FALLBACK_COUNTER`,
  `Njulf.Shaders/ddgi_update_shared.glsl:2171,2259`):
  1. no cards (`:1934`);
  2. grid-cell resolve fail — 24³ lookup grid spans only the card-AABB union
     (`SurfaceCacheManager.cs:318-490`), hits outside it always fail;
  3. projection reject in `TryProjectDdgiSurfaceCard` (`:1817-1848`): UV
     outside [-0.02,1.02] or depth outside [-0.05,1.05] (a), or
     `dot(hitNormal, cardAxis) ≤ 0.001` for all 6 axes (b);
  4. `bestScore0 ≤ 0` (`:1978`);
  5. alpha-0 texel reject (`SampleDdgiSurfaceCacheCandidate:1918-1923`) —
     card found but the capture ray missed at that texel (silhouettes/
     corners), both blend candidates invalid.
- Fallback returns analytic lit radiance (never black) — so this is an
  energy-consistency + perf + "can't delete the analytic path" problem, not
  a black-splotch problem.
- **SDF-backend-specific suspicion**: SDF cascade rays stop `t·0.08` short of
  the surface (cone epsilon, `DDGI_GLOBAL_SDF_TRACE_EPSILON_SLOPE`,
  `ddgi_update_shared.glsl:127`) — at t=10 m the reported hit point floats
  0.8 m off the wall, which blows the card depth window (3a) and can exit the
  lookup grid (2). Ray-query hits land exactly on triangles and don't suffer
  this. The per-backend split counter (Stage A) confirms or kills this.

## Stage A — instrument first (small PR; the fix stage is gated on its data)

Session lesson: every un-instrumented fix round here produced a regression.

1. **Per-cause fallback counters (GPU)** — extend the base-100 block in
   `Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs` (grow
   `DdgiSdfSurfaceCacheCounterCount` + `CounterCount`; readback struct + its
   parsing follow the existing 6 counters' pattern):
   - `cacheRejectGridMiss`, `cacheRejectProjection` (UV/depth),
     `cacheRejectNormalAxis`, `cacheRejectAlphaTexel`, `cacheRejectNoCards`
     — increment at each branch in `TrySampleDdgiSurfaceCacheRadiance` /
     `TryProjectDdgiSurfaceCard` (`ddgi_update_shared.glsl:1926-1993,
     1817-1848`). Projection sub-causes 3a vs 3b must be separate counters —
     they have different fixes.
   - `cacheFallbackSdfBackend` / `cacheFallbackRayQueryBackend` — increment
     alongside the existing fallback counter, keyed on which trace backend
     produced the hit (the call sites at `:2171` and `:2259` already know).
2. **Capture-quality counters**: in `surface_cache_update.comp`, count texels
   written with alpha=0 (capture-ray miss) vs alpha=1 → gives an atlas
   valid-texel fraction. Plumb a CPU-side card-age p95 from
   `SurfaceCacheManager` (it already tracks per-card age for LRU sorting).
3. **Reporting + filter** (answers "add counters to SDF filter?"):
   - Give the cache its own **`cache` filter category**, not the `sdf` one:
     extend the `--diag-filter` option (from the previous handoff plan; if
     the other agent hasn't implemented it yet, add it here —
     `NjulfHelloGame/SampleSmokeOptions.cs` + parser + tests) to accept a
     comma list: `gi`, `sdf`, `cache`, `all`.
   - Add a `CACHE TRIAGE` line in `SampleDiagnosticsReporter.cs` mirroring
     the DDGI/SDF triage pattern: state from fallback% vs gate + dominant
     reject cause + valid-texel fraction, with a `next=` hint.
   - The **per-backend fallback split does double-duty**: also print it in
     the SDF section/triage, since SDF-cascade hit shading is a consumer —
     that is the one cache counter that belongs in the `sdf` filter too.

## Stage B — fixes, ranked (each gated on Stage A confirming its branch matters)

1. **SDF hit refinement before lookup** (if `cacheFallbackSdfBackend` ≫
   ray-query): after an SDF trace hit, refine the hit point onto the
   isosurface before card lookup — 1–2 extra fine samples stepping
   `p += dir * fineSample.DistanceMeters` (cheap sphere-trace polish), in the
   SDF branch of `TraceProbeRay` (`ddgi_update_shared.glsl`, hit handling
   near `:2259`). This fixes the depth-window and grid-miss rejects at their
   root and also improves visibility moments. (Lumen does the equivalent
   "surface expand/contract" when shading SDF hits.)
2. **Atlas dilation pass** (if `cacheRejectAlphaTexel` dominates): after the
   capture dispatch in `Njulf.Rendering/Pipeline/SurfaceCachePasses.cs`, run
   a small compute pass filling alpha-0 texels from valid 8-neighbors (the
   design doc's promised "1-texel atlas gutters", M3). One shader
   (`surface_cache_dilate.comp`), same pipeline-creation pattern as
   `global_sdf_mip_reduce.comp` in `GlobalSdfPasses.cs`.
3. **Soften the two lookup windows** (if 3a/3b still show after 1–2):
   widen the card depth window and normal threshold for far/SDF hits —
   e.g. scale the depth tolerance with hit distance (mirroring the trace's
   cone epsilon) instead of the fixed [-0.05,1.05]; accept the best axis
   with down-weighted score rather than hard-rejecting at `≤ 0.001`.
4. **Grid coverage** (if `cacheRejectGridMiss` is significant): pad the 24³
   grid AABB and per-card bounds by the far-cascade dilation margin (~1
   coarse voxel) in `SurfaceCacheManager.cs:318-490`.
5. Only after `cacheFallback% < 2%` sustained on the validation scenes: the
   M4 deletion of the analytic per-hit light loop — **separate PR, not this
   one**.

## Out of scope

Atlas resizing (occupancy is cosmetic), capture-budget tuning (round-robin is
healthy), the forward gather fallback (different subsystem), probe
relocation/backface work (already planned separately).

## Verification

1. Land Stage A alone, run with `--diag-filter=cache`, capture one still +
   one moving window: the reject-cause distribution identifies the dominant
   branch — attach it before starting Stage B.
2. After each Stage B item: `cacheFallback%` trend on the same captures;
   target <10% after items 1–2, <2% after 3–4. Watch `gpuDdgiUs` stays
   ≤ ~1.9 ms and `hybridPerfOk=True` (dilation + refinement add cost).
3. Visual: normal render — hit-shading consistency means fewer luminance
   discontinuities where backend switches (compare against the ray-backend
   heatmap view); no new artifacts in the slice view.
4. Tests: counter plumbing mirrors existing patterns —
   `RendererDiagnosticsTests`, `DdgiDiagnosticsFormattingTests`,
   `SampleSmokeOptionsParserTests` (filter), `ShaderBuildTests` string
   assertions for the new shader + counter constants.