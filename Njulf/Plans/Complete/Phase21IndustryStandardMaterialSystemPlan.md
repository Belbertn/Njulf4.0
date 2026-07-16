# Phase 21 Industry Standard Material System Plan

Goal: evolve Njulf's Phase 16 material foundation into a production-grade, glTF-aligned, high-quality material system with physically plausible layered shading, correct texture handling, robust diagnostics, and bounded performance.

This plan builds on the current implementation:

1. Base metallic-roughness PBR is already present.
2. Material feature flags and optional `GPUMaterialExtensionData` are already present.
3. glTF parsing exists for clearcoat, sheen, anisotropy, transmission, IOR, volume approximation fields, and emissive strength.
4. Shader support exists as first-pass approximations for clearcoat, sheen, anisotropy, transmission, subsurface, and emissive strength.
5. Procedural material-quality test spheres exist.

Primary standards targets:

1. glTF 2.0 core metallic-roughness material model.
2. `KHR_texture_transform`
3. `KHR_materials_clearcoat`
4. `KHR_materials_sheen`
5. `KHR_materials_anisotropy`
6. `KHR_materials_transmission`
7. `KHR_materials_ior`
8. `KHR_materials_volume`
9. `KHR_materials_emissive_strength`
10. `KHR_materials_specular`
11. `KHR_materials_iridescence`
12. `KHR_materials_dispersion`

References:

1. Khronos glTF PBR property list: https://www.khronos.org/gltf/pbr/
2. Khronos glTF extension repository: https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos
3. Khronos glTF Sample Viewer: https://github.com/KhronosGroup/glTF-Sample-Viewer
4. glTF Validator: https://github.com/KhronosGroup/glTF-Validator

## Guiding Principles

1. Keep the default opaque PBR path fast.
2. Keep feature data in optional extension payloads.
3. Do not create per-feature graphics pipelines.
4. Use feature flags to skip extension buffer reads and texture samples.
5. Respect glTF texture color spaces, channels, transforms, and UV sets.
6. Preserve physical plausibility and energy limits over dramatic but incorrect effects.
7. Add tests and diagnostics with every material feature.
8. Prefer staged vertical slices that compile, render, and validate cleanly.
9. Keep manual approximations honest in docs and diagnostics.

## Step 1: Establish Baseline And Conformance Assets

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run `NjulfHelloGame` with Vulkan validation layers enabled.
4. Capture RenderDoc frames for:
   - current glTF sample scene
   - `SampleReflectionTestSpheres`
   - material-quality test spheres
5. Record:
   - material count
   - material extension payload count
   - texture count
   - material upload bytes
   - material extension upload bytes
   - opaque forward GPU time
   - transparent forward GPU time
   - shader compile status
6. Add a local material conformance asset folder:
   - `Assets/TestMaterials/glTF/`
   - small synthetic glTFs for each supported extension
   - curated Khronos sample assets where licenses permit
7. Add a documented manual comparison workflow against the Khronos glTF Sample Viewer.

Acceptance criteria:

1. Build and tests pass before new work starts.
2. Validation is clean before new features are added.
3. Baseline diagnostics and screenshots exist for comparison.

## Step 2: Harden Material Data Contracts

Tasks:

1. Audit `GPUMaterialData` and `GPUMaterialExtensionData` for remaining space.
2. Decide whether to split high-end features into a second optional payload:
   - keep common extensions in `GPUMaterialExtensionData`
   - put rare high-end data in `GPUMaterialAdvancedExtensionData` if needed
3. Add explicit shader constants for every feature flag.
4. Add explicit offset constants for new extension struct fields that are layout-critical.
5. Add tests for:
   - C# and GLSL struct sizes
   - extension struct offsets
   - bindless buffer index parity
   - no-extension default material sentinel values
6. Make `MaterialManager` report:
   - extension payload count
   - advanced payload count if added
   - extension upload bytes
   - advanced upload bytes if added
7. Add validation for feature flags that require payloads.

Acceptance criteria:

1. Default materials still have `FeatureFlags = 0` and `ExtensionDataIndex = -1`.
2. Extension materials are deduplicated by base data, metadata, and extension data.
3. Invalid extension texture indices throw named errors.

## Step 3: Add Per-Extension Texture Coordinates And Transforms

Rationale: industry-standard glTF support requires each texture slot to respect its own `texCoord` and `KHR_texture_transform`.

Tasks:

