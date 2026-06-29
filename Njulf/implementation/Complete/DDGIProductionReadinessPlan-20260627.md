# DDGI Production Readiness Plan

Date: 2026-06-27

## Goal

Move Njulf to a production-ready DDGI-first renderer for high quality real-time lighting while keeping SSGI available only as a non-default diagnostic or optional future near-field detail layer.

The production renderer should use:

- DDGI for diffuse global illumination.
- Screen-space/contact AO for high-frequency occlusion.
- Existing reflection probes/IBL for specular indirect light, with SSR treated as a separate future feature.
- Camera-relative clipmaps for large-world coverage.
- Authored high-density local volumes for rooms and hero spaces.
- A bounded, measured probe-update pipeline that does not rely on full-frame SSGI resources.

This plan is based on the current codebase and on the DDGI/RTXGI production model: update probe volumes, classify/relocate where needed, then query irradiance during shading. DDGI is a low-frequency diffuse technique, so it should be paired with AO and separate specular systems rather than asked to replace every indirect-lighting feature.

References:

- NVIDIA RTXGI DDGI integration: https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Integration.md
- NVIDIA RTXGI DDGI algorithms: https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Algorithms.md
- Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields: https://jcgt.org/published/0008/02/01/

## Production Definition

DDGI is production-ready when all of the following are true:

- A `DdgiHigh` profile runs without SSGI passes, SSGI attachments, SSGI histories, or SSGI trace-source outputs.
- DDGI update cost is controlled by ray budget, not just probe count.
- Ray counts, max ray distances, and update rates are effective per volume/cascade.
- Opaque, foliage, and basic VFX receive plausible DDGI or probe-sampled ambient light.
- The acceleration-structure path is incremental, does not rebuild/reupload everything every static frame, and has a policy for alpha-masked, foliage, skinned, and dynamic geometry.
- Probe cache updates are bounded and do not copy the full irradiance/visibility atlas every frame.
- Large-world volume streaming does not clear unrelated probe caches or starve camera clipmaps.
- Diagnostics report actual allocated memory and GPU timings for DDGI, AS, AO, reflections, and any remaining GI resources.
- The renderer has visual and performance validation scenes with repeatable thresholds.

## Current Codebase Assessment

The existing renderer has a strong DDGI foundation:

- Camera-relative clipmap controller with scroll, teleport, fast movement, and stable physical indexing.
- Authored volumes plus camera-relative volumes.
- Probe classification, relocation, dirty bounds, update scheduling, recursive cached diffuse, debug overlays, and tests.
- DDGI sampling in the opaque forward path.
- SSGI pass execution already gated by `EffectiveUseSsgi`.

The current blockers are structural:

- High/Ultra/sample profiles still use hybrid modes with both SSGI and DDGI.
- `RenderTargetManager` creates SSGI render targets whenever global illumination is enabled, not only when SSGI is effective.
- The render-graph declaration and forward opaque path still declare/use SSGI trace-source outputs.
- `ddgi_update.comp` uses a single pushed `RaysPerProbe`; per-volume ray settings are written but not used for the probe ray loop.
- Effective max ray distance is forced to at least the volume diagonal.
- Recursive DDGI snapshots copy probe state plus full irradiance and visibility atlases before update.
- The scheduler has no previous GPU timing on cold start and can return the hard maximum update count.
- Hit lighting can multiply cost by tracing shadow rays for many lights per primary DDGI hit.
- TLAS is rebuilt and instance metadata is uploaded every enabled frame. The current AS includes only opaque non-skinned non-decal meshes.
- Authored volumes consume probe budget before camera clipmaps, which can starve clipmap coverage.
- Foliage currently does not receive DDGI.

## Non-Goals

- Do not delete SSGI in the first pass. Keep it as a backend until DDGI-only validation is complete.
- Do not require SSR to ship DDGI-only. Reflection probes/IBL are enough for this plan.
- Do not make async compute a default requirement until DDGI timing and hazards are measured on the synchronous path.
- Do not solve full path tracing or fully dynamic emissive lighting as part of this milestone.

## Milestone 0: Baseline and Instrumentation

Goal: make performance and memory evidence reliable before changing defaults.

