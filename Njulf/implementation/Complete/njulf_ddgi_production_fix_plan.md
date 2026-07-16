# Njulf DDGI Production Readiness Fix Plan

## Purpose

This plan turns the current DDGI implementation from a visually unstable/debug-oriented state into a production-ready system. It avoids intensity hacks and temporary bypasses. Every step has a target symptom, implementation work, validation, and acceptance criteria.

The current diagnostic pattern is decisive:

```text
DDGI enabled: yes
Ray query active: yes
Update/publish executing: yes
Gather fallback: 0
Empty gather tiles: 0
DDGI estimate: coverage=1.000, visible=0.000, effective=0.000
```

That means the renderer is finding DDGI volumes and gather candidates, but the forward shader still considers the gathered result to have no usable contribution.

A second major problem is scheduler health. The logs show repeated scheduler over-budget states, huge candidate overflow, and very low request counts in the larger probe setup.

---

## Current Issues to Fix

### Issue 1 — Spatial coverage is being treated as usable lighting support

**Symptom**

Pixels report full DDGI coverage while visible/effective DDGI contribution remains zero:

```text
coverage=1.000, visible=0.000, effective=0.000
```

This means the forward path is treating “this volume contains the pixel” as equivalent to “this volume has usable DDGI data for the pixel.” Those must be separate states.

**Production goal**

A DDGI volume may spatially cover a pixel, but it must not consume ownership or block other candidates unless it has usable probe data.

### Issue 2 — `ddgi.weight` / data confidence remains zero

**Symptom**

Effective DDGI remains zero even after update and publish execute. In the current shader, effective weight depends mainly on `ddgi.weight` / data confidence. If this remains zero, no amount of spatial coverage will light the scene.

**Production goal**

The renderer must expose and validate the full data-confidence chain:

```text
probe active state
irradiance atlas confidence
probe quality confidence
visibility confidence
sample totalWeight / expectedWeight
final ddgi.weight
```

### Issue 3 — Visibility moments may reject valid probes or default to black

**Symptom**

The debug views show large black regions in visibility/effective-style outputs. If visibility moments are uninitialized, zero, or over-aggressive, `EvaluateDdgiVisibility()` can reduce every candidate to zero.

**Production goal**

Uninitialized visibility data must never masquerade as valid occlusion. Visibility should be valid only when backed by warmed probe data.

### Issue 4 — Cache warmup state can suppress valid data globally

**Symptom**

The forward shader contains a global cold-cache gate. If cache generation/warmup state lags or remains cold, final confidence can be set to zero even when individual probes have valid data.

**Production goal**

Cache state should describe readiness and drive fade-in/debugging, not hard-kill valid warmed probe samples globally.

### Issue 5 — Scheduler throughput and candidate overflow are not production-ready

**Symptoms**

The diagnostics show combinations like:

```text
considered=17672
requests=153-768
scheduler overflow=~9979-17325
scheduleOverBudget=1
```

and in the larger setup:

```text
considered=32648
requests=66
rejected budget=11436
scheduleUs≈3820
scheduleP95Us≈7047
```

That is too much scheduler work and too little useful probe update throughput.

**Production goal**

The scheduler must prioritize visible/local/new/low-confidence probes predictably, stay within budget, and avoid scanning or overflowing huge candidate sets every frame.

### Issue 6 — Diagnostics do not distinguish coverage, support, visibility, and effective contribution

**Symptom**

The current diagnostic line reports `coverage/visible/effective`, but `coverage` is spatial, `visible` is not the same as usable data support, and `effective` is post-composition. This makes debugging ambiguous.

**Production goal**

Diagnostics must expose each stage of the DDGI contribution pipeline independently.

### Issue 7 — DDGI visual integration is too brittle

**Symptom**

Indirect contribution is either absent or very weak in shadowed areas. The shader currently has several multiplicative gates: data confidence, visibility/leak attenuation, AO, albedo, and fallback suppression.

**Production goal**

