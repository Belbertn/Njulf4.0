# Performance Pass + Camera-Relative DDGI Probe Reach

## Context

The renderer is far over the 1080p60 budget and the camera-relative DDGI probes barely reach past the camera:

- GPU frame ~63ms; **ForwardPlusPass alone is ~51ms**. The depth prepass draws *more* meshlets in 0.67ms → forward is fragment-shading-bound, amplified by ~2.2M mostly sub-pixel triangles (bad quad occupancy).
- **LOD never engages**: LOD1/LOD2 meshes are generated but thresholds (12×/32× bounding radius, hardcoded in `scene_opaque_compact.comp:17-18` and `SceneDataBuilder.cs:43-44`) never trigger in an interior scene → all 107,952 meshlets render at LOD0; shadows re-emit ~3× that across 3 cascades.
- **Per-pixel DDGI gather is heavyweight**: 8-corner trilinear × 2 manual-bilinear samples from *storage buffers* (~64-72 buffer loads/pixel), doubled near volume edges.
- CPU DrawScene ~33ms, but capture was (per user) probably a **Debug build with the validation layer on** (default `Standard` under `#if DEBUG`) — ~9 `CmdPipelineBarrier2` calls cost 6.2ms (~690µs each, validation signature). Real CPU issues remain: O(N²) barrier-summary string building every frame, per-frame label interpolation, Hi-Z record overhead (existing plan `Njulf/Plans/HiZCpuOptimizationPlan.md`).
- **DDGI reach**: 3 camera-relative rings share one 24×12×24 grid at 1m/4m/16m spacing + 1 authored box. Rings whose lattice covers the scene are statically scene-centered, so only ring 0 tracks the camera — reaching **±11.5m horizontally / ±5.5m vertically** ("a handful of meters"). 31,890 of 32,768 probe cap used; probeAge p95 = 41 frames.

User decisions (via AskUserQuestion): capture was probably Debug; **DDGI = rebalance for reach + enable sampled-atlas textures**; **all visual trades accepted** (aggressive LOD, shadow LOD bias, fewer near DDGI rays), everything tunable via settings.


## Track A — CPU quick wins & measurement hygiene

Order: A2 → A4 → A3 → A1 → A5.

- **A1. Pacing-line fields** — `NjulfHelloGame/SampleDiagnosticsReporter.cs` (`PrintMovementFrameDiagnostics`, ~680-688): append `validation={diagnostics.ValidationMode}` and `primaryRecordUs={CpuPrimaryCommandRecordMicroseconds}` (both already exist on `RendererDiagnostics` — lines 1211/664; reporter-only change). Keeps future captures unambiguous and surfaces the currently-invisible non-itemized record time.
- **A2. Fix O(N²) barrier summary** — `Njulf.Rendering/Pipeline/RenderGraph.cs:672` calls `BuildBarrierSummary()` (685-695) per barrier, re-formatting all previous barriers. Replace with a reusable `StringBuilder` appended once per barrier; assign `sceneData.GraphBarrierSummary` once at end of execution. Don't clobber the async-plan summary written at `VulkanRenderer.cs:5015` (guard on non-empty). Clear in `ResetBarrierPlanning`. Test: barrier-count assertion in `RenderGraphResourceDeclarationTests`.
- **A3. Debug-label allocations** — `DirectionalShadowPass.cs:111,138,167` interpolate `$"...Cascade {cascade}"` per frame even when debug utils are off. Cache `static readonly string[]` labels (cascade capacity 4). Grep `BeginDebugLabel(cmd, $"` for other loop call sites.
- **A4. Cache `Vk.GetApi()`** — `Njulf.Rendering/Utilities/BarrierBuilder.cs:83,96,104` construct a new API object per barrier; hoist to a `static readonly Vk` field. Every graph + Hi-Z barrier goes through this.
- **A5. Hi-Z record (remaining items of `Plans/HiZCpuOptimizationPlan.md`)** — `HiZBuildPass.cs` already has per-mip descriptor sets + precomputed push constants/dispatch dims (`MipRecordMetadata`). Remaining: (1) precompute per-mip `ImageMemoryBarrier2` templates in `RebuildMipMetadata` (patch only layout/stage fields per frame); (2) merge the top depth-transition + pyramid-to-general into one `CmdPipelineBarrier2` with 2 image barriers. Re-measure in Release before pursuing more; the SPD-style single-dispatch build is a follow-up only if `hizRecordUs` still exceeds the 1000µs gate.

