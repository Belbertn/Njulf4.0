# Phase 16 Material Quality Extensions Plan

Goal: add production-ready material extensions without turning the renderer into an untestable shader-variant matrix. Phase 16 should make Njulf handle clearcoat, sheen, anisotropy, simple glass/transmission, a practical subsurface approximation, per-material normal strength, and emissive intensity while preserving the fast default opaque PBR path.

The implementation should follow the glTF 2.0 material extension model where possible:

1. `KHR_materials_clearcoat`
2. `KHR_materials_sheen`
3. `KHR_materials_anisotropy`
4. `KHR_materials_transmission`
5. `KHR_materials_ior`
6. `KHR_materials_volume` only for parsing and diagnostics in the first slice
7. `KHR_materials_emissive_strength`

References:

1. https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_materials_clearcoat/README.md
2. https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_materials_sheen/README.md
3. https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_materials_anisotropy/README.md
4. https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_materials_transmission/README.md
5. https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_materials_volume/README.md
6. https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html

## Current Baseline

The current material system is compact and simple:

1. `Njulf.Rendering/Data/GPUStructs.cs`
   - `GPUMaterialData` is 96 bytes.
   - It contains base color, emissive color, normal scale and alpha data, metallic/roughness/AO factors, UV transform, and four texture indices.
2. `Njulf.Shaders/common.glsl`
   - Mirrors `GPUMaterialData`.
   - Defines `SIZEOF_GPU_MATERIAL_DATA = 96`.
   - Reads material data from the bindless material storage buffer.
3. `Njulf.Rendering/Resources/MaterialManager.cs`
   - Owns material registration, deduplication, lifetime tracking, GPU upload, and texture-index validation.
