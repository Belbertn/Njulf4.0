# Revised Hi-Z / Occlusion Production-Readiness Plan

**Date:** 2026-06-29  
**Target branch:** `Simplified`  
**Goal:** make Hi-Z occlusion production-ready and performance-positive after the compacted scene-submission path started consuming Hi-Z.

This plan replaces the broad original plan with only the remaining relevant work. The original "compacted forward path cannot consume Hi-Z" issue is no longer the primary state: `SceneOpaqueCompactionPass` now samples a previous valid Hi-Z pyramid, rejects opaque meshlets before forward, and reports tested/rejected counters. The remaining work is to make that path robust, measurable, adaptive, and eventually replace previous-frame occlusion with a current-frame post-Hi-Z visibility stage.

---

## Current State

Already handled:

- Scene-submission GPU compaction is treated as a Hi-Z consumer.
- `SceneOpaqueCompactionPass` binds the Hi-Z texture set and samples Hi-Z.
- Scene-submission Hi-Z tested/rejected counters are read back and mapped into forward occlusion diagnostics.
- The compaction shader uses previous Hi-Z camera matrices instead of current camera projection against previous depth.
- Previous-pyramid usage is gated by scene/camera/pyramid validity and uses conservative screen padding.
- Compaction skips unsafe blend-material Hi-Z rejection.
- Render graph declarations model `SceneOpaqueCompactionPass` reading `HiZPyramid`.

Still relevant:

- Adaptive policy still depends on `MeshletDiagnosticCountersActive`, so production scene-submission counters can exist while the policy reports `CountersUnavailable`.
- Diagnostics do not explicitly report Hi-Z consumer summary or counter source.
- The current compaction path uses previous-frame Hi-Z. This is conservative and useful, but not the final current-frame production architecture.
- Hi-Z build CPU recording cost may still be high when the feature is active.
- Fallback and validation behavior around false occlusion, overflows, and unprofitable scenes should be explicit.

---

## Step 1 - Make Production Counters Drive Adaptive Policy

**Purpose:** remove the remaining dependency on diagnostic meshlet counter variants.

### Tasks

1. Add a small counter-source model:

   ```csharp
   public enum HiZCounterSource
   {
       Unavailable,
       LegacyTaskShader,
       SceneSubmissionCompaction
   }
   ```

2. In `VulkanRenderer`, resolve the completed Hi-Z counter source each frame:

   - `SceneSubmissionCompaction` when scene-submission GPU compaction is enabled and completed scene-submission counters are valid.
   - `LegacyTaskShader` when diagnostic/legacy forward task counters are active and valid.
   - `Unavailable` otherwise.

3. Change `HiZVisibilityPolicyInput`:

   - Replace or supplement `MeshletCountersActive` with:

     ```csharp
     bool ProductionCountersAvailable;
     HiZCounterSource CounterSource;
     int CompletedHiZTested;
     int CompletedHiZCulled;
     ```

4. Update `HiZVisibilityPolicy.UpdateAdaptiveState(...)`:

   - Use production scene-submission counters when available.
   - Keep diagnostic task shader counters only as a fallback source.
   - Return `CountersUnavailable` only when both sources are unavailable.

5. Update tests:

   - Scene-submission counters available with diagnostics disabled -> adaptive status is not `CountersUnavailable`.
   - Legacy counters available -> policy still works.
   - No counters -> `CountersUnavailable`.

### Acceptance Criteria

- Adaptive policy can suppress or keep Hi-Z active using scene-submission counters with diagnostic meshlet counters disabled.
- Runtime pipeline recreation for diagnostic counter variants is not part of normal adaptive policy.
- Snapshot/policy diagnostics identify the counter source.

---

## Step 2 - Add Consumer and Counter Diagnostics

**Purpose:** make future "Hi-Z was built but did nothing" regressions obvious from a snapshot.

### Tasks

1. Add diagnostic fields to `SceneRenderingData`, `RendererDiagnostics`, `RendererDiagnosticsSchema`, and `PerformanceSnapshotWriter`:

   ```text
   HiZConsumerCount
   HiZConsumerSummary
   HiZBuildSkippedBecauseNoConsumer
   HiZCounterSource
   ForwardHiZTestedCount
   ForwardHiZCulledCount
   ForwardHiZCullRate
   PreviousHiZFrameValid
   ```

2. Implement a helper in `VulkanRenderer`:

   ```csharp
   private HiZConsumerDecision ResolveHiZConsumers(SceneRenderingData sceneData, HiZVisibilityPolicyDecision decision)
   ```

   Report consumers separately:

   - `SceneSubmissionPreviousHiZ`
   - `LegacyForwardTask`
   - `Foliage`
   - `Ssgi`

