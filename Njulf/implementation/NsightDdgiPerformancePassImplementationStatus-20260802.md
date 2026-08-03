# Nsight/DDGI performance pass implementation and verification guide

Date: 2026-08-02  
Source plan: `NsightDdgiPerformancePassPlan-Revised-20260802.md`  
Implementation state: implementation complete; target-GPU evidence captured;
performance, Nsight, visual, and transition-matrix acceptance remains open

## 1. Result

The revised plan is implemented as renderer, shader, diagnostics, benchmark,
and test changes. The implementation now provides a clean ProductionTiming
binary, a separate DetailedInvestigation path, a generation-owned stable DDGI
capacity fast path, incremental scheduling/readback, gather and decal
attribution, convergence retirement and compact dispatch, memory ownership
evidence, locked benchmark identities, post-timing HDR comparison, and a
strict Nsight shader-profile artifact contract.

Three 600-frame ProductionTiming runs and a matching DetailedInvestigation run
were captured on the target NVIDIA GPU. They prove several intermediate gates,
but not the complete performance contract. The current renderer CPU, GI GPU,
stable Capacity, Upload, settled transport-plus-blend, and tracked-memory
headroom gates pass. The 10 ms GPU-frame target, opaque-forward sub-budget,
0.25 ms GI CPU target, provisional four-decal target, and strict current-binary
repeatability gate do not pass. Nsight shader metrics, an approved HDR reference
with human art review, and the transition/invalidation matrix remain external
acceptance work.

## 2. Implemented plan phases

### Phase 0: controlled ProductionTiming baseline

Implemented:

- Benchmark capture requires at least 120 samples for comparability and exports
  count, average, minimum, median, P95, and maximum for CPU frame, GPU frame,
  each independent GPU pass, and each CPU stage.
- Production classification requires Release, ShippingPerformance, or
  ProfileSymbols; validation off; detailed DDGI code absent; no detailed
  readback; no debug overlay, screenshot, or RenderDoc work in a measured frame;
  and a steady, settled DDGI state.
- Every measured frame is checked against the first for GPU/driver, resolution,
  quality, scene revision/state, camera, executable, commit/dirty state, shader
  bundle, timestamp period, GI settings, feature isolation, debug view, and DDGI
  cache generation.
- The pair identity locks hardware, authored scene, scene revision, exact camera
  and projection, build/executable/shaders, settings schema, resolved GI state,
  feature isolation, and DDGI cache generation. Repeat mode additionally locks
  the exact rendered-state hash and variant. A/B mode deliberately excludes only
  the rendered target delta represented by the named variant.
- GPU independent-pass totals are reconciled to the GPU-frame duration per
  sample. The allowance is calculated from the device timestamp period and
  integer-duration quantization and is written into the report.
- `--compare-benchmark-pair` compares two report files. Repeat mode enforces the
  5% P95 gate for CPU/GPU frames and material passes at or above 0.50 ms, while
  still reporting every pass. The 0.50 ms boundary is 5% of the 10 ms GPU-frame
  budget and prevents timestamp-scale noise in minor passes from vetoing a run.
  `--benchmark-pair-ab` retains identity validation while allowing intentional
  timing movement. A pass disabled by an A/B variant is represented as zero
  rather than making the pair structurally incomparable.
- Linear HDR PFM capture starts only after the final timing sample. The report
  stores bounded-file hashes, extent, RMSE, relative RMSE, mean absolute error,
  maximum absolute error, threshold, and result.
- A bounded, strict `njulf-nsight-shader-profile-v1` artifact can be attached.
  GPU, driver, executable, and shader hashes must match. Both opaque-forward and
  geometry-decal fragment stages are mandatory, with live registers, spills,
  local memory, occupancy, coherence, texture/storage loads, instruction count,
  and dependency reasons.
- Production mode requires a pair ID, at least 120 measurement frames, and an
  HDR reference. Shader-profile evidence can be made mandatory independently.
- Benchmark simulation advances at a deterministic 60 Hz, disables adaptive
  DDGI budgeting, and defaults async compute off unless the caller explicitly
  overrides it. Source-refresh cadence is derived from the nominal 60 Hz rate,
  not observed frame rate, and measurement requires 30 consecutive ready
  frames after settling.

