# Final Plan — JCGT-style exact SDF tracing (DDA + analytic trilinear root) + surface cache quality

## Context

Branch `claude/sdf-surface-cache-quality-qhbyll`. The SDF debug raymarch view (`GLOBAL_ILLUMINATION_DEBUG_GLOBAL_SDF_SLICE`, `forward.frag:3752`) previously showed severe concentric "onion ring" terracing. The user's own recent commits (new `global_sdf_mip_reduce.comp` proper min-reduce + epsilon slope already 0.08→0.02) **removed the rings** — confirmed: `coarseSkips` 10000+→0, `avgSteps` 5→8, image now solid. But `global_sdf.glsl`'s trace is **unchanged** and still uses **epsilon-acceptance** (`d ≤ max(voxelSize*0.5, t*slope)`, `global_sdf.glsl:165,186`), so surfaces are still ~half-a-voxel inflated/puffy, silhouettes shimmer, and ~0.5–2.3% of DDGI rays exhaust the 96-step budget (`stepExhausted`), which trips the (hair-trigger) "Degraded" triage.

**Diagnosis delivered to user:** the debug view renders correctly (multi-colour = `MeshletDebugColor(cascadeIndex+1)` cascade tinting, `forward.frag:2897,2922` — not corruption); remaining issues are real-but-mild implementation (epsilon inflation + grazing step-exhaustion) plus an over-sensitive triage gate.

**Approved direction:** Full JCGT rewrite of the tracer — replace epsilon sphere-tracing with **3D-DDA voxel traversal + analytic intersection of the trilinearly-interpolated surface (cubic root)** for exact, epsilon-free hits — plus the grazing/step-exhaustion fix. Keep the user's mip-reduce work as the coarse empty-space accelerator.

