# R2-7: Global SDF + Surface Cache ray backend for DDGI (hybrid)

Plan for an implementing agent on the Njulf4.0 renderer (`Simplified` lineage, C#/Vulkan + GLSL, bindless descriptors). Backwards compatibility is NOT required — old paths are deleted once parity gates pass. Goals: performance (cheap, coherent probe rays; far more probes updated per frame) and image quality (real multi-bounce interiors, stable energy).

## Architecture (hybrid only — no other backend modes ship)

- **Global Surface Atlas (surface cache)**: per-object lit "cards" holding radiance. Used by ALL probe-ray hit shading — replaces the per-hit analytic light + shadow-ray loop in `EvaluateDirectDiffuseRadianceAtHit`/`EvaluateStableDdgiDiffuseRadianceAtHit` (`Njulf.Shaders/ddgi_update_shared.glsl`). This is where most of the win lives: hit shading becomes one atlas fetch, and the atlas already contains last frame's DDGI → infinite bounce for free.
- **Global SDF clipmap**: camera-centered distance-field cascades serving (a) probe rays on far DDGI cascades, (b) relocation/classification inside-geometry distance queries. It is NOT a general-purpose ray backend for near-field probes, and there is no pure-SDF product mode.
- **Fixed backend split**: DDGI cascades 0–1 always trace hardware ray queries (precision, no thin-wall SDF artifacts) with surface-cache hit shading; cascades ≥ `SdfBackendFirstCascade` (default 2 — a tuning knob, not a mode switch) march the global SDF with surface-cache shading. The existing full-analytic hit shading survives only as a debug reference until the M4 parity gate passes, then is deleted.

## Infrastructure inventory (verified against the codebase)

- Bindless heap (`common.glsl:1197-1207`): storage buffers + `sampler2D/2DArray/Cube/CubeArray` aliases. **No 3D textures anywhere — must be added (M0).**
- Offscreen raster infra exists (`Pipeline/SpotShadowPass.cs`, `DirectionalShadowPass.cs`, `PointShadowPass.cs`, `Resources/RenderTarget.cs`) — reuse for card capture.
- Mesh vertex/index/material buffers are already GPU-readable storage (ray-query hit shading `ResolveCommittedHitSurface` consumes them) — a GPU SDF-bake compute pass can too.
- Pass pattern to copy: `Pipeline/DdgiPipelinePasses.cs` (`DdgiComputePass` base — timestamps, barriers, indirect dispatch, diagnostics slots); submission order in `VulkanRenderer.cs` (~lines 600–700).
- Dirty tracking exists (DDGI dirty regions, `DDGI_PROBE_UPDATE_REASON_GEOMETRY_*`) — reuse the same events for SDF-brick and card invalidation.
- Quality tiers + memory gates: `RenderSettings.ApplyDdgiQualityTier` (~`RenderSettings.cs:2000-2060`) + `SampleDdgiProductionGate` tier validation.
- No existing SDF code (verified by search).

## Milestones — each is a PR-sized unit with its own gates

### M0 — Infrastructure enablers
1. 3D textures: add `sampler3D`/`image3D` aliased bindings to the bindless heap (mirror the Cube alias pattern, `common.glsl:1204-1207` + `Njulf.Rendering/Descriptors` BindlessHeap/BindlessIndex) and 3D image creation in the resource layer (`VkImageType.Type3D`, mip chains; follow `RenderTarget.cs` patterns).
2. Settings + tiers: the hybrid ships always-on — no backend enum. Tunables only: `SdfBackendFirstCascade` (default 2), per-tier SDF/atlas resolutions and per-frame budgets, plus debug-only toggles (`DebugSurfaceCacheAnalyticFallback`, debug views), all wired through `ApplyDdgiQualityTier`.
3. Pass scaffolding: `GlobalSdfPasses.cs`, `SurfaceCachePasses.cs` following `DdgiComputePass` (GPU timestamps, diagnostics counters, async-compute flags). Insert after TLAS/scene update, before `DdgiSchedulePass`.
4. Gates: shader-build + GPU-struct-layout tests for new structs; a 3D-texture compute write/read smoke test.

### M1 — Mesh SDF bake
1. GPU bake compute pass on mesh registration: per-mesh voxel grid, resolution by extent (~1 voxel per 2–4% of max extent, clamp 8³–64³; thin meshes get ≥2 voxels across thickness). Distance = min over triangles (brute force with workgroup triangle tiling is fine at these resolutions). Sign via angle-weighted pseudonormal of the closest triangle (robust on open meshes — do NOT use ray parity). Store normalized distance (÷ max extent) as R16_SFLOAT 3D texture per mesh; register bindless index + local-from-world in a `GPUMeshSdf` struct.
2. Amortized bake queue (e.g. 2 meshes/frame). Unbaked meshes are simply absent from the SDF; near-field hardware rays are unaffected meanwhile.
3. Gates: C# mirror test for voxel↔world addressing; SDF-slice debug view; bake-time/queue counters.

### M2 — Global SDF clipmap
1. 4 camera-centered cascades sized for the hybrid's actual consumers (High: 192³ ≈ 14 MB R16F each; Low: 128³): cascade 0 (~0.12 m voxel) exists from day one for future SDF consumers (shadows/AO/reflections) and relocation/classification distance queries, while DDGI rays only ever use cascades ≥ `SdfBackendFirstCascade`; cascades 1–3 (~0.25 / 0.5 / 1.0 m voxels) cover progressively larger DDGI/far-field ranges. No fine near-field DDGI SDF path is needed because near probes trace hardware rays.
2. Per-frame update: scroll exactly like the DDGI probe clipmap (reuse conventions from `CameraRelativeDdgiClipmapController.cs`); re-rasterize only dirty 8³ bricks (sources: scroll-in regions, the existing DDGI dirty-region events, dynamic-object movement). Brick content = min() over mesh SDFs of instances intersecting the brick (instance list from the TLAS instance buffer, coarse-culled per brick). Static content persists; dynamic instances re-inject their bricks each frame.
3. `global_sdf.glsl` trace function: cascade-select by position, sphere-trace with mip acceleration, step out to coarser cascade at boundary; returns hit t, cascade, central-difference normal. Budget ≤ 0.5 ms steady-state update on High tier (4 cascades).
4. Gates: full-screen SDF raymarch debug view; switch relocation/classification inside-geometry tests to `SampleGlobalSdf` (cheap win over fixed-ray heuristics); counters: bricks updated/frame, average trace steps.

### M3 — Surface cache (Global Surface Atlas)
1. Cards: per object (or per 8–10 m chunk of large objects), 6 axis-aligned orthographic projections. Tile allocator over a physical atlas (High: 4096²; tiles 8–64 px by object size/distance; shelf or quadtree allocator with LRU eviction) + indirection table `GPUSurfaceCard {objectIndex, axis, atlasRect, worldFromCard, depthRange, lastCaptureFrame}`.
2. Capture pass (raster, reuse shadow-pass infra): per-tile mini-GBuffer — albedo, packed normal, emissive, depth. Budget N tiles/frame (High: 64); priority = newly allocated > dirty (moved/material change) > stale LRU.
3. Light pass (compute, every frame over a budgeted texel window): `radianceAtlas = direct + emissive + indirect`. Direct: evaluate existing light structs at card texels with one ray-query shadow ray per selected light (reuse `TraceLightVisibility`). Indirect: previous-frame DDGI via `SampleStableDdgiIrradiance` at card position/normal × albedo/π → the atlas holds fully lit radiance including infinite bounce. Single-buffered is fine (DDGI already tolerates 1-frame latency).
4. Hit→card lookup: uniform world grid per SDF cascade (cell → small card/object index list), rebuilt alongside SDF brick updates.
5. Gates: atlas debug views (albedo/radiance/age); counters (tiles captured, texels lit, occupancy %, eviction rate); mirror test for card projection math.

### M4 — DDGI integration (the payoff)
1. Hit shading via cache in `TraceProbeRay`: grid lookup at hit point → best cards by `dot(hitNormal, cardAxis)` + card depth test → bilinear radiance fetch, blending the top 2 cards to hide seams. Fallback to the current analytic path behind `DDGI_SURFACE_CACHE_FALLBACK` with a counter; the production gate requires < 2% fallback on validation scenes, after which the analytic hit-light loop is deleted.
2. Backend split: cascade ≥ `SdfBackendFirstCascade` (default 2) traces via `SampleGlobalSdf` march instead of `rayQueryEXT`; visibility moments = march hit t (same convention); miss = existing `SampleDdgiEnvironmentMissRadiance`. Cascades 0–1 keep `rayQueryEXT` unconditionally.
3. Budget shift: hit shading is now ~1 fetch → raise per-probe rays (cascade-0 128 → 192+) and adopt the ray-sample scheduler budget (R2-5) so cascade-0 probes update near-every-frame. This plus cache-borne infinite bounce is what makes interiors bright and stable.
4. Gates: analytic-vs-cache energy parity on the validation matrix (thick-walled colored room, thin-wall corridor, Sponza, emissive room): `hybridFinalLum` delta ≤ 10%, thin-wall leak ratio unchanged, light-toggle convergence ≤ 8 frames, `gpuDdgiUs` P95 within tier budget at the raised ray counts.

### M5 — Production hardening
- Diagnostics: extend `RendererDiagnostics` + triage lines (sdfBricks, sdfSteps, cacheTiles, cacheFallback%, atlasOccupancy); debug views: per-ray backend heatmap, card-projection view, SDF slice.
- Memory gates per tier added to the production gate: High ≈ mesh SDFs 64 MB + global SDF 4×14 MB + capture/radiance atlases 2×4096²×8 B ≈ 128 MB → total GI memory stays ≤ tier targets (384 MB Ultra / 256 MB High); fail the gate otherwise.
- Perf targets (GPU, High tier, P95): global SDF ≤ 0.5 ms, capture ≤ 0.3 ms, light pass ≤ 0.4 ms, DDGI trace+blend ≤ 1.0 ms at ≥2× current ray throughput.
- Deletions once M4 gates pass: analytic per-hit light loop, `DdgiMaxShadedLights` hit-shading plumbing, dead settings.

Status: implemented locally. The M5 diagnostics counters now publish SDF traces/average steps, ray-query traces, surface-cache hits/fallbacks, cache fallback percentage, cache tiles, atlas occupancy, hybrid memory bytes, and High-tier GPU budget status. The new debug views use the same forward frame-id path as DDGI (`120..122`) and are included in the existing DDGI investigation shortcut cycle (`Ctrl+V`): global SDF slice, surface-cache card projection, and DDGI ray-backend heatmap.

## Risks / mitigations
- **Thin geometry vs SDF** (0.15–0.3 m walls): low severity in the hybrid — near-field probes always use hardware rays; SDF thinness artifacts only affect far-cascade rays against distant thin geometry, where probe spacing already exceeds wall thickness. Thin-wall validation scene remains a gate.
- **Cache staleness** (moving lights/objects): the light pass re-lights every frame and its budget window must cover all lit tiles within ≤ 4 frames; captures re-queued by existing dirty events.
- **Card seams**: 2-card weighted blend + 1-texel atlas gutters.
- **Sign errors on open/degenerate meshes**: pseudonormal sign with an unsigned-fallback flag + warning counter.
- **Memory**: tile resolution and cascade resolution are the two pressure valves — both tier-configurable and gate-enforced.

## Order & verification
M0 → M1 → M2 → M3 → M4 → M5, one commit/PR per milestone, each verified via its gates plus the standing harness (`ShaderBuildTests`, mirror tests, `GPUStructLayoutTests`, benchmark suite, `SampleDdgiProductionGate`, 200-frame stability capture on the validation scenes).
