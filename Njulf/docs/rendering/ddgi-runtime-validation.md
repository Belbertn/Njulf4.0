# Simple DDGI Runtime Validation

This checklist validates the active Simple DDGI implementation in runtime scenes. The retired full-DDGI scheduler and legacy SSGI pipeline are not part of this validation; the bounded C5 Hi-Z residual is the sole supported screen-space near-field path.

## Required Scenes

- `GiSponzaRightWallStationary`: shadowed arcade/alley support, raw diffuse, and fallback behavior.
- Sponza slow horizontal and vertical travel, including near/mid ring-boundary sweeps.
- Sponza cuts between plaza, upper facade, and alley/interior views.
- `GiCornellRoom`: colored bounce and receiver coverage.
- `GiLongCorridorOcclusion`: thin-wall visibility and leakage behavior.
- `GiFastTraversalTeleport`: camera-cut recovery and Simple DDGI warmup.
- A broad outdoor scene with a large empty-air near volume.
- Tall multi-floor architecture under every vertical-ring policy.
- Transparent cloth/glass, foliage, particles, and fog receivers.
- Dynamic geometry entering/leaving an unallocated or suppressed page.
- A deliberate page-capacity stress scene with deterministic coarse fallback.
- One sun with zero local lights, then symmetric 1/8/64/256/1,024-local-light arrays with stable packed-order permutations.
- Moving point/spot rigs that exercise tree refit, rebuild, publication rejection, and urgent source refresh.
- Constant environment, isolated L2 basis signals, rough white/metal spheres, white furnace, and SSR/reflection-probe/DDGI/environment transitions.
- Two differently posed instances sharing one skinned mesh, plus stop/start, topology/LOD change, and frame-slot reuse.
- Alpha-mask grids, stacked blended curtains, colored thin panes, layered decals on static/skinned bases, and an explicitly unsupported thick-glass object.
- Authored forest and procedural grass under wind, camera-only motion, and probe-ring/volume transitions.

## Locked Residency Matrix

Run at least three identity-locked Release repetitions for each applicable scene:

1. `Dense`: authoritative same-binary rollback and image/memory baseline.
2. `Shadow`: dense output plus predictor and exact representative gather-touch evidence.
3. `SparseNearRing`: authoritative bounded near payload with dense mid/far fallback.

Record executable identity, shader hashes, settings/capture identity, GPU/driver, scene revision, resolution, warmup/measurement windows, and whether async compute was forced. Do not compare captures with different identities or silently reduced page budgets.

Useful command-line overrides are:

```text
--simple-ddgi-residency-mode=dense|shadow|sparse-near-ring
--simple-ddgi-sparse-page-budget=<pages>
--simple-ddgi-sparse-min-page-budget=<pages>
--simple-ddgi-sparse-retention-frames=<frames>
--simple-ddgi-sparse-max-admissions=<pages>
--simple-ddgi-sparse-max-feedback=<requests>
--simple-ddgi-sparse-inactive-retry-frames=<frames>
--simple-ddgi-storage-mode=legacy|validate|packed
--simple-ddgi-mirror-coverage=disabled|full-canonical|receiver-relevant
```

The equivalent environment variables are
`NJULF_RENDERER_SIMPLE_DDGI_STORAGE_MODE` and
`NJULF_RENDERER_SIMPLE_DDGI_MIRROR_COVERAGE`. A non-disabled mirror override
also enables the sampled-atlas feature switch. Every quality preset uses
`Packed` storage by default. High and Ultra enable the sampled atlas with
`ReceiverRelevant` coverage; Low and Medium keep it disabled. Use these
overrides for controlled `Legacy`, `Validate`, `FullCanonical`, or disabled
rollback/A-B captures.

## Locked Storage Matrix

Keep the accepted canonical volume table, source ordinals, ray schedule, random
seed, camera, lights, materials, warmup, and capture frames identical. Run the
following phases independently, with two baseline repeats and at least three
timing repetitions for a candidate:

1. `legacy` + `full-canonical`: FP32 source cache, stored direction, 32-byte scratch.
2. `validate` + `full-canonical`: the same byte sizes, fixed 32-epoch codebook, and stored-direction shadow comparison.
3. `packed` + `full-canonical`: mixed Compact-28/24 cache regions and 20-byte direction-free scratch.
4. `packed` + `receiver-relevant`: compact complete-volume mirror ranges.
5. `packed` + `disabled`: canonical-only receiver reference and image-path fallback check.

