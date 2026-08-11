# Remaining Advanced Global Illumination Production Implementation Plan

- Status: Production source/runtime implementation complete as of 2026-08-11;
  automatic promotion remains fail-closed until the prerequisite and per-feature
  device qualification evidence in this plan is supplied
- Date: 2026-08-09
- Target: Simple-DDGI ray-query Transport V2
- Scope: roadmap items B1, C1, C3, C4, and C5
- Explicit exclusion: C2, the ray-tracing-pipeline/SER experiment, is not part of this plan
- Primary target for first measurements: RTX 3060 Laptop GPU
- Required wider qualification: at least one Ada-or-newer NVIDIA GPU and one non-NVIDIA/fallback device
- Baseline: the working tree after `ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md` reaches its definition of done
- Default-policy amendment (2026-08-11): new settings request B1
  `ExactCompacted`, C1 `ExtFourStateExperiment`, C3
  `PerProbeHistogramExperiment`, C4 `WorldCacheExperiment`, and C5
  `HiZHalfResolutionExperiment`; these defaults express intent only and do not
  bypass any capability, prerequisite, content, memory, resource-completeness,
  fallback, or `AutoQualified` evidence gate
- Implementation record: [`advanced-gi-experiment-status.md`](../../docs/rendering/advanced-gi-experiment-status.md)
  distinguishes implemented runtime paths from evidence that must be captured on
  the named target-device matrix

## 1. Required outcome

Implement the remaining useful roadmap work without weakening the canonical
DDGI field, hiding cost in always-resident resources, or mistaking an
experimental admission check for a working renderer feature.

The finished work must provide:

1. exact, sampled receiver-to-probe contribution feedback that improves update
   order while retaining non-camera liveness quotas;
2. a quality-neutral four-state opacity-micromap path for eligible static alpha
   masks, using the available `VK_EXT_opacity_micromap` Silk.NET backend and the
   existing shader-confirmed candidate path as the fallback;
3. directional ray guiding whose sampling distribution, PDFs, estimator,
   direction identity, maintenance rays, cache reuse, and audits are all
   mathematically consistent;
4. a separate, opt-in caustic solution for authored hero specular/refractive
   paths, with photon flux rather than diffuse brightness defining its energy;
5. a bounded near-field screen-space residual only if measured reference error
   remains after qualified B3 refinement bricks; and
6. zero persistent allocation, zero descriptors, zero render-graph passes, and
   zero shader work for each optional feature while it is effectively disabled.

The implementation is not complete merely because settings, capability
queries, mathematical helpers, or diagnostics report that an experiment is
admissible. Every active mode must own real GPU resources, passes, shaders,
synchronization, evidence, and a tested fallback.

### 1.1 Non-negotiable invariants

- The published diffuse irradiance/visibility field and positive Transport V2
  operator remain canonical.
- B1 feedback is a one-frame-late priority signal. It can never be a visibility,
  residency, allocation, or liveness gate.
- C1 can remove shader candidate work only where four-state micromap
  classification is provably identical to the frozen DDGI alpha contract.
- C3 always retains non-zero uniform-sphere support and uses the exact PDF of
  the distribution that actually generated each transport direction.
- C4 caustic energy never enters the diffuse source cache, transport atlas,
  irradiance atlas, or refinement-brick publication path.
- C5 never samples final scene color containing DDGI and never feeds its output
  back into DDGI, reflection probes, or another indirect-light bounce.
- Disabling or rejecting an experiment restores the current path without a
  scene reload, stale descriptor, stale BLAS attachment, or partially published
  history.
- No device/vendor generation is assumed faster. Promotion is based on
  end-to-end GPU time and quality evidence for the exact promoted device class.

### 1.2 Explicitly excluded C2/SER work

Do not implement a ray-tracing-pipeline backend, shader binding table, hit-object
path, Slang shader island, or shader-execution-reordering pipeline under this
plan. The RTX 3060 can expose the invocation-reorder interface while reporting
no effective reorder hint, so extension presence is not evidence of useful SER.
The current inline ray-query backend remains the only transport backend in
scope.

The existing C2 capability/admission scaffold may remain inert for historical
diagnostics, but it must allocate nothing, compile no runtime pipeline, and must
not appear in the definition of done. Removing that scaffold is separate cleanup
and is not required here.

## 2. Entry gate and frozen prerequisite contracts

Do not start production resource or shader integration until
[`ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md`](ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md)
is complete and its final ABI is merged. Planning, CPU oracles, asset-tool
prototypes, and reference captures may proceed earlier; code that depends on a
provisional ABI may not.

Freeze and record the following prerequisite values in an evidence manifest:

- GPU many-light tree layout, selection algorithm, and exact returned PDF;
- spatial emissive sampler and its triangle/texel PDF convention;
- 64-byte L2 radiance-SH ABI, descriptor indices, publication generation, and
  memory-plan ownership;
- current-pose acceleration-structure ordering and dynamic BLAS fallback;
- ray-instance/material ABI, alpha composition, UV transform, sampler, fixed
  DDGI mip/LOD policy, cutoff comparison, thin-transmission semantics, and
  foliage proxy ownership;
- stable stochastic identity fields and replay seed rules;
- source-cache factorization and cached re-lighting rules;
- content, transform, geometry, material, alpha, texture-residency, lighting,
  environment, direction-codebook, and shader-ABI revision taxonomy;
- transactional sparse-page/refinement publication and base-field fallback;
- authoritative `SimpleDdgiContentMemoryPlan` and layout compiler; and
- deterministic captures and brute-force/reference modes used by the
  prerequisite plan.

The gate also requires:

- A4 spatial emissive sampling and cached re-lighting for C3;
- B3 refinement bricks active and qualified before C5 can allocate anything;
- no outstanding alpha-conformance failure before C1 baking begins; and
- a path-traced/reference corpus with the feature-isolated scenes listed in
  Section 13.

If any frozen ABI changes later, increment the relevant ABI/revision, invalidate
only dependent cooked/runtime data, and rerun the affected gate. Never silently
reinterpret persisted bytes.

## 3. Current implementation boundary

This plan replaces or completes existing scaffolding; it does not assume those
scaffolds are production implementations.

### 3.1 B1 prototype

The current scheduler allocates two 16-byte contribution banks per active probe.
Receiver shaders aggregate a Q8 weight, a 24-bit hash/sketch of tile identity,
and consumer/role bits, and schedule classification reads the previous bank.
This proves double-buffering and priority-only consumption, but it is not the
roadmap contract:

- a hash cannot report exact screen-tile coverage;
- packed Q8 accumulation is not the exact interpolation/contribution mass;
- a global atomic on a hot per-probe word scales poorly with receiver traffic;
- requested virtual probe, resolved fallback probe, virtual page, and page
  generation are not all preserved; and
- producer sampling rates cannot be compared without inclusion-probability
  correction.

Treat this path as a reference and migration source. Do not retain its packed
layout as the production ABI merely to avoid a layout version bump.

### 3.2 C1 capability scaffold

[`VulkanContext.cs`](../Njulf.Rendering/Core/VulkanContext.cs) already queries
`VK_EXT_opacity_micromap` features and limits through Silk.NET 2.23.0, and
[`GiHardwareResearchExperiments.cs`](../Njulf.Rendering/Resources/GiHardwareResearchExperiments.cs)
contains eligibility/admission helpers. There is no cooked micromap payload,
micromap object manager, build/compaction pass, BLAS attachment, or real A/B
measurement yet.

Silk.NET 2.23.0 supplies the EXT bindings used by the current toolchain but does
not expose a KHR opacity-micromap binding. `VK_KHR_opacity_micromap` is not a
drop-in spelling change: the promoted API folds micromaps into acceleration
structures and changes ray-query shader requirements. The production design
therefore has a backend boundary, but this plan implements only the EXT backend.
Do not hand-author a partial KHR binding.

### 3.3 C3, C4, and C5 scaffolds

[`SimpleDdgiDirectionalGuidingExperiment.cs`](../Njulf.Rendering/Resources/SimpleDdgiDirectionalGuidingExperiment.cs)
contains correct basic mixture/PDF helpers but no learned distribution or GPU
sampling path.

[`GiNearFieldResearchExperiments.cs`](../Njulf.Rendering/Resources/GiNearFieldResearchExperiments.cs)
contains admission and memory arithmetic for C4/C5 but no photon trace/cache,
screen trace, temporal history, or composite passes. Its current C4 helper caps
caustic radiance relative to the receiver's diffuse baseline. That rule is
physically wrong for a bright caustic on a dark receiver and must be removed;
flux/path throughput owns C4 energy instead.

`GiRoadmapExperimentDiagnostics` currently describes intended admission. It must
be split into requested, supported, allocated, active, measured, qualified, and
fallback states so a boolean setting can never masquerade as completed work.

## 4. Fixed product and algorithm decisions

| Item | Production/research baseline | Not accepted as the baseline |
| --- | --- | --- |
| B1 capture | Exact compact records, sparse receiver sampling, subgroup/workgroup aggregation, GPU sort/unique/reduce, previous-frame per-probe summaries | hashed tile sketches; direct per-fragment global per-probe atomics |
| B1 policy | additive bounded priority term with hard minimum quotas for age, newly exposed content, off-screen safety, propagation, and captures | feedback as an eligibility or eviction gate |
| C1 bake | pinned NVIDIA OMM SDK CPU baker in the asset tool, four-state only, exact static alpha inputs | bespoke approximate resampling; two-state promotion; freezing dynamic alpha |
| C1 runtime | capability-gated `VK_EXT_opacity_micromap` backend with ordinary candidate-confirmation fallback | manual KHR binding; requiring OMM to run the renderer |
| C3 distribution | 8x8 equal-area spherical incident-energy leaves plus a hierarchical 4-way sampling pyramid, per physical probe, double-buffered | a single dominant direction; zero-support bins; treating ordinary octahedral texels as equal-area |
| C3 estimator | one-sample uniform/guided mixture with exact continuous PDF; fixed separate uniform maintenance subset | heuristic ray steering with ordinary equal-ray blend weights |
| C4 transport | bounded light-space photon/path tracing through an authored hero delta/specular/refractive event to the first diffuse receiver | adding refraction to ordinary diffuse DDGI; diffuse-relative radiance clamp |
| C4 storage | separate world-space hashed photon cache with unbiased bounded per-cell retention and a screen receiver resolve | writing caustics into DDGI probes or source records |
| C5 source | optional pre-DDGI direct-diffuse-plus-emissive buffer and short Hi-Z screen trace | final scene color, DDGI-lit color, SSR color, or post-tonemap color |
| C5 composition | confidence-gated, frequency-separated residual over DDGI/B3 with scene-linear temporal validation | a second full GI solution; screen-space result as occlusion authority |

The 8x8 C3 resolution and all initial capacities are starting candidates, not
magic constants. The layout compilers must expose exact calculated bytes and the
qualification sweep must compare 4x4, 8x8, and 16x16 distributions before 8x8
is frozen for a preset.

## 5. Shared architecture and contracts

### 5.1 Requested, admitted, and effective modes

Replace experimental booleans with versioned modes while retaining one-schema
read compatibility for existing settings:

```text
SimpleDdgiReceiverFeedbackMode:
    Off | LegacyPackedReference | ExactCompacted

DdgiOpacityMicromapMode:
    Off | ExtFourStateExperiment | AutoQualified

SimpleDdgiDirectionalGuidingMode:
    Off | CpuOracle | PerProbeHistogramExperiment | AutoQualified

GiCausticMode:
    Off | PhotonReference | WorldCacheExperiment | AutoQualified

SimpleDdgiNearFieldResidualMode:
    Off | Reference | HiZHalfResolutionExperiment | AutoQualified
```

Every mode has:

- `RequestedMode`: persisted/user-authored intent;
- `SupportedMode`: device/tool/content capability;
- `AdmittedMode`: memory and prerequisite decision before allocation;
- `EffectiveMode`: resource-complete mode for the current frame;
- `FallbackReason`: stable enum plus human-readable details; and
- `QualificationId`: device/driver/shader/content evidence hash required by an
  `AutoQualified` selection.

Do not mutate requested settings when a feature falls back. A renderer restart,
resize, content reload, or device loss must recompute admission transactionally.

Current preset policy is:

- every new preset requests B1 `ExactCompacted`, C1
  `ExtFourStateExperiment`, C3 `PerProbeHistogramExperiment`, C4
  `WorldCacheExperiment`, and C5 `HiZHalfResolutionExperiment`;
- requested mode is persisted intent and never substitutes for supported,
  admitted, effective, or qualified state;
- existing saved settings retain their authored modes, including `Off`;
- each feature still fails closed independently, and combined release
  qualification still requires every isolated gate plus the integrated
  memory/time gate; and
- no C feature is part of the DDGI-only correctness gate.

### 5.2 Central memory planning

Extend the existing content-dependent memory plan rather than allocating from
individual passes. Add independently rejectable categories:

```text
ReceiverFeedbackRecordBanks
ReceiverFeedbackSortScratch
ReceiverFeedbackProbeSummaries
OpacityMicromapResidentData
OpacityMicromapBuildScratch
OpacityMicromapCompactionHeadroom
DirectionalGuidingHistoryBanks
DirectionalGuidingBuildScratch
CausticPhotonRecords
CausticCellTableAndSortScratch
CausticHistory
NearFieldTraceTargets
NearFieldHistoryAndMoments
NearFieldFilterScratch
```

For each category report requested, required, admitted, allocated, peak-live,
retired-but-live, and fallback bytes. Scratch intervals must be represented in
the render graph so non-overlapping B1/C1/C3/C4/C5 work can alias through the
existing allocator where legal. Persistent data must never alias.

Compile sizes with checked 64-bit arithmetic from active physical probes,
scheduled probe capacity, viewport tile count, OMM build-size queries, photon
capacity, and scaled near-field dimensions. Reject before allocation if any
value overflows, exceeds Vulkan limits, exceeds the independent feature budget,
or pushes the total renderer budget beyond its headroom.

Disabled or rejected modes return zero for every category associated with that
feature. Unit tests must assert this; it is not left to lazy allocation behavior.

### 5.3 Revision and publication rules

Add only the revisions that identify new data:

- `ReceiverFeedbackLayoutRevision` and monotonically increasing feedback bank
  generation;
- `OpacityMicromapCookAbi`, `OpacityMicromapContentRevision`, and backend payload
  kind/version;
- `GuidingDistributionAbi`, per-probe distribution generation, and direction
  proposal epoch;
- `CausticTransportAbi`, hero-material revision, light/emissive distribution
  revision, caster transform/geometry revision, and cache publication epoch;
- `NearFieldResidualAbi`, trace-source layout revision, viewport/Hi-Z revision,
  and temporal history generation.

Use existing scene/material/texture revisions rather than duplicating them.
Every persistent record carries enough generation identity to detect slot reuse.
On mismatch, readers use the canonical fallback and increment a diagnostic;
they never reinterpret or partially accept the record.

B1, C3, and C4 publish double-buffered generations. A write generation is
cleared/built completely, validated by its GPU header, and then made readable by
flipping a small generation header. C5 history uses ordinary previous-frame
ownership but clears on cuts, resizes, effective-mode changes, exposure-domain
changes, or ABI revisions.

### 5.4 Stable stochastic identity

All new sampling derives from the prerequisite stable ID contract. Hash inputs
must include semantic identity, not transient array position:

```text
B1: producer class, stable receiver/tile/froxel/capture ID, frame sample epoch
C3: stable probe ID, direction proposal epoch, slot, proposal branch, intra-bin sample
C4: light/emitter stable ID, hero-caster stable ID, photon epoch, photon slot, path vertex
C5: stable pixel/tile ID, view ID, temporal sample epoch, ray slot
```

Capture/replay records the feature modes, epochs, layout revisions, scene
revisions, and seeds. GPU compaction order may differ without changing sampling
identity or the converged estimator.

### 5.5 Common observability

