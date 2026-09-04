# Meshlet Tuning and Sponza Content Audit

## Goal

Treat meshlets as a bounded, low-priority cooker/content experiment. Forward is fragment-bound, so an inconclusive result ends this plan.

## Implementation

- Add a validated `-MeshletBuildProfile` option to `cook-bistro.ps1` and include the complete profile in incremental cook identity.
- Keep meshlet-local index encoding valid up to the supported 64-vertex bound and add one cook/load round-trip test.
- Collect offline count, fill, cone-validity, and hierarchy statistics for the available profiles, then select one candidate for a GPU comparison. Do not capture every profile.
- A cooked profile must match the runtime mesh-shader bounds. For a candidate above the current production 48-vertex/64-triangle limits, serialize its profile/bounds in the cooked scene manifest and use them for compatible pipeline selection before rendering; reject unsupported combinations at load. If that plumbing is not implemented, do not test or pin 64/126 content.
- Pin a Bistro profile only after a clear matched improvement. Do not change the global production profile or Sponza profile from Bistro-only evidence.
- Audit Sponza double-sided materials across all source glTFs using material flags, topology, and a visual smoke. Change `metal_door` or any allowlist entry only when that audit proves it safe; do not prescribe the result in advance.

## Verification

- Run focused cooker identity, round-trip, bounds, hierarchy, cone-culling, and runtime-permutation compatibility tests.
- Take one matched Bistro baseline/candidate capture for the selected profile. Record Forward, whole-frame time, submitted meshlets/fill, cone rejection, overflows, and one representative image.
- Keep only a clear Forward and whole-frame improvement with no bounds, culling, or image failure. Otherwise retain the portable profile and stop.
- If the Sponza sidedness audit produces a source change, recook once and run one two-sided visual smoke.

## Limit

The measured mesh-stage ceiling is roughly 1.8 ms; this is not a primary Forward optimization.
