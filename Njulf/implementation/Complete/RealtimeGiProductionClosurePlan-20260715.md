# Realtime GI Production Closure Plan

Date: 2026-07-15  
Status: Proposed, release-blocking  
Scope: Simple DDGI visual stability, Sponza volume policy, forward gathering, probe scheduling, memory and tier enforcement, async compute, diagnostics, validation, and rollout

## Purpose

This is the execution plan for closing the remaining realtime-GI production blockers exposed by the 2026-07-15 Sponza captures. It is intentionally narrower and more operational than `RealtimeGiProductionReadinessPlan-20260713.md`, while retaining that document's architecture and production invariants. `PerfPass2-0.md` remains useful historical context for optimizations already implemented or partially implemented.

This plan is complete only when the renderer produces stable world-space lighting independent of camera height, meets the selected tier's hard time and memory budgets, reports truthful diagnostics, and passes automated visual, performance, recovery, and long-run gates. A visually improved screenshot by itself is not completion.

## Inputs and baseline evidence

The two source snapshots are:

- `performance-20260715-002614-7774689-23c82a49822040d6b9b50d487f6c768d.json`: low-plaza view.
- `performance-20260715-002621-6988985-61ee424d16e74fd3bbc1ed512792dea1.json`: higher view of the same upper courtyard area.

The low/high association is confirmed by visible-object counts, the camera-relative near-ring origin, and the scene content in the images. Phase 0 must import renamed copies of the JSON and image artifacts into a repository baseline directory; temporary attachment filenames must not be used as durable test identifiers.

### Current measured state

| Metric | Low plaza | Higher view | Production implication |
|---|---:|---:|---|
| Visible objects | 450 | 92 | Confirms capture/view association |
| Near-ring Y origin | -2.125 m | 6.875 m | Ring moved upward by 9 m |
| Near-ring Y coverage | -2.125 to 8.875 m | 6.875 to 17.875 m | Upper façade changes DDGI resolution with camera height |
| Maximum authored-volume Y | 8.5 m | 8.5 m | No stable dense coverage for the upper façade |
| Total probes | 25,249 | 25,249 | 3.08 times the 8,192 Development-profile budget |
| Active probes | 19,112 | 18,226 | Large active working set |
| Probes updated per frame | 2,048 | 2,048 | Full active refresh cannot meet a one-frame response target |
| Second-volume gathers | 7,953,617 | 2,008,067 | Low view performs extensive coarse-volume fallback/blending |
| Forward diagnostic samples outside selected grid | 927 / 24,351 | 14 / 11,031 | Strong volume-edge/view-bias instability signal |
| Forward opaque / inclusive GI gather | 87.067 ms | 24.267 ms | Dominant GPU cost and highly view dependent |
| Simple DDGI trace | 8.080 ms | 7.517 ms | Exceeds the complete 2.5 ms GI budget alone |
| Simple DDGI blend | 1.094 ms | 1.068 ms | Additional update cost |
| GPU frame | 99.459 ms | 35.481 ms | Approximately 10 FPS and 28 FPS |
| CPU renderer | 24.876 ms | 25.593 ms | Exceeds the 6 ms CPU budget |
| GI CPU scheduling/upload P95 | 18.055 ms | 18.592 ms | Approximately 72-74 times the 0.25 ms budget |
| Dirty first-update P95 | 15 frames | 15 frames | Misses the one-frame visible-response target |
| Dirty convergence P95 | 15 frames | 15 frames | Misses the eight-frame target |
| Worst recorded convergence | 773 frames | 893 frames | Unbounded long tail |
| DDGI cache and buffers | 141.68 MB | 141.68 MB | Within the separate 192 MiB High cache cap, but not the active profile model |
| Resident GI acceleration structures | 406.66 MB | 406.66 MB | Within the scenario's 512 MiB override, above the intended 256 MiB High target |
| Combined reported GI memory | 552.98 MB | 552.98 MB | Compared incorrectly with a 96 MiB total profile budget |

The captures use an NVIDIA GeForce RTX 3060 Laptop GPU at 1600x900, DdgiHigh, Standard validation, detailed GI counters enabled, and GPU timing enabled. These are instrumented diagnostics rather than clean shipping benchmarks. Phase 0 must measure both instrumented and production configurations; the instrumentation caveat does not excuse the magnitude of the current overruns.

## Confirmed root cause of the height-dependent brightness

The artifact is a DDGI coverage and transition failure, not exposure, tone mapping, bloom, fog, or a directional-shadow submission failure.

1. The active volume table exactly matches `GiSponzaRightWallStationary`, not the normal plaza layout:
   - six authored volumes cover navigable lower arcades and walkways only through Y=8.5 m;
   - the near ring is 32x12x32 at 1 m spacing;
   - the mid ring uses 3 m spacing;
   - `IndirectIntensity` is increased to 1.5.
2. From the low plaza, the dense near ring ends at Y=8.875 m. The Sponza upper architecture reaches approximately Y=18.757 m, so much of the visible upper façade is shaded from the coarse mid ring.
3. From the higher camera position, the near ring recenters to Y=6.875-17.875 m and densely covers most of the same façade. The result becomes darker and visually plausible.
4. Coarse probes around the open courtyard contain more sky and direct-light energy, use spacing-scaled bias and visibility variance floors, and are more vulnerable to cross-wall leakage. The 1.5 indirect multiplier magnifies the discontinuity.
5. The normal plaza configuration already contains an authored Y=9-19 m upper slab specifically for the upper courtyard. The validation scenario replaces it with volumes optimized around navigable locations. That policy is wrong for receiver shading because volume selection is performed at the shaded fragment's world position, not the camera or player's position.
6. Existing tests codify conflicting requirements: the validation test requires all authored volumes to end at or below Y=8.5 m, while the plaza test requires coverage through Y=18.757229 m.

