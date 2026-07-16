# Njulf 4.0 DDGI Production-Readiness and Performance Plan

## Purpose

This plan turns the current DDGI implementation into a production-ready, high-performance system without relying on debug bypasses, forced probe activation, disabled AO, oversized volumes, or other temporary workarounds.

The current diagnostic capture indicates that DDGI is active and gathered, but not contributing:

```text
ddgiVolumes=5
ddgiProbes=32648/32648
ddgiUpdated=144
gatherFallback=0
emptyTiles=0
gatherFractions local/clipmap/fallback=1.000/1.000/0.000
ddgiEstimate coverage/visible/effective/reloc/inactive=1.000/0.000/0.000/0.004/0
```

This means the main failure is no longer missing coverage. The frame has full DDGI coverage and active probes, but visible support and final effective weight are zero. The scheduler is also too expensive and under-updating:

```text
DDGI scheduler: candidates=11502, requests=66, rejected budget=11436, scheduleUs=3821, scheduleP95Us=7047, scheduleOverBudget=1
```

The production goal is therefore twofold:

1. Make valid DDGI samples contribute reliably and physically plausibly.
2. Reduce DDGI update, scheduling, memory, and warmup cost to production budgets.

---

## Production Targets

These are the target gates for the final implementation.

### Visual correctness targets

- Shadowed courtyard/alley areas receive visible bounced light from sky and sunlit masonry.
- DDGI does not collapse to black when `coverage=1` and probes are active.
- Probe visibility and leak prevention reduce leaks without eliminating valid low-frequency irradiance.
- Camera motion produces no obvious popping from clipmap scrolling or relocation.
- Dense authored/local volumes blend smoothly with camera-relative clipmaps.
- Debug views are consistent:
  - `DdgiCoverage` should show expected volume coverage.
  - `DdgiRawDiffuse` should show probe radiance before final weighting.
  - `DdgiEffectiveWeight` should be non-zero where DDGI is expected to contribute.
  - `DdgiSuppressionMask` should show whether visibility/AO/leak control is responsible for attenuation.

### Performance targets

For the sample scene at 1600×900 Development/High-equivalent settings:

- DDGI scheduling P95: **≤ 0.5 ms**.
- DDGI trace + blend + classify P95: **≤ 2.5 ms** on the target GPU tier.
- Full DDGI GPU cost P95: **≤ 3.0 ms**.
- Scheduler candidate count: **bounded to ≤ 4× request budget** under steady state.
- Budget rejection count: **not continuously dominating candidate count**.
- Warmup latency for visible near-field probes: **≤ 1 second** at 60 FPS.
- DDGI memory: explicit per-tier budgets; no silent growth beyond the selected tier.

### Stability targets

- Use a monotonic DDGI frame serial everywhere DDGI age/history is evaluated.
- No shipping dependency on:
  - `DdgiDebugForceProbeActive`
  - raw diffuse bypass
  - disabled AO
  - forced CPU reference scheduler
  - giant catch-all debug volumes
- GPU scheduler has a validated CPU reference path for tests, not as a normal runtime fallback.

---

## Phase 1 — Lock Down Reproducible Diagnostics

### 1.1 Add a DDGI diagnostic snapshot contract

Create a compact DDGI diagnostic record that can be dumped once per frame or on demand.

Suggested path:

```text
Njulf/Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs
```

Include at least:

```csharp
public readonly record struct DdgiRuntimeSnapshot(
    int VolumeCount,
    int ActiveProbeCount,
    int ScheduledProbeUpdates,
    int SchedulerCandidateCount,
    int SchedulerRequestCount,
    int SchedulerBudgetRejectedCount,
    long SchedulerGpuMicroseconds,
    long SchedulerGpuP95Microseconds,
    float EstimateCoverage,
    float EstimateVisibleSupport,
    float EstimateEffectiveWeight,
    float EstimateRelocationMagnitude,
    int EstimateInactiveProbeCount,
    int GatherFallbackTileCount,
    int EmptyGatherTileCount,
    int SelectedLocalTileCount,
    int SelectedClipmapTileCount);
```

### 1.2 Add shader-side DDGI estimate counters

