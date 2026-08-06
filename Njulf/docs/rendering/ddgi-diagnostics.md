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

- `MirrorInteriorOpportunityCount`, `MirrorImageHitCount`, `MirrorSeamFallbackCount`, `MirrorUnmirroredFallbackCount`, and `MirrorInvalidMapFallbackCount` classify every sampled-mirror opportunity. Seam and policy fallbacks are normal; invalid-map fallback must remain zero.
- `CachePackAttemptCount`, non-finite/saturation counts, and maximum radiance/distance errors qualify FP16 writes. `InvalidSourceEpochCount` and `InvalidHitKindCount` prove that malformed scratch/cache metadata failed closed. All four failure/saturation counts must be zero for promotion.
- Direction comparison samples, epoch mismatches, maximum angular error, histogram, and the conservative P99 bucket bound qualify the codebook while `Validate` retains the stored direction. Epoch mismatches must be zero.

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

## Required Ownership Invariant

Spatial coverage alone must not suppress environment fallback. If a frame reports:

```text
spatial=1.000
support=0.000
effective=0.000
```

then `ownership` must also be `0.000`, and environment fallback must remain available.
