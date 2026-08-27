# Alpha-Correct Motion Vectors Implementation Plan

Last updated: 2026-08-24

Design reference: reverted commit `65c3dfb97bfd3bee2c53b4c55d8dac743ceac8c5`. Reimplement only the motion-vector correctness work against the current tree; do not cherry-pick the commit because its implementation is coupled to depth-prepass fusion and unrelated renderer features.

## 1. Required outcome

The standalone `MotionVectorPass` must reproduce the visible depth/forward coverage of solid, masked, double-sided, mirrored, and authored-foliage geometry.

Alpha-tested holes must preserve the visible background's velocity and directional-shadow receiver signature. Covered pixels must receive the foreground velocity and signature. The repair must retain existing first-frame, camera-cut, history-consumer, and render-graph behavior.

## 2. Chosen architecture

Keep motion vectors in their existing standalone pass after `DepthPrePass`.

Use the already-populated solid and masked depth draw queues as the authoritative coverage partition:

1. Draw `SolidMeshletCount` from `BindlessIndex.SolidDepthMeshletDrawBufferBase` with the solid motion pipeline.
2. Draw `MaskedMeshletCount` from `BindlessIndex.MaskedDepthMeshletDrawBufferBase` with a new masked motion pipeline.
3. Draw the existing authored-foliage motion tail.

Do not write ordinary motion vectors from `DepthPrePass`, add a motion attachment to that pass, move previous-frame state into it, or add `foliage_depth_motion.frag`. The reverted fusion regressed performance, especially in Sponza, and must be evaluated separately from this correctness repair.

## 3. Implementation changes

### 3.1 Mesh motion pipelines

In `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs`:

- Keep the existing solid motion pipeline and add a `_maskedMotionVectorPipeline` field plus a `MaskedMotionVectorPipeline` property.
- Change the solid motion pipeline to `CullModeFlags.None`. Its fragment shader will enforce the material's data-driven sidedness, matching the solid depth prepass.
- Create the masked pipeline with:
  - `motion_vector.task.spv`;
  - `motion_vector_alpha.mesh.spv`;
  - `motion_vector_alpha.frag.spv`;
  - motion-vector color format;
  - read-only depth (`depthWriteEnable: false`);
  - blending disabled;
  - `CullModeFlags.None`.
- Give both pipelines unambiguous debug names.
- Destroy and reset the masked pipeline wherever the existing motion pipeline is destroyed so recreation and disposal remain symmetrical.

No new pipeline layout or push-constant ABI is required.

### 3.2 Standalone pass draw selection

In `Njulf.Rendering/Pipeline/MotionVectorPass.cs`:

- Replace the three draws over the simple, simple-normal, and full opaque forward buckets with the solid and masked depth queues listed above.
- Change the scene draw helper to accept the selected Vulkan pipeline and bind it only when its list is non-empty.
- Draw solid geometry before masked geometry, then run the authored-foliage tail.
- Continue using the raw per-frame depth lists. The existing motion task shader performs its own frustum test and does not need to depend on the optional GPU-compacted depth buffers.
- Preserve:
  - the motion attachment clear;
  - read-only reverse-Z depth testing;
  - descriptor bindings and push constants;
  - previous view-projection/time ownership;
  - camera-cut invalidation;
  - receiver-signature history flags;
  - the final shader-read transition and diagnostics.

The pass must no longer reference `SimpleOpaqueMeshletCount`, `SimpleNormalOpaqueMeshletCount`, `FullOpaqueMeshletCount`, or their forward draw-buffer bases.

### 3.3 Solid sidedness parity

Update `motion_vector.mesh` and `motion_vector.frag`:

- Export `command.MaterialIndex` from the mesh shader as a flat varying at a new unused location.
- Read the material in the fragment shader.
- Match `depth_sided.frag`: when `material.NormalScaleBias.w < 0.5`, discard `!gl_FrontFacing`; otherwise retain both faces.
- Perform this decision before writing velocity or the directional-shadow scratch signature.
- Leave mirrored-instance triangle correction unchanged so `gl_FrontFacing` retains the same meaning as in depth rendering.

