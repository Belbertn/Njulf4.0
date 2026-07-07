# Global SDF — Localize the Scroll/Rebake Corruption (experiment-driven)

## Context

User testing has now isolated the failure precisely:
- **Pure rotation (camera fixed) does NOT break it.** Rotation causes no clipmap scroll, so the baked volume already contains the right data in all directions.
- **Only translation breaks it**, in newly-entered regions.
- **After a round trip, the spawn area is broken too** — previously-correct bricks become corrupted.

Together this means the defect is in the **incremental scroll → invalidate → rebake path**, and it *persistently corrupts bricks* (not a transient). It is NOT a representation/thin-feature problem — that whole line (SDFFix99 / commit `6cdff10` `SampleMeshSdfPreservingThinFeatures` + `TryIntersectTrilinearNearMinimum`) was treating the wrong cause and introduced the flat-surface "acne", sealed openings, 7–16% surface-cache fallback (vs <1%), and 2.6–4.6 ms brick cost. Those must be reverted.

## Verified CORRECT — stop re-investigating these

I traced the full write↔read chain; all of the following are self-consistent and are NOT the bug:
- Toroidal addressing. Writer (`global_sdf_update.comp`): physical brick `P` stores content for logical cell `gridMin + (P − ringOffset) mod N`. Both readers (`SampleGlobalSdfCascade` hardware-linear path and `GlobalSdfLogicalVoxelToPhysicalTexel` texelFetch path in `global_sdf.glsl`) map a world position back to physical `(logical + ringOffset) mod N`. gridMin and ringOffset always advance by the **same delta** (`GlobalSdfManager.cs:808-822`), so overlap (non-frontier) bricks keep the same logical assignment and remain addressable; only the frontier slab changes and is marked dirty. Confirmed algebraically for ringOffset=0 and ≠0.
- All GPU consumers apply ringOffset: `ddgi_update_shared.glsl:2142/2194`, `forward.frag` — no consumer reads the cascade texture with a stale/zero offset. The FullSlice debug view uses the same trace as DDGI, so it faithfully shows what DDGI samples.
- Cascade-buffer field offsets (RingOffset at words 15/16/17) match the struct on both C# and GLSL sides.
- Job/metadata ordering: `PrepareUpdateJobs` runs scroll → `BuildCascadeMetadata` → `SelectDirtyBrickJobs`, all on the same post-scroll state.
- Dirty selection/consume accounting is balanced (`SelectNearestBricks` consumes exactly what it emits jobs for; priority budget floor covers the whole invalidated slab in one frame). No consume-without-bake.
- WorldMin is always a whole-brick multiple → no fractional voxel-grid drift across scrolls.

## Step 1 — Revert the mis-targeted content changes

In `global_sdf_update.comp`: delete `SampleMeshSdfPreservingThinFeatures`, restore the two candidate loops to `min(distanceMeters, SampleMeshSdf(...))`. In `global_sdf.glsl`: delete `TryIntersectTrilinearNearMinimum` and its call site. Keep the `LocalToWorldAxisScale` rename and the per-cascade/rolling diagnostics from `aceeb7d`. Update `ShaderBuildTests` accordingly. This returns to a clean baseline so the scroll bug is observed in isolation.

## Step 2 — Two decisive experiments (temporary debug toggles in `RenderSettings.GlobalIllumination`)

Both keep everything else identical and are read via push-constant/manager flags.

**Experiment A — `ForceFullSdfRebakeOnScroll`:** in `GlobalSdfCascadeRuntime.UpdateClipmap`, when a non-teleport scroll occurs, call `MarkAllDirty()` instead of `InvalidateChangedPhysicalBricks(...)` (ringOffset/gridMin still advance normally).
- Artifacts vanish ⇒ the incremental **invalidation set is wrong** (some frontier bricks that need rebake aren't marked, so they read stale) — focus on `InvalidateChangedPhysicalBricks` and `GetLogicalCellForPhysicalBrick`.
- Artifacts persist ⇒ the bricks that *are* rebaked come out wrong ⇒ go to Experiment B.

**Experiment B — `DisableToroidalScroll`:** force `RingOffset` to stay `Zero`; on any gridMin change, `MarkAllDirty()` and lay out physical == logical.
- Artifacts vanish ⇒ the defect is specifically **toroidal (ringOffset≠0) execution** even though the addressing math is algebraically correct — prime suspects then are GPU-side: (a) image write→read visibility across the multi-dispatch loop in `GlobalSdfPasses.Execute` (the per-job `TransitionToStorageReadWrite` / end-of-pass `TransitionToShaderRead` and whether consecutive small dispatches to the same volume need an image barrier), (b) storage-image view (`OutputTextureIndex`) vs sampled view (`TextureIndex`) aliasing the same memory, (c) the hardware-linear `SampleGlobalSdfCascade` Repeat seam at the wrap plane.
- Artifacts persist even here ⇒ the bug is independent of scrolling entirely and the earlier isolation needs re-checking with the user.

## Step 3 — Instrumentation to make the desync visible

- Add an SDF debug view that colors each traced sample by its **physical brick index** (or `baked-frame-index` written into a side channel): a mismatch between where content was baked and where it's read will show as a visible discontinuity exactly at the scroll frontier.
- Add a cheap CPU check: after `SelectDirtyBrickJobs`, assert every physical brick whose `GetLogicalCellForPhysicalBrick` changed this frame is either dirty or was emitted as a job; log any that slip through.

## Verification

1. Build + `dotnet test` (`GlobalSdfManagerTests`, `ShaderBuildTests`, `RendererDiagnosticsTests`).
2. Repro: from spawn, walk out ~10 m on one axis and back; capture a `GlobalSdfFullSlice` snapshot at spawn. Fix is confirmed when spawn stays clean after roaming, newly-entered areas show no phantom/holes, `DdgiSurfaceCacheFallbackPercent` is back under ~1%, and `GpuGlobalSdfBrickMicrosecondsRollingMax` is back near ~0.5 ms.
3. Run with Experiment A then B toggles to record which one restores correctness; that result names the fix site. Remove the toggles once the real fix lands.
4. Push to `Simplified`.