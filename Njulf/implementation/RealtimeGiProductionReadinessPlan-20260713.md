# Realtime GI Production Readiness Plan

Date: 2026-07-13  
Status: Proposed  
Scope: Simple DDGI, forward GI gathering, probe scheduling and scrolling, ray-query acceleration structures, far-field GI, diagnostics, quality tiers, tests, and production rollout

## Goal

Turn the current Realtime GI implementation into a production-ready system that:

1. Produces stable, plausible indirect lighting at close range, including interiors, thin geometry, moving lights, emissive objects, and dynamic geometry.
2. Maintains bounded CPU, GPU, and memory cost in large streamed worlds.
3. Degrades predictably across hardware tiers without hidden work or unbounded update spikes.
4. Recovers cleanly from camera movement, teleports, chunk streaming, missing far-field data, and unavailable ray-query resources.
5. Has automated correctness, visual-quality, performance, memory, and long-running stability gates.

The implementation must be incremental. Every substantial algorithm or storage change must be independently feature-gated, measurable against the existing path, and removable after production sign-off.

## Current measured baseline

The 2026-07-13 captures show the following at 1080p using the Development profile:

| Metric | 15:02:57 | 15:03:26 |
|---|---:|---:|
| GPU frame | 46.194 ms | 46.733 ms |
| Opaque forward pass | 36.446 ms | 36.163 ms |
| Reported DDGI update | 4.060 ms | 4.832 ms |
| Trace | 1.990 ms | 2.196 ms |
| Blend | 1.799 ms | 2.357 ms |
| Relocate/classify | 0.271 ms | 0.279 ms |
| Total probes | 20,736 | 20,736 |
| Probes updated | 4,096 | 4,096 |
| Full-ray / maintenance probes | 4,096 / 0 | 4,096 / 0 |
| Primary rays | 524,288 | 524,288 |
| Emissive revision | 2,501 | 3,118 |

Additional observations:

- The emissive revision advanced 617 times in roughly 29 seconds, effectively once per rendered frame.
- The Development profile allows 8,192 probes and a 2.5 ms total GI GPU budget, but the active configuration uses 20,736 probes and reports 4.0-4.8 ms for updates alone.
- Simple DDGI storage is approximately 112.7 MB and acceleration structures approximately 411.3 MB, for about 524 MB of tracked GI-related memory against a 96 MB profile budget.
- The opaque forward pass contains DDGI gather work that is not included in the reported GI GPU metric.
- Approximately 5.26 million rough-specular DDGI samples are performed per captured frame.
- 6,405 probes are relocated, average relocation is 47.6% of the allowed distance, and roughly 9% of probes are inactive.
- The captures report zero-irradiance and zero-final-indirect samples and set `DdgiBlackFrameSuspect=1`.
- Far-field tracing is disabled, so the outer 16 m ring uses long TLAS ray queries.
- Async compute is supported but not requested because the top-level async setting is disabled.

## Confirmed blockers

1. Emissive source revision participates in its own content signature, forcing a new revision and global dirty boost every frame.
2. The reported GI GPU budget excludes DDGI work embedded in forward shading.
3. The forward path performs an unconditional diagnostic sample and a second full gather for rough specular.
4. Probe blending is `O(updated probes × atlas texels × rays)`.
5. Unsupported samples can become black, while the visibility epsilon can normalize back into full irradiance and cause leaks.
6. Environment radiance is stored in DDGI on trace misses and then unconditionally added again as diffuse IBL.
7. Dynamic changes use global signatures instead of dirty regions, forcing world-wide updates.
8. Stability counts advance for probes that were not updated, and stability is derived only from irradiance texel zero.
9. Ring scrolling copies irradiance and visibility but does not coherently remap all CPU and GPU probe state.
10. The existing far field is one scene-sized cube, omits world transforms from its signature, and can let a coarse far hit mask closer TLAS geometry.
11. The active Simple DDGI settings are not fully controlled by `ApplyDdgiQualityTier`.

## Non-negotiable production invariants

