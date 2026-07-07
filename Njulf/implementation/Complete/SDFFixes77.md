# Global SDF Stability — Round 5 (final): Remove the Empty-Skip, Characterize the Residual Turn Artifact

## Context

Step-1 experiment result: with `ShouldSkipEmptyDirtyBrick` disabled (everything else at `106c8d3`), the SDF is "way better" — world alignment holds through 180° turns (confirmed by the latest snapshot: clean geometry at yaw ≈ 180°, `EmptyBrickSkippedCount: 0`, zero step-exhausted rays). The empty-skip chain is therefore **confirmed as the misalignment/grey-screen bug**. A minor imperfection remains when turning around.

## Decision: delete the skip permanently

Cost/benefit is unambiguous. GPU timings from earlier snapshots put a full 1024-brick bake at ~0.4 ms (`GpuGlobalSdfBrickMicroseconds: 399`); a 576-brick scroll slab is ~0.2 ms. The skip existed to save that — sub-millisecond, on scroll frames only — and its CPU-side content-tracking (`_holdsEmptyPattern`) is exactly what corrupted the field. The original problem it addressed (90% of budget wasted on empty bricks) was actually solved by the round-2 churn fixes (radiance-event filtering) and priority budget reservation; the skip was redundant belt-and-braces that turned out to have a hole in the belt.

### Changes (all in `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` + tests)

1. Remove `ShouldSkipEmptyDirtyBrick`, `IsBrickEmpty`, `GetPhysicalBrickCandidateBounds`, `_holdsEmptyPattern`, `PhysicalBrickHoldsEmptyPattern`/`SetPhysicalBrickEmptyPattern`, and the `meshSdfInstanceBounds` parameter threading (`PrepareUpdateJobs` signature, `GlobalSdfPasses.cs` call site, `MeshSdfManager.ActiveInstanceBounds` if now unused).
2. Keep the **batched nearest selection** and **scroll hysteresis** from `106c8d3` — both are audited-correct and independently valuable (they fixed the stutter and boundary thrash).
3. Selection loops simplify back to: select batch → `AddJob` each (no skip branch). Restore plain-dirty run coalescing (`ConsumeDirtyRun(start, remaining)`) if desired — it was only reduced to single bricks to accommodate the skip.
4. Keep the `GlobalSdfEmptyBrickSkippedCount` diagnostic field but hard-set to 0, or remove it from the snapshot payload — either, for less churn prefer removal.
5. Tests: delete the skip-protocol tests; keep/extend batching + hysteresis tests.

## Residual: "not perfect when turning around" — characterize before fixing

The catastrophic bug is gone; what remains is minor and needs evidence, not another speculative fix. Known candidate mechanisms, in likelihood order:

1. **Cascade handoff pop**: turning to face geometry you're moving away from demotes it fine→coarse across the visible scene (noted in round 3; inherent clipmap LOD behavior). Fix if confirmed: overlap/fade band at cascade boundaries in the trace + debug view.
2. **Hysteresis off-center window right after reversal**: for the first ~1.25 bricks of backtravel the window lags opposite to motion — slightly less fine-cascade coverage ahead. Cosmetic; tune `ScrollHysteresisBrickFraction` down (0.25 → 0.1) if implicated.
3. Grazing-angle trace dither on newly-faced surfaces (round-2 residual, view-dependent, not a state bug).

**Procedure:** reproduce the turn imperfection and capture 2–3 snapshots at the moment it's visible (one mid-turn, one just after, one settled), as before. Diagnose from the pair of png+json — specifically whether the artifact is at a fixed world distance (cascade boundary ⇒ #1), clears after ~1 brick of movement (⇒ #2), or shimmers per-frame at glancing angles (⇒ #3).

## Verification

1. Build + `GlobalSdfManagerTests`.
2. Full repro suite: forward sprint, 180° turn, return, orbit, several minutes soak — alignment holds everywhere, no grey-out, stutter stays gone, scroll-frame GPU cost stays sub-millisecond (`GpuGlobalSdfBrickMicroseconds`).
3. New snapshots of the residual turn artifact for the follow-up diagnosis.