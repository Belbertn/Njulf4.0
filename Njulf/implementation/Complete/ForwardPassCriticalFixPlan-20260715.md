# Forward-Pass Critical Fix Plan

1. **Make valid prepass depth a Forward+ invariant.** Force the depth prepass on whenever Forward+, tiled local lights, or depth-based effects are active. Remove the animation-debug override that silently disables it.

2. **Remove the broken fallback branch.** Stop clearing depth inside Forward+ as a substitute for the prepass. If no-prepass rendering must remain supported, implement it later as a separate pipeline with depth-writing opaque variants and depth-independent light culling.

3. **Enforce pass ordering and dependencies.** Declare that tiled light culling requires depth produced by `DepthPrePass`; add runtime assertions so culling and Forward+ cannot execute with missing or stale depth.

4. **Preserve depth between consumers.** Replace `StoreOp.DontCare` with `Store` in every render scope followed by another depth reader, including MotionVector, SceneSurface, Forward+, skybox, and transparency. Use `DontCare` only after the final consumer.

5. **Add regression coverage.** Test overlapping opaque geometry, tiled local lights, Hi-Z/SSGI, skybox, and transparency. Run with Vulkan validation enabled and include a tile-based GPU configuration if available.

6. **Clean up the setting contract.** Either remove `EnableDepthPrePass` from the production Forward+ configuration or make unsupported combinations fail immediately instead of producing degraded rendering.

## Completion criteria

- Forward+ cannot execute without valid populated depth.
- Tiled light culling never reads cleared, stale, or undefined depth.
- Depth remains valid until its final declared consumer.
- Disabling the prepass is either rejected or routed to a complete, explicitly supported fallback.
- Vulkan validation reports no attachment-lifetime or pass-compatibility errors in the covered configurations.
