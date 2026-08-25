# Forward+ Adaptive Quality and Performance Implementation Plan

Last updated: 2026-08-24

Scope mapping from the renderer review:

- item 3: finish adaptive C5 and DDGI receiver-cache execution;
- item 4: implement real GTAO, bent normals, and temporal filtering;
- item 5: partition transparent work by material/pipeline requirements;
- additional item: cook meshlet normal cones and use them for conservative one-sided opaque backface rejection before task emission.

## 1. Required outcome

Deliver five independently switchable and independently qualifiable renderer changes:

1. C5 traces only receivers that need new work, carries valid history through skipped regions, varies sampling rate and ray count conservatively, and no longer requires unconditional full-resolution source attachments when the trace-resolution source producer proves faster.
2. The DDGI receiver cache reprojects valid history, evaluates only dirty/interleaved gather work, and preserves the existing 16-byte forward-consumer ABI.
3. `AmbientOcclusionMode.Gtao` runs a real horizon-based GTAO implementation with temporal accumulation and a bent-normal output. `Ssao` remains the stable fallback and reference.
4. Transparent draws select the cheapest valid shader/pipeline class without changing sorted-alpha order or receiver-feedback ownership.
5. Cooked and runtime-built meshlets contain conservative geometric-normal cones. Eligible one-sided solid meshlets can be rejected before depth/forward task emission with zero false-positive culls.

Every feature must retain a canonical fallback. Do not ship any item merely because its algorithmic work count is lower; the feature must pass the image, correctness, GPU-time, and whole-frame gates in this plan.

## 2. Audited starting point

The implementation must extend the current systems rather than create parallel replacements.

### 2.1 Measured baseline

The local capture `.perf-loop-runs/quality-pass/c5-default-on-sponza.report.json` reports:

| Metric | P95 |
| --- | ---: |
| GPU frame | 20.534 ms |
| `ForwardPlusPass` | 12.169 ms |
| `SimpleDdgiReceiverCachePass` | 2.845 ms |
| `TransparentPasses` | 2.514 ms |
| `AmbientOcclusionPass` | 2.304 ms |
| motion vectors | approximately 1.69 ms |

The capture contains 481 transparent meshlets and only two lights. It is useful for regression detection, but it is not sufficient by itself to qualify transparency partitioning, high-light-count C5 source shading, or cone culling.

### 2.2 Existing contracts to preserve

- `SimpleDdgiNearFieldResidualAdaptiveResolution` already provides eighth/quarter/half global execution tiers, a 120-sample P95 governor, hysteresis, and suspension. New local adaptivity must sit below this governor.
- C5 already has reset, prepare, compact active-tile/indirect, trace, temporal, filter, frequency-separation, and composite stages. The current active list mostly removes empty receiver tiles; it is not yet a residual-energy scheduler.
- C5 already owns double-buffered radiance, moments, validity, receiver-normal, and hit-identity histories. Extend that history contract; do not add a second denoiser.
- `ForwardPlusPass` currently evaluates one exact DDGI gather for each 12x12 receiver-lattice block and resolves the complete result to a fixed half-resolution, 16-byte-per-entry buffer each eligible frame.
- `AmbientOcclusionMode.Gtao` currently reaches the same `ComputeSsao` path as `Ssao`; `UseSceneNormals` is pushed as zero. AO is depth-only, half-resolution by default, R8, and has no temporal history.
- `SceneDataBuilder` computes `MaterialForwardClass`, but all blended materials, thick transmission, and geometry decals enter one globally sorted transparent command list. Both transparent passes select one frame-global pipeline.
- `Meshlet` and `GPUMeshlet` are 48-byte sphere-and-range records. `MeshletBuilder` receives optional normals but currently computes only the sphere; cones must be built from triangle geometry and authored winding, not interpolated vertex normals.
- Meshlet sections are raw typed records in `MLT0`/`MLT1`/`MLT2`. Changing the record stride without a cooked-format branch would silently corrupt old assets.

## 3. Non-negotiable design rules

1. Land and qualify `implementation/AlphaCorrectMotionVectorsImplementationPlan-20260824.md` before enabling receiver-cache reuse, C5 checkerboarding/history-only tiles, or GTAO temporal accumulation. Mask holes, double-sided surfaces, foliage coverage, and camera cuts must not feed false history.
2. Preserve the exact current sorted-alpha comparator. Pipeline classification may split the sorted result into contiguous runs; it may not become an additional sort key.
3. Preserve `forward_ddgi_receiver_cache.glsl` and its 16-byte resolved entry layout unless a separate consumer-ABI migration is justified later.
4. A normal-cone false positive is a correctness failure. Unsupported transforms, deformation, materials, or ambiguous cones must remain visible.
5. History resources are owned by frame-serial read/write banks, not assumed safe merely because there are two frame slots. Resizing, camera cuts, projection changes, scene/GI epochs, mode changes, and failed generations must invalidate history explicitly.
6. C5 generation changes must pass through `SimpleDdgiLayoutCompiler`, generation allocation/publication, GPU ABI versioning, evidence fingerprints, qualification, diagnostics, and rollback. Do not mutate live C5 resources in place.
7. Keep detailed/provenance/debug modes canonical. Adaptive cache reuse and sparse execution must not weaken exact receiver-feedback attribution or validation captures.
8. Benchmark one candidate at a time. In particular, decide the SSAO angle-recurrence candidate before capturing the GTAO baseline so its gain or loss is not attributed to GTAO.

## 4. Delivery order and change isolation

Use the following merge order. Each numbered phase must build, pass focused tests, and have its own baseline/candidate capture before the next phase is stacked on it.

