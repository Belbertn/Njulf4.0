# Scene-Compaction Specialization Experiment

## Summary

Split `scene_opaque_compact.comp` into compile-time flat-meshlet and instance-expansion/hierarchy programs. Measure specialization and counter-readback costs independently, then report the results without recommending retention or reversion.

## Implementation Changes

- Generate eight SPIR-V artifacts: flat/instance × resolved/virtual addressing × diagnostics on/off. Do not emit the unspecialized mega-kernel artifact.
- Compile hierarchy traversal, instance expansion, shared traversal state, and control barriers only into the instance program. Keep flat opaque/depth/shadow processing only in the flat program.
- Pre-create both programs for the selected addressing mode and bind using the existing frame-global `instanceExpansion` decision. Preserve descriptor layouts, push constants, buffers, flags, and output semantics.
- Add build property `NjulfSceneCompactionCounterReadback`, defaulting to `true`. When disabled, omit only the device-to-host counter copy, its host barrier, and associated transfer visibility; retain reset, shader-consumer, payload, indirect-command, and LOD-history barriers. Force readback on during submission-validation mode.
- Add `GpuSceneOpaqueCompactionMicroseconds` and the effective readback state to renderer diagnostics and benchmark output. No GPU ABI changes.

## Minimal Verification

- Fresh `ShippingPerformance` shader compilation followed by `spirv-val` for all eight artifacts.
- Focused tests verifying artifact selection, absence of the unspecialized artifact, and that the flat SPIR-V has no workgroup variables/control barriers while the instance artifact retains them.
- One short Vulkan-validation smoke covering flat mode, hierarchical instance mode, and the readback-disabled build.
- Use the workload that produced the reported 1.47–1.77 ms result; if unavailable, use the repository's stationary 1920×1080 Bistro workload with hierarchical instance expansion active.

## Performance Measurement

Run three comparable captures on the same GPU, driver, camera, settings, and warmed caches, with VSync, validation, and diagnostic counters disabled:

1. Current mega-kernel, readback enabled.
2. Specialized programs, readback enabled.
3. Specialized programs, readback disabled.

Use 30 warm-up and 120 measured frames per capture. Compare image output against capture 1 with the existing 0.5% relative-RMSE limit. Record:

- Scene-compaction and whole-frame median/p95 GPU time.
- Absolute and percentage deltas for specialization (1→2) and readback ablation (2→3).
- Registers, shared memory, and occupancy from one profiler capture of configurations 1 and 2.
- Selected program, readback state, overflow state, artifact size/count, and pipeline creation activity.

If a delta is within observed run variation, report it as inconclusive at this quick-loop resolution. Report all measurements and skipped broader testing, with no keep/revert decision or automatic rollback.
