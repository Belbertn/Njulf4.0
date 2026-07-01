# DDGI Production Readiness Fix Plan

## Mission

Fix the DDGI path so it is correct, robust, diagnosable, performant, and safe for production. The current symptom is not simply “DDGI is disabled.” The diagnostics show DDGI volumes, probes, updates, ray queries, and cache publication are active, but forward DDGI sampling reports zero support, zero data confidence, zero effective weight, zero luminance, and zero forward samples while the gather selector reports every tile as clipmap-selected and no fallback. Treat this as a production bug in the gather/sample acceptance chain until proven otherwise.

## Primary hypothesis

The forward fast-gather path is allowed to return an empty DDGI result just because a gather tile has a nominal clipmap candidate. The shader does not validate whether that candidate produced usable probe support or data before skipping exhaustive/fallback sampling. This creates a “valid candidate, zero contribution” black hole.

The production fix must not be a permanent brute-force exhaustive gather. It should include a safe shader-side fallback guard, correct per-tile candidate selection, better readiness/diagnostics, and tests that prevent regression.

## Non-negotiable constraints

- Do not “fix” production by disabling DDGI, disabling classification permanently, forcing all pixels through exhaustive gather, or hard-coding Sponza-specific behavior.
- Keep DDGI memory and GPU cost bounded.
- Preserve existing debug views, and add new diagnostics only if they are useful for automated verification.
- Every stage must leave the renderer buildable and testable.
- Prefer small commits that can be bisected.
- Any shader contract change must be mirrored in C# layout tests.

## Relevant files

Start with these files:

- `Njulf/Njulf.Shaders/forward.frag`
- `Njulf/Njulf.Shaders/ddgi_update_shared.glsl`
- `Njulf/Njulf.Rendering/Resources/DdgiGatherTileManager.cs`
- `Njulf/Njulf.Rendering/Resources/DdgiProbeVolumeManager.cs`
- `Njulf/Njulf.Rendering/Resources/DdgiFrameLayout.cs`
- `Njulf/Njulf.Rendering/Resources/CameraRelativeDdgiClipmapController.cs`
- `Njulf/Njulf.Rendering/Data/GlobalIlluminationProbeVolumeData.cs`
- `Njulf/Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf/Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`
- `Njulf/Njulf.Tests/Ddgi*.cs`
- `Njulf/Njulf.Tests/GlobalIlluminationProbeVolumeDataTests.cs`
- `Njulf/Njulf.Tests/RendererDiagnosticsTests.cs`

## Acceptance criteria

The fix is production-ready only when all of the following are true.

### Functional criteria

- DDGI debug views no longer collapse to black after warmup:
  - `DdgiSupportCoverage`
  - `DdgiDataConfidence`
  - `DdgiEffectiveWeight`
  - `DdgiIrradiance`
  - `DdgiRawDiffuse`
  - `DdgiConfidenceChain`
- `ddgiSamples forward` becomes nonzero in visible DDGI-covered scenes.
- `ddgiEstimate spatial/support/data/effective/rawLum/finalLum` become meaningfully nonzero after warmup in the Sponza/plaza sample.
- Tiles with stale, unready, or out-of-coverage fast candidates recover through bounded fallback instead of returning empty DDGI.
- Environment fallback still works when DDGI has no valid data.
- DDGI does not introduce NaNs, INF values, flickering probe-color explosions, or persistent black indirect in valid covered regions.

### Performance criteria

- The fallback path is bounded and instrumented.
- Production mode does not perform full exhaustive volume sampling for every fragment.
- GPU DDGI time remains inside the configured DDGI budget or triggers adaptive degradation with a clear diagnostic reason.
- DDGI memory remains within configured memory budgets or produces an explicit warning/degradation path.

### Test criteria

- `dotnet test` passes.
- Shader compilation passes.
- Shader validation passes if `spirv-val` is available.
- New DDGI unit tests fail on the old behavior and pass on the fix.
- A smoke run produces a diagnostics line proving nonzero forward DDGI sampling after warmup.


## Phase 1: Add failing regression tests before fixing

The agent must write tests that describe the failure before changing production behavior.

### 1.1 Add a fast-gather black-hole unit test

Create or extend a test file such as:

```text
Njulf/Njulf.Tests/DdgiGatherFallbackTests.cs
```