3. Preserve current gating behavior, but improve the reason strings:

   - No consumer: `"No active Hi-Z consumers for this frame."`
   - Warmup: include whether a consumer exists but occlusion is gated.
   - Previous-pyramid invalid: report that compaction is waiting for a valid previous pyramid.

4. Add warnings in snapshot/debug tooling:

   - `HiZBuildEnabled == true && HiZConsumerCount == 0`
   - `HiZBuildEnabled == true && HiZCounterSource == Unavailable` for sustained frames
   - `OcclusionCullingEnabled == true && ForwardHiZTestedCount == 0` for sustained frames

### Acceptance Criteria

- A snapshot answers who consumed Hi-Z, how many meshlets were tested, how many were culled, and which counter path supplied the data.
- The old baseline failure mode is visible as a diagnostic warning if it ever returns.

---

## Step 3 - Harden Previous-Frame Scene-Submission Occlusion

**Purpose:** keep the current performance win while preventing visual instability.

### Tasks

1. Keep previous-frame Hi-Z compaction behind explicit validity gates:

   - previous pyramid valid
   - previous camera matrices valid
   - no scene change
   - no camera cut
   - not warming up

2. Add configurable safety settings under `RenderSettings`:

   ```csharp
   public sealed class HiZOcclusionSettings
   {
       public bool SceneSubmissionPreviousFrameCullingEnabled { get; set; } = true;
       public int PreviousFrameUvPaddingPixels { get; set; } = 8;
       public float OcclusionBias { get; set; } = 0.0005f;
       public bool DisablePreviousFrameCullingDuringFastCameraMotion { get; set; } = true;
   }
   ```

3. Replace hard-coded shader padding with a push-constant value.

4. Add camera-motion suppression:

   - Use camera position/forward delta.
   - Disable previous-frame Hi-Z rejection for one or more frames when motion exceeds a conservative threshold.
   - Still allow Hi-Z build during suppression so the next frame has a valid pyramid.

5. Add validation/debug counters:

   ```text
   PreviousHiZSkippedInvalidHistory
   PreviousHiZSkippedCameraMotion
   PreviousHiZTested
   PreviousHiZCulled
   ```

### Acceptance Criteria

- Moving the camera quickly does not produce missing-meshlet flashes.
- Previous-frame occlusion can be disabled independently without disabling Hi-Z build or SSGI users.
- Conservative settings are tunable without shader edits.

---

## Step 4 - Improve Policy Economics

**Purpose:** make Hi-Z performance-driven instead of merely feature-driven.

### Tasks

1. Extend policy status values:

   ```csharp
   Disabled,
   WarmingUp,
   Active,
   Skipped,
   NoConsumer,
   Suppressed,
   Probing,
   ForcedOn
   ```

2. Track separate cost inputs:

   ```text
   CompletedDepthPrePassMicroseconds
   CompletedHiZBuildMicroseconds
   CompletedSceneSubmissionCompactionMicroseconds
   CompletedForwardOpaqueMicroseconds
   ```

3. Estimate cost carefully:

   - Include Hi-Z build cost.
   - Include incremental compaction cost only when measurable.
   - Include depth-prepass cost only when depth prepass is not already required by other active features.

4. Add smoothing:

   - 30-frame EMA for cull rate.
   - 30-frame EMA for estimated saved/cost ratio.
   - Track sustained unprofitable frames before suppression.

5. Probe after suppression:

   - Build Hi-Z and run culling on probe frames.
   - Consume results through normal frame-lagged readback.
   - Never force same-frame readback.

6. Add a forced-on mode for validation and benchmark scenes.

### Acceptance Criteria

- Open/no-occlusion scenes suppress Hi-Z after the configured threshold.
- Occlusion-heavy scenes keep Hi-Z active.
- Policy reasons explain active, suppressed, probing, no-consumer, and warmup states.
- No same-frame GPU readback is introduced.

---

## Step 5 - Add Current-Frame Forward Visibility Compaction

**Purpose:** move from previous-frame occlusion to a final production architecture when the policy/counter foundation is stable.

### Target Order

```text
SceneOpaqueCompactionPass
  pre-depth and shadow compaction

DepthPrePass
  writes SceneDepth

HiZBuildPass
  builds current-frame HiZPyramid

ForwardVisibilityCompactionPass
  reads current-frame HiZPyramid
  writes final visible forward buckets and indirect dispatch args

ForwardPlusPass
  draws final visible compacted buckets
```

### Tasks

1. Split `SceneOpaqueCompactionPass` responsibilities conceptually first:

   - depth/shadow compaction stays before depth
   - forward visibility compaction moves after Hi-Z

2. Add:

   - `ForwardVisibilityCompactionPass.cs`
   - `forward_visibility_compact.comp`
   - render graph resources for final visible forward buffers, counters, and indirect dispatch args

