# Independent secondary-view rendering

Automatic planar slots and scheduled cubemap faces now use `SecondaryViewRenderer`. Native Bistro capture cost fell from **5.892 to 4.526 ms (23.2%)**, with whole-frame GPU time essentially unchanged. Captures no longer replace the main camera fields in `SceneRenderingData` or consume its visible draw streams. The old capture branches and raw-list retention switch were removed from `ForwardPlusPass` and `SceneDataBuilder`.

## Implementation

- Build each view's opaque material buckets and sorted transparency from the complete submitted scene, using stable object indices and the existing meshlet residency resolver. Apply exact object exclusions before submission. Cull objects against the reflected frustum and clip plane; test boundary meshlets with bounds that remain conservative under shear and negative/nonuniform scale. Skinned geometry bypasses unproven CPU deformation bounds.
- Give each frame slot two planar-view resource slots and six probe-face slots. Each owns its draw buffers, task-frame/frustum data, foliage cull outputs and descriptor set. Shared scene bindings are copied from the heap; overrides remain private to the view. Buffers grow only after that frame slot's fence has completed. Reusing an already recorded slot in the same submission fails explicitly.
- Use the existing corrected reverse-Z depth prepass for both planar and probe captures, then the Full/Simple/SimpleFullInput material pipelines with matching mesh interfaces and a Full fallback during preparation. Color shading reads the prepass depth. Foliage writes depth and uses its own cull/expansion outputs. Probe feedback uses the matching B1 foliage mesh producers.
- Preserve capture content policies: planar views include sorted transparency and enabled decals; probes include opaque/masked geometry and foliage. Probe `IncludesDdgi=false` also disables foliage gathers. Recursive local reflections and main-camera screen effects stay disabled in captures. Probe scheduling, publication and feedback-batch completion remain with their existing owners.
- Compute the reflector footprint by clipping its conservative world-space bounds against the main camera, then projecting that visible receiver portion into the reflected view. Expand it for the production GGX filter, trilinear sampling, clearcoat, the bounded geometric-AA roughness increase and a small temporal guard. Apply a narrower culling frustum and raster scissor while preserving capture UVs and filter scale. Clear the entire attachment. The production depth-history shader gives pixels outside the rendered region zero depth and zero confidence.
- Preserve the existing reuse age. A cropped capture refreshes when camera or coverage changes make reuse unproven. Stationary crops and full-view captures retain ordinary reprojection. A tighter motion-coverage oracle, GPU occlusion and a new spatial hierarchy are deferred.

The Bistro filtering bound conservatively covers the full 960×540 capture. Its measured improvement comes from independent object/meshlet visibility and correct material partitioning, **not** from a smaller scissor. Low-roughness footprints are covered by the focused crop tests.

## Native measurements

Artifacts: `.perf-loop-runs/secondary-views-20260906/`. Release, Bistro Presentation, DdgiHigh, 1920×1080, async compute disabled, validation off, 120 warmup and 120 measurement frames after built-in settling. Settings fingerprint: `4cb1f086956735456f7c262584c062232ed2e39f111bcfa538466c3b825c0162`.

The existing depth/pipeline fixes were already enabled for `initial.json`: capture frames averaged **5.892 ms**, reprojection **0.302 ms**, whole-frame GPU **28.600 ms**. The older 93.9 ms instrumented capture is not a comparable native baseline; see [the earlier depth/specialization evidence](AutomaticPlanarCaptureDepthAndSpecialization-20260905.md).

The first visibility measurement (`culled.json`) reduced capture frames to **4.518 ms (−23.3%)**, with **0.314 ms** reprojection and **28.464 ms** whole-frame GPU. Both runs had 24 capture and 96 reprojection samples and completed settling. The HDR comparison passed: relative RMSE **0.0571**, FLIP P95 **0.00634**, below the built-in limits of 0.12 and 0.02. CPU command recording increased; no CPU-speedup claim is made. The whole-frame GPU measurement remained below baseline, so no more elaborate culling was added.

Final-binary confirmation (`final.json`):

| Mean GPU time | Initial | Final |
| --- | ---: | ---: |
| Capture frame, 24 samples | 5.892 ms | 4.526 ms |
| Reprojection frame, 96 samples | 0.302 ms | 0.314 ms |
| Whole frame, 120 samples | 28.600 ms | 28.573 ms |

The final run has the same settings fingerprint, a comparable capture contract and no settling timeout. Its HDR comparison passed (relative RMSE **0.04451**, FLIP P95 **0.00638**). The absolute capture saving is **1.366 ms**; whole-frame timing is effectively unchanged. Both the minimum capture win and no-whole-frame-regression criteria are satisfied.

