# Foliage Phase 0 Baseline - 2026-06-18

Phase 0 captured two startup smoke baselines on the current renderer: the default Sponza sample and a synthetic foliage-like static-instance stress scenario. Raw health reports are checked in beside this note.

## Capture Commands

```powershell
dotnet run --project NjulfHelloGame -- --smoke-mode startup --smoke-frames 3 --health-report Plans\foliage-phase0-sponza-health.json
dotnet run --project NjulfHelloGame -- --performance-scenario foliage-like-static-instances --smoke-frames 3 --health-report Plans\foliage-phase0-static-foliage-health.json
```

## Reports

| Scenario | Report | Status | Smoke mode | Frames |
| --- | --- | --- | --- | --- |
| Sponza baseline | `Plans/foliage-phase0-sponza-health.json` | passed | Startup | 3 |
| Foliage-like static instances | `Plans/foliage-phase0-static-foliage-health.json` | passed | Startup | 3 |

## Baseline Diagnostics

| Metric | Sponza | Foliage-like static instances |
| --- | ---: | ---: |
| Visible objects | 464 | 464 |
| CPU object candidates | 464 | 4560 |
| Visible meshlets | 108624 | 112720 |
| CPU meshlet candidates | 108624 | 112720 |
| CPU submitted meshlets | 108624 | 112720 |
| Depth task invocations | 108299 | 112395 |
| Forward task invocations | 108299 | 112395 |
| Uploaded bytes | 121392 | 973712 |
| Instance upload bytes | 96512 | 948480 |
| Meshlet draw upload bytes | 0 | 0 |
| CPU scene build us | 487 | 1567 |
| CPU upload us | 192 | 274 |
| CPU total draw scene us | 26885 | 45257 |
| Static instance batches | 0 | 1 |
| Static instances | 0 | 4096 |
| Static batch meshlet draws | 0 | 4096 |
| CPU static batch build us | 2 | 5539 |
| GPU timing supported | 1 | 1 |
| GPU timing enabled | 0 | 0 |
| GPU timing valid | 0 | 0 |
| GPU frame us | 0 | 0 |
| Tracked GPU memory bytes | 1760437484 | 1762993388 |
| Mesh buffer allocated bytes | 962068480 | 962068480 |
| Mesh buffer used bytes | 854274724 | 854275700 |
| Scene buffer allocated bytes | 11337728 | 13041664 |
| Scene buffer peak bytes | 8760432 | 9088112 |
| Staging bytes used this frame | 122704 | 975184 |
| Staging bytes peak this session | 17552192 | 19780672 |

GPU timing support is present, but timing is disabled for these stable captures. All GPU pass timing fields report zero with the health-report reason: `GPU timing is disabled. Enable RenderSettings.Debug.AllowGpuTiming or press Ctrl+F4 in the sample.` Phase 0 also adds the smoke-run switch `--gpu-timing` / `NJULF_RENDERER_GPU_TIMING=true` so future captures can request timestamp collection non-interactively. On this workstation, the Sponza smoke run with `--gpu-timing --smoke-frames 6` printed first-frame diagnostics but exited nonzero before writing a health report, so the checked-in baseline keeps the passing capture and records the timing availability state explicitly.

## Foliage Placeholder Diagnostics

No actual foliage data exists yet in Phase 0, so both captures must report zero for all foliage-specific counters, byte counts, and timings.

| Foliage metric | Sponza | Foliage-like static instances |
| --- | ---: | ---: |
| Foliage patch count | 0 | 0 |
| Foliage prototype count | 0 | 0 |
| Foliage cluster count | 0 | 0 |
| Foliage visible cluster count | 0 | 0 |
| Foliage visible meshlet draw count | 0 | 0 |
| CPU foliage build us | 0 | 0 |
| CPU foliage upload us | 0 | 0 |
| GPU foliage cull us | 0 | 0 |
| GPU foliage depth us | 0 | 0 |
| GPU foliage forward us | 0 | 0 |
| GPU foliage shadow us | 0 | 0 |

## Notes

- The synthetic scenario adds 4096 masked static-instance foliage cards through the existing static batch path. This gives Phase 1 a repeatable stress input before real foliage clusters and meshlet draws are introduced.
- The scenario defaults to startup smoke capture when no explicit smoke mode is supplied, including when `--smoke-frames` is supplied. This avoids mixing the baseline capture with resize lifecycle behavior.
