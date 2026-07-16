# Simple DDGI v2 (`fe3ae89`) — verification, issues, next steps

## Verdict

The v2 architecture landed substantially correct and is working in the Cornell scene: pooled probe space with authored volume (700 @0.75 m) + 3 rings (6912 each @1/4/16 m), finest-first selection with edge fade, explicit per-frame update queue (no modulo scheduling), per-probe freshness, shift-copy scroll implemented, forward gather diagnostics now enabled and healthy (5096 samples, 100% nonzero irradiance, avg luminance 0.62, avg visibility 0.997, GI GPU 1.53 ms of 2.5 ms). Screenshots 2–3 are the **legacy** A/B path's debug views (snapshots show `SimpleDdgiActive=0`, clipmap presets, views 32/7) — not defects in the new path.

Verified correct: volume-table sort & FirstProbeIndex pooling everywhere (trace/blend/relocate/gather), per-axis anchor/follow (Cornell never recenters — correct), atlas/scratch sizing math, energy conventions (π once), octahedral bilinear + mirror borders, struct layouts pinned (160/96/32/32/76 B), scroll shift-copy with correct barriers and fresh marking, probe-budget overflow enforcement.

## Issues found (ranked)

### Functional (shader)
1. **Relocation is inert — backfaces are never detected on the TLAS path.** `ddgi_simple_trace.comp:148` classifies backface via `dot(direction, surfaceNormal) > 0`, but `ResolveCommittedHitSurface` already flipped the normal to face the ray (`ddgi_hit_shading.glsl:153-154`), so the test is always false ⇒ `backfaceCount==0` ⇒ `targetRelocation` stays zero (`ddgi_simple_relocate_classify.comp:100,118-126`). Fix: capture a backface flag (or geometric normal) before the flip. (Far-field path detects backfaces fine — TLAS-specific.)
2. **Classification is inverted.** `ddgi_simple_relocate_classify.comp:115,145`: `active = hitCount > 0`. A probe buried in a wall (all backface hits) stays ACTIVE and leaks darkness; a probe in open air seeing only sky (all misses) goes INACTIVE and is skipped by the gather (`ddgi_simple_shared.glsl:459`) — this will punch holes in outdoor GI the moment the verticality scene is tested. Hidden in Cornell only because a closed box has no misses. The intended signal, `backfaceRatio`, is computed (`:113`) but unused. Note: `SimpleDdgiShaderMirrorTests` lines ~272-273 currently *enshrine* the inverted rule (all-miss→inactive) — the test must change with the fix.
3. **Relocation magnitude can't escape geometry.** `clamp(0.45·spacing − dist, 0, 0.45·spacing)` (`relocate_classify.comp:123-126`) moves *less* the farther the backface is; for dist > 0.225·spacing the probe steps toward the wall but never exits. Use distance-past-the-backface (Majercik-style) instead.
4. **Hard cut to black outside all volumes.** Gather returns `vec3(0)` when no volume contains the point (`ddgi_simple_shared.glsl:533,546-548`) — a visible discontinuity at the outermost ring faces. Fade the outermost containing volume toward the environment/sky term instead.
5. Minor: forward OUT_OF_GRID/CLAMPED investigation counters use the unbiased position while the gather uses the biased one (forward.frag ~1971) — counters can disagree with reality.

### Memory / budget
6. **GI memory failure (125.7 vs 100.7 MB) is the scroll copy-temp buffer** — a persistent, full-atlas-sized (44 MB) device buffer allocated unconditionally (`SimpleDdgiVolumeManager.cs:961,970`), even in scenes that can never scroll. `CopyScrolledAtlasRuns` copies the whole pooled atlas to temp (`:1064`) instead of just the scrolled volume's slice. Fix: allocate lazily on first recenter and size to the largest single volume's atlas slice (~17.7 MB), or copy only the affected probe range. That alone brings GI memory to ~82 MB — green. (Ray scratch is fine: update-budget-sized, ~6.7 MB.)
7. **`DdgiProbeBudget` never raised** (`RenderBudgetProfile.cs:60/65`, still 8192 vs 21,436 actual) → chronic "DDGI probes" failure row. The v2 doc said to bump it deliberately; raise to the 32,768 cap.
8. "DDGI atlas memory"/"DDGI total memory" budget rows read 0/Unavailable (status 4) under the simple path (`DdgiTextureBytes` forced 0 at `VulkanRenderer.cs:5359`; `DdgiAtlasMemoryBudgetBytes` only set when legacy active) — the real 54.9 MB is invisible to the budget table.

