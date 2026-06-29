# Hi-Z / Occlusion Production-Readiness Plan for Njulf4.0 `Simplified`

**Target branch:** `Simplified`  
**Primary goal:** stop paying Hi-Z cost when no forward path can consume it, then make Hi-Z occlusion effective and measurable in the GPU-compacted scene-submission path.  
**Baseline snapshot:** `performance-20260628-190404.json`, captured `2026-06-28T19:04:04+02:00`.

---

## 1. Current diagnosis

The attached snapshot shows Hi-Z/occlusion as the main *secondary* CPU-side loss after DDGI scheduling.

| Metric | Snapshot value | Interpretation |
|---|---:|---|
| CPU frame | 91.309 ms | CPU frame is far over the 6 ms development budget. |
| `CpuHiZBuildRecordMicroseconds` | 7.903 ms | Hi-Z build recording is the second-largest avoidable CPU cost after DDGI scheduling. |
| `CpuDepthPrePassRecordMicroseconds` | 0.056 ms | The depth prepass itself is not the CPU problem. |
| `DepthPrePassEnabled` | 1 | Depth is generated. |
| `HiZEnabled` / `OcclusionEnabled` | 1 / 1 | The policy believes Hi-Z occlusion is active. |
| `HiZMipCount` | 9 | A 9-mip pyramid is being built. |
| `HiZWidth` / `HiZHeight` | 800 × 450 | Hi-Z source is half-resolution for the current 1600 × 900 scene render extent. |
| `ForwardOcclusionTestedMeshletsGpu` | 0 | No forward meshlets were actually measured as Hi-Z-tested. |
| `ForwardGpuOcclusionRejectedMeshlets` | 0 | No meshlets were rejected by Hi-Z. |
| `GpuMeshletCountersEnabled` | 0 | The adaptive policy lacks valid forward occlusion counters. |
| `HiZPolicyAdaptiveStatus` | `CountersUnavailable` | Policy cannot prove Hi-Z is profitable. |
| `SceneSubmissionForwardPath` | `GpuCompactedIndirect` | Forward path uses the compacted indirect path. |
| `SceneSubmissionForwardTaskShader` | `CompactedEmitTask` | The selected task shader only emits compacted work; it does not perform Hi-Z culling. |

### Key codebase findings

1. `VulkanRenderer.DrawScene()` calls `PlanHiZVisibility(...)`, writes `sceneData.HiZBuildEnabled`, `sceneData.OcclusionCullingEnabled`, and diagnostic fields, then the render graph later executes `SceneOpaqueCompactionPass`, `DepthPrePass`, `HiZBuildPass`, and `ForwardPlusPass`.

2. `PlanHiZVisibility()` builds a `HiZVisibilityPolicyInput` from depth-prepass settings, global Hi-Z toggles, scene/camera change state, GPU meshlet counter availability, completed GPU timing, and completed occlusion counters. It then calls `HiZVisibilityPolicy.Plan(...)`.

3. `HiZVisibilityPolicy.Plan(...)` already has the right high-level intent: warm up after camera/scene changes, suppress Hi-Z when adaptive measurements are unprofitable, and periodically probe again. The gap is that adaptive logic only runs when `MeshletCountersActive` is true. In this snapshot the counters are unavailable, so the policy stays active instead of suppressing or proving benefit.

4. `HiZBuildPass` is compute-only and supports async compute, but in this snapshot async compute is disabled. The pass records a descriptor bind, push constant, dispatch, and layout transition for every mip.

5. The legacy `forward.task` shader contains the actual Hi-Z occlusion test and increments forward occlusion counters. The compacted path uses `forward_compacted.task`, which emits compacted draws without performing the Hi-Z test. Therefore, with `SceneSubmissionForwardPath = GpuCompactedIndirect`, building the Hi-Z pyramid currently provides no forward-occlusion benefit.

6. `SceneOpaqueCompactionPass` currently runs before `DepthPrePass` and `HiZBuildPass`. Its shader performs candidate compaction, frustum rejection, GPU LOD selection, depth-list compaction, and shadow-list compaction, but it cannot use the current frame's Hi-Z pyramid because that pyramid is produced later.

The immediate issue is therefore **not merely that Hi-Z is expensive**. The correctness issue is that **Hi-Z is built and marked active while the selected forward path cannot consume it**.

---

## 2. Production target

The finished system should satisfy these production requirements:

1. **No wasted Hi-Z build:** If no enabled consumer can read the pyramid in the current frame, `HiZBuildPass` must not record or execute, except for explicit periodic probes.
2. **Compacted path supports occlusion:** The GPU-compacted scene submission path must be able to perform Hi-Z occlusion before forward shading.
3. **No same-frame CPU readback:** Adaptive decisions must use frame-lagged counters/timestamps and never block the CPU waiting for GPU results.
4. **Always-available production counters:** Hi-Z usefulness should not require switching to diagnostic shader variants or recreating pipelines at runtime.
5. **Stable fallback:** Legacy non-compacted forward Hi-Z remains available until post-Hi-Z compaction is validated.
6. **Measurable profitability:** Policy decisions must compare estimated saved forward work against depth + Hi-Z + post-compaction cost.
7. **Safe rollout:** Feature flags, validation scenes, counters, budget gates, and regression tests must be in place before defaulting it on.

---

## 3. Implementation plan

## Step 0 — Freeze a reproducible baseline

**Purpose:** make sure every later change can be judged against the current failure mode.

### Tasks

1. Add a checked-in baseline note under:
   - `Njulf/Plans/Baselines/HiZOcclusion-20260628/`
   - include the attached snapshot and a short summary:
     - CPU Hi-Z build record: `7.903 ms`
     - occlusion tested: `0`
     - occlusion rejected: `0`
     - forward path: `GpuCompactedIndirect`
     - task shader: `CompactedEmitTask`
     - adaptive status: `CountersUnavailable`

2. Add a benchmark scene tag to the sample runner:
   - `normal-sponza-interior` or current `Strut` scenario
   - camera angle that demonstrates the current zero-occlusion result
   - at least one separate synthetic occluder scene with large hidden geometry behind an opaque wall

3. Capture baseline snapshots after GPU timestamps become valid:
   - discard first 3–5 frames while `GpuTimingPending = 1`
   - capture at least 120 frames to match the existing scheduler/Hi-Z moving windows
   - save median, P95, and max for:
     - CPU frame
     - CPU Hi-Z build record
     - GPU depth prepass
     - GPU Hi-Z build
     - forward opaque GPU time
     - occlusion tested / culled counts

### Acceptance criteria

- A baseline artifact exists.
- At least one “occlusion should help” scene and one “occlusion should not help” scene are available.
- The current failure mode is reproducible: Hi-Z active but compacted forward path reports zero useful occlusion.

---

## Step 1 — Add a consumer-aware Hi-Z gate

**Purpose:** stop the immediate waste: do not build Hi-Z when no active pass can consume it.

### Current problem

`PlanHiZVisibility()` is deciding whether to build/use Hi-Z before it knows whether the selected forward path can actually consume Hi-Z. In the current compacted-indirect path, `forward_compacted.task` does not test Hi-Z.

### Tasks

1. Extend `HiZVisibilityPolicyInput` in:
   - `Njulf/Njulf.Rendering/Data/HiZVisibilityPolicy.cs`

   Add:

   ```csharp
   bool ForwardConsumerCanUseHiZ,
   bool ForceHiZProbe,
   bool OtherPassRequiresHiZ,
   string ConsumerSummary
   ```

2. Add a helper in `VulkanRenderer.cs`:

   ```csharp
   private HiZConsumerDecision ResolveHiZConsumers(SceneRenderingData sceneData)
   ```

   Initial logic:

   ```text
   Legacy forward path:
     can consume Hi-Z via forward.task

   GPU compacted indirect/direct path:
     cannot consume Hi-Z until Step 4 introduces post-HiZ forward compaction

   Foliage:
     only mark as a consumer when the active foliage shader path samples the Hi-Z pyramid

   Other passes:
     do not count Ambient Occlusion, TiledLightCulling, or Forward depth reads as Hi-Z consumers; those read scene depth, not the pyramid
   ```

3. Modify `PlanHiZVisibility(...)` to accept `SceneRenderingData sceneData` or enough inputs to determine the consumer decision.

4. Modify `HiZVisibilityPolicy.Plan(...)`:

   ```text
   if no consumer and not ForceHiZProbe:
       BuildHiZ = false
       UseHiZForOcclusion = false
       Status = Skipped
       Reason = "No active Hi-Z consumer for the selected forward path."
   ```

5. Add a diagnostic field to `SceneRenderingData` and `RendererDiagnostics`:

   ```text
   HiZConsumerSummary
   HiZConsumerCount
   HiZBuildSkippedBecauseNoConsumer
   ```

