# DDGI Production Readiness Implementation Plan

## Goal

Make DDGI indirect lighting and bounce lighting reliable enough for production use in the `Simplified` branch. The plan focuses on fixing the current failure modes first, then hardening the feature with diagnostics, tests, validation scenarios, performance gates, and rollback controls.

## Primary suspected failure modes

1. **Forward DDGI gather can return black instead of falling back.**  
   `forward.frag` has an exhaustive DDGI sampler, but `SampleDdgiIrradiance()` currently returns an empty result when the gather tile is missing or marked fallback. Any tile without a valid candidate can therefore lose all DDGI even if probe data exists.

2. **Bounce feedback uses an inconsistent intensity convention.**  
   The trace path writes intensity-scaled radiance into ray results, forward shading multiplies by global intensity again, and stable DDGI feedback divides by global intensity before using the field for a second bounce. This makes bounce energy unpredictable and can make it appear absent.

3. **Forward GI application is gated by depth pre-pass.**  
   DDGI itself can shade from world position, normal, and probe buffers, but the current forward flag path can disable all GI when `DepthPrePassEnabled == false`.

4. **Final DDGI contribution is aggressively suppressed.**  
   Coverage, confidence, contact suppression, visibility leak clamp, and AO all multiply down the final field. This is useful for leak prevention, but it hides whether the underlying probe atlas is valid.

5. **Classification/confidence can zero valid probes.**  
   The forward sampler multiplies by probe active state and quality confidence. A misclassification or low confidence can make otherwise valid irradiance invisible.



## Phase 2 — Fix forward DDGI gather fallback

This is the highest-impact fix and should be implemented first.

### 2.1 Patch `SampleDdgiIrradiance()`

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

Current issue:

- `ReadDdgiGatherTile(tile)` can fail.
- A tile can be marked `DDGI_GATHER_TILE_FALLBACK_FLAG`.
- In both cases, the function returns an empty result instead of trying the exhaustive sampler.

Implementation:

```glsl
DdgiSampleResult SampleDdgiIrradiance(vec3 worldPosition, vec3 normal, float indirectAo)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    if (ForwardGlobalIlluminationEnabled() == 0u)
        return result;

    uint volumeCount;
    if (!DdgiHeaderEnabled(volumeCount))
        return result;

    float globalIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 12u), 0.0, 8.0);

    DdgiGatherTileInfo tile;
    bool tileValid = ReadDdgiGatherTile(tile);
    bool tileHasUsableCandidate = tileValid &&
        (tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u;

    if (tileHasUsableCandidate)
    {
        DdgiSampleResult tiledResult = SampleDdgiGatherCandidates(
            tile,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);

        if (tiledResult.coverage > 0.000001 || tiledResult.weight > 0.000001)
            return tiledResult;
    }

    if (DdgiExhaustiveGatherFallbackEnabled())
    {
        return SampleDdgiIrradianceExhaustive(
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);
    }

    return result;
}
```

Add helper:

```glsl
bool DdgiExhaustiveGatherFallbackEnabled()
{
    // Replace with your final flag source.
    return true;
}
```

### 2.2 Add diagnostics for fallback usage

File target:

```text
Njulf/Njulf.Rendering/Data/SceneRenderingData.cs
```

Add counters:

```csharp
public int DdgiForwardGatherFallbackUsed;
public int DdgiForwardGatherFallbackDisabled;
public int DdgiForwardGatherTileEmpty;
```

If you already have a GPU diagnostics buffer path, prefer shader-side counters. Otherwise, start with CPU-side tile diagnostics already available from `DdgiGatherTileManager`.

### 2.3 Improve gather-tile policy

File target:

```text
Njulf/Njulf.Rendering/Resources/DdgiGatherTileManager.cs
```

Review `BuildTiles()` and make the production behavior explicit:

- If at least one active clipmap exists, every tile should get a primary clipmap candidate.
- Authored/local volume candidates should override clipmap candidates where their projected bounds overlap.
- If no tile candidate exists but DDGI is active, the shader fallback must sample exhaustively.
- `TileFallbackFlag` should mean “fast gather has no candidate,” not “DDGI is disabled.”

