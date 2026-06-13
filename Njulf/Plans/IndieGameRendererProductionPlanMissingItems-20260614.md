# Indie Game Renderer Production Plan - Missing Items Audit

Audit date: 2026-06-14

Reviewed plan: `Plans/Complete/IndieGameRendererProductionPlan.md`

Reviewed sample controls: `NjulfHelloGame/SampleInputController.cs`

Verification run:

- `dotnet build Njulf.sln` - passed with 0 warnings and 0 errors.
- `dotnet test Njulf.sln --no-build` - passed 189 tests.

This list focuses on plan items that are not implemented or are only represented by placeholders/settings. Manual validation items that cannot be proven by static review are listed separately at the end.

## Missing or Incomplete Implementation Items

### Phase 2 - HDR Pipeline and Tone Mapping

- Auto exposure is not implemented. The renderer has manual exposure controls and tone mapping, but no luminance histogram, average luminance reduction, adaptation, or automatic exposure path was found.

### Phase 6 - Sky, Environment, and IBL

- Real HDR environment loading is not implemented. `EnvironmentSettings` exposes `SourceKind` and `SourcePath`, and the sample can request an HDR equirectangular environment, but `EnvironmentManager` always generates procedural sky data instead of loading or converting the configured source.
- Equirectangular-to-cubemap conversion for authored HDRI assets was not found.
- Diffuse irradiance and prefiltered specular are generated from the procedural fallback environment only; authored environment convolution/prefiltering is not implemented.
- The environment fallback flag appears to remain active because the environment path always uses generated data.

### Phase 8 - Anti-Aliasing

- Motion vector generation is missing. A motion vector render target exists, but no render pass or shader writes velocity data to it.
- TAA is implemented as a history resolve without motion vectors. `MotionVectorsEnabled` is set to disabled, and the TAA shader does not consume a velocity buffer.
- Velocity-based TAA anti-ghosting/reprojection is therefore incomplete.

### Phase 10 - Reflection Pipeline

- Static reflection probe capture and filtering are not implemented. Probe metadata and shader blending exist, but probe captures are not queued or completed, and probe sampling uses the global environment fallback rather than captured cubemap contents.
- Reflection probe capture/filtering tooling was not found.
- SSR is not implemented. Only enum/debug-view placeholders were found.
- Planar reflections are not implemented. Only enum/debug-view placeholders were found.

### Phase 11 - Transparency and Decals

- Projected decals are not implemented. Geometry decal/material support exists, but no projected decal data path, pass, or shader was found.
- Weighted blended OIT is not implemented. The enum/debug option exists, but no accumulation/revealage render targets or OIT composite pass were found.

### Phase 12 - Animation Support

- Morph targets are not implemented. Skeletons, clips, CPU sampling, and GPU skinning exist, but no morph target import, storage, or render path was found.

### Phase 13 - Particles and VFX

- GPU particle simulation is not implemented. The settings include a GPU simulation mode, but particle updates still run through the CPU simulation path.

### Phase 14 - Asset Pipeline Hardening

- KTX2/Basis/BCn compressed texture support was not found.
- HDR image loading for environment assets is missing, which also blocks the authored HDRI workflow from Phase 6.

### Phase 15 - LOD, Instancing, and World Scale

- Imported mesh LOD support was not found. The renderer generates and selects meshlet LOD ranges, but no asset-authored mesh LOD import path was found.
- LOD hysteresis was not found.
- Foliage-specific batching was not found.
- Impostors are not implemented.
- GPU-driven draw or meshlet compaction was not found. Static mesh buffer memory compaction exists, but that is not the same as GPU-driven draw compaction.

### Phase 17 - Render Settings and Quality Levels

- The required Low/Medium/High/Ultra quality presets are missing. The existing `RenderQualityPreset` values are `Development`, `PerformanceCapture`, and `Cinematic`.
- Global render resolution scale is missing. Ambient occlusion has a resolution scale and budget profiles carry a resolution-scale value, but there is no general scene/render-target resolution scale.
- Dynamic resolution is not implemented.
- Save/load of render settings from a config file was not found.

### Phase 18 - Editor and Debug Tooling Hooks

- `DebugDrawList` data collection exists, but no render pass or shader was found that draws the submitted debug lines, boxes, spheres, or frustums.
- Several debug overlay modes appear to be settings/diagnostics only rather than visual overlays, including light tiles, reflection probe volumes, decal volumes, object bounds, meshlet bounds, pass timings, and GPU memory.
- Decal volume debug draw reporting is hard-coded to zero, so decal volume visualization is not active.

### Phase 20 - Shipping Hardening

- The controlled missing-asset smoke scenario is still skipped. `SampleLifecycleSmokeRunner` records the missing-asset scenario as skipped even when forced, with a note that importer fallback wiring is not active.

## Manual Verification Gaps

These items may be implemented, but this audit did not prove them because they require a live graphics run, GPU tooling, or manual inspection:

- Vulkan validation-layer clean startup, resize, render, and shutdown.
- RenderDoc frame capture readability and naming completeness.
- Visual correctness of HDR, bloom, shadows, AO, AA, fog, reflections, transparency, particles, and debug overlays.
- Performance and memory budgets on low-spec, recommended, and target hardware.
- Long-run soak behavior, device lost handling, alt-tab behavior, fullscreen behavior, and CI graphics smoke behavior on actual GPU runners.
