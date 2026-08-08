# Sparse Surface-Aware Simple-DDGI Probe Paging Implementation Status

- Date: 2026-08-05
- Source plan: `SparseSurfaceAwareSimpleDdgiProbePagingImplementationPlan-20260803.md`
- Base commit: `4e5b6f32939dee8acf53830819582c483ef6a102`
- State: feature implementation complete and enabled for qualified tiers;
  correctness/memory/lifecycle automation and the targeted Sponza hallway/
  traversal production gates pass; broader cross-scene promotion remains open
- Release sample assembly SHA-256:
  `b96173233be50c50114fdc3224e710c5af07170a0f6af699904f1024992052ed`
- Release rendering assembly SHA-256:
  `04b999321eff9d9972bbed755b98c5098af2b968cf4de6ee47d32020825b948c`
- Target used here: NVIDIA GeForce RTX 3060 Laptop GPU, Vulkan 1.4.341,
  driver 610.248.0

## Result

`Dense`, `Shadow`, and authoritative `SparseNearRing` are implemented. Sparse
mode retains the existing virtual grids and toroidal coordinates, pages only
the near camera ring with fixed 2x2x2 probe pages, and keeps mid/far rings dense
as the mandatory coarse fallback. High, Ultra, and DDGI High select sparse
residency; Low and Medium remain dense.

The implementation includes GPU demand, deterministic reconciliation,
generation-safe physical addressing, resident-only scheduler publication,
fixed-size fence-complete feedback, inactive-page suppression/retry, controlled
failure/re-entry, diagnostics/debug views, settings/serialization, working-set
analysis, memory admission, and explicit development pin/freeze controls.

The final frames-in-flight issue found during live Dense -> Shadow -> Sparse
testing is fixed. Njulf has one shared update-after-bind heap, so replacing a
live descriptor while an older submitted frame can read it is illegal even if
the old allocation is deferred for destruction. A residency generation change
now completes only the renderer's submitted frame fences, verifies the returned
completion token, and then publishes the new descriptor. It never calls
`vkDeviceWaitIdle`; insufficient progress fails closed. Auxiliary page-arena
replacements use the same transaction.

## Implemented plan phases

- Phase 1: settings/schema, exact checked page layout, virtual/toroidal/page/
  physical address contracts, virtual/physical memory split, deterministic CPU
  reference model, admission/degradation, and hard-limit fixtures.
- Phase 2: fixed residency arena, placeholder binding, bindless/render-graph
  resources, generation-stamped 1 KiB feedback, retirement, exact diagnostics,
  and independently timestamped demand/residency/feedback passes.
- Phase 3: conservative depth demand, representative receiver-touch oracle,
  receiver-miss demand, Shadow comparison, working-set analyzer/export, and
  predictor coverage metrics.
- Phase 4: paging params and per-volume records, physical payload helpers,
  compact receiver records, extended update identity, and generation checks in
  trace, transport, blend, relocation, publication, sampled publication, and
  debug paths. Dense remains identity addressed.
- Phase 5: bounded GPU reset/classify/reconcile/initialize/feedback stages,
  deterministic admission/eviction, fail-closed mapping publication,
  nonresident rejection, partial ownership, and dense coarser fallback.
- Phase 6: reconciliation precedes scheduler classification; nonresident work
  is filtered; newly admitted pages wake; GPU-emitted updates carry physical
  address and mapping/resource identity; fixed summaries drive lifecycle,
  convergence, source, and atmosphere accounting.
- Phase 7: empty-page suppression/retry, geometry-generation invalidation,
  camera-cut policy, runtime freeze/fallback/re-entry, completion-token
  retirement, live transition smoke coverage, and development pin/freeze.
- Phase 8 implementation: pass-level timings, exact diagnostics, benchmark and
  Shadow evidence hooks, runtime CLI controls, quality-tier promotion, shader
  audits, documentation, and this status report.

Phase 9 remains the plan's explicitly separate optional density experiment and
was not used to claim topology-identical sparse completion.

## Deliberate implementation decisions

