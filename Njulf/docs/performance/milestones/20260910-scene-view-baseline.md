# Scene/view implementation baseline — 2026-09-10

Source: `10440367` plus the pre-existing uncommitted production-pipeline ownership changes. No engine source edits from this implementation were applied before these runs. The original tracked diff, untracked source copies and source hashes are under `artifacts/scene-views-20260910/`. The reviewed plan and ownership checklist are documentation changes only at this point.

Hardware: Ryzen 5 5600H / NVIDIA GeForce RTX 3060 Laptop GPU; runtime reports Vulkan 1.4.341 and driver 610.248.0. Windows, .NET SDK 10.0.203. Workload: cooked Bistro, Normal scenario, DdgiHigh, 1920×1080, Release, validation off, GPU timestamps on, VSync off, 120 warmup and 480 measured frames.

## Rejected reference

The responsive-startup reference exported 480 CPU and GPU samples, but its loaded shader identity changed starting at measured frame 112. CaptureContract.Comparable was false. Receiver feedback also reported an incomplete pipeline bank and near-field GI lacked a valid witness. The health report's passed status and zero diagnostic errors are insufficient to accept this capture.

| Rejected run | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: |
| CPU | 9.105 | 11.142 | 14.721 |
| GPU | 28.094 | 28.540 | 28.739 |

These are rejected observations, not a performance baseline or a candidate improvement. Compact data: [rejected-responsive-summary.json](../../../artifacts/scene-views-20260910/rejected-responsive-summary.json). Full active-investigation report: `artifacts/scene-views-20260910/baseline-final/reference/reference.json`.

The initial Release build compiled 242 uncached shader variants. A 900-second build timeout interrupted contract verification; the retry with a 2400-second limit passed verification and built with zero warnings/errors. Subsequent incremental builds took about three seconds. One capture was stopped after discovering the runtime cache defaults on C:. Existing caches were copied to D: and left untouched on C: because their original ownership is uncertain.

## Next measurement

Use the existing `blocking-active-scene` startup mode and `full-quality` readiness target for reference and candidate. This controls initialization rather than changing quality or omitting effects. Do not apply the scene/view implementation patches until the baseline attempt finishes and its evidence has been assessed.

```powershell
. ./artifacts/scene-views-20260910/environment.ps1
pwsh -NoProfile -File tools/perf-loop.ps1 -BaselineOnly -InitializeHdrReference -RepeatCount 1 -Configuration Release -Scene Bistro -Scenario Normal -WarmupFrames 120 -MeasureFrames 480 -BenchmarkTimeoutSeconds 2400 -RunDirectory artifacts/scene-views-20260910/baseline-blocking -HdrReferencePath artifacts/scene-views-20260910/reference-blocking.pfm
```

The environment file redirects TEMP/TMP, Vulkan pipeline caches, pipeline binaries, DDGI warm caches and environment maps under the D: workspace, and sets both startup controls. Preserve these inputs for the active comparison. Raw rejected captures and cache copies remain temporary investigation artifacts and must be pruned under the repository retention policy after the investigation completes.

## Accepted baseline

Blocking startup completed both the reference and timing run. CaptureContract reports Comparable=true and ProductionTiming=true with no mismatches, all 480 GPU samples valid, and HDR relative RMSE 0.0019609917 against the 0.005 limit. The standalone loaded-shader inventory assertion passed. The generic 16.67 ms performance target was not met; this capture establishes comparison data rather than claiming that target.

| Accepted baseline | p50 ms | p95 ms | p99 ms |
| --- | ---: | ---: | ---: |
| CPU | 8.051 | 9.939 | 14.486 |
| GPU | 21.918 | 22.177 | 22.344 |

Summary: [baseline-summary.json](../../../artifacts/scene-views-20260910/baseline-blocking/baseline-summary.json). Raw evidence remains in `baseline-blocking/` while implementation is active. D: had approximately 288.5 GiB free at the milestone. Candidate timing, steady allocation comparison and feature validation remain outstanding. Source implementation edits began only after this baseline completed.