Every feature exposes CPU and GPU P50/P95/P99/maximum time, exact bytes,
allocation state, pass count, fallback count/reason, non-finite counts, overflow
counts, and generation mismatch counts. Sampling features also expose effective
sample count, PDF min/max, inverse-PDF quantiles, and rejected/invalid samples.

Add feature-isolation capture flags and debug views without making debug
resources resident in normal builds. `PerformanceSnapshotWriter` must serialize
stable schema-versioned fields; missing old fields deserialize to disabled/zero.

## 6. B1 — Exact receiver contribution feedback

### 6.1 Semantics

For each sampled receiver gather, record the probes that actually supplied the
published lighting, not merely the ideal corners before residency/fallback. Each
record identifies:

- requested virtual probe ID;
- resolved contributing virtual probe ID;
- resolved virtual page ID and page publication generation;
- exact screen/capture/froxel tile ID in a producer-specific namespace;
- actual normalized interpolation weight after validity, backface, visibility,
  cascade/ring, and fallback decisions;
- inverse inclusion probability for the receiver sampling policy;
- consumer class and fallback ownership role; and
- feedback layout/bank generation.

“Exact” means the IDs and weight used by that gather invocation are carried into
feedback without a hash or a different second gather. FP32 is canonical for
weight accumulation. A compact FP16 weight candidate can be evaluated only
against the FP32 reference and cannot be the initial implementation.

If a requested fine probe falls back to a base probe, preserve both IDs. Charge
the resolved probe with visible contribution and increment demand/fallback
pressure for the requested probe/page. This separates “refresh what lit the
pixel” from “finish the missing higher-quality owner.”

### 6.2 Producer sampling and normalization

Use deterministic Bernoulli/stratified sampling with known inclusion probability
per producer. Aggregate contributions using a Horvitz-Thompson-style correction:

```text
reportedMass = physicalReceiverContribution * interpolationWeight / inclusionProbability
```

`physicalReceiverContribution` is consumer-specific and scene-linear:

| Producer | Contribution measure |
| --- | --- |
| Opaque forward | shaded sample area times indirect-diffuse ownership weight |
| Alpha mask/foliage | surviving coverage times projected sample area |
| Transparent/weighted OIT | transmittance/composite weight times projected area; never raw fragment count |
| Particles | composited alpha/coverage times projected particle footprint, bounded per emitter |
| Fog | froxel projected solid angle/volume weight times DDGI indirect-scattering coefficient |
| Reflection capture | cubemap texel solid angle times rough-DDGI ownership weight |
| Refinement/base fallback | actual blend ownership split; weights across owners still sum to the gather's DDGI weight |

Do not compare uncorrected opaque samples taken every frame with fog or particle
samples taken less frequently. Clamp only the *scheduler priority transform*,
not the accumulated physical mass. Record unclamped and transformed statistics.

The default candidate policy is one representative opaque receiver per 8x8
screen tile and a rotating subset of tiles. Transparent/fog/particle producers
use their natural workgroup/subgroup and lower bounded rates. The layout compiler
chooses the sampling fraction so worst-case records fit the admitted bank. A
capture mode can force every tile for validation.

### 6.3 Record and summary ABIs

Start with a naturally aligned 32-byte record:

```text
struct GPUSimpleDdgiReceiverContributionRecordV2       // 32 bytes
{
    uint RequestedVirtualProbeId;
    uint ResolvedVirtualProbeId;
    uint ResolvedVirtualPageId;
    uint ExactTileId;
    float InterpolationWeight;
    float InverseInclusionProbability;
    uint PackedConsumerFallbackAndPageGeneration;
    uint FeedbackGeneration;
};
```

If page-generation bits do not fit the packed field after the prerequisite ABI
freezes, expand to 40/48 bytes. Do not truncate a generation to preserve 32
bytes. The C# and GLSL mirrors must assert size, offset, alignment, endian, flag,
and sentinel equality.

After sorting/uniquing, publish a per-resolved-probe summary, initially 32 bytes
per bank:

```text
struct GPUSimpleDdgiReceiverContributionSummaryV2     // 32 bytes
{
    float EstimatedContributionMass;
    float MaximumSingleReceiverWeight;
    uint ExactUniqueTileCount;
    uint SampledReceiverCount;
    uint ConsumerMask;
    uint PackedFallbackCounts;
    uint FeedbackGeneration;
    uint StatusFlags;
};
```

Maintain two summary banks: frame N writes one while scheduling consumes the
validated frame N-1 bank. Record append banks also need two generations because
late graphics/compute producers can overlap the next frame's schedule. Scratch
does not need two banks if graph lifetimes prove no overlap.

Capacity is calculated, not hard-coded:

```text
sampledScreenTiles = ceil(tileCount * screenSamplingProbability)
screenRecords      = sampledScreenTiles * maximumUniqueGatherOwnersPerTile
otherRecords       = sum(producerReservedRecordQuotas)
recordCapacity     = alignUp(screenRecords + otherRecords + safetyMargin, workgroupSize)
bankBytes          = recordCapacity * recordStride
summaryBytes       = activeProbeCapacity * summaryStride * 2
```

The planner reports the assumptions and rejects `ExactCompacted` if its minimum
safe capacity does not fit. It never silently reduces liveness quotas elsewhere.

### 6.4 Low-contention capture

Opaque feedback should be generated in a sparse compute pass over the existing
forward visibility/depth/material data where possible. One workgroup handles a
tile, reconstructs the same receiver gather, and emits each unique owner once.
This avoids a global atomic per shaded opaque fragment.

For producers that must report during their real gather:

- use subgroup matching/ballots to combine lanes with the same
  `(resolvedProbe, requestedProbe, page, tile, role)` key;
- reduce FP32 weights and choose one elected lane for the append reservation;
- use workgroup shared-memory tables in fog and particle compute paths;
- reserve append space once per subgroup/workgroup chunk; and
- never spin on a globally contended hash table.

The capture header contains append count, dropped count, per-producer reserved
range/counter, overflow bit, generation, frame serial, and ABI version. Producers
cannot consume another producer's reserved minimum; unused reservations may be
borrowed through a final shared overflow range.

If any required producer range overflows, mark the entire write generation
invalid for scheduling. Keep diagnostics, but consume the ordinary scheduler
priors next frame rather than a preferential partial sample. Adapt sampling or
request a larger admitted capacity on a later safe resource transition; never
reallocate in the middle of a frame.

### 6.5 GPU sort, unique, and reduce

Use a stable, bounded GPU radix pipeline:

1. reset the write header and per-producer counters;
2. capture compact records;
3. build a 64-bit primary key from resolved probe and exact tile, with requested
   probe/page/role as secondary fields;
4. perform 8-bit LSD radix histogram, prefix, and scatter passes over only the
   append count using indirect dispatch;
5. mark run boundaries for identical probe/tile/role records;
6. reduce duplicates to one exact tile contribution;
7. reduce tile records by resolved probe into the next summary bank;
8. separately reduce requested-versus-resolved fallback pressure by requested
   page; and
9. validate counts/generation, then publish the bank header.

Reuse a proven project scan/radix primitive if one exists after the prerequisite
work; otherwise implement this as a shared GPU primitive with CPU reference
tests rather than embedding an untestable private sorter in B1. Sort operations
are integer-deterministic. FP32 reductions may use a fixed tree order in capture
mode; production may use subgroup reductions if its error stays within the
reference tolerance.

### 6.6 Scheduler integration and quotas

Convert estimated contribution mass and unique tile coverage into bounded
dimensionless priority using robust frame-local normalization (median/P90 or a
log transform), not a scene-dependent absolute threshold. Proposed form:

```text
massScore     = log2(1 + mass / max(medianMass, epsilon))
coverageScore = log2(1 + uniqueTileCount)
feedbackScore = saturate(kMass * massScore + kCoverage * coverageScore + roleBias)
```

The values of `kMass`, `kCoverage`, and the cap are tuned from captures and then
versioned. Feedback adds to the existing urgency/deadline/cost score; it does
not replace age, dirty revision, residual propagation, residency, or completion
ordering.

Reserve scheduler capacity before feedback ranking for:

- off-screen/background safety refresh;
- maximum-age/deadline refresh;
- newly exposed or newly resident geometry;
- incomplete refinement-brick/base ownership transitions;
- reflection captures and non-main views;
- residual/transport propagation and certificate work; and
- at least one forward-progress slot for any non-empty starved class.

Use weighted fair queues or explicit minimum counts, and publish proof counters
showing requested and serviced quota per class. Feedback candidates compete only
for the remaining discretionary work. If the previous bank is missing, invalid,
overflowed, stale, or from a different layout/viewport generation, its score is
zero and the scheduler continues normally.

### 6.7 B1 diagnostics and debug views

Expose:

- records emitted/unique/reduced/dropped by producer;
- append and sort capacity utilization;
- sampled tile fraction and inverse-probability distribution;
- exact unique tiles, mass, maximum weight, consumer mask, and fallback pressure
  per probe/page;
- subgroup/workgroup aggregation ratio and global atomic reservations;
- capture, radix, unique, reduce, and scheduler-consume GPU times;
- valid/stale/overflowed bank counts and one-frame age;
- quota requested/serviced/starved counts;
- time from newly important receiver to first update and coherent publication;
- correlation between predicted importance and path-traced screen error; and
- a view coloring actual resolved probe owner, requested fallback owner,
  contribution mass, and scheduling result.

### 6.8 B1 acceptance gate

B1 is complete only when:

- GPU records match a CPU gather/reference model for IDs, pages, roles, weights,
  and unique tile counts across scrolling, sparse fallback, B3 ownership, and
  overlapping volumes;
- sampling-rate changes preserve normalized mass within statistical confidence;
- no hot path performs one global per-probe atomic per fragment;
- overflow, stale bank, resize, device loss, disabled mode, and zero-probe cases
  safely use ordinary scheduling;
- every non-feedback quota makes bounded forward progress in an adversarial
  camera-focused scene;
- receiver-weighted time-to-visible and time-to-certified error improve on the
  target scenes at equal ray/work budget;
- feedback overhead is lower than the saved/redirected GI time at P95 and does
  not cause a P99 frame-time regression; and
- the disabled mode allocates zero B1 V2 record/sort resources and the legacy
  reference mode remains available only for A/B until removal is approved.

## 7. C1 — Exact opacity-micromap acceleration

### 7.1 Backend decision

Implement a shared content/caching contract and two runtime backends:

```text
IOpacityMicromapBackend
    NullOpacityMicromapBackend       // always available; ordinary candidates
    ExtOpacityMicromapBackend        // implemented by this plan
    KhrOpacityMicromapBackend        // reserved identifier; not implemented
```

The EXT backend is useful despite the later KHR extension. The traversal
representation and four-state behavior solve the same workload, Silk.NET already
provides the EXT API, and current RTX 3060 drivers expose it. The KHR promotion
made significant object/build and shader changes, so isolate Vulkan calls and
backend-specific cooked payloads rather than pretending one set of structs can
serve both APIs.

The null backend is not an error path. It is the canonical output whenever the
extension, feature, four-state format, cooked payload, memory, content eligibility,
or measured qualification is absent. KHR support can be added later after the
binding/toolchain/driver matrix is real; it is not C1's current definition of
done. Do not hand-author a partial KHR binding.

### 7.2 Exact eligibility contract

Only a primitive/material variant satisfying every row is eligible:

| Input | Required C1 state | Otherwise |
| --- | --- | --- |
| Geometry | static triangle topology and stable UVs used by the DDGI hit shader | ordinary non-opaque candidate path |
| Material alpha mode | deterministic `MASK` | opaque fast path or ordinary blend/thin path |
| Alpha function | standard single sampled alpha multiplied only by cook-time-known constants | candidate path |
| Material factor | finite, frozen base alpha included in the cook key | candidate path on runtime change |
| Vertex alpha | every vertex in the baked primitive range has the same bit-identical cook-time constant, preferably exactly one | candidate path for varying/interpolated alpha |
| Texture | immutable alpha content with the exact runtime format/decoded values available to the baker | candidate path |
| UV selection/transform | exact texcoord set and identity transform initially; a later canonical transformed-UV stream must be shared bit-for-bit by baker and shader | candidate path |
| Sampler | SDK-qualified point or linear policy exactly matching DDGI | candidate path |
| LOD/mip | the same fixed DDGI ray LOD and complete resident mip data | candidate path |
| Cutoff | finite frozen value; shipping comparison remains `alpha >= cutoff` opaque | candidate path on change |
| Transmission/procedural behavior | no thin transmission, animated mask, procedural alpha, video texture, deforming UV, or per-ray alpha override | candidate path |
| Format | four-state OMM supported at an admitted per-triangle subdivision | candidate path |

Current DDGI alpha is composed from material base alpha, interpolated vertex
alpha, and sampled base-color alpha after a material UV transform, at a
deterministic material LOD. The cooker must model that exact expression. For a
uniform non-zero material/vertex multiplier, derive the texture threshold in a
numerically audited way while preserving the equality rule. A zero multiplier
is uniformly transparent. If exact floating-point comparison cannot be proven,
mark the affected microtriangles unknown or reject the primitive; do not shift
the cutoff by an epsilon. Begin with identity UV transforms because CPU-prebaked
transforms are not automatically bit-identical to GPU shader arithmetic. Extend
eligibility only by making a canonical transformed-UV stream an input shared by
both the OMM baker and DDGI hit shader and passing conformance.

Compressed textures must be baked from the exact compressed/decoded alpha values
seen by the runtime sampler, not an uncompressed source PNG that may quantize or
filter differently. sRGB alpha remains linear per the Vulkan format semantics.
Border color, address mode, normalization, texture transform, and mip selection
are part of the key. If a future shader derives LOD per ray, either bake a
conservative result over every reachable mip or disable OMM for that material.

Rigid instances may share an eligible static BLAS. Skinned/deforming geometry,
procedural foliage proxies, and animated alpha remain excluded in this plan even
if a particular driver could technically build an OMM for them. This keeps
content lifetime and amortization bounded and matches the roadmap gate.

### 7.3 NVIDIA OMM SDK asset-tool integration

Use the NVIDIA RTX Opacity Micro-Map SDK CPU baker rather than reimplementing its
contour classifier, UV reuse detection, subdivision heuristic, special-index
promotion, post-bake deduplication, and spatial ordering.

Integration requirements:

1. pin an audited SDK commit/release and record source URL, commit, license,
   build flags, compiler, and binary hash in provenance and third-party notices;
2. build a small versioned native C ABI bridge rather than exposing C++ ABI to
   .NET;
3. invoke it from `Njulf.AssetTool` and `ModelAssetCooker`; no baker library is
   loaded by the shipping renderer;
4. begin with the CPU baker as NVIDIA recommends for evaluation/offline data;
5. force `OC1_4_State`; the asset tool must reject or ignore any two-state
   output option in production mode;
6. pass the exact index buffer, selected/transformed UVs, alpha texture bytes,
   sampler policy, fixed mip, effective cutoff, and per-primitive subdivision;
7. let the SDK select subdivision from UV texel coverage, capped by the queried
   device-class policy, then sweep/cook alternate levels for qualification;
8. retain unknown-transparent/unknown-opaque states at every mixed/cutoff
   boundary so the existing ray-query shader remains authoritative; and
9. run the SDK image/debug dump and the project's independent classifier oracle
   in asset-tool validation mode.

The bridge must use bounded inputs, checked size conversion, deterministic
allocator callbacks, structured errors, cancellation, and transactional output.
A native crash or bake failure fails that optional asset chunk without corrupting
the base model. Ordinary cooked geometry remains complete and usable.

Do not add the GPU baker initially. Add it only in a later slice if measurements
show that substantial eligible static content cannot be cooked offline or load
time is dominated by CPU baking. A GPU baker must have an independent async
compute/frame budget and the same exact-output gate; it is not justified merely
because the SDK provides one.

### 7.4 Cooked schema and content key

Add an optional, checksummed model chunk with:

```text
OpacityMicromapCookHeader
    Magic, schema version, cook ABI, endianness
    BackendPayloadKind = VulkanExtFourState
    SDK version/commit hash
    SourceContentHash
    OMM format and maximum subdivision
    primitive count and usage histogram count
    OMM data bytes, index bytes, descriptor/histogram counts
    offsets/sizes/checksums for every span

OpacityMicromapMaterialContract
    material/primitive range
    texcoord set and full UV transform bits
    texture content/format/mip hash
    sampler/address/border policy
    material alpha bits, uniform vertex alpha bits, cutoff bits, fixed LOD bits
    alpha-contract and shader ABI revisions

Backend payload
    four-state micromap data
    per-primitive OMM indices and index format
    usage counts grouped by format/subdivision
    optional debug classification statistics
```

Hash raw canonical bytes, not object hash codes. The runtime key includes mesh
topology/index/UV hashes, material contract, texture content/residency revision,
alpha/shader ABI, payload schema/backend, and subdivision policy. Transform-only
instance changes do not invalidate the payload. Any material/texture/key mismatch
uses the plain BLAS immediately and queues no synchronous rebake.

The base asset and optional chunk are separate transactions. Unknown schema,
truncation, checksum failure, out-of-range offsets, hostile counts, or unsupported
payload kind produces one bounded diagnostic and ignores the chunk.

### 7.5 EXT runtime resource lifecycle

`OpacityMicromapManager` owns device objects and a content-keyed cache. For each
admitted payload:

1. validate EXT extension/feature/four-state subdivision limits and required
   acceleration-structure dependencies;
2. validate all cooked counts and query `GetMicromapBuildSizesEXT` on the actual
   device; never trust cooked byte estimates as allocation sizes;
3. reserve persistent input, micromap storage, build scratch, query/compaction,
   and retirement headroom through the central planner;
4. upload OMM data, per-primitive indices, and usage counts to device-addressable
   buffers with the required usage flags;
5. record `CmdBuildMicromapsEXT` on the existing AS build queue using reusable,
   correctly aligned scratch;
6. barrier micromap build writes before size query/copy or BLAS build reads;
7. query compacted size, compact when the measured saving clears the same
   absolute/relative policy used for BLAS compaction, and retain the original
   until the compact copy completes;
8. chain `AccelerationStructureTrianglesOpacityMicromapEXT` into the matching
   triangle geometry while building the owning BLAS;
9. compact the BLAS only after it was built against the final micromap object;
10. publish the OMM-enhanced BLAS and micromap lease together; and
11. retire buffers, micromap objects, BLAS objects, descriptor-visible state,
    and query slots through fence/timeline completion.

The RTX 3060 reports no host-command support, so GPU command-buffer builds are
the required path. No synchronous host build may appear in frame submission.
Builds are load/stream work with a bounded per-frame byte/time budget. Until the
entire OMM+BLAS generation is ready, TLAS instances reference the ordinary
candidate-tested BLAS.

Never use the EXT force-two-state instance/ray flags. Unknown states must still
surface as ray-query candidates and execute `DdgiCandidatePassesOpacityPolicy`.
Known transparent states disappear from candidate traversal; known opaque states
are committed by traversal. Record candidate classifications in a conformance
shader so known-opaque and ordinary opaque hits retain identical instance,
primitive, barycentric, winding, and material metadata.

### 7.6 BLAS sharing and variants

The current static BLAS cache is primarily mesh-keyed, while OMM attachment also
depends on material/texture/cutoff/UV policy. Replace the relevant cache key with:

```text
StaticBlasVariantKey
    MeshGeometryKey
    RayGeometryPolicy
    OpacityMicromapContentKeyOrNull
    AccelerationStructureBuildAbi
```

Identical variants share a BLAS and micromap lease across rigid instances.
Instances of one mesh with different alpha material contracts either use separate
bounded variants or the plain BLAS. Enforce a per-mesh and global OMM variant cap;
on pressure, retain the most reused/beneficial qualified variants and fall back
for the rest. Never evict the only plain fallback in order to keep an OMM variant.

BLAS/OMM eviction is atomic from the TLAS publisher's perspective. A texture
reload or material revision stops selecting the old variant immediately, builds
or finds the correct replacement asynchronously, and retires the old generation
only after no TLAS references it.

### 7.7 Backend abstraction for future KHR support

Keep these concerns backend-neutral:

- exact content eligibility and source hash;
- baker invocation and independent classification oracle;
- requested/effective settings and diagnostics;
- BLAS variant ownership, memory admission, and retirement;
- conformance scene/capture definitions; and
- performance/quality gate.

Keep payload structs, Vulkan object types, feature chains, shader compile defines,
build commands, barriers, and attachment structs backend-specific. A future KHR
backend must get its own payload kind and shader variant, including the KHR ray
query execution-mode requirement. It must pass the full parity/performance gate;
it cannot reinterpret the EXT payload without a specified conversion.

### 7.8 C1 diagnostics

Expose per asset and aggregate:

- eligible/rejected primitive and triangle counts by reason;
- opaque, transparent, unknown-opaque, and unknown-transparent microtriangle
  counts and known coverage `(T + O) / total`;
- subdivision histogram, payload bytes, reuse before/after bake, and disk bytes;
- upload, uncompacted, compacted, scratch, peak-live, and BLAS-variant bytes;
- cook/load/build/compact/BLAS/TLAS wall and GPU time;
- cache hits, misses, variants, rebuilds, evictions, fallbacks, and stale-key
  attempts;
- candidate invocations, candidates rejected/confirmed, alpha texture samples,
  trace time, total DDGI time, and amortized construction time;
- backend and whether the device resolves OMM in hardware or a driver/software
  path when this can be identified; and
- debug overlays for O/T/UO/UT, subdivision, candidate savings, and fallback
  reason.

Do not use the SDK's example “about 30% trace reduction” as a project gate.
Candidate reduction is explanatory evidence only; total GI time decides.

### 7.9 C1 equality and performance gate

Qualification requires:

- bit-identical mask survival for exhaustive CPU texture/sampler/cutoff edge
  cases, including alpha exactly equal to cutoff;
- OMM-on/off agreement for committed hit/miss, instance, primitive, barycentrics,
  distance within the existing ray tolerance, front/back face, and material
  across the alpha conformance suite;
- identical thin-transmission/blend behavior because those assets remain on the
  fallback path;
- forced fallback for animated alpha, procedural masks, varying vertex alpha,
  deforming UVs/geometry, missing mips, texture residency/change, unsupported
  samplers/formats, variant-cap pressure, corrupted chunks, and extension loss;
- Vulkan validation clean builds, compaction, BLAS attachment, eviction,
  resize/reload, and device-loss teardown;
- construction and disk/residency cost amortized over measured asset lifetime;
- lower mean and P95 *total DDGI time* at equal rays/work and no P99 frame hitch;
  and
- no memory-budget or general BLAS-cache regression when disabled/fallen back.

Run the gate separately on the RTX 3060, where NVIDIA documents pre-Ada OMM
resolution as software/emulated, and an Ada-or-newer NVIDIA GPU with hardware
OMM. The feature may qualify on one class and remain off on the other. Also run
the null path on AMD/Intel or another device without EXT support.

## 8. C3 — MIS-correct directional ray guiding

### 8.1 Entry conditions and scope

C3 starts only after the prerequisite spatial emissive hierarchy and cached
re-lighting paths pass their own gates. First prove that residual variance comes
from finding important *directions* (small bright apertures, compact emissives,
strong environment features), rather than a broken light PDF, stale cache,
insufficient refresh, or too few visibility rays.

The first production experiment guides DDGI transport/source directions only.
It does not guide relocation rays, probe-classification rays, thin-wall
visibility maintenance, transport audits, or caustic photons. Those retain their
independent uniform/reference distributions until separate proofs exist.

Guiding changes sampling, not the transport equation. At equal total work its
converged expected value must match an independent uniform reference. If a scene
has no learned energy or a distribution is stale/invalid, sampling degenerates
to a uniform sphere without changing resource ownership or publication.

### 8.2 Equal-area directional domain

Map a unit direction to an equal-area square:

```text
u = wrap(atan2(direction.y, direction.x) / (2*pi), 0, 1)
v = direction.z * 0.5 + 0.5
```

The inverse samples `phi = 2*pi*u`, `z = 2*v - 1`, and
`r = sqrt(max(0, 1 - z*z))`. Uniform `(u,v)` therefore has density
`1 / (4*pi)` on the sphere. An 8x8 grid has exactly 64 equal-solid-angle leaves,
each with `Omega = 4*pi/64`. This avoids a lookup-table approximation to the
solid angle of octahedral or cube texels.

Arrange the 8x8 leaves in a three-level 4-way quadtree. Sampling descends using
the stored child energies and then samples `(u,v)` uniformly inside the selected
leaf. The guided PDF is exactly:

```text
Pleaf = product(selectedChildWeight / sumSiblingWeights)
pGuided(direction) = Pleaf / OmegaLeaf
```

Compute the PDF from the same quantized child weights and branch comparisons used
to generate the sample. Do not compute it later from a newer FP32 histogram.
The azimuth seam is periodic in training/filtering/debug visualization.

Qualification sweeps 4x4, 8x8, and 16x16 leaves. The baseline 8x8 choice is
frozen only if it provides the best quality per total byte/time on the target
scenes; the ABI encodes resolution so captures cannot be misread.

### 8.3 Persistent distribution ABI and memory

Store one distribution per *physical probe slot*, guarded by virtual-probe ID,
page generation, and distribution generation. Sparse slot reuse cannot inherit
another probe's guide.

The 8x8 candidate stores 64 leaves, 16 level-2 nodes, 4 level-1 nodes, and one
root: 85 non-negative weights. Start with FP16 persistent weights and FP32
training/build arithmetic. Pad each bank to a naturally aligned stride containing:

```text
GPUSimpleDdgiGuidingDistributionHeader
    VirtualProbeId
    PageGeneration
    DistributionGeneration
    DirectionProposalEpoch
    SampleCountAndAge
    TotalIncidentEnergy          // finite FP32 scale for temporal learning
    Flags

85 x float16 normalized non-negative hierarchy weights in [0, 1]
padding to the versioned stride
```

Use two banks so frame N samples a fully published distribution while new
evidence builds frame N+1. For an 8x8 candidate, the planner starts from
`alignUp(headerBytes + 85*2, storageAlignment) * physicalProbeCapacity * 2`, not
from an unexplained fixed allocation. Report the exact stride and total. A FP32
reference bank is allocated only by validation mode.

Training scratch is per updated probe/ray batch:

```text
trainingBytes = scheduledGuidedProbeCapacity * 64 * sizeof(float)
```

If one probe spans several ray workgroups, write one 64-bin partial per workgroup
and reduce those partials deterministically. Otherwise, use shared-memory bins
and one coalesced FP32 store per leaf. Avoid a device-wide atomic for every ray.

### 8.4 Unbiased training signal

For a direction sampled from density `pSample`, deposit a non-negative estimate
of incident energy into its leaf:

```text
sampleEnergy = luminance(max(Li, 0)) / pSample
leafIntegralEstimate += sampleEnergy
```

`Li` is the finite incident source radiance produced by that ray before probe
projection. Tag it with its content/light/environment revisions. Deposit each
new ray exactly once; do not train repeatedly from a temporally blended atlas or
from the guide's own reconstructed distribution.

The proposal may use robust winsorization or a log-domain temporal filter to stop
one firefly from steering all rays, because proposal bias does not bias an
estimator that retains support and uses its exact PDF. However:

- preserve an unclamped diagnostic/reference accumulator;
- derive the robust cap from prior published statistics, never future samples;
- record clipped sample count and energy;
- reset or rapidly relearn only regions affected by the prerequisite revision
  journal; and
- fall back to uniform when total finite energy is zero, non-finite, too old, or
  below a minimum effective sample count.

Build parent sums in FP32, retain finite total incident energy in the header, and
normalize all hierarchy weights by the root before FP16 conversion. Quantize and
then validate every node. A zero sibling sum chooses children uniformly and
reports the matching probability. Publication fails closed to a uniform
distribution on a NaN, negative value, inconsistent generation, or invalid root.

Use temporally decayed learning with age/effective sample count rather than a
fixed blend factor for every probe. A newly exposed/invalidated probe learns
quickly; a stable high-sample probe changes slowly. Measure KL/Jensen-Shannon or
total-variation change only for direction-epoch rotation decisions, not as a
correctness threshold.

### 8.5 Sampling and exact estimator

Let `alpha` be the uniform mixture fraction, never below 0.10 in this experiment:

```text
pUniform(direction) = 1 / (4*pi)
pMix(direction) = alpha*pUniform(direction)
                + (1-alpha)*pGuided(direction)
```

For each guided-transport slot, select the uniform or guided branch with the same
stable random variate contract used to record the branch. Regardless of branch,
the ray is a sample from `pMix`; its one-sample contribution is `F(direction) /
pMix(direction)`. The branch PDF alone is not the estimator denominator.

Retain a separate fixed uniform maintenance set of
`max(MinimumMaintenanceRays, ceil(MaintenanceFraction * totalRays))`; begin the
sweep at 25%, with a hard non-zero count per probe update. Those directions are
stratified/stable and continue to own visibility moments, relocation,
classification, thin-blocker coverage, and maintenance diagnostics.

If maintenance rays also contribute radiometrically, combine the two techniques
with the balance heuristic. For `nU` maintenance samples from `pUniform` and
`nM` samples from `pMix`, a sample from technique `j` contributes:

```text
wj(direction) = nj*pj(direction) /
                (nU*pUniform(direction) + nM*pMix(direction))
contribution  = F(direction) * wj(direction) / (nj*pj(direction))
              = F(direction) /
                (nU*pUniform(direction) + nM*pMix(direction))
```

Sum the contributions from both techniques. Handle a zero technique count
explicitly. CPU double-precision oracles, GLSL, and C# helpers must agree for
branch selection, leaf probability, continuous PDF, balance weight, and
estimator on boundaries and degenerate histograms.

Do not clamp inverse PDF in the shipping estimator. The uniform floor already
bounds it. If a safety guard catches zero/non-finite PDF, discard that update,
use the previous published probe, and increment a fatal validation counter.

### 8.6 Projection and visibility ownership

The existing equal-weight ray blend cannot consume variable-PDF samples
unchanged. Refactor the trace/blend interface so every radiometric sample carries
its technique and FP32 generation-time PDF through ray scratch/source cache.

For irradiance direction `n`, the Monte Carlo term is based on:

```text
Li(direction) * max(dot(n, direction), 0) / PDF
```

with the multi-technique denominator above and the project's existing
normalization. L2 radiance projection similarly multiplies each SH basis by the
exact estimator weight. Accumulate in FP32; convert only at the versioned atlas
publication boundary.

Guided samples do not update directional visibility moments in the first
promotion because preferential bright-direction sampling can miss dark nearby
blockers. Uniform maintenance rays remain the sole visibility/relocation source.
If later work wants to use guided hits for visibility, it needs a separate
directional reconstruction proof and cannot simply apply radiometric inverse-PDF
weights to distance moments.

The positive diffuse transport operator remains non-negative: reject non-finite
or negative radiance before projection, and never let signed SH reconstruction
replace its input. C3 modifies sampling weights, not positivity or publication
ownership.

### 8.7 Stable direction identity and source-cache reuse

Variable directions cannot inherit fixed Fibonacci-codebook identity. Define a
versioned `SimpleDdgiDirectionSampleIdentity`:

```text
StableProbeId
ProposalEpoch
SlotIndex
Technique                    // uniform maintenance or mixture
MixtureBranch                // uniform or guided
LeafIndex
IntraLeafSampleBits
PackedDirectionOct32         // authoritative decoded direction check
GenerationTimePdfBits        // authoritative FP32 PDF
DistributionGeneration
```

The stable hash inputs regenerate the direction; packed direction and PDF detect
ABI drift and permit exact cached re-lighting. Source-cache records store the PDF
that generated the direction. They never reconstruct it from the current
distribution.