1. **Prerequisites and counters:** alpha-correct motion, stable baseline captures, pass sub-timestamps, and feature-off reference images.
2. **Meshlet cones:** independent of temporal work and useful to the existing depth/forward paths.
3. **Transparent partitioning:** independent of GI/AO history and able to retain the current universal pipeline as fallback.
4. **Adaptive DDGI receiver cache:** establishes conservative screen-space reprojection and dirty-tile infrastructure.
5. **Adaptive C5:** adds local trace/resolve scheduling first, then evaluates the trace-resolution source producer.
6. **GTAO:** adds the new algorithm, then temporal history, then bent-normal lighting integration.

Do not combine the cooked meshlet ABI, transparent pipeline matrix, C5 ABI, receiver-cache resources, and GTAO resources in one review or one performance candidate.

## 5. Meshlet normal-cone backface culling

### 5.1 Cooked and GPU record

Extend both `Njulf.Core/Geometry/Meshlet.cs` and `Njulf.Rendering/Data/GPUStructs.cs` with one aligned 16-byte tail:

```text
Vector3 NormalConeAxis       // object-space unit geometric-normal axis
float   NormalConeCutoff     // cos(maximum angular deviation)
```

The resulting record is 64 bytes. Use full float precision for the first implementation. The power-of-two stride simplifies GPU addressing and avoids having to prove a quantization error bound at the same time as the culling algorithm.

Contract:

- a valid cullable cone has a finite unit axis and `0 < cutoff <= 1`;
- `axis = (0,0,0), cutoff = -1` is the only disabled sentinel;
- all valid non-degenerate triangle normals must satisfy `dot(axis, triangleNormal) >= cutoff` within the documented build epsilon;
- degenerate triangles do not contribute, but a meshlet containing no valid triangles receives the disabled sentinel.

Update `GPUStructLayoutTests`, `common.glsl`, `SIZEOF_GPU_MESHLET`, `ReadMeshlet`, offsets, buffer-size accounting, mesh upload/compaction, and every source contract that assumes 48 bytes.

### 5.2 Deterministic cone cooking

In `MeshletBuilder`, compute the cone after each optimized or adjacency-fallback meshlet has its local vertex and triangle lists:

1. Reconstruct every triangle from the meshlet-local triangle indices and global positions.
2. Compute the authored geometric normal with the same `(p1 - p0) x (p2 - p0)` winding used by the mesh shader.
3. Ignore non-finite or near-zero-area triangles.
4. Sum valid normals weighted by twice-triangle area and normalize the sum for the candidate axis.
5. Compute the minimum dot product between that axis and every valid unit face normal.
6. Subtract a small documented floating-point safety margin from the minimum cutoff.
7. Disable the cone when the axis is undefined, the cutoff is non-finite, or the safe cutoff is not positive.

Use the same managed implementation for meshoptimizer-built and fallback meshlets so the result does not depend on a native `meshopt_Bounds` ABI or library version.

Keep `MeshOptimizerCodec.BuildMeshlets(... coneWeight: 0.0f)` unchanged for this feature. A nonzero cone weight changes meshlet topology and cache/occupancy behavior and must be evaluated later as a separate cook candidate.

### 5.3 Cooked-format migration

Bump `CookedFormatVersions.Mesh` from 1.2 to 1.3 and make the meshlet decoder version-aware.

- Define an internal 48-byte `CookedMeshletV12` reader for mesh format 1.0-1.2.
- Expand legacy records to the 64-byte runtime `Meshlet` with the disabled sentinel. Never reinterpret a legacy section using the new stride.
- Write 64-byte records for format 1.3.
- Update `CookedAssetMigrator`: a structural migration may expand legacy records with disabled cones, while a source recook is required to obtain cullable cones.
- Advance the relevant build-tool/cache fingerprint so normal asset cooking regenerates all three LOD meshlet sections.
- Validate cone finiteness, axis length, cutoff range, and every meshlet vertex/triangle range while loading.
- Document 1.3 in `docs/CookedAssets.md` and add old-version load, new-version round-trip, corrupt-cone rejection, and migration tests.

### 5.4 Draw eligibility without growing command records

Rename the unused `GPUMeshletDrawCommand.Padding` word to `Flags`; the record remains 16 bytes. Reserve a raw-command flag for `CanNormalConeCull`. Add the corresponding bit to `GPUPackedMeshletDrawCommand.Flags` without changing its size.

`SceneDataBuilder` sets eligibility only when all of the following are true:

- the material is solid opaque, not masked, blended, decal, or foliage;
- `MaterialRenderMetadata.DoubleSided` is false;
- the object is not skinned or otherwise vertex-deformed;
- the object transform is a similarity transform: three finite, orthogonal basis vectors with equal absolute scale within a conservative tolerance;
- the material/object payload signature used to cache draw lists includes the eligibility inputs.

Signed uniform scale is allowed only after the mirrored-instance property tests pass. The existing `ResolveMirroredInstanceTriangle` winding repair means the cone axis is transformed by `WorldMatrixInverseTranspose` in the same logical orientation as emitted triangles. Shear and nonuniform scale remain ineligible because they can widen a normal cone.

### 5.5 Conservative perspective test

Add one shared GLSL include, for example `meshlet_normal_cone.glsl`, and a CPU reference implementation used by tests.

For object cone axis `a`, cutoff `c`, world sphere center `C`, radius `r`, and camera position `P`:

```text
A       = normalize(inverseTranspose(world) * a)
V       = normalize(P - C)
sinA    = sqrt(max(1 - c*c, 0))
sinB    = clamp(r / length(P - C), 0, 1)
cosB    = sqrt(max(1 - sinB*sinB, 0))
cosAB   = c*cosB - sinA*sinB
sinAB   = sinA*cosB + c*sinB
reject  = cosAB > 0 and dot(A,V) < -sinAB - safetyEpsilon
```

