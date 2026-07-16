# Blend Materials and Decals Implementation Plan

## Goal

Properly support glTF alpha materials, especially Sponza's `dirt_decal` BLEND material, without rendering decal quads as opaque black or grey rectangles.

The confirmed issue is not light culling, UV transforms, or mesh placement. The artifact comes from alpha-blended decal geometry being rendered through the opaque depth prepass and opaque forward pass with blending disabled. A temporary workaround that skipped BLEND submeshes removed the artifact, confirming the diagnosis, but it also removed decals entirely. This plan replaces that workaround with explicit opaque, masked, and transparent/decal rendering paths.

## Current State

1. `ModelImporter` reads glTF material alpha metadata into `ModelMaterial`:
   - `AlphaMode`
   - `AlphaCutoff`
   - `DoubleSided`

2. `ModelRenderUploadService` uploads materials as `GPUMaterialData`, but the GPU material contract does not currently expose alpha mode/cutoff or double-sided state to shaders.

3. `MeshPipeline` builds one forward graphics pipeline with:
   - `BlendEnable = false`
   - `DepthWriteEnable` controlled per pipeline creation, but only opaque usage exists for forward rendering.
   - `CullMode = BackBit`

4. The render graph is effectively:
   - Depth prepass
   - Tiled light culling
   - Forward opaque pass

5. Sponza has one `BLEND` material: `dirt_decal`, used by decal meshes on floors, walls, and pillars.

6. Rendering BLEND decals in the opaque path causes rectangular artifacts because transparent portions are not blended/discarded correctly and the geometry participates in depth as solid surfaces.

## Design Principles

1. Keep opaque geometry fast and unchanged where possible.
2. Treat `MASK` as alpha-tested opaque geometry: it can write depth and participate in light culling/depth prepass.
3. Treat `BLEND` as transparent/decal geometry: no depth writes, drawn after opaque, with blending enabled.
4. Do not hide correctness behind asset-specific hacks such as skipping `dirt_decal` by name.
5. Preserve renderer diagnostics so imported alpha objects are visible in logs/tests.
6. Avoid changing GPU struct sizes unless the change is deliberate and covered by layout tests.

## Step 1: Extend Material GPU Contract

Add alpha rendering metadata to `GPUMaterialData` without increasing struct size if possible.

Recommended packing:
- `NormalScaleBias.x`: normal scale, already used.
- `NormalScaleBias.y`: alpha mode code.
  - `0.0 = Opaque`
  - `1.0 = Mask`
  - `2.0 = Blend`
- `NormalScaleBias.z`: alpha cutoff.
- `NormalScaleBias.w`: double-sided flag.
  - `0.0 = false`
  - `1.0 = true`

Tasks:
1. Update `ModelRenderUploadService.BuildGpuMaterialData()` to encode alpha metadata.
2. Keep `MaterialManager.CreateDefaultMaterial()` as opaque defaults: `(normalScale=1, alphaMode=0, alphaCutoff=0.5, doubleSided=0)`.
3. Update shader comments in `common.glsl` to document `NormalScaleBias` semantics.
4. Add unit tests for `Opaque`, `Mask`, `Blend`, cutoff, and double-sided packing.
5. Run `GPUStructLayoutTests` to confirm no layout size changes.

Acceptance criteria:
- `BuildGpuMaterialData()` preserves imported alpha mode/cutoff.
- `SIZEOF_GPU_MATERIAL_DATA` remains 96 unless intentionally changed.

## Step 2: Classify Render Objects by Alpha Mode

The renderer needs to know whether a render object is opaque, masked, or blend before recording draw commands.

Preferred approach:
1. Add a CPU-side material classification helper:
   - `MaterialRenderMode.Opaque`
   - `MaterialRenderMode.Mask`
   - `MaterialRenderMode.Blend`
2. Store or expose classification through `MaterialManager` using the registered `MaterialHandle`.
3. Add classification to scene build output so render passes can consume separate draw lists.

Implementation options:
1. Minimal: classify from `GPUMaterialData.NormalScaleBias.y` in `SceneDataBuilder` when resolving material index.
2. Cleaner: keep a parallel CPU material metadata list in `MaterialManager` keyed by material handle.

Recommended first implementation:
- Use `GPUMaterialData.NormalScaleBias.y` to avoid broad API changes.
- Refactor later if material metadata grows.

Acceptance criteria:
- Scene build can produce separate draw counts/lists for opaque/masked vs blend objects.
- Existing opaque render path remains unchanged for `Opaque` and `Mask` objects.

## Step 3: Split Draw Command Buffers

Current meshlet draw command buffer is a single list. Add separate lists for opaque and transparent draw commands.

Tasks:
1. In `SceneDataBuilder`, build two meshlet draw command arrays:
   - `OpaqueMeshletDrawCommands`: `Opaque` and `Mask` materials.
   - `TransparentMeshletDrawCommands`: `Blend` materials.
