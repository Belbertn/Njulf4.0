# DDGI Improvement Plan: Dense Near Coverage, Gather Fallback, Hybrid Composition, and Surface-Aware Probe Relocation

## Context

The current DDGI behavior shows large black regions in debug views and weak indirect lighting in a narrow, shadowed architectural alley/courtyard. The most likely combined causes are:

- The camera-relative DDGI clipmap is too sparse for the scene scale.
- The clipmap is vertically offset too high for ground-level alley lighting.
- The forward shader returns an empty DDGI sample when the gather-tile path fails or marks fallback.
- Hybrid composition suppresses environment fallback based on raw DDGI coverage even when the effective DDGI contribution is low.
- Probe relocation exists, but it does not explicitly target a stable minimum distance from nearby surfaces.

This plan keeps the camera-relative clipmap model, adds a dense local-volume option for alleys/interiors, makes the shader fallback path robust, and improves relocation so probes float in stable free space near surfaces instead of sitting inside or too close to geometry.

---

## Goals

1. Improve indirect light in narrow exterior/interior spaces.
2. Reduce black DDGI debug coverage regions.
3. Make camera-relative clipmaps usable for plaza-scale scenes while allowing dense authored local volumes for thin-wall areas.
4. Prevent missing gather-tile data from producing a hard black DDGI result.
5. Make environment fallback cooperate with DDGI rather than being suppressed too early.
6. Improve probe relocation so probes keep a minimum free-space distance from nearby surfaces.
7. Add diagnostics and acceptance criteria so future DDGI changes are easier to validate.

---

## Non-goals

- Do not replace DDGI with a completely different GI solution.
- Do not force every probe onto a surface. Probes should remain in free space.
- Do not make all cascades dense. Only near-field and local volumes should become denser.
- Do not disable classification. Classification is still needed for probes that cannot be relocated into a valid position.

---

## High-level implementation order

1. Establish a reliable baseline with current debug views.
2. Fix clipmap placement and spacing for the sample scene.
3. Add a dense authored local DDGI volume around the alley/courtyard.
4. Make `SampleDdgiIrradiance()` fall back to exhaustive volume sampling when gather tiles are missing/fallback.
5. Change hybrid composition to suppress environment fallback using effective DDGI contribution, not raw coverage.
6. Improve relocation to target a minimum distance from nearby surfaces.
7. Add diagnostics, tests, and performance gates.
8. Validate with screenshots, debug views, and performance snapshots.

---

## Step 0: Capture a baseline before changing code

### 0.1 Record current settings

Record the current values from `Njulf/NjulfHelloGame/SamplePlazaGlobalIllumination.cs`:

```csharp
settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
gi.Mode = GlobalIlluminationMode.Ddgi;
gi.UseSsgi = false;
gi.UseDdgi = true;
gi.UseRayQueryBackend = true;
gi.DdgiCameraRelativeEnabled = true;
gi.DdgiProbeClassificationEnabled = true;
gi.DdgiProbeRelocationEnabled = true;
gi.DdgiClipmapProbeCountX = 24;
gi.DdgiClipmapProbeCountY = 10;
gi.DdgiClipmapProbeCountZ = 24;
gi.DdgiClipmapBaseSpacing = 1.5f;
gi.DdgiClipmapVerticalCenterOffset = 8.0f;
gi.IndirectIntensity = 1.85f;
gi.EnvironmentFallbackIntensity = 0.12f;
```

### 0.2 Capture screenshots for every relevant DDGI debug view

Capture these views from the same camera position:

- Normal lit view.
- Final indirect.
- DDGI irradiance.
- DDGI visibility.
- DDGI probe state.
- DDGI relocation.
- DDGI leak clamp.
- DDGI coverage.
- DDGI cascade selection.
- DDGI cascade blend weight.
- DDGI gather local volume.
- DDGI gather clipmap.
- DDGI gather fallback.

### 0.3 Capture diagnostics

Record the runtime values for:

- `DdgiProbeVolumeCount`
- `DdgiProbeCount`
- `DdgiActiveProbeCount`
- `DdgiProbesUpdated`
- `DdgiRaysPerProbe`
- `DdgiGpuSchedulerFallbackActive`
- `DdgiGatherTileCount`
- `DdgiGatherFallbackTileCount`
- `DdgiPublishedCacheLatencyFrames`
- `DdgiRayScratchBytes`
- `DdgiUpdatedAtlasBytes`