3. Reuse the existing material buckets:

   - simple opaque
   - simple-normal opaque
   - full opaque

4. Move Hi-Z rejection from previous-frame scene compaction into the new current-frame pass when enabled.

5. Keep previous-frame compaction as a fallback/probe path until current-frame compaction is validated.

6. Add overflow handling:

   - increment overflow counters
   - avoid drawing partially invalid buffers
   - fall back next frame to non-occluded compacted path

### Acceptance Criteria

- Render graph shows `HiZPyramid -> ForwardVisibilityCompactionPass -> ForwardPlusPass`.
- Current-frame synthetic occluder scenes report nonzero tested and culled counts.
- Visual output is stable during camera motion.
- Overflow cannot corrupt forward draws.

---

## Step 6 - Optimize Hi-Z Build Recording Cost

**Purpose:** reduce CPU cost when Hi-Z is genuinely useful.

### Tasks

1. Add detailed CPU timing inside `HiZBuildPass`:

   ```text
   depth transition time
   pyramid transition time
   descriptor bind time
   push/dispatch time
   final barrier time
   ```

2. Cache per-mip immutable metadata:

   - source extent
   - destination extent
   - dispatch group counts
   - descriptor handles

3. Batch barriers where valid:

   - one depth read-only transition
   - one pyramid writable transition
   - final shader-read transition
   - keep required per-mip read-after-write barriers only where needed

4. Evaluate descriptor-array or bindless mip access to reduce per-mip descriptor binds.

5. Evaluate a fused pyramid shader only after simpler recording optimizations are measured.

6. Evaluate async compute only with timestamp/Nsight evidence of end-to-end improvement.

### Acceptance Criteria

- Active Hi-Z CPU record cost is below 0.5 ms on the development profile, or the remaining cost is explained by timing breakdown.
- GPU Hi-Z build time remains separately reported.
- Async compute is defaulted on only if full-frame time improves.

---

## Step 7 - Fallback and Validation Modes

**Purpose:** keep Hi-Z a performance feature, never a correctness dependency.

### Tasks

1. Add stable render settings:

   ```csharp
   public sealed class HiZOcclusionSettings
   {
       public bool Enabled { get; set; } = true;
       public bool AdaptiveEnabled { get; set; } = true;
       public bool PreviousFrameSceneSubmissionEnabled { get; set; } = true;
       public bool CurrentFrameForwardVisibilityEnabled { get; set; } = false;
       public bool ForceOn { get; set; }
       public bool ForceProbe { get; set; }
       public bool ValidateAgainstLegacyPath { get; set; }
   }
   ```

2. Define fallback hierarchy:

   ```text
   current-frame visibility compaction valid:
     use current-frame visible buffers

   current-frame visibility compaction unavailable/overflowed:
     use previous-frame scene-submission Hi-Z if valid

   previous-frame Hi-Z invalid/disabled:
     use compacted path without Hi-Z occlusion

   compaction unavailable:
     use legacy forward path
   ```

3. Add validation scenes:

   - occluder wall
   - open courtyard
   - fast camera pan
   - teleport/camera cut
   - animated/skinned objects

4. Add automated smoke checks where possible:

   - no validation-layer errors
   - no counter overflows
   - no sustained build-with-zero-tested warning

### Acceptance Criteria

- Any Hi-Z path can be disabled without device loss.
- Overflow or invalid history falls back to visible rendering.
- Validation scenes cover both high-occlusion and no-occlusion cases.

---

## Step 8 - Benchmark and Rollout

**Purpose:** default the feature only after it proves value.

### Tasks

1. Capture 120-frame snapshots for:

   - current baseline scene
   - synthetic occluder scene
   - open/no-occlusion scene
   - fast camera motion scene

2. Record median, P95, and max for:

   ```text
   CPU frame
   CPU Hi-Z build record
   GPU depth prepass
   GPU Hi-Z build
   GPU scene submission compaction
   GPU forward opaque
   Hi-Z tested
   Hi-Z culled
   estimated saved/cost/net
   ```

3. Rollout sequence:

   - production counters and diagnostics
   - hardened previous-frame compaction on by default for validation builds
   - current-frame visibility compaction behind setting
   - Hi-Z build recording optimization
   - production default only after warning-free validation

### Acceptance Criteria

- Occlusion-heavy scenes show positive net savings.
- Open scenes suppress or skip Hi-Z.
- Fast camera motion remains visually stable.
- Snapshot warnings are clean before production default.

---

## Priority Order

1. Production counter source and adaptive policy.
2. Consumer/counter diagnostics.
3. Harden/tune previous-frame scene-submission Hi-Z.
4. Policy economics and suppression/probing.
5. Current-frame forward visibility compaction.
6. Hi-Z build recording optimization.
7. Fallback/validation scenes and rollout gates.