The first correctness objective is therefore to make important visible receiver coverage stable in world space and shared across production and validation configurations. Tuning exposure, sky intensity, tone mapping, or material albedo cannot satisfy this objective.

## Findings that are not the primary cause

The following paths are healthy in these captures and should not be changed speculatively:

- auto exposure is disabled and exposure remains 1.0 in both captures;
- tone-mapper, HDR format, environment intensities, and direct-shadow settings are identical;
- directional-shadow recording is not skipped and no cascade submission overflows;
- there are no shader NaN/Inf samples;
- there are no zero irradiance-atlas texel samples or zero visibility-atlas moment samples;
- the ray-query TLAS is available and has no budget-rejected BLAS instances;
- Vulkan validation reports no warnings or errors;
- toroidal scrolling does not clear the atlas in either captured frame.

These remain regression gates, but they are not justification for broad changes to exposure, shadows, or materials.

## Non-negotiable production invariants

1. The indirect lighting at a fixed world-space receiver is stable across camera position, height, orientation, FOV, and ring recentering, except for physically valid view-dependent specular effects.
2. An important visible receiver never loses dense/authored coverage solely because the camera moved to another navigable floor.
3. Fine valid coverage is authoritative. Coarser rings may fill missing ownership but may not overwrite or brighten a valid finer result.
4. Volume transitions conserve energy and remain visually continuous. A transition may change quality, not mean lighting.
5. Fresh, scroll-exposed, stale-generation, inactive, or unavailable probes never contribute as valid history.
6. Missing support produces a bounded, explicit environment fallback and never an unexplained black or full-strength leaked result.
7. Scene, quality-tier, validation-scenario, and runtime overrides compose deterministically. Applying a validation preset cannot silently replace production receiver coverage.
8. Every tier has one authoritative probe, ray, update, cache, acceleration-structure, scratch, CPU-time, and GPU-time budget. No subsystem may silently exceed or reinterpret it.
9. Normal production telemetry adds less than 0.1 ms GPU and 0.05 ms CPU P95. Expensive investigation counters are explicit and never used for shipping performance qualification.
10. Async execution is used only when synchronization is proven, output is equivalent, and measured P95/P99 frame time improves. Graphics-only remains the correctness reference.
11. A release capture contains enough metadata to reproduce the exact camera, scene, build, shader, settings, and feature state.
12. No warning named "black frame," "over budget," "fallback," or "production ready" may be raised from a one-pixel heuristic or an ambiguous inclusive metric.

## Release-blocking acceptance targets

Targets are evaluated in Release with validation and detailed investigation counters disabled unless the individual test explicitly requires them. The initial reference device is the captured RTX 3060 Laptop GPU; at least one lower-memory Vulkan ray-query device must also pass its appropriate tier.

### Visual stability and correctness

| Gate | Required result |
|---|---|
| Fixed upper-façade receiver luminance across low/high cameras | <= 5% P95 relative difference in diffuse indirect after convergence |
| Worst connected upper-façade transition region | <= 10% relative difference; no visible hard boundary |
| Static temporal luminance variation | < 3% P95 after warmup |
| Camera travel through a ring boundary | No single-frame indirect luminance step > 5% in locked receiver ROIs |
| HDR reference comparison | Relative RMSE <= 12% and FLIP P95 <= 0.08 |
| Thin-wall / adjacent-space leak ratio | At or below a calibrated high-sample reference threshold |
| Unsupported sample with valid environment | Stable non-black fallback |
| Camera teleport | No stale lighting; fallback until valid probes arrive |
| Graphics versus async output | Within the same locked image tolerance |

Direct lighting, exposure, and tone-map parameters must remain locked during the low/high DDGI comparison. Debug views used to isolate `FinalIndirect`, sampled irradiance, volume contributor, visibility, support, and fallback must be captured alongside the beauty image.

### Performance and responsiveness

| Metric | High 1080p target |
|---|---:|
| Total GPU frame P95 | <= 10.0 ms profile budget |
| End-to-end frame time P95 | <= 16.67 ms |
| End-to-end frame time P99 | <= 20.0 ms |
| Total incremental GI GPU P95 | <= 2.5 ms |
| Steady-state probe update P95 | <= 1.0 ms |
| Dynamic-dirty probe update P95 | <= 1.75 ms |
| Forward diffuse-GI incremental cost P95 | <= 0.75 ms |
| GI CPU scheduling and upload P95 | <= 0.25 ms |
| Total GI P99 spike | <= 3.5 ms |
| Visible dirty first update | <= 1 frame P95 |
| Near-field convergence | <= 8 frames P95 |
| Second-volume gather rate outside transition bands | <= 10% of shaded DDGI fragments |
| Second-volume gather rate inside declared transition bands | <= 25% unless an A/B capture proves a justified exception |

An inclusive forward-draw timestamp is not an incremental GI timer. Until the renderer has a direct isolated scope, the incremental gather cost must be measured by deterministic paired GI-on/GI-off or gather-simple/gather-reference runs with identical visibility and warmed state.

### Memory and residency

| Resource | High target |
|---|---:|
| Simple DDGI cache, including sampled mirror | <= 192 MiB |
| Resident GI acceleration structures | <= 256 MiB |
| Far-field cache | Explicit tier cap, never an unbounded remainder |
| GI scratch/transient resources | Explicitly reported and bounded |
| Combined GI residency | Sum of the declared component caps; reported once without double counting |
| Long-run memory growth | No positive trend after warmup and residency stabilization |

If the Sponza reference scene cannot meet the 256 MiB acceleration-structure cap, the implementation must stream, compact, or use ray-query geometry LOD. Raising an internal scenario override is not a production fix.

## Delivery strategy

Correctness and observability precede optimization. Each phase has an independent gate and rollback. Storage migrations, lighting-composition changes, scheduling changes, and async submission changes must not be combined in one pull request.