6. Update `PerformanceSnapshotWriter` so the snapshot clearly reports:

   ```text
   HiZPolicyReason = "No active Hi-Z consumer for compacted forward path."
   HiZConsumerSummary = "Forward=CompactedEmitTask(no Hi-Z), Foliage=none"
   ```

### Acceptance criteria

- With the current snapshot’s path (`GpuCompactedIndirect` + `CompactedEmitTask`), `HiZBuildPass` is skipped.
- `CpuHiZBuildRecordMicroseconds` drops near zero when no consumer exists.
- `DepthPrePassEnabled` remains independent; do not disable the depth prepass unless a separate policy says it is safe.
- Snapshot reports a reason that is understandable without reading code.

---

## Step 2 — Make production occlusion counters always available

**Purpose:** remove the dependency on diagnostic shader variants for adaptive policy.

### Current problem

`HiZVisibilityPolicy.UpdateAdaptiveState(...)` only updates profitability when `MeshletCountersActive` is true. In the snapshot it is false, so adaptive status is `CountersUnavailable`.

The code already has a better production-grade counter path in `SceneOpaqueCompactionPass`: a per-frame `GPUSceneSubmissionCounters` buffer, async readback, and frame-lagged snapshots. Use this path for always-on, low-overhead visibility telemetry.

### Tasks

1. Extend `GPUSceneSubmissionCounters` in:
   - `Njulf/Njulf.Rendering/Data/GPUStructs.cs`

   Add fields:

   ```csharp
   public uint ForwardHiZCandidateCount;
   public uint ForwardHiZTestedCount;
   public uint ForwardHiZCulledCount;
   public uint ForwardHiZSkippedNoConsumerCount;
   public uint ForwardHiZSkippedPolicyCount;
   public uint ForwardHiZSkippedInvalidBoundsCount;
   public uint ForwardPostHiZCompactionEmittedCount;
   public uint ForwardPostHiZCompactionOverflowCount;
   ```

2. Mirror those fields in:
   - `SceneSubmissionCounterSnapshot`
   - `SceneRenderingData`
   - `RendererDiagnostics`
   - `PerformanceSnapshotWriter`
   - `RendererDiagnosticsSchema`

3. Keep counters frame-lagged:
   - never block on readback
   - use the last completed frame, exactly like existing scene submission counters

4. Rename policy input fields to avoid confusion:

   ```text
   CompletedForwardOcclusionTested
   CompletedForwardOcclusionCulled
   CompletedHiZConsumerAvailable
   CompletedHiZCounterSource
   ```

5. Make `HiZVisibilityPolicy` adaptive logic accept production counters even when `Settings.Diagnostics.GpuMeshletCountersEnabled == false`.

6. Keep diagnostic task-shader counters only as a validation/debug option. Avoid runtime `DeviceWaitIdle` pipeline recreation as part of normal adaptive policy.

### Acceptance criteria

- `HiZPolicyAdaptiveStatus` is never `CountersUnavailable` when production scene-submission counters are available.
- Toggling diagnostic meshlet counters is not required to activate adaptive suppression.
- The snapshot can report whether counters came from:
  - `LegacyTaskShader`
  - `SceneSubmissionPostHiZCompaction`
  - `Unavailable`

---

## Step 3 — Split scene submission into pre-depth and post-Hi-Z stages

**Purpose:** let the compacted path consume current-frame Hi-Z.

### Current problem

`SceneOpaqueCompactionPass` currently happens before `DepthPrePass` and `HiZBuildPass`. It writes:

- compacted opaque forward buffer
- solid depth buffer
- masked depth buffer
- directional shadow buffers
- counters
- indirect dispatch args

Because this pass executes before the Hi-Z pyramid exists, it cannot perform current-frame Hi-Z occlusion.

### Target architecture

Split the current responsibilities:

```text
ScenePreDepthCompactionPass
  before DepthPrePass
  writes:
    - solid depth compacted buffer
    - masked depth compacted buffer
    - directional shadow compacted buffers
    - optional pre-HiZ opaque candidate buffer
    - pre-depth/shadow counters

DepthPrePass
  writes:
    - SceneDepth

HiZBuildPass
  reads:
    - SceneDepth
  writes:
    - HiZPyramid

ForwardVisibilityCompactionPass
  after HiZBuildPass
  reads:
    - original or precompacted opaque candidates
    - HiZPyramid
    - scene buffers
  writes:
    - final visible opaque forward buffer
    - forward indirect dispatch args
    - Hi-Z tested / culled / emitted counters

ForwardPlusPass
  reads:
    - final visible opaque forward buffer
  no longer needs to run per-meshlet Hi-Z in the compacted task shader
```

