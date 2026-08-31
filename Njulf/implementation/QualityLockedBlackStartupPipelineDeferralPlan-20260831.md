# Eliminate the Multi-Minute Black Startup

## Summary

- Treat the observed run as the baseline, not an improvement measurement. Pipeline-cache timestamps show:
  - `ddgi_simple_receiver_cache_b1.comp.spv`: about **600.8 seconds**
  - Adaptive receiver-cache pipeline set: another **22.8 seconds**
- These pipelines are created during the first production command recording, blocking the render thread while the bootstrap continues presenting black frames.
- Make the first production frame use the existing exact canonical DDGI path. Receiver-feedback caching and opaque specializations are output-equivalent optimizations and must never gate visible content.

## Implementation Changes

- Remove every pipeline-creation path from receiver-feedback command recording:
  - `TryBeginSimpleDdgiReceiverFeedbackCapture` only uses an already-published pipeline bank.
  - Adaptive dispatches never call resource or pipeline `Ensure` methods that can invoke Vulkan pipeline creation.
  - Allocate inexpensive buffers, layouts, and descriptors during graph initialization; defer shader-pipeline creation.
- After the first successful full-quality scene present, compile the complete receiver-feedback/adaptive bank on the existing bounded pipeline worker.
  - Build into local ownership and publish one immutable bank with `Volatile.Write` only after all handles are valid.
  - Read the bank once per frame with `Volatile.Read`.
  - Until ready—or permanently after failure—skip feedback collection and retain exact canonical shading.
  - Dispose incomplete handles on failure and allow unrelated post-first-present specializations to continue.
  - Blocking/exhaustive startup modes continue compiling the complete bank synchronously.
- Add a targeted function-preserving shader recipe:
  - Apply trailing `-Od` to exact B1/receiver-attribution compute and graphics variants, including adaptive B1 and missing-receiver variants.
  - Keep ordinary shaders on `-Os`.
  - This follows the existing forward ray-query workaround. Static probes reduced B1 compute from one 2,863-label function to 140 functions/998 labels, and transparent B1 from 1.73 MB to 812 KB.
  - Retain this recipe only if native creation time improves by at least 50% and steady-state GPU p95 does not regress by more than 2% or 0.2 ms, whichever is larger. The scheduling fix remains regardless.
- Make bootstrap progress observable without adding a loading graphics pipeline:
  - Use a subtle animated dark clear while the frame remains pipeline-free.
  - Show elapsed startup time, completed pipeline count, and the oldest active pipeline basename in the window title.
  - Emit a throttled heartbeat after two seconds, then every ten seconds.
  - Restore the normal title on the first full-quality scene present.

## Interfaces and State

- Retain and complete the already-started `RendererStartupSnapshot` additions:
  - `ActivePipelineCount`
  - `OldestActivePipelineMicroseconds`
  - `ActivePipelineSummary`
- Include these fields in startup JSONL diagnostics and test their thread-safe aggregation.
- Keep receiver-bank readiness internal; introduce no new CLI options or rendering-quality setting.
- Document the invariant: render-critical pipeline creation must remain zero, and receiver-feedback readiness may affect performance only—not final image quality.

## Verification

- Build `Development`, run targeted tests, and validate every affected artifact with `spirv-val`.
- Add tests proving:
  - Affected artifacts retain multiple `OpFunction` records and receive the intended compile option.
  - Command-recording paths cannot create pipelines.
  - Frames use exact fallback before publication, the optimized path afterward, and fallback after compilation failure.
  - Partial banks are never observable and shutdown cannot race publication.
- Run controlled startup comparisons with both Vulkan cache directories redirected to isolated temporary directories:
  1. Empty-cache cold run.
  2. Warm rerun using the produced caches.
  3. Qualified-seed run where applicable.
- Use startup telemetry—not RenderDoc—for timing. Required gates:
  - First non-uniform full-quality scene is presented before receiver-feedback compilation begins.
  - Zero render-critical pipeline creations.
  - No first-frame draw stall attributable to B1/adaptive pipeline creation.
  - Warm startup target ≤5 seconds, hard limit ≤10 seconds.
  - Qualified-seed cold startup target ≤15 seconds, hard limit ≤30 seconds.
  - Record per-artifact native creation times and visible-content timestamp before and after.
- Use RenderDoc for two fixed-camera captures:
  - First visible frame: exact fallback bound, no receiver-cache feedback dispatch, valid non-uniform scene.
  - Post-publication frame: feedback/adaptive dispatches and specialized opaque path present with valid descriptors and barriers.
  - Compare final LDR output using the repository’s quality tolerance and check validation output.
- Use `ProfileSymbols` for subsequent Nsight Graphics GPU profiling; use `Development` for reproducing and timing this startup failure. Do not use an interactive runtime debugger unless telemetry and RenderDoc evidence conflict.

## Assumptions

- Receiver-feedback and cache-specialized pipelines remain pixel-output-equivalent optimizations, matching the existing startup design documentation.
- User pipeline caches will not be deleted or overwritten during testing.
- The partial startup-telemetry patch already present in the workspace is the implementation starting point; unrelated dirty-worktree changes remain untouched.