Return visible when the camera is inside/on the sphere, the axis/cutoff is invalid, the combined normal/view cone reaches 90 degrees (`cosAB <= 0`), the transform is ineligible, or any intermediate is non-finite. The sphere-angle term bounds the view-direction change across the complete meshlet instead of testing only its center.

For an orthographic camera, use the constant surface-to-camera direction and `sinB = 0`. If projection kind is not currently available in `GPUMeshletTaskFrameData`, add an explicit projection flag; do not infer orthographic projection from approximate matrix values in every invocation.

### 5.6 GPU integration

Apply the test at the earliest active producer:

1. In `scene_opaque_compact.comp`, reject eligible solid opaque commands before appending camera-visible opaque and solid-depth commands. Masked depth, foliage, and all transparent commands bypass the test.
2. In the raw fallback `forward.task` and depth task path, perform the same test after frustum rejection and before Hi-Z/task emission.
3. Do not retest commands that have already passed the scene compactor merely to increment another counter. If a later path cannot prove that provenance, carry a `ConeTested` command flag or use the common test once there.

Directional, spot, point, and ray-query shadow culling are a later subphase. They require light-specific direction bounds and separate proof against the shadow pipeline's cull mode. Camera depth/forward culling must not be blocked on shadow support.

### 5.7 Diagnostics and validation

Add counters for candidate, tested, rejected, invalid-cone, double-sided/masked, deformed, and nonuniform-transform skips. Report cullable meshlet coverage and rejection percentage in `RendererDiagnostics` and performance artifacts.

Add a debug view that colors visible meshlets as cullable, ineligible, invalid-cone, or raw-fallback. A GPU-rejected meshlet cannot be drawn, so its count and optional captured bounds come from the compaction diagnostics buffer rather than the color target.

Tests must include:

- planar same-winding triangles produce a tight valid cone;
- opposing, degenerate-only, and non-finite triangles disable the cone;
- optimized and fallback builders cover every authored triangle;
- randomized CPU property tests compare the cone decision with brute-force facing for every triangle and sampled point in the bounding sphere; no false-positive rejection is permitted;
- perspective near-camera, silhouette, camera-inside-sphere, orthographic, signed-uniform mirror, nonuniform, shear, and skinned cases;
- raw and packed command flag propagation and unchanged 16-byte command layouts;
- shader mirror/layout tests for the 64-byte meshlet record;
- legacy cooked assets load with disabled cones and 1.3 assets round-trip with cones.

## 6. Item 3A: adaptive DDGI receiver-cache execution

### 6.1 Chosen architecture

Keep the existing half-resolution resolved cache and exact 12x12 gather lattice. Replace the unconditional gather/resolve sequence with three stages owned by a small generation-safe runtime adjacent to `ForwardPlusPass`:

1. `SimpleDdgiReceiverCacheClassifyPass`: reproject prior resolved entries, validate them, classify tiles, write valid history directly to the current output bank, and compact dirty/interleaved work.
2. `SimpleDdgiReceiverCacheGatherPass`: execute the existing exact gather only for compacted lattice work items by indirect dispatch.
3. `SimpleDdgiReceiverCacheResolvePass`: reconstruct only dirty output tiles; clean entries were already written by reprojection.

The old full gather/full resolve path remains callable as `Canonical` mode and is the fallback for unsupported devices, invalid history, exact feedback capture, overflow, and qualification comparison.

### 6.2 Resources

Add two frame-serial history banks containing:

- the existing 16-byte resolved cache entries;
- compact receiver metadata at cache resolution: current logarithmic depth, octahedral reconstructed geometric normal, validity/confidence, age, and a receiver/material signature when the corrected motion coverage attachment provides one;
- a per-tile scheduling record;
- a transient dirty gather-work list, dirty resolve-tile list, counters, overflow flags, and indirect dispatch arguments.

Use an 8x8 cache-entry scheduling tile initially. Keep the final consumer descriptor at `SimpleDdgiReceiverCacheBufferBase + frameIndex` so forward shaders do not change.

Recreate and invalidate these resources on render-extent/scale changes, camera cuts, projection discontinuities, scene-content changes that invalidate receiver identity, DDGI layout/source epoch changes, cache mode changes, and device-generation rollback.

### 6.3 Classification and rate selection

For each current half-resolution cache entry, reconstruct the current receiver from depth and sample corrected motion. Reproject the previous cache coordinate and accept history only when:

- the coordinate is in bounds;
- both current and previous receivers are valid;
- logarithmic depth and reconstructed-normal thresholds pass;
- the receiver signature matches when available;
- the history age is within the maximum refresh interval;
- no camera, scene, material, lighting, DDGI-layout, or source epoch requires invalidation.

Classify each tile from maximum depth gradient, normal gradient, motion, rejection ratio, DDGI/probe revision, irradiance change estimate, confidence, and age:

- **Full:** evaluate every existing gather-lattice site in a new/disoccluded/high-gradient/changed tile.
- **Half:** evaluate one of two stable checkerboard phases.
- **Quarter:** evaluate one of four 2x2 phases.
- **Reuse:** perform no exact gather this frame; use accepted reprojected entries.

These rates are fractions of the existing exact gather lattice, not full-resolution per-fragment DDGI. Newly exposed receivers and epoch changes always start at Full. Every tile has a bounded forced-refresh age, so a low-gradient tile cannot remain stale indefinitely.

### 6.4 Gather and dirty resolve

Change the gather shader's work identity from `gl_GlobalInvocationID.xy` to a compact work-item record containing the target gather coordinate and scheduling metadata. Preserve `SampleSimpleDdgiGather`, radiometric ownership, leak attenuation, environment complement, rough-specular visibility, exact packing, and finite clamps.

The dirty resolve shader must:

- read newly gathered values when present;
- use accepted reprojected neighbors for unsampled phases;
- retain current depth-compatible reconstruction and nearest-valid fallback;
- never blend across rejected receiver depth/normal/signature boundaries;
- write the unchanged six FP16 radiance channels and packed visibility/roughness metadata expected by `forward_ddgi_receiver_cache.glsl`.

