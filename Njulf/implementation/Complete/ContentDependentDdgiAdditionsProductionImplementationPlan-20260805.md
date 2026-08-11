# Content-Dependent Simple-DDGI Additions Production Implementation Plan

- Status: Production source/runtime implementation complete; rollout remains
  independently qualification-gated by device, content, and quality profile
- Date: 2026-08-05
- Target: Simple DDGI with ray-query Transport V2
- Scope: many-light sampling, directional radiance for rough reflections, and animated/transparent geometry participation
- Primary profiles: High and Ultra; lower profiles remain opt-in until measured
- Baseline: the current working tree, including GPU-resident scheduling, sparse physical probe storage, and source-cache/layout work already in progress
- Rollout rule: each addition must remain independently selectable, measurable, and revertible; there is no all-or-nothing feature switch

## 1. Required outcome

Implement three content-dependent extensions without regressing the common
single-sun, opaque-static scene:

1. replace the bounded, order-dependent local-light selection at DDGI ray hits
   with an unbiased, point-dependent GPU light sampler;
2. add a compact directional incident-radiance field that can supply stable
   rough reflections and, in a later promotion stage, one bounded
   glossy-to-diffuse bounce;
3. make deformed skinned meshes, alpha-tested and supported transparent
   surfaces, geometry decals, and foliage proxies participate in the DDGI ray
   scene under explicit representation and budget contracts.

The completed implementation must satisfy all of the following:

- a scene with one directional sun and no local lights takes a constant-time
  bypass and does not allocate, build, or traverse a local-light hierarchy;
- every eligible local light has non-zero selection probability at every hit
  where it can contribute, and the estimator uses the exact selection PDF;
- light ordering in `LightManager` cannot change the converged DDGI result;
- diffuse irradiance remains the certified, non-negative Transport V2 field;
  directional radiance is a versioned sidecar and cannot silently alter the
  diffuse solver when disabled;
- DDGI rough specular composes with SSR, reflection probes, and environment IBL
  through one ownership hierarchy rather than being added as duplicate energy;
- animated acceleration structures consume the current GPU-skinned position
  stream, with an explicitly diagnosed proxy fallback when the frame budget is
  exhausted;
- alpha mask, ordinary alpha blend, thin transmission, decals, foliage, and
  unsupported thick refraction have different, documented ray semantics;
- ordinary motion, wind, opacity edits, and light edits produce regional or
  prioritized refresh work. They must not trigger a global DDGI hard reset;
- CPU work is limited to genuinely changed light, transform, animation,
  material, and environment data. Hierarchy construction, refits, sampling,
  dynamic build recording, counters, and dispatch sizing remain GPU-side where
  practical;
- every new persistent byte is compiled by the authoritative DDGI memory-plan
  path, and every optional accelerator has a canonical fallback;
- deterministic capture/replay, shader ABI tests, brute-force references,
  image/energy validation, target-GPU budgets, and safe fallback paths pass
  before a preset enables a feature.

These additions solve content gaps. They do not replace DDGI visibility,
relocation, scrolling, cascade/ring placement, or the existing diffuse
transport equation.

## 2. Current implementation boundary

This plan is based on current code, not on an older design document.

### 2.1 Direct light evaluation at DDGI hits

[`LightManager.cs`](../Njulf.Rendering/Resources/LightManager.cs) maintains a
dense GPU light array with a maximum of 1,024 lights and stable revision data.
The Simple-DDGI trace push constants carry total, directional, and local light
counts plus `MaxShadedLights`.

