# Sparse, Surface-Aware Simple-DDGI Probe Paging Implementation Plan

- Status: proposed implementation plan
- Date: 2026-08-03
- Target: Simple-DDGI, structured gather, ray-query transport V2
- Primary target profile: `DdgiHigh`, three camera-relative rings
- Required predecessor: [GPU-Resident Simple-DDGI Scheduling Implementation Plan](SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md)

## 1. Outcome

Keep the existing regular virtual probe lattices, volume ordering, receiver
selection, and toroidal scroll mapping, but stop using the virtual probe index as
the address of every large probe payload.

The target design has two address spaces:

- a dense **virtual probe address** for topology, world placement, toroidal
  scrolling, dirty state, scheduling, diagnostics, and deterministic ray
  rotation;
- a bounded **physical payload address** for published irradiance, visibility
  moments, the private Jacobi irradiance target, the V2 source-ray cache, and the
  optional sampled-image mirror.

The near ring is divided into fixed `2 x 2 x 2` virtual pages. A GPU-resident
page table maps only demanded pages to a smaller physical page pool. Visible
opaque receivers generate proactive demand from the current depth buffer;
rate-limited receiver misses cover transparent and other non-depth consumers;
resident pages remain eligible for a measured retention interval. A missing
fine page contributes no fine support and enters the already-established
coarser-ring recovery path. Mid and far rings remain dense and provide the
mandatory fallback field.

This is an application-managed compact pool, not Vulkan sparse binding. The
actual Vulkan buffers and sampled-image arrays are allocated only for the
resolved physical probe capacity, so the memory saving is real and stable while
descriptors, synchronization, and failure behavior stay under renderer control.

The first production milestone keeps the current `28 x 14 x 28`, `18 x 10 x
18`, and `12 x 8 x 12` virtual topology unchanged. Only after that mode passes
quality and performance gates may part of the measured saving fund a denser
near lattice.

## 2. Baseline and opportunity

The identity-locked `final-v13` baseline is recorded in
[NsightDdgiPerformancePassImplementationStatus-20260802.md](Complete/NsightDdgiPerformancePassImplementationStatus-20260802.md).
The local Run A report records 15,368 probes, 2,252 classified inactive probes,
and 177,519,440 required live Simple-DDGI bytes. Thus the supplied 14.6% value
is exact to one decimal place: `2,252 / 15,368 = 14.65%`.

The current memory plan reconciles as follows for the 128-ray High profile:

| Resource | Current bytes | Addressing today |
|---|---:|---|
| Params and 16 volume records | 1,744 | fixed |
| Published irradiance SSBO | 7,868,416 | per virtual probe |
| Visibility SSBO | 31,473,664 | per virtual probe |
| Private transport irradiance SSBO | 7,868,416 | per virtual probe |
| V2 source-ray cache | 70,815,744 | per virtual probe x 128 rays |
| Probe state | 491,776 | per virtual probe |
| Relocation/classification | 737,664 | per virtual probe |
| Two readback buffers | 1,376,768 | virtual state plus bounded classification |
| Update queue | 131,072 | 4,096 requests |
| Ray-result scratch | 16,777,216 | 4,096 requests x 128 rays |
| Sampled atlas mirror | 39,976,960 | 15,616 provisioned layers |
| **Total** | **177,519,440** | |

Under the proposed split, the large payload attached to one physical probe is
10,240 bytes in this profile:

```text
published irradiance       512 B
visibility moments       2,048 B
private Jacobi target      512 B
128-ray V2 source cache  4,608 B
sampled-image mirror     2,560 B
                         -------
                         10,240 B
```

Probe state and relocation/classification remain virtually indexed. This costs
only 80 bytes per logical probe and avoids forcing the scheduler and every
diagnostic through a second indirection.

If individual inactive probes could be removed with no page granularity, the
current inactive population represents 23,060,480 payload bytes, or about 22.0
MiB. That is a useful lower-bound signal, not a safe capacity target:

- classification says a probe is enclosed or unusable, not whether a page was
  recently required by a receiver;
- a page containing one useful probe retains all eight physical slots;
- visible-surface demand may identify empty air that has never been classified
  inactive;
- camera motion, transparent receivers, and retention increase the working set.

Capacity must therefore be selected from shadow-mode working-set traces, not by
setting the pool to 85.4% of the current probe count.

Illustrative current-plan totals, before adding the small residency arena, are:

| Near-ring physical fraction | Total physical probes including dense mid/far | Estimated live bytes | Gross saving |
|---:|---:|---:|---:|
| 50% | 9,880 | 115.35 MiB | 53.95 MiB |
| 60% | 10,984 | 125.94 MiB | 43.36 MiB |
| 70% | 12,080 | 137.09 MiB | 32.21 MiB |
| 75% | 12,624 | 142.32 MiB | 26.97 MiB |
| 80% | 13,176 | 147.62 MiB | 21.68 MiB |

These are sizing examples only. The post-GPU-scheduler baseline will remove
probe-count-scaled shipping readback and add its arena, so final acceptance must
compare exact same-binary dense and sparse memory plans.

## 3. Goals

1. Preserve the exact virtual volume topology and receiver-space selection
   contract.
2. Preserve toroidal scrolling without copying preserved probe payloads.
3. Allocate large near-ring payloads only for current or recently relevant
   surface pages.
4. Make a missing, initializing, stale, or remapped page fail closed before any
   payload read.
5. Resolve missing fine support through the existing mid/far gather hierarchy.
6. Keep physical capacity fixed during ordinary frames; residency changes must
   not create Vulkan allocations, update descriptors, wait for the device, or
   read a page table back to the CPU.
7. Integrate page admission with GPU-resident scheduling, source generations,
   convergence, publication, dirty regions, relocation, and atmosphere cohorts.
8. Measure actual allocated bytes, page-demand accuracy, churn, gather overhead,
   update latency, and image quality before enabling the mode by default.
9. Make a later near-field density increase compete under the same memory, ray,
   timing, and quality budgets.

## 4. Non-goals for the first production slice

- Do not use `VK_KHR_sparse_binding`, sparse buffers, sparse images, or per-frame
  `vkQueueBindSparse` work.
- Do not sparsify the mid or far fallback rings.
- Do not sparsify authored volumes. An explicit authored residency policy can be
  considered after the ring implementation is accepted.
- Do not change irradiance or visibility formats, octahedral resolution,
  interpolation, visibility moments, relocation math, bounce count, or ray
  directions in the topology-identical milestone.
- Do not increase the virtual grid, update budget, or primary-ray budget in the
  same change that first makes sparse rendering authoritative.
- Do not use classified-inactive state as the only surface-demand source.
- Do not make actual resident count masquerade as allocated memory. Allocated
  physical capacity, not current occupancy, is the residency saving.
- Do not build a CPU page scheduler or read the GPU mapping back before command
  recording.
- Do not let a debug view change residency unless an explicit development-only
  pin/request command is enabled.

## 5. Dependencies and integration boundary

Sparse authority depends on the `GpuResident` mode described in
[SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md](SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md):

- the GPU owns per-probe eligibility, update-queue emission, indirect dispatch,
  and lifecycle commit;
- shipping feedback is fixed-size and frame-late;
- the full Simple-DDGI segment is cross-frame serialized;
- stale queue work is rejected by generations;
- stable frames perform no per-probe CPU enumeration or upload.

Shadow demand, pure layout code, the reference allocator, and diagnostics can
land before that migration completes. Authoritative sparse mapping must not. If
the projects overlap, update the scheduler design as follows rather than adding
a second scheduler:

- scheduler state remains indexed by virtual probe address;
- classification scans or derives work only for dense probes and resident
  sparse pages;
- emitted requests carry both virtual and physical payload addresses;
- page reconciliation runs before scheduler classification;
- residency and scheduler arenas share generation and transaction stamps but
  retain separate layout calculators and ownership classes.

The atmosphere/cohort rules in
[SunSkyDdgiAsyncReflectionRemainingImplementationPlan-20260803.md](SunSkyDdgiAsyncReflectionRemainingImplementationPlan-20260803.md)
also remain authoritative. Eviction is an explicit generation-stamped retirement
event; it must never be inferred from frame age or a zero stale count.

## 6. Required invariants

### 6.1 Addressing