### Phase 1: stable-capacity CPU stall and scheduler

Implemented:

- `SimpleDdgiCapacityKey` owns quality tier, topology fingerprint, probe/ray/
  request capacities, readback mode, sampled-atlas mode and budget, transport
  mode, and feature state.
- A matching key returns before memory-plan construction, Vulkan buffer-size
  queries, allocation, descriptor registration, or device waits. Validation
  builds verify the cached plan against live resources.
- Capacity telemetry separately reports CPU state growth, plan creation,
  predicate work, buffer lookups/lock time, device-idle wait, transitions,
  readback reconciliation, sampled-atlas admission/ensure, descriptor work, and
  retired-resource destruction.
- Transition telemetry includes a reason bitmask, transition/wait counts, total
  required live bytes, and previous/required bytes for every canonical atlas,
  transport buffer, ray scratch, probe state, queue, classification, readback,
  and sampled-atlas resource.
- All DDGI and sampled-atlas device-idle waits go through renderer stall
  telemetry. There is no direct unreported wait on the stable path.
- True capacity changes synchronize once, release generation-incompatible
  allocations when required by the hard budget, batch descriptor registration,
  and otherwise retire old buffers after the frames-in-flight safety interval.
  Retired count and bytes are exported.
- Probe scheduling uses persistent class/volume queues and a wake heap. Stable
  membership is retained; only changed probes are refreshed. Visibility work is
  reused while camera and volume generations are unchanged.
- Completed probe-state readback processes only the submitted update-index set,
  not the entire probe pool. Scratch arrays and dispatch lists are retained.
- Update work is grouped into contiguous ray-count batches so dispatches use the
  actual active rectangle rather than a queue-wide maximum-ray rectangle.

### Phase 2: far-field gating and productionization

Implemented:

- Far-field sky visibility is evaluated only for meaningful environment
  fallback instead of every near-full-DDGI-ownership fragment.
- A `far-field-forced-old` benchmark variant restores the old evaluation path
  for a controlled image/timing pair.
- Detailed counters use a 16 by 16 sample stride and weight 256. The exported
  sky-visibility count is explicitly a sampled full-frame estimate.
- DDGI detailed atomic helpers default off and are enabled only for Debug or
  DetailedInvestigation shader builds.
- Shipping shader builds run `VerifyProductionDiagnosticAtomics.ps1`, which
  parses the emitted SPIR-V instruction stream and rejects `OpAtomicIAdd` in all
  production forward fragments and Simple-DDGI compute modules.

### Phase 3: forward gather multiplicity and shader pressure

Implemented:

- Every sampled Simple-DDGI fragment is classified as one gather, two gathers,
  or recovery gather.
- Second gathers have mutually exclusive reason counters: ring transition,
  missing/invalid primary support, recovery, coverage edge, primary ownership
  below threshold, and debug-only request.
- Reports include classified population and one/second/recovery percentages.
- Primary ownership now has an explicit early-out threshold so a complete
  primary gather does not automatically execute a second volume gather.
- Legacy fast-gather state is exported as an explicit status: not applicable to
  Simple-DDGI, disabled, readback unavailable, no attempts, all rejected, or
  accepted. Zero attempts are no longer unexplained.
- The Nsight artifact contract prevents the pre-change 166-register result from
  being silently reused: its executable and shader hashes must match the new
  capture, and the opaque/decal stage metrics must be present.

### Phase 4: cached transport convergence and retirement

Implemented:

- Per-ring, incrementally maintained telemetry reports exact residual buckets,
  source-epoch buckets, solver-generation buckets, and mutually exclusive
  pending reasons.
- It reports residual-qualified probes that are not converged; participating,
  source-ready/stale, pending, inactive, and converged populations; scheduled
  probes/rays; dispatch/useful/no-op lanes; and work-weighted per-ring transport
  and blend timing estimates.
- Solver-completion latency is measured from source refresh through the required
  solver generation and reported per ring as sample count, P50, P95, and exact
  maximum. Histograms are fixed-size and render-thread allocation-free.
- Converged probes retire from the active set. Source, geometry, relocation,
  lighting, age, maintenance, cache-repair, and explicit reset paths requeue the
  appropriate probes.
