# Simple-DDGI-Only Forward Shader and Compact Gather Data Implementation Plan

- Status: Superseded by the Simple-DDGI-only cleanup (2026-08-04)
- Date: 2026-08-03
- Primary target: ShippingPerformance, 1080p, NVIDIA RTX 3060 Laptop GPU
- Scope owner: forward receiver path and the receiver-visible Simple-DDGI ABI
- Related plans:
  - [GPU-Resident Simple-DDGI Scheduling Implementation Plan](SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md)
  - [Sun/Sky, DDGI, Async Compute, and Reflection Remaining Implementation Plan](SunSkyDdgiAsyncReflectionRemainingImplementationPlan-20260803.md)
- Baseline evidence: [Nsight DDGI Performance Pass Implementation Status](Complete/NsightDdgiPerformancePassImplementationStatus-20260802.md)

> **Superseded:** Legacy/full DDGI and the original SSGI pipeline have now
> been deleted. The runtime has one Simple-DDGI path, so statements below about
> retaining, selecting, or testing a second backend are historical context only.

## 1. Required outcome

When Simple DDGI is selected, every production forward shader must contain only
the Simple-DDGI receiver implementation. It must not contain the legacy DDGI
receiver, legacy exhaustive-gather fallback, legacy state/atlas access, or
Simple-DDGI diagnostic-only work.

The receiver must read a dedicated, compact, coherently published probe record
instead of the 32-byte compute/scheduler state. The target record is one aligned
16-byte load and contains only:

- packed relocation;
- receiver validity/active weight;
- receiver rejection/publication flags; and
- the canonical atlas probe address.

The update, scheduler, relocation, blend, publication validation, diagnostics,
and state-readback paths retain the full compute-oriented state. Splitting the
ABIs must isolate future scheduler changes from forward shading rather than
forcing the scheduler state itself to remain frozen for receivers.

The production gather must still evaluate the same eight trilinear corners and
preserve the current multi-volume ownership, recovery, visibility, leak-control,
and environment-complement equations. Its per-corner order changes to load and
reject compact state before either atlas access, and it computes atlas addressing
once for use by both irradiance and visibility reads.

Completion requires all of the following:

1. Simple production SPIR-V has no legacy DDGI receiver code or legacy DDGI
   descriptor access.
2. Simple production SPIR-V has no receiver debug resample, source-cache debug
   loop, rejection-counter atomic, or diagnostic-only gather payload.
3. Each in-domain corner performs at most one aligned 16-byte receiver-record
   load; state-invalid corners perform no irradiance or visibility atlas read.
4. Receiver records and the canonical atlases are published as one coherent
   transaction and fail closed through allocation, scroll, invalidation, abort,
   resize, reload, and device-loss paths.
5. Full/legacy DDGI remains available as a separately compiled backend when
   Simple DDGI is not selected. The task removes dual-backend code from a Simple
   binary; it does not delete the supported legacy backend globally.
6. Opaque, transparent, weighted-transparent, foliage, fog, and particle
   consumers use a backend-correct receiver ABI. Simple update/solver compute
   shaders continue to use the compute state unless a later plan deliberately
   changes their transaction model.
7. Correctness, synchronization, memory, shader-binary, image-quality, and
   controlled performance gates in this plan pass.

## 2. Evidence and current baseline

### 2.1 Timing baseline

The accepted identity-locked `final-v13` production runs report:

| Metric | Run A | Run B | Run C | Interpretation |
|---|---:|---:|---:|---|
| GPU frame P95 | 26.253 ms | 26.509 ms | 26.308 ms | Renderer-wide and outside this task alone |
| Opaque forward P95 | 19.616 ms | 19.866 ms | 19.541 ms | Inclusive target pass, not DDGI-only time |
| GI GPU P95 | 1.394 ms | 1.381 ms | 1.329 ms | Simple update work is already below its existing gate |
| Simple-DDGI Upload P95 | 1.760 ms | 1.731 ms | 1.732 ms | Separate CPU scheduling/upload problem |

The detailed capture classified 2,032,512 one-gather pixels and 496,185
two-gather pixels, plus 10,193 recovery gathers. All 506,378 second gathers were
attributed, for a classified second-gather fraction of 19.94%. That evidence
does not justify replacing the eight-corner estimator or removing the valid
second-volume/recovery behavior in this task.

The 19.5-19.9 ms number includes material evaluation, lighting, shadows,
reflections, GI composition, and other forward work. It is a comparison metric,
not a claim that DDGI costs 19.5-19.9 ms. The implementation must use staged A/B
variants and shader profiling to attribute any improvement.

### 2.2 Current shader shape

[`forward.frag`](../Njulf.Shaders/forward.frag) currently:

- includes the 2,500-line [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl);
- defines the full legacy DDGI result, selection, sampling, exhaustive fallback,
  composition, and diagnostic implementation in the same translation unit;
- reads Simple-DDGI parameters and selects Simple versus full DDGI at runtime;
- maps a `SimpleDdgiGatherResult` into the legacy-shaped `DdgiSampleResult` so
  shared debug and composition paths can consume it;
- initializes and carries numerous Simple-DDGI debug fields in ordinary forward
  code;
- can call `SampleSimpleDdgiDebug`, rereading probe state and visibility; and
- retains source-cache radiance, rejection mask/reason, cascade contributor,
  fallback, and other DDGI debug branches in production-shaped source.

The current shader filenames containing `simple` describe a simpler material or
vertex-input path (`FORWARD_SIMPLE_OPAQUE` and
`FORWARD_SIMPLE_VERTEX_INPUT`). They do not select the Simple-DDGI backend. The
build currently has no unambiguous forward-GI-backend compile definition.

[`Njulf.Shaders.csproj`](../Njulf.Shaders/Njulf.Shaders.csproj) invokes the shader
compiler separately for many opaque/output/provenance combinations, but each
forward fragment translation still receives both DDGI implementations. Release,
ShippingPerformance, and ProfileSymbols compile detailed counter atomics out;
that alone does not remove runtime debug branches, debug payload, source-cache
debug sampling, or the legacy receiver.

### 2.3 Current gather and state cost

`SampleSimpleDdgiVolumeGather` currently performs this sequence for every valid
one of the eight corners:

1. derive `probeIndex`;
2. read `SimpleDdgiProbeState`;
3. derive relocated position and direction;
4. sample irradiance;
5. sample visibility moments;
6. evaluate state and atlas rejection reasons; and
7. accumulate support, visibility, and radiance.

`SimpleDdgiProbeState` is 32 bytes:

| Field | Receiver use | Compute/scheduler use |
|---|---|---|
| `relocation.xyz` | Yes | Yes |
| `activeWeight` | Yes | Yes |
| fresh/scroll/inactive/relocation flags | Yes | Yes |
| source-cache-invalid flag | No | Yes |
| physical-slot generation | No | Yes, publication safety |
| `age` | No | Yes |
| `classification` | Only inactive/active result | Yes |
| `luminanceChangeEma` | No | Yes |