Keep sampled directions stable for a minimum proposal epoch (start evaluation at
16–32 frames). When a guide changes materially, rotate only a bounded fraction
of guided slots per update. Unchanged slots retain hit geometry and can use the
prerequisite cached re-light path; rotated slots retrace. Geometry/alpha revision
still invalidates hits normally. A distribution update alone does not make an
old sample statistically invalid because its stored generating PDF remains exact.

Make cache cardinality explicit:

- maintenance slot count;
- guided slot count;
- active proposal epochs retained;
- source records per probe/page;
- direction rotations/retraces per frame; and
- bytes per direction identity/PDF sidecar.

Do not index source records solely by a direction-codebook ordinal after C3.
Warm-start/persisted data must key the new ABI and proposal epoch or fall back.

### 8.8 Scheduler and audit integration

C3 is admitted within the existing ray budget; it does not create unbounded extra
rays. The scheduler's cost model learns/measures:

- maintenance versus guided trace cost;
- distribution build cost;
- direction-rotation retrace cost;
- alpha/material divergence by proposal class; and
- estimator variance/effective sample size.

Reserve maintenance rays before guided work. Starvation or memory pressure
reduces guided slots first and ultimately selects uniform-only mode. A probe with
no valid guide still receives its ordinary update quota.

Update tail, convergence, transport-cache, and audit semantics to use weighted
energy/residual estimates and effective sample size rather than assuming every
ray has weight `4*pi/N`. The independent audit set is stable uniform-sphere and
must not share the learned distribution. Compare the guided estimator against
that audit with confidence intervals; a guide is never allowed to certify itself.

### 8.9 C3 diagnostics and debug views

Expose:

- learned leaf energy, hierarchy probabilities, entropy, age, sample count, and
  distribution-generation views;
- uniform/guided branch counts, maintenance/guided slot counts, rotations, cache
  re-lights, and retraces;
- PDF and inverse-PDF min/P50/P95/P99/max, balance weights, zero/invalid guards,
  clipped training samples, and effective sample size;
- distribution build, sampling overhead, trace, blend/projection, and total GI
  times;
- FP16 versus FP32 guide error and 4x4/8x8/16x16 memory/time;
- per-probe variance/MSE against uniform and path-traced references;
- source-cache hit rate/cardinality and tail/certificate differences; and
- spherical debug maps showing learned density, selected rays, light/aperture
  direction, and uniform support.

### 8.10 C3 acceptance gate

C3 qualifies only when:

- analytic constant, single-lobe, multi-lobe, zero-energy, seam, pole, and
  adversarial-tiny-bin distributions pass PDF normalization and integral tests;
- GPU sampling frequencies match the stored hierarchy probabilities under a
  statistical goodness-of-fit test;
- constant-radiance, HDR environment, small aperture, compact emissive, and
  occluded-emitter estimators agree with brute-force/uniform reference within
  confidence and show no persistent energy bias;
- uniform maintenance preserves visibility, relocation, thin-wall, and probe
  classification gates;
- cache reuse, direction rotation, regional revision, sparse slot reuse,
  scrolling, and warm-start transitions never use a mismatched PDF/direction;
- independent uniform audits and the final transport certificate remain valid;
- equal-time quality improves materially, or equal-error total GI time falls,
  in the scenes that motivated guiding, without a regression in diffuse scenes;
- P95/P99 total GI time, memory, and source-cache pressure fit the promoted
  profile; and
- disabled/rejected/untrained modes allocate nothing C3-specific and produce the
  ordinary uniform result.

## 9. C4 — Dedicated tagged-caustic cache

### 9.1 Authored scope

C4 is not inferred from every shiny material. Add an explicit material/instance
contract:

```text
GiCausticParticipationMode:
    None
    MirrorHero
    ClosedDielectricHero
    RoughSpecularReference
```

`MirrorHero` requires a finite, energy-conserving reflective model.
`ClosedDielectricHero` additionally requires a watertight consistently oriented
closed mesh, valid geometric normals, finite IOR, absorption/attenuation data,
and an unambiguous interior/exterior medium. Reject open/thin glass, alpha blend,
non-manifold topology, nested unsupported media, animated topology, or missing
thickness semantics. A rigid or qualified current-pose closed object may be
admitted only after its AS and material revisions are available to the photon
pass.

`RoughSpecularReference` is for comparison and uses the exact GGX/VNDF PDF. It
does not become a preset mode until its variance and cost beat mirror/dielectric
scope. Ordinary metallic/rough surfaces, water, particles, foliage, and all
materials without the explicit tag remain `None` and incur no C4 work.

The authoring UI reports why a requested hero is rejected and estimates its
photon/cache cost. Add an asset-tool topology/material validator; never discover
an invalid closed volume only after rendering starts.

### 9.2 Path ownership

The cache owns only paths of the form:

```text
emitter -> one or more tagged specular/refraction events -> first diffuse receiver
```

At least one tagged event is mandatory. A direct emitter-to-diffuse path is not
stored. Stop at the first qualified diffuse receiver, a configured maximum
specular depth, escape, unsupported medium, alpha/thin surface, or path-weight
termination. Start with a maximum of two dielectric interfaces through one
closed hero object or one mirror event; broader medium stacks are explicitly
deferred.

Store incident photon flux at the diffuse hit. Apply the receiver's current
diffuse BRDF/albedo during resolve, so receiver material changes do not require
rebaking photon flux and energy is not applied twice.

The following resources are forbidden C4 outputs:

- Simple-DDGI source cache;
- irradiance, visibility, transport, or directional-radiance atlases;
- B3 refinement-brick data;
- DDGI scheduler residual/tail values; and
- environment/reflection-probe convolution.

The forward composite reads C4 as a separately tagged indirect component after
canonical DDGI. A debug ownership view must distinguish direct, DDGI diffuse,
rough reflection, caustic, and near-field residual energy.

### 9.3 Light-space task generation and exact flux

Generate photon tasks on the GPU from the prerequisite light and emissive
distributions. A task records stable emitter ID, hero caster ID, sample epoch,
slot, and every selection/proposal PDF.

Use a bounded two-level proposal:

1. select an eligible emitter by emitted power and its potential relationship to
   admitted hero bounds, retaining exact `pEmitter`;
2. select a hero caster with an exact discrete `pCasterGivenEmitter`;
3. sample emitter position/area with `pPosition`;
4. sample a direction from a mixture of the emitter's canonical emission
   distribution and a cone/solid-angle proposal toward the caster bound; and
5. trace and reject the task if ordinary occlusion or a different first tagged
   hero prevents the selected caster from being the selected tagged event.

The canonical emission component retains support. Caster-bound targeting is an
importance proposal, not permission to ignore blockers or alter radiance. Use
the exact mixture PDF for the generated direction.

Initial photon flux is derived from emitted radiometric power and the complete
joint PDF:

```text
Phi0 = emittedContribution /
       (photonTaskCount * pEmitter * pCasterGivenEmitter *
        pPosition * pDirection)
```

At each non-delta sampled event multiply throughput by
`f * abs(cosTheta) / pEvent`. At a Fresnel reflection/refraction branch, divide
by the sampled branch probability. Delta events use their correct measure-aware
throughput and refraction radiance/eta convention. Russian roulette, if enabled
after a minimum depth, divides surviving throughput by survival probability.

CPU double-precision oracles must cover point, spot, directional, area, emissive
triangle, mirror, Fresnel reflection, transmission, absorption, total internal
reflection, and miss cases. The GPU records enough sampled PDFs/throughput in a
validation buffer to replay any non-finite or high-weight path.

### 9.4 Photon differentials and trace pass

Use the current-pose TLAS and frozen material/alpha semantics. Ordinary alpha
mask candidates are confirmed exactly as DDGI; thin/blended surfaces do not
become dielectric hero boundaries.

Propagate a ray cone or photon differentials through reflection/refraction and
intersect it with the diffuse receiver plane. Store an anisotropic footprint
ellipse (two tangent-plane axes or an equivalent positive covariance) as in
NVIDIA's adaptive anisotropic photon-scattering approach. Validate analytic
differentials against two auxiliary differential rays in reference mode.

Clamp footprint eigenvalues only to explicit world-space minimum/maximum support
radii chosen to avoid singular kernels and unbounded neighbor searches. Renormalize
the kernel after any clamp; do not clamp flux. Record clamp counts and the
integrated kernel error.

Record successful paths into a bounded append buffer. Capacity equals the
admitted photon task count because each task produces at most one first-diffuse
record. An append overflow is therefore an ABI/planner error and invalidates the
write generation. Unsuccessful tasks still count in the denominator that
normalized `Phi0`; otherwise changing scene occlusion would change emitted
energy incorrectly.

### 9.5 Photon record and cache layout

Start with an auditable 80-byte FP32 reference record:

```text
GPUCausticPhotonReferenceV1                         // 80 bytes
    float3 WorldPosition; float SupportRadius;
    float3 IncidentFlux;  float PathWeightDebug;
    uint PackedIncidentDirection;
    uint PackedReceiverNormal;
    uint PathTagAndDepth;
    uint StablePhotonId;
    float4 TangentPlaneFootprint;
    uint SourceId;
    uint HeroInstanceId;
    uint TransportRevision;
    uint CacheGeneration;
```

The five 16-byte groups above make the reference stride 80 bytes. The final field
ordering must be C#/GLSL offset-tested. A 48- or 64-byte candidate may pack
direction/normal, FP16 axes, and HDR flux only after an image/energy/range gate
proves it on the reference corpus. Do not choose RGB packing that overflows bright
caustics or loses color ratios merely to reach an assumed stride.

Build a separate world-space cache:

1. compute an exact signed cell coordinate from world position and configured
   cell size/cascade;
2. sort records by `(cellKey, stablePhotonHash)`;
3. mark cell runs and counts;
4. retain at most `K` photons per cell using deterministic bottom-K hash
   sampling; every record in a run of size `M > K` has inclusion probability
   `K/M` and retained flux is divided by that probability;
5. create an open-addressed table from full cell key to compact run offset/count;
6. validate probe count, table load factor, insertion success, bounds, and
   generation; and
7. publish the completed cache bank transactionally.

Bottom-K retention is an unbiased bounded sample; “first K after arbitrary GPU
arrival” is not. Full signed cell identity is compared after hashing so collisions
cannot return photons from another world cell. If table insertion or capacity
validation fails, retain the previous valid cache or composite zero, never a
spatially biased partial cache.

The planner derives:

```text
photonRecordBytes = photonTaskCapacity * photonStride * writeBankCount
cellTableCapacity = nextPowerOfTwo(ceil(maximumOccupiedCells / targetLoadFactor))
sortScratchBytes  = radixScratch(photonTaskCapacity, keyAndIndexStride)
```

Start with a maximum table load factor of 0.5 for predictable probes; measure
before changing it. Allocate two persistent cache generations only when
multi-frame publication is active. Scratch may alias B1 radix scratch if graph
lifetimes and maximum key/record stride are compatible.

### 9.6 Multi-frame construction and invalidation

For a low per-frame photon budget, build a coherent write generation across a
bounded number of frames while readers continue using the prior generation.
Normalize flux by the total number of emission tasks represented by that
generation, including failed paths. Publish only after the target task count,
sort/cache build, validation, and barriers complete.

Light/emissive distribution, hero transform/geometry/material, receiver geometry,
or caustic transport ABI changes invalidate affected paths. Begin with a complete
C4 write-generation rebuild on any relevant change; regional/source-partitioned
invalidation is a later optimization after correctness. The old generation may
remain visible for at most a configured stale grace interval only for unchanged
sources/geometry. Otherwise fade its separate contribution to zero and use no
caustic fallback rather than displaying energy in the wrong place.

Camera movement does not invalidate the world cache. Scene reload, origin rebase,
cell-size/layout change, or stable-ID loss does. Publication and retirement use
the same timeline/fence discipline as DDGI but never share generation headers.

### 9.7 Receiver gather, filtering, and composite

Resolve at full or half screen resolution over visible opaque receivers after
depth/forward visibility is available. For each receiver:

1. compute all cache cells overlapped by the bounded photon footprint/search
   radius;
2. fetch only matching full cell keys and valid generation/revision records;
3. reject backfacing/normal-incompatible photons conservatively;
4. evaluate a normalized anisotropic kernel in the receiver tangent plane;
5. accumulate incident flux density in FP32;
6. multiply the photon-flux density by the current energy-conserving diffuse
   BRDF; do not apply a second incident cosine because deposited photon flux per
   receiver area already estimates irradiance;
7. output non-negative scene-linear caustic radiance, confidence, and sample
   moments; and
8. composite under the separate C4 ownership tag.

Use a compact tile list so empty tiles dispatch no expensive gather. A half-res
mode uses depth/normal-aware reconstruction and must be compared with full-res
reference. Temporal stabilization, if required, uses motion, depth, normal,
object/material revision, cache generation, and neighborhood clipping. It does
not reuse C5 history and cannot preserve stale energy across a C4 revision.

Off-screen caustic receivers remain in the world cache and appear when visible;
the screen resolve is not the canonical storage. Transparent receivers and
volumetric caustics are outside the first scope.

### 9.8 Energy budget and firefly policy

Replace `MaximumEnergyFraction` and the current diffuse-relative composite clamp
with explicit budgets:

- photons/tasks per cache generation and per frame;
- maximum tagged path depth;
- hero material/caster count;
- cache cells, photons per cell, and bytes;
- construction and resolve milliseconds; and
- optional clearly labeled artistic source multiplier whose effect on emitted
  power is reported.

None of these clamps valid caustic radiance to a fraction of the local diffuse
baseline. A physically bright caustic on black/dark material is allowed. Energy
is controlled by emitted power, exact path throughput/PDF, kernel normalization,
and receiver BRDF.

Handle fireflies through better emitter/caster proposals, MIS, bottom-K retention,
more samples, robust temporal reconstruction, and diagnosing incorrect PDFs.
Reference mode has no radiance clamp. Any production display-domain outlier
control must be separately named, measured for bias/energy loss, and cannot be
used to pass the reference-energy gate.

### 9.9 C4 diagnostics and debug views

Expose:

- authored/admitted/rejected heroes by reason;
- task counts by emitter/caster, successful tagged events, first-diffuse hits,
  misses/occlusions, depth, TIR, and termination reason;
- complete PDF and throughput quantiles, emitted/retained/resolved energy, and
  energy by path tag/source/hero;
- differential/reference error, footprint axes, clamp counts, and cells visited;
- photon buffer, occupied cells, run lengths, bottom-K inclusion probabilities,
  hash probes/collisions/load, and invalid-generation counts;
- cache build latency, trace/sort/build/resolve/filter/composite and total GPU
  time, plus every byte/peak-live value;
- reference versus compact-record error and temporal history validity; and
- views for photon positions/directions/footprints, cells, path tags, source
  ownership, raw density, filtered radiance, confidence, and rejection reason.

### 9.10 C4 acceptance gate

C4 qualifies only when:

- CPU/GPU path throughput and PDF oracles pass every supported emitter/material
  case, including Fresnel/TIR/absorption;
- simple analytic mirror/refractive setups conserve energy within Monte Carlo
  confidence and converge to a path-traced reference;
- bottom-K cache retention remains unbiased as cell occupancy, task order, and
  GPU scheduling change;
- a dark receiver can display the correct bright caustic without a
  diffuse-relative cap;
- ordinary diffuse, untagged specular, direct lighting, DDGI, and C4 ownership
  neither omit nor double count supported paths;
- C4 bytes never appear in DDGI memory/atlas/source-cache categories and no C4
  value can be read by the transport operator;
- camera pans/cuts, cache publication, hero/light motion, scene reload, origin
  rebase, overflow, and invalid content produce no stale energy or flash;
- error per total millisecond beats C4-off in the dedicated hero scenes, and the
  experiment fits its independent P95/P99 time/memory budgets; and
- C4-off/no-hero/rejected modes allocate no photon/cache/history resources and
  issue no C4 pass or barrier.

## 10. C5 — Bounded near-field screen-space residual