1. Identical scene inputs do not change GI revisions or dirty state.
2. Every sampled probe slot has a matching logical position, physical storage index, generation, relocation state, classification state, and temporal history.
3. Invalid, freshly exposed, stale-generation, or unavailable probe data never contributes to shading.
4. Missing DDGI support falls back smoothly to environment lighting rather than producing black.
5. Visibility suppression is not cancelled by weight normalization.
6. Environment energy has one documented owner and is not counted twice.
7. Dynamic updates are spatially bounded unless the underlying change is genuinely global.
8. GI work obeys a hard ray, probe, GPU-time, CPU-time, and memory budget.
9. Total GI timing includes update, gather, composition, far-field, and GI-specific acceleration-structure work.
10. Camera scrolling and teleporting cannot expose stale lighting.
11. Near real geometry always wins over a coarser far-field representation.
12. Unsupported hardware, unavailable TLAS data, missing streamed pages, or failed async planning fall back before unsafe work is recorded.
13. Debug and diagnostic features have negligible cost when disabled.
14. Every quality tier has explicit resource and update limits.

## Target architecture

Use three bounded GI layers:

| Layer | Responsibility | Proposed representation |
|---|---|---|
| Near | Contact quality, interiors, important authored areas, dynamic objects | Dense camera-relative and authored DDGI volumes plus a near detailed TLAS |
| Mid | Surrounding buildings and terrain | Coarser camera-relative DDGI rings with reduced ray and atlas budgets |
| Far | Large static world and streamed distant chunks | Fixed-physical-resolution paged radiance/SDF cascades with bounded residency |

Dynamic geometry remains authoritative in the near/mid ray-query representation. Static far-field pages are streamed or rebuilt incrementally. Missing far-field data falls back to the environment and never invalidates near-field correctness.

## Initial acceptance budgets

The reference GPU and driver must be named in capture metadata before these gates become release-blocking. The initial High target is the current Development profile goal at 1080p60.

### Performance and memory

| Metric | High target |
|---|---:|
| Total GI GPU, including forward gather | <= 2.5 ms P95 |
| Steady-state probe update | <= 1.0 ms P95 |
| Dynamic-dirty probe update | <= 1.75 ms P95 |
| Forward diffuse GI gather | <= 0.75 ms P95 |
| GI CPU scheduling and upload | <= 0.25 ms P95 |
| Total GI P99 spike | <= 3.5 ms |
| Simple DDGI cache | <= 192 MiB High, <= 96 MiB Medium |
| Resident GI acceleration structures | <= 256 MiB High |
| Resident memory growth during world travel | Bounded by the selected tier |

Acceleration-structure and DDGI-cache budgets must be reported separately and together. A tier may not silently exceed either budget.

### Quality and responsiveness

| Metric | Initial gate |
|---|---:|
| Static temporal luminance variation | < 3% P95 |
| Visible near-field dirty response | Begins within 1 frame |
| Near-field dirty convergence | <= 8 frames P95 |
| Global environment transition | <= 30 frames without flashing |
| Camera scroll | No stale samples or visible copy spike |
| Teleport | No stale samples; environment fallback until valid probes arrive |
| Unsupported sample | Never black when a valid environment fallback exists |

Image thresholds must be calibrated in Phase 0 and then frozen. Proposed starting gates are HDR relative RMSE <= 12% and FLIP P95 <= 0.08 against high-sample diffuse-indirect references in controlled scenes, plus scene-specific leak and contact tests.

## Phase 0 - Lock baselines and complete accounting

### Implementation

1. Add GPU timestamp scopes for:
   - Simple DDGI trace;
   - relocation/classification;
   - blend;
   - forward diffuse gather;
   - rough-specular gather;
   - environment fallback/composition;
   - far-field trace and update;
   - GI-related TLAS/BLAS build, update, and compaction.
2. Update `RenderBudgetEvaluator` so total GI GPU time includes forward-shader work.
3. Report DDGI cache, far-field cache, scratch, BLAS, and TLAS memory independently and as a combined total.
4. Add a capture schema version and record GPU, driver, resolution, quality tier, scene revision, debug state, and feature flags.
5. Ensure detailed diagnostic counters are compiled out or sparsely sampled when disabled.
6. Create reproducible baseline captures and high-sample reference images for the validation scenes defined later in this plan.

