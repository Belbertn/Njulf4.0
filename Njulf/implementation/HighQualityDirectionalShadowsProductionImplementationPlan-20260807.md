# High-Quality Directional Shadows Production Implementation Plan

- Date: 2026-08-07
- Status: implemented through the deterministic CSM, hard-ray, and hybrid-contact slices; hardware promotion remains evidence-gated
- Target: stable cascaded sun shadows for every quality tier, with an optional
  ray-query hard/soft shadow path for qualified Ultra hardware
- Expands: `implementation/Shadows.md`
- Related but independent work: DDGI cohort/history stabilization. Replacing a
  direct-shadow backend must not be presented as a fix for DDGI transitions.

## Implementation outcome (2026-08-13)

The approved initial-production scope is implemented:

- CSM fitting now uses renderer-owned transported-basis state, a rotation-
  invariant guarded sphere, symmetric nearest-texel snapping, and independently
  hysteretic/outward-quantized depth. Split overlap and reverse-Z sampling remain
  compatible with the existing forward path.
- Directional settings now serialize requested mode, filter/bias policy, split
  lambda, conservative caster extrusion, and contact-ray distance. Legacy files
  migrate without silently opting into ray queries.
- Static cache validity is tracked and refreshed per cascade. Current-submission
  recording is distinct from post-submit durability, and dynamic/foliage depth
  is recomposed correctly after skipped full-ray frames.
- Acceleration-structure preparation is a union of active consumers and works
  when DDGI is disabled. Directional readiness fails closed on missing category
  semantics, culled static instances, rejected BLAS allocations, current-pose
  proxy fallback, excluded foliage, or deferred dynamic builds.
- A graphics-queue compute pass after visible depth writes a full-resolution,
  double-buffered packed `R8Unorm`-equivalent mask. It reconstructs a closest-
  depth geometric normal, applies bounded large-coordinate-safe offsets, uses a
  dedicated TLAS instance mask, shares deterministic alpha-candidate semantics,
  caps candidate traversal conservatively, and skips background/far receivers.
- `RayQueryHard` consumes the mask once for supported opaque depth owners.
  `HybridContact` conservatively combines it with CSM and fades the final 20% of
  the bounded ray segment. Transparent/decal/debug receivers retain an explicit
  named CSM fallback, and scenes without those receivers skip redundant map work.
- Requested/effective state, stable fallback reason/detail, map/cache fitter
  provenance, mask format/extent/bytes/generation, ray-scene generation/epoch,
  and separate map/ray GPU timings are available in runtime diagnostics.

The two evidence-gated phases remain intentionally inactive, as this plan
requires: Phase 2 does not allocate a CSM temporal resolve without measured
residual stepping, and Phase 6 `RayQuerySoft` resolves to deterministic CSM
without preparing ray-scene, mask, motion, or history resources until finite-sun
sampling and denoising qualify. All ray modes remain opt-in pending release-build
visual, validation-layer, and target-GPU budget captures.

## 1. Decision

Ship the work in two independently promotable tracks:

1. Make the existing cascaded shadow map (CSM) path deterministic and stable.
   This remains the default and fallback path on all quality tiers. The change
   keeps the current cascade count, overlap/blending, static/dynamic/foliage
   rendering, PCF sampling, and direct-light integration.
2. Add ray-query directional visibility as an optional Ultra path. Introduce it
   first as a full-resolution hard-shadow mask, then add finite-sun soft shadows
   only after hard-shadow correctness and cost are certified.

The ray-query path is a requested/effective mode, not an unconditional preset
switch. It may become effective only when all of the following are true for the
current frame:

- the device and selected queue support the required Vulkan features;
- the acceleration structure is current and complete for the requested shadow
  distance;
- every admitted caster class has defined and tested material/deformation
  semantics;
- camera-visible receivers have a valid way to consume the result;
- the measured GPU and memory cost stays within the active render budget.

If any gate fails, use CSM for that frame and expose the reason in diagnostics.
Never treat absent, stale, or partially resident acceleration-structure content
as unoccluded space.

The first production target is therefore:

```text
All tiers       -> stabilized CSM
High / Ultra    -> optional CSM + bounded ray-query contact shadows
Qualified Ultra -> optional full-resolution ray-query hard sun shadows
Future Ultra    -> optional finite-sun sampling plus temporal/spatial denoising
```

Virtual shadow maps and ReSTIR DI are outside this plan. They are much larger
systems and do not address this engine's immediate one-sun stability problem as
directly as stable CSM plus a shared ray-query path.

## 2. Current implementation evidence

| Area | Current behavior | Consequence for this plan |
| --- | --- | --- |
| `Njulf.Rendering/Data/DirectionalShadowDataBuilder.cs` | Chooses `UnitY` or `UnitZ` as the light-view up vector at an absolute-dot threshold of `0.95`. | A moving sun can cross the threshold and abruptly rebuild the light basis. The builder must become stateful, or consume explicit persistent stabilization state. |
| Cascade fitting | Fits a light-space AABB and makes its XY dimensions square using the larger projected dimension. | The extent can still change as the camera frustum rotates relative to the light basis. Use a rotation-invariant sphere/fixed extent. |
| Texel snapping | Applies `MathF.Floor` to light-space center coordinates. | Movement is directionally biased and the discontinuity occurs at an asymmetric boundary. Use explicit round-to-nearest semantics. |
| Cascade depth | Expands projected corner depth using a large caster padding supplied by the caller. | Depth precision and changes are coupled to the instantaneous fit. Stabilize Z independently and keep it from changing XY coverage. |
| Cascade transitions | Split overlap and blend data are already generated and consumed. | Preserve the existing split/transition contract; add regression tests rather than replacing it. |
| `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs` | Maintains static-cache and working D32 array images, then adds dynamic and foliage casters. | Preserve this architecture. Improve it with per-cascade dirty signatures after matrices stabilize. |
| `Njulf.Shaders/forward.frag` | Selects/blends cascades, performs rejection/fallback, and manually compares PCF taps. | CSM remains inline for the initial stabilization. A screen-space resolve is introduced only if temporal stabilization is proven necessary. |
| `Njulf.Rendering/Data/RenderSettings.cs` | Exposes map size, cascade count/distance, blend, raster/normal bias, and PCF radius. Ultra currently increases CSM quality and uses SMAA High. | Add an explicit requested shadow mode. Do not assume Ultra has motion vectors merely because a future shadow filter needs them. |
| `Njulf.Rendering/Resources/AccelerationStructureManager.cs` | Builds useful BLAS/TLAS and material metadata, but lifecycle, residency, metrics, and naming are DDGI-oriented. | Generalize ownership to a union of consumers. Shadow readiness must not depend on DDGI being enabled. |
| Existing ray-query geometry policy | Supports static/rigid and alpha candidates, uses bind-pose proxies for skinned meshes, and excludes foliage and ordinary blended transparency. BLAS caching is mesh-oriented. | This is sufficient for a developer prototype, not a production full-scene Ultra mode. Each gap is a promotion blocker. |
| `Njulf.Shaders/ddgi_simple_trace.comp` and DDGI hit-shading includes | Demonstrate TLAS binding, candidate iteration, alpha coverage, and optional thin-surface traversal. | Extract generic material/candidate helpers; do not duplicate or couple direct shadows to DDGI state. |
| Motion-vector lifecycle | Motion vectors are currently required by effective TAA, while the Ultra preset uses SMAA High. | Make motion-vector allocation/execution consumer-driven before enabling any temporal shadow filter. |
| Production render graph | Directional shadow maps run before the depth prepass; depth and motion are available before forward lighting. | Insert a ray shadow-mask pass after visible-surface depth (and motion when needed) and before forward lighting. |
| Diagnostics and editor | CSM runtime counters/debug views and reflection-driven shadow settings already exist. | Extend these surfaces with stabilization, readiness, fallback, traversal, and history information. |

