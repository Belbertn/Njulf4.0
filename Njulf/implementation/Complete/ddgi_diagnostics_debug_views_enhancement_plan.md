# DDGI Diagnostics and Debug-View Identification Enhancement Plan

## Goal

Make DDGI failures diagnosable from a single run without scanning the full renderer log, and make each DDGI debug view visually identifiable at first glance in screenshots/video captures.

Current evidence shows DDGI is active, ray queries are active, DDGI update/publish runs, cache generation advances, and warmup reaches recovery/steady states, but forward DDGI estimates remain zero while gather selection is `local/clipmap/fallback=0.000/1.000/0.000` and `forwardFallback=0/0`. The enhanced diagnostics must make that state obvious.

---

## Target files

Primary implementation targets:

- `Njulf/NjulfHelloGame/SampleDiagnosticsReporter.cs`
- `Njulf/NjulfHelloGame/SampleInputController.cs`
- `Njulf/NjulfHelloGame/Program.cs`
- `Njulf/Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf/Njulf.Shaders/forward.frag`
- `Njulf/Njulf.Tests/*` for coverage

Optional, if the renderer already has overlay text support:

- `Njulf/Njulf.Rendering/Debugging/DebugOverlayMode.cs`
- debug overlay rendering code

---

## Part 1: Add a runtime DDGI diagnostics filter

### 1. Add a diagnostic filter enum

Add a small enum in `SampleDiagnosticsReporter.cs` or a new sample-level file:

```csharp
internal enum SampleDiagnosticsFilter
{
    FullFrame,
    DdgiOnly
}
```

Do not add this to core renderer settings unless needed. This is a sample-console behavior, not a renderer feature.

### 2. Add filter state and public controls to `SampleDiagnosticsReporter`

Add:

```csharp
private SampleDiagnosticsFilter _filter = SampleDiagnosticsFilter.FullFrame;

public SampleDiagnosticsFilter Filter => _filter;

public SampleDiagnosticsFilter ToggleDdgiFilter()
{
    _filter = _filter == SampleDiagnosticsFilter.DdgiOnly
        ? SampleDiagnosticsFilter.FullFrame
        : SampleDiagnosticsFilter.DdgiOnly;

    Console.WriteLine($"Diagnostics filter: {_filter}");
    return _filter;
}
```

Also add a direct setter if tests or CLI options need it:

```csharp
public void SetFilter(SampleDiagnosticsFilter filter)
{
    _filter = filter;
    Console.WriteLine($"Diagnostics filter: {_filter}");
}
```

### 3. Split DDGI printing into a dedicated method

Extract the existing GI/DDGI console output into named methods:

```csharp
private static void PrintGiDiagnostics(RendererDiagnostics diagnostics) { ... }
private static void PrintDdgiSchedulerDiagnostics(RendererDiagnostics diagnostics) { ... }
private static void PrintDdgiUpdateDiagnostics(RendererDiagnostics diagnostics) { ... }
private static void PrintDdgiTriageDiagnostics(RendererDiagnostics diagnostics) { ... }
```

`PrintDdgiTriageDiagnostics` should be new and intentionally short.

### 4. Make `PrintFirstFrameDiagnostics` honor the filter

At the point where `RendererDiagnostics diagnostics` is available, branch early:

```csharp
if (_filter == SampleDiagnosticsFilter.DdgiOnly)
{
    _diagnosticFrameCounter++;

    // Keep the output frequent enough for interactive debugging but not every frame.
    if (_diagnosticFrameCounter % 30 != 0)
        return;

    PrintDdgiTriageDiagnostics(diagnostics);
    PrintGiDiagnostics(diagnostics);
    PrintDdgiSchedulerDiagnostics(diagnostics);
    PrintDdgiUpdateDiagnostics(diagnostics);
    return;
}
```

Then keep the current full-frame behavior unchanged for `FullFrame`.

### 5. Add a DDGI triage classification line

The new line should be at the top of DDGI-only output and should tell the developer what subsystem is most suspicious.

Example output:

```text
DDGI TRIAGE: state=FastGatherBlackHole severity=Red reason='clipmap tiles selected but forward support/data/effective all zero and fallback unused' next='shader fast-gather acceptance fallback'
```

Recommended classifier:

