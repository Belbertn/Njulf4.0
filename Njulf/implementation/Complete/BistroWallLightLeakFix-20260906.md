# Bistro right-wall GI light leak

## RenderDoc evidence

The supplied camera is `(-5.93249, 2.6411655, 9.370055)`, yaw
`2.2665093`, pitch `0.015787821`, FOV `0.98174775`, at 1600x900.
The snapshot uses DDGI High, exact receiver gathering, packed canonical
storage, and the structured atlas fallback. Refinement bricks are disabled.

RenderDoc 1.45 captured the reproduction in `wall_frame1059.rdc`. Event 210
uses `forward_opaque_simple_full_input_ddgi.frag.spv` and writes HDR scene
color. Pixel debugging at `(797,503)` reports world position
`(11.873778,0.803484,24.148384)` and normal
`(-0.931924,-0.003899,-0.362632)`. Its depth matches the opaque depth buffer.
The wall is outside the fine ring and gathers the middle ring, spacing
`3.137475 m`. The near-visibility sidecar covers the fine ring only.

The actual fragment trace and captured buffers show bright probes below the
pavement contributing to that wall pixel:

| Probe | World Y | Irradiance, green | Visibility before selection |
| --- | ---: | ---: | ---: |
| 12461 | -1.051165 | 22.772646 | 0.821972 |
| 12462 | -1.051165 | 16.720034 | 1.000000 |
| 12641 | -1.051165 | 19.745371 | 0.476992 |
| 12642 | -1.051165 | 17.079529 | 1.000000 |

The visible probe above the pavement, 12479, has green irradiance 5.525829.
The shader debugger reproduces the captured HDR output: approximately
`(1.079865,1.071445,1.115406)` for the wall-base pixel. This is direct GPU
shader evidence, rather than a CPU-only mirror. The capture contains no
RenderDoc debug messages.

## Correction

Visibility blending treated the full distance to an authored reverse face
as free-space support. This lets an active probe behind a floor or wall
retain substantial visibility beyond the shell, particularly in a coarse
ring. `BlendVisibilityTexel` now gives that ray zero distance in the
visibility moments. Actual distances remain available to relocation and
coherent depth-layer construction; front faces, double-sided surfaces, and
far-field occupancy retain their existing treatment.

The gather also had a minimum radiance-selection weight of 0.05 for every
blocked probe. It now retains only a numerical floor of 0.00001. Availability
continues to determine volume ownership independently, and normalized
gathering retains support in an entirely blocked cell. Reducing the selection
floor alone did not fix the reported glow; correcting reverse-face visibility
is necessary.

A second RenderDoc capture, after the moment correction, isolates the remaining
doorway strip at `(820,505)`. Its selected transport visibility is `0.13032092`,
but the final `smoothstep(0.01, 0.08, visibility)` produces `1.0`. The debugger
records that promotion and HDR output `(1.329717,1.205212,1.056490)`. This explains
why weakening occluded probes alone still leaves a bright strip: normalization
recovers their radiance, and composition then admits it at full strength.

Final composition now uses the measured visibility directly. The existing
thin-wall strength and five-percent final attenuation floor remain in effect.
The terminal transmission-hit shading path uses the same policy.

Replacing the captured forward fragment shader with the corrected production
SPIR-V in RenderDoc, while retaining the captured probe buffers, reduces the
doorway pixel's HDR green channel from `1.205078` to `0.261719`. The control
pixel above the wall changes from `0.430664` to `0.428711`. This isolates the
effect of the composition correction from subsequent probe convergence.

## Reproduction and image check

A fresh scene initialized directly at this camera uses a different probe
layout and does not reproduce the broad glow. The supplied origins and blend
centers are needed:

| Ring | Probe origin | Blend center |
| --- | --- | --- |
| 0 | (-17.9375, -3.9375, -3.875) | (-4.8125, 2.4953785, 9.25) |
| 1 | (-16.706512, -7.326115, -3.67107) | (10.068633, 4.6817646, 22.730774) |
| 2 | (-39.375, -28.125, -28.125) | (31.192513, 17.985237, 36.481483) |

A temporary hook fixes these inputs, disables refinement to match the
snapshot, and fixes exposure at its recorded 0.07680455. The scene and lighting
are unchanged. Captures are taken after long stationary warmup and completed
DDGI convergence. The original glow persists with reflections disabled and
in diffuse-GI-only output. The final comparison uses those same two views.

Artifacts are retained under `%TEMP%/njulf-wall-leak-20260906`:

- `layout-before`: original shader and supplied layout.
- `renderdoc-inspect/wall_frame1059.rdc`: reproduced leak with the numerical
  selection floor, before the visibility-moment correction.
- `renderdoc-wall-inputs.json`, `renderdoc-trace-changes.json`, and
  `renderdoc-probe-evidence.json`: pixel debugger and captured probe evidence.
- `renderdoc-final/wall_frame1059.rdc`: capture after the moment correction;
  `trace-changes.json` and `doorway-composition-evidence.json` in that folder
  record the remaining doorway pixel's visibility being promoted to full strength.
  `replay-correction-pixels.json` records before/after values with the corrected
  forward shader substituted directly in RenderDoc.
- `wall-verified`: final native captures and renderer diagnostics.
- `reproducer-final.cs`, `reproducer-host.patch`, and `reproducer-layout.patch`:
  temporary controls archived for reproduction.

The wall-base ROI is `[778,493,815,513)`, the wall above it is
`[779,470,797,484)`, the doorway strip is `[815,495,825,515)`, and the
sunlit-road control is `[672,720,713,755)`.
Measurements use display-byte luma `0.2126 R + 0.7152 G + 0.0722 B`, not
linear HDR irradiance.

| View and region | Before | After |
| --- | ---: | ---: |
| No reflections: wall base | 95.13007 | 41.59572 |
| No reflections: wall above | 47.48620 | 41.04913 |
| No reflections: doorway strip | 109.33971 | 30.03465 |
| No reflections: sunlit road | 157.85394 | 157.84862 |
| Diffuse GI: wall base | 94.50550 | 40.65644 |
| Diffuse GI: wall above | 46.82170 | 40.45504 |
| Diffuse GI: doorway strip | 108.79427 | 29.04794 |
| Diffuse GI: sunlit road | 28.44112 | 28.43737 |

Visual inspection confirms that both the broad glow and the doorway strip are
removed. The wall-base brightness now agrees with the wall above it. The
fresh-layout reference has doorway-strip luma 31.21324 in the diffuse view;
the corrected supplied layout is similarly dark. That fresh layout is a
sanity check, not the matched before/after baseline.

## Validation

The Development shader/sample build passes with zero warnings and errors,
including the final build after removing all reproduction hooks. Both ordinary
and guided production DDGI blend shaders and the captured forward fragment
variant pass `spirv-val --target-env vulkan1.3`.
Five existing numerical reference checks pass for visibility selection,
constant-field normalization, supported zero-visibility gathering, ownership,
and final thin-wall attenuation. These maintain existing fixtures; the
rendered comparison is the primary behavioral check.

The final native run uses `--scene=bistro --quality-preset=ddgi-high
--smoke-frames=2000` and exits successfully. Its diffuse capture has 15,368
probes, 14,821 converged participants, zero pending solver probes, a current
tail certificate, exposure 0.07680455, and zero Vulkan validation warnings or
errors. The cold launch required a lengthy NVIDIA pipeline compilation after
the shader cache invalidation; no startup-performance claim is made here.

The camera, exposure, layout, and capture hooks are archived in the temporary
artifact directory and removed from production source. Debugger sessions are
closed and the pre-existing breakpoints retain their original states.