| Order | Phase | Release outcome |
|---:|---|---|
| 0 | Reproducible evidence and truthful accounting | Every later change can be measured and reproduced |
| 1 | Canonical Sponza configuration | Production and validation cannot drift |
| 2 | Stable receiver coverage | Upper façade retains dense coverage at every camera height |
| 3 | Energy-conserving gather and transitions | Near/mid/authored changes do not alter mean lighting |
| 4 | Bounded scheduling and recovery | Visible changes update promptly without starvation |
| 5 | Forward/update performance | GI cost fits CPU/GPU budgets |
| 6 | Memory, tier, AS, and far-field enforcement | Resource use is bounded and tier-correct |
| 7 | Async compute correctness and benefit | Supported devices overlap work safely |
| 8 | Production diagnostics and budget semantics | Warnings and captures are actionable and truthful |
| 9 | Regression matrix, soak, and rollout | Automated evidence supports shipping |

## Phase 0 - Reproducible evidence and truthful accounting

### 0.1 Freeze the two-camera Sponza reproduction

1. Add named camera bookmarks for:
   - `SponzaPlazaUpperFacadeLow`, starting from the existing Y=1.35 m right-wall validation position;
   - `SponzaPlazaUpperFacadeHigh`, using the exact high-camera transform captured after camera metadata is added;
   - a deterministic 10-20 second vertical path between them.
2. Lock sun, environment, material animation, exposure, resolution, random seeds, and frame warmup count.
3. Define world-space receiver ROIs on the shared central upper façade, right wall, arcade interior, and an outdoor reference patch.
4. Capture beauty, direct-only, final-indirect, sampled-irradiance, volume-contributor, support, visibility, ownership, and fallback outputs.
5. Import the two original JSON files and images under `Plans/Baselines/RealtimeGiClosure-20260715/` with descriptive names and a manifest. Preserve source hashes.

### 0.2 Make captures reproducible

Extend the capture schema with:

- scene kind and active `SamplePerformanceScenario`;
- camera position, yaw, pitch, FOV, near/far plane, view/projection hash, and camera-cut serial;
- frame serial, frames since scene load, warmup state, and frames since each ring recentered;
- build configuration, validation mode, application version/commit, shader bundle hash, and settings schema version;
- resolved GI tier and a stable hash plus human-readable summary of every effective GI setting;
- ring dimensions, spacings, origins, physical offsets, authored bounds, priority, intended purpose, and budget decision;
- `IndirectIntensity`, environment fallback, environment intensities, shadow settings, and exposure state;
- whether each feature is compiled, requested, active, inactive, or in fallback, with the reason;
- diagnostic sampling rates and estimated instrumentation overhead.

Serialize enums as stable names in the human-facing snapshot while retaining schema-compatible numeric values only where required. Increment `SchemaVersion` and keep one migration reader for existing baselines.

### 0.3 Correct timing attribution

1. Keep `GpuForwardOpaqueMicroseconds` explicitly inclusive.
2. Rename or redefine `GpuForwardGiGatherMicroseconds`; it must not be the complete forward draw while being summed as an exclusive GI scope.
3. Add a deterministic paired-run harness for incremental forward GI cost until a direct isolated measurement is technically valid.
4. Report trace, blend, relocation/classification, sampled-atlas synchronization, far-field work, BLAS/TLAS work, and CPU upload independently.
5. Add inner CPU spans around volume-table construction, readback processing, dirty mapping, scheduling, state upload, atlas preservation, and command recording.
6. Measure production diagnostics disabled, normal telemetry enabled, and detailed investigation enabled as separate modes.

### Phase 0 gate

- Two clean runs from the same bookmark differ by < 5% for each timing after warmup.
- The JSON alone identifies and reproduces the camera, scenario, resolved settings, build, and feature state.
- Every GI cost is exclusive or explicitly labeled inclusive/estimated; no inclusive scope is double-counted.
- Normal telemetry overhead is below 0.1 ms GPU and 0.05 ms CPU P95.
- The low/high visual failure is reproducible without manually interpreting attachment order.

## Phase 1 - Canonical scene and tier configuration

### 1.1 Introduce one Sponza GI profile

Create one canonical Sponza GI configuration object or builder used by both `SamplePlazaGlobalIllumination` and `SampleGlobalIlluminationValidation`. It owns:

- authored receiver volumes and their priorities;
- near/mid/far ring dimensions, spacing, vertical policy, ray counts, and update quotas;
- indirect and environment fallback intensity;
- acceleration-structure, cache, scratch, ray, update, and probe budgets;
- shadow, environment, reflection, AO, exposure, and post-processing values relevant to reference output.

Validation scenarios may overlay diagnostics, deterministic camera placement, fixed exposure, or feature isolation. They may not silently replace receiver coverage, raise indirect intensity, or reapply a broad quality preset after scene configuration.

Define and test configuration precedence:

1. hardware capability and memory tier;
2. quality-tier defaults;
3. scene profile;
4. validation overlay;
5. explicit user/runtime override.

Every layer must be idempotent. Reapplying it during load or reload must produce the same settings fingerprint.

### 1.2 Remove hidden artistic compensation

1. Reset the shared Sponza physical indirect multiplier to 1.0 for baseline calibration.
2. Keep artistic styling, if desired, in a separately named scene exposure/style control that is not used to hide transport error.
3. Calibrate against a high-sample reference before changing the multiplier.
4. Fail a validation test if a scenario changes the physical GI multiplier or environment fallback without declaring the reason in metadata.

### 1.3 Compile authored and ring layouts against budgets

Before resource allocation, build a resolved layout report containing requested, accepted, resized, and rejected volumes. Production behavior must be deterministic:

- never silently drop a ring or authored volume;
- preserve explicit receiver-priority ordering;
- report the exact probe and byte cost of each volume;
- reject an invalid tier/profile combination in validation and development;
- use a documented degraded layout in production when hardware cannot support the requested tier;
- include the degradation reason in diagnostics.

### Phase 1 tests