On list overflow, invalid indirect arguments, or missing history, run the canonical path for that frame rather than publishing a partial cache.

### 6.5 Receiver-feedback and C5 ownership

When a `SimpleDdgiReceiverFeedbackCaptureProducerContract` is active, force the canonical full gather/resolve path for that producer. Sparse reuse cannot silently stand in for exact per-frame source attribution.

Expose the cache's depth/normal/signature metadata and dirty mask to C5 through an explicit read-only frame token. C5 may reuse receiver classification and gradients when extents map exactly; it must not treat cached DDGI irradiance as the direct-diffuse/emissive residual source. If the token is unavailable or from the wrong frame/generation, C5 reconstructs its own metadata.

### 6.6 Settings, telemetry, and tests

Add an internal/effective mode with `Canonical`, `TemporalAdaptive`, and `Disabled`; keep `TemporalAdaptive` behind qualification until promoted. Advanced thresholds should derive from a small quality preset rather than exposing every constant in the editor.

Record separate GPU timings for classify/reproject, gather, and dirty resolve plus:

- candidate, accepted-history, rejected-history, and empty receiver counts;
- full/half/quarter/reuse tile counts;
- exact gathers saved and forced refreshes;
- rejection reasons and maximum history age;
- dirty-list capacity/overflow and canonical-fallback reason.

Add CPU scheduler tests, reprojection boundary tests, packing parity tests, history invalidation tests, indirect-list overflow tests, canonical/adaptive shader contract tests, generation rollback tests, and an image comparison that toggles canonical/adaptive cache output under camera and object motion.

## 7. Item 3B: adaptive C5 execution

### 7.1 Split resolve coverage from trace work

Advance the C5 GPU ABI from its current V12 contract and replace the single meaning of “active tile” with two compact lists:

- **resolve tiles:** all currently occupied receivers that need temporal carry/filter/composite work;
- **trace tiles:** the subset receiving new residual samples this frame.

The prepare/classification stage writes independent counts and indirect arguments for trace, temporal/resolve, filter, and composite. A skipped trace tile must still enter the resolve list when valid reprojected history exists. This prevents holes when checkerboarding or using history-only tiles.

Add a double-buffered per-tile scheduler history separate from the transient 80-byte diagnostic tile record. A compact 16-byte scheduling record should retain prior residual energy, variance, confidence/validity, update age/phase, and the structural/lighting epoch used to produce it.

### 7.2 Tile classifier

Split the current prepare responsibility into `Classify` and `Prepare` stages.

`Classify` runs before new source work and uses current full depth/motion, prior receiver identity, prior tile energy/variance, camera/scene/light/material/GI revisions, and optional receiver-cache metadata. It assigns:

- `TraceHigh`: new/disoccluded/changed or high-variance; every pixel, maximum admitted rays;
- `TraceNormal`: active residual; every pixel, normal ray count;
- `TraceInterleaved`: stable but nonzero residual; one checkerboard phase and minimum ray count;
- `HistoryOnly`: accepted history, no new ray this frame;
- `Inactive`: no receiver or residual contribution and no valid history.

Rules:

- a new receiver, failed reprojection, high motion, structural revision, or lighting epoch change cannot be HistoryOnly;
- low estimated contribution uses both signed residual energy and a perceptual luminance/error floor, not source luminance alone;
- stable tiles receive periodic forced refresh;
- phase selection is a deterministic hash of tile coordinate plus frame serial;
- local ray count never exceeds the quality preset or the outer global execution tier's admitted maximum;
- global governor demotion/suspension remains authoritative when aggregate C5 P95 exceeds 750 microseconds.

### 7.3 Checkerboard trace and temporal reconstruction

Extend the trace work item/tile record with phase and ray-budget codes. Trace only the selected pixels and write explicit raw-sample validity; do not encode “not traced” as a valid black residual.

Extend `ddgi_near_field_residual_temporal.comp` to:

- reproject accepted history for every resolve tile;
- distinguish new trace, interleaved hole, history-only, disocclusion, and inactive pixels;
- clamp radiance using the existing signed residual neighborhood/moment contract;
- reconstruct interleaved holes from accepted history plus depth/normal-compatible current neighbors;
- decay confidence and residual energy during repeated history-only frames;
- force new trace after the configured maximum age;
- update persistent scheduler energy/variance only after valid temporal output.

Filtering reads the resolve list rather than the trace list. Composite must leave scene color unchanged outside valid resolved residuals.

### 7.4 Reduce source-generation cost in two gates

Implement local scheduling first while retaining the current Forward+ C5 attachments. This isolates trace/history correctness from source-production changes.

Then add a candidate `SimpleDdgiNearFieldSourcePass` at the current C5 execution extent:

- render the already-visible solid and masked opaque buckets into a trace-resolution depth target, `NearFieldDirectSource`, and `NearFieldReceiverPayload`;
- use the exact shared material coverage, sidedness, mirrored winding, direct-light, and emissive helpers used by depth/forward rendering;
- discard fragments outside the classifier's resolve/refresh tile mask;
- include authored foliage only after its trace-resolution coverage and receiver identity match the full path;
- keep transparency and geometry decals out of this source pass unless the existing C5 contract explicitly admits them.

This is a small second raster at trace resolution. It is preferable to racy many-to-one storage writes from the full-resolution forward fragment shader and does not require fragment-shader interlock.

After image parity and timing qualification:

- change `NearFieldDirectSource` and `NearFieldReceiverPayload` to trace extent in the admitted C5 layout;
- remove the two C5 MRT attachments and shader outputs from ordinary `ForwardPlusPass` variants;
- update dynamic-rendering attachment contracts, render graph declarations, descriptor bindings, source/trace extents, memory accounting, and qualification fingerprints;
- retain the old Forward+ MRT producer as the canonical rollback path until the new producer is promoted.