## 3. Quality contract

The implementation is complete only when it meets all of these invariants.

### 3.1 CSM invariants

- Smooth camera or light motion cannot cause a light-basis reset at an arbitrary
  direction threshold.
- Camera rotation cannot change an otherwise identical cascade XY extent.
- Sub-texel motion keeps a cascade center fixed until the nearest texel boundary
  is crossed; positive and negative motion behave symmetrically.
- Cascade depth changes never alter XY coverage.
- All receiver corners and the complete PCF footprint stay inside the selected
  cascade.
- Existing split overlap, cross-cascade blending, rejection fallback, static
  caching, dynamic casters, foliage casters, and reverse-Z comparisons remain
  functional.
- Stabilization cannot trade shimmer for clipped casters, acne, or excessive
  peter-panning.

### 3.2 Ray-query invariants

- A ray shadow is evaluated against current geometry, not a stale TLAS or a
  bind-pose shape presented as exact animation.
- Residency is conservative for the entire tested ray segment. Incomplete
  residency selects CSM; it never produces a lit result.
- Opaque, alpha-mask, double-sided, skinned, foliage, and explicitly supported
  transparent materials have written semantics and targeted tests.
- A deterministic one-ray hard-shadow path has no temporal filter and no random
  noise.
- Finite-sized-sun sampling is a separate mode with explicit history resources,
  rejection, and denoising.
- Enabling ray shadows while DDGI is disabled works. Enabling DDGI and ray
  shadows together shares infrastructure without duplicate BLAS ownership.
- Unsupported hardware, device loss, scene rebuild, resize, mode transition, or
  an invalid light causes a deterministic, diagnosed fallback.

### 3.3 Performance invariants

- Stabilized CSM does not increase map resolution or cascade count implicitly.
- Static-cache work is no worse than the current path and should decrease during
  camera/light motion that no longer changes a cascade matrix.
- Full-screen ray work is skipped for background pixels and receivers that
  cannot receive the selected sun light.
- CSM and full ray shadows are not both rendered indefinitely unless a named
  hybrid/transparent fallback requires both.
- Shipping decisions use release-build GPU timestamps and the repository's
  render-budget profiles. Example performance figures from external articles
  are architectural context, not Njulf budgets.

## 4. User-facing modes and fallback model

Add a serialized `DirectionalShadowMode` with these values:

| Mode | Maps | Camera-space rays | Intended use |
| --- | --- | --- | --- |
| `Cascaded` | Full existing CSM | None | Default on every tier and universal fallback. |
| `HybridContact` | Full existing CSM | Bounded near-camera first-hit rays | Optional High/Ultra improvement for contact detail and nearby cascade limitations. |
| `RayQueryHard` | Disabled when the full ray path is qualified; retained only for an explicit receiver/caster fallback | One deterministic sun ray per visible pixel | Optional qualified Ultra path. |
| `RayQuerySoft` | Same fallback policy as hard mode | Stochastic finite-sun rays plus history/denoising | Later Ultra feature; not part of initial promotion. |

Keep requested and effective state separate:

```text
RequestedDirectionalShadowMode
EffectiveDirectionalShadowMode
DirectionalShadowFallbackReason
DirectionalShadowReadinessGeneration
```

The effective-state decision must happen early enough that the same frame can
prepare and render CSM when ray readiness fails. Continue building stable CSM
data while a ray mode is requested so fallback does not begin with an
unstabilized or uninitialized basis.

Suggested fallback reasons include unsupported feature/format, no shadow-casting
directional light, TLAS unavailable, TLAS stale, residency incomplete, skinned
geometry unqualified, foliage unqualified, material semantics unqualified,
transparent receiver fallback unavailable, resource creation failure, and GPU
budget demotion. The editor and performance capture must report the concrete
reason rather than only `RayQuery=false`.

Do not silently change existing saved scenes to ray queries. Initially, all
presets request `Cascaded`; expose the new modes as opt-in. After certification,
the Ultra preset may request `RayQueryHard` on capable hardware while preserving
an explicit user override and CSM fallback.

## 5. Phase 0 - Freeze baselines and add observability

### 5.1 Deterministic capture scenarios

Add scripted captures before changing the fitter:

- fixed camera while the sun direction crosses both sides of the current
  `abs(dot(light, UnitY)) == 0.95` boundary;
- a full sun azimuth sweep at low, medium, near-zenith, and near-nadir elevation;
- camera translations of `0.1`, `0.49`, `0.51`, `1.0`, and `1.5` cascade texels in
  positive and negative light-space X/Y;
