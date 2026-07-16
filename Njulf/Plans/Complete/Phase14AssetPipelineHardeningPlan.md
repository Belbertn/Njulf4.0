# Phase 14: Asset Pipeline Hardening Detailed Implementation Plan

Goal: make real production assets load consistently from common DCC exports without manual unpacking, hidden color-space mistakes, or renderer-specific file edits. Phase 14 should harden glTF/glb ingestion, embedded resources, texture samplers, texture transforms, vertex streams, compressed textures, HDR environment images, import diagnostics, fallback behavior, and automated content validation.

## Recommendation

Implement Phase 14 as an asset contract and diagnostics phase, not as a visual-effect phase.

Recommended order:
1. replace the current ad hoc glTF validation with a real glTF asset reader that understands `.gltf`, `.glb`, external buffers, embedded buffers, data URIs, buffer views, images, textures, samplers, materials, meshes, and extensions.
2. preserve the existing Assimp static-mesh path only where it remains useful, but make glTF JSON/binary metadata the source of truth for glTF-specific behavior.
3. add explicit import diagnostics before widening renderer features.
4. add embedded images and `.glb` support before compressed textures.
5. add sampler and texture transform support before adding more material extensions.
6. add vertex color and multiple UV streams with tests and shader contracts.
7. add KTX2/Basis or BCn compressed texture support through a clean texture-loader abstraction.
8. add HDR environment-image loading and asset validation fixtures.

Do not start by adding many material extensions. Phase 16 owns clearcoat, sheen, anisotropy, transmission, subsurface, and related shader quality work. Phase 14 should make the core asset transport reliable enough that those material extensions can be added safely later.

## Current Baseline

Relevant current behavior:
- `Njulf.Assets/ModelImporter.cs` imports models through Assimp and then reads a partial glTF JSON manifest for material metadata.
- `ModelImporter.LoadAndValidateGltfManifest` only runs for `.gltf`; `.glb` currently bypasses the glTF manifest path.
- glTF buffers must be external `.bin` files. Missing URI, embedded data URI buffers, and `.glb` binary chunks are rejected.
- glTF images must be external image files. `bufferView` images and data URI images are rejected.
- glTF `textures[].sampler`, `samplers`, `texCoord`, and texture transform metadata are not preserved.
- `ModelMaterial` stores texture paths only, not texture slots, sampler state, texture coordinate set, or texture transform per slot.
- `ModelSubMesh` stores one UV stream as `TexCoords`. `GPUVertex` has `TexCoord` and `TexCoord2`, but upload currently populates `TexCoord2` with zero.
- Vertex colors are not imported or uploaded.
- `GPUMaterialData.TexCoordOffsetScale` exists and defaults to `(0, 0, 1, 1)`, but it is not populated from `KHR_texture_transform`.
- `TextureManager.LoadTextureFromFile` decodes standard images to RGBA with `StbImageSharp` and uploads `R8G8B8A8Srgb` or `R8G8B8A8Unorm`.
- Texture cache keys include path, mip policy, sRGB flag, and max dimension, but not sampler state or embedded-resource identity.
- `BindlessHeap` owns a default sampler and screen sampler. Imported texture sampler state is not represented per texture binding.
- `ModelRenderUploadService` chooses sRGB for albedo and emissive, linear for normal and metallic-roughness, and only samples AO from the metallic-roughness texture when paths match.
- Diagnostics already report loaded texture count, mipmap fallbacks, downscaled textures, estimated texture bytes, loaded material count, loaded texture count, and default substitutions.
- `SampleReflectionTestSpheres.cs` creates procedural PBR validation materials and is useful as a stable reference backdrop for asset-pipeline sample scenes.

Important consequence: Phase 14 must distinguish asset bytes, image decoding, GPU texture creation, sampler state, material texture usage, and shader sampling contracts. Treating every texture as a file path plus one global sampler will not survive real `.glb` content.

## Target Outcome

Phase 14 is complete when:
- common Blender, Maya, Substance, and DCC-exported `.gltf` and `.glb` assets load without manual unpacking.
- external, embedded, data URI, and `.glb` binary buffers are supported.
- external, embedded buffer-view, data URI, PNG, JPEG, KTX2, and supported HDR images have clear load paths.
- texture sampler wrap and filter modes are imported and applied.
- texture coordinate set selection is honored for material texture slots.
- `KHR_texture_transform` offset, scale, rotation, and texCoord override are applied correctly.
- vertex color and at least two UV sets are imported, uploaded, and available in shaders.
- compressed textures reduce GPU memory for supported formats.
- color-space rules are explicit, tested, and visible in diagnostics.
- unsupported required glTF extensions fail with actionable messages.
- unsupported optional glTF extensions are reported clearly without corrupting the asset.
- import diagnostics name the asset path, material, texture, image, buffer, mesh, primitive, and extension responsible for warnings or failures.