If the second raster is slower on a device class, retain adaptive trace/resolve scheduling and select the canonical source producer for that device. Do not keep both producers active in one frame.

### 7.5 Ray cardinality and resource packing

Use discrete per-tile ray tiers, initially 1/2/4 rays per selected pixel, clamped by `SimpleDdgiNearFieldResidualRaysPerPixel` and quality evidence. Additional rays are allowed only for high variance, low confidence, or recent invalidation. A stable tile must demote rapidly; a changed tile must escalate immediately.

Once parity is established, reduce C5 memory in a separate mechanical step:

- oct-encode stored history normals if device format support and error tests pass;
- pack validity/confidence/age lanes where no atomic/image-format contract is lost;
- alias raw residual and non-overlapping filter/source scratch through render-graph transient lifetimes;
- do not pack hit identity or structural revisions until collision/reprojection tests prove equivalence.

### 7.6 C5 diagnostics and qualification tests

Extend telemetry/evidence with:

- occupied, resolve, trace-high, trace-normal, interleaved, history-only, and inactive tile counts;
- pixels/rays requested and saved by class;
- forced refreshes, history accepts/rejects, disocclusions, and maximum age;
- residual energy/variance quantiles by tile class;
- source, classify, prepare, trace, temporal, filter, and composite GPU times;
- current/maximum global tier, local ray distribution, memory bytes, overflow/fallback reason, and source-producer mode.

Update the C5 ABI mirror tests, layout compiler tests, generation transaction/rollback tests, evidence fingerprint and qualification tests, shader build inventory, render-graph resource tests, and temporal reference tests. Add deterministic CPU scheduler tests and a checkerboard reconstruction test with a moving alpha-masked foreground over a lit background.

## 8. Item 4: real GTAO, temporal filtering, and bent normals

### 8.1 Preserve SSAO and add distinct passes

Keep `ambient_occlusion.comp` and `AmbientOcclusionBlurPass` as the `Ssao` implementation and reference. Add a separate GTAO path rather than a large mode branch inside the SSAO loop:

1. `GtaoPass` — raw horizon search and bent-normal estimate;
2. `GtaoTemporalPass` — motion reprojection, rejection, clamping, and history update;
3. `GtaoSpatialPass` — depth/normal-aware filtering and publication of final scalar AO plus filtered bent normal.

Only the selected mode's passes execute. Both paths continue to publish scalar AO through the existing `AmbientOcclusionBlurredTexture` contract, so existing material/forward AO behavior remains stable.

### 8.2 GTAO algorithm

At AO resolution, reconstruct the center view position and geometric normal from reverse-Z depth. For each spatially/temporally rotated screen-space direction:

- march positive and negative horizons with a fixed quality-preset direction/step count;
- choose Hi-Z/depth mip from projected step footprint for distant samples;
- convert samples to view-space horizon angles;
- apply radius, thickness, falloff, plane-bias, and depth-discontinuity guards;
- integrate analytic visibility over the visible normal hemisphere;
- accumulate the dominant unoccluded direction and encode a normalized view-space bent normal.

Use a stable blue-noise/hash rotation plus a bounded temporal sequence. Keep sample count, radius, thickness, and falloff in presets (`Low`, `Balanced`, `High`) with a small advanced override surface; do not expose raw internal thresholds by default.

Raw output uses `R16G16B16A16Sfloat` with octahedral bent normal in XY, visibility in Z, and confidence/auxiliary moment in W. This reliable storage format is the initial correctness representation; compact formats may be evaluated after qualification.

### 8.3 Histories and resources

Add:

- transient `GtaoRaw` and `GtaoSpatialScratch` RGBA16F images, aliasable when lifetimes permit;
- double-buffered RGBA16F GTAO history;
- double-buffered compact geometry metadata, initially `R32G32Uint`, containing octahedral geometric normal, logarithmic depth, validity, and age/flags;
- a filtered RGBA16F output sampled by Forward+ for bent-normal integration;
- the existing R8 `AmbientOcclusionBlurred` scalar output.

If receiver identity is available from the corrected motion/signature path, include it in rejection through a compact history sidecar. Otherwise, material/scene changes invalidate the complete GTAO history epoch rather than risking a stale per-pixel identity.

Declare all reads/writes and history roles in `ProductionRenderPipelineDeclaration`, add creation/recreation/disposal and byte accounting in `RenderTargetManager`, and verify sampled/storage format features during startup.

### 8.4 Temporal accumulation

Use the corrected motion vector convention already consumed by TAA/C5. Reject history for:

- camera cut, invalid previous frame, mode/extent/quality/projection change;
- out-of-bounds reprojection;
- current/previous empty mismatch;
- depth discontinuity or low geometric-normal agreement;
- receiver-signature mismatch when available;
- non-finite data or excessive age.

Clamp reprojected visibility to the current depth/normal-compatible neighborhood. Clamp/renormalize the bent normal within the current neighborhood's visible-direction cone rather than linearly accepting an opposing vector. Weight history by confidence and motion; new/disoccluded pixels converge quickly and stable pixels converge slowly.

The spatial pass performs one shared-memory cross-bilateral filter over the temporal result and writes both scalar visibility and a renormalized bent normal. Do not run the existing two-pass SSAO blur after GTAO.

### 8.5 Forward-lighting integration

Roll bent normals out in three gates:

1. Produce and debug them while forward lighting uses only scalar AO.
2. Use the filtered bent normal for diffuse environment/sky irradiance only.
3. Optionally use it for DDGI diffuse lookup after separate leak/energy qualification.

