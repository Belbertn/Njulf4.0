# DDGI Remaining Production Optimization Plan - 2026-06-28

This plan covers only the optimization and production-readiness items from
`DDGIOnlyPlan.md` that are not complete yet, or are only partially mitigated.
It assumes the current baseline already has:

- `DdgiHigh` as the default quality preset.
- DDGI-only mode disabling SSGI runtime resources through `EffectiveUseSsgi`.
- Per-cascade rays and explicit max ray distances.
- Primary-ray update budgeting and cold-start caps.
- Camera clipmaps reserved before authored volumes.
- TLAS skip/update support for unchanged or transform-only instance sets.
- Basic DDGI ambient on foliage and particles.

The remaining work is ordered by risk and dependency. Steps 1-4 are the
highest-impact production performance work.

## Step 1 - Rebaseline With Strict Production Evidence

### Goal

Make performance claims trustworthy before doing deeper architectural work.

### Current Gap

Benchmark and production-gate code exists, but there is no checked-in evidence
that the required long, valid GPU-timestamp benchmark suite has passed on the
target Legion 5 hardware.

### Work

1. Add a named `ddgi-production-remaining` benchmark preset if the existing
   runner cannot express this exact profile.
2. Require all DDGI production reports to include:
   - Warmup frames: at least 300.
   - Measured frames: 600-1000.
   - Valid GPU timestamp sample count equal to the measured frame count, unless
     the report is explicitly marked invalid.
   - Separate pass timing for TLAS, DDGI update, DDGI recursive snapshot,
     forward shading, AO, shadows, foliage, particles, and post.
   - DDGI scheduled primary rays by volume and cascade.
   - DDGI effective shaded-light count.
   - Recursive snapshot copy count and bytes.
   - SSGI resource and pass absence in DDGI-only mode.
3. Add a report validation test that fails reports with missing GPU timings.
4. Run required scenarios:
   - Stationary Sponza/plaza right wall.
   - Cornell room.
   - Thin-wall leak test.
   - Bright exterior viewed from dark room.
   - Moving point light.
   - Moving rigid object.
   - Local volume streaming.
   - Fast traversal and teleport.
   - Forest/foliage.
   - Many lights.
5. Store the report path and summary in `implementation/Notes.txt` or a dated
   benchmark markdown file.

### Gate

No later optimization is considered successful unless the report has valid GPU
p50, p95, and max timings for at least 600 measured frames.

## Step 2 - Split DDGI Trace, Blend, Relocate/Classify, and Publish

### Goal

Remove the monolithic `DdgiUpdatePass` bottleneck and make update bandwidth
scale with scheduled probe work.

### Current Gap

The renderer still uses `DdgiUpdatePass`, `DdgiRecursiveSnapshotPass`, recursive
probe-state buffers, recursive irradiance atlas buffers, and recursive visibility
atlas buffers. Recursive copying has been narrowed to scheduled ranges, but the
plan's split pipeline is not implemented.

### Work

1. Create explicit passes:
   - `DdgiTracePass`
   - `DdgiBlendPass`
   - `DdgiRelocateClassifyPass`
   - `DdgiPublishPass`
   - optional later `DdgiVariabilityPass`
2. Split `ddgi_update.comp` into smaller shaders:
   - `ddgi_trace.comp`
   - `ddgi_blend.comp`
   - `ddgi_relocate_classify.comp`
   - optional `ddgi_variability.comp`
3. Allocate a compact ray-result scratch buffer sized by scheduled ray budget,
   not active probe count.
4. `DdgiTracePass` writes per-ray:
   - radiance
   - hit distance
   - hit distance squared
   - front/backface evidence
   - relocation evidence
   - confidence flags
5. `DdgiBlendPass` updates only scheduled probe texels/tiles.
6. `DdgiRelocateClassifyPass` consumes compact evidence and updates only
   scheduled probes.
7. `DdgiPublishPass` publishes the completed cache for frame N+1.
8. Keep frame N forward shading on the previous stable published cache.
9. Remove `DdgiRecursiveSnapshotPass` from:
   - `VulkanRenderer`
   - `ProductionRenderPipelineDeclaration`
   - pass ordering tests
   - diagnostics once replacement counters exist
10. Remove recursive atlas/state bindless slots after all shaders no longer
    reference them.
11. Replace recursive full-resource allocation with either:
    - double-buffered published/current DDGI cache, or
    - explicit previous/current aliases with frame-lifetime barriers.
12. Update diagnostics:
    - DDGI trace GPU us
    - DDGI blend GPU us
    - DDGI relocate/classify GPU us
    - DDGI publish GPU us
    - ray scratch bytes
    - updated atlas bytes
    - published-cache latency