## Non-Goals

Phase 14 does not include:
- new visual material lobes such as clearcoat, sheen, anisotropy, transmission, or subsurface. Those belong to Phase 16.
- animation import beyond the Phase 12 contract.
- LOD, instancing, impostors, foliage batching, or world streaming. Those belong to Phase 15.
- editor UI for asset inspection.
- runtime hot reload as a requirement.
- complete support for every glTF extension.
- arbitrary 3D DCC format hardening beyond what Assimp already provides.
- lossless round-tripping of glTF files.

## Core Design Decisions

1. Treat glTF/glb as a structured asset format, not as a loose collection of paths.
2. Use glTF JSON and binary chunks as the source of truth for glTF-specific behavior. Assimp can remain a geometry bootstrapper only where it does not discard important data.
3. Preserve asset provenance in diagnostics. Every imported buffer, image, texture, sampler, material, mesh, and primitive should retain source indices and names.
4. Keep color-space decisions tied to material usage, not image file extension.
5. Do not silently ignore required glTF extensions. Fail before rendering corrupted content.
6. Do not silently ignore optional extensions that affect appearance. Warn with extension name and affected material or texture.
7. Prefer explicit fallback assets over invisible fallbacks. Missing normal should use default normal; missing albedo should use default white; unsupported compressed format should produce a clear fallback or failure according to settings.
8. Keep GPU struct changes versioned and tested. Vertex layout changes must update `GPUStructs.cs`, `common.glsl`, shader build tests, and layout tests together.
9. Make compressed texture support capability-driven. Probe Vulkan format support and choose fallback decode/transcode paths deterministically.
10. Add small fixture assets for tests. Do not rely only on large sample scenes.

## Architecture

Introduce a dedicated glTF asset layer in `Njulf.Assets`:
- `Njulf.Assets/Gltf/GltfAsset.cs`
- `Njulf.Assets/Gltf/GltfAssetReader.cs`
- `Njulf.Assets/Gltf/GltfBufferStore.cs`
- `Njulf.Assets/Gltf/GltfImageStore.cs`
- `Njulf.Assets/Gltf/GltfMaterialReader.cs`
- `Njulf.Assets/Gltf/GltfMeshReader.cs`
- `Njulf.Assets/Gltf/GltfExtensionDiagnostics.cs`
- `Njulf.Assets/Diagnostics/AssetImportDiagnostics.cs`
- `Njulf.Assets/Diagnostics/AssetImportMessage.cs`

Keep `ModelImporter` as the public importer facade initially:
- route `.gltf` and `.glb` to the new glTF reader.
- route non-glTF formats through the existing Assimp path.
- keep current tests passing while replacing internals incrementally.

Suggested ownership split:
- `Njulf.Assets`: parse files, resolve glTF buffers/images/samplers/materials/meshes, emit CPU-side `ModelMesh`.
- `Njulf.Core.Scene`: runtime model/render object concepts.
- `Njulf.Rendering.Resources.TextureManager`: GPU texture allocation, decoding integration, sampler creation, compressed upload, HDR upload.
- `Njulf.Rendering.Resources.ModelRenderUploadService`: CPU model-to-GPU material/mesh upload, texture usage color space, material fallback accounting.
- `Njulf.Shaders`: shader-side vertex/material contract.

Acceptance criteria:
- `ModelImporter.Import` remains the stable public entry point.
- glTF-specific parsing is testable without Vulkan.
- renderer upload remains separate from asset parsing.

## glTF And glb Reader

Add `.glb` parsing:
- validate 12-byte GLB header.
- validate magic `glTF`, version `2`, and total length.
- read JSON chunk.
- read optional BIN chunk.
- reject malformed chunk lengths with asset path and byte offset.
- support only glTF 2.0.

Support `.gltf`:
- parse JSON with comments/trailing commas behavior if desired, but production fixtures should be strict.
- resolve external file URIs relative to the asset file directory.
- support data URI buffers and images.

Buffer model:
- `GltfBuffer`: source kind, byte length, URI, binary chunk slice, decoded bytes.
- `GltfBufferView`: buffer index, byte offset, byte length, byte stride, target.
- `GltfAccessor`: component type, type, count, normalized, min/max, sparse data if supported or clearly rejected.

First-slice accessor support:
- scalar, vec2, vec3, vec4, mat4 where required.
- component types for positions, normals, tangents, UVs, colors, joints, weights, indices.
- normalized unsigned byte/short for vertex colors and joint data.
- index component types unsigned byte, unsigned short, unsigned int.

Sparse accessor policy:
- support sparse accessors in Phase 14 if practical.
- if not supported in the first slice, reject only when sparse accessors are actually used and report accessor index/path.

