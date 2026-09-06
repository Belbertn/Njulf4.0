# DDGI audit lifecycle and scene compaction — 2026-09-06

## Decision and changes

Keep the focused DDGI correctness fix and lifecycle diagnostics. Leave audit
chunk scheduling and scene compaction unchanged. This work establishes no
whole-frame performance improvement.

- Reproduced a controller defect: `CancelAudit` after certification overwrote
  the accepted summary reason and made `IsCertified` false despite unchanged
  generations. The existing certification regression failed on all three new
  assertions before the fix and passes afterward. Cancellation now applies
  only to a frozen audit, including at the manager boundary. Actual generation
  invalidation remains authoritative. This defect was demonstrated directly;
  it was not observed causing the captured frame spikes.
- Skip camera-admission bookkeeping for an already current certificate.
  Preserve the earlier frozen-audit continuation path and all real source,
  ownership, calibration, scroll, and recovery invalidations.
- Record admission cause, frozen/current generations, logical volume identity,
  first/final chunk submission, observed completion or termination, coverage
  counts, and elapsed time. Keep 64 immutable transitions with explicit eviction
  count and reuse snapshots between transitions. Reports retain deduplicated
  history and separate audit-active/idle GPU frame tails using aligned command
  intent. Certification payload schemas, digests, GPU ABI, and shaders are unchanged.
- Do not suppress generic convergence resets without runtime evidence. The
  captured retries audited different canonical fields; both captures reported
  zero same-tuple re-audit attempts, audit cancellations, and readback timeouts.

## One moving-scene comparison

Instrumented baseline before the cancellation/admission fixes, then one candidate;
Release, Sponza, DDGI High, horizontal trajectory, 120 warmup and 300 measured
frames, VSync off, active-scene pipeline startup with bootstrap wait. Both runs
exited successfully with 300 valid GPU samples and no settling timeout.

| Measured value | Baseline | Candidate |
| --- | ---: | ---: |
| CPU mean / p95 / max (ms) | 2.751 / 3.221 / 12.302 | 3.941 / 6.337 / 57.576 |
| GPU frame mean (ms) | 32.118 | 33.194 |
| GPU frame p95 / p99 / max (ms) | 42.683 / 52.026 / 59.174 | 42.132 / 43.888 / 46.937 |
| Audit pass mean / p95 / max (ms, inactive frames included) | 0.246 / 1.506 / 1.769 | 0.242 / 1.431 / 1.732 |
| Additional settling frames | 322 | 146 |
| Retained starts / rejected / certified | 3 / 2 / 1 | 2 / 2 / 0 |

Different settling/source epochs and canonical fields make the performance
comparison **inconclusive**. CPU tails worsened; GPU mean also increased while
GPU tails decreased. No speedup or latency-regression bound is claimed from
this pair. Candidate audit-active frames (62) had GPU p95/p99/max
30.725/31.083/31.083 ms; idle frames (238) had 42.377/45.052/46.937 ms. The worst
tails were outside audit dispatch. These cohorts differ in scene state, so
this is not a causal estimate of the audit's overhead. Keep two chunks per
frame; the measurements do not justify the optional scheduling change.

All retained audits submitted 62 chunks over 31 scheduler frames and terminated
one scheduler frame after the final submission:

| Run, solve/canonical-field generation | Result | Admission-to-consumption latency (ms) |
| --- | --- | ---: |
| Baseline 2/62 | Tail above tolerance | 911.243 |
| Baseline 3/79 | Tail above tolerance | 890.840 |
| Baseline 4/89 | Certified | 978.933 |
| Candidate 3/88 | Tail above tolerance | 927.218 |
| Candidate 4/98 | Tail above tolerance | 980.029 |

The candidate's last completed audit covered exactly 12,166 participants and
778,624 texels, with zero non-finite, cache identity, or counter-overflow failures.
It correctly rejected above-tolerance evidence; subsequent moving-scene work
remained in accelerated solve. Completion latency here includes CPU observation
delay and is not exclusive GPU execution time.

Artifacts: [baseline JSON](../.perf-loop-runs/ddgi-audit-compaction-20260906/sponza-baseline.json),
[candidate JSON](../.perf-loop-runs/ddgi-audit-compaction-20260906/sponza-candidate.json).
The first baseline launch stalled during startup and produced no measurement;
it was excluded and replaced once using bootstrap startup. Its log is retained
as `sponza-startup-stalled.log`. The moving captures preceded the final removal
of lifecycle diagnostics from the versioned transient payload and two final
diagnostic-only hooks; they are diagnostic evidence, not final-binary performance
qualification.