Every switch must advance the storage/allocation generation, recreate
incompatible resources, clear source/atlas validity, and perform a cold coherent
rebuild. It must not reinterpret live bytes or issue `vkDeviceWaitIdle`.

## Content-Dependent Qualification Matrix

Keep scene, probe/source epochs, stochastic hash ABI, sampling-sequence epoch, camera path, light identities, material revisions, and capture frames locked across comparisons. Record requested and effective modes separately.

1. Run the qualified previous behavior with all content-dependent rollout features disabled.
2. Enable only `ManyLightSampling`; cover zero locals, exact-threshold boundary, and tree mode independently. Repeat symmetric scenes under at least 100 deterministic packed-light permutations and compare the stochastic mean/confidence interval with the exact oracle.
3. Enable only `DirectionalRadiance` in receiver-only mode. Run constant-field, isolated-basis, rotated-lobe, roughness-sweep, white-furnace, SSR, local-probe, and environment ownership captures. Diffuse output must remain equivalent to the disabled run.
4. Enable `OneBounceGlossyTransport` only after receiver-only passes. Verify previous-generation parity, source-generation rejection, energy amplification, and monotonic convergence.
5. Enable `RecursiveGlossyTransport` only after one-bounce passes. Verify the four-byte/ray F0/roughness sidecar over every admitted region, identical solve/audit operator fingerprints, independent RGB contraction/tail bounds, source-generation rejection, and deterministic fallback to `OneBounce` on injected storage or audit failure.
6. Enable only `CurrentPoseGeometry`; exercise shared-mesh/different-pose identity, frame-slot reuse, topology rebuild, forced dynamic-AS budgets, and regional invalidation.
7. Enable only `TransparentGeometry`; independently exercise alpha masks, stable stochastic blend, deterministic thin transmittance, layer/candidate overflow, decals, and unsupported thick refraction.
8. Enable only `FoliageGeometry`; compare proxy integrated transmittance and bounced-color ROIs while moving the camera without changing world/probe inputs, then exercise wind cadence and forced triangle-budget exhaustion.
9. Run the reviewed combined profile and reconcile aggregate GI/AS time and memory rather than granting each addition the entire frame budget.

For every directional run, require native pipeline creation for all three
modules (`directional_prepare`, `directional_project`, and
`directional_publish`), not shader-build success alone. On a GPU-resident
transaction, the stage must execute after diffuse blend and before canonical
publish. Reconcile accepted, committed, published, and receiver-record counts;
failed commits, producer failure bits, missing completion bits, and transaction
predicate bits must all be zero. Also include a fully reverse-face/zero-support
fixture: it must publish a checked zero-sample SH record, commit diffuse data,
and leave directional reflection ownership at zero.

`LegacyTopKReference` and `L1Reference` require explicit validation authorization and must be visibly named in the capture. `RecursiveCertified` is an explicit opt-in production mode; it must report its sidecar and audit state and may fall back only to `OneBounce`, never to an unreported recursive implementation.

For bounded sample runs, `--ddgi-content-conformance` (or
`NJULF_RENDERER_DDGI_CONTENT_CONFORMANCE=true`) grants runtime-only
authorization to the content-dependent modes requested by the selected
quality preset. It is not persisted, does not claim device qualification, and
is rejected by `--benchmark-require-production`.

## Debug Buffers

Capture `FinalIndirect`, `DdgiIrradiance`, `DdgiSampledIrradiance`, `DdgiFinalDiffuse`, `DdgiRawDiffuse`, `DdgiCoverage`, `DdgiSupportCoverage`, `DdgiDataConfidence`, `DdgiVisibilityMoments`, and `DdgiUpdateReasons` after cold start and after at least 120 steady frames. For paging runs also capture `DdgiProbeResidency`, `DdgiResidencyFallback`, `DdgiPageAge`, and `DdgiPhysicalPage`. For content-dependent runs additionally capture local-light PDF/sample identity, directional-SH/source ownership, ray-geometry/proxy class, alpha/transmittance/decal decisions, dynamic-AS age, and regional invalidation overlays when the build exposes them.

Probe overlays must retain the regular virtual lattice. Confirm that nonresident probes are neutral/hollow rather than omitted, newly mapped pages remain fresh until complete publication, and missing fine ownership visibly resolves through the expected coarser ring.