- A virtual probe address identifies one toroidal lattice slot and remains stable
  for every preserved world cell across a cell-aligned scroll.
- At most one physical page owns a virtual page, and at most one virtual page
  owns a physical page.
- A valid sparse table entry and its reverse physical-page metadata agree on
  virtual page, physical page, residency-resource generation, and mapping
  generation.
- No shader derives a payload address directly from a sparse virtual probe
  address.
- Dense mode resolves `physicalProbeIndex == virtualProbeIndex`, preserving the
  current behavior and providing a strict rollback/oracle path.

### 6.2 Sampling and fallback

- A nonresident probe is rejected before probe payload, sampled-image layer, or
  source-cache access.
- Initializing, evicted, stale-generation, and suppressed-empty pages contribute
  zero fine support.
- Partial fine support retains its existing normalized interpolation behavior;
  the coarser volume fills only missing ownership.
- With paging enabled, at least one containing coarser ring is dense. If the
  configured topology cannot provide that guarantee, sparse mode is unavailable.
- Missing fine pages never bypass visibility or leak protection in the coarser
  gather.

### 6.3 Update and publication

- An update record carries the virtual state generation, physical payload
  index, and physical-page mapping generation observed during scheduling.
- Trace, relocate/classify, transport, blend, publish, and lifecycle commit all
  reject a stale virtual or physical generation.
- A reassigned physical page cannot expose its previous owner's atlas or source
  cache.
- New page mappings are published only after source-cache valid words and new
  virtual states have been initialized fail-closed.
- Fresh/nonresident/dirty flags clear only after a matching complete
  publication, never at allocation or scheduling time.

### 6.4 Capacity and lifetime

- Virtual topology changes and physical capacity changes are explicit resource
  transactions. Page demand alone never reallocates a buffer or image.
- The stable capacity key returns before Vulkan size queries, descriptor work,
  retirement work, or memory-plan reconstruction.
- Retired physical pools remain alive until their completion token permits
  destruction; no `DeviceWaitIdle` is used.
- A full page pool degrades to deterministic coarser-ring fallback. It does not
  overrun, grow opportunistically, or evict a currently demanded/pinned page.

### 6.5 Determinism and observability

- Given identical volume tables, depth demand, feedback demand, settings, and
  prior residency state, the reference model and GPU choose the same admissions
  and evictions.
- Atomics collect demand and counts; they do not define winner order.
- Delayed residency feedback carries frame serial and all relevant generations.
- Every byte reported as saved is reconciled against the dense-equivalent plan
  and the concrete live Vulkan allocations.

## 7. Terminology and address model

Use distinct names in C#, GLSL, diagnostics, and documentation:

- `virtualProbeIndex`: current global toroidal probe index. It addresses probe
  state, relocation/classification, scheduler state, and virtual diagnostics.
- `physicalProbeIndex`: compact payload slot. It addresses all atlases, the V2
  source cache, and sampled-image layers.
- `virtualPageIndex`: a volume-local physical-coordinate page plus that volume's
  page-table base.
- `physicalPageIndex`: one slot in the bounded sparse page pool.
- `virtualStateGeneration`: current per-virtual-slot generation.
- `pageMappingGeneration`: a full-width, non-zero owner token minted for one
  virtual-page/physical-page assignment. It is not a truncated frame number or
  a per-page counter that may silently produce an ABA match.
- `residencyResourceGeneration`: generation of the complete page arena and
  payload-capacity transaction.

Do not continue using an unqualified `probeIndex` where both address spaces are
in scope.

### 7.1 Fixed page geometry

The proposed production page is `2 x 2 x 2` probes, eight payload slots:

- it matches the cardinality of the trilinear stencil;
- it exactly divides every axis of the current High rings;
- it keeps spatial over-allocation small around a surface;
- one High-profile page is 81,920 payload bytes, or 80 KiB;
- the current near ring has `14 x 7 x 14 = 1,372` virtual pages.

A `4 x 2 x 4` candidate may be evaluated by the Phase 0 simulator, but page
dimensions are compile-time ABI constants, not an exposed runtime tuning knob.
Do not ship multiple page-size shader variants unless measured gather cost proves
the smaller page unsuitable.

### 7.2 Virtual-to-page calculation

For a logical coordinate `l` in a volume, preserve the existing toroidal step:

```text
v = (l + physicalOffset) mod gridCount
```

Then resolve:

```text
pageCoord       = v / pageDimension
pageLocalCoord  = v % pageDimension
virtualPage     = volume.pageTableFirst + flatten(pageCoord, volume.pageGrid)
pageLocalProbe  = flatten(pageLocalCoord, 2 x 2 x 2)
physicalProbe   = sparsePoolFirstProbe + table[virtualPage].physicalPage * 8
                  + pageLocalProbe
```

For a dense volume:

```text
physicalProbe = volume.densePhysicalFirstProbe + flatten(v, gridCount)
```

Grid axes not divisible by two use ceil-divided page grids. Invalid padding slots
remain permanently unused but are charged to physical capacity if their page is
resident. Tests must cover these edge pages even though all current High ring
axes divide evenly.

### 7.3 Volume paging record

Keep `GPUSimpleDdgiVolume.GridCountsAndFirstProbe.W` as the first virtual probe.
Append a parallel, fixed-size `GPUSimpleDdgiVolumePaging` table rather than
overloading the existing float-packed fields:

```text
Addressing: virtualFirstProbe, pageTableFirst, densePhysicalFirst, residencyMode
PageLayout: pageGridX, pageGridY, pageGridZ, sparsePoolFirstProbe
```

Append residency arena index, virtual/physical counts, feature flags, and
residency-resource generation to `GPUSimpleDdgiParams`. Update the C# size,
`SIMPLE_DDGI_HEADER_WORDS`, params allocation, and all ABI tests together.

### 7.4 Physical address helper

Add one shader helper returning a complete value, not just an integer:

```text
SimpleDdgiProbeAddress
    virtualProbeIndex
    physicalProbeIndex
    pageMappingGeneration
    resident
```

All receiver and solver gathers call it before payload access. Update consumers
read the physical address stamped in their queue record and validate it against
the live mapping before writing.

## 8. Storage model

### 8.1 Keep virtually indexed

- `GPUSimpleDdgiProbeState`;
- relocation/classification records;
- `GPUSimpleDdgiSchedulerProbeState` from the GPU scheduler plan;
- dirty, source/cohort, wake, and latency metadata;
- virtual page history and suppression evidence.

These resources preserve topology and state even while the payload is absent.
Nonresident state has an explicit flag and zero active weight; do not overload
`Inactive`, which remains geometric classification evidence.

### 8.2 Move to physical indexing

- published irradiance atlas SSBO;
- visibility atlas SSBO;
- private transport irradiance target;
- V2 source-ray cache;
- sampled irradiance and visibility image layers.

Ray-result scratch stays queue-relative. The update queue stays request-relative.

### 8.3 Residency arena

Create a dedicated `SimpleDdgiProbePageCache` resource/controller and a checked
`SimpleDdgiProbePageLayout`. One append-only bindless residency arena contains:

| Region | Suggested record | Capacity |
|---|---:|---:|
| Header and generations | fixed | one |
| Volume paging table | 32 B | max 16 volumes |
| Virtual page table | 16 B | accepted virtual pages |
| Virtual page history/demand | 16 B | accepted virtual pages |
| Physical reverse metadata | 32 B | sparse physical page capacity |
| Demand/source counters | fixed/bounded | demand classes x volumes |
| Classify/prefix/admission scratch | checked | virtual/physical page capacity |
| Init work list and indirect commands | checked | admissions per frame |
| Fixed feedback summary | fixed | one per transaction |

Use 16-byte region alignment and checked 64-bit size arithmetic. For the current
profile, residency-only arena overhead should remain at or below 512 KiB; at the
hard 32,768-virtual-probe limit it should remain at or below 1 MiB unless a new
measured plan is approved.

Suggested page-table entry fields are physical-page-plus-one (`0` means
nonresident), mapping generation, flags, and reserved/debug data. Suggested
physical metadata includes owner virtual page, mapping generation, last relevant
frame, last published frame, pin/in-flight flags, and current demand class.

