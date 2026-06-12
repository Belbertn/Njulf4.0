# Phase 12: Animation Rendering Detailed Implementation Plan

Goal: support production animated content: characters, creatures, props, doors, machinery, and environmental motion. Phase 12 should add glTF skeleton, skin, joint, weight, animation clip, playback, bounds, and GPU skinning support while preserving the current static mesh rendering path.

## Recommendation

Implement animation in vertical slices:
1. import and validate skeletons, skins, joints, weights, and clips without changing rendering.
2. add CPU animation sampling and pose evaluation with tests.
3. add a simple CPU-skinned validation path only for correctness and debugging.
4. add production GPU skinning for the renderer.
5. integrate animated bounds with culling, shadows, diagnostics, and sample controls.
6. add blending, cross-fade, additive poses, and morph targets after the base skinned glTF path is solid.

Do not begin with a full animation graph, IK, ragdolls, or gameplay state machine. Those are engine/game systems. The renderer phase needs reliable import, pose evaluation, skinning, culling, and diagnostics first.

## Current Baseline

Relevant current behavior:
- `Njulf.Assets/ModelImporter.cs` imports static geometry through Assimp and a partial glTF JSON manifest.
- Node transforms are currently baked into vertices during import.
- `ModelMesh` and `ModelSubMesh` only carry positions, normals, tangents, bitangents, UVs, indices, materials, and bounds.
- `ModelRenderUploadService` uploads each submesh as a static `RenderObject` with `MeshHandle` and `MaterialHandle`.
- `GPUVertex` contains position, normal, UVs, and tangent. It has no joint index or weight streams.
- `MeshManager` builds meshlets from static vertex positions and static indices.
- `SceneDataBuilder` culls objects from static mesh bounds transformed by `RenderObject.WorldMatrix`.
- `RenderObject` has no animation component, skeleton instance, pose handle, or dynamic bounds override.
- `SampleInputController.cs` has many renderer debug controls, but no animation controls.
- Tests cover static glTF material import, node transform baking, upload service behavior, scene culling helpers, GPU struct layout, and shader builds.

Important consequence: animated import cannot keep baking animated node transforms into vertex positions. Phase 12 must preserve node hierarchy and bind-pose data for skinned meshes while keeping the existing static path unchanged for non-animated assets.

## Target Outcome

Phase 12 is complete when:
- skinned glTF characters render correctly.
- imported skeleton hierarchy, inverse bind matrices, joints, vertex joint indices, vertex weights, and animation clips are preserved.
- animations can play, pause, seek, loop, cross-fade, and blend.
- GPU skinning is the production path.
- CPU sampling and optional CPU skinning remain available for tests/debugging.
- skinned mesh bounds update conservatively enough that culling and shadows do not pop.
- animation diagnostics identify active clips, pose counts, joint counts, skinning cost, and culled/skinned mesh counts.
- `NjulfHelloGame` can load and inspect a sample animated character.

## Non-Goals

Phase 12 does not include:
- full gameplay animation state machines.
- inverse kinematics.
- physics ragdolls.
- animation retargeting between different skeletons.
- facial animation authoring tools.
- cinematic timeline tooling.
- compressed animation clips as the first implementation.
- animation editor UI.
- skeletal LOD or mesh simplification beyond basic future hooks.

## Data Model

Add animation data in `Njulf.Core` or `Njulf.Assets` depending on ownership. The preferred split is:
- import-time asset data in `Njulf.Assets`.
- runtime scene/component data in `Njulf.Core.Scene` or a new `Njulf.Core.Animation` namespace.
- GPU upload/runtime resources in `Njulf.Rendering`.

Suggested files:
- `Njulf.Core/Animation/Skeleton.cs`
- `Njulf.Core/Animation/SkeletonJoint.cs`
- `Njulf.Core/Animation/Skin.cs`
- `Njulf.Core/Animation/AnimationClip.cs`
- `Njulf.Core/Animation/AnimationChannel.cs`
- `Njulf.Core/Animation/AnimationSampler.cs`
- `Njulf.Core/Animation/AnimationPose.cs`
- `Njulf.Core/Animation/Animator.cs`
- `Njulf.Core/Scene/SkinnedRenderObject.cs`
- `Njulf.Rendering/Resources/SkinningManager.cs`
- `Njulf.Rendering/Data/AnimationData.cs`
- `Njulf.Rendering/Pipeline/SkinningPass.cs`
- `Njulf.Shaders/skinning.comp`

