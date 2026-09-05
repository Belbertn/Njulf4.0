# Automatic planar capture: depth and shader selection

## Measurement contract

The historical Bistro trace reports 93.059 ms for opaque capture and 93.928 ms
for the complete automatic-planar pass at 1600 x 900 (800 x 450 capture), using
ProfileSymbols and locked base clocks. It is the motivating trace, not the
denominator for the new measurements.

The local comparison uses the same current-source Release/Compile build,
RTX 3060 Laptop GPU and driver 610.62, Bistro Presentation, DdgiHigh,
`bistro-presentation` trajectory, async compute disabled, 120 warmup and 120
measurement frames. The benchmark forces 1920 x 1080 (960 x 540 capture);
an attempted initial window resize does not override it. Clocks are not locked.
The opaque visibility compute experiment is disabled. Exclusion encoding is
explicitly SortedList. Capture and reprojection timings are separated using
completed-frame lifecycle records, not averaged together.

Raw draw lists were explicitly retained with scene-submission validation in
the baseline: the old production instance path can omit these buffers while
still reporting their expansion capacities. Comparing against that missing
geometry would give a misleading performance result. The implementation now
retains populated, camera-independent records whenever the prepared scene
contains an automatic-planar receiver, without requiring validation mode.

Local artifacts are in `.perf-loop-runs/planar-depth-20260905/`, including the
initial tracked diff, source revision, command arguments, shader builds, GPU
reports, linear HDR images, and binary hashes for subsequent runs.

| Variant | Capture mean / p95 (ms) | Reproject mean (ms) | GPU frame mean (ms) |
| --- | --- | --- | --- |
| Original source, retained records (`baseline900`, actual 1080p) | 21.9719 / 22.376 | 0.2893 | 32.2251 |
| Capture depth writes, Full (`depth-write`) | 21.9598 / 22.169 | 0.3186 | 32.5384 |
| Reflected prepass, Full (`full-control-final`) | 6.9664 / 7.070 | 0.3137 | 29.3932 |
| Reflected prepass, specialized (`specialized-final`) | 5.9457 / 6.048 | 0.3067 | 29.3876 |

Each row above includes 24 capture and 96 reprojection samples. Depth writes
alone do not provide a measurable capture-time reduction in this pair. A
separate reflected-view prepass is therefore evaluated before specialization.
The changed reprojection cost also reflects its newly populated depth input.

The prepass saves 14.993 ms (68.3%) versus depth writes alone, including its
extra mesh submission, depth pass, barrier, color pass, depth-history copy and
prefilter. Mean GPU frame time improves 9.7%. It exceeds the required 5% and
0.05 ms capture win without the allowed 1% frame regression, so it is enabled
by default. `NJULF_AUTOMATIC_PLANAR_DEPTH_PREPASS=0` selects the independently
correct depth-writing path for comparisons.

The Full prepass HDR comparison against the depth-writing Full reference
passes the existing whole-image contract: relative RMSE 0.04149 (limit 0.12),
FLIP p95 0.007455 (limit 0.02). These are whole-image production thresholds,
not a claim of pixel equality. Both runs submit 83,725 raw opaque meshlets.

Specialization saves another 1.021 ms (14.7%) of capture time versus the final
Full control, with essentially unchanged whole-frame time. The selected Bistro
family is `CompactedSimpleFullInput`, using
`forward_opaque_simple_full_input_ddgi.frag.spv` and
`forward_simple_full_input_compacted.mesh.spv`. The matched comparison uses
the same rendering binary. Its HDR comparison against Full also passes:
relative RMSE 0.05331 and FLIP p95 0.006504. The complete capture improvement
against the matched original is 72.9%; this is not a comparison against the
historical 93 ms trace. Specialization is enabled by default;
`NJULF_AUTOMATIC_PLANAR_CAPTURE_SPECIALIZATION=0` retains the Full control.