Both fixed-function culling and fragment sidedness must match the depth prepass: fixed culling is disabled, and the material decides whether a backface survives.

### 3.4 Masked material coverage

Add `Njulf.Shaders/motion_vector_alpha.mesh`:

- Reuse the existing `MotionVectorTaskPayload` contract and `motion_vector.task` shader.
- Fetch the full `GPUVertex` with `FetchRenderableVertex`.
- Export:
  - current and previous screen UVs;
  - UV0 and UV1;
  - vertex color;
  - material index;
  - receiver signature;
  - history frame/flags.
- Preserve current skinning behavior, previous object-transform lookup, camera history fallback, and mirrored triangle ordering from `motion_vector.mesh`.

Add `Njulf.Shaders/motion_vector_alpha.frag`:

- Include `material_coverage.glsl` rather than duplicating alpha logic.
- Apply the same material-sidedness check as `depth_alpha.frag`.
- Call `EvaluateMaterialAlphaCoverage(material, uv0, uv1, vertexColor.a)` and gate with `MaterialCoverageSurvivesForward`.
- This must preserve material-selected UV sets, offset/scale/rotation, material alpha, sampled base-color alpha, vertex alpha, alpha mode, and cutoff semantics.
- Discard before writing velocity or receiver identity.
- Copy the existing velocity clamp and directional-shadow scratch write without changing their history semantics.

Do not implement a motion-specific alpha threshold or texture sampling path. Depth, forward, and motion must share the same helper.

### 3.5 Authored-foliage coverage

Update `foliage_motion.mesh`:

- Export `command.ClusterIndex`, `command.LodLevel`, and authored geometry mode `1u` as flat varyings after the existing locations.
- Preserve current/previous instance reads and wind deformation at `Time` and `PreviousTime`.

Update `foliage_motion.frag`:

- Replace the simplified albedo-alpha calculation with `foliage_coverage.glsl`.
- Call `FoliageCoverageSurvives` with the material, interpolated UV, authored geometry mode, cluster index, LOD band, and `gl_FragCoord.xy`.
- Ignore the returned sampled color; this pass needs only the coverage decision.
- Write velocity and receiver identity only after coverage survives.

This makes authored foliage reuse the same material alpha and stable LOD-dither decision as its depth/forward path. Procedural blade/card motion remains outside this repair because the current motion pass has no procedural motion producer.

## 4. API and compatibility impact

- Add `MeshPipeline.MaskedMotionVectorPipeline` as the only new managed interface.
- Do not change `GPUMotionVectorPushConstants`, `SceneRenderingData`, bindless indices, material layout, or render-graph resources.
- Do not change the ownership or ordering of `DepthPrePass` and `MotionVectorPass`.
- Do not change the existing first-frame or camera-cut rule: invalid history makes previous UV equal current UV and therefore emits zero velocity.
- Keep receiver-signature writes in every surviving solid, masked, and authored-foliage fragment.

## 5. Automated tests

Add a focused `Njulf.Tests/AlphaCorrectMotionVectorTests.cs` fixture with source/pipeline contract tests that require:

1. `MotionVectorPass` uses the solid and masked depth counts and bindless bases, in that order, and no longer uses the three forward buckets.
2. The scene draw helper accepts a pipeline, and `MeshPipeline` creates and destroys the masked pipeline.
3. Both scene motion pipelines use `CullModeFlags.None`.
4. Both scene fragment shaders perform data-driven sidedness before any output.
5. The masked mesh shader exports UV0, UV1, vertex color, and material index.
6. The masked fragment shader includes `material_coverage.glsl`, calls `EvaluateMaterialAlphaCoverage`, and gates with `MaterialCoverageSurvivesForward`.
7. Authored-foliage motion exports cluster/LOD/geometry inputs and calls `FoliageCoverageSurvives` with `gl_FragCoord.xy`.
8. Velocity and receiver-signature writes occur only after the relevant discard decisions.
9. `DepthPrePass` remains free of motion-vector attachments and previous-frame motion state.

