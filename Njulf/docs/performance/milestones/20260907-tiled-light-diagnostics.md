# Opt-in CPU tile diagnostics — 2026-09-07

Implemented `Settings.Diagnostics.TiledLightDiagnosticsEnabled` (runtime-only,
default false). The enabled Light Tiles overlay also requests collection.
`TiledLightDiagnosticsValid` distinguishes uncollected statistics from valid
zeros. Controls, budgets, GPU light culling and full snapshot assembly retain
their existing cadence. No timer or cache was added.

## Verification

- Source: `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty working tree;
  pre-existing secondary-view changes were present and held fixed.
- Release renderer and sample-host builds passed. Managed builds reused existing
  shader artifacts with `-p:BuildProjectReferences=false
  -p:_GetChildProjectCopyToOutputDirectoryItems=false`; an initial full build was
  stopped during unrelated shader validation.
- `TiledLightDiagnosticsTests`: 10 passed, covering demand, overlapping lights,
  rejected lights, empty/invalid grids and stale-result clearing. The test build
  reported the existing `SampleDebugViewCycleTests.cs:31` CS0162 warning.
- On/off rendering smoke: both health reports passed with zero reported warnings
  and errors (Vulkan validation was off). HDR comparison passed at 1920x1080:
  relative RMSE `0.0000037531623993`, HDR-FLIP p95 `0`, RMSE limit `0.005`.

## Exploratory timings — inconclusive

RTX 3060 Laptop GPU, driver `610.248.0`, .NET SDK `10.0.203`, Release/High,
GlobalIlluminationTest scene, stationary camera, 1920x1080. The temporary ManyLights
fixture contained 128 non-shadowing point lights with range 100; all 8,160 tiles
contained all 128 lights. GI was disabled in both on/off smoke runs. Both used
30 configured warmup frames, 120 additional settling frames and 240 measured
frames with 240 valid GPU timing samples. Camera, scene state, GI settings and
loaded shader-bundle hashes matched.

| Metric (ms) | Collection enabled | Collection disabled |
| --- | ---: | ---: |
| CPU DrawScene mean | 2.138479 | 1.606304 |
| CPU DrawScene median | 1.962 | 1.348 |
| CPU DrawScene p95 | 2.471 | 2.445 |
| CPU DrawScene p99 | 5.491 | 7.419 |
| GPU frame mean | 59.658812 | 60.093867 |
| GPU frame p95 | 59.879 | 61.714 |
| GPU frame p99 | 60.231 | 62.386 |

CPU p95 improved only 0.026 ms (1.05%); CPU p99 and GPU p95 worsened. The harness
requires GI `SteadyState` even when GI is disabled, so both reports have
`SettlingWaitTimedOut=true` and `CaptureContract.Comparable=false`. These are
exploratory timings, not accepted performance evidence. CPU DrawScene excludes
the subsequent diagnostics assembly. No frame-rate improvement is claimed.
The opt-in behavior is retained; performance qualification remains inconclusive.

An earlier original/candidate pair with GI enabled measured CPU p95
3.143/2.075 ms, but also timed out settling; its initial HDR export failed due to
insufficient disk space. Those timings were excluded from the decision.

## Evidence and reproduction

Compact reports, logs, test results and the temporary fixture patch are under
`.perf-loop-runs/tile-diagnostics-20260907/`; the on/off smoke files are in its
`direct-light-smoke/` subdirectory. Those captures were initially written to C:
after the disk-space failure, then relocated under the repository's ignored
artifact root for normal two-day payload retention. The cleanup dry run was
reviewed; no cleanup was applied by this task.

For reproduction, apply `direct-light-smoke/temporary-comparison-fixture.patch`
temporarily. It sets the light fixture above and adds an environment-controlled
opt-in with GI disabled to the sample host. Build the Release renderer and host
using the focused build switches above. Run this command twice, setting
`NJULF_TILE_DIAGNOSTICS_COMPARE=1` then `0` in the process environment and using
separate report/image paths:

```powershell
& ./NjulfHelloGame/bin/Release/net10.0/NjulfHelloGame.exe --benchmark --benchmark-report <report.json> --health-report <health.json> --benchmark-warmup-frames 30 --benchmark-measure-frames 240 --benchmark-max-settle-frames 120 --benchmark-budget-profile stress --benchmark-pair-id tile-diagnostics-direct-lights --benchmark-variant baseline --benchmark-hdr-candidate <capture.pfm> --scene GlobalIlluminationTest --performance-scenario ManyLights --quality-preset high --validation off --gpu-timing
```

For the disabled run, also pass `--benchmark-hdr-reference <enabled.pfm>
--benchmark-hdr-max-relative-rmse 0.005`. Restore the two temporary fixture hunks
afterward. They are not part of the implementation.
