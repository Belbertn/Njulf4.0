# Fix GTAO-induced image softness

## Context

Switching `AmbientOcclusion.Mode` from `Ssao` to `Gtao` makes the whole rendered
image look soft. SSAO does not do this.

The AO *consumer* is identical in both modes — `SampleScreenSpaceAoDirect()`
(`Njulf.Shaders/forward_surface_shading.glsl:1380-1391`) does one
`textureLod(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX, uv, 0.0).r`, and both paths
write the same full-res R8_UNORM `AmbientOcclusionBlurred` target with the same
`ScreenSampler`. No mip bias, no jitter change, no shared depth pyramid, no TAA
history collision. So the softness is produced inside the GTAO chain itself and
by one GTAO-exclusive consumer.

Two root causes, in order of impact:

**1. The bent normal replaces the normal-mapped shading normal.**
`TryResolveIndirectDiffuseNormal()`
(`forward_surface_shading.glsl:1415-1446`) ends with:

```glsl
resolvedNormal = normalize(mix(shadingNormal, worldBentNormal,
    smoothstep(0.0, 0.25, hemisphere)));
```

`smoothstep(0.0, 0.25, hemisphere)` saturates to `1.0` whenever
`dot(bent, shading) > 0.25` — virtually every lit pixel. So `shadingNormal` is
not blended toward the bent normal, it is **discarded**. The replacement is a
half-resolution, depth-reconstructed *geometric* normal
(`gtao.comp:330` → `ReconstructGeometricNormal`, `gtao.comp:140-199`) that has
been temporally accumulated and spatially blurred, and carries zero normal-map
detail. It then drives environment diffuse irradiance
(`forward.frag:1919-1925`, `:1977`, `:1992`, `:2100`, `:2270`) and, at Ultra,
the DDGI lobe (`forward.frag:1555-1556`, `:2003-2012`). Every high-frequency
detail in the ambient term is erased.

This is on by default: `RenderSettings.cs:211-221` sets `Mode = Gtao` for every
preset above Low and `BentNormalMode = EnvironmentOnly` (High) /
`EnvironmentAndDdgi` (Ultra). `AmbientOcclusionSettings.cs:110-113` gates
`EffectiveBentNormalMode` on `Mode == Gtao`, so flipping SSAO → GTAO silently
turns bent normals on.

**2. The GTAO spatial pass does not upsample — it smears.**
GTAO traces at half res (`ResolutionScale = 0.5`, `AmbientOcclusionSettings.cs:57`;
`GtaoRaw` half-res RGBA16F, `RenderTargetManager.cs:264-269`) and reconstructs to
full res inside `gtao_spatial.comp`. That reconstruction is broken in two ways:

- `ResolveSourceCoordinate()` (`gtao_spatial.comp:69-78`) is a **nearest** map —
  `floor((out + 0.5) * src / out)` — with no fractional weight. Every full-res
  pixel inside a half-res texel resolves to the same source texel.
- The bilateral edge-stopping reference `centerGeometry`
  (`gtao_spatial.comp:219-224`) is the **half-res** packed geometry texel. The
  shader never reads full-res `SceneDepth` at all, so it cannot tell where the
  real depth edges are.

The mismatched-extent branch (`:262-296`) then spreads each output pixel over a
3×3 half-res kernel via `CombinedAxisGaussianWeight` (`:115-136`) — roughly a
6×6 full-res footprint — with `Radius` **hardcoded to `2u`**
(`GtaoPasses.cs:694`), ignoring `AmbientOcclusion.BlurRadius` entirely.

SSAO does the opposite: `ResolveDepthAwareAo()`
(`ambient_occlusion_blur.comp:128-196`) takes its center depth from the
**full-res** `SceneDepth` at the output UV (`:133-138`), keeps
`sourceFraction = fract(sourcePosition)` for a true bilinear reconstruct in X
(`:139-141`, `:174-176`), and weights every tap against full-res depth
(`:162-172`). And SSAO's blur is skippable — `BlurRadius == 0` bypasses the pass
outright (`AmbientOcclusionBlurPass.cs:101-105`).