- Global convergence and source-repair populations are distinct. Stable probes
  preserve compatible fixed-point evidence across source-cache reuse.
- Transport and blend operate on compact active update batches. No-op lane
  counts remain visible so an incomplete compaction cannot masquerade as
  convergence.
- A routine source refresh now validates the refreshed source before waking
  neighbors. Propagation occurs only for invalid results or a material residual
  change, with a two-times-convergence-threshold wake hysteresis. Explicit
  invalidation paths remain fail-closed and are not suppressed by that routine
  hysteresis.

### Phase 5: decal recheck

Implemented:

- Named variants support all decals disabled, decal DDGI disabled, decal shadow
  reception disabled, and one isolated decal material index.
- Decal shadow and DDGI reception are independent settings and independent
  forward-shader flags. Shadow reception persists in settings schema version 8;
  material isolation remains a capture-only override.
- Sparse decal attribution reports estimated fragment invocation, back-face
  kills, alpha/coverage kills, surviving fragments, DDGI gathers, and shadow
  evaluations, with stride and weight.
- Geometry decal rejection occurs before lighting where the material/back-face/
  alpha contract allows it. DDGI and shadow work is recorded only for surviving,
  eligible decal fragments.

The four-decal sub-1 ms exit gate remains a hardware measurement. A larger decal
pipeline redesign remains conditional on that controlled result, as required by
the revised plan.

### Phase 6: observability

Implemented:

- Direct DDGI waits are included in `RuntimeDeviceWaitIdleCount` and stall time.
- Forward shadow receiver telemetry is named `Capacity`; the obsolete `Count`
  property remains only as a compatibility alias and is excluded from the
  semantic manifest.
- Snapshot counter semantics are generated recursively for nested diagnostics
  and list records. Numeric values are labeled exact, sampled estimate,
  capacity, configured budget, emitted work, or unavailable.
- Capture metadata contains executable hash, commit, dirty-worktree state,
  shader-bundle hash, settings schema, scene asset/state hashes, full camera and
  projection hashes, DDGI generation/warmup/convergence state, and GPU timestamp
  period.
- The default executable identity is a deterministic manifest hash of the
  process apphost and every application-local `Njulf*.dll`, including filenames
  and bounded contents. Managed implementation changes therefore invalidate
  benchmark identity even when the native apphost bytes do not change.
- Pass sum and unexplained GPU time are exported as full distributions.
- Zero-gate quality metrics normalize finite values at or below `1e-12` to zero,
  so the approximately `1.65e-16` emissive remainder is no longer a false
  over-budget result. Non-finite input still fails closed.

### Phase 7: memory headroom

Implemented:

- The memory audit separates canonical atlas, sampled mirror, transport,
  readback, scratch, retired, disabled-feature retention, and duplicate-mirror
  ownership. It reports tracked budget/headroom and concrete audit findings.
- V1 allocates only graph-safe 16-byte transport placeholders instead of
  probe-sized buffers that it never reads. V2 retains concrete transport
  storage.
- One authoritative readback-size calculation is shared by planning,
  allocation, and transition validation, removing capacity oscillation.
- Disabling Simple-DDGI reconciles resources to graph-safe capacity instead of
  retaining the active field. Retired generations and bytes remain visible.
- Unbounded acceleration-structure residency avoids unnecessary candidate
  selection/sort scratch work.

The below-80% tracked-memory gate remains a target-GPU capture gate. No packing
or streaming change was made without a timing result, because the plan forbids
trading residency for new shader traffic or stalls.

### Phase 8: optional work

Async compute, AO, meshlet reconstruction, additional culling, and ray-query
changes remain deferred. This matches the revised plan: they become eligible
only after the primary CPU, forward, convergence, decal, and memory gates have
measured results.

## 3. Target-GPU evidence captured in this workspace

### 3.1 Controlled ProductionTiming runs

The current-binary evidence is:

- `.tmp/nsight-ddgi/final-v13-a.json` and `.pfm`
- `.tmp/nsight-ddgi/final-v13-b.json` and `.pfm`
- `.tmp/nsight-ddgi/final-v13-c.json` and `.pfm`
- `.tmp/nsight-ddgi/final-v13-detailed.json` and `.pfm`

