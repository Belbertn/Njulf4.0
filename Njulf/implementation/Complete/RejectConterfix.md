# Next round: fix reject-counter semantics, stale-brick tearing, remaining fallback

Repo `Belbertn/Njulf4.0`, branch `Simplified` (HEAD `f2aae0f`).

## State (verified — don't re-derive)

Wins from `f2aae0f`: SDF hit refinement (2 iterations) + `backendFirstCascade=2`
cut `cacheFallback%` from 27–91% to ~8–22%. Per-backend split works
(`fallbackBackend sdf/ray` sums exactly to fallback). Triage Degraded
threshold, `no-dirty-bricks` skip reason, step-exhausted fall-through: all
correct. `stepExhausted=0`, `insideStarts=0`, `alpha` rejects = 0 (silhouette
texel theory refuted).

## Task 1 — Fix reject-counter semantics (they're currently unusable for ranking)

`depthUv`/`normal` counters read 400k–1.1M/frame vs only ~5–10k lookups:
they're incremented **per candidate card** inside the 3×3×3-cell loop
(`TryProjectDdgiSurfaceCard`, `Njulf.Shaders/ddgi_update_shared.glsl:1841,1850`)
— a successful lookup still logs dozens of rejects. Meanwhile the per-lookup
terminal causes sum exactly to fallback: `grid + noCards == fallback` every
frame (e.g. 399+1365=1764).

1. In `TrySampleDdgiSurfaceCacheRadiance` (`:1959-2004`), track locally which
   reject reason occurred most among candidates (two uint tallies for
   depthUv/normal is enough); on the `bestScore0 <= 0` exit, increment
   **one** per-lookup counter for the dominant reason instead of (or in
   addition to) the `NO_CARDS` counter at `:2001`.
2. Rename the `:2001` counter semantics: it is "no candidate passed
   projection", not "no cards exist" (`:1937` is the real no-cards branch).
   Rename in reporter output: `noCandidatePassed`.
3. Remove the per-candidate increments at `:1841`/`:1850` or keep them under
   distinct names (`candDepthUv`, `candNormal`) so per-lookup and
   per-candidate scales are never mixed in one list.

## Task 2 — Stale-brick tearing after camera movement (new artifact, product-relevant)

The slice screenshot shows sawtooth/chevron tearing along wall tops and edges
at brick-ish granularity, plus marbled stale patches — appearing after camera
movement, persisting while still (`bricks=0, backlog=0` = scheduler believes
everything is clean). Suspected: clipmap scroll-slab invalidation gap —
`InvalidateMovedAxisSlab`/ring-offset math or priority-run consumption in
`Njulf.Rendering/Resources/GlobalSdfManager.cs` missing bricks on multi-axis
or multi-brick scrolls, leaving stale world data marked clean.

1. **Confirm first** (cheap): add a debug hotkey/option that calls
   `MarkAllCascadesDirty()` once. Repro: move sideways ~10–20 m, stop, capture
   slice (tearing visible) → trigger full re-dirty → if the slice heals after
   the backlog drains, staleness is confirmed and the invalidation path is
   guilty. If it does NOT heal, the artifact is in trace/content, not
   invalidation — stop and report.
2. On confirmation, audit `UpdateClipmap`'s slab invalidation
   (`GlobalSdfManager.cs`, `InvalidateMovedAxisSlab` + `MarkLogicalRegionDirty`
   + ring wrap): known suspect classes — (a) multi-axis deltas invalidate
   three axis-aligned slabs but the L-shaped union's corner overlap is fine,
   *gaps* arise when `ConsumePriorityDirtyRun` consumes a physical run that
   wraps across the ring seam while the slab was marked in logical space;
   (b) `delta` accumulated over multiple frames of fast movement exceeding
   slab handling. Add a unit test in `GlobalSdfManagerTests.cs` that scrolls
   a small cascade through multi-axis multi-brick deltas and asserts every
   logical brick whose world content changed is marked dirty (mirror-test the
   physical↔logical mapping the same way existing addressing tests do).

## Task 3 — Remaining fallback reduction (data-justified now)

Terminal causes: ~75–80% "no candidate passed" (per-candidate data says
depthUv dominates ~3:1 over normal), ~20–25% grid miss.

1. Distance-scaled card depth tolerance: in `TryProjectDdgiSurfaceCard`, widen
   the `d` window (currently fixed [-0.05,1.05]) by a term proportional to
   hit distance (mirror the trace's cone epsilon ~`0.08·t`, normalized by
   `depthRange`), passed in or derived from `worldPosition` distance to
   probe. SDF hits still land slightly off-surface even after refinement.
2. Pad the lookup-grid AABB and per-card bounds by ~1 far-cascade voxel
   (`SurfaceCacheManager.cs:318-490`) to cut grid misses.
3. Target: fallback% <5% steady. The <2% gate + analytic-path deletion stays
   a separate follow-up.

## Task 4 — Backface visibility-moment math (flag, small fix, low volume today)

`f2aae0f` changed `mean = max(moments.x, 0.0001)` → `mean = moments.x` in 3
evaluators to admit RTXGI-style negative moments. Blending mixed-sign first
moments through a mean/variance (Chebyshev) filter is statistically unsound:
a texel blending +t and −0.8t can yield mean≈0 with large mean2 → huge
variance → visibility weight ≈1 → leak. RTXGI reduces backface influence at
*blend weight* level, not by feeding negative means into Chebyshev. Since
`insideStarts=0` the volume is tiny today; fix cheaply: keep the negative
encoding for relocation/classification, but at visibility-*evaluate* time use
`mean = max(moments.x, 0.0)` and rely on the shortened magnitude; or exclude
backface rays from visibility-moment blending entirely (RTXGI-faithful).
Choose one, comment the contract at `EncodeDdgiHitVisibilityMoment`.

## Verification

1. Task 1: per-lookup reject causes now sum to fallback and are directly
   rankable; capture shows dominant cause explicitly.
2. Task 2: repro sequence heals after forced re-dirty (confirmation), then no
   tearing after the fix with normal movement; new unit test green.
3. Task 3: fallback% <5% steady-state still + moving; `gpuDdgiUs` ≤ ~1.9 ms.
4. Task 4: no visual change expected today (volume ≈ 0); no leak regressions
   near interior walls.
5. Tests: `GlobalSdfManagerTests` (new scroll-invalidation test),
   `RendererDiagnosticsTests`, `DdgiDiagnosticsFormattingTests`,
   `ShaderBuildTests` string updates.