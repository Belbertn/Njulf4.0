# Indie Game Renderer Production Plan

Target outcome: Njulf can ship a visually strong indie game scene, not just a technical rendering demo. The renderer should support authored glTF content, stable performance, predictable asset import, attractive lighting, post-processing, debugging tools, and production validation.

This plan assumes the current baseline already includes:
- Vulkan renderer with dynamic rendering.
- Mesh/task shader path.
- Bindless storage buffers and textures.
- CPU scene upload and meshlet draw command generation.
- Textured glTF material import for base color, normal, metallic-roughness-AO, and emissive.
- Forward+ tiled lighting.
- Depth prepass.
- Basic transparent forward pass.

## Guiding Practices

1. Build visible quality in vertical slices.
2. Keep render passes explicit and inspectable in RenderDoc.
3. Prefer stable, boring production systems over clever one-off effects.
4. Add diagnostics and tests with each renderer feature.
5. Keep content pipeline behavior deterministic.
6. Use physically plausible defaults unless the game deliberately chooses a stylized look.
7. Avoid adding many effects before the frame graph, resource lifetime, and validation story are solid.

## Phase 1: Stabilize The Current Renderer

Goal: Make the existing renderer trustworthy before adding more visual systems.

Tasks:
1. Run `dotnet build Njulf.sln` and `dotnet test Njulf.sln`.
2. Fix any failing CPU tests before adding rendering features.
3. Run `NjulfHelloGame` with Vulkan validation layers enabled.
4. Capture a frame in RenderDoc.
5. Verify render pass order:
   - Depth prepass.
   - Tiled light culling.
   - Opaque Forward+.
   - Transparent Forward+.
   - Present.
6. Verify the renderer has no validation errors during startup, resize, normal frame rendering, and shutdown.
7. Verify all buffers, textures, pipelines, and descriptor sets have debug names.
8. Add frame diagnostics for pass timings, visible meshlets, lights, texture count, material count, and uploaded bytes.
9. Remove or isolate stale plan files that no longer describe the current implementation.

Acceptance criteria:
- CPU tests pass.
- Example scene runs validation-clean.
- RenderDoc shows expected passes and no accidental per-object draw loop.
- Diagnostics are visible from `NjulfHelloGame`.

## Phase 2: Add A Real HDR Frame Pipeline

Goal: Stop writing final lighting directly to the swapchain. Modern game rendering should light into an HDR scene color target, then composite to the swapchain.

Tasks:
1. Add a renderer-owned HDR color image per swapchain size.
2. Use a format such as `R16G16B16A16_SFLOAT` for scene color.
3. Add a render target manager for transient and persistent render targets.
4. Change `ForwardPlusPass` and `TransparentForwardPass` to render into HDR scene color.
5. Add a final fullscreen composite pass that writes to the swapchain.
6. Implement linear-to-sRGB handling explicitly.
7. Add exposure control:
   - Manual exposure first.
   - Auto exposure later.
8. Add tone mapping:
   - Start with ACES fitted or Hable.
   - Keep the operator selectable for testing.
9. Add a debug view to show raw HDR scene color before tone mapping.

Acceptance criteria:
- Swapchain pass only composites final LDR output.
- Bright emissive and direct lights can exceed `1.0` before tone mapping.
- The scene no longer clips harshly under high light intensity.

## Phase 3: Add Bloom And Emissive Polish

Goal: Make emissive materials, highlights, and light sources feel intentional.

Tasks:
1. Add bright-pass extraction from HDR scene color.
2. Add downsample chain render targets.
3. Add separable or dual-filter blur.
4. Add upsample/combine pass.
5. Add bloom intensity, threshold, knee, and radius controls.
6. Ensure emissive materials contribute naturally to bloom.
7. Add debug views for bloom mask and each mip level.

Acceptance criteria:
- Emissive textures visibly glow without destroying detail.
- Bloom is stable during camera movement.
- Bloom can be disabled at runtime for comparison.

## Phase 4: Add Directional Shadows

Goal: Add the single most important missing realism feature: shadows.

