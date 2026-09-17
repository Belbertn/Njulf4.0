# AA, AO, and texture quality repair — 2026-09-15

## Workload and source

- Base revision: `b92d1f0e7a4aa80f72a53052d2466291c5bed647`, with the user's uncommitted AA/AO/texture changes. Original patch: `artifacts/aa-fix-20260915/user-changes.patch`.
- Hardware: NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.62, 6144 MiB VRAM.
- Preserve native-resolution SMAA, SMAA Ultra, DdgiHigh bent-normal lighting, and the 1024px imported texture default. Explicit texture overrides remain authoritative.
- Sponza production default: DdgiHigh, SMAA High, half-resolution GTAO, AMD half-resolution reflections. The temporal capture adds 330 stationary frames before the unchanged horizontal/vertical routes (v3 contract, 1590 captured frames at 1600x900).

## Confirmed numerical regression

The original GTAO temporal shader rejects otherwise valid history when its age
reaches 32. On the next frame, its bent direction jumps back to the current raw
sample. The GPU regression dispatches the production shader with actual sampled
and storage images. Before: fails at age 32; after: passes ages 31, 32, 33, and
255, and still rejects invalid epochs and depth disocclusions. This establishes
the age-reset defect; it does not by itself establish that every reported
Sponza flash has the same cause.

Evidence: `artifacts/aa-fix-20260915/gtao-before.trx`, `gtao-after.trx`, and the
matching test logs. The temporary focused test host references the existing
renderer ABI but explicitly loads freshly compiled before/after production
GTAO SPIR-V; it does not substitute a CPU model for shader execution.

Reproduce after a normal build:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Release --filter FullyQualifiedName~GtaoTemporalGpuTests
```

For failure sensitivity, compile the saved original shader and supply its
absolute path through `NJULF_GTAO_TEST_SHADER` to the same test.

## Other corrections and validation

- AA push constants reuse the unused padding word: 120 bytes, predication at 100, jitter at 104/112. Compiled shader member offsets are compared with C# layout.
- Keep intermediate/post-effect color linear; SMAA alone encodes its edge samples. Neighborhood blending and presentation preserve a single output encoding.
- Restore upstream SMAA crossing offsets, corner sample centers, diagonal endpoint handling, linear search-LUT sampling, and predication threshold adjustment. An independent upstream GPU oracle covers corners and diagonals. Its linear-light tolerance includes one RGBA8 blend-weight step and screenshot quantization; it does not allow a broad sRGB error tolerance.
- Restore inverse cascade-texel PCF scaling and the normal-bias cap already used by the screen-space CSM resolve.
- Fresh direct compilation and `spirv-val` pass for SMAA's three stages, TAA, and GTAO temporal.

## Sponza pulse: reflection history feedback

The reported stationary curtain pulse survived the AA/AO corrections and fixed
exposure, but disappeared with reflections disabled. Classification reused an
analytic result at history age 8, publishing a valid resolution-skip marker.
Temporal consumers interpreted that marker as an unobserved ray, periodically
restarting accumulation. AMD's reduced-resolution path additionally fed an
already filtered estimate through denoising again.

Correct the shared metadata interpretation, preserve valid cached history, and
keep DDGI receiving observations: its radiance changes without a topology
generation change. Crucially, **disable classified raw-input reuse while AMD
denoising is active**. AMD's normal temporal accumulation remains enabled. Its
filtered history cannot safely substitute for a separate analytic-source cache.

Rejected candidates are retained in the compact comparison evidence:

- `sponza-after`: AA/AO fixes alone did not resolve the reflection pulse.
- `sponza-fixed-exposure`: exposure was not the cause.
- `sponza-reflection-fix`: fixing only the existing denoiser missed the default AMD path.
- `sponza-amd-fix`: stable but froze unconverged DDGI into visible blocks.
- `sponza-final`: excluding DDGI still froze other filtered reconstruction errors into bright patches. Despite good temporal metrics, reject its images.
- `sponza-fresh-observations`: fresh AMD inputs remove those patches and retain the pulse reduction.

For 329 stationary image pairs, p95 mean absolute RGB change (8-bit code units):

| Region | Original dirty baseline | Fresh observations | Reduction |
| --- | ---: | ---: | ---: |
| Whole frame | 0.283110 | 0.007403 | 97.4% |
| Left curtain gold trim | 0.573453 | 0.008040 | 98.6% |
| Right curtain gold trim | 0.233171 | 0.006223 | 97.3% |
| Floor | 0.528450 | 0.000602 | 99.9% |
| Arch | 0.581643 | 0.008905 | 98.5% |

These are local image-change measurements, not proof of all-scene temporal
correctness or a decal-specific diagnosis. The original baseline's automatic
moving-route analysis failed because its validator sampled world Z while the
capture drove world X. The saved cameras/images are intact; stationary samples
remain usable. The corrected analyzer now validates the actual world-X path.

## Regression coverage

- 55 focused CPU/ABI/preset/capture checks passed (`focused-final.trx`).
- 38 GPU and AMD integration checks passed, no skips (`gpu-amd-final.trx`): actual production GTAO, shader push layouts, presentation encoding, independent SMAA, and sparse reflection policy.
- Final normal production build passed with zero warnings/errors in 14m11s, including the complete DDGI specialization verification. All 562 embedded shader resources match the interim bundle used for the accepted capture (`production-resource-comparison.json`). Repeated the affected ABI/GPU checks against normal build output: 32 passed, zero skipped (`production-gpu-final.trx`). These overlap the checks above, not 32 additional distinct tests.
- Original shader layout fails all six AA layout cases; corrected shaders pass. Original GTAO fails at age 32; corrected shader preserves valid history and rejects disocclusions.
- Three broader reflection contract tests still fail obsolete source-string assertions (deferred pipeline methods and an expected count of reset writes). Their asserted code was not changed by this repair; 42 other cases in that fixture passed. See `reflection-contracts.trx`.
- Presentation tests exercised the preferred UNORM swapchain; an explicitly forced sRGB swapchain remains outside this local check.

## Reproduction

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Release --no-restore -p:NjulfShaderBuildMode=Compile
./NjulfHelloGame/bin/Release/net10.0/NjulfHelloGame.exe --sponza-temporal-capture-dir artifacts/sponza-temporal-check --validation off --vsync false --max-fps 0
```