### Gate

- Repeated captures vary by less than 5% after warmup.
- Every GI cost is assigned to exactly one reported subsystem.
- Enabling normal production diagnostics changes GPU frame time by less than 0.1 ms.
- Reference images, settings, performance snapshots, and scene revisions are stored together.

## Phase 1 - Correct deterministic invalidation and stale-pass execution

### Implementation

1. Remove revision metadata from the emissive content signature.
2. Build the complete stable content signature first.
3. Increment `_ddgiEmissiveSourceRevision` once only when that signature changes.
4. Write the resulting revision to GPU upload data after comparison.
5. Replace ambiguous packed signature fields with explicitly named content and metadata fields where practical.
6. Gate trace, relocate/classify, and blend as one coherent update transaction. If trace cannot execute, no consumer may process stale ray scratch.
7. Advance stable-update counts only for probes present in the completed update queue.
8. Reduce luminance change across all irradiance texels or through a proper per-probe reduction instead of texel zero.
9. Reset stability when a probe becomes fresh, dirty, invalid, relocated materially, or reactivated.
10. Retry inactive probes immediately when affected by dynamic dirty bounds.

### Tests

- Identical emissive inputs keep one revision for 10,000 frames.
- Emissive intensity, material, transform, bounds, creation, and removal each increment exactly once.
- Static scenes allow dirty frames to expire and reach maintenance-ray updates.
- Trace suppression prevents all downstream update passes.
- Only updated probes accumulate stability.
- Stability reduction detects a change isolated to any atlas direction.

### Gate

- No permanent dirty boost in a static scene.
- Full-ray probe updates drop to the configured steady-state amount.
- No stale scratch or stale update queue consumption is possible.
- New tests pass under Debug and Release configurations.

## Phase 2 - Correct sampling, visibility, and environment composition

### Structured gather result

Replace the current irradiance-only return with a result containing:

```text
DdgiGatherResult
  irradiance
  validSupport
  spatialCoverage
  transportVisibility
  confidence
  selectedVolume
  selectedSpacing
  validProbeCount
```

### Implementation

1. Exclude inactive, invalid, stale-generation, and uninitialized probes from valid support.
2. Keep numerical epsilon separate from physical transport weight.
3. Do not allow an epsilon visibility floor to normalize back into full irradiance.
4. If valid support is zero, return explicit unsupported state rather than a plausible-looking zero irradiance value.
5. Blend environment fallback according to missing support, not as an unconditional second source.
6. Document whether probe radiance includes sky/environment misses. Apply that ownership consistently in trace and forward composition.
7. Apply albedo, metallic response, `1 / PI`, AO, and global intensity exactly once.
8. Remove hardcoded coverage, support, and weight values from the Simple DDGI forward path.
9. Make raw irradiance, raw diffuse, final diffuse, final indirect, support, visibility, and fallback debug modes use documented units.
10. Replace fixed world-space normal/view/visibility biases with spacing-relative values clamped to safe world-space minima and maxima.
11. Add smooth cross-volume/cascade transition rules based on coverage and spacing.

### Rough specular policy

Disable the current second diffuse-irradiance gather as a production specular approximation. Use the existing reflection hierarchy, SSR, reflection probes, and environment prefiltering. Reintroduce DDGI-assisted rough specular only after the probe representation stores a validated directional radiance lobe suitable for specular response.

### Tests and visual oracles

- Unsupported gather with a valid environment returns stable fallback illumination.
- Fully occluded probe visibility cannot recover through normalization.
- Open-environment energy is not counted twice.
- Diffuse albedo and `1 / PI` are applied once.
- Thin-wall, corner, under-table, and adjacent-room scenes remain within locked leak thresholds.
- Volume transitions have no discontinuity above the locked image threshold.

### Gate

- `DdgiBlackFrameSuspect` remains zero in valid production test scenes.
- No unsupported pixel becomes black solely because probes are temporarily invalid.
- Thin-wall leakage is reduced without eliminating valid bounced light.
- Raw and final debug views match CPU reference calculations.

