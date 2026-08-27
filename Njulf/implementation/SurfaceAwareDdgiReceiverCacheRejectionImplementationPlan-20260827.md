# Surface-Aware Simple-DDGI Receiver-Cache Rejection Implementation Plan

- Status: Planned
- Date: 2026-08-27
- Primary target: safely recover the receiver-cache performance opportunity for
  `DdgiHigh`, including `HybridRayQuery`, without reintroducing black noise
- Correctness oracle: exact per-fragment Simple-DDGI gather
- Related roadmap:
  [`ForwardPlusAdaptiveQualityImplementationPlan-20260824.md`](ForwardPlusAdaptiveQualityImplementationPlan-20260824.md),
  item 3A

## 1. Required outcome

Replace the receiver cache's depth-only spatial admission with a conservative
surface-aware contract. A fragment may consume a cached DDGI result only when
the cache can prove that the result was reconstructed from the same local
receiver surface. Every unproven fragment must execute the existing exact DDGI
gather and composition path.

The completed implementation must:

- retain the exact path as the visual oracle and fail-closed fallback;
- keep DDGI, hybrid reflections, C4/C5, shadows, AO, and material features
  active rather than suppressing a feature to make an artifact disappear;
- reject cross-silhouette, cross-fold, and high-normal-gradient cache reuse;
- never turn an invalid or unsupported cache entry into black radiance;
- preserve the exact gather's radiometric ownership, leak attenuation,
  environment complement, rough-specular visibility, and finite clamps;
- expose cache acceptance and exact-fallback evidence in performance captures;
- keep `DdgiHigh` on exact per-fragment gathering until the correctness and
  performance promotion gates in this plan pass.

This plan addresses same-frame spatial rejection. Temporal reprojection,
adaptive gather rates, and dirty-tile scheduling remain the separate item 3A
work in the related roadmap and must build on this surface contract later.

## 2. Audited starting point and incident evidence

### 2.1 Regression boundary

Commit `b22f8366` allowed `DdgiHigh` with hybrid reflections to consume the
coarse receiver cache. The cache evaluates one exact gather per 12x12 screen
tile, then resolves one packed radiance record per 2x2 fragment block. Its
reconstruction admitted neighbors using only a 10-bit logarithmic depth code.

That was insufficient on curtain folds and detailed masonry. Nearby pixels
could have compatible depth codes but materially different geometric normals,
so irradiance evaluated for one receiver orientation was reused on another.
The visible result was black dotted or connected noise following surface
detail.

The current correction restores exact per-fragment ownership for `DdgiHigh`.
The incident input and corrected exact-camera run are identified by:

- failing snapshot:
  `performance-20260826-235812-9311166-49eb3e962d3c4d13a89e8586cf34e800.json`;
- corrected validation:
  `performance-20260827-003337-1283617-8328effabdb14c42a3a05e114abd69bb.json`;
- incident camera position `(5.423569, 1.5170902, 1.0029265)`, yaw
  `-1.3008178`, pitch `-0.55801713`.

These external artifacts are validation evidence, not repository inputs. Add a
repository-owned deterministic scenario or bookmark containing the same camera
before implementation begins.

### 2.2 Current cache contract

The implementation boundary is:

- `ForwardPlusPass` owns the 12x12 gather dispatch, 2x2 resolve, frame-local
  buffers, descriptors, barriers, pipeline selection, and cache diagnostics;
- `ddgi_simple_receiver_cache.comp` selects a representative depth receiver,
  reconstructs a geometric normal, executes `SampleSimpleDdgiGather`, and
  writes a 16-byte gather-lattice record;
- `ddgi_simple_receiver_cache_resolve.comp` repairs empty cells and blends four
  lattice records using only receiver depth compatibility;
- `forward_ddgi_receiver_cache.glsl` reads one 16-byte resolved record;
- `forward.frag` either uses the cache-only specialization or executes the exact
  gather path;
