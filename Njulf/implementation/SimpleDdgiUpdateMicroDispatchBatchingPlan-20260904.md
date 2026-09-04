# Batch Simple-DDGI Update Micro-dispatches

## Summary

Reduce GPU submission overhead in update-active Sponza frames while preserving DDGI budgets, ordering, output, and urgent/ordinary transaction behavior. Optimize the schedule and commit passes, then report measured impact without making a keep/revert recommendation.

## Implementation Changes

- Capture one fresh baseline from the current worktree before editing.
- Schedule pass:
  - Drive downstream stages through indirect commands sized from actual candidate/accepted counts; leave commands at zero when no work exists.
  - Give generic and tail admission separate slots and arm exactly one.
  - Fuse materialization and emit classification into one per-update kernel.
  - Size emit/scatter work from accepted updates instead of request capacity.
  - Retain barriers only where one stage consumes globally produced data.
- Commit pass:
  - Replace scroll validation, local commit, and propagation dispatches with one 64-lane workgroup per active volume/ring.
  - Within each group: validate its outcomes, synchronize, apply local commits, synchronize, then propagate within that volume.
  - Preserve rejection, all-or-defer, generation, and counter semantics; use zero indirect groups when nothing was accepted.
- Feedback reduction:
  - Keep the required partial-to-final dispatch boundary.
  - Parallelize fixed summary and cursor publication across the final group's lanes, publishing the completion header last.
- Update the internal C#/GLSL dispatch-slot ABI, shader manifest, pipeline arrays, barriers, and substage timing labels together. Keep the public pass timing names and existing readback contract unchanged.

## Minimal Verification

- Freshly compile every changed shader and run SPIR-V validation.
- Add one focused batching test with empty and hand-authored multi-volume/ring workloads, verifying zero-work suppression, exclusive admission selection, lossless outcome partitioning, commit/propagation ordering, scroll gates, and feedback totals.
- Run one 12-frame Sponza smoke with Vulkan synchronization validation enabled.
- Capture one matched post-change Nsight GPU trace using the same update trigger, camera, settings, resolution, build configuration, clocks, warm-up, and validation state as the baseline.

## Performance Report

Report before, after, absolute delta, and percentage delta for:

- Whole GPU frame and total Simple-DDGI time.
- `SimpleDdgiSchedulePass` and `SimpleDdgiSchedulerCommitPass`.
- Hardware dispatch counts and compute occupancy for both passes.
- Active volumes/rings, considered/eligible/accepted probes, emitted rays, and urgent/ordinary transaction counts.

Flag the comparison as inconclusive if workloads differ or pipeline compilation contaminates either capture. Leave the implementation in place and report only the observed magnitude and direction—no keep/revert threshold or recommendation.

## Assumptions

- The current dirty worktree is the authoritative baseline; unrelated changes, including the extended feedback-readback call, remain untouched.
- One matched trace pair is a directional impact check, not a release-grade performance campaign.
- No DDGI quality settings, scheduling budgets, async behavior, or public APIs change.