The final composition should be physically plausible, stable, and fail-soft: missing DDGI support should allow environment fallback or a farther cascade to contribute instead of producing black.

### Issue 8 — Performance is over budget on the target hardware

**Symptoms**

The logs show over-budget CPU/GPU frames, high forward cost, large shadow memory, and DDGI scheduler/update costs that fluctuate strongly.

**Production goal**

DDGI must fit into a predictable per-frame budget. On RTX 3060 laptop-class hardware, target scheduler cost should be sub-millisecond and DDGI update cost should be capped by quality tier.

---

# Step-by-Step Fix Plan

## Phase 0 — Freeze a Reproducible Baseline

### 0.1 Add a fixed DDGI repro preset

Create a named preset for this Sponza alley/courtyard scene:

```csharp
RenderQualityPreset.DdgiSponzaDebugBaseline
```

It should lock:

```csharp
gi.Mode = GlobalIlluminationMode.Ddgi;
gi.UseSsgi = false;
gi.UseDdgi = true;
gi.UseRayQueryBackend = true;
gi.DdgiCameraRelativeEnabled = true;
gi.DdgiProbeClassificationEnabled = true;
gi.DdgiProbeRelocationEnabled = true;
```

It should also lock camera-relative clipmap counts, local volume bounds, update budgets, AO state, exposure, and environment fallback.

### 0.2 Add a deterministic camera bookmark

Add a camera bookmark system for known DDGI test views:

```text
SponzaAlley_01
SponzaCourtyard_01
SponzaInterior_01
CornellBox_01
ThinWallRoom_01
```

Each bookmark should include camera position, yaw, pitch, FOV, near/far planes, and expected DDGI volume selection.

### 0.3 Store the baseline screenshots and diagnostics

Save these per run:

```text
final image
DDGI raw diffuse
DDGI coverage
DDGI support/data confidence
DDGI visibility moments
DDGI effective weight
DDGI suppression mask
scheduler diagnostics
volume diagnostics
GPU timings
```

**Acceptance criteria**

A single command should reproduce the current failing case and produce the same diagnostic signature:

```text
coverage high, effective zero
```

---

## Phase 1 — Split Spatial Coverage from Usable Support

### 1.1 Extend `DdgiSampleResult`

Add separate fields:

```glsl
struct DdgiSampleResult
{
    vec3 irradiance;
    float weight;           // final usable data confidence
    float spatialCoverage;  // geometric volume/lattice coverage
    float supportCoverage;  // active, warmed, confidence-backed support
    float visibility;
    float leakClamp;
    float activeProbe;
    ...
};
```

Keep `coverage` only as a compatibility alias during migration, or rename all usages explicitly.

Recommended migration:

```glsl
#define coverage spatialCoverage // temporary only during migration
```

Remove the alias after all call sites are updated.

### 1.2 Compute both coverages in `SampleDdgiVolumeIrradiance()`

Track these separately:

```glsl
float expectedWeight = 0.0;       // all geometrically relevant corners
float spatialWeight = 0.0;        // corners inside volume/lattice support
float supportedWeight = 0.0;      // active + irradiance confidence + quality
float visibleWeight = 0.0;        // supported + visibility
```

Rules:

```text
expectedWeight increases for every geometrically relevant trilinear corner.
spatialWeight increases before confidence checks.
supportedWeight increases only after probeActive, irradianceConfidence, and qualityConfidence pass.
visibleWeight increases only after visibility evaluation.
```

The result should be:

```glsl
result.spatialCoverage = clamp(spatialWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * edgeFade;
result.supportCoverage = clamp(supportedWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * edgeFade;
result.weight = clamp(visibleWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * edgeFade;
```

Important: if `visibleWeight <= epsilon`, return spatial coverage for diagnostics, but do not report ownership support.

### 1.3 Change candidate ownership to use support, not spatial coverage

In `AccumulateDdgiCandidate()`, do not consume `remainingCoverage` using spatial coverage.

Replace this logic:

```glsl
float candidateCoverage = clamp(candidate.coverage, 0.0, 1.0);
float blendWeight = candidateCoverage * remainingCoverage;
remainingCoverage -= blendWeight;
```