Tasks:
1. Add shadow map resources for directional lights.
2. Implement a shadow depth pass using the existing mesh/task shader path where practical.
3. Start with one directional shadow map.
4. Add cascaded shadow maps for large scenes.
5. Add stable cascade fitting to reduce shimmering.
6. Add PCF filtering.
7. Add normal bias and slope-scaled depth bias controls.
8. Add shadow debug views:
   - Cascade color overlay.
   - Shadow map preview.
   - Shadow receiver factor.
9. Integrate shadow sampling into `forward.frag`.

Acceptance criteria:
- Directional light casts stable shadows.
- Resize and light direction changes remain validation-clean.
- Shadow acne and peter-panning are controllable with exposed settings.

## Phase 5: Add Spot And Point Light Shadows

Goal: Support authored gameplay lighting such as torches, lamps, flashlights, and magic effects.

Tasks:
1. Add optional shadow state per spot light.
2. Add spot shadow atlas allocation.
3. Render spot light shadow maps into the atlas.
4. Add point light cubemap or dual-paraboloid shadows only if needed.
5. Limit shadowed local lights by budget.
6. Sort or prioritize shadowed lights by screen importance.
7. Add diagnostics for shadow atlas usage and shadowed light count.

Acceptance criteria:
- Spot lights can cast shadows.
- Unshadowed lights remain cheap.
- The renderer enforces a clear shadow budget instead of silently overcommitting GPU work.

## Phase 6: Add Sky, Environment Lighting, And IBL

Goal: Make PBR materials look correct in indirect light.

Tasks:
1. Add skybox rendering from HDR cubemap.
2. Add equirectangular HDR import or conversion to cubemap.
3. Generate or load diffuse irradiance cubemap.
4. Generate or load prefiltered specular environment cubemap.
5. Add BRDF integration LUT.
6. Update PBR shader to combine:
   - Direct lighting.
   - Diffuse IBL.
   - Specular IBL.
   - Ambient occlusion.
7. Add roughness-aware environment reflection.
8. Add fallback procedural sky for projects without HDRI.
9. Add debug views for irradiance, prefiltered environment mips, and BRDF LUT.

Acceptance criteria:
- Metallic and rough surfaces look plausible without placing many fill lights.
- The hard-coded `albedo * 0.08` ambient approximation is removed or only used as a fallback.
- Indoor and outdoor scenes can be lit with a sky/environment source.

## Phase 7: Add Ambient Occlusion

Goal: Restore small-scale contact shading that direct lighting and IBL miss.

Tasks:
1. Add depth and normal inputs for screen-space AO.
2. Implement SSAO or GTAO.
3. Add bilateral blur.
4. Composite AO into indirect lighting, not direct light unless intentionally stylized.
5. Add radius, intensity, bias, and sample count controls.
6. Add debug view for raw and blurred AO.

Acceptance criteria:
- Corners, creases, and contact points gain depth.
- AO does not halo badly around silhouettes.
- AO cost is measurable in pass timing diagnostics.

## Phase 8: Add Anti-Aliasing

Goal: Remove jagged edges and shader shimmer.

Tasks:
1. Add FXAA or SMAA as a fast first pass.
2. Add jittered projection support to cameras.
3. Add motion vector render target.
4. Add TAA only after motion vectors are reliable.
5. Add responsive anti-ghosting controls.
6. Keep non-TAA fallback available for stylized games.

Acceptance criteria:
- Static geometry edges are smoother.
- Transparent decals and normal maps do not shimmer excessively.
- TAA, if enabled, does not ghost badly during camera movement.

## Phase 9: Add Fog And Atmospheric Depth

Goal: Give scenes depth, scale, and mood.

Tasks:
1. Add height fog in the composite or forward shader.
2. Add distance fog with color and density controls.
3. Add optional directional inscattering from sun direction.
4. Add local fog volumes later if the game needs them.
5. Add debug controls to isolate fog contribution.

Acceptance criteria:
- Large scenes have depth separation.
- Fog integrates with sky color and exposure.
- Fog can be art-directed per scene.

## Phase 10: Add Reflection Support

Goal: Improve metals, wet surfaces, water, windows, and polished floors.

Tasks:
1. Start with static reflection probes.
2. Add box-projected reflection probe sampling.
3. Add probe blending by volume.
4. Add probe capture and filtering tooling.
5. Add SSR only if the target game needs dynamic screen-space reflections.
6. Add planar reflections for water or mirrors if required.