### Acceptance criteria

- In `DdgiGatherFallback` debug view, fallback tiles no longer imply black indirect.
- In scenes with active probes, `DdgiIrradiance` debug should show non-zero values in fallback regions after convergence.
- Exhaustive fallback should be measurable but bounded. It should not become the normal path for the whole screen when clipmaps are active.

---

## Phase 3 — Unify DDGI radiance and intensity convention

This is required for stable bounce lighting.

### 3.1 Choose the production convention

Use this convention:

- **Probe atlas stores raw scene irradiance/radiance.**
- **Per-volume and global intensity are applied only when shading the final visible surface.**
- **Bounce feedback samples raw atlas data and does not divide by global intensity.**

This prevents feedback loops from being affected by artistic display intensity controls.

### 3.2 Stop scaling ray results before atlas write

File target:

```text
Njulf/Njulf.Shaders/ddgi_update_shared.glsl
```

In the trace pass, replace:

```glsl
vec3 sampleIrradiance = radiance * intensity;
rayResult.radiance = sampleIrradiance;
```

with:

```glsl
vec3 sampleIrradiance = radiance;
rayResult.radiance = sampleIrradiance;
```

Keep `intensity` available only if a compatibility flag is needed.

### 3.3 Add per-volume intensity to forward sampling info

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

Extend `DdgiVolumeSampleInfo`:

```glsl
float volumeIntensity;
```

In `ReadDdgiVolumeSampleInfo()`, read `RayAndUpdateParams.z`:

```glsl
info.volumeIntensity = max(rayAndUpdateParams.z, 0.0);
```

In `SampleDdgiVolumeIrradiance()`, replace final scaling:

```glsl
result.irradiance = clamp((accumulated / totalWeight) * globalIntensity, vec3(0.0), vec3(64.0));
```

with:

```glsl
float finalIntensity = globalIntensity * max(info.volumeIntensity, 0.0);
result.irradiance = clamp((accumulated / totalWeight) * finalIntensity, vec3(0.0), vec3(64.0));
```

### 3.4 Fix stable bounce feedback scaling

File target:

```text
Njulf/Njulf.Shaders/ddgi_update_shared.glsl
```

Replace:

```glsl
return blendedCoverage > 0.000001 && globalIntensity > 0.0001
    ? clamp((blendedIrradiance / blendedCoverage) / globalIntensity, vec3(0.0), vec3(64.0))
    : vec3(0.0);
```

with:

```glsl
return blendedCoverage > 0.000001
    ? clamp(blendedIrradiance / blendedCoverage, vec3(0.0), vec3(64.0))
    : vec3(0.0);
```

### 3.5 Add compatibility mode for migration

Keep a temporary flag:

```csharp
DdgiRawAtlasRadianceConventionEnabled
```

If disabled, the old intensity-scaled behavior can be used for comparison. Default it to `true` once tests pass.

### Acceptance criteria

- First-bounce indirect does not change wildly when `IndirectIntensity` changes; only final brightness changes.
- Bounce contribution becomes visible in a Cornell-room or emissive-room scene after convergence.
- Changing `IndirectIntensity` should not destabilize the cached atlas history.

---

## Phase 4 — Fix forward GI application gate

### 4.1 Separate SSGI requirements from DDGI requirements

File target:

```text
Njulf/Njulf.Rendering/Pipeline/ForwardPlusPass.cs
```

Current behavior should be changed so DDGI does not require `sceneData.DepthPrePassEnabled` unless a specific debug or AO path requires it.

Replace the logic with a clearer split:

```csharp
private bool ShouldApplyGlobalIllumination(SceneRenderingData sceneData)
{
    if (sceneData.AnimationDebugView != AnimationDebugView.None)
        return false;

    if (!RenderFeatureIsolationPolicy.AllowsPostProcessing(sceneData.ActiveFeatureIsolation))
        return false;

    return ShouldApplyDdgi(sceneData) || ShouldApplySsgi(sceneData);
}

private bool ShouldApplySsgi(SceneRenderingData sceneData)
{
    GlobalIlluminationSettings gi = _settings.GlobalIllumination;
    return gi.EffectiveUseSsgi && sceneData.DepthPrePassEnabled;
}

private bool ShouldApplyDdgi(SceneRenderingData sceneData)
{
    GlobalIlluminationSettings gi = _settings.GlobalIllumination;
    return gi.EffectiveUseDdgi &&
           sceneData.DdgiProbeCount > 0 &&
           (sceneData.DepthPrePassEnabled || gi.DdgiAllowForwardWithoutDepthPrePass);
}
```

### 4.2 Verify shader usage

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

Confirm DDGI sampling only needs:

- `fragWorldPosition`
- `ddgiNormal`
- probe volume buffer
- probe state buffer
- irradiance atlas buffer
- visibility atlas buffer
- gather tile buffer

Do not tie the DDGI path to depth texture validity except for screen-space AO or debug views.

### Acceptance criteria

- DDGI can render when SSGI is off.
- DDGI can render when depth-prepass-dependent SSGI is disabled.
- Existing SSGI path still requires valid depth and behaves unchanged.

---

## Phase 5 — Add a temporary “raw DDGI visible” debug mode

The existing final composition can hide useful probe data. Add a debug path that shows DDGI before suppression.

### 5.1 Add debug view option if needed

File target:

```text
Njulf/Njulf.Rendering/Data/RenderSettings.cs
```

Add or reuse:

```csharp
GlobalIlluminationDebugView.DdgiRawDiffuse = ...
GlobalIlluminationDebugView.DdgiSuppressionMask = ...
```

If adding enum values is too invasive, reuse `DdgiIrradiance`, `DdgiCoverage`, and `FinalIndirect` while adding the debug override flag.

### 5.2 Bypass final suppression for diagnosis

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

In `ComposeHybridDiffuseGi()`, support a debug override:

```glsl
if (DdgiDebugBypassFinalSuppression())
{
    result.diffuse = clamp(ddgiDiffuse, vec3(0.0), vec3(64.0));
    result.ddgiCoverage = clamp(ddgi.coverage, 0.0, 1.0);
    result.environmentFallbackWeight = 0.0;
    result.nearContactSuppression = 0.0;
    return result;
}
```

### 5.3 Add suppression debug output

Show the terms that suppress DDGI:

```glsl
vec3 debugSuppression = vec3(
    ddgiVisibleSupport,
    1.0 - ddgiContactSuppression,
    ddgiLowFrequencyCoverage);
```

### Acceptance criteria

- You can visually distinguish “atlas has no light” from “composition suppressed the light.”
- Debug mode never leaks into normal rendering unless explicitly enabled.

---

## Phase 6 — Harden probe classification and confidence handling

### 6.1 Add debug force-active path

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

In `SampleDdgiVolumeIrradiance()`, after reading active state:

```glsl
float probeActive = clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);
if (DdgiDebugForceProbeActive())
    probeActive = 1.0;
```

### 6.2 Make production classification less destructive

File target:

```text
Njulf/Njulf.Shaders/ddgi_update_shared.glsl
```

Current classification can set active to zero when invalid-probe score is high. For production, prefer gradual confidence reduction over hard black unless a probe is clearly inside geometry.

Change target behavior:

- Strong backface/close-hit evidence can suppress probe active.
- Weak evidence should reduce confidence, not fully deactivate.
- Inactive probes should recover when evidence becomes valid.

Suggested initial tuning:

```glsl
float hardInvalid = smoothstep(0.75, 0.95, invalidProbeScore);
float softInvalid = smoothstep(0.35, 0.75, invalidProbeScore);
float targetActiveProbe = classificationEnabled ? (1.0 - hardInvalid) : 1.0;
float confidencePenalty = 1.0 - softInvalid * 0.75;
```

Then apply `confidencePenalty` to irradiance/visibility quality rather than hard-zeroing the probe.

### 6.3 Add confidence floor for forward validation

File target:

```text
Njulf/Njulf.Shaders/forward.frag
```

During production debugging only:

```glsl
float qualityConfidence = ...;
if (DdgiDebugBypassFinalSuppression())
    qualityConfidence = max(qualityConfidence, 0.25);
```

Do not keep this in normal production rendering unless validated.

### Acceptance criteria

- `DdgiProbeState` debug no longer shows large valid areas as inactive.
- Thin-wall test remains leak-safe.
- New probes recover from invalid classification within a bounded number of frames.

---

## Phase 7 — Verify update scheduling, cold start, and publication

### 7.1 Verify pass execution conditions

File targets:

```text
Njulf/Njulf.Rendering/Pipeline/DdgiPipelinePasses.cs
Njulf/Njulf.Rendering/VulkanRenderer.cs
```

Confirm the update passes execute when:

```text
GlobalIllumination.Enabled == true
EffectiveUseDdgi == true
EffectiveUseRayQueryBackend == true
AccelerationStructureManager.Active == true
DdgiProbeVolumeCount > 0
DdgiProbesUpdated > 0
```

Add diagnostics for each false condition so “DDGI inactive” has a precise reason.

### 7.2 Improve cold-start behavior

File target:

```text
Njulf/Njulf.Rendering/Resources/DdgiProbeVolumeManager.cs
```

Ensure newly initialized probes get enough updates quickly:

- Keep `DdgiColdStartMaxProbeUpdatesPerFrame` high enough for validation scenes.
- Mark newly exposed clipmap cells with `ProbeUpdateReasonNewCellFlag`.
- Avoid using high hysteresis for new cells.
- Confirm the shader already resets history for new cells; keep that behavior.

### 7.3 Publication sanity check

File target:

```text
Njulf/Njulf.Rendering/Pipeline/DdgiPipelinePasses.cs
```

`DdgiPublishPass` currently publishes only if `sceneData.DdgiProbesUpdated > 0`. That is reasonable, but diagnostics should show if there were updates but no publish.

Add a diagnostic field:

```csharp
public int DdgiPublishExecuted;
public string DdgiPublishSkipReason;
```

### Acceptance criteria

- Cold-start scene becomes visibly lit within expected frames.
- Diagnostics show a clear active/inactive reason.
- Publish latency is stable and not silently skipped.

---

## Phase 8 — Synchronization and render graph validation

### 8.1 Review buffer barriers between DDGI passes

File targets:

```text
Njulf/Njulf.Rendering/Pipeline/DdgiPipelinePasses.cs
Njulf/Njulf.Rendering/Pipeline/DdgiSchedulePass.cs
Njulf/Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs
```

Confirm these transitions are correct:

1. Scheduler writes update queue and indirect dispatch.
2. Trace reads queue and writes ray-result scratch.
3. Blend reads ray-result scratch and writes irradiance/visibility atlases and probe state.
4. Relocate/classify reads ray-result scratch and writes probe state/relocation buffers.
5. Publish barrier makes data visible to next frame’s fragment/compute users.

### 8.2 Confirm one-frame latency is intended

The render graph currently runs forward shading before DDGI update. This means DDGI updates are consumed on a later frame. That is acceptable, but diagnostics and tests should expect it.

Add a comment in the pipeline declaration:

```csharp
// DDGI update runs after ForwardPlusPass and publishes cache data for subsequent frames.
```

### 8.3 Validate async-compute flags are diagnostic-only

If async compute is not actually using a separate queue, ensure diagnostics do not imply real overlap. Keep DDGI barriers conservative until queue ownership transitions are implemented.

### Acceptance criteria

- Vulkan validation has no storage-buffer hazards for DDGI resources.
- RenderDoc capture shows DDGI trace/blend/relocate using correct buffers.
- DDGI debug views show expected one-frame latency, not random stale data.

---

## Phase 9 — Add tests for the fixed behavior

### 9.1 Unit tests

Add or extend tests in:

```text
Njulf/Njulf.Tests/DdgiGatherTileManagerTests.cs
Njulf/Njulf.Tests/GlobalIlluminationProbeVolumeDataTests.cs
Njulf/Njulf.Tests/ForwardPlusPassTests.cs
Njulf/Njulf.Tests/GPUStructLayoutTests.cs
```