- `MeshPipeline` and `Njulf.Shaders.csproj` own the cache-required opaque shader
  variants;
- `SimpleDdgiShaderMirrorTests` currently protects the depth-aware ABI and
  shader wiring.

The radiance record is fully occupied:

| Word | Payload |
|---:|---|
| 0 | DDGI irradiance X/Y as FP16x2 |
| 1 | DDGI irradiance Z + environment irradiance X as FP16x2 |
| 2 | environment irradiance Y/Z as FP16x2 |
| 3 | depth:10, rough-specular visibility:10, minimum roughness:6, full-weight roughness:6 |

Do not steal precision from these radiance or reflection-visibility fields for
the new surface identity.

### 2.3 Actual DDGI surface inputs

The exact opaque diffuse gather currently receives:

- `fragWorldPosition`;
- `ddgiNormal`, which is the oriented geometric normal, not the sampled normal
  map;
- view direction and global DDGI parameters.

Material diffuse reflectance, material AO, and the ordinary shading normal are
applied after or outside the cached DDGI irradiance evaluation. Therefore the
first surface-aware ABI needs precise receiver position/depth and geometric
normal. It does not need a full material G-buffer or a material hash.

Add a receiver/material signature only if a differential test proves that a
current cacheable path has another discontinuous input. Temporal reuse may need
such a signature later, but it must not force the standalone motion-vector pass
or a new full-resolution attachment for this same-frame repair.

## 3. Non-negotiable correctness rules

1. Cache admission is a proof, not a quality hint. Invalid, non-finite,
   ambiguous, or weakly supported entries select exact gathering.
2. No rejection path returns zero radiance, an empty cache sample, or a nearest
   unrelated receiver. Rejection means exact fallback.
3. Resolve-time rejection and fragment-time rejection are both required.
   Resolve protects the cached value from contaminated inputs; fragment
   validation protects the real mesh normal from depth-reconstruction error and
   mixed 2x2 blocks.
4. Thresholds may create false negatives, which cost performance. They may not
   be loosened merely to improve hit rate when doing so creates false accepts.
5. The cache remains frame-local in this implementation. No previous-frame
   sample is accepted without the separate temporal-history contract.
6. Exact receiver-feedback attribution, reflection captures, detailed DDGI
   diagnostics, provenance captures, and any unsupported pipeline combination
   retain their existing exact behavior.
7. Resource, descriptor, shader, or generation failure falls back to the exact
   pipeline for the complete view.

## 4. Chosen architecture

Use a two-stage surface proof with an exact fragment fallback:

1. The 12x12 gather producer writes the existing radiance record plus a compact
   sidecar describing the exact representative pixel's depth and geometric
   normal.
2. The 2x2 resolve reconstructs its own representative surface and blends only
   lattice samples whose sidecars are compatible with that surface.
3. The resolved cache writes the unchanged radiance record plus the resolved
   representative sidecar.
4. The opaque fragment shader compares the resolved sidecar with its actual
   `fragWorldPosition` and oriented `geometricNormal`.
5. Compatible fragments use the cache. Rejected fragments execute the existing
   exact gather and exact composition in the same shader variant.

This design adds no prepass, material target, ray query, or temporal dependency.
It places the final decision where the authoritative mesh normal is available.

### 4.1 Surface sidecar ABI

Add a parallel 8-byte `GPUSimpleDdgiReceiverSurface` record at both gather and
resolved-cache resolution:

| Word | Payload |
|---:|---|
| 0 | octahedral geometric normal as SNORM16x2 |
| 1 | 24-bit logarithmic reverse-Z code + representative pixel X/Y offsets |

The packed offset uses four bits for X and four bits for Y, supporting both the
12x12 gather tile and 2x2 resolved block with one ABI. Depth code zero is the
invalid sentinel; valid depth occupies `1..0x00ffffff`.

