# Quality-locked loading and performance integration evidence

- Date: 2026-09-01
- Device: NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0
- Target: native 1920x1080, 60 fps; GPU p95 at most 10 ms, renderer CPU
  p95 at most 6 ms, p99 at most 16.67 ms, and at least 20% headroom beneath a
  2 GiB tracked-memory target
- Integration base: `e7b01acc` on `Simplified-SDF`
- Donor: `5a67aa4e` on `perf/quality-locked-1080p60-20260830`
- Captured build identity: `9ffa11ba70ae8ab75848d8813b8e8b281871fcf9`

## Outcome

The three approved plans are implemented in the renderer, shader build, sample
harness, controls, and tests. The loading work removes receiver-feedback and
receiver-cache pipeline families from the first visible production frame. The
performance candidates remain individually reversible, and the async resource
and synchronization plans are complete.

Qualification does not establish the requested 1080p60 result. The final
ShippingPerformance Bistro exact-gather capture reports GPU p95 33.686 ms,
renderer CPU p95 13.933 ms, and 2,494,885,131 tracked bytes. The adaptive/static
receiver-cache experiment is slower and fails the quality envelope, so Exact is
the production default. The current same-family compute topology has no
compatible profitable Auto certificate, so Auto stays on graphics. These are
fail-closed dispositions, not silent promotions.

## Loading implementation

| Contract | Implementation | Disposition |
| --- | --- | --- |
| Exact canonical first production frame | The active-scene manifest no longer waits for exact receiver-feedback producers, adaptive receiver compute, transparent feedback partitions, or hybrid cache receiver programs. | Retained |
| Pipeline-free feedback configuration | Feedback mode/configuration and command recording do not create pipelines. Missing banks keep the exact path. | Retained |
| Immutable post-present publication | Receiver feedback/runtime/compute and hybrid receiver families build on the bounded compiler and publish only as complete banks. | Retained |
| Benchmark/capture readiness | Benchmark and quality clocks wait for authenticated production diagnostics and the requested receiver path to be effective; bootstrap/exact-fallback frames cannot be mislabeled. | Retained |
| Targeted shader native-compiler workaround | Forward ray-query and exact receiver-attribution families append `-Od`; ordinary production shaders retain `-Os`. | Operationally retained; isolated startup A/B is inconclusive |
| Startup observability | JSONL and window-title progress expose elapsed time, completed pipelines, active count, oldest duration, and active basename. | Retained |
| Capture ownership | Startup and quality automation use renderer-owned final-LDR/HDR captures. No Windows screenshot or overlay API is used. | Retained |

Relevant integration commits include `c81e2ff9`, `86c6cf6b`, `27eebb2f`,
`f2823e0b`, `adadc9fa`, `94b9dbdd`, and `9ffa11ba` plus the final working-tree
integration recorded with this evidence.

### Startup measurement

The controlled ShippingPerformance Bistro/DdgiHigh/Standard renderer-owned
1600x900 final-LDR run produced:

- responsive bootstrap: 3.811 s (3 s target missed, 5 s hard limit passed);
- production graph: 15.676 s (control-plane measurement only);
- visually qualified final frame: 16.733 s;
- 16 compile misses, so the run is not exact-warm and uses the
  empty/incomplete-cache 15 s target / 30 s hard-limit classification;
- zero validation warnings/errors and zero render-critical pipeline creations;
- post-visible receiver specializations and compute bank completed without
  extending the final-frame milestone.

The multi-minute black startup is removed. The bootstrap and incomplete-cache
soft targets remain unmet. No controlled native-pipeline A/B proves the
plan-specific 50% `-Od` creation-time criterion, so no isolated performance
claim is made for that compiler option.

## Candidate integration ledger

"Implemented" means the source path, rollback, telemetry, and tests are present.
It does not mean the candidate passed the current-branch three-cycle ABBA keep
gate. Where current compatible evidence is absent, the performance disposition
is explicitly inconclusive.

