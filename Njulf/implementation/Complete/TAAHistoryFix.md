# Temporal-history defect sweep — Njulf4.0 `Simplified-SDF`

## Context

Diagnosing the TAA shimmer established two invariants this codebase violates in more than one
place:

1. **A bindless descriptor slot must never be re-pointed per frame.** `BindlessHeap` owns a
   single `_textureSamplerSet` (`BindlessHeap.cs:354`) that `RegisterTextureLocked` updates
   inline with `vkUpdateDescriptorSets` (`:543`). With `FramesInFlight = 2`
   (`RenderingConstants.cs:7`), re-pointing a slot while an earlier frame is still executing
   makes that frame sample the wrong image. Binding flags are
   `UpdateAfterBindBit | PartiallyBoundBit` with no `UpdateUnusedWhilePendingBit`
   (`BindlessHeap.cs:57-59`), and `UPDATE_AFTER_BIND` is precisely what stops the validation
   layers tracking per-descriptor use by pending submissions — which is why both perf captures
   show `ValidationErrorMessageCount = 0` while the bug is live. **Validation cannot detect
   this class. Only a runtime assertion can.**
2. **A temporal history gate must not compare jittered camera state.** With TAA jitter on, the
   projection matrix changes every frame by design; any history check that treats that as "the
   camera changed" disables itself permanently.

This plan fixes the two remaining instances of (1) and the one instance of (2). It assumes the
TAA history fix (register both banks once, pass the read index in the push block) lands first
or alongside — it is the same edit shape as Finding 1.

## Findings

### Finding 1 — Hybrid reflection opaque scene-color snapshot (invariant 1) — P1

`HybridReflectionVulkanRuntime.cs:1117-1135`:

```csharp
int bank = ValidateFrameIndex(frameIndex);
RenderTarget snapshot = Required(HistoryTarget(1 - bank), "opaque SceneColor snapshot");
...
_bindlessHeap.RegisterTexture(
    BindlessIndex.OpaqueSceneColorSnapshotTexture, snapshot.View, ...);
```

The view alternates with `bank` every frame and is written into one shared slot — the exact
TAA defect. It is worse in one respect: the only consumer is
`forward_surface_shading.glsl:3615` (slot 298, `common.glsl:505`), and the **forward pass runs
earlier in the frame than this registration**. Descriptor writes are not command-buffer
ordered — they hit the set at CPU record time and the GPU reads whatever is there at
execution — so the forward pass samples whichever bank the CPU last published, which may be
the one currently being written. `VulkanRenderer.cs:12594-12598` also publishes this slot
(to `DefaultBlackTexture`), making it a two-writer slot as well.

**Fix.** Same shape as the TAA fix, and the same shape the codebase already uses correctly for
the environment double buffer (`BindlessIndexTable.PrefilteredEnvironmentNextTexture` +
`EnvironmentManager.cs:740-748`):

- `BindlessIndexTable.cs` — append `OpaqueSceneColorSnapshotTextureB` at the **end** of the
  `+ 1` chain (after `GtaoDebugTexture`, `:880`). Never insert mid-chain: the table is
  mirrored as hard-coded integers in `common.glsl:468-510`, so an insertion shifts every later
  slot. Add the mirrored GLSL constant.
- Register **both** history banks once, from `OnTargetsRecreated` (`HybridReflectionVulkanRuntime.cs:1144`)
  and wherever the init-time publication happens, never from a `Record`/`Execute` path.
- Publish the resolved index per frame as data, not as a descriptor write: add
  `OpaqueSceneColorSnapshotTextureIndex` next to the existing
  `sceneData.OpaqueSceneColorSnapshotAvailable` flag (`:1142`), carry it into the GPU scene
  struct the forward shader already reads, and index by it at
  `forward_surface_shading.glsl:3615` instead of the fixed constant.
- Delete the `RegisterTexture` call at `:1131`.

Registering both banks permanently means one descriptor records `ShaderReadOnlyOptimal` while
its image sits in another layout for part of the frame. That is legal and standard: Vulkan
only requires the recorded layout to match for descriptors the shader actually **accesses**,
and `PartiallyBoundBit` is already set.

### Finding 2 — GTAO temporal history is permanently dead under TAA (invariant 2) — P1

`GtaoPasses.cs:37`, inside `GtaoHistoryState.CanReuse`:

```csharp
_projection.Equals(sceneData.ProjectionMatrix) &&
```

`sceneData.ProjectionMatrix` is the **jittered** projection:
`SceneDataBuilder.cs:593` builds it via `ApplyProjectionJitter(camera.ProjectionMatrix, projectionJitter)`
and `:1023` publishes that same matrix. With TAA on, the Halton jitter shears it every frame,
so the equality is false on every frame and `GtaoHistoryValid` is pinned at 0 forever.

Confirmed by both captures: `AmbientOcclusionEnabled = 1`, `AmbientOcclusionMode = Gtao`,
`AntiAliasingMode = Taa`, `JitterEnabled = 1` → `GtaoHistoryValid = 0`. This is almost
certainly the "crisscross pattern" from `8f93578` — a GTAO temporal filter that never
accumulates leaves the raw spatial sampling pattern uncovered.

This is the **only** history gate in the codebase comparing a camera matrix against
`sceneData.ProjectionMatrix` (verified by sweep), so it is a single-site fix.