The final executable dependencies match the recorded benchmark hashes:

- `Njulf.Rendering.dll`: `E4B74A6F29511081471A1A745CFE900A5BAC12BE649318A34D4C6B1D615632AA`
- `Njulf.Shaders.dll`: `21B3C85B6F2C001F13CB841DFE07E9D903EC1E5FDF010914EEE87DE0A490DBCD`

## RenderDoc

`renderdoc/inspect_frame564.rdc` and `renderdoc-inspection.json` contain a forced capture refresh, not a reuse frame. Inspection used RenderDoc 1.45; native reports supply the performance numbers.

| Event | Submitted groups | Pipeline/state |
| --- | ---: | --- |
| 4088, secondary depth | 41,703 | Full compacted mesh + `planar_capture_depth.frag`, reverse-Z GreaterEqual, depth writes enabled |
| 4103, secondary opaque | 41,703 | `forward_simple_full_input_compacted.mesh` + `forward_opaque_simple_full_input_ddgi.frag`, GreaterEqual, depth writes disabled |
| 4111, secondary transparency | 2,474 | Sorted forward transparency, GreaterEqual, depth writes disabled |

The former opaque submission was 83,725 groups. The captured frame excluded one reflector object, rejected 1,163 objects and 4,176 boundary meshlets, and submitted transparency independently of the main view. The main view had 1,600 transparent groups. RenderDoc reported no debug messages in this capture. The descriptor-isolation test separately ran with Vulkan validation enabled.

## Focused verification

Release build and production shader checks passed; 32 affected shader artifacts were compiled. There were no Release game build warnings or errors. The test project retains its unrelated existing `SampleDebugViewCycleTests` unreachable-code warning.

29 distinct focused cases passed across scoped runs:

- `SecondaryViewVisibilityTests`: reflected-only visibility, clip tolerance, conservative transformed meshlet bounds, filter expansion/cropped frustum and transparent order.
- `SecondaryViewResourceTests`: one GPU submission reads all eight secondary descriptor sets, including independent foliage outputs and frustum data, then confirms the main bindings remain intact. It also checks the recorded-slot overwrite guard.
- `AutomaticPlanarCaptureRasterTests`: production capture/depth fragments preserve nearest surviving depth, alpha holes, exclusions and sidedness; cubemap depth ignores planar clipping/exclusions. The production history compute shader marks only the cropped rendered rectangle valid.
- Existing capture pipeline, cubemap view and probe scheduler tests cover material interfaces/fallbacks and transactional face/publication behavior.

No full test suite, Nsight CLI or broad profiling campaign was run.

## Existing hybrid alternative

`hybrid-only.json` disables automatic planar captures while keeping the existing hybrid SSR/ray-query mode and its production budgets. Whole-frame GPU averaged **27.290 ms**, about **1.17 ms below** the first fixed-planar measurement. Its broad HDR comparison against `culled.pfm` passed (relative RMSE **0.0574**, FLIP P95 **0.00543**). Side-by-side preview images were inspected. Production-disabled detailed counters must not be interpreted as zero ray workload.

This is not evidence of mirror-quality equivalence. The Bistro bookmark contains little demanding mirror coverage. Screen hits inherit main-scene shading; missing hits use `HybridShadeHit`, which evaluates base material/emission, directional lights, at most four local light samples, DDGI and environment lighting. That is a different shading contract from the planar forward renderer, including its transparent scene content. Off-screen detail depends on the ray scene and budgets. Mirror stability and transparent/off-screen hit-shading parity therefore remain unproven, and the planar path stays enabled by default. No new tracer was implemented.

## Reproduction controls

- `NJULF_SECONDARY_VIEW_CULLING=0`: diagnostic uncull control; exclusions and content policy remain active.
- `NJULF_SECONDARY_VIEW_CROP=0`: full capture footprint.
- `NJULF_SECONDARY_VIEW_TRACE=1`: per-capture draw/rejection counts, footprint and CPU preparation time.
- `NJULF_SECONDARY_VIEW_RENDERDOC=1`: when injected, request one capture after startup and force the following planar refresh so the captured frame contains rendering work.
- `NJULF_AUTOMATIC_PLANAR_ENABLED=0`: hybrid-alternative comparison only.

Existing depth-prepass and specialization A/B controls remain available. Normal runs use the corrected pipelines, independent culling and conservative footprint calculation without tracing or capture instrumentation.