- Normal Sponza and `GiSponzaRightWallStationary` resolve to identical core GI settings and receiver coverage.
- Repeated configuration and scene reload are idempotent.
- No test retains the blanket `Max.Y <= 8.5` requirement.
- Validation overlays cannot change `IndirectIntensity`, volume bounds, or ring layout unless the test explicitly opts into a different oracle.
- Settings serialization round-trips the canonical profile without loss.

### Phase 1 gate

- The two capture modes produce the same resolved GI configuration hash.
- No hidden 1.5 multiplier remains in the Sponza production/validation path.
- The resolved layout is within its declared probe and memory limits before a frame is rendered.

## Phase 2 - Stable world-space receiver coverage

### 2.1 Restore upper-façade correctness immediately

Behind a temporary feature flag, restore the known-correct authored coverage through approximately Y=19 m for the upper courtyard. Use the existing Y=9-19 slab first as the correctness reference. Do not call this the final production layout until it passes the budget gates.

This reference change must demonstrate that the low-plaza upper-façade luminance matches the high-camera result without exposure, environment, material, or direct-light changes.

### 2.2 Replace navigation-only authoring with receiver-aware authoring

Model authored DDGI coverage by purpose:

- `ReceiverHero`: visible architecture requiring stable high-quality shading;
- `NavigableInterior`: player-accessible dark or enclosed spaces;
- `DynamicInfluence`: regions affected by moving geometry/lights;
- `TransitionSupport`: deliberate overlap needed for cascade continuity.

Create compact receiver volumes for the upper central façade, left/right upper walls, and balcony/arcade transition surfaces. A receiver surface may be important even if no camera can stand beside it.

Evaluate three layouts with identical captures:

1. the full Y=9-19 correctness slab;
2. compact upper-façade receiver volumes;
3. compact receiver volumes plus a vertically biased/asymmetric near-ring policy.

Select the lowest-cost layout that passes all visual and transition gates. Prefer compact authored receiver volumes over globally increasing ring density. Keep the full slab as a safe fallback until the compact layout proves equivalent.

### 2.3 Make vertical ring policy explicit

The near ring currently centers/clamps around the camera and therefore changes which remote surfaces receive 1 m probes. Add tier-controlled vertical placement or anisotropic coverage so tall architecture is handled intentionally rather than incidentally.

Requirements:

- independent horizontal and vertical counts/spacing or a documented authored-volume substitute;
- deterministic hysteresis so small vertical motion does not recenter repeatedly;
- receiver-aware coverage validation for each camera bookmark;
- no probe history reuse for a different logical world cell;
- no change to mean lighting when a surface transitions between authored, near, and mid coverage.

### 2.4 Add a CPU coverage validator

Given scene receiver AABBs and a camera path, validate:

- selected primary volume and spacing at representative world points;
- distance to volume edges and blend-band width;
- whether a coarser fallback is available;
- total probe/memory cost;
- expected recenter events;
- uncovered or under-resolved receiver regions.

Emit this as build/test data and optional editor/debug overlay. The Sponza upper façade must be covered at the required spacing for every point on the locked low/high path.

### Phase 2 gate

- Fixed upper-façade ROIs meet the <= 5% P95 low/high indirect-luminance gate.
- No important receiver moves from 1 m directly to 3 m coverage without a declared overlap band.
- No hard volume boundary is visible in beauty, final-indirect, irradiance, visibility, or contributor debug captures.
- The selected layout stays within the resolved tier's probe/cache limits.

## Phase 3 - Energy-conserving gather and cascade transitions

Coverage fixes the immediate Sponza failure, but gather semantics must prevent the same class of artifact in another scene.

### 3.1 Separate selection, sample position, and ownership

1. Select candidate volumes from stable, unbiased world-space receiver position and authored priority.
2. Apply normal/view bias only to probe interpolation and visibility queries, not to unstable volume identity.
3. Report when the biased position exits the selected interpolation domain.
4. Ensure a biased edge sample enters an explicit overlap/fallback path rather than accidentally changing energy.
5. Clamp bias by architectural thickness and world-space maximum, not only by probe spacing.

Add shader-mirror tests for upward, downward, grazing, and reversing view directions at every volume face and corner.

### 3.2 Replace binary full ownership from sparse validity

Audit `SimpleDdgiRadiometricOwnership`. The current rule can grant full spatial ownership when any usable support exists. Replace it with a bounded support-aware rule that:

- keeps normalized irradiance independent of missing-probe count;
- does not amplify one bright valid probe to full-cell authority;
- does not stamp a dark trilinear lattice from inactive probes;
- composes the missing ownership exactly once with the next volume or environment;
- exposes spatial coverage, usable support, directional support, visibility, and final ownership separately.

The exact function must be selected by CPU/shader oracle and visual A/B results, not an arbitrary scalar threshold.

### 3.3 Harden coarse-ring visibility and leak control

1. Calibrate the spacing-scaled Chebyshev variance floor against thin-wall, courtyard, and multi-floor references.
2. Prevent coarse variance floors from turning blocked transport into full visibility.
3. Add receiver-normal, backface, relocation, and probe-to-receiver distance diagnostics per ring.
4. Ensure near geometry remains authoritative over coarse/far data.
5. Consider a conservative geometry/thickness proxy for coarse probes where visibility moments alone cannot distinguish adjacent spaces.
6. Keep irradiance physically bounded; do not solve leaks with an artistic clamp.

### 3.4 Define deterministic overlap blending

- authored volumes win when valid and inside their interior region;
- near/mid/far transitions use an explicit world-space blend band wider than maximum bias;
- both sides of a blend represent the same radiometric quantity;
- weights sum to one, including environment fallback;
- invalid or fresh finer data fades to valid coarser/environment data without reusing stale history;
- the number of sampled volumes is capped.

### Phase 3 tests

