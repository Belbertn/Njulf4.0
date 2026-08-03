# Aggressive Simple-DDGI Cache Packing and Partial Mirroring Implementation Plan

- Status: Planned
- Date: 2026-08-03
- Target: Simple DDGI with ray-query Transport V2
- Primary profiles: DDGI High and Ultra
- Primary goals: lower persistent VRAM, lower receiver-gather bandwidth, and preserve leak resistance
- Rollout rule: every representation change is qualified independently before the combined packed mode becomes a preset default

## 1. Required outcome

Reduce the Simple-DDGI working set and receiver-gather traffic without changing
the lighting model, admitting stale probe data, weakening visibility, or hiding
quality loss behind a looser global intensity or leak clamp.

The completed implementation must provide all of the following:

- visibility moments use one packed `RG16F` record per 16x16 atlas texel rather
  than an `RGBA16F` record whose last two channels duplicate validity;
- Transport V2 source radiance is stored in FP16 after finite/range validation;
- source distance uses FP16 only for volume classes whose conservative range and
  hit-position error bounds pass, while other volumes retain FP32 distance;
- deterministic ray directions are reconstructed from the probe, ray index,
  source-direction epoch, and fixed rotation codebook instead of being stored in
  every persistent source-cache entry or transient ray result;
- the optional filtered image mirror contains only complete, receiver-relevant
  volumes/rings in a compact layer space, with canonical SSBO fallback for every
  unmirrored volume and every octahedral seam sample;
- canonical volume admission is independent of whether the optional image
  accelerator fits; the mirror consumes only remaining admitted memory;
- the layout compiler, Vulkan allocations, shader address calculations,
  diagnostics, and capture reports use one authoritative byte plan;
- toggling an ABI or coverage mode forces a generation change, resource
  recreation, and cold source/atlas rebuild. Old bytes are never reinterpreted;
- image-difference, HDR perceptual, thin-wall leak, ring-transition, scrolling,
  and transport-convergence gates pass before a packed mode is enabled by a
  quality preset;
- measured residency decreases by the exact amount predicted by the memory plan,
  and receiver gather time/bandwidth does not regress.

This is a representation and working-set project. It must not change ray-query
counts, bounce equations, probe placement, solver stopping criteria, material
transport, visibility weighting, or per-tier scheduling policy.

## 2. Current implementation boundary

The plan is based on the current working-tree implementation, not an older DDGI
design document.

### 2.1 Canonical atlases

[`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
currently charges:

- 8x8 irradiance at 8 bytes per texel: 512 bytes per probe;
- 16x16 visibility at 8 bytes per texel: 2,048 bytes per probe;
- 2,560 bytes per probe for the canonical atlas pair.

Both buffers use the generic `ReadSimpleDdgiAtlasTexel` and
`WriteSimpleDdgiAtlasTexel` helpers in
[`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl). Each texel
is already stored as two `packHalf2x16` words. Irradiance needs RGB plus its
per-texel validity alpha. Visibility uses only mean and second moment in `.xy`;
`.z/.w` are written as `1.0` and are used as history/validity sentinels.

The visibility channels therefore cannot simply be deleted. The validity
contract must first move to probe state and publication ordering.

### 2.2 Persistent source cache and ray scratch

The current Transport V2 source cache is ABI version 2 and uses a fixed 36-byte
record for every global cache ray:

| Words | Payload | Current representation |
|---:|---|---|
| 0-3 | source radiance RGB + source hit distance | four FP32 values |
| 4 | traced ray direction | octahedral SNORM16x2 |
| 5 | canonical surface normal | octahedral SNORM16x2 |
| 6 | reflected diffuse RGB + material AO | UNORM8x4 |
| 7 | transmitted diffuse RGB | UNORM8x4 |
| 8 | physical-slot generation + valid/hit/backface flags | `uint` |

The matching C# record is `GPUSimpleDdgiTransportRayCache` in
[`GPUStructs.cs`](../Njulf.Rendering/Data/GPUStructs.cs). Cache indexing is
currently:

```text
(globalProbeIndex * raysPerProbe + directionRayIndex) * 9 words
```

The transient `GPUSimpleDdgiRayResult` is 32 bytes: FP32 radiance/distance,
three FP32 direction components, and a word containing FP16 visibility distance
and FP16 hit kind. Trace, relocate/classify, transport, and blend all consume
that stored direction.

The ray direction is deterministic, but the current frame quaternion changes
every frame. A cache entry can survive far longer than the frame that traced it,
so reconstructing from the current frame quaternion would be incorrect. A
persistent source-direction epoch is required.

### 2.3 Sampled image mirror

[`SimpleDdgiSampledAtlas.cs`](../Njulf.Rendering/Resources/SimpleDdgiSampledAtlas.cs)
currently allocates one `R16G16B16A16Sfloat` irradiance layer and one
`R16G16B16A16Sfloat` visibility layer for every provisioned canonical probe.
Global probe index maps directly to image group/layer.

The mirror has two synchronization paths:

- `CopyAll` performs a complete SSBO-to-image copy after allocation, clearing,
  or another full-sync event;
- [`ddgi_simple_publish_sampled.comp`](../Njulf.Shaders/ddgi_simple_publish_sampled.comp)
  dual-writes updated probes after canonical publication.

Interior octahedral quads use one hardware-filtered image sample. Seam quads
fall back to four canonical SSBO samples. The fallback is a correctness feature
and remains mandatory.

### 2.4 Existing validation and evidence infrastructure

The repository already contains the pieces needed for a real qualification
gate:

- [`SampleSponzaGiCaptureHarness.cs`](../NjulfHelloGame/SampleSponzaGiCaptureHarness.cs)
  provides locked cameras, random seed, warm-up, low/high endpoints, traversal,
  receiver ROIs, manifests, and diagnostic outputs;
- [`SampleMaterialGiCaptureComparison.cs`](../NjulfHelloGame/SampleMaterialGiCaptureComparison.cs)
  provides strict scene-linear image comparison and provenance validation;
- [`SampleMaterialGiApprovedHdrRegression.cs`](../NjulfHelloGame/SampleMaterialGiApprovedHdrRegression.cs)
  provides approved-reference HDR-FLIP, ROI, transition, and temporal metrics;
- [`SampleGlobalIlluminationValidation.cs`](../NjulfHelloGame/SampleGlobalIlluminationValidation.cs)
  defines Sponza, Cornell, thin-wall, long-corridor, vertical-ring, dynamic,
  teleport, and outdoor validation scenarios;
- [`SampleDdgiProductionGate.cs`](../NjulfHelloGame/SampleDdgiProductionGate.cs)
  already evaluates support, warm-up, visibility, and thin-wall leak policy;
- performance snapshots already expose canonical atlas, sampled image, source
  cache, ray scratch, and total DDGI bytes.