Acceptance criteria:
- `.glb` assets with external-free buffers import.
- `.gltf` assets with external buffers still import.
- data URI buffers import.
- malformed GLB files fail with clear diagnostics.
- buffer/accessor bounds are validated before reading.

## Embedded Images

Support image source kinds:
- external URI.
- data URI.
- `bufferView` plus MIME type.
- `.glb` embedded image through BIN chunk buffer view.

Add an image source identity:

```csharp
public readonly record struct ModelImageSource(
    string DebugName,
    string? FilePath,
    int ImageIndex,
    int? BufferViewIndex,
    string? MimeType,
    ReadOnlyMemory<byte> Bytes);
```

Do not pass embedded images around as fake filesystem paths. Introduce a texture request abstraction:

```csharp
public sealed class ModelTextureSource
{
    public string DebugName { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public byte[]? Bytes { get; init; }
    public string? MimeType { get; init; }
    public string CacheIdentity { get; init; } = string.Empty;
}
```

Acceptance criteria:
- embedded `.glb` PNG/JPEG images load.
- data URI PNG/JPEG images load.
- cache identity distinguishes two embedded images with the same name.
- diagnostics count external, embedded, data URI, and buffer-view images separately.

## Texture Samplers

Add sampler import from glTF `samplers`:
- `wrapS`
- `wrapT`
- `minFilter`
- `magFilter`
- inferred mip filter.

Map glTF values:
- `CLAMP_TO_EDGE` -> `SamplerAddressMode.ClampToEdge`.
- `MIRRORED_REPEAT` -> `SamplerAddressMode.MirroredRepeat`.
- `REPEAT` -> `SamplerAddressMode.Repeat`.
- `NEAREST`/`LINEAR` for mag/min filters.
- mipmap min filters to nearest/linear mipmap mode.

Add renderer sampler cache:
- `SamplerManager` or expand existing sampler handling in `TextureManager`/`BindlessHeap`.
- key by wrap/filter/anisotropy/lod range.
- destroy samplers with renderer lifetime.
- use imported sampler when registering texture descriptors.

Suggested API:

```csharp
public readonly record struct TextureSamplerDescription(
    TextureWrapMode WrapU,
    TextureWrapMode WrapV,
    TextureFilterMode MinFilter,
    TextureFilterMode MagFilter,
    TextureMipFilterMode MipFilter,
    float MaxAnisotropy);
```

Important descriptor implication:
- bindless descriptors currently combine image view and sampler. If the same image is used with two samplers, allocate two bindless texture indices or introduce separate image/sampler descriptors later. For Phase 14, allocate distinct bindless entries per image+sampler+color-space+mip policy.

Acceptance criteria:
- material textures using clamp do not repeat at edges.
- nearest textures can render pixelated without engine code changes.
- cache keys include sampler identity.
- sampler diagnostics report imported, defaulted, and unique GPU sampler counts.

## Texture Transform Extension

Support `KHR_texture_transform` for material texture slots.

Fields:
- `offset`
- `scale`
- `rotation`
- `texCoord`

Current limitation:
- `GPUMaterialData.TexCoordOffsetScale` can represent offset and scale for one shared transform but cannot represent rotation, separate per-texture transforms, or per-slot texCoord choices.

Recommended first production implementation:
1. Add per-material texture slot metadata in CPU model data.
2. Add a compact GPU material extension struct or expand `GPUMaterialData` if acceptable.
3. Support at least base color transform in the first slice only if layout churn must be minimized.
4. Preferred: support transforms per slot for base color, normal, metallic-roughness, occlusion, and emissive.