2. Add corresponding per-frame GPU buffers in `SceneDataBuilder`.
3. Extend `SceneRenderingData` with:
   - `OpaqueMeshletCount`
   - `TransparentMeshletCount`
   - `TransparentMeshletDrawBuffer`
4. Update bindless buffer indices if a new transparent draw buffer is required.
   - Option A: add new static bindless indices for transparent draw buffers.
   - Option B: reuse the same draw buffer bindless slot and bind/register before each pass. Avoid this unless descriptor update ordering is clearly safe.
5. Update `common.glsl` to expose transparent draw buffer indices.
6. Update diagnostics to report opaque and transparent meshlet counts separately.

Acceptance criteria:
- Opaque pass renders no BLEND material meshlets.
- Transparent pass receives all BLEND material meshlets.
- No BLEND geometry participates in depth prepass.

## Step 4: Depth Prepass Rules

Depth prepass should include:
- `Opaque`
- `Mask`, with alpha discard using cutoff

Depth prepass should exclude:
- `Blend`

Tasks:
1. Update depth task/mesh shaders to use opaque draw command count only.
2. For `Mask` materials, depth pass needs access to material alpha info and albedo alpha texture.
3. Decide how to alpha-test in depth pass:
   - Add a depth fragment shader for alpha-tested materials, or
   - Split depth prepass into solid opaque and alpha-mask depth paths.

Recommended staged approach:
1. Phase A: exclude `Blend`; keep `Mask` in opaque if no visible asset currently depends on alpha mask depth.
2. Phase B: add proper alpha-mask depth pass with fragment discard.

Acceptance criteria:
- BLEND decals do not write depth.
- MASK materials can be supported without punching solid rectangular depth later.

## Step 5: Add Transparent/Decal Forward Pass

Create a new pass after `ForwardPlusPass`, e.g. `TransparentForwardPass` or `DecalBlendPass`.

Pipeline state:
- `BlendEnable = true`
- Suggested standard alpha blending:
  - `SrcColorBlendFactor = SrcAlpha`
  - `DstColorBlendFactor = OneMinusSrcAlpha`
  - `ColorBlendOp = Add`
  - `SrcAlphaBlendFactor = One`
  - `DstAlphaBlendFactor = OneMinusSrcAlpha`
  - `AlphaBlendOp = Add`
- `DepthTestEnable = true`
- `DepthWriteEnable = false`
- Depth compare remains reverse-Z compatible: `GreaterOrEqual`
- Cull mode should respect double-sided materials.

Double-sided support options:
1. Create a double-sided transparent pipeline with `CullMode = None` and route double-sided materials there.
2. Initially use `CullMode = None` for all transparent materials. This is simpler and acceptable for decals.

Tasks:
1. Extend `MeshPipeline` to create `TransparentForwardPipeline`.
2. Add `TransparentForwardPass` to the render graph after opaque forward pass.
3. Reuse forward mesh shader if draw command buffer indices are parameterized.
4. Add push constant field or shader define to choose draw command buffer base.
5. Use same fragment shader with blending enabled; keep `Blend` materials from discarding except when `alpha <= 0`.
6. Ensure color attachment `LoadOp = Load`, not `Clear`, because opaque pass already rendered color.
7. Use depth attachment read-only or depth-test-only with no writes.

Acceptance criteria:
- Decals render over opaque scene without black/grey rectangular backgrounds.
- Opaque scene color is preserved before transparent pass.
- Transparent pass does not modify depth.

## Step 6: Sorting Transparent Geometry

Transparent blending needs ordering. Decals are often coplanar or near-surface, so sorting may be less visible, but general BLEND support needs it.

Tasks:
1. Compute approximate per-object or per-meshlet distance to camera in `SceneDataBuilder`.
2. Sort transparent draw commands back-to-front for standard alpha blending.
3. Keep opaque draw commands unsorted or existing order.
4. Add stable tie-breakers to prevent flickering:
   - material index
   - object index
   - meshlet index

Acceptance criteria:
- Transparent geometry order is stable across frames.
- Camera movement does not cause obvious flicker from equal-depth decal ordering.

## Step 7: Decal-Specific Handling

Sponza dirt decals are geometry decals, not projected decals. Treat them as transparent geometry first.

Potential refinements after basic support:
1. Depth bias for decals to reduce z-fighting.
2. Separate decal pipeline with polygon offset or shader offset.
3. Premultiplied alpha support if asset textures are authored that way.
4. Optional material flag for decal priority/layering.

Tasks:
1. Inspect Sponza decal meshes for z-fighting after transparent pass is implemented.
2. If z-fighting remains, add a small depth bias for transparent decal pipeline.
3. Keep bias small and configurable.

Acceptance criteria:
- Sponza dirt decals appear as dirt overlays, not rectangular geometry.
- Decals do not shimmer or fight heavily with underlying stone surfaces.

## Step 8: Shader Alpha Semantics

Forward fragment shader should handle alpha differently by material mode.

