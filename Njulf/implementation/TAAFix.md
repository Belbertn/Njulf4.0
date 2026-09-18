# TAA stability fix — Njulf4.0 `Simplified-SDF`

## Context

TAA on `Simplified-SDF` shows jagged edges oscillating back and forth every frame. The
resolve (`Njulf/Njulf.Shaders/taa_resolve.frag`) never converges: the history is not
anchored to the pixel grid, and the two rejection heuristics it uses fire hardest at exactly
the silhouette pixels TAA exists to fix. The result is that edge pixels present the raw,
jittered, un-antialiased current frame most frames, and the accumulated image translates
sub-pixel on the jitter cadence.

Reference implementations used throughout: **Flax** (`Source/Shaders/TAA.shader`,
`Source/Shaders/Temporal.hlsl`, `Content/Editor/MaterialTemplates/Features/MotionVectors.hlsl`,
`Source/Engine/Graphics/RenderView.cpp`) and **Prowl**
(`Prowl.Runtime/Assets/Defaults/TAA.shader`, `Rendering/Image Effects/TAAEffect.cs`).

## Root causes (ranked)

**1. Motion vectors carry the jitter delta — the primary cause.**
`motion_vector.mesh:115-116` multiplies by `pc.Push.ViewProjectionMatrix` (jittered, built by
`SceneDataBuilder.ApplyProjectionJitter` at `SceneDataBuilder.cs:593`) and by the previous
frame's equally-jittered `PreviousViewProjectionMatrix`. `motion_vector.frag:29` writes
`inCurrentUv - inPreviousUv`, so

```
velocity = physical motion + (Jₙ − Jₙ₋₁)
```

`taa_resolve.frag:67` reprojects with that raw velocity. The history is therefore re-fetched,
re-filtered and re-written at a location that swings by up to ~1 px and flips direction every
frame on the 8-frame Halton cycle. The presented image inherits the current frame's jitter
offset instead of converging on the pixel grid. Both references remove the jitter from *both*
clip positions at the source (Flax `MotionVectors.hlsl`: `prevHPos -= TemporalAAJitter.zw;
curHPos -= TemporalAAJitter.xy;`; Prowl `Line.shader:64-65`).

**2. The depth gate rejects history at every silhouette.**
`taa_resolve.frag:109-118`: `currentDepth` is sampled through `ScreenSampler`, which is
`Filter.Linear` (`BindlessHeap.cs:285-313`), so it is a bilinear blend of non-linear reverse-Z
values. `previousDepth = historySample.a` is a bilinear blend of the *colour buffer's* alpha at
the reprojected UV — across an edge that is a blend of two unrelated depths. The tolerance
(`·0.002` of a raw reverse-Z value) is meaninglessly tight at distance. And because the
projection is jittered, the depth at a given pixel legitimately flips between foreground and
background across frames at silhouettes. `depthConsistent` therefore fails at edges →
`resolved = current` → the raw jittered edge is presented. This alone reproduces the symptom.

**3. The luma-delta feedback collapse defeats accumulation.**
`taa_resolve.frag:129-132` forces `feedback` to `TaaFeedbackMin` via
`smoothstep(0.04, 0.24, |luma(history) − luma(current)|)`. At an aliased edge that difference
*is* the signal TAA integrates. Neither reference has this term; both rely on neighbourhood
clipping, which already bounds the history.

**4. No velocity dilation.** Single tap at `taa_resolve.frag:57-60`. A silhouette pixel can
read the background's velocity while holding foreground colour. Prowl dilates over the
closest-depth 3×3 (`GetClosestMotionVector`).

**5. Per-channel `clamp` instead of AABB clip.** `taa_resolve.frag:101-107` clamps YCoCg
per channel, shifting chroma and over-rejecting. Flax uses `ClipToAABB`
(Pedersen 2016, `Temporal.hlsl`).

**6. Bilinear history resampling.** No Catmull-Rom, so repeated resampling compounds into
blur. Prowl's `SampleHistoryCatmullRom` is the standard fix.

**7. Jitter scaled by the wrong extent.** `VulkanRenderer.cs:4131-4132` divides by
`_swapchain.Extent`, but the scene renders into `_lastSceneRenderExtent`. Under dynamic
resolution scaling the jitter stops being a sub-pixel offset of the rendered image.

## Changes

### 1. Make motion vectors jitter-free (`GPUMotionVectorPushConstants` 208 → 224 B)