Reproduce the moving workload (change the report filename between builds):

```powershell
& './NjulfHelloGame/bin/Release/net10.0/NjulfHelloGame.exe' --scene=sponza --quality-preset=ddgi-high --benchmark-trajectory=sponza-horizontal --benchmark-warmup-frames=120 --benchmark-measure-frames=300 --benchmark-max-settle-frames=4096 --benchmark-budget-profile=stress --benchmark-pair-id=ddgi-audit-20260906 --benchmark-variant=baseline --benchmark-report=.perf-loop-runs/ddgi-audit-compaction-20260906/sponza-candidate.json --pipeline-startup=active-scene --startup-wait=bootstrap --gpu-timing=true --vsync=false
```

`--benchmark-variant=baseline` identifies the authored scene variant, not the
before/after code label.

One additional 60-frame stationary
`--performance-scenario=gi-sponza-freeze-after-atmosphere-step` smoke used the
same settings and 120-frame warmup. It exited successfully with 60 valid GPU
samples, 421 additional settling frames, and no settling timeout. The final
certificate was current: 12,119/12,119 participants, 775,616/775,616 texels,
zero non-finite values, overflows, timeouts, or same-tuple retries. Three
above-tolerance results preceded certification of solve 5 / canonical field 81;
the successful audit took 892.470 ms from admission to observed consumption.
See [stationary atmosphere report](../.perf-loop-runs/ddgi-audit-compaction-20260906/sponza-relight.json).
This scenario freezes the atmosphere after its first rendered step; the retained
source-lighting generation was 1 throughout. It verifies stationary completion,
not a post-certification lighting-edit response. Real generation changes are
covered by the focused controller/readback tests; no scheduling or lighting
math was changed.

## Scene compaction measurement gate

The prior approximately 1.6 ms dispatch intervals both include GPU context
switching and cannot establish exclusive kernel cost. One Nsight Graphics
2026.3.1 GPU Trace attempt requested eight warmed Bistro frames, with real-time
shader profiling and multi-pass profiling left off. It failed before capture:

> GPU Performance Counters unavailable. Please enable access to GPU performance counters.

See [capture log](../.perf-loop-runs/ddgi-audit-compaction-20260906/compaction-trace.log).
No counter permissions or system settings were changed. The clean measurement
gate is blocked, so no instance/material-read, atomic, or synchronization
optimization was attempted. The new timestamp samples are also not a substitute
for exclusive execution measurement. No new coverage/count/overflow validation
campaign is warranted because compaction is unchanged.

The retained output trimming's saved benefit is local: compaction mean
**0.817 to 0.765 ms**, whole-frame mean **26.532 to 26.556 ms**, effectively
neutral. The [existing trimming evidence](Complete/OrderedPerformancePlansEvidence-20260904.md)
records image validation, 44,176 output commands in both runs, and zero overflow.

When counter access is available, capture a clean dispatch first. Only if its
exclusive cost is worthwhile, inspect repeated instance/material loads,
contention and barriers, change the supported hotspot, and compare one matched
before/after with visible draw coverage, emitted counts, and overflow handling.

## Focused verification

Release application build succeeded. Focused tests cover the reproduced
post-certification cancellation defect, real frozen-generation cancellation,
every frozen-generation readback mutation, above-tolerance rejection, camera
admission versus frozen continuation, bounded immutable lifecycle history and
serialization, report timing alignment/deduplication, and the existing exact
certification wire contract. No shader, rendering-output, resource-lifetime,
or scheduling contract changed, so no new image/ABI/full-suite matrix was run.

Logs are retained under `.tmp/ddgi-audit-*.log`. The test project reports the
existing unrelated CS0162 warning in `SampleDebugViewCycleTests.cs:31`.

- `ddgi-audit-tests.log`: 7 passed (initial lifecycle, motion, and controller checks).
- `ddgi-audit-regression-before.log`: expected failure reproducing certificate revocation.
- `ddgi-audit-regression-after.log`: 4 passed (the fix and actual invalidation/rejection).
- `ddgi-audit-evidence-tests.log`: 3 passed (immutable history and report alignment).
- `ddgi-audit-wire-check.log`: 4 passed (history serialization and existing strict wire verification).
- `ddgi-audit-final-build.log`: final Release application build.

Tests were repeated only after a relevant implementation change or to verify
the discovered certification-payload compatibility risk.
