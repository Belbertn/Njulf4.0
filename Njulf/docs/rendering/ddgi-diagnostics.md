# DDGI Diagnostics

DDGI diagnostics distinguish geometric coverage from usable lighting support.

## Forward Estimate Metrics

`spatial` is geometric volume and lattice coverage. A high value means DDGI volumes cover the shaded pixel.

`support` is usable probe support. It only rises when sampled probes are active and have irradiance alpha, quality confidence, and contribution weight.

`data` is the data confidence used for final ownership and composition.

`visibility` is the sampled visibility confidence for the strongest supported probe chain.

`leak` is final leak attenuation after visibility transport.

`effective` is the final DDGI trust after support, data confidence, and leak attenuation.

`rawLum` is raw DDGI diffuse luminance before final fallback composition.

`finalLum` is final composed indirect diffuse luminance.

`fallbackWeight` is the average environment fallback weight used by final DDGI composition. It should rise where DDGI support is low, but it must not hide a weak raw DDGI signal in production validation.

`ownership` is support-based DDGI ownership consumed for the pixel. Unsupported spatial coverage must not consume ownership.

## Scheduler Metrics

`requestBudget` is the intended probe request budget for the frame.

`primaryRayBudget` is the intended DDGI primary ray budget for the frame.

`ddgiDispatchCapacity` is the predicted GPU scheduler dispatch/request upper bound.

`ddgiActualRequests` and `ddgiActualPrimaryRays` come from GPU scheduler readback. They are reported as `pending` until a valid readback exists.

`candidates` is compacted scheduler candidate count. `stableSkipped` is the count of stable probes intentionally not emitted as candidates.

`candidateBufferOverflow` means the bounded scan generated more compacted candidates than the candidate output region can hold.

`bucketCapDrop` / `perBucketOverflow` means a priority bucket hit its local top-k capacity before global compaction. It is an admission-quality signal, not a hard buffer overflow.

`requestBudgetRejected` means finalize found more valid candidates than the request budget could accept.

`primaryRayBudgetRejected` means finalize stopped accepting candidates because the primary ray budget would be exceeded.

`traceDispatchGroups`, `traceProbeCount`, `traceRayCount`, `blendProbeCount`, `relocateClassifyProbeCount`, and `publishProbeCount` report the actual DDGI update workload. In GPU scheduler mode these values come from completed scheduler readback and are reported as pending until readback is valid. For the trace shader, one dispatch group maps to one updated probe.

## Packed Storage and Sampled Mirror

`SimpleDdgiStorage` is the authoritative capture record for the active representation. It is copied from the same compiled layouts used for allocation and shader metadata; consumers must not derive canonical bytes by subtracting an optional mirror from an aggregate.

- `PackingMode`, `AbiVersion`, `DirectionCodebookVersion`, `StorageLayoutFingerprint`, and `MirrorLayoutFingerprint` identify the byte/address contract. A change is a cold generation boundary.
- `CanonicalIrradianceFormat/Bytes` report the two-word `RGBA16F` texel; `CanonicalVisibilityFormat/Bytes` report the one-word `RG16F` moment texel. Visibility validity is probe-state bit 5, not a duplicated texel channel.
- `SourceCacheLegacyBytes`, `SourceCacheCompact28Bytes`, `SourceCacheCompact24Bytes`, their ray counts, and `SourceCacheAlignmentBytes` account for every mixed, 16-byte-aligned per-volume region.
- `Fp16DistanceEligible*` and `Fp32Distance*` distinguish volumes whose range/ULP/thickness gates selected Compact-24 from those retaining Compact-28 FP32 distance.
- `RayScratchStrideBytes` is 32 for `Legacy`/`Validate` and 20 for `Packed`; `RayScratchBytes` is the exact provisioned work allocation.
- Mirror counts are separate: requested, policy-eligible, budget-admitted, and 256-layer-rounded provisioned probes. Irradiance, visibility, logical total, actual allocator bytes, excluded identities, coverage mode, allocation generation, and fallback reason are reported independently.