All three production runs use ShippingPerformance, validation off, 720 warmup
frames, 600 measurement frames, fixed 60 Hz simulation, adaptive budgeting off,
and async compute off. They match these identities:

- Pair: `sponza-right-wall-20260803-final-v13`, variant `baseline`.
- Executable bundle:
  `sha256:448f9a12ef48a9cc44cb64758ab4dd10f6a8b51fde8deb54aed0416a272d5927`.
- Comparable identity:
  `sha256:ae97e45750a5dec298e80ee3c54081d2c87f6c9a925021d62d08d0203d0ffbe6`.
- Full identity:
  `sha256:8966efc4f11f4745b533cfbc1c97eb6f950c26e07a254fec85baf439108e141e`.
- GPU: NVIDIA GeForce RTX 3060 Laptop GPU; driver `610.248.0`.

Production P95 results are:

| Metric | Run A | Run B | Run C | Contract/result |
|---|---:|---:|---:|---|
| Renderer CPU | 3.775 ms | 3.787 ms | 3.667 ms | Pass, at or below 6 ms |
| GPU frame | 26.253 ms | 26.509 ms | 26.308 ms | Fail, above 10 ms |
| Opaque forward | 19.616 ms | 19.866 ms | 19.541 ms | Fail assigned sub-budget |
| Transparent/decal pass | 1.762 ms | 1.780 ms | 1.764 ms | Fail provisional 1 ms gate |
| GI CPU scheduling/upload | 2.026 ms | 2.002 ms | 2.014 ms | Fail, above 0.25 ms |
| GI GPU | 1.394 ms | 1.381 ms | 1.329 ms | Pass, at or below 2.5 ms |
| Simple-DDGI Upload | 1.760 ms | 1.731 ms | 1.732 ms | Pass intermediate 2 ms gate |
| Stable Capacity | 0.001 ms | 0.001 ms | 0.001 ms | Pass 0.1 ms gate |
| Transport plus blend | 0.843 ms | 0.868 ms | 0.792 ms | Pass 2.25 ms gate |

Run A attributes Upload P95 as 0.612 ms scheduler refresh, 1.116 ms queue
build, and 0.197 ms readback. Queue and scheduler CPU work therefore remain the
dominant reason the final 0.25 ms GI CPU gate fails. Its independent GPU pass
sum is 26.254 ms versus a 26.253 ms GPU frame; exported unexplained time is
-0.001 ms, within the 20 microsecond timestamp-query allowance.

The stable Capacity frame is a true no-op: `StableKeyHit=true`, with zero buffer
size lookups, transitions, descriptor registrations, retired-resource
destructions, and device-idle waits. Required live DDGI bytes are 177,519,440.
No full scheduler rebuild occurs in the measurement window.

Settled transport reports 10,164 converged probes, 542 pending probes, 480
routine-propagation probes, no no-op lanes, and 99.51% of qualified probes
converged or waiting only for scheduled refresh. The final frame updates 570
probes. Stationary dirty-event latency fields are unavailable because no dirty
event occurred during measurement; zero samples are not accepted as proof of
the one-frame dirty-update gate.

### 3.2 Gather attribution and image comparison

The matching DetailedInvestigation evidence changes the classified second-
gather fraction from 74.97% in `final-v4-detailed.json` to 19.94% in
`final-v13-detailed.json`, a 73.4% relative reduction. The final detailed run
classifies 2,032,512 one-gather pixels, 496,185 two-gather pixels, and 10,193
recovery pixels. All 506,378 second gathers are accounted for by 63,132 ring
transitions, 10,193 recovery gathers, 6 coverage edges, and 433,047 ownership
fallbacks; missing-support and debug-only reasons are zero. Fast-gather status
is explicitly `not-applicable:simple-ddgi-uses-structured-volume-gather`.

The detailed candidate passes the local linear-HDR threshold with relative RMSE
0.05951. Production A/B use the local reference
`.tmp/nsight-ddgi/local-settled-reference-20260803.pfm`, SHA-256
`c9d6383378326f731659665970b715ccfb360c6ca7925c68f924f817bee849b5`,
and report relative RMSE 0.05703 against a 0.12 threshold. This is automated
regression evidence only: the reference is local and has not received art
approval or human seam/motion review.