With:

```glsl
float candidateSpatial = clamp(candidate.spatialCoverage, 0.0, 1.0);
float candidateSupport = clamp(candidate.supportCoverage, 0.0, 1.0);
float candidateData = clamp(candidate.weight, 0.0, 1.0);

float candidateOwnership = candidateSupport * smoothstep(0.02, 0.20, candidateData);

if (candidateOwnership <= 0.000001)
{
    // Keep candidateSpatial for diagnostics, but do not consume ownership.
    return -1.0;
}

float blendWeight = clamp(candidateOwnership * remainingCoverage, 0.0, remainingCoverage);
remainingCoverage = clamp(remainingCoverage - blendWeight, 0.0, 1.0);
```

### 1.4 Blend by useful ownership

When accumulating candidate irradiance:

```glsl
blendedIrradiance += candidate.irradiance * blendWeight;
blendedSupportCoverage += candidate.supportCoverage * blendWeight;
blendedSpatialCoverage += candidate.spatialCoverage * blendWeight;
blendedDataConfidence += candidate.weight * blendWeight;
```

Resolve result as:

```glsl
result.irradiance = blendedIrradiance / max(totalOwnership, epsilon);
result.spatialCoverage = maxSpatialCoverageSeen;
result.supportCoverage = blendedSupportCoverage / max(totalOwnership, epsilon);
result.weight = blendedDataConfidence / max(totalOwnership, epsilon);
```

### 1.5 Fallback behavior after this fix

If local volume has spatial coverage but zero support, it must not block:

```text
primary clipmap
secondary clipmap
environment fallback
```

**Acceptance criteria**

In the failing scene, this diagnostic should no longer be possible except during the first totally empty frame:

```text
spatial=1.000, support=0.000, effective=0.000, environmentFallback=0.000
```

Expected behavior after fix:

```text
spatial high, support low -> fallback or farther cascade contributes
spatial high, support high -> effective DDGI becomes nonzero
```

---

## Phase 2 — Fix Probe Data Confidence End-to-End

### 2.1 Add a per-pixel DDGI data-confidence debug view

Add debug views:

```csharp
GlobalIlluminationDebugView.DdgiSpatialCoverage
GlobalIlluminationDebugView.DdgiSupportCoverage
GlobalIlluminationDebugView.DdgiDataConfidence
GlobalIlluminationDebugView.DdgiVisibilityConfidence
GlobalIlluminationDebugView.DdgiEffectiveWeight
```

Map them to stable shader constants.

### 2.2 Expose the confidence chain in `SampleDdgiVolumeIrradiance()`

For the strongest contributing probe, expose:

```glsl
stateIrradiance.w
irradianceAtlasSample.w
qualityAndReason.x  // ray hit confidence
qualityAndReason.y  // state irradiance confidence
qualityAndReason.z  // visibility confidence
probeActive
visibility
supportWeight
totalWeight
expectedWeight
```

Pack into debug output modes or a small diagnostics storage buffer.

### 2.3 Validate irradiance atlas confidence writes

In the DDGI blend/update shader, verify that each irradiance texel stores confidence consistently:

```glsl
vec4(irradiance.rgb, confidence)
```

The confidence should mean:

```text
this texel has enough valid samples to be trusted for this direction
```

It should not become zero merely because radiance is dark. Dark but valid irradiance is still valid data.

### 2.4 Separate dark irradiance from invalid irradiance

This is critical. A black corner of the scene can legitimately have low radiance. That must not be the same as missing data.

Use separate values:

```text
irradiance luminance -> lighting intensity
confidence alpha     -> data validity
```

The renderer must allow:

```text
radiance = 0.02, confidence = 1.0
```

and distinguish it from:

```text
radiance = 0.0, confidence = 0.0
```

### 2.5 Add invariants

Add shader or CPU diagnostics warnings:

```text
coverage high + support zero + active probes nonzero
support high + irradiance zero + confidence high
visibility zero + visibility moments uninitialized
```