Detailed validation builds additionally populate `ValidationCounters`. The logical counters are backed by a dedicated, double-buffered 256-byte validation bank, and each completed bank carries a transfer-written frame sentinel; a report is valid only when that sentinel and the completed-frame slot agree. This keeps validation atomics at low physical offsets without consuming production diagnostic-buffer space. Normal Release/ShippingPerformance shaders compile these atomics out. Ordinary pack, direction, and mirror-path observations use the same deterministic 1/64 sampling stride; non-finite, saturation, invalid-epoch, invalid-hit-kind, and invalid-map failures are counted exactly so a zero-error gate cannot miss an exception.

- `MirrorInteriorOpportunityCount`, `MirrorImageHitCount`, `MirrorSeamFallbackCount`, `MirrorUnmirroredFallbackCount`, and `MirrorInvalidMapFallbackCount` classify sampled-mirror opportunities. Image hits include both interior and octahedral-boundary samples. Seam and policy fallbacks are normal; invalid-map fallback must remain zero.
- `MirrorBoundaryImageHitCount` is the subset of image hits whose original, unpadded bilinear footprint crosses an octahedral boundary. Divide it by `MirrorImageHitCount` to estimate boundary traffic; `MirrorSeamFallbackCount` counts missing-image boundary samples instead. Both hit counters use the same 1/64 selection. Detailed builds log accumulated hit/boundary counts every 64 distinct valid readback frames. These observations are not production timing measurements.
- `CachePackAttemptCount`, non-finite/saturation counts, and maximum radiance/distance errors qualify FP16 writes. `InvalidSourceEpochCount` and `InvalidHitKindCount` prove that malformed scratch/cache metadata failed closed. All four failure/saturation counts must be zero for promotion.
- Direction comparison samples, epoch mismatches, maximum angular error, histogram, and the conservative P99 bucket bound qualify the codebook while `Validate` retains the stored direction. Epoch mismatches must be zero.

Sampled irradiance and visibility images retain 8x8 and 16x16 interiors inside
10x10 RGBA16F and 18x18 RG16F array layers. The one-texel borders use the canonical
octahedral mirror rule, including opposite corners, and are populated during
both queued publication and full synchronization. Sampling uses the interior
coordinate transformed by `(uv * N + 1) / (N + 2)` and hardware bilinear filtering
at LOD zero. Canonical SSBO payloads remain unpadded. Sampled payload is 2,096
bytes per provisioned probe (previously 1,536); allocated-byte reporting also
includes device alignment. Existing mirror budgets and SSBO fallback still apply.

`MirrorAllocatedBytes` may exceed logical payload bytes only because of explicit allocator alignment. A zero allocated value with nonzero admitted bytes means image creation fell back; inspect `MirrorFallbackReason`. Budget evaluation uses these explicit resources and prefers actual mirror allocation bytes when available.

## Sparse Probe Residency

`SimpleDdgiProbeResidency.Mode` distinguishes three contracts:

- `Dense` uses identity physical addressing and does not run a page transaction.
- `Shadow` keeps dense payload addressing while the depth predictor and representative receiver gathers stamp virtual near-ring pages for qualification.
- `SparseNearRing` makes the bounded near-ring page table authoritative. Authored, mid, and far volumes remain dense, and a containing dense coarser ring supplies missing fine ownership.

Shipping residency feedback is a generation-stamped 1 KiB summary copied only after scheduler commit. It never contains a page list. `FeedbackValid=false` means the first fence-complete summary is pending or a stale resource generation was rejected; it does not mean a zero-valued page population.

The key capacity fields are:

- `VirtualProbeCount` and `VirtualPageCount`: unchanged topology and the near-ring 2×2×2 page grid.
- `DensePhysicalProbeCount`: payload reserved for dense authored/mid/far volumes.
- `SparsePhysicalPageCapacity` and `PhysicalProbeCapacity`: immutable allocation capacity, independent of current occupancy.
- `ResidentPageCount`, `FreePageCount`, `InitializingPageCount`, and `PublishedPageCount`: current mapping lifecycle.
- `OrdinaryAllocationToPublication*Frames` and `CutAllocationToPublication*Frames`: independent P50/P95/maximum first-publication latency distributions; the aggregate fields remain available for continuity.
- `GpuSimpleDdgiPageDemandMicroseconds`, `GpuSimpleDdgiPageResidencyMicroseconds`, and `GpuSimpleDdgiPageFeedbackMicroseconds`: independently timestamped page stages.
- `NonResidentVirtualProbeCount`, `ActiveResidentProbeCount`, `InactiveResidentProbeCount`, and `ConvergedResidentProbeCount`: distinct residency, geometry-classification, and solver populations.