### Tasks

1. Rename/refactor `SceneOpaqueCompactionPass` into two conceptual units:
   - keep the existing class initially, but introduce separate methods:
     - `ExecuteDepthAndShadowCompaction(...)`
     - `ExecuteForwardCompactionPreHiZ(...)`
   - once stable, split into two pass classes.

2. Add a new pass:
   - `Njulf/Njulf.Rendering/Pipeline/ForwardVisibilityCompactionPass.cs`

3. Add a new shader:
   - `Njulf/Njulf.Shaders/forward_visibility_compact.comp`

4. Add new render-graph resources:
   - `SceneForwardVisibleMeshletDrawBuffer`
   - `SceneForwardVisibleIndirectDispatchBuffer`
   - `SceneForwardVisibilityCounters`

5. Update:
   - `ProductionRenderPipelineDeclaration.cs`
   - `RenderGraphResource.cs`
   - `RenderGraphDiagnosticExporter.cs` if needed
   - render graph inventory/diagnostics tests

6. Pass ordering must become:

   ```text
   ScenePreDepthCompactionPass
   DirectionalShadowPass
   DepthPrePass
   HiZBuildPass
   ForwardVisibilityCompactionPass
   ForwardPlusPass
   ```

   Shadow compaction can remain pre-depth because it does not depend on the camera Hi-Z pyramid.

7. Make `ForwardPlusPass` select the new final visible buffer when `ForwardVisibilityCompactionPass` is active.

### Acceptance criteria

- The compacted forward path has a post-Hi-Z visibility stage.
- `ForwardPlusPass` no longer depends on `forward_compacted.task` pretending to be a visibility stage.
- The render graph shows the new data dependency: `HiZPyramid -> ForwardVisibilityCompactionPass -> ForwardPlusPass`.

---

## Step 4 — Implement Hi-Z testing in the new post-Hi-Z compaction shader

**Purpose:** bring the compacted path up to parity with the legacy forward task shader.

### Tasks

1. Extract shared GLSL helpers from `forward.task` into a shared include:

   - existing logic:
     - `ProjectToUvDepth`
     - `MeshletOccludedByHiZBounds4Tap`
     - `MeshletOccludedByHiZFull6Point5Tap`
   - new file:
     - `Njulf/Njulf.Shaders/hiz_occlusion_shared.glsl`

2. Use the same depth convention:
   - this renderer uses reverse-Z; `hiz_downsample.comp` stores the minimum depth of a 2×2 region.
   - preserve the existing `nearestDepth <= max(farthestOccluder - bias, 0.0)` test unless validation proves it needs correction.

3. The compute shader should:

   ```text
   for each candidate meshlet:
     read draw command
     resolve world bounds / radius
     reject invalid bounds safely
     increment candidate count
     if policy says no Hi-Z:
       append without testing
     else:
       increment tested count
       run selected Hi-Z test mode
       if occluded:
         increment culled count
         return
       append visible draw command
       increment emitted count
       increment indirect dispatch x
   ```

4. Add `GPUForwardVisibilityCompactionPushConstants` in `GPUStructs.cs`:

   ```csharp
   Matrix4x4 ViewProjectionMatrix;
   Matrix4x4 InverseViewMatrix;
   Vector2 ScreenDimensions;
   uint CurrentFrameIndex;
   uint InputDrawBufferBaseIndex;
   uint InputDrawCount;
   uint OutputDrawBufferBaseIndex;
   uint OutputCapacity;
   uint CounterBufferBaseIndex;
   uint IndirectDispatchBufferBaseIndex;
   uint HiZTextureIndex;
   uint HiZMipCount;
   uint HiZTestMode;
   float OcclusionBias;
   uint Flags;
   ```

5. Use the same bindless buffer/image model as the rest of the renderer:
   - storage buffers through `BindlessHeap.StorageBufferSet`
   - Hi-Z pyramid through `BindlessTextures[HiZDepthTexture]`

6. Add overflow handling:
   - if output index exceeds capacity, increment overflow counter and do not write
   - if overflow occurs, force fallback to non-occluded compacted path or direct legacy path on the next frame

