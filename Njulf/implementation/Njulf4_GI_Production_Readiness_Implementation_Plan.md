# Njulf 4.0 — SSGI/DDGI Production-Readiness Implementation Plan

**Target branch:** `Simplified`  
**Scope:** Stabilize and correctly combine SSGI and DDGI, make the GI debug views trustworthy, restore intended DDGI update budgets, and establish production-quality validation and diagnostics.  
**Primary scene:** Sponza Plaza / right-wall fixed-camera test  
**Probe-count policy:** Keep the current `28 × 12 × 28` camera-relative grids unchanged until the correctness and stability work below is complete.

---

## 1. Goals and non-goals

### Goals

- Make every GI debug view display only the quantity named by the view.
- Eliminate the debug-view feedback loop into the SSGI trace source.
- Define one unambiguous data contract for SSGI RGB and alpha.
- Apply SSGI confidence/support exactly once.
- Use DDGI as a stable low-frequency baseline instead of suppressing it with AO when SSGI has no valid replacement.
- Restore per-cascade DDGI ray counts and honor the configured bounce-distance limit.
- Verify graphics/compute synchronization, especially with async compute enabled.
- Add objective quality, temporal-stability, and performance gates.
- Determine probe-density changes from measured coverage and age data rather than visual comparison with unrelated videos.

### Non-goals for the first production pass

- Increasing the global DDGI grid size.
- Replacing the current ray-query backend.
- Rewriting the entire forward renderer.
- Adding multiple-bounce SSGI.
- Pursuing physically exact energy conservation before the existing pipeline is stable and measurable.

---

## 2. Confirmed problems to address

1. `WriteForwardColor()` also writes the active debug color into `outSsgiTraceSource`.
2. The SSGI producer and composite continue to run while DDGI debug views are displayed.
3. SSGI temporal history can contain results produced from a previous debug view.
4. `ssgi_trace.comp` stores confidence-premultiplied RGB while later stages treat RGB as unpremultiplied radiance and weight it by alpha again.
5. `ssgi_composite.frag` applies another support curve, causing sparse-but-valid samples to be attenuated twice.
6. The trace has an aggressive 8% screen-edge fade and starts range fading at 35% of the configured trace distance.
7. DDGI coverage is multiplied by `activeProbe` again after coverage already includes active/confidence terms.
8. AO suppresses DDGI near contact even when SSGI has no valid support to replace it.
9. DDGI RGB is already AO-modulated before the AO-derived coverage suppression.
10. The CPU stores per-volume ray counts, but `ddgi_update.comp` uses one global maximum for every cascade.
11. Camera-relative DDGI ray distance is forced to the volume diagonal, bypassing `MaxBounceDistance`.
12. The update scheduler reacts to measured GPU time using request count, without visibility into the actual scheduled ray workload.

---

## 3. Delivery strategy

Implement the work as small, reviewable pull requests in the following order:

| PR | Subject | Depends on |
|---|---|---|
| PR 1 | Reproducible GI baseline and debug-view isolation | None |
| PR 2 | Correct SSGI radiance/support data contract | PR 1 |
| PR 3 | Stable DDGI baseline and AO/hybrid handoff | PR 2 |
| PR 4 | Per-volume DDGI rays and bounded ray distance | PR 1 |
| PR 5 | DDGI scheduling diagnostics and temporal stability | PR 4 |
| PR 6 | Vulkan synchronization and async-compute validation | PRs 2–5 |
| PR 7 | Quality tuning, regression captures, and release gates | PRs 1–6 |

Do not combine all changes in one commit. Each PR must have its own captures, tests, and rollback point.

---

# Phase 0 — Establish a reproducible baseline

## Step 0.1 — Create the fixed-camera reproduction

Add a deterministic Sponza GI scenario with:

- Fixed camera transform matching the supplied screenshots.
- Fixed light transform and intensity.
- Fixed resolution: `1600 × 900`.
- Dynamic resolution disabled.
- Auto exposure disabled.
- Bloom and fog disabled.
- Fixed random/temporal frame sequence.
- Separate variants with AO enabled and disabled.
- Separate variants with async compute enabled and disabled.

Suggested location:

- `Njulf/NjulfHelloGame/SampleGlobalIlluminationValidation.cs`
- `Njulf/NjulfHelloGame/SamplePerformanceScenario.cs`
- `Njulf/NjulfHelloGame/SamplePerformanceScenarioRunner.cs`

Add a named path such as:

```text
sponza-right-wall-stationary
```

## Step 0.2 — Capture the current baseline

Capture at least 240 frames after a 240-frame warm-up for:

