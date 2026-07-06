# Global SDF Stability — Round 5: Isolate the Rotation/Misalignment Bug by Experiment

## Context

Baseline: HEAD back at `106c8d3` (batched selection + hysteresis; stutter gone). Two hardening attempts were tried and reverted:
- **A. ForceEmptyPattern + perpetual idle sweep** (SDFFixes55): rotation unfixed, forward movement worse.
- **B. Render-graph read declarations / barriers** (SDFFixes56): rotation unfixed, "worse in general".

## What the failed experiments prove

1. **B failing kills the GPU queue-race theory** as the cause of the rotation misalignment. (Whether the declarations are still *correct* hygiene is separate — but they don't fix this bug, and since they made things worse they should stay reverted until this is solved.)
2. **A failing is directional evidence about the empty-skip chain.** ForceEmptyPattern makes the GPU write whatever the CPU's occupancy verdict says. If that verdict is ever wrong ("empty" for a brick that has geometry), A *erases real geometry* where round-3 merely *kept stale content*. A being distinctly worse in forward movement is exactly that signature.
3. **Timeline correlation:** the misalignment/rotation symptom was first reported at `b6e2a63` — the commit that *introduced* the empty-skip (`ShouldSkipEmptyDirtyBrick`, `_holdsEmptyPattern`). Rounds 1–2 (no skip) had trace-quality issues but never world misalignment.

All static analysis of the skip chain (mapping math, bounds, padding, batch accounting) has come back clean repeatedly — so stop reading and start measuring. The defect is either a subtle CPU-side case the reading keeps missing, or GPU-side (job constants vs. shader write path).

## Plan: two decisive experiments, then the targeted fix

### Step 1 — One-line experiment: disable the skip, keep everything else

In `GlobalSdfManager.ShouldSkipEmptyDirtyBrick`, return `false` unconditionally (or gate the skip behind a debug setting). Everything else at `106c8d3` stays. Then run the repro: move forward ~10 m, rotate 180°, move back; orbit; soak a few minutes.

- **If rotation misalignment disappears** → the skip chain is definitively guilty; go to Step 2 to find the exact defect. (Cost of running without the skip meanwhile: scroll frames re-bake ~576 mostly-empty bricks — the pre-`b6e2a63` behavior, which was stable.)
- **If it persists** → the skip is exonerated; the bug is in scroll/selection/GPU-write, and Step 2's simulation will catch it anyway.

### Step 2 — CPU simulation test that replays the failure (no GPU needed)

Add a test to `GlobalSdfManagerTests` that drives `GlobalSdfManager.PrepareUpdateJobs` frame-by-frame with:
- a synthetic mesh-bounds set resembling the scene (walls/floor boxes straddling the origin),
- a scripted camera path: forward 10–15 m in small steps (crossing several brick boundaries on multiple axes), 180° turn (position unchanged), then back past the start; include a diagonal leg.

Maintain a reference model alongside: for every physical brick of every cascade, track the logical cell it was last baked for (from the emitted jobs' `BrickStartIndex`/constants) or "holds empty pattern" (from skips). After every simulated frame, assert the invariant: **every physical brick whose *current* logical cell intersects the padded mesh bounds was last baked for that exact logical cell; every skipped brick's current cell is occupancy-empty AND its last write was the empty pattern.**

This deterministically catches any dropped brick, wrong skip, stale-flag, or mapping inconsistency — the exact class of bug we've been unable to pin by reading. When it fails, the failing frame + brick index pinpoints the defect; fix it, keep the test.

### Step 3 — If (and only if) Steps 1–2 exonerate the CPU entirely

Then the divergence is GPU-side (push constants vs. shader write/decode, or execution). Add a debug validation: after baking a brick, also write its logical-cell hash into the candidate-history buffer slot (already exists, already read back for diagnostics); CPU cross-checks the hash against its expectation and logs mismatches with brick coordinates. That turns the GPU side into measurable state instead of guesswork.

## Files to modify

- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` — skip kill-switch (Step 1); whatever the simulation reveals (Step 2)
- `Njulf/Njulf.Tests/GlobalSdfManagerTests.cs` — the replay/invariant test (Step 2)
- (Step 3 only) `global_sdf_update.comp` + diagnostics readback for logical-cell hash validation

## Verification

1. Step 1 result reported (skip on/off vs. rotation repro) — this alone tells us which half of the system is guilty.
2. Step 2 test red on the current code (reproducing the defect) → fix → green, and green across forward/reverse/diagonal paths.
3. In-game repro clean with the skip re-enabled; `GlobalSdfEmptyBrickSkippedCount` still high; no misalignment after repeated reversals; no grey-out over a soak.