**Acceptance criteria**

The debug view should show nonzero `DdgiDataConfidence` for warmed visible probes, even if the irradiance is dark.

---

## Phase 3 — Initialize Visibility and Irradiance Safely

### 3.1 Initialize visibility moments to non-occluding values

Uninitialized visibility moments must not behave as “fully occluded.”

For each new probe visibility texel, initialize to:

```glsl
mean  = maxRayDistance;
mean2 = maxRayDistance * maxRayDistance;
```

or use an explicit confidence channel/sidecar state and ignore visibility until confidence is valid.

### 3.2 Initialize irradiance as invalid-but-safe

Initialize irradiance atlas to:

```glsl
vec4(0.0, 0.0, 0.0, 0.0)
```

Meaning:

```text
black radiance, zero confidence
```

This is acceptable only if Phase 1 is implemented, because invalid probes no longer consume ownership.

### 3.3 Add visibility confidence

Visibility moments currently have only mean/mean2. Add confidence through one of these production options:

Option A — Store confidence in probe state:

```glsl
qualityAndReason.z = visibilityConfidence;
```

Option B — Add a separate visibility confidence atlas:

```text
R8/R16 per visibility texel or per probe
```

Option A is cheaper and enough for now.

### 3.4 Do not evaluate visibility from invalid moments

When visibility confidence is low:

```glsl
visibility = 1.0;
visibilityTrust = 0.0;
```

Then data support should remain low because visibility trust is low, but the sample should not produce a false occlusion that kills all lighting.

### 3.5 Test uninitialized probes explicitly

Create a unit/GPU test:

```text
Clear DDGI resources
Render one frame
Assert spatial coverage may be high
Assert support coverage is zero
Assert environment fallback is not blocked
Assert visibility debug does not report false full occlusion
```

**Acceptance criteria**

Freshly initialized probes should never cause:

```text
spatial=1, support=0, fallback=0
```

---

## Phase 4 — Fix Cache Warmup Semantics

### 4.1 Replace global cold-cache hard zeroing

Current behavior should not globally force data confidence to zero once some probes are valid.

Replace:

```glsl
if (DdgiCacheCold())
    dataConfidence = 0.0;
```

With a scalar that only affects blending/fade-in:

```glsl
float cacheReadiness = DdgiCacheReadiness(); // 0..1
float dataConfidence = clamp(ddgi.weight * cacheReadiness, 0.0, 1.0);
```

But production preferred behavior is per-probe/per-region confidence, not global readiness.

### 4.2 Split cache validity from warmup phase

Use separate meanings:

```text
cacheGeneration == 0       -> no published DDGI data exists
warmupState != SteadyState -> some regions are still warming
```

Only `cacheGeneration == 0` should fully disable DDGI sampling.

Warmup state should influence scheduling and optional fade-in, not invalidate warmed probes.

### 4.3 Store dynamic cache state in a dedicated small buffer

Do not rely on static volume metadata upload timing for rapidly changing cache generation/warmup state.

Add:

```csharp
GPUDdgiRuntimeHeader
{
    uint CacheGeneration;
    uint LastUpdatedFrameSerial;
    uint WarmupState;
    float VisibleWarmupFraction;
    float LocalWarmupFraction;
    float Cascade0WarmupFraction;
}
```

Upload or write this after publish.

### 4.4 Make publish ordering explicit

Current production order can keep forward shading one frame behind DDGI updates. That is acceptable, but must be explicit:

```text
Frame N forward reads cache from frame N-1
Frame N DDGI update writes cache for frame N+1
```

Diagnostics should print:

```text
forwardCacheGeneration
publishedCacheGeneration
cacheLatencyFrames
```

### 4.5 Add cache-state warnings

Warn when:

```text
cacheGeneration > 0 but forward cacheGeneration remains 0
cache warmup state remains ColdStart for > N frames
visibleWarmupFraction does not increase while updateExec=1
```

**Acceptance criteria**

After standing still, cache state should progress:

```text
NoCache -> LocalVolumeWarmup -> NearCascadeWarmup -> SteadyState
```

and warmed probe samples must contribute before the whole system reaches steady state.

---

## Phase 5 — Rework Final DDGI Composition

### 5.1 Base ownership on support, not spatial coverage

Final composition should use:

```glsl
float support = clamp(ddgi.supportCoverage, 0.0, 1.0);
float data = clamp(ddgi.weight, 0.0, 1.0);
float visibility = clamp(ddgi.visibility, 0.0, 1.0);
```

Then:

```glsl
float ddgiTrust = support * smoothstep(0.02, 0.20, data);
```

### 5.2 Treat leak/visibility as attenuation, not ownership

Visibility/leak can attenuate the DDGI field, but should not decide whether the environment fallback is blocked.

Use:

```glsl
float leakAttenuation = mix(0.15, 1.0, visibilityOrLeakTrust);
vec3 ddgiField = ddgiDiffuse * ddgiTrust * leakAttenuation;
```

Environment fallback should be:

```glsl
float fallbackWeight = (1.0 - ddgiTrust) * environmentFallbackIntensity;
```

not:

```text
1 - spatialCoverage
```

### 5.3 Avoid AO double-killing DDGI

AO should not be multiplied repeatedly through:

```text
SampleDdgiDiffuse
ComposeHybridDiffuseGi
near-contact suppression
fallback suppression
```

Choose one of these production models:

**Preferred model:**

```text
Apply AO to short-range/detail indirect only.
Do not strongly apply SSAO to low-frequency DDGI.
Use bent-normal/contact terms only for close contact leaks.
```

Concrete rule:

```glsl
vec3 ddgiDiffuse = ddgi.irradiance * albedo / PI;
vec3 ddgiField = ddgiDiffuse * ddgiTrust * leakAttenuation;
vec3 fallbackField = diffuseIbl * fallbackWeight * indirectAo;
```

Do not multiply DDGI by full SSAO unless there is a proven leak problem.

### 5.4 Clamp only pathological values

Clamp radiance for NaN/inf or extreme outliers, not as a normal exposure control.

Add helpers:

```glsl
vec3 SafeRadiance(vec3 x)
{
    x = any(isnan(x)) || any(isinf(x)) ? vec3(0.0) : x;
    return clamp(x, vec3(0.0), vec3(64.0));
}
```

### 5.5 Add composition invariants

Warn when:

```text
support > 0.5 and data > 0.5 but effective == 0
spatial > 0.9 and support == 0 and fallback == 0
visibility == 0 for most pixels for more than N frames
```

**Acceptance criteria**

The final DDGI effective weight should become nonzero in warmed shadowed regions. Unsupported regions should show fallback or farther cascade contribution, not black ownership.

---

## Phase 6 — Redesign the DDGI Scheduler for Production

### 6.1 Stop treating the whole active probe set as a per-frame candidate pool

The current scheduler considers thousands to tens of thousands of probes and overflows candidate generation. Production scheduling should use bounded work groups.

Use fixed budget lanes:

```text
Lane 0: new / scrolled cells
Lane 1: visible local probes
Lane 2: visible cascade-0 probes
Lane 3: safety shell probes
Lane 4: low-confidence probes
Lane 5: age refresh / round robin
```

Each lane gets a budget. Example:

```text
new cells:       25%
local visible:   25%
cascade0 visible:25%
safety shell:    10%
low confidence:  10%
age refresh:      5%
```

During warmup, increase local/cascade0 budgets.

### 6.2 Add a probe hot-set cache

Build a small hot set from:

```text
current camera frustum
screen-space gather tiles
local volumes intersecting view
dirty regions
newly scrolled clipmap cells
```

Only the hot set should be heavily scored each frame.

### 6.3 Use hierarchical clipmap scheduling

For camera-relative clipmaps:

```text
cascade 0 gets direct per-cell scheduling
higher cascades use sparse age refresh and dirty updates
```

Do not score all cascades uniformly.

