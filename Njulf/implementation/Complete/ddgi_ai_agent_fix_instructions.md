# AI Agent Instruction: Fix DDGI Fast-Gather Black-Hole on `Simplified`

## Mission

Fix the DDGI issue in `Belbertn/Njulf4.0` branch `Simplified` to production readiness.

The current failure mode is:

- DDGI is enabled.
- Ray query is supported and active.
- DDGI update, blend, relocate/classify, and publish passes are executing.
- The cache reaches `Recovery` and later `SteadyState`.
- The gather tile path reports all tiles as clipmap tiles: `gatherFractions local/clipmap/fallback=0.000/1.000/0.000`.
- Shader-side fallback is never used: `gatherFallback=0`, `forwardFallback=0/0`.
- The final forward DDGI estimate remains fully zero:
  `spatial/support/data/visibility/leak/effective/rawLum/finalLum/ownership = 0`.
- `ddgiSamples forward/probeQuality=0/0`, which means the forward shader is not reaching usable probe support.
- This persists even after warmup reaches `SteadyState`.

The essential fix is to stop treating "a tile has a clipmap candidate" as proof that the candidate produced usable DDGI. A fast-gather tile result must be validated before it is accepted. If it is empty or invalid, forward shading must run a bounded fallback path instead of returning black.

---

## Files to inspect first

Inspect these files before editing:

1. `Njulf/Njulf.Shaders/forward.frag`
   - `DdgiSampleResult`
   - `EmptyDdgiSampleResult`
   - `SampleDdgiGatherCandidates`
   - `SampleDdgiIrradianceExhaustive`
   - `SampleDdgiIrradiance`
   - DDGI debug views near `GLOBAL_ILLUMINATION_DEBUG_DDGI_*`
   - diagnostic counter helpers around `AccumulateDdgiForwardEstimateDiagnostics`

2. `Njulf/Njulf.Rendering/Resources/DdgiGatherTileManager.cs`
   - `BuildTiles`
   - `SelectClipmapCandidates`
   - fallback flag assignment
   - tile statistics: selected local, selected clipmap, fallback tile count

3. DDGI diagnostics formatting/writing:
   - search for `forwardFallback`
   - search for `emptyTiles`
   - search for `gatherFallback`
   - search for `ddgiEstimate`
   - likely files include `RendererDiagnostics.cs` and/or `PerformanceSnapshotWriter.cs`

4. Tests:
   - search for DDGI shader mirror tests
   - search for gather tile manager tests
   - add or update tests in `Njulf/Njulf.Tests`

---

## Root cause to fix

### Current shader bug

`SampleDdgiIrradiance()` currently accepts a non-fallback tile like this:

```glsl
if (ReadDdgiGatherTile(tile) &&
    (tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)
    return SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);
```

That is not safe. `SampleDdgiGatherCandidates(...)` can return an empty `DdgiSampleResult` with zero support, zero data confidence, zero ownership, and zero irradiance. Returning it immediately black-holes DDGI and prevents fallback.

### Current CPU tile-selection bug

`DdgiGatherTileManager.BuildTiles()` currently selects a primary clipmap once, then assigns it to every tile:

```csharp
int selectedClipmapTileCount = primaryClipmap != InvalidVolumeIndex ? tileCount : 0;
```

and fallback is set only when no local/primary/secondary candidate exists. Since a primary clipmap usually exists, the fallback fraction stays zero even when the actual shaded pixel cannot use the chosen clipmap candidate.

The CPU selector can remain conservative for the first patch, but the shader must not trust it blindly.

---

## Production fix requirements

### Requirement 1: Add shader-side fast-gather acceptance validation

In `forward.frag`, add a helper near the DDGI gather functions:

```glsl
bool DdgiSampleHasUsableSupport(DdgiSampleResult sample)
{
    return sample.spatialCoverage > 0.0001 &&
           sample.supportCoverage > 0.0001 &&
           sample.weight > 0.0001 &&
           sample.ownershipConsumed > 0.0001;
}
```

Do not require nonzero luminance in this helper. A physically dark area can legitimately have low irradiance. The acceptance test should be based on support/data/ownership, not brightness.

Optionally add a stricter debug helper for logging only:

```glsl
bool DdgiSampleHasVisibleRadiance(DdgiSampleResult sample)
{
    return dot(max(sample.irradiance, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722)) > 0.00001;
}
```

Do not use the radiance helper to reject data in production.

---

### Requirement 2: Make invalid fast-gather results fall back

Replace the immediate return in `SampleDdgiIrradiance()` with explicit validation.