4. `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
   - Maps `ModelMaterial` into `GPUMaterialData`.
   - Resolves albedo, normal, metallic-roughness, and emissive textures.
   - Uses `Emissive.rgb` directly with emissive texture RGB.
   - Already supports per-material normal scale through `NormalScaleBias.x`.
5. `Njulf.Assets/ModelImporter.cs`
   - Has a JSON manifest pass for `.gltf`.
   - Reads base PBR material values and texture paths.
   - Rejects embedded buffers and buffer-view images.
   - Does not read `KHR_materials_*` extensions beyond core PBR behavior.
6. `Njulf.Shaders/forward.frag`
   - Uses a metallic-roughness GGX BRDF.
   - Samples IBL, reflection probes, shadows, AO, and emissive.
   - Has no material feature flags.
7. `NjulfHelloGame/SampleReflectionTestSpheres.cs`
   - Provides a useful reflection and roughness test row.
   - It does not yet exercise material extension features.

Important current constraints:

1. `GPUStructLayoutTests` parses `common.glsl`, so every GPU struct size and offset change must update C# and GLSL together.
2. `MaterialManager.MaterialDataComparer` must include any new fields that affect rendered output.
3. `MaterialManager.ValidateMaterialTextureIndices` must validate every new bindless texture index.
4. `SceneDataBuilder` sorts opaque draw commands by material index, which helps keep material-feature branches coherent.
5. The default path must still render existing assets without changing visual output except for intentional emissive-strength default handling.

## Non-Goals

Do not let Phase 16 expand into a full offline-quality material renderer.

1. Do not add path tracing, ray-traced refraction, or multi-bounce transmission.
2. Do not add full skin diffusion profiles in the first implementation.
3. Do not add a large number of pipeline permutations for every material feature combination.
4. Do not implement `KHR_materials_pbrSpecularGlossiness`; report it as unsupported because it is incompatible with several modern material extensions and with the current metallic-roughness shader path.
5. Do not implement `KHR_materials_unlit` as part of Phase 16 unless a sample asset proves it is blocking import. If detected, report it clearly.
6. Do not support embedded/buffer-view extension textures until Phase 14 embedded image support exists.
7. Do not make transmission sample from the same HDR image that the pass is writing to. If screen refraction is implemented, use a copied opaque scene color input.
8. Do not add volume scattering in the first slice. Parse `KHR_materials_volume` fields and either approximate attenuation for simple glass or report the unsupported portion.
9. Do not remove existing alpha coverage behavior for normal blend and mask materials.

## Target Outcome

After Phase 16:

1. Existing PBR materials render the same and remain cheap.
2. glTF files using supported material extensions import deterministically.
3. Unsupported material extensions produce actionable diagnostics with material names and extension names.
4. Clearcoat adds a second glossy layer suitable for varnish, car paint, ceramic, and lacquered props.
5. Sheen supports cloth and velvet-like rim response.
6. Anisotropy supports brushed metals using the existing vertex tangent basis.
7. Simple glass supports optical transparency better than alpha-as-coverage.
8. Subsurface approximation gives skin, wax, cloth, and thin leaves a controlled soft-lighting option.
9. Emissive intensity is separate from emissive color and texture.
10. Material feature flags remain manageable and visible in diagnostics.
11. `SampleReflectionTestSpheres` contains a focused material-quality validation scene.

## Production Strategy

Use progressive slices. Each slice must compile shaders, pass CPU tests, and keep Vulkan validation clean before moving on.

Mandatory slices:

1. Material data contract and diagnostics.
2. glTF extension parsing and CPU-side upload mapping.
3. Clearcoat.
4. Emissive intensity and normal-strength validation.
5. Sheen.
6. Anisotropy.
7. Simple glass/transmission.
8. Subsurface approximation.
9. Sample scene and debug views.
10. Tests, performance validation, and documentation.

Recommended implementation order:

1. Build the data model and feature flags first.
2. Wire importer diagnostics before shading changes.
3. Add one feature at a time to `forward.frag`.
4. Add sample spheres as each feature becomes visible.
5. Add performance measurements after all feature branches exist.

## Slice 0: Baseline And Safety Checks

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run `NjulfHelloGame` with validation enabled.
4. Capture a RenderDoc frame of the current sample.
5. Record:
   - material count
   - texture count
   - material upload bytes
   - forward pass timing
   - transparent forward pass timing
   - shader compile status
6. Capture screenshots of:
   - existing Sponza/material sample
   - `SampleReflectionTestSpheres`
7. Save the baseline numbers in the implementation PR or local notes.

Acceptance criteria:

1. Existing tests pass before Phase 16 changes.
2. Baseline screenshots exist for visual comparison.
3. Validation output is clean before new features are added.

## Slice 1: Define Material Feature Flags

Add a single source of truth for material features.

Recommended flags:

```csharp
[Flags]
public enum MaterialFeatureFlags : uint
{
    None = 0,
    Clearcoat = 1u << 0,
    ClearcoatTexture = 1u << 1,
    ClearcoatRoughnessTexture = 1u << 2,
    ClearcoatNormalTexture = 1u << 3,
    Sheen = 1u << 4,
    SheenColorTexture = 1u << 5,
    SheenRoughnessTexture = 1u << 6,
    Anisotropy = 1u << 7,
    AnisotropyTexture = 1u << 8,
    Transmission = 1u << 9,
    TransmissionTexture = 1u << 10,
    VolumeApproximation = 1u << 11,
    Subsurface = 1u << 12,
    SubsurfaceTexture = 1u << 13,
    EmissiveStrength = 1u << 14
}
```

Implementation notes:

1. Store flags as unsigned data in the GPU contract. Do not encode them through float comparisons.
2. Keep `MaterialRenderMode` separate from quality features. Alpha mode is a render-list decision; clearcoat, sheen, anisotropy, emissive strength, and subsurface are shader decisions.
3. Add helper methods:
   - `MaterialFeatureFlagsExtensions.HasAnyExtensionLighting()`
   - `MaterialFeatureFlagsExtensions.RequiresTransparentPass()`
   - `MaterialFeatureFlagsExtensions.RequiresOpaqueSceneColorInput()`
4. Add tests for flag packing and transparent-pass classification.

Files likely touched:

1. New `Njulf.Rendering/Data/MaterialFeatureFlags.cs`
2. `Njulf.Rendering/Data/MaterialRenderMode.cs`
3. `Njulf.Tests/MaterialFeatureFlagsTests.cs`

Acceptance criteria:

1. Feature flags are readable from C# and GLSL.
2. Transmission can be classified without changing normal blend materials.
3. Existing material render-mode tests still pass.

## Slice 2: Extend The GPU Material Contract

Industry-standard goal: the default material path should pay almost nothing for absent extensions, but feature materials should have all required data available without creating many pipelines.

Recommended contract:

1. Keep the current base material fields first for readability:
   - `Albedo`
   - `Emissive`
   - `NormalScaleBias`
   - `MetallicRoughnessAO`
   - `TexCoordOffsetScale`
   - four base texture indices
2. Append one small control block to `GPUMaterialData`:
   - `uint FeatureFlags`
   - `int ExtensionDataIndex`
   - `uint Reserved0`
   - `uint Reserved1`
3. Add a new optional storage buffer for extension payloads:
   - `GPUMaterialExtensionData`
   - one entry only for materials with `ExtensionDataIndex >= 0`
   - default materials keep `FeatureFlags = 0` and `ExtensionDataIndex = -1`
4. Register the extension storage buffer in `BindlessIndex`.

Recommended `GPUMaterialExtensionData` fields:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUMaterialExtensionData
{
    public Vector4 Clearcoat;          // x factor, y roughness, z normal scale, w reserved
    public Vector4 SheenColor;         // rgb color, a roughness
    public Vector4 Anisotropy;         // x strength, y rotation, z reserved, w reserved
    public Vector4 Transmission;       // x factor, y ior, z thickness, w attenuation distance
    public Vector4 AttenuationColor;   // rgb color, a reserved
    public Vector4 Subsurface;         // rgb color, a strength
    public int ClearcoatTextureIndex;
    public int ClearcoatRoughnessTextureIndex;
    public int ClearcoatNormalTextureIndex;
    public int SheenColorTextureIndex;
    public int SheenRoughnessTextureIndex;
    public int AnisotropyTextureIndex;
    public int TransmissionTextureIndex;
    public int ThicknessTextureIndex;
    public int SubsurfaceTextureIndex;
    public int Padding0;
    public int Padding1;
    public int Padding2;
}
```

Why separate extension data:

1. Most materials remain base PBR and only read `GPUMaterialData`.
2. The larger extension payload is only read when `FeatureFlags != 0`.
3. The data model can grow without forcing every material to carry many texture slots.
4. It keeps shader branching centralized in one extension evaluation path.

Implementation notes:

1. Add `GPUMaterialExtensionData` to `GPUStructs.cs`.
2. Update `common.glsl`:
   - GLSL struct definition.
   - `SIZEOF_GPU_MATERIAL_DATA`.
   - `SIZEOF_GPU_MATERIAL_EXTENSION_DATA`.
   - field offsets for new critical fields.
   - `ReadMaterialExtension(uint extensionIndex)`.
3. Add a new bindless storage index:
   - `MATERIAL_EXTENSION_DATA_BUFFER_INDEX`
   - update `STATIC_BUFFER_COUNT`
   - update `BindlessIndex`
   - update `BindlessIndexTests`
4. Update `MaterialManager`:
   - store extension data alongside material slots
   - deduplicate on base and extension data
   - upload the extension buffer when dirty
   - expose extension upload bytes and count
   - validate all extension texture indices
