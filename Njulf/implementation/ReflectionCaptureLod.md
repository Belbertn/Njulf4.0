# Reflection capture LOD

Implemented capture-pixel LOD selection in the CPU secondary draw-list builder. The
GPU benefit comes from submitting fewer meshlets to the existing depth and color
passes. No shader, descriptor, or GPU ABI changes were required.

The selector uses capture dimensions/projection, capture-camera distance to a
conservative world bound, simplification error, and a scale bound that includes
shear. Each reflector/probe has independent 15% hysteresis. Skinned meshes,
geometry decals, invalid inputs, and missing error metadata retain full detail.
Probe tickets freeze requested and resident effective LODs across all six faces;
membership or residency changes retry the private capture without publishing a
partial cubemap. Existing residency demands are unioned, including failed retries.

`Reflections.CaptureLodEnabled` defaults to true. Pixel budgets are Low 4, Medium 2,
High/DdgiHigh 1, Ultra 0.5. Settings schema 28 preserves explicit overrides and lets
older files inherit their preset. See `RendererSettingsReference.md` for controls.

## Validation, 2026-09-07

- Release C# builds passed; all 95 targeted regression tests passed. Result:
  `.perf-loop-runs/reflection-lod-20260907/tests/reflection-lod-native.trx`.
- The unchanged shader compile reported 534 up-to-date modules. The separate long
  Simple-DDGI receiver verifier was interrupted; it is not claimed as completed.
  Subsequent C# builds used `BuildProjectReferences=false`, `DesignTimeBuild=true`,
  and `UseSharedCompilation=false` to avoid rerunning unchanged shader verification.
- Added `reflection-lod` scene/trajectory: sharp mirror, rough floor, colored relief
  meshes, thin silhouette, and a probe captured one face per frame. Its 240-frame
  near/far route uses native resolution and 11 HDR checkpoints.
- Medium and Ultra, LOD enabled versus disabled: all checkpoints passed the existing
  HDR/FLIP comparer (relative RMSE <= 0.12, FLIP p95 <= 0.02). Six adjacent-frame
  comparisons per preset passed a 0.02 relative temporal residual threshold.
  All four admitted captures published a probe and reported zero Vulkan validation
  warnings/errors. Near/far previews were inspected for silhouettes and reflection
  continuity. These are local checks, not a formal release campaign.

| Maximum across checkpoints | Medium | Ultra |
| --- | ---: | ---: |
| HDR relative RMSE | 0.005155 | 0.010757 |
| FLIP p95 | 0.005548 | 0.002147 |
| Relative temporal residual | 0.000744 | 0.000889 |

The matched Ultra route recorded 213 planar submissions in each variant. Candidate
meshlets fell from 121,410 to 28,597 (76.4%); command bytes fell from 1,920,064 to
445,712 (76.8%). This measures draw-command traffic, not streamed geometry uploads.

A single Bistro/DdgiHigh moving-camera pair on RTX 3060 Laptop, 1920x1080, 240 measured
frames, validation off, yielded planar-pass p95 4.426 -> 3.965 ms, GPU frame p95
29.912 -> 29.034 ms, and CPU frame p95 15.328 -> 12.539 ms. Both runs settled and
passed loaded-shader inventory/boundary checks. This is a provisional timing signal:
one pair does not establish noise bounds, and independent per-frame pipeline-creation
evidence was not collected. Raw reports: `.perf-loop-runs/reflection-lod-20260907/`.
The Bistro HDR comparison passed (relative RMSE 0.04885, FLIP p95 0.00998).

Quality evidence is under
`C:/Users/njaal/AppData/Local/Temp/NjulfReflectionLod/quality/`: `Ultra-*-clean`,
`Medium-*-native`, and the two `*-comparison.json` files. The harness requires a
clean worktree, so source snapshots were committed only in the isolated detached
worktree `D:/Code/reflection-lod-validation` (Ultra f92f15de; Medium a8c88820).
The user's working tree was not committed or reset. Failed preliminary attempts
are retained separately; none were admitted as passing evidence.

The isolated copy included redundant build configurations and exhausted available
space on D:. Automatic approval review blocked cleanup commands, including narrowed
file-only cleanup, without a more specific reason. Those temporary copies remain;
final validation binaries and images were written to C: instead.