Acceptance criteria:
- Metallic materials reflect plausible surroundings.
- Reflection behavior is stable and controllable.
- Probe debug volumes can be displayed.

## Phase 11: Improve Transparency And Decals

Goal: Make alpha materials and surface detail production-safe.

Tasks:
1. Keep opaque, masked, and blend draw lists separate.
2. Add alpha-tested depth pass support for masked materials.
3. Add decal depth bias controls.
4. Add material-level blend mode metadata.
5. Add projected decal system if the game needs dynamic bullet holes, grime, blood, or scorch marks.
6. Add weighted blended OIT only if regular sorted transparency is insufficient.
7. Add transparent shadow receiving where visually important.

Acceptance criteria:
- Masked foliage and fences write correct depth.
- Geometry decals do not z-fight badly.
- Transparent object sorting is deterministic.

## Phase 12: Add Animation Rendering

Goal: Support characters, creatures, props, doors, and environmental motion.

Tasks:
1. Extend asset import for skeletons, skins, joints, weights, and animation clips.
2. Add CPU animation sampling first.
3. Add GPU skinning path for production.
4. Add skinned mesh buffers and skinning matrices.
5. Add morph target support if needed for faces or deformations.
6. Add animation bounds updates for culling.
7. Add tests for imported skeleton hierarchy and clip sampling.

Acceptance criteria:
- Skinned glTF character renders correctly.
- Animation can be played, paused, blended, and looped.
- Culling does not incorrectly remove animated meshes.

## Phase 13: Add Particles And VFX

Goal: Provide gameplay polish: fire, smoke, dust, sparks, magic, impacts, rain, and UI-world effects.

Tasks:
1. Add billboard sprite rendering.
2. Add flipbook texture support.
3. Add soft particles using scene depth.
4. Add GPU particle simulation if particle counts require it.
5. Add trails and beam rendering.
6. Add emissive particles that feed bloom.
7. Add sorting and batching by material.

Acceptance criteria:
- Particles blend correctly over scene depth.
- Particle rendering is batched and budgeted.
- VFX can be authored without changing renderer code.

## Phase 14: Harden The Asset Pipeline

Goal: Make real production assets load consistently.

Tasks:
1. Add `.glb` support.
2. Add embedded glTF buffer and image support.
3. Add texture sampler import:
   - Wrap modes.
   - Min/mag filters.
   - Mip filters.
4. Add texture transform extension support.
5. Add vertex color support.
6. Add multiple UV set support.
7. Add KTX2/Basis or BCn compressed texture path.
8. Add HDR image loading for environment maps.
9. Validate color spaces:
   - sRGB for albedo and emissive.
   - Linear for normal, roughness, metallic, AO.
10. Add import diagnostics that list unsupported glTF extensions clearly.

Acceptance criteria:
- Common DCC-exported glTF and glb assets load without manual unpacking.
- Missing optional features fail gracefully or fall back visibly.
- Texture memory use is lower with compressed texture formats.

## Phase 15: Add LOD, Instancing, And World Scale Systems

Goal: Make large scenes affordable.

Tasks:
1. Add mesh LOD import or generated LODs.
2. Add distance-based LOD selection.
3. Add hysteresis to avoid LOD popping.
4. Add hardware or meshlet instancing for repeated props.
5. Add foliage batching path.
6. Add impostors for distant complex objects if needed.
7. Add occlusion culling after depth prepass or via Hi-Z.
8. Add GPU-driven compaction only after CPU systems are proven insufficient.

Acceptance criteria:
- Large scenes maintain frame time budget.
- Repeated objects do not duplicate GPU resources.
- LOD transitions are stable enough for gameplay cameras.

## Phase 16: Add Material Quality Extensions

Goal: Support a broader range of art styles and high-quality assets.

Tasks:
1. Add clearcoat.
2. Add sheen.
3. Add anisotropy.
4. Add transmission or simple glass.
5. Add subsurface approximation for skin, wax, leaves, or cloth.
6. Add per-material normal strength and emissive intensity.
7. Add material keywords or flags without creating an unmanageable pipeline explosion.

Acceptance criteria:
- Extensions are opt-in and do not slow the default path significantly.
- Shader variants remain manageable.
- Unsupported material features are reported during import.

## Phase 17: Add Render Settings And Quality Levels

Goal: Make the renderer configurable for different hardware.