- CPU oracle versus shader mirror for volume selection, edge weights, ownership, and composition.
- Fixed irradiance field across authored/near/mid transitions conserves energy within 2% numerically.
- Thin-wall adjacent-room scene stays below the locked leak threshold.
- Open courtyard, closed room, grazing wall, and vertical-stack tests remain finite and continuous.
- Randomized camera/view directions do not change diffuse indirect at a fixed receiver beyond tolerance.

### Phase 3 gate

- The vertical camera path has no > 5% one-frame indirect step.
- Coarse-ring fallback cannot brighten the upper-façade ROI beyond the reference tolerance.
- No NaN/Inf, negative radiance, double environment contribution, or unexplained black ownership gap occurs.

## Phase 4 - Bounded scheduling, scroll recovery, and latency

### 4.1 Make visible correctness the first scheduling priority

Use explicit work classes in this order, each with minimum and maximum quotas:

1. newly exposed and visible near/authored probes;
2. visible dirty probes affected by geometry, light, emissive, or environment change;
3. visible low-confidence or inactive-retry probes;
4. near maintenance;
5. mid maintenance;
6. far maintenance and age refresh.

The queue must know receiver visibility/importance, volume purpose, ring, age, confidence, dirty reason, generation, and estimated ray cost. A 25,249-probe scene cannot meet a one-frame full refresh; the production guarantee is that the visible affected working set begins updating in one frame and converges within the declared window.

### 4.2 Enforce ray and time budgets before dispatch

- maintain hard request and primary-ray caps per tier;
- account for full versus maintenance rays per queue item;
- reserve capacity for newly exposed visible probes;
- prevent continuous motion from starving maintenance or dynamic dirty regions;
- use measured completed GPU time to adapt future budgets with bounded hysteresis;
- keep deterministic fixed-budget mode for validation;
- never increase work merely because a previous frame missed its time budget.

### 4.3 Finish coherent scrolling and recovery validation

Verify state, irradiance, visibility, relocation, classification, age, confidence, dirty latency, generation, and sampled-atlas mirror all map to the same logical cell after positive/negative and multi-axis scrolls.

For a teleport or large remap:

- invalidate incompatible history immediately;
- preserve only provably matching cells;
- show environment/coarser fallback while visible fine probes warm;
- avoid full-buffer CPU uploads and frame-wide stalls;
- record exposed, preserved, invalidated, and first-updated counts per ring.

### 4.4 Repair latency metrics

The current P50/P95 values are pinned at 15 frames while maximum convergence reaches 773-893 frames. Audit histogram lifecycle, censoring, reset semantics, and completion definition. Report:

- event-to-first-scheduled;
- event-to-first-completed;
- event-to-visible-first-effect;
- event-to-converged;
- percentiles per reason and ring;
- outstanding censored events separately from completed samples.

### Phase 4 gate

- Visible dirty first-completed update <= 1 frame P95.
- Near/authored convergence <= 8 frames P95.
- No completed event exceeds the documented hard maximum without an actionable warning and reason.
- A 30-minute random walk shows no starvation, generation mismatch, stale-light flash, or unbounded queue growth.

## Phase 5 - Forward and update performance

### 5.1 Remove pathological second-volume work

The low capture records 7.95 million second-volume gathers. Reduce this structurally:

1. Build per-tile or clustered DDGI candidate data containing the highest-priority relevant authored/ring volumes and transition state.
2. Avoid scanning all nine volumes per fragment.
3. Early-out only when valid interior ownership is sufficient; never trade continuity for speed.
4. Restrict two-volume sampling to declared overlap bands, missing-support cases, or active transition recovery.
5. Add per-volume-pair gather counts and screen-space heatmaps.
6. Enforce the <= 10% non-transition and <= 25% transition gather-rate gates.

### 5.2 Complete sampled-atlas optimization

The sampled atlas is active in the captures. Verify and optimize rather than reimplement it:

- hardware-filtered interior samples match the canonical path;
- octahedral seams have a bounded, measured fallback cost;
- synchronization copies only updated probe ranges/layers;
- no full mirror synchronization occurs without atlas changes;
- sampled-image memory is included in the tier cache cap;
- after equivalence is proven, evaluate making the image representation canonical to remove duplicate SSBO storage.

Any canonical-storage migration must be a separate reversible pull request.

### 5.3 Reduce per-fragment gather cost

Evaluate in this order with image-equivalence gates after each step:

1. skip zero/negligible trilinear corners before state/atlas reads;
2. prefetch or compact state needed by the selected tile candidates;
3. reduce redundant state and visibility reads shared by diffuse/debug paths;
4. ensure rough specular does not perform an undeclared second DDGI gather;
5. move expensive detailed counters to sparse pixels or dedicated diagnostic draws;
6. evaluate subgroup/shared-memory reuse only after simpler changes are measured.

### 5.4 Profile and remove the 18 ms CPU GI path

Use the Phase 0 inner spans to address the largest exclusive costs. Expected work includes:

- replace full probe-state scans/uploads with dirty ranges or GPU-resident operations;
- avoid per-frame allocation, sorting, string construction, and layout rebuilding when unchanged;
- keep volume tables and quotas cached by settings/layout generation;
- process readbacks asynchronously without making scheduling correctness depend on them;
- build update queues on GPU when it produces a measured net win;
- batch barriers, descriptors, and uploads;
- keep investigation formatting off the render thread.

### 5.5 Reduce trace/blend work

- tune full and maintenance rays independently per ring and volume purpose;
- update only scheduled probes and only required atlas texels;
- retain the reduced-complexity blend path if its error stays within tolerance;
- ensure outer rings do not issue long TLAS rays when a valid paged far field should own that range;
- use material/light LOD appropriate to ring resolution;
- validate ray savings against energy and thin-wall references.

### 5.6 Keep non-GI forward cost visible

The inclusive forward pass also contains geometry, material, shadow, and overdraw cost. Complete the remaining `PerfPass2-0.md` LOD/shadow work under separate renderer metrics so GI is not blamed for unrelated cost and unrelated optimizations are not credited to GI.