### 6.4 Make local volumes first-class scheduling targets

If local volume gather fraction is high, local probes must receive a guaranteed budget until warmed.

Add diagnostics:

```text
localVolumeVisibleProbeCount
localVolumeWarmedProbeCount
localVolumeQueuedProbeCount
localVolumeSkippedReason
```

### 6.5 Fix candidate overflow semantics

Overflow should mean “candidate buffer capacity insufficient,” not “normal operation.”

Production rule:

```text
scheduler overflow must be zero in steady state
```

If overflow occurs:

```text
increase candidate buffer only if memory budget allows
otherwise reduce candidate generation before compaction
```

Do not silently overflow every frame.

### 6.6 Prioritize low-confidence probes

The current diagnostics show no `lowConfidence` candidates despite effective DDGI being zero. Add direct low-confidence detection from GPU probe state:

```glsl
bool lowConfidence = qualityConfidence < threshold || irradianceConfidence < threshold;
```

Schedule low-confidence probes in visible/local areas before stable age refresh.

### 6.7 Avoid readback dependency for scheduling correctness

GPU readback has latency. It is fine for diagnostics/adaptive tuning, but scheduling should not require readback to know whether visible probes are cold.

Use GPU-side counters and GPU-side queues for rendering, CPU readback only for diagnostics.

### 6.8 Cap GPU scheduler time

Set target budgets by quality tier:

```text
Low:    <= 0.15 ms scheduler, <= 0.50 ms update
Medium: <= 0.25 ms scheduler, <= 1.00 ms update
High:   <= 0.35 ms scheduler, <= 1.50 ms update
Ultra:  <= 0.50 ms scheduler, <= 2.50 ms update
```

If scheduler exceeds budget:

```text
reduce candidate generation
not merely final request count
```

**Acceptance criteria**

For the 17,672-probe setup:

```text
scheduler overflow = 0 in steady state
scheduleUs < 350 us on High
visible/local requests are nonzero
low-confidence visible probes are updated
```

For the 32,648-probe setup:

```text
scheduleUs < 500 us on Ultra
requests are not starved to ~66 unless explicitly ray-budget limited
warmup reaches visible readiness within a few seconds
```

---

## Phase 7 — Make Warmup Deterministic and Visible

### 7.1 Define production warmup states

Use explicit states:

```text
NoCache
LocalVolumeWarmup
NearCascadeWarmup
SteadyState
Recovery
```

Avoid ambiguous `ColdStart` if a partial cache exists.

### 7.2 Warm local volumes before broad clipmaps

In scenes with dense local volumes:

```text
local volume visible probes first
cascade 0 next
higher cascades after visible support exists
```

### 7.3 Add warmup progress metrics

Diagnostics should include:

```text
visibleProbeCount
visibleWarmedProbeCount
localProbeCount
localWarmedProbeCount
cascade0ProbeCount
cascade0WarmedProbeCount
visibleSupportCoverageEstimate
```

### 7.4 Fade in, do not pop in

Once support/data confidence becomes valid, blend contribution in over a small number of frames:

```text
8-20 frames depending on quality tier
```

This should be a visual fade, not a data-validity gate.

### 7.5 Recovery rules

On teleport, major scene change, probe allocation change, or local slot eviction:

```text
invalidate affected ranges only
preserve unaffected volume/cascade cache
prioritize affected visible probes
```

Do not clear all DDGI resources unless the volume layout becomes incompatible.

**Acceptance criteria**

After startup:

```text
first frames: fallback visible, DDGI invalid support does not block
warmup: local/cascade0 support rises steadily
steady: effective DDGI is nonzero in shadowed visible areas
```

---

## Phase 8 — Validate Probe Relocation and Classification

### 8.1 Confirm relocation is not hiding probes inside geometry

The diagnostic shows relocation fractions around `0.029-0.058`, so relocation is active but not extreme. Still, production readiness requires validation.

Add overlay modes:

```text
logical probe position
relocated probe position
surface push direction
relocation magnitude / max relocation
invalid classification score
```