Define the packing once in a shared GLSL include and mirror it in a small C#
reference type. Require finite, normalized normals and in-range offsets before
publishing a valid record. Decode failure produces an invalid surface, never a
default normal.

Keep the existing 16-byte radiance payload unchanged. Parallel storage avoids
turning its single aligned load into a larger always-live radiance structure.
The fragment path reads the 8-byte sidecar first and reads the 16-byte radiance
record only after admission passes.

Memory accounting must use:

```text
gather sidecar bytes = ceil(width / 12) * ceil(height / 12) * 8 * framesInFlight
resolved sidecar bytes = ceil(width / 2) * ceil(height / 2) * 8 * framesInFlight
```

At 1920x1080 with two frames in flight this is approximately 8.1 MiB. Report
the measured allocation in diagnostics rather than hiding it inside the
existing receiver-cache byte count.

### 4.2 One shared surface-compatibility function

Create one GLSL helper and one CPU reference implementation with the same
inputs and named thresholds. The test must:

1. reject invalid/non-finite metadata or reconstructed positions;
2. decode the representative pixel coordinate from the cache coordinate,
   scale, and packed offset;
3. reconstruct both receiver positions with the same inverse view-projection
   convention;
4. compare relative depth and world-space separation;
5. reject excessive receiver-plane separation;
6. reject when the geometric-normal dot product falls below the admitted cone;
7. return a reason code as well as the Boolean decision.

Scale the position tolerance by a bounded local pixel footprint and camera
distance. Do not use only an absolute world-unit epsilon, and do not retain the
current 9%-to-41% depth compatibility window as the authoritative test.

Start with conservative constants, lock them in the CPU reference, and tune
only from exact/candidate differential evidence. Persist the effective
threshold ABI/version in performance captures.

### 4.3 Gather producer changes

In `ddgi_simple_receiver_cache.comp`:

- preserve representative selection, exact gather equations, feedback
  ownership, environment fallback, and the existing radiance packing;
- record the selected pixel offset, exact reverse-Z depth, and the same
  view-oriented reconstructed geometric normal used for the gather;
- write an invalid radiance record and invalid sidecar together on every early
  out;
- add the sidecar bindless buffer index to
  `GPUSimpleDdgiReceiverCachePushConstants`;
- keep radiance and sidecar publication within the same dispatch and barrier
  contract.

The producer must not sample material textures or infer identity from a ray
query. Those would add cost without matching the current diffuse-gather input
contract.

### 4.4 Surface-aware resolve

In `ddgi_simple_receiver_cache_resolve.comp`:

- add the inverse view-projection matrix and sidecar buffer bindings to the
  resolve ABI;
- choose a deterministic representative from the occupied pixels in each 2x2
  output block;
- prefer the surface class covering the most pixels; break ties by closest
  reverse-Z receiver and then fixed pixel order;
- load radiance and sidecar together for each lattice candidate;
- multiply bilinear weights by surface compatibility, not depth compatibility
  alone;
- require a documented minimum compatible support weight/count before
  publishing a valid resolved entry;
- allow the existing nearest-valid search only when the candidate passes the
  complete surface test against the output receiver;
- when support is absent or ambiguous, publish an invalid sidecar. Do not write
  a black substitute or an unrelated nearest-depth value;
- preserve the current FP16 radiance and rough-visibility packing for valid
  output.

The resolve output's representative sidecar describes the receiver for which
the radiance was reconstructed. It is not merely copied from an arbitrary
lattice corner.

### 4.5 Fragment admission and exact fallback

Replace the cache-only high-quality specialization with a cache-or-exact
specialization:

- sample the resolved sidecar using the existing 2x2 entry index;
- compare it with `gl_FragCoord`, `fragWorldPosition`, and the oriented
  `geometricNormal`;
- on acceptance, load the 16-byte radiance record and use the current cached
  composition;
- on rejection, call the same `SampleSimpleDdgiGather` and composition helpers
  used by the exact opaque pipeline;