The final logic should look like this:

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
    bool triedFastTile = false;
    bool fastTileWasInvalid = false;

    if (ReadDdgiGatherTile(tile) &&
        (tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)
    {
        triedFastTile = true;

        DdgiSampleResult tiled = SampleDdgiGatherCandidates(
            tile,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);

        if (DdgiSampleHasUsableSupport(tiled))
            return tiled;

        fastTileWasInvalid = true;
    }

    // Production safety net:
    // If the fast tile exists but produced no usable support, do not rely solely
    // on DDGI_EXHAUSTIVE_GATHER_FALLBACK_ENABLED. The fast path has already
    // failed correctness, so run bounded recovery.
    if (fastTileWasInvalid || DdgiExhaustiveGatherFallbackEnabled())
    {
        DdgiSampleResult fallback = SampleDdgiIrradianceExhaustive(
            min(volumeCount, 16u),
            worldPosition,
            normal,
            indirectAo,
            globalIntensity);

        if (DdgiSampleHasUsableSupport(fallback))
            return fallback;

        // Returning fallback even if empty is acceptable for debug accounting,
        // but it must be distinguishable in diagnostics.
        return fallback;
    }

    return result;
}
```

Important: in the user diagnostics, `gatherFallback=0`, so a patch that only falls through to `DdgiExhaustiveGatherFallbackEnabled()` will not fix the issue unless that setting is also enabled. Production safety fallback must run when a non-fallback fast tile returns unusable data.

---

### Requirement 3: Add or wire forward fallback diagnostics

The runtime output already exposes fields like `forwardFallback=0/0` and `emptyTiles=0`. Make sure they are real, shader-driven counters rather than always zero.

Add or wire these counters:

- fast tile sampled count
- fast tile accepted count
- fast tile invalid/empty count
- fallback attempted count
- fallback accepted count
- fallback empty count

A good final format is:

```text
forwardFallback=<accepted>/<attempted>
emptyTiles=<fastTileInvalidOrEmptyCount>
```

Use the existing renderer diagnostic buffer/counter pattern in `forward.frag`. Search for current `AddRendererDiagnostic(...)` usage and follow existing conventions.

Acceptance criteria:

- With the current Sponza/plaza repro, `forwardFallback` should no longer be `0/0` if the fast clipmap tile produces no usable support.
- `emptyTiles` should become nonzero while the issue is present and should trend down after the deeper tile-selection fix.
- The DDGI estimate counters should become nonzero after fallback starts finding usable support.

---

### Requirement 4: Fix the CPU gather tile selector so it is not misleading

The shader-side fallback is mandatory, but the CPU tile selector should also be corrected.

In `DdgiGatherTileManager.BuildTiles()`:

1. Stop treating a global primary clipmap as valid proof for every tile.
2. Track "candidate assigned" separately from "candidate likely usable".
3. Make fallback eligibility conservative:
   - If no authored/local volume overlaps a tile and clipmap coverage cannot be proven for the tile, mark the tile fallback.
   - If clipmap candidates are assigned globally for now, record this as "unproven clipmap fast candidate" and rely on shader validation.
4. Do not allow `selectedClipmapTileCount = tileCount` to hide invalid fast gather.
5. Add a `candidateMode` or equivalent internal diagnostic if useful:
   - `Local`
   - `ClipmapProven`
   - `ClipmapUnproven`
   - `Fallback`

Minimal acceptable production step:

- Keep primary/secondary clipmap assignment if a full geometric tile test is too expensive right now.
- But add diagnostics that make it obvious these are "unproven" fast candidates.
- Ensure shader-side invalid-result fallback is always active.

Better production step:

- Estimate per-tile conservative world/depth coverage using available depth or camera frustum info.
- Assign fallback for tiles outside clipmap influence or with no proved clipmap coverage.
- Keep shader validation as a permanent safety net.

---

### Requirement 5: Add tests that prevent regression

Add tests that fail if the black-hole behavior returns.

#### Shader/source regression test

Add a test that parses `forward.frag` and fails if `SampleDdgiIrradiance()` contains an immediate return from `SampleDdgiGatherCandidates(...)`.

Test intent:

```text
SampleDdgiIrradiance must validate fast-gather output before returning it.
```

The test should require the function to contain:

- `DdgiSampleHasUsableSupport`
- a fallback call after fast-gather failure
- no direct `return SampleDdgiGatherCandidates(...)`

#### Gather tile manager tests

Add tests for `DdgiGatherTileManager.BuildTiles()`:

1. When DDGI is active but no candidate can be proven, fallback tiles are produced.
2. When a primary clipmap exists, the test must not assume every tile is a proven valid clipmap tile unless the implementation can actually prove it.
3. Authored/local volume tiles still select local candidates.
4. Fallback fraction and selected clipmap fraction are consistent with tile flags.

#### Diagnostics tests

Add or update tests for diagnostics formatting:

- `forwardFallback=<accepted>/<attempted>` reflects nonzero fallback attempts.
- `emptyTiles` reports invalid fast tile results.
- The test should not pass if those fields are hardcoded to zero.

---

### Requirement 6: Validate with the exact repro diagnostics

Run the sample scene that generated the diagnostics and verify these minimum outcomes.

Before fix, the repro shows:

```text
gatherFallback=0
forwardFallback=0/0
emptyTiles=0
gatherFractions local/clipmap/fallback=0.000/1.000/0.000
ddgiEstimate spatial/support/data/visibility/leak/effective/rawLum/finalLum/ownership=0.000/...
ddgiSamples forward/probeQuality=0/0
```

After the shader fallback fix, expected minimum:

```text
forwardFallback attempted > 0
emptyTiles > 0 if fast clipmap candidates still produce no support
ddgiSamples forward > 0 once fallback finds support
ddgiEstimate spatial/support/data/effective > 0 once DDGI data is usable
```

After CPU tile selector fix, expected stronger result:

```text
gatherFractions should no longer hide everything as clipmap if the clipmap candidate is unproven
forwardFallback attempts should decrease compared to the shader-only patch
ddgiEstimate finalLum should be nonzero in lit Sponza areas
```

Do not mark the task complete until `DdgiSupportCoverage`, `DdgiDataConfidence`, `DdgiEffectiveWeight`, `DdgiIrradiance`, and `DdgiRawDiffuse` debug views show meaningful nonzero output in the repro.

---

## Implementation order

### Step 1: Patch `forward.frag`

- Add `DdgiSampleHasUsableSupport`.
- Replace the immediate fast-gather return in `SampleDdgiIrradiance`.
- Add bounded fallback when a fast tile returns an unusable result.
- Add diagnostic counters for fast invalid and fallback attempted/accepted.
- Compile shaders.

### Step 2: Run the repro

- Confirm that `forwardFallback` is no longer `0/0`.
- Confirm that `emptyTiles` reflects invalid fast gather.
- Check DDGI debug views.

### Step 3: Patch `DdgiGatherTileManager.cs`

- Stop reporting every tile as a proven clipmap tile just because a primary clipmap exists.
- Add conservative fallback marking for unproven tiles or explicitly label them as unproven.
- Keep shader validation permanently.

### Step 4: Add regression tests

- Add shader source test preventing direct return from `SampleDdgiGatherCandidates`.
- Add gather tile construction tests.
- Add diagnostics formatting tests.

### Step 5: Final production validation

Run at least these modes/views:

- `None`
- `DdgiSupportCoverage`
- `DdgiDataConfidence`
- `DdgiVisibilityConfidence`
- `DdgiConfidenceChain`
- `DdgiEffectiveWeight`
- `DdgiIrradiance`
- `DdgiRawDiffuse`
- `DdgiProbeLogicalPosition`
- `DdgiGatherClipmapBlendWeight`

Record before/after diagnostic snippets in the PR or commit message.

---

## Do not do these incomplete fixes

Do not only enable `DdgiExhaustiveGatherFallbackEnabled` in settings. That masks the issue but does not fix the unsafe fast-gather acceptance.

Do not only change the CPU tile selector. Shader-side validation must remain because any future stale, uninitialized, out-of-date, or under-warmed tile candidate can still produce zero support.

Do not use luminance as the only validity check. Dark probe data can be valid. Use support/data/ownership.

Do not suppress the problem by increasing environment fallback. The bug is that DDGI support is zero while updates are running.

Do not mark success based on `cacheWarmup=SteadyState` alone. The uploaded diagnostics show `SteadyState` with DDGI estimates still zero.

---

## Definition of done

The fix is complete only when all of the following are true:

1. `SampleDdgiIrradiance()` validates fast-gather output before returning.
2. A failed fast tile triggers bounded fallback even if the global exhaustive fallback setting is disabled.
3. `forwardFallback` and `emptyTiles` diagnostics are real and nonzero in the current failure repro.
4. `DdgiGatherTileManager` no longer reports every tile as a proven clipmap tile without proof.
5. Tests prevent reintroducing `return SampleDdgiGatherCandidates(...)` in `SampleDdgiIrradiance`.
6. The repro no longer shows all-zero `ddgiEstimate` after warmup.
7. DDGI debug views show nonzero support/data/effective weight/irradiance in lit scene regions.
8. No large performance regression is introduced after the CPU selector is fixed; shader fallback should be a safety net, not the primary path forever.

---

## Suggested commit message

```text
Fix DDGI fast-gather empty-result fallback

Validate DDGI fast-gather results before accepting tile candidates.
Run bounded forward fallback when clipmap gather produces no usable support,
wire fallback/empty-tile diagnostics, and tighten gather tile candidate
classification so global clipmap assignment no longer hides invalid tiles.
Add regression tests for shader acceptance and gather fallback behavior.
```
