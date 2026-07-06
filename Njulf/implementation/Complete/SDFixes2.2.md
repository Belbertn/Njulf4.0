# SDF re-evaluation: architecture verdict + instrumentation plan to pin the content bug

## Context

The debug-view split (DDGI slice vs full slice) is fixed and working. The full-slice view now reveals a real SDF defect: at spawn the field reads near-surface (green) everywhere — correct. After moving the camera a few meters left, large wall faces read uniform saturated **blue** (≥ +12 voxels, i.e. "no surface within meters") and floor plates read uniform **red** (deeply negative / "inside"), with green rims at edges. Steady-state diagnostics show `executed=0:'no-dirty-bricks', backlog=0, bricks=0` — but all captured lines are from *standing still*; the transition frames (where scroll → invalidate → regen happens) were never captured.

## Re-evaluation: what is verified correct (no refactor needed)

I audited the whole path; the architecture is sound and internally consistent:

- **Clipmap scroll bookkeeping** (`GlobalSdfManager.UpdateClipmap`, `InvalidateChangedPhysicalBricks`): compares *absolute* logical cells per physical brick, marks changed bricks priority-dirty. Correct.
- **Ring addressing** is consistent across all three sides: CPU (`GetLogicalCellForPhysicalBrick`, physical = x + y·B + z·B²), compute-shader write (`global_sdf_update.comp:158-165, 224-225, 262` — writes physical texel, computes world pos from logical brick), and sampling (`global_sdf.glsl:79` toroidal shift; verified exact: texel written for logical voxel L lands where the sampler reads it).
- **Sampler**: bindless volume textures use the default Linear+**Repeat** sampler (`BindlessHeap.cs:244-246`), which the toroidal shift requires. Correct.
- **Push constants**: `GlobalSdfPasses.CreatePushConstants` forwards the job's `RingOffset`, `LogicalGridMin`, `WorldMin`, `Resolution`, `BricksPerAxis` correctly.
- **Metadata**: rebuilt+uploaded every frame even on the `no-dirty-bricks` early-out path (upload happens before that return).

Conclusion: **do not refactor the clipmap/brick machinery.** The failure is in *content* (what regenerated bricks contain, or which meshes feed them), or in a transition-time scheduling gap that steady-state diagnostics can't see.

## What the failure signature implies

For a surface pixel to render saturated blue in the full view, the **minimum |distance| across all four cascades** must exceed ~12 voxels — so cascade 0/1 content is wrong there *and* cascade 2/3 is wrong or coarse-saturated too. The broken region is exactly the newly-entered (left) part of the world; the right side (cells that never left the clipmap window since the initial full build) still looks correct. Top hypotheses, in order:

1. **Bricks regenerated after scroll come out empty** (`candidateCount==0` → written as `maxExtent` = +32 voxels ⇒ uniform blue): mesh-SDF candidate list wrong/unavailable at regen time. Note `PrepareInstanceRecords` silently skips instances with no baked SDF record (`MeshSdfManager.cs:153-157`) — only 40 records are active and the skipped count is not in the diag line.
2. **Scroll happened but invalidation/consumption missed bricks** on some frames (e.g. pass skipped via `ResolveSkipReason` — accel-structure-inactive — during movement, or a gap between `MarkAllDirty` from `FastCameraMovement` and drain).
3. **Mesh SDF distance quality** for heavily non-uniform-scaled boxes (`SampleMeshSdf` directional scale / unsigned-fallback sheets) ⇒ inflated negative interiors (the red plates).

## Plan

### 1. Instrumentation (small, decisive — the "more counters" the user asked about)