1. Add compact transform data for extension texture slots.
2. Support at least UV0 and UV1 for every extension texture.
3. Store transform as:
   - offset
   - scale
   - rotation
   - texCoord set
4. Decide layout:
   - option A: append transform fields to `GPUMaterialExtensionData`
   - option B: create a compact extension texture transform array
5. Update importer mapping for:
   - clearcoat texture
   - clearcoat roughness texture
   - clearcoat normal texture
   - sheen color texture
   - sheen roughness texture
   - anisotropy texture
   - transmission texture
   - thickness texture
   - specular texture
   - specular color texture
   - iridescence texture
   - iridescence thickness texture
   - dispersion, if texture-backed in future revisions
6. Update shader sampling helpers:
   - `SelectUv(texCoordSet)`
   - `ApplyTextureTransform`
   - feature-specific UV helpers
7. Add importer diagnostics for unsupported UV sets above UV1.

Acceptance criteria:

1. Extension textures use their own UV transforms.
2. Base texture transforms remain unchanged.
3. Synthetic glTFs prove different UV sets work for extension textures.

## Step 3.5: Complete Double-Sided Material Support

Current state: glTF `doubleSided` is parsed and stored on `ModelMaterial`, packed into `GPUMaterialData.NormalScaleBias.w`, and mirrored into `MaterialRenderMetadata.SurfaceFlags`. The renderer does not yet make culling decisions per material for opaque, masked depth, shadow, and forward pipelines, so this is not complete production double-sided rendering.

Tasks:

1. Keep parsing glTF core `doubleSided`.
2. Preserve the material metadata flag:
   - `MaterialSurfaceFlags.DoubleSided`
   - `GPUMaterialData.NormalScaleBias.w`
3. Split draw command lists or pipeline dispatches by culling requirement:
   - single-sided opaque
   - double-sided opaque
   - single-sided masked depth
   - double-sided masked depth
   - single-sided shadow alpha
   - double-sided shadow alpha
4. Add no-cull pipeline variants only for double-sided material groups.
5. Keep default single-sided materials on back-face culling for performance.
6. In fragment shading, handle back faces correctly:
   - use `gl_FrontFacing`
   - flip normal/tangent basis for back faces where needed
   - keep normal maps stable on both sides
7. Validate double-sided transparent behavior, which already uses no culling in the current transparent pipeline.
8. Add diagnostics:
   - double-sided material count
   - double-sided opaque draw count
   - double-sided masked draw count
9. Add tests:
   - importer preserves `doubleSided`
   - upload packs the flag
   - scene builder classifies double-sided draw groups
   - shader compiles with back-face normal handling
10. Add a sample material:
   - `MaterialQuality.DoubleSidedCloth`
   - thin cloth/leaf/card mesh that proves both sides render and shade correctly

Acceptance criteria:

1. glTF double-sided materials render both front and back faces.
2. Default materials still use back-face culling.
3. Back-face normal mapping is visually stable.
4. Double-sided masked alpha works in depth and shadow passes.
5. Vulkan validation remains clean.

## Step 4: Implement `KHR_materials_specular`

Rationale: specular is a core part of modern glTF material authoring and should be implemented before more exotic effects.

Data fields:

1. `SpecularFactor`, default `1`.
2. `SpecularColor`, default white.
3. `SpecularTexture`.
4. `SpecularColorTexture`.

Tasks:

1. Add feature flags:
   - `Specular`
   - `SpecularTexture`
   - `SpecularColorTexture`
2. Extend `ModelMaterial`.
3. Parse:
   - `specularFactor`
   - `specularTexture`
   - `specularColorFactor`
   - `specularColorTexture`
4. Resolve textures:
   - specular texture: linear
   - specular color texture: sRGB
5. Pack data into extension payload.
6. Update BRDF:
   - compute dielectric F0 from IOR
   - apply specular strength
   - apply specular color to dielectric specular
   - keep metals controlled primarily by base color and metallic workflow
7. Add direct-light and IBL integration.
8. Add debug view for specular factor and color.
9. Add tests:
   - importer field parsing
   - texture color-space policy
   - GPU payload packing
   - shader compilation

Acceptance criteria:

1. Non-metallic materials can have reduced or tinted specular.
2. Metallic base behavior remains stable.
3. Specular disabled path matches previous output.

## Step 5: Correct Clearcoat To Production Quality

Current state: visible approximation exists.

Tasks:

1. Apply clearcoat normal texture and clearcoat normal scale.
2. Evaluate clearcoat as a second dielectric specular lobe.
3. Add direct-light clearcoat contribution.
4. Add IBL clearcoat contribution.
5. Apply energy compensation so base layer is reduced by clearcoat Fresnel.
6. Use clearcoat roughness texture green channel.
7. Use clearcoat texture red channel.
8. Keep clearcoat normal independent from base normal.
9. Add debug views:
   - clearcoat factor
   - clearcoat roughness
   - clearcoat normal
10. Add tests for:
   - imported texture channels
   - payload packing
   - shader compilation

Acceptance criteria:

1. Clearcoat adds a glossy top reflection without over-brightening the base layer.
2. Clearcoat normal visibly affects only the coat highlight.
3. Disabled clearcoat matches baseline.

## Step 6: Correct Sheen To Production Quality

Current state: visible rim approximation exists.

Tasks:

1. Implement glTF-compatible sheen BRDF approximation.
2. Integrate sheen into direct lighting.
3. Integrate sheen into IBL.
4. Respect:
   - sheen color RGB
   - sheen color texture RGB, sRGB
   - sheen roughness factor
   - sheen roughness texture alpha
5. Apply energy compensation with the base material.
6. Make sheen coexist correctly with clearcoat.
7. Add debug views:
   - sheen color
   - sheen roughness
8. Add tests for parsing, upload, and shader compilation.

Acceptance criteria:

1. Velvet and cloth samples show soft grazing/back-scatter response.
2. Clearcoat remains visibly above sheen when both are active.
3. Strength zero matches baseline.

## Step 7: Correct Anisotropy To Production Quality

Current state: roughness-only approximation exists.

Tasks:

1. Implement anisotropic GGX distribution.
2. Use tangent and bitangent basis from vertex data.
3. Rotate tangent basis by `anisotropyRotation`.
4. Decode anisotropy texture:
   - red/green encode tangent-space direction
   - blue multiplies strength
5. Use anisotropic alpha X/Y from roughness and strength.
6. Add fallback:
   - if tangents are missing or degenerate, disable anisotropic branch and report diagnostic.
7. Add anisotropic IBL approximation:
   - first pass: bent reflection direction or tangent-adjusted reflection
   - later pass: optional lookup or preintegration improvement
8. Add debug views:
   - anisotropy strength
   - anisotropy direction
9. Add tests:
   - importer parsing
   - texture channel policy
   - tangent fallback diagnostics
   - shader compilation

Acceptance criteria:

1. Brushed metal highlights elongate along tangent direction.
2. Rotation changes highlight orientation.
3. Strength zero matches isotropic baseline.

## Step 8: Implement Real Transmission, IOR, Volume, And Dispersion

Current state: environment fallback transmission exists.

Tasks:

1. Add opaque scene color copy after opaque forward and before transparent forward.
2. Register copied opaque scene color in the bindless texture table.
3. Transition copied image to shader-read layout before transparent forward.
4. Prevent feedback loops by never sampling the current HDR target while writing to it.
5. Use IOR for Fresnel reflection and refraction offset.
6. Keep reflective surface contribution at high transmission.
7. Use roughness to blur or mip-select transmitted scene color.
8. Implement volume attenuation:
   - thickness factor
   - thickness texture green channel
   - attenuation color
   - attenuation distance
9. Add `KHR_materials_dispersion`:
   - parse `dispersion`
   - pack scalar
   - apply chromatic refraction offsets for RGB
   - keep quality toggle because this costs extra samples
10. Add transparency sorting rules:
   - transmission materials are transparent-pass materials
   - preserve deterministic sorting
   - document no order-independent transparency in this phase
11. Add diagnostics:
   - opaque scene color copy enabled
   - copy bytes
   - transmission material count
   - volume material count
   - dispersion material count
12. Add debug views:
   - transmission factor
   - IOR
   - thickness
   - attenuation color
   - dispersion
13. Add tests:
   - importer parsing
   - payload packing
   - transparent classification
   - render graph transition validation where testable
   - shader compilation

Acceptance criteria:

1. Glass is not visually equivalent to alpha fade.
2. Reflection remains visible at full transmission.
3. Volume attenuation tints transmitted light by thickness.
4. Dispersion produces controlled chromatic separation.
5. Vulkan validation reports no render-target feedback loop.

## Step 9: Implement `KHR_materials_iridescence`

Tasks:

1. Add feature flags:
   - `Iridescence`
   - `IridescenceTexture`
   - `IridescenceThicknessTexture`
2. Extend material data:
   - iridescence factor
   - iridescence IOR
   - iridescence thickness minimum
   - iridescence thickness maximum
   - texture indices
   - texture transforms
3. Parse:
   - `iridescenceFactor`
   - `iridescenceTexture`
   - `iridescenceIor`
   - `iridescenceThicknessMinimum`
   - `iridescenceThicknessMaximum`
   - `iridescenceThicknessTexture`
4. Resolve textures as linear data maps.
5. Implement thin-film Fresnel approximation.
6. Apply only to dielectric-facing specular unless metallic behavior is explicitly validated against glTF reference behavior.
7. Integrate with:
   - base specular
   - clearcoat ordering
   - IOR
8. Add quality toggle:
   - approximate iridescence
   - high-quality spectral approximation
9. Add debug views:
   - iridescence factor
   - thickness
   - resulting tint
10. Add tests and sample spheres.

Acceptance criteria:

1. Oil-film and soap-bubble style assets show view-dependent hue shifts.
2. Factor zero matches baseline.
3. Clearcoat and iridescence ordering is documented and stable.

## Step 10: Upgrade Volume Beyond First Approximation

Tasks:

1. Improve thickness handling for closed meshes where possible.
2. Keep authored thickness texture support for real-time path.
3. Add attenuation approximation for:
   - thin objects
   - thick glass
   - colored liquid-like materials
4. Document limitations:
   - no multi-bounce scattering
   - no ray-traced caustics
   - no path-traced interior scattering
5. Add optional screen-depth based thickness approximation behind a quality toggle.
6. Add diagnostics when volume is used without transmission.

Acceptance criteria:

1. `KHR_materials_volume` assets look materially different from thin transmission.
2. Invalid or unsupported volume combinations produce actionable diagnostics.

## Step 11: Add Artist-Facing Material Debug Views

Tasks:

1. Add material debug enum values for:
   - feature flags
   - base color
   - metallic
   - roughness
   - normal strength
   - emissive intensity
   - specular factor
   - specular color
   - clearcoat factor
   - clearcoat roughness
   - sheen color
   - sheen roughness
   - anisotropy strength
   - anisotropy direction
   - transmission
   - IOR
   - volume thickness
   - attenuation
   - iridescence factor
   - iridescence thickness
   - dispersion
   - subsurface strength
2. Expose controls in `SampleInputController`.
3. Print concise diagnostics in `SampleDiagnosticsReporter`.
4. Keep debug views cheap and explicit.

Acceptance criteria:

1. Every supported material feature can be isolated visually.
2. Diagnostics show feature counts and extension upload bytes.

## Step 12: Improve Import Diagnostics And Asset Validation

Tasks:

1. Track supported extension names used per asset.
2. Track unsupported extension names used per asset.
3. Include material index and material name in extension warnings.
4. Include texture role in missing texture errors.
5. Report unsupported UV set usage.
6. Report unsupported extension combinations:
   - `KHR_materials_pbrSpecularGlossiness`
   - `KHR_materials_unlit`, unless separately implemented
7. Add optional command or tool step to run glTF Validator against test assets.
8. Add import diagnostics tests with synthetic glTFs.

Acceptance criteria:

1. Asset authors can tell exactly which material feature is missing or approximated.
2. Unsupported extensions never silently disappear.

## Step 13: Add Material Quality Settings

Tasks:

1. Add renderer material quality enum:
   - Low
   - Medium
   - High
   - Ultra
2. Define feature behavior per quality:
   - clearcoat direct only vs direct plus IBL
   - sheen approximation vs full direct and IBL
   - anisotropic direct only vs anisotropic IBL approximation
   - transmission environment fallback vs scene-color refraction
   - dispersion off vs enabled
   - iridescence approximate vs high-quality spectral
3. Keep pipeline count unchanged.
4. Pass quality settings through push constants or a compact renderer settings buffer.
5. Add diagnostics that print active quality behavior.

Acceptance criteria:

1. Low quality remains fast on integrated GPUs.
2. High/Ultra enable the complete material model.
3. Default materials remain unaffected.

## Step 14: Expand Sample Scene Coverage

Tasks:

1. Keep existing reflection row.
2. Keep current material-quality row.
3. Add a second material-quality row:
   - specular tinted dielectric
   - clearcoat with separate normal
   - sheen with texture-backed color
   - anisotropy rotated sample
   - scene-refraction glass
   - volume colored glass
   - iridescent film
   - dispersion diamond/glass
4. Add small authored textures if repository policy allows.
5. Name objects consistently:
   - `MaterialQuality.SpecularTint`
   - `MaterialQuality.ClearcoatNormal`
   - `MaterialQuality.SheenTextured`
   - `MaterialQuality.AnisotropyRotated`
   - `MaterialQuality.SceneGlass`
   - `MaterialQuality.VolumeGlass`
   - `MaterialQuality.IridescenceFilm`
   - `MaterialQuality.DispersionGlass`
6. Add simple labels only if there is an existing in-world label system.

Acceptance criteria:

1. One sample view exercises every material feature.
2. Debug views clearly isolate each feature.
3. Sample assets do not hide broken imported material paths.

## Step 15: Add Reference And Regression Tests

CPU tests:

1. `GPUStructLayoutTests`
2. `BindlessIndexTests`
3. `MaterialManagerTests`
4. `ModelRenderUploadServiceTests`
5. `ContentRendererIntegrationTests`
6. `RendererDiagnosticsTests`

Shader tests:

1. all material shaders compile
2. feature flag constants exist
3. extension read helpers exist
4. shader interface locations match across mesh and fragment shaders

Visual regression tests:

1. capture deterministic screenshots where practical
2. compare material debug views first, beauty screenshots second
3. keep thresholds loose enough for driver differences

Acceptance criteria:

1. Tests prove data mapping, not just compilation.
2. Shader interface mismatch is caught before Vulkan validation at runtime.

## Step 16: Performance Validation

Tasks:

1. Create default-material stress scene:
   - many materials
   - no extensions
   - verify no extension payload reads or samples
2. Create extension-material stress scene:
   - many materials
   - mixed features
   - clear draw sorting by material index
3. Capture RenderDoc and GPU timing for:
   - default opaque PBR
   - clearcoat
   - sheen
   - anisotropy
   - transmission
   - volume
   - iridescence
   - dispersion
4. Document cost per feature.
5. Add performance diagnostics:
   - extension material count by feature
   - opaque scene color copy cost
   - extra transmission samples
   - material quality level

Acceptance criteria:

1. Default path regression is negligible.
2. Expensive features have explicit quality controls.
3. Feature cost is visible in diagnostics.

## Step 17: Documentation

Tasks:

1. Update renderer documentation with a material support table:
   - feature
   - glTF extension
   - importer support
   - shader support
   - texture channels
   - color space
   - current limitations
2. Document the GPU material layout.
3. Document how to add a future material extension.
4. Document known limitations:
   - no path tracing
   - no caustics
   - no order-independent transparency
   - no full subsurface diffusion profile
   - screen-space transmission limitations
5. Add authoring recommendations:
   - prefer glTF metallic-roughness
   - use correct texture color spaces
   - avoid unsupported UV sets until implemented
   - validate assets with glTF Validator

Acceptance criteria:

1. A new contributor can implement another material extension safely.
2. Artists can understand which glTF material features are supported.

## Step 18: Definition Of Done

Phase 21 is complete when:

1. `dotnet build Njulf.sln` passes.
2. `dotnet test Njulf.sln` passes.
3. Vulkan validation is clean on startup, resize, render, and shutdown.
4. RenderDoc shows no descriptor errors and no target feedback loops.
5. Default PBR materials remain fast and skip extension payload reads.
6. glTF material extensions are parsed with correct defaults, channels, color spaces, and texture transforms.
7. Supported material features:
   - specular
   - clearcoat
   - sheen
   - anisotropy
   - transmission
   - IOR
   - volume attenuation
   - emissive strength
   - iridescence
   - dispersion
   - Njulf-specific subsurface approximation
8. Unsupported features produce actionable diagnostics.
9. Material debug views cover every supported feature.
10. Sample scene demonstrates every supported material feature.
11. Performance costs are measured and documented.

## Recommended PR Breakdown

PR 1: Texture transforms and diagnostics hardening.

PR 2: `KHR_materials_specular`.

PR 3: production clearcoat and sheen.

PR 4: production anisotropy.

PR 5: opaque scene color copy and real transmission.

PR 6: full volume attenuation and dispersion.

PR 7: iridescence.

PR 8: material debug views and sample expansion.

PR 9: conformance assets, performance measurements, and documentation.