Suggested GPU addition:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUMaterialTextureTransform
{
    public Vector4 BaseColorOffsetScale;
    public Vector4 NormalOffsetScale;
    public Vector4 MetallicRoughnessOffsetScale;
    public Vector4 EmissiveOffsetScale;
    public Vector4 RotationAndTexCoordSet;
}
```

Alternative:
- expand `GPUMaterialData` to include two transform vectors and packed texCoord/rotation fields. This requires updating `SIZEOF_GPU_MATERIAL_DATA` and all offset tests.

Shader behavior:
- choose UV set by slot.
- apply glTF transform in correct order:

```text
uv' = offset + rotation_matrix * (uv * scale)
```

Validate exact glTF convention with fixtures before relying on it.

Acceptance criteria:
- offset and scale work for all material texture slots.
- rotation works or is explicitly reported as unsupported with affected material/texture.
- `texCoord` override chooses `TEXCOORD_1` when requested.
- tests cover base color texture transform and normal map transform.

## Multiple UV Sets

Current state:
- `GPUVertex` has `TexCoord` and `TexCoord2`.
- upload sets `TexCoord2 = Vector2.Zero`.
- importer reads only `MTextureCoords[0]`.

Tasks:
1. Add `TexCoords0` and `TexCoords1` to `ModelMesh` and `ModelSubMesh`.
2. Keep `TexCoords` as a compatibility alias during migration or remove it in a controlled refactor.
3. Import `TEXCOORD_0` and `TEXCOORD_1`.
4. Populate `GPUVertex.TexCoord` and `GPUVertex.TexCoord2`.
5. Add material texture slot `TexCoordSet`.
6. Update shader sampling to select the correct UV set per slot.
7. Preserve UV flip behavior according to `ImporterOptions` and glTF conventions.

Acceptance criteria:
- a fixture with albedo on `TEXCOORD_0` and emissive/AO on `TEXCOORD_1` renders correctly.
- `ModelRenderUploadService` validation checks both UV streams.
- shader debug view can show UV0/UV1 if an asset debug view is added.

## Vertex Colors

Support glTF `COLOR_0`:
- `VEC3` or `VEC4`.
- float, normalized unsigned byte, normalized unsigned short.
- default to white when missing.

GPU layout options:
- expand `GPUVertex` to include `Color`.
- add a separate optional vertex color buffer.

Recommended for Phase 14:
- expand `GPUVertex` with `Vector4 Color` if the shader and meshlet path can absorb the larger stride.
- update `GPUStructLayoutTests`, `common.glsl`, `ReadVertex`, mesh shaders, and fragment inputs.
- if stride increase is too costly, use a separate color stream and bindless buffer. This is more complex but keeps base vertex stride smaller.

Shader behavior:
- multiply base color factor and base color texture by vertex color for materials that use vertex colors.
- default vertex color should be white, not black.
- add material flag to enable/disable vertex color multiplication if needed.

Acceptance criteria:
- vertex color fixtures render correctly.
- missing vertex colors preserve current appearance.
- layout tests lock the new vertex stride and offsets.
- `SampleReflectionTestSpheres.cs` is updated if `GPUVertex` construction requires a color field.

## Texture Color Space

Make color-space policy explicit by texture role:
- base color: sRGB.
- emissive: sRGB.
- normal: linear.
- metallic-roughness: linear.
- occlusion: linear.
- vertex color: linear values from glTF, multiplied in shader with decoded base color.
- HDR environment: floating point linear.
- data textures and lookup textures: linear unless explicitly documented otherwise.

Tasks:
1. Add `TextureColorSpace` enum.
2. Store requested color space in texture source/request.
3. Include color space in cache key.
4. For compressed textures, choose sRGB or UNORM Vulkan formats according to role.
5. Add diagnostics for color-space decisions.
6. Add tests that albedo/emissive and normal/ORM produce different cache entries for the same image source.

Acceptance criteria:
- same image used as albedo and normal creates separate texture entries or descriptor entries with correct formats.
- diagnostics flag suspicious usage, such as normal maps requested as sRGB.
- fallback textures match role color space.

## Compressed Texture Path

Support KTX2/Basis or BCn through a texture loader abstraction.

Recommended approach:
1. Add `ITextureDecoder` or `TextureAssetDecoder`.
2. Keep current `StbImageSharp` PNG/JPEG decoder as one decoder.
3. Add KTX2 parser/transcoder support behind a separate decoder.
4. Prefer GPU-native compressed upload when Vulkan reports support.
5. Fallback transcode to RGBA when GPU compressed format is unsupported.
6. Track memory saved by compressed textures in diagnostics.

Supported first formats:
- KTX2 with Basis Universal supercompression, if a reliable transcode library is selected.
- BC1/BC3/BC5/BC7 where device supports sampled image format features.
- ETC2/ASTC only if target hardware requires it.

TextureManager changes:
- create images with block-compressed Vulkan formats.
- calculate block-compressed staging sizes.
- upload per-mip block data with correct `BufferImageCopy` dimensions.
- avoid runtime mip generation for precompressed mip chains.
- respect KTX orientation metadata or report unsupported orientation.

Acceptance criteria:
- KTX2 or BCn texture fixture loads.
- compressed texture memory estimate is lower than RGBA equivalent.
- unsupported GPU compressed formats fall back or fail according to settings.
- mip chains are uploaded without linear-blit generation.

## HDR Environment Images

Phase 6 added environment lighting concepts. Phase 14 should harden source image loading for real HDR assets.

Support:
- `.hdr` Radiance equirectangular.
- `.exr` optional later if library choice is acceptable.
- KTX cubemap or equirectangular HDR if compressed/HDR KTX support is added.

TextureManager requirements:
- decode floating-point image data.
- create `R16G16B16A16Sfloat` or `R32G32B32A32Sfloat` source texture.
- preserve linear values.
- support max dimension and diagnostics.
- integrate with `EnvironmentManager` source loading.

Acceptance criteria:
- an HDR equirectangular environment map loads and produces plausible IBL.
- invalid HDR files report path and decoder error.
- HDR texture memory is included in diagnostics.

## Import Diagnostics

Add structured diagnostics rather than only throwing exceptions.

Suggested types:

```csharp
public enum AssetImportSeverity
{
    Info,
    Warning,
    Error
}