```csharp
private static string ClassifyDdgiState(RendererDiagnostics d)
{
    if (d.GlobalIlluminationEnabled == 0 || d.GlobalIlluminationMode == GlobalIlluminationMode.Disabled)
        return "Disabled";

    if (d.GlobalIlluminationMode == GlobalIlluminationMode.Ddgi &&
        d.GlobalIlluminationRayQueryActive == 0)
        return "RayQueryInactive";

    if (d.DdgiProbeVolumeCount <= 0 || d.DdgiActiveProbeCount <= 0)
        return "NoVolumesOrProbes";

    if (d.DdgiUpdateExecuted == 0 || d.DdgiProbesUpdated <= 0)
        return "NoProbeUpdates";

    bool fastGatherOnly =
        d.DdgiGatherSelectedClipmapTileFraction > 0.95f &&
        d.DdgiGatherFallbackTileFraction < 0.001f &&
        d.DdgiForwardGatherFallbackUsed == 0 &&
        d.DdgiForwardGatherFallbackDisabled == 0;

    bool noForwardContribution =
        d.DdgiAverageSupportCoverageEstimate <= 0.0001f &&
        d.DdgiAverageDataConfidenceEstimate <= 0.0001f &&
        d.DdgiAverageEffectiveContributionEstimate <= 0.0001f &&
        d.DdgiForwardEstimateRawDiffuseLuminance <= 0.0001f &&
        d.DdgiForwardEstimateFinalDiffuseLuminance <= 0.0001f;

    if (fastGatherOnly && noForwardContribution)
        return "FastGatherBlackHole";

    bool noProbeQuality =
        d.DdgiProbeIrradianceAlphaAverage <= 0.0001f &&
        d.DdgiProbeQualityXAverage <= 0.0001f &&
        d.DdgiProbeQualityYAverage <= 0.0001f &&
        d.DdgiProbeQualityZAverage <= 0.0001f;

    if (noProbeQuality && d.DdgiCacheGeneration > 0)
        return "ProbeQualityZero";

    if (d.DdgiClassifiedInactiveProbeCountEstimate > 0 &&
        d.DdgiAverageSupportCoverageEstimate <= 0.0001f)
        return "ClassificationOrActiveStateSuppressed";

    if (d.DdgiAverageSpatialCoverageEstimate > 0.0f &&
        d.DdgiAverageSupportCoverageEstimate <= 0.0001f)
        return "SpatialCoverageWithoutSupport";

    if (d.DdgiAverageEffectiveContributionEstimate > 0.0f ||
        d.DdgiForwardEstimateFinalDiffuseLuminance > 0.0f)
        return "Contributing";

    return "UnknownZeroContribution";
}
```

Also print the key “why” values in a compact way:

```text
DDGI TRIAGE VALUES: volumes=3 probes=24192/24192 updated=1024 cache=665:SteadyState gather=0.000/1.000/0.000 fallback=0/0 support/data/effective=0.000/0.000/0.000 alpha/q=0.000/0.000/0.000/0.000 inactive=929
```

### 6. Add the shortcut

Use `Ctrl+F` as the default shortcut unless it conflicts with your local workflow.

In `SampleInputController.cs` add:

```csharp
private const string ToggleDdgiDiagnosticsFilter = "toggle_ddgi_diagnostics_filter";
private bool _toggleDdgiDiagnosticsFilterPressed;
private readonly Action? _toggleDdgiDiagnosticsFilter;
```

Add a constructor parameter:

```csharp
Action? toggleDdgiDiagnosticsFilter = null
```

Store it.

In `Update`:

```csharp
if (WasChordPressed(Key.F, ref _toggleDdgiDiagnosticsFilterPressed))
{
    _toggleDdgiDiagnosticsFilter?.Invoke();
}
```

Do not add this to `DefaultActionBindings` because it is a physical Control chord, consistent with the current `WasChordPressed` pattern.

### 7. Wire the reporter into the input controller

`Program.cs` currently constructs `SampleInputController` before `SampleDiagnosticsReporter`. Reverse that dependency or pass a callback after construction.

Preferred minimal change:

1. Create `_diagnosticsReporter` before `_inputController`.
2. Pass `() => _diagnosticsReporter?.ToggleDdgiFilter()` into `SampleInputController`.

Example:

```csharp
_diagnosticsReporter = new SampleDiagnosticsReporter(
    materialManager,
    services.GetService<IModelRenderUploadService>());

_inputController = new SampleInputController(
    camera,
    input,
    Exit,
    renderer,
    lightManager,
    ResolveSceneLightingMode(),
    _sampleVfxEffects,
    _performanceScenarioRunner,
    () => CycleScene(meshManager, materialManager, lightManager, renderer, camera),
    () => _diagnosticsReporter.ToggleDdgiFilter());
```

Update constructor call sites and tests accordingly.

---

## Part 2: Make DDGI debug views visually self-identifying

### 8. Add a shared shader identity overlay for DDGI debug views