The implementation should extend these contracts rather than create an
unreviewed screenshot script.

## 3. Expected memory opportunity

The following table uses tier probe caps and configured maximum source-ray
counts. It is a theoretical upper-bound comparison; a real scene may admit fewer
probes. `Compact-28` means FP16 radiance, FP32 distance, no stored direction.
`Compact-24` additionally means a qualified FP16 distance.

| Tier | Probe cap | Rays/probe | Source cache now | Compact-28 | Compact-24 | Canonical visibility saving | Full-mirror visibility saving |
|---|---:|---:|---:|---:|---:|---:|---:|
| Low | 4,096 | 32 | 4.5 MiB | 3.5 MiB | 3.0 MiB | 4 MiB | 4 MiB, mirror normally off |
| Medium | 8,192 | 64 | 18 MiB | 14 MiB | 12 MiB | 8 MiB | 8 MiB, mirror normally off |
| High | 16,384 | 128 | 72 MiB | 56 MiB | 48 MiB | 16 MiB | 16 MiB |
| Ultra | 32,768 | 192 | 216 MiB | 168 MiB | 144 MiB | 32 MiB | 32 MiB |

If every source distance qualifies, source-cache plus canonical-visibility plus
full-mirror-format savings are approximately 5.5 MiB Low, 14 MiB Medium,
56 MiB High, and 136 MiB Ultra. Partial mirroring saves an additional:

```text
(provisionedTotalProbeLayers - provisionedMirroredProbeLayers) * 1,536 bytes
```

after the visibility image becomes RG16F. Direction-free ray scratch changes
the scratch stride from 32 to 20 bytes, saving:

```text
updateRequestCapacity * raysPerProbe * 12 bytes
```

These figures must be recomputed from actual accepted volumes and reported by
the runtime. The plan does not claim them as measured results.

## 4. Selected representation design

### 4.1 Use typed atlas APIs, not a stride-sensitive generic helper

Split the generic atlas API into explicit irradiance and visibility operations:

```text
Read/WriteSimpleDdgiIrradianceTexel      -> vec4, 2 words
Read/WriteSimpleDdgiVisibilityMoments    -> vec2, 1 word
SampleSimpleDdgiIrradianceBilinear       -> vec4
SampleSimpleDdgiVisibilityBilinear       -> vec2
```

Keep separate address helpers and byte constants. No caller may infer a texel
stride from a bindless index. This prevents a private irradiance target, compact
visibility buffer, or future atlas from accidentally using the other format.

The canonical formats become:

| Payload | SSBO format | Sampled-image format |
|---|---|---|
| irradiance | `RGBA16F`, 8 bytes/texel | `R16G16B16A16Sfloat` |
| visibility moments | `RG16F`, 4 bytes/texel | `R16G16Sfloat` |

### 4.2 Move visibility validity to probe state

Reserve probe-state flag bit 5 as
`SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID`. Bits 6-7 remain reserved and the
24-bit physical-slot generation remains in bits 8-31.

The flag has these semantics:

- CPU/GPU invalidation, new allocation, toroidal exposure, incompatible
  topology change, and relocation clear it before a slot can be gathered;
- a full visibility refresh writes every 16x16 moment texel, executes a storage
  memory barrier, then sets the flag at the same commit point that clears
  `FRESH`;
- a solver-only Transport V2 update preserves both moments and the valid flag;
- an inactive or failed publication clears the flag and cannot become visible;
- gather rejection tests the state flag, not sampled `.z`;
- adaptive visibility history treats non-fresh, visibility-valid state as valid
  history. It no longer reads `previous.z`;
- irradiance alpha remains unchanged. Removing irradiance validity is not part
  of this work.

This preserves the current all-texels-before-state publication invariant and
removes duplicated per-texel metadata.

### 4.3 Add per-volume source-cache regions

A fixed global stride cannot safely mix FP16-eligible and FP32-distance volumes.
Append one 16-byte cache-layout vector to `GPUSimpleDdgiVolume` and its GLSL
mirror. Use bit-preserving `uint`/float conversion for integer fields:

```text
CacheLayout.x = source-cache region base word
CacheLayout.y = source-cache stride words
CacheLayout.z = compact sampled-atlas first layer + 1; zero means unmirrored
CacheLayout.w = source-cache format, mirror payload, and ABI flags
```

Define `CacheLayout.w` once and mirror it verbatim:

```text
bits  0-1: source format (0 Legacy-36, 1 Compact-28, 2 Compact-24, 3 invalid)
bit      2: irradiance mirror payload present
bit      3: visibility mirror payload present
bits  4-7: storage ABI version
bits 8-15: direction codebook version
bits 16-31: reserved, written as zero and rejected if unexpectedly nonzero
```

Use named masks/shifts in C# and GLSL. Do not use overlapping Boolean aliases or
infer payload presence solely from a nonzero layer base.

The volume record grows from 96 to 112 bytes and
`SIMPLE_DDGI_VOLUME_STRIDE_WORDS` grows from 24 to 28. The params allocation is
still bounded to sixteen volumes, so this costs only 256 bytes.

Align every volume cache region to 16 bytes. Cache lookup becomes:

```text
volume.cacheBaseWord +
((probeIndex - volume.firstProbeIndex) * raysPerProbe + directionRayIndex) *
volume.cacheStrideWords
```

All trace, transport, source-cache debug, clear, capacity, and audit paths must
use the shared helper. No second hand-written address formula is permitted.

### 4.4 Define three source-cache formats

Keep the current 36-byte format only as a qualification/rollback ABI. Add:

| Format | Words | Bytes | Payload |
|---|---:|---:|---|
| Legacy-36 | 9 | 36 | current FP32 radiance/distance + stored oct direction |
| Compact-28 | 7 | 28 | FP16 radiance, FP32 distance, reconstructed direction |
| Compact-24 | 6 | 24 | FP16 radiance/distance, reconstructed direction |

`Compact-28` layout:

```text
word 0: FP16 radiance R/G
word 1: FP16 radiance B + reserved half
word 2: FP32 source distance
word 3: octahedral SNORM16x2 normal
word 4: reflected RGB + material AO UNORM8x4
word 5: transmitted RGB + reserved UNORM8x4
word 6: generation, flags, and direction epoch
```

`Compact-24` packs FP16 radiance B and FP16 source distance into word 1 and
shifts the remaining words down by one.

Do not use C# struct layout as the only byte authority. Add explicit packed
record structs for layout tests, but make the storage-layout compiler's word
counts the allocation authority used by both admission and Vulkan.

### 4.5 Reconstruct direction from a stored five-bit epoch

Use the currently free bits 27-31 of each cache entry's generation/flag word as
a five-bit source-direction epoch. Preserve bits 0-23 for physical-slot
generation and bits 24-26 for valid/hit/backface.