Mint mapping generations from one monotonic 32-bit arena counter. Reaching its
reserved wrap threshold forces a new residency-resource transaction after all
old submissions retire; it must not wrap in place. The scheduler queue header
captures the full residency-resource generation, while each update record
captures the mapping generation. This keeps the per-request ABI at 32 bytes
without packing two lossy generation fragments into one word.

### 8.4 Physical payload layout

Dense payload spans and the sparse pool share one physical index space so the
existing atlas/source-cache descriptors remain usable. The memory plan lays out:

1. dense authored-volume payloads;
2. dense mid/far ring payloads;
3. the sparse physical page pool.

The exact order may differ if identity addressing is required in Dense mode; it
must be deterministic and recorded in each volume paging record. In Dense mode,
all volume bases reproduce the current `physical == virtual` layout.

The sampled atlas provisions layers from physical capacity, retaining the
existing 256-layer capacity quantum. The sparse page budget is an eight-probe
multiple, while total physical capacity is rounded independently for the image
mirror. Both the unrounded payload capacity and rounded image-layer capacity
must be reported.

## 9. Demand model

Surface demand has one proactive producer and bounded supplemental producers.
All producers execute before reconciliation for their target demand epoch.

Make concurrent demand stamping deterministic and race-free. A suggested
16-byte record uses one 32-bit atomic lane per demand class, packing a monotonic
24-bit demand epoch and an 8-bit inverse-distance bucket. Producers use
`atomicMax`; a newer epoch dominates an older write, and the nearest request wins
within one epoch. Transparent/post-opaque feedback targets the next epoch. Before
the 24-bit epoch wraps, perform a serialized zero-clear and restart the epoch
only after older writers have retired. If profiling selects a different record,
its compare/exchange protocol must have the same no-lost-request and no-stale-
winner guarantees; a racy stamp-plus-separate-distance reset is not acceptable.

### 9.1 Proactive visible opaque demand

Add `SimpleDdgiPageDemandPass` after opaque forward shading and before residency
reconciliation. It reads the current scene depth and current camera matrices:

1. dispatch over screen tiles;
2. sample a stratified subset of depth values, including discontinuity coverage;
3. reject clear/background depth;
4. reconstruct world position with `InverseViewProjectionMatrix` using the same
   depth convention as existing SSGI/AO helpers;
5. reconstruct a conservative geometric normal from neighboring depth where
   required for the bounded DDGI sample bias;
6. select the primary virtual volume with the same unbiased receiver rules as
   `SelectSimpleDdgiVolume`;
7. for a sparse volume, mark every virtual page touched by the eight-corner
   biased gather stencil;
8. record current-frame visible demand, minimum quantized camera distance, and
   source counter.

Start shadow evaluation with one invocation per `8 x 8` screen tile and four
stratified depth samples. Do not freeze that choice until shadow instrumentation
measures predictor false negatives and page inflation. The final predictor must
cover normal/view bias without using an unconditional whole-page halo that
turns the near ring dense again.

Scene normals must not become a feature requirement: `SceneNormal` is currently
owned by the SSGI path, while depth exists in DDGI-only configurations.

### 9.2 Receiver feedback demand

The depth pass does not cover transparent receivers, particles, fog samples, or
all bias/discontinuity mistakes. Add a small request helper to the Simple gather:

- a missing fine page may atomically stamp a receiver-miss request;
- transparent/particle callers may sparsely touch resident pages so retention
  does not oscillate;
- forward callers rate-limit requests to one representative fragment per screen
  tile or quad;
- requests deduplicate by virtual page;
- debug-only sampling does not request pages;
- solver bounce gathers do not allocate pages in the first production version.
  They use dense coarser fallback and report would-request telemetry in shadow
  mode. This prevents recursive transport from expanding the fine working set
  across the scene.

Opaque depth demand is highest priority, receiver misses are next, and optional
non-depth touches are lower. Demand flags define fixed priority classes; atomic
arrival order never decides admission.

### 9.3 Recently relevant retention

Each resident page records its last qualifying demand frame. A page remains
retained while any of the following holds:

- demanded by a visible receiver this frame;
- requested by a bounded receiver-miss producer this frame;
- it has an admitted update or publication transaction in flight;
- it is fresh and within its maximum publication-latency window;
- its last qualifying demand is younger than the configured retention interval;
- it is explicitly pinned by a development tool or future authored policy.

Retention is hysteresis, not permanent priority. Historical maximum demand must
not make a page unevictable after the camera moves. The default interval is
selected from shadow traces; expose a clamped frame setting for experiments, but
quality tiers own shipping defaults.

### 9.4 Camera cuts and teleports

A camera cut does not flush the physical pool. Current visible demand wins
immediately; old non-demanded pages lose ordinary retention protection under
capacity pressure and become deterministic victims. Admissions are bounded per
frame so page initialization and fresh-probe work cannot spike without limit.
Until the new fine pages publish, dense coarser rings remain the visible result.

Track ordinary-motion and cut/teleport allocation-to-publication latency
separately.

### 9.5 Inactive-page suppression

Classification complements surface demand after a page has been evaluated:

- a page is a suppressible empty candidate only when every valid virtual slot
  has completed relocation/classification and is confidently inactive;
- require at least two matching full retraces or equivalent explicit confidence
  evidence before suppression;
- current in-flight work and mixed active/inactive pages are never suppressed;
- suppression clears on a relevant geometry/topology revision, newly exposed
  toroidal cell, explicit dirty overlap, or bounded retry expiry;
- a suppressed page remains nonresident even if ordinary depth demand persists,
  and receivers use coarser rings;
- bounded retry re-admits it at low rate to handle streaming and geometry
  changes.

Do not evict individual inactive probes from a partly useful eight-probe page.
Keep `Inactive`, `NonResident`, and `SuppressedEmpty` as separate states and
diagnostics.

## 10. Deterministic GPU page reconciliation

Implement reconciliation as staged compute inside
`SimpleDdgiPageResidencyPass`.

### 10.1 Classify

One invocation per virtual page determines:

- sparse eligibility and current mapping validity;
- current demand class and distance bucket;
- retention age;
- pin/in-flight/publication state;
- confirmed-empty suppression state;
- whether it is resident, a missing admission candidate, a free slot, or an
  evictable victim.

Invalid table/reverse-map disagreement increments an error counter, invalidates
the entry, and fails closed. It never attempts an in-place repair using
untrusted indices.

### 10.2 Deterministic compact and rank

Compact admission candidates and victims with group counts/prefixes. Define a
total order:

Admissions, highest first:

1. current visible surface;
2. current receiver miss;
3. fresh/dirty page whose receiver latency is already outstanding;
4. recent visible retention;
5. optional low-priority non-depth request;
6. retry of a suppressed/inactive page.

Within a class, use minimum distance, oldest outstanding latency, volume
priority, virtual page index, then deterministic cursor rotation where fairness
requires it.

Victims, lowest value first:

1. confirmed empty and unrequested;
2. expired and unrequested;
3. oldest last-relevant frame;
4. lowest demand class/farthest distance;
5. physical page index as final tie-break.

Current demand, pins, and in-flight transactions are not victim candidates.

### 10.3 Admit

One bounded authority pairs free/victim pages with the highest-ranked missing
pages while enforcing:

- physical page capacity;
- maximum admissions and evictions per frame;
- per-volume minimum/maximum residency policy if later needed;
- no duplicate virtual or physical owner;
- no eviction of a protected page;
- no counter, candidate, or init-list overflow.

If demand exceeds capacity or the per-frame admission limit, rejected pages stay
nonresident and are reported. The coarse fallback makes this a quality-pressure
event, not a memory-safety event.

### 10.4 Unmap, initialize, and map

For each reassignment, execute in this order with compute barriers:

1. clear the old virtual table entry;
2. advance the physical page mapping generation, never allowing zero;
3. mark every valid old virtual slot nonresident/fresh and advance its virtual
   state generation so stale requests cannot commit;
4. clear the source-cache validity/generation word for every physical probe/ray
   in the page; the remaining cache payload need not be zeroed;
5. initialize every valid new virtual state as non-published, fresh, source-cache
   invalid, zero active weight, and scheduler-dirty;
6. initialize the reverse physical owner record;
7. publish the new page-table entry with its valid flag and mapping generation;
8. append the new page's valid virtual probes to the scheduler wake/input path.