The diagnostic already reports aggregate `coverage/visible/effective`. Make this explicit and auditable by adding a small reduction buffer written by the forward pass when DDGI debug instrumentation is enabled.

Track:

- `coverageSum`
- `visibleSupportSum`
- `effectiveWeightSum`
- `rawDiffuseLuminanceSum`
- `finalDdgiDiffuseLuminanceSum`
- `sampleCount`
- `zeroVisibleButCoveredCount`
- `zeroEffectiveButCoveredCount`

Production use: this should be disabled by default and only enabled for diagnostics.

### 1.3 Add acceptance thresholds to diagnostics

Emit warnings when these conditions persist for more than N frames:

```text
coverage > 0.75 && visibleSupport < 0.05
coverage > 0.75 && effectiveWeight < 0.05
schedulerOverBudget == true for > 30 frames
budgetRejectedCount > requestCount * 8 for > 30 frames
activeProbeCount / scheduledProbeUpdates > targetWarmupFrames
```

These warnings turn the current failure into an automated detection.

---

## Phase 2 — Fix DDGI Frame-Time Semantics

### 2.1 Replace ring-buffer frame index in DDGI layout/update logic

Current DDGI layout creation uses the frame-in-flight index, which alternates between a small set of values. DDGI age, refresh, dirty-cell history, and stale-probe logic need a monotonically increasing frame serial.

Change this pattern:

```csharp
DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
    scene,
    camera,
    Settings.GlobalIllumination,
    _cameraRelativeDdgiClipmaps,
    unchecked((ulong)_currentFrame),
    viewPriorityHistoryReset,
    ResolveDdgiCameraVelocity(camera, viewPriorityHistoryReset),
    _ddgiLocalVolumeSlots);
```

To:

```csharp
ulong ddgiFrameSerial = _temporalSampleIndex;

DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
    scene,
    camera,
    Settings.GlobalIllumination,
    _cameraRelativeDdgiClipmaps,
    ddgiFrameSerial,
    viewPriorityHistoryReset,
    ResolveDdgiCameraVelocity(camera, viewPriorityHistoryReset),
    _ddgiLocalVolumeSlots);
```

Then pass the same serial to scheduling and cell-update marking:

```csharp
scheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(
    ddgiRayUpdateActive,
    layout,
    ddgiFrameSerial);
```

### 2.2 Audit all DDGI age uses

Search for every call or field using:

```text
frameIndex
CurrentFrameIndex
LastUpdateFrame
AgeFrames
MinimumProbeRefreshFrames
```

Classify each use as either:

- **ring index**: used only for per-frame resource rings/readback buffers.
- **monotonic serial**: used for age, refresh, history, cell initialization, and probe staleness.

Create two explicit variables in renderer code:

```csharp
int frameRingIndex = _currentFrame;
ulong frameSerial = _temporalSampleIndex;
```

No DDGI age or refresh code should receive `frameRingIndex`.

### 2.3 Regression tests

Add tests that run the clipmap/controller for at least 300 logical frames and confirm:

- Probe age increases monotonically when not updated.
- New cells reset age/history.
- `MinimumProbeRefreshFrames` triggers refresh candidates.
- Two frames-in-flight do not cap age at 0 or 1.

Suggested path:

```text
Njulf/Njulf.Tests/DdgiFrameSerialTests.cs
```

---

## Phase 3 — Rebuild the Final DDGI Composition Model

The diagnostic shows `coverage=1.000` but `visible=0.000` and `effective=0.000`. The composition model must not allow valid DDGI coverage to be completely eliminated by stacked AO, visibility, and leak suppression unless there is strong evidence of invalid data.

### 3.1 Separate five concepts

Currently several concepts are mixed into one effective weight. Separate them explicitly:

1. **Coverage**: Is a volume/probe set present for this pixel?
2. **Data confidence**: Is the probe data initialized, recent, and radiometrically valid?
3. **Visibility/leak confidence**: Does visibility suggest this probe should affect this point?
4. **Material response**: Albedo, metallic, diffuse BRDF.
5. **Screen/contact occlusion**: SSAO/contact darkening applied once to the final indirect result.

### 3.2 Replace hard suppression with confidence-weighted blending