Keep this baseline so every later step can be compared visually and numerically.

---

## Step 1: Retune camera-relative clipmap placement

### Problem

The current near clipmap uses `1.5m` spacing and a `+8m` vertical center offset. For a ground-level alley, this can place many probes around the upper walls and roofline while lower walls, floor, columns, and cloth are near the bottom edge of the volume or poorly covered.

### Change

Edit `Njulf/NjulfHelloGame/SamplePlazaGlobalIllumination.cs`.

Start with this conservative test configuration:

```csharp
gi.DdgiClipmapVerticalCenterOffset = 0.0f;
gi.DdgiClipmapBaseSpacing = 1.0f;
gi.DdgiClipmapProbeCountY = 14;
```

If the result improves but still looks under-sampled, test:

```csharp
gi.DdgiClipmapVerticalCenterOffset = 0.0f;
gi.DdgiClipmapBaseSpacing = 0.75f;
gi.DdgiClipmapProbeCountY = 14;
```

### Probe-budget check

Current high-style layout:

```text
24 * 10 * 24 * 4 = 23,040 probes
```

With only Y increased:

```text
24 * 14 * 24 * 4 = 32,256 probes
```

That is still close to the high preset budget. Avoid jumping immediately to `32 * 14 * 32 * 4`, because that is:

```text
32 * 14 * 32 * 4 = 57,344 probes
```

That needs an ultra-like active-probe budget and more atlas memory.

### Temporary debug fallback

For debugging only, increase the fallback so missing DDGI is distinguishable from total darkness:

```csharp
gi.EnvironmentFallbackIntensity = 0.5f;
settings.Environment.DiffuseIntensity = 0.3f;
```

After diagnosing coverage, tune these back down.

### Acceptance criteria

- DDGI coverage debug view has fewer black areas on lower walls and floor.
- Probe-state debug view shows active probes near the camera and alley surfaces.
- Moving closer should not be required to get basic indirect light.
- Final indirect debug view should show low-frequency bounce on the alley floor and lower walls.

---

## Step 2: Add a dense authored local DDGI volume for the alley/courtyard

### Rationale

Camera-relative clipmaps are good for broad outdoor coverage. Dense authored volumes are better for thin-wall spaces, alleys, interiors, and tight courtyards. The renderer already has local/authored volume support and the gather path prioritizes local volumes before clipmaps.

### Change

Add an authored DDGI volume around the problematic alley/courtyard bounds.

Recommended starting point:

```csharp
BoundingBox alleyBounds = new BoundingBox(
    new Vector3(/* min x */, /* min y */, /* min z */),
    new Vector3(/* max x */, /* max y */, /* max z */));

GlobalIlluminationProbeVolume alleyVolume =
    GlobalIlluminationProbeVolume.CreateThinWallRoomPreset(alleyBounds, 0.45f);

alleyVolume.Name = "Dense Alley DDGI";
alleyVolume.Intensity = 1.5f;
alleyVolume.MaxProbeUpdatesPerFrame = 128;
alleyVolume.Priority = 256;
alleyVolume.UpdatePriority = 256;

scene.GlobalIlluminationProbeVolumes.Add(alleyVolume);
```

If `0.45m` is too expensive, use the small-room preset:

```csharp
GlobalIlluminationProbeVolume alleyVolume =
    GlobalIlluminationProbeVolume.CreateSmallRoomPreset(alleyBounds, 0.6f);
```

### Bounds guidance

The authored volume should include:

- Floor region visible to the camera.
- Lower and mid wall surfaces.
- Cloth/curtain areas if they are meant to receive bounce.
- Upper arches only if they matter for visible indirect light.

Avoid making the local volume cover the entire plaza. Keep it tight around the hard lighting problem.

### Debug validation

Use these DDGI debug views:

- Gather local volume: should show a non-black local-volume selection over the alley.
- Gather clipmap: should still cover areas outside the local volume.
- Gather fallback: should be low or absent inside the alley.
- Coverage: should be bright enough in the local-volume area.
- Probe state: should show active/covered/visible probes.

### Acceptance criteria

- The dense local volume visibly improves the alley before any shader fallback changes.
- Local-volume gather debug is active over the intended screen region.
- Probe count remains within the active-probe budget.
- Frame time does not regress beyond the selected DDGI quality target.

---

## Step 3: Make DDGI gather fallback robust in `forward.frag`

### Problem