**Fix.** Compare the unjittered projection. `SceneDataBuilder` already has it in hand —
`camera.ProjectionMatrix` at `:593`, and it already keeps an unjittered derivative for
transparency at `:595-596`.

- `SceneRenderingData` — add `UnjitteredProjectionMatrix`; set it at `SceneDataBuilder.cs:1023`
  from `camera.ProjectionMatrix` (pre-jitter).
- `GtaoPasses.cs:37` and the matching `Commit` at `:38-42` — compare and store
  `sceneData.UnjitteredProjectionMatrix`.

This mirrors both reference engines, which keep exactly this field for exactly this reason:
Flax's `RenderView::NonJitteredProjection` (`Source/Engine/Graphics/RenderView.cpp:20`) and
Prowl's `Camera.NonJitteredProjectionMatrix` (`TAAEffect.cs`, `OnPreCull`).

While here, verify `gtao_temporal.comp` samples the motion-vector texture by **UV**, not by
texel: GTAO runs at `AmbientOcclusionResolutionScale = 0.5` (800×450 in the captures) while
motion vectors are full-res (1600×900). Not a claimed bug — a check that must pass before
GTAO history means anything.

### Finding 3 — `AmbientOcclusionBlurredTexture` has two writers (invariant 1, latent) — P2

`AmbientOcclusionBlurPass.cs:103` and `:107` publish the slot with either
`AmbientOcclusionRaw.View` or `AmbientOcclusionBlurred.View` depending on
`ao.BlurRadius == 0 && !requiresFullResolutionResolve`, and `GtaoPasses.cs:675` publishes the
same slot again from its own `EnsureBindings`. Today the blur pass's choice is settings-driven
rather than frame-alternating, so `RequiresPublication` dedups it to a no-op and nothing races
— but two passes own one slot, and animating `BlurRadius` turns it into a live instance of
Finding 1.

**Fix.** Give the slot a single owner: publish it from `VulkanRenderer.RegisterAmbientOcclusionTextures`
driven by the effective AO mode, drop the publication from `GtaoPasses.cs:675`, and gate the
blur pass's publication behind a dirty flag in the style of
`ReflectionProbeManager.cs:639-648` (`_descriptorDirty`) so it cannot fire per frame.

### Not a bug — volumetric fog

I flagged `VolumetricFogHistoryValid = 0` earlier as suspicious. It is not: both captures show
`FogEnabled = 0`, `GpuVolumetricFogTemporalMicroseconds = 0` and every froxel counter at zero.
The pass is not running. `FroxelFogRenderer.cs:373` sets the flag from `!cameraCut` only, with
no matrix comparison. Nothing to fix.

### Verified clean

`SimpleDdgiSampledAtlas.cs:318-323` (fixed base + group index), `SpotShadowAtlas.cs:173-176`
and `PointShadowCubemapArray.cs:196-198` (stable working view, fallback only when absent),
`ReflectionProbeManager.cs:639-648` (dirty-gated), `EnvironmentManager.cs:740-748` (dedicated
second slot), `PointShadowPool.cs:57/77` and `TextureManager.cs` (per-resource slots).
`GtaoComputePassBase` is also already correct on the descriptor side: `RewriteDescriptors`
pre-writes **two** sets (`GtaoPasses.cs:721-760`) and `BindAndPush(cmd, writeIndex, …)` selects
between them rather than mutating one — this and `PrefilteredEnvironmentNextTexture` are the
two in-repo precedents the fixes above should follow.

## Guard so this class cannot come back

Validation will never catch it, so add the detector to `BindlessHeap.RegisterTextureLocked`
(`BindlessHeap.cs:496-545`): record the frame serial alongside `_publishedTextures[i]`, and in
`Development`/debug builds fail loudly when a slot is republished to a **different** identity
more than once within one frame serial, or republished at all after frame recording has begun.
That signature is exactly what all three findings produce, and it would have caught the TAA bug
in one frame instead of three rounds.

Surface the counters too: `GetPublicationMetrics()` (`BindlessHeap.cs:548`) already tracks
`DesiredChanges` / `NoOpRegistrations` / `ActualWrites`. Publishing `ActualWrites` per frame in
the diagnostics payload makes "a descriptor is being rewritten every frame" visible in any
capture — in steady state it should be ~0.

## Verification

1. `dotnet build Njulf/Njulf.sln`, then `dotnet test Njulf/Njulf.Tests` — `GtaoImplementationTests`,
   `GPUStructLayoutTests`, `ShaderEffectGpuTests`, and any bindless index mirror test.
2. **Finding 2 pass/fail gate:** run with `AntiAliasing.Mode = Taa` and `AmbientOcclusion.Mode = Gtao`,
   capture the performance JSON, and confirm `GtaoHistoryValid = 1` in steady state. It is `0`
   today in both captures — this single number is the whole test. The crisscross pattern from
   `8f93578` should go with it.
3. **Finding 1:** exercise the transparency/refraction path that samples the opaque snapshot
   under camera motion; look for frame-alternating flicker. `HybridReflectionHistoryValid` is
   already `1`, so it should stay `1`.
4. With the new guard active, run a few thousand frames with TAA + GTAO + reflections enabled
   and confirm it never trips.
5. Re-run the sweep to confirm nothing new appeared:
   `grep -rn "RegisterTexture(" Njulf/Njulf.Rendering --include=*.cs` — every remaining hit
   must be init/resize/dirty-gated, never a per-frame `Record`/`Execute` path with an
   alternating view.