Generate a fixed 32-rotation codebook once with the documented integer hash and
quaternion algorithm, then check in its normalized quaternion components as
canonical IEEE-754 bit patterns. GLSL reconstructs them with `uintBitsToFloat`;
the C# mirror reconstructs the same bits for tests and diagnostics. Runtime C#
and GLSL code must not independently regenerate the table with platform math.
A full source refresh chooses an epoch from the monotonic frame/source sequence,
uses that rotation for every ray written by the probe transaction, and records
the epoch in every cache entry.

Direction reconstruction is:

```text
perProbeRotation = compose(codebook[sourceEpoch], hashRotation(probeIndex))
direction = Fibonacci(directionRayIndex, raysPerProbe, perProbeRotation)
direction = OctDecode(OctEncode(direction))
```

The final octahedral round trip deliberately matches the precision of the
direction currently read from the cache. It prevents the removal of a word from
also becoming an unreviewed direction-precision change.

Introduce this codebook while the stored direction still exists. In detailed
validation, compare stored and reconstructed directions and report maximum and
P99 angular error. Remove the direction word only after that shadow comparison
passes.

Centralize the mapping from queue-local ray ordinal to full source
`directionRayIndex`. Trace, transport, blend, relocation, and all CPU mirrors
must call the same helper. Maintenance subsets must reconstruct the exact cached
source direction, not a new compact sequence.

### 4.6 Remove direction from transient ray scratch

Replace the FP16 hit-kind half with exact bit metadata. The result metadata word
contains:

```text
bits  0-15: FP16 visibility distance
bits 16-18: exact hit kind 0..4
bits 19-23: source-direction epoch 0..31
bits 24-31: validity/reserved flags
```

The direction-free scratch record is:

```text
words 0-3: FP32 radiance + source distance
word 4: packed visibility/hit/epoch metadata
```

This is a 20-byte record. Keep scratch radiance/distance FP32 in this project so
persistent-cache quantization can be evaluated independently and source-refresh
work retains a high-precision transient oracle.

Trace writes the chosen or cached epoch. Relocate/classify reconstructs the
direction when it needs the nearest backface vector. Transport reconstructs it
for hit position and reflected/transmitted view direction. Blend reconstructs
once per ray into workgroup shared memory rather than once per atlas texel.

If measured blend ALU cost exceeds the bandwidth saving, retain an explicitly
documented 24-byte fallback scratch format with one packed oct direction. Do not
silently restore the 32-byte FP32 direction record.

### 4.7 Admit FP16 only through explicit range/error gates

FP16 radiance is eligible only after the writer:

- rejects non-finite values and marks the source cache invalid;
- clamps through one documented source-radiance function;
- records pre-clamp/saturation telemetry in validation builds;
- demonstrates zero saturation in the production capture matrix.

FP16 distance is a per-volume decision. Compute the same maximum trace distance
used by the shader from the accepted grid counts and spacing. `Compact-24` is
eligible only when all of the following hold:

1. the maximum is finite and no greater than 65,504;
2. the worst-case FP16 ULP over the permitted interval is no larger than one
   quarter of the transport hit-point offset;
3. the same ULP is no larger than ten percent of the configured conservative
   architectural thickness;
4. synthetic values at every half exponent boundary and captured P99/max hit
   distances satisfy the decoded hit-position error gate;
5. thin-wall and long-corridor image/leak gates pass.

Volumes that fail any condition use `Compact-28`; they do not disable FP16
radiance or compact storage for other volumes. A future normalized-UNORM or
log-distance encoding is out of scope and requires its own oracle.

### 4.8 Compile a compact receiver mirror

Add a pure sampled-atlas layout result containing complete volume ranges:

```text
canonical first probe
probe count
compact first layer
volume index/source identity
reason/priority
```

Never mirror only part of a volume. A gather may address any of its physical
slots after toroidal scrolling, and all eight corners must resolve through one
coherent representation.

Support three coverage modes during qualification:

- `FullCanonical`: current one-layer-per-global-probe behavior;
- `ReceiverRelevant`: compact receiver-owned volumes plus near/mid rings;
- `Disabled`: canonical SSBO only.

The selected production candidate for `ReceiverRelevant` is:

1. accepted authored volumes with `ReceiverHero`, `NavigableInterior`, or
   `DynamicInfluence` purpose, in existing ownership order;
2. accepted near ring;
3. accepted mid ring;
4. exclude `TransitionSupport` and far ring by default.

Phase 0 usage telemetry must confirm that this set serves at least 95 percent of
forward receiver interior-quad image opportunities. If it does not, add the
specific measured volume class or retain `FullCanonical`; do not lower the hit
rate gate to justify a predetermined result.

Compile canonical volume admission first, without optional image bytes. Then
admit complete mirror ranges in priority order from the remaining DDGI memory
budget. The optional accelerator may fall back or shrink, but it may never cause
a canonical receiver volume to be rejected.

Store `compactFirstLayer + 1` in the volume cache-layout metadata. Sampling
computes:

```text
compactLayer = compactFirstLayer + (probeIndex - volume.firstProbeIndex)
```

An unmirrored volume, an out-of-range layer, a disabled payload, or an
octahedral seam takes the canonical typed SSBO path. These are normal fallbacks,
not undefined descriptor accesses.

## 5. Non-negotiable invariants

### 5.1 Publication and validity

- A receiver sees either the prior complete probe or the new complete probe.
- `VISIBILITY_VALID` is set only after all moment writes for the matching slot
  generation are visible.
- Clearing or reallocating an atlas clears the corresponding state validity
  before the new bytes can be sampled.
- Solver-only work never rebuilds visibility from front-face-only cached source
  distances.
- A failed, stale, relocated, inactive, or generation-mismatched update cannot
  publish canonical or mirrored data.

### 5.2 Cache identity

- Cache validity requires matching physical-slot generation, source generation
  contract, ABI version, ray capacity, and volume storage layout generation.
- Every entry written by one full probe source refresh carries the epoch used to
  trace that entry.
- A maintenance subset maps to the same global Fibonacci indices as the full
  source sequence.
- A layout/stride/format/ray-count change invalidates the complete affected
  source region before reuse.
- Non-finite decode, invalid format bits, invalid stride, or address overflow
  fails closed and schedules source repair.

### 5.3 Mirror mapping

- Canonical probe index and compact mirror layer are never assumed equal in
  `ReceiverRelevant` mode.
- Mapping metadata and image descriptors become visible as one allocation
  generation.
- Full sync copies only declared canonical ranges to their matching compact
  layers.
- Incremental publication writes only when the updated volume is mirrored.
- A remap cannot sample an image layer left over from a different volume or
  physical-slot generation.