**3. (contributing) Temporal over-accumulation.** `StableHistoryWeight = 0.92`
clamped to `0.95`, `MaximumHistoryAge = 32` (`GtaoPasses.cs:548-554`), with
history refetched through a LINEAR sampler every frame
(`gtao_temporal.comp:253`) — a bilinear convolution compounded up to 32 times.
The bent normal is blended with history (`:302-303`) with **no** neighbourhood
clamp; only visibility gets one (`:291`). Motion is sampled bilinearly at
half-res UVs from a full-res buffer (`:233`) and, unlike `taa_resolve.frag:65-66`,
is never de-jittered before `motionPixels` (`:297`).

### What Prowl does differently

Reference: `ProwlEngine/Prowl` → `Prowl.Runtime/Assets/Defaults/GTAO.shader`.

- **No bent normal at all.** Prowl's GTAO emits a scalar AO
  (`Pass "CalculateGTAO"`, `aoOutput = vec4(ao, ao, ao, 1.0)`) and `Pass "Composite"`
  just multiplies it into colour. It never touches the shading normal — which is
  why it cannot cause cause #1.
- **Its blur is keyed on full-res depth.** `Pass "Blur"` linearizes
  `_CameraDepthTexture` at the *output* `TexCoords` and weights each tap by
  `abs(centerDepth - sampleDepth) / centerDepth` — the same shape Njulf's SSAO
  upsample uses, and what Njulf's GTAO spatial pass is missing.
- **Its downsampled depth is point-sampled on purpose.** `Pass "DownsampleDepth"`
  uses `texelFetch`, with the comment "averaging two depths yields a position on
  no actual surface".
- **Its temporal is cheap and bounded**: 3×3 neighbourhood clamp of history
  against the current AO, history dropped on off-screen reprojection, single
  `_TResponse` — no 32-frame age, no unclamped normal accumulation.
- It feeds GTAO the real G-buffer view normal (`_CameraNormalsTexture`). Njulf is
  forward+ with no G-buffer, so depth reconstruction stays — but that is exactly
  why the resulting bent normal must not be trusted as a shading normal.

## Plan

### Step 1 — Apply the bent normal as a bounded rotation, not a replacement

Rewrite `TryResolveIndirectDiffuseNormal()` in
`Njulf.Shaders/forward_surface_shading.glsl:1415-1446` to treat the GTAO payload
as a *delta* from the surface's geometric normal, and apply that delta to the
fragment's own shading normal:

1. Change the signature to
   `bool TryResolveIndirectDiffuseNormal(vec3 shadingNormal, vec3 geometricNormal, out vec3 resolvedNormal)`.
2. Decode and transform the bent normal to world space exactly as today
   (`:1426-1439`), keeping the NaN/inf and `payload.w <= 0.0` guards.
3. Derive the occlusion bend as the rotation from `geometricNormal` to
   `worldBentNormal`: `axis = cross(geometricNormal, worldBentNormal)`,
   `angle = acos(clamp(dot(...), -1.0, 1.0))`. Bail to `false` when
   `length(axis)` is below ~1e-5 (parallel ⇒ no bend).
4. Scale and clamp: `angle = min(angle, maxBendAngle) * confidence`, where
   `confidence = payload.w` and `maxBendAngle` is a constant (~45°, i.e.
   `0.7854`). Optionally also scale by `(1.0 - payload.z)` so unoccluded pixels
   get no bend at all.
5. Apply that rotation to `shadingNormal` with Rodrigues' formula around
   `normalize(axis)`, normalize, and reject (return `false`, leaving
   `resolvedNormal = shadingNormal`) if `dot(result, shadingNormal) <= 0.0`.

Because the result is anchored on `shadingNormal` and perturbed by a small
bounded rotation, per-pixel normal-map detail passes straight through — which is
what the current `mix()` destroys.