### Gate

- No `DdgiRecursiveSnapshotPass` exists in the active production graph.
- No recursive probe-state, irradiance, or visibility atlas buffers are resident.
- Steady stationary frames perform zero recursive-copy bytes.
- Update bandwidth scales with scheduled probes and ray count.
- Full test suite passes and Vulkan validation is clean while toggling DDGI
  quality tiers.

## Step 3 - Bound DDGI Hit Lighting Cost

### Goal

Make DDGI trace cost independent from total scene light count.

### Current Gap

`ddgi_update.comp` still loops over lights up to `MaxShadedLights` and may trace
a visibility ray per contributing light. This is bounded by settings, but it is
not the production light-selection model from the plan.

### Work

1. Add DDGI hit-light selection metadata:
   - primary directional light index
   - local light grid or clustered light list for world-space ray hits
   - optional alias table for local lights
2. In trace shader, evaluate:
   - one deterministic directional light
   - one selected local light
   - optional second local light only for high-priority interior/authored volumes
3. Trace at most one visibility ray per selected light.
4. Use energy-normalized or unbiased importance weighting for local-light
   selection.
5. Add diagnostics:
   - selected directional hits
   - selected local hits
   - DDGI visibility rays
   - skipped local light count
   - light-selection mode
6. Add stress tests for 8, 32, 64, and 128 lights.
7. Keep `DdgiMaxShadedLights` only as a compatibility fallback during migration,
   then remove the loop-over-N production path.

### Gate

DDGI trace p95 grows approximately linearly with scheduled primary rays and does
not grow materially when local light count increases from 8 to 128.

## Step 4 - Add Compact DDGI Material and Emissive Metadata

### Goal

Avoid expensive or unstable material sampling in coarse DDGI rays and make
emissive lighting production-ready.

### Current Gap

The DDGI ray-hit path still samples material textures in the shader. Emissive
surfaces can contribute when hit, but there is no emissive-source table for
small or sparse emitters.

### Work

1. Extend material GPU metadata with DDGI fields:
   - average diffuse albedo
   - average emissive radiance
   - emissive importance
   - alpha policy
   - preferred DDGI texture mip
2. Build those fields during asset/material upload.
3. Use compact values for coarse cascades.
4. Use ray-cone or fixed coarse mips for fine/interior cascades when texture
   sampling is still required.
5. Add an emissive-source table:
   - triangle/proxy bounds
   - radiance
   - importance
   - affected probe radius
   - revision id
6. Sample at most one emissive source per hit by importance.
7. Promote tiny important emitters to analytic lights or emissive proxies.
8. Mark nearby DDGI probes dirty when emissive intensity/material changes.

### Gate

- Emissive-panel validation lights nearby geometry without requiring random
  probe rays to hit tiny triangles.
- Coarse cascades do not sample full-resolution material textures.
- DDGI trace time remains stable in emissive-heavy scenes.

## Step 5 - Finish SSGI-Free Shader Variants

### Goal

Make DDGI-only truly one-output in shader and pipeline state, not just
resource-gated at runtime.

### Current Gap

DDGI-only mode omits SSGI resources and passes, but `forward.frag` and
`foliage_forward.frag` still contain SSGI MRT output paths and the foliage shader
still writes `outSsgiTraceSource`.

### Work

1. Add compile-time shader defines:
   - `NJULF_SSGI_TRACE_OUTPUT=0/1`
   - or dedicated DDGI-only shader entry variants.
2. In DDGI-only variants:
   - remove `layout(location = 1) out vec4 outSsgiTraceSource`
   - remove SSGI trace-source writes
   - use one color attachment only
3. Split or specialize mesh and foliage pipeline creation so the render pass
   attachment count matches the shader outputs.
4. Keep SSGI variants available only when `EffectiveUseSsgi` is true.
5. Update shader build tests to assert DDGI-only variants have no SSGI MRT
   output.

### Gate

A DDGI-only pipeline capture contains no SSGI color output, descriptor, barrier,
image, pass, or shader output location.

## Step 6 - Build Stable Local Volume Streaming and Pooling

### Goal

Allow room/interior/local DDGI volumes without whole-cache churn or clipmap
starvation.

### Current Gap

Camera clipmaps are reserved before authored volumes, but there is no complete
stable local-volume slot pool, free-list, streaming-cell metadata, or
room/portal-aware active-volume selection.

### Work

1. Add authored-volume production metadata:
   - priority
   - blend distance
   - streaming cell id
   - interior flag
   - quality class
   - max ray distance
   - steady hysteresis
   - dirty hysteresis
   - update priority