| Candidate | Feature control | Integration | Current disposition |
| --- | --- | --- | --- |
| Master/per-feature controls | master and full mask | `7af3dc8d` | Retained control plane |
| MR-001 complete meshlet working set and resolved addressing | `meshlet-working-set`, `resolved-meshlet-addressing` | `bf540f28` | Implemented; current formal ABBA inconclusive |
| MR-002 stable refinement lanes | `stable-ddgi-refinement` | `ae953963` | Implemented; current quality run still exposes separate capacity-generation clears |
| MR-003 redundant hybrid projection elision | `hybrid-projection-elision` | `5df06881` | Implemented; current isolated ABBA inconclusive |
| MR-005 screen-local admission | `screen-local-receiver` | `835dfc87` | Implemented; active only for an explicit cache experiment |
| MR-006 complementary receiver programs | `split-hybrid-forward` | `54153746` plus native accepted/fallback artifacts in this integration | Rejected for production cache activation; Exact is default |
| MR-008 row-major adaptive gather | `row-major-gather` | `7c9db588` | Implemented; active only for an explicit cache experiment; isolated ABBA inconclusive |
| MR-009 shared resolve staging | `shared-resolve-staging` | `f9625178` | Implemented; active only for an explicit cache experiment; isolated ABBA inconclusive |
| MR-011/MR-012 static debug and bent-normal specialization | `static-shader-specialization` | `2b161a0c` plus native lane artifacts | Implemented; static shrink is supporting evidence only |
| Directional-lattice sharing | `directional-lattice-sharing` | `79af826e` | Implemented with scalar fallback; current runtime keep claim inconclusive |
| Publication-generation reuse | `generation-reuse` | `3a015d32` | Implemented with invalidation/wrap rollback; current runtime keep claim inconclusive |
| Asymmetric sided streams | `asymmetric-sided-streams` | `2be9064f` | Implemented with symmetric fallback; current runtime keep claim inconclusive |
| Masked-feedback compaction | `compact-masked-feedback` | `940b4061` | Implemented with dense overflow fallback; current runtime keep claim inconclusive |
| Sparse hybrid-lobe payload | `sparse-hybrid-lobe` | `c826d502` | Implemented with attachment rollback; current runtime keep claim inconclusive |
| Atomic DDGI/far-field async plans | `async-gi` plus independent async mode | `756670e7` plus final topology/scheduler/resource fixes | Correctness infrastructure retained; Auto performance activation rejected on current topology |

MR-004, MR-007, MR-010, MR-013, and MR-014 remain excluded as directed by
the integration plan. Their reverted donor experiments were not reintroduced.

### Receiver-cache disposition

The current static inventory is 24 accepted-only artifacts (20.93 MiB,
1,272,450 instructions), 24 exact-fallback artifacts (27.03 MiB, 1,643,926
instructions), and 24 combined rollback artifacts (34.35 MiB, 2,035,538
instructions). The separate modules are smaller than the combined module, but
that does not translate into an end-to-end win on the current renderer:

| Runtime path | GPU frame | Forward opaque | Transparent | Cache |
| --- | ---: | ---: | ---: | ---: |
| Combined cache receiver | about 160 ms | about 129 ms | included in the pathological frame | active |
| Static accepted/fallback split | about 49.3 ms | about 17.75 ms | about 17.2 ms | about 4.2 ms |
| Exact receiver control | about 35.3-38.7 ms | about 17.9 ms | about 2.5 ms | 0 ms |

The split duplicates receiver raster work and is slower than Exact. Its
deterministic comparison against Exact also produced FLIP p95 about 0.195,
outside the 0.02 contract. Production presets therefore request
`SimpleDdgiReceiverCacheMode.Exact`; cache variants remain explicit experiments
and rollback artifacts.

### Async disposition

The renderer now represents the complete DDGI residency/light-tree footprint,
projects immutable resource state, attaches waits to the first concrete
consumer, preserves exact graphics completion domains, and elides unnecessary
same-family per-allocation barriers. Device selection prefers a second
compute-capable queue in graphics family 0 over dedicated family 2 to avoid
thousands of exclusive ownership transfers.

A forced same-family Standard-validation run completed with zero Vulkan
warnings/errors, but did not prove useful overlap or whole-frame improvement.
The existing source certificates are topology-specific to a distinct dedicated
compute family. Auto therefore reports no eligible path on graphics family 0 /
compute family 0 and executes on graphics. Forced validation remains available.
The pre-reconciliation graphics/forced timing probes are not comparable ABBA
captures and are not used for a keep claim.

## Static and automated verification

- Development solution build: 140 compiled, 27 cache hits, 317 up to date;
  succeeded with zero warnings/errors.
- Release solution build: 484 shader artifacts up to date; succeeded with zero
  warnings/errors.
- The additional Rider `ProfileSymbols` whole-solution pass reported no IDE
  problems, but remained in one optimized `ddgi_simple_trace.comp` invocation
  for more than 70 minutes. That exact compiler child was terminated and Rider
  consequently reported the optional pass as failed without diagnostics; it is
  not used as correctness or performance evidence.
- ShippingPerformance build: production diagnostic-atomic audit passed for 268
  forward/non-scheduler modules; 244 receiver/update modules retained their
  exact pinned counts; 15 bounded scheduler modules were correctly excluded.
- Receiver verifier: 12 receiver modules, 6 cache-required forward modules, 72
  ownership-locked variants, and the resolve ABI passed.
- Full Release tests: 4,365 passed, 0 failed, 1 skipped. The skip is the known
  unavailable cooked Sponza fixture.
- Focused async tests: 116 passed, 0 failed.
- Final benchmark timing-reconciliation tests: 3 passed, 0 failed. The runtime
  capture then reconciled all five added shadow/planar parents with zero
  unexplained GPU time.
- Performance campaign manifest validation: 9 workloads, 7 qualification
  workloads, passed validate-only checks.
- Vulkan validation in the qualifying Standard runs: zero warnings/errors.
- No runtime debugger was required.

## RenderDoc and renderer-owned image evidence