Never apply the bent normal to direct lights, geometric visibility, shadow bias, specular reflection direction, transmission interfaces, or normal-map evaluation. Material AO and screen-space GTAO remain separate factors; preserve the renderer's existing policy for which indirect terms receive each factor.

Add an effective `AmbientOcclusionBentNormalMode` with `Off`, `EnvironmentOnly`, and `EnvironmentAndDdgi`, defaulting to `Off` until each gate is promoted.

### 8.6 Debugging and tests

Extend `AmbientOcclusionDebugView` with raw GTAO visibility, horizon contribution, raw/filtered bent normal, temporal confidence, history rejection, and final GTAO. Keep the existing SSAO views stable.

Tests must cover:

- `Gtao` selects the new pipeline and `Ssao` still selects the old one;
- flat plane, corner, thin occluder, depth discontinuity, reversed-Z empty depth, and large-radius reference cases;
- bent-normal encoding/decoding, unit length, hemisphere constraint, and finite output;
- reprojection convention, camera cut, extent/mode/preset reset, depth/normal rejection, age refresh, and neighborhood clamp;
- render-target creation, graph lifetime/barriers, descriptor banks, format support fallback, byte accounting, settings serialization, and shader inventory;
- forward shader uses bent normals only in admitted indirect diffuse branches.

Use path-traced or high-sample AO reference images for interiors, contact corners, foliage/masks, thin geometry, and moving disocclusions. The existing SSAO thresholds remain the minimum gate: relative RMSE <= 0.005, FLIP P95 <= 0.02, ROI mean-luminance shift <= 0.02, and ROI P95-luminance shift <= 0.03. GTAO promotion additionally requires a measurable improvement over SSAO in at least one predeclared horizon/contact-occlusion metric at equal GPU budget.

## 9. Item 5: transparent material and pipeline partitioning

### 9.1 Bounded pipeline key

Add a CPU-side `TransparentMaterialClass`:

- `GeometryDecal`;
- `OrdinaryBlend`;
- `ThickTransmission`.

Derive it from `MaterialForwardClass`, `MaterialRenderMetadata.IsGeometryDecal`, and `TransmissionPolicy`. Store it in `TransparentMeshletDraw`; the current code computes `forwardClass` but drops it before `AddTransparentDraw`.

Build a bounded `TransparentPipelineKey` from:

- material class;
- sorted alpha versus weighted OIT;
- ray scene required;
- exact receiver feedback required;
- decal receiver cache required.

Do not generate arbitrary material-feature combinations. Ordinary and decal shaders strip thick-transmission/ray-query code; thick approximation retains the raster fallback; thick ray query uses the ray layout. Global layered transparent shadow policy may still require the ray scene for every receiver, while `ThickTransmissionMode.RayQuery` requires it only for the thick class.

### 9.2 Sorted-alpha run construction

Leave `CompareTransparentMeshlets` unchanged. After sorting, copy commands in exactly the current order and segment adjacent commands with equal pipeline keys into `TransparentDrawRun` records:

```text
FirstDraw
DrawCount
PipelineKey
```

`TransparentForwardPass` begins dynamic rendering once, then iterates runs in order. It binds descriptors/pipeline only when the key changes, pushes the run range, and issues one mesh-task draw for that range. Receiver-feedback producer completion occurs once after every required run has recorded successfully.

The 256-byte `GPUForwardPushConstants` ABI is frozen. Encode the transparent first-draw offset in the high bits of `MeshletDrawBufferBaseIndex` and keep the actual bindless base in its low bits:

- reserve 14 low bits for the bindless buffer index;
- reserve 18 high bits for `FirstDraw`, covering indices 0-262143;
- add managed and GLSL pack/unpack helpers;
- make every task/mesh/fragment comparison use the unpacked base index;
- task shaders read command `FirstDraw + gl_WorkGroupID.x`, while `MeshletDrawCount` remains the run-local count.

Use this encoding only for the transparent buffer. If the bindless base or draw offset is not representable, run count exceeds a bounded CPU cap, or a required pipeline is unavailable, issue the current one-list universal draw and record the fallback reason. Do not grow the push constants or silently clamp commands.

### 9.3 Weighted OIT bucketing

Weighted OIT does not depend on back-to-front order. Build stable per-key buckets and issue one draw per admitted key, retaining deterministic command order within each bucket. Keep the current OIT composition equation and pass ordering in this feature; moving geometry decals or thick refraction to a different composition pass is separate quality work.

If exact receiver-feedback capture defines order-sensitive ownership, use the canonical list or prove bucket equivalence before enabling bucketing for that capture.

### 9.4 Pipeline objects and shader variants

Refactor `MeshPipeline` around a bounded lookup keyed by `TransparentPipelineKey` while preserving named accessors for the universal fallback during migration.

- Create ordinary and geometry-decal non-ray variants eagerly only when fast startup policy permits.
- Create weighted and uncommon thick/ray/feedback combinations lazily and cache success/failure.
- Give every variant a stable cache key/debug name and symmetrical destruction/recreation.
- Bind the receiver-cache set only for admitted geometry-decal runs.
- Bind the ray scene only for keys that require it.
- Use exact-feedback variants for every run owned by an active producer; abort capture on any unavailable required combination instead of mixing exact and non-exact output.
- Keep one universal sorted and one universal OIT pipeline as rollback paths.

### 9.5 Fragment contract

Use compile-time role defines/includes to remove impossible branches:

- ordinary: no geometry-decal path, no thick media traversal, no thick ray-query code;
- decal: decal blending/depth bias/receiver policies, no thick transmission;
- thick approximation: bounded raster approximation, no ray descriptors;
- thick ray: ray-query media traversal and required ray descriptors.

All variants retain common material alpha, sidedness, lighting, fog, reflection, shadow, and debug helpers. A debug/universal variant remains authoritative for feature-isolation views that need to inspect all classes in one shader.

