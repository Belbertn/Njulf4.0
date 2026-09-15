# AMD denoising implementation optimization — 2026-09-14

Source: `9cc0c406779c4e818b6e8c30b0f22da1916f2ead` plus the uncommitted optimization changes. Vendor AMD kernels, feature defaults, ray budgets, resolution and filter-quality settings are unchanged. Final source hashes and compact measurements are in [the comparison JSON](20260914-amd-denoising-optimization.json).

## Workload and decision

NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0, Windows/Vulkan 1.4.341. Development build. Measurements use 1920×1080, VSync/validation off, 120 warmup and 240 measured frames after the existing pipeline-settling gate. Builds and GPU test runs did not overlap the reported benchmark windows. Baselines include the same added nested timestamp instrumentation.

Keep the optimization bundle: both scenes improve in repeated runs, with unchanged signal resolution and small static image differences. These are GPU frame-time improvements, not a claim about AMD hardware or a universal frame-rate gain.

| Scene / run | GPU mean ms | p95 ms | p99 ms |
|---|---:|---:|---:|
| Area lights baseline | 25.467 | 26.790 | 27.497 |
| Area lights optimized | 21.627 | 22.855 | 24.328 |
| Area lights repeat | 21.598 | 23.223 | 24.173 |
| Area lights final-source verification | 21.563 | 23.073 | 24.463 |
| Material showcase / compact glass baseline | 27.999 | 29.871 | 30.973 |
| Compact glass optimized | 24.396 | 27.042 | 28.654 |
| Compact glass repeat | 24.300 | 26.194 | 27.320 |

The first optimized runs reduce whole-frame GPU mean by **15.1%** and **12.9%**, respectively. Area-scene reflection filtering/preparation (the parent temporal pass) falls from 5.656 to 2.558 ms; area shadow denoising falls from 2.237 to 1.885 ms. Glass temporal/preparation falls from 2.298 to 1.845 ms, correction from 1.745 to 1.476 ms, and spatial filtering from 1.389 to 1.253 ms. Nested timestamps are diagnostic subdivisions and must not be added to those parent costs.

Private reflection allocation at 1080p falls from **265,940,224 to 191,031,424 bytes**: 74,908,800 bytes saved (28.2%). Glass and shadow allocation/quality budgets are unchanged.

## Implementation

- Resolve one opaque backend per frame. Create only its pipelines on demand. AMD failure selects Existing and invalidates history before dispatch. Existing, AMD and Off do not submit each other's filters. Compact and legacy glass are exclusive.
- Reuse the classifier's 8×8 receiver list for AMD's three filter stages in Adaptive mode. Preserve the screen path for Legacy mode. Full-screen preparation defines inactive-pixel halos; classifier-validated history carries copy their hit distance without repeating the preparation history test.
- Store reflection guide fields as contiguous planes; bank seven persistent fields and share nine scratch fields plus tile averages. Clear only fields not fully initialized by their producing pass. Existing history images have sampled aliases for hardware bilinear radiance/variance reads, while geometric/identity history checks remain explicit.
- Cache glass neighborhood matches, spatial geometry weights and composition inputs. Aggregate reuse counters per workgroup with unconditional entry/exit barriers. Compute-only storage descriptors use dispatch-uniform indexing.
- Batch shadow stages across lights; publish all packed light bytes once per native pixel. Private AMD barriers use compute/transfer scopes and the shadow consumer scope. Execution remains serialized on the graphics queue.
- Add nested denoising stage timings and actual dispatch counters, separate from frame totals. Keep timestamp nesting balanced if query capacity is exhausted.

## Validation