public enum AssetImportMessageCode
{
    UnsupportedRequiredExtension,
    UnsupportedOptionalExtension,
    MissingExternalBuffer,
    MissingExternalImage,
    EmbeddedBufferLoaded,
    EmbeddedImageLoaded,
    SamplerDefaulted,
    TextureTransformApplied,
    TextureTransformUnsupported,
    ColorSpaceAssigned,
    TextureFallbackUsed,
    CompressedTextureTranscoded,
    CompressedTextureUnsupported,
    VertexColorImported,
    UvSetImported,
    AccessorBoundsInvalid
}

public sealed record AssetImportMessage(
    AssetImportSeverity Severity,
    AssetImportMessageCode Code,
    string AssetPath,
    string? JsonPointer,
    string Message);
```

Expose diagnostics:
- `ModelMesh.ImportDiagnostics`.
- `ModelRenderUploadDiagnostics`.
- sample reporting in `SampleDiagnosticsReporter.cs`.
- optional log output during `NjulfHelloGame` load.

Acceptance criteria:
- diagnostics list unsupported extensions by name.
- required extension errors stop import.
- optional extension warnings do not stop import unless settings demand strict mode.
- messages include JSON pointer or equivalent source path where practical.

## glTF Extension Policy

Required first-slice support:
- core glTF 2.0.
- `KHR_texture_transform`.
- `KHR_materials_emissive_strength` if already visually important, otherwise warn and default.
- `KHR_texture_basisu` when compressed texture path is implemented.

Required explicit warnings:
- `KHR_materials_clearcoat`.
- `KHR_materials_sheen`.
- `KHR_materials_transmission`.
- `KHR_materials_ior`.
- `KHR_materials_volume`.
- `KHR_materials_specular`.
- `KHR_materials_iridescence`.
- `KHR_materials_anisotropy`.
- `KHR_materials_unlit`.
- `KHR_materials_pbrSpecularGlossiness`.
- `EXT_meshopt_compression`.
- `KHR_draco_mesh_compression`.
- `MSFT_texture_dds`.

Policy:
- if an extension appears in `extensionsRequired` and is unsupported, fail.
- if an unsupported extension appears only in `extensionsUsed`, warn.
- if an optional unsupported material extension substantially affects appearance, warn at material level.

Acceptance criteria:
- fixture with unsupported required extension fails.
- fixture with unsupported optional extension imports and reports warning.
- extension diagnostics include asset path and extension name.

## Material And Texture Slot Model

Replace path-only material texture fields with structured texture slots.

Suggested model:

```csharp
public sealed class ModelTextureSlot
{
    public ModelTextureSource? Source { get; init; }
    public TextureSamplerDescription Sampler { get; init; }
    public TextureColorSpace ColorSpace { get; init; }
    public int TexCoordSet { get; init; }
    public Vector2 Offset { get; init; } = Vector2.Zero;
    public Vector2 Scale { get; init; } = new(1f, 1f);
    public float RotationRadians { get; init; }
}

