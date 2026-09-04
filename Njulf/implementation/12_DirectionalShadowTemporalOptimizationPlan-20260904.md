# Directional-Shadow Temporal Optimization

## Goal

Use plan 9 as the first shared-validity consumer and keep the path only if producer plus shadow filtering is faster overall.

## Implementation

- Use the shared codec's bilinear-tap order to select the mask bit corresponding to the existing shadow reprojection sample.
- Reject immediately when that bit is false. Preserve shadow light/mode, blocker, age, neighborhood clamp, blend, diagnostics, and reset rules.
- Initially retain shadow-specific depth, normal, precision, and exact-identity checks. Remove a duplicated check only when a focused equivalence test proves the shared representation and threshold are identical for that check.
- Keep the existing 12-byte shadow history and motion read. Populate retained fields normally so diagnostics and downstream behavior stay compatible.
- If shared validity is unavailable, use the existing local validation path. Add no public setting and do not change CSM resolve, spatial filtering, resolution, or sampling quality.

## Verification

- Add one focused graph/codec test for producer ordering, tap-bit selection, fallback, and retained shadow-specific checks.
- Fresh-build and SPIR-V-validate affected shaders, then run one Vulkan-validation smoke with motion, a camera cut, and a moving occluder.
- Take one matched baseline/candidate pair with plan 9 present in both builds. Compare the shared producer plus directional-shadow temporal aggregate and whole-frame time, not the shadow timer alone.
- Keep only a net gain without trails, false reuse, or reset failure. Otherwise disable the producer and retain the existing shadow path.

## Limit

The reported 0.97-1.52 ms range may include CSM resolve, so the recoverable portion is smaller.
