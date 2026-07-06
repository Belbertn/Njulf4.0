# Global SDF Stability — Round 2 Analysis & Fix Plan

## Context

Round-1 fixes (conservative `safeBound` clamp, probe-scroll dirtying removed, priority-reserved budgets, cell-solve-first hits) all landed in `bcec7f1` and clearly worked: moving-frame backlog dropped 28,549 → 408, idle churn gone. Remaining symptoms: dithered band across floors at mid distance, chewed/wiggly silhouettes on objects close to the camera, and residual instability while moving. New snapshots show `GlobalSdfAverageTraceSteps` ~34 (was ~7–11), a moving frame with 408 plain-dirty bricks but **0 priority bricks**, and a GPU frame that wrote 1024/1024 bricks all-empty.

## Findings (ranked)

### 1. `ddaCells` never resets — grazing rays exhaust and miss (the dither band + chewed silhouettes; stationary too)

`Njulf/Njulf.Shaders/global_sdf.glsl`, `TraceGlobalSdfCascadeSegment`: `ddaCells` is initialized once (line ~336) and incremented in the near-band cell branch, but **never reset when the ray re-enters the coarse phase**. It is a lifetime cap of `GLOBAL_SDF_DDA_MAX_CELLS = 32` cells across the whole segment. A ray at grazing incidence to a floor repeatedly dips in and out of the ±1.5-voxel near band, accumulates 32 cells, then `break`s → returned as a **miss** (`ddaExhausted`) even though `maxSteps` budget remains.

- The dithered band across the floor is the marginal zone where rays alternate between hitting and exhausting — it scintillates as the camera moves because sample phase shifts every frame.
- Objects close to the camera present many silhouette-grazing rays → same exhaustion dither = the chewed door-frame/statue edges.
- The conservative clamp made this worse: distances now cap at ~4–8 voxels, so rays take more steps overall (avg 34) and reach the DDA cap sooner.
- It reproduces stationary (snapshot 084114: backlog 0, artifacts still present) — confirming it's trace quality, not staleness.

**Fix:** make the cap per-near-band-episode, not per-segment: reset `ddaCells = 0` in the coarse branch (when `|d| > 1.5·voxel`). Optionally, on `ddaExhausted`, continue the loop in coarse mode instead of `break` while `steps < maxSteps`. `maxSteps` remains the real cost bound.

### 2. Probe-volume scrolls still re-bake the SDF via a second path (movement churn, again)

Round 1 removed `DirtyProbeRequests`, but `VulkanRenderer.CollectDdgiDirtyRegions` (~line 6210) does:

```csharp
if (volumeSignature != _lastDdgiProbeVolumeSignature)
    AddDdgiDirtyRegion(EstimateSceneProbeBounds(scene), 4.0f, DdgiDirtyReason.StreamIn);
```

The DDGI probe volumes are camera-relative, so their signature changes whenever any probe cascade scrolls — i.e. constantly during movement — and this dirties the **entire scene bounds + 4m**, which `ApplyDdgiEvents` → `MarkDirtyWorldBounds` applies to all 4 SDF cascades as plain (non-priority) dirty bricks. Light changes (`DirectionalLightChanged` dirties the whole scene) and VFX/emissive changes flow through the same path. These are all *radiance* events — probes must re-trace, but the SDF is geometry-only and doesn't change.

This explains the moving snapshot exactly: 408 plain-dirty / 0 priority bricks, and full-budget frames writing 1024/1024 **empty** bricks (whole-scene bounds are mostly air; the plain-dirty drain uses a linear physical-index scan, not nearest-first, so it grinds through empty periphery bricks).

**Fix:** filter by reason in `GlobalSdfManager.ApplyDdgiEvents` (`GlobalSdfManager.cs`, DirtyRegions loop): only apply regions whose `DdgiDirtyReason` is geometry-related (`GeometryAdded`, `GeometryRemoved`, object-transform changes / `Unknown` from `WithDirtyBounds`). Skip `StreamIn`, `DirectionalLightChanged`, `LocalLightChanged`, `EmissiveChanged`. `DdgiDirtyRegion` already carries the reason — no plumbing needed.

### 3. Empty bricks still consume the full update budget (slow convergence when moving toward new areas)

After the round-1 change, an empty brick's content is a pure function of voxel-in-brick position (`safeBound` = distance-to-brick-surface + padding) — **identical for every empty brick in a cascade**. Yet scroll slabs of sky/air are dispatched as full 512-thread workgroup bakes and consume budget, competing with the geometry bricks the player is moving toward ("freaks out when I move towards a room").

**Fix (CPU-side skip):** in `GlobalSdfManager`, keep a per-physical-brick `holdsEmptyPattern` flag. Each frame, build a per-cascade occupancy test from the ~40 mesh-SDF instance bounds (cheap AABB tests at selection time, padded by the same 4 voxels + 1 voxel mesh-cull expansion the shader uses — keep them consistent). When selecting a dirty brick: if occupancy says empty *and* `holdsEmptyPattern` is already true, consume the dirty flag without dispatching or spending budget; otherwise dispatch and update the flag (set true when occupancy-empty, false otherwise). Since the empty pattern is logical-cell-independent, scrolled-in empty bricks over already-empty physical bricks become free. Add a diagnostic counter (e.g. `GlobalSdfEmptyBrickSkippedCount`) to verify.

### 4. Minor / follow-ups

- **Debug view differs from production handoff**: `GlobalSdfRaymarchDebugColor` (`forward.frag` ~2902) restarts each cascade trace from `t = 0` with 128 steps each, while the production DDGI path continues at `segment.T`. After fix #1, if band artifacts persist only in the debug view, consider making the debug view use the production cross-cascade helper so it shows what DDGI actually sees.
- **Average trace steps ~34**: expected consequence of the conservative clamp. If it grows problematic, the candidate-gather padding (currently 4 voxels) is the tunable; a coarse mip/occupancy skip is the long-term fix. Don't tune this until fixes 1–3 are measured.

## Files to modify

- `Njulf/Njulf.Shaders/global_sdf.glsl` — fix #1 (reset `ddaCells` in coarse branch; optional continue-instead-of-break on exhaustion)
- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` — fix #2 (reason filter in `ApplyDdgiEvents`), fix #3 (empty-pattern skip in `SelectDirtyBrickJobs`/job selection)
- `Njulf/Njulf.Rendering/Pipeline/GlobalSdfPasses.cs` + diagnostics plumbing — new skip counter for fix #3
- `Njulf/Njulf.Tests/GlobalSdfManagerTests.cs` — cover reason filtering and empty-skip accounting

## Verification

1. `dotnet build` + run `GlobalSdfManagerTests` / shader build tests.
2. Re-capture snapshots:
   - Stationary near the door: the dithered floor band and chewed silhouettes should be gone (fix #1) — this reproduces without any updates running, so it isolates the shader fix.
   - Moving: `GlobalSdfDirtyBrickBacklog` should stay near the scroll-slab size with priority bricks nonzero and plain-dirty ≈ 0 (fix #2); `GlobalSdfBricksWrittenEmptyCount` should collapse in favor of the new skip counter (fix #3).
   - Watch `GlobalSdfAverageTraceSteps`: should drop somewhat once grazing rays stop burning the DDA cap; `DdgiSdfStepExhaustedCount` should stay near zero.
3. Fly toward the room repeatedly: geometry ahead should resolve within a frame or two of entering the cascade window.