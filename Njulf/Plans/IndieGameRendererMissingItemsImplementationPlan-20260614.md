# Indie Game Renderer Missing Items Implementation Plan

Date: 2026-06-14

Source audit: `Plans/IndieGameRendererProductionPlanMissingItems-20260614.md`

This plan reconciles the audit against the current codebase. Items already implemented are skipped with rationale. Items that are useful and safe to implement as CPU-side production contracts are included in this change. GPU features that require new render passes, shaders, authored content, and live graphics validation are left as explicit follow-up phases rather than being represented by placeholders.

## Phase 2 - HDR Pipeline and Tone Mapping

Status: mostly already implemented.

- Keep existing HDR scene color, tone mapping, manual exposure, and auto exposure paths. Current code includes `AutoExposureManager`, `AutoExposurePass`, and `auto_exposure.comp`.
- Follow-up: validate the histogram/adaptation path in a live graphics run and capture RenderDoc evidence.
- No code change needed in this pass.

## Phase 6 - Sky, Environment, and IBL

Status: mostly already implemented.

- Keep existing Radiance `.hdr` loading, equirectangular-to-cubemap conversion, irradiance generation, prefiltered environment generation, BRDF LUT, and procedural fallback in `EnvironmentManager` and `EnvironmentMapProcessor`.
- Follow-up: move convolution/prefilter work to GPU or an offline cache if HDRI load time becomes a content-pipeline bottleneck.
- No code change needed in this pass.

## Phase 8 - Anti-Aliasing

Status: current implementation exists, live validation remains.

- Keep current motion-vector pass and TAA velocity-buffer integration.
- Follow-up: validate camera/object velocity correctness and anti-ghosting using RenderDoc and moving-object test content.
- No code change needed in this pass.

## Phase 10 - Reflection Pipeline

Status: partial.

- Keep existing static probe metadata, box projection/blending data, and capture/prefilter diagnostic counters.
- Follow-up: implement real probe cubemap capture/filtering before enabling probe captures by default.
- Skip SSR and planar reflections for now because they are optional in the original plan and not broadly useful unless a target game needs water, mirrors, or glossy screen-space detail.

## Phase 11 - Transparency and Decals

Status: partial.

- Keep sorted alpha blending, masked/decal material metadata, alpha-tested depth support, and geometry decals.
- Follow-up: implement projected decal buffers/pass only when dynamic decals are required by game content.
- Skip weighted blended OIT for now because sorted transparency is the lower-risk default and OIT adds render targets/composite work that should be driven by content need.

## Phase 12 - Animation Support

Status: partial.

- Keep skeleton, clips, CPU sampling, GPU skinning, and animation bounds work.
- Added explicit import diagnostics for morph targets so unsupported blend-shape assets are visible.
- Follow-up: implement morph target import/storage/render path only if character facial animation or deformations are required.

## Phase 13 - Particles and VFX

Status: CPU path remains the production default.

- Keep CPU particle simulation and batching. It is simpler and appropriate until measured particle counts require GPU simulation.
- Follow-up: add GPU simulation only after authoring/performance data shows CPU simulation is a bottleneck.

## Phase 14 - Asset Pipeline Hardening

Status: improved in this pass.

- `.glb`, embedded buffers/images, sampler import, texture transforms, vertex colors, multiple UV sets, and HDR environment loading already exist.
- Added fail-fast diagnostics for required `KHR_texture_basisu` / KTX2-Basis textures instead of implying decode support.
- Added morph-target diagnostics.
- Follow-up: implement actual KTX2/Basis/BCn decode/upload path before declaring compressed texture support complete.

## Phase 15 - LOD, Instancing, and World Scale

Status: improved in this pass.

- Existing generated meshlet LODs, distance selection, static instance batches, and Hi-Z occlusion remain.
- Added CPU LOD hysteresis to reduce threshold popping.
- Follow-up: authored mesh LOD import, foliage-specific batching, impostors, and GPU-driven compaction require asset/runtime design and are not useful to add as placeholders.

## Phase 17 - Render Settings and Quality Levels

Status: improved in this pass.

- Replaced old development/performance/cinematic preset names with Low, Medium, High, and Ultra.
- Added centralized `RenderSettings.ApplyQualityPreset`.
- Added clamped global resolution-scale settings and dynamic-resolution policy state.
- Added JSON save/load for production settings.
- Follow-up: wire resolution scale into a dedicated scaled scene-color/depth path with an upscale pass. The current renderer is swapchain-depth sized, so forcing sub-resolution targets without that pass would be unsafe.

## Phase 18 - Editor and Debug Tooling Hooks

Status: partial.

- Keep existing debug draw collection, selected-object inspection, GPU timing, screenshot, and RenderDoc hooks.
- Follow-up: add actual debug line/volume render passes and visual overlays for tiles, probes, decals, bounds, timings, and memory.

## Phase 20 - Shipping Hardening

Status: improved in this pass.

- Changed forced missing-asset smoke mode from unconditional skipped to a controlled CPU-side content-load failure check.
- Follow-up: add renderer-backed missing texture/material fallback smoke once the graphics smoke runner can safely mutate a temporary scene.

## Verification Plan

- Run `dotnet build Njulf.sln`.
- Run `dotnet test Njulf.sln --no-build`.
- For GPU-scale follow-ups, validate with Vulkan validation layers and RenderDoc rather than relying on CPU tests.
