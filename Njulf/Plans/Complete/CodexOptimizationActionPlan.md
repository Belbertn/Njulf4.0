# Codex Optimization Action Plan

This plan distills the remaining useful items from `CodexOptimization.txt` into an implementation sequence focused on measurable performance wins and production-quality diagnostics.

## Goals

- Make every runtime GPU stall attributable to a specific renderer path.
- Prevent avoidable render-target rebuilds during steady gameplay.
- Bound staging overflow memory while keeping uploads frame-safe.
- Remove low-risk per-frame CPU waste.
- Move feature isolation from sample-app setting mutation into renderer-owned pass gating.

## Phase 1: Make Stalls Attributable

Goal: every global GPU stall has a reason, duration, and recent-event trail.

Implementation:

- Add a renderer-owned helper for measured idle waits, for example `RecordDeviceWaitIdle(RuntimeStallReason reason, string description, Action wait)`.
- Replace raw `_context.WaitIdle()` and runtime `DeviceWaitIdle()` calls with measured waits where they affect runtime behavior.
- Cover these paths:
  - render-target profile rebuild
  - swapchain recreation
  - environment resource recreation
  - mesh diagnostic pipeline recreation
- Leave dispose-time `DeviceWaitIdle()` untracked unless shutdown diagnostics become a requirement.
- Preserve existing `RuntimeStallTracker` event history and extend reason descriptions rather than adding a parallel diagnostics system.

Acceptance criteria:

- `RuntimeDeviceWaitIdleCount` increments when render targets, swapchain, environment resources, or diagnostic pipeline recreation force a device idle.
- diagnostics identify the worst stall reason as `DeviceWaitIdle` or `ResourceResize` when applicable.
- recent stall events include useful descriptions such as `Render target profile rebuild`, `Swapchain recreate`, and `Environment resource recreate`.
- unit tests cover tracker behavior for the new reasons or helper logic where practical.

## Phase 2: Stabilize Render-Target Rebuilds

Goal: dynamic resolution and setting churn must not repeatedly recreate render targets during steady gameplay.

Implementation:

- Separate requested dynamic resolution scale from committed render-target scale.
- Quantize dynamic resolution scale to stable buckets, for example 0.02 or 0.05 steps.
- Add a cooldown or confirmation window before applying dynamic-resolution extent changes.
- Require multiple consecutive frames outside the target band before changing committed render-target scale.
- Keep explicit resize and direct user setting changes responsive; debounce should mainly protect runtime dynamic scaling.
- Add diagnostics:
  - requested dynamic scale
  - committed render-target scale
  - render-target recreate count
  - last render-target recreate reason

Acceptance criteria:

- dynamic resolution does not call `WaitIdle()` every time frame time oscillates around the threshold.
- render-target recreate count remains stable during normal camera movement.
- quality preset changes and explicit resolution changes still rebuild once when required.
- diagnostics make it clear whether a resize came from dynamic resolution, swapchain resize, or render feature target changes.

## Phase 3: Fix Staging Overflow Lifetime

Goal: overflow staging buffers should be frame-safe, bounded, reusable, and diagnosable.

Implementation:

- Replace permanent `_largeUploadBuffers` retention with a small reuse pool.
- Track each overflow buffer's size, last-used frame index, and owning fence or retirement frame.
- Reuse overflow buffers only after the frame that used them is complete.
- Destroy excess overflow buffers after safe retirement when caps are exceeded.
- Add caps:
  - max retained overflow buffer count
  - max retained overflow bytes
  - max single retained overflow buffer size, if useful
- Add diagnostics:
  - overflow count this frame
  - total overflow count
  - retained overflow buffer count
  - retained overflow bytes
  - peak overflow bytes
  - largest overflow allocation

Acceptance criteria:

- repeated burst uploads reuse overflow buffers instead of allocating indefinitely.
- one-time large uploads do not permanently inflate staging memory.
- `TotalAllocatedBytes` reflects currently retained buffers, not historical allocations.
- staging diagnostics expose upload budget pressure clearly enough to tune the default ring size.

