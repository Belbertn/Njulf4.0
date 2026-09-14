# Separate optical denoising — 2026-09-14

Implemented bounded transparent-layer export, temporal reconstruction, two spatial
passes, and optical-only correction for sorted transparency and weighted OIT.
The forward shader retains raw blending; layer overflow retains the raw pixel.
See [design and controls](../../rendering/optical-denoising.md) and
[machine-readable evidence](20260914-optical-denoising.json).

Source: `cc35bcc6fc69854dd96e316cd6e14ec69b139e8e`, dirty working tree including
preexisting optical-showcase and DDGI work. Device: NVIDIA GeForce RTX 3060 Laptop
GPU, driver 610.248.0, Vulkan 1.4.341. Workload: MaterialShowcase, 1600×900,
HybridRayQuery reflections, thick ray transmission with RGB dispersion, reflection
budget 1,048,576 and transmission budget 2,097,152. Baseline capture freezes scene
time and camera; filtering advances optical sample seeds. Memory allocation is
536,870,464 bytes within the default 512 MiB budget, deferred until transparency.

## Matched diagnostic captures

Caustics were disabled for the energy comparison because independently stochastic
caustic lighting changed the nested-glass region in the initial full-scene pair.
These are 120-frame capture windows with validation enabled and GI still warming;
they are **not a steady-state benchmark**. CPU tails include initialization activity.

| Measurement | Sorted bypass | Sorted filtered | Weighted filtered (caustics on) |
|---|---:|---:|---:|
| Optical passes, GPU ms | 0.149 | 27.467 | 27.371 |
| Reported total GPU ms | 44.400 | 72.143 | 70.340 |
| CPU frame average ms | 41.51 | 68.42 | 70.50 |
| CPU frame p95 ms | 55.85 | 77.37 | 79.70 |
| CPU frame maximum ms | 192.19 | 205.73 | 266.40 |

Linear-HDR ROI measurements, filtered versus bypass: blue glass mean +0.34%,
nested glass +0.11%, water +0.02%; high-frequency RMS decreased 99.54%, 81.30%,
and 81.92%, respectively. The opaque control mean changed approximately -0.004%.
All captured HDR values were finite and nonnegative. These spatial measurements
include real detail, so they supplement the GPU behavioral tests rather than prove
temporal convergence or animation quality.

Both modes captured 401,826 records. 8,008 pixels (0.56% of the image) exceeded
the layer limit and retained raw rendering; the record pool was not exhausted.
Sorted filtered history reused 599,583 lobe observations. Dense overlap can retain
visible grain. Long animated-water sequences remain unqualified.

## Validation and decisions

- Final normal Development build: zero errors, 47 warnings; 39 focused tests
  passed, none skipped, including production-kernel Vulkan tests. The broader
  selection passed 326/329; its three preexisting contract-test failures are named
  in the JSON record.
- All 542 generated SPIR-V artifacts passed Vulkan 1.3 validation during the full
  build investigation; the final three optical modules were validated again.
- GPU tests cover energy/noise, black versus absent observations, shuffled layers,
  material/object/normal/facing boundaries, motion, disocclusion, overflow,
  stochastic transmission distances, confidence, and sorted/OIT composition.
- Sorted and weighted native captures recorded no Vulkan errors. Initial bypass
  and filtered runs also completed health reports with zero validation warnings.
  Later diagnostic runs were stopped after fenced image/telemetry readback because
  unrelated background native compilation delayed shutdown; they lack final health
  reports. The final lazy-allocation change was build/test verified, not recaptured.
- Skipping zero-weight lobes and bypass reconstruction reduced measured optical
  cost from about 37.7 ms to 27.5 ms. Symmetric spatial exchanges fixed bright-peak
  energy loss; an isolated-bright-sample GPU test preserves total energy. A later
  temporal equality shortcut gave no reliable speedup and was reverted.
- The original ray fragment control also required 329.8 seconds to reach full
  quality with the current mesh/layout. The candidate completed native compilation
  in roughly 260 seconds; these are unmatched diagnostic observations, not a
  startup speedup claim. Early stopped export/spirv-opt trials were inconclusive;
  no spirv-opt workaround was integrated. Cold ray-pipeline compilation remains slow.
- GPU total telemetry now includes the four optical passes. Earlier capture totals
  omitted them and must not be used for end-to-end comparisons.

Decision: retain the separate pass and on/off/bypass controls. Quality and layer
isolation are demonstrated, but the roughly 27 ms GPU cost on this device is a
material limitation; this is not a 60 FPS performance qualification.

## Reproduction and retention

Build with `dotnet build Njulf.sln -c Development`. Run focused tests with:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --filter "FullyQualifiedName~OpticalDenoising|FullyQualifiedName~BindlessIndex|FullyQualifiedName~ReflectionProbeFrameTelemetry"
```

Run the built sample for each mode, using distinct output directories:

```powershell
NjulfHelloGame.exe --scene material-showcase --smoke-frames 400 --baseline-snapshot-dir <output> --optical-denoising on --gi-caustic-mode Off --transparency-mode sorted --validation standard --max-fps 60 --vsync off --startup-log <output>/startup.json --health-report <output>/health.json
```

Compare `on` with `bypass`; use `weighted` for OIT. Diagnostic runs set
`NJULF_PIPELINE_BINARY_AUTO_CAPTURE=0` and `NJULF_PIPELINE_COMPILE_WORKERS=1`.
TEMP, TMP, pipeline/environment/DDGI caches and captures stayed under
`artifacts/optical-denoising-20260914` on D:. Raw captures and diagnostic scripts
remain available only for the short retention window. The approved pruning script
was inspected and applied after sample processes stopped; its compact cleanup
record is stored alongside this milestone: 5,982 obsolete files (25.51 GiB) removed,
zero failures. Re-run it after two days to expire the
remaining recent payloads; retain this report and JSON.