- Toroidal scrolling changes logical ownership, not compact physical-layer
  identity; newly exposed slots remain rejected until republished.

### 5.4 Memory and fallback

- The layout report and actual allocation agree byte-for-byte for canonical
  irradiance, compact visibility, source regions by format, scratch, state,
  readback, and mirror payloads.
- All arithmetic is checked on CPU and bounded before shader address
  calculations.
- At least the existing 16-byte graph-safe placeholders remain for absent
  buffers.
- Unsupported `R16G16Sfloat` sampled/storage/filter capabilities disable the
  visibility image payload or the optional mirror; canonical RG16F SSBO
  operation remains available.
- No preset raises `DdgiAtlasMemoryBudgetBytes` merely to make the new path fit.

### 5.5 Quality and performance

- Packing does not alter scheduling, ray counts, bounce equations, exposure, or
  accepted volume list during parity captures.
- Freed memory is retained as headroom during qualification. Reinvesting it in
  more probes is a separate, explicitly reviewed change.
- A representation that saves memory but creates a leak, ring seam, temporal
  flash, convergence change, or material-energy shift does not ship.
- Direction reconstruction must not move meaningful work from bandwidth to ALU
  and regress total GI GPU time.

## 6. Dependencies and implementation order

This work changes contracts referenced by three active plans:

- [`SunSkyDdgiAsyncReflectionRemainingImplementationPlan-20260803.md`](SunSkyDdgiAsyncReflectionRemainingImplementationPlan-20260803.md)
  requires exact per-ring source-cache ABI ray counts and source-generation
  enforcement;
- [`SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md`](SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md)
  freezes GPU queue/state/arena contracts and currently lists formats and cache
  replacement as non-goals;
- [`ErrorBoundedAcceleratedMultiBounceConvergenceImplementationPlan-20260803.md`](ErrorBoundedAcceleratedMultiBounceConvergenceImplementationPlan-20260803.md)
  consumes the source cache and canonical/private irradiance field.

Preferred order:

1. finalize the atmosphere/source-cohort generation contract;
2. land the storage-layout metadata, typed atlas helpers, and packed ABI from
   this plan;
3. freeze the GPU-resident scheduler queue/state ABI against the new epoch and
   stride fields;
4. implement the error-bounded solver using the shared packed-cache decoder.

If the GPU scheduler lands first, treat this work as one scheduler-resource ABI
upgrade and update its CPU oracle, indirect queue, fingerprints, and fallback in
the same phase. Do not create parallel CPU and GPU cache layouts.

Every topology change requires a new Simple-DDGI async resource-usage and
timeline/barrier audit. Trace, relocate/classify, transport, blend, canonical
publish, sampled publish, and commit remain one serialized transaction until
that audit is recertified.

## 7. Phase 0 - Lock baselines and collect eligibility evidence

### Tasks

1. Record current commit/worktree identity, shader fingerprint, material/cook
   fingerprint, driver/device, quality tier, exact accepted volume table, random
   seed, source generation, warm-up frames, and capture frames.
2. Run two identical legacy-layout captures for each required scenario to
   measure deterministic repeatability before defining candidate tolerances.
3. Extend detailed diagnostics with bounded/sampled counters for:
   - maximum and histogram of source radiance components before packing;
   - non-finite and would-saturate source radiance;
   - source distance maximum/P99 by authored purpose and ring cascade;
   - predicted FP16 distance ULP and decoded hit-position displacement;
   - forward versus solver atlas samples by volume/cascade;
   - image-eligible interior samples, seam fallbacks, unmirrored fallbacks, and
     image fetches;
   - source-cache format and bytes by volume;
   - canonical and mirrored probe/layer counts.
4. Add a development shadow comparison that reconstructs directions while the
   stored direction remains authoritative. Sample a bounded subset so detailed
   diagnostics do not distort production timing.
5. Capture at minimum:
   - Sponza low and high bookmarks plus vertical traversal;
   - Cornell colored-bounce room;
   - Simple-DDGI furnace/constant-radiance reference;
   - thin-wall adjacent rooms;
   - long corridor occlusion;
   - verticality rings;
   - camera scroll and teleport;
   - dynamic light, dynamic object, emissive, and atmosphere/source-generation
     invalidation;
   - outdoor/sky-heavy far-ring coverage.
6. Preserve beauty, final indirect, sampled irradiance, visibility, source-cache
   radiance, support/ownership/fallback, cascade contributor, probe state, and
   update-reason outputs.
7. Add explicit screen/world ROIs for:
   - the dark side of each thin wall;
   - the lit control side;
   - near/mid and mid/far transition bands;
   - high-albedo multi-bounce surfaces;
   - dark low-radiance surfaces;
   - Sponza curtain-adjacent receivers.

### Exit gate

- Baseline repeats have matching provenance and a documented numerical noise
  floor.
- Every accepted volume has range and receiver-use evidence.
- The proposed receiver mirror set covers at least 95 percent of forward
  receiver interior image opportunities, or the policy is revised before code
  removal/allocation work begins.
- No existing source value is non-finite; any legacy saturation is understood
  and fixed before FP16 qualification.

## 8. Phase 1 - Make storage layout an authoritative compiled contract

### Tasks

1. Introduce strongly typed layout contracts, for example:
   - `SimpleDdgiStorageAbiVersion`;
   - `SimpleDdgiTransportCacheFormat`;
   - `SimpleDdgiTransportCacheRegion`;
   - `SimpleDdgiSampledAtlasCoverageMode`;
   - `SimpleDdgiSampledAtlasRange`;
   - `SimpleDdgiStorageLayout`.
2. Extend `SimpleDdgiLayoutVolumeRequest` with explicit grid dimensions,
   maximum trace distance, mirror class/priority, and stable volume identity.
   Do not infer new behavior from `SourceOrdinal >= 10000` outside the existing
   compatibility decoder.
3. Split layout admission into:
   - canonical/cache/state/work-buffer admission;
   - optional mirror admission from remaining bytes.
4. Make `SimpleDdgiMemoryPlan` report:
   - visibility texel stride and bytes;
   - source bytes for Legacy-36, Compact-28, and Compact-24 regions;
   - direction-free scratch stride and bytes;
   - requested/mirrored probe counts and rounded mirror capacity;
   - irradiance and visibility mirror bytes separately;
   - padding/alignment bytes;
   - total live bytes.
5. Append `CacheLayout` to `GPUSimpleDdgiVolume` and update C#/GLSL stride
   constants, upload size, source mirrors, and layout tests.
6. Add the storage layout, visibility format, mirror coverage, distance packing,
   direction codebook version, and ray-result stride to the layout fingerprint.
7. On any fingerprint change:
   - wait/retire resources using the existing safe capacity-transition policy;
   - allocate the complete new set;
   - clear cache/atlas payloads;
   - advance allocation/volume/source generations;
   - mark every affected probe fresh/source-invalid/visibility-invalid;
   - publish descriptors and metadata only for the new generation.
