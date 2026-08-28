# Global Illumination Remaining Features Roadmap

Last updated: 2026-08-28

## Current state

The core DDGI implementation is already feature-rich. It includes camera-relative
probe rings and authored volumes, multi-bounce transport, GPU scheduling,
receiver feedback, persistent warm starts, cached relighting, emissive mesh and
VFX sampling, refinement bricks, near-visibility data, far-field caching,
directional radiance, glossy receiver modes, transparent geometry modes, and the
C1/C3/C4/C5 advanced paths.

Several entries in `implementation/GI.md` are therefore stale. Emissive sampling,
animated emissives, VFX emitters, HDR environments, multi-bounce transport,
opacity micromaps, directional guiding, hero caustics, and C5 near-field GI
already have implementations. This document tracks the remaining work instead.

The 2026-08-27 Forward+ adaptive-quality closure also completed the bounded C5
local scheduler, temporal surface-aware DDGI receiver cache, GTAO/bent-normal
indirect lighting, transparent pipeline partitioning, and conservative meshlet
normal-cone culling. Their preset activation is:

| Preset | AO | Bent-normal diffuse | DDGI receiver | C5 local scheduler |
| --- | --- | --- | --- | --- |
| Low | Disabled | Off | Exact | Off |
| Medium | GTAO Low | Off | Exact | Off |
| High | GTAO Balanced | Environment | Exact | On |
| DdgiHigh | GTAO High | Off | Exact | On |
| Ultra | GTAO High | Environment + exact DDGI | Exact | On |

Meshlet cones and transparent partitioning are enabled in every preset and
fail closed to visible/universal behavior when their inputs are unavailable.
GTAO similarly falls back to SSAO when its required formats are unsupported.
The supplied 2026-08-27 hardware frames failed the receiver-cache visual gate,
so the cache candidates are no longer runtime defaults. The same evidence also
showed a separate accelerated DDGI fixed-point-tail failure; production uses
the certified Jacobi solver while acceleration remains explicitly selectable
for qualification. P2 content-dependent features remain optional and were not
part of this closure.

## Recommended implementation order

1. Recover performance headroom in C5 and the DDGI receiver cache.
2. Implement true froxel volumetric fog and smoke.
3. Add hybrid ray-query reflections for sharp indirect specular.
4. Add thick transmission, refraction, and generalized caustics.
5. Add analytical area lights.
6. Graduate dynamic, foliage, and procedural geometry participation.
7. Complete quality-tier tuning and cross-vendor qualification.

## Remaining feature work

### P0: True froxel volumetric fog and smoke //DONE

The existing fog path is analytic distance/height fog with a coarse DDGI ambient
tint. The B5 directional-fog experiment cannot currently be admitted because the
renderer has neither a production L2 incident-radiance sidecar nor a froxel phase
consumer.

- [x] Add a camera-relative 3D froxel grid with logarithmic depth slicing.
- [x] Inject global height fog and bounded local density volumes.
- [x] Support animated 3D density noise, wind/flow, and particle/VFX smoke
      injection.
- [x] Inject sunlight, point lights, spotlights, and emissive lighting.
- [x] Apply shadow maps or ray-query visibility to direct volumetric lighting.
- [x] Sample directional L2 DDGI incident radiance per froxel or froxel cluster.
- [x] Convolve indirect radiance with a Henyey-Greenstein phase function.
- [x] Keep direct and indirect volumetric energy ownership explicitly separated.
- [x] Add temporal jitter, reprojection, history rejection, and depth-aware
      upsampling.
- [x] Add volumetric self-shadowing and a coarse transmittance representation so
      dense smoke can attenuate both direct and indirect lighting.
- [x] Add a bounded multiple-scattering approximation after the single-scattering
      path is qualified.
- [x] Add debug views for density, extinction, direct radiance, indirect radiance,
      history confidence, and final transmittance.

### P0: Hybrid ray-query reflections //DONE

The current reflection system supports the environment, static probes, SSR, and
planar reflections. Directional DDGI provides rough low-frequency specular energy,
but it is not a replacement for sharp scene reflections.

- [x] Use SSR as the first reflection source.
- [x] Trace ray queries only for invalid, off-screen, disoccluded, or low-confidence
      SSR samples.
- [x] Shade reflection hits using direct lights, emissives, and DDGI.
- [x] Add roughness-dependent resolution and ray budgets.
- [x] Add temporal accumulation, spatial filtering, and disocclusion handling.
- [x] Implement the fallback chain: SSR, ray query, local reflection probe,
      global environment.
- [x] Handle transparent and alpha-tested hit policies consistently with DDGI.
- [x] Add reflection confidence and source-selection debug views.

### P1: Thick transmission, refraction, and generalized caustics //DONE

