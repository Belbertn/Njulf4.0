# GTAO Spatial Optimization

## Goal

Preserve `raw -> temporal -> spatial` and existing quality settings while removing duplicate geometry reconstruction and redundant reduced-resolution spatial loads.

## Implementation

- Add graph-owned `GtaoCurrentGeometry` `RG32UI` scratch at the GTAO working extent. Raw stores the geometric normal and view depth it already reconstructs.
- Temporal reads that scratch instead of reconstructing the same geometry. It must still write `GtaoGeometryHistory` with its age and rejection state.
- In spatial, load each unique source-resolution temporal/geometry texel once into the shared tile. Combine Gaussian coefficients for output taps that alias the same source texel, then evaluate depth, normal, and bent-normal weights once.
- Preserve quarter-, half-, and full-resolution behavior, odd extents, edge clamping, radius, precision, temporal weights, and bent-normal output.
- Remove only bindings proven unused after the change: raw HiZ, temporal scene depth, and spatial scene-depth/reconstruction inputs. Keep `GtaoCurrentGeometry` stores whenever temporal consumes them; skip only unrelated debug work when its view is disabled.
- Define scratch creation, clear/reset behavior, layout transitions, and resize handling in the render graph. Add no public setting.

## Verification

- Add one CPU mirror test for exact collapsed-kernel coefficients across supported scales and odd edges.
- Fresh-build the three shaders, run `spirv-val`, and exercise the path once under Vulkan validation.
- Take one fresh, matched Sponza baseline/candidate pair at the same resolution and settings. The supplied 5.19/3.38 ms trace is prioritization evidence, not a baseline unless build and workload identity match.
- Record raw, temporal, spatial, total GTAO, scratch memory, and whole-frame time. Keep only a clear total-GTAO improvement with no image or whole-frame regression.
