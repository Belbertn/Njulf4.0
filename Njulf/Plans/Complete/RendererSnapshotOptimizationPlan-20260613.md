# Renderer Snapshot Optimization Plan - 2026-06-13

Source snapshot: `Plans/performance-20260613-142343.json`

## Baseline Findings

- CPU renderer time is `16.602 ms` against a `6.0 ms` budget.
- Tracked GPU memory is `3,322,486,636 bytes` (`3168.6 MiB`) against a `2048 MiB` budget.
- GPU timings are not usable in this capture: `GpuTimingSupported = 0`, `GpuTimingEnabled = 0`, and all GPU pass timings are `0`.
- The frame is dominated by CPU command recording and waiting on the previous frame fence:
  - `CpuHiZBuildRecordMicroseconds = 8146` (`8.146 ms`)
  - `CpuPointShadowRecordMicroseconds = 2185` (`2.185 ms`)
  - `CpuDirectionalShadowRecordMicroseconds = 1526` (`1.526 ms`)
  - `CpuSkinningRecordMicroseconds = 851` (`0.851 ms`)
  - `CpuWaitForFrameFenceMicroseconds = 19557` (`19.557 ms`)
- Memory pressure is mostly asset and always-resident renderer allocation:
  - Textures: `1,716,868,348 bytes` (`1637.3 MiB`)
  - Mesh buffers: `805,306,368 bytes` allocated (`768 MiB`), `610,882,684 bytes` used (`582.6 MiB`)
  - Reflection probes: `268,471,344 bytes` (`256 MiB`)
  - Shadow maps: `123,731,968 bytes` (`118 MiB`)
  - Staging buffers: `134,217,728 bytes` (`128 MiB`)
  - Render targets: `80,757,632 bytes` (`77 MiB`)
- Hi-Z/occlusion is suspicious in this capture. It reports `19,508` occlusion-culled meshlets, but `ForwardMeshletVisibleAfterOcclusion = 66,057`, equal to `SubmittedOpaqueMeshlets`. Either the diagnostic field is not representing post-occlusion submissions, or Hi-Z is costing CPU without reducing the forward task dispatch count.

## Priority 1: Make The Snapshot Actionable

### 1. Enable Real GPU Pass Timings

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Diagnostics/RendererDiagnostics.cs`
- `NjulfHelloGame/SampleInputController.cs`

Current issue:
- The snapshot cannot distinguish a real GPU bottleneck from CPU-side command-recording and frame-pacing stalls because all GPU timings are zero.

Optimization:
1. Add a startup/device diagnostic that explains why GPU timing is unsupported or disabled.
2. Add a runtime toggle or startup option to enable GPU timing where supported.
3. Persist GPU timing support, enablement, pending state, and per-pass timing validity in the snapshot.
4. Re-capture this same scenario after timing is available.

Acceptance criteria:
- Snapshot contains non-zero timings for depth, Hi-Z, shadows, forward, fog, bloom, AA, particles, and composite on hardware that supports timestamps.
- If unsupported, the snapshot includes a clear unsupported reason rather than zero-valued timings that look like successful measurements.

### 2. Fix Occlusion Diagnostics Before Tuning Hi-Z

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/depth.task`

Current issue:
- `OcclusionCulledMeshlets = 19508`, but `ForwardMeshletVisibleAfterOcclusion = 66057`, which matches submitted meshlets. This prevents a reliable Hi-Z cost/benefit decision.

Optimization:
1. Split diagnostics into clear fields:
   - CPU-submitted forward meshlets.
   - GPU forward task invocations.
   - GPU frustum culled.
   - GPU occlusion tested.
   - GPU occlusion rejected.
   - GPU emitted meshlets.
2. Ensure `ForwardMeshletVisibleAfterOcclusion` means post-occlusion emitted meshlets or rename it.
3. Add a snapshot sanity check that flags impossible combinations.

Acceptance criteria:
- With Hi-Z enabled, emitted forward meshlets plus rejected meshlets reconciles against tested meshlets.
- With Hi-Z disabled, occlusion-tested and occlusion-rejected counters are zero.

## Priority 2: Cut CPU Command Recording Cost

### 3. Reduce Hi-Z Build Recording Overhead