Suggested CPU types:

```csharp
public sealed class Skeleton
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<SkeletonJoint> Joints { get; init; } = Array.Empty<SkeletonJoint>();
    public int RootJointIndex { get; init; } = -1;
}

public readonly struct SkeletonJoint
{
    public string Name { get; init; }
    public int ParentIndex { get; init; }
    public Matrix4x4 LocalBindTransform { get; init; }
    public Matrix4x4 InverseBindMatrix { get; init; }
}

public sealed class Skin
{
    public string Name { get; init; } = string.Empty;
    public Skeleton Skeleton { get; init; } = new();
    public IReadOnlyList<int> JointIndices { get; init; } = Array.Empty<int>();
    public IReadOnlyList<Matrix4x4> InverseBindMatrices { get; init; } = Array.Empty<Matrix4x4>();
}

public sealed class AnimationClip
{
    public string Name { get; init; } = string.Empty;
    public float DurationSeconds { get; init; }
    public IReadOnlyList<AnimationChannel> Channels { get; init; } = Array.Empty<AnimationChannel>();
}
```

Channel support:
- translation.
- rotation.
- scale.
- cubic spline interpolation should be detected and reported first; linear and step are required for the first production slice.

Acceptance criteria:
- static models still import as before.
- animated models expose skeleton, skin, and clip data.
- unsupported animation features produce clear diagnostics instead of silent corruption.

## Import Pipeline

Phase 12 should prefer glTF semantics as the source of truth. Assimp can still parse geometry, but glTF skin/animation metadata should be validated directly from JSON where required because the current importer already reads glTF material metadata directly for deterministic behavior.

Tasks:
1. Extend `ModelMesh` with:
   - `Skeletons`
   - `Skins`
   - `AnimationClips`
   - per-submesh `SkinIndex`
   - per-vertex `JointIndices`
   - per-vertex `JointWeights`
   - source node id/name for each submesh
2. Extend `ModelSubMesh` with:
   - `NodeIndex`
   - `SkinIndex`
   - `JointIndices0`
   - `JointWeights0`
   - optional `JointIndices1` and `JointWeights1` later for more than four influences.
3. Stop baking skinned node transforms into vertex positions. Preserve mesh-local bind-space vertices for skinned meshes.
4. Keep baking static node transforms for non-skinned static meshes until a scene-node transform system replaces it.
5. Parse glTF:
   - `nodes`
   - `skins`
   - `joints`
   - `skeleton`
   - `inverseBindMatrices`
   - `animations`
   - channel target node/path
   - sampler input times
   - sampler output values
   - interpolation mode.
6. Read vertex attributes:
   - `JOINTS_0`
   - `WEIGHTS_0`
   - later `JOINTS_1`
   - later `WEIGHTS_1`
7. Normalize weights and reject invalid data:
   - negative weights.
   - zero total weight for skinned vertex.
   - joint index outside skin joint list.
   - inverse bind matrix count mismatch.
8. Preserve node hierarchy transforms:
   - translation/rotation/scale.
   - matrix.
   - parent-child relations.
9. Add import diagnostics:
   - skeleton count.
   - joint count.
   - skin count.
   - skinned submesh count.
   - clip count.
   - animation channel count.
   - unsupported interpolation count.
   - max influences per vertex.

Acceptance criteria:
- a minimal animated glTF imports with one skeleton, one skin, one clip, valid joint weights, and valid inverse bind matrices.
- a static glTF import remains unchanged.
- malformed animation data fails with asset path and offending index/name.

## Coordinate And Matrix Conventions

The current engine uses its own `Matrix4x4` and row-vector-style transform helpers in places such as `SceneDataBuilder.TransformPoint`. Animation must be explicit about matrix order.

Tasks:
1. Document one animation convention:
   - local transform order: scale, rotation, translation.
   - parent-to-child composition order.
   - final skin matrix formula.
2. Add tests that prove:
   - parent transform affects child joint.
   - inverse bind matrix cancels bind pose.
   - animated rotation bends a simple two-joint strip in the expected direction.
3. Add helper conversion methods for glTF TRS and matrix transforms.
4. Avoid duplicating ad hoc transform math in importer, pose evaluator, CPU skinning, and shaders.

Recommended final skin matrix:
```text
jointSkinMatrix = inverse(globalMeshTransform) * globalJointTransform * inverseBindMatrix
```