7. Keep direct and indirect dispatch support:
   - write `DrawMeshTasksIndirectCommandEXT` compatible arguments
   - do not read the visible count back on CPU for same-frame dispatch

### Acceptance criteria

- In a synthetic occlusion scene, `ForwardHiZTestedCount > 0`.
- In a synthetic occlusion scene, `ForwardHiZCulledCount > 0`.
- In an open scene, cull count may be low, and adaptive suppression must engage after the configured threshold.
- No GPU validation errors.
- Visual output matches the legacy path within expected conservative-occlusion tolerances.

---

## Step 5 — Fix policy economics and hysteresis

**Purpose:** turn Hi-Z from a static feature into a profitability-driven system.

### Tasks

1. Extend `HiZVisibilityPolicyStatus`:

   ```csharp
   Disabled,
   WarmingUp,
   Active,
   Skipped,
   NoConsumer,
   Probing,
   Suppressed,
   ForcedOn
   ```

2. Extend `HiZVisibilityPolicyInput`:

   ```csharp
   bool ForwardConsumerCanUseHiZ;
   bool ProductionCountersAvailable;
   int CompletedForwardHiZCandidateCount;
   int CompletedForwardHiZTestedCount;
   int CompletedForwardHiZCulledCount;
   long CompletedForwardVisibilityCompactionMicroseconds;
   long CompletedForwardOpaqueMicroseconds;
   long CompletedDepthPrePassMicroseconds;
   long CompletedHiZBuildMicroseconds;
   ```

3. Estimate total cost as:

   ```text
   HiZCost = DepthPrePassGpu + HiZBuildGpu + ForwardVisibilityCompactionGpu
   ```

   Do **not** include depth prepass cost when the depth prepass is already required by other active passes.

4. Estimate saved time as:

   ```text
   SavedForwardGpu =
       ForwardOpaqueGpu * (Culled / max(1, Tested - Culled))
   ```

   Keep this as a heuristic; never block on exact timing.

5. Add a smoothing window:
   - 30-frame EMA for cull rate
   - 30-frame EMA for saved/cost ratio
   - 120-frame P95 for Hi-Z build CPU/GPU cost

6. Suppress Hi-Z when all are true for `UnprofitableFrameThreshold` frames:
   - cull rate below `MinUsefulOcclusionCullRate`
   - estimated saved < `MinEstimatedSavedMicroseconds`
   - estimated saved/cost ratio < `MinEstimatedSavedToCostRatio`

7. Probe again after `AdaptiveProbeIntervalFrames`.
   - On a probe frame, build Hi-Z and run post-Hi-Z compaction.
   - Use probe results only after they become available through normal frame-lagged readback.

8. Add a forced-on path:
   - for debugging
   - for known-heavy-occlusion scenes
   - for validation benchmarks

### Acceptance criteria

- Hi-Z suppresses itself in the attached baseline scenario if it continues to reject zero meshlets.
- Hi-Z remains active in synthetic occluder scenes where rejection is materially useful.
- No same-frame readback or CPU/GPU synchronization is introduced.
- Policy reasons in the snapshot explain every state transition.

---

## Step 6 — Reduce Hi-Z build CPU recording overhead

**Purpose:** after gating and post-Hi-Z compaction are correct, reduce the 7.9 ms CPU recording spike when Hi-Z is genuinely useful.

### Current implementation

`HiZBuildPass.Execute(...)`:

- transitions scene depth to read-only
- transitions pyramid to general
- binds the compute pipeline
- loops over every mip
- binds a descriptor set per mip
- pushes constants per mip
- dispatches per mip
- transitions each mip to shader read

This is simple and correct, but the per-mip recording/barrier pattern can be expensive.

### Tasks

1. Add per-phase CPU timing:
   - descriptor bind time
   - push/dispatch time
   - transition/barrier time
   - descriptor-set recreation on swapchain resize

2. Cache immutable per-mip metadata:
   - source extent
   - destination extent
   - dispatch groups
   - descriptor set handle

3. Replace per-mip descriptor sets with descriptor arrays if the current bindless infrastructure supports it:
   - one descriptor set for all mips
   - mip index in push constants
   - fewer descriptor binds per frame

4. Batch barriers:
   - one depth-to-read-only transition
   - one pyramid-to-general transition for all writable subresources
   - one final image barrier for the whole pyramid after all dispatches
   - only keep per-mip barriers if required for read-after-write between mip levels

5. Consider a fused pyramid shader:
   - first pass builds multiple lower mips using shared memory
   - later small mips can be built in a single dispatch
   - this reduces command count and CPU overhead