5. Add a CPU-only path for tests, matching current `MaterialManager()` behavior.
6. Keep `ExtensionDataIndex = -1` for `MaterialFeatureFlags.None`.
7. Use default white, black, or normal textures for absent extension textures:
   - intensity/roughness masks: white when factor should pass through, black when feature is disabled
   - normal maps: default normal
   - color textures: white when multiplied by factor, black only when feature disabled by factor

Files likely touched:

1. `Njulf.Rendering/Data/GPUStructs.cs`
2. `Njulf.Shaders/common.glsl`
3. `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
4. `Njulf.Rendering/Descriptors/BindlessIndex.cs` if present in this project
5. `Njulf.Rendering/Resources/MaterialManager.cs`
6. `Njulf.Tests/GPUStructLayoutTests.cs`
7. `Njulf.Tests/BindlessIndexTests.cs`
8. `Njulf.Tests/MaterialManagerTests.cs`

Acceptance criteria:

1. Existing default material has no extension payload.
2. Existing material snapshots still work.
3. New struct sizes match between C# and GLSL.
4. Material upload diagnostics include base and extension upload bytes.
5. Invalid extension texture indices throw named errors.

## Slice 3: Extend Asset-Side Material Data

Add imported material fields before touching shader logic.

Recommended `ModelMaterial` additions:

```csharp
public MaterialFeatureFlags FeatureFlags { get; set; }

public float EmissiveStrength { get; set; } = 1f;

public float ClearcoatFactor { get; set; }
public float ClearcoatRoughness { get; set; }
public float ClearcoatNormalScale { get; set; } = 1f;
public string? ClearcoatTexturePath { get; set; }
public string? ClearcoatRoughnessTexturePath { get; set; }
public string? ClearcoatNormalTexturePath { get; set; }

public Vector4 SheenColor { get; set; } = Vector4.Zero;
public float SheenRoughness { get; set; }
public string? SheenColorTexturePath { get; set; }
public string? SheenRoughnessTexturePath { get; set; }

public float AnisotropyStrength { get; set; }
public float AnisotropyRotation { get; set; }
public string? AnisotropyTexturePath { get; set; }

public float TransmissionFactor { get; set; }
public float Ior { get; set; } = 1.5f;
public float ThicknessFactor { get; set; }
public float AttenuationDistance { get; set; } = float.PositiveInfinity;
public Vector4 AttenuationColor { get; set; } = new Vector4(1f, 1f, 1f, 1f);
public string? TransmissionTexturePath { get; set; }
public string? ThicknessTexturePath { get; set; }

public Vector4 SubsurfaceColor { get; set; } = new Vector4(1f, 1f, 1f, 1f);
public float SubsurfaceStrength { get; set; }
public string? SubsurfaceTexturePath { get; set; }
```

Implementation notes:

1. Keep defaults physically neutral.
2. Clamp imported values in renderer upload, not deep inside the importer, so tests can verify mapping behavior.
3. Preserve material names for diagnostics.
4. Add a list of unsupported material extension names to import diagnostics.
5. Add a list of supported material extension names actually used by the asset.
6. Keep `ModelMaterial.Default` extension-free.

Files likely touched:

1. `Njulf.Assets/ModelImporter.cs`
2. `Njulf.Assets/IModelRenderUploadService.cs`
3. `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
4. `Njulf.Tests/ContentRendererIntegrationTests.cs`
5. `Njulf.Tests/ModelRenderUploadServiceTests.cs`

Acceptance criteria:

1. A `ModelMaterial` can represent all targeted Phase 16 features.
2. Default material output remains unchanged except `EmissiveStrength = 1`.
3. Diagnostics can report extension usage counts.

## Slice 4: Parse glTF Material Extensions

Extend the JSON manifest path in `ModelImporter`.

Supported extension parsing:

1. `KHR_materials_clearcoat`
   - `clearcoatFactor`
   - `clearcoatTexture`
   - `clearcoatRoughnessFactor`
   - `clearcoatRoughnessTexture`
   - `clearcoatNormalTexture`
   - `clearcoatNormalTexture.scale`
2. `KHR_materials_sheen`
   - `sheenColorFactor`
   - `sheenColorTexture`
   - `sheenRoughnessFactor`
   - `sheenRoughnessTexture`
3. `KHR_materials_anisotropy`
   - `anisotropyStrength`
   - `anisotropyRotation`
   - `anisotropyTexture`
4. `KHR_materials_transmission`
   - `transmissionFactor`
   - `transmissionTexture`
5. `KHR_materials_ior`
   - `ior`
6. `KHR_materials_volume`
   - `thicknessFactor`
   - `thicknessTexture`
   - `attenuationDistance`
   - `attenuationColor`
7. `KHR_materials_emissive_strength`
   - `emissiveStrength`

Texture channel rules to encode in comments and tests:

1. Clearcoat intensity uses clearcoat texture red.
2. Clearcoat roughness uses clearcoat roughness texture green.
3. Clearcoat normal uses a normal texture and its own normal scale.
4. Sheen color uses RGB and should be loaded as sRGB.
5. Sheen roughness uses alpha.
6. Anisotropy texture uses red/green for direction and blue for strength.
7. Transmission uses red.
8. Volume thickness uses green.

Extension conflict handling:

1. If `KHR_materials_pbrSpecularGlossiness` is present, report unsupported and fall back to core PBR only if core PBR fields are present.
2. If `KHR_materials_unlit` is present, report unsupported and use default lit PBR fallback unless Phase 16 later chooses to support unlit.
3. If `KHR_materials_volume` is present without `KHR_materials_transmission`, report that volume has no effect.
4. If a material uses transmission and alpha blend, keep the material in the transparent path and report both behaviors in diagnostics.
5. If texture `texCoord` differs from the currently supported UV set, report unsupported UV set and use UV0 until multiple UV set support is complete.