- Normal application build: zero warnings/errors. Sixteen AMD/compact SPIR-V modules pass `spirv-val --target-env vulkan1.3`.
- 76 focused tests pass, zero failures/skips, including GPU layered filtering and the shared workgroup counter on partially empty groups. Nested timings do not increase assembled frame totals. Two pre-existing nullable warnings remain in unrelated test sources.
- Area lights: 900-frame standard-validation smoke passes with zero warnings/errors. Recorded counts: `AmdShadow=16`, `AmdReflectionPrepare=1`, `AmdReflection=3`; no Existing filters.
- Existing reflection + legacy glass smoke passes with zero warnings/errors: `ExistingReflection=3`, `ExistingGlass=4`; no AMD/compact filters. Off passes with only `ReflectionCopy=1`.
- Compact weighted glass: resize operations to 1280×720, 1600×900 and 800×600 pass; a post-resize capture reports `CompactGlass=5` with no legacy glass dispatches. No Vulkan messages appeared. The process later stalled during shutdown and was stopped; this run has no final health report. Native optical export overflow remains possible (3,577 pixels in that capture), as before.
- A deterministic moving-camera ReflectionLod benchmark completes with the AMD chain active. This exercises planar/probe receivers and thin geometry; it is not a moving-glass or animated-light qualification.
- Inspected matched area/glass images show no new tile seams or lost reflection structure. Approximate tone-mapped full-image mean absolute differences are 0.000079 and 0.001320 on a normalized 0–1 scale. These are similarity checks, not ground-truth accuracy or temporal ghosting measurements.

## Intermediate candidates and limitations

The descriptor-only candidate was inconclusive: first area run regressed to 27.97 ms mean / 32.99 ms p95, while its repeat returned to 25.44 / 26.88, close to the 25.47 / 26.79 baseline. No standalone descriptor speedup is claimed. Uniform indexing remains part of the measured final memory-layout bundle. Initial glass caching alone reduced temporal filtering from approximately 1.37 to 0.99 ms. The intermediate memory/shadow bundle measured 22.05 / 23.50 ms in the area scene before tile dispatch and narrower dependencies.

A validation launch accidentally overlapped build output copying and was discarded; the resulting file-lock build was rerun successfully before retained validation. Other build locks occurred when prior captures lingered after export. No rejected/mixed-output run is used for the table.

A final CPU-only cleanup latches backend setup once per frame and avoids temporary pipeline/stage arrays on the hot path. The shader binaries and GPU command sequence are unchanged. Final-source verification again passed all 76 tests and measured 21.563 ms GPU mean / 23.073 ms p95 in the area scene, with the same exclusive dispatch counts.

Allocation-failure injection, live backend switching, FP32 runtime filtering, animated occluder/light changes and moving overlapping glass are not fully qualified by this investigation. Existing reset/fallback paths remain in place. Benchmark health reports exceed the existing 10 ms whole-frame budget in both baseline and optimized scenes; the optimization does not establish the earlier 3 ms combined-denoising target. Features remain opt-in. Glass remains the custom layered filter because AMD's opaque denoiser does not provide this refraction contract.

## Reproduce and retention

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
# Shared benchmark launcher retained as compact evidence under the ignored run root:
& artifacts/amd-denoising-20260914/run.ps1 -Name check-area -Benchmark
& artifacts/amd-denoising-20260914/run.ps1 -Name check-glass -Scene MaterialShowcase -Optical compact -Benchmark
& artifacts/amd-denoising-20260914/run.ps1 -Name check-resize -Scene MaterialShowcase -Optical compact -Transparency weighted -SmokeMode resize -Frames 400
```

The launcher puts TEMP, renderer caches and captures on D: in this workspace. Compact JSON/Markdown/scripts are retained. Raw images/captures remain only for the active review and at most two days after completion. `tools/prune-local-artifacts.ps1` inspected storage: 250.37 GiB free; only 0.16 GiB of older candidates belonged to another reflection investigation and were preserved pending reference ownership. No source assets or unrelated work were removed.

Contracts: [AMD denoiser](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/denoiser/), [AMD SSSR tile scheduling](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/stochastic-screen-space-reflections/), [Vulkan dependency scopes](https://docs.vulkan.org/samples/latest/samples/performance/pipeline_barriers/README.html). Implementation guidance is also summarized in [the renderer documentation](../../rendering/amd-denoising.md).
