# High-Quality SDF + Surface Cache Refactor

## Context

The SDF debug raymarch view (screenshot on branch `Simplified`) shows severe concentric "onion ring" terracing. Diagnostics show the build pipeline is healthy (bricks=0 dirty, backlog=0, stepExhausted=0) — the artifact is in **how hits are accepted**, not in scheduling or step budget.

A previous fix round (repo doc `implementation/SDDFFF.md`, fixes A–C: world-space projection tolerances, gradient-projection refinement, epsilon slope 0.08→0.02) already landed and brought surface-cache fallback from 9–30% down to 0.3–5%. What remains is the fundamental limitation the JCGT paper ("Ray Tracing of Signed Distance Function Grids", Hansson Söderlund/Evans/Akenine-Möller, JCGT 11(3)#6, 2022) addresses: **epsilon-acceptance sphere tracing on a grid SDF produces t-dependent inflated surfaces**. Verified root causes, ranked:

1. **Hit acceptance `d ≤ max(voxelSize*0.5, t*0.02)`** (`global_sdf.glsl:165,186`). With `SdfBackendFirstCascade=2`, all DDGI SDF rays trace 0.5/1.0 m-voxel cascades ⇒ hits accepted 0.25–0.5 m+ short of the surface, at a t that depends on the step sequence ⇒ iso-t shells = the rings.
2. **Quantized mip LOD + toroidally-incoherent mips.** `SelectGlobalSdfTraceLod` flips LOD on power-of-two distance shells, and `global_sdf_mip_reduce.comp` min-reduces the volume in *physical* space — after any clipmap scroll, mips blend texels across the toroidal wrap seam ⇒ structured coarse-skip errors.
3. **Huge hit uncertainty fed to the surface cache** (`DdgiGlobalSdfTraceUncertaintyMeters = max(voxel*0.5, t*0.02)`, `ddgi_update_shared.glsl:2178-2181`) inflates projection depth/UV windows and grid search radius ⇒ wrong-card accepts (bleeding) and noisy scoring.

Non-bug: `candidate projectReject == refs` in diagnostics is a reporting artifact — the candidate counters are only emitted inside the total-failure branch (`ddgi_update_shared.glsl:2053-2062`).

Enabling facts verified: cascade volumes are already registered with the bindless default sampler = **Linear + Repeat** (`BindlessHeap.CreateDefaultSampler`, `Descriptors/BindlessHeap.cs:236-263`; registration `GlobalSdfManager.cs:294`), and ring scrolls are brick-granular so `physical = (logical + ringOffset*8) mod resolution` — a REPEAT sampler filters the toroidal clipmap seamlessly.

**User decisions:** screenshot = SDF debug raymarch view (rings are the traced field itself); scope = deep refactor (full DDA/analytic cubic as optional stretch).

**Process warning:** `ShaderBuildTests.GlobalSdfShaders_UseToroidalClipmapBrickAddressing` (`Njulf.Tests/ShaderBuildTests.cs:1447-1599`) string-pins nearly every line touched (including `Does.Not.Contain("float encodedDistance = textureLod(")`, the epsilon formula, coarse-skip lines, mip pins, `generateFullMipChain: true`). **Each phase must rewrite its pins in the same commit.**

---

## Phase 1 — Iso-surface root finding replaces epsilon acceptance (the artifact fix)

Files: `Njulf.Shaders/global_sdf.glsl`, `Njulf.Shaders/ddgi_update_shared.glsl`, `Njulf.Shaders/forward.frag` (debug call site ~2884), `Njulf.Tests/ShaderBuildTests.cs`.

### 1a. Rewrite `TraceGlobalSdfCascadeSegment` (`global_sdf.glsl:139-193`)

March at LOD0 only; detect a crossing of a **fixed iso level**, then refine by false position. The iso level is t-independent (kills the rings) but nonzero (keeps walls thinner than a coarse voxel from leaking — pure zero-crossing would miss geometry whose trilinear field never dips below 0 at 0.5/1.0 m voxels):

```glsl
const float GLOBAL_SDF_TRACE_MIN_STEP_VOXELS   = 0.25;  // step floor, NOT hit acceptance
const float GLOBAL_SDF_TRACE_RELAXATION        = 0.9;   // conservative factor for non-exact grid SDF
const float GLOBAL_SDF_TRACE_SURFACE_ISO_VOXELS = 0.25; // fixed level set to intersect
const uint  GLOBAL_SDF_TRACE_REFINE_ITERATIONS = 2u;

float surfaceIso = voxelSize * GLOBAL_SDF_TRACE_SURFACE_ISO_VOXELS;
float minStep = voxelSize * GLOBAL_SDF_TRACE_MIN_STEP_VOXELS;
float dPrev = Sample(t) - surfaceIso;
bool armed = dPrev > 0.0;                       // replaces initialSurfaceBandEnd heuristic
loop {
    tNext = t + max(dPrev * GLOBAL_SDF_TRACE_RELAXATION, minStep);
    if (tNext > exitT) return miss;
    dNext = Sample(tNext) - surfaceIso;
    if (!armed) armed = dNext > 0.0;
    else if (dNext <= 0.0) return RefineHit(t, dPrev, tNext, dNext);
    t = tNext; dPrev = dNext;
}
```

Refinement (false position on the decoded trilinear field, bracket [tA,dA>0]..[tB,dB≤0]):

```glsl
for (i < GLOBAL_SDF_TRACE_REFINE_ITERATIONS) {
    tMid = tA + dA * (tB - tA) / max(dA - dB, 1e-6);
    dMid = Sample(tMid) - surfaceIso;
    if (dMid > 0.0) { tA = tMid; dA = dMid; } else { tB = tMid; dB = dMid; }
}
tHit = tA + dA * (tB - tA) / max(dA - dB, 1e-6);
hitError = max(min(dA, -dB) + surfaceIso, voxelSize * 0.05);  // residual + iso bias
```

- Delete `traceEpsilon`, the `hitTestArmed` band logic, `SelectGlobalSdfTraceLod` usage, and the coarse-skip branch (mips stay *generated* but unused until Phase 3). Remove `epsilonSlope` from the signature; update both callers (`ddgi_update_shared.glsl:2203`, `forward.frag:2884`); delete `DDGI_GLOBAL_SDF_TRACE_EPSILON_SLOPE` (`ddgi_update_shared.glsl:144`).
- Extend `GlobalSdfTraceResult` (`global_sdf.glsl:11-20`) with `float HitErrorMeters;` — update all constructor sites (`:162, :187, :192` and `ddgi_update_shared.glsl:2236`). `CoarseSkipCount` stays, reads 0.
- `EstimateGlobalSdfNormal`: shrink eps from 1 voxel to `0.5 * voxelSize`.

### 1b. Consume refined hits in `TraceProbeRay` (`ddgi_update_shared.glsl:2298-2346`)

- Replace the 4-iteration clamped-Newton block (`:2308-2326`) with **one** optional Newton polish (dt clamped ±0.5·voxel) — the secant already converged. Keep finest-cascade normal re-projection.
- `surfaceCacheHitErrorMeters = max(trace.HitErrorMeters, 0.05)`; delete the `DdgiGlobalSdfTraceUncertaintyMeters` re-max (`:2337-2339`) and remove/rewrite that function.
- Cascade hand-off (`TraceDdgiGlobalSdf:2229`): change `t = segment.T + voxelSize` → `t = max(segment.T + 0.05 * voxelSize, t)` so the next cascade can't skip a surface just past the boundary (progress still guaranteed by `cascadeIndex++`).

### 1c. Tests
Rewrite trace pins in `ShaderBuildTests.cs:1506-1571`: pin new constants, the bracketing condition, false-position formula, `HitErrorMeters`, and `Does.Not.Contain("max(voxelSize * 0.5")`.

**Landable alone: yes — rings should visibly disappear.**

## Phase 2 — Hardware trilinear via REPEAT sampler

Files: `global_sdf.glsl`, `ShaderBuildTests.cs`.

Replace `SampleGlobalSdfCascadeLod`'s 8× `texelFetch`+per-corner remap (`global_sdf.glsl:83-94`) with one filtered fetch:

```glsl
float res = float(cascade.Resolution);
vec3 logicalVoxel = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
vec3 clamped = clamp(logicalVoxel, vec3(0.5), vec3(res - 0.5));   // clamp in LOGICAL space
vec3 uvw = (clamped + vec3(cascade.RingOffsetX, cascade.RingOffsetY, cascade.RingOffsetZ) * 8.0) / res;
float encoded = textureLod(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], uvw, 0.0).r;
```

Correctness (record as comment + test pin): voxelInBrick < 8 ⇒ `(brick+ring) mod N * 8 + vib == (voxel + ring*8) mod 8N`; logical voxel centers sit at `i+0.5` = sampler texel centers; REPEAT addressing implements the mod. Bit-equivalent to the manual path away from edges, better at brick boundaries. `EstimateGlobalSdfNormal` drops 48→6 fetches.

- Keep `GlobalSdfLogicalVoxelToPhysicalTexel` (used by Phase 5 / debug).
- Default bindless sampler is already Linear+Repeat; if validation complains about mip filtering, add a dedicated LinearRepeat/maxLod-0 sampler in `BindlessHeap`/`SamplerManager` and pass at `GlobalSdfManager.cs:294` (only possible CPU change).
- Tests: invert the `Does.Not.Contain("float encodedDistance = textureLod(")` pin (`ShaderBuildTests.cs:1509-1516`); pin the uvw formula + logical clamp instead.

**Verify: image-identical (± float noise), DdgiTrace GPU time down.**

## Phase 3 — Remove mip chain + coarse-skip machinery

Files: `Resources/GlobalSdfManager.cs`, `Pipeline/GlobalSdfPasses.cs`, `global_sdf.glsl`, delete `global_sdf_mip_reduce.comp`, `ShaderBuildTests.cs`, `GlobalSdfManagerTests.cs`.

- `GlobalSdfManager.EnsureResources` (`:279-298`): `generateFullMipChain: false`; delete `RegisterMipStorageImages` (`:301-312`) and all `MipStorageImageIndices` plumbing (runtime `:632-646`, `AddJob :558-582`, `GlobalSdfUpdateJob :1160-1173`, frees `:595-596`). Saves ~8 MB + per-frame reduce dispatches.
- `GlobalSdfPasses.cs`: delete `MipReduceShaderName`, `_mipReducePipeline`, `GenerateMinMipChain` (`:316-363`), its barrier/constants, the "GlobalSdfMips" timestamp block (`:179-191`). **Critical:** `GenerateMinMipChain` currently owns the final `volume.TransitionToShaderRead(cmd)` — replace with an explicit loop over touched volumes or samplers see the wrong layout.
- `global_sdf.glsl`: delete `ClampGlobalSdfLod`, `FetchGlobalSdfCascadeEncodedDistance`, the lod parameter (fold into `SampleGlobalSdfCascade`), `SelectGlobalSdfTraceLod`, `CoarseSkipCount`. Keep counter constant `DDGI_SDF_COARSE_SKIP_COUNTER` (reads 0) so `RendererDiagnosticsBuffer` layout (count=23) and the diagnostics line stay stable.
- `GPUGlobalSdfCascade.MipCount` stays in the struct (layout pinned); value becomes 1.
- No replacement coarse structure: the ±32-voxel encode band already gives 16/32 m empty-space steps on backend cascades. Only revisit (logical-space occupancy volume) if `stepExhausted` grows.
- Tests: rewrite mip pins (`ShaderBuildTests.cs:1572-1592`, `:1454`); check `RenderFeatureIsolationPolicy`/pipeline-declaration tests for mip pass references.

## Phase 4 — Surface cache quality

Files: `ddgi_update_shared.glsl`, new `Njulf.Shaders/surface_cache_dilate.comp`, `Pipeline/SurfaceCachePasses.cs`, `Resources/SurfaceCacheManager.cs`, `RendererDiagnosticsBuffer.cs`, `Data/RendererDiagnostics.cs`, `NjulfHelloGame/SampleDiagnosticsReporter.cs`, `ShaderBuildTests.cs`.

- **4a. Honest counters:** move the four candidate-counter emissions out of the `bestScore0 <= 0.0` block (`:2053-2062`) so they run on success too; optionally add `DDGI_SURFACE_CACHE_LOOKUP_COUNTER` at base+23 (bump `DdgiSdfSurfaceCacheCounterCount`, reporter, pins).
- **4b. Normal gate:** `TryProjectDdgiSurfaceCard` raise `normalScore <= 0.001` (`:1872`) → `< 0.2` (cos ≈ 78°). Watch `reject normal`/fallback%; drop to 0.1 if curved geometry regresses.
- **4c. Tight windows for free:** Phase 1's `hitErrorMeters` (~0.05–0.1 m) auto-tightens depth window (`:1863`) and grid radius (`:2007`); add floor const `DDGI_SURFACE_CACHE_MIN_HIT_ERROR_METERS = 0.05`. Watch `reject depthUv`.
- **4d. Atlas dilation pass:** new `surface_cache_dilate.comp` after capture over updated tiles: invalid texels (`a == 0`) inside a card's `AtlasRect` get the average of valid 3×3 neighbors with sentinel alpha 0.5 (passes the `a > 0.001` test at `:1971`); one iteration; never read outside the card rect; dilate both radiance and capture(normal) atlases. Attacks silhouette `reject alpha`.
- **4e. Grid bounds padding** ≥ new max hitError (0.1 m) in `SurfaceCacheManager` grid build — verify `reject grid` stays flat.
- Keep `SdfBackendFirstCascade=2` (HW ray-query owns the near field; 1b covers the seam).

## Phase 5 (optional stretch) — JCGT DDA + analytic trilinear root

When `|d| < ~1.5·voxelSize`, switch from sphere stepping to voxel DDA; per cell fetch 8 corners (reuse `GlobalSdfLogicalVoxelToPhysicalTexel`) and solve the cubic of the trilinear interpolant along the ray (Cardano or 2 Newton iterations seeded by the linear root), guarded to the cell's t-range. Gate behind `#define GLOBAL_SDF_TRACE_ANALYTIC 1`, finest backend cascade only. Do this only if silhouettes still shimmer after Phases 1–4.

---

## Tunables
- Add (shader consts, no RenderSettings churn): `GLOBAL_SDF_TRACE_MIN_STEP_VOXELS=0.25`, `GLOBAL_SDF_TRACE_RELAXATION=0.9`, `GLOBAL_SDF_TRACE_SURFACE_ISO_VOXELS=0.25`, `GLOBAL_SDF_TRACE_REFINE_ITERATIONS=2`, `DDGI_SURFACE_CACHE_MIN_HIT_ERROR_METERS=0.05`, normal gate `0.2`.
- Remove: `DDGI_GLOBAL_SDF_TRACE_EPSILON_SLOPE`, `traceEpsilon`, `SelectGlobalSdfTraceLod`, mip-reduce shader/pipeline/constants, `generateFullMipChain` for SDF volumes.
- Keep: `SdfBackendFirstCascade=2`, `SdfClipmapResolution=192`, budgets, R16Sfloat, ±32-voxel encode band (R16F relative error ≤0.016 voxel at band edge — fine for secant near d≈0).

## Verification (per phase, using the existing diagnostics line)
1. Build shaders + `dotnet test` (ShaderBuildTests, GlobalSdfManagerTests, SurfaceCacheCardProjectorTests, RendererDiagnostics tests).
2. Run the sample, same viewpoint as the screenshot, capture the SDF debug raymarch view + frame diagnostics:

| Signal | Expected |
|---|---|
| SDF debug view | onion rings gone after Phase 1 |
| `avgSteps` | up ~5 → 8–15 after Phase 1 (acceptable); GPU time down after Phase 2 |
| `stepExhausted` | stays ~0; if it grows, raise maxSteps 96→128 (`ddgi_update_shared.glsl:2298`) |
| `coarseSkips` | → 0 (Phase 1), vestigial (Phase 3) |
| `insideStarts`/`backfaceSynthesized` | flat; a jump = refined hits landing inside surfaces |
| `surfaceCache hits/fallback%` | hit ratio up after Phase 4; `reject alpha` down after 4d |
| `reject depthUv` | watch after 1/4c; sharp rise ⇒ raise error floor |
| GPU timestamps | "GlobalSdfMips" disappears (Phase 3); DdgiTrace down (Phase 2) |

3. Validate DDGI probe visibility (Chebyshev moments shift because refined hits land up to ~0.5 m later than today's early accepts): check leak/darkening in the DDGI validation scenes (`SampleGlobalIlluminationValidation`/`SampleDdgiProductionGate`).
4. Movement test: walk 10–20 m and stop — no stale seam planes (Phase 3 removes the incoherent-mip contribution; if stale shells persist, the `SDDFFF.md` Task D clipmap-invalidation audit is next).

## Risks
- Grazing-ray step counts at LOD0-only (monitor `stepExhausted` before adding any occupancy structure).
- Probe visibility-moment consumers shift with later hit t — validate, don't assume.
- Phase 3 layout regression: preserve the `TransitionToShaderRead` currently done by the mip pass.
- Test blast radius: ~60 pinned strings in `GlobalSdfShaders_UseToroidalClipmapBrickAddressing` — rewrite per phase, same commit.

## Branch
Develop on `claude/sdf-surface-cache-quality-qhbyll`; commit per phase, push with `git push -u origin`.