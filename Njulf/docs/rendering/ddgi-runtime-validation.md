# DDGI Runtime Validation

This checklist validates the DDGI implementation in the runtime scenes after shader and scheduler changes.

## Required Scenes

- `GiSponzaRightWallStationary`: shadowed arcade/alley support, raw diffuse, and fallback behavior.
- `GiCornellRoom`: local dense volume support and colored bounce.
- `GiLongCorridorOcclusion`: thin-wall visibility and leakage behavior.
- `GiLocalVolumeStreaming`: camera-relative clipmap scrolling and local volume stream-in/out.
- `GiFastTraversalTeleport`: camera cut recovery and cache warmup reset.

## Debug Buffers

Capture these debug views for each scene after cold start and again after at least 120 steady frames:

- `DdgiRawDiffuse`
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
- `ownership`
- `ddgiActualRequests`
- `ddgiActualPrimaryRays`
- `candidateBufferOverflow`
- `perBucketOverflow`
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

## Acceptance Checks

- `gatherFallback` remains zero in DDGI-covered regions.
- `spatial` can be high while support warms up, but `support`, `data`, and `effective` become nonzero after warmup.
- `ownership` stays zero when `support` and `data` are zero.
- Environment fallback remains nonzero where DDGI support is low.
- `rawLum` is nonzero in covered shadowed regions after warmup.
- `traceProbeCount == ddgiActualRequests`.
- `relocateClassifyProbeCount == ddgiActualRequests`.
- `candidateBufferOverflow` and `perBucketOverflow` are zero in steady camera, or the measured nonzero values are bounded and explained.
- `gpuDdgiScheduleP95Us` and split DDGI update timings remain within the selected quality budget.

## Automation Hooks

The runtime scenario, metric, debug-buffer, and gate definitions live in `NjulfHelloGame/SampleGlobalIlluminationValidation.cs`. The console diagnostic names are intentionally aligned with `docs/rendering/ddgi-diagnostics.md` so captured logs and benchmark reports use the same terms.