The successful RenderDoc file
`.perf-loop-runs/receiver-static-split-warm-20260901/renderdoc-postpublication/njulf-postpublication_frame542.rdc`
is 1920x1080, but replay/export shows one present, one dynamic-rendering begin,
and zero draw/dispatch commands. It captured the bootstrap clear and exposed the
capture-readiness race fixed by this integration. Subsequent injected attempts
did not reach a usable post-publication production capture within a practical
runtime. The plan's requested pair of production RenderDoc captures is therefore
not satisfied and no GPU-pass conclusion is inferred from this file.

The deterministic quality harness instead produced ten renderer-owned 1920x1080
PNG frames with embedded SHA-256 receipts. It did not call the Windows screenshot
path.

## Final native 1080p result

The final controlled ShippingPerformance command used Bistro, DdgiHigh, exact
receiver gathering, all performance bits requested, Auto async, VSync off, 30
warm-up frames, 120 measured stationary frames, and the strict 1080p60 gate.
It required 943 additional convergence frames and did not time out.

| Metric | p50 | p95 | p99 | Gate |
| --- | ---: | ---: | ---: | --- |
| Renderer CPU frame | 12.734 ms | 13.933 ms | 18.105 ms | Failed 6 ms p95 and 16.67 ms p99 |
| GPU frame | 31.255 ms | 33.686 ms | 34.185 ms | Failed 10 ms p95 and 16.67 ms p99 |
| Independent GPU-pass sum | 31.255 ms | 33.686 ms | 34.185 ms | Reconciled exactly |
| Unexplained GPU time | 0 ms | 0 ms | 0 ms | Passed |

Tracked GPU memory was 2,494,885,131 bytes versus the 1,717,986,918-byte limit
required for 20% headroom on a 2 GiB target. Validation and GI diagnostic
warning/error counts were zero. The capture contract was comparable, production
timing was true, and it reported zero identity or timing mismatches.

Dominant GPU p95 scopes were ForwardPlus/ForwardGiGather 17.816 ms (one aliased
scope, not two additive passes), AO 4.541 ms, hybrid-reflection DDGI base
3.443 ms, transparent 2.292 ms, AO blur 1.362 ms, motion vectors 0.919 ms,
directional-shadow temporal 0.898 ms, and depth 0.530 ms. The approximately
20 ms frame-fence wait is GPU backpressure, while renderer `DrawSceneTotal`
matches the 13.933 ms CPU-frame p95.

## Quality result and unresolved gate

The deterministic 240-frame native 1920x1080 Bistro moving-camera capture
passed projection, exposure, camera-cut, warm-up, scheduler-feedback, topology,
and toroidal-scroll checks. All 42 recentered frames preserved the atlas.
Nevertheless it failed because two non-recenter frames cleared the canonical
atlas and complete receiver map, causing four global convergence restarts.

Source/telemetry correlation narrows the cause to canonical capacity/storage
invalidation, most likely a synchronized buffer reallocation; the per-frame
capture does not export the exact capacity-transition reason. In that source
path the clear is required when a canonical buffer cannot preserve its contents
without overlapping old and new allocations. Removing it narrowly would either
retain an oversized generation or exceed the hard memory budget during overlap.
The harness and clear contract were not weakened. This remains an open renderer
quality/memory architecture issue and blocks a combined-stack quality pass
independently of the receiver-cache rejection.

The same run reported 2,504,657,547 tracked bytes, so it would fail the memory
contract even if the two clears were eliminated.

## Evidence artifacts

The runtime artifacts are ignored local evidence, not source-controlled build
outputs:

| Artifact | SHA-256 |
| --- | --- |
| `.perf-loop-runs/final-quality-locked-20260901/final-shipping-1080p-exact-r3.json` | `523630ed01c27226f1f0ca87c374315b54666b702173f2c5e076eba126483a0c` |
| `.perf-loop-runs/final-quality-locked-20260901/final-shipping-1080p-exact-r3.health.json` | `8ce6ac57ac69cb7b51a65c0c6067ef8feb0e75c716785c7a9a301ad62c36a887` |
| `.perf-loop-runs/final-quality-locked-20260901/final-shipping-1080p-exact-r3.startup.jsonl` | `957d177f026d8385b43f3da98a8289bc859f929ae751420a360a3cc9a9d8de1d` |
| `.perf-loop-runs/final-quality-locked-20260901/quality-exact.health.json` | `7c1e802573afcbbac7607da3ea93109b879378747c317f30384a2a1990801ff6` |
| `.perf-loop-runs/final-quality-locked-20260901/quality-exact/bistro-quality-run.json` | `3358070c99e00a4cde58fa83ec75b437ea897526225860a4cb29aa44b31c91bf` |

## Completion disposition

Implementation and rollback delivery is complete. Loading hard limits pass for
the available incomplete-cache capture, though soft targets do not. The
receiver-cache performance candidate is rejected for production activation;
async Auto is quarantined on the current topology; most other individual
candidate performance claims remain inconclusive without fresh three-cycle
ABBA evidence. The aggregate 1080p60, memory, and moving-camera quality gates
fail, and the missing cooked Sponza fixture prevents the complete scene matrix.

Accordingly, this evidence does not claim that the quality-locked performance
plan achieved its success contract and does not move the user-supplied plan into
`implementation/Complete`.
