# DDGI Runtime Validation

This checklist validates the DDGI implementation in the runtime scenes after shader and scheduler changes.

## Required Scenes

- `GiSponzaRightWallStationary`: shadowed arcade/alley support, raw diffuse, and fallback behavior.
- `GiCornellRoom`: default camera-relative DDGI clipmap support and colored bounce.
- `GiLongCorridorOcclusion`: thin-wall visibility and leakage behavior.
- `GiLocalVolumeStreaming`: camera-relative clipmap scrolling and local volume stream-in/out.
- `GiFastTraversalTeleport`: camera cut recovery and cache warmup reset.

## Debug Buffers

Capture these debug views for each scene after cold start and again after at least 120 steady frames:

- `DdgiRawDiffuse`
- `DdgiSampledIrradiance`
- `DdgiFinalDiffuse`
- `DdgiConfidenceBypass`
- `DdgiEffectiveWeight`
- `DdgiSupportCoverage`
- `DdgiCoverage`
- `DdgiSuppressionMask`
- `DdgiVisibilityMoments`
- `DdgiGatherLocalVolume`
- `DdgiGatherClipmap`

## Metrics To Record

- `spatial`
- `support`
- `data`
- `visibility`
- `effective`
- `rawLum`
- `finalLum`
- `fallbackWeight`
- `ownership`
- `ddgiActualRequests`
- `ddgiActualPrimaryRays`
- average rays per request: `ddgiActualPrimaryRays / max(ddgiActualRequests, 1)`
- `ddgiProbeStateBufferBytes`
- `candidateBufferOverflow`
- `bucketCapDrop` / `perBucketOverflow`
- `requestBudgetRejected`
- `primaryRayBudgetRejected`
- `traceDispatchGroups`
- `traceProbeCount`
- `traceRayCount`
- `blendProbeCount`
- `relocateClassifyProbeCount`
- `publishProbeCount`
- `gpuDdgiUs`
- `gpuDdgiScheduleP95Us`
- DDGI update P95 by pass: `DdgiSchedulePass`, `DdgiTracePass`, `DdgiBlendPass`, `DdgiRelocateClassifyPass`, `DdgiPublishPass`

## Acceptance Checks

- `gatherFallback` remains zero in DDGI-covered regions.
- `spatial` can be high while support warms up, but `support`, `data`, and `effective` become nonzero after warmup.
- `ownership` stays zero when `support` and `data` are zero.
- Environment fallback remains nonzero where DDGI support is low.
- `rawLum` is nonzero in covered shadowed regions after warmup.
- Phase 9 weak-bounce gates fail when raw atlas/sample/blend energy is healthy but `finalLum` collapses, when visible final diffuse is dominated by high `fallbackWeight` while DDGI raw/effective contribution is weak, when emissive scenes have no emissive bounce signal, or when thin-wall/leak scenes bypass leak attenuation to recover brightness.
- `traceProbeCount == ddgiActualRequests`.
- `traceRayCount == ddgiActualPrimaryRays`, including GPU-scheduler adaptive ray buckets.
- `relocateClassifyProbeCount == ddgiActualRequests`.
- Average rays per request drops for steady cameras and rises for dirty, low-confidence, or high-inconsistency probes without exceeding the primary-ray budget.
- With `DdgiProbeL1MetadataEnabled=true`, probe state allocation includes the compact representation metadata vector, while diffuse output still comes from the fixed irradiance atlas.
- `DdgiSelfShadowBiasScale=1.0` and `DdgiHysteresisResponse=1.0` preserve authored volume behavior; non-default values are visible in sample diagnostics and change only self-shadow bias/history response, not final intensity.
- Phase 7 production scenes cover Sponza interior, sunlit courtyard, colored bounce room, thin-wall corridor, emissive room, moving rigid object, moving local light, camera teleport/scroll, and outdoor foliage/plaza.
- `candidateBufferOverflow` is zero in steady camera. `bucketCapDrop` / `perBucketOverflow` may be nonzero when local scheduler bucket caps trim candidates, but the measured value must be bounded and explained by scheduler admission policy.
- `gpuDdgiScheduleP95Us` and split DDGI update timings remain within the selected quality budget.
- Phase 8 tier budgets are enforced by P95: `DdgiLow <= 0.75 ms`, `DdgiMedium <= 1.0 ms`, `DdgiHigh <= 1.5 ms`, and `DdgiUltra <= 2.5 ms`.
- DDGI atlas memory budgets do not exceed the selected tier target: 64 MB, 128 MB, 192 MB, or 384 MB.
- Emergency degradation reduces work while preserving visible, dirty, and new-probe near-field updates before spending remaining work on off-frustum safety refresh.

## Automation Hooks

The runtime scenario, metric, debug-buffer, and gate definitions live in `NjulfHelloGame/SampleGlobalIlluminationValidation.cs`. The console diagnostic names are intentionally aligned with `docs/rendering/ddgi-diagnostics.md` so captured logs and benchmark reports use the same terms.