## Phase 4: Remove Low-Risk Per-Frame Waste

Goal: reduce CPU overhead without changing rendering behavior.

Implementation:

- Add cached matrices to `SceneRenderingData`:
  - `InverseViewMatrix`
  - `InverseProjectionMatrix`
  - `InverseViewProjectionMatrix`
- Compute the inverse matrices once in `SceneDataBuilder.Build()` after view, projection, and view-projection are created.
- Replace pass-local `.Invert()` calls in:
  - `ForwardPlusPass`
  - `TransparentForwardPass`
  - `SkyboxPass`
  - `ParticlePass`
  - `AmbientOcclusionPass`
  - `AmbientOcclusionBlurPass`
  - `FogPass`
  - `TiledLightCullingPass`
- Skip `ResetSecondaryGraphicsCommandPool()` in `VulkanRenderer.BeginFrame()` when `Settings.UseSecondaryCommandBuffers` is false.
- If secondary command buffers were allocated in a previous frame, either reset once when toggling back on or reset lazily before the next secondary recording.

Acceptance criteria:

- render passes no longer perform repeated camera matrix inversions.
- secondary command-pool reset is skipped when secondary command buffers are disabled.
- existing GPU struct layout tests still pass.
- rendered output is unchanged.

## Phase 5: Renderer-Owned Feature Isolation

Goal: profiling modes must work consistently outside `NjulfHelloGame`.

Implementation:

- Keep `RenderSettings.FeatureIsolation` as the public API.
- Add renderer or render-graph pass gating based on isolation mode.
- Stop relying on `SampleInputController` mutating unrelated quality settings to approximate isolation.
- Keep sample-app toggles simple: change only `Settings.FeatureIsolation`.
- Add diagnostics:
  - active isolation mode
  - skipped pass count
  - optionally, names of skipped passes in debug builds

Suggested pass behavior:

- `FullFrame`: current behavior.
- `Geometry`: depth, opaque forward, minimal composite/debug.
- `Shadows`: shadow passes plus required setup.
- `PostProcessing`: post-processing passes over normal scene inputs where available.
- `Reflections`: reflection preparation and affected forward path.
- `Animation`: skinning plus minimal geometry.
- `Particles`: particle simulation/render plus required composite.

Acceptance criteria:

- toggling feature isolation does not permanently alter quality preset settings.
- render graph consistently skips irrelevant passes.
- diagnostics and GPU timings match the active isolated pass set.
- isolation behavior works from renderer settings alone, independent of the sample app.

## Implementation Order

1. Phase 1 first, because it proves which stalls matter.
2. Phase 2 next, because render-target rebuilds are the highest-risk avoidable stall source.
3. Phase 3 after that, because staging overflow can silently become memory pressure.
4. Phase 4 can be implemented anytime, but it is smaller and less strategic.
5. Phase 5 last, because it changes profiling behavior and needs careful pass semantics.

## Verification

Run:

```powershell
dotnet test Njulf.sln
```

Manual validation in the sample app:

- Toggle AO, AA, bloom, fog, resolution scale, and quality presets.
- Enable dynamic resolution and watch committed scale and render-target recreate count.
- Trigger asset upload bursts and watch staging overflow diagnostics.
- Cycle feature isolation modes and verify pass timings and skipped passes are coherent.

## Notes

- Treat frame-fence waits as a symptom, not a target to remove blindly. Act only if `CpuWaitForFrameFenceMicroseconds` is high.
- Treat scene payload rebuild work as conditional. The current code already separates static and culling signatures, but camera-dependent CPU payloads, CPU meshlet frustum culling, debug snapshots, transparency sorting, and shadow payloads can still invalidate broad work.
- Use diagnostics from Phase 1 before deciding whether to optimize GPU pass cost, CPU recording cost, upload pressure, or presentation stalls.