### 10.1 Mandatory measure-before-build gate

The first C5 deliverable is evidence, not a shader. With canonical DDGI and
qualified B3 refinement bricks enabled, render path-traced/reference sequences
for visible hands, small props, doorway/crease contacts, compact emissives,
newly revealed geometry, and moving receiver/occluder pairs. Decompose error by
world-space spatial frequency and path ownership.

Proceed to GPU prototype work only if all are true:

- statistically material residual error remains inside the configured near-field
  distance after B3 convergence;
- the error is primarily visible-screen local detail that a short depth ray can
  observe;
- increasing B3 density/work by the same expected C5 cost is less effective;
- the error is not alpha, motion, visibility-moment, source-staleness, emissive
  PDF, or DDGI liveness failure; and
- a CPU/image-space oracle demonstrates a useful high-frequency residual without
  feeding DDGI-lit color back into transport.

If this gate fails, record “C5 evaluated; no material post-B3 opportunity,” keep
the feature off with zero resources, and consider C5 complete for this roadmap.
Do not build SSGI merely because the setting scaffold exists.

### 10.2 Trace-source ownership

When C5 is admitted, compile a forward-pipeline variant with a dedicated
scene-linear `DirectDiffuseAndEmissive` output. It contains:

- shadowed direct diffuse outgoing radiance;
- material emissive outgoing radiance in the frozen photometric convention; and
- no DDGI, B3, directional-radiance reflection, SSR, reflection probe,
  environment IBL, C4 caustic, C5 history, fog, transparency composite, bloom,
  exposure, or tone mapping.

All opaque/alpha-mask/qualified foliage paths that can populate the main depth
buffer write the same source contract. Transparent and particle receivers are
outside the first prototype.

Use `R16G16B16A16_SFLOAT` in reference mode. Evaluate
`B10G11R11_UFLOAT_PACK32` as the production source format only if HDR range,
quantization, blend/write support, and residual-image tests pass. The extra MRT,
attachment view, descriptor, framebuffer/dynamic-rendering declaration, and
shader output exist only in a C5-effective render-graph/pipeline variant. C5-off
must not pay permanent source bandwidth or pipeline-output cost.

Record a trace-source ABI and debug view. A shader test must fail if an indirect
term is added to this output. Captures verify that changing DDGI while direct
lighting/emission stays fixed leaves the source bit-identical.

### 10.3 Short Hi-Z trace

Trace at half resolution initially, with full and quarter resolution available
for reference/performance sweeps. One invocation corresponds to a stable receiver
pixel selected by depth-aware downsampling. Reconstruct world position, geometric
and shading normal, material/object identity, diffuse BRDF, and motion.

Generate a cosine-weighted hemisphere direction with the stable C5 sample ID.
Trace only a bounded world/view distance through the current depth/Hi-Z pyramid:

1. offset from the receiver with a scale-aware normal/depth bias;
2. advance in view space with hierarchical mip selection;
3. descend on a possible crossing;
4. perform bounded binary/secant refinement at mip zero;
5. validate thickness, front/back relationship, depth discontinuity, normal, and
   viewport bounds; and
6. return exact source-buffer coordinates plus confidence, or a miss.

Every loop has a compile-time/runtime-clamped maximum step count. Trace distance,
thickness, start bias, step count, mip visits, and refinement iterations are
settings owned by a versioned quality profile and displayed in diagnostics.
Dynamic resolution, reversed depth, projection changes, jitter, and view arrays
must use the same conventions as the existing Hi-Z builder.

At a valid hit, estimate a single diffuse bounce from the hit's
direct-diffuse/emissive outgoing radiance using the receiver BRDF, cosine, and
the exact ray PDF. Reject non-finite data. At a miss or rejected hit, the C5
candidate is invalid and its residual/confidence is zero; canonical DDGI/B3 and
environment already remain in the image. Do not add environment a second time.

Blue-noise/low-discrepancy rotation is allowed only through stable IDs and the
exact cosine PDF. More than one ray is a profile budget decision, not a shader
constant.

### 10.4 Frequency separation and composition

C5 does not add the complete screen-space bounce on top of DDGI. Build two
depth/normal/material-aware reconstructions of the valid candidate:

```text
nearEstimate = small-support spatiotemporal reconstruction
lowEstimate  = broader bilateral reconstruction whose world-space support
               matches the effective B3/base probe footprint
bandResidual = nearEstimate - lowEstimate
```

The broader radius is derived from the actual local refinement/base probe
spacing projected at the receiver, not a fixed pixel blur. Kernels ignore invalid
hits and renormalize; regions without enough support produce zero confidence,
not dark values. Apply a small tile-level mean-preservation correction so the
band does not accumulate low-frequency energy because of kernel truncation.

Composite in scene-linear indirect-diffuse space:

```text
finalIndirect = max(0, canonicalDdgiPlusB3 + confidence * bandResidual)
```

This permits real contact brightening or darkening while keeping the low-frequency
field under DDGI/B3 ownership. “No false darkening” means negative residual is
accepted only where the screen hit/history/material evidence is valid and the
reference supports it; an invalid/miss/edge/disocclusion always yields zero
residual. Record positive and negative residual energy independently.

Compare this fixed band-pass design with the existing positive-only
`max(candidate - low, 0)` scaffold in reference mode. The positive-only form may
hide false darkening but injects net energy and can double count; it cannot be
promoted unless an energy/reference gate proves otherwise.

Never use C5 confidence as an occlusion multiplier on canonical DDGI. Never
replace DDGI with black on a screen miss. Never temporally blur final composed
scene color to conceal invalid residual history.

### 10.5 Temporal reconstruction and disocclusion

Use an SVGF-style scene-linear temporal stage specialized for the residual:

- reproject by current motion vectors and view/jitter transform;
- validate previous receiver depth in world/view units;
- validate geometric and shading normal;
- require matching stable object ID, material revision, and trace-source ABI;
- require compatible local B3/base ownership, probe spacing, and C5 profile;
- reject camera cuts, viewport/resolution changes, origin rebases, and mode or
  scene-generation changes;
- clip history to a variance-aware current 3x3 neighborhood before blending;
- track first and second luminance moments and bounded history length; and
- reduce history weight as hit confidence, screen-edge distance, motion, or
  source validity falls.

The hit surface may move independently of the receiver. Store hit screen/depth
and object/material identity and validate it against the current source buffer;
receiver-only reprojection is insufficient for a moving emissive/occluder.

After temporal accumulation, use at most the measured number of edge-aware
à-trous/bilateral passes. Each pass reads depth, normal, material/object identity,
variance, and validity. Empty tiles are compacted out. Filtering cost and each
history rejection reason are timed/counted separately.

### 10.6 Resource layout

Add a `SimpleDdgiNearFieldResidualLayoutCompiler`. For scaled dimensions
`W = ceil(viewWidth*scale)`, `H = ceil(viewHeight*scale)`, calculate checked bytes
for the actual selected formats:

```text
full-resolution DirectDiffuseAndEmissive source
scaled raw candidate radiance + validity/confidence
scaled hit metadata (screen/depth/object/material/revision)
two scaled history radiance images
two scaled luminance-moment images
history length/validity
two scaled filter ping-pong images when filtering is admitted
tile activity/count/indirect-dispatch buffers
```

Declare which images can alias after their last read. History and source cannot
alias. Include image row/layer/mip alignment and allocator granularity rather than
reporting only `width*height*texelBytes`.

The compiler selects a complete profile or rejects it; it cannot omit hit
metadata/disocclusion history to squeeze under budget. On resize, allocate and
validate the new set before switching descriptors, then retire the old set after
GPU completion. Disabled, failed measure-before-build, B3-unqualified, source-
variant-unavailable, or budget-rejected states return an empty resource set and
zero render-graph passes.

### 10.7 Render order

The opaque forward pass writes normal scene color plus the optional direct-source
attachment. C5 trace/filter/composite runs after opaque depth/source are complete,
after the canonical DDGI contribution for those receivers is known, and before
transparent/fog/post-processing/TAA ownership would contaminate the source. C4
and C5 use separate inputs/outputs; whichever composites first is recorded in
the ownership ABI and neither can sample the other's output.

Barriers are explicit:

- color-attachment writes to compute sampled reads for the source;
- depth/Hi-Z writes to compute sampled reads;
- trace storage writes to temporal/filter reads;
- temporal/filter writes between iterations; and
- final C5 storage/color writes to subsequent graphics/post-process reads.

Do not use a generic full-pipeline barrier. Queue ownership is transferred only
if measurements justify async compute and the semaphore/timeline overlap is real.

### 10.8 C5 diagnostics and debug views

Expose:

- post-B3 reference error by region/frequency/path class and the go/no-go result;
- rays, hits, misses, screen exits, steps/mip visits, refinement, thickness/
  normal/depth rejects, and trace distance;
- source radiance, raw candidate, near estimate, low estimate, signed residual,
  final contribution, and ownership views;
- confidence components for depth, normal, motion, object, material revision,
  hit validity, history, and screen edge;
- history accepted/rejected by reason, length, moments, variance, and clamp count;
- positive/negative/net residual energy and low-frequency leakage;
- source MRT, trace, temporal, each filter, composite, and total GPU time;
- every requested/admitted/allocated/peak-live byte and tile compaction ratio;
  and
- path-traced MSE/SSIM or the project's locked perceptual/error metrics for DDGI,
  DDGI+B3, and DDGI+B3+C5 at equal total time.

### 10.9 C5 acceptance gate

C5 qualifies only when:

- the measure-before-build evidence remains reproducible and names scenes/regions
  where C5 addresses real post-B3 error;
- the trace source is independent of DDGI/C4/C5 and matches its CPU/shader
  ownership contract;
- short-ray intersection passes analytic planes, thin gaps, depth steps, reversed
  depth, jitter, dynamic resolution, and screen-boundary tests;
- stationary/moving camera, camera cut, moving receiver/hit/emitter, disocclusion,
  origin rebase, resize, and material revision sequences show no persistent
  ghost, edge darkening, stale light, or flash;
- miss/invalid history produces exactly zero residual and leaves canonical
  DDGI+B3 unchanged;
- off-screen or temporarily invisible emitters never remove baseline lighting;
- signed low-frequency leakage and net energy stay within the locked reference
  tolerance, with no double counting or false darkening;
- DDGI+B3+C5 produces a meaningful reference-error reduction per total
  millisecond compared directly with spending the same work/memory on B3;
- P95/P99 total frame time and memory meet the independently admitted profile;
  and
- disabled/rejected states have no source attachment, image, buffer, descriptor,
  pipeline variant in active use, pass, or synchronization cost.

## 11. Integrated frame and render-graph architecture

### 11.1 Logical frame order

The features communicate only through their declared canonical inputs:

```text
changed assets/materials/textures
        |
        +--> optional offline-cooked EXT OMM payload
        |          |
current-pose geometry --> OMM build/compact --> OMM BLAS variant --+
ordinary geometry -----------------------------------------------+--> TLAS
                                                                    |
light/emissive tree + hero list --> C4 photon tasks/trace/cache ----+
                                                                    |
previous C3 guide --> DDGI schedule/trace --> C3 training/build(next guide)
        ^                    |
        |                    +--> canonical DDGI/B3 publication
        |
previous B1 summary --> DDGI scheduler

opaque forward:
    reads canonical DDGI/B3
    optionally writes direct-diffuse/emissive C5 source
    emits/currently reconstructs B1 opaque feedback
        |
        +--> optional C4 screen resolve/composite
        +--> optional C5 trace/reconstruct/composite
        +--> transparent/particles/fog (and their B1 feedback)
                  |
                  +--> B1 compact/sort/reduce/publish for next frame
        |
        +--> TAA/post processing
```

C1 changes traversal acceleration only. It cannot change DDGI shader/material
ownership. C3 changes ray proposal/weights only. C4/C5 composite after canonical
DDGI and never become a source for the next DDGI frame.

### 11.2 Pass additions and placement