The exact order must be adapted to Njulf's matrix convention and locked by tests before GPU skinning is trusted.

Acceptance criteria:
- CPU skinning and GPU skinning produce matching vertex positions within tolerance.
- skeleton debug lines align with visible skinned mesh joints.

## CPU Sampling

CPU sampling should be the first runtime implementation because it is easy to test and debug.

Add an `Animator` component:
- implements `IUpdateable`.
- owns current playback state.
- writes a local pose and final skin matrices.
- can be attached to a model or `SkinnedRenderObject`.

Suggested API:

```csharp
public sealed class Animator : IUpdateable
{
    public bool Enabled { get; set; } = true;
    public int UpdateOrder { get; set; }
    public AnimationClip? CurrentClip { get; }
    public float TimeSeconds { get; private set; }
    public float Speed { get; set; } = 1.0f;
    public bool Looping { get; set; } = true;

    public void Play(AnimationClip clip, bool loop = true);
    public void Pause();
    public void Resume();
    public void Stop();
    public void Seek(float timeSeconds);
    public void CrossFade(AnimationClip nextClip, float durationSeconds);
    public ReadOnlySpan<Matrix4x4> GetSkinMatrices(int skinIndex);
}
```

Sampling tasks:
1. Implement clip time wrapping for looping.
2. Implement clamped playback for non-looping clips.
3. Implement step interpolation.
4. Implement linear translation and scale interpolation.
5. Implement normalized quaternion slerp for rotation.
6. Add cubic spline support after linear paths are correct.
7. Evaluate local pose from default bind pose plus animated channels.
8. Build global joint transforms by hierarchy order.
9. Build final skin matrices per skin.
10. Cache channel lookup by joint and property for efficient sampling.

Acceptance criteria:
- clips play, pause, seek, loop, and stop.
- blend and cross-fade behavior is deterministic.
- sampling tests pass for translation, rotation, scale, hierarchy, looping, and clamping.

## CPU Skinning Debug Path

A CPU-skinned path is not the production renderer path, but it is valuable for tests and for validating GPU skinning.

Tasks:
1. Add a CPU utility that skins vertex positions, normals, and tangents from:
   - bind-pose vertices.
   - joint indices.
   - weights.
   - final skin matrices.
2. Use it only in tests/debug tools, not normal rendering.
3. Add fixture meshes:
   - one joint rigid influence.
   - two-joint bend.
   - four-weight blend.
4. Compare CPU-skinned output against expected positions.
5. Later compare GPU skinning readback or shader reference output against CPU skinning.

Acceptance criteria:
- CPU skinning proves the math before shader work begins.
- CPU debug path catches weight normalization and matrix order errors.

## GPU Vertex And Mesh Data

Production GPU skinning needs joint and weight streams without bloating static mesh vertices unnecessarily.