- stationary camera position with yaw/pitch/roll sweeps;
- motion through every cascade overlap and beyond maximum shadow distance;
- a tall off-camera caster whose shadow enters the visible frustum;
- static, rigid-dynamic, skinned, alpha-mask, authored foliage, procedural grass,
  double-sided, and transparent test objects;
- large world coordinates and a camera teleport;
- DDGI off, DDGI on with raster tracing, DDGI on with ray queries, and ray shadows
  without DDGI;
- 1080p, 1440p, and 4K at each relevant AA mode.

Store the camera/light tracks and exact settings beside the rendering tests so
the old and new implementations receive identical input.

### 5.2 CSM diagnostic additions

Extend `DirectionalShadowRuntimeDiagnostics` and performance JSON with, per
cascade:

- basis generation/reset count and reset reason;
- normalized light forward/right/up and orthogonality error;
- raw sphere radius, guarded/final extent, usable texel count, and world texel
  size;
- unsnapped center, snapped integer texel coordinates, and snap delta;
- requested and stabilized near/far depth bounds and depth quantum;
- matrix generation and static-cache dirty reason/mask;
- transition near/far and overlap width;
- PCF footprint rejects and cascade fallbacks already counted by the shader.

Record GPU timestamps for static shadow rendering, dynamic rendering, foliage,
cache copy, and total directional shadow work where timestamp capacity permits.

### 5.3 Baseline artifacts and thresholds

For each deterministic track, capture:

- beauty output and existing cascade/receiver-factor debug views;
- per-frame matrices and new diagnostic fields;
- static-cache redraw masks;
- release-build CPU and GPU medians plus high percentiles after warm-up;
- validation output and device/driver identity.

Freeze numeric image-difference and timing thresholds from these artifacts
before promotion. Do not invent a universal millisecond allowance without a
target GPU. The active `RenderBudgetProfile`, including the existing Ultra 4K
profile, remains authoritative.

**Phase 0 exit:** a test can reproduce the current basis reset and quantify
matrix jumps, texel stepping, cache redraws, and frame cost.

## 6. Phase 1 - Stabilize CSM without changing its shading architecture

### 6.1 Introduce persistent light-basis state

Create a small state object, for example
`DirectionalShadowStabilizationState`, owned by `VulkanRenderer` or a dedicated
fitter instance. Refactor the static-only builder API so the previous valid
basis is an explicit input/output rather than hidden global state.

State includes:

- stable identity of the selected directional light;
- previous normalized light direction;
- previous right/up vectors;
- validity and generation;
- per-cascade stabilized depth state;
- the settings/camera-projection identity needed to invalidate only relevant
  cached values.

Reset it only for a renderer lifecycle reset, a changed selected-light identity,
invalid/non-finite input, or a genuine discontinuous light teleport. Do not
reset it for ordinary camera movement, a harmless light-list reorder, or a
cascade cache miss. If the scene currently lacks a stable light handle, add one
rather than treating an array index as identity.

For the first valid frame:

1. Normalize the light direction and reject zero/non-finite input.
2. Select the least-aligned canonical axis from X/Y/Z.
3. Construct a normalized right vector and the matching up vector with a fixed
   handedness.

For subsequent frames, parallel-transport the prior basis:

1. Compute the minimal rotation from the previous light direction to the new
   direction.
2. Apply it to the previous right/up vectors.
3. Project one transported axis onto the plane perpendicular to the new light
   direction, normalize it, and reconstruct the other axis with a cross product.
4. Sign-align against the transported basis and verify handedness.
5. Handle only the numerically antiparallel case with a deterministic axis based
   on the prior basis; record this as a discontinuity diagnostic.

Use double precision for CPU-side dot products, projected centers, and snapping
when practical, then convert the final matrix to GPU floats. Assert finite,
orthonormal output in Debug builds.

This removes the threshold switch without freezing sun movement: the basis
continues to rotate by the smallest change required by the moving light.

### 6.2 Fit rotation-invariant XY coverage

For each already-overlapped cascade slice:

1. Generate the same eight world-space frustum corners used by the current split
   system.
2. Compute a stable slice center. The average of the eight corners is acceptable
   if tests confirm invariance; an analytic frustum-slice center is preferable
   when available.
3. Set the raw radius to the maximum world-space distance from the center to any
   corner. Because distances are measured before projection, camera rotation and
   transported light-basis rotation cannot change the radius.
4. Quantize the radius upward to suppress floating-point breathing. Cache/fix it
   while FOV, aspect, near plane, cascade split depths, map size, and filter
   footprint are unchanged.
5. Reserve a texel guard band for the full manual-PCF footprint and bilinear
   neighborhood. Compute the usable resolution as map size minus both guards and
   expand the final radius so receiver coverage is unchanged.
6. Use one square orthographic XY extent from the guarded radius. Do not derive
   width/height from a projected AABB.

The guard size must be derived from the actual unique PCF sample footprint in
`forward.frag`, including the radius-zero four-texel case. Add a test that
projects every receiver corner and expands it by every possible PCF offset; no
tap may rely on texture-edge clamping.

### 6.3 Snap the center to the nearest texel

Project the cascade center onto the transported light right/up axes and compute:

```text
worldTexelSize = (2 * guardedRadius) / shadowMapResolution
snappedTexel   = round(projectedCenter / worldTexelSize)
snappedCenter  = snappedTexel * worldTexelSize
```

Use an explicit midpoint rule with symmetric positive/negative behavior, such as
`MidpointRounding.AwayFromZero`. Do not depend on the platform's implicit default.
Reconstruct the world/light-view center from the snapped XY coordinates while
leaving its light-direction coordinate to the separate depth policy.

Tests must prove:

- less than half-texel movement does not change the snapped center;
- crossing a boundary moves exactly one integer texel;
- equivalent positive and negative tracks have mirrored results;
- no frame emits a fractional snapped-texel coordinate beyond numeric epsilon.

### 6.4 Stabilize depth independently

Do not reuse instantaneous XY min/max values to define Z. Maintain two explicit
intervals:

- receiver depth: contains all overlapped frustum corners;
- caster search depth: expands toward the light far enough to contain shadow
  casters that can project into the receiver sphere.

The first production implementation should remain conservative:

1. Derive receiver depth from the sphere/corners in the stabilized light basis.
2. Apply an explicit caster-extrusion setting with the existing behavior as the
   compatibility default; rename the concept if necessary so it is not confused
   with camera shadow distance.
3. Add a small receiver guard on the opposite side.
4. Quantize the near bound outward and the far bound outward using a per-cascade
   depth quantum.
5. Expand immediately when required. Contract only after a short stability
   window or at a bounded rate, and never past the current conservative target.
6. Build the reverse-Z orthographic projection from these stabilized bounds.

Depth history and its generation are independent from basis and XY generations.
A Z-only change must not invalidate the snapped XY center. Document the sign
convention with a unit test because the current projection reverses near/far.

After the conservative version is correct, an optional caster-aware tightening
may project scene/caster bounds that overlap the guarded XY sphere. It is allowed
only if it demonstrably improves precision and cannot exclude an off-camera
caster. It must preserve outward quantization/hysteresis and is not a blocker for
the first stable release.

### 6.5 Preserve transitions and sampling behavior

Keep these contracts unchanged in Phase 1:

- split-lambda calculation and configured maximum shadow distance;
- overlap expansion and `CascadeTransitionData` layout;
- cascade selection and blend weights in `forward.frag`;
- out-of-map/depth rejection and fallback to another cascade;
- DDGI far-field sun-shadow fallback beyond the last map, where currently valid;
- manual depth comparison before filtering;
- static, dynamic, and foliage caster passes;
- current reverse-Z compare and depth-bias sign.

Add before/after tests for split values and blend weights so stability work
cannot subtly narrow overlap or introduce a seam.

### 6.6 Make bias scale predictably

Once the stabilized extent is in place, add an opt-in world-texel-scaled bias
mode. Derive per-cascade normal and raster bias from `worldTexelSize`, surface
slope, and bounded user multipliers. Keep legacy manual values for compatibility
until scene comparisons establish new preset defaults.

Requirements:

- bias changes smoothly across cascades and participates in cascade blending;
- normal bias uses the geometric receiver normal, not a normal-map perturbation;
- bias is clamped in world units to prevent large far-cascade detachment;
- raster bias is set per cascade when texel sizes differ;
- acne and peter-panning scenes are captured at grazing and overhead sun angles.

Do not add stochastic PCF or change filter shape in the same patch as the
stabilizer. That would obscure whether a regression comes from fitting or
filtering.

### 6.7 Use per-cascade cache invalidation

Replace the global static-cache decision with per-cascade signatures containing
only inputs that affect that cascade: matrix generation, relevant settings,
static caster/scene revision, map identity, and rendering-policy generation.
Reuse the existing valid-cascade mask to refresh only dirty layers.

Dynamic and wind-driven foliage layers continue to render at their required
frequency. Report a dirty bitmask and per-layer reason. Stable camera motion
should now leave far-cascade static layers valid for long stretches rather than
redrawing all layers because any byte in `GPUShadowData` changed.

### 6.8 Phase 1 automated tests

Extend `Njulf.Tests/DirectionalShadowDataBuilderTests.cs` and add focused fitter
tests for:

- a dense sun-direction sweep through the old `0.95` threshold;
- full azimuth/elevation sweeps including pole and antiparallel handling;
- finite, orthonormal, fixed-handedness bases;
- maximum per-frame basis rotation bounded by the input light rotation plus
  numeric tolerance during continuous tracks;
- constant radii/extents under camera yaw, pitch, and roll;
- round-to-nearest behavior and negative-coordinate symmetry;
- sub-texel invariance and exact integer-texel boundary changes;
- all overlapped corners plus PCF guards inside clip/UV bounds;
- conservative depth containment, outward quantization, hysteresis, and
  reverse-Z mapping;
- reset only on documented lifecycle/light events;
- unchanged cascade splits, overlap, blend widths, selected-light indices, and
  strength;
- per-cascade signature invalidation;
- serialization/clamping and editor coverage for new settings;
- GPU struct layout/offset stability if any shared struct changes.

**Phase 1 exit:** the threshold-reset track has no matrix discontinuity; camera
rotation does not breathe the extent; translation changes XY only on symmetric
nearest-texel boundaries; no coverage/bias regression is visible; and release
performance remains within the frozen budget.

## 7. Phase 2 - Evidence-gated short CSM temporal resolve

This phase is optional. Run the Phase 1 trajectories first. If pixel stepping is
no longer objectionable, record that result and omit the feature.

If a signed-off comparison still shows objectionable one-texel stepping, move
the resolved CSM factor for opaque visible surfaces into a scene-resolution
shadow-mask pass. Keep the existing map rendering and cascade evaluation logic;
only its resolved result becomes a texture consumed by forward lighting.

Use a deliberately short history, normally two to four accepted samples, with:

- motion-vector reprojection;
- previous/current depth rejection;
- geometric-normal rejection;
- camera-cut, resize, dynamic-resolution, light-change, mode-change, and matrix
  generation resets;
- rejection when either sample has an invalid cascade projection/fallback state;
- neighborhood min/max or variance clamping;
- reduced history at contact edges and disocclusions;
- a small maximum history weight so a newly moving shadow cannot visibly lag.

Generalize motion-vector demand from `TAA only` to a consumer bitset such as
`TAA | DirectionalShadowTemporal | DirectionalShadowDenoiser`. Ultra's SMAA
configuration must allocate and execute motion vectors when a shadow consumer
needs them.

Do not filter sorted or weighted transparent receivers through an opaque-depth
history. They remain on their existing direct path until a dedicated policy is
implemented.

Expose rejection-reason counters and a history-confidence debug view. Reject
this phase if it reduces stepping only by creating trails, detached contacts, or
light leaks.

**Phase 2 entry:** Phase 1 is accepted and a repeatable residual stepping metric
fails its frozen threshold.

**Phase 2 exit:** the metric passes; disocclusions converge within the configured
short age; no history survives a reset; and AA modes without TAA work correctly.

## 8. Phase 3 - Generalize the acceleration structure into shared infrastructure

Do this before exposing any full ray-shadow mode outside development builds.

### 8.1 Consumer-driven preparation

Replace the current DDGI-only enable condition with a requirement union, for
example:

```text
RayQueryConsumers = Ddgi | DirectionalContact | DirectionalFull
```

Each consumer supplies:

- required ray distance/coverage volume;
- geometry/material categories;
- current-pose requirements;
- update freshness;
- queue stages that read TLAS/metadata;
- whether incomplete coverage permits a local fallback.

`VulkanRenderer.PrepareAccelerationStructures` computes the union even when GI
is disabled. BLAS ownership remains shared. Start with one union TLAS and reserve
Vulkan instance-mask bits for GI and directional shadows so each query ignores
irrelevant instances. If benchmarks show that the larger union TLAS hurts both
consumers, keep shared BLAS but permit separate TLAS instances as an evidence-
based optimization.

Rename or wrap DDGI-specific metadata/diagnostics that are actually generic,
while retaining DDGI-specific counters separately. Update barriers so TLAS and
metadata visibility covers compute ray-mask reads and, later, fragment reads for
transparent receivers.

### 8.2 Shadow residency contract

Define coverage as a spatial contract, not merely an allocation target:

- `HybridContact` requires complete admitted geometry for every ray segment in
  its bounded contact volume.
- `RayQueryHard/Soft` requires complete admitted geometry from every visible
  receiver to the computed directional-ray exit distance.
- Directional `TMax` comes from the ray's intersection with qualified scene/
  residency bounds, capped by an explicit shadow-distance policy. Do not use an
  arbitrary huge constant and assume geometry is resident.
- Any evicted, pending, failed, or stale region invalidates readiness for the
  affected mode before shading.
- A GI far-field representation is not a valid substitute for missing direct
  shadow casters unless a separate, tested direct-shadow approximation is
  explicitly selected.

For full mode, prefer failover of the whole directional shadow path for the
frame over spatial patches of incorrectly lit pixels. A future tiled fallback
may be considered only if it has conservative coverage, seam-free compositing,
and its own debug view.

### 8.3 Geometry and material qualification matrix

Track readiness by category:

| Category | Required implementation before full-mode promotion |
| --- | --- |
| Static opaque | Shared cached BLAS; opaque instance mask; first-hit fast path. |
| Rigid dynamic | Current transform in TLAS for the same rendered frame; update/rebuild generation checked before dispatch. |
| Alpha mask | Non-opaque candidate handling using the authoritative material alpha rule, texture, cutoff, UV transform, sidedness, and a stable ray-footprint LOD. |
| Double-sided | Match raster material semantics rather than relying on an unconditional hardware backface-cull flag. |
| Skinned | BLAS built/refit from current skinned positions. A bind-pose proxy is allowed only in a named debug prototype, never as qualified exact shadows. Cache keys must include deformation stream/pose ownership rather than only mesh handle. |
| Authored foliage | Shared prototype geometry plus current instances, alpha rules, and the same wind/deformation time as visible/shadow rendering, or a measured conservative representation with an explicit quality label. |
| Procedural grass | Bounded generated geometry/BLAS update or a documented raster foliage subpath. It cannot be silently absent. |
| Ordinary blended transparency | Explicitly non-occluding unless the material opts into a supported shadow model. Do not convert alpha blend to random binary occlusion. |
| Thin transmission | Later optional RGB/transmittance traversal with a bounded layer count and matching receiver format; not required for the initial scalar hard mode. |
| Geometry decals | Follow the underlying receiver; do not add duplicate occluder geometry unless the material model explicitly requires it. |

Extract generic GLSL helpers from the DDGI candidate code for triangle/material
lookup, sidedness, and alpha coverage. Keep DDGI telemetry and transport policy
outside the shared include. Primary-shadow alpha LOD must be derived from a
stable ray/pixel footprint; do not blindly copy a DDGI fixed-LOD approximation.

### 8.4 Animated-geometry ordering

Define an explicit frame dependency:

```text
animation/wind state
    -> current deformation buffers
    -> BLAS refit/rebuild
    -> TLAS update
    -> ray shadow mask / transparent receiver rays
    -> forward lighting
```

Stamp each stage with a generation/frame identity. The mask pass verifies that
the TLAS generation matches the visible scene state. Measure refit quality and
schedule periodic rebuilds only if traversal degradation is demonstrated.

**Phase 3 exit:** the AS can be prepared with DDGI off; consumer masks and
barriers are validated; incomplete/stale coverage reliably selects CSM; and all
category readiness is visible in diagnostics.

## 9. Phase 4 - Full-resolution hard ray-query shadow mask

### 9.1 Resources and graph placement

Add a render-graph resource such as `DirectionalShadowMask` and a
`DirectionalRayShadowPass` after the visible-surface depth prepass and after
motion vectors when a temporal consumer is active, but before forward lighting.
Declare every dependency explicitly: scene depth, camera/environment/light data,
TLAS, instance metadata, geometry/material buffers, bindless alpha textures, and
the output mask.

Use a full-resolution `R8Unorm` mask for the deterministic scalar hard mode
(`1 = visible`, `0 = occluded`). Soft/history modes may select `R16Sfloat` and
additional hit-distance/moment resources at target recreation. Keep the graph's
logical resource identity stable across format variants.

Run the first version on the graphics queue in compute dispatches to avoid
premature cross-queue ownership complexity. Promote to asynchronous compute only
after timestamps show useful overlap and all depth/TLAS/mask transfers pass
validation.

### 9.2 Receiver reconstruction

For each pixel:

1. Detect background using the same reverse-Z convention as the depth/AO paths
   and write visible without tracing.
2. Reconstruct the current world position from scene depth and inverse matrices.
3. Reconstruct a conservative geometric normal with the existing closest-depth
   derivative technique, not a normal-mapped shading normal.
4. Offset the origin using bounded world-scale normal and light-direction terms;
   set a nonzero `TMin` to prevent self-hits.
5. Skip the ray when the selected sun cannot contribute to the receiver.

Add silhouette, thin-geometry, grazing-angle, and large-coordinate tests. If
depth-derived normals cannot meet the self-hit/leak threshold, add an optional
octahedral `R16G16Snorm` geometric receiver-normal target to the depth prepass.
That becomes mandatory before hard-mode promotion rather than hiding failures
with a larger global epsilon. A true receiver-normal target is mandatory for the
later soft denoiser if depth reconstruction is not demonstrably equivalent.