Tasks:

- Add or verify GPU timestamp scopes for:
  - AS BLAS work.
  - AS TLAS work.
  - DDGI probe update.
  - DDGI cache copy/commit.
  - Opaque forward DDGI sampling.
  - AO.
  - Reflection probe/IBL pass.
  - SSGI passes when enabled.
- Add per-frame DDGI counters:
  - Active volumes.
  - Active probes.
  - Updated probes by reason: new cell, dirty bound, visible, safety, age.
  - Primary probe rays.
  - Secondary/shadow rays from hit lighting.
  - Rays per cascade/volume.
  - Irradiance and visibility atlas bytes.
  - Recursive/staging cache bytes.
- Add render-target diagnostics:
  - SSGI target bytes.
  - Scene-surface target bytes.
  - DDGI cache bytes.
  - Reflection/AO bytes.
- Create repeatable benchmark scenes:
  - Open plaza.
  - Closed room with doorway.
  - Thin-wall leak test.
  - Foliage-heavy area.
  - Moving light/object test.
  - Camera teleport/fast traversal.
  - Local authored volume streaming test.

Acceptance:

- Benchmarks produce valid GPU timestamps after warmup.
- Results include p50, p95, max, and first-frame/cold-start timing.
- A DDGI run and a hybrid run can be compared without hidden zero/missing GPU timing.

## Milestone 1: Add a Real `DdgiHigh` Profile Without Making It Default

Goal: create a profile that expresses the target renderer while the old hybrid path remains available.

Recommended initial settings:

- `GlobalIllumination.Mode = Ddgi`.
- `UseDdgi = true`.
- `UseRayQueryBackend = true`.
- `UseSsgi = false`.
- Probe relocation enabled.
- Probe classification enabled.
- Camera-relative DDGI enabled.
- Async DDGI disabled by default until measured.
- AO enabled.
- Reflection probes/IBL enabled.

Initial conservative DDGI budget:

- Budget updates by primary rays, not just probes.
- Cold start: bounded burst budget, not hard max probe count.
- Steady frame: small fixed ray budget with adaptive scaling.
- High quality mode may increase rays/update rate only after GPU timings are valid.

Acceptance:

- `DdgiHigh` can be selected from settings/presets.
- It does not change the existing hybrid preset behavior.
- Diagnostics clearly show DDGI mode, SSGI disabled, and ray-query backend state.

## Milestone 2: Remove SSGI Runtime Footprint From DDGI Mode

Goal: DDGI mode should not allocate, transition, render to, or declare SSGI resources.

Tasks:

- Change render-target creation to use `EffectiveUseSsgi`, not `GlobalIllumination.Enabled`.
- Gate or remove these resources when SSGI is disabled:
  - `SsgiTraceSource`.
  - `SsgiRaw`.
  - `SsgiHitDistance`.
  - `SsgiFiltered`.
  - SSGI color/depth/normal histories.
  - SSGI moments and history length.
  - `GiFinalDiffuse` if only used by SSGI composite.
- Gate `SceneSurfacePass` and `SceneNormal`/`SceneMaterial` allocation behind actual consumers.
- Make the production render graph conditional, or split it into DDGI-only and SSGI-capable declarations.
- Add one-output opaque forward pipelines for DDGI-only mode.
- Add one-output foliage pipelines for DDGI-only mode.
- Build shader variants without `FORWARD_SSGI_TRACE_SOURCE_OUTPUT` when SSGI is disabled.
- Ensure pass execution, resource declarations, attachment counts, and pipeline layouts agree.

Acceptance:

- In `DdgiHigh`, diagnostics show 0 bytes for SSGI render targets.
- In `DdgiHigh`, the opaque forward pass uses one color attachment unless another active feature requires more.
- In `DdgiHigh`, the render graph contains no SSGI passes or SSGI-only resources.
- Hybrid/SSGI modes still work.

Tests:

- Unit test `RenderTargetManager` allocations for DDGI-only, SSGI-only, hybrid, and disabled GI.
- Pipeline declaration test proving DDGI-only graph has no SSGI resource names.
- Shader build test proving DDGI-only forward variants compile without SSGI trace output.