`SampleDdgiIrradianceExhaustive()` exists, but `SampleDdgiIrradiance()` currently returns an empty DDGI sample when the gather tile is invalid or flagged as fallback. That makes missing gather tiles produce black indirect lighting even when volumes may still cover the world position.

### File

`Njulf/Njulf.Shaders/forward.frag`

### Change

Modify `SampleDdgiIrradiance()` so gather tiles are an optimization, not a hard dependency.

Expected logic:

```glsl
DdgiSampleResult SampleDdgiIrradiance(vec3 worldPosition, vec3 normal, float indirectAo)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    if (ForwardGlobalIlluminationEnabled() == 0u)
        return result;

    uint volumeCount;
    if (!DdgiHeaderEnabled(volumeCount))
        return result;

    float globalIntensity = clamp(
        ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 12u),
        0.0,
        8.0);

    DdgiGatherTileInfo tile;
    if (ReadDdgiGatherTile(tile) &&
        (tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)
    {
        return SampleDdgiGatherCandidates(
            tile,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);
    }

    return SampleDdgiIrradianceExhaustive(
        volumeCount,
        worldPosition,
        normal,
        indirectAo,
        globalIntensity);
}
```

### Optional safety limit

If exhaustive fallback becomes expensive, cap it to the maximum shader-supported volume count:

```glsl
volumeCount = min(volumeCount, 16u);
```

The current exhaustive loop already limits volume scanning to 16 in its loop structure, so keep that contract consistent.

### Diagnostics

Use `GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK` to identify where fallback happens. After this change, fallback regions should no longer become hard-black if a valid volume exists.

### Tests

Add or update shader compile tests so all forward variants still compile:

- `forward_opaque.frag.spv`
- `forward_opaque_ddgi.frag.spv`
- `forward_opaque_simple.frag.spv`
- `forward_opaque_simple_ddgi.frag.spv`
- `forward_opaque_simple_full_input.frag.spv`
- `forward_opaque_simple_full_input_ddgi.frag.spv`

### Acceptance criteria

- Fallback gather tiles no longer force black DDGI if a volume covers the shaded position.
- Debug fallback regions still render meaningful indirect light when exhaustive sampling succeeds.
- No shader compilation regressions.

---

## Step 4: Change hybrid diffuse composition to use effective DDGI contribution

### Problem

The current composition reduces environment fallback based on raw DDGI coverage. But DDGI is later reduced by visibility, support weight, and near-contact suppression. That can produce this failure mode:

```text
coverage says DDGI owns the pixel
but visible/support weight says DDGI is weak
environment fallback is suppressed
final indirect becomes black or near-black
```

### File

`Njulf/Njulf.Shaders/forward.frag`

### Change

In `ComposeHybridDiffuseGi()`, compute an effective DDGI weight first, then use that same effective weight both to add DDGI and to suppress environment fallback.

Suggested structure:

```glsl
HybridDiffuseGiResult ComposeHybridDiffuseGi(
    vec3 diffuseIbl,
    vec3 ddgiDiffuse,
    DdgiSampleResult ddgi,
    float indirectAo,
    float environmentFallbackIntensity)
{
    HybridDiffuseGiResult result;

    float ddgiCoverage = clamp(ddgi.coverage, 0.0, 1.0);
    float visibleSupport = smoothstep(0.05, 0.35, clamp(ddgi.weight, 0.0, 1.0));

    float nearContactOcclusion = 1.0 - clamp(indirectAo, 0.0, 1.0);
    float thinWallLeakClampStrength = clamp(
        ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 14u),
        0.0,
        1.0);
    float thinWallProxyThickness = clamp(
        ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 15u),
        0.0,
        1.0);

    float proxyContactScale = clamp(thinWallProxyThickness * 8.0, 0.0, 1.0);
    float visibilityLeakSuppression = clamp(
        (1.0 - clamp(ddgi.leakClamp, 0.0, 1.0)) * thinWallLeakClampStrength,
        0.0,
        0.95);
    float ddgiContactSuppression = clamp(
        max(nearContactOcclusion * mix(0.65, 0.9, proxyContactScale), visibilityLeakSuppression),
        0.0,
        0.98);

    float effectiveDdgiWeight = clamp(
        ddgiCoverage * visibleSupport * (1.0 - ddgiContactSuppression),
        0.0,
        1.0);

    vec3 ddgiField = ddgiDiffuse * effectiveDdgiWeight;
    float environmentFallbackWeight = clamp(
        (1.0 - effectiveDdgiWeight) * indirectAo * environmentFallbackIntensity,
        0.0,
        4.0);
    vec3 environmentFallbackField = diffuseIbl * environmentFallbackWeight;

    result.diffuse = clamp(environmentFallbackField + ddgiField, vec3(0.0), vec3(64.0));
    result.ddgiCoverage = ddgiCoverage;
    result.environmentFallbackWeight = environmentFallbackWeight;
    result.nearContactSuppression = ddgiContactSuppression;
    return result;
}
```