Memory evidence reports `PhysicalPayloadBytes`, `PageArenaBytes`, `FeedbackReadbackBytes`, `DenseEquivalentBytes`, `AllocatedCapacityBytes`, and `AvoidedBytes`. Layout telemetry additionally reports edge-page padding and sampled-atlas 256-layer rounding. Savings always compare concrete capacity allocations; a low resident count is never reported as released memory.

Demand and churn evidence includes visible/receiver page counts, request overflow, retained age buckets, admissions, evictions, failed admissions, pressure streaks, suppression/retry counts, and allocation-to-schedule/publication P50/P95/max. Ring records keep virtual, resident, active, inactive, demanded, and converged populations separate.

Correctness counters must remain zero in a qualified capture:

- page-table/reverse-map disagreement;
- duplicate virtual or physical owners;
- stale virtual, mapping, or resource requests that reach mutation;
- out-of-range requests and bounded-request overflow;
- nonresident payload-read validation failures.

`NonResidentGatherRejectionCount` is expected during warmup or pressure and proves the fine payload was rejected before access. `CoarserFallbackCount` should rise with it when a dense containing ring supplies the missing ownership. A rejection without usable coarser fallback is a correctness failure, not a normal sparse miss.

### Shadow predictor evidence

In `Shadow`, the depth pass stamps predicted opaque pages and rate-limited receiver gathers stamp instrumented actual pages while all rendering remains dense. `PredictorComparisonValid` gates `PredictorFalseNegativePageCount`, `PredictorFalsePositivePageCount`, `PredictorFalseNegativeRate`, and `PredictorInflationRatio`. The initial trajectory gates are false-negative P95 ≤ 0.5%, inflation P95 ≤ 1.5×, and no page missed for more than two consecutive rendered frames.

`SimpleDdgiResidencyWorkingSetAnalyzer` consumes validation page sets and exports an atomic JSON report containing per-frame unique demand, candidate retention working sets, 2×2×2 versus offline 4×2×4 geometry, coverage errors, P50/P95/P99/max pool requirements, deterministic capacity/churn simulations, and exact memory projections. This is offline qualification tooling; it is not used by the shipping scheduler.

### Runtime failure state

`MutationFrozen=true` means page eviction/reassignment has stopped. If `ResidencyStateValid=true`, the last validated complete map remains readable while a controlled retry is prepared. If false, the sparse fine volume fails closed and receivers use dense coarser lighting. `SparseAuthoritative`, `FallbackReason`, and the `simple-ddgi-probe-residency` feature-state row reflect this distinction. Re-entry always creates a new residency resource generation at a frame boundary.

## Residency Debug Views

- `DdgiProbeResidency`: virtual probe markers for published, fresh, demanded-missing, retained, suppressed, and nonresident states. Nonresident probes remain visible as neutral/hollow markers.
- `DdgiResidencyFallback`: missing fine ownership and the coarser ring that supplied it.
- `DdgiPageAge`: retention age and pressure visualization.
- `DdgiPhysicalPage`: physical page identity and mapping generation for validation.

The views are observational. They do not pin or request pages. Development tools must explicitly call `TrySetSimpleDdgiProbeResidencyDevelopmentPin` or `SetSimpleDdgiProbeResidencyDevelopmentFreeze`; these APIs require debug tooling to be enabled. Pin state, command count, last command, and development freeze state are exported in residency telemetry. Runtime-failure freeze is a separate latch and cannot be cleared through the development API.

## Content-Dependent DDGI Additions

`ContentDependentDdgi` is the capture-stable authority for the many-light, directional-radiance, dynamic-geometry, transparency, and foliage additions. It is present both in `RendererDiagnostics` and at the top level of a performance snapshot. Inspect it before interpreting counters:

- `ConfiguredFeatures` and every `Requested*Mode` record persisted/preset intent.
- `ActiveFeatures` and every `Effective*Mode` record the independently qualified behavior used by the frame.
- `DirectionalRadianceFallbackReason`, `RaySceneFallbackReason`, `FoliageFallbackReason`, and `SettingsMigrationDiagnostic` explain every downgrade. A requested feature with a weaker effective mode and no reason is a capture failure.
- `LightBufferRevision`, `LightTreeTopologyRevision`, `LightTreeContentRevision`, `RaySceneResourceGeneration`, `RaySceneContentEpoch`, `SceneContentRevision`, `MaterialContentRevision`, `SourceLightingEpoch`, and `SamplingSequenceEpoch` distinguish resource lifetime from content invalidation.
- `StochasticHashAbiVersion`, `DirectionalRadianceAbiVersion`, `RayQueryInstanceAbiVersion`, and `RayQueryInstanceRecordBytes` make replay/addressing compatibility explicit.

Reference modes (`LegacyTopKReference`, `L1Reference`, and `RecursiveExperimental`) must show explicit validation authorization. They are not shipping fallbacks.

### Many-light tree and estimator

`LightTree` reports requested/effective mode, qualification, build action/reason, local-light/leaf/node/depth counts, complete publication revisions, generation, planned/allocated/peak/retired bytes, build/refit/reuse/bypass totals, allocation failures, publication-validation failures, and readback readiness.

The single-sun/no-local fast path must show `BypassEmpty`, zero planned/allocated tree bytes, and no build/refit/traversal work. For a live tree, `PublicationFeedbackPending=true` means the finalized state copy is still owned by an in-flight frame fence; `PublicationValid` becomes authoritative only after the completed readback matches all revisions, bank, generation, counts, and checksum. A mismatch rejects the publication, increments `PublicationValidationFailureCount`, requests a new inactive-bank build, and uses the exact safety path.

`ManyLightCounters` contains bypass/exact/tree-attempt/tree-success/tree-fallback hits; sampled and duplicate draws; visibility evaluations; zero-term rejects; uniform repairs; invalid sample/PDF repairs; and exact light evaluations. It also exposes decoded mean, geometric-mean, and minimum PDF plus maximum finite estimator weight. Duplicate draws are valid independent samples and must not be treated as an error. `UniformRepairCount` may rise for deliberately malformed validation inputs; sustained repairs or any `InvalidSampleOrPdfCount` fail a qualified capture.

### Directional radiance and glossy ownership

Requested/effective directional and glossy modes are independent. `DirectionalRadianceBudgetBytes`, `DirectionalRadiancePlannedBytes`, `DirectionalRadianceAllocatedBytes`, and `DirectionalRadianceParityBytes` reconcile the canonical sidecar and the extra previous-generation parity required by glossy modes. A failed sidecar never disables diffuse DDGI: effective directional/glossy mode becomes off, the DDGI rough-reflection weight becomes zero, and SSR/reflection-probe/environment ownership continues.

`GpuSimpleDdgiDirectionalRadianceMicroseconds` covers the complete ordered
prepare/project/publish sequence. It runs after diffuse blend and before
canonical probe publication. The projector accumulates RGB L2 coefficients in
FP32, one bounded source scan per probe, then the publisher validates the
fingerprinted scratch record, blends compatible history, packs the 64-byte
FP16 sidecar, and performs the final validity store. The sequence reuses the
first 128 bytes of each queue-local ray-scratch allocation only after every
diffuse consumer has finished; there is no persistent FP32 sidecar allocation.

A structurally valid record with `validSampleCount=0` is a checked zero-support
publication, for example when every cached hit is an authored one-sided reverse
face. It allows the diffuse transaction to commit but is rejected as a
directional receiver source, so reflection/environment fallback retains
ownership. Do not classify this state as projection corruption.

The scheduler's aggregate `producerFailureMask` reserves directional witnesses
8 through 14 for capacity, missing work, projection, scratch, publication,
header, and source failures respectively. Any such bit fails the transaction;
all must be zero in a qualified capture. `missingCompletionMask` and
`transactionPredicateMask` must also be zero. Trace-only many-light push flags
share physical bits with cached-solve sweep controls by design, so they must
never appear on transport, blend, directional, or publish dispatches.

