# Global SDF Stability — Round 3: Stutter While Moving + Instability After Reversing

## Context

Rounds 1–2 landed (`b6e2a63`): conservative distances, radiance-event filtering, empty-brick skip, DDA-episode reset. Snapshots confirm the machinery is healthy — scroll frames show exactly `invalidated = priority-updated + empty-skipped`, zero empty writes, zero plain-dirty churn. Two symptoms remain: **stuttering while moving** and **loss of stability after moving forward then rotating ~180° to go back**. The post-rotation snapshot shows 3 cascades scrolling in a single frame (1728 priority bricks) and speckle around mid-distance objects.

## Findings

### 1. O(N²) brick selection on scroll frames — the stutter (confirmed)

The round-2 change to `SelectDirtyBrickJobs` (`GlobalSdfManager.cs:536+`) selects bricks **one at a time**: `SelectNearestPriorityDirtyBricks(camera, maxCount: 1, …)` inside a `while` loop, once per brick (including every empty-skipped brick). Each call runs `SelectNearestBricks` (`GlobalSdfManager.cs:1217`), which **scans all 13,824 bricks of the cascade** computing brick centers and camera distances.

- Normal cascade0 scroll frame: 576 selections × 13,824-brick scan ≈ 8M iterations.
- The post-rotation snapshot's 3-slab frame: 1,728 × 13,824 ≈ **24M iterations with a Vector3 distance each** — tens of ms of CPU on exactly the frames where you cross a brick boundary. That is the movement stutter, and it's worst on direction reversal (see #2).
- Degenerate case: after a full invalidation, the full-dirty path does the same per-brick loop → 13,824 × 13,824 per cascade.

**Fix:** batch the selection. Call `SelectNearestPriorityDirtyBricks(camera, maxCount: remaining, …)` once, iterate the returned list applying `ShouldSkipEmptyDirtyBrick` per brick (skips don't consume `remaining`), and loop for another batch only while `remaining > 0 && cascade.HasPriorityDirtyBricks`. Same semantics (nearest-first, skips free), but one or two O(N) scans per cascade per frame instead of one per brick. Apply the same to the full-dirty nearest path. The plain-dirty `FindNextDirtyBrick` path is already O(1) amortized per brick — leave it.

### 2. Direction reversal recrosses every cascade boundary at once + no scroll hysteresis

`UpdateClipmap` derives the grid min by pure flooring of the camera position (`CalculateCenteredGridMinimum`). Two consequences:

- When you move forward you cross cascade 0/1/2 scroll boundaries at staggered times. When you **reverse**, you are sitting just past all the boundaries you most recently crossed, so within the first ~1 brick of backward travel every cascade rescrolls — the snapshot caught 3 slabs (1,728 bricks) in one frame. Combined with #1 that's the biggest possible hitch, right at the moment of turning around.
- Hovering or oscillating at a boundary (strafing, rotating in place with slight positional drift) flips the floor result back and forth — each flip invalidates and re-bakes a 576-brick slab both ways. Pure waste and shimmer.

**Fix:** add a dead-band (hysteresis) to `GlobalSdfCascadeRuntime.UpdateClipmap` (`GlobalSdfManager.cs:~745`): only adopt `nextGridMin` when the camera has penetrated ≥ some fraction (e.g. 0.25–0.5) of a brick beyond the switch point for that axis; otherwise keep the current window. This both spreads reversal rescrolls over distance and eliminates boundary thrash. Keep the existing full-`MarkAllDirty` path for ≥ whole-window jumps. Unit-test: camera oscillating ±0.1 bricks around a boundary must produce zero scrolls.

### 3. Async-compute frame coherency — suspected cause of the residual per-scroll flicker (verify first)

`GlobalSdfPass` runs on the async compute queue when `DdgiAsyncComputeEnabled` (default **true**, `VulkanRenderer.cs:4649`). The cascade metadata upload and brick bakes are recorded on that queue, but the forward pass (which renders the SDF debug views) runs on the graphics queue. If the graph pipelines async compute (graphics consumes last frame's compute output), then on every scroll frame the graphics queue can see **new ring-offset metadata with one-frame-old brick content** — exactly a one-frame window of garbage in freshly scrolled slabs, i.e. flicker that only appears while moving.

**Verify before fixing:** run with `DdgiAsyncComputeEnabled = false` and compare movement stability. If flicker disappears, fix by making the SDF cascade metadata read by graphics-queue consumers match the brick content they can actually see (simplest: keep `GlobalSdfPass` off the async queue; better: double-buffer the cascade metadata so metadata and texture content flip together).

### 4. Noted, lower priority

- The speckle around mid-distance objects in the post-rotation screenshot sits in the cascade0→1 handoff zone (~12m). Moving *away* from geometry demotes it fine→coarse across the visible scene (moving toward it, promotion happens ahead of you) — inherent clipmap LOD pop, worth a fade/overlap band across the handoff later, not now.
- `GlobalSdfAverageTraceSteps` ~40: acceptable; revisit with a coarse-skip only if GPU trace time becomes a problem.

## Files to modify

- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` — batch selection in `SelectDirtyBrickJobs` (#1), scroll hysteresis in `UpdateClipmap` (#2)
- `Njulf/Njulf.Tests/GlobalSdfManagerTests.cs` — batch-selection semantics (nearest-first + skip accounting unchanged), hysteresis oscillation test
- (#3, only if verified) `Njulf/Njulf.Rendering/VulkanRenderer.cs` / `GlobalSdfPasses.cs` — queue placement or metadata double-buffering

## Verification

1. `dotnet build` + `GlobalSdfManagerTests`.
2. Stutter: fly forward continuously — frame time on scroll frames should no longer spike (compare before/after; the 3-slab reversal frame is the stress case). CPU-side, `PrepareUpdateJobs` should be ~O(bricksPerCascade) per frame.
3. Reversal: move forward 10m, rotate 180°, move back — no burst of scroll invalidations at the turn (hysteresis defers them), no frame hitch, SDF debug view stays stable.
4. Async experiment: toggle `DdgiAsyncComputeEnabled` off; if movement flicker changes, implement fix #3 and re-verify with it back on.
5. Counters to watch in snapshots: `GlobalSdfScrollInvalidatedBricks` should be 0 while oscillating in place; `GlobalSdfBrickUpdateBudget` spikes only on genuine scroll frames.