- Normal rendering.
- `FinalIndirect`.
- `DdgiIrradiance`.
- `DdgiCoverage`.
- `DdgiProbeState`.
- `DdgiVisibility`.
- `SsgiRaw`.
- `SsgiFiltered`.
- `SsgiHistory`.
- `SsgiRayHitMask`.
- `SsgiHistoryRejection`.

Store:

- Frame 1 after view switch.
- Frame 30.
- Frame 120.
- Frame 240.
- A temporal mean image.
- A temporal standard-deviation image.
- Right-wall ROI mean luminance and standard deviation.
- GPU timings for all GI passes.
- DDGI active probes, scheduled updates, and rays per probe.

## Step 0.3 — Add baseline metrics

Extend diagnostics with:

```text
SsgiRawMeanSupport
SsgiFilteredMeanSupport
SsgiHistoryRejectionRatio
SsgiRightWallRelativeLumaStdDev
DdgiMeanCoverage
DdgiCoverageBelow025PixelCount
DdgiInactiveProbeRatio
DdgiScheduledProbeCount
DdgiScheduledRayCount
DdgiGpuUpdateMicroseconds
DdgiEstimatedFullRefreshFrames
```

### Phase 0 acceptance criteria

- The scenario runs deterministically for repeated executions.
- Two runs with identical settings differ by no more than one quantization unit in debug captures, or the remaining expected nondeterminism is documented.
- Baseline results are checked into `Plans/Baselines` or another established baseline location.
- No rendering change is made in this phase.

---

# Phase 1 — Make GI debug views trustworthy

## Step 1.1 — Introduce one shared debug execution policy

Create:

```text
Njulf/Njulf.Rendering/Pipeline/GlobalIlluminationPassExecutionPolicy.cs
```

It should provide at least:

```csharp
bool IsDdgiDebugView(GlobalIlluminationDebugView view);
bool IsSsgiDebugView(GlobalIlluminationDebugView view);
bool ShouldRunSsgiProducer(GlobalIlluminationSettings gi);
bool ShouldCompositeSsgi(GlobalIlluminationSettings gi);
```

Use this policy from every GI pass instead of duplicating switch expressions.

### Required policy

- SSGI trace, temporal, and denoise may continue running in the background whenever SSGI is enabled. This keeps history warm while a DDGI debug view is displayed.
- SSGI composite runs only for:
  - `None`
  - `FinalIndirect`
- SSGI composite must not run for:
  - Any DDGI debug view.
  - `SsgiRaw`
  - `SsgiFiltered`
  - `SsgiHistory`
  - `SsgiRayHitMask`
  - `SsgiHistoryRejection`

Files:

- `Njulf/Njulf.Rendering/Pipeline/SsgiTracePass.cs`
- `Njulf/Njulf.Rendering/Pipeline/SsgiTemporalPass.cs`
- `Njulf/Njulf.Rendering/Pipeline/SsgiDenoisePass.cs`
- `Njulf/Njulf.Rendering/Pipeline/SsgiCompositePass.cs`

## Step 1.2 — Separate forward color from the SSGI trace source

In:

```text
Njulf/Njulf.Shaders/forward.frag
```

Change `WriteForwardColor()` so it writes only the visible forward color.

Remove:

```glsl
outSsgiTraceSource = color;
```

from `WriteForwardColor()`.

Keep `WriteSsgiTraceSource()` as the only function allowed to write the trace-source attachment.

## Step 1.3 — Write a canonical trace source before GI debug returns

After direct lighting has been evaluated, but before the GI debug-view branches, write:

```glsl
WriteSsgiTraceSource(
    vec4(clamp(directLighting + emissive, vec3(0.0), vec3(64.0)), 1.0));
```

The source must be identical whether the visible output is:

- Normal rendering.
- `FinalIndirect`.
- A DDGI debug view.
- An SSGI debug view.

Do not use visible debug color as SSGI radiance.

Ensure pixels that exit through earlier non-GI debug branches are irrelevant because the SSGI composite is disabled for those modes, or explicitly clear the trace-source attachment before rendering.

## Step 1.4 — Prevent stale debug history

Because the trace source will no longer depend on `DebugView`, changing Ctrl+G modes should not alter SSGI input. Still add a trace-source generation/version value to temporal history state, or invalidate history when:

- SSGI is toggled off and back on.
- The SSGI producer did not execute in the previous frame.
- Trace-source format or dimensions change.
- Any SSGI estimator version changes during development.

Suggested changes:

- Add `_lastProducerExecutedFrame`.
- Add `_traceContractVersion`.
- Reset history if the current frame is not consecutive with the last producer frame.

