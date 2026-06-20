# Asset Export Recommendations

These rules describe the asset shapes Njulf is designed to import predictably.

## Runtime Format

1. Export runtime models as glTF 2.0 `.gltf` or `.glb`.
2. Use `.glb` when the asset should be a single transport file.
3. Use `.gltf` with external `.bin` and image files when artists need editable package contents.
4. Keep OBJ and FBX for legacy/non-runtime interchange only; those formats use the Assimp fallback path.

## Materials

1. Prefer standard metallic-roughness PBR materials.
2. Use `MASK` alpha for dense foliage, leaf cards, and grass.
3. Avoid `BLEND` alpha for dense foliage unless sorting and overdraw cost are intentional.
4. Set a clear alpha cutoff for masked foliage.
5. Mark foliage cards double-sided when the asset depends on two-sided rendering.
6. Optional material extensions may import as data, but renderer support decides whether they affect final pixels.

## Textures

1. Keep base color and emissive textures in sRGB color space.
2. Keep normal, metallic-roughness, occlusion, and mask textures in linear color space.
3. Keep texture dimensions inside the active project budget before committing the asset.
4. PNG and JPEG are supported for standard image transport.
5. KTX2/BC content should be used only when the renderer supports the chosen container and compression mode.
6. Avoid required BasisU, WebP, AVIF, DDS, Draco, or meshopt assets until the importer and renderer explicitly support that path.

## Geometry

1. Triangulate meshes before export where possible.
2. Include normals and tangents for lit assets.
3. Include UV0 for textured materials; include UV1 only when the material or renderer uses it.
4. Keep vertex color streams only when they carry authored data.
5. Include LODs or author game-ready mesh density for downloaded marketplace assets.

## Validation

1. Run `Njulf.AssetTool report <asset-folder> --json <report-path>` before referencing downloaded assets from a sample scene.
2. Treat unsupported required extensions as asset-prep failures.
3. Treat high texture memory warnings as budget review items.
4. Prefer validation reports that name the selected backend, classifications, diagnostics, and decision notes.
