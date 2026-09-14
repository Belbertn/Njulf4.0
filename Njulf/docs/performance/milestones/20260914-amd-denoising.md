# AMD denoising integration — 2026-09-14

Implemented AMD opaque-reflection and per-area-light visibility denoising, plus
compact layered glass filtering. See [controls and contracts](../../rendering/amd-denoising.md)
and [compact measurements](20260914-amd-denoising.json).

Source: `e9cc5c6443e3fe4a4ad07d516c18a7d2439b216c`, dirty implementation working
tree. GPU: NVIDIA GeForce RTX 3060 Laptop, driver 610.248.0, Vulkan 1.4.341.
Changed source and vendor files are recorded in the
[SHA-256 manifest](20260914-amd-denoising-source.json).
Development build, VSync off, caustics off. AMD SDK 1.1.4 / Denoiser 1.3 is
pinned to `c6efa6bf7f2027b3ec94f28578bb5965eabb9e55`.

## Decision

Keep all new paths opt-in. They improve selected noisy signals, but the initial
combined denoising target of 3 ms mean / 4 ms p95 at 1600×900 is not met or
qualified. Reflection filtering remains a substantial cost. Compact glass is
cheaper than the earlier full-resolution implementation but still expensive.
No whole-frame speedup claim is made.

## Measurements

The final area-light benchmark uses **1920×1080**, 120 warmup frames and 240
measurement frames, validation off. No build ran concurrently. Pass timings
include the sparse reflection preparation step in reflection temporal time.

| Final area-light workload | Mean ms | p95 ms | p99 ms |
|---|---:|---:|---:|
| CPU frame | 2.146 | 2.717 | 5.318 |
| GPU frame | 25.225 | 26.744 | 41.050 |
| AMD reflection preparation/filtering | 5.40 | 5.55 | 5.57 |
| AMD area visibility filtering | 2.15 | 2.40 | 2.44 |
| Area visibility rays | 0.85 | 0.97 | 0.99 |

The benchmark reports failure against its existing **10 ms whole-GPU-frame
budget**; this is not a Vulkan validation failure. Its largest reported p95 pass
was ambient occlusion. AMD area histories allocate 100,120,512 bytes and AMD
reflection guides allocate 265,940,224 bytes at this resolution.

Earlier existing/AMD-shadow-only runs have GPU mean/p95/p99 of
23.228/26.840/26.983 ms and 21.863/28.268/28.505 ms respectively. They overlapped
CPU shader compilation and used an earlier dispatch implementation: retain them
as diagnostic baselines, not matched performance evidence. FP32 reconstructed
reflections measured 5.468 ms mean / 5.611 ms p95; the FP16 candidate showed no
material improvement on this device.

Glass snapshots use **1600×900**, sorted transparency, 120 full-quality frames,
standard validation and a fixed camera. They are diagnostic captures with GI
warming, not steady-state timings. Compact filtering measured 8.735 ms in the
first capture and 10.105 ms in the final capture (temporal + spatial + correction;
clear adds 0.170–0.195 ms). The earlier full-resolution optical milestone recorded
27.467 ms under its diagnostic workload. Do not treat this as a controlled speedup.
Compact optical allocation is 536,641,600 bytes within 512 MiB; the final snapshot
reports 298,460 captured fragments, 124,801 history reuses and 8,008 overflow
pixels. Overflow retains the original signal and remains a quality limitation.

The matched bypass/compact water ROI high-frequency RMS is 0.49882 / 0.02901
(94.2% reduction); mean luminance is 1.01510 / 1.00101 (−1.39%). Blue-glass mean
changes +0.40%; nested-glass mean is effectively unchanged. All HDR values are
finite. These spatial metrics include real detail and are not a temporal-noise
oracle. Other sampled regions do not show uniform noise reduction.
Weighted OIT also exported a finite HDR capture and preserved the layered
composition visually; water RMS was 0.02822 and mean luminance 1.00766. Its cold
ray-transparent forward pipeline took several minutes to compile. This is a
composition check, not a controlled comparison between transparency algorithms.

## Validation and rejected candidates

* Normal Development sample build: zero errors/warnings. Final AMD/compact
  shader artifacts: 16 passed `spirv-val --target-env vulkan1.3`.
* 75 focused tests passed, none skipped, including GPU execution of production
  compact kernels: nearest-two selection from shuffled/eight-layer exports,
  constant-signal preservation, noise reduction and history reset/identity rejection.
* Final 400-frame area-light smoke test completed with standard Vulkan validation:
  health passed, zero validation warnings and zero errors. It ran while the
  weighted-transparency process compiled pipelines; its timings are not benchmark evidence.
* Standard-validation resize smoke passed at 1280×720, 1600×900 and 800×600,
  with zero warnings/errors. These are the smoke harness's early-frame window
  transitions; they are not a long-running history-resize stress test.
* Area-light captures visibly preserve scene reflections and remove noisy tube
  highlights/contact shadows. The earlier full-resolution AMD shadow candidate
  cost 3.902 ms in a 1600×900 snapshot and was replaced with half-resolution
  tracing/filtering plus depth-aware upsampling.
* Feeding skipped-ray analytic fallbacks directly to AMD produced bright,
  speckled metal. Rejected. The separate sparse-observation preparation pass
  restores the dark reflection signal and recognizable reflected lights.
* Glass standard-validation snapshots exported without reported VUIDs. Capture
  processes were manually stopped after extended shutdown waits; a Rider paused
  stack showed `PipelineCompilationScheduler.WaitForAll` at line 163. Managed
  frame values were unavailable, so this does not establish the underlying
  shutdown cause. Do not count these runs as completed health-report passes.
* Static images and kernel reset behavior are covered. Camera/occluder motion,
  disocclusion trails and broad hardware qualification remain open.

## Reproduce

Build normally (do not use `UseExisting` shader artifacts):

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

Run `NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe` with:

```text
--scene AnalyticalAreaLights --reflection-denoiser amd --area-denoising on --optical-denoising off --gi-caustic-mode Off --benchmark=true --benchmark-warmup-frames 120 --benchmark-measure-frames 240 --benchmark-report artifacts/amd-denoising/benchmark.json --validation off --vsync off --max-fps 0
```

For glass use `--scene MaterialShowcase --optical-denoising compact` and
`--transparency-mode sorted` or `weighted`. Compare against `--optical-denoising
bypass`. Capture scripts/raw data live under ignored
`artifacts/amd-denoising-20260914` for the active investigation only. Artifact
pruning inspection found 0.16 GiB eligible older payloads and about 250 GiB free;
no storage pressure justified deleting older reflection reference copies.
Retain this compact evidence when raw captures are pruned after the retention window.