In `forward.frag`, add helper functions near the DDGI debug constants and helpers.

The overlay must be tiny, deterministic, and independent of scene lighting. It should not require text rendering.

Recommended elements:

- A 4-pixel border around the screen.
- A top-left badge with:
  - category color
  - binary bars encoding the `GlobalIlluminationDebugView` numeric value
  - a small checker/stripe pattern unique to DDGI
- A bottom-left channel legend strip for multi-channel views.

Example shader helpers:

```glsl
bool IsDdgiDebugView(uint view)
{
    return view >= GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE &&
           view <= GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION;
}

vec3 DdgiDebugCategoryColor(uint view)
{
    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_INDIRECT)
        return vec3(1.0, 0.55, 0.10); // radiance

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT)
        return vec3(0.10, 0.85, 1.0); // coverage/weight

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN)
        return vec3(0.25, 0.45, 1.0); // confidence

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP)
        return vec3(0.10, 1.0, 0.25); // visibility

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK)
        return vec3(1.0, 0.10, 0.85); // gather

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION)
        return vec3(0.85, 0.85, 0.10); // probe

    return vec3(1.0, 1.0, 1.0);
}

vec3 ApplyDdgiDebugIdentity(vec3 color, uint view)
{
    if (!IsDdgiDebugView(view))
        return color;

    vec2 p = gl_FragCoord.xy;
    vec2 screen = max(pc.Push.ScreenDimensions, vec2(1.0));
    vec3 category = DdgiDebugCategoryColor(view);

    // 4-pixel category border.
    bool border =
        p.x < 4.0 || p.y < 4.0 ||
        p.x >= screen.x - 4.0 ||
        p.y >= screen.y - 4.0;
    if (border)
        color = category;

    // Top-left badge, 96x32.
    bool badge = p.x < 96.0 && p.y < 32.0;
    if (badge)
    {
        float checker = mod(floor(p.x / 8.0) + floor(p.y / 8.0), 2.0);
        color = mix(category * 0.35, category, checker);

        // Encode debug-view id as six binary bars.
        for (uint bit = 0u; bit < 6u; bit++)
        {
            float x0 = 8.0 + float(bit) * 12.0;
            bool inBar = p.x >= x0 && p.x < x0 + 8.0 && p.y >= 20.0 && p.y < 28.0;
            if (inBar)
            {
                bool one = ((view >> bit) & 1u) != 0u;
                color = one ? vec3(1.0) : vec3(0.0);
            }
        }
    }

    // Bottom-left RGB channel legend for multi-channel diagnostic views.
    bool legend = p.x < 96.0 && p.y >= screen.y - 12.0;
    if (legend)
    {
        if (p.x < 32.0)
            color = vec3(1.0, 0.0, 0.0);
        else if (p.x < 64.0)
            color = vec3(0.0, 1.0, 0.0);
        else
            color = vec3(0.0, 0.0, 1.0);
    }

    return color;
}

void WriteDdgiDebugColor(uint view, vec3 color)
{
    WriteForwardColor(vec4(ApplyDdgiDebugIdentity(color, view), 1.0));
}
```

Adjust references if `GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_INDIRECT` does not exist. `FinalIndirect` is not DDGI-only; do not classify it as DDGI unless explicitly desired.

### 9. Route every DDGI debug return through the helper

Replace DDGI debug output like:

```glsl
WriteForwardColor(vec4(vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)), 1.0));
return;
```

with:

```glsl
WriteDdgiDebugColor(
    GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE,
    vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));
return;
```

Do this for all DDGI debug views:

- `DdgiIrradiance`
- `DdgiRawDiffuse`
- `DdgiSuppressionMask`
- `DdgiEffectiveWeight`
- `DdgiSpatialCoverage`
- `DdgiSupportCoverage`
- `DdgiDataConfidence`
- `DdgiVisibilityConfidence`
- `DdgiConfidenceChain`
- `DdgiEnvironmentFallbackWeight`
- `DdgiVisibility`
- `DdgiVisibilityMoments`
- `DdgiProbeIndex`
- `DdgiProbeState`
- `DdgiProbeRelocation`
- `DdgiRelocationNormalized`
- `DdgiProbeLogicalPosition`
- `DdgiProbeRelocatedPosition`
- `DdgiProbeRelocationDirection`
- `DdgiClassificationInvalidScore`
- `DdgiLeakClamp`
- `DdgiCoverage`
- `DdgiCascadeSelection`
- `DdgiCascadeBlendWeight`
- `DdgiUpdateReasons`
- `DdgiRayBudget`
- `DdgiGatherLocalVolume`
- `DdgiGatherClipmap`
- `DdgiGatherClipmapBlendWeight`
- `DdgiGatherFallback`

