# Renderer Architecture 05 - Visibility-First Frame Plan - 2026-06-14

Target outcome: the frame is organized around early visibility and work reduction. Depth, Hi-Z, culling, LOD, light clustering, and compacted render lists are prepared before expensive shading. All later passes consume known-visible work and resolution-appropriate resources.

The purpose is to maximize performance without sacrificing quality. The renderer should spend shading, transparency, particle, reflection, and post cost only where the frame needs it.

## Non-Negotiable Requirements

1. Visibility decisions happen before expensive shading.
2. Depth and Hi-Z are dimensionally consistent with scene resolution.
3. Light culling uses the same depth and resolution policy as forward shading.
4. LOD, impostor, foliage, and occlusion decisions are visible in diagnostics.
5. Pass order is chosen from measured dependencies, not historical convenience.
6. Old duplicate visibility decisions are removed as the new frame structure becomes authoritative.

## Target Frame Shape

1. Begin frame and acquire swapchain image.
2. Upload dirty GPU scene changes.
3. Run animation/skinning needed for current bounds or vertex data.
4. Run object/instance visibility and LOD selection.
5. Depth prepass or visibility depth pass.
6. Build Hi-Z.
7. Run occlusion refinement and meshlet compaction.
8. Build light tiles/clusters from final depth.
9. Render shadows from compacted shadow lists.
10. Render opaque forward.
11. Render sky/environment.
12. Render decals or material overlays as appropriate.
13. Render transparent/OIT path.
14. Render particles.
15. Render debug overlays.
16. Run fog, exposure, bloom, tone map, AA/upscale, and composite.
17. Present and retire frame resources.

## Phase 0 - Dependency Audit

1. List every current pass and its true dependencies.
2. Mark each pass as:
   - visibility producer.
   - visibility consumer.
   - shading pass.
   - post pass.
   - diagnostics pass.
   - presentation pass.
3. Identify passes currently doing work before visibility is known.
4. Identify passes that can be skipped when no visible consumers exist.
5. Add a frame-order diagnostics dump.

Acceptance criteria:
- Pass order can be justified by dependencies and performance impact.

## Phase 1 - Dirty Upload And Animation Before Visibility

1. Upload dirty object/material/mesh state first.
2. Run GPU skinning before bounds-dependent culling when skinned bounds require updated pose.
3. Support conservative skinned bounds when full skinned bounds are too expensive.
4. Track animation/skinning cost separately from visibility cost.
5. Ensure previous transforms are stable for motion vectors.

Acceptance criteria:
- Visibility uses current frame transforms and valid bounds.

## Phase 2 - Early Object And LOD Visibility

1. Run object frustum culling before any per-meshlet expansion.
2. Run LOD selection immediately after object visibility.
3. Cull entire object/instance clusters before meshlet work.
4. Choose impostors before meshlet expansion for far objects.
5. Output visible object list and selected representation.

Acceptance criteria:
- Far or invisible objects do not create meshlet candidates for main shading.

## Phase 3 - Depth Prepass Policy

1. Decide when depth prepass is required:
   - required for Hi-Z occlusion.
   - required for SSAO/fog/light culling quality.
   - beneficial for heavy overdraw.
   - optional for very small scenes.
2. Add adaptive policy driven by measured benefit:
   - depth prepass cost.
   - forward overdraw savings.
   - Hi-Z culling savings.
3. Ensure alpha-tested depth participation is correct.
4. Expose depth-prepass decision and measured benefit in diagnostics.
5. Avoid disabling depth prepass when downstream passes require depth.

Acceptance criteria:
- Depth prepass is skipped only when it is safe and measured to help.

## Phase 4 - Hi-Z And Occlusion Refinement

1. Build Hi-Z directly after depth.
2. Use scene-resolution depth as mip 0.
3. Use graph-generated barriers and no manual depth path duplication.
4. Run occlusion refinement after Hi-Z.
5. Compact final visible meshlet lists after occlusion.
6. Add hysteresis/conservative policy to avoid popping.

Acceptance criteria:
- Occlusion reduces downstream meshlet count without visible artifacts.

## Phase 5 - Light Culling After Depth

1. Run tiled or clustered light culling after final depth/Hi-Z is available.
2. Use scene resolution tile dimensions.
3. Support debug overlays for light pressure.
4. Add depth-aware min/max per tile when useful.
5. Skip light culling entirely when no local lights affect the scene.
6. Ensure forward pass consumes culling buffers generated this frame.

Acceptance criteria:
- Local-light scenes pay light culling once and forward shading uses compact light lists.