`SimpleDdgiRoughSpecularSampleCount` and `SimpleDdgiRoughSpecularNonzeroCount` show receiver attempts and accepted nonzero DDGI ownership. A nonzero count below the configured roughness band, a non-finite SH record reaching a receiver, or simultaneous full DDGI and environment ownership is a correctness failure.

Use the reflection debug view `DdgiDirectionalRadianceLobe` to inspect the SH
sidecar evaluated in each receiver's reflection direction before the BRDF.
`SourceOwnership` displays normalized local-reflection, directional-DDGI, and
environment weights as red, green, and blue; the channels must sum to one.

### Ray geometry, transparency, decals, and foliage

`RaySceneResourceGeneration` changes only when GPU resource identity/layout changes. `RaySceneContentEpoch` changes for pose, material, proxy, transform, or semantic content and remains stable across ordinary frame-slot/TLAS-buffer rotation. Dynamic-AS diagnostics report full builds, refits, proxy/exclusion/budget fallbacks, topology mismatches, primitive/scratch/storage/peak/retired bytes, and BLAS/TLAS CPU/GPU timings.

Geometry decision counters distinguish stochastic-alpha accepts/rejects, deterministic transparent visibility layers/limits, thin-surface termination and unsupported hits, decal candidates/retention/association/rejection/overflow, invalid ray metadata, and foliage-proxy hits. Alpha decisions use stable probe/ray/source/instance/primitive identity rather than frame number. Geometry decals are overlays only and must never occlude a primary, visibility, or shadow ray. Thick refraction, nested dielectric paths, caustics, and participating volumes remain unsupported and must report a proxy or exclusion.

The `Foliage` performance section reports GPU patch/vertex/index bytes, GPU generation and CPU record/upload time, requested and represented instance counts, density error, wind age, near/mid/far card counts, excluded patches, cadence/content signatures, LOD policy version, and fallback reason. Tier selection is based on authored probe influence in world space; camera motion alone must not change the signature, tier counts, or generated placement.

### Advanced-GI experiment telemetry

`GiRoadmapExperiments` reports the requested, supported, admitted, effective,
qualified, resource-complete, and fallback state for B1/C1/C3/C4/C5. Treat an
effective mode as a resource-lifetime state, not a quality result: promotion
also requires the immutable device/driver/shader/content evidence identified by
the mode's qualification ID. Rejected or disabled modes report zero allocated
bytes, descriptors, and pass work.

B1 is reported in `ReceiverFeedbackRuntime`. `Readable` means the exact
64-byte summary header was copied after the submission, observed after its
fence, and validated against the current allocation/generation/layout. Inspect
append/capacity utilization, probe and fallback partial/summary counts,
dropped/invalid records, producer-overflow mask, publication generation, and
the five bounded timing regions. Any drop, producer overflow, invalid record,
stale generation, or incomplete flag clears publication authority and leaves
ordinary scheduler quotas in control. The record/sort/summary memory entries
are the same central categories used by `SimpleDdgiContentMemory`.

The C5 `SimpleDdgiNearFieldResidual` snapshot payload is schema-versioned and
has an explicit readback state. Only `Available` with both timestamp and
counter readback valid is a measured GPU sample. `PendingRendererIntegration`
can report an admitted CPU memory plan but deliberately reports zero live
allocation, timings, counters, energy, and capture IDs; it must never be used
to claim a C5 implementation ran. The renderer records dedicated reset,
prepare, trace, temporal, optional filter/frequency, composite, and finalize
passes only after evidence-bound admission. The V13 finalize dispatch reduces
immutable per-tile records and publishes the completion word last; no earlier
workgroup may claim whole-frame completion. Snapshot migrations never infer C5
telemetry from a mode bit.

Starting another frame does not erase the last common-frame sample. An
`Available` sample remains authoritative while a newer readback is in flight;
`AgeFrames` advances and the status states that newer work is pending. A valid
positive timestamp delta also remains available when its display conversion
rounds below one microsecond to `0 us`. If a required timestamp is genuinely
absent, the pending status names the exact pass instead of presenting the whole
stage set as zero-cost work.