### 10. Add console view descriptions when cycling DDGI debug

When `CycleDdgiDebugView()` or focused GI debug changes the view, print a one-line visual identity hint.

Add:

```csharp
private static string DescribeDdgiDebugView(GlobalIlluminationDebugView view)
{
    return view switch
    {
        GlobalIlluminationDebugView.DdgiSupportCoverage =>
            "cyan border; grayscale support. Black means no accepted active probes.",
        GlobalIlluminationDebugView.DdgiDataConfidence =>
            "blue border; grayscale atlas/data confidence. Black means alpha/quality is zero.",
        GlobalIlluminationDebugView.DdgiConfidenceChain =>
            "blue border; RGB = irradiance alpha / quality / visibility confidence.",
        GlobalIlluminationDebugView.DdgiSuppressionMask =>
            "cyan border; RGB = support / leak attenuation / data confidence.",
        GlobalIlluminationDebugView.DdgiGatherClipmap =>
            "magenta border; hashed color = selected primary clipmap volume.",
        GlobalIlluminationDebugView.DdgiGatherFallback =>
            "magenta border; red = fallback, green = fast gather.",
        GlobalIlluminationDebugView.DdgiProbeLogicalPosition =>
            "yellow border; repeated world-position bands. Useful to spot wrong clipmap addressing.",
        _ => "DDGI debug view; border/badge encodes view category and id."
    };
}
```

Then append it to the existing print:

```csharp
Console.WriteLine($"DDGI debug legend: {DescribeDdgiDebugView(gi.DebugView)}");
```

### 11. Add a DDGI “investigation ring” shortcut

The existing `Ctrl+D` cycle has too many views. Add a second shortcut for the minimal sequence needed for the current bug.

Use `Ctrl+V` or another available chord:

```csharp
private bool _cycleDdgiInvestigationViewPressed;
```

Cycle only:

1. `DdgiGatherClipmap`
2. `DdgiGatherFallback`
3. `DdgiSupportCoverage`
4. `DdgiDataConfidence`
5. `DdgiConfidenceChain`
6. `DdgiIrradiance`
7. `DdgiRawDiffuse`
8. `DdgiProbeLogicalPosition`
9. `DdgiUpdateReasons`

This is the sequence needed to diagnose “all tiles select clipmap, but no support/data reaches final shading.”

---

## Part 3: Make screenshots/video unambiguous

### 12. Add a screenshot-friendly debug badge contract

A valid DDGI debug screenshot must reveal the view from:

- border color category,
- top-left binary badge,
- bottom-left RGB legend,
- console line printed when view was selected.

The screenshot must not require guessing from the scene.

### 13. Add optional CPU-side capture name suffix

If screenshot capture supports naming, add the active debug view and filter into the generated filename:

```text
screenshot-000123-gi-DdgiSupportCoverage-ddgi-filter.png
```

This is optional, but very useful when reviewing captures later.

---

## Part 4: Validation checklist

### Build and shader validation

- `dotnet build Njulf/Njulf.sln`
- Existing shader build tests must pass.
- `forward.frag` must compile with all new helper functions.
- No new push constants or descriptor bindings are required.

### Interactive validation

1. Run Sponza DDGI scenario.
2. Press `Ctrl+F`.
3. Console should switch to DDGI-only diagnostics.
4. Press DDGI investigation-cycle shortcut.
5. Each debug view must show:
   - colored screen border,
   - top-left badge,
   - bottom-left channel legend if multi-channel,
   - console legend line.
6. DDGI-only diagnostics should clearly report current failure as something like:
   - `FastGatherBlackHole`
   - `ProbeQualityZero`
   - `ClassificationOrActiveStateSuppressed`

### Regression expectations

- Full diagnostics mode remains unchanged unless `Ctrl+F` is pressed.
- Non-DDGI debug views are not visually modified.
- Final production rendering is unchanged when `GlobalIlluminationDebugView.None`.
- The badge overlays only the small border/corner/legend regions.

---

## Acceptance criteria

This task is complete when:

- `Ctrl+F` toggles DDGI-only diagnostics at runtime.
- DDGI-only diagnostics print a compact triage state and only GI/DDGI lines.
- The current failure state is immediately summarized as a named state, not hidden in a long frame log.
- Every DDGI debug view has an unmistakable visual identity.
- Screenshots of DDGI debug views can be distinguished without looking at console history.
- Existing full diagnostics and rendering behavior remain unchanged by default.