Do not compute effective DDGI weight as:

```glsl
coverage * visibleSupport * (1.0 - contactSuppression)
```

when `visibleSupport` can collapse to zero everywhere.

Use a staged model:

```glsl
float coverage = clamp(ddgi.coverage, 0.0, 1.0);
float dataConfidence = clamp(ddgi.weight, 0.0, 1.0);
float visibilityConfidence = clamp(ddgi.visibility, 0.0, 1.0);

// Visibility should attenuate leak-prone samples but should not be the only gate.
float leakAttenuation = mix(1.0, visibilityConfidence, leakStrength);
float effectiveDdgiWeight = coverage * smoothstep(0.02, 0.25, dataConfidence);

vec3 ddgiField = ddgiDiffuse * effectiveDdgiWeight * leakAttenuation;
```

Then apply AO once after DDGI/environment mixing:

```glsl
vec3 indirect = mix(environmentDiffuse * environmentFallbackIntensity, ddgiDiffuse, effectiveDdgiWeight);
indirect *= indirectAo;
```

### 3.3 Remove double AO application

`SampleDdgiDiffuse()` currently applies `indirectAo`, while composition also uses `indirectAo` to reduce fallback and contact. Production model should apply AO once.

Change:

```glsl
vec3 SampleDdgiDiffuse(DdgiSampleResult ddgi, vec3 albedo, float metallic, float indirectAo)
{
    float diffuseWeight = 1.0 - clamp(metallic, 0.0, 1.0);
    return ddgi.irradiance * (albedo / PI) * diffuseWeight * indirectAo;
}
```

To:

```glsl
vec3 SampleDdgiDiffuse(DdgiSampleResult ddgi, vec3 albedo, float metallic)
{
    float diffuseWeight = 1.0 - clamp(metallic, 0.0, 1.0);
    return ddgi.irradiance * (albedo / PI) * diffuseWeight;
}
```

Apply `indirectAo` only in the final indirect mix.

### 3.4 Add a minimum physically plausible fallback path

When coverage is valid but visibility confidence is low, do not return black. Use a conservative blend toward environment diffuse:

```glsl
float ddgiTrust = effectiveDdgiWeight * leakAttenuation;
float environmentTrust = 1.0 - ddgiTrust;
vec3 indirect = ddgiDiffuse * ddgiTrust + environmentDiffuse * environmentFallbackIntensity * environmentTrust;
```

This is not a workaround. It is the correct low-confidence behavior: untrusted DDGI should yield to broad environment irradiance, not zero irradiance.

### 3.5 Composition acceptance tests

Create shader-equivalent CPU tests for these cases:

| coverage | data | visibility | AO | Expected result |
|---:|---:|---:|---:|---|
| 1.0 | 1.0 | 1.0 | 1.0 | full DDGI |
| 1.0 | 1.0 | 0.0 | 1.0 | environment fallback + reduced DDGI, not black |
| 1.0 | 0.0 | 1.0 | 1.0 | environment fallback |
| 0.0 | 0.0 | 0.0 | 1.0 | environment fallback |
| 1.0 | 1.0 | 0.2 | 0.5 | indirect is AO-darkened once |

Suggested path:

```text
Njulf/Njulf.Tests/DdgiCompositionTests.cs
```

---

## Phase 4 — Correct DDGI Visibility and Leak Confidence

The diagnostic says visible support is zero. This means either the visibility moments are wrong, the probe-to-point distance comparison is too strict, or visibility is being interpreted too destructively.

### 4.1 Instrument visibility moments

Add debug counters for:

- Average visibility moment mean.
- Average visibility variance.
- Average probe-to-point distance.
- Count where `probeDistance > mean` by large margins.
- Count where visibility is exactly zero.
- Count where visibility is zero but irradiance confidence is non-zero.

Add a debug view:

```csharp
GlobalIlluminationDebugView.DdgiVisibilityMoments
```

With RGB channels:

```text
R = mean / maxRayDistance
G = sqrt(variance) / maxRayDistance
B = probeDistance / maxRayDistance
```

### 4.2 Validate visibility direction convention

In the forward shader, visibility is sampled using:

```glsl
vec3 probeToPointDirection = probeToBiasedPoint / biasedDistanceToProbe;
ReadDdgiProbeVisibility(probeIndex, probeToPointDirection)
```

In the update shader, visibility is written for ray directions emitted from the probe. Confirm that both use the same convention:

```text
visibility texel direction = direction from probe to hit/point
forward query direction = direction from probe to shaded point
```

If any code uses point-to-probe direction, visibility will be sampled from the wrong octahedral lobe and can become systematically wrong.

### 4.3 Store visibility confidence separately from leak attenuation

Visibility has two jobs:

1. Prevent leaking through walls.
2. Express confidence in probe-to-point transport.

Do not let visibility alone zero the whole probe contribution. Store and use:

```glsl
float visibilityTransport = EvaluateDdgiVisibility(...);
float visibilityConfidence = smoothstep(0.02, 0.40, visibilityTransport);
float leakAttenuation = mix(1.0, visibilityTransport, leakStrength);
```

Use `visibilityConfidence` for diagnostics and quality, but apply `leakAttenuation` gently to irradiance.

### 4.4 Fix moment variance floor per cascade/spacing

Current visibility uses a fixed variance floor. In large or small spaces, this may be too strict.

Use a spacing-scaled variance floor:

```glsl
float minVariance = max(0.005, info.minProbeSpacing * info.minProbeSpacing * 0.0025);
float variance = max(mean2 - mean * mean, minVariance);
```

The exact coefficient should be tuned from diagnostic histograms, not guessed.

### 4.5 Acceptance criteria

For the failing screenshot area:

```text
coverage >= 0.75
visibleSupport >= 0.15
effectiveWeight >= 0.15
zeroVisibleButCoveredCount / coveredCount < 5%
```

---

## Phase 5 — Make Probe State Initialization and Warmup Deterministic

The current frame has 32,648 active probes but only 144 updates reported by the GI summary and 66 GPU scheduler requests in readback. This is too low for a large active set.

### 5.1 Define warmup states

Add explicit DDGI runtime states:

```csharp
public enum DdgiRuntimeWarmupState
{
    Disabled,
    ColdStart,
    LocalVolumeWarmup,
    NearCascadeWarmup,
    SteadyState,
    Recovery
}
```

State transitions:

```text
ColdStart -> LocalVolumeWarmup when DDGI first activates
LocalVolumeWarmup -> NearCascadeWarmup when visible local probes reach confidence target
NearCascadeWarmup -> SteadyState when cascade 0 visible probes reach confidence target
Recovery when camera cut/teleport/resource rebuild occurs
```

### 5.2 Prioritize visible local and cascade-0 probes

During warmup, use strict budget allocation:

```text
50% local authored volumes
35% camera cascade 0 visible/frustum probes
10% newly exposed scroll cells
5% safety/age refresh
```

Only after local/cascade-0 confidence reaches target should far cascades receive substantial budget.

### 5.3 Track warmup completion by confidence, not frame count

A probe should count as warmed when:

```text
activeProbe > 0.5
irradianceConfidence > 0.25
visibilityConfidence > 0.10
lastUpdateAge <= warmupMaxAge
```

Aggregate per volume/cascade:

```text
warmedVisibleProbeFraction
warmedLocalProbeFraction
warmedCascade0ProbeFraction
```

Expose this in diagnostics.

### 5.4 Prevent visible areas from using cold probes as black

When a probe is selected but not initialized or low-confidence, reduce DDGI trust and blend toward fallback. Do not interpret missing initialized data as black irradiance.

Shader rule:

```glsl
if (dataConfidence <= epsilon)
{
    // Contributes coverage information but no black lighting.
    // Final composition uses environment fallback for this portion.
}
```

### 5.5 Acceptance criteria

At startup in the plaza scene:

```text
local visible warmup >= 80% within 30 frames
cascade0 visible warmup >= 80% within 60 frames
DDGI effective weight in covered visible region > 0.15 within 60 frames
```

---

## Phase 6 — Redesign the GPU Scheduler for High Performance

Current diagnostic:

```text
candidates=11502
requests=66
budgetRejected=11436
scheduleUs=3821
scheduleP95Us=7047
scheduleOverBudget=1
```