## Phase 3 - Optimize forward gather and probe blending

### Low-risk forward optimizations

1. Execute `SampleSimpleDdgiDebug` only for an active debug view or sparse investigation pixel.
2. Remove the rough-specular second gather.
3. Cache volume selection and reusable probe indices within a fragment invocation.
4. Avoid rereading probe state for diffuse, diagnostics, and debug paths.
5. Evaluate tile-level or low-resolution gather caches for workloads with high overdraw, but retain material-correct final composition.

### Sampled atlas migration

Migrate packed manually filtered SSBO atlases to sampled GPU images behind a feature flag:

- Irradiance candidates: `R11G11B10_UFLOAT` and `RGBA16F`.
- Visibility candidate: `RG16F`, initially benchmarked at both 8x8 and 16x16 per probe.
- Use texture-array layers or correctly padded atlases so hardware bilinear filtering cannot cross probe boundaries.
- Validate octahedral seam handling and border replication.
- Preserve the existing storage path until image and performance comparison gates pass.

### Blend complexity reduction

Replace `rays × texels` evaluation with one or more prototypes:

1. Ray splatting into octahedral bins followed by seam-aware spatial filtering.
2. Parallel SH projection for diffuse irradiance, optionally reconstructing an atlas only where required.
3. Compact spherical-Gaussian lobes if they provide a measurable quality advantage for comparable cost.

The selected representation must approach `O(rays + texels)` per probe, use subgroup/shared-memory reductions where profitable, and preserve a directional visibility representation adequate for leak control.

### Per-ring work budgets

Use independent near, mid, and far settings for:

- full rays;
- maintenance rays;
- irradiance resolution/representation;
- visibility resolution;
- maximum ray distance;
- material-texture LOD;
- maximum shaded lights;
- update quota.

Trace shaders must receive the actual ring/cascade index. Far rings may not sample all material textures at LOD 0.

### Gate

- Forward diffuse gather <= 0.75 ms P95 at 1080p on the reference GPU.
- Steady probe update <= 1.0 ms P95.
- Total GI <= 2.5 ms P95.
- Blend work no longer scales as atlas texels multiplied by every ray.
- Locked quality metrics do not regress beyond their tolerance.

## Phase 4 - Region-based invalidation and budget-aware scheduling

### Dirty event model

Introduce a `GiDirtyRegion` carrying:

```text
old world bounds
new world bounds
influence bounds
reason flags
priority
source revision
source identifier
```

Generate events for:

- light position, direction, range, color, or intensity changes;
- emissive material, intensity, bounds, or transform changes;
- dynamic geometry movement using the union of old and new bounds;
- geometry creation and removal;
- streamed chunk load/unload;
- authored-volume changes;
- global environment changes.

Eliminate per-frame hashing of every render object as the normal dynamic-change mechanism. Retain optional signature validation in development builds to detect missed events.

### Probe mapping and queue construction

1. Map dirty AABBs to integer probe ranges for each intersecting volume.
2. Store dirty state in bounded CPU or GPU bitsets.
3. Deduplicate overlapping events before scheduling.
4. Reset stability and reactivate inactive probes inside dirty regions.
5. Assign update priority in this order:
   - fresh or dirty visible near probes;
   - dirty near probes outside the frustum;
   - aged visible near probes;
   - mid-ring dirty and maintenance probes;
   - far-ring dirty and maintenance probes;
   - inactive retries.
6. Give each reason and ring a minimum and maximum quota so continuous motion cannot starve maintenance.
7. Use measured GPU time to adjust the next frame's probe and ray budgets with hysteresis, bounded rates of change, and a deterministic fixed-budget validation mode.
8. Record dirty-to-first-update and dirty-to-converged latency histograms.

### Gate

- A moving object updates only intersecting and influence-affected probe regions.
- Continuous motion remains inside the dirty-update budget.
- Visible near-field changes begin within one frame and converge within eight frames P95.
- No ring, dirty reason, or inactive-retry class can starve indefinitely.
- Development signature validation detects no missing dirty events during the validation suite.