## Metrics To Record

- Simple DDGI active probe count, updated probe count, and primary ray count.
- Scheduler request and primary-ray budgets, plus any rejected work.
- Recenter, atlas-clear, atlas-fresh, and warmup state.
- Page demand, page residency, page feedback, scheduler, trace, transport, blend, directional prepare/project/publish aggregate, relocation, and canonical publish timings.
- DDGI atlas/buffer memory and the configured tier budget.
- Virtual/dense/sparse physical capacities, sampled-atlas rounded capacity, page padding, arena bytes, dense-equivalent bytes, allocated bytes, and avoided bytes.
- Resident/free/initializing/published/suppressed pages and per-ring virtual/resident/active/inactive/demanded/converged populations.
- Visible and receiver demand, retained age buckets, admission/eviction/failed-admission counts, pressure streaks, suppression/retry, and request overflow.
- Allocation-to-first-schedule and allocation-to-first-publication P50/P95/max.
- Page/reverse disagreement, duplicate owners, stale virtual/mapping/resource requests, out-of-range requests, nonresident rejections, and coarser fallbacks.
- In Shadow, predictor false negatives/positives, false-negative rate, inflation ratio, and the exported working-set/capacity simulation report.
- Storage ABI/mode, canonical visibility bytes, cache bytes and rays by format, recursive glossy sidecar bytes/rays, cache padding, scratch stride/bytes, and FP16-distance eligibility/rejection by volume.
- Mirror requested/eligible/admitted/provisioned probes, typed logical bytes, actual allocator bytes, excluded volume identities, mapping/allocation generations, and fallback reason.
- Image hits, interior opportunities, seam/unmirrored/invalid-map fallbacks, cache non-finite/saturation/error maxima, and direction epoch/angular-error evidence from a detailed build.
- Detailed validation-bank readback validity. Direction histogram cardinality must equal its sample count, and the reported P99 bound must remain finite even when samples reach the overflow bucket.
- `ContentDependentDdgi` configured/active feature masks; every requested/effective mode pair; all fallback/migration reasons; light/tree/ray-scene/source/sampling revisions and ABI versions.
- Light-tree action/reason, local/leaf/node/depth counts, full publication revisions/generation, readback pending/validation failures, exact planned/allocated/live/retired/peak bytes, and build/refit/reuse/bypass totals.
- Many-light bypass/exact/tree hits, samples, duplicates, visibility/zero-term work, uniform repairs, invalid sample/PDF repairs, PDF statistics, maximum estimator weight, and exact-oracle comparison evidence.
- Directional-radiance budget/planned/allocated/parity bytes, projection/receiver counters, roughness ownership, invalid/cross-generation rejection, negative reconstruction/clamp energy, one-bounce convergence evidence, and recursive per-channel contraction/tail evidence.
- C5 active resolution, layout revision, joined stage-timing sample count, P50/P95, demotion/promotion count, rejected history/source counts, and exact independent memory categories. P95 above 0.75 ms must demote; half-resolution promotion requires sustained P95 at or below 0.45 ms and an admitted `AutoQualified` half profile.
- GPU-resident accepted/committed/published/failed counts plus transaction predicate, missing-completion, producer-failure, and cache-read masks. The combined content profile must prove the trace-only many-light flag payload does not alter cached-solve first-color/sweep completion.
- Ray-scene resource generation versus content epoch; current-pose/proxy/excluded counts; dynamic BLAS build/refit/topology/budget counts; storage/scratch/primitive/retired bytes; and BLAS/TLAS timing.
- Stochastic-alpha, thin-layer, unsupported-material, decal association/overflow, and invalid-metadata counters.
- Foliage requested/represented density, density error, near/mid/far cards, excluded patches, wind age, content/cadence signature, policy version, GPU buffer bytes, and CPU/GPU generation time.

## Acceptance Checks