8. Preserve compatibility report fields, but add explicit packed fields instead
   of deriving canonical bytes by subtracting sampled bytes.

### Tests

- Checked byte arithmetic at zero, one probe, growth-quantum boundaries, group
  boundaries, tier caps, maximum rays, and maximum volume count.
- Mixed Compact-24/Compact-28 region offsets are aligned, non-overlapping, and
  exactly cover the source buffer.
- Optional mirror admission cannot change canonical accepted source ordinals.
- A mirror range is either complete or absent.
- C# `Marshal.SizeOf`, GLSL word strides, and memory-plan constants agree.
- Fingerprint changes invalidate incompatible live data exactly once.

### Exit gate

The legacy representation still renders and reports exactly the pre-phase byte
plan while all allocation/address decisions come from the new compiler.

## 9. Phase 2 - Convert visibility to RG16F safely

### Tasks

1. Add and mirror `VISIBILITY_VALID` in CPU/GPU state flags.
2. Audit every state mutation in
   [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs),
   `ddgi_simple_relocate_classify.comp`, `ddgi_simple_blend.comp`, scheduler
   commit code, toroidal preservation, clearing, readback, and recovery.
3. Change gather rejection to consume `vec2 moments` plus state validity.
4. Replace `previous.z` history checks in adaptive visibility blending with the
   matching probe-state validity/fresh-update contract.
5. Split typed atlas address/read/write/bilinear helpers and update every caller.
6. Change `VisibilityBytesPerProbe` from 2,048 to 1,024 and allocate/clear/copy
   the canonical visibility SSBO at the new stride.
7. Change the visibility sampled image to `R16G16Sfloat`, storage declaration to
   `rg16f`, byte estimator to four bytes/texel, and buffer-to-image copy offsets
   to the typed stride.
8. Query sampled, linear-filter, transfer-destination, and storage-image format
   support for both image formats. Provide a clear fallback reason if RG16F
   image publication is unavailable.
9. Keep the legacy RGBA visibility mode as a capture-only representation until
   parity passes; never reinterpret one allocation as the other.

### Tests

- Pack/unpack all finite half boundary values, zero moments, bootstrap moments,
  and maximum configured trace moments.
- Confirm RG `.xy` bits equal legacy RGBA `.xy` bits for the same writer input.
- Fresh, scroll-exposed, relocated, inactive, cleared, failed, and valid probes
  produce the same rejection reason masks as the legacy representation.
- A visibility-valid flag is never set before all 256 texels are written.
- Solver-only updates preserve visibility bytes and validity.
- SSBO bilinear and RG16F hardware filtering match for interior quads; seam
  samples retain the canonical mirrored-octahedral convention.
- Initial full sync and incremental image publication copy the exact RG byte
  ranges with correct Vulkan barriers/layouts.

### Exit gate

- Legacy RGBA and candidate RG produce bit-identical moment `.xy` values.
- No fresh/invalid probe becomes gatherable in stress, scroll, teleport, or
  forced-abort tests.
- Canonical visibility and full visibility mirror residency are each halved.
- Image and leak gates in Section 14 pass before RG becomes the only production
  visibility format.

## 10. Phase 3 - Introduce direction epochs and direction-free scratch

### Tasks

1. Implement the 32-entry deterministic source rotation codebook in shared GLSL
   and a C# mirror.
2. Add `SimpleDdgiUpdateDirectionRayIndex` and use it in all shader/CPU paths.
3. While retaining Legacy-36:
   - trace with the codebook;
   - store both oct direction and epoch;
   - reconstruct and compare in sampled detailed diagnostics;
   - verify every full probe refresh uses one coherent epoch.
4. Replace packed half hit kind with the exact metadata bitfield.
5. Change `GPUSimpleDdgiRayResult` and shader stride from eight to five words.
6. Update trace writes, transport fallback, transport output, relocate/classify,
   full and reduced blend, source-cache debug, and tests to reconstruct direction.
7. In blend, reconstruct/quantize once per active ray and retain it in shared
   memory with radiance and visibility metadata.
8. Add invalid-epoch, invalid-hit-kind, and mismatched-direction counters that
   fail closed in validation.
9. Retain the legacy 32-byte scratch mode only for controlled A/B until the new
   path passes. A mode switch recreates scratch and invalidates pending queues.

### Tests

- All 32 epochs, representative probes, ray counts 1/8/24/32/48/64/96/128/192/256,
  and maintenance subsets match C# mirrors.
- Reconstructed plus oct-round-tripped directions match stored Legacy-36
  directions within the established SNORM16 angular bound.
- Direction distribution remains unit length, finite, unbiased, and distinct
  across probes/epochs.
- Source refresh and later solver-only reuse reconstruct the same direction
  after hundreds of frames and across frame-index wrap tests.
- Relocation chooses the same nearest-backface direction and classification.
- Scratch address math works for non-16-byte 20-byte records and maximum queue
  capacity without overlapping entries.

### Exit gate

- No direction/epoch mismatch or non-finite reconstruction occurs.
- Image/counter parity passes with the codebook still stored, then again with
  direction-free scratch.
- Ray scratch drops from 32 to 20 bytes/result.
- GI GPU P95 does not regress beyond the repeatability band; if blend becomes
  ALU-bound, evaluate the documented 24-byte packed-direction fallback.

## 11. Phase 4 - Pack the persistent source cache

### Tasks

1. Implement Legacy-36, Compact-28, and Compact-24 read/write helpers behind the
   per-volume format metadata.
2. Sanitize inputs before packing. Invalid values do not become zero-valued
   valid light; they invalidate the entry/probe and increment diagnostics.
3. Pack source radiance through `packHalf2x16` and decode with nonnegative,
   finite validation.
4. Apply the conservative FP16 distance eligibility calculation during layout
   compilation. Store the selected format in the layout report and volume flags.
5. Pack/unpack distance only in Compact-24. Compact-28 preserves its FP32 bit
   pattern.
6. Remove the stored direction and reconstruct it from the entry epoch.
7. Update cache capacity/clear/invalidation/debug and any planned audit/solver
   readers to use region base/stride/format.
8. Add sampled validation comparing FP32 source inputs with decoded values:
   component absolute/relative error, luminance error, distance error, and world
   hit-position displacement by volume class.
9. Ensure source-cache ABI changes are included in atmosphere cohort capacity,
   source completion, scheduler fingerprints, and convergence restart rules.
10. Keep accepted volume/probe/ray schedules locked for packing parity runs so
    memory savings cannot masquerade as a lighting difference.

### Tests

- Boundary vectors: zero, subnormal, minimum normal, exponent boundaries,
  maximum finite half, negative/non-finite rejection, and bright single-channel
  HDR colors.