The final control replaces the exploratory `prepass-full` result: Vulkan
validation exposed dynamic cull/depth setters on the statically configured
depth-only pipeline. Capture prepass recording now skips those setters.

The focused NUnit run passes 13 tests, including the production Full capture
and prepass fragment programs rendered into an 8 x 4 depth fixture. The fixture
checks nearest depth in both submission orders, alpha holes and survivors,
exclusion-only background, the clipped half-space, and single-sided rejection.
The pipeline tests exercise task/taskless family mapping, incomplete family
publication, late specialization after Full fallback, feedback selection, and
retirement. The new SPIR-V artifacts also pass `spirv-val` and the production
DDGI atomic/receiver audits.

The final related CPU suite passes 45 tests. The production Compile build
passes with zero errors and warnings; the test build has one existing
unreachable-code warning in `SampleDebugViewCycleTests`. A final production
stream validation run selects the expected capture shader pair and reports
no capture dynamic-state error. It still reports
`VUID-vkCmdDispatchIndirect-None-08114` for the C5 near-field residual
`depthHierarchy` descriptor, outside this capture change. This limits the
claim to capture validation rather than a clean application-wide validation
run. Earlier object exclusion remains an investigation only.

## Contracts implemented

- Captures clear reverse-Z depth to zero and own depth writes after alpha,
  material-sidedness, exclusion, and half-space discards. They do not force
  early fragment tests while writing depth. Procedural and authored foliage
  also use capture pipelines with depth writes.
- The optional prepass uses the same Full mesh producer, forward material
  decoder, transformed UVs, vertex alpha, sidedness and exact planar clipping.
  A depth-write/read barrier precedes the color pass, which loads depth, uses
  GreaterOrEqual, and does not write depth. Foliage retains its own depth-writing
  pass; transparency tests scene depth without contributing opaque depth.
- Simple, SimpleFullInput and Full requests preserve their material interface
  and active task/taskless stage. Full material buckets stay Full. The bank
  publishes complete color/feedback pairs and never caches a temporary Full
  fallback. All capture color programs retain ordinary DDGI directional
  radiance; main-view hybrid receiver programs are not used for captures.
- Raw counts come from populated arrays, independently of main-view GPU
  expansion capacity. The capture task program does not read the main camera's
  frustum or Hi-Z data. Existing depth-history capture and reprojection consume
  the surviving geometry depth; untouched background stays zero.

## Object exclusions: investigation only

The measured Bistro slot contains one excluded object, represented by one
SortedList payload word. The frame uses 220 of 1024 metadata bank words, with
no metadata-capacity rejection. The optional existing bitset encoding is not
enabled by this change. The fragment helper performs an exact list scan before
material coverage and lighting. Its cost has not been isolated from clipping,
material decoding, rasterization and lighting, so no standalone exclusion-time
claim is made.

The first useful follow-up is an exclusion check immediately after the raw draw
command supplies `InstanceId`, before meshlet/vertex fetch and rasterization.
For the taskless path this can be a uniform mesh-workgroup rejection that emits
zero geometry; the task path can reject before emitting mesh work. Both must
use the existing frame/slot metadata and exact exclusion representation. This
removes raster work for an excluded receiver instead of merely making its
per-fragment lookup cheaper. A per-capture compute-filtered draw stream is a
larger alternative if repeated per-meshlet checks remain costly; it needs
separate counts, descriptors and lifetime ownership for every reflected view.

Keep the exact per-fragment half-space clip for triangles crossing the plane.
Only a conservative whole-object/meshlet bound can move that decision earlier.
Do not reuse the bounded receiver-membership list helper for exclusions: the
exclusion helper intentionally has no 256-entry truncation. A future experiment
must measure complete capture and whole-frame cost with unchanged geometry,
lighting, exclusions and capture cadence, and include an intersecting-plane
case. No earlier-stage exclusion optimization is implemented here.