Test intent:

- A gather tile can contain a clipmap candidate.
- The actual sample result can still have zero support/data/ownership.
- In that case, the shader sampling policy must request fallback instead of accepting the empty candidate.

Because shader code is difficult to unit-test directly, add a small C# mirror for the acceptance policy.

Suggested production helper location:

```text
Njulf/Njulf.Rendering/Data/DdgiGatherAcceptance.cs
```

Suggested API:

```csharp
public readonly record struct DdgiGatherSampleQuality(
    float SpatialCoverage,
    float SupportCoverage,
    float DataConfidence,
    float OwnershipConsumed,
    float RawLuminance);

public static class DdgiGatherAcceptance
{
    public static bool HasUsableFastGatherResult(DdgiGatherSampleQuality quality)
    {
        // Thresholds must match shader constants.
    }
}
```

Test cases:

- Zero support returns `false`.
- Zero data confidence returns `false`.
- Zero ownership returns `false`.
- Valid low-but-nonzero support/data/ownership returns `true`.
- NaN and infinity return `false`.

### 1.2 Add gather tile selection tests

Extend `DdgiGatherTileManager` tests or add:

```text
Njulf/Njulf.Tests/DdgiGatherTileManagerTests.cs
```

Test intent:

- When camera-relative clipmaps exist, the tile manager may select clipmap candidates, but it must not report “no fallback needed forever” unless those candidates are known usable.
- Tiles outside known projected local volumes should either select a valid clipmap candidate with readiness or mark fallback/recovery eligibility.
- Readiness values of zero must not produce a confident fast-gather tile.

Required cases:

1. No DDGI active:
   - Header disabled.
   - No candidates.
2. DDGI active but no candidates:
   - Fallback flag set.
3. Clipmap candidate with zero readiness:
   - Candidate weight is zero.
   - Tile is fallback-eligible.
4. Local volume projected into screen tiles:
   - Local volume gets priority for those tiles.
5. Primary and secondary clipmaps both valid:
   - Blend weights are normalized and nonnegative.

### 1.3 Add diagnostic parser regression test

Add a test that parses a diagnostics line and fails if DDGI is in this broken state:

- `ddgiVolumes > 0`
- `ddgiUpdated > 0`
- `cacheGeneration > 0`
- `gatherFractions clipmap = 1.000`
- `ddgiEstimate support = 0`
- `ddgiSamples forward = 0`
- `forwardFallback = 0/0`

The test should not require an exact whole line. It should parse key-value fragments robustly.

Suggested file:

```text
Njulf/Njulf.Tests/DdgiDiagnosticsRegressionTests.cs
```

## Phase 2: Add shader-side safety fallback

This is the first production behavior change. It prevents black-hole results regardless of CPU tile bugs.

### 2.1 Add shared thresholds

In `forward.frag`, near the DDGI constants, add named constants:

```glsl
const float DDGI_FAST_GATHER_MIN_SPATIAL_COVERAGE = 0.0001;
const float DDGI_FAST_GATHER_MIN_SUPPORT_COVERAGE = 0.0001;
const float DDGI_FAST_GATHER_MIN_DATA_CONFIDENCE = 0.0001;
const float DDGI_FAST_GATHER_MIN_OWNERSHIP = 0.0001;
```

Keep them conservative. These thresholds are not artistic tuning; they decide whether the fast path has any data at all.

### 2.2 Add a validity function in `forward.frag`

Add:

```glsl
bool DdgiHasUsableFastGatherResult(DdgiSampleResult sample)
{
    if (any(isnan(sample.irradiance)) || any(isinf(sample.irradiance)))
        return false;

    return sample.spatialCoverage > DDGI_FAST_GATHER_MIN_SPATIAL_COVERAGE &&
           sample.supportCoverage > DDGI_FAST_GATHER_MIN_SUPPORT_COVERAGE &&
           sample.weight > DDGI_FAST_GATHER_MIN_DATA_CONFIDENCE &&
           sample.ownershipConsumed > DDGI_FAST_GATHER_MIN_OWNERSHIP;
}
```

### 2.3 Modify `SampleDdgiIrradiance`

Change the fast path from “return any non-fallback tile result” to “return only if usable.”