This indicates the scheduler is generating and processing far too many candidates for too few accepted requests.

### 6.1 Cap candidate generation before prefix/finalize

Do not generate unbounded candidates and reject almost all of them later. Apply per-class generation caps before writing candidate records.

Suggested limits:

```text
visibleFrustumCandidateCap = requestBudget * 2
safetyCandidateCap = requestBudget
ageCandidateCap = requestBudget / 2
dirtyCandidateCap = min(dirtyProbeCount, requestBudget * 2)
```

Global cap:

```text
totalCandidateCap = requestBudget * 4
```

### 6.2 Use priority quotas instead of one global rejection phase

Allocate request budget by priority class:

```text
P0 new cells / streamed local slots: 30–50%
P1 dirty geometry/material/light: 20–30%
P2 visible frustum confidence/variance: 20–40%
P3 safety/age/far cascade: 5–15%
```

Unused budget flows downward to lower-priority classes.

### 6.3 Replace expensive global finalize with bounded top-K per bucket

The current `finalize` stage dominates schedule cost. Convert candidate handling to bounded top-K per priority bucket.

Algorithm:

1. Each workgroup produces a small local top-K list.
2. Workgroups atomically append only their top candidates per bucket.
3. A final compact pass merges bounded bucket lists.
4. Stop once request and primary-ray budgets are exhausted.

This prevents an 11,502-candidate finalize pass when only 66 probes will be updated.

### 6.4 Make request/ray budgets adaptive but not self-starving

Current scheduler produced only 66 requests despite `ddgiUpdated=144` and a large active set. Adaptive budget reductions should not permanently starve visible probes.

Rules:

- Set a hard minimum visible-near-field update count.
- Set a hard minimum local-volume update count when local volumes are visible.
- If scheduler P95 is over budget, reduce far/safety/age work first.
- Never reduce P0 new-cell/local-visible budget below the warmup minimum.

Example:

```csharp
MinimumVisibleNearFieldUpdates = 256;
MinimumLocalVolumeUpdates = 128;
MinimumNewCellUpdates = 128;
```

These are tier-dependent.

### 6.5 Split scheduler into warmup and steady-state modes

Warmup mode:

- Smaller candidate universe.
- Prioritize local and cascade 0.
- Avoid broad safety shell scans.

Steady-state mode:

- Use confidence/variance/age-driven refresh.
- Lower per-frame budget.
- Safety refresh is distributed temporally.

### 6.6 Scheduler performance acceptance criteria

For the diagnostic scene:

```text
scheduleP95Us <= 500
scheduleOverBudget = 0 for 300-frame run
budgetRejected <= requests * 4
requests >= min(visibleWarmupBudget, configured budget)
primaryRays close to primaryRayBudget when warmup needs work
```

---

## Phase 7 — Rationalize Probe Counts and Memory by Quality Tier

The current setup uses approximately 32,648 active probes and reports DDGI bytes around 525 MB. This is too expensive for a high-performance production tier.

### 7.1 Define explicit DDGI tier budgets

Example production tiers:

| Tier | Cascades | Cascade 0 grid | Local volume max | DDGI memory target | DDGI GPU target |
|---|---:|---:|---:|---:|---:|
| Low | 2 | 16×6×16 | 1 small | ≤ 64 MB | ≤ 1.0 ms |
| Medium | 3 | 20×8×20 | 2 small | ≤ 128 MB | ≤ 1.8 ms |
| High | 3–4 | 24×10×24 | 2–4 local | ≤ 192 MB | ≤ 2.5 ms |
| Ultra | 4 | 32×12/16×32 | 4+ local | ≤ 384 MB | ≤ 4.0 ms |

### 7.2 Do not allocate all high-density coverage globally

Use density where it matters:

- Dense authored local volumes for interiors/alleys.
- Moderate cascade 0 camera-relative grid.
- Coarser far cascades.
- No dense all-world catch-all volume.

### 7.3 Use volume admission by screen importance and confidence need

Local volumes should enter the local pool when:

```text
camera inside volume OR volume projected coverage > threshold OR volume has high priority
```

They should stay resident while their contribution remains visible or until a higher-priority volume evicts them.