### Optional debug addition

Add a debug view for effective DDGI weight if there is a spare debug enum slot, or temporarily map one existing debug view during development.

Useful debug output:

```glsl
WriteForwardColor(vec4(vec3(effectiveDdgiWeight), 1.0));
```

### Acceptance criteria

- Pixels with low DDGI support keep some environment fallback.
- Final indirect debug view is not black solely because raw coverage is high.
- Thin-wall suppression still prevents obvious leaking.
- Shadowed areas remain dark, but not unnaturally crushed to zero.

---

## Step 5: Improve surface-aware probe relocation

### Goal

Keep camera-relative probe placement, but allow individual probes to relocate to a stable free-space position near surfaces.

The target behavior is:

```text
logical probe position = camera-relative clipmap grid position
physical probe position = logical probe position + relocation offset
```

Probes should not be snapped onto surfaces. They should be pushed out of geometry or away from very close surfaces until they are at least a minimum free-space distance away.

### Current foundation

The forward shader already samples probes using relocation:

```glsl
vec3 probePosition = DdgiProbeWorldPosition(info, corner) + relocationAndClassification.xyz;
```

The DDGI trace/relocate pipeline already collects close-hit information, computes a relocation direction, and clamps relocation distance.

### 5.1 Shader-only relocation improvement

Start with a shader-only change before adding new settings.

#### Files

- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl`
- `Njulf/Njulf.Shaders/ddgi_trace.comp`
- `Njulf/Njulf.Shaders/ddgi_relocate_classify.comp`

#### Add nearest-hit reduction

`DdgiRayResult` already stores hit distance. Use that to estimate the nearest surface distance during relocation.

In the relocation/classification pass, add local tracking:

```glsl
float localNearestHitDistance = 3.402823e38;

for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
{
    DdgiRayResult result = ReadDdgiRayResult(updateIndex, rayIndex);
    if (result.flags <= 0.0)
        continue;

    if (result.hit > 0.5)
        localNearestHitDistance = min(localNearestHitDistance, result.hitDistance);

    // existing accumulation continues here
}
```

Store it in an unused shared component, for example:

```glsl
SharedBackfaceAndMissCount[localIndex] = vec4(
    localBackfaceCount,
    localMissCount,
    localNearestHitDistance,
    0.0);
```

Reduce it in `localIndex == 0`:

```glsl
float nearestHitDistance = 3.402823e38;

for (uint i = 0u; i < DDGI_LOCAL_SIZE; i++)
{
    nearestHitDistance = min(nearestHitDistance, SharedBackfaceAndMissCount[i].z);
}

if (nearestHitDistance >= 3.0e38)
    nearestHitDistance = max(normalBias + viewBias, 0.05);
```

#### Compute target free-space distance

Replace or augment the current relocation distance calculation:

```glsl
float minProbeSpacing = max(min(min(probeSpacing.x, probeSpacing.y), probeSpacing.z), 0.001);
float targetSurfaceDistance = max(minProbeSpacing * 0.15, 0.08);
float maxRelocationDistance = 0.4 * minProbeSpacing;

float neededPush = max(targetSurfaceDistance - nearestHitDistance, 0.0);
float closePush = closeRatio * max(normalBias + viewBias, 0.01) * 4.0;
float unclampedRelocationDistance = max(neededPush, closePush);

float relocationDistance = relocationEnabled
    ? clamp(unclampedRelocationDistance, 0.0, maxRelocationDistance)
    : 0.0;
```

Keep the existing direction calculation:

```glsl
vec3 relocationDirection = length(totalRelocation) > 0.0001
    ? normalize(totalRelocation)
    : vec3(0.0);