Test cases:

1. **Gather tile fallback policy**
   - No DDGI active → header disabled.
   - Active clipmap exists → every tile has a clipmap candidate.
   - Active authored local volume projected into screen → overlapping tiles prefer local volume.
   - No fast candidate → tile fallback flag set, but shader fallback expected.

2. **Forward GI gate**
   - DDGI active + depth prepass off + `DdgiAllowForwardWithoutDepthPrePass` true → GI flag enabled.
   - SSGI active + depth prepass off → SSGI remains disabled.
   - Animation debug view active → GI disabled.

3. **Radiance convention**
   - `BuildHeader()` still writes global intensity.
   - Per-volume intensity remains in `RayAndUpdateParams.z`.
   - Shader struct offsets match CPU structs if any fields changed.

### 9.2 Shader build tests

Run:

```bash
dotnet test Njulf/Njulf.Tests/Njulf.Tests.csproj --filter ShaderBuildTests
```

Ensure all shader variants compile:

- forward full material
- forward simple global IBL
- compacted forward
- DDGI trace
- DDGI blend
- DDGI relocate/classify
- DDGI scheduler stages

### 9.3 Render validation scenarios

Use the existing validation scenes:

- `GiSponzaRightWallStationary`
- `GiCornellRoom`
- `GiThinWallLeakTest`
- `GiMovingPointLight`
- `GiMovingRigidObject`
- `GiBrightExteriorRoom`
- `GiLongCorridorOcclusion`
- `GiEmissiveMaterialRoom`
- `GiLocalVolumeStreaming`
- `GiFastTraversalTeleport`

Run each with:

- CPU scheduler mode
- GPU scheduler mode
- CPU/GPU compare mode if validation is enabled

### Acceptance criteria

- Unit tests pass.
- Shader build tests pass.
- Validation scenarios show non-zero DDGI indirect where expected.
- No NaN/Inf HDR outliers.
- Thin-wall leakage stays below the validation threshold.

---

## Phase 10 — Performance hardening

### 10.1 Measure fallback cost

After enabling exhaustive fallback, track how often it runs. The fallback is correctness-first, not meant to dominate production rendering.

Add or log:

```text
DdgiGatherFallbackTileCount
DdgiGatherSelectedClipmapTileCount
DdgiGatherSelectedLocalTileCount
DdgiForwardGatherFallbackUsed
GpuForwardOpaqueMicroseconds
GpuDdgiTraceMicroseconds
GpuDdgiBlendMicroseconds
GpuDdgiRelocateClassifyMicroseconds
```

### 10.2 Optimize if exhaustive fallback is frequent

If many tiles use exhaustive fallback:

1. Fix gather-tile candidate assignment first.
2. Consider a compute prepass that builds a more accurate per-tile DDGI candidate buffer.
3. Consider using depth/normal min-max per tile for volume selection.
4. Keep exhaustive fallback only as a safety net.

### 10.3 Maintain update budgets

Use existing DDGI budget settings:

```text
DdgiProbeUpdateTimeBudgetMilliseconds
DdgiGpuScheduleTimeBudgetMilliseconds
DdgiGpuTotalUpdateTimeBudgetMilliseconds
DdgiProbeUpdatePrimaryRayBudget
DdgiMaxProbeUpdatesPerFrame
```

Do not increase quality defaults to hide correctness bugs.

### Acceptance criteria

- Exhaustive fallback does not dominate forward cost in normal clipmap scenes.
- DDGI update remains inside budget on target hardware.
- No emergency degrade or scheduler fallback is active during validation unless intentionally tested.

---

## Phase 11 — Production rollout sequence

### Step 1 — Correctness mode

Enable:

```text
DdgiExhaustiveGatherFallbackEnabled = true
DdgiRawAtlasRadianceConventionEnabled = true
DdgiAllowForwardWithoutDepthPrePass = true
```

Temporarily allow:

```text
DdgiDebugBypassFinalSuppression = true
DdgiDebugForceProbeActive = true
```