- `Njulf/Njulf.Rendering/Data/GPUStructs.cs:1861-1880` — append
  `public Vector4 TemporalJitterNdc;` after `PreviousCameraPosition` (xy = current NDC jitter,
  zw = previous; mirrors Flax's `Float4 TemporalAAJitter`). Appending keeps `vec4` 16-byte
  alignment; size becomes 224. `GPUFogPushConstants` is already 224 B, so the push-constant
  budget is proven.
- `Njulf/Njulf.Shaders/common.glsl:1529-1546` — mirror the field;
  `:1895` `SIZEOF_GPU_MOTION_VECTOR_PUSH_CONSTANTS` 208 → 224.
- `motion_vector.mesh:48`, `motion_vector_alpha.mesh:51`, `foliage_motion.mesh:45`,
  `foliage_grass_motion.mesh:53` — take the jitter as a parameter:

  ```glsl
  vec2 ClipToUv(vec4 clip, vec2 jitterNdc)
  {
      vec2 ndc = clip.xy / max(abs(clip.w), 0.000001);
      return (ndc - jitterNdc) * 0.5 + vec2(0.5);
  }
  ```

  Call sites pass `pc.Push.TemporalJitterNdc.xy` for current and `.zw` for previous
  (`motion_vector.mesh:120-123`, `motion_vector_alpha.mesh:133-136`,
  `foliage_motion.mesh:158-161`, `foliage_grass_motion.mesh:77-81`).
  `gl_Position` keeps the **jittered** `currentClip` — rasterisation must still match the
  depth prepass.
- `Njulf/Njulf.Rendering/Pipeline/MotionVectorPass.cs` — add
  `private Vector2 _previousJitterNdc;` next to `_previousViewProjectionMatrix` (`:34-40`);
  derive `Vector2 previousJitter = previousFrameValid ? _previousJitterNdc : currentJitter;`
  alongside `previousTime` (`:151-154`); latch
  `_previousJitterNdc = new Vector2(sceneData.JitterX, sceneData.JitterY);` next to
  `_previousViewProjectionMatrix = …` (`:324`); clear it wherever
  `_hasPreviousViewProjectionMatrix` is reset (`:740-746`). Set
  `TemporalJitterNdc = new Vector4(currentJitter, previousJitter)` at **all three** push
  sites (`:385`, `:545`, `:616`).

This also removes the same wobble from every other temporal consumer (hybrid reflections,
DDGI near-field C5, directional-shadow temporal, AMD shadow/reflection denoisers,
`temporal_surface_validity.comp`), none of which ever subtracted the jitter.

### 2. Rewrite `Njulf/Njulf.Shaders/taa_resolve.frag`

Delete: `Luma()`, `jitterVelocity`/`physicalVelocity`, the `historySample.a` depth
round-trip, `depthConsistent`, the `historyDelta` feedback collapse, and the per-channel
YCoCg `clamp`.

Structure:

- **One 3×3 loop** over the current frame collecting, in a single pass: RGB sum (for the
  sharpen reference), YCoCg min/max and first/second moments, and the **closest depth**.
  Depth is reverse-Z (`MeshPipeline.cs:5524` `CompareOp.GreaterOrEqual`), so closest is the
  **largest** value — the inverse of Prowl's comparison. Record `closestOffset`.
- **Sharpen** (Flax `TAA.shader`):
  `current += (current - neighborhoodAverage) * pc.TaaSharpness; current = max(current, 0.0);`
- **Dilated velocity**: sample `MOTION_VECTOR_TEXTURE_INDEX` at `inUv + closestOffset`;
  keep the existing NaN/Inf guard. Velocity is now purely geometric, so
  `historyUv = inUv - velocity` and the same vector feeds the motion ramp — no more
  raw/physical split.
- **History fetch**: `SampleHistoryCatmullRom` ported from Prowl (9 taps → 5 bilinear
  fetches, `max(result, 0.0)`) for RGB only; alpha read with a separate plain
  `textureLod`, since Catmull-Rom's negative lobes would corrupt a counter.
- **Variance AABB + `ClipToAabb`** (Flax `Temporal.hlsl`), with Prowl's motion-tightened
  gamma replacing the deleted luma hack:

  ```glsl
  float gamma   = mix(1.25, 0.75, motion);
  vec3  aabbMin = max(neighborhoodMinimum, firstMoment - standardDeviation * gamma);
  vec3  aabbMax = min(neighborhoodMaximum, firstMoment + standardDeviation * gamma);
  vec3  clippedHistory = YCoCgToRgb(ClipToAabb(RgbToYCoCg(historyColor), aabbMin, aabbMax));
  ```

- **Disocclusion, scale-free, no linearisation needed.** Reverse-Z depth is ≈ `near/z`, so a
  relative difference is already a view-space relative difference. Following Flax's shape
  (compare against the current depth buffer at the reprojected UV — there is no previous
  depth buffer in either engine), but soft rather than binary:

  ```glsl
  float historyDepth  = SampleDepthPoint(clamp(historyUv, vec2(0.0), vec2(1.0)));
  float depthRelative = abs(closestDepth - historyDepth) /
                        max(max(closestDepth, historyDepth), 1e-4);
  float disocclusion  = smoothstep(0.02, 0.08, depthRelative);
  ```

  Sky reads 0 on both sides → 0; sky-vs-geometry → 1. Depth must be point-sampled
  (`texelFetch`, or `textureLod` on integer texel centres) — never through the linear
  `ScreenSampler`.
- **History length in alpha** (alpha is free once the depth round-trip is gone). This
  replaces the hard `resolved = current` fallback, so a rejected pixel ramps in instead of
  popping:

  ```glsl
  float previousLength = historyValid ? clamp(historyAlpha, 1.0, 32.0) : 0.0;
  float currentLength  = min(previousLength * (1.0 - disocclusion) + 1.0, 32.0);
  feedback = min(feedback, 1.0 - 1.0 / currentLength);   // 0, 0.5, 0.67 … → feedback
  outHistory = vec4(resolved, currentLength);
  ```

  `historyValid` keeps `pc.TaaHistoryValid != 0u && historyUvValid && velocityFinite`.
- **Unchanged on purpose**: the velocity ramp
  (`smoothstep(0.25, max(0.5, pc.TaaVelocityRejectionScale), velocityPixels)`, then
  `mix(TaaFeedbackMax, TaaFeedbackMin, motion)`) and the 0.85/0.95 defaults. Flax's
  `StationaryBlending 0.95` / `MotionBlending 0.85` are the same values over an equivalent
  ~1 px ramp, so this is already correct and should not be churned.
- Debug view 5 now encodes the (already physical) `velocity`; views 6 and 7 keep working —
  view 6 keeps `pc.TaaCurrentJitterUv` in use.

### 3. `TaaSharpness` setting — **no ABI change needed**

`anti_aliasing_push.glsl:30` has a dead `uint TaaJitterPadding` whose only job is 8-aligning
the two trailing `vec2`s. Replace it in place with `float TaaSharpness`: same 4 bytes, so the
block stays **120 bytes** with `TaaCurrentJitterUv`/`TaaPreviousJitterUv` still at 104/112.
`GPUStructLayoutTests.cs:215` and `AntiAliasingRepairTests.cs:128-143` need no edit.

- `Njulf/Njulf.Shaders/anti_aliasing_push.glsl:30` — `uint TaaJitterPadding;` → `float TaaSharpness;`
- `Njulf/Njulf.Rendering/Data/GPUStructs.cs:3805-3834` — same rename/retype.
- `Njulf/Njulf.Rendering/Data/PostProcessingSettings.cs:199-257` — add
  `private float _taaSharpness = 0.1f;` (Flax's `TAA_Sharpness` default) and a
  `TaaSharpness` property clamped to `[0, 1]`, non-finite → `0.1f`, matching the
  `TaaVelocityRejectionScale` pattern at `:251-257`.
- `Njulf/Njulf.Rendering/Pipeline/AntiAliasingPass.cs:449-454` — add
  `TaaSharpness = _settings.AntiAliasing.TaaSharpness,`.
- `Njulf/RendererSettingsReference.md:588-601` — document it in the anti-aliasing table.
- `RenderingSettingsEditorPanelTests.cs:21` already reflects over `AntiAliasingSettings`, so
  the new property is picked up automatically.

### 4. Jitter extent + small correctness fixes

- `Njulf/Njulf.Rendering/VulkanRenderer.cs:4131-4132` — pass
  `_lastSceneRenderExtent.Width/Height` instead of `_swapchain.Extent.*`. That field is
  already in scope and used a few lines below at `:4172-4173`.
- `Njulf/Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs:1079-1085` — the
  AA pass samples `DEPTH_TEXTURE_INDEX` but declares no depth read. Add
  `ReadDepth(RenderGraphResourceId.SceneDepth)` (same helper used at `:274`, `:351`) so the
  graph emits the right barrier/layout.
- `Njulf/Njulf.Rendering/Pipeline/AntiAliasingPass.cs:458-467` — `ResetTaaHistory` clears
  four latches but not `_taaPreviousPostEffectRevision`; add it for symmetry.

### 5. Tests

`Njulf/Njulf.Tests/AntiAliasingRepairTests.cs:145-164`
(`TaaResolve_ReprojectsWithRawVelocityButRejectsWithPhysicalVelocityAndDepth`) pins the bug
as source text — it asserts `"vec2 historyUv = inUv - rawVelocity;"`,
`"vec2 physicalVelocity = rawVelocity - jitterVelocity;"` and
`"float previousDepth = historySample.a;"`. Replace it with a test pinning the new contract:

- `taa_resolve.frag` **contains** `"vec2 historyUv = inUv - velocity;"`, `"ClipToAabb("`,
  `"SampleHistoryCatmullRom("`, `"closestOffset"`, `"outHistory = vec4(resolved, currentLength);"`.
- `taa_resolve.frag` **does not contain** `"rawVelocity"`, `"jitterVelocity"`,
  `"historySample.a"`.
- `motion_vector.mesh` contains `"ClipToUv(currentClip, pc.Push.TemporalJitterNdc.xy)"` and
  `"ClipToUv(previousClip, pc.Push.TemporalJitterNdc.zw)"` — the jitter removal must not
  silently regress.
- Keep the three `AntiAliasingPass.cs` assertions as they are.

Also add `TaaSharpness == 0.1f` plus its clamp behaviour to
`TaaDefaultsFavorStableHistoryAndRejectInvalidInputs` (`:108-126`).

`GPUStructLayoutTests.cs:89` derives the motion-vector size from `Marshal.SizeOf`, so the
208 → 224 change is covered once `common.glsl:1895` is updated. `HaltonJitter_IsExactlyZeroMeanAcrossEachSupportedCycle`
(`:81-106`) and `AntiAliasingExtent_MatchesTheSelectedQuality` (`:45-60`) are untouched by
this work and must stay green.

### 6. Optional — `AntiAliasingDebugView.TaaHistoryLength = 8`

Visualising `currentLength / 32` and `disocclusion` is the fastest way to confirm the fix
(a converged still frame should go uniformly white except at moving silhouettes). Touches
`PostProcessingSettings.cs:181-191`, a `pc.DebugView == 8u` branch in `taa_resolve.frag`, and
the debug-view list in `RendererSettingsReference.md:615-624`.

## Non-goals

- **TAA stays post-tonemap on LDR.** `Plans/Complete/Phase8AntiAliasingPlan.md:426-462`
  argues for an HDR resolve before bloom (as Flax does), but that means new pass ordering
  through `ValidatePassOrder`, HDR history targets and reworked sRGB ownership. Resolving on
  display-referred colour is inherently more stable and needs no firefly weighting, so this
  fix keeps it and the deviation from the plan doc stands.
- **Skinned deformation still produces no velocity.** `motion_vector.mesh:112-115` applies the
  current-pose vertex to both the current and previous *rigid* matrices, so an animating
  character in place reads ~zero motion. Only foliage reads a true previous pose
  (`foliage_motion.mesh:135-153`). Separate fix.
- **Sky, particles, transparents and decals write `(0,0)`**, i.e. "static", not camera
  velocity. Neighbourhood clipping covers most of it; a proper fix is a separate change.
- **Render-scale awareness.** `TaaHistoryA/B` and the TAA render area are swapchain-sized
  while `LdrSceneColor`/`MotionVectors` follow the scene render extent
  (`RenderTargetManager.cs:1181-1188`). `SmaaQualityPresetRedoPlan.md:113` says to keep it
  that way; change 4 only fixes the jitter scale, not the resolve-time rescale.
- The camera-only reprojection paths (`hybrid_reflection_temporal.comp`,
  `ddgi_near_field_residual_temporal.comp`) still reconstruct from jittered
  `previousViewProjection` / `PreviousHiZViewProjection` matrices. Out of scope.

## Verification

1. `dotnet build Njulf/Njulf.sln` — compiles every shader through
   `Njulf.ShaderBuild/CompileNjulfShaderArtifacts`; a GLSL/C# push-constant mismatch fails here.
2. `dotnet test Njulf/Njulf.Tests` — must pass `AntiAliasingRepairTests`,
   `GPUStructLayoutTests` (both size constants), `MotionVectorCameraReprojectionTests`
   (TAA must still force authored velocity), `DirectionalShadowContractsTests`, and
   `ShaderEffectGpuTests` (GPU smoke over `None`/`SmaaMedium`/`Taa`, zero validation errors).

## Branch

Work on `Simplified-SDF` (already checked out, clean). Commit and
`git push -u origin Simplified-SDF`.