- **CPU scroll telemetry** (`GlobalSdfManager` → `SceneRenderingData` → SDF diag line): per frame, summed over cascades (plus per-cascade for cascade 0): scroll delta cells, bricks invalidated by scroll, priority/dirty consumed, backlog. Catches hypothesis 2 directly.
- **GPU regen counters** in `global_sdf_update.comp` via the existing `AddRendererDiagnostic` mechanism (one write per brick from `gl_LocalInvocationIndex==0`): `bricksWrittenEmpty` (candidateCount==0) and `bricksWrittenWithCandidates`. Catches hypothesis 1 directly.
- **Mesh coverage in the SDF diag line**: append `bakedMeshes/pendingBakes` (already on sceneData: `MeshSdfBakedMeshCount`, `MeshSdfPendingBakeCount`) and plumb `MeshSdfManager.LastFrameSkippedInstanceSdfCount` into sceneData/diagnostics (`skippedInstances=`). If skipped > 0, some visible static meshes never reach the global SDF.
- **Cascade-isolation debug views**: extend the full-slice debug path so a single cascade can be isolated (add `GlobalSdfCascade0..3` views 124-127, mirroring the existing enum/shader/cycling pattern from the GlobalSdfFullSlice work; sample *only* that cascade, render `Valid==false` as dark grey, signed distance as the existing green/blue/red ramp). This is the killer tool: after moving left, cycling cascades shows instantly which cascade is broken and whether it looks *empty* (flat blue), *shifted* (structured garbage), or *out-of-window* (dark).

### 2. Capture protocol (user-side, one session)

1. From spawn: cycle isolated cascades 0-3 — all should read near-zero at surfaces.
2. Move ~5 m left **while diagnostics stream**, stop, wait 2 s.
3. Save: diag lines *during* movement (scrolled/invalidated/consumed/emptyWritten) and after; screenshots of isolated cascade 0 and 1.

### 3. Decision tree → targeted fix (next session, based on data)

- Scroll delta ≠ 0 but invalidated == 0 → bug in scroll→invalidate trigger; fix there.
- Invalidated > 0 but consumed == 0 → scheduling bug in `SelectDirtyBrickJobs`; fix there.
- Consumed > 0 and `bricksWrittenEmpty` ≈ consumed → candidate/instance availability at regen: make instance records robust during accel rebuild (fall back to last-known-good list) and re-mark bricks dirty when `activeMeshSdfCount` collapses; if `skippedInstances > 0`, get those meshes baked/recorded.
- Consumed > 0, written non-empty, isolated cascade still wrong → GPU-side content bug at ring≠0; add the CPU-side round-trip unit test below to bisect.
- Only red plates remain → tune `SampleMeshSdf` non-uniform-scale/unsigned-fallback handling.

### 4. Hardening worth doing regardless (cheap)

- `MarkAllDirty()` (triggered every frame by `FastCameraMovement`) clears `_priorityDirtyBricks`, so post-movement drain runs in physical-scan order instead of nearest-camera-first. Keep nearest-first drain when a full-dirty backlog exists so the visible region converges first.
- Unit test (`Njulf.Tests`): simulate `UpdateClipmap` scrolls + job selection, CPU-emulate the compute shader's physical→logical→world mapping, and assert (a) every regenerated brick's world region equals a newly-entered window cell and (b) unchanged + regenerated bricks exactly tile the window. This locks the ring math against regressions without a GPU.

## Branch note

The latest work (including the debug-view ordering fix) lives on `origin/Simplified` (`44fbc16`), one commit ahead of my designated branch. Implementation starts by resetting `claude/sdf-debug-views-issue-9x3kau` onto `origin/Simplified` so the fix is included.

## Files to touch

- `Njulf/Njulf.Rendering/Resources/GlobalSdfManager.cs` — scroll telemetry fields, nearest-first drain.
- `Njulf/Njulf.Shaders/global_sdf_update.comp` — empty/non-empty brick counters.
- `Njulf/Njulf.Shaders/forward.frag` + `RenderSettings.cs` + `VulkanRenderer.cs` + `GlobalIlluminationPassExecutionPolicy.cs` + `SampleInputController.cs` — cascade-isolation debug views (same pattern as the GlobalSdfFullSlice commit).
- `Njulf/Njulf.Rendering/Data/SceneRenderingData.cs`, `RendererDiagnostics.cs`, `Njulf/NjulfHelloGame/SampleDiagnosticsReporter.cs` — new counters in the SDF diag line.
- `Njulf/Njulf.Tests/` — clipmap scroll round-trip test; extend `ShaderBuildTests` for new debug views.

## Verification

- `dotnet test` (ShaderBuildTests compile the .comp/.frag changes; new scroll round-trip test).
- User runs the capture protocol above; the new counters + cascade isolation determine which branch of the decision tree we take.