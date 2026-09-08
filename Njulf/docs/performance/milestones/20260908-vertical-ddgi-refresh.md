# Vertical DDGI refresh, 2026-09-08

Kept a correctness fix for camera ascent. The reproduced route now publishes all
five entering near-ring layers (782 probes each), performs no global atlas clears,
reports no failed scroll cohorts, and finishes with no deferred cascades.
[Compact capture evidence](20260908-vertical-ddgi-refresh.json) includes rejected
candidates, timings, identities, and health limitations.

Revision: `7eafc485087af01fabfa4a931fb4d03eae93dae7`, with a worktree already dirty
before this investigation. Unrelated changes were preserved. Hardware: Ryzen 5
5600H, RTX 3060 Laptop, driver 610.248.0. Workload: Sponza, Development 1080p60
profile, DDGI GPU-resident scheduling with recursive glossy and directional
guiding, 1920x1080, Standard validation, NormalTelemetry. The user capture was
1600x900 and showed 34 lifetime atlas clears.

The new `sponza-vertical-refresh` route holds the supplied X/Z, yaw, pitch, and FOV,
rises from Y=2 to Y=32 in 90 frames, then holds through frame 239. This is 30 metres
in 1.5 seconds at the trajectory's 60 Hz reference rate; wall time depends on frame
rate. It reaches above the supplied snapshot's Y=24.178.

## Causes and changes

- A refinement's tracing horizon changed from 40.375 to 65.79086 metres and back.
  The storage fingerprint changed, although all cache addresses and formats were
  identical. Capacity handling consequently cleared every atlas. Address
  compatibility now excludes range diagnostics, while volume identity includes
  the tracing horizon so only the affected volume loses sample authority.
- The near grid is 34x15x23. A vertical entering plane needs 34x23=782 requests,
  exceeding the ordinary 640-request cap. Persistent capacity now reserves an
  affordable plane on every ring axis. Ordinary work keeps its budget; this plane
  uses 32 rays per probe, or 25,024 rays within the 32,768 spatial-repair ceiling.
- The first candidate admitted all five layers but rejected the first layer's
  directional publication. Instrumented producer evidence identified a check
  requiring both maintenance and mixture samples. The 32-of-128 spatial subset
  contains only uniform maintenance samples. Projection, blend, staging and audit
  now accept that authenticated subset using the existing balance denominator.
- A subsequent refinement move was incorrectly included in mandatory ring-cohort
  validation. Independent authored/refinement refreshes now retain their ordinary
  commit validation. Mandatory ring cohort checks remain intact. Cohort rejection
  also retains missing-completion and producer masks for diagnosis.

## Evidence and limits

| Diagnostic run | Global clears during route | Complete near layers | Cohort warnings/failures | CPU p95 / p99 ms | GPU p95 / p99 ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 2 | 0 | 0 | 16.420 / 51.093 | 61.430 / 68.566 |
| Capacity/address candidate, rejected | 0 | 4 of 5 | 2 | 15.485 / 37.027 | 47.228 / 53.569 |
| Accepted | 0 | 5 of 5 | 0 | 15.868 / 23.231 | 46.157 / 55.568 |

Final focused tests: **314 passed**. Broader checks: 327 passed and one pre-existing
failure, `RuntimeDiagnostics_ExportExactCommitRejections`, which expects the shader
source substring `COUNTER_COMMIT_REJECTED));` but the existing shader contains a
single closing parenthesis. Both files are unchanged from HEAD. The trace-horizon
identity regression was observed failing before its fix. All 22 affected
projection/blend/audit/commit SPIR-V modules passed `spirv-val --target-env vulkan1.4`;
the seven affected modules loaded by the route match the compiled hashes. Vulkan
validation and GI diagnostic warning/error counts are zero in the accepted capture.

These are diagnostic timings, not a production performance result. All health
reports reject the existing 6 ms CPU-renderer budget. Shader loading occurred
during measurement, the run uses Development/Standard, and transport had not fully
converged. The accepted final HDR capture is retained, but no temporal image
comparison proves pixel-identical old lighting or completely imperceptible
transitions. The evidence establishes removal of global resets and successful
entering-layer publication. The debugger attach yielded unbound breakpoints and
empty stacks; temporary breakpoints and that session were removed.

## Reproduction and retention

Run from the workspace root after a Development build:

```powershell
$runRoot = Join-Path (Get-Location).Path '.codex-tmp/vertical-gi-refresh-20260908'
New-Item -ItemType Directory -Force (Join-Path $runRoot 'temp') | Out-Null
$env:TEMP = Join-Path $runRoot 'temp'
$env:TMP = $env:TEMP
$env:NJULF_DDGI_WARM_CACHE_DIR = Join-Path $runRoot 'repro-warm-cache'
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore
Push-Location NjulfHelloGame
try {
    & './bin/Development/net10.0/NjulfHelloGame.exe' --benchmark --scene Sponza `
        --benchmark-trajectory sponza-vertical-refresh `
        --benchmark-warmup-frames 320 --benchmark-measure-frames 240 `
        --benchmark-max-settle-frames 1024 --validation standard --gpu-timing `
        --benchmark-report (Join-Path $runRoot 'repro.json') `
        --health-report (Join-Path $runRoot 'repro.health.json') `
        --benchmark-hdr-candidate (Join-Path $runRoot 'repro-final.pfm')
} finally { Pop-Location }
```

`DdgiMotionFrames` preserves clear/scroll events that a final snapshot misses.
GPU completion feedback trails the CPU scroll event by two frames in these runs.
Expected final evidence: 240 frames, five 782/782/782 accepted/traced/committed
cohorts, zero clear events, zero cohort failures, zero final deferred cascades.

Raw data stays under `.codex-tmp/vertical-gi-refresh-20260908`, retained only for
this investigation and at most two days after completion. Superseded HDR captures
are pruned after recording these findings; the accepted HDR and compact evidence
remain; the task directory retains approximately 44 MiB. An early run's certified
warm cache was verified by its save telemetry,
copied and hash-checked on D:, then removed from C:. Storage inspection with
`tools/prune-local-artifacts.ps1` found about 298 GiB free and one older 20 MiB
secondary-view artifact outside this investigation; that reference was retained.