### 7.4 Expose memory diagnostics per DDGI resource

Break down current `bytes=525093744` into:

```text
probe volume buffer
probe state buffer
irradiance atlas
visibility atlas
ray scratch
update queue
scheduler buffers
gather tile buffer
local slot reserved pool
```

This will show whether the cost is atlas capacity, scratch allocation, scheduler buffers, or reserved local slots.

### 7.5 Memory acceptance criteria

For High tier:

```text
DDGI total memory <= configured tier budget
ray scratch <= maxProbeUpdatesPerFrame * raysPerProbe * rayResultStride
local slot reserved pool <= visible local volume needs
no hidden allocation proportional to absolute max probes unless required by active budget
```

---

## Phase 8 — Production Relocation and Classification

The current relocation magnitude is tiny in diagnostics (`reloc=0.004`), and probes are active. Relocation is not the immediate reason for black output, but it still needs production hardening.

### 8.1 Keep relocation as free-space correction, not surface snapping

Probe position should remain:

```text
physical probe position = logical lattice position + stable relocation offset
```

Relocation target:

```text
minimum free-space distance from nearest surface
```

Not:

```text
snap probe directly onto a surface
```

### 8.2 Store nearest-surface statistics

During trace, store per-probe aggregate:

```text
nearestHitDistance
closeHitRatio
backfaceRatio
missRatio
relocationDirection
```

Use these to compute relocation and classification.

### 8.3 Use confidence-aware relocation

Only apply relocation when evidence is strong:

```glsl
float relocationEvidence = smoothstep(0.10, 0.35, closeRatio) * (1.0 - missRatio);
vec3 targetRelocation = relocationDirection * targetDistance * relocationEvidence;
```

Clamp:

```glsl
maxRelocationDistance = minProbeSpacing * settings.DdgiRelocationMaxDistanceFraction;
```

Blend with history using a monotonic frame serial and reset on new logical cell.

### 8.4 Classification should not black-hole valid regions

Classification should mark truly invalid probes inactive, but final gathering should handle inactive probe coverage by falling back to trusted neighboring probes/environment rather than black.

Acceptance:

```text
inactive probe fraction stable and explainable
covered pixels with inactive local candidates still receive fallback/environment
no flickering active/inactive state near walls
```

---

## Phase 9 — Correct Pass Ordering and Cache Publication Semantics

The current production order updates DDGI after forward shading. This means forward shading uses the previous DDGI cache. That is acceptable for production, but it must be explicit and handled during startup/warmup.

### 9.1 Keep one-frame-latency cache, but expose cache generation

Track:

```text
DdgiCacheGeneration
DdgiLastUpdatedFrameSerial
DdgiCacheWarmupState
```

Forward pass should know whether the cache is cold, warming, or steady.

### 9.2 Avoid black during cold cache

When cache is cold or confidence is low, final shading uses environment fallback. It should not use zero initialized DDGI data as black light.

### 9.3 Optional future mode: pre-forward warmup pass

For scene loads or camera cuts, a limited pre-forward DDGI warmup pass can update a small set of visible local/cascade-0 probes before the first shaded frame. This should be a controlled production feature, not a debug workaround.

Example:

```text
PreForwardDdgiWarmupPass: max 128 probes, local + cascade0 only, only on scene load/camera cut
```

---

## Phase 10 — Validation Suite

### 10.1 Add deterministic DDGI test scenes

Create small scenes with known expected behavior:

1. Open sky box with diffuse ground.
2. Thin-wall corridor with sunlight at one end.
3. Sponza-like courtyard with sunlit upper wall and shadowed lower arcade.
4. Local dense volume inside a small room.
5. Camera-relative scrolling test.
6. Teleport/camera-cut test.

### 10.2 Automated metrics per scene

Capture and assert:

```text
mean shadowed indirect luminance
mean sunlit indirect luminance
coverage mean
visible support mean
effective weight mean
zero-visible-covered fraction
scheduler P95
DDGI GPU P95
DDGI memory
warmup frame count
```

### 10.3 Image regression gates

For each test scene:

- Save golden debug buffers:
  - final color
  - DDGI raw diffuse
  - DDGI effective weight
  - DDGI coverage
  - DDGI visibility
  - suppression mask
