# Renderer Architecture Step 1 Baseline - 2026-06-20

Captured with `NjulfHelloGame` smoke runs using `--baseline-snapshot-dir Plans/Baselines/RendererArchitectureStep1-20260620` and `--gpu-timing`.

The current local sample path exports successfully after the first rendered frame. GPU timestamps are enabled, but the first exported frame reports `GpuTimingValid=0` with reason `GPU timing is enabled; waiting for a completed frame of timestamp results.` CPU pass timings, memory estimates, scene submission mode, meshlet counts, particle counts, and foliage counts are available in the snapshots.

## Snapshot Files

| Scenario | Snapshot |
| --- | --- |
| Normal Sponza/interior | `normal-sponza-interior/performance-20260620-031107.json` |
| Forest foliage | `forest-foliage/performance-20260620-031247.json` |

## Baseline Metrics

| Metric | Normal Sponza/interior | Forest foliage |
| --- | ---: | ---: |
| CPU total draw scene us | 285753 | 211427 |
| CPU depth record us | 2242 | 2356 |
| CPU Hi-Z record us | 11610 | 11632 |
| CPU light cull record us | 1789 | 2107 |
| CPU forward opaque record us | 2951 | 2988 |
| CPU transparent record us | 556 | 425 |
| CPU bloom extract record us | 433 | 519 |
| CPU fog record us | 1920 | 1957 |
| CPU composite record us | 1211 | 1323 |
| GPU timing valid | 0 | 0 |
| Render target bytes | 53878000 | 53878000 |
| Tracked GPU memory bytes | 1760360872 | 1728650792 |
| Scene submission mode | Cpu | Cpu |
| Scene CPU candidates | 108624 | 6 |
| Scene GPU emitted | 0 | 0 |
| Opaque meshlets | 108299 | 6 |
| Submitted CPU meshlets | 108624 | 6 |
| Rendered particles | 88 | 88 |
| Foliage patches | 18 | 34 |
| Foliage prototypes | 18 | 21 |
| Foliage clusters | 18 | 50 |
| Foliage visible clusters | 0 | 0 |
| Foliage meshlet draws | 0 | 0 |
| Foliage far impostors | 0 | 0 |

## Commands

```powershell
dotnet run --no-build --project NjulfHelloGame/NjulfHelloGame.csproj -- --smoke-mode startup --smoke-frames 1 --gpu-timing --baseline-snapshot-dir Plans/Baselines/RendererArchitectureStep1-20260620 --health-report Plans/Baselines/RendererArchitectureStep1-20260620/health-normal.json --startup-log Plans/Baselines/RendererArchitectureStep1-20260620/startup-normal.json
```

```powershell
dotnet run --no-build --project NjulfHelloGame/NjulfHelloGame.csproj -- --smoke-mode startup --smoke-frames 1 --performance-scenario forest-foliage --gpu-timing --baseline-snapshot-dir Plans/Baselines/RendererArchitectureStep1-20260620 --health-report Plans/Baselines/RendererArchitectureStep1-20260620/health-forest-foliage.json --startup-log Plans/Baselines/RendererArchitectureStep1-20260620/startup-forest-foliage.json
```