Recommended data layout:
- keep `GPUVertex` unchanged for static geometry.
- add a separate `GPUSkinVertex` or `GPUVertexSkinningData` buffer:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUVertexSkinningData
{
    public ushort Joint0;
    public ushort Joint1;
    public ushort Joint2;
    public ushort Joint3;
    public ushort Weight0;
    public ushort Weight1;
    public ushort Weight2;
    public ushort Weight3;
}
```

Alternative:
- use `uint4` joint indices and `float4` weights for simplicity first.
- optimize to 16-bit packed data after correctness is proven.

Tasks:
1. Extend `MeshInfo` with optional skinning data offset/count.
2. Add a skinning vertex buffer to `MeshManager`.
3. Add bindless buffer indices for:
   - skinning vertex data.
   - skin matrix buffer.
   - skinned output vertex buffer if compute skinning writes a second vertex stream.
4. Add tests for buffer offsets, alignment, and bindless index constants.
5. Keep static mesh upload path unchanged.

Acceptance criteria:
- static meshes pay no extra vertex stride cost.
- skinned meshes upload joint and weight streams alongside base vertices.
- shader and C# struct layout tests protect every new GPU struct.

## GPU Skinning Strategy

Use compute skinning as the production path.

Why compute first:
- preserves the existing mesh/task shader draw architecture.
- lets forward, depth, shadow, transparent, and future passes all read already-skinned vertices.
- avoids duplicating skinning logic in every graphics shader.
- gives one place to time and debug skinning cost.

Target frame order:
1. animation CPU sampling updates skin matrices.
2. upload skin matrices for current frame.
3. `SkinningPass` computes skinned vertices for visible or active skinned meshes.
4. depth/shadow/forward/transparent passes read skinned vertex buffer for skinned meshlets.

Tasks:
1. Add `SkinningManager`:
   - owns per-frame skin matrix buffers.
   - owns skinned output vertex buffers.
   - tracks animated instance allocations.
   - handles resize/growth and safe retirement.
2. Add `GPUSkinningDispatch` data:
   - source vertex offset.
   - source skinning data offset.
   - destination vertex offset.
   - vertex count.
   - skin matrix offset.
   - object/instance id.
3. Add a `SkinningPass` compute pipeline.
4. Add `skinning.comp`.
5. Ensure barriers:
   - CPU/staging upload to compute shader read for matrices.
   - compute shader write to mesh/task/fragment shader read for skinned vertices.
6. Update mesh/task shader vertex fetch to select static or skinned vertex buffer per object/mesh.
7. Add debug names for all skinning buffers and pipelines.
8. Add GPU timing and CPU record timing.

Acceptance criteria:
- GPU skinning updates before depth, shadow, and forward passes.
- RenderDoc shows a named `SkinningPass`.
- validation layers are clean.
- skinned mesh vertices are used consistently by all render passes.

## Scene And Render Object Integration

Add a runtime object model that can represent animated model instances independently.

Suggested approach:
- keep `RenderObject` for static meshes.
- add `SkinnedRenderObject : RenderObject` with:
  - `SkinHandle`
  - `Animator`
  - `AnimatedBounds`
  - `SkinningInstanceId`
  - optional `CurrentPoseRevision`

Tasks:
1. Extend `Model` to own skeletons, skins, and animation clips.
2. Update `Model.CreateInstance()` to create separate animator/pose state for each animated instance.
3. Add scene update registration for `Animator`.
4. Ensure multiple instances of the same animated asset can play different clips/times independently.
5. Make static and animated instances share immutable mesh/material/skeleton/clip data where possible.
6. Ensure `Dispose` releases skinning allocations and material/mesh references.

Acceptance criteria:
- two instances of the same character can play different animations.
- model asset data is shared; per-instance pose data is not shared.
- scene update order produces deterministic animation results.

## Bounds And Culling

Animated bounds are required for correct culling and shadows.

Implementation stages:
1. Conservative first:
   - use imported skinned mesh bounds expanded by a configurable margin.
   - never shrink during animation.
2. Better:
   - compute per-clip bounds offline/import-time by sampling each clip.
   - store clip bounds per skinned submesh.
3. Production:
   - update per-instance bounds from active clip/blend state.
   - optionally use joint bounds or bounding capsules for characters.

Tasks:
1. Add `RenderObject` bounds override or `SkinnedRenderObject.AnimatedBoundingBox`.
2. Update `SceneDataBuilder` to use animated bounds when present.
3. Include skinned objects in shadow caster bounds.
4. Account for motion during cross-fades.
5. Add diagnostics:
   - skinned object count.
   - animated bounds mode.
   - culling misses prevented by conservative margin if measurable.

Acceptance criteria:
- animated characters are not culled when limbs move outside bind-pose bounds.
- shadow casting does not pop when animation extends bounds.
- conservative bounds are visible in debug overlays once Phase 18 debug draw exists.

## Meshlets And Skinned Meshes

The existing renderer is meshlet-based. Skinning changes vertex positions after meshlet generation, so culling must be conservative.

Tasks:
1. Keep meshlets generated from bind-pose geometry initially.
2. Expand meshlet bounding spheres for skinned meshes by a per-mesh or per-clip margin.
3. Add optional per-meshlet animation bounds later if needed.
4. Ensure meshlet local vertex indices still reference the correct source/skinned vertex data.
5. Update CPU and GPU culling tests to use expanded skinned bounds.

Acceptance criteria:
- meshlet culling does not incorrectly remove animated limbs.
- skinned meshlet bounds are conservative but not catastrophically large for normal character assets.

## Animation Blending

Add blending after single-clip playback is correct.

Required first:
- cross-fade from clip A to clip B.
- blend local transforms by channel:
  - translation: lerp.
  - scale: lerp.
  - rotation: normalized slerp.
- missing channels fall back to bind/default pose.

Later:
- layered blending by joint mask.
- additive clips.
- root motion extraction.

Tasks:
1. Add `AnimationBlendState`.
2. Add cross-fade duration and weight curve.
3. Add tests for blend endpoints and midpoints.
4. Add diagnostics for active clip count and blend weight.

Acceptance criteria:
- cross-fades are smooth and deterministic.
- missing channels do not snap joints to identity.

## Root Motion

Root motion is important for characters but should be explicit.

Tasks:
1. Identify root motion source joint or node.
2. Add settings:
   - apply root motion to object transform.
   - keep root motion in pose.
   - extract delta only.
3. Add API:
   - `Animator.ConsumeRootMotionDelta()`.
4. Add tests for looping root motion and frame-rate independence.

Acceptance criteria:
- root motion does not double-apply.
- users can choose in-place or motion-driven animation.

## Morph Targets

Morph targets are optional for Phase 12 and should come after skeleton skinning.

Use cases:
- facial animation.
- corrective shapes.
- cloth or soft deformation.

Implementation tasks:
1. Import glTF morph target attributes:
   - position.
   - normal.
   - tangent if available.
2. Import mesh default weights.
3. Import animation channels targeting `weights`.
4. Add morph target buffers.
5. Evaluate morph weights in `Animator`.
6. Apply morphs before skinning in compute, or combine morph and skinning in one compute shader.

Acceptance criteria:
- morph target support is opt-in.
- skinned meshes without morphs do not pay morph target memory/runtime cost.

## Render Pass Integration

Normal frame order after Phase 12:
1. CPU scene update.
2. CPU animation sampling.
3. skin matrix upload.
4. `SkinningPass`.
5. shadow passes.
6. depth prepass.
7. Hi-Z/AO/light culling.
8. opaque forward.
9. skybox.
10. transparent forward.
11. fog.
12. bloom.
13. composite.
14. anti-aliasing.
15. present.

Tasks:
1. Insert `SkinningPass` before every pass that reads vertices.
2. If shadows are rendered before normal depth, skinning must happen before shadows.
3. Add barriers from skinning writes to graphics shader reads.
4. Ensure resize does not invalidate persistent skinning buffers unnecessarily.
5. Ensure inactive animated objects do not dispatch skinning work.

Acceptance criteria:
- shadows, depth, opaque lighting, transparent rendering, and reflections all see the same animated pose.
- skinning dispatch is skipped when no skinned meshes are active.

## Settings And Debug Views

Add animation settings under `RenderSettings`.

Suggested API:

```csharp
public enum AnimationSkinningMode : uint
{
    Disabled = 0,
    CpuDebug = 1,
    GpuCompute = 2
}