```

Keep history blending, but ensure new-cell / reset-history probes do not keep stale relocation.

### 5.2 Add configurable relocation parameters

After the shader-only version works, expose settings in `GlobalIlluminationSettings`.

Suggested new properties:

```csharp
private float _ddgiRelocationTargetSurfaceDistanceFraction = 0.15f;
private float _ddgiRelocationMinSurfaceDistance = 0.08f;
private float _ddgiRelocationMaxDistanceFraction = 0.40f;
private float _ddgiRelocationBlendAlpha = 0.20f;

public float DdgiRelocationTargetSurfaceDistanceFraction
{
    get => _ddgiRelocationTargetSurfaceDistanceFraction;
    set => _ddgiRelocationTargetSurfaceDistanceFraction = Clamp(value, 0.02f, 0.35f);
}

public float DdgiRelocationMinSurfaceDistance
{
    get => _ddgiRelocationMinSurfaceDistance;
    set => _ddgiRelocationMinSurfaceDistance = Clamp(value, 0.01f, 0.5f);
}

public float DdgiRelocationMaxDistanceFraction
{
    get => _ddgiRelocationMaxDistanceFraction;
    set => _ddgiRelocationMaxDistanceFraction = Clamp(value, 0.05f, 0.49f);
}

public float DdgiRelocationBlendAlpha
{
    get => _ddgiRelocationBlendAlpha;
    set => _ddgiRelocationBlendAlpha = Clamp(value, 0.02f, 1.0f);
}
```

Then pass these to shaders. Options:

1. Add a `vec4 RelocationParams` to `GPUDdgiUpdatePushConstants`.
2. Update the shader push constant block in `ddgi_update_shared.glsl`.
3. Update `SIZEOF_GPU_DDGI_UPDATE_PUSH_CONSTANTS` in `common.glsl`.
4. Update `GPUStructLayoutTests`.

Suggested shader usage:

```glsl
float targetSurfaceDistance = max(
    minProbeSpacing * pc.RelocationParams.x,
    pc.RelocationParams.y);