Tasks:
1. Add central render settings object.
2. Add quality presets:
   - Low.
   - Medium.
   - High.
   - Ultra.
3. Expose per-feature toggles:
   - Shadows.
   - Bloom.
   - AO.
   - Fog.
   - Reflections.
   - Anti-aliasing.
4. Add resolution scale.
5. Add dynamic resolution later if needed.
6. Save and load settings from a simple config file.

Acceptance criteria:
- Expensive features can be disabled independently.
- Settings changes are safe at runtime.
- Render diagnostics show which features are active.

## Phase 18: Add Editor And Debug Tooling Hooks

Goal: Make rendering issues debuggable during content creation.

Tasks:
1. Add debug draw for lines, boxes, spheres, frustums, and light volumes.
2. Add renderer debug overlays:
   - Light tiles.
   - Cascades.
   - Reflection probes.
   - Decal volumes.
   - Bounds.
3. Add material inspection output for selected objects.
4. Add GPU timing query support per pass.
5. Add screenshot capture.
6. Add frame capture trigger integration for RenderDoc if available.

Acceptance criteria:
- Artists and programmers can diagnose lighting, culling, and material issues.
- Pass timings are available without an external profiler.
- Debug features can be compiled out or disabled for shipping.

## Phase 19: Performance And Memory Budgets

Goal: Keep visual quality inside a predictable frame budget.

Tasks:
1. Define target platforms and frame budgets.
2. Track GPU memory usage by category:
   - Mesh buffers.
   - Textures.
   - Materials.
   - Render targets.
   - Shadow maps.
3. Add per-pass GPU timings.
4. Add CPU timing around scene building and asset upload.
5. Add upload budget per frame.
6. Avoid synchronous GPU waits during normal gameplay.
7. Add stress scenes:
   - Many lights.
   - Many materials.
   - Many transparent objects.
   - Large texture set.
   - Large meshlet count.

Acceptance criteria:
- Performance regressions can be measured.
- Content budgets are documented.
- Runtime stalls are visible in diagnostics.

## Phase 20: Shipping Hardening

Goal: Make the renderer reliable enough for game production.

Tasks:
1. Test startup, resize, fullscreen/windowed switching, minimize, restore, and shutdown.
2. Test device feature rejection paths with clear error messages.
3. Test missing asset fallback behavior.
4. Test repeated load/unload of scenes.
5. Test long-running sessions for leaks and descriptor exhaustion.
6. Add crash-safe logging around renderer initialization.
7. Add GPU validation test mode gated by environment variable.
8. Add CI checks for:
   - CPU tests.
   - Shader compilation.
   - Formatting or static analysis if adopted.

Acceptance criteria:
- Renderer survives common lifecycle operations.
- Unsupported hardware fails clearly.
- Content errors point to the responsible asset path and material name.

## Recommended First Vertical Slice

The first slice should produce the biggest visual improvement with the least architectural churn:

1. Stabilize current validation and diagnostics.
2. Add HDR scene color.
3. Add tone mapping and exposure.
4. Add bloom.
5. Add directional shadows.
6. Add skybox and IBL.
7. Add SSAO.
8. Add FXAA or SMAA.

Definition of done for the first slice:
- `NjulfHelloGame` renders Sponza or equivalent sample content with textured materials, shadows, bloom, tone mapping, sky lighting, AO, and anti-aliasing.
- The frame is validation-clean.
- RenderDoc clearly shows all major passes.
- Renderer diagnostics report pass timings and active feature settings.

## Long-Term Definition Of Done

The renderer is ready for a good-looking indie game when:

1. It renders into HDR and tone maps to the swapchain.
2. It supports bloom, shadows, sky/IBL, AO, fog, and anti-aliasing.
3. It supports static and animated glTF/glb content.
4. It handles opaque, masked, transparent, decal, emissive, and common PBR materials.
5. It provides reflection probes or an equivalent reflection solution.
6. It has a robust asset pipeline with clear diagnostics.
7. It exposes quality settings and feature toggles.
8. It has pass timing, memory diagnostics, and RenderDoc-friendly debug names.
9. It remains validation-clean during startup, rendering, resize, scene reload, and shutdown.
10. It has automated tests for CPU-side contracts and optional GPU smoke tests for rendering paths.
