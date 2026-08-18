Current near-field decision: C5 adaptive Hi-Z SSGI is the sole complement to
Simple DDGI. It starts at quarter resolution, can fall back to eighth under its
0.75 ms P95 budget, and reserves half resolution for evidence-bound profiles
with sustained headroom. Surfels are deferred and are not part of the current
runtime, memory plan, or 60 FPS target.

- Ray traced transparency.
- Ray traced caustics.
- Accelerate same-frame convergence of cached multi-bounce Transport V2 for urgent and local changes while preserving the certified fixed point.
- Emissive mesh importance sampling.
- Hardware ray traced reflections replacement.
- Neural denoising or vendor-specific upscalers
- Emissive mesh
- RT VFX
- HDRI
- Optional ray-query reflection fallback later
If SSR’s off-screen misses become too visible, trace rays only for invalid SSR pixels. Shade those hits using direct lighting, emissive materials, and DDGI. This remains automatic, but is the more expensive second phase.


Yes—if implemented literally, it is a large and risky change. I would split “#4” into three very different scopes:

1. Importance-guided probe scheduling — medium change

Use existing signals such as visibility, distance, dirty state, source-generation staleness, and luminance-change EMA to decide which probes update first. Keep each probe’s existing fixed ray tier.

The renderer already has much of this infrastructure. This is the version relevant to moving-sun responsiveness and fits naturally into the GPU-resident scheduler.

2. Variance-guided ray counts — large change

Dynamically assign different ray counts to individual probes. This affects:

- Queue compaction and indirect dispatch buckets.
- Source-cache completeness and generation tracking.
- Convergence/error estimation.
- Deterministic ray sequences.
- Budget accounting and validation.

The fixed source-cache ray cardinality makes this especially awkward: a low-ray update cannot simply be declared a complete source refresh.

3. Importance-guided ray directions — very large research change

Bias rays toward high-energy directions, geometry, or the sun. This requires correct sampling PDFs and weighting to avoid biased lighting, plus substantial temporal-stability work. I would not prioritize it yet.

For the sun problem, we only need scope 1:

- Reserve source rays for visible probes.
- Promote visible probes stale for the new sun generation.
- Guarantee their first publication within approximately two frames.
- Preserve fixed ray counts and the existing source-cache ABI.
- Continue the remaining field as a bounded background cohort.

That improves response without redesigning ray tracing. The actual smoothness fix remains generation-safe history retention/crossfading; importance-guided scheduling merely gets the new result ready sooner. I would rename the recommendation to **“importance-guided probe scheduling”** and defer true variance-guided ray allocation until measurements show it is necessary.

Content-dependent additions
- Many-light importance sampling: a light tree, alias table, or reservoir-based selector at probe hits would reduce bias from the current bounded top-light selection. High value in scenes with many local lights; negligible for a mostly single-sun scene.
- Directional radiance for rough reflections: store a compact SG or low-order SH representation alongside diffuse irradiance. This would support rough glossy reuse and glossy-to-diffuse transport, which the current diffuse-only field cannot represent. NVIDIA’s production DDGI work explicitly discusses reusing     probe irradiance for recursive glossy reflection. NVIDIA production DDGI
- Animated and transparent geometry participation: the acceleration-structure path currently excludes decals, foliage proxies, blended transparency, and uses bind-pose proxies for some skinned geometry; see AccelerationStructureManager.cs.
