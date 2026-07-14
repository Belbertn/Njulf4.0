# Simplified-SDF: Minimal DDGI rebuild + coarse far-field clipmap (Njulf4.0)

## Context

Two systems on `Simplified` are overbuilt and misbehaving:

1. **The SDF/surface cache** (~40 commits, d5d4e98..0bedea6): Lumen-style per-mesh SDFs + toroidal clipmap + brick updates + card-based surface cache (~10.8k LOC C#, ~6.5k GLSL). Never stabilized; bugs clustered in toroidal scrolling, dirty-brick bookkeeping, non-uniform-scale distance conversion, and hit→card projection (post-mortem: `Njulf/implementation/SDFHope3.md`).
2. **DDGI itself**: at the pre-SDF commit it already rendered too weak. The repo's own `implementation/DDGIProductionBounceLightingPlan-20260703.md` names the causes: possible double albedo/π between probe update and forward gather, a **nine-term multiplicative confidence chain** in the gather (any one low channel kills GI unattributably), and complexity beyond the reference implementations ("WickedEngine is simpler than our scheduler and clipmap system").

Decisions made with the user:
- Restart from `fcdb0788f92eee85c94e40aadee143f4ee37ced6` (last pre-SDF commit) on new branch **`Simplified-SDF`**.
- **Keep DDGI as the technique, rebuild it minimally** from the references the repo already cites (RTXGI / WickedEngine / JCGT) — fixed probe grid, no cascade clipmap, no GPU scheduler, no confidence chain, relocation off in v1. Legacy DDGI stays dormant behind a setting as an A/B reference.
- Ray backend tiering (user's): near/thin/important geometry → existing TLAS ray query; **far static geometry → coarse occupancy clipmap (the only new "SDF" work)**; dynamic BLAS/skinned proxies → later.
- Out of scope permanently (the tar pits): toroidal addressing, incremental brick/dirty updates, per-mesh SDF bake+composition, card surface cache, multi-gate confidence stacks.
- Process fix: every piece of GPU math lands with a CPU mirror + source-contract test (pattern: `Njulf.Tests/DdgiShaderModelMirrorTests.cs`), plus an analytic-box-scene conformance oracle; never screenshot-only verification.

Reusable plumbing at fcdb0788 (verified): hardware ray-query probe loop `TraceProbeRay` (`Njulf/Njulf.Shaders/ddgi_update_shared.glsl:1666`) with backend-agnostic hit evaluators (`EvaluateDirectDiffuseRadianceAtHit` :1036, `EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit`, `EvaluateStableDdgiDiffuseRadianceAtHit` :1373, `SampleDdgiEnvironmentMissRadiance` :921, light visibility `TraceLightVisibility` :885); TLAS + per-instance data (`Resources/AccelerationStructureManager.cs`, `CollectStaticOpaqueInstances` :200); bindless split vertex/index streams (slots 65/66/67/4, `common.glsl`); compute-pass pattern (`Pipeline/DdgiPipelinePasses.cs`, 2-set variant `Pipeline/PipelineObjects/ComputePipeline.cs`); buffers via `Memory/BufferManager.CreateBuffer` + slots in `Descriptors/BindlessIndexTable.cs`; shader auto-glob build + `ShaderBuildTests.RequiredShaders`; pass order in `Pipeline/ProductionRenderPipelineDeclaration.cs:15`.

## Phase 0 — Branch setup

1. `git checkout -b Simplified-SDF fcdb0788f92eee85c94e40aadee143f4ee37ced6`; `git push -u origin Simplified-SDF`.
2. Commit this plan as `Njulf/implementation/SimplifiedSdfPlan-20260711.md`.
3. `dotnet test Njulf/Njulf.sln` to record the baseline (fcdb0788 sits mid-plan; fix only build/test blockers).

## Phase 1 — Minimal DDGI rebuild ("v1 working")

A new, small, parallel probe path. Legacy DDGI untouched and selectable for A/B.

### 1a. Mode + settings
`GlobalIlluminationSettings` (`Data/RenderSettings.cs:1367`, follow the `UseRayQueryBackend`/`Effective*` pattern): `DdgiSimpleEnabled` (master switch: legacy DDGI passes skip via `ShouldExecute`, simple passes run), `SimpleDdgiProbeSpacing` (default ~1.25 m), `SimpleDdgiRaysPerProbe` (128; first frame max for fast convergence), `SimpleDdgiHysteresis` (0.97), `SimpleDdgiProbeUpdatesPerFrame` (0 = all; plaza grid is small).

### 1b. Volume manager
New `Resources/SimpleDdgiVolumeManager.cs` (model: `DdgiProbeVolumeManager` buffer fields + `Register(BindlessHeap)` :449). Fixed probe grid auto-fit to static scene bounds (plaza ~30 m ⇒ ~32×8×32 probes). Buffers (storage buffers, packed-half convention like existing atlases): irradiance atlas 8×8 octahedral texels/probe; visibility atlas 16×16 (distance, distance²); ray scratch (probes × rays × radiance+distance); `GPUSimpleDdgiParams` params buffer (grid origin/spacing/counts, ray count, hysteresis, frame index/rotation seed). New `BindlessIndex` consts before `StaticBufferCount` (`BindlessIndexTable.cs:634` + debug-name switch ~:802).

### 1c. Shaders (all new; small)
- `ddgi_simple_shared.glsl` — params read, octahedral mapping, spherical-Fibonacci direction gen with per-frame random rotation, probe indexing, and `vec3 SampleSimpleDdgiIrradiance(worldPos, normal, viewDir)`: 8-probe trilinear × backface × Chebyshev visibility (with self-shadow bias) — **no confidence chain**. Returns irradiance.
- `ddgi_simple_trace.comp` — one thread per ray: ray-query the TLAS; shade hits with the existing evaluators — **extract** `EvaluateDirectDiffuseRadianceAtHit` + emissive/light-selection helpers + sky miss from `ddgi_update_shared.glsl` into a shared include (`ddgi_hit_shading.glsl`) as a pure move (legacy include keeps including it; source-contract tests pin the move); the recursive-bounce term samples previous-frame `SampleSimpleDdgiIrradiance` at the hit. Writes radiance+distance to scratch. *(This is the seam Phase 2 plugs into.)*
- `ddgi_simple_blend.comp` — per probe texel: JCGT/RTXGI cosine-weighted accumulate + hysteresis into irradiance; sharper-exponent weights for visibility moments; firefly clamp + half-overflow guard (WickedEngine notes in the bounce plan).
- **Convention locked with mirror tests**: rays store incoming radiance; atlas stores irradiance; forward applies `albedo/π` exactly once.
- `forward.frag`: when simple mode, diffuse GI = `SampleSimpleDdgiIrradiance(...) * albedo / π`. Wire the handful of most-used debug views to the simple path (`FinalIndirect`, `DdgiIrradiance`, `DdgiVisibilityMoments`, `DdgiProbeIndex` — enum at `RenderSettings.cs:365`).

### 1d. Passes
New `Pipeline/SimpleDdgiPasses.cs`: `SimpleDdgiTracePass` (requiresRayQuery, model `DdgiTracePass` :16) + `SimpleDdgiBlendPass` (2-set). Register in `VulkanRenderer.BuildRenderPasses` (~:648) + `ProductionRenderPipelineDeclaration._passOrder`. Update all probes per frame (round-robin knob exists but defaults off).

### 1e. Tests + verification
- Mirror tests (new `SimpleDdgiShaderMirrorTests.cs`): Fibonacci directions, octahedral round-trip, blend hysteresis energy (against a CPU integration of a constant-radiance sphere ⇒ atlas must converge to that constant — the bounce plan's normalization check), Chebyshev weight; source-contract pins on the convention lines and the extracted include.
- `ShaderBuildTests.RequiredShaders` += 2 comps; pass-order test update; settings-default assertions.
- GPU: plaza scene, `DdgiSimpleEnabled=true` — colored-wall bounce visible on floor, no leaks through interior walls, converges <1 s, stable under camera motion (fixed grid ⇒ nothing scrolls). A/B flip to legacy DDGI for comparison. **Success gate for v1.**

## Phase 2 — Coarse far-field occupancy clipmap

Unchanged in substance from prior design; the seam is now `ddgi_simple_trace.comp`.

- **Data**: one world-anchored storage buffer, 4 B/voxel (`RGB8 albedo + flags`, bit0 = occupied), default 128³ = 8 MB, extent auto-fit to static scene bounds (plaza ⇒ ~0.3–0.5 m voxels). `GPUFarFieldClipmapParams` params buffer + `GPUFarFieldInstance { VertexOffset, IndexOffset, IndexCount, MaterialIndex, mat4 World }` instance buffer (note: `GPUDdgiRayQueryInstance` lacks `IndexCount`) built from the `CollectStaticOpaqueInstances` walk (refactor into shared helper). New `Resources/FarFieldClipmapManager.cs` + bindless slots.
- **Bake**: `farfield_voxelize.comp` (clear/voxelize modes; one thread per triangle, per-instance dispatch ~40 instances; SAT box-triangle overlap; `atomicOr` occupancy + albedo from `ReadMaterial`); `Pipeline/FarFieldClipmapBakePass.cs` (2-set pattern), runs while `BakePending` (once at load).
- **Trace**: `farfield_clipmap.glsl` — `bool TraceFarFieldClipmap(origin, dir, tMin, tMax, out t, out faceNormal, out albedo)`, 3D DDA, hit = first occupied voxel, normal = entered face. In `ddgi_simple_trace.comp`: rays with expected length beyond `FarFieldStartDistance` (or all rays when `FarFieldForceAll` debug flag) trace the clipmap instead of the TLAS, then reuse the same hit-shading epilogue. Light visibility at far hits keeps the ray-query shadow ray in v1.
- **Settings**: `FarFieldClipmapEnabled` (default false until verified), `FarFieldClipmapResolution` (128), `FarFieldStartDistance`, `FarFieldMaxTraceSteps` (256). Diagnostics counters (far rays/hits/step-exhausted, baked triangles) via `RendererDiagnosticsBuffer`; debug views `FarFieldOccupancySlice`/`FarFieldTraceResult`.
- **Tests**: new `FarFieldClipmapOracleTests.cs` — CPU mirror of addressing + SAT overlap + DDA vs the **analytic box SDF** of the stress-scene transforms (`SampleStressSceneBuilder.cs`): every analytic surface voxel occupied (conservative), no occupancy >1 voxel off-surface, DDA hits within 1 voxel of analytic hits. Struct layout lockstep + source contracts as in Phase 1.
- **GPU A/B**: `FarFieldForceAll=true` vs TLAS-only — compare irradiance debug views + diagnostics; then production split by `FarFieldStartDistance`; inspect occupancy slice for crisp box footprints.

## Phase 3 — Camera-following volume (only when roaming exceeds the fixed volume/grid)

Applies to both the probe grid and the clipmap, same dumb policy: world-anchored, snap-recenter on threshold crossing, **full rebake into a second buffer amortized over N frames, flip on completion** (probe grid: re-center and let hysteresis reconverge, or double-buffer likewise). No toroidal addressing, no partial invalidation, ever.

## Phase 4 — Later (outline)

- Simple probe relocation (backface-fraction nudge) if in-wall probes prove problematic.
- Distance upgrade for the clipmap: jump-flood R16 distance (+4 MB) for faster marching, same oracle.
- Occupancy-march light visibility (drops far-field ray-query dependency); dynamic rigid TLAS refit; skinned proxies.
- Other consumers (reflections, AO, particle collision) call `TraceFarFieldClipmap`.
- Delete legacy DDGI + (eventually) SSGI once the simple path reaches parity.

## Files summary

| Action | Path |
|---|---|
| new | `Resources/SimpleDdgiVolumeManager.cs`, `Resources/FarFieldClipmapManager.cs` |
| new | `Pipeline/SimpleDdgiPasses.cs`, `Pipeline/FarFieldClipmapBakePass.cs` |
| new | `Njulf.Shaders/ddgi_simple_shared.glsl`, `ddgi_simple_trace.comp`, `ddgi_simple_blend.comp`, `ddgi_hit_shading.glsl` (extraction), `farfield_clipmap.glsl`, `farfield_voxelize.comp` |
| new | `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`, `FarFieldClipmapOracleTests.cs` |
| edit | `Njulf.Shaders/ddgi_update_shared.glsl` (pure extraction), `forward.frag` (simple gather branch) |
| edit | `Data/RenderSettings.cs`, `Data/GPUStructs.cs`, `Data/RendererDiagnostics.cs`, `Descriptors/BindlessIndexTable.cs`, `VulkanRenderer.cs`, `Pipeline/ProductionRenderPipelineDeclaration.cs` |
| edit | `Njulf.Tests/ShaderBuildTests.cs`, pipeline-declaration + settings tests |

## Verification

1. `dotnet test Njulf/Njulf.sln` — mirror, oracle, shader-embed, pass-order suites (CPU-only, no GPU needed).
2. `NjulfHelloGame` plaza: Phase 1 gate (visible bounce, no leaks, <1 s convergence, stable in motion; A/B vs legacy DDGI) → Phase 2 A/B (`FarFieldForceAll` vs TLAS) → production split.
3. Budget sanity via existing diagnostics (DDGI update ≤ ~1.5 ms High-tier target from the bounce plan; clipmap bake is load-time).

## Risks

- The evaluator extraction from the 2519-line legacy include must be a pure move (source-contract tests + legacy still compiling via `ShaderBuildTests` keep it honest).
- Fixed grid without relocation ⇒ probes inside walls read black; Chebyshev + backface weights mask most of it in the plaza; relocation-lite is a Phase 4 item, not a v1 blocker.
- DDA cost on long grazing rays (~few hundred steps): confined to far rays; Phase 4 distance upgrade is the fix, not heuristics.
- Legacy + simple DDGI coexisting doubles some surface area temporarily; acceptable as the A/B oracle until parity, then legacy is deleted (Phase 4).