File:

```text
Njulf/Njulf.Rendering/Pipeline/SsgiTemporalPass.cs
```

## Step 1.5 — Add tests

Add tests covering:

- Every `GlobalIlluminationDebugView` maps to the expected pass policy.
- `SsgiCompositePass.ShouldExecute()` is false for all DDGI and raw SSGI debug views.
- The forward shader compiles with the separated outputs.
- Ctrl+G transitions do not carry visible output from the previous debug view.

Suggested test files:

```text
Njulf/Njulf.Tests/GlobalIlluminationPassExecutionPolicyTests.cs
Njulf/Njulf.Tests/ShaderBuildTests.cs
```

### Phase 1 acceptance criteria

- `DdgiCoverage` is a pure grayscale scalar visualization.
- `DdgiIrradiance` contains no additive SSGI detail.
- `SsgiRayHitMask` displays only `SsgiRaw.a`.
- Switching Ctrl+G cannot alter the SSGI trace input.
- RenderDoc shows no SSGI composite draw in disallowed debug modes.
- No one-frame flash or stale history appears after cycling debug views.

---

# Phase 2 — Correct the SSGI estimator and support contract

## Step 2.1 — Document the texture contract

Add comments beside the render-target declarations and shader output:

```text
SsgiRaw.rgb = unpremultiplied conditional mean hit radiance
SsgiRaw.a   = support/confidence in [0, 1]
```

The contract means:

- RGB is not multiplied by alpha.
- Alpha expresses how much evidence supports RGB.
- Temporal and spatial passes may use alpha as a statistical weight.
- The final composite maps alpha to one contribution weight exactly once.

Update comments in:

- `Njulf/Njulf.Rendering/Resources/RenderTargetManager.cs`
- `Njulf/Njulf.Shaders/ssgi_trace.comp`
- `Njulf/Njulf.Shaders/ssgi_temporal.comp`
- `Njulf/Njulf.Shaders/ssgi_denoise.comp`
- `Njulf/Njulf.Shaders/ssgi_composite.frag`

## Step 2.2 — Output conditional mean radiance

In `ssgi_trace.comp`, replace the current output calculation with:

```glsl
float support = clamp(
    accumulatedConfidence / max(float(rayCount), 1.0),
    0.0,
    1.0);

vec3 meanRadiance =
    accumulatedConfidence > 0.0001
        ? accumulatedRadiance / accumulatedConfidence
        : vec3(0.0);

vec3 energy = meanRadiance * intensity;

imageStore(SsgiRawOutput, pixel, vec4(energy, support));
```

Retain confidence weighting inside `accumulatedRadiance`; only remove the second implicit premultiplication caused by division by total ray count.

## Step 2.3 — Audit temporal assumptions

`NeighborhoodStats()` already multiplies sample RGB by alpha. That behavior is appropriate after RGB becomes unpremultiplied.

Verify and test:

- Current-frame radiance is weighted by current support once.
- History support and history length remain separate concepts.
- A current miss may reuse history briefly, but its support decays.
- Rejected history cannot keep high RGB with zero support indefinitely.
- Firefly clipping is performed on unpremultiplied radiance.
- History moments track resolved radiance rather than premultiplied energy.

Add a unit/reference test using synthetic values:

```text
one bright hit + five misses
two bright hits + four misses
six hits
all misses
```

Expected behavior must be monotonic in support and must not exhibit a 10×–15× jump between adjacent hit counts.

## Step 2.4 — Audit the denoiser assumptions

The denoiser currently applies support weighting while averaging RGB, which is appropriate for unpremultiplied RGB.

Verify:

- Unsupported neighbors do not darken supported samples.
- Low-support samples can borrow stable radiance from compatible neighbors.
- Depth, normal, luma, hit-distance, and history weights remain independently observable.
- The output alpha remains support, not filtered luminance.

Add debug modes or counters for:

```text
bilateral weight sum
support weight sum
history convergence
variance
```

## Step 2.5 — Apply support once in the composite

Replace the current hard-threshold curve:

```glsl
smoothstep(0.08, 0.75, support)
```

with a support mapping that has no dead zone for valid samples. Use an initial implementation such as:

```glsl
float contactWeight = smoothstep(0.0, 0.35, support);
```

Make the upper support threshold configurable:

```csharp
SsgiFullSupportThreshold = 0.35f;
```

Do not multiply RGB by support anywhere else after the trace contract change.

Suggested setting location:

```text
Njulf/Njulf.Rendering/Data/RenderSettings.cs
```

