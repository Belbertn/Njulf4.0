# Standalone Motion-Vector Sided Raster Optimization

## Goal

Keep `MotionVectorPass` separate from depth and use its existing compacted one-sided/double-sided partitions to avoid back-face fragment work.

## Implementation

- Extend dynamic raster-state eligibility to compacted solid and masked motion-vector pipelines.
- Before every compacted motion partition, set both dynamic states explicitly:
  - confirmed one-sided specialization: back-face culling and reverse-Z `GreaterOrEqual`;
  - double-sided or mixed/fallback partition: no culling and reverse-Z `GreaterOrEqual`.
- Preserve alpha coverage, foliage motion, mirrored-winding handling, history invalidation, receiver signatures, draw order, and the non-compacted fallback.
- Add no shader ABI, render target, public API, or setting. Do not reintroduce depth-plus-motion fusion.

## Verification

- Add focused tests for dynamic-state eligibility and state emission for solid/masked, one-sided, double-sided, and mixed partitions.
- Build the renderer and run one Sponza Vulkan-validation smoke using the motion-vector debug view.
- Take one matched warm baseline/candidate pair and record `MotionVectorPass` plus whole-frame time. Keep only a clear motion improvement without rendering or frame regression.