### 9.6 Run governor, diagnostics, and tests

Many alternating sorted classes can make pipeline switches more expensive than the universal shader. Add a deterministic governor using run count, meshlets per run, and required ray classes. Start with a conservative cap (for example 256 runs and at least 8 meshlets per nontrivial specialized run), then tune only from captures. A fallback changes performance, not draw order or output.

Record:

- meshlets and runs per material class/key;
- sorted run count, average/max run length, pipeline binds, and universal fallbacks;
- ray-query meshlets avoided, receiver-cache/feedback meshlets, and per-class fragment/task counts when detailed diagnostics are enabled;
- per-class pipeline availability/failure and GPU sub-timings where timestamp capacity permits.

Tests must assert:

- classifier mapping for ordinary, decal, thin/non-volume blend, and volume transmission;
- the existing sort result is byte-for-byte unchanged before segmentation;
- run construction covers every command once, with exact contiguous ranges and no reordering;
- range pack/unpack boundaries and universal fallback above representable limits;
- sorted pass issues runs in order and weighted OIT buckets deterministically;
- ray-query transmission alone does not select ray pipelines for ordinary/decal runs;
- global layered ray shadows do select ray-capable variants where required;
- receiver cache remains decal-only and exact feedback remains producer-complete;
- pipeline creation, lazy failure, cache naming, cleanup, and shader-role definitions are symmetrical.

## 10. Cross-cutting renderer integration

### 10.1 Render graph and synchronization

Add explicit graph resource IDs and declarations for receiver-cache histories/lists, C5 classifier/scheduler state/source depth, and GTAO histories/scratch. Each indirect producer needs a compute-write to indirect-read barrier; each history publication needs the correct compute-write to next-frame sampled/storage-read dependency.

Do not mark these passes async-compute candidates until per-device timestamps show actual overlap and queue ownership costs are included. Initial qualification uses the existing graphics queue for deterministic ordering.

### 10.2 Settings and serialization

Add effective modes/presets with safe defaults:

- receiver cache: canonical until adaptive qualification promotes it;
- C5 local adaptivity/checkerboard/source producer: off independently behind qualification bits;
- GTAO: available but not made the default AO mode until quality/performance promotion;
- bent-normal use: off, then environment-only after qualification;
- transparent partitioning and meshlet cone culling: feature flags with universal/visible fallbacks.

Update settings file DTOs, enum validation, editor controls, reset/default profiles, sample app controls where relevant, capture identity/hash, and settings round-trip tests. Internal safety thresholds should not become ordinary user-facing sliders.

### 10.3 Diagnostics and capture identity

Include all effective modes, generations, history-valid flags, scheduler thresholds/presets, format choices, fallback reasons, and per-feature counters in `RendererDiagnostics` and performance JSON. A baseline/candidate comparison is invalid if these identities differ unexpectedly.

Add pass-level timestamp names rather than hiding new work under `ForwardPlusPass` or one aggregate AO label. Preserve the existing aggregate fields for dashboards while adding subpass fields.

## 11. Build and automated-test sequence

