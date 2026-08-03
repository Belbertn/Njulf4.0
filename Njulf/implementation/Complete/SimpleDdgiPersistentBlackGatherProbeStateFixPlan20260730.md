# Simple-DDGI Persistent Black Gather/Probe-State Fix Plan

## Problem

With a stationary camera and unlimited warm-up time, opaque Sponza receivers remain black in both
`DdgiIrradiance` and `DdgiSupportCoverage`. This is not expected convergence behavior:

- `DdgiIrradiance` black means the gather returned zero irradiance.
- `DdgiSupportCoverage` black means the gather found no accepted probe-data mass.
- Persistent black rules out ordinary fresh-probe warm-up and points to volume selection, a stuck
  probe-state transition, permanent classification, unpublished atlas data, or incomplete fallback.

Expected black is limited to the sky/background and receivers intentionally outside GI participation.

## Constraints

- Fix the underlying state/gather path; do not hide the failure by recoloring the debug view.
- Never make inactive or unpublished probes contribute radiance merely to fill holes.
- Preserve thin-wall leak prevention and camera-relative ring behavior.
- Reconcile the existing uncommitted changes in `SimpleDdgiVolumeManager`,
  `ddgi_simple_shared.glsl`, `ddgi_simple_transport.comp`, and their tests before editing overlapping
  code. In particular, retain the separation between data availability and visibility weighting.

## Phase 1 — Produce an attributed, locked repro

1. Lock the current camera and GI settings; stop camera-relative recentering during the measurement.
2. Capture after initial warm-up and again after at least 600 stationary frames:
   - `DdgiSpatialCoverage`
   - `DdgiSupportCoverage`
   - `DdgiProbeState`
   - `DdgiClassificationInvalidScore`
   - `DdgiUpdateReasons`
   - `DdgiGatherClipmap`
   - `DdgiGatherFallback`
   - `DdgiIrradiance`
   - `FinalIndirect`
3. Save the diagnostics snapshot from the same frame. Record per-volume active/inactive probes,
   warmed fraction, pending updates, atlas generation, published probes, second-volume gather rate,
   and zero-support-but-spatially-covered fraction.
4. Classify each black ROI:
   - Spatial black: selection/layout failure.
   - Spatial white + support black: probe-state/publication failure.
   - Support nonzero + irradiance black: valid but zero-energy transport failure.

Do not tune thresholds until every black ROI has one of these attributions.

## Phase 2 — Add Simple-DDGI rejection diagnostics

`SimpleDdgiProbeSupportsGather` currently collapses all failures to one Boolean. Replace its internal
decision with a rejection bit mask while preserving the existing Boolean helper:

- outside grid/volume
- `FRESH`
- `SCROLL_EXPOSED`
- `RELOCATION_PENDING`
- inactive flag
- inactive classification
- zero `activeWeight`
- invalid irradiance atlas alpha
- invalid visibility atlas marker

On diagnostic pixels only, accumulate counts by reason and by selected/fallback/recovery volume.
Expose:

- first failing reason and combined reason mask in a dedicated debug view or the existing
  `DdgiProbeState` view;
- per-reason totals in `RendererDiagnostics`;
- number of receivers for which all eight probes failed in primary, fallback, and recovery gathers.

Files:

- `Njulf.Shaders/ddgi_simple_shared.glsl`
- `Njulf.Shaders/common.glsl`
- `Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs`
- `Njulf.Rendering/Data/{SceneRenderingData,RendererDiagnostics,DdgiRuntimeSnapshot}.cs`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`

Acceptance: a stationary capture identifies the dominant rejection reason for every black ROI without
adding measurable cost to non-debug frames.

## Phase 3 — Repair volume selection and fallback

1. Verify `SelectSimpleDdgiVolume` with the unbiased world position. A receiver reported spatially
   covered must select a containing volume even if the forward tile candidate cache is stale or empty.
2. Treat tile candidates strictly as an optimization. On invalid candidates, overflow, or zero-support
   candidates, fall back to the bounded metadata walk.
3. Replace the fixed primary/secondary/one-recovery assumption with a bounded containing-volume walk:
   finest local/ring first, then progressively coarser containing rings, stopping at the first result
   with valid data availability. Cap the walk by the configured Simple-DDGI volume limit.
4. Keep transition/edge weight separate from availability. An unsupported fine ring must not suppress
   a supported coarse ring.
5. Blend irradiance using visibility-selected mass, but calculate `validSupport` and radiometric
   ownership from state-valid published data mass. Keep the late leak attenuation as the visibility
   safeguard.
6. If no containing volume has valid data, return zero DDGI ownership so the final composition gives
   the environment fallback the entire missing share. Raw support/irradiance debug views must remain
   truthful and black for this exceptional case.

Files:

- `Njulf.Shaders/ddgi_simple_shared.glsl`
- `Njulf.Shaders/forward.frag`
- `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`

Acceptance: spatially covered receivers never stop at an unsupported finer ring when a supported
coarser ring exists; `FinalIndirect` has no hard-black hole even during a genuine cache miss.

## Phase 4 — Repair persistent probe-state lifecycle failures

Using the Phase 2 attribution, fix the relevant transition rather than weakening all gates:

- **Fresh/scroll-exposed:** clear only after trace, blend, transport, and sampled-atlas publication
  complete for the probe's current logical generation. Ensure stationary visible unsupported cells
  receive highest scheduling priority.
- **Relocation pending:** give relocation a bounded retry/timeout path. A failed relocation must become
  retryable inactive state, not remain pending forever.
- **Inactive classification:** retain periodic classification retries and prioritize inactive probes
  bordering visible zero-support cells. Review invalid-score hysteresis so dense geometry cannot
  permanently deactivate all corners of a receiver cell.
- **Zero active weight:** verify CPU readback cannot overwrite a newer GPU generation. Key state
  reconciliation by logical cell and generation, not physical slot alone.
- **Invalid atlas markers:** publish irradiance and visibility validity atomically for the same probe
  generation; never expose one atlas from a newer generation than the other.

Add invariants and diagnostics for maximum frames spent fresh, scroll-exposed, relocation-pending, and
unpublished. A stationary visible probe exceeding its bound must raise a validation finding.

Primary files:

- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`
- `Njulf.Shaders/ddgi_simple_trace.comp`
- `Njulf.Shaders/ddgi_simple_blend.comp`
- `Njulf.Shaders/ddgi_simple_transport.comp`
- Simple-DDGI relocation/classification and publication shaders

Acceptance: with a stationary camera, pending/fresh/unpublished counts monotonically drain; inactive
cells retain enough active neighboring support or are recovered by a coarser ring.

## Phase 5 — Scheduler and convergence guardrails

1. Add a visible-zero-support priority class ahead of maintenance and periodic refresh.
2. Prevent transport convergence waves or repeated local invalidations from continuously resetting
   field-wide convergence evidence or starving never-published probes.
3. Preserve local fixed-point evidence across bounded repair waves; reset it only for a genuine lighting
   field/source-generation boundary.
4. Add starvation telemetry: oldest visible unsupported probe age, count above the latency target, and
   updates allocated to zero-support repair.

Acceptance: no visible unsupported probe remains unscheduled beyond the configured bounded latency, and
the convergence watchdog is not the mechanism that eventually repairs ordinary support.

## Tests

Add focused tests before the Sponza validation:

- Per-reason rejection-mask tests for every `SimpleDdgiProbeSupportsGather` gate.
- Fine unsupported + coarse supported gather selects the coarse result.
- Stale/invalid tile candidates fall back to metadata selection.
- All rings unsupported yields zero DDGI ownership and full environment complement.
- Fresh, scroll-exposed, relocation-pending, and inactive probes all have bounded recovery transitions.
- Old readback generations cannot overwrite newer probe state.
- Camera-relative recenter preserves valid physical-slot/logical-cell ownership.
- Thin-wall and room-isolation validators remain green.
- Shader compilation and mirror-contract tests pass.

Run:

```powershell
dotnet build Njulf.sln
dotnet test Njulf.sln
```

Then run the locked Sponza capture at low and high traversal positions.

## Completion gates

- `DdgiSpatialCoverage` is nonzero across intended opaque receivers.
- Persistent black in `DdgiSupportCoverage` is absent from intended opaque receivers after bounded
  warm-up; remaining black is explicitly attributed to background or excluded GI participation.
- Zero-support-but-spatially-covered fraction is below 0.1% and contains no stable multi-pixel ROI.
- `DdgiIrradiance` contains plausible nonzero low-frequency energy wherever support is valid.
- `FinalIndirect` has no hard-black holes during warm-up or steady state.
- No thin-wall leak, relocation popping, or stale-generation regression.
- Forward gather and DDGI update GPU times remain within the selected quality budget.