Update the single caller at `Njulf.Shaders/forward.frag:1552-1554` to pass
`geometricNormal` (already in scope, see `forward.frag:979`). Behaviour of
`bentNormalValid` and the `ForwardAmbientOcclusionBentNormalMode() == 2u` branch
at `:1555-1556` is unchanged.

`Njulf.Tests/GtaoImplementationTests.cs:329` only asserts the substring
`"TryResolveIndirectDiffuseNormal("` is present — still satisfied, but re-check
after editing.

### Step 2 — Give `gtao_spatial.comp` a real depth-aware upsample

**CPU side — `Njulf.Rendering/Pipeline/GtaoPasses.cs`:**

- Add `Binding(6, DescriptorType.CombinedImageSampler)` to the `GtaoSpatialPass`
  layout (`:645-652`).
- In `RewriteDescriptors()` (`:705-738`), add a
  `GtaoImageDescriptor(6, CombinedImageSampler, _renderTargets.SceneDepth.View,
  _bindlessHeap.ScreenSampler, ImageLayout.DepthStencilReadOnlyOptimal)` —
  copy the pattern already used by `GtaoPass` at `:469-474`.
- In `Execute()` (`:686-696`): stop hardcoding `Radius = 2u` (`:694`); take it
  from `_settings.AmbientOcclusion.BlurRadius`, clamped to `GTAO_MAX_RADIUS`, so
  GTAO's blur is tunable the way SSAO's is. Add the inverse projection matrix to
  the push block.