- obtain rough-specular visibility from the same selected source as diffuse
  irradiance so a rejected fragment cannot mix cached visibility with exact
  diffuse GI;
- ensure hybrid reflections continue to own their directional glossy output;
  the fallback gather supplies the diffuse/visibility terms that the cache
  would otherwise own;
- keep feature MRT outputs and their locations unchanged.

Refactor exact diffuse composition into a shared helper before adding the
branch. Do not copy a shortened approximation into the fallback. The cache and
exact branches must converge immediately before material reflectance/AO
composition.

Use branch structure that reads sidecar metadata before radiance, and inspect
the generated SPIR-V/vendor disassembly for accepted-path register pressure.
Subgroup or quad-wide rejection is optional only if it does not turn one
fragment's rejection into a false cache acceptance for another fragment.

### 4.6 Pipeline and mode semantics

Introduce an explicit effective mode, for example:

```text
Exact
LegacyDepthOnlyBenchmark
SurfaceAwareCandidate
SurfaceAwareQualified
```

- `Exact` remains the `DdgiHigh` production default during development.
- `LegacyDepthOnlyBenchmark` is diagnostic-only and must never be selected by
  a quality preset.
- `SurfaceAwareCandidate` is enabled only by an explicit qualification control.
- `SurfaceAwareQualified` may become the preset-selected accelerator only after
  all promotion gates pass.

Avoid another pipeline-key combinatorial bit if possible. Change the semantic
meaning of the production `ReceiverCache` feature to the safe cache-or-exact
contract, give its shader artifacts unambiguous surface-aware names or an ABI
version, and retain the old cache-only shader only for the controlled benchmark
mode.

`ShouldConsumeSimpleDdgiReceiverCache` must require a qualified/candidate
surface-aware runtime for high-quality presets. Merely having a cache buffer or
hybrid-reflection receiver available is not sufficient.

## 5. Managed and Vulkan integration

### 5.1 Resource ownership

Extend `ForwardPlusPass` with double-banked gather-sidecar and
resolved-sidecar buffers. Allocation, replacement, descriptor updates,
generation checks, byte reporting, and cleanup must be symmetric with the
existing radiance buffers.

Append new bindless indices rather than shifting published indices. Update:

- `BindlessIndexTable` names and capacity checks;
- buffer-manager allocation descriptions;
- output and consumer descriptor layouts;
- descriptor-pool counts and writes;
- compute-to-compute and compute-to-fragment barriers;
- resize/recreation rollback and cleanup paths;
- `SimpleDdgiReceiverCacheBufferBytes` reporting to separate radiance and
  surface-sidecar bytes.

The complete cache is available only when both radiance and sidecar banks for
the current frame/generation are valid and bound.

### 5.2 ABI mirrors

Update and test together:

- `GPUSimpleDdgiReceiverCachePushConstants`;
- `GPUSimpleDdgiReceiverCacheResolvePushConstants`;
- the shared C#/GLSL surface packing version;
- descriptor set bindings at set 2;
- shader build variants and embedded-resource expectations;
- memory estimates and performance snapshot identity.

Use explicit size/offset tests for every changed struct. Do not rely only on
source-string shader tests for packing correctness.

### 5.3 Failure behavior

Pipeline compilation, resource creation, descriptor publication, or dispatch
failure must:

1. leave the current surface-aware cache unavailable;
2. select an already-valid exact opaque pipeline;
3. report a stable fallback reason;
4. never publish a partially written cache bank;
5. retire replacement resources through the existing fence/generation rules.

## 6. Diagnostics and observability

Add GPU counters for:

- resolve candidates and valid resolved entries;
- resolve rejection by invalid, depth/position, plane, normal, and insufficient
  support;