## Step 2.6 — Parameterize screen-edge and range fading

Add:

```csharp
SsgiScreenEdgeFadeFraction = 0.02f;
SsgiRangeFadeStartFraction = 0.70f;
```

Replace the hard-coded shader values:

```text
0.08 screen-edge fade
0.35 range-fade start
```

Pass the values through SSGI trace push constants.

Clamp ranges:

```text
ScreenEdgeFadeFraction: 0.0–0.10
RangeFadeStartFraction: 0.25–0.95
```

## Step 2.7 — Add SSGI contract tests

Add tests for:

- Zero hits produces RGB=0, alpha=0.
- One perfect hit among six produces the hit radiance in RGB and `1/6` alpha.
- Six perfect hits produce their mean radiance and alpha=1.
- The composite contribution is monotonic in support.
- A support value just above zero is not forcibly discarded.
- Edge fade is symmetric on all four edges.
- Range fade begins at the configured fraction.

### Phase 2 acceptance criteria

- The fixed-camera `SsgiRaw` image has stable radiance values at supported pixels rather than support-darkened values.
- The filtered result does not show salt-and-pepper luminance changes matching individual ray hits.
- Right-wall relative temporal luminance standard deviation is below 2% after warm-up, with a stretch goal below 1%.
- Existing validation gates remain satisfied:
  - history rejection ratio ≤ 0.35
  - stable temporal luma error ≤ 0.05
  - disocclusion recovery ≤ 6 frames
- No measurable NaN or infinity pixels.

---

# Phase 3 — Restore a stable DDGI baseline and correct the hybrid handoff

## Step 3.1 — Remove duplicate DDGI validity multiplication

In `forward.frag`, change:

```glsl
float ddgiLowFrequencyCoverage =
    clamp(ddgi.coverage * ddgi.activeProbe, 0.0, 1.0);
```

to:

```glsl
float ddgiLowFrequencyCoverage =
    clamp(ddgi.coverage, 0.0, 1.0);
```

`ddgi.coverage` already includes:

- probe active state
- irradiance confidence
- quality confidence
- interpolation weight
- volume edge fade

Keep `activeProbe` as a debug/diagnostic output.

## Step 3.2 — Remove AO as a proxy for SSGI support

Remove:

```glsl
ddgiContactSuppression
nearContactOcclusion
nearContactSuppression
```

from the DDGI/IBL blend.

Use:

```glsl
float ddgiValidity = clamp(ddgi.coverage, 0.0, 1.0);
vec3 finalDiffuseIndirect =
    mix(diffuseIbl, ddgiDiffuse, ddgiValidity);
```

AO should affect the lighting term once, not decide whether DDGI is replaced.

Audit `EvaluateIbl()` and `SampleDdgiDiffuse()` to ensure AO is applied exactly once to each source.

## Step 3.3 — Define the initial production hybrid model

For the stabilization release, use this explicit model:

```text
IBL/DDGI = stable low-frequency baseline
SSGI     = support-weighted near-field/contact enhancement
```

Do not remove DDGI based on AO.

Document that SSGI is an enhancement term, not a full replacement, until a unified hybrid composite is implemented.

Add a bounded contact scale:

```csharp
SsgiContactIntensity = 1.0f;
```

Clamp to a safe range such as `0–2`.

## Step 3.4 — Add energy-budget diagnostics

Add debug outputs for:

```text
IBL diffuse only
DDGI diffuse only
SSGI contact only
IBL/DDGI baseline
final hybrid indirect
SSGI / baseline luminance ratio
```

Add per-frame counters:

```text
mean DDGI contribution
mean SSGI contribution
95th percentile SSGI/baseline ratio
pixels where SSGI exceeds baseline by > 2×
```

## Step 3.5 — Plan the later unified hybrid composite

After the stabilization release, evaluate adding a dedicated `HybridGiCompositePass` that receives:

- DDGI/IBL baseline diffuse.
- SSGI radiance.
- SSGI support.
- Scene material.
- AO.

The pass can then blend or replace the near-field portion using actual SSGI support instead of AO. Do not block the stabilization release on this rearchitecture unless additive SSGI fails the energy-budget tests.

### Phase 3 acceptance criteria

- Turning SSGI off leaves a stable, visibly useful DDGI/IBL baseline.
- Areas with zero SSGI support retain DDGI.
- AO does not make DDGI coverage collapse.
- Enabling SSGI adds localized contact detail without producing frame-dependent holes.
- The final indirect view is never darker merely because the raw SSGI mask has a miss.

---

# Phase 4 — Restore intended DDGI work distribution

