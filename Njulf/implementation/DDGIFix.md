# Global SDF at Simplified HEAD (018c886): analysis of this round + remaining fixes

## Context

Commit `018c886` implemented the previous plan's two fixes: a min-filter mip
reduction (`global_sdf_mip_reduce.comp`) replacing the average blit, and the
distance-proportional trace epsilon. The mechanics of both landed, but the mip
pass broke the cascade textures' image-layout/descriptor contract, the debug
view's epsilon slope is too small to fix the floor rings, and the diagnostics
captured in a GI debug view are misleading (large groups legitimately read
zero there). This plan covers what to fix now, ranked.

## Reading this round's evidence correctly (important, saves wasted work)

1. **The three screenshots are three different debug views**, cycled via
   Ctrl+V: the flat green/blue one is the *surface-cache card projection*
   view (flat colors are expected there — it is NOT a broken SDF slice); the
   ringed/speckled one is the *GlobalSdfSlice*; the red/blue/green/black
   patchwork is the *DdgiRayBackendHeatmap*.
2. **The zeroed DDGI diagnostics are debug-view noise, not a regression.**
   `ddgiClipmapCoverage`, `ddgiEstimate`, `ddgiSampledProbeUse`,
   `ddgiFastGather`, trace `diagSamples`, blend diagnostics and
   `ddgiProbeConfidence` all read 0 in both dumps — but both dumps were taken
   with `debug=DdgiRayBackendHeatmap` active. The GI debug views (forward
   frame-ids 120–122, see `Njulf/implementation/DDGI-SDF.md`) replace the
   forward lighting path, so the forward gather/estimate counters never
   accumulate, and the diag-sample gating tied to the forward frame-id stops
   sampling the trace/blend energy counters. Meanwhile the counters that ARE
   written unconditionally (`sdfTraces`, `rayQueryTraces`, `cacheFallback%`,
   scheduler/update CPU counts) are all alive and healthy.
   **Action:** capture diagnostics with `debug=None` when judging DDGI
   health; optionally (low priority) exempt the diagnostics gating from
   debug-view frame-ids so triage lines stay meaningful while debugging.
3. `forwardUs=5103` (~5 ms forward pass) is the debug raymarch itself —
   full-screen 128-step SDF marching per pixel. It is not a product-path
   regression; it disappears with `debug=None`. (`gpuDdgiUs≈2900` is still
   over budget — tracked separately below.)
4. Verified sound in `018c886` (do not redo): min-filter reduce shader math,
   LOD clamp ≤ 3 in both `ClampGlobalSdfLod` and `SelectGlobalSdfTraceLod`,
   epsilon-slope plumbing through `TraceGlobalSdfCascadeSegment`, DDGI slope
   constant 0.08, debug maxSteps 96→128, per-mip storage-image bindless
   indices (allocator starts at `FirstDynamicTextureIndex`, no collision;
   shared free list makes `FreeTextureIndex` on storage indices legal; heap
   is UPDATE_AFTER_BIND).

## Bug F — mip pass breaks the cascade texture layout/descriptor contract (fix first)

`GenerateMinMipChain` (`Njulf.Rendering/Pipeline/GlobalSdfPasses.cs`,
added in `018c886`):

- **Leaves the cascade volumes in `ImageLayout.General` permanently.** The
  pre-existing contract (every consumer + the creation-time descriptor in
  `GlobalSdfManager.EnsureResources`) is `ShaderReadOnlyOptimal` at
  end-of-pass. There is no final `TransitionToShaderRead` anymore.
- **Rewrites the sampled-texture descriptor every frame at record time**
  (`_bindlessHeap.RegisterTexture(..., ImageLayout.General)`) to paper over
  that. Update-after-bind makes this legal-ish, but it races the in-flight
  previous frame and only covers volumes touched *this* frame — any path that
  re-registers with `ShaderReadOnlyOptimal` (e.g. `EnsureResources` on
  resolution change) reintroduces a layout/descriptor mismatch → undefined
  sampling.
- **The final barrier only covers compute** (`DstStageMask=ComputeShaderBit`),
  so the forward fragment shader (all three SDF debug views) reads mip data
  with no visibility guarantee — a real sync bug that can account for the
  lingering speckle/dark-patch artifacts in the slice view.

### Fix F (all in `GlobalSdfPasses.cs` + `global_sdf_mip_reduce.comp`)

1. Change `global_sdf_mip_reduce.comp` to read the source mip via
   **`imageLoad` on a storage descriptor** instead of `texelFetch` on the
   sampled full-chain view: pass `SourceStorageImageIndex =
   MipStorageImageIndices[mip-1]` (mip 0's index is the cascade's existing
   storage index) and use `imageSize()` instead of `textureSize()`. The whole
   reduce then runs purely on storage descriptors in `General` layout.
2. Delete **both** `RegisterTexture` calls from `GenerateMinMipChain` — the
   sampled descriptor stays registered once at creation with
   `ShaderReadOnlyOptimal` and is never rewritten at record time.
3. After the mip loop, call `volume.TransitionToShaderRead(cmd)` — this
   restores the layout contract and its dst stages
   (`FragmentShader|ComputeShader`) provide the missing fragment-stage
   visibility in one barrier.
4. Optional tidy: scope the per-iteration barrier to the two mips involved
   instead of all levels.

## Bug G — floor rings persist because the debug epsilon slope is too small

`GLOBAL_SDF_DEBUG_TRACE_EPSILON_SLOPE = 0.002` (`forward.frag`): at 20 m
that's a 4 cm threshold — below the grazing-convergence scale, so floor rays
still exhaust 128 steps and miss (the concentric rings in the slice view).
Raise to **0.01–0.02** (at 20 m → 20–40 cm ≈ the far-cascade voxel size).
Debug-view-only knob; DDGI's 0.08 slope is fine.

## Perf note (not a code change this round)

`hybridSdfSplitUs mips≈340 µs` for the min-mip pass and `gpuDdgiUs≈2.9 ms`
overall. After F+G land and the user re-measures with `debug=None`, judge
whether the mip pass needs to skip untouched cascades (it already only runs
on touched volumes) or reduce to LOD ≤ 3 generation only (levels > 3 are
never sampled anymore — generating them is pure waste; stopping the loop at
mip 3 is a free ~2× win on the mip pass and also shrinks
`ValidateFormatSupport` requirements).
Actually include that: **stop the mip loop at mip 3** since the trace clamps
LOD ≤ 3 (`min(maxMip, 3u)`), and drop the now-unneeded
`BlitSrc|BlitDst|SampledImageFilterLinear` format requirements added for the
old blit path in `VolumeTexture.ValidateFormatSupport` if nothing else blits
these volumes.

## Verification

1. Rebuild, run with `debug=None`, capture the triage lines — the previously
   "zeroed" DDGI groups should be populated again (confirming the debug-view
   explanation) and `hybridFinalLum`/`effective` should be judged from that
   capture only.
2. `GlobalSdfSlice` view: floor solid to each cascade's range (no rings), no
   speckle on props, no flat immediate-hit sheets; strafe to confirm no
   ring-seam artifacts appear while bricks stream.
3. `forwardUs` back to normal with `debug=None`; `hybridSdfSplitUs mips`
   roughly halves with the mip-3 cutoff.
4. Tests: update `ShaderBuildTests.cs` string assertions for the
   `imageLoad`-based reduce shader; no C# math changes otherwise.