Add conditional passes/resources to
[`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs):

| Pass/resource | Position and dependency |
| --- | --- |
| `OpacityMicromapBuildPass` | AS streaming/build stage before any BLAS that attaches the object; not emitted with no pending admitted build |
| `SimpleDdgiGuidingSample` | library operation inside trace using previous published guide |
| `SimpleDdgiGuidingTrainPass` | after trace ray results are complete |
| `SimpleDdgiGuidingBuildPass` | after training reduction; publishes for a later frame |
| `GiCausticTaskPass` | after current light/emissive/hero data and current TLAS are valid |
| `GiCausticTracePass` | after task generation/TLAS; may build a future cache generation |
| `GiCausticCacheBuildPass` | after trace append; sort/retain/hash/publish |
| `GiCausticResolvePass` | after opaque depth/visibility; reads only a prior complete cache generation |
| `NearFieldTraceSource` | optional opaque forward attachment, not a standalone final-color copy |
| `SimpleDdgiNearFieldTracePass` | after source and Hi-Z/depth reads are valid |
| `SimpleDdgiNearFieldTemporalPass` | after trace |
| `SimpleDdgiNearFieldFilterPass` | zero/bounded iterations after temporal |
| `SimpleDdgiNearFieldCompositePass` | before transparency/fog/TAA/post ownership |
| `SimpleDdgiReceiverFeedbackReducePass` | after the last enabled B1 producer, normally fog/particles/transparent; publishes next-frame summary |

Opaque B1 feedback may be reconstructed by a sparse tile compute pass immediately
after forward visibility or emitted by an equivalent low-contention forward
variant. Choose through measurement, but transparent/particle/fog feedback must
finish before the final B1 reduction. A capture/cubemap view uses a distinct tile
namespace and joins the same reduction only after its producer completes.

Pass declarations are generated from *effective* modes and admitted resource
sets. A skipped callback with still-declared images/descriptors is not
zero-allocation compliance.

### 11.3 Synchronization rules

Use synchronization2 stage/access pairs that name the real producer/consumer:

- transfer writes to micromap-build reads;
- micromap-build writes to compact-copy/query and AS-build reads;
- AS-build writes to ray-query reads;
- DDGI trace writes to C3 training reads, training writes to hierarchy-build
  reads, and hierarchy writes to next-frame shader reads;
- C4 task writes to indirect/trace reads, trace writes to sort/cache-build reads,
  cache writes to later resolve reads;
- forward color/depth writes to C4/C5 compute reads;
- B1 graphics/compute append writes to sort/reduce reads, summary writes to the
  next scheduler read; and
- C4/C5 composite writes to later transparent/post-process reads.

Keep queue-family ownership and timeline values in resource objects, not implicit
global state. Async compute is enabled only when timestamp/capture evidence shows
real overlap without extending critical-path waits or increasing peak-live
memory. The correct first implementation can use one queue with narrow barriers.

Do not add device-idle waits, per-frame fence polling, CPU counter readback, or
pipeline creation to any feature path. Readback diagnostics use the existing
delayed bounded mechanism. New pipeline variants are prewarmed and cached before
the mode can become effective.

### 11.4 Descriptor and shader variants

Reserve versioned bindless slots only for persistent resources that are actually
admitted. Optional images/buffers bind canonical dummy resources only during a
transactional transition; a disabled stable state does not retain full-sized
allocations. Shader compile defines and pipeline keys include:

```text
receiver feedback ABI/mode
EXT OMM traversal variant where shader execution-mode differences require it
directional guiding ABI/resolution/estimator mode
caustic resolve record/profile
near-field source format/resolution/filter profile
```

Prewarm every effective variant, persist pipeline-cache evidence, and fail back
before frame rendering if creation fails. Validate descriptor counts against
device limits and include them in pressure diagnostics.

## 12. Implementation phases and hard gates

Do not land all features in one patch. Every phase ends with a working fallback,
tests, evidence, and no dead setting that claims more than the implementation.

### Phase 0 — Prerequisite freeze and baseline corpus

1. Complete the content-dependent plan and record the frozen contracts from
   Section 2.
2. Capture feature-off GPU timings, bytes, ray counts, candidate counts, image/
   energy results, liveness, and long-run stability.
3. Lock deterministic camera/light/content scripts and path-traced references.
4. Record device, driver, Vulkan extension/property, compiler, shader, asset,
   and settings hashes.
5. Add the C2/SER exclusion to roadmap implementation status so it cannot be
   accidentally scheduled as part of C1–C5.

Gate: all prerequisite tests pass and captures replay. No later phase is allowed
to “temporarily” fork an unfinished alpha, direction, revision, or memory ABI.

### Phase 1 — Shared modes, layouts, evidence, and zero-allocation tests

1. Add versioned requested/admitted/effective modes and settings migration.
2. Split experiment admission from real resource-active/qualified diagnostics.
3. Extend central memory planning and render-graph conditional resource support.
4. Add shared generation headers, stable fallback reasons, capture schema, and
   performance snapshots.
5. Add tests proving each disabled/rejected mode creates zero feature resources,
   descriptors, passes, dispatches, and barriers.

Gate: feature-off output/timing/memory matches baseline within noise, settings
round-trip, and failed allocation/capability transitions remain usable.

### Phase 2 — B1 CPU oracle, V2 ABI, and capture

1. Implement CPU exact gather/feedback/reduction and priority reference models.
2. Add record/summary ABI mirrors, layout compiler, generation headers, and
   double banks.
3. Add sparse opaque compute capture and subgroup/workgroup producer helpers.
4. Keep scheduler consumption disabled; compare captured V2 data against the
   CPU model and legacy sketch.
5. Qualify sampling rates and inverse-inclusion normalization.

Gate: exact IDs/weights/tiles/roles match and capture overhead/capacity are
bounded with no hot global per-fragment atomics.

### Phase 3 — B1 GPU sort/reduce and scheduler promotion

1. Land shared radix/scan primitive plus deterministic CPU/GPU tests.
2. Sort/unique/reduce into validated previous-frame summaries.
3. Add requested-page fallback pressure and quota-preserving scheduler score.
4. Add overflow invalidation, resize/reload transitions, debug views, and
   adversarial liveness captures.
5. A/B legacy, exact, and disabled modes at equal ray budgets.

Gate: Section 6.8 passes before declaring `ExactCompacted` release-qualified or
scheduling legacy-reference removal. The 2026-08-11 product-policy amendment
requests it by default without treating that request as qualification evidence.

### Phase 4 — C1 cooker proof and independent conformance

1. Pin/audit/build the NVIDIA OMM SDK CPU bridge and provenance.
2. Implement exact eligibility, source hashing, optional cooked schema, and
   transactional model output.
3. Bake the alpha conformance corpus in four-state mode.
4. Implement an independent CPU sampler/cutoff oracle and SDK debug overlays.
5. Verify corrupt/unsupported/mismatched chunks fall back to ordinary models.

Gate: every known state is provably uniform under the shipping alpha expression;
all ambiguous states are unknown, and no renderer runtime dependency on the
baker exists.

### Phase 5 — C1 EXT runtime, BLAS variants, and device A/B

1. Implement null/EXT backend boundary and complete EXT object lifecycle.
2. Extend static BLAS cache keys, sharing, retirement, and memory admission.
3. Build, compact, attach, publish, evict, and reload OMM variants
   asynchronously.
4. Add same-ray conformance captures and detailed total-time diagnostics.
5. Run RTX 3060, Ada+, and no-EXT fallback qualification.

Gate: Section 7.9 passes per device class. Leave `AutoQualified` off for any
class without durable evidence.

### Phase 6 — C3 oracle, ABI, and guide training

1. Implement equal-area mapping, hierarchy builder/sampler, PDFs, mixture/MIS,
   projection, and analytic CPU tests in double precision.
2. Add persistent/scratch layout, physical-slot generation identity, FP32
   reference, and FP16 candidate.
3. Train/build guides on GPU but continue tracing uniformly; compare guide
   statistics and sampling goodness-of-fit.
4. Add proposal epoch/direction/PDF source-cache identity and migration.
5. Update audit/tail/cardinality models before enabling guided transport.

Gate: guide construction and PDF normalization pass; ordinary uniform rendering
remains unchanged.

### Phase 7 — C3 guided transport and qualification

1. Enable mixture sampling for a bounded subset while retaining maintenance
   rays.
2. Carry exact PDFs through trace, source cache, irradiance/SH projection, and
   transport audits.
3. Add bounded direction rotation and cached re-light reuse.
4. Sweep resolution, uniform floor, maintenance fraction, learning rates, and
   epoch length with equal-work captures.
5. Validate statistical parity, visibility, convergence, and total cost.

Gate: Section 8.10 passes. Keep C3 experimental if it benefits only isolated
scenes or source-cache pressure negates the variance win.

### Phase 8 — C4 authoring, path oracle, and FP32 reference

1. Add hero material/topology authoring and validation.
2. Implement CPU double-precision emitter/specular/refraction/photon-density
   oracles.
3. Implement GPU task generation/trace and 80-byte reference records without
   temporal reuse.
4. Compare analytic differential footprints with auxiliary rays.
5. Prove energy/path ownership and remove the diffuse-relative clamp/tests.

Gate: analytic scenes converge without energy loss/double counting and ordinary
content remains zero-work.

### Phase 9 — C4 cache, resolve, compression, and qualification

1. Implement sort, deterministic bottom-K retention, exact cell table, and
   transactional multi-frame publication.
2. Add tiled screen resolve, optional temporal reconstruction, and composite.
3. Evaluate cell sizes/cascades, K, task counts, full/half resolve, record
   compression, and generation latency.
4. Validate motion/reload/origin-rebase/fallback and total quality per millisecond.

Gate: Section 9.10 passes before C4 is release-qualified. C4 remains authored
hero-only; its mode is requested by default, but ordinary untagged content owns
zero C4 work and every admission/fallback gate remains authoritative.

### Phase 10 — C5 post-B3 measurement decision

1. Run and archive the mandatory residual decomposition.
2. Compare added B3 work with the CPU/image-space C5 oracle at equal cost.
3. Publish an explicit go/no-go evidence file.

Gate: a no-go ends C5 implementation cleanly with zero resources. A go proceeds
to Phase 11; do not weaken the gate to justify already-written GPU code.

### Phase 11 — C5 bounded prototype and qualification

1. Add optional direct-source forward variant and source-ownership tests.
2. Implement bounded Hi-Z trace, hit metadata, and raw reference output.
3. Add temporal rejection/moments, two-scale frequency separation, tiled filter,
   and confidence-gated composite.
4. Sweep resolution/ray/trace/filter profiles and compare directly to B3 spend.
5. Validate motion, edges, cuts, off-screen emitters, energy, and zero allocation.

Gate: Section 10.9 passes. Otherwise retain the reference/debug implementation
behind developer builds or remove it; no preset enables it.

### Phase 12 — Integrated qualification and documentation

1. Run each feature alone and supported combinations under the total memory and
   frame budget.
2. Validate resource aliasing, barriers, descriptor pressure, pipeline prewarm,
   device loss, resize/reload, and 30–60 minute traversals.
3. Run randomized mode toggles only at safe transactional boundaries.
4. Freeze ABI/schema versions, update settings/docs/debug views, and archive
   evidence/qualification IDs.
5. Remove obsolete prototype claims; retain reference modes only where they add
   diagnostic value.

Gate: Section 20's definition of done passes with no known correctness,
liveness, memory, or synchronization waiver.

## 13. Validation and evidence matrix

### 13.1 Locked scenes and expected evidence

| Scene/sequence | B1 | C1 | C3 | C4 | C5 |
| --- | --- | --- | --- | --- | --- |
| White furnace / constant environment | normalized receiver mass and quotas | no eligible content, exact null fallback | constant integral/PDF parity, no learned bias | no hero = zero work | no post-B3 opportunity/zero residual |
| Doorway with bright exterior | exact requested/resolved owners and time-to-visible | alpha-free control | aperture variance/error improvement | off | contact residual only if B3 misses it |
| Thin alpha fence at cutoff values | alpha receiver ownership | exhaustive O/T/UO/UT and hit parity | visibility maintenance unchanged | unsupported alpha interaction rejected | screen edge/miss fallback |
| Dense static foliage cards | foliage coverage normalization | known coverage, candidate/total-time A/B | bright-sky directional learning without blocker leaks | off | source/edge stability |
| Animated/deforming foliage | producer stability | mandatory ordinary-candidate fallback | regional relearn without guide mismatch | rejected unless independently hero-qualified | motion/history rejection |
| Sparse-page scroll/teleport | fallback/requested page pressure, stale-bank rejection | BLAS variant unaffected | physical-slot generation reset | world-cache generation/origin handling | full history reset and zero residual |
| Reflection capture away from camera | guaranteed capture quota/tile namespace | traversal parity | ordinary transport ownership | optional cache remains view independent | no C5 dependence |
| Fog and particle storm | normalized bounded producer mass | no eligibility expansion | guide work cannot starve maintenance | zero unless explicit hero (normally off) | excluded receivers, no source pollution |
| One tiny HDR emissive through a slit | visual-priority scheduling | no-op control | principal variance-quality target | optional hero interaction | visible/off-screen source fallback |
| Moving sun/emissive/material edit | revision priority and liveness | exact invalidation/fallback where alpha changes | regional retraining and stored-PDF cache reuse | new coherent cache or zero, never stale | hit/source/material history rejection |
| Mirror focusing onto diffuse plane | ordinary DDGI baseline | no-op control | no false solution claim | analytic flux/footprint/reference target | off to isolate C4 |
| Glass sphere/prism and dark receiver | ordinary baseline | glass remains candidate/non-OMM | uniform reference | refraction/TIR/absorption, no diffuse clamp | off to isolate C4 |
| Moving hero caustic object | stable non-C4 field | AS lifecycle control | guide independent | generation latency, no stale trail | off to isolate C4 |
| Hands/props/crease with B3 | contribution and B3 ownership | no-op control | guide isolated | off | mandatory B3-vs-C5 equal-cost target |
| Camera pan/cut and screen exit | feedback bank/tile generation | unchanged traversal | direction epoch stability | view-independent cache + resolve history rejection | zero at invalid edge/cut, no ghost/dark rim |
| Corrupt/missing optional asset data | ordinary scheduling | safe OMM chunk rejection/plain BLAS | uniform fallback | no hero/cache fallback | no source/profile fallback |
| Mixed worst-case content | quota proof and sort capacity | build/residency pressure | cache/cardinality/variance | task/cache/resolve pressure | source/history/filter pressure |

For every sequence archive:

- deterministic input/camera script and warm-up/run length;
- settings and requested/effective feature modes;
- device/driver/API/extensions/properties;
- executable, shader, asset, ABI, and qualification hashes;
- feature-off, isolated-on, reference, and integrated captures;
- image/energy/error data and region masks;
- CPU/GPU timings and work counters;
- exact memory/descriptor/pipeline counts; and
- validation messages, fallback events, non-finite/overflow/mismatch counters.

### 13.2 Quality-neutral versus statistical comparisons

C1 is quality-neutral and should compare the same deterministic rays/hits. Known
OMM classifications must be exact; use bit equality where representations are
identical and existing distance/barycentric tolerances where hardware traversal
already permits numeric variation.

B1 changes work ordering but not final certified output. Compare equal-work
transients and final convergence separately. Its final certificate must match the
ordinary scheduler; its value is lower receiver-weighted error sooner.

C3/C4 are Monte Carlo. Use common random numbers where valid, many independent
seeds, confidence intervals, energy-integral tests, and convergence curves. A
single favorable frame or denoised screenshot is not evidence of unbiasedness.

C5 is a bounded reconstruction estimator. Compare signed residual, frequency
leakage, temporal stability, and final reference error. Report regions where
screen information is absent rather than treating canonical DDGI fallback as a
C5 hit.

### 13.3 Device matrix

Minimum qualification matrix:

| Class | Purpose |
| --- | --- |
| RTX 3060 Laptop, current project driver | primary performance/memory target; EXT OMM pre-Ada behavior; no SER assumption |
| Ada-or-newer NVIDIA with EXT OMM | hardware OMM C1 comparison and higher-throughput scaling |
| NVIDIA device/driver with OMM disabled by override | same-vendor null-backend parity |
| AMD or Intel ray-query device without EXT OMM | capability/fallback, portable B1/C3/C4/C5 behavior |
| minimum supported memory/profile device | admission/fallback and zero-allocation pressure |

Record exact PCI/device ID, driver, OMM properties/subdivision limits, memory
budget, subgroup properties, timestamp period, and whether a result is hardware,
driver, or inferred behavior. Qualification is keyed to a device-class rule, not
the marketing name alone.

### 13.4 Long-run and transition tests

Run 30–60 minute sequences with:

- continuous camera traversal across sparse pages/rings and world-origin shifts;
- periodic alpha texture/material reloads and OMM variant churn;
- lights/emissives/heroes moving, appearing, and disappearing;
- resolution/window changes and dynamic-resolution steps;
- B3 ownership allocation/eviction;
- safe-boundary feature toggles and budget pressure;
- capture start/stop and diagnostics readback; and
- simulated allocation/pipeline/build failure and device-loss teardown.

Require stable allocated/retired bytes, bounded cache/table load, no descriptor or
query leak, no generation-wrap ambiguity, no CPU managed allocation growth, no
validation errors, and no increasing P99 hitch trend.

## 14. Performance and memory policy

### 14.1 Measurement protocol

Measure optimized builds with validation/debug views/readbacks disabled after all
pipelines and normal streaming are warm. Keep a separate cold-load/build report.
Use fixed content, resolution, ray budget, clocks/power mode where practical, and
enough frames for P50/P95/P99 plus bootstrap confidence intervals. On the laptop,
record temperature, power state, and throttling so a later thermal state is not
misreported as a feature regression.

Primary resolutions are 1920x1080 and 2560x1440; add 3840x2160 stress for B1/C5
screen scaling. Measure:

- individual GPU timestamps and critical-path total;
- CPU record/submit/update time;
- bytes read/written where tooling supports it;
- occupancy/divergence/cache metrics in NVIDIA Nsight Graphics for selected
  captures;
- work-normalized time (per record, candidate, ray, photon, tile); and
- full renderer frame time, not only an isolated pass.

The feature wins only if confidence intervals exclude measurement noise and the
gain persists after construction, streaming, compaction, history, and added
bandwidth are included.

### 14.2 Initial experiment capacity envelopes

These are conservative *admission envelopes*, not blindly allocated defaults:

| Feature | Initial RTX 3060 experiment envelope | Planner behavior |
| --- | --- | --- |
| B1 | up to 32 MiB including two record banks, summaries, and non-aliased sort scratch | lower deterministic sample fraction while preserving producer minima, else use ordinary scheduling |
| C1 | resident OMM/variant bytes bounded inside a dedicated fraction of the existing AS budget; build/retirement headroom separately reserved | choose qualified reused variants, otherwise plain BLAS |
| C3 | up to 16 MiB persistent plus scratch; 8x8 estimate is roughly 6–7 MiB for two aligned banks at 15,368 probes before scratch | reduce guided capacity/resolution, then uniform fallback; never remove maintenance rays |
| C4 | up to 96 MiB for experimental record banks, table, scratch, and resolve history | reduce photons/cells/history profile or reject; no diffuse DDGI memory theft |
| C5 | up to 96 MiB at 1440p including the optional full-res source, half-res history/moments/filter scratch | select a complete lower-resolution profile or reject; never omit validation metadata |

Before values enter a preset, replace envelopes with measured tier budgets and
document the exact byte equation/output for representative 1080p/1440p/4K and
probe capacities. `VK_EXT_memory_budget` headroom and peak overlapping old/new
generations are mandatory admission inputs.

### 14.3 Promotion floors

Use these initial floors in addition to every correctness gate:

- B1: statistically significant receiver-weighted transient-error or response-
  latency improvement at equal GI work, with net non-negative P95 total GI time
  after feedback overhead and no P99 frame regression;
- C1: at least the larger of a 3% and 0.05 ms mean/P95 total-DDGI win in a
  representative alpha-heavy workload after amortization, with no representative
  scene regression beyond noise;
- C3: at least 20% receiver-weighted MSE/variance reduction at equal total GI
  time, or at least 10% total-time reduction at matched error, in the qualifying
  aperture/emissive corpus, with no material diffuse-scene regression;
- C4: at least 20% error reduction inside tagged-caustic reference masks at the
  admitted feature budget, stable outside-mask energy, and a demonstrated quality-
  per-millisecond advantage over simply raising generic ray samples;
- C5: at least 20% reduction of the measured post-B3 near-field error at equal
  total added milliseconds versus more B3 work, with no material whole-frame
  error/temporal regression.

These floors prevent promotion on noise; they do not promise that a feature will
qualify. Tighten them if the renderer's normal variance demands it. Any change to
a floor is versioned in the qualification evidence, not adjusted after seeing one
unfavorable scene.

### 14.4 CPU and allocation rules

- No per-frame LINQ, unbounded dictionary growth, transient managed arrays, or
  synchronous native calls in active paths.
- Reuse arrays, command scratch, staging ranges, query pools, and descriptor
  writes with explicit high-water telemetry.
- Asset cooking may parallelize deterministically, but runtime streaming obeys a
  bounded byte/time/build count.
- GPU indirect dispatch counts come from validated bounded counters; a corrupt
  counter cannot dispatch beyond the admitted buffer.
- Readbacks are delayed, sampled, and optional; scheduling never waits for them.
- Cache eviction scans use maintained queues/heaps or bounded samples, not full
  per-frame CPU walks.

## 15. Failure and fallback behavior

| Failure/absence | Required behavior |
| --- | --- |
| B1 bank stale, overflowed, corrupt, wrong viewport/layout generation | ignore whole bank; ordinary scheduler/quota policy; retain diagnostics |
| B1 V2 memory rejected | legacy reference only when explicitly requested, otherwise ordinary no-feedback scheduling |
| OMM extension/feature/four-state unavailable | null backend and plain candidate-tested BLAS |
| OMM chunk absent/corrupt/key mismatch/unsupported sampler | plain BLAS immediately; bounded diagnostic; no synchronous rebake |
| OMM build/compact/allocation/pipeline error | discard incomplete variant, keep plain BLAS/TLAS, retire partial objects safely |
| C3 guide missing/zero/stale/non-finite/wrong slot generation | exact uniform distribution; maintenance remains active |
| C3 PDF/direction mismatch detected | discard affected update, keep previous published probe, raise validation/fatal evidence counter |
| C3 memory pressure | shrink guided slots/resolution at a safe transition, then uniform; never shrink required maintenance below its gate |
| No C4 hero or invalid hero contract | zero C4 resource set/work; authored rejection reason |
| C4 task/cache overflow or hash insertion failure | do not publish partial cache; prior compatible cache or zero contribution |
| C4 relevant revision while new cache builds | old cache only if still semantically valid; otherwise fade/zero, never stale placement |
| C5 B3 gate/error opportunity absent | no trace-source variant/resource/pass; canonical DDGI+B3 only |
| C5 invalid hit/history/edge/miss | confidence/residual zero; canonical DDGI+B3 unchanged |
| C5 resize/allocation/pipeline error | atomically retain old compatible set until safe or switch off; never sample mismatched dimensions |
| Any non-finite optional contribution | reject optional sample/generation, increment diagnostics, preserve canonical finite lighting |
| Device loss/recreation | destroy/retire all optional objects, re-query capabilities, re-admit from requested modes, start histories/generations clean |

Fallback transitions are tested outputs, not exception-only code. A fallback
must preserve frame rendering and produce a stable reason in diagnostics. Repeated
failure is rate-limited and cannot retry/reallocate every frame.

## 16. Test implementation

### 16.1 Unit and property tests

Add focused suites rather than extending one monolithic experiment test:

```text
SimpleDdgiReceiverFeedbackLayoutTests
SimpleDdgiReceiverFeedbackReferenceTests
SimpleDdgiReceiverFeedbackSamplingTests
SimpleDdgiReceiverFeedbackSchedulerPolicyTests

OpacityMicromapEligibilityTests
OpacityMicromapContentKeyTests
OpacityMicromapCookedPayloadTests
OpacityMicromapBuildPlanTests
OpacityMicromapBlasVariantTests

SimpleDdgiDirectionalGuidingDistributionTests
SimpleDdgiDirectionalGuidingEstimatorTests
SimpleDdgiDirectionSampleIdentityTests
SimpleDdgiGuidingMemoryPlanTests

GiCausticPathEstimatorTests
GiCausticPhotonDifferentialTests
GiCausticCacheRetentionTests
GiCausticCacheLayoutTests
GiCausticOwnershipTests

SimpleDdgiNearFieldTraceTests
SimpleDdgiNearFieldHistoryTests
SimpleDdgiNearFieldFrequencyTests
SimpleDdgiNearFieldLayoutTests
```

Required property/fuzz coverage includes:

- checked arithmetic at zero, one, normal, limit, and overflow capacities;
- all enum/flag/ABI sentinels and settings migration/round-trip;
- B1 inclusion probabilities, duplicate order, hash collisions, capacity
  overflow, quota starvation, and generation wrap behavior;
- C1 random alpha blocks around cutoff, UV/address boundaries, constant/varying
  vertex alpha, malformed chunks/counts/offsets, and content-key bit changes;
- C3 probability normalization, sample-frequency goodness, finite inverse PDFs,
  constant-function integrals, seam/pole bins, all-zero/single-bin/HDR guides,
  and MIS technique-count edges;
- C4 every light/specular branch, TIR, absorption, bottom-K permutation
  invariance/unbiased selection, full-key hash collision, and normalized kernel;
- C5 projection/depth conventions, ray-step limits, miss/edge confidence,
  reprojection identity changes, band-pass constant/gradient response, and
  complete-profile memory rejection; and
- feature-off plans returning exact zero counts/bytes/resources.

Use deterministic seeds and a statistically justified tolerance/sample count.
Tests must not become flaky because they run a random trial once.

### 16.2 CPU/GLSL ABI mirror tests

Extend shader mirror verification for every struct, push constant, descriptor,
flag, enum, counter, alignment, and stride. Generate or parse canonical constants
where practical. At minimum assert:

- C# `Unsafe.SizeOf` and `Marshal.OffsetOf` versus GLSL offsets;
- storage alignment and array stride;
- record/header ABI versions and invalid sentinels;
- direction mapping/PDF constants and uniform-floor bits;
- page/probe/tile namespaces and packed generation ranges;
- caustic path tags and source-ownership bits; and
- C5 format/profile and history rejection bits.

`ShaderBuildTests` compile disabled, reference, and active variants, including
EXT OMM capable/disabled configurations. Run shader reflection checks so an
optimized-out or shifted optional binding cannot silently mismatch C#.

### 16.3 GPU/Vulkan integration tests

Extend the existing conformance harnesses with optional hardware tests:

- B1 emit/sort/unique/reduce a tiny known record set and compare a readback to
  the CPU model;
- build an EXT four-state OMM, attach it to a tiny BLAS, trace every known/
  unknown microtriangle, compact/evict/rebuild it, and compare the plain BLAS;
- sample a known C3 distribution on GPU, read frequency/PDF data, and integrate
  constant/spherical-lobe functions;
- trace analytic C4 mirror/prism paths, build colliding cells, resolve a plane,
  and compare FP32 oracle energy; and
- trace C5 against analytic depth planes/steps with controlled motion/history.

Tests skip with a precise capability reason where hardware is absent; the null
backend/fallback assertions still run. Validation layers, synchronization
validation, GPU-assisted validation where practical, robust buffer access, and
device-address lifetime checks run in dedicated jobs/captures.

### 16.4 Rendered regression and evidence tests

Keep small deterministic smoke images in ordinary CI and high-sample HDR/path-
traced evidence in the existing material/GI evidence workflow. Tests compare
linear HDR data before exposure/tonemap. Lock region masks and metrics; never
approve a large global tolerance that hides localized alpha, caustic, or contact
failures.

Each qualification artifact is authenticated like existing GI/material evidence
and records its feature ABI/qualification ID. Stale evidence fails closed after a
shader, content, driver-class, or algorithm revision.

## 17. Recommended reviewable slices

Keep patches independently testable and revertible:

1. settings enums/migration plus requested/admitted/effective diagnostics;
2. optional resource-plan categories and zero-allocation tests;
3. B1 V2 CPU model and record/summary ABI;
4. B1 sparse opaque producer and common producer aggregation helper;
5. B1 GPU radix/scan primitive with standalone tests;
6. B1 unique/reduce publication, then scheduler/quota consumption;
7. NVIDIA OMM SDK provenance/native bridge and asset-tool-only smoke test;
8. OMM exact eligibility/content key/cooked chunk and corrupt-data tests;
9. null/EXT micromap backend and build-size/resource lifecycle;
10. static BLAS variant attachment/compaction/retirement;
11. OMM conformance harness/debug/total-time evidence;
12. C3 equal-area CPU hierarchy/PDF/MIS oracle;
13. C3 memory ABI and GPU train/build without guided tracing;
14. direction sample identity/PDF/source-cache migration;
15. C3 mixture tracing and FP32 projection;
16. C3 maintenance/audit/tail integration, then qualification sweep;
17. C4 hero authoring/topology validation and CPU path oracle;
18. C4 GPU task/trace with 80-byte reference records;
19. C4 differential footprint and analytic energy evidence;
20. C4 sort/bottom-K/cell cache publication;
21. C4 tiled resolve/history/composite and compression sweep;
22. C5 post-B3 go/no-go evidence only;
23. optional C5 direct-source pipeline variant and ownership tests;
24. C5 Hi-Z trace/reference output;
25. C5 temporal validation and frequency-separated composite;
26. integrated render-graph/barrier/aliasing/device-transition validation; and
27. final performance matrix, docs, evidence, and preset decisions.

Do not combine estimator math, compressed storage, temporal filtering, and
preset promotion in one slice. Reference representations remain available until
the compressed/optimized successor has passed the same evidence.

## 18. Primary code touch map

Names under “new” are proposed and may be adjusted to the repository's final
naming convention, but their ownership boundaries should remain.

### 18.1 Shared settings, planning, graph, and diagnostics

- [`RenderSettings.cs`](../Njulf.Rendering/Data/RenderSettings.cs): modes,
  budgets, schema migration, requested/effective accessors
- [`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs): admission,
  transactional transitions, scene data, diagnostics assembly