Run focused tests for each phase, then the full solution:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~Meshlet|FullyQualifiedName~CookedAsset|FullyQualifiedName~GPUStructLayout"
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~Transparency|FullyQualifiedName~MaterialForward"
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~ReceiverCache|FullyQualifiedName~SimpleDdgiNearFieldResidual"
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~AmbientOcclusion|FullyQualifiedName~Gtao"
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Debug
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c ShippingPerformance
dotnet build Njulf.sln
dotnet test Njulf.sln
```

Shader qualification must compile changed source in all configurations; do not use `NjulfShaderBuildMode=UseExisting`. Require SPIR-V validation, Vulkan validation with no relevant warnings/errors, stable descriptor/layout counts, and no leaked or retired-live generation resources during resize/recreation loops.

## 12. Runtime validation matrix

Use fixed assets, camera paths, warmup, frame count, resolution, compiler mode, and capture identity. Capture feature-off, canonical, and candidate output.

### 12.1 Required scenes

- Sponza horizontal and stationary paths for the current performance baseline.
- Bistro exterior/interior traversal for depth complexity, thin geometry, and temporal motion.
- A dense one-sided opaque stress scene with cameras on both sides, planar walls, curved meshes, mirrored instances, and intentionally nonuniform/skinned exclusions for cone culling.
- A mixed transparency scene with many ordinary panes/particles, geometry decals, a few thick ray-query volumes, overlapping sorted classes, and weighted OIT.
- A GTAO reference scene with planes, convex/concave corners, thin occluders, foliage/masks, moving foreground/background disocclusions, and large depth discontinuities.
- A C5/receiver-cache scene with animated receivers, emissive changes, probe/layout changes, camera cuts, low-energy stable regions, and high-variance near-field contact lighting.

### 12.2 Temporal and lifecycle cases

Exercise cold start, camera cut, projection/FOV change, render-scale change, swapchain recreation, scene reload, material/light revision, DDGI layout regeneration/rollback, mode toggle, history-bank wrap, list overflow injection, and 30-60 minute traversal/soak runs.

Check specifically for stale irradiance, GTAO ghost trails, checkerboard holes, C5 starvation, residual energy drift, transparency order changes, missing one-sided geometry, descriptor use-after-retire, and unbounded history age.

## 13. Performance and promotion gates

Use the repository's ABBA campaign and bootstrap confidence intervals. CPU/GPU frame P95 and P99 regressions outside the target pass must remain at or below 1% unless a pre-approved quality trade is documented.

### 13.1 Meshlet cones

- Zero reference-image differences outside existing discarded one-sided backfaces.
- Zero false-positive culls in property/reference tests.
- Positive reduction in emitted depth/forward mesh tasks in the dense backface workload.
- Retain only if the target workload improves GPU frame or combined depth+forward P95 by at least 3% and 0.10 ms with a positive 95% lower bound, while Sponza/Bistro do not regress by more than 1%.
- Report the 48-to-64-byte meshlet memory increase and resulting total mesh buffer budget impact.

### 13.2 Receiver cache

- Canonical/adaptive HDR and cache-debug comparisons pass the pinned image thresholds with no temporal trails.
- On the current Sponza C5-on path, require at least a 20% and 0.25 ms P95 reduction in `SimpleDdgiReceiverCachePass`, with no greater than 1% whole-frame regression.
- The intended target is at or below 1.7 ms P95 from the measured 2.845 ms baseline; missing that target is not an automatic rejection if the statistical retain gate passes and diagnostics identify remaining cost.

### 13.3 C5

- The existing total C5 production governor budget remains 0.75 ms P95 for its measured stages.
- Require a reduction in traced pixels/rays on stable workloads without increased disocclusion error or starvation.
- Promote the trace-resolution source producer only if it reduces `ForwardPlusPass + C5 source` P95 by at least 5% and 0.20 ms versus the canonical MRT producer and passes image parity.
- Whole-frame P95/P99 may not regress by more than 1% in motion/high-variance paths where adaptivity correctly performs full work.

### 13.4 GTAO

- At equal resolution and quality preset, combined GTAO raw+temporal+spatial P95 must fit within the current 2.304 ms AO P95 budget; target <= 2.0 ms on the capture GPU.
- It must beat SSAO in at least one predeclared reference-image horizon/contact metric without violating the pinned aggregate thresholds.
- Bent-normal integration is a separate candidate: environment-only and environment+DDGI are captured independently and require energy/leak/reference wins.

### 13.5 Transparency

- Sorted-alpha reference output and command order must remain identical within shader floating-point tolerance.
- In the mixed ordinary/decal/thick workload, require at least a 10% and 0.15 ms `TransparentPasses` P95 improvement with a positive 95% lower bound.
- Sponza and single-class transparent workloads may not regress by more than 1%; the run governor should choose the universal path when specialization cannot amortize switches.

If a feature misses its retain gate, keep the correctness tests and captured evidence, switch the effective default back to canonical/off, and revise the specific cost center. Do not hide a miss by stacking another candidate.

## 14. Risks and mitigations

- **C5 history holes:** one active list cannot represent “trace” and “carry history.” Use separate trace and resolve lists and explicit raw validity.
- **Adaptive starvation:** low energy can be a stale estimate. Bound history age and force refresh on structural/lighting epochs.
- **Receiver-cache ghosting:** depth alone is insufficient at silhouettes. Use corrected motion plus depth, normal, optional receiver signature, confidence, and conservative invalidation.
- **Sparse feedback attribution:** reused history is not a new exact sample. Force canonical cache execution for exact producer captures.
- **C5 source-pass duplication:** trace-resolution rerasterization may cost more than MRT bandwidth on some GPUs. Keep source producer selection generation-safe and evidence-driven.
- **GTAO ghosting or bent-normal flips:** clamp visibility and direction separately, reject on geometry discontinuity, and reset aggressively on lifecycle changes.
- **Bent-normal energy misuse:** apply only to admitted indirect diffuse terms; never change direct/specular/geometric visibility implicitly.
- **Transparent order regression:** never bucket sorted alpha. Build runs only after the existing sort and cover contiguous ranges exactly.
- **Pipeline-switch explosion:** use a bounded key, lazy variants, run governor, and universal fallback.
- **Cone false positives:** use full-precision cooked cones, sphere-bounded perspective math, similarity-transform eligibility, and brute-force property tests.
- **Cooked stride corruption:** branch on mesh format minor and expand legacy records; never raw-read 48-byte data as 64-byte records.
- **Memory growth:** report receiver/C5/GTAO history and 64-byte meshlet costs, then pack/alias only as a separately tested optimization.

## 15. Explicit exclusions

- Replacing Forward+ with deferred/visibility-buffer rendering.
- Replacing DDGI or C5 with surfels, ReSTIR GI, or path tracing.
- Changing the exact DDGI gather/lighting equations while making the cache sparse.
- Making GTAO affect direct lights, shadowing, specular reflection vectors, or transmission geometry.
- Reordering sorted transparency or redesigning weighted-OIT composition in the partitioning change.
- Cone culling for alpha-masked foliage, transparency, skinned/deformed geometry, shear/nonuniform transforms, or shadows in the first cone phase.
- Changing meshoptimizer cone weight in the cone-record change.
- Async-compute promotion without device-specific overlap evidence.
- Simultaneously shipping compact/quantized cone encoding with the first conservative culling implementation.

## 16. Definition of done

- Alpha-correct motion is the admitted temporal input.
- Mesh format 1.3 cooks valid full-precision cones, legacy assets load safely with disabled cones, and eligible solid meshlets are conservatively rejected before camera depth/forward task emission.
- Receiver-cache temporal/adaptive mode publishes the unchanged 16-byte cache ABI, has bounded refresh and exact-feedback fallback, and passes its performance gate.
- C5 has distinct trace/resolve lists, checkerboard/history-only reconstruction, per-tile ray tiers, bounded refresh, complete telemetry, and a qualified source producer.
- `AmbientOcclusionMode.Gtao` is a real horizon-based path with stable temporal filtering and a filtered bent-normal output; SSAO remains intact.
- Bent normals affect only explicitly admitted indirect diffuse lighting modes.
- Sorted transparency is not reordered, weighted OIT uses deterministic buckets, and specialized pipelines bind ray/cache/feedback resources only when required.
- Every new resource participates in render-graph declarations, recreation, history invalidation, memory accounting, generation rollback, and diagnostics.
- Focused tests, shader builds, full build, full test suite, Vulkan validation, image gates, soak tests, and the per-feature performance campaigns pass.