- The runtime reconciliation path uses direct bounded deterministic
  classification/ranking rather than dispatching the originally proposed
  prefix stage. The standalone prefix shader remains available for ABI/build
  coverage, but omitting a redundant runtime dispatch reduced overhead without
  changing ordering or determinism.
- The update record is 48 bytes, not an aspirational 32 bytes. It retains the
  virtual probe, physical payload address, virtual/page/resource identities,
  and existing scheduling/ray metadata needed for fail-closed validation.
- Current same-binary Dense bytes differ from the historical number written in
  the plan because the GPU-resident scheduler, transport-tail, and compact
  receiver work changed the shared binary. Every gate here compares the current
  Dense and Sparse plans, never the historical binary.
- Simple-DDGI paging remains in one serial graphics ownership segment. Forced
  async validation is clean, but no Simple-DDGI async path is promoted without
  independent eligibility and profitability evidence.

## Automated evidence

Fresh Release build:

- 0 warnings, 0 errors.
- All production receiver SPIR-V contract checks passed.
- Production non-scheduler diagnostic-atomic audit passed.
- `git diff --check` found no whitespace errors (Git reported only configured
  LF-to-CRLF conversion notices).

Fresh Release tests:

- 1,893 passed, 0 failed.
- Focused paging, ABI, transition, and smoke tests: 159 passed, 0 failed.
- Coverage includes address round trips, odd grids/padding, deterministic
  allocation and eviction, generations, suppression/retry, memory degradation,
  settings/schema, CPU/shader mirrors, graph order/barriers, receiver physical
  access, stale rejection, fallback composition, and descriptor-publication
  ordering.
- One earlier no-build invocation left a zero-CPU test host after the command
  timeout. The verified orphan was terminated; an immediate full rerun with
  VSTest hang diagnostics completed all 1,893 tests in 59.5 seconds and did not
  identify a hung fixture.

Key shader identities:

- page demand:
  `0df6014e628596ec9cfcf0932bd3ffac86690363f4887c474c34a5a8a44699f4`
- page reconcile:
  `dd2b57e13361fe0c9a7c509004aad0b5602a391d530b8cda940d2803dbdce6cc`
- opaque compact Simple-DDGI receiver:
  `50df0028ba8e90beb618a339cb93ade7a5b0198e0d44e60d4bf8e72047a0b5a6`

## Target-hardware evidence

The two current-build transition reports below used the exact Release assembly
identities above. The broader matrix was captured against the immediately
preceding Release sample assembly
`656c7faa8638973ab254238becde5e5eeef515f3a5c881d3d6795337d9005852`.
The intervening production change only preserved the append-only numeric value
of a stall-reason enum; both Standard and synchronization validation were rerun
against the final assemblies for the affected resource-generation transition.
All reports used the same RTX 3060 Laptop device identity.

| Exercise | Result |
| --- | --- |
| Exact-current-build Standard Dense -> Shadow -> Sparse -> rollback | Passed; authoritative Sparse final state and valid feedback; 0 validation warnings/errors; 0 mapping, duplicate, stale, range, early-retirement, or device-idle findings |
| Exact-current-build synchronization-validation transition | Passed with the same zero invariants |
| Three consecutive preceding-build Standard transitions | Passed; 0 validation warnings/errors; 0 mapping, duplicate, stale, range, overflow, or early-retirement findings; 0 device-idle calls |
| 48-frame Standard steady Sparse run | Passed; feedback/state valid; 57 resident and published pages, 903 free, no pressure |
| 32-frame Standard Shadow run | Passed; actual 57, true positive 57, false negative 0, false positive 0, inflation 1.0x |
| Sparse resize sequence | Passed at 1280x720, 1920x1080, and 800x600; 0 validation messages |
| Two sparse scene reloads | Passed; post-reload frames observed; 0 validation messages |
| Forced-async synchronization run | Passed; independent queue available but no Simple-DDGI async segment was eligible, so the certified graphics path remained active |

Primary artifacts:

- `artifacts/sparse-ddgi-final/final-standard-residency-switch-current-build.json`
- `artifacts/sparse-ddgi-final/final-sync-residency-switch-current-build.json`
- `artifacts/sparse-ddgi-final/final-standard-residency-switch-fence-{1,2,3}.json`
- `artifacts/sparse-ddgi-final/final-sync-residency-switch-fence.json`
- `artifacts/sparse-ddgi-final/final-standard-sparse-48-fence.json`
- `artifacts/sparse-ddgi-final/final-standard-shadow-32-fence.json`
- `artifacts/sparse-ddgi-final/final-standard-sparse-resize.json`
- `artifacts/sparse-ddgi-final/final-standard-sparse-reload.json`
- `artifacts/sparse-ddgi-final/final-sync-sparse-forced-async.json`

## Correctness and memory gates

The current High fixture reports:

- Dense equivalent: 209,946,912 bytes.
- Sparse allocated capacity: 169,504,816 bytes.
- Avoided bytes: 40,442,096 (19.26% of Dense).
- Residency arena: 139,024 bytes, below the 512 KiB gate.
- Tracked GPU memory: 444,342,031 bytes, about 20.7% of the target 2 GiB
  profile and below its 80% gate.
- Nonresident gather rejection/coarser fallback: 46/46.
- Mapping disagreement, duplicate owners, stale mutations, out-of-range
  requests, request overflow, sustained pressure, and retired bytes: all zero.

The memory saving exceeds both the 16 MiB/10% minimum and the 32 MiB target.
It is calculated from immutable allocation capacity, not current occupancy.

## Original qualification gaps

This status does not claim the plan's full production acceptance certificate.
The implementation is enabled, but the following evidence gates remain:

- The identity-locked Sponza/Cornell/outdoor/multi-floor/transparent/dynamic
  scene matrix and linear-HDR Dense/Shadow/Sparse comparisons have not been
  captured three times.
- Human motion, cut, page/ring boundary, thin-wall, leak, foliage, fog,
  particle, and transparency review is not automatable in this session.
- A 4,096-frame benchmark settling attempt still timed out because the
  concurrently modified transport-tail certificate never reached the shared
  benchmark's settled state. The resulting report is intentionally marked
  non-comparable and cannot certify paging performance.
- That non-comparable trace measured GPU frame P95 8.28 ms and forward P95
  4.21 ms, but page-demand P95 0.524 ms exceeded the paging plan's 0.20 ms
  demand-plus-reconcile target. A shorter non-comparable A/B trace showed
  forward average +0.123 ms / +2.99%, but P95 cannot be accepted from an
  unsettled pair.
- No Nsight register/occupancy profile or approved HDR reference/human signoff
  was supplied.

These are evidence/promotion gates, not hidden fallback implementations. Sparse
remains reversible through the Dense setting, all unsupported prerequisites
fail to dense coarser lighting, and async remains unpromoted. Do not label the
feature fully production-qualified until the missing locked evidence passes the
unchanged thresholds.

## Hallway/refresh follow-up qualification

The follow-up implementation in
`../SimpleDdgiHallwayHotspotRefreshAndStutterFixPlan-20260806.md` supersedes the
original status above for the targeted Sponza hallway and ordinary-traversal
gates. It does not supersede the still-open broader scene matrix or Nsight
signoff.

Three clean target-GPU Release captures share contract fingerprint
`e761924d1b914048439c67ac1c3f4030a50f7dc5bebcfdce669eea06e8a7f902`.
Each completed with 121/121 artifacts verified by the capture gate and by an
independent SHA-256/byte-length pass. Every traversal ended with 223 resident,
223 published, and zero initializing pages; ordinary publication P95 was one
frame and pending fresh/source work was zero. Their high endpoints each reached
a current 1.4959423% tail certificate with exact 6,063/6,063 participant and
388,032/388,032 texel coverage.

The final high endpoint uses 111,474,128 allocated sparse bytes versus
138,383,040 dense-equivalent bytes, avoids 26,908,912 bytes, and has zero
retired bytes, mapping disagreements, duplicate owners, stale/range requests,
or request overflow. Three production forward-gather pairs measured +0.065,
+0.044, and +0.057 ms P95; final production captures measured 0.614-0.620 ms
total GI GPU and 0.058-0.066 ms GI CPU P95. The exact reports, hashes, quality
oracle, lifecycle results, and qualified binary identities are recorded in
section 7 of the follow-up document.