2. Preallocate a fixed DDGI probe pool for the selected profile.
3. Reserve stable ranges for camera clipmaps.
4. Add a local-volume slot allocator:
   - fixed slot count
   - free-list
   - stable probe range per slot
   - per-slot generation
5. Select active authored volumes by:
   - camera containment
   - room/portal membership when available
   - distance
   - probe density
   - approximate screen coverage
   - artist priority
6. Reassigning a local slot initializes only that slot range.
7. Keep allocation signature separate from current local-volume assignment.
8. Add diagnostics:
   - active local slots
   - local slot generation
   - local slot init bytes
   - local volume eviction reason
   - cache clear reason

### Gate

Streaming one local room volume does not clear clipmap probes or unrelated local
volume slots. Multi-room traversal produces zero whole-cache reinitializations.

## Step 7 - Optimize DDGI Gather Volume Selection

### Goal

Avoid looping over many volumes per pixel in forward shading.

### Current Gap

Forward DDGI gather still resolves candidate volumes in shader. The production
plan calls for a small per-tile/per-cluster list: zero or one local volume,
current clipmap, adjacent clipmap, and environment fallback.

### Work

1. Build a screen-tile or clustered DDGI volume list each frame.
2. For each tile/cluster, store:
   - local authored volume index or none
   - primary clipmap index
   - secondary clipmap index for transition
   - blend weights
3. Upload a compact `DdgiGatherTileBuffer`.
4. Change forward shader to query only the preselected indices.
5. Keep a debug fallback path for exhaustive volume search.
6. Add debug views:
   - selected local volume
   - selected clipmap
   - clipmap transition weight
   - volume list miss/fallback

### Gate

Forward DDGI gather has a fixed small upper bound independent of active volume
capacity.

## Step 8 - Complete Small-Room Quality Path

### Goal

Make small rooms and thin walls high quality without forcing global clipmaps to
be dense.

### Current Gap

Validation scenes exist, but there is no complete authored-room volume
validator, no production metadata enforcement, and no special thin-wall quality
path.

### Work

1. Add an authoring/runtime validator for local DDGI volumes:
   - probes initially inside geometry
   - insufficient free-space coverage
   - probes too close to thin walls
   - excessive overlap
   - invalid blend margin
   - probe quota overflow
2. Add room-volume presets:
   - 0.4-0.75 m spacing
   - 8-15 m max ray distance
   - 32 steady rays
   - 48-64 dirty rays
   - low initial hysteresis
3. Add thin-wall policies:
   - conservative two-sided/thickened RT proxy
   - visibility-confidence leak clamp
   - spacing-scaled normal/view bias
4. Ensure geometric normal is used for DDGI cache query and surface bias.
5. Add validation screenshots or metrics for:
   - Cornell room
   - bright exterior through doorway/window
   - thin wall between warm/cool rooms

### Gate

Thin-wall leakage is within the accepted luminance threshold and room/clipmap
transitions have no visible seam above the accepted threshold.

## Step 9 - Replace Aggregate Dirty Signatures With Source-Level Invalidation

### Goal

Dirty only the probes affected by changed scene content.

### Current Gap

There are dirty bounds and some validation scenarios, but the full source-level
dirty-reason system from the plan is not complete.

### Work

1. Track previous/current bounds for:
   - rigid objects
   - skinned proxy objects
   - local lights
   - emissive sources
   - streamed chunks
2. Track material revisions and reverse map materials to using objects.
3. Add explicit DDGI dirty reasons:
   - GeometryAdded
   - GeometryRemoved
   - TransformChanged
   - MaterialChanged
   - EmissiveChanged
   - LocalLightChanged
   - DirectionalLightChanged
   - StreamIn
   - StreamOut
   - Teleport
   - AgeRefresh
4. Apply reason-specific history policy:
   - reset for new/teleport/removed geometry
   - very low hysteresis for added or moved geometry
   - medium hysteresis for emissive/local light changes
   - progressive cascade update for sun changes
   - normal hysteresis for age refresh
5. Store reason flags per scheduled probe and expose them in debug overlays.

### Gate

Moving, spawning, deleting, or changing one object affects old/new regions only
and does not dirty the complete scene.

## Step 10 - Finish Foliage, VFX, Fog, and Smoke Integration

### Goal

Keep DDGI influence on non-opaque rendering while avoiding per-fragment or
per-particle heavy DDGI gathers.

### Current Gap

Foliage and particles sample DDGI ambient now, but foliage samples in-fragment,
still has SSGI trace output, and there is no emitter/batch/froxel DDGI path for
VFX, fog, or smoke.

### Work

1. Foliage:
   - sample DDGI per cluster, meshlet, or vertex
   - interpolate to fragments
   - use stable clump/geometric normal for DDGI lookup
   - keep bent/wrap normals for appearance
   - include trees and large leaf cards in RT via proxies
   - exclude fine grass blades from RT