- `Njulf.Rendering/Data/GPUStructs.cs:3917-3925` — add
  `public Matrix4x4 InverseProjectionMatrix;` as the **first** field of
  `GPUGtaoSpatialPushConstants` (matching `AmbientOcclusionBlurPushBlock`
  ordering in `ambient_occlusion_blur.comp:21-30`). 24 → 88 bytes; the GTAO main
  pass already pushes ~180 bytes, so the budget is fine. Source the matrix the
  same way `GtaoPass` does (`GtaoPasses.cs:440-458`).
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs:379-390` — add
  `ReadComputeDepth(RenderGraphResourceId.SceneDepth)` to the `Pass("GtaoSpatialPass", ...)`
  declaration, as `Pass("GtaoPass", ...)` has at `:360`.

**Shader side — `Njulf.Shaders/gtao_spatial.comp`:**

- Add `layout(set = 0, binding = 6) uniform sampler2D DepthTexture;` and
  `mat4 InverseProjectionMatrix` to the push block (`:8-23`).
- Port `FetchDepth(vec2 uv)` and `ReconstructViewDepth(vec2 uv, float depth)`
  verbatim from `ambient_occlusion_blur.comp:44-57` (both are small and
  self-contained; `MulRowMajor` is already available via `common.glsl`).
- Replace the mismatched-extent branch (`:262-296`) with a bilinear-anchored
  joint bilateral, mirroring `ResolveDepthAwareAo`
  (`ambient_occlusion_blur.comp:128-196`):
  - `outputUv = (vec2(pixel) + 0.5) / OutputDimensions`;
    `centerViewDepth = ReconstructViewDepth(outputUv, FetchDepth(outputUv))` —
    **full-res** depth at the output pixel. This is the key change: the edge
    reference must come from the full-res buffer, not the half-res payload.
  - `sourcePosition = outputUv * vec2(sourceExtent) - 0.5`;
    `sourceBase = ivec2(floor(sourcePosition))`;
    `sourceFraction = fract(sourcePosition)`.
  - Iterate `sourceBase + ivec2(x, y)` over `x,y ∈ [0..1]` expanded by `radius`,
    weighting each tap by bilinear(`sourceFraction`) × `Gaussian(depthDifference,
    depthSigma)` computed against `centerViewDepth` × the existing
    `normalAgreement` term from `AccumulateSharedSample` (`:150-183`).
  - Keep the existing rejection guards (`sampleGeometry.w < 0.0`,
    `normalAgreement < 0.50`, `dot(bentNormal, centerGeometry.xyz) <= 0.0`) and
    the existing fallback to `centerPayload` when `weightSum` underflows
    (`:297-302`).
- **Widen the shared-memory preload.** `sharedSourceOrigin` / `sharedSourceEnd`
  (`:192-200`) are computed through the nearest map; `floor(uv * src - 0.5)` can
  reach one texel *before* it. Extend the preload span by one texel on each axis
  and confirm the result still fits `GTAO_SHARED_STRIDE`
  (`GTAO_GROUP_SIZE + GTAO_MAX_RADIUS * 2 = 12`), raising `GTAO_MAX_RADIUS` or
  the stride if it does not. Getting this wrong reads uninitialised shared memory.
- Leave the equal-extent branch (`:240-261`) alone — it is already correct.

### Step 3 — Tighten the temporal pass

Smaller, but it compounds both causes above.

- `Njulf.Shaders/gtao_temporal.comp:233` — fetch `MotionTexture` with
  `texelFetch` at the corresponding full-res pixel instead of a bilinear
  `textureLod` at half-res UVs. Averaging velocity across a silhouette
  mis-reprojects, exactly the reason Prowl point-samples its downsampled depth.
- `gtao_temporal.comp:297` — subtract the TAA jitter delta from `motion` before
  `motionPixels`, mirroring `taa_resolve.frag:65-66`. Today a static camera under
  TAA injects ~1px of phantom motion per frame.
- `gtao_temporal.comp:302-303` — clamp the reprojected bent normal against the
  3×3 current-frame neighbourhood the way `previous.z` already is at `:291`,
  or reject history when `dot(previousBent, currentBent)` falls below the
  existing 0.20 gate more aggressively.
- `GtaoPasses.cs:548-554` — reduce `MaximumHistoryAge` (32 → ~8) and
  `StableHistoryWeight` (0.92 → ~0.85) so bilinear history resampling compounds
  far fewer times.

## Files to modify

| File | Change |
|---|---|
| `Njulf/Njulf.Shaders/forward_surface_shading.glsl` | Step 1 — bounded-rotation bent normal (`:1415-1446`) |
| `Njulf/Njulf.Shaders/forward.frag` | Step 1 — pass `geometricNormal` at `:1552` |
| `Njulf/Njulf.Shaders/gtao_spatial.comp` | Step 2 — depth binding, full-res bilateral upsample, shared-tile span |
| `Njulf/Njulf.Rendering/Pipeline/GtaoPasses.cs` | Step 2 — binding 6, descriptor, push matrix, `Radius` from settings; Step 3 — temporal constants |
| `Njulf/Njulf.Rendering/Data/GPUStructs.cs` | Step 2 — `GPUGtaoSpatialPushConstants.InverseProjectionMatrix` |
| `Njulf/Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs` | Step 2 — `ReadComputeDepth(SceneDepth)` on `GtaoSpatialPass` |
| `Njulf/Njulf.Shaders/gtao_temporal.comp` | Step 3 — point-sampled motion, de-jitter, bent-normal clamp |

Reuse rather than reimplement: `ResolveDepthAwareAo`, `FetchDepth` and
`ReconstructViewDepth` from `ambient_occlusion_blur.comp:44-57, 128-196`;
`Gaussian`, `AccumulateSharedSample`, `DecodeOctahedral`, `EncodeOctahedral`
already in `gtao_spatial.comp`; the `SceneDepth` descriptor pattern from
`GtaoPasses.cs:469-474`.

## Verification

1. **Build shaders and run the suite.**
   `dotnet test Njulf/Njulf.Tests` — `GtaoImplementationTests`,
   `AmbientOcclusionShaderContractTests`, `GPUStructLayoutTests` and
   `ScenePipelineManifestTests` all touch this area.
   `GtaoImplementationTests.cs:323` pins `Pass("GtaoSpatialPass"` in the graph
   text and `:329` pins `TryResolveIndirectDiffuseNormal(` — both should still
   pass; update the struct-layout expectations if any assert the old 24-byte
   push size.


All work on branch `Simplified-SDF`.