float maxRelocationDistance = pc.RelocationParams.z * minProbeSpacing;
float relocationBlendAlpha = pc.RelocationParams.w;
```

### 5.3 Keep classification as the safety valve

Do not force every invalid probe to relocate. Keep the classification logic:

- High close-hit ratio means the probe may be inside or too near geometry.
- High backface ratio means the probe may be behind/inside geometry.
- If the probe cannot be relocated safely, classify it inactive.

### 5.4 Avoid temporal instability

Rules:

- Reset relocation when `ShouldResetDdgiProbeHistory(flags)` is true.
- Clamp relocation to less than half the cell spacing.
- Blend relocation over time.
- Avoid exact surface snapping.
- Avoid per-frame target-distance changes unless settings changed.

### Diagnostics

Use the existing relocation debug view first:

```glsl
GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION
```

During development, consider showing normalized relocation length:

```glsl
float relocationAmount = length(ddgiSample.relocation) / max(min(info.spacing.x, min(info.spacing.y, info.spacing.z)) * 0.4, 0.001);
WriteForwardColor(vec4(vec3(relocationAmount), 1.0));
```

### Acceptance criteria

- Probes near walls/floors move slightly into free space.
- Probes do not snap to walls or visibly pop while moving the camera.
- DDGI coverage increases in the alley/courtyard.
- Leak clamp and classification still prevent obvious light leaks through thin walls.
- Relocation debug view shows stable, limited movement.

---

## Step 6: Improve DDGI debug views and diagnostics

### Add useful runtime counters

Add or expose these values in diagnostics if not already visible:

- Local-volume selected tile count.
- Clipmap selected tile count.
- Fallback tile count.
- Average DDGI coverage.
- Average DDGI visible support.
- Average DDGI effective contribution weight.
- Average relocation length.
- Classified inactive probe count.
- Probes reset due to new logical cells.
- Probes updated due to camera-relative movement.

### Suggested debug views

Existing useful views:

- `GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP`
- `GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK`

Optional new development-only views:

- Effective DDGI weight.
- Environment fallback weight.
- Relocation normalized length.
- Classification invalid score.
- Surface distance target failure.

### Acceptance criteria

- Black debug areas can be explained by a specific debug view.
- Fallback, coverage, active probe, and relocation views agree with each other.
- Debug views no longer require guessing whether the issue is coverage, probe state, visibility, or composition.

---

## Step 7: Add tests

### Unit tests

Add or update tests in `Njulf/Njulf.Tests`.

#### Clipmap layout tests

File ideas:

- `CameraRelativeDdgiClipmapControllerTests.cs`
- `GlobalIlluminationProbeVolumeDataTests.cs`

Test cases:

- Vertical center offset of `0.0f` centers the Y lattice around the camera.
- A positive vertical offset moves the lattice upward as expected.
- Probe counts produce expected active-probe counts.
- Probe budgets clamp correctly.

#### Gather fallback tests

File idea:

- `DdgiGatherTileManagerTests.cs`

Test cases:

- Gather tile fallback flag is set only when no local or clipmap candidate exists.
- Local volume takes priority over clipmap.
- Clipmap candidate exists when camera-relative volumes are active.

Shader behavior is not easy to unit test directly, so rely on shader compile tests and runtime debug validation for `SampleDdgiIrradiance()`.

#### Settings tests

File idea:

- `RenderSettingsTests.cs` or existing render settings test file.

Test cases for relocation settings if added:

- `DdgiRelocationTargetSurfaceDistanceFraction` clamps to `[0.02, 0.35]`.
- `DdgiRelocationMinSurfaceDistance` clamps to `[0.01, 0.5]`.
- `DdgiRelocationMaxDistanceFraction` clamps below `0.5`.
- `DdgiRelocationBlendAlpha` clamps to `[0.02, 1.0]`.

#### GPU struct layout tests

If `GPUDdgiUpdatePushConstants` changes:

- Update `common.glsl` size constants.
- Update `GPUStructLayoutTests.cs` expected size.
- Ensure shader and C# push constant layouts match exactly.

### Shader compile tests

Ensure these compile:

- `ddgi_trace.comp`
- `ddgi_blend.comp`
- `ddgi_relocate_classify.comp`
- all `ddgi_schedule_*.comp`
- all forward fragment variants
- foliage DDGI/SSGI forward variants

### Acceptance criteria

- All tests pass.
- All shaders compile.
- GPU struct layout tests pass after any push constant changes.

---

## Step 8: Validate visually and numerically

### Visual validation sequence

Use the same camera position as the baseline.

1. Normal view.
2. Final indirect.
3. DDGI irradiance.
4. DDGI visibility.
5. DDGI probe state.
6. DDGI coverage.
7. DDGI gather local volume.
8. DDGI gather clipmap.
9. DDGI gather fallback.
10. DDGI relocation.
11. DDGI leak clamp.

### Camera movement validation

Move through the alley slowly and check:

- No sudden black bands.
- No obvious probe popping.
- Relocation changes smoothly.
- Clipmap scrolling does not wipe out all local lighting.
- Local authored volume remains stable while camera-relative clipmaps move.

### Performance validation

Capture before/after values:

- DDGI update GPU time.
- DDGI schedule GPU time.
- Forward pass GPU time.
- Active probe count.
- Probe updates per frame.
- Primary ray count.
- Atlas memory.
- Ray scratch memory.
- Frame time.

### Acceptance criteria

- Final indirect visibly improves in the lower alley/courtyard.
- Debug coverage black regions are reduced or explained by intentional fallback/no-volume areas.
- Performance remains within the selected DDGI quality budget.
- No obvious light leaking through thin walls.
- No obvious camera-motion popping.

---

## Step 9: Suggested commit breakdown

### Commit 1: Retune sample DDGI placement

Files:

- `Njulf/NjulfHelloGame/SamplePlazaGlobalIllumination.cs`

Changes:

- Lower vertical offset.
- Increase near-cascade Y probes.
- Optionally reduce base spacing.
- Temporarily adjust fallback intensity only if desired for debugging.

### Commit 2: Add dense local alley DDGI volume

Files depend on where the scene is created, likely one of:

- `Njulf/NjulfHelloGame/SampleSceneLoader.cs`
- `Njulf/NjulfHelloGame/SamplePlazaGlobalIllumination.cs`
- sample scene builder files

Changes:

- Add authored local volume around alley/courtyard bounds.
- Use thin-wall or small-room preset.
- Add diagnostics output for active local volume count if missing.

### Commit 3: Add shader fallback from gather tiles to exhaustive sampling

Files:

- `Njulf/Njulf.Shaders/forward.frag`
- shader compile tests if needed

Changes:

- Update `SampleDdgiIrradiance()`.
- Ensure fallback gather tiles no longer force empty DDGI.

### Commit 4: Fix hybrid composition fallback suppression

Files:

- `Njulf/Njulf.Shaders/forward.frag`

Changes:

- Use effective DDGI weight.
- Suppress environment fallback with effective weight instead of raw coverage.

### Commit 5: Surface-aware relocation shader improvement

Files:

- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl`
- `Njulf/Njulf.Shaders/ddgi_trace.comp`
- `Njulf/Njulf.Shaders/ddgi_relocate_classify.comp`

