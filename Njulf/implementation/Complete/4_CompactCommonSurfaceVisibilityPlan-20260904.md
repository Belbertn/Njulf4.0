# Compact Common Surface/Visibility Feasibility

## Goal

Determine whether a shared opaque-surface payload saves more work than it adds. Implement this only after the localized plans and shared-validity producer; do not replace the renderer path in one change.

## Gates

1. **Coverage:** add a diagnostic counter for rigid `SimpleOpaque` pixels with constant, non-emissive materials and no textures, vertex colors, extensions, normal maps, transforms, masking, skinning, or LOD dithering. Stop if coverage is too small to affect the target passes.
2. **Producer cost:** reuse plan 9's identity/depth/normal data. Prototype only a current-frame `SurfaceMaterial` `RG32_UINT` target:
   - word 0: base-layer F0 RGB and perceptual roughness as UNORM8;
   - word 1: canonical diffuse reflectance RGB and material AO as UNORM8.
   Validity comes from plan 9; do not allocate the original current/previous `RGBA32_UINT` geometry pair.
3. Write the material target for eligible geometry during the existing depth/motion raster sequence, then measure its attachment, clear, transition, memory, `DepthPrePass`, and whole-frame cost before adding a consumer. Stop on a material regression.
4. If the producer is affordable, migrate one measured high-cost consumer. Keep all invalid/ineligible pixels on its existing path and compare that one migration before considering another.
5. Motion reconstruction, surface-driven Forward, and 2x2 VRS are later consumers only after the first migration proves a net whole-frame gain. Do not bundle them.

## Contracts

- Document and test the two-word codec, color space, invalid handling, eligibility parity, resize/camera-cut reset, and graph ownership.
- Scene depth remains authoritative. Existing histories and receiver payloads remain until a consumer migration independently proves they are redundant.
- No public setting or API. Missing capability or data fails closed to the legacy path.

## Verification

- Use one matched baseline/candidate pair for each gate, not one comparison containing several migrations.
- For the producer, run one codec/layout test and one Vulkan-validation smoke with eligible and fallback geometry.
- For a consumer, run only its focused shader/graph check and one representative image comparison.

## Stop Condition

Coverage too low, excessive full-resolution bandwidth, or no clear whole-frame gain ends this plan without a renderer-wide migration.