### 9.3 Query algorithm

Dispatch in measured workgroups, initially `8x8`. Cast from the reconstructed
receiver toward the selected sun using the same light direction as CSM and
direct lighting.

- Use first-hit termination and skip procedural AABBs for a fully opaque-only
  query/mask where valid.
- When the shadow instance mask includes non-opaque triangles, iterate
  candidates, evaluate sidedness/alpha coverage, confirm the first blocking
  intersection, and terminate.
- Use an opaque instance mask/partition to retain the fast path when profiling
  justifies a split dispatch or query.
- Bound candidate/layer work and diagnose cap hits. A cap hit must use a
  conservative documented result, not silently return visible.
- Use stable alpha LOD and no per-frame randomness in hard mode.
- Store optional hit distance only for diagnostics or a later soft filter; do
  not inflate the shipping hard mask without evidence.

The Vulkan ray-query tutorial documents the intended first-hit directional
shadow pattern, ray epsilon, first-hit flags, alpha-candidate option, and the
need to update TLAS for moving geometry. Treat that as API guidance; repository
tests remain the correctness authority.

### 9.4 Lighting integration

For opaque/decal visible surfaces, sample `DirectionalShadowMask` by the current
pixel and multiply the selected directional light exactly once. Preserve
unshadowed behavior for other directional lights unless/until the engine
supports multiple shadowed suns.

In full ray mode, skip directional map rendering only after readiness is known.
Keep stable CSM preparation cheap and available for same-frame fallback.

Camera-space opaque depth does not represent transparent receiver surfaces.
Use this staged policy:

1. The developer prototype may retain CSM for sorted/weighted transparent
   receivers and must label the effective path as hybrid.
2. Before `RayQueryHard` is promoted as a full production mode, compile a
   ray-query-capable transparent forward variant that calls the same shared
   directional-visibility helper at the transparent fragment's world position.
3. Extend descriptor layouts and TLAS/metadata barriers to fragment shader
   stages only for that variant.
4. If a transparent pipeline cannot query correctly, select whole-frame CSM or
   expose an explicitly named hybrid mode; do not show an unshadowed receiver.

### 9.5 Hard-path diagnostics

Add:

- requested/effective mode and fallback reason;
- mask resolution/format and dispatch dimensions;
- rays issued/skipped, hit/miss rate, average/max candidates, alpha texture
  samples, cap hits, and invalid receiver count;
- TLAS/visible-scene generation match;
- resident coverage distance/volume and category completeness;
- GPU timestamps for AS updates, mask trace, and forward consumption;
- debug views for mask, hit distance, candidate count, residency, CSM-versus-ray
  difference, and fallback tiles/frame state.

Detailed counters may use sampled or debug builds, but shipping timing captures
must run without counter-induced serialization.

**Phase 4 exit:** static, rigid, alpha-mask, and qualified animated/foliage test
scenes match the agreed reference; ray shadows work with DDGI off; transparent
receivers follow the declared policy; no partial residency appears as light; and
the path fits the frozen Ultra budget.

## 10. Phase 5 - Optional hybrid contact shadows

This is independently useful and can ship before full-scene ray semantics are
complete.

- Keep stabilized CSM for all general directional visibility.
- Trace only within a configurable near-camera/contact distance for receivers
  whose bounded ray segment has complete AS coverage.
- Combine conservatively, normally `min(csmVisibility, rayVisibility)`, so the
  contact ray adds missing nearby occlusion rather than erasing a map shadow.
- Fade the ray contribution over an end band to avoid a visible radius ring.
- Default to full resolution for the high-quality setting. Half-resolution or
  checkerboard contact rays are separate scalability options that require edge
  reconstruction tests.
- Disable the contact contribution as a unit when near-field residency is
  incomplete; retain CSM and report the reason.

This path is a practical place to validate ray origin, alpha masks, rigid
dynamics, infrastructure sharing, and cost before full-mode promotion. It does
not excuse missing skinned/foliage semantics in a mode advertised as full ray
shadows.

**Phase 5 exit:** the fade is invisible in motion, CSM cannot be weakened by the
combine operation, near-field residency gates are conservative, and cost is
acceptable on the intended High/Ultra profiles.

## 11. Phase 6 - Optional finite-sun soft shadows

Start only after the deterministic hard path is accepted. Use the existing
environment sun angular-radius value as the physical control.

### 11.1 Sampling

- Sample a direction over the sun disk/cone using a blue-noise-scrambled low-
  discrepancy sequence.
- Use one ray per pixel per frame as the initial performance target, with a
  configurable multi-ray recovery path for invalid history/captures.
- Rotate/scramble by pixel and frame without creating stationary patterns.
- Preserve an angular radius of zero as an exact deterministic hard-shadow
  mode.
- Record visibility and, if useful for filter radius/rejection, blocker distance.

### 11.2 Temporal accumulation

Allocate current/history visibility and moments only while the mode is active.
Require motion vectors independent of AA choice. Reproject with:

- depth and geometric-normal tests;
- camera/light/mode/resolution/TLAS-generation resets;
- disocclusion and material/category rejection;
- neighborhood/variance clamping;
- bounded maximum age and responsive weighting for moving blockers;
- explicit history validity for pixels that skipped tracing.

Skinned and wind-driven motion requires motion data consistent with the rendered
surface. If only camera motion is available for a category, reject its history
instead of accumulating a trail.

### 11.3 Spatial denoising

Use a small depth/normal/hit-distance-aware filter or limited A-trous passes.
Filter visibility, not scene color. Prevent cross-silhouette bleeding and retain
contact sharpness. Dispatch only over valid receiver pixels and skip when the
temporal result has converged if an evidence-backed tile classifier is later
added.

Do not reuse the optional short CSM temporal filter blindly. Soft ray noise has
different variance, history, and spatial requirements even if resource/helper
code is shared.

### 11.4 Soft-path acceptance

- stationary noise converges below the frozen temporal-variance threshold;
- camera and animated-object disocclusions do not leave visible trails;
- penumbra width responds continuously to sun angular radius and blocker/
  receiver geometry;
- thin silhouettes, alpha masks, and foliage do not dissolve under the filter;
- resize/camera cut/light change clears history in the same frame;
- total AS, ray, history, and filter cost stays within the qualified Ultra
  profile.