Changes:

- Aggregate nearest hit distance.
- Push probes toward target free-space distance.
- Keep relocation clamped and blended.
- Preserve classification behavior.

### Commit 6: Expose relocation settings

Files:

- `Njulf/Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf/Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf/Njulf.Shaders/common.glsl`
- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl`
- `Njulf/Njulf.Rendering/Pipeline/DdgiPipelinePasses.cs`
- `Njulf/Njulf.Tests/GPUStructLayoutTests.cs`

Changes:

- Add relocation settings.
- Add push constant data if needed.
- Update shader/C# layout constants.
- Add tests.

### Commit 7: Diagnostics and validation

Files may include:

- `Njulf/Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf/Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`
- `Njulf/NjulfHelloGame/SampleDiagnosticsReporter.cs`

Changes:

- Add counters for effective DDGI weight, relocation, fallback tiles, and classified probes where feasible.
- Update sample diagnostics output.

---

## Risk checklist

### Risk: Probe count explodes

Mitigation:

- Keep dense settings only for near cascade or authored local volumes.
- Track active-probe count and atlas memory.
- Avoid increasing X/Y/Z globally without budget checks.

### Risk: Relocation causes popping

Mitigation:

- Blend relocation over time.
- Reset only on new logical cells or true history resets.
- Clamp to less than half probe spacing.
- Avoid snapping to exact surfaces.

### Risk: Light leaks increase

Mitigation:

- Keep classification enabled.
- Keep thin-wall leak clamp enabled.
- Validate leak clamp debug view.
- Prefer dense local volume over excessive relocation.

### Risk: Exhaustive fallback is too expensive

Mitigation:

- Use it only when gather tile is invalid/fallback.
- Cap volume count to 16.
- Fix gather tile coverage so fallback is rare.

### Risk: Push constant layout mismatch

Mitigation:

- Update C# and GLSL together.
- Update size constants in `common.glsl`.
- Run `GPUStructLayoutTests`.
- Run shader compile tests.

---

## Recommended initial values

### Camera-relative clipmap

```csharp
gi.DdgiClipmapVerticalCenterOffset = 0.0f;
gi.DdgiClipmapBaseSpacing = 0.75f; // test 1.0f first if performance is tight
gi.DdgiClipmapProbeCountX = 24;
gi.DdgiClipmapProbeCountY = 14;
gi.DdgiClipmapProbeCountZ = 24;
```

### Dense local volume

```csharp
GlobalIlluminationProbeVolume alleyVolume =
    GlobalIlluminationProbeVolume.CreateThinWallRoomPreset(alleyBounds, 0.45f);

alleyVolume.Intensity = 1.5f;
alleyVolume.MaxProbeUpdatesPerFrame = 128;
alleyVolume.Priority = 256;
alleyVolume.UpdatePriority = 256;
```

### Surface-aware relocation

```text
Target surface distance fraction: 0.15 * minProbeSpacing
Minimum surface distance:         0.08m
Maximum relocation distance:      0.40 * minProbeSpacing
Relocation blend alpha:           0.20
```

### Temporary debug fallback

```csharp
gi.EnvironmentFallbackIntensity = 0.5f;
settings.Environment.DiffuseIntensity = 0.3f;
```

Return these to final art values after coverage is fixed.

---

## Final acceptance checklist

- [ ] Clipmap near-cascade probes cover the lower alley and floor.
- [ ] Dense local volume is active in gather-local-volume debug view.
- [ ] Gather fallback debug is not dominating the alley.
- [ ] `SampleDdgiIrradiance()` no longer returns black solely due to gather fallback.
- [ ] Hybrid composition preserves fallback where DDGI support is weak.
- [ ] Probe relocation moves probes into free space without snapping to surfaces.
- [ ] Classification still disables invalid probes.
- [ ] Final indirect debug view shows plausible low-frequency bounce.
- [ ] Normal lit view is brighter in shadowed surfaces without obvious leaks.
- [ ] Camera movement does not cause visible popping.
- [ ] Shader compile tests pass.
- [ ] GPU struct layout tests pass.
- [ ] Performance remains within the selected quality target.