Use this only to prove the atlas contains usable light.

### Step 2 — Normal mode with fallback

Disable debug bypasses:

```text
DdgiDebugBypassFinalSuppression = false
DdgiDebugForceProbeActive = false
DdgiDebugDisableVisibilityWeight = false
```

Keep:

```text
DdgiExhaustiveGatherFallbackEnabled = true
DdgiRawAtlasRadianceConventionEnabled = true
DdgiAllowForwardWithoutDepthPrePass = true
```

Tune classification and suppression until validation passes.

### Step 3 — Performance mode

If exhaustive fallback is too expensive, improve `DdgiGatherTileManager` so fallback is rare. Do not remove exhaustive fallback until the tile selector is proven reliable.

### Step 4 — Production default

Final defaults should be:

```text
DdgiExhaustiveGatherFallbackEnabled = true
DdgiRawAtlasRadianceConventionEnabled = true
DdgiAllowForwardWithoutDepthPrePass = true
DdgiDebugBypassFinalSuppression = false
DdgiDebugForceProbeActive = false
DdgiDebugDisableVisibilityWeight = false
```

### Step 5 — Remove or hide unsafe debug toggles

Before shipping, hide developer-only flags from public configuration or mark them clearly as debug-only.

---

## Phase 12 — Final production gates

DDGI is production-ready when all of the following are true:

### Correctness gates

- DDGI debug irradiance shows non-zero indirect lighting in validation scenes.
- Bounce lighting is visible in closed/interior scenes after convergence.
- Emissive material room shows indirect contribution from emissive surfaces.
- Moving point light updates DDGI without long stale lighting.
- Moving rigid object invalidates nearby probes.
- Fast traversal/teleport resets newly exposed probes without severe ghosting.
- Thin-wall and bright-exterior-room leakage remain below target thresholds.

### Stability gates

- No NaN/Inf pixels in HDR output.
- No Vulkan validation storage-buffer hazards.
- No descriptor binding mismatch.
- No shader compile regressions.
- No GPU scheduler invalid/duplicate requests in validation mode.
- CPU/GPU scheduler compare mode is either clean or has documented acceptable differences.

### Performance gates

- DDGI update GPU time is within the configured budget.
- DDGI scheduler GPU time is within the configured budget.
- Forward pass cost increase from fallback is understood and bounded.
- No persistent emergency degrade in normal scenes.
- No persistent GPU scheduler fallback in GPU scheduler mode.

### Diagnostics gates

- DDGI inactive states have explicit reasons.
- Gather fallback count is visible in diagnostics.
- Probe update count and primary ray count are visible.
- Published cache latency is visible.
- Debug views clearly separate raw irradiance, coverage, visibility, active state, and final indirect.

---

## Suggested commit breakdown

1. `ddgi: add production debug flags and diagnostics`
2. `ddgi: add exhaustive forward gather fallback`
3. `ddgi: unify raw atlas radiance convention`
4. `ddgi: allow forward ddgi without depth prepass dependency`
5. `ddgi: add raw irradiance and suppression debug views`
6. `ddgi: soften classification confidence handling`
7. `ddgi: add gather, gate, and radiance convention tests`
8. `ddgi: add validation scenario gates and performance reporting`
9. `ddgi: tune production defaults and document rollout`

---

## Minimal patch order if time is limited

1. Patch `forward.frag` so `SampleDdgiIrradiance()` uses `SampleDdgiIrradianceExhaustive()` when gather tiles are missing/fallback.
2. Temporarily return `ddgiDiffuse` directly in `ComposeHybridDiffuseGi()` to prove probe data is visible.
3. Remove the global-intensity division from `SampleStableDdgiIrradiance()`.
4. Stop multiplying trace ray radiance by per-volume intensity before atlas blending, or gate it behind a compatibility flag.
5. Split `ForwardPlusPass.ShouldApplyGlobalIllumination()` so DDGI is not disabled just because depth pre-pass is off.
6. Disable probe classification once to check whether active-state suppression is the remaining cause.
7. Re-enable final suppression and tune confidence/visibility/contact terms until validation scenes pass.