Rules:
1. `Opaque`: ignore alpha for discard; output alpha can be 1 or material alpha, but blending is disabled.
2. `Mask`: discard when `baseAlpha <= alphaCutoff`; output opaque color.
3. `Blend`: discard only when alpha is effectively zero; output alpha for blending.

Suggested code shape:

```glsl
float alphaMode = material.NormalScaleBias.y;
float alphaCutoff = material.NormalScaleBias.z;
float outputAlpha = material.Albedo.a * albedoSample.a;

if (alphaMode > 0.5 && alphaMode < 1.5 && outputAlpha <= alphaCutoff)
    discard;
if (alphaMode > 1.5 && outputAlpha <= 0.001)
    discard;
```

Acceptance criteria:
- MASK materials cut out cleanly.
- BLEND materials blend in transparent pass.
- Opaque materials are unaffected.

## Step 9: Pipeline and Render Graph Integration

Tasks:
1. Add transparent pass after `ForwardPlusPass` in `VulkanRenderer.InitializeRenderGraph()`.
2. Ensure swapchain image layout remains `ColorAttachmentOptimal` between opaque and transparent passes.
3. Ensure transparent pass color attachment uses `LoadOp.Load`.
4. Ensure final transition to present happens after transparent pass.
5. Verify depth image layout is compatible with transparent depth testing.

Acceptance criteria:
- Render graph order is deterministic:
  1. Depth prepass
  2. Tiled light culling
  3. Opaque forward
  4. Transparent/decal forward
  5. Present transition

## Step 10: Tests

Add tests at three layers.

Material tests:
1. `BuildGpuMaterialData_EncodesAlphaModeAndCutoff`
2. `DefaultMaterial_IsOpaque`
3. `BlendMaterial_UsesBlendRenderMode`

Scene builder tests:
1. Opaque material produces opaque draw commands.
2. Mask material produces opaque/masked draw commands.
3. Blend material produces transparent draw commands only.
4. Transparent draw commands are sorted back-to-front.

Shader/pipeline tests:
1. Shader build includes transparent pass shaders.
2. Pipeline creation validates blend state for transparent pipeline.
3. GPU struct layout tests continue to pass.

Integration tests:
1. Import Sponza and assert `dirt_decal` is recognized as BLEND.
2. Assert Sponza produces nonzero transparent draw command count.
3. Assert opaque draw command count excludes BLEND decal meshlets.

## Step 11: Diagnostics

Update diagnostics to make alpha rendering visible.

Add fields:
- `OpaqueObjectCount`
- `MaskedObjectCount`
- `TransparentObjectCount`
- `OpaqueMeshletCount`
- `TransparentMeshletCount`
- `BlendMaterialCount`

Update `SampleDiagnosticsReporter` to print these counts.

Acceptance criteria:
- Running `NjulfHelloGame` clearly reports whether transparent decals are active.

## Step 12: Rollout Order

Recommended implementation order:

1. Encode alpha metadata in `GPUMaterialData`.
2. Add material classification helper and tests.
3. Split scene draw command generation into opaque and transparent lists.
4. Exclude BLEND from depth prepass and opaque pass.
5. Add transparent pipeline with blending and no depth writes.
6. Add transparent render pass after opaque pass.
7. Add basic back-to-front transparent sorting.
8. Add optional decal depth bias if Sponza decals z-fight.
9. Add diagnostics and Sponza-specific integration assertions.
10. Run full test suite and manually validate Sponza.

## Non-Goals for First Implementation

1. Full order-independent transparency.
2. Projected/deferred decal system.
3. Per-material custom blend modes.
4. Weighted blended OIT.
5. Transparent shadowing.

These can be added after basic glTF BLEND support is correct.

## Manual Validation Checklist

1. Launch `NjulfHelloGame` with Sponza.
2. Confirm black/grey rectangular artifacts on pillars and walls are gone.
3. Confirm dirt decals are visible as subtle blended overlays.
4. Move camera close to decal surfaces and check for z-fighting shimmer.
5. Rotate camera and confirm decal ordering does not flicker significantly.
6. Verify opaque lighting and normal maps are unchanged.
7. Check diagnostics for nonzero transparent object/meshlet counts.

## Risks

1. Sorting per meshlet can be expensive if done every frame for many transparent meshlets.
2. Decals may need depth bias due to coplanar geometry.
3. Using one transparent pipeline with culling disabled may render unintended backfaces for generic transparent meshes.
4. Adding bindless draw buffer indices requires keeping shader constants and C# `BindlessIndex` synchronized.
5. Alpha BLEND without premultiplied alpha may look wrong if a future asset uses premultiplied textures.

## Recommended Future Refactor

After basic support works, introduce explicit renderer material metadata instead of packing all material policy into vector spare channels. A future `GPUMaterialFlags` integer or a separate `MaterialMetadata` CPU table would make alpha mode, double-sided state, blend mode, and decal flags easier to reason about.