- Compare with tolerance.
- Fail CI on large luminance regressions or effective-weight collapse.

### 10.4 CPU/GPU scheduler equivalence tests

For a fixed scene and camera:

- Run CPU reference scheduling.
- Run GPU scheduling.
- Compare request counts, priorities, invalid probes, duplicates, and per-volume distribution.
- Do not require identical ordering, but require equivalent coverage and priority constraints.

---

## Phase 11 — Implementation Order

### Step 1 — Frame serial correctness

- Add `frameSerial` and `frameRingIndex` naming.
- Pass monotonic serial to DDGI layout, scheduler, update marking, and age logic.
- Add tests for age/refresh.

Expected result:

```text
DdgiAverageProbeAge and DdgiStaleProbeCount become meaningful.
Age refresh starts behaving predictably.
```

### Step 2 — Composition refactor

- Remove double AO application.
- Separate coverage, data confidence, visibility/leak confidence, and final AO.
- Ensure low-confidence DDGI blends to environment fallback, not black.
- Add CPU tests for composition.

Expected result:

```text
coverage=1 should not produce effective=0 unless data confidence is truly zero and fallback is also zero.
```

### Step 3 — Visibility diagnostics and tuning

- Add visibility moment debug view and counters.
- Validate octahedral direction convention.
- Use spacing-scaled variance floor.
- Convert visibility from hard gate to leak attenuation/confidence.

Expected result:

```text
ddgiEstimate visible rises above zero in covered regions.
DdgiVisibility debug view explains remaining dark areas.
```

### Step 4 — Warmup state machine

- Add DDGI warmup states.
- Allocate budgets first to local and cascade-0 visible probes.
- Track warmed visible probe fraction.

Expected result:

```text
Visible shadowed areas receive DDGI within the warmup target.
```

### Step 5 — Scheduler redesign

- Add per-priority quotas.
- Cap candidate generation before prefix/finalize.
- Replace expensive finalize with bounded bucket/top-K selection.
- Add scheduler tests and P95 budget gates.

Expected result:

```text
schedulerP95 <= 0.5 ms
budgetRejected <= requests * 4
scheduleOverBudget=0 steady-state
```

### Step 6 — Memory tiering

- Break down DDGI memory by resource.
- Define tier budgets.
- Resize active probe counts and local slot pool by tier.
- Ensure scratch buffers scale with update budget, not absolute probe count.

Expected result:

```text
High tier DDGI memory stays within the configured target.
```

### Step 7 — Relocation/classification hardening

- Add nearest-distance statistics.
- Make relocation evidence-based and stable.
- Prevent classification from causing black output.
- Add corridor/thin-wall regression scenes.

Expected result:

```text
Reduced wall leaking without suppressing valid low-frequency bounce.
```

### Step 8 — Production polish

- Remove or hide debug-only flags from normal presets.
- Add per-tier defaults for DDGI, AO, shadows, and environment fallback.
- Add user-facing diagnostics for DDGI health.
- Add CI tests and performance snapshots.

Expected result:

```text
DDGI is stable, explainable, performant, and regression-tested.
```

---

## What Not to Ship

Do not ship any of these as the final fix:

- Disabling AO to make DDGI visible.
- Forcing probes active.
- Bypassing visibility entirely.
- Using only raw diffuse debug view.
- Forcing CPU scheduler in normal runtime.
- Making a huge dense authored volume around the whole map.
- Increasing update budgets until the GPU is over budget.
- Treating environment fallback as the primary lighting solution.

These are useful diagnostic toggles, not production solutions.

---

## Final Production Definition of Done

DDGI is production-ready when all of these are true:

```text
coverage > 0.75 in target regions
visible support > 0.15 in target regions
effective weight > 0.15 in target regions after warmup
zero-visible-covered fraction < 5%
scheduler P95 <= 0.5 ms
DDGI total GPU P95 <= tier target
DDGI memory <= tier target
startup visible warmup <= 60 frames for near field
no persistent scheduleOverBudget
no reliance on debug bypass flags
image regression tests pass
CPU/GPU scheduler validation passes
```