- forward cache candidates and accepted fragments;
- forward rejection by the same stable reason taxonomy;
- exact-fallback fragment count;
- accepted cache percentage and exact-fallback percentage;
- non-finite metadata/decoded-position failures;
- legacy mode usage, which must be zero outside controlled captures.

Add timings or attributable benchmark pairs for:

- gather production;
- surface-aware resolve;
- cache-or-exact forward shading;
- total `SimpleDdgiReceiverCachePass + ForwardPlusPass` versus exact forward.

Add a receiver-cache debug view with stable colors for accepted cache,
invalid/empty, depth/plane reject, normal reject, insufficient support, and
exact fallback. Add an optional accepted-error heat map generated from a
qualification-only dual evaluation of cache and exact output on sampled pixels.

Performance JSON must include:

- requested/effective cache mode and ABI version;
- surface thresholds;
- radiance and sidecar bytes;
- acceptance/fallback/rejection counters;
- pipeline artifact name;
- canonical fallback reason;
- whether exact dual-evaluation diagnostics affected timing eligibility.

## 7. Delivery sequence

### Phase 0: freeze the oracle and incident fixture

- Add the exact incident camera as a deterministic Sponza validation case.
- Capture exact scene-linear DDGI output, final HDR output, cache-off pipeline
  identity, and feature-state evidence.
- Add an assertion that `DdgiHigh` remains exact unless the candidate mode is
  explicitly requested.

### Phase 1: CPU contract and sidecar ABI

- Implement the C# pack/decode and surface-compatibility reference.
- Add randomized round-trip, invalid-value, offset-boundary, reverse-Z,
  normal-cone, and plane-separation tests.
- Add authoritative size, stride, and memory-plan tests.

### Phase 2: producer and resolve metadata

- Allocate and bind sidecar banks.
- Publish producer surface records.
- Implement surface-aware resolve and invalid publication.
- Add a metadata/rejection debug capture before enabling any forward consumer.

### Phase 3: cache-or-exact forward path

- Refactor exact diffuse composition into shared helpers.
- Add fragment admission and exact fallback.
- Compile every currently cache-eligible opaque family/output combination,
  including near-field and hybrid receivers. For caustic or any other
  cache-ineligible combination, prove that the exact pipeline is selected while
  the requested feature remains active.
- Prove source selection with counters and pipeline names.

### Phase 4: automated correctness qualification

- Run layout, shader, pipeline-key, lifecycle, and source-contract tests.
- Run exact/candidate image comparisons on the full validation matrix.
- Keep candidate mode opt-in until every correctness gate passes.

### Phase 5: performance qualification and promotion

- Run statistically paired exact/candidate/legacy captures.
- Analyze cache-pass cost, forward cost, hit rate, fallback divergence, memory,
  and whole-frame P95/P99.
- Promote Low/Medium first if qualified, then `DdgiHigh` separately.
- Change a preset default only in a dedicated promotion change containing the
  approved evidence references.

### Phase 6: handoff to temporal/adaptive work

- Make the sidecar the required surface identity input for temporal
  reprojection and dirty-tile classification.
- Add motion and receiver/material signatures only for temporal identity where
  depth/normal cannot prove history continuity.
- Do not combine temporal reuse with the initial spatial-rejection promotion.

## 8. Automated test plan

### 8.1 Numerical/reference tests

- 24-bit depth and representative-offset round trips at 1x1, partial 2x2, and
  complete 12x12 tiles;
- octahedral normal round trips over randomized unit vectors;
- equal planar receivers accept across ordinary perspective depth variation;
- opposing, orthogonal, folded, and threshold-adjacent normals reject;
- foreground/background silhouettes reject even when their old 10-bit depth
  codes collide;
- curved receivers accept only inside the admitted normal/plane bounds;
- non-finite depth, position, normal, and matrices reject;
- no compatible resolve support publishes an invalid sidecar;
- CPU and shader-compatible packing constants remain identical.

### 8.2 Shader and pipeline contracts