[`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl) currently has
two bounded policies:

- a selected-light path that scales one preselected light; and
- the active Simple-DDGI path, which scans at most the first 64 lights, inserts
  at most eight local candidates into a top-light list, and evaluates only the
  surviving entries.

Directional lights are handled first. Local importance uses emitted luminance,
range/spot attenuation, and `N dot L`, but lights outside the first 64 entries
or below the fixed top-K receive zero contribution. That is deterministic and
cheap, but biased and dependent on packed-light order. It also conflicts with
the current Ultra setting, which can request 12 shaded lights while the shader
hard limit is eight.

The existing emissive-triangle path already contains a Vose alias-table
implementation. It is useful as a packing, PDF, and validation example, but a
single global alias table is not sufficient for punctual lights: their
importance changes strongly with hit position, normal, range, and spot cone.

### 2.2 Diffuse probe data and reflection ownership

The canonical field stores 8x8 directional irradiance texels and 16x16
visibility moments per probe. Irradiance is directional in the lookup sense,
but it is cosine-convolved diffuse irradiance, not incident radiance suitable
for a specular BRDF.

[`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
currently accounts for 512 irradiance bytes and 2,048 visibility bytes per
physical probe. The current source-cache ABI is version 5 and the full cache
record remains 36 bytes, with compact representations being developed by the
existing packing plan.

[`ddgi_simple_blend.comp`](../Njulf.Shaders/ddgi_simple_blend.comp) already has
nine real-SH coefficient accumulators and L2 basis evaluation for a reduced
diffuse path. This is reusable math and test material, but it is deliberately
not the Transport V2 representation: signed SH reconstruction is not a
positive diffuse transport operator.

[`forward.frag`](../Njulf.Shaders/forward.frag) explicitly leaves indirect
specular to SSR/reflection probes/prefiltered environment. The persisted
`SimpleDdgiRoughSpecularEnabled` setting has no complete production shader path
and must not be treated as evidence that rough-specular DDGI is already
implemented.

### 2.3 Ray-scene participation

[`AccelerationStructureManager.cs`](../Njulf.Rendering/Resources/AccelerationStructureManager.cs)
currently records explicit participation policies:

| Content | Current DDGI ray representation |
|---|---|
| Static and rigid opaque meshes | triangle BLAS/TLAS instance |
| Static and rigid alpha-mask meshes | non-opaque candidate-tested triangles |
| Thin supported surfaces | candidate-tested thin-surface contract |
| Skinned opaque/alpha-mask meshes | bind-pose proxy |
| Geometry decals | excluded |
| Foliage | `FoliageProxyPending`, therefore excluded |
| General blended transparency | excluded |

Static BLAS data comes from the mesh manager's split position stream and is
cached by mesh. Acceleration structures are prepared in
[`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs) before
[`SkinningPass.cs`](../Njulf.Rendering/Pipeline/SkinningPass.cs) records its
compute dispatch, so a deformed BLAS cannot currently consume that frame's
skinned output. The skinning output has a device address but lacks the full
acceleration-structure build-input usage and synchronization contract.

Procedural foliage is generated by mesh shaders with camera-dependent LOD and
wind. Those transient raster primitives are not a stable triangle stream that
can simply be handed to a BLAS builder. Geometry decals are material overlays,
not independent opaque occluders; inserting them as ordinary triangles would
produce false visibility.

## 3. Fixed product and algorithm decisions

The implementation should not begin with three open-ended algorithm searches.
The following decisions are the production baseline; alternatives remain
oracle or experimental modes only.

| Area | Production baseline | Explicitly deferred |
|---|---|---|
| Many local lights | point-dependent binary light tree, exact small-count path, exact PDF, sampling with replacement | screen-space ReSTIR/reservoir reuse as the first implementation; global alias table as the only proposal |
| Directional representation | real RGB L2 SH sidecar, FP16 payload with FP32 accumulation | fitted SG lobes as the shipping baseline; replacing diffuse irradiance with signed SH |
| Rough reflection scope | high-roughness receiver reuse first; existing reflection hierarchy owns sharper paths | sharp/mirror reflection from DDGI |
| Glossy transport scope | separately promoted single extra glossy-to-diffuse bounce | unlimited recursive glossy feedback in the first release |
| Skinned meshes | current-pose updateable BLAS ring sourced from GPU skinning | bind pose described as current geometry |
| Alpha mask | deterministic cutoff matching material/raster policy | treating cutouts as opaque bounds |
| Ordinary alpha blend | stable stochastic coverage for primary transport; analytic layered transmittance for visibility | sorted raster-style blending at arbitrary ray depth |
| Thick glass/volume | explicitly unsupported, with diagnostic fallback | pretending alpha blend models refraction or a participating medium |
| Geometry decals | non-occluding candidate overlay associated with the base hit | confirming decal triangles as ray blockers |
| Authored foliage | mesh/alpha proxy at a qualified LOD | camera-dependent raster meshlet stream |
| Procedural grass | stable camera-independent clustered card proxy | exact per-blade BLAS at production scale |

L2 SH is selected over SG because projection, temporal blending, spatial
interpolation, validation, and Jacobi reads are linear and deterministic. SG
fitting introduces nonlinear lobe assignment and temporal lobe swapping. L2 is
only intended for sufficiently rough reflection; its frequency limitation is
enforced by the roughness gate and the reflection-source hierarchy.

The NVIDIA production DDGI paper reuses probe *irradiance* as a maximum-roughness
approximation for second and later glossy orders. It does not claim to store an
SH or SG radiance field. This plan's L2 radiance sidecar is an engine extension
motivated by that technique and by the paper's stated limitation that additional
filtered-radiance representations are needed for varying roughness.

## 4. Target frame architecture

The additions share revision, publication, and budget contracts, but their
heavy work is independent:

```text
changed scene/light/material uploads
             |
             +--> Light content revision --> GPU light-tree refit/rebuild
             |
Animation --> SkinningPass --> deformed BLAS updates --+
Foliage ----> proxy generation --> proxy BLAS updates --+--> TLAS update
Static/material/decal changes --------------------------+
                                                        |
GPU DDGI scheduling --> trace current ray scene + light sampler
                                  |
                                  +--> source cache / ray results
                                                |
                       diffuse Transport V2 blend/publish (unchanged contract)
                                                |
                       L2 radiance SH project/blend/publish (sidecar)
                                                |
                  receiver rough-specular resolve / optional one-bounce reuse
```

Required ordering for a frame that updates animation is:

1. upload changed animation inputs and record skinning;
2. barrier compute shader writes to acceleration-structure build reads;
3. update/rebuild deformed and foliage proxy BLAS objects;
4. barrier BLAS writes to TLAS build reads;
5. update the TLAS and ray-instance metadata transactionally;
6. barrier TLAS writes to ray-query reads;
7. run DDGI trace, transport, blend, and publication;
8. expose only completely published diffuse, visibility, state, and optional
   radiance-SH generations to receivers.

Light-tree work can run before DDGI trace and independently of the AS path. An
unchanged light revision must produce no build dispatch. An empty local-light
set publishes an empty-root sentinel without allocating or traversing a tree.

## 5. Shared contracts that must land first

### 5.1 Feature modes and preset ownership

Replace ambiguous booleans and overloaded caps with versioned enums and typed
budgets in [`RenderSettings.cs`](../Njulf.Rendering/Data/RenderSettings.cs):

```text
SimpleDdgiLocalLightSamplingMode:
    Auto | Exact | LightTree | LegacyTopKReference

SimpleDdgiDirectionalRadianceMode:
    Off | L1Reference | L2

SimpleDdgiGlossyTransportMode:
    Off | ReceiverOnly | OneBounce | RecursiveExperimental

DdgiSkinnedGeometryMode:
    Excluded | ConservativeProxy | CurrentPose

DdgiTransparentGeometryMode:
    MaskOnly | MaskAndThin | StochasticBlend

DdgiFoliageGeometryMode:
    Excluded | AuthoredMeshOnly | AuthoredAndProceduralProxy
```

Add independent budgets for local-light samples per hit by ring/tier, exact
light threshold, dynamic BLAS bytes and builds per frame, foliage proxy
triangles and update cadence, transparency/decal candidate limits, and
directional-sidecar memory. Do not overload `MaxShadedLights`; migrate saved
settings to the new directional-exact and local-sample fields and retain the
old value only as a capture/reference compatibility alias for one schema
version.

Initial preset policy:

- Low/Medium: all three additions off unless a device profile is explicitly
  qualified;
- High: `Auto` many-light mode, current-pose skinned geometry within budget,
  mask/thin support, L2 receiver-only rough specular after qualification;
- Ultra: High plus stochastic blended surfaces and qualified foliage proxies;
  one-bounce glossy transport remains separately gated;
- `RecursiveExperimental` can never be selected by a shipping preset.

### 5.2 Revision taxonomy

Stop using one generation as a catch-all reset signal. Introduce and document:

| Revision/epoch | Changes when | Permitted response |
|---|---|---|
| `LightBufferRevision` | packed light data changes | refit/rebuild light tree and prioritize affected probes |
| `LightTreeTopologyRevision` | leaf membership/order/topology changes | publish a new tree root after full build |
| `LightTreeContentRevision` | bounds/flux/cone data changes | refit or rebuild; never clear probe allocation |
| `RaySceneResourceGeneration` | BLAS/TLAS resource identity or ABI changes | descriptor/resource transaction and cold ray-scene publication |
| `RaySceneContentEpoch` | pose, proxy, opacity, or transform changes | regional dirty bounds and priority; no global reset |
| `DirectionalRadianceAbiVersion` | SH layout/convention changes | recreate/clear sidecar only, plus dependent history |
| `SourceLightingEpoch` | source lighting semantics or environment changes | invalidate/retrace affected source samples under existing cache rules |
| `DdgiSamplingSequenceEpoch` | stochastic sequence contract changes | deterministic cache invalidation, not a per-frame increment |

Moving the sun direction is a light-content change, not a layout/resource
change. It must urgently refresh visible and high-energy probe cohorts and blend
new samples into history; it must not clear the atlases or rebuild unrelated
AS/light-tree topology. This is required to avoid the hard-reset appearance
called out in earlier DDGI review.

### 5.3 Stable stochastic identity

All stochastic decisions used to populate persistent source data derive from a
single documented hash tuple:

```text
(worldProbeStableKey,
 directionRayOrdinal,
 sourceLightingEpoch,
 samplingSequenceEpoch,
 decisionDomain,
 optional instance/primitive identity)
```

`decisionDomain` separates light-tree traversal, alpha coverage, foliage proxy
generation, and any future proposal. Frame number is not an input. Solver-only
iterations over a cached source sample must reuse the original light/coverage
decision and radiance; otherwise the source cache ceases to be a stable linear
operator. Capture/replay stores all epochs and the codebook/hash ABI version.

### 5.4 Publication and memory planning

Extend [`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
before allocating new buffers. The plan must account separately for:

- light-tree nodes, leaves, sort/build scratch, and indirect arguments;
- canonical directional-SH bytes per admitted physical probe;
- a second SH parity only when same-frame Jacobi glossy transport requires it;
- optional receiver-side sampled acceleration, if later justified by profiling;
- dynamic BLAS/TLAS storage, update scratch, and deferred-retirement bytes;
- foliage proxy vertex/index storage and frame-slot rings.

All C# and GLSL structures use explicit size/offset assertions. A new physical
probe slot is receiver-visible only after irradiance, visibility, compact state,
and every enabled directional sidecar have been written for the same slot
generation. Optional-resource allocation failure falls back to the previous
qualified mode rather than reducing canonical diffuse coverage.

### 5.5 Reference modes

Keep slow, deterministic oracles available in validation builds:

- all-lights direct evaluation for a bounded probe/hit set;
- current legacy top-K path for before/after captures only;
- FP32 L2 SH storage and reconstruction;
- numerical cubemap/GGX convolution for selected receiver pixels;
- CPU and GPU brute-force alpha/decal hit association;
- bind-pose, conservative proxy, and current-pose AS comparisons.

Reference modes must be impossible to enable accidentally in shipping presets
and must announce themselves in captures and diagnostics.

## 6. Many-light importance sampling

### 6.1 Estimator contract

Continue to evaluate all enabled directional lights exactly. They are normally
one sun, have global support, and should not consume stochastic local-light
samples. For local lights:

1. if the local count is zero, take the no-op path;
2. if the local count is at or below the exact threshold, evaluate every local
   light whose finite influence contains the hit;
3. otherwise draw `N` local samples with replacement from the point-dependent
   tree;
4. for each sample, evaluate the existing BRDF, attenuation, spot term, and
   visibility once and accumulate `f(light) / (N * p(light | hit))`;
5. retain duplicate draws as independent samples, or combine them only with an
   exactly adjusted multiplicity. Never collapse duplicates as though they were
   one draw.

Every eligible leaf with non-zero physical contribution must have a non-zero
PDF. The branch proposal uses a conservative non-negative contribution bound.
Mix it with a small uniform-over-eligible-leaves proposal when finite-precision
or a zero bound could otherwise remove support, and calculate the PDF of that
mixture exactly. If all bounds are zero, terminate with zero; if the bound data
is invalid, use the uniform fallback and increment a repair counter.

Do not use a radiance clamp to hide rare large `1 / p` samples. Firefly policy
must first improve the proposal, sample count, probability floor mixture, and
temporal filtering. Any optional robust clamp is an explicitly biased quality
mode with separate evidence and cannot be the reference result.

### 6.2 Light-tree data and GPU build

Add a packed `GPUDdgiLightTreeNode` with a frozen C#/GLSL ABI. A 64-byte draft
node is sufficient for the first implementation:

| Payload | Purpose |
|---|---|
| influence AABB or sphere bounds | conservative distance/range support bound |
| aggregate flux/luminance upper bound | branch importance |
| aggregate spot-direction cone | conservative cone rejection/bound |
| left/right child or leaf range | traversal |
| leaf count, flags, and validation checksum | fallback and diagnostics |

Leaves store the packed local-light index plus stable light identity/revision.
Directional lights are not inserted. For 1,024 total lights, a full binary tree
is small enough to keep traversal data resident; exact allocation is computed
from the admitted local count rather than a permanent worst-case block.

Implement GPU passes in this order:

1. `DdgiLightBoundsPass`: compact eligible local lights and compute finite
   influence bounds, emitted-flux bounds, Morton keys, and leaf records;
2. `DdgiLightSortPass`: deterministic GPU sort for the current 1,024-light
   ceiling, retaining stable identity as the tie-breaker;
3. `DdgiLightTreeBuildPass`: construct the binary hierarchy and parent links;
4. `DdgiLightTreeRefitPass`: update aggregate bounds/cones/flux bottom-up when
   topology remains acceptable;
5. `DdgiLightTreeFinalizePass`: validate root totals, write tree state/counters,
   and generate any indirect build/validation dispatch arguments.

Use refit for intensity, color, and small transform changes. Rebuild when leaf
membership changes, Morton order changes beyond the measured quality threshold,
the root extent changes materially, or a bounded refit-age limit is reached.
The GPU writes the rebuild-needed flag; the CPU does not scan the light set to
make this decision. A complete new tree is built into inactive storage and its
root/state are published atomically. Trace never observes a partially built
tree.

### 6.3 Point-dependent branch weights

At a DDGI surface hit, calculate a branch bound using:

- shortest conservative distance from the hit to the node influence bounds;
- aggregate emitted luminance/flux;
- maximum possible range attenuation;
- aggregate spot-cone support;
- an upper bound for the cosine term when one is safely available.

The bound need not be tight to remain unbiased, but it must never be lower than
the represented contribution in a way that produces a zero-probability valid
light. Start with a deliberately conservative bound, validate support against
the all-lights oracle, then tighten only with proof and tests. Traverse from
root to leaf by normalizing the two child bounds and multiply branch
probabilities in FP32. Compute the final PDF through a shared helper used by
both the sampler and validation shader.

For lights outside their finite range, exact zero support is safe. Infinite or
malformed range data enters a conservative root/leaf class and cannot be
silently discarded. Shadow rays are issued only after a selected sample passes
finite, range, spot, and `N dot L` checks.

### 6.4 Shader and scheduler integration

Replace the Simple-DDGI top-K block in
[`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl) with a typed
sampler interface:

```text
DdgiEvaluateDirectionalLightsExact(...)
DdgiSampleLocalLightTree(..., sampleOrdinal, out light, out pdf)
DdgiEvaluateLocalLightSample(..., pdf, sampleCount)
```

The trace update record carries local sample count rather than a misleading
maximum shaded-light count. Near/mid/far rings may use different counts, but
the count for a source sample is stored or reconstructible from its immutable
source epoch so later transport does not reinterpret it. Remove the hardcoded
8/64 correctness limits from the production path. Retain explicit limits only
for validation modes and emit an error if a preset requests an impossible
value.

The GPU scheduler includes estimated local shadow rays in admission and can
reduce *new sample count* for lower-priority rings under budget. It must not
change the estimator PDF or drop sampled contributions after selection. An
urgent cohort caused by a changed high-energy light receives priority without
clearing unrelated cached source samples.

### 6.5 Diagnostics and debug views

Add per-frame and cumulative counters for:

- local-light count and exact/tree/bypass hit counts;
- tree builds, refits, build reason, node count, depth, and bytes;
- sampled lights, duplicate draws, visibility rays, and rejected zero terms;
- minimum/mean/geometric-mean PDF and maximum finite estimator weight;
- uniform-fallback and invalid-bound repairs;
- per-light sample histogram and light-order permutation hash;
- all-lights oracle mean, variance, relative energy error, and confidence
  interval for validation samples;
- tree build/refit/traversal/shadow GPU time by queue;
- stale-tree age and unpublished-tree count.

Debug views should display light influence bounds/tree depth, sampled leaf ID,
PDF heat, estimator weight, and per-probe local-light variance.

### 6.6 Many-light acceptance gate

Promotion requires:

- 0 local lights and 1 sun match the diffuse baseline within deterministic
  numerical tolerance and show no tree allocation/build/traversal;
- 1 through the exact threshold match brute-force all-lights evaluation;
- symmetric 8/64/256/1,024-light scenes are invariant under at least 100 stable
  permutations of packed-light order;
- for stochastic scenes, the 95% confidence interval of the sample mean contains
  the brute-force reference and measured residual bias is below 1% after the
  predeclared validation sample count;
- moving point/spot lights, range crossings, disabled lights, malformed finite
  values, tree rebuilds, and capture replay produce no missing-support case,
  non-finite PDF, stale-root read, or one-frame energy flash;
- single-sun GPU regression is no more than measurement noise and the qualified
  many-light profile stays inside its allocated DDGI trace/shadow budget on
  every target GPU class.

## 7. Directional radiance for rough reflections

### 7.1 Canonical L2 SH convention and ABI

Store nine real SH coefficients for RGB incident radiance. The canonical
direction convention is:

```text
omega points from the probe toward the traced source/hit;
L(omega) is radiance arriving back at the probe from that direction.
```

Freeze basis ordering, constants, handedness, normalization, and direction
encoding in one shared specification. Reuse the proven basis math in
`ddgi_simple_blend.comp`, but create named radiance helpers rather than sharing
ambiguous irradiance functions.

Use FP32 multiplication and workgroup reduction, then pack finite coefficients
to a 64-byte `GPUSimpleDdgiRadianceShL2` record:

- 27 FP16 coefficient values in 14 words, with one reserved half;
- remaining words for slot generation, valid/history flags, sample/quality
  metadata, representation version, and checksum/debug state.

The exact word assignment is frozen only after CPU/GLSL offset, pack/unpack,
endianness, and FP32-oracle tests exist. FP16 overflow is detected before pack;
invalid records fail closed and request retrace. Do not silently clamp HDR
radiance to the largest half value.

`Off`, `L1Reference`, and `L2` have different ABI/layout identifiers. L1 is a
lower-quality bandwidth experiment, not data that can be interpreted as the
first four words of L2 without an explicit representation tag.

### 7.2 Projection and temporal update

Project directly from traced source radiance/ray direction while those values
are present in ray scratch or the persistent source cache. Never attempt to
deconvolve the diffuse irradiance atlas.

For each updated probe:

1. load each valid ray's source radiance and immutable traced direction;
2. accumulate `L(omega) * Y_lm(omega) * 4*pi/N` in FP32 using parallel shared
   reduction;
3. calculate valid-ray coverage and coefficient energy diagnostics;
4. apply the same lifecycle, relocation, scrolling, and source-generation
   validity decisions as the diffuse result;
5. blend temporal history in coefficient space with an age/confidence-aware
   hysteresis;
6. pack and publish the sidecar for the probe's current physical slot.

The existing serial reduced-SH path is an oracle only. Production projection
must distribute rays and coefficients across the workgroup and be profiled for
bank conflicts/register pressure. A constant environment, each isolated SH
basis function, a single broad lobe, and rotated versions of each signal form
the unit/reference suite.

Receiver-only mode needs one canonical sidecar because trace reads the previous
frame before blend writes this frame. Same-frame Jacobi transport sweeps require
read/write parity; the layout compiler enables and charges the second sidecar
only for those modes. No shader may read a coefficient record being updated by
another pass in the same sweep.

### 7.3 Rough-specular receiver evaluation

Use the existing eight-probe spatial selection, lifecycle, backface,
visibility, cascade/ring, and transition weights. Interpolate SH coefficients
with normalized accepted-probe weights, evaluate in the receiver reflection
direction, and convolve for roughness before applying the engine's split-sum
specular BRDF once.

Generate an offline, checked-in GGX band-scale table for `l=0`, `l=1`, and
`l=2` over perceptual roughness. The table uses the same environment-prefilter
convention as the existing reflection code, preserves DC energy, and is tested
against numerical spherical integration. The existing BRDF LUT continues to
carry the view/Fresnel term; do not apply Fresnel or visibility twice.

Low-order SH is allowed only in a qualified high-roughness interval. Determine
the exact lower threshold from the numerical/capture gate, record it in the
device profile, and cross-fade over a nonzero band. A provisional test range of
0.55-0.70 is appropriate, but it is not a shipping constant until approved
against the oracle. Below the band, DDGI weight is exactly zero.

Negative reconstruction caused by SH ringing is measured and clamped only at
the final radiance evaluation. Roughness convolution/windowing should reduce
ringing before that clamp. Track negative energy and reject a profile whose
clamp materially changes mean energy.

Start with a correctness implementation in the existing receiver shader. If
coefficient bandwidth fails the target budget, add a dedicated half-resolution
or checkerboard `SimpleDdgiRoughSpecularResolvePass` with depth/normal/material
aware reprojection and upsampling. Do not add that complexity speculatively;
the canonical SSBO path and image result remain the oracle. Any optional
sampled coefficient accelerator is separately admitted by the layout compiler
and must have a canonical fallback.

### 7.4 Reflection-source ownership

Create one explicit indirect-specular selector rather than summing independent
sources. The target priority is:

1. valid screen-space or geometric reflection result;
2. qualified local reflection probe;
3. DDGI directional radiance in its roughness/confidence band;
4. prefiltered global environment.

Blend by source confidence and transition bands so weights sum to one. DDGI
already contains environment misses and direct/source lighting, so adding it on
top of environment IBL would double-count energy. Local reflection-box
parallax, SSR validity, DDGI probe confidence, and roughness all participate in
the source weight. Emit a debug view of final source ownership and normalized
weights.

The old `SimpleDdgiRoughSpecularEnabled` persisted setting migrates to
`DirectionalRadianceMode=L2` plus `GlossyTransportMode=ReceiverOnly` only when
the saved schema explicitly opted into the experimental behavior. Otherwise it
migrates to Off, with a one-time diagnostic; it must not silently enable a new
lighting model.

### 7.5 One-bounce glossy-to-diffuse transport

Promote this only after receiver-only mode passes. At a DDGI primary ray hit:

1. compute the outgoing/view direction back toward the source probe;
2. load the hit normal, roughness, metallic/F0, and energy-conserving material
   terms;
3. reflect to the incident direction and sample the *previous-generation*
   directional-radiance field using normal probe visibility/validity rules;
4. apply the qualified rough-specular convolution and material BRDF once;
5. add that bounded one-bounce outgoing radiance to the source sample;
6. let the normal diffuse projection carry the result into irradiance.

This produces glossy-to-diffuse transport while retaining the existing diffuse
field as receiver output. The shipping first step permits one additional
glossy reuse only. Tag source samples with the glossy-transport generation so a
cache created under receiver-only semantics is never reused as one-bounce data.

Before considering recursive fixed-point feedback, add a solver audit that
measures energy amplification, parity correctness, convergence delta, and
white-furnace behavior. `RecursiveExperimental` must use previous-generation
Jacobi reads and explicit iteration limits; it cannot use arbitrary radiance
clamping as its convergence mechanism and cannot become a preset default under
this plan.

### 7.6 Directional-radiance diagnostics and gate

Add:

- allocated/published/valid SH probe counts and exact bytes;
- projection rays, valid coverage, FP16 pack error, coefficient min/max/energy;
- negative reconstruction count/energy and final clamp energy;
- receiver sample count, accepted probes, fallback reason, source-ownership
  weights, roughness histogram, and nonzero DDGI specular pixels;
- one-bounce reads, invalid/cross-generation rejects, added energy, and
  convergence deltas;
- projection, receiver resolve, and optional glossy-transport GPU timings.

Promotion requires:

- constant-environment DC and isolated-basis tests match FP32 analytical
  results; rotation tests preserve energy;
- FP16 sidecar error remains within the declared HDR range and no non-finite
  coefficient reaches a receiver;
- roughness sweeps match numerical GGX-prefilter references within the declared
  high-roughness image/energy threshold, with no DDGI contribution below the
  approved band;
- white furnace, colored-box, broad-lobe, moving-sun, moving-emissive, local
  reflection-probe, and SSR transition scenes show no double energy, source
  ownership pop, stale-slot flash, or temporal lobe flip;
- receiver-only mode leaves diffuse irradiance bitwise or tolerance-equivalent;
- one-bounce mode passes energy conservation and converges monotonically in the
  approved material/albedo range;
- all target profiles fit the exact memory plan and GPU budget.

## 8. Animated and transparent geometry participation

### 8.1 Split CPU collection from GPU build recording

Refactor `AccelerationStructureManager` into two explicit stages:

- `PrepareFrameRayScene`: collect immutable topology/material/instance changes,
  choose declared representations under budget, allocate/reuse resources, and
  produce a build plan without recording geometry builds;
- `RecordDynamicRaySceneBuilds`: after skinning and foliage proxy generation,
  record deformed/proxy BLAS work, required barriers, TLAS work, metadata
  publication, and indirect counters.

Keep static compacted BLAS caching separate from updateable dynamic BLAS. The
current transactional completeness rule remains: DDGI receives either a fully
declared ray scene or the previous valid generation. A declared conservative
proxy is a complete representation; an accidental missing BLAS is not.

Add a frame-slot dynamic-AS pool with:

- updateable BLAS storage and scratch suballocation;
- explicit frames-in-flight ownership and timeline retirement;
- per-object topology/pose/representation revision;
- prior and current world bounds for regional invalidation;
- full-build/refit/proxy/excluded reason;
- peak/live/retired bytes in diagnostics.

Never call device idle to recycle a dynamic AS during steady-state rendering.

### 8.2 Current-pose skinned BLAS

Change GPU skinning output allocation to include
`AccelerationStructureBuildInputReadOnlyBitKHR` in addition to storage and
device-address usage. Because `GPUVertex` begins with position, configure the
triangle geometry with `R32G32B32` position format and the exact `GPUVertex`
stride. Add compile-time/contract tests for that offset and stride.

After `SkinningPass`:

1. issue a barrier from compute shader writes to AS-build vertex reads;
2. for every admitted skinned instance, update its frame-slot BLAS from its
   current output range;
3. use `AllowUpdate | PreferFastTrace` for the original build and update only
   when geometry count, flags, format, primitive count, vertex count, and active
   state are unchanged as required by Vulkan;
4. do a full build when topology, LOD, vertex/index count, or representation
   changes;
5. barrier BLAS writes before the TLAS references them.

Key dynamic BLAS by render-object identity, mesh/topology identity, LOD, and
frame slot—not by mesh handle alone. Two characters sharing a mesh can have
different poses. Extend ray-hit metadata so shaders can load deformed position,
normal, tangent, UV, and material data from the correct full skinned stream;
the static split-position assumption cannot be reused for all fields.

Budget policy is explicit per object:

```text
CurrentPose -> ConservativeProxy -> Excluded
```

Choose by visible/DDGI influence, swept bounds, recent probe-ray hit rate, and
declared profile budget. A pose from the previous use of the frame slot is not
a valid fallback. A one-frame stale current-pose representation is allowed only
as a named emergency policy with a counter and capture marker; normal fallback
uses an authored capsule/box/coarse-mesh proxy. Bind pose remains a reference or
explicit conservative-proxy option, never the reported current pose.

### 8.3 Ray-instance/material ABI

Version `GPUDdgiRayQueryInstance` and add typed fields for:

- geometry class: static, rigid, skinned, alpha mask, alpha blend, thin
  transmission, decal overlay, authored foliage, procedural foliage proxy;
- vertex source/bindless buffer index, base, stride, and attribute format;
- index base/type and material identity/revision;
- alpha mode/cutoff/two-sided/transmission flags;
- decal layer, stable order, depth tolerance/bias, and overlay flags;
- representation generation and debug identity;
- existing normal transform data.

Freeze the final record size only after shader reflection/offset tests. Keep
static and dynamic access helpers typed so a static fast path does not pay for
unneeded skinned attribute loads. Invalid metadata causes a fail-closed opaque
proxy or previous complete TLAS, according to the declared policy; it must not
be interpreted under the old ABI.

### 8.4 Alpha mask and ordinary blended surfaces

Retain deterministic candidate confirmation for alpha-mask materials and make
its texture sampling, cutoff, mip/LOD policy, UV transform, and two-sided
behavior match the raster material contract. The current bounded candidate
loop's overflow-as-opaque behavior remains a conservative emergency fallback,
but candidate overflow must be visible in telemetry and fail dense-foliage
qualification.

For ordinary non-refractive alpha blend/premultiplied surfaces, use stable
stochastic coverage for DDGI primary rays:

```text
accept surface candidate when stableRandom < effectiveCoverageAlpha;
otherwise continue the ray query.
```

An accepted surface is evaluated at full material radiance; do not multiply it
by alpha again. The mixture probability already produces the expected
`alpha * surface + (1-alpha) * behind` result. The random identity includes
probe/ray/source epoch and instance/primitive/barycentric identity, never frame
number, so cached sources do not shimmer.

Visibility/shadow queries use deterministic front-to-back transmittance instead
of stochastic blockers where the material model permits it. Accumulate colored
thin transmittance, terminate when throughput falls below a declared threshold,
and retain explicit layer/candidate caps with conservative overflow and
diagnostics. Ensure the same surface cannot contribute both stochastic opacity
and thin transmission in one path.

Physical thin transmission may extend the existing thin-surface contract.
Thick refractive glass, nested dielectrics, caustics, and participating media
remain unsupported. Classify them visibly and choose a documented proxy policy
(normally conservative thin transmission or exclusion); never imply that
stochastic alpha is a refraction solution. Additive particles are source/VFX
proxies and do not enter the TLAS as occluders.

### 8.5 Geometry decals as hit overlays

Insert geometry-decal triangles as non-opaque ray candidates with a
`DecalOverlay` geometry class. Never confirm them as blockers for primary,
visibility, or shadow rays.

During a primary DDGI query:

1. retain a bounded set of nearest decal candidates while continuing traversal;
2. commit the nearest valid base surface under its normal opacity rules;
3. associate a decal only when its distance from the base hit is within the
   authored depth-bias/tolerance contract and facing/normal/material rules
   match raster behavior;
4. order accepted overlays by `DecalLayer` and stable object ID;
5. composite decal albedo, emissive, normal, roughness, metallic, AO, and
   opacity using shared material-overlay helpers;
6. evaluate direct and transport lighting once with the final overlaid material.

Shadow and visibility queries ignore decal candidates entirely. Candidate-cap
overflow retains the closest/highest-priority records deterministically and
increments an error counter. Build a CPU reference for base-hit association,
depth tolerance, layer ordering, and material composition; capture coplanar,
intersecting, animated-base, and grazing-angle cases.

### 8.6 Authored and procedural foliage proxies

Do not attempt to capture camera-dependent raster mesh-shader output. Add a
`DdgiFoliageProxyManager` with two paths:

- authored mesh foliage: instantiate a qualified source mesh or explicit DDGI
  proxy LOD with its alpha-mask/thin material and patch transform;
- procedural grass/billboards: generate a stable, camera-independent bounded
  set of crossed-card or low-poly proxies per foliage cluster.

Procedural proxy placement uses a stable cluster/material/seed hash and matches
the raster density distribution statistically. Calibrate card size, count,
alpha, tint, two-sidedness, and thin transmittance to preserve integrated
occlusion and bounced color—not individual blade silhouettes. The compute
generator writes AS-build-capable vertex/index buffers and conservative cluster
bounds. Wind deformation uses the same clock and wind function family as the
raster path, then refits updateable proxy BLAS at a bounded cadence.

Participation is distance/ring and budget aware. Near receiver/probe regions
receive authored or denser proxies; far rings use reduced stable density or no
proxy according to a measured transmittance threshold. LOD selection depends on
world/probe influence, not the camera, to prevent view movement from changing
GI geometry. Proxy transitions cross-fade through update priority/history, not
a global clear.

### 8.7 Regional invalidation and scheduling

For each dynamic object/proxy, union prior and current conservative world AABBs
and expand by the maximum relevant probe influence. Feed that swept region to
the GPU scheduler as a soft dirty/priority region. Pose and wind updates advance
`RaySceneContentEpoch` locally; they do not advance resource generation or
invalidate every source-cache entry.

Track material-only changes separately:

- albedo/emissive/roughness/transmission edits reprioritize source shading for
  rays whose cached hit identity matches or whose probes overlap the object;
- alpha mode/cutoff changes also affect visibility and may require regional
  retrace;
- topology/BLAS identity changes use the resource transaction but still dirty
  only swept affected regions after publication.

If hit-identity indexing is not yet available, the conservative first release
uses swept-region invalidation. Do not substitute a global reset as a temporary
shipping behavior.

### 8.8 Geometry diagnostics and gate

Expose counts/bytes/timings for every representation class, including:

- static, rigid, current-pose skinned, stale-emergency, conservative proxy,
  foliage proxy, decal, alpha-mask, alpha-blend, thin, unsupported, excluded;
- dynamic BLAS full builds/refits, scratch/storage bytes, deferred retirements,
  budget deferrals, topology mismatch rebuilds, and build/TLAS GPU time;
- candidate tests, alpha accepts/rejects, transmittance layers, decal candidates,
  association rejects, layer overflow, and candidate overflow;
- foliage proxy cards/triangles, density error, wind age, and ring LOD;
- swept dirty volume, affected probe count, source retraces, and accidental
  global invalidation count.

Qualification scenes include:

- a skinned character moving between strongly colored lights, with current-pose
  ray-hit silhouettes compared against raster/deformed CPU reference;
- two instances sharing one mesh but using different poses;
- topology/LOD changes, animation stop/start, frame-slot reuse, and budget
  fallback without use-after-free or pose aliasing;
- alpha cutout grids at minification, stacked transparent curtains, colored
  thin panes, candidate/layer overflow, and deterministic capture replay;
- layered decals over static, rigid, and skinned bases at grazing angles;
- authored trees and procedural grass under wind, camera motion, and ring
  transitions, compared by integrated transmittance and indirect-color ROIs;
- explicit unsupported thick glass that reports its fallback rather than
  silently disappearing.

No profile is promoted with validation-layer errors, AS update-rule violations,
one-frame bind-pose flashes, unbounded build work, alpha temporal shimmer,
decal occlusion, foliage camera coupling, or global DDGI resets from ordinary
animation.

## 9. Render-graph and synchronization changes

Declare all new passes/resources in
[`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs)
and update [`AsyncComputePassCatalog.cs`](../Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs).
At minimum, model:

| Pass/resource | Reads | Writes | Required predecessor |
|---|---|---|---|
| Light bounds/sort/build/refit | packed light buffer, prior tree state | inactive tree, build scratch/state | changed light upload |
| Skinning | bind pose, joints | frame-slot deformed vertices | animation upload |
| Foliage proxy generation | cluster/material/wind data | proxy vertices/indices/bounds | changed foliage/wind inputs |
| Dynamic BLAS builds | deformed/proxy geometry | frame-slot BLAS | compute-write to AS-build-read barrier |
| TLAS update | all admitted BLAS, transforms | inactive/updated TLAS | BLAS-write to AS-build-read barrier |
| DDGI trace | published TLAS/metadata/tree, light/material buffers | ray results/source cache | AS-build and tree-write to ray-query/shader-read barriers |
| Diffuse blend/publish | ray/source data, previous field | irradiance/visibility/state | trace/transport |
| Radiance-SH blend/publish | ray/source data, previous SH | current SH sidecar | trace; previous parity for glossy modes |
| Rough-specular receive | published probe state/SH, receiver material data | final specular or resolve target | SH publication and receiver inputs |

Queue ownership transfers must be explicit if light-tree or AS work moves to
async compute. Dynamic AS commands stay on a queue family/device capability
that supports them. Timeline values own frame-slot reuse and retirement. Add a
validation configuration that forces separate eligible queue families to test
ownership instead of relying only on the single-queue desktop path.

## 10. Implementation phases and promotion gates

These phases describe landing order. After Phase 1, many-light, geometry, and
receiver-only SH work may be developed independently, but each merge must keep
all disabled paths equivalent to baseline.

### Phase 0 — Freeze evidence and current contracts

Tasks:

- capture deterministic baseline bundles for single sun, many local lights,
  rough materials, animated characters, alpha, decals, and foliage;
- record current DDGI CPU/GPU timings, shadow rays, source-cache revisions,
  atlas bytes, AS bytes, and hard-reset/invalidation events;
- add a light-order permutation test demonstrating current top-K bias;
- add scenes that reproduce sun-motion hard-reset behavior and bind-pose/
  excluded-geometry gaps;
- inventory the concurrent cache-packing/layout changes and choose the exact
  ABI commit these additions build upon.

Exit: captures are replayable with provenance, and no new layout is designed
against stale source-cache assumptions.

### Phase 1 — Shared settings, revisions, ABI tests, and diagnostics

Tasks:

- add typed feature modes/budgets and saved-settings migration;
- implement the revision taxonomy and stable stochastic hash helper in C# and
  GLSL with golden vectors;
- extend layout planning with disabled zero-byte regions for each new resource;
- version ray-instance metadata and add shader-reflection/offset tests;
- add counters/capture schema fields before behavior changes;
- distinguish light/scene content refresh from resource-generation reset and
  remove any sun/pose path that incorrectly clears all probes.

Exit: all features Off match baseline; settings round-trip; layout totals and
ABI tests pass; moving the sun/pose produces priority events rather than a
resource reset.

### Phase 2 — Many-light reference, tree, and production sampler

Tasks:

- implement all-lights and legacy-top-K reference modes;
- implement node ABI, bounds pass, deterministic sort, tree build/refit,
  inactive-tree publication, and validation shader;
- implement exact small-count path and unbiased traversal/PDF estimator;
- integrate ring-tier sample counts and GPU scheduler shadow-ray estimates;
- add statistical/permutation/debug captures and target timing profiles;
- remove the production 8/64 truncation and diagnose incompatible old settings.

Exit: Section 6.6 passes. Promote `Auto` only on qualified profiles; retain exact
and legacy reference modes for diagnostics.

### Phase 3 — Ray-scene refactor and current-pose skinned geometry

Tasks:

- split frame preparation from dynamic build recording and reorder skinning,
  BLAS, TLAS, and trace;
- add AS build-input usage/barriers to skinning output;
- add dynamic frame-slot BLAS/scratch pools and timeline retirement;
- implement deformed attribute metadata/loads and full-build/refit decisions;
- implement swept-region invalidation and explicit proxy budget fallback;
- add Vulkan validation and multi-instance/current-pose regression suites.

Exit: current-pose scenes pass with bounded GPU/memory work; no bind-pose
misreporting, frame-slot aliasing, device-idle stall, or global pose reset.

### Phase 4 — Alpha, thin transmission, and decal overlays

Tasks:

- unify alpha-mask sampling with raster material semantics;
- implement stable stochastic alpha blend for primary rays and deterministic
  layered transmittance for visibility;
- make unsupported refractive classes explicit;
- insert non-occluding decal candidates, base-hit association, stable layer
  composition, and CPU reference tests;
- add candidate/layer admission budgets, overflow policy, and diagnostics.

Exit: dense alpha/transparent/decal gates pass without shimmer, false decal
shadows, hidden overflow, or unsupported glass silently treated as correct.

### Phase 5 — Foliage proxy system

Tasks:

- add authored proxy LOD ingestion and stable per-patch instances;
- add compute-generated clustered procedural cards with AS-capable buffers;
- share/calibrate wind and density semantics, conservative bounds, updateable
  BLAS, cadence, and ring-aware participation;
- add statistical transmittance/color references and camera-independence tests;
- integrate proxy budgets, fallback, capture provenance, and regional dirtying.

Exit: foliage meets integrated transmittance/indirect-color error budgets and
does not rebuild or change GI merely because the camera moves.

### Phase 6 — L2 SH sidecar and receiver-only rough specular

Tasks:

- freeze SH convention/64-byte ABI and implement CPU/GLSL golden tests;
- implement parallel projection, temporal blend, slot publication, layout
  accounting, and FP32 oracle;
- generate and validate the checked-in GGX band-scale table;
- implement receiver evaluation, roughness gate, normalized reflection-source
  ownership, and negative-energy diagnostics;
- profile canonical fetches; add half-resolution resolve or optional sampled
  acceleration only if required by the declared budget;
- migrate/retire the old rough-specular boolean and update documentation.

Exit: Section 7.6 receiver-only gates pass and diffuse output remains unchanged
when the one-bounce mode is Off.

### Phase 7 — Bounded glossy-to-diffuse bounce

Tasks:

- add SH read/write parity and exact layout accounting;
- extend hit material evaluation with previous-generation directional lookup;
- tag source-cache semantics/generation and invalidate safely on mode changes;
- implement one-bounce energy/convergence audits and transport debug views;
- validate interactions with many-light samples, alpha/transmission, moving
  geometry, scrolling, sparse slot reuse, and sun motion.

Exit: the one-bounce gate passes independently. Recursive mode remains
experimental even if its test harness exists.

### Phase 8 — Integrated budgets, soak, and preset rollout

Tasks:

- run the full matrix with each feature alone and all qualified combinations;
- collect P50/P95/P99 CPU/GPU timings and peak/steady memory on every supported
  GPU class and representative scene content;
- run long animation/wind/light-motion, scroll, teleport, resize, device-loss,
  settings-toggle, hot-reload, and capture/replay soaks;
- verify no CPU queue construction/upload reappears for unchanged inputs;
- approve preset/device-profile tables and fallback reasons through reviewed
  evidence, not a universal default.

Exit: definition of done is satisfied and documentation/capture schemas are
versioned with the shipping behavior.

## 11. Validation matrix

### 11.1 Unit and contract tests

- C#/GLSL struct sizes, offsets, alignment, endianness, descriptor indices, and
  push-constant size;
- stable hash golden vectors for every decision domain;
- light bounds/cone aggregation, tree parent/child coverage, branch PDF product,
  mixture PDF, zero-bound fallback, and deterministic stable sort;
- SH basis values, projection normalization, rotations, FP16 pack/unpack,
  GGX band table checksum/DC preservation, and slot-generation rejection;
- Vulkan update eligibility decisions for unchanged/changed topology;
- skinned vertex position/stride and attribute source addressing;
- alpha coverage edge cases, transmittance composition, decal association/layer
  ordering, and foliage stable generation;
- swept-AABB and regional-probe overlap calculations;
- settings migration, capture schema, mode/ABI incompatibility, and exact memory
  totals including parity and retired dynamic AS.

### 11.2 GPU integration tests

- shader compilation/reflection and SPIR-V validation for every supported mode;
- tree build/refit/publication under 0/1/max lights and rapid revision changes;
- ray-query validation for static, skinned, alpha, blended, thin, decal, and
  foliage geometry;
- separate-queue ownership/barriers, frames-in-flight reuse, resize/recreation,
  settings toggles, descriptor generation, and deferred destruction;
- sparse probe slot allocate/evict/reuse with SH sidecar enabled;
- source-cache reuse across solver-only sweeps with stable light/alpha decisions;
- forced budget exhaustion and allocation failure for every optional resource.

### 11.3 Rendering/reference scenes

| Scene | Primary evidence |
|---|---|
| One sun, no locals | no-op parity and zero tree cost |
| Symmetric local-light arrays, 8 to 1,024 | unbiased energy, permutation invariance, variance |
| Moving point/spot rigs | tree publication and temporal refresh without flashes |
| Constant/analytic directional fields | SH projection and rotation correctness |
| Rough white/metal spheres | GGX roughness sweep and source hierarchy |
| SSR/local-probe/DDGI/environment transitions | normalized ownership, no double energy |
| White furnace and high-albedo boxes | energy conservation and transport convergence |
| Moving sun | smooth prioritized refresh, no atlas reset |
| Two differently posed shared-mesh characters | current-pose BLAS identity |
| Alpha fence/leaf grid and stacked curtains | cutoff/coverage/transmittance and temporal stability |
| Layered decals on animated/static bases | non-occluding overlay association/order |
| Authored forest and procedural windy grass | integrated proxy transmittance/color and camera independence |
| Scroll, ring transition, teleport | publication, invalidation, sparse-slot safety |

Use scene-linear HDR comparisons, temporal-difference heatmaps, ROI energy,
HDR-FLIP or the repository's approved perceptual metric, and statistical
confidence intervals where stochastic sampling is involved. Never approve a
stochastic estimator from one screenshot.

## 12. Performance and memory gates

Create a per-device-class budget table before enabling presets. Until hardware
targets are recorded, use these provisional non-negotiable gates:

- single-sun/no-local path: no light-tree allocation or dispatch and no
  measurable DDGI trace regression beyond the benchmark noise band;
- unchanged lights: zero light-tree build/refit dispatches and zero CPU tree
  construction/upload work;
- light-tree build/refit is separately timestamped and amortized; rapid light
  edits cannot create unbounded queued rebuilds;
- dynamic AS work is capped by builds, primitives, scratch bytes, and GPU time;
  excess work takes the declared proxy/deferral path before frame budget overrun;
- receiver-only SH allocates exactly 64 canonical bytes per admitted physical
  probe plus alignment/metadata explicitly reported by the compiler; parity and
  optional accelerators are charged separately;
- rough-specular receiver bandwidth/ALU stays within its profile allocation or
  the separately qualified lower-rate resolve is used;
- every new GPU timestamp reports P50/P95/P99, and the combined qualified mode
  stays inside the total GI and AS budgets rather than approving each feature
  against the whole frame independently;
- CPU profiling shows no per-frame full light scan, proxy triangle upload, or
  DDGI queue rebuild when inputs are unchanged;
- memory snapshots reconcile planned, allocated, live, retired, and peak bytes
  without deriving one category by subtraction.

Preset promotion requires recorded values on minimum, median, and high-end
supported devices. A device can qualify many-light mode while rejecting SH or
dynamic foliage; capability and budget reasons remain independent.

## 13. Observability, capture, and documentation

Extend [`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs),
[`SceneRenderingData.cs`](../Njulf.Rendering/Data/SceneRenderingData.cs),
[`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs),
and the sample capture/reporting tools with:

- requested/effective mode for each addition plus fallback reason;
- all resource revisions/epochs/ABI versions and stable sampling seed/version;
- light tree, SH, dynamic AS, transparency, decal, and foliage counters listed
  in their sections;
- CPU and GPU time per pass, queue, P50/P95/P99, and work-unit denominator;
- exact planned/allocated/live/retired/peak bytes;
- accidental hard reset count and reason, regional dirty volume/probe count,
  stale source age, and urgent refresh completion time;
- capture provenance: settings schema, shader hashes, scene/light/material/
  animation revisions, target device/driver, warm-up, random epochs, and oracle
  mode.

Add debug overlays for local-light PDF/sample identity, directional-SH lobe and
source ownership, ray-geometry class/proxy state, alpha/transmittance decisions,
decal association, dynamic AS age, and regional invalidation. Update
[`RendererSettingsReference.md`](../RendererSettingsReference.md),
[`ddgi-diagnostics.md`](../docs/rendering/ddgi-diagnostics.md), and
[`ddgi-runtime-validation.md`](../docs/rendering/ddgi-runtime-validation.md)
with semantics, unsupported cases, expected counters, and troubleshooting.

## 14. Failure and fallback behavior

| Failure/unsupported condition | Required behavior |
|---|---|
| Light-tree allocation/build/validation fails | exact small set when feasible, otherwise last complete tree for unchanged light revision or declared legacy/reference fallback; never partial tree |
| Invalid light bound/PDF | uniform eligible-leaf proposal with exact PDF, repair counter, validation failure threshold |
| Directional SH allocation fails | diffuse DDGI continues; rough DDGI source weight becomes zero; reflection hierarchy falls back |
| Invalid/non-finite SH record | reject by slot generation/validity and use next reflection source |
| Dynamic BLAS budget exhausted | declared conservative proxy/exclusion by priority; no stale arbitrary pose |
| Dynamic build/update rule mismatch | full rebuild if budget permits, otherwise declared proxy; validation counter |
| Alpha/decal candidate cap exceeded | conservative documented result plus overflow counter; profile fails if sustained |
| Unsupported thick refractive/volume material | explicit diagnostic proxy/exclusion, never ordinary alpha pretending to be refraction |
| Foliage proxy budget exhausted | lower declared stable proxy tier or excluded far participation; no camera-coupled emergency LOD |
| Optional sampled/resolve accelerator unavailable | canonical SH path if within budget, otherwise DDGI rough contribution off |
| Revision changes during build | discard unpublished work and rebuild/refit latest revision; trace keeps prior complete compatible generation |

Every fallback appears in the runtime snapshot and capture manifest. Silent
quality fallback is a production failure.

## 15. Recommended reviewable implementation slices

1. Baseline scenes, counters, capture schema, and current top-K/bind-pose gap
   evidence only.
2. Typed settings, saved-schema migration, revision taxonomy, stable hash, and
   zero-byte layout regions.
3. Ray-instance ABI vNext and shader/CPU contract tests, behavior unchanged.
4. All-lights oracle and light-order permutation harness.
5. GPU local-light bounds/sort/tree build/refit/publication.
6. Exact small-count and unbiased production tree sampler/PDF validation.
7. Many-light scheduler budgets, diagnostics, and independent preset rollout.
8. AS prepare/record split, dynamic pool, barriers, and unchanged static path.
9. Current-pose skinned BLAS plus attribute loads and proxy fallback.
10. Swept regional invalidation and animation qualification.
11. Unified alpha-mask and thin visibility semantics.
12. Stochastic alpha blend and layered transmittance.
13. Non-occluding decal overlay candidates and reference tests.
14. Authored foliage proxies.
15. Procedural clustered foliage generation, wind refit, and statistical gates.
16. L2 SH ABI, FP32 oracle, projection, packing, and publication with no receiver.
17. GGX band table and receiver-only reflection ownership integration.
18. Optional lower-rate resolve only if canonical receiver profiling requires it.
19. One-bounce SH parity, hit reuse, source-cache semantic version, and solver
    audit.
20. Integrated soak, docs, device qualification, and preset changes.

Do not combine the first dynamic-AS reorder, stochastic transparency, light-tree
sampler, and SH receiver change in one rendering patch. Their failure signatures
overlap, and a combined image can hide both positive and negative energy errors.

## 16. Primary code touch map

### Resource, layout, and scene management

- [`LightManager.cs`](../Njulf.Rendering/Resources/LightManager.cs)
- new `SimpleDdgiLightTree.cs` and light-tree GPU pass wrappers
- [`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
- [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [`AccelerationStructureManager.cs`](../Njulf.Rendering/Resources/AccelerationStructureManager.cs)
- [`SkinningManager.cs`](../Njulf.Rendering/Resources/SkinningManager.cs)
- [`FoliageManager.cs`](../Njulf.Rendering/Resources/FoliageManager.cs)
- new `DdgiFoliageProxyManager.cs`
- [`GPUStructs.cs`](../Njulf.Rendering/Data/GPUStructs.cs)
- bindless/descriptor tables used by light-tree, SH, and dynamic vertex sources

### Pipeline and synchronization

- [`SkinningPass.cs`](../Njulf.Rendering/Pipeline/SkinningPass.cs)
- [`SimpleDdgiPasses.cs`](../Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs)
- [`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs)
- [`AsyncComputePassCatalog.cs`](../Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs)
- [`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs)

### Shaders

- [`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl)
- [`ddgi_simple_trace.comp`](../Njulf.Shaders/ddgi_simple_trace.comp)
- [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl)
- [`ddgi_simple_blend.comp`](../Njulf.Shaders/ddgi_simple_blend.comp)
- transport/publish/audit shaders that mirror DDGI push constants or source
  semantics
- new light bounds/sort/tree build/refit validation shaders
- new shared radiance-SH and ray-material-overlay helpers
- [`forward.frag`](../Njulf.Shaders/forward.frag) and, only if justified, a new
  rough-specular resolve compute shader
- skinning/foliage proxy generation shaders and required barriers

### Settings, diagnostics, validation, and docs

- [`RenderSettings.cs`](../Njulf.Rendering/Data/RenderSettings.cs)
- [`SceneRenderingData.cs`](../Njulf.Rendering/Data/SceneRenderingData.cs)
- [`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs)
- [`DdgiRuntimeSnapshot.cs`](../Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs)
- [`RenderBudgetEvaluator.cs`](../Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs)
- [`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs)
- DDGI Vulkan/unit/capture harnesses under `Njulf.Rendering/Diagnostics` and
  `NjulfHelloGame`
- renderer settings and DDGI diagnostic/runtime documentation

## 17. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Tree proposal has missing support and becomes biased | conservative bounds, uniform mixture floor, exact PDF, all-lights support oracle, permutation/statistical tests |
| Rare tiny PDFs create fireflies | improve point-dependent bounds and sample stratification; diagnose weights; do not hide reference error with an implicit clamp |
| Tree rebuild flashes or reads partial data | inactive build storage, revision/checksum validation, atomic root publication, prior complete-tree fallback |
| Single-sun scene pays feature overhead | directional exact path and zero-local compile/runtime bypass with zero tree resources/dispatches |
| L2 SH is too low-frequency for requested roughness | measured roughness gate/cross-fade, GGX band convolution, sharper sources retain ownership |
| SH FP16/ringing changes energy | FP32 projection/oracle, finite range gate, DC/rotation/furnace tests, final-only measured negative clamp |
| DDGI and environment/reflection probes double-count | normalized source-ownership selector, one split-sum application, ownership debug view |
| Glossy recursion becomes unstable | receiver-only first, bounded one-bounce promotion, previous-generation parity, source semantic version, energy/convergence audit |
| Concurrent cache packing changes reinterpret SH/source data | depend on a frozen layout-compiler/ABI commit; independent version IDs and cold recreation |
| Skinned BLAS reads data still being written | explicit compute-write to AS-build-read barrier and render-graph validation |
| Shared mesh causes shared animated BLAS pose | object/topology/frame-slot key, multi-pose regression test |
| Dynamic AS exceeds frame/memory budget | fixed per-profile build/byte/time budgets and declared proxy hierarchy with counters |
| Stochastic alpha shimmers through source caching | stable source-epoch hash; frame number excluded; deterministic capture replay |
| Decals create false blockers or float to wrong base | never confirm overlay candidates, depth/facing association, stable layers, CPU reference |
| Procedural foliage GI changes with camera LOD | camera-independent world-cluster proxies and statistical transmittance qualification |
| Animation/wind/sun causes hard resets | separate content epochs from resource generations; swept regional priority and urgent cohorts |
| Vulkan AS update constraints are violated | build-geometry signature comparison, original `AllowUpdate`, full rebuild/proxy fallback, validation tests |
| Optional allocation reduces core DDGI coverage | canonical diffuse admission first, optional independent admission/fallback, exact memory-plan reconciliation |

## 18. Definition of done

This project is complete only when:

- the production local-light path is unbiased, order-invariant, point-dependent,
  exact for small sets, and a zero-cost bypass for the single-sun case;
- light tree build/refit/publication is GPU-resident, revision-safe, fully
  diagnosed, and never exposes partial hierarchy state;
- the L2 radiance sidecar has a frozen/tested ABI, exact memory accounting,
  deterministic parallel projection, safe sparse-slot publication, and an FP32
  oracle;
- rough DDGI reflection operates only in an approved roughness/confidence band
  and composes with SSR, reflection probes, and environment without duplicate
  energy;
- receiver-only mode does not alter diffuse transport, and one-bounce
  glossy-to-diffuse transport passes independent energy/convergence gates;
- current-pose skinned geometry is built after skinning with valid Vulkan update
  rules, frame-safe ownership, deformed attribute access, and explicit proxies;
- alpha mask, supported alpha blend, thin transmission, decals, authored
  foliage, and procedural foliage proxies have tested, distinct ray semantics;
- thick refraction/volumes and other unsupported content are explicitly reported
  and use documented fallbacks;
- normal light, pose, wind, alpha, and decal edits cause bounded regional or
  prioritized refresh without a global DDGI hard reset;
- CPU/GPU unit, shader, Vulkan validation, reference-render, statistical,
  temporal, sparse-slot, scroll/teleport, failure, memory, and performance gates
  pass on every promoted device profile;
- diagnostics and capture manifests expose effective modes, revisions, PDFs,
  source ownership, representation classes, fallback reasons, bytes, timings,
  and reset causes;
- each feature can be disabled independently and returns to the qualified
  previous behavior without stale descriptors, data reinterpretation, or a
  device-idle stall;
- presets are enabled only from reviewed evidence, and recursive glossy mode
  remains experimental.

## 19. Primary technical references

- NVIDIA Research, [Dynamic Many-Light Sampling for Real-Time Ray Tracing](https://research.nvidia.com/labs/rtr/publication/moreau2019manylight/): point-dependent hierarchical many-light sampling and GPU-oriented dynamic hierarchy maintenance.
- NVIDIA Research, [Importance Sampling of Many Lights on the GPU](https://research.nvidia.com/labs/rtr/publication/moreau2019manylight_rtg/): implementation-oriented companion material for the many-light sampler.
- NVIDIA Research, [Scaling Probe-Based Real-Time Dynamic Global Illumination for Production](https://research.nvidia.com/publication/2020-09_scaling-probe-based-real-time-dynamic-global-illumination-production-technical): production DDGI, including reuse of irradiance as a maximum-roughness approximation for later glossy orders and the limitations of that approximation.
- Journal of Computer Graphics Techniques, [Scaling Probe-Based Real-Time Dynamic Global Illumination for Production (paper)](https://www.jcgt.org/published/0010/02/01/paper-lowres.pdf): detailed production technique and discussion of filtered radiance needed for broader glossy roughness support.
- NVIDIA Research, [Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields](https://research.nvidia.com/index.php/publication/2019-05_dynamic-diffuse-global-illumination-ray-traced-irradiance-fields): foundational DDGI probe update and sampling model.
- Khronos Vulkan Specification, [Acceleration Structures](https://docs.vulkan.org/spec/latest/chapters/accelstructures.html): authoritative build/update invariants, synchronization, and geometry requirements.
- Khronos Vulkan Tutorial, [Ray-Traced Shadow Transparency](https://docs.vulkan.org/tutorial/latest/courses/18_Ray_tracing/05_Shadow_transparency.html): candidate intersections and conditional confirmation for transparent ray geometry.