## Phase 5 - Coherent toroidal scrolling and teleport recovery

Replace atlas-copy scrolling with shared toroidal addressing.

Each physical probe slot must contain or index coherently:

- irradiance;
- visibility moments;
- relocation;
- classification and active weight;
- valid/generation state;
- temporal EMA and stability count;
- last-update serial and age.

### Incremental scroll

1. Maintain a logical world origin and physical circular offset per ring.
2. Advance the offset when the logical origin moves by whole cells.
3. Preserve overlapping physical slots without copying.
4. Increment generation and invalidate only newly exposed slices.
5. Do not sample exposed slots until their first update completes for the new generation.
6. Prioritize exposed visible slices without starving existing maintenance.

### Teleport and major remap

1. Recenter every ring atomically in one frame.
2. Invalidate all affected generations before shading.
3. Bootstrap a bounded set of visible near probes first.
4. Use environment fallback until valid support becomes available.
5. Drop or ignore readbacks and updates from previous generations.

### Tests

- CPU oracle for logical-to-physical mapping across random positive and negative scrolls.
- Random camera walk over thousands of cell transitions.
- Multi-cell jumps and complete teleports.
- Simultaneous authored-volume add/remove and ring recenter.
- Late readback and late async completion from an older generation.

### Gate

- No atlas-copy GPU or CPU spike during normal scrolling.
- All preserved probes retain matching atlas and state data.
- Exposed and teleported slots never contribute stale values.
- Randomized mapping tests pass for every ring dimension and delta.

## Phase 6 - Streamed near acceleration structures and paged far field

### Near/mid acceleration structures

1. Partition static geometry into streamable world chunks with reusable, compacted BLAS objects.
2. Keep only relevant chunks and dynamic instances in the resident GI TLAS.
3. Separate detailed near geometry from simplified mid-distance proxies where beneficial.
4. Add explicit GI representations for foliage, masked geometry, skinned meshes, and important transparent occluders:
   - foliage proxy cards or clusters;
   - animated low-poly/capsule proxies for skinned objects;
   - validated opaque approximation or opacity-micromap path for masked geometry;
   - selected opaque proxy geometry for important translucent occluders.
5. Track build, refit, compaction, residency, and eviction cost separately.

### Paged far field

Replace the single scene-sized clipmap with fixed-physical-resolution, world-keyed pages and multiple cascades.

1. Define page coordinates in stable world space.
2. Assign fixed voxel size per cascade rather than deriving it from total scene extent.
3. Stream or precompute static pages by world chunk.
4. Rebuild only pages affected by static edits or chunk changes.
5. Include world transforms, mesh revisions, material revisions, and page settings in invalidation.
6. Maintain a hard residency and update budget with deterministic eviction.
7. Mark missing pages explicitly and fall back to environment lighting.
8. Keep continuous dynamic objects out of static far pages unless a low-frequency dynamic overlay is later proven necessary.

### Correct hybrid trace ordering

1. Trace the detailed near TLAS segment first.
2. Trace far pages only after the configured near segment, or compare actual hit distances if ranges overlap.
3. Never let a coarse far hit suppress a closer detailed hit.
4. Preserve emissive and bounce-energy conventions across near/far transitions.
5. Expose debug views for near hit, far hit, selected hit, missing page, step exhaustion, and cascade selection.

### Gate

- A 10 km validation world uses memory based on resident radius and quality tier, not total world size.
- World travel does not cause unbounded BLAS/TLAS growth.
- Chunk streaming and static edits update only relevant pages.
- Near geometry always wins over farther coarse geometry.
- Missing or stale far pages degrade to stable environment lighting.
- No GI-related streaming hitch exceeds the locked P99 budget.

## Phase 7 - Quality tiers, authored detail, and async integration

### Simple DDGI quality tiers

Extend `ApplyDdgiQualityTier` so every tier explicitly controls:

- ring count and dimensions;
- horizontal and vertical spacing;
- near/mid/far coverage;
- full and maintenance rays per ring;
- irradiance representation and resolution;
- visibility resolution;
- probe-update and ray budgets;
- dirty-response quotas;
- material-texture LOD and shaded-light limits;
- DDGI cache memory;
- resident acceleration-structure memory;
- far-field page resolution and residency;
- async-compute policy.