- producer writes radiance and sidecar for every valid/invalid exit;
- resolve reads sidecars and no longer uses depth-only compatibility as its
  authoritative decision;
- nearest-valid repair requires full surface compatibility;
- forward reads sidecar before radiance and has an exact rejection branch;
- cached and exact source selection drives both diffuse and rough visibility;
- every cache-capable opaque family has a matching exact fallback artifact;
- hybrid reflection and C4/C5 MRT definitions and locations remain unchanged;
- detailed counters, provenance, reflection capture, and B1 feedback retain
  exact behavior;
- legacy cache-only mode cannot be selected by a production preset.

### 8.3 Lifecycle and failure tests

- cold start and first frame;
- odd and 1x1 extents;
- resize, render-scale change, swapchain recreation, and device-generation
  rollback;
- cache-mode toggles and shader/pipeline creation failure injection;
- missing sidecar descriptor/buffer and allocation failure;
- frame-bank alternation and fence-safe retirement;
- scene reload, camera cut, and reflection capture transitions;
- no partial cache publication after an exception or dispatch failure.

## 9. Runtime validation matrix

Capture exact and surface-aware modes with identical assets, build, shader
bundle, seed, resolution, warm-up, camera path, exposure, and feature settings.

Required scenes and cases:

- the reported Sponza curtain/masonry camera, stationary and with slow
  sub-pixel motion;
- Sponza horizontal traversal and right-wall benchmark path;
- Bistro exterior/interior traversal;
- a thin-geometry and silhouette scene with rails, triangle tips, overlapping
  depth, and sub-2x2 features;
- a faceted/curved-normal stress scene spanning the acceptance threshold;
- double-sided and mirrored geometry;
- alpha-masked geometry, foliage, geometry decals, and emissive surfaces;
- animated/skinned receivers and moving rigid objects;
- DDGI layout/source revision, lighting change, scene reload, camera cut,
  resize, and mode toggle;
- `DdgiHigh + HybridRayQuery` with the same requested/effective feature state as
  the incident capture;
- C4/C5 requested and effective combinations without suppressing them for the
  comparison.

For every case retain:

- scene-linear exact and candidate DDGI output;
- final HDR and displayed output;
- rejection debug view;
- accepted-error heat map from non-timing qualification captures;
- performance JSON with pipeline and counter provenance;
- a short temporal sequence, not only a single still.

## 10. Promotion gates

### 10.1 Correctness gates

- The original curtain/masonry black-noise pattern is absent at the exact
  incident camera and during slow motion.
- No cache rejection produces black/zero output; rejected pixels are proven by
  counters or debug view to use exact gathering.
- Accepted-cache pixels pass the repository's strict scene-linear/HDR image
  comparison against exact output in every required scene.
- Add a dark-outlier metric: where exact indirect luminance is materially
  non-zero, candidate luminance may not collapse to a small fraction of exact.
  Pin the threshold from the exact incident capture before implementation and
  require zero unexplained connected components in the curtain/masonry ROIs.
- No new temporal crawling, silhouette halos, energy spikes, NaN/Inf pixels,
  or feature fallback is permitted.
- Hybrid reflections, C4/C5, shadows, AO, and material feature-state evidence
  must match the exact control unless a pre-existing independent gate is
  explicitly recorded.
- Vulkan validation reports no relevant warnings/errors.

### 10.2 Performance gates

Compare total ownership cost, not the cache pass in isolation:

```text
surface-aware cost = SimpleDdgiReceiverCachePass + cache-or-exact ForwardPlusPass
exact cost = exact ForwardPlusPass
```

- Require a positive 95% confidence lower bound for the total GPU-frame win on
  the incident Sponza path.
- Target at least 3% and 0.25 ms P95 improvement versus exact at 1080p on the
  capture GPU; do not promote a statistically positive but operationally
  negligible win.
- Whole-frame P99 and all unrelated pass timings may not regress by more than
  1% without a documented cause.