Atlas texels and sampled layers need not be cleared because no receiver reads
them until logical state is committed valid. Every direct debug path must honor
the same state/residency gate.

### 10.5 Page lifecycle

Use explicit states such as:

```text
NonResident -> Initializing -> ResidentFresh -> ResidentPublished
ResidentPublished -> ExpiredCandidate -> NonResident
ResidentFresh/Published -> SuppressedEmpty -> NonResident
```

`Initializing` is never receiver-valid. `ResidentFresh` has a valid address but
zero fine support. `ResidentPublished` still may contain individually inactive
probes. An aborted later DDGI update leaves the page fresh and retry-eligible; it
does not expose stale payload.

## 11. Gather and shader changes

### 11.1 Fine gather

Refactor `SampleSimpleDdgiVolumeGather` in
[`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl):

1. compute the virtual address with the unchanged toroidal mapping;
2. resolve residency and the physical payload address;
3. if nonresident, record `SIMPLE_DDGI_GATHER_REJECT_NON_RESIDENT`, optionally
   stamp bounded receiver demand, and continue without state/payload reads;
4. read virtual probe state;
5. reject fresh, exposed, relocation-pending, inactive, or zero-active state;
6. only then read physical irradiance/visibility payloads;
7. retain the current trilinear, directional, visibility, ownership, and leak
   composition math.

Extend the rejection mask and detailed counters without changing existing bit
meanings. Add a distinct second-gather attribution for fine-page
nonresidency/suppression rather than counting it as generic invalid data.

If all eight fine corners are nonresident, skip all fine atlas/state work and
enter `FindSimpleDdgiFallbackVolume`. If only some corners are resident, their
valid interpolation mass is preserved and the coarser gather fills the missing
ownership through `BlendSimpleDdgiGatherResults`.

The page table is small and cache-friendly, but address resolution adds work to
the hottest shader. Start with the clear reference implementation. Then profile
distinct-page lookup count and, only if measured, cache repeated page entries
within one eight-corner gather. Do not trade the existing octahedral sampled
atlas win for register spills or unbounded local arrays.

### 11.2 Physical atlas helpers

Rename atlas/source-cache helper parameters to `physicalProbeIndex`. The sampled
atlas group/layer calculation also uses the physical index. Keep deterministic
ray rotation and world-position lookup keyed by `virtualProbeIndex`, so moving a
page does not change its ray sequence or logical location.

### 11.3 Update queue ABI

Retain the 32-byte `GPUSimpleDdgiProbeUpdate` size by consuming its two remaining
reserved words:

| Word | Meaning in sparse-capable ABI |
|---:|---|
| 0 | virtual probe index |
| 1 | volume index |
| 2 | update flags and active ray count |
| 3 | expected virtual state generation and age |
| 4 | full source-ray cardinality |
| 5 | requested source-lighting generation |
| 6 | physical probe index |
| 7 | expected page mapping generation |

Dense mode fills a valid direct physical index and a dense resource-generation
sentinel. The queue header/dispatch transaction carries the expected full
residency-resource generation; word 7 carries the full mapping generation for
the individual assignment. Every consumer validates both. Update
`GPUStructs.cs`, GLSL, GPU scheduler emit, CPU reference emit, ABI tests, capture
decoders, and validation comparison together.

### 11.4 Update stages

- Trace resolves world position and state by virtual address, writes ray scratch
  by queue offset, and writes source cache by physical address.
- Relocate/classify reads and writes virtual state/relocation records; it validates
  the physical mapping because its outcome belongs to that payload transaction.
- Transport reads/writes the queue probe's physical cache/target but maps every
  neighbor gathered at a ray hit independently. Missing fine neighbors use
  coarser rings and do not allocate in the first release.
- Blend writes published/private atlas payload by physical address and virtual
  state by virtual address.
- Canonical publish copies physical target to physical published payload.
- Sampled publish writes the physical image layer.
- Lifecycle commit validates both generations before clearing virtual fresh/dirty
  state or changing source/cohort counters.

Every stage increments a separate stale-virtual, stale-page, nonresident, and
out-of-range rejection counter. A stale request may release its private outcome
slot but may not mutate current page, probe, source, or cohort state.

## 12. GPU scheduler integration

`SimpleDdgiGpuScheduler` continues to reason in virtual probe space. Page
reconciliation executes before its classify stage.

Production classification should eventually enumerate:

- every dense physical span; and
- valid virtual probes derived from the resident sparse-page list.

This avoids scanning a denser unresident virtual near lattice. A first mirror
implementation may scan all virtual probes and early-reject nonresident ones if
it remains within the 0.25 ms scheduler gate. Promotion requires measurement,
not an assumed optimization.

Required policy changes:

- nonresident virtual probes are not maintenance or convergence participants;
- newly admitted pages enter `VisibleZeroSupport` or
  `FreshExposedVisible` according to their demand source;
- current visible page demand replaces the CPU camera-proximity heuristic as the
  highest-quality visibility signal in sparse mode;
- inactive suppression and retry remain explicit lanes;
- near-ring quota reservations clamp to resident pending work and return unused
  capacity to mid/far maintenance;
- page admission never implies probe update admission;
- current demand and publication latency may reserve a bounded recovery quota,
  but primary rays still obey the global hard budget;
- resident-count changes update convergence denominators exactly.

Page allocation and probe scheduling need separate counters. Reporting eight
admitted pages must never be interpreted as 64 published probes.

## 13. Toroidal scrolling, invalidation, and dirty state

The page table is indexed from the existing toroidal physical coordinate. For a
preserved world cell, the origin shift and physical-offset change produce the
same virtual coordinate, page, local slot, and payload mapping; no payload copy
is required.

For newly exposed slices:

- invalidate only the newly owned virtual slots, even if the rest of their page
  is preserved;
- advance each exposed virtual state generation;
- clear inactive/suppression evidence for the exposed slots;
- if the containing page is resident, mark those slots fresh and eligible when
  demanded;
- if the page is nonresident, leave them nonresident and let current depth demand
  decide whether to allocate it;
- recompute all-page suppression when any member changes ownership.

Non-cell-aligned topology changes, page dimensions, ring grids, residency mode,
physical capacity, sampled-atlas capacity, and V2 ray capacity are incompatible
capacity-key changes. They create a new residency-resource generation and fresh
bootstrap. Cell-aligned scrolls do not.

Regional light/material/geometry dirtiness affects resident pages normally.
Nonresident pages need only retain the latest relevant revision/generation; if
later allocated, they perform a full current-generation bootstrap rather than
replaying every missed dirty event. A geometry change immediately clears empty
suppression for overlapping virtual pages.

## 14. Source cache, convergence, and atmosphere cohorts

The V2 source cache is the largest single saving and the most important stale
alias risk. Clearing only each cache entry's validity/generation word during
physical-page reassignment is mandatory. Relying on coincident 24-bit virtual
generations from unrelated page owners is not safe.

Convergence semantics become:

- resident, noninactive, source-current probes participate;
- nonresident and suppressed pages do not count as pending solver work;
- newly allocated probes join the current source generation fresh and invalid;
- an evicted probe retires from a cohort only through the page reconciliation
  commit carrying the matching page/source/cohort generations;
- allocating a page during an active atmosphere cohort adds its current-source
  obligation before it can become receiver-valid;
- a page cannot publish payload from a retired atmosphere source version;
- exact page admission/eviction deltas are included in the GPU commit summary.

The atmosphere controller must consume fence-complete summary records. It must
not infer release because a page aged out, resident count fell, or stale count
reached zero.

Routine maintenance does not keep an otherwise irrelevant page resident. If a
page expires, its cached source may be discarded; reallocation performs a full
source refresh. This is the bandwidth half of the sparse trade.

## 15. Memory planning and admission

Refactor `SimpleDdgiMemoryPlan` to distinguish:

```text
VirtualProbeCount
DensePayloadProbeCount
SparseVirtualProbeCount
SparseVirtualPageCount
SparsePhysicalPageCapacity
PhysicalProbeCapacity
SampledAtlasPhysicalProbeCapacity
```

Calculate virtually indexed, physically indexed, request-relative, and fixed
arena resources separately. Add exact fields for residency arena regions and
the future GPU scheduler arena; `LiveBytes` remains the sum of concrete live
allocations.

`SimpleDdgiLayoutReport` must report:

- requested/accepted virtual topology;
- dense fallback payload reservation;
- requested/admitted sparse physical capacity;
- dense-equivalent bytes;
- allocated sparse bytes;
- avoided bytes;
- page padding and sampled-atlas rounding overhead;
- explicit degradation reason if a physical page budget is reduced.

Admission order in sparse mode is quality-critical:

1. validate at least two ordered rings and structured gather/V2/GPU-resident
   prerequisites;
2. admit virtual topology under the existing virtual probe and volume limits;
3. reserve the mandatory dense coarser ring payloads;
4. reserve virtually indexed state and scheduler metadata;
5. reserve the configured sparse near-page pool and residency arena;
6. reserve request scratch and the optional sampled mirror;
7. accept, explicitly reduce, or reject according to
   `SimpleDdgiLayoutAdmissionMode`.

The physical page budget is explicit and reproducible. Do not silently consume
whatever heap budget happens to remain on one machine. `Degrade` may reduce the
page pool only to a declared minimum working-set capacity and must record the
decision. `Reject` fails before Vulkan allocation. A sparse plan that cannot
retain its dense coarser fallback is invalid.

Dense mode must still produce the current 177,519,440-byte plan for the captured
inputs before scheduler-plan changes are applied. Add this exact regression
fixture.

## 16. Settings, modes, and compatibility

Add a Simple-specific mode:

```csharp
public enum SimpleDdgiProbeResidencyMode : uint
{
    Dense = 0,
    Shadow = 1,
    SparseNearRing = 2
}
```

- `Dense`: current direct physical layout and authoritative rollback.
- `Shadow`: dense rendering plus demand collection and reference/GPU page-model
  telemetry; no receiver-visible mapping change.
- `SparseNearRing`: near ring paged, authored/mid/far volumes dense.

Add settings for physical page budget, retention frames, maximum admissions per
frame, maximum receiver-feedback requests, and optional inactive retry. Keep
page dimensions internal. Clamp all settings, serialize them, include them in
layout/capacity fingerprints, capture identity, CLI overrides, and
`RendererSettingsReference.md`.

Rollout defaults:

- all tiers remain `Dense` while implementation and shadow validation land;
- `Shadow` is development/capture only;
- High and Ultra are candidates for `SparseNearRing` after locked signoff;
- Low and Medium remain dense until their smaller payloads show a measured net
  benefit;
- authored volumes stay dense irrespective of purpose in the first release.

Sparse mode additionally requires structured gather, transport V2, toroidal
addressing, GPU-resident scheduling, and a dense coarser ring. Unsupported
combinations report a precise fallback reason.

### 16.1 Runtime scheduler failure

Do not allocate a hidden full-density payload pool alongside sparse mode. If the
GPU scheduler becomes unavailable at runtime:

- freeze the last complete resident page table and payload while a controlled
  retry is possible;
- stop page eviction/reassignment;
- if residency state itself is invalid, disable the sparse fine volume in the
  uploaded volume policy and sample dense coarser rings;
- re-enter only at a frame boundary with a new transaction generation;
- never read back the full mapping or wait the device to manufacture a CPU
  sparse fallback.

CPU reference and compare modes continue to use Dense/Shadow layouts.

## 17. Render graph, pass order, and synchronization

Add `RenderGraphResourceId.SimpleDdgiResidency` and bind the concrete arena in
all normal and async binding paths. A graph-safe placeholder is registered while
the feature is inactive.

Target order after GPU scheduler migration:

1. opaque ForwardPlus samples the previous complete DDGI field and may emit
   bounded receiver demand;
2. `SimpleDdgiPageDemandPass` reads current depth and appends/stamps surface
   demand;
3. `SimpleDdgiPageResidencyPass` classifies, admits, unmaps, initializes, maps,
   and publishes its work list;
4. `SimpleDdgiSchedulePass` classifies only live/resident probes and emits the
   update queue;
5. trace;
6. relocate/classify;
7. transport;
8. blend;
9. canonical and optional sampled publish;
10. scheduler local/propagation commit;
11. residency/scheduler feedback reduction and fixed readback copy;
12. transparent/fog/particle consumers may sample the newly completed field and
    stamp demand for the next reconciliation.

Declare:

- Forward/transparent/fog/particle Simple samplers: residency read; bounded
  demand write where enabled;
- page demand: scene depth sampled read, params/volume policy read, residency
  read/write;
- page residency: residency read/write, virtual probe/scheduler state
  read/write, source cache write for validity clears;
- scheduler and every update consumer: residency read, scheduler arena and
  existing resources according to their current contracts;
- publish/commit: residency read and feedback write.

Use compute-write to compute-read/write barriers between residency stages and
before scheduler classify. If residency writes indirect initialization commands,
use the same compute-to-draw-indirect synchronization2 contract as the GPU
scheduler plan.

The residency arena, scheduler arena, virtual state, physical payloads, queue,
and sampled atlas are one cross-frame serial dependency. Do not allow frame N+1
reconciliation to overlap frame N publication. Certify the graphics-queue path
first. Re-audit the whole Simple-DDGI async segment after correctness; do not add
per-stage queue-family ping-pong.

## 18. Diagnostics and debug tooling

### 18.1 Exact scalar diagnostics

Add current and delayed generation-stamped fields for:

- virtual probe/page count;
- dense physical probe count;
- sparse physical page/probe capacity;
- current resident/free/initializing/published/suppressed page count;
- nonresident virtual probe count;
- classified inactive resident probe count;
- current demanded pages by source and priority;
- retained pages by age bucket;
- admissions, evictions, failed admissions, pool-pressure frames, and maximum
  consecutive pressure frames;
- page-table/reverse-map disagreement, duplicate owner, stale virtual request,
  stale mapping request, and out-of-range counts;
- allocation-to-first-schedule and allocation-to-first-publication P50/P95/max;
- visible fine-demand hit/miss fraction;
- gather nonresident rejection and coarser-fallback counts;
- confirmed-empty suppression and retry counts;
- physical payload bytes, page arena bytes, dense-equivalent bytes, allocated
  capacity bytes, and avoided bytes;
- per-frame payload bytes/rays avoided because pages are nonresident;
- per-ring virtual/resident/active/inactive/demanded/converged populations.

Keep `SimpleDdgiInactiveProbeCount` as classification evidence. Do not add
nonresident probes to it. Add explicit fields rather than changing old schema
semantics silently.

Shipping readback stays fixed-size and at most 4 KiB per frame. Detailed page
tables or page lists are validation-only delayed readbacks.

### 18.2 Debug views

Add:

- `DdgiProbeResidency`: logical probe markers colored resident-published,
  resident-fresh, demanded-missing, retained, suppressed-empty, and nonresident;
- `DdgiResidencyFallback`: receivers whose fine ownership was missing because of
  page residency and the coarser ring that supplied it;
- `DdgiPageAge`: retained age/pressure visualization;
- optional physical-page ID/generation view for validation.

Probe overlays remain on the regular virtual grid. A nonresident marker is
hollow/neutral rather than omitted, making it possible to verify that topology
and toroidal motion did not change.

Update
[ddgi-diagnostics.md](../docs/rendering/ddgi-diagnostics.md),
[ddgi-runtime-validation.md](../docs/rendering/ddgi-runtime-validation.md), the
debug shortcut cycle, runtime snapshots, performance JSON schema, schema
semantics, and benchmark manifest tests.

### 18.3 Shadow-mode working-set report

For every capture trajectory, export:

- unique current-demand pages per frame;
- retained working set for candidate retention intervals;
- page occupancy/utilization for `2 x 2 x 2` and the offline `4 x 2 x 4`
  comparison;
- predicted versus instrumented actual fine-page touches;
- false-negative and false-positive page counts;
- required pool P50/P95/P99/max;
- simulated admissions/evictions/pressure for candidate capacities;
- projected exact memory plan for each candidate.

Freeze these reports before selecting shipping capacity or retention defaults.

## 19. Implementation phases

### Phase 0: freeze evidence and paging decisions

Tasks:

- Preserve the exact same-binary Dense baseline, including the
  177,519,440-byte reconciliation and the 2,252 inactive count.
- Add a pure offline calculator for current High topology, page geometry,
  physical payload cost, sampled-layer rounding, and candidate page budgets.
- Record static, slow-motion, ring-boundary, vertical motion, camera cut,
  teleport, transparent, foliage, and dynamic-geometry demand trajectories.
- Compare `2 x 2 x 2` and `4 x 2 x 4` page granularity offline.
- Predeclare capacity, retention, quality, memory, and timing gates before
  authoritative sparse rendering.
- Record the dependency revision/status of the GPU scheduler and atmosphere
  cohort work.

Exit criteria:

- Dense byte accounting is exact and every resource has one owner.
- Candidate pool sizes come from P50/P95/P99/max working sets, not inactive
  percentage alone.
- Page size and demand sampling policy have written evidence.
- No production behavior changes.

### Phase 1: pure contracts, address oracle, and memory plan

Tasks:

- Add residency mode/settings and serialization with Dense defaults.
- Implement `SimpleDdgiProbePageLayout` with checked sizes/offsets.
- Implement pure virtual/toroidal/page/physical address helpers.
- Split `SimpleDdgiMemoryPlan` into virtual and physical capacities.
- Extend layout reports and budget admission without allocating GPU resources.
- Implement `SimpleDdgiProbePageReferenceModel` for demand, retention,
  suppression, admission, eviction, and generation transitions.
- Add exact fixtures for every tier and hard maximum.

Exit criteria:

- Dense plan remains byte-identical for unchanged inputs.
- Address round trips and scroll preservation pass exhaustive small-grid tests.
- The reference model is allocation-free per simulated frame and deterministic.
- Overflow, invalid topology, and insufficient fallback budgets fail before
  Vulkan allocation.

### Phase 2: residency arena and inert graph integration

Tasks:

- Add `SimpleDdgiProbePageCache` and residency arena allocation/retirement.
- Append the bindless index and graph resource.
- Add GPU structs, GLSL constants, ABI/layout tests, placeholder registration,
  fixed summary readback, and cleanup.
- Add no-op demand/residency passes with timestamp slots and graph declarations.
- Include residency in capacity keys, resource generations, async binding audits,
  diagnostics, and memory reports.

Exit criteria:

- Dense rendering is unchanged.
- Shadow/Dense stable frames perform no arena reallocation or descriptor churn.
- Arena bytes reconcile exactly and stay within overhead gates.
- Vulkan validation is clean through enable/disable, resize, reload, profile
  changes, and shutdown.

### Phase 3: shadow surface demand and coverage oracle

Tasks:

- Implement depth reconstruction and page marking in
  `SimpleDdgiPageDemandPass`.
- Add rate-limited actual gather-touch instrumentation in Shadow mode.
- Add receiver-miss demand records without changing gather output.
- Simulate the page allocator on GPU and in the CPU reference model; render from
  the dense field.
- Add `SimpleDdgiResidencyCoverageValidator` rather than changing the meaning of
  the existing geometric receiver-coverage validator.
- Export working-set distributions and tune predictor sampling/dilation.

Exit criteria:

- Shadow output is pixel-identical to Dense.
- GPU page demand/admission output matches the reference model for deterministic
  fixtures.
- Predictor false-negative and inflation gates pass on the full trajectory
  matrix.
- Selected pool/retention defaults are frozen before sparse image comparisons.

### Phase 4: physical payload addressing behind Dense mode

Tasks:

- Add the volume paging table and expanded params header.
- Introduce `SimpleDdgiProbeAddress` and rename virtual/physical parameters.
- Convert atlas, source cache, sampled mirror, trace, transport, blend, publish,
  and debug helpers to accept physical payload addresses.
- Fill direct addresses in Dense mode and use the extended queue ABI.
- Add virtual/page/resource generation validation to every update stage while
  mapping remains identity.

Exit criteria:

- Dense mode resolves `physical == virtual` everywhere.
- Dense HDR output is bitwise identical where deterministic, otherwise within a
  predeclared numerical-zero tolerance.
- All stale-generation fault-injection cases fail closed.
- No pass still treats an unqualified sparse virtual index as an atlas/cache
  address.

### Phase 5: authoritative sparse map and coarse fallback

Tasks:

- Implement deterministic classify/compact/admit/reconcile stages.
- Implement unmap, validity-word clear, state initialization, reverse mapping,
  and map publication.
- Enable near-ring table lookup in `SparseNearRing`.
- Add nonresident gather rejection, partial-support composition, and exact
  coarser fallback attribution.
- Keep the current virtual topology and CPU/GPU update budgets unchanged.
- Add fixed summary feedback and validation-only page-table comparison.

Exit criteria:

- No stale or nonresident payload is read under fault injection.
- CPU reference and GPU page mappings are identical for deterministic inputs.
- Missing fine pages always use dense coarser support or the existing final
  environment complement if all fields are unavailable.
- Page pressure cannot overrun a buffer, duplicate an owner, or replay old work.
- Topology-identical sparse quality and memory gates pass on graphics queue.

### Phase 6: GPU scheduler and lifecycle authority

Tasks:

- Run page reconciliation before scheduler classification.
- Filter nonresident work and wake newly admitted page probes.
- Fill physical address/generation in GPU-emitted update records.
- Integrate page transitions with dirty latency, convergence, source refresh,
  atmosphere cohorts, and scheduler commit.
- Disable any sparse-mode CPU page/probe enumeration and validation readback in
  shipping mode.
- Optimize scheduler enumeration to resident pages only if the measured virtual
  scan misses the scheduler timing gate.

Exit criteria:

- Stable sparse frames have O(1)/O(volume count) CPU work and no page/probe
  upload or readback.
- Allocation, update, publication, eviction, and cohort counts reconcile.
- Ordinary visible page publication latency meets its gate without starving
  dense outer-ring maintenance.
- Same-frame or stale summary data never controls rendering.

### Phase 7: suppression, runtime fallback, and lifetime hardening

Tasks:

- Add confirmed-empty page suppression and bounded retry.
- Add geometry revision/dirty-region suppression invalidation.
- Implement runtime scheduler/residency failure behavior and controlled re-entry.
- Exercise resource-capacity transitions and retirement with frames in flight.
- Add camera cut/teleport pressure policy and development pin/freeze controls.

Exit criteria:

- Stationary empty pages do not churn.
- Streamed/moved geometry reactivates suppressed pages within the declared
  latency.
- Runtime failure preserves safe coarse lighting without device waits or stale
  aliasing.
- Old physical pools are never destroyed or rebound early.

### Phase 8: performance, async audit, and production promotion

Tasks:

- Profile demand, classify, compact, reconcile, initialization, gather lookup,
  scheduler, trace, transport, blend, publish, and commit separately.
- Optimize only measured bottlenecks.
- Run locked Dense/Shadow/Sparse repetitions with executable and shader identity.
- Complete human review for movement, page boundaries, ring boundaries, thin
  walls, camera cuts, and transparent content.
- Certify the graphics path, then run whole-segment async validation and
  profitability captures.
- Publish an implementation-status document with exact commits, binaries,
  shader hashes, tests, captures, memory reconciliation, and default decision.

Exit criteria:

- All gates in section 21 pass in three locked production repetitions.
- Sparse default promotion is limited to qualified tiers and configurations.
- Async is either independently certified/profitable or remains disabled with
  the accepted graphics path unchanged.

### Phase 9: spend a measured fraction on denser near probes

This is a separate quality experiment after topology-identical Sparse is
accepted.

Tasks:

- Reduce near spacing while increasing virtual near grid dimensions to preserve
  the same world reach.
- Keep mid/far topology and physical payload budget fixed.
- Re-run shadow working-set simulation because surface-page count scales roughly
  with surface area, not current inactive percentage.
- Account for the larger virtual state and GPU scheduler arena.
- Compare sparse-original and sparse-denser modes at equal physical memory and
  primary-ray/update budgets.
- Admit a density change only if visible publication latency remains bounded.
- Retain a meaningful share of the saving as memory headroom; do not spend the
  entire pool reduction automatically.

Exit criteria:

- Denser near probes produce a reviewed improvement in contact detail, thin-wall
  behavior, or indirect-light spatial error.
- Total Simple-DDGI live bytes remain below the original same-binary Dense plan.
- Total tracked memory remains within the 2 GiB/80% gate.
- GPU/CPU timing, ray budget, convergence, and motion stability still pass.

## 20. Test and validation matrix

### 20.1 Pure/unit tests

Add focused tests for:

- logical -> toroidal physical coordinate -> virtual page/local slot;
- virtual page/local slot -> virtual probe round trip;
- negative and multi-axis scroll deltas;
- one-cell scroll preservation and newly exposed slice invalidation;
- odd/non-divisible grid dimensions and padded page slots;
- dense identity mapping;
- page-table/reverse-map bijection;
- generation advance and non-zero wrap behavior;
- deterministic free allocation, eviction order, admission limits, and ties;
- no victim when every page is demanded or pinned;
- camera-cut pressure and retention expiry;
- mixed active/inactive pages and all-inactive suppression;
- geometry revision and retry reactivation;
- current-profile exact Dense bytes and sparse capacity examples;
- sampled-atlas 256-layer rounding;
- zero/min/max capacity, overflow, and budget degradation/rejection;
- queue virtual/physical field packing and ABI offsets;
- stale virtual generation, stale page generation, and stale resource generation;
- cohort add/retire accounting on allocation and eviction;
- partial fine support plus coarser ownership composition;
- nonresident rejection before a payload-read sentinel;
- demand predictor versus exact CPU gather-page oracle.

Candidate files:

- `Njulf.Tests/SimpleDdgiProbePageLayoutTests.cs`
- `Njulf.Tests/SimpleDdgiProbePageReferenceModelTests.cs`
- `Njulf.Tests/SimpleDdgiResidencyCoverageValidatorTests.cs`
- `Njulf.Tests/SimpleDdgiSparseGatherTests.cs`
- `Njulf.Tests/SimpleDdgiSparseGenerationTests.cs`
- extensions to layout, manager, sampled-atlas, shader-mirror, GPU-struct,
  renderer-diagnostics, serialization, graph-resource, and benchmark tests.

### 20.2 Shader/ABI tests

- Compile every new compute shader in all normal shader configurations.
- Assert C#/GLSL header, volume paging, page-table, metadata, queue, push-constant,
  and arena offsets.
- Assert every physical payload access flows through a physical address.
- Assert every update consumer validates both generations.
- Assert source-cache validity words are cleared on page reassignment.
- Assert Dense mode emits identity addresses.
- Extend CPU shader mirrors for page address, rejection, partial ownership, and
  coarse fallback behavior.

### 20.3 Vulkan/runtime tests

Exercise:

- Dense -> Shadow -> Sparse mode changes;
- enable/disable and unsupported prerequisite fallback;
- sampled atlas supported, disabled, and budget-rejected;
- V2 ray-capacity/profile changes;
- window resize and render-resolution changes;
- scene reload and topology change;
- slow single-cell toroidal scrolling on every axis;
- large scroll, camera cut, and teleport;
- page pool full, zero free pages, and bounded admission pressure;
- dynamic geometry entering/leaving a suppressed page;
- light/material/atmosphere invalidation during allocation and eviction;
- frames-in-flight resource retirement;
- graphics queue, forced async validation, rejected async fallback, and shutdown.

Validation must report zero out-of-range descriptors, queue-family errors,
stale-publication mutations, duplicate owners, and leaked/early-destroyed
resources.

### 20.4 Scene matrix

- Sponza right wall stationary baseline;
- Sponza slow horizontal/vertical travel and ring-boundary sweeps;
- Sponza camera cuts between plaza, upper facade, and alley/interior views;
- Cornell/thin-wall interior with dense fine demand on both wall sides;
- broad outdoor scene with large empty-air near volume;
- tall multi-floor architecture under each vertical-ring policy;
- transparent cloth/glass, foliage, particles, and fog receivers;
- moving object entering an unallocated page;
- dynamic local light and sun/sky generation change;
- deliberate physical-capacity stress with deterministic coarse fallback.

For each applicable scene capture normal lit output plus irradiance, support,
effective weight, cascade contributor, residency, residency fallback, page age,
visibility moments, relocation, and source-cache views.

## 21. Acceptance gates

Freeze exact thresholds in Phase 0 and keep these minimum contracts.

### 21.1 Correctness

- Invalid page/probe/payload indices: 0.
- Duplicate virtual or physical page owners: 0.
- Stale virtual/page/resource requests that mutate current state: 0.
- Request, page candidate, init list, indirect command, and counter overflow: 0
  in qualified profiles.
- Nonresident payload reads detected by validation instrumentation: 0.
- Dense fallback ring unavailable while sparse mode is active: 0 frames.
- Aborted/partial update exposing stale fine data: 0.
- Vulkan validation errors/warnings attributable to the feature: 0.

### 21.2 Memory

- Dense same-binary plan reconciles exactly.
- Sparse allocated bytes reconcile to physical capacity, not occupancy.
- Residency arena is <= 512 KiB for the current profile and <= 1 MiB at the hard
  virtual limit unless pre-approved from measured evidence.
- Topology-identical sparse saves at least the greater of 16 MiB or 10% of
  same-binary Dense live Simple-DDGI bytes; the target is at least 32 MiB.
- A densified candidate remains below its same-binary original Dense plan and
  retains at least 16 MiB net saving.
- Total tracked GPU memory remains <= 80% of the target 2 GiB profile.
- Stable frames create/destroy/rebind zero residency or payload resources.

### 21.3 Demand, churn, and latency

- Shadow predictor false-negative rate is predeclared and passes every qualified
  trajectory. Initial qualification targets <= 0.5% of instrumented opaque
  fine-page touches per frame at P95, with no demanded page missed for more than
  two consecutive rendered frames; any evidence-backed replacement threshold is
  frozen before sparse image review. Misses are also covered by receiver
  feedback.
- Proactive demand inflation is measured against the exact opaque gather-touch
  oracle; its P95 target is <= 1.5x pages, and the resulting retained working set
  must fit the selected pool without sustained pressure.
- Stationary settled admission and eviction counts are zero.
- Qualified ordinary motion has no sustained page-pool pressure.
- Page allocation occurs in the first reconciliation after accepted demand.
- First coherent fine publication P95 is <= 2 rendered frames for ordinary
  motion and <= 8 frames for a declared cut/teleport stress path, unless Phase 0
  freezes a stricter evidence-backed threshold.
- Fine misses during warmup are visibly supplied by a coarser ring; no black or
  stale-cell flash is accepted.
- Suppressed pages reactivate within the declared geometry-dirty/retry latency.

### 21.4 GPU and CPU performance

- Surface demand plus page reconciliation P95 target: <= 0.20 ms.
- Added topology-identical sparse forward-gather P95 cost: <= 0.15 ms and <= 5%
  of the corresponding Dense forward pass.
- Total Simple scheduler P95 remains <= 0.25 ms.
- Total GI GPU P95 remains <= 2.50 ms.
- Same-quality GPU frame P95 regression is <= 3%.
- Stable sparse render-thread work is O(1)/O(volume count), allocates zero managed
  bytes, performs no page/probe scan, and reads back only the fixed summary.
- Primary-ray and request budgets are never increased merely to hide residency
  latency.

### 21.5 Image quality

- Dense and Shadow are pixel-identical.
- Topology-identical Sparse passes the existing linear-HDR relative-RMSE gate of
  0.12 against the locked Dense reference.
- Raw DDGI mean luminance does not regress by more than 5% and dark-pixel
  fraction does not worsen by more than two percentage points in qualified
  steady scenes, unless an art-approved scene-specific exception is recorded.
- No visible page-grid pattern, fine/coarse seam, one-frame stale-cell flash,
  increased wall leak, temporal pumping, or camera-reversal ownership change is
  accepted in human review.
- The densified candidate must demonstrate a reviewed quality benefit at equal
  physical memory/ray budgets; fitting more virtual probes alone is not success.

## 22. Expected files

### 22.1 New files

- `Njulf.Rendering/Resources/SimpleDdgiProbePageCache.cs`
- `Njulf.Rendering/Resources/SimpleDdgiProbePageLayout.cs`
- `Njulf.Rendering/Resources/SimpleDdgiProbePageReferenceModel.cs`
- `Njulf.Rendering/Resources/SimpleDdgiResidencyCoverageValidator.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiPageDemandPass.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiPageResidencyPass.cs`
- `Njulf.Shaders/ddgi_simple_page_shared.glsl`
- `Njulf.Shaders/ddgi_simple_page_demand.comp`
- `Njulf.Shaders/ddgi_simple_page_classify.comp`
- `Njulf.Shaders/ddgi_simple_page_prefix.comp`
- `Njulf.Shaders/ddgi_simple_page_reconcile.comp`
- `Njulf.Shaders/ddgi_simple_page_initialize.comp`
- `Njulf.Shaders/ddgi_simple_page_feedback.comp`
- unit/ABI/validation test files listed in section 20.

The exact stage split may be reduced after the reference implementation, but do
not fuse stages if it obscures ordering, makes deterministic validation
impossible, or removes independent timing attribution.

### 22.2 Core files to update

- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`
- `Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs`
- `Njulf.Rendering/Resources/SimpleDdgiSampledAtlas.cs`
- GPU scheduler resource/layout/model files from the predecessor plan
- `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResource.cs`
- `Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs`
- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Shaders/ddgi_simple_shared.glsl`
- `Njulf.Shaders/ddgi_simple_trace.comp`
- `Njulf.Shaders/ddgi_simple_relocate_classify.comp`
- `Njulf.Shaders/ddgi_simple_transport.comp`
- `Njulf.Shaders/ddgi_simple_blend.comp`
- `Njulf.Shaders/ddgi_simple_publish.comp`
- `Njulf.Shaders/ddgi_simple_publish_sampled.comp`
- forward/transparent/foliage/fog/particle callers that emit bounded demand
- shader, struct-layout, bindless, graph, diagnostics, settings, snapshot,
  benchmark, and sample-option tests
- `RendererSettingsReference.md`
- `docs/rendering/ddgi-diagnostics.md`
- `docs/rendering/ddgi-runtime-validation.md`

Keep residency code out of the already very large volume manager except for
coordination, public properties, layout handoff, and resource binding.

## 23. Recommended PR sequence

### PR 1: contracts, address oracle, and exact memory model

- Dense-default settings/schema.
- Page/address/reference types.
- Virtual/physical memory-plan split.
- Pure allocator and exhaustive unit fixtures.
- No GPU resources or rendering change.

### PR 2: inert arena and graph plumbing

- Residency arena/layout, bindless index, graph resource, placeholders,
  generations, lifetime, fixed feedback, diagnostics.
- No-op passes and Vulkan lifecycle tests.
- Dense remains authoritative.

### PR 3: Shadow demand and working-set evidence

- Depth demand, actual-touch instrumentation, receiver feedback, reference/GPU
  compare, reports, predictor tuning.
- Dense rendering only.
- Freeze page size/capacity/retention gates.

### PR 4: physical-address-ready Dense pipeline

- Params/volume/queue ABI.
- Physical atlas/source-cache helpers.
- All consumers validate virtual and physical generations.
- Identity mapping; image output unchanged.

### PR 5: sparse mapping and gather fallback

- Deterministic page reconciliation and initialization.
- Near-ring page lookup, nonresident rejection, partial support, coarser fallback.
- Graphics-queue topology-identical sparse validation.

### PR 6: GPU scheduler/lifecycle/cohort integration

- Resident eligibility, page wakeups, update record emission, indirect consumers,
  exact convergence/source/atmosphere accounting, fixed shipping summary.
- Remove sparse CPU scans/uploads/readbacks.

### PR 7: suppression, fallback, and lifetime hardening

- Inactive-page suppression/retry.
- Geometry invalidation, cuts/teleports, runtime failure/re-entry, retirement and
  frames-in-flight stress.

### PR 8: optimization and production promotion

- Locked timings, memory, HDR, motion review, async audit, tier defaults, docs,
  implementation-status report.

### PR 9: optional near-density trade

- Equal-budget density experiment and independent approval.

Each PR must be independently reversible. Do not combine first physical
indirection, first authoritative eviction, GPU scheduler authority, and default
promotion in one change.

## 24. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Depth-only demand misses transparent/thin/discontinuous receivers | Stratified depth predictor, measured false-negative gate, bounded receiver feedback, coarse fallback |
| Page indirection slows the forward gather | Small cache-friendly table, fixed page geometry, early empty-cell fallback, profile distinct lookups/registers before optimizing |
| Page granularity retains too many inactive probes | Shadow comparison, all-inactive suppression, keep page size at eight unless evidence favors larger |
| Capacity pressure causes fine/coarse pumping | Retention hysteresis, deterministic priority, current-demand protection, admission limit, page-age/pressure gates |
| Camera cut cannot allocate because old pages are retained | Cut-aware pressure policy allows immediate eviction of non-current pages; coarser field remains valid |
| Reused source cache aliases a new owner | Clear cache validity words, validate virtual and page generations, reverse-map checks |
| Stale queue publishes into a reused physical page | Queue stamps physical address and mapping/resource generation; every stage and commit validates |
| Toroidal scroll invalidates preserved pages | Page table indexed from existing toroidal physical coordinate; exhaustive scroll/address tests |
| Sparse convergence never completes | Nonresident pages excluded explicitly; allocations/evictions adjust exact generation-stamped cohorts |
| Eviction falsely releases atmosphere state | Release only in GPU commit summary with matching cohort/page/source generations |
| Sparse pool saving is erased by scheduler/page metadata | Exact layout compiler accounting and 512 KiB/1 MiB arena gates |
| Actual resident count is reported as memory saved | Report physical capacity and concrete allocation bytes separately from occupancy |
| Runtime GPU fallback needs a dense pool | Freeze safe sparse state or disable fine volume and use dense coarser rings; no duplicate dense allocation |
| Densification overwhelms update bandwidth | Separate phase, equal ray/update budgets, publication-latency gate |
| Async compute races page-table readers | One serial Simple-DDGI ownership segment and explicit graph resource/timeline dependencies |
| Debugging changes residency and hides bugs | Debug views read only; explicit development pin/request is separate and reported |

## 25. Definition of done

The feature is complete only when:

- the virtual grids, volume selection, and toroidal mapping are unchanged;
- near-ring payload uses the bounded GPU page table/pool while mid/far remain
  dense;
- every receiver and update payload access uses a validated physical address;
- empty fine cells reliably compose through coarser rings without stale data,
  black flashes, seams, or new leaks;
- page demand, retention, allocation, eviction, suppression, and reactivation are
  deterministic and generation-safe;
- GPU-resident scheduling and exact source/convergence/atmosphere accounting
  understand residency;
- shipping stable frames do no per-page/probe CPU work or full readback;
- live bytes reconcile exactly and the sparse mode clears its minimum saving
  gate;
- three identity-locked repetitions clear CPU, GPU, memory, churn, latency, and
  HDR gates;
- human motion/cut/thin-wall/transparent review passes;
- unsupported/failure paths fall back to safe dense coarser lighting without a
  device wait;
- an implementation-status document records all code, shader, test, capture,
  memory, and promotion evidence;
- any near-field densification is approved independently under the same physical
  memory and ray budgets.

## 26. Explicit non-shortcuts

- Do not call classification state a page table.
- Do not retain full-size payload buffers and claim savings from low resident
  occupancy.
- Do not bind/unbind Vulkan memory per camera frame.
- Do not use a CPU-selected page list in shipping sparse mode.
- Do not read back page demand before scheduling updates.
- Do not sample a physical layer before validating residency and generations.
- Do not clear only the page table while leaving a stale queue able to publish.
- Do not merge nonresident and inactive semantics.
- Do not sparsify the last coarser fallback ring.
- Do not make environment fallback replace a valid coarser DDGI field.
- Do not let solver bounce demand recursively populate the whole fine volume in
  the first release.
- Do not lower capacity or retention after looking at the candidate image without
  regenerating the predeclared shadow evidence.
- Do not spend all measured savings on density and then report sparse memory
  success.
- Do not increase update/ray budgets to compensate for avoidable allocation
  churn.
- Do not promote the default from a stationary single-scene capture.
- Do not infer completion, retirement, or atmosphere release from frame age,
  resident count, or zero stale probes.
- Do not use `DeviceWaitIdle` for page capacity changes, fallback, descriptor
  rebinding, or retirement.