## Milestone 3: Fix DDGI Ray Budgets and Hit Shading Cost

Goal: make DDGI update cost predictable and tunable.

Tasks:

- Use per-volume/per-cascade ray counts in `ddgi_update.comp`.
- Include rays per probe in the update queue or resolve it from the selected volume metadata.
- Budget scheduling in ray units:
  - `sum(updatedProbes * raysPerProbe)`.
  - Track estimated shadow rays separately.
- Add cold-start caps so first active frame cannot use the hard maximum update count.
- Stop forcing max ray distance to the volume diagonal.
- Add explicit max ray distance per cascade and authored volume.
- Add debug output for effective ray distance by volume.
- Bound direct lighting at probe hits:
  - Limit shadowed lights per hit.
  - Prefer clustered/top-N relevant lights.
  - Use cheaper unshadowed or cached terms for the rest.
  - Add a profile cap for `DDGI_MAX_SHADED_LIGHTS`.
- Add material sampling controls for DDGI hit shading:
  - Use coarse mips or compact material metadata where possible.
  - Avoid expensive texture paths for distant cascades.

Acceptance:

- Far cascades use fewer rays than near cascades in measured counters.
- Reducing far-cascade ray count reduces GPU time.
- Reducing max ray distance reduces traversal cost.
- Scenes with many lights have bounded DDGI cost.
- First-frame update cost is bounded by the configured cold-start budget.

Tests:

- Shader or SPIR-V test proving per-volume ray count is consumed.
- Scheduler test for ray-budget enforcement.
- Scheduler test for cold-start cap.
- Test proving far cascade ray reductions affect reported ray count.

## Milestone 4: Stable Probe Cache and Incremental Update Commit

Goal: shade from a stable cache while updating a bounded set of probes without full atlas copies.

Tasks:

- Define two cache roles:
  - Stable frame-N cache sampled by shading.
  - Update/staging writes for probes scheduled this frame.
- Remove full-frame copies of probe state, irradiance atlas, and visibility atlas from the steady path.
- Copy/commit only changed probe tiles or use a ping-pong/tile-indirection scheme.
- Keep recursive GI sampling pointed at the stable cache for the duration of a frame.
- Define explicit barriers and ownership for:
  - Stable cache reads.
  - Update writes.
  - Tile commit.
  - Next-frame cache visibility.
- Make cache memory diagnostics report current, staging, and optional recursive memory separately.

Acceptance:

- DDGI update cost scales with updated probe count, not full atlas size.
- A frame with 0 updated probes performs no full atlas copy.
- Opaque shading samples stable data while updates occur later in the frame.
- Visual output remains stable during movement and probe updates.

Tests:

- Unit test for cache memory accounting.
- GPU diagnostic check for no full copy when update count is 0.
- Visual test for flicker during camera movement and local dirty updates.

## Milestone 5: Production Acceleration Structures

Goal: make the ray-query scene representative, incremental, and safe for streaming.

Tasks:

- Separate static, dynamic, skinned, foliage, and alpha-masked geometry policy.
- Avoid full TLAS rebuild/reupload when a static scene has not changed.
- Use update/refit paths where supported and beneficial.
- Track dirty instance transforms/material flags.
- Add dynamic geometry proxies for:
  - Important moving opaque objects.
  - Skinned characters, at least as simplified proxy geometry or bounds.
  - Foliage cards/clusters where they materially affect occlusion.
- Add alpha-mask policy:
  - Either approximate as opaque for coarse GI, use alpha-tested any-hit where affordable, or provide simplified opacity geometry.
  - Make the chosen policy explicit per material/domain.
- Remove `WaitIdle` from steady streaming/growth paths. Use deferred destruction/retirement for resized buffers.
- Add AS memory and build timing diagnostics.

Acceptance:

- Static camera/static scene does not rebuild the TLAS every frame.
- Static-frame AS cost is near zero except synchronization overhead.
- Dynamic object movement updates only affected AS data.
- Foliage/alpha-heavy scenes have a documented and tested DDGI visibility policy.
- No steady-state buffer growth path blocks on device idle.

Tests:

- Static scene AS test: no TLAS rebuild after warmup.
- Transform update test: dynamic instance updates without full static rebuild.
- Alpha/foliage visual test: DDGI occlusion is plausible and stable.
- Streaming test: AS buffer growth does not stall with `WaitIdle` in steady operation.

## Milestone 6: Volume Allocation, Streaming, and Room Quality

Goal: guarantee global clipmap coverage while allowing high-density local volumes.

Tasks:

- Reserve camera clipmap probe budget before authored/local volumes.
- Allocate authored volumes from a separate priority pool or leftover budget.
- Add local-volume admission policy:
  - Priority.
  - Camera proximity.
  - Screen relevance.
  - Indoor/room importance.
  - Probe budget cost.
- Avoid clearing unrelated probe caches when authored volumes stream in/out.
- Preserve cache data for volumes whose identity, dimensions, and probe layout remain compatible.
- Add explicit volume IDs/generations for cache reuse.
- Validate overlapping-volume selection in shader/CPU metadata.
- Prefer higher-density local volumes for rooms while keeping clipmaps present.

Acceptance:

- Camera clipmaps cannot be starved by authored volumes.
- Streaming a local volume does not clear all clipmap probes.
- Room volumes visibly improve indoor bounce/detail.
- Overlapping volumes blend/select predictably.

Tests:

- Layout test proving clipmap budget reservation happens before authored volume admission.
- Streaming test proving unrelated probe cache data survives local volume changes.
- Visual room test with and without local high-density volume.
- Overlap test for local volume priority and blend behavior.

## Milestone 7: DDGI Shading Coverage

Goal: every important visible domain has a DDGI-only lighting answer.

Tasks:

- Opaque:
  - Keep direct DDGI sampling in forward shading.
  - Verify geometric-normal use and bias behavior.
  - Add debug views for selected volume/probe contribution.
- Foliage:
  - Add DDGI receive path to foliage shaders.
  - Use stable one-output variants when SSGI is off.
  - Decide whether foliage contributes to AS as opaque approximation, alpha policy, or proxy geometry.
- VFX/particles:
  - Add cheap probe-sampled ambient for non-emissive particles.
  - Keep purely emissive particles separate.
  - Add optional emissive proxy injection later if needed.
- Materials:
  - Ensure emissive surface handling is bounded and does not explode probe-update cost.
  - Add material flags for expensive DDGI hit shading paths.
- AO:
  - Keep AO mandatory in DDGI-only mode.
  - If current AO is not GTAO, call it screen-space/contact AO until GTAO is actually implemented.
- Specular:
  - Keep reflection probes/IBL separate from DDGI diffuse.
  - Do not block DDGI-only on SSR.

Acceptance:

- Opaque, foliage, and basic VFX do not rely on SSGI to look integrated.
- DDGI-only mode has no black/flat foliage in indirect lighting.
- AO provides stable contact grounding without SSGI.
- Reflection behavior is no worse than the existing reflection-probe/IBL path.

Tests:

- Foliage indirect-light visual test.
- Particle ambient visual test.
- Indoor contact AO test.
- Emissive surface stress test.

## Milestone 8: Adaptive Quality and Performance Profiles

Goal: keep high quality while avoiding unstable frame time.

Tasks:

- Implement DDGI quality tiers:
  - `DdgiLow`: fewer volumes/rays, short distances, low update rate.
  - `DdgiMedium`: balanced defaults.
  - `DdgiHigh`: target production quality.
  - `DdgiUltra`: validation/headroom only.
- Add adaptive budget controls:
  - GPU-time target.
  - Ray-budget target.
  - Cold-start burst cap.
  - Minimum refresh rate for active volumes.
  - Hysteresis to avoid oscillation.
- Prioritize updates:
  - New cells.
  - Dirty local changes.
  - Camera-visible probes.
  - Safety refresh.
  - Age refresh.
- Add emergency degrade path:
  - Reduce far-cascade rays.
  - Reduce far-cascade update rate.
  - Skip expensive hit-lighting shadows.
  - Preserve near/room quality first.

Initial target budgets to tune after measurement:

- DDGI update p95: 1.5 ms to 2.5 ms on target high-quality hardware.
- AS steady static p95: near zero after warmup.
- AS dynamic p95: budgeted and scene dependent.
- Total GI/AO/reflection overhead p95: defined per quality tier.
- SSGI bytes in `DdgiHigh`: 0.