The current transparent GI contract supports masked geometry, thin transmission,
and stochastic blend. It does not provide physical thick refraction, nested media,
or general participating-volume transport. The existing hero caustic cache is a
useful specialized path, not a general solution.

- [x] Track ray entry and exit surfaces for closed dielectric objects.
- [x] Implement configurable IOR and total internal reflection.
- [x] Implement Beer-Lambert absorption and colored glass.
- [x] Support rough refraction and thickness-aware transmission.
- [x] Add a bounded nested dielectric/media stack.
- [x] Add water-surface integration and moving-water caustics.
- [x] Generalize caustics beyond hero-tagged emitters and receivers.
- [x] Add optional dispersion only after the base transport path is stable.
- [x] Define deterministic fallbacks for unsupported or over-budget paths.

### P1: Analytical area lights //DONE

The analytical light manager currently supports point, directional, and spot
lights. Emissive meshes cover some area-light use cases but do not replace
controllable analytical lights.

- [x] Add rectangular lights.
- [x] Add disk lights.
- [x] Add tube/line lights if required by target content.
- [x] Use LTC evaluation for rasterized direct lighting.
- [x] Add correct area-light sampling or MIS at DDGI ray hits.
- [x] Add optional IES photometric profiles.
- [x] Include area lights in many-light scheduling, diagnostics, and mutation
      invalidation.

### P1: Graduate dynamic and procedural geometry participation

Support paths exist for skinned geometry, transparent geometry, and foliage, but
the cheaper quality tiers still rely on conservative proxies or exclusions.

- [ ] Make certified current-pose skinned transport practical at High quality.
- [ ] Improve foliage participation without allowing alpha geometry to dominate
      traversal cost.
- [ ] Integrate runtime terrain and other procedural geometry.
- [ ] Support destructible and topology-changing geometry with bounded
      invalidation.
- [ ] Integrate deforming water surfaces.
- [ ] Add per-content-class BLAS rebuild/refit/LOD budgets.
- [ ] Preserve certified transport-tail behavior during continuous animation.

### P1: Faster urgent and local Transport V2 convergence

The existing pre-forward urgent lane provides same-frame response for a bounded
set of visible near-ring probes, but it is limited to one cached transport update
and radiometric changes whose cached geometry is already valid. Extend this path
to accelerate the visible first indirect bounces of urgent and local changes
without claiming that the entire global field can always converge in one frame.

- [ ] Measure edit-to-first-visible-response and edit-to-certified-convergence
      P50/P95/P99 separately for environment, light, emissive, material,
      transform, and topology changes.
- [ ] Extend the pre-forward urgent lane from one cached transport update to a
      bounded private multi-sweep solve for eligible radiometric changes.
- [ ] Select one to four same-frame cached sweeps from residual, contraction, and
      available GPU-headroom evidence.
- [ ] Process a bounded transitive dependency neighborhood so the most important
      first indirect bounces of a local change propagate during the same frame.
- [ ] Spill remaining dependent probes into the existing sparse residual queue
      without weakening fairness or eventual full-field coverage.
- [ ] Publish only complete coherent probe payloads; never expose intermediate
      sweep state to receivers.
- [ ] Keep the urgent transaction's canonical source cache immutable and issue no
      additional primary or shadow ray queries.
- [ ] Keep geometry and topology changes on the ordinary post-forward path unless
      current-frame acceleration-structure readiness is explicitly supported and
      proven safe.
- [ ] Preserve the canonical positive Transport V2 operator and its existing tail
      tolerance; acceleration may change execution order and latency, not the
      certified fixed point.
- [ ] Prove that the eventual field matches the canonical long-run solve within
      FP16 tolerance and that every certification claim still comes from a
      complete generation-current tail audit.
- [ ] Enforce a strict GPU-time budget and fall back to the ordinary sparse
      propagation path when same-frame work would exceed it.

### P2: Optional content-dependent features

- [ ] Couple volumetric clouds and cloud shadows into the sky and GI systems.
- [ ] Add physically consistent aerial perspective for large outdoor scenes.
- [ ] Improve indirect subsurface and translucent-material transport.
- [ ] Add artist-authored light portals for difficult interiors if needed.
- [ ] Add a baked lightmap or irradiance-volume backend for hardware without
      practical ray-query support, if such hardware is a shipping target.

## General quality improvements

### Directional and glossy GI

- [ ] Make directional L2 GI affordable at High quality by projecting it into a
      compact screen-space receiver representation instead of evaluating the full
      SH payload independently for every fragment.
- [ ] Tune the glossy fallback by roughness and confidence so transitions between
      ray-query reflections, directional DDGI, probes, and the environment remain
      stable.
- [ ] Add energy-conservation tests covering direct, diffuse indirect, glossy
      indirect, emissive, and volumetric ownership.