Update `Njulf.Tests/ShaderBuildTests.cs`:

- Add `motion_vector_alpha.mesh` and `motion_vector_alpha.frag` to `RequiredShaders`.
- Increase the expected embedded shader-resource count from 273 to 275.
- Retain the SPIR-V magic and non-empty resource checks.

The existing material-alpha contract tests remain authoritative for cutoff equality and alpha-mode behavior; do not duplicate those numerical cases in the motion fixture.

## 6. Runtime validation

Run with Vulkan validation enabled and select the existing motion-vector debug view.

Validate these scenarios:

1. Move the camera past alpha-masked Sponza curtains. Curtain texels show curtain motion; holes show the visible background's motion rather than a full-card foreground vector.
2. Exercise a mask using UV1, a non-identity base-color UV transform, and vertex alpha. Its motion silhouette must match its depth/forward silhouette.
3. View both sides of single- and double-sided materials. Single-sided backfaces remain absent; double-sided backfaces carry motion.
4. Repeat with a negative-determinant/mirrored instance and confirm facing is unchanged.
5. Exercise authored foliage with wind and at multiple LOD bands. Motion coverage must follow leaf alpha and stable LOD dither without rectangular mesh/card blocks.
6. Put a moving object behind masked geometry and verify both velocity and directional-shadow receiver identity come from the background in mask holes.
7. Start from cold history and trigger a camera cut. The affected frame must contain zero motion rather than stale vectors.

Require no Vulkan validation errors for shader interfaces, pipeline creation, descriptor use, dynamic rendering, or attachment layouts.

## 7. Build and test commands

Run from the `Njulf` directory:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~AlphaCorrectMotionVectorTests|FullyQualifiedName~ShaderBuildTests"
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Debug
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release
dotnet build Njulf.sln
dotnet test Njulf.sln
```

## 8. Performance qualification

Capture baseline and candidate timings in both Release and ShippingPerformance using the existing Bistro-motion and Sponza-horizontal workloads, with identical assets, camera trajectories, settings, warmup, and measurement windows.

Record at minimum:

- GPU frame P50/P95/P99;
- `GpuMotionVectorMicroseconds` P50/P95/P99;
- `GpuDepthPrePassMicroseconds` P50/P95/P99;
- complete GPU-timing sample counts.

Use the repository campaign's maximum 1% GPU-frame P95 regression as the merge gate in both scenes. The depth-prepass timing should remain statistically unchanged because this repair does not modify it.

If the gate fails, do not restore depth-prepass fusion. Retain the correctness implementation on the working branch, capture pass-level evidence, and propose a separate optimization such as a sidedness-specific queue split.

## 9. Explicit exclusions

- Depth-prepass/motion-vector fusion.
- Automatic reflections, transmission, fog, or other features from `65c3dfb`.
- Alpha-blended transparency and geometry decals.
- Procedural foliage blade/card motion generation.
- Previous-pose skin deformation; existing motion still applies the previous object transform to the current skinned position.
- Activating or redefining the currently unused `MaterialSurfaceFlags.WritesMotionVectors` flag.
- Changes to the shared material or foliage coverage contracts themselves.

## 10. Definition of done

- Scene motion draws exactly the solid and masked depth partitions.
- Solid and masked motion match depth-prepass sidedness.
- Masked motion uses the exact shared material coverage contract.
- Authored-foliage motion uses the exact shared foliage coverage contract.
- Alpha holes retain visible background velocity and receiver identity.
- Camera-cut and invalid-history behavior remains correct.
- Shader builds, focused tests, full build, and full test suite pass.
- Runtime validation produces no relevant Vulkan errors.
- Bistro and Sponza remain within the 1% GPU-frame P95 regression gate.