A single fence-complete validation failure retains the last valid witness and
increments the recovery streak. The last rejected stage-specific integrity
reason remains visible after a later valid frame so an intermittent fault cannot
erase its own evidence. Three consecutive failures suppress new C5 work and
request one fresh generation even when the layout is unchanged. The replacement
has eight frames to produce an authoritative witness. A failed rebuild or missed
deadline enters fence-backed terminal retirement; effective mode may become
`Off/ResourceIncomplete` only after active, pending, and retired C5 allocations
reach zero live bytes. Adaptive tiers encode compacted tile identities with the
active trace-width stride, not the larger admitted-allocation stride; trace,
temporal, finalization, and CPU validation therefore address the same dense tile
domain after a resolution transition.

For an authoritative C5 frame, inspect its exclusive source/raw-trace/temporal/
filter/composite/finalize timings together with exact cosine/guided proposal,
valid-sample, rays/hits/misses, bounded-step and mip-visit reject, history
accept/reject, variance clipping, signed and absolute residual energy, tile
compaction/overflow, and requested/admitted/allocated/peak/retired byte
counters. Proposal totals must equal cosine plus guided proposals; guided zero
samples must be a subset of valid guided proposals; rays and valid samples may
not exceed proposals. A non-finite statistic, incomplete readback, stale
source/layout identity, or missing reference capture is a failed qualification
sample, not a zero-cost or zero-error result.

Performance snapshot schemas add these contracts incrementally: v5 C5, v6/v7
C1 lifecycle/content, v8 C3, v9 C4, and v10 B1. Opening an older schema
explicitly zeroes only the telemetry that did not yet exist while preserving
historical mode intent for inspection.

The sample's compact diagnostics output mirrors these authoritative fields.
Its roadmap line prints requested-to-effective mode and fallback state for all
five experiments. The following B1 line prints publication generation/frame,
append/drop/capacity utilization, probe and fallback reductions, invalid and
overflow masks, the five GPU stage timings, and exact allocated/peak/retired
bytes. This line is operational telemetry only: it does not replace the
qualification manifest or the locked capture corpus.

## Transport audit lifecycle

`SimpleDdgiTransportConvergence.TailAuditLifecycleEvents` retains the last 64
audit transitions; `TailAuditLifecycleDroppedEventCount` reports evictions.
Snapshots are immutable and reused until the next transition. An unchanged
certified field does not enter camera-admission bookkeeping, and cancelling
an audit that is no longer frozen cannot revoke its certificate. Real field
generation changes still invalidate certification.

Each event identifies the admission trigger and certification/source-cache
reason, the full frozen and currently observed generation tuples, and the
logical volume-table generation at admission and observation. Compare the
actual field/source/ownership generations when investigating repeat work;
different audit IDs alone do not prove that the input changed.

`Started`, `FirstSubmitted`, and `DispatchComplete` distinguish admission from
GPU command submission. `Certified` or `Rejected` records CPU consumption of
the completed readback; `Cancelled` and `TimedOut` record other termination.
`ElapsedMicroseconds` measures admission to that observation, including
scheduling and readback delay. Frame serials use the DDGI scheduler domain,
which can differ from renderer/benchmark frame indices. Chunk counts distinguish
partial dispatch from completed coverage. The camera-motion gate only defers
new admission; a frozen audit continuing during motion is expected.

Benchmark reports deduplicate retained transitions into `DdgiAuditLifecycleEvents`
(including retained warmup history). `DdgiAuditActiveGpuFrameMilliseconds` and
`DdgiAuditIdleGpuFrameMilliseconds` summarize measured GPU frame timing using
aligned completed-frame command intent. Unaligned or invalid samples are
excluded. These are the existing GPU frame pass-sum measurements, not exclusive
kernel execution. Compare p95/p99/max alongside audit completion latency;
different camera/convergence cohorts do not establish an audit's causal cost.
Lifecycle diagnostics remain outside the versioned transient certification
payload and its semantic digest.

## Troubleshooting