### 3.3 Repeatability, memory, and acceleration structures

The current-binary A/B comparison fails only DepthPrePass (7.06%) and
SimpleDdgiTransportPass (6.31%). A/C fails DepthPrePass (6.51%) and transport
(8.01%). B/C fails only transport (13.81%). CPU frame, GPU frame, opaque
forward, transparent/decal, and all other material passes meet the 5% rule, but
the strict repeatability exit criterion is still failed. An earlier v12 A/C
pair passes all 23 metrics at 5%; it predates executable bundle hashing and is
retained as directional evidence, not current-binary signoff.

Tracked memory is 1,687,367,867 of 2,147,483,648 bytes: 78.57% utilization and
21.43% headroom, passing the 20% headroom gate. Settled BLAS residency is
152,138,368 bytes, and the capture reports 255,060,736 bytes of compacted
resident storage saved with no pending query, overflow, readback failure, or
retired AS bytes.

The production gate fails only `budget-metrics-within-gate`, naming GI CPU
scheduling/upload and GPU frame. That narrow gate result does not override the
separate forward, decal, repeatability, dirty-event, Nsight, and visual-review
exit criteria above.

## 4. Production capture procedure

Build the exact production executable:

```powershell
dotnet build NjulfHelloGame\NjulfHelloGame.csproj -c ShippingPerformance --no-restore
```

Run two repeats using one approved PFM reference and one stable pair ID. Keep
each report and candidate image at a distinct path:

```powershell
.\NjulfHelloGame\bin\ShippingPerformance\net10.0\NjulfHelloGame.exe `
  --benchmark `
  --performance-scenario gi-sponza-right-wall-stationary `
  --quality-preset ddgi-high `
  --validation off `
  --benchmark-warmup-frames 720 `
  --benchmark-measure-frames 120 `
  --benchmark-pair-id sponza-right-wall-20260802 `
  --benchmark-variant baseline `
  --benchmark-report .tmp\nsight-ddgi\baseline-a.json `
  --benchmark-hdr-reference .tmp\nsight-ddgi\approved-reference.pfm `
  --benchmark-hdr-candidate .tmp\nsight-ddgi\baseline-a.pfm `
  --benchmark-shader-profile .tmp\nsight-ddgi\nsight-shader-profile.json `
  --benchmark-require-production `
  --benchmark-require-shader-profile
```

Compare repeat A with repeat B:

```powershell
.\NjulfHelloGame\bin\ShippingPerformance\net10.0\NjulfHelloGame.exe `
  --compare-benchmark-pair `
  .tmp\nsight-ddgi\baseline-a.json `
  .tmp\nsight-ddgi\baseline-b.json `
  --benchmark-pair-report .tmp\nsight-ddgi\repeat-comparison.json
```

For a controlled feature delta, retain the same pair ID and change only
`--benchmark-variant`. Supported values are:

- `baseline`
- `far-field-gated`
- `far-field-forced-old`
- `decals-disabled`
- `decal-ddgi-disabled`
- `decal-shadows-disabled`
- `decal-material:<non-negative material index>`

Compare the result with `--benchmark-pair-ab`. A/B mode validates normalized
identity but does not apply the 5% repeatability gate to the intentional delta.

## 5. Nsight shader-profile artifact

Export current opaque and geometry-decal fragment statistics from NVIDIA Nsight
Graphics, then transcribe the measured values into this strict JSON contract:

```json
{
  "Schema": "njulf-nsight-shader-profile-v1",
  "Tool": "NVIDIA Nsight Graphics",
  "ToolVersion": "replace-with-captured-version",
  "GpuDeviceName": "exact benchmark CaptureGpuDeviceName",
  "DriverVersion": "exact benchmark CaptureGpuDriverVersion",
  "ExecutableHash": "exact benchmark CaptureRun.ExecutableHash",
  "ShaderBundleHash": "exact benchmark CaptureRun.ShaderBundleHash",
  "Stages": [
    {
      "Pass": "ForwardPlusPass",
      "Shader": "forward_opaque_ddgi.frag",
      "Variant": "baseline",
      "LiveRegisters": 1,
      "SpillBytes": 0,
      "LocalMemoryBytes": 0,
      "OccupancyPercent": 1.0,
      "ThreadCoherencePercent": 1.0,
      "TextureLoadCount": 0,
      "StorageLoadCount": 0,
      "InstructionCount": 1,
      "SampledDependencyReasons": ["replace with Nsight result or 'none observed'"]
    },
    {
      "Pass": "TransparentPasses",
      "Shader": "forward.frag",
      "Variant": "geometry-decal",
      "LiveRegisters": 1,
      "SpillBytes": 0,
      "LocalMemoryBytes": 0,
      "OccupancyPercent": 1.0,
      "ThreadCoherencePercent": 1.0,
      "TextureLoadCount": 0,
      "StorageLoadCount": 0,
      "InstructionCount": 1,
      "SampledDependencyReasons": ["replace with Nsight result or 'none observed'"]
    }
  ]
}
```