2. Particles and VFX:
   - sample DDGI once per emitter, beam segment, or particle batch
   - represent sustained flames/explosions/glows as analytic proxy lights
   - mark DDGI dirty only for sustained effects
   - keep transient sparks/muzzle flashes direct-only by default
3. Fog and smoke:
   - sample coarse DDGI/froxel ambient lighting
   - do not add individual fog/smoke particles to TLAS
4. Add diagnostics:
   - foliage DDGI sample count
   - particle/VFX DDGI sample count
   - VFX dirty-probe events

### Gate

Particle count has no material effect on TLAS or DDGI traversal cost, while
sustained fire/glow effects still light nearby geometry.

## Step 11 - Replace Scheduler Scan/Sort With Persistent Queues

### Goal

Keep CPU DDGI scheduling below production budget at maximum probe count.

### Current Gap

The scheduler has priority categories and ray budgeting, but still rents arrays,
scans active probes, and sorts candidates for frustum/safety work.

### Work

1. Maintain persistent queues per cascade and reason:
   - Uninitialized
   - DirtyGeometry
   - DirtyLighting
   - VisibleNear
   - VisibleFar
   - Safety
   - AgeRefresh
2. Replace full candidate sort with:
   - bounded top-K heap, or
   - fixed scoring buckets
3. Score by:
   - dirty reason
   - volume priority
   - cascade
   - distance
   - current/expanded/predicted frustum membership
   - age
   - confidence
   - variability after Step 12
   - estimated ray cost
4. Reserve a small budget fraction for far-cascade and out-of-frustum refresh.
5. Remove frame-local `ArrayPool<DdgiProbeUpdateCandidate>.Rent(activeProbeCount)`
   paths from production scheduling.
6. Add CPU scheduler timing histograms and p95 gate.

### Gate

CPU DDGI scheduling p95 is below 0.25 ms at the maximum supported probe pool.

## Step 12 - Add Variability and Adaptive Quality

### Goal

Spend DDGI update work where lighting changes or quality is low.

### Current Gap

There is no complete per-probe variability/luminance-change system driving the
scheduler.

### Work

1. Add per-probe fields:
   - luminance mean
   - luminance variance/change
   - update age
   - irradiance confidence
   - visibility confidence
   - last dirty reason
2. Add `DdgiVariabilityPass` or integrate variability computation into
   `DdgiBlendPass`.
3. Feed variability into scheduler queues.
4. Update fewer stable probes.
5. Boost new, dirty, high-variance, and low-confidence probes.
6. Let far cascades converge over longer periods.
7. Keep minimum refresh to avoid permanent starvation.
8. Adapt update count/ray budget, not clipmap layout, during gameplay.

### Gate

Dynamic lighting converges quickly near the camera, stable areas consume less
update work, and far cascades never retain stale lighting indefinitely.

## Step 13 - Revisit Async Compute Only After Stable Frame-N/Frame-N+1 Cache

### Goal

Use async compute only if profiling proves overlap and no queue contention.

### Current Gap

`DdgiAsyncComputeEnabled` is default-off, which is correct. The plan's later
async path depends on stable previous/current DDGI cache publication.

### Work

1. Wait until Steps 2 and 12 are complete.
2. Use a real compute-capable queue.
3. Add timeline semaphore synchronization.
4. Add explicit ownership transitions where needed.
5. Publish DDGI updates for frame N+1.
6. Measure overlap rather than only pass time.
7. Add fallback to graphics queue if validation, queue support, or performance
   benefit fails.

### Gate

Async DDGI reduces total frame p95 or leaves it unchanged with useful overlap.
If not, keep it default-off.

## Final Production Gate

The remaining work is complete only when all of these are true:

1. DDGI-only active graph has no SSGI resources, passes, barriers, descriptors,
   or shader outputs.
2. DDGI update is split and has no recursive snapshot pass or recursive atlas
   copies.
3. DDGI trace cost is bounded by scheduled primary rays and selected light count,
   not total scene light count.
4. Local-volume streaming does not clear unrelated probe ranges.
5. Forward DDGI gather samples a fixed small set of preselected volumes.
6. Foliage, particles, VFX, fog, and smoke receive plausible DDGI without heavy
   per-fragment/per-particle gathers.
7. Scheduler p95 is below 0.25 ms at maximum supported probe count.
8. DDGI update p95 is at or below 2.5 ms on the target laptop in sustained
   multi-scenario runs.
9. Full automated tests pass.
10. Vulkan validation is clean while toggling DDGI debug views, quality tiers,
    local-volume streaming, and scene changes.