- [`SimpleDdgiContentMemoryPlan.cs`](../Njulf.Rendering/Resources/SimpleDdgiContentMemoryPlan.cs)
  and [`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs):
  authoritative optional byte planning
- [`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs):
  conditional passes/resources/lifetimes
- [`RenderTargetManager.cs`](../Njulf.Rendering/Resources/RenderTargetManager.cs):
  optional source/history image sets
- [`GiRoadmapExperimentDiagnostics.cs`](../Njulf.Rendering/Resources/GiRoadmapExperimentDiagnostics.cs),
  [`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs),
  [`DdgiRuntimeSnapshot.cs`](../Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs), and
  [`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs):
  real state/evidence
- [`ShaderLibrary.cs`](../Njulf.Shaders/ShaderLibrary.cs) and pipeline factories:
  variants and prewarm

### 18.2 B1

Existing files to refactor:

- [`SimpleDdgiGpuSchedulerLayout.cs`](../Njulf.Rendering/Resources/SimpleDdgiGpuSchedulerLayout.cs)
- [`SimpleDdgiGpuScheduler.cs`](../Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs)
- [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl)
- [`ddgi_simple_schedule_shared.glsl`](../Njulf.Shaders/ddgi_simple_schedule_shared.glsl)
- [`ddgi_simple_schedule_classify.comp`](../Njulf.Shaders/ddgi_simple_schedule_classify.comp)
- forward, transparent, weighted-OIT, foliage, particle, fog, receiver-cache, and
  reflection-capture receiver shaders/passes

Proposed new files:

```text
Njulf.Rendering/Resources/SimpleDdgiReceiverFeedbackLayout.cs
Njulf.Rendering/Resources/SimpleDdgiReceiverFeedbackReference.cs
Njulf.Rendering/Pipeline/SimpleDdgiReceiverFeedbackPass.cs
Njulf.Shaders/ddgi_receiver_feedback_abi.glsl
Njulf.Shaders/ddgi_receiver_feedback_reset.comp
Njulf.Shaders/ddgi_receiver_feedback_opaque.comp
Njulf.Shaders/gpu_radix_histogram.comp
Njulf.Shaders/gpu_radix_prefix.comp
Njulf.Shaders/gpu_radix_scatter.comp
Njulf.Shaders/ddgi_receiver_feedback_unique.comp
Njulf.Shaders/ddgi_receiver_feedback_reduce.comp
```

Use a shared radix primitive only if its contract is generic and tested; otherwise
prefix the files/classes with DDGI to avoid claiming a reusable subsystem.

### 18.3 C1

Existing files to extend:

- [`VulkanContext.cs`](../Njulf.Rendering/Core/VulkanContext.cs) and
  [`GiHardwareResearchCapabilities.cs`](../Njulf.Rendering/Core/GiHardwareResearchCapabilities.cs)
- [`GiHardwareResearchExperiments.cs`](../Njulf.Rendering/Resources/GiHardwareResearchExperiments.cs)
- [`AccelerationStructureManager.cs`](../Njulf.Rendering/Resources/AccelerationStructureManager.cs)
- [`ModelAssetCooker.cs`](../Njulf.Assets/Cooked/ModelAssetCooker.cs), cooked
  format/payload/authentication/migration files, and asset upload contracts