- Report hit/fallback rates by scene. Low hit rate is a performance failure, not
  a reason to loosen correctness thresholds.
- Report the sidecar memory increase and verify it against the byte formula.
- Candidate captures with dual exact evaluation or detailed atomics are not
  timing-eligible.

If the fragment exact-fallback branch erases the win through register pressure
or divergence, retain the correctness work and evaluate a separate sparse
compute repair design. That alternative may compact rejected pixels, evaluate
exact gathers into a full-resolution repair buffer, and keep the forward
consumer narrow, but it requires an authoritative current-frame geometric
normal producer and is not part of the initial implementation.

### 10.3 Preset promotion rule

`DdgiHigh` may consume `SurfaceAwareQualified` only when:

1. all correctness gates pass;
2. the total-cost retain gate passes;
3. all required shader family combinations are available;
4. exact fallback counters are present and trustworthy;
5. the exact mode remains selectable for qualification and emergency rollback.

Promotion must not depend on hybrid reflections being enabled. Glossy ownership
does not prove diffuse cache compatibility.

## 11. Risks and mitigations

- **Depth-reconstructed normal differs from the mesh normal:** validate again
  in the fragment against the authoritative oriented geometric normal; reject
  conservatively.
- **Mixed 2x2 block:** the resolved representative may cover only part of the
  block; every other fragment independently rejects to exact.
- **Coplanar but unrelated receiver:** use precise depth, representative pixel,
  world-space plane separation, and bounded support. Add a signature only if
  differential evidence still shows false acceptance.
- **Nearest-valid repair crosses a fold:** require complete surface
  compatibility or publish invalid.
- **Fallback branch raises register pressure:** load sidecar before radiance,
  inspect compiled code, and measure total ownership cost. Use sparse compute
  repair only as a separately qualified follow-up.
- **Fallback divergence crawls with thresholds:** use deterministic packed
  metadata and stable thresholds; capture slow camera motion and threshold
  boundary scenes.
- **Sidecar/radiance bank mismatch:** publish them as one generation and make
  availability depend on both descriptors and buffers.
- **Pipeline variant explosion:** reuse the existing receiver-cache feature
  semantic and generate only the already-supported opaque family/output
  combinations.
- **Performance tuning weakens correctness:** exact parity and outlier gates are
  promotion blockers; false negatives are preferable to false accepts.

## 12. Explicit exclusions

- Changing DDGI probe placement, update scheduling, visibility equations,
  transport, or bounce lighting.
- Blurring, clamping, darkening, or hiding the artifact instead of rejecting an
  incompatible cache sample.
- Disabling hybrid reflections, C4/C5, AO, shadows, materials, or other visible
  features for cache eligibility.
- Adding a deferred G-buffer or material-ID attachment for the first spatial
  rejection implementation.
- Temporal reprojection, checkerboard gather rates, dirty-tile scheduling, or
  history age policy; those remain the related adaptive-cache item.
- Making legacy depth-only cache consumption a production fallback.
- Promoting `DdgiHigh` before exact/candidate runtime evidence passes.

## 13. Definition of done

- A versioned surface sidecar is produced, resolved, consumed, diagnosed, and
  memory-accounted across both frame banks.
- Resolve rejects incompatible depth/position/normal samples and never
  substitutes black or an unrelated nearest receiver.
- Forward fragments accept only proven cache entries and otherwise execute the
  exact DDGI diffuse/visibility path.
- All opaque family and advanced-output shader combinations compile and retain
  their feature outputs.
- Automated numerical, ABI, shader, pipeline, and lifecycle tests pass.
- The complete runtime matrix passes exact/candidate correctness gates,
  including the original black-noise camera and temporal motion.
- Statistically paired captures pass the total-cost performance gate.
- `DdgiHigh` promotion occurs in a separate evidence-backed change, while exact
  mode remains available as the permanent oracle and rollback path.
