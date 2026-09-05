# Forward Fragment Interface Reduction Evidence

Plan: [3_ForwardFragmentInterfaceReductionPlan-20260904.md](../3_ForwardFragmentInterfaceReductionPlan-20260904.md)

## Production follow-up (2026-09-05)

The opaque `Simple` and `SimpleFullInput` families now transport material,
object, and meshlet IDs as separate component-qualified `perprimitiveEXT`
scalars at location 2. World position remains interpolated. `SimpleFullInput`
uses dedicated regular and compacted mesh artifacts so Full and transparent
pipelines retain their original interface.

Release compilation generated 498 shader artifacts with zero build warnings or
errors and passed the production shader audits. Eight affected production
modules passed `spirv-val`; disassembly confirmed matching `Location`,
`Component`, `Flat`, and `PerPrimitiveEXT` decorations on both stages. The
focused shader-embedding test passed. Performance acceptance was an explicit
implementation premise for this follow-up, so no additional timing capture was
run.

## Original candidate disposition

No candidate was retained during the original measurement loop. Each
independent interface change was implemented,
fresh-built, SPIR-V validated, and measured once against the same warmed Bistro
presentation trajectory. Neither produced a clear timing improvement, and both
failed the matched HDR comparison. The source changes were therefore reverted
as required by the plan's acceptance gate.

The family-matching candidate was not built as a combined change. Its reduced
`Simple` and `SimpleFullInput` interfaces depend on the two rejected changes and
would not provide an independent reduction after they were removed.

## Capture contract

- GPU: NVIDIA GeForce RTX 3060 Laptop GPU.
- Driver: 610.248.0.
- Resolution: 1920 x 1080.
- Scenario: Bistro, normal performance scenario, presentation quality.
- Submission: GPU compaction and indirect dispatch enabled.
- Sampling: 480 warm-up frames and 240 measured frames.
- Pair/trajectory: `forward-fragment-interface-20260904` /
  `bistro-presentation`.
- The settings fingerprint and build commit matched across all three captures.
  Each candidate had the expected distinct shader fingerprint.

## Timing

All values are milliseconds. Positive percentages are regressions.

| Capture | GPU average | GPU p50 | GPU p95 | Forward average | Forward p50 | Forward p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 29.766 | 29.520 | 31.193 | 18.910 | 18.896 | 19.224 |
| Reconstruct world position | 30.254 (+1.64%) | 30.014 (+1.68%) | 31.539 (+1.11%) | 19.289 (+2.01%) | 19.258 (+1.92%) | 19.753 (+2.75%) |
| Pack IDs into `uvec3` | 30.317 (+1.85%) | 30.052 (+1.80%) | 31.829 (+2.04%) | 19.251 (+1.80%) | 19.240 (+1.82%) | 19.547 (+1.68%) |

Evidence:

- [Baseline capture](../../.perf-loop-runs/forward-fragment-interface-20260904/baseline.json)
- [World-reconstruction capture](../../.perf-loop-runs/forward-fragment-interface-20260904/world-reconstruction.json)
- [Packed-ID capture](../../.perf-loop-runs/forward-fragment-interface-20260904/id-packing.json)

## Interface and correctness checks

The world-position candidate reduced the freshly reflected `Simple` interface
from 11 to 8 transported 32-bit components and removed one location. The
packed-ID candidate reduced `Simple` from six locations to four and Full from
nine locations to seven while preserving the transported component count.

For each candidate, the Release shader build regenerated 178 affected forward
artifacts and completed the production atomic and receiver-contract audits.
Eight representative regular/compacted mesh and `Simple`, `SimpleFullInput`,
Full, and transparent fragment modules passed `spirv-val`. Reflection confirmed
producer/consumer location and type agreement; the packed `uvec3` carried the
`Flat` decoration on both stages.

The HDR gate was a maximum relative RMSE of `0.005`. World reconstruction
measured `0.086532`; ID packing measured `0.084000`. Both capture contracts were
non-comparable only because of that failed image gate. The conservative result
is rejection even though ID grouping is intended to preserve the three integer
values exactly.

No Nsight shader-profile artifact was supplied, so hardware register counts,
spills/local memory, and interface-stall counters were unavailable. Source or
SPIR-V component counts were not substituted for those hardware measurements.
A permanent reflection test and Vulkan family-validation matrix were not added
for interfaces that the timing gate rejected and that are absent from the final
source.