public enum AnimationDebugView : uint
{
    None = 0,
    JointWeights = 1,
    JointIndex = 2,
    SkinningError = 3,
    Skeleton = 4,
    AnimatedBounds = 5,
    ClipTime = 6
}

public sealed class AnimationSettings
{
    public bool Enabled { get; set; } = true;
    public AnimationSkinningMode SkinningMode { get; set; } = AnimationSkinningMode.GpuCompute;
    public AnimationDebugView DebugView { get; set; } = AnimationDebugView.None;
    public int MaxJointsPerSkeleton { get; set; } = 256;
    public int MaxAnimatedInstances { get; set; } = 1024;
    public bool UpdateWhenOffscreen { get; set; } = true;
    public bool UseConservativeBounds { get; set; } = true;
    public float BoundsPadding { get; set; } = 0.25f;
}
```

Recommended constraints:
- max joints: `1` to `1024`, default `256`.
- max animated instances: `0` to a documented budget.
- bounds padding: `0.0` to `10.0`.

Acceptance criteria:
- settings clamp invalid values.
- disabling animation freezes or disables animated objects according to documented behavior.
- debug views do not affect normal rendering when disabled.

## Sample App Controls

Extend `NjulfHelloGame/SampleInputController.cs` after settings and sample animated content exist.

Suggested controls:
- toggle animation playback.
- pause/resume current animation.
- step animation frame.
- cycle animation clip.
- cycle animation debug view.
- cycle skinning mode.
- speed down/up.
- toggle skeleton/bounds debug once debug drawing exists.

Implementation notes:
- follow the existing `WasPressed` pattern.
- add `PrintAnimationSettings`.
- avoid key collisions with existing Phase 1-11 controls.
- print current clip, time, speed, loop, skinning mode, and debug view.

Acceptance criteria:
- `NjulfHelloGame` can load a sample animated asset and inspect animation state from console output.
- animation can be paused for RenderDoc captures.

## Diagnostics

Extend `RendererDiagnostics`, `SceneRenderingData`, and sample reporting.

Add fields:
- `AnimationEnabled`
- `AnimationSkinningMode`
- `AnimationDebugView`
- `AnimatedModelCount`
- `SkinnedObjectCount`
- `SkeletonCount`
- `SkinCount`
- `AnimationClipCount`
- `ActiveAnimatorCount`
- `PlayingAnimatorCount`
- `PausedAnimatorCount`
- `SkinnedVertexCount`
- `SkinningDispatchCount`
- `JointMatrixCount`
- `MaxJointsPerSkeleton`
- `CpuAnimationSampleMicroseconds`
- `CpuSkinMatrixUploadMicroseconds`
- `CpuSkinningRecordMicroseconds`
- `GpuSkinningMicroseconds`
- `SkinningUploadBytes`
- `SkinMatrixBufferSize`
- `SkinnedVertexBufferSize`
- `AnimatedBoundsMode`

Update `SampleDiagnosticsReporter` with a compact animation line.

Acceptance criteria:
- performance regressions in animation sampling and skinning are measurable.
- asset import diagnostics distinguish static and animated content.
- RenderDoc and console diagnostics agree on whether GPU skinning ran.

## Tests

Importer tests:
1. static glTF import remains unchanged.
2. glTF skeleton hierarchy is preserved.
3. skin joint list and inverse bind matrices are imported.
4. `JOINTS_0` and `WEIGHTS_0` are imported per vertex.
5. weights are normalized or rejected according to documented policy.
6. animation clips preserve name, duration, samplers, channels, and interpolation.
7. malformed skin references fail with useful messages.
8. unsupported interpolation is reported clearly.

Pose and sampling tests:
1. step interpolation.
2. linear translation.
3. linear scale.
4. quaternion rotation interpolation.
5. loop wrapping.
6. non-looping clamp.
7. parent-child hierarchy.
8. inverse bind cancellation.
9. cross-fade endpoints and midpoint.
10. root motion extraction if implemented.

Skinning tests:
1. one-joint rigid skinning.
2. two-joint blend.
3. four-weight blend.
4. normal and tangent transform.
5. CPU and GPU layout compatibility.
6. skinned bounds contain animated vertices.

Scene and renderer tests:
1. `Model.CreateInstance()` gives each animated instance independent animator state.
2. static and animated models can coexist.
3. skinned render objects use animated bounds for culling.
4. GPU struct layout tests include new animation structs.
5. bindless index tests include new skinning buffers.
6. shader build tests compile `skinning.comp`.
7. diagnostics default to zero when no animated content is present.

Manual validation:
1. load a known simple skinned glTF sample.
2. verify bind pose.
3. play animation at normal speed.
4. pause and inspect skeleton pose in RenderDoc/debug view.
5. run two instances at different times.
6. verify shadows follow animation.
7. verify culling does not pop limbs.
8. resize window and toggle settings.
9. shutdown validation-clean.

## Sample Assets

Add small, deterministic test assets to avoid relying only on large production content.

Recommended assets:
- minimal one-joint triangle.
- two-joint bending strip.
- simple walking character from a permissive source.
- prop animation: door open/close.
- non-skinned node animation: rotating fan or moving platform.

Asset requirements:
- committed with licenses if external.
- small enough for CI tests.
- no embedded buffers/images until Phase 14 supports them, unless tests intentionally assert unsupported behavior.
- external `.bin` and texture files compatible with current importer.

Acceptance criteria:
- tests do not depend on fragile external downloads.
- sample app has one practical animated scene.

## Performance Budget

Initial budgets should be conservative and visible:
- CPU animation sampling: under 0.25 ms for a small scene.
- GPU skinning: under 0.5 ms for a small character set.
- avoid per-frame allocations in animation sampling and skin matrix building.
- avoid synchronous GPU waits during normal animation updates.

Tasks:
1. preallocate pose arrays.
2. reuse per-instance skin matrix storage.
3. batch skinning dispatches where possible.
4. skip inactive or unchanged animators when safe.
5. add stress scene:
   - many characters.
   - many clips.
   - high joint count.
   - high skinned vertex count.
6. track memory:
   - joint matrices.
   - skinning input buffers.
   - skinned output vertex buffers.
   - animation clip data.

Acceptance criteria:
- animation cost is visible and budgetable.
- stress scenes reveal bottlenecks before production content depends on the system.

## Implementation Order

1. Add animation data model types and tests.
2. Extend glTF import to preserve nodes, skins, joints, weights, inverse bind matrices, and clips.
3. Add import diagnostics and malformed asset tests.
4. Implement CPU clip sampling and pose evaluation.
5. Implement CPU skinning debug utility and math validation tests.
6. Add `Animator` and animated model instance ownership.
7. Add animated bounds and integrate culling/shadow bounds.
8. Add GPU skinning data buffers and struct layout tests.
9. Add `SkinningManager`, `SkinningPass`, and `skinning.comp`.
10. Update mesh/task shader vertex fetch to select static or skinned vertices.
11. Add renderer diagnostics and sample reporting.
12. Add sample app controls in `SampleInputController`.
13. Add a small animated sample scene.
14. Add cross-fade blending.
15. Add root motion if needed by the sample/game.
16. Add morph targets only after skeletal skinning is stable.
17. Run full tests, shader build, Vulkan validation, and RenderDoc inspection.

## Rollout Slices

### Slice A: Import And Data Contracts

Deliver:
- skeleton/skin/clip model types.
- glTF import for joints, weights, inverse bind matrices, and clips.
- diagnostics.
- tests.

Definition of done:
- animated assets can be imported and inspected without rendering changes.

### Slice B: CPU Sampling

Deliver:
- `Animator`.
- clip playback.
- pose evaluation.
- CPU skinning debug utility.
- independent model instances.

Definition of done:
- tests prove animation math and per-instance playback.

### Slice C: GPU Skinning

Deliver:
- skinning buffers.
- compute skinning shader/pass.
- vertex fetch integration.
- barriers.
- timings.

Definition of done:
- a skinned glTF character renders and animates validation-clean.

### Slice D: Bounds, Shadows, And Debugging

Deliver:
- animated bounds.
- shadow integration.
- debug views.
- sample controls.
- diagnostics.

Definition of done:
- animated characters do not pop from culling and shadows follow the pose.

### Slice E: Production Animation Features

Deliver as needed:
- cross-fades.
- additive/layered blending.
- root motion.
- morph targets.

Definition of done:
- common character and prop workflows are supported without renderer hacks.

## Risks

1. Matrix convention mistakes can make animation look close but wrong.
2. Current static import bakes node transforms, which conflicts with skinned mesh bind-space requirements.
3. Meshlet culling can incorrectly remove animated geometry if bounds are not conservative.
4. Adding joint/weight data to `GPUVertex` would waste memory on static meshes.
5. GPU skinning needs careful barriers before depth, shadow, and forward passes.
6. Multiple animated instances can accidentally share mutable pose state.
7. Assimp and glTF JSON can disagree on node/material/mesh indexing if not handled carefully.
8. Animation data can cause per-frame allocation churn if pose arrays are not reused.

Mitigations:
- lock matrix order with tiny deterministic tests.
- preserve static import path separately from skinned import path.
- use conservative animated bounds first.
- keep skinning streams separate from static vertex data.
- add RenderDoc-friendly pass and buffer names.
- add per-instance animator tests.
- use direct glTF validation for animation metadata.
- measure CPU allocation and timing in diagnostics.

## Final Acceptance Criteria

Phase 12 is complete when:
1. static model import and rendering remain unchanged.
2. glTF skeletons, skins, joints, weights, inverse bind matrices, and clips import correctly.
3. animation clips can play, pause, seek, loop, and cross-fade.
4. CPU sampling is deterministic and covered by tests.
5. GPU compute skinning is the production path.
6. skinned vertices are used consistently by depth, shadow, opaque, transparent, fog/bloom/composite-dependent rendering.
7. animated bounds prevent incorrect culling and shadow popping.
8. multiple instances of the same animated asset can play different animations independently.
9. diagnostics expose animation counts, memory, timings, active clips, and skinning mode.
10. `NjulfHelloGame` exposes practical animation controls through `SampleInputController`.
11. shader build, GPU struct layout, bindless index, importer, sampling, skinning, and scene tests pass.
12. Vulkan validation is clean during startup, animation playback, pause/seek, resize, scene reload, and shutdown.
13. RenderDoc clearly shows skin matrix upload, `SkinningPass`, and subsequent render passes reading the expected animated vertex data.