## 12. Performance work, in priority order

Apply optimizations only after correctness counters can prove what they change.

1. Stable matrices and per-cascade cache dirty masks.
2. Skip background and back-facing/non-contributing receiver pixels.
3. Share BLAS and one union TLAS with consumer instance masks when traversal
   measurements support it.
4. Avoid building/rendering CSM during fully qualified ray frames, except for an
   explicit fallback consumer.
5. Separate opaque fast queries from alpha-candidate work if candidate counters
   show a material cost.
6. Bound contact rays by distance and fade.
7. Compact or tile-dispatch only if the prefix/indirect overhead wins at target
   resolutions.
8. Consider half-resolution/checkerboard only as a lower-cost option, not the
   Ultra reference.
9. Consider async compute only after dependency/ownership correctness and real
   overlap are demonstrated.
10. Refit versus rebuild deforming BLAS based on measured update and traversal
    cost, with periodic rebuild only when needed.

For every optimization, capture both pass time and total frame time. Reject an
optimization that moves work between passes without improving the frame or that
changes shadow semantics without a named quality setting.

## 13. Validation matrix

### 13.1 Automated CPU/contract tests

- fitter basis/extent/snap/depth tests from Phase 1;
- serialization, preset migration, clamping, and reflection-driven editor tests;
- requested/effective fallback state table tests;
- render-graph pass order, resource declaration, access, and queue ownership
  tests;
- descriptor layout/stage visibility and shader compilation tests;
- GPU struct offsets/size/alignment tests;
- AS consumer-union, instance-mask, residency-completeness, and generation tests;
- BLAS cache-key tests for static versus deforming geometry;
- material/category readiness and conservative cap behavior tests;
- motion-vector consumer-lifecycle and history-reset tests;
- device capability and resource-allocation failure fallback tests.

### 13.2 Vulkan validation runs

Run validation for:

- startup/shutdown and repeated mode changes;
- swapchain resize, dynamic-resolution changes, minimize/restore, and device
  recreation;
- DDGI/ray shadow combinations;
- TLAS build/update followed by compute and fragment consumers;
- bindless alpha texture access and destroyed/recreated resources;
- graphics-to-async experiments, if enabled;
- capture/readback while each debug view is active.

There must be zero new validation errors and no lifetime warnings attributable
to the feature.

### 13.3 Visual scenes

| Scene | Required observation |
| --- | --- |
| Old up-vector threshold sweep | No reset, flip, or whole-shadow jump. |
| Micro-translation grid | Symmetric nearest-texel steps; no continuous shimmer while inside a cell. |
| Camera rotation in place | No extent breathing; only physically expected shadow movement. |
| Cascade ramps/overlap | No seam, brightness pulse, or invalid fallback at transitions. |
| Grazing plane and stacked thin slabs | Bounded acne and peter-panning in every cascade. |
| Tall off-screen occluder | No depth clipping or missing projected shadow. |
| Alpha fence/tree cards | Stable cutoff, sidedness, texture LOD, and ray/raster agreement. |
| Rigid and skinned animation | Current-pose shadow, no one-frame TLAS lag, no bind-pose proxy in qualified mode. |
| Wind foliage and procedural grass | Declared exact/fallback behavior with no missing category. |
| Glass/thin transmission | Matches the documented material policy; no accidental binary opaque shadow. |
| Long-distance resident caster | Ray is bounded yet reaches every qualified caster. |
| Forced residency loss | Immediate diagnosed CSM fallback, never a flash of unshadowed pixels. |
| Large coordinates | Stable origin offset, finite matrices, and no precision bands. |
| Camera cut/resize | No stale temporal history or stale mask. |

### 13.4 Performance qualification

For every intended target GPU and resolution:

- use Release builds, production validation level, stable clocks where possible,
  and a fixed warm-up;
- report median and high-percentile total GPU frame time plus shadow/AS pass
  timestamps;
- report CPU preparation time, AS memory, shadow-map/mask/history memory, BLAS
  update counts, and resident instance counts;
- compare CSM baseline, stabilized CSM, hybrid contact, hard ray, and soft ray
  with identical scene/camera/light tracks;
- repeat with DDGI off and on to quantify actual infrastructure amortization;
- retain the existing quality profile's total-frame budget as the ship gate.

## 14. Rollout and failure behavior

Use three levels of exposure:

1. `Developer`: mode can run with incomplete category support, but the viewport
   watermark and diagnostics name every exclusion. It is never selected by a
   preset.
2. `Experimental`: required categories for selected certification scenes are
   complete; opt-in editor setting remains. Captures include automatic CSM A/B.
3. `Production`: all promotion gates, validation, visual thresholds, and target
   GPU budgets pass. Only then may Ultra request the mode by default.

On any runtime failure:

- choose CSM before directional-light evaluation for that frame;
- invalidate ray/temporal history generations;
- retain/reinitialize stable CSM basis state as appropriate;
- report the first actionable fallback reason and count repeated occurrences;
- avoid retry storms for persistent allocation/build failures by using a bounded
  retry/backoff policy while CSM remains active.

Mode changes must be deterministic and artifact-free. During resource warm-up,
continue displaying CSM; switch only when the complete ray mask for a matching
scene generation is ready.

## 15. Planned code touch map

Exact names may be adjusted to repository conventions, but ownership should
remain separated as follows.

### CSM stabilization