- [`MaterialAlphaCoverageContract.cs`](../Njulf.Rendering/Data/MaterialAlphaCoverageContract.cs),
  [`DdgiMaterialTextureLodPolicy.cs`](../Njulf.Rendering/Data/DdgiMaterialTextureLodPolicy.cs),
  [`material_alpha.glsl`](../Njulf.Shaders/material_alpha.glsl), and
  [`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl)
- [`Program.cs`](../Njulf.AssetTool/Program.cs), AssetTool project/provenance, and
  [`THIRD-PARTY-NOTICES.txt`](../Njulf.AssetTool/THIRD-PARTY-NOTICES.txt)

Proposed new files:

```text
Njulf.Assets/Cooked/OpacityMicromapCookedPayload.cs
Njulf.AssetTool/OpacityMicromapCookCommand.cs
native/Njulf.OmmCookerBridge/                 // pinned build, C ABI, provenance
Njulf.Rendering/Resources/OpacityMicromapManager.cs
Njulf.Rendering/Resources/IOpacityMicromapBackend.cs
Njulf.Rendering/Resources/ExtOpacityMicromapBackend.cs
Njulf.Rendering/Resources/OpacityMicromapContentKey.cs
Njulf.Rendering/Pipeline/OpacityMicromapBuildPass.cs
```

Do not add `KhrOpacityMicromapBackend.cs` beyond an enum/reserved interface test
until real Silk/toolchain support is selected.

### 18.4 C3

Existing files to refactor:

- [`SimpleDdgiDirectionalGuidingExperiment.cs`](../Njulf.Rendering/Resources/SimpleDdgiDirectionalGuidingExperiment.cs)
- [`SimpleDdgiDirectionCodebook.cs`](../Njulf.Rendering/Data/SimpleDdgiDirectionCodebook.cs)
- [`SimpleDdgiGpuScheduler.cs`](../Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs)
- [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [`SimpleDdgiPasses.cs`](../Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs)
- trace, blend, directional-radiance, source-cache, transport, and audit shaders

Proposed new files:

```text
Njulf.Rendering/Data/SimpleDdgiDirectionSampleIdentity.cs
Njulf.Rendering/Resources/SimpleDdgiGuidingLayout.cs
Njulf.Rendering/Resources/SimpleDdgiGuidingReference.cs
Njulf.Rendering/Resources/SimpleDdgiGuidingManager.cs
Njulf.Rendering/Pipeline/SimpleDdgiGuidingPasses.cs
Njulf.Shaders/ddgi_directional_guiding.glsl
Njulf.Shaders/ddgi_guiding_train.comp
Njulf.Shaders/ddgi_guiding_build.comp
Njulf.Shaders/ddgi_guiding_validate.comp
```

### 18.5 C4

Existing files to refactor:

- [`GiNearFieldResearchExperiments.cs`](../Njulf.Rendering/Resources/GiNearFieldResearchExperiments.cs):
  remove diffuse-relative clamp and replace admission-only plan
- material authoring/transport contracts and editor material panel
- [`LightManager.cs`](../Njulf.Rendering/Resources/LightManager.cs), emissive
  sampler/tree, AS manager, scene mutation revisions, and forward composition

Proposed new files:

```text
Njulf.Rendering/Data/GiCausticMaterialContract.cs
Njulf.Assets/Validation/GiCausticHeroValidator.cs
Njulf.Rendering/Resources/GiCausticLayout.cs
Njulf.Rendering/Resources/GiCausticReference.cs
Njulf.Rendering/Resources/GiCausticCacheManager.cs
Njulf.Rendering/Pipeline/GiCausticPasses.cs
Njulf.Shaders/gi_caustic_abi.glsl
Njulf.Shaders/gi_caustic_tasks.comp
Njulf.Shaders/gi_caustic_trace.comp
Njulf.Shaders/gi_caustic_cache_build.comp
Njulf.Shaders/gi_caustic_resolve.comp
Njulf.Shaders/gi_caustic_temporal.comp       // only if measured necessary
```

### 18.6 C5

Existing files to extend:

- [`GiNearFieldResearchExperiments.cs`](../Njulf.Rendering/Resources/GiNearFieldResearchExperiments.cs)
- [`HiZDepthPyramid.cs`](../Njulf.Rendering/Resources/HiZDepthPyramid.cs) and
  [`HiZBuildPass.cs`](../Njulf.Rendering/Pipeline/HiZBuildPass.cs)
- [`ForwardPlusPass.cs`](../Njulf.Rendering/Pipeline/ForwardPlusPass.cs), forward
  and foliage shaders, motion vectors, render targets, and main scene composite

Proposed new files:

```text
Njulf.Rendering/Resources/SimpleDdgiNearFieldResidualLayout.cs
Njulf.Rendering/Resources/SimpleDdgiNearFieldResidualReference.cs
Njulf.Rendering/Resources/SimpleDdgiNearFieldResidualManager.cs
Njulf.Rendering/Pipeline/SimpleDdgiNearFieldResidualPasses.cs
Njulf.Shaders/near_field_residual_trace.comp
Njulf.Shaders/near_field_residual_temporal.comp
Njulf.Shaders/near_field_residual_filter.comp
Njulf.Shaders/near_field_residual_composite.comp
```

### 18.7 Tests and documentation

- split/replace relevant parts of
  [`GiRoadmapExperimentTests.cs`](../Njulf.Tests/GiRoadmapExperimentTests.cs)
- existing scheduler/layout/shader/build/alpha/material/render-graph/Vulkan
  harness suites plus the Section 16 suites
- [`RendererSettingsReference.md`](../RendererSettingsReference.md)
- debug UI/tooltips and sample diagnostics reporter
- implementation status/evidence documents written after each phase, not
  pre-marked complete in this plan

## 19. Risks and mitigations

| Risk | Consequence | Required mitigation |
| --- | --- | --- |
| B1 sort costs more than redirected work saves | frame-time regression despite better priority | sparse producer sampling, subgroup/workgroup aggregation, indirect bounded radix, scratch aliasing, and net total-time gate |
| B1 sampling favors one producer/camera | starvation or wrong priority | known inclusion probability, per-producer reservations, normalized physical contribution, and hard non-feedback quotas |
| B1 fallback IDs are collapsed | missing fine pages never finish or base pages receive wrong pressure | preserve requested and resolved IDs and reduce their purposes separately |
| B1 partial overflow biases visible probes | scheduler chases an arbitrary prefix | invalidate the whole generation and use ordinary priors |
| OMM baker and shader disagree at cutoff/filter/LOD | holes or false blockers | exact frozen alpha expression, same compressed values, four-state unknown boundary, independent oracle, same-ray parity |
| Static mesh is instanced with different alpha materials | incorrect shared OMM BLAS | material-aware bounded BLAS variants and always-available plain variant |
| OMM saves candidates but pre-Ada emulation/build/memory loses overall | negative RTX 3060 value | end-to-end per-class gate; qualify Ada and RTX 3060 independently |
| Native OMM SDK complicates builds/distribution | fragile asset pipeline | pinned source/binary provenance, narrow C ABI, asset-tool-only load, deterministic build, optional chunk/fallback |
| EXT becomes legacy while KHR evolves | future migration cost | backend-specific payload/object layer plus shared semantic key; no fake/manual KHR implementation |
| Guide learns its own noise/firefly | unstable direction collapse | prior-only robust training, diagnostics, uniform floor, maintenance set, independent uniform audit |
| Quantized C3 hierarchy PDF differs from generation | biased estimator | generate and evaluate PDF from the same stored quantized branch weights and store generation-time FP32 PDF |
| Guided rays undersample dark blockers | leaks/relocation failures | uniform maintenance exclusively owns initial visibility/relocation |
| Direction changes destroy cached re-light benefit | C3 loses more time than it saves | bounded proposal epochs, partial slot rotation, explicit source identity/PDF/cardinality |
| Variable-PDF weights break transport certification | false convergence or energy bias | refactor projection/audit/tail math first; independent uniform certificate |
| Photon targeting omits contributing paths | biased/missing caustics | canonical emission mixture retains support, selected caster must match first tagged event, exact joint PDFs |
| Photon cache overflows dense focus | biased clipped caustic | task-sized append, deterministic bottom-K with inclusion correction, invalid whole generation on table failure |
| Caustic clamp is tied to diffuse | dark receivers lose physically valid caustic | delete diffuse-relative clamp; flux/BRDF/kernel energy contract and analytic tests |
| Hero glass contract is incomplete | wrong refraction/medium stack | strict closed-volume validation and limited supported depth/media; reject unsupported content |
| C4 temporal reuse leaves moving light | visible stale caustic trail | revision-keyed coherent publication and strict history rejection/zero fallback |
| C5 samples final scene color | positive feedback/double counting | dedicated shader-enforced direct-diffuse/emissive attachment and bit-independence test |
| C5 screen miss appears dark | edge/off-screen artifact | residual/confidence zero on miss; DDGI/B3 never multiplied by confidence |
| C5 high-pass leaks low-frequency energy | double counting or broad darkening | matched world-space kernels, mean preservation, signed-energy metrics, direct path-reference gate |
| C5 history trusts only receiver motion | moving hit/emitter ghost | validate both receiver and hit object/depth/material/revision |
| Optional images remain allocated while disabled | hidden VRAM cost | effective-mode graph variants and exact zero-resource tests |
| Too many combinations explode pipelines/testing | hitches and unqualified states | independently keyed modes, isolation by default, only supported combination matrix, prewarm before activation |
| Async compute adds waits/peak live bytes | worse critical path | single-queue correct baseline; enable overlap only from timeline/peak-memory evidence |
| Evidence becomes stale after ABI/driver change | false promotion | qualification ID hashes and fail-closed invalidation |

## 20. Definition of done

### 20.1 Shared completion

- The prerequisite content-dependent plan is complete and all frozen contract
  versions are recorded.
- C2/SER is explicitly excluded and has no implemented runtime work under this
  plan.
- Requested/supported/admitted/effective/qualified states are distinct,
  persisted correctly, and visible in diagnostics.
- Every feature has checked central memory planning, exact allocated/peak/retired
  telemetry, safe transactional transition, pipeline prewarm, and zero-allocation
  disabled/rejected tests.
- Every new C#/GLSL/cooked ABI is versioned, mirrored, bounds-checked, and covered
  by deterministic capture/replay.
- Render-graph dependencies, barriers, queue ownership, descriptor pressure,
  resize/reload, failure, and device loss pass validation.
- Feature-off canonical DDGI images, certificates, performance, and memory match
  the post-prerequisite baseline within the locked tolerances/noise.

### 20.2 B1 completion

- V2 feedback preserves exact requested/resolved probe, page/generation, tile,
  weight, role, and inclusion probability.
- Hot receiver paths aggregate locally and use bounded append reservations;
  exact GPU sort/unique/reduce matches the CPU model.
- Previous-frame summaries affect priority only and all off-screen/age/new-
  geometry/reflection/propagation/refinement quotas make bounded progress.
- Stale/overflow/resize/missing feedback falls back to ordinary scheduling.
- Equal-work transient receiver error/latency improves with net acceptable
  total-time overhead, and Section 6.8/14 gates pass.

### 20.3 C1 completion

- The pinned NVIDIA OMM CPU baker produces optional deterministic four-state
  cooked payloads from the exact DDGI alpha inputs through an audited native
  bridge.
- EXT/null backend, build-size query, build, compaction, BLAS variant attachment,
  publication, sharing, eviction, reload, and teardown are real and validated.
- Unknown boundaries execute the unchanged candidate shader; two-state,
  dynamic/procedural/thin/unsupported content always falls back.
- OMM-on/off same-ray and image/energy conformance passes.
- RTX 3060, Ada+, and no-EXT results are archived; `AutoQualified` is enabled only
  for device classes with a measured amortized total-GI win.
- No KHR backend or SER work is incorrectly claimed as part of C1.

### 20.4 C3 completion

- Equal-area hierarchy sampling, continuous PDFs, mixture/MIS, training,
  projection, direction identity, source-cache reuse, maintenance, and audit math
  pass CPU/GPU statistical tests.
- Every radiometric variable-PDF sample carries its generation-time PDF; invalid
  identity cannot publish a probe.
- Uniform maintenance preserves visibility/relocation/classification and cannot
  be starved by guiding.
- FP16/selected hierarchy resolution, proposal epoch, rotation, and memory
  choices are backed by FP32/equal-work evidence.
- Guided results converge to the uniform reference and meet the Section 8.10/14
  quality-per-time gate; otherwise mode remains off/unqualified.

### 20.5 C4 completion

- Hero authoring rejects unsupported volumes/materials before runtime work.
- Light/emitter/caster proposals, specular/refraction throughput, differentials,
  photon flux, bottom-K retention, cache lookup, and kernel resolve pass analytic
  and path-traced energy tests.
- The world cache publishes coherently and survives/misses safely across motion,
  reload, origin changes, pressure, and failure.
- Caustic energy is independent of local diffuse brightness and never enters any
  DDGI transport/source/publication resource.
- Screen resolve/history/composite has explicit ownership and no stale trails.
- Tagged-scene quality per millisecond passes Section 9.10/14; ordinary/no-hero
  scenes have zero C4 work.

### 20.6 C5 completion

One of two outcomes is valid:

1. the archived post-B3 gate finds no worthwhile screen-observable residual, C5
   remains off with zero resources, and the evidence says why; or
2. the bounded prototype is implemented and passes every gate below.

For outcome 2:

- the optional source contains direct diffuse and emissive only and is provably
  independent of DDGI/C4/C5/final scene color;
- bounded Hi-Z trace, exact PDF, hit metadata, temporal rejection, frequency
  separation, confidence, and composite work at the admitted profile;
- miss/invalid/edge/history failure leaves DDGI+B3 exactly unchanged;
- motion/cuts/resize/off-screen source sequences have no persistent ghost, false
  darkening, visible-source dependency, or double counting;
- equal-cost comparison beats more B3 work on measured post-B3 error and meets
  Section 10.9/14; and
- C5 remains optional and outside the DDGI-only correctness certificate.

### 20.7 Final handoff

- All unit, shader, asset, GPU integration, rendered evidence, long-run, and
  target-device tests pass.
- Performance and evidence artifacts are committed/referenced with exact hashes;
  no result is inferred from extension presence or a helper unit test.
- Settings reference, debug UI, diagnostics schema, authoring guidance, third-
  party notices, fallback documentation, and implementation status are current.
- No placeholder TODO, fake qualification boolean, silent clamp, unbounded loop,
  per-frame managed allocation, synchronous build, device-idle wait, or known
  stale-generation path remains.

## 21. Primary technical references

### DDGI and sampling

- NVIDIA Research, [Scaling Probe-Based Real-Time Dynamic Global Illumination for Production](https://research.nvidia.com/publication/2020-09_scaling-probe-based-real-time-dynamic-global-illumination-production-technical): production probe states, cascades, update pruning, and glossy-reuse context.
- Müller, Gross, and Novák, [Practical Path Guiding for Efficient Light-Transport Simulation](https://jannovak.info/publications/PathGuide/index.html): spatio-directional learning, iterative guiding, and unbiased sampling foundation.
- Veach, [Robust Monte Carlo Methods for Light Transport Simulation](https://graphics.stanford.edu/papers/veach_thesis/): exact PDFs and multiple-importance-sampling balance heuristic.

### Opacity micromaps

- Khronos, [`VK_EXT_opacity_micromap` proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_opacity_micromap.html): EXT objects, build, attachment, formats, and traversal semantics.
- Khronos, [`VK_KHR_opacity_micromap` proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_opacity_micromap.html): promoted API differences and KHR ray-query requirements; basis for keeping payload/backends separate.
- Khronos Vulkan specification, [Opacity micromaps chapter](https://docs.vulkan.org/spec/latest/chapters/VK_KHR_opacity_micromap/micromaps.html): normative promoted behavior and constraints.
- NVIDIA RTX, [Opacity Micro-Map SDK](https://github.com/NVIDIA-RTX/OMM) and [integration guide](https://github.com/NVIDIA-RTX/OMM/blob/main/docs/integration_guide.md): four-state exact workflow, CPU/GPU bakers, reuse, subdivision, contour classification, spatial ordering, coverage metrics, and pre-Ada behavior.
- Silk.NET, [`Silk.NET.Vulkan.Extensions.EXT` 2.23.0](https://www.nuget.org/packages/Silk.NET.Vulkan.Extensions.EXT/2.23.0): binding version used by the current EXT backend.

### Caustics

- NVIDIA Developer, [Generating Ray-Traced Caustic Effects in Unreal Engine 4, Part 1](https://developer.nvidia.com/blog/generating-ray-traced-caustic-effects-in-unreal-engine-4-part-1): light-space photon tasks, photon records, adaptive anisotropic footprints, and screen resolve inspiration.
- NVIDIA Research, [Toward Practical Real-Time Photon Mapping: Efficient GPU Density Estimation](https://research.nvidia.com/publication/2013-03_toward-practical-real-time-photon-mapping-efficient-gpu-density-estimation): GPU photon-density estimation background.
- Eurographics, [Efficient Caustic Rendering with Lightweight Photon Mapping](https://diglib.eg.org/items/f9767b98-cb07-4e86-a27f-66eaed48f6dd): focused photon emission and bounded caustic tradeoffs.

### Screen-space residual and temporal reconstruction

- NVIDIA Research, [Spatiotemporal Variance-Guided Filtering](https://research.nvidia.com/labs/rtr/publication/schied2017spatiotemporal/): moments, temporal validation, variance, and edge-aware reconstruction.
- NVIDIA Research, [Dynamic Diffuse Global Illumination Resampling](https://research.nvidia.com/publication/2021-12_dynamic-diffuse-global-illumination-resampling): hybrid dynamic diffuse-GI sampling/reuse and validation context.
- NVIDIA Research, [ReSTIR GI: Path Resampling for Real-Time Path Tracing](https://research.nvidia.com/publication/2021-06_restir-gi-path-resampling-real-time-path-tracing): reuse/reference context; this plan deliberately does not turn C5 into a full ReSTIR-GI system.

These references motivate algorithms and contracts; the repository's frozen
material, alpha, probe, publication, memory, and ownership ABIs remain
authoritative for implementation.
