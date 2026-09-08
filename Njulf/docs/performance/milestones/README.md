# Performance milestones and artifact retention

Keep compact records of meaningful improvements, regressions, and rejected experiments here. Compare timings only when hardware, workload, build settings, and measurement conditions match. Historical numbers below are separate experiments, not a continuous comparable trend.

## Preserved highlights

| Milestone | Evidence and decision |
| --- | --- |
| Historical Bistro baseline (original date not established during cleanup) | Five repeats, 600 measured frames: CPU p95 3.092 ms, GPU p95 14.325 ms; original harness reports target met. [Original summary](bistro-historical-baseline.json). Hardware/build provenance is incomplete in this summary; do not compare it directly with later captures. |
| 2026-09-06 surface input reuse | Disabling shared producer reduced Bistro motion + directional temporal GPU cost 2.934 → 1.687 ms; frame mean/p95 33.493/34.185 → 31.940/32.524 ms. Keep disabled. Depth/motion fusion remains opt-in because image qualification is unresolved. CSM normal reuse removed after only 2.1% pass improvement. [Full milestone, conditions, image results and reproduction](../../../implementation/Complete/SurfaceInputReuse-20260906.md). |
| 2026-09-07 GTAO investigation | Sponza, RTX 3060 Laptop, 1080p DdgiHigh: raw GTAO mean 4.659892 → 4.461925 ms (-4.25%), p95 4.759 → 4.520 ms; GPU frame mean 20.433158 → 20.123808 ms, p95 20.650 → 20.309 ms. Static image gate passed. Inconclusive under approximately 5% quick-loop signal; production unchanged. [Preserved report with hashes and conditions](20260907-gtao-investigation.md). |

Existing milestone reports in `implementation/Complete/` and `docs/` remain intact. Compact JSON, CSV, scripts, logs and written findings in ignored run directories also remain available after this cleanup. Three early loop attempts in `.perf-loop-runs/summary.json` timed out during baseline and rolled back; they provide no performance comparison.

## Retention

[2026-09-07 system-drive migration](20260907-c-drive-migration.md) maps verified Codex diagnostic folders from C: to their retained evidence on D: and records cleanup limitations.

At each milestone record source revision and dirty state, hardware/driver, scene/resolution/settings, warmup and measured frames, baseline/candidate mean and tail latency, image and validation gates, decision, caveats, and reproduction command. Include a small representative image only when it materially documents a visual result. Do not promote single incomparable timings into a progress claim.

Bulky raw evidence and isolated build copies have a two-day retention window after an investigation completes. Preserve references needed by an active campaign. The cleanup utility conservatively uses each file's last-write time and preserves `.perf-loop-runs/campaign`, the shader cache, tracked files, and text/structured reports. Inspect its dry run before applying, and exclude active investigation data if necessary. Historical raw-image/trace/build links in older reports may no longer resolve after pruning; numeric comparisons and decisions are retained, but deleted captures cannot be reanalyzed without rerunning.

Run `pwsh -NoProfile -File tools/prune-local-artifacts.ps1` from the repository to preview, then add `-Apply`. Applied cleanup records here report removed bytes/files per original directory and failures. Cleanup does not touch normal source assets, project `bin`/`obj`, or cooked production assets outside the ignored run roots.