## Phase 6 - Shadow Scheduling

1. Select visible shadow-casting lights before shadow passes.
2. Use GPU-generated shadow lists.
3. Render only required cascades/lights/faces.
4. Support lower LOD or impostor policy for far shadows.
5. Reuse visibility data where camera/light frusta allow it.
6. Skip shadow passes with zero compacted casters.

Acceptance criteria:
- Shadow work scales with visible casters and selected lights, not total scene content.

## Phase 7 - Opaque Shading

1. Forward pass consumes compact opaque list.
2. Bind light culling, material, meshlet, and GPU scene buffers through graph-declared dependencies.
3. Avoid per-frame CPU draw list upload.
4. Keep material debug and selected-object debug working from GPU scene IDs.
5. Measure emitted meshlets and shaded pixels.

Acceptance criteria:
- Opaque shading receives only visible compacted work.

## Phase 8 - Transparency, OIT, And Particles

1. Generate transparent candidates during visibility.
2. Sort or OIT-accumulate after opaque depth is available.
3. Use scene depth for soft particles and depth-tested transparency.
4. Simulate GPU particles before particle visibility/rendering.
5. Cull particle emitters and particle tiles before drawing.
6. Skip transparent/particle passes when their compacted counts are zero.

Acceptance criteria:
- Transparency and particles are ordered after opaque depth and pay only for visible content.

## Phase 9 - Post And Composite

1. Run post passes after all scene color contributors.
2. Classify post passes by resolution:
   - scene resolution.
   - half/quarter resolution.
   - swapchain resolution.
   - history resolution.
3. Handle dynamic resolution through graph resource classes.
4. Invalidate histories on resolution change.
5. Composite/upscale once at the end.
6. Avoid repeated full-screen passes when pass fusion is safe and measured beneficial.

Acceptance criteria:
- Pixel work is scaled appropriately and final output quality remains stable.

## Phase 10 - Remove Old Architecture

Delete or replace:

1. Duplicate CPU culling paths no longer feeding production.
2. Per-pass checks that compensate for wrong pass order.
3. Manual pass skips that the graph can derive from unused outputs.
4. Any old render-list upload path bypassing visibility-first compaction.
5. Any target-size workaround left from pre-graph dynamic resolution.

Acceptance criteria:
- The production frame has one visibility path and one frame-order authority.

## Validation

1. Unit tests:
   - pass dependency ordering.
   - pass culling rules.
   - dynamic resolution resource sizing.
   - zero-count pass skipping.
2. Integration tests:
   - empty scene.
   - many opaque objects.
   - alpha-tested foliage.
   - transparent scene.
   - particle scene.
   - local-light scene.
   - shadow-heavy scene.
3. Performance validation:
   - visible work decreases with occlusion/LOD.
   - skipped passes report zero CPU/GPU time.
   - no GPU readbacks in frame loop.
   - no hidden `DeviceWaitIdle` in normal frame flow.

## Definition Of Done

1. Frame order is visibility-first and graph-validated.
2. Expensive passes consume compacted visible work.
3. Zero-work passes are skipped safely.
4. Old duplicate visibility and pass-order workarounds are removed.
5. Diagnostics prove work reduction and quality validation passes.

## Implementation Notes - 2026-06-14

- Phase 0 dependency audit is implemented in `VisibilityFirstFramePlanner`, classifying production passes by role and validating key visibility-first ordering constraints.
- Unit tests verify current production ordering, required visibility/light/depth dependencies, and zero-work skip classification for optional passes.
- Production frame order is visibility-first: `GpuVisibilityPass`, `DepthPrePass`, `MotionVectorPass`, `HiZBuildPass`, `GpuOcclusionCompactionPass`, AO, light culling, shadows, opaque, sky, transparency/OIT, particles, debug, post, composite, AA/present.
- `GpuOcclusionCompactionPass` reuses the GPU visibility compute path after current-frame Hi-Z, then rewrites compact draw lists consumed by light culling, shadows, opaque, and transparency.
- Runtime pass skipping is centralized in `FramePassRuntimePolicy` for depth, Hi-Z, light culling, shadows, transparency/OIT, particles, and debug draw zero-work decisions.
- Depth prepass enablement is policy-driven: user preference can skip optional depth work, but downstream consumers such as opaque depth, Hi-Z, AO, fog, TAA motion vectors, local light culling, transparency, and particles keep depth enabled.
- Render graph diagnostics and inventory now expose the final compaction producer for draw-list buffers and the new production pass order.