## Step 4.1 — Use each volume’s ray count in the shader

`GPUDdgiProbeVolume.RayAndUpdateParams.x` already stores the resolved per-volume ray count.

In `ddgi_update.comp`, replace:

```glsl
uint raysPerProbe =
    clamp(pc.RaysPerProbe, 1u, DDGI_MAX_RAYS_PER_PROBE);
```

with:

```glsl
uint raysPerProbe = clamp(
    uint(round(updateParams.x)),
    1u,
    DDGI_MAX_RAYS_PER_PROBE);
```

Use the push-constant ray count only as a defensive fallback if the volume value is invalid.

Expected Sponza Plaza values:

```text
Cascade 0: 128
Cascade 1:  96
Cascade 2:  64
Cascade 3:  48
```

## Step 4.2 — Clarify the CPU contract

Rename global fields where practical:

```text
RaysPerProbe -> MaxRaysPerProbe
```

when the value is the maximum across volumes.

Keep per-volume values authoritative for actual dispatch work.

Update:

- `GlobalIlluminationProbeVolumeData.BuildVolumes()`
- `GPUDdgiProbeVolumeHeader`
- `SceneRenderingData`
- renderer diagnostics
- tests

## Step 4.3 — Honor `MaxBounceDistance`

Change camera-relative volume creation so `MaxRayDistance` does not default to the full cascade diagonal.

Initial production policy:

```csharp
MaxRayDistance = MathF.Min(
    diagonal,
    settings.MaxBounceDistance);
```

Then change `EffectiveMaxRayDistance()` so it validates and clamps the authored value rather than forcing it to at least the volume diagonal.

A safe implementation:

```csharp
private static float EffectiveMaxRayDistance(
    GlobalIlluminationProbeVolume volume)
{
    float value = volume.MaxRayDistance;
    if (!float.IsFinite(value))
        value = 16.0f;

    return Math.Clamp(value, 0.1f, 1000.0f);
}
```

If later testing shows far cascades require a scale factor, add an explicit per-cascade policy rather than silently using the diagonal.

## Step 4.4 — Add scheduled-ray diagnostics

Track both:

```text
scheduled probe requests
scheduled primary rays
```

For each request:

```text
ray cost = volume.RaysPerProbe
```

Expose totals and per-cascade values in `RendererDiagnostics`.

## Step 4.5 — Make the scheduler ray-cost aware

After per-volume rays are correct, update the scheduler so time budgeting is based on estimated ray work rather than request count alone.

Suggested approach:

1. Maintain an exponentially smoothed `microsecondsPerRay`.
2. Estimate each request’s ray cost from its volume.
3. Fill requests by priority until the estimated ray budget is reached.
4. Preserve mandatory new-cell and safety-refresh quotas.
5. Fall back to request-count budgeting until enough timing history exists.

Do not allow the time-budget controller to starve:

- new clipmap cells
- dirty bounds
- safety refresh
- the nearest cascade

## Step 4.6 — Add DDGI workload tests

Tests must verify:

- Every cascade preserves its configured ray count.
- The shader reads `updateParams.x`.
- `MaxBounceDistance=14` never produces a 60–480 unit ray distance.
- Time-budget scaling preserves mandatory requests.
- Per-cascade scheduled-ray totals match the request list.
- Changing ray distance or per-volume rays updates the resource signature where required.

### Phase 4 acceptance criteria

- GPU captures show `128/96/64/48` rays by cascade.
- Far cascades no longer trace at the near-cascade ray count.
- Ray distance respects the configured production policy.
- DDGI GPU time decreases or more probes update within the same budget.
- The right-wall result does not become less stable after restoring the intended workload.

---

# Phase 5 — DDGI temporal-quality and probe-health hardening

## Step 5.1 — Use existing overlays systematically

Validate these overlays in the fixed-camera scene:

- `DdgiProbeActivity`
- `DdgiUpdatedProbes`
- `DdgiProbeRelocation`
- `DdgiProbeAge`
- `DdgiPhysicalSlots`
- `DdgiCascadeBounds`
- `DdgiNewlyExposedCells`
- `DdgiFrustumPriority`
- `DdgiSafetyRefresh`
- `DdgiCascadeBlend`
- `DdgiUpdateReasons`

## Step 5.2 — Add per-cascade health metrics

Record:

```text
probe count
active-probe ratio
classified-inactive ratio
relocated-probe ratio
mean age
95th percentile age
maximum age
updates per frame
scheduled rays per frame
estimated full refresh time
coverage histogram
visibility histogram
```

## Step 5.3 — Validate classification stability