| Symptom | Likely area |
| --- | --- |
| `spatial` high, `support` low | Probe data, irradiance confidence, quality confidence, or warmup scheduling |
| `support` high, `effective` low | Final composition, leak attenuation, visibility, or AO interaction |
| `gatherFallback` high | Gather tile assignment or volume coverage |
| `candidateBufferOverflow` high | Scan-list quota or candidate output capacity |
| `bucketCapDrop` high | Priority bucket top-k cap or source quota distribution |
| `requestBudgetRejected` high | Request budget too small for current warmup/dirty workload |
| `primaryRayBudgetRejected` high | Primary ray budget too small for selected probes |
| `PredictorFalseNegativeRate` high in Shadow | Depth sampling, discontinuity coverage, or sample-bias mismatch |
| `PredictorInflationRatio` high in Shadow | Predictor is over-conservative and may erase the sparse working-set benefit |
| `FailedAdmissionCount` or pressure streak growing | Page capacity/retention mismatch or cut/teleport stress; inspect page age and fallback |
| duplicate owner or reverse disagreement nonzero | Invalid residency transaction; mutation should freeze and the capture fails |
| `MutationFrozen=true`, `ResidencyStateValid=false` | Sparse fine addressing was disabled; inspect `FallbackReason` and confirm dense coarser output |
| nonresident rejections without coarser fallback | Dense containing-ring invariant or gather ownership composition failure |
| `rawLum` high, `finalLum` low | Final composition or suppression mask |
| `finalLum` visible, `rawLum` weak, `fallbackWeight` high | Environment fallback is masking weak DDGI bounce |
| `ddgiActualRequests=pending` | First readback frames have not completed yet |
| mirror opportunities high, image hits low | Coverage policy excludes an important volume class, budget admitted only a prefix, or seam traffic dominates |
| `MirrorInvalidMapFallbackCount` nonzero | Mapping generation, descriptor group/layer bounds, or compact range publication mismatch |
| packed cache repairs increase | Epoch/source-generation identity, mixed-region address, or FP16 input validation failure |
| Compact-24 absent for a volume | Its maximum range, half ULP, hit offset, architectural thickness, or synthetic exponent-boundary gate rejected FP16 distance |
| requested/effective content mode differs | Inspect the matching fallback reason and the per-device rollout feature mask; reference modes also require validation authorization |
| tree requested but `BypassEmpty` | No eligible local lights; this is the required zero-allocation single-sun path |
| `PublicationFeedbackPending=true` | The frame-slot fence has not completed the 64-byte tree-state readback yet |
| publication-validation failures nonzero | Stale revisions, wrong bank/generation/counts, or state checksum mismatch; the tree was rejected and a rebuild/exact fallback was requested |
| many-light invalid sample/PDF nonzero | Malformed publication, traversal, or PDF mixture; capture fails even if final radiance looks plausible |
| directional requested but effective off | Sidecar qualification, exact memory admission, allocation, ABI, or publication failed; diffuse DDGI should remain active |
| directional time is nonzero but commit reports missing blend completion | Trace-only many-light push flags leaked into cached-solve sweep bits, or the blend-to-directional compute barrier/order was violated |
| directional record is valid with zero samples | Checked zero directional support; diffuse may commit, while directional reflection ownership must remain zero |
| DDGI rough-specular samples but zero nonzero ownership | Receiver roughness/confidence is outside the qualified band or higher-priority SSR/reflection-probe ownership is valid |
| dynamic budget deferrals/topology mismatches grow | BLAS storage/scratch/build/primitive limits or a topology signature change forced rebuild/proxy/exclusion |
| transparent layer/candidate limit nonzero | Scene exceeded its bounded deterministic transparency contract; inspect conservative overflow frequency before promotion |
| decal candidate limit nonzero | More overlays intersected a ray than the deterministic retained set can hold; sustained overflow fails qualification |
| foliage density error or excluded patches high | Proxy triangle budget or probe-space far-tier policy removed representation; inspect the explicit foliage fallback and tier counts |
| foliage signature changes during camera-only motion | Camera data leaked into proxy generation/LOD; this is a correctness failure |

## Required Ownership Invariant

Spatial coverage alone must not suppress environment fallback. If a frame reports:

```text
spatial=1.000
support=0.000
effective=0.000
```

then `ownership` must also be `0.000`, and environment fallback must remain available.