Files:
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Resources/HiZDepthPyramid.cs`

Current issue:
- Hi-Z command recording is the largest measured CPU cost at `8.146 ms`.
- `HiZBuildPass.Execute` records one descriptor bind, push constant, dispatch, and barrier per mip every frame.
- The capture uses a `800x450` Hi-Z pyramid with `10` mips.

Optimization:
1. Add a temporary A/B snapshot workflow for Hi-Z on/off using the existing `EnableHiZOcclusion` control.
2. If the forward-emitted count does not drop enough to pay for Hi-Z, disable adaptive Hi-Z sooner for this scene.
3. Batch descriptor writes and avoid per-mip descriptor-set churn where possible.
4. Consider a single shader path that builds multiple mips per dispatch group, or reduce the generated mip count for low-resolution pyramids.
5. Avoid redundant image barriers when the previous and next layouts are already known.

Acceptance criteria:
- `CpuHiZBuildRecordMicroseconds` drops below `1.0 ms`, or Hi-Z auto-suppresses when it cannot reduce forward work materially.
- Visual output remains stable with occlusion enabled.

### 4. Reduce Shadow Recording In The Default Sample

Files:
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`
- `Njulf.Rendering/Pipeline/PointShadowPass.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `NjulfHelloGame/SampleLighting.cs`

Current issue:
- Directional and point shadows cost `3.711 ms` of CPU recording combined.
- Point shadows render six dynamic-rendering passes for one selected point light.
- Spot shadows allocate a `4096x4096` atlas even when `SpotShadowSelectedCount = 0`.

Optimization:
1. Make the development/default sample shadow settings proportional to selected lights:
   - Do not allocate or register a spot atlas when no spot shadows can be selected.
   - Keep point shadows disabled by default unless the active lighting scenario needs them.
2. Add per-light shadow invalidation so static shadow maps are not re-recorded every frame when light, caster, and receiver inputs are unchanged.
3. For point shadows, record only faces whose frustum intersects shadow casters.
4. Consider layered point-shadow rendering to reduce six begin/end rendering blocks and repeated viewport/scissor setup.
5. Add a lower-cost default directional cascade profile for development captures, for example two cascades before three or four.

Acceptance criteria:
- Default scene with one directional light and one point light records below `1.5 ms` total shadow CPU time.
- Spot shadow memory is near zero when no spot shadows are selected.
- Shadow diagnostics report skipped static shadow updates.

### 5. Move Expensive Pass Recording To Secondary Command Buffers

Files:
- `Njulf.Rendering/Core/CommandBufferManager.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/*.cs`

Current issue:
- The renderer records all passes serially into one primary graphics command buffer.
- Hi-Z, shadow passes, and skinning are good candidates for parallel command recording if their inputs are immutable for the frame.

Optimization:
1. Add reusable per-frame secondary command buffers for independent passes.
2. Record directional shadows, point shadows, Hi-Z, skinning, and post effects in parallel where dependencies allow.
3. Keep the primary command buffer responsible for frame ordering and barriers.
4. Preserve debug labels around each pass.

Acceptance criteria:
- Total `CpuTotalDrawSceneMicroseconds` drops without changing pass output.
- RenderDoc still shows readable pass labels and order.

## Priority 3: Bring GPU Memory Under Budget

### 6. Add Texture Streaming Or Stronger Texture Budgeting

Files:
- `Njulf.Rendering/Resources/TextureManager.cs`
- `Njulf.Assets/ModelImporter.cs`
- `NjulfHelloGame/Program.cs`

Current issue:
- Texture assets consume `1.637 GiB`, nearly the entire `2 GiB` development budget by themselves.
- All `77` loaded file textures are downscaled, but they still dominate memory.

Optimization:
1. Add profile-based `MaxLoadedTextureDimension` values:
   - Development 1080p60: `1024` default.
   - High quality: `2048` or higher.
2. Prefer compressed GPU formats for suitable color/normal/material textures where available.
3. Add texture residency tiers and unload textures for inactive sample scenarios.
4. Add snapshot output for the top N largest textures by source path and estimated bytes.

Acceptance criteria:
- Development profile texture memory drops below `768 MiB`.
- Snapshot identifies the largest texture contributors without a debugger.

### 7. Shrink Always-Resident Reflection Probe Allocation

Files:
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`

Current issue:
- Snapshot has `ReflectionProbeCount = 2`, but `ReflectionProbeCapacity = 64`.
- Estimated reflection probe memory is `256 MiB`, implying allocation/budgeting is based on max capacity rather than active probes.

Optimization:
1. Allocate the reflection cubemap array to the active or configured runtime capacity, not the maximum default of 64.
2. Grow the array only when authored probes exceed current capacity.
3. Add a low-memory development default such as `MaxProbes = 8` and `ProbeResolution = 128`.
4. Separate metadata buffer capacity from cubemap image capacity in diagnostics.

Acceptance criteria:
- Two active probes consume less than `64 MiB` in the development profile.
- Increasing authored probes still works through explicit growth.

### 8. Right-Size Mesh And Scene Buffers

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/Memory/BufferManager.cs`

Current issue:
- Mesh buffers allocate seven `16 MiB` buffers initially and grow by doubling. Snapshot shows `768 MiB` allocated for `582.6 MiB` used.
- Scene draw buffers are preallocated for `65,536` meshlet draw commands per buffer, per frame, across several categories.

Optimization:
1. Replace uniform initial mesh buffer size with per-buffer defaults based on expected stride and content:
   - Vertex/index buffers can stay larger.
   - Metadata, meshlet vertex indices, triangle indices, and skinning data should start smaller.
2. Add shrink/repack support after model load, or expose a `CompactStaticBuffers()` step after sample scene loading.
3. Split scene buffer capacities by actual use: transparent, masked, local-shadow, and depth buffers should not all start at opaque meshlet capacity.
4. Add high-water diagnostics for each scene buffer, not just aggregate scene buffer bytes.

Acceptance criteria:
- Mesh allocation overhead stays below 15 percent after scene load.
- Scene buffer allocation reflects actual high-water usage within one growth step.

### 9. Lazily Allocate Feature Render Targets

Files:
- `Njulf.Rendering/Resources/RenderTargetManager.cs`
- `Njulf.Rendering/Pipeline/AntiAliasingPass.cs`
- `Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs`
- `Njulf.Rendering/Pipeline/BloomPass.cs`

Current issue:
- Render targets for SMAA, TAA history, motion vectors, AO scratch, and bloom are allocated up front.
- Snapshot anti-aliasing mode is SMAA, motion vectors are disabled, and TAA history is not used.

Optimization:
1. Allocate TAA history and motion vectors only when TAA or motion vectors are enabled.
2. Allocate SMAA edge/blend targets only for SMAA modes.
3. Allocate AO targets only when AO is enabled.
4. Allocate bloom mip chains to `Settings.Bloom.MipCount`, not `BindlessIndex.MaxBloomMipTextures`.

Acceptance criteria:
- Disabled features contribute zero or near-zero render-target memory.
- Enabling features at runtime creates the required targets and re-registers bindless textures correctly.

### 10. Make Staging Ring Size Profile-Based

Files:
- `Njulf.Rendering/Memory/StagingRing.cs`
- `Njulf.Rendering/ServiceCollectionExtensions.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`

Current issue:
- Staging allocation is `128 MiB`, while this frame uses only `74 KiB` and the session peak is about `7.7 MiB`.

Optimization:
1. Default development staging to `32 MiB` or less.
2. Grow on demand when large uploads occur.
3. Track per-frame and session peak per category.

Acceptance criteria:
- Idle/rendering frames keep staging below `32 MiB`.
- Large model or texture upload still succeeds without manual tuning.

## Priority 4: Reduce Submitted Work

### 11. Improve Meshlet Quality And LOD Distribution

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Assets/MeshletBuilder.cs`

Current issue:
- Snapshot submits `66,057` CPU meshlets out of `372,985` total.
- `23,849` total meshlets have fewer than `32` triangles, which increases task dispatch overhead.
- Average submitted meshlet size is only `31.85` triangles.

Optimization:
1. Revisit LOD0 limits. Current LOD0 is capped at `32` triangles, which creates many small meshlets.
2. Add a quality mode that targets larger, spatially coherent meshlets for static environment geometry.
3. Use meshopt-style clustering or another spatial clustering pass instead of triangle-order grouping where possible.
4. Add import diagnostics for meshlet count, small meshlet count, and average triangles per meshlet per mesh.

Acceptance criteria:
- Submitted meshlet count drops materially for the same visible geometry.
- Occlusion and frustum culling remain effective or improve.

### 12. Add Scenario-Level Quality Profiles

Files:
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Diagnostics/RenderBudgetSettings.cs`
- `NjulfHelloGame/SamplePerformanceScenario.cs`

Current issue:
- The default development profile enables many production features simultaneously: HDR, bloom, fog, AO, SMAA, environment, reflections, directional shadows, spot-shadow resources, point shadows, animation, and particles.

Optimization:
1. Add explicit quality presets for `Development`, `PerformanceCapture`, and `Cinematic`.
2. In `PerformanceCapture`, keep one feature group under test at a time.
3. Persist the active quality preset in the performance snapshot.

Acceptance criteria:
- A performance capture can isolate geometry, shadows, post-processing, reflections, animation, or particles without source edits.
- Snapshot comparisons are meaningful because feature state is encoded in the captured file.

## Verification Checklist

1. Re-run the same snapshot scenario after each priority group.
2. Track:
   - `CpuTotalDrawSceneMicroseconds`
   - `CpuHiZBuildRecordMicroseconds`
   - `CpuDirectionalShadowRecordMicroseconds`
   - `CpuPointShadowRecordMicroseconds`
   - `TrackedGpuMemoryBytes`
   - `TextureAssetBytes`
   - `MeshBufferAllocatedBytes`
   - `ReflectionProbeBytes`
   - `ShadowMapBytes`
   - `RuntimeStallMicrosecondsThisFrame`
3. Capture one run with Hi-Z on and one with Hi-Z off.
4. Capture one run with point shadows on and one with point shadows off.
5. Run `dotnet test Njulf.sln` after implementation changes.
