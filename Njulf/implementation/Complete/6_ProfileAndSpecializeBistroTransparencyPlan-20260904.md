# Profile and Specialize Bistro Transparency

## Goal

Attribute Bistro's transparent cost, then implement one scene-independent optimization. Do not revisit the ineffective TLAS-mask experiment.

## Decision

- Use the existing capture if it identifies fragment versus task/mesh cost; otherwise take one short symbolized capture.
- If fragment-bound, add only the missing `ThinGlass + SortedAlphaBlend + RaySceneRequired` specialization used by the captured workload. Reuse the existing ThinGlass manifest/classification and non-ray specialization; do not add a sorted/weighted by ray/non-ray matrix.
- The ray ThinGlass shader must retain alpha/blending, directional DDGI, shadows, ray-required reflections, and current refraction behavior while compiling out only proven-unused generic material paths.
- If task/mesh-bound instead, add one taskless transparent mesh path using `gl_WorkGroupID`. Preserve back-to-front command order, 64/126 bounds, fragment selection, and diagnostic counter semantics.
- Select one branch only. Add no Bistro asset/name override or public setting.

## Verification

- Update only the classifier/run-planning/artifact tests affected by the selected branch.
- Fresh-build changed shaders, run `spirv-val`, and exercise the pipeline under Vulkan validation.
- Take one fresh Bistro baseline/candidate pair with identical build, artifacts, resolution, settings, warmup, GPU, and driver. Use the existing 4.78 ms only when that identity matches.
- Keep a clear `TransparentForwardPass` improvement without a whole-frame or representative-image regression; otherwise reject or mark inconclusive.