Run fixed-camera captures with:

1. Classification on, relocation on.
2. Classification on, relocation off.
3. Classification off, relocation off.

If flicker changes materially:

- Add temporal hysteresis to active/inactive classification.
- Require multiple consecutive invalid classifications before disabling a probe.
- Require multiple consecutive valid classifications before reactivating it.
- Preserve the previous relocation while evidence is weak.
- Expose classification transition counts.

## Step 5.4 — Validate clipmap scrolling

During slow translation:

- Verify only newly exposed cells reset.
- Verify physical-slot ring addressing is stable.
- Verify retained cells preserve irradiance/history.
- Verify edge blending between cascades is continuous.
- Verify no probe is marked updated on the CPU before its GPU update is actually submitted.

## Step 5.5 — Probe-density decision gate

Only consider increasing density if, after Phases 1–4:

- `DdgiCoverage` has persistent low-coverage regions on valid geometry.
- The nearest eight probes provide insufficient supported weight.
- The wall’s lighting gradient is smaller than the current 1.5-unit base spacing.
- Probe classification removes too many neighbors.
- Cascade boundaries visibly cross important interior geometry.
- Probe-age metrics remain healthy under the current workload.

Preferred density changes:

1. Reduce base spacing locally or for cascade 0.
2. Add an authored high-density interior volume.
3. Increase only the axis that is demonstrably undersampled.
4. Increase the global 3D grid last.

Every full-grid dimension increase must include its memory and refresh-time impact. Doubling all three axes costs approximately 8× as many probes.

### Phase 5 acceptance criteria

- No stationary probe repeatedly toggles active/inactive without a geometry change.
- 95th percentile age remains within the target refresh period for each cascade.
- Slow clipmap scrolling produces no visible steps or flashes.
- Probe-density decisions are supported by captured metrics.

---

# Phase 6 — Vulkan synchronization and async-compute validation

## Step 6.1 — Enable synchronization validation

Run with:

```text
VK_LAYER_KHRONOS_validation
VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT
```

Exercise:

- Async compute disabled.
- Async compute enabled.
- Secondary command buffers disabled/enabled.
- Camera stationary and moving.
- Ctrl+G cycling.
- Swapchain resize.
- Scene reload.

## Step 6.2 — Audit DDGI resource ownership

Verify synchronization for:

```text
DDGI probe state buffer
DDGI irradiance atlas buffer
DDGI visibility atlas buffer
recursive snapshot buffers
probe update queue
relocation/classification buffer
```

Confirm:

- Graphics reads cannot overlap incomplete compute writes.
- Queue-family ownership transfers are correct if queues differ.
- The recursive snapshot copy sees completed previous writes.
- The update shader does not read and write the same logical history unexpectedly.
- Timeline semaphore values correspond to the intended frame.

## Step 6.3 — Audit SSGI transitions

Verify:

```text
SsgiTraceSource: color attachment -> shader read
SsgiRaw: storage write -> shader read
history ping-pong: shader read/write separation
GiFinalDiffuse: storage write -> shader read
SceneColor: color attachment load/store during composite
```

## Step 6.4 — Compare async and synchronous output

Capture the same deterministic 240-frame sequence with async compute on and off.

The final and debug images should match within the chosen floating-point tolerance. A material temporal difference indicates a synchronization or ordering defect.

### Phase 6 acceptance criteria

- Zero Vulkan validation errors.
- Zero synchronization-validation hazards.
- Async-on and async-off captures are visually and numerically equivalent within tolerance.
- No intermittent right-wall flicker occurs only in async mode.

---

# Phase 7 — Production tuning and release qualification

## Step 7.1 — Tune one parameter family at a time

Tune in this order:

1. SSGI support mapping.
2. SSGI edge-fade fraction.
3. SSGI range-fade start.
4. SSGI history responsiveness.
5. SSGI ray/step quality presets.
6. DDGI hysteresis.
7. DDGI per-cascade ray counts.
8. DDGI update-time budget.
9. DDGI base spacing, only if the density gate permits it.

Never tune probe count and ray/update budgets simultaneously.

## Step 7.2 — Establish quality presets

For each preset, explicitly define:

```text
SSGI resolution scale
SSGI rays
SSGI steps
SSGI denoiser radius/iterations
DDGI cascade count
DDGI probe counts
DDGI base spacing
DDGI rays per cascade
DDGI max updates
DDGI update-time budget
```

Avoid hidden fall-through defaults.

## Step 7.3 — Required validation scenes

Run:

- Sponza right-wall stationary.
- Sponza slow pan.
- Sponza translation.
- Bright exterior looking into dark interior.
- Cornell room.
- Thin-wall leak test.
- Moving point light.
- Moving rigid object.
- Camera cut.
- FOV change.
- Resize and scene reload.

## Step 7.4 — Required quality gates

Minimum gates:

```text
history rejection ratio                 <= 0.35
stable temporal relative-luma error     <= 0.05
right-wall relative luma stddev         <= 0.02
disocclusion recovery                   <= 6 frames
thin-wall leakage                       <= 0.03 relative luma
NaN/Inf HDR outliers                    == 0 pixels
DDGI coverage debug contamination       == 0 pixels
```

Stretch targets:

```text
right-wall relative luma stddev         <= 0.01
stable temporal relative-luma error     <= 0.02
```

## Step 7.5 — Required performance gates

Retain the established SSGI budgets unless intentionally revised:

```text
SSGI trace       <= 2200 µs
SSGI temporal    <=  900 µs
SSGI spatial     <= 1800 µs
```

Define a DDGI target for the shipping GPU tier. For the Sponza production profile, begin with:

```text
DDGI update      <= 4000 µs
```

Also gate:

```text
total scheduled DDGI rays
probe refresh time by cascade
GI memory usage
transient render-target memory
```

## Step 7.6 — Long-run test

Run at least 30 minutes with:

- Repeated movement through clipmap cells.
- Periodic camera cuts.
- Debug-view cycling.
- Async compute enabled.
- Resolution changes.
- GI enable/disable toggles.

Track:

- GPU errors.
- NaNs.
- resource reinitializations.
- history resets.
- probe ages.
- memory growth.
- frame-time spikes.

---

# 4. File-by-file implementation checklist

## `Njulf/Njulf.Shaders/forward.frag`

- [ ] Remove implicit `outSsgiTraceSource` write from `WriteForwardColor()`.
- [ ] Write canonical direct-light-plus-emissive trace source explicitly.
- [ ] Remove duplicate `ddgi.coverage * ddgi.activeProbe`.
- [ ] Remove AO-derived DDGI contact suppression.
- [ ] Keep `activeProbe` for debug only.
- [ ] Add isolated IBL/DDGI/hybrid debug outputs if needed.
- [ ] Verify AO is applied once per indirect source.

## `Njulf/Njulf.Shaders/ssgi_trace.comp`

- [ ] Store unpremultiplied conditional mean radiance in RGB.
- [ ] Store support in alpha.
- [ ] Parameterize edge fade.
- [ ] Parameterize range-fade start.
- [ ] Preserve mean hit distance.
- [ ] Add comments defining the texture contract.

## `Njulf/Njulf.Shaders/ssgi_temporal.comp`

- [ ] Verify all confidence weighting assumes unpremultiplied RGB.
- [ ] Verify miss decay also decays support.
- [ ] Add producer continuity/contract-version invalidation.
- [ ] Add synthetic estimator tests.

## `Njulf/Njulf.Shaders/ssgi_denoise.comp`

- [ ] Verify support is used as a weight exactly once.
- [ ] Verify unsupported neighbors do not darken supported pixels.
- [ ] Add filter-weight diagnostics.

## `Njulf/Njulf.Shaders/ssgi_composite.frag`

- [ ] Replace `smoothstep(0.08, 0.75, support)`.
- [ ] Use the configurable support mapping.
- [ ] Keep receiver albedo and metallic handling documented.
- [ ] Ensure the composite runs only in `None` and `FinalIndirect`.

## `Njulf/Njulf.Rendering/Pipeline/Ssgi*Pass.cs`

- [ ] Use shared execution policy.
- [ ] Keep producer history warm.
- [ ] Disable composite in all raw/DDGI debug views.
- [ ] Add pass-execution tests.

## `Njulf/Njulf.Shaders/ddgi_update.comp`

- [ ] Use `updateParams.x` for rays per probe.
- [ ] Keep push-constant rays only as fallback.
- [ ] Confirm frame-direction rotation uses the resolved per-volume count.
- [ ] Add scheduled-ray diagnostics.

## `Njulf/Njulf.Rendering/Resources/DdgiFrameLayout.cs`

- [ ] Set camera-relative `MaxRayDistance` from the configured bounce limit.
- [ ] Preserve per-cascade rays.
- [ ] Add tests for emitted volume parameters.

## `Njulf/Njulf.Rendering/Data/GlobalIlluminationProbeVolumeData.cs`

- [ ] Stop forcing ray distance to the volume diagonal.
- [ ] Clarify maximum versus per-volume ray fields.
- [ ] Extend tests for max distance and ray count.