### Diagnostics wiring
9. **Relocation/classification results are never read back**: `_probeInactive` is never populated (only cleared, `:577`), so the scheduler's inactive-skip (`:474-477`) is dead code — classification cannot recover ray budget; and `DdgiProbeRelocationCount`/`ClassifiedInactiveProbeCountEstimate`/`AverageRelocationFractionEstimate` are only fed by the legacy path (`VulkanRenderer.cs:5231,5509`) so they read 0. Either read back the classification buffer (latency-tolerant) or emit GPU counters from the relocate pass.
10. **`DdgiScrollCount` unwired**: manager tracks `ScrollCopyCount` (`:1036`) but nothing reads it; `PopulateDdgiDiagnostics` zeroes the field for the simple path. Scroll activity is currently unobservable — bad, since the scroll path has never executed at runtime yet (Cornell never recenters).

### Scheduling / polish (low)
11. Unused quota not redistributed: authored volume caps at 700 of its 1024 quota ⇒ only 1724 of 2048 budget used; authored volume fully re-traced every frame.
12. Fresh-marking runs before `EnsureCapacity` may clear atlases on growth ⇒ one slow-convergence episode when probe count grows (static scenes unaffected).
13. Authored lattice origin is snapped but `WorldMin/Max` are stored unsnapped (`:783,793-794`) ⇒ edge-fade band misaligned up to one spacing; `latticeSize` at `:784` is dead code.
14. 48-byte relocation/classification record stride is a magic constant with no struct/size pin.
15. Round-robin cursors keyed by table slot, not volume identity (harmless discontinuity when the volume set changes); simultaneous ring recenters aren't staggered.

### Pre-existing, unrelated to GI
16. CPU renderer 11.0 ms vs 6 ms budget — Hi-Z record (7.6 ms) + Hi-Z final barrier (6.6 ms) dominate an 8-object scene. Now the single biggest frame cost; separate work item.

### Test gaps
- Nothing executes the real manager besides the scroll-runs helper: no tests for `BuildVolumeTable`, quotas/queue, `EnforceProbeBudget`, `EnsureCapacity` sizing (would have caught the 44 MB temp), or recenter hysteresis.
- Mirror tests are self-contained reimplementations — only the string-contract test guards the actual shader text; none would catch bugs 1–4 (and one test locks in bug 2).
- No test asserts simple-path diagnostics get populated (bugs 9–10 invisible to CI).

## Recommended next steps (order)

1. **Fix relocation/classification** (issues 1–3 + the inverted mirror test): backface flag before normal flip; classify on `backfaceRatio` (embedded→inactive, sky-seeing→active); escape-distance magnitude. Do this before outdoor testing — issue 2 will punch holes in any scene with sky.
2. **Fix the scroll temp allocation + raise `DdgiProbeBudget`** (issues 6–7) → all budget rows green.
3. **Environment fallback outside all volumes** (issue 4).
4. **Wire the missing diagnostics** (issues 8–10): scroll count, relocation/classification counts (GPU counters are cheapest), simple-path atlas budget rows.
5. **Runtime-exercise the ring machinery for the first time**: verticality/tower scene walk test — expect `SimpleDdgiRecenterCount`/`DdgiScrollCount` > 0, no frame hitch, no GI pop, `FORWARD_OUT_OF_GRID = 0` inside coverage; then A/B legacy for the same path.
6. Scheduling/pinning polish (issues 11–15) opportunistically.
7. Separate item: CPU Hi-Z record/barrier cost (~14 ms CPU in an 8-object scene) — biggest remaining frame-budget failure and unrelated to GI.