Do not use one fixed 20,736-probe layout across all hardware. Preserve close quality through dense near rings and authored local volumes, not by globally increasing far-ring density.

### Authored volume policy

1. Support dense volumes around important interiors, multi-floor spaces, hero areas, and complex geometry.
2. Permit anisotropic vertical spacing where architecture needs it.
3. Define deterministic priority and blending between authored volumes and camera-relative rings.
4. Include authored-volume memory and update cost in tier budgets.
5. Provide debug visualization for volume ownership, spacing, support, and blend weight.

### Async compute

Coordinate with `implementation/RemainingAsyncPathsImplementationPlan-20260713.md`.

1. Keep graphics-only execution as the correctness reference.
2. Enable async DDGI only when all concrete resources have a valid synchronization and ownership plan.
3. Measure actual frame-time improvement rather than relying on pass overlap alone.
4. Use `Auto` as the eventual production policy, with per-GPU hysteresis and persistent validation data.
5. Fall back before recording if the async plan is incomplete or unsupported.

### Gate

- Low, Medium, High, and Ultra tiers stay within their declared budgets.
- Tier changes do not retain incompatible history or stale resources.
- Authored volumes improve selected areas without discontinuities or unbounded memory.
- Async and graphics-only output remain within locked image tolerance.
- Async is enabled only where it improves P95/P99 frame time.

## Phase 8 - Production diagnostics, validation, and rollout

### Required production telemetry

- Total GI GPU and CPU time.
- Forward gather and rough-specular cost.
- Per-ring trace, blend, relocation, and classification time.
- Full, maintenance, fresh, dirty, inactive-retry, and skipped probe counts.
- Ray count and ray savings by ring and reason.
- Dirty event counts, affected probes, and update-latency histograms.
- Valid support, unsupported fallback, stale-generation rejection, and black-suspect counts.
- Scroll, teleport, invalidation, and bootstrap counts.
- DDGI, scratch, far-field, BLAS, and TLAS memory.
- Far-field page residency, misses, rebuilds, evictions, and trace-step buckets.
- Async requested, supported, enabled, overlap, and fallback reason.

Budget violations must generate actionable warnings. Metrics whose subsystem is inactive must report unavailable rather than a misleading zero or inherited legacy state.

### Validation suite

#### Unit and CPU-oracle tests

- Emissive and light revision idempotence.
- Dirty AABB-to-probe mapping.
- Scheduler priority, fairness, and hard-budget enforcement.
- Stability updates only for completed probes.
- Toroidal mapping and generation invalidation.
- Far-field page keys and signatures, including transforms and material revisions.
- Near/far hit selection and distance ordering.
- Tier serialization and validation.

#### Shader-oracle tests

- Irradiance and visibility decoding.
- Support and environment-fallback composition.
- Visibility under fully occluded and low-weight conditions.
- Octahedral filtering and seam behavior.
- Ray-splat/SH projection against a CPU reference.
- Bias scaling across all ring spacings.
- NaN, infinity, and zero-weight handling.

#### Visual scenes

1. Cornell box with movable wall and emissive panel.
2. Thin-wall adjacent rooms.
3. Sponza/courtyard with bright exterior and dark arcades.
4. Tall multi-floor interior.
5. Dense moving rigid objects.
6. Skinned characters and foliage.
7. Day/night and rapidly changing emissives.
8. Large streamed terrain and city chunks.
9. Camera random walk, high-speed travel, and teleport.
10. Missing and late-arriving far-field pages.

#### Performance and stability

- Fixed-hardware GPU performance CI.
- Cold start, warm steady state, continuous dynamics, and streaming profiles.
- Multi-hour camera-travel soak tests.
- Generation-wrap and frame-serial-wrap tests.
- Resource recreation, resize, device-loss recovery, and quality-tier switching.
- Vulkan validation with graphics-only and async paths.
- Memory leak and residency-bound verification.

### Rollout controls

Keep narrowly scoped feature flags for:

1. corrected invalidation;
2. structured gather/composition;
3. sampled-image atlas;
4. optimized blend representation;
5. regional scheduler;
6. toroidal scrolling;
7. paged far field;
8. streamed GI acceleration structures.

Each flag must support identical-scene A/B captures. Every flag requires an owner, removal condition, and diagnostics field. Do not retain indefinite dual implementations after the replacement passes all gates.

Production must also retain one emergency GI kill switch that falls back to environment/reflection lighting without requiring renderer restart.

## Recommended pull-request sequence

1. Complete timing, memory accounting, capture metadata, and baseline assets.
2. Fix emissive revision feedback and add revision regression tests.
3. Gate update passes coherently and correct stability bookkeeping.
4. Introduce structured gather support and correct environment composition.
5. Remove unconditional debug and rough-specular gathers.
6. Add sampled-image atlas path and image-equivalence tests.
7. Replace ray-by-texel blending with the selected reduced-complexity representation.
8. Add dirty-region events, mapping, scheduler quotas, and budget feedback.
9. Introduce toroidal indexing and generation-valid probe slots.
10. Stream and bound near/mid GI acceleration structures.
11. Implement paged fixed-resolution far-field cascades and correct hybrid ordering.
12. Define Simple DDGI quality tiers and authored-volume policies.
13. Validate async integration on top of the stable graphics-only path.
14. Run full visual, performance, memory, streaming, and soak gates.
15. Make the new path default, retain an emergency kill switch, and remove obsolete feature branches after sign-off.

Each pull request must be independently buildable, testable, capturable, and reversible. Storage-format migrations must not be combined with composition changes or scheduling changes in the same pull request.

## Primary file ownership

| Area | Primary files |
|---|---|
| Revisions, scene events, frame integration | `Njulf.Rendering/VulkanRenderer.cs` |
| Probe layout, scheduling, state, scrolling | `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs` |
| Pass execution and gating | `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs` |
| Trace, gather, blend, classification | `Njulf.Shaders/ddgi_simple_*.glsl`, `Njulf.Shaders/ddgi_simple_*.comp` |
| Forward composition and debug | `Njulf.Shaders/forward.frag` |
| Far-field storage and updates | `Njulf.Rendering/Resources/FarFieldClipmapManager.cs`, `Njulf.Rendering/Pipeline/FarFieldClipmapBakePass.cs` |
| Ray-query geometry and streaming | `Njulf.Rendering/Resources/AccelerationStructureManager.cs` |
| Tiers and runtime configuration | `Njulf.Rendering/Data/RenderSettings.cs` |
| Performance and memory gates | `Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs` and diagnostics data types |
| Regression and oracle coverage | `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`, `Njulf.Tests/FarFieldClipmapOracleTests.cs`, new focused test files |

## Final definition of done

Realtime GI is production-ready only when all of the following are true:

1. Static scenes settle into maintenance work with no changing revisions or permanent dirty flags.
2. Total measured GI cost, including forward gather and GI acceleration structures, remains inside the selected tier's P95 and P99 budgets.
3. Cache and resident acceleration-structure memory remain bounded during indefinite world travel.
4. Unsupported, stale, missing, or invalid probe data cannot produce black frames, leaks, or stale lighting.
5. Camera scrolling and teleports preserve or invalidate every probe resource coherently.
6. Dynamic lights, emissives, and geometry update spatially bounded probe regions with documented latency.
7. Close-range interiors meet contact, thin-wall, energy, stability, and reference-image gates.
8. Large streamed worlds maintain stable lighting without global rebuilds or world-sized acceleration structures.
9. Near geometry always overrides coarser far-field data.
10. Low, Medium, High, and Ultra tiers have tested and enforced quality, time, ray, probe, and memory budgets.
11. Graphics-only and supported async execution are validation-clean and visually equivalent within tolerance.
12. Automated unit, shader-oracle, visual-regression, performance, streaming, and soak suites pass.
13. Production diagnostics identify the cause of every fallback or budget violation.
14. Temporary feature flags and legacy paths have documented removal decisions, leaving only the emergency GI kill switch.