Reference: Hansson-Söderlund, Evans, Akenine-Möller, *Ray Tracing of Signed Distance Function Grids*, JCGT 11(3) 2022 (https://jcgt.org/published/0011/03/06/, https://research.nvidia.com/publication/2022-09_ray-tracing-signed-distance-function-grids); builds on Marmitt et al. 2004 fast ray/trilinear-isosurface intersection; iquilezles.org raymarching/normals articles.

**Process warning:** `ShaderBuildTests.GlobalSdfShaders_UseToroidalClipmapBrickAddressing` (`Njulf.Tests/ShaderBuildTests.cs:1447-1599`) string-pins nearly every line touched. Each phase rewrites its pins in the same commit.

---

## Architecture of the new tracer (hybrid: coarse sphere-jump → fine DDA+cubic)

Mirrors JCGT + clipmap best practice: cheap conservative marching through empty space, exact analytic intersection only in the near band.

1. **Coarse phase (empty-space skip).** Sphere-step using the min-reduced mip distance (now correct after the user's `global_sdf_mip_reduce.comp`). Advance `t += max(mipDistance - margin, minStep)` while `|d| > ~1.5·voxelSize`. This keeps step counts low and is what fixes grazing exhaustion (no per-voxel walk across empty space).
2. **Fine phase (exact hit).** When `|d| ≤ ~1.5·voxelSize`, switch to **Amanatides-Woo 3D-DDA** over LOD0 voxel cells of the current cascade. Per cell traversed:
   - Fetch the 8 encoded corners via the existing `GlobalSdfLogicalVoxelToPhysicalTexel` + `texelFetch` (reuse `global_sdf.glsl:33-63`), decode to metres.
   - Restrict the ray to the cell's `[tEnter, tExit]`; the decoded trilinear field along a linear ray is a **cubic** `f(t) = k3 t³ + k2 t² + k1 t + k0` (Marmitt/JCGT coefficient grouping, below).
   - Solve for the **smallest root in `[tEnter, tExit]`** (analytic cubic + one Newton polish). Root found ⇒ exact `tHit`; `HitErrorMeters` = residual `|f(tHit)|`-derived, sub-voxel (~0.01–0.03 m); normal = **analytic trilinear gradient** at the hit (not central differences).
   - No root ⇒ step DDA to the next cell.
3. **Cascade hand-off** unchanged in structure (`TraceDdgiGlobalSdf`), but pass the exact hit + honest `HitErrorMeters` out; on boundary, advance `t = max(segment.T + 0.05·voxel, t)`.

### Cubic coefficients (per cell, ray o+t·d in cell-local [0,1]³)
Let `p = a + t·b` with `a` = ray entry in cell-local coords, `b` = `d` scaled by inv-cell-size. Trilinear field `f = Σ c_ijk · Bx_i(u)·By_j(v)·Bz_k(w)` with `B_0(x)=1-x, B_1(x)=x`. Substituting the linear `u,v,w(t)` yields (grouping the standard difference form):
- `k0 = f(a)` (evaluate trilinear at entry)
- `k1,k2,k3` from the products of `b` components with the mixed-difference tensors of the 8 corners (implement the compact Marmitt 2004 recurrence; unit-tested against a brute-force sampled reference in a new host test).
Solve via depressed-cubic/Cardano; clamp/select the first real root in-interval; 1 Newton step `t -= f(t)/f'(t)` to polish. `f'` is the quadratic derivative (also the along-ray gradient — reuse for the normal sign).

---

## Phase 1 — Core tracer rewrite (`global_sdf.glsl`) — the artifact + precision fix
Files: `Njulf.Shaders/global_sdf.glsl`, `Njulf.Tests/ShaderBuildTests.cs`, new host test in `Njulf.Tests` for cubic coefficients.
- Add `EvaluateGlobalSdfTrilinear(cellCorners, localP)` and `AnalyticTrilinearGradient(...)` helpers.
- Add `IntersectTrilinearCell(corners, aLocal, bLocal, tEnter, tExit, out tHit, out residual)` — builds + solves the cubic.
- Rewrite `TraceGlobalSdfCascadeSegment` as the hybrid coarse-sphere → fine-DDA loop above. Remove `traceEpsilon` hit acceptance and the `hitTestArmed` band. Keep `GlobalSdfRayAabbExit` for the cell/cascade clip. Extend `GlobalSdfTraceResult` with `float HitErrorMeters;` (update all constructor sites incl. `ddgi_update_shared.glsl:2236`).
- Normal: replace `EstimateGlobalSdfNormal` at the hit with the analytic gradient from the solved cell (keep the 6-tap version for the coarse/debug fallback).
- Keep `SelectGlobalSdfTraceLod`/mip fetch for the **coarse phase only** (correct now).
- Tests: rewrite trace pins (`ShaderBuildTests.cs:1506-1571`); add `GlobalSdfCubicIntersectionTests` asserting the analytic root matches a densely-sampled trilinear zero-crossing within 1e-3 voxel for randomized cells/rays (incl. grazing).

## Phase 2 — DDGI consumer integration (`ddgi_update_shared.glsl`)
- `TraceProbeRay` (`:2298-2346`): delete the 4-iteration Newton refine block (redundant — DDA hit is exact). Set `surfaceCacheHitErrorMeters = max(sdfTrace.HitErrorMeters, DDGI_SURFACE_CACHE_MIN_HIT_ERROR_METERS=0.05)`; delete the `DdgiGlobalSdfTraceUncertaintyMeters` re-max (`:2337-2339`) and rewrite that function to honest semantics.
- `TraceDdgiGlobalSdf` (`:2183-2237`): keep the coarse-phase step budget but note DDA steps are near-band-bounded; the coarse sphere-jump prevents grazing exhaustion. Raise the DDGI cap 96→128 (`:2298`) as headroom; keep the counter emission.
- Hand-off advance `:2229` → `t = max(segment.T + 0.05*voxelSize, t)`.

## Phase 3 — Hardware trilinear sampling (perf; complements coarse phase)
Files: `global_sdf.glsl`, `ShaderBuildTests.cs`. Replace the coarse-phase 8×`texelFetch` in `SampleGlobalSdfCascadeLod` with a single `textureLod` using the Linear+Repeat bindless default sampler (already configured, `Descriptors/BindlessHeap.cs:236-263`; registration `GlobalSdfManager.cs:294`): `uvw = (clamp(logicalVoxel,0.5,res-0.5) + ringOffset*8) / res`. Correctness: `(brick+ring) mod N *8 + vib == (voxel+ring*8) mod 8N`; REPEAT implements the mod. DDA fine phase keeps explicit `texelFetch` corners (needs integer corners). Drops the coarse sampler + normal fallback from 8/48 fetches to 1/6. Invert the `Does.Not.Contain("float encodedDistance = textureLod(")` pin.

## Phase 4 — Surface cache quality
Files: `ddgi_update_shared.glsl`, new `Njulf.Shaders/surface_cache_dilate.comp`, `Pipeline/SurfaceCachePasses.cs`, `Resources/SurfaceCacheManager.cs`, `RendererDiagnosticsBuffer.cs`/`Data/RendererDiagnostics.cs`/`SampleDiagnosticsReporter.cs`, `ShaderBuildTests.cs`.
- **4a. Honest candidate counters:** the `lookups/hits/fallback` counters already landed; move the four `candidate*` emissions out of the `bestScore0 <= 0.0` block (`:2053-2062`) so they count on success too (so `projectReject/refs` becomes meaningful, not ~100% by construction).
- **4b. Windows auto-tighten** from Phase 2's sub-voxel `hitError`; add floor const `DDGI_SURFACE_CACHE_MIN_HIT_ERROR_METERS=0.05`; watch `reject depthUv`.
- **4c. Normal gate:** raise `TryProjectDdgiSurfaceCard` `normalScore <= 0.001` (`:1872`) → `< 0.2`; watch `reject normal`.
- **4d. Atlas dilation pass:** new `surface_cache_dilate.comp` after capture over updated tiles — invalid texels (`a==0`) inside a card `AtlasRect` get valid-3×3-neighbor average with sentinel alpha 0.5; dilate radiance + capture(normal) atlases; never read outside the card rect. Kills silhouette `reject alpha`.
- **4e. Grid padding** ≥ new max hitError in `SurfaceCacheManager` grid build; verify `reject grid` flat.

## Phase 5 — Triage threshold honesty
File: `NjulfHelloGame/SampleDiagnosticsReporter.cs:549`. Change `degraded = DdgiSdfStepExhaustedCount > 0u || ...` to a fraction (e.g. `> 0.01 * traceCount`) so "Degraded" reflects a real rate, not a single ray. Update `DebugToolingContractsTests`/`SampleSmokeOptionsParserTests` if they assert the string.

---

## Tunables
- Add: `GLOBAL_SDF_TRACE_NEAR_BAND_VOXELS=1.5`, `GLOBAL_SDF_TRACE_MIN_STEP_VOXELS=0.25`, `GLOBAL_SDF_DDA_MAX_CELLS` (per-segment cap), `DDGI_SURFACE_CACHE_MIN_HIT_ERROR_METERS=0.05`, normal gate `0.2`.
- Remove: `traceEpsilon` acceptance, `hitTestArmed` band, the 4-iter Newton refine in `TraceProbeRay`.
- Keep: user's `global_sdf_mip_reduce.comp` (coarse skip), `SdfBackendFirstCascade=2`, resolution 192, R16Sfloat, ±32-voxel encode band, `MipCount`/mips (coarse phase).

## Verification (SDF debug raymarch view + frame diagnostics line)
1. Build shaders + `dotnet test` (ShaderBuildTests, new GlobalSdfCubicIntersectionTests, GlobalSdfManagerTests, SurfaceCacheCardProjectorTests, DebugTooling/SmokeOptions).
2. Same viewpoint as the screenshots:

| Signal | Expected |
|---|---|
| SDF debug view silhouettes | crisp, no puffiness/shimmer/hatching (exact hits) |
| `avgSteps` | coarse steps low (~5–10); DDA cells only near band |
| `stepExhausted` | → ~0; if residual, raise `GLOBAL_SDF_DDA_MAX_CELLS`/128 cap |
| `insideStarts`/`backfaceSynthesized` | flat |
| `surfaceCache hits/fallback%` | hit ratio up (tight windows + dilation); `reject alpha` down |
| `candidate projectReject/refs` | now meaningful (not ~100%) after 4a |
| triage state | `SteadyState` at real rates after Phase 5 |

3. DDGI probe visibility: exact hits land ~½-voxel *later* than epsilon-accepts ⇒ Chebyshev moments shift; validate leak/darkening in `SampleGlobalIlluminationValidation`/`SampleDdgiProductionGate`.
4. Movement test: walk 10–20 m, stop — no stale seam planes; cascade-handoff seams (the green ceiling ellipse) reduced.

## Risks
- Cubic robustness at grazing/degenerate cells — the new host unit test guards this; Newton polish + interval clamp for stability; fall back to coarse sphere step if the solver returns no in-interval root.
- DDA divergence/perf on GPU (per-lane variable cell counts) — bounded by near-band-only entry + `GLOBAL_SDF_DDA_MAX_CELLS`.
- R16F corner precision — ≤0.016 voxel at band edge; fine for the cubic near d≈0.
- Probe visibility-moment consumers shift — validate, don't assume.
- Test blast radius: ~60 pinned strings across the touched files — rewrite per phase, same commit.

## Branch / commits
Develop on `claude/sdf-surface-cache-quality-qhbyll`; commit per phase, push `git push -u origin claude/sdf-surface-cache-quality-qhbyll`. No PR unless asked.