### 8.2 Separate relocation validity from lighting support

A relocated probe can be spatially valid but not yet radiance-valid. Classification/relocation should not set lighting confidence by itself.

### 8.3 Add relocation stability constraints

Use:

```text
max relocation <= 0.4 * minProbeSpacing
temporal blend for relocation
reset relocation only when physical slot maps to a new logical cell
```

### 8.4 Validate thin-wall behavior

Create test scenes:

```text
thin wall with lit side / dark side
curtains / cloth
narrow alley
corner occlusion
```

**Acceptance criteria**

Relocation should reduce leaks without turning probe support or visibility to zero across broad regions.

---

## Phase 9 — Tune Probe Volume Design for Production

### 9.1 Use authored local volumes intentionally

Dense local volumes should cover the actual visible problem areas, not arbitrary guessed bounds.

Add tools to author/inspect:

```text
volume bounds
probe spacing
probe count
active physical slot
local slot generation
streaming cell id
coverage heatmap
support heatmap
```

### 9.2 Do not let local volumes monopolize unsupported pixels

After Phase 1, local volumes can spatially cover pixels without blocking clipmaps unless their support is valid. This enables safe local volumes.

### 9.3 Set scene-specific default volume presets

Recommended presets:

```text
Outdoor broad clipmap: 1.0-1.5m cascade0 spacing
Courtyard/interior local volume: 0.5-0.75m spacing
Thin-wall local volume: 0.35-0.5m spacing
```

### 9.4 Budget active probes by tier

For RTX 3060 laptop-class hardware:

```text
High: 16k-24k active probes
Ultra: 32k max only if async and scheduler are healthy
```

Avoid defaulting to 32k+ active probes until scheduler and warmup are proven.

**Acceptance criteria**

A dense local volume should visibly improve the alley without causing global scheduler starvation.

---

## Phase 10 — Production Performance Work

### 10.1 Enable async compute for DDGI safely

DDGI updates are for the next frame, so they are good async candidates.

Plan:

```text
Forward pass reads previous DDGI cache
DDGI schedule/trace/blend/classify/publish run async after required TLAS/resources are available
Next frame forward consumes published cache
```

Add queue ownership barriers only for buffers actually shared across queues.

### 10.2 Reduce forward shader DDGI cost

The forward pass should not do expensive exhaustive DDGI sampling in normal operation.

Production path:

```text
gather tile -> local volume + primary clipmap + optional secondary only
no exhaustive scan except debug or emergency fallback
```

Add a counter for exhaustive fallback usage. It should be near zero in normal frames.

### 10.3 Make gather tiles support-aware

CPU/GPU gather tile selection should know whether local volumes and clipmaps are warmed enough.

Tile data should include:

```text
local volume index
local support readiness
primary clipmap index
primary support readiness
secondary clipmap index
blend hint
```

This avoids forward shading trying unsupported candidates first.

### 10.4 Reduce shadow cost interaction

The screenshot is dominated by directional shadows. DDGI must not be used to compensate for incorrect or too-dark direct shadowing.

Validate separately:

```text
direct-only image
DDGI raw diffuse only
final direct + DDGI
```

### 10.5 Stabilize GPU memory budget

Track DDGI memory separately:

```text
volume metadata
probe state
relocation/classification
irradiance atlas
visibility atlas
ray scratch
scheduler buffers
gather tiles
```

Add budget policies:

```text
reduce active probes before exceeding memory budget
reduce visibility resolution before disabling DDGI
reduce far cascades before local/cascade0
```

**Acceptance criteria**

Production High preset target:

```text
scheduler < 0.35 ms
DDGI update < 1.5 ms
forward DDGI overhead < 0.5 ms
no scheduler overflow in steady state
memory within configured budget
```

---

## Phase 11 — Regression Tests and Tooling

### 11.1 Shader unit-style tests

Create CPU mirror tests for:

```text
EvaluateDdgiVisibility
coverage/support computation
candidate ownership blending
cache readiness logic
fallback weighting
```

