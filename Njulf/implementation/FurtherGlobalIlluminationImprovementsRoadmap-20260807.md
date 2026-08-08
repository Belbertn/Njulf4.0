# Further Global Illumination Quality, Dynamism, and Performance Roadmap

- Status: proposed opportunity list
- Date: 2026-08-07
- Target: Simple-DDGI with ray-query Transport V2
- Primary goals: faster response to scene changes, first-class emissive/VFX lighting,
  higher near-field quality, and lower CPU/GPU cost without lowering the qualified
  image-quality floor
- Scope rule: this document adds to the current plans; it does not replace or
  duplicate them

## 1. Required boundary

Complete the correctness and production work already described by:

- [`SimpleDdgiHallwayHotspotRefreshAndStutterFixPlan-20260806.md`](SimpleDdgiHallwayHotspotRefreshAndStutterFixPlan-20260806.md),
  which owns tail-audit liveness, source-cohort progress, coherent sparse-page
  publication, and the current hallway artifact; and
- [`ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md`](ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md),
  which already owns punctual many-light sampling, the L2 radiance sidecar for
  rough reflections, current-pose skinned geometry, alpha/transparent semantics,
  decals, and foliage ray proxies.

Those are prerequisites. In particular, no new optimization should be layered on
top of a source cohort or tail audit that can still stop making progress.

This roadmap uses the following labels:

- **QN** — quality-neutral optimization. It must preserve the same qualified
  estimator, source identity, ray directions, material semantics, and final
  convergence target.
- **Dynamic** — reduces latency or artifacts after a light, material, geometry,
  environment, or VFX change.
- **Quality** — adds information the current diffuse field cannot represent. It
  may cost more and must have an independent switch and budget.
- **Research** — potentially valuable, but not suitable for production scheduling
  until a bounded prototype beats the existing path on locked evidence.

For **QN** work, “faster” must never mean fewer qualified rays, lower probe or
atlas resolution, shorter bounce distance, a looser tail certificate, silent
clamping, or a lower-quality fallback. Reordering, caching, specialization,
incremental work, memory-layout changes, and better hardware use are allowed.

## 2. Current code baseline

The renderer already contains most of the foundations normally proposed for a
DDGI upgrade:

| Area | Current implementation | Consequence for new work |
| --- | --- | --- |
| Probe scheduling | GPU-resident scheduling, ray-count buckets, indirect dispatch, classification, dirty boosts, and per-ring quotas in [`SimpleDdgiGpuScheduler.cs`](../Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs), [`SimpleDdgiSchedulePass.cs`](../Njulf.Rendering/Pipeline/SimpleDdgiSchedulePass.cs), and [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs) | Extend the existing scheduler rather than adding another CPU queue. |
| Transport | V2 separates source tracing from cached multi-bounce solves and audits a complete field | New accelerators must converge to the same operator and pass the same certificate. |
| Source cache | The 24/28/36-byte records in [`GPUStructs.cs`](../Njulf.Rendering/Data/GPUStructs.cs) and [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl) retain source radiance, hit distance, deterministic direction, normal, diffuse/transmission response, occlusion, hit class, and generations | There is enough geometric information to avoid repeating many primary traversals after lighting-only changes, but radiance and geometry lifetime are currently coupled by `SourceLightingGeneration`. |
| Residency | Sparse near-ring pages, receiver demand, sampled-atlas mirrors, toroidal scrolling, and coarse fallback are present | Refinement must be an additive, coherently published layer rather than another replacement residency system. |
| Emissive meshes | [`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs) builds a bounded world-space emissive-triangle table and [`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl) samples its global alias distribution | Correct direct emissive sampling exists, but a global area/luminance proposal can have high variance and table rebuilds remain CPU-heavy. |
| Dynamic change discovery | `CollectDdgiDirtyRegions` walks render objects and particle effects and compares tracked signatures every frame | Unchanged frames still pay scene-scale CPU discovery work. |
| VFX | Particles receive DDGI in [`particle.vert`](../Njulf.Shaders/particle.vert); fog receives it in [`fog.comp`](../Njulf.Shaders/fog.comp); sustained emissive particle effects generate dirty regions | VFX can receive GI, but particle emitters are not yet first-class radiance sources in the emissive table. |
| Ray scene | Static/dynamic BLAS caching, TLAS skip/update selection, compaction, residency limits, alpha candidates, and far-field fallback exist in [`AccelerationStructureManager.cs`](../Njulf.Rendering/Resources/AccelerationStructureManager.cs) | Focus on avoiding unnecessary traversal and improving exact alpha traversal rather than rebuilding the AS system. |
| Frame order | The main DDGI update chain is declared after `ForwardPlusPass` in [`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs) | This protects graphics reads and enables overlap, but normal changes become visible no earlier than a subsequent frame. |
| Pipeline creation | Individual passes create Vulkan pipeline-cache objects, including deferred GI trace variants | No repository path currently persists `vkGetPipelineCacheData`; a cold first use can still compile on the frame that enables a variant. |

## 3. Priority summary

| ID | Improvement | Class | Priority | Main win |
| --- | --- | --- | --- | --- |
| A1 | Factor cached geometry from cached lighting | QN, Dynamic | Highest | Re-light cached rays without repeating primary traversal. |
| A2 | Retrace only ray segments touched by changed bounds | QN, Dynamic | Highest | Moving a local object no longer refreshes every ray of every affected probe. |
| A3 | Replace full-scene change scans with a GI mutation journal | QN | Highest | Unchanged-frame CPU cost becomes proportional to actual edits. |
| A4 | Build a spatial, incrementally refittable emissive hierarchy | QN, Quality, Dynamic | Highest | Lower emissive variance and remove CPU table rebuild spikes. |
| A5 | Inject sustained VFX as energy-matched macro emitters | Quality, Dynamic | Highest | Fire, beams, explosions, and magical effects light the world without particle BLAS objects. |
| A6 | Add residual-driven sparse transport propagation | QN, Dynamic | High | Local changes stop requiring indiscriminate full-field solve work. |
| A7 | Add a bounded pre-forward urgent update lane | Dynamic | High | Visible light/environment edits can affect the current frame when safe. |
| A8 | Persist and prewarm GI pipeline state | QN | High | Remove first-use and preset-switch shader/pipeline stutter. |
| A9 | Add exact content-specialized trace fast paths | QN | High | Skip branches and end binary shadow traversal at the first accepted blocker. |
| A10 | Schedule by expected error reduction per unit cost | QN, Dynamic | High | Smooth traversal spikes while retaining fairness and final quality. |
| A11 | Split source-cache hot headers from conditional hit payloads | QN | Medium | Reduce bandwidth for misses, backfaces, and other early exits. |
| A12 | Re-light environment misses without tracing | QN, Dynamic | High | HDRI color, intensity, and rotation changes update much faster. |
| A13 | Support animated/procedural emissive textures | Quality, Dynamic | High | Screens, signs, lava, and animated emissive masks emit their current radiance. |
| B1 | Feed actual receiver contribution back to scheduling | QN, Dynamic | Medium | Work follows probes that materially affect visible pixels. |
| B2 | Add a validated persistent warm-start cache | QN, Dynamic | Medium | Scene loads begin from coherent lighting instead of black/coarse-only warmup. |
| B3 | Add on-demand surface refinement bricks | Quality, Dynamic | Medium | Thin interiors and emissive detail gain local resolution without densifying the world. |
| B4 | Strengthen near-ring visibility representation | Quality | Medium | Reduce moment-based light leaking at thin walls and doorways. |
| B5 | Make fog/smoke consume directional incident radiance | Quality | Medium | Volumes respond to light direction and phase instead of isotropic tint alone. |
| B6 | Add photometric emissive authoring and energy diagnostics | Quality | Medium | Emissive strength becomes predictable across assets and exposure settings. |
| C1 | Research exact opacity-micromap acceleration | QN, Research | Research | Reduce alpha-candidate shader work for fences and foliage without changing cutoff semantics. |
| C2 | Research a ray-tracing-pipeline/SER backend | QN, Research | Research | Measure cross-vendor invocation reordering against the inline ray-query baseline. |
| C3 | Add MIS-correct directional ray guiding | Quality, Research | Research | Spend non-uniform rays on high-energy directions without bias. |
| C4 | Add a dedicated tagged-caustic cache | Quality, Research | Research | Support specular-to-diffuse and refractive caustics without contaminating diffuse DDGI. |
| C5 | Prototype a bounded near-field screen-space residual | Quality, Research | Optional experiment | Test whether visible sub-probe detail justifies an SSGI layer after refinement bricks. |

## 4. Highest-priority dynamic and quality-neutral work

### A1. Factor cached geometry from cached lighting

The current source record already stores most of a first-hit shading point, but
`sourceRadiance` and `sourceLightingGeneration` make a light edit invalidate the
same transaction that owns the primary intersection. Split that ownership into:

1. a **geometric path cache**, keyed by probe physical generation, ray-direction
   epoch, geometry epoch, and material-geometry epoch; and
2. a **source-lighting cache**, keyed by the geometric record plus directional,
   punctual, emissive, and environment revisions.

The geometric record should keep the existing direction, distance, hit class,
normal, diffuse/transmission response, and material occlusion. Add an optional
compact hit-identity sidecar—instance, primitive, and quantized barycentrics or
UV—only where current material/emission lookup cannot be reconstructed exactly.

Then select the cheapest valid refresh:

- environment color/intensity/rotation change: re-evaluate cached misses, with no
  ray query;
- directional-light color/intensity change with unchanged direction and geometry:
  rescale or re-evaluate the cached transfer, preserving visibility;
- directional/local-light direction or transform change: reuse the cached hit
  point and material response and cast only the required visibility rays;
- emissive hierarchy change: re-run emissive next-event sampling at cached hit
  points, with exact current PDFs and visibility;
- geometry, opacity topology, relocation, or direction-epoch change: perform the
  existing full source trace.

Keep both caches private until the full target source generation is complete.
The canonical field must never combine old geometry with partially updated
lighting metadata.

**Acceptance gate:** for cache-valid probes, a pure environment update emits zero
primary TLAS rays; a sun intensity/color update emits zero primary TLAS rays and
no new visibility rays when the visibility transfer is still exact; all modes
converge to the same locked reference as a full source retrace.

### A2. Retrace only ray segments touched by changed bounds

Regional invalidation currently identifies affected probes, but a bounded object
change can still force every source direction in those probes to refresh. Use the
cached ray segment to refine the decision on GPU:

1. upload or GPU-produce old/new swept AABBs with a typed change reason;
2. conservatively intersect each cached segment from the probe to its first hit
   (or maximum distance for a miss) with those bounds;
3. copy untouched source records into a private next generation;
4. retrace only intersecting rays; and
5. publish only when every entry has a valid next-generation provenance stamp.

Removal and opacity/material changes require either stable hit identity or the
union of old and new bounds. Overflow, missing provenance, direction changes, or
uncertain bounds fall back to the current full-probe refresh. The optimization
must be conservative: false positives cost work; false negatives are forbidden.

**Acceptance gate:** opening a door, moving one character, or moving one emissive
prop produces the same source records as a full retrace while the primary-ray
count scales with intersected cached segments rather than all rays in all dirty
probes.

### A3. Replace full-scene change scans with a GI mutation journal

Replace the per-frame object and particle scans in `CollectDdgiDirtyRegions` with
a bounded journal written by the systems that already know an edit occurred:

- scene add/remove/transform and static-instance updates;
- light property and transform updates;
- material transport, alpha, emission, and texture revisions;
- skinning and animation bounds;
- foliage wind/proxy changes;
- environment/HDRI changes; and
- sustained VFX start, stop, transform, envelope, and source-shape changes.

Each event needs a stable producer ID, old/new or swept bounds, reason flags,
content revision, and a monotonic serial. Coalesce events by world brick on GPU
or in a bounded CPU hash table. Keep the current full scan as a validation oracle
and emergency fallback. Journal overflow must cause one diagnosed conservative
global invalidation, never lost work.

**Acceptance gate:** an unchanged 100,000-object scene performs zero render-object
and VFX signature comparisons for GI; replaying the same edits through journal and
reference-scan modes yields identical dirty probes and source generations.

### A4. Build a spatial, incrementally refittable emissive hierarchy

The current global alias table is unbiased for its declared proposal but does not
include point-dependent distance, orientation, solid angle, or receiver-normal
terms. It can therefore spend samples on emitters that are globally bright but
negligible at the current hit. Extend the many-light hierarchy planned for
punctual lights into a separately versioned emissive-cluster hierarchy:

- cook stable per-mesh emissive triangle clusters with area, radiance bounds,
  normal cones, texture-coverage metadata, and stable keys;
- instantiate those clusters without enumerating every world-space triangle on
  CPU;
- build/refit a two-level GPU hierarchy for changed instances or emission;
- choose a cluster using conservative point-dependent importance, then sample a
  triangle/texel within it with an exact combined PDF;
- retain a uniform/global-alias mixture floor so every eligible emitter has
  support; and
- retain the current alias table and brute-force modes as statistical oracles.

Do not silently merge punctual, triangle, and VFX proposals. Either sample them
through a common root with an exact technique probability or combine independent
proposals using MIS.

**Acceptance gate:** emitter ordering and clustering do not change the converged
mean, every source has non-zero support, a static scene performs no hierarchy
build/upload work, and equal-ray variance is lower than the global alias path in
multi-emitter rooms and outdoor neon scenes.

### A5. Inject sustained VFX as energy-matched macro emitters

The existing VFX path detects sustained emissive effects and marks dirty regions,
but those effects need an actual radiance representation. Have GPU particle
simulation reduce each eligible emitter into a small stable record containing:

- integrated emitted power/color over the current envelope;
- weighted centroid and covariance or principal axis;
- conservative current and swept bounds;
- an analytic source type: sphere, capsule, cone, line/beam, disk, or bounded
  volume; and
- a stable source ID and revision.

Sample these records through the emissive hierarchy or a separate exact proposal.
Use one record per emitter, beam segment group, or spatial cluster—not one BLAS
instance or light per particle. Apply hysteresis to source admission and energy,
not to the final lighting result. Short sparks and muzzle flashes should remain
direct/screen-space by default unless an authored override says that their brief
indirect flash is important.

Large receiving emitters should take a small number of representative DDGI
samples across their extent and interpolate them; the present one-center sample
remains the cheap path for compact effects.

**Acceptance gate:** a sustained fire, beam, and explosion light nearby diffuse
geometry with stable energy; changing particle tessellation or particle count at
fixed authored power does not change the GI mean; TLAS instance count is
unchanged; transient effects do not create a global source cohort.

### A6. Add residual-driven sparse transport propagation

Transport V2 already caches the operator inputs, but a local source edit can still
cause broad solve work. Add a prioritized residual queue over the same fixed-point
operator:

1. seed the queue with probes whose direct/source term changed;
2. measure the change in their published/private irradiance;
3. enqueue dependent probes only when their conservative propagated residual can
   affect the final error budget;
4. process the largest error-reduction-per-cost entries first while preserving a
   starvation deadline; and
5. finish with the existing generation-frozen complete-field audit.

Dependencies can be derived from cached hit gathers into a compact reverse list,
or a pull formulation can test changed source bricks. The threshold controls work
ordering, not correctness: it must not loosen the global tail tolerance. If the
dependency structure is incomplete, overflows, or becomes stale, fall back to the
current complete sweep.

**Acceptance gate:** a local emissive or door change reaches the same certified
fixed point as a full sweep, but early solve work is localized and the number of
untouched probe/ray evaluations falls substantially. The current 2.5% tail target
is unchanged.

### A7. Add a bounded pre-forward urgent update lane

The normal update chain deliberately follows `ForwardPlusPass`. Add a small lane
before forward shading only for work that can reuse already-valid geometry:

- pure environment re-light of cached misses;
- light color/intensity updates with reusable visibility;
- a bounded set of visible near-ring cached-hit re-light operations; and
- optionally the first direct-delta step from a newly admitted sustained VFX
  source.

This lane must write a private buffer and atomically publish a complete urgent
cohort before forward. It must not wait for current-frame skinning or a dynamic
BLAS build; geometry changes remain on the normal next-generation path. Unused
capacity returns to the post-forward scheduler, and exceeding the lane budget
keeps the previous coherent field rather than stalling graphics.

**Acceptance gate:** qualified light/environment edits affect visible near-field
indirect light in the same frame or the immediately following frame, with no
mixed generation, graphics wait spike, or reduction in the ordinary update
budget.

### A8. Persist and prewarm GI pipeline state

Create one renderer-owned pipeline-cache service instead of isolated empty cache
objects that disappear at shutdown:

- load and validate cache data by vendor ID, device ID, driver identity,
  `pipelineCacheUUID`, shader-bundle hash, and engine ABI;
- pass the cache to every GI compute and optional ray pipeline;
- pre-create all variants admitted by the selected device/profile during loading;
- serialize cache data atomically after successful creation or clean shutdown;
- treat invalid/incompatible data as an empty cache, never as a fatal error; and
- capture pipeline compile count/time and any first-use creation during play.

`VK_KHR_pipeline_binary` can be evaluated later for deterministic per-pipeline
keys, but an ordinary persistent `VkPipelineCache` is the first cross-vendor step.

**Acceptance gate:** after one warm run, enabling DDGI, changing quality tier, or
entering emissive/alpha/transport variants performs no pipeline creation on a
render-critical frame.

### A9. Add exact content-specialized trace fast paths

Keep the general shader, then add prewarmed variants selected from immutable frame
facts:

- no alpha-candidate or thin-transmission geometry;
- no local lights;
- no emissive sources;
- one directional sun only;
- complete TLAS versus split far-field traversal; and
- detailed diagnostics absent.

For binary shadow visibility, initialize the query with
`gl_RayFlagsTerminateOnFirstHitEXT`: once an opaque or candidate-tested surface is
confirmed as a blocker, any accepted hit is sufficient. Do not use that fast path
for analytic layered transmittance, where traversal must continue and accumulate
transmission. Preserve the current material candidate function in every path that
can see alpha or thin surfaces.

Also measure 32- versus 64-invocation workgroups per device class and keep the
same stable probe/ray IDs. Selection must be based on capability and locked
benchmark evidence, not vendor-name folklore.

**Acceptance gate:** every specialized variant matches the general shader on the
same source/ray records and passes alpha/thin-surface conformance; unsupported or
mixed content selects the general path.

### A10. Schedule by expected error reduction per unit cost

The current scheduler budgets requests and primary rays, while actual cost varies
with alpha candidates, shadow rays, material texture fetches, far-field steps,
and local-light/emissive sampling. Feed delayed GPU measurements back per probe,
ring, and content class:

- estimated source error or luminance change;
- visible receiver contribution;
- recent TLAS candidate count and visibility-ray count;
- alpha/transmission and material texture cost;
- far-field step count;
- cached-only versus primary-trace eligibility; and
- age/deadline/fairness debt.

Rank by expected error reduction divided by predicted cost, with hard minimum
quotas and maximum latency for every admitted cohort. A quality floor always
receives the currently qualified work; spare measured headroom may accelerate
additional probes or solver sweeps, but a frame-pressure controller may not lower
that floor silently.

**Acceptance gate:** the same workload and final certificate complete without
starvation, P95/P99 GI time improves in mixed opaque/alpha/far-field scenes, and
steady-state quality is unchanged.

### A11. Split source-cache hot headers from conditional hit payloads

The packed cache is already much smaller than the legacy record, but solver rays
that are misses or rejected backfaces do not need normals, albedo, transmission,
or material occlusion. Test a versioned structure-of-arrays or two-level layout:

- a dense hot header containing radiance, distance/classification, generation,
  and direction epoch; and
- a compact hit payload indexed only by records that require bounce shading.

Keep page-local allocation so a probe's records remain coherent, and charge the
payload index/allocator metadata in the layout compiler. A scene with mostly
surface hits may choose the existing fixed record; this is a measured admission
choice, not a universal replacement.

**Acceptance gate:** decoded values and tail certification match the current
packed oracle; outdoor/miss-heavy scenes reduce source-cache bytes read and solve
time; indoor hit-heavy scenes do not regress beyond benchmark noise.

### A12. Re-light environment misses without tracing

This is the smallest useful slice of A1 and can land first. A cached miss already
has a deterministic direction and hit classification. When only the HDRI,
procedural sky, exposure-independent environment intensity, or environment
rotation changes:

- retain geometry/direction epochs;
- evaluate the new environment radiance for cached miss directions;
- leave surface-hit visibility and material data untouched;
- start the residual transport solve from only the changed miss records; and
- preserve the prior complete field until the new source cohort is publishable.

If a bright solar disk exists in an HDRI, optionally extract it into the existing
exact directional-light path and remove that energy from the diffuse environment
proposal, avoiding rare uniform-direction hits while retaining total energy.

**Acceptance gate:** an HDRI rotation or sky-time step produces zero primary
geometry traversals for valid cached probes and matches a complete retrace of the
same fixed ray directions.

### A13. Support animated and procedural emissive textures

The cooked emissive profile is appropriate for static textures, but video panels,
animated signs, lava, shader-driven masks, and damaged emissive surfaces need a
runtime representation:

- build a GPU luminance/coverage mip hierarchy for changed emissive textures;
- associate cooked triangle clusters with texture-space bounds;
- update only clusters overlapping changed texture tiles;
- sample cluster, triangle, and texel with an exact joint PDF;
- retain stable source IDs across frames and separate texture-content revisions
  from geometry/transform revisions; and
- define an explicit update rate for video/procedural sources while keeping the
  last complete hierarchy authoritative between updates.

Scalar emissive-strength or color edits should refit weights without rebuilding
geometry. Alpha/emissive texture coupling must use the same coverage convention
as material transport.

**Acceptance gate:** animated screens and lava produce current, spatially correct
indirect light; unchanged textures dispatch no reductions; emitter selection is
unbiased against a brute-force texture/triangle oracle.

## 5. Additional quality and usability work

### B1. Feed actual receiver contribution back to scheduling

Extend the existing page-demand/feedback path so a sampled subset of forward,
transparent, particle, and fog receivers reports the exact contributing virtual
probe/page IDs, interpolation weights, fallback ownership, and screen-tile
coverage. Consume this one frame later as a priority signal, not as an exclusive
visibility gate. Off-screen safety, newly exposed geometry, reflection captures,
and age refresh retain minimum quotas.

This is more useful than camera-frustum membership alone: a probe can be inside
the frustum but contribute almost nothing, while a doorway or reflective capture
can make a small probe set visually dominant.

### B2. Add a validated persistent warm-start cache

Serialize completed, certified static/background page generations and optional
geometric path records by world cell. Key them by exact scene, mesh, transform,
material-transport, environment, layout, direction-codebook, and shader ABI
hashes. Stream them as a prior while the live system validates and refreshes.

Warm data must never become authoritative when any key mismatches. Partial or
corrupt files fall back to ordinary coarse/environment lighting without a stall.
Dynamic objects and VFX remain live overlays. This improves load/teleport quality
without turning DDGI into a baked-only system.

### B3. Add on-demand surface refinement bricks

Layer a small regular fine-probe brick pool over the existing rings. Demand bricks
near:

- highly weighted visible receivers;
- thin opposing surfaces and doorway/portal regions;
- bright compact emissives;
- frequently moving geometry; and
- authored hero areas.

Use the base ring as the complete fallback until every probe in a fine brick is
current and coherently published. Refinement must be world keyed and hysteretic,
not camera-jitter keyed. Eviction returns ownership atomically to the base field.

### B4. Strengthen near-ring visibility representation

The existing directional visibility moments are compact but can underestimate a
thin nearby blocker. Evaluate an optional near-ring sidecar containing one of:

- conservative minimum/low-quantile hit distance per octahedral bin;
- a small two-layer depth representation; or
- a near-occluder cone/depth lobe plus the existing moments.

Use it only when it identifies a measured moment-leak case; do not globally darken
the field. The ordinary moment path remains the fallback and memory admission is
independent. Gates need thin-wall, doorway, foliage-card, and moving-occluder
oracles, including false-occlusion measurements.

### B5. Make fog and smoke consume directional incident radiance

`fog.comp` currently applies a coarse irradiance-derived ambient term. Once the
planned L2 incident-radiance sidecar is production-ready, sample it per froxel or
froxel cluster and integrate it against the configured scattering phase function.
This lets a shaft, sunset, or colored doorway light a volume directionally instead
of as an isotropic tint.

Keep direct volumetric lighting and DDGI indirect ownership separate. Dense smoke
can later publish a coarse transmittance field for ray visibility, but individual
smoke particles must never enter the TLAS.

### B6. Add photometric emissive authoring and energy diagnostics

Define one exposure-independent emissive convention, preferably scene-linear
radiance derived from authored luminance/radiance units. Add:

- explicit unit metadata and conversion for emissive factors/textures;
- average, peak, covered-area, integrated-power, and selected-probability views;
- warnings for texture/mesh scale changes that unexpectedly alter power;
- reference swatches and a calibrated emissive-room scene; and
- separate artistic multipliers whose effect on physical energy is visible.

This does not force every game asset to be physically measured; it makes the
non-physical choice deliberate and consistent.

## 6. Optional hardware and research paths

### C1. Research exact opacity-micromap acceleration

Evaluate `VK_EXT_opacity_micromap` as a capability-gated acceleration path for
static alpha-mask geometry after the existing alpha and foliage semantics are
frozen. This is a quality-neutral research track: it may classify traversal work
earlier, but it may not change which samples are opaque or transparent.

The experiment should:

1. query the extension, formats, subdivision limits, build sizes, and update
   capabilities in [`VulkanContext.cs`](../Njulf.Rendering/Core/VulkanContext.cs),
   with the current candidate-confirmation path as the mandatory fallback;
2. build micromaps from the same UVs, texture content, sampler policy, mip policy,
   and material cutoff used by DDGI ray shading;
3. mark only provably uniform microtriangles opaque or transparent and retain an
   unknown state at every cutoff boundary so shader confirmation remains
   authoritative;
4. key cooked/runtime data by mesh topology, UVs, alpha texture revision, cutoff,
   residency, and subdivision policy, rebuilding or falling back whenever those
   inputs change;
5. attach and compact the micromap with its owning BLAS without breaking current
   BLAS sharing, streaming, or residency budgets; and
6. expose build time, bytes, compacted bytes, classified/unknown microtriangles,
   candidate invocations, traversal time, cache hits, rebuilds, and fallbacks.

Animated alpha, procedural masks, deforming UVs, unsupported formats, missing
mips, or insufficient subdivision stay on the existing shader candidate path.
Do not freeze animation or sample a coarser alpha representation to make a mesh
eligible.

Promotion requires micromap-on/off visibility equality across the alpha
conformance suite, no change to thin-transmission behavior, bounded build and
residency cost, and a total-GI-time win after amortizing construction. A reduction
in candidate invocations alone is not sufficient.

### C2. Research a ray-tracing-pipeline and SER backend

Research Shader Execution Reordering through the ratified, multi-vendor
`VK_EXT_ray_tracing_invocation_reorder` path. Do not add a new dependency on the
older vendor-specific `VK_NV_ray_tracing_invocation_reorder` interface. Because
the EXT interface depends on `VK_KHR_ray_tracing_pipeline`, this is a separate
ray-tracing-pipeline backend experiment, not a switch applied directly to the
shipping inline ray-query compute shader.

Build a controlled three-way comparison:

1. the current inline ray-query backend;
2. an equivalent ray-tracing-pipeline backend with reordering disabled; and
3. the same pipeline using EXT hit objects and invocation reordering by measured
   hit, material, alpha/transmission, or miss classes.

All three paths must consume the same GPU update queue, probe/ray IDs, stable
directions, material and alpha policy, source-cache ABI, generation ownership,
and diagnostics. Query both extension support and the physical-device reordering
property: an implementation that exposes hit objects but reports no effective
reordering belongs in case 2, not case 3. Keep the current backend as the runtime
and compile-time fallback.

The experiment may use a small Slang/SPIR-V shader island until the primary GLSL
toolchain supports the EXT shader operations. Record traversal, shading, SBT,
stack, reordering, synchronization, pipeline creation, and total GI time rather
than reporting traversal alone. Capture per-class occupancy/coherence data where
the device exposes it.

Promotion requires same-ray output parity within the existing deterministic
image and energy tolerances, identical alpha/thin-transmission/far-field
semantics, lower P95 and total GI time on each promoted device class, and no
first-use hitch after pipeline prewarming. No vendor or GPU generation should be
assumed faster without this A/B.

### C3. Add MIS-correct directional ray guiding

Use accumulated per-probe or per-brick incident-energy histograms to guide only a
fraction of source directions. Retain a fixed uniform-sphere proposal floor and
combine both with exact PDFs/MIS so all directions keep support. Stable direction
identity, maintenance subsets, cache cardinality, and audit semantics must be
redesigned explicitly; they cannot be inherited accidentally from the Fibonacci
codebook.

This is valuable for small bright apertures and emissive sources, but only after
spatial emissive sampling and cached re-lighting have removed the cheaper sources
of variance and latency.

### C4. Add a dedicated tagged-caustic cache

Thick refraction and specular-to-diffuse paths should not be approximated as
ordinary diffuse DDGI. For authored hero materials, trace a very small tagged
photon/path set into a separate world-space caustic cache or screen-space
reservoir. Composite it with explicit ownership and an energy budget. The feature
remains off for ordinary materials and cannot feed the positive diffuse transport
operator unless a separate stability proof exists.

### C5. Prototype a bounded near-field screen-space residual

Only after the canonical DDGI field and B3 refinement bricks meet their quality
gates, measure whether contact-scale bounce around visible hands, props, compact
emissives, and newly revealed creases still has material error. If it does,
prototype a deliberately bounded SSGI residual rather than a second complete GI
solution:

- trace only short screen/depth rays;
- sample a direct-diffuse/emissive trace source that excludes DDGI instead of
  feeding final DDGI-lit scene color back into transport;
- estimate only the high-frequency residual and gate the low-frequency component
  already owned by DDGI;
- reject disocclusions using depth, normal, motion, and material revisions;
- use environment/DDGI as the mandatory miss and invalid-history fallback;
- expose confidence and source ownership in a debug view; and
- allocate no trace target, history, descriptors, or passes while disabled.

Compare this experiment directly against DDGI plus B3 refinement bricks, not
only against unrefined DDGI. Promotion requires a meaningful reference-error win
per millisecond, stable camera pans/cuts and screen edges, and no double counting,
false darkening, or dependence on visible emitters. It remains optional and must
never participate in the DDGI-only correctness gate.

## 7. Recommended implementation order

1. Finish the current liveness, participant, source-cohort, and sparse-publication
   plan and freeze new reference captures.
2. Land the existing content-dependent ABI/revision groundwork so new light,
   emissive, material, animation, and VFX revisions have one taxonomy.
3. Implement A3 and A8 first: the mutation journal and persistent pipeline
   prewarm are isolated, measurable, and do not alter lighting math.
4. Implement the small A12 environment-miss relight slice, then generalize it
   into A1 geometry/lighting factorization.
5. Add A2 segment-selective copy-on-write source refresh.
6. Implement A4 emissive hierarchy and A13 dynamic emissive textures, preserving
   the existing alias/brute-force oracles.
7. Add A5 VFX macro emitters.
8. Add A9 exact trace variants and first-hit termination, then A10 cost-aware
   scheduling and A11 cache hot/cold layout based on measured bottlenecks.
9. Add A6 residual propagation and only then A7 urgent pre-forward publication.
10. Promote B1/B2, then evaluate B3-B6 independently by scene need and budget.
11. Keep C1-C4 as isolated experiments until each beats the shipping baseline.
12. Evaluate C5 only after B3 refinement bricks, and promote it only if it wins
    their direct quality-per-millisecond comparison on remaining sub-probe error.

Do not combine source-cache factorization, partial ray retracing, a new emissive
PDF, VFX injection, and residual transport in one patch. Their failure signatures
all appear as incorrect source radiance and would be difficult to attribute.

## 8. Validation matrix

Every promoted feature needs locked before/after runs for:

| Scenario | Required evidence |
| --- | --- |
| Sun color/intensity step | Cached-hit and full-retrace equality; primary and visibility ray counts; visible response latency. |
| Slowly moving sun | Shadow-only re-light validity, source-cohort progress, no history reset or stale visibility. |
| HDRI rotation and procedural-sky step | Zero-primary-ray miss re-light path versus full retrace. |
| Moving emissive mesh | Old/new swept bounds, segment-selective retrace coverage, emissive PDF/energy, no global reset. |
| Animated emissive sign/video | Texture-reduction and hierarchy revision, brute-force texel/triangle oracle, stable temporal energy. |
| Fire, explosion, beam, and sparks | Macro-source energy, dirty event count, response latency, particle-count invariance, zero particle TLAS instances. |
| Door open/close and moving character | Journal/reference dirty-set equality, partial/full retrace equality, thin-wall leakage. |
| Static alpha fence and foliage | Candidate semantics, micromap-on/off visibility equality, unknown-state coverage, build/residency cost, candidate count, traversal time. |
| Animated/deforming alpha foliage | Mandatory micromap fallback, candidate semantics, wind stability, no stale micromap attachment. |
| Divergent ray/material workload | Inline ray query versus RT pipeline versus EXT reordering; same-ray parity, occupancy/coherence, SBT/stack/synchronization and total GI time. |
| Sub-probe contact and compact-emissive detail | DDGI plus B3 bricks versus optional C5 SSGI; path-traced error, camera pan/cut and screen-edge stability, ownership, false darkening, and total GPU time. |
| Camera traversal, recenter, and teleport | Coarse/fine ownership, publication latency, no black/stale flash, no warm-cache misuse. |
| Large static scene with no edits | Zero GI scene scans, zero hierarchy rebuild/upload, zero source refresh outside watchdog policy. |
| Mixed expensive content | P50/P95/P99 per pass and per work unit, fairness/deadline proof, unchanged final certificate. |

Quality-neutral A/Bs must preserve:

- exact stable sample/ray/source IDs where the algorithm is unchanged;
- exact material, alpha, thin-transmission, emissive, and environment semantics;
- the existing or tighter image/energy tolerances and the same tail target;
- no new non-finite, firefly, leakage, fallback, or generation mismatch counts;
- deterministic capture/replay; and
- the current fallback when a new cache, hierarchy, journal, or hardware feature
  is unavailable.

Record CPU/GPU P50, P95, P99, maximum, bytes read/written, primary rays,
visibility rays, candidate intersections, material fetches, far-field steps,
source records copied/retraced/re-lit, hierarchy nodes touched, VFX macro sources,
residual-queue population, and time-to-visible/time-to-certified completion.

## 9. Primary code touch map

### Change discovery, revisions, and source construction

- [`VulkanRenderer.cs`](../Njulf.Rendering/VulkanRenderer.cs)
- [`LightManager.cs`](../Njulf.Rendering/Resources/LightManager.cs)
- material/texture managers and transport compiler under `Njulf.Rendering/Resources`
- particle/VFX scene types and [`GpuParticleRuntimeManager.cs`](../Njulf.Rendering/Resources/GpuParticleRuntimeManager.cs)
- new mutation-journal and emissive-hierarchy managers

### Scheduling, storage, and transport

- [`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [`SimpleDdgiGpuScheduler.cs`](../Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs)
- [`SimpleDdgiStorageLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiStorageLayoutCompiler.cs)
- [`SimpleDdgiLayoutCompiler.cs`](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
- [`SimpleDdgiPasses.cs`](../Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs)
- [`ProductionRenderPipelineDeclaration.cs`](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs)

### Shaders

- [`ddgi_simple_trace.comp`](../Njulf.Shaders/ddgi_simple_trace.comp)
- [`ddgi_hit_shading.glsl`](../Njulf.Shaders/ddgi_hit_shading.glsl)
- [`ddgi_simple_shared.glsl`](../Njulf.Shaders/ddgi_simple_shared.glsl)
- [`ddgi_simple_transport.comp`](../Njulf.Shaders/ddgi_simple_transport.comp)
- [`ddgi_simple_transport_operator.glsl`](../Njulf.Shaders/ddgi_simple_transport_operator.glsl)
- scheduler classify/commit/feedback shaders
- [`particle_simulate.comp`](../Njulf.Shaders/particle_simulate.comp),
  [`particle.vert`](../Njulf.Shaders/particle.vert), and
  [`fog.comp`](../Njulf.Shaders/fog.comp)

### Acceleration structures, diagnostics, and tests

- [`AccelerationStructureManager.cs`](../Njulf.Rendering/Resources/AccelerationStructureManager.cs)
- [`RendererDiagnostics.cs`](../Njulf.Rendering/Data/RendererDiagnostics.cs)
- [`DdgiRuntimeSnapshot.cs`](../Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs)
- [`PerformanceSnapshotWriter.cs`](../Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs)
- the existing DDGI ABI, storage, scheduler, emissive, alpha-conformance,
  Vulkan-validation, capture, and production-gate tests

## 10. Explicit non-recommendations

- Do not lower ray counts, ring density, source distance, visibility resolution,
  or convergence tolerance and label it a quality-neutral optimization.
- Do not add one light, BLAS instance, or TLAS instance per particle.
- Do not globally reset the field for a bounded emissive, VFX, light, material,
  or geometry change.
- Do not publish partially re-lit source records or partially completed fine
  pages to make response appear faster.
- Do not use a temporal blur to hide stale generation ownership.
- Do not use optional SSGI hit confidence as occlusion or feed final DDGI-lit
  scene color back into another indirect bounce.
- Do not approximate alpha cutoffs in an opacity micromap when an exact unknown
  state plus shader confirmation is available.
- Do not assume a ray-tracing pipeline, SER, or vendor-specific feature is faster;
  require a same-workload A/B on each promoted device class.

## 11. Technical references

- NVIDIA Research, [Scaling Probe-Based Real-Time Dynamic Global Illumination for Production](https://research.nvidia.com/publication/2020-09_scaling-probe-based-real-time-dynamic-global-illumination-production-technical): production DDGI state, transition, pruning, and multiresolution foundations.
- NVIDIA Research, [Dynamic Many-Light Sampling for Real-Time Ray Tracing](https://research.nvidia.com/labs/rtr/publication/moreau2019manylight/): dynamic two-level light hierarchies and incremental refitting.
- Khronos, [Vulkan ray traversal](https://docs.vulkan.org/spec/latest/chapters/raytraversal.html): confirmed-hit and `TerminateOnFirstHit` semantics.
- Khronos, [Vulkan ray-tracing guide](https://docs.vulkan.org/guide/latest/extensions/ray_tracing.html): ray-query/ray-pipeline tradeoffs and ray-tracing performance guidance.
- Khronos, [Vulkan pipeline cache guide](https://docs.vulkan.org/guide/latest/pipeline_cache.html): saving and reusing pipeline cache data across runs.
- Khronos, [`VK_EXT_opacity_micromap`](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_opacity_micromap.html): traversal-time subtriangle opacity representation.
- Khronos, [`VK_EXT_ray_tracing_invocation_reorder`](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_ray_tracing_invocation_reorder.html): ratified multi-vendor hit-object and invocation-reordering interface.
- Khronos, [Shader Execution Reordering sample](https://docs.vulkan.org/samples/latest/samples/extensions/ray_tracing_invocation_reorder/README.html): capability-gated invocation reordering and measured ray-work coherence.