## Track B — GPU structural wins (the 51ms forward pass)

Order: B1+B2 as one shader/struct change → B3.

- **B1. Plumb LOD distance ratios, lower defaults** — new `SceneSubmissionSettings` (RenderSettings.cs, ~line 737): `GpuLod1DistanceRatio` default **4.0** (clamp [1,64]), `GpuLod2DistanceRatio` default **10.0** (clamp ≥Lod1, ≤128). Plumbing follows the existing pattern: settings → `SceneRenderingData` (+`Reset()`) → `VulkanRenderer.cs:~1404` → `GPUSceneOpaqueCompactionPushConstants` (append 2 floats at end; mirror in `common.glsl:894`; `GPUStructLayoutTests` auto-validates) → `SceneOpaqueCompactionPass.cs:241-285` fills → `scene_opaque_compact.comp` `SelectLodLevel` (427) reads push constants instead of consts (delete lines 17-18). **Also update the CPU mirror** `SceneDataBuilder.cs:43-44` (+hysteresis at 1660-1672) from the same settings so CPU-fallback frames don't pop. Old behavior reachable via 12/32. Persist in the `SceneSubmissionFile` record (~4327-4402). Update `RendererSettingsReference.md`.
- **B2. Shadow LOD bias** — new `SceneSubmissionSettings.GpuShadowLodBias`, int, default **1**, clamp [0,2]. Same plumbing ride-along (one more push-constant uint + padding). In `scene_opaque_compact.comp`, `ApplyGpuLodSelection` gains a `lodBias` param (`min(lod + bias, 2)`); `ProcessDirectionalShadowCandidate` (639) passes it, opaque/depth paths pass 0. 2048² cascades can't resolve LOD0 anyway. Do NOT add reduced-frequency far cascades (swimming risk, marginal gain) — the static cascade cache already exists (`DirectionalShadowPass.IsStaticCacheDirty`) but its signature hashes camera-fitted cascade matrices, so it only helps stationary views; leave as-is. Verify scene objects are actually tagged static (`SceneOpaqueCompactionPass.cs:140-149` already skips static shadow re-compaction).
- **B3. DDGI gather cost** —
  1. **Enable the already-built sampled-atlas path** (user-approved): `SimpleDdgiSampledAtlasEnabled` (RenderSettings.cs:1591) is fully implemented (fp16 texture-array mirror synced after blend, HW-bilinear interior-quad path in `ddgi_simple_shared.glsl:509-567`, budget guard + fallback reason already wired) but every tier forces it **false** (RenderSettings.cs:2451). Enable in the sample scenario first, A/B `forwardUs` + `LastSampledAtlasSynchronizationMicroseconds` + `SampledAtlasImageBytes`; if clean, default it on for DdgiHigh+. Note: SSBO stays canonical (seam samples still use it), so this *adds* image memory rather than saving memory — the win is gather speed (up to 8 SSBO loads → 1 filtered fetch per corner-sample).
  2. **Relax second-volume early-out** — `ddgi_simple_shared.glsl:901` requires `ownership >= 0.999`, so almost any interior pixel pays a second 8-corner gather. Relax to `ownership >= 0.95` and add an investigation counter for second-gather rate. A/B with `DdgiCoverage`/`FinalIndirect` debug views.
  3. Deferred (only if still over budget): skip visibility-moments sampling for corners with negligible trilinear weight.

## Track C — Camera-relative probe reach

Order: C4 (observability) with/before C1 → C2. C3 intentionally skipped.

- **C1. Per-ring grids + retuned spacing** — replace the single shared ring grid with per-ring settings in `GlobalIlluminationSettings` (legacy `SimpleDdgiRingGridSizeX/Y/Z` becomes a setter for all three):
  | ring | grid (new setting, DdgiHigh default) | spacing (base 1.0→**1.25**, mult 4.0→**3.0**) | probes | reach |
  |---|---|---|---|---|
  | near | `SimpleDdgiNearRingGridSizeX/Y/Z` = 28×14×28 | 1.25m | 10,976 | **±16.9m / ±8.1m** |
  | mid | `SimpleDdgiMidRingGridSizeX/Y/Z` = 18×10×18 | 3.75m | 3,240 | ±31.9m / ±16.9m |
  | far | `SimpleDdgiFarRingGridSizeX/Y/Z` = 12×8×12 | 11.25m | 1,152 | ±61.9m / ±39.4m |
  Rings total 15,368 (47% of cap) → 17,400 headroom for authored volumes (Sponza slab 11,154 fits). Keep `MaxSimpleDdgiTotalProbeCount=32768` unexposed (it sizes ~592MB of buffers; graceful degradation via `EnforceProbeBudget` exists). Code: `SimpleDdgiVolumeManager.CreateRingVolume` (1857-1862) switches grid by ringIndex. Scale other tiers proportionally in `ApplySimpleDdgiQualityTier`; update sample overrides in `SampleGlobalIlluminationValidation.cs:386-391`. Persist new fields in the settings-file record.