### 11.2 GPU scene tests

Create automated screenshot/metric tests:

```text
CornellBox_Static
Sponza_Alley_Shadowed
Sponza_Courtyard_Sunlit
ThinWallRoom
CameraScroll_Clipmap
LocalVolume_StreamInOut
```

### 11.3 Expected metric thresholds

For each test, define:

```text
average support coverage
average effective DDGI weight
fallback weight
visible warmed fraction
scheduler time
update time
candidate overflow
```

### 11.4 RenderDoc validation checklist

For a failing pixel, inspect:

```text
selected gather tile
selected volume index
probe indices sampled
probe states
irradiance atlas texels
visibility atlas texels
ddgi.weight
ddgi.supportCoverage
effectiveDdgiWeight
final color contribution
```

### 11.5 CI guards

Add tests that fail if:

```text
spatial coverage is high but support/effective/fallback are all zero
scheduler overflow persists in steady state
cache remains cold for too many frames
visible local probes are starved
```

---

# Implementation Order

## Milestone 1 — Correctness First

1. Add `spatialCoverage` and `supportCoverage` to `DdgiSampleResult`.
2. Update `SampleDdgiVolumeIrradiance()` to compute spatial/support/visible weights separately.
3. Update `AccumulateDdgiCandidate()` so unsupported candidates do not consume ownership.
4. Update `ResolveDdgiAccumulation()` to aggregate spatial/support/data separately.
5. Update `ComposeHybridDiffuseGi()` to use support/data for trust and fallback suppression.
6. Add debug views for support coverage and data confidence.
7. Validate that `coverage=1/effective=0` no longer blocks fallback or farther cascades.

## Milestone 2 — Visibility and Data Validity

1. Initialize visibility moments to safe non-occluding values or add explicit visibility confidence.
2. Ensure irradiance confidence distinguishes dark valid data from invalid data.
3. Add debug output for probe confidence chain.
4. Validate raw diffuse, support, visibility, and effective-weight views.

## Milestone 3 — Cache and Warmup

1. Replace global `DdgiCacheCold()` hard kill with explicit cache validity/readiness.
2. Add runtime DDGI header for cache generation and warmup fractions.
3. Make warmup per-region, not global.
4. Add warmup diagnostics and warnings.

## Milestone 4 — Scheduler Productionization

1. Replace broad candidate scoring with bounded scheduling lanes.
2. Add hot-set scheduling from gather tiles/frustum/local volumes.
3. Guarantee local/cascade0/new-cell budgets during warmup.
4. Add low-confidence probe scheduling.
5. Eliminate steady-state scheduler overflow.
6. Tune budgets by quality tier.

## Milestone 5 — Performance and Shipping Quality

1. Move DDGI update work to async compute where supported.
2. Reduce forward DDGI cost to tile-guided sampling only.
3. Add active probe and memory budget policies.
4. Add regression scenes and CI guards.
5. Tune Sponza and thin-wall presets.

---

# Done Criteria

DDGI is production-ready when all of the following are true:

```text
No high-spatial / zero-support / zero-fallback black ownership regions.
Effective DDGI weight becomes nonzero in warmed shadowed areas.
Unsupported local volumes do not block clipmaps or environment fallback.
Visibility moments do not falsely occlude uninitialized probes.
Cache generation and warmup state progress predictably.
Scheduler overflow is zero in steady state.
Visible/local/low-confidence probes are updated before age-only refresh.
DDGI update and scheduler costs stay within the selected quality-tier budget.
Debug views make every DDGI failure mode obvious.
Automated regression scenes pass visual and numeric thresholds.
```

---

# Do Not Do

Avoid these because they hide the real bugs:

```text
Do not raise DDGI intensity to compensate for zero effective weight.
Do not disable visibility permanently.
Do not disable classification permanently.
Do not rely on exhaustive gather as the normal production path.
Do not let spatial coverage suppress fallback.
Do not increase probe count until scheduler overflow is fixed.
Do not tune AO or exposure until raw/support/effective DDGI are correct.
```