## `Njulf/Njulf.Rendering/Resources/DdgiProbeUpdateScheduler.cs`

- [ ] Add scheduled-ray accounting.
- [ ] Add ray-cost-aware budgeting.
- [ ] Preserve priority and safety quotas.
- [ ] Add starvation tests.

## `Njulf/Njulf.Rendering/VulkanRenderer.cs`

- [ ] Use the shared GI debug policy.
- [ ] Add new diagnostics to snapshots.
- [ ] Ensure debug modes map to pure outputs.

## `Njulf/Njulf.Rendering/Data/RenderSettings.cs`

- [ ] Add `SsgiFullSupportThreshold`.
- [ ] Add `SsgiScreenEdgeFadeFraction`.
- [ ] Add `SsgiRangeFadeStartFraction`.
- [ ] Add `SsgiContactIntensity`.
- [ ] Add validation/clamping and serialization tests.

---

# 5. Test plan

## Unit tests

- GI debug execution policy for every enum value.
- SSGI support mapping monotonicity.
- SSGI one-hit/two-hit/all-hit estimator cases.
- DDGI per-cascade ray resolution.
- DDGI max-ray-distance resolution.
- DDGI scheduler mandatory-request preservation.
- DDGI scheduled-ray accounting.
- Resource-signature changes.
- settings serialization and clamp ranges.

## Shader tests

- Compile every affected shader permutation.
- Validate push-constant sizes.
- Validate GPU struct offsets.
- Validate extra render-target outputs.
- Run shader reflection/layout tests.

## Integration tests

- Ctrl+G cycle produces pure debug views.
- SSGI off leaves DDGI baseline intact.
- DDGI off leaves SSGI functional.
- AO on/off does not change DDGI coverage.
- Async on/off produces equivalent output.
- Camera cut invalidates only necessary history.
- Resize/reload resets resources cleanly.

## Image regression tests

For each required scene, compare:

- Mean absolute relative luminance error.
- 95th percentile error.
- Temporal standard deviation.
- Disocclusion recovery time.
- Coverage/debug mask purity.
- Thin-wall leakage.

---

# 6. Rollout and rollback

## Rollout

1. Land PR 1 and verify debug captures before changing lighting math.
2. Land PR 2 behind a temporary internal setting:
   ```text
   UseUnpremultipliedSsgiRadiance
   ```
3. Compare old/new results in automated captures.
4. Make the corrected path default after acceptance.
5. Land PRs 3 and 4 independently so DDGI composition and workload changes can be bisected.
6. Remove temporary legacy toggles before release.
7. Update release notes and rendering documentation.

## Rollback points

- PR 1 can be reverted without affecting normal rendering.
- PR 2 can temporarily retain the old estimator behind a development-only switch.
- PR 3 should retain a simple `IBL/DDGI` fallback path.
- PR 4 should retain the global ray-count value as a defensive shader fallback.
- Async compute can be disabled independently if synchronization validation finds a platform issue.

---

# 7. Definition of done

The GI work is production-ready when all of the following are true:

- [ ] Every Ctrl+G debug view is isolated and visually trustworthy.
- [ ] Debug colors never enter the SSGI trace source.
- [ ] SSGI RGB/alpha semantics are documented and tested.
- [ ] SSGI support is applied once.
- [ ] The right-wall fixed-camera test meets the temporal-stability gate.
- [ ] DDGI remains present wherever SSGI has no support.
- [ ] AO no longer acts as a proxy for SSGI validity.
- [ ] DDGI uses the configured per-cascade ray counts.
- [ ] DDGI ray distance respects the configured maximum.
- [ ] DDGI scheduling reports probe and ray workloads.
- [ ] No Vulkan synchronization errors occur.
- [ ] Async and synchronous output agree within tolerance.
- [ ] All validation scenes pass.
- [ ] GI performance remains within the shipping budget.
- [ ] A 30-minute long-run test shows no memory growth, probe starvation, history corruption, NaNs, or temporal flashing.
- [ ] Probe density is increased only if post-fix measurements prove it is necessary.

---

# 8. First implementation milestone

The first milestone should contain only these changes:

1. Isolate SSGI composite execution.
2. Separate `WriteForwardColor()` from `WriteSsgiTraceSource()`.
3. Produce clean versions of the four supplied debug views.
4. Add the fixed-camera right-wall baseline.
5. Add pass-execution tests.

Do not change probe counts, SSGI weighting, DDGI composition, or DDGI ray budgets until the new debug captures confirm that each buffer is being observed without feedback or additive contamination.