6. Evaluate async compute:
   - `HiZBuildPass` already declares compute queue intent and async support
   - enable only when graphics/compute queue ownership barriers are correct
   - verify overlap in GPU timestamps/Nsight before defaulting on

### Acceptance criteria

- When Hi-Z is active, CPU recording cost for the Hi-Z build path is below 0.5 ms on the development profile.
- GPU Hi-Z cost is reported separately from CPU recording cost.
- Async compute is only enabled when it improves end-to-end frame time, not merely pass-local time.

---

## Step 7 — Preserve correctness with robust fallback modes

**Purpose:** make the new path safe enough for production.

### Tasks

1. Add `RenderSettings` controls:

   ```csharp
   public sealed class VisibilitySettings
   {
       public bool HiZEnabled { get; set; } = true;
       public bool AdaptiveHiZEnabled { get; set; } = true;
       public bool HiZPostCompactionEnabled { get; set; } = false; // enable after validation
       public bool ForceHiZProbe { get; set; }
       public bool ForceLegacyHiZTaskShader { get; set; }
       public bool ValidatePostHiZCompaction { get; set; }
   }
   ```

   Current global toggles `EnableHiZOcclusion` and `EnableAdaptiveHiZOcclusion` can be kept as renderer-level overrides, but production settings should live under `RenderSettings`.

2. Fallback hierarchy:

   ```text
   PostHiZCompaction valid:
     use post-HiZ compacted indirect path

   PostHiZCompaction overflow or validation failed:
     use pre-HiZ compacted path without occlusion

   Compaction unavailable:
     use legacy forward task shader path

   Forced debug:
     use legacy forward.task Hi-Z path for comparison
   ```

3. On overflow:
   - record diagnostics
   - force fallback next frame
   - avoid drawing from partially invalid buffers

4. On invalid resource state:
   - skip post-HiZ compaction
   - report `ForwardVisibilityCompactionSkipReason`

5. Do not make Hi-Z required for correctness. It must remain a performance feature.

### Acceptance criteria

- Feature can be disabled at runtime without device loss.
- Overflow never corrupts draws.
- Any failure falls back to a visible, conservative rendering path.

---

## Step 8 — Extend diagnostics and snapshots

**Purpose:** make future regressions obvious.

### Add snapshot fields

In `SceneRenderingData`, `RendererDiagnostics`, `RendererDiagnosticsSchema`, and `PerformanceSnapshotWriter`, add:

```text
HiZConsumerSummary
HiZConsumerCount
HiZBuildSkippedBecauseNoConsumer
HiZBuildCpuRecordMicroseconds
HiZBuildGpuMicroseconds
HiZPolicyState
HiZPolicyReason
HiZCounterSource
HiZEstimatedSavedMicroseconds
HiZEstimatedCostMicroseconds
HiZEstimatedNetMicroseconds
HiZEstimatedSavedToCostRatio
HiZSuppressedFrameCount
HiZProbeFrame
ForwardHiZCandidateCount
ForwardHiZTestedCount
ForwardHiZCulledCount
ForwardHiZCullRate
ForwardHiZInvalidBoundsCount
ForwardVisibilityCompactionGpuMicroseconds
ForwardVisibilityCompactionOverflowCount
ForwardVisibilityCompactionSkipReason
```

### Add warning logic

Warn when:

```text
HiZBuildEnabled == true && HiZConsumerCount == 0
HiZBuildEnabled == true && ForwardHiZTestedCount == 0 for N completed frames
HiZPolicyAdaptiveStatus == CountersUnavailable while production counters are expected
ForwardVisibilityCompactionOverflowCount > 0
HiZEstimatedNetMicroseconds < 0 for N completed frames
```

### Acceptance criteria

- A new snapshot immediately answers:
  - Was Hi-Z built?
  - Who consumed it?
  - How many meshlets were tested?
  - How many were culled?
  - Was it profitable?
  - Why did the policy decide to enable/suppress/probe?

---

## Step 9 — Tests and validation

### Unit tests

Add or extend tests under `Njulf/Njulf.Tests`:

1. `HiZVisibilityPolicyTests.cs`
   - no consumer -> skipped
   - legacy consumer -> active after warmup
   - post-HiZ compaction consumer -> active after warmup
   - low cull rate -> suppressed
   - high cull rate and positive net -> active
   - suppressed -> probe after interval
   - camera cut -> warmup