### Phase 5 gate

- Total incremental GI GPU <= 2.5 ms P95 and GI CPU <= 0.25 ms P95 in the shipping measurement mode.
- Simple DDGI trace/blend and forward gather each meet their sub-budgets.
- Low/high views both meet the total frame P95/P99 targets.
- Detailed counters are demonstrably responsible for only their separately reported overhead.
- All optimized paths pass image equivalence and transition gates.

## Phase 6 - Memory, tiers, acceleration structures, and far field

### 6.1 Establish one budget authority

Resolve the conflict between the Development profile's 8,192-probe/96 MiB GI limits, DdgiHigh's 192 MiB cache allowance, and the scenario's 512 MiB acceleration-structure override.

The resolved quality tier must provide component budgets consumed by both allocation and `RenderBudgetEvaluator`. The evaluator must report:

- requested versus accepted probes per volume;
- canonical DDGI storage, sampled mirror, state, update queue, scratch, and transient bytes;
- BLAS, TLAS, retired, scratch, and metadata bytes;
- far-field page/table/cache bytes;
- component status against its own cap;
- combined unique residency against the sum of component caps;
- actual heap usage separately from engine-tracked allocations.

Do not compare acceleration structures against a separate 512 MiB cap and then also include them under a 96 MiB total cap. Do not count the same bytes twice.

### 6.2 Bound probe/cache memory by tier

- generate per-tier ring and authored-volume budgets rather than relying on the absolute 32,768 implementation cap;
- preserve near/hero quality before far density;
- support a deterministic compact layout for Medium/Low;
- include sampled-atlas duplication in decisions;
- fail validation when a scene requests more than the tier can represent;
- test quality switching, resize, reload, and memory-pressure degradation.

### 6.3 Make AS streaming effective

Streaming is marked active, but all 444 Sponza static instances remain resident and consume 406.66 MB. Implement a bounded working set:

- retain detailed near/important geometry;
- use ray-query BLAS LOD or compact geometry for mid range;
- evict or page remote static BLAS under a hard budget;
- preserve thin walls and upper-façade occluders needed for correctness;
- update TLAS incrementally and bound retired allocations;
- report candidate, resident, evicted, rejected, and fallback geometry by reason.

### 6.4 Make far-field state truthful and useful

The capture advertises `paged-far-field` while pool capacity and resident pages are zero. Either:

1. provision and validate a real bounded far-field cache used by outer rings; or
2. report the feature as supported/requested but inactive with a precise reason.

Never expose an active feature flag for an empty implementation state. Missing pages must fall back predictably and cannot cause near geometry to disappear behind coarse data.

### Phase 6 gate

- Every tier stays within component and combined hard memory limits.
- Sponza High resident AS <= 256 MiB or a formally approved platform-specific tier defines and meets a different cap without contradicting the budget evaluator.
- A two-hour travel/streaming run has bounded resident, retired, and scratch memory with no upward trend.
- Far-field flags, capacity, residency, timing, and fallback reason agree.

## Phase 7 - Async compute correctness and measured benefit

### 7.1 Fix the current plan-validation fallback

Async compute is requested and supported by a dedicated compute family, but execution is disabled and latched because a non-terminal segment accesses the acquired swapchain image.

1. Add a focused render-graph test reproducing the exact compiled segment/resource plan.
2. Trace which pass/resource declaration causes early swapchain access.
3. Keep swapchain ownership in the terminal graphics segment; compute candidates must use offscreen resources only.
4. Validate concrete resource bindings, queue-family ownership, release/acquire barriers, semaphore stages, and first consumer waits before recording.
5. Reject only the invalid plan generation. A recoverable graph/settings rebuild may retry; do not permanently latch a session-wide fallback without a non-recoverable correctness reason.
6. Preserve the graphics-only path and capture the fallback reason without changing output.

### 7.2 Enable only profitable async work

Candidate passes include DDGI trace, blend, relocation/classification, and far-field update only when dependencies permit real overlap. Measure:

- compute queue busy time;
- overlapped versus serialized time;
- ownership-transfer and semaphore overhead;
- graphics critical-path reduction;
- P50/P95/P99 frame time and power/thermal stability.

Use `Auto` only after a device-specific history proves a net benefit. Disable async on devices/topologies where transfers or serialization regress frame time.

### Phase 7 gate

- Graphics and async paths are Vulkan-validation clean over resize, reload, tier switch, capture, and shutdown.
- No plan fallback occurs on the supported reference topology.
- Output stays within locked image tolerance.
- Async improves P95 or P99 frame time by a predeclared meaningful amount; otherwise graphics-only remains selected.

## Phase 8 - Production diagnostics and budget semantics

### 8.1 Replace the black-frame one-pixel heuristic

`DdgiBlackFrameSuspect` currently becomes true when any diagnostic sample has zero final indirect and either DDGI/IBL is zero. In these captures all simple-DDGI irradiance samples are nonzero, yet the warning fires.

Replace it with separate metrics:

- total sampled opaque receiver pixels;
- expected-GI receiver pixels;
- unsupported pixels and fraction;
- valid zero-radiance pixels and fraction;
- zero final indirect caused by black albedo, metallic response, AO, or disabled environment;
- unexpected zero indirect fraction in declared validation ROIs;
- frame-wide blackout fraction;
- consecutive affected frames;
- state relative to recenter, clear, teleport, and warmup.

Reserve `BlackFrameSuspect` for a calibrated large-area/frame-wide failure. Use separate names for local support holes and expected black material output.

### 8.2 Make warnings actionable

Every warning must include:

- observed value and threshold;
- active tier/profile/scenario;
- camera and frame serial;
- affected ring/volume/resource;
- whether the value is current, P95, cumulative, or stale readback;
- recommended next debug view or capture;
- suppression reason when the subsystem is intentionally inactive.