Diagnostics requirements:

1. Include material index and material name.
2. Include extension name.
3. Include unsupported field when relevant.
4. Include texture path resolution failures with absolute paths.
5. Include extension counts:
   - clearcoat material count
   - sheen material count
   - anisotropy material count
   - transmission material count
   - subsurface material count
   - emissive-strength material count
   - unsupported extension count

Files likely touched:

1. `Njulf.Assets/ModelImporter.cs`
2. `Njulf.Assets/IModelRenderUploadService.cs`
3. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
4. `Njulf.Rendering/Data/RendererDiagnostics.cs`
5. `Njulf.Rendering/Data/SceneRenderingData.cs`

Acceptance criteria:

1. Small synthetic glTF files can prove each extension field is parsed.
2. Unsupported extensions do not silently disappear.
3. Texture paths resolve through the same external-image validation path as existing PBR textures.

## Slice 5: Resolve Extension Textures And Upload Extension Data

Update `ModelRenderUploadService`.

Tasks:

1. Extend `MaterialTextureIndices` or create `MaterialExtensionTextureIndices`.
2. Resolve extension textures with correct color space:
   - clearcoat factor and roughness: linear
   - clearcoat normal: linear normal
   - sheen color: sRGB
   - sheen roughness: linear
   - anisotropy: linear
   - transmission: linear
   - thickness: linear
   - subsurface color: sRGB if color texture is added
3. Track texture handles for lifetime, just like existing base material textures.
4. Build `GPUMaterialExtensionData` only when `FeatureFlags != None`.
5. Keep default extension texture indices valid even when a feature is disabled.
6. Clamp uploaded factors:
   - clearcoat factor: `[0, 1]`
   - clearcoat roughness: `[0, 1]`, apply final min roughness in shader if needed
   - clearcoat normal scale: `[0, +inf)` with a sane practical cap such as `4`
   - sheen color: `[0, +inf)` but warn above a practical brightness threshold
   - sheen roughness: `[0, 1]`
   - anisotropy strength: `[0, 1]`
   - transmission factor: `[0, 1]`
   - IOR: clamp to a practical range such as `[1, 3]`
   - thickness: `[0, +inf)`
   - subsurface strength: `[0, 1]`
   - emissive strength: `[0, +inf)`
7. Set `FeatureFlags` based on effective values after clamping and texture presence.
8. If `TransmissionFactor > 0`, classify as requiring transparent rendering or a dedicated transmission mode.

Emissive intensity decision:

1. Store emissive strength in `GPUMaterialExtensionData` only if the material already has an extension payload, or store it in `Emissive.w`.
2. Prefer `Emissive.w` for emissive strength because every material already reads `Emissive`.
3. Define the contract:
   - `Emissive.rgb` is emissive color factor.
   - `Emissive.a` is emissive strength.
   - default `Emissive.a = 1`.
4. Update `MaterialManager.CreateDefaultMaterial()` to use `Emissive = new Vector4(0, 0, 0, 1)`.
5. Update all sample materials that set `Emissive = Vector4.Zero` if they should have neutral strength. Since RGB is zero, visual output remains zero either way, but tests should reflect the contract.

Files likely touched:

1. `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
2. `Njulf.Rendering/Resources/MaterialManager.cs`
3. `Njulf.Rendering/Data/GPUStructs.cs`
4. `Njulf.Tests/ModelRenderUploadServiceTests.cs`
5. `Njulf.Tests/MaterialManagerTests.cs`
6. `NjulfHelloGame/SampleReflectionTestSpheres.cs`

Acceptance criteria:

1. Extension textures are loaded with correct sRGB/linear policy.
2. Extension texture handles are released with material lifetime.
3. Material deduplication considers extension payloads.
4. Emissive strength multiplies emissive color and texture.

## Slice 6: Add Shader Extension Evaluation Infrastructure

Refactor `forward.frag` before implementing features.

Tasks:

1. Introduce a material shading input struct:

```glsl
struct MaterialShadingInput
{
    vec3 albedo;
    float metallic;
    float roughness;
    float ambientOcclusion;
    vec3 emissive;
    vec3 normal;
    vec3 clearcoatNormal;
    vec3 tangent;
    vec3 bitangent;
    uint featureFlags;
};
```

2. Introduce helper functions:
   - `bool HasMaterialFeature(uint flags, uint feature)`
   - `GPUMaterialExtensionData ReadMaterialExtensionSafe(GPUMaterialData material)`
   - `vec3 ResolveTangent(...)`
   - `vec3 ResolveBitangent(...)`
   - `vec3 ResolveNormalFromTexture(...)`
   - `float SampleLinearChannel(int textureIndex, vec2 uv, int channel, float fallback)`
3. Keep base PBR functions intact first:
   - `DistributionGGX`
   - `GeometrySmith`
   - `FresnelSchlick`
   - `EvaluatePbrLight`
   - `EvaluateIbl`
4. Add feature evaluation as additive/layered functions instead of inline blocks in `main`.
5. Guard all extension reads with `FeatureFlags != 0`.
6. Add debug output modes for:
   - material feature flags
   - clearcoat factor
   - sheen intensity
   - anisotropy direction/strength
   - transmission factor
   - subsurface strength
   - emissive intensity

Files likely touched:

1. `Njulf.Shaders/forward.frag`
2. `Njulf.Shaders/common.glsl`
3. `Njulf.Rendering/Data/RenderSettings.cs`
4. `NjulfHelloGame/SampleInputController.cs`
5. `NjulfHelloGame/SampleDiagnosticsReporter.cs`

Acceptance criteria:

1. Shader compiles after refactor with no visual feature changes.
2. Default PBR materials still follow the current code path.
3. Debug views can isolate material feature data.

## Slice 7: Implement Clearcoat

Clearcoat is the first real feature because it is high impact and relatively contained.

Import behavior:

1. `clearcoatFactor` default: `0`
2. `clearcoatRoughnessFactor` default: `0`
3. `clearcoatTexture.r` multiplies factor.
4. `clearcoatRoughnessTexture.g` multiplies roughness.
5. `clearcoatNormalTexture` uses its own normal scale.

Shader behavior:

1. Compute clearcoat only when effective clearcoat factor is greater than zero.
2. Use the clearcoat normal for the clearcoat lobe.
3. Use a dielectric F0 around `0.04` for IOR 1.5 unless `KHR_materials_ior` is later applied.
4. Add a second GGX specular lobe.
5. Reduce base diffuse/specular energy by the coat Fresnel term:
   - base contribution multiplied by approximately `1 - clearcoatFactor * clearcoatFresnel`
6. Apply clearcoat to direct lights.
7. Apply clearcoat to IBL:
   - sample prefiltered environment using clearcoat normal and clearcoat roughness
   - use BRDF LUT with clearcoat roughness
8. Do not let clearcoat affect emissive.
9. Add debug view for clearcoat factor and clearcoat normal.

Acceptance criteria:

1. A rough base material with clearcoat shows a sharp top highlight.
2. Clearcoat roughness broadens only the top lobe.
3. Clearcoat normal can differ from base normal.
4. Turning clearcoat off returns to baseline PBR.
5. Default materials do not sample clearcoat textures.

Tests:

1. Importer parses clearcoat factors and textures.
2. Upload maps clearcoat values and texture indices.
3. Material dedupe separates clearcoat and non-clearcoat materials.
4. Shader build tests pass.

## Slice 8: Implement Emissive Strength And Normal Strength Contract

Normal strength mostly exists, but Phase 16 should make it explicit and testable.

Tasks:

1. Define `NormalScaleBias.x` as base normal strength in docs and tests.
2. Clamp normal scale during upload.
3. Add diagnostics for abnormal normal strengths:
   - negative values clamp to zero
   - very large values warn or clamp
4. Implement `KHR_materials_emissive_strength`.
5. Use `Emissive.a` as emissive strength.
6. Update `forward.frag`:

```glsl
vec3 emissive = max(material.Emissive.rgb * emissiveSample.rgb * material.Emissive.a, vec3(0.0));
```

7. Add a debug view for emissive intensity before tone mapping.
8. Update tests to expect default emissive strength of `1`.

Acceptance criteria:

1. Existing non-emissive materials remain black emissive.
2. Emissive strength greater than one feeds HDR and bloom.
3. Normal strength zero flattens normal-map perturbation.
4. Normal strength one matches current behavior.

## Slice 9: Implement Sheen

Sheen should support cloth and velvet without becoming a full fabric renderer.

Import behavior:

1. `sheenColorFactor` default: `[0, 0, 0]`
2. `sheenColorTexture.rgb` multiplies the color factor.
3. `sheenRoughnessFactor` default: `0`
4. `sheenRoughnessTexture.a` multiplies roughness.
5. Load sheen color texture as sRGB.
6. Load sheen roughness as linear.

Shader behavior:

1. Compute sheen only when color luminance or sheen texture contribution is greater than zero.
2. Start with a practical glTF-compatible sheen lobe:
   - use a Charlie distribution or a normalized velvet approximation
   - use roughness to control lobe width
   - keep it energy-aware by reducing base diffuse/specular where sheen is strong
3. Apply sheen to direct lighting.
4. Add a simple IBL sheen approximation:
   - sample irradiance or prefiltered environment with a rough lobe
   - keep it controlled by roughness and view angle
5. Layer order:
   - base PBR
   - sheen
   - clearcoat on top if clearcoat is enabled
6. Add debug view for sheen color and roughness.

Acceptance criteria:

1. Velvet-like material has visible rim/soft sheen under grazing view.
2. Sheen can coexist with clearcoat, with clearcoat visibly above it.
3. Sheen disabled path matches baseline.
4. Sheen color texture color space is correct.

Tests:

1. Importer reads sheen factors and textures.
2. Upload maps sheen color, roughness, and texture indices.
3. Shader compiles.

## Slice 10: Implement Anisotropy

Anisotropy depends on the existing tangent basis and should be conservative.

Import behavior:

1. `anisotropyStrength` default: `0`
2. `anisotropyRotation` default: `0`
3. `anisotropyTexture.rg` encodes direction in tangent space.
4. `anisotropyTexture.b` multiplies strength.
5. Load anisotropy texture as linear.

Shader behavior:

1. Use existing `fragWorldTangent` and reconstructed bitangent.
2. Rotate tangent/bitangent by `anisotropyRotation`.
3. If anisotropy texture is present:
   - decode `rg` from `[0, 1]` to `[-1, 1]`
   - derive direction angle or tangent vector
   - multiply strength by blue channel
4. Replace isotropic GGX specular with anisotropic GGX only when effective strength is greater than zero.
5. Keep diffuse unchanged.
6. Use roughness to derive anisotropic alpha values:
   - one axis sharper
   - one axis broader
   - clamp to avoid sparkle and divide-by-zero
7. Add IBL approximation:
   - first slice may use anisotropic normal/tangent adjustment with prefiltered env
   - later improvement can use split-sum anisotropic lookup if needed
8. Add debug view for anisotropy direction and strength.

Acceptance criteria:

1. Brushed metal sphere has elongated highlights aligned to tangent direction.
2. Rotating anisotropy changes highlight orientation.
3. Strength zero matches isotropic baseline.
4. Missing tangents fall back to isotropic lighting and report a diagnostic if the material requested anisotropy.

Tests:

1. Importer reads anisotropy extension.
2. Upload maps strength, rotation, and texture.
3. `BuildGpuVertices_DerivesTangentHandednessFromBitangent` remains valid.
4. Add a test that anisotropy feature is disabled when no tangent data is available only if the implementation chooses to validate tangent availability at import time.

## Slice 11: Implement Simple Glass And Transmission

Transmission must be treated as optical transparency, not alpha coverage.

Import behavior:

1. `transmissionFactor` default: `0`
2. `transmissionTexture.r` multiplies factor.
3. `ior` default: `1.5`
4. `volume.thicknessFactor` and `thicknessTexture.g` may be parsed.
5. `attenuationDistance` and `attenuationColor` may be parsed.

Render-list behavior:

1. A material with effective transmission greater than zero must render in the transparent pass or a dedicated transmission pass.
2. It should still write no opaque depth in the default implementation.
3. It should receive shadows and direct lighting like other transparent materials where possible.
4. It should remain deterministic under current transparent sorting.

Shader behavior, first production slice:

1. Preserve surface reflection from PBR even when transmission is high.
2. Reduce diffuse contribution based on transmission.
3. Blend transmitted scene color or environment color through the material:
   - if opaque scene color copy exists, sample it with roughness-based blur/refraction offset
   - otherwise use environment color as a fallback
4. Use IOR for Fresnel.
5. Use roughness to blur or soften transmission.
6. Apply simple attenuation:
   - if thickness and attenuation distance are valid, tint transmitted color
   - otherwise use base color tint lightly
7. Do not implement order-independent transparency here.
8. Do not sample the current HDR target while writing to it.

Required render-target decision:

1. If the current frame graph already has a resolved opaque scene color available before transparent forward, use it.
2. If not, add a copy of HDR scene color after opaque forward and before transparent forward:
   - bind it as `OPAQUE_SCENE_COLOR_TEXTURE_INDEX`
   - transition it to shader read
   - document the cost in diagnostics
3. If adding the copy is too much for the first slice, implement environment-only simple glass and leave screen refraction behind a follow-up task.

Acceptance criteria:

1. A glass sphere remains visibly reflective at full transmission.
2. Transmission is not the same as alpha fade.
3. Glass sorting is deterministic.
4. No feedback loop validation error occurs.
5. Disabling transmission returns to standard transparent or opaque behavior.

Tests:

1. Importer reads transmission, IOR, and volume fields.
2. Upload maps transmission and classifies transparent pass requirement.
3. Render graph validation remains clean.
4. Shader build tests pass.

## Slice 12: Implement Subsurface Approximation

This is a pragmatic real-time approximation, not full subsurface scattering.

Data model:

1. Use a Njulf-specific material field for subsurface unless a future glTF extension is selected.
2. Add `SubsurfaceColor`.
3. Add `SubsurfaceStrength`.
4. Optionally add `SubsurfaceTexturePath` for authored masks.

Shader behavior:

1. Apply only to dielectric materials by default.
2. Use a wrap diffuse or Burley-inspired soft diffuse term:
   - broaden direct diffuse response
   - bias contribution toward grazing and back-lit angles
3. Keep it energy-limited:
   - reduce normal Lambert diffuse as subsurface contribution increases
4. Modulate by AO so creases do not glow.
5. Add optional thin-transmission term for leaves:
   - use negative light direction and view direction
   - keep it controlled by strength
6. Do not alter specular.
7. Add debug view for subsurface strength.

Acceptance criteria:

1. Wax/skin/leaves can look softer without becoming emissive.
2. Strength zero matches baseline.
3. AO still affects indirect soft shading.
4. Metallic materials ignore or strongly suppress subsurface.

Tests:

1. Upload maps subsurface data and flags.
2. Debug flag path compiles.

## Slice 13: Add Material Debug Views And Diagnostics

Material extensions need visibility during content creation.

Debug views:

1. Material feature flags as color bands.
2. Base normal strength.
3. Emissive intensity.
4. Clearcoat factor.
5. Clearcoat roughness.
6. Sheen color and roughness.
7. Anisotropy strength and direction.
8. Transmission factor.
9. Subsurface strength.

Runtime diagnostics:

1. `ClearcoatMaterialCount`
2. `SheenMaterialCount`
3. `AnisotropyMaterialCount`
4. `TransmissionMaterialCount`
5. `SubsurfaceMaterialCount`
6. `EmissiveStrengthMaterialCount`
7. `MaterialExtensionDataCount`
8. `MaterialExtensionUploadBytes`
9. `UnsupportedMaterialExtensionCount`
10. `OpaqueSceneColorCopyEnabled`
11. `OpaqueSceneColorCopyBytes`

Import diagnostics:

1. Supported extension names used by the asset.
2. Unsupported extension names used by the asset.
3. Material names for unsupported extensions.
4. Texture substitution counts by extension texture role.

Files likely touched:

1. `Njulf.Rendering/Data/RendererDiagnostics.cs`
2. `Njulf.Rendering/Data/SceneRenderingData.cs`
3. `Njulf.Rendering/Data/SceneDataBuilder.cs`
4. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
5. `NjulfHelloGame/SampleInputController.cs`
6. `Njulf.Assets/IModelRenderUploadService.cs`

Acceptance criteria:

1. Diagnostics expose material feature counts.
2. Debug views isolate each feature without editing shaders.
3. Importer warnings are specific enough for an artist or programmer to fix the asset.

## Slice 14: Update SampleReflectionTestSpheres

Use `NjulfHelloGame/SampleReflectionTestSpheres.cs` as the focused visual validation scene for Phase 16.

Recommended layout:

1. Keep the existing reflection row:
   - chrome
   - smooth gold
   - brushed baseline metal
   - glossy dielectric
2. Add a material-extension row:
   - clearcoat red paint
   - velvet/sheen cloth
   - anisotropic brushed metal
   - simple glass
   - wax/subsurface
   - high-emissive ceramic or strip
3. Add names that map directly to diagnostics:
   - `MaterialQuality.ClearcoatPaint`
   - `MaterialQuality.SheenVelvet`
   - `MaterialQuality.AnisotropicMetal`
   - `MaterialQuality.SimpleGlass`
   - `MaterialQuality.SubsurfaceWax`
   - `MaterialQuality.EmissiveHighIntensity`
4. Use procedural materials first so the sample does not depend on new texture assets.
5. Add texture-backed variants later with small authored test textures if the repo accepts sample texture assets.
6. Keep sphere mesh generation unchanged unless tangent orientation needs a visible anisotropy pattern.
7. Add a simple ground plane and light setup only if existing scene lighting does not make the feature differences readable.

Material examples:

1. Clearcoat paint:
   - base red
   - metallic `0`
   - roughness `0.45`
   - clearcoat `1`
   - clearcoat roughness `0.04`
2. Sheen velvet:
   - base deep blue or burgundy
   - roughness `0.8`
   - sheen color near pale blue/pink
   - sheen roughness `0.4`
3. Anisotropic metal:
   - metallic `1`
   - roughness `0.28`
   - anisotropy strength `0.85`
   - anisotropy rotation `0` and maybe a second rotated sample if space allows
4. Simple glass:
   - base faint blue/green
   - roughness `0.02`
   - transmission `0.85`
   - IOR `1.45`
5. Subsurface wax:
   - base warm off-white or peach
   - roughness `0.55`
   - subsurface color warm orange
   - subsurface strength `0.5`
6. Emissive:
   - albedo dark
   - emissive color saturated
   - emissive strength `6` or higher so HDR/bloom reacts

Acceptance criteria:

1. Running `NjulfHelloGame` shows all Phase 16 features in one stable view.
2. Material debug views make each sphere's feature obvious.
3. Existing reflection test spheres remain useful for Phase 10 regression checks.

## Slice 15: Shader Variant And Pipeline Policy

Keep variants limited.

Policy:

1. Use one opaque forward shader for default and extension materials.
2. Use one transparent forward shader for alpha blend and transmission.
3. Do not create per-feature pipelines.
4. Use material sorting to reduce branch divergence.
5. Use feature flags to avoid extension buffer reads and extra texture samples for default materials.
6. Use compile-time constants only for renderer-wide quality levels, not material feature combinations.
7. If a feature becomes too expensive, add quality toggles:
   - clearcoat direct only vs direct plus IBL
   - sheen direct only vs direct plus IBL
   - anisotropic direct only vs anisotropic IBL approximation
   - transmission environment fallback vs scene-color refraction

Acceptance criteria:

1. Pipeline count does not grow with material feature combinations.
2. Default materials avoid extension texture samples.
3. Feature material cost is measurable in diagnostics and RenderDoc.

## Slice 16: Validation And Tests

CPU tests:

1. `GPUStructLayoutTests`
   - new material sizes
   - extension struct size
   - new offsets
2. `MaterialManagerTests`
   - default material has no extension payload
   - extension material registers and deduplicates correctly
   - extension texture index validation rejects invalid indices
   - extension upload snapshot preserves data
3. `ModelRenderUploadServiceTests`
   - maps each extension factor
   - clamps factors correctly
   - resolves correct texture color space policies
   - classifies transmission as transparent
   - emissive strength defaults to `1`
4. `ContentRendererIntegrationTests`
   - synthetic glTF with clearcoat parses correctly
   - synthetic glTF with sheen parses correctly
   - synthetic glTF with anisotropy parses correctly
   - synthetic glTF with transmission, IOR, and volume fields parses correctly
   - unsupported extension is reported
5. `BindlessIndexTests`
   - new storage buffer index matches GLSL

Shader tests:

1. `ShaderBuildTests` compiles:
   - `forward.frag`
   - `common.glsl` include path
   - transparent forward path if compiled separately
2. Add shader smoke tests for:
   - feature flag constants
   - extension struct read helper

Manual GPU validation:

1. Run `NjulfHelloGame`.
2. Toggle material debug views.
3. Resize window.
4. Capture RenderDoc frame.
5. Verify:
   - material buffer upload
   - extension buffer upload
   - no descriptor out-of-bounds
   - no feedback loop for transmission scene color
   - default materials skip extension reads where visible in shader debugging

Performance validation:

1. Compare baseline and Phase 16 sample:
   - default Sponza or current glTF sample
   - material-quality spheres
2. Record:
   - forward pass GPU time
   - transparent pass GPU time
   - material upload bytes
   - extension upload bytes
   - texture count
   - material count
3. Add a stress scene with many default materials and no extensions. Default-path regression should be negligible.
4. Add a stress scene with many extension materials. Cost should be documented and bounded.

Acceptance criteria:

1. `dotnet build Njulf.sln` passes.
2. `dotnet test Njulf.sln` passes.
3. Shader build tests pass.
4. Validation is clean on startup, resize, rendering, and shutdown.
5. Unsupported material features are visible in diagnostics.

## Slice 17: Documentation

Update developer-facing docs after implementation.

Tasks:

1. Document material feature support in `Plans/IndieGameRendererProductionPlan.md` or a renderer README if one exists.
2. Add a material support table:
   - feature
   - glTF extension
   - importer support
   - shader support
   - texture channel support
   - limitations
3. Document the GPU material layout contract.
4. Document how to add future material extensions without creating pipeline explosion.
5. Document known limitations:
   - no full volumetric scattering
   - no order-independent glass
   - no ray-traced refraction
   - no multi-scattering skin diffusion

Acceptance criteria:

1. A new contributor can see exactly what material features are supported.
2. Unsupported features have explicit status rather than ambiguous behavior.

## Detailed File Impact

Likely primary files:

1. `Njulf.Assets/ModelImporter.cs`
   - add extension parsing
   - add extension diagnostics
   - add helper readers for nested extension texture paths and factors
2. `Njulf.Assets/IModelRenderUploadService.cs`
   - extend upload diagnostics
3. `Njulf.Rendering/Data/GPUStructs.cs`
   - add flags and extension struct
4. `Njulf.Rendering/Data/MaterialFeatureFlags.cs`
   - new feature enum
5. `Njulf.Rendering/Resources/MaterialManager.cs`
   - store, dedupe, validate, and upload extension payloads
6. `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
   - map imported materials to GPU base and extension data