- Distance values immediately below/at/above every relevant half exponent,
  maximum per-volume trace distance, and thin-wall hit distances.
- Exact bit layout and flags for all three formats.
- Mixed-format buffer regions survive source refresh, solver reuse, invalidation,
  toroidal scroll, ray-count change, and topology rebuild.
- Constant-radiance and Lambertian CPU/GPU oracles preserve energy within the
  declared quantization bound.
- Multi-generation Cornell/white-enclosure convergence does not accumulate a
  biased dark or bright error.

### Exit gate

- Zero non-finite/saturation/overflow/address errors.
- Every FP16-distance volume passes the static ULP bound and hardware capture
  displacement metrics.
- Compact-28 reports 28 bytes/cache ray and Compact-24 reports 24 exactly.
- Thin-wall, ring seam, image difference, energy, and convergence gates pass per
  format and in a mixed-format scene.

## 12. Phase 5 - Implement compact receiver-relevant mirroring

### Tasks

1. Add the pure mirror-range compiler and coverage modes.
2. Preserve the existing canonical accepted layout, then select whole mirror
   volumes using the policy and remaining budget.
3. Allocate image layer capacity from compact mirrored probe count, including
   the existing 256-layer growth quantum and device array-layer/group limits.
4. Refactor `SimpleDdgiSampledAtlas` to create irradiance RGBA16F and visibility
   RG16F images with typed byte calculations.
5. Replace `CopyAll` with `CopyRanges`. For each declared range, copy the
   canonical first probe to the compact first image layer for both typed
   payloads.
6. Update sampled publication to:
   - read the update's volume;
   - reject generation mismatch;
   - return immediately for an unmirrored volume;
   - map physical local probe to compact layer;
   - write typed irradiance and visibility payloads.
7. Pass the selected `SimpleDdgiVolume` to typed sampling helpers so they can
   resolve compact layer or canonical fallback.
8. Validate compact layer, group, and descriptor bounds before nonuniform image
   access. Invalid mapping falls back and records validation evidence; it never
   aliases group zero as data.
9. On mirror layout change:
   - create/clear the new images;
   - upload new volume mapping metadata;
   - copy every selected canonical range;
   - transition all images to shader-read;
   - publish descriptors/mapping generation together;
   - retire old images only after their last submitted use.
10. Split diagnostics into requested, eligible, admitted, provisioned, and
    actually sampled mirror probes/bytes.
11. Preserve `FullCanonical` for qualification and emergency comparison, but set
    `ReceiverRelevant` as the production candidate only after the 95-percent hit
    gate and GPU timing gate pass.

### Tests

- Compact mapping with holes in canonical probe indices and multiple image
  groups.
- Group/layer boundaries, 256-layer capacity rounding, device layer limits, and
  descriptor limits.
- Authored-only, rings-only, near-only, near+mid, rejected far ring, and no
  eligible mirror layouts.
- Canonical admission/source ordinals are identical with mirror disabled,
  partial, full, budget-exhausted, and unsupported.
- Full range copy and sparse incremental publish target the same layer.
- Unmirrored interior samples and all seam samples return the canonical result.
- Scroll/recenter preserves physical layer ownership; new slabs remain invalid
  until matching publication.
- A layout remap cannot show stale content from the prior compact layer owner.

### Exit gate

- Actual image bytes equal the mirror compiler's typed, rounded estimate.
- No canonical volume is rejected to retain the optional mirror.
- At least 95 percent of measured forward receiver interior opportunities use
  the image path in target High/Ultra scenes.
- Far/transition fallback does not create a visible ring boundary or bandwidth
  regression.
- Forward gather P95 or measured atlas-read bandwidth improves; otherwise keep
  the mirror disabled for that tier/device class.

## 13. Phase 6 - Diagnostics, settings, documentation, and rollout

### Settings and compatibility

Retain `SimpleDdgiSampledAtlasEnabled` as the feature switch. Add explicit,
serialized settings for coverage and storage qualification rather than
overloading the Boolean:

```text
SimpleDdgiSampledAtlasCoverageMode
SimpleDdgiStoragePackingMode       // Legacy, Validate, Packed
```

Do not expose low-level per-volume stride or FP16 eligibility as artist knobs.
Those are compiler decisions. Command-line/capture overrides may force legacy,
full mirror, or individual packed phases for controlled evidence.

### Diagnostics