Warnings must clear when the condition clears. Cumulative historical maxima may be reported, but cannot masquerade as the current frame state.

### 8.3 Correct feature and budget status

- distinguish `Supported`, `Requested`, `Active`, `Fallback`, and `Disabled`;
- do not list paged far field as active with zero capacity;
- surface async fallback in the top warning list;
- report probe/layout budget mismatch before allocation;
- serialize budget statuses as names;
- separate profile mismatch from genuine allocation overrun;
- report actual heap headroom independently from configured engine tier.

### 8.4 Add receiver/cascade evidence

For sparse diagnostic pixels and named ROIs, record:

- primary and secondary volume/ring;
- spacing and edge distance;
- biased versus unbiased grid position;
- valid probe count and support;
- sampled irradiance and visibility;
- ownership and environment fallback;
- final diffuse-indirect luminance.

This turns a camera-dependent screenshot into a directly attributable volume/cascade report.

### Phase 8 gate

- Healthy reference captures contain no false black-frame warning.
- Deliberate support-hole, blackout, async-fallback, and budget-overrun tests emit the correct warning and clear afterward.
- A snapshot explains the low/high Sponza difference without source-code inference.
- Inactive metrics report unavailable with a reason, not zero-as-success.

## Phase 9 - Regression matrix, CI, soak, and rollout

### 9.1 Unit and CPU-oracle tests

- canonical settings precedence and idempotence;
- authored receiver coverage and budget resolution;
- world-position volume selection and overlap weights;
- biased sample behavior at all faces/corners;
- support-aware ownership and environment composition;
- coarse visibility and thin-wall cases;
- toroidal logical/physical mapping and generation validity;
- dirty-event scheduling, quotas, fairness, latency histograms, and hard budgets;
- memory accounting without overlap/double count;
- async segment and resource-plan validation;
- capture schema serialization/migration;
- warning thresholds, lifecycle, and false-positive resistance.

### 9.2 Shader-oracle tests

- authored/near/mid/far transition continuity;
- irradiance and visibility decoding across sampled-atlas seams;
- normalized gather with partial support;
- fully blocked, open, and mixed visibility;
- view/normal bias over all ring spacings;
- invalid/fresh/inactive/stale probe rejection;
- NaN/Inf/zero-weight handling;
- environment represented exactly once;
- graphics versus async resource/result equivalence.

### 9.3 Visual regression scenes

1. Sponza upper façade from low and high bookmarks plus vertical travel.
2. Sponza dark arcades versus sunlit courtyard.
3. Thin-wall adjacent rooms with bright exterior.
4. Tall multi-floor interior with vertical camera travel.
5. Cornell box with moving wall, light, and emissive panel.
6. Camera teleport and rapid backtracking.
7. Missing/late far-field pages.
8. Streaming geometry entering/leaving AS residency.
9. Day/night environment transition.
10. Skinned, instanced, foliage, and particle receivers where supported.

Store HDR references, LDR review images, masks/ROIs, settings fingerprints, and diagnostics together. Golden updates require explicit visual-review approval and a written reason.

### 9.4 Performance matrix

Run at minimum:

- 1600x900 to compare with the reported captures;
- 1920x1080 as the High release gate;
- Low, Medium, High, and Ultra where supported;
- production diagnostics, detailed diagnostics, and validation modes separately;
- graphics-only and async-auto;
- stationary warm, vertical traversal, continuous dynamic dirty work, and teleport recovery;
- reference RTX 3060 Laptop GPU plus one lower-memory ray-query GPU.

Use fixed clocks/power where the test environment permits, record driver and thermal state, discard warmup, and evaluate P50/P95/P99 rather than a single frame.

### 9.5 Long-run and recovery gates

- two-hour random camera walk and vertical traversal;
- repeated scene reload, resize, fullscreen/swapchain recreation, quality switch, and GI kill-switch cycles;
- far-field and BLAS residency churn;
- frame/generation serial wrap simulation;
- delayed readback and queue completion;
- device-loss recovery path where available;
- shutdown with no live Vulkan objects or retained allocations.

### 9.6 Rollout controls

Keep temporary, narrowly scoped flags for:

- canonical Sponza layout;
- receiver-aware volume coverage;
- corrected ownership/transition composition;
- optimized tile candidate gather;
- bounded scheduler;
- canonical sampled atlas, if migrated;
- AS streaming/LOD;
- paged far field;
- async compute.

Each flag needs an owner, A/B capture, rollback condition, telemetry, and removal milestone. Retain one emergency GI kill switch that falls back to stable environment/reflection lighting without restart. Remove old Sponza configuration paths and obsolete flags after the release candidate completes soak and rollback windows.

### Phase 9 gate

- All unit, shader, visual, performance, memory, async, validation, and soak gates pass on the required matrix.
- No release-blocking warning is present in healthy production captures.
- The low/high Sponza sequence passes automatically and cannot regress silently.
- Rollback and GI kill-switch behavior are tested and documented.

## Recommended pull-request sequence

Each pull request must build, test, capture, and roll back independently.

1. Capture schema vNext: scenario, camera, resolved settings, build/shader identity, feature states.
2. Baseline harness and Sponza low/high bookmarks, ROIs, debug outputs, and repository artifacts.
3. Timing/memory accounting corrections and production-versus-investigation measurement modes.
4. Canonical Sponza profile and deterministic settings precedence; remove the validation 1.5 multiplier.
5. Restore Y=9-19 correctness reference coverage and add the low/high visual test.
6. Add receiver-aware compact volumes and budget compiler; select the production layout by A/B gates.
7. Stabilize volume selection, bias, ownership, and authored/near/mid blending.
8. Calibrate coarse visibility and thin-wall leak control.
9. Repair scheduler priorities, latency measurement, and scroll/teleport recovery.
10. Add tile/candidate volume selection and bound second-volume gathers.
11. Optimize sampled-atlas synchronization and per-fragment gather cost.
12. Remove CPU state/upload hot paths and reduce trace/blend work.
13. Unify tier budgets and correct unique memory accounting.
14. Bound AS residency and make far-field state active or truthfully inactive.
15. Fix async swapchain segmentation, synchronization, retry policy, and device selection.
16. Replace black-frame and warning semantics; complete actionable diagnostics.
17. Run the full release matrix, soak, visual approval, and staged rollout.
18. Remove obsolete configuration paths and temporary feature flags after the rollback window.

