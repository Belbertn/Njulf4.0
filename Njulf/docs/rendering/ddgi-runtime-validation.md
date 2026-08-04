# Simple DDGI Runtime Validation

This checklist validates the active Simple DDGI implementation in runtime scenes. The retired full-DDGI scheduler and SSGI pipeline are not part of this validation.

## Required Scenes

- `GiSponzaRightWallStationary`: shadowed arcade/alley support, raw diffuse, and fallback behavior.
- `GiCornellRoom`: colored bounce and receiver coverage.
- `GiLongCorridorOcclusion`: thin-wall visibility and leakage behavior.
- `GiFastTraversalTeleport`: camera-cut recovery and Simple DDGI warmup.

## Debug Buffers

Capture `FinalIndirect`, `DdgiIrradiance`, `DdgiSampledIrradiance`, `DdgiFinalDiffuse`, `DdgiRawDiffuse`, `DdgiCoverage`, `DdgiSupportCoverage`, `DdgiDataConfidence`, `DdgiVisibilityMoments`, and `DdgiUpdateReasons` after cold start and after at least 120 steady frames.

## Metrics To Record

- Simple DDGI active probe count, updated probe count, and primary ray count.
- Scheduler request and primary-ray budgets, plus any rejected work.
- Recenter, atlas-clear, atlas-fresh, and warmup state.
- Trace, transport, blend, relocation, and publish timings.
- DDGI atlas/buffer memory and the configured tier budget.

## Acceptance Checks

- Simple DDGI publishes visible diffuse bounce in covered shadowed regions after warmup.
- Support, data confidence, and effective contribution become nonzero after warmup; environment fallback remains available where support is low.
- Scheduled requests and primary rays remain within their configured hard budgets.
- Recenter, atlas clear, and camera-cut frames are treated as transient evidence rather than steady-state regressions.
- Simple DDGI timing and storage remain within the selected quality tier budget.
- Emergency degradation preserves visible, dirty, and newly exposed near-field updates before background refresh work.

## Automation Hooks

The runtime scenario, metric, debug-buffer, and gate definitions live in `NjulfHelloGame/SampleGlobalIlluminationValidation.cs`. The console diagnostics are aligned with `docs/rendering/ddgi-diagnostics.md`.