Plumb through [`SceneRenderingData.cs`](../Njulf.Rendering/Data/SceneRenderingData.cs),
[`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs),
[`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs), and
[`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs):

- storage ABI/version/mode;
- visibility canonical format/bytes;
- source cache bytes and ray counts by format;
- FP16-distance eligible/ineligible volume/probe counts and reasons;
- ray scratch stride/bytes;
- mirror coverage mode, eligible/admitted/provisioned probes, per-payload bytes,
  and excluded volume identities;
- image hit, seam fallback, unmirrored fallback, invalid-map fallback counts;
- pack saturation/non-finite/error maxima;
- direction epoch mismatch/angular error;
- allocation/mapping generation and fallback reason.

Update [`RenderBudgetEvaluator.cs`](../Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs)
to sum explicit resources. Update
[`RendererSettingsReference.md`](../RendererSettingsReference.md),
[`ddgi-diagnostics.md`](../docs/rendering/ddgi-diagnostics.md), and
[`ddgi-runtime-validation.md`](../docs/rendering/ddgi-runtime-validation.md)
with the modes, fallbacks, and gates.

### Rollout stages

1. `Legacy`: new compiler/telemetry, old bytes and full mirror.
2. `VisibilityPacked`: RG16F visibility, legacy source cache/direction.
3. `DirectionValidate`: codebook plus stored-direction shadow comparison.
4. `DirectionPacked`: direction-free scratch, legacy source precision.
5. `CachePacked`: Compact-28/24 with full mirror.
6. `PartialMirror`: packed cache plus receiver-relevant image ranges.
7. Preset candidate: Low/Medium packed canonical SSBO; High/Ultra packed plus
   qualified partial mirror.
8. Remove validation-only legacy shader variants only after one full release
   window and archived rollback evidence.

No stage advances because its memory number looks good. Each stage needs its own
identity-locked evidence bundle and the gates below.

## 14. Image-difference, leak, and temporal qualification

### 14.1 Comparison method

For each phase, run baseline and candidate from a cold DDGI start with:

- identical accepted volume list and probe counts;
- identical camera/light/material/environment inputs;
- identical source-direction codebook sequence and fixed random seed;
- identical warm-up and capture frames;
- production debug views excluded from timing samples;
- complete manifest/provenance validation.

Run baseline twice and candidate twice. The baseline repeatability floor must be
below every hard engineering ceiling before candidate qualification starts. Use
the repeatability result to distinguish noise from a systematic shift, but never
raise a hard ceiling to accommodate either baseline or candidate variance.

Use scene-linear global RMSE/relative RMSE/max component error, HDR-FLIP P95,
and ROI metrics. Packing qualification must include at least:

- final indirect;
- sampled irradiance;
- visibility;
- source-cache radiance;
- beauty/final diffuse;
- ownership/fallback and cascade contributor masks.

### 14.2 Hard quality ceilings

Unless baseline repeatability is already worse and is fixed first:

- global final-indirect relative RMSE: at most 1 percent;
- global beauty HDR-FLIP P95: at most 0.02;
- named ROI mean-luminance shift: at most 2 percent;
- named ROI P95 luminance shift: at most 3 percent;
- no new non-finite, negative-radiance, invalid-probe, or stale-mirror pixel;
- visibility-packed-only phase: moment `.xy` parity is bit exact before
  filtering and image error remains within existing sampled/canonical parity;
- direction-reconstruction-only phase: no systematic energy shift beyond the
  baseline repeatability band.

If the existing approved-reference infrastructure requires stricter gates, the
stricter value wins.

### 14.3 Leak gates

For `GiThinWallLeakTest`, `GiLongCorridorOcclusion`, Sponza curtain/walls, and
ring transition ROIs:

- the dark-side/lit-side leak ratio may not increase by more than 0.5 percentage
  points absolute or 5 percent relative, whichever is stricter;
- dark-side P99 luminance may not exceed its approved bound;
- no connected bright region may appear across a wall, floor, curtain, or ring
  boundary where the legacy candidate is dark;
- visibility moment mean/variance, transport visibility, ownership, and
  fallback counters remain within the baseline repeatability band;
- camera scroll, recenter, and teleport sequences contain no one-frame stale
  mirror flash;
- near/mid and mid/far traversal contains no step above the approved transition
  ROI threshold.

Distance packing is rejected for the failing volume class even if aggregate
images pass but a leak-specific ROI fails.

### 14.4 Energy and convergence gates

- Constant-radiance and furnace results retain their analytic energy.
- Cornell and high-albedo enclosure fixed points remain within the declared
  quantization error, with no monotonic bias across solver generations.
- Source-cache hit/miss/repair, source ray, solve ray, published probe, and
  convergence transaction counts match the locked schedule.
- Packed storage does not cause extra source invalidations or extend source
  cohort/convergence completion beyond repeatability.

## 15. Performance and memory gates

Use the existing benchmark/performance snapshot path and an Nsight capture on
the primary RTX 3060 Laptop target. Record at least three identity-locked runs
per mode.

Required metrics:

- canonical visibility bytes;
- source cache bytes by format;
- ray scratch bytes;
- sampled irradiance/visibility image bytes;
- total Simple-DDGI live and retired bytes;
- forward pass GPU P50/P95;
- total GI GPU P50/P95;
- trace, relocate/classify, transport, blend, and publish timings;
- sampled publication/full-sync time;
- image-hit/seam/unmirrored sample ratios;
- L2/DRAM storage-read traffic and texture-read traffic where the profiler
  exposes comparable counters;
- CPU layout/upload stable-path P95.

Acceptance:

- allocated bytes equal the compiler estimate; no unexplained allocator growth;
- RG16F halves both canonical and mirrored visibility payload bytes;
- Compact-28/24 and 20-byte scratch meet their exact stride formulas;
- stable frames perform no mirror allocation, remap, full copy, descriptor
  rewrite, device idle, or per-volume CPU enumeration beyond the existing
  bounded layout work;
- receiver-relevant mirroring reduces total mirror bytes relative to the same
  accepted layout in `FullCanonical` mode;
- forward and total GI P95 do not regress outside run-to-run noise;
- the candidate demonstrates either a measurable forward/gather improvement or
  a profiler-confirmed atlas read-traffic reduction. If neither occurs, do not
  enable the sampled mirror by default merely because it is smaller;
- no new runtime stall or retired-resource backlog appears during resize,
  quality switch, reload, scroll, or mirror fallback.

## 16. Test inventory

### Existing tests to extend

- [`SimpleDdgiLayoutCompilerTests.cs`](../Njulf.Tests/SimpleDdgiLayoutCompilerTests.cs)
- [`SimpleDdgiShaderMirrorTests.cs`](../Njulf.Tests/SimpleDdgiShaderMirrorTests.cs)
- [`GPUStructLayoutTests.cs`](../Njulf.Tests/GPUStructLayoutTests.cs)
- [`ShaderBuildTests.cs`](../Njulf.Tests/ShaderBuildTests.cs)
- [`FarFieldClipmapOracleTests.cs`](../Njulf.Tests/FarFieldClipmapOracleTests.cs)
- [`CameraRelativeDdgiClipmapControllerTests.cs`](../Njulf.Tests/CameraRelativeDdgiClipmapControllerTests.cs)
- [`SimpleDdgiBounceConvergenceTests.cs`](../Njulf.Tests/SimpleDdgiBounceConvergenceTests.cs)
- [`RenderBudgetEvaluatorTests.cs`](../Njulf.Tests/RenderBudgetEvaluatorTests.cs)
- [`PerformanceSnapshotWriterTests.cs`](../Njulf.Tests/PerformanceSnapshotWriterTests.cs)
- [`SampleSponzaGiCaptureHarnessTests.cs`](../Njulf.Tests/SampleSponzaGiCaptureHarnessTests.cs)
- [`SampleGlobalIlluminationValidationSettingsTests.cs`](../Njulf.Tests/SampleGlobalIlluminationValidationSettingsTests.cs)
- [`SampleDdgiBenchmarkSuiteTests.cs`](../Njulf.Tests/SampleDdgiBenchmarkSuiteTests.cs)

### New focused test fixtures

Add focused fixtures rather than expanding source-string pins for all behavior:

- `SimpleDdgiStorageLayoutTests.cs` - region offsets, mixed strides, exact bytes,
  fingerprint changes, two-stage admission;
- `SimpleDdgiVisibilityPackingTests.cs` - typed RG addressing and state-validity
  model;
- `SimpleDdgiDirectionEpochTests.cs` - codebook, epoch persistence, subset mapping,
  angular oracle;
- `SimpleDdgiTransportCachePackingTests.cs` - Legacy/28/24 pack/decode/range/error
  oracle;
- `SimpleDdgiSampledAtlasLayoutTests.cs` - compact ranges, priorities, budget,
  remaps, layer/group boundaries;
- hardware integration coverage in the existing Vulkan smoke/capture harness for
  format features, image copy/publication, transitions, and descriptor bounds.

Source-text assertions may protect a critical ABI token, but behavioral C#
mirrors and compiled shaders are the primary tests.

## 17. File-level implementation map

### Core layout and resource ownership

- [`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
- proposed `Njulf.Rendering/Resources/SimpleDdgiStorageLayoutCompiler.cs`
- proposed `Njulf.Rendering/Resources/SimpleDdgiSampledAtlasLayout.cs`
- [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [`SimpleDdgiSampledAtlas.cs`](../Njulf.Rendering/Resources/SimpleDdgiSampledAtlas.cs)
- [`GPUStructs.cs`](../Njulf.Rendering/Data/GPUStructs.cs)
- [`BindlessIndexTable.cs`](../Njulf.Rendering/Descriptors/BindlessIndexTable.cs)

### Shaders and passes

- [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl)
- [`ddgi_simple_trace.comp`](../Njulf.Shaders/ddgi_simple_trace.comp)
- [`ddgi_simple_relocate_classify.comp`](../Njulf.Shaders/ddgi_simple_relocate_classify.comp)
- [`ddgi_simple_transport.comp`](../Njulf.Shaders/ddgi_simple_transport.comp)
- [`ddgi_simple_blend.comp`](../Njulf.Shaders/ddgi_simple_blend.comp)
- [`ddgi_simple_publish.comp`](../Njulf.Shaders/ddgi_simple_publish.comp)
- [`ddgi_simple_publish_sampled.comp`](../Njulf.Shaders/ddgi_simple_publish_sampled.comp)
- [`forward.frag`](../Njulf.Shaders/forward.frag)
- [`SimpleDdgiPasses.cs`](../Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs)
- [`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs)
- [`AsyncComputePassCatalog.cs`](../Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs)

### Settings, diagnostics, and evidence

- [`RenderSettings.cs`](../Njulf.Rendering/Data/RenderSettings.cs)
- [`SceneRenderingData.cs`](../Njulf.Rendering/Data/SceneRenderingData.cs)
- [`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs)
- [`RenderBudgetEvaluator.cs`](../Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs)
- [`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs)
- [`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs)
- [`SampleSponzaGiCaptureHarness.cs`](../NjulfHelloGame/SampleSponzaGiCaptureHarness.cs)
- [`SampleGlobalIlluminationValidation.cs`](../NjulfHelloGame/SampleGlobalIlluminationValidation.cs)
- [`SampleDdgiProductionGate.cs`](../NjulfHelloGame/SampleDdgiProductionGate.cs)
- [`SampleDiagnosticsReporter.cs`](../NjulfHelloGame/SampleDiagnosticsReporter.cs)
- [`RendererSettingsReference.md`](../RendererSettingsReference.md)
- [`ddgi-diagnostics.md`](../docs/rendering/ddgi-diagnostics.md)
- [`ddgi-runtime-validation.md`](../docs/rendering/ddgi-runtime-validation.md)

## 18. Recommended implementation slices

Keep each slice reviewable and independently revertible:

1. Baseline/range/mirror-use/direction-shadow telemetry only.
2. Storage layout compiler and volume cache-layout metadata, legacy bytes only.
3. Visibility-valid state bit and typed atlas helpers, still RGBA visibility.
4. Canonical RG16F visibility.
5. RG16F sampled visibility image and typed copy/publication.
6. Direction codebook plus stored-direction shadow validation.
7. Packed ray metadata and 20-byte direction-free scratch.
8. Compact-28 source cache.
9. Per-volume Compact-24 eligibility and mixed regions.
10. Compact mirror range compiler and two-stage admission.
11. Range-based full sync, incremental publish, and sampling fallback.
12. Diagnostics/schema/docs and full qualification bundle.
13. Preset rollout only after reviewed evidence.

Do not combine visibility validity, direction codebook, FP16 source packing, and
partial mirror mapping in the first rendering change. A combined failure would
be difficult to attribute and could pass aggregate images while hiding a local
leak.

## 19. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Dropping visibility `.z/.w` makes cleared texels look valid | Move validity to probe state first; barrier before setting; fail-closed lifecycle tests |
| FP16 distance shifts a bounce hit across a thin wall | Per-volume static ULP gate, Compact-28 fallback, hit-position telemetry, dedicated leak ROIs |
| FP16 radiance biases multi-generation bounce | FP32 transient oracle, pack/decode error metrics, furnace/Cornell/high-albedo convergence gates |
| Current frame rotation reconstructs the wrong cached ray | Persist five-bit source epoch in every entry and use a fixed codebook |
| Direction reconstruction adds too much blend ALU | Reconstruct once into shared memory; measure; retain documented 24-byte packed-direction fallback |
| Variable cache strides corrupt adjacent volume regions | One compiled region table, 16-byte alignment, checked CPU arithmetic, shared shader address helper |
| Partial mirror samples global index as compact layer | Per-volume base+1 mapping; bounds checks; canonical fallback; mapping oracle |
| Mirror remap exposes stale data | New allocation/mapping generation, full range copy before descriptor publication, deferred retirement |
| Optional mirror rejects canonical coverage | Two-stage admission; canonical layout first, mirror only from remaining budget |
| Far ring exclusion causes a transition seam | Measured receiver hit ratio, far-ring fallback counters, vertical traversal and transition ROI gates |
| RG16F image unsupported on a device | Independent format query and canonical SSBO fallback with explicit reason |
| Saved memory silently admits more probes and invalidates A/B | Lock accepted layout for parity; retain savings as headroom until separate capacity approval |
| Async queues observe old descriptors/layout metadata | Treat resource/mapping generation as one transaction and redo queue/timeline audit |
| Snapshot tools misreport totals after partial mirror | Emit explicit typed resources and counts; stop deriving canonical bytes by subtraction |

## 20. Definition of done

The project is complete only when all of the following are true:

- visibility is canonically RG16F with state-owned validity and exact byte
  accounting;
- source cache supports mixed, compiler-selected Compact-28/24 regions and no
  production entry stores a ray direction;
- transient ray scratch is direction-free at 20 bytes, or the measured 24-byte
  packed-direction fallback is explicitly selected and documented;
- source direction survives cache reuse through an exact stored epoch and
  mirrored C#/GLSL codebook;
- sampled images use RGBA16F irradiance, RG16F visibility, and compact complete
  receiver-volume ranges;
- unmirrored/seam/capability/budget cases always take a correct canonical
  fallback;
- optional mirror admission cannot reduce canonical coverage;
- layout, runtime allocation, diagnostics, and snapshot byte totals agree;
- unit, shader compile, GPU integration, lifecycle, scroll/teleport, image,
  HDR-FLIP, ROI, leak, energy, convergence, memory, and performance gates pass;
- target presets are enabled only for device/tier combinations with reviewed
  evidence and a safe fallback;
- no new validation error, non-finite value, cache repair storm, stale mirror
  flash, ring seam, or runtime device-idle stall remains.