Acceptance:

- Quality tiers produce monotonic cost changes.
- Adaptive budget reduces DDGI work under load without visual popping.
- High-quality profile meets measured p95 targets on target hardware.
- Fallback/degrade decisions are visible in diagnostics.

Tests:

- Performance test for each tier.
- Stress test with many lights.
- Stress test with camera fast movement.
- Stress test with authored volume streaming.

## Milestone 9: Validation and Production Gate

Goal: prove DDGI-only is ready before changing defaults.

Required validation scenes:

- Outdoor plaza with mixed sun/sky/local lights.
- Indoor room with doorway and colored bounce.
- Thin-wall leakage scene.
- Long corridor/occlusion scene.
- Foliage-heavy scene.
- Moving character/object scene.
- Moving light scene.
- Emissive material scene.
- Local volume streaming scene.
- Fast traversal/teleport scene.

Gate criteria:

- No SSGI resources allocated in `DdgiHigh`.
- No SSGI passes declared or executed in `DdgiHigh`.
- No full DDGI atlas copy in steady frames without updates.
- No full TLAS rebuild in unchanged static scenes.
- Foliage and basic VFX receive plausible DDGI/probe ambient.
- Clipmaps remain allocated even when authored volumes are present.
- GPU timings are valid and within tier budgets.
- Memory diagnostics match actual allocated resources.
- Visual debug views expose volume selection, probe validity, update reasons, and ray budgets.
- SSGI and hybrid modes still work as non-default compatibility backends.

Only after this gate should `DdgiHigh` become the default production GI profile.

## Milestone 10: Optional SSGI Reintroduction

Goal: reintroduce SSGI only if it adds visible value that DDGI+AO cannot cover.

Rules:

- SSGI must be opt-in.
- SSGI resources must be allocated only when enabled.
- SSGI must not be part of the default DDGI profile.
- SSGI should act as a near-field/detail layer, not the primary GI source.
- Any SSGI blend must be measured against:
  - DDGI-only.
  - DDGI + AO.
  - DDGI + improved local volumes.

Acceptance:

- Enabling SSGI changes memory/pass diagnostics explicitly.
- Disabling SSGI returns memory and pass count to DDGI-only baseline.
- The feature has a documented visual benefit per millisecond.

## Suggested PR Order

1. Add diagnostics and benchmark scenes.
2. Add `DdgiHigh` profile, disabled-by-default.
3. Gate SSGI render targets and histories by `EffectiveUseSsgi`.
4. Split/gate render graph resources and forward secondary attachment usage.
5. Add DDGI-only forward and foliage shader/pipeline variants.
6. Fix per-volume ray counts and max ray distance.
7. Convert scheduler to ray-budgeted updates with cold-start caps.
8. Bound DDGI hit-lighting cost.
9. Replace full recursive/atlas snapshots with stable-cache incremental update.
10. Productionize AS dirty tracking and static-frame behavior.
11. Add dynamic/foliage/alpha AS policy.
12. Reserve clipmap probe budget and improve local-volume streaming/cache reuse.
13. Add foliage and VFX DDGI receive.
14. Add adaptive DDGI quality tiers.
15. Run production validation gate.
16. Make `DdgiHigh` the default.
17. Decide whether optional SSGI detail is still worth reintroducing.

## Main Risks

- DDGI without AO will look too soft and ungrounded.
- Probe hit lighting can dominate cost in many-light scenes if not capped.
- Alpha/foliage visibility can cause obvious GI mismatch if ignored.
- Large local volumes can consume probe budget unless clipmaps are reserved first.
- Cache invalidation can cause visible flicker or warmup artifacts if streaming clears too much.
- Async compute can hide or create hazards; defer it until the synchronous pipeline is correct and measured.

## Final Recommendation

Proceed with DDGI-only as the production target, but do not flip the default until the first nine milestones pass. The current plan is directionally right; the revised plan makes the first milestone stricter: prove the renderer is actually DDGI-only in memory, pass graph, shader variants, ray budgets, and acceleration-structure behavior before using performance numbers to make quality decisions.