2. `SceneSubmissionDiagnosticsPolicyTests.cs`
   - forward path string reflects post-HiZ compaction
   - skip reasons are stable and human-readable

3. `GPUStructLayoutTests.cs`
   - `GPUForwardVisibilityCompactionPushConstants`
   - extended `GPUSceneSubmissionCounters`
   - C# / GLSL struct sizes and offsets match

4. `ProductionRenderPipelineDeclarationTests.cs`
   - `ForwardVisibilityCompactionPass` appears after `HiZBuildPass`
   - `ForwardPlusPass` appears after `ForwardVisibilityCompactionPass`

5. `RenderGraphResourceDeclarationTests.cs`
   - resources have correct read/write access and barriers
   - Hi-Z pyramid dependency is present

### GPU/integration tests

1. **No-consumer baseline**
   - compacted forward path active
   - post-HiZ compaction disabled
   - Hi-Z must skip
   - no visual change

2. **Occluder wall synthetic scene**
   - post-HiZ compaction enabled
   - many meshlets hidden
   - `ForwardHiZCulledCount > 0`
   - frame time improves or policy remains active

3. **Open courtyard scene**
   - low occlusion
   - policy suppresses after threshold
   - periodic probe still runs

4. **Camera cut / teleport**
   - policy warms up
   - no stale pyramid false positives

5. **Animation/skinning scene**
   - moving objects do not cause aggressive false occlusion
   - conservative bounds are respected

6. **Validation mode**
   - compare post-HiZ compaction output against legacy path for a bounded sample
   - do not require exact equality where occlusion intentionally removes work; compare visible output and conservative no-false-positive rules

### Acceptance criteria

- Unit tests pass.
- GPU validation layers remain clean.
- Synthetic occluder scene produces nonzero culling.
- Open scene suppresses wasted Hi-Z.
- Attached baseline no longer records ~7.9 ms CPU Hi-Z build when the compacted path has no Hi-Z consumer.

---

## Step 10 — Rollout plan

### PR 1 — No-consumer gate and diagnostics

- Add consumer-aware policy input.
- Add `HiZConsumerSummary`.
- Skip Hi-Z when compacted forward cannot consume it.
- Add unit tests.
- Expected immediate impact on attached snapshot: remove most of the 7.903 ms CPU Hi-Z build record cost.

### PR 2 — Production counters

- Extend scene submission counters.
- Feed policy from production counter readback.
- Stop relying on diagnostic task shader variants for adaptive decisions.
- Add snapshot fields and tests.

### PR 3 — Post-Hi-Z forward compaction prototype

- Add `ForwardVisibilityCompactionPass`.
- Add compute shader.
- Keep disabled by default behind `HiZPostCompactionEnabled`.
- Add synthetic occlusion validation scene.

### PR 4 — Default-on for validation builds

- Enable post-Hi-Z compaction for development/validation profiles.
- Collect 120-frame baselines.
- Tune thresholds.

### PR 5 — Hi-Z build recording optimization

- Reduce per-mip CPU overhead.
- Batch barriers/descriptors.
- Evaluate fused pyramid shader if necessary.

### PR 6 — Production default

- Enable adaptive post-Hi-Z compaction by default only when:
  - counters are valid
  - no validation failures
  - no overflows
  - policy suppresses unprofitable scenes
  - snapshot warnings are clean

---

## 4. Engineering risks and mitigations

| Risk | Mitigation |
|---|---|
| False occlusion hides visible meshlets | Conservative bounds, warmup frames, validation scenes, legacy fallback, compare captures. |
| Same-frame readback stalls CPU | Use only frame-lagged counters and timestamps. |
| Post-Hi-Z compaction duplicates work | Keep pre-depth/shadow compaction separate; only compact forward visibility after Hi-Z. |
| Hi-Z build GPU cost exceeds savings | Policy compares saved/cost ratio and suppresses. |
| Async compute worsens frame time | Enable only after timestamp/Nsight validation. |
| Runtime pipeline recreation stalls | Keep diagnostic variants out of production policy; avoid `DeviceWaitIdle` in normal toggles. |
| Counter overflow corrupts dispatch | Use capacity checks, overflow counters, and fallback next frame. |
| Diagnostics become too noisy | Report summary fields and only warn on sustained N-frame issues. |

---

## 5. Definition of done

The Hi-Z/occlusion work is production-ready when all of the following are true:

1. The attached baseline scenario no longer builds Hi-Z when the compacted path cannot consume it.
2. Post-Hi-Z forward compaction is available and validated.
3. Occlusion counters are production counters, not diagnostic-only counters.
4. The adaptive policy can suppress unprofitable Hi-Z without manual toggles.
5. Synthetic occlusion scenes show real `ForwardHiZCulledCount`.
6. Open/no-occlusion scenes suppress Hi-Z after a short measurement period.
7. GPU timestamps and CPU record timings are cleanly reported in performance snapshots.
8. No same-frame CPU readback is introduced.
9. Validation-layer runs are clean.
10. Fallback paths are tested and documented.
11. Render graph diagnostics show the correct dependency chain.
12. The system is controlled by stable `RenderSettings` flags, not hidden renderer fields.

---

## 6. Expected impact

### Immediate fix after PR 1

For the attached snapshot, the expected immediate win is removal of the wasted Hi-Z build record cost:

```text
CpuHiZBuildRecordMicroseconds: ~7903 -> near 0 when no consumer exists
```

This does not depend on solving DDGI or implementing new GPU occlusion compaction. It simply prevents the renderer from paying for an unused pyramid.

### Full fix after PR 3–6

For scenes with meaningful occlusion, the renderer should:

```text
DepthPrePass -> HiZBuild -> ForwardVisibilityCompaction -> ForwardPlus
```

and report:

```text
ForwardHiZTestedCount > 0
ForwardHiZCulledCount > 0
HiZEstimatedNetMicroseconds > 0
HiZPolicyState = Active
```

For scenes without meaningful occlusion, the renderer should report:

```text
HiZPolicyState = Suppressed or NoConsumer
HiZBuildEnabled = false except probe frames
ForwardHiZCulledCount = 0
HiZEstimatedNetMicroseconds <= 0
```

---

## 7. File checklist

### Core policy/data

- `Njulf/Njulf.Rendering/Data/HiZVisibilityPolicy.cs`
- `Njulf/Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf/Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf/Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf/Njulf.Rendering/Data/GPUStructs.cs`

### Renderer orchestration

- `Njulf/Njulf.Rendering/VulkanRenderer.cs`
- `Njulf/Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf/Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf/Njulf.Rendering/Pipeline/RenderGraphResource.cs`

### Passes

- `Njulf/Njulf.Rendering/Pipeline/SceneOpaqueCompactionPass.cs`
- `Njulf/Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf/Njulf.Rendering/Pipeline/ForwardVisibilityCompactionPass.cs` *(new)*
- `Njulf/Njulf.Rendering/Pipeline/ForwardPlusPass.cs`

### Shaders

- `Njulf/Njulf.Shaders/hiz_downsample.comp`
- `Njulf/Njulf.Shaders/forward.task`
- `Njulf/Njulf.Shaders/forward_compacted.task`
- `Njulf/Njulf.Shaders/scene_opaque_compact.comp`
- `Njulf/Njulf.Shaders/hiz_occlusion_shared.glsl` *(new)*
- `Njulf/Njulf.Shaders/forward_visibility_compact.comp` *(new)*

### Diagnostics

- `Njulf/Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`
- `Njulf/Njulf.Rendering/Diagnostics/RendererDiagnosticsSchema.cs`
- `Njulf/Njulf.Rendering/Diagnostics/RenderGraphDiagnosticExporter.cs`

### Tests

- `Njulf/Njulf.Tests/HiZVisibilityPolicyTests.cs` *(new or renamed from existing adaptive policy tests)*
- `Njulf/Njulf.Tests/GPUStructLayoutTests.cs`
- `Njulf/Njulf.Tests/ProductionRenderPipelineDeclarationTests.cs`
- `Njulf/Njulf.Tests/RenderGraphResourceDeclarationTests.cs`
- `Njulf/Njulf.Tests/SceneSubmissionDiagnosticsPolicyTests.cs`
- `Njulf/Njulf.Tests/SampleBenchmarkAnalyzerTests.cs`

---

## 8. Priority summary

1. **Fix the gating bug first.** Do not build Hi-Z if compacted forward cannot use it.
2. **Make counters production-grade.** Adaptive policy must not rely on diagnostic variants.
3. **Split compaction.** Current compaction runs too early to use current-frame Hi-Z.
4. **Add post-Hi-Z forward compaction.** This is the real production path.
5. **Optimize Hi-Z build recording.** Only after the system is correct and useful.
6. **Enable async compute only after evidence.** Timestamp/Nsight validation first.