- `Njulf.Rendering/Data/DirectionalShadowDataBuilder.cs`
- new `Njulf.Rendering/Data/DirectionalShadowStabilizationState.cs` or a fitter
  object colocated with the builder
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`
- `Njulf.Rendering/Data/DirectionalShadowRuntimeDiagnostics.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Shaders/forward.frag`
- `Njulf.Editor/ShadowEditorPanel.cs`
- `Njulf.Tests/DirectionalShadowDataBuilderTests.cs` plus focused new test files
- `RendererSettingsReference.md`

### Shared ray scene

- `Njulf.Rendering/Resources/AccelerationStructureManager.cs`
- new consumer-requirement/readiness types under `Njulf.Rendering/Resources` or
  `Njulf.Rendering/Data`
- generic ray-instance GPU metadata and corresponding layout tests
- shared ray-query alpha/material GLSL include extracted from DDGI code
- DDGI call sites updated to use the shared helper without changing transport
  behavior
- `Njulf.Rendering/VulkanRenderer.cs` preparation/order and diagnostics

### Directional ray mask

- new `Njulf.Rendering/Pipeline/DirectionalRayShadowPass.cs`
- new `Njulf.Shaders/directional_ray_shadow.comp`
- optional shadow resolve/history/filter compute shaders for evidence-gated
  temporal and soft phases
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- render-graph resource identifiers/descriptors and access declarations
- `Njulf.Rendering/Resources/RenderTargetManager.cs`
- forward/transparent shader variants and descriptor layouts
- renderer pipeline/resource lifecycle and async-resource plan, if later enabled
- debug/editor/performance capture surfaces and shader/graph/AS tests

Keep `GPUShadowData` layout unchanged during Phase 1 if possible. Put ray-mode
parameters in a separate, explicitly tested GPU block rather than casually
shifting common offsets used by existing shaders.

## 16. Suggested reviewable implementation slices

1. Baseline tracks, CSM diagnostics, and regression tests only.
2. Persistent transported basis with old fitting retained.
3. Sphere/fixed XY extent, PCF guard, and nearest-texel snapping.
4. Independent stabilized depth and per-cascade cache signatures.
5. Bias qualification and CSM documentation/preset tuning.
6. Residual-stepping decision; implement or formally omit short CSM temporal
   resolve.
7. Consumer-driven shared AS readiness, instance masks, and GI-off tests.
8. Shared alpha/material query helpers and static-opaque developer mask pass.
9. Rigid dynamics, alpha masks, current-pose skinned BLAS, and generation checks.
10. Authored/procedural foliage and transparent caster/receiver policy.
11. Full lighting integration, fallback state machine, diagnostics, and hard-path
    certification.
12. Optional bounded contact mode and performance tuning.
13. Optional finite-sun history/denoiser and soft-path certification.
14. Ultra preset promotion only after production gates pass.

Each slice must compile shaders, run affected unit/contract tests, and preserve a
usable CSM fallback. Avoid combining fitter math, AS ownership, material
semantics, and denoising in one review.

## 17. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Transported basis accumulates numeric drift | Re-orthonormalize every update, use double intermediates, assert error, and test long closed sun paths. |
| A rare antiparallel light jump still changes orientation | Treat it as an explicit discontinuity, choose a prior-basis axis deterministically, record it, and recognize that the light itself teleported. |
| Sphere fit wastes resolution | Compare measured receiver texel density and bias against the current fit; tune cascade splits/map size only as an explicit preset decision. Stability takes priority over a rotating tight fit. |
| Guard band reduces usable resolution | Derive the smallest guard from the real PCF footprint and report effective texel density. |
| Depth hysteresis clips a newly relevant caster | Expand immediately, contract slowly/outward only, and keep an off-camera caster test. |
| Shared TLAS residency for full sun shadows grows memory | Track requirements per consumer, share BLAS, use masks, benchmark union versus separate TLAS, and fail to CSM under pressure. |
| Deforming BLAS updates dominate frame time | Measure refit/rebuild, deduplicate identical pose streams, bound qualified foliage distance, and retain CSM/hybrid modes. Do not use bind pose while calling it exact. |
| Alpha candidate traversal is expensive | Use an opaque fast partition/mask, stable LOD, counters, and material admission policy. |
| Transparent receivers cannot use the opaque screen mask | Provide a ray-capable transparent shader variant or explicitly remain hybrid/CSM. |
| Soft denoising trails animated geometry | Require matching motion or reject history; cap age and use depth/normal/material/TLAS-generation rejection. |
| Ultra silently becomes slower when DDGI is off | Benchmark both shared and shadow-only configurations; promotion is based on total cost in each supported configuration. |
| Ray resources fail mid-frame | Decide effective mode before use, preserve CSM readiness, clear history, and surface the reason. |

## 18. Definition of done

### Stabilized CSM is done when

- the old threshold sweep and all camera tracks pass numeric and visual tests;
- extents are rotation invariant, centers use symmetric nearest-texel snapping,
  and depth is independently conservative/stable;
- cascade overlap/blending, PCF, static/dynamic/foliage casting, fallback, and
  reverse-Z behavior have no regressions;
- static-cache invalidation is per cascade and no more expensive in the frozen
  tracks;
- optional temporal resolve is either accepted by its gate or explicitly omitted
  with evidence;
- settings, diagnostics, debug views, tests, and reference documentation are
  updated.

### Hard ray-query Ultra is done when

- it works independently of DDGI and shares BLAS/TLAS infrastructure correctly
  when DDGI is enabled;
- AS residency and scene-generation completeness are conservative and tested;
- every advertised geometry/material/receiver class passes its qualification
  scene, including current-pose animation and foliage;
- unsupported or incomplete frames show stable CSM with an actionable reason;
- full-resolution hard output is deterministic, seam-free, and within the
  qualified Ultra frame/memory budget;
- Vulkan validation, lifecycle, shader contracts, performance captures, and
  editor/debug surfaces pass review.

### Soft ray-query Ultra is done when

- the hard path remains available with angular radius zero;
- finite-sun sampling, temporal rejection, motion-vector lifecycle, and spatial
  denoising pass the noise, trail, disocclusion, and edge tests;
- its separate resource and GPU costs fit the qualified profile;
- it remains optional and never weakens the hard/CSM fallback guarantees.

## 19. References

- [Vulkan ray-query shadows tutorial](https://docs.vulkan.org/tutorial/latest/courses/18_Ray_tracing/03_Ray_query_shadows.html) - first-hit directional-shadow queries, ray bounds/epsilon, candidate handling, and TLAS update behavior.
- [NVIDIA RTXGI overview](https://developer.nvidia.com/blog/rtx-global-illumination-part-i/) - architectural rationale for amortizing ray-tracing infrastructure across GI, glossy effects, and shadow rays. Its hardware-specific performance examples are not used as Njulf budgets.
- `implementation/Shadows.md` - source outline and long-term hybrid direction.
- `RendererSettingsReference.md` - current settings/debug-view contract to update alongside implementation.