Add `--sponza-temporal-variant reflections-disabled` (or
`auto-exposure-disabled`, `decals-disabled`, `ambient-occlusion-disabled`,
`bent-normals-disabled`, `anti-aliasing-disabled`) for a diagnostic comparison.
Variants are recorded in the manifest and do not implicitly enable benchmarks.

## Timing, quality, and decision

Keep the repair. The image defect and numerical/ABI regressions are corrected,
and the short cost comparisons show no material GPU regression. Quality settings
remain enabled. These are local paired observations, not a release qualification
or a claim of a statistically established speedup.

Cost workload differs from the temporal capture: 1920x1080, DdgiHigh, SMAA High,
full-resolution AO, AMD half-resolution reflections, 1024px texture limit,
StressUnlimited budget, validation off, VSync off, uncapped. One sequential
baseline/candidate pair per scene: 120 warmup frames, automatic convergence
settling, then 240 measured frames. All six reports have 240 valid GPU samples,
no settling timeout, comparable production timing, and matching settings within
each pair. Builds and image analysis finished before measurement. HDR readback
occurs after the measured window. Both variants use the same cache directories;
shader changes can require new pipelines during warmup.

GPU frame time in milliseconds (before → after):

| Scene | Mean | p95 | p99 |
| --- | ---: | ---: | ---: |
| Sponza | 31.731 → 31.963 | 33.818 → 33.546 | 34.319 → 34.860 |
| Bistro | 33.016 → 32.996 | 35.434 → 35.298 | 35.687 → 35.717 |
| Living Room | 37.760 → 37.342 | 39.611 → 39.001 | 40.248 → 39.427 |

Mean deltas: +0.232 ms (+0.73%), -0.020 ms (-0.06%), and -0.418 ms (-1.11%).
Sponza p99 increased 0.541 ms even though p95 decreased; do not describe this as
an across-the-board timing improvement. Single pairs provide no confidence
interval. The captured GPU distributions exceed the 16.667 ms 60-fps budget.

End-of-measurement memory snapshots are identical within each pair:

| Scene | Tracked GPU MiB | Driver-reported device-local MiB | Texture estimate MiB |
| --- | ---: | ---: | ---: |
| Sponza | 3023.25 | 3933.15 | 109.40 |
| Bistro | 3073.56 | 4005.15 | 301.50 |
| Living Room | 1762.94 | 2905.15 | 4.41 |

These are snapshots, **not whole-run/startup peak memory measurements**. CPU
mean/p95/p99, exact identities, settling counts, and memory components are in the
compact JSON. No six-GiB peak-memory sign-off is claimed.

Reproduce each side from its saved application directory (the original renderer
and shader DLLs were preserved before edits):

```powershell
$env:NJULF_MAX_IMPORTED_TEXTURE_SIZE = '1024'
./artifacts/aa-fix-20260915/candidate-app/NjulfHelloGame.exe --benchmark --benchmark-report artifacts/cost-sponza.json --benchmark-warmup-frames 120 --benchmark-measure-frames 240 --benchmark-max-settle-frames 4096 --benchmark-budget-profile stress --benchmark-hdr-candidate artifacts/cost-sponza.pfm --scene SponzaPlaza --performance-scenario Normal --quality-preset ddgi-high --validation off --gpu-timing --vsync false --max-fps 0
```

The exact six-run script, including shared cache directories, is
`artifacts/aa-fix-20260915/run-cost-check.ps1`. Substitute `Bistro` and
`LivingRoom` for the other scenes. Raw reports are in its `cost/` directory.

The accepted v3 temporal capture completed all 1590 frames and all three route
analyses. Reviewed the strongest-change sheets for world-X and vertical motion;
they do not show the frozen patches seen in the rejected candidates. This is a
targeted review, not an exhaustive perceptual oracle for every moving pixel.

Compact comparisons: [20260915-aa-ao-texture-stability.json](20260915-aa-ao-texture-stability.json).

Evidence is kept under `artifacts/aa-fix-20260915/` on D:. Pruned 29.28 GiB of
superseded PNGs after recording results, retaining frame 100 and the first
ranked-change sheet per route for rejected candidates. Keep the complete
original baseline and accepted capture as the current references. Roughly
196.7 GiB remains free. The standard artifact-pruning tool also inspected and
removed obsolete generated payloads; no source assets, tracked files, or
junction targets were removed. Bulky captures and isolated binaries are
disposable after two days; preserve this record and compact comparison evidence.