Target logic:

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
    if (ReadDdgiGatherTile(tile) &&
        (tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)
    {
        DdgiSampleResult tiled = SampleDdgiGatherCandidates(
            tile,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);

        if (DdgiHasUsableFastGatherResult(tiled))
            return tiled;

        // Continue to bounded fallback.
    }

    if (DdgiExhaustiveGatherFallbackEnabled())
        return SampleDdgiIrradianceExhaustive(min(volumeCount, 16u), worldPosition, normal, indirectAo, globalIntensity);

    return result;
}
```

### 2.4 Instrument fallback

Add a forward diagnostic counter for:

- fast tile attempted
- fast tile accepted
- fast tile rejected due to zero spatial
- fast tile rejected due to zero support
- fast tile rejected due to zero data
- fast tile rejected due to zero ownership
- shader fallback attempted
- shader fallback accepted
- shader fallback empty

Expose these in the existing `forwardFallback` and/or `ddgiSupportReject` diagnostics. Do not silently hide fallback cost.

### 2.5 Test after Phase 2

Run:

```bash
dotnet build Njulf/Njulf.sln
dotnet test Njulf/Njulf.sln
```

Then run the sample with DDGI debug views. Expected transitional result:

- `forwardFallback` should become nonzero if the fast tile path is still bad.
- `ddgiSamples forward` should become nonzero if the atlas/probe data is actually valid.
- If `forwardFallback` is nonzero but `ddgiSamples forward` is still zero, continue to Phase 4 and Phase 5 because probe state/atlas data is also invalid.

## Phase 3: Make gather tile selection production-correct

The shader fallback is only a safety net. The CPU tile selection must stop declaring every tile confidently clipmap-covered when it cannot prove usable support.

### 3.1 Define gather tile semantics

Update comments and tests so the meaning is explicit:

- `TileLocalVolumeValidFlag`: local volume candidate exists and is projected to overlap this tile.
- `TilePrimaryClipmapValidFlag`: a clipmap candidate exists and is allowed to be tried for this tile.
- `TileSecondaryClipmapValidFlag`: secondary clipmap candidate exists and is allowed to be tried for blend/transition.
- `TileFallbackFlag`: fast tile candidates are absent or not trusted; shader should use bounded fallback.
- A tile can have both candidates and fallback eligibility if candidates are low-readiness or conservative.

### 3.2 Stop treating global clipmap existence as perfect tile coverage

In `DdgiGatherTileManager.BuildTiles`, the current production-risk behavior is equivalent to:

```csharp
selectedClipmapTileCount = primaryClipmap != InvalidVolumeIndex ? tileCount : 0;
```

Replace this with a readiness-aware and coverage-aware decision.

Minimum production-safe policy:

1. If no primary clipmap exists, mark fallback.
2. If primary clipmap readiness is zero, mark fallback.
3. If primary clipmap exists but is not known warmed enough for the current region, mark fallback-eligible.
4. If no local volume overlaps the tile and the primary clipmap has low readiness, mark fallback-eligible.
5. Keep clipmap candidates for speed, but do not suppress fallback unless the tile has enough readiness.

### 3.3 Add tile readiness metadata

Extend `DdgiGatherSupportReadiness` or add a new struct that distinguishes:

- local readiness
- primary clipmap readiness
- secondary clipmap readiness
- conservative fallback eligibility
- warmup state

Do not normalize all-zero readiness to steady in production code unless the caller explicitly asks for legacy behavior. The current `NormalizeOrSteady` behavior is dangerous because “no data supplied” becomes “fully ready.” Split it into two APIs:

```csharp
NormalizeForRuntime()
NormalizeForLegacyTestsOnly()
```

Runtime should treat missing readiness conservatively.

### 3.4 Add fallback eligibility to tile upload

When building a tile:

- If all candidate weights are zero, set fallback.
- If support readiness is below a configurable minimum, set fallback.
- If the tile only has clipmap candidates and clipmap readiness is below threshold, set fallback.

Suggested setting:

```csharp
DdgiGatherTileMinimumReadiness = 0.05f
```

This setting should live under global illumination settings, with a default that preserves quality.

### 3.5 Verify gather diagnostics

After this phase, `gatherFractions` should no longer be misleading. In the broken case it should show either:

- some fallback fraction, or
- clipmap fraction plus forward fallback counters.

A report of `clipmap=1.000 fallback=0.000` must mean the tile builder genuinely trusts all fast candidates.

## Phase 4: Validate the probe atlas and state update chain

If Phase 2 still yields zero data after fallback, the issue is not only tile selection. Validate that updated probes actually write nonzero atlas/state data.

### 4.1 Add a DDGI probe-state validation debug path

Add a low-cost optional debug readback or compute reduction for updated probes:

Per frame, record:

- updated probe count
- probes with nonzero irradiance atlas alpha
- probes with nonzero irradiance RGB
- probes with nonzero visibility moments
- probes with active state
- probes with nonzero `qualityAndReason.x/y/z`
- probes rejected by classification
- probes with only sky misses
- probes with only backfaces

Expose as diagnostics:

```text
ddgiAtlas updated/nonzeroAlpha/nonzeroRgb/nonzeroVisibility=...
ddgiState active/qx/qy/qz/nonzero=...
ddgiTrace hits/misses/backfaces/close=...
```

### 4.2 Validate blend pass behavior

Inspect `ddgi_update_shared.glsl` around the blend pass.

Checks:

- `raysThisProbe` is never zero for active requests.
- Shared arrays are written for every ray consumed by `AccumulateProbeIrradianceTexel`.
- `SharedRayIrradiance[rayIndex]` is initialized for all ray indices used by the irradiance accumulation loop.
- `WriteProbeIrradianceAtlasTexel` writes alpha greater than zero for valid rays.
- Blend hysteresis cannot keep alpha at zero forever after reset.
- `previousActiveProbe` is not zero on first valid update.

### 4.3 Add shader/CPU mirror tests for atlas packing

Create tests that verify:

- irradiance atlas word offsets match expected sizes
- visibility atlas word offsets match expected sizes
- probe index to atlas offset is correct
- packed half read/write contracts are mirrored

Suggested test file:

```text
Njulf/Njulf.Tests/DdgiAtlasLayoutTests.cs
```

### 4.4 Add a forced-probe shader debug mode

Add a temporary or debug-only option:

- force active probes
- force quality confidence to one
- force atlas alpha acceptance
- preserve raw irradiance visibility

Use it only to isolate classification/quality from atlas writes. It must be behind debug settings and disabled in production defaults.

Expected outcomes:

- If forced confidence makes DDGI visible, classification/quality is the next root cause.
- If forced confidence still gives zero irradiance, trace/blend/atlas writes are broken.

## Phase 5: Harden classification and relocation

The logs show `classification` running and probe confidence reported as zero. Even if gather fallback fixes sampling, production must not allow classification to kill all probes in normal scenes.

### 5.1 Treat miss-only sky probes as valid irradiance contributors

Current quality logic must not require geometric hits for a probe to be useful. In outdoor or open atrium scenes, many rays miss and sample sky/environment. Those probes should still be valid for diffuse sky irradiance.

Change classification policy:

- Hit confidence is useful for visibility confidence, not the sole validity of irradiance.
- Miss ratio should reduce geometry visibility confidence only when it indicates poor local occlusion data, not zero irradiance validity.
- Sky/environment radiance from misses should produce positive irradiance alpha/data confidence.

### 5.2 Make invalid-probe detection conservative during warmup

During cold start/recovery/near-cascade warmup:

- Do not hard-disable probes based on a single frame of backface/close-hit evidence.
- Require multiple consecutive invalid observations before setting active to zero.
- Blend invalid state more slowly than radiance state.
- Reset invalid history on teleport/layout change/new cell.

### 5.3 Avoid backface over-suppression on thin or single-sided geometry

Sponza-like scenes have many thin surfaces, curtains, railings, arches, and potentially inconsistent material sidedness.

Implement one or more of:

- Material flag for two-sided or thin geometry in ray-query instance metadata.
- Backface penalty capped below hard invalidation unless close-hit ratio is also high.
- Backface invalidation only if nearest hit is within relocation/normal-bias danger range.
- Classification threshold scaled by probe spacing.

### 5.4 Add tests for classification policy

Add unit tests for the CPU mirror of classification:

- miss-only sky probe remains active with positive irradiance confidence
- close-hit heavy probe can relocate or become invalid
- backface-only far hits do not immediately invalidate
- warmup requires multiple frames to hard-disable
- reset/new-cell clears invalid history

Suggested file:

```text
Njulf/Njulf.Tests/DdgiProbeClassificationPolicyTests.cs
```

## Phase 6: Correct cache warmup/readiness semantics

The diagnostics can show `cacheWarmup=SteadyState` while forward DDGI support remains zero. That is not production-ready.

### 6.1 Separate scheduler warmup from shading readiness

Define distinct concepts:

- scheduler warmup: enough probes have been scheduled/updated
- atlas readiness: enough updated probes have nonzero atlas alpha/data
- shading readiness: forward sampling observes nonzero support/data/effective weight

Do not use scheduler warmup alone to imply shading readiness.

### 6.2 Add readiness outputs

Expose:

```text
ddgiReadiness scheduler/atlas/shading=...
```

Suggested calculation:

- scheduler readiness: existing warmup fractions
- atlas readiness: nonzero atlas alpha probes / warmed probes
- shading readiness: accepted forward DDGI samples / attempted forward DDGI samples

### 6.3 Use readiness in tile building

Feed atlas/shading readiness into `DdgiGatherTileManager.Upload` instead of relying only on scheduler warmup.

A tile should not fully suppress fallback until readiness is above threshold.

## Phase 7: Implement bounded production fallback

Exhaustive gather is useful as a correctness fallback, but unbounded per-fragment exhaustive scanning of all volumes is not production-safe.

### 7.1 Add a bounded fallback mode

Modes:

```csharp
public enum DdgiForwardFallbackMode
{
    Disabled,
    DebugExhaustive,
    BoundedNearbyVolumes,
    BoundedClipmapThenEnvironment
}
```

Production default:

```text
BoundedClipmapThenEnvironment
```

Debug default can enable exhaustive.

### 7.2 Bounded fallback strategy

When fast gather fails:

1. Try local volume if any projected local candidate exists.
2. Try primary clipmap.
3. Try secondary clipmap if near transition or primary failed.
4. Try at most `N` additional volumes, sorted by expected relevance.
5. If still empty, return environment fallback with diagnostic reason.

Recommended defaults:

```text
DdgiForwardFallbackMaxExtraVolumes = 2
DdgiForwardFallbackMaxTotalVolumes = 4
DdgiForwardFallbackMinimumAcceptedSupport = 0.0001
```

### 7.3 Diagnostics for fallback cost

Expose:

```text
forwardFallback attempted/accepted/empty/volumesAvg/volumesMax=...
```

Acceptance criterion:

- Fallback is allowed during warmup and edge cases.
- Fallback should decrease as readiness increases.
- Persistent fallback above a threshold should emit a warning.

## Phase 8: Add production diagnostics and warnings

### 8.1 Add explicit warnings for impossible states

Warn when:

- DDGI active, probes updated, cache generation positive, but forward samples are zero for more than `N` frames.
- Gather tiles report 100% clipmap and 0% fallback while fast gather acceptance is 0%.
- Cache warmup is steady but shading readiness is near zero.
- Probe atlas alpha remains zero after repeated updates.
- Classification disables more than a threshold of recently updated probes.

Example warning:

```text
DDGI warning: fast gather starvation: clipmapTiles=100%, acceptedFast=0%, fallback=0%, shadingReadiness=0%; forcing bounded fallback.
```

### 8.2 Add an automatic safety valve

If the impossible state persists for several frames:

- Enable bounded fallback automatically.
- Reduce trust in fast gather candidates.
- Keep logging the reason.

Do not silently change rendering mode.

## Phase 9: Validate with sample scenes

Use at least these scenarios:

1. Sponza / plaza large indoor-outdoor scene.
2. Simple Cornell-box-like closed room.
3. Outdoor open sky scene.
4. Thin-wall corridor scene.
5. Scene with no DDGI volumes.
6. Scene with only authored local volume.
7. Scene with only camera-relative clipmaps.
8. Teleport/camera-cut scenario.
9. Fast camera movement through clipmap transitions.
10. Low budget / memory pressure scenario.

For each scenario, capture:

- startup after 1 frame
- after 30 frames
- after 120 frames
- after steady state
- after camera movement
- after teleport

Record debug screenshots for:

- `DdgiIrradiance`
- `DdgiRawDiffuse`
- `DdgiSupportCoverage`
- `DdgiDataConfidence`
- `DdgiEffectiveWeight`
- `DdgiGatherFallback`
- `DdgiGatherClipmap`
- `DdgiConfidenceChain`

## Phase 10: Performance tuning

### 10.1 Budget DDGI sampling cost

Measure forward shader cost with:

- fast gather only
- bounded fallback during warmup
- bounded fallback steady-state
- debug exhaustive fallback

Ensure debug exhaustive is clearly not production mode.

### 10.2 Reduce persistent fallback

If fallback remains high after warmup:

- Improve CPU tile candidate selection.
- Increase tile metadata precision.
- Add per-tile depth/world classification.
- Add more candidate slots if needed.

Do not simply increase budgets without explaining why.

### 10.3 Consider GPU-built gather tiles later

If CPU tile projection is too conservative, plan a later GPU pass that builds gather tiles from depth/normal buffers and clipmap coverage. Do not block the current fix on this unless CPU tile selection cannot meet acceptance criteria.

## Phase 11: Final cleanup

1. Remove temporary force-confidence debug code unless it is intentionally kept behind a documented debug flag.
2. Ensure all new settings have defaults and descriptions.
3. Ensure diagnostics are stable and parseable.
4. Ensure no shader constants diverge from C# mirror constants.
5. Ensure no all-exhaustive path is enabled in production defaults.
6. Ensure fallback counters are not always zero.
7. Ensure warnings only trigger on real unhealthy states.

## Phase 12: Final validation checklist

Run:

```bash
dotnet format Njulf/Njulf.sln --verify-no-changes
dotnet build Njulf/Njulf.sln
dotnet test Njulf/Njulf.sln
```

Compile shaders using the project’s normal shader build path. If available, run:

```bash
spirv-val Njulf/Njulf.Shaders/*.spv
```

Then run the sample and verify at least one steady-state diagnostics line satisfies:

```text
ddgiVolumes > 0
ddgiProbes > 0
ddgiUpdated > 0
cacheGeneration > 0
cacheWarmup is NearCascadeWarmup or SteadyState
forward ddgiSamples > 0
supportCoverage > 0
dataConfidence > 0
effectiveWeight > 0
rawLum > 0 or finalLum > 0
```

Also verify that the broken-state signature is gone:

```text
clipmap=1.000 AND fallback=0.000 AND support=0.000 AND data=0.000 AND forwardSamples=0
```


## Agent working rules
- Complete every step in detail, to high quality
- After shader changes, rebuild shaders and run a smoke frame.
- Do not proceed from Phase 2 to Phase 3 until fallback counters prove the black-hole path is visible.
- Do not proceed from Phase 4 to Phase 5 until atlas/state diagnostics prove whether the problem is data generation or data selection.
- Do not tune thresholds until correctness is visible in debug views.
- Do not accept any fix that only works with classification disabled.
- Do not accept any fix that only works with exhaustive fallback enabled.

## Quick triage decision tree

```text
DDGI active + updates > 0 + forward samples = 0
    |
    +-- Fast gather attempted?
    |       |
    |       +-- yes -> Does fast sample have support/data?
    |       |          |
    |       |          +-- no -> fallback must run; fix shader acceptance and tile readiness
    |       |          +-- yes -> debug final composition/trust suppression
    |       |
    |       +-- no -> gather tile buffer/header/bindless issue
    |
    +-- Fallback runs but still zero?
            |
            +-- atlas alpha zero -> trace/blend/atlas write issue
            +-- quality zero -> classification/readiness issue
            +-- spatial zero -> volume addressing/coverage issue
            +-- irradiance zero but alpha positive -> ray radiance/material/light/env issue
```

## Expected end state

The finished system should have three layers of protection:

1. Correct tile candidates that usually choose the right local/clipmap volumes.
2. Shader-side acceptance checks that prevent empty fast-gather results from black-holing DDGI.
3. Bounded fallback and readiness diagnostics that keep production rendering correct while exposing performance or warmup problems.

When this is done, DDGI can fail gracefully under low readiness or bad data, but it should no longer silently report active clipmap coverage while producing zero indirect lighting.