- Simple DDGI publishes visible diffuse bounce in covered shadowed regions after warmup.
- Support, data confidence, and effective contribution become nonzero after warmup; environment fallback remains available where support is low.
- Scheduled requests and primary rays remain within their configured hard budgets.
- Recenter, atlas clear, and camera-cut frames are treated as transient evidence rather than steady-state regressions.
- Simple DDGI timing and storage remain within the selected quality tier budget.
- Emergency degradation preserves visible, dirty, and newly exposed near-field updates before background refresh work.
- Invalid indices, duplicate owners, stale mutations, bounded-buffer overflow, stale publication, and feature-attributable Vulkan validation messages are zero.
- A full pool never reallocates, overruns, or evicts a currently demanded, pinned, or in-flight page; excess demand receives deterministic dense coarser lighting.
- Ordinary-motion first-publication P95 is evaluated independently from cut/teleport P95; aggregate latency must not hide a failing cohort.
- Stationary settled admission and eviction counts are zero. Ordinary motion has no sustained pressure.
- First coherent fine publication P95 is at most two rendered frames for ordinary motion and at most eight for the declared cut/teleport stress path.
- Shadow false-negative P95 is at most 0.5%, demand inflation P95 is at most 1.5×, and no actual demanded page is missed for more than two consecutive rendered frames.
- The current-profile residency arena is at most 512 KiB and the hard virtual-limit fixture is at most 1 MiB.
- Topology-identical sparse saves at least the greater of 16 MiB or 10% of same-binary Dense live bytes. The current High fixture saves 40,442,096 bytes (209,946,912 Dense versus 169,504,816 Sparse) with a 139,024-byte arena.
- Total tracked GPU memory is at most 80% of the target 2 GiB profile, and stable frames create/destroy/rebind no residency or payload resources.
- Added sparse forward-gather P95 is at most 0.15 ms and 5%; page-management GPU P95 is at most 0.25 ms; added CPU P95 is at most 0.10 ms with no stable-frame per-page enumeration.
- Dense/Sparse HDR comparisons meet the frozen error gates and human review finds no black flash, stale-cell flash, page seam, ring seam, new leak, or transparent/fog/foliage pumping.
- RG16F canonical and mirrored visibility bytes are exactly 1,024 bytes per provisioned probe; moment `.xy` payload bits match the legacy writer input and invalid/fresh probes remain fail-closed through probe state.
- Legacy cache radiance/distance remain FP32 bit-exact. Compact-28 is exactly 28 bytes/ray with FP32 distance; Compact-24 is exactly 24 bytes/ray and is used only after its static range/ULP/thickness gates. Packed scratch is exactly 20 bytes/result.
- Packed writes report zero non-finite values and zero radiance saturation. Direction epoch mismatches and invalid-map fallbacks are zero; stored-versus-reconstructed direction maximum/P99 remain inside the established octahedral SNORM16 bound.
- Final-indirect relative RMSE is at most 1%, beauty HDR-FLIP P95 at most 0.02, named ROI mean-luminance shift at most 2%, and named ROI P95 shift at most 3%, unless an existing approved-reference gate is stricter.
- Thin-wall/corridor/curtain/ring dark-to-lit leak ratio does not rise by more than 0.5 percentage points absolute or 5% relative, whichever is stricter; no new connected bright region, stale mirror flash, or ring-transition step appears.
- Receiver-relevant image hits cover at least 95% of measured forward interior opportunities before it is considered for a preset. It reduces mirror bytes versus full-canonical and neither forward nor total-GI P95 regresses outside repeatability noise.
- Compiler logical bytes, Vulkan allocation diagnostics, and performance-snapshot totals agree. Optional mirror admission never changes the accepted canonical source-ordinal set.
- The one-sun/no-local run allocates and dispatches no light tree and remains within benchmark noise of the qualified baseline.
- Exact local-light runs match the all-lights oracle. Tree runs retain every nonzero-support light, have finite positive PDFs, preserve duplicate draws, and place the brute-force result inside the predeclared 95% confidence interval with less than 1% measured residual bias.
- Packed-light permutations do not change the statistical result; stale/partial tree publication is never consumed, and publication/PDF correctness counters remain zero outside deliberate fault injection.
- L2 DC/basis/rotation and FP16 pack tests pass; DDGI rough ownership is zero below the approved band; source weights remain normalized; receiver-only leaves diffuse output equivalent; one-bounce passes the energy/convergence gate; recursive transport passes the same-operator RGB audit or deterministically resolves to one-bounce.
- C5 starts at quarter resolution in explicit mode, remains at or below 0.75 ms P95 on the RTX 3060 qualification run, demotes to eighth under pressure, and never promotes to half without both bound evidence and sustained timing headroom. Disabled or rejected C5 records no dispatches and composites exact canonical DDGI+B3.
- Directional prepare/project/publish pipelines create natively on every target driver; a valid zero-sample record commits diffuse data but never claims reflection ownership; all scheduler transaction masks remain zero in the combined many-light profile.
- Current-pose ray silhouettes follow both differently posed shared-mesh instances without bind-pose/stale-slot flashes. Ordinary pose/material/wind changes advance content epochs and bounded dirty regions without changing resource generation or globally clearing DDGI.
- Alpha decisions replay identically, transparent visibility remains deterministic, decals never occlude, and sustained candidate/layer overflow is zero. Unsupported thick glass is visibly proxied or excluded.
- Camera-only motion does not change foliage proxy content signatures, tier selection, or stable placement. Wind age stays within the declared cadence and proxy density/transmittance/color remain inside the frozen ROI gates.

