# Tail-certified DDGI qualification

Production enablement is authorized by one fail-closed command:

```powershell
dotnet run --project NjulfHelloGame -c ShippingPerformance --no-build -- `
  --tail-ddgi-qualification artifacts/tail-ddgi/manifest.json `
  --tail-ddgi-qualification-report artifacts/tail-ddgi/qualification.json
```

The command exits `0` only when every mathematical, correctness, ray-budget,
acceleration, memory, HDR, production-timing, scenario, repetition-identity, and
long-soak criterion passes. A completed build or unit-test run is not accepted
as runtime qualification. Missing reports, old reports without tail evidence,
non-ShippingPerformance captures, absent HDR comparisons, or pending
certificates fail the command.

The manifest schema is shown in
[tail-ddgi-qualification-manifest.example.json](tail-ddgi-qualification-manifest.example.json).
All relative paths are resolved from the manifest directory, and all thirteen
paths must be unique:

- three identity-locked `tail-jacobi` benchmark repetitions;
- three identity-locked `tail-accelerated` benchmark repetitions;
- accelerated scroll, teleport, source-change, relocation, high-albedo, and
  thin-wall benchmark reports;
- one passed ShippingPerformance long-soak report.

## Capture contract

Build the exact production configuration first:

```powershell
dotnet build Njulf.sln -c ShippingPerformance
```

Each benchmark capture must use validation off, DDGI High, GPU timing, at least
120 measurement frames, a durable HDR reference, one common pair ID, and the
appropriate tail variant. For example:

```powershell
dotnet run --project NjulfHelloGame -c ShippingPerformance --no-build -- `
  --benchmark `
  --benchmark-require-production `
  --benchmark-warmup-frames 30 `
  --benchmark-measure-frames 120 `
  --benchmark-budget-profile high `
  --benchmark-pair-id tail-ddgi-production-01 `
  --benchmark-variant tail-jacobi `
  --benchmark-report artifacts/tail-ddgi/jacobi-1.json `
  --benchmark-hdr-reference artifacts/tail-ddgi/reference.hdr `
  --benchmark-hdr-candidate artifacts/tail-ddgi/jacobi-1.hdr `
  --quality-preset ddgi-high `
  --performance-scenario GiSimpleDdgiFurnace `
  --gpu-timing `
  --validation off
```

Repeat with report/candidate suffixes `2` and `3`. Then use
`--benchmark-variant tail-accelerated` for the three paired accelerated runs.
Repeated captures must have the exact same full rendered-state identity and
must each pass their production timing budgets. Their P50/P95/P99 variation is
recorded as evidence; it is not replaced by an unrelated cross-run jitter
threshold.
The qualification command permits exactly one resolved-setting difference
between each pair:
`gi.simpleDdgi.transport.accelerationEnabled=0` versus `1`. Camera, scene,
device, driver, executable, commit, shader bundle, cache generation, quality,
and every other resolved GI setting must remain identical.

Capture scenario reports with `tail-accelerated` and these exact scenario
mappings:

| Manifest role | Performance scenario |
| --- | --- |
| `scroll` | `GiLocalVolumeStreaming` |
| `teleport` | `GiFastTraversalTeleport` |
| `sourceChange` | `GiMovingPointLight` |
| `relocation` | `GiMovingRigidObject` |
| `highAlbedo` | `GiSimpleDdgiFurnace` |
| `thinWall` | `GiThinWallLeakTest` |

The moving-light and moving-rigid benchmark scenarios apply 30 deterministic
simulation frames of motion, then hold their changed scene state fixed. This
keeps the source-change and relocation events real while providing a bounded
recovery window in which a complete current tail certificate can be audited.
Interactive runs of those scenarios remain continuously animated.

Run the soak with the same ShippingPerformance binary, device, driver, commit,
shader bundle, DDGI High settings, and accelerated defaults. It must be a
duration-owned run or at least 3,600 frames:

```powershell
dotnet run --project NjulfHelloGame -c ShippingPerformance --no-build -- `
  --tail-ddgi-long-soak `
  --long-run-report artifacts/tail-ddgi/long-soak.json `
  --long-run-warmup-frames 1200 `
  --long-run-sample-interval 15 `
  --smoke-frames 3600
```

`--tail-ddgi-long-soak` is fail-closed and owns the remaining identity:
1920x1080 hidden window, VSync off, `ShippingPerformance` validation off,
DDGI High, `GiSimpleDdgiFurnace`, HighSpec1440p60 budgets, GPU timing,
gpu-resident scheduling, disabled async adaptation, fixed 60 Hz simulation,
and the accelerated tail variant. Conflicting overrides are rejected before a
frame is rendered. The 1,200-frame warmup keeps late driver residency outside
the measured memory trend without increasing the one-MiB growth tolerance.

The soak uses `GpuDdgiUpdateMicroseconds` for the same tail-DDGI `GI GPU`
metric reported by benchmark evidence. Material compile/upload timing remains
recorded as informational stress telemetry because the deterministic soak
intentionally recompiles a material every 30 frames; those three material
pipeline metrics are explicitly listed as non-applicable in the soak report
and are not substituted for DDGI scheduling or GPU budgets. CPU frame, GPU
frame, GI CPU, and GI GPU timing retain every post-warmup sample, report
P50/P95/P99, and gate the production P95 threshold. Hard resource, memory,
counter, descriptor, and telemetry-coverage limits remain per-sample gates.
Managed-memory growth compares full-GC retained baselines at the warmup and
terminal boundaries while retaining intermediate observations for slope and
GC-sawtooth diagnostics.

## Evidence checked

For every benchmark, the report records GI, accelerated-solve, and audit GPU
P50/P95/P99; primary probes and rays; ray queries; shadow rays; cached solver
sweeps and cache-entry evaluations; audit chunks; final bound and tolerance;
participant/texel coverage; HDR comparison; and DDGI memory ownership.

The command additionally runs deterministic analytic systems for q=0.95 and
q=0.99 white enclosures, 2/20/128-probe chains, a reflected/transmitted thin
sheet, and a chromatic enclosure. The measured fixed-point error must be no
larger than the reported infinity-norm tail bound. Both this suite and the
runtime pair use the same tolerance, and runtime acceleration must reduce solve
epochs or convergence frames by at least 30 percent.