### Near-field detail and leakage

- [x] Improve C5 disocclusion detection, temporal confidence, and history repair.
- [x] Add checkerboard/interleaved reconstruction and higher ray counts only in
      high-variance tiles.
- [ ] Allocate refinement bricks automatically from receiver density, geometric
      complexity, lighting variance, and observed error.
- [ ] Add two-layer near-visibility depth or a compact visibility cone only for
      scenes where the current B4 sidecar demonstrably fails.
- [ ] Keep surfel GI deferred unless validated failure cases show that B3, B4, and
      C5 cannot provide the required near-field quality.

### Validation and production qualification

- [ ] Build path-traced golden references for interiors, emissives, foliage,
      glass, smoke, water, and rapid lighting transitions.
- [ ] Add equal-work image comparisons for every promoted feature and fallback.
- [ ] Run 30-60 minute traversal tests and longer soak tests for temporal failure
      modes, memory growth, and convergence starvation.
- [ ] Qualify C1, C3, C4, and C5 on representative NVIDIA, AMD, and Intel devices.
- [ ] Test feature combinations rather than qualifying each feature only in
      isolation.
- [ ] Record qualification identifiers and fallback reasons in benchmark
      artifacts.

## Performance roadmap

The latest local Sponza C5-on capture reports a GPU frame time of approximately
20.53 ms P95 against a 12 ms target. Its largest measured costs are Forward+ at
approximately 12.17 ms P95, DDGI receiver-cache generation at approximately
2.85 ms P95, and motion vectors at approximately 1.69 ms P95. The individual
stable DDGI update passes are comparatively small, so probe tracing is not the
first optimization target.

### P0: Reduce C5 setup and execution cost

- [ ] Avoid requiring a dedicated full-resolution motion-vector pass solely for
      C5; emit motion in an existing pass or generate it only for candidate tiles.
- [ ] Produce C5 normal, direct-diffuse, and emissive inputs at trace resolution or
      only for active tiles rather than through unconditional full-resolution
      outputs.
- [x] Skip C5 where residual energy, estimated visibility error, or perceptual
      contribution is below threshold.
- [x] Trace alternating/checkerboard tiles and reconstruct temporally.
- [x] Use additional rays only for high-variance or low-confidence tiles.
- [ ] Pack C5 history formats and alias non-overlapping resources through the
      render graph.
- [x] Add per-stage timing and memory counters that separate C5 input generation,
      tracing, filtering, and composition.

### P0: Optimize the DDGI receiver cache

The receiver cache should be retained: local comparisons show that exact
per-fragment DDGI is substantially more expensive. The goal is to reduce cache
construction and reconstruction cost.

- [x] Add temporal reprojection with conservative disocclusion rejection.
- [x] Rebuild only dirty or high-gradient tiles.
- [x] Select tile rate from depth, normal, material, motion, and GI gradients.
- [x] Dynamically choose full, half, or quarter-rate evaluation per tile.
- [ ] Investigate fusing sampling and reconstruction or sharing workgroup data.
- [ ] Reuse receiver-cache payloads as C5 inputs where ownership remains clear.

### P1: Finish certified adaptive ray cardinality

The current adaptive baseline retains the authored full ray cardinality because
the convergence residual measures transport change rather than directional
quadrature error.

- [ ] Add a directional variance or quadrature-error witness.
- [ ] Allow stable probes to demote to shorter nested ray prefixes only when the
      witness certifies the lower count.
- [ ] Escalate ray count rapidly after lighting, visibility, or geometry changes.
- [ ] Record quality error and saved ray work by ring and content class.

### P1: Memory and scheduling

- [ ] Focus memory reduction on C5 histories, receiver-cache formats, directional
      sidecars, and render-graph aliasing; the core source cache is already packed.
- [ ] Re-evaluate async compute per device only when timestamps demonstrate useful
      overlap without extending the critical path.
- [ ] Keep shader execution reordering and a full ray-tracing pipeline as research
      items rather than near-term priorities; current ray-query update work is not
      the dominant frame cost.
- [ ] Preserve the existing cost-aware scheduler, source refresh modes, sparse
      propagation, pipeline caches, and specialized trace variants instead of
      rebuilding systems that are already present.

## Explicit non-priorities

- A generic neural denoiser should not precede targeted temporal/spatial filters
  for reflections, C5, and volumetrics.
- Surfel GI should remain deferred until concrete scenes demonstrate an
  unaddressed deficiency in the current probe, visibility, refinement, and C5
  combination.
- A full RT pipeline or SER path should not displace higher-return work while the
  inline ray-query update passes remain a small part of frame time.
- Existing completed roadmap work should be maintained and qualified rather than
  re-entered as new feature development.
