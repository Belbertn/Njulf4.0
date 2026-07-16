# Forward-Pass High-Priority Fix Plan

This plan excludes the SSGI attachment and foliage-integration findings.

1. **Unify masked-material coverage inputs.** Pass vertex-color alpha through the depth-prepass mesh/fragment path and calculate coverage from the same material alpha, albedo texture alpha, vertex alpha, cutoff, and alpha-mode rules used by Forward+.

2. **Share the masked-alpha implementation.** Move the coverage calculation into a shader include used by both `depth_alpha.frag` and `forward.frag`, preventing the two passes from drifting apart as material features change.

3. **Match foliage coverage across both passes.** Apply the same LOD-band dithering and stable coverage seed in `foliage_depth.frag` and `foliage_forward.frag`. Keep depth writes only for pixels that will survive the forward foliage pass.

4. **Centralize foliage discard rules.** Extract shape, alpha-cutoff, and LOD-dither decisions into a shared shader helper so depth and forward foliage execute identical coverage logic.

5. **Make tiled-light culling depth-aware.** Reconstruct the tile's visible view-space depth interval and test each local light volume against both the tile frustum and that interval. Remove the currently unused `minDepth` and `maxDepth` parameters.

6. **Handle tile overflow deterministically.** When candidates exceed `MaxLightsPerTile`, retain lights by a stable influence metric such as projected contribution and distance instead of atomic arrival order. Record overflow counts for diagnostics.

7. **Add focused regression coverage.** Test vertex-alpha masks, foliage LOD transitions, mirrored draw order, sparse-depth tiles, lights outside a tile's depth range, and repeatability when a tile exceeds its light limit.

## Completion criteria

- Masked geometry writes depth for exactly the pixels shaded by Forward+.
- Foliage depth coverage matches forward coverage throughout every LOD band.
- Off-depth local lights are rejected before entering a tile's light list.
- Overflow produces a stable, intentional light selection across repeated frames.
- Shader validation and the relevant renderer tests pass without new Vulkan validation errors.