public sealed class ModelMaterial
{
    public ModelTextureSlot? BaseColorTexture { get; set; }
    public ModelTextureSlot? NormalTexture { get; set; }
    public ModelTextureSlot? MetallicRoughnessTexture { get; set; }
    public ModelTextureSlot? OcclusionTexture { get; set; }
    public ModelTextureSlot? EmissiveTexture { get; set; }
}
```

Migration plan:
- keep legacy `AlbedoTexturePath`, `NormalTexturePath`, etc. during transition.
- populate structured slots for glTF imports.
- convert legacy path fields to slots in upload service.
- remove or deprecate path-only fields after tests and sample content move.

Acceptance criteria:
- existing tests using path fields still pass during migration.
- new glTF tests assert sampler, texCoord, and transform fields.
- upload service consumes structured slots preferentially.

## Renderer Upload Changes

`ModelRenderUploadService` should:
- resolve structured texture slots.
- choose texture color space by slot role.
- pass sampler description to `TextureManager`/`BindlessHeap`.
- include texture transform and texCoord selection in GPU material data.
- track default substitutions per role.
- track embedded texture uploads separately from file texture uploads.
- release texture handles correctly for shared images with multiple descriptors/samplers.

`TextureManager` should:
- load from file path or memory bytes.
- cache by source identity, color space, mip policy, max dimension, sampler descriptor, and decode/transcode settings.
- support decoded RGBA8, decoded HDR float, and compressed mip-chain uploads.
- expose texture format, mip count, byte estimate, source kind, color space, and compression state.

Acceptance criteria:
- identical embedded image bytes referenced by two materials can share GPU image where sampler/color-space compatibility permits.
- one image referenced with two different samplers produces correct bindless descriptors.
- texture release does not double-free shared images or descriptors.

## Shader Contract Changes

Likely shader changes:
- `GPUVertex` adds vertex color or a separate color stream is introduced.
- `forward.mesh` passes UV0, UV1, vertex color, and material index to fragment shader.
- `forward.frag` chooses UV per material texture slot.
- material texture transform is applied per slot.
- base color includes vertex color when enabled.
- debug view can optionally show UV set, vertex color, texture transform, or material texture role.

Required test updates:
- `GPUStructLayoutTests.cs`.
- shader constants in `common.glsl`.
- `ShaderBuildTests.cs`.
- `ModelRenderUploadServiceTests.cs`.

Acceptance criteria:
- shader build passes.
- struct sizes and offsets are locked by tests.
- procedural geometry such as `SampleReflectionTestSpheres.cs` remains valid after vertex layout changes.

## Sample Content

Add a Phase 14 sample validation scene or loader mode in `NjulfHelloGame`.

Recommended fixtures:
1. `AssetPipeline.ExternalGltf`
   - external `.bin`, external PNG/JPEG textures, standard samplers.
2. `AssetPipeline.EmbeddedGlb`
   - `.glb` with embedded buffer and embedded PNG/JPEG.
3. `AssetPipeline.TextureTransform`
   - clear checker texture with offset/scale/rotation and `TEXCOORD_1`.
4. `AssetPipeline.VertexColor`
   - simple colored mesh with no base color texture.
5. `AssetPipeline.CompressedTexture`
   - KTX2/BCn base color and normal map if supported.
6. `AssetPipeline.HdrEnvironment`
   - HDR environment map loaded through environment settings.

Use `SampleReflectionTestSpheres.cs` as a stable PBR reference near imported assets:
- verify imported color-space and texture transforms against known procedural sphere materials.
- verify compressed and uncompressed material variants under the same lighting.
- verify HDR environment changes affect both imported assets and procedural spheres plausibly.

Acceptance criteria:
- sample mode can switch between asset-pipeline fixtures.
- diagnostics print import warnings and texture memory details.
- failed fixture assets fail clearly instead of crashing later during rendering.

## Tests

Add CPU-only asset tests with tiny generated fixtures.

Suggested test files:
- `Njulf.Tests/GltfGlbReaderTests.cs`
- `Njulf.Tests/GltfEmbeddedResourceTests.cs`
- `Njulf.Tests/GltfSamplerImportTests.cs`
- `Njulf.Tests/GltfTextureTransformTests.cs`
- `Njulf.Tests/GltfVertexStreamTests.cs`
- `Njulf.Tests/GltfExtensionDiagnosticsTests.cs`
- `Njulf.Tests/TextureDecoderTests.cs`
- `Njulf.Tests/TextureManagerCompressedFormatTests.cs`
- `Njulf.Tests/HdrTextureImportTests.cs`
- `Njulf.Tests/AssetImportDiagnosticsTests.cs`

Required test coverage:
- `.glb` header validation.
- JSON/BIN chunk extraction.
- external buffer path resolution.
- data URI buffer decode.
- bufferView image extraction.
- data URI image decode.
- sampler wrap/filter mapping.
- texture transform parsing.
- `texCoord` override parsing.
- UV0 and UV1 import.
- vertex color import and default white fallback.
- unsupported required extension failure.
- unsupported optional extension warning.
- color-space role assignment.
- cache key identity for embedded images.
- compressed texture metadata and fallback policy.
- HDR image decode if decoder is added.

Acceptance criteria:
- `dotnet test Njulf.sln` covers asset parsing without requiring Vulkan.
- renderer upload tests cover texture slot conversion and GPU material packing.
- shader layout tests cover any changed structs.

## Implementation Stages

### Stage 14.1: Diagnostics And Public Contracts

Tasks:
1. Add `AssetImportDiagnostics`, `AssetImportMessage`, severities, and message codes.
2. Add import diagnostics to `ModelMesh`.
3. Add `ModelTextureSource`, `ModelTextureSlot`, `TextureColorSpace`, and `TextureSamplerDescription`.
4. Keep legacy path texture fields for compatibility.
5. Add diagnostics tests.

Acceptance criteria:
- existing imports still work.
- diagnostics can be asserted in tests.
- model upload can ignore structured slots until later stages.

### Stage 14.2: glTF/glb Reader Foundation

Tasks:
1. Add `GltfAssetReader`.
2. Parse `.gltf` JSON.
3. Parse `.glb` JSON and BIN chunks.
4. Validate glTF 2.0 asset version.
5. Parse buffers, buffer views, accessors, images, textures, samplers, materials, meshes, nodes, and extensions metadata.
6. Add malformed GLB and unsupported extension tests.

Acceptance criteria:
- `.glb` fixtures parse without Assimp.
- extension policy is enforced.
- buffer/accessor bounds validation is deterministic.

### Stage 14.3: Embedded Buffers And Images

Tasks:
1. Support external buffers.
2. Support BIN chunk buffers.
3. Support data URI buffers.
4. Support external images.
5. Support data URI images.
6. Support bufferView images.
7. Add cache identities for embedded resources.
8. Add tests for every source kind.

Acceptance criteria:
- embedded `.glb` texture assets import.
- current explicit rejection paths are removed or replaced with supported behavior.
- diagnostics count source kinds.

### Stage 14.4: Structured Material Texture Slots

Tasks:
1. Populate `ModelTextureSlot` for base color, normal, metallic-roughness, occlusion, and emissive.
2. Preserve factors, alpha mode, alpha cutoff, double-sided, normal scale, and occlusion strength.
3. Assign color spaces by role.
4. Map glTF texture index to image source and sampler.
5. Keep path fields populated for legacy compatibility where possible.
6. Update upload service to prefer texture slots.

Acceptance criteria:
- existing material import tests still pass.
- new tests assert sampler, color space, texture source, and role.
- AO shared ORM behavior still works.

### Stage 14.5: Texture Loading From Memory And Samplers

Tasks:
1. Add `TextureManager.LoadTexture` overload for `ModelTextureSource`.
2. Add sampler cache and imported sampler mapping.
3. Include sampler state in bindless descriptor identity.
4. Add file/memory cache identity tests.
5. Add sampler diagnostics.

Acceptance criteria:
- buffer-view images upload to GPU.
- clamp/repeat/nearest/linear sampler fixtures render correctly.
- same image with different samplers behaves correctly.

### Stage 14.6: Multiple UV Sets And Texture Transforms

Tasks:
1. Add UV0 and UV1 to model data.
2. Populate `GPUVertex.TexCoord2`.
3. Parse per-texture `texCoord`.
4. Parse and apply `KHR_texture_transform`.
5. Update material GPU data and shaders.
6. Add layout, shader, and import tests.

Acceptance criteria:
- UV1 fixture renders correctly.
- texture offset/scale/rotation fixture renders correctly or rotation is explicitly diagnosed if deferred.
- default UV0 assets remain unchanged.

### Stage 14.7: Vertex Colors

Tasks:
1. Import `COLOR_0`.
2. Add CPU model vertex color stream.
3. Add GPU vertex color path.
4. Update procedural geometry constructors, including `SampleReflectionTestSpheres.cs`, if the vertex struct expands.
5. Multiply base color by vertex color in shader.
6. Add tests and sample fixture.

Acceptance criteria:
- vertex-colored asset renders without a texture.
- missing vertex colors default to white.
- all GPU layout tests pass.

### Stage 14.8: Compressed Textures

Tasks:
1. Choose KTX2/Basis/BCn decoder library and isolate it behind an interface.
2. Parse KTX2 metadata and mip levels.
3. Probe Vulkan compressed format support.
4. Upload native compressed mips where supported.
5. Transcode or fallback when unsupported.
6. Add compressed texture diagnostics and memory savings fields.
7. Add tests with small fixtures.

Acceptance criteria:
- compressed texture fixture loads.
- memory estimate is lower than RGBA fallback.
- unsupported format behavior is deterministic.

### Stage 14.9: HDR Environment Loading

Tasks:
1. Add HDR decoder path.
2. Upload floating-point environment source textures.
3. Integrate with `EnvironmentManager`.
4. Add diagnostics for HDR dimensions, format, and bytes.
5. Add sample environment fixture.

Acceptance criteria:
- HDR equirectangular environment renders through existing IBL path.
- invalid HDR file fails with useful diagnostics.

### Stage 14.10: Sample Validation And Reporting

Tasks:
1. Add asset-pipeline sample fixtures.
2. Add sample scene mode or loader option.
3. Print import diagnostics in `SampleDiagnosticsReporter.cs`.
4. Include `SampleReflectionTestSpheres.cs` in validation scene.
5. Capture RenderDoc frames for external glTF, embedded GLB, texture transform, vertex color, compressed texture, and HDR environment samples.

Acceptance criteria:
- sample assets demonstrate the hardened pipeline.
- unsupported optional extensions are visible but non-fatal.
- validation layers remain clean.

## Diagnostics Fields

Extend upload/import diagnostics with:

```csharp
int ImportedGltfCount;
int ImportedGlbCount;
int ExternalBufferCount;
int EmbeddedBufferCount;
int DataUriBufferCount;
int ExternalImageCount;
int EmbeddedImageCount;
int DataUriImageCount;
int BufferViewImageCount;
int ImportedSamplerCount;
int UniqueGpuSamplerCount;
int TextureTransformCount;
int UnsupportedOptionalExtensionCount;
int UnsupportedRequiredExtensionCount;
int VertexColorMeshCount;
int Uv0MeshCount;
int Uv1MeshCount;
int CompressedTextureCount;
int CompressedTextureFallbackCount;
ulong CompressedTextureEstimatedBytes;
ulong UncompressedEquivalentEstimatedBytes;
int HdrTextureCount;
```

Sample diagnostics line:

```text
Asset diagnostics: gltf=1, glb=1, externalBuffers=1, embeddedBuffers=1, externalImages=4, embeddedImages=3, samplers=5/3 unique, transforms=2, vertexColorMeshes=1, uv1Meshes=2, compressed=4 fallback=0, textureMiB=38.4 savedMiB=91.2, optionalExtensionWarnings=3.
```

Acceptance criteria:
- diagnostics reset per import/upload scope where appropriate.
- sample reporting stays concise.
- detailed messages remain available for logs/tests.

## Validation Matrix

Validate these asset cases:
1. plain external `.gltf` with external `.bin` and PNG.
2. `.gltf` with data URI buffer.
3. `.gltf` with data URI image.
4. `.glb` with embedded BIN geometry and embedded image.
5. `.glb` with buffer-view image.
6. material with clamp sampler.
7. material with nearest sampler.
8. material with UV1 texture slot.
9. material with `KHR_texture_transform`.
10. mesh with `COLOR_0`.
11. KTX2/Basis or BCn texture asset.
12. HDR environment image.
13. unsupported required extension.
14. unsupported optional extension.
15. missing external image.
16. Git LFS pointer texture.

Acceptance criteria:
- each case has an automated test or documented manual validation scene.
- failures happen at import/upload time with actionable messages.
- no case reaches shader sampling with invalid descriptor indices.

## Risk Register

High-risk areas:
- replacing Assimp behavior may change transforms, winding, tangent basis, or UV flipping.
- `.glb` and accessor parsing can introduce subtle byte-offset and stride bugs.
- texture transform convention mistakes are easy to miss visually.
- expanding `GPUVertex` can increase memory bandwidth and require many shader/test updates.
- compressed texture library choice can add platform/build complexity.
- sampler-per-texture bindless descriptors can increase descriptor usage.
- embedded images can create cache identity and lifetime bugs.
- color-space mistakes can look plausible but physically wrong.

Mitigations:
- keep fixture assets tiny and targeted.
- lock matrix, winding, and UV behavior with tests.
- compare before/after screenshots for Sponza and sample assets.
- add RenderDoc validation for vertex attributes and texture descriptors.
- isolate compressed texture support behind a decoder interface.
- track descriptor counts and texture memory diagnostics.
- keep legacy path fallback until glTF reader parity is proven.

## Industry-Standard Definition Of Done

Phase 14 is production-ready when:
1. `.gltf` and `.glb` assets from common DCC tools load without manual unpacking.
2. external, embedded, data URI, and buffer-view resources are supported.
3. texture samplers are honored.
4. texture transforms and texture coordinate set selection work.
5. vertex color and multiple UV sets work.
6. compressed texture assets reduce memory on supported hardware.
7. HDR environment maps load through the content pipeline.
8. color-space policy is explicit, tested, and reflected in texture formats.
9. unsupported extensions produce clear diagnostics.
10. missing assets and malformed data fail early with asset path and source location.
11. renderer upload uses safe fallbacks and never binds invalid descriptors.
12. sample validation demonstrates the feature set under real lighting and reflection conditions.
13. CPU tests cover the parser and import contracts.
14. shader and GPU layout tests cover changed renderer contracts.
15. RenderDoc captures show expected textures, samplers, vertex attributes, and material constants.

## Final Acceptance Checklist

Before marking Phase 14 complete:
1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Import external `.gltf`, embedded `.glb`, texture-transform, UV1, vertex-color, compressed-texture, and HDR environment fixtures.
4. Run `NjulfHelloGame` with Vulkan validation enabled.
5. Capture a RenderDoc frame for the asset-pipeline sample scene.
6. Verify imported samplers in descriptors.
7. Verify UV0/UV1 and vertex color in mesh shader inputs.
8. Verify texture transforms in material sampling.
9. Verify albedo/emissive textures use sRGB formats and normal/ORM/AO use linear formats.
10. Verify compressed textures use native compressed formats or report deterministic fallback.
11. Verify HDR environment textures use floating-point formats.
12. Verify unsupported required extensions fail clearly.
13. Verify unsupported optional extensions report warnings.
14. Verify missing files and Git LFS pointer textures fail at import/upload with useful messages.
15. Verify resize, scene reload, and shutdown remain validation-clean.

The phase is done when artists can export normal `.glb` or `.gltf` files from their DCC tools, place them in the project, and get predictable rendering or precise diagnostics without changing renderer code or manually unpacking asset internals.