7. `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
   - new storage buffer index if extension data is separate
8. `Njulf.Shaders/common.glsl`
   - mirror data contract
9. `Njulf.Shaders/forward.frag`
   - evaluate extensions
10. `Njulf.Rendering/Data/SceneDataBuilder.cs`
   - classify transmission materials
   - count feature materials
11. `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`
   - ensure transmission has correct inputs
12. `Njulf.Rendering/Resources/RenderTargetManager.cs`
   - opaque scene color copy if needed
13. `NjulfHelloGame/SampleReflectionTestSpheres.cs`
   - add material-quality sample row
14. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
   - print extension diagnostics
15. `NjulfHelloGame/SampleInputController.cs`
   - debug view controls

Likely tests:

1. `Njulf.Tests/GPUStructLayoutTests.cs`
2. `Njulf.Tests/BindlessIndexTests.cs`
3. `Njulf.Tests/MaterialManagerTests.cs`
4. `Njulf.Tests/ModelRenderUploadServiceTests.cs`
5. `Njulf.Tests/ContentRendererIntegrationTests.cs`
6. `Njulf.Tests/ShaderBuildTests.cs`

## Risk Register

Risk: `GPUMaterialData` changes break shader reads.

Mitigation:

1. Update C# and GLSL in the same commit.
2. Add explicit offset constants for new fields.
3. Run `GPUStructLayoutTests` before shader work.

Risk: material extensions slow every material.

Mitigation:

1. Keep extension payload in a separate buffer.
2. Branch on `FeatureFlags == 0` before extension reads.
3. Keep extension texture samples behind feature-specific checks.
4. Measure a default-material stress scene.

Risk: transmission creates a render-target feedback loop.

Mitigation:

1. Only sample a copied opaque scene color or environment fallback.
2. Add RenderDoc validation step.
3. Keep the fallback path available.

Risk: shader grows too large and hard to reason about.

Mitigation:

1. Group feature functions by extension.
2. Keep base PBR functions unchanged.
3. Add debug views with clear constants.
4. Consider moving material extension helpers into a new included GLSL file only after the first implementation is stable.

Risk: glTF extension texture color spaces are wrong.

Mitigation:

1. Encode color-space policy in `ModelRenderUploadService` tests.
2. Treat data maps as linear.
3. Treat color maps as sRGB only where specified.

Risk: extension diagnostics become noisy.

Mitigation:

1. Count repeated warnings by extension.
2. Print detailed material names once per asset load.
3. Keep concise runtime diagnostics for frame reporting.

Risk: simple glass looks like ordinary alpha.

Mitigation:

1. Preserve reflection at high transmission.
2. Use IOR Fresnel.
3. Use environment or opaque-scene color for transmitted light.
4. Add side-by-side sample spheres comparing alpha blend and transmission.

## Definition Of Done

Phase 16 is complete when:

1. Existing PBR assets still render correctly.
2. Default material path remains cheap and does not fetch extension textures.
3. Clearcoat, sheen, anisotropy, transmission, subsurface approximation, normal strength, and emissive intensity can be authored.
4. Supported glTF material extensions are parsed with correct factors, texture channels, color spaces, and defaults.
5. Unsupported material extensions are reported during import with material names.
6. Material feature counts and extension upload sizes appear in diagnostics.
7. `SampleReflectionTestSpheres` visibly demonstrates all implemented material quality features.
8. Shader variants remain bounded to existing opaque/transparent paths plus renderer-wide quality toggles.
9. `dotnet build Njulf.sln` passes.
10. `dotnet test Njulf.sln` passes.
11. Shader build tests pass.
12. `NjulfHelloGame` runs validation-clean through startup, resize, rendering, and shutdown.
13. RenderDoc shows no descriptor errors, no target feedback loop, and a clear default-vs-extension material path.

## Recommended First Implementation PR

Keep the first PR small enough to review:

1. Add `MaterialFeatureFlags`.
2. Add `GPUMaterialExtensionData`.
3. Add extension buffer plumbing in `MaterialManager`.
4. Add struct layout and material manager tests.
5. Add diagnostics fields.
6. Do not add shading changes yet except for harmless feature debug views.

Recommended second PR:

1. Parse `KHR_materials_clearcoat`.
2. Upload clearcoat data.
3. Implement clearcoat direct and IBL shading.
4. Add one clearcoat sample sphere.

Recommended third PR:

1. Add emissive strength.
2. Add sheen.
3. Add anisotropy.
4. Extend sample spheres and tests.

Recommended fourth PR:

1. Add simple transmission/glass.
2. Add subsurface approximation.
3. Add final diagnostics, docs, and performance notes.