## Transition and Failure Exercises

Exercise Dense → Shadow → Sparse and back at frame boundaries; enable/disable; unsupported prerequisites; sampled atlas supported/disabled/budget-rejected; V2 ray-capacity changes; resize/resolution changes; scene reload/topology changes; one-cell scrolling on every axis; large scroll/cut/teleport; pool-full pressure; dynamic geometry invalidation; light/material/atmosphere changes; frames-in-flight retirement; graphics queue; forced async validation and rejected async fallback; and shutdown.

The bounded automated mode exercises the live residency transition without
restarting the renderer and verifies mode telemetry, exact settings rollback,
resource-generation changes, feedback readiness, mapping invariants, and stable
device identity:

```text
--smoke-mode=ddgi-residency-switch
--smoke-frames=12
--quality-preset=ddgi-high
--simple-ddgi-scheduler-mode=gpu-resident
--simple-ddgi-residency-mode=sparse-near-ring
```

The bindless storage heap is shared by all frames in flight. A capacity or
residency-arena transition therefore completes the renderer's submitted frame
fences before rewriting a live update-after-bind descriptor. This is a targeted
resource-generation transaction, not `vkDeviceWaitIdle`; the transition smoke
must report zero device-idle calls. Old resources remain completion-token owned
until the descriptor readers have finished, and a transition fails closed if
the returned fence progress cannot certify the old generation.

On a runtime scheduler/residency failure, verify that mutation freezes without a device wait. A valid last map may remain readable; an invalid map disables the sparse fine volume and uses the dense coarser field. Controlled re-entry must install a fresh residency-resource generation before mutation resumes. No hidden full-density near payload may exist beside sparse mode.

Force each content-dependent fallback at least once: tree allocation/finalization/readback mismatch, invalid bound/PDF, directional-sidecar budget/allocation failure, dynamic BLAS storage/scratch/build/primitive exhaustion, topology mismatch, transparency/decal caps, unsupported thick refraction, foliage generation unavailability, and foliage triangle exhaustion. The previous complete compatible generation or documented exact/proxy/exclusion path must remain usable; the corresponding runtime and performance snapshots must contain a nonempty reason. No failure may publish partial tree/AS/SH state or call device idle during steady-state recovery.

For development pin/freeze testing, enable debug tooling and use the explicit renderer APIs. Verify a pinned missing page is admitted at the highest class, a pinned resident page is never a victim, unpin restores ordinary retention/eviction, and development freeze stops mutation without clearing a valid map. Merely selecting any debug view must leave residency unchanged.

## Shadow Working-Set Export

Validation capture code supplies predictor and instrumented page sets to `SimpleDdgiResidencyWorkingSetAnalyzer`. Export the resulting `SimpleDdgiResidencyWorkingSetReport` with `WriteJson`. The report includes per-frame unique demand, candidate retention populations, 2×2×2 and offline 4×2×4 demand geometry, coverage errors, required pool P50/P95/P99/max, deterministic admission/eviction/pressure simulations, and exact memory projections. Freeze this evidence before changing shipping page capacity or retention.

## Automation Hooks

The console and performance JSON diagnostics are aligned with `docs/rendering/ddgi-diagnostics.md`. Pure address, allocator, memory, ABI, shader-contract, graph-order, and working-set tests live in `Njulf.Tests`. Runtime/HDR thresholds still require a Vulkan-capable machine and captured scene evidence; a green unit suite does not substitute for the locked repetitions and human review above.