The shader reads the record as two aligned `uvec4`/`vec4` loads. A full
eight-corner cell therefore requests 256 bytes of state before cache effects,
then performs up to eight irradiance and eight visibility samples. State-invalid
probes still reach both atlas samples because rejection happens afterward.

The sampled-image fast path uses hardware filtering for ordinary interior taps;
the SSBO seam path performs four texel reads per atlas sample. This plan preserves
both paths and improves the state/addressing and early-rejection work around
them. It does not claim to reduce the worst-case count of valid-probe atlas
samples.

### 2.4 Current resource and frame boundary

[`SimpleDdgiVolumeManager`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
owns one compute-writable probe-state buffer. CPU initialization/invalidation,
relocation/classification, blending, generation checks, scheduler feedback, and
readback all share that ABI.

The production graph currently declares both Simple and full DDGI resources as
forward readers. Opaque forward runs before the Simple update chain and sees the
previous complete publication. `SimpleDdgiPublishPass` runs after trace,
relocate/classify, transport, and blend; transparent work runs after publication
and may see the current complete publication. The new receiver record must
preserve this boundary exactly.

The render graph and async-compute identity currently know the compute state but
not a separate receiver record. Merely changing GLSL would leave false graph
dependencies and would omit required publication/queue-transfer barriers.

## 3. Scope and non-goals

### 3.1 In scope

- Explicit forward GI backend selection at shader-build and pipeline-profile
  boundaries.
- A dedicated compact Simple-DDGI receiver buffer and stable bindless index.
- CPU and GPU packing/unpacking contracts for the compact record.
- Transactional compact-record initialization, invalidation, and publication.
- Splitting Simple-DDGI core/update, receiver gather, and diagnostic shader
  includes.
- Splitting legacy forward receiver code out of the Simple translation unit.
- A minimal production gather result and optional diagnostic sidecar.
- Early state rejection before atlas reads and one-time atlas-address derivation
  per surviving corner.
- Precise render-graph resources, barriers, async identities, and pipeline
  selection for the active backend.
- Migration or explicit backend specialization for all receiver call sites.
- Source, ABI, SPIR-V, model, image, Vulkan-validation, and performance tests.
- Removal of temporary benchmark/reference toggles after qualification.

### 3.2 Non-goals

- Changing probe placement, ray directions, trace shading, atlas formats,
  visibility moments, octahedral filtering, trilinear interpolation, or leak
  equations.
- Reducing the eight corners for a valid interpolation cell.
- Removing valid Simple-DDGI authored/ring volume fallback, overlap blending,
  bounded recovery gather, far-field/environment energy complement, or
  sky-visibility behavior.
- Deleting the full DDGI implementation or preventing users from selecting it.
- Folding scheduler generation, age, convergence, source-cache, or luminance
  state into the receiver ABI.
- Making update/solver compute shaders consume the published receiver record in
  the same update transaction.
- Solving the separate 1.7 ms CPU upload/scheduling issue; the GPU-resident
  scheduler plan owns that work.
- Promoting an uncertified async-compute path.
- Treating an inclusive opaque-forward timing change as DDGI attribution without
  shader and controlled A/B evidence.

## 4. Non-negotiable invariants

### 4.1 Receiver equivalence

- A probe rejected by current receiver-visible state remains rejected after the
  split.
- Fresh, newly scroll-exposed, explicitly inactive, inactive-classified,
  relocation-pending, zero-weight, invalid-irradiance, and invalid-visibility
  conditions remain distinguishable in diagnostic builds.
- Active-weight participation remains continuous; it is not converted to a
  Boolean except for the existing `<= 0.001` rejection boundary.
- Relocation decoding is relative to the selected volume spacing and is bounded
  tightly enough that interpolation, visibility distance, and seam behavior do
  not materially change.
- Irradiance and visibility alpha/validity metadata remain authoritative after a
  compact state record passes its early checks.
- Volume selection uses the unbiased receiver position; bias remains an
  interpolation/visibility detail.
- Environment radiance complements missing Simple-DDGI ownership exactly as it
  does today. It is not the legacy-backend fallback that this task removes.

### 4.2 Publication and ownership

- The 32-byte compute state remains the authority for update eligibility,
  generation, convergence, and diagnostics.
- The compact record is a published projection, never the authority used to
  validate a queued update.
- GPU publication checks the queue entry against the current compute-state
  generation before writing a receiver record.
- A receiver record that exposes a valid atlas address is written only after the
  matching canonical atlas/state transaction is complete.
- A newly allocated, remapped, invalidated, or newly exposed slot is made
  receiver-invalid before forward can interpret its previous atlas contents as
  the new world cell.
- Aborted or stale transactions cannot publish a valid new receiver record. An
  unchanged slot retains its previous complete record; an invalidated/remapped
  slot remains fail-closed.
- CPU code may publish fail-closed invalidation records. Only the validated GPU
  publication path may turn a record valid during normal operation.
- CPU readback lag must never cause an old CPU copy of probe state to overwrite a
  newer GPU-published valid receiver record.

### 4.3 Build and runtime selection

- A Simple production binary never branches to or references full DDGI.
- A full-DDGI production binary never references the Simple receiver record.
- Environment-only/emergency operation selects an environment-only pipeline or
  a guaranteed invalid Simple snapshot; it never reintroduces dual-backend
  runtime selection.
- `ProfileSymbols` uses production control flow plus symbols. It must not silently
  use diagnostic control flow and invalidate profiling results.
- Runtime backend changes rebuild or select a pipeline profile before draws use
  it. A stale pipeline compiled for the previous backend is not allowed.

### 4.4 Stable and disabled behavior

- No stable-frame CPU loop over all probes is added.
- No receiver-state readback is added.
- Receiver data adds no per-frame upload when topology and CPU-owned
  invalidations are unchanged.
- Disabled Simple DDGI adds no dispatch solely to maintain the compact buffer.
- All bindless slots remain valid or point at an intentionally invalid dummy
  resource during lifecycle transitions.

## 5. Target architecture

The intended frame data flow is:

```text
CPU allocation / topology / fail-closed invalidation
                         |
                         v
       32-byte compute state --------> 16-byte receiver record (invalid only)
                |                                  |
                |                                  v
                |                     opaque reads previous complete snapshot
                v
 trace -> relocate/classify -> transport -> blend -> canonical atlas publish
                |                                  |
                +------ generation validation -----+
                                                   v
                                  publish compact receiver record
                                                   |
                                  sampled-atlas mirror completes
                                                   v
                         transparent/fog/particle/future frame readers
```

The record and atlas remain single published resources unless async validation
proves double buffering necessary. Existing atlas hazards already serialize
opaque reads against later update writes; the receiver buffer joins the same
atomic resource set.

### 5.1 Shader module boundary

Refactor the monolithic include into these responsibilities:

| Proposed source | Responsibility | Consumers |
|---|---|---|
| `ddgi_simple_core.glsl` | Constants, params, volume mapping, atlas primitives, compute state/update ABI, shared math | Simple compute shaders; receiver include only where needed |
| `ddgi_simple_update_shared.glsl` | Source cache, ray/update writes, generation helpers, solver-only gather/accessors | Trace, relocate, transport, blend, publish |
| `ddgi_simple_receiver.glsl` | Compact record decode, volume selection, production gather, atlas sampling | Simple forward, fog, particle, foliage receiver variants |
| `ddgi_simple_receiver_diagnostics.glsl` | Rejection reasons/counters, source-cache comparison, debug sample and diagnostic payload | Debug/DetailedInvestigation receiver variants only |
| `ddgi_full_receiver.glsl` | Legacy forward DDGI result, gather, fallback, composition adapters | Full-DDGI forward/fog/foliage variants only |

Keep `ddgi_simple_shared.glsl` temporarily as a compatibility umbrella that
includes the core/update files for compute shaders. Remove the umbrella after
all call sites have explicit includes and source-contract tests no longer depend
on the monolith.

`common.glsl` should contain neutral renderer primitives. Move or guard any
legacy receiver functions currently embedded there so a Simple translation
unit never preprocesses them. Build defines must be visible before conditional
includes.

### 5.2 Backend vocabulary

Introduce one renderer-side `ForwardGiBackend` enum and mirrored shader values:

- `EnvironmentOnly`;
- `FullDdgi`; and
- `SimpleDdgi`.

Use an unambiguous definition such as
`NJULF_FORWARD_GI_BACKEND=SIMPLE_DDGI`; do not reuse
`FORWARD_SIMPLE_OPAQUE`. New artifact names must spell out `simpleddgi` versus
`simple_material` so logs, captures, and pipeline cache keys cannot confuse the
two axes.

Resolve the backend on the CPU:

1. `SimpleDdgi` when `EffectiveUseSimpleDdgi` is true and the manager has a valid
   receiver resource generation;
2. `FullDdgi` when `EffectiveUseDdgi` is true; otherwise
3. `EnvironmentOnly`, including emergency fallback and resource bring-up.

Do not select `FullDdgi` as a hidden fallback for a requested Simple backend.
During a transient Simple resource rebuild, either retain the previous complete
Simple generation or select the environment-only profile.

## 6. Compact receiver ABI

### 6.1 Keep the compute ABI intact

Keep `GPUSimpleDdgiProbeState` at 32 bytes. It remains free to evolve with the
GPU-resident scheduling work. Do not rename its fields to receiver concepts or
remove `age`, generation, source-cache state, classification detail, or
`luminanceChangeEma` merely because forward no longer reads them.

The new structure is a separate 16-byte, `Pack = 4` CPU/GPU ABI named
`GPUSimpleDdgiReceiverProbe` (or one consistently chosen equivalent). Its GLSL
representation is one `uvec4`:

| Word | Encoding | Meaning |
|---:|---|---|
| 0 | two signed normalized 16-bit values | relocation X/Y relative to `0.5 * volume.spacing` |
| 1 | signed normalized 16-bit Z + unsigned normalized 16-bit weight | relocation Z and active/validity weight |
| 2 | receiver-only bit field | coherent-publication and exact receiver rejection flags |
| 3 | unsigned atlas probe slot | shared irradiance/visibility logical address; `0xffffffff` is invalid |

The present relocation shader caps relocation length at `0.45 * spacing`.
Encoding against `0.5 * spacing` leaves explicit headroom and gives a maximum
per-component quantization error of approximately
`0.5 * spacing / 32767`, far below the current `0.02 * spacing` relocation-change
threshold. Move the relocation range constants needed by both publisher and
packer into one shared contract and add a test that fails if the update-side
maximum reaches or exceeds the receiver encoding range.

Use an explicit signed-normalized packer rather than depending on implementation
casts. CPU and GLSL helpers must agree on endpoint mapping, rounding, negative
zero, and the `-32768` representation. Pack active weight as UNORM16. Canonicalize
weights at the existing rejection threshold so quantization cannot turn a
currently rejected `<= 0.001` probe into an accepted probe or vice versa outside
one defined quantization unit.

Do not silently clamp invalid input. A non-finite relocation/weight, non-positive
spacing, relocation outside the declared encoding range, or out-of-range atlas
address publishes an invalid sentinel record and increments a bounded diagnostic
in diagnostic builds. CPU validation should reject impossible authored/layout
data before it reaches this point.

### 6.2 Receiver flags

Define a new receiver flag namespace instead of exposing compute flag bit
positions as a permanent ABI. It needs at least:

- `PUBLISHED_COHERENT`;
- `FRESH`;
- `SCROLL_EXPOSED`;
- `RELOCATION_PENDING`;
- `INACTIVE_FLAG`; and
- `INACTIVE_CLASSIFICATION`.

Keeping the two inactive reasons distinct costs no extra load and preserves the
diagnostic rejection vocabulary. Do not copy source-cache-invalid, age, update
generation, or luminance-change state. Generation safety is enforced before
publication, not reevaluated by every receiver.

The production early-state rejection mask is the OR of:

- missing `PUBLISHED_COHERENT`;
- invalid atlas sentinel;
- fresh/scroll/relocation/inactive flags; and
- decoded weight at or below `0.001`.

Irradiance `w <= 0.5` and visibility `z <= 0.5` remain post-sample rejection
conditions.

### 6.3 Atlas address contract

Word 3 is a logical canonical atlas probe slot, not an assumed copy of the state
array index. The initial implementation maps physical probe slot to the same
atlas slot, but all receiver helpers consume the stored address. This creates an
actual ABI boundary and permits future atlas compaction/remapping without
changing scheduler state.

Derive a local `SimpleDdgiAtlasAddress` once after early state validation. It
contains the values required by the active storage path:

- SSBO base for the irradiance tile;
- SSBO base for the visibility tile; and
- sampled-image group/layer when the sampled mirror is enabled.

Reuse it for both samples. Keep the logical slot in the persistent record rather
than packing current sampled-image descriptor-group geometry into the ABI;
`sampledAtlasLayersPerTexture` is a resource-generation property and can change
without rewriting otherwise valid records.

Validate address multiplication and descriptor group/layer bounds before
sampling. An invalid address fails closed and is separately visible in a
diagnostic build.

### 6.4 Memory accounting

The compact record halves receiver state bandwidth, but it does not halve total
persistent state memory because the compute state remains. It adds 16 bytes per
admitted probe. Update:

- `SimpleDdgiLayoutCompiler` and its capacity/admission hashes;
- diagnostics and performance snapshot fields;
- hard/soft memory estimates and headroom;
- allocation/reallocation identities; and
- disabled/stable allocation expectations.

The existing memory-headroom release gate remains at least 20%. Do not hide the
new allocation by subtracting bytes from the still-live compute state.

## 7. Receiver publication and synchronization

### 7.1 Resource ownership

Add a dedicated `SimpleDdgiReceiverProbeBuffer` to
`SimpleDdgiVolumeManager`, sized as `probeCapacity * 16` and exposed read-only to
production receivers.

Append a stable bindless storage index at the then-current end of
[`BindlessIndexTable.cs`](../Njulf.Rendering/Descriptors/BindlessIndexTable.cs).
At the time of this plan, the expected new index is 188, after
`EnvironmentGiDataBuffer`; do not insert it at index 174 and shift the existing
far-field/environment ABI. Mirror the value in `common.glsl`, index-name helpers,
uniqueness tests, and descriptor registration.

Register an invalid dummy record when Simple DDGI is disabled or resources are
being replaced. Destroy/retire the real buffer under the same completion-token
rules as the canonical atlas and compute state.

### 7.2 CPU write rules

CPU code writes receiver data only for lifecycle and fail-closed transitions:

- clear every newly allocated capacity slot to invalid sentinel;
- invalidate all affected slots before topology replacement, incompatible
  recenter, physical-slot remap, clear, or reload;
- invalidate newly exposed toroidal slots before opaque forward;
- invalidate CPU-detected layout/generation failures before their old atlas can
  be interpreted under new ownership; and
- optionally initialize a record from known-complete imported data only through
  an explicit validated import path.

Use the same bounded dirty ranges already identified for state invalidation. A
stable frame issues no receiver upload. Never rebuild and upload valid records
from frame-late `_probeStateScratch`; it can lag a GPU publication.

Issue transfer/host-write to graphics/compute shader-read barriers for the exact
consumer stages: fragment, mesh, vertex, and compute as applicable. Tests must
prove that invalidation is visible to opaque forward in the same frame.

### 7.3 GPU publication

Extend `GPUSimpleDdgiPublishPushConstants`, `SimpleDdgiPublishPass`, and
`ddgi_simple_publish.comp` with the receiver buffer index.

For every accepted queue entry:

1. validate probe index, volume index, queue generation, and physical-state
   generation;
2. complete the V2 private-to-canonical irradiance copy, or recognize the V1
   canonical blend result;
3. ensure all workgroup atlas writes are complete before the publishing lane
   proceeds;
4. read the final compute state;
5. pack relocation relative to the validated volume spacing;
6. normalize receiver flags and assign the logical atlas slot;
7. write the four record words, writing the coherent/publication word last if
   the raw-storage write API cannot guarantee a single 128-bit store; and
8. make the write visible to subsequent receiver stages only when the complete
   publish pass, including any sampled-image mirror, has finished.

Prefer one aligned `uvec4` store if the storage helper and generated SPIR-V
guarantee it. If four scalar stores are required, publish fail closed first,
write payload/address, then publish the final coherent flag with an explicit
buffer memory ordering boundary. A reader must never accept a record whose
payload belongs to a different publication.

The current V1 canonical publish shader returns early because blend writes the
canonical atlas directly. Refactor that branch so V1 still writes the receiver
record after blend. V2 continues to copy private irradiance first. Visibility is
already part of the transaction and must be complete before record validity is
exposed.

If the optional sampled atlas is enabled, transparent/fog/particle consumers
must not read the new receiver record until both irradiance and visibility image
mirrors are complete. This can be achieved by keeping record and image dispatches
inside one render-graph pass with an end-of-pass dependency, or by moving the
record commit to a final lightweight commit dispatch. Choose the version whose
SPIR-V and timestamp evidence is better; do not rely on command recording order
without memory visibility.

### 7.4 Abort and stale-work behavior

- A generation mismatch writes nothing; the prior complete record remains for
  an unchanged slot.
- A slot invalidated for new spatial ownership remains invalid if its queued work
  becomes stale or aborts.
- Relocation-pending/inactive final state may publish a coherent record carrying
  rejection flags; production receivers then skip both atlases.
- Resource-generation replacement starts with a fully invalid new buffer and
  does not inherit addresses from the retired generation.
- Device loss/recreation follows allocation semantics, not an attempted replay
  of uncompleted publication.

Model these rules explicitly rather than inferring them from the current state
buffer contents.

### 7.5 Render graph and async compute

Add `RenderGraphResourceId.SimpleDdgiReceiverProbes` and register its buffer set.
Update production resource declarations so:

- Simple opaque/transparent/weighted/foliage/fog/particle readers declare the
  compact buffer and Simple atlases, not the compute state or full-DDGI state;
- full-DDGI receiver variants declare only full-DDGI receiver resources;
- environment-only variants declare neither backend;
- Simple update passes keep the compute state;
- Simple publish declares a compute write to the receiver buffer; and
- no broad union declaration creates a false forward dependency on both
  backends.

Pass a render feature/profile object containing `includeSsgi` and
`ForwardGiBackend` into graph declaration/registration/validation. A backend
change must trigger graph recompilation or select a precompiled graph with the
exact same resource profile.

Add the receiver buffer to `SimpleDdgiAsyncBufferIdentity`, async resource
bindings, ownership-transfer compilation, state projection, and the atomic
Simple update segment. Required scopes include:

- prior graphics receiver read -> compute write;
- compute write -> later graphics/compute receiver read;
- CPU invalidation transfer/host write -> graphics receiver read; and
- graphics/compute queue-family ownership transfer when the certified async path
  is forced for validation.

Do not certify or promote async compute as part of this change. Re-run the async
resource audit because the identity and publication set changed.

## 8. Production gather design

### 8.1 Two-stage corner evaluation

Implement each volume gather in two clear stages. A compiler may unroll them,
but the source contract must retain the early-rejection ordering.

Stage A, receiver metadata:

1. compute cell base, affine fractions, and trilinear weights;
2. reject out-of-domain corners without reading a record;
3. derive the physical state slot;
4. perform one aligned `uvec4` receiver-record load;
5. decode flags, address, active weight, and relocation;
6. reject non-coherent, sentinel, state-invalid, or zero-weight records; and
7. cache the surviving relocated position, direction, distance, trilinear
   weight, active weight, and atlas address.

Stage B, atlas and radiometry:

1. derive the reusable SSBO/image address once per surviving record;
2. read irradiance and visibility;
3. reject invalid atlas metadata;
4. compute directional and visibility selection weights; and
5. accumulate the same available, valid, directional, visible, and irradiance
   masses as the current estimator.

Do not introduce local arrays that spill merely to express two stages. Inspect
generated register/local-memory behavior. If explicit eight-element arrays
spill, use an inlined per-corner helper that preserves state-before-atlas order
or a small unrolled macro and prove the generated result.

### 8.2 Minimal production result

Define a production `SimpleDdgiReceiverResult` containing only values consumed by
normal forward composition:

- irradiance;
- valid, directional, and spatial support required for ownership;
- transport visibility required by leak attenuation/sky visibility;
- selected spacing/transition and primary/secondary ownership weights only if
  the non-debug composition truly consumes them.

Audit uses rather than copying the existing `SimpleDdgiGatherResult`. Values used
only for visualization—source-cache irradiance, contributor color, selected
volume IDs, rejection mask/reason/count, and debug fallback labels—belong in a
diagnostic sidecar compiled only when
`NJULF_SIMPLE_DDGI_RECEIVER_DIAGNOSTICS=1`.

Create a backend-neutral, minimal forward composition structure rather than
mapping the Simple result into the legacy `DdgiSampleResult`. Shared hybrid/SSGI
composition functions should consume radiometric quantities, not a legacy
backend's complete debugging vocabulary.

### 8.3 Production debug removal

In ShippingPerformance, Release, and ProfileSymbols Simple variants:

- do not declare source-cache debug accumulators;
- do not call `SampleSimpleDdgiProbeSourceCacheIrradiance`;
- do not compute rejection reason masks when a Boolean/state test suffices;
- do not increment gather/rejection atomics;
- do not call `SampleSimpleDdgiDebug`;
- do not carry volume contributor colors/IDs solely for views;
- do not retain DDGI cascade/rejection/fallback debug branch fanout; and
- do not bind the compute state solely for debug.

Debug and DetailedInvestigation build diagnostic Simple artifacts that include
the sidecar. If a diagnostic view needs generation or source-cache data omitted
from the compact record, that diagnostic artifact may explicitly read the
compute state. Production graph declarations remain free of that dependency.

The UI/settings layer must report DDGI receiver views as unavailable in a
production artifact rather than accepting a mode whose code was compiled out.
Non-DDGI material, shadow, lighting, and general renderer debug features are not
removed by this task.

### 8.4 Fallback semantics

Use this exact distinction:

| Behavior | Simple production binary |
|---|---|
| Runtime switch to full/legacy DDGI | Remove |
| Legacy tile-header interpretation and exhaustive volume scan | Remove |
| Full-DDGI readiness/cache fallback | Remove |
| Structured-gather-enabled runtime fallback to legacy | Remove; CPU profile owns readiness |
| Invalid/unavailable Simple resource | Fail closed to environment-only lighting |
| Fine-to-coarse Simple volume fallback | Preserve |
| Authored/ring overlap blend | Preserve |
| Bounded recovery gather | Preserve |
| Missing-ownership environment complement | Preserve |
| Far-field and sky-visibility complement | Preserve |

This prevents “compile out fallback machinery” from accidentally deleting
radiometric correctness behavior.

## 9. Shader build and pipeline selection

### 9.1 Declarative variant matrix

Replace repeated hand-written compile/copy/embed commands with MSBuild items whose
metadata declares:

- source;
- material/input path;
- output attachment profile (SSGI trace source versus DDGI-only);
- GI backend;
- provenance output;
- transparency mode; and
- diagnostic capability.

Generate only combinations that a pipeline can select. Validate unique logical
resource names and emitted output paths. Suggested unambiguous names include:

- `forward_opaque_simpleddgi_*.frag.spv`;
- `forward_opaque_fullddgi_*.frag.spv`;
- `forward_opaque_environment_*.frag.spv`;
- `forward_transparent_simpleddgi.frag.spv`;
- `forward_weighted_oit_simpleddgi.frag.spv`; and
- backend-specific foliage/fog variants.

Use `simple_material` or the existing material-path metadata for
`FORWARD_SIMPLE_OPAQUE`; never rely on the bare filename segment `simple` to
identify the GI backend.

Debug/DetailedInvestigation adds the diagnostic define. Release,
ShippingPerformance, and ProfileSymbols set it to zero. Keep the existing
production atomic verifier and extend it to the new artifacts.

### 9.2 Pipeline profile

Make `MeshPipeline`, `FoliagePipeline`, fog, and relevant particle pipeline
creation accept a complete immutable shader profile. Include at least:

- `ForwardGiBackend`;
- SSGI trace-output attachment presence;
- provenance attachment presence;
- diagnostic capability; and
- material/input path where it changes the artifact.

Use the profile in shader resource names and pipeline-cache keys. On a settings,
emergency-fallback, render-attachment, or receiver-resource-generation change,
either select an already created exact profile or create it and retire the old
pipelines after GPU completion. Do not mutate a pipeline object's backend field
while old command buffers can still reference it.

Add a runtime assertion at draw recording that the scene's resolved backend,
render-graph profile, bound receiver generation, and pipeline profile agree.
Capture these values in diagnostics and benchmark identity.

### 9.3 SPIR-V contract audit

Add a build step or test helper using `spirv-dis`/`spirv-val` that checks the
actual binary, not only preprocessed source.

A production Simple forward artifact must have:

- no reference to `DDGI_PROBE_STATE_BUFFER_INDEX`, full-DDGI atlas/state slots,
  or legacy scheduler/tile resources;
- no reference to `SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX`;
- a reference to the compact receiver slot;
- no DDGI diagnostic `OpAtomicIAdd`;
- no source-cache debug traversal;
- no local-memory spill introduced by the gather rewrite; and
- one receiver-record load sequence per in-domain corner in disassembly/profile
  evidence.

A full-DDGI artifact must not reference the compact Simple buffer. A diagnostic
Simple artifact must retain the expected debug features, proving they were moved
rather than accidentally deleted.

## 10. Receiver call-site migration

Audit every call to `SampleSimpleDdgiGather`, `SampleSimpleDdgiIrradiance`, and
`SampleDdgiAmbientIrradiance`.

| Consumer | Required action |
|---|---|
| `forward.frag` opaque material variants | Select Simple-only include/artifact and compact production result |
| regular transparent forward | Build/select Simple-only variant; reads current post-publish snapshot |
| weighted OIT forward | Build/select Simple-only variant; no generic dual-backend artifact |
| `fog.comp` | Build backend-specific variants; Simple variant uses compact receiver, full variant uses full DDGI |
| `particle.vert` | Replace monolithic Simple include with compact receiver include; non-Simple mode emits no Simple GI |
| `foliage_mesh.mesh` and `foliage_grass.mesh` | Add backend-specific mesh variants; Simple variant gathers from compact receiver without fragment-tile candidates |
| `foliage_forward.frag` | Select output/backend-consistent pipeline artifacts and keep interpolated ambient contract coherent |
| Simple trace/transport/solver compute | Keep compute state and update-side gather/accessor; do not bind compact publication as same-transaction authority |
| CPU probe/debug overlays | Continue using full state readback; no receiver readback |

Forward fragment tile candidates depend on `gl_FragCoord`. Keep that candidate
path local to fragment receiver builds. Foliage mesh, fog, and particle receiver
helpers must use bounded volume metadata selection without pretending screen-tile
candidates are available.

The foliage mesh shaders currently call the legacy ambient helper even when the
renderer can select Simple DDGI. Treat this as a backend-correctness audit item;
do not leave foliage bound to the legacy state merely because its gather rate is
lower than opaque fragment shading.

## 11. Detailed implementation sequence

Each phase ends with its tests passing and a reviewable commit/diff boundary.
Do not combine all shader, ABI, resource, and pipeline changes into one
unattributable performance revision.

### Phase 0 - Freeze evidence and generate an artifact inventory

1. Reproduce one `final-v13` ProductionTiming run and verify benchmark identity,
   scene, camera path, resolution, settings, shader bundle hash, and GPU clocks.
2. Archive current forward SPIR-V, disassembly, byte size, descriptor references,
   shader statistics, and an Nsight range capture for opaque forward.
3. Record registers, occupancy, spills/local memory, global-load transactions,
   texture/SSBO utilization, instruction count, long-scoreboard/dependency stalls,
   and the relative cost of the gather region if source correlation is available.
4. Add a temporary benchmark-only selector capable of choosing the current
   reference artifact versus each staged artifact. It must be unavailable in
   ordinary shipping configuration.
5. Record the complete receiver call-site and render-graph resource inventory.

Exit: a reproducible baseline package exists and the old receiver remains
selectable only for controlled A/B validation.

### Phase 1 - Add explicit backend profiles without changing gather math

Files expected to change:

- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs`;
- `Njulf.Rendering/Pipeline/PipelineObjects/FoliagePipeline.cs`;
- fog/particle pipeline creation;
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`;
- `Njulf.Rendering/VulkanRenderer.cs`;
- `Njulf.Shaders/Njulf.Shaders.csproj`;
- shader library/resource lookup; and
- pipeline/profile tests.

Tasks:

1. Introduce `ForwardGiBackend` and immutable shader/graph profile records.
2. Resolve the backend from effective settings and resource readiness.
3. Refactor the MSBuild shader matrix to explicit backend metadata.
4. Compile a Simple artifact with the current gather first, guarded from legacy
   code by the new backend define.
5. Compile separate full and environment artifacts.
6. Make pipeline recreation/cache selection backend-aware.
7. Add diagnostics identifying the selected artifact and backend.
8. Run an A/B capture for compile-time specialization alone.

Exit: Simple forward no longer contains or can execute the legacy backend, but
still reads the old compute state. This isolates compile-size/register effects.

### Phase 2 - Define and allocate the compact ABI

Files expected to change:

- `Njulf.Rendering/Data/GPUStructs.cs`;
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`;
- `Njulf.Shaders/common.glsl`;
- the new Simple core/receiver includes;
- `Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs`;
- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`;
- diagnostics/performance snapshot contracts; and
- ABI/layout/index tests.

Tasks:

1. Add the 16-byte CPU structure and exact CPU pack/unpack helpers.
2. Add the GLSL structure and matching helpers.
3. Freeze relocation range, weight threshold canonicalization, flags, and atlas
   sentinel in mirror tests.
4. Append the bindless index without renumbering existing slots.
5. Add exact byte accounting to capacity/admission and diagnostics.
6. Allocate, register, clear, replace, and retire the buffer with the Simple
   resource generation.
7. Add invalid dummy registration.
8. Add bounded CPU fail-closed writes for initialization and topology/scroll
   invalidation only.

Exit: the compact buffer exists and is always safe to bind, but production still
uses the old state until publication tests pass.

### Phase 3 - Publish receiver records transactionally

Files expected to change:

- `Njulf.Shaders/ddgi_simple_publish.comp`;
- `Njulf.Shaders/ddgi_simple_publish_sampled.comp` or a new commit shader;
- `Njulf.Rendering/Data/GPUStructs.cs` push constants;
- `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`;
- `SimpleDdgiVolumeManager.cs` transaction/resource metadata;
- `RenderGraphResource.cs`;
- `ProductionRenderPipelineDeclaration.cs`;
- `VulkanRenderer.cs` async identities/bindings; and
- publication/render-graph/async tests.

Tasks:

1. Extend publish push constants with the receiver buffer.
2. Implement generation-validated V1 and V2 compact publication.
3. Prove atlas writes precede record coherence and sampled mirrors precede
   consumers.
4. Model invalidation and aborted transactions explicitly.
5. Add graph usage and queue-family ownership scopes.
6. Add a validation-only readback/test hook for a bounded record range; do not
   add production readback.
7. Compare decoded records against the matching completed compute state and
   atlas metadata over scroll/update scenarios.

Exit: record publication is coherent under graphics and forced-async validation,
including failure paths.

### Phase 4 - Split shader includes and switch the production gather

Files expected to change:

- `Njulf.Shaders/ddgi_simple_shared.glsl`;
- new `ddgi_simple_core.glsl`;
- new `ddgi_simple_update_shared.glsl`;
- new `ddgi_simple_receiver.glsl`;
- new `ddgi_simple_receiver_diagnostics.glsl`;
- new `ddgi_full_receiver.glsl`;
- `Njulf.Shaders/forward.frag`; and
- shader-model/source-contract tests.

Tasks:

1. Extract neutral, update-only, receiver-only, and diagnostic code without
   changing math.
2. Introduce the minimal backend-neutral forward GI composition result.
3. Add the compact record read and address helper.
4. Reorder corner evaluation so state-invalid probes skip atlas reads.
5. Preserve atlas-validity rejection and all support/ownership equations.
6. Remove mapping through legacy `DdgiSampleResult` in the Simple branch.
7. Compile debug sidecars only in diagnostic configurations.
8. Inspect disassembly after each structural change for spills, duplicated loads,
   or failed dead-code elimination.
9. Run the old-versus-new CPU/GPU gather oracle and deterministic HDR captures.
10. Run staged performance captures for compact load and early rejection.

Exit: production Simple forward consumes only the compact receiver ABI and meets
the binary contract.

### Phase 5 - Migrate remaining receiver consumers

1. Build/select Simple/full/environment fog variants.
2. Move particle Simple sampling to the receiver include.
3. Build Simple/full/environment foliage mesh variants and preserve its
   per-cluster/meshlet interpolation contract.
4. Build/select regular and weighted transparent Simple variants.
5. Remove compute-state reads from every production Simple receiver graph usage.
6. Keep update/solver compute includes on the compute ABI.
7. Validate each consumer before removing its generic dual-backend artifact.

Exit: the receiver ABI is consistent across rendering, and no hidden call site
keeps the legacy or compute-oriented state resident in a Simple production
translation unit.

### Phase 6 - Make render-graph declarations exact

1. Parameterize production declarations by the immutable feature/backend profile.
2. Remove union Simple/full receiver usages.
3. Add the receiver buffer as a publish output and exact consumer input.
4. Verify opaque uses the prior complete snapshot and later consumers use the
   post-publish snapshot.
5. Recompile dependency/barrier plans for SSGI on/off and provenance on/off.
6. Re-run graphics and forced-async Vulkan validation.

Exit: shader bindings, graph declarations, and actual accesses agree for every
profile.

### Phase 7 - Qualification and cleanup

1. Run the full automated, deterministic image, validation, memory, soak, and
   performance matrices.
2. Review HDR differences with locked exposure and a human visual pass.
3. Remove the benchmark-only old receiver selector and old generic dual-backend
   production artifacts.
4. Retain a test-only CPU/reference gather oracle.
5. Remove the compatibility umbrella include after all compute and receiver
   sources use explicit modules.
6. Update renderer settings/reference documentation and implementation status.
7. Rebaseline pipeline/shader bundle identities intentionally.

Exit: no shipping runtime fallback or dead artifact remains, and evidence is
archived beside the implementation status.

## 12. Verification plan

### 12.1 ABI and packing tests

Extend or add tests near:

- `FarFieldClipmapOracleTests.cs`;
- `SimpleDdgiLayoutCompilerTests.cs`;
- `SimpleDdgiShaderMirrorTests.cs`;
- `SimpleDdgiVolumeManagerTests.cs`; and
- `BindlessIndexTests.cs`.

Required cases:

- `Marshal.SizeOf<GPUSimpleDdgiProbeState>() == 32` remains true;
- `Marshal.SizeOf<GPUSimpleDdgiReceiverProbe>() == 16`;
- offsets are exactly 0, 4, 8, and 12;
- CPU and GPU mirror pack/unpack agree for zero, positive/negative endpoints,
  current `0.45 * spacing` extrema, tiny/large valid spacing, and randomized
  samples;
- decoded relocation error stays within the analytic SNORM bound;
- active-weight error stays within one UNORM16 step and preserves the `0.001`
  rejection contract;
- NaN, infinity, zero spacing, overflow relocation, invalid address, and sentinel
  inputs fail closed;
- inactive classification folds into its receiver flag without losing the
  explicit inactive-state reason;
- every live atlas address is in bounds and every uninitialized record is
  sentinel-invalid;
- the new bindless index is unique, mirrored in GLSL, named correctly, and does
  not renumber prior constants; and
- memory planning includes exactly 16 bytes per admitted receiver probe.

### 12.2 Publication model tests

Add deterministic model and integration cases for:

- new allocation starts entirely invalid;
- CPU invalidation is visible before opaque forward;
- first successful V1 publication;
- first successful V2 private-to-canonical publication;
- optional sampled-atlas publication;
- stale queue generation writes no record;
- aborted update retains an old complete unchanged slot;
- invalidated/remapped slot remains invalid after abort;
- fresh, scroll-exposed, inactive, and relocation-pending final states reject
  before atlas reads;
- successful relocation publishes matching relocation and atlas address;
- toroidal overlap retains old complete records while newly exposed physical
  slots fail closed;
- authored/ring topology change and physical offsets;
- resource growth/shrink, quality-tier change, reload, clear, device recreation,
  and device loss; and
- two in-flight frames cannot let delayed CPU readback overwrite GPU publication.

### 12.3 Gather oracle tests

Keep the pre-change estimator as test-only reference logic. Feed both paths the
same params, volume table, compute-state projection, irradiance, and visibility
data.

Cover:

- all eight corners valid;
- every individual state rejection reason;
- every individual atlas rejection reason;
- one to seven surviving corners;
- boundary and outside-domain cells;
- affine interpolation fractions at 0, epsilon, 0.5, and 1;
- front/back-facing directional weights;
- near-zero and full visibility;
- sampled interior and SSBO seam addressing;
- fine/coarse overlap, early ownership exit, second-volume use, and recovery;
- authored versus ring priority;
- environment complement with zero/partial/full ownership;
- thin-wall leak attenuation and sky visibility; and
- quantized relocation at maximum positive/negative displacement.

For full-precision reference inputs, all semantic classifications and volume
selection decisions must match. Numeric output differences must remain within
the documented packing/filter tolerance.

### 12.4 Shader build and source-contract tests

Update `ShaderBuildTests.cs` and `SimpleDdgiShaderMirrorTests.cs` so they assert
responsibility boundaries rather than brittle assumptions that everything lives
in `ddgi_simple_shared.glsl`.

Compile and validate every selected combination:

- Simple/full/environment backend;
- SSGI trace output on/off;
- default/simple-material/full-input material paths;
- provenance on/off;
- opaque/regular transparent/weighted transparent;
- foliage/fog/particle receiver variants; and
- production versus diagnostic configurations.

Inspect disassembly/reflect descriptors to enforce the contracts in section 9.3.
Also assert there are no duplicate embedded resource names and pipeline lookup
can resolve every declared profile.

### 12.5 Render graph and synchronization tests

Extend `ProductionRenderPipelineDeclarationTests.cs` and
`AsyncComputePhase3Tests.cs` to prove:

- Simple forward reads receiver records and Simple atlases only;
- full forward reads full-DDGI resources only;
- environment forward reads neither backend;
- Simple publish writes receiver records;
- update passes retain compute-state access;
- diagnostic Simple builds add compute-state/diagnostic resources only when
  required;
- opaque read -> update write and publish write -> later receiver read barriers
  have valid stages/accesses;
- transfer invalidation -> graphics read is present;
- queue-family release/acquire includes the receiver buffer; and
- graph/profile/pipeline backend mismatch is rejected.

Run Vulkan validation with:

- graphics-only execution;
- `ForceEnabledForValidation` for the exact Simple update path;
- sampled atlas on/off;
- SSGI on/off;
- provenance on/off;
- resize/reload/backend toggle; and
- two frames in flight under sustained movement.

No new validation message is accepted.

### 12.6 Image and scenario matrix

Use locked camera, exposure, scene seed, shader bundle, and HDR output. Capture:

- stationary representative plaza/Sponza view;
- slow camera motion;
- fast motion and toroidal recenter;
- ring transition and authored/ring overlap;
- fresh/invalid/relocation-pending populations;
- thin wall, doorway, corner, and occluded receiver cases;
- forced zero-support environment complement;
- SSGI disabled and hybrid SSGI;
- provenance output;
- masked materials and decals;
- regular and weighted transparency;
- foliage/grass;
- fog; and
- particles.

For static deterministic old/new comparisons, target linear-HDR relative RMSE
at or below 0.005, no changed volume/rejection classification, and no localized
seam/transition outlier outside the analytically predicted relocation tolerance.
Also retain the broader existing locked-reference gate and require human review
for leaks, darkening, popping, bands, and transition changes. If the numeric gate
fails, do not relax it before determining whether packing, publication, or the
reference itself is responsible.

### 12.7 Soak and lifecycle tests

Run at least 30 minutes with:

- continuous camera movement and repeated ring scroll;
- backend toggles;
- quality-tier/resource-capacity changes;
- sampled atlas enable/disable where supported;
- scene reload and volume add/remove;
- atmosphere/light changes; and
- graphics-only plus forced-async validation runs.

Require no stale-world flashes, address validation failures, unbounded diagnostic
growth, descriptor reuse, memory leak, device loss, or steady-state receiver
upload.

## 13. Performance attribution and acceptance

### 13.1 Staged experiments

Measure these revisions independently against the same baseline:

| Stage | Change isolated | Question answered |
|---|---|---|
| A | Current dual-backend, 32-byte state | Baseline |
| B | Compile-time Simple-only, old state/result | Benefit from removing legacy control/code pressure |
| C | Production result/debug split, old state | Benefit from removing debug payload/branches |
| D | 16-byte compact record, same atlas ordering | State-bandwidth/addressing benefit |
| E | Early state rejection before atlas reads | Benefit from skipping invalid-probe atlas work |
| F | Exact graph/resources and all consumers | Final integrated result |

For each stage, record shader hash, SPIR-V size/instruction count, descriptors,
registers, spills/local memory, occupancy, global load transactions, texture/SSBO
transactions, and relevant dependency stalls. Do not change scheduler budget,
probe count, rays, resolution, material settings, or camera path between paired
runs.

### 13.2 Timing protocol

Use the established ProductionTiming contract:

- 720 warm-up frames;
- 600 measured frames;
- three identity-locked runs per variant;
- P50/P95/P99 for opaque forward, GPU frame, GI GPU, Simple upload, and affected
  consumer passes; and
- repeatability review before accepting a delta.

Interleave A/B runs where practical to reduce clock/thermal drift. Record clocks,
power state, executable/shader bundle identities, scene/camera/settings hashes,
validation state, and async mode.

### 13.3 Mechanism gates

The final Simple production shader must show:

- receiver-state bytes per full valid eight-corner gather reduced from 256 to
  128 before cache effects;
- one rather than two aligned state vectors per corner;
- zero compute-state/legacy-state descriptor access;
- atlas accesses skipped for compact-state-invalid corners;
- lower SPIR-V size and instruction count than Stage A;
- no new spill or local-memory traffic;
- no register-count increase or occupancy loss unless an independently larger
  measured timing gain justifies it; and
- no DDGI diagnostic atomic/source-cache debug work.

### 13.4 Timing and regression gates

Promotion requires:

- a statistically distinguishable opaque-forward P95 improvement whose paired
  median is at least 0.5 ms or 2.5%, whichever is larger, on the primary target;
- no individual final run slower than the accepted baseline range after
  repeatability is established;
- no GPU-frame regression attributable to added synchronization;
- no Simple GI update-pass regression above 5% or 0.1 ms, whichever is larger;
- no Simple upload P95 regression above 0.1 ms and no stable-frame receiver
  upload;
- no material regression in transparent, foliage, fog, or particle passes; and
- at least 20% admitted memory headroom after the new persistent buffer.

If the timing threshold is not met, keep the stages separable and use Nsight
evidence to decide whether a structural ABI cleanup is worth retaining. Do not
claim the task as a performance success based only on smaller SPIR-V or the
inclusive pass moving within run-to-run noise.

## 14. Telemetry and diagnostics

Add low-overhead CPU-visible fields for:

- resolved forward GI backend and shader profile;
- receiver resource generation, capacity, and bytes;
- CPU invalidation bytes/ranges this frame;
- GPU receiver records published this frame;
- invalid publication attempts by reason, frame-late and diagnostic-only;
- packed relocation overflow/non-finite failures;
- invalid atlas-address failures;
- production versus diagnostic receiver artifact;
- shader bundle/artifact identity; and
- whether sampled atlas, graphics queue, or forced async path was active.

Do not add per-pixel production atomics to measure early rejection. Use the
diagnostic artifact with sparse screen-tile weighting for investigation, then
return to the production artifact for timing.

## 15. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Packed relocation changes visibility/interpolation | Spacing-relative SNORM with 0.5-spacing range, analytic error bound, CPU/GPU mirror tests, HDR differential gate |
| Quantized active weight crosses rejection threshold | Canonical threshold packing and exhaustive boundary tests |
| New record is valid before atlas completion | Generation validation, fail-closed initialization, ordered commit, explicit graph barriers, sampled-mirror completion gate |
| CPU readback overwrites newer GPU publication | CPU writes invalidations only; GPU is sole normal valid publisher |
| V1 path never publishes because current shader returns early | Separate V1 receiver commit after blend and dedicated test |
| New spatial cell samples old physical-slot atlas | Invalidate before ownership remap; valid only after matching generation publishes |
| Simple shader still carries legacy code through `common.glsl` | Extract/guard receiver sections and inspect SPIR-V descriptor references |
| Debug code remains despite counter macro | Separate receiver diagnostic include/result/artifact and binary audit |
| Two-stage gather arrays spill | Inspect disassembly/Nsight; use inlined/unrolled helper while preserving early ordering |
| Build matrix becomes unmaintainable | Declarative metadata, only reachable combinations, unique-name/profile tests |
| Backend toggled without pipeline/graph rebuild | Immutable profile, cache key, draw-time consistency assertion, lifecycle tests |
| Full DDGI accidentally removed | Keep and test independent full artifacts; remove only dual-backend Simple artifact |
| Environment complement mistaken for legacy fallback | Explicit preserve/remove table and gather-oracle coverage |
| Other receiver still reads compute/legacy state | Complete call-site inventory, per-artifact SPIR-V audit, precise graph tests |
| Added buffer erodes memory target | Exact layout accounting/admission and 20% headroom gate |
| Async update races graphics receiver | Add resource to atomic identity/ownership set; forced validation before any certification |
| GPU scheduler plan changes compute state | Compact projection remains separate; publisher is the integration point |
| Inclusive timing is misattributed | Staged A/B variants plus shader metrics and interleaved three-run protocol |

## 16. Coordination with adjacent plans

### 16.1 GPU-resident scheduler

The compact record is deliberately downstream of scheduler/update state. If the
GPU-resident scheduler lands first:

- add the receiver allocation to the Simple resource arena and capacity hash;
- let the GPU invalidation/commit path perform fail-closed transitions;
- keep generation validation in the final publish/commit stage; and
- do not duplicate receiver fields in the scheduler ABI.

If this plan lands first, the later scheduler migration must preserve the compact
publisher and may replace only how eligible updates and CPU invalidations are
generated.

### 16.2 Atmosphere/source cohorts

A source-cohort generation remains compute/publication authority. It is not
stored in the receiver record. A record becomes coherent only after the existing
source/transport publication contract permits the matching atlas to publish.
Do not let the compact ABI shorten atmosphere propagation release conditions.

### 16.3 Async compute

The receiver buffer changes the Simple update segment's resource identity and
invalidates any prior resource-usage audit. Keep `Auto` uncertified until the
updated atomic set passes the graphics/compute queue topology, stage/access,
ownership, output-equivalence, and profitability gates in the async plan.

## 17. Definition of done

The implementation is complete only when all boxes are true:

- [ ] Explicit environment/full/Simple backend profiles select distinct shader
      artifacts and graph resources.
- [ ] Simple production forward SPIR-V contains no full-DDGI receiver, fallback,
      descriptor access, or compute-state access.
- [ ] Simple production receiver diagnostics/debug work is absent from SPIR-V.
- [ ] `GPUSimpleDdgiProbeState` remains 32 bytes and
      `GPUSimpleDdgiReceiverProbe` is exactly 16 bytes.
- [ ] One aligned compact record load replaces two compute-state vector loads per
      in-domain corner.
- [ ] State-invalid corners issue no irradiance or visibility atlas read.
- [ ] Relocation/weight packing meets analytic and HDR quality gates.
- [ ] Atlas address indirection is validated and used by both atlas samples.
- [ ] CPU invalidation and V1/V2 GPU publication are coherent and generation
      safe.
- [ ] Opaque sees the prior complete snapshot; later receivers see only a complete
      current publication.
- [ ] Async identities/transfers include the new resource and forced validation
      is clean.
- [ ] Opaque, transparent, weighted OIT, foliage, fog, and particles are
      backend-correct.
- [ ] Simple update/solver compute continues to use the compute ABI safely.
- [ ] Exact render-graph declarations no longer union Simple and full receiver
      resources.
- [ ] All ABI, model, source, build, graph, validation, image, lifecycle, and soak
      tests pass.
- [ ] Three-run controlled captures meet the mechanism, performance, regression,
      and memory gates.
- [ ] Temporary old-receiver benchmark selection and generic dual-backend
      production artifacts are removed.
- [ ] The final implementation status records hashes, captures, metrics, known
      limitations, and any deferred follow-up supported by evidence.