- **C2. Update quotas + ray rebalance** — machinery exists (`ResolveVolumeQuotas` weights 6/3/1). Retune DdgiHigh in `ApplySimpleDdgiRingQualityTier`: near quota 384/672 → **512/1024** (ring-0 full sweep ≈ 11 frames), mid → 96/324, far → 24/128; keep `SimpleDdgiProbeUpdatesPerFrame=2048`. Offset GPU cost: `SimpleDdgiNearFullRaysPerProbe` 96 → **64** (user-approved; maintenance stays 24) so scheduled primary rays stay ≈ flat while `gpuDdgiUs` (2687µs, already over the 1.5ms tier gate) trends down. Contingency if authored volume starves: kind-aware weight split in `ResolveVolumeQuotas` (1337-1342).
- **C3. Camera-biased origin for scene-covering rings — skipped**: when the lattice covers the scene, coverage is already complete; biasing to the camera only moves overscan outside geometry and burns update budget on scroll-invalidated probes.
- **C4. Per-ring reach + age diagnostics** — add `EstimatedAgeP95Frames` to `DdgiVolumeDiagnosticsEntry` (computed per volume from CPU-resident `_probeAges` in `SimpleDdgiVolumeManager.GetVolumeDiagnostics`, 359-420; legacy manager defaults 0). New reporter line: `Frame diagnostics DDGI rings: ring0 grid=28x14x28 spacing=1.25 reach=±16.9/±8.1m ageP95=…; …` (reach from existing SizeX/Y fields). Tests in `DdgiDiagnosticsFormattingTests`.

## Tests to add/update

`GPUStructLayoutTests` + `ShaderBuildTests` (auto), `RendererDiagnosticsTests` (new settings defaults/clamps; tier-ordering block ~1442 → per-ring fields), `SceneDataBuilderTests` (parameterized LOD ratios), `SampleGlobalIlluminationValidationSettingsTests:29-32` (per-ring budget math), `SimpleDdgiVolumeManagerTests` (per-ring grid selection, quota expectations, age-p95 helper), `RenderGraphResourceDeclarationTests` (barrier summary).

## Verification

1. `dotnet build Njulf.sln -c Release` (rebuilds shaders) + `dotnet test Njulf/Njulf.Tests`.
2. Compare against Step-0 baseline (Release, `NJULF_RENDERER_VALIDATION=off`):
   - Track A: pacing line shows `validation=Off`, `primaryRecordUs`; `hizRecordUs` < 1000µs, `mipDependenciesAndFinalLayout` < 500µs (gates from `HiZCpuOptimizationPlan.md`); cpuDrawMs p95 down.
   - Track B: `forwardUs` large drop from ~51ms; `gpuLod=a/b/c` shows LOD1/LOD2 nonzero; `gpuDirShadow` emits down; `SampledAtlasActive=1` with empty fallback reason; shadow quality via `ShadowDebugView`.
   - Track C: new `DDGI rings:` line shows ring0 reach ±16.9/±8.1m, ring0 ageP95 low teens; walk the scene edge with `DdgiCoverage` debug view — coverage persists to ~±17m before mid-ring takeover; `gpuDdgiUs` ≤ 2687µs baseline; `maxBudget` < 85%.
3. Regression sweep: GI validation scenarios (`GiSponzaRightWallStationary`, `GiCornellRoom`, `GiQualityInterior`) — `ddgiEstimate` luminance fields vs baseline; watch `simpleEvents recenter/clear` churn after C1.

## Risks

- B1 defaults may pop visually → tunable, old values reachable, CPU hysteresis exists.
- B3.1 default flip is device-dependent → keep opt-in until the A/B capture is clean (fallback reason is surfaced).
- C1 changes recenter/scroll churn → watch investigation-line counters.
- Push-constant struct growth guarded by `ValidatePushConstantRange` + `GPUStructLayoutTests`.