The values `1` in the template are placeholders, not accepted performance
evidence. Replace every metric with the new capture. Divergence can be reported
as `100 - ThreadCoherencePercent`.

## 6. Detailed investigation procedure

Build `DetailedInvestigation` and use the same scenario, camera, resolution,
quality, and authored inputs. Do not pass `--benchmark-require-production`:

```powershell
dotnet build NjulfHelloGame\NjulfHelloGame.csproj -c DetailedInvestigation --no-restore
```

The resulting report is intentionally classified as DetailedInvestigation and
is timing-ineligible. Use its exact view, projection, scene asset/state, and GI
hash fields to verify it describes the same workload, then use its sampled
gather, far-field, decal, capacity, and convergence counters only for
attribution.

For the capacity hypothesis, also collect a CPU sampling/timeline trace around
`SimpleDdgiVolumeManager.Upload`. The capacity sub-timers distinguish plan,
lookup/lock, transition, wait, sampled-atlas, descriptor, and retirement cost;
the external trace is still needed to prove driver wait versus buffer-manager
contention.

## 7. Verification completed in this workspace

- Full ShippingPerformance solution build: succeeded with zero warnings and
  zero errors.
- Production SPIR-V audit: 20 forward/Simple-DDGI modules checked; zero
  `OpAtomicIAdd` instructions found.
- Focused final performance/contract verification: 116 passed, zero failed.
- Full Release test assembly: 2,114 passed and one failed out of 2,115. The
  failure is the unrelated timing assertion in
  `CookedAssetTests.ModelCooker_CooksAndIncrementallyReloadsAnimatedGlbPackage`:
  cooked load was 98.34 ms against an 81.12 ms threshold. An isolated rerun
  reproduced the timing miss at 133.60 ms against 124.27 ms. No DDGI or renderer
  assertion failed, and the unrelated asset-performance threshold was not
  weakened as part of this work.

The hardware-only test remains intentionally skipped by the ordinary unit-test
run and must be executed on the target Vulkan/NVIDIA environment.

## 8. Remaining acceptance evidence

The following acceptance work remains:

- Obtain a strict current-binary repeat pair with every material pass at or
  above 0.50 ms within 5%. The three v13 runs do not meet this criterion.
- Capture new Nsight opaque/decal live-register, spill, local-memory, occupancy,
  coherence, load, instruction, and dependency results. Nsight Graphics 2026.3
  CLI attempts did not produce an export after the game exited and were stopped;
  no profile values are inferred or fabricated.
- Create an approved HDR reference and complete human review for darkness,
  seams, ring transitions, slow motion, fast recenter, and forced fallback.
- Execute and record stationary, recenter, light, geometry, reload, tier-change,
  clear, and device-loss matrix results, including a real dirty event proving
  dirty first update at or below one frame and transient-memory behavior.
- Reduce GI CPU P95 from approximately 2.0 ms to 0.25 ms, the GPU frame from
  approximately 26.3 ms to 10 ms, and opaque forward from approximately 19.7 ms
  to its assigned sub-budget.
- Capture the controlled decal variants and either bring the four-decal pass
  below 1 ms or obtain a quality-approved replacement budget.
- Resolve the unrelated cooked-asset timing-test failure so the entire Release
  assembly is green, without relaxing it as part of this performance pass.

Implementation is complete and the captured passing intermediate gates are
real. Until the remaining artifacts and failed gates pass, the performance pass
must not be described as fully hardware- or art-qualified.
