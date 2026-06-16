# Nsight Graphics Performance Findings - 2026-06-16

Source: two Nsight Graphics screenshots from `NjulfHelloGame_2026_06_16_01_17_19.ngfx-gputrace`.

## Capture Summary

- Matched frame duration: about 85.12 ms.
- Active Vulkan GPU time: about 69.44 ms.
- Dominant passes:
  - `GpuVisibilityPass`: about 38.1 ms.
  - `ForwardPlusPass`: about 21.5 ms.
  - `DepthPrePass`: about 6.2 ms.
- Nsight throughput counters are low for memory and SM throughput:
  - SM throughput around 11%.
  - VRAM throughput around 5%.
  - L2 throughput around 5%.
  - Warp occupancy shows high unallocated warps.
- Initial read: this frame is not bandwidth-bound. The main problem is too much serialized or low-occupancy GPU work before and during mesh shading.

Nsight also reports mismatched perf markers, so nested pass analysis may be incomplete. The top-level pass durations are still clear enough to rank the work.

## Highest Impact Work

1. Rework `GpuVisibilityPass` first.

   `gpu_visibility.comp` dispatches per object, then loops every selected meshlet and appends to several output lists with atomics. For non-transparent meshlets it also loops directional cascades, spot shadows, and point shadow faces. This can scale badly with meshlet count and active shadowed lights.

   Recommended direction:
   - Split visibility into cheaper stages:
     - object/frustum/LOD selection,
     - visible object or meshlet range compaction,
     - shadow caster list generation only for relevant visible ranges.
   - Replace per-meshlet global atomics where practical with prefix sums, per-workgroup bins, or two-pass count/compact.
   - Separate local shadow list generation so point/spot shadow cost can be capped and profiled independently.

2. Remove duplicated forward visibility work.

   `GpuVisibilityPass` emits opaque meshlet draws, then `forward.task` re-runs meshlet frustum and Hi-Z checks before emitting mesh shader work. It also performs diagnostic atomics per candidate.

   Recommended direction:
   - Make `GpuVisibilityPass` produce the final compact visible opaque list.
   - Let `ForwardPlusPass` consume that final list directly.
   - Keep task-shader Hi-Z as a debug/validation path or only enable it when adaptive counters show it rejects enough work to offset `DepthPrePass` plus Hi-Z sampling.
   - Gate forward diagnostic atomics behind a debug/timing mode.

3. A/B Hi-Z and depth prepass ROI immediately.

   The captured frame pays about 6.2 ms for `DepthPrePass`, then pays additional task-shader Hi-Z cost in `ForwardPlusPass`. If Hi-Z does not reject enough forward meshlets, it is probably a net loss for this scene.

   Existing controls:
   - `F8`: toggle Hi-Z occlusion.
   - `Ctrl+F4`: toggle GPU timing.
   - `Ctrl+F6`: cycle feature isolation.
   - `Ctrl+F5`: cycle quality preset.

   Test:
   - Capture Hi-Z on/off from the same camera.
   - Compare `DepthPrePass`, `HiZBuildPass`, `ForwardPlusPass`, and forward emitted/rejected meshlet counters.
   - If the forward reduction is less than the depth plus Hi-Z overhead, disable Hi-Z for this scenario or make adaptive suppression more aggressive.

4. Isolate shadow-list generation.

   Shadow work is likely amplifying `GpuVisibilityPass`: every visible non-transparent meshlet can be considered for directional cascades, spot shadow lists, and point shadow cube faces.

   Existing controls:
   - `F1`: toggle directional shadows.
   - `F12`: toggle spot shadows.
   - `Number4`: toggle point shadows.
   - `Number3`: cycle lighting mode.

   Test:
   - Capture baseline.
   - Capture with point shadows off.
   - Capture with spot shadows off.
   - Capture with all shadows off.
   - Compare `GpuVisibilityPass`, shadow pass timings, and `ForwardPlusPass`.

   If visibility time collapses with local shadows disabled, prioritize:
   - lower default local shadow budgets,
   - stricter light/object coarse culling before meshlet list generation,
   - separate compute passes per shadow type,
   - cached static shadow caster lists where camera motion does not invalidate them.

5. Optimize `ForwardPlusPass` after work reduction.

   Forward shading is the second largest visible pass. It likely contains expensive fragment work: material texture sampling, AO sampling, shadow PCF, tiled light loops, reflections, and debug/material branches.

   Recommended direction after reducing submitted meshlets:
   - Specialize release pipelines for common material/light feature sets.
   - Remove or compile out debug/material inspection branches from normal forward shaders.
   - Profile directional, spot, and point shadow PCF radii separately.
   - Check whether AO bilateral sampling in the forward shader is worth its cost versus pre-resolved AO.

## Near-Term Validation Checklist

- Capture with `F8` Hi-Z off.
- Capture with all shadows off.
- Capture with point shadows off only.
- Capture with spot shadows off only.
- Capture with `Ctrl+F6` feature isolation modes.
- Ensure perf marker hierarchy is stable across traced frames to remove the Nsight mismatch warning.
- Record counters for:
  - visible objects,
  - submitted opaque meshlets,
  - forward candidates,
  - forward frustum rejects,
  - forward occlusion rejects,
  - forward emitted meshlets,
  - local shadow meshlet counts.

## Expected Priority

1. `GpuVisibilityPass` list generation and atomics.
2. Duplicate meshlet culling between visibility and forward task shader.
3. Hi-Z/depth prepass adaptive policy.
4. Local shadow visibility generation.
5. Forward fragment shader specialization and feature trimming.