## Primary code ownership

| Area | Primary files |
|---|---|
| Sponza scene profile and validation overlays | `NjulfHelloGame/SamplePlazaGlobalIllumination.cs`, `NjulfHelloGame/SampleGlobalIlluminationValidation.cs`, `NjulfHelloGame/Program.cs`, `NjulfHelloGame/SampleInputController.cs` |
| Settings, tiers, and serialization | `Njulf.Rendering/Data/RenderSettings.cs` |
| Volume layout, budgets, scheduling, state, scrolling | `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs` |
| Trace/blend/relocate passes | `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`, `Njulf.Shaders/ddgi_simple_trace.comp`, `Njulf.Shaders/ddgi_simple_blend.comp`, `Njulf.Shaders/ddgi_simple_relocate_classify.comp` |
| Gather, visibility, ownership, transitions | `Njulf.Shaders/ddgi_simple_shared.glsl`, `Njulf.Shaders/forward.frag` |
| AS residency and ray-query geometry | `Njulf.Rendering/Resources/AccelerationStructureManager.cs` |
| Far-field pages and tracing | far-field resource/pass code and `Njulf.Shaders/farfield_clipmap.glsl` |
| Render graph and async submission | render-graph planning code and `Njulf.Rendering/VulkanRenderer.cs` |
| Diagnostics and budgets | `Njulf.Rendering/Data/RendererDiagnostics.cs`, `Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`, `Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs` |
| Unit and shader mirrors | `Njulf.Tests/SimpleDdgiVolumeManagerTests.cs`, `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`, focused new Sponza/config/diagnostics/async tests |

## Risk register and mitigations

| Risk | Mitigation |
|---|---|
| Adding upper coverage exceeds the absolute probe cap and silently drops rings | Compile layout against the tier before allocation; start with correctness slab, then choose compact receiver volumes; never silently drop |
| Support-aware ownership reintroduces a dark probe lattice | Normalize radiance separately from ownership; validate CPU oracle and partial-support scenes before rollout |
| Coarse visibility hardening removes legitimate bounce | Calibrate against closed/open references and lock leak plus energy thresholds |
| Aggressive gather early-outs create transition seams | Restrict early-out to validated interior ownership and keep transition heatmaps/gates |
| Ray/maintenance reductions slow dynamic response | Reserve visible dirty quotas and enforce first-update/convergence gates |
| AS streaming removes important occluders | Receiver/occluder importance tags, conservative near residency, missing-geometry diagnostics, visual tests |
| Canonical sampled-image storage creates seam or synchronization regressions | Separate migration PR, canonical reference path, exact seam tests, rollback flag |
| Async fixes introduce ownership or lifetime errors | Graphics reference, concrete-resource validation, Vulkan validation, stress/recovery suite |
| Performance qualification is distorted by detailed counters | Separate modes, record sampling rate and overhead, qualify only the production mode |
| Golden-image updates normalize a regression | Require ROI metrics, settings hash, reviewer approval, and written update reason |

## Explicit non-solutions

Do not:

- lower exposure, sky intensity, environment diffuse, or wall albedo to hide the upper-façade artifact;
- retain `IndirectIntensity = 1.5` as compensation for missing or leaking transport;
- add a world-sized dense slab without enforcing the selected tier's probe and memory budgets;
- loosen visibility globally until the coarse-ring image looks brighter or darker;
- clamp irradiance to an artistic maximum instead of fixing ownership and leakage;
- disable the warning without replacing its invalid one-pixel semantics;
- relabel the 87 ms inclusive forward draw as isolated GI work;
- raise Development/High budgets to match current consumption without a tier and hardware decision;
- call async production-ready while the plan is falling back;
- call paged far field active with zero capacity and zero resident pages;
- approve a Debug/validation/detailed-counter capture as the shipping performance result;
- keep duplicate Sponza production and validation layouts after canonicalization.

## Final definition of done

Realtime GI is production-ready only when all of the following are true:

1. The Sponza upper façade matches the locked reference from both low and high cameras and during the complete vertical path.
2. Normal and validation scenarios use the same canonical receiver coverage and physical GI settings.
3. Authored, near, mid, far, and environment transitions conserve energy and have no visible boundary.
4. Visible dirty work begins within one frame and converges within eight frames P95 without starvation.
5. Total GI CPU/GPU, total frame time, probes, cache, AS, scratch, and combined unique residency meet the resolved tier's hard budgets.
6. Second-volume gathering is bounded to intentional transition/recovery cases.
7. AS streaming and far-field behavior are bounded, truthful, and preserve important geometry.
8. Graphics and supported async paths are validation-clean, visually equivalent, and async is selected only when beneficial.
9. Captures contain camera, scenario, build, shader, resolved settings, active/fallback feature state, and reproducible baseline identity.
10. Black-frame, support-hole, budget, and fallback warnings are calibrated, actionable, and free of known false positives.
11. Production telemetry stays within its overhead budget and detailed counters are never confused with shipping performance.
12. Unit, shader-oracle, visual, performance, memory, recovery, and two-hour soak suites pass on the required device/tier matrix.
13. The emergency GI fallback works without restart and has a tested rollback path.
14. Obsolete Sponza layouts, temporary compatibility paths, and completed feature flags are removed after the rollout window.
15. The release evidence—code revision, settings, shaders, captures, metrics, approvals, and known limitations—is stored together and reviewed.

